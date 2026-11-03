using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Host;
using OniMoono.Host.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The garbage collection option (src/OniMoono.Host/GcMode.cs, compiled in here): what the
    /// host does with the runtime's GC as it started, and what the log says.
    public class GcModeTests
    {
        static Func<string, string> Env(params (string Name, string Value)[] variables)
            => name => variables.FirstOrDefault(v => v.Name == name).Value;

        [Theory]
        [InlineData("fast", true, true, "Applied")]
        [InlineData("low-memory", false, true, "Applied")]
        // Linux and macOS start again with the other GC; Windows' exe sets it before the runtime.
        [InlineData("low-memory", true, true, "Relaunch")]
        [InlineData("fast", false, true, "Relaunch")]
        [InlineData("low-memory", true, false, "NotApplied")]
        public void ThePlanFollowsTheChoiceAndTheRuntime(string choice, bool isServer, bool canRelaunch, string expected)
            => Assert.Equal(expected, GcMode.Plan(choice, isServer, Env(), canRelaunch).ToString());

        [Fact]
        public void TheUsersVariableWinsAndIsNamed()
        {
            var env = Env(("DOTNET_gcServer", "1"));
            Assert.Equal(GcMode.Outcome.Environment, GcMode.Plan("low-memory", true, env, canRelaunch: true));
            Assert.Equal("DOTNET_gcServer=1", GcMode.UserVariable(env));
            Assert.Equal("COMPlus_gcServer=0", GcMode.UserVariable(Env(("COMPlus_gcServer", "0"))));
            Assert.Contains("DOTNET_gcServer=1 in the environment wins over the gc option (low-memory, the settings file)",
                GcMode.LogLine("Server GC", "low-memory", "the settings file", GcMode.Outcome.Environment, env));
        }

        [Fact]
        public void ARunStartedAgainNeverStartsAgain()
        {
            // Its DOTNET_gcServer is the host's own, not the user's.
            var env = Env(("ONI_MOONO_GC_RELAUNCH", "low-memory"), ("DOTNET_gcServer", "0"));
            Assert.Null(GcMode.UserVariable(env));
            Assert.Equal(GcMode.Outcome.Relaunched, GcMode.Plan("low-memory", false, env, canRelaunch: true));
            Assert.Equal(GcMode.Outcome.NotApplied, GcMode.Plan("low-memory", true, env, canRelaunch: true));
        }

        [Fact]
        public void TheRelaunchEnvironmentKeepsTheRestAndSetsTheVariables()
        {
            var current = new Hashtable { ["PATH"] = "/usr/bin", ["DOTNET_gcServer"] = "1" };
            var env = GcMode.RelaunchEnvironment(current, "low-memory");
            Assert.Equal(["DOTNET_gcServer=0", "ONI_MOONO_GC_RELAUNCH=low-memory", "PATH=/usr/bin"], env);
            Assert.Contains("DOTNET_gcServer=1", GcMode.RelaunchEnvironment(current, "fast"));
        }

        [Fact]
        public void TheLogNamesTheGcByItsTechnicalName()
        {
            var datas = new Dictionary<string, object> { ["ServerGC"] = true, ["ConcurrentGC"] = true, ["HeapCount"] = 16, ["GCDynamicAdaptationMode"] = 1 };
            Assert.Equal("Server GC with DATAS (up to 16 heaps), concurrent", GcMode.Describe(true, datas));
            var capped = new Dictionary<string, object> { ["ServerGC"] = true, ["ConcurrentGC"] = true, ["HeapCount"] = 4, ["GCDynamicAdaptationMode"] = 0 };
            Assert.Equal("Server GC with 4 heaps (DATAS off), concurrent", GcMode.Describe(true, capped));
            var workstation = new Dictionary<string, object> { ["ServerGC"] = false, ["ConcurrentGC"] = true, ["HeapCount"] = 1 };
            Assert.Equal("Workstation concurrent GC", GcMode.Describe(false, workstation));
            Assert.Equal("gc: Workstation concurrent GC: the gc option is low-memory (the command line); started again with DOTNET_gcServer=0 to apply it",
                GcMode.LogLine("Workstation concurrent GC", "low-memory", "the command line", GcMode.Outcome.Relaunched, Env()));
            Assert.Equal("gc config: ConcurrentGC=True, HeapCount=4", GcMode.ConfigLine(new Dictionary<string, object> { ["HeapCount"] = 4, ["ConcurrentGC"] = true }));
        }

        [Fact]
        public void TheRowOffersFastFirstWithoutTechnicalNames()
        {
            var row = GameTweakNames.Rows.Single(r => r.Id == GameTweakNames.GcRow);
            Assert.Equal(["fast", "low-memory"], row.Choices);
            Assert.Equal("--gc fast|low-memory", row.LaunchOption);
            Assert.False(row.IsTweak);
            Assert.Equal(GameTweakNames.General, row.Group);
            // Players who read "server" may turn it off thinking it's not for them.
            foreach (var text in new[] { row.Title, row.Effect, row.Detail, row.LaunchOption })
            {
                Assert.DoesNotContain("server", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("workstation", text, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal("low memory", TweakStrings.All.Single(s => s.Key == TweakStrings.ChoiceKey(row, "low-memory")).English);
        }

        [Fact]
        public void TheRuntimeHereHasTheSettingsTheLogReads()
        {
            // GC.GetConfigurationVariables names what Describe reads (on any GC).
            var config = GC.GetConfigurationVariables();
            Assert.True(config.ContainsKey("ConcurrentGC"));
            Assert.True(config.ContainsKey("HeapCount"));
            Assert.True(config.ContainsKey("GCDynamicAdaptationMode"));
        }
    }
}
