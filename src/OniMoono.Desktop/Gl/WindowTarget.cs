using System;
using Brutal.GlfwApi;
using N = Brutal.OpenGlApi.OpenGl.Native;

namespace OniMoono.Desktop.Gl
{
    /// Where the presenter thread puts the window's picture and how it shows it: OpenGL's own
    /// default framebuffer and SwapBuffers (GlSwapTarget), or on Windows a DXGI swap chain
    /// (DxgiTarget). Every call is on the presenter thread, with the window's context current.
    /// ARCHITECTURE §3.5, "Presentation on Windows".
    internal interface IWindowTarget : IDisposable
    {
        /// For the log: "OpenGL (SwapBuffers)", "DXGI flip model".
        string Name { get; }

        /// The framebuffer the picture is drawn into between Begin and Show (0: the window's
        /// default framebuffer).
        uint Framebuffer { get; }

        /// Rows of Framebuffer run top-down (Direct3D's order): the presenter flips what it draws.
        bool FlipY { get; }

        /// Makes Framebuffer ready for a `width` x `height` picture and binds it for drawing.
        /// False: the target failed for good (Failure says why).
        bool Begin(int width, int height);

        /// Shows the picture drawn since Begin, `interval` vertical blanks after the last (0: at
        /// once). It returns once the picture is on its way to the display (OpenGL: with vsync).
        /// `again`: the last frame shown again between frames (the window's contents were
        /// damaged, or the splash), which OpenGL swaps as it is set, without waiting. False: the
        /// target failed for good.
        bool Show(int interval, bool again);

        /// The part of the last Show spent waiting for the picture to be on its way (Stopwatch
        /// ticks).
        long LastWait { get; }

        /// Why the target failed, once Begin or Show returned false.
        string Failure { get; }
    }

    /// OpenGL's own presentation: the window's default framebuffer, swapped with SwapBuffers.
    internal sealed class GlSwapTarget(GlfwWindow window) : IWindowTarget
    {
        const uint SyncGpuCommandsComplete = 0x9117, TimeoutExpired = 0x911B;
        const ulong WaitSliceNanoseconds = 100_000_000;
        int swapInterval = -1;

        public string Name => "OpenGL (SwapBuffers)";
        public uint Framebuffer => 0;
        public bool FlipY => false;
        public string Failure => null;

        public bool Begin(int width, int height)
        {
            GlApi.BindFramebuffer(GlConst.DrawFramebuffer, 0);
            return true;
        }

        public long LastWait { get; private set; }

        public unsafe bool Show(int interval, bool again)
        {
            LastWait = 0;
            if (!again && interval != swapInterval)
            {
                Glfw.SwapInterval(interval);
                swapInterval = interval;
            }
            window.SwapBuffers();
            if (!again && interval > 0)
            {
                // With vsync, a fence after the swap, waited for so that the frame counts as
                // queued until the driver has done its swap. That it signals only then is driver
                // behaviour, not a GL guarantee (SwapBuffers is the window system's: WGL, GLX,
                // EGL). Measured on Windows at 60 Hz with it and one frame queued (19827d58): a
                // key press reached the screen in 50 ms instead of 83, as in the original. That
                // was an RTX 3090 on NVIDIA's driver 610.88 (OpenGL 4.5), windowed under Windows
                // 11's compositor; other drivers and compositors are unmeasured.
                long t = GlTimings.Now;
                var swapped = N.glFenceSync(SyncGpuCommandsComplete, default);
                N.glFlush();
                while (N.glClientWaitSync(swapped, default, WaitSliceNanoseconds) == TimeoutExpired) { }
                N.glDeleteSync(swapped);
                LastWait = GlTimings.Now - t;
            }
            return true;
        }

        public void Dispose() { }
    }
}
