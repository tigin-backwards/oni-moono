using System;
using System.Linq;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The brain-rate game tweak's allowance (src/OniMoono.GameTweaks/BrainRateAllowance.cs,
    /// compiled in here): brain updates a frame for a rate of real time, never more than the
    /// game's own count a frame, spread evenly.
    public class BrainRateAllowanceTests
    {
        /// The allowance of each of `frames` frames of `fps`.
        static int[] Run(double rate, int perFrame, double fps, int frames)
        {
            var allowance = new BrainRateAllowance(rate);
            return Enumerable.Range(0, frames).Select(_ => allowance.Take(1 / fps, perFrame)).ToArray();
        }

        [Theory]
        [InlineData(60)]
        [InlineData(79)]
        [InlineData(120)]
        [InlineData(240)]
        public void DuplicantsGetThirtyASecondAboveThirtyFps(double fps)
        {
            int frames = (int)(fps * 10);
            var taken = Run(30, 1, fps, frames);
            Assert.All(taken, n => Assert.InRange(n, 0, 1));
            // 10 s: 300 updates, give or take the first frame's.
            Assert.InRange(taken.Sum(), 299, 301);
        }

        [Fact]
        public void UpdatesAreSpreadEvenly()
        {
            // 120 fps at 30 a second: one update every fourth frame, never two frames running.
            var taken = Run(30, 1, 120, 1200);
            var at = Enumerable.Range(0, taken.Length).Where(i => taken[i] == 1).ToArray();
            for (int i = 1; i < at.Length; i++) Assert.Equal(4, at[i] - at[i - 1]);
            // Critters at 60 fps, 150 a second: 2 or 3 every frame, 5 every two frames.
            var critters = Run(150, 5, 60, 600);
            Assert.All(critters.Skip(1), n => Assert.InRange(n, 2, 3));
            for (int i = 1; i + 1 < critters.Length; i += 2) Assert.Equal(5, critters[i] + critters[i + 1]);
        }

        [Theory]
        [InlineData(30, 1)]
        [InlineData(20, 1)]
        [InlineData(5, 1)]
        [InlineData(30, 5)]
        [InlineData(11.4, 5)]
        public void AtThirtyFpsOrBelowEveryFrameGetsTheGamesCount(double fps, int perFrame)
        {
            var taken = Run(30 * perFrame, perFrame, fps, 3000);
            Assert.All(taken, n => Assert.Equal(perFrame, n));
        }

        [Fact]
        public void AfterASlowFrameNoFrameGetsMoreThanTheGamesCount()
        {
            var allowance = new BrainRateAllowance(150);
            for (int i = 0; i < 100; i++) allowance.Take(1 / 120.0, 5);
            // A 3 s autosave frame: the game's count, then at most the game's count a frame while
            // the second's worth carried over is spent, then the rate again.
            Assert.Equal(5, allowance.Take(3, 5));
            var after = Enumerable.Range(0, 240).Select(_ => allowance.Take(1 / 120.0, 5)).ToArray();
            Assert.All(after, n => Assert.InRange(n, 0, 5));
            Assert.InRange(after.Skip(120).Sum(), 149, 151);
        }

        [Fact]
        public void UnevenFramesUnderTheRateRunAsTheGame()
        {
            // Frames alternating 10 ms and 90 ms (20 a second, under 30): the game's one a frame.
            var allowance = new BrainRateAllowance(30);
            int sum = 0;
            for (int i = 0; i < 2000; i++) sum += allowance.Take(i % 2 == 0 ? 0.010 : 0.090, 1);
            Assert.InRange(sum, 1990, 2000);
        }

        [Fact]
        public void TheFirstFrameGetsTheGamesCount()
        {
            Assert.Equal(1, new BrainRateAllowance(30).Take(1 / 240.0, 1));
            Assert.Equal(5, new BrainRateAllowance(150).Take(0, 5));
        }

        [Fact]
        public void ACountOfZeroOrLessIsKept()
        {
            var allowance = new BrainRateAllowance(30);
            Assert.Equal(0, allowance.Take(1, 0));
            Assert.Equal(-1, allowance.Take(1, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BrainRateAllowance(0));
        }

        [Fact]
        public void AFrameOfNoTimeEarnsNothing()
        {
            var allowance = new BrainRateAllowance(30);
            allowance.Take(1 / 120.0, 1);
            Assert.Equal(0, allowance.Take(0, 1));
            Assert.Equal(0, allowance.Take(-1, 1));
        }

        // ---- The floors in game time (issue #85) ----

        /// Updates over `seconds` at `fps` for a group of `brains` at `timeScale`.
        static int Sum(double rate, int perFrame, double fps, double seconds, int brains, double timeScale)
        {
            var allowance = new BrainRateAllowance(rate);
            int sum = 0;
            for (int i = 0; i < (int)(fps * seconds); i++)
            {
                int n = allowance.Take(1 / fps, perFrame, brains, timeScale);
                Assert.InRange(n, 0, perFrame);
                sum += n;
            }
            return sum;
        }

        [Theory]
        [InlineData(1, 150)]    // 2,400 / 15 = 160 a second
        [InlineData(2, 320)]
        [InlineData(3, 480)]
        [InlineData(10, 1600)]
        public void EveryBrainGetsATurnWithinFifteenGameSeconds(double timeScale, double wanted)
        {
            // 2,400 critters at 400 fps, where the game's 5 a frame (2,000 a second) never binds.
            wanted = Math.Max(wanted, 160 * timeScale);
            int sum = Sum(150, 5, 400, 10, 2400, timeScale);
            Assert.InRange(sum, wanted * 10 - 6, wanted * 10 + 6);
            // The turn period in game seconds: brains / (updates a game second).
            Assert.True(2400 / (sum / (10 * timeScale)) <= BrainRateAllowance.MaxTurnSeconds * 1.01);
        }

        [Theory]
        [InlineData(40, 3)]
        [InlineData(30, 3)]
        [InlineData(60, 10)]
        [InlineData(12, 1)]
        public void TheFloorNeverGivesMoreThanTheGamesCountAFrame(double fps, double timeScale)
        {
            // 2,400 critters want 480 a second at speed 3: at 40 fps the game's 5 a frame, 200.
            var allowance = new BrainRateAllowance(150);
            for (int i = 0; i < 2000; i++) Assert.Equal(5, allowance.Take(1 / fps, 5, 2400, timeScale));
        }

        [Theory]
        [InlineData(1070, 1)]   // 71 a second wanted: under 150
        [InlineData(1070, 2)]   // 143
        [InlineData(700, 3)]    // 140
        [InlineData(0, 3)]
        public void UnderTheFloorsNothingChanges(int brains, double timeScale)
        {
            foreach (double fps in new[] { 45.0, 60, 79, 144 })
            {
                var plain = new BrainRateAllowance(150);
                var floored = new BrainRateAllowance(150);
                for (int i = 0; i < 3000; i++) Assert.Equal(plain.Take(1 / fps, 5), floored.Take(1 / fps, 5, brains, timeScale));
            }
        }

        [Fact]
        public void TheFloorsCreditCarriesItsFraction()
        {
            // 1,070 critters at speed 3: 214 a second. At 60 fps 3.57 a frame: 3 or 4, evenly.
            var allowance = new BrainRateAllowance(150);
            var taken = Enumerable.Range(0, 601).Select(_ => allowance.Take(1 / 60.0, 5, 1070, 3)).ToArray().Skip(1).ToArray();
            Assert.All(taken, n => Assert.InRange(n, 3, 4));
            Assert.InRange(taken.Sum(), 2139, 2141);
        }

        [Fact]
        public void TheGamesSlowestRateIsAFloorAtHighSpeeds()
        {
            // The game gives its count every 0.2 game seconds at least: 5 / 0.2 × 10 = 250 a second
            // at speed 10 for critters, 50 for Duplicants, however few there are.
            Assert.Equal(250, new BrainRateAllowance(150).RateFor(5, 10, 10));
            Assert.Equal(50, new BrainRateAllowance(30).RateFor(1, 10, 10));
            Assert.Equal(150, new BrainRateAllowance(150).RateFor(5, 10, 3));
            Assert.InRange(Sum(30, 1, 240, 10, 10, 10), 498, 502);
        }

        [Fact]
        public void DuplicantsGetTheSameFloor()
        {
            // 300 Duplicants at speed 3: 60 a second, a turn every 15 game seconds; 100 keep 30.
            Assert.Equal(60, new BrainRateAllowance(30).RateFor(1, 300, 3));
            Assert.Equal(30, new BrainRateAllowance(30).RateFor(1, 100, 3));
            Assert.InRange(Sum(30, 1, 240, 10, 300, 3), 598, 602);
        }

        [Fact]
        public void PausedTheRateIsThePlainOne()
        {
            Assert.Equal(150, new BrainRateAllowance(150).RateFor(5, 5000, 0));
            Assert.Equal(150, new BrainRateAllowance(150).RateFor(5, 5000, double.NaN));
        }
    }
}
