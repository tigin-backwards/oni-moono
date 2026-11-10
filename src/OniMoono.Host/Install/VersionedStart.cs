#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OniMoono.Host.Install
{
    /// A start of a versioned install (VersionedInstall; the entry point sets
    /// ONI_MOONO_INSTALL_ROOT), before anything of the game loads: install the source folder's
    /// version for the game's build when it is new, put the entry point in place, then play the
    /// installed version chosen for the build. When that is not the running one (a bootstrap copy started from the source folder, or another game build),
    /// state/current names it and the exit code asks the entry point to start it.
    internal static class VersionedStart
    {
        /// Kept open while this version plays, so pruning leaves it alone.
        static FileStream? held;

        /// The DLC subscribed is unknown before the game starts: versions are chosen as for a
        /// game with none, which only matters to one whose mod_info.yaml names DLC (Moono's don't).
        static readonly IReadOnlySet<string> NoDlc = new HashSet<string>();

        /// Null to go on playing this version, else the process's exit code. `useVersion`:
        /// `--use-version VERSION|latest`, pinning a version by hand (rollback) or clearing it.
        public static int? Run(string root, string gameDir, string? useVersion, List<string> log)
        {
            var install = new VersionedInstall(root, VersionedInstall.ThisPlatform, log.Add);
            var here = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string running = Path.GetFileName(here);
            bool inRoot = string.Equals(Path.GetDirectoryName(here), install.PlayerDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            bool bootstrap = running.StartsWith(VersionedInstall.BootstrapPrefix, StringComparison.Ordinal);
            if (!inRoot)
            {
                log.Add($"install: {VersionedInstall.RootVariable} is {root}, but this player runs from {here}; not a versioned start");
                return null;
            }

            var managed = OniMoono.Engine.Platform.GameLayout.Find(gameDir)?.DataDir is { } data ? Path.Combine(data, "Managed") : null;
            uint? build = managed != null ? GameBuild.Read(managed) : null;
            if (build == null)
            {
                log.Add($"install: the game's build at {gameDir} can't be read; playing {running}");
                return bootstrap ? Fail(log, "the game's build can't be read") : null;
            }
            if (useVersion != null)
            {
                install.WriteState("pin", useVersion == "latest" ? null : useVersion);
                log.Add(useVersion == "latest" ? "install: --use-version latest: no version pinned" : $"install: --use-version: {useVersion} pinned");
            }

            var source = Environment.GetEnvironmentVariable(VersionedInstall.SourceVariable);
            bool fromSource = !string.IsNullOrEmpty(source);
            if (fromSource) install.WriteState("source", Path.GetFullPath(source!));
            else source = install.ReadState("source");
            if (!string.IsNullOrEmpty(source))
            {
                var result = install.InstallFrom(source!, build.Value, NoDlc, out var version);
                if (result == VersionedInstall.SourceResult.NoSource) log.Add($"install: the source folder {source} is gone; no updates from it");
            }

            var chosen = install.Choose(build.Value, NoDlc);
            if (chosen == null)
            {
                log.Add($"install: no installed version is for game build {build}; playing {running}");
                return bootstrap ? Fail(log, $"no version for game build {build}") : null;
            }
            Entry.Install(install, chosen.Dir, log);
            install.WriteState("current", chosen.Name);
            if (chosen.Name != running)
            {
                install.WriteState("switch", chosen.Name);
                log.Add($"install: game build {build}: switching to version {chosen.Name}");
                return VersionedInstall.SwitchExitCode;
            }
            held = VersionedInstall.Hold(chosen.Dir);
            install.Prune(chosen.Name);
            log.Add($"install: game build {build}: playing version {chosen.Name} (installed: {string.Join(", ", install.InstalledVersions().Select(v => v.Name))})");
            return null;
        }

        static int Fail(List<string> log, string why)
        {
            log.Add($"install: nothing installed: {why}");
            Launch.StandaloneLaunch.Tell($"Moono could not be installed: {why}.", error: true, test: false);
            return 2;
        }
    }
}
