using System;
using OniMoono.Desktop.Vulkan;
using OniMoono.Engine.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace OniMoono.Desktop.D3d11
{
    /// A texture format on the Direct3D 11 device: the DXGI format, how texels are converted on
    /// upload (VkFormats.Convert's conversions, plus ARGB32's byte order, which D3D11 views
    /// cannot swizzle), and the block size and bytes per block (1 and bytes per texel when not
    /// block-compressed).
    internal readonly record struct D3d11TextureFormat(DXGI_FORMAT Format, TexelConversion Conversion, bool ArgbToRgba, int BlockBytes, int BlockSize);

    /// Formats as the original's Direct3D 11 player has them where measured
    /// (docs/research/d3d11-unity-probes.md §5, "GetNativeTexturePtr": RGBA32, ARGB32, RGB24 and
    /// RGBA4444 as R8G8B8A8_UNORM, BGRA32 as B8G8R8A8_UNORM, Alpha8 as A8_UNORM, R8 as R8_UNORM,
    /// RGB565 as B5G6R5_UNORM, RHalf/RGBAHalf/RFloat as R16_FLOAT/R16G16B16A16_FLOAT/R32_FLOAT,
    /// DXT1/DXT5/BC7 as BC1/BC3/BC7_UNORM; render textures and their depth in the same section and
    /// §3), else the DXGI format whose texels read as OpenGL's device reads them (VkFormats.Of).
    internal static class D3d11Formats
    {
        public static D3d11TextureFormat Of(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.Alpha8: return new(DXGI_FORMAT.DXGI_FORMAT_A8_UNORM, TexelConversion.None, false, 1, 1);
                case TextureFormat.R8: return new(DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, TexelConversion.None, false, 1, 1);
                case TextureFormat.RG16: return new(DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM, TexelConversion.None, false, 2, 1);
                case TextureFormat.RGB24: return new(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, TexelConversion.Rgb24ToRgba32, false, 4, 1);
                case TextureFormat.RGBA32: return new(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, TexelConversion.None, false, 4, 1);
                case TextureFormat.ARGB32: return new(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, TexelConversion.None, true, 4, 1);
                case TextureFormat.BGRA32: return new(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, TexelConversion.None, false, 4, 1);
                case TextureFormat.ARGB4444: return new(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, TexelConversion.Argb4444ToRgba32, false, 4, 1);
                case TextureFormat.RGBA4444: return new(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, TexelConversion.Rgba4444ToRgba32, false, 4, 1);
                // Red in the top five bits, as Unity's RGB565 and OpenGL's UNSIGNED_SHORT_5_6_5:
                // DXGI names components from the least significant bit (dxgiformat.h).
                case TextureFormat.RGB565: return new(DXGI_FORMAT.DXGI_FORMAT_B5G6R5_UNORM, TexelConversion.None, false, 2, 1);
                case TextureFormat.R16: return new(DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, TexelConversion.None, false, 2, 1);
                case TextureFormat.RG32: return new(DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM, TexelConversion.None, false, 4, 1);
                case TextureFormat.RGB48: return new(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM, TexelConversion.Rgb48ToRgba64, false, 8, 1);
                case TextureFormat.RGBA64: return new(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM, TexelConversion.None, false, 8, 1);
                case TextureFormat.RHalf: return new(DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT, TexelConversion.None, false, 2, 1);
                case TextureFormat.RGHalf: return new(DXGI_FORMAT.DXGI_FORMAT_R16G16_FLOAT, TexelConversion.None, false, 4, 1);
                case TextureFormat.RGBAHalf: return new(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, TexelConversion.None, false, 8, 1);
                case TextureFormat.RFloat: return new(DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT, TexelConversion.None, false, 4, 1);
                case TextureFormat.RGFloat: return new(DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, TexelConversion.None, false, 8, 1);
                case TextureFormat.RGBAFloat: return new(DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT, TexelConversion.None, false, 16, 1);
                case TextureFormat.DXT1: return new(DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM, TexelConversion.None, false, 8, 4);
                case TextureFormat.DXT5: return new(DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM, TexelConversion.None, false, 16, 4);
                case TextureFormat.BC4: return new(DXGI_FORMAT.DXGI_FORMAT_BC4_UNORM, TexelConversion.None, false, 8, 4);
                case TextureFormat.BC5: return new(DXGI_FORMAT.DXGI_FORMAT_BC5_UNORM, TexelConversion.None, false, 16, 4);
                case TextureFormat.BC6H: return new(DXGI_FORMAT.DXGI_FORMAT_BC6H_UF16, TexelConversion.None, false, 16, 4);
                case TextureFormat.BC7: return new(DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM, TexelConversion.None, false, 16, 4);
            }
            if (TextureFormats.IsBlockCompressed(f) && TextureFormats.CanDecode(f)) return Decoded(f);
            throw new NotSupportedException($"Texture format {f} has no Direct3D 11 equivalent in this device.");
        }

        /// A block-compressed format decoded on the CPU, as OpenGL's device decodes the ones it
        /// cannot sample (TextureFormats.DecodeLevel): to RGBA8, BC6H to half floats. Also a BC
        /// texture whose level 0 is no whole number of blocks, which D3D11 cannot make.
        public static D3d11TextureFormat Decoded(TextureFormat f) => f == TextureFormat.BC6H
            ? new(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, TexelConversion.Decode, false, 8, 1)
            : new(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, TexelConversion.Decode, false, 4, 1);

        /// A render texture's colour format (GraphicsFormat as RenderShape gives it), as the
        /// OpenGL device's (GlTextures.InternalOf) in DXGI terms. B8G8R8A8 render textures are
        /// R8G8B8A8, as on OpenGL and Vulkan (the original's resource format for them is not
        /// measured; a shader reads the same values).
        public static DXGI_FORMAT Color(GraphicsFormat f) => f switch
        {
            GraphicsFormat.R8G8B8A8_UNorm or GraphicsFormat.R8G8B8A8_SRGB or GraphicsFormat.B8G8R8A8_UNorm or GraphicsFormat.B8G8R8A8_SRGB => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            GraphicsFormat.R16G16B16A16_SFloat => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
            GraphicsFormat.R32G32B32A32_SFloat => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
            GraphicsFormat.R32G32_SFloat => DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
            GraphicsFormat.R16G16_SFloat => DXGI_FORMAT.DXGI_FORMAT_R16G16_FLOAT,
            GraphicsFormat.R32_SFloat => DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
            GraphicsFormat.R16_SFloat => DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT,
            GraphicsFormat.R8_UNorm => DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
            GraphicsFormat.R8G8_UNorm => DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM,
            GraphicsFormat.R16_UNorm => DXGI_FORMAT.DXGI_FORMAT_R16_UNORM,
            GraphicsFormat.R16G16_UNorm => DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM,
            GraphicsFormat.R16G16B16A16_UNorm => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM,
            // Measured: the original's RGB565 render texture is B5G6R5_UNORM (§5).
            GraphicsFormat.B5G6R5_UNormPack16 => DXGI_FORMAT.DXGI_FORMAT_B5G6R5_UNORM,
            // The original's ARGB4444 render texture is R8G8B8A8_UNorm (§3).
            GraphicsFormat.B4G4R4A4_UNormPack16 => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            GraphicsFormat.B5G5R5A1_UNormPack16 => DXGI_FORMAT.DXGI_FORMAT_B5G5R5A1_UNORM,
            GraphicsFormat.A2B10G10R10_UNormPack32 => DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM,
            GraphicsFormat.B10G11R11_UFloatPack32 => DXGI_FORMAT.DXGI_FORMAT_R11G11B10_FLOAT,
            // Integer formats are not offered (as on OpenGL: no game target uses them).
            _ => throw new NotSupportedException($"Render texture format {f} has no Direct3D 11 equivalent in this device."),
        };

        /// A depth surface: its typeless resource, depth-stencil view and shader view formats,
        /// and whether it has stencil. More than 16 bits is D32_FLOAT_S8X24 (the original's 24-
        /// and 32-bit depth, R32G8X24_TYPELESS with SRV R32_FLOAT_X8X24, §3 and §5); 16 bits and
        /// less D16 (the original's 16-bit depth and a depth-only texture asked for 0 bits, §3).
        public static (DXGI_FORMAT Resource, DXGI_FORMAT View, DXGI_FORMAT Shader, bool Stencil) Depth(int bits) => bits > 16
            ? (DXGI_FORMAT.DXGI_FORMAT_R32G8X24_TYPELESS, DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT, DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS, true)
            : (DXGI_FORMAT.DXGI_FORMAT_R16_TYPELESS, DXGI_FORMAT.DXGI_FORMAT_D16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, false);

        /// Converts one level's texels for upload; returns the bytes to copy (the input itself
        /// when nothing changes).
        public static unsafe ReadOnlySpan<byte> Convert(in D3d11TextureFormat f, TextureFormat source, byte* texels, long length, int width, int height, ref byte[] scratch)
        {
            if (!f.ArgbToRgba) return VkFormats.Convert(f.Conversion, source, texels, length, width, height, ref scratch);
            // Unity's ARGB32 bytes (a, r, g, b) as R8G8B8A8 (r, g, b, a).
            long pixels = Math.Min(length / 4, (long)width * height);
            long size = pixels * 4;
            if (scratch == null || scratch.Length < size) scratch = new byte[size];
            fixed (byte* o = scratch)
                for (long i = 0; i < pixels; i++)
                {
                    o[i * 4] = texels[i * 4 + 1];
                    o[i * 4 + 1] = texels[i * 4 + 2];
                    o[i * 4 + 2] = texels[i * 4 + 3];
                    o[i * 4 + 3] = texels[i * 4];
                }
            return new ReadOnlySpan<byte>(scratch, 0, (int)size);
        }

        /// Bytes per texel of the formats render textures use (readbacks).
        public static int TexelBytes(DXGI_FORMAT f) => f switch
        {
            DXGI_FORMAT.DXGI_FORMAT_R8_UNORM or DXGI_FORMAT.DXGI_FORMAT_A8_UNORM => 1,
            DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM or DXGI_FORMAT.DXGI_FORMAT_R16_UNORM or DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT or DXGI_FORMAT.DXGI_FORMAT_B5G6R5_UNORM
                or DXGI_FORMAT.DXGI_FORMAT_B5G5R5A1_UNORM or DXGI_FORMAT.DXGI_FORMAT_B4G4R4A4_UNORM => 2,
            DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM or DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT or DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT => 8,
            DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT => 16,
            _ => 4,
        };

        /// One texel of a readback in OpenGL's ReadPixels(RGBA, FLOAT) terms: missing green and
        /// blue read 0, missing alpha 1 (as VkFormats.ReadTexel).
        public static unsafe Color ReadTexel(DXGI_FORMAT format, byte* p)
        {
            static float U8(byte v) => v / 255f;
            static float U16(ushort v) => v / 65535f;
            static float H(ushort v) => (float)BitConverter.UInt16BitsToHalf(v);
            switch (format)
            {
                case DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM: return new(U8(p[0]), U8(p[1]), U8(p[2]), U8(p[3]));
                case DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM: return new(U8(p[2]), U8(p[1]), U8(p[0]), U8(p[3]));
                case DXGI_FORMAT.DXGI_FORMAT_R8_UNORM: return new(U8(p[0]), 0, 0, 1);
                case DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM: return new(U8(p[0]), U8(p[1]), 0, 1);
                case DXGI_FORMAT.DXGI_FORMAT_R16_UNORM: return new(U16(((ushort*)p)[0]), 0, 0, 1);
                case DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM: return new(U16(((ushort*)p)[0]), U16(((ushort*)p)[1]), 0, 1);
                case DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM: { var s = (ushort*)p; return new(U16(s[0]), U16(s[1]), U16(s[2]), U16(s[3])); }
                case DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT: return new(H(((ushort*)p)[0]), 0, 0, 1);
                case DXGI_FORMAT.DXGI_FORMAT_R16G16_FLOAT: return new(H(((ushort*)p)[0]), H(((ushort*)p)[1]), 0, 1);
                case DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT: { var s = (ushort*)p; return new(H(s[0]), H(s[1]), H(s[2]), H(s[3])); }
                case DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT: return new(((float*)p)[0], 0, 0, 1);
                case DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT: return new(((float*)p)[0], ((float*)p)[1], 0, 1);
                case DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT: { var s = (float*)p; return new(s[0], s[1], s[2], s[3]); }
                case DXGI_FORMAT.DXGI_FORMAT_B5G6R5_UNORM: { int v = ((ushort*)p)[0]; return new((v >> 11) / 31f, (v >> 5 & 63) / 63f, (v & 31) / 31f, 1); }
                // DXGI order from the least significant bit: B 0-4, G 5-9, R 10-14, A 15.
                case DXGI_FORMAT.DXGI_FORMAT_B5G5R5A1_UNORM: { int v = ((ushort*)p)[0]; return new((v >> 10 & 31) / 31f, (v >> 5 & 31) / 31f, (v & 31) / 31f, v >> 15); }
                case DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM: { uint v = ((uint*)p)[0]; return new((v & 1023) / 1023f, (v >> 10 & 1023) / 1023f, (v >> 20 & 1023) / 1023f, (v >> 30) / 3f); }
                default: throw new NotSupportedException($"reading back {format}");
            }
        }
    }
}
