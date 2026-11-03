using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OniMoono.GameTweaks
{
    // The `path-probe-repair` tweak's search (ProbeRepair): the game's path probe as a flood, and
    // the repair of a probe's result after the world changed. Plain .NET with no game types (the
    // unit tests compile it in); the game side reads the game's arrays through these structs, which
    // have the game's layouts (checked when the tweak starts).

    /// PathFinder.Cell: one (cell, nav type) state of a probe. NavTypes: the state's nav type in
    /// the low 4 bits, its parent's above; valid while QueryId is the grid's serial number.
    [StructLayout(LayoutKind.Sequential)]
    public struct PCell
    {
        public int Cost, Parent;
        public ushort QueryId;
        public byte NavTypes, TransitionId;
    }

    /// PathGrid.ProberCell: a cell's cheapest state but a tube's (what GetCost answers).
    [StructLayout(LayoutKind.Sequential)]
    public struct PProber
    {
        public int Cost;
        public ushort QueryId;
        public byte NavType;
    }

    /// NavGrid.Link: an edge from a cell's row to `Link`, from nav type `Start` to `End`.
    [StructLayout(LayoutKind.Sequential)]
    public struct PLink
    {
        public int Link;
        public byte Start, End, Trans, Cost;

        public bool Same(in PLink o) => Link == o.Link && Start == o.Start && End == o.End && Trans == o.Trans && Cost == o.Cost;
    }

    /// What the probe asks of the navigator's abilities and the world besides the links:
    /// MinionPathFinderAbilities.GetSubmergedPathCostPenalty and TraversePath, PathFinder.IsSubmerged.
    public interface IProbeRules
    {
        bool IsSubmerged(int cell);
        /// The penalty for taking `link` with these path flags (the game asks it when the link's end
        /// is submerged or a swim). Never negative (the game's is a multiple of the link's cost).
        int Penalty(byte flags, in PLink link);
        /// Whether the link from (fromCell, fromNav) to (pathCell, pathNav) may be taken with these
        /// flags, and the flags after it.
        bool Traverse(ref byte flags, int pathCell, byte pathNav, int fromCell, byte fromNav, int cost, byte trans, bool submerged);
    }

    /// What changed in the world for the probes: a link row replaced, or the cells whose links'
    /// evaluation changed: as the source of a link (a tube entrance, a suit marker), as its
    /// destination (a door's permissions, submerged, idle traversal) or under its void offsets (a
    /// door).
    public enum ProbeChange : byte { Row, Src, Dst, Void }

    public struct ProbeEvent
    {
        public ProbeChange Kind;
        public int Cell;
        /// Row: the new row, and the row it replaced (filled when the worker applies it).
        public PLink[] Row, Old;
    }

    /// The changes of one frame, in order, numbered.
    public sealed class ProbeBatch
    {
        public long Seq;
        public readonly List<ProbeEvent> Events = new List<ProbeEvent>();
    }

    /// The changes since a probe's result, merged: each changed row's row as the result saw it,
    /// and the cells.
    public sealed class ChangeSet
    {
        public readonly Dictionary<int, PLink[]> OldRows = new Dictionary<int, PLink[]>();
        public readonly HashSet<int> Src = new HashSet<int>(), Dst = new HashSet<int>(), Void = new HashSet<int>();

        public bool IsEmpty => OldRows.Count == 0 && Src.Count == 0 && Dst.Count == 0 && Void.Count == 0;
        public int Count => OldRows.Count + Src.Count + Dst.Count + Void.Count;

        public void Clear()
        {
            OldRows.Clear(); Src.Clear(); Dst.Clear(); Void.Clear();
        }

        /// One event after those already added (a row keeps the first row it replaced).
        public void Add(in ProbeEvent e)
        {
            switch (e.Kind)
            {
                case ProbeChange.Row: if (!OldRows.ContainsKey(e.Cell)) OldRows[e.Cell] = e.Old ?? ProbeGraph.Empty; break;
                case ProbeChange.Src: Src.Add(e.Cell); break;
                case ProbeChange.Dst: Dst.Add(e.Cell); break;
                case ProbeChange.Void: Void.Add(e.Cell); break;
            }
        }
    }

    /// The nav grid's links as the worker sees them: a row per cell (never changed in place; a
    /// change replaces it), and for each cell the links into it. The game's NavTypeTable: nav
    /// types numbered in ascending order of those the grid has (SlotNav, NavSlot).
    public sealed class ProbeGraph
    {
        public const byte Swim = 6, Hover = 5, Tube = 8, NumNavTypes = 11;
        public static readonly PLink[] Empty = new PLink[0];

        public readonly int Width, CellCount, MaxLinks, NavCount;
        public readonly byte[] SlotNav;
        public readonly int[] NavSlot = new int[NumNavTypes];
        /// Per transition, its void offsets as cell deltas; all of them, once each.
        public readonly int[][] VoidDeltas;
        public readonly int[] AllVoidDeltas;
        public readonly PLink[][] Rows;
        /// Links into each cell: (source cell << 6) | index in its row.
        readonly int[][] rev;
        readonly int[] revCount;

        public ProbeGraph(int width, int cellCount, int maxLinks, byte[] navTypes, int[][] voidDeltas, PLink[][] rows)
        {
            if (maxLinks > 64) throw new ArgumentOutOfRangeException(nameof(maxLinks), "at most 64 links a cell");
            Width = width; CellCount = cellCount; MaxLinks = maxLinks;
            var slots = new List<byte>();
            for (int i = 0; i < NumNavTypes; i++)
            {
                NavSlot[i] = -1;
                if (Array.IndexOf(navTypes, (byte)i) >= 0) { NavSlot[i] = slots.Count; slots.Add((byte)i); }
            }
            SlotNav = slots.ToArray();
            NavCount = SlotNav.Length;
            VoidDeltas = voidDeltas;
            var all = new HashSet<int>();
            foreach (var v in voidDeltas) if (v != null) foreach (var d in v) all.Add(d);
            AllVoidDeltas = new int[all.Count];
            all.CopyTo(AllVoidDeltas);
            Rows = rows;
            rev = new int[cellCount][];
            revCount = new int[cellCount];
            for (int u = 0; u < cellCount; u++)
            {
                var row = rows[u] ??= Empty;
                for (int j = 0; j < row.Length; j++) AddRev(row[j].Link, (u << 6) | j);
            }
        }

        void AddRev(int v, int entry)
        {
            if ((uint)v >= (uint)CellCount) return;
            var a = rev[v];
            int n = revCount[v];
            if (a == null) rev[v] = a = new int[4];
            else if (n == a.Length) Array.Resize(ref rev[v], n * 2);
            rev[v][n] = entry;
            revCount[v] = n + 1;
        }

        void RemoveRev(int v, int entry)
        {
            if ((uint)v >= (uint)CellCount) return;
            var a = rev[v];
            int n = revCount[v];
            for (int i = 0; i < n; i++)
                if (a[i] == entry) { a[i] = a[n - 1]; revCount[v] = n - 1; return; }
        }

        /// Replaces a row; returns the one it replaced.
        public PLink[] SetRow(int cell, PLink[] row)
        {
            row ??= Empty;
            var old = Rows[cell];
            for (int j = 0; j < old.Length; j++) RemoveRev(old[j].Link, (cell << 6) | j);
            Rows[cell] = row;
            for (int j = 0; j < row.Length; j++) AddRev(row[j].Link, (cell << 6) | j);
            return old;
        }

        /// The links into a cell, as (source cell << 6) | index: entries [0, count).
        public int[] In(int cell, out int count)
        {
            count = revCount[cell];
            return rev[cell];
        }
    }

    /// Counts of one repair, for the tweak's report and the tests.
    public struct RepairStats
    {
        public int ChangedEdges, Invalidated, Pops, Relaxed, Stale;
    }

    /// A repair's result for the main thread to write into the navigator's PathGrid: the states and
    /// the cells' prober entries that changed, and the cells that became reachable or stopped being
    /// (ascending).
    public sealed class ProbeDelta
    {
        public readonly List<int> States = new List<int>(), ProberCells = new List<int>(), Newly = new List<int>(), Gone = new List<int>();
        public readonly List<PCell> Cells = new List<PCell>();
        public readonly List<PProber> Probers = new List<PProber>();

        public void Clear()
        {
            States.Clear(); Cells.Clear(); ProberCells.Clear(); Probers.Clear(); Newly.Clear(); Gone.Clear();
        }

        public void ApplyTo(PCell[] cells, PProber[] probers)
        {
            for (int i = 0; i < States.Count; i++) cells[States[i]] = Cells[i];
            for (int i = 0; i < ProberCells.Count; i++) probers[ProberCells[i]] = Probers[i];
        }

        /// A sorted list of reachable cells after this delta (Newly added, Gone removed).
        public void ApplyTo(List<int> reach, List<int> into)
        {
            into.Clear();
            int i = 0, a = 0, r = 0;
            var gone = Gone; var newly = Newly;
            while (i < reach.Count || a < newly.Count)
            {
                int next;
                if (a >= newly.Count || (i < reach.Count && reach[i] < newly[a])) next = reach[i++];
                else next = newly[a++];
                while (r < gone.Count && gone[r] < next) r++;
                if (r < gone.Count && gone[r] == next) continue;
                into.Add(next);
            }
        }
    }

    /// The probe: Flood is the game's PathProber.Run over PathFinder.AddPotentials into a
    /// PathGrid's arrays, with the same queue (PathFinder.PotentialList, its thresholds kept from
    /// one flood to the next as the worker's list keeps them) and so the same order, and records
    /// each state's path flags, which the game keeps only in its queue. Repair brings a flooded
    /// (or repaired) result up to date with a ChangeSet: the states whose parent link changed or
    /// got worse, and everything below them, are dropped and found again from the states around
    /// them, then improvements spread as in a flood; costs and the reachable cells come out as a
    /// new flood's, but where two routes cost the same either may be the parent.
    public sealed class ProbeEngine
    {
        readonly ProbeGraph g;
        readonly int nc;
        readonly byte[] slotNav;
        readonly int[] navSlot;

        public ProbeEngine(ProbeGraph graph)
        {
            g = graph;
            nc = graph.NavCount;
            slotNav = graph.SlotNav;
            navSlot = graph.NavSlot;
            int states = graph.CellCount * nc;
            ov = new PCell[states];
            ovFlags = new byte[states];
            ovStamp = new int[states];
            mark = new int[states];
            setAt = new int[states];
            cellMark = new int[graph.CellCount];
            foundBits = new ulong[(graph.CellCount + 63) >> 6];
        }

        public ProbeGraph Graph => g;

        // ---- The flood: PathProber.Run ----

        struct Entry
        {
            public int Key, Cell;
            public byte Nav, Flags;
        }

        /// PathFinder.PotentialList.PriorityQueue: a binary heap with its sift rules.
        sealed class Heap
        {
            public Entry[] A = new Entry[1024];
            public int N;

            // The game's sifts swap the moving entry down or up; these move the others into its
            // hole and make the same comparisons (`a - b > 0`, as the game's, overflow and all).
            public void Push(in Entry e)
            {
                if (N == A.Length) Array.Resize(ref A, N * 2);
                var a = A;
                int pos = N++, key = e.Key;
                while (pos > 0)
                {
                    int parent = (pos - 1) / 2;
                    if (a[parent].Key - key <= 0) break;
                    a[pos] = a[parent];
                    pos = parent;
                }
                a[pos] = e;
            }

            public Entry Pop()
            {
                var a = A;
                var result = a[0];
                if (N <= 1) { N = 0; return result; }
                var x = a[--N];
                int count = N, pos = 0, key = x.Key;
                while (true)
                {
                    int m = pos, mk = key, l = 2 * pos + 1, r = l + 1;
                    if (l < count && mk - a[l].Key > 0) { m = l; mk = a[l].Key; }
                    if (r < count && mk - a[r].Key > 0) m = r;
                    if (m == pos) break;
                    a[pos] = a[m];
                    pos = m;
                }
                a[pos] = x;
                return result;
            }
        }

        // PathFinder.PotentialList.HOTQueue
        Heap hot = new Heap(), cold = new Heap();
        int hotThreshold = int.MinValue, coldThreshold = int.MinValue, queued;

        void Enqueue(int key, int cell, byte nav, byte flags)
        {
            var e = new Entry { Key = key, Cell = cell, Nav = nav, Flags = flags };
            if (key <= hotThreshold) hot.Push(e);
            else
            {
                cold.Push(e);
                coldThreshold = Math.Max(coldThreshold, key);
            }
            queued++;
        }

        Entry Dequeue()
        {
            if (hot.N == 0)
            {
                (hot, cold) = (cold, hot);
                hotThreshold = coldThreshold;
            }
            queued--;
            return hot.Pop();
        }

        PLink[] scratchLinks = new PLink[64];
        int[] scratchWas = new int[64];
        bool[] scratchSub = new bool[64];
        int[] scratchCost = new int[64];

        /// PathProber.Run(root, abilities, grid, nav type, path grid, serial, …, flags, found) after
        /// PathGrid.BeginUpdate: `cells`/`probers` are the path grid's arrays, `flags` gets every
        /// state's path flags, `found` the cells found (as PathGrid.SetCell adds them). Returns how
        /// many times a state was set.
        public int Flood(PCell[] cells, PProber[] probers, byte[] flags, ushort serial, int root, byte rootNav, byte rootFlags, IProbeRules rules, List<int> found)
        {
            int set = 0;
            if (foundDirty) Array.Clear(foundBits, 0, foundBits.Length);
            foundDirty = true;
            var rows = g.Rows;
            int cellCount = g.CellCount;
            // AddPotential(root, InvalidCell, NumNavTypes, 0, 0): the root's cell as GetCell gave it.
            int rootState = root * nc + navSlot[rootNav];
            // (Set writes every field of a state: what the game's copy of it held is never read.)
            var rootData = new PCell { Cost = 0, Parent = -1, NavTypes = (byte)(rootNav | (ProbeGraph.NumNavTypes << 4)), TransitionId = 0 };
            Enqueue(0, root, rootNav, rootFlags);
            Set(cells, probers, flags, serial, root, rootNav, ref rootData, rootFlags, found);
            set++;
            while (queued > 0)
            {
                var e = Dequeue();
                ref var cd = ref cells[e.Cell * nc + navSlot[e.Nav]];
                if (cd.QueryId != serial || cd.Cost != e.Key) continue;
                // AddPotentials
                int pcell = e.Cell;
                if ((uint)pcell >= (uint)cellCount) continue;
                int cost = cd.Cost, parentCell = cd.Parent;
                byte parentNav = (byte)(cd.NavTypes >> 4), pnav = e.Nav;
                var row = rows[pcell];
                if (scratchLinks.Length < row.Length) Grow(row.Length);
                int n = 0;
                for (int i = 0; i < row.Length; i++)
                {
                    var l = row[i];
                    if (l.Start == pnav && (parentCell != l.Link || parentNav != l.Start)) scratchLinks[n++] = l;
                }
                int m = 0;
                for (int j = 0; j < n; j++)
                {
                    var l = scratchLinks[j];
                    ref var c = ref cells[l.Link * nc + navSlot[l.End]];
                    int ccost = c.QueryId == serial ? c.Cost : -1;
                    int num5 = cost + l.Cost;
                    // No penalty is negative: a link that costs no less before it can't after.
                    if (ccost != -1 && num5 >= ccost) continue;
                    bool sub = rules.IsSubmerged(l.Link);
                    int penalty = sub || l.End == ProbeGraph.Swim ? rules.Penalty(e.Flags, l) : 0;
                    num5 += penalty;
                    if (ccost == -1 || num5 < ccost)
                    {
                        scratchWas[m] = ccost;
                        scratchLinks[m] = l;
                        scratchSub[m] = sub;
                        scratchCost[m] = num5;
                        m++;
                    }
                }
                for (int k = 0; k < m; k++)
                {
                    var l = scratchLinks[k];
                    int was = scratchWas[k];
                    int num6 = scratchCost[k];
                    if (was == -1 || num6 < was)
                    {
                        byte pf = e.Flags;
                        if (rules.Traverse(ref pf, l.Link, l.End, pcell, pnav, num6, l.Trans, scratchSub[k]))
                        {
                            var data = new PCell { Cost = num6, Parent = pcell, NavTypes = (byte)(l.End | (pnav << 4)), TransitionId = l.Trans };
                            Enqueue(num6, l.Link, l.End, pf);
                            Set(cells, probers, flags, serial, l.Link, l.End, ref data, pf, found);
                            set++;
                        }
                    }
                }
            }
            return set;
        }

        readonly ulong[] foundBits;
        bool foundDirty;
        // The index of a 64-bit word's lowest set bit (netstandard2.1 has no BitOperations).
        static readonly byte[] DeBruijn =
        {
            0, 1, 48, 2, 57, 49, 28, 3, 61, 58, 50, 42, 38, 29, 17, 4, 62, 55, 59, 36, 53, 51, 43, 22, 45, 39, 33, 30, 24, 18, 12, 5,
            63, 47, 56, 27, 60, 41, 37, 16, 54, 35, 52, 21, 44, 32, 23, 11, 46, 26, 40, 15, 34, 20, 31, 10, 25, 14, 19, 9, 13, 8, 7, 6,
        };

        /// The cells the last Flood found, ascending (the found list sorted).
        public void SortedFound(List<int> into)
        {
            var bits = foundBits;
            for (int w = 0; w < bits.Length; w++)
            {
                ulong b = bits[w];
                if (b == 0) continue;
                bits[w] = 0;
                int baseCell = w << 6;
                do
                {
                    into.Add(baseCell + DeBruijn[((b & (ulong)-(long)b) * 0x03F79D71B4CB0A89UL) >> 58]);
                    b &= b - 1;
                } while (b != 0);
            }
            foundDirty = false;
        }

        void Grow(int n)
        {
            int size = Math.Max(n, scratchLinks.Length * 2);
            Array.Resize(ref scratchLinks, size);
            Array.Resize(ref scratchWas, size);
            Array.Resize(ref scratchSub, size);
            Array.Resize(ref scratchCost, size);
        }

        /// PathGrid.SetCell.
        void Set(PCell[] cells, PProber[] probers, byte[] flags, ushort serial, int cell, byte nav, ref PCell data, byte pf, List<int> found)
        {
            data.QueryId = serial;
            int s = cell * nc + navSlot[nav];
            cells[s] = data;
            flags[s] = pf;
            if (nav != ProbeGraph.Tube)
            {
                ref var p = ref probers[cell];
                if (p.QueryId != serial)
                {
                    found?.Add(cell);
                    foundBits[cell >> 6] |= 1UL << cell;
                }
                if (p.QueryId != serial || data.Cost < p.Cost)
                {
                    p.QueryId = serial;
                    p.Cost = data.Cost;
                    p.NavType = nav;
                }
            }
        }

        // ---- The repair ----

        // The repaired states over the result's: ov[s] where ovStamp[s] is this repair's.
        readonly PCell[] ov;
        readonly byte[] ovFlags;
        readonly int[] ovStamp;
        readonly int[] mark, setAt, cellMark;
        int stamp, markStamp, cellStamp, popId;
        readonly List<int> touched = new List<int>(), inval = new List<int>(), srcs = new List<int>(), touchedCells = new List<int>();
        readonly HashSet<int> srcSeen = new HashSet<int>();
        readonly HashSet<EdgeKey> ce = new HashSet<EdgeKey>();
        int[] hk = new int[1024], hs = new int[1024];
        int hn;
        PCell[] baseCells;
        byte[] baseFlags;
        ushort serial;
        IProbeRules rules;
        RepairStats stats;

        readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            readonly int u, link, rest;
            public EdgeKey(int u, in PLink l) { this.u = u; link = l.Link; rest = l.Start | (l.End << 8) | (l.Trans << 16); }
            public bool Equals(EdgeKey o) => u == o.u && link == o.link && rest == o.rest;
            public override bool Equals(object obj) => obj is EdgeKey o && Equals(o);
            public override int GetHashCode() => (u * 486187739) ^ (link * 16777619) ^ rest;
        }

        bool Valid(int s) => ovStamp[s] == stamp ? ov[s].QueryId == serial : baseCells[s].QueryId == serial;
        PCell Peek(int s) => ovStamp[s] == stamp ? ov[s] : baseCells[s];
        byte Flags(int s) => ovStamp[s] == stamp ? ovFlags[s] : baseFlags[s];

        ref PCell Write(int s)
        {
            if (ovStamp[s] != stamp)
            {
                ovStamp[s] = stamp;
                ov[s] = baseCells[s];
                if (ov[s].QueryId != serial) ov[s].QueryId = 0;
                ovFlags[s] = baseFlags[s];
                touched.Add(s);
            }
            return ref ov[s];
        }

        bool AnyValid(int cell)
        {
            int b = cell * nc;
            for (int k = 0; k < nc; k++) if (Valid(b + k)) return true;
            return false;
        }

        void Source(int u)
        {
            if ((uint)u < (uint)g.CellCount && srcSeen.Add(u)) srcs.Add(u);
        }

        readonly HashSet<PLinkKey> rowSet = new HashSet<PLinkKey>();

        readonly struct PLinkKey : IEquatable<PLinkKey>
        {
            readonly int link, rest;
            public PLinkKey(in PLink l) { link = l.Link; rest = l.Start | (l.End << 8) | (l.Trans << 16) | (l.Cost << 24); }
            public bool Equals(PLinkKey o) => link == o.link && rest == o.rest;
            public override bool Equals(object obj) => obj is PLinkKey o && Equals(o);
            public override int GetHashCode() => link * 16777619 ^ rest;
        }

        /// The links of a changed row that are in only one of its versions.
        void Diff(int u, PLink[] old, PLink[] cur)
        {
            if (old.Length * cur.Length <= 64)
            {
                foreach (var l in old) if (!Contains(cur, l)) ce.Add(new EdgeKey(u, l));
                foreach (var l in cur) if (!Contains(old, l)) ce.Add(new EdgeKey(u, l));
                return;
            }
            rowSet.Clear();
            foreach (var l in cur) rowSet.Add(new PLinkKey(l));
            foreach (var l in old) if (!rowSet.Contains(new PLinkKey(l))) ce.Add(new EdgeKey(u, l));
            rowSet.Clear();
            foreach (var l in old) rowSet.Add(new PLinkKey(l));
            foreach (var l in cur) if (!rowSet.Contains(new PLinkKey(l))) ce.Add(new EdgeKey(u, l));
        }

        static bool Contains(PLink[] row, in PLink l)
        {
            for (int i = 0; i < row.Length; i++) if (row[i].Same(l)) return true;
            return false;
        }

        static bool HasDelta(int[] deltas, int d)
        {
            if (deltas == null) return false;
            for (int i = 0; i < deltas.Length; i++) if (deltas[i] == d) return true;
            return false;
        }

        /// Repairs a navigator's result: `cells`, `probers` and `flags` as its last flood or repair
        /// left them (read only), `serial` its grid's serial number, `cs` what changed since. The
        /// states and prober entries that differ go to `delta`; `flags` gets the repaired states'
        /// flags. Ties aside, `cells` with `delta` applied is what Flood gives on the world now.
        public RepairStats Repair(PCell[] cells, PProber[] probers, byte[] flags, ushort serial, ChangeSet cs, IProbeRules rules, ProbeDelta delta)
        {
            stats = default;
            delta.Clear();
            baseCells = cells; baseFlags = flags; this.serial = serial; this.rules = rules;
            if (++stamp == int.MaxValue) { Array.Clear(ovStamp, 0, ovStamp.Length); stamp = 1; }
            if (++markStamp == int.MaxValue) { Array.Clear(mark, 0, mark.Length); markStamp = 1; }
            touched.Clear(); inval.Clear(); srcs.Clear(); srcSeen.Clear(); ce.Clear();
            hn = 0;
            NextPop();
            var rows = g.Rows;

            // 1. The changed links and their sources.
            foreach (int u in cs.Src) Source(u);
            foreach (var kv in cs.OldRows)
            {
                int u = kv.Key;
                Diff(u, kv.Value, rows[u]);
                Source(u);
            }
            foreach (int v in cs.Dst)
            {
                if ((uint)v >= (uint)g.CellCount) continue;
                var into = g.In(v, out int n);
                for (int i = 0; i < n; i++)
                {
                    int u = into[i] >> 6;
                    ce.Add(new EdgeKey(u, rows[u][into[i] & 63]));
                    Source(u);
                }
            }
            foreach (int x in cs.Void)
                foreach (int d in g.AllVoidDeltas)
                {
                    int u = x - d;
                    if ((uint)u >= (uint)g.CellCount) continue;
                    foreach (var l in rows[u])
                        if (HasDelta(g.VoidDeltas[l.Trans], d)) { ce.Add(new EdgeKey(u, l)); Source(u); }
                    if (cs.OldRows.TryGetValue(u, out var old))
                        foreach (var l in old)
                            if (HasDelta(g.VoidDeltas[l.Trans], d)) { ce.Add(new EdgeKey(u, l)); Source(u); }
                }
            stats.ChangedEdges = ce.Count;

            // 2. States whose parent link changed and may now cost more or give other flags, and
            // everything below them (along the links the result was found with).
            foreach (int u in srcs)
            {
                if (!AnyValid(u)) continue;
                bool all = cs.Src.Contains(u);
                var row = cs.OldRows.TryGetValue(u, out var o) ? o : rows[u];
                foreach (var l in row)
                {
                    int ks = navSlot[l.Start];
                    if (ks < 0) continue;
                    int s = u * nc + ks;
                    if (!Valid(s)) continue;
                    int t = State(l);
                    if (t < 0 || !Valid(t) || mark[t] == markStamp) continue;
                    var c = Peek(t);
                    if (c.Parent != u || (c.NavTypes >> 4) != l.Start || c.TransitionId != l.Trans) continue;
                    if ((all || ce.Contains(new EdgeKey(u, l))) && !StillGood(s, u, l, t))
                    {
                        mark[t] = markStamp;
                        inval.Add(t);
                    }
                }
            }
            Close(0, cs);

            // 3. Found again from the states around them, and from every changed link.
            foreach (int t in inval) Seed(t);
            foreach (int u in srcs)
            {
                if (!AnyValid(u)) continue;
                bool all = cs.Src.Contains(u);
                foreach (var l in rows[u])
                {
                    int ks = navSlot[l.Start];
                    if (ks < 0) continue;
                    int s = u * nc + ks;
                    if (Valid(s) && (all || ce.Contains(new EdgeKey(u, l)))) Relax(s, u, l);
                }
            }

            // 4. Spread as a flood does. A state whose parent changed and whose own state did not
            // follow (other flags, or the parent now skips it) is dropped with what hangs below it.
            while (hn > 0)
            {
                int key = hk[0], s = hs[0];
                Pop();
                if (!Valid(s) || Peek(s).Cost != key) continue;
                stats.Pops++;
                NextPop();
                int u = s / nc;
                byte nu = slotNav[s % nc];
                var row = rows[u];
                foreach (var l in row) if (l.Start == nu) Relax(s, u, l);
                foreach (var l in row)
                {
                    if (l.Start != nu) continue;
                    int t = State(l);
                    if (t < 0 || !Valid(t) || setAt[t] == popId) continue;
                    var c = Peek(t);
                    if (c.Parent != u || (c.NavTypes >> 4) != nu || c.TransitionId != l.Trans) continue;
                    stats.Stale++;
                    int from = inval.Count;
                    mark[t] = markStamp;
                    inval.Add(t);
                    Close(from, null);
                    for (int i = from; i < inval.Count; i++) Seed(inval[i]);
                }
            }
            stats.Invalidated = inval.Count;

            // 5. What changed, for the main thread.
            if (++cellStamp == int.MaxValue) { Array.Clear(cellMark, 0, cellMark.Length); cellStamp = 1; }
            touchedCells.Clear();
            foreach (int s in touched)
            {
                var now = ov[s];
                var was = cells[s];
                bool nowValid = now.QueryId == serial, wasValid = was.QueryId == serial;
                if (nowValid) flags[s] = ovFlags[s];
                bool same = nowValid ? wasValid && now.Cost == was.Cost && now.Parent == was.Parent && now.NavTypes == was.NavTypes && now.TransitionId == was.TransitionId
                    : !wasValid;
                if (!same)
                {
                    if (!nowValid) now = new PCell { Cost = -1, Parent = -1 };
                    delta.States.Add(s);
                    delta.Cells.Add(now);
                }
                int cell = s / nc;
                if (cellMark[cell] != cellStamp) { cellMark[cell] = cellStamp; touchedCells.Add(cell); }
            }
            touchedCells.Sort();
            foreach (int cell in touchedCells)
            {
                var old = probers[cell];
                bool oldValid = old.QueryId == serial;
                int best = -1;
                byte bestNav = 0;
                for (int k = 0; k < nc; k++)
                {
                    byte nav = slotNav[k];
                    if (nav == ProbeGraph.Tube) continue;
                    int s = cell * nc + k;
                    if (!Valid(s)) continue;
                    int c = Peek(s).Cost;
                    if (best == -1 || c < best) { best = c; bestNav = nav; }
                }
                // The game keeps the first state it found at the cheapest cost; a kept one stays.
                if (best != -1 && oldValid && old.Cost == best && old.NavType != bestNav)
                {
                    int ks = navSlot[old.NavType];
                    if (ks >= 0 && Valid(cell * nc + ks) && Peek(cell * nc + ks).Cost == best) bestNav = old.NavType;
                }
                bool newValid = best != -1;
                if (newValid ? !oldValid || old.Cost != best || old.NavType != bestNav : oldValid)
                {
                    delta.ProberCells.Add(cell);
                    delta.Probers.Add(newValid ? new PProber { Cost = best, QueryId = serial, NavType = bestNav } : new PProber { Cost = -1 });
                }
                if (newValid && !oldValid) delta.Newly.Add(cell);
                else if (!newValid && oldValid) delta.Gone.Add(cell);
            }
            baseCells = null; baseFlags = null; this.rules = null;
            return stats;
        }

        /// setAt's stamps run on from one repair to the next.
        void NextPop()
        {
            if (++popId == int.MaxValue) { Array.Clear(setAt, 0, setAt.Length); popId = 1; }
        }

        int State(in PLink l)
        {
            if ((uint)l.Link >= (uint)g.CellCount) return -1;
            int ts = navSlot[l.End];
            return ts < 0 ? -1 : l.Link * nc + ts;
        }

        /// Adds to inval, from index `from`, every valid state hanging off one there (its parent
        /// link in the row the result saw: `cs`'s old row, else the current one), and drops them.
        void Close(int from, ChangeSet cs)
        {
            var rows = g.Rows;
            for (int i = from; i < inval.Count; i++)
            {
                int s = inval[i], u = s / nc;
                byte nu = slotNav[s % nc];
                PLink[] row = null;
                if (cs == null || !cs.OldRows.TryGetValue(u, out row)) row = rows[u];
                foreach (var l in row)
                {
                    if (l.Start != nu) continue;
                    int t = State(l);
                    if (t < 0 || mark[t] == markStamp || !Valid(t)) continue;
                    var c = Peek(t);
                    if (c.Parent != u || (c.NavTypes >> 4) != nu || c.TransitionId != l.Trans) continue;
                    mark[t] = markStamp;
                    inval.Add(t);
                }
            }
            for (int i = from; i < inval.Count; i++) Write(inval[i]).QueryId = 0;
        }

        /// Relaxes every link into a dropped state from a valid one.
        void Seed(int t)
        {
            int v = t / nc;
            byte nt = slotNav[t % nc];
            var into = g.In(v, out int n);
            var rows = g.Rows;
            for (int i = 0; i < n; i++)
            {
                int u = into[i] >> 6;
                var l = rows[u][into[i] & 63];
                if (l.End != nt) continue;
                int ss = navSlot[l.Start];
                if (ss < 0) continue;
                int s = u * nc + ss;
                if (Valid(s)) Relax(s, u, l);
            }
        }

        /// The cost of `l` from valid state s (cell u), as AddPotentials computes it.
        int LinkCost(int s, in PLink l, out bool sub)
        {
            int cand = Peek(s).Cost + l.Cost;
            sub = rules.IsSubmerged(l.Link);
            if (sub || l.End == ProbeGraph.Swim) cand += rules.Penalty(Flags(s), l);
            return cand;
        }

        /// The link from s to its child t changed, but t may keep its state: the link is still
        /// there, s still takes it, and it costs no more and gives the same flags (a lower cost is
        /// found by relaxing the changed links).
        bool StillGood(int s, int u, in PLink l, int t)
        {
            if (!Contains(g.Rows[u], l)) return false;
            var c = Peek(s);
            if (l.Link == c.Parent && (c.NavTypes >> 4) == l.Start) return false;
            int cand = LinkCost(s, l, out bool sub);
            if (cand > Peek(t).Cost) return false;
            byte pf = Flags(s);
            if (!rules.Traverse(ref pf, l.Link, l.End, u, slotNav[s % nc], cand, l.Trans, sub)) return false;
            return pf == Flags(t);
        }

        /// AddPotentials for one link of valid state s: sets the link's end when it gets cheaper.
        /// An end that keeps its cost through this same link and flags counts as set again.
        void Relax(int s, int u, in PLink l)
        {
            stats.Relaxed++;
            var c = Peek(s);
            byte nu = slotNav[s % nc];
            if (l.Link == c.Parent && (c.NavTypes >> 4) == l.Start) return;
            int t = State(l);
            if (t < 0) return;
            int cand = LinkCost(s, l, out bool sub);
            bool was = Valid(t);
            byte pf = Flags(s);
            if (was)
            {
                var ct = Peek(t);
                if (cand > ct.Cost) return;
                if (cand == ct.Cost)
                {
                    if (ct.Parent == u && (ct.NavTypes >> 4) == nu && ct.TransitionId == l.Trans
                        && rules.Traverse(ref pf, l.Link, l.End, u, nu, cand, l.Trans, sub) && pf == Flags(t))
                        setAt[t] = popId;
                    return;
                }
            }
            if (!rules.Traverse(ref pf, l.Link, l.End, u, nu, cand, l.Trans, sub)) return;
            ref var w = ref Write(t);
            // found again: a later drop of its parent drops it again
            mark[t] = 0;
            w.Cost = cand;
            w.Parent = u;
            w.QueryId = serial;
            w.NavTypes = (byte)(l.End | (nu << 4));
            w.TransitionId = l.Trans;
            ovFlags[t] = pf;
            setAt[t] = popId;
            Push(cand, t);
        }

        void Push(int key, int s)
        {
            if (hn == hk.Length) { Array.Resize(ref hk, hn * 2); Array.Resize(ref hs, hn * 2); }
            int i = hn++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (hk[p] <= key) break;
                hk[i] = hk[p]; hs[i] = hs[p];
                i = p;
            }
            hk[i] = key; hs[i] = s;
        }

        void Pop()
        {
            int key = hk[--hn], s = hs[hn], i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= hn) break;
                int r = l + 1, m = r < hn && hk[r] < hk[l] ? r : l;
                if (hk[m] >= key) break;
                hk[i] = hk[m]; hs[i] = hs[m];
                i = m;
            }
            hk[i] = key; hs[i] = s;
        }

        /// Cells whose prober entries differ in validity or cost between two grids (each with its
        /// own serial): 0 when both answer GetCost alike everywhere.
        public static int ProberDifferences(PProber[] a, ushort serialA, PProber[] b, ushort serialB, int cellCount, List<int> first = null)
        {
            int n = 0;
            for (int c = 0; c < cellCount; c++)
            {
                bool va = a[c].QueryId == serialA, vb = b[c].QueryId == serialB;
                if (va != vb || (va && a[c].Cost != b[c].Cost))
                {
                    n++;
                    if (first != null && first.Count < 8) first.Add(c);
                }
            }
            return n;
        }
    }

    /// When the tweak stands down: another Harmony owner patches a method it replaces or relies on
    /// (PerfProbe's stopwatches, test infrastructure, only time them).
    public static class ProbeRepairPolicy
    {
        public const string Owner = "OniMoono.Host.GameTweaks";

        /// The first foreign patch, as "OWNER patches METHOD", or null.
        public static string Foreign(IEnumerable<KeyValuePair<string, IEnumerable<string>>> patchOwnersByMethod)
        {
            foreach (var kv in patchOwnersByMethod)
                foreach (var o in kv.Value)
                    if (o != Owner && o != "PerfProbe" && !o.StartsWith("PerfProbe.", StringComparison.Ordinal))
                        return $"{o} patches {kv.Key}";
            return null;
        }
    }
}
