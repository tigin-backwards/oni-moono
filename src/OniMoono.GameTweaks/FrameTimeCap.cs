using System;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `low-fps-timing`'s game time part (**changes timing**; LowFpsTiming): while a
    /// colony runs, a frame's game time (Time.deltaTime, and so Time.time) is capped at the 0.2 s
    /// the sim takes of it.
    ///
    /// Game.SimEveryTick takes min(Time.deltaTime, 0.2 s) a frame (its sub-ticks, and the remainder
    /// under 1/60 s kept for the next frame) and drops the rest. Time.deltaTime is the real frame
    /// time clamped to maximumDeltaTime (1/3 s) times the speed, up to 1 s at speed 3. Whatever
    /// runs on Time.deltaTime or Time.time then runs up to 5× faster than the sim on long frames:
    /// animations and their events (work, eating and moves that wait for an animation to end),
    /// the automation clock (Buffer and Filter Gate delays), Time.time timers (overheat and
    /// scalding damage, coughs, temperature checks, Athletics from moves) and Update gameplay.
    ///
    /// With the tweak, each SimEveryTick (a postfix: the frames a colony runs, paused or not) caps
    /// the next frame's Time.deltaTime at 0.2 s in the engine (OniMoono.Host.GameTimeCap, read by
    /// name: this assembly is built against the game's Unity). smoothDeltaTime, Time.time,
    /// timeSinceLevelLoad and the FixedUpdate steps follow it; unscaledDeltaTime and real time
    /// stay real (camera, UI, input). A frame of 0.2 s or less is untouched: above 15 fps at
    /// speed 3, 10 at speed 2, 5 at speed 1. The main menu and loading run no SimEveryTick, so
    /// no cap.
    ///
    /// This part is off for the session once another Harmony owner patches SimEveryTick other than
    /// with a postfix (it could change the sim's cap; checked at the first frame, then every
    /// 4,096). Without the engine's cap (not Moono's) its install fails, and the tweak's with it.
    [GameMember(GameUse.Postfix, "System.Void Game::SimEveryTick(System.Single)")]
    [GameMember(GameUse.Mirrors, "System.Void Game::SimEveryTick(System.Single)", "0339e1b13ef02096 linux-744825 win-744825 linux-763796 win-763796", "5c36683191300649 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023 win-763325 win-763612")]
    public static class FrameTimeCap
    {
        /// The tweak this is a part of (its game members are declared under that name), and the
        /// part as Player.log names it.
        public const string Name = LowFpsTiming.Name, Part = "game time cap";

        static Action<float> capNextFrame;
        static MethodBase[] guarded;
        static long frames;
        static bool off;

        /// Frames a cap was armed for (specs).
        public static long Armed;

        public static void Install(Harmony harmony)
        {
            var type = Type.GetType("OniMoono.Host.GameTimeCap, OniMoono", false)
                ?? throw new TypeLoadException("OniMoono.Host.GameTimeCap is not in the player");
            capNextFrame = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), type.GetMethod("CapNextFrame"));
            guarded = Guarded();
            harmony.Patch(AccessTools.Method(typeof(Game), "SimEveryTick"), postfix: new HarmonyMethod(typeof(FrameTimeCap), nameof(Stepped)));
        }

        /// The methods another owner's patch (but a postfix) makes this tweak stand down (ModOverlaps).
        internal static MethodBase[] Guarded() => new[] { AccessTools.Method(typeof(Game), "SimEveryTick") };

        internal static ProbeRepairPolicy.GuardedKinds KindsOf(MethodBase method) => ProbeRepairPolicy.GuardedKinds.AllButPostfixes;

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded, ProbeRepairPolicy.GuardedKinds.AllButPostfixes) is not { } why) return false;
            global::Debug.Log($"{Name}: {Part} off ({why})");
            return true;
        }

        /// The game's own cap: SimEveryTick's `dt = Mathf.Min(dt, 0.2f)`.
        [MirrorsGame("System.Void Game::SimEveryTick(System.Single)")]
        static void Stepped()
        {
            if (off) return;
            if ((frames++ & 0xFFF) == 0 && (off = Foreign())) return;
            capNextFrame(0.2f);
            Armed++;
        }
    }
}
