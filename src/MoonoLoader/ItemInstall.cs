#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OniMoono.Host.Install;

namespace MoonoLoader
{
    /// What the loader's Copy button does, without the game (the tests compile this file too):
    /// installs the version of the item the game loaded the loader from into Moono's versioned
    /// install, as Moono's installer does (PlayerFiles, every file checked against the version's
    /// manifest), puts the entry point in place and names the item as the install's source, so
    /// later starts of Moono update from it. The line for Steam's launch options starts that
    /// entry point.
    internal sealed class ItemInstall
    {
        public const string RootVariable = "ONI_MOONO_INSTALL_ROOT";

        /// The version folder the game chose (the loader's own folder), the item's root, and the
        /// versioned install's root.
        public readonly string VersionDir, ItemRoot, Root;
        public readonly bool Windows;

        public ItemInstall(string versionDir, string root, bool windows)
        {
            VersionDir = Path.GetFullPath(versionDir).TrimEnd('/', '\\');
            ItemRoot = ItemRootOf(VersionDir);
            Root = Path.GetFullPath(root);
            Windows = windows;
        }

        /// The item's root: the version folder itself, or the folder of its archived_versions.
        public static string ItemRootOf(string versionDir)
        {
            versionDir = Path.GetFullPath(versionDir).TrimEnd('/', '\\');
            var parent = Path.GetDirectoryName(versionDir);
            return parent != null && Path.GetFileName(parent) == "archived_versions" ? Path.GetDirectoryName(parent)! : versionDir;
        }

        /// Where Moono installs itself, as its own installers choose: ONI_MOONO_INSTALL_ROOT when
        /// set; on Windows %LOCALAPPDATA%\oni-moono\versioned (the native host); on Linux the
        /// folder ci/workshop/OniMoono.sh picks, a sandboxed Steam's own when the item is in one.
        public static string DefaultRoot(string itemRoot, bool windows, Func<string, string?> env, string localAppData)
        {
            if (env(RootVariable) is { Length: > 0 } set) return set;
            if (windows) return Path.Combine(localAppData, "oni-moono", "versioned");
            var home = env("HOME") ?? "";
            if (home.Length > 0 && itemRoot.StartsWith(home + "/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal))
                return home + "/.var/app/com.valvesoftware.Steam/data/oni-moono/versioned";
            if (home.Length > 0 && itemRoot.StartsWith(home + "/snap/steam/", StringComparison.Ordinal))
                return home + "/snap/steam/common/oni-moono/versioned";
            var data = env("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : home + "/.local/share";
            return data + "/oni-moono/versioned";
        }

        public string Platform => Windows ? "windows-x64" : "linux-x64";
        public string EntryPath => Path.Combine(Root, Windows ? "OniMoono.exe" : "OniMoono");

        /// The line for ONI's launch options in Steam, the form setup.cmd and setup.sh use.
        public string LaunchOption => $"\"{EntryPath}\" -- %command%";

        /// What moono.json says of this version: its name and this platform's folder and manifest.
        public sealed class VersionInfo
        {
            public string Version = "", Folder = "", Manifest = "";
        }

        static readonly Regex item = new Regex("\"workshopItem\"\\s*:\\s*\"oni-moono\"");
        static readonly Regex version = new Regex("\"version\"\\s*:\\s*\"([A-Za-z0-9.-]{1,64})\"");

        /// moono.json as scripts/workshop-item.py writes it, or null with why not.
        public static VersionInfo? ReadInfo(string versionDir, string platform, out string? problem)
        {
            problem = null;
            string text;
            try { text = File.ReadAllText(Path.Combine(versionDir, PlayerFiles.MarkerFile)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                problem = $"{PlayerFiles.MarkerFile} can't be read: {e.Message}";
                return null;
            }
            var v = version.Match(text);
            var block = Regex.Match(text, "\"" + Regex.Escape(platform) + "\"\\s*:\\s*\\{([^}]*)\\}");
            string Field(string name) => Regex.Match(block.Groups[1].Value, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;
            if (!item.IsMatch(text) || !v.Success || v.Groups[1].Value[0] == '.')
            {
                problem = $"{PlayerFiles.MarkerFile} doesn't name a version of Moono";
                return null;
            }
            var info = new VersionInfo { Version = v.Groups[1].Value, Folder = Field("folder"), Manifest = Field("manifest") };
            if (!block.Success || !PlainName(info.Folder) || !PlainName(info.Manifest))
            {
                problem = $"version {info.Version} has no {platform} player";
                return null;
            }
            return info;
        }

        static bool PlainName(string name) =>
            name.Length > 0 && name != "." && name != ".." && name.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_');

        /// Installs the version when it isn't yet, the entry point, and the state Moono reads.
        /// Returns what was done, or throws Failure with why not.
        public string Install(int processId, Action<string>? makeExecutable)
        {
            var info = ReadInfo(VersionDir, Platform, out var problem) ?? throw new Failure(problem!);
            var playerDir = Path.Combine(Root, "player");
            var installed = Path.Combine(playerDir, info.Version);
            string done;
            if (Directory.Exists(installed)) done = $"Moono {info.Version} is installed in {Root}";
            else
            {
                byte[] manifest, marker, modInfo;
                try
                {
                    manifest = File.ReadAllBytes(Path.Combine(VersionDir, info.Manifest));
                    marker = File.ReadAllBytes(Path.Combine(VersionDir, PlayerFiles.MarkerFile));
                    modInfo = File.ReadAllBytes(Path.Combine(VersionDir, PlayerFiles.ModInfoFile));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { throw new Failure($"its manifest can't be read (the download is incomplete?): {e.Message}"); }
                problem = PlayerFiles.Copy(Path.Combine(VersionDir, info.Folder), manifest, info.Manifest, marker, modInfo, playerDir, info.Version,
                    Windows ? "OniMoono.exe" : "OniMoono", processId, makeExecutable);
                if (problem != null) throw new Failure(problem);
                done = $"Moono {info.Version} installed in {Root}";
            }
            try
            {
                InstallEntry(installed, makeExecutable);
                var state = Path.Combine(Root, "state");
                Directory.CreateDirectory(state);
                File.WriteAllText(Path.Combine(state, "source"), ItemRoot + "\n");
                File.WriteAllText(Path.Combine(state, "current"), info.Version + "\n");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { throw new Failure(e.Message); }
            return done;
        }

        /// The entry point, as Entry.Install puts it: on Windows the version's OniMoono.exe (an
        /// older one renamed to OniMoono.exe.old first), on Linux the shell script.
        void InstallEntry(string versionDir, Action<string>? makeExecutable)
        {
            var entry = EntryPath;
            if (Windows)
            {
                var fresh = Path.Combine(versionDir, "OniMoono.exe");
                if (File.Exists(entry) && File.ReadAllBytes(entry).SequenceEqual(File.ReadAllBytes(fresh))) return;
                if (File.Exists(entry))
                {
                    if (File.Exists(entry + ".old")) File.Delete(entry + ".old");
                    File.Move(entry, entry + ".old");
                }
                File.Copy(fresh, entry);
            }
            else
            {
                if (File.Exists(entry) && File.ReadAllText(entry) == PlayerFiles.EntryScript) return;
                var temp = entry + ".new";
                File.WriteAllText(temp, PlayerFiles.EntryScript);
                makeExecutable?.Invoke(temp);
                if (File.Exists(entry)) File.Delete(entry);
                File.Move(temp, entry);
            }
        }

        public sealed class Failure : Exception
        {
            public Failure(string message) : base(message) { }
        }
    }
}
