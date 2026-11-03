using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Brutal.OpenGlApi;
using static OniMoono.Desktop.Gl.GlConst;
using N = Brutal.OpenGlApi.OpenGl.Native;

namespace OniMoono.Desktop.Gl
{
    /// One non-readable texture loaded from an asset file, on its way to the GPU in the background
    /// (GlBackgroundUploads). Its state moves Queued → Reading → Ready → Taken; whoever takes it
    /// (the GL thread between frames, or the main thread when a draw needs it first) uploads it.
    internal sealed class BackgroundUpload
    {
        public const int Queued = 0, Reading = 1, Ready = 2, Taken = 3;
        public int State;
        public readonly ManualResetEventSlim Read = new();
        public DeviceTexture Device;
        public PixelMemory Memory;
        public TexelSource? Source;
        public TexelUpload Upload;
        public GlTextures.SamplerSettings Sampler;
        public long Bytes;
        public bool Failed;

        /// The texels in Upload (reader thread, or the main thread when it takes a queued one).
        public void ReadTexels()
        {
            try
            {
                Upload.Memory = Memory ?? Source.Value.ReadMemory();
            }
            catch (Exception e)
            {
                Failed = true;
                EngineLog.Engine($"background upload: texels unreadable ({e.Message}); the texture stays empty");
            }
            Memory = null;
            Source = null;
        }
    }

    /// Textures loaded from asset files go to the GPU in the background, in load order: worker
    /// threads read their texels, and the GL thread uploads them one at a time whenever it has no
    /// frame to execute (so a frame waits at most for one texture's upload). A draw that samples
    /// one not yet uploaded takes it, and the upload is recorded into its frame ahead of the draw
    /// (GlTextures.Prepare), as every upload was before; a texture released first is dropped. So
    /// the first scene shows without waiting for its 2 GiB of animation textures, and a DLC
    /// bundle's textures go up while the game's start-up goes on. Only non-readable textures go
    /// this way: the game never reads their texels, which the texture gives up when queued.
    /// ONI_BACKGROUND_UPLOADS=0 turns it off.
    internal static unsafe class GlBackgroundUploads
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("ONI_BACKGROUND_UPLOADS") != "0";

        static readonly ConcurrentQueue<BackgroundUpload> toRead = new(), ready = new();
        static readonly SemaphoreSlim readable = new(0);
        static readonly object budgetGate = new();
        /// Texels read and not yet taken, bounded so reading runs only so far ahead of uploading.
        static long readyBytes, readyPeak;
        const long ReadAhead = 256L << 20;

        /// Diagnostics (--frame-stats): the texels read and not yet uploaded, and their peak
        /// since the last call.
        public static string TakeCounters() => $"read ahead {Interlocked.Read(ref readyBytes) >> 20} MiB (peak {Interlocked.Exchange(ref readyPeak, Interlocked.Read(ref readyBytes)) >> 20})";
        static Thread[] readers;
        static GlWorker worker;

        // Statistics (StartupStats): queued and uploaded in the background since the last drain,
        // and the longest single upload.
        static long queued, queuedBytes, uploaded, uploadedBytes, longestTicks;
        static int pending;

        public static void Start(GlWorker glWorker)
        {
            worker = glWorker;
            worker.IdleWork = RunOne;
            worker.HasIdleWork = () => !ready.IsEmpty;
            int count = Math.Max(2, Environment.ProcessorCount / 4);
            readers = new Thread[count];
            for (int i = 0; i < count; i++)
            {
                readers[i] = new Thread(ReadLoop) { Name = "Texel reader", IsBackground = true, Priority = ThreadPriority.BelowNormal };
                readers[i].Start();
            }
        }

        /// Queues a loaded texture (main thread); false when it goes the usual way.
        public static bool Queue(TextureNative t)
        {
            if (!Enabled || worker == null || t is not TexelTextureNative tt || tt.Readable || tt is not (Texture2DNative or Texture2DArrayNative)) return false;
            if (tt.Width <= 0 || tt.Height <= 0 || tt.Depth <= 0 || !tt.HasTexels) return false;
            if (t.Gpu is not DeviceTexture g)
            {
                g = new DeviceTexture();
                t.Gpu = g;
                GraphicsDevice.Adopt(t, g);
            }
            else if (g.Background != null) Claim(g, null);
            else if (g.RecordedContent == tt.ContentVersion) return true;
            var (memory, source) = tt.DetachTexels();
            var job = new BackgroundUpload
            {
                Device = g,
                Memory = memory,
                Source = source,
                Upload = new TexelUpload
                {
                    Format = tt.Format, Width = tt.Width, Height = tt.Height, Depth = tt.Depth, MipCount = tt.MipCount,
                    Target = tt is Texture2DArrayNative ? TexTarget2DArray : TexTarget2D,
                },
                Sampler = new GlTextures.SamplerSettings(t.FilterMode, t.WrapU, t.WrapV, t.WrapW, t.MipBias, t.Aniso),
                Bytes = tt.SliceSize * tt.Depth,
            };
            g.Background = job;
            g.RecordedEmpty = false;
            g.RecordedContent = tt.ContentVersion;
            g.RecordedTarget = job.Upload.Target;
            g.RecordedSampler = t.SamplerVersion;
            Interlocked.Increment(ref queued);
            Interlocked.Add(ref queuedBytes, job.Bytes);
            Interlocked.Increment(ref pending);
            toRead.Enqueue(job);
            readable.Release();
            return true;
        }

        static void ReadLoop()
        {
            while (true)
            {
                readable.Wait();
                if (!toRead.TryDequeue(out var job)) continue;
                lock (budgetGate)
                    while (Interlocked.Read(ref readyBytes) > ReadAhead) Monitor.Wait(budgetGate, 50);
                if (Interlocked.CompareExchange(ref job.State, BackgroundUpload.Reading, BackgroundUpload.Queued) != BackgroundUpload.Queued) continue;
                job.ReadTexels();
                long ahead = Interlocked.Add(ref readyBytes, job.Bytes);
                for (long peak = Interlocked.Read(ref readyPeak); ahead > peak; peak = Interlocked.Read(ref readyPeak))
                    if (Interlocked.CompareExchange(ref readyPeak, ahead, peak) == peak) break;
                Volatile.Write(ref job.State, BackgroundUpload.Ready);
                job.Read.Set();
                ready.Enqueue(job);
                worker.Wake();
            }
        }

        static void Spent(BackgroundUpload job)
        {
            Interlocked.Add(ref readyBytes, -job.Bytes);
            lock (budgetGate) Monitor.PulseAll(budgetGate);
        }

        /// The main thread needs the texture as it is (a draw samples it, or it is changed or
        /// released): a background upload not yet taken is taken, and recorded into the frame
        /// (`recordTo`), or dropped when null. One the GL thread took is done before any later frame.
        public static void Claim(DeviceTexture g, GlWorker recordTo)
        {
            var job = g.Background;
            g.Background = null;
            if (job == null) return;
            bool mine;
            if (Interlocked.CompareExchange(ref job.State, BackgroundUpload.Taken, BackgroundUpload.Queued) == BackgroundUpload.Queued)
            {
                mine = true;
                if (recordTo != null) job.ReadTexels();
                else
                {
                    job.Memory?.Free();
                    job.Memory = null;
                    job.Source = null;
                }
            }
            else
            {
                job.Read.Wait();
                mine = Interlocked.CompareExchange(ref job.State, BackgroundUpload.Taken, BackgroundUpload.Ready) == BackgroundUpload.Ready;
                if (mine) Spent(job);
            }
            if (!mine) return;
            Done(job);
            if (recordTo == null || job.Failed)
            {
                job.Upload.Memory?.Free();
                return;
            }
            GlTextures.PostUpload(recordTo, g, job.Upload);
            GlTextures.PostSampler(recordTo, g, job.Sampler);
        }

        static void Done(BackgroundUpload job)
        {
            // Reported when the queue runs empty after a large load (a scene's, a bundle's).
            if (Interlocked.Decrement(ref pending) != 0 || Interlocked.Read(ref queued) < 100) return;
            StartupStats.Mark($"background uploads drained: {Interlocked.Exchange(ref uploaded, 0)} of {Interlocked.Exchange(ref queued, 0)} textures " +
                              $"({Interlocked.Exchange(ref uploadedBytes, 0) >> 20} of {Interlocked.Exchange(ref queuedBytes, 0) >> 20} MiB) by the GL thread, " +
                              $"the longest {Interlocked.Exchange(ref longestTicks, 0) * 1000.0 / Stopwatch.Frequency:F0} ms");
        }

        /// One background upload (GL thread, no frame to execute); false when there was none.
        // The GPU is kept at most this far behind the uploads handed to it: a fence follows each
        // such amount, and no more is uploaded until it has passed. Without it Mesa's d3d12
        // driver let a backlog of uploads build up and then blocked one small upload for
        // seconds (the GL thread with it, and the frame waiting behind).
        const long InFlightBytes = 32L << 20;
        static long sinceFence;
        static GlSync fence;
        static bool fenced;
        const uint SyncGpuCommandsComplete = 0x9117, TimeoutExpired = 0x911B;

        /// The GPU has caught up with the uploads before the last fence (GL thread).
        static bool CaughtUp()
        {
            if (!fenced) return true;
            if (N.glClientWaitSync(fence, default, 0) == TimeoutExpired) return false;
            N.glDeleteSync(fence);
            fenced = false;
            return true;
        }

        static bool RunOne()
        {
            if (!CaughtUp()) return false;
            while (ready.TryDequeue(out var job))
            {
                if (Interlocked.CompareExchange(ref job.State, BackgroundUpload.Taken, BackgroundUpload.Ready) != BackgroundUpload.Ready) continue;
                Spent(job);
                long start = GlTimings.Now;
                if (!job.Failed)
                {
                    try
                    {
                        job.Upload.Execute(job.Device);
                        GlTextures.ApplySamplerNow(job.Sampler, job.Device);
                    }
                    catch (Exception e)
                    {
                        EngineLog.Engine($"background upload failed: {e.Message}");
                    }
                }
                else job.Upload.Memory?.Free();
                sinceFence += job.Bytes;
                if (sinceFence >= InFlightBytes)
                {
                    fence = N.glFenceSync(SyncGpuCommandsComplete, default);
                    N.glFlush();
                    fenced = true;
                    sinceFence = 0;
                }
                long ticks = GlTimings.Now - start;
                GlTimings.TextureUploads += ticks;
                Interlocked.Add(ref StartupStats.GlUploadTicks, ticks);
                Interlocked.Increment(ref uploaded);
                Interlocked.Add(ref uploadedBytes, job.Bytes);
                if (ticks > Interlocked.Read(ref longestTicks)) Interlocked.Exchange(ref longestTicks, ticks);
                if (ticks * 10 > Stopwatch.Frequency) StartupStats.Mark($"background upload of {job.Upload.Width}x{job.Upload.Height}x{job.Upload.Depth} {job.Upload.Format} ({job.Bytes >> 10} KiB, {job.Upload.MipCount} mips) took {ticks * 1000.0 / Stopwatch.Frequency:F0} ms");
                Done(job);
                return true;
            }
            return false;
        }
    }
}
