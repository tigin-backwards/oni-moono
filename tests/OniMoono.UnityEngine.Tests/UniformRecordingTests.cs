using System;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Tests
{
    /// The device resolves what a material and the globals give a program once per their
    /// versions; every draw must still see the current values, its own block's overrides and
    /// its own per-draw values.
    public class UniformRecordingTests
    {
        static readonly int ColorId = Shader.PropertyToID("_RecordingTestColor");
        static readonly int CutoffId = Shader.PropertyToID("_RecordingTestCutoff");
        static readonly int GlobalId = Shader.PropertyToID("_RecordingTestGlobal");

        static DeviceProgram Program()
        {
            var p = new DeviceProgram();
            p.Uniforms.Add(new UniformBinding { Name = "_Color", Type = FloatVec4, Size = 1, Source = UniformSource.Property, PropertyId = ColorId });
            p.Uniforms.Add(new UniformBinding { Name = "_Cutoff", Type = FloatType, Size = 1, Source = UniformSource.Property, PropertyId = CutoffId });
            p.Uniforms.Add(new UniformBinding { Name = "_Global", Type = FloatVec4, Size = 1, Source = UniformSource.Property, PropertyId = GlobalId });
            p.Uniforms.Add(new UniformBinding { Name = "unity_ObjectToWorld", Type = FloatMat4, Size = 4, IsMatrix = true, Source = UniformSource.ObjectToWorld });
            p.LooseLength = 4 + 1 + 4 + 16;
            return p;
        }

        static float[] Record(DeviceProgram p, DrawInputs d)
        {
            var values = new float[p.LooseLength];
            GlUniforms.Record(p, d, values, Span<byte>.Empty);
            return values;
        }

        [Fact]
        public void DrawsSeeCurrentMaterialGlobalBlockAndPerDrawValues()
        {
            var p = Program();
            var material = new MaterialNative();
            material.Properties.SetVector(ColorId, new Vector4(1f, 0f, 0f, 1f));
            material.Properties.SetFloat(CutoffId, 0.5f);
            ShaderGlobals.Properties.SetVector(GlobalId, new Vector4(7f, 7f, 7f, 7f));
            var d = new DrawInputs { Material = material, ObjectToWorld = Matrix4x4.Translate(new Vector3(3f, 0f, 0f)) };

            var v = Record(p, d);
            Assert.Equal(1f, v[0]);
            Assert.Equal(0.5f, v[4]);
            Assert.Equal(7f, v[5]);
            Assert.Equal(3f, v[9 + 12]); // translation x (column-major m03)

            // The material changes.
            material.Properties.SetFloat(CutoffId, 0.25f);
            Assert.Equal(0.25f, Record(p, d)[4]);

            // A block overrides one draw only.
            var block = new PropertySheet();
            block.SetVector(ColorId, new Vector4(0f, 0f, 1f, 1f));
            d.Block = block;
            v = Record(p, d);
            Assert.Equal(0f, v[0]);
            Assert.Equal(1f, v[2]);
            Assert.Equal(0.25f, v[4]);
            d.Block = null;
            Assert.Equal(1f, Record(p, d)[0]);

            // The globals change, and so does the draw's matrix.
            ShaderGlobals.Properties.SetVector(GlobalId, new Vector4(9f, 9f, 9f, 9f));
            d.ObjectToWorld = Matrix4x4.Translate(new Vector3(5f, 0f, 0f));
            v = Record(p, d);
            Assert.Equal(9f, v[5]);
            Assert.Equal(5f, v[9 + 12]);
        }

        static readonly int MainTexId = Shader.PropertyToID("_RecordingTestTex");
        static readonly int CountId = Shader.PropertyToID("_RecordingTestCount");

        [Fact]
        public void BlockTextureKeepsTheMaterialsTilingAndOffset()
        {
            // MaterialPropertyBlock.SetTexture sets only the texture: X_ST still comes from the
            // material unless the block sets that vector.
            var p = new DeviceProgram();
            p.Uniforms.Add(new UniformBinding { Name = "_RecordingTestTex_ST", Type = FloatVec4, Size = 1, Source = UniformSource.TextureScaleOffset, PropertyId = MainTexId });
            p.LooseLength = 4;
            var material = new MaterialNative();
            material.Properties.SetTexture(MainTexId, null);
            material.Properties.SetTextureScaleOffset(MainTexId, new Vector4(2f, 3f, 0.25f, 0.5f));
            var block = new PropertySheet();
            block.SetTexture(MainTexId, null);
            var d = new DrawInputs { Material = material, Block = block };
            Assert.Equal(new[] { 2f, 3f, 0.25f, 0.5f }, Record(p, d));

            block.SetVector(Shader.PropertyToID("_RecordingTestTex_ST"), new Vector4(4f, 4f, 0f, 0f));
            Assert.Equal(new[] { 4f, 4f, 0f, 0f }, Record(p, d));
        }

        [Fact]
        public void BlockIntegerOverridesTheMaterialsValue()
        {
            // Block over material over globals, whichever store (float or integer) holds it.
            var p = new DeviceProgram();
            p.Uniforms.Add(new UniformBinding { Name = "_RecordingTestCount", Type = IntType, Size = 1, Source = UniformSource.Property, PropertyId = CountId });
            p.LooseLength = 1;
            var material = new MaterialNative();
            material.Properties.SetFloat(CountId, 1f);
            var block = new PropertySheet();
            block.SetInt(CountId, 5);
            Assert.Equal(5f, Record(p, new DrawInputs { Material = material, Block = block })[0]);
            Assert.Equal(1f, Record(p, new DrawInputs { Material = material })[0]);
        }
    }
}
