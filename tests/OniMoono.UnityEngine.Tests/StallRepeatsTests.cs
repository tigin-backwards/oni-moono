using System.Diagnostics;
using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// The presenter's repeats while no frame comes (StallRepeats): none while frames come as
    /// usual, the first after three times the usual frame time (at least 50 ms), then one every usual
    /// frame time (1/60 to 1/30 s) until a frame comes.
    public class StallRepeatsTests
    {
        static readonly long F = Stopwatch.Frequency;
        static long Ms(double ms) => (long)(ms * F / 1000);

        /// Frames `interval` ms apart from t = 1 s; returns the time of the last.
        static long Frames(StallRepeats s, int count, double interval, double jitter = 0)
        {
            long t = F;
            for (int i = 0; i < count; i++)
            {
                t += Ms(interval + (i % 2 == 0 ? jitter : -jitter));
                s.Frame(t);
            }
            return t;
        }

        [Fact]
        public void NothingIsDueBeforeTheFirstFrame()
        {
            var s = new StallRepeats();
            Assert.Equal(long.MaxValue, s.Due);
            Assert.True(s.WaitMilliseconds(F) > 1_000_000);
        }

        [Fact]
        public void FramesAsUsualNeverMeetARepeat()
        {
            // 45 fps with ±8 ms of jitter: every frame comes before the next repeat is due.
            var s = new StallRepeats();
            long t = F;
            for (int i = 0; i < 2000; i++)
            {
                long gap = Ms(22.2 + (i % 2 == 0 ? 8 : -8));
                if (i > 100) Assert.True(t + gap < s.Due, $"frame {i}");
                t += gap;
                s.Frame(t);
            }
            Assert.InRange(s.Typical, Ms(21.5), Ms(23));
        }

        [Fact]
        public void TheFirstRepeatWaitsThreeTimesTheUsualFrameTime()
        {
            var s = new StallRepeats();
            long t = Frames(s, 300, 22.2);
            Assert.InRange(s.Due - t, Ms(66), Ms(67.5));
            Assert.InRange(s.Cadence, Ms(22), Ms(22.5));

            // Fast frames: at least 50 ms; repeats at most 60 a second.
            s = new StallRepeats();
            t = Frames(s, 300, 6.9);
            Assert.Equal(F / 20, s.Due - t);
            Assert.Equal(F / 60, s.Cadence);

            // Slow frames: three times theirs; repeats at least 30 a second.
            s = new StallRepeats();
            t = Frames(s, 300, 50);
            Assert.InRange(s.Due - t, Ms(149), Ms(151));
            Assert.Equal(F / 30, s.Cadence);
        }

        [Fact]
        public void RepeatsComeAtAStepCadenceUntilAFrame()
        {
            var s = new StallRepeats();
            long t = Frames(s, 300, 22.2);
            long typical = s.Typical, cadence = s.Cadence, first = s.Due;
            // A stall of 1.5 s; each repeat goes 0.3 ms after it was due.
            long due = first;
            int repeats = 0;
            while (due < t + Ms(1500))
            {
                s.Repeated(due + Ms(0.3));
                repeats++;
                Assert.Equal(due + cadence, s.Due);
                due = s.Due;
            }
            Assert.Equal(repeats, s.Run);
            Assert.Equal(first, s.RunStarted);
            Assert.InRange(repeats, 63, 66);
            // The frame after it: repeats start over, and the stall left the usual frame time alone.
            t += Ms(1500);
            s.Frame(t);
            Assert.Equal(0, s.Run);
            Assert.Equal(typical, s.Typical);
            Assert.InRange(s.Due - t, Ms(66), Ms(67.5));
        }

        [Fact]
        public void ALateRepeatMovesTheNextOn()
        {
            var s = new StallRepeats();
            Frames(s, 300, 22.2);
            long due = s.Due;
            s.Repeated(due + Ms(10));
            Assert.Equal(due + Ms(10) + s.Cadence, s.Due);
        }

        [Fact]
        public void ASkippedRepeatIsTriedAgainSoonThenGivenUp()
        {
            var s = new StallRepeats();
            Frames(s, 300, 22.2);
            long due = s.Due, cadence = s.Cadence;
            s.Skipped(due);
            Assert.Equal(due + F / 500, s.Due);
            // Still not taken after nearly a cadence: its turn goes, the next is on the grid.
            long now = due + cadence - Ms(1);
            s.Skipped(now);
            Assert.Equal(due + cadence, s.Due);
            s.Repeated(s.Due);
            Assert.Equal(due + 2 * cadence, s.Due);
        }

        [Fact]
        public void WaitsRoundUpToTheMillisecond()
        {
            var s = new StallRepeats();
            long t = Frames(s, 300, 22.2);
            Assert.Equal(0, s.WaitMilliseconds(s.Due));
            Assert.Equal(1, s.WaitMilliseconds(s.Due - Ms(0.2)));
            Assert.Equal(67, s.WaitMilliseconds(t));
        }
    }
}
