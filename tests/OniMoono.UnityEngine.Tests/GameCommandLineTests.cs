using System;
using System.IO;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// The game's command line (#53; src/OniMoono.Host/Options.cs and GameCommandLine.cs,
    /// compiled in here): which of the player's arguments are the game's, and what
    /// Environment.GetCommandLineArgs() then gives the game's code.
    public class GameCommandLineTests
    {
        static readonly string Exe = Path.GetFullPath("/games/oni/OxygenNotIncluded");

        [Fact]
        public void EverythingAfterTheGamesExecutableIsTheGames()
        {
            // Steam's command: its wrappers (with a `--` of their own), then the game's executable.
            var o = Options.Parse(["--gc", "low-memory", "--", "/steam/reaper", "SteamLaunch", "AppId=457140", "--", Exe,
                "-foo", "bar", "--baz=1", "-moddev"])!;
            Assert.NotNull(o);
            Assert.Equal(Exe, o.GameExecutable);
            Assert.Equal(Path.GetDirectoryName(Exe), o.GameDir);
            Assert.Equal("low-memory", o.Gc);
            Assert.Equal(["-foo", "bar", "--baz=1", "-moddev"], o.GameArguments);
        }

        [Fact]
        public void UnitysAndOursAfterTheExecutableAreReadAndPassedOn()
        {
            var o = Options.Parse(["--", Exe, "-screen-width", "1024", "--gpu", "integrated", "-popupwindow"])!;
            Assert.Equal(1024, o.ScreenWidth);
            Assert.Equal("integrated", o.Gpu);
            Assert.Equal(["-screen-width", "1024", "--gpu", "integrated", "-popupwindow"], o.GameArguments);
        }

        [Fact]
        public void ADoubleDashOrGameArgAfterTheExecutableIsTheGames()
        {
            var o = Options.Parse(["--", Exe, "--", "x", "--game-arg", "y"])!;
            Assert.Equal(["--", "x", "--game-arg", "y"], o.GameArguments);
        }

        [Fact]
        public void AnUnknownOptionAmongOursStopsThePlayer()
        {
            Assert.Null(Options.Parse(["--baz=1"]));
            Assert.Null(Options.Parse(["--baz=1", "--", Exe]));
            Assert.Null(Options.Parse(["--game-arg"]));
        }

        [Fact]
        public void TheDirectFormPassesUnitysStrayWordsAndGameArgs()
        {
            var o = Options.Parse(["--game", "/games/oni", "-screen-width", "1024", "-moddev", "--frames", "5", "stray",
                "--game-arg", "--baz=1", "--game-arg", "two words", "-logFile", "p.log"])!;
            Assert.Null(o.GameExecutable);
            Assert.Equal(5, o.Frames);
            Assert.Equal(["-screen-width", "1024", "-moddev", "stray", "--baz=1", "two words", "-logFile", "p.log"], o.GameArguments);
        }

        [Fact]
        public void UnitysBeforeTheSteamCommandComeFirst()
        {
            var o = Options.Parse(["-force-vulkan", "--no-focus", "--", Exe, "-moddev"])!;
            Assert.True(o.NoFocus);
            Assert.Equal(["-force-vulkan", "-moddev"], o.GameArguments);
        }

        [Fact]
        public void TheGameSeesItsExecutableThenItsArguments()
        {
            var dir = Directory.CreateTempSubdirectory("oni-cmdline-");
            try
            {
                Directory.CreateDirectory(Path.Combine(dir.FullName, OniMoono.Engine.Platform.GameLayout.DataFolderName));
                var layout = OniMoono.Engine.Platform.GameLayout.Find(dir.FullName);
                var direct = Options.Parse(["--game", dir.FullName, "--headless", "-moddev"])!;
                string name = OperatingSystem.IsWindows() ? "OxygenNotIncluded.exe" : "OxygenNotIncluded";
                Assert.Equal([Path.Combine(dir.FullName, name), "-moddev"], GameCommandLine.For(direct, layout));
                var steam = Options.Parse(["--headless", "--", Exe, "-moddev"])!;
                Assert.Equal([Exe, "-moddev"], GameCommandLine.For(steam, layout));
            }
            finally
            {
                dir.Delete(true);
            }
        }

        [Fact]
        public void GetCommandLineArgsAndCommandLineAnswerTheGames()
        {
            var before = Environment.GetCommandLineArgs();
            try
            {
                Assert.True(GameCommandLine.Install([Exe, "-foo", "two words", "--baz=1"]));
                Assert.Equal([Exe, "-foo", "two words", "--baz=1"], Environment.GetCommandLineArgs());
                Assert.EndsWith(" -foo \"two words\" --baz=1", Environment.CommandLine);
            }
            finally
            {
                GameCommandLine.Install(before);
            }
        }
    }
}
