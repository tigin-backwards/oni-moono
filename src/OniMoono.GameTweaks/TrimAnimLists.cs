using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `trim-anim-lists` (exact, on by default): the anim batch groups' frame, frame
    /// element and symbol frame lists give back their unused slots once the game has loaded its
    /// anims.
    ///
    /// KGlobalAnimParser appends every anim's frames and frame elements to its batch group's lists
    /// (KBatchGroupData.animFrames, frameElements, symbolFrameInstances) one at a time while
    /// KAnimGroupFile.LoadAll parses them, so each list's array has grown by doubling and holds up
    /// to twice the slots it uses: with every DLC about 10.05 M slots of 36 bytes for 8.31 M frame
    /// elements, 63 MB unused, and 4 MB more in the other two (docs/research/heap-reduction-designs.md
    /// §2). After Assets.OnPrefabInit (its LoadAnims, then CreatePrefabs, where mods such as
    /// Extended Building Width rewrite groups' elements) each list's Capacity is set to its Count.
    /// The elements, their order and every index stay as they were: Capacity is the only thing
    /// that changes, and nothing in the game or the mods scanned reads it. A list that grows later
    /// (a mod adding elements in play) grows as it would have. With ONI_TWEAK_VERIFY=1 each list's
    /// bytes are compared before and after.
    [GameMember(GameUse.Postfix, "System.Void Assets::OnPrefabInit()")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<HashedString,KBatchGroupData> KAnimBatchManager::batchGroupData")]
    public static class TrimAnimLists
    {
        public const string Name = "trim-anim-lists";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        public static void Install(Harmony harmony)
        {
            // Last, after mods' own postfixes on it (which may still add elements).
            harmony.Patch(AccessTools.Method(typeof(Assets), "OnPrefabInit"),
                postfix: new HarmonyMethod(typeof(TrimAnimLists), nameof(Loaded)) { priority = Priority.Last });
        }

        static void Loaded()
        {
            var manager = KAnimBatchManager.Instance();
            if (manager == null) return;
            long freed = 0, mismatched = 0;
            int lists = 0;
            foreach (var data in manager.batchGroupData.Values)
            {
                if (data == null) continue;
                freed += Trim(data.frameElements, ref lists, ref mismatched);
                freed += Trim(data.animFrames, ref lists, ref mismatched);
                freed += Trim(data.symbolFrameInstances, ref lists, ref mismatched);
            }
            string check = verify ? $", {mismatched} differed" : "";
            global::Debug.Log($"{Name}: {lists} lists trimmed, {freed / 1e6:F1} MB freed{check}");
        }

        /// Sets the list's Capacity to its Count; returns the bytes of the slots that dropped.
        static long Trim<T>(List<T> list, ref int lists, ref long mismatched) where T : struct
        {
            if (list == null || list.Capacity == list.Count) return 0;
            long slack = (long)(list.Capacity - list.Count) * Marshal.SizeOf<T>();
            T[] before = verify ? list.ToArray() : null;
            list.Capacity = list.Count;
            lists++;
            if (before != null && !MemoryMarshal.AsBytes(before.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(list.ToArray().AsSpan())))
                mismatched++;
            return slack;
        }
    }
}
