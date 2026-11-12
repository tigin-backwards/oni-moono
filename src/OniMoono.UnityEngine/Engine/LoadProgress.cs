using System;
using System.Diagnostics;
using System.Threading;

namespace OniMoono.Engine
{
    /// How far a save load is (game tweak `loading-animation`), for the presenter's bar
    /// (GlLoadAnimation). The main thread only marks where it is: each mark is a timestamp and an
    /// int written, and the Start pass counts the behaviours it has started; the presenter
    /// thread reads them and works out the bar (Value). Nothing here is game state.
    ///
    /// The phases, each weighted by how long it took the last time this save was loaded
    /// (Expected; the study's mid-game colony when it was never loaded). What is counted within
    /// a phase is the engine's or .NET's (a stream's position, the engine's Start queue); the
    /// game's methods only mark where phases begin, so a game update that changes them moves
    /// the bar by time instead, and never changes the load:
    /// 0. the scene switch: from the load's start until the backend scene is loaded (the engine's
    ///    SceneLoader);
    /// 1. the save read, until its body is decompressed (SaveLoader.DecompressContents);
    /// 2. decompression: the compressed bytes through the zlib stream (Decompressing);
    /// 3. the world set up, until SaveManager.Load;
    /// 4. SaveManager.Load: its reader's position in the decompressed body (Objects), or, when a
    ///    mod transpiles it, the time;
    /// 5. what follows it until the Start pass begins (Game.Load, Game.OnSpawn);
    /// 6. the Start pass: every loaded object's Start (KMonoBehaviour's OnSpawn), counted by the
    ///    engine's Start queue.
    /// Within a phase with nothing counted, the bar moves by the time spent against the time
    /// expected (Ease): about 80% of the phase by its expected time, creeping on after that,
    /// never reaching its end before the next mark.
    internal static class LoadProgress
    {
        public const int Phases = 7;
        public const int SceneLoaded = 1, Decompression = 2, WorldSetup = 3, Objects = 4, AfterObjects = 5, Starts = 6;

        /// Seconds per phase when no load of the save was kept: a mid-game colony's (ours, Linux,
        /// with the shader warm-up off the main thread; docs/research/save-load-and-loading-screen.md).
        public static readonly double[] DefaultSeconds = [0.45, 0.1, 0.45, 1.0, 5.8, 0.5, 11.0];

        /// The compressed body's stream while it is decompressed (the fast-save-compression
        /// tweak's): its position is the decompression's progress.
        public static volatile System.IO.Stream Decompressing;

        static readonly long[] marks = new long[Phases];
        static volatile int phase = -1;
        static double[] expected = DefaultSeconds;
        static int loadFrame = -1;

        /// The phase the load is in (-1: none).
        public static int Phase => phase;

        /// Presenter thread: SaveManager.Load's progress (0 to 1) when it can be read; null: none.
        public static Func<double> ObjectsFraction;

        /// The Start pass under way (main thread writes, the presenter reads): behaviours started
        /// and in all.
        static volatile int startsDone, startsTotal;

        /// What the last load's phases took, seconds, once it ended (main thread; the tweak keeps
        /// them for the next load of the same save).
        public static Action<double[]> Measured;

        /// A load starts (main thread, its frame), its phases expected to take `seconds` (null:
        /// the default profile).
        public static void Begin(double[] seconds)
        {
            expected = seconds is { Length: Phases } && Array.TrueForAll(seconds, s => s >= 0) ? seconds : DefaultSeconds;
            Array.Clear(marks);
            startsDone = startsTotal = 0;
            loadFrame = UnityEngine.Time.frameCount;
            Mark(0);
            Hook();
        }

        /// The load reached `index` (main thread): later phases only.
        public static void Mark(int index)
        {
            if (index != 0 && (phase < 0 || index <= phase)) return;
            marks[index] = Stopwatch.GetTimestamp();
            phase = index;
        }

        /// The Start pass begins with `count` behaviours (main thread, BehaviourManager): after
        /// SaveManager.Load, it is the Start phase.
        public static void StartPass(int count)
        {
            int p = phase;
            if (p < AfterObjects || p > Starts) return;
            if (p == AfterObjects)
            {
                if (count < 1) return;
                Mark(Starts);
            }
            else if (count < startsTotal / 10) return; // a small pass that follows it
            startsDone = 0;
            startsTotal = count;
        }

        /// One more behaviour of the Start pass started (main thread).
        public static void Started(int done)
        {
            if (phase == Starts) startsDone = done;
        }

        /// The bar now (presenter thread): 0 to 1, not yet monotonic (the presenter keeps it so).
        public static double Now()
        {
            int p = phase;
            if (p < 0) return 0;
            double seconds = (Stopwatch.GetTimestamp() - Volatile.Read(ref marks[p])) / (double)Stopwatch.Frequency;
            double? measured = null;
            try
            {
                if (p == Decompression && Decompressing is { } stream && stream.CanSeek && stream.Length > 0) measured = (double)stream.Position / stream.Length;
                else if (p == Objects && ObjectsFraction is { } f) measured = f();
                else if (p == Starts && startsTotal > 0) measured = (double)startsDone / startsTotal;
            }
            catch (Exception)
            {
                // A stream closed or replaced meanwhile: by time this once.
                measured = null;
            }
            return Value(p, seconds, measured, expected);
        }

        /// The bar for being `seconds` into phase `p`, `measured` of it done when that is known,
        /// with the phases weighted by `expected` seconds: the phases before it whole, this one
        /// in part (below its end).
        internal static double Value(int p, double seconds, double? measured, double[] expected)
        {
            double total = 0;
            foreach (var e in expected) total += Math.Max(0, e);
            if (total <= 0 || p < 0) return 0;
            double done = 0;
            for (int i = 0; i < p && i < expected.Length; i++) done += Math.Max(0, expected[i]) / total;
            if (p >= expected.Length) return 1;
            double part = measured is { } m && !double.IsNaN(m) ? m : Ease(seconds / Math.Max(0.05, expected[p]));
            part = Math.Clamp(part, 0, 0.995);
            return Math.Clamp(done + Math.Max(0, expected[p]) / total * part, 0, 1);
        }

        /// A phase's part done by the time spent against the time expected (`x`): 80% at 1, on
        /// towards 99% after, never past it.
        internal static double Ease(double x) => x <= 0 ? 0 : 0.99 * (1 - Math.Exp(-1.63 * x));

        static bool hooked;

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            PlayerLoop.FrameStart += Ended;
        }

        /// The first frame after the load's own (main thread): what each phase took. A phase not
        /// reached took nothing; the last runs to this frame's start.
        static void Ended()
        {
            if (phase < 0 || UnityEngine.Time.frameCount <= loadFrame) return;
            long end = Stopwatch.GetTimestamp();
            var seconds = new double[Phases];
            int last = phase;
            for (int i = 0; i <= last; i++)
            {
                long from = marks[i], to = end;
                for (int j = i + 1; j <= last; j++)
                    if (marks[j] != 0) { to = marks[j]; break; }
                seconds[i] = from == 0 ? 0 : Math.Max(0, (to - from) / (double)Stopwatch.Frequency);
            }
            phase = -1;
            ObjectsFraction = null;
            Measured?.Invoke(seconds);
        }
    }
}
