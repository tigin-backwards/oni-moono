using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
    /// Test runs (ONI_VERIFY_TEXEL_COPIES=1): each source is checksummed when its copy starts, and
    /// the copy as soon as it is made; a difference means the copy read texels being written (a
    /// torn copy) and is logged at the fence. A write after the copy, which waited for it (every
    /// engine access does), changes neither.
    internal static class TexelCopies
    {
        /// Below this a copy is cheaper than handing it to another thread.
        const long AsyncThreshold = 64 << 10;

        static readonly List<Task> pending = new();
        internal static bool Verify = Environment.GetEnvironmentVariable("ONI_VERIFY_TEXEL_COPIES") == "1";
        static readonly List<(TexelTextureNative Texture, ulong Sum, Task<ulong> Copied)> checks = new();
        static long verified, torn;

        /// Copies found torn so far (verify mode).
        internal static long Torn => torn;

        /// Copies `source` (the texture's texels) into `target`; returns the copy in progress, or
        /// null when it was made at once.
        public static Task Start(TexelTextureNative texture, PixelMemory source, PixelMemory target)
        {
            if (source.Size < AsyncThreshold)
            {
                source.Span.CopyTo(target.Span);
                return null;
            }
            Task copy;
            if (Verify)
            {
                ulong sum = Checksum(source);
                var copied = Task.Run(() => { source.Span.CopyTo(target.Span); return Checksum(target); });
                checks.Add((texture, sum, copied));
                copy = copied;
            }
            else copy = Task.Run(() => source.Span.CopyTo(target.Span));
            texture.PendingCopy = copy;
            pending.Add(copy);
            return copy;
        }

        /// Every copy finished: called at the start of a frame, before scripts run.
        public static void Fence()
        {
            if (pending.Count == 0) return;
            // A copy that has not run in 10 s is reported, with the pool's own counts, and waited
            // for still: a copy the pool lost (a worker that could not start, under memory
            // pressure) once kept the frame waiting here silently.
            foreach (var copy in pending)
                for (int seconds = 10; !copy.IsCompleted && !Finished(copy, 10_000); seconds += 10)
                    if (seconds == 10 || seconds % 60 == 0)
                        EngineLog.Engine($"texel copies: a copy still {copy.Status} after {seconds} s ({pending.Count(c => !c.IsCompleted)} of {pending.Count} unfinished); pool: {ThreadPoolState.Describe()}");
            Task.WaitAll(pending);
            pending.Clear();
            if (!Verify) return;
            foreach (var (texture, sum, copied) in checks)
            {
                verified++;
                if (copied.Result != sum) EngineLog.Engine($"texel copy: {texture.Name} was written while it was copied ({++torn} of {verified})");
            }
            if (verified % 20000 < checks.Count) EngineLog.Engine($"texel copies: {verified} verified, {torn} changed while copying");
            checks.Clear();
        }

        /// Whether the copy finished within the time, faulted or not (WaitAll then throws for it).
        static bool Finished(Task copy, int milliseconds)
        {
            try
            {
                return copy.Wait(milliseconds);
            }
            catch (AggregateException)
            {
                return true;
            }
        }

        static unsafe ulong Checksum(PixelMemory memory)
        {
            ulong h = 1469598103934665603UL;
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(memory.Span);
            foreach (var w in words) h = (h ^ w) * 1099511628211UL;
            return h;
        }
    }
}
