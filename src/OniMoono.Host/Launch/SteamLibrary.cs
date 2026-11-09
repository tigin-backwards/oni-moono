#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OniMoono.Host.Launch
{
    /// Where this machine's Steam keeps things, as the standalone launch needs them: the Steam
    /// install folders, their libraries, the game's installs in them with their appmanifests, and
    /// whether the client is running. Read only; nothing here writes a Steam file. The places are setup.sh's and
    /// setup.ps1's (SteamLibraryTests checks that the lists agree).
    internal sealed class SteamLibrary
    {
        public const string AppId = "457140";
        public const string GameFolder = "OxygenNotIncluded";

        /// The machine as this reads it: replaced by a made-up one in tests.
        internal sealed class Machine
        {
            public bool Windows = OperatingSystem.IsWindows();
            public string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            public Func<string, string?> Env = Environment.GetEnvironmentVariable;
            /// HKCU's value at a subkey and name (Windows); null when there is none.
            public Func<string, string, object?> Registry = ReadCurrentUser;

            static object? ReadCurrentUser(string subkey, string name)
            {
                if (!OperatingSystem.IsWindows()) return null;
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subkey);
                    return key?.GetValue(name);
                }
                catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
            }
        }

        public enum Sandbox { None, Flatpak, Snap }

        /// A Steam install: its folder and how it runs.
        public sealed record Root(string Dir, Sandbox Sandbox);

        /// The game in a library: its folder, the library and Steam install holding it, and its
        /// appmanifest (null when the library has none for it).
        public sealed record GameInstall(string Dir, string Library, Root Root, AppManifest? Manifest);

        /// appmanifest_457140.acf: StateFlags (4, fully installed, alone when nothing is pending),
        /// Steam's build ID, the install folder's name.
        public sealed record AppManifest(string Path, int StateFlags, string? BuildId, string? InstallDir)
        {
            public const int FullyInstalled = 4, UpdateRequired = 2;

            /// Installed with nothing pending: StateFlags is FullyInstalled alone. Any other bit
            /// (an update required, queued, running or paused; files being validated) is Steam's
            /// to finish first.
            public bool Ready => StateFlags == FullyInstalled;

            public string Describe() => Ready ? "installed, nothing pending"
                : (StateFlags & FullyInstalled) == 0 ? $"not fully installed (StateFlags {StateFlags})"
                : (StateFlags & UpdateRequired) != 0 ? $"an update is pending (StateFlags {StateFlags})"
                : $"Steam has work pending on it (StateFlags {StateFlags})";

            public static AppManifest? Read(string path)
            {
                Vdf vdf;
                try { vdf = Vdf.Parse(File.ReadAllText(path)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
                var state = vdf.Block("AppState");
                if (state == null) return null;
                int flags = int.TryParse(state.Value("StateFlags"), out var f) ? f : 0;
                return new AppManifest(path, flags, state.Value("buildid"), state.Value("installdir"));
            }
        }

        readonly Machine machine;

        public SteamLibrary(Machine? machine = null) => this.machine = machine ?? new Machine();

        /// The Linux places, in setup.sh's order: Steam's own installer, the distribution's
        /// package, the snap and the Flatpak.
        public static readonly string[] LinuxRoots =
            [".steam/steam", ".local/share/Steam", "snap/steam/common/.local/share/Steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam"];

        /// Test runs: ONI_MOONO_STEAM_ROOTS (folders separated as PATH's are) in place of the
        /// machine's Steam installs. Its presence also keeps SteamClient from starting or
        /// opening anything of the real Steam's.
        public const string RootsVariable = "ONI_MOONO_STEAM_ROOTS";

        public bool IsTest => machine.Env(RootsVariable) is { Length: > 0 };

        public IReadOnlyList<Root> Roots()
        {
            var found = new List<Root>();
            var seen = new HashSet<string>(machine.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            void Add(string dir)
            {
                if (!File.Exists(Path.Combine(dir, "steamapps", "libraryfolders.vdf")) && !Directory.Exists(Path.Combine(dir, "steamapps"))) return;
                if (!seen.Add(Resolved(dir))) return; // ~/.steam/steam is a link to one of the others
                found.Add(new Root(dir, SandboxOf(dir)));
            }
            if (machine.Env(RootsVariable) is { Length: > 0 } test)
            {
                foreach (var dir in test.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) Add(dir);
                return found;
            }
            if (machine.Windows)
            {
                if (machine.Registry(@"Software\Valve\Steam", "SteamPath") is string path && path.Length > 0) Add(OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path);
                var x86 = machine.Env("ProgramFiles(x86)");
                if (x86 != null) Add(Path.Combine(x86, "Steam"));
            }
            else
                foreach (var relative in LinuxRoots) Add(Path.Combine(machine.Home, relative));
            return found;
        }

        Sandbox SandboxOf(string dir)
        {
            if (machine.Windows) return Sandbox.None;
            if (dir.Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal)) return Sandbox.Flatpak;
            if (dir.Contains("/snap/steam/", StringComparison.Ordinal)) return Sandbox.Snap;
            return Sandbox.None;
        }

        static string Resolved(string dir)
        {
            try
            {
                var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return dir; }
        }

        /// A Steam install's libraries: its own folder first, then libraryfolders.vdf's paths
        /// (the current format's `"N" { "path" "..." }` and the old `"N" "..."`).
        public static IReadOnlyList<string> Libraries(Root root)
        {
            var libraries = new List<string> { root.Dir };
            var file = Path.Combine(root.Dir, "steamapps", "libraryfolders.vdf");
            Vdf vdf;
            try { vdf = Vdf.Parse(File.ReadAllText(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return libraries; }
            var folders = vdf.Block("libraryfolders") ?? vdf.Block("LibraryFolders");
            if (folders == null) return libraries;
            foreach (var (key, value, block) in folders.Entries)
            {
                var path = block?.Value("path") ?? (value != null && int.TryParse(key, out _) ? value : null);
                if (string.IsNullOrEmpty(path)) continue;
                if (!libraries.Any(l => SamePath(l, path))) libraries.Add(path);
            }
            return libraries;
        }

        static bool SamePath(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        /// The game's installs in every library of every Steam install: where the library has an
        /// appmanifest for it, in the folder it names; else a steamapps/common/OxygenNotIncluded
        /// with the game's data (setup's test), without a manifest.
        public IReadOnlyList<GameInstall> GameInstalls()
        {
            var installs = new List<GameInstall>();
            var seen = new HashSet<string>(machine.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var root in Roots())
                foreach (var library in Libraries(root))
                {
                    var steamapps = Path.Combine(library, "steamapps");
                    var manifestPath = Path.Combine(steamapps, $"appmanifest_{AppId}.acf");
                    var manifest = File.Exists(manifestPath) ? AppManifest.Read(manifestPath) : null;
                    var dir = Path.Combine(steamapps, "common", manifest?.InstallDir is { Length: > 0 } named ? named : GameFolder);
                    if (manifest == null && !Directory.Exists(Path.Combine(dir, GameFolder + "_Data"))) continue;
                    if (!seen.Add(Resolved(dir))) continue;
                    installs.Add(new GameInstall(dir, library, root, manifest));
                }
            return installs;
        }

        /// Steam's running client, as it records itself: on Windows HKCU's
        /// Software\Valve\Steam\ActiveProcess (pid, ActiveUser); on Linux the same keys in
        /// registry.vdf beside the install's ~/.steam. Running: the process is alive; logged in:
        /// ActiveUser is not 0.
        public (bool Running, bool LoggedIn) ClientState(Func<int, bool> alive)
        {
            int pid = 0, user = 0;
            if (machine.Windows && !IsTest)
            {
                pid = machine.Registry(@"Software\Valve\Steam\ActiveProcess", "pid") is int p ? p : 0;
                user = machine.Registry(@"Software\Valve\Steam\ActiveProcess", "ActiveUser") is int u ? u : 0;
            }
            else
            {
                foreach (var file in RegistryFiles())
                {
                    try
                    {
                        var active = Vdf.Parse(File.ReadAllText(file)).Path("Registry", "HKCU", "Software", "Valve", "Steam", "ActiveProcess");
                        if (active == null) continue;
                        pid = int.TryParse(active.Value("pid"), out var p) ? p : 0;
                        user = int.TryParse(active.Value("ActiveUser"), out var u) ? u : 0;
                        if (pid != 0) break;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            bool running = pid != 0 && alive(pid);
            return (running, running && user != 0);
        }

        /// registry.vdf of each Steam install: in its home's .steam folder (~/.steam, or the
        /// sandbox's), or, for a test root, in the root itself.
        IEnumerable<string> RegistryFiles()
        {
            if (IsTest)
            {
                foreach (var root in Roots()) yield return Path.Combine(root.Dir, "registry.vdf");
                yield break;
            }
            foreach (var home in new[] { machine.Home, Path.Combine(machine.Home, "snap/steam/common"), Path.Combine(machine.Home, ".var/app/com.valvesoftware.Steam") })
            {
                var file = Path.Combine(home, ".steam", "registry.vdf");
                if (File.Exists(file)) yield return file;
            }
        }
    }
}
