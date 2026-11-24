using System;
using System.Collections.Generic;
using System.Diagnostics;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;

namespace OniMoono.Desktop.D3d11
{
    /// The loading screen's animation on the Direct3D 11 device (D3d11Present), as VkLoadAnimation
    /// on Vulkan's presenter: while a save loads (LoadingScreen), the last frame shown again 30
    /// times a second with the progress bar under it and the loading screen's kanim playing on
    /// from its frames rendered ahead; the first frame after the loading screen's ends it, and
    /// nothing is drawn over a game frame. The arming, pacing and bar are GlLoadAnimation's
    /// (Wanted, WatchdogDue, Advance, LoadProgress) and the run's bookkeeping VkLoadAnimation's;
    /// what is drawn is returned as quads for the device's presenter program: the kanim's tile
    /// from its strip and the bar's rectangles, opaque.
    ///
    /// Everything here runs on the device thread, which also renders the strip: it is handed
    /// over (HandOver) by a command recorded after the strip's rendering, so the presenter's draws
    /// that read it come after it on the one immediate context, and nothing waits before it is
    /// given back (Done), after which the main thread releases it ("Direct3D 11 defers the
    /// destruction of objects", ID3D11DeviceContext::Flush, Remarks).
    internal sealed unsafe class D3d11LoadAnimation
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        long lastFrameTicks;
        bool running, runByWatchdog, replayed;
        long runStart, lastRedraw, lastShown;
        int redraws, longGaps;
        double longestGap, longGapMs, longGapShowMs;

        /// Milliseconds between redraws (GlLoadAnimation's 30 a second).
        const double RedrawMs = 1000.0 / 30;

        public bool Running => running;
        public double IdleMs => (clock.ElapsedTicks - lastFrameTicks) * 1000.0 / Stopwatch.Frequency;

        /// Milliseconds until the next redraw of a run is due (0: due; the first at once).
        public int DueMs
        {
            get
            {
                if (!running || redraws == 0) return 0;
                double left = RedrawMs - (clock.ElapsedTicks - lastRedraw) * 1000.0 / Stopwatch.Frequency;
                return left <= 0 ? 0 : (int)Math.Ceiling(left);
            }
        }

        // ----- Kanim frames -----

        LoadingScreen.KanimFrames frames, pending;
        D3d11Texture strip, pendingStrip;

        /// The frames' strip has been rendered (device thread, by a command after its rendering).
        public void HandOver(LoadingScreen.KanimFrames handed, D3d11Texture image)
        {
            if (pending != null) pending.Done = true;
            pending = handed;
            pendingStrip = image;
        }

        void TakePending(int armed)
        {
            if (pending == null) return;
            if (pending.Frame != armed)
            {
                pending.Done = true;
                pending = null;
                return;
            }
            GiveBack();
            frames = pending;
            strip = pendingStrip;
            pending = null;
        }

        void GiveBack()
        {
            if (frames == null) return;
            frames.Done = true;
            frames = null;
            strip = null;
        }

        /// Starts or continues the run over the frame shown now.
        public void Begin(int armed, bool byWatchdog)
        {
            long now = clock.ElapsedTicks;
            if (!running)
            {
                running = true;
                runByWatchdog = byWatchdog;
                runStart = lastRedraw = now;
                redraws = longGaps = 0;
                longestGap = longGapMs = longGapShowMs = 0;
                lastShown = 0;
                replayed = false;
                progress = 0;
                lastProgress = 0;
            }
            else if (!byWatchdog) runByWatchdog = false;
            TakePending(armed);
        }

        /// A game frame was presented: the run ends unless the frame is still the loading
        /// screen's.
        public void GameFrame(bool stillWanted)
        {
            lastFrameTicks = clock.ElapsedTicks;
            if (running && !stillWanted) End();
        }

        /// Ends a run and gives the frames back (also when the device stops).
        public void End()
        {
            if (!running)
            {
                if (pending != null) pending.Done = true;
                pending = null;
                GiveBack();
                return;
            }
            running = false;
            if (pending != null)
            {
                pending.Done = true;
                pending = null;
            }
            bool hadFrames = frames != null;
            GiveBack();
            if (redraws == 0) return;
            LoadingScreen.LastBar = progress;
            LogTrace();
            double seconds = (lastRedraw - runStart) / (double)Stopwatch.Frequency;
            LoadingScreen.Finished(redraws, seconds, longestGap, longGaps, longGapMs, longGapShowMs, replayed && hadFrames, runByWatchdog);
        }

        /// The redraw's picture was presented.
        public void Shown()
        {
            if (running) lastShown = clock.ElapsedTicks;
        }

        // ----- Drawing -----

        /// This redraw's kanim tile: the strip, the rectangle in the window (pixels, rows top
        /// first) and the strip's texels it shows (pixels of the texture as stored, `SrcTop` the
        /// image row shown at the rectangle's top), and whether it is scaled.
        public readonly record struct Tile(D3d11Texture Strip, int X0, int Top, int X1, int Bottom, int SrcX, int SrcTop, int SrcBottom, bool Scaled, bool First,
            LoadingScreen.KanimFrames Frames);

        /// Counts this redraw and gives its tile, if the run has frames (`t`: the time into the
        /// run, for the bar).
        public Tile? Redraw(int width, int height, out double t)
        {
            long now = clock.ElapsedTicks;
            double gap = (now - lastRedraw) * 1000.0 / Stopwatch.Frequency;
            if (redraws > 0)
            {
                longestGap = Math.Max(longestGap, gap);
                if (gap > 100)
                {
                    double show = lastShown > lastRedraw ? (lastShown - lastRedraw) * 1000.0 / Stopwatch.Frequency : 0;
                    longGaps++;
                    longGapMs += gap;
                    longGapShowMs += show;
                    if (PresentTrace.Enabled)
                        PresentTrace.Log($"loading animation: redraw {redraws} {gap:F0} ms after the one before ({show:F0} ms of it presenting that one), at {Stopwatch.GetTimestamp()}");
                }
            }
            lastRedraw = now;
            redraws++;
            t = (now - runStart) / (double)Stopwatch.Frequency;
            var f = frames;
            if (f == null || strip?.Srv == null) return null;
            int index = (int)Math.Floor(t * f.Rate) + f.Start;
            index = f.Loop ? index % f.Count : Math.Min(index, f.Count - 1);
            var tile = TileOf(f, index, width, height, strip.Height, !replayed) with { Strip = strip };
            replayed = true;
            return tile;
        }

        /// Tile `index` of `f` over a `width` x `height` window (VkLoadAnimation.RecordKanim's
        /// geometry): the area in window pixels, rows top first (the frame shown is stretched
        /// over the window too); the tile's rows in the strip where the device rendered them
        /// (TileTop), read turned over when the strip stores them bottom first.
        internal static Tile TileOf(LoadingScreen.KanimFrames f, int index, int width, int height, int stripHeight, bool first)
        {
            int sx = f.TileX(index), sy = f.TileY(index);
            double kx = (double)width / f.ScreenWidth, ky = (double)height / f.ScreenHeight;
            int x0 = (int)Math.Round(f.Area.xMin * kx), x1 = (int)Math.Round(f.Area.xMax * kx);
            int top = height - (int)Math.Round(f.Area.yMax * ky), bottom = height - (int)Math.Round(f.Area.yMin * ky);
            bool scaled = x1 - x0 != f.TileWidth || bottom - top != f.TileHeight;
            int srcTop = TileTop(f, sy, stripHeight), srcBottom = srcTop + f.TileHeight;
            bool bottomFirst = BottomFirst(f);
            return new Tile(null, x0, top, x1, bottom, sx, bottomFirst ? srcBottom : srcTop, bottomFirst ? srcTop : srcBottom, scaled, first, f);
        }

        /// The strip's rows are stored bottom first (a render texture as Unity's Direct3D 11
        /// programs render one, as on Vulkan), not top first as the back buffer's (ScreenRows).
        internal static bool BottomFirst(LoadingScreen.KanimFrames f) => !f.Strip.ScreenRows;

        /// The first image row of the tile `sy` pixels up the strip (its place, rows bottom
        /// first): the same row stored bottom first, turned over stored top first.
        internal static int TileTop(LoadingScreen.KanimFrames f, int sy, int stripHeight) => BottomFirst(f) ? sy : stripHeight - (sy + f.TileHeight);

        double progress;
        long lastProgress;
        readonly List<(double T, double V, int Phase)> barTrace = new();

        /// The bar's rectangles (OpenGL's window coordinates, from the bottom left), opaque: the
        /// start-up splash's track and fill, the fill's edge columns part-covered, shown 0.15 s
        /// into the run (VkLoadAnimation.RecordBar).
        public void Bar(int width, int height, double t, List<SplashRain.BarRect> rects)
        {
            rects.Clear();
            if (t < 0.15 || width <= 0 || height <= 0) return;
            int barWidth = Math.Max(120, width * 2 / 5), barHeight = Math.Max(4, height / 160);
            int x = (width - barWidth) / 2, y = Math.Max(barHeight * 2, height / 14);
            double fx0, fx1;
            if (LoadProgress.Phase >= 0 || progress > 0)
            {
                long now = clock.ElapsedTicks;
                double dt = lastProgress == 0 ? 0 : (now - lastProgress) / (double)Stopwatch.Frequency;
                lastProgress = now;
                progress = GlLoadAnimation.Advance(progress, LoadProgress.Phase >= 0 ? LoadProgress.Now() : progress, dt);
                if (PresentTrace.Enabled && (barTrace.Count == 0 || t - barTrace[^1].T >= 0.1)) barTrace.Add((t, progress, LoadProgress.Phase));
                fx0 = x;
                fx1 = x + barWidth * progress;
            }
            else
            {
                const double period = 1.6;
                double p = t % period / period;
                p = p * p * (3 - 2 * p);
                double pulse = barWidth * 0.28;
                double start = x - pulse + (barWidth + pulse) * p;
                fx0 = Math.Max(x, start);
                fx1 = Math.Min(x + barWidth, start + pulse);
            }
            BarRects(x, y, barWidth, barHeight, fx0, fx1, rects);
        }

        static readonly (float R, float G, float B) Track = (0.20f, 0.18f, 0.27f), Fill = (0.83f, 0.35f, 0.55f);

        /// The track one pixel around the bar, the whole filled columns from fx0 to fx1, and the
        /// part-covered columns at both ends blended between the two colours (VkLoadAnimation's).
        internal static void BarRects(int x, int y, int barWidth, int barHeight, double fx0, double fx1, List<SplashRain.BarRect> rects)
        {
            rects.Add(new SplashRain.BarRect(x - 1, y - 1, barWidth + 2, barHeight + 2, Track.R, Track.G, Track.B));
            int whole0 = (int)Math.Ceiling(fx0), whole1 = (int)Math.Floor(fx1);
            if (whole1 > whole0) rects.Add(new SplashRain.BarRect(whole0, y, whole1 - whole0, barHeight, Fill.R, Fill.G, Fill.B));
            void Edge(int col, double cover)
            {
                if (cover <= 0 || col < x || col >= x + barWidth) return;
                rects.Add(new SplashRain.BarRect(col, y, 1, barHeight, Track.R + (Fill.R - Track.R) * (float)cover, Track.G + (Fill.G - Track.G) * (float)cover,
                    Track.B + (Fill.B - Track.B) * (float)cover));
            }
            if (fx1 > fx0)
            {
                if (whole1 >= whole0)
                {
                    Edge(whole0 - 1, whole0 - fx0);
                    Edge(whole1, fx1 - whole1);
                }
                else Edge((int)Math.Floor(fx0), fx1 - fx0);
            }
        }

        public void LogTrace()
        {
            if (barTrace.Count == 0) return;
            PresentTrace.Log("loading animation: bar " + string.Join(" ", barTrace.ConvertAll(b => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b.T:F2}:{b.V:F4}:{b.Phase}"))));
            barTrace.Clear();
        }
    }
}
