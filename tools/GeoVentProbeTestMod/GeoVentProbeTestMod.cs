using HarmonyLib;
using KMod;
using UnityEngine;

namespace GeoVentProbeTestMod
{
    /// Measures the geothermal vents (#61, tests/e2e/fix-geothermal-vents.yaml): each sim step
    /// (Game.StepTheSim, with or without time), the mass the sim reports emitted by each vent's
    /// emitter right after that step, the mass the vent's own code accounts
    /// (GeothermalVent.OnMassEmitted), and its Sim200ms calls. Logs a line every 10 game seconds:
    /// `GeoVentProbe: ...`.
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            harmony.Patch(AccessTools.Method(typeof(Game), "StepTheSim"), postfix: new HarmonyMethod(typeof(Mod), nameof(StepPost)));
            harmony.Patch(AccessTools.Method(typeof(GeothermalVent), "OnMassEmitted"), prefix: new HarmonyMethod(typeof(Mod), nameof(EmittedPre)));
            harmony.Patch(AccessTools.Method(typeof(GeothermalVent), "Sim200ms"), prefix: new HarmonyMethod(typeof(Mod), nameof(Sim200Pre)));
            harmony.Patch(AccessTools.Method(typeof(Game), "SimEveryTick"), postfix: new HarmonyMethod(typeof(Mod), nameof(TickPost)));
            Debug.Log("GeoVentProbe: installed");
        }

        /// Pushes KG of steam at 600 K through each geothermal controller to its enabled vents, as
        /// its own venting does (GeothermalController.PushToVents), for specs (setup.call).
        public static void Fill(float kg)
        {
            var steam = ElementLoader.FindElementByHash(SimHashes.Steam);
            foreach (var controller in UnityEngine.Object.FindObjectsByType<GeothermalController>(FindObjectsSortMode.None))
            {
                controller.PushToVents(new GeothermalVent.ElementInfo
                {
                    isSolid = false, elementHash = SimHashes.Steam, elementIdx = steam.idx, mass = kg, temperature = 600f, diseaseIdx = byte.MaxValue,
                });
                Debug.Log($"GeoVentProbe: pushed {kg} kg of steam through {controller.GetProperName()}");
            }
        }

        static readonly AccessTools.FieldRef<GeothermalVent, GeothermalVent.EmitterInfo> emitter =
            AccessTools.FieldRefAccess<GeothermalVent, GeothermalVent.EmitterInfo>("emitterInfo");

        static int steps, zeroSteps, frames, ventCalls, zeroStepsNonzero, framesProcessed;
        static float reported, reportedAfterZero, accounted, seenAtSim200;
        static float nextLog = -1f;

        /// Since the load: the mass the sim reported emitted by the vents (after every step of the
        /// sim), the mass the vents took off what they have left (OnMassEmitted), and the steps
        /// without time (frames too short for a sim sub-tick), for specs (`type:` state).
        public static float Emitted, Accounted;
        public static int StepsWithoutTime;

        /// What the vents have left, together (GeothermalVent.MaterialAvailable).
        public static float Left
        {
            get
            {
                float left = 0f;
                foreach (int world in Components.GeothermalVents.GetWorldsIds())
                    foreach (GeothermalVent vent in Components.GeothermalVents.GetItems(world))
                        left += vent.MaterialAvailable();
                return left;
            }
        }

        static unsafe float Entry(GeothermalVent vent)
        {
            int handle = emitter(vent).simHandle;
            if (!Sim.IsValidHandle(handle) || Game.Instance.simData.emittedMassEntries == null) return 0f;
            return Game.Instance.simData.emittedMassEntries[Sim.GetHandleIndex(handle)].mass;
        }

        static unsafe void StepPost(float dt, Sim.GameDataUpdate* __result)
        {
            if (__result == null) return;
            framesProcessed += __result->numFramesProcessed;
            if (dt > 0f) steps++; else { zeroSteps++; StepsWithoutTime++; }
            foreach (int world in Components.GeothermalVents.GetWorldsIds())
                foreach (GeothermalVent vent in Components.GeothermalVents.GetItems(world))
                {
                    float m = Entry(vent);
                    Emitted += m;
                    if (dt > 0f) reported += m;
                    else if (m > 0f) { reportedAfterZero += m; zeroStepsNonzero++; }
                }
        }

        static void EmittedPre(float mass)
        {
            accounted += mass;
            Accounted += mass;
        }

        static void Sim200Pre(GeothermalVent __instance, float dt)
        {
            ventCalls++;
            if (dt > 0f) seenAtSim200 += Entry(__instance);
        }

        static void TickPost()
        {
            frames++;
            if (GameClock.Instance == null) return;
            float now = GameClock.Instance.GetTime();
            if (nextLog < 0f) { nextLog = now + 10f; return; }
            if (now < nextLog) return;
            var parts = new System.Text.StringBuilder();
            foreach (int world in Components.GeothermalVents.GetWorldsIds())
                foreach (GeothermalVent vent in Components.GeothermalVents.GetItems(world))
                    parts.Append($" [{vent.GetProperName()} left={vent.MaterialAvailable():F1} over={vent.IsOverPressure()} state={vent.smi?.GetCurrentState()?.name}]");
            Debug.Log($"GeoVentProbe: t={now:F1} speed={Time.timeScale} frames={frames} steps={steps} zeroSteps={zeroSteps} framesProcessed={framesProcessed} " +
                      $"ventSim200={ventCalls} reported={reported:F2} seenAtSim200={seenAtSim200:F2} accounted={accounted:F2} " +
                      $"afterZero={reportedAfterZero:F2}/{zeroStepsNonzero}{parts}");
            nextLog = now + 10f;
            steps = zeroSteps = frames = ventCalls = zeroStepsNonzero = framesProcessed = 0;
            reported = reportedAfterZero = accounted = seenAtSim200 = 0f;
        }
    }
}
