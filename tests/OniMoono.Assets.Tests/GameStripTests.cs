using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GameStrip;
using OniMoono.Assets.Files;
using Xunit;

namespace OniMoono.Assets.Tests
{
    /// tools/GameStrip: FMOD banks without their samples, bundles rewritten stored, and a data
    /// file's streamed references dropped.
    public class GameStripTests
    {
        static byte[] Chunk(string id, byte[] body)
        {
            var b = new List<byte>(Encoding.ASCII.GetBytes(id));
            b.AddRange(BitConverter.GetBytes(body.Length));
            b.AddRange(body);
            if ((body.Length & 1) != 0) b.Add(0);
            return b.ToArray();
        }

        static byte[] Riff(string form, params byte[][] chunks)
        {
            var body = Encoding.ASCII.GetBytes(form).Concat(chunks.SelectMany(c => c)).ToArray();
            return Chunk("RIFF", body);
        }

        [Fact]
        public void BankKeepsEverythingButItsSampleChunks()
        {
            var proj = Chunk("LIST", Encoding.ASCII.GetBytes("PROJ").Concat(new byte[] { 1, 2, 3 }).ToArray()); // odd: padded
            var hash = Chunk("HASH", new byte[] { 9, 9, 9, 9 });
            var bank = Riff("FEV ", proj, Chunk("SND ", new byte[1000]), hash, Chunk("SND ", new byte[17]));

            var stripped = Banks.Strip(bank, "test.bank");

            Assert.Equal(Riff("FEV ", proj, hash), stripped);
            Assert.Equal(stripped.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(stripped.AsSpan(4)));
        }

        [Fact]
        public void NonBankIsRefused()
        {
            Assert.Throws<InvalidDataException>(() => Banks.Strip(Riff("WAVE", Chunk("data", new byte[4])), "x.wav"));
            var truncated = Riff("FEV ", Chunk("SND ", new byte[64]))[..40];
            Assert.Throws<InvalidDataException>(() => Banks.Strip(truncated, "short.bank"));
        }

        [Theory]
        [InlineData(6u)]
        [InlineData(8u)]
        public void BundleRoundTripsThroughTheReader(uint format)
        {
            var header = new List<byte>(Encoding.ASCII.GetBytes("UnityFS\0"));
            var f = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(f, format);
            header.AddRange(f);
            header.AddRange(Encoding.ASCII.GetBytes("5.x.x\0" + "6000.3.5f2\0"));
            var rng = new Random(1);
            var big = new byte[300_000]; // spans three blocks
            rng.NextBytes(big);
            var nodes = new List<(string, uint, byte[])>
            {
                ("CAB-abc", 4u, Encoding.UTF8.GetBytes("serialized")),
                ("CAB-abc.resS", 0u, big),
                ("CAB-abc.resource", 0u, []),
            };

            var archive = UnityFsArchive.Parse(BundleWriter.Write(header.ToArray(), nodes));

            Assert.Equal("6000.3.5f2", archive.UnityRevision);
            Assert.Equal(nodes.Select(n => (n.Item1, n.Item2)), archive.Nodes.Select(n => (n.Path, n.Flags)));
            for (int i = 0; i < nodes.Count; i++) Assert.Equal(nodes[i].Item3, archive.Read(archive.Nodes[i]));
        }

        /// A data file of the game with its streamed payloads dropped: still a valid file, every
        /// reference size 0 at offset 0 with its path kept, and nothing else changed.
        [Fact]
        public void StrippedDataFileParsesWithEmptyReferences()
        {
            var dir = GameData.DataDir;
            OniMoono.Tests.GameInstall.RequirePayloads(); // a stripped copy's references are already empty
            var game = Path.GetDirectoryName(dir)!;
            var before = Install.Load(game, Path.Combine("OxygenNotIncluded_Data", "sharedassets1.assets"));
            Assert.NotEmpty(before.Refs);
            var original = (byte[])before.Bytes.Clone();
            foreach (var r in before.Refs)
            {
                BinaryPrimitives.WriteInt64LittleEndian(before.Bytes.AsSpan(r.OffsetAt), 0);
                BinaryPrimitives.WriteUInt32LittleEndian(before.Bytes.AsSpan(r.SizeAt), 0);
            }

            var after = StreamRefs.Find(SerializedFile.Parse("sharedassets1.assets", before.Bytes));

            Assert.Equal(before.Refs.Select(r => (r.PathId, r.Path)), after.Select(r => (r.PathId, r.Path)));
            Assert.All(after, r => Assert.Equal((0L, 0L), (r.Offset, r.Size)));
            // Only the offset and size fields changed.
            var changed = Enumerable.Range(0, original.Length).Where(i => original[i] != before.Bytes[i]).ToHashSet();
            var fields = before.Refs.SelectMany(r => Enumerable.Range(r.OffsetAt, 8).Concat(Enumerable.Range(r.SizeAt, 4))).ToHashSet();
            Assert.Subset(fields, changed);
        }
    }
}
