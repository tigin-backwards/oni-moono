using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using OniMoono.Assets.Compression;
using OniMoono.Assets.IO;

namespace OniMoono.Assets.Files
{
    /// A UnityFS archive's data read block by block, as Unity reads a chunked LZ4 bundle: each read
    /// decodes the blocks it covers from the archive's file, and nothing is held but the decoded
    /// blocks of a bounded cache (BlockCache). A block a read covers whole is decoded straight into
    /// the reader's buffer; a block it covers in part goes through the cache, so neighbouring reads
    /// (the objects of a serialized file, the two ends of a texture) decode it once. The bytes a
    /// read gives are those the whole decode gives (UnityFsArchive.Parse): the same decoder, block
    /// by block. Thread-safe: readers on any threads (the main thread's objects, texture uploads).
    public sealed unsafe class ArchiveBlocks
    {
        readonly MappedFile? file;
        readonly byte[]? fileBytes;
        readonly long fileLength;
        /// Block i's decoded data starts at dataStart[i] and ends at dataStart[i + 1]; its
        /// compressed bytes start at fileStart[i] in the file.
        readonly long[] dataStart;
        readonly long[] fileStart;
        readonly int[] compressedSize;
        readonly byte[] compression;
        /// Verify mode: the whole decode, which each read is compared with.
        readonly byte[]? whole;
        readonly string name;


        public long Length => dataStart[^1];
        public string Name => name;

        internal ArchiveBlocks(string name, MappedFile? file, byte[]? fileBytes, int firstBlockAt, (int Uncompressed, int Compressed, ushort Flags)[] blocks, byte[]? whole)
        {
            this.name = name;
            this.file = file;
            this.fileBytes = fileBytes;
            fileLength = file?.Length ?? fileBytes!.Length;
            dataStart = new long[blocks.Length + 1];
            fileStart = new long[blocks.Length];
            compressedSize = new int[blocks.Length];
            compression = new byte[blocks.Length];
            long data = 0, at = firstBlockAt;
            for (int i = 0; i < blocks.Length; i++)
            {
                var b = blocks[i];
                if (b.Compressed < 0 || b.Uncompressed < 0 || at + b.Compressed > fileLength)
                    throw new InvalidDataException("UnityFS: block table runs past the file (truncated archive).");
                dataStart[i] = data;
                fileStart[i] = at;
                compressedSize[i] = b.Compressed;
                compression[i] = (byte)(b.Flags & UnityFsArchive.CompressionMask);
                data += b.Uncompressed;
                at += b.Compressed;
            }
            dataStart[blocks.Length] = data;
            if (whole != null && whole.Length != data) throw new ArgumentException("The whole decode is not the archive's data length.", nameof(whole));
            this.whole = whole;
        }

        /// The blocks that UnityFsArchive reads this way: LZ4 (or LZ4HC) blocks, maybe with stored
        /// ones among them, at least two and none over 1 MiB. Any other archive (LZMA, every block
        /// stored, one big block) is decoded whole, as before.
        internal static bool Suits((int Uncompressed, int Compressed, ushort Flags)[] blocks)
        {
            if (blocks.Length < 2) return false;
            bool lz4 = false;
            foreach (var b in blocks)
            {
                uint c = b.Flags & UnityFsArchive.CompressionMask;
                if (c == 2 || c == 3) lz4 = true;
                else if (c != 0) return false;
                if (b.Uncompressed > 1 << 20) return false;
            }
            return lz4;
        }

        ReadOnlySpan<byte> Compressed(int block)
        {
            long at = fileStart[block];
            int size = compressedSize[block];
            return file != null ? new ReadOnlySpan<byte>(file.Pointer + at, size) : new ReadOnlySpan<byte>(fileBytes, checked((int)at), size);
        }

        void Decode(int block, Span<byte> output)
        {
            long start = Stopwatch.GetTimestamp();
            UnityFsArchive.DecompressInto(Compressed(block), output, compression[block]);
            BundleBlockStats.Decoded(output.Length, Stopwatch.GetTimestamp() - start);
        }

        /// The block holding data offset `offset` (the last of any empty blocks before it).
        int BlockAt(long offset)
        {
            int lo = 0, hi = dataStart.Length - 2;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (dataStart[mid] <= offset) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /// Copies `destination.Length` bytes of the archive's data at `offset` into `destination`.
        public void Read(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > Length || destination.Length > Length - offset) throw new ArgumentOutOfRangeException(nameof(offset));
            if (destination.Length == 0) return;
            long start = Stopwatch.GetTimestamp();
            int block = BlockAt(offset);
            long at = offset;
            int done = 0;
            while (done < destination.Length)
            {
                long blockStart = dataStart[block];
                int blockLength = (int)(dataStart[block + 1] - blockStart);
                int within = (int)(at - blockStart);
                int count = Math.Min(blockLength - within, destination.Length - done);
                if (count > 0)
                {
                    var part = destination.Slice(done, count);
                    if (count == blockLength) Decode(block, part);
                    else CopyCached(block, within, part);
                    done += count;
                    at += count;
                }
                block++;
            }
            BundleBlockStats.Read(destination.Length, Stopwatch.GetTimestamp() - start);
            if (whole != null) Verify(offset, destination);
        }

        public byte[] ToArray(long offset, int count)
        {
            var result = new byte[count];
            Read(offset, result);
            return result;
        }

        /// Part of a block, through the cache. Decodes are made outside its lock: two threads
        /// missing the same block both decode it, to the same bytes.
        void CopyCached(int block, int within, Span<byte> destination)
        {
            if (BlockCache.TryCopy(this, block, within, destination))
            {
                BundleBlockStats.Hit();
                return;
            }
            BundleBlockStats.Miss();
            int length = (int)(dataStart[block + 1] - dataStart[block]);
            var buffer = BlockCache.Rent(length);
            Decode(block, buffer.AsSpan(0, length));
            BlockCache.Add(this, block, buffer, within, destination);
        }

        void Verify(long offset, ReadOnlySpan<byte> read)
        {
            var expected = new ReadOnlySpan<byte>(whole, checked((int)offset), read.Length);
            bool same = read.SequenceEqual(expected);
            BundleBlockStats.Verified(read.Length, same);
            if (!same)
            {
                int first = 0;
                while (read[first] == expected[first]) first++;
                BundleBlockStats.Log?.Invoke($"bundle blocks: a read of {read.Length} bytes at {offset} in {name} differs from the whole decode at byte {offset + first} ({read[first]} for {expected[first]})");
            }
        }
    }

    /// The decoded blocks of the archives read block by block, shared by all of them: up to
    /// Capacity bytes, the least recently used going first, all of it dropped once no read has
    /// used it for IdleSeconds. U60's bundles, as the game loads them (its root prefabs pull every
    /// object of each serialized file in at start-up, in reference order, not the file's), need
    /// about 30 MB for their 5,600 reads of 201 MB on the main thread to decode 214 MB (a block
    /// for each object would decode 762 MB; docs/research/heap-reduction-designs.md §1.4).
    static class BlockCache
    {
        public const int Capacity = 32 << 20;
        public const int IdleSeconds = 10;
        /// Buffers of evicted blocks kept for the next misses, of each size.
        const int SparesPerSize = 4;

        sealed class Entry(ArchiveBlocks archive, int block, byte[] data)
        {
            public readonly ArchiveBlocks Archive = archive;
            public readonly int Block = block;
            public readonly byte[] Data = data;
            public LinkedListNode<Entry>? Node;
        }

        static readonly object gate = new();
        static readonly Dictionary<(ArchiveBlocks, int), Entry> entries = new();
        /// Most recently used first.
        static readonly LinkedList<Entry> order = new();
        static readonly Dictionary<int, Stack<byte[]>> spares = new();
        static long held;
        static long lastUse;
        static Timer? trim;

        /// Copies part of a cached block out (under the lock, so an evicted buffer can be reused
        /// for the next block at once); false when the block is not cached.
        public static bool TryCopy(ArchiveBlocks archive, int block, int within, Span<byte> destination)
        {
            lock (gate)
            {
                if (!entries.TryGetValue((archive, block), out var entry)) return false;
                order.Remove(entry.Node!);
                order.AddFirst(entry.Node!);
                lastUse = Environment.TickCount64;
                entry.Data.AsSpan(within, destination.Length).CopyTo(destination);
                return true;
            }
        }

        /// A buffer for a block of `length` bytes: its size the next power of two.
        public static byte[] Rent(int length)
        {
            int size = (int)Math.Max(4096, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)length));
            lock (gate)
                if (spares.TryGetValue(size, out var stack) && stack.Count > 0) return stack.Pop();
            return new byte[size];
        }

        /// Copies part of a block just decoded into `buffer` out, and caches it.
        public static void Add(ArchiveBlocks archive, int block, byte[] buffer, int within, Span<byte> destination)
        {
            lock (gate)
            {
                buffer.AsSpan(within, destination.Length).CopyTo(destination);
                lastUse = Environment.TickCount64;
                if (entries.ContainsKey((archive, block)))
                {
                    // Another reader cached it meanwhile.
                    Spare(buffer);
                    return;
                }
                var entry = new Entry(archive, block, buffer);
                entry.Node = order.AddFirst(entry);
                entries.Add((archive, block), entry);
                held += buffer.Length;
                while (held > Capacity && order.Last is { } last)
                {
                    order.RemoveLast();
                    entries.Remove((last.Value.Archive, last.Value.Block));
                    held -= last.Value.Data.Length;
                    Spare(last.Value.Data);
                }
                trim ??= new Timer(_ => Trim(), null, IdleSeconds * 1000, IdleSeconds * 1000);
            }
        }

        static void Spare(byte[] buffer)
        {
            if (!spares.TryGetValue(buffer.Length, out var stack)) spares[buffer.Length] = stack = new Stack<byte[]>();
            if (stack.Count < SparesPerSize) stack.Push(buffer);
        }

        /// Drops every block once none has been used for IdleSeconds (after the start-up's and a
        /// load's reads; a later read decodes again).
        internal static void Trim(bool force = false)
        {
            lock (gate)
            {
                if (entries.Count == 0 && spares.Count == 0) return;
                if (!force && Environment.TickCount64 - lastUse < IdleSeconds * 1000) return;
                entries.Clear();
                order.Clear();
                spares.Clear();
                held = 0;
            }
        }

        /// The bytes the cache holds (tests).
        public static long Held
        {
            get { lock (gate) return held; }
        }
    }

    /// What bundles read block by block have done (ArchiveBlocks): reads, decodes, cache hits and
    /// misses, and in verify mode (ONI_BUNDLE_BLOCKS_VERIFY=1) the reads compared with the whole
    /// decode. Logs a summary every 10 s while the counts change.
    public static class BundleBlockStats
    {
        static long reads, readBytes, readTicks, decodes, decodedBytes, decodeTicks, hits, misses, verified, verifiedBytes, mismatches;
        static long mainReads, mainReadBytes, mainReadTicks;
        static Timer? timer;
        static long lastReported = -1;

        /// The engine's log for verify mode's lines (null: verify mode is off).
        public static Action<string>? Log { get; private set; }
        /// The main thread, whose reads are counted apart (they hold up a frame).
        public static int MainThreadId = -1;

        /// Verify mode: report to `log`.
        public static void StartVerify(Action<string> log)
        {
            Log = log;
            timer ??= new Timer(_ => Report(force: false), null, 10_000, 10_000);
        }

        internal static void Read(int bytes, long ticks)
        {
            Interlocked.Increment(ref reads);
            Interlocked.Add(ref readBytes, bytes);
            Interlocked.Add(ref readTicks, ticks);
            if (Environment.CurrentManagedThreadId == MainThreadId)
            {
                Interlocked.Increment(ref mainReads);
                Interlocked.Add(ref mainReadBytes, bytes);
                Interlocked.Add(ref mainReadTicks, ticks);
            }
        }

        internal static void Decoded(int bytes, long ticks)
        {
            Interlocked.Increment(ref decodes);
            Interlocked.Add(ref decodedBytes, bytes);
            Interlocked.Add(ref decodeTicks, ticks);
        }

        internal static void Hit() => Interlocked.Increment(ref hits);
        internal static void Miss() => Interlocked.Increment(ref misses);

        internal static void Verified(int bytes, bool same)
        {
            Interlocked.Increment(ref verified);
            Interlocked.Add(ref verifiedBytes, bytes);
            if (!same) Interlocked.Increment(ref mismatches);
        }

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// The counts so far, in one line.
        public static string Summary() =>
            $"{Interlocked.Read(ref reads)} reads ({Interlocked.Read(ref readBytes) / 1e6:F1} MB, {Ms(Interlocked.Read(ref readTicks)):F0} ms; "
            + $"main thread {Interlocked.Read(ref mainReads)}, {Interlocked.Read(ref mainReadBytes) / 1e6:F1} MB, {Ms(Interlocked.Read(ref mainReadTicks)):F0} ms), "
            + $"{Interlocked.Read(ref decodes)} blocks decoded ({Interlocked.Read(ref decodedBytes) / 1e6:F1} MB, {Ms(Interlocked.Read(ref decodeTicks)):F0} ms), "
            + $"cache {Interlocked.Read(ref hits)} hits, {Interlocked.Read(ref misses)} misses";

        /// Verify mode's summary (also every 10 s while reads are made).
        public static void Report(bool force = true)
        {
            var log = Log;
            if (log == null) return;
            long now = Interlocked.Read(ref verified);
            if (!force && Interlocked.Exchange(ref lastReported, now) == now) return;
            log($"bundle blocks: {now} reads verified ({Interlocked.Read(ref verifiedBytes) / 1e6:F1} MB), {Interlocked.Read(ref mismatches)} mismatches; {Summary()}");
        }
    }
}
