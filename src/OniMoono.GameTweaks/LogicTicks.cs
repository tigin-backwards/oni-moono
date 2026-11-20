using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `low-fps-timing`'s automation part (**changes timing**; LowFpsTiming): when a
    /// frame holds two automation ticks or more, the second and later run between the frame's sim
    /// sub-ticks instead of back to back before them.
    ///
    /// LogicCircuitManager.Refresh (from Game.Update, before Game.SimEveryTick) runs a tick
    /// (PropagateSignals, then onLogicTick) for each 0.1 s of Time.deltaTime. A frame of more than
    /// 0.1 s of game time (below 30 fps at speed 3, 20 at speed 2, 10 at speed 1) runs two or more
    /// of them in a row, then the frame's sub-ticks. A network state that lasts one tick then
    /// begins and ends with no sub-tick between: event-driven buildings see both edges, but a pump,
    /// door or valve switched on for one tick never runs in it.
    ///
    /// With the tweak, Refresh runs as the game's (a mirror of it) but for where the ticks go
    /// (LogicTicksCore): the same ticks, counted by the game's loop, the frame's first one where
    /// the game runs it, each later one just before the sub-tick it falls on (SimEveryTickLoop,
    /// shared with sim-step-pace), with a sub-tick after each tick while the frame has as many
    /// sub-ticks as ticks. A frame with one tick, or with one sub-tick at most, runs as the game's.
    /// Topology refreshes (a dirty network), bridge lists and overload checks stay where they are.
    /// Ticks left over (a frame whose SimEveryTick did not run) run at the next Refresh, first.
    ///
    /// This part is off for the session once another Harmony owner patches Refresh,
    /// RenderEveryTick or SimEveryTick (checked at the first frame, then every 4,096).
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void LogicCircuitManager::Refresh(System.Single)", "e1d5feb1a7177132 linux-744825 win-744825 linux-763796 win-763796 linux-764025 win-764025", "45bf7e5a1fdf4dd7 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023 win-763325 win-763612")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void Game::SimEveryTick(System.Single)", "0339e1b13ef02096 linux-744825 win-744825 linux-763796 win-763796 linux-764025 win-764025", "5c36683191300649 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023 win-763325 win-763612")]
    [GameMember(GameUse.Uses, "System.Void LogicCircuitManager::RenderEveryTick(System.Single)")]
    [GameMember(GameUse.Uses, "System.Void Game::UnsafeSim200ms(System.Single)")]
    [GameMember(GameUse.Uses, "System.Single Game::simDt")]
    [GameMember(GameUse.Uses, "System.Int32 Game::simSubTick")]
    [GameMember(GameUse.Uses, "System.Boolean Game::hasFirstSimTickRun")]
    public static class LogicTicks
    {
        /// The tweak this is a part of (its game members are declared under that name), and the
        /// part as Player.log names it.
        public const string Name = LowFpsTiming.Name, Part = "automation ticks";

        static MethodBase[] guarded;
        static long frames;
        static bool off;

        /// The frame's ticks and where each goes (LogicTicksCore.Plan), the next to run, and the
        /// manager they run on.
        static int[] before = new int[16];
        static int count, next;
        static LogicCircuitManager manager;

        /// Counts since the start (specs, the e2e probe): automation ticks run, and those run
        /// between sub-ticks instead of before them.
        public static long Ticks, Moved;

        /// Whether ticks wait for this frame's sub-ticks (SimEveryTickLoop).
        internal static bool Pending => next < count;

        public static void Install(Harmony harmony)
        {
            guarded = Guarded();
            harmony.Patch(AccessTools.Method(typeof(LogicCircuitManager), "Refresh"), prefix: new HarmonyMethod(typeof(LogicTicks), nameof(Refresh)) { priority = Priority.Last });
            SimEveryTickLoop.Install(harmony);
        }

        /// The methods another owner's patch makes this tweak stand down (ModOverlaps).
        internal static MethodBase[] Guarded() =>
            new[]
            {
                AccessTools.Method(typeof(LogicCircuitManager), "Refresh"), AccessTools.Method(typeof(LogicCircuitManager), "RenderEveryTick"),
                AccessTools.Method(typeof(Game), "SimEveryTick"),
            };

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded) is not { } why) return false;
            global::Debug.Log($"{Name}: {Part} off ({why})");
            return true;
        }

        /// A tick as the game's Refresh runs one.
        static void Tick(LogicCircuitManager m)
        {
            m.PropagateSignals(force_send_events: false);
            if (m.onLogicTick != null) m.onLogicTick();
            Ticks++;
        }

        /// The ticks that go before the frame's k-th sub-tick (1-based).
        internal static void BeforeSubTick(int k)
        {
            while (next < count && before[next] <= k)
            {
                next++;
                Moved++;
                Tick(manager);
            }
        }

        /// The frame's ticks not run yet, then none.
        internal static void EndFrame()
        {
            while (next < count)
            {
                next++;
                Moved++;
                Tick(manager);
            }
            count = next = 0;
            manager = null;
        }

        /// LogicCircuitManager.Refresh, the frame's later ticks left for SimEveryTick.
        static bool Refresh(LogicCircuitManager __instance, float dt, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (Pending) EndFrame();
            if (off) return true;
            if ((frames++ & 0xFFF) == 0 && (off = Foreign())) return true;
            var m = __instance;
            if (m.conduitSystem.IsDirty)
            {
                m.conduitSystem.Update();
                LogicCircuitNetwork.logicSoundRegister.Clear();
                m.PropagateSignals(force_send_events: true);
                m.elapsedTime = 0f;
            }
            else if (SpeedControlScreen.Instance != null && !SpeedControlScreen.Instance.IsPaused)
            {
                m.elapsedTime += dt;
                m.timeSinceBridgeRefresh += dt;
                int ticks = LogicTicksCore.Plan(ref m.elapsedTime, LogicCircuitManager.ClockTickInterval, dt, Game.Instance.simDt, ref before);
                int now = 0;
                while (now < ticks && before[now] == 0) now++;
                for (int j = 0; j < now; j++) Tick(m);
                if (now < ticks)
                {
                    manager = m;
                    next = now;
                    count = ticks;
                }
                if (m.timeSinceBridgeRefresh > LogicCircuitManager.BridgeRefreshInterval)
                {
                    m.UpdateCircuitBridgeLists();
                    m.timeSinceBridgeRefresh = 0f;
                }
            }
            foreach (LogicCircuitNetwork network in Game.Instance.logicCircuitSystem.GetNetworks())
                m.CheckCircuitOverloaded(dt, network.id, network.GetBitsUsed());
            return false;
        }
    }
}
