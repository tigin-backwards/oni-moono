using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-anim-cleanup` (opt-in). Every frame KBatchedAnimUpdater.CleanUp sweeps 16
    /// chunks of its grid of animation controllers (the grid is swept round and round), testing
    /// every controller in them for having been destroyed, to drop those entries. Controllers
    /// normally leave the grid when disabled, so the sweeps nearly always find nothing; but each
    /// test reads the controller (U57: about 0.5 ms a frame over 90,000 controllers).
    ///
    /// A destroyed controller is null from the moment it is destroyed, and only destroyed ones
    /// are dropped. So the ids of controllers destroyed recently are kept, and a chunk is swept
    /// only when its dictionary still holds one of them: the same entries are dropped at the same
    /// sweep. An id is forgotten once the sweep has gone round the whole grid since its
    /// destruction (any entry it had was swept by then). With ONI_TWEAK_VERIFY=1 the chunks not
    /// swept are swept anyway and any entry dropped there is logged.
    [GameMember(GameUse.Prefix, "System.Void KBatchedAnimController::OnDestroy()")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void KBatchedAnimUpdater::CleanUp()", "c7ab6262d3b6aaf6 linux-744825 win-744825", "791aedce58afce33 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    public static class FastAnimCleanup
    {
        public const string Name = "fast-anim-cleanup";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly AccessTools.FieldRef<KBatchedAnimUpdater, Dictionary<int, KBatchedAnimController>[,]> grid =
            AccessTools.FieldRefAccess<KBatchedAnimUpdater, Dictionary<int, KBatchedAnimController>[,]>("controllerGrid");
        static readonly AccessTools.FieldRef<KBatchedAnimUpdater, int> chunkIndex = AccessTools.FieldRefAccess<KBatchedAnimUpdater, int>("cleanUpChunkIndex");

        /// Controllers destroyed recently, with the chunk sweeps left before they are forgotten.
        static readonly List<(int Id, int SweepsLeft)> destroyed = new List<(int, int)>();
        static readonly List<int> dropped = new List<int>();
        static long sweeps, skipped, missed;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(KBatchedAnimController), "OnDestroy"), prefix: new HarmonyMethod(typeof(FastAnimCleanup), nameof(Destroyed)));
            harmony.Patch(AccessTools.Method(typeof(KBatchedAnimUpdater), "CleanUp"), prefix: new HarmonyMethod(typeof(FastAnimCleanup), nameof(CleanUp)));
        }

        static void Destroyed(KBatchedAnimController __instance) => destroyed.Add((__instance.GetInstanceID(), int.MaxValue));

        /// KBatchedAnimUpdater.CleanUp: the same 16 chunks in the same order, swept only where a
        /// recently destroyed controller's entry may remain.
        static bool CleanUp(KBatchedAnimUpdater __instance)
        {
            var controllers = grid(__instance);
            if (controllers == null)
            {
                destroyed.Clear(); // no world: nothing to sweep, ever
                return false;
            }
            if (Camera.main == null) return false;
            // Bounded: past this many pending ids every chunk is swept, as the game does.
            bool sweepAll = destroyed.Count > 50000;
            int width = controllers.GetLength(0), count = controllers.Length;
            ref int index = ref chunkIndex(__instance);
            for (int k = 0; k < destroyed.Count; k++)
                if (destroyed[k].SweepsLeft == int.MaxValue) destroyed[k] = (destroyed[k].Id, count);
            for (int i = 0; i < 16; i++)
            {
                int chunk = (index + i) % count;
                var map = controllers[chunk % width, chunk / width];
                bool holds = sweepAll;
                if (!holds)
                    foreach (var (id, _) in destroyed)
                        if (map.ContainsKey(id)) { holds = true; break; }
                if (holds) { Sweep(map); sweeps++; }
                else
                {
                    skipped++;
                    if (verify && Sweep(map) > 0) Log($"{Name}: a skipped chunk held destroyed controllers ({++missed})");
                }
            }
            index = (index + 16) % count;
            // Each id has now had 16 more chunks swept.
            for (int k = destroyed.Count - 1; k >= 0; k--)
            {
                int left = destroyed[k].SweepsLeft - 16;
                if (left <= 0) destroyed.RemoveAt(k);
                else destroyed[k] = (destroyed[k].Id, left);
            }
            if (verify && (sweeps + skipped) % 10000 == 0) Log($"{Name}: {sweeps} chunks swept, {skipped} skipped, {missed} wrong skips");
            return false;
        }

        /// The game's sweep of one chunk: entries whose controller was destroyed are removed.
        static int Sweep(Dictionary<int, KBatchedAnimController> map)
        {
            dropped.Clear();
            foreach (var item in map)
                if (item.Value == null) dropped.Add(item.Key);
            foreach (int id in dropped) map.Remove(id);
            return dropped.Count;
        }

        static void Log(string text) => global::Debug.Log(text);
    }
}
