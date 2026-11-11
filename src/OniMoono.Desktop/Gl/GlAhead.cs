using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using OniMoono.Engine;

namespace OniMoono.Desktop.Gl
{
    /// Device work done ahead of use: the storage of textures scripts make (`new Texture2D`) and
    /// the buffers of meshes whose data is set but which no draw has used yet are made on the GL
    /// thread while it has no frame to execute (or, when it never runs out of frames, about a
    /// millisecond before each), instead of in the frame that first draws them. Making them is
    /// most of the cost of a first draw on some drivers (Mesa's D3D12: about 0.4 ms a texture,
    /// 40 µs a mesh of three buffers), and a view that shows much at once for the first time
    /// pays it all in one frame: a colony preview or timelapse snapshot after a load (the
    /// animation batches' data textures and the terrain chunks' meshes, thousands of them).
    /// What a frame records is unchanged: a texture's first upload fills the storage made ahead
    /// (or makes it, if it comes first), and a mesh's first draw posts its upload into the frame
    /// ahead of the draw when it has not run yet. ONI_AHEAD_OF_USE=0 turns it off.
    internal static class GlAhead
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("ONI_AHEAD_OF_USE") != "0";

        /// Textures made ahead are at most this large; a larger one waits for its first use.
        public const long MaxTextureBytes = 16L << 20;

        static readonly ConcurrentQueue<Action> jobs = new();
        static GlWorker worker;

        /// Time a frame waits at most for work ahead that the GL thread has started.
        static readonly long SliceTicks = Stopwatch.Frequency / 1000;

        public static bool HasWork => !jobs.IsEmpty;

        // Diagnostics (--frame-stats, ONI_STARTUP_STATS=1): jobs run and the GL thread's time
        // on them since the queue was last empty, logged when it empties.
        static long ticks, ran;

        public static void Start(GlWorker glWorker)
        {
            worker = glWorker;
            var otherWork = worker.IdleWork;
            var hasOtherWork = worker.HasIdleWork;
            // The background uploads first; this work when they have none, or cannot go on yet.
            worker.IdleWork = () =>
            {
                if (hasOtherWork?.Invoke() == true && otherWork()) return true;
                if (!HasWork) return false;
                RunSome();
                return true;
            };
            worker.HasIdleWork = () => hasOtherWork?.Invoke() == true || HasWork;
        }

        public static void Enqueue(Action job) => jobs.Enqueue(job);

        /// Wakes the GL thread for queued work (main thread, after queueing some).
        public static void Wake()
        {
            if (HasWork) worker?.Wake();
        }

        // GL thread: it ran work ahead while idle since the last batch.
        static bool idled;

        /// GL thread, before each batch of commands: a slice of the queued work when the thread
        /// had no idle time since the last batch (a frame waiting for the GL thread as soon as it
        /// is done with the last, frame rate unlimited), so the work still gets done.
        public static void BetweenBatches()
        {
            if (!idled && HasWork) Run();
            idled = false;
        }

        /// GL thread, while it has no frame to execute.
        static void RunSome()
        {
            idled = true;
            Run();
        }

        /// GL thread: queued jobs for about a millisecond.
        static void Run()
        {
            long start = GlTimings.Now, now = start;
            while (now - start < SliceTicks && jobs.TryDequeue(out var job))
            {
                try { job(); }
                catch (Exception e) { EngineLog.Engine($"work ahead of use failed: {e.Message}"); }
                now = GlTimings.Now;
                ran++;
            }
            ticks += now - start;
            if (jobs.IsEmpty && ran >= 100 && (OniMoono.Engine.PlayerLoop.FrameStats || StartupStats.Enabled))
                EngineLog.Engine($"work ahead of use: {ran} textures and meshes made in {ticks * 1000.0 / Stopwatch.Frequency:F0} ms (GL thread)");
            if (jobs.IsEmpty) ticks = ran = 0;
        }
    }
}
