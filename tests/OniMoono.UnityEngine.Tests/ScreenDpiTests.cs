using OniMoono.Desktop;
using Xunit;

namespace OniMoono.Tests
{
    /// Screen.dpi as the original reports it (ScreenDpi).
    public class ScreenDpiTests
    {
        [Theory]
        // Measured in the original on Windows: GetDpiForWindow's value at 100%, 125% and 150%.
        [InlineData(96u, 96u, 96f)]
        [InlineData(120u, 120u, 120f)]
        [InlineData(144u, 144u, 144f)]
        // The window's DPI wins over its monitor's (they differ only while WM_DPICHANGED is due).
        [InlineData(120u, 144u, 120f)]
        // No window DPI (GetDpiForWindow failed or is missing): the monitor's effective DPI.
        [InlineData(0u, 144u, 144f)]
        public void WindowsReportsTheWindowsLogicalDpi(uint window, uint monitor, float expected)
            => Assert.Equal(expected, ScreenDpi.Windows(window, monitor));

        [Fact]
        public void APhysicalSizeGivesThePhysicalDpi()
        {
            // 2560 pixels over 598 mm: the 108.74 our Windows player reported before.
            Assert.Equal(108.736f, ScreenDpi.Physical(2560, 598), 3);
        }

        [Fact]
        public void NoPhysicalSizeGivesZero()
        {
            Assert.Equal(0f, ScreenDpi.Physical(1280, 0));
            Assert.Equal(0f, ScreenDpi.Physical(1280, -1));
        }
    }
}
