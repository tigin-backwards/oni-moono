using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `critter-needs-first` (**changes behaviour**; issue #85): a critter that
    /// is ready to inhale its food or to excrete takes one of the frame's brain turns ahead of
    /// the round robin.
    ///
    /// BrainScheduler gives 5 critter brains a turn a frame, in a fixed order, and a frame
    /// advances at most 0.2 game seconds: with N critters at speed 3 a critter's turn comes every
    /// `0.6 N / fps` game seconds (`N / 25` below 15 fps). A critter that inhales its food
    /// (GasAndLiquidConsumerMonitor: Slicksters, Pufts) starts one meal a turn, 1.5 kg, which a
    /// Slickster burns in 45 game seconds and a Puft in 18. Once its turns are rarer than that it
    /// stays hungry; eating ranks above excreting and is ready sooner after a meal (5 s against
    /// 10), so a hungry critter never excretes: its stomach fills for cycles and empties at once
    /// after the next load, when excreting is the first chore ready. Measured with 2,400 critters
    /// at 30 fps: a turn every 48 game seconds, 8 Slicksters ate 184 times in 600 game seconds
    /// and never excreted.
    ///
    /// The tweak asks the game's priority queue (BrainScheduler.PrioritizeBrain, which the
    /// game serves before the round robin, within the frame's count) for a critter's turn:
    /// - when its inhaling monitor's wait after a meal is over and it is hungry (the monitor
    ///   enters its `looking` state): the turn finds a cell and starts the meal. Once per wait:
    ///   a critter with nothing to inhale in reach stays in that state and is not asked again;
    /// - when its excrete condition can first hold: its stomach holds enough, and the game's
    ///   minimum time since its last meal or excretion has passed (checked once, that long after
    ///   each meal, each excretion and its start).
    /// No turn is added: the others' turns come that much later (with 16 such critters among
    /// 2,400 at 30 fps, every 49.3 game seconds instead of 48.4). Critters that eat solids take a
    /// stomach's worth a meal and are asked for only to excrete.
    ///
    /// A critter that attacks on its own (Beetas, Pokeshells guarding eggs, critters that hunt)
    /// notices a target only on its turn: ThreatMonitor's `safe.seeking` state searches the
    /// attackable entries in a 41×41 box on the turn's pre-update. With 500 critters a turn comes
    /// every 3.3 game seconds at speed 1 and 30 fps, but every 10 at speed 3 and 20 below 15 fps:
    /// by then, and its 0–4 s wait before attacking, a passing target has gone. So the tweak also
    /// asks for the turn of a critter in `safe.seeking` with a target it could take in that box
    /// (ThreatTurnTarget, the search's tests read only), or in `threatened.creature` (its turn
    /// picks the fight or the flight), once its last turn is older than the round the game takes
    /// at speed 1 and 30 fps (critter brains / 150 game seconds). Where turns come that often
    /// already, none is ever due. ThreatTurns bounds it: at most 4 critters tested a frame, and
    /// at most one ask for every 5 of the group's turns, so the round robin keeps 4 in 5 of them.
    /// A test visits that box's partitioner entries up to the first target or the 50th entry,
    /// with a CanReach read each; nothing is allocated a frame. Measured (12 Beetas, a Pip
    /// visiting 8 game seconds at a time, 500 other critters): docs/research/brain-probes.md.
    ///
    /// `brain-rate` installs the same hooks for its critter cap, where turns are scarce at any
    /// frame rate; with both tweaks on they are installed once.
    ///
    /// The checks and threat turns belong to one game: each new Game (Game.OnPrefabInit, before
    /// the colony's state machines start) forgets them. Leaving a colony frees its state machines
    /// without their states' exits (StateMachineController.OnLoadLevel), so none is untracked
    /// then; a freed one read later throws (#93). A key whose test throws is dropped.
    ///
    /// With ONI_TWEAK_VERIFY=1 each asked turn is followed: it must come within as many frames as
    /// brains were queued before it, plus 2 (the queue is served in order, at least one a frame);
    /// the count and those over are logged every 600 game seconds.
    [GameMember(GameUse.Postfix, "System.Void GasAndLiquidConsumerMonitor::InitializeStates(StateMachine/BaseState&)")]
    [GameMember(GameUse.Uses, "GameStateMachine`4/State<GasAndLiquidConsumerMonitor,GasAndLiquidConsumerMonitor/Instance,IStateMachineTarget,GasAndLiquidConsumerMonitor/Def> GasAndLiquidConsumerMonitor::looking")]
    [GameMember(GameUse.Postfix, "System.Void CreatureCalorieMonitor/Instance::OnCaloriesConsumed(System.Object)")]
    [GameMember(GameUse.Postfix, "System.Void CreatureCalorieMonitor/Instance::Poop(PoopData)")]
    [GameMember(GameUse.Postfix, "System.Void CreatureCalorieMonitor/Instance::StartSM()")]
    [GameMember(GameUse.Uses, "System.Single CreatureCalorieMonitor/Instance::lastMealOrPoopTime")]
    [GameMember(GameUse.Uses, "System.Single CreatureCalorieMonitor/Def::minimumTimeBeforePooping")]
    [GameMember(GameUse.Uses, "CreatureCalorieMonitor/Stomach CreatureCalorieMonitor/Instance::stomach")]
    [GameMember(GameUse.Uses, "System.Boolean CreatureCalorieMonitor/Stomach::IsReadyToPoop()")]
    [GameMember(GameUse.Uses, "CreatureCalorieMonitor/PauseStates CreatureCalorieMonitor::pause")]
    [GameMember(GameUse.Postfix, "System.Void ThreatMonitor::InitializeStates(StateMachine/BaseState&)")]
    [GameMember(GameUse.Uses, "ThreatMonitor/SafeStates ThreatMonitor::safe")]
    [GameMember(GameUse.Uses, "GameStateMachine`4/State<ThreatMonitor,ThreatMonitor/Instance,IStateMachineTarget,ThreatMonitor/Def> ThreatMonitor/SafeStates::seeking")]
    [GameMember(GameUse.Uses, "ThreatMonitor/ThreatenedStates ThreatMonitor::threatened")]
    [GameMember(GameUse.Uses, "GameStateMachine`4/State<ThreatMonitor,ThreatMonitor/Instance,IStateMachineTarget,ThreatMonitor/Def> ThreatMonitor/ThreatenedStates::creature")]
    [GameMember(GameUse.Mirrors, "System.Void ThreatMonitor/Instance::GatherThreats()", "df132b7b087bdaf0 linux-744825 win-744825", "7ddd76b19bb8f283 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023 win-763325 win-763612", "4534b15a68168dc9 linux-763796 win-763796 linux-764025 win-764025")]
    [GameMember(GameUse.Uses, "System.Int32 BrainScheduler/BrainGroup::get_BrainCount()")]
    [GameMember(GameUse.Uses, "System.Int32 BrainScheduler/BrainGroup::InitialProbeCount()")]
    [GameMember(GameUse.Postfix, "System.Void BrainScheduler::RenderEveryTick(System.Single)")]
    [GameMember(GameUse.Uses, "System.Void BrainScheduler::PrioritizeBrain(Brain)")]
    // Verify mode's count of the turns that followed.
    [GameMember(GameUse.Postfix, "System.Void Brain::UpdateBrain()", Conditional = true)]
    [GameMember(GameUse.Uses, "BrainScheduler Game::BrainScheduler")]
    [GameMember(GameUse.Postfix, "System.Void Game::OnPrefabInit()")]
    public static class CritterNeedsFirst
    {
        public const string Name = "critter-needs-first";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly DueChecks<CreatureCalorieMonitor.Instance> excreteChecks = new DueChecks<CreatureCalorieMonitor.Instance>();
        static readonly ThreatTurns<ThreatMonitor.Instance> threatTurns = new ThreatTurns<ThreatMonitor.Instance>();
        static readonly Func<ThreatMonitor.Instance, bool> threatReady = ThreatReady;
        static readonly Action<ThreatMonitor.Instance> threatAsk = ThreatAsk;
        static Func<BrainScheduler.BrainGroup, int> probeCount;
        static BrainScheduler.BrainGroup critters;
        static bool dropLogged;

        /// For tests (E2eDriver's state reads): the turns asked for, by need.
        public static long ToInhale, ToExcrete, ToAttack;

        /// For tests: the critters followed for threat turns, and those dropped this game as their
        /// test threw.
        public static int ThreatTracked => threatTurns.Count;
        public static int ThreatDropped => threatTurns.Dropped;

        /// Installs the hooks once: `brain-rate` installs them too, for its critter cap. They go
        /// under the first caller's Harmony ID, and in again if that tweak's install was taken back.
        public static void Install(Harmony harmony)
        {
            var states = AccessTools.Method(typeof(GasAndLiquidConsumerMonitor), nameof(GasAndLiquidConsumerMonitor.InitializeStates));
            if (ForeignPatches.Hooked(states, typeof(CritterNeedsFirst))) return;
            harmony.Patch(states, postfix: new HarmonyMethod(typeof(CritterNeedsFirst), nameof(InhalerStates)));
            var instance = typeof(CreatureCalorieMonitor.Instance);
            var meal = new HarmonyMethod(typeof(CritterNeedsFirst), nameof(MealOrExcretion));
            harmony.Patch(AccessTools.Method(instance, nameof(CreatureCalorieMonitor.Instance.OnCaloriesConsumed)), postfix: meal);
            harmony.Patch(AccessTools.Method(instance, nameof(CreatureCalorieMonitor.Instance.Poop), new[] { typeof(PoopData) }), postfix: meal);
            harmony.Patch(AccessTools.Method(instance, nameof(CreatureCalorieMonitor.Instance.StartSM)), postfix: meal);
            harmony.Patch(AccessTools.Method(typeof(ThreatMonitor), nameof(ThreatMonitor.InitializeStates)), postfix: new HarmonyMethod(typeof(CritterNeedsFirst), nameof(ThreatStates)));
            probeCount ??= AccessTools.MethodDelegate<Func<BrainScheduler.BrainGroup, int>>(AccessTools.Method(typeof(BrainScheduler.BrainGroup), "InitialProbeCount"));
            harmony.Patch(AccessTools.Method(typeof(BrainScheduler), nameof(BrainScheduler.RenderEveryTick)), postfix: new HarmonyMethod(typeof(CritterNeedsFirst), nameof(Frame)));
            if (verify) harmony.Patch(AccessTools.Method(typeof(Brain), nameof(Brain.UpdateBrain)), postfix: new HarmonyMethod(typeof(CritterNeedsFirst), nameof(Turn)));
            harmony.Patch(AccessTools.Method(typeof(Game), "OnPrefabInit"), postfix: new HarmonyMethod(typeof(CritterNeedsFirst), nameof(NewGame)));
        }

        /// Game.OnPrefabInit: a new game, before its state machines start. The last game's
        /// critters are forgotten; its state machines were freed without leaving their states.
        static void NewGame()
        {
            excreteChecks.Clear();
            threatTurns.Clear();
            asked.Clear();
            critters = null;
            critterScheduler = null;
            dropLogged = false;
        }

        /// GasAndLiquidConsumerMonitor.InitializeStates: entering `looking` (hungry, the wait
        /// after a meal over) asks for the critter's turn.
        static void InhalerStates(GasAndLiquidConsumerMonitor __instance)
        {
            __instance.looking.Enter("CritterNeedsFirst", smi =>
            {
                if (Ask(smi.GetComponent<Brain>())) ToInhale++;
            });
        }

        /// ThreatMonitor.InitializeStates: a critter looking for a target or under threat is
        /// tracked while it is in that state, and each of its turns moves its due time.
        static void ThreatStates(ThreatMonitor __instance)
        {
            foreach (var state in new[] { __instance.safe.seeking, __instance.threatened.creature })
                state.Enter("CritterNeedsFirst", ThreatTrack).PreBrainUpdate(ThreatTurned).Exit("CritterNeedsFirst", ThreatUntrack);
        }

        static void ThreatTrack(ThreatMonitor.Instance smi)
        {
            // Duplicants seek too; only the critter group's brains are tracked.
            if (smi.IAmADuplicant) return;
            var brain = smi.GetComponent<Brain>();
            if (brain == null || !brain.HasTag(GameTags.CreatureBrain)) return;
            threatTurns.Track(smi, Time.time + Gap());
        }

        static void ThreatTurned(ThreatMonitor.Instance smi) => threatTurns.Turned(smi, Time.time + Gap());

        static void ThreatUntrack(ThreatMonitor.Instance smi) => threatTurns.Remove(smi);

        /// The round of turns the game gives the critter group at speed 1 and 30 fps (5 a frame).
        static float Gap() => CritterGroup() is { } group ? group.BrainCount / (float)BrainRate.CritterRate : 0f;

        static BrainScheduler.BrainGroup CritterGroup()
        {
            var current = Game.BrainScheduler;
            if (current == null) return null;
            if (critters == null || !ReferenceEquals(current, critterScheduler))
            {
                critterScheduler = current;
                critters = null;
                foreach (var group in current.brainGroups)
                    if (group is BrainScheduler.CreatureBrainGroup) critters = group;
            }
            return critters;
        }

        static BrainScheduler critterScheduler;

        static bool ThreatReady(ThreatMonitor.Instance smi)
        {
            // A freed state machine (its colony left) has no state machine: isMasterNull would throw.
            if (smi.GetStateMachine() == null || smi.isMasterNull || !smi.IsRunning())
            {
                threatTurns.Remove(smi);
                return false;
            }
            return smi.IsInsideState(smi.sm.threatened) || ThreatTurnTarget(smi);
        }

        static void ThreatAsk(ThreatMonitor.Instance smi)
        {
            if (Ask(smi.GetComponent<Brain>())) ToAttack++;
        }

        /// Whether the critter's next turn would find a target: ThreatMonitor.GatherThreats'
        /// tests, read only, over the same box and at most as many entries. It stops at the
        /// first, and keeps no list.
        [MirrorsGame("System.Void ThreatMonitor/Instance::GatherThreats()")]
        static bool ThreatTurnTarget(ThreatMonitor.Instance smi)
        {
            var extents = new Extents(Grid.PosToCell(smi.gameObject), smi.def.maxSearchDistance);
            targetFound = false;
            targetsLeft = smi.def.maxSearchEntities;
            if (targetsLeft > 0)
                GameScenePartitioner.Instance.VisitEntries(extents.x, extents.y, extents.width, extents.height, GameScenePartitioner.Instance.attackableEntitiesLayer, visitTarget, smi);
            return targetFound;
        }

        static bool targetFound;
        static int targetsLeft;
        static readonly Func<object, ThreatMonitor.Instance, Util.IterationInstruction> visitTarget = VisitTarget;

        static Util.IterationInstruction VisitTarget(object obj, ThreatMonitor.Instance smi)
        {
            targetsLeft--;
            if (obj is FactionAlignment fa && fa.transform != null && fa != smi.alignment
                && (smi.def.friendlyCreatureTags == null || !fa.kprefabID.HasAnyTags(smi.def.friendlyCreatureTags))
                && fa.IsAlignmentActive()
                && (FactionManager.Instance.GetDisposition(smi.alignment.Alignment, fa.Alignment) == FactionManager.Disposition.Attack
                    || (smi.attackOwnFaction && fa.Alignment == smi.alignment.Alignment))
                && smi.navigator.CanReach(fa.attackable.GetCell(), smi.def.offsets))
                targetFound = true;
            return targetFound || targetsLeft <= 0 ? Util.IterationInstruction.Halt : Util.IterationInstruction.Continue;
        }

        /// A meal, an excretion or the monitor's start: its excrete condition cannot hold before
        /// the game's minimum time after it; checked then.
        static void MealOrExcretion(CreatureCalorieMonitor.Instance __instance)
        {
            excreteChecks.Set(__instance, __instance.lastMealOrPoopTime + __instance.def.minimumTimeBeforePooping);
        }

        /// After the frame's brain turns: the excrete checks that have come due.
        static void Frame()
        {
            float now = Time.time;
            if (threatTurns.Count > 0 && CritterGroup() is { } critterGroup)
            {
                // The group's turns this frame: brain-rate's cap when it ran the group, else the game's count.
                int turns = BrainRate.CritterTurnsFrame == Time.frameCount ? BrainRate.CritterTurns : probeCount(critterGroup);
                threatTurns.Serve(now, critterGroup.BrainCount / (float)BrainRate.CritterRate, turns, threatReady, threatAsk);
                if (threatTurns.LastError is { } error && !dropLogged)
                {
                    dropLogged = true;
                    global::Debug.LogWarning($"{Name}: a critter's threat turn test threw, so it is no longer tracked ({threatTurns.Dropped} so far this game): {error}");
                }
            }
            if (excreteChecks.Count == 0) return;
            foreach (var smi in excreteChecks.Take(now))
            {
                // CreatureCalorieMonitor.ReadyToPoop, as the game tests it on the critter's turn.
                if (smi.GetStateMachine() == null || !smi.IsRunning() || !smi.stomach.IsReadyToPoop()) continue;
                if (now - smi.lastMealOrPoopTime < smi.def.minimumTimeBeforePooping || smi.IsInsideState(smi.sm.pause)) continue;
                if (Ask(smi.GetComponent<Brain>())) ToExcrete++;
            }
            if (verify && GameClock.Instance != null && GameClock.Instance.GetTime() >= nextLog)
            {
                nextLog = GameClock.Instance.GetTime() + 600f;
                global::Debug.Log($"{Name}: {ToInhale} turns asked to inhale, {ToExcrete} to excrete, {ToAttack} to attack; {followed} followed, {late} later than their place in the queue");
            }
        }

        static bool Ask(Brain brain)
        {
            if (brain == null || Game.BrainScheduler == null) return false;
            if (verify) asked[brain] = (Time.frameCount, Queued(brain));
            Game.BrainScheduler.PrioritizeBrain(brain);
            return true;
        }

        // ---- Verify ----

        static readonly Dictionary<Brain, (int Frame, int Ahead)> asked = new Dictionary<Brain, (int, int)>();
        static long followed, late;
        static float nextLog;
        public static long Followed => followed;
        public static long Late => late;

        /// The brains queued in the brain's group before it.
        static int Queued(Brain brain)
        {
            foreach (var group in Game.BrainScheduler.brainGroups)
                if (brain.HasTag(group.tag)) return group.priorityBrains.Count;
            return 0;
        }

        static void Turn(Brain __instance)
        {
            if (asked.Count == 0 || !asked.TryGetValue(__instance, out var at)) return;
            asked.Remove(__instance);
            followed++;
            if (Time.frameCount - at.Frame > at.Ahead + 2) late++;
        }
    }
}
