namespace OniMoono.GameTweaks
{
    /// What a frame of Game.SimEveryTick asks of the game (SimEveryTickLoop): a call of
    /// UnsafeSim200ms, a sub-tick of StateMachineUpdater, and what runs before the frame's k-th
    /// sub-tick (1-based; low-fps-timing's deferred automation ticks).
    internal interface ISimTicks
    {
        void Sim200ms(float dt);
        void SubTick();
        void BeforeSubTick(int k);
    }

    /// Game.SimEveryTick's frame for the sim-step-pace and low-fps-timing tweaks, without the
    /// game types (the unit tests drive it against a copy of the game's own). The game's loop as
    /// it is: the frame's time, at most 0.2 s, adds to `simDt`; each 1/60 s of it is a sub-tick,
    /// and every 12th sub-tick steps the sim by 0.2 s. With `skipNoTime` (sim-step-pace), a frame
    /// with time but no sub-tick does not call the sim (the game calls UnsafeSim200ms(0) there); a
    /// frame with no time (paused) still does. BeforeSubTick(k) comes first in the k-th pass of the
    /// loop, before its step.
    internal static class SimStepPaceCore
    {
        /// Returns whether the game would have called UnsafeSim200ms(0) where this did not.
        public static bool Frame<T>(ref float simDt, ref int simSubTick, ref bool hasFirstSimTickRun, float dt, bool skipNoTime, ref T ticks) where T : struct, ISimTicks
        {
            dt = dt < 0.2f ? dt : 0.2f; // Mathf.Min(dt, 0.2f)
            simDt += dt;
            if (simDt >= 1f / 60f)
            {
                int k = 0;
                do
                {
                    ticks.BeforeSubTick(++k);
                    simSubTick++;
                    simSubTick %= 12;
                    if (simSubTick == 0)
                    {
                        hasFirstSimTickRun = true;
                        ticks.Sim200ms(0.2f);
                    }
                    if (hasFirstSimTickRun) ticks.SubTick();
                    simDt -= 1f / 60f;
                }
                while (simDt >= 1f / 60f);
                return false;
            }
            if (skipNoTime && dt > 0f) return true;
            ticks.Sim200ms(0f);
            return false;
        }
    }
}
