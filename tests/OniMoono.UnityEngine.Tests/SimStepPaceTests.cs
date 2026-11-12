using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The sim-step-pace game tweak's frame (src/OniMoono.GameTweaks/SimStepPaceCore.cs, compiled
    /// in here) against a copy of the game's Game.SimEveryTick (U59 744825, U60 762230): the
    /// same steps and sub-ticks in the same order, the same state after each frame, and no call
    /// of no time on a frame with time.
    public class SimStepPaceTests
    {
        /// What a frame called, in order: 0.2f a step, 0f a call of no time, -1 a sub-tick.
        struct Log : ISimTicks
        {
            public List<float> Calls;
            public void Sim200ms(float dt) => Calls.Add(dt);
            public void SubTick() => Calls.Add(-1);
        }

        sealed class GameCopy
        {
            public float simDt;
            public int simSubTick;
            public bool hasFirstSimTickRun;
            public readonly List<float> Calls = new List<float>();

            static float Min(float a, float b) => a < b ? a : b; // UnityEngine.Mathf.Min

            /// Game.SimEveryTick as the game has it, UnsafeSim200ms and AdvanceOneSimSubTick logged.
            public void SimEveryTick(float dt)
            {
                dt = Min(dt, 0.2f);
                simDt += dt;
                if (simDt >= 1f / 60f)
                {
                    do
                    {
                        simSubTick++;
                        simSubTick %= 12;
                        if (simSubTick == 0)
                        {
                            hasFirstSimTickRun = true;
                            Calls.Add(0.2f);
                        }
                        if (hasFirstSimTickRun) Calls.Add(-1);
                        simDt -= 1f / 60f;
                    }
                    while (simDt >= 1f / 60f);
                }
                else
                {
                    Calls.Add(0f);
                }
            }
        }

        /// Both over the same frame times; returns the game's calls, the tweak's, and its skip count.
        static (List<float> Game, List<float> Tweak, int Skipped) Run(IEnumerable<float> frames)
        {
            var game = new GameCopy();
            float simDt = 0;
            int subTick = 0, skipped = 0;
            bool first = false;
            var log = new Log { Calls = new List<float>() };
            foreach (var dt in frames)
            {
                game.SimEveryTick(dt);
                if (SimStepPaceCore.Frame(ref simDt, ref subTick, ref first, dt, ref log)) skipped++;
                Assert.Equal(BitConverter.SingleToInt32Bits(game.simDt), BitConverter.SingleToInt32Bits(simDt));
                Assert.Equal(game.simSubTick, subTick);
                Assert.Equal(game.hasFirstSimTickRun, first);
            }
            return (game.Calls, log.Calls, skipped);
        }

        [Theory]
        [InlineData(30)]
        [InlineData(59)]
        [InlineData(60)]
        [InlineData(61)]
        [InlineData(144)]
        [InlineData(500)]
        public void OnlyTheCallsOfNoTimeAreLeftOut(double fps)
        {
            var (game, tweak, skipped) = Run(Enumerable.Repeat((float)(1 / fps), (int)(fps * 20)));
            Assert.Equal(game.Where(c => c != 0f), tweak);
            Assert.Equal(game.Count(c => c == 0f), skipped);
            Assert.DoesNotContain(0f, tweak);
            // 20 s of 1x: a step every 0.2 s.
            Assert.InRange(tweak.Count(c => c == 0.2f), 99, 100);
        }

        [Fact]
        public void TheGameCallsOfNoTimeOnlyAboveSixtyFps()
        {
            Assert.Equal(0, Run(Enumerable.Repeat(1f / 50, 1000)).Skipped);
            Assert.InRange(Run(Enumerable.Repeat(1f / 144, 1440)).Skipped, 830, 850);
        }

        [Fact]
        public void PausedFramesStillCallTheSim()
        {
            var frames = Enumerable.Repeat(1f / 144, 300).Concat(Enumerable.Repeat(0f, 50)).Concat(Enumerable.Repeat(1f / 144, 300));
            var (game, tweak, _) = Run(frames);
            Assert.Equal(game.Where(c => c != 0f).Count() + 50, tweak.Count);
            Assert.Equal(50, tweak.Count(c => c == 0f));
        }

        [Fact]
        public void IrregularFramesMatchTheGame()
        {
            var random = new Random(7);
            // Speeds 1 to 3, hitches above the game's 0.2 s limit, pauses.
            var frames = Enumerable.Range(0, 20000).Select(_ => random.Next(10) switch
            {
                0 => 0f,
                1 => (float)(random.NextDouble() * 0.5),
                _ => (float)(random.NextDouble() / 40) * (1 + random.Next(3)),
            }).ToList();
            var (game, tweak, skipped) = Run(frames);
            Assert.Equal(game.Where(c => c != 0f), tweak.Where(c => c != 0f));
            Assert.Equal(frames.Count(f => f == 0f), tweak.Count(c => c == 0f));
            Assert.Equal(game.Count(c => c == 0f) - frames.Count(f => f == 0f), skipped);
        }
    }
}
