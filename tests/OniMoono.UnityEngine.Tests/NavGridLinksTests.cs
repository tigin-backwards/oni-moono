using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// compact-nav-links' store (src/OniMoono.Host/NavGridLinks.cs, compiled in here) against the
    /// game's flat array: the links of real colonies (NavProbe's dumps of DeCrew's Duplicant and
    /// baby critter grids in TestData; every grid of c100, DeCrew and soggy lab 2 with
    /// ONI_NAVPROBE_DUMPS=DIR, the spike's out/navprobe) written as CreateLinks writes them, from
    /// several threads, and read as the pathfinder reads them.
    public class NavGridLinksTests
    {
        /// NavGrid.Link's layout.
        struct Link
        {
            public int link;
            public byte start, end, transition, cost;

            public override string ToString() => $"{link}:{start}>{end}#{transition}/{cost}";
        }

        sealed class Dump
        {
            public string Name = "";
            public int Cells, Max;
            public byte[] Counts = [];
            public Link[][] Lists = [];
        }

        static Dump Read(string name, Stream stream)
        {
            using var r = new BinaryReader(stream);
            var dump = new Dump { Name = name, Cells = r.ReadInt32(), Max = r.ReadInt32() };
            dump.Counts = r.ReadBytes(dump.Cells);
            dump.Lists = new Link[dump.Cells][];
            for (int c = 0; c < dump.Cells; c++)
            {
                var list = dump.Lists[c] = new Link[dump.Counts[c]];
                for (int k = 0; k < list.Length; k++)
                    list[k] = new Link { link = r.ReadInt32(), start = r.ReadByte(), end = r.ReadByte(), transition = r.ReadByte(), cost = r.ReadByte() };
            }
            return dump;
        }

        static IEnumerable<Dump> Dumps()
        {
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData"), "navlinks-*.bin.gz").Order(StringComparer.Ordinal))
                yield return Read(Path.GetFileName(file), new GZipStream(File.OpenRead(file), CompressionMode.Decompress));
            var more = Environment.GetEnvironmentVariable("ONI_NAVPROBE_DUMPS");
            if (string.IsNullOrEmpty(more) || !Directory.Exists(more)) yield break;
            foreach (var file in Directory.GetFiles(more, "*.bin", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                yield return Read(Path.GetFileName(file), File.OpenRead(file));
        }

        static Link[] Store(int cells, int max)
        {
            NavGridLinks.Mode = NavGridLinks.Compact;
            var handle = NavGridLinks.New<Link>(cells * max, max);
            Assert.True(NavGridLinks.IsHandle(handle));
            return handle;
        }

        /// NavGridUpdater.CreateLinks(cell, …)'s writes: the links from the cell's first slot,
        /// then -1 after them (into the next cell when they overflow).
        static void CreateLinks(Link[] links, int cell, int max, Link[] list)
        {
            int at = cell * max;
            foreach (var l in list) NavGridLinks.Store(links, at++, l);
            NavGridLinks.StoreLink(links, at, -1);
        }

        /// The cell's links as AddPotentials' first loop reads them (to -1 or the cell's end).
        static List<Link> Bounded(Link[] links, int cell, int max, bool walk, ref NavLinkCursor<Link> cursor)
        {
            var result = new List<Link>();
            for (int i = cell * max; i < cell * max + max; i++)
            {
                var l = walk ? NavGridLinks.Walk(links, i, ref cursor) : NavGridLinks.Load(links, i);
                if (l.link == -1) break;
                result.Add(l);
            }
            return result;
        }

        /// As ValidatePath and TravelTube read them: to -1, past the cell's end if it overflowed.
        static List<Link> ToMarker(Link[] links, int cell, int max, ref NavLinkCursor<Link> cursor)
        {
            var result = new List<Link>();
            int i = cell * max;
            var l = NavGridLinks.Walk(links, i, ref cursor);
            while (l.link != -1)
            {
                result.Add(l);
                l = NavGridLinks.Walk(links, ++i, ref cursor);
            }
            return result;
        }

        static void Same(Link[] flat, Link[] store, int cells, int max, string what)
        {
            var flatCursor = new NavLinkCursor<Link>();
            var storeCursor = new NavLinkCursor<Link>();
            var walkCursor = new NavLinkCursor<Link>();
            for (int c = 0; c < cells; c++)
            {
                var expected = Bounded(flat, c, max, walk: false, ref flatCursor);
                var loaded = Bounded(store, c, max, walk: false, ref storeCursor);
                var walked = Bounded(store, c, max, walk: true, ref walkCursor);
                if (!expected.SequenceEqual(loaded) || !expected.SequenceEqual(walked))
                    Assert.Fail($"{what}: cell {c}: flat [{string.Join(" ", expected)}], Load [{string.Join(" ", loaded)}], Walk [{string.Join(" ", walked)}]");
            }
            Assert.Equal(flat.Length, NavGridLinks.Length(store));
        }

        [Fact]
        public void DumpsReplayedFromSeveralThreadsReadAsTheFlatArray()
        {
            int dumps = 0;
            foreach (var dump in Dumps())
            {
                dumps++;
                var flat = new Link[dump.Cells * dump.Max];
                var store = Store(dump.Cells, dump.Max);
                // The load's build: one job a row of cells (here: interleaved blocks of 256), on
                // every core at once.
                int rows = (dump.Cells + 255) / 256;
                Parallel.For(0, rows, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) }, row =>
                {
                    for (int c = row * 256; c < Math.Min(dump.Cells, row * 256 + 256); c++)
                    {
                        CreateLinks(flat, c, dump.Max, dump.Lists[c]);
                        CreateLinks(store, c, dump.Max, dump.Lists[c]);
                    }
                });
                Same(flat, store, dump.Cells, dump.Max, dump.Name);
                var cursor = new NavLinkCursor<Link>();
                for (int c = 0; c < dump.Cells; c++) Assert.Equal(dump.Lists[c], ToMarker(store, c, dump.Max, ref cursor));
                // Then the updates' rebuilds: cells with lists of other cells (longer and shorter).
                var random = new Random(dump.Cells);
                for (int round = 0; round < 3; round++)
                {
                    var cells = Enumerable.Range(0, dump.Cells / 8).Select(_ => random.Next(dump.Cells)).Distinct().ToArray();
                    var sources = cells.Select(_ => dump.Lists[random.Next(dump.Cells)]).ToArray();
                    Parallel.For(0, cells.Length, i =>
                    {
                        CreateLinks(store, cells[i], dump.Max, sources[i]);
                    });
                    for (int i = 0; i < cells.Length; i++) CreateLinks(flat, cells[i], dump.Max, sources[i]);
                    Same(flat, store, dump.Cells, dump.Max, $"{dump.Name}, rebuild {round}");
                }
            }
            Assert.True(dumps >= 2, "the TestData dumps are missing");
        }

        static Link Make(int cell, int version, int k) => new() { link = cell, start = (byte)(k % 11), end = (byte)(version & 0xFF), transition = (byte)k, cost = (byte)(version >> 8) };

        /// Readers racing writers: never out of range, and every list read is one a reader of the
        /// game's array can see: each slot k holds a link some version of the cell had at k.
        [Fact]
        public async Task ReadersRacingWritersSeeOldNewOrMixedLists()
        {
            const int cells = 4096, max = 20;
            var store = Store(cells, max);
            var written = new int[cells]; // the newest version committed (versions only grow)
            for (int c = 0; c < cells; c++) CreateLinks(store, c, max, []);
            using var stop = new CancellationTokenSource();
            long reads = 0;
            var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
            {
                var random = new Random(r);
                var cursor = new NavLinkCursor<Link>();
                while (!stop.IsCancellationRequested)
                {
                    int c = random.Next(cells);
                    int newest = Volatile.Read(ref written[c]) + 1;
                    try
                    {
                        var list = (random.Next(2) == 0) ? Bounded(store, c, max, walk: true, ref cursor) : Bounded(store, c, max, walk: false, ref cursor);
                        for (int k = 0; k < list.Count; k++)
                        {
                            var l = list[k];
                            int version = l.end | l.cost << 8;
                            if (l.link != c || l.transition != k || version > Volatile.Read(ref written[c]) + 1)
                                failures.Enqueue($"cell {c} slot {k}: {l} (newest {newest})");
                        }
                    }
                    catch (Exception e)
                    {
                        failures.Enqueue($"cell {c}: {e.GetType().Name}: {e.Message}");
                    }
                    Interlocked.Increment(ref reads);
                }
            })).ToArray();
            // One writer at a time per cell, several writers: as the main thread's updates and
            // ONI Lag Fix's parallel ones.
            Parallel.For(0, 4, w =>
            {
                var random = new Random(100 + w);
                for (int n = 0; n < 200_000; n++)
                {
                    int c = random.Next(cells / 4) * 4 + w;
                    int version = written[c] + 1;
                    int count = random.Next(max);
                    var list = Enumerable.Range(0, count).Select(k => Make(c, version, k)).ToArray();
                    CreateLinks(store, c, max, list);
                    Volatile.Write(ref written[c], version);
                }
            });
            stop.Cancel();
            await Task.WhenAll(readers);
            Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(10)));
            Assert.True(reads > 1000, $"{reads} reads");
            // Quiet again: exactly the last version of each cell.
            var cursor = new NavLinkCursor<Link>();
            for (int c = 0; c < cells; c++)
                foreach (var (l, k) in Bounded(store, c, max, walk: true, ref cursor).Select((l, k) => (l, k)))
                    Assert.Equal(written[c], l.end | l.cost << 8);
        }

        /// A cell with more links than slots writes into the next cell's slots, as the game's
        /// array does ("Out of nav links"); writes the pathfinder never makes stay as the array
        /// has them where the store can hold them.
        [Fact]
        public void OverflowAndOtherWritesReadAsTheFlatArray()
        {
            const int cells = 64, max = 6;
            var flat = new Link[cells * max];
            var store = Store(cells, max);
            void Both(Action<Link[]> write)
            {
                write(flat);
                write(store);
            }
            var random = new Random(7);
            Link[] List(int cell, int count) => Enumerable.Range(0, count).Select(k => Make(cell, random.Next(1000), k)).ToArray();
            for (int c = 0; c < cells; c++)
            {
                var list = List(c, random.Next(max));
                Both(a => CreateLinks(a, c, max, list));
            }
            // Cell 10 overflows by 3 (into 11), cell 20 by a whole cell and 2 (into 21 and 22),
            // cell 30 ends exactly at its last slot (the -1 is cell 31's first).
            var overflow10 = List(10, max + 3);
            var overflow20 = List(20, 2 * max + 2);
            var exact30 = List(30, max);
            Both(a => CreateLinks(a, 10, max, overflow10));
            Both(a => CreateLinks(a, 20, max, overflow20));
            Both(a => CreateLinks(a, 30, max, exact30));
            Same(flat, store, cells, max, "overflow");
            var cursor = new NavLinkCursor<Link>();
            var flatCursor = new NavLinkCursor<Link>();
            foreach (var c in new[] { 10, 20, 30 }) Assert.Equal(ToMarker(flat, c, max, ref flatCursor), ToMarker(store, c, max, ref cursor));
            // A -1 written into the middle of a cell's links cuts them; one after them changes nothing.
            Both(a => NavGridLinks.StoreLink(a, 40 * max + 1, -1));
            Both(a => NavGridLinks.StoreLink(a, 41 * max + max - 1, -1));
            // A link's target changed in place.
            var list42 = List(42, 3);
            Both(a => CreateLinks(a, 42, max, list42));
            Both(a => NavGridLinks.StoreLink(a, 42 * max + 1, 12345));
            // A run of links not starting at a cell's first slot.
            var list43 = List(43, 4);
            Both(a => CreateLinks(a, 43, max, list43));
            var tail = List(43, 2);
            Both(a =>
            {
                NavGridLinks.Store(a, 43 * max + 2, tail[0]);
                NavGridLinks.Store(a, 43 * max + 3, tail[1]);
                NavGridLinks.StoreLink(a, 43 * max + 4, -1);
            });
            Same(flat, store, cells, max, "other writes");
            // Out of range, as the array.
            Assert.Throws<IndexOutOfRangeException>(() => NavGridLinks.Load(store, cells * max));
            Assert.Throws<IndexOutOfRangeException>(() => NavGridLinks.Store(store, -1, default));
            Assert.Throws<IndexOutOfRangeException>(() => NavGridLinks.StoreLink(store, cells * max, -1));
            Assert.Throws<IndexOutOfRangeException>(() => NavGridLinks.Walk(store, cells * max, ref cursor));
        }

        /// Arrays that are not handles (the scratch pad's, a mod's own, flat mode) are indexed as
        /// before by every helper.
        [Fact]
        public void PlainArraysAreIndexedAsBefore()
        {
            foreach (int length in new[] { 1, 2, 56 })
            {
                var a = new Link[length];
                NavGridLinks.Store(a, length - 1, Make(3, 4, 5));
                Assert.Equal(Make(3, 4, 5), NavGridLinks.Load(a, length - 1));
                NavGridLinks.StoreLink(a, 0, -1);
                Assert.Equal(-1, a[0].link);
                var cursor = new NavLinkCursor<Link>();
                Assert.Equal(-1, NavGridLinks.Walk(a, 0, ref cursor).link);
                Assert.Equal(length, NavGridLinks.Length(a));
                Assert.False(NavGridLinks.IsHandle(a));
                Assert.Throws<IndexOutOfRangeException>(() => NavGridLinks.Load(a, length));
                Assert.Throws<IndexOutOfRangeException>(() => NavGridLinks.Walk(a, length, ref cursor));
            }
        }

        /// A map of 2^20 cells or more, or more than 255 slots a cell, takes 64-bit heads.
        [Fact]
        public void LargeMapsTakeWideHeads()
        {
            var narrow = NavGridLinks.StoreOf(Store(1000, 56));
            var cells = NavLinkStore<Link>.NarrowCells + 1;
            var wideStore = Store(cells, 3);
            Assert.False(narrow.Wide);
            Assert.True(NavGridLinks.StoreOf(wideStore).Wide);
            var flat = new Link[cells * 3];
            for (int c = 0; c < cells; c++)
            {
                CreateLinks(flat, c, 3, []);
                CreateLinks(wideStore, c, 3, []);
            }
            var random = new Random(3);
            foreach (var c in Enumerable.Range(0, 2000).Select(_ => random.Next(cells)).Append(cells - 1))
            {
                var list = Enumerable.Range(0, random.Next(3)).Select(k => Make(c, 1, k)).ToArray();
                CreateLinks(flat, c, 3, list);
                CreateLinks(wideStore, c, 3, list);
            }
            Same(flat, wideStore, cells, 3, "wide");
            Assert.True(NavGridLinks.StoreOf(Store(10, 300)).Wide);
        }

        /// Verify mode's comparison finds a difference between the store and the array.
        [Fact]
        public void VerifyComparesWithTheFlatArray()
        {
            NavGridLinks.Verify = true;
            try
            {
                var handle = Store(100, 8);
                var store = NavGridLinks.StoreOf(handle);
                for (int c = 0; c < 100; c++) CreateLinks(handle, c, 8, Enumerable.Range(0, c % 8).Select(k => Make(c, 1, k)).ToArray());
                Assert.Equal(0, store.Verify(out long links, out _));
                Assert.Equal(Enumerable.Range(0, 100).Sum(c => c % 8), links);
                store.Flat![5 * 8 + 1].cost = 99;
                Assert.Equal(1, store.Verify(out _, out var first));
                Assert.Contains("cell 5 slot 1", first);
            }
            finally
            {
                NavGridLinks.Verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
            }
        }
    }
}
