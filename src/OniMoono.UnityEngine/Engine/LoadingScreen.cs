using System;
using System.Diagnostics;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Engine
{
    /// The loading screen while a save loads (game tweak `loading-animation`, its game side in
    /// OniMoono.Host.GameTweaks.LoadingAnimation): the whole load runs inside one engine frame,
    /// so no frame is presented for seconds. The presenter thread then shows the last frame (the
    /// loading screen) again 30 times a second, with an animation of its own over it and,
    /// when the loading screen plays a kanim (a mod's), that kanim's frames rendered beforehand.
    /// This class is what the main thread and the presenter share; the drawing is the device's
    /// (GlLoadAnimation).
    ///
    /// Two ways in, both by main-thread frame numbers (Time.frameCount, which the presenter's
    /// frames carry):
    /// - **Armed** by a scene load that starts while the loading overlay is up (SceneLoader's
    ///   LoadStarting): every frame shown from before that frame is the loading screen, and the
    ///   first frame of the load's own frame or later ends it.
    /// - **The overlay is up** (LoadingOverlay.Load until a scene load or LoadingOverlay.Clear):
    ///   a frame shown from then on that no frame follows for WatchdogMs is the loading screen
    ///   too, until the next frame comes.
    ///
    /// Nothing here is game state: nothing the game or a mod reads changes.
    internal static class LoadingScreen
    {
        /// The tweak is on (set once at start-up, before any load).
        public static bool Enabled;

        /// No frame for this long after one shown with the overlay up starts the animation.
        public const int WatchdogMs = 250;

        static volatile int armedFrame = -1, overlayFrame = -1;

        /// The main-thread frame a scene load under the loading overlay started in; -1: none.
        public static int ArmedFrame => armedFrame;

        /// The frame the loading overlay was put up in, while it is up; -1: not up.
        public static int OverlayFrame => overlayFrame;

        /// The presenter's wake-up (GlDevice): the arming above changed while it may be waiting.
        public static Action Changed;

        /// The loading overlay was put up this frame (main thread).
        public static void OverlayShown()
        {
            if (!Enabled) return;
            overlayFrame = Time.frameCount;
            Changed?.Invoke();
        }

        /// The loading overlay is gone without a load (main thread).
        public static void OverlayCleared() => overlayFrame = -1;

        /// A scene load starts under the loading overlay (main thread, its frame's start).
        public static void Arm()
        {
            if (!Enabled) return;
            armedFrame = Time.frameCount;
            overlayFrame = -1;
            Changed?.Invoke();
        }

        // ----- Kanim frames rendered ahead (main thread) -----

        /// Frames of the loading screen's kanim rendered into one texture, each a tile of `Area`
        /// (screen pixels, rows bottom first) as the screen showed it with the kanim at that
        /// frame. The presenter cycles them over the last frame from `Start` on.
        internal sealed class KanimFrames
        {
            public RenderTextureNative Strip;
            public int Count, Columns, TileWidth, TileHeight;
            public RectInt Area;
            public int ScreenWidth, ScreenHeight;
            /// The tile the screen showed when they were rendered (the kanim's frame then).
            public int Start;
            public float Rate;
            public bool Loop;
            /// The arming they go with (ArmedFrame).
            public int Frame;
            /// The presenter is done with them (presenter thread sets it; the main thread then
            /// releases the texture).
            public volatile bool Done;
        }

        static KanimFrames kept;

        /// Upper bounds on the frames' cost: main-thread time (the loop stops and nothing is
        /// shown past it) and the strip's memory (tiles at a lower resolution past it).
        public const double BudgetMs = 50;
        public const long BudgetBytes = 64L << 20;

        /// The last rendering's main-thread milliseconds and frame count (logs, specs).
        public static double LastRenderMs;
        public static int LastRenderedFrames;

        static bool hooked;

        /// Renders the loading screen once for each frame of a kanim it shows (main thread, at a
        /// scene load's start, before the overlay goes): the kanim's frame is the frame number
        /// its batch's instance data holds, `frameOffset` bytes into `instanceData`, a float
        /// RGBA texture the kanim's draw samples. Each frame number in
        /// [first, first + count) is written there in turn, the overlay canvases drawn into a
        /// tile, and the original bytes put back; the controller itself is not touched. `area`:
        /// the screen pixels to keep. Returns false (and logs why) when it does not render them.
        public static bool RenderKanim(Texture2D instanceData, int frameOffset, int first, int count, int current, float rate, bool loop, RectInt area, string what)
        {
            if (!Enabled || armedFrame != Time.frameCount) return false;
            var device = GraphicsDevice.Current;
            if (device == null || !device.RendersFrames || !device.AnimatesLoading || instanceData == null || count < 2) return false;
            var native = instanceData.Native as Texture2DNative;
            if (native == null || native.Format != TextureFormat.RGBAFloat || frameOffset < 0 || frameOffset + 4 > native.Width * native.Height * 16) return false;
            int screenW = Platform.PlatformServices.Current.FramebufferWidth, screenH = Platform.PlatformServices.Current.FramebufferHeight;
            area = Clamp(area, screenW, screenH);
            if (area.width <= 0 || area.height <= 0) return false;
            var clock = Stopwatch.StartNew();
            // Tiles at a lower resolution when the frames would take more than the budget.
            double scale = Math.Min(1.0, Math.Sqrt((double)BudgetBytes / ((long)area.width * area.height * 4 * count)));
            int tileW = Math.Max(1, (int)(area.width * scale)), tileH = Math.Max(1, (int)(area.height * scale));
            int maxSize = Math.Min(8192, device.Caps.MaxTextureSize);
            int columns = Math.Max(1, Math.Min(count, maxSize / tileW));
            int rows = (count + columns - 1) / columns;
            if (tileW > maxSize || rows * tileH > maxSize)
            {
                EngineLog.Engine($"loading animation: {what}: {count} frames of {tileW}x{tileH} do not fit a texture; not replayed");
                return false;
            }
            var strip = RenderTexture.Describe(columns * tileW, rows * tileH, 24, RenderTextureFormat.ARGB32);
            strip.Name = "loading animation frames";
            var frames = new KanimFrames
            {
                Strip = strip, Count = count, Columns = columns, TileWidth = tileW, TileHeight = tileH, Area = area,
                ScreenWidth = screenW, ScreenHeight = screenH, Start = Math.Clamp(current, 0, count - 1), Rate = rate > 0 ? rate : 30, Loop = loop, Frame = armedFrame,
            };
            float original = ReadFloat(instanceData, frameOffset);
            int rendered = 0;
            bool over = false;
            try
            {
                for (int k = 0; k < count; k++)
                {
                    WriteFloat(instanceData, frameOffset, first + k);
                    instanceData.Apply(false);
                    var tile = new RectInt(k % columns * tileW, k / columns * tileH, tileW, tileH);
                    UI.CanvasSystem.RenderOverlaysInto(device, strip, tile, area, clear: k == 0);
                    rendered++;
                    // The first frame's cost, times the frames, past the budget: stop now.
                    if (k == 0 && clock.Elapsed.TotalMilliseconds * count > BudgetMs * 1.5 || clock.Elapsed.TotalMilliseconds > BudgetMs)
                    {
                        over = true;
                        break;
                    }
                }
            }
            finally
            {
                WriteFloat(instanceData, frameOffset, original);
                instanceData.Apply(false);
                UI.CanvasSystem.RestoreScreenBinding(device);
            }
            LastRenderMs = clock.Elapsed.TotalMilliseconds;
            LastRenderedFrames = rendered;
            if (over)
            {
                EngineLog.Engine($"loading animation: {what}: rendering its {count} frames would take over {BudgetMs:F0} ms ({rendered} took {LastRenderMs:F1} ms); not replayed");
                Release(strip);
                device.FlushRecorded();
                return false;
            }
            Drop();
            kept = frames;
            Hook();
            device.HandOverLoadingFrames(frames);
            device.FlushRecorded();
            EngineLog.Engine($"loading animation: {what}: {count} frames of {area.width}x{area.height} at ({area.x}, {area.y}){(tileW != area.width ? $" kept at {tileW}x{tileH}" : "")}, {rate:F0} fps{(loop ? ", looping" : "")}, rendered in {LastRenderMs:F1} ms");
            return true;
        }

        static RectInt Clamp(RectInt r, int w, int h)
        {
            int x0 = Math.Clamp(r.xMin, 0, w), y0 = Math.Clamp(r.yMin, 0, h), x1 = Math.Clamp(r.xMax, 0, w), y1 = Math.Clamp(r.yMax, 0, h);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        static float ReadFloat(Texture2D t, int offset) => t.GetRawTextureData<float>()[offset / 4];

        static void WriteFloat(Texture2D t, int offset, float value)
        {
            var data = t.GetRawTextureData<float>();
            data[offset / 4] = value;
        }

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            PlayerLoop.FrameStart += ReleaseDone;
        }

        /// Each frame's start: the frames the presenter is done with are released.
        static void ReleaseDone()
        {
            if (kept != null && kept.Done) Drop();
        }

        static void Drop()
        {
            if (kept == null) return;
            var strip = kept.Strip;
            kept.Done = true;
            kept = null;
            Release(strip);
        }

        static void Release(RenderTextureNative strip)
        {
            if (strip?.Gpu == null) return;
            GraphicsDevice.Release(ref strip.Gpu);
        }

        // ----- What the presenter saw (presenter thread writes; logs and specs read) -----

        /// Loads the animation ran during, and over the last of them: redraws, seconds shown,
        /// the longest time between two redraws, the redraws that came over 100 ms after the
        /// one before, and whether kanim frames were replayed.
        public static int Loads, LastRedraws, LastLongGaps;
        public static double LastSeconds, LastLongestGapMs;
        public static bool LastReplayed;

        /// ONI_TWEAK_VERIFY=1: pixels of the first replayed frame that differ from the frame shown
        /// (GlLoadAnimation); -1 before a check.
        public static int LastReplayDiffer = -1;

        /// The progress bar's value when the load's last frame came (0 to 1; 0 without a bar).
        public static double LastBar;

        public static void Finished(int redraws, double seconds, double longestGapMs, int longGaps, double longGapMs, double longGapShowMs, bool replayed, bool watchdog)
        {
            Loads++;
            LastRedraws = redraws;
            LastSeconds = seconds;
            LastLongestGapMs = longestGapMs;
            LastLongGaps = longGaps;
            LastReplayed = replayed;
            EngineLog.Engine($"loading animation: {redraws} redraws over {seconds:F1} s{(watchdog ? " (no frame for a while under the loading overlay)" : "")}{(replayed ? ", the kanim replayed" : "")}; "
                             + $"longest gap between redraws {longestGapMs:F0} ms, {longGaps} over 100 ms ({longGapMs:F0} ms in all, {longGapShowMs:F0} ms of it presenting)");
        }
    }
}
