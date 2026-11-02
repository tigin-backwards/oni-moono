using System;
using System.Collections.Generic;
using OniMoono.GameTweaks;

namespace OniMoono.Tests
{
    /// A small world for the path-probe-repair tests: cells (solid, liquid), nav types valid in
    /// them, link rows from transitions (with costs and void offsets), and the tables
    /// MinionPathFinderAbilities reads (door restrictions, tube entrances, suit markers, idle
    /// traversal). Rules is a port of MinionPathFinderAbilities.TraversePath and
    /// GetSubmergedPathCostPenalty and PathFinder.IsSubmerged over these tables. Each change
    /// returns the events the tweak's change feed makes of it in the game.
    sealed class ProbeModel
    {
        public const byte Floor = 0, Ladder = 4, Hover = 5, Swim = 6, Pole = 7, Tube = 8;
        public static readonly byte[] NavTypes = { Floor, Ladder, Hover, Swim, Pole, Tube };
        // NavMasks bits, as Grid.NavFlags
        const byte DoorBit = 1, TubeBit = 2, IdleBit = 4, SuitBit = 0x10;
        // PotentialPath.Flags
        const byte FAtmo = 1, FJet = 2, FMask = 4, FSuitChecks = 8, FLead = 0x10;
        /// Restriction.Directions: every direction.
        public const byte Block = 7;

        public readonly int W, H, Count;
        public readonly bool[] Solid, Liquid, LadderAt, PoleAt, TubeAt;
        readonly byte[] mask;
        readonly bool[] submerged;
        readonly Dictionary<int, (byte Orientation, Dictionary<int, byte> Masks)> restrictions = new Dictionary<int, (byte, Dictionary<int, byte>)>();
        readonly Dictionary<int, (bool Operational, int Capacity, HashSet<int> Reserved)> tubes = new Dictionary<int, (bool, int, HashSet<int>)>();
        readonly Dictionary<int, (byte Flags, byte PathFlags, int Suits, int Lockers, HashSet<int> SuitRes, HashSet<int> LockerRes)> suits =
            new Dictionary<int, (byte, byte, int, int, HashSet<int>, HashSet<int>)>();
        // transitions: id -> (dx, dy, start, end, cost, voids)
        readonly List<(int Dx, int Dy, byte Start, byte End, byte Cost, int[] Voids)> transitions = new List<(int, int, byte, byte, byte, int[])>();
        readonly Dictionary<(int, int, byte, byte), byte> transitionIds = new Dictionary<(int, int, byte, byte), byte>();
        public readonly PLink[][] Rows;
        readonly Random costs;
        bool suitMarkers = true;

        public struct Abilities
        {
            public int Prefab, Proxy, Key;
            public bool FuelOut, Idle, Swim;
        }

        ProbeModel(int w, int h, int seed, bool suitMarkers = true)
        {
            W = w; H = h; Count = w * h;
            Solid = new bool[Count]; Liquid = new bool[Count]; LadderAt = new bool[Count]; PoleAt = new bool[Count]; TubeAt = new bool[Count];
            mask = new byte[Count]; submerged = new bool[Count];
            Rows = new PLink[Count][];
            costs = new Random(seed);
            this.suitMarkers = suitMarkers;
            // Every transition up front, as the nav grid has them (their void offsets with them).
            foreach (var (dx, dy) in Offsets)
                foreach (byte a in NavTypes)
                    foreach (byte b in NavTypes)
                        if (Allowed(dx, dy, a, b)) TransitionId(dx, dy, a, b);
        }

        public static ProbeModel Random(Random rng, int w, int h, bool suitMarkers = true)
        {
            var m = new ProbeModel(w, h, rng.Next(), suitMarkers);
            for (int c = 0; c < m.Count; c++)
            {
                int y = c / w;
                m.Solid[c] = y == 0 || rng.Next(5) == 0;
                m.Liquid[c] = !m.Solid[c] && y < 3 && rng.Next(3) == 0;
                m.LadderAt[c] = rng.Next(7) == 0;
                m.PoleAt[c] = rng.Next(20) == 0;
                m.TubeAt[c] = rng.Next(9) == 0;
            }
            for (int i = 0; i < m.Count / 12; i++) m.restrictions[rng.Next(m.Count)] = ((byte)rng.Next(3), new Dictionary<int, byte>());
            foreach (var kv in m.restrictions)
            {
                m.mask[kv.Key] |= DoorBit;
                if (rng.Next(2) == 0) kv.Value.Masks[rng.Next(2) == 0 ? 200 + rng.Next(4) : 300 + rng.Next(2)] = (byte)rng.Next(1, 8);
            }
            for (int i = 0; i < m.Count / 15; i++)
            {
                int c = rng.Next(m.Count);
                m.tubes[c] = (rng.Next(4) != 0, 1 + rng.Next(2), new HashSet<int>());
                m.mask[c] |= TubeBit;
            }
            m.suitMarkers = suitMarkers;
            for (int i = 0; i < (suitMarkers ? m.Count / 20 : 0); i++)
            {
                int c = rng.Next(m.Count);
                m.suits[c] = ((byte)rng.Next(8), (byte)(1 << rng.Next(5) & (FAtmo | FJet | FMask | FLead)), rng.Next(3), 2 + rng.Next(2), new HashSet<int>(), new HashSet<int>());
                m.mask[c] |= SuitBit;
            }
            for (int c = 0; c < m.Count; c++) if (rng.Next(25) == 0) m.mask[c] |= IdleBit;
            for (int c = 0; c < m.Count; c++) m.submerged[c] = m.ComputeSubmerged(c);
            for (int c = 0; c < m.Count; c++) m.Rows[c] = m.MakeRow(c);
            return m;
        }

        /// A floor corridor along y = 1, n cells long.
        public static ProbeModel Corridor(int n)
        {
            var m = new ProbeModel(n, 3, 1);
            for (int c = 0; c < m.Count; c++) m.Solid[c] = c / n != 1;
            for (int c = 0; c < m.Count; c++) m.Rows[c] = m.MakeRow(c);
            // the corridor's cells are 0..n-1 in the tests: renumber by putting y = 1 first
            return m.Shifted();
        }

        // Corridor cells are n..2n-1; the tests name them 0..n-1 through this.
        int shift;
        ProbeModel Shifted() { shift = W; return this; }
        public int CorridorCell(int i) => shift + i;

        public int RandomFloorCell(Random rng)
        {
            while (true)
            {
                int c = rng.Next(Count);
                if (Valid(c, Floor)) return c;
            }
        }

        public ProbeGraph Graph()
        {
            var rows = (PLink[][])Rows.Clone();
            var voids = new int[256][];
            for (int i = 0; i < transitions.Count; i++) voids[i] = transitions[i].Voids;
            return new ProbeGraph(W, Count, 56, NavTypes, voids, rows);
        }

        // ---- The nav grid ----

        bool Open(int c) => (uint)c < (uint)Count && !Solid[c];

        bool Valid(int c, byte nav)
        {
            if (!Open(c)) return false;
            int below = c - W;
            switch (nav)
            {
                case Floor: return below >= 0 && Solid[below];
                case Ladder: return LadderAt[c];
                case Pole: return PoleAt[c];
                case Hover: return !Liquid[c];
                case Swim: return Liquid[c];
                case Tube: return TubeAt[c];
            }
            return false;
        }

        static readonly (int Dx, int Dy)[] Offsets = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1), (2, 0), (-2, 0), (0, 2), (2, 1), (-2, -1) };

        byte TransitionId(int dx, int dy, byte start, byte end)
        {
            if (transitionIds.TryGetValue((dx, dy, start, end), out var id)) return id;
            if (transitions.Count == 255) throw new InvalidOperationException("too many transitions");
            id = (byte)transitions.Count;
            var voids = dx != 0 && dy != 0 ? new[] { dx, dy * W } : Math.Abs(dx) == 2 ? new[] { Math.Sign(dx) } : new int[0];
            transitions.Add((dx, dy, start, end, (byte)(1 + costs.Next(12)), voids));
            transitionIds[(dx, dy, start, end)] = id;
            return id;
        }

        /// A nav type to itself or to and from a floor (fewer than 256 transitions); tubes one cell.
        /// Without suit markers, routes of the same cost must lead to the same costs: a link's
        /// flags are its start's, and PathFinder.AddPotentials' skip of the link back to the parent
        /// must not matter, so nav types change only up or down, and stay the same only sideways
        /// (the parent through a link of the same nav type is never above or below).
        bool Allowed(int dx, int dy, byte a, byte b)
        {
            if (a != b && a != Floor && b != Floor) return false;
            if ((a == Tube || b == Tube) && (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)) return false;
            return suitMarkers || (a == b) == (dx != 0);
        }

        PLink[] MakeRow(int u)
        {
            var row = new List<PLink>();
            if (!Open(u)) return ProbeGraph.Empty;
            int x = u % W;
            foreach (byte a in NavTypes)
            {
                if (!Valid(u, a)) continue;
                foreach (var (dx, dy) in Offsets)
                {
                    int tx = x + dx, ty = u / W + dy;
                    if (tx < 0 || tx >= W || ty < 0 || ty >= H) continue;
                    int v = ty * W + tx;
                    foreach (byte b in NavTypes)
                    {
                        if (!Valid(v, b)) continue;
                        if (!Allowed(dx, dy, a, b)) continue;
                        if (row.Count == 56) break;
                        byte id = TransitionId(dx, dy, a, b);
                        row.Add(new PLink { Link = v, Start = a, End = b, Trans = id, Cost = transitions[id].Cost });
                    }
                }
            }
            return row.ToArray();
        }

        static bool SameRow(PLink[] a, PLink[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!a[i].Same(b[i])) return false;
            return true;
        }

        bool ComputeSubmerged(int c)
        {
            if ((uint)c >= (uint)Count) return false;
            int above = c + W;
            bool aboveValid = above < Count;
            if (aboveValid && Liquid[above]) return true;
            return Liquid[c] && aboveValid && Solid[above];
        }

        // ---- Changes, with the feed's events ----

        void Rebuild(ProbeBatch batch, int c, int radius)
        {
            int x0 = c % W, y0 = c / W;
            for (int y = Math.Max(0, y0 - radius); y <= Math.Min(H - 1, y0 + radius); y++)
                for (int x = Math.Max(0, x0 - radius); x <= Math.Min(W - 1, x0 + radius); x++)
                {
                    int u = y * W + x;
                    var row = MakeRow(u);
                    if (SameRow(row, Rows[u])) continue;
                    Rows[u] = row;
                    batch.Events.Add(new ProbeEvent { Kind = ProbeChange.Row, Cell = u, Row = row });
                }
        }

        void Resubmerge(ProbeBatch batch, int c)
        {
            foreach (int x in new[] { c, c - W })
            {
                if ((uint)x >= (uint)Count) continue;
                bool now = ComputeSubmerged(x);
                if (now == submerged[x]) continue;
                submerged[x] = now;
                batch.Events.Add(new ProbeEvent { Kind = ProbeChange.Dst, Cell = x });
            }
        }

        static void Add(ProbeBatch b, ProbeChange kind, int cell) => b.Events.Add(new ProbeEvent { Kind = kind, Cell = cell });

        public ProbeBatch SetDoor(int corridorIndex, byte directions, int proxy)
        {
            int c = CorridorCell(corridorIndex);
            var b = new ProbeBatch();
            if (!restrictions.TryGetValue(c, out var r))
            {
                restrictions[c] = r = (0, new Dictionary<int, byte>());
                mask[c] |= DoorBit;
            }
            if (directions == 0) r.Masks.Remove(proxy); else r.Masks[proxy] = directions;
            Add(b, ProbeChange.Dst, c);
            Add(b, ProbeChange.Void, c);
            return b;
        }

        /// A few random changes of the kinds the game makes.
        public ProbeBatch RandomChanges(Random rng, int n, Abilities[] navigators)
        {
            var b = new ProbeBatch();
            for (int i = 0; i < n; i++)
            {
                int c = rng.Next(W, Count);
                switch (rng.Next(9))
                {
                    case 0: // dig or build: the links around the cell, and submerged above and below
                    case 1:
                        Solid[c] = !Solid[c];
                        if (Solid[c]) Liquid[c] = false;
                        Rebuild(b, c, 3);
                        Resubmerge(b, c);
                        break;
                    case 2: // liquid flows in or out
                        if (Solid[c]) break;
                        Liquid[c] = !Liquid[c];
                        Rebuild(b, c, 3);
                        Resubmerge(b, c);
                        break;
                    case 3: // a ladder built or removed
                        LadderAt[c] = !LadderAt[c];
                        Rebuild(b, c, 3);
                        break;
                    case 4: // a door's access for one Duplicant or a model
                        {
                            if (!restrictions.TryGetValue(c, out var r))
                            {
                                restrictions[c] = r = ((byte)rng.Next(3), new Dictionary<int, byte>());
                                mask[c] |= DoorBit;
                            }
                            int id = rng.Next(2) == 0 ? navigators[rng.Next(navigators.Length)].Proxy : 300 + rng.Next(2);
                            if (rng.Next(3) == 0) r.Masks.Remove(id); else r.Masks[id] = (byte)rng.Next(1, 8);
                            Add(b, ProbeChange.Dst, c);
                            Add(b, ProbeChange.Void, c);
                            break;
                        }
                    case 5: // a tube entrance: power, capacity, a reservation
                        {
                            if (!tubes.TryGetValue(c, out var t)) { t = (true, 1, new HashSet<int>()); mask[c] |= TubeBit; }
                            switch (rng.Next(3))
                            {
                                case 0: t.Operational = !t.Operational; break;
                                case 1: t.Capacity = rng.Next(3); break;
                                default:
                                    int p = navigators[rng.Next(navigators.Length)].Prefab;
                                    if (!t.Reserved.Remove(p)) t.Reserved.Add(p);
                                    break;
                            }
                            tubes[c] = t;
                            Add(b, ProbeChange.Src, c);
                            break;
                        }
                    case 6 when suitMarkers: // a suit checkpoint: its flags, suits in its docks, reservations
                        {
                            if (!suits.TryGetValue(c, out var s)) { s = (2, FAtmo, 1, 2, new HashSet<int>(), new HashSet<int>()); mask[c] |= SuitBit; }
                            switch (rng.Next(3))
                            {
                                case 0: s.Flags = (byte)rng.Next(8); break;
                                case 1: s.Suits = rng.Next(s.Lockers + 1); break;
                                default:
                                    int p = navigators[rng.Next(navigators.Length)].Prefab;
                                    var set = rng.Next(2) == 0 ? s.SuitRes : s.LockerRes;
                                    if (!set.Remove(p)) set.Add(p);
                                    break;
                            }
                            suits[c] = s;
                            Add(b, ProbeChange.Src, c);
                            break;
                        }
                    case 7: // idle traversal blocked or not (a nav mask bit read at both ends)
                        mask[c] ^= IdleBit;
                        Add(b, ProbeChange.Dst, c);
                        Add(b, ProbeChange.Src, c);
                        break;
                    default: // a tube built or removed
                        TubeAt[c] = !TubeAt[c];
                        Rebuild(b, c, 2);
                        break;
                }
            }
            return b;
        }

        // ---- MinionPathFinderAbilities ----

        public IProbeRules Rules(Abilities ab) => new ModelRules(this, ab);

        sealed class ModelRules : IProbeRules
        {
            readonly ProbeModel m;
            readonly Abilities ab;
            public ModelRules(ProbeModel m, Abilities ab) { this.m = m; this.ab = ab; }

            public bool IsSubmerged(int cell) => m.ComputeSubmerged(cell);

            public int Penalty(byte flags, in PLink link)
            {
                bool flag = (flags & (FAtmo | FJet | FLead)) != 0;
                bool flag2 = link.End == Swim;
                if (!ab.Swim) return !flag ? link.Cost * 2 : 0;
                if (flag & flag2) return link.Cost * 50;
                if (!flag && !flag2) return link.Cost * 2;
                if (!flag & flag2 && IsSubmerged(link.Link)) return link.Cost / 2;
                return 0;
            }

            bool Permission(int cell, int fromCell, byte fromNav)
            {
                if ((m.mask[cell] & DoorBit) == 0 || !m.restrictions.TryGetValue(cell, out var r)) return true;
                int dx = cell % m.W - fromCell % m.W, dy = cell / m.W - fromCell / m.W;
                byte directions = 0;
                switch (r.Orientation)
                {
                    case 0: if (dx < 0) directions |= 1; if (dx > 0) directions |= 2; break;
                    case 1: if (dy > 0) directions |= 1; if (dy < 0) directions |= 2; break;
                    case 2: if (Math.Abs(dx) != 1 && Math.Abs(dy) != 1 && fromNav != 10) directions |= 4; break;
                }
                if (r.Masks.TryGetValue(ab.Proxy, out var v) || r.Masks.TryGetValue(ab.Key, out v)) return (v & directions) == 0;
                return true;
            }

            public bool Traverse(ref byte flags, int pathCell, byte pathNav, int fromCell, byte fromNav, int cost, byte trans, bool submerged)
            {
                if (!Permission(pathCell, fromCell, fromNav)) return false;
                foreach (int d in m.transitions[trans].Voids)
                {
                    int c = fromCell + d;
                    if ((uint)c < (uint)m.Count && !Permission(c, fromCell, fromNav)) return false;
                }
                if (pathNav == Tube && fromNav == Floor && !UsableTube(fromCell)) return false;
                if (!ab.Swim && (pathNav == Swim || fromNav == Swim)) return false;
                if (pathNav == Hover && (ab.FuelOut || (flags & FJet) == 0)) return false;
                byte sflags = 0, spath = 0;
                bool flag = false;
                if ((flags & FSuitChecks) != 0 && (m.mask[fromCell] & SuitBit) != 0 && m.suits.TryGetValue(fromCell, out var s))
                {
                    sflags = s.Flags;
                    spath = s.PathFlags;
                    flag = (sflags & 2) != 0;
                }
                bool flag2 = pathCell % m.W > fromCell % m.W == ((sflags & 4) == 0);
                bool flag3 = (flags & (FAtmo | FJet | FMask | FLead)) != 0;
                if (flag)
                {
                    bool flag4 = (flags & spath) != 0;
                    if (flag2) { if (!flag3 && !HasSuit(fromCell)) return false; }
                    else if (flag3 && (sflags & 1) != 0 && (!flag4 || !HasEmptyLocker(fromCell))) return false;
                }
                if (ab.Idle && ((m.mask[pathCell] & IdleBit) != 0 || (m.mask[fromCell] & IdleBit) != 0)) return false;
                if (flag)
                {
                    if (flag2) { if (!flag3) flags |= spath; }
                    else flags &= unchecked((byte)~(FAtmo | FJet | FMask | FLead));
                }
                return true;
            }

            bool UsableTube(int cell)
            {
                if ((m.mask[cell] & TubeBit) == 0 || !m.tubes.TryGetValue(cell, out var t) || !t.Operational) return false;
                return t.Reserved.Count < t.Capacity || t.Reserved.Contains(ab.Prefab);
            }

            bool HasSuit(int cell)
            {
                var s = m.suits[cell];
                return s.SuitRes.Count < s.Suits || s.SuitRes.Contains(ab.Prefab);
            }

            bool HasEmptyLocker(int cell)
            {
                if (!m.suits.TryGetValue(cell, out var s)) return false;
                return s.LockerRes.Count < s.Lockers - s.Suits || s.LockerRes.Contains(ab.Prefab);
            }
        }
    }
}
