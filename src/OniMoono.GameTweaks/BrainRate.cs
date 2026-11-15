using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using BrainGroup = BrainScheduler.BrainGroup;

    /// Game tweak `brain-rate` (opt-in, **changes behaviour**): Duplicant brain updates held to a
    /// rate of real time instead of one per rendered frame.
    ///
    /// BrainScheduler updates one Duplicant brain every rendered frame (its group's
    /// InitialProbeCount, with no time term: its own constants state a 30 fps frame), so how
    /// often each Duplicant re-evaluates its errands grows with the frame rate. A brain update
    /// runs its sensors and, when they ask, a chore search: about 2 ms a turn in a late-game
    /// colony, three quarters of all brain time there.
    ///
    /// With the tweak the Duplicant group may update at most 30 brains a second of real time
    /// (BrainRateAllowance), spread evenly over frames, and never more than the game's own count
    /// in a frame: at 30 fps or below nothing changes; at 120 fps the group runs every fourth
    /// frame. A frame whose allowance is 0 skips the group altogether (BrainGroup.RenderEveryTick
    /// and PostRenderEveryTick), so its alternation between its priority queue and its round
    /// robin counts only the frames it runs, and the brains asking for priority (idle Duplicants)
    /// are still served first. The rate rises with the group's size and the game's speed
    /// (BrainRateAllowance's floors): every Duplicant gets a turn within 15 game seconds while the
    /// game's own count a frame allows it.
    ///
    /// Every other group runs as the game runs it: a mod's, and the critters'. The tweak capped
    /// critter brains at 150 a second until issue #85: in a colony of 2,400 critters at speed 3
    /// that gave each a turn every 48 game seconds whatever the frame rate, Slicksters stayed
    /// hungry and none excreted (a hungry critter eats before it excretes). Critter turns are
    /// about 2% of the main thread with or without a cap (docs/research/brain-probes.md), so the
    /// cap went.
    ///
    /// What changes: above 30 fps a Duplicant re-evaluates its errands less often per real second
    /// than without the tweak, as often as the game would at 30 fps.
    ///
    /// Measured with the critter cap still in (Linux, 2026-10-25): U57 at 35–39 fps, 6.0 →
    /// 4.7–5.1 brain updates a frame and 2.7–2.8 → 2.2–2.45 ms; Gluttony at 14 fps unchanged.
    [GameMember(GameUse.Uses, "System.Int32 BrainScheduler/BrainGroup::InitialProbeCount()")]
    [GameMember(GameUse.Uses, "System.Int32 BrainScheduler/BrainGroup::get_BrainCount()")]
    [GameMember(GameUse.Postfix, "System.Int32 BrainScheduler/DupeBrainGroup::InitialProbeCount()")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void BrainScheduler::RenderEveryTick(System.Single)", "6014ab20d776bb7b linux-744825 win-744825", "0eafd0e3e60c8f62 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    public static class BrainRate
    {
        public const string Name = "brain-rate";

        /// Updates a second of real time: the game's per-frame count at its stated 30 fps.
        public const double DuplicantRate = 30;

        static Func<BrainGroup, int> probeCount;
        static Type dupeGroup;
        static readonly Dictionary<BrainGroup, BrainRateAllowance> allowances = new Dictionary<BrainGroup, BrainRateAllowance>();
        /// The running group's allowance this frame, while it runs (InitialProbeCount's answer).
        static int current = -1;
        static BrainScheduler scheduler;

        public static void Install(Harmony harmony)
        {
            dupeGroup = typeof(BrainScheduler.DupeBrainGroup);
            probeCount = AccessTools.MethodDelegate<Func<BrainGroup, int>>(AccessTools.Method(typeof(BrainGroup), "InitialProbeCount"));
            harmony.Patch(AccessTools.Method(typeof(BrainScheduler), nameof(BrainScheduler.RenderEveryTick)), prefix: new HarmonyMethod(typeof(BrainRate), nameof(RenderEveryTick)));
            var cap = new HarmonyMethod(typeof(BrainRate), nameof(InitialProbeCount));
            harmony.Patch(AccessTools.Method(dupeGroup, "InitialProbeCount"), postfix: cap);
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
                if (type == dupeGroup)
                {
                    if (!allowances.TryGetValue(group, out var allowance))
                        allowances[group] = allowance = new BrainRateAllowance(DuplicantRate);
                    allowed = allowance.Take(real, probeCount(group), group.BrainCount, Time.timeScale);
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
