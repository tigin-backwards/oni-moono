using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// Which drivers get bindless textures (src/OniMoono.Desktop/Gl/GlBindless.cs).
    public class GlBindlessTests
    {
        [Theory]
        [InlineData("ATI Technologies Inc.", true)]        // AMD's Windows driver (issue #27)
        [InlineData("AMD", true)]                          // Mesa's radeonsi (issue #43)
        [InlineData("NVIDIA Corporation", false)]
        [InlineData("Microsoft Corporation", false)]       // Mesa d3d12 (WSL)
        [InlineData("Intel", false)]
        public void AmdsDriversBindToUnits(string vendor, bool slow)
            => Assert.Equal(slow, GlBindless.SlowOnDriver(vendor));
    }
}
