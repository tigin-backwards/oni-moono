using System;
using OniMoono.Engine;
using Xunit;

namespace OniMoono.Tests
{
    // FramePacing: with vsync the wall-clock delta is rounded to whole refreshes, the remainder
    // carried so the deltas add up to the wall clock.
    public class FramePacingTests
    {
        const double P = 0.0166807;

        [Fact]
        public void JitterAroundARefreshGivesOneRefresh()
        {
            double carry = 0;
            foreach (double wall in new[] { 0.01624, 0.01712, 0.01660, 0.01690, 0.01645 })
                Assert.Equal(P, FramePacing.Quantize(wall, P, ref carry), 9);
        }

        [Fact]
        public void SlowFramesGiveWholeRefreshesThatAddUpToTheWallClock()
        {
            double carry = 0, wallSum = 0, sum = 0;
            var random = new Random(1);
            for (int i = 0; i < 10_000; i++)
            {
                double wall = 0.018 + random.NextDouble() * 0.04;
                double d = FramePacing.Quantize(wall, P, ref carry);
                double n = d / P;
                Assert.Equal(Math.Round(n), n, 6);
                Assert.True(n >= 1);
                wallSum += wall;
                sum += d;
            }
            Assert.InRange(sum - wallSum, -P * 0.5 - 1e-9, P * 0.5 + 1e-9);
        }

        [Fact]
        public void AHitchKeepsItsLength()
        {
            double carry = 0;
            double d = FramePacing.Quantize(5.6, P, ref carry);
            Assert.InRange(d, 5.6 - P * 0.5, 5.6 + P * 0.5);
        }

        [Fact]
        public void WithoutAKnownRefreshTheWallClock()
        {
            double carry = 0.004; int shortFrames = 0;
            Assert.Equal(0.0173, FramePacing.Next(0.0173, 0, ref carry, ref shortFrames));
            Assert.Equal(0, carry);
        }

        [Fact]
        public void AFrameOverASecondPassesThroughAndDropsTheRemainder()
        {
            double carry = 0.005; int shortFrames = 0;
            Assert.Equal(1.37, FramePacing.Next(1.37, P, ref carry, ref shortFrames));
            Assert.Equal(0, carry);
            Assert.Equal(P, FramePacing.Next(0.0167, P, ref carry, ref shortFrames), 9);
        }

        [Fact]
        public void AFrameUnderAMillisecondPassesThrough()
        {
            double carry = 0.002; int shortFrames = 0;
            Assert.Equal(0.0004, FramePacing.Next(0.0004, P, ref carry, ref shortFrames));
            Assert.Equal(0.002, carry);
        }

        [Fact]
        public void EightShortFramesInARowGiveTheWallClock()
        {
            double carry = 0; int shortFrames = 0;
            for (int i = 0; i < 7; i++) Assert.Equal(P, FramePacing.Next(0.0069, P, ref carry, ref shortFrames), 9);
            Assert.Equal(0.0069, FramePacing.Next(0.0069, P, ref carry, ref shortFrames));
            Assert.Equal(0, carry);
            Assert.Equal(P, FramePacing.Next(0.0168, P, ref carry, ref shortFrames), 9);
        }

        [Fact]
        public void PacedFramesAreRounded()
        {
            double carry = 0; int shortFrames = 0;
            Assert.Equal(2 * P, FramePacing.Next(0.0329, P, ref carry, ref shortFrames), 9);
        }
    }
}
