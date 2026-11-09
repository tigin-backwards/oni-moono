using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OniMoono.Desktop
{
    /// Custom cursors on Windows that follow the pointer size (Settings > Accessibility > Mouse
    /// pointer > Size), as the original player's do.
    ///
    /// Measured on Windows 11 25H2 with desktop duplication (the pointer shape Windows displays):
    /// at pointer size 64 the original shows its 32x32 cursor as a 64x64 shape with the hotspot
    /// at 4,4 (48: 48x48 at 3,3), and follows size changes while it runs with the same cursor
    /// handle; GetIconInfo on that handle still gives 32x32 at 2,2. A cursor from
    /// CreateIconIndirect (what glfwCreateCursor makes) or CreateIconFromResourceEx (any flags,
    /// DIB or PNG) stays 32x32 at every size, in any DPI awareness. A cursor that LoadImage reads
    /// from a .cur file (with or without LR_DEFAULTSIZE or LR_SHARED, DIB or PNG) is scaled as
    /// the original's is, live too, pixel for pixel the same shapes. So the image goes through a
    /// temporary .cur file, and the cursor stays ours rather than GLFW's: the window's procedure
    /// is subclassed to answer WM_SETCURSOR over the client area with it while the cursor is
    /// shown (GLFW's Normal and Captured modes, where GLFW would show its own); everything else,
    /// hidden and disabled modes, the borders and the default arrow, stays GLFW's.
    ///
    /// The load is the original's call (traced on its player): LoadImageW(NULL, file,
    /// IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE). LR_DEFAULTSIZE loads the image at
    /// the system's cursor size, SM_CXCURSOR at the system DPI (the main monitor's at sign-in):
    /// 32 at 100%, 48 at 150%, 64 at 200%. So on a high-DPI main monitor the original's 32x32
    /// cursor is a 48x48 or 64x64 one, which the pointer size then scales as before (issue #62).
    internal sealed unsafe class Win32Cursor : IDisposable
    {
        internal const uint IMAGE_CURSOR = 2, LR_LOADFROMFILE = 0x10, LR_DEFAULTSIZE = 0x40;
        const uint WM_SETCURSOR = 0x20, WM_NCHITTEST = 0x84;
        const int GWLP_WNDPROC = -4, HTCLIENT = 1;

        delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

        readonly nint hwnd;
        readonly nint glfwProc;
        readonly WndProc proc; // kept alive while the window uses it
        nint cursor;
        bool shown = true;

        public Win32Cursor(nint hwnd)
        {
            this.hwnd = hwnd;
            proc = WindowProc;
            glfwProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
        }

        /// Makes the cursor from top-down RGBA pixels and shows it; false (and nothing changed)
        /// when Windows does not load it.
        public bool Set(byte[] rgba, int width, int height, int hotX, int hotY)
        {
            nint loaded = Load(rgba, width, height, hotX, hotY, LoadImageW);
            if (loaded == 0) return false;
            var previous = cursor;
            cursor = loaded;
            ShowIfOver();
            if (previous != 0) DestroyCursor(previous);
            return true;
        }

        /// Back to GLFW's cursor; the caller has GLFW set it.
        public void Clear()
        {
            if (cursor != 0) DestroyCursor(cursor);
            cursor = 0;
        }

        /// Whether GLFW's cursor mode shows a cursor. Called after GLFW applied the mode, which
        /// set its own cursor (the arrow) if the pointer is over the client area.
        public bool Shown
        {
            set
            {
                shown = value;
                ShowIfOver();
            }
        }

        /// As GLFW does for its own cursors: the new cursor at once, not at the next mouse move.
        void ShowIfOver()
        {
            if (cursor == 0 || !shown || !GetCursorPos(out var p) || WindowFromPoint(p) != hwnd) return;
            if (SendMessageW(hwnd, WM_NCHITTEST, 0, (nint)(((p.y & 0xffff) << 16) | (p.x & 0xffff))) == HTCLIENT)
                SetCursor(cursor);
        }

        nint WindowProc(nint window, uint message, nint wParam, nint lParam)
        {
            if (message == WM_SETCURSOR && cursor != 0 && shown && (lParam & 0xffff) == HTCLIENT)
            {
                SetCursor(cursor);
                return 1;
            }
            return CallWindowProcW(glfwProc, window, message, wParam, lParam);
        }

        public void Dispose()
        {
            SetWindowLongPtrW(hwnd, GWLP_WNDPROC, glfwProc);
            Clear();
        }

        internal delegate nint LoadImage(nint module, string name, uint type, int width, int height, uint flags);

        /// The cursor loaded from a temporary .cur file of the image, as the original loads its
        /// own; 0 when it is too large for a .cur file, the file cannot be written or Windows
        /// does not load it.
        internal static nint Load(byte[] rgba, int width, int height, int hotX, int hotY, LoadImage load)
        {
            if (width > 256 || height > 256) return 0;
            string path = Path.Combine(Path.GetTempPath(), $"oni-moono-cursor-{Environment.ProcessId}.cur");
            try
            {
                File.WriteAllBytes(path, CurFile(rgba, width, height, hotX, hotY));
                return load(0, path, IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
            finally { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        /// A .cur file with one 32 bpp image: the bottom-up BGRA rows, then the AND mask (set
        /// where alpha is 0).
        internal static byte[] CurFile(byte[] rgba, int width, int height, int hotX, int hotY)
        {
            int maskStride = (width + 31) / 32 * 4;
            int image = 40 + width * height * 4 + maskStride * height;
            var data = new byte[22 + image];
            var w = new BinaryWriter(new MemoryStream(data));
            w.Write((short)0); w.Write((short)2); w.Write((short)1);
            w.Write((byte)width); w.Write((byte)height); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)hotX); w.Write((short)hotY); w.Write(image); w.Write(22);
            w.Write(40); w.Write(width); w.Write(height * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(width * height * 4 + maskStride * height); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = height - 1; y >= 0; y--)
                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 4;
                    w.Write(rgba[o + 2]); w.Write(rgba[o + 1]); w.Write(rgba[o]); w.Write(rgba[o + 3]);
                }
            int mask = 22 + 40 + width * height * 4;
            for (int y = height - 1, row = 0; y >= 0; y--, row++)
                for (int x = 0; x < width; x++)
                    if (rgba[(y * width + x) * 4 + 3] == 0) data[mask + row * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
            return data;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint LoadImageW(nint module, string name, uint type, int width, int height, uint flags);

        [DllImport("user32.dll")]
        static extern bool DestroyCursor(nint cursor);

        [DllImport("user32.dll")]
        static extern nint SetCursor(nint cursor);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        static extern nint WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

        [DllImport("user32.dll")]
        static extern nint CallWindowProcW(nint proc, nint hwnd, uint message, nint wParam, nint lParam);
    }
}
