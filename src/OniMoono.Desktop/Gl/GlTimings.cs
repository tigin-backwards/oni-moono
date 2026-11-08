using System.Diagnostics;
using System.Threading;

namespace OniMoono.Desktop.Gl
{
    /// Diagnostics (--frame-stats): GL-thread time by kind of work since the last frame counters,
    /// in Stopwatch ticks.
    internal static class GlTimings
    {
        public static long Busy, Draws, TextureUploads, MeshUploads, Targets, Present;

        public static long Now => Stopwatch.GetTimestamp();

        public static string Take(int frames)
        {
            static string Ms(long ticks, int frames) => (ticks * 1000.0 / Stopwatch.Frequency / frames).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            var text = $"gl thread busy {Ms(Busy, frames)}: draws {Ms(Draws, frames)}, texture uploads {Ms(TextureUploads, frames)}, mesh uploads {Ms(MeshUploads, frames)}, targets {Ms(Targets, frames)}, present {Ms(Present, frames)}";
            Busy = Draws = TextureUploads = MeshUploads = Targets = Present = 0;
            return text;
        }
    }

    /// Diagnostics (--frame-stats): the presenter thread's time per picture it showed, in
    /// Stopwatch ticks, by step: waiting for the frame's commands, getting the target ready
    /// (DXGI: resizing, the interop lock), the copy into it, showing it (SwapBuffers; DXGI: the
    /// unlock, the copy into the back buffer and Present) and, with vsync, waiting until it is on
    /// its way to the display. Also the time between pictures shown: the mean and the mean of
    /// the slowest 1%. The presenter thread adds; the GL thread takes.
    internal static class PresenterTimings
    {
        public static long Pictures, FrameWait, Begin, Copy, Show, Wait, Repeats;
        static readonly object gate = new();
        static readonly System.Collections.Generic.List<double> intervals = new();
        static readonly System.Collections.Generic.List<double> ages = new();

        /// When the game frame the presenter is showing began (presenter thread; 0 for a redraw
        /// or a repeat): Shown counts the time from then until its picture was issued.
        public static long FrameStarted;
        static long lastShown;

        /// Counted only with frame stats on (nothing reads them otherwise).
        static bool On => OniMoono.Engine.PlayerLoop.FrameStats;

        public static void Add(ref long counter, long ticks)
        {
            if (On) Interlocked.Add(ref counter, ticks);
        }

        /// When the presenter last issued a picture, before waiting for it to be on its way
        /// (presenter thread; the splash's pacing).
        static long lastPicture;

        /// How long the presenter waits for a frame before drawing the splash again: 16 ms, or
        /// when its presents are paced to the display (DXGI waits until each picture is on its
        /// way, up to a vertical blank), what is left of 16 ms since the last picture, so the
        /// splash keeps one picture per blank instead of one per two.
        public static int SplashWait(bool pacedByPresent)
        {
            if (!pacedByPresent) return 16;
            long elapsed = (Stopwatch.GetTimestamp() - lastPicture) * 1000 / Stopwatch.Frequency;
            return (int)System.Math.Clamp(16 - elapsed, 1, 16);
        }

        /// A picture was shown (presenter thread); `waited`, the ticks spent after issuing it
        /// waiting for it to be on its way.
        public static void Shown(long waited)
        {
            lastPicture = Stopwatch.GetTimestamp() - waited;
            long started = FrameStarted;
            FrameStarted = 0;
            if (!On) return;
            Interlocked.Increment(ref Pictures);
            long now = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (lastShown != 0 && intervals.Count < 100_000) intervals.Add((now - lastShown) * 1000.0 / Stopwatch.Frequency);
                if (started != 0 && ages.Count < 100_000) ages.Add((lastPicture - started) * 1000.0 / Stopwatch.Frequency);
                lastShown = now;
            }
        }

        /// The last picture was presented again while no frame came (StallRepeats): counted on
        /// its own, outside the pictures and their intervals.
        public static void Repeated()
        {
            if (On) Interlocked.Increment(ref Repeats);
        }

        public static string Take()
        {
            long n = Interlocked.Exchange(ref Pictures, 0);
            long repeats = Interlocked.Exchange(ref Repeats, 0);
            long frameWait = Interlocked.Exchange(ref FrameWait, 0), begin = Interlocked.Exchange(ref Begin, 0),
                copy = Interlocked.Exchange(ref Copy, 0), show = Interlocked.Exchange(ref Show, 0), wait = Interlocked.Exchange(ref Wait, 0);
            double[] sorted, aged;
            lock (gate)
            {
                sorted = intervals.ToArray();
                intervals.Clear();
                aged = ages.ToArray();
                ages.Clear();
            }
            if (n == 0) return "presenter: nothing shown";
            static string F(double v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            string Ms(long ticks) => F(ticks * 1000.0 / Stopwatch.Frequency / n);
            var (mean, slowest) = MeanAndSlowest(sorted);
            var (age, oldest) = MeanAndSlowest(aged);
            return $"presenter per picture ({n}): frame wait {Ms(frameWait)}, begin {Ms(begin)}, copy {Ms(copy)}, show {Ms(show)}, vsync wait {Ms(wait)}; interval mean {F(mean)}, slowest 1% {F(slowest)}"
                   + $"; frame start to present mean {F(age)}, slowest 1% {F(oldest)}" + (repeats > 0 ? $"; {repeats} repeats while no frame came" : "");
        }

        /// The mean of `values`, and the mean of their slowest 1% (at least one value); sorts
        /// them.
        internal static (double Mean, double Slowest) MeanAndSlowest(double[] values)
        {
            if (values.Length == 0) return (0, 0);
            System.Array.Sort(values);
            double sum = 0, worst = 0;
            foreach (var v in values) sum += v;
            int k = System.Math.Max(1, values.Length / 100);
            for (int i = values.Length - k; i < values.Length; i++) worst += values[i];
            return (sum / values.Length, worst / k);
        }
    }
}
