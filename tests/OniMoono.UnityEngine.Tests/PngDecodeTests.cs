using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// LoadImage's own PNG decoder (src/OniMoono.UnityEngine/Core/PngDecode.cs) against stb_image,
    /// which decoded every PNG before it and still decodes what it leaves: the same answer, size,
    /// format and texels for every file, whichever of the two decodes it. Generated files cover
    /// each filter, colour type and chunk layout, and the files it must leave to stb (and those
    /// both refuse); ONI_PNG_CORPUS (directories, ':'-separated) adds every .png under them.
    public class PngDecodeTests(ITestOutputHelper output) : EngineTest
    {
        /// What LoadImage made of `data`, through LoadImage (ours where it applies) or stb alone.
        static (bool Ok, int W, int H, TextureFormat Format, int Mips, byte[] Texels) Load(byte[] data, bool stbOnly, bool mips = false)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mips);
            bool ok = stbOnly ? ImageConversion.LoadWithStb(tex, data, png: true, markNonReadable: false) : tex.LoadImage(data);
            var native = (Texture2DNative)tex.NativeOrThrow;
            var texels = native.Memory.Span.ToArray();
            var result = (ok, tex.width, tex.height, tex.format, native.MipCount, texels);
            UnityEngine.Object.DestroyImmediate(tex);
            return result;
        }

        /// Whether LoadImage's own decoder decodes the file (rather than leaving it to stb).
        static bool Ours(byte[] png)
        {
            if (!PngDecode.TryParse(png, out var image)) return false;
            return PngDecode.TryDecode(png, image, new byte[(long)image.Width * image.Height * 4]);
        }

        static void AssertSame(byte[] png, string name, bool mips = false)
        {
            var stb = Load(png, stbOnly: true, mips);
            var ours = Load(png, stbOnly: false, mips);
            Assert.True(stb.Ok == ours.Ok, $"{name}: stb {(stb.Ok ? "decodes" : "refuses")} it, LoadImage {(ours.Ok ? "decodes" : "refuses")} it");
            Assert.True((stb.W, stb.H, stb.Format, stb.Mips) == (ours.W, ours.H, ours.Format, ours.Mips), $"{name}: {stb.W}x{stb.H} {stb.Format} {stb.Mips} mips against {ours.W}x{ours.H} {ours.Format} {ours.Mips}");
            if (!stb.Texels.AsSpan().SequenceEqual(ours.Texels))
            {
                int i = 0;
                while (stb.Texels[i] == ours.Texels[i]) i++;
                Assert.Fail($"{name}: texel byte {i} of {stb.Texels.Length} is {ours.Texels[i]}, stb's {stb.Texels[i]}");
            }
        }

        // ---- A PNG writer for the tests: any colour type and depth, chosen filters, chunks.

        sealed class Png
        {
            public int Width, Height, Depth = 8, Color = 6, Interlace;
            public byte[] Pixels;           // packed rows, as the file's channels and depth
            public Func<int, int> Filter = y => y % 5;
            public int IdatSplit;           // > 0: IDAT chunks of this size
            public List<(string Type, byte[] Data)> Before = new(), After = new();
            public Func<byte[], byte[]> Zlib = raw => Compress(raw);
            public bool NoIend;

            int Channels => Color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
            public int RowBytes => (Width * Channels * Depth + 7) / 8;

            public static byte[] Compress(byte[] raw, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var output = new MemoryStream();
                using (var z = new ZLibStream(output, level, leaveOpen: true)) z.Write(raw);
                return output.ToArray();
            }

            public byte[] Filtered()
            {
                int bpp = Math.Max(1, Channels * Depth / 8), stride = RowBytes;
                var raw = new byte[Height * (stride + 1)];
                for (int y = 0; y < Height; y++)
                {
                    int f = Filter(y);
                    raw[y * (stride + 1)] = (byte)f;
                    for (int i = 0; i < stride; i++)
                    {
                        int x = Pixels[y * stride + i];
                        int a = i >= bpp ? Pixels[y * stride + i - bpp] : 0;
                        int b = y > 0 ? Pixels[(y - 1) * stride + i] : 0;
                        int c = y > 0 && i >= bpp ? Pixels[(y - 1) * stride + i - bpp] : 0;
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        int predictor = f switch { 1 => a, 2 => b, 3 => (a + b) >> 1, 4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c, _ => 0 };
                        raw[y * (stride + 1) + 1 + i] = (byte)(x - predictor);
                    }
                }
                return raw;
            }

            public byte[] Bytes()
            {
                using var s = new MemoryStream();
                s.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
                var ihdr = new byte[13];
                BigEndian(ihdr, 0, Width);
                BigEndian(ihdr, 4, Height);
                ihdr[8] = (byte)Depth;
                ihdr[9] = (byte)Color;
                ihdr[12] = (byte)Interlace;
                Chunk(s, "IHDR", ihdr);
                foreach (var (type, data) in Before) Chunk(s, type, data);
                var z = Zlib(Filtered());
                int split = IdatSplit > 0 ? IdatSplit : Math.Max(1, z.Length);
                for (int i = 0; i < z.Length; i += split) Chunk(s, "IDAT", z[i..Math.Min(z.Length, i + split)]);
                foreach (var (type, data) in After) Chunk(s, type, data);
                if (!NoIend) Chunk(s, "IEND", []);
                return s.ToArray();
            }

            static void Chunk(Stream s, string type, byte[] data)
            {
                var length = new byte[4];
                BigEndian(length, 0, data.Length);
                s.Write(length);
                var t = System.Text.Encoding.ASCII.GetBytes(type);
                s.Write(t);
                s.Write(data);
                var crc = new Crc32();
                crc.Append(t);
                crc.Append(data);
                var c = new byte[4];
                BigEndian(c, 0, (int)crc.Current);
                s.Write(c);
            }

            static void BigEndian(byte[] b, int at, int v)
            {
                b[at] = (byte)(v >> 24);
                b[at + 1] = (byte)(v >> 16);
                b[at + 2] = (byte)(v >> 8);
                b[at + 3] = (byte)v;
            }
        }

        static Png Image(int w, int h, int color, int depth = 8, int seed = 1, double noise = 0.3)
        {
            var png = new Png { Width = w, Height = h, Color = color, Depth = depth };
            var rnd = new System.Random(seed);
            png.Pixels = new byte[h * png.RowBytes];
            for (int i = 0; i < png.Pixels.Length; i++)
                png.Pixels[i] = rnd.NextDouble() < noise ? (byte)rnd.Next(256) : (byte)(i * 7 / 3 + i / png.RowBytes * 5);
            return png;
        }

        [Theory]
        [InlineData(6)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(0)]
        public void EachFilterAndColourTypeDecodesAsStbDoes(int color)
        {
            foreach (var (w, h) in new[] { (1, 1), (1, 9), (9, 1), (3, 7), (17, 5), (64, 64), (528, 306), (531, 211) })
                for (int scheme = 0; scheme < 7; scheme++)
                {
                    var png = Image(w, h, color, seed: w * 1000 + h * 10 + scheme);
                    png.Filter = scheme < 5 ? (_ => scheme) : scheme == 5 ? (y => y % 5) : (y => (y * 7 + 3) % 5);
                    var bytes = png.Bytes();
                    Assert.True(Ours(bytes), $"{w}x{h} colour {color} is ours to decode");
                    AssertSame(bytes, $"{w}x{h} colour {color} filters {scheme}");
                }
        }

        [Fact]
        public void ChunkLayoutsDecodeAsStbDoes()
        {
            var text = System.Text.Encoding.ASCII.GetBytes("Comment\0made for a test");
            var cases = new Dictionary<string, Action<Png>>
            {
                ["one IDAT"] = _ => { },
                ["IDATs of 1 byte"] = p => p.IdatSplit = 1,
                ["IDATs of 100 bytes"] = p => p.IdatSplit = 100,
                ["ancillary chunks before and after"] = p => { p.Before.Add(("tEXt", text)); p.Before.Add(("gAMA", [0, 0, 0xB1, 0x8F])); p.After.Add(("tIME", new byte[7])); },
                ["an empty IDAT"] = p => p.Before.Add(("IDAT", [])),
                ["stored blocks"] = p => p.Zlib = raw => Png.Compress(raw, CompressionLevel.NoCompression),
                ["fastest"] = p => p.Zlib = raw => Png.Compress(raw, CompressionLevel.Fastest),
                ["mip chain"] = _ => { },
            };
            foreach (var (name, change) in cases)
            {
                var png = Image(97, 61, 6, seed: name.Length);
                change(png);
                var bytes = png.Bytes();
                Assert.True(Ours(bytes), name);
                AssertSame(bytes, name, mips: name == "mip chain");
            }
        }

        /// Files this decoder leaves to stb, or that both refuse: LoadImage's answer is stb's.
        [Fact]
        public void WhatItLeavesToStbLoadsAsStbLoadsIt()
        {
            var cases = new Dictionary<string, Func<byte[]>>
            {
                ["palette"] = () => { var p = Image(40, 30, 3); p.Pixels = p.Pixels.Select(b => (byte)(b % 4)).ToArray(); p.Before.Add(("PLTE", Enumerable.Range(0, 12).Select(i => (byte)(i * 20)).ToArray())); return p.Bytes(); },
                ["palette with tRNS"] = () => { var p = Image(40, 30, 3); p.Pixels = p.Pixels.Select(b => (byte)(b % 4)).ToArray(); p.Before.Add(("PLTE", new byte[12])); p.Before.Add(("tRNS", [0, 128])); return p.Bytes(); },
                ["RGB with tRNS"] = () => { var p = Image(40, 30, 2); p.Before.Add(("tRNS", [0, 7, 0, 14, 0, 21])); return p.Bytes(); },
                ["grey with tRNS"] = () => { var p = Image(40, 30, 0); p.Before.Add(("tRNS", [0, 9])); return p.Bytes(); },
                ["16-bit RGBA"] = () => Image(33, 21, 6, depth: 16).Bytes(),
                ["16-bit grey"] = () => Image(33, 21, 0, depth: 16).Bytes(),
                ["1-bit grey"] = () => Image(33, 21, 0, depth: 1).Bytes(),
                ["2-bit grey"] = () => Image(33, 21, 0, depth: 2).Bytes(),
                ["4-bit grey"] = () => Image(33, 21, 0, depth: 4).Bytes(),
                ["RGB with a PLTE"] = () => { var p = Image(40, 30, 2); p.Before.Add(("PLTE", new byte[12])); return p.Bytes(); },
                ["a wrong Adler-32"] = () => { var p = Image(40, 30, 6); p.Zlib = raw => { var z = Png.Compress(raw); z[^1] ^= 0x55; return z; }; return p.Bytes(); },
                ["a filter type 5"] = () => { var p = Image(40, 30, 6); p.Filter = y => y == 3 ? 5 : 1; return p.Bytes(); },
                ["too few rows"] = () => { var p = Image(40, 30, 6); var full = p.Filtered(); p.Zlib = raw => Png.Compress(raw[..(raw.Length - 41)]); return p.Bytes(); },
                ["a row too many"] = () => { var p = Image(40, 30, 6); p.Zlib = raw => Png.Compress(raw.Concat(new byte[161]).ToArray()); return p.Bytes(); },
                ["a truncated zlib stream"] = () => { var p = Image(40, 30, 6); p.Zlib = raw => Png.Compress(raw)[..^20]; return p.Bytes(); },
                ["zlib with a preset dictionary flag"] = () => { var p = Image(40, 30, 6); p.Zlib = raw => { var z = Png.Compress(raw); z[1] = 0xBB; return z; }; return p.Bytes(); },
                ["no IEND"] = () => { var p = Image(40, 30, 6); p.NoIend = true; return p.Bytes(); },
                ["IEND without its CRC"] = () => { var b = Image(40, 30, 6).Bytes(); return b[..^4]; },
                ["an unknown critical chunk"] = () => { var p = Image(40, 30, 6); p.Before.Add(("ABCD", [1, 2, 3])); return p.Bytes(); },
                ["a second IHDR"] = () => { var p = Image(40, 30, 6); p.Before.Add(("IHDR", new byte[13])); return p.Bytes(); },
                ["a chunk past the end"] = () => { var b = Image(40, 30, 6).Bytes(); return b[..^30]; },
                ["data after IEND"] = () => Image(40, 30, 6).Bytes().Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }).ToArray(),
                ["a wrong CRC"] = () => { var b = Image(40, 30, 6).Bytes(); b[29] ^= 1; return b; },
                ["no IDAT"] = () => { var p = Image(40, 30, 6); var b = p.Bytes(); return b[..33].Concat(b[^12..]).ToArray(); },
                ["interlaced"] = () => { var p = Image(1, 1, 6); p.Interlace = 1; return p.Bytes(); },
                ["a bad compression method"] = () => { var b = Image(40, 30, 6).Bytes(); b[8 + 8 + 10] = 1; return b; },
                ["width 0"] = () => { var b = Image(40, 30, 6).Bytes(); b[16] = b[17] = b[18] = b[19] = 0; return b; },
                ["just the signature"] = () => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A],
            };
            foreach (var (name, make) in cases)
                AssertSame(make(), name);
        }

        /// ONI_PNG_CORPUS: every PNG under the directories named (read only), and the synthetic
        /// Load screen previews when out/load-screen-saves has them.
        [Fact]
        public void TheCorpusDecodesAsStbDoes()
        {
            var dirs = (Environment.GetEnvironmentVariable("ONI_PNG_CORPUS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../out/load-screen-saves/synthetic"));
            var files = new List<string>();
            // The tree's previews are hard links to the few PNGs at its top.
            if (Directory.Exists(root)) files.AddRange(Directory.GetFiles(root, "*.png"));
            foreach (var dir in dirs) files.AddRange(Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories));
            int ours = 0;
            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file);
                if (Ours(bytes)) ours++;
                AssertSame(bytes, file);
            }
            if (files.Count > 0) output.WriteLine($"PNG corpus: {files.Count} files, {ours} of them for LoadImage's own decoder, all as stb decodes them");
        }

        /// While ImageDecodeMemo is open, the same bytes loaded again come from it: the same
        /// answer, size, format, mips and texels as a decode, into the caller's own texture.
        [Fact]
        public void TheDecodeMemoGivesWhatADecodeGives()
        {
            var png = Image(120, 80, 6, seed: 5).Bytes();
            var other = Image(120, 80, 2, seed: 6).Bytes();
            var jpg = new Texture2D(16, 16, TextureFormat.RGB24, false).EncodeToJPG();
            var expected = new[] { png, other, jpg }.Select(b => Load(b, stbOnly: false)).ToArray();
            var expectedMips = Load(png, stbOnly: false, mips: true);
            ImageDecodeMemo.Open();
            try
            {
                for (int round = 0; round < 3; round++)
                {
                    var got = new[] { png, other, jpg }.Select(b => Load((byte[])b.Clone(), stbOnly: false)).ToArray();
                    for (int i = 0; i < got.Length; i++)
                    {
                        Assert.Equal((expected[i].Ok, expected[i].W, expected[i].H, expected[i].Format, expected[i].Mips), (got[i].Ok, got[i].W, got[i].H, got[i].Format, got[i].Mips));
                        Assert.Equal(expected[i].Texels, got[i].Texels);
                    }
                    var mips = Load(png, stbOnly: false, mips: true);
                    Assert.Equal(expectedMips.Mips, mips.Mips);
                    Assert.Equal(expectedMips.Texels, mips.Texels);
                }
                Assert.Equal(3, ImageDecodeMemo.Decodes);
                Assert.Equal(9, ImageDecodeMemo.Hits);
                // Each load is the caller's own texture: changing one changes no other.
                var a = new Texture2D(2, 2);
                var b = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                Assert.True(a.LoadImage(png));
                Assert.True(b.LoadImage(png, markNonReadable: true));
                Assert.False(b.isReadable);
                a.SetPixel(0, 0, Color.red);
                a.Apply();
                var c = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                Assert.True(c.LoadImage(png));
                Assert.Equal(expected[0].Texels, ((Texture2DNative)c.NativeOrThrow).Memory.Span.ToArray());
                // Bytes that differ in one place are another image.
                var changed = (byte[])png.Clone();
                changed[^20] ^= 1;
                var d = new Texture2D(2, 2);
                Assert.Equal(Load(changed, stbOnly: true).Ok, d.LoadImage(changed));
                foreach (var t in new[] { a, b, c, d }) UnityEngine.Object.DestroyImmediate(t);
            }
            finally
            {
                ImageDecodeMemo.Close();
            }
            // Closed, it keeps nothing.
            Load(png, stbOnly: false);
            Assert.False(ImageDecodeMemo.TryGet(png, out _, out _, out _, out _));
        }

        /// ONI_PNG_BENCH=FILE: decode times of stb and LoadImage, for docs/research.
        [Fact]
        public void Bench()
        {
            var file = Environment.GetEnvironmentVariable("ONI_PNG_BENCH");
            if (string.IsNullOrEmpty(file)) return;
            var bytes = File.ReadAllBytes(file);
            foreach (bool stb in new[] { true, false, true, false })
            {
                var times = new List<double>();
                for (int i = 0; i < 20; i++)
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (stb) ImageConversion.LoadWithStb(tex, bytes, png: true, markNonReadable: false);
                    else tex.LoadImage(bytes);
                    times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    UnityEngine.Object.DestroyImmediate(tex);
                }
                times.Sort();
                if (!stb && PngDecode.TryParse(bytes, out var image))
                {
                    var buffer = new byte[image.Width * image.Height * 4];
                    var parts = new List<double>();
                    for (int i = 0; i < 20; i++)
                    {
                        long start = System.Diagnostics.Stopwatch.GetTimestamp();
                        PngDecode.TryDecode(bytes, image, buffer);
                        parts.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                    var raw = new byte[image.Height * (image.Width * image.Channels + 1)];
                    var inflate = new List<double>();
                    for (int i = 0; i < 20; i++)
                    {
                        long start = System.Diagnostics.Stopwatch.GetTimestamp();
                        PngDecode.Inflate(bytes, image.Data, raw);
                        inflate.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                    parts.Sort();
                    inflate.Sort();
                    var filters = new int[5];
                    for (int y = 0; y < image.Height; y++) if (raw[y * (image.Width * image.Channels + 1)] < 5) filters[raw[y * (image.Width * image.Channels + 1)]]++;
                    output.WriteLine($"{image.Width}x{image.Height}x{image.Channels}: TryDecode median {parts[10]:F2} ms, of it inflate {inflate[10]:F2} ms; row filters {string.Join(",", filters)}");
                }
                output.WriteLine($"{(stb ? "stb" : "ours")}: median {times[10]:F2} ms, best {times[0]:F2} ms ({Path.GetFileName(file)}, {bytes.Length} bytes)");
            }
        }
    }
}
