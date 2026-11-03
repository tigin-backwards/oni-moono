using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using OniMoono.Assets.Files;
using OniMoono.Assets.IO;
using Xunit;

namespace OniMoono.Assets.Tests
{
    /// BundleCache: a bundle's data decompressed once into a cache file and mapped, with the bytes
    /// of the in-memory decode; damaged, stale and unwritable caches.
    public sealed class BundleCacheTests : IDisposable
    {
        readonly string folder = Directory.CreateTempSubdirectory("moono-bundle-cache-").FullName;
        readonly List<string> logged = new();

        public BundleCacheTests()
        {
            BundleCache.ForgetMappings();
            BundleCache.AutoCleanup = false;
            BundleCache.Configure(Path.Combine(folder, "cache"), verify: false, line => { lock (logged) logged.Add(line); });
        }

        public void Dispose()
        {
            BundleCache.Configure(null, false, null);
            BundleCache.AutoCleanup = true;
            BundleCache.ForgetMappings();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        string Cache => Path.Combine(folder, "cache");

        /// U59's hires_base_bundle (one LZMA block) and U60's (128 KiB LZ4HC chunks), where installed.
        public static IEnumerable<object[]> Bundles()
        {
            var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects");
            foreach (var game in new[] { "oni-linux", "oni-linux-761211" })
            {
                var path = Path.Combine(projects, game, "OxygenNotIncluded_Data", "StreamingAssets", "hires_base_bundle");
                if (File.Exists(path)) yield return [path];
            }
        }

        static bool IsMapped(UnityFsArchive archive) => !MemoryMarshal.TryGetArray(archive.Data, out _);

        string Copy(string bundle, string name = "hires_base_bundle")
        {
            var dir = Path.Combine(folder, "game");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.Copy(bundle, path, overwrite: true);
            return path;
        }

        [Theory]
        [MemberData(nameof(Bundles))]
        public void MissThenHitMapTheDecodedBytes(string bundle)
        {
            var path = Copy(bundle);
            var expected = UnityFsArchive.Open(path);
            var first = BundleCache.Open(path);
            Assert.True(IsMapped(first));
            Assert.True(first.Data.Span.SequenceEqual(expected.Data.Span));
            var files = Directory.GetFiles(Cache);
            Assert.Single(files);
            Assert.EndsWith($".v{BundleCache.FormatVersion}.bin", files[0]);
            Assert.Equal(BundleCache.HeaderSize + expected.Data.Length, new FileInfo(files[0]).Length);
            var written = File.GetLastWriteTimeUtc(files[0]);

            // A later launch: the file is mapped as it is, not written again.
            BundleCache.ForgetMappings();
            var second = BundleCache.Open(path);
            Assert.True(IsMapped(second));
            Assert.Equal(written, File.GetLastWriteTimeUtc(files[0]));
            Assert.True(second.Data.Span.SequenceEqual(expected.Data.Span));
            Assert.Equal(expected.Nodes, second.Nodes);
            Assert.Equal(expected.UnityRevision, second.UnityRevision);

            // Its serialized file reads every object as the in-memory one's does.
            var cab = expected.Nodes.First(n => !n.Path.EndsWith(".resS"));
            var copy = SerializedFile.Parse(cab.Path, expected.Read(cab));
            var mapped = SerializedFile.Parse(cab.Path, second.Slice(cab));
            Assert.Equal(copy.Objects, mapped.Objects);
            Assert.Equal(copy.Externals, mapped.Externals);
            foreach (var obj in copy.Objects)
            {
                var a = copy.OpenObject(obj);
                var b = mapped.OpenObject(obj);
                Assert.Equal(a.ReadBytes(a.Length), b.ReadBytes(b.Length));
            }
        }

        /// A cache file that is not the bundle's (truncated, of another key, length, version, or
        /// no cache file at all) is rebuilt, never read.
        [Theory]
        [InlineData("truncated")]
        [InlineData("longer")]
        [InlineData("magic")]
        [InlineData("version")]
        [InlineData("key")]
        [InlineData("length")]
        [InlineData("empty")]
        public void DamagedCacheFilesAreRebuilt(string damage)
        {
            var bundle = Bundles().LastOrDefault()?[0] as string;
            if (bundle == null) return;
            var path = Copy(bundle);
            var expected = UnityFsArchive.Open(path).Data.ToArray();
            BundleCache.Open(path);
            var cache = Directory.GetFiles(Cache).Single();
            BundleCache.ForgetMappings();
            using (var f = new FileStream(cache, FileMode.Open, FileAccess.ReadWrite))
            {
                void Flip(long at)
                {
                    f.Position = at;
                    int b = f.ReadByte();
                    f.Position = at;
                    f.WriteByte((byte)(b ^ 1));
                }
                switch (damage)
                {
                    case "truncated": f.SetLength(f.Length - 1); break;
                    case "longer": f.SetLength(f.Length + 1); break;
                    case "magic": Flip(0); break;
                    case "version": Flip(8); break;
                    case "key": Flip(40); break;
                    case "length": Flip(16); break;
                    case "empty": f.SetLength(0); break;
                }
            }
            var rebuilt = BundleCache.Open(path);
            Assert.True(IsMapped(rebuilt));
            Assert.True(rebuilt.Data.Span.SequenceEqual(expected));
            Assert.Equal(BundleCache.HeaderSize + expected.Length, new FileInfo(Directory.GetFiles(Cache).Single()).Length);
            Assert.Contains(logged, l => l.Contains("rebuilding it"));
        }

        /// A changed bundle (a game update) gets a new cache file, and the old one is deleted.
        [Fact]
        public void AChangedBundleReplacesItsOldCacheFile()
        {
            var bundles = Bundles().Select(b => (string)b[0]).ToArray();
            if (bundles.Length < 2) return;
            var path = Copy(bundles[0]);
            BundleCache.Open(path);
            var old = Directory.GetFiles(Cache).Single();
            Copy(bundles[1]);
            var archive = BundleCache.Open(path);
            var now = Directory.GetFiles(Cache).Single();
            Assert.NotEqual(old, now);
            Assert.True(archive.Data.Span.SequenceEqual(UnityFsArchive.Open(bundles[1]).Data.Span));
        }

        /// The cache folder cannot be made (here a file is in its way, as a read-only folder or a
        /// full disk would fail): the bundle is decoded into memory, and that is logged once.
        [Fact]
        public void AnUnwritableCacheFallsBackToMemory()
        {
            var bundle = Bundles().LastOrDefault()?[0] as string;
            if (bundle == null) return;
            File.WriteAllText(Cache, "in the way");
            var path = Copy(bundle);
            var archive = BundleCache.Open(path);
            Assert.False(IsMapped(archive));
            Assert.True(archive.Data.Span.SequenceEqual(UnityFsArchive.Open(path).Data.Span));
            BundleCache.Open(path);
            Assert.Single(logged, l => l.Contains("kept in memory"));
        }

        /// A bundle of stored blocks is mapped from its own file, with no cache file.
        [Fact]
        public void AStoredBundleIsMappedFromItsFile()
        {
            var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
            var path = Path.Combine(folder, "stored_bundle");
            File.WriteAllBytes(path, StoredBundle(data, [("CAB-a", 0, 3000), ("CAB-a.resS", 3000, 2000)]));
            var archive = BundleCache.Open(path);
            Assert.True(IsMapped(archive));
            Assert.True(archive.Data.Span.SequenceEqual(data));
            Assert.Equal(data[3000..], archive.Read(archive.Find("CAB-a.resS")));
            Assert.False(Directory.Exists(Cache) && Directory.GetFiles(Cache).Length > 0);
            Assert.True(UnityFsArchive.Open(path).Data.Span.SequenceEqual(data));
        }

        /// Verify mode decodes each bundle in memory too and says the bytes are the same.
        [Fact]
        public void VerifyComparesTheMappingWithTheDecode()
        {
            var bundle = Bundles().LastOrDefault()?[0] as string;
            if (bundle == null) return;
            BundleCache.Configure(Cache, verify: true, line => { lock (logged) logged.Add(line); });
            var path = Copy(bundle);
            BundleCache.Open(path);
            BundleCache.ForgetMappings();
            BundleCache.Open(path);
            Assert.Equal(2, logged.Count(l => l.StartsWith("bundle cache verify: hires_base_bundle:") && l.EndsWith("the same")));
        }

        /// Clean-up: leftover temporary files no launch holds, and cache files whose bundle is gone
        /// or whose format is older, go; a temporary file being written and a live cache stay.
        [Fact]
        public void CleanupDeletesWhatNoLaunchUses()
        {
            var bundle = Bundles().LastOrDefault()?[0] as string;
            if (bundle == null) return;
            var path = Copy(bundle);
            BundleCache.Open(path);
            var live = Directory.GetFiles(Cache).Single();
            var gone = Copy(bundle, "gone_bundle");
            BundleCache.Open(gone);
            var orphan = Directory.GetFiles(Cache).Single(f => f != live);
            File.Delete(gone);
            var older = Path.Combine(Cache, "x-00000000-00.v0.bin");
            var header = new byte[BundleCache.HeaderSize];
            Encoding.ASCII.GetBytes("MoonoBC\0").CopyTo(header, 0);
            File.WriteAllBytes(older, header);
            var left = Path.Combine(Cache, "x.bin.1-1.tmp");
            File.WriteAllText(left, "left");
            File.SetLastWriteTimeUtc(left, DateTime.UtcNow.AddHours(-1));
            var writing = Path.Combine(Cache, "y.bin.2-1.tmp");
            File.WriteAllText(writing, "being written");
            File.SetLastWriteTimeUtc(writing, DateTime.UtcNow.AddHours(-1));
            var fresh = Path.Combine(Cache, "z.bin.3-1.tmp");
            File.WriteAllText(fresh, "just made");
            using (new FileStream(writing, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                BundleCache.Cleanup(Cache);
            Assert.True(File.Exists(live));
            Assert.False(File.Exists(orphan));
            Assert.False(File.Exists(older));
            Assert.False(File.Exists(left));
            Assert.True(File.Exists(writing));
            Assert.True(File.Exists(fresh));
        }

        /// A mapped window reads only inside itself, as an array's does.
        [Fact]
        public void AMappedWindowStaysInside()
        {
            var path = Path.Combine(folder, "window.bin");
            File.WriteAllBytes(path, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
            var mapped = new MappedMemory(path, 16, 64);
            Assert.Equal(Enumerable.Range(16, 64).Select(i => (byte)i).ToArray(), mapped.Memory.ToArray());
            var window = mapped.Memory.Slice(8, 8);
            Assert.Equal(new byte[] { 24, 25, 26, 27, 28, 29, 30, 31 }, window.ToArray());
            Assert.Throws<ArgumentOutOfRangeException>(() => mapped.Memory.Slice(60, 8));
            Assert.Throws<InvalidDataException>(() => new MappedMemory(path, 200, 64));
        }

        /// A UnityFS archive of one stored block (no compression anywhere) with these nodes.
        static byte[] StoredBundle(byte[] data, (string Path, long Offset, long Size)[] nodes)
        {
            var info = new MemoryStream();
            void Be32(Stream s, int v) { var b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
            void Be64(Stream s, long v) { var b = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(b, v); s.Write(b); }
            info.Write(new byte[16]);
            Be32(info, 1);
            Be32(info, data.Length);
            Be32(info, data.Length);
            info.Write([0, 0]);
            Be32(info, nodes.Length);
            foreach (var n in nodes)
            {
                Be64(info, n.Offset);
                Be64(info, n.Size);
                Be32(info, 4);
                info.Write(Encoding.UTF8.GetBytes(n.Path + "\0"));
            }
            var file = new MemoryStream();
            file.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
            Be32(file, 8);
            file.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
            file.Write(Encoding.ASCII.GetBytes("6000.3.17f1\0"));
            Be64(file, 0);
            Be32(file, (int)info.Length);
            Be32(file, (int)info.Length);
            Be32(file, 0);
            while (file.Length % 16 != 0) file.WriteByte(0);
            file.Write(info.ToArray());
            file.Write(data);
            return file.ToArray();
        }
    }
}
