#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using OniMoono.Engine;
using OniMoono.Engine.Platform;

namespace OniMoono.Host
{
    /// Binds a DllImport of a game plugin (steam_api64, SimDLL, fmodstudio…) to the game's file in
    /// Plugins/ (GameLayout.Native) before the runtime's own probing, as Unity's player binds its
    /// plugins, wherever the player is installed.
    ///
    /// CoreCLR probes a DllImport's name in the importing assembly's folder (the player's, for
    /// Steamworks.NET) and then through the operating system's search: on Windows the exe's
    /// folder, System32, the Windows folder, the working directory and PATH; on Linux
    /// LD_LIBRARY_PATH and the system's library folders. Only when all of that fails does the
    /// load context's ResolvingUnmanagedDll event reach the game's folder. Any other copy of the
    /// name on that search won: #74, an older steam_api64.dll somewhere on the Windows search,
    /// bound in place of the game's, so Steamworks.NET's SteamAPI.Init failed at
    /// SteamAPI_ManualDispatch_Init (an export older Steamworks SDKs lack) and the game ran
    /// without Steam, its DLCs unseen. A resolver set per assembly (SetDllImportResolver) runs
    /// before that probing.
    internal sealed class GamePlugins
    {
        readonly GameLayout layout;
        readonly IReadOnlyDictionary<string, string> overrides;
        readonly string replacements;
        readonly int cpu;
        readonly bool windows = OperatingSystem.IsWindows();
        /// Name -> the library bound for it (zero: none in the game, left to the runtime's probing).
        readonly Dictionary<string, IntPtr> bound = new(StringComparer.Ordinal);
        readonly HashSet<string> announced = new(StringComparer.Ordinal);
        readonly HashSet<string> missingLogged = new(StringComparer.Ordinal);

        /// The plugins the game cannot start without (Steam's, the simulation's, FMOD's).
        static readonly string[] requiredPlugins = ["steam_api", "steam_api64", "SimDLL", "fmodstudio", "fmodstudioL"];

        /// For `layout`, with `overrides` (`--native-override NAME=PATH`) and the host's
        /// replacements folder, in a process of CPU type `cpu` (NativeExports.X8664 or Arm64).
        public GamePlugins(GameLayout layout, IReadOnlyDictionary<string, string> overrides, string replacements, int cpu)
        {
            this.layout = layout;
            this.overrides = overrides;
            this.replacements = replacements;
            this.cpu = cpu;
        }

        /// Sets the resolver on every assembly loaded now and later, `skip` aside (the framework's,
        /// which import no game plugin).
        public void Install(Func<Assembly, bool> skip)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
            {
                if (!skip(e.LoadedAssembly)) Register(e.LoadedAssembly);
            };
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (!skip(assembly)) Register(assembly);
        }

        /// Sets the resolver on `assembly`. An assembly can have one; one that has another (set by
        /// its own code) keeps it, and a dynamic one can have none.
        public void Register(Assembly assembly)
        {
            if (assembly.IsDynamic) return;
            try
            {
                NativeLibrary.SetDllImportResolver(assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        /// The library for DllImport name `name`: the override, the game's plugin (or the host's
        /// replacement for it), or zero, which leaves the name to the runtime's probing and then
        /// to AssemblyPolicy.ResolveNative, which reports a missing or broken plugin as Mono does.
        public IntPtr Resolve(string name, Assembly requester, DllImportSearchPath? searchPath)
        {
            lock (bound)
            {
                if (bound.TryGetValue(name, out var known)) return known;
                var handle = Bind(name);
                bound[name] = handle;
                return handle;
            }
        }

        IntPtr Bind(string name)
        {
            if (overrides.TryGetValue(name, out var overridePath))
                return NativeLibrary.TryLoad(overridePath, out var o) ? Announce(name, o, overridePath, "--native-override") : IntPtr.Zero;
            var native = layout.Native(name, windows, cpu, replacements);
            if (native == null)
            {
                LogMissing(name);
                return IntPtr.Zero;
            }
            if (!NativeLibrary.TryLoad(native.Value.Path, out var handle))
            {
                if (IsRequired(name)) EngineLog.Engine($"Native {name}: {native.Value.Path} did not load");
                return IntPtr.Zero;
            }
            if (native.Value.Replacement)
                return Announce(name, handle, native.Value.Path, $"the host's replacement: the game's plugin has no {NativeExports.ArchName(cpu)} code");
            return IsRequired(name) ? Announce(name, handle, native.Value.Path, null) : handle;
        }

        static bool IsRequired(string name) => Array.IndexOf(requiredPlugins, name) >= 0;

        /// Logs, once per name, the folders searched for a required plugin that none of them has,
        /// so a player log tells a misplaced install from a broken library (#49).
        public void LogMissing(string name)
        {
            if (!IsRequired(name)) return;
            lock (missingLogged)
                if (!missingLogged.Add(name)) return;
            var root = Path.GetDirectoryName(layout.IsMacBundle ? layout.App!.TrimEnd('/') : layout.DataDir) ?? "";
            var folders = layout.PluginFolders(windows).Select(f => Path.GetRelativePath(root, f));
            string files = layout.IsMacBundle ? $"{name}.bundle, lib{name}.dylib or {name}.dylib"
                : windows ? $"{name}.dll" : $"lib{name}.so, {name}.so or {name}";
            EngineLog.Engine($"Native {name}: no plugin found ({files}); searched {string.Join(", ", folders)}");
        }

        /// Logs, once per name, which file a plugin was bound to (`what`: why not the game's, or
        /// null); for a SimDLL other than the game's, also its SYSINFO_Acquire string
        /// (onimaxxing-sim's banner, UTF-16; the game itself never calls it), which shows the
        /// game's imports bind to that library; for Steam's, an export the
        /// game's Steamworks.NET needs that the library lacks (an older Steamworks SDK's).
        public unsafe IntPtr Announce(string name, IntPtr handle, string path, string? what)
        {
            lock (announced)
                if (!announced.Add(name)) return handle;
            string note = "";
            if (name == "SimDLL" && what != null && NativeLibrary.TryGetExport(handle, "SYSINFO_Acquire", out var acquire))
            {
                var text = ((delegate* unmanaged<byte*>)acquire)();
                if (text != null) note = ": SYSINFO \"" + (text[1] == 0 ? new string((char*)text) : Marshal.PtrToStringUTF8((IntPtr)text)) + "\"";
                if (NativeLibrary.TryGetExport(handle, "SYSINFO_Release", out var release)) ((delegate* unmanaged<void>)release)();
            }
            if (name.StartsWith("steam_api", StringComparison.Ordinal) && !NativeLibrary.TryGetExport(handle, "SteamAPI_ManualDispatch_Init", out _))
                note = ": no SteamAPI_ManualDispatch_Init export (an older Steamworks SDK's), so Steam will not initialise";
            EngineLog.Engine($"Native {name}: {path}{(what != null ? $" ({what})" : "")}{note}");
            return handle;
        }
    }
}
