using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// Setting RenderTexture.active is Graphics.SetRenderTarget, as measured in the original
    /// (UnityProbe's apigaps "rt"): GL.Clear and GL drawing go to it, the viewport covers it
    /// whole (also when it was active already), it is created, and null is the back buffer.
    public class ActiveRenderTargetTests : EngineTest
    {
        static RecordingDevice Install(out RenderDevice previous)
        {
            var device = new RecordingDevice();
            previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            return device;
        }

        [Fact]
        public void SettingActiveBindsTheTextureForClears()
        {
            var device = Install(out var previous);
            try
            {
                var a = new RenderTexture(4, 4, 0);
                var b = new RenderTexture(8, 8, 0);
                Graphics.SetRenderTarget(b);
                RenderTexture.active = a;
                GL.Clear(false, true, Color.green);
                Assert.Same(a, RenderTexture.active);
                Assert.Same(a.NativeOrThrow, device.Clears[^1].Target);
                Assert.Equal(Color.green, device.Clears[^1].Color);
            }
            finally
            {
                RenderTexture.active = null;
                GraphicsDevice.Install(previous);
            }
        }

        [Fact]
        public void SettingActiveCoversTheWholeTargetEvenWhenItWasActive()
        {
            var device = Install(out var previous);
            try
            {
                var a = new RenderTexture(8, 8, 0);
                Graphics.SetRenderTarget(a);
                GL.Viewport(new Rect(0, 0, 2, 2));
                RenderTexture.active = a;
                Assert.Equal(new Rect(0, 0, 8, 8), device.Viewports[^1]);
            }
            finally
            {
                RenderTexture.active = null;
                GraphicsDevice.Install(previous);
            }
        }

        [Fact]
        public void SettingActiveCreatesTheTexture()
        {
            var device = Install(out var previous);
            try
            {
                var t = new RenderTexture(4, 4, 0);
                Assert.False(t.IsCreated());
                RenderTexture.active = t;
                Assert.True(t.IsCreated());
            }
            finally
            {
                RenderTexture.active = null;
                GraphicsDevice.Install(previous);
            }
        }

        [Fact]
        public void NullBindsTheBackBuffer()
        {
            var device = Install(out var previous);
            try
            {
                Graphics.SetRenderTarget(new RenderTexture(4, 4, 0));
                RenderTexture.active = null;
                GL.Clear(false, true, Color.red);
                Assert.Null(RenderTexture.active);
                Assert.Null(device.Clears[^1].Target);
            }
            finally
            {
                GraphicsDevice.Install(previous);
            }
        }
    }
}
