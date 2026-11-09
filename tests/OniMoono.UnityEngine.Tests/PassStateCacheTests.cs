using System;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// A material's pass states are kept per pass: a material drawn with alternating passes,
    /// or whose values are resolved again every frame (the globals change), resolves the same
    /// states without making new cache entries, and a change the state reads shows at once.
    public class PassStateCacheTests : EngineTest
    {
        static readonly int StencilId = Shader.PropertyToID("_Stencil");
        static readonly int GlobalId = Shader.PropertyToID("_PassStateTestGlobal");

        static Shader UiDefault()
        {
            AssetSystem.Initialize(GameInstall.DataDir);
            ProjectSettings.Load();
            var shader = Shader.Find("UI/Default");
            Assert.NotNull(shader);
            return shader;
        }

        [Fact]
        public void AlternatingPassesAndChangingGlobalsMakeNoNewEntries()
        {
            var shader = UiDefault();
            var native = (ShaderNative)shader.NativeOrThrow;
            var pass = native.SubShaders[0].Passes[0];
            // Two passes with the same program (as two of a shader's passes can share one).
            var a = new PassState(pass.Data.Struct("m_State"));
            var b = new PassState(pass.Data.Struct("m_State"));
            Assert.NotEmpty(a.PropertyIds);
            var material = new Material(shader);
            try
            {
                var d = new DrawInputs { Material = (MaterialNative)material.NativeOrThrow, Shader = native };
                var program = new DeviceProgram { Id = 1 };
                MaterialValues Values() => GlUniforms.Record(program, d, Span<float>.Empty, Span<byte>.Empty);

                var first = ResolvedState.Of(a, d, Values());
                Assert.True(first.SameBits(ResolvedState.Of(b, d, Values())));
                long bytes = 0;
                for (int i = 0; i < 50; i++)
                {
                    // As every frame: a global changes, so the material's values resolve again.
                    Shader.SetGlobalFloat(GlobalId, i);
                    var values = Values();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    var sa = ResolvedState.Of(a, d, values);
                    var sb = ResolvedState.Of(b, d, values);
                    bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.True(sa.SameBits(first));
                    Assert.True(sb.SameBits(first));
                }
                Assert.Equal(0, bytes);
                Assert.Equal(2, Values().Resolved.Length);

                // A property the state reads changes: the new state at once, for both passes.
                material.SetFloat(StencilId, 3f);
                Assert.Equal(3, ResolvedState.Of(a, d, Values()).StencilRef);
                Assert.Equal(3, ResolvedState.Of(b, d, Values()).StencilRef);
                material.SetFloat(StencilId, 0f);
                Assert.True(ResolvedState.Of(a, d, Values()).SameBits(first));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
