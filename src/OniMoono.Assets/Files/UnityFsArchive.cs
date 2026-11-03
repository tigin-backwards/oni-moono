using System;
using System.Collections.Generic;
using System.IO;
using OniMoono.Assets.Compression;
using OniMoono.Assets.IO;

namespace OniMoono.Assets.Files
{
    /// A UnityFS AssetBundle archive: a set of named nodes (SerializedFiles, .resS streams) stored
    /// in compressed blocks.
    public sealed class UnityFsArchive
    {
        public sealed record Node(string Path, long Offset, long Size, uint Flags);

        public string UnityRevision { get; }
        public IReadOnlyList<Node> Nodes { get; }
        readonly byte[] data; // all blocks, decompressed

        const uint CompressionMask = 0x3F;
        const uint BlocksInfoAtEnd = 0x80;
        const uint BlockInfoNeedPaddingAtStart = 0x200;

        UnityFsArchive(string revision, List<Node> nodes, byte[] data)
        {
            UnityRevision = revision;
            Nodes = nodes;
            this.data = data;
        }

        public static UnityFsArchive Open(string path) => Parse(File.ReadAllBytes(path));

        /// A damaged or truncated file (sizes, offsets or counts past its end) is invalid data.
        public static UnityFsArchive Parse(byte[] file)
        {
            try
            {
                return ParseChecked(file);
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or OverflowException or OutOfMemoryException or EndOfStreamException)
            {
                throw new InvalidDataException("UnityFS: damaged or truncated archive.", e);
            }
        }

        static UnityFsArchive ParseChecked(byte[] file)
        {
            var r = new DataReader(file) { BigEndian = true };
            if (r.ReadCString() != "UnityFS") throw new InvalidDataException("Not a UnityFS archive.");
            uint format = r.ReadUInt32();
            r.ReadCString(); // player version ("5.x.x")
            var revision = r.ReadCString();
            r.ReadInt64(); // total size
            int compressedInfoSize = r.ReadInt32();
            int uncompressedInfoSize = r.ReadInt32();
            uint flags = r.ReadUInt32();
            if (format >= 7) r.Align(16);

            byte[] infoBytes;
            if ((flags & BlocksInfoAtEnd) != 0)
            {
                infoBytes = new byte[compressedInfoSize];
                Array.Copy(file, file.Length - compressedInfoSize, infoBytes, 0, compressedInfoSize);
            }
            else
            {
                infoBytes = r.ReadBytes(compressedInfoSize);
            }
            var info = Decompress(infoBytes, uncompressedInfoSize, flags & CompressionMask);
            if ((flags & BlockInfoNeedPaddingAtStart) != 0) r.Align(16);

            var ir = new DataReader(info) { BigEndian = true };
            ir.Skip(16); // uncompressed data hash
            int blockCount = ir.ReadInt32();
            var blocks = new (int uncompressed, int compressed, ushort flags)[blockCount];
            long total = 0;
            for (int i = 0; i < blockCount; i++)
            {
                blocks[i] = (ir.ReadInt32(), ir.ReadInt32(), ir.ReadUInt16());
                total += blocks[i].uncompressed;
            }
            int nodeCount = ir.ReadInt32();
            var nodes = new List<Node>(nodeCount);
            for (int i = 0; i < nodeCount; i++)
                nodes.Add(new Node(Path: ReadNodePath(ir, out var offset, out var size, out var nodeFlags), offset, size, nodeFlags));

            var data = new byte[total];
            int outPos = 0;
            int inPos = r.AbsolutePosition;
            foreach (var b in blocks)
            {
                if (b.compressed < 0 || b.uncompressed < 0 || (long)inPos + b.compressed > file.Length || (long)outPos + b.uncompressed > data.Length)
                    throw new InvalidDataException("UnityFS: block table runs past the file (truncated archive).");
                var src = new ReadOnlySpan<byte>(file, inPos, b.compressed);
                var dst = new Span<byte>(data, outPos, b.uncompressed);
                DecompressInto(src, dst, (uint)b.flags & CompressionMask);
                inPos += b.compressed;
                outPos += b.uncompressed;
            }
            foreach (var n in nodes)
                if (n.Offset < 0 || n.Size < 0 || n.Offset > data.Length || n.Size > data.Length - n.Offset)
                    throw new InvalidDataException($"UnityFS: node {n.Path} runs past the archive's data.");
            return new UnityFsArchive(revision, nodes, data);
        }

        static string ReadNodePath(DataReader r, out long offset, out long size, out uint flags)
        {
            offset = r.ReadInt64();
            size = r.ReadInt64();
            flags = r.ReadUInt32();
            return r.ReadCString();
        }

        static byte[] Decompress(byte[] input, int size, uint compression)
        {
            var output = new byte[size];
            DecompressInto(input, output, compression);
            return output;
        }

        static void DecompressInto(ReadOnlySpan<byte> input, Span<byte> output, uint compression)
        {
            switch (compression)
            {
                case 0:
                    if (input.Length != output.Length) throw new InvalidDataException("Stored block size mismatch.");
                    input.CopyTo(output);
                    break;
                case 1: Lzma.Decode(input, output); break;
                case 2:
                case 3: Lz4.Decode(input, output); break;
                default: throw new InvalidDataException($"Unsupported UnityFS compression {compression}.");
            }
        }

        /// The bytes of a node (a copy).
        public byte[] Read(Node node)
        {
            var result = new byte[node.Size];
            Array.Copy(data, node.Offset, result, 0, node.Size);
            return result;
        }

        /// The bytes of a node, in place: a window on the archive's decompressed data, which stays
        /// alive while any node's window is held.
        public ArraySegment<byte> Slice(Node node) => new(data, checked((int)node.Offset), checked((int)node.Size));

        public Node Find(string path) => Nodes.Find(n => n.Path == path) ?? throw new FileNotFoundException(path);
    }

    static class ListExtensions
    {
        public static T? Find<T>(this IReadOnlyList<T> list, Predicate<T> match) where T : class
        {
            foreach (var x in list) if (match(x)) return x;
            return null;
        }
    }
}
