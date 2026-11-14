namespace OniMoono.Host
{
    /// The frame cap's deadlines (Application.targetFrameRate without vSync, --max-frame-rate,
    /// the 60 Hz of a player without a display). Each frame is due a period after the one
    /// before was due, not a period after it started: sleeping for what was left of each
    /// frame's own period lost every overrun (a frame longer than the period, the half
    /// millisecond a sleep ends late), so a cap of 60 ran at 57.0 fps on a late-game colony
    /// (docs/research/perceived-smoothness.md, Part 3). A frame that ran over is made up for by
    /// the frames after it, which sleep less. The debt carried is at most one period: after a
    /// long stall (a save, a load) at most one frame goes without its sleep, and no run of them.
    internal struct FramePace
    {
        int rate;
        // When the frame now ending is due to end (seconds on the caller's clock).
        double due;

        /// How long the frame that started at `frameStart` and has run until `now` sleeps for
        /// a cap of `rate` frames a second (none: 0 or less); negative or zero: not at all.
        public double Remaining(double now, double frameStart, int rate)
        {
            if (rate <= 0)
            {
                this.rate = 0;
                return 0;
            }
            double period = 1.0 / rate;
            // A new rate (or the first frame with one) counts from this frame's start.
            if (rate != this.rate)
            {
                this.rate = rate;
                due = frameStart + period;
            }
            else due += period;
            if (due < now - period) due = now - period;
            // (Never more than a period's sleep: a clock that went back.)
            else if (due > now + period) due = now + period;
            return due - now;
        }
    }
}
