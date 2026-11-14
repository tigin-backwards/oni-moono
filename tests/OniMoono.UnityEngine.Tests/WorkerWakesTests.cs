using System;
using System.Threading;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The fast-jobs game tweak's worker wakes (src/OniMoono.GameTweaks/WorkerWakes.cs, compiled
    /// in here): Wake(n) wakes the first n workers once each, and a wake given while a worker is
    /// busy waits for it, as the one shared semaphore's count did.
    public class WorkerWakesTests
    {
        [Fact]
        public void WakeReachesTheFirstWorkersOnly()
        {
            var wakes = new WorkerWakes(4);
            wakes.Wake(2);
            Assert.True(wakes.Pending(0));
            Assert.True(wakes.Pending(1));
            Assert.False(wakes.Pending(2));
            Assert.False(wakes.Pending(3));
        }

        [Fact]
        public void WakeOfMoreThanAllWakesEachOnce()
        {
            var wakes = new WorkerWakes(3);
            wakes.Wake(10);
            for (int i = 0; i < 3; i++)
            {
                wakes.Wait(i);
                Assert.False(wakes.Pending(i));
            }
        }

        [Fact]
        public void WakesGivenWhileBusyAreKept()
        {
            var wakes = new WorkerWakes(2);
            wakes.Wake(1);
            wakes.Wake(2);
            wakes.Wait(0);
            wakes.Wait(0);
            wakes.Wait(1);
            Assert.False(wakes.Pending(0));
            Assert.False(wakes.Pending(1));
        }

        [Fact]
        public void WaitingWorkersTakeEveryWake()
        {
            const int workers = 8, rounds = 200;
            var wakes = new WorkerWakes(workers);
            var counts = new int[workers];
            var threads = new Thread[workers];
            for (int w = 0; w < workers; w++)
            {
                int index = w;
                threads[w] = new Thread(() =>
                {
                    for (int r = 0; r < rounds; r++)
                    {
                        wakes.Wait(index);
                        Interlocked.Increment(ref counts[index]);
                    }
                })
                { IsBackground = true };
                threads[w].Start();
            }
            for (int r = 0; r < rounds; r++) wakes.Wake(workers);
            foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(30)));
            Assert.All(counts, c => Assert.Equal(rounds, c));
        }
    }

    /// The fast-jobs game tweak's batch hand-over (BatchHandOver, in the same file): a batch is
    /// the spare again only once the caller and every worker it was handed to are done with it.
    public class BatchHandOverTests
    {
        sealed class Batch : HeldBatch
        {
            public int Run, Next = -1, Count, Remaining;
            public int[] Seen;
        }

        [Fact]
        public void TheSpareIsTheBatchEveryHolderGaveBack()
        {
            var batches = new BatchHandOver<Batch>(3);
            Assert.Null(batches.Spare());
            var batch = new Batch();
            batches.Hand(batch, 2);
            Assert.Same(batch, batches.Take(0));
            Assert.Null(batches.Take(0)); // taken once
            Assert.Null(batches.Take(2)); // not handed to the third worker
            batches.Release(batch);       // worker 0
            batches.Release(batch);       // the caller
            Assert.Null(batches.Spare()); // worker 1 has not taken it
            Assert.Same(batch, batches.Take(1));
            Assert.Null(batches.Spare());
            batches.Release(batch);
            Assert.Same(batch, batches.Spare());
            Assert.Null(batches.Spare());
        }

        [Fact]
        public void ABatchAWorkerNeverTookIsGivenBackByTheNextHandOver()
        {
            var batches = new BatchHandOver<Batch>(2);
            Batch first = new Batch(), second = new Batch();
            batches.Hand(first, 2);
            batches.Release(first);       // the caller; both workers are still on their way
            Assert.Null(batches.Spare());
            batches.Hand(second, 1);      // replaces worker 0's
            Assert.Null(batches.Spare()); // worker 1 may still take the first
            Assert.Same(second, batches.Take(0));
            Assert.Same(first, batches.Take(1));
            batches.Release(first);
            Assert.Same(first, batches.Spare());
            batches.Release(second);
            Assert.Null(batches.Spare()); // the second's caller
            batches.Release(second);
            Assert.Same(second, batches.Spare());
        }

        [Fact]
        public void ABatchWithoutHelpersIsTheSpareAfterItsRun()
        {
            var batches = new BatchHandOver<Batch>(4);
            var batch = new Batch();
            for (int run = 0; run < 3; run++)
            {
                batches.Hand(batch, 0);
                batches.Release(batch);
                Assert.Same(batch, batches.Spare());
            }
        }

        /// The scheduler's use, with workers that wake late: every item of every run runs once,
        /// in its own run, while batches are used again.
        [Fact]
        public void LateWorkersNeverTakeItemsOfALaterRun()
        {
            const int Workers = 3, Runs = 20000;
            var batches = new BatchHandOver<Batch>(Workers);
            var wakes = new WorkerWakes(Workers);
            int wrong = 0, made = 0;
            bool stop = false;

            void Work(Batch b)
            {
                int run = Volatile.Read(ref b.Run), i;
                while ((i = Interlocked.Increment(ref b.Next)) < b.Count)
                {
                    if (Volatile.Read(ref b.Run) != run || Interlocked.Increment(ref b.Seen[i]) != 1) Interlocked.Increment(ref wrong);
                    Interlocked.Decrement(ref b.Remaining);
                }
            }

            var threads = new Thread[Workers];
            for (int w = 0; w < Workers; w++)
            {
                int worker = w;
                threads[w] = new Thread(() =>
                {
                    var random = new Random(worker);
                    while (true)
                    {
                        wakes.Wait(worker);
                        if (Volatile.Read(ref stop)) return;
                        if (random.Next(4) == 0) Thread.Yield(); // a late wake
                        var b = batches.Take(worker);
                        if (b == null) continue;
                        Work(b);
                        batches.Release(b);
                    }
                });
                threads[w].IsBackground = true;
                threads[w].Start();
            }
            var counts = new Random(7);
            for (int run = 1; run <= Runs; run++)
            {
                int count = 1 + counts.Next(6);
                var b = batches.Spare();
                if (b == null) { b = new Batch(); made++; }
                b.Count = count;
                b.Next = -1;
                b.Seen = new int[count];
                Volatile.Write(ref b.Remaining, count);
                Volatile.Write(ref b.Run, run);
                int helpers = Math.Min(Workers, count - 1);
                batches.Hand(b, helpers);
                wakes.Wake(helpers);
                Work(b);
                var spin = new SpinWait();
                while (Volatile.Read(ref b.Remaining) != 0) spin.SpinOnce();
                foreach (int seen in b.Seen) if (seen != 1) wrong++;
                batches.Release(b);
            }
            Volatile.Write(ref stop, true);
            wakes.Wake(Workers);
            foreach (var t in threads) Assert.True(t.Join(10000));
            Assert.Equal(0, wrong);
            Assert.True(made < Runs / 2, $"{made} batches made for {Runs} runs");
        }
    }
}

