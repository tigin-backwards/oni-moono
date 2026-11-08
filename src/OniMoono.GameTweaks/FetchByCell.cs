using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using OniMoono.Tweaks;
using Fetchable = FetchManager.Fetchable;
using Group = FetchManager.FetchablesByPrefabId;
using Pickup = FetchManager.Pickup;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fetch-by-cell` (opt-in). Each Duplicant's fetch update (FetchManager.UpdatePickups,
    /// about 30 a second with brain-rate) runs, for every prefab group, FetchablesByPrefabId.UpdatePickups:
    /// CouldBePickedUpByMinion on every item, the path cost of each distinct cell (through the first
    /// item there that passed), a sort by (tag bits, priority desc, cost, quality desc, freshness desc)
    /// and a dedup to the first item of each (tag bits, priority). A late-game colony keeps tens of
    /// thousands of items in a few thousand piles (one with 33,600 items in 3,600 cells), so most of
    /// that work is the same item over again.
    ///
    /// A mirror kept by the group's own updates (add, remove, storage, tags, freshness), the items'
    /// cell changes and their reservations holds, per (tag bits, priority, cell) key, the key's best
    /// item by the game's order within one cell (quality desc, freshness desc). An update costs each
    /// key's cell once, as the game does, keeps for each (tag bits, priority) the cheapest key, and
    /// writes the group's result in the game's order; the manager's merge and sort are the game's.
    /// The best item is still checked with CouldBePickedUpByMinion (a key whose best fails checks
    /// all of its items), and so is every item whose answer can differ by Duplicant: those with
    /// reservations, and those in a storage with an Automatable (conveyor loaders and receptacles).
    /// A cell whose items take their work spots from different sources (CellSources: a bottle
    /// filler's stored water, given the filler's work cell, beside loose water) is costed as the
    /// game costs it, through the first item there in the group's list that passes.
    ///
    /// Keys on worlds the Duplicant cannot reach are left out: a cell at least R cells inside a
    /// world's rectangle (R: the largest offset any fetchable's tracker can return) on a world none
    /// of the Duplicant's probed cells (Navigator.occupiedCells, exactly the cells PathGrid.GetCost
    /// answers for) lies on has no path cost through any item there, so the game drops it too.
    ///
    /// Equal to the game's but for ties: where two items of one prefab have the same tag bits,
    /// priority, cost, quality and freshness, the game's unstable sort picks one of them and this
    /// may pick the other. The merged list's order is the same (its sort compares only those).
    ///
    /// Off while another mod patches what it replaces or relies on (Guarded*): Efficient Supply,
    /// CycleTrim and Fast Track's Optimize Debris Collection replace the group's update; Waste Not,
    /// Want Not and Delivery Temperature Limit change its order; No Manual Delivery, Forbid Items,
    /// Controlled Storage and others change CouldBePickedUpByMinion. The world skip alone is off
    /// while another mod changes path costs or probes (Teleport Suit, GRAVNet, Fast Track's paths).
    /// Fast Track's Reduce Debris Checks never runs the manager's update, so this never starts.
    ///
    /// ONI_VERIFY_FETCH_BY_CELL=1 (or ONI_TWEAK_VERIFY=1) keeps the game's pass and runs this one
    /// beside it, logging any result that differs other than by a tie, any item in a left-out cell
    /// that had a path cost, and the mirror's drift from the game's lists, checked every few
    /// seconds in this mode (in play only after one of its hooks ran off the main thread). ONI_FETCH_STATS=1 times the update.
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Void FetchManager::UpdatePickups(Navigator,WorkerBase)")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<FetchManager/Pickup> FetchManager::pickups")]
    [GameMember(GameUse.Postfix, "HandleVector`1/Handle<System.Int32> FetchManager/FetchablesByPrefabId::AddPickupable(Pickupable)")]
    [GameMember(GameUse.Prefix, "System.Void FetchManager/FetchablesByPrefabId::RemovePickupable(HandleVector`1/Handle<System.Int32>)")]
    [GameMember(GameUse.Postfix, "System.Void FetchManager/FetchablesByPrefabId::Sim1000ms(System.Single)")]
    [GameMember(GameUse.Prefix | GameUse.Postfix | GameUse.Replaces, "System.Void FetchManager/FetchablesByPrefabId::UpdatePickups(Navigator,System.Int32)", "e4cc1335082bb485 linux-744825 win-744825", "b45774460058219d linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Mirrors, "System.Void FetchManager/FetchablesByPrefabId::GatherPickupablesWhichCanBePickedUp(System.Int32)", "6cd11484113f6092 linux-744825 win-744825", "2fc63b7f2adc7707 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Mirrors, "System.Void FetchManager/FetchablesByPrefabId::GatherReachablePickups(Navigator)", "4c726f3c5109962d linux-744825 win-744825", "d228f46314d8bb69 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Postfix, "System.Void FetchManager/FetchablesByPrefabId::UpdateStorage(HandleVector`1/Handle<System.Int32>,Storage)")]
    [GameMember(GameUse.Postfix, "System.Void FetchManager/FetchablesByPrefabId::UpdateTags(HandleVector`1/Handle<System.Int32>)")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<HandleVector`1/Handle<System.Int32>,Rottable/Instance> FetchManager/FetchablesByPrefabId::rotUpdaters")]
    [GameMember(GameUse.Uses, "System.Comparison`1<FetchManager/Pickup> FetchManager/PickupComparerIncludingPriority::CompareInst")]
    [GameMember(GameUse.Uses, "System.Comparison`1<FetchManager/Pickup> FetchManager/PickupComparerNoPriority::CompareInst")]
    [GameMember(GameUse.Mirrors, "System.Int32 FetchManager/PickupComparerIncludingPriority::Compare(FetchManager/Pickup,FetchManager/Pickup)", "413f1c94b8c1808f linux-744825 win-744825", "5252581b0567c63f linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Mirrors, "System.Int32 FetchManager/PickupComparerNoPriority::Compare(FetchManager/Pickup,FetchManager/Pickup)", "57122b4609e03d09 linux-744825 win-744825", "d3146cafe60500f5 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Uses, "HandleVector`1<System.Int32> KCompactedVectorBase::handles")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<System.Int32> KCompactedVectorBase::dataHandleIndices")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<System.Byte> HandleVector`1::versions")]
    [GameMember(GameUse.Mirrors, "System.Int32 Navigator::GetNavigationCost(System.Int32)", "4eba7ac2e3d9026a linux-744825 win-744825", "b4d6025ca9eb0758 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Mirrors, "System.Int32 Navigator::GetNavigationCost(System.Int32,System.Collections.Generic.IReadOnlyList`1<CellOffset>)", "20db8b62bd525136 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<CellOffset> OffsetTracker::offsets")]
    [GameMember(GameUse.Uses, "CellOffset[][] OffsetTableTracker::table")]
    [GameMember(GameUse.Mirrors, "System.Int32 PathGrid::GetCost(System.Int32)", "9867f47471c0017d linux-744825 win-744825", "7563b0821f466925 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Postfix, "System.Void Pickupable::ClearReservations()")]
    [GameMember(GameUse.Mirrors, "System.Boolean Pickupable::CouldBePickedUpByMinion(System.Int32)", "c524d09a8866c7d2 linux-744825 win-744825", "2a8030098ddb463e linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Mirrors, "System.Boolean Pickupable::CouldBePickedUpCommon(System.Int32)", "b30f277116b23868 linux-744825 win-744825", "ec801066e2b989a2 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Postfix, "System.Int32 Pickupable::Reserve(System.String,System.Int32,System.Single)")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<Pickupable/Reservation> Pickupable::reservations")]
    [GameMember(GameUse.Postfix, "System.Void Pickupable::Unreserve(System.String,System.Int32)")]
    [GameMember(GameUse.Postfix, "System.Void Pickupable::UpdateCachedCell(System.Int32)")]
    [GameMember(GameUse.Mirrors, "System.Int32 Workable::GetNavigationCost(Navigator,System.Int32)", "6f770195fcf55293 linux-744825 win-744825", "914e9e06b539ba07 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Uses, "OffsetTracker Workable::offsetTracker")]
    [GameMember(GameUse.Postfix, "System.Void Workable::SetOffsets(CellOffset[])")]
    [GameMember(GameUse.Postfix, "System.Void Workable::SetOffsetTable(CellOffset[][])")]
    public static class FetchByCell
    {
        public const string Name = "fetch-by-cell";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1" || Environment.GetEnvironmentVariable("ONI_VERIFY_FETCH_BY_CELL") == "1";
        static readonly double resyncSeconds = double.TryParse(Environment.GetEnvironmentVariable("ONI_FETCH_RESYNC_S"), out var r) && r > 0 ? r : 5;
        const int UnknownRadius = 1 << 20;
        const byte NoWorld = byte.MaxValue;

        static bool installed;
        enum State { Unchecked, On, Off }
        static State state, worldState;

        // ---- Game internals ----
        static readonly AccessTools.FieldRef<Group, Dictionary<HandleVector<int>.Handle, Rottable.Instance>> rotUpdaters =
            AccessTools.FieldRefAccess<Group, Dictionary<HandleVector<int>.Handle, Rottable.Instance>>("rotUpdaters");
        static readonly AccessTools.FieldRef<Pickupable, List<Pickupable.Reservation>> reservations =
            AccessTools.FieldRefAccess<Pickupable, List<Pickupable.Reservation>>("reservations");
        static readonly AccessTools.FieldRef<FetchManager, List<Pickup>> managerPickups = AccessTools.FieldRefAccess<FetchManager, List<Pickup>>("pickups");
        static readonly AccessTools.FieldRef<Workable, OffsetTracker> offsetTracker = AccessTools.FieldRefAccess<Workable, OffsetTracker>("offsetTracker");
        static readonly AccessTools.FieldRef<OffsetTracker, List<CellOffset>> trackerOffsets = AccessTools.FieldRefAccess<OffsetTracker, List<CellOffset>>("offsets");
        static readonly AccessTools.FieldRef<OffsetTableTracker, CellOffset[][]> trackerTable = AccessTools.FieldRefAccess<OffsetTableTracker, CellOffset[][]>("table");
        static readonly AccessTools.FieldRef<KCompactedVectorBase, HandleVector<int>> handles = AccessTools.FieldRefAccess<KCompactedVectorBase, HandleVector<int>>("handles");
        static readonly AccessTools.FieldRef<KCompactedVectorBase, List<int>> dataHandleIndices = AccessTools.FieldRefAccess<KCompactedVectorBase, List<int>>("dataHandleIndices");
        static readonly AccessTools.FieldRef<HandleVector<int>, List<byte>> versions = AccessTools.FieldRefAccess<HandleVector<int>, List<byte>>("versions");
        static Comparison<Pickup> compareWithPriority, compareNoPriority;

        // ---- The mirror ----
        sealed class Item
        {
            public Pickupable P;
            public GroupMirror G;
            public HandleVector<int>.Handle Handle;
            public int Cell, Tag, Prio, Fresh, Qual;
            public bool Special, Rot;
            public Key Key;
            public int Pos = -1; // in Key.Members or Key.Specials
            public int Class = -2; // world index counted in G.Interior, -1 counted in G.Outside, -2 not counted
            public object Source; // where its work spots come from (SourceOf), counted in G.Cells
            public int RotPos = -1;
        }

        sealed class Key
        {
            public int Tag, Prio, Cell, Pos = -1;
            public readonly List<Item> Members = new List<Item>();
            public readonly List<Item> Specials = new List<Item>();
            public Item Best;
            public bool Dirty;
        }

        sealed class GroupMirror
        {
            public Group Group;
            public readonly Dictionary<int, Item> ByHandle = new Dictionary<int, Item>();
            public readonly Dictionary<(int, int, int), Key> Keys = new Dictionary<(int, int, int), Key>();
            public readonly List<Key> KeyList = new List<Key>();
            public readonly List<Item> RotItems = new List<Item>();
            public readonly Dictionary<int, CellSources> Cells = new Dictionary<int, CellSources>();
            public int[] Interior = new int[64];
            public int Outside;
            public bool Unsafe;
            // Per update (one worker per group at a time)
            public readonly List<Pickup> Shadow = new List<Pickup>();
            public readonly Dictionary<int, int> Costs = new Dictionary<int, int>();
            public readonly Dictionary<long, (Item Item, ushort Cost)> Subs = new Dictionary<long, (Item, ushort)>();
            public readonly List<(Item Item, ushort Cost)> SubList = new List<(Item, ushort)>();
            public bool PendingCompare;
            public readonly HashSet<int> SkippedCells = new HashSet<int>();
            public long Exceptions, SpecialChecks, KeysSeen, KeysSkipped, GroupsSkipped, Recomputes, MixedCosts;
        }

        static FetchManager boundTo;
        static readonly Dictionary<Group, GroupMirror> groups = new Dictionary<Group, GroupMirror>();
        static readonly Dictionary<Pickupable, Item> byItem = new Dictionary<Pickupable, Item>();
        static bool offThread;
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static double lastResync, boundAt;
        
        // ---- The world view ----
        static int radius;
        static readonly Dictionary<CellOffset[][], int> tableRadius = new Dictionary<CellOffset[][], int>();
        static readonly Dictionary<Type, bool> overridesOffsets = new Dictionary<Type, bool>();
        static readonly int[] rectX = new int[256], rectY = new int[256], rectW = new int[256], rectH = new int[256];
        static readonly bool[] rectOk = new bool[256];
        static long layout;
        static readonly bool[] reached = new bool[256];
        static bool skipActive, active;
        /// Each navigator's reached worlds from its last probe (kept weakly: destroyed navigators go).
        sealed class NavReach
        {
            public List<int> Cells;
            public int Count;
            public PathGrid Grid;
            public ushort Serial;
            public readonly bool[] Reached = new bool[256];
        }
        static System.Runtime.CompilerServices.ConditionalWeakTable<Navigator, NavReach> navCache = new System.Runtime.CompilerServices.ConditionalWeakTable<Navigator, NavReach>();

        // ---- Shadow counters ----
        static long mismatches, gIdentical, gTie, gOther, lIdentical, lTie, lOther, premiseChecked, premiseWrong, shadowUpdates;
        static int otherLogged;
        static double lastShadowLog;

        // ---- Stats ----
        static readonly bool stats = Environment.GetEnvironmentVariable("ONI_FETCH_STATS") == "1";
        static bool statsInstalled;
        [ThreadStatic] static long itemStart;
        static long workerTicks, mainTicks, mainStart, calls;
        static int windowStartFrame = -1;
        static double windowStartTime;

        public static void Install(Harmony harmony)
        {
            if (installed) return;
            installed = true;
            InstallShared(harmony);
        }

        static HarmonyMethod M(string name, int priority = -1) =>
            priority < 0 ? new HarmonyMethod(typeof(FetchByCell), name) : new HarmonyMethod(typeof(FetchByCell), name) { priority = priority };

        static void InstallShared(Harmony harmony)
        {
            MainThread.Install(harmony);
            compareWithPriority = (Comparison<Pickup>)AccessTools.Field(AccessTools.Inner(typeof(FetchManager), "PickupComparerIncludingPriority"), "CompareInst").GetValue(null);
            compareNoPriority = (Comparison<Pickup>)AccessTools.Field(AccessTools.Inner(typeof(FetchManager), "PickupComparerNoPriority"), "CompareInst").GetValue(null);
            var g = typeof(Group);
            harmony.Patch(AccessTools.Method(typeof(FetchManager), nameof(FetchManager.UpdatePickups)), prefix: M(nameof(ManagerPre)), postfix: M(nameof(ManagerPost)));
            harmony.Patch(AccessTools.Method(g, nameof(Group.UpdatePickups)), prefix: M(nameof(GroupPre), Priority.Last), postfix: M(nameof(GroupPost)));
            harmony.Patch(AccessTools.Method(g, nameof(Group.AddPickupable)), postfix: M(nameof(Added)));
            harmony.Patch(AccessTools.Method(g, nameof(Group.RemovePickupable)), prefix: M(nameof(Removing)));
            harmony.Patch(AccessTools.Method(g, nameof(Group.UpdateStorage)), postfix: M(nameof(DataChanged)));
            harmony.Patch(AccessTools.Method(g, nameof(Group.UpdateTags)), postfix: M(nameof(DataChanged)));
            harmony.Patch(AccessTools.Method(g, nameof(Group.Sim1000ms)), postfix: M(nameof(Freshened)));
            harmony.Patch(AccessTools.Method(typeof(Pickupable), nameof(Pickupable.UpdateCachedCell)), postfix: M(nameof(CellChanged)));
            foreach (var name in new[] { nameof(Pickupable.Reserve), nameof(Pickupable.Unreserve), nameof(Pickupable.ClearReservations) })
                harmony.Patch(AccessTools.Method(typeof(Pickupable), name), postfix: M(nameof(ReservationsChanged)));
            harmony.Patch(AccessTools.Method(typeof(Workable), nameof(Workable.SetOffsets)), postfix: M(nameof(TrackerChanged)));
            harmony.Patch(AccessTools.Method(typeof(Workable), nameof(Workable.SetOffsetTable)), postfix: M(nameof(TrackerChanged)));
        }

        /// ONI_FETCH_STATS=1 (any tweak set, these two on or off): times the update and its groups.
        public static void InstallStats(Harmony harmony)
        {
            if (statsInstalled) return;
            statsInstalled = true;
            harmony.Patch(AccessTools.Method(typeof(FetchManager), nameof(FetchManager.UpdatePickups)),
                prefix: M(nameof(StatsMainPre), Priority.First), postfix: M(nameof(StatsMainPost), Priority.Last));
            harmony.Patch(AccessTools.Method(typeof(Group), nameof(Group.UpdatePickups)),
                prefix: M(nameof(StatsItemPre), Priority.First), postfix: M(nameof(StatsItemPost), Priority.Last));
        }

        // ---- Stand-down ----

        static IEnumerable<MethodBase> GuardedBoth()
        {
            var g = typeof(Group);
            foreach (var name in new[] { "UpdatePickups", "GatherPickupablesWhichCanBePickedUp", "GatherReachablePickups", "AddPickupable", "RemovePickupable", "UpdateStorage", "UpdateTags", "Sim1000ms" })
                yield return AccessTools.Method(g, name);
            yield return AccessTools.Method(typeof(Pickupable), nameof(Pickupable.UpdateCachedCell));
            yield return AccessTools.Method(typeof(Workable), nameof(Workable.GetNavigationCost), new[] { typeof(Navigator), typeof(int) });
        }

        static IEnumerable<MethodBase> GuardedSkip()
        {
            yield return AccessTools.Method(typeof(Navigator), nameof(Navigator.GetNavigationCost), new[] { typeof(int), typeof(IReadOnlyList<CellOffset>) });
            yield return AccessTools.Method(typeof(Navigator), nameof(Navigator.GetNavigationCost), new[] { typeof(int) });
            yield return AccessTools.Method(typeof(PathGrid), nameof(PathGrid.GetCost));
            yield return AccessTools.Method(typeof(Navigator), nameof(Navigator.TakeResult));
            yield return AccessTools.Method(typeof(Navigator), nameof(Navigator.UpdateProbe));
            yield return AccessTools.Method(typeof(Workable), nameof(Workable.GetOffsets), new[] { typeof(int) });
            yield return AccessTools.Method(typeof(Workable), nameof(Workable.SetOffsets));
            yield return AccessTools.Method(typeof(Workable), nameof(Workable.SetOffsetTable));
            yield return AccessTools.Method(typeof(OffsetTracker), nameof(OffsetTracker.GetOffsets));
            yield return AccessTools.Method(typeof(OffsetTableTracker), "UpdateOffsets", new[] { typeof(int) });
        }

        static IEnumerable<MethodBase> GuardedCell()
        {
            yield return AccessTools.Method(typeof(Pickupable), nameof(Pickupable.CouldBePickedUpByMinion), new[] { typeof(int) });
            yield return AccessTools.Method(typeof(Pickupable), "CouldBePickedUpCommon", new[] { typeof(int) });
            yield return AccessTools.Method(typeof(Pickupable), nameof(Pickupable.FindReservedAmount), new[] { typeof(int) });
            yield return AccessTools.PropertyGetter(typeof(Pickupable), nameof(Pickupable.UnreservedFetchAmount));
            yield return AccessTools.PropertyGetter(typeof(Pickupable), nameof(Pickupable.FetchTotalAmount));
            yield return AccessTools.Method(typeof(Pickupable), nameof(Pickupable.Reserve));
            yield return AccessTools.Method(typeof(Pickupable), nameof(Pickupable.Unreserve));
            yield return AccessTools.Method(typeof(Pickupable), nameof(Pickupable.ClearReservations));
            // Its own order in a cell and across cells (Waste Not, Want Not and Delivery Temperature
            // Limit transpile these).
            yield return AccessTools.Method(AccessTools.Inner(typeof(FetchManager), "PickupComparerIncludingPriority"), "Compare");
            yield return AccessTools.Method(AccessTools.Inner(typeof(FetchManager), "PickupComparerNoPriority"), "Compare");
        }

        static string Foreign(IEnumerable<MethodBase> methods)
        {
            var owners = new List<KeyValuePair<string, IEnumerable<string>>>();
            foreach (var m in methods)
            {
                if (m == null) return "a method it relies on was not found";
                var info = Harmony.GetPatchInfo(m);
                if (info != null) owners.Add(new KeyValuePair<string, IEnumerable<string>>($"{m.DeclaringType?.Name}.{m.Name}", info.Owners));
            }
            return ProbeRepairPolicy.Foreign(owners);
        }

        static void Check()
        {
            string why;
            try { why = Foreign(GuardedBoth()) ?? Foreign(GuardedCell()); }
            catch (Exception e) { why = "its methods could not be read: " + e.Message; }
            state = why == null ? State.On : State.Off;
            if (why != null)
            {
                global::Debug.Log($"{Name}: off ({why}); the game's pass stays");
                return;
            }
            string worlds;
            try { worlds = Foreign(GuardedSkip()); }
            catch (Exception e) { worlds = "its methods could not be read: " + e.Message; }
            worldState = worlds == null ? State.On : State.Off;
            global::Debug.Log($"{Name}: on" + (worlds == null ? "" : $", unreachable worlds not left out ({worlds})"));
        }

        // ---- The update ----

        static void ManagerPre(FetchManager __instance, Navigator navigator)
        {
            active = false;
            skipActive = false;
            if (state == State.Unchecked) Check();
            if (state != State.On) return;
            if (!ReferenceEquals(__instance, boundTo)) Bind(__instance);
            if (CheckLayout()) Recount();
            double now = clock.Elapsed.TotalSeconds;
            // The mirror is checked against the game's lists only in verify mode (every few
            // seconds, logging drift) and after a hook ran off the main thread.
            if (offThread || (verify && now - lastResync > resyncSeconds))
            {
                Resync(__instance);
                lastResync = now;
            }
            if (stats && !measured && now - boundAt > 20) MeasureMirror(__instance);
            if (worldState == State.On) skipActive = Reached(navigator) && radius < UnknownRadius;
            active = true;
        }

        static void ManagerPost(FetchManager __instance)
        {
            if (!active) return;
            active = false;
            if (!verify) return;
            // The merged list the tweaked groups make, sorted as the game sorts its own.
            var ours = new List<Pickup>();
            foreach (var kv in __instance.prefabIdToFetchables)
                if (groups.TryGetValue(kv.Value, out var gm) && !gm.Unsafe) ours.AddRange(gm.Shadow);
                else ours.AddRange(kv.Value.finalPickups);
            ours.Sort(compareNoPriority);
            switch (Classify(managerPickups(__instance), ours, null))
            {
                case 0: lIdentical++; break;
                case 1: lTie++; break;
                default: lOther++; break;
            }
            ShadowLog();
        }

        static bool GroupPre(Group __instance, Navigator worker_navigator, int worker, bool __runOriginal)
        {
            if (!active || !__runOriginal) return true;
            if (!groups.TryGetValue(__instance, out var gm) || gm.Unsafe) return true;
            if (verify)
            {
                Run(gm, worker_navigator, worker, gm.Shadow);
                gm.PendingCompare = true;
                return true;
            }
            Run(gm, worker_navigator, worker, __instance.finalPickups);
            return false;
        }

        static void GroupPost(Group __instance, Navigator worker_navigator)
        {
            if (!verify || !active || !groups.TryGetValue(__instance, out var gm) || !gm.PendingCompare) return;
            gm.PendingCompare = false;
            switch (Classify(__instance.finalPickups, gm.Shadow, gm))
            {
                case 0: Interlocked.Increment(ref gIdentical); break;
                case 1: Interlocked.Increment(ref gTie); break;
                default: Interlocked.Increment(ref gOther); break;
            }
            // The premise: every item in a skipped cell has cost -1.
            if (gm.SkippedCells.Count > 0)
                foreach (var data in __instance.fetchables.GetDataList())
                {
                    int cell = data.pickupable.cachedCell;
                    if (!gm.SkippedCells.Contains(cell)) continue;
                    Interlocked.Increment(ref premiseChecked);
                    if (data.pickupable.GetNavigationCost(worker_navigator, cell) != -1)
                    {
                        long n = Interlocked.Increment(ref premiseWrong);
                        if (n <= 20) global::Debug.LogWarning($"{Name}: {data.pickupable.name} in a left-out cell {cell} (world {Grid.WorldIdx[cell]}) has a path cost ({n})");
                    }
                }
        }

        static void Run(GroupMirror gm, Navigator nav, int worker, List<Pickup> output)
        {
            gm.SkippedCells.Clear();
            RunByCell(gm, nav, worker, output);
        }

        static void AddSkipped(GroupMirror gm, int cell)
        {
            gm.SkippedCells.Add(cell);
        }

        static bool Better(Item a, Item b) => a.Qual > b.Qual || (a.Qual == b.Qual && a.Fresh > b.Fresh);

        static void RunByCell(GroupMirror gm, Navigator nav, int worker, List<Pickup> output)
        {
            output.Clear();
            if (skipActive && FullySkipped(gm))
            {
                gm.GroupsSkipped++;
                if (verify) foreach (var key in gm.KeyList) AddSkipped(gm, key.Cell);
                return;
            }
            var costs = gm.Costs;
            costs.Clear();
            var subs = gm.Subs;
            subs.Clear();
            foreach (var key in gm.KeyList)
            {
                gm.KeysSeen++;
                if (skipActive && Skipped(key.Cell))
                {
                    gm.KeysSkipped++;
                    if (verify) AddSkipped(gm, key.Cell);
                    continue;
                }
                if (key.Dirty) Recompute(gm, key);
                var cand = PileChoice.Choose(key.Best, key.Members, key.Specials, new ItemRules { Worker = worker }, ref gm.Exceptions, ref gm.SpecialChecks);
                if (cand == null) continue;
                if (!costs.TryGetValue(key.Cell, out int cost))
                {
                    // The game costs a cell through the first item there in its list that passed;
                    // while every item there takes its work spots from one source, any gives that.
                    var via = gm.Cells.TryGetValue(key.Cell, out var sources) && !sources.Uniform ? FirstPassing(gm, key.Cell, worker) : cand.P;
                    cost = via == null ? -1 : via.GetNavigationCost(nav, key.Cell);
                    costs[key.Cell] = cost;
                }
                if (cost == -1) continue;
                ushort pathCost = (ushort)cost;
                long sub = ((long)key.Tag << 32) | (uint)key.Prio;
                if (!subs.TryGetValue(sub, out var cur) || pathCost < cur.Cost || (pathCost == cur.Cost && Better(cand, cur.Item)))
                    subs[sub] = (cand, pathCost);
            }
            var list = gm.SubList;
            list.Clear();
            foreach (var v in subs.Values) list.Add(v);
            list.Sort((a, b) =>
            {
                int c = a.Item.Tag.CompareTo(b.Item.Tag);
                return c != 0 ? c : b.Item.Prio.CompareTo(a.Item.Prio);
            });
            foreach (var (item, cost) in list)
                output.Add(new Pickup { pickupable = item.P, tagBitsHash = item.Tag, PathCost = cost, masterPriority = item.Prio, freshness = item.Fresh, foodQuality = item.Qual });
        }

        static void Recompute(GroupMirror gm, Key key)
        {
            gm.Recomputes++;
            key.Best = PileChoice.Best(key.Members, new ItemRules());
            key.Dirty = false;
        }

        /// The game's choice for a cell whose items' work spots differ: the first item there, in
        /// the group's list, that this Duplicant may pick up.
        static Pickupable FirstPassing(GroupMirror gm, int cell, int worker)
        {
            gm.MixedCosts++;
            foreach (var data in gm.Group.fetchables.GetDataList())
            {
                var p = data.pickupable;
                if (p.cachedCell == cell && p.CouldBePickedUpByMinion(worker)) return p;
            }
            return null;
        }

        struct ItemRules : IPileRules<Item>
        {
            public int Worker;
            public bool CanPick(Item item) => item.P.CouldBePickedUpByMinion(Worker);
            public bool Better(Item a, Item b) => FetchByCell.Better(a, b);
        }

        /// 0 identical, 1 the same keys with other items, 2 other.
        static int Classify(List<Pickup> game, List<Pickup> ours, GroupMirror gm)
        {
            int result = 0;
            if (game.Count != ours.Count) result = 2;
            else
                for (int i = 0; i < game.Count; i++)
                {
                    var a = game[i];
                    var b = ours[i];
                    if (a.tagBitsHash != b.tagBitsHash || a.masterPriority != b.masterPriority || a.PathCost != b.PathCost || a.foodQuality != b.foodQuality || a.freshness != b.freshness
                        || (gm == null && a.pickupable.PrefabID() != b.pickupable.PrefabID()))
                    {
                        result = 2;
                        break;
                    }
                    if (!ReferenceEquals(a.pickupable, b.pickupable)) result = 1;
                }
            if (result == 2 && Interlocked.Increment(ref otherLogged) <= 30)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"{Name}: {(gm != null ? gm.Group.prefabId + "'s result" : "the merged list")} differs from the game's ({otherLogged})");
                sb.Append($"\n  game {game.Count}, ours {ours.Count}");
                for (int i = 0; i < Math.Max(game.Count, ours.Count) && i < 12; i++)
                    sb.Append($"\n  {i}: game {Describe(game, i)} | ours {Describe(ours, i)}");
                global::Debug.LogWarning(sb.ToString());
            }
            return result;
        }

        static string Describe(List<Pickup> list, int i)
        {
            if (i >= list.Count) return "-";
            var p = list[i];
            return $"{p.pickupable.PrefabID()}#{p.pickupable.GetInstanceID()} cell {p.pickupable.cachedCell} tag {p.tagBitsHash} prio {p.masterPriority} cost {p.PathCost} q {p.foodQuality} f {p.freshness}";
        }

        static void ShadowLog()
        {
            if (!verify) return;
            shadowUpdates++;
            double now = clock.Elapsed.TotalSeconds;
            if (now - lastShadowLog < 30) return;
            lastShadowLog = now;
            long exc = 0, spc = 0, ks = 0, kk = 0, gsk = 0, mixedCosts = 0;
            foreach (var gm in groups.Values) { exc += gm.Exceptions; spc += gm.SpecialChecks; ks += gm.KeysSeen; kk += gm.KeysSkipped; gsk += gm.GroupsSkipped; mixedCosts += gm.MixedCosts; }
            global::Debug.Log($"{Name}: {shadowUpdates} updates; {gIdentical + gTie + gOther} group results, {gTie} ties, {gOther} differed; "
                + $"{lIdentical + lTie + lOther} merged lists, {lTie} ties, {lOther} differed; {premiseChecked} items in left-out cells, {premiseWrong} wrong; "
                + $"{resyncs} resyncs, {mismatches} mismatches; {ks} keys, {kk} left out, {gsk} groups left out, {exc} best items failed, {spc} special checks, {mixedCosts} cells costed through their first item");
        }

        // ---- Worlds ----

        /// Whether the navigator's reached worlds are known (reached[]): those of its occupied
        /// cells (its last probe's), and its own.
        static bool Reached(Navigator nav)
        {
            if (nav == null || !nav.reportOccupation) return false;
            var cells = nav.occupiedCells;
            var grid = nav.PathGrid;
            if (cells == null || cells.Count == 0 || grid == null) return false;
            var c = navCache.GetOrCreateValue(nav);
            if (verify || !ReferenceEquals(c.Cells, cells) || c.Count != cells.Count || !ReferenceEquals(c.Grid, grid) || c.Serial != grid.SerialNo)
            {
                Array.Clear(c.Reached, 0, 256);
                var worldIdx = Grid.WorldIdx;
                for (int i = 0; i < cells.Count; i++) c.Reached[worldIdx[cells[i]]] = true;
                c.Cells = cells;
                c.Count = cells.Count;
                c.Grid = grid;
                c.Serial = grid.SerialNo;
            }
            var flags = c.Reached;
            Array.Copy(flags, reached, 256);
            if (Grid.IsValidCell(nav.cachedCell)) reached[Grid.WorldIdx[nav.cachedCell]] = true;
            reached[NoWorld] = true;
            return true;
        }

        /// A cell whose items have no path cost for this Duplicant: on a world its probe did not
        /// reach, radius cells inside the world's rectangle.
        static bool Skipped(int cell)
        {
            int w = Class(cell);
            return w >= 0 && !reached[w];
        }

        /// The world a cell is radius cells inside of, or -1.
        static int Class(int cell)
        {
            if (!Grid.IsValidCell(cell)) return -1;
            int w = Grid.WorldIdx[cell];
            if (w == NoWorld || !rectOk[w]) return -1;
            int x = cell % Grid.WidthInCells, y = cell / Grid.WidthInCells, rr = radius;
            if (x - rr < rectX[w] || x + rr >= rectX[w] + rectW[w] || y - rr < rectY[w] || y + rr >= rectY[w] + rectH[w]) return -1;
            return w;
        }

        static bool FullySkipped(GroupMirror gm)
        {
            if (gm.Outside > 0) return false;
            var counts = gm.Interior;
            for (int w = 0; w < counts.Length; w++)
                if (counts[w] > 0 && reached[w]) return false;
            return true;
        }

        /// Whether the worlds' rectangles changed (then each is checked against Grid.WorldIdx).
        static bool CheckLayout()
        {
            var cm = ClusterManager.Instance;
            if (cm == null) return false;
            long now = 17;
            foreach (var world in cm.WorldContainers)
                now = ((((now * 31 + world.id) * 31 + world.WorldOffset.x) * 31 + world.WorldOffset.y) * 31 + world.WorldSize.x) * 31 + world.WorldSize.y;
            if (now == layout) return false;
            layout = now;
            Array.Clear(rectOk, 0, 256);
            int width = Grid.WidthInCells, height = Grid.HeightInCells;
            foreach (var world in cm.WorldContainers)
            {
                int id = world.id;
                if (id < 0 || id >= NoWorld) continue;
                int x0 = world.WorldOffset.x, y0 = world.WorldOffset.y, w = world.WorldSize.x, h = world.WorldSize.y;
                bool ok = x0 >= 0 && y0 >= 0 && w > 0 && h > 0 && x0 + w <= width && y0 + h <= height;
                for (int y = y0; ok && y < y0 + h; y++)
                    for (int x = x0; x < x0 + w; x++)
                        if (Grid.WorldIdx[y * width + x] != id) { ok = false; break; }
                rectX[id] = x0; rectY[id] = y0; rectW[id] = w; rectH[id] = h;
                rectOk[id] = ok;
                if (!ok) global::Debug.Log($"{Name}: world {id}'s rectangle holds other worlds' cells; its cells are never left out");
            }
            return true;
        }

        static int RadiusOf(Pickupable p)
        {
            var type = p.GetType();
            if (!overridesOffsets.TryGetValue(type, out bool overrides))
            {
                var m = type.GetMethod(nameof(Workable.GetOffsets), BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null);
                overrides = m == null || m.DeclaringType != typeof(Workable);
                overridesOffsets[type] = overrides;
            }
            if (overrides) return UnknownRadius;
            var tracker = offsetTracker(p);
            if (tracker == null) return 0; // GetOffsets makes a one-offset (0, 0) tracker
            if (tracker.GetType() == typeof(OffsetTableTracker))
            {
                var table = trackerTable((OffsetTableTracker)tracker);
                if (!tableRadius.TryGetValue(table, out int tr))
                {
                    tr = 0;
                    foreach (var row in table)
                        if (row != null && row.Length > 0) tr = Math.Max(tr, Math.Max(Math.Abs(row[0].x), Math.Abs(row[0].y)));
                    tableRadius[table] = tr;
                }
                return tr;
            }
            if (tracker.GetType() == typeof(StandardOffsetTracker))
            {
                int sr = 0;
                var offsets = trackerOffsets(tracker);
                if (offsets != null) foreach (var o in offsets) sr = Math.Max(sr, Math.Max(Math.Abs(o.x), Math.Abs(o.y)));
                return sr;
            }
            return UnknownRadius;
        }

        /// Where an item's work spots come from: its offset table (the same table at the same cell
        /// gives the same spots), its fixed offsets by value, or the item itself when neither.
        static object SourceOf(Pickupable p, Item item)
        {
            var type = p.GetType();
            if (!overridesOffsets.TryGetValue(type, out bool overrides))
            {
                var m = type.GetMethod(nameof(Workable.GetOffsets), BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null);
                overrides = m == null || m.DeclaringType != typeof(Workable);
                overridesOffsets[type] = overrides;
            }
            if (overrides) return item;
            var tracker = offsetTracker(p);
            if (tracker == null) return "0,0"; // GetOffsets makes a one-offset (0, 0) tracker
            if (tracker.GetType() == typeof(OffsetTableTracker)) return trackerTable((OffsetTableTracker)tracker);
            if (tracker.GetType() != typeof(StandardOffsetTracker)) return item;
            var offsets = trackerOffsets(tracker);
            if (offsets == null) return item;
            var keys = new List<long>();
            foreach (var o in offsets) keys.Add(((long)o.x << 32) | (uint)o.y);
            keys.Sort();
            var sb = new System.Text.StringBuilder();
            long previous = long.MinValue;
            foreach (var k in keys)
                if (k != previous) { sb.Append(k >> 32).Append(',').Append((int)k).Append(';'); previous = k; }
            return sb.Length == 4 && sb.ToString() == "0,0;" ? "0,0" : sb.ToString();
        }

        static void SeeRadius(Pickupable p)
        {
            int rr = RadiusOf(p);
            if (rr <= radius) return;
            if (rr >= UnknownRadius && radius < UnknownRadius) global::Debug.Log($"{Name}: {p.name}'s offsets come from a tracker of its own; no cell is left out");
            radius = rr;
            needRecount = true;
        }

        static bool needRecount;

        // ---- The mirror's upkeep ----

        static GroupMirror Mirror(Group group)
        {
            if (boundTo == null) return null;
            if (!groups.TryGetValue(group, out var gm))
            {
                gm = new GroupMirror { Group = group };
                groups[group] = gm;
            }
            return gm;
        }

        static void Bind(FetchManager manager)
        {
            boundTo = manager;
            groups.Clear();
            byItem.Clear();
            navCache = new System.Runtime.CompilerServices.ConditionalWeakTable<Navigator, NavReach>();
            layout = 0;
            radius = 0;
            CheckLayout();
            foreach (var group in manager.prefabIdToFetchables.Values) Build(Mirror(group));
            Recount();
            lastResync = boundAt = clock.Elapsed.TotalSeconds;
        }

        /// ONI_FETCH_STATS=1, once the colony's items are in: the mirror's managed size, as the
        /// live heap after a full collection with it built afresh less the heap without it.
        static void MeasureMirror(FetchManager manager)
        {
            measured = true;
            groups.Clear();
            byItem.Clear();
            byItem.TrimExcess();
            long without = Live();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Bind(manager);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            long with = Live();
            int items = 0, keys = 0;
            foreach (var gm in groups.Values) { items += gm.ByHandle.Count; keys += gm.KeyList.Count; }
            global::Debug.Log($"FETCHSTATS mirror: {groups.Count} groups, {items} items, {keys} keys: {(with - without) / 1024} KiB live, {allocated / 1024} KiB allocated building it");
        }

        static long Live()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            return GC.GetTotalMemory(true);
        }

        static bool measured;

        /// A group's mirror from its fetchables.
        static void Build(GroupMirror gm)
        {
            foreach (var item in gm.ByHandle.Values)
                if (byItem.TryGetValue(item.P, out var cur) && ReferenceEquals(cur, item)) byItem.Remove(item.P);
            gm.ByHandle.Clear();
            gm.Keys.Clear();
            gm.KeyList.Clear();
            gm.RotItems.Clear();
            gm.Cells.Clear();
            Array.Clear(gm.Interior, 0, gm.Interior.Length);
            gm.Outside = 0;
            gm.Unsafe = false;
            var group = gm.Group;
            var data = group.fetchables.GetDataList();
            var hv = handles(group.fetchables);
            var slots = dataHandleIndices(group.fetchables);
            for (int i = 0; i < data.Count; i++) Insert(gm, data[i], HandleFor(hv, slots[i]));
        }

        static HandleVector<int>.Handle HandleFor(HandleVector<int> hv, int slot)
        {
            var handle = HandleVector<int>.InvalidHandle;
            handle.index = (versions(hv)[slot] << 24) | slot;
            return handle;
        }

        static void Insert(GroupMirror gm, Fetchable data, HandleVector<int>.Handle handle)
        {
            var p = data.pickupable;
            var item = new Item { P = p, G = gm, Handle = handle, Cell = p.cachedCell, Tag = data.tagBitsHash, Prio = data.masterPriority, Fresh = data.freshness, Qual = data.foodQuality };
            item.Rot = rotUpdaters(gm.Group).ContainsKey(handle);
            item.Special = IsSpecial(p);
            if (gm.ByHandle.ContainsKey(handle.index)) gm.Unsafe = true;
            gm.ByHandle[handle.index] = item;
            if (byItem.TryGetValue(p, out var other) && !ReferenceEquals(other, item))
            {
                gm.Unsafe = true;
                other.G.Unsafe = true;
            }
            byItem[p] = item;
            if (item.Rot)
            {
                item.RotPos = gm.RotItems.Count;
                gm.RotItems.Add(item);
            }
            SeeRadius(p);
            item.Source = SourceOf(p, item);
            Join(item);
            Count(item, +1);
        }

        static bool IsSpecial(Pickupable p)
        {
            var list = reservations(p);
            if (list != null && list.Count > 0) return true;
            var storage = p.storage;
            return storage != null && storage.automatable != null;
        }

        static void Join(Item item)
        {
            var gm = item.G;
            var id = (item.Tag, item.Prio, item.Cell);
            if (!gm.Keys.TryGetValue(id, out var key))
            {
                key = new Key { Tag = item.Tag, Prio = item.Prio, Cell = item.Cell, Pos = gm.KeyList.Count };
                gm.Keys[id] = key;
                gm.KeyList.Add(key);
            }
            item.Key = key;
            if (!gm.Cells.TryGetValue(item.Cell, out var sources)) gm.Cells[item.Cell] = sources = new CellSources();
            sources.Add(item.Source);
            var list = item.Special ? key.Specials : key.Members;
            item.Pos = list.Count;
            list.Add(item);
            if (!item.Special && !key.Dirty && (key.Best == null || Better(item, key.Best))) key.Best = item;
        }

        static void Leave(Item item)
        {
            var key = item.Key;
            if (key == null) return;
            var list = item.Special ? key.Specials : key.Members;
            int last = list.Count - 1;
            if (item.Pos != last)
            {
                list[item.Pos] = list[last];
                list[item.Pos].Pos = item.Pos;
            }
            list.RemoveAt(last);
            if (ReferenceEquals(key.Best, item))
            {
                key.Best = null;
                key.Dirty = true;
            }
            item.Key = null;
            item.Pos = -1;
            if (item.G.Cells.TryGetValue(key.Cell, out var sources))
            {
                sources.Remove(item.Source);
                if (sources.Count == 0) item.G.Cells.Remove(key.Cell);
            }
            if (key.Members.Count == 0 && key.Specials.Count == 0)
            {
                var gm = item.G;
                gm.Keys.Remove((key.Tag, key.Prio, key.Cell));
                int lastKey = gm.KeyList.Count - 1;
                if (key.Pos != lastKey)
                {
                    gm.KeyList[key.Pos] = gm.KeyList[lastKey];
                    gm.KeyList[key.Pos].Pos = key.Pos;
                }
                gm.KeyList.RemoveAt(lastKey);
            }
        }

        static void Count(Item item, int delta)
        {
            var gm = item.G;
            if (delta > 0)
            {
                int w = Class(item.Cell);
                item.Class = w;
                if (w < 0) gm.Outside++;
                else
                {
                    if (w >= gm.Interior.Length) Array.Resize(ref gm.Interior, 256);
                    gm.Interior[w]++;
                }
            }
            else
            {
                if (item.Class == -1) gm.Outside--;
                else if (item.Class >= 0) gm.Interior[item.Class]--;
                item.Class = -2;
            }
        }

        /// Every item's world class again (the radius or the worlds changed).
        static void Recount()
        {
            needRecount = false;
            foreach (var gm in groups.Values)
            {
                Array.Clear(gm.Interior, 0, gm.Interior.Length);
                gm.Outside = 0;
                foreach (var item in gm.ByHandle.Values)
                {
                    item.Class = -2;
                    Count(item, +1);
                }
            }
        }

        static bool OnMain()
        {
            if (MainThread.Is) return true;
            offThread = true;
            return false;
        }

        static void Added(Group __instance, Pickupable pickupable, HandleVector<int>.Handle __result)
        {
            if (boundTo == null || !OnMain()) return;
            var gm = Mirror(__instance);
            Insert(gm, __instance.fetchables.GetData(__result), __result);
            if (needRecount) Recount();
        }

        static void Removing(Group __instance, HandleVector<int>.Handle fetchable_handle)
        {
            if (boundTo == null || !OnMain() || !groups.TryGetValue(__instance, out var gm)) return;
            if (!gm.ByHandle.TryGetValue(fetchable_handle.index, out var item)) return;
            gm.ByHandle.Remove(fetchable_handle.index);
            if (byItem.TryGetValue(item.P, out var cur) && ReferenceEquals(cur, item)) byItem.Remove(item.P);
            if (item.RotPos >= 0)
            {
                var rot = gm.RotItems;
                int last = rot.Count - 1;
                rot[item.RotPos] = rot[last];
                rot[item.RotPos].RotPos = item.RotPos;
                rot.RemoveAt(last);
                item.RotPos = -1;
            }
            Leave(item);
            Count(item, -1);
        }

        /// UpdateStorage, UpdateTags: the item's priority or tag bits, and with its storage whether
        /// it is special.
        static void DataChanged(Group __instance, HandleVector<int>.Handle fetchable_handle)
        {
            if (boundTo == null || !OnMain() || !groups.TryGetValue(__instance, out var gm)) return;
            if (!gm.ByHandle.TryGetValue(fetchable_handle.index, out var item)) return;
            var data = __instance.fetchables.GetData(fetchable_handle);
            bool special = IsSpecial(item.P);
            if (data.tagBitsHash == item.Tag && data.masterPriority == item.Prio && special == item.Special) return;
            Leave(item);
            item.Tag = data.tagBitsHash;
            item.Prio = data.masterPriority;
            item.Special = special;
            Join(item);
        }

        static void Freshened(Group __instance)
        {
            if (boundTo == null || !OnMain() || !groups.TryGetValue(__instance, out var gm)) return;
            foreach (var item in gm.RotItems)
            {
                int fresh = __instance.fetchables.GetData(item.Handle).freshness;
                if (fresh == item.Fresh) continue;
                item.Fresh = fresh;
                if (item.Key != null) item.Key.Dirty = true;
            }
        }

        static void CellChanged(Pickupable __instance)
        {
            if (boundTo == null || byItem.Count == 0 || !byItem.TryGetValue(__instance, out var item)) return;
            if (!OnMain()) return;
            int cell = __instance.cachedCell;
            if (cell == item.Cell) return;
            Leave(item);
            Count(item, -1);
            item.Cell = cell;
            Join(item);
            Count(item, +1);
        }

        static void ReservationsChanged(Pickupable __instance)
        {
            if (boundTo == null || !byItem.TryGetValue(__instance, out var item)) return;
            if (!OnMain()) return;
            bool special = IsSpecial(__instance);
            if (special == item.Special) return;
            Leave(item);
            item.Special = special;
            Join(item);
        }

        static void TrackerChanged(Workable __instance)
        {
            if (boundTo == null || !(__instance is Pickupable p) || !byItem.TryGetValue(p, out var item)) return;
            SeeRadius(p);
            if (OnMain())
            {
                var source = SourceOf(p, item);
                if (!Equals(source, item.Source) && item.Key != null)
                {
                    Leave(item);
                    item.Source = source;
                    Join(item);
                }
                else item.Source = source;
            }
            if (needRecount && OnMain()) Recount();
        }

        /// Every few seconds: the mirror against the groups' own data. Drift is logged and the
        /// group rebuilt.
        static void Resync(FetchManager manager)
        {
            offThread = false;
            long sw = Stopwatch.GetTimestamp();
            int missing = 0, extra = 0, fields = 0, cells = 0, special = 0, sources = 0, unsafeGroups = 0, rebuilt = 0, keyDrift = 0;
            var seen = new HashSet<Group>();
            foreach (var group in manager.prefabIdToFetchables.Values)
            {
                seen.Add(group);
                var gm = Mirror(group);
                var data = group.fetchables.GetDataList();
                var hv = handles(group.fetchables);
                var slots = dataHandleIndices(group.fetchables);
                bool drift = gm.Unsafe || gm.ByHandle.Count != data.Count;
                if (gm.ByHandle.Count > data.Count) extra += gm.ByHandle.Count - data.Count;
                for (int i = 0; i < data.Count; i++)
                {
                    var d = data[i];
                    var handle = HandleFor(hv, slots[i]);
                    if (!gm.ByHandle.TryGetValue(handle.index, out var item) || !ReferenceEquals(item.P, d.pickupable)) { missing++; drift = true; continue; }
                    if (item.Tag != d.tagBitsHash || item.Prio != d.masterPriority || item.Fresh != d.freshness || item.Qual != d.foodQuality) { fields++; drift = true; }
                    if (item.Cell != d.pickupable.cachedCell) { cells++; drift = true; }
                    if (item.Special != IsSpecial(d.pickupable)) { special++; drift = true; }
                    if (!Equals(item.Source, SourceOf(d.pickupable, item))) { sources++; drift = true; }
                    if (item.Key == null || item.Key.Cell != item.Cell || item.Key.Tag != item.Tag || item.Key.Prio != item.Prio) { keyDrift++; drift = true; }
                }
                if (drift)
                {
                    Build(gm);
                    rebuilt++;
                    if (gm.Unsafe) unsafeGroups++;
                }
            }
            var gone = new List<Group>();
            foreach (var group in groups.Keys) if (!seen.Contains(group)) gone.Add(group);
            foreach (var group in gone)
            {
                foreach (var item in groups[group].ByHandle.Values)
                    if (byItem.TryGetValue(item.P, out var cur) && ReferenceEquals(cur, item)) byItem.Remove(item.P);
                groups.Remove(group);
            }
            // The worlds' rectangles against Grid.WorldIdx again, and every item's class.
            layout = 0;
            CheckLayout();
            Recount();
            double ms = (Stopwatch.GetTimestamp() - sw) * 1000.0 / Stopwatch.Frequency;
            resyncs++;
            int drifted = missing + extra + fields + cells + special + sources + keyDrift;
            mismatches += drifted;
            if (drifted > 0 || (verify && resyncs % 12 == 1))
            {
                int items = 0, keys = 0, specials = 0;
                foreach (var g in groups.Values) { items += g.ByHandle.Count; keys += g.KeyList.Count; foreach (var k in g.KeyList) specials += k.Specials.Count; }
                global::Debug.Log($"{Name}: resync {resyncs}, {ms:F2} ms: {groups.Count} groups, {items} items, {keys} keys, {specials} special; drift {missing} missing, {extra} extra, {fields} fields, {cells} cells, {special} special, {sources} work-spot sources, {keyDrift} keys{(drifted > 0 ? $" ({mismatches})" : "")}; {rebuilt} groups rebuilt ({unsafeGroups} unsafe), {gone.Count} gone; radius {radius}");
            }
        }

        static long resyncs;

        // ---- Stats ----

        static void StatsMainPre()
        {
            mainStart = Stopwatch.GetTimestamp();
        }

        static void StatsMainPost()
        {
            mainTicks += Stopwatch.GetTimestamp() - mainStart;
            calls++;
            int frame = UnityEngine.Time.frameCount;
            double now = clock.Elapsed.TotalSeconds;
            if (windowStartFrame < 0)
            {
                windowStartFrame = frame;
                windowStartTime = now;
                mainTicks = workerTicks = calls = 0;
                return;
            }
            int frames = frame - windowStartFrame;
            if (frames < 600) return;
            double f = Stopwatch.Frequency / 1000.0;
            global::Debug.Log($"FETCHSTATS f{frame}: {calls / (double)frames:F2} calls a frame; main wall {mainTicks / f / frames:F3} ms a frame ({mainTicks / f / Math.Max(1, calls):F3} a call); "
                + $"group items {Interlocked.Read(ref workerTicks) / f / frames:F3} ms a frame ({Interlocked.Read(ref workerTicks) / f / Math.Max(1, calls):F3} a call); frame {(now - windowStartTime) * 1000 / frames:F2} ms; managed heap {GC.GetTotalMemory(false) >> 20} MiB");
            windowStartFrame = frame;
            windowStartTime = now;
            mainTicks = calls = 0;
            Interlocked.Exchange(ref workerTicks, 0);
        }

        static void StatsItemPre() => itemStart = Stopwatch.GetTimestamp();

        static void StatsItemPost() => Interlocked.Add(ref workerTicks, Stopwatch.GetTimestamp() - itemStart);
    }
}
