using System;
using System.Threading;
using System.Threading.Tasks;
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
        public void CallersOnSeveralThreadsAndNestedCallsEachRunTheirOwnItems()
        {
            // Runs are reused as soon as their helpers are done with them: calls back to back,
            // at once on several threads and from inside an item, must not see each other's.
            int bad = 0;
            var threads = new Thread[4];
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    for (int round = 0; round < 200; round++)
                    {
                        int count = 1 + round % 37;
                        var runs = new int[count];
                        WorkerLoop.For(count, i =>
                        {
                            Interlocked.Increment(ref runs[i]);
                            if (i == 0 && round % 10 == 0)
                            {
                                var inner = new int[5];
                                WorkerLoop.For(inner.Length, j => Interlocked.Increment(ref inner[j]));
                                foreach (int n in inner)
                                    if (n != 1) Interlocked.Increment(ref bad);
                            }
                        });
                        foreach (int n in runs)
                            if (n != 1) Interlocked.Increment(ref bad);
                    }
                });
                threads[t].Start();
            }
            foreach (var t in threads) t.Join();
            Assert.Equal(0, bad);
        }

        [Fact]
        public void ACallAllocatesNothing()
        {
            var sum = new long[1];
            Action<int> body = i => Interlocked.Add(ref sum[0], i);
            long ours = MedianBytesACall(() => WorkerLoop.For(64, body));
            long theirs = MedianBytesACall(() => Parallel.For(0, 64, body));
            // Runs are reused thread-pool work items, queued as they are: a call allocates
            // nothing. (A call finding every run still held by helpers yet to start makes one,
            // 48 bytes; Parallel.For's about 2.5 KB.)
            Assert.True(ours < 16, $"WorkerLoop.For: {ours} bytes a call (median), none expected");
            // The measure sees Parallel.For's allocations, so it would see the loop's.
            long ceiling = 64 + 48L * WorkerLoop.MaxHelpers;
            Assert.True(theirs > ceiling, $"Parallel.For: {theirs} bytes a call (median), more than {ceiling} expected");
        }

        /// The median of what single calls allocate on the calling thread. On a loaded machine
        /// some calls also pay there for the pool's own work, up to tens of KB: starting a
        /// worker thread, or growing its queue when helpers start late. That is the pool's, not
        /// the call's, and the median leaves it out (an average over 100 calls did not).
        static long MedianBytesACall(Action call)
        {
            for (int i = 0; i < 50; i++) call();
            var bytes = new long[401];
            for (int i = 0; i < bytes.Length; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                call();
                bytes[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Array.Sort(bytes);
            return bytes[bytes.Length / 2];
        }
    }
}
