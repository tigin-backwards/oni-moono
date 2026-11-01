using OniMoono.Desktop.Gl;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// How compressed formats are uploaded (src/OniMoono.Desktop/Gl/GlTextures.cs).
    public class GlFormatTests
    {
        /// S3TC and RGTC have no 3D form in GL, so 3D textures of them are decoded; BPTC keeps
        /// its blocks in 3D; 2D textures keep theirs in every format.
        [Theory]
        [InlineData(TextureFormat.DXT1, true)]
        [InlineData(TextureFormat.DXT5, true)]
        [InlineData(TextureFormat.BC4, true)]
        [InlineData(TextureFormat.BC5, true)]
        [InlineData(TextureFormat.BC6H, false)]
        [InlineData(TextureFormat.BC7, false)]
        public void VolumesDecodeFormatsWithNo3DForm(TextureFormat format, bool decoded)
        {
            var volume = GlTextures.Of(format, volume: true);
            Assert.Equal(decoded, volume.Decompress);
            Assert.Equal(!decoded, volume.Compressed);
            var flat = GlTextures.Of(format);
            Assert.True(flat.Compressed);
            Assert.False(flat.Decompress);
        }
    }
}
