#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace OniMoono.Host.Install
{
    /// Moono installed one folder per version (docs/INSTALL-MODES.md), as a Workshop item would
    /// install it:
    ///
    ///     ROOT/OniMoono.exe (Windows) or ROOT/OniMoono (Linux)   the entry point
    ///     ROOT/player/VERSION/                                   one player per version
    ///     ROOT/state/current, source, pin, switch                what the entry point and this read
    ///
    /// A source folder is laid out as the Workshop item: a root and archived_versions/*, each a
    /// version with mod_info.yaml, moono.json (MoonoItemMarker: its version and, per platform, the
    /// folder holding the release archive's oni-moono folder and its SHA256SUMS manifest). This
    /// chooses a version as the game does (ArchiveChoice); verifies the item's signature
    /// (ItemTrust: its Sigstore bundle, offline, and its SHA256SUMS, which lists the version's
    /// manifest, mod_info.yaml and moono.json); refuses a version older than one installed of its
    /// line unless pinned; copies its player into a new version folder while checking every file
    /// against the manifest, and only then moves the folder in place: a version folder is never
    /// written once it is there, so one in use is never touched. A refused item or a copy that
    /// fails its check installs nothing, and the current version stays.
    internal sealed class VersionedInstall
    {
        public const string RootVariable = "ONI_MOONO_INSTALL_ROOT", SourceVariable = "ONI_MOONO_INSTALL_SOURCE";
        /// The exit code that asks the entry point to start the version state/current names.
        public const int SwitchExitCode = 77;
        public const string BootstrapPrefix = ".bootstrap-", IncomingPrefix = PlayerFiles.IncomingPrefix;
        public const string LockFile = ".in-use";
        /// Versions kept besides the current one.
        public const int KeepPrevious = 2;

        public readonly string Root;
        readonly string platform;
        readonly Action<string> log;
        readonly Sigstore.TrustedRoot? trust;
        readonly Sigstore.Identity identity;

        /// `trust` and `identity`: whom a source folder's signature must chain to and name; by
        /// default Sigstore's public-good root embedded in Moono and Moono's Workshop workflow.
        public VersionedInstall(string root, string platform, Action<string> log, Sigstore.TrustedRoot? trust = null, Sigstore.Identity? identity = null)
        {
            Root = Path.GetFullPath(root);
            this.platform = platform;
            this.log = log;
            this.trust = trust;
            this.identity = identity ?? Sigstore.MoonoWorkshop;
        }

        const string NotSigned = "the Workshop item's files are not the ones Moono's releases signed";

        /// Why InstallFrom last refused a source's version, in a line for the player; null when it didn't.
        public string? Refusal { get; private set; }

        /// This machine's platform folder name in a source version: windows-x64 or linux-x64.
        public static string ThisPlatform => OperatingSystem.IsWindows() ? "windows-x64" : OperatingSystem.IsMacOS() ? "osx-x64" : "linux-x64";

        public string PlayerDir => Path.Combine(Root, "player");
        public string StateDir => Path.Combine(Root, "state");
        string State(string name) => Path.Combine(StateDir, name);

        public string? ReadState(string name)
        {
            try { return File.Exists(State(name)) ? File.ReadAllText(State(name)).Trim() : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        public void WriteState(string name, string? value)
        {
            Directory.CreateDirectory(StateDir);
            if (value == null) { File.Delete(State(name)); return; }
            var temp = State(name + ".tmp");
            File.WriteAllText(temp, value + "\n");
            File.Move(temp, State(name), overwrite: true);
        }

        /// moono.json of a version: what VersionedInstall needs of it.
        internal sealed class ItemInfo
        {
            public string? WorkshopItem { get; set; }
            public string? Version { get; set; }
            public Dictionary<string, PlatformInfo>? Platforms { get; set; }

            public sealed class PlatformInfo
            {
                public string? Folder { get; set; }
                public string? Manifest { get; set; }
            }

            static readonly JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };

            public static ItemInfo? Read(string dir)
            {
                try { return Parse(File.ReadAllBytes(Path.Combine(dir, OniMoono.GameTweaks.MoonoItemMarker.FileName))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            }

            public static ItemInfo? Parse(byte[] json)
            {
                try
                {
                    var info = JsonSerializer.Deserialize<ItemInfo>(json, options);
                    return info is { WorkshopItem: OniMoono.GameTweaks.MoonoItemMarker.Item, Version: { Length: > 0 } v } && IsVersionName(v) ? info : null;
                }
                catch (JsonException) { return null; }
            }
        }

        /// A version's name is a folder name: digits, letters, dots and dashes, not starting with a dot.
        public static bool IsVersionName(string name) =>
            name.Length is > 0 and <= 64 && name[0] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');

        /// Newer first: by the version's numbers (2026.11.08.01), then by name.
        public static int CompareVersions(string a, string b)
        {
            static long[] Numbers(string v) => v.Split('-')[0].Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToArray();
            var x = Numbers(a);
            var y = Numbers(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                long p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
                if (p != q) return p.CompareTo(q);
            }
            return string.CompareOrdinal(a, b);
        }

        /// A version's line: what follows its numbers (`-u60`), empty for master's. Versions are
        /// newer or older only within a line: the U59 and U60 builds of a release differ in it.
        public static string Line(string version) => version.IndexOf('-') is int dash and >= 0 ? version.Substring(dash) : "";

        /// The newest installed version of `version`'s line that is newer than it, if any.
        public string? NewerInstalled(string version) =>
            InstalledVersions().Where(v => Line(v.Name) == Line(version) && CompareVersions(v.Name, version) > 0).Select(v => v.Name).FirstOrDefault();

        /// An installed version: its name, folder and mod_info.yaml.
        public sealed record Installed(string Name, string Dir, ModInfo Info);

        public IReadOnlyList<Installed> InstalledVersions()
        {
            var found = new List<Installed>();
            if (!Directory.Exists(PlayerDir)) return found;
            foreach (var dir in Directory.GetDirectories(PlayerDir))
            {
                var name = Path.GetFileName(dir);
                if (!IsVersionName(name)) continue;
                if (ModInfo.ReadFolder(dir) is { } info && File.Exists(Path.Combine(dir, EntryFileInPlayer))) found.Add(new Installed(name, dir, info));
            }
            found.Sort((a, b) => CompareVersions(b.Name, a.Name));
            return found;
        }

        /// The player's executable in a version folder, the one the entry point starts.
        public static string EntryFileInPlayer => OperatingSystem.IsWindows() ? "OniMoono.exe" : "OniMoono";

        /// The installed version to play for the game's build: the game's rule over the
        /// installed versions (the highest minimumSupportedBuild at or below the build; of
        /// equals the newest), or the pinned one (state/pin) while it is installed.
        public Installed? Choose(long gameBuild, IReadOnlySet<string> subscribedDlc)
        {
            var installed = InstalledVersions();
            if (ReadState("pin") is { Length: > 0 } pin)
            {
                if (installed.FirstOrDefault(v => v.Name == pin) is { } pinned) return pinned;
                log($"install: the pinned version {pin} isn't installed; choosing as usual");
            }
            return installed
                .Where(v => v.Info.DlcAllowed(subscribedDlc) && v.Info.ApiVersion is 2 or 0 && v.Info.MinimumSupportedBuild <= gameBuild)
                .OrderByDescending(v => v.Info.MinimumSupportedBuild)
                .FirstOrDefault(); // InstalledVersions is newest first, and the order is stable
        }

        /// The outcome of looking at the source folder.
        public enum SourceResult { NoSource, NoMatch, AlreadyInstalled, Installed, Failed, Refused }

        /// Installs the source's version for the game's build when it isn't installed yet.
        public SourceResult InstallFrom(string source, long gameBuild, IReadOnlySet<string> subscribedDlc, out string? version)
        {
            version = null;
            if (!Directory.Exists(source)) return SourceResult.NoSource;
            var chosen = ArchiveChoice.MostSuitable(source, gameBuild, subscribedDlc);
            if (chosen == null)
            {
                log($"install: {source} has no version for game build {gameBuild}");
                return SourceResult.NoMatch;
            }
            var item = ItemInfo.Read(chosen.Dir);
            if (item == null)
            {
                log($"install: {chosen.Dir} has no moono.json naming its version; not installed");
                return SourceResult.Failed;
            }
            version = item.Version!;
            Refusal = null;
            if (Directory.Exists(Path.Combine(PlayerDir, version))) return SourceResult.AlreadyInstalled;

            // The item's signature, then what it vouches for: this version's moono.json and
            // mod_info.yaml (read once, checked, and those bytes used), then its manifest in Copy.
            ItemTrust trusted;
            byte[] marker, modInfo;
            var prefix = chosen.RelativePath.Length == 0 ? "" : chosen.RelativePath.Replace(Path.DirectorySeparatorChar, '/') + "/";
            try
            {
                trusted = ItemTrust.Verify(source, trust ?? Sigstore.TrustedRoot.Embedded, identity);
                try
                {
                    marker = File.ReadAllBytes(Path.Combine(chosen.Dir, OniMoono.GameTweaks.MoonoItemMarker.FileName));
                    modInfo = File.ReadAllBytes(Path.Combine(chosen.Dir, ModInfo.FileName));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ItemTrust.Refusal($"{prefix}{ModInfo.FileName} or moono.json can't be read: {e.Message}"); }
                trusted.Require(prefix + OniMoono.GameTweaks.MoonoItemMarker.FileName, marker);
                trusted.Require(prefix + ModInfo.FileName, modInfo);
                item = ItemInfo.Parse(marker);
                if (item?.Version != version) throw new ItemTrust.Refusal($"{prefix}moono.json changed while it was read");
            }
            catch (ItemTrust.Refusal r)
            {
                log($"install: version {version} from {source} refused: the Workshop item is not as Moono's release workflow signed it: {r.Message}");
                Refusal = NotSigned;
                return SourceResult.Refused;
            }
            log($"install: {source}: signature verified (Sigstore, {identity.SourceRepository}), SHA256SUMS {trusted.ManifestDigest}");

            // No rollback: an older version of the line than one installed only when pinned.
            if (NewerInstalled(version) is { } newer && ReadState("pin") != version)
            {
                log($"install: version {version} from {source} refused: {newer} is installed, and {version} is older (--use-version {version} installs it)");
                Refusal = $"its version {version} is older than {newer}, already installed";
                return SourceResult.Refused;
            }
            if (item.Platforms?.GetValueOrDefault(platform) is not { Folder: { Length: > 0 } folder, Manifest: { Length: > 0 } manifest })
            {
                log($"install: version {version} has no {platform} player");
                return SourceResult.NoMatch;
            }
            if (!IsPlainRelative(folder) || !IsPlainRelative(manifest))
            {
                log($"install: version {version}'s moono.json names a folder or manifest outside it; not installed");
                return SourceResult.Failed;
            }
            byte[] manifestBytes;
            try { manifestBytes = File.ReadAllBytes(Path.Combine(chosen.Dir, manifest)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"install: version {version} from {chosen.Dir} not installed: its manifest {manifest} can't be read (the download is incomplete?): {e.Message}");
                return SourceResult.Failed;
            }
            try { trusted.Require(prefix + manifest, manifestBytes); }
            catch (ItemTrust.Refusal r)
            {
                log($"install: version {version} from {source} refused: the Workshop item is not as Moono's release workflow signed it: {r.Message}");
                Refusal = NotSigned;
                return SourceResult.Refused;
            }
            string? problem = Copy(Path.Combine(chosen.Dir, folder), manifestBytes, manifest, marker, modInfo, version);
            if (problem != null)
            {
                log($"install: version {version} from {chosen.Dir} not installed: {problem}");
                return SourceResult.Failed;
            }
            log($"install: version {version} installed from {chosen.Dir} (game build {gameBuild}, its minimumSupportedBuild {chosen.Info.MinimumSupportedBuild})");
            return SourceResult.Installed;
        }

        /// The manifest's lines: SHA-256 (hex) and path, in sha256sum's format.
        public static List<(string Hash, string Path)>? ParseManifest(byte[] manifest) => PlayerFiles.ParseManifest(manifest);

        /// A manifest path that stays inside its folder: relative, `/`-separated, no `.` or `..`.
        public static bool IsPlainRelative(string path) => PlayerFiles.IsPlainRelative(path);

        /// Copies the manifest's player/ files from `platformDir` into player/VERSION, every file
        /// checked against the manifest (PlayerFiles.Copy, which the Workshop item's loader mod
        /// shares); null when it is in place, else why not. `marker` and `modInfo`: the version's
        /// moono.json and mod_info.yaml, as checked.
        string? Copy(string platformDir, byte[] manifestBytes, string manifestName, byte[] marker, byte[] modInfo, string version) =>
            PlayerFiles.Copy(platformDir, manifestBytes, manifestName, marker, modInfo, PlayerDir, version, EntryFileInPlayer, Environment.ProcessId,
                OperatingSystem.IsWindows() ? null : MarkExecutable);

        /// Linux: executables (ELF files and scripts) are made executable, as a Workshop download
        /// need not keep the mode.
        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        static void MarkExecutable(string path) =>
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        /// Held open while a version plays (FileShare: other players can hold it too; pruning
        /// opens it alone, which fails while any holds it). The runtime keeps the stream.
        public static FileStream? Hold(string versionDir)
        {
            try { return new FileStream(Path.Combine(versionDir, LockFile), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        static bool InUse(string versionDir)
        {
            var path = Path.Combine(versionDir, LockFile);
            if (!File.Exists(path)) return false;
            try
            {
                using var alone = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        /// Deletes the versions beyond the current one and the KeepPrevious newest others, and
        /// leftover bootstrap and incoming folders; never one in use (its lock is held).
        public void Prune(string current)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal) { current };
            foreach (var v in InstalledVersions().Where(v => v.Name != current).Take(KeepPrevious)) keep.Add(v.Name);
            if (ReadState("pin") is { Length: > 0 } pin) keep.Add(pin);
            if (!Directory.Exists(PlayerDir)) return;
            foreach (var dir in Directory.GetDirectories(PlayerDir))
            {
                var name = Path.GetFileName(dir);
                bool leftover = name.StartsWith(BootstrapPrefix, StringComparison.Ordinal) || name.StartsWith(IncomingPrefix, StringComparison.Ordinal);
                if (!leftover && (keep.Contains(name) || !IsVersionName(name))) continue;
                if (name.StartsWith(IncomingPrefix, StringComparison.Ordinal) && name.EndsWith("-" + Environment.ProcessId, StringComparison.Ordinal)) continue;
                if (InUse(dir)) continue;
                if (leftover && !name.StartsWith(IncomingPrefix, StringComparison.Ordinal) && Directory.GetLastWriteTimeUtc(dir) > DateTime.UtcNow.AddMinutes(-1)) continue;
                TryDelete(dir);
                if (!Directory.Exists(dir) && !leftover) log($"install: removed version {name}");
            }
        }
    }
}
