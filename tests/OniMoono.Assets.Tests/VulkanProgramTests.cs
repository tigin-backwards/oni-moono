using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Assets.Shaders;
using OniMoono.Assets.TypeTrees;
using Xunit;

namespace OniMoono.Assets.Tests
{
    /// The SMOL-V decoder and the Vulkan program entry reader on damaged input: every failure is
    /// an InvalidDataException, never an overflow, an out-of-range index or a huge allocation.
    public class VulkanProgramTests
    {
        /// A SMOL-V stream with a header and no instructions (a 20-byte SPIR-V header).
        static byte[] Smol(uint decodedSize = 20, uint version = 0x00010300, byte[]? body = null)
        {
            var b = new List<byte>();
            foreach (uint w in new[] { 0x534D4F4Cu, version, 0u, 1u, 0u, decodedSize }) b.AddRange(BitConverter.GetBytes(w));
            if (body != null) b.AddRange(body);
            return b.ToArray();
        }

        /// An entry as Unity writes them: header, no keywords, a payload with the vertex and
        /// fragment stage slots, the channel mask and one (channel, component) pair.
        static byte[] Entry(byte[] vertex, byte[] fragment, int? payloadLength = null, int? vertexOffset = null, int? vertexSize = null)
        {
            const int header = 4 + 6 * 8;
            var payload = new List<byte>();
            payload.AddRange(BitConverter.GetBytes(0u));
            int[] slots = [vertexOffset ?? header, vertexSize ?? vertex.Length, header + vertex.Length, fragment.Length, 0, 0, 0, 0, 0, 0, 0, 0];
            foreach (int s in slots) payload.AddRange(BitConverter.GetBytes(s));
            payload.AddRange(vertex);
            payload.AddRange(fragment);
            while (payload.Count % 4 != 0) payload.Add(0);
            var e = new List<byte>();
            foreach (int w in new[] { unchecked((int)MetalProgram.EntryMagic), GpuProgramTypes.SpirV, 0, 0, 0, 0, 0, payloadLength ?? payload.Count }) e.AddRange(BitConverter.GetBytes(w));
            e.AddRange(payload);
            foreach (int w in new[] { 1, 1, 0, 13 }) e.AddRange(BitConverter.GetBytes(w));
            return e.ToArray();
        }

        [Fact]
        public void AWellFormedEntryReads()
        {
            var p = VulkanProgram.Read(Entry(Smol(), Smol()));
            Assert.Equal([0, 1], p.Stages.Select(s => s.Index));
            Assert.All(p.Stages, s => Assert.Equal(0x07230203u, BitConverter.ToUInt32(s.Spirv)));
            Assert.Equal([(0, 13)], p.Inputs);
        }

        [Fact]
        public void SmolVSizesAreCheckedAgainstTheStream()
        {
            Assert.Throws<InvalidDataException>(() => SmolV.Decode(Smol(decodedSize: 0x7FFFFFF0)));
            Assert.Throws<InvalidDataException>(() => SmolV.Decode(Smol(decodedSize: 0xFFFFFFFC)));
            Assert.Throws<InvalidDataException>(() => SmolV.Decode(Smol(decodedSize: 22)));
            Assert.Throws<InvalidDataException>(() => SmolV.Decode(Smol(decodedSize: 24)));
            Assert.Throws<InvalidDataException>(() => SmolV.Decode(Smol(decodedSize: 0)));
        }

        [Fact]
        public void TruncatedOrOverlongVarintsAreRefused()
        {
            Assert.Throws<InvalidDataException>(() => SmolV.Decode(Smol(decodedSize: 24, body: [0x80])));
            Assert.Throws<InvalidDataException>(() => SmolV.Decode(Smol(decodedSize: 24, body: [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01])));
        }

        [Fact]
        public void DamagedEntriesAreRefused()
        {
            var vertex = Smol();
            Assert.Throws<InvalidDataException>(() => VulkanProgram.Read(Entry(vertex, Smol(), payloadLength: int.MaxValue)));
            Assert.Throws<InvalidDataException>(() => VulkanProgram.Read(Entry(vertex, Smol(), payloadLength: -4)));
            Assert.Throws<InvalidDataException>(() => VulkanProgram.Read(Entry(vertex, Smol(), payloadLength: 8)));
            Assert.Throws<InvalidDataException>(() => VulkanProgram.Read(Entry(vertex, Smol(), vertexOffset: int.MaxValue)));
            Assert.Throws<InvalidDataException>(() => VulkanProgram.Read(Entry(vertex, Smol(), vertexOffset: -8)));
            Assert.Throws<InvalidDataException>(() => VulkanProgram.Read(Entry(vertex, Smol(), vertexOffset: 0)));
            Assert.Throws<InvalidDataException>(() => VulkanProgram.Read(Entry(vertex, Smol(), vertexSize: int.MaxValue)));
        }

        /// Every prefix of an entry, and every single-byte change of one, either reads or fails
        /// with InvalidDataException.
        [Fact]
        public void TruncationAndBitFlipsFailCleanly()
        {
            var entry = Entry(Smol(), Smol());
            for (int n = 0; n < entry.Length; n++)
                Assert.Throws<InvalidDataException>(() => VulkanProgram.Read(entry.AsSpan(0, n)));
            foreach (var damaged in Flips(entry)) ReadsOrRefuses(damaged);
            // A real program.
            if (RealEntry() is { } real)
            {
                for (int n = 0; n < real.Length; n += Math.Max(1, real.Length / 300)) ReadsOrRefuses(real.AsSpan(0, n).ToArray());
                foreach (var damaged in Flips(real, step: Math.Max(1, real.Length / 600))) ReadsOrRefuses(damaged);
            }
        }

        static IEnumerable<byte[]> Flips(byte[] entry, int step = 1)
        {
            for (int i = 0; i < entry.Length; i += step)
                foreach (byte v in new byte[] { 0x00, 0xFF, 0x80, (byte)(entry[i] ^ 0x01) })
                {
                    var copy = (byte[])entry.Clone();
                    copy[i] = v;
                    yield return copy;
                }
        }

        static void ReadsOrRefuses(byte[] entry)
        {
            try
            {
                VulkanProgram.Read(entry);
            }
            catch (InvalidDataException)
            {
            }
        }

        /// A program of the game's data; without the data the test is skipped here, after the
        /// made-up entries.
        static byte[]? RealEntry()
        {
            foreach (var shader in ShaderDataTests.Shaders(GameData.DataDir))
            {
                var table = ShaderProgramTable.Load(shader, ShaderCompilerPlatform.Vulkan);
                if (table == null) continue;
                var parsed = shader.Struct("m_ParsedForm");
                var keywords = parsed.Array("m_KeywordNames").Cast<string>().ToArray();
                foreach (var sub in parsed.Array("m_SubShaders").Cast<Fields>())
                    foreach (var pass in sub.Array("m_Passes").Cast<Fields>())
                        foreach (var x in PlayerSubprograms.Of(pass, "progVertex", keywords).Where(x => x.Platform == ShaderCompilerPlatform.Vulkan))
                            return table.Get(x.BlobIndex).ToArray();
            }
            return null;
        }
    }
}
