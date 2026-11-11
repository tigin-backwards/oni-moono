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
using Mono.Cecil.Rocks;
using OniMoono.Engine;
using Cecil = Mono.Cecil;
using OniMoono.Tweaks;

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
    /// loaded yet are left as they were. What the original's copy of an engine or class library
    /// has and ours lacks (MonoApiGaps, D58) throws where it is named instead.
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
            Dictionary<(string Assembly, string Namespace, string Name), Gap> gaps;
            Dictionary<int, MonoMissingMembers.Missing> members;
            try
            {
                using (var pe = new PEReader(File.OpenRead(path)))
                {
                    if (!pe.HasMetadata) return null;
                    tokens = MissingTypeRefs(pe.GetMetadataReader(), out gaps);
                }
                // Members by the tokens of the image as earlier passes left it, which Cecil reads.
                using var current = new PEReader(new MemoryStream(image, writable: false));
                members = MonoMissingMembers.Find(current.GetMetadataReader());
            }
            catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
            if (tokens.Count == 0 && gaps.Count == 0 && members.Count == 0) return null;
            try
            {
                return Rewrite(image, ref symbols, path, tokens, gaps, members);
            }
            catch (Exception e) when (e is Cecil.AssemblyResolutionException or BadImageFormatException or InvalidOperationException or ArgumentException or NullReferenceException)
            {
                // The mod loads as it is: such methods fail as CoreCLR fails them. (Cecil's writer
                // throws NullReferenceException on debug information it cannot map.)
                EngineLog.Engine($"mod missing types: {Path.GetFileName(path)} not rewritten ({e.GetType().Name}: {e.Message}); it loads unchanged");
                return null;
            }
        }

        /// A type ours cannot resolve where the original can (D58): its TypeRef's token,
        /// why ours cannot (MonoApiGaps.Ours), the assembly ours could not load, and the original's
        /// underlying type where it is an enum.
        internal readonly record struct Gap(int Token, MonoApiGaps.OursFailure Failure, string? Assembly, PrimitiveTypeCode? Enum);

        /// The mod's top-level TypeRefs whose type its assembly lacks, by (assembly, namespace, name),
        /// with their tokens.
        internal static Dictionary<(string Assembly, string Namespace, string Name), int> MissingTypeRefs(MetadataReader md) => MissingTypeRefs(md, out _);

        /// Those, and the TypeRefs ours cannot resolve where the original can (`gaps`).
        internal static Dictionary<(string Assembly, string Namespace, string Name), int> MissingTypeRefs(MetadataReader md,
            out Dictionary<(string Assembly, string Namespace, string Name), Gap> gaps)
        {
            var missing = new Dictionary<(string, string, string), int>();
            gaps = new Dictionary<(string, string, string), Gap>();
            foreach (var handle in md.TypeReferences)
            {
                var reference = md.GetTypeReference(handle);
                if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                {
                    // A nested type of an assembly ours answers for: a gap when ours lacks it
                    // (D58); missing from both, it is D32's.
                    if (MonoApiGaps.Name(md, handle) is ({ } scope, var nestedName) && GapOf(scope, nestedName) is (false, var why, var lost, var underlying))
                    {
                        var outer = nestedName.IndexOf('/');
                        var dot = nestedName.LastIndexOf('.', outer);
                        gaps.TryAdd((scope, dot < 0 ? "" : nestedName[..dot], nestedName[(dot + 1)..]), new Gap(MetadataTokens.GetToken(handle), why, lost, underlying));
                    }
                    continue;
                }
                if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
                var assemblyName = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                var key = (assemblyName, md.GetString(reference.Namespace), md.GetString(reference.Name));
                var token = MetadataTokens.GetToken(handle);
                if (IsMissing(key.assemblyName, key.Item2, key.Item3)) missing.TryAdd(key, token);
                else if (GapOf(key.assemblyName, key.Item2.Length == 0 ? key.Item3 : key.Item2 + "." + key.Item3) is var (both, failure, file, enumType))
                {
                    if (both) missing.TryAdd(key, token);
                    else gaps.TryAdd(key, new Gap(token, failure, file, enumType));
                }
            }
            return missing;
        }

        /// For a type of an assembly ours answers for in place of the original's copy (MonoApiGaps)
        /// that ours cannot resolve: whether the original's copy lacks it too (then it is missing,
        /// as D40's), else why ours cannot and the assembly it could not load. Null where ours
        /// resolves it, or the assembly is another.
        static (bool Both, MonoApiGaps.OursFailure Failure, string? File, PrimitiveTypeCode? Enum)? GapOf(string assemblyName, string fullName)
        {
            if (!MonoApiGaps.OursAnswer(assemblyName) || (Loaded(assemblyName) is { } assembly && SameInBothPlayers(assembly))) return null;
            var (type, failure, file) = MonoApiGaps.Ours(assemblyName, fullName.Replace('/', '+'));
            if (type != null) return null;
            if (MonoApiGaps.Original.Find(assemblyName, fullName) is not var (copy, handle)) return (true, failure, file, null);
            return (false, failure, file, copy.EnumUnderlying(handle));
        }

        static readonly ConcurrentDictionary<(string, string, string), bool> known = new();

        /// Whether the assembly loaded under that simple name (compared ignoring case, as Mono binds
        /// it: D15) lacks the type, when its types are the same in both players; for Unity's engine
        /// and packages, ours, whether both ours and the original's copy in Managed/ lack it (a type
        /// Unity's copy has and ours lacks is our API's gap: D58, GapOf).
        [NameLookupReviewed("Whether a type a mod names is in the assembly it names (any assembly)")]
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
            Dictionary<(string Assembly, string Namespace, string Name), Gap> gaps, Dictionary<int, MonoMissingMembers.Missing> members)
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
            var scan = new Scan(tokens, members, gaps);
            var failing = new List<(Cecil.MethodDefinition Method, string Message, string Type, Type Exception)>();
            var atCall = new List<(Cecil.MethodDefinition Method, List<(Cecil.Cil.Instruction At, Scan.GapUse Use)> Uses, List<(Cecil.Cil.VariableDefinition, Cecil.TypeReference)> Locals)>();
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    if (scan.Failure(method) is var (message, missing, exception)) failing.Add((method, message, missing, exception));
                    else if (gaps.Count + members.Count > 0 && scan.Gaps(method) is var (uses, locals)) atCall.Add((method, uses, locals));
                }
            if (failing.Count == 0 && atCall.Count == 0) return null;
            var constructors = new Dictionary<(Type, bool), Cecil.MethodReference>();
            // (message), or (message, fileName) for FileNotFoundException.
            Cecil.MethodReference Constructor(Type exception, bool withFile = false)
            {
                if (!constructors.TryGetValue((exception, withFile), out var constructor))
                    constructors[(exception, withFile)] = constructor =
                        module.ImportReference(exception.GetConstructor(withFile ? [typeof(string), typeof(string)] : [typeof(string)])!);
                return constructor;
            }
            var hidden = module.ImportReference(typeof(System.Diagnostics.StackTraceHiddenAttribute).GetConstructor(Type.EmptyTypes)!);
            foreach (var (method, message, _, exception) in failing)
                Throw(method, message, Constructor(exception), hidden);
            foreach (var (method, uses, locals) in atCall)
                ThrowAtUses(method, uses, locals, Constructor);
            using var output = new MemoryStream();
            using var symbolOutput = new MemoryStream();
            module.Write(output, new Cecil.WriterParameters { WriteSymbols = withSymbols, SymbolStream = withSymbols ? symbolOutput : null, SymbolWriterProvider = withSymbols ? new Cecil.Cil.PortablePdbWriterProvider() : null });
            symbols = withSymbols ? symbolOutput.ToArray() : null;
            if (failing.Count > 0)
            {
                var names = string.Join(", ", failing.Select(f => f.Type).Distinct().Take(8));
                EngineLog.Engine($"mod missing types: {Path.GetFileName(path)}: {failing.Count} method(s) name types or members their assemblies lack ({names}); they throw as Mono's compilation of them does");
            }
            if (atCall.Count > 0)
            {
                var names = string.Join(", ", atCall.SelectMany(a => a.Uses).Select(u => u.Use.Name).Distinct().Take(8));
                EngineLog.Engine($"mod api gaps: {Path.GetFileName(path)}: {atCall.Sum(a => a.Uses.Count)} reference(s) in {atCall.Count} method(s) name what the original's assemblies have and ours lack ({names}); they throw where they run");
            }
            return output.ToArray();
        }

        /// D58. Each instruction naming a gap throws where it runs: its operands are popped and
        /// it becomes `throw new E(message)` (prefixes before it become nop), and the method's
        /// locals of a gap type hold objects (an enum's, its underlying type). The rest of the
        /// method runs as it did, and a catch around the instruction sees the exception, as it sees
        /// Mono's from a call that fails.
        static void ThrowAtUses(Cecil.MethodDefinition method, List<(Cecil.Cil.Instruction At, Scan.GapUse Use)> uses, List<(Cecil.Cil.VariableDefinition Local, Cecil.TypeReference Type)> locals,
            Func<Type, bool, Cecil.MethodReference> constructor)
        {
            var body = method.Body;
            body.SimplifyMacros();
            foreach (var (local, type) in locals) local.VariableType = type;
            var il = body.GetILProcessor();
            foreach (var (at, use) in uses)
            {
                for (var prefix = at.Previous; prefix != null && prefix.OpCode.OpCodeType == Cecil.Cil.OpCodeType.Prefix; prefix = prefix.Previous)
                {
                    prefix.OpCode = Cecil.Cil.OpCodes.Nop;
                    prefix.Operand = null;
                }
                var sequence = new List<Cecil.Cil.Instruction>();
                for (int i = 0; i < use.Pops; i++) sequence.Add(il.Create(Cecil.Cil.OpCodes.Pop));
                sequence.Add(il.Create(Cecil.Cil.OpCodes.Ldstr, use.Message));
                if (use.FileName != null) sequence.Add(il.Create(Cecil.Cil.OpCodes.Ldstr, use.FileName));
                sequence.Add(il.Create(Cecil.Cil.OpCodes.Newobj, constructor(use.Exception, use.FileName != null)));
                sequence.Add(il.Create(Cecil.Cil.OpCodes.Throw));
                // The instruction itself becomes the first, so branches to it and the exception
                // clauses that start or end at it keep their place.
                at.OpCode = sequence[0].OpCode;
                at.Operand = sequence[0].Operand;
                var after = at;
                foreach (var next in sequence.Skip(1))
                {
                    il.InsertAfter(after, next);
                    after = next;
                }
            }
            body.OptimizeMacros();
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
        internal sealed class Scan(Dictionary<(string Assembly, string Namespace, string Name), int> tokens, Dictionary<int, MonoMissingMembers.Missing> members,
            Dictionary<(string Assembly, string Namespace, string Name), Gap>? gaps = null)
        {
            readonly Dictionary<(string Assembly, string Namespace, string Name), Gap> gaps = gaps ?? new();

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
                            if ((Missing(site.ReturnType) ?? MissingInParameters(site.Parameters)) is { } s)
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
                if (member is Cecil.IMemberDefinition || !members.TryGetValue(member.MetadataToken.ToInt32(), out var missing) || missing.Gap) return null;
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
            public Cecil.TypeReference? Missing(Cecil.TypeReference? type) => First(type, isToken ??= tokens.ContainsKey);

            /// The first type a type is built from that ours cannot resolve where the original can.
            public Cecil.TypeReference? GapIn(Cecil.TypeReference? type) => gaps.Count == 0 ? null : First(type, isGap ??= gaps.ContainsKey);

            // Made once: Missing and GapIn run for every type every instruction names, and a
            // method group made a delegate for each call.
            Func<(string, string, string), bool>? isToken, isGap;

            // The first parameter's Missing or GapIn that is not null, in order, as Select and
            // FirstOrDefault gave it, without their iterator and delegates.
            Cecil.TypeReference? MissingInParameters(Mono.Collections.Generic.Collection<Cecil.ParameterDefinition> parameters)
            {
                foreach (var p in parameters)
                    if (Missing(p.ParameterType) is { } t) return t;
                return null;
            }

            Cecil.TypeReference? GapInParameters(Mono.Collections.Generic.Collection<Cecil.ParameterDefinition> parameters)
            {
                foreach (var p in parameters)
                    if (GapIn(p.ParameterType) is { } t) return t;
                return null;
            }

            Cecil.TypeReference? GapInArguments(Mono.Collections.Generic.Collection<Cecil.TypeReference> arguments)
            {
                foreach (var a in arguments)
                    if (GapIn(a) is { } t) return t;
                return null;
            }

            static Cecil.TypeReference? First(Cecil.TypeReference? type, Func<(string, string, string), bool> named)
            {
                switch (type)
                {
                    case null or Cecil.GenericParameter or Cecil.TypeDefinition:
                        return null;
                    case Cecil.GenericInstanceType generic:
                        if (First(generic.ElementType, named) is { } element) return element;
                        foreach (var argument in generic.GenericArguments)
                            if (First(argument, named) is { } t) return t;
                        return null;
                    case Cecil.IModifierType modified:
                        return First(((Cecil.TypeSpecification)type).ElementType, named) ?? First(modified.ModifierType, named);
                    case Cecil.FunctionPointerType pointer:
                        if (First(pointer.ReturnType, named) is { } returned) return returned;
                        foreach (var parameter in pointer.Parameters)
                            if (First(parameter.ParameterType, named) is { } p) return p;
                        return null;
                    case Cecil.TypeSpecification specification:
                        return First(specification.ElementType, named);
                }
                // A nested type fails with its enclosing one; its own name is D32's, but for a gap.
                if (type.DeclaringType != null) return First(type.DeclaringType, named) ?? (Key(type) is { } nested && named(nested) ? type : null);
                return Key(type) is { } key && named(key) ? type : null;
            }

            /// (assembly, namespace, name) of a type reference, nested names after their outermost
            /// type's namespace as "Outer/Inner".
            static (string, string, string)? Key(Cecil.TypeReference type)
            {
                var name = type.Name;
                var outer = type;
                for (; outer.DeclaringType != null; outer = outer.DeclaringType) name = outer.DeclaringType.Name + "/" + name;
                return outer.Scope is Cecil.AssemblyNameReference assembly ? (assembly.Name, outer.Namespace, name) : null;
            }

            /// An instruction naming a gap (D58), what it throws, and how many values it takes
            /// from the stack.
            internal readonly record struct GapUse(string Name, string Message, Type Exception, string? FileName, int Pops);

            /// The instructions of a method Mono compiles that name a gap, and its locals of a gap
            /// type; null where it names none, or where ours cannot make it compile (a catch clause
            /// of a gap type, or its own signature naming one: CoreCLR fails it as a whole).
            public (List<(Cecil.Cil.Instruction At, GapUse Use)> Uses, List<(Cecil.Cil.VariableDefinition Local, Cecil.TypeReference Type)> Locals)? Gaps(Cecil.MethodDefinition method)
            {
                var body = method.Body;
                if (GapIn(method.ReturnType) != null || method.Parameters.Any(p => GapIn(p.ParameterType) != null)) return null;
                foreach (var handler in body.ExceptionHandlers)
                    if (GapIn(handler.CatchType) != null) return null;
                var ts = method.Module.TypeSystem;
                var locals = body.Variables.Where(v => GapIn(v.VariableType) != null)
                    .Select(v => (v, Key(v.VariableType) is { } key && gaps.TryGetValue(key, out var gap) && gap.Enum is { } code ? Primitive(ts, code) : ts.Object)).ToList();
                var uses = new List<(Cecil.Cil.Instruction, GapUse)>();
                foreach (var instruction in body.Instructions)
                {
                    if (GapAt(instruction) is not { } use) continue;
                    var at = instruction;
                    // constrained. names the type of the call after it: the call throws.
                    while (at.OpCode.OpCodeType == Cecil.Cil.OpCodeType.Prefix)
                    {
                        if (at.Next == null) return null;
                        at = at.Next;
                    }
                    if (uses.Count > 0 && uses[^1].Item1 == at) continue;
                    uses.Add((at, use with { Pops = Pops(at) }));
                }
                return uses.Count == 0 && locals.Count == 0 ? null : (uses, locals);
            }

            GapUse? GapAt(Cecil.Cil.Instruction instruction)
            {
                var code = instruction.OpCode.Code;
                switch (instruction.Operand)
                {
                    case Cecil.MethodReference called:
                        var arguments = called is Cecil.GenericInstanceMethod instance ? GapInArguments(instance.GenericArguments) : null;
                        var element = (called as Cecil.GenericInstanceMethod)?.ElementMethod ?? called;
                        if (element is Cecil.MethodDefinition) return arguments is { } own ? TypeGap(own, kept: true) : null;
                        var t = GapIn(element.DeclaringType) ?? GapIn(element.ReturnType)
                            ?? GapInParameters(element.Parameters) ?? arguments;
                        if (t != null) return TypeGap(t, kept: true);
                        if (members.TryGetValue(element.MetadataToken.ToInt32(), out var missing) && missing.Gap)
                            return new GapUse(element.DeclaringType.Name + "." + element.Name, missing.Message,
                                missing.Field ? typeof(MissingFieldException) : typeof(MissingMethodException), null, 0);
                        return null;
                    case Cecil.FieldReference field:
                        if ((field is Cecil.FieldDefinition ? GapIn(field.FieldType) : GapIn(field.DeclaringType) ?? GapIn(field.FieldType)) is { } f)
                            return TypeGap(f, kept: code == Cecil.Cil.Code.Ldtoken);
                        if (field is not Cecil.FieldDefinition && members.TryGetValue(field.MetadataToken.ToInt32(), out var absent) && absent.Gap)
                            return new GapUse(field.DeclaringType.Name + "." + field.Name, absent.Message, typeof(MissingFieldException), null, 0);
                        return null;
                    case Cecil.TypeReference type:
                        return GapIn(type) is { } g ? TypeGap(g, kept: code == Cecil.Cil.Code.Ldtoken || (code == Cecil.Cil.Code.Sizeof && type is Cecil.TypeSpecification)) : null;
                    case Cecil.CallSite site:
                        return (GapIn(site.ReturnType) ?? GapInParameters(site.Parameters)) is { } s ? TypeGap(s, kept: true) : null;
                }
                return null;
            }

            /// Mono's exception for a reference to a type that does not resolve, by where its JIT
            /// meets it (as in Failure): where it keeps the error, FileNotFoundException for an
            /// assembly that cannot be loaded (UnityProbe's atcall) or TypeLoadException naming the
            /// TypeRef; where it drops it, "Failure has occurred while loading a type.".
            GapUse TypeGap(Cecil.TypeReference type, bool kept)
            {
                var assembly = (Cecil.AssemblyNameReference)type.Scope;
                var key = Key(type)!.Value;
                var name = key.Item2.Length == 0 ? key.Item3 : key.Item2 + "." + key.Item3;
                var gap = gaps[key];
                if (!kept) return new GapUse(name, LostError, typeof(TypeLoadException), null, 0);
                if (gap.Failure == MonoApiGaps.OursFailure.Assembly && gap.Assembly is { } file)
                    return new GapUse(name, $"Could not load file or assembly '{file}' or one of its dependencies.", typeof(FileNotFoundException), file, 0);
                return new GapUse(name, $"Could not resolve type with token {gap.Token:x8} from typeref (expected class '{name}' in assembly '{MonoAssemblyName(assembly)}')",
                    typeof(TypeLoadException), null, 0);
            }

            static Cecil.TypeReference Primitive(Cecil.TypeSystem ts, PrimitiveTypeCode code) => code switch
            {
                PrimitiveTypeCode.Boolean => ts.Boolean,
                PrimitiveTypeCode.Char => ts.Char,
                PrimitiveTypeCode.SByte => ts.SByte,
                PrimitiveTypeCode.Byte => ts.Byte,
                PrimitiveTypeCode.Int16 => ts.Int16,
                PrimitiveTypeCode.UInt16 => ts.UInt16,
                PrimitiveTypeCode.UInt32 => ts.UInt32,
                PrimitiveTypeCode.Int64 => ts.Int64,
                PrimitiveTypeCode.UInt64 => ts.UInt64,
                PrimitiveTypeCode.IntPtr => ts.IntPtr,
                PrimitiveTypeCode.UIntPtr => ts.UIntPtr,
                _ => ts.Int32,
            };

            /// The values an instruction takes from the stack.
            static int Pops(Cecil.Cil.Instruction instruction)
            {
                var opcode = instruction.OpCode;
                switch (opcode.StackBehaviourPop)
                {
                    case Cecil.Cil.StackBehaviour.Pop0 or Cecil.Cil.StackBehaviour.PopAll:
                        return 0;
                    case Cecil.Cil.StackBehaviour.Varpop:
                        if (instruction.Operand is Cecil.CallSite site) return site.Parameters.Count + (site.HasThis && !site.ExplicitThis ? 1 : 0) + 1;
                        if (instruction.Operand is not Cecil.IMethodSignature method || opcode.Code == Cecil.Cil.Code.Jmp) return 0;
                        return method.Parameters.Count + (method.HasThis && !method.ExplicitThis && opcode.Code != Cecil.Cil.Code.Newobj ? 1 : 0);
                    case Cecil.Cil.StackBehaviour.Pop1 or Cecil.Cil.StackBehaviour.Popi or Cecil.Cil.StackBehaviour.Popref:
                        return 1;
                    case Cecil.Cil.StackBehaviour.Pop1_pop1 or Cecil.Cil.StackBehaviour.Popi_pop1 or Cecil.Cil.StackBehaviour.Popi_popi
                        or Cecil.Cil.StackBehaviour.Popi_popi8 or Cecil.Cil.StackBehaviour.Popi_popr4 or Cecil.Cil.StackBehaviour.Popi_popr8
                        or Cecil.Cil.StackBehaviour.Popref_pop1 or Cecil.Cil.StackBehaviour.Popref_popi:
                        return 2;
                    default:
                        return 3;
                }
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
