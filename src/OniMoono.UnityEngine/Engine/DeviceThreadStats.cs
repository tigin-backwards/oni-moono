using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace OniMoono.Engine
{
    /// Diagnostics (ONI_DEVICE_BUSY=1): the device thread's running totals since start-up, logged
    /// every 600 frames (`device thread totals`), cheap enough for frame-rate runs without frame
    /// stats (the D3D11 plan's default/fallback rule logs the device thread's busy time beside
    /// the frame rate). The time it executed handed-over frames and calls, and of that the time
    /// it waited for the next picture to be taken: the presenter's queue (Vulkan) or the swap
    /// chain's frame-latency waitable (Direct3D 11). Busy is the first less the second. The
    /// counters always run (an Interlocked add a batch); only the log line needs the setting.
    internal static class DeviceThreadStats
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("ONI_DEVICE_BUSY") == "1";

        /// Stopwatch ticks; added to with Interlocked by the device's threads.
        public static long BatchTicks, PresentWaitTicks, Batches;

        /// End of a frame (main thread).
        public static void FrameEnd(int frame)
        {
            if (!Enabled || frame % 600 != 0) return;
            static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture);
            long batch = Interlocked.Read(ref BatchTicks), wait = Interlocked.Read(ref PresentWaitTicks);
            EngineLog.Engine($"device thread totals at frame {frame}: batches {Interlocked.Read(ref Batches)} in {Ms(batch)} ms, waiting for the presenter {Ms(wait)} ms, busy {Ms(batch - wait)} ms");
        }
    }
}
