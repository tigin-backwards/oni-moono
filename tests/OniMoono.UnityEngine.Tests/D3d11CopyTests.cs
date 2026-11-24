using OniMoono.Desktop.D3d11;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// The Direct3D 11 device's CopyTargetRect bounds (D3d11Device.CopyFits): a box outside the
    /// source, or placed outside the destination, is refused before CopySubresourceRegion, whose
    /// behaviour is undefined there ("might result in ... the removal of the rendering device",
    /// its Remarks).
    public class D3d11CopyTests
    {
        [Theory]
        [InlineData(0, 0, 64, 32, 0, 0, true)] // the whole source into the destination's corner
        [InlineData(10, 5, 54, 27, 40, 60, true)] // the far edges reached exactly
        [InlineData(0, 0, 65, 32, 0, 0, false)] // wider than the source
        [InlineData(0, 1, 64, 32, 0, 0, false)] // past the source's top
        [InlineData(-1, 0, 8, 8, 0, 0, false)] // left of the source
        [InlineData(0, 0, 8, 8, -1, 0, false)] // left of the destination
        [InlineData(0, 0, 8, 8, 93, 0, false)] // past the destination's right edge
        [InlineData(0, 0, 8, 8, 0, 85, false)] // past the destination's top
        [InlineData(0, 0, 0, 8, 0, 0, false)] // empty
        [InlineData(0, 0, 8, -2, 0, 0, false)] // negative height
        public void CopyFitsBothTextures(int x, int y, int w, int h, int toX, int toY, bool fits)
            => Assert.Equal(fits, D3d11Device.CopyFits(new RectInt(x, y, w, h), 64, 32, new Vector2Int(toX, toY), 100, 92));

        [Fact]
        public void CopyFitsDoesNotOverflow()
            => Assert.False(D3d11Device.CopyFits(new RectInt(int.MaxValue - 4, 0, 8, 8), int.MaxValue, 32, new Vector2Int(0, 0), 100, 92));
    }
}
