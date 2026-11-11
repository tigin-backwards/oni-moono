using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Tests
{
    /// Super-sized captures larger than one render target render in windows
    /// (FrameRenderer.Capture): their layout, the transforms that place the whole capture in a
    /// window, and the changes the game's screen-space shaders need (CaptureTiles).
    public class CaptureTilesTests
    {
        [Theory]
        [InlineData(20480, 8192, 64, 8)]
        [InlineData(10240, 1024, 64, 8)]
        [InlineData(1001, 300, 64, 8)]
        [InlineData(5760, 401, 64, 8)]
        [InlineData(500, 500, 64, 8)]
        [InlineData(500, 600, 64, 8)]
        public void SpansCoverTheCaptureOnceWithTheirGuardsInsideTheWindow(int total, int size, int guard, int align)
        {
            var spans = FrameRenderer.Spans(total, size, guard, align);
            int next = 0;
            foreach (var (start, length, origin) in spans)
            {
                Assert.Equal(next, start);
                Assert.True(length > 0);
                next = start + length;
                Assert.InRange(origin, 0, Math.Max(0, total - size));
                // The span lies in its window, with the guard on each side where the capture
                // goes on past it.
                Assert.True(start >= origin && start + length <= origin + Math.Min(size, total));
                if (start > 0) Assert.True(start - origin >= guard, $"guard before {start}");
                if (start + length < total) Assert.True(origin + size - (start + length) >= guard, $"guard after {start + length}");
                if (origin != total - size) Assert.Equal(0, origin % align);
            }
            Assert.Equal(total, next);
        }

        [Fact]
        public void AWindowCoversTheCaptureWhenItFits()
        {
            Assert.Equal((2560, 1440), FrameRenderer.WindowSize(2560, 1440, 16384, null));
            Assert.Equal((3840, 2160), FrameRenderer.WindowSize(3840, 2160, 16384, null));
            Assert.Equal((300, 200), FrameRenderer.WindowSize(2560, 1440, 16384, (300, 200)));
            // 8x of 2560x1440: past the GPU's textures, in bands of the width's windows.
            var (w, h) = FrameRenderer.WindowSize(20480, 11520, 16384, null);
            Assert.True(w <= 16384 && h < 1000 && (long)w * h <= 4096L * 2048, $"{w}x{h}");
            // 8x of 1280x720 fits the GPU, not the window budget.
            (w, h) = FrameRenderer.WindowSize(10240, 5760, 16384, null);
            Assert.True(w == 10240 && h < 1000, $"{w}x{h}");
        }

        static FrameRenderer.CaptureWindow Window(int x, int y) => new() { Scale = 4, VirtualWidth = 4000, VirtualHeight = 2000, X = x, Y = y, Width = 1000, Height = 500 };

        [Fact]
        public void TheWindowsClipTransformPlacesTheCapturesPixelsInIt()
        {
            var c = Window(1200, 700);
            var clip = c.Clip(0f, 0f, c.VirtualWidth, c.VirtualHeight);
            // The capture's pixel (1450.5, 820.5) is the window's (250.5, 120.5).
            var v = new Vector4(1450.5f / 4000f * 2f - 1f, 820.5f / 2000f * 2f - 1f, 0f, 1f);
            var w = clip * v;
            Assert.Equal(250.5f / 1000f * 2f - 1f, w.x, 5);
            Assert.Equal(120.5f / 500f * 2f - 1f, w.y, 5);
            // The tile maps texture coordinates the same way, and the device's transform from it
            // is the window's.
            var t = c.Tile;
            Assert.Equal(250.5f / 1000f, 1450.5f / 4000f * t.x + t.z, 5);
            Assert.Equal(120.5f / 500f, 820.5f / 2000f * t.y + t.w, 5);
            var constants = new CameraConstants(Matrix4x4.identity, Matrix4x4.identity, Vector3.zero, -1f, 1f, true, Vector2.one) { ScreenTile = t };
            for (int i = 0; i < 16; i++) Assert.Equal(clip[i], constants.TileClip[i], 5);
        }

        [Fact]
        public void TheWholeCapturesProjectionUndoesTheWindows()
        {
            var c = Window(1200, 700);
            var projection = Matrix4x4.Ortho(-16f, 16f, -9f, 9f, 0.3f, 1000f);
            var placed = c.Clip(0f, 0f, c.VirtualWidth, c.VirtualHeight) * projection;
            var constants = new CameraConstants(Matrix4x4.identity, placed, Vector3.zero, 0.3f, 1000f, true, new Vector2(4000f, 2000f)) { ScreenTile = c.Tile };
            for (int i = 0; i < 16; i++) Assert.Equal(projection[i], constants.VirtualProjection[i], 4);
            // Outside a tiled capture: the projection as it is.
            var plain = constants with { ScreenTile = default };
            Assert.Equal(placed, plain.VirtualProjection);
            Assert.Equal(new Vector4(1f, 1f, 0f, 0f), plain.Tile);
        }

        [Fact]
        public void TheScreenToWorldMappingFollowsTheWindow()
        {
            // Game.LateUpdate's _WsToCs: world = (1 - uv) * zw + xy over the screen's uv.
            var screen = new Vector4(0.7f, 0.6f, -0.3f, -0.2f);
            var c = Window(1200, 700);
            var narrowed = FrameRenderer.NarrowScreenToWorld(screen, c);
            var t = c.Tile;
            foreach (var (u, v) in new[] { (0f, 0f), (0.25f, 0.5f), (1f, 1f) })
            {
                // The window's uv (u, v) is the screen's ((u - o) / s).
                float su = (u - t.z) / t.x, sv = (v - t.w) / t.y;
                Assert.Equal((1f - su) * screen.z + screen.x, (1f - u) * narrowed.z + narrowed.x, 5);
                Assert.Equal((1f - sv) * screen.w + screen.y, (1f - v) * narrowed.w + narrowed.y, 5);
            }
        }

        const string LiquidLike = "#version 150\n#extension GL_ARB_explicit_attrib_location : require\nuniform \tvec4 _ScreenParams;\nUNITY_LOCATION(5) uniform  sampler2D MRT0_Copy;\nUNITY_LOCATION(6) uniform  sampler2D MRT1_Copy;\nin  vec2 vs_TEXCOORD3;\nvec4 u_xlat6;\nvoid main()\n{\n    u_xlat17 = texture(MRT0_Copy, u_xlat6.xy);\n    u_xlat18 = texture(MRT1_Copy, u_xlat6.xy);\n    u_xlat19 = texture(MRT1_Copy, vs_TEXCOORD3.xy);\n    u_xlat20 = texture(_Other, u_xlat6.xy);\n}\n";

        [Fact]
        public void LiquidReadsItsCopiesInTheWindow()
        {
            var text = CaptureTiles.Copies(LiquidLike, out int reads);
            Assert.Equal(3, reads);
            Assert.Contains("texture(MRT0_Copy, u_xlat6.xy * oni_ScreenTile.xy + oni_ScreenTile.zw)", text);
            Assert.Contains("texture(MRT1_Copy, vs_TEXCOORD3.xy * oni_ScreenTile.xy + oni_ScreenTile.zw)", text);
            Assert.Contains("texture(_Other, u_xlat6.xy)", text);
            Assert.Contains("#extension GL_ARB_explicit_attrib_location : require\nuniform vec4 oni_ScreenTile;\n", text);
            Assert.Equal("#version 150\nvoid main() {}\n", CaptureTiles.Copies("#version 150\nvoid main() {}\n", out reads));
            Assert.Equal(0, reads);
        }

        [Fact]
        public void LightBufferReadsGoToTheWholeCapturesBuffer()
        {
            const string TiledBlockLike = "#version 150\nUNITY_LOCATION(4) uniform  sampler2D _LightBufferTex;\nvoid main()\n{\n    u_xlat3 = texture(_LightBufferTex, u_xlat1.xy);\n}\n";
            var text = CaptureTiles.Lights(TiledBlockLike, out int reads);
            Assert.Equal(1, reads);
            Assert.Contains("texture(_LightBufferTex, (u_xlat1.xy - oni_ScreenTile.zw) / oni_ScreenTile.xy)", text);
            string vertex = "#version 150\nvoid main() {}\n", fragment = TiledBlockLike;
            Assert.False(CaptureTiles.Apply("Klei/TiledBlock", ref vertex, ref fragment));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(fragment, "uniform vec4 oni_ScreenTile;"));
            // The light buffer of a tiled capture: the whole capture, within the GPU's textures
            // and LightPixels, its aspect kept.
            var (lw, lh) = FrameRenderer.LightSize(20480, 11520, 16384);
            Assert.True(lw * (long)lh <= 4096L * 2304 && Math.Abs(lw / (double)lh - 16.0 / 9.0) < 0.01, $"{lw}x{lh}");
            Assert.Equal((2560, 1440), FrameRenderer.LightSize(2560, 1440, 16384));
        }

        const string StarsLike = "#version 150\nuniform \tvec4 hlslcc_mtx4x4unity_MatrixVP[4];\nin  vec4 in_POSITION0;\nout vec2 vs_TEXCOORD2;\nvec4 u_xlat0;\nvoid main()\n{\n    u_xlat0 = hlslcc_mtx4x4unity_MatrixVP[3] * in_POSITION0.wwww;\n    gl_Position = u_xlat0;\n    vs_TEXCOORD2.xy = u_xlat0.xy + vec2(1.0, 1.0);\n    return;\n}\n";

        [Fact]
        public void ScreenSpaceProgramsPlaceTheirVerticesInTheWholeCaptureThenTheWindow()
        {
            var text = CaptureTiles.Positions(StarsLike);
            Assert.StartsWith("#version 150\nuniform vec4 oni_ScreenTile;\n", text);
            Assert.Contains("void oni_tile_main()\n{", text);
            Assert.EndsWith("\nvoid main()\n{\n    oni_tile_main();\n    gl_Position.xy = gl_Position.xy * oni_ScreenTile.xy + (oni_ScreenTile.xy + 2.0 * oni_ScreenTile.zw - 1.0) * gl_Position.w;\n}\n", text);
            // After Direct3DClip's own main: its main is the one wrapped.
            var both = CaptureTiles.Positions(Direct3DClip.Vertex(StarsLike));
            Assert.Contains("void oni_main()", both);
            Assert.Contains("void oni_tile_main()\n{\n    oni_main();", both);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(both, @"\bvoid main\(\)"));

            string vertex = StarsLike, fragment = LiquidLike;
            Assert.True(CaptureTiles.Apply("Klei/Stars ", ref vertex, ref fragment));
            Assert.Equal(LiquidLike, fragment);
            vertex = StarsLike;
            Assert.False(CaptureTiles.Apply("Klei/Liquid", ref vertex, ref fragment));
            Assert.Equal(StarsLike, vertex);
            Assert.Contains("oni_ScreenTile.zw)", fragment);
        }

        static float[] Resolve(UniformSource source, bool matrix, bool wholeCapture, CameraConstants camera)
        {
            var u = new UniformBinding { Name = "u", Type = matrix ? FloatMat4 : FloatVec4, Size = matrix ? 4 : 1, IsMatrix = matrix, Source = source, WholeCapture = wholeCapture };
            var o = new float[16];
            GlUniforms.Resolve(u, new DrawInputs { Camera = camera, RenderTexture = true }, o);
            return o;
        }

        static float[] Floats(Matrix4x4 m) => new[] { m.m00, m.m10, m.m20, m.m30, m.m01, m.m11, m.m21, m.m31, m.m02, m.m12, m.m22, m.m32, m.m03, m.m13, m.m23, m.m33 };

        [Fact]
        public void UniformsOfAWindowAreTheWholeCapturesButWhatRasterises()
        {
            var c = Window(1200, 700);
            var view = Matrix4x4.Translate(new Vector3(-3f, -2f, 10f));
            var projection = Matrix4x4.Ortho(-16f, 16f, -9f, 9f, 0.3f, 1000f);
            var placed = c.Clip(0f, 0f, c.VirtualWidth, c.VirtualHeight) * projection;
            var camera = new CameraConstants(view, placed, new Vector3(3f, 2f, -10f), 0.3f, 1000f, true, new Vector2(4000f, 2000f)) { ScreenTile = c.Tile };

            Assert.Equal(Floats(placed * view), Resolve(UniformSource.MatrixVP, true, wholeCapture: false, camera));
            var whole = Resolve(UniformSource.MatrixVP, true, wholeCapture: true, camera);
            var expected = Floats(projection * view);
            for (int i = 0; i < 16; i++) Assert.Equal(expected[i], whole[i], 4);
            // The camera's projection and the ortho size are the whole capture's.
            var ortho = Resolve(UniformSource.OrthoParams, false, wholeCapture: false, camera);
            Assert.Equal(16f, ortho[0], 3);
            Assert.Equal(9f, ortho[1], 3);
            var tile = Resolve(UniformSource.ScreenTile, false, wholeCapture: false, camera);
            Assert.Equal(new[] { c.Tile.x, c.Tile.y, c.Tile.z, c.Tile.w }, tile[..4]);
            Assert.Equal(new[] { 1f, 1f, 0f, 0f }, Resolve(UniformSource.ScreenTile, false, wholeCapture: false, camera with { ScreenTile = default })[..4]);
        }
    }
}
