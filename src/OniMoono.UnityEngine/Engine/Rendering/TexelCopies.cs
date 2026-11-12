using System;
using System.Collections.Generic;
using System.Threading;

namespace OniMoono.Engine.Rendering
{
    /// The device's snapshots of readable textures' texels, copied on worker threads. A large
    /// colony re-applies about 20 MB of textures a frame (its animation batches); the game keeps
    /// raw pointers into that memory and rewrites it every frame, so the device needs its own copy,
    /// but nothing requires the main thread to make it.
    ///
    /// A copy starts when the device records the upload (during rendering) and must be finished
    /// before the texels can change again. Every engine access to a texture's texels waits for its
    /// copy (TexelTextureNative.Memory and the methods replacing the memory), and the next frame
    /// waits for all of them before any script runs (Fence): the game writes these textures from
    /// its scripts. The device waits for a copy before uploading it.
    ///
    /// A copy is a work item of the thread pool's, reused once the fence has seen it finished, so
    /// copies allocate nothing once there are enough of them. Whichever thread comes first makes
    /// it: a pool thread, or one that waits for it (the fence, an engine access, the device). So
    /// no wait depends on a pool thread starting: the fence makes every copy no pool thread has
    /// taken yet itself.
    ///
    /// Test runs (ONI_VERIFY_TEXEL_COPIES=1): each source is checksummed when its copy starts, and
    /// the copy as soon as it is made; a difference means the copy read texels being written (a
    /// torn copy) and is logged at the fence. A write after the copy, which waited for it (every
    /// engine access does), changes neither.
    internal static class TexelCopies
    {
        /// Below this a copy is cheaper than handing it to another thread.
        const long AsyncThreshold = 64 << 10;

        /// Copies started since the last fence, and finished ones to reuse (main thread).
        static readonly List<Copy> pending = new();
        static readonly Stack<Copy> idle = new();
        internal static bool Verify = Environment.GetEnvironmentVariable("ONI_VERIFY_TEXEL_COPIES") == "1";
        static long verified, torn;

        /// Copies found torn so far (verify mode).
        internal static long Torn => torn;

        /// Copies `source` (the texture's texels) into `target`; returns the copy in progress, or
        /// none (IsCompleted) when it was made at once.
        public static TexelCopy Start(TexelTextureNative texture, PixelMemory source, PixelMemory target)
        {
            if (source.Size < AsyncThreshold)
            {
                source.Span.CopyTo(target.Span);
                return default;
            }
            var copy = idle.Count > 0 ? idle.Pop() : new Copy();
            var started = copy.Begin(texture, source, target, Verify);
            texture.PendingCopy = started;
            pending.Add(copy);
            ThreadPool.UnsafeQueueUserWorkItem(copy, preferLocal: false);
            return started;
        }

        /// Every copy finished: called at the start of a frame, before scripts run.
        public static void Fence()
        {
            if (pending.Count == 0) return;
            // Copies no pool thread has taken yet are made here: none waits for a thread to start.
            foreach (var copy in pending) copy.TryMake();
            // A copy that has not finished in 10 s is reported, with the pool's own counts, and
            // waited for still: a copy taken by a thread that then stalled.
            foreach (var copy in pending)
                for (int seconds = 10; !copy.WaitFinished(10_000); seconds += 10)
                    if (seconds == 10 || seconds % 60 == 0)
                        EngineLog.Engine($"texel copies: a copy still unfinished after {seconds} s ({Unfinished()} of {pending.Count} unfinished); pool: {ThreadPoolState.Describe()}");
            List<Exception> errors = null;
            foreach (var copy in pending)
            {
                if (copy.Error != null) (errors ??= new()).Add(copy.Error);
                if (Verify)
                {
                    verified++;
                    if (copy.Error == null && copy.CopiedSum != copy.SourceSum)
                        EngineLog.Engine($"texel copy: {copy.Texture.Name} was written while it was copied ({++torn} of {verified})");
                }
                copy.End();
                idle.Push(copy);
            }
            if (Verify && verified % 20000 < pending.Count) EngineLog.Engine($"texel copies: {verified} verified, {torn} changed while copying");
            pending.Clear();
            // As Task.WaitAll did for the copies' tasks.
            if (errors != null) throw new AggregateException(errors);
        }

        static int Unfinished()
        {
            int n = 0;
            foreach (var copy in pending)
                if (!copy.Finished)
                    n++;
            return n;
        }

        /// One copy at a time, again and again: each Begin starts a generation, and a handle
        /// (TexelCopy) names the generation it waits for, so a handle kept past the fence (the
        /// device's, for an upload it runs later) finds its copy finished whatever the work item
        /// does next.
        internal sealed class Copy : IThreadPoolWorkItem
        {
            PixelMemory source, target;
            bool checksum;
            /// The copy in hand (written by the main thread before the copy is queued) and the
            /// last one finished; whether a thread has taken the one in hand.
            long generation, finished;
            int taken = 1;
            public Exception Error;
            public TexelTextureNative Texture;
            public ulong SourceSum, CopiedSum;

            public bool Finished => HasFinished(Volatile.Read(ref generation));

            /// Main thread, on a copy no thread holds (new, or idle since the fence).
            public TexelCopy Begin(TexelTextureNative texture, PixelMemory source, PixelMemory target, bool verify)
            {
                this.source = source;
                this.target = target;
                checksum = verify;
                Texture = verify ? texture : null;
                SourceSum = verify ? Checksum(source) : 0;
                CopiedSum = 0;
                Error = null;
                long g = Volatile.Read(ref generation) + 1;
                Volatile.Write(ref generation, g);
                // Last: from here a thread can take the copy (also through a stale queue entry
                // of this item's, left by a copy a waiting thread made first), and it sees the
                // fields above.
                Volatile.Write(ref taken, 0);
                return new TexelCopy(this, g);
            }

            /// Main thread, after the fence: the memories are let go of.
            public void End()
            {
                source = target = null;
                Texture = null;
            }

            void IThreadPoolWorkItem.Execute() => TryMake();

            /// Makes the copy on this thread unless another has taken it.
            public void TryMake()
            {
                if (Interlocked.CompareExchange(ref taken, 1, 0) != 0) return;
                long g = Volatile.Read(ref generation);
                try
                {
                    source.Span.CopyTo(target.Span);
                    if (checksum) CopiedSum = Checksum(target);
                }
                catch (Exception e)
                {
                    Error = e;
                }
                lock (this)
                {
                    Volatile.Write(ref finished, g);
                    Monitor.PulseAll(this);
                }
            }

            /// Whether generation `g` has finished.
            public bool HasFinished(long g) => Volatile.Read(ref finished) >= g;

            /// Waits for generation `g`, making it on this thread if no other has taken it.
            public void Wait(long g)
            {
                if (!HasFinished(g))
                {
                    if (Volatile.Read(ref generation) == g) TryMake();
                    lock (this)
                        while (!HasFinished(g))
                            Monitor.Wait(this);
                }
                // As a failed task's Wait: the copy's exception (the fence reports it too).
                if (Error is { } e && Volatile.Read(ref generation) == g) throw new AggregateException(e);
            }

            /// Waits for the copy in hand up to `milliseconds` (main thread); whether it finished.
            public bool WaitFinished(int milliseconds)
            {
                long g = Volatile.Read(ref generation);
                if (HasFinished(g)) return true;
                lock (this)
                    if (!HasFinished(g))
                        Monitor.Wait(this, milliseconds);
                return HasFinished(g);
            }
        }

        static ulong Checksum(PixelMemory memory)
        {
            ulong h = 1469598103934665603UL;
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(memory.Span);
            foreach (var w in words) h = (h ^ w) * 1099511628211UL;
            return h;
        }
    }

    /// A texel copy that may still be in progress (TexelCopies.Start); the default one is none.
    internal readonly struct TexelCopy
    {
        readonly TexelCopies.Copy copy;
        readonly long generation;

        internal TexelCopy(TexelCopies.Copy copy, long generation)
        {
            this.copy = copy;
            this.generation = generation;
        }

        public bool IsNone => copy == null;

        public bool IsCompleted => copy == null || copy.HasFinished(generation);

        /// Waits for the copy, making it on this thread if no other has taken it yet; throws
        /// for a failed one (and so does the fence).
        public void Wait() => copy?.Wait(generation);
    }
}
