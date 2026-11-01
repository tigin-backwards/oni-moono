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

    /// Frame timings, off as in U59's player. Measured there (UnityProbe's frametiming, Linux):
    /// IsFeatureEnabled is false, CaptureFrameTimings does nothing, and GetLatestTimings returns 0
    /// for any count and leaves the array as it was; GetVSyncsPerSecond is 0 and both timer
    /// frequencies 1e9 (nanoseconds). Here the CPU timer is Stopwatch's. (U60's player, a
    /// development build, has the feature on and records every frame.)
    public static partial class FrameTimingManager
    {
        public static bool IsFeatureEnabled() => false;

        public static void CaptureFrameTimings() { }

        /// No frames are recorded: nothing is copied.
        public static uint GetLatestTimings(uint numFrames, FrameTiming[] timings) => 0;

        public static float GetVSyncsPerSecond() => 0f;

        public static ulong GetGpuTimerFrequency() => 1000000000UL;

        public static ulong GetCpuTimerFrequency() => (ulong)Stopwatch.Frequency;
    }
}
