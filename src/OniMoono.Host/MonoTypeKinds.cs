using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Mono.Cecil.Rocks;
using OniMoono.Engine;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Host
{
    /// ARCHITECTURE D57. A mod built for an older game names a type in its signatures as a value
    /// type that is now a class (LogicValueChanged became one in U57, Crop.CropVal in U60), or as
    /// a class that is now a value type. CoreCLR refuses every signature that does: "Could not load
    /// type 'LogicValueChanged' … due to value type mismatch". Mono (Unity's fork, branch
    /// unity-6000.3-mbe) reads the tag and the class apart (metadata/metadata.c,
    /// do_mono_metadata_parse_type: compare_type_literals accepts `valuetype` for a class), and
    /// keeps the tag where the type it parses is not shared: a method's locals (parsed transient,
    /// mono_metadata_parse_mh_full), a field of the mod's own (parsed with its flags as attributes,
    /// so never replaced by the class's shared type) and a generic instantiation's arguments. In
    /// method signatures and member references the class's own type replaces it
    /// (mono_metadata_parse_type_internal). Its JIT then sees such a local or field as a value
    /// type of the class: storing an object in it, or passing its value where an object is
    /// expected, fails the method's compilation as a whole, every call, with "Invalid IL code in
    /// Smart_HEP.SmartHEPStorage:OnLogicValueChanged (object): IL_0006: stloc.0   \n\n" from the
    /// caller's frame (mini/method-to-ir.c, target_type_is_incompatible and check_call_signature;
    /// set_exception_type_from_invalid_il). A member reference through such a generic argument
    /// names another instantiation than the game's (MonoMissingMembers, D42). A class tag for a
    /// value type fails the member reference: BadImageFormatException "Expected reference type but
    /// got type kind 17". Measured in the original player (UnityProbe's kindchange).
    ///
    /// As a mod loads, each method whose compilation fails so on Mono gets the body Mono's
    /// compilation gives it, throwing Mono's exception and hidden from stack traces. The rest run
    /// as the class: every signature's tag is the type's own, and where a method reaches a member
    /// through the address of a location of such a type (C# for a struct's field or method), it
    /// reaches it through the location's value; a constructor called on an address constructs
    /// into the location. On Mono that reads unrelated memory or crashes the player.
    internal static class MonoTypeKinds
    {
        /// The image with Mono's failures and the types' own tags, or null when no signature tags a
        /// type of an assembly the same in both players (MonoMissingTypes) against its kind.
        internal static byte[]? Adjust(byte[] image, ref byte[]? symbols, string path)
        {
            Kinds kinds;
            try
            {
                using var pe = new PEReader(new MemoryStream(image, writable: false));
                if (!pe.HasMetadata) return null;
                kinds = Mismatches(pe.GetMetadataReader());
            }
            catch (BadImageFormatException)
            {
                return null;
            }
            if (kinds.ValueTagged.Count == 0 && kinds.ClassTagged.Count == 0) return null;
            try
            {
                return Rewrite(image, ref symbols, path, kinds);
            }
            catch (Exception e) when (e is Cecil.AssemblyResolutionException or BadImageFormatException or InvalidOperationException or ArgumentException or NullReferenceException)
            {
                EngineLog.Engine($"mod type kinds: {Path.GetFileName(path)} not rewritten ({e.GetType().Name}: {e.Message}); it loads unchanged");
                return null;
            }
        }

        /// The TypeRefs (by token) some signature tags `valuetype` while the type is a class; those
        /// tagged `class` while it is a value type, with Mono's kind number of the type; the member
        /// references whose signatures tag one so; and whether the assembly skips verification.
        internal sealed record Kinds(HashSet<int> ValueTagged, Dictionary<int, int> ClassTagged, HashSet<int> ClassTaggedMembers, bool SkipsVerification);

        internal static Kinds Mismatches(MetadataReader md)
        {
            var tags = new Tags(md);
            foreach (var handle in md.MethodDefinitions) tags.Method(md.GetMethodDefinition(handle).Signature);
            foreach (var handle in md.FieldDefinitions) tags.Field(md.GetFieldDefinition(handle).Signature);
            List<(int Token, int Class)>? classTaggedMembers = null;
            foreach (var handle in md.MemberReferences)
            {
                tags.MemberClasses = null;
                tags.Member(md.GetMemberReference(handle).Signature);
                if (tags.MemberClasses is { } classes)
                    foreach (var c in classes) (classTaggedMembers ??= []).Add((MetadataTokens.GetToken(handle), c));
            }
            for (int row = 1, rows = md.GetTableRowCount(TableIndex.TypeSpec); row <= rows; row++)
                tags.Specification(md.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(row)).Signature);
            for (int row = 1, rows = md.GetTableRowCount(TableIndex.MethodSpec); row <= rows; row++)
                tags.Instantiation(md.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row)).Signature);
            for (int row = 1, rows = md.GetTableRowCount(TableIndex.StandAloneSig); row <= rows; row++)
                tags.Standalone(md.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(row)).Signature);
            var valueTagged = new HashSet<int>();
            foreach (var token in tags.Values)
                if (Kind(md, MetadataTokens.TypeReferenceHandle(token & 0xffffff)) is { ValueType: false, Same: true })
                    valueTagged.Add(token);
            var classTagged = new Dictionary<int, int>();
            var members = new HashSet<int>();
            if (classTaggedMembers != null)
                foreach (var (member, token) in classTaggedMembers)
                {
                    if (!classTagged.ContainsKey(token))
                    {
                        if (Kind(md, MetadataTokens.TypeReferenceHandle(token & 0xffffff)) is not { ValueType: true, Same: true } kind) continue;
                        classTagged[token] = kind.MonoKind;
                    }
                    members.Add(member);
                }
            return new Kinds(valueTagged, classTagged, members, SkipsVerification(md));
        }

        /// A type's kind: whether it is a value type, its MonoTypeEnum, and whether its assembly
        /// is the same in both players.
        internal readonly record struct TypeKind(bool ValueType, int MonoKind, bool Same);

        static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string, string), TypeKind?> kindsByName = new();

        /// The kind of the type a TypeRef names, or null where it cannot be resolved here.
        static TypeKind? Kind(MetadataReader md, TypeReferenceHandle handle)
        {
            var reference = md.GetTypeReference(handle);
            if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                var key = (md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name), md.GetString(reference.Namespace), md.GetString(reference.Name));
                if (kindsByName.TryGetValue(key, out var known)) return known;
                // Not cached until the assembly is loaded: it may load later.
                if (Resolve(md, handle) is not { } resolved) return null;
                return kindsByName[key] = Of(resolved);
            }
            return Resolve(md, handle) is { } type ? Of(type) : null;
        }

        static TypeKind Of(Type type) => new(type.IsValueType, type.IsValueType ? MonoKind(type) : 0, MonoMissingTypes.SameInBothPlayers(type.Assembly));

        /// The type a TypeRef names, from the assembly loaded under its name, or null.
        internal static Type? Resolve(MetadataReader md, TypeReferenceHandle handle)
        {
            var reference = md.GetTypeReference(handle);
            var name = md.GetString(reference.Name);
            switch (reference.ResolutionScope.Kind)
            {
                case HandleKind.AssemblyReference:
                    var ns = md.GetString(reference.Namespace);
                    var assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                    return MonoMissingTypes.Loaded(assembly)?.GetType(ns.Length == 0 ? name : ns + "." + name, throwOnError: false);
                case HandleKind.TypeReference:
                    return Resolve(md, (TypeReferenceHandle)reference.ResolutionScope)?.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
                default:
                    return null;
            }
        }

        /// The MonoTypeEnum of a value type's own type (metadata/blob.h): its primitive's, else
        /// MONO_TYPE_VALUETYPE (an enum's too).
        static int MonoKind(Type type) => Type.GetTypeCode(type) switch
        {
            _ when type.IsEnum => 0x11,
            _ when type == typeof(IntPtr) => 0x18,
            _ when type == typeof(UIntPtr) => 0x19,
            _ when type == typeof(TypedReference) => 0x16,
            TypeCode.Boolean => 0x02,
            TypeCode.Char => 0x03,
            TypeCode.SByte => 0x04,
            TypeCode.Byte => 0x05,
            TypeCode.Int16 => 0x06,
            TypeCode.UInt16 => 0x07,
            TypeCode.Int32 => 0x08,
            TypeCode.UInt32 => 0x09,
            TypeCode.Int64 => 0x0a,
            TypeCode.UInt64 => 0x0b,
            TypeCode.Single => 0x0c,
            TypeCode.Double => 0x0d,
            _ => 0x11,
        };

        /// Whether Mono's JIT skips the checks for this assembly (mini/mini.c,
        /// mini_assembly_can_skip_verification; metadata/assembly.c,
        /// mono_assembly_has_skip_verification): a RequestMinimum permission set on the assembly
        /// with SkipVerification, which C# emits for a build with unsafe code allowed.
        internal static bool SkipsVerification(MetadataReader md)
        {
            if (!md.IsAssembly) return false;
            var name = "SkipVerification"u8;
            foreach (var handle in md.GetAssemblyDefinition().GetDeclarativeSecurityAttributes())
            {
                var attribute = md.GetDeclarativeSecurityAttribute(handle);
                if (attribute.Action != DeclarativeSecurityAction.RequestMinimum) continue;
                var blob = md.GetBlobBytes(attribute.PermissionSet).AsSpan();
                // A named property: its name, then the bool's byte.
                int at = blob.IndexOf(name);
                if (at >= 0 && at + name.Length < blob.Length && blob[at + name.Length] == 1) return true;
            }
            return false;
        }

        /// Collects the TypeRefs (by token) signatures tag `valuetype` and, in a member
        /// reference's signature, `class`: walks the signature blobs (ECMA-335 II.23.2) without
        /// decoding them into types.
        sealed class Tags(MetadataReader md)
        {
            public readonly HashSet<int> Values = [];
            public List<int>? MemberClasses;
            bool member;

            public void Method(BlobHandle blob) => Walk(blob, static (Tags t, ref BlobReader r) => t.MethodSignature(ref r));
            public void Field(BlobHandle blob) => Walk(blob, static (Tags t, ref BlobReader r) => { r.ReadSignatureHeader(); t.Type(ref r); });
            public void Specification(BlobHandle blob) => Walk(blob, static (Tags t, ref BlobReader r) => t.Type(ref r));

            public void Member(BlobHandle blob)
            {
                member = true;
                Walk(blob, static (Tags t, ref BlobReader r) =>
                {
                    if (r.RemainingBytes > 0 && (r.ReadByte() & 0x0f) == (int)SignatureKind.Field) t.Type(ref r);
                    else { r.Offset = 0; t.MethodSignature(ref r); }
                });
                member = false;
            }

            public void Instantiation(BlobHandle blob) => Walk(blob, static (Tags t, ref BlobReader r) =>
            {
                r.ReadSignatureHeader();
                for (int i = r.ReadCompressedInteger(); i > 0; i--) t.Type(ref r);
            });

            public void Standalone(BlobHandle blob) => Walk(blob, static (Tags t, ref BlobReader r) =>
            {
                var header = r.ReadSignatureHeader();
                if (header.Kind != SignatureKind.LocalVariables) { r.Offset = 0; t.MethodSignature(ref r); return; }
                for (int i = r.ReadCompressedInteger(); i > 0; i--) t.Type(ref r);
            });

            delegate void Reader(Tags tags, ref BlobReader reader);

            void Walk(BlobHandle blob, Reader read)
            {
                if (blob.IsNil) return;
                var reader = md.GetBlobReader(blob);
                try { read(this, ref reader); }
                catch (BadImageFormatException) { }
            }

            void MethodSignature(ref BlobReader r)
            {
                var header = r.ReadSignatureHeader();
                if (header.IsGeneric) r.ReadCompressedInteger();
                int count = r.ReadCompressedInteger();
                Type(ref r);
                for (int i = 0; i < count; i++) Type(ref r);
            }

            void Type(ref BlobReader r)
            {
                while (true)
                {
                    switch ((SignatureTypeCode)r.ReadCompressedInteger())
                    {
                        case SignatureTypeCode.Pointer or SignatureTypeCode.ByReference or SignatureTypeCode.SZArray or SignatureTypeCode.Pinned or SignatureTypeCode.Sentinel:
                            continue;
                        case SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier:
                            r.ReadTypeHandle();
                            continue;
                        case (SignatureTypeCode)0x11:
                            Tagged(r.ReadTypeHandle(), value: true);
                            return;
                        case (SignatureTypeCode)0x12:
                            Tagged(r.ReadTypeHandle(), value: false);
                            return;
                        case SignatureTypeCode.GenericTypeParameter or SignatureTypeCode.GenericMethodParameter:
                            r.ReadCompressedInteger();
                            return;
                        case SignatureTypeCode.Array:
                        {
                            Type(ref r);
                            r.ReadCompressedInteger();
                            for (int i = r.ReadCompressedInteger(); i > 0; i--) r.ReadCompressedInteger();
                            for (int i = r.ReadCompressedInteger(); i > 0; i--) r.ReadCompressedSignedInteger();
                            return;
                        }
                        case SignatureTypeCode.GenericTypeInstance:
                        {
                            bool value = r.ReadCompressedInteger() == 0x11;
                            Tagged(r.ReadTypeHandle(), value, generic: true);
                            for (int i = r.ReadCompressedInteger(); i > 0; i--) Type(ref r);
                            return;
                        }
                        case SignatureTypeCode.FunctionPointer:
                            MethodSignature(ref r);
                            return;
                        default:
                            return;
                    }
                }
            }

            void Tagged(EntityHandle handle, bool value, bool generic = false)
            {
                if (handle.Kind != HandleKind.TypeReference) return;
                int token = MetadataTokens.GetToken(handle);
                if (value) Values.Add(token);
                else if (member && !generic) (MemberClasses ??= []).Add(token);
            }
        }

        static byte[]? Rewrite(byte[] image, ref byte[]? symbols, string path, Kinds kinds)
        {
            using var resolver = LoadedFirstResolver.ForMod(path);
            bool withSymbols = symbols != null;
            Cecil.ModuleDefinition module;
            try
            {
                module = Cecil.ModuleDefinition.ReadModule(new MemoryStream(image), new Cecil.ReaderParameters
                {
                    AssemblyResolver = resolver,
                    ReadSymbols = withSymbols,
                    SymbolStream = withSymbols ? new MemoryStream(symbols!) : null,
                    SymbolReaderProvider = withSymbols ? new Cil.PortablePdbReaderProvider() : null,
                });
            }
            catch (Exception e) when (withSymbols && e is Cil.SymbolsNotMatchingException or Cil.SymbolsNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                withSymbols = false;
                module = Cecil.ModuleDefinition.ReadModule(new MemoryStream(image), new Cecil.ReaderParameters { AssemblyResolver = resolver });
            }
            using var owned = module;
            var mismatched = module.GetTypeReferences().Where(t => kinds.ValueTagged.Contains(t.MetadataToken.ToInt32())).ToList();
            var classTagged = module.GetTypeReferences().Where(t => kinds.ClassTagged.ContainsKey(t.MetadataToken.ToInt32())).ToList();
            var names = string.Join(", ", mismatched.Concat(classTagged).Select(t => t.FullName).Distinct().Take(8));
            using var original = new OriginalTokens(path);
            var methods = new Methods(kinds, original.Token);
            var failing = new List<(Cecil.MethodDefinition Method, string Message, Type Exception)>();
            int fixedUp = 0;
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !methods.Touches(method)) continue;
                    ((string Message, Type Exception)? Failure, Dictionary<Cil.Instruction, (Use, Cil.Instruction?)> Uses) outcome;
                    try
                    {
                        outcome = methods.Compile(method);
                    }
                    catch (Exception e) when (e is InvalidCastException or ArgumentOutOfRangeException or NullReferenceException or InvalidOperationException)
                    {
                        // IL the run cannot follow: the method is left as it is.
                        continue;
                    }
                    if (outcome.Failure is var (message, exception)) failing.Add((method, message, exception));
                    else fixedUp += Fix(method, outcome.Uses);
                }
            // Every signature tags the types as they are.
            foreach (var reference in mismatched) reference.IsValueType = false;
            foreach (var reference in classTagged) reference.IsValueType = true;
            var constructors = new Dictionary<Type, Cecil.MethodReference>();
            var hidden = module.ImportReference(typeof(System.Diagnostics.StackTraceHiddenAttribute).GetConstructor(Type.EmptyTypes)!);
            foreach (var (method, message, exception) in failing)
            {
                if (!constructors.TryGetValue(exception, out var constructor))
                    constructors[exception] = constructor = module.ImportReference(exception.GetConstructor([typeof(string)])!);
                MonoMissingTypes.Throw(method, message, constructor, hidden);
            }
            using var output = new MemoryStream();
            using var symbolOutput = new MemoryStream();
            module.Write(output, new Cecil.WriterParameters { WriteSymbols = withSymbols, SymbolStream = withSymbols ? symbolOutput : null, SymbolWriterProvider = withSymbols ? new Cil.PortablePdbWriterProvider() : null });
            symbols = withSymbols ? symbolOutput.ToArray() : null;
            EngineLog.Engine($"mod type kinds: {Path.GetFileName(path)} names {names} by another kind than the type's; {failing.Count} method(s) throw as Mono's compilation of them does, {fixedUp} member access(es) through an address reach the object");
            return output.ToArray();
        }

        /// What one use of an address of a location of a mismatched type does with it.
        internal enum Use { Member, Load, Constructor, Kept }

        /// Rewrites each address of such a location whose one use reaches a member through it: the
        /// address becomes the location's value (a member of a class is reached through the
        /// reference), a load through it goes, and a constructor called on it becomes `newobj`
        /// and a store to the location. Returns how many it rewrote.
        static int Fix(Cecil.MethodDefinition method, Dictionary<Cil.Instruction, (Use Use, Cil.Instruction? User)> uses)
        {
            int count = 0;
            var il = method.Body.GetILProcessor();
            foreach (var (address, (use, user)) in uses)
            {
                if (use is Use.Kept || user == null) continue;
                if (use is Use.Member or Use.Load)
                {
                    if (!ToValue(address)) continue;
                    if (use is Use.Load) { user.OpCode = Cil.OpCodes.Nop; user.Operand = null; }
                    count++;
                }
                else if (use is Use.Constructor && Store(address) is { } store)
                {
                    address.OpCode = Cil.OpCodes.Nop;
                    address.Operand = null;
                    user.OpCode = Cil.OpCodes.Newobj;
                    il.InsertAfter(user, store);
                    count++;
                }
            }
            if (count > 0)
            {
                method.Body.SimplifyMacros();
                method.Body.OptimizeMacros();
            }
            return count;
        }

        /// The address instruction turned into a load of the location's value; false where it
        /// cannot be.
        static bool ToValue(Cil.Instruction address)
        {
            switch (address.OpCode.Code)
            {
                case Cil.Code.Ldloca: case Cil.Code.Ldloca_S: address.OpCode = Cil.OpCodes.Ldloc; return true;
                case Cil.Code.Ldarga: case Cil.Code.Ldarga_S: address.OpCode = Cil.OpCodes.Ldarg; return true;
                case Cil.Code.Ldflda: address.OpCode = Cil.OpCodes.Ldfld; return true;
                case Cil.Code.Ldsflda: address.OpCode = Cil.OpCodes.Ldsfld; return true;
                case Cil.Code.Ldelema: address.OpCode = Cil.OpCodes.Ldelem_Ref; address.Operand = null; return true;
                case Cil.Code.Unbox: address.OpCode = Cil.OpCodes.Castclass; return true;
                default: return false;
            }
        }

        /// The store to the location an address instruction names, after its operands (left on the
        /// stack by removing the address instruction) and the new object; null where there is none.
        static Cil.Instruction? Store(Cil.Instruction address) => address.OpCode.Code switch
        {
            Cil.Code.Ldloca or Cil.Code.Ldloca_S => Cil.Instruction.Create(Cil.OpCodes.Stloc, (Cil.VariableDefinition)address.Operand),
            Cil.Code.Ldarga or Cil.Code.Ldarga_S => Cil.Instruction.Create(Cil.OpCodes.Starg, (Cecil.ParameterDefinition)address.Operand),
            Cil.Code.Ldflda => Cil.Instruction.Create(Cil.OpCodes.Stfld, (Cecil.FieldReference)address.Operand),
            Cil.Code.Ldsflda => Cil.Instruction.Create(Cil.OpCodes.Stsfld, (Cecil.FieldReference)address.Operand),
            Cil.Code.Ldelema => Cil.Instruction.Create(Cil.OpCodes.Stelem_Ref),
            _ => null,
        };

        /// One value on the evaluation stack as Mono's JIT types it: a value type of a class (a
        /// local, field or generic argument tagged `valuetype`), or another; and the instruction
        /// that made it, when it is the address of a location of a mismatched type.
        readonly record struct Slot(Cecil.TypeReference? Odd, Cil.Instruction? Address);

        /// Mono's compilation of a method, as far as the mismatched types decide it.
        internal sealed class Methods(Kinds kinds, Func<Cecil.IMetadataTokenProvider, uint>? token = null)
        {
            public Kinds Kinds => kinds;

            /// The token Mono's message names for an operand: the mod's file's.
            public Func<Cecil.IMetadataTokenProvider, uint>? Token => token;

            readonly Dictionary<Cecil.TypeReference, bool> involved = new();

            /// The TypeRef itself, mismatched as a value type.
            bool Mismatched(Cecil.TypeReference? type) =>
                type != null && type is not Cecil.TypeSpecification && type is not Cecil.GenericParameter && type is not Cecil.TypeDefinition
                && kinds.ValueTagged.Contains(type.MetadataToken.ToInt32());

            /// Whether a type is built from a mismatched one.
            bool Involves(Cecil.TypeReference? type)
            {
                if (type == null || type is Cecil.GenericParameter) return false;
                if (involved.TryGetValue(type, out var known)) return known;
                bool result = Mismatched(type) || type switch
                {
                    Cecil.GenericInstanceType generic => Involves(generic.ElementType) || generic.GenericArguments.Any(Involves),
                    Cecil.TypeSpecification specification => Involves(specification.ElementType),
                    _ => type.DeclaringType != null && Involves(type.DeclaringType),
                };
                return involved[type] = result;
            }

            bool Involves(Cecil.MethodReference method) =>
                Involves(method.DeclaringType) || Involves(method.ReturnType) || method.Parameters.Any(p => Involves(p.ParameterType))
                || (method is Cecil.GenericInstanceMethod generic && generic.GenericArguments.Any(Involves));

            /// Whether Mono's compilation of the method may depend on a mismatched type.
            public bool Touches(Cecil.MethodDefinition method)
            {
                var body = method.Body;
                if (body.Variables.Any(v => Involves(v.VariableType))) return true;
                foreach (var instruction in body.Instructions)
                {
                    switch (instruction.Operand)
                    {
                        case Cecil.MethodReference called when Involves(called) || kinds.ClassTaggedMembers.Contains(((called as Cecil.GenericInstanceMethod)?.ElementMethod ?? called).MetadataToken.ToInt32()):
                        case Cecil.FieldReference field when Involves(field.FieldType) || Involves(field.DeclaringType) || kinds.ClassTaggedMembers.Contains(field.MetadataToken.ToInt32()):
                        case Cecil.TypeReference type when Involves(type):
                        case Cecil.ParameterDefinition parameter when Involves(parameter.ParameterType):
                            return true;
                    }
                }
                return false;
            }

            /// A value type of a class, where Mono's JIT types a location so: a local tagged so, or a
            /// field of the mod's own (FieldDefinition) declared so.
            Cecil.TypeReference? OddLocal(Cil.VariableDefinition variable) => Mismatched(Unmodified(variable.VariableType)) ? Unmodified(variable.VariableType) : null;

            static Cecil.TypeReference Unmodified(Cecil.TypeReference type)
            {
                while (type is Cecil.PinnedType or Cecil.RequiredModifierType or Cecil.OptionalModifierType)
                    type = ((Cecil.TypeSpecification)type).ElementType;
                return type;
            }

            /// A member's type as Mono's JIT sees it: a generic parameter of an instantiation whose
            /// argument is tagged `valuetype` is a value type of the class; a field the mod declares
            /// keeps its tag; anything else in a member's signature is the type's own.
            Cecil.TypeReference? Odd(Cecil.TypeReference type, Cecil.MemberReference member)
            {
                type = Unmodified(type);
                if (type is Cecil.GenericParameter parameter)
                {
                    Cecil.TypeReference? argument = null;
                    if (parameter.Type == Cecil.GenericParameterType.Type && member.DeclaringType is Cecil.GenericInstanceType owner && parameter.Position < owner.GenericArguments.Count)
                        argument = owner.GenericArguments[parameter.Position];
                    else if (parameter.Type == Cecil.GenericParameterType.Method && member is Cecil.GenericInstanceMethod method && parameter.Position < method.GenericArguments.Count)
                        argument = method.GenericArguments[parameter.Position];
                    return Mismatched(argument) ? argument : null;
                }
                return member is Cecil.FieldDefinition && Mismatched(type) ? type : null;
            }

            /// The outcome: the failure Mono's compilation reports (its message and exception), or
            /// the uses of each address of a mismatched type's location.
            public ((string Message, Type Exception)? Failure, Dictionary<Cil.Instruction, (Use, Cil.Instruction?)> Uses) Compile(Cecil.MethodDefinition method)
            {
                var run = new Run(this, method);
                var failure = run.Walk();
                return (failure, run.Uses);
            }

            sealed class Run(Methods owner, Cecil.MethodDefinition method)
            {
                readonly Cil.MethodBody body = method.Body;
                readonly bool verifying = !owner.Kinds.SkipsVerification;
                public readonly Dictionary<Cil.Instruction, (Use, Cil.Instruction?)> Uses = new();
                List<Slot> stack = [];

                public (string, Type)? Walk()
                {
                    // Mono's JIT skips blocks it cannot reach when it verifies (skip_dead_blocks).
                    var reachable = Reachable();
                    var entries = new Dictionary<Cil.Instruction, List<Slot>>();
                    var handlers = new Dictionary<Cil.Instruction, bool>();
                    foreach (var handler in body.ExceptionHandlers)
                    {
                        handlers[handler.HandlerStart] = handler.HandlerType is Cil.ExceptionHandlerType.Catch or Cil.ExceptionHandlerType.Filter;
                        if (handler.FilterStart != null) handlers[handler.FilterStart] = true;
                    }
                    bool live = true;
                    foreach (var instruction in body.Instructions)
                    {
                        if (verifying && !reachable.Contains(instruction)) { live = false; continue; }
                        if (handlers.TryGetValue(instruction, out var catches))
                            stack = catches ? [new Slot(null, null)] : [];
                        else if (!live)
                            stack = entries.TryGetValue(instruction, out var entry) ? [.. entry] : [];
                        live = true;
                        if (Step(instruction) is { } failure) return failure;
                        if (stack.Count > 64) return null;
                        foreach (var target in Targets(instruction))
                            entries.TryAdd(target, [.. stack]);
                        if (Ends(instruction)) live = false;
                    }
                    return null;
                }

                static IEnumerable<Cil.Instruction> Targets(Cil.Instruction instruction) => instruction.Operand switch
                {
                    Cil.Instruction target when instruction.OpCode.Code is not Cil.Code.Leave and not Cil.Code.Leave_S => [target],
                    Cil.Instruction[] targets => targets,
                    _ => [],
                };

                static bool Ends(Cil.Instruction instruction) => instruction.OpCode.FlowControl is Cil.FlowControl.Branch or Cil.FlowControl.Return or Cil.FlowControl.Throw
                    || instruction.OpCode.Code is Cil.Code.Endfinally or Cil.Code.Endfilter or Cil.Code.Jmp;

                HashSet<Cil.Instruction> Reachable()
                {
                    var seen = new HashSet<Cil.Instruction>();
                    var work = new Stack<Cil.Instruction>();
                    if (body.Instructions.Count > 0) work.Push(body.Instructions[0]);
                    foreach (var handler in body.ExceptionHandlers)
                    {
                        if (handler.HandlerStart != null) work.Push(handler.HandlerStart);
                        if (handler.FilterStart != null) work.Push(handler.FilterStart);
                    }
                    while (work.Count > 0)
                    {
                        var instruction = work.Pop();
                        while (instruction != null && seen.Add(instruction))
                        {
                            if (instruction.Operand is Cil.Instruction target) work.Push(target);
                            else if (instruction.Operand is Cil.Instruction[] targets) foreach (var t in targets) work.Push(t);
                            if (Ends(instruction)) break;
                            instruction = instruction.Next;
                        }
                    }
                    return seen;
                }

                (string, Type) Invalid(Cil.Instruction at) =>
                    ($"Invalid IL code in {MonoIlNames.Method(method)}: IL_{at.Offset:x4}: {MonoIlNames.Disassemble(method, at, owner.Token)}\n\n", typeof(InvalidProgramException));

                Slot Pop() => stack.Count == 0 ? default : Pop1();

                Slot Pop1()
                {
                    var slot = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    return slot;
                }

                void Push(Cecil.TypeReference? odd = null, Cil.Instruction? address = null) => stack.Add(new Slot(odd, address));

                /// Records what uses an address; a second use, or one that is none of the kinds, keeps it.
                void Used(Slot slot, Use use, Cil.Instruction user)
                {
                    if (slot.Address is not { } address) return;
                    Uses[address] = Uses.ContainsKey(address) ? (Use.Kept, null) : (use, user);
                }

                Cil.Instruction? AddressOf(Cecil.TypeReference type, Cil.Instruction instruction) => owner.Mismatched(Unmodified(type)) ? instruction : null;

                static bool Same(Cecil.TypeReference? a, Cecil.TypeReference? b) => a != null && b != null && a.FullName == b.FullName;

                /// A store of a value into a location Mono types `odd`: fails unless the value is a
                /// value type of the same class; a value type of a class fails anywhere else.
                bool Incompatible(Cecil.TypeReference? odd, Slot value) => odd != null ? !Same(odd, value.Odd) : value.Odd != null;

                (string, Type)? Step(Cil.Instruction instruction)
                {
                    var code = instruction.OpCode.Code;
                    switch (code)
                    {
                        case Cil.Code.Nop or Cil.Code.Break or Cil.Code.Volatile or Cil.Code.Tail or Cil.Code.Unaligned or Cil.Code.Readonly or Cil.Code.Constrained or Cil.Code.No:
                            return null;
                        case Cil.Code.Ldloc_0 or Cil.Code.Ldloc_1 or Cil.Code.Ldloc_2 or Cil.Code.Ldloc_3 or Cil.Code.Ldloc_S or Cil.Code.Ldloc:
                            Push(owner.OddLocal(Local(instruction)));
                            return null;
                        case Cil.Code.Ldloca_S or Cil.Code.Ldloca:
                            Push(null, AddressOf(Local(instruction).VariableType, instruction));
                            return null;
                        case Cil.Code.Stloc_0 or Cil.Code.Stloc_1 or Cil.Code.Stloc_2 or Cil.Code.Stloc_3 or Cil.Code.Stloc_S or Cil.Code.Stloc:
                        {
                            var value = Pop();
                            // Not checked when the assembly skips verification (dont_verify_stloc):
                            // Mono's JIT then fails an assertion later, or runs the method.
                            if (verifying && Incompatible(owner.OddLocal(Local(instruction)), value)) return Invalid(instruction);
                            return null;
                        }
                        case Cil.Code.Ldarg_0 or Cil.Code.Ldarg_1 or Cil.Code.Ldarg_2 or Cil.Code.Ldarg_3 or Cil.Code.Ldarg_S or Cil.Code.Ldarg:
                            Push();
                            return null;
                        case Cil.Code.Ldarga_S or Cil.Code.Ldarga:
                            Push(null, instruction.Operand is Cecil.ParameterDefinition parameter ? AddressOf(parameter.ParameterType, instruction) : null);
                            return null;
                        case Cil.Code.Starg_S or Cil.Code.Starg:
                            if (verifying && Pop().Odd != null) return Invalid(instruction);
                            return null;
                        case Cil.Code.Ldfld:
                        {
                            var field = (Cecil.FieldReference)instruction.Operand;
                            if (Failed(field) is { } bad) return bad;
                            Used(Pop(), Use.Member, instruction);
                            Push(owner.Odd(field.FieldType, field));
                            return null;
                        }
                        case Cil.Code.Ldflda:
                        {
                            var field = (Cecil.FieldReference)instruction.Operand;
                            if (Failed(field) is { } bad) return bad;
                            var target = Pop();
                            if (target.Odd != null) return Invalid(instruction);
                            Used(target, Use.Member, instruction);
                            Push(null, AddressOf(field.FieldType, instruction));
                            return null;
                        }
                        case Cil.Code.Stfld:
                        {
                            var field = (Cecil.FieldReference)instruction.Operand;
                            if (Failed(field) is { } bad) return bad;
                            var value = Pop();
                            var target = Pop();
                            if (target.Odd != null || Incompatible(owner.Odd(field.FieldType, field), value)) return Invalid(instruction);
                            Used(target, Use.Member, instruction);
                            return null;
                        }
                        case Cil.Code.Ldsfld:
                        {
                            var field = (Cecil.FieldReference)instruction.Operand;
                            if (Failed(field) is { } bad) return bad;
                            Push(owner.Odd(field.FieldType, field));
                            return null;
                        }
                        case Cil.Code.Ldsflda:
                        {
                            var field = (Cecil.FieldReference)instruction.Operand;
                            if (Failed(field) is { } bad) return bad;
                            Push(null, AddressOf(field.FieldType, instruction));
                            return null;
                        }
                        case Cil.Code.Stsfld:
                        {
                            // Mono does not check a static field's store.
                            if (Failed((Cecil.FieldReference)instruction.Operand) is { } bad) return bad;
                            Pop();
                            return null;
                        }
                        case Cil.Code.Call or Cil.Code.Callvirt or Cil.Code.Newobj:
                            return Call(instruction, (Cecil.MethodReference)instruction.Operand);
                        case Cil.Code.Ret:
                            if (method.ReturnType.MetadataType != Cecil.MetadataType.Void && Pop().Odd != null) return Invalid(instruction);
                            stack.Clear();
                            return null;
                        case Cil.Code.Box:
                        {
                            var value = Pop();
                            // A box of a class leaves the value as it is.
                            if (owner.Mismatched((Cecil.TypeReference)instruction.Operand)) { stack.Add(value with { Address = null }); return null; }
                            if (value.Odd != null) return Invalid(instruction);
                            Used(value, Use.Kept, instruction);
                            Push();
                            return null;
                        }
                        case Cil.Code.Pop:
                            Used(Pop(), Use.Kept, instruction);
                            return null;
                        case Cil.Code.Dup:
                        {
                            var value = Pop();
                            if (value.Address != null) Uses[value.Address] = (Use.Kept, null);
                            stack.Add(value);
                            stack.Add(value);
                            return null;
                        }
                        case Cil.Code.Ldobj:
                        {
                            var address = Pop();
                            if (address.Odd != null) return Invalid(instruction);
                            Used(address, Use.Load, instruction);
                            Push();
                            return null;
                        }
                        case Cil.Code.Initobj:
                        {
                            var address = Pop();
                            if (address.Odd != null) return Invalid(instruction);
                            Used(address, Use.Kept, instruction);
                            return null;
                        }
                        case Cil.Code.Unbox:
                            if (Pop().Odd != null) return Invalid(instruction);
                            Push(null, AddressOf((Cecil.TypeReference)instruction.Operand, instruction));
                            return null;
                        case Cil.Code.Ldelema:
                            Pop();
                            Pop();
                            Push(null, AddressOf((Cecil.TypeReference)instruction.Operand, instruction));
                            return null;
                        case Cil.Code.Leave or Cil.Code.Leave_S:
                            stack.Clear();
                            return null;
                    }
                    // Any other instruction: a value type of a class among its operands fails it
                    // (Mono's JIT accepts it only where the cases above do), an address it takes is
                    // used as it is.
                    int pops = Pops(instruction);
                    for (int i = 0; i < pops; i++)
                    {
                        var value = Pop();
                        if (value.Odd != null) return Invalid(instruction);
                        Used(value, Use.Kept, instruction);
                    }
                    for (int i = Pushes(instruction); i > 0; i--) Push();
                    return null;
                }

                /// A member reference whose signature tags a value type `class` fails as Mono reads it.
                (string, Type)? Failed(Cecil.MemberReference member)
                {
                    var token = member.MetadataToken.ToInt32();
                    if (!owner.Kinds.ClassTaggedMembers.Contains(token)) return null;
                    var kind = ClassTaggedKind(member);
                    return ($"Expected reference type but got type kind {kind}", typeof(BadImageFormatException));
                }

                int ClassTaggedKind(Cecil.MemberReference member)
                {
                    IEnumerable<Cecil.TypeReference> types = member switch
                    {
                        Cecil.MethodReference m => m.Parameters.Select(p => p.ParameterType).Prepend(m.ReturnType),
                        Cecil.FieldReference f => [f.FieldType],
                        _ => [],
                    };
                    foreach (var type in types)
                        if (FirstClassTagged(type) is { } kind) return kind;
                    return 0x11;
                }

                int? FirstClassTagged(Cecil.TypeReference type)
                {
                    switch (type)
                    {
                        case Cecil.GenericInstanceType generic:
                            return FirstClassTagged(generic.ElementType) ?? generic.GenericArguments.Select(FirstClassTagged).FirstOrDefault(k => k != null);
                        case Cecil.TypeSpecification specification:
                            return FirstClassTagged(specification.ElementType);
                        case Cecil.GenericParameter or Cecil.TypeDefinition:
                            return null;
                    }
                    return owner.Kinds.ClassTagged.TryGetValue(type.MetadataToken.ToInt32(), out var kind) ? kind : null;
                }

                (string, Type)? Call(Cil.Instruction instruction, Cecil.MethodReference called)
                {
                    var element = (called as Cecil.GenericInstanceMethod)?.ElementMethod ?? called;
                    if (Failed(element) is { } bad) return bad;
                    bool newobj = instruction.OpCode.Code == Cil.Code.Newobj;
                    var arguments = new Slot[called.Parameters.Count];
                    for (int i = arguments.Length - 1; i >= 0; i--) arguments[i] = Pop();
                    Slot? self = called.HasThis && !newobj ? Pop() : null;
                    // check_call_signature, in every assembly.
                    if (self is { Odd: not null }) return Invalid(instruction);
                    for (int i = 0; i < arguments.Length; i++)
                    {
                        var parameter = called.Parameters[i].ParameterType;
                        if (Incompatible(parameter.IsByReference ? null : owner.Odd(parameter, called), arguments[i])) return Invalid(instruction);
                        Used(arguments[i], Use.Kept, instruction);
                    }
                    if (self is { } target)
                        Used(target, called.Name == ".ctor" ? Use.Constructor : instruction.Previous?.OpCode.Code == Cil.Code.Constrained ? Use.Kept : Use.Member, instruction);
                    if (newobj) Push();
                    else if (called.ReturnType.MetadataType != Cecil.MetadataType.Void) Push(owner.Odd(called.ReturnType, called));
                    return null;
                }

                Cil.VariableDefinition Local(Cil.Instruction instruction) => instruction.OpCode.Code switch
                {
                    Cil.Code.Ldloc_0 or Cil.Code.Stloc_0 => body.Variables[0],
                    Cil.Code.Ldloc_1 or Cil.Code.Stloc_1 => body.Variables[1],
                    Cil.Code.Ldloc_2 or Cil.Code.Stloc_2 => body.Variables[2],
                    Cil.Code.Ldloc_3 or Cil.Code.Stloc_3 => body.Variables[3],
                    _ => (Cil.VariableDefinition)instruction.Operand,
                };

                int Pops(Cil.Instruction instruction) => instruction.OpCode.StackBehaviourPop switch
                {
                    Cil.StackBehaviour.Pop0 => 0,
                    Cil.StackBehaviour.Pop1 or Cil.StackBehaviour.Popi or Cil.StackBehaviour.Popref => 1,
                    Cil.StackBehaviour.Pop1_pop1 or Cil.StackBehaviour.Popi_pop1 or Cil.StackBehaviour.Popi_popi or Cil.StackBehaviour.Popi_popi8
                        or Cil.StackBehaviour.Popi_popr4 or Cil.StackBehaviour.Popi_popr8 or Cil.StackBehaviour.Popref_pop1 or Cil.StackBehaviour.Popref_popi => 2,
                    Cil.StackBehaviour.Popi_popi_popi or Cil.StackBehaviour.Popref_popi_popi or Cil.StackBehaviour.Popref_popi_popi8 or Cil.StackBehaviour.Popref_popi_popr4
                        or Cil.StackBehaviour.Popref_popi_popr8 or Cil.StackBehaviour.Popref_popi_popref => 3,
                    // calli: the signature's parameters, `this` and the function pointer.
                    Cil.StackBehaviour.Varpop when instruction.Operand is Cecil.CallSite site => site.Parameters.Count + (site.HasThis ? 1 : 0) + 1,
                    Cil.StackBehaviour.PopAll => stack.Count,
                    _ => 0,
                };

                static int Pushes(Cil.Instruction instruction) => instruction.OpCode.StackBehaviourPush switch
                {
                    Cil.StackBehaviour.Push0 => 0,
                    Cil.StackBehaviour.Push1_push1 => 2,
                    Cil.StackBehaviour.Varpush when instruction.Operand is Cecil.CallSite site => site.ReturnType.MetadataType == Cecil.MetadataType.Void ? 0 : 1,
                    Cil.StackBehaviour.Varpush => 0,
                    _ => 1,
                };
            }
        }
    }

    /// The tokens of a mod's file, which Mono's messages name, for references of the image an
    /// earlier pass rewrote (Cecil renumbers member references as it writes): matched by full
    /// name, read only when a message needs one.
    internal sealed class OriginalTokens(string path) : IDisposable
    {
        Cecil.ModuleDefinition? module;
        Dictionary<string, uint>? tokens;

        public uint Token(Cecil.IMetadataTokenProvider operand)
        {
            var own = operand.MetadataToken.ToUInt32();
            if (operand is not Cecil.MemberReference member) return own;
            if (tokens == null)
            {
                tokens = new Dictionary<string, uint>(StringComparer.Ordinal);
                try
                {
                    module = Cecil.ModuleDefinition.ReadModule(path);
                    void Add(Cecil.MemberReference m) => tokens.TryAdd(m.GetType().Name + " " + m.FullName, m.MetadataToken.ToUInt32());
                    foreach (var reference in module.GetMemberReferences()) Add(reference);
                    foreach (var reference in module.GetTypeReferences()) Add(reference);
                    foreach (var type in module.GetTypes())
                    {
                        Add(type);
                        foreach (var field in type.Fields) Add(field);
                        foreach (var method in type.Methods)
                        {
                            Add(method);
                            if (!method.HasBody) continue;
                            foreach (var instruction in method.Body.Instructions)
                                if (instruction.Operand is Cecil.TypeSpecification or Cecil.GenericInstanceMethod) Add((Cecil.MemberReference)instruction.Operand);
                        }
                    }
                }
                catch (Exception e) when (e is BadImageFormatException or IOException or Cecil.AssemblyResolutionException or InvalidOperationException)
                {
                }
            }
            return tokens.TryGetValue(member.GetType().Name + " " + member.FullName, out var token) ? token : own;
        }

        public void Dispose() => module?.Dispose();
    }

    /// Names as Mono's messages print them (metadata/debug-helpers.c).
    internal static class MonoIlNames
    {
        /// mono_method_full_name (method, TRUE): "Ns.Class:Name (int,string)".
        public static string Method(Cecil.MethodDefinition method)
        {
            var text = new StringBuilder(TypeFullName(method.DeclaringType)).Append(':').Append(method.Name);
            if (method.HasGenericParameters) text.Append('<').Append(string.Join(", ", method.GenericParameters.Select(p => p.Name))).Append('>');
            text.Append(" (").Append(string.Join(",", method.Parameters.Select(p => Desc(p.ParameterType)))).Append(')');
            return text.ToString();
        }

        /// mono_type_full_name of a class: its namespace, its enclosing classes and name joined by
        /// '/', a generic definition's parameters.
        static string TypeFullName(Cecil.TypeReference type)
        {
            var name = type.DeclaringType != null ? TypeFullName(type.DeclaringType) + "/" + type.Name : (type.Namespace.Length > 0 ? type.Namespace + "." : "") + type.Name;
            if (type is Cecil.TypeDefinition { HasGenericParameters: true } definition)
            {
                var own = definition.GenericParameters.Skip(definition.DeclaringType?.GenericParameters.Count ?? 0).Select(p => p.Name).ToList();
                if (own.Count > 0) name += "<" + string.Join(",", own) + ">";
            }
            return name;
        }

        /// mono_type_get_desc with namespaces.
        public static string Desc(Cecil.TypeReference type)
        {
            switch (type)
            {
                case Cecil.ByReferenceType reference: return Desc(reference.ElementType) + "&";
                case Cecil.PointerType pointer: return Desc(pointer.ElementType) + "*";
                case Cecil.ArrayType array: return Desc(array.ElementType) + "[" + new string(',', Math.Max(array.Rank - 1, 0)) + "]";
                case Cecil.GenericInstanceType generic: return Desc(generic.ElementType) + "<" + string.Join(", ", generic.GenericArguments.Select(Desc)) + ">";
                case Cecil.RequiredModifierType required: return Desc(required.ElementType) + " modreq(" + Desc(required.ModifierType) + ")";
                case Cecil.OptionalModifierType optional: return Desc(optional.ElementType) + " modopt(" + Desc(optional.ModifierType) + ")";
                case Cecil.PinnedType pinned: return Desc(pinned.ElementType);
                case Cecil.FunctionPointerType: return "*()";
                case Cecil.GenericParameter parameter: return parameter.Name;
            }
            var primitive = type.MetadataType switch
            {
                Cecil.MetadataType.Void => "void",
                Cecil.MetadataType.Boolean => "bool",
                Cecil.MetadataType.Char => "char",
                Cecil.MetadataType.SByte => "sbyte",
                Cecil.MetadataType.Byte => "byte",
                Cecil.MetadataType.Int16 => "int16",
                Cecil.MetadataType.UInt16 => "uint16",
                Cecil.MetadataType.Int32 => "int",
                Cecil.MetadataType.UInt32 => "uint",
                Cecil.MetadataType.Int64 => "long",
                Cecil.MetadataType.UInt64 => "ulong",
                Cecil.MetadataType.Single => "single",
                Cecil.MetadataType.Double => "double",
                Cecil.MetadataType.String => "string",
                Cecil.MetadataType.Object => "object",
                Cecil.MetadataType.IntPtr => "intptr",
                Cecil.MetadataType.UIntPtr => "uintptr",
                Cecil.MetadataType.TypedByReference => "typedbyref",
                _ => null,
            };
            if (primitive != null) return primitive;
            return type.DeclaringType != null ? Desc(type.DeclaringType) + "/" + type.Name : (type.Namespace.Length > 0 ? type.Namespace + "." : "") + type.Name;
        }

        /// One instruction as mono_disasm_code_one writes it with its default helper: the opcode's
        /// name padded to 10, then a token as 0x%08x, a string in quotes, a variable's or
        /// argument's number, a branch's target as IL_%04x, a number.
        public static string Disassemble(Cecil.MethodDefinition method, Cil.Instruction instruction, Func<Cecil.IMetadataTokenProvider, uint>? token = null)
        {
            var text = new StringBuilder(instruction.OpCode.Name.PadRight(10));
            switch (instruction.OpCode.OperandType)
            {
                case Cil.OperandType.InlineType or Cil.OperandType.InlineField or Cil.OperandType.InlineMethod or Cil.OperandType.InlineTok or Cil.OperandType.InlineSig:
                    var operand = (Cecil.IMetadataTokenProvider)instruction.Operand;
                    text.Append("0x").Append((token?.Invoke(operand) ?? operand.MetadataToken.ToUInt32()).ToString("x8", CultureInfo.InvariantCulture));
                    break;
                case Cil.OperandType.InlineString:
                    text.Append('"').Append((string)instruction.Operand).Append('"');
                    break;
                case Cil.OperandType.InlineVar or Cil.OperandType.ShortInlineVar:
                    text.Append(instruction.Operand switch
                    {
                        Cil.VariableDefinition variable => variable.Index,
                        Cecil.ParameterDefinition parameter => parameter.Index + (method.HasThis ? 1 : 0),
                        _ => 0,
                    });
                    break;
                case Cil.OperandType.InlineBrTarget or Cil.OperandType.ShortInlineBrTarget:
                    text.Append("IL_").Append(((Cil.Instruction)instruction.Operand).Offset.ToString("x4", CultureInfo.InvariantCulture));
                    break;
                case Cil.OperandType.InlineSwitch:
                    text.Append('(').Append(string.Join(", ", ((Cil.Instruction[])instruction.Operand).Select(t => "IL_" + t.Offset.ToString("x4", CultureInfo.InvariantCulture)))).Append(')');
                    break;
                case Cil.OperandType.InlineI or Cil.OperandType.ShortInlineI:
                    text.Append(Convert.ToInt32(instruction.Operand, CultureInfo.InvariantCulture));
                    break;
                case Cil.OperandType.InlineR or Cil.OperandType.ShortInlineR:
                    text.Append(Convert.ToDouble(instruction.Operand, CultureInfo.InvariantCulture).ToString("G6", CultureInfo.InvariantCulture));
                    break;
            }
            return text.ToString();
        }
    }
}
