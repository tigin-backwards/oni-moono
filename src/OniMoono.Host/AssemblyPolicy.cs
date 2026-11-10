using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using OniMoono.Engine;
using OniMoono.Engine.Platform;

namespace OniMoono.Host
{
    /// Where every assembly the game asks for comes from (ARCHITECTURE §2):
    ///
    /// 1. The .NET framework (the trusted platform assemblies) answers first, so Mono's BCL copies
    ///    in Managed/ (mscorlib, System.*, Mono.Security, I18N…) are never loaded.
    /// 2. Unity engine assemblies (UnityEngine, UnityEngine.*Module) resolve to our facades in
    ///    engine/, which forward to OniMoono.UnityEngine; the engine bridge ships with the host.
    /// 3. Unity's packages (UnityEngine.UI, TextMeshPro, Timeline) are this project's replacements
    ///    (PLAN M9), from packages/ next to the host, never Unity's from Managed/.
    /// 4. Everything else — the game and its third-party libraries — loads from Managed/.
    ///
    /// The game runs in the default load context, so its Type.GetType, Assembly.Load and
    /// AppDomain queries behave as they did under Unity.
    internal static class AssemblyPolicy
    {
        /// Unity's own support packages listed in ScriptingAssemblies.json. They implement engine
        /// features (Burst, native collections, profiler integration) that our engine replaces, and
        /// no game assembly references them, so they are not loaded.
        static readonly HashSet<string> unitySupportPackages = new(StringComparer.Ordinal)
        {
            "Unity.Burst", "Unity.Burst.Unsafe", "Unity.Collections", "Unity.Collections.LowLevel.ILSupport",
            "Unity.Mathematics", "Unity.MemoryProfiler", "Unity.Multiplayer.Center.Common", "Unity.Profiling.Core",
        };

        /// Mono class libraries shipped in Managed/ that the framework does not carry. They target
        /// Mono's runtime internals; their roles are filled by .NET (code pages: the host registers
        /// CodePagesEncodingProvider). Never loaded.
        static readonly HashSet<string> monoClassLibraries = new(StringComparer.Ordinal)
        {
            "I18N", "I18N.West", "Mono.Security", "System.ComponentModel.Composition",
            "System.ServiceModel.Internals", "System.EnterpriseServices",
        };

        static string managedDir = "";

        /// Where the game's assemblies and our engine facades are (ModAccess resolves mods' references there).
        public static string[] ReferenceDirectories => [managedDir, engineDir];

        /// Whether assemblies in this folder are this policy's to resolve: Managed/, the engine's
        /// facades, the UI packages and the host's own folder.
        public static bool IsPolicyDirectory(string folder)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            foreach (var dir in new[] { managedDir, engineDir, packagesDir, AppContext.BaseDirectory })
                if (dir.Length > 0 && string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), StringComparison.Ordinal)) return true;
            return false;
        }
        static OniMoono.Engine.Platform.GameLayout? layout;
        static string engineDir = "";

        /// The Unity packages this project replaces (PLAN M9).
        static readonly HashSet<string> replacedPackages = new(StringComparer.Ordinal) { "UnityEngine.UI", "Unity.TextMeshPro", "Unity.Timeline" };

        /// Where the replacements load from: packages/ next to the host.
        static string packagesDir = "";

        /// The replacements missing from packages/ next to the host (an incomplete install).
        public static List<string> MissingPackages(string hostDir) =>
            replacedPackages.Order(StringComparer.Ordinal).Where(p => !File.Exists(Path.Combine(hostDir, "packages", p + ".dll"))).ToList();

        public static void Install(OniMoono.Engine.Platform.GameLayout game, string hostDir)
        {
            packagesDir = Path.Combine(hostDir, "packages");
            layout = game;
            managedDir = Path.Combine(game.DataDir, "Managed");
            engineDir = Path.Combine(hostDir, "engine");
            AssemblyLoadContext.Default.Resolving += Resolve;
            plugins = new GamePlugins(game, NativeOverrides, ReplacementsDir, ProcessCpu);
            // Not the framework's (no game plugin there) nor BRUTAL's, which set their own
            // resolvers (an assembly can have one).
            plugins.Install(a => a.GetName().Name is not { } n || IsFrameworkAssembly(n) || n.StartsWith("Brutal.", StringComparison.Ordinal));
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
        }

        /// The game's plugins, bound before the runtime's own probing (#74).
        static GamePlugins? plugins;

        static bool IsEngineAssembly(string name) => File.Exists(Path.Combine(engineDir, name + ".dll"));

        /// Unity's engine modules: only our implementation may provide them. A module we have no
        /// facade for is one nothing references, so it is never loaded.
        static bool IsUnityEngineModule(string name) => name == "UnityEngine" || (name.StartsWith("UnityEngine.", StringComparison.Ordinal) && name.EndsWith("Module", StringComparison.Ordinal));

        static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var simple = name.Name;
            if (string.IsNullOrEmpty(simple)) return null;
            if (IsEngineAssembly(simple)) return context.LoadFromAssemblyPath(Path.Combine(engineDir, simple + ".dll"));
            if (IsUnityEngineModule(simple)) return null;
            if (unitySupportPackages.Contains(simple) || monoClassLibraries.Contains(simple)) return null;
            // Unity's packages come from packages/ only (Program checks they are all there).
            if (replacedPackages.Contains(simple)) return context.LoadFromAssemblyPath(Path.Combine(packagesDir, simple + ".dll"));
            var path = Path.Combine(managedDir, simple + ".dll");
            if (!File.Exists(path)) return null;
            var image = MonoFieldSemantics.Adjust(path);
            // Its conversions of floats to integers give Mono's values out of range (MonoConversions)
            // and its directory listings come in Mono's order (MonoDirectoryListing).
            if (MonoConversions.AdjustGame(image ?? File.ReadAllBytes(path), path) is { } converted) image = converted;
            // A development build's [Debuggable(DisableOptimizations)] is ignored, as Mono ignores it
            // (MonoJitOptimizer).
            if (MonoJitOptimizer.Adjust(image ?? File.ReadAllBytes(path), path) is { } optimized)
            {
                image = optimized;
                EngineLog.Engine($"game image: {simple}.dll asks for its code unoptimised (Debuggable); compiled optimised, as on Mono");
            }
            if (image == null) return context.LoadFromAssemblyPath(path);
            var assembly = context.LoadFromStream(new MemoryStream(image));
            ModAccess.RecordGameLocation(assembly, path);
            return assembly;
        }

        /// Development and test runs only (`--native-override NAME=PATH`): a native library
        /// loaded instead of the game's plugin of that name, e.g. the offline Steam test double
        /// (tools/SteamStub).
        public static readonly Dictionary<string, string> NativeOverrides = new(StringComparer.Ordinal);

        /// The host's replacements for game plugins that lack the process's architecture
        /// (GameLayout.Native): `plugins/` beside the host.
        public static string ReplacementsDir => Path.Combine(AppContext.BaseDirectory, GameLayout.ReplacementsFolderName);

        /// The process's architecture as a Mach-O CPU type (NativeExports.X8664 or Arm64).
        public static int ProcessCpu => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? NativeExports.Arm64 : NativeExports.X8664;

        /// What GamePlugins left to the runtime's probing and the runtime did not find either: the
        /// override or game plugin that did not load, or none at all. Game and mod code sees
        /// Mono's DllNotFoundException (D59); the runtime raises this event only once its own
        /// probing has failed.
        static IntPtr ResolveNative(Assembly requester, string name)
        {
            if (NativeOverrides.TryGetValue(name, out var overridePath))
                return plugins!.Announce(name, NativeLibrary.Load(overridePath), overridePath, "--native-override");
            var native = layout?.Native(name, OperatingSystem.IsWindows(), ProcessCpu, ReplacementsDir);
            bool monoMessage = !IsHostAssembly(requester);
            if (native == null)
            {
                plugins?.LogMissing(name);
                return monoMessage ? throw MonoDllNotFound.Missing(name) : IntPtr.Zero;
            }
            if (NativeLibrary.TryLoad(native.Value.Path, out var handle)) return handle;
            return monoMessage ? throw MonoDllNotFound.Failed(name, native.Value.Path) : NativeLibrary.Load(native.Value.Path);
        }

        /// An assembly of the host's own (its folder, engine/ and packages/), whose P/Invokes keep
        /// CoreCLR's exceptions; the game's (Managed/, or loaded from an image) and mods' get Mono's.
        static bool IsHostAssembly(Assembly assembly)
        {
            var location = assembly.Location;
            if (string.IsNullOrEmpty(location)) return false;
            var dir = Path.GetDirectoryName(location);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(dir, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), comparison)
                || string.Equals(dir, engineDir, comparison) || string.Equals(dir, packagesDir, comparison);
        }

        /// The simple names of the assemblies the game was built with (ScriptingAssemblies.json):
        /// Unity's serializer knows the types of these only (JsonUtility, JsonLayout).
        public static HashSet<string> BuildAssemblyNames(string gameDataDir)
        {
            var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(gameDataDir, "ScriptingAssemblies.json")));
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in json.RootElement.GetProperty("names"u8).EnumerateArray())
                names.Add(Path.GetFileNameWithoutExtension(entry.GetString()!));
            return names;
        }

        /// Loads the scripting assemblies up front, as the Unity player does: the game snapshots
        /// AppDomain.GetAssemblies() early and resolves saved type names against that snapshot.
        public static List<Assembly> PreloadScriptingAssemblies(string gameDataDir)
        {
            var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(gameDataDir, "ScriptingAssemblies.json")));
            var loaded = new List<Assembly>();
            foreach (var entry in json.RootElement.GetProperty("names"u8).EnumerateArray())
            {
                var simple = Path.GetFileNameWithoutExtension(entry.GetString()!);
                if (unitySupportPackages.Contains(simple) || monoClassLibraries.Contains(simple)) continue;
                if (IsFrameworkAssembly(simple)) continue;
                if (IsUnityEngineModule(simple)) continue; // ours load on demand through the facades
                if (!replacedPackages.Contains(simple) && !IsEngineAssembly(simple) && !File.Exists(Path.Combine(managedDir, simple + ".dll"))) continue;
                loaded.Add(Assembly.Load(new AssemblyName(simple)));
            }
            return loaded;
        }

        /// Assemblies of the shared framework (the trusted platform assemblies that live in the
        /// runtime directory or in packages the host ships, as opposed to the host's own projects).
        static readonly HashSet<string> framework = new(
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !Path.GetDirectoryName(p)!.Equals(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
                            || Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal))
                .Select(p => Path.GetFileNameWithoutExtension(p)!),
            StringComparer.OrdinalIgnoreCase);

        internal static bool IsFrameworkAssembly(string simple) => framework.Contains(simple);

        /// Mono's class libraries in Managed/ that the framework does not carry (never loaded).
        internal static bool IsMonoClassLibrary(string simple) => monoClassLibraries.Contains(simple);

        /// The assembly files the host runs with (--check-natives): its own (the host directory's,
        /// framework ones aside: the engine, the Steamworks.NET port, BRUTAL…), the UI packages it
        /// serves, and those of Managed/ that Resolve would load (the game and its libraries; not
        /// Mono's class libraries, Unity's modules or support packages, or what ours replace).
        public static List<string> RuntimeAssemblyPaths()
        {
            var result = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path)
            {
                if (names.Add(Path.GetFileNameWithoutExtension(path))) result.Add(path);
            }
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Order(StringComparer.Ordinal))
            {
                var simple = Path.GetFileNameWithoutExtension(path);
                if (!simple.StartsWith("System.", StringComparison.Ordinal) && !simple.StartsWith("Microsoft.", StringComparison.Ordinal)) Add(path);
            }
            if (Directory.Exists(packagesDir))
                foreach (var path in Directory.EnumerateFiles(packagesDir, "*.dll").Order(StringComparer.Ordinal)) Add(path);
            foreach (var path in Directory.EnumerateFiles(managedDir, "*.dll").Order(StringComparer.Ordinal))
            {
                var simple = Path.GetFileNameWithoutExtension(path);
                if (IsFrameworkAssembly(simple) || IsUnityEngineModule(simple) || unitySupportPackages.Contains(simple) || monoClassLibraries.Contains(simple)
                    || replacedPackages.Contains(simple)) continue;
                Add(path);
            }
            return result;
        }
    }
}
