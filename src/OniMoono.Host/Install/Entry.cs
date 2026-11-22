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
    ///   is renamed to OniMoono.exe.old, which the next start deletes. The same exe goes in as
    ///   Restarter.exe too, for App.Restart (PlayerFiles.InstallWindowsEntry).
    /// - Linux: the version's own executable (host_linux.c), which picks the version itself when
    ///   it has player/ beside it, else a shell script (Script) that does the same, for a version
    ///   built with the SDK's apphost; written to a temporary file and renamed over the old one, so
    ///   a running entry point keeps the file it opened (PlayerFiles.InstallLinuxEntry).
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
                    if (!PlayerFiles.InstallWindowsEntry(install.Root, versionDir)) return;
                }
                else if (!PlayerFiles.InstallLinuxEntry(install.Root, versionDir, MakeExecutable)) return;
                log.Add($"install: entry point {entry} updated");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Add($"install: entry point {entry} not updated: {e.Message}");
            }
        }

        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        static void MakeExecutable(string path) => File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);

        /// The Linux entry point's fallback script (shared with the Workshop item's loader mod).
        public const string Script = PlayerFiles.EntryScript;
    }
}
