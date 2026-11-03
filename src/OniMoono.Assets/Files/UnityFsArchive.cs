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
        /// All blocks, decompressed: an array, or a mapped file (BundleCache).
        readonly ReadOnlyMemory<byte> data;

        const uint CompressionMask = 0x3F;
        const uint BlocksInfoAtEnd = 0x80;
        const uint BlockInfoNeedPaddingAtStart = 0x200;

        /// An archive's header and tables, read from its file without decompressing its data.
        public sealed class Layout
        {
            public string Revision = "";
            public List<Node> Nodes = new();
            internal (int Uncompressed, int Compressed, ushort Flags)[] Blocks = [];
            /// Where the first block starts in the file.
            public int DataStart;
            /// The decompressed data's length (the blocks' sum).
            public long DataLength;

            /// Every block stored as it is: the data is the file's bytes from DataStart on.
            public bool Stored
            {
                get
                {
                    foreach (var b in Blocks)
                        if ((b.Flags & CompressionMask) != 0 || b.Compressed != b.Uncompressed) return false;
                    return true;
                }
            }
        }

        UnityFsArchive(Layout layout, ReadOnlyMemory<byte> data)
        {
            if (data.Length != layout.DataLength) throw new InvalidDataException("UnityFS: the data's length is not the blocks' sum.");
            foreach (var n in layout.Nodes)
                if (n.Offset < 0 || n.Size < 0 || n.Offset > data.Length || n.Size > data.Length - n.Offset)
                    throw new InvalidDataException($"UnityFS: node {n.Path} runs past the archive's data.");
            UnityRevision = layout.Revision;
            Nodes = layout.Nodes;
            this.data = data;
        }

        /// The archive at `path`, decompressed into memory.
        public static UnityFsArchive Open(string path) => Parse(File.ReadAllBytes(path));

        /// A damaged or truncated file (sizes, offsets or counts past its end) is invalid data.
        public static UnityFsArchive Parse(byte[] file) => Checked(() =>
        {
            var layout = ReadLayoutChecked(file);
            var data = new byte[layout.DataLength];
            DecodeChecked(file, layout, data);
            return new UnityFsArchive(layout, data);
        });

        /// The archive's header and tables (invalid data for a damaged file).
        public static Layout ReadLayout(byte[] file) => Checked(() => ReadLayoutChecked(file));

        /// Decompresses every block of `file` into `output`, which is DataLength bytes.
        public static void Decode(byte[] file, Layout layout, Span<byte> output)
        {
            try
            {
                DecodeChecked(file, layout, output);
            }
            catch (Exception e) when (IsDamage(e))
            {
                throw new InvalidDataException("UnityFS: damaged or truncated archive.", e);
            }
        }

        /// An archive over its data decompressed already (DataLength bytes: a mapping).
        public static UnityFsArchive FromData(Layout layout, ReadOnlyMemory<byte> data) => new(layout, data);

        static bool IsDamage(Exception e) => e is ArgumentException or IndexOutOfRangeException or OverflowException or OutOfMemoryException or EndOfStreamException;

        static T Checked<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception e) when (IsDamage(e))
            {
                throw new InvalidDataException("UnityFS: damaged or truncated archive.", e);
            }
        }

        static Layout ReadLayoutChecked(byte[] file)
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
            var blocks = new (int, int, ushort)[blockCount];
            long total = 0;
            for (int i = 0; i < blockCount; i++)
            {
                blocks[i] = (ir.ReadInt32(), ir.ReadInt32(), ir.ReadUInt16());
                total += blocks[i].Item1;
            }
            int nodeCount = ir.ReadInt32();
            var nodes = new List<Node>(nodeCount);
            for (int i = 0; i < nodeCount; i++)
                nodes.Add(new Node(Path: ReadNodePath(ir, out var offset, out var size, out var nodeFlags), offset, size, nodeFlags));
            return new Layout { Revision = revision, Nodes = nodes, Blocks = blocks, DataStart = r.AbsolutePosition, DataLength = total };
        }

        static void DecodeChecked(byte[] file, Layout layout, Span<byte> data)
        {
            if (data.Length != layout.DataLength) throw new ArgumentException("The output is not the archive's data length.", nameof(data));
            int outPos = 0;
            int inPos = layout.DataStart;
            foreach (var b in layout.Blocks)
            {
                if (b.Compressed < 0 || b.Uncompressed < 0 || (long)inPos + b.Compressed > file.Length || (long)outPos + b.Uncompressed > data.Length)
                    throw new InvalidDataException("UnityFS: block table runs past the file (truncated archive).");
                var src = new ReadOnlySpan<byte>(file, inPos, b.Compressed);
                var dst = data.Slice(outPos, b.Uncompressed);
                DecompressInto(src, dst, (uint)b.Flags & CompressionMask);
                inPos += b.Compressed;
                outPos += b.Uncompressed;
            }
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
        public byte[] Read(Node node) => Slice(node).ToArray();

        /// The bytes of a node, in place: a window on the archive's decompressed data (an array,
        /// or a mapped file), which stays alive while any node's window is held.
        public ReadOnlyMemory<byte> Slice(Node node) => data.Slice(checked((int)node.Offset), checked((int)node.Size));

        /// The decompressed data, in place.
        public ReadOnlyMemory<byte> Data => data;

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
