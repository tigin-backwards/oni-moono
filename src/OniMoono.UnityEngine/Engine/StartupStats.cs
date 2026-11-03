using System;
using System.Diagnostics;
using System.Threading;

namespace OniMoono.Engine
{
    /// Diagnostics (ONI_STARTUP_STATS=1): start-up phases logged with the time since the process
    /// started and the JIT's work so far (all threads' compile time summed, and the logging
    /// thread's), and the texel reads, uploads and program builds of each slow frame.
    internal static class StartupStats
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("ONI_STARTUP_STATS") == "1";

        static readonly DateTime processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

        public static double Now => (DateTime.UtcNow - processStart).TotalMilliseconds;

        public static void Mark(string what)
        {
            if (Enabled) EngineLog.Engine($"[startup {Now,8:F0} ms] {what} ({Jit()})");
        }

        /// The JIT's compile time summed over all threads, its compiles (a method compiled again
        /// at a higher tier counts again), and this thread's compile time.
        public static string Jit() =>
            $"jit {System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds:F0} ms over {System.Runtime.JitInfo.GetCompiledMethodCount()} methods, "
            + $"{System.Runtime.JitInfo.GetCompilationTime(currentThread: true).TotalMilliseconds:F0} ms on this thread";

        // Counters since the last frame report (Interlocked: some are GL-thread counters).
        public static long Uploads, UploadBytes, UploadTicks, ReadTicks, Programs, ProgramTicks, ProgramWaitTicks, GlBusyTicks, GlUploadTicks;

        static long frameStart = Stopwatch.GetTimestamp();

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// End of a frame: reports it when slow (or among the first few).
        public static void FrameEnd(int frame)
        {
            if (!Enabled) return;
            long now = Stopwatch.GetTimestamp();
            double ms = Ms(now - frameStart);
            frameStart = now;
            if (ms > 100 || frame < 3)
                Mark($"frame {frame} took {ms:F0} ms: texel reads {Ms(Interlocked.Exchange(ref ReadTicks, 0)):F0} ms, uploads recorded {Interlocked.Exchange(ref Uploads, 0)} ({Interlocked.Exchange(ref UploadBytes, 0) >> 20} MiB) in {Ms(Interlocked.Exchange(ref UploadTicks, 0)):F0} ms, programs built {Interlocked.Exchange(ref Programs, 0)} in {Ms(Interlocked.Exchange(ref ProgramTicks, 0)):F0} ms (GL thread), main thread waited {Ms(Interlocked.Exchange(ref ProgramWaitTicks, 0)):F0} ms for programs; GL thread busy {Ms(Interlocked.Exchange(ref GlBusyTicks, 0)):F0} ms, texture uploads {Ms(Interlocked.Exchange(ref GlUploadTicks, 0)):F0} ms; window messages pumped {Interlocked.Exchange(ref Platform.MainThreadPump.Pumps, 0)} times, longest gap {Ms(Interlocked.Exchange(ref Platform.MainThreadPump.LongestTicks, 0)):F0} ms");
            else
            {
                Interlocked.Exchange(ref ReadTicks, 0); Interlocked.Exchange(ref Uploads, 0); Interlocked.Exchange(ref UploadBytes, 0);
                Interlocked.Exchange(ref UploadTicks, 0); Interlocked.Exchange(ref Programs, 0); Interlocked.Exchange(ref ProgramTicks, 0);
                Interlocked.Exchange(ref ProgramWaitTicks, 0); Interlocked.Exchange(ref GlBusyTicks, 0); Interlocked.Exchange(ref GlUploadTicks, 0);
                Interlocked.Exchange(ref Platform.MainThreadPump.Pumps, 0); Interlocked.Exchange(ref Platform.MainThreadPump.LongestTicks, 0);
            }
        }
    }
}
