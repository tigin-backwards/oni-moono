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
    }
}
