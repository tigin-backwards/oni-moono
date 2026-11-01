using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// Which uniform types are samplers, and the targets they read
    /// (src/OniMoono.Desktop/Gl/GlPrograms.cs).
    public class GlSamplerTypeTests
    {
        /// The 40 sampler types of GLSL 4.50 (section 4.1.7) by their GL type tokens
        /// (glcorearb.h), with the texture target each reads.
        [Theory]
        [InlineData(0x8B5D, 0x0DE0)] [InlineData(0x8B5E, 0x0DE1)] [InlineData(0x8B5F, 0x806F)] [InlineData(0x8B60, 0x8513)]
        [InlineData(0x8B61, 0x0DE0)] [InlineData(0x8B62, 0x0DE1)] [InlineData(0x8B63, 0x84F5)] [InlineData(0x8B64, 0x84F5)]
        [InlineData(0x8DC0, 0x8C18)] [InlineData(0x8DC1, 0x8C1A)] [InlineData(0x8DC2, 0x8C2A)] [InlineData(0x8DC3, 0x8C18)]
        [InlineData(0x8DC4, 0x8C1A)] [InlineData(0x8DC5, 0x8513)]
        [InlineData(0x8DC9, 0x0DE0)] [InlineData(0x8DCA, 0x0DE1)] [InlineData(0x8DCB, 0x806F)] [InlineData(0x8DCC, 0x8513)]
        [InlineData(0x8DCD, 0x84F5)] [InlineData(0x8DCE, 0x8C18)] [InlineData(0x8DCF, 0x8C1A)] [InlineData(0x8DD0, 0x8C2A)]
        [InlineData(0x8DD1, 0x0DE0)] [InlineData(0x8DD2, 0x0DE1)] [InlineData(0x8DD3, 0x806F)] [InlineData(0x8DD4, 0x8513)]
        [InlineData(0x8DD5, 0x84F5)] [InlineData(0x8DD6, 0x8C18)] [InlineData(0x8DD7, 0x8C1A)] [InlineData(0x8DD8, 0x8C2A)]
        [InlineData(0x900C, 0x9009)] [InlineData(0x900D, 0x9009)] [InlineData(0x900E, 0x9009)] [InlineData(0x900F, 0x9009)]
        [InlineData(0x9108, 0x9100)] [InlineData(0x9109, 0x9100)] [InlineData(0x910A, 0x9100)]
        [InlineData(0x910B, 0x9102)] [InlineData(0x910C, 0x9102)] [InlineData(0x910D, 0x9102)]
        public void EverySamplerTypeIsOne(uint type, uint target)
        {
            Assert.True(GlPrograms.IsSampler(type));
            Assert.Equal(target, GlPrograms.SamplerTarget(type));
        }

        [Theory]
        [InlineData(0x1406)] // float
        [InlineData(0x8B52)] // vec4
        [InlineData(0x8B5C)] // mat4
        [InlineData(0x1404)] // int
        [InlineData(0x904D)] // image2D
        public void OtherTypesAreNot(uint type) => Assert.False(GlPrograms.IsSampler(type));
    }
}
