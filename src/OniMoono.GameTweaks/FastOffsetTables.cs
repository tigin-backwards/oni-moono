using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Fetchable = FetchManager.Fetchable;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-offset-tables` (opt-in). Each Duplicant's fetch update (once a frame in
    /// U57) first makes sure every fetchable item's work offsets are current: a job over every
    /// prefab's items (U57: about 27,000 in 211 groups) in which the workers check each item's
    /// offset tracker (its cell is the item's cell and its offsets are set) and the main thread
    /// recomputes the items that fail, in group order then list order. Almost all pass: the job
    /// is a cache miss or three per item, about 0.75 ms a frame of the main thread's time.
    ///
    /// An item's offsets go stale only through a few transitions, all in the game's code: the
    /// tracker's cell changes (OffsetTracker.UpdateCell, on any GetOffsets for another cell), a
    /// solid or navigable cell in its extents changes (OffsetTableTracker.OnCellChanged clears the
    /// offsets), the item gets a new tracker (Workable's four assignments of offsetTracker), or it
    /// becomes fetchable (FetchManager.Add). The tweak marks the item at each, and replaces the job
    /// with a pass over the marked items that are fetchable: the same check, and the same
    /// recompute for those that fail, in the same group-then-list order (a schedule the game's job
    /// can take, its main thread taking only the first group). An item that passes, or is not
    /// fetchable, is unmarked; one that still fails stays marked, as the game would retry it
    /// every update.
    ///
    /// With ONI_TWEAK_VERIFY=1 every update also checks every fetchable item as the game does,
    /// and logs any that fails without being marked (and fixes it).
    [GameMember(GameUse.Postfix, "HandleVector`1/Handle<System.Int32> FetchManager::Add(Pickupable)")]
    [GameMember(GameUse.Prefix, "System.Void FetchManager::Remove(Tag,HandleVector`1/Handle<System.Int32>)")]
    [GameMember(GameUse.Transpiler, "System.Void FetchManager::UpdatePickups(Navigator,WorkerBase)", "c99d507490e92722 linux-744825 win-744825", "5c631d5233025d1f linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230", Rewrites = 1)]
    [GameMember(GameUse.Uses, "WorkItemCollection`2<FetchManager/UpdateOffsetTables,System.Object> FetchManager::updateOffsetTables")]
    [GameMember(GameUse.Postfix, "System.Void OffsetTableTracker::OnCellChanged(System.Object)")]
    [GameMember(GameUse.Postfix, "System.Void OffsetTableTracker::UpdateCell(System.Int32,System.Int32)")]
    [GameMember(GameUse.Postfix, "System.Void OffsetTracker::UpdateCell(System.Int32,System.Int32)")]
    [GameMember(GameUse.Transpiler, "System.Collections.Generic.IReadOnlyList`1<CellOffset> Workable::GetOffsets(System.Int32)", "a15b3a9aa9279c46 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230", Rewrites = 1)]
    [GameMember(GameUse.Transpiler, "System.Void Workable::SetOffsetTable(CellOffset[][])", "0aeb0b6fbf1d2786 linux-744825 win-744825", "ceeaa2b15d0cf62e linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230", Rewrites = 1)]
    [GameMember(GameUse.Transpiler, "System.Void Workable::SetOffsets(CellOffset[])", "7300b07737b428bb linux-744825 win-744825", "8b1fc1698d1ea5b7 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230", Rewrites = 1)]
    [GameMember(GameUse.Transpiler, "System.Boolean Workable::ValidateOffsets(System.Int32)", "22b59c7ba40b813d linux-744825 win-744825", "24cfa72c440b0d97 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230", Rewrites = 1)]
    [GameMember(GameUse.Uses, "OffsetTracker Workable::offsetTracker")]
    public static class FastOffsetTables
    {
        public const string Name = "fast-offset-tables";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Items whose offsets may be stale. Locked: a tracker could change cell on a worker.
        static readonly HashSet<Pickupable> marked = new HashSet<Pickupable>();
        /// Owners of the trackers that do not name theirs (StandardOffsetTracker).
        static readonly ConditionalWeakTable<OffsetTracker, Pickupable> owners = new ConditionalWeakTable<OffsetTracker, Pickupable>();
        /// Each fetchable item's group and handle, kept by FetchManager.Add and Remove.
        static readonly Dictionary<Pickupable, (FetchManager.FetchablesByPrefabId Group, HandleVector<int>.Handle Handle)> registered =
            new Dictionary<Pickupable, (FetchManager.FetchablesByPrefabId, HandleVector<int>.Handle)>();
        static readonly Dictionary<FetchManager.FetchablesByPrefabId, int> groupOrder = new Dictionary<FetchManager.FetchablesByPrefabId, int>();
        static readonly List<(int Group, int Index, Pickupable Item)> pass = new List<(int, int, Pickupable)>();
        static readonly List<Pickupable> snapshot = new List<Pickupable>();
        /// The manager whose fetchables `registered` mirrors; another (a new game) is read afresh.
        static FetchManager boundTo;
        /// An item registered twice: the mirror cannot say which entry a removal ends, so every
        /// update checks every item, as the game does.
        static bool duplicates;
        static long updates, fixedItems, missed;

        public static void Install(Harmony harmony)
        {
            var assigned = new HarmonyMethod(typeof(FastOffsetTables), nameof(TrackerAssignments));
            foreach (var method in new[] { "SetOffsets", "SetOffsetTable", "GetOffsets", "ValidateOffsets" })
                harmony.Patch(AccessTools.Method(typeof(Workable), method, method.StartsWith("Get") || method.StartsWith("Validate") ? new[] { typeof(int) } : null),
                    transpiler: assigned);
            harmony.Patch(AccessTools.Method(typeof(OffsetTracker), "UpdateCell"), postfix: new HarmonyMethod(typeof(FastOffsetTables), nameof(CellUpdated)));
            harmony.Patch(AccessTools.Method(typeof(OffsetTableTracker), "UpdateCell"), postfix: new HarmonyMethod(typeof(FastOffsetTables), nameof(TableChanged)));
            harmony.Patch(AccessTools.Method(typeof(OffsetTableTracker), "OnCellChanged"), postfix: new HarmonyMethod(typeof(FastOffsetTables), nameof(TableChanged)));
            harmony.Patch(AccessTools.Method(typeof(FetchManager), nameof(FetchManager.Add)), postfix: new HarmonyMethod(typeof(FastOffsetTables), nameof(Added)));
            harmony.Patch(AccessTools.Method(typeof(FetchManager), nameof(FetchManager.Remove)), prefix: new HarmonyMethod(typeof(FastOffsetTables), nameof(Removing)));
            harmony.Patch(AccessTools.Method(typeof(FetchManager), nameof(FetchManager.UpdatePickups)), transpiler: new HarmonyMethod(typeof(FastOffsetTables), nameof(UpdatePickupsTranspiler)));
        }

        /// After each `this.offsetTracker = …` in Workable: TrackerAssigned(this).
        static IEnumerable<CodeInstruction> TrackerAssignments(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var field = AccessTools.Field(typeof(Workable), "offsetTracker");
            var hook = AccessTools.Method(typeof(FastOffsetTables), nameof(TrackerAssigned));
            int found = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode != OpCodes.Stfld || !Equals(instruction.operand, field)) continue;
                found++;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, hook);
            }
            // One offsetTracker assignment (TweakInstall checks the count).
            OniMoono.Tweaks.TranspilerCounts.Report(Name, original, found);
        }

        /// In FetchManager.UpdatePickups, GlobalJobManager.Run(updateOffsetTables) becomes
        /// FixOffsets(updateOffsetTables, this). The work items are still made and finished (each
        /// recycles its pooled list), with nothing collected.
        static IEnumerable<CodeInstruction> UpdatePickupsTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var run = AccessTools.Method(typeof(GlobalJobManager), nameof(GlobalJobManager.Run));
            var list = new List<CodeInstruction>(instructions);
            int index = list.FindIndex(i => i.opcode == OpCodes.Call && Equals(i.operand, run));
            if (index < 1 || !(list[index - 1].operand is FieldInfo f) || f.Name != "updateOffsetTables")
            {
                OniMoono.Tweaks.TranspilerCounts.Report(Name, original, 0);
                return list;
            }
            list[index].operand = AccessTools.Method(typeof(FastOffsetTables), nameof(FixOffsets));
            list.Insert(index, new CodeInstruction(OpCodes.Ldarg_0));
            OniMoono.Tweaks.TranspilerCounts.Report(Name, original, 1);
            return list;
        }

        static void Mark(Pickupable item)
        {
            lock (marked) marked.Add(item);
        }

        static void TrackerAssigned(Workable workable)
        {
            if (!(workable is Pickupable item)) return;
            var tracker = workable.offsetTracker;
            if (tracker != null && !(tracker is OffsetTableTracker))
            {
                lock (owners)
                {
                    owners.Remove(tracker);
                    owners.Add(tracker, item);
                }
            }
            Mark(item);
        }

        /// OffsetTracker.UpdateCell: a tracker without a named owner changed cell.
        static void CellUpdated(OffsetTracker __instance)
        {
            if (__instance is OffsetTableTracker) return;
            Pickupable item;
            lock (owners)
                if (!owners.TryGetValue(__instance, out item)) return;
            Mark(item);
        }

        /// OffsetTableTracker.UpdateCell and OnCellChanged.
        static void TableChanged(OffsetTableTracker __instance)
        {
            if (__instance.cmp is Pickupable item) Mark(item);
        }

        static void Added(FetchManager __instance, Pickupable pickupable, HandleVector<int>.Handle __result)
        {
            if (!ReferenceEquals(__instance, boundTo)) return;
            var group = __instance.prefabIdToFetchables[pickupable.KPrefabID.PrefabID()];
            if (registered.ContainsKey(pickupable)) duplicates = true;
            registered[pickupable] = (group, __result);
            Mark(pickupable);
        }

        static void Removing(FetchManager __instance, Tag prefab_tag, HandleVector<int>.Handle fetchable_handle)
        {
            if (!ReferenceEquals(__instance, boundTo) || !__instance.prefabIdToFetchables.TryGetValue(prefab_tag, out var group)) return;
            var fetchables = group.fetchables;
            if (!fetchable_handle.IsValid() || !fetchables.IsVersionValid(fetchable_handle)) return;
            var item = fetchables.GetData(fetchable_handle).pickupable;
            if (registered.TryGetValue(item, out var entry) && entry.Handle == fetchable_handle) registered.Remove(item);
        }

        /// The offset pass, in place of the job.
        static void FixOffsets(IWorkItemCollection _, FetchManager manager)
        {
            updates++;
            if (!ReferenceEquals(manager, boundTo)) Bind(manager);
            if (duplicates)
            {
                CheckAll(manager, fix: true);
                return;
            }
            if (verify) CheckAll(manager, fix: false);
            snapshot.Clear();
            lock (marked)
            {
                if (marked.Count == 0) return;
                snapshot.AddRange(marked);
            }
            groupOrder.Clear();
            foreach (var group in manager.prefabIdToFetchables.Values) groupOrder[group] = groupOrder.Count;
            pass.Clear();
            foreach (var item in snapshot)
            {
                if (registered.TryGetValue(item, out var entry))
                    pass.Add((groupOrder[entry.Group], ListIndex(entry.Group, entry.Handle), item));
                else
                    lock (marked) marked.Remove(item); // not fetchable: FetchManager.Add marks it again
            }
            pass.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Index.CompareTo(b.Index));
            foreach (var (_, _, item) in pass)
            {
                int cell = item.cachedCell;
                if (!item.ValidateOffsets(cell))
                {
                    item.GetOffsets(cell);
                    fixedItems++;
                }
                if (item.ValidateOffsets(cell))
                    lock (marked) marked.Remove(item);
            }
            if (verify && updates % 2000 == 0) global::Debug.Log($"{Name}: {updates} updates, {fixedItems} items recomputed, {missed} stale items not marked");
        }

        /// The fetchables as the manager holds them now, every item marked to be checked.
        static void Bind(FetchManager manager)
        {
            boundTo = manager;
            registered.Clear();
            duplicates = false;
            foreach (var group in manager.prefabIdToFetchables.Values)
            {
                var data = group.fetchables.GetDataList();
                var hv = group.fetchables.handles;
                var slots = group.fetchables.dataHandleIndices; // each entry's handle slot
                for (int i = 0; i < data.Count; i++)
                {
                    var item = data[i].pickupable;
                    if (registered.ContainsKey(item)) duplicates = true;
                    registered[item] = (group, HandleFor(hv, slots[i]));
                    Mark(item);
                }
            }
        }


        /// The current handle of a slot of a handle vector (version in the top byte, as PackHandle).
        static HandleVector<int>.Handle HandleFor(HandleVector<int> hv, int slot)
        {
            var handle = HandleVector<int>.InvalidHandle;
            handle.index = (hv.versions[slot] << 24) | slot;
            return handle;
        }

        static int ListIndex(FetchManager.FetchablesByPrefabId group, HandleVector<int>.Handle handle)
        {
            var hv = group.fetchables.handles;
            hv.UnpackHandleUnchecked(handle, out _, out int slot);
            return hv.Items[slot];
        }

        /// The game's own pass, on the main thread: every fetchable checked, in group then list
        /// order; fix recomputes the stale ones, otherwise they are reported (verify mode).
        static void CheckAll(FetchManager manager, bool fix)
        {
            foreach (var group in manager.prefabIdToFetchables.Values)
            {
                foreach (Fetchable fetchable in group.fetchables.GetDataList())
                {
                    var item = fetchable.pickupable;
                    if (item.ValidateOffsets(item.cachedCell)) continue;
                    if (fix)
                    {
                        item.GetOffsets(item.cachedCell);
                        continue;
                    }
                    bool isMarked;
                    lock (marked) isMarked = marked.Contains(item);
                    if (isMarked) continue;
                    global::Debug.Log($"{Name}: {item.name} had stale offsets without being marked ({++missed})");
                    Mark(item);
                }
            }
        }
    }
}
