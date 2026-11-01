using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace OniMoono.Engine.Platform
{
    /// Where an install of the game keeps its data and native plugins. Unity's players lay them out
    /// per platform:
    /// - Linux and Windows: `<game>/OxygenNotIncluded_Data` (Application.dataPath) with the plugins
    ///   in `Plugins/` (Linux, `lib<name>.so`) or `Plugins/x86_64/` (Windows, `<name>.dll`);
    /// - macOS: the bundle `OxygenNotIncluded.app`, whose `Contents/` is Application.dataPath, the
    ///   serialized data in `Contents/Resources/Data`, and each plugin a bundle
    ///   `Contents/PlugIns/<name>.bundle` whose executable (CFBundleExecutable of its Info.plist)
    ///   is the library.
    /// The layout is told from the files, not from the OS the host runs on, so a Mac install can be
    /// read (and checked) on Linux.
    internal sealed class GameLayout
    {
        /// The folder of the serialized data, ScriptingAssemblies.json and Managed/.
        public string DataDir { get; private init; } = "";
        /// Application.dataPath: the data folder, or the bundle's Contents on macOS.
        public string DataPath { get; private init; } = "";
        public string StreamingAssetsPath => Path.Combine(DataDir, "StreamingAssets");
        /// The folder the native plugins are in.
        public string PluginsDir { get; private init; } = "";
        /// The .app bundle (null for other layouts).
        public string App { get; private init; }
        /// The bundle's CFBundleIdentifier (null for other layouts).
        public string BundleIdentifier { get; private init; }

        public bool IsMacBundle => App != null;

        public const string DataFolderName = "OxygenNotIncluded_Data";

        /// The layout of `gameDir` (what --game names): a folder with OxygenNotIncluded_Data, a .app
        /// bundle, or a folder holding one .app (the Mac Steam install). Null when it is none.
        public static GameLayout Find(string gameDir)
        {
            var data = Path.Combine(gameDir, DataFolderName);
            if (Directory.Exists(data))
            {
                // Unity's Windows player keeps 64-bit plugins in Plugins/x86_64; the Linux build in Plugins.
                var x64 = Path.Combine(data, "Plugins", "x86_64");
                return new GameLayout { DataDir = data, DataPath = data, PluginsDir = Directory.Exists(x64) ? x64 : Path.Combine(data, "Plugins") };
            }
            var app = IsBundle(gameDir) ? gameDir : SingleBundle(gameDir);
            return app == null ? null : MacBundle(app);
        }

        /// The game folder (what --game takes) of the executable Steam's %command% names: the folder
        /// of OxygenNotIncluded (Linux) or OxygenNotIncluded.exe (Windows); on macOS the bundle,
        /// named itself or by its executable `OxygenNotIncluded.app/Contents/MacOS/Oxygen Not
        /// Included` (CFBundleExecutable; which of the two Steam passes is to verify on a Mac).
        /// Null for any other path.
        public static string GameDirOfExecutable(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd('/');
            var name = Path.GetFileName(full);
            if (name is "OxygenNotIncluded" or "OxygenNotIncluded.exe") return Path.GetDirectoryName(full);
            if (name == "OxygenNotIncluded.app") return full;
            var macOS = Path.GetDirectoryName(full);
            var contents = macOS == null ? null : Path.GetDirectoryName(macOS);
            if (name == MacExecutable && contents != null && Path.GetFileName(macOS) == "MacOS" && Path.GetFileName(contents) == "Contents"
                && Path.GetFileName(Path.GetDirectoryName(contents)) == "OxygenNotIncluded.app")
                return Path.GetDirectoryName(contents);
            return null;
        }

        /// The Mac bundle's executable (its Info.plist's CFBundleExecutable).
        public const string MacExecutable = "Oxygen Not Included";

        /// Where Steam installs the game on macOS, for a home folder (to verify on a Mac; Steam's
        /// library folders elsewhere are named in libraryfolders.vdf, as on Linux).
        public static string DefaultMacSteamInstall(string home) => Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "OxygenNotIncluded");

        static bool IsBundle(string dir) => dir.TrimEnd('/', '\\').EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(dir, "Contents", "Info.plist"));

        static string SingleBundle(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            string found = null;
            foreach (var candidate in Directory.EnumerateDirectories(dir, "*.app"))
            {
                if (!IsBundle(candidate) || !Directory.Exists(Path.Combine(candidate, "Contents", "Resources", "Data"))) continue;
                if (found != null) return null;
                found = candidate;
            }
            return found;
        }

        static GameLayout MacBundle(string app)
        {
            var contents = Path.Combine(app, "Contents");
            var data = Path.Combine(contents, "Resources", "Data");
            if (!Directory.Exists(data)) return null;
            return new GameLayout
            {
                App = app,
                DataDir = data,
                DataPath = contents,
                PluginsDir = Path.Combine(contents, "PlugIns"),
                BundleIdentifier = PlistString(Path.Combine(contents, "Info.plist"), "CFBundleIdentifier"),
            };
        }

        /// The files a native plugin name (a DllImport's) may be, in the order they are tried.
        public IEnumerable<string> PluginCandidates(string name, bool windows)
        {
            if (IsMacBundle)
            {
                var bundle = Path.Combine(PluginsDir, name + ".bundle", "Contents");
                var executable = PlistString(Path.Combine(bundle, "Info.plist"), "CFBundleExecutable");
                if (executable != null) yield return Path.Combine(bundle, "MacOS", executable);
                yield return Path.Combine(PluginsDir, "lib" + name + ".dylib");
                yield return Path.Combine(PluginsDir, name + ".dylib");
                yield break;
            }
            foreach (var candidate in windows ? new[] { $"{name}.dll", name } : new[] { $"lib{name}.so", $"{name}.so", name })
                yield return Path.Combine(PluginsDir, candidate);
        }

        /// The first candidate that exists, or null.
        public string Plugin(string name, bool windows)
        {
            foreach (var path in PluginCandidates(name, windows))
                if (File.Exists(path)) return path;
            return null;
        }

        /// The folder beside the host that holds replacements for the game's plugins (`plugins/`).
        public const string ReplacementsFolderName = "plugins";

        /// The files a replacement for plugin `name` may be in folder `dir`.
        public static IEnumerable<string> ReplacementCandidates(string name, string dir) =>
            [Path.Combine(dir, $"lib{name}.dylib"), Path.Combine(dir, $"lib{name}.so"), Path.Combine(dir, $"{name}.dll")];

        /// The library the host binds for plugin `name` in a process of CPU type `cpu`
        /// (NativeExports.X8664 or Arm64): the game's plugin when it has that architecture, else a
        /// replacement in `replacements` (the host's `plugins/` folder) that has it, else the
        /// game's plugin (which then fails to load, as it would without a replacement). This is how
        /// an arm64 macOS build runs on the onimaxxing-sim SimDLL: Klei's Mac SimDLL is x86_64
        /// only, so the port shipped as `plugins/libSimDLL.dylib` is chosen, while an x86_64
        /// process keeps Klei's. `Replacement` tells which was chosen.
        public (string Path, bool Replacement)? Native(string name, bool windows, int cpu, string replacements)
        {
            var plugin = Plugin(name, windows);
            if (plugin != null && HasArchitecture(plugin, cpu)) return (plugin, false);
            if (replacements != null)
                foreach (var path in ReplacementCandidates(name, replacements))
                    if (File.Exists(path) && HasArchitecture(path, cpu)) return (path, true);
            return plugin != null ? (plugin, false) : null;
        }

        /// Whether a library file has code for CPU type `cpu` (a file of unknown format is taken to,
        /// and so is one that cannot be read, which then fails to load as in Unity: D58).
        public static bool HasArchitecture(string path, int cpu)
        {
            var head = new byte[4096];
            int n;
            try
            {
                using var f = File.OpenRead(path);
                n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                return true;
            }
            var archs = NativeExports.Architectures(head.AsSpan(0, n));
            return archs.Count == 0 || archs.Contains(cpu);
        }

        /// A string value of a top-level key of an XML property list (Unity's and Klei's
        /// Info.plists are XML), or null.
        public static string PlistString(string path, string key)
        {
            if (!File.Exists(path)) return null;
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
                doc.Load(reader);
            var dict = doc.DocumentElement?["dict"];
            if (dict == null) return null;
            for (var node = dict.FirstChild; node != null; node = node.NextSibling)
            {
                if (node is not XmlElement { Name: "key" } k || k.InnerText != key) continue;
                var value = node.NextSibling;
                while (value != null && value is not XmlElement) value = value.NextSibling;
                return value is XmlElement { Name: "string" } s ? s.InnerText : null;
            }
            return null;
        }
    }

    /// The per-user folders of Unity's macOS player for a game (Application.persistentDataPath,
    /// temporaryCachePath and the player log), under a home folder (or a test run's).
    internal readonly record struct MacUserFolders(string PersistentDataPath, string TemporaryCachePath, string ConsoleLogPath)
    {
        /// `bundleIdentifier`: the game bundle's CFBundleIdentifier ("unity.Klei.Oxygen Not
        /// Included"). Unity's Mac player keeps persistent data in
        /// ~/Library/Application Support/<bundle identifier>, its caches in ~/Library/Caches/<bundle
        /// identifier> and its log in ~/Library/Logs/<company>/<product>/Player.log
        /// (docs/research/macos-feasibility.md §5.2; to verify on a Mac).
        public static MacUserFolders For(string home, string bundleIdentifier, string company, string product)
        {
            var library = Path.Combine(home, "Library");
            return new MacUserFolders(
                Path.Combine(library, "Application Support", bundleIdentifier),
                Path.Combine(library, "Caches", bundleIdentifier),
                Path.Combine(library, "Logs", company, product, "Player.log"));
        }
    }
}
