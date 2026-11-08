using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Xunit;

namespace OniMoono.Tests
{
    /// RenderTexture's constructors as measured in the original (UnityProbe's apigaps "rtctor",
    /// GLCore, gamma colour space).
    public class RenderTextureConstructorTests : EngineTest, IDisposable
    {
        readonly List<string> logged = new();

        void OnLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Log) logged.Add(type + " " + message);
        }

        /// The colour space comes from the project's player settings, loaded here: an earlier
        /// test that loaded them is not to be relied on.
        public RenderTextureConstructorTests()
        {
            OniMoono.Engine.Assets.AssetSystem.Initialize(System.IO.Path.Combine(TmpTestSupport.GameDir, "OxygenNotIncluded_Data"));
            OniMoono.Engine.Assets.ProjectSettings.Load();
            Application.logMessageReceived += OnLog;
        }

        void IDisposable.Dispose()
        {
            Application.logMessageReceived -= OnLog;
            Dispose();
        }

        static string Describe(RenderTexture rt)
        {
            var d = rt.descriptor;
            return $"{rt.width}x{rt.height} depth={rt.depth} format={rt.format} gf={rt.graphicsFormat} ds={rt.depthStencilFormat} sRGB={rt.sRGB} mips={rt.mipmapCount} | mipCount={d.mipCount} ds={d.depthStencilFormat} bits={d.depthBufferBits}";
        }

        [Fact]
        public void ReadWriteAsksForSrgbOnlyInLinearSpace()
        {
            foreach (RenderTextureReadWrite rw in Enum.GetValues(typeof(RenderTextureReadWrite)))
            {
                var rt = new RenderTexture(16, 8, 24, RenderTextureFormat.ARGB32, rw);
                Assert.Equal("16x8 depth=24 format=ARGB32 gf=R8G8B8A8_UNorm ds=D24_UNorm_S8_UInt sRGB=False mips=1 | mipCount=-1 ds=D24_UNorm_S8_UInt bits=24", Describe(rt));
            }
            var half = new RenderTexture(16, 8, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.sRGB);
            Assert.Equal("16x8 depth=24 format=ARGBHalf gf=R16G16B16A16_SFloat ds=D24_UNorm_S8_UInt sRGB=False mips=1 | mipCount=-1 ds=D24_UNorm_S8_UInt bits=24", Describe(half));
        }

        [Fact]
        public void Bgra32FallsBackToArgb32WithAWarning()
        {
            var rt = new RenderTexture(16, 8, 0, RenderTextureFormat.BGRA32, 2);
            Assert.Equal(RenderTextureFormat.ARGB32, rt.format);
            Assert.Equal(GraphicsFormat.R8G8B8A8_UNorm, rt.graphicsFormat);
            Assert.Contains("Warning 'B8G8R8A8_UNorm' is not supported. RenderTexture::GetTemporary fallbacks to R8G8B8A8_UNorm format on this platform. Use 'SystemInfo.IsFormatSupported' C# API to check format support.", logged);
        }

        [Fact]
        public void MipCountIsTheDescriptorsAndCreateRefusesTooMany()
        {
            var rt = new RenderTexture(16, 8, 24, RenderTextureFormat.ARGB32, 3);
            Assert.Equal(1, rt.mipmapCount);
            Assert.Equal(3, rt.descriptor.mipCount);
            Assert.Equal(-1, new RenderTexture(16, 8, 24, RenderTextureFormat.ARGB32, 0).descriptor.mipCount);
            Assert.True(rt.Create());
            var many = new RenderTexture(16, 8, 24, GraphicsFormat.R8G8B8A8_UNorm, 6);
            Assert.False(many.Create());
            Assert.False(many.IsCreated());
            Assert.Contains("Error RenderTexture.Create failed: Invalid mip count (6) for Texture with a max mip count of 5.", logged);
            var mipped = new RenderTexture(16, 8, 24, RenderTextureFormat.ARGB32, 3) { useMipMap = true };
            Assert.Equal(3, mipped.mipmapCount);
        }

        [Fact]
        public void DefaultFormats()
        {
            Assert.Equal("16x8 depth=0 format=ARGB32 gf=R8G8B8A8_UNorm ds=None sRGB=False mips=1 | mipCount=-1 ds=None bits=0", Describe(new RenderTexture(16, 8, 0, DefaultFormat.LDR)));
            Assert.Equal("16x8 depth=24 format=ARGBHalf gf=R16G16B16A16_SFloat ds=D24_UNorm_S8_UInt sRGB=False mips=1 | mipCount=-1 ds=D24_UNorm_S8_UInt bits=24", Describe(new RenderTexture(16, 8, 24, DefaultFormat.HDR)));
            Assert.Equal("16x8 depth=24 format=Depth gf=None ds=D24_UNorm_S8_UInt sRGB=False mips=1 | mipCount=-1 ds=D24_UNorm_S8_UInt bits=24", Describe(new RenderTexture(16, 8, 16, DefaultFormat.DepthStencil)));
            Assert.Equal("16x8 depth=16 format=Shadowmap gf=None ds=D16_UNorm sRGB=False mips=1 | mipCount=-1 ds=D16_UNorm bits=16", Describe(new RenderTexture(16, 8, 32, DefaultFormat.Shadow)));
            var video = new RenderTexture(16, 8, 0, DefaultFormat.Video);
            Assert.True(video == null);
            Assert.Contains("Error Texture creation failed. 'YUV2' is not supported for Render usage on this platform. Use 'SystemInfo.IsFormatSupported' C# API to check format support.", logged);
        }

        [Fact]
        public void GraphicsFormats()
        {
            Assert.Equal("16x8 depth=16 format=ARGB32 gf=R8G8B8A8_SRGB ds=D16_UNorm sRGB=True mips=1 | mipCount=3 ds=D16_UNorm bits=16", Describe(new RenderTexture(16, 8, 16, GraphicsFormat.R8G8B8A8_SRGB, 3)));
            Assert.Equal("16x8 depth=32 format=RFloat gf=R32_SFloat ds=D32_SFloat_S8_UInt sRGB=False mips=1 | mipCount=-1 ds=D32_SFloat_S8_UInt bits=32", Describe(new RenderTexture(16, 8, 32, GraphicsFormat.R32_SFloat)));
            Assert.Equal("16x8 depth=16 format=ARGB32 gf=R8G8B8A8_UNorm ds=D16_UNorm sRGB=False mips=1 | mipCount=-1 ds=D16_UNorm bits=16", Describe(new RenderTexture(16, 8, 7, GraphicsFormat.R8G8B8A8_UNorm)));
            // A depth format as the colour: an error, and R8G8B8A8_UNorm.
            Assert.Equal("16x8 depth=24 format=ARGB32 gf=R8G8B8A8_UNorm ds=D24_UNorm_S8_UInt sRGB=False mips=1 | mipCount=-1 ds=D24_UNorm_S8_UInt bits=24", Describe(new RenderTexture(16, 8, 24, GraphicsFormat.D24_UNorm_S8_UInt)));
            Assert.Contains("Error RenderTexture color format cannot be set to a depth/stencil format", logged);
            Assert.True(new RenderTexture(16, 8, 0, GraphicsFormat.B8G8R8A8_UNorm) == null);
            Assert.True(new RenderTexture(16, 8, 0, GraphicsFormat.RGBA_DXT5_UNorm) == null);
            // None and no depth: made, but Create fails.
            var none = new RenderTexture(16, 8, 0, GraphicsFormat.None);
            Assert.Equal(RenderTextureFormat.Depth, none.format);
            Assert.False(none.Create());
            Assert.Contains("Error RenderTexture.Create failed: colorFormat & depthStencilFormat cannot both be none.", logged);
        }

        [Fact]
        public void ColourAndDepthStencilFormats()
        {
            Assert.Equal("16x8 depth=32 format=ARGB32 gf=R8G8B8A8_SRGB ds=D32_SFloat sRGB=True mips=1 | mipCount=-1 ds=D32_SFloat bits=32", Describe(new RenderTexture(16, 8, GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.D32_SFloat)));
            Assert.Equal("16x8 depth=24 format=Depth gf=None ds=D24_UNorm_S8_UInt sRGB=False mips=1 | mipCount=3 ds=D24_UNorm_S8_UInt bits=24", Describe(new RenderTexture(16, 8, GraphicsFormat.None, GraphicsFormat.D24_UNorm_S8_UInt, 3)));
            Assert.Equal(GraphicsFormat.D24_UNorm_S8_UInt, new RenderTexture(16, 8, GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.D16_UNorm_S8_UInt).depthStencilFormat);
            Assert.Empty(logged);
            Assert.Equal(GraphicsFormat.D24_UNorm_S8_UInt, new RenderTexture(16, 8, GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.R8G8B8A8_UNorm).depthStencilFormat);
            Assert.Contains("Error Invalid Depth/Stencil GraphicsFormat", logged);
        }

        [Fact]
        public void SrgbFormatsWarnAtCreateInGammaSpace()
        {
            var rt = new RenderTexture(16, 8, 0, GraphicsFormat.R8G8B8A8_SRGB);
            Assert.Empty(logged);
            Assert.True(rt.Create());
            Assert.Equal(["Warning Requested RenderTexture with sRGB format. sRGB formats are not supported in gamma mode, fallback to a UNorm format. Use a UNorm format instead of sRGB to silence this warning."], logged);
        }

        [Fact]
        public void ZeroSizesFailAtCreate()
        {
            Assert.False(new RenderTexture(0, 8, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear).Create());
            Assert.False(new RenderTexture(8, 0, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear).Create());
            Assert.Equal(["Error RenderTexture.Create failed: Texture must have width greater than 0.", "Error RenderTexture.Create failed: Texture must have height greater than 0."], logged);
        }

        [Fact]
        public void RuntimeRenderTexturesClampAndAreReadable()
        {
            var rt = new RenderTexture(16, 8, 24);
            Assert.Equal(TextureWrapMode.Clamp, rt.wrapMode);
            Assert.True(rt.isReadable);
            Assert.Equal(TextureDimension.Tex2D, rt.dimension);
            Assert.Equal(TextureDimension.Tex2D, ((Texture)rt).dimension);
            var temp = RenderTexture.GetTemporary(16, 8, 0);
            Assert.Equal(TextureWrapMode.Clamp, temp.wrapMode);
            RenderTexture.ReleaseTemporary(temp);
            Assert.Equal(GraphicsFormat.R8G8B8A8_UNorm, SystemInfo.GetGraphicsFormat(DefaultFormat.LDR));
            Assert.Equal(GraphicsFormat.D16_UNorm, SystemInfo.GetGraphicsFormat(DefaultFormat.Shadow));
        }
    }
}
