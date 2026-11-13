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
    /// removed since the last run are recorded with those nodes (PartitionerFreed, shared with
    /// fast-partitioner-events), and each node's scan looks up only
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
    [GameMember(GameUse.Postfix | GameUse.Transpiler, "System.Void ScenePartitioner::Sim1000ms(System.Single)", "05fc3c89d3d74062 linux-744825 win-744825", "fd6073d6209c4325 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023", Rewrites = 1)]
    [GameMember(GameUse.Uses, "System.Int32 HybridListHashSet`1::get_Count()")]
    public static class FastPartitionerCleanup
    {
        public const string Name = "fast-partitioner-cleanup";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static long nodes, dropped, wrong;

        public static void Install(Harmony harmony)
        {
            // The handles removed since the last scan, by node: PartitionerFreed (shared with fast-partitioner-events).
            PartitionerFreed.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(ScenePartitioner), nameof(ScenePartitioner.Sim1000ms)),
                transpiler: new HarmonyMethod(typeof(FastPartitionerCleanup), nameof(Transpiler)));
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
            dropped += PartitionerFreed.DropFreed(entries, layer, y, x);
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
    }
}
