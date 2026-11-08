using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// The Moono Tweaks options (src/OniMoono.Host/PlayerSettings.cs, compiled in here):
    /// built-in defaults, then the settings file, then the command line; and the file's round trip.
    public class PlayerSettingsTests
    {
        static readonly string[] All = ["fast-a", "fast-b", "slow-c", "off-d"];
        static readonly string[] Changing = ["slow-c", "off-d"];

        static EffectiveOptions Resolve(PlayerSettings file, string[] tweaks = null, string splash = null, bool frameStats = false, string logPaths = null)
            => EffectiveOptions.Resolve(All, Changing, tweaks ?? [], splash, frameStats, file, logPaths);

        [Fact]
        public void DefaultsAreExactWithTheSplashAndNoFrameStats()
        {
            var o = Resolve(new PlayerSettings());
            Assert.Equal(["fast-a", "fast-b"], o.EnabledTweaks);
            Assert.All(All, n => Assert.Equal(OptionSource.Default, o.Tweaks[n].Source));
            Assert.Equal(("moo", OptionSource.Default), (o.Splash, o.SplashSource));
            Assert.Equal((false, OptionSource.Default), (o.FrameStats, o.FrameStatsSource));
            Assert.Equal(("hidden", OptionSource.Default), (o.LogPaths, o.LogPathsSource));
            Assert.True(o.HideLogPaths);
        }

        /// A tweak that changes behaviour but is on by default (path-probe-repair): on without a
        /// choice, still left out of `exact`, and a choice of on is not recorded.
        [Fact]
        public void AChangingTweakCanBeOnByDefaultAndStayOutOfExact()
        {
            string[] offByDefault = ["off-d"];
            var o = EffectiveOptions.Resolve(All, Changing, [], null, false, new PlayerSettings(), offByDefault: offByDefault);
            Assert.Equal(["fast-a", "fast-b", "slow-c"], o.EnabledTweaks);
            o = EffectiveOptions.Resolve(All, Changing, ["exact"], null, false, new PlayerSettings(), offByDefault: offByDefault);
            Assert.Equal(["fast-a", "fast-b"], o.EnabledTweaks);
            var saved = EffectiveOptions.Apply(new PlayerSettings(), offByDefault, new Dictionary<string, bool> { ["slow-c"] = true, ["off-d"] = false }, null, null);
            Assert.Empty(saved.GameTweaks);
            saved = EffectiveOptions.Apply(new PlayerSettings(), offByDefault, new Dictionary<string, bool> { ["slow-c"] = false }, null, null);
            Assert.Equal(new Dictionary<string, bool> { ["slow-c"] = false }, saved.GameTweaks);
        }

        [Fact]
        public void LogPathsComeFromTheFileThenTheCommandLine()
        {
            var file = PlayerSettings.Parse("""{ "logPaths": "full" }""");
            var o = Resolve(file);
            Assert.Equal(("full", OptionSource.SettingsFile), (o.LogPaths, o.LogPathsSource));
            Assert.False(o.HideLogPaths);
            o = Resolve(file, logPaths: "hidden");
            Assert.Equal(("hidden", OptionSource.CommandLine), (o.LogPaths, o.LogPathsSource));
            o = Resolve(new PlayerSettings(), logPaths: "full");
            Assert.Equal(("full", OptionSource.CommandLine), (o.LogPaths, o.LogPathsSource));
            // The screen records only "full"; hidden again is the default, so the file drops it.
            var saved = EffectiveOptions.Apply(new PlayerSettings(), Changing, new Dictionary<string, bool>(), null, null, hideLogPaths: false);
            Assert.Equal("{\"logPaths\":\"full\"}", saved.ToJson(indented: false));
            Assert.Null(EffectiveOptions.Apply(saved, Changing, new Dictionary<string, bool>(), null, null, hideLogPaths: true).LogPaths);
            Assert.Equal("full", EffectiveOptions.Apply(saved, Changing, new Dictionary<string, bool>(), true, null).LogPaths);
        }

        [Fact]
        public void SettingsFileOverridesDefaultsPerTweak()
        {
            var file = new PlayerSettings { Splash = "moo", FrameStats = true };
            file.GameTweaks["fast-b"] = false;
            file.GameTweaks["slow-c"] = true;
            var o = Resolve(file);
            Assert.Equal(["fast-a", "slow-c"], o.EnabledTweaks);
            Assert.Equal(OptionSource.Default, o.Tweaks["fast-a"].Source);
            Assert.Equal(OptionSource.SettingsFile, o.Tweaks["fast-b"].Source);
            Assert.Equal(OptionSource.SettingsFile, o.Tweaks["slow-c"].Source);
            Assert.Equal(("moo", OptionSource.SettingsFile), (o.Splash, o.SplashSource));
            Assert.Equal((true, OptionSource.SettingsFile), (o.FrameStats, o.FrameStatsSource));
        }

        [Fact]
        public void CommandLineTweaksReplaceTheFilesWholeSet()
        {
            var file = new PlayerSettings();
            file.GameTweaks["slow-c"] = true;
            file.GameTweaks["fast-a"] = false;
            Assert.Equal(["fast-a", "fast-b"], Resolve(file, ["exact"]).EnabledTweaks);
            Assert.Equal(["fast-a", "fast-b", "slow-c", "off-d"], Resolve(file, ["all"]).EnabledTweaks);
            Assert.Equal(["fast-b", "slow-c"], Resolve(file, ["exact", "-fast-a", "slow-c"]).EnabledTweaks);
            Assert.Equal(["fast-b"], Resolve(file, ["fast-b"]).EnabledTweaks);
            Assert.All(All, n => Assert.Equal(OptionSource.CommandLine, Resolve(file, ["fast-b"]).Tweaks[n].Source));
        }

        [Fact]
        public void NoneMeansNoTweaksWhateverTheFileAndDefaultsSay()
        {
            var file = new PlayerSettings();
            file.GameTweaks["slow-c"] = true;
            Assert.Empty(Resolve(file, ["none"]).EnabledTweaks);
            Assert.Empty(Resolve(new PlayerSettings(), ["none"]).EnabledTweaks);
            // Test scripts put `none` first; a run that names tweaks after it gets just those.
            Assert.Equal(["fast-a", "fast-b"], Resolve(file, ["none", "exact"]).EnabledTweaks);
            Assert.Equal(["off-d"], Resolve(file, ["none", "off-d"]).EnabledTweaks);
        }

        [Fact]
        public void CommandLineSplashAndFrameStatsOverrideTheFile()
        {
            var file = new PlayerSettings { Splash = "moo", FrameStats = true };
            var o = Resolve(file, splash: "none", frameStats: false);
            Assert.Equal(("none", OptionSource.CommandLine), (o.Splash, o.SplashSource));
            // --frame-stats only turns them on; without it the file decides.
            Assert.Equal((true, OptionSource.SettingsFile), (o.FrameStats, o.FrameStatsSource));
            o = Resolve(new PlayerSettings(), splash: "moo", frameStats: true);
            Assert.Equal(("moo", OptionSource.CommandLine), (o.Splash, o.SplashSource));
            Assert.Equal((true, OptionSource.CommandLine), (o.FrameStats, o.FrameStatsSource));
        }

        [Fact]
        public void UnknownTweaksInTheFileAreReportedAndKept()
        {
            var file = PlayerSettings.Parse("""{ "gameTweaks": { "gone-tweak": true, "fast-a": false } }""");
            var o = Resolve(file);
            Assert.Equal(["gone-tweak"], o.UnknownTweaks);
            Assert.Equal(["fast-b"], o.EnabledTweaks);
            var saved = EffectiveOptions.Apply(file, Changing, new Dictionary<string, bool> { ["fast-b"] = false }, null, null);
            Assert.True(saved.GameTweaks["gone-tweak"]);
        }

        [Fact]
        public void ApplyRecordsOnlyWhatDiffersFromTheDefaults()
        {
            var file = new PlayerSettings { Splash = "moo" };
            file.GameTweaks["fast-a"] = false;
            file.GameTweaks["slow-c"] = true;
            // The screen's rows: fast-a back to its default, off-d on, frame stats on, splash off;
            // slow-c is not among them (the command line set it), so the file keeps it.
            var saved = EffectiveOptions.Apply(file, Changing,
                new Dictionary<string, bool> { ["fast-a"] = true, ["fast-b"] = true, ["off-d"] = true }, splashMoo: false, frameStats: true);
            Assert.Equal(new Dictionary<string, bool> { ["off-d"] = true, ["slow-c"] = true }, saved.GameTweaks);
            Assert.Equal("none", saved.Splash);
            Assert.True(saved.FrameStats);
            // The splash back on is its default, so the file drops it.
            Assert.Null(EffectiveOptions.Apply(saved, Changing, new Dictionary<string, bool>(), splashMoo: true, frameStats: null).Splash);
            saved = EffectiveOptions.Apply(saved, Changing, new Dictionary<string, bool>(), splashMoo: null, frameStats: false);
            Assert.Null(saved.FrameStats);
        }

        [Fact]
        public void FileRoundTrip()
        {
            var dir = Path.Combine(Path.GetTempPath(), "oni-moono-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = PlayerSettings.PathIn(dir);
                Assert.Equal(Path.Combine(dir, "oni-moono", "settings.json"), path);
                var missing = PlayerSettings.Read(path, out var problem);
                Assert.Null(problem);
                Assert.Empty(missing.GameTweaks);
                Assert.Null(missing.Splash);
                Assert.Null(missing.FrameStats);

                var settings = new PlayerSettings { Splash = "moo", FrameStats = true };
                settings.GameTweaks["slow-c"] = true;
                settings.GameTweaks["fast-b"] = false;
                settings.Write(path);
                Assert.False(File.Exists(path + ".tmp"));
                var back = PlayerSettings.Read(path, out problem);
                Assert.Null(problem);
                Assert.Equal(settings.GameTweaks, back.GameTweaks);
                Assert.Equal("moo", back.Splash);
                Assert.True(back.FrameStats);
                Assert.Equal(settings.ToJson(), back.ToJson());

                // An empty file records nothing.
                new PlayerSettings().Write(path);
                Assert.Equal("{}\n", File.ReadAllText(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void TheFileFromBeforeTheRenameIsCopiedOnce()
        {
            var dir = Path.Combine(Path.GetTempPath(), "oni-moono-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Neither file: nothing to copy.
                Assert.Null(PlayerSettings.MigrateIn(dir, out var problem));
                Assert.Null(problem);
                Assert.False(File.Exists(PlayerSettings.PathIn(dir)));

                var old = PlayerSettings.OldPathIn(dir);
                Assert.Equal(Path.Combine(dir, "oni-brutal", "settings.json"), old);
                var settings = new PlayerSettings { Splash = "none" };
                settings.GameTweaks["fast-b"] = false;
                settings.Write(old);
                Assert.Equal(old, PlayerSettings.MigrateIn(dir, out problem));
                Assert.Null(problem);
                Assert.Equal(settings.ToJson(), PlayerSettings.Read(PlayerSettings.PathIn(dir), out problem).ToJson());
                Assert.True(File.Exists(old));

                // Once there is a file in the new place, the old one is never copied over it.
                new PlayerSettings().Write(PlayerSettings.PathIn(dir));
                Assert.Null(PlayerSettings.MigrateIn(dir, out problem));
                Assert.Equal("{}\n", File.ReadAllText(PlayerSettings.PathIn(dir)));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[1, 2]")]
        [InlineData("""{ "gameTweaks": { "fast-a": "yes" } }""")]
        [InlineData("""{ "splash": "cows" }""")]
        [InlineData("""{ "logPaths": "some" }""")]
        public void AnUnreadableFileCountsAsEmpty(string content)
        {
            var dir = Path.Combine(Path.GetTempPath(), "oni-moono-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = PlayerSettings.PathIn(dir);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, content);
                var settings = PlayerSettings.Read(path, out var problem);
                Assert.NotNull(problem);
                Assert.Empty(settings.GameTweaks);
                Assert.Null(settings.Splash);
                Assert.Equal(["fast-a", "fast-b"], Resolve(settings).EnabledTweaks);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void TheLogSaysWhereEachOptionCameFrom()
        {
            var file = new PlayerSettings();
            file.GameTweaks["fast-b"] = false;
            Assert.Equal(["game tweaks on (default): fast-a", "game tweaks off (default): slow-c, off-d", "game tweaks off (the settings file): fast-b",
                          "splash moo (default)", "frame stats off (default)", "log paths hidden (default)", "gpu default (default)", "renderer auto (default)",
                          "dxgi on (default)", "stall repeats on (default)", "update check on (default)", "gc fast (default)"],
                Resolve(file).Describe().ToArray());
            file.UpdateCheck = false;
            Assert.Equal(["game tweaks from the command line (--game-tweak none): none", "splash none (the command line)", "frame stats on (the command line)",
                           "log paths full (the command line)", "gpu default (default)", "renderer auto (default)", "dxgi on (default)", "stall repeats on (default)",
                           "update check off (the settings file)", "gc fast (default)"],
                Resolve(file, ["none"], "none", true, "full").Describe().ToArray());
        }

        // ----- Windows: presenting through DXGI -----

        static EffectiveOptions WithDxgi(PlayerSettings file, bool? dxgi = null)
            => EffectiveOptions.Resolve(All, Changing, [], null, false, file, null, null, null, dxgi);

        [Fact]
        public void DxgiIsOnUnlessTheFileOrTheCommandLineSaysOff()
        {
            var o = WithDxgi(new PlayerSettings());
            Assert.Equal((true, OptionSource.Default), (o.Dxgi, o.DxgiSource));
            var file = PlayerSettings.Parse("""{ "dxgi": false }""");
            o = WithDxgi(file);
            Assert.Equal((false, OptionSource.SettingsFile), (o.Dxgi, o.DxgiSource));
            o = WithDxgi(file, dxgi: true);
            Assert.Equal((true, OptionSource.CommandLine), (o.Dxgi, o.DxgiSource));
            o = WithDxgi(new PlayerSettings(), dxgi: false);
            Assert.Equal((false, OptionSource.CommandLine), (o.Dxgi, o.DxgiSource));
            Assert.Contains("dxgi off (the command line)", o.Describe());
            Assert.Contains("dxgi on (default)", WithDxgi(new PlayerSettings()).Describe());
        }

        [Fact]
        public void TheFileRecordsDxgiOnlyWhenOff()
        {
            var none = new Dictionary<string, bool>();
            var saved = EffectiveOptions.Apply(new PlayerSettings(), Changing, none, null, null, dxgi: false);
            Assert.Equal("{\"dxgi\":false}", saved.ToJson(indented: false));
            Assert.False(PlayerSettings.Parse(saved.ToJson()).Dxgi);
            Assert.Null(EffectiveOptions.Apply(saved, Changing, none, null, null, dxgi: true).Dxgi);
            // A screen save that does not touch it keeps what the file had.
            Assert.False(EffectiveOptions.Apply(saved, Changing, none, true, null).Dxgi);
            Assert.Throws<InvalidOperationException>(() => PlayerSettings.Parse("""{ "dxgi": "yes" }"""));
        }

        // ----- Windows, DXGI: repeats while no frame comes -----

        static EffectiveOptions WithStallRepeats(PlayerSettings file, bool? stallRepeats = null)
            => EffectiveOptions.Resolve(All, Changing, [], null, false, file, commandLineStallRepeats: stallRepeats);

        [Fact]
        public void StallRepeatsAreOnUnlessTheFileOrTheCommandLineSaysOff()
        {
            var o = WithStallRepeats(new PlayerSettings());
            Assert.Equal((true, OptionSource.Default), (o.StallRepeats, o.StallRepeatsSource));
            var file = PlayerSettings.Parse("""{ "stallRepeats": false }""");
            o = WithStallRepeats(file);
            Assert.Equal((false, OptionSource.SettingsFile), (o.StallRepeats, o.StallRepeatsSource));
            o = WithStallRepeats(file, stallRepeats: true);
            Assert.Equal((true, OptionSource.CommandLine), (o.StallRepeats, o.StallRepeatsSource));
            o = WithStallRepeats(new PlayerSettings(), stallRepeats: false);
            Assert.Contains("stall repeats off (the command line)", o.Describe());
        }

        [Fact]
        public void TheFileRecordsStallRepeatsOnlyWhenOff()
        {
            var none = new Dictionary<string, bool>();
            var saved = EffectiveOptions.Apply(new PlayerSettings(), Changing, none, null, null, stallRepeats: false);
            Assert.Equal("{\"stallRepeats\":false}", saved.ToJson(indented: false));
            Assert.False(PlayerSettings.Parse(saved.ToJson()).StallRepeats);
            Assert.Null(EffectiveOptions.Apply(saved, Changing, none, null, null, stallRepeats: true).StallRepeats);
            Assert.False(EffectiveOptions.Apply(saved, Changing, none, true, null).StallRepeats);
        }

        [Fact]
        public void TheFileRecordsTheUpdateCheckOnlyWhenOff()
        {
            var none = new Dictionary<string, bool>();
            var saved = EffectiveOptions.Apply(new PlayerSettings(), Changing, none, null, null, updateCheck: false);
            Assert.Equal("{\"updateCheck\":false}", saved.ToJson(indented: false));
            Assert.False(PlayerSettings.Parse(saved.ToJson()).UpdateCheck);
            Assert.Equal((false, OptionSource.SettingsFile), (Resolve(saved).UpdateCheck, Resolve(saved).UpdateCheckSource));
            Assert.Equal((true, OptionSource.Default), (Resolve(new PlayerSettings()).UpdateCheck, Resolve(new PlayerSettings()).UpdateCheckSource));
            Assert.Null(EffectiveOptions.Apply(saved, Changing, none, null, null, updateCheck: true).UpdateCheck);
            Assert.False(EffectiveOptions.Apply(saved, Changing, none, true, null).UpdateCheck);
        }

        // ----- Garbage collection -----

        static EffectiveOptions WithGc(PlayerSettings file, string gc = null)
            => EffectiveOptions.Resolve(All, Changing, [], null, false, file, commandLineGc: gc);

        [Fact]
        public void GcIsFastUnlessTheFileOrTheCommandLineSaysLowMemory()
        {
            var o = WithGc(new PlayerSettings());
            Assert.Equal(("fast", OptionSource.Default), (o.Gc, o.GcSource));
            var file = PlayerSettings.Parse("""{ "gc": "low-memory" }""");
            o = WithGc(file);
            Assert.Equal(("low-memory", OptionSource.SettingsFile), (o.Gc, o.GcSource));
            Assert.Contains("gc low-memory (the settings file)", o.Describe());
            o = WithGc(file, "fast");
            Assert.Equal(("fast", OptionSource.CommandLine), (o.Gc, o.GcSource));
            o = WithGc(new PlayerSettings(), "low-memory");
            Assert.Equal(("low-memory", OptionSource.CommandLine), (o.Gc, o.GcSource));
        }

        [Fact]
        public void TheFileRecordsGcOnlyWhenLowMemory()
        {
            var none = new Dictionary<string, bool>();
            var saved = EffectiveOptions.Apply(new PlayerSettings(), Changing, none, null, null, gc: "low-memory");
            Assert.Equal("{\"gc\":\"low-memory\"}", saved.ToJson(indented: false));
            // OniMoono.exe scans the indented file for this: the key, a colon, the value.
            Assert.Contains("\"gc\": \"low-memory\"", saved.ToJson());
            Assert.Equal("low-memory", PlayerSettings.Parse(saved.ToJson()).Gc);
            Assert.Null(EffectiveOptions.Apply(saved, Changing, none, null, null, gc: "fast").Gc);
            // A screen save that does not touch it keeps what the file had.
            Assert.Equal("low-memory", EffectiveOptions.Apply(saved, Changing, none, true, null).Gc);
            Assert.Throws<FormatException>(() => PlayerSettings.Parse("""{ "gc": "workstation" }"""));
        }
    }
}
