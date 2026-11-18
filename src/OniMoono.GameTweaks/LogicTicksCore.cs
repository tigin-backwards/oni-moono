namespace OniMoono.GameTweaks
{
    /// Where a frame's automation ticks go, for the low-fps-timing tweak's automation part
    /// (LogicTicks), without the game types (the unit tests drive it against copies of the game's
    /// loops).
    ///
    /// The game's LogicCircuitManager.Refresh adds the frame's Time.deltaTime to `elapsedTime` and
    /// runs a tick (PropagateSignals, onLogicTick) while it is above the 0.1 s interval, all before
    /// the frame's sim sub-ticks (Game.SimEveryTick: min(deltaTime, 0.2 s) added to `simDt`, a
    /// sub-tick for each 1/60 s of it). With two ticks or more in a frame, ticks run back to back
    /// with no sub-tick between them. Plan counts the ticks with the game's own loop (the same
    /// number, the same `elapsedTime` after) and gives each a place: the frame's first tick, and
    /// every tick of a frame with one tick or one sub-tick at most, run where the game runs them
    /// (`before[j]` 0). Another tick j runs just before sub-tick `before[j]` (1-based, in the
    /// frame), the first one at or after the moment it falls on in the frame, and after the one
    /// the tick before it went before, and early enough to leave a sub-tick for each tick after
    /// it, so a sub-tick follows each tick while the frame has as many sub-ticks as ticks. Moments
    /// are fractions of the frame: the ticks' of deltaTime, the sub-ticks' of the sim's share of
    /// it (all of it below 0.2 s).
    internal static class LogicTicksCore
    {
        public const float SubTick = 1f / 60f;

        /// The frame's tick count; `before` (grown as needed) holds each tick's place.
        /// `elapsedTime` is the field after the game's `elapsedTime += dt`; `simDt` the game's
        /// before this frame's SimEveryTick.
        public static int Plan(ref float elapsedTime, float interval, float dt, float simDt, ref int[] before)
        {
            // The game's loop (Refresh), keeping each tick's elapsedTime before its subtraction.
            int ticks = 0;
            float e = elapsedTime;
            while (e > interval)
            {
                if (ticks == before.Length) System.Array.Resize(ref before, before.Length * 2);
                before[ticks++] = System.BitConverter.SingleToInt32Bits(e);
                e -= interval;
            }
            elapsedTime = e;
            if (ticks == 0) return 0;
            if (ticks == 1 || dt <= 0f)
            {
                for (int j = 0; j < ticks; j++) before[j] = 0;
                return ticks;
            }

            // The frame's sub-ticks (Game.SimEveryTick's loop), and the moment of each.
            float share = dt < 0.2f ? dt : 0.2f; // Mathf.Min(dt, 0.2f)
            float s = simDt + share;
            int subTicks = 0;
            double firstSub = 0;
            if (s >= SubTick)
            {
                firstSub = share - ((double)s - SubTick);
                do
                {
                    subTicks++;
                    s -= SubTick;
                }
                while (s >= SubTick);
            }
            if (subTicks <= 1)
            {
                for (int j = 0; j < ticks; j++) before[j] = 0;
                return ticks;
            }

            // Sub-tick k (1-based) falls at firstSub + (k - 1) / 60 into the sim's share; tick j at
            // dt - (e_j - interval) into dt, e_j its elapsedTime before the subtraction.
            before[0] = 0;
            int last = 1;
            for (int j = 1; j < ticks; j++)
            {
                double at = (dt - ((double)System.BitConverter.Int32BitsToSingle(before[j]) - interval)) / dt * share;
                int k = last + 1;
                // Room for a sub-tick after each tick still to come, when the frame has it.
                int latest = subTicks - (ticks - 1 - j);
                if (latest < k) latest = k;
                if (latest > subTicks) latest = subTicks;
                while (k < latest && firstSub + (k - 1) * (double)SubTick < at) k++;
                if (k > subTicks) k = subTicks;
                before[j] = k;
                last = k;
            }
            return ticks;
        }
    }
}
