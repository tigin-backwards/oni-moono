#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OniMoono.Host.Install
{
    /// The versioned install's entry point, ROOT/OniMoono.exe or ROOT/OniMoono: what Steam's
    /// launch options and shortcuts the user makes start. It starts the version state/current
    /// names and starts again when that version's exit code asks for a switch (VersionedStart).
    /// - Windows: the native host (src/OniMoono.NativeHost) itself, copied from the chosen
    ///   version, which runs in "entry" mode when it has player/ beside it. The running exe can't
    ///   be overwritten but can be renamed: a new one goes in as OniMoono.exe after the old one
    ///   is renamed to OniMoono.exe.old, which the next start deletes.
    /// - Linux: a shell script (Script), written to a temporary file and renamed over the old
    ///   one; a running shell keeps reading the file it opened.
    internal static class Entry
    {
        public static string PathIn(string root) => Path.Combine(root, VersionedInstall.EntryFileInPlayer);

        public static void Install(VersionedInstall install, string versionDir, List<string> log)
        {
            var entry = PathIn(install.Root);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var fresh = Path.Combine(versionDir, "OniMoono.exe");
                    if (File.Exists(entry) && File.ReadAllBytes(entry).AsSpan().SequenceEqual(File.ReadAllBytes(fresh))) return;
                    if (File.Exists(entry)) File.Move(entry, entry + ".old", overwrite: true);
                    File.Copy(fresh, entry);
                }
                else
                {
                    if (File.Exists(entry) && File.ReadAllText(entry) == Script) return;
                    var temp = entry + ".new";
                    File.WriteAllText(temp, Script);
                    File.SetUnixFileMode(temp, (UnixFileMode)0b111_101_101);
                    File.Move(temp, entry, overwrite: true);
                }
                log.Add($"install: entry point {entry} updated");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Add($"install: entry point {entry} not updated: {e.Message}");
            }
        }

        /// The Linux entry point (shared with the Workshop item's loader mod).
        public const string Script = PlayerFiles.EntryScript;
    }
}
