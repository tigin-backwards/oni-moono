using System;
using System.Collections.Generic;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The scene partitioner's node walks (src/OniMoono.GameTweaks/PartitionerWalkCore.cs), for
    /// fast-partitioner-cleanup and fast-partitioner-events: dropping a node's recorded freed
    /// handles, highest place first, leaves its list as the game's descending walk does, whatever
    /// else the record holds; and the nodes walked are the game's.
    public class PartitionerWalkTests
    {
        /// HybridListHashSet as the game has it: a list and each entry's place, removal by swap.
        sealed class Node
        {
            public readonly List<int> List = new();
            public readonly Dictionary<int, int> Place = new();

            public void Add(int h) { if (Place.TryAdd(h, List.Count)) List.Add(h); }

            public void Remove(int h)
            {
                if (!Place.TryGetValue(h, out int at)) return;
                Place.Remove(h);
                List[at] = List[List.Count - 1];
                List.RemoveAt(List.Count - 1);
                if (at < List.Count) Place[List[at]] = at;
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void DroppingRecordedHandlesMatchesTheWalk(int seed)
        {
            var random = new Random(seed);
            for (int round = 0; round < 300; round++)
            {
                var node = new Node();
                int n = random.Next(0, 400);
                for (int i = 0; i < n; i++) node.Add(random.Next(100000));
                var freed = new HashSet<int>();
                var removed = new List<int>();
                foreach (var h in node.List)
                    if (random.Next(5) == 0) { freed.Add(h); removed.Add(h); }
                // The record also holds handles already gone from this node, ones removed twice,
                // and live ones (ScenePartitioner.Remove without a free).
                for (int i = 0; i < 10; i++) removed.Add(100000 + random.Next(1000));
                if (removed.Count > 0) removed.Add(removed[random.Next(removed.Count)]);
                if (node.List.Count > 0) removed.Add(node.List[random.Next(node.List.Count)]);
                foreach (var h in removed) if (h >= 100000) freed.Add(h);

                var walked = new List<int>(node.List);
                PartitionerWalkCore.Walk(walked, h => freed.Contains(h));

                var dropping = new List<(int Place, int Handle)>();
                PartitionerWalkCore.FreedPlaces(node.Place, removed, h => freed.Contains(h), dropping);
                foreach (var (_, h) in dropping) node.Remove(h);

                Assert.Equal(walked, node.List);
            }
        }

        /// TriggerEventInternal's node bounds as the game writes them (GetNodeExtents, the clamps).
        static (int, int, int, int) Games(int x, int y, int width, int height, int nodeSize, int nodesY, int nodesX)
        {
            int ClampNodeX(int v) => Math.Min(Math.Max(v, 0), nodesX - 1);
            int ClampNodeY(int v) => Math.Min(Math.Max(v, 0), nodesY - 1);
            int ex = ClampNodeX(x / nodeSize), ey = ClampNodeY(y / nodeSize);
            int ew = 1 + ClampNodeX((x + width) / nodeSize) - ex, eh = 1 + ClampNodeY((y + height) / nodeSize) - ey;
            int num = Math.Min(ey + eh, nodesY), num2 = Math.Max(ey, 0), num3 = Math.Max(ex, 0), num4 = Math.Min(ex + ew, nodesX);
            return (num2, num, num3, num4);
        }

        [Fact]
        public void WalksTheGamesNodes()
        {
            var random = new Random(3);
            for (int i = 0; i < 20000; i++)
            {
                int x = random.Next(-20, 400), y = random.Next(-20, 400), w = random.Next(1, 40), h = random.Next(1, 40);
                Assert.Equal(Games(x, y, w, h, 16, 24, 20), PartitionerWalkCore.WalkedNodes(x, y, w, h, 16, 24, 20));
            }
            Assert.Equal((0, 2, 0, 2), PartitionerWalkCore.WalkedNodes(15, 15, 1, 1, 16, 24, 20));
            Assert.Equal((0, 1, 0, 1), PartitionerWalkCore.WalkedNodes(14, 14, 1, 1, 16, 24, 20));
        }
    }
}
