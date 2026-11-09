using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OniMoono.Host;
using Xunit;
using Cecil = Mono.Cecil;
using Cil = Mono.Cecil.Cil;

namespace OniMoono.Tests
{
    /// paged-path-grids' tables (src/OniMoono.Host/PathGridPages.cs, compiled in here) against a
    /// flat array, from one thread and several, and its rewrite of the game's PathGrid
    /// (PathGridPagesRewrite) on the game's own Assembly-CSharp: U60 761211 (ONI_GAME_DIR or
    /// ~/projects/oni-linux-762230) and U59 744825 when installed (ONI_U59_GAME_DIR or
    /// ~/projects/oni-linux).
    public class PathGridPagesTests(ITestOutputHelper output)
    {
        /// PathFinder.Cell's layout (12 bytes).
        struct Cell
        {
            public int cost, parent;
            public ushort queryId;
            public byte navTypes, transitionId;
        }

        /// PathGrid.ProberCell's (8 bytes).
        struct Prober
        {
            public int cost;
            public ushort queryId;
            public byte navType;
        }

        const int Paged = PathGridPages.PageShift, PagedMask = PathGridPages.PageMask, Flat = 31, FlatMask = int.MaxValue;

        static Cell Random(Random r) => new() { cost = r.Next(-1, 5000), parent = r.Next(-1, 100000), queryId = (ushort)r.Next(0, 65536), navTypes = (byte)r.Next(256), transitionId = (byte)r.Next(256) };

        [Theory]
        [InlineData(Paged, PagedMask, 256 * 37)]
        [InlineData(Paged, PagedMask, 256 * 37 + 101)]
        [InlineData(Paged, PagedMask, 7)]
        [InlineData(Flat, FlatMask, 256 * 37 + 101)]
        public void ReadsAsTheArray(int shift, int mask, int length)
        {
            var pages = PathGridPages.New<Cell>(length, shift);
            var flat = new Cell[length];
            var r = new Random(length);
            Assert.Equal(length, PathGridPages.Length(pages));
            for (int n = 0; n < 20000; n++)
            {
                int i = r.Next(length);
                switch (r.Next(4))
                {
                    case 0:
                        var v = Random(r);
                        flat[i] = v;
                        PathGridPages.StoreIn(pages, i, v, shift, mask);
                        break;
                    case 1:
                        // zeros, as ResetProberCells stores
                        flat[i] = default;
                        PathGridPages.StoreIn(pages, i, default(Cell), shift, mask);
                        break;
                    default:
                        Assert.Equal(flat[i], PathGridPages.LoadIn(pages, i, shift, mask));
                        break;
                }
            }
            for (int i = 0; i < length; i++) Assert.Equal(flat[i], PathGridPages.LoadIn(pages, i, shift, mask));
            // Out of range as the array is: negative, the length, past the last page.
            foreach (int bad in new[] { -1, -256, int.MinValue, length, length + 1, (length | 255) + 1, int.MaxValue })
            {
                Assert.Throws<IndexOutOfRangeException>(() => PathGridPages.LoadIn(pages, bad, shift, mask));
                Assert.Throws<IndexOutOfRangeException>(() => PathGridPages.StoreIn(pages, bad, new Cell { cost = 1 }, shift, mask));
            }
        }

        [Fact]
        public void ZerosNeverMakeAPage()
        {
            var pages = PathGridPages.New<Prober>(256 * 10);
            for (int i = 0; i < 256 * 9; i++) PathGridPages.Clear(pages, i);
            for (int k = 0; k < 9; k++) Assert.True(PathGridPages.IsZeroPage(pages, k));
            // The last page is the array's end: its own from the start.
            Assert.False(PathGridPages.IsZeroPage(pages, 9));
            PathGridPages.Store(pages, 300, new Prober { cost = 4, queryId = 9, navType = 1 });
            Assert.False(PathGridPages.IsZeroPage(pages, 1));
            Assert.True(PathGridPages.IsZeroPage(pages, 0));
            Assert.Equal(4, PathGridPages.Load(pages, 300).cost);
            Assert.Equal(default, PathGridPages.Load(pages, 299));
            // A page once made stays (a store of zeros into it is a store).
            PathGridPages.Clear(pages, 300);
            Assert.False(PathGridPages.IsZeroPage(pages, 1));
            Assert.Equal(default, PathGridPages.Load(pages, 300));
            // The Zero page itself is never written.
            Assert.All(Zero<Prober>.Page, p => Assert.Equal(default, p));
        }

        [Fact]
        public void HandleIsEachGridsOwn()
        {
            var a = PathGridPages.New<Cell>(1000);
            var b = PathGridPages.New<Cell>(1000);
            var ha = PathGridPages.Handle(a);
            Assert.Single(ha);
            Assert.NotSame(ha, PathGridPages.Handle(b));
            // Flat: the game's array itself.
            var flat = PathGridPages.New<Cell>(1000, Flat);
            Assert.Same(flat[0], PathGridPages.Handle(flat));
            Assert.Equal(1000, flat[0].Length);
        }

        /// Writers on several threads making the same pages: no write is lost, each page is made
        /// once, and readers racing them read each field as zero or what was written, never out
        /// of range. A reader can see some of an element's fields written and others not yet: a
        /// 12-byte struct is not stored in one go, in the game's flat array either, so the fields
        /// are checked one by one (each is aligned, so each is read whole).
        [Fact]
        public void PagesMadeOnceUnderRaces()
        {
            const int length = 256 * 64 + 17, threads = 8;
            for (int round = 0; round < 20; round++)
            {
                var pages = PathGridPages.New<Cell>(length);
                long before = Interlocked.Read(ref PathGridPages.PagesMade);
                using var start = new Barrier(threads + 2);
                bool done = false;
                var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var readers = Enumerable.Range(0, 2).Select(t => new Thread(() =>
                {
                    start.SignalAndWait();
                    var r = new Random(t);
                    while (!Volatile.Read(ref done))
                    {
                        int i = r.Next(length);
                        try
                        {
                            var v = PathGridPages.Load(pages, i);
                            // Each element is only ever written with its own index (or left zero).
                            if (v.cost != 0 && v.cost != i + 1 || v.parent != 0 && v.parent != -(i + 1) || v.queryId > 1 || v.navTypes != 0 || v.transitionId != 0)
                                failures.Enqueue($"element {i} read {v.cost}/{v.parent}/{v.queryId}");
                        }
                        catch (Exception e)
                        {
                            failures.Enqueue($"element {i}: {e.GetType().Name}");
                        }
                    }
                })).ToArray();
                // Thread t writes every element i with i % threads == t, page by page in the same
                // order as the others: they race on each page's creation.
                var writers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
                {
                    start.SignalAndWait();
                    for (int i = t; i < length; i += threads) PathGridPages.Store(pages, i, new Cell { cost = i + 1, parent = -(i + 1), queryId = 1 });
                })).ToArray();
                foreach (var th in readers.Concat(writers)) th.Start();
                foreach (var th in writers) th.Join();
                Volatile.Write(ref done, true);
                foreach (var th in readers) th.Join();
                Assert.Empty(failures);
                for (int i = 0; i < length; i++)
                {
                    var v = PathGridPages.Load(pages, i);
                    Assert.True(v.cost == i + 1 && v.parent == -(i + 1), $"element {i}: {v.cost}");
                }
                // 64 full pages made here (the last, partial one is the table's from the start);
                // other tests' tables may count too while they run, so at least.
                Assert.True(Interlocked.Read(ref PathGridPages.PagesMade) - before >= 64);
                for (int k = 0; k < 64; k++) Assert.False(PathGridPages.IsZeroPage(pages, k));
            }
        }

        /// Verify mode's comparison while writers run, as in play: probe threads write their
        /// grids (each grid by one thread at a time, as the game's are) while the main thread
        /// compares every table with its flat array. Half the writers store through the host's
        /// helpers, half through path-probe-repair's view of a table. Each makes its table's
        /// pages in a random order (a page the comparison read as Zero may be made and written
        /// before it compares it), then rewrites every element (the comparison lands between a
        /// write's two stores, and a writer stopped there by the GC or the scheduler stays there
        /// for longer than any fixed wait). No difference may be reported: every write stores both.
        [Fact]
        public void VerifyQuietUnderWriters()
        {
            const int length = 256 * 2048, threads = 4;
            bool done = false;
            var tables = new GameTweaks.PCell[threads][][];
            var writers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                var r = new Random(t);
                int n = length / 256;
                var order = Enumerable.Range(0, n).ToArray();
                while (!Volatile.Read(ref done))
                {
                    var pages = PathGridPages.New<GameTweaks.PCell>(length, Paged, shadow: true);
                    Volatile.Write(ref tables[t], pages);
                    Action<int, GameTweaks.PCell> store = (t & 1) == 0
                        ? (i, v) => PathGridPages.StoreShadowed(pages, i, v, Paged, PagedMask)
                        : GameTweaks.ProbePages<GameTweaks.PCell>.Over(pages, length, Paged, Zero<GameTweaks.PCell>.Page, k => PathGridPages.Materialise(pages, k),
                            PathGridPages.ShadowOf(pages), PathGridPages.WritesOf(pages)) is var view ? (i, v) => view[i] = v : null;
                    GameTweaks.PCell Any() => new() { Cost = r.Next(1, 5000), Parent = r.Next(-1, 100000), QueryId = (ushort)r.Next(1, 65536), NavTypes = (byte)r.Next(256) };
                    for (int k = n - 1; k > 0; k--) { int j = r.Next(k + 1); (order[k], order[j]) = (order[j], order[k]); }
                    foreach (int k in order) store((k << 8) + r.Next(256), Any());
                    for (int pass = 0; pass < 2 && !Volatile.Read(ref done); pass++)
                        for (int i = 0; i < length; i++) store(i, (i & 7) == 0 ? default : Any());
                }
            })).ToArray();
            foreach (var th in writers) th.Start();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int runs = 0, midWrite = 0, unsettled = 0;
            long wrong = 0;
            while (clock.ElapsedMilliseconds < 4000)
            {
                wrong += PathGridPages.VerifyAll(out _, out _, out string first, out int mid, out int open);
                midWrite += mid;
                unsettled += open;
                if (first != null) output.WriteLine(first);
                runs++;
            }
            Volatile.Write(ref done, true);
            foreach (var th in writers) th.Join();
            output.WriteLine($"{runs} comparisons, {wrong} differences, {midWrite} mid-write, {unsettled} unsettled");
            Assert.Equal(0, wrong);
            Assert.Equal(0, unsettled);
            // With the writers done, every table as its flat array, compared once.
            Assert.Equal(0, PathGridPages.VerifyAll(out int tableCount, out _, out _, out int after, out _));
            Assert.Equal(0, after);
            Assert.True(tableCount >= threads);
            GC.KeepAlive(tables);
        }

        /// What verify mode is for: a write that reaches the pages and not the flat array (a
        /// writer that skips the helpers) is found, in a page made by it and in one made before.
        [Fact]
        public void VerifyFindsAWriteOutsideTheHelpers()
        {
            var pages = PathGridPages.New<Cell>(256 * 8, Paged, shadow: true);
            PathGridPages.StoreShadowed(pages, 2 * 256 + 1, new Cell { cost = 3 }, Paged, PagedMask);
            try
            {
                PathGridPages.Materialise(pages, 5)[7] = new Cell { cost = 9, queryId = 2 };
                pages[2][1].parent = 4;
                Assert.Equal(2, PathGridPages.VerifyAll(out _, out _, out string first, out int midWrite, out int unsettled));
                Assert.Equal("Cell element 513 of 2048", first);
                Assert.Equal(0, midWrite);
                Assert.Equal(0, unsettled);
            }
            finally
            {
                // Agreeing again, for the other tests' comparisons.
                PathGridPages.StoreShadowed(pages, 5 * 256 + 7, default(Cell), Paged, PagedMask);
                PathGridPages.StoreShadowed(pages, 2 * 256 + 1, default(Cell), Paged, PagedMask);
            }
            Assert.Equal(0, PathGridPages.VerifyAll(out _, out _, out _));
        }

        // ---- The rewrite of the game's PathGrid ----

        static string GameAssembly(string variable, string folder)
        {
            var dir = Environment.GetEnvironmentVariable(variable)
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", folder);
            var path = Path.Combine(dir, "OxygenNotIncluded_Data", "Managed", "Assembly-CSharp.dll");
            return File.Exists(path) ? path : null;
        }

        public static TheoryData<string, string> Builds() => new() { { "ONI_GAME_DIR", "oni-linux-762230" }, { "ONI_U59_GAME_DIR", "oni-linux" } };

        /// PathGrid's twelve accesses go through the tables and nothing else changes: every other
        /// instruction of its methods, and every other method of the assembly, as it was.
        [Theory]
        [MemberData(nameof(Builds))]
        public void RewritesPathGridOnly(string variable, string folder)
        {
            var path = GameAssembly(variable, folder);
            if (path == null)
            {
                Assert.Skip($"no game at {variable} or ~/projects/{folder}");
                return;
            }
            if (GameInstall.IsReferenceAssembly(path)) Assert.Skip(GameInstall.NeedsAssembly + "Assembly-CSharp (a reference assembly)");
            var resolver = new Cecil.DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(path));
            using var original = Cecil.ModuleDefinition.ReadModule(path, new Cecil.ReaderParameters { AssemblyResolver = resolver });
            using var module = Cecil.ModuleDefinition.ReadModule(path, new Cecil.ReaderParameters { AssemblyResolver = resolver });
            var log = new List<string>();
            int sites = PathGridPagesRewrite.RewriteModule(module, true, "Assembly-CSharp.dll", log);
            foreach (var line in log) output.WriteLine(line);
            Assert.Equal(12, sites);
            // Written and read back: a valid image.
            var image = new MemoryStream();
            module.Write(image);
            image.Position = 0;
            using var back = Cecil.ModuleDefinition.ReadModule(image, new Cecil.ReaderParameters { AssemblyResolver = resolver });
            var grid = back.GetType("PathGrid");
            foreach (var array in PathGridPagesRewrite.Arrays)
            {
                Assert.Contains(grid.Fields, f => f.Name == PathGridPagesRewrite.PagesField(array) && f.FieldType.FullName.EndsWith("[][]", StringComparison.Ordinal));
                Assert.Contains(grid.Fields, f => f.Name == PathGridPagesRewrite.LengthField(array) && f.FieldType.FullName == "System.Int32");
            }
            var changed = new List<string>();
            foreach (var type in original.GetTypes())
            {
                var after = back.GetType(type.FullName);
                foreach (var m in type.Methods.Where(m => m.HasBody))
                {
                    var n = after.Methods.Single(x => x.FullName == m.FullName);
                    if (Same(m.Body, n.Body)) continue;
                    changed.Add($"{type.Name}.{m.Name}");
                    Assert.Equal("PathGrid", type.FullName);
                    CheckOnlyAccesses(m, n);
                }
            }
            output.WriteLine($"changed: {string.Join(", ", changed.Order(StringComparer.Ordinal))}");
            Assert.Equal(new[] { ".ctor", "GetCell", "GetCost", "GetCostIgnoreProberOffset", "GetProberCell", "ResetProberCells", "SetCell" },
                changed.Select(c => c[(c.IndexOf('.') + 1)..]).Distinct().Order(StringComparer.Ordinal).ToArray());
        }

        static string Text(Cil.Instruction i) => i.Operand switch
        {
            Cil.Instruction t => $"{i.OpCode} @{t.Offset}",
            Cil.Instruction[] ts => $"{i.OpCode} {ts.Length}",
            var o => $"{i.OpCode} {o}",
        };

        static bool Same(Cil.MethodBody a, Cil.MethodBody b) =>
            a.Instructions.Count == b.Instructions.Count && a.Instructions.Zip(b.Instructions).All(p => Text(p.First) == Text(p.Second))
            && a.Variables.Count == b.Variables.Count;

        /// The rewritten method against the game's: the same instructions but the accesses (as
        /// PathGridPagesRewrite's table says) and the constructor's ten added after each `newarr`.
        static void CheckOnlyAccesses(Cecil.MethodDefinition before, Cecil.MethodDefinition after)
        {
            var a = before.Body.Instructions;
            var b = after.Body.Instructions;
            int j = 0;
            for (int k = 0; k < a.Count; k++, j++)
            {
                var o = a[k];
                var r = b[j];
                string where = $"{before.Name} IL_{o.Offset:x4}: {o} became {r}";
                bool field = o.Operand is Cecil.FieldReference { Name: "Cells" or "ProberCells" };
                switch (o.OpCode.Code)
                {
                    case Cil.Code.Ldfld when field:
                        Assert.True(r.OpCode.Code == Cil.Code.Ldfld && r.Operand is Cecil.FieldReference f && (f.Name.EndsWith(">pages", StringComparison.Ordinal) || f.Name.EndsWith(">length", StringComparison.Ordinal)), where);
                        continue;
                    case Cil.Code.Ldelem_Any or Cil.Code.Stelem_Any or Cil.Code.Ldelema when r.OpCode.Code != o.OpCode.Code:
                        Assert.True(r.OpCode.Code is Cil.Code.Call or Cil.Code.Nop, where);
                        if (r.OpCode.Code == Cil.Code.Call) Assert.Equal("OniMoono.Host.PathGridPages", ((Cecil.MethodReference)r.Operand).DeclaringType.FullName);
                        continue;
                    case Cil.Code.Initobj or Cil.Code.Ldobj when r.OpCode.Code != o.OpCode.Code:
                        Assert.True(r.OpCode.Code == Cil.Code.Nop, where);
                        continue;
                    case Cil.Code.Ldlen when o.Previous?.Operand is Cecil.FieldReference { Name: "Cells" or "ProberCells" }:
                        Assert.True(r.OpCode.Code == Cil.Code.Nop, where);
                        continue;
                    case Cil.Code.Newarr when before.IsConstructor && o.Next?.Operand is Cecil.FieldReference { Name: "Cells" or "ProberCells" }:
                        Assert.True(r.OpCode.Code == Cil.Code.Call && ((Cecil.MethodReference)r.Operand).Name == "New", where);
                        // stloc, dup, ldloc, stfld pages, dup, ldloc, call Length, stfld length, ldloc, call Handle
                        j += 10;
                        Assert.Equal("Handle", ((Cecil.MethodReference)b[j].Operand).Name);
                        continue;
                }
                // Branches widened to their long forms are the same branch.
                Assert.True(o.OpCode.Code == r.OpCode.Code || o.OpCode.Name + "" == r.OpCode.Name.Replace(".s", "") || r.OpCode.Name == o.OpCode.Name.Replace(".s", ""), where);
                if (o.Operand is not Cil.Instruction && o.Operand is not Cil.Instruction[]) Assert.Equal($"{o.Operand}", $"{r.Operand}");
            }
            Assert.Equal(b.Count, j);
        }

        /// Fast Track's locks on the arrays are kept, and nothing else of it is a problem
        /// (ONI_FASTTRACK_DLL, else the repository's copy when there is one).
        [Fact]
        public void FastTrackOnlyLocks()
        {
            var path = Environment.GetEnvironmentVariable("ONI_FASTTRACK_DLL") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-brutal", "out", "ft-mods", "Dev", "FastTrack", "FastTrack.dll");
            if (!File.Exists(path))
            {
                Assert.Skip($"no Fast Track at {path}");
                return;
            }
            var (locks, problems) = Scan(path);
            output.WriteLine($"{path}: {locks}");
            Assert.Empty(problems);
            Assert.Contains("3 lock(s)", locks);
        }

        static (string Log, List<string> Problems) Scan(string path)
        {
            using var module = Cecil.ModuleDefinition.ReadModule(path);
            var log = new List<string>();
            List<string> problems;
            lock (PathGridPagesRewrite.Problems)
            {
                int before = PathGridPagesRewrite.Problems.Count;
                PathGridPagesRewrite.RewriteModule(module, false, Path.GetFileName(path), log);
                problems = PathGridPagesRewrite.Problems.Skip(before).ToList();
                PathGridPagesRewrite.Problems.RemoveRange(before, problems.Count);
            }
            return (string.Join("; ", log), problems);
        }

        /// Every Workshop DLL of ONI_WS_DIR (the repository's out/ws: 3,547 of them) through the
        /// mods' check: which lock and which would make the session flat (skipped unless set).
        [Fact]
        public void WorkshopMods()
        {
            var dir = Environment.GetEnvironmentVariable("ONI_WS_DIR");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Assert.Skip("ONI_WS_DIR unset");
                return;
            }
            int dlls = 0, unreadable = 0;
            var flat = new List<string>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(file);
                if (!PathGridPagesRewrite.MayTouch(bytes)) { dlls++; continue; }
                try
                {
                    var (log, problems) = Scan(file);
                    dlls++;
                    if (log.Length > 0) output.WriteLine($"{Path.GetRelativePath(dir, file)}: {log}");
                    flat.AddRange(problems);
                }
                catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or Cecil.AssemblyResolutionException or ArgumentException or IOException)
                {
                    unreadable++;
                }
            }
            output.WriteLine($"{dlls} DLLs, {unreadable} unreadable, {flat.Count} would make the session flat");
            foreach (var p in flat) output.WriteLine("  " + p);
            Assert.Empty(flat);
        }
    }
}
