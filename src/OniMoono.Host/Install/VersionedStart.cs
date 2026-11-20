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

        /// The Workshop item this install updates from (state/source) once Run chose to play an
        /// installed version the item still updates; null otherwise (no versioned start, or an
        /// item with no version for this game). The update check skips such installs.
        public static string? UpdatesFrom { get; private set; }

        /// The DLC subscribed is unknown before the game starts: versions are chosen as for a
        /// game with none, which only matters to one whose mod_info.yaml names DLC (Moono's don't).
        static readonly IReadOnlySet<string> NoDlc = new HashSet<string>();

        /// Null to go on playing this version, else the process's exit code. `useVersion`:
        /// `--use-version VERSION|latest`, pinning a version by hand (rollback) or clearing it.
        public static int? Run(string root, string gameDir, string? useVersion, List<string> log) =>
            Run(new VersionedInstall(root, VersionedInstall.ThisPlatform, log.Add), gameDir, useVersion, log, AppContext.BaseDirectory, Tell,
                () => SteamItem.Find(new Launch.SteamLibrary(), gameDir));

        static void Tell(string message, bool error) => Launch.StandaloneLaunch.Tell(message, error, test: false);

        /// `tell`: a message to the player (a dialog; error or not). `playerDir`: the running player's folder.
        /// `steamItems`: Steam's copies of the Workshop item (SteamItem.Find); none when null.
        public static int? Run(VersionedInstall install, string gameDir, string? useVersion, List<string> log, string playerDir, Action<string, bool> tell,
            Func<IReadOnlyList<SteamItem.Copy>>? steamItems = null)
        {
            UpdatesFrom = null;
            var here = Path.GetFullPath(playerDir).TrimEnd(Path.DirectorySeparatorChar);
            string running = Path.GetFileName(here);
            bool inRoot = string.Equals(Path.GetDirectoryName(here), install.PlayerDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            bool bootstrap = running.StartsWith(VersionedInstall.BootstrapPrefix, StringComparison.Ordinal);
            if (!inRoot)
            {
                log.Add($"install: {VersionedInstall.RootVariable} is {install.Root}, but this player runs from {here}; not a versioned start");
                return null;
            }

            var managed = OniMoono.Engine.Platform.GameLayout.Find(gameDir)?.DataDir is { } data ? Path.Combine(data, "Managed") : null;
            uint? build = managed != null ? GameBuild.Read(managed) : null;
            if (build == null)
            {
                log.Add($"install: the game's build at {gameDir} can't be read; playing {running}");
                return bootstrap ? Fail(log, tell, "the game's build can't be read") : null;
            }
            if (useVersion != null)
            {
                install.WriteState("pin", useVersion == "latest" ? null : useVersion);
                log.Add(useVersion == "latest" ? "install: --use-version latest: no version pinned" : $"install: --use-version: {useVersion} pinned");
            }

            // The folders to update from: the one this start was given (a first start from the
            // item) or the recorded one, and Steam's copies of the item. The recorded one can
            // be the game's copy of the item in its mods folder (the loader mod runs from
            // there), which only the original game refreshes: Steam's copy is recorded in its
            // place, so an install that began there heals at its next start.
            var given = Environment.GetEnvironmentVariable(VersionedInstall.SourceVariable);
            var recorded = install.ReadState("source");
            var sources = new List<(string Dir, bool Steam, VersionedInstall.SourceVersion? Version)>();
            void Add(string dir, bool steam)
            {
                dir = Path.GetFullPath(dir);
                if (!sources.Any(s => SamePath(s.Dir, dir))) sources.Add((dir, steam, install.VersionIn(dir, build.Value, NoDlc)));
            }
            // A copy Steam is still to update is left alone, unless this start was given it.
            var pending = new List<string>();
            IReadOnlyList<SteamItem.Copy> copies = [];
            try { copies = steamItems?.Invoke() ?? []; }
            catch (Exception e) { log.Add($"install: Steam's copy of the Workshop item wasn't looked for: {e.GetType().Name}: {e.Message}"); }
            foreach (var copy in copies)
            {
                if (copy.Pending == null || (!string.IsNullOrEmpty(given) && SamePath(copy.Dir, given))) Add(copy.Dir, steam: true);
                else
                {
                    pending.Add(copy.Dir);
                    log.Add($"install: Steam's copy of the Workshop item at {copy.Dir} isn't read this start: {copy.Pending}");
                }
            }
            var other = !string.IsNullOrEmpty(given) ? given : recorded;
            if (!string.IsNullOrEmpty(other) && !pending.Any(p => SamePath(p, other))) Add(other, steam: false);
            // The version that would play first (as Choose orders them); of equals Steam's copy.
            var ordered = sources.Where(s => s.Version != null)
                .OrderByDescending(s => s.Version!.MinimumSupportedBuild)
                .ThenByDescending(s => s.Version!.Name, Comparer<string>.Create(VersionedInstall.CompareVersions))
                .Concat(sources.Where(s => s.Version == null)).ToList();
            string? steamCopy = ordered.FirstOrDefault(s => s.Steam).Dir;
            var record = steamCopy ?? (string.IsNullOrEmpty(given) ? null : Path.GetFullPath(given));
            if (record != null && (recorded == null || !SamePath(recorded, record)))
            {
                install.WriteState("source", record);
                if (steamCopy != null) log.Add($"install: updates come from Steam's copy of the Workshop item, {steamCopy}{(string.IsNullOrEmpty(recorded) ? "" : $" (was {recorded})")}");
            }

            string? refused = null, refusal = null;
            int? needsBuild = null;
            bool looked = false;
            foreach (var (source, _, holds) in ordered)
            {
                // A version older than the one installed for this game would never play: not installed.
                if (holds != null && !install.WouldPlay(holds, build.Value, NoDlc))
                {
                    log.Add($"install: {source} holds version {holds.Name}, older than the installed one; not installed");
                    looked = true;
                    continue;
                }
                var result = install.InstallFrom(source, build.Value, NoDlc, out var version);
                if (result == VersionedInstall.SourceResult.NoSource) log.Add($"install: the source folder {source} is gone; no updates from it");
                if (result == VersionedInstall.SourceResult.Refused && refused == null)
                {
                    refusal = install.Refusal;
                    refused = $"Moono did not install {(version != null ? $"version {version}" : "an update")} from the Workshop item: {refusal}.";
                }
                if (install.SourceNeedsBuild is { } needs) needsBuild = Math.Min(needsBuild ?? needs, needs);
                else if (result != VersionedInstall.SourceResult.NoSource) looked = true;
                if (result is VersionedInstall.SourceResult.Installed or VersionedInstall.SourceResult.AlreadyInstalled) break;
            }
            // Told as an item for a newer game only when no folder had anything else to say.
            if (looked) needsBuild = null;

            var chosen = install.Choose(build.Value, NoDlc);
            if (chosen == null)
            {
                log.Add($"install: no installed version is for game build {build}; playing {running}");
                if (!bootstrap) return null;
                // A first start from an item that is for a newer game: where this game's Moono is.
                if (needsBuild != null)
                {
                    tell($"{OlderGame(build.Value, needsBuild.Value)} Install Moono for your game from a GitHub release instead: {ReleasesFor(build.Value)}", true);
                    return 2;
                }
                return Fail(log, tell, refused != null ? $"{refusal}" : $"no version for game build {build}");
            }
            // The item no longer has a version for this game (it carries the current game's build alone):
            // the installed version plays on, and the player is told once per game build
            // (state/no-updates) where its updates are. A game update to the item's build clears it.
            if (needsBuild != null)
            {
                if (install.ReadState("no-updates") != build.Value.ToString())
                {
                    install.WriteState("no-updates", build.Value.ToString());
                    tell($"{OlderGame(build.Value, needsBuild.Value)} Version {chosen.Name} plays on, without updates from the Workshop. Moono for your game is on GitHub: {ReleasesFor(build.Value)}", false);
                }
            }
            else if (install.ReadState("no-updates") != null) install.WriteState("no-updates", null);
            // A refused version is told once (state/refused), as the version installed plays on.
            if (refused != null && install.ReadState("refused") != refused)
            {
                install.WriteState("refused", refused);
                tell($"{refused} Version {chosen.Name} plays on; Player.log says why.", true);
            }
            // A version missing files of its saved manifest doesn't start: the player is told
            // which, and how to repair it.
            if (VersionedInstall.MissingFiles(chosen.Dir) is { Count: > 0 } missing)
            {
                log.Add($"install: version {chosen.Name} at {chosen.Dir} is missing {missing.Count} files of its manifest: {string.Join(", ", missing)}");
                tell(MissingMessage(chosen.Name, chosen.Dir, missing), true);
                return 2;
            }
            Entry.Install(install, chosen.Dir, log);
            install.WriteState("current", chosen.Name);
            if (chosen.Name != running)
            {
                install.WriteState("switch", chosen.Name);
                log.Add($"install: game build {build}: switching to version {chosen.Name}");
                return VersionedInstall.SwitchExitCode;
            }
            held?.Dispose();
            held = VersionedInstall.Hold(chosen.Dir);
            install.Prune(chosen.Name, build.Value, NoDlc);
            UpdatesFrom = needsBuild == null ? install.ReadState("source") : null;
            log.Add($"install: game build {build}: playing version {chosen.Name} (installed: {string.Join(", ", install.InstalledVersions().Select(v => v.Name))})");
            return null;
        }

        static bool SamePath(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        /// What the player is told when an installed version is missing files: up to five of them.
        public static string MissingMessage(string version, string versionDir, IReadOnlyList<string> missing)
        {
            var lines = new List<string> { $"Moono's installed version {version} is missing {missing.Count} files:" };
            lines.AddRange(missing.Take(5));
            if (missing.Count > 5) lines.Add($"…and {missing.Count - 5} more (Player.log lists them all).");
            lines.Add("");
            lines.Add($"To repair, delete {Path.GetFullPath(versionDir)} and start the game again.");
            return string.Join("\n", lines);
        }

        /// What a player whose game is older than every version in the Workshop item is told first.
        static string OlderGame(uint build, int needsBuild) =>
            $"Moono's Workshop item is for {Update(needsBuild)} of Oxygen Not Included and later. Your game is {Update((int)build)}.";

        /// Where Moono for a game build is: U59's last release (the frozen u59 line's), else the
        /// releases.
        static string ReleasesFor(uint build) => build is >= 744825 and < 762230 ? VersionedInstall.ReleasesUrl + "/tag/v2026.11.19.03" : VersionedInstall.ReleasesUrl;

        /// A game build as players know it: its update's name and the number (GameBuild: U59 is
        /// 744825, U60 starts at 762230; a later update needs its line here).
        static string Update(int build) => build switch
        {
            >= 762230 => $"the U60 update (build {build})",
            >= 744825 => $"the U59 update (build {build})",
            _ => $"build {build}",
        };

        static int Fail(List<string> log, Action<string, bool> tell, string why)
        {
            log.Add($"install: nothing installed: {why}");
            tell($"Moono could not be installed: {why}.", true);
            return 2;
        }
    }
}
