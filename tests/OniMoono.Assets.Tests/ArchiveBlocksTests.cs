using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OniMoono.Assets.Files;
using OniMoono.Assets.TypeTrees;
using Xunit;

namespace OniMoono.Assets.Tests
{
    /// Archives read block by block (ArchiveBlocks): every read gives the bytes of the whole
    /// decode, at any offset and length, across block boundaries, from several threads at once.
    public class ArchiveBlocksTests
    {
        /// A UnityFS archive (format 6, tables stored) of `blocks`, each (data, compression):
        /// 0 stored, 3 LZ4HC (written as literals only, which any LZ4 decoder reads).
        internal static byte[] Archive(IReadOnlyList<(byte[] Data, int Compression)> blocks, IReadOnlyList<(string Path, long Offset, long Size)> nodes)
        {
            var info = new MemoryStream();
            var w = new BigEndianWriter(info);
            w.Bytes(new byte[16]);
            w.Int(blocks.Count);
            var payload = new MemoryStream();
            foreach (var (data, compression) in blocks)
            {
                var stored = compression == 0 ? data : Literals(data);
                w.Int(data.Length);
                w.Int(stored.Length);
                w.Short((short)compression);
                payload.Write(stored);
            }
            w.Int(nodes.Count);
            foreach (var (path, offset, size) in nodes)
            {
                w.Long(offset);
                w.Long(size);
                w.Int(4);
                w.CString(path);
            }
            var file = new MemoryStream();
            var h = new BigEndianWriter(file);
            h.CString("UnityFS");
            h.Int(6);
            h.CString("5.x.x");
            h.CString("6000.3.17f1");
            h.Long(0);
            h.Int((int)info.Length);
            h.Int((int)info.Length);
            h.Int(0x40); // blocks and directory together, tables stored
            file.Write(info.ToArray());
            file.Write(payload.ToArray());
            return file.ToArray();
        }

        /// An LZ4 block of literals only: one last sequence.
        static byte[] Literals(byte[] data)
        {
            var o = new MemoryStream();
            int n = data.Length;
            o.WriteByte((byte)(Math.Min(n, 15) << 4));
            if (n >= 15)
            {
                int rest = n - 15;
                while (rest >= 255) { o.WriteByte(255); rest -= 255; }
                o.WriteByte((byte)rest);
            }
            o.Write(data);
            return o.ToArray();
        }

        sealed class BigEndianWriter(Stream s)
        {
            public void Bytes(byte[] b) => s.Write(b);
            public void Int(int v) { var b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
            public void Short(short v) { var b = new byte[2]; System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(b, v); s.Write(b); }
            public void Long(long v) { var b = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(b, v); s.Write(b); }
            public void CString(string v) { s.Write(Encoding.UTF8.GetBytes(v)); s.WriteByte(0); }
        }

        /// Blocks of uneven sizes (the last short, one stored, one empty) over random bytes.
        static (byte[] File, byte[] Whole) Sample(int seed = 1)
        {
            var random = new Random(seed);
            int[] sizes = [4096, 4096, 1000, 4096, 0, 4096, 4096, 777];
            var blocks = new List<(byte[], int)>();
            foreach (var (size, i) in sizes.Select((s, i) => (s, i)))
            {
                var data = new byte[size];
                random.NextBytes(data);
                blocks.Add((data, i == 2 ? 0 : 3));
            }
            var whole = blocks.SelectMany(b => b.Item1).ToArray();
            var file = Archive(blocks, [("CAB-test", 0, 9000), ("CAB-test.resS", 9000, whole.Length - 9000)]);
            return (file, whole);
        }

        [Fact]
        public void ChunkedLz4ArchivesAreReadByBlocks()
        {
            var (file, whole) = Sample();
            var archive = UnityFsArchive.ParseBlocks(file);
            Assert.True(archive.ReadsBlocks);
            Assert.False(UnityFsArchive.Parse(file).ReadsBlocks);
            foreach (var node in archive.Nodes)
                Assert.Equal(whole.AsSpan((int)node.Offset, (int)node.Size).ToArray(), archive.Read(node));
            Assert.Equal(UnityFsArchive.Parse(file).Nodes.Select(n => UnityFsArchive.Parse(file).Read(n)), archive.Nodes.Select(archive.Read));
        }

        [Fact]
        public void EveryRangeReadsAsTheWholeDecode()
        {
            var (file, whole) = Sample();
            var node = UnityFsArchive.ParseBlocks(file).Slice(new UnityFsArchive.Node("all", 0, whole.Length, 0));
            // Around every block boundary (4096, 8192, 9192, 13288 twice for the empty block,
            // 17384, 21480): reads starting, ending and spanning there.
            long[] boundaries = [0, 4096, 8192, 9192, 13288, 17384, 21480, whole.Length];
            foreach (long b in boundaries)
                for (long start = Math.Max(0, b - 3); start <= Math.Min(whole.Length, b + 3); start++)
                    foreach (int length in new[] { 0, 1, 2, 5, 1000, 4096, 4097, 9000, 12000 })
                    {
                        if (start + length > whole.Length) continue;
                        Assert.Equal(whole.AsSpan((int)start, length).ToArray(), node.ToArray(start, length));
                    }
            var random = new Random(7);
            for (int i = 0; i < 2000; i++)
            {
                int start = random.Next(whole.Length + 1);
                int length = random.Next(whole.Length - start + 1);
                if (i % 2 == 0) length = Math.Min(length, random.Next(64));
                Assert.Equal(whole.AsSpan(start, length).ToArray(), node.ToArray(start, length));
            }
            Assert.Equal(whole, node.ToArray());
        }

        [Fact]
        public void ReadsOutsideANodeAreRefused()
        {
            var (file, whole) = Sample();
            var archive = UnityFsArchive.ParseBlocks(file);
            var resource = archive.Slice(archive.Find("CAB-test.resS"));
            Assert.Equal(whole.Length - 9000, resource.Count);
            Assert.Equal(whole[9000..9010], resource.ToArray(0, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => resource.ToArray(resource.Count - 4, 8));
            Assert.Throws<ArgumentOutOfRangeException>(() => resource.ToArray(-1, 1));
            Assert.Empty(resource.ToArray(resource.Count, 0));
        }

        [Fact]
        public void ConcurrentReadersSeeTheWholeDecode()
        {
            var (file, whole) = Sample(3);
            var node = UnityFsArchive.ParseBlocks(file).Slice(new UnityFsArchive.Node("all", 0, whole.Length, 0));
            int failures = 0;
            // More threads than cache slots, each reading small ranges (through the cache) and
            // large ones (blocks decoded straight into the reader's buffer).
            Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, t =>
            {
                var random = new Random(100 + t);
                for (int i = 0; i < 3000; i++)
                {
                    int start = random.Next(whole.Length);
                    int length = Math.Min(whole.Length - start, i % 5 == 0 ? random.Next(20000) : random.Next(300));
                    if (!node.ToArray(start, length).AsSpan().SequenceEqual(whole.AsSpan(start, length))) Interlocked.Increment(ref failures);
                }
            });
            Assert.Equal(0, failures);
        }

        /// The cache holds no more than its capacity, evicting the least recently used blocks, and
        /// drops everything when idle; reads after either still give the whole decode's bytes.
        [Fact]
        public void TheCacheStaysBounded()
        {
            var random = new Random(5);
            var blocks = new List<(byte[], int)>();
            for (int i = 0; i < 640; i++)
            {
                var data = new byte[65536];
                random.NextBytes(data);
                blocks.Add((data, 3));
            }
            var whole = blocks.SelectMany(b => b.Item1).ToArray();
            var file = Archive(blocks, [("CAB-big", 0, whole.Length)]);
            var node = UnityFsArchive.ParseBlocks(file).Slice(new UnityFsArchive.Node("all", 0, whole.Length, 0));
            // A small read in each block: each block cached, 40 MiB in all.
            for (int b = 0; b < 640; b++)
            {
                Assert.Equal(whole.AsSpan(b * 65536 + 100, 10).ToArray(), node.ToArray(b * 65536 + 100, 10));
                Assert.InRange(BlockCache.Held, 0, BlockCache.Capacity);
            }
            Assert.Equal(whole.AsSpan(65536 * 639 + 5, 7).ToArray(), node.ToArray(65536 * 639 + 5, 7));
            BlockCache.Trim(force: true);
            Assert.Equal(0, BlockCache.Held);
            Assert.Equal(whole.AsSpan(65536 * 3 - 5, 70000).ToArray(), node.ToArray(65536 * 3 - 5, 70000));
        }

        [Fact]
        public void VerifyModeComparesEachRead()
        {
            var (file, whole) = Sample();
            var archive = UnityFsArchive.ParseBlocks(file, verify: true);
            var node = archive.Slice(archive.Nodes[1]);
            node.ToArray(5, 5000);
            Assert.Equal(whole[9005..14005], node.ToArray(5, 5000));
        }

        /// LZMA, every block stored, one block: decompressed whole, as before block reads.
        [Fact]
        public void OtherArchivesAreDecodedWhole()
        {
            var data = new byte[5000];
            new Random(2).NextBytes(data);
            var stored = Archive([(data[..2500], 0), (data[2500..], 0)], [("CAB-a", 0, 5000)]);
            var single = Archive([(data, 3)], [("CAB-a", 0, 5000)]);
            Assert.Throws<InvalidDataException>(() => UnityFsArchive.ParseBlocks(stored));
            Assert.Throws<InvalidDataException>(() => UnityFsArchive.ParseBlocks(single));
            var dir = Directory.CreateTempSubdirectory("archive-blocks-").FullName;
            try
            {
                foreach (var (name, bytes, blocks) in new[] { ("stored", stored, false), ("single", single, false), ("chunked", Sample().File, true) })
                {
                    var path = Path.Combine(dir, name);
                    File.WriteAllBytes(path, bytes);
                    var archive = UnityFsArchive.OpenFile(path);
                    Assert.Equal(blocks, archive.ReadsBlocks);
                    var whole = UnityFsArchive.Parse(bytes);
                    foreach (var node in whole.Nodes) Assert.Equal(whole.Read(node), archive.Read(node));
                }
            }
            finally
            {
                // Mapped files cannot be deleted on Windows while mapped; the folder is temporary.
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// A block table running past the file is refused when the archive is opened, as the
        /// whole decode refuses it.
        [Fact]
        public void TruncatedChunkedArchivesAreInvalidData()
        {
            var (file, _) = Sample();
            Assert.Throws<InvalidDataException>(() => UnityFsArchive.ParseBlocks(file[..^100]));
            Assert.Throws<InvalidDataException>(() => UnityFsArchive.Parse(file[..^100]));
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, file[..^100]);
                Assert.Throws<InvalidDataException>(() => UnityFsArchive.OpenFile(path));
                File.WriteAllBytes(path, []);
                Assert.Throws<InvalidDataException>(() => UnityFsArchive.OpenFile(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// The game's high-resolution bundle (U60: LZ4HC in 128 KiB blocks) opened from its file
        /// as LoadFromFile opens it: every node, and every object of its serialized file, read
        /// block by block is the whole decode's.
        [Fact]
        public void GameBundleReadByBlocksMatchesItsWholeDecode()
        {
            OniMoono.Tests.GameInstall.RequirePayloads(); // a stripped copy's bundles are not read by blocks
            var path = Path.Combine(GameData.DataDir, "StreamingAssets", "hires_base_bundle");
            var archive = UnityFsArchive.OpenFile(path, verify: true);
            var whole = UnityFsArchive.Open(path);
            Assert.True(archive.ReadsBlocks);
            foreach (var node in whole.Nodes)
                Assert.True(whole.Read(node).AsSpan().SequenceEqual(archive.Read(node)), node.Path);
            var cab = whole.Nodes.First(n => !n.Path.EndsWith(".resS"));
            var copy = SerializedFile.Parse(cab.Path, whole.Read(cab));
            var blocks = SerializedFile.Parse(cab.Path, archive.Slice(cab));
            Assert.Equal(copy.Objects, blocks.Objects);
            Assert.Equal(copy.Externals, blocks.Externals);
            Assert.Equal(copy.Types.Select(t => t.ClassId), blocks.Types.Select(t => t.ClassId));
            foreach (var obj in copy.Objects)
            {
                var a = copy.OpenObject(obj);
                var b = blocks.OpenObject(obj);
                Assert.Equal(a.ReadBytes(a.Length), b.ReadBytes(b.Length));
                TypeTreeReader.ReadObject(blocks.TypeOf(obj).Tree!, blocks.OpenObject(obj));
            }
            // The texels of a few textures, as TexelSource reads them: ranges of the .resS node.
            var resS = whole.Nodes.First(n => n.Path.EndsWith(".resS"));
            var inMemory = whole.Slice(resS);
            var byBlocks = archive.Slice(resS);
            var random = new Random(11);
            for (int i = 0; i < 200; i++)
            {
                long start = (long)(random.NextDouble() * resS.Size);
                int length = (int)Math.Min(resS.Size - start, random.Next(1 << 21));
                Assert.Equal(inMemory.ToArray(start, length), byBlocks.ToArray(start, length));
            }
            Assert.Matches(@"\), 0 mismatches;", BundleBlockStatsReport());
        }

        static string BundleBlockStatsReport()
        {
            string line = "";
            BundleBlockStats.StartVerify(l => line = l);
            BundleBlockStats.Report();
            return line;
        }
    }
}
