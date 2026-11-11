using System;

namespace OniMoono.Engine.UI
{
    /// The drawing order of an overlay canvas's draws that lets more of them merge, with the same
    /// pixels (docs/research/load-screen-many-saves.md, "A design that keeps the pixels exact").
    ///
    /// Two draws can swap places without changing a pixel when no pixel is touched by both:
    /// blending, depth and stencil all act on one pixel at a time. So each draw, in hierarchy
    /// order, gets a layer: one above the highest layer of an earlier draw of another state whose
    /// pixels it shares, or that draw's layer when the state is the same (it can join it). The
    /// draws are then sorted by layer, then by the state's first appearance in the layer, then by
    /// their order, and consecutive draws of the same state merge as before. Any two draws that
    /// share a pixel keep their order: of different states, the later one is on a higher layer; of
    /// the same state on the same layer, they are in one group in their order. Unity's canvas
    /// batching orders draws so (the original draws the Load screen's colony view of 715 saves in
    /// about 61 draws).
    ///
    /// A draw's pixels are given as the inclusive ranges of the pixel centres its geometry's
    /// bounds can reach (Covers); a draw that reaches none (off the screen, or thinner than a
    /// pixel between two centres) shares no pixel with any. A barrier (a stencil write or pop, a
    /// draw whose pixels are not known from its geometry) keeps its place: the draws before it are
    /// ordered among themselves, then it, then those after it.
    ///
    /// Overlap tests against every earlier draw would cost O(n²) (5,070 draws: 13 million tests),
    /// so draws are kept in a grid of the target's cells: a draw tests only the draws of the cells
    /// it covers, each once, and skips a cell whose highest layer could not raise its own. The
    /// arrays are kept from call to call: no allocation once they are large enough.
    internal sealed class CanvasLayering
    {
        /// A draw's state that is a barrier.
        public const int Barrier = -1;
        /// Cell side in pixels.
        public const int CellSize = 64;

        /// The draws: their number, each one's state (a dense id from 0 below StateCount, or
        /// Barrier) and pixel-centre ranges (X0 > X1 or Y0 > Y1: none).
        public int Count, StateCount;
        public int[] State = [], X0 = [], Y0 = [], X1 = [], Y1 = [];
        /// The result: the draws' indices in drawing order.
        public int[] Order = [];
        /// Each draw's layer (tests; diagnostics).
        public int[] Layer = [];

        int[] seenBy = [], nextInGroup = [], layerStart = [], byLayer = [];
        int[] groupStamp = [], groupHead = [], groupTail = [], groupOrder = [];
        int[] cellHead = [], cellStamp = [], cellMax = [];
        int[] entryNext = [], entryDraw = [];
        int cols, rows, epoch, groupEpoch;

        /// Room for n draws of up to states states, their inputs cleared.
        public void Reset(int n, int states)
        {
            Count = n;
            StateCount = states;
            Grow(ref State, n);
            Grow(ref X0, n);
            Grow(ref Y0, n);
            Grow(ref X1, n);
            Grow(ref Y1, n);
            Grow(ref Order, n);
            Grow(ref Layer, n);
            Grow(ref seenBy, n);
            Grow(ref nextInGroup, n);
            Grow(ref byLayer, n);
            Grow(ref layerStart, n + 2);
            Grow(ref groupStamp, states);
            Grow(ref groupHead, states);
            Grow(ref groupTail, states);
            Grow(ref groupOrder, states);
        }

        /// Makes room for at least `states` state ids after Reset (ids are given while filling).
        public void EnsureStates(int states)
        {
            if (states <= StateCount) return;
            StateCount = states;
            Grow(ref groupStamp, states);
            Grow(ref groupHead, states);
            Grow(ref groupTail, states);
            Grow(ref groupOrder, states);
        }

        static void Grow(ref int[] a, int n)
        {
            if (a.Length >= n) return;
            int size = Math.Max(n, a.Length * 2);
            var b = new int[size];
            // Stamps compare against counters that only grow: a fresh array reads as never stamped.
            a = b;
        }

        /// The pixel-centre ranges a draw can reach on a target width × height pixels, given its
        /// bounds in pixels: centres at k + 0.5, widened by slack for rounding (the device's and
        /// the rasterizer's sub-pixel grid). False when it reaches none.
        public static bool Covers(double minX, double minY, double maxX, double maxY, int width, int height, out int x0, out int y0, out int x1, out int y1)
        {
            const double Slack = 0.125;
            x0 = y0 = 0;
            x1 = y1 = -1;
            if (!(minX <= maxX && minY <= maxY)) return false; // NaN too
            double fx0 = Math.Ceiling(minX - 0.5 - Slack), fx1 = Math.Floor(maxX - 0.5 + Slack);
            double fy0 = Math.Ceiling(minY - 0.5 - Slack), fy1 = Math.Floor(maxY - 0.5 + Slack);
            fx0 = Math.Max(fx0, 0);
            fy0 = Math.Max(fy0, 0);
            fx1 = Math.Min(fx1, width - 1);
            fy1 = Math.Min(fy1, height - 1);
            if (fx0 > fx1 || fy0 > fy1) return false;
            x0 = (int)fx0;
            y0 = (int)fy0;
            x1 = (int)fx1;
            y1 = (int)fy1;
            return true;
        }

        /// Orders the draws (Order) for a target of width × height pixels.
        public void Run(int width, int height)
        {
            cols = Math.Max(1, (width + CellSize - 1) / CellSize);
            rows = Math.Max(1, (height + CellSize - 1) / CellSize);
            int cells = cols * rows;
            if (cellHead.Length < cells)
            {
                cellHead = new int[cells];
                cellStamp = new int[cells];
                cellMax = new int[cells];
                epoch = 0;
            }
            for (int i = 0; i < Count; i++) seenBy[i] = -1;
            int written = 0, from = 0;
            for (int i = 0; i < Count; i++)
            {
                if (State[i] != Barrier) continue;
                written = Segment(from, i, written);
                Layer[i] = 0;
                Order[written++] = i;
                from = i + 1;
            }
            Segment(from, Count, written);
        }

        /// Layers and orders draws [from, to), none a barrier, writing them to Order from `at`.
        int Segment(int from, int to, int at)
        {
            if (to <= from) return at;
            if (++epoch == int.MaxValue)
            {
                Array.Clear(cellStamp);
                epoch = 1;
            }
            int entries = 0, maxLayer = 0;
            for (int i = from; i < to; i++)
            {
                int layer = 0;
                if (X0[i] <= X1[i] && Y0[i] <= Y1[i])
                {
                    int s = State[i];
                    int cx0 = X0[i] / CellSize, cx1 = X1[i] / CellSize, cy0 = Y0[i] / CellSize, cy1 = Y1[i] / CellSize;
                    for (int cy = cy0; cy <= cy1; cy++)
                        for (int cx = cx0; cx <= cx1; cx++)
                        {
                            int c = cy * cols + cx;
                            if (cellStamp[c] != epoch || cellMax[c] + 1 <= layer) continue;
                            for (int e = cellHead[c]; e >= 0; e = entryNext[e])
                            {
                                int j = entryDraw[e];
                                if (seenBy[j] == i) continue;
                                seenBy[j] = i;
                                if (X0[i] > X1[j] || X0[j] > X1[i] || Y0[i] > Y1[j] || Y0[j] > Y1[i]) continue;
                                int candidate = State[j] == s ? Layer[j] : Layer[j] + 1;
                                if (candidate > layer) layer = candidate;
                            }
                        }
                    int need = entries + (cx1 - cx0 + 1) * (cy1 - cy0 + 1);
                    if (entryNext.Length < need)
                    {
                        int size = Math.Max(need, entryNext.Length * 2);
                        Array.Resize(ref entryNext, size);
                        Array.Resize(ref entryDraw, size);
                    }
                    for (int cy = cy0; cy <= cy1; cy++)
                        for (int cx = cx0; cx <= cx1; cx++)
                        {
                            int c = cy * cols + cx;
                            if (cellStamp[c] != epoch)
                            {
                                cellStamp[c] = epoch;
                                cellHead[c] = -1;
                                cellMax[c] = -1;
                            }
                            entryDraw[entries] = i;
                            entryNext[entries] = cellHead[c];
                            cellHead[c] = entries++;
                            if (layer > cellMax[c]) cellMax[c] = layer;
                        }
                }
                Layer[i] = layer;
                if (layer > maxLayer) maxLayer = layer;
            }
            // A stable counting sort by layer.
            Array.Clear(layerStart, 0, maxLayer + 2);
            for (int i = from; i < to; i++) layerStart[Layer[i] + 1]++;
            for (int l = 0; l <= maxLayer; l++) layerStart[l + 1] += layerStart[l];
            for (int i = from; i < to; i++) byLayer[from + layerStart[Layer[i]]++] = i;
            // Within each layer, the draws of each state together, states in order of first
            // appearance, draws in their order.
            int k = from;
            while (k < to)
            {
                int layer = Layer[byLayer[k]];
                if (++groupEpoch == int.MaxValue)
                {
                    Array.Clear(groupStamp);
                    groupEpoch = 1;
                }
                int groups = 0;
                for (; k < to && Layer[byLayer[k]] == layer; k++)
                {
                    int i = byLayer[k], s = State[i];
                    nextInGroup[i] = -1;
                    if (groupStamp[s] != groupEpoch)
                    {
                        groupStamp[s] = groupEpoch;
                        groupHead[s] = groupTail[s] = i;
                        groupOrder[groups++] = s;
                    }
                    else
                    {
                        nextInGroup[groupTail[s]] = i;
                        groupTail[s] = i;
                    }
                }
                for (int g = 0; g < groups; g++)
                    for (int i = groupHead[groupOrder[g]]; i >= 0; i = nextInGroup[i])
                        Order[at++] = i;
            }
            return at;
        }
    }
}
