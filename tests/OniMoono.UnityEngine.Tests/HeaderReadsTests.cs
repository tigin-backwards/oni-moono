using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The fast-load-screen game tweak's reads (src/OniMoono.GameTweaks/HeaderReads.cs, compiled
    /// in here), in a model of the Load screen's refresh: two passes over a listing, each reading
    /// every header twice (IsFileValid, then GetFileInfo) through LoadHeader, with the game's
    /// parse, its fix-ups and its warnings. The tweak's LoadHeader is FastLoadScreen's, over the
    /// model's types; with it a refresh must give the same colonies, in the same order, with the
    /// same log lines.
    public class HeaderReadsTests : IDisposable
    {
        public struct Header { public uint buildVersion; public int headerSize; public uint headerVersion; public int compression; }

        /// SaveGame.GameInfo's shape: values, strings, a string array and a string list.
        public struct Info
        {
            public int numberOfCycles;
            public string baseName;
            public int saveMajorVersion;
            public int saveMinorVersion;
            public string[] worldTraits;
            public Guid colonyGuid;
            public string dlcId;
            public List<string> dlcIds;
        }

        readonly string dir = Path.Combine(Path.GetTempPath(), "header-reads-" + Guid.NewGuid().ToString("N"));

        readonly ITestOutputHelper output;

        public HeaderReadsTests(ITestOutputHelper output)
        {
            this.output = output;
            Directory.CreateDirectory(dir);
        }

        public void Dispose() => Directory.Delete(dir, recursive: true);

        /// The Load screen and the game's header code, as far as the refresh goes.
        sealed class Game
        {
            public readonly List<string> Log = new List<string>();
            public HeaderReads<Header, Info> Reads;
            public int Opens;
            public Func<string[]> List;
            public Action BetweenPasses;
            bool blankSent;

            /// SaveGame.GetHeader.
            Info GetHeader(byte[] raw, out Header header, string file)
            {
                header = new Header
                {
                    buildVersion = BitConverter.ToUInt32(raw, 0), headerSize = BitConverter.ToInt32(raw, 4),
                    headerVersion = BitConverter.ToUInt32(raw, 8), compression = BitConverter.ToInt32(raw, 12),
                };
                var data = new byte[header.headerSize];
                Array.Copy(raw, 16, data, 0, Math.Min(data.Length, raw.Length - 16));
                if (header.headerSize == 0 && !blankSent)
                {
                    blankSent = true;
                    Log.Add("SaveFileHeaderBlank - " + file);
                }
                var info = JsonConvert.DeserializeObject<Info>(Encoding.UTF8.GetString(data));
                if (info.saveMajorVersion < 7 || info.saveMajorVersion == 7 && info.saveMinorVersion < 14)
                    if (info.worldTraits != null)
                        for (int i = 0; i < info.worldTraits.Length; i++) info.worldTraits[i] = info.worldTraits[i].Replace('\\', '/');
                if (info.saveMajorVersion < 7 || info.saveMajorVersion == 7 && info.saveMinorVersion < 34) info.dlcIds = new List<string> { info.dlcId };
                return info;
            }

            /// SaveLoader.LoadHeader as the game has it.
            Info GameLoadHeader(string file, out Header header)
            {
                Opens++;
                using var stream = File.OpenRead(file);
                var prefix = new byte[16];
                _ = stream.Read(prefix, 0, 16); // one read, as the game's (a short one leaves zeros)
                int size = BitConverter.ToInt32(prefix, 4);
                var raw = new byte[16 + size];
                Array.Copy(prefix, raw, 16);
                stream.Position = 16;
                _ = stream.Read(raw, 16, size); // one read, as the game's (a short one leaves zeros)
                return GetHeader(raw, out header, file);
            }

            /// LoadHeader with FastLoadScreen's prefix and postfix when the tweak is on.
            Info LoadHeader(string file, out Header header)
            {
                if (Reads == null) return GameLoadHeader(file, out header);
                if (Reads.TryGet(file, out header, out var info)) return info;
                var raw = Reads.TakeRaw(file);
                if (raw != null)
                {
                    try
                    {
                        info = GetHeader(raw, out header, file);
                        Reads.Store(file, header, info);
                        return info;
                    }
                    catch (Exception) { }
                }
                info = GameLoadHeader(file, out header);
                Reads.Store(file, header, info);
                return info;
            }

            bool IsFileValid(string file)
            {
                try { return LoadHeader(file, out _).saveMajorVersion >= 7; }
                catch (Exception e)
                {
                    Log.Add("Corrupted save file: " + file + " " + e.GetType().Name);
                    return false;
                }
            }

            Info? GetFileInfo(string file)
            {
                try
                {
                    var info = LoadHeader(file, out _);
                    if (info.saveMajorVersion >= 7) return info;
                }
                catch (Exception e)
                {
                    Log.Add("Exception while loading " + file + " " + e.GetType().Name);
                }
                return null;
            }

            /// GetColoniesDetails: each valid file's details, in the listing's order. A caller
            /// changes what it was given (as the screen's sorting and lists may).
            List<string> Details(string[] files)
            {
                Reads?.Begin(files, files.Select(File.GetLastWriteTimeUtc).ToArray());
                var details = new List<string>();
                foreach (var file in files)
                    if (IsFileValid(file) && GetFileInfo(file) is Info info)
                    {
                        details.Add(Path.GetFileName(file) + " " + JsonConvert.SerializeObject(info));
                        if (info.worldTraits?.Length > 0) info.worldTraits[0] = "changed by a caller";
                        info.dlcIds?.Add("added by a caller");
                    }
                Reads?.End();
                return details;
            }

            /// RefreshColonyList: CheckCloudLocalOverlap's pass, then the list's.
            public List<string> Refresh(bool tweak)
            {
                Reads = tweak ? new HeaderReads<Header, Info>(Copy) : null;
                var first = Details(List());
                BetweenPasses?.Invoke();
                var second = Details(List());
                Reads?.End();
                Reads = null;
                return first.Concat(new[] { "--" }).Concat(second).ToList();
            }

            static Info Copy(Info info)
            {
                if (info.worldTraits != null) info.worldTraits = (string[])info.worldTraits.Clone();
                if (info.dlcIds != null) info.dlcIds = new List<string>(info.dlcIds);
                return info;
            }
        }

        string Save(string name, object info, int? size = null, int? truncate = null)
        {
            var body = info == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(info));
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(744825u));
            bytes.AddRange(BitConverter.GetBytes(size ?? body.Length));
            bytes.AddRange(BitConverter.GetBytes(1u));
            bytes.AddRange(BitConverter.GetBytes(1));
            bytes.AddRange(body);
            bytes.AddRange(new byte[64]);
            var all = bytes.ToArray();
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, truncate is int t ? all.Take(t).ToArray() : all);
            return path;
        }

        static object Sav(string name, int minor = 39, string[] traits = null, string[] dlcs = null)
            => new
            {
                baseName = name, saveMajorVersion = 7, saveMinorVersion = minor, numberOfCycles = 12, worldTraits = traits ?? new[] { "traits/A" },
                colonyGuid = Guid.NewGuid(), dlcId = "", dlcIds = dlcs ?? new[] { "EXPANSION1_ID" }
            };

        void Tree()
        {
            Save("a.sav", Sav("Alpha"));
            Save("b.sav", Sav("Beta", minor: 13, traits: new[] { "traits\\Old", "traits\\Older" }));
            Save("c.sav", Sav("Gamma", minor: 20));
            Save("empty.sav", null, size: 0);
            Save("zero.sav", null, size: 0, truncate: 0);
            Save("short.sav", Sav("Short"), truncate: 40);
            Save("negative.sav", Sav("Negative"), size: -100);
            Save("huge.sav", Sav("Huge"), size: int.MaxValue - 8);
            Save("old.sav", new { baseName = "Old", saveMajorVersion = 6, saveMinorVersion = 0 });
            Save("bad-json.sav", "not json at all");
            for (int i = 0; i < 40; i++) Save($"many{i:D2}.sav", Sav("Many" + i));
        }

        Game NewGame() => new Game { List = () => Directory.GetFiles(dir, "*.sav").OrderBy(f => f, StringComparer.Ordinal).ToArray() };

        [NeedsGameAssembly("Newtonsoft.Json")]
        [Fact]
        public void ARefreshIsTheGamesWithOneReadAFile()
        {
            Tree();
            var game = NewGame();
            var expected = game.Refresh(tweak: false);
            var expectedLog = game.Log.ToList();
            int gameOpens = game.Opens;
            var tweaked = NewGame();
            var actual = tweaked.Refresh(tweak: true);
            Assert.Equal(expected, actual);
            Assert.Equal(expectedLog, tweaked.Log);
            // The blank-header warning once, each corrupt file's warning in each pass.
            Assert.Single(expectedLog, l => l.StartsWith("SaveFileHeaderBlank"));
            Assert.Equal(2, expectedLog.Count(l => l.StartsWith("Corrupted save file: " + Path.Combine(dir, "bad-json.sav"))));
            // The game opens each valid file four times, the others once a pass (43 valid, 6
            // corrupt, 1 too old). With the tweak the game reads a file only when the read-ahead
            // had not reached it or its bytes did not parse: once a file, and the corrupt ones
            // once more in the second pass.
            Assert.Equal(43 * 4 + 7 * 2, gameOpens);
            Assert.True(tweaked.Opens <= 50 + 6, $"{tweaked.Opens} game reads");
        }

        [NeedsGameAssembly("Newtonsoft.Json")]
        [Fact]
        public void EachCallerGetsItsOwnCopy()
        {
            var path = Save("a.sav", Sav("Alpha", traits: new[] { "traits/A", "traits/B" }));
            var reads = new HeaderReads<Header, Info>(i => { i.worldTraits = (string[])i.worldTraits.Clone(); i.dlcIds = new List<string>(i.dlcIds); return i; });
            reads.Begin(new[] { path }, new[] { File.GetLastWriteTimeUtc(path) }, threads: 0);
            var info = new Info { worldTraits = new[] { "x", "y" }, dlcIds = new List<string> { "d" } };
            reads.Store(path, default, info);
            info.worldTraits[0] = "changed";
            info.dlcIds.Add("added");
            Assert.True(reads.TryGet(path, out _, out var first));
            Assert.Equal(new[] { "x", "y" }, first.worldTraits);
            Assert.Equal(new[] { "d" }, first.dlcIds);
            first.worldTraits[1] = "changed too";
            first.dlcIds.Clear();
            Assert.True(reads.TryGet(path, out _, out var second));
            Assert.Equal(new[] { "x", "y" }, second.worldTraits);
            Assert.Equal(new[] { "d" }, second.dlcIds);
            Assert.NotSame(first.worldTraits, second.worldTraits);
            Assert.NotSame(first.dlcIds, second.dlcIds);
        }

        [NeedsGameAssembly("Newtonsoft.Json")]
        [Fact]
        public void TheMemoServesOnlyTheListingAndItsTimes()
        {
            var a = Save("a.sav", Sav("Alpha"));
            var b = Save("b.sav", Sav("Beta"));
            var reads = new HeaderReads<Header, Info>(i => i);
            var time = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            // Before any listing, and for a file outside it, nothing is kept or served.
            reads.Store(a, default, new Info { baseName = "early" });
            Assert.False(reads.TryGet(a, out _, out _));
            reads.Begin(new[] { a }, new[] { time }, threads: 0);
            reads.Store(b, default, new Info { baseName = "outside" });
            Assert.False(reads.TryGet(b, out _, out _));
            reads.Store(a, default, new Info { baseName = "Alpha" });
            Assert.True(reads.TryGet(a, out _, out var info));
            Assert.Equal("Alpha", info.baseName);
            reads.End();
            // Between listings nothing is served; a later listing with the same time is.
            Assert.False(reads.TryGet(a, out _, out _));
            reads.Begin(new[] { a }, new[] { time }, threads: 0);
            Assert.True(reads.TryGet(a, out _, out _));
            // A new time (the file was written since) is read again.
            reads.Begin(new[] { a }, new[] { time.AddTicks(1) }, threads: 0);
            Assert.False(reads.TryGet(a, out _, out _));
        }

        [NeedsGameAssembly("Newtonsoft.Json")]
        [Fact]
        public void AFileWrittenBetweenPassesOrRefreshesIsReadAgain()
        {
            Tree();
            var path = Path.Combine(dir, "a.sav");
            List<string> Run(bool tweak)
            {
                Save("a.sav", Sav("Alpha"));
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                var game = NewGame();
                game.BetweenPasses = () =>
                {
                    Save("a.sav", Sav("Alpha rewritten"));
                    File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
                };
                var first = game.Refresh(tweak);
                game.BetweenPasses = null;
                Save("a.sav", Sav("Alpha again"));
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
                return first.Concat(game.Refresh(tweak)).ToList();
            }
            // (Each Save makes a new colony GUID; compare the names.)
            static IEnumerable<string> Names(List<string> d) => d.Select(l => l.StartsWith("a.sav") ? JsonConvert.DeserializeObject<Info>(l.Substring(6)).baseName : l.Split(' ')[0]);
            var expected = Names(Run(false)).ToList();
            Assert.Equal(expected, Names(Run(true)));
            Assert.Equal(new[] { "Alpha", "Alpha rewritten", "Alpha again", "Alpha again" }, expected.Where(n => n.StartsWith("Alpha")));
        }

        [NeedsGameAssembly("Newtonsoft.Json")]
        [Fact]
        public void ReadAheadGivesTheGamesBytesInAnyOrder()
        {
            var paths = Enumerable.Range(0, 300).Select(i => Save($"s{i:D3}.sav", Sav("S" + i))).ToList();
            paths.Add(Save("short.sav", Sav("Short"), truncate: 30));
            paths.Add(Save("negative.sav", Sav("Negative"), size: -5));
            paths.Add(Path.Combine(dir, "missing.sav"));
            var stamps = paths.Select(p => DateTime.UnixEpoch).ToArray();
            foreach (var order in new[] { paths, Enumerable.Reverse(paths).ToList(), paths.Where((_, i) => i % 7 == 0).ToList() })
            {
                var reads = new HeaderReads<Header, Info>(i => i);
                reads.Begin(paths, stamps);
                int taken = 0;
                foreach (var path in order)
                {
                    var raw = reads.TakeRaw(path);
                    if (raw == null) continue;
                    taken++;
                    // The same bytes as the game's read; a second take is the caller's to read.
                    Assert.Equal(HeaderReads<Header, Info>.ReadRaw(path), raw);
                    Assert.Null(reads.TakeRaw(path));
                }
                reads.End();
            }
            // Given time, every readable file is read ahead.
            var ahead = new HeaderReads<Header, Info>(i => i);
            ahead.Begin(paths, stamps);
            System.Threading.Thread.Sleep(500);
            Assert.Equal(301, paths.Count(p => ahead.TakeRaw(p) != null));
            ahead.End();
            // A truncated file reads as the game reads it: its bytes, then zeros.
            var shortRaw = HeaderReads<Header, Info>.ReadRaw(paths[300]);
            Assert.True(shortRaw.Skip(30).All(b => b == 0));
            Assert.ThrowsAny<Exception>(() => HeaderReads<Header, Info>.ReadRaw(paths[301]));
            Assert.ThrowsAny<Exception>(() => HeaderReads<Header, Info>.ReadRaw(paths[302]));
        }

        [NeedsGameAssembly("Newtonsoft.Json")]
        [Fact]
        public void TheModelReadsAsTheGameWithManyFiles()
        {
            for (int i = 0; i < 500; i++) Save($"m{i:D3}.sav", Sav("M" + i % 37, minor: i % 3 == 0 ? 13 : 39, traits: new[] { "traits\\T" + i }));
            Save("bad.sav", "{");
            var game = NewGame();
            var expected = game.Refresh(tweak: false);
            var tweaked = NewGame();
            for (int run = 0; run < 3; run++)
            {
                tweaked.Log.Clear();
                Assert.Equal(expected, tweaked.Refresh(tweak: true));
                Assert.Equal(game.Log.Where(l => !l.StartsWith("SaveFileHeaderBlank")), tweaked.Log);
            }
        }

        /// ONI_HEADER_BENCH=DIR: the raw headers of every save under DIR (only read), one after
        /// another as the game reads them, then through the read-ahead, for docs/research.
        [Fact]
        public void Bench()
        {
            var root = Environment.GetEnvironmentVariable("ONI_HEADER_BENCH");
            if (string.IsNullOrEmpty(root)) return;
            var files = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            var stamps = files.Select(_ => DateTime.UnixEpoch).ToArray();
            for (int round = 0; round < 2; round++)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                long bytes = 0;
                foreach (var f in files)
                    try { bytes += HeaderReads<Header, Info>.ReadRaw(f).Length; } catch (Exception) { }
                var one = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                start = System.Diagnostics.Stopwatch.GetTimestamp();
                var reads = new HeaderReads<Header, Info>(i => i);
                reads.Begin(files, stamps);
                long ahead = 0;
                foreach (var f in files)
                {
                    var raw = reads.TakeRaw(f);
                    if (raw == null) try { raw = HeaderReads<Header, Info>.ReadRaw(f); } catch (Exception) { }
                    ahead += raw?.Length ?? 0;
                }
                reads.End();
                var parallel = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Assert.Equal(bytes, ahead);
                output.WriteLine($"{files.Length} headers: {one:F0} ms one after another, {parallel:F0} ms with the read-ahead ({reads.ReadAheadTaken} read ahead)");
            }
        }
    }
}
