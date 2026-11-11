using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Brutal.StbApi;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine
{
    public partial struct DrivenRectTransformTracker
    {
        // Driven-property tracking serves the editor's inspector; in a player Unity records
        // nothing.
        public void Add(Object driver, RectTransform rectTransform, DrivenTransformProperties drivenProperties) { }
        public void Clear() { }
        public void Clear(bool revertValues) { }
    }

    public static partial class ImageConversion
    {
        public static bool LoadImage(this Texture2D tex, byte[] data) => LoadImage(tex, data, false);

        /// Whether LoadImage would take the data as an image (a PNG or JPEG signature).
        internal static bool LooksLikeImage(byte[] data)
            => data != null && (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G'
                                || data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF);

        /// Replaces the texture with a PNG or JPEG image: PNGs load as ARGB32, JPEGs as RGB24,
        /// at the image's size, keeping a mip chain if the texture had one. False (texture
        /// unchanged) when the data is not an image.
        public static bool LoadImage(this Texture2D tex, byte[] data, bool markNonReadable)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));
            if (data == null) throw new ArgumentNullException(nameof(data));
            Threads.AssertMainThread("LoadImage");
            bool png = data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';
            bool jpeg = data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
            if (!png && !jpeg) return false;
            if (ImageDecodeMemo.TryGet(data, out var keptFormat, out int keptWidth, out int keptHeight, out var keptTexels))
            {
                Fill(tex, keptFormat, keptWidth, keptHeight, keptTexels, markNonReadable);
                return true;
            }
            bool loaded = png && PngDecode.TryParse(data, out var parsed) && LoadPng(tex, data, parsed, markNonReadable)
                || LoadWithStb(tex, data, png, markNonReadable);
            if (loaded && ImageDecodeMemo.IsOpen)
            {
                var native = (Texture2DNative)tex.NativeOrThrow;
                ImageDecodeMemo.Keep(data, native.Format, native.Width, native.Height, native.Memory.Span.Slice(0, (int)native.MipSize(0)));
            }
            return loaded;
        }

        /// A decode kept by ImageDecodeMemo, into the texture as the decode left it.
        static void Fill(Texture2D tex, TextureFormat format, int w, int h, byte[] level0, bool markNonReadable)
        {
            var native = (Texture2DNative)tex.NativeOrThrow;
            int mips = native.MipCount > 1 ? TextureFormats.FullMipCount(w, h) : 1;
            var texels = new PixelMemory(TextureFormats.ImageSize(format, w, h, mips));
            level0.CopyTo(texels.Span);
            native.Width = w;
            native.Height = h;
            native.Format = format;
            native.MipCount = mips;
            native.Readable = true;
            native.Adopt(texels);
            TexelAddressing.BuildMips(native);
            if (markNonReadable) native.Readable = false;
            native.ContentChanged();
        }

        /// The PNGs PngDecode takes: decoded into new texels, which replace the texture's only once
        /// the whole image decoded (else stb decodes it).
        static bool LoadPng(Texture2D tex, byte[] data, in PngDecode.Image image, bool markNonReadable)
        {
            var native = (Texture2DNative)tex.NativeOrThrow;
            int w = image.Width, h = image.Height;
            int mips = native.MipCount > 1 ? TextureFormats.FullMipCount(w, h) : 1;
            var texels = new PixelMemory(TextureFormats.ImageSize(TextureFormat.ARGB32, w, h, mips));
            if (!PngDecode.TryDecode(data, image, texels.Slice(0, (long)w * h * 4)))
            {
                texels.Free();
                return false;
            }
            native.Width = w;
            native.Height = h;
            native.Format = TextureFormat.ARGB32;
            native.MipCount = mips;
            native.Readable = true;
            native.Adopt(texels);
            TexelAddressing.BuildMips(native);
            if (markNonReadable) native.Readable = false;
            native.ContentChanged();
            return true;
        }

        /// stb_image's decode (JPEGs, and the PNGs PngDecode leaves to it).
        internal static bool LoadWithStb(Texture2D tex, byte[] data, bool png, bool markNonReadable)
        {
            using var image = Stb.LoadFromMemory(data, png ? 4 : 3);
            if (image.IsNull()) return false;
            var native = (Texture2DNative)tex.NativeOrThrow;
            var format = png ? TextureFormat.ARGB32 : TextureFormat.RGB24;
            int w = image.Width, h = image.Height;
            native.Width = w;
            native.Height = h;
            native.Format = format;
            native.MipCount = native.MipCount > 1 ? TextureFormats.FullMipCount(w, h) : 1;
            native.Readable = true;
            native.Allocate(native.SliceSize, 0);
            // stb rows run top to bottom; Unity's run bottom to top.
            var src = image.AsSpan();
            var dst = native.Memory.Span;
            int channels = png ? 4 : 3;
            for (int y = 0; y < h; y++)
            {
                var row = src.Slice((h - 1 - y) * w * channels, w * channels);
                var outRow = dst.Slice(y * w * channels, w * channels);
                if (png)
                    for (int x = 0; x < w; x++)
                    {
                        outRow[x * 4] = row[x * 4 + 3];
                        outRow[x * 4 + 1] = row[x * 4];
                        outRow[x * 4 + 2] = row[x * 4 + 1];
                        outRow[x * 4 + 3] = row[x * 4 + 2];
                    }
                else row.CopyTo(outRow);
            }
            TexelAddressing.BuildMips(native);
            if (markNonReadable) native.Readable = false;
            native.ContentChanged();
            return true;
        }

        /// PNG of level 0: RGB for formats without alpha, greyscale for single-channel red
        /// formats, RGBA otherwise; 8 bits per channel. Null, with an error, for a compressed
        /// format; an unreadable texture throws (measured in the original player, as for JPG
        /// and TGA).
        public static byte[] EncodeToPNG(this Texture2D tex)
        {
            if (!Level0(tex, out var format, out var mip, out int w, out int h)) return null;
            return EncodePng(format, mip, w, h, (int)TextureFormats.MipSize(format, w, 1));
        }

        public static byte[] EncodeToJPG(this Texture2D tex) => EncodeToJPG(tex, 75);

        /// Baseline JPEG of level 0 at `quality` clamped to 1..100: greyscale for single-channel
        /// red formats, RGB otherwise, alpha dropped (not blended), Alpha8 as black. Null, with an
        /// error, for a compressed format; an unreadable texture throws (measured in the original
        /// player). stb's writer differs from the original's (libjpeg's tables) in form, not in
        /// what decodes: the original subsamples chroma 2 x 2 at every quality (stb only up to
        /// 90), writes grey as one component (stb: three, the chroma flat) and uses 16-bit
        /// quantisation tables at the lowest qualities (stb clamps them to 8 bits).
        public static byte[] EncodeToJPG(this Texture2D tex, int quality)
        {
            if (!Level0(tex, out var format, out var mip, out int w, out int h)) return null;
            int channels = Channels(format) == 1 ? 1 : 3;
            return Jpeg.Encode(w, h, channels, Rows(format, mip, w, h, channels, topDown: true, bgr: false, rowPrefix: 0), quality);
        }

        /// Uncompressed TGA of level 0 (image type 2, or 3 for greyscale; rows bottom to top, no
        /// alpha bits in the descriptor): 8-bit greyscale for single-channel red formats, BGR for
        /// formats without alpha, BGRA otherwise. Null, with an error, for a compressed format;
        /// an unreadable texture throws (measured in the original player).
        public static byte[] EncodeToTGA(this Texture2D tex)
        {
            if (!Level0(tex, out var format, out var mip, out int w, out int h)) return null;
            int channels = Channels(format);
            return Tga.Encode(w, h, channels, Rows(format, mip, w, h, channels, topDown: false, bgr: true, rowPrefix: 0));
        }

        /// PNG of raw texels whose rows run bottom to top, `rowBytes` apart (0: packed), as
        /// EncodeToPNG writes a texture of that format (measured in the original player with
        /// RGBA, sRGB RGBA, BGRA, RGB rows with padding and R8 data). Null, with an error, for a
        /// format the engine cannot decode.
        public static unsafe byte[] EncodeArrayToPNG(Array array, GraphicsFormat format, uint width, uint height, uint rowBytes = 0)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            var textureFormat = GraphicsFormatUtility.GetTextureFormat(format);
            if (textureFormat == 0 || !TextureFormats.CanDecode(textureFormat))
            {
                Debug.LogError($"Unable to encode texture in format {format} to PNG.");
                return null;
            }
            int w = (int)width, h = (int)height;
            int packed = (int)TextureFormats.MipSize(textureFormat, w, 1);
            int stride = rowBytes == 0 ? packed : (int)rowBytes;
            // The rows are read where they are: the array stays pinned while it is encoded.
            var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            try
            {
                var source = new ReadOnlySpan<byte>((void*)handle.AddrOfPinnedObject(), Buffer.ByteLength(array));
                if (h > 0 && source.Length < (long)stride * (h - 1) + packed) throw new ArgumentException("The array is too small for the image.", nameof(array));
                return EncodePng(textureFormat, source, w, h, stride);
            }
            finally
            {
                handle.Free();
            }
        }

        /// PNG of texels whose rows run bottom to top, `stride` bytes apart, as EncodeToPNG
        /// writes them: RGBA formats whose every texel is opaque as RGB (no reader the game has
        /// tells the two apart: LoadImage gives the same texels), straight from the rows where
        /// their bytes are the PNG's.
        static byte[] EncodePng(TextureFormat format, ReadOnlySpan<byte> texels, int w, int h, int stride)
        {
            int channels = Channels(format);
            if (w <= 0 || h <= 0) return Png.Encode(w, h, channels, new byte[Math.Max(0, h) * (1 + Math.Max(0, w) * channels)]);
            bool direct = format == TextureFormat.RGBA32 && channels == 4 || format == TextureFormat.RGB24 && channels == 3;
            int inputChannels = channels;
            if (channels == 4 && Opaque(format, texels, w, h, stride))
            {
                channels = 3;
                // RGBA32 rows go in as they are, their alpha left out by the writer.
                if (!direct) inputChannels = 3;
            }
            using var png = new PngWriter(null, w, h, channels, inputChannels);
            var row = direct ? null : new byte[w * inputChannels];
            bool packedRows = stride == (int)TextureFormats.MipSize(format, w, 1);
            for (int y = 0; y < h; y++)
            {
                int srcY = h - 1 - y;
                if (direct)
                {
                    png.WriteRow(texels.Slice(srcY * stride, w * inputChannels));
                    continue;
                }
                for (int x = 0; x < w; x++)
                {
                    // Packed rows (block formats among them) are read as one level; padded ones a
                    // row at a time.
                    var c = packedRows ? Texel(format, texels, w, x, srcY) : Texel(format, texels.Slice(srcY * stride), w, x, 0);
                    int o = x * inputChannels;
                    if (inputChannels == 1)
                    {
                        row[o] = c.r;
                        continue;
                    }
                    row[o] = c.r;
                    row[o + 1] = c.g;
                    row[o + 2] = c.b;
                    if (inputChannels == 4) row[o + 3] = c.a;
                }
                png.WriteRow(row);
            }
            png.Finish();
            return png.ToArray();
        }

        /// Whether every texel of an 8-bit RGBA format has alpha 255 (other formats: false).
        static bool Opaque(TextureFormat format, ReadOnlySpan<byte> texels, int w, int h, int stride)
        {
            uint mask = format switch
            {
                TextureFormat.RGBA32 or TextureFormat.BGRA32 => 0xFF000000u,
                TextureFormat.ARGB32 => 0x000000FFu,
                _ => 0,
            };
            if (mask == 0) return false;
            var all = new System.Numerics.Vector<uint>(mask);
            int lanes = System.Numerics.Vector<uint>.Count;
            for (int y = 0; y < h; y++)
            {
                var pixels = MemoryMarshal.Cast<byte, uint>(texels.Slice(y * stride, w * 4));
                int i = 0;
                for (; i + lanes <= pixels.Length; i += lanes)
                    if ((new System.Numerics.Vector<uint>(pixels.Slice(i)) & all) != all) return false;
                for (; i < pixels.Length; i++)
                    if ((pixels[i] & mask) != mask) return false;
            }
            return true;
        }

        /// JPEG of raw texels, as EncodeArrayToPNG reads them and EncodeToJPG writes them.
        public static byte[] EncodeArrayToJPG(Array array, GraphicsFormat format, uint width, uint height, uint rowBytes = 0, int quality = 75)
        {
            var texels = Pack(array, format, width, height, rowBytes, "JPG", out var textureFormat);
            if (texels == null) return null;
            int w = (int)width, h = (int)height, channels = Channels(textureFormat) == 1 ? 1 : 3;
            return Jpeg.Encode(w, h, channels, Rows(textureFormat, texels, w, h, channels, topDown: true, bgr: false, rowPrefix: 0), quality);
        }

        /// TGA of raw texels, as EncodeArrayToPNG reads them and EncodeToTGA writes them.
        public static byte[] EncodeArrayToTGA(Array array, GraphicsFormat format, uint width, uint height, uint rowBytes = 0)
        {
            var texels = Pack(array, format, width, height, rowBytes, "TGA", out var textureFormat);
            if (texels == null) return null;
            int w = (int)width, h = (int)height, channels = Channels(textureFormat);
            return Tga.Encode(w, h, channels, Rows(textureFormat, texels, w, h, channels, topDown: false, bgr: true, rowPrefix: 0));
        }

        /// A texture's level 0 for the EncodeTo functions. An unreadable texture throws
        /// ArgumentException; a compressed format logs an error (false).
        static bool Level0(Texture2D tex, out TextureFormat format, out ReadOnlySpan<byte> mip, out int width, out int height)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));
            var native = (Texture2DNative)tex.NativeOrThrow;
            if (!native.Readable)
                throw new ArgumentException($"Texture '{tex.name}' is not readable. For this reason, scripts cannot access the memory allocated to it. You can make this texture readable in the Texture Import Settings.");
            format = native.Format;
            width = native.Width;
            height = native.Height;
            if (!TextureFormats.CanEncode(format))
            {
                Debug.LogError("Unsupported texture format - Texture2D::EncodeTo functions do not support compressed texture formats.");
                mip = default;
                return false;
            }
            mip = native.Memory.Span.Slice(0, (int)native.MipSize(0));
            return true;
        }

        /// Raw rows `rowBytes` apart (0: packed), packed; null, with an error, for a format the
        /// engine cannot decode.
        static unsafe byte[] Pack(Array array, GraphicsFormat format, uint width, uint height, uint rowBytes, string kind, out TextureFormat textureFormat)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            textureFormat = GraphicsFormatUtility.GetTextureFormat(format);
            if (textureFormat == 0 || !TextureFormats.CanDecode(textureFormat))
            {
                Debug.LogError($"Unable to encode texture in format {format} to {kind}.");
                return null;
            }
            int w = (int)width, h = (int)height;
            int packed = (int)TextureFormats.MipSize(textureFormat, w, 1);
            int stride = rowBytes == 0 ? packed : (int)rowBytes;
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(array, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var source = new ReadOnlySpan<byte>((void*)handle.AddrOfPinnedObject(), Buffer.ByteLength(array));
                if (source.Length < (long)stride * (h - 1) + packed) throw new ArgumentException("The array is too small for the image.", nameof(array));
                var texels = new byte[packed * h];
                for (int y = 0; y < h; y++) source.Slice(y * stride, packed).CopyTo(texels.AsSpan(y * packed));
                return texels;
            }
            finally
            {
                handle.Free();
            }
        }

        /// The encoders' channels for a format: 1 (grey) for single-channel red formats, 3 for
        /// formats without alpha, 4 otherwise (Alpha8 included; measured in the original).
        static int Channels(TextureFormat format) => format switch
        {
            TextureFormat.R8 or TextureFormat.R16 or TextureFormat.RHalf or TextureFormat.RFloat or TextureFormat.BC4 => 1,
            TextureFormat.RGB24 or TextureFormat.RGB565 or TextureFormat.RG16 or TextureFormat.RG32 or TextureFormat.RGHalf
                or TextureFormat.RGFloat or TextureFormat.RGB48 or TextureFormat.DXT1 => 3,
            _ => 4,
        };

        /// A texel as the encoders read it (measured in the original): Alpha8 as black with alpha
        /// (GetPixel reads white), RGB565 with its bits repeated into the low ones, two-channel
        /// formats with blue 1, other channels rounded half up from their values.
        static Color32 Texel(TextureFormat format, ReadOnlySpan<byte> mip, int w, int x, int y)
        {
            if (format == TextureFormat.Alpha8) return new Color32(0, 0, 0, mip[y * w + x]);
            if (format == TextureFormat.RGB565)
            {
                int v = mip[(y * w + x) * 2] | mip[(y * w + x) * 2 + 1] << 8;
                int r = v >> 11, g = (v >> 5) & 63, b = v & 31;
                return new Color32((byte)(r << 3 | r >> 2), (byte)(g << 2 | g >> 4), (byte)(b << 3 | b >> 2), 255);
            }
            var c = TextureFormats.Decode(format, mip, w, x, y);
            bool twoChannels = format is TextureFormat.RG16 or TextureFormat.RG32 or TextureFormat.RGHalf or TextureFormat.RGFloat or TextureFormat.BC5;
            return new Color32(ToByte(c.r), ToByte(c.g), twoChannels ? (byte)255 : ToByte(c.b), ToByte(c.a));
        }

        static byte ToByte(float f) => (byte)(Mathf.Clamp01(f) * 255f + 0.5f);

        /// A level's texels (rows bottom to top in `mip`) as 8-bit rows of `channels` (grey: red),
        /// top row first or last, RGB(A) or BGR(A), each row after `rowPrefix` zero bytes.
        static byte[] Rows(TextureFormat format, ReadOnlySpan<byte> mip, int w, int h, int channels, bool topDown, bool bgr, int rowPrefix)
        {
            int rowSize = rowPrefix + w * channels;
            var rows = new byte[h * rowSize];
            for (int y = 0; y < h; y++)
            {
                int row = y * rowSize + rowPrefix;
                int srcY = topDown ? h - 1 - y : y;
                if (format == TextureFormat.RGBA32 && channels == 4 && !bgr)
                {
                    mip.Slice(srcY * w * 4, w * 4).CopyTo(rows.AsSpan(row));
                    continue;
                }
                if (format == TextureFormat.RGB24 && channels == 3 && !bgr)
                {
                    mip.Slice(srcY * w * 3, w * 3).CopyTo(rows.AsSpan(row));
                    continue;
                }
                for (int x = 0; x < w; x++)
                {
                    var c = Texel(format, mip, w, x, srcY);
                    int o = row + x * channels;
                    if (channels == 1)
                    {
                        rows[o] = c.r;
                        continue;
                    }
                    rows[o] = bgr ? c.b : c.r;
                    rows[o + 1] = c.g;
                    rows[o + 2] = bgr ? c.r : c.b;
                    if (channels == 4) rows[o + 3] = c.a;
                }
            }
            return rows;
        }
    }
}

namespace UnityEngine.Experimental.Rendering
{
    public partial class GraphicsFormatUtility
    {
        public static bool IsCrunchFormat(TextureFormat format) => TextureFormats.IsCrunch(format);

        // Each texture format's graphics format, as the original player reports them in the
        // game's (gamma) colour space, where isSRGB changes none of them.
        static readonly System.Collections.Generic.Dictionary<TextureFormat, GraphicsFormat> graphicsFormats = new()
        {
            [(TextureFormat)1] = (GraphicsFormat)54, [(TextureFormat)2] = (GraphicsFormat)67, [(TextureFormat)3] = (GraphicsFormat)7,
            [(TextureFormat)4] = (GraphicsFormat)8, [(TextureFormat)5] = (GraphicsFormat)88, [(TextureFormat)7] = (GraphicsFormat)69,
            [(TextureFormat)9] = (GraphicsFormat)21, [(TextureFormat)10] = (GraphicsFormat)97, [(TextureFormat)12] = (GraphicsFormat)101,
            [(TextureFormat)13] = (GraphicsFormat)66, [(TextureFormat)14] = (GraphicsFormat)59, [(TextureFormat)15] = (GraphicsFormat)45,
            [(TextureFormat)16] = (GraphicsFormat)46, [(TextureFormat)17] = (GraphicsFormat)48, [(TextureFormat)18] = (GraphicsFormat)49,
            [(TextureFormat)19] = (GraphicsFormat)50, [(TextureFormat)20] = (GraphicsFormat)52, [(TextureFormat)21] = (GraphicsFormat)141,
            [(TextureFormat)22] = (GraphicsFormat)73, [(TextureFormat)24] = (GraphicsFormat)106, [(TextureFormat)25] = (GraphicsFormat)109,
            [(TextureFormat)26] = (GraphicsFormat)102, [(TextureFormat)27] = (GraphicsFormat)104, [(TextureFormat)28] = (GraphicsFormat)97,
            [(TextureFormat)29] = (GraphicsFormat)101, [(TextureFormat)30] = (GraphicsFormat)111, [(TextureFormat)31] = (GraphicsFormat)115,
            [(TextureFormat)32] = (GraphicsFormat)113, [(TextureFormat)33] = (GraphicsFormat)117, [(TextureFormat)34] = (GraphicsFormat)118,
            [(TextureFormat)41] = (GraphicsFormat)125, [(TextureFormat)42] = (GraphicsFormat)126, [(TextureFormat)43] = (GraphicsFormat)127,
            [(TextureFormat)44] = (GraphicsFormat)128, [(TextureFormat)45] = (GraphicsFormat)120, [(TextureFormat)46] = (GraphicsFormat)122,
            [(TextureFormat)47] = (GraphicsFormat)124, [(TextureFormat)48] = (GraphicsFormat)130, [(TextureFormat)49] = (GraphicsFormat)132,
            [(TextureFormat)50] = (GraphicsFormat)134, [(TextureFormat)51] = (GraphicsFormat)136, [(TextureFormat)52] = (GraphicsFormat)138,
            [(TextureFormat)53] = (GraphicsFormat)140, [(TextureFormat)62] = (GraphicsFormat)6, [(TextureFormat)63] = (GraphicsFormat)5,
            [(TextureFormat)64] = (GraphicsFormat)118, [(TextureFormat)65] = (GraphicsFormat)124, [(TextureFormat)66] = (GraphicsFormat)145,
            [(TextureFormat)67] = (GraphicsFormat)146, [(TextureFormat)68] = (GraphicsFormat)147, [(TextureFormat)69] = (GraphicsFormat)148,
            [(TextureFormat)70] = (GraphicsFormat)149, [(TextureFormat)71] = (GraphicsFormat)150, [(TextureFormat)72] = (GraphicsFormat)22,
            [(TextureFormat)73] = (GraphicsFormat)23, [(TextureFormat)74] = (GraphicsFormat)24, [(TextureFormat)75] = (GraphicsFormat)9,
            [(TextureFormat)76] = (GraphicsFormat)10, [(TextureFormat)77] = (GraphicsFormat)11, [(TextureFormat)78] = (GraphicsFormat)12,
            [(TextureFormat)79] = (GraphicsFormat)25, [(TextureFormat)80] = (GraphicsFormat)26, [(TextureFormat)81] = (GraphicsFormat)27,
            [(TextureFormat)82] = (GraphicsFormat)28,
        };

        public static GraphicsFormat GetGraphicsFormat(TextureFormat format, bool isSRGB)
            => graphicsFormats.TryGetValue(format, out var graphics) ? graphics : GraphicsFormat.None;

        /// The texture format whose texels a graphics format's data has (the first with that
        /// graphics format; its sRGB twin for an sRGB one); 0 for none.
        internal static TextureFormat GetTextureFormat(GraphicsFormat format)
        {
            foreach (var (texture, graphics) in graphicsFormats)
                if (graphics == format) return texture;
            return format switch
            {
                GraphicsFormat.R8G8B8A8_SRGB => TextureFormat.RGBA32,
                GraphicsFormat.B8G8R8A8_SRGB => TextureFormat.BGRA32,
                GraphicsFormat.R8G8B8_SRGB => TextureFormat.RGB24,
                _ => 0,
            };
        }
    }
}

namespace OniMoono.Engine
{
    /// The PNG of an empty image (no rows or no columns), as the engine always wrote it: one
    /// IHDR, one zlib-compressed IDAT, IEND. PngWriter writes every other.
    internal static class Png
    {
        public static byte[] Encode(int width, int height, int channels, byte[] filteredRows)
        {
            using var output = new MemoryStream();
            output.Write(PngWriter.Signature);
            var header = new byte[13];
            PngWriter.BigEndian(header.AsSpan(0), width);
            PngWriter.BigEndian(header.AsSpan(4), height);
            header[8] = 8;
            header[9] = channels switch { 1 => 0, 3 => 2, _ => 6 };
            Chunk(output, "IHDR", header);
            using (var compressed = new MemoryStream())
            {
                using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(filteredRows);
                Chunk(output, "IDAT", compressed.ToArray());
            }
            Chunk(output, "IEND", Array.Empty<byte>());
            return output.ToArray();
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            Span<byte> word = stackalloc byte[4];
            PngWriter.BigEndian(word, data.Length);
            s.Write(word);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(data);
            var crc = new Crc32();
            crc.Append(typeBytes);
            crc.Append(data);
            PngWriter.BigEndian(word, (int)crc.Current);
            s.Write(word);
        }
    }

    /// Baseline JPEG through stb_image_write, in BRUTAL.Stb's native library (whose bindings
    /// cover only the file writers): stbi_write_jpg_to_func, collecting the output in memory.
    internal static unsafe class Jpeg
    {
        static delegate* unmanaged<delegate* unmanaged<void*, void*, int, void>, void*, int, int, int, void*, int, int> write;

        /// JPEG of 8-bit rows, top row first, of 1 (grey) or 3 (RGB) channels; quality clamped to
        /// 1..100.
        public static byte[] Encode(int width, int height, int channels, byte[] rows, int quality)
        {
            if (write == null)
            {
                var library = NativeLibrary.Load("stb", typeof(Stb).Assembly, null);
                write = (delegate* unmanaged<delegate* unmanaged<void*, void*, int, void>, void*, int, int, int, void*, int, int>)NativeLibrary.GetExport(library, "stbi_write_jpg_to_func");
            }
            var output = new MemoryStream();
            var handle = GCHandle.Alloc(output);
            try
            {
                fixed (byte* data = rows)
                    if (write(&Append, (void*)GCHandle.ToIntPtr(handle), width, height, channels, data, Math.Clamp(quality, 1, 100)) == 0)
                        return null;
            }
            finally
            {
                handle.Free();
            }
            return output.ToArray();
        }

        [UnmanagedCallersOnly]
        static void Append(void* context, void* data, int size)
            => ((MemoryStream)GCHandle.FromIntPtr((IntPtr)context).Target).Write(new ReadOnlySpan<byte>(data, size));
    }

    /// Uncompressed TGA writer: an 18-byte header (no image id, no colour map, no footer), then
    /// the rows as given.
    internal static class Tga
    {
        /// TGA of 8-bit rows, bottom row first: grey (image type 3), BGR or BGRA (type 2).
        public static byte[] Encode(int width, int height, int channels, byte[] rows)
        {
            var tga = new byte[18 + rows.Length];
            tga[2] = (byte)(channels == 1 ? 3 : 2);
            tga[12] = (byte)width;
            tga[13] = (byte)(width >> 8);
            tga[14] = (byte)height;
            tga[15] = (byte)(height >> 8);
            tga[16] = (byte)(channels * 8);
            rows.CopyTo(tga, 18);
            return tga;
        }
    }
}
