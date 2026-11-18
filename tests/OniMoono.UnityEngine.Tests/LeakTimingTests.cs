using System.Collections.Generic;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The low-fps-timing game tweak's leak part (src/OniMoono.GameTweaks/LeakTiming.cs): a copy of
    /// WorldDamage's leak window (ApplyDamage's `spawnTimes` check, Update's 1 s expiry; U59 744825,
    /// U60 763612) on the game's real-time clock and on LeakClock's game time, with a tile under
    /// damage every frame.
    public class LeakTimingTests
    {
        /// GameClock's fields as its AddTime keeps them.
        sealed class Clock
        {
            public float sinceCycleStart;
            public int cycle;

            public void Add(float dt)
            {
                sinceCycleStart += dt;
                while (sinceCycleStart >= 600f)
                {
                    cycle++;
                    sinceCycleStart -= 600f;
                }
            }
        }

        sealed class Window
        {
            readonly Dictionary<int, float> spawnTimes = new Dictionary<int, float>();

            /// ApplyDamage's check: a leak unless the cell is in the window.
            public bool Damage(int cell, float now)
            {
                if (spawnTimes.ContainsKey(cell)) return false;
                spawnTimes[cell] = now;
                return true;
            }

            /// Update's expiry.
            public void Update(float now)
            {
                var expired = new List<int>();
                foreach (var e in spawnTimes)
                    if (now - e.Value > 1f) expired.Add(e.Key);
                foreach (var cell in expired) spawnTimes.Remove(cell);
            }
        }

        /// The frames (0-based) a cell leaks in, over `frames` frames at `fps` and `speed`: the
        /// game's (real time) and the tweak's (game time). Paused frames (`paused`) have no game
        /// time and no damage; WorldDamage.Update still runs.
        static (List<int> Game, List<int> Tweak) Run(double fps, float speed, int frames, int startCycle = 0, (int From, int To) paused = default)
        {
            var clock = new Clock { cycle = startCycle, sinceCycleStart = 50f };
            var leak = new LeakClock();
            object owner = new object();
            var game = new Window();
            var tweak = new Window();
            var (g, t) = (new List<int>(), new List<int>());
            double real = 1000.0;
            for (int f = 0; f < frames; f++)
            {
                real += 1.0 / fps;
                bool running = f < paused.From || f >= paused.To;
                if (running)
                {
                    clock.Add((float)(speed / fps));
                    if (game.Damage(7, (float)real)) g.Add(f);
                    if (tweak.Damage(7, leak.Seconds(owner, clock.sinceCycleStart, clock.cycle))) t.Add(f);
                }
                game.Update((float)real);
                tweak.Update(leak.Seconds(owner, clock.sinceCycleStart, clock.cycle));
            }
            return (g, t);
        }

        [Fact]
        public void AtSpeed1TheSameLeaksAsTheGame()
        {
            // 64 fps: frame times exact in float, so real and game time agree to the bit.
            var (game, tweak) = Run(64, 1f, 64 * 120);
            Assert.Equal(game, tweak);
            Assert.Equal(game, Run(64, 1f, 64 * 120, startCycle: 2000).Tweak);
        }

        /// Game time between a cell's leaks, in seconds.
        static List<double> Gaps(List<int> frames, double gameSecondsAFrame)
        {
            var gaps = new List<double>();
            for (int i = 1; i < frames.Count; i++) gaps.Add((frames[i] - frames[i - 1]) * gameSecondsAFrame);
            return gaps;
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(2f)]
        [InlineData(3f)]
        [InlineData(10f)] // a speed mod
        public void OneLeakAGameSecondAtAnySpeed(float speed)
        {
            const double fps = 64;
            double dt = speed / fps;
            var (game, tweak) = Run(fps, speed, (int)(120 / dt)); // 120 s of game time
            // The tweak: a game second and at most two frames between leaks, at any speed.
            Assert.All(Gaps(tweak, dt), gap => Assert.InRange(gap, 1.0 + 1e-9, 1.0 + 2 * dt));
            Assert.InRange(tweak.Count, (int)(120 / (1 + 2 * dt)), 121);
            // The game: a real second, so `speed` game seconds.
            Assert.All(Gaps(game, dt), gap => Assert.InRange(gap, speed + 1e-9, speed * (1 + 2 / fps)));
        }

        [Fact]
        public void LateInAColonyAsEarly()
        {
            // GetTime's float steps by 1/8 s at cycle 2,000; counted from the session's start it does not.
            Assert.Equal(Run(64, 3f, 64 * 40).Tweak, Run(64, 3f, 64 * 40, startCycle: 2000).Tweak);
        }

        [Fact]
        public void PausedTheWindowWaits()
        {
            // A leak at frame 0, then paused for 10 real seconds from frame 32 (half a game second in):
            // the next leak comes a game second (and two frames) after the first, as unpaused.
            var (game, tweak) = Run(64, 1f, 64 * 13, paused: (32, 32 + 640));
            Assert.Equal(0, tweak[0]);
            Assert.Equal(32 + 640 + 34, tweak[1]);
            // The game's window ran out during the pause: it leaks on the first frame after it.
            Assert.Equal(32 + 640, game[1]);
        }

        [Fact]
        public void ANewClockCountsAgain()
        {
            var leak = new LeakClock();
            object a = new object(), b = new object();
            Assert.Equal(0f, leak.Seconds(a, 100f, 3000));
            Assert.Equal(1.5f, leak.Seconds(a, 101.5f, 3000));
            Assert.Equal(500.5f, leak.Seconds(a, 0.5f, 3001)); // over a new cycle
            Assert.Equal(0f, leak.Seconds(b, 10f, 5));
        }
    }
}
