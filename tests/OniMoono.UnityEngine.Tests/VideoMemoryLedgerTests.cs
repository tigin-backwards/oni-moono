using System;
using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// --frame-stats' video memory by kind (src/OniMoono.Desktop/Gl/VideoMemoryLedger.cs): a
    /// texture's bytes move between kinds as its storage changes, a render texture's depth and
    /// multisampled surfaces count apart from its colour, and nothing is left once it is freed.
    [Collection(nameof(VideoMemoryLedgerTests))]
    public sealed class VideoMemoryLedgerTests : IDisposable
    {
        public VideoMemoryLedgerTests() => VideoMemoryLedger.Reset();

        public void Dispose() => VideoMemoryLedger.Reset();

        [Fact]
        public void TextureBytesFollowItsStorage()
        {
            var t = new DeviceTexture();
            t.SetBytes(1000, "texture DXT5");
            Assert.Equal((1000L, 1), VideoMemoryLedger.Of("texture DXT5"));
            // New storage of another format: the old kind gives its bytes and its count back.
            t.SetBytes(4000, "texture RGBA32");
            Assert.Equal((0L, 0), VideoMemoryLedger.Of("texture DXT5"));
            Assert.Equal((4000L, 1), VideoMemoryLedger.Of("texture RGBA32"));
            Assert.Equal(4000, VideoMemoryLedger.Total);
            t.SetBytes(0, null);
            Assert.Equal((0L, 0), VideoMemoryLedger.Of("texture RGBA32"));
            Assert.Equal(0, VideoMemoryLedger.Total);
        }

        [Fact]
        public void RenderTextureSurfacesCountApart()
        {
            var t = new DeviceTexture();
            t.SetBytes(100 + 40 + 400, "render target R8G8B8A8_UNorm", depth: 40, multisampled: 400);
            Assert.Equal((100L, 1), VideoMemoryLedger.Of("render target R8G8B8A8_UNorm"));
            Assert.Equal((40L, 1), VideoMemoryLedger.Of(DeviceTexture.DepthKind));
            Assert.Equal((400L, 1), VideoMemoryLedger.Of(DeviceTexture.MultisampledKind));
            // Re-created without anti-aliasing: the multisampled surface goes.
            t.SetBytes(100 + 40, "render target R8G8B8A8_UNorm", depth: 40);
            Assert.Equal((0L, 0), VideoMemoryLedger.Of(DeviceTexture.MultisampledKind));
            Assert.Equal((40L, 1), VideoMemoryLedger.Of(DeviceTexture.DepthKind));
            t.SetBytes(0, null);
            Assert.Equal(0, VideoMemoryLedger.Total);
            Assert.Equal((0L, 0), VideoMemoryLedger.Of(DeviceTexture.DepthKind));
            Assert.Equal((0L, 0), VideoMemoryLedger.Of("render target R8G8B8A8_UNorm"));
        }

        [Fact]
        public void PeaksRestartAtEachReport()
        {
            var a = new DeviceTexture();
            var b = new DeviceTexture();
            a.SetBytes(3L << 20, "texture BC7");
            b.SetBytes(5L << 20, "texture BC7");
            b.SetBytes(0, null);
            Assert.Equal("3 MiB (peak 8) asked for, texture BC7 1: 3 MiB (peak 8)", VideoMemoryLedger.Take());
            Assert.Equal("3 MiB (peak 3) asked for, texture BC7 1: 3 MiB", VideoMemoryLedger.Take());
        }
    }
}
