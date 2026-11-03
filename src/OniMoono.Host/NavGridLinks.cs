#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace OniMoono.Host
{
    /// Game tweak `compact-nav-links` (docs/research/navgrid-links-store.md). The game keeps each
    /// nav grid's links in one flat array, `NavGrid.Links`: maxLinksPerCell slots for every cell
    /// of the map (56 on the Duplicant grid), the cell's links first and a link of -1 ending
    /// them. Fewer than 6 % of the slots hold a link (1.8 % on a 1192×768 map, where the 19 grids
    /// take 2.4 GB). With the tweak, `Links` stays a `Link[]` (every signature, Harmony patch and
    /// mod that passes the array keeps working), but one of a single element holding
    /// HandleMark and the number of a store that keeps the links in compressed rows: per cell a
    /// 32-bit head (the cell's block and its count) and the links packed in power-of-two
    /// blocks. The load-time rewrite (NavGridLinksRewrite) sends every element access of a
    /// `Link[]` in the game and in mods through the helpers here, which index a plain array as
    /// before and read or write a store when given its handle. Reads give exactly what the
    /// game's array holds up to each cell's -1; the slots after it, which nothing reads, are not
    /// kept.
    ///
    /// Writes. The game's one writer (NavGridUpdater.CreateLinks) stores a cell's links from the
    /// cell's first slot on and then writes -1 into the slot after them. Store stages them in a
    /// buffer of the writing thread and StoreLink's -1 commits them: in place when they fit the
    /// cell's block, else into a new block, and the cell's head is published with one write after
    /// the links. Blocks are never reused while the store lives, so a reader holding an old head
    /// reads the old block: a reader racing a rewrite sees the old list, the new one or, in
    /// place, a mix of the two, as it can in the game's array. A cell with more links than its
    /// slots (the game logs "Out of nav links") writes on into the next cell's slots there; the
    /// store does the same: the cell is marked full and the rest go to the next cell.
    ///
    /// A store's head is 32 bits (count, size class, block number) while the map has fewer than
    /// 2^20 cells and maxLinksPerCell is below 256 (each cell takes at most one block of each
    /// size, as blocks only grow), else 64.
    ///
    /// With ONI_TWEAK_VERIFY=1 each store also keeps the game's flat array, written as the game
    /// writes it, and Verify compares every cell's links in both (CompactNavLinks runs it).
    public static class NavGridLinks
    {
        /// A handle's `link` field: below -1, which the game never stores.
        public const int HandleMark = unchecked((int)0x8C4E4C53);

        /// Whether the store is used: decided once, at the first grid (Decide). 0: not yet.
        internal static int Mode;
        internal const int Compact = 1, Flat = 2;

        /// Why the session runs flat (null while compact or undecided).
        internal static string? FlatReason;

        /// The decision, made at the first NavGrid: null for the store, else why not (the host's
        /// CompactNavLinks sets it; unset, the store is used).
        internal static Func<string?>? Decide = null;

        static readonly object deciding = new();

        /// ONI_TWEAK_VERIFY=1: stores keep the flat array beside them.
        internal static bool Verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// `new Link[length]` in NavGrid's constructor: a store's handle in compact mode, else
        /// the array.
        public static T[] New<T>(int length, int maxLinksPerCell) where T : unmanaged
        {
            if (Mode == 0)
                lock (deciding)
                    if (Mode == 0)
                    {
                        string? reason = Unsafe.SizeOf<T>() != 8 ? $"a link is {Unsafe.SizeOf<T>()} bytes, not 8" : Decide?.Invoke();
                        FlatReason = reason;
                        Volatile.Write(ref Mode, reason == null ? Compact : Flat);
                    }
            if (Mode != Compact || length < 0 || maxLinksPerCell < 2 || maxLinksPerCell > 65535 || length % maxLinksPerCell != 0) return new T[length];
            return new NavLinkStore<T>(length / maxLinksPerCell, maxLinksPerCell, Verify).Handle;
        }

        /// Whether the array is a store's handle.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsHandle<T>(T[] links) where T : unmanaged =>
            links.Length == 1 && Unsafe.As<T, int>(ref links[0]) == HandleMark;

        /// The store a handle names.
        internal static NavLinkStore<T> StoreOf<T>(T[] handle) where T : unmanaged =>
            NavLinkStore<T>.Find(Unsafe.Add(ref Unsafe.As<T, int>(ref handle[0]), 1));

        /// The end of a cell's links: link -1, the other fields 0.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static T Marker<T>() where T : unmanaged
        {
            T marker = default;
            Unsafe.As<T, int>(ref marker) = -1;
            return marker;
        }

        /// `ldelem.any Link`.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Load<T>(T[] links, int index) where T : unmanaged
        {
            if (links.Length != 1) return links[index];
            return LoadOne(links, index);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static T LoadOne<T>(T[] links, int index) where T : unmanaged =>
            IsHandle(links) ? StoreOf(links).Read(index) : links[index];

        /// `ldelem.any Link` in a walk of a cell's links (the three hot loops): through a cursor
        /// local to the method, which holds the cell's block while the walk is in it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Walk<T>(T[] links, int index, ref NavLinkCursor<T> cursor) where T : unmanaged
        {
            if ((object)links == cursor.Array && index > cursor.Low)
            {
                if (index < cursor.High) return cursor.Packed![index + cursor.Delta];
                if (index == cursor.End) return Marker<T>();
            }
            return WalkMiss(links, index, ref cursor);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static T WalkMiss<T>(T[] links, int index, ref NavLinkCursor<T> cursor) where T : unmanaged
        {
            if (!IsHandle(links))
            {
                var value = links[index];
                cursor.Array = links;
                cursor.Packed = links;
                cursor.Store = null;
                cursor.Low = index;
                cursor.High = links.Length;
                cursor.Delta = 0;
                cursor.End = -1;
                return value;
            }
            var store = (object)links == cursor.Array && cursor.Store != null ? cursor.Store : NavLinkStore<T>.Recent(links);
            return store.Open(links, index, ref cursor);
        }

        /// `stelem.any Link`.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Store<T>(T[] links, int index, T value) where T : unmanaged
        {
            if (links.Length != 1) links[index] = value;
            else StoreOne(links, index, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void StoreOne<T>(T[] links, int index, T value) where T : unmanaged
        {
            if (IsHandle(links)) StoreOf(links).Stage(links, index, value);
            else links[index] = value;
        }

        /// `ldelema Link` + `stfld Link::link`: the end of a cell's links when -1.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void StoreLink<T>(T[] links, int index, int value) where T : unmanaged
        {
            if (links.Length != 1) Unsafe.As<T, int>(ref links[index]) = value;
            else StoreLinkOne(links, index, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void StoreLinkOne<T>(T[] links, int index, int value) where T : unmanaged
        {
            if (IsHandle(links)) StoreOf(links).WriteLink(links, index, value);
            else Unsafe.As<T, int>(ref links[index]) = value;
        }

        /// `ldlen` of a `Link[]`: a handle's is the game's array's.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Length<T>(T[] links) where T : unmanaged =>
            links.Length != 1 || !IsHandle(links) ? links.Length : StoreOf(links).Length;
    }

    /// Walk's state, a local of each method that walks a cell (NavGridLinksRewrite adds it): while
    /// `Array` is the walked array, slots Low+1 .. High-1 are Packed[slot + Delta], and End (when
    /// not -1) is the slot of the cell's -1. A walk's first slot (Low) always reads the cell
    /// again.
    public struct NavLinkCursor<T> where T : unmanaged
    {
        internal object? Array;
        internal NavLinkStore<T>? Store;
        internal T[]? Packed;
        internal int Low, High, Delta, End;
    }

    /// A store as the tweak's summary and verify mode see it.
    internal interface INavLinkStore
    {
        bool Wide { get; }
        (long Bytes, long Links, long Slots) Size();
        int Verify(out long links, out string? first);
        void Dump(System.IO.Stream to);
    }

    internal static class NavLinkStores
    {
        /// Every store by its handle, which keeps it alive.
        internal static readonly ConditionalWeakTable<object, INavLinkStore> ByHandle = new();
    }

    /// One grid's links in compressed rows (NavGridLinks).
    internal sealed class NavLinkStore<T> : INavLinkStore where T : unmanaged
    {
        static WeakReference<NavLinkStore<T>>?[] stores = new WeakReference<NavLinkStore<T>>?[16];
        static int storeCount;
        static readonly object registering = new();

        /// The store a walk last opened, on any thread (a walk's cursor is new in each call of
        /// AddPotentials, so each would otherwise look its store up by number, through a weak
        /// reference). It keeps at most that one store of an unloaded world alive, until the
        /// next world's first walk.
        static NavLinkStore<T>? recent;

        /// The handle's store, the most recent one when it is that.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static NavLinkStore<T> Recent(T[] handle)
        {
            var store = recent;
            if (store != null && store.Handle == handle) return store;
            return recent = NavGridLinks.StoreOf(handle);
        }

        /// The store numbered `id` (its handle keeps it alive).
        internal static NavLinkStore<T> Find(int id)
        {
            var all = Volatile.Read(ref stores);
            if ((uint)id < (uint)all.Length && all[id] is { } weak && weak.TryGetTarget(out var store)) return store;
            throw new InvalidOperationException($"nav links: no store {id}");
        }

        internal readonly T[] Handle;
        internal readonly int Cells, Max, Length, Id;
        readonly ulong divisor;
        readonly bool wide;
        readonly uint[]? narrowHeads;
        readonly ulong[]? wideHeads;
        readonly Pool[] pools;
        /// Verify mode: the game's array, written as the game writes it.
        internal readonly T[]? Flat;
        int inexact;

        /// The most cells of a store with 32-bit heads (block 0 of each size means none).
        internal const int NarrowCells = (1 << 20) - 2;

        internal NavLinkStore(int cells, int max, bool keepFlat)
        {
            Cells = cells;
            Max = max;
            Length = checked(cells * max);
            divisor = ulong.MaxValue / (ulong)max + 1;
            wide = cells > NarrowCells || max > 255;
            if (wide) wideHeads = new ulong[cells];
            else narrowHeads = new uint[cells];
            int classes = 1 + Log2Ceiling(max);
            pools = new Pool[classes];
            for (int c = 0; c < classes; c++) pools[c] = new Pool(c, wide ? (1L << 40) - 1 : (1 << 20) - 1);
            if (keepFlat) Flat = new T[Length];
            Handle = new T[1];
            lock (registering)
            {
                Id = storeCount++;
                if (Id >= stores.Length)
                {
                    var grown = new WeakReference<NavLinkStore<T>>?[stores.Length * 2];
                    Array.Copy(stores, grown, stores.Length);
                    Volatile.Write(ref stores, grown);
                }
                stores[Id] = new WeakReference<NavLinkStore<T>>(this);
                NavLinkStores.ByHandle.Add(Handle, this);
            }
            ref int first = ref Unsafe.As<T, int>(ref Handle[0]);
            first = NavGridLinks.HandleMark;
            Unsafe.Add(ref first, 1) = Id;
        }

        public bool Wide => wide;

        static int Log2Ceiling(int n) => n <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(n - 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int CellOf(int index) => (int)Math.BigMul(divisor, (uint)index, out _);

        /// A cell's head: its block (size class and number; number 0: none) and its count.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Head(int cell, out long block, out int size, out int count)
        {
            if (wide)
            {
                ulong h = Volatile.Read(ref wideHeads![cell]);
                count = (int)(h & 0xFFFF);
                size = (int)((h >> 16) & 0xFF);
                block = (long)(h >> 24);
            }
            else
            {
                uint h = Volatile.Read(ref narrowHeads![cell]);
                count = (int)(h & 0xFF);
                size = (int)((h >> 8) & 0xF);
                block = h >> 12;
            }
        }

        void Publish(int cell, long block, int size, int count)
        {
            if (wide) Volatile.Write(ref wideHeads![cell], (ulong)block << 24 | (ulong)size << 16 | (uint)count);
            else Volatile.Write(ref narrowHeads![cell], (uint)block << 12 | (uint)size << 8 | (uint)count);
        }

        /// The packed links' array and the offset of a block in it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        T[] Slots(long block, int size, out int offset) => pools[size].Slots(block, out offset);

        void CheckIndex(int index)
        {
            if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
        }

        /// The link the game's array holds at `index`, as far as a reader stopping at -1 sees it.
        internal T Read(int index)
        {
            CheckIndex(index);
            int cell = CellOf(index), k = index - cell * Max;
            Head(cell, out long block, out int size, out int count);
            if (k >= count) return NavGridLinks.Marker<T>();
            return Slots(block, size, out int offset)[offset + k];
        }

        /// A walk reaching a new cell (NavGridLinks.Walk): the cursor takes the cell's block.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Open(T[] handle, int index, ref NavLinkCursor<T> cursor)
        {
            CheckIndex(index);
            int cell = CellOf(index), low = cell * Max, k = index - low;
            Head(cell, out long block, out int size, out int count);
            cursor.Array = handle;
            cursor.Store = this;
            cursor.Low = low;
            cursor.High = low + count;
            cursor.End = count < Max ? low + count : -1;
            if (count == 0)
            {
                cursor.Packed = null;
                cursor.Delta = 0;
                return NavGridLinks.Marker<T>();
            }
            var slots = Slots(block, size, out int offset);
            cursor.Packed = slots;
            cursor.Delta = offset - low;
            return k < count ? slots[offset + k] : NavGridLinks.Marker<T>();
        }

        /// A cell's links now (a copy).
        internal int Links(int cell, T[] into)
        {
            Head(cell, out long block, out int size, out int count);
            if (count == 0) return 0;
            var slots = Slots(block, size, out int offset);
            for (int k = 0; k < count; k++) into[k] = slots[offset + k];
            return count;
        }

        /// Sets a cell's links: in its block when they fit, else in a new one.
        void Commit(int cell, T[] links, int count)
        {
            Head(cell, out long block, out int size, out _);
            if (count > 0 && (block == 0 || count > 1 << size))
            {
                size = Log2Ceiling(count);
                while ((block = pools[size].Allocate()) < 0)
                    if (++size >= pools.Length) throw new InvalidOperationException($"nav links: store {Id} has no block left for {count} links");
            }
            if (count > 0)
            {
                var slots = Slots(block, size, out int offset);
                for (int k = 0; k < count; k++) slots[offset + k] = links[k];
            }
            Publish(cell, block, size, count);
        }

        /// The links staged by one thread: a run of slots from Start, not yet ended by -1.
        sealed class Staged
        {
            internal NavLinkStore<T>? Store;
            internal int Start, Count;
            internal T[] Links = new T[64];
        }

        [ThreadStatic] static Staged? staged;

        /// `links[index] = value`.
        internal void Stage(T[] handle, int index, T value)
        {
            CheckIndex(index);
            var run = staged ??= new Staged();
            if (run.Store != this || index != run.Start + run.Count)
            {
                if (run.Store != null && run.Count > 0) run.Store.Unended(run);
                run.Store = this;
                run.Start = index;
                run.Count = 0;
            }
            if (run.Count == run.Links.Length) Array.Resize(ref run.Links, run.Count * 2);
            run.Links[run.Count++] = value;
            if (Flat != null) Flat[index] = value;
        }

        /// `links[index].link = value`.
        internal void WriteLink(T[] handle, int index, int value)
        {
            CheckIndex(index);
            var run = staged;
            if (Flat != null) Unsafe.As<T, int>(ref Flat[index]) = value;
            if (value == -1)
            {
                if (run != null && run.Store == this && index == run.Start + run.Count && run.Count > 0)
                {
                    Ended(run.Start, run.Links, run.Count);
                    run.Store = null;
                    run.Count = 0;
                    return;
                }
                if (run != null && run.Store != null && run.Count > 0) run.Store.Unended(run);
                Ended(index, run?.Links ?? [], 0);
                return;
            }
            if (run != null && run.Store != null && run.Count > 0) run.Store.Unended(run);
            int cell = CellOf(index), k = index - cell * Max;
            var list = new T[Max];
            int count = Links(cell, list);
            if (k < count)
            {
                Unsafe.As<T, int>(ref list[k]) = value;
                Commit(cell, list, count);
            }
            else if (k == count) Inexact($"slot {index}: the -1 ending cell {cell}'s links overwritten with {value}");
        }

        /// Slots Start.. written with `count` links and then -1: CreateLinks' writes. Each cell
        /// they touch keeps its links before the run (when the run starts after them, the -1
        /// before it hides the run); a cell the run fills to its end is full, and the reader
        /// goes on into the next.
        void Ended(int start, T[] run, int count)
        {
            int end = start + count, cell = CellOf(start), low = cell * Max;
            if (start == low && count < Max)
            {
                Commit(cell, run, count);
                return;
            }
            var list = new T[Max];
            for (; low <= end; cell++, low += Max)
            {
                int from = Math.Max(start, low) - low, to = Math.Min(end, low + Max) - low;
                int old = Links(cell, list);
                if (from > old) continue;
                for (int k = from; k < to; k++) list[k] = run[low + k - start];
                Commit(cell, list, to);
                if (end < low + Max) break;
            }
        }

        /// A run of links that a write elsewhere interrupted before its -1: as written, the
        /// cell's own links after the run are kept; slots it wrote past the cell's -1 cannot be
        /// held (what the game's array has after them is not kept).
        void Unended(Staged run)
        {
            int start = run.Start, end = start + run.Count, cell = CellOf(start), low = cell * Max;
            var list = new T[Max];
            bool lost = false;
            for (; low < end; cell++, low += Max)
            {
                int from = Math.Max(start, low) - low, to = Math.Min(end, low + Max) - low;
                int old = Links(cell, list);
                if (from > old) continue;
                if (to > old && to < Max) lost = true;
                for (int k = from; k < to; k++) list[k] = run.Links[low + k - start];
                Commit(cell, list, Math.Max(old, to));
            }
            run.Store = null;
            run.Count = 0;
            if (lost) Inexact($"links written at {start}..{end - 1} without a -1 after them");
        }

        void Inexact(string what)
        {
            if (Interlocked.Increment(ref inexact) == 1)
                OniMoono.Engine.EngineLog.Engine($"nav links: store {Id}: {what}; the game's array would hold slots the store does not");
        }

        /// Verify mode: every cell's links in the store against the game's array, as the
        /// pathfinder reads them (from the cell's first slot to its -1 or the cell's end) and as
        /// a walk does (through Walk); the number of cells that differ, and the first's details.
        public int Verify(out long links, out string? first)
        {
            links = 0;
            first = null;
            if (Flat == null) return 0;
            int wrong = 0;
            var cursor = new NavLinkCursor<T>();
            for (int cell = 0; cell < Cells; cell++)
            {
                int low = cell * Max;
                for (int k = 0; k < Max; k++)
                {
                    var expected = Flat[low + k];
                    var walked = NavGridLinks.Walk(Handle, low + k, ref cursor);
                    var read = Read(low + k);
                    int expectedLink = Unsafe.As<T, int>(ref expected);
                    if (!Same(expected, walked) || !Same(expected, read))
                    {
                        if (expectedLink == -1 && Unsafe.As<T, int>(ref walked) == -1 && Unsafe.As<T, int>(ref read) == -1) break; // the -1's other fields are not kept
                        if (wrong++ == 0) first = $"cell {cell} slot {k}: the game's array has {Show(expected)}, the store {Show(read)}, a walk {Show(walked)}";
                        break;
                    }
                    if (expectedLink == -1) break;
                    links++;
                }
            }
            return wrong;
        }

        /// The cells' links in NavProbe's format (docs/research/navgrid-links-store.md): the cell
        /// count, maxLinksPerCell, a count byte a cell, then the links in cell order.
        public void Dump(System.IO.Stream to)
        {
            using var w = new System.IO.BinaryWriter(to);
            w.Write(Cells);
            w.Write(Max);
            for (int cell = 0; cell < Cells; cell++)
            {
                Head(cell, out _, out _, out int count);
                w.Write((byte)Math.Min(count, 255));
            }
            var list = new T[Max];
            for (int cell = 0; cell < Cells; cell++)
            {
                int count = Links(cell, list);
                for (int k = 0; k < count; k++) w.Write(Unsafe.As<T, long>(ref list[k]));
            }
        }

        static bool Same(T a, T b) => Unsafe.As<T, long>(ref a) == Unsafe.As<T, long>(ref b);

        static string Show(T link) => $"{Unsafe.As<T, int>(ref link)}/{Unsafe.As<T, long>(ref link) >> 32:x8}";

        /// The bytes the store holds (heads and blocks taken) and its links (the cells' counts).
        public (long Bytes, long Links, long Slots) Size()
        {
            long bytes = (long)Cells * (wide ? 8 : 4), links = 0, slots = 0;
            foreach (var pool in pools) bytes += pool.Bytes;
            for (int cell = 0; cell < Cells; cell++)
            {
                Head(cell, out _, out _, out int count);
                links += count;
            }
            slots = (long)Cells * Max;
            return (bytes, links, slots);
        }

        /// The blocks of one size (2^Shift links), numbered from 1, in chunks of at least 4,096
        /// links allocated as they are needed.
        sealed class Pool
        {
            readonly int shift, chunkShift;
            readonly long limit;
            T[]?[] chunks = new T[]?[4];
            long next = 1;

            internal Pool(int shift, long limit)
            {
                this.shift = shift;
                this.limit = limit;
                chunkShift = Math.Max(12, shift);
            }

            internal long Bytes
            {
                get
                {
                    long bytes = 0;
                    foreach (var chunk in Volatile.Read(ref chunks))
                        if (chunk != null) bytes += (long)chunk.Length * Unsafe.SizeOf<T>();
                    return bytes;
                }
            }

            /// A new block's number, or -1 when this size has none left.
            internal long Allocate()
            {
                long block = Interlocked.Increment(ref next) - 1;
                if (block > limit) return -1;
                long chunk = (block << shift) >> chunkShift;
                var all = Volatile.Read(ref chunks);
                if (chunk >= all.Length || Volatile.Read(ref all[chunk]) == null) Grow((int)chunk);
                return block;
            }

            void Grow(int chunk)
            {
                lock (this)
                {
                    var all = chunks;
                    if (chunk >= all.Length)
                    {
                        var grown = new T[]?[Math.Max(all.Length * 2, chunk + 1)];
                        Array.Copy(all, grown, all.Length);
                        all = grown;
                    }
                    if (all[chunk] == null) Volatile.Write(ref all[chunk], new T[1 << chunkShift]);
                    Volatile.Write(ref chunks, all);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal T[] Slots(long block, out int offset)
            {
                long slot = block << shift;
                offset = (int)(slot & ((1L << chunkShift) - 1));
                return Volatile.Read(ref chunks)[slot >> chunkShift]!;
            }
        }
    }
}
