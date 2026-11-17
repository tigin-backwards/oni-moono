using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using OniMoono.GameTweaks;
using OniMoono.Host.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// compact-report-entries' store (src/OniMoono.GameTweaks/CompactReportDaysCore.cs, compiled in
    /// here) over the same stand-ins for DailyReport, ReportEntry and ArrayRef as
    /// fast-serialization's writer tests (ReportEntriesTests): what a packed day gives out again
    /// must be the entries as they were, field for field, and a save written from the records
    /// must be the game's serializer's bytes over the original objects. Also the host's scan of
    /// assemblies for code that reads `reportEntries` itself (ReportAccessScan).
    public partial class ReportEntriesTests
    {
        struct DayShapes : IReportDayShapes<Day, Entry>
        {
            public int Day(Day day) => day.day;
            public List<Entry> Entries(Day day) => day.reportEntries;
            public int NoteStorageId(Entry entry) => entry.noteStorageId;
            public int GameHash(Entry entry) => entry.gameHash;
            public int ReportType(Entry entry) => (int)entry.reportType;
            public string Context(Entry entry) => entry.context;
            public int ContextId(Entry entry) => entry.contextId;
            public float Accumulate(Entry entry) => entry.accumulate;
            public float AccPositive(Entry entry) => entry.accPositive;
            public float AccNegative(Entry entry) => entry.accNegative;
            public Entry[] ContextElements(Entry entry) => entry.contextEntries.elements;
            public int ContextSize(Entry entry) => entry.contextEntries.sizeImpl;
            public int ContextCapacity(Entry entry) => entry.contextEntries.capacityImpl;
            public void DayTemplate() { }
            public void EntryTemplate() { }
            public void ContextTemplate() { }
            public void LargeString(string text, int byteCount) { }
            public Entry New() => new Entry();
            public void Fill(Entry e, in PackedEntry r, string context, Entry[] elements)
            {
                e.noteStorageId = r.Id;
                e.gameHash = -1;
                e.reportType = (Kind)r.Type;
                e.context = context;
                e.contextId = r.ContextId;
                e.accumulate = r.Accumulate;
                e.accPositive = r.Positive;
                e.accNegative = r.Negative;
                e.contextEntries = new Refs { elements = elements, sizeImpl = r.Size, capacityImpl = r.Capacity };
                e.isChild = r.Child != 0;
            }
            public bool IsChild(Entry entry) => entry.isChild;
            public bool Plain(Entry entry) => entry.GetType() == typeof(Entry);
            public void SetEntries(Day day, List<Entry> entries) => day.reportEntries = entries;
        }

        static ReportDayStore<Day, Entry, DayShapes> NewStore() => new ReportDayStore<Day, Entry, DayShapes>();

        /// Entries of the shapes the game makes: no gameHash, no nulls in a list, children only
        /// one level down mostly, with room left in the arrays as ArrayRef's doubling leaves it.
        static List<Day> GameShapedHistory(Random random, int days, int deep = 20)
        {
            string[] names = { null, "", "Ada", "Meep", "Stinky", "中文", "Hatch", new string('n', 300), new string('中', 400) };
            int id = 1;
            Entry Make(int depth, bool child)
            {
                var e = new Entry
                {
                    noteStorageId = id++, reportType = (Kind)random.Next(3), context = child ? names[random.Next(names.Length)] : null,
                    contextId = random.Next(4) == 0 ? random.Next() : 0, isChild = child,
                    accumulate = (float)(random.NextDouble() * 1000 - 500), accPositive = (float)random.NextDouble(), accNegative = -(float)random.NextDouble(),
                };
                if (random.Next(5) == 0) e.accPositive = BitConverter.Int32BitsToSingle(random.Next());
                int kind = random.Next(4);
                if (kind == 1) e.contextEntries = new Refs { elements = new Entry[random.Next(3)], capacityImpl = random.Next(3) };
                else if (kind >= 2 && (depth == 0 || depth < 3 && random.Next(deep) == 0))
                {
                    int size = random.Next(1, 12), capacity = 1;
                    while (capacity < size) capacity *= 2;
                    var elements = new Entry[capacity];
                    for (int i = 0; i < size; i++) elements[i] = Make(depth + 1, true);
                    e.contextEntries = new Refs { elements = elements, sizeImpl = size, capacityImpl = capacity };
                }
                return e;
            }
            var history = new List<Day>();
            for (int d = 0; d < days; d++)
            {
                var day = new Day { day = d };
                int n = random.Next(8) == 0 ? 0 : random.Next(1, 30);
                for (int i = 0; i < n; i++) day.reportEntries.Add(Make(0, false));
                // A mod's report type: any int.
                if (random.Next(4) == 0) day.reportEntries.Add(new Entry { noteStorageId = id++, reportType = (Kind)random.Next(1000, int.MaxValue) });
                history.Add(day);
            }
            return history;
        }

        static string Differs(ReportDayStore<Day, Entry, DayShapes> store, Entry a, Entry b, string where) => store.Differs(a, b, where);

        [Fact]
        public void PackedDaysGiveBackTheirEntries()
        {
            var random = new Random(20261117);
            var store = NewStore();
            var days = GameShapedHistory(random, 400);
            var originals = days.Select(d => store.Copy(d.reportEntries)).ToList();
            foreach (var day in days) Assert.True(store.Pack(day, checkShared: true), $"day {day.day} not packed");
            Assert.All(days, d => Assert.Null(d.reportEntries));
            Assert.Equal(400, store.Days);
            for (int i = 0; i < days.Count; i++)
            {
                var day = days[i];
                // Entry by entry, as GetEntry asks: the first of each type.
                foreach (Kind kind in new[] { Kind.Zero, Kind.One, Kind.Two })
                {
                    var expected = originals[i].FirstOrDefault(e => e.reportType == kind);
                    var got = store.Top(day, (int)kind, out bool packed);
                    Assert.True(packed);
                    Assert.Null(Differs(store, got, expected, $"day {i} {kind}"));
                    if (got != null) Assert.Same(got, store.Top(day, (int)kind, out _));
                    var peeked = store.Peek(day, (int)kind);
                    // A day with a view is not peeked: its objects are given out.
                    Assert.Null(peeked);
                }
                var full = store.Full(day);
                Assert.Same(full, day.reportEntries);
                Assert.Null(store.Differs(full, originals[i]));
                foreach (Kind kind in new[] { Kind.Zero, Kind.One, Kind.Two })
                    if (full.FirstOrDefault(e => e.reportType == kind) is { } first) Assert.Same(first, store.Top(day, (int)kind, out _) ?? first);
            }
            // The views beyond the last 32 closed again, unchanged.
            Assert.Equal(32, store.Views);
            Assert.Equal(368, days.Count(d => d.reportEntries == null));
            store.SyncAll();
            Assert.All(days, d => Assert.Null(d.reportEntries));
            Assert.Equal(0, store.Writebacks);
        }

        [Fact]
        public void PeekFillsScratchWithTheEntry()
        {
            var random = new Random(7);
            var store = NewStore();
            var days = GameShapedHistory(random, 300, deep: int.MaxValue);
            var originals = days.Select(d => store.Copy(d.reportEntries)).ToList();
            foreach (var day in days) Assert.True(store.Pack(day, checkShared: false));
            int peeked = 0;
            for (int i = 0; i < days.Count; i++)
                foreach (Kind kind in new[] { Kind.Zero, Kind.One, Kind.Two, (Kind)12345 })
                {
                    var expected = originals[i].FirstOrDefault(e => e.reportType == kind);
                    var got = store.Peek(days[i], (int)kind);
                    if (expected == null)
                    {
                        Assert.Null(got);
                        continue;
                    }
                    Assert.NotNull(got);
                    peeked++;
                    Assert.Null(Differs(store, got, expected, $"day {i} {kind}"));
                }
            Assert.True(peeked > 500);
            // Nothing given out: no views.
            Assert.Equal(0, store.Views);
        }

        [Fact]
        public void ShapesNotKeptExactlyStayUnpacked()
        {
            var store = NewStore();
            Day Of(params Entry[] entries) => new Day { day = 1, reportEntries = entries.ToList() };
            var hashed = E(1);
            hashed.gameHash = 5;
            var past = E(2);
            past.contextEntries = new Refs { elements = new[] { E(3), E(4) }, sizeImpl = 1, capacityImpl = 2 };
            var hole = E(5);
            hole.contextEntries = new Refs { elements = new[] { null, E(6) }, sizeImpl = 2, capacityImpl = 2 };
            var over = E(7);
            over.contextEntries = new Refs { elements = new[] { E(8) }, sizeImpl = 2, capacityImpl = 1 };
            var shared = E(9);
            var wide = E(10);
            wide.contextEntries = new Refs { elements = new Entry[70000], sizeImpl = 0, capacityImpl = 70000 };
            var odd = E(11);
            odd.contextEntries = new Refs { elements = null, sizeImpl = 1, capacityImpl = 0 };
            var cases = new[]
            {
                Of(hashed), Of(E(12), null), Of(past), Of(hole), Of(over), Of(E(13, null, shared), shared), Of(wide), Of(odd),
                new Day { day = 2, reportEntries = null },
            };
            foreach (var day in cases)
            {
                var before = day.reportEntries;
                Assert.False(store.Pack(day, checkShared: true));
                Assert.Same(before, day.reportEntries);
            }
            Assert.Equal(0, store.Days);
            Assert.Equal(0, store.Records.Count);
            // Shared entries are refused only when asked to look (a loaded day is a tree).
            Assert.True(store.Pack(Of(E(14, null, shared), shared), checkShared: false));
            // An empty day packs, and gives back an empty list.
            var empty = new Day { day = 3, reportEntries = new List<Entry>() };
            Assert.True(store.Pack(empty, checkShared: true));
            Assert.Null(store.Top(empty, 0, out bool packed));
            Assert.True(packed);
            Assert.Empty(store.Full(empty));
        }

        [Fact]
        public void WritesToEntriesGivenOutAreKept()
        {
            var random = new Random(11);
            var store = NewStore();
            store.ViewCapacity = 2;
            var days = GameShapedHistory(random, 10).Where(d => d.reportEntries.Count > 2).ToList();
            var originals = days.Select(d => store.Copy(d.reportEntries)).ToList();
            foreach (var day in days) Assert.True(store.Pack(day, checkShared: true));
            // A value written through an entry given out alone.
            int kind = (int)originals[0][0].reportType;
            store.Top(days[0], kind, out _).accPositive += 1;
            originals[0][0].accPositive += 1;
            // An entry added to a list given out whole, as GetEntry adds one.
            store.Full(days[1]).Add(new Entry { noteStorageId = 999, reportType = (Kind)77 });
            originals[1].Add(new Entry { noteStorageId = 999, reportType = (Kind)77 });
            // A child added to an entry's ArrayRef, in both.
            void Grow(Entry parent)
            {
                var refs = parent.contextEntries;
                var grown = new Entry[Math.Max(1, (refs.elements?.Length ?? 0) * 2)];
                refs.elements?.CopyTo(grown, 0);
                grown[refs.sizeImpl] = new Entry { noteStorageId = 1000, context = "new", isChild = true };
                parent.contextEntries = new Refs { elements = grown, sizeImpl = refs.sizeImpl + 1, capacityImpl = grown.Length };
            }
            Grow(store.Full(days[2])[0]);
            Grow(originals[2][0]);
            // Other days push those views out.
            for (int i = 3; i < days.Count; i++) store.Full(days[i]);
            store.SyncAll();
            Assert.Equal(3, store.Writebacks);
            Assert.All(days, d => Assert.Null(d.reportEntries));
            for (int i = 0; i < 3; i++) Assert.Null(store.Differs(store.Full(days[i]), originals[i]));
            Assert.Equal(77, (int)store.Full(days[1]).Last().reportType);
            // A day changed into a shape not kept: it keeps its objects, unpacked.
            var broken = store.Full(days[3]);
            broken.Add(null);
            store.SyncAll();
            Assert.Same(broken, days[3].reportEntries);
            Assert.False(store.IsPacked(days[3]));
        }

        /// A save written from the records: the bytes, templates and warnings of the game's
        /// serializer over the original entries, with packed days, days given out and days not
        /// packed mixed in one history.
        [Fact]
        public void SavesFromTheRecordsAreTheGames()
        {
            var random = new Random(20261118);
            var store = NewStore();
            store.ViewCapacity = 4;
            var days = GameShapedHistory(random, 600);
            var copies = days.Select(d => new Day { day = d.day, reportEntries = store.Copy(d.reportEntries) }).ToList();
            // A day the store does not keep, and a null day.
            var hashed = E(5);
            hashed.gameHash = 3;
            days.Insert(17, new Day { day = 17, reportEntries = new List<Entry> { hashed } });
            copies.Insert(17, new Day { day = 17, reportEntries = new List<Entry> { hashed } });
            days.Insert(30, null);
            copies.Insert(30, null);
            foreach (var day in days) store.Pack(day, checkShared: true);
            Assert.Equal(600, store.Days);
            writer.Packed = store;
            try
            {
                var today = D(-1, E(1, "today", E(2, "child")));
                SamePacked(days, copies, today);
                // Some days given out, one written to (in the original copies too).
                for (int i = 100; i < 110; i++) store.Full(days[i]);
                if (store.Top(days[201], (int)Kind.Two, out _) is { } e)
                {
                    e.accumulate = 42;
                    copies[201].reportEntries.First(c => c.reportType == Kind.Two).accumulate = 42;
                }
                SamePacked(days, copies, today);
                Assert.True(store.Writebacks <= 1);
                // Trimmed and compacted: the same bytes.
                var kept = days.Skip(300).ToList();
                var keptCopies = copies.Skip(300).ToList();
                store.Prune(kept);
                Assert.True(store.Compact(force: true));
                SamePacked(kept, keptCopies, today);
            }
            finally
            {
                writer.Packed = null;
            }
        }

        void SamePacked(List<Day> days, List<Day> originals, Day today)
        {
            var game = new Game();
            var theirs = new MemoryStream();
            using (var w = new BinaryWriter(theirs, Encoding.UTF8, true))
            {
                w.Write(0x11223344);
                game.WriteValue(w, typeof(List<Day>), originals);
                game.WriteValue(w, typeof(Day), today);
            }
            templates = new List<Type>();
            warnings = new List<string>();
            var ours = new MemoryStream();
            using (var w = new BinaryWriter(ours, Encoding.UTF8, true))
            {
                w.Write(0x11223344);
                writer.WriteDays(w, days);
                writer.WriteDay(w, today);
            }
            Assert.True(theirs.ToArray().AsSpan().SequenceEqual(ours.ToArray()), $"{ours.Length} bytes against the game's {theirs.Length}");
            Assert.Equal(game.Templates, templates);
            Assert.Equal(game.Warnings, warnings);
        }

        [Fact]
        public void GiveAndTake()
        {
            var store = NewStore();
            var days = GameShapedHistory(new Random(3), 50);
            var copies = days.Select(d => store.Copy(d.reportEntries)).ToList();
            foreach (var day in days) store.Pack(day, checkShared: false);
            store.Full(days[5]);
            store.Give();
            for (int i = 0; i < days.Count; i++) Assert.Null(store.Differs(days[i].reportEntries, copies[i]));
            store.Take();
            for (int i = 0; i < days.Count; i++)
                if (i != 5) Assert.Null(days[i].reportEntries);
            Assert.NotNull(days[5].reportEntries);
            // Given others' lists (verify mode's originals).
            var originals = new Dictionary<Day, List<Entry>> { [days[0]] = copies[0] };
            store.Give(originals);
            Assert.Same(copies[0], days[0].reportEntries);
            store.Take();
            Assert.Null(days[0].reportEntries);
        }

        // ---- ReportAccessScan ----

        /// Only to be found: this assembly names the field, as a mod using reflection would.
        const string ByName = "reportEntries";

        [Fact]
        public void ScanFindsTheFieldNamed()
        {
            Assert.Equal("reportEntries", ByName);
            Assert.NotNull(ReportAccessScan.ModAccess(typeof(ReportEntriesTests).Assembly));
            Assert.Null(ReportAccessScan.ModAccess(typeof(object).Assembly));
            Assert.Null(ReportAccessScan.ModAccess(typeof(Xunit.Assert).Assembly));
        }

        [Fact]
        public void ScanFindsTheGamesOwnAccesses()
        {
            var dir = GameInstall.DataDir;
            var path = Path.Combine(dir, "Managed", "Assembly-CSharp.dll");
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file);
            var sites = ReportAccessScan.FieldSites(pe);
            Assert.Equal(new[]
            {
                "ReportManager/DailyReport::.ctor ldfld stfld",
                "ReportManager/DailyReport::GetEntry ldfld",
                "SaveLoader::GetDailyReportMetrics ldfld",
                "SimpleInfoScreen::RefreshStressPanel ldfld",
            }, sites);
            Assert.Null(ReportAccessScan.ModAccess(pe.GetMetadataReader()));
        }

        [Fact]
        public void ScanReadsTheIl()
        {
            var tokens = new HashSet<int> { 0x04000010 };
            byte[] Il(params byte[] b) => b;
            // ldarg.0; ldfld 0x04000010; ret
            Assert.Equal("ldfld", ReportAccessScan.Accesses(Il(0x02, 0x7B, 0x10, 0, 0, 0x04, 0x2A), tokens));
            // ldc.i4 whose operand spells the opcode and token: not an access.
            Assert.Null(ReportAccessScan.Accesses(Il(0x20, 0x7B, 0x10, 0, 0, 0x2A), tokens));
            // switch over two targets, then ldflda and stfld; a two-byte opcode (ldloc 0) between.
            Assert.Equal("ldflda stfld", ReportAccessScan.Accesses(Il(0x45, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFE, 0x0C, 0, 0, 0x7C, 0x10, 0, 0, 0x04, 0x7D, 0x10, 0, 0, 0x04, 0x2A), tokens));
            Assert.Equal("ldtoken", ReportAccessScan.Accesses(Il(0xD0, 0x10, 0, 0, 0x04, 0x2A), tokens));
            Assert.Null(ReportAccessScan.Accesses(Il(0x7B, 0x11, 0, 0, 0x04, 0x2A), tokens));
        }
    }
}
