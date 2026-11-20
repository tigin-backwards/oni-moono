using System;
using OniMoono.Assets.TypeTrees;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// QualitySettings' texture settings (TextureQuality): the game's Low Res Textures option is
    /// quality level 1, whose global mipmap limit of 1 uploads loaded textures from their second
    /// level, and whose anisotropic mode turns anisotropic filtering off.
    public class TextureQualityTests : EngineTest
    {
        static Fields Level(int? limit, int? aniso, string limitName = "globalTextureMipmapLimit")
        {
            var f = new Fields();
            f.Add("name", "Low");
            if (limit is { } l) f.Add(limitName, l);
            if (aniso is { } a) f.Add("anisotropicTextures", a);
            return f;
        }

        /// A Texture2D as an asset file has it (texels inline), bound into a texture made here.
        static Texture2D Asset(int width, int height, TextureFormat format, int mips, bool readable, bool? ignoreLimit = false)
        {
            var t = new Texture2D(1, 1);
            long size = TextureFormats.ImageSize(format, width, height, mips);
            var image = new byte[size];
            new System.Random(width * 31 + height).NextBytes(image);
            var settings = new Fields();
            settings.Add("m_FilterMode", 1);
            settings.Add("m_Aniso", 4);
            settings.Add("m_MipBias", 0f);
            settings.Add("m_WrapU", 0);
            settings.Add("m_WrapV", 0);
            settings.Add("m_WrapW", 0);
            var stream = new Fields();
            stream.Add("offset", 0UL);
            stream.Add("size", 0U);
            stream.Add("path", "");
            var f = new Fields();
            f.Add("m_Name", "asset");
            f.Add("m_Width", width);
            f.Add("m_Height", height);
            f.Add("m_CompleteImageSize", (uint)size);
            f.Add("m_MipsStripped", 0);
            f.Add("m_TextureFormat", (int)format);
            f.Add("m_MipCount", mips);
            f.Add("m_IsReadable", readable);
            if (ignoreLimit is { } ignore) f.Add("m_IgnoreMipmapLimit", ignore);
            f.Add("m_ImageCount", 1);
            f.Add("m_TextureDimension", 2);
            f.Add("m_TextureSettings", settings);
            f.Add("m_ColorSpace", 1);
            f.Add("image data", image);
            f.Add("m_StreamData", stream);
            Native(t).Bind(f, null);
            return t;
        }

        static Texture2DNative Native(Texture2D t) => (Texture2DNative)t.Native;

        /// Runs `test` at a quality level's settings, and goes back to level 0's.
        static void At(int limit, int aniso, Action test)
        {
            TextureQuality.Apply(limit, aniso);
            try { test(); }
            finally { TextureQuality.Apply(0, TextureQuality.AnisoPerTexture); }
        }

        [Fact]
        public void LevelsAreRead()
        {
            Assert.Equal((1, 0), TextureQuality.Of(Level(1, 0)));
            Assert.Equal((0, 1), TextureQuality.Of(Level(0, 1)));
            // Unity before 2022.2 named the limit textureQuality.
            Assert.Equal((2, 2), TextureQuality.Of(Level(2, 2, "textureQuality")));
            Assert.Equal((0, TextureQuality.AnisoPerTexture), TextureQuality.Of(Level(null, null)));
            Assert.Equal(0, TextureQuality.Of(Level(-1, 1)).MipmapLimit);
        }

        [Fact]
        public void TheDefaultsChangeNothing()
        {
            Assert.Equal(0, TextureQuality.MipmapLimit);
            Assert.Equal(TextureQuality.AnisoPerTexture, TextureQuality.Anisotropic);
            var t = Asset(64, 32, TextureFormat.RGBA32, 7, readable: true);
            Assert.Equal(0, TextureQuality.SkippedLevels(Native(t)));
            foreach (int level in new[] { 0, 1, 4, 16 }) Assert.Equal(level, TextureQuality.Aniso(level));
        }

        [Fact]
        public void OnlyLoadedTexturesWithMipsThatDoNotIgnoreTheLimitFollowIt()
        {
            At(1, TextureQuality.AnisoPerTexture, () =>
            {
                Assert.Equal(1, TextureQuality.SkippedLevels(Native(Asset(64, 64, TextureFormat.DXT5, 7, readable: false))));
                Assert.Equal(1, TextureQuality.SkippedLevels(Native(Asset(64, 64, TextureFormat.RGBA32, 7, readable: true))));
                // Bundles older than Unity 2022.2 have no m_IgnoreMipmapLimit: they follow it.
                Assert.Equal(1, TextureQuality.SkippedLevels(Native(Asset(64, 64, TextureFormat.RGBA32, 7, readable: false, ignoreLimit: null))));
                Assert.Equal(0, TextureQuality.SkippedLevels(Native(Asset(64, 64, TextureFormat.RGBA32, 7, readable: false, ignoreLimit: true))));
                Assert.Equal(0, TextureQuality.SkippedLevels(Native(Asset(64, 64, TextureFormat.RGBA32, 1, readable: false))));
                // Textures made by scripts ignore it (Texture2D.ignoreMipmapLimit is true for them).
                var script = new Texture2D(64, 64, TextureFormat.RGBA32, true);
                Assert.True(script.mipmapCount > 1);
                Assert.Equal(0, TextureQuality.SkippedLevels(Native(script)));
            });
        }

        /// A loaded texture whose texels a script replaced, or made non-readable, is uploaded
        /// whole: its asset file no longer has its texels.
        [Fact]
        public void TexturesAScriptChangedAreUploadedWhole()
        {
            At(1, TextureQuality.AnisoPerTexture, () =>
            {
                var nonReadable = Asset(64, 64, TextureFormat.RGBA32, 7, readable: true);
                nonReadable.Apply(false, true);
                Assert.Equal(0, TextureQuality.SkippedLevels(Native(nonReadable)));
                var reinitialized = Asset(64, 64, TextureFormat.RGBA32, 7, readable: true);
                Assert.True(reinitialized.Reinitialize(32, 32, TextureFormat.RGBA32, true));
                Assert.Equal(0, TextureQuality.SkippedLevels(Native(reinitialized)));
                // Written with SetPixels and applied, it still follows the limit, as in Unity.
                var written = Asset(64, 64, TextureFormat.RGBA32, 7, readable: true);
                written.SetPixel(0, 0, Color.red);
                written.Apply();
                Assert.Equal(1, TextureQuality.SkippedLevels(Native(written)));
            });
        }

        [Fact]
        public void AtLeastOneLevelIsKept()
        {
            At(3, TextureQuality.AnisoPerTexture, () =>
            {
                Assert.Equal(1, TextureQuality.SkippedLevels(Native(Asset(4, 4, TextureFormat.RGBA32, 2, readable: false))));
                Assert.Equal(3, TextureQuality.SkippedLevels(Native(Asset(64, 64, TextureFormat.RGBA32, 7, readable: false))));
            });
        }

        [Fact]
        public void SizesAndTexelsStayTheAssets()
        {
            var t = Asset(64, 32, TextureFormat.RGBA32, 7, readable: true);
            var before = t.GetPixels32(0);
            At(1, TextureQuality.AnisoPerTexture, () =>
            {
                Assert.Equal(64, t.width);
                Assert.Equal(32, t.height);
                Assert.Equal(7, t.mipmapCount);
                Assert.Equal(new Vector2(1f / 64, 1f / 32), t.texelSize);
                Assert.Equal(before, t.GetPixels32(0));
                Assert.Equal(64 * 32, t.GetPixels(0).Length);
            });
        }

        [Fact]
        public void UploadsStartAtTheLimit()
        {
            foreach (var (format, w, h, mips) in new[] { (TextureFormat.RGBA32, 64, 32, 7), (TextureFormat.DXT5, 100, 60, 7), (TextureFormat.DXT1, 8, 8, 4), (TextureFormat.Alpha8, 33, 17, 6) })
                for (int skip = 1; skip < mips; skip++)
                {
                    var u = new TexelUpload { Format = format, Width = w, Height = h, Depth = 3, MipCount = mips, Target = GlConst.TexTarget2DArray };
                    u.SkipLevels(skip);
                    Assert.Equal(Math.Max(1, w >> skip), u.Width);
                    Assert.Equal(Math.Max(1, h >> skip), u.Height);
                    Assert.Equal(mips - skip, u.MipCount);
                    Assert.Equal(TextureFormats.ImageSize(format, w, h, mips), u.SliceBytes);
                    for (int m = 0; m < u.MipCount; m++)
                    {
                        Assert.Equal(TextureFormats.MipOffset(format, w, h, m + skip), u.MipOffset(m));
                        Assert.Equal(TextureFormats.MipSize(format, Math.Max(1, w >> (m + skip)), Math.Max(1, h >> (m + skip))), u.MipSize(m));
                    }
                }
            var none = new TexelUpload { Format = TextureFormat.RGBA32, Width = 64, Height = 32, Depth = 1, MipCount = 7, Target = GlConst.TexTarget2D };
            none.SkipLevels(0);
            Assert.Equal((64, 32, 7, 0L, 0L), (none.Width, none.Height, none.MipCount, none.Base, none.SliceStride));
        }

        [Fact]
        public void TakenUploadsLeaveTheLargestLevelsOut()
        {
            var t = Asset(64, 32, TextureFormat.RGBA32, 7, readable: true);
            var arena = new FrameArena();
            At(1, TextureQuality.AnisoPerTexture, () =>
            {
                var u = TexelUpload.Take(Native(t), arena);
                u.Copying.Wait();
                Assert.Equal((32, 16, 6), (u.Width, u.Height, u.MipCount));
                Assert.Equal(64L * 32 * 4, u.Base);
                UploadBuffers.Return(u.Memory);
            });
            arena.Reset();
            var full = TexelUpload.Take(Native(t), arena);
            full.Copying.Wait();
            Assert.Equal((64, 32, 7, 0L, 0L), (full.Width, full.Height, full.MipCount, full.Base, full.SliceStride));
            UploadBuffers.Return(full.Memory);
        }

        [Fact]
        public void ChangingTheLimitUploadsAffectedTexturesAgain()
        {
            var asset = Asset(64, 32, TextureFormat.RGBA32, 7, readable: true);
            var ignoring = Asset(64, 32, TextureFormat.RGBA32, 7, readable: true, ignoreLimit: true);
            var script = new Texture2D(64, 64, TextureFormat.RGBA32, true);
            var notUploaded = Asset(64, 32, TextureFormat.RGBA32, 7, readable: true);
            foreach (var t in new[] { asset, ignoring, script }) Native(t).Gpu = new object();
            try
            {
                int a = Native(asset).ContentVersion, i = Native(ignoring).ContentVersion, s = Native(script).ContentVersion, n = Native(notUploaded).ContentVersion;
                At(1, TextureQuality.AnisoPerTexture, () =>
                {
                    Assert.Equal(a + 1, Native(asset).ContentVersion);
                    Assert.Equal(i, Native(ignoring).ContentVersion);
                    Assert.Equal(s, Native(script).ContentVersion);
                    // Not on the device yet: its first upload takes the limit.
                    Assert.Equal(n, Native(notUploaded).ContentVersion);
                });
                // And back.
                Assert.Equal(a + 2, Native(asset).ContentVersion);
                // The same level again changes nothing.
                TextureQuality.Apply(0, TextureQuality.AnisoPerTexture);
                Assert.Equal(a + 2, Native(asset).ContentVersion);
            }
            finally
            {
                foreach (var t in new[] { asset, ignoring, script }) Native(t).Gpu = null;
            }
        }

        [Fact]
        public void AnisotropicModes()
        {
            var t = Asset(64, 32, TextureFormat.RGBA32, 7, readable: true);
            int sampler = Native(t).SamplerVersion;
            At(1, TextureQuality.AnisoDisable, () =>
            {
                Assert.True(Native(t).SamplerVersion > sampler);
                foreach (int level in new[] { 0, 1, 4, 16 }) Assert.Equal(1, TextureQuality.Aniso(level));
                // anisoLevel still reads the texture's own.
                Assert.Equal(4, t.anisoLevel);
            });
            At(0, TextureQuality.AnisoForceEnable, () =>
            {
                Assert.Equal(0, TextureQuality.Aniso(0));
                Assert.Equal(9, TextureQuality.Aniso(1));
                Assert.Equal(9, TextureQuality.Aniso(9));
                Assert.Equal(12, TextureQuality.Aniso(12));
                Assert.Equal(16, TextureQuality.Aniso(20));
            });
        }
    }
}
