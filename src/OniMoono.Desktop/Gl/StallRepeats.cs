using System;
using System.Diagnostics;

namespace OniMoono.Desktop.Gl
{
    /// When the presenter shows the last picture again while no frame comes (DXGI, Windows): a
    /// variable-refresh display (G-Sync, FreeSync) refreshes only when a picture is presented,
    /// and one left without pictures for a second or more (an autosave, a load hitch) flickers or
    /// dims as its refresh rate falls (#60). So once no frame has been presented for three times
    /// the usual time between frames, and at least 50 ms, the last picture is presented again,
    /// and again every usual frame time (1/60 to 1/30 s), until a frame comes: the display keeps
    /// about the refresh rate it had while the game ran. Three times the usual time, and 50 ms,
    /// lie beyond the spread of frames in play (a 200-Duplicant colony at about 95 fps: 16 of
    /// 26,398 frames took longer, each a visible hitch already), so frames that come as usual
    /// never meet a repeat; a display's own low-framerate compensation covers gaps that short.
    ///
    /// The usual time between frames is a moving average of the intervals between frames
    /// presented (each counted up to four times the average, so a stall moves it little), not
    /// counting those with a repeat in them. Times are Stopwatch ticks; presenter thread only.
    internal sealed class StallRepeats
    {
        static readonly long Hz30 = Stopwatch.Frequency / 30, Hz60 = Stopwatch.Frequency / 60, Floor = Stopwatch.Frequency / 20;

        long lastFrame, typical, next;
        bool repeatedSinceFrame;

        /// Repeats since the last frame presented.
        public int Run { get; private set; }

        /// When the run of repeats began (the first's due time).
        public long RunStarted { get; private set; }

        /// The usual time between frames (0 before two frames).
        public long Typical => typical;

        /// No frame waits longer than this before the first repeat.
        public long Threshold => Math.Max(Floor, 3 * typical);

        /// The time between repeats.
        public long Cadence => Math.Clamp(typical, Hz60, Hz30);

        /// When the next repeat is due; long.MaxValue before the first frame.
        public long Due => lastFrame == 0 ? long.MaxValue : retry != 0 ? retry : next;

        /// A game frame was presented at `now`.
        public void Frame(long now)
        {
            if (lastFrame != 0 && !repeatedSinceFrame)
            {
                long gap = now - lastFrame;
                typical = typical == 0 ? gap : typical - typical / 20 + Math.Min(gap, 4 * typical) / 20;
            }
            lastFrame = now;
            repeatedSinceFrame = false;
            Run = 0;
            retry = 0;
            next = now + Threshold;
        }

        /// The repeat due was presented at `now`: the next is a cadence after it was due, or
        /// after now when it went more than a millisecond late.
        public void Repeated(long now)
        {
            if (Run == 0) RunStarted = next;
            Run++;
            repeatedSinceFrame = true;
            long cadence = Cadence;
            next = now - next > Stopwatch.Frequency / 1000 ? now + cadence : next + cadence;
            retry = 0;
        }

        /// The repeat due was skipped at `now` (the display had not taken the last picture yet):
        /// tried again 2 ms on, until a cadence has passed, when its turn is given up.
        public void Skipped(long now)
        {
            long cadence = Cadence;
            if (now + Retry < next + cadence) retry = now + Retry;
            else
            {
                retry = 0;
                next += cadence;
                if (next <= now) next = now + cadence;
            }
        }

        static readonly long Retry = Stopwatch.Frequency / 500;
        long retry;

        /// Milliseconds until the next repeat is due at `now` (0: due), for a timed wait.
        public int WaitMilliseconds(long now)
        {
            long left = Due - now;
            if (left <= 0) return 0;
            if (left >= Stopwatch.Frequency * 1_000_000L) return int.MaxValue;
            return (int)Math.Min(int.MaxValue, (left * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
        }
    }
}
