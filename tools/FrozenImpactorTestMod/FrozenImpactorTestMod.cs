using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace FrozenImpactorTestMod
{
    /// Holds the Demolior's shader inputs still (tests/e2e/d3d11-coverage-impactor.yaml), so two
    /// runs, and two renderers, draw the same pixels and the scene can be judged
    /// (docs/research/d3d11-plan.md, M3 review fixes, finding 5). Each frame the game gives
    /// those materials the real time and new random numbers (U60 764025):
    /// - ParallaxBackgroundObject.LateUpdate, the planet (Klei/DLC4/DemoliorBackground):
    ///   `_UnscaledTime` from Time.unscaledTime and `_Random` from two Random.value;
    /// - LargeImpactorVisualizerEffect.OnPostRender, the landing zone's grid
    ///   (Klei/PostFX/LargeImpactorVisualizerShader): the fold check, `_EntryStartTime` and
    ///   `_UnscaledTime`, each from Time.unscaledTime.
    /// Those reads become constants: the time 1000 s, the entry's start 1 s (so the entry effect
    /// is long over; not 0, as the grid's program takes the start's sign), the random numbers
    /// 0.5. Nothing else changes. A method whose reads are not the ones listed fails the load, so
    /// a new build cannot pass unfrozen.
    public sealed class Mod : UserMod2
    {
        const float Time = 1000f, EntryStart = 1f, RandomValue = 0.5f;

        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            harmony.Patch(AccessTools.Method(typeof(ParallaxBackgroundObject), "LateUpdate"), transpiler: new HarmonyMethod(typeof(Mod), nameof(Planet)));
            harmony.Patch(AccessTools.Method(typeof(LargeImpactorVisualizerEffect), "OnPostRender"), transpiler: new HarmonyMethod(typeof(Mod), nameof(Grid)));
            UnityEngine.Debug.Log("FrozenImpactor: installed");
        }

        static readonly MethodInfo UnscaledTime = AccessTools.PropertyGetter(typeof(UnityEngine.Time), nameof(UnityEngine.Time.unscaledTime));
        static readonly MethodInfo RandomValueGetter = AccessTools.PropertyGetter(typeof(UnityEngine.Random), nameof(UnityEngine.Random.value));

        static IEnumerable<CodeInstruction> Planet(IEnumerable<CodeInstruction> code) => Replace(code, "ParallaxBackgroundObject.LateUpdate", [Time], 2);

        static IEnumerable<CodeInstruction> Grid(IEnumerable<CodeInstruction> code) => Replace(code, "LargeImpactorVisualizerEffect.OnPostRender", [Time, EntryStart, Time], 0);

        /// Each Time.unscaledTime read becomes the next of `times`, each Random.value read
        /// RandomValue; the counts must be exactly these.
        static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> code, string where, float[] times, int randoms)
        {
            var list = new List<CodeInstruction>(code);
            int t = 0, r = 0;
            foreach (var c in list)
            {
                if (c.Calls(UnscaledTime))
                {
                    if (t >= times.Length) throw new InvalidOperationException($"FrozenImpactor: {where} reads Time.unscaledTime more than {times.Length} times");
                    Become(c, times[t++]);
                }
                else if (c.Calls(RandomValueGetter))
                {
                    r++;
                    Become(c, RandomValue);
                }
            }
            if (t != times.Length || r != randoms)
                throw new InvalidOperationException($"FrozenImpactor: {where} reads Time.unscaledTime {t} times and Random.value {r} times, not {times.Length} and {randoms}");
            UnityEngine.Debug.Log($"FrozenImpactor: {where}: {t} time and {r} random reads held");
            return list;
        }

        // In place, keeping the instruction's labels and blocks.
        static void Become(CodeInstruction c, float value)
        {
            c.opcode = System.Reflection.Emit.OpCodes.Ldc_R4;
            c.operand = value;
        }
    }
}
