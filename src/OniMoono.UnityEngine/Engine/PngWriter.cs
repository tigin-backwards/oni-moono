using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OniMoono.Engine
{
    /// A PNG written row by row: 8 bits a channel, greyscale (1 channel), RGB (3) or RGBA (4).
    /// Each row gets the filter whose output has the smallest sum of absolute values (as bytes
    /// taken signed: libpng's heuristic), and the filtered rows are deflated (level 9 unless
    /// asked otherwise) with zlib's filtered strategy. The rows are cut into bands of about 2 MB;
    /// each band is filtered and deflated on its own (in parallel when there are several), ends
    /// in a sync flush, and becomes one IDAT chunk, so the zlib stream is the bands' output one
    /// after the other (its Adler-32 in a last IDAT of 4 bytes). Where the bands fall depends
    /// only on the image's width and channels, so the same rows always give the same bytes. Any
    /// PNG reader reads the file; the bands restart deflate's window, which costs 0.02 % in size
    /// (a 5120x2880 capture of a colony).
    internal sealed unsafe class PngWriter : IDisposable
    {
        /// Rows a band takes: about this many filtered bytes.
        internal const int BandBytes = 2 << 20;
        /// ImageConversion's level: the smallest files (5 to 9 % smaller than level 6's on the
        /// game's images, for 3 to 4 times the time, spent on other threads in U60's flow).
        internal const int SmallestLevel = 9;

        /// Bands being encoded at once, at most: half the cores, so that the game's own threads
        /// keep theirs, and at most 8.
        static readonly int MaxInFlight = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

        readonly Stream output;
        readonly int level;
        readonly int channels, inputChannels, rowBytes, inputRowBytes, height, bandRows;
        // The band being filled: its rows as given (output channels), and the row before it.
        Band filling;
        byte* lastRow;
        readonly Queue<Task<Chunk>> inFlight = new();
        readonly List<Chunk> kept = new();
        long keptBytes;
        int rows, bands;
        uint adler = 1;
        bool finished, disposed;

        /// A PNG of `channels` channels written into `output` as its bands are done (null: kept
        /// in memory, for ToArray). `inputChannels` 4 with `channels` 3 drops each row's alpha.
        public PngWriter(Stream output, int width, int height, int channels, int inputChannels = 0, int level = SmallestLevel)
        {
            if (level is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(level));
            this.level = level;
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "A PNG has at least one row and one column.");
            if (channels is not (1 or 3 or 4)) throw new ArgumentOutOfRangeException(nameof(channels));
            inputChannels = inputChannels == 0 ? channels : inputChannels;
            if (inputChannels != channels && !(inputChannels == 4 && channels == 3)) throw new ArgumentOutOfRangeException(nameof(inputChannels));
            this.output = output;
            this.channels = channels;
            this.inputChannels = inputChannels;
            this.height = height;
            rowBytes = checked(width * channels);
            inputRowBytes = checked(width * inputChannels);
            bandRows = Math.Clamp(BandBytes / (rowBytes + 1), 1, height);
            lastRow = (byte*)NativeMemory.AllocZeroed((nuint)rowBytes);
            Span<byte> head = stackalloc byte[8 + 25];
            Signature.CopyTo(head);
            ChunkHeader(head.Slice(8), 13, "IHDR"u8);
            var ihdr = head.Slice(16, 13);
            BigEndian(ihdr, width);
            BigEndian(ihdr.Slice(4), height);
            ihdr[8] = 8;
            ihdr[9] = channels switch { 1 => 0, 3 => 2, _ => 6 };
            ihdr[10] = ihdr[11] = ihdr[12] = 0;
            BigEndian(head.Slice(29), (int)Crc32.Compute(head.Slice(12, 17)));
            Emit(Chunk.Copy(head));
        }

        public static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

        /// The next row, top first: width x the input's channels bytes.
        public void WriteRow(ReadOnlySpan<byte> row)
        {
            if (finished) throw new InvalidOperationException("The PNG is finished.");
            if (row.Length != inputRowBytes) throw new ArgumentException($"A row is {inputRowBytes} bytes.", nameof(row));
            if (rows >= height) throw new InvalidOperationException("The image has all its rows.");
            if (filling == null)
            {
                // The band owns the row before it from here.
                filling = new Band(Math.Min(bandRows, height - rows), rowBytes, channels, level, lastRow);
                lastRow = null;
            }
            var into = new Span<byte>(filling.Raw + (long)filling.Count * rowBytes, rowBytes);
            if (inputChannels == channels) row.CopyTo(into);
            else
                for (int i = 0, o = 0; o < rowBytes; i += 4, o += 3)
                {
                    into[o] = row[i];
                    into[o + 1] = row[i + 1];
                    into[o + 2] = row[i + 2];
                }
            filling.Count++;
            rows++;
            if (filling.Count == filling.Rows) Dispatch();
        }

        /// Ends the image (every row given) and the file.
        public void Finish()
        {
            if (finished) throw new InvalidOperationException("The PNG is finished.");
            if (rows != height) throw new InvalidOperationException($"{height - rows} row(s) missing.");
            finished = true;
            while (inFlight.Count > 0) Collect();
            Span<byte> tail = stackalloc byte[16 + 12];
            ChunkHeader(tail, 4, "IDAT"u8);
            BigEndian(tail.Slice(8), (int)adler);
            BigEndian(tail.Slice(12), (int)Crc32.Compute(tail.Slice(4, 8)));
            ChunkHeader(tail.Slice(16), 0, "IEND"u8);
            BigEndian(tail.Slice(24), unchecked((int)0xAE426082));
            Emit(Chunk.Copy(tail));
            output?.Flush();
        }

        /// The file, once finished, when it was kept in memory.
        public byte[] ToArray()
        {
            if (!finished || output != null) throw new InvalidOperationException("Only a finished PNG kept in memory has its bytes.");
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)keptBytes));
            int at = 0;
            foreach (var chunk in kept)
            {
                chunk.Span.CopyTo(bytes.AsSpan(at));
                at += chunk.Length;
                chunk.Free();
            }
            kept.Clear();
            return bytes;
        }

        /// Encodes the full band: on another thread when bands are many, here when there is one.
        void Dispatch()
        {
            var band = filling;
            filling = null;
            // The next band filters its first row against this band's last.
            var next = (byte*)NativeMemory.Alloc((nuint)rowBytes);
            Buffer.MemoryCopy(band.Raw + (long)(band.Rows - 1) * rowBytes, next, rowBytes, rowBytes);
            lastRow = next;
            bool first = bands++ == 0, last = rows == height;
            if (first && last) inFlight.Enqueue(Task.FromResult(band.Encode(true, true)));
            else
            {
                while (inFlight.Count >= MaxInFlight) Collect();
                inFlight.Enqueue(Task.Run(() => band.Encode(first, last)));
            }
        }

        /// Writes out the oldest band once it is done.
        void Collect()
        {
            var chunk = inFlight.Dequeue().GetAwaiter().GetResult();
            adler = Adler32.Combine(adler, chunk.Adler, chunk.FilteredBytes);
            Emit(chunk);
        }

        void Emit(Chunk chunk)
        {
            if (output == null)
            {
                kept.Add(chunk);
                keptBytes += chunk.Length;
                return;
            }
            try { output.Write(chunk.Span); }
            finally { chunk.Free(); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var task in inFlight)
                try { task.GetAwaiter().GetResult().Free(); }
                catch (Exception) { }
            inFlight.Clear();
            foreach (var chunk in kept) chunk.Free();
            kept.Clear();
            filling?.Free();
            filling = null;
            if (lastRow != null) NativeMemory.Free(lastRow);
            lastRow = null;
        }

        internal static void ChunkHeader(Span<byte> at, int length, ReadOnlySpan<byte> type)
        {
            BigEndian(at, length);
            type.CopyTo(at.Slice(4));
        }

        internal static void BigEndian(Span<byte> b, int value)
        {
            b[0] = (byte)(value >> 24);
            b[1] = (byte)(value >> 16);
            b[2] = (byte)(value >> 8);
            b[3] = (byte)value;
        }

        /// A band's rows, unfiltered, and the row before them (zeros above the image). Owns its
        /// memory, `prior` included, until encoded.
        sealed class Band(int rows, int rowBytes, int channels, int level, byte* prior)
        {
            public readonly int Rows = rows;
            public int Count;
            public byte* Raw = (byte*)NativeMemory.Alloc((nuint)((long)rows * rowBytes));
            byte* prior = prior;

            /// The band as one IDAT chunk: the zlib header first in the image, then the filtered
            /// rows deflated, ending in a sync flush (the image's last band: in the final block).
            public Chunk Encode(bool first, bool last)
            {
                long filteredBytes = (long)Rows * (rowBytes + 1);
                var filtered = (byte*)NativeMemory.Alloc((nuint)filteredBytes);
                try
                {
                    for (int r = 0; r < Rows; r++)
                        PngFilter.Choose(Raw + (long)r * rowBytes, r == 0 ? prior : Raw + (long)(r - 1) * rowBytes, filtered + r * (long)(rowBytes + 1), rowBytes, channels);
                    var data = new ReadOnlySpan<byte>(filtered, checked((int)filteredBytes));
                    uint adler = Adler32.Compute(data);
                    var sink = new NativeSink(filteredBytes / 2 + 64);
                    sink.Advance(8);
                    if (first)
                    {
                        // CMF: deflate with a 32 KB window; FLG: the level's class, and the
                        // check bits that make the pair a multiple of 31.
                        int flevel = level >= 7 ? 3 : level == 6 ? 2 : level >= 2 ? 1 : 0;
                        int flg = flevel << 6;
                        flg += 31 - (0x78 << 8 | flg) % 31;
                        sink.WriteByte(0x78);
                        sink.WriteByte((byte)flg);
                    }
                    long flushed;
                    using (var deflate = new DeflateStream(sink, new ZLibCompressionOptions { CompressionLevel = level, CompressionStrategy = ZLibCompressionStrategy.Filtered }, leaveOpen: true))
                    {
                        deflate.Write(data);
                        deflate.Flush();
                        flushed = sink.Length;
                    }
                    // A band before the last ends with its sync flush, not the stream's end.
                    if (!last) sink.Truncate(flushed);
                    int length = checked((int)(sink.Length - 8));
                    var head = new Span<byte>(sink.Pointer, 8);
                    ChunkHeader(head, length, "IDAT"u8);
                    uint crc = Crc32.Compute(new ReadOnlySpan<byte>(sink.Pointer + 4, length + 4));
                    Span<byte> word = stackalloc byte[4];
                    BigEndian(word, (int)crc);
                    sink.Write(word);
                    return new Chunk(sink.Detach(), checked((int)sink.Length), adler, filteredBytes);
                }
                finally
                {
                    NativeMemory.Free(filtered);
                    Free();
                }
            }

            public void Free()
            {
                if (Raw != null) NativeMemory.Free(Raw);
                Raw = null;
                if (prior != null) NativeMemory.Free(prior);
                prior = null;
            }
        }

        /// Finished bytes of the file in native memory.
        sealed class Chunk(byte* pointer, int length, uint adler, long filteredBytes)
        {
            byte* pointer = pointer;
            public readonly int Length = length;
            public readonly uint Adler = adler;
            public readonly long FilteredBytes = filteredBytes;

            public ReadOnlySpan<byte> Span => new(pointer, Length);

            public static Chunk Copy(ReadOnlySpan<byte> bytes)
            {
                var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
                bytes.CopyTo(new Span<byte>(p, bytes.Length));
                return new Chunk(p, bytes.Length, 1, 0);
            }

            public void Free()
            {
                if (pointer != null) NativeMemory.Free(pointer);
                pointer = null;
            }
        }

        /// A write-only stream into native memory that grows as it is written.
        sealed class NativeSink(long capacity) : Stream
        {
            byte* data = (byte*)NativeMemory.Alloc((nuint)Math.Max(64, capacity));
            long capacity = Math.Max(64, capacity), length;

            public byte* Pointer => data;
            public override long Length => length;

            public void Advance(long bytes)
            {
                Reserve(bytes);
                length += bytes;
            }

            public void Truncate(long to) => length = to;

            public byte* Detach()
            {
                var p = data;
                data = null;
                return p;
            }

            void Reserve(long more)
            {
                if (length + more <= capacity) return;
                long grown = Math.Max(capacity * 2, length + more);
                data = (byte*)NativeMemory.Realloc(data, (nuint)grown);
                capacity = grown;
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Reserve(buffer.Length);
                buffer.CopyTo(new Span<byte>(data + length, buffer.Length));
                length += buffer.Length;
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void WriteByte(byte value)
            {
                Reserve(1);
                data[length++] = value;
            }

            protected override void Dispose(bool disposing)
            {
                if (data != null) NativeMemory.Free(data);
                data = null;
                base.Dispose(disposing);
            }

            public override void Flush() { }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Position { get => length; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
