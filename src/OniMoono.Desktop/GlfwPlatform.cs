using System.Linq;
using System;
using System.Collections.Generic;
using Brutal.GlfwApi;
using Brutal.GlfwApi.Win32;
using Brutal.Numerics;
using OniMoono.Engine.Platform;
using UnityEngine;
using static Brutal.GlfwApi.Glfw.Constants;

namespace OniMoono.Desktop
{
    /// Window startup settings: Unity's player defaults from PlayerSettings, overridable with
    /// Unity's player arguments -screen-width, -screen-height, -screen-fullscreen, -monitor and
    /// -window-mode. Unity documents -window-mode exclusive|borderless, for Windows only; ours
    /// takes them everywhere (exclusive is a fullscreen window off Windows), and windowed too.
    internal sealed record WindowSettings(string Title, int Width, int Height, FullScreenMode Mode)
    {
        /// Development runs driven by scripted input ignore the window's own mouse and keyboard.
        public bool IgnoreUserInput { get; init; }
        /// Diagnostics (--gl-debug): a debug context, whose driver reports errors and
        /// performance problems through GL debug output.
        public bool DebugContext { get; init; }
        /// -monitor N: the monitor to open on, 1-based as in Unity (0: the primary one).
        public int Monitor { get; init; }
        /// The graphics API the window is made for: an OpenGL 4.5 core context (with a hidden
        /// window sharing it, for the GL thread), or none, for a Vulkan surface.
        public RenderApi Api { get; init; } = RenderApi.OpenGl;
        /// --no-focus: the window opens without taking the desktop's focus, and still takes
        /// input once someone focuses it (test runs on a desktop someone is using).
        public bool NoFocus { get; init; }
    }

    internal enum RenderApi { OpenGl, Vulkan }

    /// The player window (GLFW, with an OpenGL 4.5 core context or for a Vulkan surface) and
    /// its input devices.
    internal sealed unsafe class GlfwPlatform : IPlatform, IDisposable
    {
        readonly GlfwWindow window, renderWindow;
        readonly InputStateFeed feed = new();
        FullScreenMode mode;
        int2 windowedPosition, windowedSize;
        /// In exclusive fullscreen, the monitor's mode from before the window took it: GLFW's
        /// video mode is the current one (glfwGetVideoMode), the exclusive mode until it is left.
        GlfwVidMode? desktopBeforeExclusive;
        /// Whether windowedPosition is one the window had as a window, not a fullscreen start's.
        bool windowedPositionKnown;
        bool cursorVisible = true;
        CursorLockMode cursorLock;
        GlfwCursor customCursor;
        /// Windows: custom cursors that follow the pointer size (made on first use).
        Win32Cursor win32Cursor;
        bool closeRequested;
        /// Our character callback, kept alive for as long as GLFW may call it.
        GlfwCharUnmanagedCallback charCallback;
        /// What a callback of ours threw, thrown again once GLFW's poll returns (an exception
        /// must not unwind through GLFW's native frames).
        Exception callbackFailure;

        public GlfwWindow Window => window;
        /// The hidden window whose context the main thread renders with.
        public GlfwWindow RenderWindow => renderWindow;

        readonly bool scripted;

        const int GLFW_ANY_PLATFORM = 0x00060000;
        // GLFW 3.4's string hint, which the binding's GlfwWindowHint does not name.
        const GlfwWindowHint GLFW_WAYLAND_APP_ID = (GlfwWindowHint)0x00026001;
        // GLFW_POSITION_X/Y's "no position" (the system picks one).
        const int GLFW_ANY_POSITION = unchecked((int)0x80000000);
        /// The original Linux player's executable name, its X11 WM_CLASS.
        const string ApplicationId = "OxygenNotIncluded";

        /// The API the window was made for.
        public RenderApi Api { get; }

        static bool errorCallbackSet, initialized;

        /// Initializes GLFW unless it is (the Vulkan device asks it about Vulkan support before
        /// a window exists). Unity's Linux player is an X11 client (XWayland under Wayland
        /// compositors), where window positions exist; GLFW's X11 backend is preferred, else
        /// whatever GLFW finds.
        public static void Initialize()
        {
            if (initialized) return;
            // Once a process: a platform made again (OpenGL after Vulkan could not start) keeps it.
            if (!errorCallbackSet) Glfw.ErrorCallback += (code, description) => Debug.LogWarning($"GLFW {code}: {description}");
            errorCallbackSet = true;
            // Unity's Linux player is an X11 client (XWayland under Wayland compositors), where
            // window positions exist; prefer GLFW's X11 backend, else whatever GLFW finds. The
            // retry for when X11 is supported and DISPLAY set but its init fails is a guard only:
            // no such display has been seen.
            if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 }
                && Glfw.IsPlatformSupported(Brutal.GlfwApi.GlfwPlatform.X11))
            {
                Glfw.SetInitHint(GlfwInitHint.Platform, (int)Brutal.GlfwApi.GlfwPlatform.X11);
                initialized = Glfw.Init();
                if (!initialized) Glfw.SetInitHint(GlfwInitHint.Platform, GLFW_ANY_PLATFORM);
            }
            if (!initialized && !(initialized = Glfw.Init())) throw new InvalidOperationException("GLFW could not initialize (no display available).");
        }

        public GlfwPlatform(WindowSettings settings)
        {
            Api = settings.Api;
            Initialize();
            Glfw.DefaultWindowHints();
            if (Api == RenderApi.Vulkan) Glfw.WindowHint(GlfwWindowHint.ClientApi, GLFW_NO_API);
            else
            {
                Glfw.WindowHint(GlfwWindowHint.ClientApi, GLFW_OPENGL_API);
                Glfw.WindowHint(GlfwWindowHint.ContextVersionMajor, 4);
                Glfw.WindowHint(GlfwWindowHint.ContextVersionMinor, 5);
                Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, GLFW_OPENGL_CORE_PROFILE);
                Glfw.WindowHint(GlfwWindowHint.OpenGlForwardCompat, GLFW_TRUE);
                Glfw.WindowHint(GlfwWindowHint.OpenGlDebugContext, settings.DebugContext ? GLFW_TRUE : GLFW_FALSE);
                Glfw.WindowHint(GlfwWindowHint.DepthBits, 24);
                Glfw.WindowHint(GlfwWindowHint.StencilBits, 8);
                Glfw.WindowHint(GlfwWindowHint.Doublebuffer, GLFW_TRUE);
            }
            Glfw.WindowHint(GlfwWindowHint.Visible, GLFW_FALSE);
            // Scripted runs take no input from the desktop and do not take its focus either.
            Glfw.WindowHint(GlfwWindowHint.FocusOnShow, settings.IgnoreUserInput || settings.NoFocus ? GLFW_FALSE : GLFW_TRUE);
            // The window identifies itself as the original's does, so desktops group it with,
            // and take its icon from, the same launchers. Unity's Linux player names the X11
            // class and instance after its executable (WM_CLASS "OxygenNotIncluded",
            // "OxygenNotIncluded" as shipped); GLFW would use the title. Unity's Wayland app ID
            // is unmeasured here, so ours takes the X11 class, the name a .desktop entry's
            // StartupWMClass already matches.
            Glfw.WindowHint(GlfwWindowHint.X11ClassName, ApplicationId);
            Glfw.WindowHint(GlfwWindowHint.X11InstanceName, ApplicationId);
            Glfw.WindowHint(GLFW_WAYLAND_APP_ID, ApplicationId);

            var monitor = Glfw.PrimaryMonitor;
            var monitors = Glfw.GetMonitors();
            if (settings.Monitor >= 1 && settings.Monitor <= monitors.Length) monitor = monitors[settings.Monitor - 1];
            var desktop = monitor.VideoMode;
            mode = Supported(settings.Mode);
            int width = settings.Width > 0 ? settings.Width : desktop.Width;
            int height = settings.Height > 0 ? settings.Height : desktop.Height;
            if (mode is FullScreenMode.FullScreenWindow) Glfw.WindowHint(GlfwWindowHint.Decorated, GLFW_FALSE);
            // The window is created where it will be placed, so that the placement it restores to
            // after being maximized while hidden is on its monitor. GLFW maximizes a hidden
            // window by hand (maximizeWindowManually: WS_MAXIMIZE and a move to the work area),
            // not through ShowWindow. Observed on Windows 11 (build 26200) with a hidden test
            // window: after a SetWindowPos to the second monitor and that manual maximize,
            // GetWindowPlacement still gave the CW_USEDEFAULT rectangle it was created with
            // (unless the placement had been read after the move), and our window, created at
            // the default position, came back on the first monitor when restored (06e61962).
            // Created at its position it is fine: GLFW's createNativeWindow itself reads and
            // sets the new window's placement (Get/SetWindowPlacement) after creating it.
            if (mode != FullScreenMode.ExclusiveFullScreen)
            {
                var position = mode == FullScreenMode.FullScreenWindow
                    ? monitor.Position
                    : monitor.Position + (new int2(desktop.Width, desktop.Height) - new int2(width, height)) / 2;
                Glfw.WindowHint(GlfwWindowHint.PositionX, position.X);
                Glfw.WindowHint(GlfwWindowHint.PositionY, position.Y);
            }
            window = Glfw.CreateWindow(new GlfwWindow.CreateInfo
            {
                Title = settings.Title,
                Size = mode == FullScreenMode.FullScreenWindow ? new int2(desktop.Width, desktop.Height) : new int2(width, height),
                Monitor = mode == FullScreenMode.ExclusiveFullScreen ? monitor : GlfwMonitor.Null,
            });
            Glfw.WindowHint(GlfwWindowHint.PositionX, GLFW_ANY_POSITION);
            Glfw.WindowHint(GlfwWindowHint.PositionY, GLFW_ANY_POSITION);
            windowedSize = new int2(width, height);
            if (mode == FullScreenMode.FullScreenWindow) window.Position = monitor.Position;
            else if (mode != FullScreenMode.ExclusiveFullScreen) window.CenterToMonitor(monitor);
            windowedPosition = window.Position;
            windowedPositionKnown = mode is FullScreenMode.Windowed or FullScreenMode.MaximizedWindow;
            if (mode == FullScreenMode.ExclusiveFullScreen) desktopBeforeExclusive = desktop;
            if (settings.Monitor > 0)
                OniMoono.Engine.EngineLog.Engine($"window: monitor {settings.Monitor} of {monitors.Length} at {monitor.Position.X},{monitor.Position.Y}; window at {window.Position.X},{window.Position.Y}");
            SetIcon(window);
            if (mode == FullScreenMode.MaximizedWindow
                || mode == FullScreenMode.Windowed && TooLargeForWorkArea(monitor, new int2(width, height)))
                window.Maximize();
            // OpenGL: rendering happens on this thread in a hidden window's context shared with
            // the visible one, whose context belongs to the presenter thread (GlPresenter).
            if (Api == RenderApi.OpenGl)
            {
                renderWindow = Glfw.CreateWindow(new GlfwWindow.CreateInfo
                {
                    Title = settings.Title,
                    Size = new int2(1, 1),
                    Monitor = GlfwMonitor.Null,
                    Share = window,
                });
                renderWindow.MakeContextCurrent();
            }
            // Showing it focuses it unless FocusOnShow is off (glfwShowWindow). An exclusive
            // fullscreen window is already shown and focused (_glfwCreateWindowWin32).
            window.Visible = true;

            // Scripted runs (host --input) take input only from the script.
            scripted = settings.IgnoreUserInput;
            if (scripted) InputState.Instance.MousePresent = true;
            else
            {
                // The modifiers held are read as each event comes in: one reported later (pumped
                // meanwhile) would otherwise take those of the last event GLFW has seen.
                window.OnKey += (w, key, scancode, action, mods) =>
                {
                    var held = Modifiers();
                    Report(() => OnKey(key, scancode, action, mods, held));
                };
                // BRUTAL's own character thunk cuts the code point to 16 bits ((ushort)point), so
                // characters above U+FFFF (emoji) arrived as others, or as a lone surrogate:
                // ours replaces it, with the whole code point. BRUTAL's other callbacks stay.
                charCallback = OnCharNative;
                Glfw.PInvoke.SetCharCallback(window, charCallback);
                window.OnMouseButton += (w, button, action, mods) =>
                {
                    var held = Modifiers();
                    Report(() => OnMouseButton(button, action, held));
                };
                window.OnCursorPos += (_, pos) => Report(() => feed.MouseMoved(ToPixels(pos)));
                window.OnScroll += (_, offset) => Report(() => feed.Scrolled(new Vector2((float)offset.X, (float)offset.Y), MouseGuiPosition()));
                window.OnCursorEnter += (_, entered) => Report(() => InputState.Instance.MousePresent = entered);
            }
            window.OnClose += _ => Report(() => closeRequested = true);
            window.OnFramebufferSize += (_, size) => Report(ReadFramebufferSize);
            Glfw.MonitorCallback += (_, _) => Report(RefreshWindowState);
            // The window may have moved to another monitor.
            window.OnPosition += (_, _) => Report(RefreshWindowState);
            // The system may have discarded what the window showed: GLFW's refresh callback says
            // so (exposure). Told at once, during a pump too: the presenter shows the last frame
            // again, which changes nothing the frame sees. Focus changes are told too, in case a
            // compositor changes how it presents a fullscreen window as it gains or loses the
            // focus without an expose; that is unconfirmed (never observed), but a present
            // again costs little.
            window.OnRefresh += _ => ContentsDamaged?.Invoke();
            window.OnFocus += (_, _) => ContentsDamaged?.Invoke();
            if (Gl.PresentTrace.Enabled)
            {
                window.OnFocus += (_, e) => Trace($"focus {e}");
                window.OnIconify += (_, e) => Trace($"iconify {e}");
                window.OnMaximize += (_, e) => Trace($"maximize {e}");
                window.OnSize += (_, e) => Trace($"window size {e}");
                window.OnFramebufferSize += (_, e) => Trace($"framebuffer size {e}");
                window.OnPosition += (_, e) => Trace($"position {e}");
                window.OnRefresh += _ => Trace("refresh (contents damaged)");
            }
            RefreshWindowState();
            InputState.Instance.MousePresent = true;
            // Unity's player starts with the pointer where it is (the game's hover card shows for
            // what is under it before the mouse first moves); GLFW reports only movements. Asked
            // of GLFW: the window's CursorPos is what BRUTAL read as it created the window, before
            // it was moved, maximized and shown.
            if (!scripted)
            {
                Glfw.PInvoke.GetCursorPos(window, out double x, out double y);
                feed.MouseMoved(ToPixels(new double2(x, y)));
            }
        }

        /// The window's contents may need presenting again (main thread, during PollEvents).
        public event Action ContentsDamaged;

        static void Trace(string text) => Gl.PresentTrace.Log($"frame {Time.frameCount}: window {text}");

        // ----- IPlatform -----

        public bool HasDisplay => true;
        // Window and monitor state is cached: Screen/Display properties are read many times per
        // frame and each GLFW query is a round trip to the display server.
        int2 framebufferSize;
        OniMoono.Engine.Platform.DisplayMode? desktopMode;
        float? dpi;

        void RefreshWindowState()
        {
            ReadFramebufferSize();
            desktopMode = null;
            dpi = null;
            // Where the window is after a move, for when it is on no monitor (CurrentMonitor).
            if (window.Monitor.IsNull() && MonitorHolding(Glfw.GetMonitors()) is { } holding) lastMonitor = holding;
        }

        void ReadFramebufferSize() => framebufferSize = KeptFramebufferSize(window.FramebufferSize, framebufferSize);

        /// A minimized window's framebuffer is 0x0 (Windows), but Unity's player keeps the size
        /// it had: measured in the original, a minimized 1280x720 window (and a minimized
        /// 2560x1440 fullscreen one) keeps Screen.width/height, Display.renderingWidth/Height and
        /// the cameras' pixel sizes, and frames go on, until it is restored at the same size.
        internal static int2 KeptFramebufferSize(int2 reported, int2 last)
            => reported.X > 0 && reported.Y > 0 || last.X <= 0 || last.Y <= 0 ? reported : last;

        public int FramebufferWidth => framebufferSize.X;
        public int FramebufferHeight => framebufferSize.Y;
        public FullScreenMode FullScreenMode => mode;
        /// Scripted runs behave as a focused window with the mouse over it, whatever the desktop
        /// is doing.
        public bool Focused => scripted || window.IsFocused;

        /// Monitor DPI from its physical size (0 when the monitor does not report one, as Unity).
        public float Dpi => dpi ??= MonitorDpi(CurrentMonitor);

        /// The window and taskbar icon (the falling Gassy Moo, embedded). Wayland has no window icons: the
        /// compositor takes them from the desktop entry, and GLFW reports an error if asked.
        static unsafe void SetIcon(GlfwWindow window)
        {
            if (Glfw.GetPlatform() == Brutal.GlfwApi.GlfwPlatform.Wayland) return;
            using var stream = typeof(GlfwPlatform).Assembly.GetManifestResourceStream("Moo.png");
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            using var image = Brutal.StbApi.Stb.LoadFromMemory(data, 4);
            fixed (byte* pixels = image.AsSpan())
            {
                var icon = new GlfwImage { Width = image.Width, Height = image.Height, Pixels = pixels };
                Glfw.PInvoke.SetWindowIcon(window, 1, &icon);
            }
            if (OperatingSystem.IsWindows()) SetClassIcon(window);
        }

        /// The taskbar asks the window for its icon, and while the main thread is busy loading the
        /// game (no message pump) it gives up and takes the window class's, which GLFW leaves as
        /// Windows' generic one. The class gets the exe's icon (resource 32512, the .NET SDK's
        /// ApplicationIcon), at the sizes the shell wants.
        static void SetClassIcon(GlfwWindow window)
        {
            const int GCLP_HICON = -14, GCLP_HICONSM = -34, SM_CXICON = 11, SM_CYICON = 12, SM_CXSMICON = 49, SM_CYSMICON = 50;
            var module = GetModuleHandleW(IntPtr.Zero);
            var big = LoadImageW(module, 32512, 1, GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON), 0);
            var small = LoadImageW(module, 32512, 1, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), 0);
            if (big == IntPtr.Zero || small == IntPtr.Zero) return; // a host without the icon (tests)
            var hwnd = (IntPtr)window.GetWin32Window();
            SetClassLongPtrW(hwnd, GCLP_HICON, big);
            SetClassLongPtrW(hwnd, GCLP_HICONSM, small);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr GetModuleHandleW(IntPtr name);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr LoadImageW(IntPtr module, nint name, uint type, int width, int height, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SetClassLongPtrW(IntPtr hwnd, int index, IntPtr value);

        /// Whether the window's IME is on (null: not set yet; Windows' default is on).
        bool? imeEnabled;

        /// Windows: the window takes IME input only when Input.imeCompositionMode asks for it
        /// (ImeInput), as Unity's player: without its input context an IME such as Microsoft
        /// Pinyin leaves the keys to the game (issue #30). The same calls as Unity's: the context
        /// taken away, and the window's default one given back.
        void ApplyIme()
        {
            bool wanted = OniMoono.Engine.Platform.ImeInput.WantedNow;
            if (imeEnabled == wanted) return;
            imeEnabled = wanted;
            var hwnd = (IntPtr)window.GetWin32Window();
            const uint IACE_DEFAULT = 0x10;
            if (wanted) ImmAssociateContextEx(hwnd, IntPtr.Zero, IACE_DEFAULT);
            else ImmAssociateContext(hwnd, IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("imm32.dll")]
        static extern IntPtr ImmAssociateContext(IntPtr hwnd, IntPtr himc);

        [System.Runtime.InteropServices.DllImport("imm32.dll")]
        static extern bool ImmAssociateContextEx(IntPtr hwnd, IntPtr himc, uint flags);

        static float MonitorDpi(GlfwMonitor m)
        {
            var mm = m.PhysicalSize;
            return mm.X <= 0 ? 0f : m.Size.X / (mm.X / 25.4f);
        }

        GlfwMonitor CurrentMonitor
        {
            get
            {
                var m = window.Monitor;
                if (!m.IsNull()) return m;
                // A window (a fullscreen window too, which is a monitor-sized window): the
                // monitor holding its centre, so a mode change keeps it on that monitor rather
                // than moving it to the primary one.
                var monitors = Glfw.GetMonitors();
                if (MonitorHolding(monitors) is { } holding) return lastMonitor = holding;
                // On none (minimized on Windows, the window is at -32000,-32000): the last one it
                // was seen on, while that is still connected.
                foreach (var monitor in monitors)
                    if (monitor.NativeHandle == lastMonitor.NativeHandle) return monitor;
                return Glfw.PrimaryMonitor;
            }
        }

        /// The monitor CurrentMonitor last found the window's centre on.
        GlfwMonitor lastMonitor;

        GlfwMonitor? MonitorHolding(ReadOnlySpan<GlfwMonitor> monitors)
        {
            var position = window.Position;
            var size = window.Size;
            int cx = position.X + size.X / 2, cy = position.Y + size.Y / 2;
            foreach (var monitor in monitors)
            {
                var p = monitor.Position;
                var mode = monitor.VideoMode;
                if (cx >= p.X && cy >= p.Y && cx < p.X + mode.Width && cy < p.Y + mode.Height) return monitor;
            }
            return null;
        }

        static OniMoono.Engine.Platform.DisplayMode ModeOf(GlfwVidMode m) => new(m.Width, m.Height, (uint)m.RefreshRate, 1);

        public OniMoono.Engine.Platform.DisplayMode DesktopMode => desktopMode ??= ModeOf(CurrentMonitor.VideoMode);

        public OniMoono.Engine.Platform.DisplayMode[] DisplayModes
        {
            get
            {
                var list = new List<OniMoono.Engine.Platform.DisplayMode>();
                foreach (var m in CurrentMonitor.VideoModes)
                {
                    var mode = ModeOf(m);
                    if (!list.Contains(mode)) list.Add(mode);
                }
                if (list.Count > 1)
                    return list.OrderBy(m => m.Width).ThenBy(m => m.Height).ThenBy(m => (double)m.RefreshNumerator / Math.Max(1u, m.RefreshDenominator)).ToArray();
                // A display that reports only its current mode (virtual displays such as Xvfb or
                // WSLg): Unity's Linux player then offers its table of standard resolutions.
                var standard = StandardModes(DesktopMode);
                return standard.Length > 0 ? standard : list.ToArray();
            }
        }

        /// Unity's standard resolutions that fit the desktop, at 60 Hz, in its order (measured:
        /// Screen.resolutions in the original player on a 1280x1024 Xvfb screen, PerfProbe
        /// measure.txt).
        internal static OniMoono.Engine.Platform.DisplayMode[] StandardModes(OniMoono.Engine.Platform.DisplayMode desktop)
        {
            var standard = new List<OniMoono.Engine.Platform.DisplayMode>();
            foreach (var (w, h) in StandardResolutions)
                if (w <= desktop.Width && h <= desktop.Height) standard.Add(new(w, h, 60, 1));
            return standard.ToArray();
        }

        static readonly (int Width, int Height)[] StandardResolutions =
        [
            (640, 480), (720, 480), (768, 480), (800, 480), (854, 480), (960, 540), (1024, 576), (800, 600),
            (1024, 600), (960, 640), (1024, 640), (1136, 640),
            (960, 720), (1152, 720), (1280, 720), (1024, 768), (1152, 768), (1280, 768), (1366, 768), (1280, 800),
            (1152, 864), (1280, 864), (1440, 900), (1600, 900), (1280, 960), (1440, 960), (1280, 1024), (1600, 1024),
            (1400, 1050), (1680, 1050), (1920, 1080), (2560, 1080), (2048, 1152), (1600, 1200), (1920, 1200),
            (1792, 1344), (1856, 1392), (1920, 1440), (2160, 1440), (2304, 1440), (2560, 1440), (3440, 1440),
            (2048, 1536), (2560, 1600), (2880, 1800), (3200, 1800), (2560, 1920), (2560, 2048), (3200, 2048), (3840, 2160),
        ];

        /// Unity supports exclusive fullscreen only in its Windows player; elsewhere a request for
        /// it gives a fullscreen window (as the Linux player reports: FullScreenWindow). Maximized
        /// windows are Windows' and macOS's only, and "on other platforms, this mode falls back
        /// to FullScreenWindow" (Unity's FullScreenMode.MaximizedWindow reference, 6000.3).
        static FullScreenMode Supported(FullScreenMode mode) => mode switch
        {
            FullScreenMode.ExclusiveFullScreen when !OperatingSystem.IsWindows() => FullScreenMode.FullScreenWindow,
            FullScreenMode.MaximizedWindow when !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() => FullScreenMode.FullScreenWindow,
            _ => mode,
        };

        /// Windows keeps a window within the monitor's size with its frame: a windowed request as
        /// tall as the monitor (the game's windowed mode at the native resolution) came out shorter
        /// than asked, its title bar above the screen and the desktop showing below it (measured:
        /// 3840x2160 at 150% scaling gave a 3840x2130 client area, the window at y -45). Such a
        /// window is maximized instead, which fills the work area with its title bar shown. The
        /// side borders don't count: Windows 10 and 11 draw them invisible, so a window as wide as
        /// the monitor shows whole.
        bool TooLargeForWorkArea(GlfwMonitor monitor, int2 size)
        {
            if (!OperatingSystem.IsWindows()) return false;
            window.GetWindowFrameSize(out _, out int top, out _, out int bottom);
            monitor.GetMonitorWorkArea(out _, out var workArea);
            return !FitsWorkArea(size, top + bottom, workArea);
        }

        internal static bool FitsWorkArea(int2 size, int frameHeight, int2 workArea)
            => size.X <= workArea.X && size.Y + frameHeight <= workArea.Y;

        /// Where a window's client area goes for its frame (as GLFW reports it, Windows' invisible
        /// resize borders included) to be centred on the monitor.
        internal static int2 CentredPosition(int2 monitorPosition, int2 monitorSize, int2 size, int left, int top, int right, int bottom)
            => new(monitorPosition.X + (monitorSize.X - (size.X + left + right)) / 2 + left,
                monitorPosition.Y + (monitorSize.Y - (size.Y + top + bottom)) / 2 + top);

        public void SetResolution(int width, int height, FullScreenMode newMode, uint refreshNumerator, uint refreshDenominator)
        {
            newMode = Supported(newMode);
            if (Gl.PresentTrace.Enabled) Trace($"SetResolution {width}x{height} {newMode} (was {mode}, framebuffer {framebufferSize.X}x{framebufferSize.Y})");
            var monitor = CurrentMonitor;
            // The monitor's desktop mode: in exclusive fullscreen, the one from before it.
            var desktop = mode == FullScreenMode.ExclusiveFullScreen && desktopBeforeExclusive is { } before ? before : monitor.VideoMode;
            if (mode is FullScreenMode.Windowed or FullScreenMode.MaximizedWindow)
            {
                windowedPosition = window.Position;
                windowedSize = window.Size;
                windowedPositionKnown = true;
            }
            switch (newMode)
            {
                case FullScreenMode.ExclusiveFullScreen:
                {
                    if (window.IsMaximised) window.Restore();
                    int refresh = refreshDenominator == 0 || refreshNumerator == 0 ? desktop.RefreshRate : (int)Math.Round((double)refreshNumerator / refreshDenominator);
                    desktopBeforeExclusive = desktop;
                    window.SetFullscreen(monitor, new int2(width, height), refresh);
                    break;
                }
                case FullScreenMode.FullScreenWindow:
                {
                    // A maximized window keeps its maximized placement under the new size.
                    if (window.IsMaximised) window.Restore();
                    window.SetAttribute(GlfwWindowAttribute.Decorated, GLFW_FALSE);
                    window.SetWindowed(monitor.Position, new int2(desktop.Width, desktop.Height));
                    break;
                }
                default:
                {
                    window.SetAttribute(GlfwWindowAttribute.Decorated, GLFW_TRUE);
                    var size = new int2(width, height);
                    // Out of exclusive fullscreen first: GLFW gives a window its frame only once it
                    // is no monitor's (glfwSetWindowAttrib stores DECORATED until then, and
                    // win32's frame size is a popup's, none), and the work area is the exclusive
                    // mode's until the desktop mode is back.
                    if (mode == FullScreenMode.ExclusiveFullScreen) window.SetWindowed(windowedPosition, size);
                    bool maximize = newMode == FullScreenMode.MaximizedWindow || TooLargeForWorkArea(monitor, size);
                    bool restored = window.IsMaximised && !maximize;
                    if (restored) window.Restore();
                    bool fromFullscreen = mode is FullScreenMode.ExclusiveFullScreen or FullScreenMode.FullScreenWindow;
                    if (fromFullscreen && windowedPositionKnown)
                        window.SetWindowed(windowedPosition, size);
                    else if (fromFullscreen || restored)
                    {
                        // A maximized window made smaller is centred on the monitor it was
                        // maximized on, as Unity does (measured: the original maximized on a
                        // 2560x1440 second monitor at 2560,0, then 1024x768 windowed, gave a
                        // 1024x768 client at 3328,347: its frame centred on the whole monitor).
                        // So is a window that started fullscreen and never had a windowed
                        // position: placed at the monitor's corner, its title bar was off it.
                        window.GetWindowFrameSize(out int left, out int top, out int right, out int bottom);
                        window.SetWindowed(CentredPosition(monitor.Position, new int2(desktop.Width, desktop.Height), size, left, top, right, bottom), size);
                    }
                    // A window about to be maximized keeps its size: what it has when maximized
                    // is the size it restores to.
                    else if (!window.IsMaximised && !maximize) window.Size = size;
                    if (maximize) window.Maximize();
                    break;
                }
            }
            if (newMode != FullScreenMode.ExclusiveFullScreen) desktopBeforeExclusive = null;
            mode = newMode;
            window.Refresh();
            RefreshWindowState();
        }

        /// Inside PumpEvents: what the callbacks report waits for the next PollEvents.
        bool pumping;
        readonly List<Action> pumped = new();

        // Inside a GLFW callback, or the frame's own poll: GLFW must not be polled from a
        // callback, and what a callback runs may log, which pumps (MainThreadPump).
        int inGlfw;

        void Report(Action report)
        {
            if (pumping) pumped.Add(report);
            else
            {
                inGlfw++;
                try { report(); }
                finally { inGlfw--; }
            }
        }

        /// GLFW's character callback: the Unicode code point typed (GLFWcharfun).
        void OnCharNative(GlfwWindowHandle* handle, uint codepoint)
        {
            try
            {
                var held = Modifiers();
                Report(() => feed.Text(codepoint, held));
            }
            catch (Exception e)
            {
                callbackFailure ??= e;
            }
        }

        void ThrowCallbackFailure()
        {
            if (callbackFailure is not { } failure) return;
            callbackFailure = null;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }

        public void PumpEvents()
        {
            if (pumping || inGlfw > 0) return;
            pumping = true;
            try
            {
                Glfw.PollEvents();
            }
            finally
            {
                pumping = false;
            }
            ThrowCallbackFailure();
        }

        public bool PollEvents(InputState input)
        {
            // The IME mode the last frame left goes on before the messages since are read, so
            // their keys go through the input context it asks for.
            if (OperatingSystem.IsWindows()) ApplyIme();
            // What came in while the last frame pumped messages, in the order it came, then the
            // messages since.
            inGlfw++;
            try
            {
                if (pumped.Count > 0)
                {
                    var reports = pumped.ToArray();
                    pumped.Clear();
                    foreach (var report in reports) report();
                }
                Glfw.PollEvents();
            }
            finally
            {
                inGlfw--;
            }
            ThrowCallbackFailure();
            ReadFramebufferSize();
            if (!scripted) FollowCursorOutside();
            // Each close of the window is one quit request: Application.wantsToQuit may refuse it.
            bool open = !closeRequested;
            closeRequested = false;
            return open;
        }

        /// Unity's Input.mousePosition follows the pointer outside the window too, relative to the
        /// window (measured in the original player: (1400, -133) for a pointer beside a 1024x768
        /// window at the origin, then following it as it moves); GLFW reports movements only over
        /// the window, so outside it the position is read each frame. No GUI events come from it.
        void FollowCursorOutside()
        {
            // The window's CursorPos is the last position its callbacks reported: asked of GLFW.
            Glfw.PInvoke.GetCursorPos(window, out double x, out double y);
            var size = window.Size;
            if (x >= 0 && y >= 0 && x < size.X && y < size.Y) return;
            var pixels = ToPixels(new double2(x, y));
            if (pixels != (Vector2)InputState.Instance.MousePosition) InputState.Instance.MouseMoved(pixels);
        }

        public bool CursorVisible
        {
            get => cursorVisible;
            set
            {
                cursorVisible = value;
                ApplyCursorMode();
            }
        }

        public CursorLockMode CursorLockState
        {
            get => cursorLock;
            set
            {
                cursorLock = value;
                ApplyCursorMode();
            }
        }

        /// Locked: hidden and kept centred (GLFW disabled mode); Confined: kept inside the window.
        void ApplyCursorMode()
        {
            var glfwMode = cursorLock switch
            {
                CursorLockMode.Locked => GlfwCursorMode.Disabled,
                CursorLockMode.Confined => cursorVisible ? GlfwCursorMode.Captured : GlfwCursorMode.Hidden,
                _ => cursorVisible ? GlfwCursorMode.Normal : GlfwCursorMode.Hidden,
            };
            window.SetInputMode(GlfwInputMode.Cursor, (int)glfwMode);
            if (win32Cursor != null) win32Cursor.Shown = glfwMode is GlfwCursorMode.Normal or GlfwCursorMode.Captured;
        }

        /// Texture pixels run bottom-up; cursor images top-down.
        public void SetCursorImage(Color32[] pixels, int width, int height, Vector2 hotspot)
        {
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var c = pixels[(height - 1 - y) * width + x];
                    int o = (y * width + x) * 4;
                    rgba[o] = c.r;
                    rgba[o + 1] = c.g;
                    rgba[o + 2] = c.b;
                    rgba[o + 3] = c.a;
                }
            if (OperatingSystem.IsWindows())
            {
                win32Cursor ??= new Win32Cursor((IntPtr)window.GetWin32Window())
                {
                    Shown = cursorVisible && cursorLock != CursorLockMode.Locked,
                };
                if (!customCursor.IsNull())
                {
                    window.SetCursor(default);
                    customCursor.Dispose();
                    customCursor = default;
                }
                if (win32Cursor.Set(rgba, width, height, (int)hotspot.x, (int)hotspot.y)) return;
                win32Cursor.Clear(); // not loaded: GLFW's, unscaled
            }
            fixed (byte* p = rgba)
            {
                var cursor = Glfw.CreateCursor(new GlfwCursor.CreateInfo
                {
                    Image = new GlfwImage { Width = width, Height = height, Pixels = p },
                    Hot = new int2((int)hotspot.x, (int)hotspot.y),
                });
                window.SetCursor(cursor);
                if (!customCursor.IsNull()) customCursor.Dispose();
                customCursor = cursor;
            }
        }

        public void SetDefaultCursor()
        {
            win32Cursor?.Clear();
            window.SetCursor(default);
            if (!customCursor.IsNull()) customCursor.Dispose();
            customCursor = default;
        }

        public string Clipboard
        {
            get => Glfw.Clipboard ?? "";
            set => Glfw.SetClipboard(value ?? "");
        }

        /// Runs first when the platform is disposed, while the windows still exist.
        public event Action Closing;

        public void Dispose()
        {
            Closing?.Invoke();
            win32Cursor?.Dispose();
            if (!customCursor.IsNull()) customCursor.Dispose();
            if (Api == RenderApi.OpenGl) renderWindow.Dispose();
            window.Dispose();
            Glfw.Terminate();
            initialized = false;
        }

        // ----- Input -----

        /// GLFW reports the cursor in window coordinates from the top left; Unity in framebuffer
        /// pixels from the bottom left.
        Vector2 ToPixels(double2 pos)
        {
            var size = window.Size;
            var fb = KeptFramebufferSize(window.FramebufferSize, framebufferSize);
            float sx = size.X == 0 ? 1f : (float)fb.X / size.X, sy = size.Y == 0 ? 1f : (float)fb.Y / size.Y;
            return PointerPixels.Screen(new Vector2((float)pos.X * sx, (float)pos.Y * sy), fb.Y);
        }

        Vector2 MouseGuiPosition()
        {
            return PointerPixels.Gui(InputState.Instance.MousePosition, FramebufferHeight);
        }

        /// The modifier keys held as GLFW has them now: read in the callback, the state just
        /// after the event (a Shift press holds Shift). GLFW's own `mods` would differ on X11,
        /// whose events carry the state from before them (translateState of the event's state).
        EventModifiers Modifiers()
        {
            EventModifiers m = 0;
            bool Down(GlfwKey k) => window.GetKey(k) == GlfwKeyAction.Press;
            if (Down(GlfwKey.LeftShift) || Down(GlfwKey.RightShift)) m |= EventModifiers.Shift;
            if (Down(GlfwKey.LeftControl) || Down(GlfwKey.RightControl)) m |= EventModifiers.Control;
            if (Down(GlfwKey.LeftAlt) || Down(GlfwKey.RightAlt)) m |= EventModifiers.Alt;
            if (Down(GlfwKey.LeftSuper) || Down(GlfwKey.RightSuper)) m |= EventModifiers.Command;
            return m;
        }

        /// What each held key's press reported, by scancode: its release reports the same, even
        /// when the layout changed in between.
        readonly Dictionary<int, KeyCode> pressed = new();

        void OnKey(GlfwKey key, int scancode, GlfwKeyAction action, GlfwModifier mods, EventModifiers held)
        {
            KeyCode code;
            if (action == GlfwKeyAction.Release && pressed.Remove(scancode, out var down)) code = down;
            else
            {
                code = KeyMap.ToUnity(key);
                // Printable keys follow the layout unless the build asks for positions (KeyboardLayout).
                if (KeyboardLayout.IsPrintable(key) && !OniMoono.Engine.Assets.ProjectSettings.UsePhysicalKeys
                    && KeyboardLayout.Current(key, scancode) is { } layoutCode)
                    code = layoutCode;
                if (action == GlfwKeyAction.Press) pressed[scancode] = code;
            }
            if (code == KeyCode.None) return;
            var modifiers = held;
            if ((mods & GlfwModifier.CapsLock) != 0) modifiers |= EventModifiers.CapsLock;
            if (KeyMap.IsKeypad(code)) modifiers |= EventModifiers.Numeric;
            if (KeyMap.IsFunction(code)) modifiers |= EventModifiers.FunctionKey;
            switch (action)
            {
                case GlfwKeyAction.Press:
                    feed.KeyDown(code, modifiers, repeat: false, MouseGuiPosition());
                    break;
                case GlfwKeyAction.Repeat:
                    feed.KeyDown(code, modifiers, repeat: true, MouseGuiPosition());
                    break;
                case GlfwKeyAction.Release:
                    feed.KeyUp(code, modifiers, MouseGuiPosition());
                    break;
            }
        }

        void OnMouseButton(GlfwMouseButton button, GlfwButtonAction action, EventModifiers held)
        {
            int index = (int)button;
            if (index > 6) return;
            if (action == GlfwButtonAction.Press) feed.MouseDown(index, held, MouseGuiPosition());
            else feed.MouseUp(index, held, MouseGuiPosition());
        }
    }

    /// Turns platform input into Unity's input state and IMGUI event stream. Unity sends a key
    /// press as a KeyDown event with the key code, and the character it types as a second
    /// KeyDown event with KeyCode.None (Return and Tab type '\n' and '\t').
    internal sealed class InputStateFeed
    {
        static InputState State => InputState.Instance;
        int clickCount;
        float lastClickTime = -1f;
        int lastClickButton = -1;
        Vector2 dragStart;
        readonly bool[] buttonsDown = new bool[7];

        static void Queue(EventType type, EventModifiers modifiers, Vector2 guiPosition, Action<Event> fill = null)
        {
            var e = new Event { type = type, modifiers = modifiers, mousePosition = guiPosition };
            fill?.Invoke(e);
            Event.Enqueue(e);
        }

        public void KeyDown(KeyCode code, EventModifiers modifiers, bool repeat, Vector2 guiPosition)
        {
            if (!repeat) State.KeyPressed(code);
            Queue(EventType.KeyDown, modifiers, guiPosition, e => e.keyCode = code);
            char typed = code switch
            {
                KeyCode.Return or KeyCode.KeypadEnter => '\n',
                KeyCode.Tab => '\t',
                _ => '\0',
            };
            if (typed != '\0') Queue(EventType.KeyDown, modifiers, guiPosition, e => e.character = typed);
        }

        public void KeyUp(KeyCode code, EventModifiers modifiers, Vector2 guiPosition)
        {
            State.KeyReleased(code);
            Queue(EventType.KeyUp, modifiers, guiPosition, e => e.keyCode = code);
        }

        /// Typed text: one character event per UTF-16 unit.
        public void Text(uint codepoint, EventModifiers modifiers)
        {
            foreach (var ch in TextOf(codepoint))
                Queue(EventType.KeyDown, modifiers, Vector2.zero, e => e.character = ch);
        }

        /// A typed code point's UTF-16 units; none for a value that is no character's (a
        /// surrogate, or above U+10FFFF), which no system should send but which must not stop
        /// the frame's input.
        internal static string TextOf(uint codepoint)
            => System.Text.Rune.IsValid(codepoint) ? new System.Text.Rune(codepoint).ToString() : "";

        public void MouseMoved(Vector2 pixels)
        {
            var previous = State.MousePosition;
            State.MouseMoved(pixels);
            var gui = PointerPixels.Gui(pixels, OniMoono.Engine.Platform.PlatformServices.Current.FramebufferHeight);
            var delta = new Vector2(pixels.x - previous.x, -(pixels.y - previous.y));
            int dragging = Array.IndexOf(buttonsDown, true);
            Queue(dragging >= 0 ? EventType.MouseDrag : EventType.MouseMove, 0, gui, e =>
            {
                e.delta = delta;
                e.button = Math.Max(0, dragging);
            });
        }

        public void Scrolled(Vector2 delta, Vector2 guiPosition)
        {
            State.Scrolled(delta);
            var lines = WheelEventDelta(delta, OperatingSystem.IsWindows());
            Queue(EventType.ScrollWheel, 0, guiPosition, e => e.delta = lines);
        }

        /// An IMGUI ScrollWheel event's delta for a wheel scroll of `notches` (up positive, as
        /// Input.mouseScrollDelta has it): it points down for wheel-down, one per notch on Linux
        /// and three on Windows, as Unity's players report it (measured with real wheel input).
        internal static Vector2 WheelEventDelta(Vector2 notches, bool windows)
            => new Vector2(-notches.x, -notches.y) * (windows ? 3f : 1f);

        public void MouseDown(int button, EventModifiers modifiers, Vector2 guiPosition)
        {
            buttonsDown[button] = true;
            State.KeyPressed(KeyCode.Mouse0 + button);
            float now = Time.realtimeSinceStartup;
            if (button == lastClickButton && IsDoubleClick(now - lastClickTime, guiPosition - dragStart)) clickCount++;
            else clickCount = 1;
            lastClickTime = now;
            lastClickButton = button;
            dragStart = guiPosition;
            int clicks = clickCount;
            Queue(EventType.MouseDown, modifiers, guiPosition, e =>
            {
                e.button = button;
                e.clickCount = clicks;
            });
        }

        /// A click of the same button `seconds` after the last and `offset` pixels from it counts
        /// on: on Windows within the system's double-click time and rectangle, which is centred
        /// on the first click (GetDoubleClickTime; GetSystemMetrics SM_CXDOUBLECLK and
        /// SM_CYDOUBLECLK), read at each click as the user may change them. Elsewhere 0.5 s and
        /// 5 pixels, values of ours, not measured in Unity's Linux player: X11 itself has no such
        /// setting, and desktops' own (XSETTINGS Net/DoubleClickTime) are not read.
        static bool IsDoubleClick(float seconds, Vector2 offset)
        {
            if (!OperatingSystem.IsWindows()) return seconds < 0.5f && offset.sqrMagnitude < 25f;
            const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37;
            return IsWithin(seconds, offset, GetDoubleClickTime() / 1000f, GetSystemMetrics(SM_CXDOUBLECLK), GetSystemMetrics(SM_CYDOUBLECLK));
        }

        /// Windows' rule (Microsoft Learn, About Mouse Input, "Double-Click Messages"): the second
        /// click's hot spot still within the rectangle centred on the first's, and "the elapsed
        /// time does not exceed the double-click time-out value".
        internal static bool IsWithin(float seconds, Vector2 offset, float time, int width, int height)
            => seconds <= time && Math.Abs(offset.x) <= width / 2 && Math.Abs(offset.y) <= height / 2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetDoubleClickTime();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        public void MouseUp(int button, EventModifiers modifiers, Vector2 guiPosition)
        {
            buttonsDown[button] = false;
            State.KeyReleased(KeyCode.Mouse0 + button);
            int clicks = clickCount;
            Queue(EventType.MouseUp, modifiers, guiPosition, e =>
            {
                e.button = button;
                e.clickCount = clicks;
            });
        }
    }

    internal static class KeyMap
    {
        public static bool IsKeypad(KeyCode k) => k is >= KeyCode.Keypad0 and <= KeyCode.KeypadEquals;
        // Unity flags Backspace as a function key too (its text editing key bindings expect it).
        public static bool IsFunction(KeyCode k) => k is >= KeyCode.UpArrow and <= KeyCode.F15 or KeyCode.Delete or KeyCode.Insert or KeyCode.Backspace;

        public static KeyCode ToUnity(GlfwKey key) => key switch
        {
            GlfwKey.Space => KeyCode.Space,
            GlfwKey.Apostrophe => KeyCode.Quote,
            GlfwKey.Comma => KeyCode.Comma,
            GlfwKey.Minus => KeyCode.Minus,
            GlfwKey.Period => KeyCode.Period,
            GlfwKey.Slash => KeyCode.Slash,
            >= GlfwKey.Number0 and <= GlfwKey.Number9 => KeyCode.Alpha0 + (key - GlfwKey.Number0),
            GlfwKey.Semicolon => KeyCode.Semicolon,
            GlfwKey.Equal => KeyCode.Equals,
            >= GlfwKey.A and <= GlfwKey.Z => KeyCode.A + (key - GlfwKey.A),
            GlfwKey.LeftBracket => KeyCode.LeftBracket,
            GlfwKey.Backslash => KeyCode.Backslash,
            GlfwKey.RightBracket => KeyCode.RightBracket,
            GlfwKey.GraveAccent => KeyCode.BackQuote,
            GlfwKey.Escape => KeyCode.Escape,
            GlfwKey.Enter => KeyCode.Return,
            GlfwKey.Tab => KeyCode.Tab,
            GlfwKey.Backspace => KeyCode.Backspace,
            GlfwKey.Insert => KeyCode.Insert,
            GlfwKey.Delete => KeyCode.Delete,
            GlfwKey.Right => KeyCode.RightArrow,
            GlfwKey.Left => KeyCode.LeftArrow,
            GlfwKey.Down => KeyCode.DownArrow,
            GlfwKey.Up => KeyCode.UpArrow,
            GlfwKey.PageUp => KeyCode.PageUp,
            GlfwKey.PageDown => KeyCode.PageDown,
            GlfwKey.Home => KeyCode.Home,
            GlfwKey.End => KeyCode.End,
            GlfwKey.CapsLock => KeyCode.CapsLock,
            GlfwKey.ScrollLock => KeyCode.ScrollLock,
            GlfwKey.NumLock => KeyCode.Numlock,
            GlfwKey.PrintScreen => KeyCode.Print,
            GlfwKey.Pause => KeyCode.Pause,
            >= GlfwKey.F1 and <= GlfwKey.F15 => KeyCode.F1 + (key - GlfwKey.F1),
            >= GlfwKey.Kp0 and <= GlfwKey.Kp9 => KeyCode.Keypad0 + (key - GlfwKey.Kp0),
            GlfwKey.KpDecimal => KeyCode.KeypadPeriod,
            GlfwKey.KpDivide => KeyCode.KeypadDivide,
            GlfwKey.KpMultiply => KeyCode.KeypadMultiply,
            GlfwKey.KpSubtract => KeyCode.KeypadMinus,
            GlfwKey.KpAdd => KeyCode.KeypadPlus,
            GlfwKey.KpEnter => KeyCode.KeypadEnter,
            GlfwKey.KpEqual => KeyCode.KeypadEquals,
            GlfwKey.LeftShift => KeyCode.LeftShift,
            GlfwKey.LeftControl => KeyCode.LeftControl,
            GlfwKey.LeftAlt => KeyCode.LeftAlt,
            GlfwKey.LeftSuper => KeyCode.LeftWindows,
            GlfwKey.RightShift => KeyCode.RightShift,
            GlfwKey.RightControl => KeyCode.RightControl,
            GlfwKey.RightAlt => KeyCode.RightAlt,
            GlfwKey.RightSuper => KeyCode.RightWindows,
            GlfwKey.Menu => KeyCode.Menu,
            _ => KeyCode.None,
        };
    }
}
