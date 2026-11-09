using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The game tweaks' call-site transpilers (src/OniMoono.GameTweaks/CallSiteReroute.cs,
    /// compiled in here) run after every mod's. Issue #41: fast-overlays rerouted the overlay
    /// modes' AddTargetIfVisible calls before Fast Track's CullConduits transpiler could replace
    /// them with its own, which also shows the conduits it hides inside solid tiles; those wires
    /// and pipes then never appeared in the overlays.
    public class CallSiteRerouteTests
    {
        /// Stands in for an overlay mode: Update calls a generic helper, as the modes call
        /// Mode.AddTargetIfVisible<SaveLoadRoot>.
        public class Mode
        {
            public static readonly List<string> Calls = new();

            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Update() => AddTarget(this, Calls);

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void AddTarget<T>(T target, List<string> calls) => calls.Add("game");
        }

        /// The tweak's version of the helper.
        public static class Tweak
        {
            public static void AddTarget<T>(T target, List<string> calls) => calls.Add("tweak");

            public static IEnumerable<CodeInstruction> Reroute(IEnumerable<CodeInstruction> instructions)
                => CallSiteReroute.Rewrite(instructions, d => d == helper ? AccessTools.Method(typeof(Tweak), nameof(AddTarget)) : null);
        }

        /// A mod replacing the call by its exact target, as PLib's ReplaceMethodCallSafe does.
        public static class Mod
        {
            public static void AddTarget(Mode target, List<string> calls) => calls.Add("mod");

            public static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions)
            {
                var target = helper.MakeGenericMethod(typeof(Mode));
                foreach (var instruction in instructions)
                {
                    if (instruction.operand is MethodInfo m && m == target)
                        instruction.operand = AccessTools.Method(typeof(Mod), nameof(AddTarget));
                    yield return instruction;
                }
            }
        }

        static readonly MethodInfo helper = AccessTools.Method(typeof(Mode), nameof(Mode.AddTarget));

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void ModsTranspilerFindsTheCallTheTweakReroutes()
        {
            var update = AccessTools.Method(typeof(Mode), nameof(Mode.Update));
            var tweaks = new Harmony("test.callsitereroute.tweaks");
            var mod = new Harmony("test.callsitereroute.mod");
            try
            {
                // The tweaks install before any mod loads.
                tweaks.Patch(update, transpiler: CallSiteReroute.Transpiler(typeof(Tweak), nameof(Tweak.Reroute)));
                Mode.Calls.Clear();
                new Mode().Update();
                Assert.Equal(new[] { "tweak" }, Mode.Calls);

                mod.Patch(update, transpiler: new HarmonyMethod(typeof(Mod), nameof(Mod.Replace)));
                Mode.Calls.Clear();
                new Mode().Update();
                Assert.Equal(new[] { "mod" }, Mode.Calls);
            }
            finally
            {
                mod.UnpatchAll(mod.Id);
                tweaks.UnpatchAll(tweaks.Id);
            }
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void TweaksTranspilersRunLast() => Assert.Equal(Priority.Last, CallSiteReroute.Transpiler(typeof(Tweak), nameof(Tweak.Reroute)).priority);
    }
}
