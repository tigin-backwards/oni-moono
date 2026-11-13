using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using BrainGroup = BrainScheduler.BrainGroup;

    /// Game tweak `brain-rate` (opt-in, **changes behaviour**): brain updates held to a rate of
    /// real time instead of one batch per rendered frame.
    ///
    /// BrainScheduler updates one Duplicant brain and five critter brains every rendered frame
    /// (its groups' InitialProbeCount, with no time term: its own constants state a 30 fps frame),
    /// so how often each Duplicant re-evaluates its errands, and each critter its behaviours,
    /// grows with the frame rate. A brain update runs its sensors and, when they ask, a chore
    /// search; Moono runs U57 at about 7 times the original's frame rate, so about 7 times as many
    /// a game second (1.4 ms of a 12.6 ms frame on U57, 2.4 ms on Gluttony).
    ///
    /// With the tweak each group may update at most 30 Duplicant brains or 150 critter brains a
    /// second of real time (BrainRateAllowance), spread evenly over frames, and never more than
    /// the game's own count in a frame: at 30 fps or below nothing changes; at 120 fps the
    /// Duplicant group runs every fourth frame and the critter group gets one or two brains a
    /// frame. A frame whose allowance is 0 skips the group altogether (BrainGroup.RenderEveryTick
    /// and PostRenderEveryTick), so the Duplicant group's alternation between its priority queue
    /// and its round robin counts only the frames it runs, and the brains asking for priority
    /// (idle Duplicants, threatened critters) are still served first. A group of a mod's is left
    /// as it is.
    ///
    /// What changes: above 30 fps a Duplicant re-evaluates its errands, and a critter its
    /// behaviours, less often per real second than without the tweak, as often as the game would
    /// at 30 fps (and still more often per game second than the original at U57's 11 fps).
    ///
    /// Measured (Linux, 2026-10-25): U57 at 35–39 fps, 6.0 → 4.7–5.1 brain updates a frame and
    /// 2.7–2.8 → 2.2–2.45 ms; 2minnow at 42–45 fps, 6.0 → 4.1–4.3 and 1.82 → 1.35–1.45 ms;
    /// Gluttony at 14 fps unchanged. The gain grows with the frame rate (at 79 fps, 2.3 a frame).
    [GameMember(GameUse.Uses, "System.Int32 BrainScheduler/BrainGroup::InitialProbeCount()")]
    [GameMember(GameUse.Postfix, "System.Int32 BrainScheduler/CreatureBrainGroup::InitialProbeCount()")]
    [GameMember(GameUse.Postfix, "System.Int32 BrainScheduler/DupeBrainGroup::InitialProbeCount()")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void BrainScheduler::RenderEveryTick(System.Single)", "6014ab20d776bb7b linux-744825 win-744825", "0eafd0e3e60c8f62 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
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

        public static void Install(Harmony harmony)
        {
            dupeGroup = typeof(BrainScheduler.DupeBrainGroup);
            critterGroup = typeof(BrainScheduler.CreatureBrainGroup);
            probeCount = AccessTools.MethodDelegate<Func<BrainGroup, int>>(AccessTools.Method(typeof(BrainGroup), "InitialProbeCount"));
            harmony.Patch(AccessTools.Method(typeof(BrainScheduler), nameof(BrainScheduler.RenderEveryTick)), prefix: new HarmonyMethod(typeof(BrainRate), nameof(RenderEveryTick)));
            var cap = new HarmonyMethod(typeof(BrainRate), nameof(InitialProbeCount));
            harmony.Patch(AccessTools.Method(dupeGroup, "InitialProbeCount"), postfix: cap);
            harmony.Patch(AccessTools.Method(critterGroup, "InitialProbeCount"), postfix: cap);
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
                    allowed = allowance.Take(real, probeCount(group));
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
