using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace OniMoono.Engine.Video
{
    /// Converts decoded I420 (YUV 4:2:0) pictures to RGBA32 texels on the CPU.
    ///
    /// This is the CPU stand-in for the build's `Hidden/VideoDecode` shader (unity_builtin_extra),
    /// which Unity uses to turn the three plane textures of a VP8 frame into RGB. Its GLSL
    /// (YCbCr pass) is BT.601 limited-range with libyuv-style coefficients, read from the
    /// planes' alpha channels normalized to [0,1]:
    ///
    ///     R = 1.15625·Y + 1.59375·V              − 0.872539997
    ///     G = 1.15625·Y − 0.390625·U − 0.8125·V  + 0.531369984
    ///     B = 1.15625·Y + 1.984375·U             − 1.06861997      A = 1
    ///
    /// independent of the stream's colour metadata. The half-resolution chroma planes are
    /// sampled bilinearly at the full-resolution texel centres (weights ¾/¼ in each direction,
    /// clamped at the edges), and the result is rounded to 8 bits as a UNorm render target
    /// stores it. Arithmetic is fixed point (1/4096 of a level). Rows are written bottom-up, as
    /// Unity textures store them, so the picture's top row is the texture's last row.
    ///
    /// When the GL device can run the shader, the decoder can hand it the planes instead; this
    /// class is the only place that knows the formula.
    internal static unsafe class VideoFrameConverter
    {
        // Coefficients × 4096.
        const int Ky = 4736, KrV = 6528, KgU = 1600, KgV = 3328, KbU = 8128;
        // Offsets × 255 × 4096, rounded.
        const int OffR = 911352, OffG = 555003, OffB = 1116152;

        /// Converts a `width`×`height` I420 picture into `destination` (width·height·4 bytes).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void I420ToRgba32(byte* y, int strideY, byte* u, int strideU, byte* v, int strideV,
            int width, int height, byte* destination)
        {
            if (width <= 0 || height <= 0) return;
            int bands = Math.Clamp(Environment.ProcessorCount, 1, 8);
            if (height < 64) bands = 1;
            int rowsPerBand = (height + bands - 1) / bands;
            var planes = new Planes { Y = y, U = u, V = v, StrideY = strideY, StrideU = strideU, StrideV = strideV, Width = width, Height = height, Destination = destination };
            if (bands == 1)
            {
                ConvertRows(planes, 0, height);
                return;
            }
            Parallel.For(0, bands, band =>
            {
                int start = band * rowsPerBand, end = Math.Min(height, start + rowsPerBand);
                if (start < end) ConvertRows(planes, start, end);
            });
        }

        struct Planes
        {
            public byte* Y, U, V, Destination;
            public int StrideY, StrideU, StrideV, Width, Height;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ConvertRows(Planes p, int startRow, int endRow)
        {
            int chromaWidth = (p.Width + 1) >> 1, chromaHeight = (p.Height + 1) >> 1;
            // Chroma rows interpolated vertically, ×4.
            var vu = new int[chromaWidth];
            var vv = new int[chromaWidth];
            for (int row = startRow; row < endRow; row++)
            {
                // Texel centre (row + ½) in chroma texels is row/2 + ¼; the nearer chroma row gets ¾.
                int near = row >> 1;
                int far = (row & 1) == 0 ? Math.Max(near - 1, 0) : Math.Min(near + 1, chromaHeight - 1);
                byte* un = p.U + near * p.StrideU, uf = p.U + far * p.StrideU;
                byte* vn = p.V + near * p.StrideV, vf = p.V + far * p.StrideV;
                for (int i = 0; i < chromaWidth; i++)
                {
                    vu[i] = 3 * un[i] + uf[i];
                    vv[i] = 3 * vn[i] + vf[i];
                }
                byte* ySrc = p.Y + row * p.StrideY;
                byte* dst = p.Destination + (long)(p.Height - 1 - row) * p.Width * 4;
                for (int x = 0; x < p.Width; x++)
                {
                    int cn = x >> 1;
                    int cf = (x & 1) == 0 ? Math.Max(cn - 1, 0) : Math.Min(cn + 1, chromaWidth - 1);
                    // ×16 in total (4 vertically × 4 horizontally).
                    int u16 = 3 * vu[cn] + vu[cf] - 128 * 16;
                    int v16 = 3 * vv[cn] + vv[cf] - 128 * 16;
                    int y16 = ySrc[x] * 16;
                    // (Ky·Y + …)·16 − offset·16, rounded, then ÷(4096·16).
                    int luma = Ky * y16;
                    int r = luma + KrV * v16 - (OffR - KrV * 128) * 16;
                    int g = luma - KgU * u16 - KgV * v16 + (OffG - (KgU + KgV) * 128) * 16;
                    int b = luma + KbU * u16 - (OffB - KbU * 128) * 16;
                    dst[0] = Clamp(r);
                    dst[1] = Clamp(g);
                    dst[2] = Clamp(b);
                    dst[3] = 255;
                    dst += 4;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static byte Clamp(int value)
        {
            int v = (value + (1 << 15)) >> 16;
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
    }
}
