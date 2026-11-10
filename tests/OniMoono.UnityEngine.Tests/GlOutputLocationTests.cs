using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// The draw buffers a fragment source writes (GlPrograms.OutputLocations), as the device
    /// masks the others of a multiple-target framebuffer (#71: Klei/Stars writes only target 0
    /// while the temperature overlay's _MRT2 is bound).
    public class GlOutputLocationTests
    {
        [Fact]
        public void LocationsAreTheDeclaredOutputs()
        {
            const string stars = "in vec4 vs_TEXCOORD0;\nlayout(location = 0) out vec4 SV_Target0;\nvoid main() { SV_Target0 = vec4(1.0); }\n";
            Assert.Equal(0b1u, GlPrograms.OutputLocations(stars));
            const string backwall = "layout(location = 0) out vec4 SV_Target0;\nlayout(location = 1) out vec4 SV_Target1;\nlayout( location=2 ) out vec4 SV_Target2;\n";
            Assert.Equal(0b111u, GlPrograms.OutputLocations(backwall));
        }

        [Fact]
        public void SourcesWithoutLocationsWriteEveryBuffer()
        {
            Assert.Equal(~0u, GlPrograms.OutputLocations("out vec4 color;\nvoid main() { color = vec4(1.0); }\n"));
            Assert.Equal(~0u, GlPrograms.OutputLocations("void main() { gl_FragData[1] = vec4(1.0); }\n"));
        }
    }
}
