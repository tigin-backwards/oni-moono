using System;
using System.Diagnostics;

namespace UnityEngine
{
    /// One frame's timings, as FrameTimingManager hands them out. Times are milliseconds,
    /// timestamps ticks of FrameTimingManager.GetCpuTimerFrequency.
    public partial struct FrameTiming
    {
        public double cpuFrameTime;
        public double cpuMainThreadFrameTime;
        public double cpuMainThreadPresentWaitTime;
        public double cpuRenderThreadFrameTime;
        public double gpuFrameTime;
        public ulong frameStartTimestamp;
        public ulong firstSubmitTimestamp;
        public ulong cpuTimePresentCalled;
        public ulong cpuTimeFrameComplete;
        public float heightScale;
        public float widthScale;
        public uint syncInterval;
    }

    /// Frame timings of the frames the player completed, newest first. Measured in U60's Linux
    /// player (UnityProbe's frametiming): the feature is on and records every frame whether or
    /// not CaptureFrameTimings is called; a sample's cpuFrameTime runs from its frame's start to
    /// the next frame's start, the main thread's time and the wait for present adding up to it;
    /// gpuFrameTime is 0 (no GPU timer under OpenGL), the scales 1, GetVSyncsPerSecond 0 and both
    /// timer frequencies 1e9 (nanoseconds). Here the CPU timer is Stopwatch's. The player renders
    /// on its main thread, so the render thread's time is 0.
    public static partial class FrameTimingManager
    {
        const int Kept = 32;
        static readonly FrameTiming[] ring = new FrameTiming[Kept];
        static int newest = -1, count;
        static long frameStart, firstSubmit, presentCalled, frameComplete;

        public static bool IsFeatureEnabled() => true;

        /// The player records every frame's timings itself.
        public static void CaptureFrameTimings() { }

        /// Copies up to numFrames of the latest timings, newest first, and returns how many it
        /// copied. Unity's returns the count available up to numFrames even when the array is
        /// shorter, writing past its end, and a null array crashes it; here the copy stops at the
        /// array's length.
        public static uint GetLatestTimings(uint numFrames, FrameTiming[] timings)
        {
            int n = (int)Math.Min(Math.Min(numFrames, (uint)count), (uint)(timings?.Length ?? 0));
            for (int i = 0; i < n; i++) timings![i] = ring[((newest - i) % Kept + Kept) % Kept];
            return (uint)n;
        }

        public static float GetVSyncsPerSecond() => 0f;

        public static ulong GetGpuTimerFrequency() => 1000000000UL;

        public static ulong GetCpuTimerFrequency() => (ulong)Stopwatch.Frequency;

        /// A frame starts: the previous one's sample is complete.
        internal static void BeginFrame(long now)
        {
            if (frameStart != 0)
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                long submit = firstSubmit != 0 ? firstSubmit : presentCalled;
                long present = presentCalled != 0 ? presentCalled : now;
                double main = (present - frameStart) * ms, frame = (now - frameStart) * ms;
                newest = (newest + 1) % Kept;
                ring[newest] = new FrameTiming
                {
                    cpuFrameTime = frame,
                    cpuMainThreadFrameTime = main,
                    cpuMainThreadPresentWaitTime = Math.Max(0.0, frame - main),
                    frameStartTimestamp = (ulong)frameStart,
                    firstSubmitTimestamp = (ulong)(submit != 0 ? submit : present),
                    cpuTimePresentCalled = (ulong)present,
                    cpuTimeFrameComplete = (ulong)(frameComplete != 0 ? frameComplete : present),
                    heightScale = 1f,
                    widthScale = 1f,
                };
                if (count < Kept) count++;
            }
            frameStart = now;
            firstSubmit = presentCalled = frameComplete = 0;
        }

        /// The frame's rendering begins.
        internal static void Submitting(long now) { if (firstSubmit == 0) firstSubmit = now; }

        internal static void PresentCalled(long now) => presentCalled = now;

        internal static void FrameComplete(long now) => frameComplete = now;
    }
}
