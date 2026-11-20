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
        string Version(string relative, string version, long minimumBuild, string content = "dll", int api = 2, string? item = null)
        {
            var v = Path.Combine(item ?? Source, relative);
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
            install.Prune("2026.11.13.01-u60", U60, NoDlc);
            var left = install.InstalledVersions().Select(v => v.Name).ToArray();
            Assert.Equal(["2026.11.13.01-u60", "2026.11.12.01-u60", first], left);
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
            install.Prune(old!, U60, NoDlc);
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
            // It updates from the item: the update check is skipped (UpdateCheck.Start).
            Assert.Equal(Path.GetFullPath(Source), VersionedStart.UpdatesFrom);
        }

        /// The item without a U59 build (the U60 build alone, as it is since U59 left it): an
        /// install on a U59 game keeps playing its U59 version, installs nothing from the item,
        /// and is told once where its updates are; when the game updates to U60 it takes the
        /// item's version as before. A first start from that item on a U59 game installs nothing
        /// and says where Moono for U59 is.
        [Fact]
        public void ItemWithoutAVersionForTheGame()
        {
            var game = Game((uint)U59);
            Workshop();
            var told = new List<(string Message, bool Error)>();
            var bootstrap = Path.Combine(Root, "player", VersionedInstall.BootstrapPrefix + "1");
            Directory.CreateDirectory(bootstrap);
            int? Start(string playerDir, bool fromItem = false)
            {
                Environment.SetEnvironmentVariable(VersionedInstall.SourceVariable, fromItem ? Source : null);
                try { return VersionedStart.Run(Install(), game, null, log, playerDir, (m, e) => told.Add((m, e))); }
                finally { Environment.SetEnvironmentVariable(VersionedInstall.SourceVariable, null); }
            }
            string[] Versions() => Install().InstalledVersions().Select(v => v.Name).ToArray();
            void U60Only(string version)
            {
                Directory.Delete(Source, true);
                Version("", version, U60);
            }

            // Installed from the item while it had a U59 build.
            Assert.Equal(VersionedInstall.SwitchExitCode, Start(bootstrap, fromItem: true));
            var u59 = Path.Combine(Root, "player", "2026.11.08.01");
            Assert.Null(Start(u59));
            Assert.Empty(told);
            string before = Hashes(u59);

            // The item is the U60 build alone: U59 plays on, nothing is installed, told once.
            U60Only("2026.11.14.01-u60");
            log.Clear();
            Assert.Null(Start(u59));
            var (message, error) = Assert.Single(told);
            Assert.False(error);
            Assert.Equal("Moono's Workshop item is for the U60 update (build 762230) of Oxygen Not Included and later. Your game is the U59 update (build 744825). " +
                "Version 2026.11.08.01 plays on, without updates from the Workshop. Moono for your game is on GitHub: https://github.com/tigin-backwards/oni-moono/releases/tag/v2026.11.19.03", message);
            Assert.Equal(["2026.11.08.01"], Versions());
            Assert.Equal(before, Hashes(u59));
            Assert.Equal("2026.11.08.01", Install().ReadState("current"));
            Assert.Contains(log, l => l.Contains("has no version for game build 744825: it is for build 762230 and later") && l.Contains(VersionedInstall.ReleasesUrl));
            // No updates from the item: the update check runs, as for a GitHub install.
            Assert.Null(VersionedStart.UpdatesFrom);
            Assert.Empty(Directory.GetDirectories(Path.Combine(Root, "player"), ".incoming-*"));
            // Later starts and later items: logged, not told again; folders Steam left empty change nothing.
            U60Only("2026.11.15.01-u60");
            Directory.CreateDirectory(Path.Combine(Source, "archived_versions", "u59"));
            log.Clear();
            Assert.Null(Start(u59));
            Assert.Single(told);
            Assert.Equal(["2026.11.08.01"], Versions());
            Assert.Contains(log, l => l.Contains("has no version for game build 744825"));

            // The game updates to U60: the item's version is installed and switched to.
            told.Clear();
            game = Game((uint)U60);
            Assert.Equal(VersionedInstall.SwitchExitCode, Start(u59));
            Assert.Empty(told);
            Assert.Equal("2026.11.15.01-u60", Install().ReadState("current"));
            Assert.Equal(["2026.11.15.01-u60", "2026.11.08.01"], Versions());
            Assert.Null(Start(Path.Combine(Root, "player", "2026.11.15.01-u60")));
            Assert.Null(Install().ReadState("no-updates"));
            Assert.NotNull(VersionedStart.UpdatesFrom);
            Assert.Empty(told);

            // A first start from the U60-only item on a U59 game: nothing installed, and where to go.
            game = Game((uint)U59);
            Directory.Delete(Root, true);
            Directory.CreateDirectory(bootstrap);
            Assert.Equal(2, Start(bootstrap, fromItem: true));
            Assert.Equal(("Moono's Workshop item is for the U60 update (build 762230) of Oxygen Not Included and later. Your game is the U59 update (build 744825). " +
                "Install Moono for your game from a GitHub release instead: https://github.com/tigin-backwards/oni-moono/releases/tag/v2026.11.19.03", true), Assert.Single(told));
            Assert.Empty(Versions());
            Assert.False(File.Exists(Entry.PathIn(Root)));
        }

        /// A made-up Steam install at dir/NAME whose library holds the Workshop item `id` in
        /// `version` (and another mod's folder), as SteamItem.Find reads it.
        string SteamCopy(string version, string name = "steam", string id = "3837112063", long build = U60)
        {
            var root = Path.Combine(dir, name);
            var content = Path.Combine(root, "steamapps", "workshop", "content", "457140");
            var item = Path.Combine(content, id);
            if (Directory.Exists(item)) Directory.Delete(item, true);
            Directory.CreateDirectory(Path.Combine(content, "1234567890"));
            File.WriteAllText(Path.Combine(content, "1234567890", "mod_info.yaml"), "minimumSupportedBuild: 1\nAPIVersion: 2\n");
            File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\"\n{\n}\n");
            Version("", version, build, item: item);
            return item;
        }

        /// Steam's appworkshop_457140.acf for the item: the manifest installed and the latest known.
        void WorkshopState(string installed, string latest, string name = "steam", string id = "3837112063") =>
            File.WriteAllText(Path.Combine(dir, name, "steamapps", "workshop", "appworkshop_457140.acf"),
                "\"AppWorkshop\"\n{\n\t\"appid\"\t\t\"457140\"\n\t\"NeedsDownload\"\t\t\"0\"\n\t\"WorkshopItemsInstalled\"\n\t{\n"
                + $"\t\t\"{id}\"\n\t\t{{\n\t\t\t\"size\"\t\t\"1\"\n\t\t\t\"timeupdated\"\t\t\"1794758977\"\n\t\t\t\"manifest\"\t\t\"{installed}\"\n\t\t}}\n\t}}\n"
                + $"\t\"WorkshopItemDetails\"\n\t{{\n\t\t\"{id}\"\n\t\t{{\n\t\t\t\"manifest\"\t\t\"{installed}\"\n\t\t\t\"latest_manifest\"\t\t\"{latest}\"\n\t\t}}\n\t}}\n}}\n");

        /// Steam's copies as a start finds them: in the made-up Steam installs (ONI_MOONO_STEAM_ROOTS) alone.
        Func<IReadOnlyList<SteamItem.Copy>> SteamItems(string game, params string[] roots) => () => SteamItem.Find(new OniMoono.Host.Launch.SteamLibrary(new()
        {
            Home = Path.Combine(dir, "home"),
            Env = name => name == OniMoono.Host.Launch.SteamLibrary.RootsVariable ? string.Join(Path.PathSeparator, roots.Select(r => Path.Combine(dir, r))) : null,
            Registry = (_, _) => null,
        }), game);

        /// The game's copy of the item in its mods folder (where the loader mod runs from), in
        /// `version`: the folder an install started from the loader has as its source.
        string GamesCopy(string version)
        {
            var copy = Path.Combine(dir, "Documents", "Klei", "OxygenNotIncluded", "mods", "Steam", "3837112063");
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
            Version("", version, U60, item: copy);
            return copy;
        }

        /// An install whose source is the game's copy of the item, which Steam never updates:
        /// it installs from Steam's copy once that is newer and records it as the source, with
        /// nothing for the player to do. Without Steam's copy it keeps updating from the
        /// recorded folder.
        [Fact]
        public void StaleSourceHealsFromSteamsCopy()
        {
            var game = Game((uint)U60);
            const string a = "2026.11.13.01-u60", b = "2026.11.15.02-u60";
            var copy = GamesCopy(a);
            var told = new List<(string Message, bool Error)>();
            var bootstrap = Path.Combine(Root, "player", VersionedInstall.BootstrapPrefix + "1");
            Directory.CreateDirectory(bootstrap);
            int? Start(string version, string? given = null, bool steam = true, string? use = null)
            {
                Environment.SetEnvironmentVariable(VersionedInstall.SourceVariable, given);
                try
                {
                    return VersionedStart.Run(Install(), game, use, log, version.StartsWith('.') ? bootstrap : Path.Combine(Root, "player", version),
                        (m, e) => told.Add((m, e)), steam ? SteamItems(game, "steam") : null);
                }
                finally { Environment.SetEnvironmentVariable(VersionedInstall.SourceVariable, null); }
            }
            string[] Versions() => Install().InstalledVersions().Select(v => v.Name).ToArray();

            // The first start, from the game's copy; no Steam folder is found.
            Assert.Equal(VersionedInstall.SwitchExitCode, Start(".bootstrap", given: copy, steam: false));
            Assert.Equal(copy, Install().ReadState("source"));
            Assert.Null(Start(a, steam: false));

            // Steam's copy missing: the recorded folder still updates the install.
            const string a2 = "2026.11.13.02-u60";
            GamesCopy(a2);
            Assert.Equal(VersionedInstall.SwitchExitCode, Start(a));
            Assert.Equal(a2, Install().ReadState("current"));
            Assert.Equal(copy, Install().ReadState("source"));

            // Steam is still to download the update (its manifests differ): its copy isn't read.
            var steams = SteamCopy(b);
            WorkshopState(installed: "111", latest: "222");
            log.Clear();
            Assert.Null(Start(a2));
            Assert.Equal([a2, a], Versions());
            Assert.Equal(copy, Install().ReadState("source"));
            Assert.Contains(log, l => l.Contains("isn't read this start: Steam has manifest 111 of it installed and 222 to download"));

            // Steam's copy damaged (a file of a download not finished): the current version plays on.
            WorkshopState(installed: "222", latest: "222");
            File.WriteAllText(Path.Combine(steams, VersionedInstall.ThisPlatform, "player", "OniMoono.dll"), "half");
            Assert.Null(Start(a2));
            Assert.Equal([a2, a], Versions());
            Assert.Equal(a2, Install().ReadState("current"));
            Assert.Contains(log, l => l.Contains("doesn't match the manifest"));
            Assert.Empty(Directory.GetDirectories(Path.Combine(Root, "player"), ".incoming-*"));

            // Steam's copy complete and newer: installed and switched to, and it is the source now.
            Install().WriteState("source", copy);
            SteamCopy(b);
            log.Clear();
            Assert.Equal(VersionedInstall.SwitchExitCode, Start(a2));
            Assert.Equal(b, Install().ReadState("current"));
            Assert.Equal(steams, Install().ReadState("source"));
            Assert.Contains(log, l => l.Contains($"updates come from Steam's copy of the Workshop item, {steams} (was {copy})"));
            Assert.Null(Start(b));
            Assert.Equal([b, a2], Versions());
            Assert.Empty(told);

            // A start from the stale copy again (its OniMoono.exe, or the loader's state): the
            // newest installed version plays, the old one isn't installed again, Steam's copy stays the source.
            GamesCopy(a);
            Assert.Equal(VersionedInstall.SwitchExitCode, Start(".bootstrap", given: copy));
            Assert.Equal(b, Install().ReadState("current"));
            Assert.Equal([b, a2], Versions());
            Assert.Equal(steams, Install().ReadState("source"));

            // A pinned version plays on while Steam's newer one is installed beside it.
            const string c = "2026.11.16.01-u60";
            SteamCopy(c);
            Assert.Null(Start(b, use: b));
            Assert.Equal([c, b], Versions());
            Assert.Equal(b, Install().ReadState("current"));
            Assert.Equal(VersionedInstall.SwitchExitCode, Start(b, use: "latest"));
            Assert.Equal(c, Install().ReadState("current"));

            // An older copy at Steam (the item rolled back) is not installed.
            SteamCopy(a);
            Assert.Null(Start(c));
            Assert.Equal(c, Install().ReadState("current"));
            Assert.Equal([c, b], Versions());
            Assert.Empty(told);
        }

        /// A Steam copy for a newer game than the installed one installs nothing.
        [Fact]
        public void SteamsCopyForANewerGameIsNotInstalled()
        {
            var game = Game((uint)U59);
            Workshop();
            var install = Install();
            Assert.Equal(VersionedInstall.SourceResult.Installed, install.InstallFrom(Source, U59, NoDlc, out var u59));
            install.WriteState("source", Source);
            SteamCopy("2026.11.15.02-u60");
            var told = new List<(string Message, bool Error)>();
            Assert.Null(VersionedStart.Run(Install(), game, null, log, Path.Combine(Root, "player", u59!), (m, e) => told.Add((m, e)), SteamItems(game, "steam")));
            Assert.Equal([u59], Install().InstalledVersions().Select(v => v.Name).ToArray());
            Assert.Empty(told);
            // Steam's copy alone as the source: still nothing, and the player is told where U59's updates are.
            Install().WriteState("source", null);
            Assert.Null(VersionedStart.Run(Install(), game, null, log, Path.Combine(Root, "player", u59!), (m, e) => told.Add((m, e)), SteamItems(game, "steam")));
            Assert.Equal([u59], Install().InstalledVersions().Select(v => v.Name).ToArray());
            Assert.Contains(log, l => l.Contains("has no version for game build 744825: it is for build 762230 and later"));
            Assert.Contains("plays on, without updates from the Workshop", Assert.Single(told).Message);
        }

        /// Steam's copy is found by its moono.json in every library of every Steam install and
        /// in the library holding the game, on Linux paths too, whatever its ID; another mod's
        /// folder is not it.
        [Fact]
        public void SteamsCopyIsFoundInItsLibrary()
        {
            Assert.Equal(Path.Combine(dir, "lib"), SteamItem.LibraryOf(Path.Combine(dir, "lib", "steamapps", "common", "OxygenNotIncluded")));
            Assert.Equal(Path.Combine(dir, "lib"), SteamItem.LibraryOf(Path.Combine(dir, "lib", "steamapps", "common", "OxygenNotIncluded") + Path.DirectorySeparatorChar));
            Assert.Null(SteamItem.LibraryOf(Path.Combine(dir, "oni-linux-763023")));
            Assert.Null(SteamItem.LibraryOf(""));
            if (!OperatingSystem.IsWindows())
                Assert.Equal("/home/a/.var/app/com.valvesoftware.Steam/.local/share/Steam",
                    SteamItem.LibraryOf("/home/a/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/OxygenNotIncluded"));

            // ~/.local/share/Steam with a second library that holds the game and the item.
            var home = Path.Combine(dir, "home");
            var root = Path.Combine(home, ".local", "share", "Steam");
            var library = Path.Combine(dir, "mnt", "games", "SteamLibrary");
            Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{root.Replace("\\", "\\\\")}\"\n\t}}\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{library.Replace("\\", "\\\\")}\"\n\t}}\n}}\n");
            var item = SteamCopy("2026.11.15.02-u60", name: Path.Combine("mnt", "games", "SteamLibrary"), id: "42");
            var machine = new OniMoono.Host.Launch.SteamLibrary.Machine { Windows = false, Home = home, Env = _ => null, Registry = (_, _) => null };
            var found = Assert.Single(SteamItem.Find(new OniMoono.Host.Launch.SteamLibrary(machine), null));
            Assert.Equal((item, "42", null), (found.Dir, found.Id, found.Pending));

            // No Steam install known: the library the game is in.
            var none = new OniMoono.Host.Launch.SteamLibrary.Machine { Windows = false, Home = Path.Combine(dir, "nobody"), Env = _ => null, Registry = (_, _) => null };
            Assert.Empty(SteamItem.Find(new OniMoono.Host.Launch.SteamLibrary(none), Path.Combine(dir, "elsewhere")));
            Assert.Equal(item, Assert.Single(SteamItem.Find(new OniMoono.Host.Launch.SteamLibrary(none), Path.Combine(library, "steamapps", "common", "OxygenNotIncluded"))).Dir);
            // Found once when the game's library is also a listed one.
            Assert.Single(SteamItem.Find(new OniMoono.Host.Launch.SteamLibrary(machine), Path.Combine(library, "steamapps", "common", "OxygenNotIncluded")));

            // appworkshop_457140.acf: pending only when both manifests are there and differ.
            var state = Path.Combine(library, "steamapps", "workshop", "appworkshop_457140.acf");
            Assert.Null(SteamItem.Pending(state, "42"));
            WorkshopState("5", "5", name: Path.Combine("mnt", "games", "SteamLibrary"), id: "42");
            Assert.Null(SteamItem.Pending(state, "42"));
            Assert.Null(SteamItem.Pending(state, "43"));
            WorkshopState("5", "6", name: Path.Combine("mnt", "games", "SteamLibrary"), id: "42");
            Assert.NotNull(SteamItem.Pending(state, "42"));
            Assert.NotNull(Assert.Single(SteamItem.Find(new OniMoono.Host.Launch.SteamLibrary(machine), null)).Pending);
            File.WriteAllText(state, "not a vdf {{{");
            Assert.Null(SteamItem.Pending(state, "42"));
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

        /// Pruning keeps the current version, one previous (the newest other version for the
        /// game's build) and a pinned one; a version in use stays; leftovers go.
        [Fact]
        public void PruneKeepsOnePreviousAndClearsLeftovers()
        {
            Workshop();
            var install = Install();
            for (int i = 1; i <= 5; i++)
            {
                Directory.Delete(Path.Combine(Source, VersionedInstall.ThisPlatform), true);
                Version("", $"2026.11.0{i}.01-u60", U60);
                install.InstallFrom(Source, U60, NoDlc, out _);
            }
            string[] Versions() => install.InstalledVersions().Select(v => v.Name).ToArray();
            Directory.CreateDirectory(Path.Combine(Root, "player", ".incoming-x-1"));
            var bootstrap = Path.Combine(Root, "player", ".bootstrap-1");
            Directory.CreateDirectory(bootstrap);
            Directory.SetLastWriteTimeUtc(bootstrap, DateTime.UtcNow.AddHours(-1));
            // A pinned version stays, and one held open.
            install.WriteState("pin", "2026.11.01.01-u60");
            using (VersionedInstall.Hold(Path.Combine(Root, "player", "2026.11.02.01-u60")))
            {
                install.Prune("2026.11.05.01-u60", U60, NoDlc);
                Assert.Equal(["2026.11.05.01-u60", "2026.11.04.01-u60", "2026.11.02.01-u60", "2026.11.01.01-u60"], Versions());
            }
            Assert.False(Directory.Exists(bootstrap));
            Assert.Empty(Directory.GetDirectories(Path.Combine(Root, "player"), ".incoming-*"));
            install.WriteState("pin", null);
            install.Prune("2026.11.05.01-u60", U60, NoDlc);
            Assert.Equal(["2026.11.05.01-u60", "2026.11.04.01-u60"], Versions());
            // The current version older than the other (pinned by hand, then unpinned): both stay.
            install.Prune("2026.11.04.01-u60", U60, NoDlc);
            Assert.Equal(["2026.11.05.01-u60", "2026.11.04.01-u60"], Versions());

            // The previous one is for the game's build: on a U59 game the U59 version stays
            // rather than a newer U60 one, which could not play it.
            Assert.Equal(VersionedInstall.SourceResult.Installed, install.InstallFrom(Source, U59, NoDlc, out var u59));
            Assert.Equal("2026.11.04.01-u60", install.Previous("2026.11.05.01-u60", U60, NoDlc)!.Name);
            Assert.Equal(u59, install.Previous("2026.11.05.01-u60", U60 - 1, NoDlc)!.Name);
            install.Prune("2026.11.05.01-u60", U60, NoDlc);
            Assert.Equal(["2026.11.05.01-u60", "2026.11.04.01-u60"], Versions());
            Assert.Null(install.Previous("2026.11.05.01-u60", U59 - 1, NoDlc));
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

        /// What a restart starts, per install mode (RestartEntry): the entry point for a version
        /// folder of the versioned install; the executable App.Restart names (null) for a player
        /// started from its own folder (launch options, standalone), a bootstrap or incoming copy,
        /// or a root without its entry point.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ARestartStartsTheEntryPointOfAVersionedInstall(bool windows)
        {
            var entryName = windows ? "OniMoono.exe" : "OniMoono";
            var version = Path.Combine(Root, "player", "2026.11.16.04-u60");
            Directory.CreateDirectory(version);
            // Launch options and standalone: no install root, or a player outside it.
            var own = Path.Combine(dir, "oni-moono", "player");
            Assert.Null(RestartEntry.For(null, own, entryName, windows));
            Assert.Null(RestartEntry.For("", own, entryName, windows));
            // The versioned install before its entry point is in place.
            Assert.Null(RestartEntry.For(Root, version, entryName, windows));
            File.WriteAllText(Path.Combine(Root, entryName), "entry");
            var entry = Path.Combine(Root, entryName);
            Assert.Equal(entry, RestartEntry.For(Root, version, entryName, windows));
            Assert.Equal(entry, RestartEntry.For(Root + Path.DirectorySeparatorChar, version + Path.DirectorySeparatorChar, entryName, windows));
            Assert.Null(RestartEntry.For(Root, own, entryName, windows));
            Assert.Null(RestartEntry.For(Root, Root, entryName, windows));
            Assert.Null(RestartEntry.For(Root, Path.Combine(Root, "player", ".bootstrap-42"), entryName, windows));
            Assert.Null(RestartEntry.For(Root, Path.Combine(Root, "player", ".incoming-42"), entryName, windows));
            // Windows paths differ in case only where the file system doesn't care.
            var upper = Path.Combine(Path.GetDirectoryName(Root)!, "ROOT", "player", "2026.11.16.04-u60");
            if (windows) Assert.Equal(entry, RestartEntry.For(Root, upper, entryName, windows));
            else Assert.Null(RestartEntry.For(Root, upper, entryName, windows));
        }

        /// The Windows entry point's files (PlayerFiles.InstallWindowsEntry): the version's
        /// OniMoono.exe as OniMoono.exe and as Restarter.exe, which App.Restart starts beside the
        /// entry point; a changed one replaces each after renaming it to .old.
        [Fact]
        public void TheWindowsEntryPointHasARestarterBesideIt()
        {
            var v1 = Path.Combine(Root, "player", "1");
            var v2 = Path.Combine(Root, "player", "2");
            Directory.CreateDirectory(v1);
            Directory.CreateDirectory(v2);
            File.WriteAllText(Path.Combine(v1, "OniMoono.exe"), "MZ 1");
            File.WriteAllText(Path.Combine(v2, "OniMoono.exe"), "MZ 2");
            Assert.True(PlayerFiles.InstallWindowsEntry(Root, v1));
            Assert.Equal("MZ 1", File.ReadAllText(Path.Combine(Root, "OniMoono.exe")));
            Assert.Equal("MZ 1", File.ReadAllText(Path.Combine(Root, "Restarter.exe")));
            Assert.False(PlayerFiles.InstallWindowsEntry(Root, v1));
            // An entry point installed before Restarter.exe was: only the Restarter goes in.
            File.Delete(Path.Combine(Root, "Restarter.exe"));
            Assert.True(PlayerFiles.InstallWindowsEntry(Root, v1));
            Assert.False(File.Exists(Path.Combine(Root, "OniMoono.exe.old")));
            Assert.Equal("MZ 1", File.ReadAllText(Path.Combine(Root, "Restarter.exe")));
            File.WriteAllText(Path.Combine(Root, "Restarter.exe.old"), "older");
            Assert.True(PlayerFiles.InstallWindowsEntry(Root, v2));
            foreach (var name in new[] { "OniMoono.exe", "Restarter.exe" })
            {
                Assert.Equal("MZ 2", File.ReadAllText(Path.Combine(Root, name)));
                Assert.Equal("MZ 1", File.ReadAllText(Path.Combine(Root, name + ".old")));
            }
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
