using OniMoono.Engine.Rendering;
using Xunit;

namespace OniMoono.Tests
{
    /// Which subshader a renderer's forward rendering draws (ARCHITECTURE D68).
    public class ForwardSubShaderTests : EngineTest
    {
        /// Cartoon FX's shaders: a first subshader with a ShadowCaster pass alone and a second
        /// with the forward pass and a ShadowCaster. The material reports the first; forward
        /// rendering draws the second's forward pass.
        [Fact]
        public void ForwardRenderingDrawsTheFirstSubshaderWithAForwardPass()
        {
            static ShaderPass Pass(string lightMode) => new() { Name = lightMode, Tags = new() { ["LightMode"] = lightMode } };
            static SubShader Sub(params ShaderPass[] passes) => new() { Passes = passes, Tags = new() };
            var shadowOnly = Sub(Pass("SHADOWCASTER"));
            var forward = Sub(Pass("FORWARDBASE"), Pass("SHADOWCASTER"));
            var shader = new ShaderNative { SubShaders = [shadowOnly, forward] };
            Assert.Same(shadowOnly, shader.ActiveSubShader);
            Assert.Equal(1, shader.PassCount);
            Assert.Same(forward, shader.ForwardSubShader);
            // A shader whose first subshader has a forward pass draws it.
            var plain = new ShaderNative { SubShaders = [forward, shadowOnly] };
            Assert.Same(forward, plain.ForwardSubShader);
            // None with one: the active one.
            var none = new ShaderNative { SubShaders = [shadowOnly] };
            Assert.Same(shadowOnly, none.ForwardSubShader);
        }
    }
}
