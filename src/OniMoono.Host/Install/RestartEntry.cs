#nullable enable
using System;
using System.IO;

namespace OniMoono.Host.Install
{
    /// What a restart starts (App.Restart, through the Restarter). App.Restart names the process's
    /// own executable. That is right for a player started from its own folder (launch options,
    /// standalone) and on Windows for one started through the versioned install's entry point,
    /// which hosts the player in its own process. On Linux the entry point is a script that
    /// starts the version's OniMoono, so App.Restart names that one, and a restart would skip
    /// the version choice. The host records the entry point in ONI_MOONO_RESTART_EXE for both
    /// Restarters, which start it in place of the executable they are given.
    internal static class RestartEntry
    {
        public const string Variable = "ONI_MOONO_RESTART_EXE";

        /// The entry point to start again: ROOT's when `playerDir` is one of the versioned
        /// install's version folders (ROOT/player/VERSION, not a bootstrap or incoming copy) and
        /// ROOT holds its entry point (`entryName`); otherwise null, and the Restarter starts
        /// the executable it is given. `installRoot`: ONI_MOONO_INSTALL_ROOT.
        public static string? For(string? installRoot, string playerDir, string entryName, bool windows)
        {
            if (string.IsNullOrEmpty(installRoot)) return null;
            var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
            var here = Path.GetFullPath(playerDir).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(here);
            if (!string.Equals(Path.GetDirectoryName(here), Path.Combine(root, "player"), comparison)) return null;
            if (name.StartsWith(VersionedInstall.BootstrapPrefix, StringComparison.Ordinal) || name.StartsWith(VersionedInstall.IncomingPrefix, StringComparison.Ordinal)) return null;
            var entry = Path.Combine(root, entryName);
            return File.Exists(entry) ? entry : null;
        }

        /// ONI_MOONO_RESTART_EXE for this process: set, or cleared (a player a restart started
        /// inherits its first run's).
        public static string? Record() =>
            Record(For(Environment.GetEnvironmentVariable(VersionedInstall.RootVariable), AppContext.BaseDirectory,
                VersionedInstall.EntryFileInPlayer, OperatingSystem.IsWindows()));

        static string? Record(string? entry)
        {
            Environment.SetEnvironmentVariable(Variable, entry);
            return entry;
        }
    }
}
