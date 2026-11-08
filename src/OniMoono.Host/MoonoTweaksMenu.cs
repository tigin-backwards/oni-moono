using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Host.GameTweaks;

namespace OniMoono.Host
{
    /// The main menu's "Moono Tweaks" button and screen (OniMoono.GameTweaks.MoonoTweaksMenu,
    /// built from the game's own UI): the options EffectiveOptions resolves, each with a toggle,
    /// saved to the settings file (PlayerSettings) and applied by restarting the game. This side
    /// gives the screen its rows and saves what it chose; options the command line set are shown
    /// but not changeable. Each row's value is a number: 0 or 1 for a toggle (off, on), else the
    /// index in its Choices (the GPU's).
    internal static class MoonoTweaksMenu
    {
        public static void Install(EffectiveOptions effective, PlayerSettings file, string settingsPath)
        {
            // The rows, their sections and copy from the one catalog (GameTweakNames.Rows).
            var rows = GameTweakNames.Rows;
            static int Flag(bool on) => on ? 1 : 0;
            (int Value, bool Locked, int Default) State(TweakInfo row) => row.Id switch
            {
                GameTweakNames.SplashRow => (Flag(effective.Splash == "moo"), effective.SplashSource == OptionSource.CommandLine, Flag(EffectiveOptions.DefaultSplash == "moo")),
                GameTweakNames.FrameStatsRow => (Flag(effective.FrameStats), effective.FrameStatsSource == OptionSource.CommandLine, 0),
                GameTweakNames.LogPathsRow => (Flag(effective.HideLogPaths), effective.LogPathsSource == OptionSource.CommandLine, Flag(EffectiveOptions.DefaultLogPaths == "hidden")),
                GameTweakNames.GpuRow => (Array.IndexOf(row.Choices!, effective.Gpu), effective.GpuSource == OptionSource.CommandLine,
                    Array.IndexOf(row.Choices!, EffectiveOptions.DefaultGpu)),
                GameTweakNames.RendererRow => (Array.IndexOf(row.Choices!, effective.Renderer), effective.RendererSource == OptionSource.CommandLine,
                    Array.IndexOf(row.Choices!, EffectiveOptions.DefaultRenderer)),
                GameTweakNames.DxgiRow => (Flag(effective.Dxgi), effective.DxgiSource == OptionSource.CommandLine, Flag(EffectiveOptions.DefaultDxgi)),
                GameTweakNames.StallRepeatsRow => (Flag(effective.StallRepeats), effective.StallRepeatsSource == OptionSource.CommandLine, Flag(EffectiveOptions.DefaultStallRepeats)),
                GameTweakNames.UpdateCheckRow => (Flag(effective.UpdateCheck), false, Flag(EffectiveOptions.DefaultUpdateCheck)),
                GameTweakNames.GcRow => (Array.IndexOf(row.Choices!, effective.Gc), effective.GcSource == OptionSource.CommandLine,
                    Array.IndexOf(row.Choices!, EffectiveOptions.DefaultGc)),
                var name => (Flag(effective.Tweaks[name].On), effective.Tweaks[name].Source == OptionSource.CommandLine,
                    Flag(EffectiveOptions.DefaultOn(name, GameTweakNames.OffByDefault))),
            };
            // A tweak this game version can't run (GameTweaks.Unavailable): shown off, greyed out
            // and not changeable, its setting left as the file has it.
            var unavailable = GameTweaks.GameTweaks.Unavailable;
            var off = rows.Select(r => r.IsTweak && unavailable.ContainsKey(r.Id)).ToArray();
            var states = rows.Select(State).Select((s, i) => off[i] ? (Value: 0, Locked: true, s.Default) : s).ToArray();
            // The screen's text goes by its translation keys (TweakStrings; "" for none), with
            // every key's English: the screen shows the game's language from translations/CODE.po
            // beside the player, English where it has none. The launch options stay as they are.
            static string GainKey(TweakInfo row) => GameTweakNames.GainText(row) is { } gain ? TweakStrings.GainKey(gain.Gain) : "";
            static string DetailKey(TweakInfo row) => row.Detail.Length > 0 ? TweakStrings.RowKey(row, "DETAIL") : "";
            Func<Dictionary<string, string>, string[]> resolve = po => TweakStrings.Resolve(po);
            string translations = Path.Combine(AppContext.BaseDirectory, "translations");

            var saved = file;
            Func<string[], int[], string?> save = (ids, values) =>
            {
                var tweaks = new Dictionary<string, bool>(StringComparer.Ordinal);
                bool? splash = null, frameStats = null, hideLogPaths = null, dxgi = null, stallRepeats = null, updateCheck = null;
                string? gpu = null, renderer = null, gc = null;
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] == GameTweakNames.SplashRow) splash = values[i] != 0;
                    else if (ids[i] == GameTweakNames.FrameStatsRow) frameStats = values[i] != 0;
                    else if (ids[i] == GameTweakNames.LogPathsRow) hideLogPaths = values[i] != 0;
                    else if (ids[i] == GameTweakNames.GpuRow) gpu = rows.First(r => r.Id == GameTweakNames.GpuRow).Choices![values[i]];
                    else if (ids[i] == GameTweakNames.RendererRow) renderer = rows.First(r => r.Id == GameTweakNames.RendererRow).Choices![values[i]];
                    else if (ids[i] == GameTweakNames.DxgiRow) dxgi = values[i] != 0;
                    else if (ids[i] == GameTweakNames.StallRepeatsRow) stallRepeats = values[i] != 0;
                    else if (ids[i] == GameTweakNames.UpdateCheckRow) updateCheck = values[i] != 0;
                    else if (ids[i] == GameTweakNames.GcRow) gc = rows.First(r => r.Id == GameTweakNames.GcRow).Choices![values[i]];
                    else tweaks[ids[i]] = values[i] != 0;
                }
                var next = EffectiveOptions.Apply(saved, GameTweakNames.OffByDefault, tweaks, splash, frameStats, hideLogPaths, gpu, renderer, dxgi, updateCheck, gc, stallRepeats);
                try
                {
                    next.Write(settingsPath);
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
                {
                    EngineLog.Engine($"settings: could not write {settingsPath}: {e.Message}");
                    return e.Message;
                }
                saved = next;
                EngineLog.Engine($"settings: saved {settingsPath}: {next.ToJson(indented: false)}");
                return null;
            };

            var install = GameTweaks.GameTweaks.TypedAssembly.GetType("OniMoono.GameTweaks.MoonoTweaksMenu", throwOnError: true)!.GetMethod("Install")!;
            install.Invoke(null, [new Harmony("OniMoono.Host.MoonoTweaksMenu"),
                rows.Select(r => r.Id).ToArray(), rows.Select(r => TweakStrings.GroupKey(r.Group)).ToArray(),
                rows.Select(r => TweakStrings.RowKey(r, "TITLE")).ToArray(), rows.Select(r => TweakStrings.RowKey(r, "EFFECT")).ToArray(),
                rows.Select(DetailKey).ToArray(), rows.Select(GainKey).ToArray(), rows.Select(r => r.LaunchOption).ToArray(),
                rows.Select(r => r.Group.ChangesGameplay).ToArray(), rows.Select(r => r.Group == GameTweakNames.BugFixes).ToArray(),
                rows.Select(r => r.Choices?.Select(c => TweakStrings.ChoiceKey(r, c)).ToArray()).ToArray(),
                states.Select(s => s.Value).ToArray(), states.Select(s => s.Default).ToArray(), states.Select(s => s.Locked).ToArray(), off,
                settingsPath, save,
                TweakStrings.All.Select(t => t.Key).ToArray(), TweakStrings.All.Select(t => t.English).ToArray(), resolve, translations]);

            // The mods' patches on the tweaks' methods, read at each main menu: the tweaks on this
            // run, the methods their load-time rewrites changed, and the transpilers reviewed
            // against compact-nav-links' rewrite.
            try
            {
                TweakRewrites.Prepare();
                Func<System.Reflection.MethodBase, string?> rewritten = TweakRewrites.RewrittenBy;
                GameTweaks.GameTweaks.TypedAssembly.GetType("OniMoono.GameTweaks.ModOverlaps", throwOnError: true)!.GetMethod("Install")!
                    .Invoke(null, [effective.EnabledTweaks.Where(GameTweaks.GameTweaks.Available).ToArray(), rewritten,
                        CompactNavLinks.reviewed.Select(r => r.Target).ToArray(), CompactNavLinks.reviewed.Select(r => r.Transpiler).ToArray()]);
            }
            catch (Exception e)
            {
                EngineLog.Engine($"mod overlaps: not installed: {e.GetType().Name}: {e.Message}");
            }

            // The update badge beside the button: the check runs in the background from now on.
            if (effective.UpdateCheck)
            {
                UpdateCheck.Start(EngineConfig.PersistentDataPath, GameVersionCheck.ThisUpdate == "U60", EngineLog.Engine);
                Func<string[]?> poll = () => !UpdateCheck.Done ? null
                    : UpdateCheck.Result is { } found ? [found.Tag, found.Url, found.Notes, found.Truncated ? "1" : "0"] : [];
                GameTweaks.GameTweaks.TypedAssembly.GetType("OniMoono.GameTweaks.UpdateBadge", throwOnError: true)!.GetMethod("Install")!
                    .Invoke(null, [poll]);
            }
        }
    }
}
