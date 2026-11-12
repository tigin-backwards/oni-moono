using System;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // The texel-copy check (ONI_VERIFY_TEXEL_COPIES) looks for copies that read texels while
    // they were being written. A write that first waits for the copy (every engine access does, as
    // the font atlas's glyph writes) is not one; reported as one, it made a QA run fail falsely.
    public class TexelCopyTests : EngineTest
    {
        [Fact]
        public void AWriteThatWaitsForTheCopyIsNotATornCopy()
        {
            TexelCopies.Verify = true;
            try
            {
                long before = TexelCopies.Torn;
                var texture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                var native = (Texture2DNative)texture.NativeOrThrow;
                var source = native.Memory;
                byte original = source.Span[0];
                var target = new PixelMemory(source.Size);
                TexelCopies.Start(native, source, target);
                native.Memory.Span[0] = (byte)(original + 1); // waits for the copy first
                TexelCopies.Fence();
                Assert.Equal(before, TexelCopies.Torn);
                Assert.Equal(original, target.Span[0]);
            }
            finally
            {
                TexelCopies.Verify = false;
            }
        }

        [Fact]
        public void CopiesAreMadeAndReusedWithoutAllocating()
        {
            var texture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            var native = (Texture2DNative)texture.NativeOrThrow;
            var targets = new PixelMemory[4];
            for (int i = 0; i < targets.Length; i++) targets[i] = new PixelMemory(native.Memory.Size);
            void Round(byte value)
            {
                native.Memory.Span.Fill(value);
                foreach (var target in targets) TexelCopies.Start(native, native.Memory, target);
                TexelCopies.Fence();
                foreach (var target in targets) Assert.True(target.Span.IndexOfAnyExcept(value) < 0);
            }
            for (int round = 0; round < 20; round++) Round((byte)round);
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 100; round++) Round((byte)(round + 20));
            long perCopy = (System.GC.GetAllocatedBytesForCurrentThread() - before) / (100 * targets.Length);
            Assert.True(perCopy < 8, $"{perCopy} bytes a copy");
        }

        [Fact]
        public void AHandleKeptPastTheFenceStaysDoneWhenItsWorkItemIsReused()
        {
            var texture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            var native = (Texture2DNative)texture.NativeOrThrow;
            var kept = TexelCopies.Start(native, native.Memory, new PixelMemory(native.Memory.Size));
            Assert.False(kept.IsNone);
            TexelCopies.Fence();
            Assert.True(kept.IsCompleted);
            var next = TexelCopies.Start(native, native.Memory, new PixelMemory(native.Memory.Size));
            Assert.True(kept.IsCompleted);
            kept.Wait();
            next.Wait(); // made here if no pool thread has taken it
            Assert.True(next.IsCompleted);
            TexelCopies.Fence();
        }

        [Fact]
        public void AnotherThreadWaitingOnCopiesSeesThemWholeAcrossFences()
        {
            // As the device's thread: it waits for each upload's copy, sometimes a frame late,
            // while the main thread fences and reuses the work items.
            var texture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            var native = (Texture2DNative)texture.NativeOrThrow;
            var queue = new System.Collections.Concurrent.BlockingCollection<(TexelCopy Copy, PixelMemory Target, byte Value)>();
            int bad = 0;
            var device = new System.Threading.Thread(() =>
            {
                foreach (var (copy, target, value) in queue.GetConsumingEnumerable(TestContext.Current.CancellationToken))
                {
                    copy.Wait();
                    if (target.Span[0] != value || target.Span[^1] != value) System.Threading.Interlocked.Increment(ref bad);
                }
            });
            device.Start();
            var targets = new PixelMemory[24];
            for (int i = 0; i < targets.Length; i++) targets[i] = new PixelMemory(native.Memory.Size);
            for (int frame = 0; frame < 300; frame++)
            {
                byte value = (byte)frame;
                native.Memory.Span.Fill(value);
                // Six copies a frame, into targets the device is done with by the time they come round.
                for (int i = 0; i < 6; i++)
                {
                    var target = targets[(frame % 4) * 6 + i];
                    queue.Add((TexelCopies.Start(native, native.Memory, target), target, value), TestContext.Current.CancellationToken);
                }
                TexelCopies.Fence();
                while (queue.Count > 12) System.Threading.Thread.Yield();
            }
            queue.CompleteAdding();
            device.Join();
            Assert.Equal(0, bad);
        }

        [Fact]
        public void ASmallCopyIsMadeAtOnce()
        {
            var texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            var native = (Texture2DNative)texture.NativeOrThrow;
            native.Memory.Span.Fill(7);
            var target = new PixelMemory(native.Memory.Size);
            Assert.True(TexelCopies.Start(native, native.Memory, target).IsNone);
            Assert.Equal(7, target.Span[0]);
        }
    }
}
