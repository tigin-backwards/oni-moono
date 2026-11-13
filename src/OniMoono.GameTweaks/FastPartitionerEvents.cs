using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using Handle = HandleVector<int>.Handle;

    /// Game tweak `fast-partitioner-events`. Every Grid.Objects write (an item changing cell, an
    /// item stored or taken off a rail) runs a scene-partitioner event on that object layer:
    /// ScenePartitioner.TriggerEventInternal walks every entry of the cell's 16×16 node(s), looks
    /// each up and calls the callbacks of those that overlap the cell. On the Pickupables layer no
    /// entry has a callback (every item is an entry: 25,837 on a late-game colony, up to 2,691 in a
    /// node), so each write walks the whole node and calls nothing; on that colony it is 2.3% of
    /// the main thread (docs/research/conduit-updates.md).
    ///
    /// The walk's only effects there are the ones it has on the node's list: a freed handle it
    /// meets is removed (by swapping the list's last entry into its place). So on a layer where no
    /// entry has had a callback since the tweak was installed (each entry's callback is set by
    /// ScenePartitionerEntry.Init before ScenePartitioner.Insert puts it in a node, which the tweak
    /// watches), the walk is skipped: a node that is not dirty holds no freed handle, and a dirty
    /// node's freed handles are dropped from the places the walk would meet them, highest first
    /// (PartitionerFreed, the record shared with fast-partitioner-cleanup), which leaves the list
    /// as the walk does. The event's own counter and the layer's global listeners (TriggerEvent,
    /// around this method) run as in the game.
    ///
    /// With ONI_TWEAK_VERIFY=1 each skipped node's walk is replayed on a copy of its list first:
    /// an entry whose callback the game would have called, or a list that comes out different, is
    /// counted and logged. Off for the session while another mod patches the partitioner's event,
    /// insert, remove or scan, or while Fast Track, MoreTek, Colony Performance Fixes or Multiplayer
    /// Mod is active (checked at the first event and every 4,096).
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void ScenePartitioner::TriggerEventInternal(System.Int32,System.Int32,System.Int32,System.Int32,ScenePartitionerLayer,System.Object)", "aa21b8008dd4fefe linux-744825 win-744825", "4f3c5bee3b515f81 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Postfix, "System.Void ScenePartitioner::Insert(HandleVector`1/Handle<System.Int32>)")]
    [GameMember(GameUse.Prefix, "System.Void ScenePartitioner::Remove(HandleVector`1/Handle<System.Int32>)")]
    [GameMember(GameUse.Postfix, "System.Void ScenePartitioner::Sim1000ms(System.Single)")]
    [GameMember(GameUse.Uses, "System.Void ScenePartitioner::TriggerEvent(System.Int32,System.Int32,System.Int32,System.Int32,ScenePartitionerLayer,System.Object)")]
    [GameMember(GameUse.Uses, "System.Void ScenePartitioner::TriggerEvent(System.Collections.Generic.IEnumerable`1<System.Int32>,ScenePartitionerLayer,System.Object)")]
    [GameMember(GameUse.Uses, "Extents ScenePartitioner::GetNodeExtents(ScenePartitionerEntry)")]
    public static class FastPartitionerEvents
    {
        public const string Name = "fast-partitioner-events";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Test infrastructure (PerfProbe's ab.txt): true runs the game's walk, for an A/B in one run.
        public static bool Bypass = Environment.GetEnvironmentVariable("ONI_PARTITIONER_EVENTS_BYPASS") == "1";

        /// The layers (ScenePartitionerLayer.layer) an entry with a callback has been put in.
        static readonly bool[] hadCallback = new bool[256];
        static bool off;
        static long calls;

        /// Skipped walks, freed handles dropped, and differences the verify mode found.
        public static long Skipped, Dropped, Wrong;

        public static void Install(Harmony harmony)
        {
            PartitionerFreed.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.Insert)), postfix: new HarmonyMethod(typeof(FastPartitionerEvents), nameof(Inserted)));
            harmony.Patch(AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.TriggerEventInternal)),
                prefix: new HarmonyMethod(typeof(FastPartitionerEvents), nameof(Walk)) { priority = Priority.Last });
        }

        /// The methods another owner's patch makes this tweak stand down (ModOverlaps).
        internal static IEnumerable<MethodBase> Guarded() => new MethodBase[]
        {
            AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.TriggerEventInternal)),
            AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.TriggerEvent), new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(ScenePartitionerLayer), typeof(object) }),
            AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.TriggerEvent), new[] { typeof(IEnumerable<int>), typeof(ScenePartitionerLayer), typeof(object) }),
            AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.Insert)),
            AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.Remove)),
            AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.Sim1000ms)),
        };

        /// The mods whose partitioner work this tweak stands down for, by static ID.
        static readonly string[] standDownMods = { "PeterHan.FastTrack", "MoreTek", "SaitoColonyPerf", "multiplayerMod" };

        static void Inserted(Handle handle)
        {
            if (GameScenePartitioner.Instance.Lookup(handle, out var entry) && entry.eventCallback != null) hadCallback[entry.layer & 0xFF] = true;
        }

        static bool Foreign()
        {
            if (ModStandDown.Active(standDownMods) is { } mod)
            {
                global::Debug.Log($"{Name}: off ({mod} is active)");
                return true;
            }
            foreach (var method in Guarded())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var o in info.Owners)
                    if (!ModOverlapsCore.IsTweakOwner(o) && !ModOverlapsCore.IsOurs(o))
                    {
                        global::Debug.Log($"{Name}: off ({o} patches ScenePartitioner.{method.Name})");
                        return true;
                    }
            }
            return false;
        }

        static readonly List<Handle> replay = new();
        static readonly Func<Handle, bool> freed = h => !GameScenePartitioner.Instance.Lookup(h, out _);

        /// ScenePartitioner.TriggerEventInternal, skipped where its walk can only drop freed handles.
        static bool Walk(ScenePartitioner __instance, int x, int y, int width, int height, ScenePartitionerLayer layer, bool __runOriginal)
        {
            if (!__runOriginal || off || Bypass) return __runOriginal;
            if ((calls++ & 0xFFF) == 0)
            {
                if (off = Foreign()) return true;
                if (verify && calls > 1) global::Debug.Log($"{Name}: {Skipped} walks skipped, {Dropped} freed handles dropped, {Wrong} wrong");
            }
            int l = layer.layer;
            if (hadCallback[l & 0xFF] || !ReferenceEquals(PartitionerFreed.Owner, __instance)) return true;
            var nodes = __instance.nodes;
            var (y0, y1, x0, x1) = PartitionerWalkCore.WalkedNodes(x, y, width, height, __instance.nodeSize, nodes.GetLength(1), nodes.GetLength(2));
            for (int i = y0; i < y1; i++)
                for (int j = x0; j < x1; j++)
                {
                    ref var node = ref nodes[l, i, j];
                    if (verify) Replay(node.entries, x, y, width, height, l, i, j, node.dirty);
                    else if (node.dirty) Dropped += PartitionerFreed.DropFreed(node.entries, l, i, j);
                }
            Skipped++;
            return false;
        }

        /// The game's walk of one node on a copy, then the tweak's; differences counted and logged.
        static void Replay(HybridListHashSet<Handle> entries, int x, int y, int width, int height, int l, int i, int j, bool dirty)
        {
            var list = entries.list;
            var lookup = GameScenePartitioner.Instance;
            for (int k = list.Count - 1; k >= 0; k--)
                if (lookup.Lookup(list[k], out var e) && e.eventCallback != null && e.obj != null
                    && x + width - 1 >= e.x && x <= e.x + e.width - 1 && y + height - 1 >= e.y && y <= e.y + e.height - 1)
                    global::Debug.Log($"{Name}: node {l},{i},{j} has an entry the game's walk would call ({++Wrong})");
            replay.Clear();
            replay.AddRange(list);
            PartitionerWalkCore.Walk(replay, freed);
            if (dirty) Dropped += PartitionerFreed.DropFreed(entries, l, i, j);
            bool same = replay.Count == list.Count;
            for (int k = 0; same && k < list.Count; k++) same = replay[k].Equals(list[k]);
            if (!same)
            {
                global::Debug.Log($"{Name}: node {l},{i},{j} differs from the game's walk ({++Wrong})");
                // Leave the node as the game's walk would have.
                entries.Clear();
                foreach (var h in replay) entries.Add(h);
            }
        }
    }
}
