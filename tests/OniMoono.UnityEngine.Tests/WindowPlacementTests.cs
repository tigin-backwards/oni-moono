using Brutal.Numerics;
using OniMoono.Desktop;
using Xunit;

namespace OniMoono.Tests
{
    /// Where the window goes on Windows: windowed requests too tall for the work area are
    /// maximized, and a maximized window made smaller is centred on its monitor.
    public class WindowPlacementTests
    {
        // A 3840x2160 monitor at 150% scaling, whose title bar and bottom border measured 45 + 11.
        static readonly int2 WorkArea = new(3840, 2160);
        const int FrameHeight = 56;

        [Fact]
        public void ANativeResolutionWindowDoesNotFit()
        {
            Assert.False(GlfwPlatform.FitsWorkArea(new int2(3840, 2160), FrameHeight, WorkArea));
            // What Windows made of it, saved by the game and asked for again on the next start.
            Assert.False(GlfwPlatform.FitsWorkArea(new int2(3840, 2130), FrameHeight, WorkArea));
        }

        [Fact]
        public void AWindowWithItsFrameOnScreenFits()
        {
            Assert.True(GlfwPlatform.FitsWorkArea(new int2(2560, 1440), FrameHeight, WorkArea));
            Assert.True(GlfwPlatform.FitsWorkArea(new int2(3840, 2160 - FrameHeight), FrameHeight, WorkArea));
            Assert.False(GlfwPlatform.FitsWorkArea(new int2(3840, 2160 - FrameHeight + 1), FrameHeight, WorkArea));
        }

        [Fact]
        public void AWindowWiderThanTheWorkAreaDoesNotFit()
            => Assert.False(GlfwPlatform.FitsWorkArea(new int2(3841, 1440), FrameHeight, WorkArea));

        [Fact]
        public void ARestoredWindowIsCentredOnItsMonitorAsUnitys()
        {
            // Measured with the original: maximized on a 2560x1440 second monitor at 2560,0 (100%
            // scaling: borders 8, title bar 23), then 1024x768 windowed.
            var position = GlfwPlatform.CentredPosition(new int2(2560, 0), new int2(2560, 1440), new int2(1024, 768), 8, 31, 8, 8);
            Assert.Equal(new int2(3328, 347), position);
        }

        [Fact]
        public void ARestoredWindowStaysOnAMonitorLeftOfThePrimary()
            => Assert.Equal(new int2(-1600, 191),
                GlfwPlatform.CentredPosition(new int2(-1920, 0), new int2(1920, 1080), new int2(1280, 720), 8, 31, 8, 8));
    }
}
