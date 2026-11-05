using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace OniMoono.Engine
{
    /// Frame stats (`--frame-stats`), every 600 frames after the memory lines: what the main
    /// thread waited for that the engine owns (the device, texel copies), the runtime's GC, JIT,
    /// locks and thread pool over the window, the machine's state (MachineStats), and the lines
    /// of the reports added by the host (the game's update: OniMoono.GameTweaks'
    /// FrameStatsProbes). The counters are added to only with frame stats on.
    internal static class FrameStatsWindow
    {
        /// Main thread, Stopwatch ticks: waiting for the device to take the next frame (the
        /// previous one still executing), waiting for a call's result from the device (pixel
        /// read-back, program compilation, diagnostics) and how many there were, and waiting for
        /// last frame's texel copies.
        public static long DeviceFrameWait, DeviceCallWait, DeviceCalls, TexelCopyWait;

        /// Main thread, Stopwatch ticks: waiting before a frame, with vsync, for the picture
        /// QualitySettings.maxQueuedFrames frames back to be presented (QueuedFrames).
        public static long QueuedFrameWait;
        /// The frames whose wait was on (frames left time to spare).
        public static long QueuedFrameWaitsOn;

        /// The device's thread waiting for the presenter to take a picture (its queue full), in
        /// Stopwatch ticks; added to with Interlocked.
        public static long PresenterQueueWait;

        /// Reports of the window's wall seconds and frames: lines to log.
        public static readonly List<Func<double, int, string[]>> Reports = new();

        /// The window's longest GC pause (of the collections seen: the last one at each frame's
        /// end, so a frame with two keeps only the later), and the most a frame was paused in
        /// all, in milliseconds (GcFrame).
        static double longestGcPause, mostGcPauseInFrame;
        static int lastGcIndex = -1;
        static double lastPauseTotal = -1;

        static RuntimeSample lastRuntime;
        static MachineSample lastMachine;
        static long windowStarted;

        public static long Now => Stopwatch.GetTimestamp();

        /// At each frame's end (frame stats on): the GC's pauses since the last frame.
        public static void GcFrame()
        {
            double total = GC.GetTotalPauseDuration().TotalMilliseconds;
            if (lastPauseTotal >= 0 && total > lastPauseTotal)
            {
                mostGcPauseInFrame = Math.Max(mostGcPauseInFrame, total - lastPauseTotal);
                var info = GC.GetGCMemoryInfo(GCKind.Any);
                if (info.Index != lastGcIndex)
                {
                    lastGcIndex = (int)info.Index;
                    foreach (var pause in info.PauseDurations) longestGcPause = Math.Max(longestGcPause, pause.TotalMilliseconds);
                }
            }
            lastPauseTotal = total;
        }

        /// The window's lines, and the start of the next window. The first call only starts one.
        public static List<string> Take(int frames)
        {
            var lines = new List<string>();
            long now = Stopwatch.GetTimestamp();
            var runtime = RuntimeSample.Now();
            var machine = MachineStats.Sample();
            bool started = windowStarted != 0;
            double seconds = started ? (now - windowStarted) / (double)Stopwatch.Frequency : 0;
            // The first call starts the reports' windows too (what they counted is dropped).
            foreach (var report in Reports)
            {
                try
                {
                    var reported = report(seconds, started ? frames : 0);
                    if (started) lines.AddRange(reported);
                }
                catch (Exception e)
                {
                    lines.Add($"frame stats report failed: {e.GetType().Name}: {e.Message}");
                }
            }
            if (started)
            {
                lines.Add(Waits(frames, seconds, runtime - lastRuntime));
                lines.Add(MachineStats.Describe(lastMachine, machine, seconds));
            }
            DeviceFrameWait = DeviceCallWait = DeviceCalls = TexelCopyWait = QueuedFrameWait = QueuedFrameWaitsOn = 0;
            longestGcPause = mostGcPauseInFrame = 0;
            Interlocked.Exchange(ref PresenterQueueWait, 0);
            windowStarted = now;
            lastRuntime = runtime;
            lastMachine = machine;
            return lines;
        }

        static string Waits(int frames, double seconds, RuntimeSample d)
        {
            var c = CultureInfo.InvariantCulture;
            double ms = 1000.0 / Stopwatch.Frequency;
            string Ms(long ticks) => (ticks * ms / frames).ToString("F2", c);
            string PerFrame(double n) => (n / frames).ToString("F2", c);
            return $"main thread waits (ms/frame over {frames}): device frame {Ms(DeviceFrameWait)}, queued frames {Ms(QueuedFrameWait)} (on for {PerFrame(QueuedFrameWaitsOn * 100.0)}%), device calls {Ms(DeviceCallWait)} ({PerFrame(DeviceCalls)} a frame), texel copies {Ms(TexelCopyWait)}; "
                + $"device thread waiting for the presenter {Ms(Interlocked.Read(ref PresenterQueueWait))}; "
                + $"GC paused {(d.PauseMs / frames).ToString("F2", c)} (gen0 {d.Gen0}, gen1 {d.Gen1}, gen2 {d.Gen2} in the window; longest pause {longestGcPause.ToString("F1", c)} ms, "
                + $"most in a frame {mostGcPauseInFrame.ToString("F1", c)} ms), allocated {(d.Allocated / 1048576.0 / Math.Max(seconds, 1e-9)).ToString("F1", c)} MiB/s; "
                + $"JIT {(d.JitMs / frames).ToString("F2", c)} on all threads ({d.JitMethods} methods); lock contentions {PerFrame(d.Contentions)} a frame; "
                + $"thread pool {d.PoolThreads} threads ({(d.PoolThreadsChange >= 0 ? "+" : "")}{d.PoolThreadsChange}), {d.PoolQueued} queued, {PerFrame(d.PoolCompleted)} items a frame";
        }

        /// The runtime's counters at a moment; a difference holds the window's (the thread
        /// pool's thread count and queue as they are now, with the change in threads).
        internal struct RuntimeSample
        {
            public long Gen0, Gen1, Gen2, Allocated, JitMethods, Contentions, PoolCompleted, PoolQueued;
            public int PoolThreads, PoolThreadsChange;
            public double PauseMs, JitMs;

            public static RuntimeSample Now() => new()
            {
                Gen0 = GC.CollectionCount(0),
                Gen1 = GC.CollectionCount(1),
                Gen2 = GC.CollectionCount(2),
                PauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                Allocated = GC.GetTotalAllocatedBytes(false),
                JitMs = System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds,
                JitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(false),
                Contentions = Monitor.LockContentionCount,
                PoolThreads = ThreadPool.ThreadCount,
                PoolQueued = ThreadPool.PendingWorkItemCount,
                PoolCompleted = ThreadPool.CompletedWorkItemCount,
            };

            public static RuntimeSample operator -(RuntimeSample now, RuntimeSample then) => new()
            {
                // A collection of a generation counts in the younger ones too: each is its own.
                Gen2 = now.Gen2 - then.Gen2,
                Gen1 = now.Gen1 - then.Gen1 - (now.Gen2 - then.Gen2),
                Gen0 = now.Gen0 - then.Gen0 - (now.Gen1 - then.Gen1),
                PauseMs = now.PauseMs - then.PauseMs,
                Allocated = now.Allocated - then.Allocated,
                JitMs = now.JitMs - then.JitMs,
                JitMethods = now.JitMethods - then.JitMethods,
                Contentions = now.Contentions - then.Contentions,
                PoolThreads = now.PoolThreads,
                PoolThreadsChange = now.PoolThreads - then.PoolThreads,
                PoolQueued = now.PoolQueued,
                PoolCompleted = now.PoolCompleted - then.PoolCompleted,
            };
        }
    }
}
