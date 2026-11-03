using System;
using System.Collections.Generic;
using System.Threading;
using Brutal.GlfwApi;
using Brutal.GlfwApi.Win32;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Win32;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;

namespace OniMoono.Desktop.Vulkan
{
    /// One offscreen back buffer: colour and depth-stencil images the device thread renders a
    /// frame into, with what was drawn into it (as GlPresenter's buffers).
    internal sealed class VkBackBuffer
    {
        public VkImageResource Color, Depth;
        public int Width, Height, Index, DrawnFrame = -1;
        public bool DrawnThisFrame, ClearedLast, DrawnSinceAllocation;
    }

    /// Presentation on a thread of its own, as GlPresenter (ARCHITECTURE §3.5): the device thread
    /// renders each frame into one of three offscreen back buffers, submits it, and hands the
    /// buffer over; this thread acquires a swapchain image, copies the frame into it (both are
    /// stored top row first), draws the loading splash over it while that runs, and presents. Up to
    /// two frames are queued for presentation, one with vsync; the swapchain uses FIFO with vsync
    /// and IMMEDIATE (else MAILBOX) without. The swapchain is made again when the window's size or
    /// the present mode changes or the surface reports it out of date; a minimized window (a 0x0
    /// surface) is not presented to, while frames go on rendering at the size the window had (issue
    /// #29); with vsync its pictures are paced at 60 Hz as if presented, as the original's are (and
    /// OpenGL's and DXGI's, measured), rather than running as fast as the frames come.
    ///
    /// Ordering needs no semaphores between the two threads: both submit to the one queue, the
    /// frame before the copy that reads it, and each image's barriers wait for the work that
    /// earlier submissions did on it. The copy leaves the back buffer as the device thread
    /// left it (a colour attachment), so its layout tracking stays right.
    internal sealed unsafe class VkPresenter : IDisposable
    {
        const int MaxQueued = 2, MaxQueuedWithVsync = 1, Slots = 2;

        readonly VkContext vk;
        readonly GlfwWindow window;
        readonly VkSurfaceKHR surface;
        readonly VkSplash splash;
        readonly VkBackBuffer[] buffers = [new() { Index = 0 }, new() { Index = 1 }, new() { Index = 2 }];
        readonly VkFormat depthFormat;
        int current;
        VkBackBuffer handedOver, shown;

        // Swapchain (presenter thread after construction).
        VkSwapchainKHR swapchain;
        VkImage[] images = Array.Empty<VkImage>();
        VkImageView[] views = Array.Empty<VkImageView>();
        VkSemaphore[] renderDone = Array.Empty<VkSemaphore>();
        VkImageLayout[] imageLayouts = Array.Empty<VkImageLayout>();
        public VkFormat SwapchainFormat { get; private set; }
        VkExtent2D extent;
        VkPresentModeKHR presentMode;
        int swapInterval = -1;
        // When the next picture of a minimized window is due, with vsync (PaceMinimized).
        long minimizedDue;
        bool outOfDate;
        readonly VkPresentModeKHR[] presentModes = Array.Empty<VkPresentModeKHR>();
        /// Swapchain creations that failed in a row (made again after a growing delay, 0.1 s
        /// doubling to 2 s, and given up after five tries and ten seconds).
        int recreateFailures;
        const int RecreateAttempts = 5;
        bool disposed, ownsSplash;

        /// Windows: DXGI's swap chain in place of Vulkan's (VkDxgiTarget; null: the
        /// VkSwapchainKHR), the window's handle, and its restarts after a failure while running:
        /// Vulkan's swapchain stands in until one succeeds (DxgiRestarts: bursts of at most ten
        /// tries, 1 to 8 s apart). It does reach the screen after DXGI's (measured), unlike
        /// OpenGL's SwapBuffers; DXGI is restarted all the same, for how Windows treats the picture.
        VkDxgiTarget dxgi;
        readonly IntPtr dxgiWindow;
        bool dxgiLost;
        readonly DxgiRestarts dxgiRestarts = new();
        Action waitIdle;
        static string MadeUp => DxgiSwapChain.Fault != null ? $" (a made-up fault: --dxgi-fault {DxgiSwapChain.Fault})" : "";

        // Per presenter frame in flight.
        readonly VkCommandPool pool;
        readonly CommandBuffer[] commands = new CommandBuffer[Slots];
        readonly VkFence[] fences = new VkFence[Slots];
        readonly bool[] submitted = new bool[Slots];
        readonly VkSemaphore[] acquired = new VkSemaphore[Slots];
        int slot;

        readonly Thread thread;
        readonly object gate = new();
        readonly Queue<(VkBackBuffer Buffer, int Interval, int Width, int Height, long Started)> queue = new();
        int queued;
        bool stopping, refreshWanted;
        volatile Exception failure;

        /// Why presenting stopped for good (the presenter thread ended), or null. The device
        /// treats it as a lost device: the game quits with the error.
        public Exception Failure => failure;
        volatile int windowWidth, windowHeight;

        /// The main thread's frame the device thread is executing (diagnostics).
        public int Frame;
        /// When the game frame being handed over began (device thread; PresenterTimings).
        public long FrameStarted;

        /// Makes the surface's first swapchain at once (on the calling thread), so that a
        /// window Vulkan cannot present to falls back to OpenGL before the game starts.
        public VkPresenter(VkContext vk, GlfwWindow window, VkSurfaceKHR surface, VkSplash splash, int width, int height, VkFormat depthFormat)
        {
            this.vk = vk;
            this.window = window;
            this.surface = surface;
            this.splash = splash;
            this.depthFormat = depthFormat;
            windowWidth = Math.Max(1, width);
            windowHeight = Math.Max(1, height);
            try
            {
                if (!vk.Physical.GetSurfaceSupportKHR(vk.QueueFamily, surface)) throw new VulkanUnavailableException("the graphics queue cannot present to the window");
                int modes = vk.Physical.GetSurfacePresentModesKHR_Count(surface);
                presentModes = new VkPresentModeKHR[modes];
                vk.Physical.GetSurfacePresentModesKHR_Write(surface, presentModes);
                pool = vk.Device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = vk.QueueFamily, Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit }, null);
                for (int i = 0; i < Slots; i++)
                {
                    commands[i] = Brutal.VulkanApi.Abstractions.DeviceExtensions.AllocateCommandBuffer(vk.Device, new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary });
                    fences[i] = vk.Device.CreateFence(new VkFenceCreateInfo(), null);
                    acquired[i] = vk.Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
                }
                // Windows: DXGI's swap chain in place of Vulkan's (VkDxgiTarget), when it is on and
                // starts; one log line says which, and why not DXGI.
                if (OperatingSystem.IsWindows() && DxgiSwapChain.Enabled)
                {
                    dxgiWindow = (IntPtr)window.GetWin32Window();
                    dxgi = VkDxgiTarget.Create(vk, dxgiWindow, windowWidth, windowHeight, out string reason, out bool windowTouched);
                    if (dxgi == null)
                    {
                        // A failure after the swap chain was made (its last steps) is one while
                        // running: Vulkan's swapchain stands in and DXGI is restarted.
                        dxgiLost = windowTouched;
                        EngineLog.Engine($"present: Vulkan's swapchain, since DXGI could not start: {reason}{MadeUp}"
                            + (windowTouched ? "; the window had DXGI's swap chain by then, so DXGI is restarted" : ""));
                    }
                    else SwapchainFormat = VkDxgiTarget.Format;
                }
                else if (OperatingSystem.IsWindows()) EngineLog.Engine("present: Vulkan's swapchain (DXGI is off)");
                if (dxgi == null)
                {
                    Recreate(windowWidth, windowHeight, 1);
                    if (swapchain.IsNull()) throw new VulkanUnavailableException("no swapchain could be made for the window");
                }
                VkContext.FailAt("presenter-create");
            }
            catch
            {
                // The splash stays the caller's until the presenter is made (VkDevice releases it).
                ownsSplash = false;
                Dispose();
                throw;
            }
            ownsSplash = true;
            thread = new Thread(Run) { Name = "Presenter", IsBackground = true };
            thread.Start();
        }

        // ----- Device thread -----

        /// The back buffer this frame draws into, at the rendering size (kept at the size a frame
        /// began drawing at; see GlPresenter.BackBuffer).
        public VkBackBuffer BackBuffer(int width, int height, VkFrames frames)
        {
            var b = buffers[current];
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (b.Color == null || (b.Width != width || b.Height != height) && !b.DrawnThisFrame) Allocate(b, width, height, frames);
            else if (PresentTrace.Enabled && (b.Width != width || b.Height != height))
                PresentTrace.Log($"frame {Frame}: back buffer {b.Index} kept at {b.Width}x{b.Height} for the rest of the frame (the window is {width}x{height})");
            return b;
        }

        /// The back buffer to read from (ReadPixels of the screen): this frame's, at the size it
        /// was drawn.
        public VkBackBuffer ReadBuffer(int width, int height, VkFrames frames)
        {
            var b = buffers[current];
            return b.Color != null && b.DrawnThisFrame ? b : BackBuffer(width, height, frames);
        }

        public void Drew(bool clear)
        {
            var b = buffers[current];
            b.DrawnThisFrame = true;
            b.DrawnSinceAllocation = true;
            b.ClearedLast = clear;
        }

        void Allocate(VkBackBuffer b, int width, int height, VkFrames frames)
        {
            if (PresentTrace.Enabled)
                PresentTrace.Log($"frame {Frame}: back buffer {b.Index} allocated {width}x{height} (was {b.Width}x{b.Height}){(b.DrawnThisFrame ? ", discarding what the frame drew into it" : "")}");
            var (oldColor, oldDepth) = (b.Color, b.Depth);
            if (oldColor != null) frames.Retire(() => { oldColor.Destroy(vk); oldDepth.Destroy(vk); });
            b.Color = VkImageResource.Create(vk, VkFormat.R8G8B8A8UNorm, VkImageViewType._2D, width, height, 1, 1, 1,
                VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit, name: "back buffer");
            b.Depth = VkImageResource.Create(vk, depthFormat, VkImageViewType._2D, width, height, 1, 1, 1,
                VkImageUsageFlags.DepthStencilAttachmentBit, name: "back buffer depth");
            b.Width = width;
            b.Height = height;
            b.DrawnSinceAllocation = false;
        }

        /// Hands the frame drawn into the current back buffer to the presenter (device thread,
        /// after the frame's commands are submitted), as GlPresenter.Present: a frame that drew
        /// nothing shows the last frame again.
        public void Present(int interval, int width, int height)
        {
            // The window's size, which a frame of another size is stretched over (RecordCopy).
            windowWidth = Math.Max(1, width);
            windowHeight = Math.Max(1, height);
            var b = buffers[current];
            var choice = GlPresenter.Choose(b.Color != null, b.DrawnSinceAllocation, b.DrawnThisFrame, handedOver != null);
            bool complete = choice == GlPresenter.PresentChoice.Frame;
            if (!complete)
            {
                b.DrawnThisFrame = false;
                if (choice == GlPresenter.PresentChoice.Nothing) return;
                b = handedOver;
            }
            else
            {
                b.DrawnFrame = Frame;
                b.DrawnThisFrame = false;
            }
            lock (gate)
            {
                if (queued >= (interval > 0 ? MaxQueuedWithVsync : MaxQueued) && OniMoono.Engine.PlayerLoop.FrameStats)
                {
                    long waiting = GlTimings.Now;
                    while (queued >= (interval > 0 ? MaxQueuedWithVsync : MaxQueued) && failure == null) Monitor.Wait(gate);
                    Interlocked.Add(ref OniMoono.Engine.FrameStatsWindow.PresenterQueueWait, GlTimings.Now - waiting);
                }
                while (queued >= (interval > 0 ? MaxQueuedWithVsync : MaxQueued) && failure == null) Monitor.Wait(gate);
                if (failure != null) return; // VkDevice reports it (Failure)
                queue.Enqueue((b, interval, Math.Max(1, width), Math.Max(1, height), complete ? FrameStarted : 0));
                queued++;
                Monitor.PulseAll(gate);
            }
            if (!complete) return;
            handedOver = b;
            current = (current + 1) % buffers.Length;
        }

        // ----- Main thread -----

        /// The window's contents were damaged: the last frame is shown again (GlPresenter.Refresh).
        public void Refresh()
        {
            lock (gate)
            {
                refreshWanted = true;
                Monitor.PulseAll(gate);
            }
        }

        public void WaitIdle()
        {
            lock (gate)
                while (queued > 0 && failure == null) Monitor.Wait(gate);
        }

        // ----- Presenter thread -----

        void Run()
        {
            try
            {
                while (true)
                {
                    (VkBackBuffer Buffer, int Interval, int Width, int Height, long Started) frame = default;
                    bool redraw = false;
                    lock (gate)
                    {
                        while (queue.Count == 0 && !stopping && !(refreshWanted && shown != null))
                        {
                            if (splash != null && splash.Active)
                            {
                                if (!Monitor.Wait(gate, PresenterTimings.SplashWait(dxgi != null))) { redraw = true; break; }
                            }
                            else Monitor.Wait(gate);
                        }
                        if (queue.Count > 0)
                        {
                            frame = queue.Dequeue();
                            redraw = false;
                        }
                        else if (stopping) break;
                        else redraw = true;
                        refreshWanted = false;
                        if (redraw) queued++;
                    }
                    if (redraw) Show(shown, swapInterval < 0 ? 1 : swapInterval, windowWidth, windowHeight, gameFrame: false);
                    else
                    {
                        // vSyncCount 2-4: FIFO shows an image per vertical blank, so the frame
                        // is presented that many times (OpenGL's swap interval does the same).
                        PresenterTimings.FrameStarted = frame.Started;
                        Show(frame.Buffer, frame.Interval, frame.Width, frame.Height, gameFrame: true);
                        PresenterTimings.FrameStarted = 0;
                        // (DXGI's Present takes the interval itself.)
                        for (int again = 1; again < frame.Interval && dxgi == null; again++) Show(frame.Buffer, frame.Interval, frame.Width, frame.Height, gameFrame: false);
                    }
                    lock (gate)
                    {
                        queued--;
                        Monitor.PulseAll(gate);
                    }
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
        }

        /// Copies a back buffer (null: black) into the window's next picture, the splash over it,
        /// and presents: through DXGI (ShowDxgi) or Vulkan's swapchain (ShowSwapchain).
        void Show(VkBackBuffer b, int interval, int width, int height, bool gameFrame)
        {
            if (gameFrame) shown = b;
            if (gameFrame && splash != null && splash.Active) splash.GameFrame();
            long t = GlTimings.Now;
            lastWait = 0;
            if (OperatingSystem.IsWindows() && dxgiLost && RestartDxgi() is { } restarted) dxgi = restarted;
            if (OperatingSystem.IsWindows() && dxgi != null) ShowDxgi(b, interval, width, height);
            else ShowSwapchain(b, interval, width, height);
            PresenterTimings.Add(ref PresenterTimings.Show, GlTimings.Now - t - lastWait);
            PresenterTimings.Add(ref PresenterTimings.Wait, lastWait);
            PresenterTimings.Shown(lastWait);
        }

        /// Time the last Show waited for its picture to be on its way (Stopwatch ticks).
        long lastWait;

        /// The command buffer of the next presenter slot, once its last submission is done.
        CommandBuffer BeginSlot(out int s)
        {
            s = slot;
            slot = (slot + 1) % Slots;
            if (submitted[s])
            {
                var fence = fences[s];
                VkCheck.Ok(vk.Device.PInvoke.vkWaitForFences.Invoke(vk.Device.Handle, 1, &fence, true, -1), "vkWaitForFences (presenter)");
                vk.Device.PInvoke.vkResetFences.Invoke(vk.Device.Handle, 1, &fence);
                submitted[s] = false;
            }
            return commands[s];
        }

        /// Records the copy of `b` (null: black) into `target` (in TransferDstOptimal), scaled to
        /// `width` x `height` when its size differs. Both are stored top row first
        /// (RenderConventions).
        void RecordCopy(CommandBuffer cb, VkBackBuffer b, VkImage target, int width, int height)
        {
            if (b != null)
            {
                var color = b.Color;
                // The frame is complete: it was submitted before it was handed over.
                color.ForTransferRead(cb);
                bool scaled = b.Width != width || b.Height != height;
                var blit = new VkImageBlit
                {
                    SrcSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    DstSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                };
                blit.SrcOffsets[0] = new VkOffset3D(0, 0, 0);
                blit.SrcOffsets[1] = new VkOffset3D(b.Width, b.Height, 1);
                blit.DstOffsets[0] = new VkOffset3D(0, 0, 0);
                blit.DstOffsets[1] = new VkOffset3D(width, height, 1);
                cb.BlitImage(color.Image, VkImageLayout.TransferSrcOptimal, target, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageBlit>(&blit, 1),
                    scaled ? VkFilter.Linear : VkFilter.Nearest);
                // Back to what the device thread expects.
                color.ForAttachment(cb);
            }
            else
            {
                var black = new VkClearColorValue();
                var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
                vk.Device.PInvoke.vkCmdClearColorImage.Invoke(cb.Handle, target, VkImageLayout.TransferDstOptimal, &black, 1, &range);
            }
        }

        /// A minimized window has no swapchain to wait on: with vsync each picture not presented
        /// takes what is left of one refresh at 60 Hz (the monitor is out of reach here: GLFW's
        /// are the main thread's), counted from when the last was due so that Sleep's coarse steps
        /// even out. One refresh a call, as FIFO would wait one blank a present: a frame at
        /// vSyncCount 2 to 4 is shown that many times already. Measured minimized (U60, a paused
        /// colony, the game's vsync on Windows): the original stayed at or under 60 fps, OpenGL
        /// and DXGI at 60, and this path at 69 to 75 before this.
        void PaceMinimized(int interval)
        {
            long period = MinimizedPeriod(interval);
            if (period == 0) return;
            long wait = MinimizedWait(ref minimizedDue, GlTimings.Now, period);
            if (wait > 0) Thread.Sleep((int)(wait * 1000 / System.Diagnostics.Stopwatch.Frequency));
        }

        /// The time a minimized window's picture takes (Stopwatch ticks): one refresh at 60 Hz
        /// with vsync, whatever the interval (each frame comes that many times); none without.
        internal static long MinimizedPeriod(int interval) => interval > 0 ? System.Diagnostics.Stopwatch.Frequency / 60 : 0;

        /// The wait before a minimized window's picture (Stopwatch ticks), moving `due` on a
        /// period; a picture more than a period late starts the count again from now.
        internal static long MinimizedWait(ref long due, long now, long period)
        {
            if (due < now - period) due = now;
            long wait = due - now;
            due += period;
            return Math.Max(0, wait);
        }

        /// Through Vulkan's swapchain.
        void ShowSwapchain(VkBackBuffer b, int interval, int width, int height)
        {
            if (interval != swapInterval || outOfDate || FailNextRecreate || OutOfMemoryRecreates > 0 || width != extent.Width || height != extent.Height || swapchain.IsNull())
            {
                if (!TryRecreate(width, height, interval)) return;
                if (swapchain.IsNull())
                {
                    PaceMinimized(interval);
                    return;
                }
            }
            // The copy fills the swapchain's images, whose size is the surface's now, which can
            // differ from the size the frame was handed over with (the window changed since).
            width = extent.Width;
            height = extent.Height;
            var cb = BeginSlot(out int s);
            int index = 0;
            var r = vk.Device.PInvoke.vkAcquireNextImageKHR.Invoke(vk.Device.Handle, swapchain, -1, acquired[s], default, &index);
            if (r == VkResult.ErrorOutOfDateKHR)
            {
                outOfDate = true;
                return;
            }
            if (r == VkResult.SuboptimalKHR) outOfDate = true;
            else VkCheck.Ok(r, "vkAcquireNextImageKHR"); // surface lost: presenting ends (Failure)

            VkCheck.Ok(vk.Device.PInvoke.vkResetCommandBuffer.Invoke(cb.Handle, 0), "vkResetCommandBuffer");
            var begin = new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
            VkCheck.Ok(vk.Device.PInvoke.vkBeginCommandBuffer.Invoke(cb.Handle, &begin), "vkBeginCommandBuffer (presenter)");
            var target = images[index];
            Barrier(cb, target, imageLayouts[index], VkImageLayout.TransferDstOptimal, VkPipelineStageFlags2.AllTransferBit, 0, VkPipelineStageFlags2.AllTransferBit, VkAccessFlags2.TransferWriteBit);
            RecordCopy(cb, b, target, width, height);
            var layout = VkImageLayout.TransferDstOptimal;
            if (splash != null && splash.Active)
            {
                Barrier(cb, target, layout, VkImageLayout.ColorAttachmentOptimal, VkPipelineStageFlags2.AllTransferBit, VkAccessFlags2.TransferWriteBit,
                    VkPipelineStageFlags2.ColorAttachmentOutputBit, VkAccessFlags2.ColorAttachmentReadBit | VkAccessFlags2.ColorAttachmentWriteBit);
                layout = VkImageLayout.ColorAttachmentOptimal;
                splash.Draw(cb, views[index], SwapchainFormat, width, height, s);
            }
            Barrier(cb, target, layout, VkImageLayout.PresentSrcKHR, VkPipelineStageFlags2.AllCommandsBit,
                VkAccessFlags2.TransferWriteBit | VkAccessFlags2.ColorAttachmentWriteBit, VkPipelineStageFlags2.None, 0);
            imageLayouts[index] = VkImageLayout.PresentSrcKHR;
            VkCheck.Ok(vk.Device.PInvoke.vkEndCommandBuffer.Invoke(cb.Handle), "vkEndCommandBuffer (presenter)");

            var wait = new VkSemaphoreSubmitInfo { Semaphore = acquired[s], StageMask = VkPipelineStageFlags2.AllTransferBit };
            var signal = new VkSemaphoreSubmitInfo { Semaphore = renderDone[index], StageMask = VkPipelineStageFlags2.AllCommandsBit };
            var buffer = new VkCommandBufferSubmitInfo { CommandBuffer = cb.Handle };
            var submit = new VkSubmitInfo2
            {
                WaitSemaphoreInfoCount = 1, WaitSemaphoreInfos = &wait,
                CommandBufferInfoCount = 1, CommandBufferInfos = &buffer,
                SignalSemaphoreInfoCount = 1, SignalSemaphoreInfos = &signal,
            };
            vk.Queue.Submit2(new Span<VkSubmitInfo2>(&submit, 1), fences[s]);
            submitted[s] = true;
            var sc = swapchain;
            var done = renderDone[index];
            var present = new VkPresentInfoKHR { WaitSemaphoreCount = 1, WaitSemaphores = &done, SwapchainCount = 1, Swapchains = &sc, ImageIndices = &index };
            try
            {
                if (vk.Queue.PresentKHR(present) == VkResult.SuboptimalKHR) outOfDate = true;
            }
            catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
            {
                outOfDate = true;
            }
            if (interval > 0)
            {
                // With vsync a frame counts as queued until its copy is done (GlPresenter).
                long t = GlTimings.Now;
                var fence = fences[s];
                VkCheck.Ok(vk.Device.PInvoke.vkWaitForFences.Invoke(vk.Device.Handle, 1, &fence, true, -1), "vkWaitForFences (presenter)");
                lastWait = GlTimings.Now - t;
            }
        }

        /// Through DXGI (VkDxgiTarget): the copy goes into the shared image, which the
        /// submission takes from and gives back to D3D11 (queue family EXTERNAL, the keyed mutex);
        /// D3D11 then copies and presents it. A failure restarts DXGI (or, until that works,
        /// Vulkan's swapchain stands in); the picture is not shown.
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        void ShowDxgi(VkBackBuffer b, int interval, int width, int height)
        {
            // The interval redraws between frames use (Run).
            swapInterval = interval;
            var cb = BeginSlot(out int s);
            if (!dxgi.Prepare(width, height, waitIdle ??= () => vk.Queue.WaitIdle()))
            {
                DxgiFailed();
                return;
            }
            width = dxgi.Width;
            height = dxgi.Height;
            VkCheck.Ok(vk.Device.PInvoke.vkResetCommandBuffer.Invoke(cb.Handle, 0), "vkResetCommandBuffer");
            var begin = new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
            VkCheck.Ok(vk.Device.PInvoke.vkBeginCommandBuffer.Invoke(cb.Handle, &begin), "vkBeginCommandBuffer (presenter)");
            var target = dxgi.Image;
            // Taken from D3D11; what it held is not kept (the copy writes the whole picture).
            ExternalBarrier(cb, target, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, acquire: true,
                VkPipelineStageFlags2.None, 0, VkPipelineStageFlags2.AllTransferBit, VkAccessFlags2.TransferWriteBit);
            RecordCopy(cb, b, target, width, height);
            var layout = VkImageLayout.TransferDstOptimal;
            if (splash != null && splash.Active)
            {
                Barrier(cb, target, layout, VkImageLayout.ColorAttachmentOptimal, VkPipelineStageFlags2.AllTransferBit, VkAccessFlags2.TransferWriteBit,
                    VkPipelineStageFlags2.ColorAttachmentOutputBit, VkAccessFlags2.ColorAttachmentReadBit | VkAccessFlags2.ColorAttachmentWriteBit);
                layout = VkImageLayout.ColorAttachmentOptimal;
                splash.Draw(cb, dxgi.View, VkDxgiTarget.Format, width, height, s);
            }
            // Given back to D3D11.
            ExternalBarrier(cb, target, layout, VkImageLayout.General, acquire: false,
                VkPipelineStageFlags2.AllCommandsBit, VkAccessFlags2.TransferWriteBit | VkAccessFlags2.ColorAttachmentWriteBit, VkPipelineStageFlags2.None, 0);
            VkCheck.Ok(vk.Device.PInvoke.vkEndCommandBuffer.Invoke(cb.Handle), "vkEndCommandBuffer (presenter)");

            VkWin32KeyedMutexAcquireReleaseInfoKHR mutex;
            VkDeviceMemory memoryCell;
            nint acquireKey, releaseKey;
            int timeout;
            dxgi.MutexInfo(&mutex, &memoryCell, &acquireKey, &releaseKey, &timeout);
            var buffer = new VkCommandBufferSubmitInfo { CommandBuffer = cb.Handle };
            var submit = new VkSubmitInfo2 { Next = &mutex, CommandBufferInfoCount = 1, CommandBufferInfos = &buffer };
            // vkQueueSubmit2's only success code is VK_SUCCESS (vk.xml); its errors throw and end
            // presenting (Failure). VK_KHR_win32_keyed_mutex does not say what happens when the
            // acquire times out. One that never completes is not known to happen; if it did, the
            // waits for the queue would freeze the game. Nothing handles it.
            vk.Queue.Submit2(new Span<VkSubmitInfo2>(&submit, 1), fences[s]);
            submitted[s] = true;
            if (!dxgi.Show(interval))
            {
                DxgiFailed();
                return;
            }
            lastWait = dxgi.LastWait;
            dxgiRestarts.Presented(GlTimings.Now);
        }

        /// DXGI failed while running: everything it held is released, and it is restarted, or
        /// Vulkan's swapchain stands in until a restart works.
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        void DxgiFailed()
        {
            EngineLog.Engine($"present: DXGI failed: {dxgi.Failure}{MadeUp}");
            try { vk.Queue.WaitIdle(); } catch (VkResultError) { }
            dxgi.Dispose();
            dxgi = null;
            dxgiLost = true;
            outOfDate = true;
            dxgi = RestartDxgi();
        }

        /// DXGI started anew after it failed, when DxgiRestarts allows a try; null when it does
        /// not start (or may not be tried yet). Vulkan's swapchain, standing in, gives the window
        /// up first (a window has one swap chain at a time).
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        VkDxgiTarget RestartDxgi()
        {
            long now = GlTimings.Now;
            if (!dxgiRestarts.MayTry(now)) return null;
            dxgiRestarts.Tried(now);
            int tries = dxgiRestarts.Tries;
            if (!swapchain.IsNull())
            {
                vk.Queue.WaitIdle();
                DestroyImages();
                vk.Device.DestroySwapchainKHR(swapchain, null);
                swapchain = default;
            }
            var restarted = VkDxgiTarget.Create(vk, dxgiWindow, windowWidth, windowHeight, out string reason, out _);
            if (restarted != null)
            {
                EngineLog.Engine($"present: DXGI started again (restart {tries})");
                dxgiRestarts.Started(GlTimings.Now);
                dxgiLost = false;
                SwapchainFormat = VkDxgiTarget.Format;
                return restarted;
            }
            EngineLog.Engine(!dxgiRestarts.Exhausted
                ? $"present: DXGI restart {tries} failed: {reason}{MadeUp}; Vulkan's swapchain until the next in {DxgiRestarts.DelaySeconds(tries):F0} s"
                : $"present: DXGI restart {tries} failed: {reason}{MadeUp}; no more tries: Vulkan's swapchain from here on");
            outOfDate = true;
            return null;
        }

        /// An ownership transfer of `image` from (`acquire`) or to D3D11 (queue family EXTERNAL),
        /// with a layout change.
        void ExternalBarrier(CommandBuffer cb, VkImage image, VkImageLayout from, VkImageLayout to, bool acquire,
            VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess, VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess)
        {
            var barrier = new VkImageMemoryBarrier2
            {
                SrcStageMask = srcStage, SrcAccessMask = srcAccess, DstStageMask = dstStage, DstAccessMask = dstAccess,
                OldLayout = from, NewLayout = to,
                SrcQueueFamilyIndex = acquire ? VK.QUEUE_FAMILY_EXTERNAL : vk.QueueFamily,
                DstQueueFamilyIndex = acquire ? vk.QueueFamily : VK.QUEUE_FAMILY_EXTERNAL,
                Image = image,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            };
            var dependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier };
            cb.PipelineBarrier2(dependency);
        }

        static void Barrier(CommandBuffer cb, VkImage image, VkImageLayout from, VkImageLayout to, VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess,
            VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess)
        {
            var barrier = new VkImageMemoryBarrier2
            {
                SrcStageMask = srcStage, SrcAccessMask = srcAccess, DstStageMask = dstStage, DstAccessMask = dstAccess,
                OldLayout = from == VkImageLayout.PresentSrcKHR ? VkImageLayout.Undefined : from, NewLayout = to,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = image,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            };
            var dependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier };
            cb.PipelineBarrier2(dependency);
        }

        VkPresentModeKHR ModeFor(int interval)
        {
            if (interval > 0) return VkPresentModeKHR.FifoKHR;
            if (Array.IndexOf(presentModes, VkPresentModeKHR.ImmediateKHR) >= 0) return VkPresentModeKHR.ImmediateKHR;
            if (Array.IndexOf(presentModes, VkPresentModeKHR.MailboxKHR) >= 0) return VkPresentModeKHR.MailboxKHR;
            return VkPresentModeKHR.FifoKHR;
        }

        /// Recreate on the presenter thread: a creation that fails (the driver out of memory
        /// during a resize would be one; none has been seen) leaves no swapchain and is tried again
        /// after a delay (0.1 s, doubling to 2 s; frames meanwhile are not shown); after
        /// RecreateAttempts failures in a row spanning RecreateGiveUp seconds, or for a failure that
        /// cannot pass (a lost surface, a format that takes no copies), it is thrown and presenting
        /// ends (Failure).
        bool TryRecreate(int width, int height, int interval)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (recreateFailures > 0 && now < nextRecreate) return false; // backing off: the frame is not shown
            try
            {
                if (OutOfMemoryRecreates > 0)
                {
                    OutOfMemoryRecreates--;
                    outOfDate = true;
                    throw new VkResultError(VkResult.ErrorOutOfDeviceMemory, "ONI_VULKAN_FAIL=present-oom-after (a test of a swapchain out of memory)");
                }
                Recreate(width, height, interval);
                if (recreateFailures > 0) EngineLog.Engine($"vulkan: the swapchain was made after {recreateFailures} failed attempt(s)");
                recreateFailures = 0;
                return true;
            }
            // What Recreate's calls can return that may pass (vk.xml's error codes of
            // vkCreateSwapchainKHR, the surface queries and vkGetSwapchainImagesKHR): out of host
            // or device memory, a window still held by another swap chain, and, for
            // vkCreateSwapchainKHR, VK_ERROR_INITIALIZATION_FAILED (an object's initialization
            // "could not be completed for implementation-specific reasons", the specification's
            // general definition). None of them returns VK_ERROR_OUT_OF_DATE_KHR.
            catch (VkResultError e) when (e.Result is VkResult.ErrorOutOfHostMemory or VkResult.ErrorOutOfDeviceMemory
                or VkResult.ErrorNativeWindowInUseKHR or VkResult.ErrorInitializationFailed)
            {
                if (recreateFailures++ == 0) firstFailure = now;
                // Given up only after both RecreateAttempts tries and RecreateGiveUp seconds, so a
                // burst of failures (a resize while memory is short) has time to pass.
                if (recreateFailures >= RecreateAttempts && now - firstFailure > RecreateGiveUp * System.Diagnostics.Stopwatch.Frequency) throw;
                double delay = Math.Min(MaxBackoff, 0.1 * Math.Pow(2, recreateFailures - 1));
                nextRecreate = now + (long)(delay * System.Diagnostics.Stopwatch.Frequency);
                EngineLog.Engine($"vulkan: the swapchain could not be made ({e.Result}); trying again in {delay * 1000:F0} ms");
                outOfDate = true;
                return false;
            }
        }

        long firstFailure, nextRecreate;
        const double MaxBackoff = 2, RecreateGiveUp = 10;

        /// Test hook (present-oom-after:N): swapchain creations still to fail as out of memory.
        public volatile int OutOfMemoryRecreates;

        /// (Re)makes the swapchain for the window's size and the swap interval; leaves it null
        /// while the window is minimized.
        void Recreate(int width, int height, int interval)
        {
            if (FailNextRecreate)
            {
                FailNextRecreate = false;
                throw new VkResultError(VkResult.ErrorSurfaceLostKHR, "ONI_VULKAN_FAIL=present (a test of a presenter failure)");
            }
            bool force = outOfDate;
            outOfDate = false;
            swapInterval = interval;
            var caps = vk.Physical.GetSurfaceCapabilitiesKHR(surface);
            var size = caps.CurrentExtent.Width != -1 ? caps.CurrentExtent : new VkExtent2D(width, height);
            size = new VkExtent2D(Math.Clamp(size.Width, caps.MinImageExtent.Width, Math.Max(caps.MinImageExtent.Width, caps.MaxImageExtent.Width)),
                Math.Clamp(size.Height, caps.MinImageExtent.Height, Math.Max(caps.MinImageExtent.Height, caps.MaxImageExtent.Height)));
            var mode = ModeFor(interval);
            if (!swapchain.IsNull() && size.Width == extent.Width && size.Height == extent.Height && mode == presentMode && !force && size.Width > 0) return;
            // Minimized and already without a swapchain: nothing to wait for or release.
            if (swapchain.IsNull() && (size.Width == 0 || size.Height == 0))
            {
                extent = size;
                return;
            }
            // The images about to be replaced may still be read by submitted work.
            vk.Queue.WaitIdle();
            var old = swapchain;
            swapchain = default;
            DestroyImages();
            if (size.Width == 0 || size.Height == 0)
            {
                if (!old.IsNull()) vk.Device.DestroySwapchainKHR(old, null);
                extent = size;
                return;
            }
            int formatCount = vk.Physical.GetSurfaceFormatsKHR_Count(surface);
            var formats = new VkSurfaceFormatKHR[formatCount];
            vk.Physical.GetSurfaceFormatsKHR_Write(surface, formats);
            // Gamma colour space: the frame's values go to the display as they are, as OpenGL's
            // default framebuffer (no sRGB encoding).
            var chosen = formats[0];
            foreach (var f in formats)
                if (f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR && f.Format is VkFormat.B8G8R8A8UNorm or VkFormat.R8G8B8A8UNorm) { chosen = f; break; }
            var features = vk.Physical.GetFormatProperties(chosen.Format).OptimalTilingFeatures;
            if ((features & VkFormatFeatureFlags.BlitDstBit) == 0 || (caps.SupportedUsageFlags & VkImageUsageFlags.TransferDstBit) == 0)
                throw new VulkanUnavailableException($"the window's swapchain format {chosen.Format} takes no copies");
            int count = Math.Max(caps.MinImageCount, 3);
            if (caps.MaxImageCount > 0) count = Math.Min(count, caps.MaxImageCount);
            var composite = (caps.SupportedCompositeAlpha & VkCompositeAlphaFlagsKHR.OpaqueBitKHR) != 0 ? VkCompositeAlphaFlagsKHR.OpaqueBitKHR
                : (VkCompositeAlphaFlagsKHR)((int)caps.SupportedCompositeAlpha & -(int)caps.SupportedCompositeAlpha);
            try
            {
                swapchain = vk.Device.CreateSwapchainKHR(new VkSwapchainCreateInfoKHR
                {
                    Surface = surface, MinImageCount = count, ImageFormat = chosen.Format, ImageColorSpace = chosen.ColorSpace, ImageExtent = size,
                    ImageArrayLayers = 1, ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit,
                    ImageSharingMode = VkSharingMode.Exclusive, PreTransform = caps.CurrentTransform, CompositeAlpha = composite,
                    PresentMode = mode, Clipped = true, OldSwapchain = old,
                }, null);
            }
            finally
            {
                // The old swapchain is retired even when the new one could not be made.
                if (!old.IsNull()) vk.Device.DestroySwapchainKHR(old, null);
            }
            SwapchainFormat = chosen.Format;
            extent = size;
            presentMode = mode;
            int n = vk.Device.GetSwapchainImagesKHR_Count(swapchain);
            images = new VkImage[n];
            vk.Device.GetSwapchainImagesKHR_Write(swapchain, images);
            views = new VkImageView[n];
            renderDone = new VkSemaphore[n];
            imageLayouts = new VkImageLayout[n];
            for (int i = 0; i < n; i++)
            {
                views[i] = vk.Device.CreateImageView(new VkImageViewCreateInfo
                {
                    Image = images[i], ViewType = VkImageViewType._2D, Format = chosen.Format,
                    SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
                }, null);
                renderDone[i] = vk.Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            }
            EngineLog.Engine($"vulkan: swapchain {size.Width}x{size.Height} {chosen.Format}, {n} images, {mode}");
        }

        void DestroyImages()
        {
            foreach (var v in views) vk.Device.DestroyImageView(v, null);
            foreach (var s in renderDone) vk.Device.DestroySemaphore(s, null);
            views = Array.Empty<VkImageView>();
            renderDone = Array.Empty<VkSemaphore>();
            images = Array.Empty<VkImage>();
        }

        /// Test hook (ONI_VULKAN_FAIL=present-after:N, VkDevice): the next swapchain creation
        /// fails as on a lost surface.
        public volatile bool FailNextRecreate;

        /// Presents what was handed over, stops the thread and releases its objects and the
        /// back buffers (the device is idle after). Safe to call again.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (thread != null)
            {
                lock (gate)
                {
                    stopping = true;
                    Monitor.PulseAll(gate);
                }
                thread.Join();
            }
            try { vk.Queue.WaitIdle(); } catch (VkResultError) { }
            if (ownsSplash) splash?.Dispose();
            if (OperatingSystem.IsWindows()) dxgi?.Dispose();
            dxgi = null;
            DestroyImages();
            if (!swapchain.IsNull()) vk.Device.DestroySwapchainKHR(swapchain, null);
            swapchain = default;
            for (int i = 0; i < Slots; i++)
            {
                if (!fences[i].IsNull()) vk.Device.DestroyFence(fences[i], null);
                if (!acquired[i].IsNull()) vk.Device.DestroySemaphore(acquired[i], null);
            }
            if (!pool.IsNull()) vk.Device.DestroyCommandPool(pool, null);
            foreach (var b in buffers)
            {
                b.Color?.Destroy(vk);
                b.Depth?.Destroy(vk);
                b.Color = b.Depth = null;
            }
        }
    }
}
