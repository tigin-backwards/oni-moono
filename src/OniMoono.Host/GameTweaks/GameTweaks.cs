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
        internal static System.Reflection.Assembly? typed;

        /// The tweaks off for this run, with why (TweakInstall).
        public static IReadOnlyDictionary<string, string> Unavailable => TweakInstall.Unavailable;

        public static bool Available(string name) => TweakInstall.Available(name);

        /// Program, before the game's assemblies load: every tweak's checks against the game's
        /// files (TweakInstall), and the Moono Tweaks menu's (GameTweakNames.Menu), on a worker. compact-nav-links and paged-path-grids, whose
        /// rewrites run as the assemblies load, take its verdict at their first grid (Decide):
        /// off, the session keeps the game's flat arrays.
        public static void Check(string managed) =>
            TweakInstall.Start(managed, System.IO.Path.Combine(AppContext.BaseDirectory, "OniMoono.GameTweaks.dll"), typeof(GameTweaks).Assembly, GameTweakNames.Checked);

        /// The tweaks `enabled` names, and the always-on patches (GameTweakNames.AlwaysOn) whatever it names.
        public static void Install(IReadOnlySet<string> enabled)
        {
            backgroundSave = enabled.Contains(BackgroundSave.Name) && Available(BackgroundSave.Name);
            foreach (var (name, install, log) in Installers(n => (enabled.Contains(n) || GameTweakNames.AlwaysOn.Contains(n)) && Available(n)))
            {
                bool on = TweakInstall.Guarded(name, install, () => typed, log);
                if (name == BackgroundSave.Name) backgroundSave = on;
            }
            EngineLog.Engine($"game tweaks: installed, {System.Diagnostics.Stopwatch.GetElapsedTime(0, TweakInstall.CheckTicks).TotalMilliseconds:F1} ms of it the installs' checks");
            if (enabled.Contains(CompactNavLinks.Name) == false && Environment.GetEnvironmentVariable("ONI_NAVLINKS_DUMP") is { Length: > 0 })
                CompactNavLinks.InstallDump(new Harmony("OniMoono.Host.GameTweaks." + CompactNavLinks.Name));
            InstallFetchStats();
        }

        /// Each tweak that `on` takes, in the order they install, with its install (given the
        /// tweak's own Harmony ID: the Moono Tweaks screen tells by it which tweak a mod overlaps,
        /// ModOverlapsCore.OwnerOf, and a tweak whose install fails takes back what it patched)
        /// and whether its success is logged. Also the check tool's (TweakCheckTool).
        internal static IEnumerable<(string Name, Action<Harmony> Install, bool Log)> Installers(Func<string, bool> on)
        {
            // Both replace SaveLoader.CompressContents: with background-save on, it does the
            // compressing, with .NET's zlib when fast-save-compression is on too.
            bool fastCompression = on(FastSaveCompression.Name);
            if (fastCompression) yield return (FastSaveCompression.Name, h => FastSaveCompression.Install(h, compress: !on(BackgroundSave.Name)), true);
            if (on(BackgroundSave.Name)) yield return (BackgroundSave.Name, h => BackgroundSave.Install(h, fastCompression), true);
            if (on(FmodQuarantine.Name)) yield return (FmodQuarantine.Name, FmodQuarantine.Install, true);
            if (on(FmodStealFix.Name)) yield return (FmodStealFix.Name, FmodStealFix.Install, true);
            if (on(NoSaveGc.Name)) yield return (NoSaveGc.Name, NoSaveGc.Install, true);
            if (on(PostLoadGc.Name)) yield return (PostLoadGc.Name, PostLoadGc.Install, true);
            if (on(FastSerialization.Name)) yield return (FastSerialization.Name, FastSerialization.Install, true);
            if (on(FastParticleCollision.Name)) yield return (FastParticleCollision.Name, FastParticleCollision.Install, true);
            // Enabled at start-up (CompactNavLinks.Enable); this adds the load's summary.
            if (on(CompactNavLinks.Name)) yield return (CompactNavLinks.Name, CompactNavLinks.Install, true);
            // Enabled at start-up too (PagedPathGrids.Enable); this adds the summary in play.
            if (on(PagedPathGrids.Name)) yield return (PagedPathGrids.Name, PagedPathGrids.Install, true);
            // Enabled at start-up (ExactWorldgen.Enable); this says when it could not widen.
            if (on(ExactWorldgen.Name)) yield return (ExactWorldgen.Name, ExactWorldgen.Install, true);
            if (on(LoadingAnimation.Name)) yield return (LoadingAnimation.Name, LoadingAnimation.Install, true);
            // fast-load-screen's engine side; its game side is a typed tweak (below), which logs it.
            if (on(LoadScreenPreviews.Name)) yield return (LoadScreenPreviews.Name, LoadScreenPreviews.Install, false);
            // The tweaks built against the game's own types (src/OniMoono.GameTweaks).
            System.Reflection.MethodInfo? install = null;
            foreach (var name in GameTweakNames.Typed.Concat(GameTweakNames.AlwaysOn))
            {
                if (!on(name)) continue;
                install ??= TypedAssembly.GetType("OniMoono.GameTweaks.Patches")!.GetMethod("Install")!;
                var typedInstall = install;
                yield return (name, h =>
                {
                    if (!(bool)typedInstall.Invoke(null, [h, name])!) throw new InvalidOperationException($"OniMoono.GameTweaks has no tweak {name}");
                }, true);
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

        /// ONI_FETCH_STATS=1 (measurements): times FetchManager.UpdatePickups and its groups
        /// (OniMoono.GameTweaks.FetchByCell), whatever tweaks are on.
        static void InstallFetchStats()
        {
            if (Environment.GetEnvironmentVariable("ONI_FETCH_STATS") != "1") return;
            var type = TypedAssembly.GetType("OniMoono.GameTweaks.FetchByCell")!;
            type.GetMethod("InstallStats")!.Invoke(null, [new Harmony("OniMoono.Host.GameTweaks.fetch-stats")]);
            EngineLog.Engine("fetch stats on");
        }

        /// The end of the run: saves still being written are finished first.
        public static void Shutdown()
        {
            if (backgroundSave) BackgroundSave.WaitAll();
            FmodQuarantine.Shutdown();
            FmodStealFix.Shutdown();
        }
    }
}
