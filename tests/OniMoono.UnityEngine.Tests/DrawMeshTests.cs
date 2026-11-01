using System.Linq;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    public class DrawMeshTests : EngineTest
    {
        static readonly int ColorId = Shader.PropertyToID("_Color");

        /// Graphics.DrawMesh draws with the block's values at the call: a change to the block
        /// before the frame renders reaches only later draws. After the frame, the block is written
        /// in place again (no copy).
        [Fact]
        public void ABlockChangedAfterDrawMeshKeepsTheQueuedValues()
        {
            var game = System.Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                       ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "projects", "oni-linux-761211");
            AssetSystem.Initialize(System.IO.Path.Combine(game, "OxygenNotIncluded_Data"));
            ProjectSettings.Load();
            FrameRenderer.Install();
            var device = new RecordingDevice();
            var previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            try
            {
                var camera = new GameObject("camera", typeof(Camera)).GetComponent<Camera>();
                camera.transform.position = new Vector3(0f, 0f, -10f);
                var mesh = new Mesh { vertices = [Vector3.zero, Vector3.up, Vector3.right], triangles = [0, 1, 2] };
                var material = new Material(Shader.Find("Hidden/Internal-GUITextureClip"));
                var block = new MaterialPropertyBlock();

                block.SetColor(ColorId, Color.red);
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, null, 0, block);
                block.SetColor(ColorId, Color.blue);
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, null, 0, block);
                block.SetColor(ColorId, Color.green);
                Frame();
                var colors = device.MeshDrawProperties.Select(p => (Color)p.Vectors[ColorId]).ToArray();
                Assert.Equal(new[] { Color.red, Color.blue }, colors);

                var sheet = block.Sheet;
                block.SetColor(ColorId, Color.white);
                Assert.Same(sheet, block.Sheet);
                Assert.Equal(Color.white, block.GetColor(ColorId));
            }
            finally
            {
                GraphicsDevice.Install(previous);
            }
        }

        /// Graphics.DrawMeshNow draws at once with the pass Material.SetPass set last, measured in
        /// the original (tools/UnityProbe, drawmeshnow): the matrix places the mesh in the world,
        /// seen by the rendering camera's view (GL.modelview plays no part) through GL's
        /// projection; the overloads without a sub-mesh index draw every sub-mesh, and outside a
        /// camera's rendering nothing is drawn. Project Expedition's prospecting scan draws so
        /// from NavPathDrawer.OnPostRender (Workshop 3803418607).
        [Fact]
        public void DrawMeshNowDrawsWithThePassSetInTheCameraView()
        {
            var game = System.Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                       ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "projects", "oni-linux-761211");
            AssetSystem.Initialize(System.IO.Path.Combine(game, "OxygenNotIncluded_Data"));
            ProjectSettings.Load();
            FrameRenderer.Install();
            var device = new RecordingDevice();
            var previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            Camera.CameraCallback post = null;
            try
            {
                var camera = new GameObject("camera", typeof(Camera)).GetComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = 8f;
                camera.transform.position = new Vector3(8f, 8f, -10f);
                camera.targetTexture = new RenderTexture(16, 16, 24);
                var mesh = new Mesh { vertices = [Vector3.zero, Vector3.up, Vector3.right], triangles = [0, 1, 2] };
                var lines = new Material(Shader.Find("Lines/Colored Blended"));
                var other = new Material(Shader.Find("Hidden/Internal-GUITextureClip"));
                var up = Matrix4x4.Translate(new Vector3(0f, 8f, 0f));
                var projection = Matrix4x4.Ortho(0f, 16f, 0f, 16f, -100f, 100f);
                Matrix4x4 view = default;
                post = c =>
                {
                    if (c != camera) return;
                    view = c.worldToCameraMatrix;
                    GL.PushMatrix();
                    GL.LoadProjectionMatrix(projection);
                    GL.modelview = Matrix4x4.Translate(new Vector3(0f, 0f, -10f));
                    Assert.True(other.SetPass(0));
                    Assert.True(lines.SetPass(0));
                    Graphics.DrawMeshNow(mesh, up);
                    Graphics.DrawMeshNow(mesh, up, 0);
                    Graphics.DrawMeshNow(mesh, new Vector3(0f, 8f, 0f), Quaternion.identity);
                    Graphics.DrawMeshNow(mesh, new Vector3(0f, 8f, 0f), Quaternion.identity, 0);
                    GL.PopMatrix();
                };
                Camera.onPostRender += post;
                Frame();

                Assert.Equal(4, device.MeshNowDraws.Count);
                Assert.Equal(new[] { -1, 0, -1, 0 }, device.MeshNowDraws.Select(d => d.SubMesh).ToArray());
                foreach (var d in device.MeshNowDraws)
                {
                    Assert.Same(lines, d.Material);
                    Assert.Same(mesh.Data, d.Mesh);
                    Assert.Equal(up, d.ObjectToWorld);
                    Assert.Equal(view, d.View);
                    Assert.Equal(projection, d.Projection);
                }

                device.MeshNowDraws.Clear();
                lines.SetPass(0);
                Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
                Assert.Empty(device.MeshNowDraws);
                Assert.Throws<System.ArgumentNullException>(() => Graphics.DrawMeshNow(null, Matrix4x4.identity));
            }
            finally
            {
                if (post != null) Camera.onPostRender -= post;
                GraphicsDevice.Install(previous);
            }
        }

        /// Graphics.SetRenderTarget makes the texture (or null, the screen) RenderTexture.active,
        /// and GL.sRGBWrite starts false (gamma colour space) and keeps what is set, as measured
        /// in the original (tools/UnityProbe, drawmeshnow). Project Expedition's deposit coverage
        /// uses both from OnRenderImage.
        [Fact]
        public void SetRenderTargetMakesTheTextureActive()
        {
            var texture = new RenderTexture(16, 16, 24);
            Graphics.SetRenderTarget(texture);
            Assert.Same(texture, RenderTexture.active);
            Graphics.SetRenderTarget(null);
            Assert.Null(RenderTexture.active);

            Assert.False(GL.sRGBWrite);
            GL.sRGBWrite = true;
            Assert.True(GL.sRGBWrite);
            GL.sRGBWrite = false;
        }

        /// Transparent DrawMesh draws at the same distance go by their materials' instance IDs,
        /// ascending, whatever order they were submitted in (UnityProbe draworder): the material
        /// made last draws first, under the other. A nearer draw still goes last.
        [Fact]
        public void TransparentDrawsAtTheSameDistanceGoByMaterial()
        {
            var game = System.Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                       ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "projects", "oni-linux-761211");
            AssetSystem.Initialize(System.IO.Path.Combine(game, "OxygenNotIncluded_Data"));
            ProjectSettings.Load();
            FrameRenderer.Install();
            var device = new RecordingDevice();
            var previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            try
            {
                var camera = new GameObject("camera", typeof(Camera)).GetComponent<Camera>();
                camera.orthographic = true;
                camera.transform.position = new Vector3(0f, 0f, -100f);
                var mesh = new Mesh { vertices = [Vector3.zero, Vector3.up, Vector3.right], triangles = [0, 1, 2] };
                var shader = Shader.Find("Hidden/Internal-GUITextureClip");
                var first = new Material(shader) { name = "first", renderQueue = 3500 };
                var second = new Material(shader) { name = "second", renderQueue = 3500 };
                string Drawn() => string.Join(",", device.MeshDraws.Select(d => d.Material.name));

                foreach (var (a, b) in new[] { (first, second), (second, first) })
                {
                    device.MeshDraws.Clear();
                    Graphics.DrawMesh(mesh, Matrix4x4.Translate(new Vector3(0f, 0f, -23.5f)), a, 0);
                    Graphics.DrawMesh(mesh, Matrix4x4.Translate(new Vector3(0f, 0f, -23.5f)), b, 0);
                    Frame();
                    Assert.Equal("second,first", Drawn());
                }
                // Within a float step at the camera's distance: still a tie.
                device.MeshDraws.Clear();
                Graphics.DrawMesh(mesh, Matrix4x4.Translate(new Vector3(0f, 0f, -23.5f + 9.174312e-05f)), first, 0);
                Graphics.DrawMesh(mesh, Matrix4x4.Translate(new Vector3(0f, 0f, -23.5f + 8.928571e-05f)), second, 0);
                Frame();
                Assert.Equal("second,first", Drawn());
                // Nearer draws last.
                device.MeshDraws.Clear();
                Graphics.DrawMesh(mesh, Matrix4x4.Translate(new Vector3(0f, 0f, -23.6f)), first, 0);
                Graphics.DrawMesh(mesh, Matrix4x4.Translate(new Vector3(0f, 0f, -23.5f)), second, 0);
                Frame();
                Assert.Equal("second,first", Drawn());
                device.MeshDraws.Clear();
                Graphics.DrawMesh(mesh, Matrix4x4.Translate(new Vector3(0f, 0f, -23.5f)), first, 0);
                Graphics.DrawMesh(mesh, Matrix4x4.Translate(new Vector3(0f, 0f, -23.6f)), second, 0);
                Frame();
                Assert.Equal("first,second", Drawn());
            }
            finally
            {
                GraphicsDevice.Install(previous);
            }
        }
    }
}
