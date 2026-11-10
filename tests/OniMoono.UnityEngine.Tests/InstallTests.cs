#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OniMoono.Host.Install;
using Xunit;

namespace OniMoono.Tests
{
    /// The versioned install (src/OniMoono.Host/Install, compiled in here) from a fake source
    /// folder in the Workshop layout: the root (a U60 build, minimumSupportedBuild 762230) and
    /// archived_versions/u59 (744825), each with mod_info.yaml, moono.json and this platform's
    /// player with its SHA256SUMS manifest.
    public sealed class InstallTests : IDisposable
    {
        const long U59 = 744825, U60 = 762230;
        static readonly IReadOnlySet<string> NoDlc = new HashSet<string>();
        readonly string dir = Path.Combine(Path.GetTempPath(), "oni-moono-install-" + Guid.NewGuid().ToString("N"));
        readonly List<string> log = new();
        string Source => Path.Combine(dir, "source");
        string Root => Path.Combine(dir, "root");

        public InstallTests() => Directory.CreateDirectory(dir);
        public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

        VersionedInstall Install() => new(Root, VersionedInstall.ThisPlatform, log.Add);

        /// A version folder at `relative` in the source: mod_info.yaml, moono.json, and the
        /// player (the entry file, OniMoono.dll with `content`, a file in a folder) with its manifest.
        string Version(string relative, string version, long minimumBuild, string content = "dll", int api = 2)
        {
            var v = Path.Combine(Source, relative);
            var platform = Path.Combine(v, VersionedInstall.ThisPlatform);
            Directory.CreateDirectory(Path.Combine(platform, "player", "packages"));
            File.WriteAllText(Path.Combine(v, "mod_info.yaml"), $"minimumSupportedBuild: {minimumBuild}\nAPIVersion: {api}\nversion: \"{version}\"\n");
            var manifest = $"oni-moono-{version}-{VersionedInstall.ThisPlatform}.SHA256SUMS";
            File.WriteAllText(Path.Combine(v, "moono.json"),
                $"{{\"workshopItem\": \"oni-moono\", \"version\": \"{version}\", \"platforms\": {{\"{VersionedInstall.ThisPlatform}\": {{\"folder\": \"{VersionedInstall.ThisPlatform}\", \"manifest\": \"{manifest}\"}}}}}}");
            File.WriteAllText(Path.Combine(platform, "player", VersionedInstall.EntryFileInPlayer), "#!/bin/sh\necho " + version + "\n");
            File.WriteAllText(Path.Combine(platform, "player", "OniMoono.dll"), content);
            File.WriteAllText(Path.Combine(platform, "player", "packages", "UnityEngine.UI.dll"), "ui " + version);
            File.WriteAllText(Path.Combine(platform, "setup.sh"), "setup");
            var lines = Directory.GetFiles(platform, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(platform, f).Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(platform, p))))}  {p}\n");
            File.WriteAllText(Path.Combine(v, manifest), string.Concat(lines));
            return v;
        }

        void Workshop(string u60 = "2026.11.08.01-u60", string u59 = "2026.11.08.01")
        {
            Version("", u60, U60);
            Version(Path.Combine("archived_versions", "u59"), u59, U59);
        }

        static string Hashes(string folder) => string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => f + " " + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))));

        [Fact]
        public void ModInfoReadsAsTheGameDoes()
        {
            var info = ModInfo.Parse("# Moono\nminimumSupportedBuild: 762230\nAPIVersion: 2\nrequiredDlcIds:\n  - expansion1_id\nforbiddenDlcIds: [DLC2_ID]\nversion: \"1.0\" # a comment\n")!;
            Assert.Equal(762230, info.MinimumSupportedBuild);
            Assert.Equal(2, info.ApiVersion);
            Assert.Equal(["EXPANSION1_ID"], info.RequiredDlcIds!);
            Assert.Equal(["DLC2_ID"], info.ForbiddenDlcIds!);
            Assert.Equal("1.0", info.Version);
            Assert.False(info.DlcAllowed(new HashSet<string>()));
            Assert.True(info.DlcAllowed(new HashSet<string> { "EXPANSION1_ID" }));
            Assert.False(info.DlcAllowed(new HashSet<string> { "EXPANSION1_ID", "DLC2_ID" }));
            // lastWorkingBuild stands in for a missing minimumSupportedBuild.
            Assert.Equal(500000, ModInfo.Parse("lastWorkingBuild: 500000\n")!.MinimumSupportedBuild);
            // supportedContent (deprecated): VANILLA_ID alone forbids Spaced Out.
            Assert.Equal(["EXPANSION1_ID"], ModInfo.Parse("supportedContent: VANILLA_ID\n")!.ForbiddenDlcIds!);
            Assert.Null(ModInfo.Parse("supportedContent: ALL\n")!.ForbiddenDlcIds);
            Assert.Null(ModInfo.Parse("  indented: 1\n"));
        }

        [Fact]
        public void ArchiveChoiceIsTheGamesRule()
        {
            Workshop();
            Assert.Equal("", ArchiveChoice.MostSuitable(Source, U60, NoDlc)!.RelativePath);
            Assert.Equal("", ArchiveChoice.MostSuitable(Source, 999999, NoDlc)!.RelativePath);
            Assert.Equal(Path.Combine("archived_versions", "u59"), ArchiveChoice.MostSuitable(Source, U59, NoDlc)!.RelativePath);
            Assert.Equal(Path.Combine("archived_versions", "u59"), ArchiveChoice.MostSuitable(Source, U60 - 1, NoDlc)!.RelativePath);
            Assert.Null(ArchiveChoice.MostSuitable(Source, U59 - 1, NoDlc));
            // APIVersion 1 is left out.
            Version(Path.Combine("archived_versions", "old"), "2026.01.01.01", 1, api: 1);
            Assert.Null(ArchiveChoice.MostSuitable(Source, 100, NoDlc));
            // Without archived_versions the root is taken whatever its build.
            Directory.Delete(Path.Combine(Source, "archived_versions"), true);
            Assert.Equal("", ArchiveChoice.MostSuitable(Source, 1, NoDlc)!.RelativePath);
        }

        [Fact]
        public void FirstInstallThenBuildSwitching()
        {
            Workshop();
            var install = Install();
            Assert.Equal(VersionedInstall.SourceResult.Installed, install.InstallFrom(Source, U60, NoDlc, out var v60));
            Assert.Equal("2026.11.08.01-u60", v60);
            Assert.Equal("2026.11.08.01-u60", install.Choose(U60, NoDlc)!.Name);
            // The player only, and what choosing reads: not the archive's other files.
            var installed = Path.Combine(Root, "player", v60!);
            Assert.True(File.Exists(Path.Combine(installed, "packages", "UnityEngine.UI.dll")));
            Assert.True(File.Exists(Path.Combine(installed, "mod_info.yaml")));
            Assert.False(File.Exists(Path.Combine(installed, "setup.sh")));
            if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(Path.Combine(installed, "OniMoono")).HasFlag(UnixFileMode.UserExecute));
            // No U59 version installed yet: none for that build.
            Assert.Null(install.Choose(U59, NoDlc));
            Assert.Equal(VersionedInstall.SourceResult.Installed, install.InstallFrom(Source, U59, NoDlc, out var v59));
            Assert.Equal("2026.11.08.01", install.Choose(U59, NoDlc)!.Name);
            Assert.Equal("2026.11.08.01-u60", install.Choose(U60, NoDlc)!.Name);
            Assert.Equal(VersionedInstall.SourceResult.AlreadyInstalled, install.InstallFrom(Source, U60, NoDlc, out _));
            Assert.Empty(Directory.GetDirectories(Path.Combine(Root, "player"), ".incoming-*"));
        }

        [Fact]
        public void UpdateAddsAFolderAndLeavesTheRunningOneAlone()
        {
            Workshop();
            var install = Install();
            install.InstallFrom(Source, U60, NoDlc, out var first);
            var running = Path.Combine(Root, "player", first!);
            using var held = VersionedInstall.Hold(running);
            var before = Hashes(running);
            // Steam updates the item: a newer root version.
            Directory.Delete(Path.Combine(Source, VersionedInstall.ThisPlatform), true);
            Version("", "2026.11.09.01-u60", U60, content: "newer");
            Assert.Equal(VersionedInstall.SourceResult.Installed, install.InstallFrom(Source, U60, NoDlc, out var second));
            Assert.Equal("2026.11.09.01-u60", second);
            Assert.Equal("2026.11.09.01-u60", install.Choose(U60, NoDlc)!.Name);
            Assert.Equal(before, Hashes(running));
            // Pruning keeps the version in use whatever its age.
            for (int i = 10; i < 14; i++)
            {
                Directory.Delete(Path.Combine(Source, VersionedInstall.ThisPlatform), true);
                Version("", $"2026.11.{i}.01-u60", U60);
                install.InstallFrom(Source, U60, NoDlc, out _);
            }
            install.Prune("2026.11.13.01-u60");
            var left = install.InstalledVersions().Select(v => v.Name).ToArray();
            Assert.Equal(["2026.11.13.01-u60", "2026.11.12.01-u60", "2026.11.11.01-u60", first], left);
            Assert.Equal(before, Hashes(running));
        }

        [Fact]
        public void CorruptOrPartialSourceLeavesTheCurrentVersion()
        {
            Workshop();
            var install = Install();
            install.InstallFrom(Source, U60, NoDlc, out var current);
            var player = Path.Combine(Source, VersionedInstall.ThisPlatform, "player");

            Directory.Delete(Path.Combine(Source, VersionedInstall.ThisPlatform), true);
            Version("", "2026.11.09.01-u60", U60);
            File.AppendAllText(Path.Combine(player, "OniMoono.dll"), "tampered");
            Assert.Equal(VersionedInstall.SourceResult.Failed, install.InstallFrom(Source, U60, NoDlc, out _));
            Assert.Contains(log, l => l.Contains("player/OniMoono.dll doesn't match the manifest"));

            Version("", "2026.11.09.01-u60", U60);
            File.Delete(Path.Combine(player, "packages", "UnityEngine.UI.dll"));
            Assert.Equal(VersionedInstall.SourceResult.Failed, install.InstallFrom(Source, U60, NoDlc, out _));
            Assert.Contains(log, l => l.Contains("is missing (the download is incomplete)"));

            Version("", "2026.11.09.01-u60", U60);
            File.Delete(Path.Combine(Source, $"oni-moono-2026.11.09.01-u60-{VersionedInstall.ThisPlatform}.SHA256SUMS"));
            Assert.Equal(VersionedInstall.SourceResult.Failed, install.InstallFrom(Source, U60, NoDlc, out _));

            Assert.Equal(current, install.Choose(U60, NoDlc)!.Name);
            Assert.Equal([current], install.InstalledVersions().Select(v => v.Name).ToArray());
            Assert.Empty(Directory.GetDirectories(Path.Combine(Root, "player"), ".incoming-*"));
        }

        [Fact]
        public void ManifestPathsStayInsideTheFolder()
        {
            Assert.True(VersionedInstall.IsPlainRelative("player/packages/a.dll"));
            Assert.False(VersionedInstall.IsPlainRelative("player/../../x"));
            Assert.False(VersionedInstall.IsPlainRelative("/etc/passwd"));
            Assert.False(VersionedInstall.IsPlainRelative("player\\x"));
            Assert.False(VersionedInstall.IsPlainRelative("C:/x"));
            Workshop();
            var manifest = Directory.GetFiles(Source, "*.SHA256SUMS").Single();
            File.AppendAllText(manifest, new string('0', 64) + "  player/../../escape\n");
            Assert.Equal(VersionedInstall.SourceResult.Failed, Install().InstallFrom(Source, U60, NoDlc, out _));
            Assert.False(VersionedInstall.IsVersionName("../x"));
            Assert.False(VersionedInstall.IsVersionName(".bootstrap-1"));
        }

        [Fact]
        public void RollbackByHandPinsTheKeptVersion()
        {
            Workshop();
            var install = Install();
            install.InstallFrom(Source, U60, NoDlc, out var old);
            Directory.Delete(Path.Combine(Source, VersionedInstall.ThisPlatform), true);
            Version("", "2026.11.09.01-u60", U60);
            install.InstallFrom(Source, U60, NoDlc, out var latest);
            Assert.Equal(latest, install.Choose(U60, NoDlc)!.Name);
            install.WriteState("pin", old);
            Assert.Equal(old, install.Choose(U60, NoDlc)!.Name);
            install.Prune(old!);
            Assert.Contains(latest, install.InstalledVersions().Select(v => v.Name));
            install.WriteState("pin", null);
            Assert.Equal(latest, install.Choose(U60, NoDlc)!.Name);
            // A pin naming no installed version is ignored.
            install.WriteState("pin", "2020.01.01.01");
            Assert.Equal(latest, install.Choose(U60, NoDlc)!.Name);
        }

        /// The release manifest is checked as the player is copied, for integrity: a file that
        /// doesn't match (a download Steam hasn't finished, or a corrupt one) installs nothing.
        /// An older version than one installed installs as any other: there is no rollback rule.
        [Fact]
        public void CorruptVersionInstallsNothing()
        {
            Workshop();
            var install = Install();
            Assert.Equal(VersionedInstall.SourceResult.Installed, install.InstallFrom(Source, U60, NoDlc, out var current));
            Directory.Delete(Path.Combine(Source, VersionedInstall.ThisPlatform), true);
            Version("", "2026.11.09.01-u60", U60);
            File.WriteAllText(Path.Combine(Source, VersionedInstall.ThisPlatform, "player", "OniMoono.dll"), "half a download");
            Assert.Equal(VersionedInstall.SourceResult.Failed, install.InstallFrom(Source, U60, NoDlc, out _));
            Assert.Contains(log, l => l.Contains("doesn't match the manifest"));
            Assert.Equal([current], install.InstalledVersions().Select(v => v.Name).ToArray());
            Assert.Empty(Directory.GetDirectories(Path.Combine(Root, "player"), ".incoming-*"));
            // An older version installs.
            Directory.Delete(Source, true);
            Workshop(u60: "2026.11.07.01-u60");
            Assert.Equal(VersionedInstall.SourceResult.Installed, install.InstallFrom(Source, U60, NoDlc, out _));
        }

        /// A source's unchecked metadata is read capped, so a huge one installs nothing of that
        /// version, and an error while reading the source is a refusal: the current version plays on.
        [Fact]
        public void HostileSourceInstallsNothing()
        {
            Workshop();
            var install = Install();
            Assert.Equal(VersionedInstall.SourceResult.Installed, install.InstallFrom(Source, U60, NoDlc, out var current));
            foreach (var name in new[] { "moono.json", "mod_info.yaml" })
            {
                Directory.Delete(Source, true);
                Workshop(u60: "2026.11.09.01-u60");
                using (var f = File.OpenWrite(Path.Combine(Source, name))) f.SetLength(1 << 20);
                install.InstallFrom(Source, U60, NoDlc, out _);
                Assert.DoesNotContain(install.InstalledVersions(), v => v.Name == "2026.11.09.01-u60");
            }
            // archived_versions can't be listed: the exception is a refusal, told to the player.
            if (OperatingSystem.IsWindows()) return;
            Directory.Delete(Source, true);
            Workshop(u60: "2026.11.09.01-u60");
            var archived = Path.Combine(Source, "archived_versions");
            File.SetUnixFileMode(archived, UnixFileMode.None);
            try
            {
                Assert.Equal(VersionedInstall.SourceResult.Refused, install.InstallFrom(Source, U60, NoDlc, out _));
                Assert.Equal("the Workshop item could not be read", install.Refusal);
                Assert.Contains(log, l => l.Contains("UnauthorizedAccessException"));
            }
            finally { File.SetUnixFileMode(archived, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Assert.Equal([current], install.InstalledVersions().Select(v => v.Name).ToArray());
        }

        /// A fake game folder whose Assembly-CSharp.dll has KleiVersion.ChangeList = build.
        string Game(uint build)
        {
            var game = Path.Combine(dir, "game");
            var managed = Path.Combine(game, "OxygenNotIncluded_Data", "Managed");
            Directory.CreateDirectory(managed);
            var assembly = new System.Reflection.Emit.PersistedAssemblyBuilder(new System.Reflection.AssemblyName("Assembly-CSharp"), typeof(object).Assembly);
            var type = assembly.DefineDynamicModule("Assembly-CSharp").DefineType("KleiVersion", System.Reflection.TypeAttributes.Public);
            type.DefineField("ChangeList", typeof(uint), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static | System.Reflection.FieldAttributes.Literal).SetConstant(build);
            type.CreateType();
            assembly.Save(Path.Combine(managed, "Assembly-CSharp.dll"));
            return game;
        }

        /// The first start from the item, as OniMoono.exe or OniMoono.sh starts it: a bootstrap copy
        /// of the player with the item as its source. A corrupt download installs nothing and says
        /// so; complete, it installs and switches; and a later corrupt update plays on.
        [Fact]
        public void BootstrapInstallsTheItem()
        {
            var game = Game((uint)U60);
            Workshop();
            var dll = Path.Combine(Source, VersionedInstall.ThisPlatform, "player", "OniMoono.dll");
            File.WriteAllText(dll, "corrupt");
            var told = new List<(string Message, bool Error)>();
            var bootstrap = Path.Combine(Root, "player", VersionedInstall.BootstrapPrefix + "1");
            Directory.CreateDirectory(bootstrap);
            int? Start(string playerDir)
            {
                Environment.SetEnvironmentVariable(VersionedInstall.SourceVariable, playerDir == bootstrap ? Source : null);
                try { return VersionedStart.Run(Install(), game, null, log, playerDir, (m, e) => told.Add((m, e))); }
                finally { Environment.SetEnvironmentVariable(VersionedInstall.SourceVariable, null); }
            }

            Assert.Equal(2, Start(bootstrap));
            Assert.Equal(("Moono could not be installed: no version for game build 762230.", true), Assert.Single(told));
            Assert.Empty(Install().InstalledVersions());
            Assert.False(File.Exists(Entry.PathIn(Root)));

            // The item complete: installed, and the bootstrap hands over to it.
            told.Clear();
            Directory.Delete(Source, true);
            Workshop();
            Assert.Equal(VersionedInstall.SwitchExitCode, Start(bootstrap));
            Assert.Empty(told);
            Assert.Equal("2026.11.08.01-u60", Install().ReadState("current"));
            var installed = Path.Combine(Root, "player", "2026.11.08.01-u60");

            // Steam brings an update with a corrupt file: the installed version plays on.
            Directory.Delete(Path.Combine(Source, VersionedInstall.ThisPlatform), true);
            Version("", "2026.11.09.01-u60", U60);
            File.WriteAllText(Path.Combine(Source, VersionedInstall.ThisPlatform, "player", "OniMoono.dll"), "d");
            Assert.Null(Start(installed));
            Assert.Empty(told);
            Assert.Equal(["2026.11.08.01-u60"], Install().InstalledVersions().Select(v => v.Name).ToArray());
        }

        /// The release manifest is kept in the installed version's folder, and each start checks
        /// that every player file it lists is there (existence only): none missing plays, some
        /// missing doesn't start and says which (five at most, Player.log all), and a version
        /// installed before manifests were kept is not checked.
        [Fact]
        public void MissingFilesStopTheStart()
        {
            var game = Game((uint)U60);
            Workshop();
            var install = Install();
            Assert.Equal(VersionedInstall.SourceResult.Installed, install.InstallFrom(Source, U60, NoDlc, out var version));
            var installed = Path.Combine(Root, "player", version!);
            Assert.True(File.Exists(Path.Combine(installed, VersionedInstall.SavedManifestFile)));
            var told = new List<(string Message, bool Error)>();
            int? Start() => VersionedStart.Run(Install(), game, null, log, installed, (m, e) => told.Add((m, e)));

            // None missing: it plays.
            Assert.Empty(VersionedInstall.MissingFiles(installed)!);
            Assert.Null(Start());
            Assert.Empty(told);

            // Some missing: it doesn't start, and says which and how to repair.
            var ui = Path.Combine("packages", "UnityEngine.UI.dll");
            File.Delete(Path.Combine(installed, ui));
            Assert.Equal([ui], VersionedInstall.MissingFiles(installed));
            Assert.Equal(2, Start());
            Assert.Equal(($"Moono's installed version {version} is missing 1 files:\n{ui}\n\nTo repair, delete {installed} and start the game again.", true), Assert.Single(told));
            Assert.Contains(log, l => l.Contains("is missing 1 files") && l.Contains(ui));

            // More than five: the first five, then how many more; the log has them all.
            var many = Enumerable.Range(1, 8).Select(i => $"extra{i}.dll").ToList();
            var message = VersionedStart.MissingMessage(version!, installed, many);
            Assert.Equal(string.Join("\n", [$"Moono's installed version {version} is missing 8 files:", .. many.Take(5), "…and 3 more (Player.log lists them all).", "", $"To repair, delete {installed} and start the game again."]), message);
            File.AppendAllLines(Path.Combine(installed, VersionedInstall.SavedManifestFile), many.Select(m => new string('0', 64) + "  player/" + m));
            told.Clear();
            Assert.Equal(2, Start());
            Assert.Contains("…and 4 more (Player.log lists them all).", Assert.Single(told).Message);
            Assert.Contains(log, l => l.Contains("is missing 9 files") && l.Contains("extra8.dll"));

            // No saved manifest (installed before this was kept): not checked, it plays.
            File.Delete(Path.Combine(installed, VersionedInstall.SavedManifestFile));
            Assert.Null(VersionedInstall.MissingFiles(installed));
            told.Clear();
            Assert.Null(Start());
            Assert.Empty(told);
        }

        [Fact]
        public void PruneKeepsTwoPreviousAndClearsLeftovers()
        {
            Workshop();
            var install = Install();
            for (int i = 1; i <= 5; i++)
            {
                Directory.Delete(Path.Combine(Source, VersionedInstall.ThisPlatform), true);
                Version("", $"2026.11.0{i}.01-u60", U60);
                install.InstallFrom(Source, U60, NoDlc, out _);
            }
            Directory.CreateDirectory(Path.Combine(Root, "player", ".incoming-x-1"));
            var bootstrap = Path.Combine(Root, "player", ".bootstrap-1");
            Directory.CreateDirectory(bootstrap);
            Directory.SetLastWriteTimeUtc(bootstrap, DateTime.UtcNow.AddHours(-1));
            install.Prune("2026.11.05.01-u60");
            Assert.Equal(["2026.11.05.01-u60", "2026.11.04.01-u60", "2026.11.03.01-u60"], install.InstalledVersions().Select(v => v.Name).ToArray());
            Assert.False(Directory.Exists(bootstrap));
            Assert.Empty(Directory.GetDirectories(Path.Combine(Root, "player"), ".incoming-*"));
        }

        [Fact]
        public void VersionsCompareByTheirNumbers()
        {
            Assert.True(VersionedInstall.CompareVersions("2026.11.10.01", "2026.11.09.02") > 0);
            Assert.True(VersionedInstall.CompareVersions("2026.11.09.01-u60", "2026.11.09.01") > 0);
            Assert.Equal(0, VersionedInstall.CompareVersions("1.0", "1.0"));
        }

        [Fact]
        public void EntryScript()
        {
            if (OperatingSystem.IsWindows()) return;
            Directory.CreateDirectory(Root);
            var install = Install();
            Assert.StartsWith("#!/bin/sh\n", Entry.Script);
            Assert.Contains("77", Entry.Script);
            // The entry point goes in once, and again only when it changes.
            Workshop();
            install.InstallFrom(Source, U60, NoDlc, out var v);
            Entry.Install(install, Path.Combine(Root, "player", v!), log);
            Assert.Equal(Entry.Script, File.ReadAllText(Path.Combine(Root, "OniMoono")));
            int updates = log.Count(l => l.Contains("entry point"));
            Entry.Install(install, Path.Combine(Root, "player", v!), log);
            Assert.Equal(updates, log.Count(l => l.Contains("entry point")));
        }

        /// An install from a source folder writes nothing outside the install root: no shortcut
        /// on the desktop, in the Start menu or under XDG applications.
        [Fact]
        public void InstallFromSourceMakesNoShortcut()
        {
            var data = Path.Combine(dir, "data");
            Directory.CreateDirectory(Path.Combine(data, "applications"));
            var old = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            try
            {
                Workshop();
                var install = Install();
                Assert.Equal(VersionedInstall.SourceResult.Installed, install.InstallFrom(Source, U60, NoDlc, out var v));
                Entry.Install(install, Path.Combine(Root, "player", v!), log);
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_DATA_HOME", old);
            }
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(data, "applications")));
            Assert.Equal(["data", "root", "source"], Directory.GetDirectories(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
            Assert.Empty(Directory.GetFiles(Root, "*.lnk", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Root, "*.desktop", SearchOption.AllDirectories));
            Assert.DoesNotContain(log, l => l.Contains("shortcut", StringComparison.OrdinalIgnoreCase));
            Assert.Null(typeof(VersionedInstall).Assembly.GetType("OniMoono.Host.Install.Shortcuts"));
        }

        [Fact]
        public void MarkerTellsMoonosItem()
        {
            Workshop();
            Assert.True(OniMoono.GameTweaks.MoonoItemMarker.IsMoonoItem(Source));
            Assert.True(OniMoono.GameTweaks.MoonoItemMarker.IsMoonoItem(Path.Combine(Source, "archived_versions", "u59")));
            Assert.False(OniMoono.GameTweaks.MoonoItemMarker.IsMoonoItem(dir));
            Assert.False(OniMoono.GameTweaks.MoonoItemMarker.IsMoonoItem(""));
            File.WriteAllText(Path.Combine(dir, "moono.json"), "{\"workshopItem\": \"another\"}");
            Assert.False(OniMoono.GameTweaks.MoonoItemMarker.IsMoonoItem(dir));
        }
    }
}

namespace OniMoono.Tests
{
    /// The game's build as the versioned install reads it (GameBuild): KleiVersion.ChangeList
    /// from Assembly-CSharp.dll's metadata, which reference assemblies keep too. Every frozen
    /// Linux build (/mnt/e/oni-builds/oni-linux-NNNNNN) reads as its NNNNNN, and the game the
    /// build compiles against (ONI_GAME_DIR, as CI fetches it) as ci/game-refs.json's default.
    public class GameBuildTests
    {
        [Fact]
        public void ReadsKleiVersionChangeList()
        {
            if (Directory.Exists("/mnt/e/oni-builds"))
                foreach (var dir in Directory.GetDirectories("/mnt/e/oni-builds", "oni-linux-*"))
                {
                    var managed = Path.Combine(dir, "OxygenNotIncluded_Data", "Managed");
                    if (!File.Exists(Path.Combine(managed, "Assembly-CSharp.dll"))) continue;
                    Assert.Equal(uint.Parse(Path.GetFileName(dir).Substring("oni-linux-".Length)), GameBuild.Read(managed));
                }
            if (Environment.GetEnvironmentVariable("ONI_GAME_DIR") is { Length: > 0 } game && Directory.Exists(game))
            {
                var refs = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "ci", "game-refs.json")));
                var expected = uint.Parse(refs.RootElement.GetProperty("default_build"u8).GetString()!);
                var managed = Directory.Exists(Path.Combine(game, "OxygenNotIncluded_Data")) ? Path.Combine(game, "OxygenNotIncluded_Data", "Managed") : game;
                if (File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")))
                {
                    Assert.Equal(expected, GameBuild.Read(managed));
                }
            }
            Assert.Null(GameBuild.Read("/nonexistent"));
            Assert.Null(GameBuild.ReadAssembly(typeof(GameBuildTests).Assembly.Location));
        }

        static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    }
}
