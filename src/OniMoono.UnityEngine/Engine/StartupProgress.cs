using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace OniMoono.Engine
{
    /// How far start-up is to the main menu, for the loading splash's bar (any thread reads
    /// Value). Start-up passes checkpoints: the engine's own (window, first scene, first frame,
    /// each content bundle, the asset unload, the menu's scene and first frame) and the game's,
    /// recognized from its log lines (Steam ready, Global's start, the mod system, localization).
    /// Each launch's checkpoint times are kept in the player's cache folder; the next launch
    /// expects the same (the first launch uses times measured on a typical machine). Between two
    /// checkpoints the bar heads for the next one on an easing curve, quick at first and slower
    /// as it nears it: about 80% of the way when the phase takes as long as last time, and on
    /// ever more slowly while it overruns, never quite reaching the next checkpoint. Where the
    /// engine knows how far it is (the first scene's objects loaded) that counts as the phase's
    /// time. Should the next checkpoint not come (this launch skips it), the bar still moves on
    /// with start-up's time, a little behind the expected pace. The bar shown chases where it heads with a quarter-second time constant, so it
    /// moves on smoothly, without a jump, when a checkpoint comes early or when start-up reaches
    /// the menu. It only ever moves forward, and is never still before the menu.
    internal static class StartupProgress
    {
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static readonly object gate = new();
        static readonly List<(string Name, double Ms)> reached = new();
        static List<(string Name, double Ms)> expected;
        static double shown;
        static double fraction = -1;
        public static bool Done { get; private set; }

        /// The game's log lines that mark its start-up (prefixes), as checkpoint names.
        static readonly (string Prefix, string Name)[] gameLines =
        [
            ("DistributionPlatform initialized.", "platform"),
            ("Initializing at ", "global"),
            ("Using builtin mod system.", "mods"),
            ("Localization.Initialize!", "localization"),
        ];

        /// A start-up on a typical machine (Linux, every DLC; times in ms), for the first launch.
        static readonly (string, double)[] typical =
        [
            ("window", 1700), ("scene0", 4300), ("frame0", 5500), ("platform", 5600),
            ("bundle1", 13000), ("bundle2", 13100), ("bundle3", 13200), ("bundle4", 13300), ("bundle5", 13400), ("bundle6", 13500),
            ("global", 13600), ("mods", 14300), ("localization", 14400), ("unload", 21000), ("scene1", 21500), ("menu", 23000),
        ];

        static string FilePath => Path.Combine(EngineConfig.TemporaryCachePath, "oni-moono", "startup-checkpoints.json");
        /// Where players from before the Moono name kept them: read until a start-up writes FilePath.
        static string OldFilePath => Path.Combine(EngineConfig.TemporaryCachePath, "OniBrutal", "startup-checkpoints.json");

        static int bundles;

        public static void Checkpoint(string name)
        {
            if (Done) return;
            lock (gate)
            {
                if (name == "bundle") name = "bundle" + ++bundles;
                reached.Add((name, clock.Elapsed.TotalMilliseconds));
                fraction = -1;
                if (name != "menu") return;
                Done = true;
            }
            StartupStats.Mark("start-up progress: at the menu");
            StartupStats.Mark("bundle blocks at the menu: " + OniMoono.Assets.Files.BundleBlockStats.Summary());
            OniMoono.Assets.Files.BundleBlockStats.Report();
            PlayerLoop.LogVideoMemory("at the menu");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var list = new List<object>();
                foreach (var (n, ms) in reached) list.Add(new { name = n, ms });
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        /// The end of a frame: the first frame, and the menu's (the frame that loaded the second
        /// scene).
        public static void FrameEnded(int frame)
        {
            if (Done) return;
            if (frame == 0) Checkpoint("frame0");
            bool menu;
            lock (gate) menu = reached.Exists(r => r.Name == "scene1");
            if (menu) Checkpoint("menu");
        }

        /// A main-thread log line: a checkpoint when it is one of the game's (as Klei's Debug
        /// writes it, after its time, thread and level).
        public static void LogLine(string message)
        {
            if (Done || message == null) return;
            int start = TextStart(message);
            foreach (var (prefix, name) in gameLines)
                if (string.CompareOrdinal(message, start, prefix, 0, prefix.Length) == 0)
                {
                    Checkpoint(name);
                    return;
                }
        }

        /// Where a log line's text starts, after Klei's header ("[12:34:56.789] [1] [INFO] ").
        internal static int TextStart(string line)
        {
            int at = 0;
            while (at < line.Length && line[at] == '[')
            {
                int end = line.IndexOf("] ", at, StringComparison.Ordinal);
                if (end < 0) break;
                at = end + 2;
            }
            return at;
        }

        /// The part of the current phase done, when the engine knows it (0..1); -1 otherwise.
        public static void PhaseFraction(double value) => fraction = value;

        static List<(string Name, double Ms)> Expected()
        {
            if (expected != null) return expected;
            var list = new List<(string Name, double Ms)>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(File.Exists(FilePath) ? FilePath : OldFilePath));
                foreach (var e in doc.RootElement.EnumerateArray()) list.Add((e.GetProperty("name"u8).GetString()!, e.GetProperty("ms"u8).GetDouble()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                list.Clear();
            }
            if (list.Count == 0 || list[^1].Name != "menu") list = [.. typical];
            return expected = list;
        }

        /// How far into a phase the bar heads (of the span to the next checkpoint) at `u`, the
        /// phase's elapsed time over its expected time (or the engine's fraction): about 80% at
        /// u = 1, and on towards PhaseCap ever more slowly after, so an overrunning phase keeps
        /// creeping and never quite reaches the next checkpoint.
        internal static double PhasePart(double u) => PhaseCap * (1 - Math.Exp(-PhaseRate * Math.Max(0, u)));

        /// PhasePart's limit, and its rate (0.98·(1 − e^−1.7) ≈ 0.80).
        const double PhaseCap = 0.98, PhaseRate = 1.7;

        /// Where the bar heads at `nowMs`, 0..0.99: the last checkpoint `reached` (name, ms)
        /// that the `expected` launch also passed, plus PhasePart of the span to its next one.
        /// The phase's u is the larger of its time over the expected time and the engine's
        /// `fraction` (-1: unknown). It is never below Floor, for a launch that skips the next
        /// checkpoint (one that loads fewer content bundles than the last, or the first launch
        /// on a machine unlike the typical one).
        internal static double Target(IReadOnlyList<(string Name, double Ms)> expected, IReadOnlyList<(string Name, double Ms)> reached, double nowMs, double fraction)
        {
            double total = expected[^1].Ms;
            int at = -1;
            double reachedAt = 0;
            for (int r = reached.Count - 1; r >= 0 && at < 0; r--)
                for (int i = 0; i < expected.Count; i++)
                    if (expected[i].Name == reached[r].Name) { at = i; reachedAt = reached[r].Ms; break; }
            double from = at >= 0 ? expected[at].Ms : 0;
            double to = at + 1 < expected.Count ? expected[at + 1].Ms : total;
            double span = Math.Max(1, to - from);
            double u = Math.Max(fraction, (nowMs - reachedAt) / span);
            return Math.Clamp(Math.Max((from + PhasePart(u) * span) / total, Floor(nowMs / total)), 0, 0.99);
        }

        /// The least the bar heads for at `t`, start-up's time over the expected total: always
        /// behind a bar moving at the expected pace (1 − e^−t < t), so it matters only when the
        /// bar would otherwise be held at a checkpoint that does not come.
        internal static double Floor(double t) => 0.99 * (1 - Math.Exp(-Math.Max(0, t)));

        /// The bar's value `seconds` on from `shown`, chasing `target` exponentially with time
        /// constant `tau` (so the same whatever the frame rate); never backwards.
        internal static double Chase(double shown, double target, double seconds, double tau) =>
            target <= shown ? shown : shown + (target - shown) * (1 - Math.Exp(-Math.Max(0, seconds) / tau));

        /// Chase's time constants: on the way, and to full once at the menu (quick, but seen
        /// before the rain fades).
        const double ChaseSeconds = 0.25, DoneChaseSeconds = 0.08;

        static double lastRead = -1;

        /// Start-up done so far, 0..1, as shown (any thread): it chases Target, so a checkpoint
        /// reached early moves the bar on smoothly rather than at once.
        public static float Value
        {
            get
            {
                lock (gate)
                {
                    double now = clock.Elapsed.TotalSeconds;
                    double seconds = lastRead < 0 ? 0 : now - lastRead;
                    lastRead = now;
                    double target = Done ? 1 : Target(Expected(), reached, now * 1000, fraction);
                    shown = Chase(shown, target, seconds, Done ? DoneChaseSeconds : ChaseSeconds);
                    if (Done && 1 - shown < 0.001) shown = 1;
                    return (float)shown;
                }
            }
        }
    }
}
