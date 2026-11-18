namespace OniMoono.GameTweaks
{
    /// The clock of the low-fps-timing tweak's leak part (LeakTiming), without the game types (the
    /// unit tests drive it against a copy of WorldDamage's leak window).
    ///
    /// Game time in seconds since the clock was first read, from GameClock's fields as U60's
    /// GetTimeAsDouble adds them (`timeSinceStartOfCycle + cycle × 600` in double). GetTime is a
    /// float of the whole colony's time, so late in a colony it steps by more than a frame (an
    /// eighth of a second at cycle 2,000); counted from the session's first reading, the float
    /// stays as fine as `realtimeSinceStartup`, which the game reads there. A new clock (a load)
    /// starts the count again: WorldDamage's window is not saved and goes with its Game.
    internal struct LeakClock
    {
        object clock;
        double epoch;

        public float Seconds(object owner, float sinceCycleStart, int cycle)
        {
            double t = sinceCycleStart + cycle * 600.0;
            if (!ReferenceEquals(owner, clock))
            {
                clock = owner;
                epoch = t;
            }
            return (float)(t - epoch);
        }
    }
}
