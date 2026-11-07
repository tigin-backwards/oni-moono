using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The fast-anim-events game tweak's stand-down: the game's Harmony (2.4.2) skips every later
    /// prefix that can affect the original once one returned false, so a mod's bool prefix on the
    /// loop the tweak replaces never runs while the tweak's does; and the policy that decides when
    /// the tweak leaves the game's loop in place (ProbeRepairPolicy.Counted and Foreign,
    /// src/OniMoono.GameTweaks/ProbeRepairCore.cs, compiled in here).
    public class FastAnimEventsTests
    {
        /// Stands in for AnimEventManager's three-argument Update.
        public class Loop
        {
            public static readonly List<string> Calls = new();

            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Update(float dt) => Calls.Add("game");
        }

        public static class Patches
        {
            public static bool Tweak() { Loop.Calls.Add("tweak"); return false; }
            public static bool ModBool() { Loop.Calls.Add("mod bool"); return true; }
            public static bool ModBoolSeesRunOriginal(bool __runOriginal) { Loop.Calls.Add($"mod bool {__runOriginal}"); return true; }
            public static void ModVoid(bool __runOriginal) { Loop.Calls.Add($"mod void {__runOriginal}"); }
            public static void ModRef(ref float dt) { Loop.Calls.Add("mod ref"); }
            public static void ModPostfix() => Loop.Calls.Add("mod postfix");
        }

        [Fact]
        public void FalsePrefixSkipsLaterPrefixesThatCanAffectTheOriginal()
        {
            var update = AccessTools.Method(typeof(Loop), nameof(Loop.Update));
            var tweak = new Harmony("test.fastanimevents.tweak");
            var mod = new Harmony("test.fastanimevents.mod");
            HarmonyMethod M(string name) => new HarmonyMethod(typeof(Patches), name);
            try
            {
                // The tweaks install before any mod loads; at the same priority theirs run first.
                tweak.Patch(update, prefix: M(nameof(Patches.Tweak)));
                mod.Patch(update, prefix: M(nameof(Patches.ModBool)));
                mod.Patch(update, prefix: M(nameof(Patches.ModBoolSeesRunOriginal)));
                mod.Patch(update, prefix: M(nameof(Patches.ModVoid)));
                mod.Patch(update, prefix: M(nameof(Patches.ModRef)));
                mod.Patch(update, postfix: M(nameof(Patches.ModPostfix)));
                Loop.Calls.Clear();
                new Loop().Update(1f);
                // Bool and ref prefixes after the false return are skipped, as is the original;
                // a void prefix without refs still runs (and sees __runOriginal false), as do
                // postfixes.
                Assert.Equal(new[] { "tweak", "mod void False", "mod postfix" }, Loop.Calls);

                // A mod's bool prefix at a higher priority runs first, and the tweak after it.
                mod.Unpatch(update, HarmonyPatchType.All, mod.Id);
                mod.Patch(update, prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.ModBool)) { priority = Priority.High });
                Loop.Calls.Clear();
                new Loop().Update(1f);
                Assert.Equal(new[] { "mod bool", "tweak" }, Loop.Calls);
            }
            finally
            {
                mod.UnpatchAll(mod.Id);
                tweak.UnpatchAll(tweak.Id);
            }
        }

        const string Ours = "OniMoono.Host.GameTweaks.fast-anim-events";
        static readonly string[] None = new string[0];

        static string Foreign(ProbeRepairPolicy.GuardedKinds kinds, string[] prefixes = null, string[] postfixes = null, string[] transpilers = null,
            string[] finalizers = null, string[] inner = null, string method = "AnimEventManager.Update") =>
            ProbeRepairPolicy.Foreign(new[]
            {
                new KeyValuePair<string, IEnumerable<string>>(method,
                    ProbeRepairPolicy.Counted(kinds, prefixes ?? None, postfixes ?? None, transpilers ?? None, finalizers ?? None, inner ?? None)),
            });

        [Fact]
        public void OurPatchesAndPerfProbeNeverCount()
        {
            Assert.Null(Foreign(ProbeRepairPolicy.GuardedKinds.All, prefixes: new[] { Ours, "PerfProbe.Frames" },
                postfixes: new[] { "OniMoono.Host.GameTweaks.frame-stats", "PerfProbe" }));
        }

        [Fact]
        public void TheLoopCountsAllButPostfixes()
        {
            var kinds = ProbeRepairPolicy.GuardedKinds.AllButPostfixes;
            // Advanced Batteries' postfix on the public Update.
            Assert.Null(Foreign(kinds, prefixes: new[] { Ours }, postfixes: new[] { "Advanced.Batteries" }));
            // ONI Multiplayer's bool prefix (its asteroid freeze).
            Assert.Equal("OniMp patches AnimEventManager.Update", Foreign(kinds, prefixes: new[] { Ours, "OniMp" }));
            // Cooperative Colonies' transpiler; a mod's postfix and inner patch.
            Assert.Equal("coop patches AnimEventManager.Update", Foreign(kinds, transpilers: new[] { "coop" }, postfixes: new[] { "coop" }));
            Assert.Equal("some.mod patches AnimEventManager.Update", Foreign(kinds, inner: new[] { "some.mod" }));
            Assert.Equal("some.mod patches AnimEventManager.Update", Foreign(kinds, finalizers: new[] { "some.mod" }));
        }

        [Fact]
        public void CopiedMethodsCountEveryPatch()
        {
            Assert.Equal("some.mod patches AnimEventManager.PlayEvents",
                Foreign(ProbeRepairPolicy.GuardedKinds.All, postfixes: new[] { "some.mod" }, method: "AnimEventManager.PlayEvents"));
        }

        [Fact]
        public void FlaggedMethodsCountOnlyFinalizers()
        {
            var kinds = ProbeRepairPolicy.GuardedKinds.Finalizers;
            // Fast Track's transpiler on StartQueuedAnim; ONI Together's prefix on Queue.
            Assert.Null(Foreign(kinds, transpilers: new[] { "PeterHan.FastTrack" }, postfixes: new[] { Ours }, method: "KAnimControllerBase.StartQueuedAnim"));
            Assert.Null(Foreign(kinds, prefixes: new[] { "ONI_Together" }, inner: new[] { "x" }, method: "KAnimControllerBase.Queue"));
            Assert.Equal("some.mod patches KAnimControllerBase.Queue", Foreign(kinds, finalizers: new[] { "some.mod" }, method: "KAnimControllerBase.Queue"));
        }
    }
}
