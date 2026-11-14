using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// The frame cap is paced against a running deadline: each frame is due a period after the
    /// one before was due, so frames that ran over (and sleeps that ended late) are made up for,
    /// with at most one period of debt carried.
    public class FramePaceTests
    {
        /// The host's loop: a frame of `work(i)` seconds, then the sleep the pace asks for when
        /// over a millisecond, which ends `late` seconds late. Returns each frame's start.
        static List<double> Run(int rate, int frames, Func<int, double> work, double late = 0.0005, Func<int, int> rateAt = null)
        {
            var pace = new FramePace();
            var starts = new List<double>();
            double now = 100;
            for (int i = 0; i < frames; i++)
            {
                double start = now;
                starts.Add(start);
                now += work(i);
                double remaining = pace.Remaining(now, start, rateAt?.Invoke(i) ?? rate);
                if (remaining > 0.001) now += remaining + late;
            }
            starts.Add(now);
            return starts;
        }

        static double Fps(List<double> starts, int from = 0) => (starts.Count - 1 - from) / (starts[^1] - starts[from]);

        /// A sleep ends about half a millisecond late; sleeping for what was left of each frame
        /// lost it every frame (a cap of 60: 58.3).
        [Theory]
        [InlineData(30)]
        [InlineData(60)]
        [InlineData(120)]
        public void LateSleepsDoNotLowerTheRate(int rate)
        {
            var starts = Run(rate, 1200, _ => 0.004);
            Assert.InRange(Fps(starts), rate - 0.05, rate + 0.05);
        }

        /// One frame in ten over the period, as a colony's sim-step frames (measured: a cap of
        /// 60 at 57.0 fps): the frames after each sleep less.
        [Fact]
        public void FramesOverThePeriodAreMadeUpFor()
        {
            var starts = Run(60, 1200, i => i % 10 == 0 ? 0.024 : 0.008);
            Assert.InRange(Fps(starts), 59.95, 60.05);
            // Never a frame without work's time between starts, never one longer than the long frame.
            var gaps = starts.Zip(starts.Skip(1), (a, b) => b - a).ToList();
            Assert.True(gaps.Min() >= 0.008 - 1e-9);
            Assert.True(gaps.Max() <= 0.024 + 1e-9);
        }

        /// After a stall the debt is one period: one frame's worth is caught up, no run of frames
        /// without their sleep.
        [Fact]
        public void AStallIsNotCaughtUp()
        {
            var starts = Run(60, 400, i => i == 100 ? 2.0 : 0.004, late: 0);
            double after = starts[101];
            int inNextSecond = starts.Count(s => s >= after && s < after + 1.0);
            Assert.InRange(inNextSecond, 60, 62);
            // Only the first frame after the stall goes without a sleep.
            Assert.Equal(0.004, starts[102] - starts[101], 6);
            for (int i = 103; i < 399; i++) Assert.InRange(starts[i + 1] - starts[i], 1.0 / 60 - 0.0131, 1.0 / 60 + 1e-6);
            Assert.Equal(1, Enumerable.Range(102, 290).Count(i => starts[i + 1] - starts[i] < 1.0 / 60 - 1e-6));
        }

        /// Frames that never fit the period run without sleeps and build no more debt.
        [Fact]
        public void FramesSlowerThanTheCapAreNotSleptFor()
        {
            var starts = Run(120, 300, _ => 0.012);
            Assert.InRange(Fps(starts), 83.2, 83.4);
            // ... and when they fit again, one frame goes without its sleep and the cap holds.
            var pace = new FramePace();
            double now = 0;
            for (int i = 0; i < 100; i++) { double start = now; now += 0.012; Assert.True(pace.Remaining(now, start, 120) <= 0); }
            double s2 = now; now += 0.002;
            Assert.Equal(-0.002, pace.Remaining(now, s2, 120), 6);
            double s3 = now; now += 0.002;
            Assert.Equal(1.0 / 120 - 0.004, pace.Remaining(now, s3, 120), 6);
        }

        /// A new rate, or the first capped frame after frames without a cap, counts from that
        /// frame's start.
        [Fact]
        public void ANewRateCountsFromItsFrame()
        {
            var pace = new FramePace();
            Assert.Equal(1.0 / 60 - 0.004, pace.Remaining(10.004, 10, 60), 9);
            Assert.Equal(0, pace.Remaining(11, 10.9, 0));
            Assert.Equal(0, pace.Remaining(50, 49.9, -1));
            Assert.Equal(1.0 / 60 - 0.004, pace.Remaining(60.004, 60, 60), 9);
            Assert.Equal(1.0 / 30 - 0.004, pace.Remaining(70.004, 70, 30), 9);
        }

        /// The rate changing mid-run (the game sets targetFrameRate): each stretch at its rate.
        [Fact]
        public void EachStretchRunsAtItsRate()
        {
            var starts = Run(0, 900, _ => 0.003, rateAt: i => i < 300 ? 30 : i < 600 ? 120 : 60);
            Assert.InRange(299 / (starts[299] - starts[0]), 29.95, 30.05);
            Assert.InRange(299 / (starts[599] - starts[300]), 119.5, 120.5);
            Assert.InRange(299 / (starts[899] - starts[600]), 59.9, 60.1);
        }
    }
}
