using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The tweaks' stand-down check (src/OniMoono.GameTweaks/ForeignPatches.cs, compiled in
    /// here): which owners' patches of a guarded method count, by kind.
    public class ForeignPatchesTests
    {
        public class Game
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Update() { }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Other() { }
        }

        public static class Patches
        {
            public static void Before() { }
            public static void After() { }
            public static IEnumerable<CodeInstruction> Same(IEnumerable<CodeInstruction> instructions) => instructions;
            public static Exception Finally(Exception __exception) => __exception;
        }

        const string Ours = "OniMoono.Host.GameTweaks.fast-anim-events";
        static readonly MethodInfo update = AccessTools.Method(typeof(Game), nameof(Game.Update));
        static readonly MethodInfo other = AccessTools.Method(typeof(Game), nameof(Game.Other));

        /// What Find answers for Game.Update patched by these owners, each kind's in turn.
        static string Find(ProbeRepairPolicy.GuardedKinds kinds, string[] prefixes = null, string[] postfixes = null, string[] transpilers = null, string[] finalizers = null)
        {
            var owners = new List<Harmony>();
            Harmony Owner(string id)
            {
                var harmony = new Harmony(id);
                owners.Add(harmony);
                return harmony;
            }
            try
            {
                foreach (var id in prefixes ?? new string[0]) Owner(id).Patch(update, prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.Before)));
                foreach (var id in postfixes ?? new string[0]) Owner(id).Patch(update, postfix: new HarmonyMethod(typeof(Patches), nameof(Patches.After)));
                foreach (var id in transpilers ?? new string[0]) Owner(id).Patch(update, transpiler: new HarmonyMethod(typeof(Patches), nameof(Patches.Same)));
                foreach (var id in finalizers ?? new string[0]) Owner(id).Patch(update, finalizer: new HarmonyMethod(typeof(Patches), nameof(Patches.Finally)));
                return ForeignPatches.Find(new MethodBase[] { other, null, update }, kinds);
            }
            finally
            {
                foreach (var harmony in owners) harmony.Unpatch(update, HarmonyPatchType.All, harmony.Id);
            }
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void OursFrameStatsAndPerfProbeNeverCount()
        {
            Assert.Null(Find(ProbeRepairPolicy.GuardedKinds.All, prefixes: new[] { Ours, "PerfProbe.Frames", "OniMoono.Host.FrameStats" },
                postfixes: new[] { "OniMoono.Host.FrameStats", "PerfProbe" }, transpilers: new[] { "OniMoono.Host.FrameStats" }));
            Assert.Null(ForeignPatches.Find(other));
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void EveryKindCountsByDefault()
        {
            var kinds = ProbeRepairPolicy.GuardedKinds.All;
            Assert.Equal("some.mod patches Game.Update", Find(kinds, prefixes: new[] { Ours, "some.mod" }));
            Assert.Equal("some.mod patches Game.Update", Find(kinds, postfixes: new[] { "some.mod" }));
            Assert.Equal("some.mod patches Game.Update", Find(kinds, transpilers: new[] { "some.mod" }));
            Assert.Equal("some.mod patches Game.Update", Find(kinds, finalizers: new[] { "some.mod" }));
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void AllButPostfixesPassesOverAPostfix()
        {
            var kinds = ProbeRepairPolicy.GuardedKinds.AllButPostfixes;
            Assert.Null(Find(kinds, prefixes: new[] { Ours }, postfixes: new[] { "Advanced.Batteries" }));
            Assert.Equal("OniMp patches Game.Update", Find(kinds, prefixes: new[] { Ours, "OniMp" }));
            Assert.Equal("coop patches Game.Update", Find(kinds, transpilers: new[] { "coop" }, postfixes: new[] { "other" }));
            Assert.Equal("some.mod patches Game.Update", Find(kinds, finalizers: new[] { "some.mod" }));
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void FinalizersCountsOnlyFinalizers()
        {
            var kinds = ProbeRepairPolicy.GuardedKinds.Finalizers;
            Assert.Null(Find(kinds, transpilers: new[] { "PeterHan.FastTrack" }, postfixes: new[] { "a.mod" }, prefixes: new[] { "ONI_Together" }));
            Assert.Equal("some.mod patches Game.Update", Find(kinds, finalizers: new[] { "some.mod" }));
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void AListsKindsGoByMethodAndAMissingMethodCanCount()
        {
            var mod = new Harmony("some.mod");
            try
            {
                mod.Patch(update, postfix: new HarmonyMethod(typeof(Patches), nameof(Patches.After)));
                IEnumerable<MethodBase> list = new MethodBase[] { other, update };
                Assert.Equal("some.mod patches Game.Update", ForeignPatches.Find(list));
                Assert.Null(ForeignPatches.Find(list, m => m == update ? ProbeRepairPolicy.GuardedKinds.AllButPostfixes : ProbeRepairPolicy.GuardedKinds.All));
                Assert.Null(ForeignPatches.Find(new MethodBase[] { null, other }));
                Assert.Equal("a method was not found", ForeignPatches.Find(new MethodBase[] { null, update }, missing: "a method was not found"));
            }
            finally
            {
                mod.Unpatch(update, HarmonyPatchType.All, mod.Id);
            }
        }
    }
}
