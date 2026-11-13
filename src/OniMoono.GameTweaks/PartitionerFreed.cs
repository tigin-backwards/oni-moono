using System.Collections.Generic;
using HarmonyLib;

namespace OniMoono.GameTweaks
{
    using Handle = HandleVector<int>.Handle;

    /// The handles freed from the scene partitioner since its last Sim1000ms, by node, shared by
    /// fast-partitioner-cleanup and fast-partitioner-events (installed once by whichever is on;
    /// both declare the members it patches and reads).
    ///
    /// A handle is freed only through GameScenePartitioner.Free, which first removes it from the
    /// partitioner: ScenePartitioner.Remove, which marks the nodes it is in dirty and leaves the
    /// handle in their lists. Those lists drop a freed handle only when a walk (an event, a gather,
    /// a visit) or Sim1000ms's scan of the dirty nodes looks it up. Sim1000ms clears every dirty
    /// flag, so after it no node holds a freed handle; a node that is not dirty holds none, and a
    /// dirty node's freed handles are among those recorded here for it.
    internal static class PartitionerFreed
    {
        static bool installed;

        /// The partitioner the records are for (each colony load makes a new one): the records
        /// are complete for it alone, from its first removal on.
        public static ScenePartitioner Owner { get; private set; }

        /// The handles removed since the last Sim1000ms, by node (layer, y, x).
        public static readonly Dictionary<(int, int, int), List<Handle>> Removed = new();

        /// Lists `Removed` let go of, cleared, with their storage.
        static readonly Stack<List<Handle>> spareLists = new();

        public static void Install(Harmony harmony)
        {
            if (installed) return;
            installed = true;
            harmony.Patch(AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.Remove)), prefix: new HarmonyMethod(typeof(PartitionerFreed), nameof(Removing)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.Sim1000ms)), postfix: new HarmonyMethod(typeof(PartitionerFreed), nameof(Scanned)) { priority = Priority.Last });
        }

        static void Release()
        {
            foreach (var list in Removed.Values)
            {
                list.Clear();
                spareLists.Push(list);
            }
            Removed.Clear();
        }

        /// ScenePartitioner.Remove: the nodes it is about to mark dirty, with the handle.
        static void Removing(ScenePartitioner __instance, Handle handle)
        {
            if (!GameScenePartitioner.Instance.Lookup(handle, out var entry)) return;
            if (!ReferenceEquals(Owner, __instance)) { Release(); Owner = __instance; }
            var e = __instance.GetNodeExtents(entry);
            for (int y = e.y; y < e.y + e.height; y++)
                for (int x = e.x; x < e.x + e.width; x++)
                {
                    if (!Removed.TryGetValue((entry.layer, y, x), out var list)) Removed[(entry.layer, y, x)] = list = spareLists.Count > 0 ? spareLists.Pop() : new List<Handle>();
                    list.Add(handle);
                }
        }

        /// After Sim1000ms's scan every dirty node has been cleaned, so the records are done with.
        static void Scanned(ScenePartitioner __instance)
        {
            if (ReferenceEquals(Owner, __instance)) Release();
        }

        static readonly List<(int Place, Handle Handle)> dropping = new();
        static readonly System.Func<Handle, bool> freed = h => !GameScenePartitioner.Instance.Lookup(h, out _);

        /// Drops the node's freed handles as the game's descending walk would: how many.
        public static int DropFreed(HybridListHashSet<Handle> entries, int layer, int y, int x)
        {
            if (!Removed.TryGetValue((layer, y, x), out var handles)) return 0;
            PartitionerWalkCore.FreedPlaces(entries.dict, handles, freed, dropping);
            foreach (var (_, handle) in dropping) entries.Remove(handle);
            return dropping.Count;
        }
    }
}
