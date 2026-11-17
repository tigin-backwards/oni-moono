using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace OniMoono.GameTweaks
{
    /// What compact-report-entries' store asks of the game's types (ReportManager.DailyReport and
    /// ReportEntry) beyond what the report history's writer reads (IReportShapes): making an
    /// entry and setting every field of one, the two fields the writer does not read, and a day's
    /// `reportEntries`. A struct, so the store is compiled for it.
    public interface IReportDayShapes<TDay, TEntry> : IReportShapes<TDay, TEntry>
    {
        TEntry New();
        /// Every field of the entry, as `record` and the rest give them.
        void Fill(TEntry entry, in PackedEntry record, string context, TEntry[] elements);
        bool IsChild(TEntry entry);
        /// The entry is exactly the game's type (a subclass keeps its day unpacked).
        bool Plain(TEntry entry);
        void SetEntries(TDay day, List<TEntry> entries);
    }

    /// One report entry of a past day, packed (40 bytes): ReportEntry's fields, its context as an
    /// index in the store's strings, and its ArrayRef of context entries as the first of its
    /// children's records (contiguous, `Size` of them) with the array's length and the ArrayRef's
    /// size and capacity as the game had them.
    public struct PackedEntry
    {
        public int Id;
        public int Type;
        /// The context string's index, or -1 for null.
        public int Context;
        public int ContextId;
        public float Accumulate, Positive, Negative;
        public int Children;
        /// The ArrayRef's `elements.Length`, or NoArray when `elements` is null.
        public ushort Length;
        public ushort Size, Capacity;
        public byte Child;

        public const ushort NoArray = 0xFFFF;
    }

    /// The records and strings of compact-report-entries' store, as the history's writer reads
    /// them: one virtual array of PackedEntry (chunks of 65,536, so it grows without copying) and
    /// the context strings, each with its UTF-8 bytes once a save has written it.
    public sealed class PackedReportRecords
    {
        internal const int Shift = 16, Chunk = 1 << Shift, Mask = Chunk - 1;
        PackedEntry[][] chunks = new PackedEntry[8][];
        int count;

        readonly List<string> strings = new List<string>();
        readonly Dictionary<string, int> stringIds = new Dictionary<string, int>(StringComparer.Ordinal);
        byte[][] utf8 = new byte[64][];
        string lastString;
        int lastId = -1;

        public int Count => count;
        public int Strings => strings.Count;
        public long Bytes
        {
            get
            {
                long n = 0;
                foreach (var c in chunks) if (c != null) n += (long)c.Length * System.Runtime.InteropServices.Marshal.SizeOf<PackedEntry>();
                return n;
            }
        }

        public ref PackedEntry this[int i]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref chunks[i >> Shift][i & Mask];
        }

        /// `n` records at the end: the index of the first.
        public int Allocate(int n)
        {
            int start = count, end = count + n;
            int needed = (end + Mask) >> Shift;
            if (needed > chunks.Length) Array.Resize(ref chunks, Math.Max(needed, chunks.Length * 2));
            for (int c = 0; c < needed; c++) chunks[c] ??= new PackedEntry[Chunk];
            count = end;
            return start;
        }

        /// Back to `n` records (a pack that failed gives back what it took).
        public void Truncate(int n) => count = n;

        /// The whole store emptied (compaction refills it).
        internal PackedEntry[][] TakeChunks(out int n)
        {
            var old = chunks;
            n = count;
            chunks = new PackedEntry[old.Length][];
            count = 0;
            return old;
        }

        public int StringId(string s)
        {
            if (s == null) return -1;
            if (ReferenceEquals(s, lastString)) return lastId;
            if (!stringIds.TryGetValue(s, out int id))
            {
                id = strings.Count;
                strings.Add(s);
                stringIds.Add(s, id);
            }
            lastString = s;
            lastId = id;
            return id;
        }

        public string String(int id) => id < 0 ? null : strings[id];

        /// The string's UTF-8 bytes (IOHelper.WriteKleiString's), made at its first write.
        public byte[] Utf8(int id)
        {
            if (id >= utf8.Length) Array.Resize(ref utf8, Math.Max(id + 1, utf8.Length * 2));
            return utf8[id] ??= Encoding.UTF8.GetBytes(strings[id]);
        }
    }

    /// For the report history's writer: where a day's packed records are, if it is packed.
    public interface IPackedDays<TDay>
    {
        /// A day whose entries are packed, and not given out as objects now: its top-level
        /// records `start` to `start + count`.
        bool TryPacked(TDay day, out int start, out int count);
        PackedReportRecords Records { get; }
        /// Before a save writes the days: what was written to entries given out goes into the
        /// records.
        void BeforeWrite();
    }

    /// compact-report-entries' store (CompactReportDays): plain .NET, with no game types (the
    /// tests compile it in).
    ///
    /// Each packed day (a DailyReport whose `reportEntries` the tweak set to null) has a block of
    /// records: its entries first, in the list's order, then their children, block by block in
    /// the order of their parents, then theirs. Only shapes kept exactly are packed: entries of
    /// the game's own type with `gameHash` -1, no null in the list or within an ArrayRef's size,
    /// nothing but null past it, lengths under 65,535, and at night no entry reached twice.
    /// Anything else keeps its day as the game has it.
    ///
    /// Reading a packed day gives out objects again, through a view of it kept in a bounded
    /// list (the most recent `ViewCapacity` days): an entry alone (GetEntry: Top) or the whole
    /// day (Full, which puts the list back in `reportEntries` while the view lasts). A view
    /// leaving the list is compared with its records first, and a day written to since (an
    /// entry added by GetEntry, a value changed) is packed again from the objects, so what was
    /// written stays.
    public sealed class ReportDayStore<TDay, TEntry, TShapes> : IPackedDays<TDay>
        where TDay : class where TEntry : class where TShapes : struct, IReportDayShapes<TDay, TEntry>
    {
        readonly TShapes shapes = default;
        readonly PackedReportRecords records = new PackedReportRecords();
        // Per day slot: its block and its records' extent.
        int[] dayStart = new int[256], dayCount = new int[256], dayEnd = new int[256];
        TDay[] dayOf = new TDay[256];
        View[] viewOf = new View[256];
        readonly Dictionary<TDay, int> slots = new Dictionary<TDay, int>(ReferenceComparer<TDay>.Instance);
        readonly Stack<int> freeSlots = new Stack<int>();
        int nextSlot;
        long garbage;

        // The views, most recent first.
        View head, tail;
        int views;
        public int ViewCapacity = 32;

        // Packing's scratch.
        readonly List<TEntry> work = new List<TEntry>();
        readonly HashSet<TEntry> seen = new HashSet<TEntry>(ReferenceComparer<TEntry>.Instance);

        public PackedReportRecords Records => records;
        public int Days => slots.Count;
        public int Views => views;
        public long Garbage => garbage;
        public long Bytes => records.Bytes + dayStart.Length * 16L;
        public long Packs, Unpackable, Tops, Fulls, Peeks, Evictions, Writebacks, Compactions;
        /// After a day written to is packed again from its objects (verify mode's record).
        public System.Action<TDay, List<TEntry>> Repacked;

        sealed class View
        {
            public int Slot;
            public TEntry[] Tops;
            public List<TEntry> List;
            public View Prev, Next;
        }

        sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T a) => RuntimeHelpers.GetHashCode(a);
        }

        // ---- Packing ----

        /// The day's `reportEntries` into the store, and the field set to null; false (the day
        /// left as it is) for a shape not kept exactly. `checkShared`: also refuse an entry
        /// reached twice (a day made in this session; a loaded one is a tree).
        public bool Pack(TDay day, bool checkShared)
        {
            if (day == null || slots.ContainsKey(day)) return false;
            var entries = shapes.Entries(day);
            if (entries == null) return false;
            int from = records.Count;
            if (!PackBlock(entries, checkShared, out int start))
            {
                records.Truncate(from);
                Unpackable++;
                return false;
            }
            int slot = NewSlot(day);
            dayStart[slot] = start;
            dayCount[slot] = entries.Count;
            dayEnd[slot] = records.Count;
            shapes.SetEntries(day, null);
            Packs++;
            return true;
        }

        /// The entries and their descendants as one block at the end of the records.
        bool PackBlock(List<TEntry> entries, bool checkShared, out int start)
        {
            work.Clear();
            seen.Clear();
            int n = entries.Count;
            start = records.Allocate(n);
            for (int i = 0; i < n; i++)
            {
                var e = entries[i];
                if (!Set(start + i, e, checkShared)) return false;
                work.Add(e);
            }
            // Record start + k is work[k]: children are allocated in the order they are found.
            for (int k = 0; k < work.Count; k++)
            {
                var e = work[k];
                var elements = shapes.ContextElements(e);
                int size = shapes.ContextSize(e);
                ref var r = ref records[start + k];
                if (elements == null)
                {
                    if (size != 0) return false;
                    continue;
                }
                if (size < 0 || size > elements.Length) return false;
                for (int j = size; j < elements.Length; j++)
                    if (elements[j] != null) return false;
                if (size == 0) continue;
                // `r` stays valid: a chunk never moves.
                int children = records.Allocate(size);
                r.Children = children;
                for (int j = 0; j < size; j++)
                {
                    var c = elements[j];
                    if (!Set(children + j, c, checkShared)) return false;
                    work.Add(c);
                }
            }
            work.Clear();
            seen.Clear();
            return true;
        }

        bool Set(int at, TEntry e, bool checkShared)
        {
            if (e == null || !shapes.Plain(e) || shapes.GameHash(e) != -1) return false;
            if (checkShared && !seen.Add(e)) return false;
            var elements = shapes.ContextElements(e);
            int length = elements == null ? PackedEntry.NoArray : elements.Length;
            int size = shapes.ContextSize(e), capacity = shapes.ContextCapacity(e);
            if (elements != null && length >= PackedEntry.NoArray) return false;
            if ((uint)size >= PackedEntry.NoArray || (uint)capacity >= PackedEntry.NoArray) return false;
            ref var r = ref records[at];
            r.Id = shapes.NoteStorageId(e);
            r.Type = shapes.ReportType(e);
            r.Context = records.StringId(shapes.Context(e));
            r.ContextId = shapes.ContextId(e);
            r.Accumulate = shapes.Accumulate(e);
            r.Positive = shapes.AccPositive(e);
            r.Negative = shapes.AccNegative(e);
            r.Children = 0;
            r.Length = (ushort)length;
            r.Size = (ushort)size;
            r.Capacity = (ushort)capacity;
            r.Child = shapes.IsChild(e) ? (byte)1 : (byte)0;
            return true;
        }

        int NewSlot(TDay day)
        {
            int slot = freeSlots.Count > 0 ? freeSlots.Pop() : nextSlot++;
            if (slot >= dayStart.Length)
            {
                int size = dayStart.Length * 2;
                Array.Resize(ref dayStart, size);
                Array.Resize(ref dayCount, size);
                Array.Resize(ref dayEnd, size);
                Array.Resize(ref dayOf, size);
                Array.Resize(ref viewOf, size);
            }
            dayOf[slot] = day;
            slots.Add(day, slot);
            return slot;
        }

        void FreeSlot(int slot)
        {
            garbage += dayEnd[slot] - dayStart[slot];
            slots.Remove(dayOf[slot]);
            dayOf[slot] = null;
            viewOf[slot] = null;
            freeSlots.Push(slot);
        }

        public bool IsPacked(TDay day) => day != null && slots.ContainsKey(day);

        public void BeforeWrite() => SyncViews();

        // ---- Reading ----

        /// IPackedDays: a packed day that is not given out as a list now.
        public bool TryPacked(TDay day, out int start, out int count)
        {
            if (day != null && slots.TryGetValue(day, out int slot) && viewOf[slot]?.List == null)
            {
                start = dayStart[slot];
                count = dayCount[slot];
                return true;
            }
            start = count = 0;
            return false;
        }

        /// The first entry of the packed day whose type is `type` (DailyReport.GetEntry's
        /// search), as an object kept for the day's view; null when the day has none or is not
        /// packed (`packed` says which).
        public TEntry Top(TDay day, int type, out bool packed)
        {
            packed = false;
            if (day == null || !slots.TryGetValue(day, out int slot)) return null;
            if (viewOf[slot]?.List is { } list)
            {
                // Given out whole: the list is the day's, as the game reads it.
                shapes.SetEntries(day, list);
                return null;
            }
            packed = true;
            int start = dayStart[slot], n = dayCount[slot];
            for (int i = 0; i < n; i++)
                if (records[start + i].Type == type)
                {
                    var view = ViewFor(slot);
                    Tops++;
                    return view.Tops[i] ??= Make(start + i);
                }
            return null;
        }

        /// The packed day's entries as a List, put back in its `reportEntries` while its view
        /// lasts; null when the day is not packed. Entries already given out are the same objects.
        public List<TEntry> Full(TDay day)
        {
            if (day == null || !slots.TryGetValue(day, out int slot)) return null;
            var view = ViewFor(slot);
            if (view.List == null)
            {
                int start = dayStart[slot], n = dayCount[slot];
                var list = new List<TEntry>(n);
                for (int i = 0; i < n; i++) list.Add(view.Tops[i] ??= Make(start + i));
                view.List = list;
                Fulls++;
            }
            shapes.SetEntries(day, view.List);
            return view.List;
        }

        TEntry Make(int r)
        {
            ref var rec = ref records[r];
            TEntry[] elements = null;
            if (rec.Length != PackedEntry.NoArray)
            {
                elements = new TEntry[rec.Length];
                int children = rec.Children;
                for (int j = 0; j < rec.Size; j++) elements[j] = Make(children + j);
            }
            var e = shapes.New();
            shapes.Fill(e, in records[r], records.String(rec.Context), elements);
            return e;
        }

        View ViewFor(int slot)
        {
            var view = viewOf[slot];
            if (view != null)
            {
                if (view != head)
                {
                    Unlink(view);
                    PushFront(view);
                }
                return view;
            }
            view = new View { Slot = slot, Tops = new TEntry[dayCount[slot]] };
            viewOf[slot] = view;
            PushFront(view);
            views++;
            while (views > ViewCapacity && tail != view) Evict(tail);
            return view;
        }

        void PushFront(View v)
        {
            v.Prev = null;
            v.Next = head;
            if (head != null) head.Prev = v;
            head = v;
            tail ??= v;
        }

        void Unlink(View v)
        {
            if (v.Prev != null) v.Prev.Next = v.Next;
            else head = v.Next;
            if (v.Next != null) v.Next.Prev = v.Prev;
            else tail = v.Prev;
            v.Prev = v.Next = null;
        }

        /// A view out of the list: what was written to its objects packed first.
        void Evict(View v)
        {
            Unlink(v);
            views--;
            Evictions++;
            int slot = v.Slot;
            viewOf[slot] = null;
            var day = dayOf[slot];
            var current = shapes.Entries(day);
            if (current == null) SyncTops(slot, v);
            else if (Sync(slot, current, null)) shapes.SetEntries(day, null);
            // Its shape cannot be packed any more: the day keeps the list, as the game has it.
            else FreeSlot(slot);
        }

        /// The day's list against its records: the same, or packed again from it (and the view's
        /// entries with it). False when it can no longer be packed.
        bool Sync(int slot, List<TEntry> list, View v)
        {
            int start = dayStart[slot], n = dayCount[slot];
            bool same = list.Count == n;
            for (int i = 0; same && i < n; i++) same = Same(list[i], start + i);
            if (same) return true;
            if (!Repack(slot, list)) return false;
            if (v != null)
            {
                v.Tops = list.ToArray();
                v.List = list;
            }
            return true;
        }

        /// The entries given out alone against their records (the day's list never was): the
        /// same, or the day packed again from them and its other records. False when it can no
        /// longer be packed: the day then keeps the objects in its `reportEntries`, unpacked.
        bool SyncTops(int slot, View v)
        {
            int start = dayStart[slot], n = dayCount[slot];
            bool same = true;
            for (int i = 0; same && i < n; i++)
                if (v.Tops[i] != null) same = Same(v.Tops[i], start + i);
            if (same) return true;
            var list = new List<TEntry>(n);
            for (int i = 0; i < n; i++) list.Add(v.Tops[i] ?? Make(start + i));
            if (Repack(slot, list)) return true;
            shapes.SetEntries(dayOf[slot], list);
            FreeSlot(slot);
            return false;
        }

        bool Repack(int slot, List<TEntry> list)
        {
            int from = records.Count;
            if (!PackBlock(list, true, out int start))
            {
                records.Truncate(from);
                return false;
            }
            garbage += dayEnd[slot] - dayStart[slot];
            dayStart[slot] = start;
            dayCount[slot] = list.Count;
            dayEnd[slot] = records.Count;
            Writebacks++;
            Repacked?.Invoke(dayOf[slot], list);
            return true;
        }

        /// Whether the entry is what record `r` holds, its children too.
        public bool Same(TEntry e, int r)
        {
            if (e == null || !shapes.Plain(e) || shapes.GameHash(e) != -1) return false;
            ref var rec = ref records[r];
            if (shapes.NoteStorageId(e) != rec.Id || shapes.ReportType(e) != rec.Type || shapes.ContextId(e) != rec.ContextId
                || !Bits(shapes.Accumulate(e), rec.Accumulate) || !Bits(shapes.AccPositive(e), rec.Positive) || !Bits(shapes.AccNegative(e), rec.Negative)
                || shapes.IsChild(e) != (rec.Child != 0) || shapes.ContextSize(e) != rec.Size || shapes.ContextCapacity(e) != rec.Capacity
                || !string.Equals(shapes.Context(e), records.String(rec.Context), StringComparison.Ordinal)) return false;
            var elements = shapes.ContextElements(e);
            if (elements == null) return rec.Length == PackedEntry.NoArray;
            if (elements.Length != rec.Length) return false;
            for (int j = 0; j < rec.Size; j++)
                if (!Same(elements[j], rec.Children + j)) return false;
            for (int j = rec.Size; j < elements.Length; j++)
                if (elements[j] != null) return false;
            return true;
        }

        static bool Bits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

        /// Every view packed back and closed (the end of a save's checks, a test).
        public void SyncAll()
        {
            while (tail != null) Evict(tail);
        }

        /// Each view's writes packed, the views kept (before a save writes the records).
        public void SyncViews()
        {
            for (var v = head; v != null;)
            {
                var next = v.Next;
                int slot = v.Slot;
                var current = shapes.Entries(dayOf[slot]);
                bool kept = current != null ? Sync(slot, current, v) : SyncTops(slot, v);
                if (!kept)
                {
                    if (current != null) FreeSlot(slot);
                    Unlink(v);
                    views--;
                }
                v = next;
            }
        }

        // ---- The colony summary's reads ----

        TEntry[] scratch;
        int scratchNext;
        TEntry[][] scratchChildren;
        Dictionary<int, TEntry[]>[] scratchArrays;
        readonly Dictionary<int, TEntry[]> emptyArrays = new Dictionary<int, TEntry[]>();

        /// DailyReport.GetEntry for a reader that keeps nothing it is given (the colony summary):
        /// the packed entry of that type in one of four scratch objects reused in turn, its
        /// children too. Null when the day is not packed, has a view (its objects may have been
        /// written to), has no entry of the type (GetEntry adds one) or one with grandchildren:
        /// the caller asks GetEntry instead.
        public TEntry Peek(TDay day, int type)
        {
            if (day == null || !slots.TryGetValue(day, out int slot) || viewOf[slot] != null) return null;
            int start = dayStart[slot], n = dayCount[slot], r = -1;
            for (int i = 0; i < n; i++)
                if (records[start + i].Type == type)
                {
                    r = start + i;
                    break;
                }
            if (r < 0) return null;
            ref var rec = ref records[r];
            for (int j = 0; j < rec.Size; j++)
            {
                ref var c = ref records[rec.Children + j];
                if (c.Size != 0) return null;
            }
            if (scratch == null)
            {
                scratch = new TEntry[4];
                scratchChildren = new TEntry[4][];
                scratchArrays = new Dictionary<int, TEntry[]>[4];
                for (int i = 0; i < 4; i++)
                {
                    scratch[i] = shapes.New();
                    scratchChildren[i] = new TEntry[16];
                    scratchArrays[i] = new Dictionary<int, TEntry[]>();
                }
            }
            int s = scratchNext;
            scratchNext = (s + 1) & 3;
            TEntry[] elements = null;
            if (rec.Length != PackedEntry.NoArray)
            {
                if (!scratchArrays[s].TryGetValue(rec.Length, out elements)) scratchArrays[s][rec.Length] = elements = new TEntry[rec.Length];
                if (scratchChildren[s].Length < rec.Size) Array.Resize(ref scratchChildren[s], Math.Max(rec.Size, scratchChildren[s].Length * 2));
                var pool = scratchChildren[s];
                for (int j = 0; j < rec.Size; j++)
                {
                    ref var c = ref records[rec.Children + j];
                    var child = pool[j] ??= shapes.New();
                    shapes.Fill(child, in c, records.String(c.Context), EmptyArray(c.Length));
                    elements[j] = child;
                }
                for (int j = rec.Size; j < elements.Length; j++) elements[j] = null;
            }
            var e = scratch[s];
            shapes.Fill(e, in rec, records.String(rec.Context), elements);
            Peeks++;
            return e;
        }

        /// An array of nulls of that length (a child without children; never written through).
        TEntry[] EmptyArray(ushort length)
        {
            if (length == PackedEntry.NoArray) return null;
            if (!emptyArrays.TryGetValue(length, out var a)) emptyArrays[length] = a = new TEntry[length];
            return a;
        }

        // ---- Saves and upkeep ----

        List<int> given;

        /// Every packed day's list in its `reportEntries` (a writer of the game's that reads the
        /// fields runs next; `lists`: these lists where they have the day, verify mode's
        /// originals), until Take. Without Take (the tweak stands down) the days keep them.
        public void Give(Dictionary<TDay, List<TEntry>> lists = null)
        {
            given = new List<int>();
            foreach (var pair in slots)
            {
                int slot = pair.Value;
                var view = viewOf[slot];
                if (view?.List != null) continue;
                List<TEntry> list = null;
                if (lists == null || !lists.TryGetValue(pair.Key, out list))
                {
                    int start = dayStart[slot], n = dayCount[slot];
                    list = new List<TEntry>(n);
                    for (int i = 0; i < n; i++) list.Add(view?.Tops[i] ?? Make(start + i));
                }
                shapes.SetEntries(pair.Key, list);
                given.Add(slot);
            }
        }

        /// The days Give gave their lists packed again (nothing wrote to them).
        public void Take()
        {
            if (given == null) return;
            foreach (int slot in given)
                if (dayOf[slot] != null) shapes.SetEntries(dayOf[slot], null);
            given = null;
        }

        /// Days the store holds that `days` no longer lists (a mod that trims the history) go,
        /// with their views.
        public int Prune(List<TDay> days)
        {
            if (slots.Count <= (days?.Count ?? 0)) return 0;
            var listed = new HashSet<TDay>(days ?? new List<TDay>(), ReferenceComparer<TDay>.Instance);
            var gone = new List<int>();
            foreach (var pair in slots)
                if (!listed.Contains(pair.Key)) gone.Add(pair.Value);
            foreach (int slot in gone)
            {
                var v = viewOf[slot];
                if (v != null)
                {
                    Unlink(v);
                    views--;
                }
                FreeSlot(slot);
            }
            return gone.Count;
        }

        /// The records again without the blocks no day uses, when those are most of them.
        public bool Compact(bool force = false)
        {
            if (!force && (garbage < 1 << 20 || garbage * 2 < records.Count)) return false;
            var old = records.TakeChunks(out _);
            ref PackedEntry Old(int i) => ref old[i >> PackedReportRecords.Shift][i & PackedReportRecords.Mask];
            foreach (var pair in slots)
            {
                int slot = pair.Value, from = dayStart[slot], end = dayEnd[slot];
                int to = records.Allocate(end - from), shift = to - from;
                for (int i = from; i < end; i++)
                {
                    ref var r = ref records[i + shift];
                    r = Old(i);
                    if (r.Size > 0) r.Children += shift;
                }
                dayStart[slot] = to;
                dayEnd[slot] = to + (end - from);
            }
            garbage = 0;
            Compactions++;
            return true;
        }

        // ---- Verify mode ----

        /// Where two lists of entries differ (each entry's fields, its children, the arrays' nulls), or null.
        public string Differs(List<TEntry> a, List<TEntry> b)
        {
            if (a == null || b == null) return a == b ? null : "one list is null";
            if (a.Count != b.Count) return $"{a.Count} entries against {b.Count}";
            for (int i = 0; i < a.Count; i++)
                if (Differs(a[i], b[i], $"entry {i}") is { } why) return why;
            return null;
        }

        public string Differs(TEntry a, TEntry b, string where)
        {
            if (a == null || b == null) return a == b ? null : $"{where}: one is null";
            if (shapes.NoteStorageId(a) != shapes.NoteStorageId(b)) return $"{where}: noteStorageId";
            if (shapes.GameHash(a) != shapes.GameHash(b)) return $"{where}: gameHash";
            if (shapes.ReportType(a) != shapes.ReportType(b)) return $"{where}: reportType";
            if (!string.Equals(shapes.Context(a), shapes.Context(b), StringComparison.Ordinal)) return $"{where}: context";
            if (shapes.ContextId(a) != shapes.ContextId(b)) return $"{where}: contextId";
            if (!Bits(shapes.Accumulate(a), shapes.Accumulate(b))) return $"{where}: accumulate";
            if (!Bits(shapes.AccPositive(a), shapes.AccPositive(b))) return $"{where}: accPositive";
            if (!Bits(shapes.AccNegative(a), shapes.AccNegative(b))) return $"{where}: accNegative";
            if (shapes.IsChild(a) != shapes.IsChild(b)) return $"{where}: isChild";
            if (shapes.ContextSize(a) != shapes.ContextSize(b)) return $"{where}: context size";
            if (shapes.ContextCapacity(a) != shapes.ContextCapacity(b)) return $"{where}: context capacity";
            var ea = shapes.ContextElements(a);
            var eb = shapes.ContextElements(b);
            if (ea == null || eb == null) return ea == eb ? null : $"{where}: one context array is null";
            if (ea.Length != eb.Length) return $"{where}: context array of {ea.Length} against {eb.Length}";
            for (int j = 0; j < ea.Length; j++)
                if (Differs(ea[j], eb[j], $"{where} context {j}") is { } why) return why;
            return null;
        }

        /// A copy of the entries (verify mode's record of what the game would hold).
        public List<TEntry> Copy(List<TEntry> list)
        {
            var copy = new List<TEntry>(list.Count);
            foreach (var e in list) copy.Add(Copy(e));
            return copy;
        }

        TEntry Copy(TEntry e)
        {
            if (e == null) return null;
            var src = shapes.ContextElements(e);
            TEntry[] elements = null;
            if (src != null)
            {
                elements = new TEntry[src.Length];
                for (int j = 0; j < src.Length; j++) elements[j] = Copy(src[j]);
            }
            var rec = new PackedEntry
            {
                Id = shapes.NoteStorageId(e), Type = shapes.ReportType(e), ContextId = shapes.ContextId(e),
                Accumulate = shapes.Accumulate(e), Positive = shapes.AccPositive(e), Negative = shapes.AccNegative(e),
                Size = (ushort)shapes.ContextSize(e), Capacity = (ushort)shapes.ContextCapacity(e), Child = shapes.IsChild(e) ? (byte)1 : (byte)0,
            };
            var c = shapes.New();
            shapes.Fill(c, in rec, shapes.Context(e), elements);
            return c;
        }
    }
}
