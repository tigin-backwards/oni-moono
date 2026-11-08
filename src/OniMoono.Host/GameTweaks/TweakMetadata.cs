#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace OniMoono.Host.GameTweaks
{
    /// One assembly's metadata, read from its file without loading it (System.Reflection.Metadata):
    /// the game's assemblies as they are on disk (before any load-time rewrite), and the tweaks'.
    /// Members are named as GameMemberAttribute spells them (Names).
    internal sealed class MetadataFile : IDisposable
    {
        public readonly string Name;
        public readonly MetadataReader Reader;
        readonly PEReader? pe;
        readonly Dictionary<string, TypeDefinitionHandle> topLevel = new(StringComparer.Ordinal);
        bool indexed;
        int lookups;
        Names? names;

        MetadataFile(string name, PEReader pe)
        {
            Name = name;
            this.pe = pe;
            Reader = pe.GetMetadataReader();
        }

        MetadataFile(string name, MetadataReader reader)
        {
            Name = name;
            Reader = reader;
        }

        /// A loaded assembly's metadata (no method bodies: for its declarations).
        public static unsafe MetadataFile? Loaded(Assembly assembly) =>
            assembly.TryGetRawMetadata(out byte* blob, out int length) ? new MetadataFile(assembly.GetName().Name ?? "", new MetadataReader(blob, length)) : null;

        /// The assembly at `path`, or null when there is none (or it is not one).
        public static MetadataFile? Open(string path)
        {
            if (!File.Exists(path)) return null;
            var stream = File.OpenRead(path);
            try
            {
                var pe = new PEReader(stream);
                if (!pe.HasMetadata) { pe.Dispose(); return null; }
                return new MetadataFile(Path.GetFileNameWithoutExtension(path), pe);
            }
            catch (BadImageFormatException)
            {
                stream.Dispose();
                return null;
            }
        }

        public Names Names => names ??= new Names(Reader);

        /// A type by its full name (`Outer/Inner` for a nested type), or nil.
        public TypeDefinitionHandle Type(string fullName)
        {
            // A few lookups (the start-up's synchronous check) scan the table instead of indexing it.
            if (!indexed && ++lookups <= 8) return Scan(fullName);
            if (!indexed)
            {
                indexed = true;
                foreach (var handle in Reader.TypeDefinitions)
                {
                    var type = Reader.GetTypeDefinition(handle);
                    if ((type.Attributes & TypeAttributes.VisibilityMask) >= TypeAttributes.NestedPublic) continue;
                    string name = Reader.GetString(type.Name);
                    topLevel[type.Namespace.IsNil ? name : Reader.GetString(type.Namespace) + "." + name] = handle;
                }
            }
            int slash = fullName.IndexOf('/');
            if (!topLevel.TryGetValue(slash < 0 ? fullName : fullName.Substring(0, slash), out var found)) return default;
            while (slash >= 0)
            {
                int next = fullName.IndexOf('/', slash + 1);
                string inner = next < 0 ? fullName.Substring(slash + 1) : fullName.Substring(slash + 1, next - slash - 1);
                var nested = default(TypeDefinitionHandle);
                foreach (var n in Reader.GetTypeDefinition(found).GetNestedTypes())
                    if (Reader.StringComparer.Equals(Reader.GetTypeDefinition(n).Name, inner)) { nested = n; break; }
                if (nested.IsNil) return default;
                found = nested;
                slash = next;
            }
            return found;
        }

        /// Type(fullName) without the index: each top-level type's name compared in place.
        TypeDefinitionHandle Scan(string fullName)
        {
            int slash = fullName.IndexOf('/');
            string top = slash < 0 ? fullName : fullName.Substring(0, slash);
            int dot = top.LastIndexOf('.');
            string ns = dot < 0 ? "" : top.Substring(0, dot), name = dot < 0 ? top : top.Substring(dot + 1);
            TypeDefinitionHandle found = default;
            foreach (var handle in Reader.TypeDefinitions)
            {
                var type = Reader.GetTypeDefinition(handle);
                if ((type.Attributes & TypeAttributes.VisibilityMask) >= TypeAttributes.NestedPublic) continue;
                if (!Reader.StringComparer.Equals(type.Name, name) || !Reader.StringComparer.Equals(type.Namespace, ns)) continue;
                found = handle;
                break;
            }
            if (found.IsNil || slash < 0) return found;
            foreach (var part in fullName.Substring(slash + 1).Split('/'))
            {
                var nested = default(TypeDefinitionHandle);
                foreach (var n in Reader.GetTypeDefinition(found).GetNestedTypes())
                    if (Reader.StringComparer.Equals(Reader.GetTypeDefinition(n).Name, part)) { nested = n; break; }
                if (nested.IsNil) return default;
                found = nested;
            }
            return found;
        }

        public MethodBodyBlock? Body(MethodDefinitionHandle handle)
        {
            int rva = Reader.GetMethodDefinition(handle).RelativeVirtualAddress;
            return rva == 0 || pe == null ? null : pe.GetMethodBody(rva);
        }

        public void Dispose() => pe?.Dispose();
    }

    /// Type and member names as GameMemberAttribute spells them, the same whichever assembly
    /// (and metadata token) a reference or a definition comes from: `System.Int32`, `Outer/Inner`,
    /// `List`1<System.String>`, `!0` and `!!0` for type and method parameters, `T&`, `T[]`,
    /// `T modreq(M)` (optional modifiers left out).
    internal sealed class Names : ISignatureTypeProvider<string, object?>, ICustomAttributeTypeProvider<string>
    {
        readonly MetadataReader reader;
        readonly Dictionary<EntityHandle, string> cache = new();

        public Names(MetadataReader reader) => this.reader = reader;

        public string Definition(TypeDefinitionHandle handle)
        {
            if (cache.TryGetValue(handle, out var name)) return name;
            var type = reader.GetTypeDefinition(handle);
            var declaring = type.GetDeclaringType();
            string own = reader.GetString(type.Name);
            name = !declaring.IsNil ? Definition(declaring) + "/" + own
                : type.Namespace.IsNil ? own : reader.GetString(type.Namespace) + "." + own;
            cache[handle] = name;
            return name;
        }

        public string Reference(TypeReferenceHandle handle)
        {
            if (cache.TryGetValue(handle, out var name)) return name;
            var type = reader.GetTypeReference(handle);
            string own = reader.GetString(type.Name);
            name = type.ResolutionScope.Kind == HandleKind.TypeReference ? Reference((TypeReferenceHandle)type.ResolutionScope) + "/" + own
                : type.Namespace.IsNil ? own : reader.GetString(type.Namespace) + "." + own;
            cache[handle] = name;
            return name;
        }

        /// A type token's name (definition, reference or specification).
        public string Type(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)handle),
            HandleKind.TypeReference => Reference((TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
            _ => "?",
        };

        public static string Method(string declaring, string name, MethodSignature<string> signature) =>
            $"{signature.ReturnType} {declaring}::{name}{(signature.GenericParameterCount > 0 ? "``" + signature.GenericParameterCount : "")}({string.Join(",", signature.ParameterTypes)})";

        public string Method(MethodDefinitionHandle handle)
        {
            var method = reader.GetMethodDefinition(handle);
            return Method(Definition(method.GetDeclaringType()), reader.GetString(method.Name), method.DecodeSignature(this, null));
        }

        public string Field(FieldDefinitionHandle handle)
        {
            var field = reader.GetFieldDefinition(handle);
            return $"{field.DecodeSignature(this, null)} {Definition(field.GetDeclaringType())}::{reader.GetString(field.Name)}";
        }

        /// A member reference's parent: a type, or (a vararg call's) a method's declaring type.
        public string Parent(EntityHandle parent) => parent.Kind == HandleKind.MethodDefinition
            ? Definition(reader.GetMethodDefinition((MethodDefinitionHandle)parent).GetDeclaringType())
            : parent.Kind == HandleKind.ModuleReference ? "<Module>" : Type(parent);

        public string MemberReference(MemberReferenceHandle handle)
        {
            var member = reader.GetMemberReference(handle);
            string parent = Parent(member.Parent), name = reader.GetString(member.Name);
            return member.GetKind() == MemberReferenceKind.Method
                ? Method(parent, name, member.DecodeMethodSignature(this, null))
                : $"{member.DecodeFieldSignature(this, null)} {parent}::{name}";
        }

        /// Any member or type token's name, for fingerprints: what a call, load or ldtoken names.
        public string Token(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition: return Method((MethodDefinitionHandle)handle);
                case HandleKind.FieldDefinition: return Field((FieldDefinitionHandle)handle);
                case HandleKind.MemberReference: return MemberReference((MemberReferenceHandle)handle);
                case HandleKind.MethodSpecification:
                    var spec = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    return Token(spec.Method) + "<" + string.Join(",", spec.DecodeSignature(this, null)) + ">";
                case HandleKind.StandaloneSignature:
                    var sig = reader.GetStandaloneSignature((StandaloneSignatureHandle)handle);
                    return sig.GetKind() == StandaloneSignatureKind.Method ? "sig " + Method("", "", sig.DecodeMethodSignature(this, null)) : "locals";
                default: return Type(handle);
            }
        }

        public string GetPrimitiveType(PrimitiveTypeCode code) => code switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            PrimitiveTypeCode.Void => "System.Void",
            _ => code.ToString(),
        };

        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind) => Definition(handle);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind) => Reference(handle);
        public string GetTypeFromSpecification(MetadataReader r, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            r.GetTypeSpecification(handle).DecodeSignature(this, context);
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => isRequired ? unmodifiedType + " modreq(" + modifier + ")" : unmodifiedType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "method " + Method("", "", signature);
        public string GetSystemType() => "System.Type";
        public bool IsSystemType(string type) => type == "System.Type";
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    }

    /// A method body's instructions, as offsets, opcodes and raw operands.
    internal static class IlWalk
    {
        static readonly OpCode[] one = new OpCode[256], two = new OpCode[256];

        static IlWalk()
        {
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var op = (OpCode)field.GetValue(null)!;
                ushort value = (ushort)op.Value;
                if (op.Size == 1) one[value & 0xFF] = op;
                else two[value & 0xFF] = op;
            }
        }

        public struct Instruction
        {
            public int Offset, Size;
            public OpCode Op;
            /// The operand: a token, an integer, a branch's target offset (Targets for a switch).
            public long Value;
            public double Real;
            public int[]? Targets;
        }

        public static List<Instruction> Read(BlobReader il)
        {
            var result = new List<Instruction>();
            while (il.RemainingBytes > 0)
            {
                var i = new Instruction { Offset = il.Offset };
                byte b = il.ReadByte();
                i.Op = b == 0xFE ? two[il.ReadByte()] : one[b];
                switch (i.Op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget: { int d = il.ReadSByte(); i.Value = il.Offset + d; break; }
                    case OperandType.InlineBrTarget: { int d = il.ReadInt32(); i.Value = il.Offset + d; break; }
                    case OperandType.ShortInlineI: i.Value = i.Op == OpCodes.Ldc_I4_S ? il.ReadSByte() : il.ReadByte(); break;
                    case OperandType.ShortInlineVar: i.Value = il.ReadByte(); break;
                    case OperandType.InlineVar: i.Value = il.ReadUInt16(); break;
                    case OperandType.InlineI: i.Value = il.ReadInt32(); break;
                    case OperandType.InlineI8: i.Value = il.ReadInt64(); break;
                    case OperandType.ShortInlineR: i.Real = il.ReadSingle(); break;
                    case OperandType.InlineR: i.Real = il.ReadDouble(); break;
                    case OperandType.InlineSwitch:
                    {
                        int n = il.ReadInt32();
                        var deltas = new int[n];
                        for (int k = 0; k < n; k++) deltas[k] = il.ReadInt32();
                        int end = il.Offset;
                        for (int k = 0; k < n; k++) deltas[k] += end;
                        i.Targets = deltas;
                        break;
                    }
                    default: i.Value = il.ReadInt32(); break; // tokens
                }
                i.Size = il.Offset - i.Offset;
                result.Add(i);
            }
            return result;
        }

        public static bool HasToken(OpCode op) => op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType
            or OperandType.InlineTok or OperandType.InlineSig or OperandType.InlineString;
    }

    /// A game method's body fingerprint: a hash of its IL as the game's file has it, with each
    /// token spelled as the member or type it names (tokens renumber between builds), each branch
    /// as how many instructions it jumps, and its locals and exception clauses by type, so only a
    /// change to what the method does changes it.
    internal static class Fingerprints
    {
        public static (string Hash, string Listing) Of(MetadataFile file, MethodDefinitionHandle method)
        {
            var body = file.Body(method);
            var text = new StringBuilder();
            if (body == null) text.Append("no body\n");
            else
            {
                var reader = file.Reader;
                var names = file.Names;
                var il = body.GetILReader();
                int size = il.Length;
                var instructions = IlWalk.Read(il);
                var index = new Dictionary<int, int>(instructions.Count + 1);
                for (int k = 0; k < instructions.Count; k++) index[instructions[k].Offset] = k;
                index[size] = instructions.Count;
                int At(long offset) => index.TryGetValue((int)offset, out var k) ? k : -1;
                if (!body.LocalSignature.IsNil)
                {
                    var locals = reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(names, null);
                    text.Append("locals ").Append(string.Join(",", locals)).Append('\n');
                }
                foreach (var region in body.ExceptionRegions)
                {
                    text.Append(region.Kind).Append(' ').Append(At(region.TryOffset)).Append('+').Append(At(region.TryOffset + region.TryLength) - At(region.TryOffset))
                        .Append(" handler ").Append(At(region.HandlerOffset)).Append('+').Append(At(region.HandlerOffset + region.HandlerLength) - At(region.HandlerOffset));
                    if (region.Kind == ExceptionRegionKind.Catch) text.Append(' ').Append(names.Type(region.CatchType));
                    if (region.Kind == ExceptionRegionKind.Filter) text.Append(" filter ").Append(At(region.FilterOffset));
                    text.Append('\n');
                }
                for (int k = 0; k < instructions.Count; k++)
                {
                    var i = instructions[k];
                    text.Append(k).Append(' ').Append(i.Op.Name);
                    switch (i.Op.OperandType)
                    {
                        case OperandType.InlineNone: break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.InlineBrTarget: text.Append(' ').Append(At(i.Value) - k); break;
                        case OperandType.InlineSwitch: text.Append(' ').Append(string.Join(",", i.Targets!.Select(t => At(t) - k))); break;
                        case OperandType.ShortInlineR:
                        case OperandType.InlineR: text.Append(' ').Append(i.Real.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
                        case OperandType.InlineString: text.Append(" \"").Append(reader.GetUserString(MetadataTokens.UserStringHandle((int)i.Value & 0xFFFFFF))).Append('"'); break;
                        default:
                            if (IlWalk.HasToken(i.Op)) text.Append(' ').Append(names.Token(MetadataTokens.EntityHandle((int)i.Value)));
                            else text.Append(' ').Append(i.Value);
                            break;
                    }
                    text.Append('\n');
                }
            }
            var listing = text.ToString();
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(listing));
            return (Convert.ToHexString(hash, 0, 8).ToLowerInvariant(), listing);
        }
    }

    /// The game's assemblies as their files are, opened as they are first asked for.
    internal sealed class GameFiles : IDisposable
    {
        readonly string managed;
        readonly Dictionary<string, MetadataFile?> files = new(StringComparer.OrdinalIgnoreCase);

        public GameFiles(string managed) => this.managed = managed;

        /// The game's Assembly-CSharp and Assembly-CSharp-firstpass, where declared members are looked up.
        public static readonly string[] Primary = ["Assembly-CSharp", "Assembly-CSharp-firstpass"];

        /// Whether a referenced assembly is one the game ships and Moono loads as it is (not one
        /// Moono provides itself: its engine, UI packages and the runtime's).
        public bool IsGame(string name)
        {
            if (name.StartsWith("UnityEngine", StringComparison.Ordinal) || name.StartsWith("Unity.", StringComparison.Ordinal)
                || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Mono.", StringComparison.Ordinal)
                || name.StartsWith("OniMoono", StringComparison.Ordinal) || name is "mscorlib" or "netstandard")
                return false;
            return Get(name) != null;
        }

        public MetadataFile? Get(string name)
        {
            if (!files.TryGetValue(name, out var file))
                files[name] = file = MetadataFile.Open(Path.Combine(managed, name + ".dll"));
            return file;
        }

        /// A type of Assembly-CSharp or its firstpass by its full name.
        public (MetadataFile File, TypeDefinitionHandle Type)? Find(string fullName)
        {
            foreach (var name in Primary)
                if (Get(name) is { } file && file.Type(fullName) is { IsNil: false } type) return (file, type);
            return null;
        }

        /// A type of the named assembly, following its forwarders.
        public (MetadataFile File, TypeDefinitionHandle Type)? Find(string assembly, string fullName)
        {
            for (int hop = 0; hop < 4; hop++)
            {
                var file = Get(assembly);
                if (file == null) return null;
                var type = file.Type(fullName);
                if (!type.IsNil) return (file, type);
                string? next = null;
                int slash = fullName.IndexOf('/');
                string top = slash < 0 ? fullName : fullName.Substring(0, slash);
                foreach (var handle in file.Reader.ExportedTypes)
                {
                    var exported = file.Reader.GetExportedType(handle);
                    if (exported.Implementation.Kind != HandleKind.AssemblyReference) continue;
                    string name = exported.Namespace.IsNil ? file.Reader.GetString(exported.Name) : file.Reader.GetString(exported.Namespace) + "." + file.Reader.GetString(exported.Name);
                    if (name != top) continue;
                    next = file.Reader.GetString(file.Reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name);
                    break;
                }
                if (next == null) return null;
                assembly = next;
            }
            return null;
        }

        public void Dispose()
        {
            foreach (var file in files.Values) file?.Dispose();
        }
    }

    /// Whether a member found by name in the game matches (Member), and what a tweak assembly's
    /// code refers to in the game (References): each type and member reference to an assembly
    /// the game ships, from the code of a tweak's classes and every class they use in the same
    /// assembly (nested ones too: lambdas, closures, iterators), resolved in the game's files.
    internal static class GameLookup
    {
        public enum Match { Found, Changed, Missing }

        /// A member of a game type by name and signature, in the type or its game base types;
        /// `signature` is a method's (Names.Method with an empty declaring type and name, plus
        /// whether it is an instance method) or a field's type.
        public static (Match Match, MetadataFile? File, EntityHandle Member) Member(GameFiles game, MetadataFile file, TypeDefinitionHandle type, string name, bool method, string signature)
        {
            bool named = false;
            for (int depth = 0; depth < 32 && !type.IsNil; depth++)
            {
                var reader = file.Reader;
                var definition = reader.GetTypeDefinition(type);
                if (method)
                {
                    foreach (var handle in definition.GetMethods())
                    {
                        var m = reader.GetMethodDefinition(handle);
                        if (!reader.StringComparer.Equals(m.Name, name)) continue;
                        named = true;
                        if (MethodSignature(file, handle) == signature) return (Match.Found, file, handle);
                    }
                }
                else
                {
                    foreach (var handle in definition.GetFields())
                    {
                        var f = reader.GetFieldDefinition(handle);
                        if (!reader.StringComparer.Equals(f.Name, name)) continue;
                        named = true;
                        if (f.DecodeSignature(file.Names, null) == signature) return (Match.Found, file, handle);
                    }
                }
                // Then its base type, while that is the game's.
                var baseType = definition.BaseType;
                if (baseType.IsNil) break;
                if (baseType.Kind == HandleKind.TypeDefinition) { type = (TypeDefinitionHandle)baseType; continue; }
                if (baseType.Kind == HandleKind.TypeSpecification) baseType = GenericHead(reader, (TypeSpecificationHandle)baseType);
                if (baseType.Kind != HandleKind.TypeReference || ResolveReference(game, file, (TypeReferenceHandle)baseType) is not { } next) break;
                (file, type) = next;
            }
            return (named ? Match.Changed : Match.Missing, null, default);
        }

        public static string MethodSignature(MetadataFile file, MethodDefinitionHandle handle)
        {
            var m = file.Reader.GetMethodDefinition(handle);
            var signature = m.DecodeSignature(file.Names, null);
            return (signature.Header.IsInstance ? "instance " : "") + Names.Method("", "", signature);
        }

        /// A generic instantiation's type definition or reference (the type itself otherwise).
        static EntityHandle GenericHead(MetadataReader reader, TypeSpecificationHandle spec)
        {
            var blob = reader.GetBlobReader(reader.GetTypeSpecification(spec).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return default;
            blob.ReadSignatureTypeCode();
            return blob.ReadTypeHandle();
        }

        /// The root assembly reference of a type reference (through its declaring types), or nil.
        static AssemblyReferenceHandle Scope(MetadataReader reader, TypeReferenceHandle handle)
        {
            for (int depth = 0; depth < 16; depth++)
            {
                var scope = reader.GetTypeReference(handle).ResolutionScope;
                if (scope.Kind == HandleKind.TypeReference) { handle = (TypeReferenceHandle)scope; continue; }
                return scope.Kind == HandleKind.AssemblyReference ? (AssemblyReferenceHandle)scope : default;
            }
            return default;
        }

        /// A reference to a game type, resolved in the game's files; null when it is not the
        /// game's or is not there.
        public static (MetadataFile File, TypeDefinitionHandle Type)? ResolveReference(GameFiles game, MetadataFile from, TypeReferenceHandle handle)
        {
            var scope = Scope(from.Reader, handle);
            if (scope.IsNil) return null;
            string assembly = from.Reader.GetString(from.Reader.GetAssemblyReference(scope).Name);
            if (!game.IsGame(assembly)) return null;
            return game.Find(assembly, from.Names.Reference(handle));
        }

        static bool IsGameReference(GameFiles game, MetadataFile from, TypeReferenceHandle handle)
        {
            var scope = Scope(from.Reader, handle);
            return !scope.IsNil && game.IsGame(from.Reader.GetString(from.Reader.GetAssemblyReference(scope).Name));
        }

        /// What collects the type references and same-assembly types a signature names.
        internal sealed class Collector : ISignatureTypeProvider<int, object?>
        {
            public readonly HashSet<TypeReferenceHandle> References = new();
            public readonly HashSet<MemberReferenceHandle> Members = new();
            public readonly HashSet<TypeDefinitionHandle> Seen = new();
            readonly MetadataReader reader;

            public Collector(MetadataReader reader) => this.reader = reader;

            public void Definition(TypeDefinitionHandle handle) => Seen.Add(handle);

            public void Type(EntityHandle handle)
            {
                switch (handle.Kind)
                {
                    case HandleKind.TypeDefinition: Definition((TypeDefinitionHandle)handle); break;
                    case HandleKind.TypeReference: References.Add((TypeReferenceHandle)handle); break;
                    case HandleKind.TypeSpecification: reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null); break;
                }
            }

            public int GetPrimitiveType(PrimitiveTypeCode typeCode) => 0;
            public int GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind) { Definition(handle); return 0; }
            public int GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind) { References.Add(handle); return 0; }
            public int GetTypeFromSpecification(MetadataReader r, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => r.GetTypeSpecification(handle).DecodeSignature(this, context);
            public int GetSZArrayType(int elementType) => 0;
            public int GetArrayType(int elementType, ArrayShape shape) => 0;
            public int GetByReferenceType(int elementType) => 0;
            public int GetPointerType(int elementType) => 0;
            public int GetPinnedType(int elementType) => 0;
            public int GetGenericInstantiation(int genericType, ImmutableArray<int> typeArguments) => 0;
            public int GetGenericTypeParameter(object? context, int index) => 0;
            public int GetGenericMethodParameter(object? context, int index) => 0;
            public int GetModifiedType(int modifier, int unmodifiedType, bool isRequired) => 0;
            public int GetFunctionPointerType(MethodSignature<int> signature) => 0;
        }

        /// The top-level and nested types of `file` whose `Name` constant is a tweak's name, by name.
        public static Dictionary<string, List<TypeDefinitionHandle>> TweakClasses(MetadataReader reader)
        {
            var result = new Dictionary<string, List<TypeDefinitionHandle>>(StringComparer.Ordinal);
            foreach (var handle in reader.TypeDefinitions)
                foreach (var f in reader.GetTypeDefinition(handle).GetFields())
                {
                    var field = reader.GetFieldDefinition(f);
                    if ((field.Attributes & FieldAttributes.Literal) == 0 || !reader.StringComparer.Equals(field.Name, "Name")) continue;
                    var constant = field.GetDefaultValue();
                    if (constant.IsNil) continue;
                    var value = reader.GetConstant(constant);
                    if (value.TypeCode != ConstantTypeCode.String) continue;
                    var blob = reader.GetBlobReader(value.Value);
                    string name = blob.ReadUTF16(blob.Length);
                    if (!result.TryGetValue(name, out var list)) result[name] = list = new List<TypeDefinitionHandle>();
                    list.Add(handle);
                }
            return result;
        }

        /// The game references of the code reachable from `roots`, each that does not resolve in
        /// the game's files as a problem ("`Type.Member` is missing in this game version").
        public static List<string> References(GameFiles game, MetadataFile file, IEnumerable<TypeDefinitionHandle> roots) => new ReferenceScan(game, file).Problems(roots);

        /// One tweak assembly's references against the game, each type's scanned once and each
        /// reference resolved once whichever tweaks share them.
        internal sealed class ReferenceScan
        {
            readonly GameFiles game;
            readonly MetadataFile file;
            readonly Dictionary<TypeDefinitionHandle, Collector> direct = new();
            readonly Dictionary<TypeReferenceHandle, string?> typeProblems = new();
            readonly Dictionary<MemberReferenceHandle, string?> memberProblems = new();

            public ReferenceScan(GameFiles game, MetadataFile file)
            {
                this.game = game;
                this.file = file;
            }

            /// A type's own references (not its nested types'), and the types it uses.
            Collector Direct(TypeDefinitionHandle handle)
            {
                if (direct.TryGetValue(handle, out var collect)) return collect;
                var reader = file.Reader;
                direct[handle] = collect = new Collector(reader);
                void Token(EntityHandle token)
                {
                    switch (token.Kind)
                    {
                        case HandleKind.TypeDefinition:
                        case HandleKind.TypeReference:
                        case HandleKind.TypeSpecification: collect.Type(token); break;
                        case HandleKind.MethodDefinition: collect.Definition(reader.GetMethodDefinition((MethodDefinitionHandle)token).GetDeclaringType()); break;
                        case HandleKind.FieldDefinition: collect.Definition(reader.GetFieldDefinition((FieldDefinitionHandle)token).GetDeclaringType()); break;
                        case HandleKind.MemberReference:
                            if (!collect.Members.Add((MemberReferenceHandle)token)) break;
                            var member = reader.GetMemberReference((MemberReferenceHandle)token);
                            if (member.Parent.Kind != HandleKind.MethodDefinition && member.Parent.Kind != HandleKind.ModuleReference) collect.Type(member.Parent);
                            if (member.GetKind() == MemberReferenceKind.Method) member.DecodeMethodSignature(collect, null);
                            else member.DecodeFieldSignature(collect, null);
                            break;
                        case HandleKind.MethodSpecification:
                            var spec = reader.GetMethodSpecification((MethodSpecificationHandle)token);
                            Token(spec.Method);
                            spec.DecodeSignature(collect, null);
                            break;
                        case HandleKind.StandaloneSignature:
                            var sig = reader.GetStandaloneSignature((StandaloneSignatureHandle)token);
                            if (sig.GetKind() == StandaloneSignatureKind.Method) sig.DecodeMethodSignature(collect, null);
                            else sig.DecodeLocalSignature(collect, null);
                            break;
                    }
                }
                var type = reader.GetTypeDefinition(handle);
                foreach (var nested in type.GetNestedTypes()) collect.Definition(nested);
                if (!type.BaseType.IsNil) collect.Type(type.BaseType);
                foreach (var i in type.GetInterfaceImplementations()) collect.Type(reader.GetInterfaceImplementation(i).Interface);
                foreach (var f in type.GetFields()) reader.GetFieldDefinition(f).DecodeSignature(collect, null);
                foreach (var m in type.GetMethods())
                {
                    reader.GetMethodDefinition(m).DecodeSignature(collect, null);
                    var body = file.Body(m);
                    if (body == null) continue;
                    if (!body.LocalSignature.IsNil) Token(body.LocalSignature);
                    foreach (var region in body.ExceptionRegions)
                        if (region.Kind == ExceptionRegionKind.Catch) collect.Type(region.CatchType);
                    var il = body.GetILReader();
                    foreach (var i in IlWalk.Read(il))
                        if (IlWalk.HasToken(i.Op) && i.Op.OperandType != OperandType.InlineString) Token(MetadataTokens.EntityHandle((int)i.Value));
                }
                return collect;
            }

            /// `stop`: types not followed into (their own references are not the roots' problems).
            public List<string> Problems(IEnumerable<TypeDefinitionHandle> roots, IReadOnlySet<TypeDefinitionHandle>? stop = null)
            {
                var seen = new HashSet<TypeDefinitionHandle>(stop ?? (IEnumerable<TypeDefinitionHandle>)[]);
                var pending = new Queue<TypeDefinitionHandle>();
                foreach (var root in roots)
                    if (seen.Add(root)) pending.Enqueue(root);
                var problems = new List<string>();
                var types = new HashSet<TypeReferenceHandle>();
                var members = new HashSet<MemberReferenceHandle>();
                while (pending.Count > 0)
                {
                    var collect = Direct(pending.Dequeue());
                    foreach (var used in collect.Seen)
                        if (seen.Add(used)) pending.Enqueue(used);
                    types.UnionWith(collect.References);
                    members.UnionWith(collect.Members);
                }
                foreach (var reference in types)
                    if (TypeProblem(reference) is { } problem) problems.Add(problem);
                foreach (var member in members)
                    if (MemberProblem(member) is { } problem) problems.Add(problem);
                problems.Sort(StringComparer.Ordinal);
                return problems;
            }

            string? TypeProblem(TypeReferenceHandle reference)
            {
                if (typeProblems.TryGetValue(reference, out var problem)) return problem;
                if (IsGameReference(game, file, reference) && ResolveReference(game, file, reference) == null)
                    problem = $"`{Display(file.Names.Reference(reference))}` is missing in this game version";
                typeProblems[reference] = problem;
                return problem;
            }

            string? MemberProblem(MemberReferenceHandle handle)
            {
                if (memberProblems.TryGetValue(handle, out var problem)) return problem;
                memberProblems[handle] = problem = Resolve(handle);
                return problem;
            }

            string? Resolve(MemberReferenceHandle handle)
            {
                var reader = file.Reader;
                var member = reader.GetMemberReference(handle);
                var parent = member.Parent;
                if (parent.Kind == HandleKind.TypeSpecification) parent = GenericHead(reader, (TypeSpecificationHandle)parent);
                if (parent.Kind != HandleKind.TypeReference || !IsGameReference(game, file, (TypeReferenceHandle)parent)) return null;
                if (ResolveReference(game, file, (TypeReferenceHandle)parent) is not { } owner) return null; // the type's own problem
                bool method = member.GetKind() == MemberReferenceKind.Method;
                string signature;
                if (method)
                {
                    var s = member.DecodeMethodSignature(file.Names, null);
                    signature = (s.Header.IsInstance ? "instance " : "") + Names.Method("", "", s);
                }
                else signature = member.DecodeFieldSignature(file.Names, null);
                string name = reader.GetString(member.Name);
                var found = Member(game, owner.File, owner.Type, name, method, signature);
                return found.Match == Match.Found ? null
                    : $"`{Display(file.Names.Reference((TypeReferenceHandle)parent))}.{name}` {(found.Match == Match.Changed ? "changed" : "is missing")} in this game version";
            }
        }

        /// A type's name as a player reads it: `Outer.Inner`, without its generic arity.
        public static string Display(string type)
        {
            int tick = type.IndexOf('`');
            if (tick >= 0)
            {
                int end = tick + 1;
                while (end < type.Length && char.IsDigit(type[end])) end++;
                type = type.Remove(tick, end - tick);
            }
            return type.Replace('/', '.');
        }
    }
}
