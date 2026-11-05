using System.Diagnostics;
using System.Threading.Tasks;
using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// With vsync the main thread waits before a frame until the frame maxQueuedFrames - 1
    /// before it has been through the presenter, while Swappy's pipeline-mode rule says the
    /// frame's stages fit a refresh one after the other: on once their mean plus a millisecond,
    /// and half again, fits the refresh over two seconds of frames; off once more than 10% of the
    /// frames miss their refresh; at least two seconds in each mode.
    public class QueuedFramesTests
    {
        static readonly long Period = Stopwatch.Frequency / 60, Ms = Stopwatch.Frequency / 1000;

        sealed class Clock { public long Now = 1; }

        /// `frames` frames one refresh apart, each needing `need`, the main thread deciding after
        /// each; returns whether the wait is on after the last.
        static bool Run(QueuedFrames q, Clock c, ref int frame, int frames, long need, long gap = 0)
        {
            bool on = false;
            for (int i = 0; i < frames; i++)
            {
                c.Now += gap > 0 ? gap : Period;
                q.Done(frame, need);
                q.WaitFor(frame++, 2, Period, out on);
            }
            return on;
        }

        static async Task<bool> Finishes(Task wait, int milliseconds)
            => await Task.WhenAny(wait, Task.Delay(milliseconds, TestContext.Current.CancellationToken)) == wait;

        /// As the original reads it back (measured: 2 by default, 0 stays 0, 20 reads 10).
        [Fact]
        public void MaxQueuedFramesReadsBackAsTheOriginal()
        {
            Assert.Equal(2, UnityEngine.QualitySettings.maxQueuedFrames);
            try
            {
                UnityEngine.QualitySettings.maxQueuedFrames = 0;
                Assert.Equal(0, UnityEngine.QualitySettings.maxQueuedFrames);
                UnityEngine.QualitySettings.maxQueuedFrames = 20;
                Assert.Equal(10, UnityEngine.QualitySettings.maxQueuedFrames);
            }
            finally { UnityEngine.QualitySettings.maxQueuedFrames = 2; }
        }

        [Fact]
        public void QuickFramesTurnItOnAfterTwoSeconds()
        {
            var c = new Clock();
            var q = new QueuedFrames(() => c.Now);
            int frame = 1;
            Assert.False(Run(q, c, ref frame, 120, 9 * Ms));
            Assert.True(Run(q, c, ref frame, 2, 9 * Ms));
            Assert.True(q.Drain());
            Assert.False(q.Drain());
        }

        [Fact]
        public void FramesNearARefreshKeepItOff()
        {
            var c = new Clock();
            var q = new QueuedFrames(() => c.Now);
            int frame = 1;
            // (10.2 + 1) * 1.5 > 16.7
            Assert.False(Run(q, c, ref frame, 600, 102 * Ms / 10));
        }

        [Fact]
        public void MissedRefreshesTurnItOffForTwoSeconds()
        {
            var c = new Clock();
            var q = new QueuedFrames(() => c.Now);
            int frame = 1;
            Assert.True(Run(q, c, ref frame, 125, 5 * Ms));
            q.Drain();
            // One frame in eight two refreshes after the one before: 12% missed.
            bool on = true;
            for (int i = 0; i < 20 && on; i++)
            {
                on = Run(q, c, ref frame, 7, 5 * Ms);
                if (on) on = Run(q, c, ref frame, 1, 5 * Ms, 2 * Period);
            }
            Assert.False(on);
            Assert.False(Run(q, c, ref frame, 100, 5 * Ms));
        }

        [Fact]
        public async Task WaitsForTheFrameBeforeOnceOn()
        {
            var c = new Clock();
            var q = new QueuedFrames(() => c.Now);
            int frame = 1;
            Assert.True(Run(q, c, ref frame, 125, 5 * Ms));
            int next = frame + 1;
            var wait = Task.Run(() => q.WaitFor(next, 2, Period, out _), TestContext.Current.CancellationToken);
            Assert.False(await Finishes(wait, 100));
            q.Done(frame, 5 * Ms);
            Assert.True(await Finishes(wait, 5000));
        }

        [Fact]
        public async Task AStoppedPresenterHoldsNothing()
        {
            var c = new Clock();
            var q = new QueuedFrames(() => c.Now);
            int frame = 1;
            Assert.True(Run(q, c, ref frame, 125, 5 * Ms));
            var wait = Task.Run(() => q.WaitFor(frame + 10, 2, Period, out _), TestContext.Current.CancellationToken);
            Assert.False(await Finishes(wait, 100));
            q.Stop();
            Assert.True(await Finishes(wait, 5000));
        }
    }
}
