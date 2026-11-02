using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using OniMoono.Engine.Platform;

namespace OniMoono.Host
{
    /// `--check-natives`: every P/Invoke of the assemblies the host would run with this install,
    /// against the libraries it would bind to, read from their files without loading them
    /// (NativeExports). The game's plugins (SimDLL, fmodstudio, steam_api…) resolve as the host
    /// resolves them for the install's layout (GameLayout), so a Mac bundle can be checked on Linux,
    /// for either architecture (`--native-arch x86_64|arm64`), with a replacement plugin
    /// (`--native-override NAME=PATH`, or the host's `plugins/`) and the host's own natives
    /// (`--native-dir`, a publish folder). Fails when an entry point a found plugin lacks is
    /// imported, when a library lacks the architecture, or when a dependency it links is absent.
    internal static class NativeCheck
    {
        /// Library names that are the operating system's or a runtime's, not the game's: Windows'
        /// (never called on another OS), Unix and macOS system libraries, and BRUTAL's natives,
        /// which BRUTAL resolves from its packages (checked only with --native-dir; an osx-x64
        /// package carries none: docs/research/macos-feasibility.md §3.2).
        static string? Outside(string module)
        {
            var name = module.ToLowerInvariant();
            if (name.StartsWith("/system/library/") || name.StartsWith("/usr/lib/") || name == "libsystem") return "macOS system library";
            if (name is "libc" or "dl" or "libdl.so.2" or "libc.so.6" or "libasound" or "__internal") return "Unix system library";
            // Xlib and XRandR, which GLFW's X11 backend has loaded (KeyboardLayout reads the keymap
            // through Xlib, ScreenDpi the monitor's size through XRandR).
            if (name is "libx11.so.6" or "libxrandr.so.2") return "X11 client library";
            if (name.EndsWith(".dll") || name is "kernel32" or "kernelbase" or "advapi32" or "gdi32" or "gdiplus" or "user32" or "ole32" or "shell32") return "Windows library";
            if (name is "glfw" or "freetype" or "stb" or "glad") return "BRUTAL native (its package's)";
            return null;
        }

        /// Plugins an install lacks by design, and why. A P/Invoke binds when first called, so
        /// their imports fail only if called, as in Klei's own player of the same install.
        static readonly Dictionary<string, string> absentPlugins = new(StringComparer.Ordinal)
        {
            ["cimgui"] = "Klei's ImGui dev tools; the Mac build compiles them out and ships no cimgui",
            ["libEOSSDK-Mac-Shipping"] = "Epic Online Services; the game never creates an EOS platform",
            ["EOSSDK-Win64-Shipping.dll"] = "Epic Online Services; the game never creates an EOS platform",
            ["rail_api64"] = "WeGame's Rail SDK (firstpass' bindings); neither the Linux nor the Mac build ships it",
            ["sdkencryptedappticket"] = "Steam's encrypted app tickets (Steamworks.NET's bindings); no game assembly names them, and the Mac build ships no library",
        };

        /// Entry points a plugin may lack, and why: the game's own lack them on Linux (where the
        /// game runs) as on macOS, or no game code can call them.
        static readonly Dictionary<string, (string Why, string[] Entries)> absentEntries = new(StringComparer.Ordinal)
        {
            // Steamworks.NET 20.1.0 binds a newer SDK than the game's steam_api.
            ["steam_api"] = ("missing, as on Linux:",
            [
                "SteamAPI_ISteamNetworkingConnectionSignaling_Release", "SteamAPI_ISteamNetworkingConnectionSignaling_SendSignal",
                "SteamAPI_ISteamNetworkingSignalingRecvContext_OnConnectRequest", "SteamAPI_ISteamNetworkingSignalingRecvContext_SendRejectionSignal",
                "SteamGameServerClient", "SteamGameServer_ReleaseCurrentThreadMemory",
            ]),
            // Klei's Linux cimgui lacks SetKAssertCB (ARCHITECTURE D20).
            ["cimgui"] = ("missing, as on Linux:", ["SetKAssertCB"]),
            ["sdkencryptedappticket"] = ("missing, as on Linux:", ["SteamEncryptedAppTicket_BIsLicenseBorrowed", "SteamEncryptedAppTicket_BIsLicenseTemporary", "SteamEncryptedAppTicket_BIsTicketSigned"]),
            // Klei's profiler plugin, which Klei's SimDLL carries and a replacement (onimaxxing-sim)
            // need not: every caller in KProfiler is [Conditional("ENABLE_KPROFILER")], compiled
            // out of the shipped game, and KProfilerPlugin.Initialized is never set.
            ["SimDLL"] = ("not exported, never called (KProfiler, compiled out):",
            [
                "kprofile_record_string", "kprofiler_begin_section", "kprofiler_counter", "kprofiler_end_section",
                "kprofiler_flush_data_sender", "kprofiler_get_thread_uid", "kprofiler_load_plugin", "kprofiler_ping",
                "kprofiler_set_thread_info", "kprofiler_start_file_data_sender", "kprofiler_start_http_control_listener",
                "kprofiler_start_http_data_sender", "kprofiler_start_profiling", "kprofiler_stop_data_sender",
                "kprofiler_stop_profiling", "kprofiler_unload_plugin",
            ]),
        };

        /// BRUTAL's natives, which BRUTAL's own resolvers load from the host's folder.
        static readonly HashSet<string> brutalNatives = new(StringComparer.Ordinal) { "glfw", "freetype", "stb", "glad" };

        /// Checks for a process of CPU type `cpu` (NativeExports.X8664 or Arm64): Mach-O files are
        /// read at that slice, and a library without it fails. Plugins resolve as the host resolves
        /// them (--native-override, then GameLayout.Native with the replacements in
        /// `hostDir/plugins`). With `hostDir` (`--native-dir`, e.g. an osx-arm64 publish), BRUTAL's
        /// natives are checked there too. Each Mach-O library's own dependencies are listed and
        /// must be found, so the run names every native file such a process would load.
        public static int Run(GameLayout layout, IReadOnlyList<string> assemblies, int cpu, string? hostDir)
        {
            // module -> entry point -> assemblies importing it
            var imports = new SortedDictionary<string, SortedDictionary<string, SortedSet<string>>>(StringComparer.Ordinal);
            foreach (var path in assemblies)
            {
                using var pe = new PEReader(File.OpenRead(path));
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var h in md.MethodDefinitions)
                {
                    var import = md.GetMethodDefinition(h).GetImport();
                    if (import.Module.IsNil) continue;
                    var module = md.GetString(md.GetModuleReference(import.Module).Name);
                    var entry = md.GetString(import.Name);
                    if (!imports.TryGetValue(module, out var entries)) imports[module] = entries = new(StringComparer.Ordinal);
                    if (!entries.TryGetValue(entry, out var from)) entries[entry] = from = new(StringComparer.Ordinal);
                    from.Add(Path.GetFileName(path));
                }
            }
            string arch = NativeExports.ArchName(cpu);
            string replacements = Path.Combine(hostDir ?? AppContext.BaseDirectory, GameLayout.ReplacementsFolderName);
            Console.WriteLine($"{assemblies.Count} assemblies, {imports.Count} libraries, {imports.Values.Sum(e => e.Count)} entry points ({(layout.IsMacBundle ? "macOS bundle " + layout.App : layout.DataDir)}; {arch}{(hostDir != null ? "; host natives " + hostDir : "")})");
            int unexpected = 0;
            foreach (var (module, entries) in imports)
            {
                string importers = string.Join(" ", entries.Values.SelectMany(s => s).Distinct().Order(StringComparer.Ordinal));
                bool windows = OperatingSystem.IsWindows() && !layout.IsMacBundle;
                bool brutal = hostDir != null && brutalNatives.Contains(module);
                string? file = null, source = null;
                if (AssemblyPolicy.NativeOverrides.TryGetValue(module, out var overridden)) (file, source) = (overridden, "--native-override");
                else if (brutal) (file, source) = (GameLayout.ReplacementCandidates(module, hostDir!).FirstOrDefault(File.Exists), "BRUTAL's, in the host folder");
                else if (layout.Native(module, windows, cpu, replacements) is { } native) (file, source) = (native.Path, native.Replacement ? "the host's replacement" : null);
                if (file == null)
                {
                    var outside = Outside(module);
                    if (brutal)
                    {
                        unexpected += entries.Count;
                        Console.WriteLine($"  {module}: {entries.Count} entry points, NO LIBRARY in {hostDir} ({importers})");
                    }
                    else if (outside != null) Console.WriteLine($"  {module}: {entries.Count} entry points, {outside} ({importers})");
                    else if (absentPlugins.TryGetValue(module, out var why)) Console.WriteLine($"  {module}: {entries.Count} entry points, no plugin in the install, expected: {why} ({importers})");
                    else
                    {
                        unexpected += entries.Count;
                        Console.WriteLine($"  {module}: {entries.Count} entry points, NO PLUGIN in the install ({importers})");
                    }
                    continue;
                }
                var bytes = File.ReadAllBytes(file);
                string format = NativeExports.Format(bytes) ?? "unknown format";
                var archs = NativeExports.Architectures(bytes);
                // A thin file's format names its architecture; a universal one lists its slices.
                string slices = format == "Mach-O universal" ? " " + string.Join("+", archs.Select(NativeExports.ArchName)) : "";
                string shown = file.StartsWith(layout.PluginsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? Path.GetRelativePath(layout.PluginsDir, file) : file;
                string by = source != null ? $"{source} " : "";
                if (archs.Count > 0 && !archs.Contains(cpu))
                {
                    unexpected += entries.Count;
                    Console.WriteLine($"  {module}: {entries.Count} entry points, NO {arch} CODE in {by}{shown} ({format}{slices}; {importers})");
                    continue;
                }
                var exports = NativeExports.Read(bytes, cpu);
                var absent = entries.Keys.Where(e => !exports.Contains(e)).ToList();
                var (known, knownWhy) = absentEntries.TryGetValue(module, out var k) ? (k.Entries, k.Why) : ([], "");
                // Brutal.Glfw binds the native-access functions of every windowing system; a
                // platform's GLFW build has its own only (the engine calls the running one's).
                if (module == "glfw")
                    (known, knownWhy) = (absent.Where(e => System.Text.RegularExpressions.Regex.IsMatch(e, "^glfw[GS]et(GLX|WGL|EGL|OSMesa|NSGL|Cocoa|Wayland|Win32|X11)")).ToArray(),
                        "not exported, another windowing system's native access:");
                var links = NativeExports.Dependencies(bytes, cpu).Select(d => Linked(d, file)).ToList();
                string linked = links.Count > 0 ? $"; links {string.Join(" ", links)}" : "";
                Console.WriteLine($"  {module}: {entries.Count - absent.Count} of {entries.Count} entry points exported by {by}{shown} ({format}{slices}, {exports.Count} exports{linked}; {importers})");
                // A whole expected-absent group (a replacement's) is one line, not one per entry.
                bool grouped = known.Length > 3 && known.All(absent.Contains);
                if (grouped) Console.WriteLine($"      {knownWhy} {known.Length} entry points ({string.Join(" ", known)})");
                foreach (var e in absent)
                {
                    bool expected = Array.IndexOf(known, e) >= 0;
                    if (expected && grouped) continue;
                    if (!expected) unexpected++;
                    Console.WriteLine($"      {(expected ? knownWhy : "MISSING")} {e} ({string.Join(" ", entries[e])})");
                }
                foreach (var l in links.Where(l => l.EndsWith("(NOT FOUND)", StringComparison.Ordinal)))
                {
                    unexpected++;
                    Console.WriteLine($"      DEPENDENCY MISSING: {l}");
                }
            }
            Console.WriteLine(unexpected == 0 ? "every entry point of the install's plugins is exported, or its absence expected" : $"{unexpected} entry points missing");
            return unexpected == 0 ? 0 : 1;
        }

        /// A Mach-O dependency's install name: the file name, marked "(system)" for the OS's
        /// libraries and frameworks; for @rpath, @loader_path and @executable_path names, the file
        /// beside the library (the host and BRUTAL ship their dylibs side by side, the game's
        /// plugin bundles are self-contained), "(NOT FOUND)" if it is not there.
        static string Linked(string dependency, string library)
        {
            if (dependency.StartsWith("/usr/lib/", StringComparison.Ordinal) || dependency.StartsWith("/System/Library/", StringComparison.Ordinal))
                return Path.GetFileName(dependency) + " (system)";
            int slash = dependency.IndexOf('/');
            if (dependency.StartsWith('@') && slash > 0)
                return File.Exists(Path.Combine(Path.GetDirectoryName(library)!, dependency[(slash + 1)..])) ? dependency[(slash + 1)..] : dependency + " (NOT FOUND)";
            return dependency;
        }
    }
}
