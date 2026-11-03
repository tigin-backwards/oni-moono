using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The path-probe-repair tweak's repair on two real colonies' nav grids (U60 761211, our
    /// player at speed 3): TestData/probe-c100.ptc.gz and probe-decrew.ptc.gz hold the minion nav
    /// grid and the tables MinionPathFinderAbilities reads when the capture started, then the first
    /// 600 frames: every link row the game changed, every nav mask, liquid or solid and door, tube
    /// and suit table change, and every probe the game ran (root, nav type, flags, abilities). The
    /// replay applies them in order and answers each probe as the tweak does (a repair of the
    /// Duplicant's last result when it probes from the same cell with the same flags and
    /// abilities, else a flood). Every repair must be what a flood can leave, and answer as a
    /// fresh flood does (each cell's cost, the reachable cells).
    ///
    /// The captures are cut from the measurement spike's traces (ProbeTrace on branch
    /// worktree-agent-a147d34ba89ca9b75) with the link table stored compactly ("PTC1": each
    /// cell's links with their count, else the trace's "PTR1" format).
    public class ProbeTraceTests(ITestOutputHelper output)
    {
        [Theory]
        [InlineData("probe-c100.ptc.gz")]
        [InlineData("probe-decrew.ptc.gz")]
        public void RepairsOnACapturedColonyAnswerAsFloods(string file)
        {
            var t = TraceReplay.Load(Path.Combine(AppContext.BaseDirectory, "TestData", file));
            var stats = t.Replay(maxNavigators: 12, checkEvery: 2);
            Assert.True(stats.Repairs > 50, $"{stats.Repairs} repairs");
            Assert.True(stats.Checked > 25, $"{stats.Checked} checked");
            Assert.True(stats.ChangedRepairs > 10, $"{stats.ChangedRepairs} repairs changed something");
            output.WriteLine($"{file}: {stats.Probes} probes, {stats.Floods} floods, {stats.Repairs} repairs ({stats.ChangedRepairs} changed something), "
                + $"{stats.Checked} checked; of their {stats.States} states {stats.TiedStates} have the cost a flood gives but another parent; "
                + $"mean repair {stats.RepairTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, stats.Repairs):F0} us, "
                + $"mean flood {stats.FloodTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, stats.Floods):F0} us ({stats.Found / Math.Max(1, stats.Floods)} cells); "
                + $"a repair: {stats.Edges / Math.Max(1, stats.Repairs)} changed links, {stats.Invalidated / Math.Max(1, stats.Repairs)} dropped, {stats.Pops / Math.Max(1, stats.Repairs)} pops, "
                + $"{stats.Relaxed / Math.Max(1, stats.Repairs)} relaxed, {stats.Stale / Math.Max(1, stats.Repairs)} stale; changes: {stats.Rows / Math.Max(1, stats.Repairs)} rows, "
                + $"{stats.Dst / Math.Max(1, stats.Repairs)} into cells, {stats.Src / Math.Max(1, stats.Repairs)} out of cells, {stats.Void / Math.Max(1, stats.Repairs)} over cells");
            Assert.True(stats.Wrong == 0, $"{stats.Wrong} of {stats.Checked} repairs answer otherwise than a flood; first: {stats.FirstWrong}");
            // The game ran its probes on its probe thread while the main thread changed the world,
            // and the capture records a frame's changes at its end: the replay floods the world
            // as its start frame left it. A flood must give the game's result unless the world
            // changed from that frame to the one after the probe's last (the spike's replay
            // matched 99.7% of all probes).
            output.WriteLine($"{file}: {stats.GameSame} of {stats.GameChecked} floods give the game's own result's digests; of the others {stats.GameMoved} ran while the world changed");
            Assert.True(stats.GameChecked > 200, $"{stats.GameChecked} floods compared with the game's");
            Assert.True(stats.GameSame + stats.GameMoved == stats.GameChecked,
                $"{stats.GameChecked - stats.GameSame - stats.GameMoved} of {stats.GameChecked} floods of a world that stayed the same during the game's own give another result:{stats.GameOther}");
        }
    }

    /// A probe capture's replay (ProbeTraceTests).
    sealed partial class TraceReplay
    {
        public struct Stats
        {
            public int Probes, Floods, Repairs, ChangedRepairs, Checked, Wrong;
            public int GameChecked, GameSame, GameMoved;
            public string GameOther;
            public long Found, Dst, Src, Void, Rows, States, TiedStates, RepairTicks, FloodTicks, Invalidated, Pops, Relaxed, Stale, Edges;
            public string FirstWrong;
        }

        sealed class Restriction { public byte Orientation; public Dictionary<int, byte> Masks = new Dictionary<int, byte>(); }
        sealed class Tube { public bool Operational; public int Capacity; public HashSet<int> Reserved = new HashSet<int>(); }
        sealed class Suit
        {
            public int Suits, Lockers; public byte Flags, PathFlags;
            public HashSet<int> SuitRes = new HashSet<int>(), LockerRes = new HashSet<int>();
        }

        struct Probe { public int Nav, Start, End, Found, Origin, Prefab, Proxy, Key; public byte NavType, Flags, Bits; public ulong Set, Costs; }
        struct Ev { public char Kind; public int Cell; public byte V; public PLink[] Row; public byte[] Table; }

        int w, cc, ml;
        byte[] navTypes;
        PLink[][] rows;
        int[][] voids;
        byte[] mask, liq;
        byte[] restrB, tubeB, suitB;
        Dictionary<int, Restriction> restr;
        Dictionary<int, Tube> tubes;
        Dictionary<int, Suit> suits;
        readonly List<(int Frame, List<Ev> Evs)> frames = new List<(int, List<Ev>)>();
        readonly Dictionary<int, List<Probe>> byStart = new Dictionary<int, List<Probe>>();

        public static TraceReplay Load(string path)
        {
            var t = new TraceReplay();
            using var br = new BinaryReader(new BufferedStream(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), 1 << 20));
            if (br.ReadInt32() != 0x31435450) throw new InvalidDataException("not a PTC1 capture");
            br.ReadInt32();
            t.w = br.ReadInt32(); br.ReadInt32(); t.cc = br.ReadInt32(); t.ml = br.ReadInt32();
            t.navTypes = br.ReadBytes(br.ReadInt32());
            t.rows = new PLink[t.cc][];
            for (int c = 0; c < t.cc; c++) t.rows[c] = ReadLinks(br, br.ReadByte());
            int nt = br.ReadInt32();
            t.voids = new int[nt][];
            for (int i = 0; i < nt; i++)
            {
                int n = br.ReadInt32();
                t.voids[i] = new int[n];
                for (int j = 0; j < n; j++) { int x = br.ReadInt32(), y = br.ReadInt32(); t.voids[i][j] = x + y * t.w; }
            }
            t.mask = br.ReadBytes(t.cc); t.liq = br.ReadBytes(t.cc);
            t.restrB = br.ReadBytes(br.ReadInt32()); t.tubeB = br.ReadBytes(br.ReadInt32()); t.suitB = br.ReadBytes(br.ReadInt32());
            t.restr = ParseRestr(t.restrB); t.tubes = ParseTubes(t.tubeB); t.suits = ParseSuits(t.suitB);
            while (true)
            {
                int tag;
                try { tag = br.ReadByte(); } catch (EndOfStreamException) { break; }
                if (tag != 'F') throw new InvalidDataException($"bad frame tag {tag}");
                int frame = br.ReadInt32(); br.ReadInt64(); br.ReadSingle(); br.ReadByte();
                var payload = br.ReadBytes(br.ReadInt32());
                t.frames.Add((frame, t.ParseFrame(payload)));
            }
            return t;
        }

        static PLink[] ReadLinks(BinaryReader r, int n)
        {
            if (n == 0) return ProbeGraph.Empty;
            var row = new PLink[n];
            for (int j = 0; j < n; j++) row[j] = new PLink { Link = r.ReadInt32(), Start = r.ReadByte(), End = r.ReadByte(), Trans = r.ReadByte(), Cost = r.ReadByte() };
            return row;
        }

        List<Ev> ParseFrame(byte[] buf)
        {
            var evs = new List<Ev>();
            using var r = new BinaryReader(new MemoryStream(buf));
            while (r.BaseStream.Position < buf.Length)
            {
                char tag = (char)r.ReadByte();
                switch (tag)
                {
                    case 'L': { int cell = r.ReadInt32(); evs.Add(new Ev { Kind = 'L', Cell = cell, Row = ReadLinks(r, r.ReadByte()) }); break; }
                    case 'M': case 'Q': evs.Add(new Ev { Kind = tag, Cell = r.ReadInt32(), V = r.ReadByte() }); break;
                    case 'R': case 'T': case 'S': evs.Add(new Ev { Kind = tag, Table = r.ReadBytes(r.ReadInt32()) }); break;
                    case 'W': { int n = r.ReadByte(); r.ReadBytes(n * 10); break; }
                    case 'N': r.ReadBytes(4 + 4 + 1 + 1 + 4 + 4 + 4 + 1); break;
                    case 'U': case 'X': r.ReadInt32(); break;
                    case 'P':
                        {
                            var p = new Probe { Nav = r.ReadInt32() };
                            r.ReadUInt16();
                            p.Start = r.ReadInt32(); p.End = r.ReadInt32();
                            p.Origin = r.ReadInt32(); p.NavType = r.ReadByte(); p.Flags = r.ReadByte();
                            bool abil = r.ReadBoolean();
                            if (abil) { p.Prefab = r.ReadInt32(); p.Proxy = r.ReadInt32(); p.Key = r.ReadInt32(); p.Bits = r.ReadByte(); }
                            bool minion = r.ReadBoolean();
                            p.Found = r.ReadInt32();
                            r.ReadBytes(8 + 8);
                            p.Set = r.ReadUInt64(); p.Costs = r.ReadUInt64();
                            if (abil && minion)
                            {
                                if (!byStart.TryGetValue(p.Start, out var l)) byStart[p.Start] = l = new List<Probe>();
                                l.Add(p);
                            }
                            break;
                        }
                    default: throw new InvalidDataException($"bad event tag {tag}");
                }
            }
            return evs;
        }

        // ---- The tables (ProbeTrace's serialisations) ----

        static Dictionary<int, Restriction> ParseRestr(byte[] b)
        {
            var d = new Dictionary<int, Restriction>();
            using var r = new BinaryReader(new MemoryStream(b));
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int cell = r.ReadInt32();
                var x = new Restriction { Orientation = r.ReadByte() };
                int m = r.ReadInt32();
                for (int j = 0; j < m; j++) { int id = r.ReadInt32(); x.Masks[id] = r.ReadByte(); }
                d[cell] = x;
            }
            return d;
        }

        static Dictionary<int, Tube> ParseTubes(byte[] b)
        {
            var d = new Dictionary<int, Tube>();
            using var r = new BinaryReader(new MemoryStream(b));
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int cell = r.ReadInt32();
                var x = new Tube { Operational = r.ReadByte() != 0, Capacity = r.ReadInt32() };
                int k = r.ReadInt32();
                for (int j = 0; j < k; j++) x.Reserved.Add(r.ReadInt32());
                d[cell] = x;
            }
            return d;
        }

        static Dictionary<int, Suit> ParseSuits(byte[] b)
        {
            var d = new Dictionary<int, Suit>();
            using var r = new BinaryReader(new MemoryStream(b));
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int cell = r.ReadInt32();
                var x = new Suit { Suits = r.ReadInt32(), Lockers = r.ReadInt32(), Flags = r.ReadByte(), PathFlags = r.ReadByte() };
                int k = r.ReadInt32();
                for (int j = 0; j < k; j++) x.SuitRes.Add(r.ReadInt32());
                k = r.ReadInt32();
                for (int j = 0; j < k; j++) x.LockerRes.Add(r.ReadInt32());
                d[cell] = x;
            }
            return d;
        }

        /// The cells whose entries differ between two serialised tables.
        static HashSet<int> TableDiff(byte[] a, byte[] b, char kind)
        {
            var ea = Entries(a, kind); var eb = Entries(b, kind);
            var res = new HashSet<int>();
            foreach (var kv in ea) if (!eb.TryGetValue(kv.Key, out var v) || !v.AsSpan().SequenceEqual(kv.Value)) res.Add(kv.Key);
            foreach (var kv in eb) if (!ea.ContainsKey(kv.Key)) res.Add(kv.Key);
            return res;
        }

        static Dictionary<int, byte[]> Entries(byte[] b, char kind)
        {
            var d = new Dictionary<int, byte[]>();
            using var r = new BinaryReader(new MemoryStream(b));
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                long p0 = r.BaseStream.Position;
                int cell = r.ReadInt32();
                if (kind == 'R') { r.ReadByte(); int m = r.ReadInt32(); r.ReadBytes(m * 5); }
                else if (kind == 'T') { r.ReadByte(); r.ReadInt32(); int k = r.ReadInt32(); r.ReadBytes(k * 4); }
                else { r.ReadBytes(10); int k = r.ReadInt32(); r.ReadBytes(k * 4); k = r.ReadInt32(); r.ReadBytes(k * 4); }
                d[cell] = b.AsSpan((int)p0, (int)(r.BaseStream.Position - p0)).ToArray();
            }
            return d;
        }

        // ---- MinionPathFinderAbilities over the tables ----

        bool Submerged(int cell)
        {
            if ((uint)cell >= (uint)cc) return false;
            int above = cell + w;
            bool aboveValid = (uint)above < (uint)cc;
            if (aboveValid && (liq[above] & 1) != 0) return true;
            return (liq[cell] & 1) != 0 && aboveValid && (liq[above] & 2) != 0;
        }

        sealed class Rules : IProbeRules
        {
            const byte FAtmo = 1, FJet = 2, FMask = 4, FSuitChecks = 8, FLead = 0x10;
            readonly TraceReplay t;
            readonly Probe p;
            bool FuelOut => (p.Bits & 1) != 0;
            bool Idle => (p.Bits & 2) != 0;
            bool Swim => (p.Bits & 4) != 0;
            public Rules(TraceReplay t, Probe p) { this.t = t; this.p = p; }

            public bool IsSubmerged(int cell) => t.Submerged(cell);

            public uint Refused(byte flags) => ProbeGraph.Refused(Swim, FuelOut, flags);

            public int Penalty(byte flags, in PLink link)
            {
                bool flag = (flags & (FAtmo | FJet | FLead)) != 0, flag2 = link.End == ProbeGraph.Swim;
                if (!Swim) return !flag ? link.Cost * 2 : 0;
                if (flag & flag2) return link.Cost * 50;
                if (!flag && !flag2) return link.Cost * 2;
                if (!flag & flag2 && IsSubmerged(link.Link)) return link.Cost / 2;
                return 0;
            }

            bool Permission(int cell, int fromCell, byte fromNav)
            {
                if ((t.mask[cell] & 1) == 0 || !t.restr.TryGetValue(cell, out var v)) return true;
                int dx = cell % t.w - fromCell % t.w, dy = cell / t.w - fromCell / t.w;
                byte directions = 0;
                switch (v.Orientation)
                {
                    case 0: if (dx < 0) directions |= 1; if (dx > 0) directions |= 2; break;
                    case 1: if (dy > 0) directions |= 1; if (dy < 0) directions |= 2; break;
                    case 2: if (Math.Abs(dx) != 1 && Math.Abs(dy) != 1 && fromNav != 10) directions |= 4; break;
                }
                if (v.Masks.TryGetValue(p.Proxy, out var m) || v.Masks.TryGetValue(p.Key, out m)) return (m & directions) == 0;
                return true;
            }

            public bool Traverse(ref byte flags, int pathCell, byte pathNav, int fromCell, byte fromNav, int cost, byte trans, bool submerged)
            {
                if ((t.mask[pathCell] & 1) != 0 && !Permission(pathCell, fromCell, fromNav)) return false;
                foreach (int d in t.voids[trans])
                {
                    int c = fromCell + d;
                    if ((t.mask[c] & 1) != 0 && !Permission(c, fromCell, fromNav)) return false;
                }
                if (pathNav == ProbeGraph.Tube && fromNav == 0 && !UsableTube(fromCell)) return false;
                if (!Swim && (pathNav == ProbeGraph.Swim || fromNav == ProbeGraph.Swim)) return false;
                if (pathNav == ProbeGraph.Hover && (FuelOut || (flags & FJet) == 0)) return false;
                byte sflags = 0, spath = 0;
                bool flag = false;
                if ((flags & FSuitChecks) != 0 && (t.mask[fromCell] & 0x10) != 0 && t.suits.TryGetValue(fromCell, out var s))
                {
                    sflags = s.Flags; spath = s.PathFlags;
                    flag = (sflags & 2) != 0;
                }
                bool flag2 = pathCell % t.w > fromCell % t.w == ((sflags & 4) == 0);
                bool flag3 = (flags & (FAtmo | FJet | FMask | FLead)) != 0;
                if (flag)
                {
                    bool flag4 = (flags & spath) != 0;
                    if (flag2) { if (!flag3 && !HasSuit(fromCell)) return false; }
                    else if (flag3 && (sflags & 1) != 0 && (!flag4 || !HasEmptyLocker(fromCell))) return false;
                }
                if (Idle && ((t.mask[pathCell] & 4) != 0 || (t.mask[fromCell] & 4) != 0)) return false;
                if (flag)
                {
                    if (flag2) { if (!flag3) flags |= spath; }
                    else flags &= unchecked((byte)~(FAtmo | FJet | FMask | FLead));
                }
                return true;
            }

            bool UsableTube(int cell)
            {
                if ((t.mask[cell] & 2) == 0 || !t.tubes.TryGetValue(cell, out var x) || !x.Operational) return false;
                return x.Reserved.Count < x.Capacity || x.Reserved.Contains(p.Prefab);
            }

            bool HasSuit(int cell)
            {
                var s = t.suits[cell];
                return s.SuitRes.Count < s.Suits || s.SuitRes.Contains(p.Prefab);
            }

            bool HasEmptyLocker(int cell)
            {
                if (!t.suits.TryGetValue(cell, out var s)) return false;
                return s.LockerRes.Count < s.Lockers - s.Suits || s.LockerRes.Contains(p.Prefab);
            }
        }

        // ---- The replay ----

        sealed class Nav
        {
            public ProbeRepairTests.Result R;
            public Probe Key;
        }

        static bool SameKey(Probe a, Probe b) => a.Origin == b.Origin && a.NavType == b.NavType && a.Flags == b.Flags && a.Prefab == b.Prefab
            && a.Proxy == b.Proxy && a.Key == b.Key && (a.Bits & 7) == (b.Bits & 7);

        static ulong Mix(ulong h, ulong v) { h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2); return h * 0xBF58476D1CE4E5B9UL; }

        void Game(ProbeGraph graph, ProbeEngine engine, ProbeRepairTests.Result fresh, Probe p, ref Stats stats, ref ushort serial)
        {
            fresh.Serial = Next(ref serial);
            Array.Clear(fresh.Cells, 0, fresh.Cells.Length);
            Array.Clear(fresh.Probers, 0, fresh.Probers.Length);
            fresh.Reach.Clear();
            engine.Flood(fresh.Cells, fresh.Probers, fresh.Flags, fresh.Serial, p.Origin, p.NavType, p.Flags, new Rules(this, p), fresh.Reach);
            fresh.Reach.Sort();
            ulong set = 1469598103934665603UL, costs = set;
            foreach (int cell in fresh.Reach)
            {
                set = Mix(set, (ulong)cell);
                costs = Mix(costs, ((ulong)cell << 32) | (uint)fresh.Probers[cell].Cost);
            }
            stats.GameChecked++;
            if (set == p.Set && costs == p.Costs) stats.GameSame++;
            else if (Changed(p.Start, p.End + 1)) stats.GameMoved++;
            else stats.GameOther = (stats.GameOther ?? "") + $" (frames {p.Start}-{p.End}, {fresh.Reach.Count} cells, the game's {p.Found}, set {(set == p.Set ? "same" : "other")})";
        }

        /// Whether the world changed for the probes in frames after `start` up to `end`.
        bool Changed(int start, int end)
        {
            foreach (var (frame, evs) in frames)
                if (frame > start && frame <= end)
                    foreach (var e in evs) if (e.Kind == 'L' || e.Kind == 'M' || e.Kind == 'Q' || e.Kind == 'R' || e.Kind == 'T' || e.Kind == 'S') return true;
            return false;
        }

        public Stats Replay(int maxNavigators, int checkEvery, int gameEvery = 10)
        {
            var stats = new Stats();
            var graph = new ProbeGraph(w, cc, ml, navTypes, voids, rows);
            var engine = new ProbeEngine(graph);
            var check = new ProbeEngine(graph);
            var delta = new ProbeDelta();
            var navs = new Dictionary<int, Nav>();
            int states = cc * graph.NavCount;
            var fresh = new ProbeRepairTests.Result { Cells = new PCell[states], Probers = new PProber[cc], Flags = new ProbeFlags(states) };
            ushort serial = 0;
            foreach (var (frame, evs) in frames)
            {
                var batch = Apply(graph, evs);
                foreach (var n in navs.Values) foreach (var e in batch) n.R.Pending.Add(e);
                if (!byStart.TryGetValue(frame, out var probes)) continue;
                foreach (var p in probes)
                {
                    stats.Probes++;
                    // Every tenth probe the game ran, flooded here: its reachable cells and costs
                    // as the game's own result's digests (ProbeStats.Hash) recorded them.
                    if (stats.Probes % gameEvery == 0) Game(graph, check, fresh, p, ref stats, ref serial);
                    if (!navs.TryGetValue(p.Nav, out var n))
                    {
                        if (navs.Count == maxNavigators) continue;
                        navs[p.Nav] = n = new Nav { R = new ProbeRepairTests.Result { Cells = new PCell[states], Probers = new PProber[cc], Flags = new ProbeFlags(states) } };
                        n.Key = p;
                        n.R.Serial = 0;
                    }
                    var rules = new Rules(this, p);
                    if (n.R.Serial != 0 && SameKey(n.Key, p))
                    {
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        stats.Dst += n.R.Pending.Dst.Count; stats.Src += n.R.Pending.Src.Count; stats.Void += n.R.Pending.Void.Count; stats.Rows += n.R.Pending.OldRows.Count;
                        var rs = engine.Repair(n.R.Cells, n.R.Probers, n.R.Flags, n.R.Serial, n.R.Pending, rules, delta);
                        stats.Invalidated += rs.Invalidated; stats.Pops += rs.Pops; stats.Relaxed += rs.Relaxed; stats.Stale += rs.Stale; stats.Edges += rs.ChangedEdges;
                        stats.RepairTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                        delta.ApplyTo(n.R.Cells, n.R.Probers);
                        var reach = new List<int>();
                        delta.ApplyTo(n.R.Reach, reach);
                        n.R.Reach = reach;
                        stats.Repairs++;
                        if (delta.States.Count > 0) stats.ChangedRepairs++;
                        if (stats.Repairs % checkEvery == 0) Check(check, graph, n.R, fresh, p, rules, ref stats, ref serial);
                    }
                    else
                    {
                        // A pooled grid of its own each flood: new serial numbers, cleared.
                        n.R.Serial = Next(ref serial);
                        Array.Clear(n.R.Cells, 0, states);
                        Array.Clear(n.R.Probers, 0, cc);
                        n.R.Reach.Clear();
                        n.R.Root = p.Origin; n.R.Nav = p.NavType; n.R.RootFlags = p.Flags;
                        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                        engine.Flood(n.R.Cells, n.R.Probers, n.R.Flags, n.R.Serial, p.Origin, p.NavType, p.Flags, rules, n.R.Reach);
                        stats.FloodTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
                        n.R.Reach.Sort();
                        n.Key = p;
                        stats.Floods++;
                        stats.Found += n.R.Reach.Count;
                    }
                    n.R.Pending.Clear();
                }
            }
            return stats;
        }

        static ushort Next(ref ushort serial)
        {
            if (++serial == 0) serial = 1;
            return serial;
        }

        void Check(ProbeEngine check, ProbeGraph graph, ProbeRepairTests.Result r, ProbeRepairTests.Result fresh, Probe p, Rules rules, ref Stats stats, ref ushort serial)
        {
            fresh.Serial = Next(ref serial);
            Array.Clear(fresh.Cells, 0, fresh.Cells.Length);
            Array.Clear(fresh.Probers, 0, fresh.Probers.Length);
            fresh.Reach.Clear();
            fresh.Root = p.Origin;
            check.Flood(fresh.Cells, fresh.Probers, fresh.Flags, fresh.Serial, p.Origin, p.NavType, p.Flags, rules, fresh.Reach);
            fresh.Reach.Sort();
            stats.Checked++;
            var first = new List<int>();
            int wrong = ProbeEngine.ProberDifferences(r.Probers, r.Serial, fresh.Probers, fresh.Serial, cc, first);
            for (int s = 0; s < r.Cells.Length; s++)
            {
                var a = r.Cells[s]; var b = fresh.Cells[s];
                if (a.QueryId == r.Serial && b.QueryId == fresh.Serial && a.Cost == b.Cost
                    && (a.Parent != b.Parent || a.NavTypes != b.NavTypes || a.TransitionId != b.TransitionId)) stats.TiedStates++;
                if (a.QueryId == r.Serial) stats.States++;
            }
            string why = ProbeRepairTests.Fixpoint(r, graph, rules);
            if (wrong > 0 || why != null || !r.Reach.SequenceEqual(fresh.Reach))
            {
                stats.Wrong++;
                stats.FirstWrong ??= why ?? $"{wrong} cells, first {string.Join(", ", first)}";
            }
        }

        /// One frame's changes applied, as the tweak's change feed reports them.
        List<ProbeEvent> Apply(ProbeGraph graph, List<Ev> evs)
        {
            var batch = new List<ProbeEvent>();
            void Add(ProbeChange kind, int cell) => batch.Add(new ProbeEvent { Kind = kind, Cell = cell });
            foreach (var e in evs)
            {
                switch (e.Kind)
                {
                    case 'L':
                        batch.Add(new ProbeEvent { Kind = ProbeChange.Row, Cell = e.Cell, Row = e.Row, Old = graph.SetRow(e.Cell, e.Row) });
                        break;
                    case 'M':
                        {
                            int x = mask[e.Cell] ^ e.V;
                            if ((x & 5) != 0) Add(ProbeChange.Dst, e.Cell);
                            if ((x & 1) != 0) Add(ProbeChange.Void, e.Cell);
                            if ((x & 0x16) != 0) Add(ProbeChange.Src, e.Cell);
                            mask[e.Cell] = e.V;
                            break;
                        }
                    case 'Q':
                        {
                            int below = e.Cell - w;
                            bool a0 = Submerged(e.Cell), b0 = Submerged(below);
                            liq[e.Cell] = e.V;
                            if (Submerged(e.Cell) != a0) Add(ProbeChange.Dst, e.Cell);
                            if (below >= 0 && Submerged(below) != b0) Add(ProbeChange.Dst, below);
                            break;
                        }
                    case 'R':
                        foreach (int c in TableDiff(restrB, e.Table, 'R')) { Add(ProbeChange.Dst, c); Add(ProbeChange.Void, c); }
                        restrB = e.Table; restr = ParseRestr(restrB);
                        break;
                    case 'T':
                        foreach (int c in TableDiff(tubeB, e.Table, 'T')) Add(ProbeChange.Src, c);
                        tubeB = e.Table; tubes = ParseTubes(tubeB);
                        break;
                    case 'S':
                        foreach (int c in TableDiff(suitB, e.Table, 'S')) Add(ProbeChange.Src, c);
                        suitB = e.Table; suits = ParseSuits(suitB);
                        break;
                }
            }
            return batch;
        }
    }
}
