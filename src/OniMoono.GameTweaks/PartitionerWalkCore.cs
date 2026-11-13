using System;
using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// The scene partitioner's node walks without game types (unit-tested), for
    /// fast-partitioner-cleanup and fast-partitioner-events. A node's entries are a list with an
    /// index of each entry's place (HybridListHashSet), and a removal moves the list's last entry
    /// into the freed place (RemoveAtSwap).
    public static class PartitionerWalkCore
    {
        /// The places of the handles in `removed` that are still in the node (`place`: handle to
        /// its index) and freed, once each, highest first. Dropping them in that order leaves the
        /// list as the game's descending walk does: each removal swaps in the list's last entry,
        /// which the walk has already passed, so every place below is untouched.
        public static void FreedPlaces<H>(Dictionary<H, int> place, List<H> removed, Func<H, bool> freed, List<(int Place, H Handle)> dropping)
        {
            dropping.Clear();
            foreach (var handle in removed)
                if (place.TryGetValue(handle, out int at) && freed(handle) && !dropping.Contains((at, handle))) dropping.Add((at, handle));
            dropping.Sort((a, b) => b.Place.CompareTo(a.Place));
        }

        /// The game's walk of a node when no entry has a callback (ScenePartitioner's
        /// TriggerEventInternal, GatherEntries and Sim1000ms alike): from the last entry down, each
        /// freed one removed by swapping the list's last entry into its place.
        public static void Walk<H>(List<H> list, Func<H, bool> freed)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (freed(list[i]))
                {
                    list[i] = list[list.Count - 1];
                    list.RemoveAt(list.Count - 1);
                }
        }

        /// The node extents TriggerEventInternal walks, as the game clamps them: GetNodeExtents
        /// (each corner's node, clamped to the grid), then the loop bounds clamped again.
        public static (int Y0, int Y1, int X0, int X1) WalkedNodes(int x, int y, int width, int height, int nodeSize, int nodesY, int nodesX)
        {
            int ClampX(int v) => Math.Min(Math.Max(v, 0), nodesX - 1);
            int ClampY(int v) => Math.Min(Math.Max(v, 0), nodesY - 1);
            int ex = ClampX(x / nodeSize), ey = ClampY(y / nodeSize);
            int ew = 1 + ClampX((x + width) / nodeSize) - ex, eh = 1 + ClampY((y + height) / nodeSize) - ey;
            return (Math.Max(ey, 0), Math.Min(ey + eh, nodesY), Math.Max(ex, 0), Math.Min(ex + ew, nodesX));
        }
    }
}
