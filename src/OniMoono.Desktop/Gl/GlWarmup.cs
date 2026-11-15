using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;

namespace OniMoono.Desktop.Gl
{
    /// Shader.WarmupAllShaders without the main thread waiting: the programs are built on the GL
    /// thread while it has no frame to execute, one at a time, and the main thread goes on. The
    /// game calls the warm-up once a load (Game.OnPrefabInit), and the main thread then loads the
    /// colony for seconds while the GL thread is almost idle; with an empty driver cache the
    /// warm-up took 1.8 s of a save load on Linux (docs/research/save-load-and-loading-screen.md).
    ///
    /// Unity's WarmupAllShaders returns once the programs are built; nothing a script can read
    /// depends on that, only how long the first draws take. A draw whose program is not built
    /// yet builds it at its first use (GlDrawRecorder.Program's call to the GL thread, as for a
    /// program the warm-up does not cover), and the warm-up then skips it.
    ///
    /// Threads: a shader's programs (its DeviceProgramSet) are read by the main thread and its
    /// draw recorders and written by the GL thread only inside a call the main thread waits for
    /// (GlPrograms.Get). A program built here is held apart (`unpublished`) until the main thread
    /// adds it to its set (Publish, at a frame's present and before a missing program's call), or
    /// a call's GlPrograms.Get takes it. `built` (GL thread only) is every variant built, so
    /// none is built twice. ONI_WARMUP_AHEAD=0: the warm-up waits for every program, as before.
    internal static class GlWarmup
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("ONI_WARMUP_AHEAD") != "0";

        static readonly ConcurrentQueue<(ShaderNative Shader, int Blob)> pending = new();
        static readonly ConcurrentDictionary<(ShaderNative, int), DeviceProgram> unpublished = new();
        static readonly ConcurrentQueue<(ShaderNative Shader, int Blob, DeviceProgram Program)> toPublish = new();
        static readonly HashSet<(ShaderNative, int)> built = new();
        static GlWorker worker;

        // Diagnostics, GL thread: the warm-up's builds since the queue was last empty, and those
        // at first use since the last line about them.
        static readonly ProgramBuildLog warmed = new(), firstUse = new();
        static long ticks, ran, skipped;
        static long queuedAt;
        // The frames and calls handed over while a build ran, and their wait for it to end.
        static long heldFrames, heldTicks;
        // At the warm-up's first build: the time, the frames executed and the process's CPU time.
        static long startedAt, startBatches;
        static TimeSpan startProcessCpu;
        static int slowLines;

        /// A build at first use this slow gets a line of its own (the first SlowLines of a run):
        /// the main thread waited for it.
        const double SlowFirstUseMs = 100;
        const int SlowLines = 10;

        public static bool HasWork => !pending.IsEmpty;

        /// Variants queued and not built yet (the quit's log).
        public static int Pending => pending.Count;

        public static void Start(GlWorker glWorker)
        {
            worker = glWorker;
            var otherWork = worker.IdleWork;
            var hasOtherWork = worker.HasIdleWork;
            // Uploads and work ahead of use first: a frame that needs them comes sooner.
            worker.IdleWork = () =>
            {
                if (hasOtherWork?.Invoke() == true && otherWork()) return true;
                if (!HasWork) return false;
                BuildOne();
                return true;
            };
            worker.HasIdleWork = () => hasOtherWork?.Invoke() == true || HasWork;
        }

        /// Main thread: queues the variants to build and wakes the GL thread.
        public static void Queue(List<(ShaderNative Shader, int Blob)> variants)
        {
            if (variants.Count == 0) return;
            if (pending.IsEmpty) queuedAt = Stopwatch.GetTimestamp();
            foreach (var v in variants) pending.Enqueue(v);
            worker.Wake();
        }

        /// GL thread, while no frame is handed over: one variant.
        static void BuildOne()
        {
            if (!pending.TryDequeue(out var v)) return;
            long start = GlTimings.Now;
            if (startedAt == 0)
            {
                startedAt = start;
                startBatches = worker.Batches;
                startProcessCpu = Environment.CpuUsage.TotalTime;
            }
            try
            {
                if (v.Shader.Destroyed || v.Shader.DestroyRequested || !built.Add((v.Shader, v.Blob))) { skipped++; return; }
                var program = GlPrograms.BuildVariant(v.Shader, v.Blob);
                warmed.Add(v.Shader.ShaderName, v.Blob, GlPrograms.LastBuild);
                unpublished[(v.Shader, v.Blob)] = program;
                toPublish.Enqueue((v.Shader, v.Blob, program));
                ran++;
            }
            catch (Exception e)
            {
                EngineLog.Engine($"shader warm-up: {v.Shader.ShaderName} variant {v.Blob} failed: {e.Message}");
            }
            finally
            {
                long now = GlTimings.Now;
                ticks += now - start;
                // A frame handed over meanwhile has waited since then for this build.
                long waiting = worker.WaitingSince;
                if (waiting != 0)
                {
                    heldFrames++;
                    heldTicks += now - Math.Max(waiting, start);
                }
                if (pending.IsEmpty && ran + skipped > 0)
                {
                    foreach (var line in Summary(ran, skipped, ticks, Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds, warmed, firstUse, heldFrames, heldTicks,
                                 worker.Batches - startBatches, now - startedAt, (Environment.CpuUsage.TotalTime - startProcessCpu).TotalMilliseconds, Environment.ProcessorCount))
                        EngineLog.Engine(line);
                    warmed.Reset();
                    firstUse.Reset();
                    ticks = ran = skipped = heldFrames = heldTicks = startedAt = 0;
                }
            }
        }

        /// The lines of a finished warm-up: the builds and where their time went; what they cost
        /// the frames; and the builds at first use since the last warm-up, when there were any.
        internal static IEnumerable<string> Summary(long ran, long skipped, long ticks, double sinceAskedMs, ProgramBuildLog warmed, ProgramBuildLog firstUse,
            long heldFrames, long heldTicks, long batches, long spanTicks, double processCpuMs, int processors)
        {
            static string Ms(long t) => ProgramBuildLog.Ms(t);
            var c = System.Globalization.CultureInfo.InvariantCulture;
            yield return $"shader warm-up: {ran} programs built in {Ms(ticks)} ms on the GL thread while it was idle ({warmed.Sum.CpuText()} on that thread), "
                + $"{skipped} queued twice or built at first use already, done {sinceAskedMs.ToString("F0", c)} ms after the warm-up was asked";
            if (ran > 0)
                yield return $"shader warm-up: {warmed.Describe()}; {heldFrames} frames or calls were handed over during a build and waited {Ms(heldTicks)} ms in all; "
                    + $"over the warm-up's {Ms(spanTicks)} ms the GL thread executed {batches} frames or calls and the process used {processCpuMs.ToString("F0", c)} ms of CPU on {processors} logical processors";
            if (firstUse.Count > 0)
                yield return $"shader programs at first use: {firstUse.Count} built in {Ms(firstUse.Sum.Total)} ms while the main thread waited ({firstUse.Sum.CpuText()} on the GL thread): {firstUse.Describe()}";
        }

        /// GL thread, from GlPrograms.Get (the main thread waits): a variant built here and not yet
        /// published.
        public static bool Take(ShaderNative shader, int blob, out DeviceProgram program) => unpublished.TryRemove((shader, blob), out program);

        /// GL thread, from GlPrograms.Get: a variant built at its first use, and where the
        /// build's time went.
        public static void NoteBuilt(ShaderNative shader, int blob, in ProgramBuildSplit split)
        {
            built.Add((shader, blob));
            firstUse.Add(shader.ShaderName, blob, split);
            if (split.Total * 1000.0 / Stopwatch.Frequency < SlowFirstUseMs || slowLines >= SlowLines) return;
            slowLines++;
            EngineLog.Engine($"shader program at first use: {shader.ShaderName.Trim()} variant {blob} took {ProgramBuildLog.Ms(split.Total)} ms ({split.CpuText()}): {split.Describe()}"
                + (slowLines == SlowLines ? "; no more such lines this run" : ""));
        }

        /// Main thread, while no draw recorder runs: the programs built since, into their shaders'
        /// sets. A shader destroyed meanwhile has its program deleted on the GL thread.
        public static void Publish()
        {
            while (toPublish.TryDequeue(out var p))
            {
                // Taken by a call's GlPrograms.Get first: in its set already.
                if (!unpublished.TryRemove((p.Shader, p.Blob), out _)) continue;
                if (p.Shader.Destroyed || p.Shader.DestroyRequested)
                {
                    var program = p.Program;
                    worker.Post(() => GlPrograms.Delete(program));
                    continue;
                }
                if (p.Shader.Gpu is not DeviceProgramSet set)
                {
                    set = new DeviceProgramSet();
                    p.Shader.Gpu = set;
                    GraphicsDevice.Adopt(p.Shader, set);
                }
                set.Programs.TryAdd(p.Blob, p.Program);
            }
        }
    }
}
