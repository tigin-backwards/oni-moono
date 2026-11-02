using System;
using System.Linq;
using OniMoono.Engine.Platform;

namespace UnityEngine
{
    public partial class Screen
    {
        static IPlatform P => PlatformServices.Current;

        public static int width => P.FramebufferWidth;
        public static int height => P.FramebufferHeight;

        /// The whole screen: a desktop player has no notches or rounded corners (measured).
        public static Rect safeArea => new(0f, 0f, width, height);
        public static float dpi => P.Dpi;

        public static bool fullScreen
        {
            get => P.FullScreenMode != FullScreenMode.Windowed;
            set => SetResolution(width, height, value ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);
        }

        public static FullScreenMode fullScreenMode
        {
            get => P.FullScreenMode;
            set => SetResolution(width, height, value);
        }

        public static Resolution currentResolution => ToResolution(P.CurrentResolution);

        /// Supported fullscreen modes of the monitor, smallest first.
        /// The platform's modes in its order (sorted by width for modes a display reports, as
        /// Unity documents; its standard-resolution table in Unity's own order).
        public static Resolution[] resolutions => P.DisplayModes.Select(ToResolution).ToArray();

        static Resolution ToResolution(DisplayMode m)
            => new() { width = m.Width, height = m.Height, refreshRateRatio = new RefreshRate { numerator = m.RefreshNumerator, denominator = m.RefreshDenominator } };

        public static void SetResolution(int width, int height, FullScreenMode fullscreenMode, RefreshRate preferredRefreshRate)
            => P.SetResolution(width, height, fullscreenMode, preferredRefreshRate.numerator, preferredRefreshRate.denominator);

        public static void SetResolution(int width, int height, FullScreenMode fullscreenMode)
            => P.SetResolution(width, height, fullscreenMode, 0, 1);

        public static void SetResolution(int width, int height, bool fullscreen)
            => SetResolution(width, height, fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);
    }

    public partial struct Resolution
    {
        private int m_Width;
        private int m_Height;
        private RefreshRate m_RefreshRate;

        public int width { get => m_Width; set => m_Width = value; }
        public int height { get => m_Height; set => m_Height = value; }
        public RefreshRate refreshRateRatio { get => m_RefreshRate; set => m_RefreshRate = value; }

        public override string ToString() => $"{m_Width} x {m_Height} @ {m_RefreshRate}Hz";
    }

    /// The player has one display: the window's monitor.
    public partial class Display
    {
        public static Display[] displays = { new Display() };

        public static Display main => displays[0];

        Display() { }

        public int renderingWidth => PlatformServices.Current.FramebufferWidth;
        public int renderingHeight => PlatformServices.Current.FramebufferHeight;
        public int systemWidth => PlatformServices.Current.DesktopMode.Width;
        public int systemHeight => PlatformServices.Current.DesktopMode.Height;
        public bool active => true;

        /// Screen position relative to the display under it, display index in z. With one display
        /// that is the position itself on display 0.
        public static Vector3 RelativeMouseAt(Vector3 inputMouseCoordinates) => new(inputMouseCoordinates.x, inputMouseCoordinates.y, 0f);
    }

    public partial class Cursor
    {
        public static bool visible
        {
            get => PlatformServices.Current.CursorVisible;
            set => PlatformServices.Current.CursorVisible = value;
        }

        public static CursorLockMode lockState
        {
            get => PlatformServices.Current.CursorLockState;
            set => PlatformServices.Current.CursorLockState = value;
        }

        /// A hardware cursor from a texture (null restores the system arrow). The texture must be
        /// readable, as Unity requires.
        public static void SetCursor(Texture2D texture, Vector2 hotspot, CursorMode cursorMode)
        {
            if (texture == null)
            {
                PlatformServices.Current.SetDefaultCursor();
                return;
            }
            if (!texture.isReadable)
            {
                Debug.LogError("Invalid texture used for cursor - check importer settings or texture creation. Texture must be RGBA32, readable, have alphaIsTransparency enabled and have no mip chain.");
                return;
            }
            PlatformServices.Current.SetCursorImage(texture.GetPixels32(), texture.width, texture.height, hotspot);
        }
    }
}

namespace UnityEngineInternal
{
    internal partial class DisplayInternal
    {
        /// Display index > 0 that exists (the player has only the main display).
        internal static bool IsASecondaryDisplayIndex(int displayIndex) => displayIndex > 0 && displayIndex < UnityEngine.Display.displays.Length;
    }
}
