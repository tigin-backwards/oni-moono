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
    ///
    /// With ONI_OVERLAP_TEST_ANIM, for fast-anim-events (tests/e2e/fast-anim-events-*.yaml):
    /// - `prefix`: a prefix returning true on AnimEventManager's three-argument Update, as ONI
    ///   Multiplayer's asteroid freeze patches it: the tweak stands down, and the prefix runs
    ///   (LoopCalls), which it would not after the tweak's false return;
    /// - `swap`: during the event loop (after an event's Play), the controller the previous pass
    ///   visited last, while it is still the last entry, and every fifth frame a random entry's
    ///   controller get another animation of the same file by reflection, and their events
    ///   restart (StopAnimEventSequence, StartAnimEventSequence), as Colony Cuisine does; the
    ///   tweak's verify mode must find its copy of them current. This one changes what the game
    ///   shows.
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            var anim = Environment.GetEnvironmentVariable("ONI_OVERLAP_TEST_ANIM");
            if (anim == "prefix")
                harmony.Patch(AccessTools.Method(typeof(AnimEventManager), "Update", new[] { typeof(float), typeof(List<AnimEventManager.AnimData>), typeof(List<AnimEventManager.EventPlayerData>) }),
                    prefix: new HarmonyMethod(typeof(Mod), nameof(Loop)));
            else if (anim == "swap")
            {
                harmony.Patch(AccessTools.Method(typeof(AnimEventManager), "Update", new[] { typeof(float), typeof(List<AnimEventManager.AnimData>), typeof(List<AnimEventManager.EventPlayerData>) }),
                    postfix: new HarmonyMethod(typeof(Swap), nameof(Swap.After)));
                harmony.Patch(AccessTools.Method(typeof(AnimEvent), nameof(AnimEvent.Play)), postfix: new HarmonyMethod(typeof(Swap), nameof(Swap.During)));
            }
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

        public static long LoopCalls;

        static bool Loop()
        {
            LoopCalls++;
            return true;
        }
    }

    /// ONI_OVERLAP_TEST_ANIM=swap: another animation set by reflection, then the events restarted.
    public static class Swap
    {
        public static long Swaps, InPlace;

        static readonly AccessTools.FieldRef<AnimEventManager, KCompactedVector<AnimEventManager.EventPlayerData>> eventData =
            AccessTools.FieldRefAccess<AnimEventManager, KCompactedVector<AnimEventManager.EventPlayerData>>("eventData");
        static readonly AccessTools.FieldRef<KAnimControllerBase, KAnim.Anim> curAnim = AccessTools.FieldRefAccess<KAnimControllerBase, KAnim.Anim>("curAnim");
        static readonly System.Reflection.MethodInfo stopEvents = AccessTools.Method(typeof(KAnimControllerBase), "StopAnimEventSequence");
        static readonly System.Reflection.MethodInfo startEvents = AccessTools.Method(typeof(KAnimControllerBase), "StartAnimEventSequence");
        static readonly Random random = new Random(1);
        static int lastFrame = -1, lastCount;
        static KBatchedAnimController lastController;

        /// After the world's event loop (a postfix there leaves fast-anim-events on): which
        /// controller the loop visited last.
        internal static void After(AnimEventManager __instance, List<AnimEventManager.EventPlayerData> event_data)
        {
            if (!ReferenceEquals(eventData(__instance)?.GetDataList(), event_data)) return;
            lastCount = event_data.Count;
            lastController = lastCount > 0 ? event_data[lastCount - 1].controller : null;
        }

        /// After an animation event the loop played (AnimEvent.Play), at most once a frame: while
        /// the last entry is still the one the previous pass visited last, its controller is
        /// restarted in place (it lands in the same slot), and the loop visits it later in this
        /// pass. Every fifth frame also a random entry's.
        internal static void During(AnimEventManager.EventPlayerData behaviour)
        {
            int now = UnityEngine.Time.frameCount;
            if (now == lastFrame || AnimEventManager.Instance == null) return;
            var entries = eventData(AnimEventManager.Instance)?.GetDataList();
            if (entries == null || entries.Count < 2) return;
            var last = entries[entries.Count - 1].controller;
            if (entries.Count == lastCount && last != null && last == lastController && last != behaviour.controller)
            {
                lastFrame = now;
                if (Restart(last)) InPlace++;
            }
            if (now % 5 == 0 && now != lastFrame)
            {
                lastFrame = now;
                // Not the controller being visited: the loop writes its entry back to its slot,
                // which a restart has given to another entry (the game's loop would too).
                var other = entries[random.Next(entries.Count)].controller;
                if (other != behaviour.controller) Restart(other);
            }
        }

        static bool Restart(KBatchedAnimController controller)
        {
            if (controller == null || controller.CurrentAnim is not { } anim || anim.animFile == null) return false;
            var file = anim.animFile;
            KAnim.Anim other = null;
            for (int i = 0, start = random.Next(Math.Max(1, file.animCount)); i < file.animCount && other == null; i++)
            {
                var a = file.GetAnim((start + i) % file.animCount);
                if (a != null && a != anim && a.numFrames > 0 && a.frameRate > 0f && (a.numFrames != anim.numFrames || a.totalTime != anim.totalTime)) other = a;
            }
            if (other == null) return false;
            stopEvents.Invoke(controller, null);
            curAnim(controller) = other;
            startEvents.Invoke(controller, null);
            Swaps++;
            return true;
        }
    }
}
