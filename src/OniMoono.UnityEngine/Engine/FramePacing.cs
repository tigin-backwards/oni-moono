using System;
using System.Diagnostics;
using System.Threading;

namespace OniMoono.Engine
{
    /// The frame's delta with vsync, as Unity's player gives it at 60 fps: a whole number of
    /// display refreshes. Unity (2020.2 on) times a vsynced frame by its presentation, so on
    /// Windows Time.unscaledDeltaTime reads one refresh frame after frame, and two or three when
    /// a frame misses its vblank (measured in the original, U59 and U60, a 59.95 Hz monitor:
    /// 98.7% and 99.3% of 1,199 frames within 0.05 ms of a whole number of refreshes while
    /// panning a small colony). Our wall clock between frame starts varied by ±0.5 ms around
    /// the refresh, so the camera's keyboard pan, which moves by unscaledDeltaTime, stepped
    /// unevenly over a display that showed every picture for one refresh.
    ///
    /// The presenter reports each vsynced picture and the display's refresh period when it knows
    /// it (the display mode's refresh rate, DxgiSwapChain); the main thread rounds the wall-clock time
    /// since the last frame, plus what earlier rounding left over, to whole refreshes, so the
    /// deltas still add up to the wall clock (within half a refresh). The wall clock is used as
    /// before without vsync, without a known period (Linux, OpenGL without DXGI, the first
    /// seconds before the period is measured), when the presenter has shown nothing for a
    /// quarter second, for a frame over a second or under a millisecond, and while frames come
    /// in faster than the refresh on average (MeanBelow). Variable refresh (G-Sync, FreeSync)
    /// gets the rounding too: measured with G-Sync on, it panned at least as evenly as the wall
    /// clock (docs/research/frame-timing.md).
    internal static class FramePacing
    {
        static long periodTicks;
        static long lastPacedPresent;
        static double carry, meanWall;

        /// Frames that come in faster than the refresh on average, by this share or more, are
        /// not paced by it: vsync forced off in the driver (presents then return at once), or a
        /// period that is not the display's (the window moved to a faster monitor, not looked up
        /// again yet). Rounded, each would last at least a whole refresh, and the game would run
        /// up to twice as fast as the wall clock (1.83× at 110 fps on 60 Hz; reported in a
        /// Bilibili comment as speed 1 running at 2×). Vsynced frames average a refresh or more.
        /// The mean is exponential over about 32 frames, each frame counted up to two refreshes,
        /// so the short frames that catch up after a hitch do not reach it.
        const double MeanBelow = 0.95, MeanWeight = 1.0 / 32;

        /// The refresh period in Stopwatch ticks (0: not known).
        public static long PeriodTicks => Interlocked.Read(ref periodTicks);

        /// A picture was presented with vsync (presenter thread); `period`, the display's refresh
        /// period in Stopwatch ticks when known, else 0.
        public static void PacedPresent(long period)
        {
            Interlocked.Exchange(ref periodTicks, period);
            Interlocked.Exchange(ref lastPacedPresent, Stopwatch.GetTimestamp());
        }

        /// A picture was presented without vsync, or presentation stopped (presenter thread).
        public static void UnpacedPresent() => Interlocked.Exchange(ref lastPacedPresent, 0);

        /// The delta for the frame starting now, `wall` seconds of real time after the last one
        /// began (main thread).
        public static float Delta(double wall)
        {
            long period = Interlocked.Read(ref periodTicks);
            long last = Interlocked.Read(ref lastPacedPresent);
            bool paced = period > 0 && last != 0 && Stopwatch.GetTimestamp() - last <= Stopwatch.Frequency / 4;
            return (float)Next(wall, paced ? (double)period / Stopwatch.Frequency : 0, ref carry, ref meanWall);
        }

        /// The delta for a frame `wall` seconds after the last one, `p` the refresh period in
        /// seconds (0: not paced by a known refresh), with the rounding's remainder and the mean
        /// frame time (0: none yet). The wall clock, unrounded, without a period; for a frame
        /// over a second (a hitch, a resume after sleep: Godot's DeltaSmoother passes those
        /// through too), which also drops the remainder and the mean; for one under a
        /// millisecond (vsync off whatever the game asked for); and while frames come in faster
        /// than the refresh on average.
        internal static double Next(double wall, double p, ref double carry, ref double mean)
        {
            if (p <= 0 || wall > 1.0)
            {
                carry = 0;
                mean = 0;
                return wall;
            }
            if (wall < 0.001) return wall;
            if (mean <= 0) mean = p;
            mean += (Math.Min(wall, 2 * p) - mean) * MeanWeight;
            if (mean < p * MeanBelow)
            {
                carry = 0;
                return wall;
            }
            return Quantize(wall, p, ref carry);
        }

        /// `wall` plus `carry` rounded to whole refreshes of `p` seconds (at least one), the
        /// remainder kept in `carry` within half a refresh.
        internal static double Quantize(double wall, double p, ref double carry)
        {
            double total = wall + carry;
            double n = Math.Max(1, Math.Round(total / p));
            double delta = n * p;
            carry = Math.Clamp(total - delta, -p * 0.5, p * 0.5);
            return delta;
        }
    }
}
