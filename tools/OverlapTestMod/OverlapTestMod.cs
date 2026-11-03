using System;
using System.Collections.Generic;
using HarmonyLib;
using KMod;

namespace OverlapTestMod
{
    /// One patch of each kind the Moono Tweaks screen grades, on methods the default tweaks
    /// change; none changes what the game does:
    /// - a transpiler on JobManager.Run (fast-jobs replaces it): high;
    /// - a prefix returning bool on KBatchedAnimUpdater.CleanUp (fast-anim-cleanup replaces it): high;
    /// - a postfix on Sublimates.OnSpawn (fast-sublimation patches it): low;
    /// - a postfix on StateMachineController.GetSMI(Type): fast-smi-lookups steps aside for it.
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            var self = typeof(Mod);
            harmony.Patch(AccessTools.Method(typeof(JobManager), nameof(JobManager.Run)), transpiler: new HarmonyMethod(self, nameof(Same)));
            harmony.Patch(AccessTools.Method(typeof(KBatchedAnimUpdater), "CleanUp"), prefix: new HarmonyMethod(self, nameof(Go)));
            harmony.Patch(AccessTools.Method(typeof(Sublimates), "OnSpawn"), postfix: new HarmonyMethod(self, nameof(After)));
            harmony.Patch(AccessTools.Method(typeof(StateMachineController), nameof(StateMachineController.GetSMI), new[] { typeof(Type) }),
                postfix: new HarmonyMethod(self, nameof(After)));
            Debug.Log("[OverlapTestMod] patched");
        }

        static IEnumerable<CodeInstruction> Same(IEnumerable<CodeInstruction> instructions) => instructions;

        static bool Go() => true;

        static void After() { }
    }
}
