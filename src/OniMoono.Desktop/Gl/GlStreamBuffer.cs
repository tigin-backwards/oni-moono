using System;
using System.Collections.Generic;
using Brutal.OpenGlApi;
using N = Brutal.OpenGlApi.OpenGl.Native;

namespace OniMoono.Desktop.Gl
{
    /// Per-draw data streamed to the GPU (uniform blocks) through one ring
    /// buffer that stays mapped for the device's lifetime (ARB_buffer_storage, persistent and
    /// coherent). Data is copied straight into the mapping; a fence per frame records which
    /// part of the ring the GPU may still read, and a region is reused only after the frames
    /// that used it have completed. The storage is never respecified, so no orphaned
    /// allocations are left for the driver to recycle and writes never wait on the driver's
    /// own tracking of the buffer.
    internal sealed unsafe class GlStreamBuffer
    {
        const uint MapWrite = 0x0002, MapPersistent = 0x0040, MapCoherent = 0x0080;
        const uint SyncGpuCommandsComplete = 0x9117, SyncFlushCommands = 0x0001, TimeoutExpired = 0x911B;
        const ulong WaitSliceNanoseconds = 100_000_000;

        readonly long size;
        readonly int alignment;
        readonly byte* mapping;
        public readonly uint Id;

        /// Bytes handed out since creation; a position modulo the size is a ring offset.
        long head;
        long frameStart;
        /// Fenced ranges [Start, End) of positions the GPU may still be reading, oldest first.
        readonly Queue<(GlSync Fence, long Start, long End)> pending = new();

        public GlStreamBuffer(uint target, int size, int alignment)
        {
            this.size = size;
            this.alignment = Math.Max(1, alignment);
            Id = GlApi.GenBuffer();
            N.glBindBuffer(target, new GlBuffer(Id));
            N.glBufferStorage(target, size, null, MapWrite | MapPersistent | MapCoherent);
            mapping = (byte*)N.glMapBufferRange(target, 0, size, MapWrite | MapPersistent | MapCoherent);
            N.glBindBuffer(target, default);
            if (mapping == null) throw new InvalidOperationException("A persistent stream buffer could not be mapped.");
            VideoMemoryLedger.Add("stream buffers", size, 1);
        }

        /// The largest single write.
        public int MaxWrite => (int)(size / 4);

        /// Copies the bytes into the ring and returns their offset in the buffer.
        public int Write(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data) return Write(p, data.Length);
        }

        public int Write(byte* data, int length)
        {
            if (length > MaxWrite) throw new ArgumentOutOfRangeException(nameof(length));
            long start = (head + alignment - 1) / alignment * alignment;
            if (start % size + length > size) start = (start / size + 1) * size;
            long end = start + length;
            // The bytes about to be overwritten were last written one lap earlier.
            long reused = end - size;
            if (reused > frameStart)
            {
                // This frame alone has gone round the ring: fence what it has written so far.
                FenceFrame();
            }
            while (pending.Count > 0 && pending.Peek().Start < reused)
            {
                var (fence, _, _) = pending.Dequeue();
                var wait = new OniMoono.Engine.LongWait();
                while (N.glClientWaitSync(fence, SyncFlushCommands, WaitSliceNanoseconds) == TimeoutExpired)
                    wait.Slice("stream buffer: a ring region's fence", $"sync 0x{System.Runtime.CompilerServices.Unsafe.As<GlSync, nint>(ref fence):x}, buffer {Id}, bytes to {reused}");
                wait.End();
                N.glDeleteSync(fence);
            }
            Buffer.MemoryCopy(data, mapping + start % size, length, length);
            head = end;
            return (int)(start % size);
        }

        /// Marks the end of the commands that read what was written so far.
        public void FenceFrame()
        {
            if (head == frameStart) return;
            pending.Enqueue((N.glFenceSync(SyncGpuCommandsComplete, default), frameStart, head));
            frameStart = head;
        }
    }
}
