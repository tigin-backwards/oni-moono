using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace OniMoono.GameTweaks
{
    /// Frame stats (`--frame-stats`): the game's own update, timed on the main thread by
    /// FrameStatsProbes' patches and logged with the engine's other 600-frame lines: game speed
    /// and sim steps, the sim step's time in SimDLL against the managed rest, and Game.Update's
    /// main parts. Plain .NET (no game types): the unit tests compile it in.
    public static class GameFrameStats
    {
        /// What is timed: the sim step's methods, other parts of the game's update, and the state
        /// machine buckets by update rate (StateMachineUpdater.BucketGroup.AdvanceOneSubTick;
        /// UpdateRate's order, from FirstBucket).
        public const int SimEveryTick = 0, UnsafeSim200ms = 1, StepTheSim = 2, CellInfo = 3, NavGrids = 4, PathProbes = 5,
            Brains = 6, AnimEvents = 7, Jobs = 8, FirstBucket = 9, Buckets = 8, Spans = FirstBucket + Buckets;

        static readonly string[] bucketNames = { "render every tick", "render 200ms", "render 1000ms", "sim every tick", "sim 33ms", "sim 200ms", "sim 1000ms", "sim 4000ms" };

        /// Per span, in Stopwatch ticks: the time inside, the calls, and SimDLL's time inside.
        public static readonly long[] Ticks = new long[Spans], Calls = new long[Spans], Native = new long[Spans];
        static readonly long[] started = new long[Spans], nativeAtStart = new long[Spans];

        /// The main thread's calls into SimDLL: their time (Stopwatch ticks) and count.
        public static long NativeTicks, NativeCalls;
        static int nativeDepth;
        static long nativeStarted;

        /// The main thread's wait for the job workers in JobManager.Run (Stopwatch ticks).
        public static long JobWait;

        /// Game.SimEveryTick's frames: all, paused (no game time), over the game's limit of 200 ms
        /// of game time a frame, the game time they asked and got (seconds), and the 200 ms
        /// steps of the sim taken (UnsafeSim200ms with time to step).
        public static long Frames, PausedFrames, CappedFrames, Steps;
        public static double AskedSeconds, GivenSeconds;

        /// The main thread, which only it counts (others call into SimDLL and run jobs too).
        [ThreadStatic] static bool onMain;

        /// Whether the probes are in (frame stats on); FastJobs reads it before the thread.
        public static bool Enabled;

        public static bool OnMain => Enabled && onMain;

        public static void Begin(int span)
        {
            if (!onMain) return;
            started[span] = Stopwatch.GetTimestamp();
            nativeAtStart[span] = NativeTicks;
        }

        /// The end of a span begun on this thread: one begun without an end (its method threw)
        /// is overwritten by the next Begin.
        public static void End(int span)
        {
            if (!onMain || started[span] == 0) return;
            Ticks[span] += Stopwatch.GetTimestamp() - started[span];
            Native[span] += NativeTicks - nativeAtStart[span];
            Calls[span]++;
            started[span] = 0;
        }

        /// A call into SimDLL; calls made from inside one (SimDLL's callbacks into the game) count
        /// as part of it.
        public static void NativeBegin()
        {
            if (!onMain || nativeDepth++ > 0) return;
            nativeStarted = Stopwatch.GetTimestamp();
        }

        public static void NativeEnd()
        {
            if (!onMain || nativeDepth == 0 || --nativeDepth > 0) return;
            NativeTicks += Stopwatch.GetTimestamp() - nativeStarted;
            NativeCalls++;
        }

        /// The game's sim step for the frame, `dt` its Time.deltaTime (Game.SimEveryTick). No
        /// SimDLL call runs between frames, so a call left open by an exception ends here.
        public static void SimFrame(float dt)
        {
            onMain = true;
            nativeDepth = 0;
            Frames++;
            if (dt <= 0) PausedFrames++;
            if (dt > 0.2f) CappedFrames++;
            AskedSeconds += Math.Max(0, dt);
            GivenSeconds += Math.Max(0, Math.Min(dt, 0.2f));
        }

        /// UnsafeSim200ms(dt): a step of the sim when dt is above 0 (else a call for its state).
        public static void SimStep(float dt)
        {
            if (onMain && dt > 0) Steps++;
        }

        public static void AddJobWait(long since)
        {
            if (onMain) JobWait += Stopwatch.GetTimestamp() - since;
        }

        /// The window's lines (none when the game's update did not run, as in the menus), and a
        /// fresh window. `seconds`: the window's wall time; `speed`: the game's speed setting
        /// (1 to 3; 0 when unknown).
        public static string[] Take(double seconds, int frames, int speed)
        {
            try
            {
                if (Frames == 0 || frames <= 0) return Array.Empty<string>();
                return Format(seconds, frames, speed);
            }
            finally
            {
                Reset();
            }
        }

        static string[] Format(double seconds, int frames, int speed)
        {
            var c = CultureInfo.InvariantCulture;
            double ms = 1000.0 / Stopwatch.Frequency;
            string Ms(long ticks) => (ticks * ms / frames).ToString("F2", c);
            string PerFrame(double n) => (n / frames).ToString("F2", c);
            string Percent(long n) => (100.0 * n / Frames).ToString("F0", c);

            var speedLine = new StringBuilder();
            speedLine.Append(string.Format(c, "game speed (over {0} frames, {1:F1} s): ", frames, seconds));
            if (speed > 0) speedLine.Append(string.Format(c, "speed {0}, ", speed));
            speedLine.Append(string.Format(c, "{0:F2} game s a real s (asked {1:F2}); 200 ms sim steps {2} a frame, sim calls {3} a frame; ",
                seconds > 0 ? GivenSeconds / seconds : 0, seconds > 0 ? AskedSeconds / seconds : 0, PerFrame(Steps), PerFrame(Calls[UnsafeSim200ms])));
            speedLine.Append(string.Format(c, "frames at the game's 200 ms a frame limit {0}%; paused {1}%", Percent(CappedFrames), Percent(PausedFrames)));

            string Sim(int span, bool managed)
            {
                var text = $"{Ms(Ticks[span])} (SimDLL {Ms(Native[span])}";
                if (managed) text += $", managed {Ms(Ticks[span] - Native[span])}";
                return text + ")";
            }
            string simLine = $"sim (ms/frame over {frames}): SimEveryTick {Sim(SimEveryTick, true)}, UnsafeSim200ms {Sim(UnsafeSim200ms, true)}, StepTheSim {Sim(StepTheSim, true)}; "
                + $"SimDLL on the main thread {Ms(NativeTicks)} in {PerFrame(NativeCalls)} calls a frame";

            var update = new StringBuilder();
            update.Append($"game update (ms/frame, calls a frame over {frames}): state machines");
            for (int i = 0; i < Buckets; i++)
                update.Append(i == 0 ? " " : ", ").Append(bucketNames[i]).Append(' ').Append(Part(FirstBucket + i));
            update.Append($"; brains {Part(Brains)}, cell info {Part(CellInfo)}, nav grids {Part(NavGrids)}, path probes {Part(PathProbes)}, anim events {Part(AnimEvents)}");
            update.Append($", jobs {Part(Jobs)} waiting for workers {Ms(JobWait)}");
            return new[] { speedLine.ToString(), simLine, update.ToString() };

            string Part(int span) => $"{Ms(Ticks[span])} ({PerFrame(Calls[span])})";
        }

        public static void Reset()
        {
            Array.Clear(Ticks, 0, Spans);
            Array.Clear(Calls, 0, Spans);
            Array.Clear(Native, 0, Spans);
            NativeTicks = NativeCalls = JobWait = 0;
            Frames = PausedFrames = CappedFrames = Steps = 0;
            AskedSeconds = GivenSeconds = 0;
        }
    }
}
