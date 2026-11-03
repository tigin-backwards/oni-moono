using System;

namespace OniMoono.Assets.Files
{
    /// A bundle node's bytes (UnityFsArchive.Slice): a window on its archive's data decompressed
    /// whole (`Array`), or a range of an archive read block by block (`Blocks`, from `Offset`).
    /// Either stays alive while the node is held. `default` is no node.
    public readonly struct NodeData
    {
        public readonly ArraySegment<byte> Array;
        public readonly ArchiveBlocks? Blocks;
        public readonly long Offset;
        readonly int blocksCount;

        public NodeData(ArraySegment<byte> array)
        {
            Array = array;
            Blocks = null;
            Offset = 0;
            blocksCount = 0;
        }

        public NodeData(ArchiveBlocks blocks, long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > blocks.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            Array = default;
            Blocks = blocks;
            Offset = offset;
            blocksCount = count;
        }

        public static implicit operator NodeData(ArraySegment<byte> array) => new(array);

        public bool IsEmpty => Array.Array == null && Blocks == null;
        public int Count => Blocks != null ? blocksCount : Array.Count;

        /// Copies `destination.Length` bytes at `offset` in the node into `destination`.
        public void CopyTo(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > Count || destination.Length > Count - offset) throw new ArgumentOutOfRangeException(nameof(offset));
            if (Blocks != null) Blocks.Read(Offset + offset, destination);
            else Array.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }

        /// `count` bytes at `offset` in the node, in a new array.
        public byte[] ToArray(long offset, int count)
        {
            var result = new byte[count];
            CopyTo(offset, result);
            return result;
        }

        public byte[] ToArray() => ToArray(0, Count);
    }
}
