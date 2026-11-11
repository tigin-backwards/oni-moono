using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Brutal.StbApi;
using OniMoono.Engine;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Xunit;

namespace OniMoono.Tests
{
    /// The PNG writer (Engine/PngWriter.cs): adaptive filters, level-9 deflate in bands of rows,
    /// one IDAT a band. Every file is read back by a decoder of the test's own (each chunk's CRC,
    /// the zlib stream's Adler-32, each row's filter undone) and by stb, and must give the rows
    /// written.
    public class PngWriterTests : EngineTest
    {
        /// Rows of a test picture: smooth gradients, flat runs and noise, so that every filter
        /// wins somewhere and deflate has matches and literals.
        static byte[] Picture(int w, int h, int channels, int seed)
        {
            var random = new System.Random(seed);
            var rows = new byte[w * h * channels];
            for (int y = 0; y < h; y++)
            {
                int kind = (y / 7 + seed) % 4;
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < channels; c++)
                    {
                        int i = (y * w + x) * channels + c;
                        rows[i] = kind switch
                        {
                            0 => (byte)(x * 3 + c * 40),
                            1 => (byte)(y * 2 + x / 5),
                            2 => (byte)random.Next(256),
                            _ => (byte)((x / 9 + y / 4) % 2 * 200 + c),
                        };
                    }
            }
            return rows;
        }

        static byte[] Write(int w, int h, int channels, byte[] rows, Stream into = null)
        {
            using var png = new PngWriter(into, w, h, channels);
            for (int y = 0; y < h; y++) png.WriteRow(rows.AsSpan(y * w * channels, w * channels));
            png.Finish();
            return into == null ? png.ToArray() : null;
        }

        /// A PNG read by the test: its size, colour type, each row's filter type and the rows.
        static (int W, int H, int ColourType, List<int> Filters, byte[] Rows, int Idats) Read(byte[] png)
        {
            Assert.Equal(PngWriter.Signature.ToArray(), png[..8]);
            int at = 8, w = 0, h = 0, colour = -1, idats = 0;
            using var zlib = new MemoryStream();
            bool ended = false;
            while (at < png.Length)
            {
                int length = BigEndian(png, at);
                string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
                var data = png.AsSpan(at + 8, length);
                Assert.Equal((uint)BigEndian(png, at + 8 + length), Crc32.Compute(png.AsSpan(at + 4, length + 4)));
                if (type == "IHDR")
                {
                    w = BigEndian(png, at + 8);
                    h = BigEndian(png, at + 12);
                    Assert.Equal(8, data[8]);
                    colour = data[9];
                }
                else if (type == "IDAT")
                {
                    zlib.Write(data);
                    idats++;
                }
                else if (type == "IEND") ended = true;
                at += 12 + length;
            }
            Assert.True(ended);
            Assert.Equal(png.Length, at);
            int channels = colour switch { 0 => 1, 2 => 3, 6 => 4, _ => throw new InvalidDataException($"colour type {colour}") };
            var stream = zlib.ToArray();
            Assert.Equal(0x78, stream[0]);
            Assert.Equal(0, (stream[0] << 8 | stream[1]) % 31);
            byte[] filtered;
            using (var inflate = new DeflateStream(new MemoryStream(stream, 2, stream.Length - 6), CompressionMode.Decompress))
            using (var all = new MemoryStream())
            {
                inflate.CopyTo(all);
                filtered = all.ToArray();
            }
            Assert.Equal((uint)BigEndian(stream, stream.Length - 4), Adler32.Compute(filtered));
            int rowBytes = w * channels;
            Assert.Equal((long)h * (rowBytes + 1), filtered.Length);
            var rows = new byte[h * rowBytes];
            var filters = new List<int>();
            for (int y = 0; y < h; y++)
            {
                int type = filtered[y * (rowBytes + 1)];
                filters.Add(type);
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= channels ? rows[y * rowBytes + i - channels] : 0;
                    int b = y > 0 ? rows[(y - 1) * rowBytes + i] : 0;
                    int c = i >= channels && y > 0 ? rows[(y - 1) * rowBytes + i - channels] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    int predicted = type switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c,
                        _ => throw new InvalidDataException($"filter {type}"),
                    };
                    rows[y * rowBytes + i] = (byte)(filtered[y * (rowBytes + 1) + 1 + i] + predicted);
                }
            }
            return (w, h, colour, filters, rows, idats);
        }

        static int BigEndian(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];

        [Theory]
        [InlineData(1, 1, 4)]
        [InlineData(7, 3, 3)]
        [InlineData(33, 20, 1)]
        [InlineData(600, 1000, 4)]
        [InlineData(5, 4000, 3)]
        [InlineData(3000, 9, 1)]
        public void TheFileDecodesToTheRowsWritten(int w, int h, int channels)
        {
            var rows = Picture(w, h, channels, seed: w + h);
            var png = Write(w, h, channels, rows);
            var read = Read(png);
            Assert.Equal((w, h, channels switch { 1 => 0, 3 => 2, _ => 6 }), (read.W, read.H, read.ColourType));
            Assert.Equal(rows, read.Rows);
            // A band an IDAT, and one for the Adler-32.
            int bandRows = Math.Clamp(PngWriter.BandBytes / (w * channels + 1), 1, h);
            Assert.Equal((h + bandRows - 1) / bandRows + 1, read.Idats);
            using var stb = Stb.LoadFromMemory(png, channels);
            Assert.False(stb.IsNull());
            Assert.Equal(rows, stb.AsSpan().ToArray());
        }

        [Fact]
        public void SeveralBandsUseSeveralFilters()
        {
            const int w = 600, h = 1000;
            var read = Read(Write(w, h, 4, Picture(w, h, 4, seed: 3)));
            Assert.True(read.Idats > 2);
            Assert.True(new HashSet<int>(read.Filters).Count >= 3, string.Join(",", read.Filters));
        }

        /// libpng's heuristic: the filter whose output, bytes taken signed, sums to the least
        /// in absolute value; the first of equal ones.
        [Fact]
        public void EachRowTakesTheFilterWhoseOutputSumsToTheLeast()
        {
            const int w = 61, h = 90, channels = 4;
            var rows = Picture(w, h, channels, seed: 9);
            var read = Read(Write(w, h, channels, rows));
            int rowBytes = w * channels;
            for (int y = 0; y < h; y++)
            {
                var sums = new long[5];
                for (int f = 0; f < 5; f++)
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int x = rows[y * rowBytes + i];
                        int a = i >= channels ? rows[y * rowBytes + i - channels] : 0;
                        int b = y > 0 ? rows[(y - 1) * rowBytes + i] : 0;
                        int c = i >= channels && y > 0 ? rows[(y - 1) * rowBytes + i - channels] : 0;
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        int predicted = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => pa <= pb && pa <= pc ? a : pb <= pc ? b : c };
                        sums[f] += Math.Abs((int)(sbyte)(byte)(x - predicted));
                    }
                int best = 0;
                for (int f = 1; f < 5; f++)
                    if (sums[f] < sums[best]) best = f;
                Assert.True(best == read.Filters[y], $"row {y}: filter {read.Filters[y]}, sums {string.Join(", ", sums)}");
            }
        }

        [Fact]
        public void AFileWrittenIntoAStreamHasTheBytesKeptInMemory()
        {
            const int w = 700, h = 1300;
            var rows = Picture(w, h, 4, seed: 75);
            var kept = Write(w, h, 4, rows);
            using var stream = new MemoryStream();
            Write(w, h, 4, rows, stream);
            Assert.Equal(kept, stream.ToArray());
            // The same rows give the same bytes every time.
            Assert.Equal(kept, Write(w, h, 4, rows));
        }

        [Fact]
        public void RgbaRowsCanBeWrittenWithoutTheirAlpha()
        {
            const int w = 40, h = 30;
            var rgba = Picture(w, h, 4, seed: 4);
            using var png = new PngWriter(null, w, h, 3, inputChannels: 4);
            for (int y = 0; y < h; y++) png.WriteRow(rgba.AsSpan(y * w * 4, w * 4));
            png.Finish();
            var read = Read(png.ToArray());
            Assert.Equal(2, read.ColourType);
            for (int i = 0; i < w * h; i++)
                Assert.Equal((rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]), (read.Rows[i * 3], read.Rows[i * 3 + 1], read.Rows[i * 3 + 2]));
        }

        [Fact]
        public void AdlerChecksumsCombine()
        {
            var data = Picture(97, 89, 3, seed: 1);
            uint whole = Adler32.Compute(data);
            foreach (int cut in new[] { 0, 1, 5552, 5553, 12345, data.Length })
                Assert.Equal(whole, Adler32.Combine(Adler32.Compute(data.AsSpan(0, cut)), Adler32.Compute(data.AsSpan(cut)), data.Length - cut));
        }

        [Fact]
        public void Crc32MatchesTheBytewiseDefinition()
        {
            var data = Picture(53, 41, 3, seed: 2);
            uint c = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            Assert.Equal(~c, Crc32.Compute(data));
            Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        }

        // ----- EncodeToPNG and EncodeArrayToPNG -----

        static Texture2D Texture(int w, int h, TextureFormat format, byte[] texels)
        {
            var t = new Texture2D(w, h, format, false);
            t.LoadRawTextureData(texels);
            t.Apply();
            return t;
        }

        [Fact]
        public void AnOpaqueRgbaTextureIsWrittenAsRgbAndLoadsAsTheSameTexels()
        {
            const int w = 64, h = 48;
            foreach (var format in new[] { TextureFormat.RGBA32, TextureFormat.ARGB32, TextureFormat.BGRA32 })
            {
                var texels = Picture(w, h, 4, seed: 5);
                // Alpha is the first byte of ARGB32's texels, the last of the others'.
                for (int i = format == TextureFormat.ARGB32 ? 0 : 3; i < texels.Length; i += 4) texels[i] = 255;
                var source = Texture(w, h, format, texels);
                var png = source.EncodeToPNG();
                Assert.Equal(2, Read(png).ColourType);
                var loaded = new Texture2D(2, 2);
                Assert.True(loaded.LoadImage(png));
                Assert.Equal(source.GetPixels32(), loaded.GetPixels32());
            }
        }

        [Fact]
        public void ATextureWithAnyTranslucentTexelKeepsItsAlpha()
        {
            const int w = 64, h = 48;
            var texels = Picture(w, h, 4, seed: 6);
            for (int i = 3; i < texels.Length; i += 4) texels[i] = 255;
            texels[(37 * w + 11) * 4 + 3] = 254;
            var source = Texture(w, h, TextureFormat.RGBA32, texels);
            var png = source.EncodeToPNG();
            var read = Read(png);
            Assert.Equal(6, read.ColourType);
            var loaded = new Texture2D(2, 2);
            Assert.True(loaded.LoadImage(png));
            Assert.Equal(source.GetPixels32(), loaded.GetPixels32());
        }

        /// The array encoder reads rows bottom to top, `rowBytes` apart, straight from the array:
        /// padded rows give the packed rows' file.
        [Fact]
        public void ArrayRowsWithPaddingGiveThePackedRowsFile()
        {
            const int w = 300, h = 200, pad = 12;
            var packed = Picture(w, h, 4, seed: 7);
            var padded = new byte[h * (w * 4 + pad)];
            for (int y = 0; y < h; y++) packed.AsSpan(y * w * 4, w * 4).CopyTo(padded.AsSpan(y * (w * 4 + pad)));
            var a = ImageConversion.EncodeArrayToPNG(packed, GraphicsFormat.R8G8B8A8_UNorm, w, h);
            var b = ImageConversion.EncodeArrayToPNG(padded, GraphicsFormat.R8G8B8A8_UNorm, w, h, w * 4 + pad);
            Assert.Equal(a, b);
            var read = Read(a);
            for (int y = 0; y < h; y++)
                Assert.Equal(packed.AsSpan((h - 1 - y) * w * 4, w * 4).ToArray(), read.Rows.AsSpan(y * w * 4, w * 4).ToArray());
            // And the texture encoder writes the same texels the same way.
            Assert.Equal(a, Texture(w, h, TextureFormat.RGBA32, packed).EncodeToPNG());
        }

        /// A timelapse-sized image: many bands, each encoded on its own thread, in one file.
        [Fact]
        public void ALargeImageIsWrittenInBandsThatDecodeAsOne()
        {
            const int w = 2048, h = 3072;
            var texels = Picture(w, h, 4, seed: 8);
            for (int i = 3; i < texels.Length; i += 4) texels[i] = 255;
            var png = ImageConversion.EncodeArrayToPNG(texels, GraphicsFormat.R8G8B8A8_UNorm, w, h);
            var read = Read(png);
            Assert.Equal(2, read.ColourType);
            Assert.True(read.Idats > 8);
            for (int y = 0; y < h; y += 97)
                for (int x = 0; x < w; x += 13)
                {
                    int s = ((h - 1 - y) * w + x) * 4, d = (y * w + x) * 3;
                    Assert.Equal((texels[s], texels[s + 1], texels[s + 2]), (read.Rows[d], read.Rows[d + 1], read.Rows[d + 2]));
                }
        }
    }
}
