using HarmonyLib;

namespace OniMoono.GameTweaks
{
    /// Game tweak `low-fps-timing` (**changes timing**): the game's timing at a low frame rate as
    /// at a high one, as far as its parts go (docs/research/frame-rate-dependence.md):
    ///
    /// - FrameTimeCap: a frame's game time capped at the sim's 0.2 s, so animations, the
    ///   automation clock and Time.time timers keep the sim's pace;
    /// - LogicTicks: a frame's later automation ticks run between its sim sub-ticks.
    ///
    /// One row and one switch. Each part checks for other mods' patches and stands down on its
    /// own (`low-fps-timing: PART off (...)` in Player.log), so a mod that patches what one part
    /// relies on turns off that part only. Each part's class declares its own game members under
    /// this tweak's name. Later fixes of this kind join here as parts, not as rows.
    public static class LowFpsTiming
    {
        public const string Name = "low-fps-timing";

        public static void Install(Harmony harmony)
        {
            FrameTimeCap.Install(harmony);
            LogicTicks.Install(harmony);
        }
    }
}
