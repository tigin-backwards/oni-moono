using System;
using System.Runtime.InteropServices;
using OniMoono.Engine;

namespace OniMoono.Desktop.Gl
{
    /// Windows: OpenGL's SwapBuffers on a window made without a context (GlfwPlatform's
    /// ContextFreeWindow, with DXGI on), for when DXGI cannot present. The visible window is
    /// left without a pixel format until then, so that only our swap chain presents to it; here
    /// it takes the pixel format of the hidden window the presenter's context was made on, and
    /// that context is made current on it. WGL allows that: wglMakeCurrent takes any device
    /// context on the same device with the context's pixel format, not only the one the context
    /// was made with (wglMakeCurrent's documentation), so the context and every object it holds
    /// stay. A window's pixel format can be set only once (SetPixelFormat), which is why the
    /// visible window is not given one before it is needed. Dispose puts the context back on its
    /// hidden window, so that DXGI restarts render as they started. Not on the window (DXGI
    /// restarts pending: GlPresenter.SwapTarget), or when it cannot be put there, nothing is
    /// swapped: the pictures go nowhere, paced by the compositor (DwmFlush) with vsync.
    /// Presenter thread only, with the presenter's context current on its hidden window when
    /// made.
    internal sealed unsafe class Win32GlWindow : IDisposable
    {
        readonly IntPtr hwnd, windowDc, hiddenDc, context;
        // On the visible window (false: the context stays on its hidden window and nothing is
        // swapped).
        readonly bool bound;
        delegate* unmanaged<int, int> swapIntervalExt;
        static bool logged;

        public Win32GlWindow(IntPtr hwnd, bool onWindow)
        {
            this.hwnd = hwnd;
            context = wglGetCurrentContext();
            hiddenDc = wglGetCurrentDC();
            swapIntervalExt = (delegate* unmanaged<int, int>)Brutal.GlfwApi.Glfw.GetProcAddress("wglSwapIntervalEXT"u8);
            if (!onWindow) return;
            windowDc = GetDC(hwnd);
            string failure = Bind();
            bound = failure == null;
            if (bound && !logged) EngineLog.Engine("present: the presenter's OpenGL context is put on the window for SwapBuffers");
            logged |= bound;
            if (!bound) EngineLog.Engine($"present: OpenGL's SwapBuffers cannot reach the window: {failure}");
        }

        string Bind()
        {
            if (context == IntPtr.Zero || hiddenDc == IntPtr.Zero) return "no context is current";
            if (windowDc == IntPtr.Zero) return $"GetDC failed (error {Marshal.GetLastPInvokeError()})";
            int format = GetPixelFormat(hiddenDc);
            if (format == 0) return $"the context's window has no pixel format (error {Marshal.GetLastPInvokeError()})";
            int current = GetPixelFormat(windowDc);
            if (current == 0)
            {
                PixelFormatDescriptor pfd = default;
                if (DescribePixelFormat(hiddenDc, format, (uint)sizeof(PixelFormatDescriptor), &pfd) == 0)
                    return $"DescribePixelFormat {format} failed (error {Marshal.GetLastPInvokeError()})";
                if (!SetPixelFormat(windowDc, format, &pfd)) return $"SetPixelFormat {format} failed (error {Marshal.GetLastPInvokeError()})";
            }
            else if (current != format) return $"the window has pixel format {current}, the context {format}";
            if (!wglMakeCurrent(windowDc, context)) return $"wglMakeCurrent failed (error {Marshal.GetLastPInvokeError()})";
            return null;
        }

        /// wglSwapIntervalEXT applies to the window of the current context's drawable
        /// (WGL_EXT_swap_control), so it is set again once the context is on the window.
        public void SwapInterval(int interval)
        {
            if (bound && swapIntervalExt != null) swapIntervalExt(interval);
        }

        /// Swaps on the window; when the pictures go nowhere, waits for `interval` compositor
        /// frames instead.
        public void SwapBuffers(int interval)
        {
            if (bound) SwapBuffersGdi(windowDc);
            else
                for (int i = 0; i < interval; i++)
                    DwmFlush();
        }

        public void Dispose()
        {
            if (bound && !wglMakeCurrent(hiddenDc, context))
                EngineLog.Engine($"present: wglMakeCurrent back on the hidden window failed (error {Marshal.GetLastPInvokeError()})");
            // The window's class has CS_OWNDC (GLFW's): its DC is its own and ReleaseDC does
            // nothing to it, as documented; released all the same.
            if (windowDc != IntPtr.Zero) ReleaseDC(hwnd, windowDc);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PixelFormatDescriptor
        {
            public ushort Size, Version;
            public uint Flags;
            public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift, AlphaBits, AlphaShift;
            public byte AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits, DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
            public uint LayerMask, VisibleMask, DamageMask;
        }

        [DllImport("opengl32.dll")]
        static extern IntPtr wglGetCurrentContext();

        [DllImport("opengl32.dll")]
        static extern IntPtr wglGetCurrentDC();

        [DllImport("opengl32.dll", SetLastError = true)]
        static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern int GetPixelFormat(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern int DescribePixelFormat(IntPtr dc, int format, uint size, PixelFormatDescriptor* pfd);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool SetPixelFormat(IntPtr dc, int format, PixelFormatDescriptor* pfd);

        [DllImport("gdi32.dll", EntryPoint = "SwapBuffers")]
        static extern bool SwapBuffersGdi(IntPtr dc);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        static extern int DwmFlush();

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    }
}
