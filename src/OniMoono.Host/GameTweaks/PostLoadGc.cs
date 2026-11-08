#nullable enable
using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using OniMoono.Engine;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `post-load-gc`: once a colony has loaded, while the game is still paused, two
    /// blocking non-compacting gen1 collections. Under Server GC with DATAS the young generations'
    /// budgets are gigabytes, so the 1.6 GiB a large colony's load allocates after its own forced
    /// collections is still young when play starts; about a minute in, the first gen1 copies about
    /// 1 GiB of it into gen2, a 283-344 ms pause (docs/research/gc-pauses.md). A collection moves
    /// survivors up one generation, so two move it into gen2 at the end of the load instead: about
    /// 0.4 s on the paused frame, in place of the gen0 that runs there anyway. One gen1 or one
    /// gen2 only moves the pause later. It changes no game state (only when collections run).
    ///
    /// Each load (from the main menu, from the pause menu, a new colony) spawns a new Game, whose
    /// OnSpawn starts the wait (PostLoadGcGate) as a coroutine on it. Under Workstation GC (the
    /// low-memory option) the young generation is small, the load's survivors are promoted
    /// during the load, and there is no pause to remove: it does nothing there.
    [GameMember(GameUse.Postfix, "System.Void Game::OnSpawn()")]
    [GameMember(GameUse.Uses, "SpeedControlScreen SpeedControlScreen::get_Instance()")]
    [GameMember(GameUse.Uses, "System.Boolean SpeedControlScreen::get_IsPaused()")]
    internal static class PostLoadGc
    {
        public const string Name = GameTweakNames.PostLoadGc;

        /// For specs (`type:OniMoono.Host.GameTweaks.PostLoadGc::Collected`): loads collected
        /// after, loads skipped (unpaused first), and the last collections' milliseconds.
        public static int Collected, Skipped;
        public static double LastMs;

        static PropertyInfo? speedInstance, isPaused;

        public static void Install(Harmony harmony)
        {
            if (!System.Runtime.GCSettings.IsServerGC)
            {
                EngineLog.Engine($"{Name}: off under Workstation GC (the low-memory option), whose young generation stays small");
                return;
            }
            var speed = AccessTools.TypeByName("SpeedControlScreen") ?? throw new TypeLoadException("SpeedControlScreen is not in the game");
            speedInstance = AccessTools.Property(speed, "Instance") ?? throw new MissingMemberException("SpeedControlScreen", "Instance");
            isPaused = AccessTools.Property(speed, "IsPaused") ?? throw new MissingMemberException("SpeedControlScreen", "IsPaused");
            harmony.Patch(AccessTools.Method("Game:OnSpawn") ?? throw new MissingMethodException("Game", "OnSpawn"),
                postfix: new HarmonyMethod(typeof(PostLoadGc), nameof(Spawned)));
        }

        static void Spawned(MonoBehaviour __instance) => __instance.StartCoroutine(AfterLoad());

        static bool? Paused()
        {
            var screen = speedInstance!.GetValue(null);
            return screen == null ? null : (bool)isPaused!.GetValue(screen)!;
        }

        static IEnumerator AfterLoad()
        {
            var gate = new PostLoadGcGate();
            long last = Stopwatch.GetTimestamp();
            while (true)
            {
                yield return null;
                long now = Stopwatch.GetTimestamp();
                double seconds = (now - last) / (double)Stopwatch.Frequency;
                last = now;
                switch (gate.Frame(seconds, Paused()))
                {
                    case PostLoadGcGate.Verdict.Wait: continue;
                    case PostLoadGcGate.Verdict.Skip:
                        Skipped++;
                        EngineLog.Engine($"{Name}: skipped, the game was not paused on the colony's frame {gate.Frames} (unpaused before the load settled)");
                        yield break;
                }
                var first = Stopwatch.StartNew();
                GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: false);
                double a = first.Elapsed.TotalMilliseconds;
                var second = Stopwatch.StartNew();
                GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: false);
                double b = second.Elapsed.TotalMilliseconds;
                LastMs = a + b;
                Collected++;
                EngineLog.Engine($"{Name}: 2 young collections, {a + b:F0} ms ({a:F0} + {b:F0}), on the colony's frame {gate.Frames}, still paused");
                yield break;
            }
        }
    }
}
