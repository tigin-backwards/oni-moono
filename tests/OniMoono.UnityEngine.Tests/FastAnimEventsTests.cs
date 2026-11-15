using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The fast-anim-events game tweak's stand-down: the game's Harmony (2.4.2) skips every later
    /// prefix that can affect the original once one returned false, so a mod's bool prefix on the
    /// loop the tweak replaces never runs while the tweak's does. Which patches make it leave
    /// the game's loop in place: ForeignPatchesTests.
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

        [NeedsGameAssembly("0Harmony")]
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
    }
}
