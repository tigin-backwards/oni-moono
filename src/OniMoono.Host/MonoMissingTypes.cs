using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using OniMoono.Engine;
using Cecil = Mono.Cecil;

namespace OniMoono.Host
{
    /// ARCHITECTURE D40. A mod built for an older game names types the game no longer has
    /// (Endless Science fix's prefix calls `Worker Workable.get_worker()`; Worker became WorkerBase).
    /// Both runtimes fail such a method as a whole when it is compiled, at every call, before any of
    /// it runs; they report it differently. Mono's JIT (mini/method-to-ir.c; Unity's fork, branch
    /// unity-6000.3-mbe) throws a TypeLoadException from the caller's frame, the failing method
    /// having none: "Could not resolve type with token 01000071 from typeref (expected class
    /// 'Worker' in assembly 'Assembly-CSharp, …')" (metadata/class.c,
    /// mono_class_from_typeref_checked) where it resolves the reference with an error it keeps
    /// (a method's exception clauses and locals, calls and newobj on a member reference, ldtoken,
    /// a standalone signature), "Failure has occurred while loading a type." where it drops the
    /// error (CHECK_TYPELOAD: castclass, isinst, newarr and the other type operands, fields, a call
    /// to the mod's own method whose signature names the type). CoreCLR's frame is the failing
    /// method's own, and it says "Method not found: 'Worker Workable.get_worker()'." or "Could not
    /// load type 'Worker' from assembly …". Measured in both players (UnityProbe's missingtypes).
    ///
    /// As a mod loads, each of its methods that names, in Mono's order (exception clauses, locals,
    /// then the IL), a top-level type missing from an assembly already loaded whose types are the
    /// same in both players (the game's own from Managed/, other than the class libraries, and
    /// mods'; of Unity's engine and packages, which ours replace, a type both ours and Unity's copy
    /// in Managed/ lack) gets the body Mono's compilation gives it: it throws Mono's
    /// exception, and is hidden from stack traces. So does a method naming a member its type lacks
    /// (MonoMissingMembers, D42), first in the same IL order. Nested types (D32) and assemblies not
    /// loaded yet are left as they were.
    public static class MonoMissingTypes
    {
        const string LostError = "Failure has occurred while loading a type.";

        /// The mods' assemblies loaded so far (ModAccess.LoadFrom): their types are the same in both
        /// players.
        static readonly ConcurrentDictionary<Assembly, byte> mods = new();

        internal static void ModLoaded(Assembly assembly) => mods.TryAdd(assembly, 0);

        /// The image with each method that names a missing type throwing as Mono's compilation of
        /// it does, or null when it names none. `original` is the mod's file, whose metadata tokens
        /// Mono's message names; `symbols` (portable PDB bytes or null) are rewritten to match.
        internal static byte[]? Adjust(byte[] image, ref byte[]? symbols, string path)
        {
            Dictionary<(string Assembly, string Namespace, string Name), int> tokens;
            Dictionary<int, MonoMissingMembers.Missing> members;
            try
            {
                using (var pe = new PEReader(File.OpenRead(path)))
                {
                    if (!pe.HasMetadata) return null;
                    tokens = MissingTypeRefs(pe.GetMetadataReader());
                }
                // Members by the tokens of the image as earlier passes left it, which Cecil reads.
                using var current = new PEReader(new MemoryStream(image, writable: false));
                members = MonoMissingMembers.Find(current.GetMetadataReader());
            }
            catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
            if (tokens.Count == 0 && members.Count == 0) return null;
            try
            {
                return Rewrite(image, ref symbols, path, tokens, members);
            }
            catch (Exception e) when (e is Cecil.AssemblyResolutionException or BadImageFormatException or InvalidOperationException or ArgumentException or NullReferenceException)
            {
                // The mod loads as it is: such methods fail as CoreCLR fails them. (Cecil's writer
                // throws NullReferenceException on debug information it cannot map.)
                EngineLog.Engine($"mod missing types: {Path.GetFileName(path)} not rewritten ({e.GetType().Name}: {e.Message}); it loads unchanged");
                return null;
            }
        }

        /// The mod's top-level TypeRefs whose type its assembly lacks, by (assembly, namespace, name),
        /// with their tokens.
        internal static Dictionary<(string Assembly, string Namespace, string Name), int> MissingTypeRefs(MetadataReader md)
        {
            var missing = new Dictionary<(string, string, string), int>();
            foreach (var handle in md.TypeReferences)
            {
                var reference = md.GetTypeReference(handle);
                if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
                var assemblyName = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                var key = (assemblyName, md.GetString(reference.Namespace), md.GetString(reference.Name));
                if (IsMissing(key.assemblyName, key.Item2, key.Item3)) missing.TryAdd(key, MetadataTokens.GetToken(handle));
            }
            return missing;
        }

        static readonly ConcurrentDictionary<(string, string, string), bool> known = new();

        /// Whether the assembly loaded under that simple name (compared ignoring case, as Mono binds
        /// it: D15) lacks the type, when its types are the same in both players; for Unity's engine
        /// and packages, ours, whether both ours and the original's copy in Managed/ lack it (a type
        /// Unity's copy has and ours lacks is our API's gap, which fails as CoreCLR fails it).
        static bool IsMissing(string assemblyName, string ns, string name)
        {
            if (Loaded(assemblyName) is not { } assembly) return false;
            bool same = SameInBothPlayers(assembly);
            if (!same && UnityTypes(assembly) == null) return false;
            return known.GetOrAdd((assembly.GetName().Name!, ns, name), key =>
                assembly.GetType(key.Item2.Length == 0 ? key.Item3 : key.Item2 + "." + key.Item3, throwOnError: false) == null
                && (same || !UnityTypes(assembly)!.Contains((key.Item2, key.Item3))));
        }

        static readonly ConcurrentDictionary<string, HashSet<(string, string)>?> unityTypes = new(StringComparer.Ordinal);

        /// The top-level types (defined or forwarded) of the original's copy of a Unity assembly
        /// ours replaces (Managed/NAME.dll), or null for another assembly.
        internal static HashSet<(string Namespace, string Name)>? UnityTypes(Assembly assembly)
        {
            var name = assembly.GetName().Name!;
            if (!name.StartsWith("Unity", StringComparison.Ordinal) || AssemblyPolicy.ReferenceDirectories[0] is not { Length: > 0 } managed) return null;
            return unityTypes.GetOrAdd(name, n =>
            {
                var path = Path.Combine(managed, n + ".dll");
                if (!File.Exists(path)) return null;
                try
                {
                    using var pe = new PEReader(File.OpenRead(path));
                    if (!pe.HasMetadata) return null;
                    var md = pe.GetMetadataReader();
                    var types = new HashSet<(string, string)>();
                    foreach (var handle in md.TypeDefinitions)
                    {
                        var type = md.GetTypeDefinition(handle);
                        if (type.GetDeclaringType().IsNil) types.Add((md.GetString(type.Namespace), md.GetString(type.Name)));
                    }
                    foreach (var handle in md.ExportedTypes)
                    {
                        var type = md.GetExportedType(handle);
                        if (type.Implementation.Kind != HandleKind.ExportedType) types.Add((md.GetString(type.Namespace), md.GetString(type.Name)));
                    }
                    return types;
                }
                catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            });
        }

        static readonly ConcurrentDictionary<string, Assembly> loaded = new(StringComparer.OrdinalIgnoreCase);

        internal static Assembly? Loaded(string simpleName)
        {
            if (loaded.TryGetValue(simpleName, out var known)) return known;
            foreach (var assembly in AssemblyLoadContext.Default.Assemblies)
                if (!assembly.IsDynamic && string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return loaded.GetOrAdd(simpleName, assembly);
            return null;
        }

        static readonly ConcurrentDictionary<Assembly, bool> sameInBothPlayers = new();

        /// A mod's assembly, or the game's own from Managed/: not Unity's engine or packages (ours
        /// are not Unity's), nor Mono's class libraries there (the framework answers for them).
        internal static bool SameInBothPlayers(Assembly assembly) =>
            mods.ContainsKey(assembly) || sameInBothPlayers.GetOrAdd(assembly, GameOwn);

        static bool GameOwn(Assembly assembly)
        {
            var name = assembly.GetName().Name!;
            if (name.StartsWith("Unity", StringComparison.Ordinal) || AssemblyPolicy.ReferenceDirectories[0] is not { Length: > 0 } managed
                || !File.Exists(Path.Combine(managed, name + ".dll"))) return false;
            return assembly.Location is not { Length: > 0 } location || !location.StartsWith(RuntimeEnvironment.GetRuntimeDirectory(), StringComparison.Ordinal);
        }

        static byte[]? Rewrite(byte[] image, ref byte[]? symbols, string path, Dictionary<(string Assembly, string Namespace, string Name), int> tokens,
            Dictionary<int, MonoMissingMembers.Missing> members)
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
                    SymbolReaderProvider = withSymbols ? new Cecil.Cil.PortablePdbReaderProvider() : null,
                });
            }
            catch (Exception e) when (withSymbols && e is Cecil.Cil.SymbolsNotMatchingException or Cecil.Cil.SymbolsNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                withSymbols = false;
                module = Cecil.ModuleDefinition.ReadModule(new MemoryStream(image), new Cecil.ReaderParameters { AssemblyResolver = resolver });
            }
            using var owned = module;
            var scan = new Scan(tokens, members);
            var failing = new List<(Cecil.MethodDefinition Method, string Message, string Type, Type Exception)>();
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                    if (method.HasBody && scan.Failure(method) is var (message, missing, exception))
                        failing.Add((method, message, missing, exception));
            if (failing.Count == 0) return null;
            var constructors = new Dictionary<Type, Cecil.MethodReference>();
            var hidden = module.ImportReference(typeof(System.Diagnostics.StackTraceHiddenAttribute).GetConstructor(Type.EmptyTypes)!);
            foreach (var (method, message, _, exception) in failing)
            {
                if (!constructors.TryGetValue(exception, out var constructor))
                    constructors[exception] = constructor = module.ImportReference(exception.GetConstructor([typeof(string)])!);
                Throw(method, message, constructor, hidden);
            }
            using var output = new MemoryStream();
            using var symbolOutput = new MemoryStream();
            module.Write(output, new Cecil.WriterParameters { WriteSymbols = withSymbols, SymbolStream = withSymbols ? symbolOutput : null, SymbolWriterProvider = withSymbols ? new Cecil.Cil.PortablePdbWriterProvider() : null });
            symbols = withSymbols ? symbolOutput.ToArray() : null;
            var names = string.Join(", ", failing.Select(f => f.Type).Distinct().Take(8));
            EngineLog.Engine($"mod missing types: {Path.GetFileName(path)}: {failing.Count} method(s) name types or members their assemblies lack ({names}); they throw as Mono's compilation of them does");
            return output.ToArray();
        }

        /// The method's body becomes `throw new TypeLoadException(message)` (or MissingMethodException,
        /// MissingFieldException, and MonoTypeKinds' InvalidProgramException and
        /// BadImageFormatException), its frame hidden: on Mono it never runs, and the exception
        /// comes from its caller's frame.
        internal static void Throw(Cecil.MethodDefinition method, string message, Cecil.MethodReference exception, Cecil.MethodReference hidden)
        {
            var body = method.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            body.InitLocals = false;
            var il = body.GetILProcessor();
            il.Emit(Cecil.Cil.OpCodes.Ldstr, message);
            il.Emit(Cecil.Cil.OpCodes.Newobj, exception);
            il.Emit(Cecil.Cil.OpCodes.Throw);
            method.DebugInformation.SequencePoints.Clear();
            method.DebugInformation.Scope = null;
            method.DebugInformation.StateMachineKickOffMethod = null;
            method.CustomDebugInformations.Clear();
            method.CustomAttributes.Add(new Cecil.CustomAttribute(hidden));
        }

        /// Where Mono's compilation of a method first fails on a missing type or member, and its
        /// message.
        internal sealed class Scan(Dictionary<(string Assembly, string Namespace, string Name), int> tokens, Dictionary<int, MonoMissingMembers.Missing> members)
        {
            /// The message, the missing type's or member's name and the exception's type, or null
            /// when the method compiles on Mono.
            public (string Message, string Type, Type Exception)? Failure(Cecil.MethodDefinition method)
            {
                var body = method.Body;
                // The header first (metadata.c, mono_metadata_parse_mh_full): the exception clauses'
                // catch types, then the locals, each resolved keeping its error.
                foreach (var handler in body.ExceptionHandlers)
                    if (Missing(handler.CatchType) is { } t) return Kept(t);
                foreach (var variable in body.Variables)
                    if (Missing(variable.VariableType) is { } t) return Kept(t);
                foreach (var instruction in body.Instructions)
                {
                    var code = instruction.OpCode.Code;
                    switch (instruction.Operand)
                    {
                        case Cecil.MethodReference called:
                            if (code == Cecil.Cil.Code.Ldtoken)
                            {
                                if (InMember(called) is { } t) return Kept(t);
                            }
                            else if (Called(called) is { } failure) return failure;
                            // A member reference the type has no member for (D42).
                            if (Absent((called as Cecil.GenericInstanceMethod)?.ElementMethod ?? called) is { } absent) return absent;
                            break;
                        case Cecil.FieldReference field:
                            if ((field is Cecil.FieldDefinition ? Missing(field.FieldType) : Missing(field.DeclaringType) ?? Missing(field.FieldType)) is { } f)
                                return code == Cecil.Cil.Code.Ldtoken ? Kept(f) : Lost(f);
                            if (Absent(field) is { } absentField) return absentField;
                            break;
                        case Cecil.TypeReference type:
                            if (Missing(type) is { } m)
                                return code == Cecil.Cil.Code.Ldtoken || (code == Cecil.Cil.Code.Sizeof && type is Cecil.TypeSpecification) ? Kept(m) : Lost(m);
                            break;
                        case Cecil.CallSite site:
                            if ((Missing(site.ReturnType) ?? site.Parameters.Select(p => Missing(p.ParameterType)).FirstOrDefault(p => p != null)) is { } s)
                                return Kept(s);
                            break;
                    }
                }
                return null;
            }

            /// A member reference MonoMissingMembers found no member for: Mono keeps the lookup's
            /// error (MissingMethodException or MissingFieldException).
            (string, string, Type)? Absent(Cecil.MemberReference member)
            {
                if (member is Cecil.IMemberDefinition || !members.TryGetValue(member.MetadataToken.ToInt32(), out var missing)) return null;
                return (missing.Message, member.DeclaringType.Name + "." + member.Name, missing.Field ? typeof(MissingFieldException) : typeof(MissingMethodException));
            }

            /// A call, newobj, jmp, ldftn or ldvirtftn: a member reference (and a generic
            /// instantiation's arguments) is resolved keeping its error; the mod's own method is
            /// found, and its signature fails later, the error dropped.
            (string, string, Type)? Called(Cecil.MethodReference method)
            {
                if (method is Cecil.GenericInstanceMethod generic)
                {
                    if (Called(generic.ElementMethod) is { } element) return element;
                    foreach (var argument in generic.GenericArguments)
                        if (Missing(argument) is { } t) return Kept(t);
                    return null;
                }
                if (method is Cecil.MethodDefinition own)
                    return InSignature(own) is { } t ? Lost(t) : null;
                return InMember(method) is { } m ? Kept(m) : null;
            }

            Cecil.TypeReference? InMember(Cecil.MethodReference method) => Missing(method.DeclaringType) ?? InSignature(method);

            Cecil.TypeReference? InSignature(Cecil.MethodReference method)
            {
                if (Missing(method.ReturnType) is { } r) return r;
                foreach (var parameter in method.Parameters)
                    if (Missing(parameter.ParameterType) is { } p) return p;
                return null;
            }

            /// The first missing top-level type a type is built from, in the order Mono reads it.
            public Cecil.TypeReference? Missing(Cecil.TypeReference? type)
            {
                switch (type)
                {
                    case null or Cecil.GenericParameter or Cecil.TypeDefinition:
                        return null;
                    case Cecil.GenericInstanceType generic:
                        if (Missing(generic.ElementType) is { } element) return element;
                        foreach (var argument in generic.GenericArguments)
                            if (Missing(argument) is { } t) return t;
                        return null;
                    case Cecil.IModifierType modified:
                        return Missing(((Cecil.TypeSpecification)type).ElementType) ?? Missing(modified.ModifierType);
                    case Cecil.FunctionPointerType pointer:
                        if (Missing(pointer.ReturnType) is { } returned) return returned;
                        foreach (var parameter in pointer.Parameters)
                            if (Missing(parameter.ParameterType) is { } p) return p;
                        return null;
                    case Cecil.TypeSpecification specification:
                        return Missing(specification.ElementType);
                }
                // A nested type fails with its enclosing one; its own name is D32's.
                if (type.DeclaringType != null) return Missing(type.DeclaringType);
                return type.Scope is Cecil.AssemblyNameReference assembly && tokens.ContainsKey((assembly.Name, type.Namespace, type.Name)) ? type : null;
            }

            (string, string, Type) Kept(Cecil.TypeReference type)
            {
                var assembly = (Cecil.AssemblyNameReference)type.Scope;
                var name = type.Namespace.Length == 0 ? type.Name : type.Namespace + "." + type.Name;
                var token = tokens[(assembly.Name, type.Namespace, type.Name)];
                return ($"Could not resolve type with token {token:x8} from typeref (expected class '{name}' in assembly '{MonoAssemblyName(assembly)}')", name, typeof(TypeLoadException));
            }

            static (string, string, Type) Lost(Cecil.TypeReference type) =>
                (LostError, type.Namespace.Length == 0 ? type.Name : type.Namespace + "." + type.Name, typeof(TypeLoadException));
        }

        /// An assembly reference as Mono prints it (metadata/assembly.c, mono_stringify_assembly_name).
        internal static string MonoAssemblyName(Cecil.AssemblyNameReference reference)
        {
            var quote = reference.Name.Length > 0 && char.IsWhiteSpace(reference.Name[0]) ? "\"" : "";
            var version = reference.Version ?? new Version(0, 0, 0, 0);
            var culture = string.IsNullOrEmpty(reference.Culture) ? "neutral" : reference.Culture;
            var key = reference.PublicKeyToken is { Length: > 0 } t ? Convert.ToHexStringLower(t) : "null";
            var retargetable = reference.IsRetargetable ? ", Retargetable=Yes" : "";
            return $"{quote}{reference.Name}{quote}, Version={Math.Max(version.Major, 0)}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}.{Math.Max(version.Revision, 0)}, Culture={culture}, PublicKeyToken={key}{retargetable}";
        }
    }
}
