using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace OniMoono.GameTweaks
{
    using WorkOrder = AsyncPathProber.WorkOrder;
    using WorkResult = AsyncPathProber.WorkResult;

    /// Game tweak `path-probe-repair` (on by default but not in `exact`: it **changes behaviour**
    /// in one way, between routes of the same cost a Duplicant may take another one). Every
    /// frame the game probes up to four
    /// navigators on its path probe thread (AsyncPathProber): a flood from where the Duplicant
    /// stands over the whole world's nav grid, its cost to every cell it can reach, which errands
    /// and Duplicants' pathing read until its next probe a few seconds later. A Duplicant's probe
    /// mostly finds what its last one found: in a large colony the thread spends 0.2–0.3 of a core
    /// recomputing it (U60, c100 and DeCrew).
    ///
    /// With the tweak a Duplicant's next probe from the same cell, nav type, path flags and
    /// abilities repairs its last result instead (ProbeEngine.Repair): from the nav grid's links
    /// that changed since (NavGrid.UpdateGraph, diffed against a copy of the rows) and the cells
    /// where the tables a link's evaluation reads changed (doors' permissions and the nav masks,
    /// liquid and solid as far as they make a cell submerged, tube entrances, suit markers, idle
    /// traversal, each through the game's writer), the states whose parent link changed or got
    /// worse are dropped with everything below them and found again from around them, and
    /// improvements spread as in a flood. Every other probe is a flood, the game's algorithm with
    /// its queue (ProbeEngine.Flood), which also keeps each state's path flags for the next
    /// repair. Costs, the reachable cells and each state's flags come out as a flood's, except
    /// where two routes cost the same: a flood keeps the first it finds, a repair the one it had
    /// or finds first, so BuildPath may return the other route of the same cost (and which cell's
    /// cheapest nav type a tie picks). Temperature and building checks are untouched; no probe is
    /// delayed.
    ///
    /// The repaired result is written into the navigator's own PathGrid on the main thread when
    /// the game takes the result (Navigator.TakeResult), so what the main thread reads between
    /// probes is never half repaired; a flood fills the pooled grid as the game's does. Each
    /// navigator also keeps four bits of path flags a state (ProbeFlags; a 640×404 world with 7
    /// nav types: 0.9 MB a Duplicant), and the probe thread a copy of the rows and a
    /// 25-byte-a-state scratch.
    ///
    /// Stands down for the session (logged once) when another Harmony owner patches the probe
    /// (PathProber.Run, WorkOrder.Execute, PathFinder.AddPotentials/AddPotential, PathGrid.SetCell),
    /// the result's hand-over (Navigator.TakeResult), the nav grid's update (NavGrid.UpdateGraph)
    /// or the rules it relies on (MinionPathFinderAbilities.TraversePath and
    /// GetSubmergedPathCostPenalty), or when the game's arrays don't answer as expected through
    /// PathGrid's own methods (BeginUpdate, GetCell and GetCost, whatever patches them). Fast
    /// Track's Cache Paths patches the first ones.
    ///
    /// ONI_TWEAK_VERIFY=1 floods after every repair too, and counts the repairs whose costs or
    /// reachable cells differ from the flood's: wrong when the repair is not what a flood can
    /// leave (ProbeEngine.Fixpoint), else parting at a tie (above). It also audits the change
    /// feed every frame (FeedAudit). ONI_PROBE_DIGEST=FILE writes a line per result
    /// taken (frame, navigator, root, nav type, flags, reachable cells, a digest of them and one
    /// with their costs); with ONI_PROBE_REPAIR_OFF=1 only that, of the game's own probes.
    /// ONI_PROBE_REPAIR_SWITCH=FILE turns the repairs off while the file reads `off` (checked every
    /// 30 frames; the change feed keeps running), for measuring both in one run.
    public static class ProbeRepair
    {
        public const string Name = "path-probe-repair";
        const string NavGridName = "MinionNavGrid";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly string digestPath = Environment.GetEnvironmentVariable("ONI_PROBE_DIGEST");
        static readonly bool observeOnly = Environment.GetEnvironmentVariable("ONI_PROBE_REPAIR_OFF") == "1";

        static readonly AccessTools.FieldRef<PathFinderAbilities, int> prefabId = AccessTools.FieldRefAccess<PathFinderAbilities, int>("prefabInstanceID");
        static readonly AccessTools.FieldRef<MinionPathFinderAbilities, int> proxyId = AccessTools.FieldRefAccess<MinionPathFinderAbilities, int>("proxyID");
        static readonly AccessTools.FieldRef<MinionPathFinderAbilities, int> defaultKey = AccessTools.FieldRefAccess<MinionPathFinderAbilities, int>("accessControlDefaultKey");
        static readonly AccessTools.FieldRef<MinionPathFinderAbilities, bool> outOfFuel = AccessTools.FieldRefAccess<MinionPathFinderAbilities, bool>("out_of_fuel");
        static readonly AccessTools.FieldRef<MinionPathFinderAbilities, bool> idleMask = AccessTools.FieldRefAccess<MinionPathFinderAbilities, bool>("idleNavMaskEnabled");
        static readonly AccessTools.FieldRef<MinionPathFinderAbilities, bool> swimSkill = AccessTools.FieldRefAccess<MinionPathFinderAbilities, bool>("hasSwimSkill");

        // The game's arrays as this tweak's structs (the same layouts, checked by SelfTest): the
        // arrays, or with paged-path-grids the page tables behind them (ViewOf).
        static Func<PathGrid, ProbePages<PCell>> cellsOf;
        static Func<PathGrid, ProbePages<PProber>> probersOf;
        static Func<NavGrid, PLink[]> linksOf;

        /// For tests (E2eDriver's state reads, `type:OniMoono.GameTweaks.ProbeRepair::Repairs`): whether
        /// the tweak runs, the probes it repaired and flooded, and with ONI_TWEAK_VERIFY those checked
        /// and those that answered otherwise than a flood.
        public static bool Running => state == State.On;
        public static long Repairs => worker?.repairs ?? 0;
        public static long Floods => worker?.floods ?? 0;
        public static long Verified => worker?.verified ?? 0;
        public static long VerifyWrong => worker?.verifyWrong ?? 0;
        public static long VerifyTies => worker?.verifyTies ?? 0;
        public static long FloodsVerified => worker?.floodsVerified ?? 0;
        public static long FloodsWrong => worker?.floodsWrong ?? 0;

        enum State { Unchecked, On, Off }
        static State state;
        static bool loggedOff;

        public static void Install(Harmony harmony)
        {
            var self = typeof(ProbeRepair);
            HarmonyMethod M(string name, int priority = -1)
            {
                var m = new HarmonyMethod(self, name);
                if (priority >= 0) m.priority = priority;
                return m;
            }
            if (digestPath != null)
            {
                harmony.Patch(AccessTools.Method(typeof(Navigator), nameof(Navigator.TakeResult)), postfix: M(nameof(DigestPost)));
                harmony.Patch(AccessTools.Method(typeof(WorkOrder), nameof(WorkOrder.Execute)), prefix: M(nameof(DigestOrder), Priority.First));
            }
            if (statsPath != null)
            {
                harmony.Patch(AccessTools.Method(typeof(WorkOrder), nameof(WorkOrder.Execute)), prefix: M(nameof(CpuPre), Priority.First), postfix: M(nameof(CpuPost), Priority.Last));
                harmony.Patch(AccessTools.Method(typeof(AsyncPathProber.Manager), nameof(AsyncPathProber.Manager.TickFrame)), postfix: M(nameof(StatsPost)));
            }
            if (observeOnly) return;
            PinnedPages<PCell>();
            PinnedPages<byte>();
            PinnedPages<int>();
            cellsOf = ViewOf<PCell>("Cells");
            probersOf = ViewOf<PProber>("ProberCells");
            linksOf = FieldAs<NavGrid, PLink[]>(nameof(NavGrid.Links));
            harmony.Patch(AccessTools.Method(typeof(AsyncPathProber), nameof(AsyncPathProber.CreateInstance)), prefix: M(nameof(Reset)));
            harmony.Patch(AccessTools.Method(typeof(AsyncPathProber.Manager), nameof(AsyncPathProber.Manager.TickFrame)), prefix: M(nameof(TickFramePre)));
            harmony.Patch(AccessTools.Method(typeof(AsyncPathProber.Manager), nameof(AsyncPathProber.Manager.Unregister)), postfix: M(nameof(UnregisterPost)));
            harmony.Patch(AccessTools.Method(typeof(WorkOrder), nameof(WorkOrder.Execute)), prefix: M(nameof(ExecutePre), Priority.Last));
            harmony.Patch(AccessTools.Method(typeof(Navigator), nameof(Navigator.TakeResult)), prefix: M(nameof(TakeResultPre), Priority.Last));
            harmony.Patch(AccessTools.Method(typeof(NavGrid), nameof(NavGrid.UpdateGraph), new[] { typeof(List<int>) }), postfix: M(nameof(UpdateGraphPost)));
            // The tables a link's evaluation reads, through their writers.
            harmony.Patch(AccessTools.Method(typeof(Grid), "UpdateNavMask"), prefix: M(nameof(NavMaskPre)), postfix: M(nameof(NavMaskPost)));
            harmony.Patch(AccessTools.Method(typeof(Grid), "UpdateBuildMask"), prefix: M(nameof(BuildMaskPre)), postfix: M(nameof(BuildMaskPost)));
            foreach (var name in new[] { nameof(Grid.RegisterRestriction), nameof(Grid.UnregisterRestriction), nameof(Grid.SetRestriction), nameof(Grid.ClearRestriction) })
                harmony.Patch(AccessTools.Method(typeof(Grid), name), postfix: M(nameof(DoorPost)));
            foreach (var name in new[] { nameof(Grid.RegisterTubeEntrance), nameof(Grid.UnregisterTubeEntrance), nameof(Grid.ReserveTubeEntrance),
                         nameof(Grid.SetTubeEntranceReservationCapacity), nameof(Grid.SetTubeEntranceOperational),
                         nameof(Grid.RegisterSuitMarker), nameof(Grid.UnregisterSuitMarker), nameof(Grid.ReserveSuit), nameof(Grid.ReserveEmptyLocker),
                         nameof(Grid.UpdateSuitMarker) })
                harmony.Patch(AccessTools.Method(typeof(Grid), name), postfix: M(nameof(SourcePost)));
            harmony.Patch(AccessTools.Method(typeof(Game), "StepTheSim"), postfix: M(nameof(StepTheSimPost)));
        }

        /// A getter of a private array field typed as one of this tweak's structs of the same layout.
        static Func<TOwner, TArray> FieldAs<TOwner, TArray>(string field)
        {
            var f = AccessTools.Field(typeof(TOwner), field) ?? throw new MissingFieldException(typeof(TOwner).Name, field);
            var dm = new DynamicMethod("ProbeRepair_" + field, typeof(TArray), new[] { typeof(TOwner) }, typeof(ProbeRepair).Module, skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, f);
            il.Emit(OpCodes.Ret);
            return (Func<TOwner, TArray>)dm.CreateDelegate(typeof(Func<TOwner, TArray>));
        }

        /// A PathGrid's array as this tweak's struct: the array itself, or when paged-path-grids
        /// rewrote PathGrid (its `<…>pages` field) a view of its page table, which makes pages
        /// through the host's PathGridPages.Materialise and keeps verify mode's flat array
        /// (PathGridPages.ShadowOf) written. A grid's table never changes, so its view is kept.
        static Func<PathGrid, ProbePages<TView>> ViewOf<TView>(string array) where TView : struct
        {
            var pagesField = AccessTools.Field(typeof(PathGrid), $"<{array}>pages");
            if (pagesField == null)
            {
                // A one-page table of the game's own array type: the game's array is no TView[]
                // to store into a TView[][] (stelem.ref checks the type), only to read as one.
                var arrayField = AccessTools.Field(typeof(PathGrid), array);
                var flat = FieldAs<PathGrid, TView[]>(array);
                var flatViews = new System.Runtime.CompilerServices.ConditionalWeakTable<PathGrid, ProbePages<TView>>();
                return grid => flatViews.GetValue(grid, g =>
                {
                    var a = flat(g);
                    var one = Array.CreateInstance(arrayField.FieldType, 1);
                    one.SetValue(a, 0);
                    return ProbePages<TView>.Over(Pun<TView[][]>(one), a.Length, 31, null, null, null);
                });
            }
            var table = FieldAs<PathGrid, TView[][]>(pagesField.Name);
            var length = FieldAs<PathGrid, int>($"<{array}>length");
            var element = pagesField.FieldType.GetElementType().GetElementType();
            var host = Type.GetType("OniMoono.Host.PathGridPages, OniMoono", true);
            int shift = (int)host.GetField("PageShift").GetRawConstantValue();
            var zero = Pun<TView[]>(Type.GetType("OniMoono.Host.Zero`1, OniMoono", true).MakeGenericType(element).GetField("Page").GetValue(null));
            var make = Call<Func<TView[][], int, TView[]>>(host.GetMethod("Materialise").MakeGenericMethod(element), typeof(TView[]), typeof(TView[][]), typeof(int));
            var shadowOf = Call<Func<TView[][], TView[]>>(host.GetMethod("ShadowOf").MakeGenericMethod(element), typeof(TView[]), typeof(TView[][]));
            var views = new System.Runtime.CompilerServices.ConditionalWeakTable<PathGrid, ProbePages<TView>>();
            return grid => views.GetValue(grid, g =>
            {
                var t = table(g);
                return ProbePages<TView>.Over(t, length(g), shift, zero, k => make(t, k), shadowOf(t));
            });
        }

        /// The tweak's own tables (ProbePages.Paged) make their pages on the pinned object heap
        /// where the runtime has one (GC.AllocateArray, beyond netstandard2.1).
        static void PinnedPages<T>() where T : struct
        {
            MethodInfo allocate = null;
            foreach (var m in typeof(GC).GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "AllocateArray" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2) allocate = m;
            if (allocate == null) return;
            var typed = (Func<int, bool, T[]>)allocate.MakeGenericMethod(typeof(T)).CreateDelegate(typeof(Func<int, bool, T[]>));
            ProbePages<T>.Allocate = n => typed(n, true);
        }

        /// The object as T, unchecked: the game's arrays of its own structs as this tweak's.
        static T Pun<T>(object value) => PunTo<T>.Value(value);

        static class PunTo<T>
        {
            internal static readonly Func<object, T> Value = Make();

            static Func<object, T> Make()
            {
                var dm = new DynamicMethod("ProbeRepair_Pun", typeof(T), new[] { typeof(object) }, typeof(ProbeRepair).Module, skipVisibility: true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ret);
                return (Func<object, T>)dm.CreateDelegate(typeof(Func<object, T>));
            }
        }

        /// A delegate calling `target` with this tweak's struct arrays for the game's.
        static TDelegate Call<TDelegate>(MethodInfo target, Type returns, params Type[] parameters) where TDelegate : Delegate
        {
            var dm = new DynamicMethod("ProbeRepair_" + target.Name, returns, parameters, typeof(ProbeRepair).Module, skipVisibility: true);
            var il = dm.GetILGenerator();
            for (int i = 0; i < parameters.Length; i++) il.Emit(OpCodes.Ldarg, (short)i);
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            return (TDelegate)dm.CreateDelegate(typeof(TDelegate));
        }

        // ---- Standing down ----

        /// The methods another owner's patch makes this tweak stand down.
        static IEnumerable<MethodBase> Guarded()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(PathProber))) if (m.Name == nameof(PathProber.Run)) yield return m;
            yield return AccessTools.Method(typeof(WorkOrder), nameof(WorkOrder.Execute));
            yield return AccessTools.Method(typeof(PathFinder), nameof(PathFinder.AddPotentials));
            yield return AccessTools.Method(typeof(PathFinder), nameof(PathFinder.AddPotential));
            yield return AccessTools.Method(typeof(PathGrid), nameof(PathGrid.SetCell));
            yield return AccessTools.Method(typeof(Navigator), nameof(Navigator.TakeResult));
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(NavGrid))) if (m.Name == nameof(NavGrid.UpdateGraph)) yield return m;
            yield return AccessTools.Method(typeof(MinionPathFinderAbilities), nameof(MinionPathFinderAbilities.TraversePath));
            yield return AccessTools.Method(typeof(MinionPathFinderAbilities), nameof(MinionPathFinderAbilities.GetSubmergedPathCostPenalty));
        }

        static string Foreign()
        {
            var owners = new List<KeyValuePair<string, IEnumerable<string>>>();
            foreach (var m in Guarded())
            {
                if (m == null) return "a method it replaces was not found";
                var info = Harmony.GetPatchInfo(m);
                if (info != null) owners.Add(new KeyValuePair<string, IEnumerable<string>>($"{m.DeclaringType?.Name}.{m.Name}", info.Owners));
            }
            return ProbeRepairPolicy.Foreign(owners);
        }

        /// PathGrid's own methods (with any patches) answer what this tweak's views of its arrays
        /// hold, and the nav grid's links read the same through the view.
        static string SelfTest(NavGrid navGrid)
        {
            var grid = new PathGrid(4, 1, false, new[] { NavType.Floor, NavType.Ladder });
            grid.BeginUpdate(7, 0, null);
            if (grid.SerialNo != 7) return "PathGrid.BeginUpdate does not set the serial number";
            var data = new PathFinder.Cell { cost = 5, parent = 1, transitionId = 9 };
            data.SetNavTypes(NavType.Ladder, NavType.Floor);
            grid.SetCell(new PathFinder.PotentialPath(2, NavType.Ladder, PathFinder.PotentialPath.Flags.None), ref data);
            grid.EndUpdate();
            var cells = cellsOf(grid);
            var probers = probersOf(grid);
            var c = cells[2 * 2 + 1];
            if (c.Cost != 5 || c.Parent != 1 || c.QueryId != 7 || c.NavTypes != ((byte)NavType.Ladder | ((byte)NavType.Floor << 4)) || c.TransitionId != 9)
                return "PathFinder.Cell's layout differs";
            var p = probers[2];
            if (p.Cost != 5 || p.QueryId != 7 || p.NavType != (byte)NavType.Ladder) return "PathGrid.ProberCell's layout differs";
            cells[3 * 2] = new PCell { Cost = 11, Parent = 2, QueryId = 7, NavTypes = (byte)NavType.Floor, TransitionId = 3 };
            probers[3] = new PProber { Cost = 11, QueryId = 7, NavType = (byte)NavType.Floor };
            var back = grid.GetCell(3, NavType.Floor, out bool inRange);
            if (!inRange || back.cost != 11 || back.parent != 2 || back.transitionId != 3 || back.navType != NavType.Floor || grid.GetCost(3) != 11 || grid.GetCost(1) != -1)
                return "PathGrid.GetCell or GetCost answer otherwise than its arrays";
            var links = linksOf(navGrid);
            var live = navGrid.Links;
            // compact-nav-links' store is read field by field (LinkReader): no layout to check.
            for (int i = 0; i < Math.Min(IsStore(live) ? 0 : live.Length, 4096); i++)
            {
                var a = live[i]; var b = links[i];
                if (a.link != b.Link || (byte)a.startNavType != b.Start || (byte)a.endNavType != b.End || a.transitionId != b.Trans || a.cost != b.Cost)
                    return "NavGrid.Link's layout differs";
            }
            if (navGrid.maxLinksPerCell > 64) return $"{navGrid.maxLinksPerCell} links a cell, more than 64";
            return null;
        }

        static void StandDown(string why)
        {
            state = State.Off;
            if (loggedOff) return;
            loggedOff = true;
            global::Debug.Log($"{Name}: off ({why})");
        }

        // ---- The change feed (main thread) ----

        sealed class Feed
        {
            public NavGrid Grid;
            public int Width, CellCount, MaxLinks;
            public PLink[][] Rows;
            public bool[] Submerged;
            public readonly ProbeChanges Changes = new ProbeChanges();
            /// Verify mode: the rules' inputs as the feed last saw them.
            public FeedAudit Audit;

            public void Add(ProbeChange kind, int cell, PLink[] row = null) => Changes.Add(kind, cell, row);

            /// A cell whose submerged state may have changed (its element, or the cell above).
            public void Resubmerge(int x)
            {
                if ((uint)x >= (uint)CellCount) return;
                bool now = PathFinder.IsSubmerged(x);
                if (now == Submerged[x]) return;
                Submerged[x] = now;
                Add(ProbeChange.Dst, x);
            }
        }

        /// Verify mode: every frame, before the feed's batch is sealed, the inputs of a link's
        /// evaluation everywhere (submerged, the nav masks, doors' permissions, tube entrances,
        /// suit markers; every 60 frames the links too) against what they were the frame before:
        /// a cell that changed with no event of the feed's is a change the feed missed.
        sealed class FeedAudit
        {
            const byte Masks = (byte)(Grid.NavFlags.AccessDoor | Grid.NavFlags.TubeEntrance | Grid.NavFlags.PreventIdleTraversal | Grid.NavFlags.SuitMarker);
            public readonly HashSet<int> Touched = new HashSet<int>();
            readonly bool[] sub;
            readonly byte[] nav;
            Dictionary<int, long> doors, tubes, suits;
            int frames;
            public long Missed;
            readonly HashSet<int> rowsMissed = new HashSet<int>();

            static readonly System.Collections.IEnumerable restrictions = (System.Collections.IEnumerable)AccessTools.Field(typeof(Grid), "restrictions").GetValue(null);
            static readonly System.Collections.IEnumerable tubeEntrances = (System.Collections.IEnumerable)AccessTools.Field(typeof(Grid), "tubeEntrances").GetValue(null);
            static readonly System.Collections.IEnumerable suitMarkers = (System.Collections.IEnumerable)AccessTools.Field(typeof(Grid), "suitMarkers").GetValue(null);

            public FeedAudit(Feed f)
            {
                sub = new bool[f.CellCount];
                nav = new byte[f.CellCount];
                for (int c = 0; c < f.CellCount; c++)
                {
                    sub[c] = PathFinder.IsSubmerged(c);
                    nav[c] = (byte)((byte)Grid.NavMasks[c] & Masks);
                }
                doors = Hashes(restrictions);
                tubes = Hashes(tubeEntrances);
                suits = Hashes(suitMarkers);
            }

            static long Mix(long h, long v) => (h ^ v) * 1099511628211L;

            static long HashOf(object value)
            {
                if (value == null) return 0;
                if (value is System.Collections.IDictionary d)
                {
                    long s = 0;
                    foreach (System.Collections.DictionaryEntry e in d) s += Mix(Mix(17, Convert.ToInt64(e.Key)), Convert.ToInt64(e.Value));
                    return Mix(31, s);
                }
                if (value is System.Collections.IEnumerable set && !(value is string))
                {
                    long s = 0;
                    foreach (var e in set) s += Mix(19, Convert.ToInt64(e));
                    return Mix(37, s);
                }
                var t = value.GetType();
                if (t.IsPrimitive || t.IsEnum) return Convert.ToInt64(value);
                long h = 1469598103934665603L;
                foreach (var fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    h = Mix(h, HashOf(fi.GetValue(value)));
                return h;
            }

            static Dictionary<int, long> Hashes(System.Collections.IEnumerable dict)
            {
                var r = new Dictionary<int, long>();
                foreach (var kv in dict)
                {
                    var t = kv.GetType();
                    int key = (int)t.GetProperty("Key").GetValue(kv);
                    r[key] = HashOf(t.GetProperty("Value").GetValue(kv));
                }
                return r;
            }

            void Diff(Dictionary<int, long> was, Dictionary<int, long> now, string what, Feed f)
            {
                foreach (var kv in now)
                    if (!was.TryGetValue(kv.Key, out long h) || h != kv.Value) Miss(f, kv.Key, was.ContainsKey(kv.Key) ? what + " changed" : what + " added");
                foreach (var kv in was)
                    if (!now.ContainsKey(kv.Key)) Miss(f, kv.Key, what + " removed");
            }

            void Miss(Feed f, int c, string what)
            {
                if (Touched.Contains(c)) return;
                if (++Missed > 50) return;
                Grid.CellToXY(c, out int x, out int y);
                int world = Grid.WorldIdx != null && (uint)c < (uint)Grid.WorldIdx.Length ? Grid.WorldIdx[c] : -1;
                global::Debug.Log($"{Name}: the feed missed a change at cell {c} (x {x}, y {y}, world {world}): {what} (frame {GameClock.Instance?.GetFrame() ?? 0})");
            }

            public void Check(Feed f)
            {
                lock (f.Changes)
                {
                    for (int c = 0; c < f.CellCount; c++)
                    {
                        bool s = PathFinder.IsSubmerged(c);
                        if (s != sub[c])
                        {
                            sub[c] = s;
                            Miss(f, c, $"submerged now {s} ({Grid.Element[c]?.id}, above {(c + f.Width < f.CellCount ? Grid.Element[c + f.Width]?.id.ToString() + (Grid.Solid[c + f.Width] ? " solid" : "") : "-")})");
                        }
                        byte m = (byte)((byte)Grid.NavMasks[c] & Masks);
                        if (m != nav[c])
                        {
                            Miss(f, c, $"nav mask {nav[c]} to {m}");
                            nav[c] = m;
                        }
                    }
                    var d = Hashes(restrictions); Diff(doors, d, "door permissions", f); doors = d;
                    var t = Hashes(tubeEntrances); Diff(tubes, t, "tube entrance", f); tubes = t;
                    var s2 = Hashes(suitMarkers); Diff(suits, s2, "suit marker", f); suits = s2;
                    if (++frames % 60 == 0)
                    {
                        var links = new LinkReader(f.Grid);
                        for (int c = 0; c < f.CellCount; c++)
                        {
                            bool same = SameRow(links, c, f.MaxLinks, f.Rows[c]);
                            if (same) rowsMissed.Remove(c);
                            else if (rowsMissed.Add(c)) Miss(f, c, "links (no UpdateGraph row)");
                        }
                    }
                    Touched.Clear();
                }
            }
        }

        static Feed feed;
        static Worker worker;
        static readonly ConcurrentDictionary<Navigator, Field> fields = new ConcurrentDictionary<Navigator, Field>();

        static void Reset()
        {
            state = State.Unchecked;
            feed = null;
            worker = null;
            fields.Clear();
        }

        /// The host's NavGridLinks.Load for the game's link type, when compact-nav-links runs (its
        /// handle stands for NavGrid.Links: one element, its link below -1).
        static Func<NavGrid.Link[], int, NavGrid.Link> storeLoad;

        static bool IsStore(NavGrid.Link[] links) => links.Length == 1 && links[0].link < -1;

        /// A grid's links: the game's array (as this tweak's struct) or compact-nav-links' store.
        readonly struct LinkReader
        {
            readonly PLink[] flat;
            readonly NavGrid.Link[] store;

            public LinkReader(NavGrid grid)
            {
                var links = grid.Links;
                if (IsStore(links))
                {
                    if (storeLoad == null)
                    {
                        var load = Type.GetType("OniMoono.Host.NavGridLinks, OniMoono", true).GetMethod("Load").MakeGenericMethod(typeof(NavGrid.Link));
                        storeLoad = (Func<NavGrid.Link[], int, NavGrid.Link>)load.CreateDelegate(typeof(Func<NavGrid.Link[], int, NavGrid.Link>));
                    }
                    store = links;
                    flat = null;
                }
                else
                {
                    flat = linksOf(grid);
                    store = null;
                }
            }

            public PLink this[int i]
            {
                get
                {
                    if (flat != null) return flat[i];
                    var l = storeLoad(store, i);
                    return new PLink { Link = l.link, Start = (byte)l.startNavType, End = (byte)l.endNavType, Trans = l.transitionId, Cost = l.cost };
                }
            }
        }

        static PLink[] Row(in LinkReader links, int cell, int maxLinks)
        {
            int b = cell * maxLinks, n = 0;
            while (n < maxLinks && links[b + n].Link != -1) n++;
            if (n == 0) return ProbeGraph.Empty;
            // element by element: the views' arrays are the game's types to Array.Copy
            var row = new PLink[n];
            for (int j = 0; j < n; j++) row[j] = links[b + j];
            return row;
        }

        static bool SameRow(in LinkReader links, int cell, int maxLinks, PLink[] row)
        {
            int b = cell * maxLinks;
            for (int j = 0; j < row.Length; j++) if (!links[b + j].Same(row[j])) return false;
            return row.Length == maxLinks || links[b + row.Length].Link == -1;
        }

        static void Start()
        {
            var navGrid = Pathfinding.Instance?.GetNavGrid(NavGridName);
            if (navGrid == null) { StandDown($"no {NavGridName}"); return; }
            string why;
            try { why = Foreign() ?? SelfTest(navGrid); }
            catch (Exception e) { why = e.Message; }
            if (why != null) { StandDown(why); return; }
            int cellCount = Grid.CellCount, maxLinks = navGrid.maxLinksPerCell;
            var links = new LinkReader(navGrid);
            var f = new Feed { Grid = navGrid, Width = Grid.WidthInCells, CellCount = cellCount, MaxLinks = maxLinks, Rows = new PLink[cellCount][], Submerged = new bool[cellCount] };
            for (int c = 0; c < cellCount; c++)
            {
                f.Rows[c] = Row(links, c, maxLinks);
                f.Submerged[c] = PathFinder.IsSubmerged(c);
            }
            var voids = new int[navGrid.transitions.Length][];
            for (int t = 0; t < voids.Length; t++)
            {
                var vo = navGrid.transitions[t].voidOffsets ?? new CellOffset[0];
                voids[t] = new int[vo.Length];
                for (int i = 0; i < vo.Length; i++) voids[t][i] = vo[i].x + vo[i].y * Grid.WidthInCells;
            }
            var navTypes = new byte[navGrid.ValidNavTypes.Length];
            for (int i = 0; i < navTypes.Length; i++) navTypes[i] = (byte)navGrid.ValidNavTypes[i];
            worker = new Worker(Grid.WidthInCells, cellCount, maxLinks, navTypes, voids, (PLink[][])f.Rows.Clone(), f);
            if (verify)
            {
                f.Audit = new FeedAudit(f);
                var touched = f.Audit.Touched;
                f.Changes.Recorded = c => touched.Add(c);
            }
            feed = f;
            state = State.On;
            global::Debug.Log($"{Name}: on ({cellCount} cells, {navTypes.Length} nav types)");
        }

        static void TickFramePre()
        {
            if (state == State.Unchecked) Start();
            if (state != State.On) return;
            if (switchPath != null && ++switchTicks % 30 == 0)
            {
                try { switchedOff = File.Exists(switchPath) && File.ReadAllText(switchPath).Trim() == "off"; }
                catch (IOException) { }
            }
            feed.Audit?.Check(feed);
            feed.Changes.Seal();
            worker.Report();
            // Navigators destroyed while a probe of theirs was under way.
            if (++ticks % 600 == 0)
                foreach (var nav in fields.Keys)
                    if (nav == null) fields.TryRemove(nav, out _);
        }

        static int ticks, switchTicks;
        static readonly string switchPath = Environment.GetEnvironmentVariable("ONI_PROBE_REPAIR_SWITCH");
        static volatile bool switchedOff;

        static void UnregisterPost(Navigator nav) => fields.TryRemove(nav, out _);

        static void UpdateGraphPost(NavGrid __instance, List<int> dirty_nav_cells)
        {
            var f = feed;
            if (f == null || __instance != f.Grid || dirty_nav_cells == null) return;
            try
            {
                var links = new LinkReader(__instance);
                int ml = f.MaxLinks;
                foreach (int cell in dirty_nav_cells)
                {
                    if ((uint)cell >= (uint)f.CellCount || SameRow(links, cell, ml, f.Rows[cell])) continue;
                    var row = Row(links, cell, ml);
                    f.Rows[cell] = row;
                    f.Add(ProbeChange.Row, cell, row);
                }
            }
            catch (Exception e) { FeedFailed(e); }
        }

        /// A change the feed could not follow: the probes are the game's from here on.
        static void FeedFailed(Exception e)
        {
            feed = null;
            StandDown($"{e.GetType().Name}: {e.Message}");
            global::Debug.LogException(e);
        }

        static void NavMaskPre(int i, out byte __state) => __state = feed != null ? (byte)Grid.NavMasks[i] : (byte)0;

        static void NavMaskPost(int i, byte __state)
        {
            var f = feed;
            if (f == null) return;
            int changed = __state ^ (byte)Grid.NavMasks[i];
            if (changed == 0) return;
            // AccessDoor: a link into the cell or over it; PreventIdleTraversal: into or out of it;
            // TubeEntrance and SuitMarker: out of it.
            if ((changed & (int)(Grid.NavFlags.AccessDoor | Grid.NavFlags.PreventIdleTraversal)) != 0) f.Add(ProbeChange.Dst, i);
            if ((changed & (int)Grid.NavFlags.AccessDoor) != 0) f.Add(ProbeChange.Void, i);
            if ((changed & (int)(Grid.NavFlags.PreventIdleTraversal | Grid.NavFlags.TubeEntrance | Grid.NavFlags.SuitMarker)) != 0) f.Add(ProbeChange.Src, i);
        }

        static void BuildMaskPre(int i, out byte __state) => __state = feed != null ? (byte)Grid.BuildMasks[i] : (byte)0;

        static void BuildMaskPost(int i, byte __state)
        {
            var f = feed;
            if (f == null || ((__state ^ (byte)Grid.BuildMasks[i]) & (int)Grid.BuildFlags.Solid) == 0) return;
            // Solid above a liquid cell makes it submerged.
            f.Resubmerge(i - f.Width);
        }

        static void DoorPost(int cell)
        {
            var f = feed;
            if (f == null) return;
            f.Add(ProbeChange.Dst, cell);
            f.Add(ProbeChange.Void, cell);
        }

        static void SourcePost(int cell) => feed?.Add(ProbeChange.Src, cell);

        static unsafe void StepTheSimPost(Sim.GameDataUpdate* __result)
        {
            var f = feed;
            if (f == null || __result == null) return;
            try
            {
                int n = __result->numSubstanceChangeInfo;
                var changes = __result->substanceChangeInfo;
                for (int i = 0; i < n; i++)
                {
                    int c = changes[i].cellIdx;
                    f.Resubmerge(c);
                    f.Resubmerge(c - f.Width);
                }
            }
            catch (Exception e) { FeedFailed(e); }
        }

        // ---- The probe thread ----

        /// A navigator's last result, for its next probe.
        sealed class Field
        {
            public bool Valid;
            public int Root, Prefab, Proxy, Key;
            public byte Nav, Flags, Bits;
            public PathGrid Grid;
            public ushort Serial;
            public long Seq;
            public ProbeFlags StateFlags;
            public List<int> Reach = new List<int>(), Spare = new List<int>();
            public readonly ProbeDelta Delta = new ProbeDelta();
            /// A repair waiting for TakeResult, and the pooled grid its result came with.
            public volatile ProbeDelta Pending;
            public PathGrid PendingFor;
            /// Its grid changed under it (set on the main thread): the next probe floods.
            public volatile bool Broken;
        }

        sealed class MinionRules : IProbeRules
        {
            public MinionPathFinderAbilities A;

            public bool IsSubmerged(int cell) => PathFinder.IsSubmerged(cell);

            /// TraversePath's refusals by nav type, from the abilities' skill and fuel (set with A).
            public bool SwimSkill, OutOfFuel;

            public uint Refused(byte flags) => ProbeGraph.Refused(SwimSkill, OutOfFuel, flags);

            public int Penalty(byte flags, in PLink l)
                => A.GetSubmergedPathCostPenalty(new PathFinder.PotentialPath(l.Link, (NavType)l.End, (PathFinder.PotentialPath.Flags)flags),
                    new NavGrid.Link(l.Link, (NavType)l.Start, (NavType)l.End, l.Trans, l.Cost));

            public bool Traverse(ref byte flags, int pathCell, byte pathNav, int fromCell, byte fromNav, int cost, byte trans, bool submerged)
            {
                var path = new PathFinder.PotentialPath(pathCell, (NavType)pathNav, (PathFinder.PotentialPath.Flags)flags);
                bool ok = A.TraversePath(ref path, fromCell, (NavType)fromNav, cost, trans, submerged);
                flags = (byte)path.flags;
                return ok;
            }
        }

        sealed class Worker
        {
            readonly ProbeGraph graph;
            readonly ProbeEngine engine, check;
            readonly Feed feed;
            /// The last batch taken, and how many events were added when it was sealed.
            long seq, drained;
            readonly ChangeSet cs = new ChangeSet();
            readonly MinionRules rules = new MinionRules();
            // Counts for the report (main thread reads them racily: they are only reported).
            public long orders, repairs, unchanged, floodGaveUp, floods, floodNew, floodKey, floodGrid, floodHistory, floodLarge, repairTicks, floodTicks, verified, verifyWrong, verifyTies, verifySkipped;
            long reportedOrders;
            readonly Stopwatch since = Stopwatch.StartNew();
            PCell[] checkCells;
            PProber[] checkProbers, repairedProbers;
            ProbeFlags checkFlags;
            ushort checkSerial;
            PCell[] repairedCells;
            long repairSince;

            public Worker(int width, int cellCount, int maxLinks, byte[] navTypes, int[][] voids, PLink[][] rows, Feed feed)
            {
                graph = new ProbeGraph(width, cellCount, maxLinks, navTypes, voids, rows);
                engine = new ProbeEngine(graph);
                if (verify) check = new ProbeEngine(graph);
                this.feed = feed;
            }

            /// Every change the game made so far (ProbeChanges.Drain seals what was not sealed yet).
            void Drain()
            {
                drained = feed.Changes.Drain(graph);
                seq = feed.Changes.Seq;
            }

            public bool Execute(ref WorkOrder order, ref WorkResult result)
            {
                var nav = order.navigator;
                var a = order.abilities as MinionPathFinderAbilities;
                var grid = result.pathGrid;
                if (nav == null || a == null || a.GetType() != typeof(MinionPathFinderAbilities) || order.navGrid != feed.Grid
                    || grid == null || grid.applyOffset || nav.PathGrid == null || nav.PathGrid.applyOffset
                    || (uint)order.originCell >= (uint)graph.CellCount)
                    return false;
                lock (this)
                {
                    orders++;
                    Drain();
                    var f = fields.GetOrAdd(nav, _ => new Field());
                    rules.A = a;
                    rules.SwimSkill = swimSkill(a);
                    rules.OutOfFuel = outOfFuel(a);
                    byte bits = (byte)((outOfFuel(a) ? 1 : 0) | (idleMask(a) ? 2 : 0) | (swimSkill(a) ? 4 : 0));
                    bool sameKey = f.Valid && f.Root == order.originCell && f.Nav == (byte)order.startingNavType && f.Flags == (byte)order.startingFlags
                        && f.Prefab == prefabId(a) && f.Proxy == proxyId(a) && f.Key == defaultKey(a) && f.Bits == bits;
                    bool repair = false;
                    if (!f.Valid) floodNew++;
                    else if (!sameKey) floodKey++;
                    else if (f.Broken || f.Pending != null || nav.PathGrid != f.Grid || f.Grid.SerialNo != f.Serial) floodGrid++;
                    else if (!feed.Changes.Collect(f.Seq, cs)) floodHistory++;
                    else if (cs.Count > graph.CellCount / 16) floodLarge++;
                    else repair = true;
                    long t0 = Stopwatch.GetTimestamp();
                    if (repair && !Repair(f, ref result))
                    {
                        floodGaveUp++;
                        repair = false;
                    }
                    if (!repair)
                    {
                        // (verify: the worker's rows are the nav grid's when nothing was added since)
                        long added = drained;
                        bool synced = verify && Interlocked.Read(ref feed.Changes.Added) == added;
                        Flood(f, ref order, ref result, a, bits);
                        floods++;
                        long took = Stopwatch.GetTimestamp() - t0;
                        floodTicks += took;
                        if (verify) VerifyFlood(f, ref order, synced, added, took);
                    }
                    rules.A = null;
                }
                result.abilitiesInstance = order.abilities;
                order.abilities = null;
                if (order.computeReachables) Reachables(nav, ref result);
                return true;
            }

            void Flood(Field f, ref WorkOrder order, ref WorkResult result, MinionPathFinderAbilities a, byte bits)
            {
                var grid = result.pathGrid;
                ushort serial = order.serialNo;
                // WorkOrder.Execute: a pooled grid last probed at this serial or later forgets its
                // cells' entries; its states too when at this very serial (a grid a repair kept long).
                if (grid.SerialNo >= serial) grid.ResetProberCells();
                var cells = cellsOf(grid);
                if (grid.SerialNo == serial) cells.Clear();
                grid.BeginUpdate(serial, order.originCell, null);
                f.StateFlags ??= new ProbeFlags(cells.Length);
                f.Valid = false;
                // The reachable cells come sorted from the engine (as Reachables wants them), else in
                // the order found (as the game leaves them when it doesn't compute reachables).
                var found = order.computeReachables ? null : result.reachableCells;
                engine.Flood(cells, probersOf(grid), f.StateFlags, serial, order.originCell, (byte)order.startingNavType, (byte)order.startingFlags, rules, found);
                grid.EndUpdate();
                f.Root = order.originCell; f.Nav = (byte)order.startingNavType; f.Flags = (byte)order.startingFlags;
                f.Prefab = prefabId(a); f.Proxy = proxyId(a); f.Key = defaultKey(a); f.Bits = bits;
                f.Grid = grid; f.Serial = serial; f.Seq = seq;
                f.Pending = null; f.PendingFor = null; f.Broken = false;
                f.Reach.Clear();
                engine.SortedFound(f.Reach);
                if (found == null) result.reachableCells.AddRange(f.Reach);
                f.Valid = true;
            }

            /// A repair into f's delta, for TakeResult; false when it gave up (nothing changed).
            bool Repair(Field f, ref WorkResult result)
            {
                long t0 = Stopwatch.GetTimestamp();
                long added = drained;
                var cells = cellsOf(f.Grid);
                var probers = probersOf(f.Grid);
                var d = f.Delta;
                if (cs.IsEmpty)
                {
                    d.Clear();
                    unchanged++;
                }
                else if (engine.Repair(cells, probers, f.StateFlags, f.Serial, cs, rules, d, ProbeEngine.Budget(f.Reach.Count)).GaveUp)
                {
                    repairTicks += Stopwatch.GetTimestamp() - t0;
                    return false;
                }
                repairSince = f.Seq;
                f.Seq = seq;
                d.ApplyTo(f.Reach, f.Spare);
                (f.Reach, f.Spare) = (f.Spare, f.Reach);
                result.reachableCells.AddRange(f.Reach);
                f.PendingFor = result.pathGrid;
                f.Pending = d;
                repairs++;
                repairTicks += Stopwatch.GetTimestamp() - t0;
                if (verify) Verify(f, probers, d, added);
                return true;
            }

            void Verify(Field f, ProbePages<PProber> probers, ProbeDelta d, long added)
            {
                int states = graph.CellCount * graph.NavCount;
                checkCells ??= new PCell[states];
                checkFlags ??= new ProbeFlags(states);
                checkProbers ??= new PProber[graph.CellCount];
                repairedProbers ??= new PProber[graph.CellCount];
                if (++checkSerial == 0) { Array.Clear(checkCells, 0, states); Array.Clear(checkProbers, 0, checkProbers.Length); checkSerial = 1; }
                check.Flood(checkCells, checkProbers, checkFlags, checkSerial, f.Root, f.Nav, f.Flags, rules, null);
                for (int i = 0; i < probers.Length; i++) repairedProbers[i] = probers[i];
                for (int i = 0; i < d.ProberCells.Count; i++) repairedProbers[d.ProberCells[i]] = d.Probers[i];
                var first = new List<int>();
                int wrong = ProbeEngine.ProberDifferences(repairedProbers, f.Serial, checkProbers, checkSerial, graph.CellCount, first);
                // The world changed while the repair and the flood ran: they may have seen two.
                if (Interlocked.Read(ref feed.Changes.Added) != added) { verifySkipped++; return; }
                verified++;
                if (wrong == 0) return;
                // Two routes of the same cost: the repair may keep the other one, and with its flags
                // (one passed a suit checkpoint) or its parent (AddPotentials skips the link back to
                // it) what lies beyond can differ. The result is still what a flood can leave
                // (ProbeEngine.Fixpoint); anything else is a repair gone wrong.
                var baseCells = cellsOf(f.Grid);
                repairedCells ??= new PCell[states];
                for (int st = 0; st < states; st++) repairedCells[st] = baseCells[st];
                for (int i = 0; i < d.States.Count; i++) repairedCells[d.States[i]] = d.Cells[i];
                string why = ProbeEngine.Fixpoint(repairedCells, repairedProbers, f.StateFlags, f.Serial, f.Root, graph, rules, f.Reach);
                if (why == null) { verifyTies++; return; }
                if (++verifyWrong <= 20)
                {
                    global::Debug.Log($"{Name}: a repair answers otherwise than a flood in {wrong} cells (root {f.Root}, first {string.Join(", ", first)}), not through a tie: {why}");
                    try { Explain(f, d, first); }
                    catch (Exception e) { global::Debug.Log($"{Name}: (no details: {e.Message})"); }
                }
            }

            /// Verify: where the repair and the flood part, for the first cells that differ: the
            /// flood's route up to the first state the repair agrees on, and the link there.
            void Explain(Field f, ProbeDelta d, List<int> first)
            {
                int nc = graph.NavCount;
                var baseCells = cellsOf(f.Grid);
                var over = new Dictionary<int, PCell>();
                for (int i = 0; i < d.States.Count; i++) over[d.States[i]] = d.Cells[i];
                PCell Rep(int s) => over.TryGetValue(s, out var c) ? c : baseCells[s];
                bool RepValid(int s) => Rep(s).QueryId == f.Serial;
                bool ChkValid(int s) => checkCells[s].QueryId == checkSerial;
                string Xy(int c) { Grid.CellToXY(c, out int x, out int y); return $"{c} ({x},{y} w{(Grid.WorldIdx != null ? Grid.WorldIdx[c] : -1)})"; }
                string St(int s, PCell c, bool valid) => valid ? $"{(NavType)graph.SlotNav[s % nc]} cost {c.Cost} from {c.Parent}/{(NavType)(c.NavTypes >> 4)} t{c.TransitionId}" : $"{(NavType)graph.SlotNav[s % nc]} none";
                string InCs(int c) => (cs.OldRows.ContainsKey(c) ? "R" : "") + (cs.Src.Contains(c) ? "S" : "") + (cs.Dst.Contains(c) ? "D" : "") + (cs.Void.Contains(c) ? "V" : "");
                var sb = new System.Text.StringBuilder();
                sb.Append($"{Name}:   since its last result: {cs.OldRows.Count} rows, {cs.Src.Count} sources, {cs.Dst.Count} destinations, {cs.Void.Count} voids (seq {repairSince} to {seq}); differing:");
                for (int k = 0; k < Math.Min(first.Count, 3); k++)
                {
                    int c = first[k];
                    sb.Append($"\n{Name}:   cell {Xy(c)} [{InCs(c)}] element {Grid.Element[c]?.id} sub {PathFinder.IsSubmerged(c)} mask {(int)Grid.NavMasks[c]}");
                    for (int j = 0; j < nc; j++)
                    {
                        int s = c * nc + j;
                        bool rv = RepValid(s), cv = ChkValid(s);
                        if (!rv && !cv) continue;
                        sb.Append($"\n{Name}:     repair {St(s, Rep(s), rv)} | flood {St(s, checkCells[s], cv)} | flags {Flags(f, s, rv)} vs {checkFlags[s]}");
                    }
                }
                // The flood's route from the first differing cell's cheapest state, up to where the repair agrees.
                int c0 = first[0], best = -1;
                for (int j = 0; j < nc; j++)
                {
                    int s = c0 * nc + j;
                    if (ChkValid(s) && (best < 0 || checkCells[s].Cost < checkCells[best].Cost)) best = s;
                }
                Func<int, bool> agree = s => RepValid(s) && ChkValid(s) && Rep(s).Cost == checkCells[s].Cost && f.StateFlags[s] == checkFlags[s];
                Func<int, string> both = s => $"repair {St(s, Rep(s), RepValid(s))} flags {Flags(f, s, RepValid(s))} | flood {St(s, checkCells[s], ChkValid(s))} flags {(ChkValid(s) ? checkFlags[s].ToString() : "-")}";
                Walk(sb, "flood", best, s => ChkValid(s), s => checkCells[s], agree, nc, Xy, InCs, both);
                best = -1;
                for (int j = 0; j < nc; j++)
                {
                    int s = c0 * nc + j;
                    if (RepValid(s) && (best < 0 || Rep(s).Cost < Rep(best).Cost)) best = s;
                }
                Walk(sb, "repair", best, RepValid, Rep, agree, nc, Xy, InCs, both);
                var changed = new List<string>();
                foreach (var c in cs.OldRows.Keys) changed.Add("R" + Xy(c));
                foreach (var c in cs.Src) changed.Add("S" + Xy(c));
                foreach (var c in cs.Dst) changed.Add("D" + Xy(c));
                foreach (var c in cs.Void) changed.Add("V" + Xy(c));
                sb.Append($"\n{Name}:   changes: {string.Join(", ", changed.GetRange(0, Math.Min(changed.Count, 40)))}");
                global::Debug.Log(sb.ToString());
            }

            string Flags(Field f, int s, bool valid) => valid ? f.StateFlags[s].ToString() : "-";

            void Walk(System.Text.StringBuilder sb, string which, int s, Func<int, bool> valid, Func<int, PCell> cell, Func<int, bool> agrees, int nc, Func<int, string> xy, Func<int, string> inCs, Func<int, string> both)
            {
                if (s < 0) { sb.Append($"\n{Name}:   {which}: no state there"); return; }
                for (int steps = 0; steps < 100000 && valid(s); steps++)
                {
                    var c = cell(s);
                    int u = c.Parent;
                    if (u < 0) { sb.Append($"\n{Name}:   {which}: the whole route differs, back to the root"); return; }
                    int ps = u * nc + graph.NavSlot[c.NavTypes >> 4];
                    if (agrees(ps))
                    {
                        int v = s / nc;
                        PLink link = default;
                        foreach (var l in graph.Rows[u]) if (l.Link == v && l.Start == (c.NavTypes >> 4) && l.End == graph.SlotNav[s % nc] && l.Trans == c.TransitionId) link = l;
                        sb.Append($"\n{Name}:   {which} parts at the link {xy(u)} [{inCs(u)}] {(NavType)(c.NavTypes >> 4)} to {xy(v)} [{inCs(v)}] {(NavType)graph.SlotNav[s % nc]}"
                            + $" (transition {c.TransitionId}, cost {link.Cost}, parent cost {cell(ps).Cost}, child {c.Cost}; dst sub {PathFinder.IsSubmerged(v)} element {Grid.Element[v]?.id} mask {(int)Grid.NavMasks[v]}, src mask {(int)Grid.NavMasks[u]}"
                            + $", voids {string.Join(" ", Array.ConvertAll(graph.VoidDeltas[c.TransitionId] ?? new int[0], x => xy(u + x) + "[" + inCs(u + x) + "] mask " + (int)Grid.NavMasks[u + x]))})"
                            + $"\n{Name}:     parent: {both(ps)}\n{Name}:     child: {both(s)}");
                        return;
                    }
                    s = ps;
                }
                sb.Append($"\n{Name}:   {which}: no agreeing state found");
            }

            PathGrid gameGrid;
            PathFinder.PotentialList gamePotentials;
            PathFinder.PotentialScratchPad gameScratch;
            ushort gameSerial;
            public long floodsVerified, floodsWrong, gameFloods, gameFloodTicks, ourFloodTicks;

            /// The flood against the game's own PathProber.Run on the same order (verify), when
            /// the worker's rows were the nav grid's and nothing changed meanwhile.
            void VerifyFlood(Field f, ref WorkOrder order, bool synced, long added, long took)
            {
                if (!synced) { verifySkipped++; return; }
                ourFloodTicks += took;
                gameGrid ??= new PathGrid(Grid.WidthInCells, Grid.HeightInCells, false, feed.Grid.ValidNavTypes);
                gamePotentials ??= new PathFinder.PotentialList();
                gameScratch ??= new PathFinder.PotentialScratchPad(feed.Grid.maxLinksPerCell);
                if (++gameSerial == 0) gameSerial = 1;
                if (gameGrid.SerialNo >= gameSerial) gameGrid.ResetProberCells();
                long t0 = Stopwatch.GetTimestamp();
                PathProber.Run(order.originCell, order.abilities, feed.Grid, order.startingNavType, gameGrid, gameSerial, gameScratch, gamePotentials, order.startingFlags, null);
                gameFloodTicks += Stopwatch.GetTimestamp() - t0;
                gameFloods++;
                if (Interlocked.Read(ref feed.Changes.Added) != added) { verifySkipped++; return; }
                var first = new List<int>();
                int wrong = ProbeEngine.ProberDifferences(probersOf(f.Grid), f.Serial, probersOf(gameGrid), gameSerial, graph.CellCount, first);
                floodsVerified++;
                if (wrong == 0) return;
                if (++floodsWrong <= 20)
                    global::Debug.Log($"{Name}: a flood answers otherwise than the game's in {wrong} cells (root {f.Root}, first {string.Join(", ", first)})");
            }

            /// WorkOrder.Execute's tail: the reachable cells (sorted: a flood's from the engine, a
            /// repair's kept so) against those the navigator holds.
            static void Reachables(Navigator navigator, ref WorkResult result)
            {
                var reach = result.reachableCells;
                var occupied = navigator.occupiedCells;
                int i = 0, j = 0;
                while (i < occupied.Count && j < reach.Count)
                {
                    if (occupied[i] < reach[j]) result.noLongerReachableCells.Add(occupied[i++]);
                    else if (reach[j] < occupied[i]) result.newlyReachableCells.Add(reach[j++]);
                    else { i++; j++; }
                }
                for (; i < occupied.Count; i++) result.noLongerReachableCells.Add(occupied[i]);
                for (; j < reach.Count; j++) result.newlyReachableCells.Add(reach[j]);
            }

            /// Every 30 s, from the main thread.
            public void Report()
            {
                if (since.ElapsedMilliseconds < 30000 || orders == reportedOrders) return;
                since.Restart();
                reportedOrders = orders;
                double us = 1e6 / Stopwatch.Frequency;
                string v = verify ? $", {verified} repairs verified, {verifyWrong} otherwise than a flood, {verifyTies} parting from a flood at a tie, {floodsVerified} floods verified, {floodsWrong} otherwise than the game's (the game's {(gameFloods > 0 ? gameFloodTicks * us / gameFloods : 0):F0} us each, ours {(gameFloods > 0 ? ourFloodTicks * us / gameFloods : 0):F0}), {verifySkipped} not checked (the world changed meanwhile), {feed.Audit?.Missed ?? 0} changes the feed missed" : "";
                global::Debug.Log($"{Name}: {orders} probes: {repairs} repaired ({unchanged} with nothing changed, {(repairs > 0 ? repairTicks * us / repairs : 0):F0} us each), "
                    + $"{floods} flooded ({(floods > 0 ? floodTicks * us / floods : 0):F0} us each; first {floodNew}, moved or other abilities {floodKey}, "
                    + $"grid replaced {floodGrid}, changes too old {floodHistory}, too many changes {floodLarge}, repair gave up {floodGaveUp}){v}");
            }
        }

        static bool ExecutePre(ref WorkOrder __instance, ref WorkResult result)
        {
            var w = worker;
            if (state != State.On || w == null || switchedOff) return true;
            try
            {
                return !w.Execute(ref __instance, ref result);
            }
            catch (Exception e)
            {
                // The game's own probe from here on, from a clean grid.
                // Repairs already done are still taken (TakeResultPre).
                StandDown($"{e.GetType().Name}: {e.Message}");
                global::Debug.LogException(e);
                if (result.pathGrid != null)
                {
                    cellsOf(result.pathGrid).Clear();
                    result.pathGrid.ResetProberCells();
                }
                result.reachableCells?.Clear();
                result.newlyReachableCells?.Clear();
                result.noLongerReachableCells?.Clear();
                return true;
            }
        }

        /// A repair's result goes into the navigator's own grid; the pooled grid it came with
        /// goes back to the pool unused.
        static bool TakeResultPre(Navigator __instance, ref WorkResult result, ref PathGrid __result)
        {
            if (!fields.TryGetValue(__instance, out var f)) return true;
            var d = f.Pending;
            if (d == null || f.PendingFor != result.pathGrid) return true;
            f.Pending = null;
            f.PendingFor = null;
            __result = result.pathGrid;
            var grid = __instance.PathGrid;
            if (grid != f.Grid || grid.SerialNo != f.Serial)
            {
                f.Broken = true;
                return false;
            }
            d.ApplyTo(cellsOf(grid), probersOf(grid));
            if (__instance.reportOccupation)
            {
                var occupied = __instance.occupiedCells;
                MinionGroupProber.Get().OccupyST(result.newlyReachableCells);
                MinionGroupProber.Get().VacateST(result.noLongerReachableCells);
                __instance.occupiedCells = result.reachableCells;
                result.reachableCells = occupied;
            }
            return false;
        }

        // ---- Measurement (tests): ONI_PROBE_STATS=FILE ----

        static readonly string statsPath = Environment.GetEnvironmentVariable("ONI_PROBE_STATS");
        static long probeCount, probeCpuNs, probeWallTicks;
        [ThreadStatic] static long cpuStart, wallStart;
        static StreamWriter stats;
        static readonly Stopwatch statsClock = new Stopwatch();
        static long statsFrames;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct Timespec { public long Sec, Nsec; }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "clock_gettime")]
        static extern int ClockGettime(int clock, out Timespec ts);

        [System.Runtime.InteropServices.DllImport("kernel32")]
        static extern IntPtr GetCurrentThread();

        [System.Runtime.InteropServices.DllImport("kernel32")]
        static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

        static readonly bool windows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        /// This thread's CPU time (Linux: CLOCK_THREAD_CPUTIME_ID; Windows: GetThreadTimes).
        static long ThreadCpuNs()
        {
            if (windows) return GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u) ? (k + u) * 100 : 0;
            return ClockGettime(3, out var ts) == 0 ? ts.Sec * 1000000000L + ts.Nsec : 0;
        }

        static void CpuPre()
        {
            cpuStart = ThreadCpuNs();
            wallStart = Stopwatch.GetTimestamp();
        }

        static void CpuPost()
        {
            Interlocked.Add(ref probeCpuNs, ThreadCpuNs() - cpuStart);
            Interlocked.Add(ref probeWallTicks, Stopwatch.GetTimestamp() - wallStart);
            Interlocked.Increment(ref probeCount);
        }

        /// Every 5 s of real time a line: real and game time, frames, probes, the probe thread's
        /// CPU in them and the process's CPU, cumulative.
        static void StatsPost()
        {
            statsFrames++;
            if (stats == null)
            {
                stats = new StreamWriter(statsPath, append: false);
                stats.WriteLine("wall_s\tgame_s\tframes\tspeed\tpaused\tprobes\tprobe_cpu_ms\tprobe_wall_ms\tprocess_cpu_ms\tworking_set_mb\tmanaged_mb\trepair");
                statsClock.Start();
            }
            else if (statsClock.ElapsedMilliseconds < (statsLines + 1) * 5000L) return;
            statsLines++;
            float game = 0;
            int speed = 0;
            bool paused = false;
            try { game = GameClock.Instance.GetTime(); speed = SpeedControlScreen.Instance.GetSpeed(); paused = SpeedControlScreen.Instance.IsPaused; } catch (Exception) { }
            var process = Process.GetCurrentProcess();
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            long ws = process.WorkingSet64 >> 20, managed = GC.GetTotalMemory(false) >> 20;
            stats.WriteLine($"{statsClock.Elapsed.TotalSeconds:F1}\t{game:F1}\t{statsFrames}\t{speed}\t{(paused ? 1 : 0)}\t{Interlocked.Read(ref probeCount)}\t"
                + $"{Interlocked.Read(ref probeCpuNs) / 1e6:F1}\t{Interlocked.Read(ref probeWallTicks) * 1000.0 / Stopwatch.Frequency:F1}\t{cpu:F0}\t{ws}\t{managed}\t{(Running && !switchedOff ? 1 : 0)}");
            stats.Flush();
        }

        static long statsLines;

        // ---- Digests (tests) ----

        static StreamWriter digest;
        static readonly Dictionary<Navigator, int> digestIds = new Dictionary<Navigator, int>();
        static Func<PathGrid, ProbePages<PProber>> digestProbers;

        static readonly ConcurrentDictionary<Navigator, (int Root, int Nav, int Flags)> digestOrders = new ConcurrentDictionary<Navigator, (int, int, int)>();

        static void DigestOrder(ref WorkOrder __instance)
        {
            if (__instance.navigator != null)
                digestOrders[__instance.navigator] = (__instance.originCell, (int)__instance.startingNavType, (int)__instance.startingFlags);
        }

        static void DigestPost(Navigator __instance)
        {
            var grid = __instance.PathGrid;
            if (grid == null || grid.applyOffset) return;
            digestProbers ??= ViewOf<PProber>("ProberCells");
            if (digest == null)
            {
                digest = new StreamWriter(digestPath, append: false) { AutoFlush = false };
                digest.WriteLine("frame\tnavigator\troot\tnav\tflags\tcells\tset\tcosts");
            }
            var probers = digestProbers(grid);
            ushort serial = grid.SerialNo;
            ulong set = 1469598103934665603UL, costs = set;
            int n = 0;
            for (int c = 0; c < probers.Length; c++)
            {
                if (probers[c].QueryId != serial || serial == 0) continue;
                n++;
                set = (set ^ (uint)c) * 1099511628211UL;
                costs = (costs ^ (uint)c) * 1099511628211UL;
                costs = (costs ^ (uint)probers[c].Cost) * 1099511628211UL;
            }
            if (!digestIds.TryGetValue(__instance, out int id))
                digestIds[__instance] = id = __instance.GetComponent<KPrefabID>()?.InstanceID ?? -1;
            var o = digestOrders.TryGetValue(__instance, out var order) ? order : (Root: -1, Nav: -1, Flags: -1);
            digest.WriteLine($"{GameClock.Instance?.GetFrame() ?? 0}\t{id}\t{o.Root}\t{o.Nav}\t{o.Flags}\t{n}\t{set:x16}\t{costs:x16}");
            if (++digestLines % 64 == 0) digest.Flush();
        }

        static int digestLines;
    }
}
