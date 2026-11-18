using System;
using System.Reflection;
using HarmonyLib;

namespace OniMoono.GameTweaks
{
    /// Game.SimEveryTick's loop for the tweaks that change it (SimStepPaceCore): sim-step-pace
    /// leaves out its calls of no time, low-fps-timing's automation part (LogicTicks) runs a
    /// frame's later automation ticks between its sub-ticks. One prefix for both, patched under
    /// the Harmony ID of the first that installs it (ForeignPatches.Hooked: again by the next if
    /// that install is taken back). It runs the game's method when neither has anything to do in
    /// a frame.
    internal static class SimEveryTickLoop
    {
        static MethodInfo everyTick;
        static AccessTools.FieldRef<Game, float> simDt;
        static AccessTools.FieldRef<Game, int> simSubTick;
        static AccessTools.FieldRef<Game, bool> hasFirstSimTickRun;
        static Action<Game, float> unsafeSim200ms;

        /// sim-step-pace's part: true while it is on (SimStepPace sets it).
        internal static Func<bool> Pace;

        public static void Install(Harmony harmony)
        {
            if (everyTick == null)
            {
                everyTick = AccessTools.Method(typeof(Game), "SimEveryTick");
                simDt = AccessTools.FieldRefAccess<Game, float>("simDt");
                simSubTick = AccessTools.FieldRefAccess<Game, int>("simSubTick");
                hasFirstSimTickRun = AccessTools.FieldRefAccess<Game, bool>("hasFirstSimTickRun");
                unsafeSim200ms = AccessTools.MethodDelegate<Action<Game, float>>(AccessTools.Method(typeof(Game), "UnsafeSim200ms"));
            }
            if (ForeignPatches.Hooked(everyTick, typeof(SimEveryTickLoop))) return;
            harmony.Patch(everyTick, prefix: new HarmonyMethod(typeof(SimEveryTickLoop), nameof(SimEveryTick)) { priority = Priority.Last });
        }

        struct Ticks : ISimTicks
        {
            public Game Game;
            public void Sim200ms(float dt) => unsafeSim200ms(Game, dt);
            public void SubTick() => Singleton<StateMachineUpdater>.Instance.AdvanceOneSimSubTick();
            public void BeforeSubTick(int k) => LogicTicks.BeforeSubTick(k);
        }

        static bool SimEveryTick(Game __instance, float dt, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            bool pace = Pace != null && Pace();
            bool logic = LogicTicks.Pending;
            if (!pace && !logic) return true;
            var ticks = new Ticks { Game = __instance };
            bool skipped = SimStepPaceCore.Frame(ref simDt(__instance), ref simSubTick(__instance), ref hasFirstSimTickRun(__instance), dt, pace, ref ticks);
            if (logic) LogicTicks.EndFrame();
            if (pace)
            {
                if (skipped) SimStepPace.Skipped++;
                else if (dt <= 0f) SimStepPace.Paused++;
            }
            return false;
        }
    }
}
