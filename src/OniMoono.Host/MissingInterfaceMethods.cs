using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using OniMoono.Engine;
using Cecil = Mono.Cecil;

namespace OniMoono.Host
{
    /// ARCHITECTURE D22. A mod built against an older game can have a class that no longer
    /// implements every method of a game interface it lists (the game changed the method's
    /// signature). Mono loads such a class and fails only when it is used through the interface
    /// (as measured in the original player: "VTable setup of type … failed" from an IsAssignableFrom
    /// with an interface, while KMod's Assembly.GetTypes and its UserMod2 check pass). CoreCLR
    /// refuses the class when it loads, so GetTypes throws and KMod drops the whole mod (Wireless
    /// Power, whose side screen lacks the new ISliderControl.GetSliderTooltip). Before a mod's
    /// assembly is loaded, the host loads it once more in a collectible context that sees only the
    /// assemblies already loaded, collects the methods CoreCLR finds missing, and gives each such
    /// class an explicit implementation that throws the TypeLoadException CoreCLR would have: the
    /// class loads as on Mono, and using the missing method fails.
    internal static class MissingInterfaceMethods
    {
        static readonly Regex Missing = new(@"^Method '(?<method>[^']+)' in type '(?<type>[^']+)' from assembly '[^']*' does not have an implementation\.$", RegexOptions.CultureInvariant);

        /// The image completed with the missing methods, or null when nothing is missing.
        /// `symbols` (portable PDB bytes or null) are rewritten to match.
        public static byte[]? Adjust(byte[] image, ref byte[]? symbols, string path)
        {
            // CoreCLR names one missing method of a class at a time (Trashcans' misses two of
            // ISidescreenButtonControl's): the completed image is probed again until it loads.
            byte[]? completed = null;
            for (int round = 0; round < 64; round++)
            {
                var missing = Verdict(completed ?? image);
                if (missing.Count == 0) break;
                var (next, newSymbols, added) = Complete(completed ?? image, symbols, path, missing);
                if (added.Count == 0) break;
                completed = next;
                symbols = newSymbols;
                foreach (var line in added)
                    EngineLog.Engine($"mod types: {Path.GetFileName(path)}: {line} (missing; calling it throws, as using the class does on Mono)");
            }
            return completed;
        }

        /// Probes run and verdicts read from the cache in this process (the self-check).
        internal static int ProbesRun, CacheHits;

        /// The cache's folder instead of the player's cache folder (the self-check), or null.
        internal static string? CacheFolder;

        /// What Probe finds for the image, from the cache when the same image was probed against
        /// the same loaded assemblies before. A probe costs a few blocking full collections
        /// (Collect: two per probe, 80 with 40 mods at each start, measured); its verdict is a
        /// function of the image, the assemblies its probe can bind to and what those bind to, the
        /// host's rewriting of them and the runtime, so the cache key names all of them: the
        /// image's hash, the runtime's and this host's builds, and each loaded assembly its
        /// references reach, outside the runtime's own folder, by name, module version id and its
        /// file's size and time.
        static Dictionary<string, (string Message, HashSet<string> Methods)> Verdict(byte[] image)
        {
            var file = CacheFile(image);
            if (file != null && Read(file) is { } cached)
            {
                CacheHits++;
                return cached;
            }
            ProbesRun++;
            var missing = Probe(image, out var probe);
            Collect(probe);
            if (file != null) Write(file, missing);
            return missing;
        }

        /// The verdict's cache file, or null when the probe's inputs cannot all be named: the image
        /// references an assembly whose loaded copy is dynamic (no file or module version id
        /// stands for its contents), or its references cannot be read.
        static string? CacheFile(byte[] image)
        {
            var folder = CacheFolder ?? (EngineConfig.TemporaryCachePath.Length == 0 ? null : Path.Combine(EngineConfig.TemporaryCachePath, "oni-moono", "mod-type-probes"));
            if (folder == null) return null;
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(image, writable: false));
                if (!pe.HasMetadata) return null;
                var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                foreach (var handle in metadata.AssemblyReferences)
                    references.Add(metadata.GetString(metadata.GetAssemblyReference(handle).Name));
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
            {
                return null;
            }
            // The loaded assemblies by simple name: ProbeContext binds the image's references by
            // name to the default context's, and those bind theirs by name too (ModAccess.Resolve
            // binds to an assembly loaded in any context, as Mono does), so what the probe can
            // reach is the closure of the names referenced from the image. Assemblies outside it
            // (such as Harmony's, generated at each start) play no part.
            var byName = new Dictionary<string, List<(AssemblyLoadContext Context, Assembly Assembly)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var context in AssemblyLoadContext.All)
            {
                if (context.IsCollectible) continue;
                foreach (var assembly in context.Assemblies)
                {
                    var name = assembly.GetName().Name ?? "";
                    if (!byName.TryGetValue(name, out var list)) byName[name] = list = new();
                    list.Add((context, assembly));
                }
            }
            var lines = new List<string>();
            var pending = new Queue<string>(references);
            while (pending.Count > 0)
            {
                if (!byName.Remove(pending.Dequeue(), out var loaded)) continue;
                foreach (var (context, assembly) in loaded)
                {
                    // A dynamic assembly has neither a file nor a module version id that stands for its contents.
                    if (assembly.IsDynamic) return null;
                    if (Identity(assembly) is not { } line) continue;
                    lines.Add(context.Name + "|" + line);
                    foreach (var reference in ReferencedNames(assembly))
                        if (byName.ContainsKey(reference)) pending.Enqueue(reference);
                }
            }
            // This host's build: it rewrites the game's and the mods' images as they load.
            lines.Add("host|" + Identity(typeof(MissingInterfaceMethods).Assembly));
            lines.Sort(StringComparer.Ordinal);
            var key = new System.Text.StringBuilder();
            key.Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image))).Append('\n');
            key.Append(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription).Append('|')
               .Append(typeof(object).Module.ModuleVersionId.ToString("N")).Append('|')
               .Append(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture).Append('\n');
            foreach (var line in lines) key.Append(line).Append('\n');
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToString())));
            return Path.Combine(folder, hash + ".json");
        }

        static readonly Dictionary<Assembly, string?> identities = new();
        static readonly Dictionary<Assembly, string[]> referencedNames = new();
        static string? runtimeFolder;

        static string[] ReferencedNames(Assembly assembly)
        {
            lock (referencedNames)
            {
                if (!referencedNames.TryGetValue(assembly, out var names))
                    referencedNames[assembly] = names = assembly.GetReferencedAssemblies().Select(n => n.Name ?? "").ToArray();
                return names;
            }
        }

        /// An assembly as the cache key names it, or null for one of the runtime's own (from its
        /// folder: the runtime's build stands for them, whether loaded yet or not).
        static string? Identity(Assembly assembly)
        {
            lock (identities)
            {
                if (identities.TryGetValue(assembly, out var known)) return known;
                runtimeFolder ??= Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "";
                // Location is the file's for a rewritten mod or game assembly too (ModAccess).
                var location = assembly.Location;
                string? line;
                var name = assembly.GetName();
                if (location.Length > 0 && runtimeFolder.Length > 0 && string.Equals(Path.GetDirectoryName(location), runtimeFolder, StringComparison.Ordinal)
                    && name.GetPublicKeyToken() is { Length: > 0 } token && MicrosoftTokens.Contains(Convert.ToHexString(token)))
                    line = null;
                else
                {
                    line = name.FullName + "|" + assembly.ManifestModule.ModuleVersionId.ToString("N");
                    if (location.Length > 0)
                    {
                        try
                        {
                            var info = new FileInfo(location);
                            line += "|" + location + "|" + (info.Exists ? info.Length + "|" + info.LastWriteTimeUtc.Ticks : "missing");
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            line += "|" + location;
                        }
                    }
                }
                identities[assembly] = line;
                return line;
            }
        }

        /// The public key tokens of the runtime's own assemblies.
        static readonly HashSet<string> MicrosoftTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "B77A5C561934E089", "B03F5F7F11D50A3A", "CC7B13FFCD2DDD51", "7CEC85D7BEA7798E", "ADB9793829DDAE60",
        };

        /// The self-check: the image's cached verdict and a fresh probe's, as text (equal when the
        /// cache holds what the probe finds now); null for the cached one when it is not cached.
        internal static (string? Cached, string Probed) CompareWithProbe(byte[] image)
        {
            static string Text(Dictionary<string, (string Message, HashSet<string> Methods)> v)
                => string.Join("\n", v.Select(e => e.Key + ": " + e.Value.Message + " [" + string.Join(", ", e.Value.Methods.Order(StringComparer.Ordinal)) + "]"));
            var file = CacheFile(image);
            var cached = file == null ? null : Read(file);
            var probed = Probe(image, out var probe);
            Collect(probe);
            return (cached == null ? null : Text(cached), Text(probed));
        }

        sealed record CachedType(string Type, string Message, string[] Methods);

        static Dictionary<string, (string Message, HashSet<string> Methods)>? Read(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var types = System.Text.Json.JsonSerializer.Deserialize<CachedType[]>(File.ReadAllBytes(file));
                if (types == null) return null;
                var result = new Dictionary<string, (string Message, HashSet<string> Methods)>(StringComparer.Ordinal);
                foreach (var t in types)
                {
                    if (t?.Type == null || t.Message == null || t.Methods == null || result.ContainsKey(t.Type)) return null;
                    result[t.Type] = (t.Message, new HashSet<string>(t.Methods, StringComparer.Ordinal));
                }
                return result;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                return null;
            }
        }

        /// Kept in the probe's order (Complete adds the methods type by type in it).
        static void Write(string file, Dictionary<string, (string Message, HashSet<string> Methods)> missing)
        {
            try
            {
                var types = missing.Select(e => new CachedType(e.Key, e.Value.Message, e.Value.Methods.Order(StringComparer.Ordinal).ToArray())).ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temporary = file + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(temporary, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(types));
                File.Move(temporary, file, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not kept: the next start probes it again.
            }
        }

        /// Type full name (CLR form, nested with '+') → the method names CoreCLR finds missing.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static Dictionary<string, (string Message, HashSet<string> Methods)> Probe(byte[] image, out WeakReference probe)
        {
            var result = new Dictionary<string, (string Message, HashSet<string> Methods)>(StringComparer.Ordinal);
            var context = new ProbeContext();
            probe = new WeakReference(context);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                try
                {
                    assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    foreach (var loader in e.LoaderExceptions)
                    {
                        if (loader is not TypeLoadException t || Missing.Match(t.Message) is not { Success: true } m) continue;
                        var type = m.Groups["type"].Value;
                        if (!result.TryGetValue(type, out var entry)) result[type] = entry = (t.Message, new HashSet<string>(StringComparer.Ordinal));
                        entry.Methods.Add(m.Groups["method"].Value);
                    }
                }
            }
            catch (BadImageFormatException)
            {
                // Not an assembly the probe can load: the real load reports it.
            }
            finally
            {
                context.Unload();
            }
            return result;
        }

        /// The probe's copy of the assembly is gone before the real one loads: until its context is
        /// collected, AppDomain.GetAssemblies lists it too, and a mod library that picks its
        /// instance from there (PLib) would run the probe's copy, whose Location is empty.
        static void Collect(WeakReference probe)
        {
            for (int i = 0; probe.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            if (probe.IsAlive) EngineLog.Engine("mod types: a probe's load context was not collected; its copy of the mod stays listed");
        }

        /// Binds a probed mod's references only to assemblies already loaded, so probing loads
        /// nothing else (an unresolved reference fails only the probe's types that need it).
        sealed class ProbeContext() : AssemblyLoadContext("OniMoono mod type probe", isCollectible: true)
        {
            protected override Assembly? Load(AssemblyName name)
            {
                foreach (var a in Default.Assemblies)
                    if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)) return a;
                throw new FileNotFoundException($"{name.Name} is not loaded yet (type probe)");
            }
        }

        static (byte[] Image, byte[]? Symbols, List<string> Added) Complete(byte[] image, byte[]? symbols, string path,
            Dictionary<string, (string Message, HashSet<string> Methods)> missing)
        {
            using var resolver = LoadedFirstResolver.ForMod(path);
            Cecil.ModuleDefinition module;
            bool withSymbols = symbols != null;
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
            using var ownedModule = module;
            var added = new List<string>();
            var exceptionCtor = module.ImportReference(typeof(TypeLoadException).GetConstructor([typeof(string)])!);
            foreach (var (typeName, (message, methods)) in missing)
            {
                var type = module.GetType(typeName.Replace('+', '/'));
                if (type == null) continue;
                foreach (var (interfaceType, method) in InterfaceMethods(type))
                {
                    if (!methods.Contains(method.Name) || method.HasGenericParameters) continue;
                    if (Implemented(type, interfaceType, method)) continue;
                    // Built in the mod's module (ImportReference keeps a reference whose declaring
                    // type is the module's own as it is): its types, the game's, are imported one by one.
                    var reference = new Cecil.MethodReference(method.Name, Import(module, method.ReturnType), Import(module, interfaceType)) { HasThis = true };
                    foreach (var p in method.Parameters) reference.Parameters.Add(new Cecil.ParameterDefinition(Import(module, p.ParameterType)));
                    var stub = new Cecil.MethodDefinition($"{interfaceType.FullName}.{method.Name}",
                        Cecil.MethodAttributes.Private | Cecil.MethodAttributes.Final | Cecil.MethodAttributes.Virtual | Cecil.MethodAttributes.HideBySig | Cecil.MethodAttributes.NewSlot,
                        module.ImportReference(Substitute(method.ReturnType, interfaceType)));
                    foreach (var p in method.Parameters)
                        stub.Parameters.Add(new Cecil.ParameterDefinition(p.Name, p.Attributes, module.ImportReference(Substitute(p.ParameterType, interfaceType))));
                    stub.Overrides.Add(reference);
                    var il = stub.Body.GetILProcessor();
                    il.Emit(Cecil.Cil.OpCodes.Ldstr, message);
                    il.Emit(Cecil.Cil.OpCodes.Newobj, exceptionCtor);
                    il.Emit(Cecil.Cil.OpCodes.Throw);
                    type.Methods.Add(stub);
                    added.Add($"{type.FullName} gets {interfaceType.FullName}.{method.Name}");
                }
            }
            using var output = new MemoryStream();
            using var symbolOutput = new MemoryStream();
            module.Write(output, new Cecil.WriterParameters { WriteSymbols = withSymbols, SymbolStream = withSymbols ? symbolOutput : null, SymbolWriterProvider = withSymbols ? new Cecil.Cil.PortablePdbWriterProvider() : null });
            return (output.ToArray(), withSymbols ? symbolOutput.ToArray() : null, added);
        }

        /// A type from another module as the module references it; the interface's own generic
        /// parameters stay as they are (a signature names them by position).
        static Cecil.TypeReference Import(Cecil.ModuleDefinition module, Cecil.TypeReference type) => type switch
        {
            Cecil.GenericParameter => type,
            Cecil.ByReferenceType r => new Cecil.ByReferenceType(Import(module, r.ElementType)),
            Cecil.ArrayType a => new Cecil.ArrayType(Import(module, a.ElementType), a.Rank),
            Cecil.GenericInstanceType gi => ImportInstance(module, gi),
            _ => module.ImportReference(type),
        };

        static Cecil.GenericInstanceType ImportInstance(Cecil.ModuleDefinition module, Cecil.GenericInstanceType gi)
        {
            var result = new Cecil.GenericInstanceType(module.ImportReference(gi.ElementType));
            foreach (var a in gi.GenericArguments) result.GenericArguments.Add(Import(module, a));
            return result;
        }

        /// Every interface the type lists, directly, through its base classes or through other
        /// interfaces, with each of its methods.
        static IEnumerable<(Cecil.TypeReference Interface, Cecil.MethodDefinition Method)> InterfaceMethods(Cecil.TypeDefinition type)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<Cecil.TypeReference>();
            for (var t = type; t != null; t = t.BaseType?.Resolve())
                foreach (var i in t.Interfaces) pending.Push(i.InterfaceType);
            while (pending.Count > 0)
            {
                var reference = pending.Pop();
                if (!seen.Add(reference.FullName)) continue;
                var definition = reference.Resolve();
                if (definition == null) continue;
                foreach (var i in definition.Interfaces) pending.Push(i.InterfaceType);
                foreach (var m in definition.Methods)
                    if (m.IsAbstract && !m.IsStatic) yield return (reference, m);
            }
        }

        /// A public virtual method of the same name and signature in the type or a base class,
        /// or an explicit implementation of this method.
        static bool Implemented(Cecil.TypeDefinition type, Cecil.TypeReference interfaceType, Cecil.MethodDefinition method)
        {
            var signature = Signature(method, interfaceType);
            for (var t = type; t != null; t = t.BaseType?.Resolve())
                foreach (var m in t.Methods)
                {
                    if (m.Overrides.Any(o => o.Name == method.Name && o.DeclaringType.FullName == interfaceType.FullName)) return true;
                    if (m.Name == method.Name && m.IsVirtual && m.IsPublic && Signature(m, null) == signature) return true;
                }
            return false;
        }

        static string Signature(Cecil.MethodDefinition m, Cecil.TypeReference? interfaceType)
            => Substitute(m.ReturnType, interfaceType).FullName + "(" + string.Join(",", m.Parameters.Select(p => Substitute(p.ParameterType, interfaceType).FullName)) + ")";

        /// An interface method's type with the interface's generic arguments put in.
        static Cecil.TypeReference Substitute(Cecil.TypeReference type, Cecil.TypeReference? interfaceType)
        {
            if (interfaceType is not Cecil.GenericInstanceType instance) return type;
            return type switch
            {
                Cecil.GenericParameter { Type: Cecil.GenericParameterType.Type } g => instance.GenericArguments[g.Position],
                Cecil.ByReferenceType r => new Cecil.ByReferenceType(Substitute(r.ElementType, interfaceType)),
                Cecil.ArrayType a => new Cecil.ArrayType(Substitute(a.ElementType, interfaceType), a.Rank),
                Cecil.GenericInstanceType gi => Instance(gi, interfaceType),
                _ => type,
            };
        }

        static Cecil.GenericInstanceType Instance(Cecil.GenericInstanceType gi, Cecil.TypeReference interfaceType)
        {
            var result = new Cecil.GenericInstanceType(gi.ElementType);
            foreach (var a in gi.GenericArguments) result.GenericArguments.Add(Substitute(a, interfaceType));
            return result;
        }
    }
}
