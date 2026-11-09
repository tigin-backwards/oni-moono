using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using Handle = HandleVector<int>.Handle;

    /// Game tweak `fast-partitioner-cleanup` (opt-in). Once a game second ScenePartitioner.Sim1000ms
    /// goes through every node marked dirty since its last run and looks up every entry of each, to
    /// drop those whose handle has been freed. Every insertion marks its nodes dirty too, so nearly
    /// every node an object moved through is looked through in full (Gluttony: about 0.36 ms a
    /// frame on Linux; with 10,000 items stuck in two nodes, about 1.6 ms on Windows).
    ///
    /// A handle is freed only through GameScenePartitioner.Free, which first removes it from the
    /// partitioner: ScenePartitioner.Remove, which marks the nodes it is in dirty. So the handles
    /// removed since the last run are recorded with those nodes, and each node's scan looks up only
    /// those still in it, dropping the ones whose lookup fails in descending order of their place
    /// in the node's list. That is the game's descending scan: each removal swaps the list's last
    /// entry into the freed place, and every entry after that place has been kept, so the list ends
    /// up the same. The dirty flags are cleared by the game's own code. With ONI_TWEAK_VERIFY=1 each
    /// node is also looked through in full afterwards, and any entry left failing its lookup is
    /// logged and dropped.
    [GameMember(GameUse.Uses, "System.Int32 ScenePartitioner/DirtyNode::layer")]
    [GameMember(GameUse.Uses, "System.Int32 ScenePartitioner/DirtyNode::x")]
    [GameMember(GameUse.Uses, "System.Int32 ScenePartitioner/DirtyNode::y")]
    [GameMember(GameUse.Uses, "HybridListHashSet`1<HandleVector`1/Handle<System.Int32>> ScenePartitioner/ScenePartitionerNode::entries")]
    [GameMember(GameUse.Uses, "Extents ScenePartitioner::GetNodeExtents(ScenePartitionerEntry)")]
    [GameMember(GameUse.Prefix, "System.Void ScenePartitioner::Remove(HandleVector`1/Handle<System.Int32>)")]
    [GameMember(GameUse.Postfix | GameUse.Transpiler, "System.Void ScenePartitioner::Sim1000ms(System.Single)", "05fc3c89d3d74062 linux-744825 win-744825", "fd6073d6209c4325 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230", Rewrites = 1)]
    public static class FastPartitionerCleanup
    {
        public const string Name = "fast-partitioner-cleanup";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static Func<ScenePartitioner, ScenePartitionerEntry, Extents> nodeExtents = null!;

        /// The handles removed since the last scan, by node (layer, y, x), for one partitioner.
        static readonly Dictionary<(int, int, int), List<Handle>> removed = new();

        /// Lists `removed` let go of, cleared, with their storage: a list belongs to `removed`
        /// from Take until the records are done with (Release), and nothing else holds one.
        static readonly Stack<List<Handle>> spareLists = new();

        static List<Handle> Take() => spareLists.Count > 0 ? spareLists.Pop() : new List<Handle>();

        static void Release()
        {
            foreach (var list in removed.Values)
            {
                list.Clear();
                spareLists.Push(list);
            }
            removed.Clear();
        }
        static ScenePartitioner owner;
        static readonly List<(int Place, Handle Handle)> dropping = new();
        static long nodes, dropped, wrong;

        public static void Install(Harmony harmony)
        {
            nodeExtents = AccessTools.MethodDelegate<Func<ScenePartitioner, ScenePartitionerEntry, Extents>>(
                AccessTools.Method(typeof(ScenePartitioner), "GetNodeExtents", [typeof(ScenePartitionerEntry)]));
            harmony.Patch(AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.Remove)), prefix: new HarmonyMethod(typeof(FastPartitionerCleanup), nameof(Removing)));
            harmony.Patch(AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.Sim1000ms)),
                transpiler: new HarmonyMethod(typeof(FastPartitionerCleanup), nameof(Transpiler)), postfix: new HarmonyMethod(typeof(FastPartitionerCleanup), nameof(Scanned)));
        }

        /// ScenePartitioner.Remove: the nodes it is about to mark dirty, with the handle.
        static void Removing(ScenePartitioner __instance, Handle handle)
        {
            if (!GameScenePartitioner.Instance.Lookup(handle, out var entry)) return;
            if (!ReferenceEquals(owner, __instance)) { Release(); owner = __instance; }
            var e = nodeExtents(__instance, entry);
            for (int y = e.y; y < e.y + e.height; y++)
                for (int x = e.x; x < e.x + e.width; x++)
                {
                    if (!removed.TryGetValue((entry.layer, y, x), out var list)) removed[(entry.layer, y, x)] = list = Take();
                    list.Add(handle);
                }
        }

        /// In each dirty node's scan, `entries.Count` (where the descending scan starts) becomes
        /// `Clean(entries, layer, y, x)`, which drops the freed entries itself and returns 0, so the
        /// scan's loop runs no times.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, System.Reflection.MethodBase original)
        {
            var list = new List<CodeInstruction>(instructions);
            var dirtyNode = AccessTools.Inner(typeof(ScenePartitioner), "DirtyNode");
            var node = AccessTools.Inner(typeof(ScenePartitioner), "ScenePartitionerNode");
            var entriesField = AccessTools.Field(node, "entries");
            var count = AccessTools.PropertyGetter(typeof(HybridListHashSet<Handle>), nameof(HybridListHashSet<Handle>.Count));
            int entries = list.FindIndex(i => i.LoadsField(entriesField));
            int at = list.FindIndex(entries + 1, i => i.Calls(count));
            int layer = list.FindIndex(i => i.LoadsField(AccessTools.Field(dirtyNode, "layer")));
            if (entries < 0 || at < 0 || layer < 1 || list.FindAll(i => i.Calls(count)).Count != 1)
                throw new InvalidOperationException($"{Name}: ScenePartitioner.Sim1000ms is not as expected");
            var loadNode = list[layer - 1];
            list[at].opcode = OpCodes.Call;
            list[at].operand = AccessTools.Method(typeof(FastPartitionerCleanup), nameof(Clean));
            list.InsertRange(at, new[]
            {
                loadNode.Clone(), new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(dirtyNode, "layer")),
                loadNode.Clone(), new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(dirtyNode, "y")),
                loadNode.Clone(), new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(dirtyNode, "x")),
            });
            OniMoono.Tweaks.TranspilerCounts.Report(Name, original, 1);
            return list;
        }

        static int Clean(HybridListHashSet<Handle> entries, int layer, int y, int x)
        {
            nodes++;
            var lookup = GameScenePartitioner.Instance;
            if (removed.TryGetValue((layer, y, x), out var handles))
            {
                var place = entries.dict;
                dropping.Clear();
                foreach (var handle in handles)
                    if (place.TryGetValue(handle, out int at) && !lookup.Lookup(handle, out _) && !dropping.Contains((at, handle))) dropping.Add((at, handle));
                dropping.Sort((a, b) => b.Place.CompareTo(a.Place));
                foreach (var (_, handle) in dropping) entries.Remove(handle);
                dropped += dropping.Count;
            }
            if (verify)
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                    if (!lookup.Lookup(entries[i], out _))
                    {
                        global::Debug.Log($"{Name}: a freed entry was left in node {layer},{y},{x} ({++wrong})");
                        entries.Remove(entries[i]);
                    }
                if (nodes % 20000 == 0) global::Debug.Log($"{Name}: {nodes} nodes cleaned, {dropped} entries dropped, {wrong} wrong");
            }
            return 0;
        }

        /// After the scan: every dirty node has been cleaned, so the records are done with.
        static void Scanned() => Release();
    }
}
