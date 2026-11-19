using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using BrainGroup = BrainScheduler.BrainGroup;

    /// Game tweak `brain-rate` (**changes behaviour**): brain updates held to a rate of real time
    /// instead of one batch per rendered frame.
    ///
    /// BrainScheduler updates one Duplicant brain and five critter brains every rendered frame (its
    /// groups' InitialProbeCount has no time term: its constants state a 30 fps frame), so how
    /// often each brain re-evaluates grows with the frame rate. A brain update runs its sensors
    /// and, when they ask, a chore search: 1.4 ms of a 12.6 ms frame on a late-game colony, 2.4 ms
    /// on a pile-heavy one.
    ///
    /// Each group may update at most 30 Duplicant brains or 150 critter brains a second of real
    /// time (BrainRateAllowance), spread evenly over frames, and never more than the game's count
    /// in a frame: at 30 fps or below nothing changes. A frame whose allowance is 0 skips the group
    /// (BrainGroup.RenderEveryTick and PostRenderEveryTick), so the Duplicant group's alternation
    /// between its priority queue and its round robin counts only the frames it runs. A mod's group
    /// is left as it is.
    ///
    /// The rate rises with the group's size and the game's speed (BrainRateAllowance's floors,
    /// issue #85): every brain gets a turn within 15 game seconds while the game's count a frame
    /// allows it. Without the floors, 2,400 critters at speed 3 got a turn every 48 game seconds:
    /// Slicksters stayed hungry and none excreted.
    ///
    /// Within the capped turns a critter ready to inhale its food, to excrete or to attack goes
    /// first: the tweak installs CritterNeedsFirst's hooks, whether or not that tweak is on. With
    /// the cap alone a gas-eater in a large colony got too few turns to stay fed (issue #85).
    ///
    /// What differs: above 30 fps a Duplicant re-evaluates its errands, and a critter its
    /// behaviours, as often per real second as the game would at 30 fps; critters ready to inhale
    /// or excrete act sooner than the game's order has them, the others that much later.
    ///
    /// Measured (Linux, 2026-10-25): a late-game colony at 35–39 fps, 6.0 → 4.7–5.1 brain updates a
    /// frame and 2.7–2.8 → 2.2–2.45 ms; a mid-game colony at 42–45 fps, 6.0 → 4.1–4.3 and 1.82 →
    /// 1.35–1.45 ms; a pile-heavy colony at 14 fps unchanged. The gain grows with the frame rate.
    [GameMember(GameUse.Uses, "System.Int32 BrainScheduler/BrainGroup::InitialProbeCount()")]
    [GameMember(GameUse.Uses, "System.Int32 BrainScheduler/BrainGroup::get_BrainCount()")]
    [GameMember(GameUse.Postfix, "System.Int32 BrainScheduler/CreatureBrainGroup::InitialProbeCount()")]
    [GameMember(GameUse.Postfix, "System.Int32 BrainScheduler/DupeBrainGroup::InitialProbeCount()")]
    // CritterNeedsFirst's hooks, which this tweak installs too (its Frame postfix is the Postfix below).
    [GameMember(GameUse.Postfix, "System.Void GasAndLiquidConsumerMonitor::InitializeStates(StateMachine/BaseState&)")]
    [GameMember(GameUse.Postfix, "System.Void CreatureCalorieMonitor/Instance::OnCaloriesConsumed(System.Object)")]
    [GameMember(GameUse.Postfix, "System.Void CreatureCalorieMonitor/Instance::Poop(PoopData)")]
    [GameMember(GameUse.Postfix, "System.Void CreatureCalorieMonitor/Instance::StartSM()")]
    [GameMember(GameUse.Postfix, "System.Void ThreatMonitor::InitializeStates(StateMachine/BaseState&)")]
    [GameMember(GameUse.Postfix, "System.Void Brain::UpdateBrain()", Conditional = true)]
    [GameMember(GameUse.Postfix, "System.Void Game::OnPrefabInit()")]
    [GameMember(GameUse.Prefix | GameUse.Postfix | GameUse.Replaces, "System.Void BrainScheduler::RenderEveryTick(System.Single)", "6014ab20d776bb7b linux-744825 win-744825 linux-763796 win-763796", "0eafd0e3e60c8f62 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761211 win-761654 win-762107 win-762230 win-763023 win-763325 win-763612")]
    public static class BrainRate
    {
        public const string Name = "brain-rate";

        /// Updates a second of real time: the game's per-frame counts at its stated 30 fps.
        public const double DuplicantRate = 30, CritterRate = 150;

        static Func<BrainGroup, int> probeCount;
        static Type dupeGroup, critterGroup;
        static readonly Dictionary<BrainGroup, BrainRateAllowance> allowances = new Dictionary<BrainGroup, BrainRateAllowance>();
        /// The running group's allowance this frame, while it runs (InitialProbeCount's answer).
        static int current = -1;
        static BrainScheduler scheduler;

        /// The critter group's allowance and the frame it was for (CritterNeedsFirst's bound on
        /// threat turns counts the turns the group was given).
        public static int CritterTurns, CritterTurnsFrame = -1;

        public static void Install(Harmony harmony)
        {
            dupeGroup = typeof(BrainScheduler.DupeBrainGroup);
            critterGroup = typeof(BrainScheduler.CreatureBrainGroup);
            probeCount = AccessTools.MethodDelegate<Func<BrainGroup, int>>(AccessTools.Method(typeof(BrainGroup), "InitialProbeCount"));
            // Default priority, no stand-down check yet: a mod's later bool prefix is skipped
            // (docs/research/tweak-step-aside-audit.md, #15).
            harmony.Patch(AccessTools.Method(typeof(BrainScheduler), nameof(BrainScheduler.RenderEveryTick)), prefix: new HarmonyMethod(typeof(BrainRate), nameof(RenderEveryTick)));
            var cap = new HarmonyMethod(typeof(BrainRate), nameof(InitialProbeCount));
            harmony.Patch(AccessTools.Method(dupeGroup, "InitialProbeCount"), postfix: cap);
            harmony.Patch(AccessTools.Method(critterGroup, "InitialProbeCount"), postfix: cap);
            // Critters with a need go first within the cap.
            CritterNeedsFirst.Install(harmony);
        }

        /// BrainScheduler.RenderEveryTick: each group as the game runs it, with this frame's
        /// allowance as its probe count; a group with none this frame is not run.
        static bool RenderEveryTick(BrainScheduler __instance, float dt)
        {
            if (Game.IsQuitting() || KMonoBehaviour.isLoadingScene) return false;
            if (!ReferenceEquals(scheduler, __instance)) { scheduler = __instance; allowances.Clear(); }
            float real = Time.unscaledDeltaTime;
            foreach (var group in __instance.brainGroups)
            {
                int allowed = -1;
                var type = group.GetType();
                if (type == dupeGroup || type == critterGroup)
                {
                    if (!allowances.TryGetValue(group, out var allowance))
                        allowances[group] = allowance = new BrainRateAllowance(type == dupeGroup ? DuplicantRate : CritterRate);
                    allowed = allowance.Take(real, probeCount(group), group.BrainCount, Time.timeScale);
                    if (type == critterGroup) { CritterTurns = allowed; CritterTurnsFrame = Time.frameCount; }
                    if (allowed == 0) continue;
                }
                current = allowed;
                try
                {
                    group.RenderEveryTick(dt);
                    group.PostRenderEveryTick(dt);
                }
                finally { current = -1; }
            }
            return false;
        }

        static void InitialProbeCount(ref int __result)
        {
            if (current >= 0 && current < __result) __result = current;
        }
    }
}
