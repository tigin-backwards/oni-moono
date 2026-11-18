using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The logic-in-sim-steps game tweak's plan (src/OniMoono.GameTweaks/LogicTicksCore.cs) and the
    /// shared SimEveryTick loop (SimStepPaceCore.cs), against copies of the game's
    /// LogicCircuitManager.Refresh tick loop and Game.SimEveryTick (U59 744825, U60 763325): the
    /// same ticks and sub-ticks, the same state after each frame; the same order whenever a frame
    /// has one tick or one sub-tick at most; a sub-tick after every tick otherwise.
    public class LogicTicksTests
    {
        const float Interval = 0.1f; // LogicCircuitManager.ClockTickInterval

        /// A frame's calls in order: 'L' an automation tick, 'S' a sub-tick pass, 'Z' a call of no time.
        sealed class GameCopy
        {
            public float elapsedTime, simDt;
            public int simSubTick;
            public bool hasFirstSimTickRun;
            public readonly StringBuilder Calls = new StringBuilder();

            static float Min(float a, float b) => a < b ? a : b;

            public void Frame(float dt)
            {
                // Refresh (not paused, no dirty network).
                elapsedTime += dt;
                while (elapsedTime > Interval)
                {
                    elapsedTime -= Interval;
                    Calls.Append('L');
                }
                // SimEveryTick.
                dt = Min(dt, 0.2f);
                simDt += dt;
                if (simDt >= 1f / 60f)
                {
                    do
                    {
                        simSubTick++;
                        simSubTick %= 12;
                        if (simSubTick == 0) hasFirstSimTickRun = true;
                        Calls.Append('S');
                        simDt -= 1f / 60f;
                    }
                    while (simDt >= 1f / 60f);
                }
                else Calls.Append('Z');
            }
        }

        struct Log : ISimTicks
        {
            public StringBuilder Calls;
            public int[] Before;
            public int Next, Count;
            public void Sim200ms(float dt) { if (dt == 0f) Calls.Append('Z'); }
            public void SubTick() { }
            public void BeforeSubTick(int k)
            {
                while (Next < Count && Before[Next] <= k)
                {
                    Next++;
                    Calls.Append('L');
                }
                Calls.Append('S');
            }
        }

        sealed class TweakCopy
        {
            public float elapsedTime, simDt;
            public int simSubTick;
            public bool hasFirstSimTickRun;
            public int[] before = new int[2];
            public readonly StringBuilder Calls = new StringBuilder();

            public void Frame(float dt)
            {
                elapsedTime += dt;
                int ticks = LogicTicksCore.Plan(ref elapsedTime, Interval, dt, simDt, ref before);
                int now = 0;
                while (now < ticks && before[now] == 0) now++;
                Calls.Append('L', now);
                var log = new Log { Calls = Calls, Before = before, Next = now, Count = ticks };
                SimStepPaceCore.Frame(ref simDt, ref simSubTick, ref hasFirstSimTickRun, dt, false, ref log);
                Calls.Append('L', log.Count - log.Next);
            }
        }

        static (string Game, string Tweak, List<(string Game, string Tweak)> Frames) Run(IEnumerable<float> frames)
        {
            var game = new GameCopy();
            var tweak = new TweakCopy();
            var each = new List<(string, string)>();
            foreach (var dt in frames)
            {
                int g = game.Calls.Length, t = tweak.Calls.Length;
                game.Frame(dt);
                tweak.Frame(dt);
                each.Add((game.Calls.ToString(g, game.Calls.Length - g), tweak.Calls.ToString(t, tweak.Calls.Length - t)));
                Assert.Equal(BitConverter.SingleToInt32Bits(game.elapsedTime), BitConverter.SingleToInt32Bits(tweak.elapsedTime));
                Assert.Equal(BitConverter.SingleToInt32Bits(game.simDt), BitConverter.SingleToInt32Bits(tweak.simDt));
                Assert.Equal(game.simSubTick, tweak.simSubTick);
            }
            return (game.Calls.ToString(), tweak.Calls.ToString(), each);
        }

        /// Ticks the sim never sees: an 'L' with another 'L' next (no sub-tick in between).
        static int Hidden(string calls) => Enumerable.Range(0, calls.Length - 1).Count(i => calls[i] == 'L' && calls[i + 1] == 'L');

        static IEnumerable<float> Frames(double speed, double fps, double seconds, bool capped = false)
        {
            float dt = (float)(Math.Min(1 / fps, 1 / 3.0) * speed);
            if (capped && dt > 0.2f) dt = 0.2f;
            return Enumerable.Repeat(dt, (int)(seconds * fps));
        }

        [Theory]
        [InlineData(1, 60)]
        [InlineData(1, 144)]
        [InlineData(1, 500)]
        [InlineData(2, 120)]
        [InlineData(3, 180)]
        [InlineData(3, 240)]
        [InlineData(1, 10.5)] // a tick a frame at most
        [InlineData(2, 20.5)]
        [InlineData(3, 30.5)]
        [InlineData(3, 100)]
        public void TheSameOrderWithOneTickOrOneSubTickAFrame(double speed, double fps)
        {
            var (game, tweak, _) = Run(Frames(speed, fps, 60));
            Assert.Equal(game, tweak);
        }

        [Fact]
        public void FramesOfOneSubTickOrOneTickRunAsTheGame()
        {
            var random = new Random(3);
            var frames = Enumerable.Range(0, 50000).Select(_ => (float)(random.NextDouble() * 0.5)).ToList();
            var (_, _, each) = Run(frames);
            foreach (var (g, t) in each)
                if (g.Count(c => c == 'S') <= 1 || g.Count(c => c == 'L') <= 1) Assert.Equal(g, t);
        }

        [Theory]
        [InlineData(3, 20, false)]
        [InlineData(3, 15, false)]
        [InlineData(3, 10, false)]
        [InlineData(3, 5, false)]
        [InlineData(3, 3, false)]
        [InlineData(2, 10, false)]
        [InlineData(1, 5, false)]
        [InlineData(3, 5, true)]
        [InlineData(3, 3, true)]
        public void EveryTickGetsASubTickAtLowFrameRates(double speed, double fps, bool capped)
        {
            var (game, tweak, _) = Run(Frames(speed, fps, 120, capped));
            Assert.Equal(game.Count(c => c == 'L'), tweak.Count(c => c == 'L'));
            Assert.Equal(game.Count(c => c == 'S'), tweak.Count(c => c == 'S'));
            Assert.True(Hidden(game) > 0);
            Assert.Equal(0, Hidden(tweak));
        }

        /// The study's share (docs/research/frame-rate-dependence.md, case 10): 1 - 0.1 / frame time.
        [Theory]
        [InlineData(20, 1 / 3.0)]
        [InlineData(15, 0.5)]
        [InlineData(10, 2 / 3.0)]
        public void TheGameHidesTheStudysShareOfTicks(double fps, double share)
        {
            var (game, _, _) = Run(Frames(3, fps, 120));
            Assert.InRange(Hidden(game) / (double)game.Count(c => c == 'L'), share - 0.02, share + 0.02);
        }

        [Fact]
        public void IrregularFramesKeepCounts()
        {
            var random = new Random(11);
            // Speeds 1 to 3, hitches past the sim's 0.2 s and maximumDeltaTime's 1/3 s × 3, pauses.
            var frames = Enumerable.Range(0, 50000).Select(_ => random.Next(10) switch
            {
                0 => 0f,
                1 => (float)(random.NextDouble() * 1.0),
                _ => (float)(random.NextDouble() / 20) * (1 + random.Next(3)),
            }).ToList();
            var (game, tweak, each) = Run(frames);
            Assert.Equal(game.Count(c => c == 'L'), tweak.Count(c => c == 'L'));
            Assert.Equal(game.Count(c => c == 'S'), tweak.Count(c => c == 'S'));
            Assert.Equal(game.Count(c => c == 'Z'), tweak.Count(c => c == 'Z'));
            // Within a frame with at least as many sub-ticks as ticks, a sub-tick after each tick.
            foreach (var (g, t) in each)
                if (g.Count(c => c == 'S') >= g.Count(c => c == 'L')) Assert.DoesNotContain("LL", t);
        }

        [Fact]
        public void ManyTicksAFrameGrowThePlan()
        {
            // A speed mod's 10× with maximumDeltaTime's 1/3 s: 33 ticks a frame over 12 sub-ticks.
            var (game, tweak, _) = Run(Enumerable.Repeat(10f / 3f, 50));
            Assert.Equal(game.Count(c => c == 'L'), tweak.Count(c => c == 'L'));
            Assert.Equal(game.Count(c => c == 'S'), tweak.Count(c => c == 'S'));
        }

        /// The plan's cost a frame: no allocation once its array is grown, and a frame of the
        /// worst case (speed 3, 5 fps uncapped: 6 ticks over 12 sub-ticks) in well under a
        /// microsecond (printed, not asserted: the test machine is shared).
        [Fact]
        public void PlanAllocatesNothingAFrame()
        {
            int[] before = new int[16];
            float elapsed = 0f, simDt = 0f;
            const int frames = 200_000;
            for (int i = 0; i < 1000; i++)
            {
                elapsed += 0.6f;
                LogicTicksCore.Plan(ref elapsed, Interval, 0.6f, simDt, ref before);
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            int ticks = 0;
            for (int i = 0; i < frames; i++)
            {
                elapsed += 0.6f;
                ticks += LogicTicksCore.Plan(ref elapsed, Interval, 0.6f, simDt, ref before);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            clock.Stop();
            Assert.Equal(0, allocated);
            Assert.InRange(ticks, frames * 6 - 1, frames * 6 + 1);
            Console.WriteLine($"LogicTicksCore.Plan: {clock.Elapsed.TotalMilliseconds * 1e6 / frames:F0} ns a frame of 6 ticks and 12 sub-ticks");
        }
    }
}
