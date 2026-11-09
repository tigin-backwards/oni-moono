using System;
using System.Collections.Generic;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using ElementInfo = GeothermalVent.ElementInfo;
    using EmitterInfo = GeothermalVent.EmitterInfo;

    /// Game tweak `fix-geothermal-vents` (on by default, the player's choice; it changes what the
    /// game does, so it is left out of `exact`): U60's fix of the geothermal vents, on U59 (#61).
    ///
    /// A vent emits through a sim element emitter, 3 kg every 0.2 s, and takes what it emitted off
    /// what it has left (GeothermalVent.OnMassEmitted), from the mass the sim reports for its
    /// emitter (SimData.emittedMassEntries). That report changes at every Game.StepTheSim: the
    /// sim runs a step on its own thread, the next call hands back what it emitted, and the call
    /// after that the next step's or nothing. A frame too short for a sim sub-tick calls
    /// StepTheSim too, without time (UnsafeSim200ms(0)). U59 reads the report in the vent's
    /// Sim200ms, in a sub-tick bucket of its own: a short frame's call between the report's
    /// arrival and that bucket leaves the vent reading 0, and it takes nothing off. Above 60
    /// frames a second at speed 1 that is most of the time: the vent goes on emitting, its "kg
    /// remaining" stalls, and its pipes clog. U60 reads the report right after each step
    /// (Game.UnsafeSim200ms calls GeothermalVent.ProcessEmittedMass after StepTheSim), and its
    /// Sim200ms no longer reads it.
    ///
    /// Here, as U60 does it:
    /// - after each step of the sim, a postfix on Game.StepTheSim runs U60's ProcessEmittedMass
    ///   (StepTheSim's other call, in Game.OnPrefabInit, comes before any vent spawns);
    /// - the vent's unsafeSim200ms runs U60's body: U59's without the read;
    /// - OnMassEmitted runs U60's body: the mass is taken from every entry of the element in turn,
    ///   at most an entry's mass from each, their disease counts scaled down with it, and the
    ///   emitter is recomputed when an entry runs out or falls under 3 kg from 3 kg or more;
    /// - RecomputeEmissions runs U60's body: the emitter's disease count is for the 3 kg (or
    ///   less) a pulse emits, not the entry's whole mass.
    /// U60's other vent changes (the disease hashes saved since save version 7.39, and code
    /// that compiles the same) are not part of this and are left out.
    [GameMember(GameUse.Postfix, "Sim/GameDataUpdate* Game::StepTheSim(System.Single)")]
    [GameMember(GameUse.Uses, "SimData Game::simData")]
    [GameMember(GameUse.Uses, "Sim/EmittedMassInfo* SimData::emittedMassEntries")]
    [GameMember(GameUse.Uses, "Components/CmpsByWorld`1<GeothermalVent> Components::GeothermalVents")]
    [GameMember(GameUse.Uses, "GeothermalVent/EmitterInfo GeothermalVent::emitterInfo")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<GeothermalVent/ElementInfo> GeothermalVent::availableMaterial")]
    [GameMember(GameUse.Uses, "MeterController GeothermalVent::massMeter")]
    [GameMember(GameUse.Uses, "System.Single GeothermalVent::recentMass")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void GeothermalVent::unsafeSim200ms(System.Single)", "e067a8f5cb7c864e linux-744825 win-744825")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void GeothermalVent::OnMassEmitted(System.UInt16,System.Single)", "ae421805edb10c10 linux-744825 win-744825")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void GeothermalVent::RecomputeEmissions()", "d8e2583a7bd2243e linux-744825 win-744825")]
    public static class GeothermalVentMass
    {
        public const string Name = "fix-geothermal-vents";

        static Action<GeothermalVent, ushort, float> onMassEmitted;
        static Action<GeothermalVent> recomputeEmissions;

        public static void Install(Harmony harmony)
        {
            var emitted = AccessTools.Method(typeof(GeothermalVent), "OnMassEmitted", [typeof(ushort), typeof(float)]);
            var recompute = AccessTools.Method(typeof(GeothermalVent), "RecomputeEmissions");
            // Called through the methods themselves, so that other mods' patches of them run as
            // they would for the game's own calls.
            onMassEmitted = AccessTools.MethodDelegate<Action<GeothermalVent, ushort, float>>(emitted);
            recomputeEmissions = AccessTools.MethodDelegate<Action<GeothermalVent>>(recompute);
            harmony.Patch(AccessTools.Method(typeof(GeothermalVent), "unsafeSim200ms"),
                prefix: new HarmonyMethod(typeof(GeothermalVentMass), nameof(UnsafeSim200msPrefix)) { priority = Priority.Last });
            harmony.Patch(emitted, prefix: new HarmonyMethod(typeof(GeothermalVentMass), nameof(OnMassEmittedPrefix)) { priority = Priority.Last });
            harmony.Patch(recompute, prefix: new HarmonyMethod(typeof(GeothermalVentMass), nameof(RecomputeEmissionsPrefix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(Game), "StepTheSim"), postfix: new HarmonyMethod(typeof(GeothermalVentMass), nameof(StepTheSimPostfix)));
        }

        /// U60's GeothermalVent.ProcessEmittedMass, after each step of the sim.
        static unsafe void StepTheSimPostfix(Game __instance, Sim.GameDataUpdate* __result)
        {
            if (__result == null) return;
            foreach (int world in Components.GeothermalVents.GetWorldsIds())
            {
                foreach (GeothermalVent vent in Components.GeothermalVents.GetItems(world))
                {
                    int handle = vent.emitterInfo.simHandle;
                    if (!Sim.IsValidHandle(handle)) continue;
                    Sim.EmittedMassInfo info = __instance.simData.emittedMassEntries[Sim.GetHandleIndex(handle)];
                    if (info.mass > 0f) onMassEmitted(vent, info.elemIdx, info.mass);
                }
            }
        }

        /// U60's GeothermalVent.unsafeSim200ms: U59's without its read of the sim's report.
        static bool UnsafeSim200msPrefix(GeothermalVent __instance)
        {
            ref EmitterInfo info = ref __instance.emitterInfo;
            if (Sim.IsValidHandle(info.simHandle) && info.dirty)
            {
                SimMessages.ModifyElementEmitter(info.simHandle, info.cell, 1, ElementLoader.elements[info.element.elementIdx].id, 0.2f, Math.Min(3f, info.element.mass),
                    info.element.temperature, 120f, info.element.diseaseIdx, info.element.diseaseCount);
                info.dirty = false;
            }
            __instance.massMeter.SetPositionPercent(__instance.MaterialAvailable() / __instance.recentMass);
            return false;
        }

        /// U60's GeothermalVent.OnMassEmitted.
        static bool OnMassEmittedPrefix(GeothermalVent __instance, ushort element, float mass)
        {
            var available = __instance.availableMaterial;
            bool recompute = false;
            for (int i = 0; i < available.Count; i++)
            {
                if (!(mass > 0f)) break;
                ElementInfo value = available[i];
                if (value.elementIdx == element && value.mass > 0f)
                {
                    float taken = Math.Min(mass, value.mass);
                    float before = value.mass;
                    value.diseaseCount -= (int)((float)value.diseaseCount * taken / value.mass);
                    value.mass -= taken;
                    mass -= taken;
                    recompute |= value.mass <= 0f || (before >= 3f && value.mass < 3f);
                    available[i] = value;
                }
            }
            if (recompute) recomputeEmissions(__instance);
            return false;
        }

        /// U60's GeothermalVent.RecomputeEmissions.
        static bool RecomputeEmissionsPrefix(GeothermalVent __instance)
        {
            var available = __instance.availableMaterial;
            ref EmitterInfo info = ref __instance.emitterInfo;
            available.Sort();
            while (available.Count > 0 && available[available.Count - 1].mass <= 0f)
                available.RemoveAt(available.Count - 1);
            int fluids = 0;
            foreach (ElementInfo item in available)
            {
                if (!item.isSolid) fluids++;
            }
            if (fluids > 0)
            {
                int pick = UnityEngine.Random.Range(0, available.Count);
                while (available[pick].isSolid)
                    pick = (pick + 1) % available.Count;
                info.element = available[pick];
                info.element.diseaseCount = (int)((float)available[pick].diseaseCount * Math.Min(3f, info.element.mass) / available[pick].mass);
            }
            else
            {
                info.element.elementIdx = 0;
                info.element.mass = 0f;
            }
            info.dirty = true;
            return false;
        }
    }
}
