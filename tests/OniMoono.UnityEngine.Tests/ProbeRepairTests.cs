using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The path-probe-repair game tweak's search (src/OniMoono.GameTweaks/ProbeRepairCore.cs,
    /// compiled in here): a repaired probe result answers as a fresh flood does (the reachable
    /// cells and their costs, every state's cost and flags), over random worlds and random
    /// sequences of the changes the game makes (link rows, doors, liquid, tube entrances, suit
    /// markers, idle traversal), with MinionPathFinderAbilities' rules (ProbeModel).
    public class ProbeRepairTests
    {
        /// One navigator's result as the game's PathGrid holds it, with the tweak's flags.
        internal sealed class Result
        {
            public PCell[] Cells;
            public PProber[] Probers;
            public ProbeFlags Flags;
            /// The reference engine's flags (ProbeBench).
            public byte[] RefFlags;
            public ushort Serial;
            public List<int> Reach = new List<int>();
            public readonly ChangeSet Pending = new ChangeSet();
            public int Root;
            public byte Nav, RootFlags;
            public ProbeModel.Abilities Ab;
        }

        static Result Flood(ProbeEngine engine, ProbeModel model, int root, byte nav, byte flags, ProbeModel.Abilities ab, ushort serial)
        {
            int states = engine.Graph.CellCount * engine.Graph.NavCount;
            var r = new Result { Cells = new PCell[states], Probers = new PProber[engine.Graph.CellCount], Flags = new ProbeFlags(states), Serial = serial, Root = root, Nav = nav, RootFlags = flags, Ab = ab };
            engine.Flood(r.Cells, r.Probers, r.Flags, serial, root, nav, flags, model.Rules(ab), r.Reach);
            r.Reach.Sort();
            return r;
        }

        /// The repaired result against a fresh flood on the world now: the same answers
        /// everywhere (GetCost, the reachable cells), every state's cost and flags, and a parent
        /// chain BuildPath can follow (each parent a valid state of lower cost, down to the root).
        static void AssertSame(Result repaired, Result fresh, ProbeGraph graph, string where)
        {
            int nc = graph.NavCount;
            var bad = new List<int>();
            int n = ProbeEngine.ProberDifferences(repaired.Probers, repaired.Serial, fresh.Probers, fresh.Serial, fresh.Probers.Length, bad);
            Assert.True(n == 0, $"{where}: {n} cells answer otherwise, first {string.Join(", ", bad)}");
            Assert.Equal(fresh.Reach, repaired.Reach);
            for (int s = 0; s < fresh.Cells.Length; s++)
            {
                bool vr = repaired.Cells[s].QueryId == repaired.Serial, vf = fresh.Cells[s].QueryId == fresh.Serial;
                Assert.True(vr == vf, $"{where}: state {s} (cell {s / nc}) is {(vr ? "" : "not ")}reached in the repair, {(vf ? "" : "not ")}in the flood");
                if (!vr) continue;
                Assert.True(repaired.Cells[s].Cost == fresh.Cells[s].Cost, $"{where}: state {s} costs {repaired.Cells[s].Cost}, the flood's {fresh.Cells[s].Cost}");
                Assert.True(repaired.Flags[s] == fresh.Flags[s], $"{where}: state {s} has flags {repaired.Flags[s]}, the flood's {fresh.Flags[s]}");
                var c = repaired.Cells[s];
                if (c.Parent == -1) continue;
                int ps = c.Parent * nc + graph.NavSlot[c.NavTypes >> 4];
                Assert.True(repaired.Cells[ps].QueryId == repaired.Serial && repaired.Cells[ps].Cost < c.Cost,
                    $"{where}: state {s}'s parent {ps} is not a cheaper reached state");
            }
        }

        /// Null when r is what a flood can leave: every state set from its parent's state through
        /// a link of the parent's row as AddPotentials sets it (its cost and flags), no link from a
        /// state (but the one back to its parent) that would make another cheaper or reach one not
        /// reached, and each cell's prober entry its cheapest state but a tube's. Floods differ
        /// from one another only in which of two routes of the same cost they keep.
        internal static string Fixpoint(Result r, ProbeGraph g, IProbeRules rules)
        {
            int nc = g.NavCount;
            bool V(int s) => r.Cells[s].QueryId == r.Serial;
            for (int s = 0; s < r.Cells.Length; s++)
            {
                if (!V(s)) continue;
                var c = r.Cells[s];
                int cell = s / nc;
                byte nav = g.SlotNav[s % nc];
                if (c.Parent == -1)
                {
                    if (cell != r.Root || c.Cost != 0) return $"state {s} has no parent";
                }
                else
                {
                    byte pnav = (byte)(c.NavTypes >> 4);
                    int ps = c.Parent * nc + g.NavSlot[pnav];
                    if (!V(ps)) return $"state {s}'s parent {ps} is not reached";
                    var p = r.Cells[ps];
                    bool found = false;
                    foreach (var l in g.Rows[c.Parent])
                    {
                        if (l.Start != pnav || l.End != nav || l.Trans != c.TransitionId || l.Link != cell) continue;
                        found = true;
                        if (l.Link == p.Parent && (p.NavTypes >> 4) == l.Start) return $"state {s}'s parent {ps} skips the link";
                        bool sub = rules.IsSubmerged(l.Link);
                        int cand = p.Cost + l.Cost + (sub || l.End == ProbeGraph.Swim ? rules.Penalty(r.Flags[ps], l) : 0);
                        byte pf = r.Flags[ps];
                        if (cand != c.Cost) return $"state {s} costs {c.Cost}, {cand} from its parent {ps}";
                        if (!rules.Traverse(ref pf, l.Link, l.End, c.Parent, pnav, cand, l.Trans, sub)) return $"state {s}'s parent {ps} can't take the link";
                        if (pf != r.Flags[s]) return $"state {s} has flags {r.Flags[s]}, {pf} from its parent {ps}";
                    }
                    if (!found) return $"state {s}'s link from its parent {ps} is not in the row";
                }
                foreach (var l in g.Rows[cell])
                {
                    if (l.Start != nav || (l.Link == c.Parent && (c.NavTypes >> 4) == l.Start)) continue;
                    int t = l.Link * nc + g.NavSlot[l.End];
                    bool sub = rules.IsSubmerged(l.Link);
                    int cand = c.Cost + l.Cost + (sub || l.End == ProbeGraph.Swim ? rules.Penalty(r.Flags[s], l) : 0);
                    if (V(t) && r.Cells[t].Cost <= cand) continue;
                    byte pf = r.Flags[s];
                    if (rules.Traverse(ref pf, l.Link, l.End, cell, nav, cand, l.Trans, sub))
                        return V(t) ? $"state {t} costs {r.Cells[t].Cost}, {cand} from {s}" : $"state {t} is not reached, but {s} reaches it";
                }
            }
            for (int cell = 0; cell < r.Probers.Length; cell++)
            {
                int best = -1;
                for (int k = 0; k < nc; k++)
                    if (g.SlotNav[k] != ProbeGraph.Tube && V(cell * nc + k) && (best == -1 || r.Cells[cell * nc + k].Cost < best)) best = r.Cells[cell * nc + k].Cost;
                var p = r.Probers[cell];
                if ((p.QueryId == r.Serial) != (best != -1) || (best != -1 && p.Cost != best)) return $"cell {cell}'s prober entry is not its cheapest state";
                if ((best != -1) != (r.Reach.BinarySearch(cell) >= 0)) return $"cell {cell} is {(best != -1 ? "" : "not ")}reachable but {(best != -1 ? "not " : "")}listed";
            }
            return null;
        }

        static void Repair(ProbeEngine engine, ProbeModel model, Result r, ProbeDelta delta)
        {
            engine.Repair(r.Cells, r.Probers, r.Flags, r.Serial, r.Pending, model.Rules(r.Ab), delta);
            delta.ApplyTo(r.Cells, r.Probers);
            var reach = new List<int>();
            delta.ApplyTo(r.Reach, reach);
            r.Reach = reach;
            r.Pending.Clear();
        }

        public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 40).Select(i => new object[] { i });

        /// Random worlds where routes of the same cost lead to the same costs (ProbeModel.Allowed):
        /// the repair answers exactly as a flood (every state's cost and flags, the reachable
        /// cells).
        [Theory]
        [MemberData(nameof(Seeds))]
        public void RepairsAnswerAsAFreshFlood(int seed) => Run(seed, suitMarkers: false, strict: true);

        /// With suit markers, two routes of the same cost can carry other flags (one went by a
        /// suit dock), and which one a flood keeps depends on its queue: the repair and a flood
        /// may then keep different ones, and what follows from them differs. The repair must still
        /// be what a flood can leave (Fixpoint).
        [Theory]
        [MemberData(nameof(Seeds))]
        public void RepairsWithSuitMarkersAreFloodResults(int seed) => Run(seed, suitMarkers: true, strict: false);

        static void Run(int seed, bool suitMarkers, bool strict)
        {
            var rng = new Random(seed);
            var model = ProbeModel.Random(rng, 14, 11, suitMarkers);
            var graph = model.Graph();
            var engine = new ProbeEngine(graph);
            var delta = new ProbeDelta();
            var results = new List<Result>();
            for (int i = 0; i < 4; i++)
            {
                var ab = new ProbeModel.Abilities { Prefab = 100 + i, Proxy = 200 + i, Key = 300 + (i & 1), Swim = i % 2 == 0, FuelOut = i == 3, Idle = i == 1 };
                int root = model.RandomFloorCell(rng);
                results.Add(Flood(engine, model, root, ProbeModel.Floor, (byte)(i == 2 ? 8 | 1 : 8), ab, (ushort)(10 + i)));
            }
            int repairs = 0, changed = 0;
            for (int step = 0; step < 150; step++)
            {
                var batch = model.RandomChanges(rng, 1 + rng.Next(4), results.Select(r => r.Ab).ToArray());
                foreach (var e in batch.Events)
                {
                    var ev = e;
                    if (ev.Kind == ProbeChange.Row) ev.Old = graph.SetRow(ev.Cell, ev.Row);
                    foreach (var r in results) r.Pending.Add(ev);
                }
                // As the game probes: a few navigators a frame, the others later with more changes.
                foreach (var r in results)
                {
                    if (rng.Next(3) != 0) continue;
                    var before = (PCell[])r.Cells.Clone();
                    var info = $"src [{string.Join(",", r.Pending.Src)}] dst [{string.Join(",", r.Pending.Dst)}] void [{string.Join(",", r.Pending.Void)}] rows [{string.Join(",", r.Pending.OldRows.Keys)}]";
                    Repair(engine, model, r, delta);
                    var wf = Fixpoint(r, graph, model.Rules(r.Ab));
                    if (wf != null)
                    {
                        int st = int.Parse(System.Text.RegularExpressions.Regex.Match(wf, @"state (\d+)").Groups[1].Value);
                        var bc = before[st]; var ac = r.Cells[st];
                        throw new Exception($"step {step}: {wf}; {info}; before: valid {bc.QueryId == r.Serial} cost {bc.Cost} parent {bc.Parent} nav {bc.NavTypes:x} trans {bc.TransitionId}; after: cost {ac.Cost} parent {ac.Parent} nav {ac.NavTypes:x} trans {ac.TransitionId}; in delta {delta.States.Contains(st)}; nc {graph.NavCount} W {model.W}");
                    }
                    repairs++;
                    if (delta.States.Count > 0) changed++;
                    var fresh = Flood(new ProbeEngine(model.Graph()), model, r.Root, r.Nav, r.RootFlags, r.Ab, 1);
                    var why = Fixpoint(fresh, graph, model.Rules(r.Ab));
                    Assert.True(why == null, $"seed {seed} step {step}: the flood: {why}");
                    why = Fixpoint(r, graph, model.Rules(r.Ab));
                    Assert.True(why == null, $"seed {seed} step {step}: the repair: {why}");
                    if (strict) AssertSame(r, fresh, graph, $"seed {seed} step {step}");
                }
            }
            Assert.True(repairs > 100 && changed >= 5, $"{repairs} repairs, {changed} changed something");
        }

        [Fact]
        public void ARepairWithNothingChangedChangesNothing()
        {
            var rng = new Random(42);
            var model = ProbeModel.Random(rng, 12, 10);
            var graph = model.Graph();
            var engine = new ProbeEngine(graph);
            var r = Flood(engine, model, model.RandomFloorCell(rng), ProbeModel.Floor, 8, new ProbeModel.Abilities { Prefab = 1, Proxy = 2, Key = 3 }, 5);
            var delta = new ProbeDelta();
            engine.Repair(r.Cells, r.Probers, r.Flags, r.Serial, new ChangeSet(), model.Rules(r.Ab), delta);
            Assert.Empty(delta.States);
            Assert.Empty(delta.ProberCells);
            Assert.Empty(delta.Newly);
            Assert.Empty(delta.Gone);
        }

        /// Opening and closing a door (as its access control does) in front of the root: the
        /// cells behind it go and come back with the flood's costs.
        [Fact]
        public void ADoorClosingAndOpeningIsFollowed()
        {
            var rng = new Random(7);
            var model = ProbeModel.Corridor(20);
            var graph = model.Graph();
            var engine = new ProbeEngine(graph);
            var ab = new ProbeModel.Abilities { Prefab = 1, Proxy = 2, Key = 3 };
            var r = Flood(engine, model, model.CorridorCell(0), ProbeModel.Floor, 8, ab, 3);
            Assert.Equal(20, r.Reach.Count);
            var delta = new ProbeDelta();
            foreach (bool closed in new[] { true, false, true, false })
            {
                foreach (var e in model.SetDoor(10, closed ? ProbeModel.Block : (byte)0, ab.Proxy).Events) r.Pending.Add(e);
                Repair(engine, model, r, delta);
                Assert.Equal(closed ? 10 : 20, r.Reach.Count);
                var fresh = Flood(new ProbeEngine(model.Graph()), model, model.CorridorCell(0), ProbeModel.Floor, 8, ab, 1);
                AssertSame(r, fresh, graph, closed ? "closed" : "open");
            }
        }

        /// A repair past its budget gives up and leaves everything as it was; the next repair
        /// (with the same changes) is as one that never gave up.
        [Fact]
        public void ARepairPastItsBudgetGivesUpAndChangesNothing()
        {
            var model = ProbeModel.Corridor(20);
            var graph = model.Graph();
            var engine = new ProbeEngine(graph);
            var ab = new ProbeModel.Abilities { Prefab = 1, Proxy = 2, Key = 3 };
            var r = Flood(engine, model, model.CorridorCell(0), ProbeModel.Floor, 8, ab, 3);
            var cells = (PCell[])r.Cells.Clone();
            var flags = Enumerable.Range(0, r.Cells.Length).Select(s => r.Flags[s]).ToArray();
            foreach (var e in model.SetDoor(10, ProbeModel.Block, ab.Proxy).Events) r.Pending.Add(e);
            var delta = new ProbeDelta();
            delta.States.Add(1);
            var stats = engine.Repair(r.Cells, r.Probers, r.Flags, r.Serial, r.Pending, model.Rules(r.Ab), delta, budget: 4);
            Assert.True(stats.GaveUp);
            Assert.Empty(delta.States);
            Assert.Empty(delta.Newly);
            Assert.Empty(delta.Gone);
            Assert.Equal(cells, r.Cells);
            Assert.Equal(flags, Enumerable.Range(0, r.Cells.Length).Select(s => r.Flags[s]).ToArray());
            Repair(engine, model, r, delta);
            Assert.Equal(10, r.Reach.Count);
            var fresh = Flood(new ProbeEngine(model.Graph()), model, model.CorridorCell(0), ProbeModel.Floor, 8, ab, 1);
            AssertSame(r, fresh, graph, "after giving up");
        }

        [Fact]
        public void DeltaReachMergeKeepsOrder()
        {
            var d = new ProbeDelta();
            d.Newly.AddRange(new[] { 1, 5, 9 });
            d.Gone.AddRange(new[] { 2, 8 });
            var into = new List<int>();
            d.ApplyTo(new List<int> { 2, 3, 8, 10 }, into);
            Assert.Equal(new[] { 1, 3, 5, 9, 10 }, into);
        }

        [Fact]
        public void StandsDownForAnotherOwnersPatch()
        {
            IEnumerable<string> Owners(params string[] o) => o;
            Assert.Null(ProbeRepairPolicy.Foreign(new[]
            {
                new KeyValuePair<string, IEnumerable<string>>("AsyncPathProber.WorkOrder.Execute", Owners(ProbeRepairPolicy.Owner)),
                new KeyValuePair<string, IEnumerable<string>>("NavGrid.UpdateGraph", Owners(ProbeRepairPolicy.Owner, "PerfProbe", "PerfProbe.Frames")),
                new KeyValuePair<string, IEnumerable<string>>("PathProber.Run", Owners()),
            }));
            Assert.Equal("PeterHan.FastTrack patches Navigator.TakeResult", ProbeRepairPolicy.Foreign(new[]
            {
                new KeyValuePair<string, IEnumerable<string>>("AsyncPathProber.WorkOrder.Execute", Owners(ProbeRepairPolicy.Owner)),
                new KeyValuePair<string, IEnumerable<string>>("Navigator.TakeResult", Owners(ProbeRepairPolicy.Owner, "PeterHan.FastTrack")),
            }));
        }
    }
}
