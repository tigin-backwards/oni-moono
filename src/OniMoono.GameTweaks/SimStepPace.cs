using System;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `sim-step-pace` (**changes timing**): the native sim is called only at
    /// its 200 ms steps, whatever the frame rate.
    ///
    /// Game.SimEveryTick runs a sub-tick for each 1/60 s of game time and steps the sim
    /// (UnsafeSim200ms(0.2)) every 12th; a frame with no sub-tick, which at normal speed happens
    /// only above 60 fps, calls UnsafeSim200ms(0) instead. That call sends the sim a frame of no
    /// time and reads its state back (Game.StepTheSim): PrepareGameData waits in SimDLL until the
    /// sim thread has finished the step underway, so the first such frame after a step waits for
    /// the rest of it (a median 25 ms on a large modded colony on Windows), and the frame of no
    /// time is an iteration of the sim all the same (about 10 ms of CPU each: 175 ms a step at
    /// 1x uncapped against 40 ms capped at 50 fps, on that colony).
    ///
    /// With the tweak, SimEveryTick runs as the game's (SimStepPaceCore, through SimEveryTickLoop,
    /// which low-fps-timing's automation part shares) but for that call:
    /// sub-ticks, state machine updates and the 200 ms steps are the game's, as is the call on a
    /// frame with no game time (paused), so tools used while paused show at once. What changes:
    /// above 60 fps at normal speed the grid's data, dug and built tiles, and the solid and
    /// liquid change callbacks arrive with the next step, up to 0.2 s later than without the
    /// tweak; as the game does below 60 fps and at speeds 2 and 3.
    ///
    /// Off for the session once another Harmony owner patches SimEveryTick or UnsafeSim200ms
    /// (checked at the first frame, then every 4,096).
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void Game::SimEveryTick(System.Single)", "0339e1b13ef02096 linux-744825 win-744825 linux-763796 win-763796 linux-764025 win-764025 linux-764961 win-764961", "5c36683191300649 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761211 win-761654 win-762107 win-762230 win-763023 win-763325 win-763612")]
    [GameMember(GameUse.Uses, "System.Void Game::UnsafeSim200ms(System.Single)")]
    [GameMember(GameUse.Uses, "System.Single Game::simDt")]
    [GameMember(GameUse.Uses, "System.Int32 Game::simSubTick")]
    [GameMember(GameUse.Uses, "System.Boolean Game::hasFirstSimTickRun")]
    public static class SimStepPace
    {
        public const string Name = "sim-step-pace";

        static MethodBase[] guarded;
        static long frames;
        static bool off;

        /// Test infrastructure (PerfProbe's ab.txt and jank `set`): true runs the game's method, for
        /// an A/B in one run.
        public static bool Bypass;

        /// Counts since the start (E2eDriver's state reads, PerfProbe): the game's zero-time calls
        /// left out, and those made (paused frames).
        public static long Skipped, Paused;

        public static void Install(Harmony harmony)
        {
            guarded = Guarded();
            SimEveryTickLoop.Pace = On;
            SimEveryTickLoop.Install(harmony);
        }

        /// The methods another owner's patch makes this tweak stand down (ModOverlaps).
        internal static MethodBase[] Guarded() =>
            new[] { AccessTools.Method(typeof(Game), "SimEveryTick"), AccessTools.Method(typeof(Game), "UnsafeSim200ms") };

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded) is not { } why) return false;
            global::Debug.Log($"{Name}: off ({why})");
            return true;
        }

        /// Whether this frame's SimEveryTick leaves out its call of no time (SimEveryTickLoop).
        static bool On()
        {
            if (off || Bypass) return false;
            if ((frames++ & 0xFFF) == 0 && (off = Foreign())) return false;
            return true;
        }
    }
}
