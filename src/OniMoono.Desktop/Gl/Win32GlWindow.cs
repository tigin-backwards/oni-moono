using System;
using System.Runtime.InteropServices;
using OniMoono.Engine;

namespace OniMoono.Desktop.Gl
{
    /// Windows: OpenGL's SwapBuffers on a window made without a context (GlfwPlatform's
    /// ContextFreeWindow, with DXGI on), for when DXGI cannot present. The visible window is
    /// left without a pixel format until then, so that only our swap chain presents to it; here
    /// it takes a pixel format and a context, and presents double-buffered, as it always did.
    /// The presenter's context lives on a single-buffered hidden window (GlfwPlatform's
    /// HiddenGlWindow: GLFW swaps a double-buffered window once as it makes its context, which
    /// overlays take for OpenGL presentation): the window gets the double-buffered pixel format
    /// closest to it and a context of its own with the same version, profile and flags, sharing
    /// its objects (the presenter remakes those contexts do not share, GlPresenter.NoteContext).
    /// When the hidden window is double-buffered (a driver without single-buffered formats),
    /// the window takes its pixel format and the presenter's context itself: wglMakeCurrent
    /// takes any device context on the same device with the context's pixel format, not only the
    /// one the context was made with (wglMakeCurrent's documentation). A window's pixel format
    /// can be set only once (SetPixelFormat), which is why the visible window is not given one
    /// before it is needed. Dispose puts the presenter's context back on its hidden window, so
    /// that DXGI restarts render as they started. Not on the window (DXGI restarts pending:
    /// GlPresenter.SwapTarget), or when it cannot be put there, nothing is swapped: the pictures
    /// go nowhere, paced by the compositor (DwmFlush) with vsync. Presenter thread only, with
    /// the presenter's context current on its hidden window when made.
    internal sealed unsafe class Win32GlWindow : IDisposable
    {
        readonly IntPtr hwnd, windowDc, hiddenDc, context;
        // A double-buffered context made for the window, sharing the presenter's objects, when the
        // presenter's own is single-buffered (GlfwPlatform.HiddenGlWindow); else zero.
        IntPtr ownContext;
        // On the visible window (false: the context stays on its hidden window and nothing is
        // swapped).
        readonly bool bound;
        delegate* unmanaged<int, int> swapIntervalExt;
        static bool logged;

        /// The current context (presenter thread).
        public static IntPtr CurrentContext => wglGetCurrentContext();

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
            if (bound && !logged)
                EngineLog.Engine($"present: {(ownContext != IntPtr.Zero ? "a double-buffered OpenGL context sharing the presenter's is made for the window" : "the presenter's OpenGL context is put on the window")} for SwapBuffers; {Describe(windowDc)}");
            logged |= bound;
            if (!bound) EngineLog.Engine($"present: OpenGL's SwapBuffers cannot reach the window: {failure}");
        }

        /// The pixel format of the window `dc` belongs to, and whether it is double-buffered.
        public static string Describe(IntPtr dc)
        {
            int format = GetPixelFormat(dc);
            if (format == 0) return "the window has no pixel format";
            PixelFormatDescriptor pfd = default;
            if (DescribePixelFormat(dc, format, (uint)sizeof(PixelFormatDescriptor), &pfd) == 0) return $"pixel format {format} (not described)";
            return $"pixel format {format}, {((pfd.Flags & DoubleBuffer) != 0 ? "double-buffered" : "single-buffered")}";
        }

        /// Describe for window `hwnd` (its own device context).
        public static string DescribeWindow(IntPtr hwnd)
        {
            IntPtr dc = GetDC(hwnd);
            if (dc == IntPtr.Zero) return "no device context";
            try { return Describe(dc); }
            finally { ReleaseDC(hwnd, dc); }
        }

        const uint DoubleBuffer = 0x1, DoubleBufferDontCare = 0x40000000;

        string Bind()
        {
            if (context == IntPtr.Zero || hiddenDc == IntPtr.Zero) return "no context is current";
            if (windowDc == IntPtr.Zero) return $"GetDC failed (error {Marshal.GetLastPInvokeError()})";
            int format = GetPixelFormat(hiddenDc);
            if (format == 0) return $"the context's window has no pixel format (error {Marshal.GetLastPInvokeError()})";
            PixelFormatDescriptor pfd = default;
            if (DescribePixelFormat(hiddenDc, format, (uint)sizeof(PixelFormatDescriptor), &pfd) == 0)
                return $"DescribePixelFormat {format} failed (error {Marshal.GetLastPInvokeError()})";
            // SwapBuffers presents double-buffered, as it always did: a single-buffered context
            // (made so that GLFW does not swap the hidden windows) gets a double-buffered one of
            // its own for the window, sharing its objects.
            bool single = (pfd.Flags & DoubleBuffer) == 0;
            if (single)
            {
                pfd.Flags = (pfd.Flags | DoubleBuffer) & ~DoubleBufferDontCare;
                int chosen = GetPixelFormat(windowDc);
                if (chosen == 0) chosen = ChoosePixelFormat(windowDc, &pfd);
                if (chosen == 0) return $"no double-buffered pixel format (ChoosePixelFormat error {Marshal.GetLastPInvokeError()})";
                PixelFormatDescriptor got = default;
                if (DescribePixelFormat(windowDc, chosen, (uint)sizeof(PixelFormatDescriptor), &got) == 0 || (got.Flags & DoubleBuffer) == 0)
                    return $"pixel format {chosen} is not double-buffered";
                format = chosen;
                pfd = got;
            }
            int current = GetPixelFormat(windowDc);
            if (current == 0)
            {
                if (!SetPixelFormat(windowDc, format, &pfd)) return $"SetPixelFormat {format} failed (error {Marshal.GetLastPInvokeError()})";
            }
            else if (current != format) return $"the window has pixel format {current}, the context {format}";
            if (single)
            {
                string made = MakeOwnContext();
                if (made != null) return made;
            }
            if (!wglMakeCurrent(windowDc, single ? ownContext : context))
            {
                int error = Marshal.GetLastPInvokeError();
                DeleteOwnContext();
                return $"wglMakeCurrent failed (error {error})";
            }
            return null;
        }

        /// A context for the window like the current one (version, profile and flags, read from
        /// it), sharing its objects (WGL_ARB_create_context).
        string MakeOwnContext()
        {
            var create = (delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr>)Brutal.GlfwApi.Glfw.GetProcAddress("wglCreateContextAttribsARB"u8);
            if (create == null) return "no wglCreateContextAttribsARB";
            int major = 0, minor = 0, profile = 0, flags = 0;
            glGetIntegerv(0x821B, &major); // GL_MAJOR_VERSION
            glGetIntegerv(0x821C, &minor); // GL_MINOR_VERSION
            glGetIntegerv(0x9126, &profile); // GL_CONTEXT_PROFILE_MASK
            glGetIntegerv(0x821E, &flags); // GL_CONTEXT_FLAGS
            // GL_CONTEXT_FLAG_FORWARD_COMPATIBLE_BIT 1 and DEBUG_BIT 2 are WGL's 2 and 1.
            int wglFlags = ((flags & 1) != 0 ? 2 : 0) | ((flags & 2) != 0 ? 1 : 0);
            int* attributes = stackalloc int[] { 0x2091, major, 0x2092, minor, 0x9126, profile, 0x2094, wglFlags, 0 };
            ownContext = create(windowDc, context, attributes);
            return ownContext == IntPtr.Zero ? $"wglCreateContextAttribsARB failed (error 0x{Marshal.GetLastPInvokeError():X})" : null;
        }

        void DeleteOwnContext()
        {
            if (ownContext == IntPtr.Zero) return;
            wglDeleteContext(ownContext);
            ownContext = IntPtr.Zero;
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
            DeleteOwnContext();
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

        [DllImport("opengl32.dll")]
        static extern bool wglDeleteContext(IntPtr context);

        [DllImport("opengl32.dll")]
        static extern void glGetIntegerv(uint name, int* value);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern int ChoosePixelFormat(IntPtr dc, PixelFormatDescriptor* pfd);

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
