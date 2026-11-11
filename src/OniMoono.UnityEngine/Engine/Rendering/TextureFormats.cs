using System;
using System.Buffers.Binary;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace OniMoono.Engine.Rendering
{
    /// Memory layout and CPU pixel conversion of Unity's texture formats.
    ///
    /// Layout follows Unity's: mip levels are stored largest first, each tightly packed (no row
    /// padding); block-compressed formats store ceil(w/bw)·ceil(h/bh) blocks per level (PVRTC at
    /// least 2×2).
    internal static class TextureFormats
    {
        /// Size in bytes of one block and its pixel dimensions (1×1 for uncompressed formats).
        public static bool TryGetBlock(TextureFormat format, out int blockWidth, out int blockHeight, out int blockBytes)
        {
            blockWidth = blockHeight = 1;
            blockBytes = format switch
            {
                TextureFormat.Alpha8 or TextureFormat.R8 or TextureFormat.R8_SIGNED => 1,
                TextureFormat.ARGB4444 or TextureFormat.RGBA4444 or TextureFormat.RGB565 or TextureFormat.R16 or TextureFormat.RHalf
                    or TextureFormat.RG16 or TextureFormat.RG16_SIGNED or TextureFormat.R16_SIGNED or TextureFormat.YUY2 => 2,
                TextureFormat.RGB24 or TextureFormat.RGB24_SIGNED => 3,
                TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.BGRA32 or TextureFormat.RGHalf or TextureFormat.RFloat
                    or TextureFormat.RGB9e5Float or TextureFormat.RG32 or TextureFormat.RGBA32_SIGNED or TextureFormat.RG32_SIGNED => 4,
                TextureFormat.RGB48 or TextureFormat.RGB48_SIGNED => 6,
                TextureFormat.RGBAHalf or TextureFormat.RGFloat or TextureFormat.RGBA64 or TextureFormat.RGBA64_SIGNED => 8,
                TextureFormat.RGBAFloat => 16,
                _ => 0,
            };
            if (blockBytes != 0) return true;
            blockWidth = blockHeight = 4;
            blockBytes = format switch
            {
                TextureFormat.DXT1 or TextureFormat.BC4 or TextureFormat.ETC_RGB4 or TextureFormat.ETC2_RGB or TextureFormat.ETC2_RGBA1
                    or TextureFormat.EAC_R or TextureFormat.EAC_R_SIGNED => 8,
                TextureFormat.DXT5 or TextureFormat.BC5 or TextureFormat.BC6H or TextureFormat.BC7 or TextureFormat.ETC2_RGBA8
                    or TextureFormat.EAC_RG or TextureFormat.EAC_RG_SIGNED => 16,
                _ => 0,
            };
            if (blockBytes != 0) return true;
            // PVRTC: 8-byte words of 8×4 (2 bits per texel) or 4×4 texels.
            if (IsPvrtc(format, out bool twoBpp))
            {
                blockWidth = twoBpp ? 8 : 4;
                blockHeight = 4;
                blockBytes = 8;
                return true;
            }
            // ASTC: 16-byte blocks of 4×4 to 12×12 texels.
            blockWidth = blockHeight = AstcBlockSize(format);
            blockBytes = blockWidth == 0 ? 0 : 16;
            return blockBytes != 0;
        }

        static bool IsPvrtc(TextureFormat format, out bool twoBpp)
        {
            twoBpp = format is TextureFormat.PVRTC_RGB2 or TextureFormat.PVRTC_RGBA2;
            return twoBpp || format is TextureFormat.PVRTC_RGB4 or TextureFormat.PVRTC_RGBA4;
        }

        static int AstcBlockSize(TextureFormat format) => format switch
        {
            TextureFormat.ASTC_4x4 or TextureFormat.ASTC_HDR_4x4 => 4,
            TextureFormat.ASTC_5x5 or TextureFormat.ASTC_HDR_5x5 => 5,
            TextureFormat.ASTC_6x6 or TextureFormat.ASTC_HDR_6x6 => 6,
            TextureFormat.ASTC_8x8 or TextureFormat.ASTC_HDR_8x8 => 8,
            TextureFormat.ASTC_10x10 or TextureFormat.ASTC_HDR_10x10 => 10,
            TextureFormat.ASTC_12x12 or TextureFormat.ASTC_HDR_12x12 => 12,
            _ => 0,
        };

        public static bool IsBlockCompressed(TextureFormat format) => TryGetBlock(format, out int bw, out _, out _) && bw > 1;

        /// Crunch-compressed formats are decompressed on load by Unity; this build ships none.
        public static bool IsCrunch(TextureFormat format)
            => format is TextureFormat.DXT1Crunched or TextureFormat.DXT5Crunched or TextureFormat.ETC_RGB4Crunched or TextureFormat.ETC2_RGBA8Crunched;

        static int Layout(TextureFormat format, out int bw, out int bh)
        {
            if (!TryGetBlock(format, out bw, out bh, out int bytes))
                throw new ArgumentException($"Texture format {format} has no memory layout in this engine.");
            return bytes;
        }

        /// Bytes of one level of the given size (a 0×0 texture has no data).
        public static long MipSize(TextureFormat format, int width, int height)
        {
            int bytes = Layout(format, out int bw, out int bh);
            if (width <= 0 || height <= 0) return 0;
            long bx = (width + bw - 1) / bw;
            long by = (height + bh - 1) / bh;
            // PVRTC levels hold at least 2×2 words.
            if (IsPvrtc(format, out _)) { bx = Math.Max(bx, 2); by = Math.Max(by, 2); }
            return bx * by * bytes;
        }

        /// Start of level `mip` in a texture of the given base size; levels shrink to 1×1.
        public static long MipOffset(TextureFormat format, int width, int height, int mip)
        {
            if (width <= 0 || height <= 0) return 0;
            long offset = 0;
            for (int m = 0; m < mip; m++) offset += MipSize(format, Math.Max(1, width >> m), Math.Max(1, height >> m));
            return offset;
        }

        public static long ImageSize(TextureFormat format, int width, int height, int mipCount)
            => MipOffset(format, width, height, mipCount);

        /// Number of levels in a full chain: down to 1×1.
        public static int FullMipCount(int width, int height, int depth = 1)
        {
            int size = Math.Max(Math.Max(width, height), depth);
            int count = 1;
            while (size > 1)
            {
                size >>= 1;
                count++;
            }
            return count;
        }

        /// mipCount as given to a texture constructor: -1 (GenerateAllMips) means the full chain.
        public static int ResolveMipCount(int requested, int width, int height, int depth = 1)
        {
            int full = FullMipCount(width, height, depth);
            return requested < 0 ? full : Math.Clamp(requested, 1, full);
        }

        // ----- CPU pixel access (GetPixel/SetPixel family) -----

        /// Formats whose texels can be read as Color.
        public static bool CanDecode(TextureFormat format) => format switch
        {
            TextureFormat.DXT1 or TextureFormat.DXT5 or TextureFormat.BC4 or TextureFormat.BC5 or TextureFormat.BC6H or TextureFormat.BC7
                or TextureFormat.ETC_RGB4 or TextureFormat.ETC2_RGB or TextureFormat.ETC2_RGBA1 or TextureFormat.ETC2_RGBA8
                or TextureFormat.EAC_R or TextureFormat.EAC_R_SIGNED or TextureFormat.EAC_RG or TextureFormat.EAC_RG_SIGNED
                or TextureFormat.ASTC_4x4 or TextureFormat.ASTC_5x5 or TextureFormat.ASTC_6x6 or TextureFormat.ASTC_8x8
                or TextureFormat.ASTC_10x10 or TextureFormat.ASTC_12x12
                or TextureFormat.ASTC_HDR_4x4 or TextureFormat.ASTC_HDR_5x5 or TextureFormat.ASTC_HDR_6x6 or TextureFormat.ASTC_HDR_8x8
                or TextureFormat.ASTC_HDR_10x10 or TextureFormat.ASTC_HDR_12x12
                or TextureFormat.PVRTC_RGB2 or TextureFormat.PVRTC_RGBA2 or TextureFormat.PVRTC_RGB4 or TextureFormat.PVRTC_RGBA4 => true,
            _ => CanEncode(format),
        };

        /// Formats SetPixel can write (uncompressed formats).
        public static bool CanEncode(TextureFormat format) => format switch
        {
            TextureFormat.Alpha8 or TextureFormat.R8 or TextureFormat.ARGB4444 or TextureFormat.RGBA4444 or TextureFormat.RGB565
                or TextureFormat.R16 or TextureFormat.RHalf or TextureFormat.RG16 or TextureFormat.RGB24 or TextureFormat.RGBA32
                or TextureFormat.ARGB32 or TextureFormat.BGRA32 or TextureFormat.RGHalf or TextureFormat.RFloat or TextureFormat.RG32
                or TextureFormat.RGB48 or TextureFormat.RGBAHalf or TextureFormat.RGFloat or TextureFormat.RGBA64 or TextureFormat.RGBAFloat => true,
            _ => false,
        };

        static float U8(byte b) => b / 255f;
        static byte ToU8(float f) => (byte)(Mathf.Clamp01(f) * 255f + 0.5f);
        static float U16(ushort v) => v / 65535f;
        static ushort ToU16(float f) => (ushort)(Mathf.Clamp01(f) * 65535f + 0.5f);
        static float U4(int v) => v / 15f;
        static int ToU4(float f) => (int)(Mathf.Clamp01(f) * 15f + 0.5f);

        /// Reads texel (x, y) of a mip level. Formats without alpha read alpha as 1; single-channel
        /// formats read (r, 0, 0, 1) like a shader samples them, except Alpha8, which reads as white
        /// with alpha.
        public static Color Decode(TextureFormat format, ReadOnlySpan<byte> mip, int mipWidth, int x, int y)
        {
            if (IsPvrtc(format, out bool twoBpp))
                return PvrtcDecoder.Decode(mip, mipWidth, x, y, twoBpp, hasAlpha: format is TextureFormat.PVRTC_RGBA2 or TextureFormat.PVRTC_RGBA4);
            if (IsBlockCompressed(format)) return DecodeBlockTexel(format, mip, mipWidth, x, y);
            TryGetBlock(format, out _, out _, out int bpp);
            var p = mip.Slice((y * mipWidth + x) * bpp, bpp);
            switch (format)
            {
                case TextureFormat.Alpha8: return new Color(1f, 1f, 1f, U8(p[0]));
                case TextureFormat.R8: return new Color(U8(p[0]), 0f, 0f, 1f);
                case TextureFormat.RG16: return new Color(U8(p[0]), U8(p[1]), 0f, 1f);
                case TextureFormat.RGB24: return new Color(U8(p[0]), U8(p[1]), U8(p[2]), 1f);
                case TextureFormat.RGBA32: return new Color(U8(p[0]), U8(p[1]), U8(p[2]), U8(p[3]));
                case TextureFormat.ARGB32: return new Color(U8(p[1]), U8(p[2]), U8(p[3]), U8(p[0]));
                case TextureFormat.BGRA32: return new Color(U8(p[2]), U8(p[1]), U8(p[0]), U8(p[3]));
                case TextureFormat.ARGB4444:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(p);
                    return new Color(U4((v >> 8) & 15), U4((v >> 4) & 15), U4(v & 15), U4(v >> 12));
                }
                case TextureFormat.RGBA4444:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(p);
                    return new Color(U4(v >> 12), U4((v >> 8) & 15), U4((v >> 4) & 15), U4(v & 15));
                }
                case TextureFormat.RGB565:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(p);
                    return new Color((v >> 11) / 31f, ((v >> 5) & 63) / 63f, (v & 31) / 31f, 1f);
                }
                case TextureFormat.R16: return new Color(U16(BinaryPrimitives.ReadUInt16LittleEndian(p)), 0f, 0f, 1f);
                case TextureFormat.RG32: return new Color(U16(BinaryPrimitives.ReadUInt16LittleEndian(p)), U16(BinaryPrimitives.ReadUInt16LittleEndian(p[2..])), 0f, 1f);
                case TextureFormat.RGB48:
                    return new Color(U16(BinaryPrimitives.ReadUInt16LittleEndian(p)), U16(BinaryPrimitives.ReadUInt16LittleEndian(p[2..])), U16(BinaryPrimitives.ReadUInt16LittleEndian(p[4..])), 1f);
                case TextureFormat.RGBA64:
                    return new Color(U16(BinaryPrimitives.ReadUInt16LittleEndian(p)), U16(BinaryPrimitives.ReadUInt16LittleEndian(p[2..])),
                        U16(BinaryPrimitives.ReadUInt16LittleEndian(p[4..])), U16(BinaryPrimitives.ReadUInt16LittleEndian(p[6..])));
                case TextureFormat.RHalf: return new Color(H(p, 0), 0f, 0f, 1f);
                case TextureFormat.RGHalf: return new Color(H(p, 0), H(p, 1), 0f, 1f);
                case TextureFormat.RGBAHalf: return new Color(H(p, 0), H(p, 1), H(p, 2), H(p, 3));
                case TextureFormat.RFloat: return new Color(F(p, 0), 0f, 0f, 1f);
                case TextureFormat.RGFloat: return new Color(F(p, 0), F(p, 1), 0f, 1f);
                case TextureFormat.RGBAFloat: return new Color(F(p, 0), F(p, 1), F(p, 2), F(p, 3));
            }
            throw UnsupportedFormat(format);
        }

        static float H(ReadOnlySpan<byte> p, int i) => (float)BinaryPrimitives.ReadHalfLittleEndian(p[(i * 2)..]);
        static float F(ReadOnlySpan<byte> p, int i) => BinaryPrimitives.ReadSingleLittleEndian(p[(i * 4)..]);

        /// The 8-bit formats StoreRgba8 writes: what Encode gives for colours read from 8-bit
        /// channels, without going through floats.
        public static bool ReadPixelsBytes(TextureFormat format)
            => format is TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.BGRA32 or TextureFormat.RGB24;

        /// RGBA 8-bit rows (`rows` of `width` texels, bottom first) into a level at (x, y), as
        /// Encode writes each texel's colour read from them.
        public static void StoreRgba8(TextureFormat format, Span<byte> mip, int mipWidth, int x, int y, ReadOnlySpan<byte> rgba, int width, int rows)
        {
            TryGetBlock(format, out _, out _, out int bpp);
            for (int r = 0; r < rows; r++)
            {
                var from = rgba.Slice(r * width * 4, width * 4);
                var to = mip.Slice(((y + r) * mipWidth + x) * bpp, width * bpp);
                switch (format)
                {
                    case TextureFormat.RGBA32:
                        from.CopyTo(to);
                        break;
                    case TextureFormat.ARGB32:
                        for (int i = 0; i < width; i++)
                        {
                            to[i * 4] = from[i * 4 + 3];
                            to[i * 4 + 1] = from[i * 4];
                            to[i * 4 + 2] = from[i * 4 + 1];
                            to[i * 4 + 3] = from[i * 4 + 2];
                        }
                        break;
                    case TextureFormat.BGRA32:
                        for (int i = 0; i < width; i++)
                        {
                            to[i * 4] = from[i * 4 + 2];
                            to[i * 4 + 1] = from[i * 4 + 1];
                            to[i * 4 + 2] = from[i * 4];
                            to[i * 4 + 3] = from[i * 4 + 3];
                        }
                        break;
                    case TextureFormat.RGB24:
                        for (int i = 0; i < width; i++)
                        {
                            to[i * 3] = from[i * 4];
                            to[i * 3 + 1] = from[i * 4 + 1];
                            to[i * 3 + 2] = from[i * 4 + 2];
                        }
                        break;
                    default:
                        throw new ArgumentException($"{format} is not an 8-bit format StoreRgba8 writes.", nameof(format));
                }
            }
        }

        /// Writes texel (x, y) of a mip level. 8/16-bit channels are clamped to [0, 1] and rounded.
        public static void Encode(TextureFormat format, Span<byte> mip, int mipWidth, int x, int y, Color c)
        {
            TryGetBlock(format, out _, out _, out int bpp);
            var p = mip.Slice((y * mipWidth + x) * bpp, bpp);
            switch (format)
            {
                case TextureFormat.Alpha8: p[0] = ToU8(c.a); return;
                case TextureFormat.R8: p[0] = ToU8(c.r); return;
                case TextureFormat.RG16: p[0] = ToU8(c.r); p[1] = ToU8(c.g); return;
                case TextureFormat.RGB24: p[0] = ToU8(c.r); p[1] = ToU8(c.g); p[2] = ToU8(c.b); return;
                case TextureFormat.RGBA32: p[0] = ToU8(c.r); p[1] = ToU8(c.g); p[2] = ToU8(c.b); p[3] = ToU8(c.a); return;
                case TextureFormat.ARGB32: p[0] = ToU8(c.a); p[1] = ToU8(c.r); p[2] = ToU8(c.g); p[3] = ToU8(c.b); return;
                case TextureFormat.BGRA32: p[0] = ToU8(c.b); p[1] = ToU8(c.g); p[2] = ToU8(c.r); p[3] = ToU8(c.a); return;
                case TextureFormat.ARGB4444:
                    BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)((ToU4(c.a) << 12) | (ToU4(c.r) << 8) | (ToU4(c.g) << 4) | ToU4(c.b)));
                    return;
                case TextureFormat.RGBA4444:
                    BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)((ToU4(c.r) << 12) | (ToU4(c.g) << 8) | (ToU4(c.b) << 4) | ToU4(c.a)));
                    return;
                case TextureFormat.RGB565:
                {
                    int r = (int)(Mathf.Clamp01(c.r) * 31f + 0.5f), g = (int)(Mathf.Clamp01(c.g) * 63f + 0.5f), b = (int)(Mathf.Clamp01(c.b) * 31f + 0.5f);
                    BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)((r << 11) | (g << 5) | b));
                    return;
                }
                case TextureFormat.R16: BinaryPrimitives.WriteUInt16LittleEndian(p, ToU16(c.r)); return;
                case TextureFormat.RG32:
                    BinaryPrimitives.WriteUInt16LittleEndian(p, ToU16(c.r));
                    BinaryPrimitives.WriteUInt16LittleEndian(p[2..], ToU16(c.g));
                    return;
                case TextureFormat.RGB48:
                    BinaryPrimitives.WriteUInt16LittleEndian(p, ToU16(c.r));
                    BinaryPrimitives.WriteUInt16LittleEndian(p[2..], ToU16(c.g));
                    BinaryPrimitives.WriteUInt16LittleEndian(p[4..], ToU16(c.b));
                    return;
                case TextureFormat.RGBA64:
                    BinaryPrimitives.WriteUInt16LittleEndian(p, ToU16(c.r));
                    BinaryPrimitives.WriteUInt16LittleEndian(p[2..], ToU16(c.g));
                    BinaryPrimitives.WriteUInt16LittleEndian(p[4..], ToU16(c.b));
                    BinaryPrimitives.WriteUInt16LittleEndian(p[6..], ToU16(c.a));
                    return;
                case TextureFormat.RHalf: WH(p, 0, c.r); return;
                case TextureFormat.RGHalf: WH(p, 0, c.r); WH(p, 1, c.g); return;
                case TextureFormat.RGBAHalf: WH(p, 0, c.r); WH(p, 1, c.g); WH(p, 2, c.b); WH(p, 3, c.a); return;
                case TextureFormat.RFloat: WF(p, 0, c.r); return;
                case TextureFormat.RGFloat: WF(p, 0, c.r); WF(p, 1, c.g); return;
                case TextureFormat.RGBAFloat: WF(p, 0, c.r); WF(p, 1, c.g); WF(p, 2, c.b); WF(p, 3, c.a); return;
            }
            throw UnsupportedFormat(format);
        }

        static void WH(Span<byte> p, int i, float v) => BinaryPrimitives.WriteHalfLittleEndian(p[(i * 2)..], (Half)v);
        static void WF(Span<byte> p, int i, float v) => BinaryPrimitives.WriteSingleLittleEndian(p[(i * 4)..], v);

        public static UnityException UnsupportedFormat(TextureFormat format)
            => new($"Unsupported texture format - Texture2D::GetPixels and SetPixels do not support {format} in this engine.");

        /// A compressed level decoded for a device that cannot sample its format, as Unity
        /// decompresses such textures: 8-bit RGBA, or half-float RGBA for BC6H (`halfFloat`).
        /// Rows are decoded in parallel (each texel decodes its block).
        public static unsafe void DecodeLevel(TextureFormat format, byte* level, long length, int width, int height, byte* output, bool halfFloat)
        {
            nint source = (nint)level, target = (nint)output;
            System.Threading.Tasks.Parallel.For(0, height, y =>
            {
                var mip = new ReadOnlySpan<byte>((byte*)source, (int)length);
                for (int x = 0; x < width; x++)
                {
                    var c = Decode(format, mip, width, x, y);
                    long i = (long)y * width + x;
                    if (halfFloat)
                    {
                        var p = (Half*)target + i * 4;
                        p[0] = (Half)c.r; p[1] = (Half)c.g; p[2] = (Half)c.b; p[3] = (Half)c.a;
                    }
                    else
                    {
                        var p = (byte*)target + i * 4;
                        p[0] = ToU8(c.r); p[1] = ToU8(c.g); p[2] = ToU8(c.b); p[3] = ToU8(c.a);
                    }
                }
            });
        }

        // ----- Block decoding (texel reads of compressed readable textures) -----

        static Color DecodeBlockTexel(TextureFormat format, ReadOnlySpan<byte> mip, int mipWidth, int x, int y)
        {
            TryGetBlock(format, out int bw, out int bh, out int bytes);
            int blocksPerRow = (mipWidth + bw - 1) / bw;
            var block = mip.Slice(((y / bh) * blocksPerRow + x / bw) * bytes, bytes);
            int px = x % bw, py = y % bh;
            if (AstcBlockSize(format) != 0) return AstcDecoder.Decode(block, bw, bh, px, py, hdr: format >= TextureFormat.ASTC_HDR_4x4 && format <= TextureFormat.ASTC_HDR_12x12);
            switch (format)
            {
                case TextureFormat.DXT1: return ColorBlock(block, px, py, allowPunchThrough: true);
                case TextureFormat.DXT5:
                {
                    var c = ColorBlock(block[8..], px, py, allowPunchThrough: false);
                    c.a = AlphaBlock(block, px, py);
                    return c;
                }
                case TextureFormat.BC4: return new Color(AlphaBlock(block, px, py), 0f, 0f, 1f);
                case TextureFormat.BC5: return new Color(AlphaBlock(block, px, py), AlphaBlock(block[8..], px, py), 0f, 1f);
                case TextureFormat.BC6H: return BlockDecoders.Bc6hUnsigned(block, px, py);
                case TextureFormat.BC7: return BlockDecoders.Bc7(block, px, py);
                case TextureFormat.ETC_RGB4: return Rgba8(BlockDecoders.Etc(block, px, py, BlockDecoders.EtcKind.Etc1));
                case TextureFormat.ETC2_RGB: return Rgba8(BlockDecoders.Etc(block, px, py, BlockDecoders.EtcKind.Etc2));
                case TextureFormat.ETC2_RGBA1: return Rgba8(BlockDecoders.Etc(block, px, py, BlockDecoders.EtcKind.Etc2PunchThrough));
                case TextureFormat.ETC2_RGBA8:
                {
                    var c = Rgba8(BlockDecoders.Etc(block[8..], px, py, BlockDecoders.EtcKind.Etc2));
                    c.a = BlockDecoders.EacAlpha(block, px, py) / 255f;
                    return c;
                }
                case TextureFormat.EAC_R: return new Color(BlockDecoders.Eac11(block, px, py, signed: false), 0f, 0f, 1f);
                case TextureFormat.EAC_R_SIGNED: return new Color(BlockDecoders.Eac11(block, px, py, signed: true), 0f, 0f, 1f);
                case TextureFormat.EAC_RG: return new Color(BlockDecoders.Eac11(block, px, py, signed: false), BlockDecoders.Eac11(block[8..], px, py, signed: false), 0f, 1f);
                case TextureFormat.EAC_RG_SIGNED: return new Color(BlockDecoders.Eac11(block, px, py, signed: true), BlockDecoders.Eac11(block[8..], px, py, signed: true), 0f, 1f);
            }
            throw UnsupportedFormat(format);
        }

        static Color Rgba8((int R, int G, int B, int A) c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

        static Color ColorBlock(ReadOnlySpan<byte> b, int px, int py, bool allowPunchThrough)
        {
            int c0 = b[0] | (b[1] << 8), c1 = b[2] | (b[3] << 8);
            uint indices = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
            int index = (int)((indices >> (2 * (py * 4 + px))) & 3);
            Color e0 = Rgb565(c0), e1 = Rgb565(c1);
            if (c0 > c1 || !allowPunchThrough)
                return index switch
                {
                    0 => e0,
                    1 => e1,
                    2 => new Color((2 * e0.r + e1.r) / 3f, (2 * e0.g + e1.g) / 3f, (2 * e0.b + e1.b) / 3f, 1f),
                    _ => new Color((e0.r + 2 * e1.r) / 3f, (e0.g + 2 * e1.g) / 3f, (e0.b + 2 * e1.b) / 3f, 1f),
                };
            return index switch
            {
                0 => e0,
                1 => e1,
                2 => new Color((e0.r + e1.r) / 2f, (e0.g + e1.g) / 2f, (e0.b + e1.b) / 2f, 1f),
                _ => new Color(0f, 0f, 0f, 0f),
            };
        }

        static Color Rgb565(int v) => new(((v >> 11) & 31) / 31f, ((v >> 5) & 63) / 63f, (v & 31) / 31f, 1f);

        static float AlphaBlock(ReadOnlySpan<byte> b, int px, int py)
        {
            float a0 = b[0] / 255f, a1 = b[1] / 255f;
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
            int index = (int)((bits >> (3 * (py * 4 + px))) & 7);
            if (index == 0) return a0;
            if (index == 1) return a1;
            if (b[0] > b[1]) return ((8 - index) * a0 + (index - 1) * a1) / 7f;
            if (index == 6) return 0f;
            if (index == 7) return 1f;
            return ((6 - index) * a0 + (index - 1) * a1) / 5f;
        }

        // ----- GraphicsFormat ↔ TextureFormat (GraphicsFormatUtility) -----

        public static GraphicsFormat ToGraphicsFormat(TextureFormat format, bool srgb) => format switch
        {
            TextureFormat.Alpha8 or TextureFormat.R8 => srgb && format == TextureFormat.R8 ? GraphicsFormat.R8_SRGB : GraphicsFormat.R8_UNorm,
            TextureFormat.ARGB4444 => GraphicsFormat.B4G4R4A4_UNormPack16,
            TextureFormat.RGBA4444 => GraphicsFormat.R4G4B4A4_UNormPack16,
            TextureFormat.RGB24 => srgb ? GraphicsFormat.R8G8B8_SRGB : GraphicsFormat.R8G8B8_UNorm,
            TextureFormat.RGBA32 or TextureFormat.ARGB32 => srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm,
            TextureFormat.BGRA32 => srgb ? GraphicsFormat.B8G8R8A8_SRGB : GraphicsFormat.B8G8R8A8_UNorm,
            TextureFormat.RGB565 => GraphicsFormat.B5G6R5_UNormPack16,
            TextureFormat.R16 => GraphicsFormat.R16_UNorm,
            TextureFormat.RG16 => srgb ? GraphicsFormat.R8G8_SRGB : GraphicsFormat.R8G8_UNorm,
            TextureFormat.RG32 => GraphicsFormat.R16G16_UNorm,
            TextureFormat.RGB48 => GraphicsFormat.R16G16B16_UNorm,
            TextureFormat.RGBA64 => GraphicsFormat.R16G16B16A16_UNorm,
            TextureFormat.RHalf => GraphicsFormat.R16_SFloat,
            TextureFormat.RGHalf => GraphicsFormat.R16G16_SFloat,
            TextureFormat.RGBAHalf => GraphicsFormat.R16G16B16A16_SFloat,
            TextureFormat.RFloat => GraphicsFormat.R32_SFloat,
            TextureFormat.RGFloat => GraphicsFormat.R32G32_SFloat,
            TextureFormat.RGBAFloat => GraphicsFormat.R32G32B32A32_SFloat,
            TextureFormat.RGB9e5Float => GraphicsFormat.E5B9G9R9_UFloatPack32,
            TextureFormat.DXT1 or TextureFormat.DXT1Crunched => srgb ? GraphicsFormat.RGBA_DXT1_SRGB : GraphicsFormat.RGBA_DXT1_UNorm,
            TextureFormat.DXT5 or TextureFormat.DXT5Crunched => srgb ? GraphicsFormat.RGBA_DXT5_SRGB : GraphicsFormat.RGBA_DXT5_UNorm,
            TextureFormat.BC4 => GraphicsFormat.R_BC4_UNorm,
            TextureFormat.BC5 => GraphicsFormat.RG_BC5_UNorm,
            TextureFormat.BC6H => GraphicsFormat.RGB_BC6H_UFloat,
            TextureFormat.BC7 => srgb ? GraphicsFormat.RGBA_BC7_SRGB : GraphicsFormat.RGBA_BC7_UNorm,
            _ => GraphicsFormat.None,
        };

        public static TextureFormat ToTextureFormat(GraphicsFormat format) => format switch
        {
            GraphicsFormat.R8_SRGB or GraphicsFormat.R8_UNorm => TextureFormat.R8,
            GraphicsFormat.R8G8_SRGB or GraphicsFormat.R8G8_UNorm => TextureFormat.RG16,
            GraphicsFormat.R8G8B8_SRGB or GraphicsFormat.R8G8B8_UNorm => TextureFormat.RGB24,
            GraphicsFormat.R8G8B8A8_SRGB or GraphicsFormat.R8G8B8A8_UNorm => TextureFormat.RGBA32,
            GraphicsFormat.B8G8R8A8_SRGB or GraphicsFormat.B8G8R8A8_UNorm => TextureFormat.BGRA32,
            GraphicsFormat.B4G4R4A4_UNormPack16 => TextureFormat.ARGB4444,
            GraphicsFormat.R4G4B4A4_UNormPack16 => TextureFormat.RGBA4444,
            GraphicsFormat.B5G6R5_UNormPack16 => TextureFormat.RGB565,
            GraphicsFormat.R16_UNorm => TextureFormat.R16,
            GraphicsFormat.R16G16_UNorm => TextureFormat.RG32,
            GraphicsFormat.R16G16B16_UNorm => TextureFormat.RGB48,
            GraphicsFormat.R16G16B16A16_UNorm => TextureFormat.RGBA64,
            GraphicsFormat.R16_SFloat => TextureFormat.RHalf,
            GraphicsFormat.R16G16_SFloat => TextureFormat.RGHalf,
            GraphicsFormat.R16G16B16A16_SFloat => TextureFormat.RGBAHalf,
            GraphicsFormat.R32_SFloat => TextureFormat.RFloat,
            GraphicsFormat.R32G32_SFloat => TextureFormat.RGFloat,
            GraphicsFormat.R32G32B32A32_SFloat => TextureFormat.RGBAFloat,
            GraphicsFormat.E5B9G9R9_UFloatPack32 => TextureFormat.RGB9e5Float,
            GraphicsFormat.RGBA_DXT1_SRGB or GraphicsFormat.RGBA_DXT1_UNorm => TextureFormat.DXT1,
            GraphicsFormat.RGBA_DXT5_SRGB or GraphicsFormat.RGBA_DXT5_UNorm => TextureFormat.DXT5,
            GraphicsFormat.R_BC4_UNorm => TextureFormat.BC4,
            GraphicsFormat.RG_BC5_UNorm => TextureFormat.BC5,
            GraphicsFormat.RGB_BC6H_UFloat => TextureFormat.BC6H,
            GraphicsFormat.RGBA_BC7_SRGB or GraphicsFormat.RGBA_BC7_UNorm => TextureFormat.BC7,
            _ => 0,
        };

        public static bool IsSrgb(GraphicsFormat format) => format is GraphicsFormat.R8_SRGB or GraphicsFormat.R8G8_SRGB or GraphicsFormat.R8G8B8_SRGB
            or GraphicsFormat.R8G8B8A8_SRGB or GraphicsFormat.B8G8R8_SRGB or GraphicsFormat.B8G8R8A8_SRGB or GraphicsFormat.RGBA_DXT1_SRGB
            or GraphicsFormat.RGBA_DXT3_SRGB or GraphicsFormat.RGBA_DXT5_SRGB or GraphicsFormat.RGBA_BC7_SRGB;
    }
}
