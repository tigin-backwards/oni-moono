namespace OniMoono.GameTweaks
{
    /// What a frame of Game.SimEveryTick asks of the game (SimStepPace): a call of
    /// UnsafeSim200ms, and a sub-tick of StateMachineUpdater.
    internal interface ISimTicks
    {
        void Sim200ms(float dt);
        void SubTick();
    }

    /// Game.SimEveryTick's frame for the sim-step-pace tweak, without the game types (the unit
    /// tests drive it against a copy of the game's own). The game's loop as it is: the frame's
    /// time, at most 0.2 s, adds to `simDt`; each 1/60 s of it is a sub-tick, and every 12th
    /// sub-tick steps the sim by 0.2 s. A frame with time but no sub-tick does not call the sim
    /// (the game calls UnsafeSim200ms(0) there); a frame with no time (paused) still does.
    internal static class SimStepPaceCore
    {
        /// Returns whether the game would have called UnsafeSim200ms(0) where this did not.
        public static bool Frame<T>(ref float simDt, ref int simSubTick, ref bool hasFirstSimTickRun, float dt, ref T ticks) where T : struct, ISimTicks
        {
            dt = dt < 0.2f ? dt : 0.2f; // Mathf.Min(dt, 0.2f)
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
                        ticks.Sim200ms(0.2f);
                    }
                    if (hasFirstSimTickRun) ticks.SubTick();
                    simDt -= 1f / 60f;
                }
                while (simDt >= 1f / 60f);
                return false;
            }
            if (dt > 0f) return true;
            ticks.Sim200ms(0f);
            return false;
        }
    }
}
