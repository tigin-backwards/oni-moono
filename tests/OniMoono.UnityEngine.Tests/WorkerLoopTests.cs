using System;
using System.Threading;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// fast-anim-batches' loop over the dirty batches (src/OniMoono.GameTweaks/WorkerLoop.cs,
    /// compiled in here): every item runs once, the call returns when all have, and after an
    /// exception no item starts, as with Parallel.For.
    public class WorkerLoopTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(7)]
        [InlineData(500)]
        public void EveryItemRunsOnceBeforeTheCallReturns(int count)
        {
            for (int round = 0; round < 50; round++)
            {
                var runs = new int[count];
                WorkerLoop.For(count, i =>
                {
                    if (i % 7 == 0) Thread.SpinWait(200);
                    Interlocked.Increment(ref runs[i]);
                });
                Assert.All(runs, n => Assert.Equal(1, n));
            }
        }

        [Fact]
        public void NoItemStartsAfterAFailure()
        {
            // The caller alone: items run in order, and none after the one that throws.
            int saved = WorkerLoop.MaxHelpers;
            try
            {
                WorkerLoop.MaxHelpers = 0;
                var runs = new int[64];
                var e = Assert.Throws<AggregateException>(() => WorkerLoop.For(runs.Length, i =>
                {
                    runs[i]++;
                    if (i == 3) throw new InvalidOperationException();
                }));
                Assert.IsType<InvalidOperationException>(Assert.Single(e.InnerExceptions));
                Assert.Equal(new[] { 1, 1, 1, 1 }, runs[..4]);
                Assert.All(runs[4..], n => Assert.Equal(0, n));
            }
            finally
            {
                WorkerLoop.MaxHelpers = saved;
            }
        }

        [Fact]
        public void ExceptionsComeOutTogetherAndNoItemRunsTwice()
        {
            for (int round = 0; round < 50; round++)
            {
                var runs = new int[256];
                var e = Assert.Throws<AggregateException>(() => WorkerLoop.For(runs.Length, i =>
                {
                    Interlocked.Increment(ref runs[i]);
                    Thread.SpinWait(50);
                    if (i % 16 == 3) throw new InvalidOperationException(i.ToString());
                }));
                Assert.NotEmpty(e.InnerExceptions);
                Assert.All(e.InnerExceptions, x => Assert.IsType<InvalidOperationException>(x));
                Assert.All(runs, n => Assert.InRange(n, 0, 1));
                // Each exception is from an item that ran.
                Assert.All(e.InnerExceptions, x => Assert.Equal(1, runs[int.Parse(x.Message)]));
            }
        }

        [Fact]
        public void TheCallerFinishesAloneWhenNoHelperStarts()
        {
            // No helper queued: the calling thread runs every item and does not wait.
            int saved = WorkerLoop.MaxHelpers;
            try
            {
                WorkerLoop.MaxHelpers = 0;
                int thread = Environment.CurrentManagedThreadId, elsewhere = 0, ran = 0;
                WorkerLoop.For(100, _ =>
                {
                    Interlocked.Increment(ref ran);
                    if (Environment.CurrentManagedThreadId != thread) Interlocked.Increment(ref elsewhere);
                });
                Assert.Equal(100, ran);
                Assert.Equal(0, elsewhere);
            }
            finally
            {
                WorkerLoop.MaxHelpers = saved;
            }
        }

        [Fact]
        public void ACallAllocatesFarLessThanParallelFor()
        {
            var sum = new long[1];
            Action<int> body = i => Interlocked.Add(ref sum[0], i);
            for (int i = 0; i < 20; i++) WorkerLoop.For(64, body);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) WorkerLoop.For(64, body);
            long ours = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
            // The run and one pool record a helper queued.
            Assert.True(ours <= 64 + 48L * WorkerLoop.MaxHelpers, $"{ours} bytes a call");
        }
    }
}
