using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using HarmonyLib;
using Cecil = Mono.Cecil;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// ARCHITECTURE D11. Many mods are built against "publicized" copies of the game's
    /// assemblies and use the game's private members directly. Mono does not check member
    /// accessibility at run time; CoreCLR does, and throws FieldAccessException or
    /// MethodAccessException when such a mod's method is compiled (BlueprintsV2's tools reading
    /// DragTool's private fields, for example).
    ///
    /// The game loads mods' assemblies in KMod.DLLLoader.LoadDLLs with Assembly.LoadFrom; the host
    /// replaces that one call. A mod assembly that names a game member or type its code could not
    /// reach is given CoreCLR's IgnoresAccessChecksTo attribute for the game's assemblies and loaded
    /// from memory; the game's assemblies are untouched, so serialization and reflection over them
    /// stay as they were. The mod keeps behaving as one loaded from its file: its Location is its
    /// file's path (mods find their folder from it) and its folder is searched for the assemblies it
    /// references. Every other mod assembly loads as before.
    internal static class ModAccess
    {
        /// The game's assemblies whose non-public members mods use: its code, the Harmony it
        /// ships (mods built against a publicized 0Harmony read its private fields, as the
        /// Multiplayer Mod's loader reads PatchClassProcessor.containerAttributes) and TextMeshPro
        /// (Text Input Hotkeys calls TMP_InputField's private Backspace()).
        static readonly string[] gameAssemblies = ["Assembly-CSharp", "Assembly-CSharp-firstpass", "0Harmony", "Unity.TextMeshPro"];

        /// Rewritten mod assemblies and the files they were read from.
        static readonly ConcurrentDictionary<Assembly, string> locations = new();
        static readonly ConcurrentDictionary<string, Assembly> byPath = new(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> directories = new();

        static bool installed;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            // Before any mod's handler, as Unity's player registers its own first (D45).
            AppDomain.CurrentDomain.AssemblyResolve += UnityRedirect;
            var harmony = new Harmony("OniMoono.Host.ModAccess");
            var loadDlls = AccessTools.Method("KMod.DLLLoader:LoadDLLs")
                ?? throw new MissingMethodException("KMod.DLLLoader", "LoadDLLs");
            harmony.Patch(loadDlls, transpiler: new HarmonyMethod(typeof(ModAccess), nameof(Transpiler)));
            var location = AccessTools.PropertyGetter(Type.GetType("System.Reflection.RuntimeAssembly"), nameof(Assembly.Location))
                ?? throw new MissingMethodException("System.Reflection.RuntimeAssembly", "get_Location");
            harmony.Patch(location, postfix: new HarmonyMethod(typeof(ModAccess), nameof(LocationPostfix)));
            // A mod's own Assembly.LoadFrom of a file whose name is loaded gets what Mono gives (D45).
            harmony.Patch(typeof(Assembly).GetMethod(nameof(Assembly.LoadFrom), [typeof(string)])!,
                prefix: new HarmonyMethod(typeof(ModAccess), nameof(LoadFromPrefix)));
            AssemblyLoadContext.Default.Resolving += Resolve;
        }

        /// Set while ModAccess.LoadFrom loads a file it has placed already.
        [ThreadStatic] static bool placing;

        /// Assembly.LoadFrom(path) elsewhere than KMod's LoadDLLs (a mod loading a library itself):
        /// the assembly already loaded that Mono gives for the file, or the file loaded beside it.
        static bool LoadFromPrefix(string assemblyFile, ref Assembly __result)
        {
            if (placing || string.IsNullOrEmpty(assemblyFile)) return true;
            string path;
            try
            {
                path = Path.GetFullPath(assemblyFile);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return true;
            }
            if (!File.Exists(path)) return true;
            if (AlreadyLoaded(path, out bool beside) is { } existing) __result = existing;
            else if (beside) __result = Beside(path).LoadFromAssemblyPath(path);
            else return true;
            return false;
        }

        /// `Assembly.LoadFrom(path)` in LoadDLLs becomes `ModAccess.LoadFrom(path)`.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var loadFrom = typeof(Assembly).GetMethod(nameof(Assembly.LoadFrom), [typeof(string)])!;
            var replacement = typeof(ModAccess).GetMethod(nameof(LoadFrom), BindingFlags.Static | BindingFlags.NonPublic)!;
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(loadFrom))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException($"ModAccess: expected one Assembly.LoadFrom in LoadDLLs, found {replaced}");
        }

        /// A game assembly from Managed/ that AssemblyPolicy loaded from a rewritten image: its
        /// Location stays its file's, as on Mono. System Performance Observer hashes the files of
        /// Assembly-CSharp and Assembly-CSharp-firstpass and stops at load on an empty path.
        public static void RecordGameLocation(Assembly assembly, string path) => locations[assembly] = path;

        static void LocationPostfix(Assembly __instance, ref string __result)
        {
            if (__result.Length == 0 && locations.TryGetValue(__instance, out var path)) __result = path;
        }

        /// A mod's assembly, rewritten when it reaches into the game's non-public members, with its
        /// static readonly fields writable as Mono's are, with its references to nested types of the
        /// game's strings that the game no longer has bound as Mono binds them, with each method that
        /// names a type its assembly lacks throwing as Mono's compilation of it does, with a type its
        /// signatures tag as another kind than its own read as Mono reads it, with any class that
        /// misses a method of an interface it lists still loadable, with a patched method's stack
        /// frame reporting its owner type, with its string-keyed concurrent dictionaries in Mono's
        /// order, with a stack overflow in a method that can recurse thrown as an exception, with
        /// its conversions of floats to integers giving Mono's values, and with its directory
        /// listings in Mono's order, as on Mono.
        internal static Assembly LoadFrom(string path)
        {
            path = Path.GetFullPath(path);
            if (byPath.TryGetValue(path, out var loaded)) return loaded;
            if (AlreadyLoaded(path, out bool beside) is { } existing) return existing;
            var reason = Unreachable(path);
            byte[]? image, symbols = null;
            if (reason != null)
            {
                image = AddIgnoresAccessChecks(path, out symbols);
                // Its static readonly fields stay writable, as the game's are (MonoFieldSemantics).
                MonoFieldSemantics.Adjust(image, path);
                EngineLog.Engine($"mod access: {Path.GetFileName(path)} uses the game's non-public {reason}; loaded with access checks to the game's assemblies off");
            }
            else
            {
                // Mono lets reflection write a mod's static readonly fields too: the game's
                // LocString.CreateLocStringKeys writes back every LocString field of a mod's strings
                // class (PLib's PLocalization.Register), which mods often declare static readonly.
                image = MonoFieldSemantics.Adjust(path);
                var pdb = Path.ChangeExtension(path, ".pdb");
                if (File.Exists(pdb)) symbols = File.ReadAllBytes(pdb);
            }
            // An IL-only image marked 32-bit only loads, as on Mono (D50); the passes below read it so.
            if (Without32BitRequired(image ?? File.ReadAllBytes(path)) is { } anyCpu)
            {
                image = anyCpu;
                EngineLog.Engine($"mod image: {Path.GetFileName(path)} is IL only and marked to run in 32-bit processes only; loaded without that mark, as on Mono");
            }
            // Types nested in <Module> load, as on Mono (ModuleNestedTypes).
            if (ModuleNestedTypes.Adjust(image ?? File.ReadAllBytes(path), ref symbols, path) is { } unnested) image = unnested;
            // A nested type of the game's strings that the game no longer has is bound as Mono binds
            // it (MonoNestedTypeRefs).
            if (MonoNestedTypeRefs.Adjust(image ?? File.ReadAllBytes(path), ref symbols, path) is { } bound) image = bound;
            // A method naming a type its assembly lacks throws as Mono's compilation of it does
            // (MonoMissingTypes).
            if (MonoMissingTypes.Adjust(image ?? File.ReadAllBytes(path), ref symbols, path) is { } failing) image = failing;
            // A type a signature tags as another kind than its own is read as Mono reads it
            // (MonoTypeKinds), after MonoMissingTypes, which reads the tags too.
            if (MonoTypeKinds.Adjust(image ?? File.ReadAllBytes(path), ref symbols, path) is { } kinded) image = kinded;
            // A class missing a method of a game interface it lists loads, as on Mono
            // (MissingInterfaceMethods).
            if (MissingInterfaceMethods.Adjust(image ?? File.ReadAllBytes(path), ref symbols, path) is { } completed) image = completed;
            // A frame of a patched method reports its owner type, as on Mono (MonoStackFrames).
            if (MonoStackFrames.Adjust(image ?? File.ReadAllBytes(path), ref symbols, path) is { } rewritten) image = rewritten;
            // Its string-keyed concurrent dictionaries enumerate in Mono's order (MonoConcurrentDictionaries).
            if (MonoConcurrentDictionaries.Adjust(image ?? File.ReadAllBytes(path), ref symbols, path) is { } ordered) image = ordered;
            // A stack overflow in a method that can recurse throws, as on Mono (MonoStackOverflow).
            if (MonoStackOverflow.Adjust(image ?? File.ReadAllBytes(path), ref symbols, path) is { } probed) image = probed;
            // Its conversions of floats to integers give Mono's values out of range (MonoConversions),
            // and its directory listings come in Mono's order (MonoDirectoryListing, in that pass).
            if (MonoConversions.Adjust(image ?? File.ReadAllBytes(path), ref symbols, path) is { } converted) image = converted;
            // A strong-named file of another identity than the loaded assembly of its name loads
            // beside it, as in Mono (D45).
            var context = beside ? Beside(path) : AssemblyLoadContext.Default;
            if (image == null)
            {
                if (beside) return Recorded(context.LoadFromAssemblyPath(path));
                placing = true;
                try
                {
                    return Recorded(Assembly.LoadFrom(path));
                }
                finally
                {
                    placing = false;
                }
            }
            Assembly assembly;
            try
            {
                assembly = context.LoadFromStream(new MemoryStream(image), symbols == null ? null : new MemoryStream(symbols));
            }
            catch (BadImageFormatException) when (symbols != null)
            {
                // Symbols the runtime cannot read (a Windows PDB): the assembly loads without them.
                assembly = context.LoadFromStream(new MemoryStream(image));
            }
            locations[assembly] = path;
            byPath[path] = assembly;
            lock (directories)
                if (!directories.Contains(Path.GetDirectoryName(path)!)) directories.Add(Path.GetDirectoryName(path)!);
            return Recorded(assembly);
        }

        const CorFlags Required32Bit = CorFlags.Requires32Bit;

        /// D50. The image with its CLI header's 32BITREQUIRED flag cleared when it is IL only and
        /// has it, else null. Mono reads the flag only into the assembly name's processor
        /// architecture (mono_assembly_fill_assembly_name_full, metadata/assembly.c) and runs the
        /// image in a 64-bit process; CoreCLR refuses to load it there ("The assembly architecture
        /// is not compatible with the current process architecture"). An IL-only image runs the
        /// same either way; one with native code cannot be run here and is left as it is.
        internal static byte[]? Without32BitRequired(byte[] image)
        {
            int flagsOffset;
            try
            {
                using var pe = new PEReader(new MemoryStream(image, writable: false));
                var cor = pe.PEHeaders.CorHeader;
                if (cor == null || (cor.Flags & CorFlags.ILOnly) == 0 || (cor.Flags & Required32Bit) == 0) return null;
                // The COR20 header: cb, major and minor version, the metadata directory, then Flags.
                flagsOffset = pe.PEHeaders.CorHeaderStartOffset + 16;
            }
            catch (BadImageFormatException)
            {
                return null;
            }
            var copy = (byte[])image.Clone();
            var flags = (CorFlags)BitConverter.ToInt32(copy, flagsOffset) & ~Required32Bit;
            BitConverter.TryWriteBytes(copy.AsSpan(flagsOffset, 4), (int)flags);
            return copy;
        }

        /// A mod's assembly, once loaded: its types are the same in both players (MonoMissingTypes).
        static Assembly Recorded(Assembly assembly)
        {
            MonoMissingTypes.ModLoaded(assembly);
            return assembly;
        }

        /// What Mono gives for a load of this file when an assembly of its name is loaded (D15,
        /// D45; mono_domain_assembly_search compares names ignoring case): for a file without a
        /// strong name, the first assembly loaded under its simple name, whatever their versions
        /// (CoreCLR refuses a second version, and the game then drops the mod); for a strong-named
        /// file, the loaded assembly of its identity, else null with `beside` set when the default
        /// context holds another of its name (Mono loads the file beside it; CoreCLR refuses).
        static Assembly? AlreadyLoaded(string path, out bool beside)
        {
            beside = false;
            AssemblyName name;
            try
            {
                name = AssemblyName.GetAssemblyName(path);
            }
            catch (BadImageFormatException)
            {
                return null;
            }
            if (name.GetPublicKeyToken() is { Length: > 0 })
            {
                if (Identical(name) is { } same) return same;
                foreach (var a in AssemblyLoadContext.Default.Assemblies)
                    if (!a.IsDynamic && string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        beside = true;
                        EngineLog.Engine($"mod access: {path} is {name.FullName}; {a.GetName().FullName} is loaded, and this one loads beside it (as in Mono)");
                        break;
                    }
                return null;
            }
            if (Loaded(name.Name) is not { } assembly) return null;
            if (assembly.GetName().Version != name.Version)
                EngineLog.Engine($"mod access: {path} is {name.Name} {name.Version}; {assembly.GetName().Version}, already loaded, stands in for it (as in Mono)");
            return assembly;
        }

        /// A context of its own for a strong-named file whose name the default context holds in
        /// another identity; its references bind as the default context's do.
        static AssemblyLoadContext Beside(string path) => new("OniMoono mod copy: " + path);

        /// The assemblies loaded (not dynamic, not in a collectible context such as
        /// MissingInterfaceMethods' probe), in load order, as Mono's domain lists them.
        static IEnumerable<Assembly> LoadedAssemblies()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (!assembly.IsDynamic && AssemblyLoadContext.GetLoadContext(assembly) is { IsCollectible: false }) yield return assembly;
        }

        /// The first assembly loaded under this simple name (compared ignoring case, as Mono
        /// compares), or null.
        static Assembly? Loaded(string? simpleName)
        {
            foreach (var assembly in LoadedAssemblies())
                if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase)) return assembly;
            return null;
        }

        /// The first assembly loaded with this strong name, matched as Mono matches one
        /// (mono_assembly_names_equal_flags): the simple name ignoring case, the culture when both
        /// name one, the version unless either is 0.0.0.0, the public key token when both have one.
        static Assembly? Identical(AssemblyName wanted)
        {
            foreach (var assembly in LoadedAssemblies())
            {
                var name = assembly.GetName();
                if (!string.Equals(name.Name, wanted.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.CultureName != null && wanted.CultureName != null && name.CultureName != wanted.CultureName) continue;
                Version v = Normal(name.Version), w = Normal(wanted.Version);
                if (v != w && v != Zero && w != Zero) continue;
                if (name.GetPublicKeyToken() is { Length: > 0 } t && wanted.GetPublicKeyToken() is { Length: > 0 } u && !t.AsSpan().SequenceEqual(u)) continue;
                return assembly;
            }
            return null;
        }

        static readonly Version Zero = new(0, 0, 0, 0);

        static Version Normal(Version? v) => v == null ? Zero : new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        /// A reference CoreCLR could not bind binds as Mono binds it first, to an assembly already
        /// loaded: one without a strong name to the first loaded under its simple name whatever
        /// their versions (a mod built against PLib 4.22 when another mod's older PLib is the one
        /// loaded), a strong-named one to the first of its identity (another version loaded beside
        /// the first: AlreadyLoaded). Then the folders of rewritten mods are searched as
        /// Assembly.LoadFrom's would be. What is still unbound reaches AssemblyResolve, whose first
        /// handler is UnityRedirect.
        static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var loaded = name.GetPublicKeyToken() is { Length: > 0 } ? Identical(name) : Loaded(name.Name);
            if (loaded != null)
            {
                if (loaded.GetName().Version != name.Version)
                    EngineLog.Engine($"mod access: a reference to {name.Name} {name.Version} binds to {loaded.GetName().Version}, already loaded (as in Mono)");
                return loaded;
            }
            string[] dirs;
            lock (directories) dirs = directories.ToArray();
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate)) return LoadFrom(candidate);
            }
            return null;
        }

        /// The AssemblyResolve handler Unity's player registers before any mod's (D45). For a full
        /// name it loads the simple name, which in Mono gives a file of that name in the requesting
        /// assembly's folder or the first assembly loaded under it, whatever its version and key (a
        /// strong-named reference to another version than the one loaded binds there); failing
        /// that, Mono raises AssemblyResolve again with the simple name and no requesting assembly,
        /// so the handlers after it see the simple name first and then the full name.
        static Assembly? UnityRedirect(object? sender, ResolveEventArgs e)
        {
            AssemblyName name;
            try
            {
                name = new AssemblyName(e.Name);
            }
            catch (Exception ex) when (ex is ArgumentException or FileLoadException)
            {
                return null;
            }
            var simple = name.Name;
            if (string.IsNullOrEmpty(simple) || simple == e.Name) return null;
            // CoreCLR raises no Resolving event when the default context holds the name in
            // another version: the loaded assembly of that identity (beside the first) comes
            // first, as in Mono's search.
            if (name.GetPublicKeyToken() is { Length: > 0 } && Identical(name) is { } identical) return identical;
            // Mono looks in a mod assembly's folder before it raises the event (the game's and the
            // host's folders are AssemblyPolicy's, which refuses some names there).
            string? folder = null;
            try
            {
                if (e.RequestingAssembly is { IsDynamic: false } requesting && requesting.Location is { Length: > 0 } location) folder = Path.GetDirectoryName(location);
            }
            catch (NotSupportedException)
            {
            }
            if (folder != null && !AssemblyPolicy.IsPolicyDirectory(folder) && Path.Combine(folder, simple + ".dll") is var file && File.Exists(file))
            {
                try
                {
                    return LoadFrom(file);
                }
                catch (Exception ex) when (ex is FileLoadException or BadImageFormatException)
                {
                }
            }
            if (Loaded(simple) is { } loaded)
            {
                EngineLog.Engine($"mod access: a reference to {e.Name} binds to {loaded.GetName().FullName}, loaded under its simple name (as in Unity's player)");
                return loaded;
            }
            return raiseResolve?.Invoke(null, [null, simple]) as Assembly;
        }

        /// AssemblyLoadContext.OnAssemblyResolve(RuntimeAssembly?, string): raises AppDomain's
        /// AssemblyResolve as the runtime does.
        static readonly MethodInfo? raiseResolve = typeof(AssemblyLoadContext).GetMethod("OnAssemblyResolve", BindingFlags.NonPublic | BindingFlags.Static);

        /// The first game member or type the assembly references that is not public, or null.
        static string? Unreachable(string path)
        {
            found = new Dictionary<string, Assembly>();
            try
            {
                using var pe = new PEReader(File.OpenRead(path));
                if (!pe.HasMetadata) return null;
                var md = pe.GetMetadataReader();
                HashSet<Type>? subclassed = null;
                foreach (var handle in md.TypeReferences)
                    if (GameType(md, handle) is { } type && !type.IsVisible) return type.FullName;
                foreach (var handle in md.MemberReferences)
                {
                    var member = md.GetMemberReference(handle);
                    var parent = member.Parent.Kind switch
                    {
                        HandleKind.TypeReference => GameType(md, (TypeReferenceHandle)member.Parent),
                        HandleKind.TypeSpecification => GameType(md, GenericDefinition(md, (TypeSpecificationHandle)member.Parent)),
                        _ => null,
                    };
                    if (parent == null) continue;
                    var name = md.GetString(member.Name);
                    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                    // A method is matched by its parameter count (overloads of one name can differ in access).
                    int parameters = member.GetKind() == MemberReferenceKind.Method ? ParameterCount(md, member.Signature) : -1;
                    foreach (var candidate in parent.GetMember(name, all))
                    {
                        if (parameters >= 0 && candidate is MethodBase m && m.GetParameters().Length != parameters) continue;
                        if (!IsReachable(candidate, subclassed ??= Subclassed(md))) return $"{parent.FullName}.{name}";
                    }
                }
            }
            catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                found = null;
            }
            return null;
        }

        static int ParameterCount(MetadataReader md, BlobHandle signature)
        {
            var blob = md.GetBlobReader(signature);
            var header = blob.ReadSignatureHeader();
            if (header.IsGeneric) blob.ReadCompressedInteger();
            return blob.ReadCompressedInteger();
        }

        /// Public members, and protected ones of a game class the mod derives from (a mod's
        /// subclass reaches them as any subclass does). A protected member of a class the mod does
        /// not derive from is unreachable: CoreCLR throws MethodAccessException or
        /// FieldAccessException where Mono runs it (Public Cots reads the protected
        /// StateMachine.GenericInstance.smi from a postfix).
        static bool IsReachable(MemberInfo member, HashSet<Type> subclassed) => member switch
        {
            FieldInfo f => f.IsPublic || (f.IsFamily || f.IsFamilyOrAssembly) && Derives(f.DeclaringType, subclassed),
            MethodBase m => m.IsPublic || (m.IsFamily || m.IsFamilyOrAssembly) && Derives(m.DeclaringType, subclassed),
            _ => true,
        };

        static bool Derives(Type? declaring, HashSet<Type> subclassed) =>
            declaring != null && subclassed.Contains(declaring.IsGenericType ? declaring.GetGenericTypeDefinition() : declaring);

        /// The game classes the mod's classes derive from, directly or through others (generic
        /// classes as their definitions).
        static HashSet<Type> Subclassed(MetadataReader md)
        {
            var result = new HashSet<Type>();
            foreach (var handle in md.TypeDefinitions)
            {
                var definition = md.GetTypeDefinition(handle);
                // A base among the mod's own classes leads, through its own base, to the game's.
                for (int depth = 0; depth < 64 && !definition.BaseType.IsNil && definition.BaseType.Kind == HandleKind.TypeDefinition; depth++)
                    definition = md.GetTypeDefinition((TypeDefinitionHandle)definition.BaseType);
                if (definition.BaseType.IsNil) continue;
                var game = definition.BaseType.Kind switch
                {
                    HandleKind.TypeReference => GameType(md, (TypeReferenceHandle)definition.BaseType),
                    HandleKind.TypeSpecification => GameType(md, GenericDefinition(md, (TypeSpecificationHandle)definition.BaseType)),
                    _ => null,
                };
                for (var t = game; t != null; t = t.BaseType)
                    if (!result.Add(t.IsGenericType ? t.GetGenericTypeDefinition() : t)) break;
            }
            return result;
        }

        /// The game assemblies GameType found during one Unreachable, by simple name: it runs for
        /// each of a mod's type references, and finding one asked every loaded assembly for its
        /// name (an AssemblyName each). Only found ones are kept: the first loaded assembly of a
        /// name stays the first, one not loaded yet may load later.
        [ThreadStatic] static Dictionary<string, Assembly>? found;

        /// The first loaded assembly of the simple name, as AppDomain lists them.
        static Assembly? FirstLoaded(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name) return a;
            return null;
        }

        /// A type reference into a loaded game assembly, or null.
        static Type? GameType(MetadataReader md, TypeReferenceHandle handle)
        {
            if (handle.IsNil) return null;
            var t = md.GetTypeReference(handle);
            var name = md.GetString(t.Name);
            if (t.ResolutionScope.Kind == HandleKind.TypeReference)
                return GameType(md, (TypeReferenceHandle)t.ResolutionScope)?.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
            if (t.ResolutionScope.Kind != HandleKind.AssemblyReference) return null;
            var assemblyName = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope).Name);
            if (Array.IndexOf(gameAssemblies, assemblyName) < 0) return null;
            Assembly? assembly = null;
            if (found == null || !found.TryGetValue(assemblyName, out assembly))
            {
                assembly = FirstLoaded(assemblyName);
                if (assembly != null && found != null) found[assemblyName] = assembly;
            }
            var ns = md.GetString(t.Namespace);
            return assembly?.GetType(ns.Length == 0 ? name : ns + "." + name, throwOnError: false);
        }

        /// The generic type a member of a generic instantiation belongs to (GENERICINST signatures).
        static TypeReferenceHandle GenericDefinition(MetadataReader md, TypeSpecificationHandle handle)
        {
            var blob = md.GetBlobReader(md.GetTypeSpecification(handle).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return default;
            blob.ReadByte(); // CLASS or VALUETYPE
            var definition = blob.ReadTypeHandle();
            return definition.Kind == HandleKind.TypeReference ? (TypeReferenceHandle)definition : default;
        }

        /// Writing an assembly can need its references (attribute arguments of their enum types):
        /// the mod's folder, the game's assemblies and our engine facades.
        static Cecil.DefaultAssemblyResolver Resolver(string path)
        {
            var resolver = new Cecil.DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
            foreach (var dir in AssemblyPolicy.ReferenceDirectories) resolver.AddSearchDirectory(dir);
            return resolver;
        }

        /// The assembly with [IgnoresAccessChecksTo] for each game assembly (the attribute type
        /// defined in it when it has none; CoreCLR recognizes it by name), and its symbols.
        static byte[] AddIgnoresAccessChecks(string path, out byte[]? symbols)
        {
            var pdb = Path.ChangeExtension(path, ".pdb");
            bool withSymbols = File.Exists(pdb);
            using var resolver = Resolver(path);
            Cecil.ModuleDefinition module;
            try
            {
                module = Cecil.ModuleDefinition.ReadModule(path, new Cecil.ReaderParameters { ReadSymbols = withSymbols, InMemory = true, AssemblyResolver = resolver });
            }
            catch (Exception e) when (withSymbols && e is Cecil.Cil.SymbolsNotMatchingException or Cecil.Cil.SymbolsNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                // Symbols the reader cannot use (a Windows PDB, or stale): the assembly without them.
                withSymbols = false;
                module = Cecil.ModuleDefinition.ReadModule(path, new Cecil.ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            }
            using var owned = module;
            const string ns = "System.Runtime.CompilerServices", name = "IgnoresAccessChecksToAttribute";
            var attribute = module.Types.FirstOrDefault(t => t.Namespace == ns && t.Name == name);
            if (attribute == null)
            {
                attribute = new Cecil.TypeDefinition(ns, name, Mono.Cecil.TypeAttributes.NotPublic | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                    module.ImportReference(typeof(Attribute)));
                var baseCtor = module.ImportReference(typeof(Attribute).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!);
                var ctor = new Cecil.MethodDefinition(".ctor", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                    module.TypeSystem.Void);
                ctor.Parameters.Add(new Cecil.ParameterDefinition("assemblyName", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.String));
                var il = ctor.Body.GetILProcessor();
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                il.Emit(Mono.Cecil.Cil.OpCodes.Call, baseCtor);
                il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                attribute.Methods.Add(ctor);
                module.Types.Add(attribute);
            }
            var constructor = attribute.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String");
            var existing = new HashSet<string>(module.Assembly.CustomAttributes
                .Where(a => a.AttributeType.FullName == ns + "." + name && a.ConstructorArguments.Count == 1)
                .Select(a => (string)a.ConstructorArguments[0].Value), StringComparer.Ordinal);
            foreach (var game in gameAssemblies)
            {
                if (existing.Contains(game)) continue;
                var applied = new Cecil.CustomAttribute(constructor);
                applied.ConstructorArguments.Add(new Cecil.CustomAttributeArgument(module.TypeSystem.String, game));
                module.Assembly.CustomAttributes.Add(applied);
            }
            using var image = new MemoryStream();
            using var symbolStream = new MemoryStream();
            module.Write(image, new Cecil.WriterParameters { WriteSymbols = withSymbols, SymbolStream = withSymbols ? symbolStream : null, SymbolWriterProvider = withSymbols ? new Cecil.Cil.PortablePdbWriterProvider() : null });
            symbols = withSymbols ? symbolStream.ToArray() : null;
            return image.ToArray();
        }
    }
}
