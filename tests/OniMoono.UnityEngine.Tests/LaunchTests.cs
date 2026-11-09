#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OniMoono.Host.Launch;
using Xunit;

namespace OniMoono.Tests
{
    /// The standalone launch (src/OniMoono.Host/Launch, compiled in here) on made-up Steam
    /// installs: library folders, appmanifests (complete, update pending, missing), the
    /// client's registry.vdf, a test start command. Nothing here reads or starts the real
    /// Steam: every Machine names its own roots (SteamLibrary.RootsVariable).
    public sealed class LaunchTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "oni-moono-launch-" + Guid.NewGuid().ToString("N"));
        readonly Dictionary<string, string> env = new();

        public LaunchTests() => Directory.CreateDirectory(dir);
        public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

        SteamLibrary.Machine Machine(bool windows = false) => new()
        {
            Windows = windows,
            Home = Path.Combine(dir, "home"),
            Env = name => env.TryGetValue(name, out var v) ? v : null,
            Registry = (_, _) => null,
        };

        /// A Steam install at ROOT with the given extra libraries, and the game in `gameIn`
        /// (the root or a library) with an appmanifest of `stateFlags` (null: none).
        string Steam(string name, string[] libraries, string? gameIn, int? stateFlags)
        {
            var root = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + root.Replace("\\", "\\\\") + "\"\n\t}\n";
            for (int i = 0; i < libraries.Length; i++)
            {
                Directory.CreateDirectory(Path.Combine(libraries[i], "steamapps"));
                vdf += $"\t\"{i + 1}\"\n\t{{\n\t\t\"path\"\t\t\"{libraries[i].Replace("\\", "\\\\")}\"\n\t\t\"apps\" {{ \"457140\" \"1\" }}\n\t}}\n";
            }
            File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), vdf + "}\n");
            if (gameIn != null) Game(gameIn, stateFlags);
            return root;
        }

        static void Game(string library, int? stateFlags)
        {
            Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "OxygenNotIncluded", "OxygenNotIncluded_Data", "Managed"));
            if (stateFlags != null)
                File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_457140.acf"),
                    $"\"AppState\"\n{{\n\t\"appid\"\t\t\"457140\"\n\t\"name\"\t\t\"Oxygen Not Included\"\n\t\"StateFlags\"\t\t\"{stateFlags}\"\n\t\"installdir\"\t\t\"OxygenNotIncluded\"\n\t\"buildid\"\t\t\"20000000\"\n}}\n");
        }

        /// The client as running and logged in (registry.vdf in the test root, with this test's pid).
        static void Running(string root, bool loggedIn = true) =>
            File.WriteAllText(Path.Combine(root, "registry.vdf"),
                $"\"Registry\"\n{{\n\"HKCU\"\n{{\n\"Software\"\n{{\n\"Valve\"\n{{\n\"Steam\"\n{{\n\"ActiveProcess\"\n{{\n\"pid\"\t\"{Environment.ProcessId}\"\n\"ActiveUser\"\t\"{(loggedIn ? 1234 : 0)}\"\n}}\n}}\n}}\n}}\n}}\n}}\n");

        StandaloneLaunch.Plan Decide(Func<IReadOnlyList<SteamLibrary.GameInstall>, int?>? choose = null, bool steamCommand = false, bool gameGiven = false, List<string>? clientLog = null)
        {
            var machine = Machine();
            var library = new SteamLibrary(machine);
            var client = new SteamClient(library, machine, (clientLog ?? new List<string>()).Add) { Sleep = _ => System.Threading.Thread.Sleep(20) };
            return StandaloneLaunch.Decide(steamCommand, gameGiven, library, client, Path.Combine(dir, "launch.json"),
                choose ?? (_ => throw new InvalidOperationException("asked")), TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void VdfReadsBlocksEscapesAndComments()
        {
            var vdf = Vdf.Parse("// a comment\n\"A\"\n{\n  \"path\"  \"C:\\\\Steam \\\"x\\\"\"\n  \"Inner\" { \"k\" \"v\" }\n  bare value\n}\n");
            var a = vdf.Block("a")!;
            Assert.Equal("C:\\Steam \"x\"", a.Value("PATH"));
            Assert.Equal("v", a.Path("inner")!.Value("k"));
            Assert.Equal("value", a.Value("bare"));
            Assert.Empty(Vdf.Parse("\"broken\" {").Block("broken")!.Entries);
        }

        [Fact]
        public void SteamCommandAndDirectStartsAreUnchanged()
        {
            Assert.Equal(StandaloneLaunch.Mode.SteamCommand, Decide(steamCommand: true).Mode);
            Assert.Equal(StandaloneLaunch.Mode.Direct, Decide(gameGiven: true).Mode);
        }

        [Fact]
        public void CompleteInstallInALibraryStartsStandalone()
        {
            var library = Path.Combine(dir, "lib2");
            var root = Steam("steam", [library], library, 4);
            Running(root);
            env[SteamLibrary.RootsVariable] = root;
            var plan = Decide();
            Assert.Equal(StandaloneLaunch.Mode.Standalone, plan.Mode);
            Assert.Equal(Path.Combine(library, "steamapps", "common", "OxygenNotIncluded"), plan.GameDir);
            Assert.Contains(plan.Log, l => l.Contains("started directly, not by Steam: Steam Cloud syncs on the next start from Steam"));
        }

        [Theory]
        [InlineData(6)]    // fully installed, update required
        [InlineData(1026)] // update running
        [InlineData(2)]    // update required, not fully installed
        [InlineData(null)] // no appmanifest
        public void PendingUpdateOrIncompleteInstallOpensSteamsGamePage(int? flags)
        {
            var root = Steam("steam", [], dir + "/steam", flags);
            Running(root);
            env[SteamLibrary.RootsVariable] = root;
            var plan = Decide();
            Assert.Equal(StandaloneLaunch.Mode.HandOff, plan.Mode);
            // Never rungameid: the launch options could start the original game.
            Assert.Equal("steam://nav/games/details/457140", plan.Url);
            Assert.NotNull(plan.Message);
        }

        [Fact]
        public void NoGameFails()
        {
            env[SteamLibrary.RootsVariable] = Steam("steam", [], null, null);
            var plan = Decide();
            Assert.Equal(StandaloneLaunch.Mode.Failed, plan.Mode);
            Assert.Contains("wasn't found", plan.Message);
        }

        [Fact]
        public void SeveralInstallsAskOnceAndRemember()
        {
            var a = Steam("steam", [], dir + "/steam", 4);
            var other = Path.Combine(dir, "other");
            var b = Steam("steam2", [other], other, 4);
            Running(a);
            env[SteamLibrary.RootsVariable] = a + Path.PathSeparator + b;
            int asked = 0;
            var first = Decide(installs => { asked++; Assert.Equal(2, installs.Count); return 1; });
            Assert.Equal(StandaloneLaunch.Mode.Standalone, first.Mode);
            Assert.Equal(Path.Combine(other, "steamapps", "common", "OxygenNotIncluded"), first.GameDir);
            var second = Decide(); // asking again throws
            Assert.Equal(first.GameDir, second.GameDir);
            Assert.Equal(1, asked);
            Assert.Equal(StandaloneLaunch.Mode.Failed, new Func<StandaloneLaunch.Plan>(() =>
            {
                File.Delete(Path.Combine(dir, "launch.json"));
                return Decide(_ => null);
            })().Mode);
        }

        [Fact]
        public void SteamNotRunningIsStartedAndWaitedFor()
        {
            var root = Steam("steam", [], dir + "/steam", 4);
            env[SteamLibrary.RootsVariable] = root;
            // The test double's start: writes the running, logged-in client's registry.vdf.
            Directory.CreateDirectory(Path.Combine(dir, "later"));
            Running(Path.Combine(dir, "later"));
            env[SteamClient.StartVariable] = $"sleep 0.2; cp '{Path.Combine(dir, "later", "registry.vdf")}' '{Path.Combine(root, "registry.vdf")}'";
            var log = new List<string>();
            var plan = Decide(clientLog: log);
            Assert.Equal(StandaloneLaunch.Mode.Standalone, plan.Mode);
            Assert.Contains(log, l => l.Contains("starting it"));
            Assert.Contains(log, l => l.Contains("Steam is up"));
        }

        [Fact]
        public void SteamThatNeverComesUpFails()
        {
            var root = Steam("steam", [], dir + "/steam", 4);
            env[SteamLibrary.RootsVariable] = root;
            // No start command in a test run: nothing is started.
            var plan = Decide();
            Assert.Equal(StandaloneLaunch.Mode.Failed, plan.Mode);
            Running(root, loggedIn: false);
            var machine = Machine();
            var library = new SteamLibrary(machine);
            var clock = TimeSpan.Zero;
            var client = new SteamClient(library, machine, _ => { }) { Elapsed = () => clock, Sleep = d => clock += d };
            Assert.False(client.EnsureRunning(null, TimeSpan.FromSeconds(90)));
            Assert.True(clock >= TimeSpan.FromSeconds(90));
        }

        [Fact]
        public void LinuxPlacesAreSetupsAndALinkedRootCountsOnce()
        {
            // The places setup.sh looks in, in its order.
            var setup = File.ReadAllText(Path.Combine(RepoRoot(), "setup.sh"));
            var places = System.Text.RegularExpressions.Regex.Matches(setup.Substring(setup.IndexOf("find_game()", StringComparison.Ordinal)), "\"\\$HOME/([^\"]+)\"")
                .Select(m => m.Groups[1].Value).Take(4).ToArray();
            Assert.Equal(SteamLibrary.LinuxRoots, places);
            // setup.ps1 reads the registry's SteamPath, as SteamLibrary does on Windows.
            Assert.Contains(@"'HKCU:\Software\Valve\Steam'", File.ReadAllText(Path.Combine(RepoRoot(), "setup.ps1")));
            Assert.Contains(".SteamPath", File.ReadAllText(Path.Combine(RepoRoot(), "setup.ps1")));

            var home = Path.Combine(dir, "home");
            var real = Path.Combine(home, ".local", "share", "Steam");
            Directory.CreateDirectory(Path.Combine(real, "steamapps"));
            File.WriteAllText(Path.Combine(real, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { }");
            Game(real, 4);
            Directory.CreateDirectory(Path.Combine(home, ".steam"));
            File.CreateSymbolicLink(Path.Combine(home, ".steam", "steam"), real);
            var library = new SteamLibrary(Machine());
            Assert.Single(library.Roots());
            Assert.Single(library.GameInstalls());
        }

        [Fact]
        public void WindowsFindsSteamThroughTheRegistry()
        {
            var root = Steam("steam", [], dir + "/steam", 4);
            var machine = Machine(windows: true);
            machine.Registry = (key, name) => key == @"Software\Valve\Steam" && name == "SteamPath" ? root : null;
            var installs = new SteamLibrary(machine).GameInstalls();
            Assert.Single(installs);
            Assert.True(installs[0].Manifest!.Ready);
        }

        [Fact]
        public void OptionsTellAStandaloneStart()
        {
            Assert.False(OniMoono.Host.Options.Parse([])!.GameDirGiven);
            Assert.True(OniMoono.Host.Options.Parse(["--game", "/x"])!.GameDirGiven);
            var steam = OniMoono.Host.Options.Parse(["--", "/games/OxygenNotIncluded/OxygenNotIncluded.exe"]);
            Assert.True(steam!.GameDirGiven);
        }

        static string RepoRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    }
}
