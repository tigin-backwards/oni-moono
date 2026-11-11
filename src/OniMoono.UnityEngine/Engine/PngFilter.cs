using System;

namespace OniMoono.Engine
{
    /// PNG's row filters (None, Sub, Up, Average, Paeth) and libpng's choice between them: the
    /// filter whose output bytes, taken as signed, have the smallest sum of absolute values; the
    /// first of equal ones.
    internal static unsafe class PngFilter
    {
        /// Filters `row` (rowBytes bytes, `bpp` bytes a pixel) against the row above it
        /// (`prior`, zeros above the image) into `output`: the filter type byte, then the row.
        public static void Choose(byte* row, byte* prior, byte* output, int rowBytes, int bpp)
        {
            ulong none = 0, sub = 0, up = 0, avg = 0, paeth = 0;
            for (int i = 0; i < rowBytes; i++)
            {
                int x = row[i], b = prior[i];
                int a = i >= bpp ? row[i - bpp] : 0, c = i >= bpp ? prior[i - bpp] : 0;
                none += Cost(x);
                sub += Cost(x - a);
                up += Cost(x - b);
                avg += Cost(x - ((a + b) >> 1));
                paeth += Cost(x - Paeth(a, b, c));
            }
            int type = 0;
            ulong best = none;
            if (sub < best) { best = sub; type = 1; }
            if (up < best) { best = up; type = 2; }
            if (avg < best) { best = avg; type = 3; }
            if (paeth < best) type = 4;
            Apply(type, row, prior, output, rowBytes, bpp);
        }

        /// Writes the row with filter `type`.
        public static void Apply(int type, byte* row, byte* prior, byte* output, int rowBytes, int bpp)
        {
            output[0] = (byte)type;
            var o = output + 1;
            switch (type)
            {
                case 0:
                    Buffer.MemoryCopy(row, o, rowBytes, rowBytes);
                    break;
                case 1:
                    for (int i = 0; i < rowBytes; i++) o[i] = (byte)(row[i] - (i >= bpp ? row[i - bpp] : 0));
                    break;
                case 2:
                    for (int i = 0; i < rowBytes; i++) o[i] = (byte)(row[i] - prior[i]);
                    break;
                case 3:
                    for (int i = 0; i < rowBytes; i++) o[i] = (byte)(row[i] - (((i >= bpp ? row[i - bpp] : 0) + prior[i]) >> 1));
                    break;
                default:
                    for (int i = 0; i < rowBytes; i++)
                        o[i] = (byte)(row[i] - Paeth(i >= bpp ? row[i - bpp] : 0, prior[i], i >= bpp ? prior[i - bpp] : 0));
                    break;
            }
        }

        /// A filtered byte's weight: its value taken as signed, made positive.
        static uint Cost(int difference)
        {
            int v = (sbyte)(byte)difference;
            return (uint)(v < 0 ? -v : v);
        }

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    /// zlib's Adler-32, and the combination of two parts' checksums.
    internal static class Adler32
    {
        const uint Base = 65521;
        // The most bytes before the sums must be reduced (zlib's NMAX).
        const int Max = 5552;

        public static uint Compute(ReadOnlySpan<byte> data, uint adler = 1)
        {
            uint a = adler & 0xFFFF, b = adler >> 16;
            while (data.Length > 0)
            {
                int n = Math.Min(Max, data.Length);
                foreach (byte v in data.Slice(0, n))
                {
                    a += v;
                    b += a;
                }
                a %= Base;
                b %= Base;
                data = data.Slice(n);
            }
            return b << 16 | a;
        }

        /// The checksum of two parts one after the other, from each part's and the second's
        /// length (zlib's adler32_combine).
        public static uint Combine(uint first, uint second, long secondLength)
        {
            ulong rem = (ulong)(secondLength % Base);
            ulong sum1 = first & 0xFFFF;
            ulong sum2 = rem * sum1 % Base;
            sum1 += (second & 0xFFFF) + Base - 1;
            sum2 += ((first >> 16) & 0xFFFF) + ((second >> 16) & 0xFFFF) + Base - rem;
            if (sum1 >= Base) sum1 -= Base;
            if (sum1 >= Base) sum1 -= Base;
            if (sum2 >= (ulong)Base << 1) sum2 -= (ulong)Base << 1;
            if (sum2 >= Base) sum2 -= Base;
            return (uint)(sum1 | sum2 << 16);
        }
    }
}
