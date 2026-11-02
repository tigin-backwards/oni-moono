using UnityEngine;

namespace OniMoono.Engine.Platform
{
    /// A display mode of a monitor.
    internal readonly record struct DisplayMode(int Width, int Height, uint RefreshNumerator, uint RefreshDenominator);

    /// The window, display and input devices of the machine (GLFW in a windowed player, nothing
    /// in a headless one). Input arrives through PollEvents into the engine's InputState.
    internal interface IPlatform
    {
        bool HasDisplay { get; }

        /// Size of the window's drawable area in pixels (Screen.width/height).
        int FramebufferWidth { get; }
        int FramebufferHeight { get; }
        FullScreenMode FullScreenMode { get; }
        float Dpi { get; }

        /// The current mode of the monitor the window is on, and all its modes.
        DisplayMode DesktopMode { get; }
        DisplayMode[] DisplayModes { get; }

        /// Screen.currentResolution: the desktop's mode, except where the player reports another.
        DisplayMode CurrentResolution => DesktopMode;

        void SetResolution(int width, int height, FullScreenMode mode, uint refreshNumerator, uint refreshDenominator);

        /// Processes OS events, reporting input to `input`. Returns false when the user closed
        /// the window.
        bool PollEvents(InputState input);

        /// Services the window's OS messages in the middle of a long frame, so the OS does not take
        /// the window for hung ("Not responding" on Windows); what they report (input, size,
        /// closing) is kept for the next PollEvents, so nothing changes within the frame.
        void PumpEvents();

        bool CursorVisible { get; set; }
        CursorLockMode CursorLockState { get; set; }
        void SetCursorImage(Color32[] pixels, int width, int height, Vector2 hotspot);
        void SetDefaultCursor();

        string Clipboard { get; set; }
        bool Focused { get; }
    }

    /// No window and no input devices; Screen reports the project's default resolution, as a
    /// player run without graphics does.
    internal sealed class HeadlessPlatform(int width, int height) : IPlatform
    {
        public bool HasDisplay => false;
        public int FramebufferWidth { get; private set; } = width;
        public int FramebufferHeight { get; private set; } = height;
        public FullScreenMode FullScreenMode { get; private set; } = FullScreenMode.Windowed;
        public float Dpi => 0f; // Unity reports 0 when the DPI is unknown
        public DisplayMode DesktopMode => new(FramebufferWidth, FramebufferHeight, 60, 1);
        public DisplayMode[] DisplayModes => [DesktopMode];

        public void SetResolution(int w, int h, FullScreenMode mode, uint num, uint den)
        {
            FramebufferWidth = w;
            FramebufferHeight = h;
            FullScreenMode = mode;
        }

        public bool PollEvents(InputState input) => true;
        public void PumpEvents() { }
        public bool CursorVisible { get; set; } = true;
        public CursorLockMode CursorLockState { get; set; }
        public void SetCursorImage(Color32[] pixels, int width, int height, Vector2 hotspot) { }
        public void SetDefaultCursor() { }
        public string Clipboard { get; set; } = "";
        public bool Focused => true;
    }

    /// Pumps the window's messages from long engine steps (loading objects, instantiating,
    /// uploading, waiting for a bundle) at most every 100 ms on the main thread: the frame's own
    /// poll is at its start, and a start-up frame takes seconds. ONI_PUMP_EVENTS=0 turns it off.
    internal static class MainThreadPump
    {
        static readonly long interval = System.Diagnostics.Stopwatch.Frequency / 10;
        static long last = System.Diagnostics.Stopwatch.GetTimestamp();
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("ONI_PUMP_EVENTS") != "0";

        /// Diagnostics (StartupStats): the longest stretch without a poll or pump, in Stopwatch
        /// ticks, and the pumps made.
        public static long LongestTicks, Pumps;

        /// The frame polled its events (PlatformLoop).
        public static void Polled() => Note(System.Diagnostics.Stopwatch.GetTimestamp());

        static void Note(long now)
        {
            long gap = now - last;
            if (gap > LongestTicks) LongestTicks = gap;
            last = now;
        }

        public static void Maybe()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - last < interval || !Enabled || !Threads.IsMainThread) return;
            Note(now);
            Pumps++;
            PlatformServices.Current.PumpEvents();
        }
    }

    internal static class PlatformServices
    {
        public static IPlatform Current { get; private set; } = new HeadlessPlatform(1024, 768);

        public static void Install(IPlatform platform) => Current = platform;
    }
}
