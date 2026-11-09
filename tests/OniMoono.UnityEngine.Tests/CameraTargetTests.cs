using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // A camera's pixel size is its target's: a target texture, the buffers given to
    // SetTargetBuffers (the game's world MRT camera), else the screen.
    public class CameraTargetTests : EngineTest
    {
        [Fact]
        public void TargetBuffersSizeTheCamera()
        {
            OniMoono.Engine.Assets.AssetSystem.Initialize(GameInstall.DataDir);
            OniMoono.Engine.Assets.ProjectSettings.Load();
            var camera = new GameObject("Camera").AddComponent<Camera>();
            var color = new RenderTexture(96, 40, 24, RenderTextureFormat.ARGB32);
            var other = new RenderTexture(96, 40, 0, RenderTextureFormat.ARGB32);
            camera.SetTargetBuffers(new[] { color.colorBuffer, other.colorBuffer }, color.depthBuffer);
            Assert.Equal(96, camera.pixelWidth);
            Assert.Equal(40, camera.pixelHeight);
            Assert.Equal(96f / 40f, camera.aspect, 4);

            camera.targetTexture = new RenderTexture(30, 20, 0);
            Assert.Equal(30, camera.pixelWidth);
        }

        [Fact]
        public void ADestroyedRenderTextureReleasesItsDeviceResource()
        {
            // The game's MRT proxy destroys its old targets (through RenderTextureDestroyer, two
            // frames later) when it recreates them on a resize.
            OniMoono.Engine.Assets.AssetSystem.Initialize(GameInstall.DataDir);
            OniMoono.Engine.Assets.ProjectSettings.Load();
            var device = new RecordingDevice();
            var previous = OniMoono.Engine.Rendering.GraphicsDevice.Current;
            OniMoono.Engine.Rendering.GraphicsDevice.Install(device);
            try
            {
                var rt = new RenderTexture(96, 40, 24, RenderTextureFormat.ARGB32);
                var gpu = new object();
                var native = (OniMoono.Engine.Rendering.RenderTextureNative)rt.Native;
                native.Gpu = gpu;
                OniMoono.Engine.Rendering.GraphicsDevice.Adopt(native, gpu);
                Object.Destroy(rt);
                Frame();
                Assert.Contains(gpu, device.Released);
            }
            finally
            {
                OniMoono.Engine.Rendering.GraphicsDevice.Install(previous);
            }
        }
    }
}
