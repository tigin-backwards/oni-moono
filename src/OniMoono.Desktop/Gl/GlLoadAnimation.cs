using System;
using System.Diagnostics;
using Brutal.OpenGlApi;
using OniMoono.Engine;
using static OniMoono.Desktop.Gl.GlConst;
using N = Brutal.OpenGlApi.OpenGl.Native;

namespace OniMoono.Desktop.Gl
{
    /// When the loading screen is up and no frame comes (LoadingScreen: a save loading in one
    /// engine frame), the presenter shows the last frame again 30 times a second with
    /// this over it: a small bar low on the screen, the start-up bar's track with a pink pulse
    /// gliding along it, and the loading screen's kanim (a mod's) playing on from the frames
    /// rendered for it beforehand. It never draws over a frame that came after the loading
    /// screen's: the first such frame ends it.
    internal sealed unsafe class GlLoadAnimation
    {
        /// Whether to animate over the frame shown now, by its main-thread frame number
        /// (presenter thread): before an armed load's frame; or, with the loading overlay up,
        /// after the frame it was put up in once no frame has come for the watchdog's time
        /// (`idleMs`, since the last frame was presented).
        internal static bool Wanted(int shownFrame, int armedFrame, int overlayFrame, double idleMs)
            => shownFrame >= 0 && (armedFrame >= 0 && shownFrame < armedFrame
                                   || overlayFrame >= 0 && shownFrame >= overlayFrame && idleMs >= LoadingScreen.WatchdogMs);

        /// Milliseconds until the watchdog could start the animation over the frame shown now;
        /// -1: it cannot (no overlay, or the frame is older than it).
        internal static int WatchdogDue(int shownFrame, int overlayFrame, double idleMs)
            => overlayFrame >= 0 && shownFrame >= overlayFrame ? Math.Max(1, (int)Math.Ceiling(LoadingScreen.WatchdogMs - idleMs)) : -1;

        readonly Stopwatch clock = Stopwatch.StartNew();
        long lastFrameTicks;
        bool running;
        bool runByWatchdog;
        long runStart, lastRedraw;
        int redraws, longGaps;
        double longestGap, longGapMs, longGapShowMs;
        /// When the last redraw's picture was issued (Shown).
        long lastShown;
        bool replayed;

        /// The animation is running (presenter thread).
        public bool Running => running;

        /// A game frame was presented (presenter thread): an animation running ends unless the
        /// frame is still the loading screen's (`stillWanted`).
        public void GameFrame(bool stillWanted)
        {
            lastFrameTicks = clock.ElapsedTicks;
            if (running && !stillWanted) End();
        }

        /// Milliseconds between redraws: 30 a second, the rate kanims are authored at. Each redraw
        /// is a full window's copy and swap, which on Linux under X11 is CPU work beside the load
        /// (measured: docs/research/save-load-and-loading-screen.md).
        const double RedrawMs = 1000.0 / 30;

        /// How long to wait for the next redraw, from the last one (presenter thread).
        public int WaitMs => running && redraws > 0 ? Math.Max(1, (int)Math.Ceiling(RedrawMs - (clock.ElapsedTicks - lastRedraw) * 1000.0 / Stopwatch.Frequency)) : 1;

        public double IdleMs => (clock.ElapsedTicks - lastFrameTicks) * 1000.0 / Stopwatch.Frequency;

        void End()
        {
            running = false;
            if (pending != null)
            {
                pending.Done = true;
                pending = null;
            }
            if (redraws == 0)
            {
                if (frames != null) frames.Done = true;
                frames = null;
                ReleaseFence();
                return;
            }
            double seconds = (lastRedraw - runStart) / (double)Stopwatch.Frequency;
            LoadingScreen.LastBar = progress;
            if (barTrace.Count > 0)
                PresentTrace.Log("loading animation: bar " + string.Join(" ", barTrace.ConvertAll(b => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b.T:F2}:{b.V:F4}:{b.Phase}"))));
            LoadingScreen.Finished(redraws, seconds, longestGap, longGaps, longGapMs, longGapShowMs, replayed, runByWatchdog);
            if (frames != null)
            {
                frames.Done = true;
                frames = null;
            }
            ReleaseFence();
            if (readFramebuffer != 0) GlApi.DeleteFramebuffer(readFramebuffer);
            readFramebuffer = 0;
            stripTexture = 0;
        }

        // ----- Kanim frames (handed over by the GL thread, under the presenter's gate) -----

        LoadingScreen.KanimFrames frames, pending;
        uint pendingTexture, stripTexture, readFramebuffer;
        GlSync fence;
        bool hasFence, fenceDone;

        /// The GL thread has recorded the frames' rendering; `fence` follows it (GL thread,
        /// under the presenter's gate).
        public void HandOver(LoadingScreen.KanimFrames handed, uint texture, GlSync after)
        {
            if (pending != null) pending.Done = true;
            pending = handed;
            pendingTexture = texture;
            pendingFence = after;
        }

        GlSync pendingFence;

        void TakePending(int armed)
        {
            if (pending == null) return;
            if (pending.Frame != armed)
            {
                pending.Done = true;
                pending = null;
                return;
            }
            if (frames != null) frames.Done = true;
            ReleaseFence();
            if (readFramebuffer != 0) GlApi.DeleteFramebuffer(readFramebuffer);
            readFramebuffer = 0;
            frames = pending;
            stripTexture = pendingTexture;
            fence = pendingFence;
            hasFence = true;
            fenceDone = false;
            pending = null;
        }

        void ReleaseFence()
        {
            if (hasFence) N.glDeleteSync(fence);
            hasFence = false;
        }

        // ----- Drawing (presenter thread, the window's framebuffer bound) -----

        uint program, vertexArray;
        int rectLocation, colorLocation, sizeLocation;
        bool failed;

        static readonly (float R, float G, float B) Track = (0.20f, 0.18f, 0.27f), Fill = (0.83f, 0.35f, 0.55f);

        bool Ready()
        {
            if (failed) return false;
            if (program != 0) return true;
            // One quad from gl_VertexID: `rect` (x0, y0, x1, y1, window pixels, y up); the
            // colour's alpha takes the part of each pixel the rect covers, so its edges move
            // smoothly at any speed.
            const string vs = "#version 330 core\nuniform vec4 rect;\nuniform vec2 size;\nvoid main() { vec2 c = vec2(gl_VertexID & 1, gl_VertexID >> 1); vec2 p = mix(floor(rect.xy), ceil(rect.zw), c); gl_Position = vec4(p / size * 2.0 - 1.0, 0.0, 1.0); }\n";
            const string fs = "#version 330 core\nuniform vec4 rect;\nuniform vec4 color;\nout vec4 o;\nvoid main() { vec2 p = gl_FragCoord.xy; vec2 a = clamp(min(p + 0.5, rect.zw) - max(p - 0.5, rect.xy), 0.0, 1.0); o = vec4(color.rgb, 1.0) * color.a * a.x * a.y; }\n";
            uint v = GlApi.CompileShader(VertexShader, vs, out var vlog), f = GlApi.CompileShader(FragmentShader, fs, out var flog);
            program = v != 0 && f != 0 ? GlApi.LinkProgram(v, f, 0, out _) : 0;
            if (v != 0) N.glDeleteShader(new GlShader(v));
            if (f != 0) N.glDeleteShader(new GlShader(f));
            if (program == 0)
            {
                failed = true;
                EngineLog.Engine($"loading animation: program failed: {vlog} {flog}");
                return false;
            }
            rectLocation = GlApi.UniformLocation(program, "rect");
            colorLocation = GlApi.UniformLocation(program, "color");
            sizeLocation = GlApi.UniformLocation(program, "size");
            MakeVertexArray();
            return true;
        }

        void MakeVertexArray()
        {
            GlVertexArray vao = default;
            N.glGenVertexArrays(1, &vao);
            vertexArray = vao.Id;
        }

        /// The presenter now draws with another context (Windows): the objects contexts do not
        /// share are made anew.
        public void ContextReplaced()
        {
            if (vertexArray != 0) MakeVertexArray();
            readFramebuffer = 0;
        }

        /// Starts or continues the animation over the frame shown now (presenter thread).
        public void Begin(int armed, bool byWatchdog)
        {
            long now = clock.ElapsedTicks;
            if (!running)
            {
                running = true;
                runByWatchdog = byWatchdog;
                progress = 0;
                lastProgress = 0;
                barTrace.Clear();
                runStart = lastRedraw = now;
                redraws = longGaps = 0;
                longestGap = longGapMs = longGapShowMs = 0;
                lastShown = 0;
                replayed = false;
            }
            // A scene load that arms it while the watchdog's run goes on makes it the load's.
            else if (!byWatchdog) runByWatchdog = false;
            TakePending(armed);
        }

        /// Draws over the window's framebuffer (bound): the kanim's frame due now, then the bar.
        /// `flip`: the target's rows run top-down (DXGI).
        public void Draw(int width, int height, bool flip, uint target)
        {
            if (!running || width <= 0 || height <= 0) return;
            long now = clock.ElapsedTicks;
            double gap = (now - lastRedraw) * 1000.0 / Stopwatch.Frequency;
            if (redraws > 0)
            {
                longestGap = Math.Max(longestGap, gap);
                if (gap > 100)
                {
                    // The part spent drawing and presenting the redraw before (the driver, the
                    // GPU), the rest waiting for this one (a garbage collection stops the thread).
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
            double t = (now - runStart) / (double)Stopwatch.Frequency;
            DrawKanim(width, height, flip, t, target);
            DrawBar(width, height, flip, t);
        }

        /// The redraw's picture was issued (presenter thread).
        public void Shown()
        {
            if (running) lastShown = clock.ElapsedTicks;
        }

        void DrawKanim(int width, int height, bool flip, double t, uint target)
        {
            var f = frames;
            if (f == null || stripTexture == 0) return;
            if (!fenceDone)
            {
                // The rendering is still on the GL thread's queue: the frame shown already has the
                // kanim at its start frame.
                if (N.glClientWaitSync(fence, default, 0) == 0x911B /* TIMEOUT_EXPIRED */) return;
                fenceDone = true;
                ReleaseFence();
            }
            int index = (int)Math.Floor(t * f.Rate) + f.Start;
            index = f.Loop ? index % f.Count : Math.Min(index, f.Count - 1);
            if (readFramebuffer == 0)
            {
                readFramebuffer = GlApi.GenFramebuffer();
                GlApi.BindFramebuffer(ReadFramebuffer, readFramebuffer);
                N.glFramebufferTexture2D(ReadFramebuffer, ColorAttachment0, TexTarget2D, stripTexture, 0);
            }
            else GlApi.BindFramebuffer(ReadFramebuffer, readFramebuffer);
            int sx = f.TileX(index), sy = f.TileY(index);
            // The area in window pixels: the frame shown is stretched over the window too.
            double kx = (double)width / f.ScreenWidth, ky = (double)height / f.ScreenHeight;
            int x0 = (int)Math.Round(f.Area.xMin * kx), x1 = (int)Math.Round(f.Area.xMax * kx);
            int y0 = (int)Math.Round(f.Area.yMin * ky), y1 = (int)Math.Round(f.Area.yMax * ky);
            if (flip) (y0, y1) = (height - y0, height - y1);
            bool scaled = x1 - x0 != f.TileWidth || Math.Abs(y1 - y0) != f.TileHeight;
            if (!replayed && verify) Verify(f, target, x0, Math.Min(y0, y1), flip, scaled);
            GlApi.BindFramebuffer(ReadFramebuffer, readFramebuffer);
            N.glBlitFramebuffer(sx, sy, sx + f.TileWidth, sy + f.TileHeight, x0, y0, x1, y1, ColorBufferBit, scaled ? Linear : Nearest);
            replayed = true;
        }

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// ONI_TWEAK_VERIFY=1: before the first replayed frame is drawn, its tile (the kanim at the
        /// frame the screen shows) is compared with the frame shown, pixel by pixel, over the area
        /// (`target`'s pixels at x, y, rows flipped when `flip`): a replay that draws anything the
        /// live kanim did not differs there.
        void Verify(LoadingScreen.KanimFrames f, uint target, int x, int y, bool flip, bool scaled)
        {
            if (scaled)
            {
                EngineLog.Engine("loading animation: replay check skipped (the window's size is not the frame's)");
                return;
            }
            int w = f.TileWidth, h = f.TileHeight;
            var shownPixels = new byte[w * h * 4];
            var tilePixels = new byte[w * h * 4];
            N.glPixelStorei(PackAlignment, 4);
            GlApi.BindFramebuffer(ReadFramebuffer, target);
            fixed (byte* p = shownPixels) N.glReadPixels(x, y, w, h, Rgba, UnsignedByte, p);
            GlApi.BindFramebuffer(ReadFramebuffer, readFramebuffer);
            int sx = f.TileX(f.Start), sy = f.TileY(f.Start);
            fixed (byte* p = tilePixels) N.glReadPixels(sx, sy, w, h, Rgba, UnsignedByte, p);
            int differ = 0, largest = 0, bx0 = w, by0 = h, bx1 = -1, by1 = -1;
            for (int row = 0; row < h; row++)
            {
                int shownRow = flip ? h - 1 - row : row;
                for (int col = 0; col < w; col++)
                {
                    int a = (shownRow * w + col) * 4, b = (row * w + col) * 4, d = 0;
                    for (int c = 0; c < 3; c++) d = Math.Max(d, Math.Abs(shownPixels[a + c] - tilePixels[b + c]));
                    if (d > 2)
                    {
                        differ++;
                        bx0 = Math.Min(bx0, col); bx1 = Math.Max(bx1, col);
                        by0 = Math.Min(by0, row); by1 = Math.Max(by1, row);
                    }
                    largest = Math.Max(largest, d);
                }
            }
            LoadingScreen.LastReplayDiffer = differ;
            if (Environment.GetEnvironmentVariable("ONI_LOADANIM_DUMP") is { Length: > 0 } dump)
            {
                // Both as PPM, rows top first (diagnostics).
                void Write(string name, byte[] px, bool flipped)
                {
                    using var file = System.IO.File.Create(System.IO.Path.Combine(dump, name));
                    var header = System.Text.Encoding.ASCII.GetBytes($"P6 {w} {h} 255\n");
                    file.Write(header);
                    for (int r = h - 1; r >= 0; r--)
                    {
                        int src = flipped ? h - 1 - r : r;
                        for (int c = 0; c < w; c++) file.Write(px, (src * w + c) * 4, 3);
                    }
                }
                Write("shown.ppm", shownPixels, flip);
                Write("tile.ppm", tilePixels, false);
            }
            EngineLog.Engine($"loading animation: replay check: the first replayed frame against the frame shown, {differ} of {w * h} pixels differ by more than 2 (largest difference {largest}{(differ > 0 ? $", within {bx0}..{bx1} x {by0}..{by1} of the {w}x{h} area, rows bottom first" : "")})");
        }

        // The bar drawn last (0 to 1) and when (presenter thread).
        double progress;
        long lastProgress;
        /// ONI_PRESENT_TRACE=1: the bar every 0.1 s (seconds into the run, value, phase), logged
        /// when the run ends.
        readonly System.Collections.Generic.List<(double T, double V, int Phase)> barTrace = new();

        /// The bar `seconds` after it was `shown`, towards `target`: a quarter-second smoothing,
        /// never back.
        internal static double Advance(double shown, double target, double seconds)
        {
            if (!(target > shown)) return shown;
            if (seconds <= 0) return shown;
            return Math.Min(1, shown + (target - shown) * (1 - Math.Exp(-seconds / 0.25)));
        }

        void DrawBar(int width, int height, bool flip, double t)
        {
            if (!Ready()) return;
            // Fades in after a moment, so a short wait shows nothing.
            const double delay = 0.15, fadeIn = 0.5;
            float alpha = (float)Math.Clamp((t - delay) / fadeIn, 0, 1);
            alpha = alpha * alpha * (3 - 2 * alpha);
            if (alpha <= 0) return;
            // The start-up splash's bar: its place, size and colours.
            float barWidth = Math.Max(120, width * 2 / 5), barHeight = Math.Max(4, height / 160);
            float x = MathF.Round((width - barWidth) / 2), y = Math.Max(barHeight * 2, height / 14);
            float px0, px1;
            if (LoadProgress.Phase >= 0 || progress > 0)
            {
                // How far the load is (LoadProgress), never going back, eased towards it.
                long now = clock.ElapsedTicks;
                double dt = lastProgress == 0 ? 0 : (now - lastProgress) / (double)Stopwatch.Frequency;
                lastProgress = now;
                progress = Advance(progress, LoadProgress.Phase >= 0 ? LoadProgress.Now() : progress, dt);
                px0 = x;
                px1 = x + barWidth * (float)progress;
                if (PresentTrace.Enabled && (barTrace.Count == 0 || t - barTrace[^1].T >= 0.1)) barTrace.Add((t, progress, LoadProgress.Phase));
            }
            else
            {
                // Nothing to measure (no scene load under the overlay): a pulse, a quarter of the
                // track long, glides across it and out every 1.6 s, easing in and out.
                const double period = 1.6;
                float p = (float)(t % period / period);
                p = p * p * (3 - 2 * p);
                float pulse = barWidth * 0.28f;
                float start = x - pulse + (barWidth + pulse) * p;
                px0 = Math.Max(x, start);
                px1 = Math.Min(x + barWidth, start + pulse);
            }

            N.glViewport(0, 0, width, height);
            GlApi.Enable(Blend, true);
            GlApi.Enable(DepthTest, false);
            GlApi.Enable(CullFace, false);
            GlApi.Enable(ScissorTest, false);
            GlApi.Enable(StencilTest, false);
            N.glBlendFuncSeparate(One, OneMinusSrcAlpha, One, OneMinusSrcAlpha);
            N.glUseProgram(new GlProgram(program));
            N.glUniform2f(sizeLocation, width, height);
            N.glBindVertexArray(new GlVertexArray(vertexArray));
            void Rect(float rx0, float ry0, float rx1, float ry1, (float R, float G, float B) c, float a)
            {
                if (rx1 <= rx0 || ry1 <= ry0) return;
                if (flip) (ry0, ry1) = (height - ry1, height - ry0);
                N.glUniform4f(rectLocation, rx0, ry0, rx1, ry1);
                N.glUniform4f(colorLocation, c.R, c.G, c.B, a);
                N.glDrawArrays(TriangleStrip, 0, 4);
            }
            Rect(x - 1, y - 1, x + barWidth + 1, y + barHeight + 1, Track, 0.9f * alpha);
            Rect(px0, y, px1, y + barHeight, Fill, alpha);
            N.glBindVertexArray(default);
            GlApi.Enable(Blend, false);
        }
    }
}
