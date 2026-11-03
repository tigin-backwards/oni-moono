using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OniMoono.Engine
{
    /// The PNGs that ImageConversion.LoadImage decodes itself instead of through stb_image: 8 bits
    /// a channel, not interlaced, grey, grey and alpha, RGB or RGBA, without a palette or a tRNS
    /// chunk (the game's colony previews and timelapse images, and most PNGs in mods). The
    /// pixels are stb's for such a file, as PNG is lossless: zlib-inflated with .NET's zlib, the
    /// rows unfiltered, then written straight into the texture's ARGB32 rows, bottom to top.
    ///
    /// Anything else, and anything about the file that stb might take differently (a chunk it
    /// rejects, a zlib stream .NET's zlib rejects, such as a wrong Adler-32, which stb does not
    /// check), is left to stb: TryParse or TryDecode says false and the caller decodes the data
    /// with stb as before. The rules stb applies to the file's structure are kept here (IHDR
    /// first and once, no unknown critical chunk, chunks within the data, the image's exact
    /// size of inflated data, filter types 0-4).
    internal static class PngDecode
    {
        /// What TryParse found: the image's size and layout, and its IDAT chunks' data.
        internal readonly struct Image
        {
            public readonly int Width, Height, Channels;
            public readonly (int Offset, int Length)[] Data;

            public Image(int width, int height, int channels, (int, int)[] data)
            {
                Width = width;
                Height = height;
                Channels = channels;
                Data = data;
            }
        }

        /// stb's limit on either side (STBI_MAX_DIMENSIONS).
        const int MaxSide = 1 << 24;

        /// A PNG this decoder takes; false for any other data.
        public static bool TryParse(byte[] data, out Image image)
        {
            image = default;
            ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
            if (data.Length < 8 + 25 || !data.AsSpan(0, 8).SequenceEqual(signature)) return false;
            int pos = 8, width = 0, height = 0, channels = 0;
            bool first = true;
            var idat = new List<(int, int)>();
            while (true)
            {
                if (pos + 8 > data.Length) return false;
                uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
                uint type = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4));
                if (length > 1u << 30) return false;
                int body = pos + 8;
                if (type == Type("IEND"))
                {
                    // stb stops here, without reading the chunk's CRC.
                    if (first || length != 0 || idat.Count == 0) return false;
                    break;
                }
                // The chunk and its CRC must be in the data (stb reads past the end as zeros,
                // and fails on what follows).
                if ((long)body + length + 4 > data.Length) return false;
                if (first)
                {
                    if (type != Type("IHDR") || length != 13) return false;
                    width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(body));
                    height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(body + 4));
                    byte depth = data[body + 8], color = data[body + 9];
                    if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide) return false;
                    if (depth != 8 || data[body + 10] != 0 || data[body + 11] != 0 || data[body + 12] != 0) return false;
                    channels = color switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
                    if (channels == 0) return false;
                    // stb's "too large" (a raw image over 2^30 bytes), and ours: the inflated rows
                    // must fit an array.
                    if ((1 << 30) / width / channels < height) return false;
                    first = false;
                }
                else if (type == Type("IDAT"))
                {
                    if (length > 0) idat.Add((body, (int)length));
                }
                // tRNS (the one ancillary chunk stb reads), IHDR again, PLTE, CgBI and other
                // critical chunks: stb's to judge.
                else if (type == Type("tRNS") || (data[pos + 4] & 0x20) == 0) return false;
                pos = body + (int)length + 4;
            }
            image = new Image(width, height, channels, idat.ToArray());
            return true;
        }

        static uint Type(string name) => (uint)(name[0] << 24 | name[1] << 16 | name[2] << 8 | name[3]);

        /// The image as ARGB32 texels, rows bottom to top, into `argb` (Width x Height x 4 bytes).
        /// False when the zlib stream or a row's filter is not what stb would decode the same way
        /// (`argb` may then have been written).
        public static bool TryDecode(byte[] data, in Image image, Span<byte> argb)
        {
            int w = image.Width, h = image.Height, bpp = image.Channels, stride = w * bpp;
            long rawLength = (long)h * (stride + 1);
            if (argb.Length < (long)w * h * 4 || rawLength > int.MaxValue) return false;
            var raw = ArrayPool<byte>.Shared.Rent((int)rawLength);
            try
            {
                if (!Inflate(data, image.Data, raw.AsSpan(0, (int)rawLength))) return false;
                var dst = MemoryMarshal.Cast<byte, uint>(argb);
                for (int y = 0; y < h; y++)
                {
                    var row = raw.AsSpan(y * (stride + 1), stride + 1);
                    var current = row.Slice(1);
                    var previous = y == 0 ? default : raw.AsSpan((y - 1) * (stride + 1) + 1, stride);
                    if (!Unfilter(row[0], current, previous, bpp)) return false;
                    Convert(current, dst.Slice((h - 1 - y) * w, w), bpp);
                }
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
        }

        /// The IDAT data's zlib stream, inflated: exactly `raw.Length` bytes (stb fails on more or
        /// fewer).
        internal static bool Inflate(byte[] data, (int Offset, int Length)[] chunks, Span<byte> raw)
        {
            try
            {
                using var z = new ZLibStream(new Chunks(data, chunks), CompressionMode.Decompress);
                int filled = 0;
                while (filled < raw.Length)
                {
                    int n = z.Read(raw.Slice(filled));
                    if (n == 0) return false;
                    filled += n;
                }
                Span<byte> more = stackalloc byte[1];
                return z.Read(more) == 0;
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                // A stream zlib rejects (ZLibException is an IOException): stb's to judge.
                return false;
            }
        }

        /// A row unfiltered in place; `previous` is empty for the first row (whose filters see a
        /// row of zeros above, as stb's first-row filters do).
        static bool Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
        {
            int n = row.Length;
            switch (filter)
            {
                case 0:
                    return true;
                case 1:
                    if (bpp == 4) Sub4(row);
                    else for (int i = bpp; i < n; i++) row[i] += row[i - bpp];
                    return true;
                case 2:
                    if (previous.IsEmpty) return true;
                    Up(row, previous);
                    return true;
                case 3:
                    if (previous.IsEmpty)
                    {
                        for (int i = bpp; i < n; i++) row[i] += (byte)(row[i - bpp] >> 1);
                        return true;
                    }
                    if (bpp == 4)
                    {
                        Average4(row, previous);
                        return true;
                    }
                    for (int i = 0; i < bpp; i++) row[i] += (byte)(previous[i] >> 1);
                    for (int i = bpp; i < n; i++) row[i] += (byte)((row[i - bpp] + previous[i]) >> 1);
                    return true;
                case 4:
                    if (previous.IsEmpty)
                    {
                        // Paeth of (left, 0, 0) is left.
                        if (bpp == 4) Sub4(row);
                        else for (int i = bpp; i < n; i++) row[i] += row[i - bpp];
                        return true;
                    }
                    if (bpp == 4 && Vector128.IsHardwareAccelerated) Paeth4(row, previous);
                    else Paeth(row, previous, bpp);
                    return true;
                default:
                    return false;
            }
        }

        static void Up(Span<byte> row, ReadOnlySpan<byte> previous)
        {
            int i = 0, n = row.Length;
            if (Vector128.IsHardwareAccelerated)
                for (; i + 16 <= n; i += 16)
                    (Vector128.Create(row.Slice(i, 16)) + Vector128.Create(previous.Slice(i, 16))).CopyTo(row.Slice(i, 16));
            for (; i < n; i++) row[i] += previous[i];
        }

        // Four-channel rows a pixel at a time: the bytes of a pixel added without carries between
        // them (each byte's sum mod 256, as the byte loops give).
        static uint AddBytes(uint x, uint y) => ((x & 0x7F7F7F7Fu) + (y & 0x7F7F7F7Fu)) ^ ((x ^ y) & 0x80808080u);

        static void Sub4(Span<byte> row)
        {
            var px = MemoryMarshal.Cast<byte, uint>(row);
            uint left = 0;
            for (int x = 0; x < px.Length; x++) px[x] = left = AddBytes(px[x], left);
        }

        static void Average4(Span<byte> row, ReadOnlySpan<byte> previous)
        {
            var px = MemoryMarshal.Cast<byte, uint>(row);
            var up = MemoryMarshal.Cast<byte, uint>(previous);
            uint left = 0;
            for (int x = 0; x < px.Length; x++)
            {
                uint b = up[x];
                // Each byte's (left + up) >> 1, without carries between them.
                uint average = (left & b) + (((left ^ b) & 0xFEFEFEFEu) >> 1);
                px[x] = left = AddBytes(px[x], average);
            }
        }

        /// Paeth on four-channel rows, the channels of a pixel at once (16-bit lanes).
        static void Paeth4(Span<byte> row, ReadOnlySpan<byte> previous)
        {
            int w = row.Length / 4;
            ref byte r = ref MemoryMarshal.GetReference(row);
            ref byte p = ref MemoryMarshal.GetReference(previous);
            Vector128<short> a = Vector128<short>.Zero, c = Vector128<short>.Zero, mask = Vector128.Create((short)0xFF);
            for (int x = 0; x < w; x++)
            {
                var b = Widen(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, x * 4)));
                var upMinusCorner = b - c;
                var leftMinusCorner = a - c;
                var pa = Vector128.Abs(upMinusCorner);
                var pb = Vector128.Abs(leftMinusCorner);
                var pc = Vector128.Abs(upMinusCorner + leftMinusCorner);
                var useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
                var useB = Vector128.LessThanOrEqual(pb, pc);
                var predictor = Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c));
                ref byte at = ref Unsafe.Add(ref r, x * 4);
                var value = (Widen(Unsafe.ReadUnaligned<uint>(ref at)) + predictor) & mask;
                Unsafe.WriteUnaligned(ref at, Vector128.Narrow(value.AsUInt16(), value.AsUInt16()).AsUInt32().ToScalar());
                a = value;
                c = b;
            }
        }

        static Vector128<short> Widen(uint pixel) => Vector128.WidenLower(Vector128.CreateScalarUnsafe(pixel).AsByte()).AsInt16();

        static void Paeth(Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
        {
            int n = row.Length;
            for (int i = 0; i < bpp; i++) row[i] += previous[i];
            ref byte r = ref MemoryMarshal.GetReference(row);
            ref byte p = ref MemoryMarshal.GetReference(previous);
            for (int i = bpp; i < n; i++)
            {
                int a = Unsafe.Add(ref r, i - bpp), b = Unsafe.Add(ref p, i), c = Unsafe.Add(ref p, i - bpp);
                int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                int predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                Unsafe.Add(ref r, i) += (byte)predictor;
            }
        }

        /// A row of 1-4 channels as ARGB32 texels (A, R, G, B in memory), as stb expands them to
        /// four: grey to R = G = B, alpha 255 where the image has none.
        static void Convert(ReadOnlySpan<byte> row, Span<uint> texels, int bpp)
        {
            int w = texels.Length;
            switch (bpp)
            {
                case 4:
                {
                    var rgba = MemoryMarshal.Cast<byte, uint>(row);
                    int x = 0;
                    if (Vector128.IsHardwareAccelerated)
                        for (; x + 4 <= w; x += 4)
                        {
                            var v = Vector128.Create(rgba.Slice(x, 4));
                            (Vector128.ShiftLeft(v, 8) | Vector128.ShiftRightLogical(v, 24)).CopyTo(texels.Slice(x, 4));
                        }
                    for (; x < w; x++) texels[x] = BitOperations.RotateLeft(rgba[x], 8);
                    break;
                }
                case 3:
                    for (int x = 0; x < w; x++)
                        texels[x] = 0xFFu | (uint)row[x * 3] << 8 | (uint)row[x * 3 + 1] << 16 | (uint)row[x * 3 + 2] << 24;
                    break;
                case 2:
                    for (int x = 0; x < w; x++)
                    {
                        uint g = row[x * 2];
                        texels[x] = row[x * 2 + 1] | g << 8 | g << 16 | g << 24;
                    }
                    break;
                default:
                    for (int x = 0; x < w; x++)
                    {
                        uint g = row[x];
                        texels[x] = 0xFFu | g << 8 | g << 16 | g << 24;
                    }
                    break;
            }
        }

        /// The IDAT chunks' data as one stream, read in place.
        sealed class Chunks : Stream
        {
            readonly byte[] data;
            readonly (int Offset, int Length)[] chunks;
            int chunk, at;

            public Chunks(byte[] data, (int, int)[] chunks)
            {
                this.data = data;
                this.chunks = chunks;
            }

            public override int Read(Span<byte> buffer)
            {
                while (chunk < chunks.Length && at == chunks[chunk].Length)
                {
                    chunk++;
                    at = 0;
                }
                if (chunk == chunks.Length) return 0;
                int n = Math.Min(buffer.Length, chunks[chunk].Length - at);
                data.AsSpan(chunks[chunk].Offset + at, n).CopyTo(buffer);
                at += n;
                return n;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
