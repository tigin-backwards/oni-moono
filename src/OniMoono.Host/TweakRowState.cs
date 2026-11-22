#nullable enable
using System;
using OniMoono.Host.GameTweaks;

namespace OniMoono.Host
{
    /// What the Moono Tweaks screen shows on a row (MoonoTweaksMenu), from the resolved options
    /// and the settings file.
    internal static class TweakRowState
    {
        /// A row's value (0 or 1, or the index in its Choices), whether it is locked (the command
        /// line set it) and its default.
        public static (int Value, bool Locked, int Default) Of(TweakInfo row, EffectiveOptions effective, PlayerSettings file)
        {
            static int Flag(bool on) => on ? 1 : 0;
            return row.Id switch
            {
                GameTweakNames.SplashRow => (Flag(effective.Splash == "moo"), effective.SplashSource == OptionSource.CommandLine, Flag(EffectiveOptions.DefaultSplash == "moo")),
                GameTweakNames.FrameStatsRow => (Flag(effective.FrameStats), effective.FrameStatsSource == OptionSource.CommandLine, 0),
                GameTweakNames.LogPathsRow => (Flag(effective.HideLogPaths), effective.LogPathsSource == OptionSource.CommandLine, Flag(EffectiveOptions.DefaultLogPaths == "hidden")),
                GameTweakNames.GpuRow => Choice(row, effective.Gpu, effective.GpuSource == OptionSource.CommandLine, file.Gpu, EffectiveOptions.DefaultGpu),
                GameTweakNames.RendererRow => Choice(row, effective.Renderer, effective.RendererSource == OptionSource.CommandLine, file.Renderer, EffectiveOptions.DefaultRenderer),
                GameTweakNames.DxgiRow => (Flag(effective.Dxgi), effective.DxgiSource == OptionSource.CommandLine, Flag(EffectiveOptions.DefaultDxgi)),
                GameTweakNames.StallRepeatsRow => (Flag(effective.StallRepeats), effective.StallRepeatsSource == OptionSource.CommandLine, Flag(EffectiveOptions.DefaultStallRepeats)),
                GameTweakNames.UpdateCheckRow => (Flag(effective.UpdateCheck), false, Flag(EffectiveOptions.DefaultUpdateCheck)),
                GameTweakNames.GcRow => Choice(row, effective.Gc, effective.GcSource == OptionSource.CommandLine, file.Gc, EffectiveOptions.DefaultGc),
                var name => (Flag(effective.Tweaks[name].On), effective.Tweaks[name].Source == OptionSource.CommandLine,
                    Flag(EffectiveOptions.DefaultOn(name, GameTweakNames.OffByDefault))),
            };
        }

        /// A choice row's state. A running value that is not one of the row's choices (a renderer
        /// the command line chose that the screen does not offer) shows locked, at what the
        /// settings file keeps (else the default), so the screen neither breaks nor saves a value
        /// the player did not choose there.
        static (int Value, bool Locked, int Default) Choice(TweakInfo row, string value, bool locked, string? saved, string defaultValue)
        {
            int byDefault = Array.IndexOf(row.Choices!, defaultValue);
            int i = Array.IndexOf(row.Choices!, value);
            if (i >= 0) return (i, locked, byDefault);
            int kept = saved == null ? -1 : Array.IndexOf(row.Choices!, saved);
            return (kept >= 0 ? kept : byDefault, true, byDefault);
        }
    }
}
