#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace OniMoono.Host
{
    /// Game tweak `paged-path-grids` (docs/research/heap-reduction-designs.md §3). Every
    /// navigator without a probe radius (Duplicants, Rovers, Flydos) has a PathGrid the size of the
    /// whole cluster: `Cells` (a PathFinder.Cell of 12 bytes for every cell and nav type) and
    /// `ProberCells` (8 bytes a cell), 24 MB a Duplicant on a 636×404 map, 84 MB on 1192×768. A
    /// probe writes only what its Duplicant can reach, so most of each array keeps the zeros it was
    /// allocated with for the whole session. With the tweak, the load-time rewrite
    /// (PathGridPagesRewrite) gives PathGrid a page table for each array (`<Cells>pages`,
    /// `<ProberCells>pages`) and its length (`<Cells>length`, `<ProberCells>length`), and its
    /// element accesses call the helpers here: pages of PageSize elements, made on their first
    /// write of anything but zeros. A page never written is the shared Zero page, so it reads as
    /// the zeros the game's array holds there; a page once written holds what the array would. The
    /// content at every index is the array's at every moment (stale cells of old probes included:
    /// a grid is never cleared, and its ushort serial number wraps), so paging changes nothing the
    /// game computes. `Cells` and `ProberCells` stay arrays, one element each, so Fast Track's
    /// `lock (grid.Cells)` keeps working.
    ///
    /// Flat mode (decided once, at the first PathGrid: Decide) is the same rewritten code with
    /// one page, the game's array itself, which `Cells` then holds: an access the rewrite did not
    /// see reads and writes the same elements. The page size is a constant of the session
    /// (PageMode, read only once decided, so the optimised code folds it).
    ///
    /// Threads. AsyncPathProber's worker writes the grid it took from the pool while the main
    /// thread reads the navigators' grids, as in the game; nothing locks. A page is published with
    /// one compare-and-exchange of its table slot (Zero to the new page): two writers racing on an
    /// unwritten page both write into whichever page won, and a reader racing a page's creation
    /// reads Zero's zeros, what it read a moment before. A store of zeros into an unwritten page
    /// stores nothing (ResetProberCells clears every element of a grid).
    ///
    /// With ONI_TWEAK_VERIFY=1 each table also keeps the game's flat array, written by the same
    /// helpers, and PagedPathGrids compares the two (Verify).
    public static class PathGridPages
    {
        public const int PageShift = 8, PageSize = 1 << PageShift, PageMask = PageSize - 1;

        /// The decision, made at the first PathGrid: null for pages, else why the session is flat
        /// (the host's PagedPathGrids sets it; unset, pages).
        internal static Func<string?>? Decide = null;

        /// Why the session runs flat (null while paged or undecided).
        internal static string? FlatReason;

        /// ONI_TWEAK_VERIFY=1: every table keeps the flat array beside it.
        internal static readonly bool Verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Every table made, with what the summary and verify mode need (a table keeps its entry).
        internal static readonly ConditionalWeakTable<Array, Info> Tables = new();

        internal sealed class Info
        {
            internal string Kind = "";
            internal int Length, ElementSize;
            /// One page, the game's array (flat mode).
            internal bool Flat;
            /// The table's Zero page (Zero<T>.Page).
            internal Array? Zero;
            /// Verify mode: the game's array, written as the game writes it.
            internal Array? Shadow;
        }

        /// Pages made (both arrays, all grids), for the summary.
        internal static long PagesMade;

        /// For tests (E2eDriver's state reads, `type:OniMoono.Host.PathGridPages::VerifyRuns`):
        /// verify mode's comparisons of every table, and the elements found to differ in them.
        public static int VerifyRuns { get; private set; }
        public static long VerifyDifferences { get; private set; }

        /// For tests: the tables' count and what they hold now, and what the game's arrays would
        /// (bytes).
        public static int TableCount => Size().Tables;
        public static long HeldBytes => Size() is var s ? s.PageBytes + s.TableBytes : 0;
        public static long FlatBytes => Size().FlatBytes;

        /// Whether the session's tables are paged (the decision, made on the first call).
        public static bool Paged => PageMode.Paged;

        /// `newarr T` of `Cells` or `ProberCells` in PathGrid's constructor: the array's page table
        /// (the rewritten constructor keeps it, its Length and its Handle in PathGrid's fields).
        public static T[][] New<T>(int length) where T : unmanaged => New<T>(length, PageMode.Shift);

        internal static T[][] New<T>(int length, int shift) where T : unmanaged
        {
            if (length < 0) throw new OverflowException();
            T[][] pages;
            bool flat = shift >= 31 || length == 0;
            if (flat) pages = [new T[length]];
            else
            {
                int size = 1 << shift, n = (int)(((long)length + size - 1) >> shift);
                pages = new T[n][];
                if (shift == PageShift) Array.Fill(pages, Zero<T>.Page);
                else for (int k = 0; k < n; k++) pages[k] = new T[size];
                // The last page exactly as long as the array's end: its bounds check is the array's.
                pages[n - 1] = new T[length - ((n - 1) << shift)];
            }
            var info = new Info { Kind = typeof(T).Name, Length = length, ElementSize = Unsafe.SizeOf<T>(), Flat = flat, Zero = Zero<T>.Page };
            if (Verify && !flat) info.Shadow = new T[length];
            Tables.AddOrUpdate(pages, info);
            return pages;
        }

        /// The array's length (PathGrid's `<…>length`).
        public static int Length<T>(T[][] pages) where T : unmanaged =>
            Tables.TryGetValue(pages, out var info) ? info.Length : throw new ArgumentException("not a path grid's page table");

        /// What `Cells` holds: the game's array in flat mode, else a one-element stand-in (each grid
        /// its own, as `lock (grid.Cells)` wants).
        public static T[] Handle<T>(T[][] pages) where T : unmanaged =>
            Tables.TryGetValue(pages, out var info) && info.Flat ? pages[0] : new T[1];

        /// `ldelem.any T`.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Load<T>(T[][] pages, int index) where T : unmanaged => LoadIn(pages, index, PageMode.Shift, PageMode.Mask);

        /// `stelem.any T`.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Store<T>(T[][] pages, int index, T value) where T : unmanaged => StoreIn(pages, index, value, PageMode.Shift, PageMode.Mask);

        /// `ldelema T` + `initobj T` (ResetProberCells).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear<T>(T[][] pages, int index) where T : unmanaged => StoreIn(pages, index, default(T), PageMode.Shift, PageMode.Mask);

        /// Load and Store with the page size given (the tests try both modes in one process).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static T LoadIn<T>(T[][] pages, int index, int shift, int mask) where T : unmanaged => pages[index >> shift][index & mask];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void StoreIn<T>(T[][] pages, int index, T value, int shift, int mask) where T : unmanaged
        {
            int k = index >> shift;
            var page = pages[k];
            if (page == Zero<T>.Page)
            {
                if (IsZero(value))
                {
                    if (Verify) Shadow(pages, index, value);
                    return;
                }
                page = Materialise(pages, k);
            }
            page[index & mask] = value;
            if (Verify) Shadow(pages, index, value);
        }

        /// Page k of the table, made if it is Zero (path-probe-repair's views write through it).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T[] Materialise<T>(T[][] pages, int k) where T : unmanaged
        {
            var page = Volatile.Read(ref pages[k]);
            if (page != Zero<T>.Page) return page;
            // On the pinned object heap: a page lives as long as its grid, and a page made in
            // play would otherwise be copied by each of the GC's promotions on its way there.
            var fresh = GC.AllocateArray<T>(PageSize, pinned: true);
            var won = Interlocked.CompareExchange(ref pages[k], fresh, Zero<T>.Page);
            if (won != Zero<T>.Page) return won;
            Interlocked.Increment(ref PagesMade);
            return fresh;
        }

        /// Whether the table's page k was never written.
        public static bool IsZeroPage<T>(T[][] pages, int k) where T : unmanaged => pages[k] == Zero<T>.Page;

        /// Verify mode: the table's flat array (null otherwise).
        public static T[]? ShadowOf<T>(T[][] pages) where T : unmanaged =>
            Tables.TryGetValue(pages, out var info) ? (T[]?)info.Shadow : null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Shadow<T>(T[][] pages, int index, T value) where T : unmanaged
        {
            if (ShadowOf(pages) is { } shadow) shadow[index] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsZero<T>(T value) where T : unmanaged =>
            MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).IndexOfAnyExcept((byte)0) < 0;

        /// What the tables hold: their count, the bytes of the pages made and of the tables, and
        /// the bytes the game's arrays would take.
        internal static (int Tables, long PageBytes, long TableBytes, long FlatBytes) Size()
        {
            int tables = 0;
            long pageBytes = 0, tableBytes = 0, flat = 0;
            foreach (var (table, info) in Tables)
            {
                tables++;
                flat += (long)info.Length * info.ElementSize;
                tableBytes += 24 + (long)table.Length * IntPtr.Size;
                foreach (Array? page in (Array[])table)
                    if (page != null && page != info.Zero) pageBytes += 24 + (long)page.Length * info.ElementSize;
            }
            return (tables, pageBytes, tableBytes, flat);
        }

        /// Verify mode: every element of every table against its flat array. A difference seen
        /// again after a moment counts (one seen once is a write landing between the two reads).
        internal static int VerifyAll(out int tables, out long elements, out string? first)
        {
            tables = 0;
            elements = 0;
            first = null;
            int wrong = 0;
            foreach (var (table, info) in Tables)
            {
                if (info.Shadow == null) continue;
                tables++;
                elements += info.Length;
                int size = info.ElementSize;
                var pages = (Array[])table;
                const int shift = PageShift;
                ref byte shadow = ref MemoryMarshal.GetArrayDataReference(info.Shadow);
                for (int k = 0; k < pages.Length; k++)
                {
                    var page = pages[k];
                    int start = k << shift, count = page.Length;
                    ref byte flat = ref Unsafe.Add(ref shadow, (nint)start * size);
                    ref byte paged = ref MemoryMarshal.GetArrayDataReference(page);
                    if (MemoryMarshal.CreateReadOnlySpan(ref flat, count * size).SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref paged, count * size))) continue;
                    for (int e = 0; e < count; e++)
                    {
                        if (Same(ref flat, ref paged, e, size)) continue;
                        // Again, a moment later.
                        Thread.Sleep(1);
                        if (Same(ref flat, ref paged, e, size)) continue;
                        wrong++;
                        first ??= $"{info.Kind} element {start + e} of {info.Length}";
                    }
                }
            }
            VerifyRuns++;
            VerifyDifferences += wrong;
            return wrong;
        }

        static bool Same(ref byte a, ref byte b, int e, int size) =>
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref a, e * size), size).SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref b, e * size), size));
    }

    /// The page every unwritten slot of a table names: zeros, never written (Store makes a page
    /// of its own first).
    public static class Zero<T> where T : unmanaged
    {
        public static readonly T[] Page = new T[PathGridPages.PageSize];
    }

    /// The session's page size, read by the helpers once the first PathGrid decided it (the
    /// static constructor runs Decide), so tier-1 code holds it as a constant: 2^8 elements a
    /// page, or in flat mode one page of the whole array (`index >> 31` is 0 for every index
    /// that is not negative; a negative one indexes the table at -1 and throws, as the array
    /// would).
    internal static class PageMode
    {
        internal static readonly bool Paged;
        internal static readonly int Shift, Mask;

        static PageMode()
        {
            string? reason;
            try
            {
                reason = PathGridPages.Decide?.Invoke();
            }
            catch (Exception e)
            {
                reason = $"the decision failed ({e.GetType().Name}: {e.Message})";
            }
            PathGridPages.FlatReason = reason;
            Paged = reason == null;
            Shift = Paged ? PathGridPages.PageShift : 31;
            Mask = Paged ? PathGridPages.PageMask : int.MaxValue;
        }
    }
}
