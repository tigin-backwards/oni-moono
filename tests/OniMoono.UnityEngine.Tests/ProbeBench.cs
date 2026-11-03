using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// path-probe-repair's cost on the two captures (ONI_PROBE_BENCH=1, else skipped): every
    /// navigator tracked, changes merged from a batch history as the worker does, timed per part
    /// (merge, repair, flood, the main thread's delta apply), best of ONI_PROBE_BENCH_REPS
    /// replays (default 5). A digest of every probe's states (cost, parent, nav types,
    /// transition, flags) shows whether a change to the search changed any result.
    public class ProbeBench(ITestOutputHelper output)
    {
        [Theory]
        [InlineData("probe-c100.ptc.gz")]
        [InlineData("probe-decrew.ptc.gz")]
        public void Replay(string file)
        {
            if (Environment.GetEnvironmentVariable("ONI_PROBE_BENCH") != "1") Assert.Skip("ONI_PROBE_BENCH=1 runs it");
            int reps = int.TryParse(Environment.GetEnvironmentVariable("ONI_PROBE_BENCH_REPS"), out int r) ? r : 5;
            bool digest = Environment.GetEnvironmentVariable("ONI_PROBE_BENCH_DIGEST") != "0";
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", file);
            // ONI_PROBE_BENCH_AB=1: replays alternate between the reference engine (as the tweak
            // first landed) and ProbeEngine, each with its digest.
            bool ab = Environment.GetEnvironmentVariable("ONI_PROBE_BENCH_AB") == "1";
            var best = new TraceReplay.BenchStats[2];
            var digests = new ulong[2];
            for (int i = 0; i < reps * (ab ? 2 : 1); i++)
            {
                int k = ab ? 1 - (i & 1) : 1;
                var t = TraceReplay.Load(path);
                var s = t.Bench(digest && i < (ab ? 2 : 1), reference: k == 0);
                output.WriteLine($"{file} {(k == 0 ? "ref" : "new")} #{i}: {s}");
                if (s.Digest != 0) digests[k] = s.Digest;
                if (s.Digest != 0 && k == 1)
                    output.WriteLine($"{file}: floods by how many flag values their states have: {string.Join(", ", s.FlagValues.Select((c, v) => (c, v)).Where(x => x.c > 0).Select(x => $"{x.v}: {x.c}"))}");
                if (best[k].Probes == 0 || s.Total < best[k].Total) best[k] = s;
            }
            for (int k = ab ? 0 : 1; k < 2; k++)
            {
                best[k].Digest = digests[k];
                output.WriteLine($"{file} {(k == 0 ? "ref" : "new")} best: {best[k]}");
            }
            if (ab && digest) Assert.Equal(digests[0], digests[1]);
            var log = best[1].RepairLog;
            log.Sort((a, b) => b.Us.CompareTo(a.Us));
            output.WriteLine($"{file} slowest repairs (us, changes, batches behind, dropped, pops, relaxed): {string.Join("; ", log.Take(8))}");
            double floodUs = best[1].FloodTicks * 1e6 / Stopwatch.Frequency / Math.Max(1, best[1].Floods);
            double all = log.Sum(x => x.Us);
            output.WriteLine($"{file} repairs {all / 1000:F1} ms; mean flood {floodUs:F0} us; repairs over a flood's time: {log.Count(x => x.Us > floodUs)} ({log.Where(x => x.Us > floodUs).Sum(x => x.Us) / 1000:F1} ms)");
            foreach (double k in new[] { 1.0, 2, 3, 4, 6, 8, 12, 16 })
            {
                // giving up once a repair's work (pops and drops) passes the reach over k, then flooding
                double sim = 0; int gave = 0;
                foreach (var x in log)
                {
                    double budget = x.Reach / k, work = x.Pops + x.Dropped;
                    if (work > budget) { sim += x.Us * budget / work + floodUs; gave++; }
                    else sim += x.Us;
                }
                output.WriteLine($"{file} give up at reach/{k}: {gave} floods instead, repairs+floods {sim / 1000:F1} ms");
            }
            foreach (var (lo, hi) in new[] { (0, 50), (50, 200), (200, 1000), (1000, 1000000) })
            {
                var b = log.Where(x => x.Changes >= lo && x.Changes < hi).ToList();
                if (b.Count > 0) output.WriteLine($"{file} changes {lo}-{hi}: {b.Count} repairs, mean {b.Average(x => x.Us):F0} us, max {b.Max(x => x.Us):F0} us, mean behind {b.Average(x => x.Behind):F1}");
            }
        }
    }

    sealed partial class TraceReplay
    {
        public struct BenchStats
        {
            public int Navs, Probes, Floods, Repairs, Large, Unchanged, GaveUp;
            public long SortTicks, MergeTicks, RepairTicks, FloodTicks, ApplyTicks, DeltaStates, MaxApplyTicks;
            public ulong Digest;
            public List<(double Us, int Changes, long Behind, int Dropped, int Pops, int Relaxed, int Reach)> RepairLog;
            public int[] FlagValues;
            public long Total => MergeTicks + RepairTicks + FloodTicks;

            static double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
            static double Us(long t, int n) => t * 1e6 / Stopwatch.Frequency / Math.Max(1, n);

            public override string ToString() =>
                $"{Navs} navs, {Probes} probes: {Floods} floods {Ms(FloodTicks):F1} ms ({Us(FloodTicks, Floods):F0} us), "
                + $"{Repairs} repairs {Ms(RepairTicks):F1} ms ({Us(RepairTicks, Repairs):F0} us; {Unchanged} unchanged, {Large} too large, {GaveUp} gave up), "
                + $"sort {Ms(SortTicks):F1} ms, merge {Ms(MergeTicks):F2} ms, total {Ms(Total):F1} ms; main-thread apply {Ms(ApplyTicks):F2} ms "
                + $"({Us(ApplyTicks, Repairs):F1} us, max {Us(MaxApplyTicks, 1):F0} us, {DeltaStates / Math.Max(1, Repairs)} states a repair); digest {Digest:x16}";
        }

        sealed class BenchNav
        {
            public ProbeRepairTests.Result R;
            public Probe Key;
            public long Seq;
        }

        public BenchStats Bench(bool digest, bool reference = false)
        {
            var stats = new BenchStats { RepairLog = new(), FlagValues = new int[33] };
            var graph = new ProbeGraph(w, cc, ml, navTypes, voids, rows);
            var engine = new ProbeEngine(graph);
            var refEngine = reference ? new ProbeReferenceEngine(graph) : null;
            var delta = new ProbeDelta();
            var navs = new Dictionary<int, BenchNav>();
            var history = new List<ProbeBatch>();
            var cs = new ChangeSet();
            long seq = 0;
            int states = cc * graph.NavCount;
            ushort serial = 0;
            ulong dig = 1469598103934665603UL;
            var found = new List<int>();
            // ONI_PROBE_BENCH_BUDGET=K: repairs give up at the reach over K (0: never); else ProbeEngine.Budget.
            int budgetDiv = int.TryParse(Environment.GetEnvironmentVariable("ONI_PROBE_BENCH_BUDGET"), out int bd) ? bd : -1;
            if (budgetDiv == -1 && Environment.GetEnvironmentVariable("ONI_PROBE_BENCH_AB") == "1") budgetDiv = 0;
            foreach (var (frame, evs) in frames)
            {
                var batch = Apply(graph, evs);
                if (batch.Count > 0)
                {
                    var b = new ProbeBatch { Seq = ++seq };
                    b.Events.AddRange(batch);
                    history.Add(b);
                }
                if (!byStart.TryGetValue(frame, out var probes)) continue;
                foreach (var p in probes)
                {
                    stats.Probes++;
                    if (!navs.TryGetValue(p.Nav, out var n))
                    {
                        navs[p.Nav] = n = new BenchNav { R = new ProbeRepairTests.Result { Cells = new PCell[states], Probers = new PProber[cc], Flags = new byte[states] } };
                        n.Key = p;
                    }
                    var rules = new Rules(this, p);
                    bool repair = false;
                    if (n.R.Serial != 0 && SameKey(n.Key, p))
                    {
                        long m0 = Stopwatch.GetTimestamp();
                        Collect(history, seq, n.Seq, cs);
                        stats.MergeTicks += Stopwatch.GetTimestamp() - m0;
                        if (cs.Count > graph.CellCount / 16) stats.Large++;
                        else repair = true;
                    }
                    long t0 = 0;
                    RepairStats rs = default;
                    if (repair)
                    {
                        t0 = Stopwatch.GetTimestamp();
                        if (cs.IsEmpty) { delta.Clear(); stats.Unchanged++; }
                        else if (reference) refEngine.Repair(n.R.Cells, n.R.Probers, n.R.Flags, n.R.Serial, cs, rules, delta);
                        else
                        {
                            int budget = budgetDiv > 0 ? Math.Max(n.R.Reach.Count, 1024) / budgetDiv : budgetDiv == 0 ? int.MaxValue : ProbeEngine.Budget(n.R.Reach.Count);
                            rs = engine.Repair(n.R.Cells, n.R.Probers, n.R.Flags, n.R.Serial, cs, rules, delta, budget);
                            if (rs.GaveUp)
                            {
                                stats.GaveUp++;
                                stats.RepairTicks += Stopwatch.GetTimestamp() - t0;
                                repair = false;
                            }
                        }
                    }
                    if (repair)
                    {
                        var reach = n.R.Reach;
                        int reachBefore = reach.Count;
                        var into = new List<int>(reach.Count + delta.Newly.Count);
                        delta.ApplyTo(reach, into);
                        n.R.Reach = into;
                        long took = Stopwatch.GetTimestamp() - t0;
                        stats.RepairTicks += took;
                        stats.RepairLog.Add((took * 1e6 / Stopwatch.Frequency, cs.Count, seq - n.Seq, rs.Invalidated, rs.Pops, rs.Relaxed, reachBefore));
                        long a0 = Stopwatch.GetTimestamp();
                        delta.ApplyTo(n.R.Cells, n.R.Probers);
                        long a = Stopwatch.GetTimestamp() - a0;
                        stats.ApplyTicks += a;
                        stats.MaxApplyTicks = Math.Max(stats.MaxApplyTicks, a);
                        stats.DeltaStates += delta.States.Count;
                        stats.Repairs++;
                    }
                    else
                    {
                        if (++serial == 0)
                        {
                            serial = 1;
                            foreach (var o in navs.Values) { Array.Clear(o.R.Cells); Array.Clear(o.R.Probers); o.R.Serial = 0; }
                        }
                        n.R.Serial = serial;
                        n.R.Reach.Clear();
                        long t1 = Stopwatch.GetTimestamp();
                        found.Clear();
                        if (reference) refEngine.Flood(n.R.Cells, n.R.Probers, n.R.Flags, serial, p.Origin, p.NavType, p.Flags, rules, found);
                        else engine.Flood(n.R.Cells, n.R.Probers, n.R.Flags, serial, p.Origin, p.NavType, p.Flags, rules, found);
                        // The worker's reachable cells, sorted (the reference: the found list sorted).
                        long s0 = Stopwatch.GetTimestamp();
                        if (reference) { n.R.Reach.AddRange(found); n.R.Reach.Sort(); }
                        else engine.SortedFound(n.R.Reach);
                        stats.SortTicks += Stopwatch.GetTimestamp() - s0;
                        stats.FloodTicks += Stopwatch.GetTimestamp() - t1;
                        n.Key = p;
                        stats.Floods++;
                        if (digest)
                        {
                            var seen = new HashSet<byte>();
                            for (int st = 0; st < states; st++) if (n.R.Cells[st].QueryId == serial) seen.Add(n.R.Flags[st]);
                            stats.FlagValues[Math.Min(32, seen.Count)]++;
                        }
                    }
                    n.Seq = seq;
                    if (digest) dig = Digest(dig, n.R, graph);
                }
            }
            stats.Navs = navs.Count;
            stats.Digest = digest ? dig : 0;
            return stats;
        }

        /// Worker.Collect: the batches after `since` merged.
        static void Collect(List<ProbeBatch> history, long seq, long since, ChangeSet cs)
        {
            cs.Clear();
            if (since == seq) return;
            int lo = 0, hi = history.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (history[mid].Seq <= since) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < history.Count; i++)
                foreach (var e in history[i].Events) cs.Add(e);
        }

        static ulong Digest(ulong h, ProbeRepairTests.Result r, ProbeGraph g)
        {
            var cells = r.Cells;
            for (int s = 0; s < cells.Length; s++)
            {
                var c = cells[s];
                if (c.QueryId != r.Serial) continue;
                h = Mix(h, ((ulong)(uint)s << 32) | (uint)c.Cost);
                h = Mix(h, ((ulong)(uint)c.Parent << 32) | ((ulong)c.NavTypes << 16) | ((ulong)c.TransitionId << 8) | r.Flags[s]);
            }
            foreach (int cell in r.Reach) h = Mix(h, ((ulong)(uint)cell << 32) | (uint)r.Probers[cell].Cost);
            return h;
        }
    }
}
