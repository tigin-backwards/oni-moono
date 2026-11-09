using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Xunit;

namespace OniMoono.Tests
{
    public class CommandBufferTests : EngineTest
    {
        [Fact]
        public void MaterialListsItsTextureProperties()
        {
            AssetSystem.Initialize(GameInstall.DataDir);
            // Shader.Find reads the shader name registry, whatever test ran before.
            ProjectSettings.Load();
            var material = new Material(Shader.Find("Hidden/Internal-GUITextureClip"));
            Assert.Contains("_MainTex", material.GetTexturePropertyNames());
            Assert.Contains(Shader.PropertyToID("_MainTex"), material.GetTexturePropertyNameIDs());
        }

        [Fact]
        public void DrawsAfterABlitKeepTheCameraMatrices()
        {
            // The game copies its render targets and then draws the liquid with the camera's
            // matrices from one command buffer (MRTLiquidInterception).
            AssetSystem.Initialize(GameInstall.DataDir);
            ProjectSettings.Load();
            var device = new RecordingDevice();
            var previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            try
            {
                var view = Matrix4x4.TRS(new Vector3(0f, 0f, 100f), Quaternion.identity, new Vector3(1f, 1f, -1f));
                var projection = Matrix4x4.Ortho(-10f, 10f, -8f, 8f, 1f, 200f);
                device.SetCamera(new CameraConstants(view, projection, new Vector3(0f, 0f, -100f), 1f, 200f, true, new Vector2(1024f, 768f)));
                var source = new RenderTexture(16, 16, 0);
                var copy = new RenderTexture(16, 16, 0);
                var mesh = new Mesh { vertices = [Vector3.zero, Vector3.up, Vector3.right], triangles = [0, 1, 2] };
                var material = new Material(Shader.Find("Hidden/Internal-GUITextureClip"));
                var cb = new CommandBuffer();
                cb.Blit(source, copy);
                cb.DrawMesh(mesh, Matrix4x4.identity, material);
                CommandExecution.Run(cb, null);
                Assert.Equal(2, device.MeshDraws.Count);
                Assert.NotEqual(projection, device.MeshDraws[0].Projection); // the blit's own
                Assert.Equal(view, device.MeshDraws[1].View);
                Assert.Equal(projection, device.MeshDraws[1].Projection);
            }
            finally
            {
                GraphicsDevice.Install(previous);
            }
        }
    }
}
