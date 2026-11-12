using System;
using System.Diagnostics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;

namespace OniMoono.Desktop.Vulkan
{
    /// The loading screen's animation on Vulkan's presenter (VkPresenter), as GlLoadAnimation on
    /// OpenGL's: while a save loads (LoadingScreen), the last frame shown again 30 times a second
    /// with the progress bar under it and the loading screen's kanim playing on from its frames
    /// rendered ahead; the first frame after the loading screen's ends it, and nothing is drawn
    /// over a game frame. The arming, pacing and bar are GlLoadAnimation's (Wanted, WatchdogDue,
    /// Advance, LoadProgress); the drawing is recorded into the presenter's own command buffer:
    /// the kanim's tile blitted from its strip after the frame's copy (both transfers), the bar
    /// as cleared rectangles in a rendering scope (as VkSplash's bar), opaque.
    ///
    /// The strip is the render thread's image, handed over (VkDevice.HandOverLoadingFrames) once
    /// the commands that render it were submitted and it was left in TransferSrcOptimal: the
    /// presenter's submissions come after it on the one queue (whose submissions Brutal's Queue
    /// serializes), only read it, and are waited for before it is given back (Done), after which
    /// the main thread releases it.
    internal sealed unsafe class VkLoadAnimation
    {
        readonly VkContext vk;
        public VkLoadAnimation(VkContext vk) => this.vk = vk;

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

        public int WaitMs => running && redraws > 0 ? Math.Max(1, (int)Math.Ceiling(RedrawMs - (clock.ElapsedTicks - lastRedraw) * 1000.0 / Stopwatch.Frequency)) : 1;

        // ----- Kanim frames (under the presenter's gate) -----

        LoadingScreen.KanimFrames frames, pending;
        VkImageResource strip, pendingStrip;

        /// The render thread has submitted the frames' rendering, `image` left in
        /// TransferSrcOptimal (render thread, under the presenter's gate).
        public void HandOver(LoadingScreen.KanimFrames handed, VkImageResource image)
        {
            if (pending != null) pending.Done = true;
            pending = handed;
            pendingStrip = image;
        }

        /// The presenter's submissions that may read the strip are done (presenter thread).
        Action waitSubmitted;

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
            waitSubmitted?.Invoke();
            frames.Done = true;
            frames = null;
            strip = null;
        }

        /// Starts or continues the run over the frame shown now (presenter thread, under the
        /// gate). `waitSubmitted`: waits for the presenter's submissions (before a strip goes back).
        public void Begin(int armed, bool byWatchdog, Action waitSubmitted)
        {
            this.waitSubmitted = waitSubmitted;
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

        /// A game frame was presented (presenter thread): the run ends unless the frame is still
        /// the loading screen's.
        public void GameFrame(bool stillWanted)
        {
            lastFrameTicks = clock.ElapsedTicks;
            if (running && !stillWanted) End();
        }

        void End()
        {
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

        /// The redraw's picture was issued (presenter thread).
        public void Shown()
        {
            if (running) lastShown = clock.ElapsedTicks;
        }

        // ----- Recording (presenter thread) -----

        double progress;
        long lastProgress;
        readonly System.Collections.Generic.List<(double T, double V, int Phase)> barTrace = new();

        /// Records this redraw's kanim tile into `target` (TransferDstOptimal, rows top first,
        /// `width` x `height`), after the frame's copy; counts the redraw. Returns the time into
        /// the run for the bar.
        public double RecordKanim(CommandBuffer cb, VkImage target, int width, int height)
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
            double t = (now - runStart) / (double)Stopwatch.Frequency;
            var f = frames;
            if (f == null || strip == null) return t;
            int index = (int)Math.Floor(t * f.Rate) + f.Start;
            index = f.Loop ? index % f.Count : Math.Min(index, f.Count - 1);
            int sx = index % f.Columns * f.TileWidth, sy = index / f.Columns * f.TileHeight;
            // The area in window pixels, rows top first (the frame shown is stretched over the
            // window too). The strip is a render texture, stored rows bottom first: the tile is
            // read turned over.
            double kx = (double)width / f.ScreenWidth, ky = (double)height / f.ScreenHeight;
            int x0 = (int)Math.Round(f.Area.xMin * kx), x1 = (int)Math.Round(f.Area.xMax * kx);
            int top = height - (int)Math.Round(f.Area.yMax * ky), bottom = height - (int)Math.Round(f.Area.yMin * ky);
            bool scaled = x1 - x0 != f.TileWidth || bottom - top != f.TileHeight;
            if (!replayed && verify && !scaled) VerifyQueued(cb, target, x0, top, f, f.Start % f.Columns * f.TileWidth, f.Start / f.Columns * f.TileHeight);
            var blit = new VkImageBlit
            {
                SrcSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                DstSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
            };
            int srcTop = strip.Height - (sy + f.TileHeight), srcBottom = strip.Height - sy;
            blit.SrcOffsets[0] = new VkOffset3D(sx, srcBottom, 0);
            blit.SrcOffsets[1] = new VkOffset3D(sx + f.TileWidth, srcTop, 1);
            blit.DstOffsets[0] = new VkOffset3D(x0, top, 0);
            blit.DstOffsets[1] = new VkOffset3D(x1, bottom, 1);
            if (!StripRowsBottomFirst)
            {
                blit.SrcOffsets[0] = new VkOffset3D(sx, sy, 0);
                blit.SrcOffsets[1] = new VkOffset3D(sx + f.TileWidth, sy + f.TileHeight, 1);
            }
            cb.BlitImage(strip.Image, VkImageLayout.TransferSrcOptimal, target, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageBlit>(&blit, 1),
                scaled ? VkFilter.Linear : VkFilter.Nearest);
            replayed = true;
            return t;
        }

        /// How a render texture's rows are stored on Vulkan (set from the replay check while
        /// building; VkDevice renders them as OpenGL lays them out).
        internal static bool StripRowsBottomFirst = Environment.GetEnvironmentVariable("ONI_LOADANIM_STRIP_TOP_FIRST") != "1";

        /// Records the bar into `view` (in ColorAttachmentOptimal, rows top first): the start-up
        /// splash's track and fill, opaque, the fill's edge column part-covered (as SplashRain's
        /// bar), shown 0.15 s into the run.
        public void RecordBar(CommandBuffer cb, VkImageView view, int width, int height, double t)
        {
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
            var color = new VkRenderingAttachmentInfo
            {
                ImageView = view, ImageLayout = VkImageLayout.ColorAttachmentOptimal, LoadOp = VkAttachmentLoadOp.Load, StoreOp = VkAttachmentStoreOp.Store,
            };
            cb.BeginRendering(new VkRenderingInfo { RenderArea = new VkRect2D(width, height), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &color });
            // OpenGL's window coordinates (bottom left) to the image's rows (top first).
            void Rect(int rx, int ry, int rw, int rh, (float R, float G, float B) c)
            {
                int ix = Math.Max(0, rx), iy = Math.Max(0, height - (ry + rh));
                int iw = Math.Min(width - ix, rw - (ix - rx)), ih = Math.Min(height - iy, rh);
                if (iw <= 0 || ih <= 0) return;
                var clear = new VkClearAttachment { AspectMask = VkImageAspectFlags.ColorBit, ColorAttachment = 0 };
                clear.ClearValue.Color.Float32 = new Brutal.Numerics.float4(c.R, c.G, c.B, 1f);
                var rect = new VkClearRect { Rect = new VkRect2D { Offset = new VkOffset2D(ix, iy), Extent = new VkExtent2D(iw, ih) }, LayerCount = 1 };
                cb.ClearAttachments(new ReadOnlySpan<VkClearAttachment>(&clear, 1), new ReadOnlySpan<VkClearRect>(&rect, 1));
            }
            Rect(x - 1, y - 1, barWidth + 2, barHeight + 2, Track);
            int whole0 = (int)Math.Ceiling(fx0), whole1 = (int)Math.Floor(fx1);
            if (whole1 > whole0) Rect(whole0, y, whole1 - whole0, barHeight, Fill);
            // The part-covered columns at both ends move the edge on smoothly.
            void Edge(int col, double cover)
            {
                if (cover <= 0 || col < x || col >= x + barWidth) return;
                Rect(col, y, 1, barHeight, (Track.R + (Fill.R - Track.R) * (float)cover, Track.G + (Fill.G - Track.G) * (float)cover, Track.B + (Fill.B - Track.B) * (float)cover));
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
            cb.EndRendering();
        }

        static readonly (float R, float G, float B) Track = (0.20f, 0.18f, 0.27f), Fill = (0.83f, 0.35f, 0.55f);

        public void LogTrace()
        {
            if (barTrace.Count == 0) return;
            PresentTrace.Log("loading animation: bar " + string.Join(" ", barTrace.ConvertAll(b => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b.T:F2}:{b.V:F4}:{b.Phase}"))));
            barTrace.Clear();
        }

        // ----- ONI_TWEAK_VERIFY=1: the first replayed tile against the frame shown -----

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        VkBufferAlloc verifyShown, verifyTile, verifyStrip;
        LoadingScreen.KanimFrames verifyFrames;
        int verifyW, verifyH;
        bool verifyBgra;

        /// Copies the frame's area (the target, after its copy) and the tile into host buffers;
        /// compared once the slot's fence has signalled (VerifyCompare).
        void VerifyQueued(CommandBuffer cb, VkImage target, int x, int top, LoadingScreen.KanimFrames f, int sx, int sy)
        {
            int w = f.TileWidth, h = f.TileHeight;
            verifyW = w;
            verifyH = h;
            verifyShown = VkBufferAlloc.Create(vk, (long)w * h * 4, VkBufferUsageFlags.TransferDstBit, hostWrite: false, readback: true, kind: "readback buffers");
            verifyTile = VkBufferAlloc.Create(vk, (long)w * h * 4, VkBufferUsageFlags.TransferDstBit, hostWrite: false, readback: true, kind: "readback buffers");
            // The target to TransferSrc and back (it is written again by the blit after this).
            Barrier(cb, target, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal, VkAccessFlags2.TransferWriteBit, VkAccessFlags2.TransferReadBit);
            var region = new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageOffset = new VkOffset3D(x, top, 0),
                ImageExtent = new VkExtent3D(w, h, 1),
            };
            vk.Device.PInvoke.vkCmdCopyImageToBuffer.Invoke(cb.Handle, target, VkImageLayout.TransferSrcOptimal, verifyShown.Buffer, 1, &region);
            Barrier(cb, target, VkImageLayout.TransferSrcOptimal, VkImageLayout.TransferDstOptimal, VkAccessFlags2.TransferReadBit, VkAccessFlags2.TransferWriteBit);
            region.ImageOffset = new VkOffset3D(sx, StripRowsBottomFirst ? strip.Height - (sy + h) : sy, 0);
            vk.Device.PInvoke.vkCmdCopyImageToBuffer.Invoke(cb.Handle, strip.Image, VkImageLayout.TransferSrcOptimal, verifyTile.Buffer, 1, &region);
            // The whole strip, to say which frame the screen showed when it is not the start.
            verifyFrames = f;
            verifyStrip = VkBufferAlloc.Create(vk, (long)strip.Width * strip.Height * 4, VkBufferUsageFlags.TransferDstBit, hostWrite: false, readback: true, kind: "readback buffers");
            var all = new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new VkExtent3D(strip.Width, strip.Height, 1),
            };
            vk.Device.PInvoke.vkCmdCopyImageToBuffer.Invoke(cb.Handle, strip.Image, VkImageLayout.TransferSrcOptimal, verifyStrip.Buffer, 1, &all);
            verifyPending = true;
        }

        bool verifyPending;

        /// The replay check's copies wait for their slot's fence (VerifyCompare).
        public bool VerifyPending => verifyPending;

        /// The target's format stores blue first (DXGI's and most Windows swapchains').
        public void TargetFormat(VkFormat format) => verifyBgra = format is VkFormat.B8G8R8A8UNorm or VkFormat.B8G8R8A8SRGB;

        /// After the slot holding the copies has completed (presenter thread): the comparison.
        public void VerifyCompare()
        {
            if (!verifyPending) return;
            verifyPending = false;
            try
            {
                verifyShown.Invalidate(vk);
                verifyTile.Invalidate(vk);
                byte* a = (byte*)verifyShown.Mapped, b = (byte*)verifyTile.Mapped;
                int w = verifyW, h = verifyH, differ = 0, largest = 0;
                for (int row = 0; row < h; row++)
                {
                    // The tile's rows as copied: bottom first when the strip stores them so.
                    int tileRow = StripRowsBottomFirst ? h - 1 - row : row;
                    for (int col = 0; col < w; col++)
                    {
                        byte* p = a + ((long)row * w + col) * 4, q = b + ((long)tileRow * w + col) * 4;
                        int d = 0;
                        for (int c = 0; c < 3; c++)
                        {
                            int pc = verifyBgra ? p[2 - c] : p[c];
                            d = Math.Max(d, Math.Abs(pc - q[c]));
                        }
                        if (d > 2) differ++;
                        largest = Math.Max(largest, d);
                    }
                }
                LoadingScreen.LastReplayDiffer = differ;
                // Which tile the frame shown is closest to (pixels differing by more than 2).
                verifyStrip.Invalidate(vk);
                byte* st = (byte*)verifyStrip.Mapped;
                var vf = verifyFrames;
                int sw = vf.Columns * w, shh = (vf.Count + vf.Columns - 1) / vf.Columns * h;
                int best = -1, bestDiffer = int.MaxValue;
                for (int k = 0; k < vf.Count; k++)
                {
                    int tx = k % vf.Columns * w, ty = k / vf.Columns * h, n = 0;
                    for (int row = 0; row < h && n < bestDiffer; row += 2)
                        for (int col = 0; col < w; col += 2)
                        {
                            int srow = StripRowsBottomFirst ? ty + (h - 1 - row) : ty + row;
                            byte* p = a + ((long)row * w + col) * 4, q = st + ((long)srow * sw + tx + col) * 4;
                            int d = 0;
                            for (int c = 0; c < 3; c++) d = Math.Max(d, Math.Abs((verifyBgra ? p[2 - c] : p[c]) - q[c]));
                            if (d > 2) n++;
                        }
                    if (n < bestDiffer) { bestDiffer = n; best = k; }
                }
                if (Environment.GetEnvironmentVariable("ONI_LOADANIM_DUMP") is { Length: > 0 } dumpDir)
                {
                    using var file = System.IO.File.Create(System.IO.Path.Combine(dumpDir, "vk-strip.ppm"));
                    file.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {sw} {shh} 255\n"));
                    var line = new byte[sw * 3];
                    for (int r = 0; r < shh; r++)
                    {
                        for (int c = 0; c < sw; c++)
                            for (int k = 0; k < 3; k++) line[c * 3 + k] = st[((long)r * sw + c) * 4 + k];
                        file.Write(line);
                    }
                }
                EngineLog.Engine($"loading animation: replay check: the frame shown is closest to tile {best} ({bestDiffer} of {(w / 2) * (h / 2)} sampled pixels differ); the replay starts at tile {vf.Start}");
                if (Environment.GetEnvironmentVariable("ONI_LOADANIM_DUMP") is { Length: > 0 } dump)
                {
                    // Both as PPM, rows top first, RGB (diagnostics).
                    void Write(string name, byte* px, bool bgra, bool bottomFirst)
                    {
                        using var file = System.IO.File.Create(System.IO.Path.Combine(dump, name));
                        file.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {w} {h} 255\n"));
                        var line = new byte[w * 3];
                        for (int r = 0; r < h; r++)
                        {
                            int src = bottomFirst ? h - 1 - r : r;
                            for (int c = 0; c < w; c++)
                                for (int k = 0; k < 3; k++) line[c * 3 + k] = px[((long)src * w + c) * 4 + (bgra ? 2 - k : k)];
                            file.Write(line);
                        }
                    }
                    Write("vk-shown.ppm", a, verifyBgra, false);
                    Write("vk-tile.ppm", b, false, StripRowsBottomFirst);
                }
                EngineLog.Engine($"loading animation: replay check: the first replayed frame against the frame shown, {differ} of {w * h} pixels differ by more than 2 (largest difference {largest})");
            }
            finally
            {
                verifyShown.Destroy(vk);
                verifyTile.Destroy(vk);
                verifyStrip?.Destroy(vk);
                verifyShown = verifyTile = verifyStrip = null;
            }
        }

        static void Barrier(CommandBuffer cb, VkImage image, VkImageLayout from, VkImageLayout to, VkAccessFlags2 srcAccess, VkAccessFlags2 dstAccess)
        {
            var barrier = new VkImageMemoryBarrier2
            {
                SrcStageMask = VkPipelineStageFlags2.AllTransferBit, SrcAccessMask = srcAccess,
                DstStageMask = VkPipelineStageFlags2.AllTransferBit, DstAccessMask = dstAccess,
                OldLayout = from, NewLayout = to,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = image,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            };
            var dependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier };
            cb.PipelineBarrier2(dependency);
        }
    }
}
