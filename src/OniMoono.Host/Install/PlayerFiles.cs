#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OniMoono.Host.Install
{
    /// Installing one version's player from a source folder (VersionedInstall), as Moono's
    /// installer and the Workshop item's loader mod (src/MoonoLoader, which the original game runs
    /// on Unity's Mono) both do it: one source file compiled into both, so it keeps to what
    /// netstandard2.1 has. A version's moono.json names, per platform, the folder holding the
    /// release archive's oni-moono folder and its SHA256SUMS manifest; the manifest's player/
    /// files are copied into a new folder, each hashed as it is read, and the folder is moved to
    /// player/VERSION only when every one matched.
    internal static class PlayerFiles
    {
        public const string IncomingPrefix = ".incoming-";
        public const string ModInfoFile = "mod_info.yaml", MarkerFile = "moono.json";
        /// The version's release manifest, as checked while copying, kept in the installed version's
        /// folder: what MissingFiles looks for at each start.
        public const string SavedManifestFile = "SHA256SUMS";

        /// The manifest's lines: SHA-256 (hex) and path, in sha256sum's format.
        public static List<(string Hash, string Path)>? ParseManifest(byte[] manifest)
        {
            var lines = new List<(string, string)>();
            foreach (var line in Encoding.UTF8.GetString(manifest).Split('\n'))
            {
                var raw = line.TrimEnd('\r');
                if (raw.Length == 0) continue;
                if (raw.Length < 66 || raw[64] != ' ' || (raw[65] != ' ' && raw[65] != '*')) return null;
                var hash = raw.Substring(0, 64);
                if (!hash.All(Uri.IsHexDigit)) return null;
                lines.Add((hash.ToLowerInvariant(), raw.Substring(66)));
            }
            return lines;
        }

        /// Which version is newer, by the version's numbers (2026.11.08.01), then by name:
        /// above 0 when `a` is.
        public static int CompareVersions(string a, string b)
        {
            long[] Numbers(string v) => v.Split('-')[0].Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToArray();
            var x = Numbers(a);
            var y = Numbers(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                long p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
                if (p != q) return p.CompareTo(q);
            }
            return string.CompareOrdinal(a, b);
        }

        /// A manifest path that stays inside its folder: relative, `/`-separated, no `.` or `..`.
        public static bool IsPlainRelative(string path) =>
            path.Length > 0 && path[0] != '/' && path.IndexOf('\\') < 0 && path.IndexOf(':') < 0
            && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

        /// The player files the installed version at `versionDir` lacks, by the release manifest
        /// saved there at install (paths relative to the folder): existence only, nothing hashed.
        /// Null when it has no saved manifest (installed before Moono kept one) or it can't be read.
        public static List<string>? MissingFiles(string versionDir)
        {
            List<(string Hash, string Path)>? lines;
            try
            {
                var path = Path.Combine(versionDir, SavedManifestFile);
                if (!File.Exists(path)) return null;
                lines = ParseManifest(File.ReadAllBytes(path));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            if (lines == null) return null;
            var missing = new List<string>();
            foreach (var (_, path) in lines)
            {
                if (!path.StartsWith("player/", StringComparison.Ordinal) || !IsPlainRelative(path)) continue;
                var relative = path.Substring("player/".Length).Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(Path.Combine(versionDir, relative))) missing.Add(relative);
            }
            return missing;
        }

        /// Copies the manifest's player/ files from `platformDir` into a new folder of
        /// `playerDir`, hashing each as it is read, writes the version's mod_info.yaml and
        /// moono.json beside them (`modInfo`, `marker`: as the caller read and checked them), and
        /// moves the folder to playerDir/VERSION when every file matched; else deletes it and
        /// returns why. `entryFile` is the player's executable, which the manifest must list;
        /// `makeExecutable` (Linux) is given each ELF file and script, as a Workshop download need
        /// not keep their mode.
        public static string? Copy(string platformDir, byte[] manifest, string manifestName, byte[] marker, byte[] modInfo, string playerDir, string version,
            string entryFile, int processId, Action<string>? makeExecutable)
        {
            var lines = ParseManifest(manifest);
            if (lines == null) return $"its manifest {manifestName} is malformed";
            var player = lines.Where(l => l.Path.StartsWith("player/", StringComparison.Ordinal)).ToList();
            if (player.Count == 0) return "its manifest lists no player files";
            var bad = player.FirstOrDefault(l => !IsPlainRelative(l.Path)).Path;
            if (bad != null) return $"its manifest names {bad}, outside the folder";
            if (!player.Any(l => l.Path == "player/" + entryFile)) return $"its manifest has no player/{entryFile}";

            var incoming = Path.Combine(playerDir, $"{IncomingPrefix}{version}-{processId}");
            try
            {
                Directory.CreateDirectory(playerDir);
                if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
                Directory.CreateDirectory(incoming);
                var buffer = new byte[1 << 16];
                foreach (var (hash, path) in player)
                {
                    var from = Path.Combine(platformDir, path);
                    var to = Path.Combine(incoming, path.Substring("player/".Length));
                    if (!File.Exists(from)) return $"{path} is missing (the download is incomplete)";
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    string got;
                    using (var input = File.OpenRead(from))
                    using (var output = File.Create(to))
                    using (var sha = SHA256.Create())
                    {
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            sha.TransformBlock(buffer, 0, n, null, 0);
                            output.Write(buffer, 0, n);
                        }
                        sha.TransformFinalBlock(buffer, 0, 0);
                        got = Hex(sha.Hash!);
                    }
                    if (got != hash) return $"{path} doesn't match the manifest";
                    if (makeExecutable != null && IsExecutable(to)) makeExecutable(to);
                }
                // What choosing among installed versions reads, beside the player.
                File.WriteAllBytes(Path.Combine(incoming, ModInfoFile), modInfo);
                File.WriteAllBytes(Path.Combine(incoming, MarkerFile), marker);
                File.WriteAllBytes(Path.Combine(incoming, SavedManifestFile), manifest);
                Directory.Move(incoming, Path.Combine(playerDir, version));
                incoming = "";
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return e.Message;
            }
            finally
            {
                if (incoming.Length > 0)
                {
                    try { Directory.Delete(incoming, recursive: true); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
            }
        }

        /// An ELF file or a script (`#!`): what is made executable on Linux.
        public static bool IsExecutable(string path)
        {
            var head = new byte[4];
            int n;
            using (var f = File.OpenRead(path))
                n = f.Read(head, 0, 4);
            bool elf = n == 4 && head[0] == 0x7f && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';
            bool script = n >= 2 && head[0] == (byte)'#' && head[1] == (byte)'!';
            return elf || script;
        }

        static string Hex(byte[] bytes)
        {
            var s = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) s.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return s.ToString();
        }

        /// The Windows entry point's files (Entry): ROOT\OniMoono.exe, and ROOT\Restarter.exe for
        /// the game's App.Restart, which starts Restarter.exe from the folder of the process's
        /// executable, here the entry point's. Both are the version's OniMoono.exe, the native
        /// host: named Restarter.exe it runs the installed version's Restarter.
        public static readonly string[] WindowsEntryFiles = { "OniMoono.exe", "Restarter.exe" };

        /// Puts the version's OniMoono.exe in place as each of WindowsEntryFiles in `root`. One
        /// that differs is renamed to NAME.old first (a running exe can be renamed, not
        /// overwritten), which the entry point deletes at its next start. True when one changed.
        public static bool InstallWindowsEntry(string root, string versionDir)
        {
            var fresh = Path.Combine(versionDir, "OniMoono.exe");
            var bytes = File.ReadAllBytes(fresh);
            bool changed = false;
            foreach (var name in WindowsEntryFiles)
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) continue;
                if (File.Exists(path))
                {
                    if (File.Exists(path + ".old")) File.Delete(path + ".old");
                    File.Move(path, path + ".old");
                }
                File.Copy(fresh, path);
                changed = true;
            }
            return changed;
        }

        /// Whether the file at `path` is Moono's own Linux executable (src/OniMoono.NativeHost,
        /// host_linux.c), which is the entry point itself when it has player/ beside it: an ELF file
        /// that exports the native crash records' functions. The SDK's apphost is not.
        public static bool IsNativeLauncher(string path)
        {
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 4 || bytes[0] != 0x7f || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F') return false;
            var name = Encoding.ASCII.GetBytes("moono_crash_install\0");
            for (int i = 0; i + name.Length <= bytes.Length; i++)
            {
                int k = 0;
                while (k < name.Length && bytes[i + k] == name[k]) k++;
                if (k == name.Length) return true;
            }
            return false;
        }

        /// The Linux entry point, ROOT/OniMoono: the version's own executable when it is Moono's
        /// (IsNativeLauncher: with no OniMoono.dll beside it, it picks the version itself), else the
        /// shell script (EntryScript), as before it. Written to ROOT/OniMoono.new and renamed over
        /// the old one: a running entry point keeps the file it started from. True when it changed.
        public static bool InstallLinuxEntry(string root, string versionDir, Action<string>? makeExecutable)
        {
            var entry = Path.Combine(root, "OniMoono");
            var launcher = Path.Combine(versionDir, "OniMoono");
            var fresh = IsNativeLauncher(launcher) ? File.ReadAllBytes(launcher) : Encoding.UTF8.GetBytes(EntryScript);
            if (File.Exists(entry) && File.ReadAllBytes(entry).SequenceEqual(fresh)) return false;
            var temp = entry + ".new";
            File.WriteAllBytes(temp, fresh);
            makeExecutable?.Invoke(temp);
            if (File.Exists(entry)) File.Delete(entry);
            File.Move(temp, entry);
            return true;
        }

        /// The Linux entry point's fallback, ROOT/OniMoono as a shell script (InstallLinuxEntry), for
        /// a version whose OniMoono is the SDK's apphost (a player built by setup.sh with no C
        /// compiler).
        public const string EntryScript = """
            #!/bin/sh
            # Moono's entry point (docs/INSTALL-MODES.md): starts the installed player that
            # state/current names, and the next one when it exits asking for a switch (77).
            root="$(cd "$(dirname "$0")" && pwd)"
            export ONI_MOONO_INSTALL_ROOT="$root"
            unset ONI_MOONO_INSTALL_SOURCE
            switches=0
            while :; do
                version="$(cat "$root/state/current" 2>/dev/null)"
                case "$version" in
                    ""|.*|*/*) version="$(ls -1 "$root/player" 2>/dev/null | grep -v '^\.' | sort | tail -n 1)" ;;
                esac
                if [ -z "$version" ] || [ ! -x "$root/player/$version/OniMoono" ]; then
                    echo "Moono: no installed player in $root/player" >&2
                    exit 1
                fi
                "$root/player/$version/OniMoono" "$@"
                status=$?
                if [ "$status" -eq 77 ] && [ -f "$root/state/switch" ] && [ "$switches" -lt 3 ]; then
                    rm -f "$root/state/switch"
                    switches=$((switches + 1))
                    continue
                fi
                exit "$status"
            done

            """;
    }
}
