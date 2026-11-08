using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using OniMoono.Engine;

namespace OniMoono.Host.GameTweaks
{
    /// Optional changes to the game's own code, kept apart from the engine: the engine reproduces
    /// Unity, while these change how Klei's code does something, mostly without changing what it
    /// produces. Each is documented in its class with what it changes and what was measured. Which
    /// are on comes from the Moono Tweaks screen's settings file or the command line
    /// (`--game-tweak`; EffectiveOptions): by default every tweak but those that change behaviour.
    internal static class GameTweaks
    {
        static bool backgroundSave;

        /// OniMoono.GameTweaks.dll beside the host: the tweaks built against the game's own types,
        /// and the main menu's Moono Tweaks screen (MoonoTweaksMenu).
        public static System.Reflection.Assembly TypedAssembly => typed ??= System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(AppContext.BaseDirectory, "OniMoono.GameTweaks.dll"));
        static System.Reflection.Assembly? typed;

        public static void Install(IReadOnlySet<string> enabled)
        {
            bool On(string name) => enabled.Contains(name);
            if (enabled.Count == 0) return;
            // Each tweak patches under its own Harmony ID, so the Moono Tweaks screen can tell which
            // tweak a mod overlaps (OniMoono.GameTweaks.ModOverlapsCore.OwnerOf).
            static Harmony For(string name) => new Harmony("OniMoono.Host.GameTweaks." + name);
            backgroundSave = On(BackgroundSave.Name);
            // Both replace SaveLoader.CompressContents: with background-save on, it does the
            // compressing, with .NET's zlib when fast-save-compression is on too.
            if (On(FastSaveCompression.Name)) Report(FastSaveCompression.Name, () => FastSaveCompression.Install(For(FastSaveCompression.Name), compress: !backgroundSave));
            if (backgroundSave) Report(BackgroundSave.Name, () => BackgroundSave.Install(For(BackgroundSave.Name), fastCompression: On(FastSaveCompression.Name)));
            if (On(NoSaveGc.Name)) Report(NoSaveGc.Name, () => NoSaveGc.Install(For(NoSaveGc.Name)));
            if (On(PostLoadGc.Name)) Report(PostLoadGc.Name, () => PostLoadGc.Install(For(PostLoadGc.Name)));
            if (On(FastSerialization.Name)) Report(FastSerialization.Name, () => FastSerialization.Install(For(FastSerialization.Name)));
            if (On(FastParticleCollision.Name)) Report(FastParticleCollision.Name, () => FastParticleCollision.Install(For(FastParticleCollision.Name)));
            // Enabled at start-up (CompactNavLinks.Enable); this adds the load's summary.
            if (On(CompactNavLinks.Name)) Report(CompactNavLinks.Name, () => CompactNavLinks.Install(For(CompactNavLinks.Name)));
            else if (Environment.GetEnvironmentVariable("ONI_NAVLINKS_DUMP") is { Length: > 0 }) CompactNavLinks.InstallDump(For(CompactNavLinks.Name));
            // Enabled at start-up too (PagedPathGrids.Enable); this adds the summary in play.
            if (On(PagedPathGrids.Name)) Report(PagedPathGrids.Name, () => PagedPathGrids.Install(For(PagedPathGrids.Name)));
            // fast-load-screen's engine side; its game side is a typed tweak (below), which logs it.
            if (On(LoadScreenPreviews.Name)) LoadScreenPreviews.Install(For(LoadScreenPreviews.Name));
            // The tweaks built against the game's own types (src/OniMoono.GameTweaks).
            var typed = GameTweakNames.Typed.Where(On).ToArray();
            if (typed.Length > 0)
            {
                var patches = TypedAssembly.GetType("OniMoono.GameTweaks.Patches")!;
                var install = patches.GetMethod("Install")!;
                foreach (var name in typed)
                    Report(name, () =>
                    {
                        if (!(bool)install.Invoke(null, [For(name), name])!) throw new InvalidOperationException($"OniMoono.GameTweaks has no tweak {name}");
                    });
            }
        }

        /// Frame stats (`--frame-stats`): OniMoono.GameTweaks' patches that time the game's
        /// update, whose lines join the engine's every 600 frames (FrameStatsWindow).
        public static void InstallFrameStats()
        {
            try
            {
                var probes = TypedAssembly.GetType("OniMoono.GameTweaks.FrameStatsProbes")!;
                probes.GetMethod("Install")!.Invoke(null, [new Harmony("OniMoono.Host.FrameStats")]);
                FrameStatsWindow.Reports.Add(probes.GetMethod("Take")!.CreateDelegate<Func<double, int, string[]>>());
            }
            catch (Exception e)
            {
                var cause = e is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : e;
                EngineLog.Engine($"frame stats: the game's update is not timed: {cause.GetType().Name}: {cause.Message}");
            }
        }

        static void Report(string name, Action install)
        {
            install();
            EngineLog.Engine($"game tweak {name}");
        }

        /// The end of the run: saves still being written are finished first.
        public static void Shutdown()
        {
            if (backgroundSave) BackgroundSave.WaitAll();
        }
    }
}
