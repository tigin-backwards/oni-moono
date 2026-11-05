using System.Diagnostics;
using System.Threading;

namespace OniMoono.Desktop.Gl
{
    /// How far the main thread may run ahead of the screen with vsync. Unity's main thread waits
    /// before a frame (and before it samples the frame's time and input) until the frame
    /// QualitySettings.maxQueuedFrames back has appeared ("waits for frame n − maxQueuedFrames
    /// to appear", Žilys, Fixing Time.deltaTime in Unity 2020.2). Here the main thread waits,
    /// after handing a frame over, until the frame maxQueuedFrames - 1 before it has been
    /// through the presenter: copied, presented and, with vsync, the picture before it on its
    /// way (DXGI's waitable object, OpenGL's swap fence, Vulkan's copy fence). Without the wait
    /// the main thread, the device thread and the presenter each held a frame, and a frame's
    /// start reached the screen a refresh or two later than in the original
    /// (docs/research/input-latency.md).
    ///
    /// The wait serializes a frame's stages, so it is on only while they fit a refresh one after
    /// the other. When it is on follows the pipeline mode of Android's Frame Pacing library
    /// (Swappy, Apache-2.0; platform/frameworks/opt/gamesdk, games-frame-pacing/common/
    /// SwappyCommon.cpp, updateSwapInterval): frame durations are averaged over at least two
    /// seconds; with more than 10% of the frames missing their refresh, pipelining is turned on
    /// (here: the wait off); with the frame's stages summed, plus a millisecond, and half again
    /// (NON_PIPELINE_PERCENT) within the refresh period, it is turned off (the wait on); after
    /// any change the samples start anew, so a mode lasts at least two seconds. It starts
    /// pipelined, as Swappy does. Microsoft's DXGI 1.3 latency guidance draws the same line: one
    /// frame of latency while the CPU's and GPU's times together fit a refresh, two while only
    /// each does. A frame's stages here: the main thread's time from its start to its present,
    /// the device thread's executing it and the presenter's until it issues the picture (the
    /// GPU's time included, as the presenter waits for the frame); a miss, a frame through the
    /// presenter more than a refresh and a half after the one before.
    ///
    /// Presenter thread: Done once a frame has been through it (or was dropped); main thread:
    /// WaitFor. A presenter that stopped or failed ends every wait (Stop).
    internal sealed class QueuedFrames
    {
        readonly object gate = new();
        /// The clock (Stopwatch ticks; tests give their own).
        readonly System.Func<long> clock;
        int done = int.MinValue;
        bool stopped;
        int timeouts;

        /// Swappy's FRAME_DURATION_SAMPLE_SECONDS, FRAME_MARGIN, NON_PIPELINE_PERCENT and
        /// FRAME_DROP_THRESHOLD.
        static readonly long SampleTicks = 2 * Stopwatch.Frequency, Margin = Stopwatch.Frequency / 1000;
        const int NonPipelinePercent = 50, FrameDropThreshold = 10;

        // Under the gate: the samples of the last two seconds since the mode last changed
        // (Swappy's FrameDurations): when, the need, whether missed; their sums.
        readonly System.Collections.Generic.Queue<(long At, long Need, bool Missed)> window = new();
        long lastSample = -1, needSum;
        int missed;
        // The mode: whether the main thread waits; whether DXGI is to shed a queued picture
        // (Drain); the frames after that not judged as missed; the refresh period last decided
        // with (Stopwatch ticks); the changes logged so far.
        bool waiting, drain;
        int excused, changesLogged;
        long lastPeriod;

        public QueuedFrames(System.Func<long> clock = null) => this.clock = clock ?? Stopwatch.GetTimestamp;

        /// Mode changes so far (frame stats).
        public int Changes { get; private set; }

        /// A wait longer than this goes on regardless (a guard: the presenter marks every frame
        /// it takes done, presented or not).
        static readonly long Timeout = Stopwatch.Frequency;

        /// The frame `frame` (Time.frameCount) has been through the presenter, or will not be
        /// (`need` 0: not counted).
        public void Done(int frame, long need = 0)
        {
            lock (gate)
            {
                if (frame > done) done = frame;
                if (need > 0)
                {
                    long now = clock();
                    // (Not the frames a shed picture delays: DXGI's next present waits a refresh.)
                    bool miss = false;
                    if (excused > 0) excused--;
                    else miss = lastPeriod > 0 && lastSample >= 0 && now - lastSample > lastPeriod * 3 / 2;
                    lastSample = now;
                    window.Enqueue((now, need, miss));
                    needSum += need;
                    if (miss) missed++;
                    // Swappy keeps the samples after the first one older than two seconds.
                    while (window.Count >= 2 && now - Second(window) > SampleTicks)
                    {
                        var old = window.Dequeue();
                        needSum -= old.Need;
                        if (old.Missed) missed--;
                    }
                }
                Monitor.PulseAll(gate);
            }
        }

        /// Presenter thread, before presenting a frame: whether the wait has turned on since the
        /// last call, so DXGI is to shed a queued picture (DxgiSwapChain.HoldBack).
        public bool Drain()
        {
            lock (gate)
            {
                bool d = drain;
                drain = false;
                if (d) excused = 2;
                return d;
            }
        }

        static long Second(System.Collections.Generic.Queue<(long At, long Need, bool Missed)> q)
        {
            using var e = q.GetEnumerator();
            e.MoveNext();
            e.MoveNext();
            return e.Current.At;
        }

        /// The presenter stopped: nothing waits any more.
        public void Stop()
        {
            lock (gate)
            {
                stopped = true;
                Monitor.PulseAll(gate);
            }
        }

        /// Under the gate: Swappy's decision, once the samples span two seconds; `period`, the
        /// refresh period times the swap interval (0: the mean time between frames).
        void Decide(long period, int frame)
        {
            int samples = window.Count;
            if (samples < 2 || lastSample - window.Peek().At <= SampleTicks) return;
            // Without the display's period, the mean time between frames stands in, from at least
            // a second's worth of frames at 60 Hz (not the few of a loading screen).
            if (period <= 0)
            {
                if (samples < 60) return;
                period = (lastSample - window.Peek().At) / (samples - 1);
            }
            lastPeriod = period;
            long nonPipelined = needSum / samples + Margin;
            bool was = waiting;
            if (missed * 100 > FrameDropThreshold * samples) waiting = false;
            else if (!waiting && nonPipelined * (100 + NonPipelinePercent) / 100 < period) waiting = true;
            if (waiting == was) return;
            Changes++;
            if (waiting) drain = true;
            if (changesLogged++ < 50)
                OniMoono.Engine.EngineLog.Engine($"present: frame {frame}: the wait for queued frames turned {(waiting ? "on" : "off")} "
                    + $"(a frame's stages {nonPipelined * 1000.0 / Stopwatch.Frequency:F1} ms of {period * 1000.0 / Stopwatch.Frequency:F1}, {missed} of {samples} frames missed their refresh)");
            window.Clear();
            lastSample = -1;
            needSum = 0;
            missed = 0;
        }

        /// Main thread, after frame `frame` was handed over: waits until frame
        /// `frame` + 1 - maxQueued has been presented (maxQueued at least 1), while the mode
        /// says so; `period`, the display's refresh period times the swap interval when known,
        /// else 0. Returns how long it waited, in Stopwatch ticks; `on`, whether the wait was on.
        public long WaitFor(int frame, int maxQueued, long period, out bool on)
        {
            int want = frame + 1 - System.Math.Max(1, maxQueued);
            long start = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (period > 0) lastPeriod = period;
                Decide(period, frame);
                on = waiting && !stopped;
                if (done >= want || !on) return 0;
                long deadline = start + Timeout;
                while (done < want && !stopped)
                {
                    long left = deadline - Stopwatch.GetTimestamp();
                    if (left <= 0)
                    {
                        if (timeouts++ < 3) OniMoono.Engine.EngineLog.Engine($"present: frame {want} not presented after {Timeout * 1000 / Stopwatch.Frequency} ms; going on");
                        break;
                    }
                    Monitor.Wait(gate, (int)System.Math.Max(1, left * 1000 / Stopwatch.Frequency));
                }
            }
            return Stopwatch.GetTimestamp() - start;
        }
    }
}
