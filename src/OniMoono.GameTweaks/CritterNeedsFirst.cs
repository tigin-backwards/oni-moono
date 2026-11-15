using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `critter-needs-first` (opt-in, **changes behaviour**; issue #85): a critter that
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
    /// The tweak asks the game's own priority queue (BrainScheduler.PrioritizeBrain, which the
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
    /// `brain-rate` installs the same hooks for its critter cap, where turns are scarce at any
    /// frame rate; with both tweaks on they are installed once.
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
    [GameMember(GameUse.Postfix, "System.Void BrainScheduler::RenderEveryTick(System.Single)")]
    [GameMember(GameUse.Uses, "System.Void BrainScheduler::PrioritizeBrain(Brain)")]
    // Verify mode's count of the turns that followed.
    [GameMember(GameUse.Postfix, "System.Void Brain::UpdateBrain()", Conditional = true)]
    [GameMember(GameUse.Uses, "BrainScheduler Game::BrainScheduler")]
    public static class CritterNeedsFirst
    {
        public const string Name = "critter-needs-first";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly DueChecks<CreatureCalorieMonitor.Instance> excreteChecks = new DueChecks<CreatureCalorieMonitor.Instance>();
        static BrainScheduler scheduler;

        /// For tests (E2eDriver's state reads): the turns asked for, by need.
        public static long ToInhale, ToExcrete;

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
            harmony.Patch(AccessTools.Method(typeof(BrainScheduler), nameof(BrainScheduler.RenderEveryTick)), postfix: new HarmonyMethod(typeof(CritterNeedsFirst), nameof(Frame)));
            if (verify) harmony.Patch(AccessTools.Method(typeof(Brain), nameof(Brain.UpdateBrain)), postfix: new HarmonyMethod(typeof(CritterNeedsFirst), nameof(Turn)));
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

        /// A meal, an excretion or the monitor's start: its excrete condition cannot hold before
        /// the game's minimum time after it; checked then.
        static void MealOrExcretion(CreatureCalorieMonitor.Instance __instance)
        {
            excreteChecks.Set(__instance, __instance.lastMealOrPoopTime + __instance.def.minimumTimeBeforePooping);
        }

        /// After the frame's brain turns: the excrete checks that have come due.
        static void Frame(BrainScheduler __instance)
        {
            if (!ReferenceEquals(scheduler, __instance))
            {
                scheduler = __instance;
                excreteChecks.Clear();
                asked.Clear();
            }
            if (excreteChecks.Count == 0) return;
            float now = Time.time;
            foreach (var smi in excreteChecks.Take(now))
            {
                // CreatureCalorieMonitor.ReadyToPoop, as the game tests it on the critter's turn.
                if (!smi.IsRunning() || !smi.stomach.IsReadyToPoop()) continue;
                if (now - smi.lastMealOrPoopTime < smi.def.minimumTimeBeforePooping || smi.IsInsideState(smi.sm.pause)) continue;
                if (Ask(smi.GetComponent<Brain>())) ToExcrete++;
            }
            if (verify && GameClock.Instance != null && GameClock.Instance.GetTime() >= nextLog)
            {
                nextLog = GameClock.Instance.GetTime() + 600f;
                global::Debug.Log($"{Name}: {ToInhale} turns asked to inhale, {ToExcrete} to excrete; {followed} followed, {late} later than their place in the queue");
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
