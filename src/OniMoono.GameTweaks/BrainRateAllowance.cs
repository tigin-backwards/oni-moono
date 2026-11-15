using System;

namespace OniMoono.GameTweaks
{
    /// The `brain-rate` tweak's allowance for one brain group (BrainRate): how many brains it may
    /// update this frame, for at most `rate` a second of real time and never more than the game's
    /// own count a frame (`perFrame`). Each frame earns `rate × dt`; whole updates are spent and
    /// the fraction carries over, so the updates spread evenly over frames (at 120 fps and 30 a
    /// second, one every fourth frame). Up to a second's worth of credit is carried over, so
    /// frames of uneven length still average the rate; as no frame gets more than `perFrame`, at
    /// `rate / perFrame` frames a second or fewer the game's own count runs every frame.
    ///
    /// The rate has two floors in game time (issue #85), so a large group at a high game speed is
    /// not held to turns rarer than its brains need:
    /// - every brain gets a turn within MaxTurnSeconds of game time: `brains × timeScale /
    ///   MaxTurnSeconds` a second. A critter that inhales its food (Pufts, Slicksters) gets one
    ///   meal a turn, and stays hungry, and so never excretes, once its turns are rarer than a
    ///   meal lasts it: 18 game seconds for a Puft (1.5 kg a meal of 50 kg a cycle), 30 for a
    ///   Puft Prince and 45 for a Slickster;
    /// - the game's own slowest rate, its count for every GameFrameSeconds of game time (a
    ///   frame's game time is clamped to that): `perFrame × timeScale / GameFrameSeconds`.
    /// Plain .NET (no game types): the unit tests compile it in.
    public sealed class BrainRateAllowance
    {
        /// The longest a brain waits for its turn, in game seconds, while the game's own count a
        /// frame allows it: under the 18 a Puft's meal lasts.
        public const double MaxTurnSeconds = 15;
        /// The most game time a frame advances (Game.SimEveryTick's clamp).
        public const double GameFrameSeconds = 0.2;

        readonly double rate;
        double credit;
        bool started;

        public BrainRateAllowance(double ratePerSecond)
        {
            if (!(ratePerSecond > 0)) throw new ArgumentOutOfRangeException(nameof(ratePerSecond));
            rate = ratePerSecond;
        }

        /// Updates a second of real time for a group of `brains` at `timeScale`: the rate, or
        /// either floor when it is higher.
        public double RateFor(int perFrame, int brains, double timeScale)
        {
            if (!(timeScale > 0)) return rate;
            double turns = Math.Max(0, brains) * timeScale / MaxTurnSeconds;
            double games = Math.Max(0, perFrame) * timeScale / GameFrameSeconds;
            return Math.Max(rate, Math.Max(turns, games));
        }

        /// Brains this frame may update, 0 to `perFrame` (the game's own count, returned as it is
        /// when not positive). `realDt`: the frame's real time in seconds. `brains`: the group's
        /// size; `timeScale`: the game's speed (0 while paused). The first frame gets `perFrame`.
        public int Take(double realDt, int perFrame, int brains = 0, double timeScale = 1)
        {
            if (perFrame <= 0) return perFrame;
            double now = RateFor(perFrame, brains, timeScale);
            credit = started ? credit + now * Math.Max(0, realDt) : perFrame;
            started = true;
            // A frame of exactly 1 / rate earns a whole update despite rounding.
            int n = Math.Min(perFrame, (int)(credit + 1e-6));
            // What is left over is kept, up to a second's worth: frames of uneven length (a slow
            // one, then fast ones) still average the rate, and no frame gets more than the game's.
            credit = Math.Min(Math.Max(now, perFrame), Math.Max(0, credit - n));
            return n;
        }
    }
}
