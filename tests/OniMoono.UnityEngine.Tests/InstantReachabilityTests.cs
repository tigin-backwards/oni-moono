using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using OniMoono.Engine;
using OniMoono.GameTweaks;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// The instant-reachability game tweak's parts that need no game: the table's crossings, the
    /// per-cell index, which objects keep the game's polling, and the engine's move flags.
    public class InstantReachabilityTests(ITestOutputHelper output) : EngineTest
    {
        [Fact]
        public void OnlyZeroCrossingsAreRecorded()
        {
            var table = new int[8];
            var c = new ReachCrossings(table.Length);
            c.IncrementST(table, 2);     // 0 → 1
            c.IncrementST(table, 2);     // 1 → 2: still reachable
            c.Increment(table, 3);       // 0 → 1
            c.DecrementST(table, 2);     // 2 → 1
            Assert.Equal(new[] { 2, 3 }, c.Take().OrderBy(x => x));
            Assert.Empty(c.Take());
            c.Decrement(table, 2);       // 1 → 0
            c.Increment(table, 2);       // 0 → 1 again: one recheck, which finds it reachable
            Assert.Equal(new[] { 2 }, c.Take());
            Assert.Equal(new[] { 0, 0, 1, 1, 0, 0, 0, 0 }, table);
        }

        /// Writers on several threads, as Navigator probes and SolidTransferArm's async update
        /// write: whatever their interleaving, every cell whose "count > 0" differs from the last
        /// Take's is in the next one.
        [Fact]
        public void NoCrossingIsMissedAcrossThreads()
        {
            const int cells = 64;
            var table = new int[cells];
            var c = new ReachCrossings(cells);
            var rng = new System.Random(7);
            for (int round = 0; round < 200; round++)
            {
                var before = table.Select(n => n > 0).ToArray();
                // Each worker occupies some cells, then vacates what it occupied (Occupy before
                // Vacate, as the game's writers do), leaving some occupied for the next round.
                var plans = Enumerable.Range(0, 6).Select(_ => Enumerable.Range(0, 20).Select(__ => rng.Next(cells)).ToArray()).ToArray();
                var keep = plans.Select(p => rng.Next(p.Length)).ToArray();
                Parallel.For(0, plans.Length, w =>
                {
                    foreach (int cell in plans[w]) c.Increment(table, cell);
                    for (int i = keep[w]; i < plans[w].Length; i++) c.Decrement(table, plans[w][i]);
                });
                var taken = new HashSet<int>(c.Take());
                for (int cell = 0; cell < cells; cell++)
                    if ((table[cell] > 0) != before[cell]) Assert.Contains(cell, taken);
                // Vacate the rest now and then, so cells go back to 0 too.
                if (round % 5 == 4)
                {
                    for (int cell = 0; cell < cells; cell++) while (table[cell] > 0) c.DecrementST(table, cell);
                    c.Take();
                }
            }
        }

        /// What Gather finds is exactly the objects some of whose possible reads (their cell, and
        /// cell + dx + dy·width for each (dx, dy) in their bounds, Grid.OffsetCell's arithmetic,
        /// wrapping across a row's end) are the crossed cell.
        [Fact]
        public void GatherFindsExactlyTheObjectsReadingTheCell()
        {
            const int width = 9, height = 7, cellCount = width * height;
            var rng = new System.Random(3);
            var index = new ReachIndex(cellCount, width);
            var objects = new List<(int Slot, int Cell, OffsetBounds Bounds)>();
            for (int i = 0; i < 60; i++)
            {
                var b = OffsetBounds.Origin;
                b.Include(rng.Next(-3, 1), rng.Next(-2, 1));
                b.Include(rng.Next(0, 4), rng.Next(0, 3));
                int cell = rng.Next(cellCount);
                objects.Add((index.Add(i, cell, b), cell, b));
            }
            // Some move, some leave.
            for (int i = 0; i < 15; i++)
            {
                int k = rng.Next(objects.Count);
                int to = rng.Next(cellCount);
                index.Move(objects[k].Slot, to);
                objects[k] = (objects[k].Slot, to, objects[k].Bounds);
            }
            for (int i = 0; i < 10; i++)
            {
                int k = rng.Next(objects.Count);
                index.Remove(objects[k].Slot);
                objects.RemoveAt(k);
            }
            Assert.Equal(objects.Count, index.Count);
            for (int crossed = 0; crossed < cellCount; crossed++)
            {
                index.NewPass();
                var found = new List<int>();
                index.Gather(crossed, found);
                var expected = objects.Where(o => Reads(o.Cell, o.Bounds, width, cellCount).Contains(crossed)).Select(o => o.Slot).OrderBy(s => s);
                Assert.Equal(expected, found.OrderBy(s => s));
                Assert.Equal(found.Count, found.Distinct().Count());
            }
        }

        static HashSet<int> Reads(int cell, OffsetBounds b, int width, int cellCount)
        {
            var reads = new HashSet<int>();
            for (int dy = b.MinY; dy <= b.MaxY; dy++)
                for (int dx = b.MinX; dx <= b.MaxX; dx++)
                {
                    int c = cell + dx + dy * width;
                    if (c >= 0 && c < cellCount) reads.Add(c);
                }
            return reads;
        }

        /// An offset off the end of a row reads the next row's first cell, as Grid.OffsetCell
        /// does: a crossing there must find the object.
        [Fact]
        public void OffsetsWrappingARowAreFollowed()
        {
            const int width = 10;
            var index = new ReachIndex(width * 4, width);
            var b = OffsetBounds.Origin;
            b.Include(1, 0);
            int slot = index.Add("edge", 1 * width + (width - 1), b); // x 9, y 1
            index.NewPass();
            var found = new List<int>();
            index.Gather(2 * width + 0, found); // x 0, y 2: cell + 1
            Assert.Equal(new[] { slot }, found);
            found.Clear();
            index.NewPass();
            index.Gather(1 * width + 0, found); // x 0, y 1: not read
            Assert.Empty(found);
        }

        [Fact]
        public void UnknownCellsOffsetsAndTrackersKeepTheGamesPolling()
        {
            Assert.True(ReachCoverage.Covered(ReachCoverage.Cell("Pickupable"), ReachCoverage.Offsets("Workable"), ReachCoverage.Tracker("OffsetTableTracker")));
            Assert.True(ReachCoverage.Covered(ReachCoverage.Cell("Workable"), ReachCoverage.Offsets("Workable"), ReachCoverage.Tracker("StandardOffsetTracker")));
            Assert.True(ReachCoverage.Covered(ReachCoverage.Cell("Diggable"), ReachCoverage.Offsets("Workable"), ReachCoverage.Tracker(null)));
            // Unsealable's offsets are its facing: its tracker doesn't matter.
            Assert.True(ReachCoverage.Covered(ReachCoverage.Cell("Workable"), ReachCoverage.Offsets("Unsealable"), ReachCoverage.Tracker("(mod)")));
            // A mod's GetCell or GetOffsets override, or tracker subclass.
            Assert.False(ReachCoverage.Covered(ReachCoverage.Cell(null), ReachCoverage.Offsets("Workable"), ReachCoverage.Tracker("OffsetTableTracker")));
            Assert.False(ReachCoverage.Covered(ReachCoverage.Cell("Pickupable"), ReachCoverage.Offsets(null), ReachCoverage.Tracker("OffsetTableTracker")));
            Assert.False(ReachCoverage.Covered(ReachCoverage.Cell("Pickupable"), ReachCoverage.Offsets("Workable"), ReachCoverage.Tracker("(mod)")));
            Assert.False(ReachCoverage.Covered(ReachCoverage.Cell("SomeModWorkable"), ReachCoverage.Offsets("Workable"), ReachCoverage.Tracker(null)));
        }

        static List<object> Drain()
        {
            var got = new List<object>();
            TransformMoveWatch.Drain(got.Add);
            return got;
        }

        /// The engine's move flags: any change of a watched transform's place, its own or an
        /// ancestor's, raw or not, queues its token once until the next drain; others queue nothing.
        [Fact]
        public void WatchedTransformsReportEveryMoveOnce()
        {
            TransformMoveWatch.Clear();
            var parent = new GameObject("parent").transform;
            var child = new GameObject("child").transform;
            child.SetParent(parent);
            var other = new GameObject("other").transform;
            TransformMoveWatch.Watch(child.TransformNative, "child");
            Drain();

            other.position = new Vector3(5, 5, 0);
            Assert.Empty(Drain());
            child.position = new Vector3(1, 2, 0);       // raw world set
            child.localPosition = new Vector3(3, 2, 0);  // raw local set: still one report
            Assert.Equal(new object[] { "child" }, Drain());
            parent.position = new Vector3(10, 0, 0);     // an ancestor's move
            Assert.Equal(new object[] { "child" }, Drain());
            child.SetParent(other, true);                 // a parent change
            Assert.Equal(new object[] { "child" }, Drain());
            child.position = child.position;              // no change of place
            Assert.Empty(Drain());
            TransformMoveWatch.Unwatch(child.TransformNative);
            child.position = new Vector3(0, 0, 0);
            Assert.Empty(Drain());
        }

        /// The flag's cost where it is clear (ONI_MOVE_BENCH=1, else skipped): position and
        /// localPosition sets and SetParent, ns each; run before and after the flag for the
        /// comparison.
        [Fact]
        public void MoveFlagBench()
        {
            if (Environment.GetEnvironmentVariable("ONI_MOVE_BENCH") != "1") Assert.Skip("ONI_MOVE_BENCH=1 runs it");
            var root = new GameObject("root").transform;
            var leaf = new GameObject("leaf").transform;
            leaf.SetParent(root);
            var a = new GameObject("a").transform;
            var b = new GameObject("b").transform;
            const int n = 2_000_000;
            double Time(Action<int> body)
            {
                double best = double.MaxValue;
                for (int rep = 0; rep < 7; rep++)
                {
                    var w = Stopwatch.StartNew();
                    for (int i = 0; i < n; i++) body(i);
                    best = Math.Min(best, w.Elapsed.TotalMilliseconds * 1e6 / n);
                }
                return best;
            }
            var p0 = new Vector3(1, 2, 0); var p1 = new Vector3(2, 1, 0);
            double pos = Time(i => leaf.position = (i & 1) == 0 ? p0 : p1);
            double local = Time(i => leaf.localPosition = (i & 1) == 0 ? p0 : p1);
            double parentMove = Time(i => root.position = (i & 1) == 0 ? p0 : p1);
            double setParent = Time(i => leaf.SetParent((i & 1) == 0 ? a : b, false));
            output.WriteLine($"position {pos:F2} ns, localPosition {local:F2} ns, parent's position (one child) {parentMove:F2} ns, SetParent {setParent:F2} ns");
        }
    }
}
