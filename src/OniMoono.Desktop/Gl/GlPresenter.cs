using System;
using System.Threading;
using Brutal.GlfwApi;
using Brutal.GlfwApi.Win32;
using Brutal.OpenGlApi;
using OniMoono.Engine;
using static OniMoono.Desktop.Gl.GlConst;
using N = Brutal.OpenGlApi.OpenGl.Native;

namespace OniMoono.Desktop.Gl
{
    /// Presentation on a thread of its own, as Unity's player presents from its render thread:
    /// the GL thread draws each frame into an offscreen back buffer of the rendering context;
    /// the presenter thread, current on the window's context (shared with it), waits for that
    /// frame's commands to complete, copies it to the window and swaps. A swap that blocks
    /// (vsync, or a display path that reads the frame back) then overlaps the next frames' work
    /// instead of stalling it. Without vsync up to two frames are queued for presentation. With
    /// vsync, one, and a frame counts as queued until the GPU has done its swap: the driver
    /// returns from SwapBuffers at once and queues frames of its own, which came on top of ours.
    /// Measured on Windows at 60 Hz (a key press to the screen changing, out/local-tools/winlat):
    /// the original 50 ms, ours 83 ms before, 50 ms so. That was an RTX 3090 on NVIDIA's driver
    /// 610.88, windowed under Windows 11's compositor (19827d58). How long SwapBuffers takes to
    /// return and that a fence after it signals once the swap is done are that driver's
    /// behaviour, not documented (GlSwapTarget.Show); other drivers and compositors are
    /// unmeasured. Three back buffers rotate, so the GL thread never draws into a buffer that is
    /// queued or being shown. With vsync the main thread also waits before each frame until the
    /// frame QualitySettings.maxQueuedFrames before it has been through here (QueuedFrames).
    internal sealed unsafe class GlPresenter : IDisposable
    {
        const uint SyncGpuCommandsComplete = 0x9117;
        const uint TimeoutExpired = 0x911B;
        const ulong WaitSliceNanoseconds = 100_000_000;

        /// One offscreen back buffer: colour texture and depth-stencil renderbuffer, attached to
        /// a framebuffer of the rendering context; the presenter attaches the same texture to a
        /// framebuffer of its own (framebuffers are not shared between contexts).
        sealed class Buffer
        {
            public uint Color, Depth, Framebuffer;
            public int Width, Height, Generation;
            // Presenter side.
            public uint PresentFramebuffer;
            public int PresentGeneration = -1;
            /// Signalled when the presenter's copy has read the buffer: the GL thread waits for it
            /// (on the GPU) before drawing into the buffer again.
            public GlSync CopyDone;
            public bool HasCopyDone;
            // What the GL thread drew into it (GL thread): anything this frame, and whether the
            // last thing drawn was a clear; since its storage was (re)allocated, anything at all.
            public bool DrawnThisFrame, ClearedLast, DrawnSinceAllocation;
            public int Index, DrawnFrame = -1;
        }

        readonly GlfwWindow window;
        /// The window whose context this thread presents with: `window`, or for a window made
        /// without a context (Windows, DXGI on) a hidden one, whose context goes on `window`
        /// only for SwapBuffers (GlSwapTarget's Win32GlWindow).
        readonly GlfwWindow contextWindow;
        readonly bool contextFree;
        const int MaxQueued = 2, MaxQueuedWithVsync = 1;

        /// Frames that may wait here: with vsync one (the main thread's wait bounds the rest,
        /// QueuedFrames); without, QualitySettings.maxQueuedFrames, at most two (three back
        /// buffers: two queued and the one being drawn).
        internal static int QueueLimit(int interval) => interval > 0 ? MaxQueuedWithVsync : Math.Clamp(UnityEngine.QualitySettings.maxQueuedFrames, 1, MaxQueued);
        readonly Buffer[] buffers = [new() { Index = 0 }, new() { Index = 1 }, new() { Index = 2 }];
        int current;
        readonly Thread thread;
        readonly object gate = new();
        // Handed to the presenter under the gate; a frame counts as queued until it has been
        // presented.
        readonly System.Collections.Generic.Queue<(Buffer Buffer, GlSync Fence, int Interval, int Width, int Height, long Started, int Frame, long Need)> queue = new();
        /// The frames through this presenter, which the main thread waits on with vsync.
        public readonly QueuedFrames Presented = new();
        int queued;
        bool stopping;
        Exception failure;

        /// The player's loading splash, drawn over the window by this thread (null: none).
        readonly GlSplash splash;
        /// The loading screen's animation while a save loads (LoadingScreen).
        readonly GlLoadAnimation loading = new();
        // The window's framebuffer size as of the latest frame (GL thread writes): what a redraw
        // between frames fills.
        volatile int windowWidth, windowHeight;
        // The buffer last copied to the window (presenter thread).
        Buffer shown;

        /// Where pictures go (presenter thread): DXGI when it is on and starts, else OpenGL's own
        /// SwapBuffers, from start-up or from the moment DXGI fails.
        IWindowTarget target;
        /// Windows, DXGI on: the window's handle (taken on the main thread, the window's).
        readonly IntPtr dxgiWindow;
        /// Windows: the window's handle (taken on the main thread, the window's).
        readonly IntPtr windowHandle;
        /// The adapter DXGI found for the GL context (0: none), for the video memory counters.
        public long DxgiAdapterLuid { get; private set; }

        public GlPresenter(GlfwWindow window, GlfwWindow contextWindow, bool contextFree, GlSplash splash = null, int width = 0, int height = 0)
        {
            this.window = window;
            this.contextWindow = contextWindow;
            this.contextFree = contextFree;
            this.splash = splash;
            windowWidth = width;
            windowHeight = height;
            if (OperatingSystem.IsWindows()) windowHandle = (IntPtr)window.GetWin32Window();
            if (DxgiSwapChain.Enabled) dxgiWindow = windowHandle;
            thread = new Thread(Run) { Name = "Presenter", IsBackground = true };
            thread.Start();
        }

        /// The framebuffer standing in for the back buffer this frame, at the rendering size (the
        /// window's framebuffer, or the smaller size a fullscreen window renders at).
        public uint BackBuffer(int width, int height)
        {
            var b = buffers[current];
            if (b.HasCopyDone)
            {
                N.glWaitSync(b.CopyDone, default, TimeoutIgnored);
                N.glDeleteSync(b.CopyDone);
                b.HasCopyDone = false;
            }
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            // A frame keeps the size it began drawing at: allocating anew would discard what it
            // drew (the window's size changed part way through the frame).
            if (b.Framebuffer == 0 || (b.Width != width || b.Height != height) && !b.DrawnThisFrame) Allocate(b, width, height);
            else if (PresentTrace.Enabled && (b.Width != width || b.Height != height))
                PresentTrace.Log($"frame {Frame}: back buffer {b.Index} kept at {b.Width}x{b.Height} for the rest of the frame (the window is {width}x{height})");
            return b.Framebuffer;
        }

        /// The back buffer to read from (ReadPixels of the screen, captures): this frame's, at the
        /// size it was drawn, even when the window's size has changed since.
        public uint ReadBuffer(int width, int height)
        {
            var b = buffers[current];
            return b.Framebuffer != 0 && b.DrawnThisFrame ? b.Framebuffer : BackBuffer(width, height);
        }

        const ulong TimeoutIgnored = ulong.MaxValue;

        /// The GL thread drew into the current back buffer (a clear when `clear`).
        public void Drew(bool clear)
        {
            var b = buffers[current];
            b.DrawnThisFrame = true;
            b.DrawnSinceAllocation = true;
            b.ClearedLast = clear;
        }

        /// The main thread's frame the GL thread is executing (diagnostics).
        public int Frame;
        /// When the game frame being handed over began (GL thread; PresenterTimings).
        public long FrameStarted;

        void Allocate(Buffer b, int width, int height)
        {
            if (PresentTrace.Enabled)
                PresentTrace.Log($"frame {Frame}: back buffer {b.Index} allocated {width}x{height} (was {b.Width}x{b.Height}){(b.DrawnThisFrame ? ", discarding what the frame drew into it" : "")}");
            b.DrawnSinceAllocation = false;
            if (b.Framebuffer == 0)
            {
                b.Framebuffer = GlApi.GenFramebuffer();
                b.Color = GlApi.GenTexture();
                b.Depth = GlApi.GenRenderbuffer();
            }
            GlBindings.BindTexture(TexTarget2D, b.Color);
            N.glTexImage2D(TexTarget2D, 0, (int)Rgba8, width, height, 0, Rgba, UnsignedByte, null);
            N.glTexParameteri(TexTarget2D, TextureMinFilter, (int)Nearest);
            N.glTexParameteri(TexTarget2D, TextureMagFilter, (int)Nearest);
            N.glTexParameteri(TexTarget2D, TextureMaxLevel, 0);
            N.glBindRenderbuffer(Renderbuffer, new GlRenderbuffer(b.Depth));
            N.glRenderbufferStorage(Renderbuffer, Depth24Stencil8, width, height);
            GlApi.BindFramebuffer(Framebuffer, b.Framebuffer);
            N.glFramebufferTexture2D(Framebuffer, ColorAttachment0, TexTarget2D, b.Color, 0);
            N.glFramebufferRenderbuffer(Framebuffer, DepthStencilAttachment, Renderbuffer, new GlRenderbuffer(b.Depth));
            b.Width = width;
            b.Height = height;
            b.Generation++;
            GlDevice.InvalidateFramebufferBinding();
            GlStateCache.Invalidate();
        }

        /// The buffer last handed to the presenter (GL thread).
        Buffer handedOver;

        internal enum PresentChoice { Frame, LastFrameAgain, Nothing }

        /// What a present shows: the frame, when it drew into its back buffer after the buffer's
        /// storage was last allocated; otherwise the last frame shown, again, or nothing before
        /// the first.
        internal static PresentChoice Choose(bool allocated, bool drawnSinceAllocation, bool drawnThisFrame, bool anyShown)
            => allocated && drawnSinceAllocation && drawnThisFrame ? PresentChoice.Frame
                : anyShown ? PresentChoice.LastFrameAgain : PresentChoice.Nothing;

        /// Hands the frame drawn into the current back buffer to the presenter, once fewer than
        /// two frames are queued (one with vsync), and moves on to the next buffer. The window's
        /// framebuffer is `width` x `height` now; a frame drawn at another size is stretched over
        /// it, bilinearly: a fullscreen window rendering at a smaller size, as the original's is
        /// (measured on Xvfb: 1024x576 on a 1280x720 screen matched a bilinear stretch of its
        /// capture to a mean of 0.04 per channel, nearest 1.26), or a frame drawn before the
        /// window's size changed.
        ///
        /// A frame that drew nothing into its back buffer, or whose buffer was allocated anew
        /// after it drew (the window's size changed), is not shown: its buffer holds an older
        /// frame or undefined content (black on most drivers). The last frame shown is presented
        /// again in its place and the next frame draws into the same buffer, so the screen keeps
        /// the last complete frame, as a game's back buffer does across stalls and resizes.
        /// `need`: the main and GL threads' time on the frame (QueuedFrames).
        public void Present(int interval, int width, int height, long need = 0)
        {
            windowWidth = Math.Max(1, width);
            windowHeight = Math.Max(1, height);
            var b = buffers[current];
            var choice = Choose(b.Framebuffer != 0, b.DrawnSinceAllocation, b.DrawnThisFrame, handedOver != null);
            bool complete = choice == PresentChoice.Frame;
            if (PresentTrace.Enabled)
            {
                if (b.Framebuffer == 0 || !b.DrawnSinceAllocation) PresentTrace.Log($"frame {Frame}: back buffer {b.Index} ({b.Width}x{b.Height}) has nothing drawn since it was allocated: {(handedOver != null ? $"frame {handedOver.DrawnFrame} shown again" : "not presented")}");
                else if (!b.DrawnThisFrame) PresentTrace.Log($"frame {Frame}: nothing drawn into back buffer {b.Index}: {(handedOver != null ? $"frame {handedOver.DrawnFrame} shown again" : "not presented")}");
                else if (b.ClearedLast) PresentTrace.Log($"frame {Frame}: back buffer {b.Index} presented with nothing drawn after its last clear");
            }
            if (!complete)
            {
                b.DrawnThisFrame = false;
                if (choice == PresentChoice.Nothing)
                {
                    Presented.Done(Frame);
                    return;
                }
                b = handedOver;
            }
            else
            {
                b.DrawnFrame = Frame;
                b.DrawnThisFrame = false;
            }
            var fence = N.glFenceSync(SyncGpuCommandsComplete, default);
            N.glFlush();
            lock (gate)
            {
                if (queued >= QueueLimit(interval) && OniMoono.Engine.PlayerLoop.FrameStats)
                {
                    long waiting = GlTimings.Now;
                    while (queued >= QueueLimit(interval) && failure == null) Monitor.Wait(gate);
                    Interlocked.Add(ref OniMoono.Engine.FrameStatsWindow.PresenterQueueWait, GlTimings.Now - waiting);
                }
                while (queued >= QueueLimit(interval) && failure == null) Monitor.Wait(gate);
                if (failure != null) throw new InvalidOperationException("Presenting failed.", failure);
                queue.Enqueue((b, fence, interval, Math.Max(1, width), Math.Max(1, height), complete ? FrameStarted : 0, Frame, need));
                queued++;
                Monitor.PulseAll(gate);
                // The next buffer is never one a repeat is reading (it would take two frames
                // handed over during one repeat, without vsync).
                if (complete)
                    while (repeating == buffers[(current + 1) % buffers.Length] && failure == null) Monitor.Wait(gate);
            }
            if (!complete) return;
            handedOver = b;
            current = (current + 1) % buffers.Length;
        }

        // Under the gate: the buffer a repeat is reading (presenter thread), else null.
        Buffer repeating;

        // Under the gate: the window's contents were damaged since the last copy to it.
        bool refreshWanted;

        /// The window's contents were damaged (main thread, from the window's refresh and focus
        /// events): unless a frame is on its way, the presenter copies the last frame to the
        /// window again and swaps, without waiting for the next frame to render. A compositor
        /// that changes how it shows the window (a fullscreen window losing or regaining the
        /// focus) otherwise shows black, or what was there before, until the next frame.
        public void Refresh()
        {
            lock (gate)
            {
                refreshWanted = true;
                Monitor.PulseAll(gate);
            }
        }

        /// The loading screen's arming changed (main thread, LoadingScreen.Changed): the wait
        /// below is timed anew.
        public void Poke()
        {
            lock (gate) Monitor.PulseAll(gate);
        }

        /// The loading screen's kanim frames, rendered by the commands before this (GL thread).
        public void HandOverLoading(LoadingScreen.KanimFrames frames, uint texture, GlSync after)
        {
            lock (gate)
            {
                loading.HandOver(frames, texture, after);
                Monitor.PulseAll(gate);
            }
        }

        /// Under the gate: 0 when the loading screen's animation is due over the frame shown
        /// now, else how long until it may be (milliseconds), -1 never (no new frame).
        int LoadingWait()
        {
            if (!LoadingScreen.Enabled || shown == null || splash != null && splash.Active) return -1;
            int armed = LoadingScreen.ArmedFrame, overlay = LoadingScreen.OverlayFrame;
            double idle = loading.IdleMs;
            if (GlLoadAnimation.Wanted(shown.DrawnFrame, armed, overlay, idle))
            {
                loading.Begin(armed, byWatchdog: !(armed >= 0 && shown.DrawnFrame < armed));
                return 0;
            }
            return GlLoadAnimation.WatchdogDue(shown.DrawnFrame, overlay, idle);
        }

        /// Waits until every frame handed over has been presented.
        public void WaitIdle()
        {
            lock (gate)
                while (queued > 0 && failure == null) Monitor.Wait(gate);
        }

        void Run()
        {
            try
            {
                contextWindow.MakeContextCurrent();
                target = StartTarget();
                while (true)
                {
                    (Buffer Buffer, GlSync Fence, int Interval, int Width, int Height, long Started, int Frame, long Need) frame = default;
                    bool redraw = false, repeat = false;
                    lock (gate)
                    {
                        while (queue.Count == 0 && !stopping && !(refreshWanted && shown != null))
                        {
                            // While the splash runs, the window is redrawn about 60 times a
                            // second when no game frame comes.
                            if (splash != null && splash.Active)
                            {
                                if (!Monitor.Wait(gate, PresenterTimings.SplashWait(target is DxgiTarget))) { redraw = true; break; }
                            }
                            // The loading screen's animation, 30 times a second while a save
                            // loads; or a timed wait for its watchdog.
                            else if (LoadingWait() is int loadingWait && loadingWait >= 0)
                            {
                                if (loadingWait == 0)
                                {
                                    // Windows' default timer resolution (15.6 ms) would make a
                                    // 33 ms wait 47 ms; raised as for the repeats, until the next frame.
                                    if (OperatingSystem.IsWindows() && !timerRaised) timerRaised = Dxgi.timeBeginPeriod(1) == 0;
                                    if (!Monitor.Wait(gate, loading.WaitMs)) { redraw = true; break; }
                                }
                                else Monitor.Wait(gate, loadingWait);
                            }
                            // DXGI: the last frame again once no frame has come for a while
                            // (StallRepeats). The wait is the one there was, timed.
                            else if (MayRepeat)
                            {
                                int ms = stall.WaitMilliseconds(GlTimings.Now);
                                if (ms == 0) { repeat = true; break; }
                                Monitor.Wait(gate, ms);
                            }
                            else Monitor.Wait(gate);
                        }
                        if (queue.Count > 0)
                        {
                            frame = queue.Dequeue();
                            redraw = repeat = false;
                        }
                        else if (stopping) break;
                        else if (repeat) repeating = shown;
                        // Otherwise the splash is due, or the window's contents were damaged:
                        // the last frame is shown again at once.
                        else redraw = true;
                        if (!repeat) refreshWanted = false;
                        // A redraw counts as queued while it reads the frame last shown: every
                        // frame handed over has been presented, so the GL thread draws into
                        // another buffer, and it can hand over only one more meanwhile. A
                        // repeat does not: a frame handed over during one goes through at once
                        // (Present keeps the GL thread off the buffer the repeat reads).
                        if (redraw) queued++;
                    }
                    if (repeat)
                    {
                        Repeat();
                        lock (gate)
                        {
                            repeating = null;
                            Monitor.PulseAll(gate);
                        }
                        continue;
                    }
                    long presenterBusy = 0;
                    if (redraw) Redraw();
                    else
                    {
                        PresenterTimings.FrameStarted = frame.Started;
                        if (Presented.Drain() && target is DxgiTarget dxgiTarget) dxgiTarget.HoldBack();
                        long t = GlTimings.Now;
                        PresentBuffer(frame.Buffer, frame.Fence, frame.Interval, frame.Width, frame.Height);
                        // Up to issuing the picture: the swap's own wait for vsync (inside
                        // SwapBuffers on OpenGL, after Present on DXGI) is not the frame's need.
                        presenterBusy = showStarted - t;
                        PresenterTimings.FrameStarted = 0;
                        FramePresented();
                    }
                    lock (gate)
                    {
                        queued--;
                        Monitor.PulseAll(gate);
                    }
                    if (!redraw) Presented.Done(frame.Frame, frame.Need > 0 ? frame.Need + presenterBusy : 0);
                }
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    failure = e;
                    queue.Clear();
                    queued = 0;
                    Monitor.PulseAll(gate);
                }
            }
            finally
            {
                if (timerRaised) _ = Dxgi.timeEndPeriod(1);
                Presented.Stop();
                target?.Dispose();
                target = null;
                Glfw.PInvoke.MakeContextCurrent(null!);
            }
        }

        /// DXGI's target when it is on (Windows) and starts, else OpenGL's; one log line says
        /// which, and why not DXGI.
        IWindowTarget StartTarget()
        {
            if (dxgiWindow != IntPtr.Zero)
            {
                var dxgi = DxgiTarget.Create(dxgiWindow, windowWidth, windowHeight, out string reason, out bool windowTouched);
                if (dxgi != null)
                {
                    DxgiAdapterLuid = dxgi.AdapterLuid;
                    return dxgi;
                }
                // A failure after the swap chain was made (its last steps) is one while running:
                // DXGI is restarted, as SwapBuffers may not show on the window from here on.
                dxgiLost = windowTouched;
                EngineLog.Engine($"present: OpenGL's SwapBuffers, since DXGI could not start: {reason}{MadeUp}"
                    + (windowTouched ? "; the window had DXGI's swap chain by then, so DXGI is restarted" : ""));
            }
            else if (OperatingSystem.IsWindows()) EngineLog.Engine("present: OpenGL's SwapBuffers (DXGI is off)");
            return SwapTarget();
        }

        /// OpenGL's own presentation on the window: through GLFW when the window has the
        /// presenter's context, else with that context put on it (Win32GlWindow), once DXGI is
        /// given up (it could not start, or no restart is left). While restarts are pending it
        /// stays on its hidden window: with NVIDIA's "Prefer layered on DXGI swapchain", SwapBuffers
        /// on the window gave it the driver's swap chain, and every restart after it was refused
        /// with E_ACCESSDENIED (measured); without it, SwapBuffers does not reach a window whose
        /// DXGI swap chain has presented anyway (measured).
        GlSwapTarget SwapTarget() => contextFree ? new GlSwapTarget(windowHandle, onWindow: !dxgiLost || dxgiRestarts.Exhausted) : new GlSwapTarget(window);

        /// DXGI's restarts after it failed while running (DxgiRestarts: bursts of at most ten
        /// tries, 1 to 8 s apart).
        readonly DxgiRestarts dxgiRestarts = new();
        bool dxgiLost;

        /// The target failed while running (DXGI: the device was lost, or a call failed). DXGI is
        /// started anew (a new device and swap chain), as a lost Direct3D device is recovered:
        /// OpenGL's SwapBuffers does not reach the screen on a window whose flip-model swap chain
        /// has presented (measured: the window kept showing DXGI's last picture). Until a restart
        /// succeeds, and after the last, OpenGL's SwapBuffers stands in.
        void FallBack()
        {
            EngineLog.Engine($"present: {target.Name} failed: {target.Failure}{MadeUp}");
            bool wasDxgi = target is DxgiTarget;
            target.Dispose();
            target = null;
            if (wasDxgi)
            {
                dxgiLost = true;
                DxgiAdapterLuid = 0;
                target = RestartDxgi();
            }
            target ??= SwapTarget();
        }

        /// DXGI started anew after it failed, when DxgiRestarts allows a try; null when it does
        /// not start (or may not be tried yet).
        IWindowTarget RestartDxgi()
        {
            long now = GlTimings.Now;
            if (!dxgiRestarts.MayTry(now)) return null;
            dxgiRestarts.Tried(now);
            int tries = dxgiRestarts.Tries;
            var dxgi = DxgiTarget.Create(dxgiWindow, windowWidth, windowHeight, out string reason, out _);
            if (dxgi != null)
            {
                EngineLog.Engine($"present: DXGI started again (restart {tries})");
                dxgiRestarts.Started(GlTimings.Now);
                dxgiLost = false;
                DxgiAdapterLuid = dxgi.AdapterLuid;
                return dxgi;
            }
            EngineLog.Engine(!dxgiRestarts.Exhausted
                ? $"present: DXGI restart {tries} failed: {reason}{MadeUp}; {(contextFree ? "the window keeps its last picture" : "OpenGL's SwapBuffers, which Windows may not show,")} until the next in {DxgiRestarts.DelaySeconds(tries):F0} s"
                : $"present: DXGI restart {tries} failed: {reason}{MadeUp}; no more tries: OpenGL's SwapBuffers from here on, which Windows may not show");
            return null;
        }

        static string MadeUp => DxgiSwapChain.Fault != null ? $" (a made-up fault: --dxgi-fault {DxgiSwapChain.Fault})" : "";

        /// Binds buffer `b`'s colour for reading, through this thread's framebuffer for it, made
        /// (or attached anew) when missing or out of date.
        void BindForReading(Buffer b)
        {
            if (b.PresentGeneration != b.Generation || b.PresentFramebuffer == 0)
            {
                if (b.PresentFramebuffer == 0) b.PresentFramebuffer = GlApi.GenFramebuffer();
                GlApi.BindFramebuffer(ReadFramebuffer, b.PresentFramebuffer);
                N.glFramebufferTexture2D(ReadFramebuffer, ColorAttachment0, TexTarget2D, b.Color, 0);
                b.PresentGeneration = b.Generation;
            }
            else GlApi.BindFramebuffer(ReadFramebuffer, b.PresentFramebuffer);
        }

        // The context the presenter's objects were made in (Windows).
        IntPtr presentingContext;

        /// Windows: when the window target changed the current context (OpenGL's fallback on a
        /// context-free window has a double-buffered context of its own, Win32GlWindow), the
        /// objects contexts do not share are made anew in it: the framebuffers that read the
        /// frames, the probe's and the splash's vertex array. Textures, buffers and programs are
        /// shared.
        void NoteContext()
        {
            IntPtr now = Win32GlWindow.CurrentContext;
            if (now == presentingContext) return;
            bool changed = presentingContext != IntPtr.Zero;
            presentingContext = now;
            if (!changed) return;
            foreach (var b in buffers)
            {
                b.PresentFramebuffer = 0;
                b.PresentGeneration = -1;
            }
            probeFramebuffer = 0;
            splash?.ContextReplaced();
            loading.ContextReplaced();
        }

        /// Makes the target ready for a `width` x `height` picture, its framebuffer bound for
        /// drawing; falls back to OpenGL's when it fails. Returns whether rows are flipped.
        bool BeginTarget(int width, int height)
        {
            long t = GlTimings.Now;
            // The stand-in is released before DXGI starts again (its context goes back to its
            // hidden window), and made anew when the restart fails.
            if (dxgiLost && dxgiRestarts.MayTry(t))
            {
                target.Dispose();
                target = null;
                target = RestartDxgi() ?? SwapTarget();
            }
            // Ends: restarts are at least a second apart, and OpenGL's Begin never fails.
            while (!target.Begin(width, height)) FallBack();
            if (OperatingSystem.IsWindows()) NoteContext();
            PresenterTimings.Add(ref PresenterTimings.Begin, GlTimings.Now - t);
            return target.FlipY;
        }

        /// Shows the picture; when the target fails, falls back to OpenGL's and shows the last
        /// frame again through it.
        void ShowTarget(int interval, bool again)
        {
            long t = GlTimings.Now;
            bool shownOk = target.Show(interval, again);
            long wait = target.LastWait;
            PresenterTimings.Add(ref PresenterTimings.Show, GlTimings.Now - t - wait);
            PresenterTimings.Add(ref PresenterTimings.Wait, wait);
            if (shownOk)
            {
                PresenterTimings.Shown(wait);
                if (target is DxgiTarget) dxgiRestarts.Presented(GlTimings.Now);
                return;
            }
            FallBack();
            Redraw();
        }

        void PresentBuffer(Buffer b, GlSync fence, int interval, int width, int height)
        {
            // The frame's commands complete before the copy reads them. The wait is on this
            // thread rather than on the window context's GPU queue, so neither context's queue
            // ever waits on the other's.
            long t = GlTimings.Now;
            var wait = new OniMoono.Engine.LongWait();
            if (OniMoono.Engine.LongWaits.StallFence)
                while (true)
                {
                    System.Threading.Thread.Sleep(100);
                    wait.Slice("presenter: the frame's fence", $"sync 0x{System.Runtime.CompilerServices.Unsafe.As<GlSync, nint>(ref fence):x}, frame {b.DrawnFrame}, stalled by ONI_TEST_STALL_FENCE");
                }
            while (N.glClientWaitSync(fence, default, WaitSliceNanoseconds) == TimeoutExpired)
                wait.Slice("presenter: the frame's fence", $"sync 0x{System.Runtime.CompilerServices.Unsafe.As<GlSync, nint>(ref fence):x}, frame {b.DrawnFrame}");
            wait.End();
            N.glDeleteSync(fence);
            PresenterTimings.Add(ref PresenterTimings.FrameWait, GlTimings.Now - t);
            lastInterval = interval;
            bool flip = BeginTarget(width, height);
            long copyStart = GlTimings.Now;
            BindForReading(b);
            // A frame drawn before the window changed size fills the window scaled, rather than
            // leaving part of it undefined. A target whose rows run top-down (DXGI) gets the frame
            // flipped by the same copy.
            bool scaled = b.Width != width || b.Height != height;
            N.glBlitFramebuffer(0, 0, b.Width, b.Height, 0, flip ? height : 0, width, flip ? 0 : height, ColorBufferBit, scaled ? Linear : Nearest);
            // The GL thread draws into the buffer again only after the copy has read it. A frame
            // shown again replaces the fence of its earlier copy (the GL thread waits for a
            // buffer's fence only once it draws into it again, which it does not while the
            // buffer is queued).
            if (b.HasCopyDone) N.glDeleteSync(b.CopyDone);
            b.CopyDone = N.glFenceSync(SyncGpuCommandsComplete, default);
            b.HasCopyDone = true;
            shown = b;
            if (splash != null && splash.Active)
            {
                splash.GameFrame();
                splash.Draw(width, height, flip);
            }
            // Nothing is drawn over a game frame; one after the loading screen's ends its animation.
            loading.GameFrame(GlLoadAnimation.Wanted(b.DrawnFrame, LoadingScreen.ArmedFrame, LoadingScreen.OverlayFrame, 0));
            N.glFlush();
            PresenterTimings.Add(ref PresenterTimings.Copy, GlTimings.Now - copyStart);
            if (PresentTrace.Enabled) Probe(b, width, height);
            showStarted = GlTimings.Now;
            ShowTarget(interval, again: false);
        }

        // ----- Repeats while no frame comes (StallRepeats; presenter thread) -----

        readonly StallRepeats stall = new();
        bool timerRaised;

        /// Repeats are on, through DXGI, with a frame shown and the splash gone.
        bool MayRepeat => DxgiSwapChain.Repeats && target is DxgiTarget && !dxgiLost && shown != null;

        /// A game frame was presented: repeats start over; the timer resolution raised for them
        /// goes back once the frame is out.
        void FramePresented()
        {
            long now = GlTimings.Now;
            if (stall.Run > 0 && PresentTrace.Enabled)
                PresentTrace.Log($"frame {shown?.DrawnFrame ?? -1} presented again {stall.Run} times over {(now - stall.RunStarted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms while no frame came (every {stall.Cadence * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms)");
            stall.Frame(now);
            if (timerRaised)
            {
                timerRaised = false;
                _ = Dxgi.timeEndPeriod(1);
            }
        }

        /// Presents the frame last shown again (DxgiTarget.ShowRepeat), scaled to the window, once
        /// the display has taken the last picture; else it waits for the next due time. The
        /// waits between repeats need a 1 ms timer resolution (Windows' default is 15.6 ms, which
        /// would make their spacing uneven); it is raised for the run of repeats only.
        void Repeat()
        {
            if (!timerRaised) timerRaised = Dxgi.timeBeginPeriod(1) == 0;
            if (!((DxgiTarget)target).MayRepeat())
            {
                stall.Skipped(GlTimings.Now);
                return;
            }
            int width = windowWidth, height = windowHeight;
            bool flip = BeginTarget(width, height);
            BindForReading(shown);
            bool scaled = shown.Width != width || shown.Height != height;
            N.glBlitFramebuffer(0, 0, shown.Width, shown.Height, 0, flip ? height : 0, width, flip ? 0 : height, ColorBufferBit, scaled ? Linear : Nearest);
            // The GL thread draws into the buffer again only after this copy too has read it.
            if (shown.HasCopyDone) N.glDeleteSync(shown.CopyDone);
            shown.CopyDone = N.glFenceSync(SyncGpuCommandsComplete, default);
            shown.HasCopyDone = true;
            N.glFlush();
            bool ok = target is DxgiTarget d ? d.ShowRepeat() : target.Show(lastInterval, again: true);
            if (!ok)
            {
                FallBack();
                Redraw();
                return;
            }
            stall.Repeated(GlTimings.Now);
            PresenterTimings.Repeated();
        }

        /// When the last frame's picture began to be issued (QueuedFrames' need).
        long showStarted;

        /// The swap interval of the last frame presented, for redraws between frames.
        int lastInterval = 1;

        /// Copies the frame last shown to the window again (black before the first), scaled to
        /// the window's size now, with the splash over it while it runs, and swaps.
        void Redraw()
        {
            int width = windowWidth, height = windowHeight;
            if (PresentTrace.Enabled && !(splash != null && splash.Active) && !loading.Running) PresentTrace.Log($"window contents damaged: frame {shown?.DrawnFrame ?? -1} shown again");
            bool flip = BeginTarget(width, height);
            if (shown != null)
            {
                BindForReading(shown);
                bool scaled = shown.Width != width || shown.Height != height;
                N.glBlitFramebuffer(0, 0, shown.Width, shown.Height, 0, flip ? height : 0, width, flip ? 0 : height, ColorBufferBit, scaled ? Linear : Nearest);
            }
            else
            {
                N.glClearColor(0f, 0f, 0f, 1f);
                N.glClear(ColorBufferBit);
            }
            if (splash != null && splash.Active) splash.Draw(width, height, flip);
            else if (shown != null) loading.Draw(width, height, flip, target.Framebuffer);
            N.glFlush();
            ShowTarget(lastInterval, again: true);
            loading.Shown();
        }

        // ----- Diagnostics (presenter thread) -----

        uint probeTexture, probeFramebuffer;
        const int ProbeSize = 16;
        readonly byte[] probePixels = new byte[ProbeSize * ProbeSize * 4];
        bool lastDark;
        long lastSwap;
        int presented;

        /// Mean luminance of what is about to be swapped to the screen, from a 16x16 reduction of
        /// the window's back buffer; logs frames that turn near black, and long gaps between
        /// swaps.
        void Probe(Buffer b, int width, int height)
        {
            if (probeFramebuffer == 0)
            {
                if (probeTexture == 0)
                {
                    probeTexture = GlApi.GenTexture();
                    GlApi.BindTexture(TexTarget2D, probeTexture);
                    N.glTexImage2D(TexTarget2D, 0, (int)Rgba8, ProbeSize, ProbeSize, 0, Rgba, UnsignedByte, null);
                }
                probeFramebuffer = GlApi.GenFramebuffer();
                GlApi.BindFramebuffer(DrawFramebuffer, probeFramebuffer);
                N.glFramebufferTexture2D(DrawFramebuffer, ColorAttachment0, TexTarget2D, probeTexture, 0);
            }
            GlApi.BindFramebuffer(ReadFramebuffer, target.Framebuffer);
            GlApi.BindFramebuffer(DrawFramebuffer, probeFramebuffer);
            N.glBlitFramebuffer(0, 0, width, height, 0, 0, ProbeSize, ProbeSize, ColorBufferBit, Linear);
            GlApi.BindFramebuffer(ReadFramebuffer, probeFramebuffer);
            N.glPixelStorei(PackAlignment, 4);
            fixed (byte* p = probePixels) N.glReadPixels(0, 0, ProbeSize, ProbeSize, Rgba, UnsignedByte, p);
            GlApi.BindFramebuffer(DrawFramebuffer, target.Framebuffer);
            double sum = 0;
            for (int i = 0; i < probePixels.Length; i += 4) sum += 0.299 * probePixels[i] + 0.587 * probePixels[i + 1] + 0.114 * probePixels[i + 2];
            double mean = sum / (ProbeSize * ProbeSize);
            bool dark = mean < 3;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double gap = lastSwap == 0 ? 0 : (now - lastSwap) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lastSwap = now;
            presented++;
            // Gaps well above the usual frame time (a main-thread stall such as a save).
            if (gap > 200 && gap > 4 * typicalGap) PresentTrace.Log($"swap {presented} (frame {b.DrawnFrame}): {gap:F0} ms since the last swap");
            if (dark != lastDark) PresentTrace.Log($"swap {presented} (back buffer {b.Index}, {b.Width}x{b.Height}, frame {b.DrawnFrame}): {(dark ? "near black" : "visible again")} (mean luminance {mean:F1})");
            lastDark = dark;
            if (gap > 0) typicalGap = typicalGap == 0 ? gap : typicalGap * 0.95 + Math.Min(gap, 4 * typicalGap) * 0.05;
        }

        double typicalGap;

        /// Presents what was handed over, then stops the thread and releases the window's
        /// context.
        public void Dispose()
        {
            lock (gate)
            {
                stopping = true;
                Monitor.PulseAll(gate);
            }
            thread.Join();
        }
    }
}
