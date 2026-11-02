using Brutal.Numerics;
using OniMoono.Desktop;
using OniMoono.Engine.Platform;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// A fullscreen window at a resolution smaller than the monitor's renders at that resolution,
    /// stretched over the window, as the original does (measured on a 1280x720 Xvfb screen with
    /// Screen.SetResolution(…, FullScreenMode.FullScreenWindow)).
    public class FullScreenWindowTests
    {
        static readonly int2 Window = new(1280, 720);

        [Fact]
        public void RendersAtTheSizeAskedFor()
        {
            Assert.Equal(new int2(960, 720), GlfwPlatform.FullScreenWindowRendering(new int2(960, 720), Window));
            Assert.Equal(new int2(1024, 576), GlfwPlatform.FullScreenWindowRendering(new int2(1024, 576), Window));
            Assert.Equal(new int2(800, 600), GlfwPlatform.FullScreenWindowRendering(new int2(800, 600), Window));
            Assert.Equal(Window, GlfwPlatform.FullScreenWindowRendering(new int2(1280, 720), Window));
        }

        [Fact]
        public void ALargerSizeIsTheWindows()
        {
            // Measured per axis: 1600x900 gave 1280x720, 1600x600 1280x600, 640x900 640x720 and
            // 1024x768 1024x720.
            Assert.Equal(Window, GlfwPlatform.FullScreenWindowRendering(new int2(1600, 900), Window));
            Assert.Equal(new int2(1280, 600), GlfwPlatform.FullScreenWindowRendering(new int2(1600, 600), Window));
            Assert.Equal(new int2(640, 720), GlfwPlatform.FullScreenWindowRendering(new int2(640, 900), Window));
            Assert.Equal(new int2(1024, 720), GlfwPlatform.FullScreenWindowRendering(new int2(1024, 768), Window));
        }

        [Fact]
        public void OtherModesRenderAtTheWindowsSize()
        {
            Assert.Equal(Window, GlfwPlatform.FullScreenWindowRendering(null, Window));
            // A degenerate request, or a window with no framebuffer, changes nothing.
            Assert.Equal(Window, GlfwPlatform.FullScreenWindowRendering(new int2(0, 720), Window));
            Assert.Equal(new int2(0, 0), GlfwPlatform.FullScreenWindowRendering(new int2(960, 720), new int2(0, 0)));
        }

        /// The window's pixel (top left) as Input.mousePosition (bottom left), as GlfwPlatform maps it.
        static Vector2 Mouse(float x, float y, int2 rendering)
            => PointerPixels.Screen(GlfwPlatform.WindowToRendering(new Vector2(x, y), Window, rendering), rendering.Y);

        [Theory]
        // The original's Input.mousePosition with the pointer on these pixels of the screen.
        [InlineData(960, 720, 0, 0, 0f, 719f)]
        [InlineData(960, 720, 160, 0, 120f, 719f)]
        [InlineData(960, 720, 161, 1, 120.75f, 718f)]
        [InlineData(960, 720, 640, 360, 480f, 359f)]
        [InlineData(960, 720, 1279, 719, 959.25f, 0f)]
        [InlineData(960, 720, 1200, 100, 900f, 619f)]
        [InlineData(1024, 576, 161, 1, 128.8f, 574.2f)]
        [InlineData(1024, 576, 1279, 719, 1023.2f, -0.2000122f)]
        [InlineData(800, 600, 100, 100, 62.5f, 515.6667f)]
        [InlineData(800, 600, 1119, 719, 699.375f, -0.16668701f)]
        [InlineData(1280, 720, 161, 1, 161f, 718f)]
        public void ThePointerMapsIntoTheRenderedFrame(int w, int h, float x, float y, float mouseX, float mouseY)
        {
            var mouse = Mouse(x, y, new int2(w, h));
            Assert.Equal(mouseX, mouse.x, 3);
            Assert.Equal(mouseY, mouse.y, 3);
        }
    }
}
