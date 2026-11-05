using System;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Tests
{
    /// A program drawn with Direct3D's render-texture convention (Direct3DClip, Klei/Stars on
    /// Windows) reads Direct3D's projection and _ProjectionParams into a render texture, and
    /// its vertex stage turns gl_Position's y back after its own main.
    public class Direct3DClipTests
    {
        const string StarsLike = "#version 150\nuniform \tvec4 hlslcc_mtx4x4unity_MatrixVP[4];\nin  vec4 in_POSITION0;\nout vec2 vs_TEXCOORD2;\nvec4 u_xlat0;\nvoid main()\n{\n    u_xlat0 = hlslcc_mtx4x4unity_MatrixVP[3] * in_POSITION0.wwww;\n    gl_Position = u_xlat0;\n    vs_TEXCOORD2.xy = u_xlat0.xy + vec2(1.0, 1.0);\n    return;\n}\n";

        [Fact]
        public void VertexStageTurnsClipYBackAfterItsMain()
        {
            var text = Direct3DClip.Vertex(StarsLike);
            Assert.StartsWith("#version 150\n", text);
            Assert.Contains("uniform vec4 _ProjectionParams;\nvoid oni_main()\n{\n    u_xlat0 =", text);
            Assert.EndsWith("\nvoid main()\n{\n    oni_main();\n    gl_Position.y *= _ProjectionParams.x;\n}\n", text);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"\bvoid main\(\)"));
            // A program that reads _ProjectionParams already declares it.
            var declared = Direct3DClip.Vertex(StarsLike.Replace("in  vec4 in_POSITION0;", "uniform \tvec4 _ProjectionParams;\nin  vec4 in_POSITION0;"));
            Assert.DoesNotContain("uniform vec4 _ProjectionParams;", declared);
            Assert.Throws<FormatException>(() => Direct3DClip.Vertex("#version 150\nvoid other() {}\n"));
        }

        static float[] Resolve(UniformSource source, bool matrix, bool direct3D, bool renderTexture, CameraConstants camera)
        {
            var u = new UniformBinding { Name = "u", Type = matrix ? FloatMat4 : FloatVec4, Size = matrix ? 4 : 1, IsMatrix = matrix, Source = source, Direct3DClip = direct3D };
            var o = new float[16];
            GlUniforms.Resolve(u, new DrawInputs { Camera = camera, RenderTexture = renderTexture }, o);
            return o;
        }

        [Fact]
        public void ProjectionIsDirect3DsIntoRenderTexturesOnly()
        {
            var view = Matrix4x4.Translate(new Vector3(-3f, -2f, 10f));
            var projection = Matrix4x4.Ortho(-16f, 16f, -9f, 9f, 0.3f, 1000f);
            var camera = new CameraConstants(view, projection, new Vector3(3f, 2f, -10f), 0.3f, 1000f, true, new Vector2(1600f, 900f));
            var vp = projection * view;
            var flipped = Matrix4x4.Scale(new Vector3(1f, -1f, 1f)) * vp;
            static float[] Floats(Matrix4x4 m) => new[] { m.m00, m.m10, m.m20, m.m30, m.m01, m.m11, m.m21, m.m31, m.m02, m.m12, m.m22, m.m32, m.m03, m.m13, m.m23, m.m33 };

            Assert.Equal(Floats(flipped), Resolve(UniformSource.MatrixVP, true, direct3D: true, renderTexture: true, camera));
            Assert.Equal(Floats(vp), Resolve(UniformSource.MatrixVP, true, direct3D: true, renderTexture: false, camera));
            Assert.Equal(Floats(vp), Resolve(UniformSource.MatrixVP, true, direct3D: false, renderTexture: true, camera));
            // The camera's own projection stays as it is.
            Assert.Equal(Floats(projection), Resolve(UniformSource.CameraProjection, true, direct3D: true, renderTexture: true, camera));

            Assert.Equal(-1f, Resolve(UniformSource.ProjectionParams, false, direct3D: true, renderTexture: true, camera)[0]);
            Assert.Equal(1f, Resolve(UniformSource.ProjectionParams, false, direct3D: true, renderTexture: false, camera)[0]);
            Assert.Equal(1f, Resolve(UniformSource.ProjectionParams, false, direct3D: false, renderTexture: true, camera)[0]);
        }

        [Fact]
        public void OnlyKleiStarsAndOnlyWhereEnabled()
        {
            Assert.Equal(OperatingSystem.IsWindows(), Direct3DClip.Applies("Klei/Stars "));
            Assert.False(Direct3DClip.Applies("Klei/BackWallArray"));
        }
    }
}
