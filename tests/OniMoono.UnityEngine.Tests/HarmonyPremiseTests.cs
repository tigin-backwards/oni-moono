using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Xunit;

namespace OniMoono.Tests
{
    /// What the game's Harmony does where the game tweaks' stand-down design relies on it.
    public class HarmonyPremiseTests
    {
        public class Game
        {
            public static readonly List<string> Calls = new();

            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Update() => Helper(Calls);

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Helper(List<string> calls) => calls.Add("game");

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Generic<T>(T value, List<string> calls) => calls.Add("generic");
        }

        static readonly MethodInfo helper = AccessTools.Method(typeof(Game), nameof(Game.Helper));

        public static class Tweak
        {
            public static void Helper(List<string> calls) => calls.Add("tweak");

            /// A tweak's transpiler that throws when the call it rewrites is not there.
            public static IEnumerable<CodeInstruction> Strict(IEnumerable<CodeInstruction> instructions)
            {
                int n = 0;
                foreach (var i in instructions)
                {
                    if (i.Calls(helper)) { i.operand = AccessTools.Method(typeof(Tweak), nameof(Helper)); n++; }
                    yield return i;
                }
                if (n != 1) throw new InvalidOperationException("not as expected");
            }
        }

        public static class Mod
        {
            public static void Helper(List<string> calls) => calls.Add("mod");

            /// A mod's transpiler that takes the call first.
            public static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var i in instructions)
                {
                    if (i.Calls(helper)) i.operand = AccessTools.Method(typeof(Mod), nameof(Helper));
                    yield return i;
                }
            }

            public static void Before() { }
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void ATweaksThrowingTranspilerFailsALaterModsPatch()
        {
            var update = AccessTools.Method(typeof(Game), nameof(Game.Update));
            var tweaks = new Harmony("test.premise.tweaks");
            var mod = new Harmony("test.premise.mod");
            try
            {
                tweaks.Patch(update, transpiler: new HarmonyMethod(typeof(Tweak), nameof(Tweak.Strict)));
                // The mod's transpiler runs first (a higher priority) and takes the call: the
                // tweak's runs again inside the mod's Patch, and its exception comes out there.
                var thrown = Record.Exception(() => mod.Patch(update, transpiler: new HarmonyMethod(typeof(Mod), nameof(Mod.Replace)) { priority = Priority.High }));
                Assert.NotNull(thrown);
            }
            finally
            {
                mod.UnpatchAll(mod.Id);
                tweaks.UnpatchAll(tweaks.Id);
            }
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void PatchInfoOfAGenericDefinitionMissesItsInstantiationsPatches()
        {
            var definition = AccessTools.Method(typeof(Game), nameof(Game.Generic));
            var closed = definition.MakeGenericMethod(typeof(string));
            var mod = new Harmony("test.premise.generic");
            try
            {
                mod.Patch(closed, prefix: new HarmonyMethod(typeof(Mod), nameof(Mod.Before)));
                Assert.NotNull(Harmony.GetPatchInfo(closed));
                Assert.Null(Harmony.GetPatchInfo(definition));
            }
            finally
            {
                mod.UnpatchAll(mod.Id);
            }
        }
    }
}
