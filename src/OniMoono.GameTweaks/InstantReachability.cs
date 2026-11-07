using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    using Instance = ReachabilityMonitor.Instance;

    /// Game tweak `instant-reachability` (on by default, **changes timing**). ReachabilityMonitor
    /// polls every workable's reachability once a game second, spread over the frames:
    /// MinionGroupProber.IsAllReachable(its cell, its work offsets), which reads the Duplicants'
    /// reachable-cell counts (MinionGroupProber.cells). A large colony has tens of thousands of
    /// monitored workables, mostly items in storage, so the poll costs about 2 ms a frame; and a
    /// change (a door, a dug tile) is seen up to a second after the counts changed.
    ///
    /// With the tweak an answer can change only when one of its inputs changes, and each input's
    /// writer tells it:
    /// - the counts: every write path (Occupy, OccupyST, Vacate, VacateST) records the cells
    ///   whose count crosses 0 ↔ 1, race-free on workers (ReachCrossings);
    /// - the object's cell: Pickupable.GetCell is its cachedCell, written only by
    ///   UpdateCachedCell; Diggable's is fixed at spawn; the other monitored objects' (storages,
    ///   constructions, mop and move markers, rocket modules) is their transform's, followed
    ///   through the engine's per-transform move flag (EngineMoves), set only on these objects:
    ///   any change of their place, SetPosition or a raw position set, theirs or an ancestor's;
    /// - the offsets: a tracker's offsets go stale through OffsetTableTracker.OnCellChanged (the
    ///   game's solid and valid-nav-cell partitioner layers) or ForceRefresh, and change through
    ///   Workable.SetOffsets and SetOffsetTable (a new tracker). Unsealable's are its facing,
    ///   which only a template or a save sets, before spawn.
    ///
    /// Each monitored object is kept in a per-cell index (ReachIndex) with the rectangle of the
    /// offsets its tracker can ever give (fixed per table), so offset re-evaluation never moves it
    /// and only a cell change does. Once a frame, at the end of Game.Update (after the frame's
    /// path probe results are taken, Navigator.TakeResult in AsyncPathProber.TickFrame), the
    /// crossed cells are drained, the objects whose reads include them and the objects with a
    /// changed cell or offsets are rechecked with the game's own UpdateReachability, so the
    /// Reachable tag and the ReachableChanged event come from the game's state machine as they
    /// do when it polls. Load does what it did (each monitor checks at construction, and
    /// WorldInventory rechecks every item a frame after load).
    ///
    /// An object the tweak can't follow keeps the game's polling: a type overriding GetCell or
    /// GetOffsets with code it doesn't know (mods), or a tracker of a type it doesn't know. The
    /// others are taken out of the poll's bucket. One whose table tracker answers offsets outside
    /// the rectangle of its rows' first cells (a mod's postfix on the private
    /// OffsetTableTracker.UpdateOffsets(int, CellOffset[][]) or IsValidRow, which the tweak does
    /// not guard) goes back to the poll: its offsets are checked against its bounds when it
    /// registers and after each recheck that follows a change of its offsets' inputs. A second
    /// MinionGroupProber in one session (only a mod would make one) hands every object back to
    /// the poll too.
    ///
    /// What changes: an answer comes on the frame its input changed, instead of at the object's
    /// next poll (up to a game second later); a Duplicant can pick up an errand that much sooner.
    /// The answers themselves are the game's.
    ///
    /// Stands down for the session (logged once) when another Harmony owner patches what it
    /// relies on (Guarded: the monitor's states and update, the prober's table, the offsets and
    /// cells); Fast Track's FastReachability replaces ReachabilityMonitor.InitializeStates.
    ///
    /// ONI_TWEAK_VERIFY=1 compares, every frame after the drain, a slice of the event-driven
    /// objects (all of them every few frames) with a fresh IsAllReachable of their GetCell and
    /// GetOffsets, and logs each mismatch and totals every 30 s.
    public static class InstantReachability
    {
        public const string Name = "instant-reachability";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        /// Objects verified a frame (ONI_REACH_VERIFY_SLICE, default 4,000).
        static readonly int verifySlice = int.TryParse(Environment.GetEnvironmentVariable("ONI_REACH_VERIFY_SLICE"), out var s) && s > 0 ? s : 4000;

        static readonly AccessTools.FieldRef<Workable, OffsetTracker> offsetTracker = AccessTools.FieldRefAccess<Workable, OffsetTracker>("offsetTracker");
        // U60: a tracker's offsets are a List (StandardOffsetTracker copies its offsets into it).
        static readonly AccessTools.FieldRef<OffsetTracker, List<CellOffset>> trackerOffsets = AccessTools.FieldRefAccess<OffsetTracker, List<CellOffset>>("offsets");
        static readonly AccessTools.FieldRef<OffsetTableTracker, CellOffset[][]> trackerTable = AccessTools.FieldRefAccess<OffsetTableTracker, CellOffset[][]>("table");
        static readonly AccessTools.FieldRef<OffsetTableTracker, KMonoBehaviour> tableOwner = AccessTools.FieldRefAccess<OffsetTableTracker, KMonoBehaviour>("cmp");
        static readonly AccessTools.FieldRef<MinionGroupProber, int[]> proberCells = AccessTools.FieldRefAccess<MinionGroupProber, int[]>("cells");

        sealed class Entry : IRosterItem
        {
            public int RosterIndex { get; set; } = -1;
            public Instance Smi;
            public Workable Master;
            public int Slot = -1;
            public ReachCoverage.CellSource CellSource;
            public bool Watched;
            public bool Removed;
            /// Scheduled since its last recheck: its offsets may have been set anew.
            public bool CheckBounds;
            /// Its index bounds, kept here too: the check after a recheck reads the entry the
            /// drain just read, not the index's array.
            public OffsetBounds Bounds;
        }

        enum State { Unchecked, On, Off }
        static State state;

        // The session's (one MinionGroupProber's) data.
        static ReachCrossings crossings;
        static ReachIndex index;
        static MinionGroupProber boundTo;
        static readonly Dictionary<Workable, Entry> byMaster = new Dictionary<Workable, Entry>();
        static readonly ReachRoster<Entry> covered = new ReachRoster<Entry>();
        /// The objects to recheck at the next drain (a cell, an offset or a tracker changed).
        static readonly List<Entry> pending = new List<Entry>();
        static readonly List<int> gathered = new List<int>();
        static UpdateBucketWithUpdater<Instance> parked;
        static int parkedRemovals;
        static int updateIndex = -1;
        static object updater;
        static ReachabilityMonitor monitor;

        /// For tests (E2eDriver's state reads, `type:OniMoono.GameTweaks.InstantReachability::Covered`).
        public static bool Running => state == State.On;
        public static int Covered => index?.Count ?? 0;
        public static int Polled { get; private set; }
        public static long Crossings => crossings?.Recorded ?? 0;
        public static long Notified { get; private set; }
        public static long Rechecks { get; private set; }
        public static long Moves { get; private set; }
        /// The engine's move flags' notices (a watched transform or an ancestor moved).
        public static long TransformNotices { get; private set; }
        public static long Checks { get; private set; }
        public static long Mismatches { get; private set; }
        /// Objects handed back to the poll for offsets outside their bounds.
        public static long OutOfBounds { get; private set; }
        public static long Drains { get; private set; }
        public static double DrainMs => drainTicks * 1000.0 / Stopwatch.Frequency;
        static long drainTicks;
        static readonly Stopwatch since = Stopwatch.StartNew();
        static int verifyCursor;

        public static void Install(Harmony harmony)
        {
            var self = typeof(InstantReachability);
            HarmonyMethod M(string name) => new HarmonyMethod(self, name);
            harmony.Patch(AccessTools.Method(typeof(ReachabilityMonitor), nameof(ReachabilityMonitor.InitializeStates)), postfix: M(nameof(InitializeStatesPost)));
            harmony.Patch(AccessTools.Method(typeof(MinionGroupProber), "OnPrefabInit"), postfix: M(nameof(ProberInit)));
            harmony.Patch(AccessTools.Method(typeof(MinionGroupProber), nameof(MinionGroupProber.Occupy), new[] { typeof(List<int>) }), prefix: M(nameof(OccupyList)));
            harmony.Patch(AccessTools.Method(typeof(MinionGroupProber), nameof(MinionGroupProber.Occupy), new[] { typeof(int) }), prefix: M(nameof(OccupyCell)));
            harmony.Patch(AccessTools.Method(typeof(MinionGroupProber), nameof(MinionGroupProber.OccupyST)), prefix: M(nameof(OccupyListST)));
            harmony.Patch(AccessTools.Method(typeof(MinionGroupProber), nameof(MinionGroupProber.Vacate), new[] { typeof(List<int>) }), prefix: M(nameof(VacateList)));
            harmony.Patch(AccessTools.Method(typeof(MinionGroupProber), nameof(MinionGroupProber.Vacate), new[] { typeof(int) }), prefix: M(nameof(VacateCell)));
            harmony.Patch(AccessTools.Method(typeof(MinionGroupProber), nameof(MinionGroupProber.VacateST)), prefix: M(nameof(VacateListST)));
            harmony.Patch(AccessTools.Method(typeof(Pickupable), nameof(Pickupable.UpdateCachedCell)), postfix: M(nameof(CachedCellPost)));
            harmony.Patch(AccessTools.Method(typeof(Workable), nameof(Workable.SetOffsets)), postfix: M(nameof(TrackerReplaced)));
            harmony.Patch(AccessTools.Method(typeof(Workable), nameof(Workable.SetOffsetTable)), postfix: M(nameof(TrackerReplaced)));
            harmony.Patch(AccessTools.Method(typeof(OffsetTableTracker), "OnCellChanged"), postfix: M(nameof(TableStale)));
            harmony.Patch(AccessTools.Method(typeof(OffsetTracker), nameof(OffsetTracker.ForceRefresh)), postfix: M(nameof(TableStale)));
            harmony.Patch(AccessTools.Method(typeof(Game), "Update"), postfix: M(nameof(GameUpdatePost)));
            EngineMoves.Bind();
        }

        // ---- Standing down ----

        /// The methods another owner's patch makes this tweak stand down.
        internal static IEnumerable<MethodBase> Guarded()
        {
            yield return AccessTools.Method(typeof(ReachabilityMonitor), nameof(ReachabilityMonitor.InitializeStates));
            yield return AccessTools.Method(typeof(Instance), nameof(Instance.UpdateReachability));
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(MinionGroupProber)))
                if (m.Name is "IsReachable" or "IsAllReachable" or "Occupy" or "OccupyST" or "Vacate" or "VacateST") yield return m;
            yield return AccessTools.Method(typeof(Workable), nameof(Workable.GetCell));
            yield return AccessTools.Method(typeof(Workable), nameof(Workable.GetOffsets), new[] { typeof(int) });
            yield return AccessTools.Method(typeof(Workable), nameof(Workable.SetOffsets));
            yield return AccessTools.Method(typeof(Workable), nameof(Workable.SetOffsetTable));
            yield return AccessTools.Method(typeof(Pickupable), nameof(Pickupable.GetCell));
            yield return AccessTools.Method(typeof(Pickupable), nameof(Pickupable.UpdateCachedCell));
            yield return AccessTools.Method(typeof(Diggable), nameof(Diggable.GetCell));
            yield return AccessTools.Method(typeof(Unsealable), nameof(Unsealable.GetOffsets), new[] { typeof(int) });
            yield return AccessTools.Method(typeof(OffsetTracker), nameof(OffsetTracker.GetOffsets));
            yield return AccessTools.Method(typeof(OffsetTableTracker), "UpdateOffsets", new[] { typeof(int) });
            yield return AccessTools.Method(typeof(OffsetTableTracker), "UpdateCell");
            yield return AccessTools.Method(typeof(OffsetTableTracker), "OnCellChanged");
        }

        static string Foreign()
        {
            var owners = new List<KeyValuePair<string, IEnumerable<string>>>();
            foreach (var m in Guarded())
            {
                if (m == null) return "a method it relies on was not found";
                var info = Harmony.GetPatchInfo(m);
                if (info != null) owners.Add(new KeyValuePair<string, IEnumerable<string>>($"{m.DeclaringType?.Name}.{m.Name}", info.Owners));
            }
            return ProbeRepairPolicy.Foreign(owners);
        }

        /// Once, when the monitor's states are made (the first monitored object of the session,
        /// with every mod's patches made).
        static void InitializeStatesPost(ReachabilityMonitor __instance)
        {
            if (state == State.Unchecked)
            {
                string why;
                try { why = Foreign(); }
                catch (Exception e) { why = "its methods could not be read: " + e.Message; }
                if (why == null && !EngineMoves.Available) why = "the engine's transform move flags are missing";
                if (why != null)
                {
                    state = State.Off;
                    global::Debug.Log($"{Name}: off ({why}); the game's polling stays");
                    return;
                }
                state = State.On;
                global::Debug.Log($"{Name}: on");
            }
            if (state != State.On) return;
            monitor = __instance;
            var actions = __instance.root.updateActions;
            for (int i = 0; actions != null && i < actions.Count; i++)
                // By its updater, not its rate.
                if (actions[i].updater != null && actions[i].updater.GetType().Name == "UpdateReachabilityCB")
                {
                    updateIndex = i;
                    updater = actions[i].updater;
                }
            if (updateIndex < 0)
            {
                state = State.Off;
                global::Debug.Log($"{Name}: off (ReachabilityMonitor's update not found); the game's polling stays");
                return;
            }
            __instance.root.Enter(Name, Register);
            __instance.root.Exit(Name, Unregister);
        }

        // ---- The table's writers ----

        static void ProberInit(MinionGroupProber __instance)
        {
            if (state == State.Off) return;
            // A second table in this session (a mod's): the objects still monitored go back to the
            // game's polling, from the old parked bucket. A new session's are gone already.
            int handed = ReachSession.HandBack(covered.Items,
                e => !e.Removed && parked != null && monitor != null && e.Master != null && e.Smi.IsRunning(),
                e => Unpark(e.Smi),
                e => e.Removed = true,
                (e, ex) => global::Debug.LogWarning($"{Name}: {e.Master?.name} not handed back to the game's polling: {ex}"));
            if (handed > 0) global::Debug.Log($"{Name}: a new MinionGroupProber; {handed} objects back to the game's polling");
            boundTo = __instance;
            crossings = new ReachCrossings(Grid.CellCount);
            index = new ReachIndex(Grid.CellCount, Grid.WidthInCells);
            byMaster.Clear();
            pending.Clear();
            Polled = handed;
            parked = new UpdateBucketWithUpdater<Instance>(Name + " (event-driven)");
            parkedRemovals = 0;
            EngineMoves.Clear();
        }

        static bool Active(MinionGroupProber prober) => state != State.Off && ReferenceEquals(prober, boundTo) && crossings != null;

        static bool OccupyList(MinionGroupProber __instance, List<int> cells)
        {
            if (!Active(__instance)) return true;
            var table = proberCells(__instance);
            foreach (int cell in cells) crossings.Increment(table, cell);
            return false;
        }

        static bool OccupyCell(MinionGroupProber __instance, int cell)
        {
            if (!Active(__instance)) return true;
            crossings.Increment(proberCells(__instance), cell);
            return false;
        }

        static bool OccupyListST(MinionGroupProber __instance, List<int> cells)
        {
            if (!Active(__instance)) return true;
            var table = proberCells(__instance);
            foreach (int cell in cells) crossings.IncrementST(table, cell);
            return false;
        }

        static bool VacateList(MinionGroupProber __instance, List<int> cells)
        {
            if (!Active(__instance)) return true;
            var table = proberCells(__instance);
            foreach (int cell in cells) crossings.Decrement(table, cell);
            return false;
        }

        static bool VacateCell(MinionGroupProber __instance, int cell)
        {
            if (!Active(__instance)) return true;
            crossings.Decrement(proberCells(__instance), cell);
            return false;
        }

        static bool VacateListST(MinionGroupProber __instance, List<int> cells)
        {
            if (!Active(__instance)) return true;
            var table = proberCells(__instance);
            foreach (int cell in cells) crossings.DecrementST(table, cell);
            return false;
        }

        // ---- Monitored objects ----

        static readonly Dictionary<Type, (ReachCoverage.CellSource Cell, ReachCoverage.OffsetSource Offsets)> sources =
            new Dictionary<Type, (ReachCoverage.CellSource, ReachCoverage.OffsetSource)>();

        static (ReachCoverage.CellSource Cell, ReachCoverage.OffsetSource Offsets) SourcesOf(Type type)
        {
            if (sources.TryGetValue(type, out var found)) return found;
            var getCell = type.GetMethod(nameof(Workable.GetCell), BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var getOffsets = type.GetMethod(nameof(Workable.GetOffsets), BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            // Only the game's own types count as known declarers (a mod's class named the same doesn't).
            string Declarer(MethodInfo m) => m?.DeclaringType?.Assembly == typeof(Workable).Assembly ? m.DeclaringType.Name : null;
            found = (ReachCoverage.Cell(Declarer(getCell)), ReachCoverage.Offsets(Declarer(getOffsets)));
            sources[type] = found;
            return found;
        }

        static string TrackerName(OffsetTracker t) => t == null ? null : t.GetType().Assembly == typeof(Workable).Assembly ? t.GetType().Name : "(mod)";

        /// The rectangle of offsets the master can ever answer, or null when the tweak can't follow them.
        static OffsetBounds? BoundsOf(Workable master, ReachCoverage.OffsetSource offsets)
        {
            var b = OffsetBounds.Origin;
            if (offsets == ReachCoverage.OffsetSource.UnsealableFacing)
            {
                foreach (var o in OffsetGroups.LeftOnly) b.Include(o.x, o.y);
                foreach (var o in OffsetGroups.RightOnly) b.Include(o.x, o.y);
                return b;
            }
            var tracker = offsetTracker(master);
            switch (ReachCoverage.Tracker(TrackerName(tracker)))
            {
                case ReachCoverage.TrackerKind.None:
                    return b; // GetOffsets makes a StandardOffsetTracker of (0, 0)
                case ReachCoverage.TrackerKind.Standard:
                    var list = trackerOffsets(tracker);
                    if (list != null) foreach (var o in list) b.Include(o.x, o.y);
                    return b;
                case ReachCoverage.TrackerKind.Table:
                    var table = trackerTable((OffsetTableTracker)tracker);
                    if (table != null)
                        foreach (var row in table)
                            if (row != null && row.Length > 0) b.Include(row[0].x, row[0].y);
                    return b;
                default:
                    return null;
            }
        }

        /// The root state's enter, after the game added the instance to its poll's bucket.
        static void Register(Instance smi)
        {
            try
            {
                if (state != State.On || index == null || smi.master == null || !ReferenceEquals(smi.sm, monitor)) { Polled++; return; }
                var master = smi.master;
                if (byMaster.ContainsKey(master)) { Polled++; return; } // a second monitor of one workable: poll it
                var (cellSource, offsetSource) = SourcesOf(master.GetType());
                var b = cellSource == ReachCoverage.CellSource.Unknown ? null : BoundsOf(master, offsetSource);
                if (b == null) { Polled++; return; }
                if (!InBounds(master, b.Value)) { Polled++; OffsetsOutside(master); return; }
                var entry = new Entry { Smi = smi, Master = master, CellSource = cellSource, Bounds = b.Value };
                entry.Slot = index.Add(entry, master.GetCell(), b.Value);
                byMaster[master] = entry;
                covered.Add(entry);
                if (cellSource == ReachCoverage.CellSource.Transform)
                {
                    entry.Watched = EngineMoves.Watch(master.transform, entry);
                }
                Park(smi);
                // Its answer is current: the constructor asked the game, and nothing ran since.
            }
            catch (Exception e)
            {
                global::Debug.LogWarning($"{Name}: {smi.master?.name} left to the game's polling: {e}");
            }
        }

        /// The root state's exit, after the game took the instance out of whichever bucket holds it.
        static void Unregister(Instance smi)
        {
            var master = smi.master;
            if (master == null || !byMaster.TryGetValue(master, out var entry) || !ReferenceEquals(entry.Smi, smi))
            {
                if (state == State.On && index != null && Polled > 0) Polled--;
                return;
            }
            Forget(entry);
            if (++parkedRemovals >= 4096 && parked != null)
            {
                parkedRemovals = 0;
                parked.Update(0f); // frees the removed entries; none has an updater to call
            }
        }

        static void Forget(Entry entry)
        {
            entry.Removed = true;
            byMaster.Remove(entry.Master);
            index.Remove(entry.Slot);
            covered.Remove(entry);
            if (entry.Watched && entry.Master != null) EngineMoves.Unwatch(entry.Master.transform);
            entry.Watched = false;
        }

        /// Out of the poll: the update table's entry points into a bucket that never updates.
        static void Park(Instance smi)
        {
            ref var slot = ref smi.updateTable[monitor.root.updateActions[updateIndex].updateTableIdx];
            if (ReferenceEquals(slot.bucket, parked)) return;
            slot.bucket.Remove(slot.handle);
            slot.bucket = parked;
            slot.handle = parked.Add(smi, 0f, null);
        }

        /// Back into the poll, as the state machine's enter would add it (the next bucket in turn).
        static void Unpark(Instance smi)
        {
            var actions = monitor.root.updateActions;
            var action = actions[updateIndex];
            ref var slot = ref smi.updateTable[action.updateTableIdx];
            if (!ReferenceEquals(slot.bucket, parked)) return;
            parked.Remove(slot.handle);
            parkedRemovals++;
            var bucket = (UpdateBucketWithUpdater<Instance>)action.buckets[action.nextBucketIdx];
            action.nextBucketIdx = (action.nextBucketIdx + 1) % action.buckets.Length;
            actions[updateIndex] = action;
            slot.bucket = bucket;
            slot.handle = bucket.Add(smi, Singleton<StateMachineUpdater>.Instance.GetFrameTime(action.updateRate, bucket.frame),
                (UpdateBucketWithUpdater<Instance>.IUpdater)updater);
        }

        /// A parked object back to the game's polling.
        static void ToPolling(Entry entry)
        {
            var smi = entry.Smi;
            Forget(entry);
            Unpark(smi);
            Polled++;
        }

        /// Whether the object's current offsets lie inside its indexed bounds. A table tracker's
        /// are its rows' first cells, unless a mod's patch adds others. They are set anew only
        /// after they were invalidated (OnCellChanged, ForceRefresh) or the tracker's cell or the
        /// tracker changed, each of which schedules a recheck: the check runs after those
        /// rechecks (whose GetOffsets just set them), not after a crossing's, which would read
        /// offsets IsAllReachable mostly never touches.
        static bool InBounds(Workable master, OffsetBounds b)
        {
            if (!(offsetTracker(master) is OffsetTableTracker tracker)) return true;
            var offsets = trackerOffsets(tracker);
            if (offsets == null) return true;
            for (int i = 0; i < offsets.Count; i++)
                if (!b.Contains(offsets[i].x, offsets[i].y)) return false;
            return true;
        }

        static readonly HashSet<Type> outsideLogged = new HashSet<Type>();

        static void OffsetsOutside(Workable master)
        {
            OutOfBounds++;
            if (outsideLogged.Add(master.GetType()))
                global::Debug.Log($"{Name}: a {master.GetType().Name} ({master.name}) left to the game's polling: its offsets go outside its offset table's "
                    + "(a mod's patch of OffsetTableTracker.UpdateOffsets or IsValidRow); others of its type will be too, unlogged");
        }

        static void Schedule(Entry entry)
        {
            if (entry.Removed) return;
            entry.CheckBounds = true;
            pending.Add(entry);
        }

        // ---- Their inputs ----

        static void CachedCellPost(Pickupable __instance, int cell)
        {
            if (state != State.On || !byMaster.TryGetValue(__instance, out var entry)) return;
            Moved(entry, cell);
        }

        /// The engine's flag: a watched transform (or an ancestor) moved, by any means.
        internal static void TransformMoved(object data)
        {
            var entry = (Entry)data;
            if (state != State.On || entry.Removed || entry.Master == null) return;
            TransformNotices++;
            Moved(entry, entry.Master.GetCell());
        }

        static void Moved(Entry entry, int cell)
        {
            if (index.CellOf(entry.Slot) == cell) return;
            Moves++;
            index.Move(entry.Slot, cell);
            Schedule(entry);
        }

        static void TrackerReplaced(Workable __instance)
        {
            if (state != State.On || !byMaster.TryGetValue(__instance, out var entry)) return;
            var b = BoundsOf(entry.Master, SourcesOf(entry.Master.GetType()).Offsets);
            if (b == null)
            {
                // A tracker it doesn't know: back to the game's polling.
                var smi = entry.Smi;
                ToPolling(entry);
                smi.UpdateReachability();
                return;
            }
            index.SetBounds(entry.Slot, b.Value);
            entry.Bounds = b.Value;
            Schedule(entry);
        }

        static void TableStale(OffsetTracker __instance)
        {
            if (state != State.On || !(__instance is OffsetTableTracker table)) return;
            if (tableOwner(table) is Workable owner && byMaster.TryGetValue(owner, out var entry)) Schedule(entry);
        }

        // ---- Once a frame ----

        static readonly List<Entry> batch = new List<Entry>();

        static void GameUpdatePost()
        {
            if (state != State.On || crossings == null || !ReferenceEquals(MinionGroupProber.Get(), boundTo) || boundTo == null) return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                EngineMoves.Drain();
                Drain();
            }
            catch (Exception e)
            {
                global::Debug.LogException(e);
            }
            drainTicks += Stopwatch.GetTimestamp() - start;
            Drains++;
            if (verify) Verify();
            if (since.ElapsedMilliseconds >= 30000)
            {
                since.Restart();
                string v = verify ? $", {Checks} checks, {Mismatches} mismatches" : "";
                global::Debug.Log($"{Name}: {Covered} objects answered by events ({EngineMoves.Count} transforms watched), {Polled} polled; {Crossings} cells crossed, {Notified} objects notified, "
                    + $"{Moves} moves, {Rechecks} rechecks in {Drains} frames ({(Drains > 0 ? DrainMs / Drains : 0):F3} ms a frame){v}");
            }
        }

        static void Drain()
        {
            // Rounds: a recheck's event handlers may move, re-offset or recheck other objects.
            for (int round = 0; round < 8; round++)
            {
                var cells = crossings.Take();
                if (cells.Count == 0 && pending.Count == 0) return;
                index.NewPass();
                gathered.Clear();
                foreach (int cell in cells) index.Gather(cell, gathered);
                Notified += gathered.Count;
                foreach (var entry in pending)
                    if (!entry.Removed && index.Mark(entry.Slot)) gathered.Add(entry.Slot);
                pending.Clear();
                batch.Clear();
                foreach (int slot in gathered) batch.Add((Entry)index.Owner(slot));
                foreach (var entry in batch)
                {
                    if (entry.Removed) continue;
                    Rechecks++;
                    entry.Smi.UpdateReachability();
                    if (entry.CheckBounds)
                    {
                        // Its offsets may be new: outside its bounds, the poll answers it.
                        entry.CheckBounds = false;
                        if (!entry.Removed && !InBounds(entry.Master, entry.Bounds))
                        {
                            var master = entry.Master;
                            ToPolling(entry);
                            OffsetsOutside(master);
                        }
                    }
                }
            }
        }

        static readonly List<string> firstMismatches = new List<string>();

        static void Verify()
        {
            if (covered.Count == 0) return;
            var prober = MinionGroupProber.Get();
            int n = Math.Min(verifySlice, covered.Count);
            for (int i = 0; i < n; i++)
            {
                if (verifyCursor >= covered.Count) verifyCursor = 0;
                var entry = covered[verifyCursor++];
                if (entry.Removed || entry.Master == null) continue;
                var smi = entry.Smi;
                int cell = entry.Master.GetCell();
                bool fresh = prober.IsAllReachable(cell, entry.Master.GetOffsets(cell));
                bool ours = smi.sm.isReachable.Get(smi);
                Checks++;
                if (fresh == ours) continue;
                Mismatches++;
                if (Mismatches <= 50)
                    global::Debug.Log($"{Name}: mismatch: {entry.Master.name} ({entry.Master.GetType().Name}, {entry.CellSource}) at cell {cell} (indexed at {index.CellOf(entry.Slot)}), "
                        + $"offsets [{string.Join(" ", System.Linq.Enumerable.Select(entry.Master.GetOffsets(cell), o => $"{o.x},{o.y}"))}] in bounds "
                        + $"{index.BoundsOf(entry.Slot).MinX}..{index.BoundsOf(entry.Slot).MaxX}, {index.BoundsOf(entry.Slot).MinY}..{index.BoundsOf(entry.Slot).MaxY}, "
                        + $"tracker {offsetTracker(entry.Master)?.GetType().Name}: answered {ours}, the table says {fresh} (frame {GameClock.Instance?.GetFrame() ?? 0}) ({Mismatches})");
                // Fix it, so one miss is counted once.
                smi.UpdateReachability();
            }
        }
    }
}
