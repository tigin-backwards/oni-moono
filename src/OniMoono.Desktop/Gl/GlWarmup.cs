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

        // Diagnostics: built here since the queue was last empty, and the GL thread's time on them.
        static long ticks, ran, skipped;
        static long queuedAt;

        public static bool HasWork => !pending.IsEmpty;

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
            try
            {
                if (v.Shader.Destroyed || v.Shader.DestroyRequested || !built.Add((v.Shader, v.Blob))) { skipped++; return; }
                var program = GlPrograms.BuildVariant(v.Shader, v.Blob);
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
                ticks += GlTimings.Now - start;
                if (pending.IsEmpty && ran + skipped > 0)
                {
                    EngineLog.Engine($"shader warm-up: {ran} programs built in {ticks * 1000.0 / Stopwatch.Frequency:F0} ms on the GL thread while it was idle, "
                        + $"{skipped} built at first use already, done {Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds:F0} ms after the warm-up was asked");
                    ticks = ran = skipped = 0;
                }
            }
        }

        /// GL thread, from GlPrograms.Get (the main thread waits): a variant built here and not yet
        /// published.
        public static bool Take(ShaderNative shader, int blob, out DeviceProgram program) => unpublished.TryRemove((shader, blob), out program);

        /// GL thread, from GlPrograms.Get: a variant built at its first use.
        public static void NoteBuilt(ShaderNative shader, int blob) => built.Add((shader, blob));

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
