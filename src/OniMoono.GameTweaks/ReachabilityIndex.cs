using System;
using System.Collections.Generic;
using System.Threading;

namespace OniMoono.GameTweaks
{
    /// The cells of MinionGroupProber's table whose "some Duplicant can reach it" (count > 0)
    /// may have changed since the last Take: a count that went 0 → 1 or 1 → 0. The game's writers
    /// add (Occupy) before they remove (Vacate), so a cell that stays reachable through a probe
    /// swap never crosses; a crossing that is undone before the next Take is a recheck that finds
    /// nothing changed. Increment and Decrement are the Interlocked writers (workers: Navigator
    /// probes, SolidTransferArm's async update): each crossing is seen by exactly the thread whose
    /// write made it, from the Interlocked result, so none is missed however writers interleave.
    /// The *ST writers are the game's main-thread ones (plain ++/--). Plain .NET (unit tests).
    public sealed class ReachCrossings
    {
        readonly object gate = new object();
        readonly byte[] queued;
        List<int> pending = new List<int>(), spare = new List<int>();

        /// Every crossing recorded (one cell's several crossings between two Takes count once).
        public long Recorded;

        public ReachCrossings(int cellCount) => queued = new byte[cellCount];

        public void Increment(int[] table, int cell)
        {
            if (Interlocked.Increment(ref table[cell]) == 1) Record(cell);
        }

        public void Decrement(int[] table, int cell)
        {
            if (Interlocked.Decrement(ref table[cell]) == 0) Record(cell);
        }

        public void IncrementST(int[] table, int cell)
        {
            if (++table[cell] == 1) Record(cell);
        }

        public void DecrementST(int[] table, int cell)
        {
            if (--table[cell] == 0) Record(cell);
        }

        void Record(int cell)
        {
            lock (gate)
            {
                if (queued[cell] != 0) return;
                queued[cell] = 1;
                pending.Add(cell);
                Recorded++;
            }
        }

        /// The cells crossed since the last Take, each once. The list is the caller's until the
        /// next Take.
        public List<int> Take()
        {
            lock (gate)
            {
                spare.Clear();
                var taken = pending;
                pending = spare;
                spare = taken;
                foreach (int cell in taken) queued[cell] = 0;
                return taken;
            }
        }
    }

    /// The rectangle of work offsets an object can ever use, relative to its cell, always
    /// holding (0, 0) (IsAllReachable reads the object's own cell first).
    public struct OffsetBounds
    {
        public int MinX, MaxX, MinY, MaxY;

        public static OffsetBounds Origin => default;

        public void Include(int x, int y)
        {
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
        }

        public bool Contains(int x, int y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
    }

    /// The monitored objects by cell, to find the ones a table cell's crossing can change.
    ///
    /// An object at cell o with offsets inside its bounds reads the table at o and at
    /// o + dx + dy·width for each offset (dx, dy) (Grid.OffsetCell, which wraps across a row's
    /// end as plain cell arithmetic). So the objects a crossing at c concerns are those at
    /// c − dx − dy·width, for every (dx, dy) in some object's bounds, whose own bounds hold
    /// (dx, dy): Gather walks the cell lists at those cells, for every (dx, dy) in the union of
    /// all bounds so far, the same arithmetic as the game's, so wrapping offsets are found too.
    /// A slot's list links are doubly linked: moving or removing one is O(1). Main thread only.
    /// Plain .NET (unit tests).
    public sealed class ReachIndex
    {
        public readonly int Width, CellCount;
        readonly int[] head;
        int[] next = new int[0], prev = new int[0], cellOf = new int[0], stamp = new int[0];
        OffsetBounds[] bounds = new OffsetBounds[0];
        object[] owner = new object[0];
        readonly Stack<int> free = new Stack<int>();
        int used, currentStamp;
        OffsetBounds union;

        /// Slots in use.
        public int Count { get; private set; }

        public ReachIndex(int cellCount, int width)
        {
            CellCount = cellCount;
            Width = width;
            head = new int[cellCount];
            for (int i = 0; i < cellCount; i++) head[i] = -1;
        }

        public object Owner(int slot) => owner[slot];
        public int CellOf(int slot) => cellOf[slot];
        public OffsetBounds BoundsOf(int slot) => bounds[slot];
        public OffsetBounds Union => union;

        /// A slot for `who` at `cell` (an invalid cell is kept unlinked: nothing there is reachable).
        public int Add(object who, int cell, OffsetBounds b)
        {
            int slot;
            if (free.Count > 0) slot = free.Pop();
            else
            {
                if (used == next.Length) Grow();
                slot = used++;
            }
            owner[slot] = who;
            bounds[slot] = b;
            Widen(b);
            next[slot] = prev[slot] = -1;
            cellOf[slot] = -1;
            stamp[slot] = 0;
            Link(slot, cell);
            Count++;
            return slot;
        }

        public void Remove(int slot)
        {
            Unlink(slot);
            owner[slot] = null;
            free.Push(slot);
            Count--;
        }

        public void Move(int slot, int cell)
        {
            if (cellOf[slot] == cell) return;
            Unlink(slot);
            Link(slot, cell);
        }

        public void SetBounds(int slot, OffsetBounds b)
        {
            bounds[slot] = b;
            Widen(b);
        }

        /// A new pass of Gather/Mark: every slot unmarked.
        public void NewPass()
        {
            if (++currentStamp == int.MaxValue)
            {
                Array.Clear(stamp, 0, stamp.Length);
                currentStamp = 1;
            }
        }

        /// Marks the slot for this pass; false when it already was.
        public bool Mark(int slot)
        {
            if (stamp[slot] == currentStamp) return false;
            stamp[slot] = currentStamp;
            return true;
        }

        /// Adds to `into` (marking them) the unmarked slots whose reads can include `cell`.
        public void Gather(int cell, List<int> into)
        {
            for (int dy = union.MinY; dy <= union.MaxY; dy++)
                for (int dx = union.MinX; dx <= union.MaxX; dx++)
                {
                    int o = cell - dx - dy * Width;
                    if ((uint)o >= (uint)CellCount) continue;
                    for (int s = head[o]; s >= 0; s = next[s])
                        if (bounds[s].Contains(dx, dy) && Mark(s)) into.Add(s);
                }
        }

        void Widen(OffsetBounds b)
        {
            union.Include(b.MinX, b.MinY);
            union.Include(b.MaxX, b.MaxY);
        }

        void Link(int slot, int cell)
        {
            cellOf[slot] = cell;
            if ((uint)cell >= (uint)CellCount) return;
            int h = head[cell];
            next[slot] = h;
            prev[slot] = -1;
            if (h >= 0) prev[h] = slot;
            head[cell] = slot;
        }

        void Unlink(int slot)
        {
            int cell = cellOf[slot];
            if ((uint)cell < (uint)CellCount)
            {
                int n = next[slot], p = prev[slot];
                if (p >= 0) next[p] = n; else head[cell] = n;
                if (n >= 0) prev[n] = p;
            }
            next[slot] = prev[slot] = -1;
            cellOf[slot] = -1;
        }

        void Grow()
        {
            int size = Math.Max(1024, next.Length * 2);
            Array.Resize(ref next, size);
            Array.Resize(ref prev, size);
            Array.Resize(ref cellOf, size);
            Array.Resize(ref stamp, size);
            Array.Resize(ref bounds, size);
            Array.Resize(ref owner, size);
        }
    }

    /// Which monitored objects the tweak answers by events, and which keep the game's polling:
    /// only the cells and offsets the tweak can follow. Plain .NET (unit tests): the game side
    /// names the declaring types.
    public static class ReachCoverage
    {
        public enum CellSource { Unknown, Transform, CachedPickupableCell, FixedDiggableCell }
        public enum OffsetSource { Unknown, Tracker, UnsealableFacing }
        public enum TrackerKind { Unknown, None, Standard, Table }

        /// Where GetCell() comes from, by the type declaring the object's override.
        public static CellSource Cell(string declaringType) => declaringType switch
        {
            "Workable" => CellSource.Transform,
            "Pickupable" => CellSource.CachedPickupableCell,
            "Diggable" => CellSource.FixedDiggableCell,
            _ => CellSource.Unknown,
        };

        /// Where GetOffsets(int) comes from, by the type declaring the object's override.
        public static OffsetSource Offsets(string declaringType) => declaringType switch
        {
            "Workable" => OffsetSource.Tracker,
            "Unsealable" => OffsetSource.UnsealableFacing,
            _ => OffsetSource.Unknown,
        };

        /// The tracker by its exact type name (a subclass may compute anything).
        public static TrackerKind Tracker(string exactType) => exactType switch
        {
            null => TrackerKind.None,
            "StandardOffsetTracker" => TrackerKind.Standard,
            "OffsetTableTracker" => TrackerKind.Table,
            _ => TrackerKind.Unknown,
        };

        /// Whether the tweak can answer the object: a known cell and offsets source, and for the
        /// tracker's offsets a tracker it knows.
        public static bool Covered(CellSource cell, OffsetSource offsets, TrackerKind tracker) =>
            cell != CellSource.Unknown && offsets != OffsetSource.Unknown
            && (offsets == OffsetSource.UnsealableFacing || tracker != TrackerKind.Unknown);
    }
}
