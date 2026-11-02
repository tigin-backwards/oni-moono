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
                }) { IsBackground = true };
                threads[w].Start();
            }
            for (int r = 0; r < rounds; r++) wakes.Wake(workers);
            foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(30)));
            Assert.All(counts, c => Assert.Equal(rounds, c));
        }
    }
}
