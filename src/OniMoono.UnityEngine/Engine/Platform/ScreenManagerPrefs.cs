using UnityEngine;

namespace OniMoono.Engine.Platform
{
    /// The window's mode and size between runs, kept in PlayerPrefs under the names Unity's player
    /// uses (measured: the original's Linux prefs file, and its registry key on Windows, where each
    /// value name has an `_h<djb2-xor hash of the name>` suffix): `Screenmanager Fullscreen mode`
    /// (FullScreenMode as an int: 0 exclusive, 1 fullscreen window, 3 windowed),
    /// `Screenmanager Resolution Width/Height`, `Screenmanager Resolution Use Native` (1 when the
    /// size was the monitor's), and the PlayerSettings defaults they were saved under
    /// (`… Default`). Unity's player opens its window from them; ours does too, through PlayerPrefs,
    /// which keeps them where the original does. The window position Unity also saves
    /// (`Screenmanager Window Position X/Y`) is neither saved nor used: ours centres the window.
    internal static class ScreenManagerPrefs
    {
        public const string ModeKey = "Screenmanager Fullscreen mode";
        public const string WidthKey = "Screenmanager Resolution Width";
        public const string HeightKey = "Screenmanager Resolution Height";
        public const string NativeKey = "Screenmanager Resolution Use Native";
        const string DefaultSuffix = " Default";

        /// A start window: Width/Height -1 for the monitor's size.
        public readonly record struct Window(int Width, int Height, FullScreenMode Mode);

        /// PlayerSettings' window: defaultScreenWidth/Height, defaultIsNativeResolution,
        /// fullscreenMode.
        public readonly record struct Defaults(int Width, int Height, bool Native, FullScreenMode Mode)
        {
            public Window Window => Native ? new(-1, -1, Mode) : new(Width, Height, Mode);
        }

        /// The saved window, or the defaults' when none is saved, it is incomplete or invalid, or
        /// it was saved under other defaults (a build whose PlayerSettings changed).
        public static Window Saved(Defaults defaults)
        {
            if (!PlayerPrefs.HasKey(ModeKey) || !PlayerPrefs.HasKey(WidthKey) || !PlayerPrefs.HasKey(HeightKey)) return defaults.Window;
            if (Stale(WidthKey, defaults.Width) || Stale(HeightKey, defaults.Height) || Stale(NativeKey, defaults.Native ? 1 : 0) || Stale(ModeKey, (int)defaults.Mode))
                return defaults.Window;
            int mode = PlayerPrefs.GetInt(ModeKey, -1);
            if (mode is < 0 or > 3) return defaults.Window;
            if (PlayerPrefs.GetInt(NativeKey, 0) == 1) return new(-1, -1, (FullScreenMode)mode);
            int width = PlayerPrefs.GetInt(WidthKey), height = PlayerPrefs.GetInt(HeightKey);
            if (width <= 0 || height <= 0) return defaults.Window;
            return new(width, height, (FullScreenMode)mode);
        }

        static bool Stale(string key, int value) => PlayerPrefs.HasKey(key + DefaultSuffix) && PlayerPrefs.GetInt(key + DefaultSuffix) != value;

        /// The start window: the saved one, then Unity's arguments over it (-screen-width,
        /// -screen-height, -screen-fullscreen 0|1, and -window-mode, which wins over
        /// -screen-fullscreen). -1 / null: not given.
        public static Window Start(Window saved, int screenWidth, int screenHeight, int screenFullscreen, string windowMode)
        {
            var (width, height, mode) = saved;
            if (screenWidth > 0) width = screenWidth;
            if (screenHeight > 0) height = screenHeight;
            if (screenFullscreen == 0) mode = FullScreenMode.Windowed;
            else if (screenFullscreen == 1 && mode == FullScreenMode.Windowed) mode = FullScreenMode.FullScreenWindow;
            mode = windowMode switch
            {
                "exclusive" => FullScreenMode.ExclusiveFullScreen,
                "borderless" => FullScreenMode.FullScreenWindow,
                "windowed" => FullScreenMode.Windowed,
                _ => mode,
            };
            return new(width, height, mode);
        }

        /// Records the window's mode and size (Screen.width/height) as Unity's player does when
        /// they change and at quit; written to disk with the other prefs.
        public static void Record(Defaults defaults, FullScreenMode mode, int width, int height, DisplayMode desktop)
        {
            if (width <= 0 || height <= 0) return;
            Set(ModeKey, (int)mode);
            Set(WidthKey, width);
            Set(HeightKey, height);
            Set(NativeKey, width == desktop.Width && height == desktop.Height ? 1 : 0);
            Set(WidthKey + DefaultSuffix, defaults.Width);
            Set(HeightKey + DefaultSuffix, defaults.Height);
            Set(NativeKey + DefaultSuffix, defaults.Native ? 1 : 0);
            Set(ModeKey + DefaultSuffix, (int)defaults.Mode);
        }

        // Unchanged values are left alone, so a run that changes nothing leaves the file unwritten.
        static void Set(string key, int value)
        {
            if (!PlayerPrefs.HasKey(key) || PlayerPrefs.GetInt(key, value + 1) != value) PlayerPrefs.SetInt(key, value);
        }

        /// The project's defaults, once its settings are loaded (else null).
        public static Defaults? ProjectDefaults()
        {
            var ps = Assets.ProjectSettings.PlayerSettings;
            if (ps == null) return null;
            return new Defaults(ps.Int("defaultScreenWidth"), ps.Int("defaultScreenHeight"), ps.Bool("defaultIsNativeResolution"), (FullScreenMode)ps.Int("fullscreenMode"));
        }

        /// Records the window, when there is one: its current size, or the size just asked for.
        public static void RecordCurrent(int width = -1, int height = -1)
        {
            var platform = PlatformServices.Current;
            if (platform == null || !platform.HasDisplay || ProjectDefaults() is not { } defaults) return;
            Record(defaults, platform.FullScreenMode, width > 0 ? width : platform.FramebufferWidth, height > 0 ? height : platform.FramebufferHeight, platform.DesktopMode);
        }
    }
}
