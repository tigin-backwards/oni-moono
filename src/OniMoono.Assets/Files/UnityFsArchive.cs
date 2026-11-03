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
        /// All blocks, decompressed (`blocks` null), or read block by block from the file (ArchiveBlocks).
        readonly byte[]? data;
        readonly ArchiveBlocks? blocks;

        internal const uint CompressionMask = 0x3F;
        const uint BlocksInfoAtEnd = 0x80;
        const uint BlockInfoNeedPaddingAtStart = 0x200;

        UnityFsArchive(string revision, List<Node> nodes, byte[]? data, ArchiveBlocks? blocks)
        {
            long length = blocks?.Length ?? data!.Length;
            foreach (var n in nodes)
                if (n.Offset < 0 || n.Size < 0 || n.Offset > length || n.Size > length - n.Offset)
                    throw new InvalidDataException($"UnityFS: node {n.Path} runs past the archive's data.");
            UnityRevision = revision;
            Nodes = nodes;
            this.data = data;
            this.blocks = blocks;
        }

        /// The archive at `path`, decompressed into memory.
        public static UnityFsArchive Open(string path) => Parse(File.ReadAllBytes(path));

        /// The archive at `path` as AssetBundle.LoadFromFile opens it: a chunked LZ4 archive is
        /// mapped and read block by block (ArchiveBlocks.Suits), any other is decompressed whole
        /// (Open). `verify` also decompresses a chunked one whole, and compares each read with it.
        public static unsafe UnityFsArchive OpenFile(string path, bool verify = false)
        {
            MappedFile file;
            try
            {
                file = new MappedFile(path);
            }
            catch (ArgumentException e)
            {
                // An empty file cannot be mapped (Open reads it, and finds no archive).
                throw new InvalidDataException("Not a UnityFS archive.", e);
            }
            bool keep = false;
            try
            {
                var archive = Checked(() =>
                {
                    if (file.Length >= int.MaxValue) throw new InvalidDataException("UnityFS: an archive of 2 GB or more.");
                    var bytes = new ReadOnlySpan<byte>(file.Pointer, (int)file.Length);
                    var layout = ReadLayout(new DataReader(file, 0, (int)file.Length), bytes);
                    if (!ArchiveBlocks.Suits(layout.Blocks))
                    {
                        var data = new byte[layout.Total];
                        DecodeAll(bytes, layout, data);
                        return new UnityFsArchive(layout.Revision, layout.Nodes, data, null);
                    }
                    byte[]? whole = null;
                    if (verify)
                    {
                        whole = new byte[layout.Total];
                        DecodeAll(bytes, layout, whole);
                    }
                    keep = true;
                    return new UnityFsArchive(layout.Revision, layout.Nodes, null, new ArchiveBlocks(Path.GetFileName(path), file, null, layout.DataStart, layout.Blocks, whole));
                });
                return archive;
            }
            finally
            {
                if (!keep) file.Dispose();
            }
        }

        /// A damaged or truncated file (sizes, offsets or counts past its end) is invalid data.
        public static UnityFsArchive Parse(byte[] file) => Checked(() =>
        {
            var layout = ReadLayout(new DataReader(file), file);
            var data = new byte[layout.Total];
            DecodeAll(file, layout, data);
            return new UnityFsArchive(layout.Revision, layout.Nodes, data, null);
        });

        /// A chunked LZ4 archive in memory read block by block (tests; the whole decode otherwise).
        internal static UnityFsArchive ParseBlocks(byte[] file, bool verify = false) => Checked(() =>
        {
            var layout = ReadLayout(new DataReader(file), file);
            if (!ArchiveBlocks.Suits(layout.Blocks)) throw new ArgumentException("Not a chunked LZ4 archive.", nameof(file));
            byte[]? whole = null;
            if (verify)
            {
                whole = new byte[layout.Total];
                DecodeAll(file, layout, whole);
            }
            return new UnityFsArchive(layout.Revision, layout.Nodes, null, new ArchiveBlocks("memory", null, file, layout.DataStart, layout.Blocks, whole));
        });

        /// Read block by block (ArchiveBlocks), not decompressed whole.
        public bool ReadsBlocks => blocks != null;

        static T Checked<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or OverflowException or OutOfMemoryException or EndOfStreamException)
            {
                throw new InvalidDataException("UnityFS: damaged or truncated archive.", e);
            }
        }

        sealed record Layout(string Revision, List<Node> Nodes, (int Uncompressed, int Compressed, ushort Flags)[] Blocks, int DataStart, long Total);

        /// The header, block table and node table; `r` reads `file` from its start.
        static Layout ReadLayout(DataReader r, ReadOnlySpan<byte> file)
        {
            r.BigEndian = true;
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
                infoBytes = file.Slice(file.Length - compressedInfoSize, compressedInfoSize).ToArray();
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
            var blocks = new (int Uncompressed, int Compressed, ushort Flags)[blockCount];
            long total = 0;
            for (int i = 0; i < blockCount; i++)
            {
                blocks[i] = (ir.ReadInt32(), ir.ReadInt32(), ir.ReadUInt16());
                total += blocks[i].Uncompressed;
            }
            int nodeCount = ir.ReadInt32();
            var nodes = new List<Node>(nodeCount);
            for (int i = 0; i < nodeCount; i++)
                nodes.Add(new Node(Path: ReadNodePath(ir, out var offset, out var size, out var nodeFlags), offset, size, nodeFlags));
            return new Layout(revision, nodes, blocks, r.AbsolutePosition, total);
        }

        /// Every block decompressed into `data` (the layout's Total bytes).
        static void DecodeAll(ReadOnlySpan<byte> file, Layout layout, byte[] data)
        {
            int outPos = 0;
            int inPos = layout.DataStart;
            foreach (var b in layout.Blocks)
            {
                if (b.Compressed < 0 || b.Uncompressed < 0 || (long)inPos + b.Compressed > file.Length || (long)outPos + b.Uncompressed > data.Length)
                    throw new InvalidDataException("UnityFS: block table runs past the file (truncated archive).");
                var src = file.Slice(inPos, b.Compressed);
                var dst = new Span<byte>(data, outPos, b.Uncompressed);
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

        internal static void DecompressInto(ReadOnlySpan<byte> input, Span<byte> output, uint compression)
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

        /// The bytes of a node, in place: a window on the archive's decompressed data, which stays
        /// alive while any node's window is held, or a range of its blocks (ArchiveBlocks).
        public NodeData Slice(Node node) => blocks != null
            ? new NodeData(blocks, node.Offset, checked((int)node.Size))
            : new NodeData(new ArraySegment<byte>(data!, checked((int)node.Offset), checked((int)node.Size)));

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
