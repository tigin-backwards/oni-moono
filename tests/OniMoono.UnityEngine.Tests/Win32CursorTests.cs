using System;
using System.IO;
using System.Runtime.InteropServices;
using OniMoono.Desktop;
using Xunit;

namespace OniMoono.Tests
{
    /// Custom cursors on Windows load as the original's do (issue #62): LoadImageW from a .cur
    /// file at the system's cursor size (LR_DEFAULTSIZE), so a high-DPI main monitor shows them
    /// as large as the original does.
    public class Win32CursorTests
    {
        static byte[] Image(int size)
        {
            var rgba = new byte[size * size * 4];
            for (int i = 3; i < rgba.Length; i += 8) rgba[i] = 255;
            return rgba;
        }

        [Fact]
        public void LoadsTheFileAsTheOriginalDoes()
        {
            (string name, uint type, int width, int height, uint flags, byte[] file) call = default;
            nint handle = Win32Cursor.Load(Image(32), 32, 32, 2, 2, (module, name, type, width, height, flags) =>
            {
                call = (name, type, width, height, flags, File.ReadAllBytes(name));
                return 42;
            });
            Assert.Equal(42, handle);
            // The original player's call: LoadImageW(NULL, file, IMAGE_CURSOR, 0, 0, 0x50).
            Assert.Equal((Win32Cursor.IMAGE_CURSOR, 0, 0, 0x50u), (call.type, call.width, call.height, call.flags));
            Assert.Equal(Win32Cursor.LR_LOADFROMFILE | Win32Cursor.LR_DEFAULTSIZE, call.flags);
            Assert.Equal(Win32Cursor.CurFile(Image(32), 32, 32, 2, 2), call.file);
            Assert.False(File.Exists(call.name)); // removed once loaded
        }

        [Fact]
        public void TooLargeImagesAreNotLoaded()
        {
            Assert.Equal(0, Win32Cursor.Load(Image(257), 257, 257, 0, 0, (_, _, _, _, _, _) => throw new InvalidOperationException()));
        }

        [Fact]
        public void TheCurFileHoldsOneImageWithItsHotspot()
        {
            var file = Win32Cursor.CurFile(Image(32), 32, 32, 2, 3);
            Assert.Equal(new byte[] { 0, 0, 2, 0, 1, 0, 32, 32, 0, 0, 2, 0, 3, 0 }, file[..14]);
            Assert.Equal(22 + 40 + 32 * 32 * 4 + 4 * 32, file.Length);
        }

        /// The real load, on Windows: the cursor comes out at the system's cursor size whatever
        /// the image's, as the original's does (SM_CXCURSOR is 32 at 100%, 48 at 150%).
        [Fact]
        public void OnWindowsTheCursorHasTheSystemCursorSize()
        {
            if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only (LoadImageW)");
            nint cursor = Win32Cursor.Load(Image(64), 64, 64, 4, 4, Win32Cursor.LoadImageW);
            Assert.NotEqual(0, cursor);
            try
            {
                Assert.True(GetIconInfo(cursor, out var info));
                try
                {
                    Assert.NotEqual(0, GetObjectW(info.color, Marshal.SizeOf<BITMAP>(), out var bitmap));
                    Assert.Equal((GetSystemMetrics(13), GetSystemMetrics(14)), (bitmap.width, bitmap.height));
                }
                finally
                {
                    DeleteObject(info.color);
                    DeleteObject(info.mask);
                }
            }
            finally { DestroyCursor(cursor); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO { public int icon, x, y; public nint mask, color; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int type, width, height, stride; public short planes, bpp; public nint bits; }

        [DllImport("user32.dll")] static extern bool GetIconInfo(nint icon, out ICONINFO info);
        [DllImport("user32.dll")] static extern bool DestroyCursor(nint cursor);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("gdi32.dll")] static extern int GetObjectW(nint handle, int size, out BITMAP bitmap);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(nint handle);
    }
}
