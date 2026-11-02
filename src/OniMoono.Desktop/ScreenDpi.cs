using System;
using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using Brutal.GlfwApi.Win32;
using Brutal.GlfwApi.X11;

namespace OniMoono.Desktop
{
    /// Screen.dpi as the original player reports it. The game reads it in
    /// ScreenResolutionMonitor.UpdateShouldUseGamepadUIMode (Screen.dpi > 130 with Screen.height
    /// under 900 switches the large build and tool menus on), so a physical DPI where the original
    /// gives a logical one changes the game's layout.
    internal static unsafe class ScreenDpi
    {
        /// Windows: the window's DPI. Measured in the original (a per-monitor-v2 window, U59) it is
        /// GetDpiForWindow's: 96 at 100% (a 2560x1440 monitor of 108.74 physical DPI), 120 at 125%
        /// and 144 at 150% (a 2560x1440 virtual monitor of 130.048), never the physical DPI.
        /// GetDpiForWindow is 0 for an invalid window (and missing before Windows 10 1607): the
        /// monitor's effective DPI then.
        internal static float Windows(uint windowDpi, uint monitorEffectiveDpi) => windowDpi != 0 ? windowDpi : monitorEffectiveDpi;

        internal static float ReadWindows(GlfwWindow window, GlfwMonitor monitor)
        {
            var hwnd = (IntPtr)window.GetWin32Window();
            uint windowDpi = 0, monitorDpi = 0;
            try
            {
                windowDpi = GetDpiForWindow(hwnd);
            }
            catch (EntryPointNotFoundException) { }
            if (windowDpi == 0)
            {
                try
                {
                    if (GetDpiForMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), MDT_EFFECTIVE_DPI, out var x, out _) == 0) monitorDpi = x;
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
                // GLFW's content scale is the same effective DPI over 96.
                if (monitorDpi == 0) monitorDpi = (uint)MathF.Round(monitor.ContentScale.X * 96f);
            }
            return Windows(windowDpi, monitorDpi);
        }

        const uint MONITOR_DEFAULTTONEAREST = 2;
        const int MDT_EFFECTIVE_DPI = 0;

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        /// Other platforms: the monitor's physical DPI from its pixel width and reported size (0
        /// when it reports none, as Unity).
        internal static float Physical(int widthPixels, int widthMillimetres)
            => widthMillimetres <= 0 ? 0f : widthPixels / (widthMillimetres / 25.4f);

        /// X11: the physical DPI of the monitor's RandR output, from the size the output reports.
        /// GLFW gives an output that reports none (an Xvfb's, whatever its -dpi) the size of 96 DPI,
        /// which read as 96.19 on a 1280x720 Xvfb, where the original reads 0 (measured on Xvfb:
        /// 0 by default and with -dpi 72, 120, 144 and 200).
        internal static float ReadX11(GlfwMonitor monitor)
        {
            var display = X11Extensions.GetX11Display();
            var output = monitor.GetX11Monitor();
            if (display == 0 || output == 0) return 0f;
            var resources = XRRGetScreenResourcesCurrent(display, XDefaultRootWindow(display));
            if (resources == 0) return 0f;
            try
            {
                var info = XRRGetOutputInfo(display, resources, (nuint)output);
                if (info == null) return 0f;
                try
                {
                    return Physical(monitor.VideoMode.Width, (int)info->MillimetreWidth);
                }
                finally
                {
                    XRRFreeOutputInfo(info);
                }
            }
            finally
            {
                XRRFreeScreenResources(resources);
            }
        }

        /// XRROutputInfo up to its size in millimetres.
        [StructLayout(LayoutKind.Sequential)]
        unsafe struct XRROutputInfo
        {
            public nuint Timestamp, Crtc;
            public byte* Name;
            public int NameLength;
            public nuint MillimetreWidth, MillimetreHeight;
        }

        [DllImport("libX11.so.6")]
        static extern nuint XDefaultRootWindow(nint display);

        [DllImport("libXrandr.so.2")]
        static extern nint XRRGetScreenResourcesCurrent(nint display, nuint window);

        [DllImport("libXrandr.so.2")]
        static extern unsafe XRROutputInfo* XRRGetOutputInfo(nint display, nint resources, nuint output);

        [DllImport("libXrandr.so.2")]
        static extern unsafe void XRRFreeOutputInfo(XRROutputInfo* info);

        [DllImport("libXrandr.so.2")]
        static extern void XRRFreeScreenResources(nint resources);
    }
}
