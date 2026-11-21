using System;
using System.Diagnostics;

namespace OniMoono.Desktop.Gl
{
    /// When a presenter may restart DXGI after it failed while running (GlPresenter,
    /// Vulkan.VkPresenter): at most MaxPerBurst tries in a row, the delay between them doubling
    /// from 1 s to 8 s, so a burst spans about a minute and outlasts a slow driver reset (TDR).
    /// A restart that then presents for ResetAfter seconds ends the burst: a later failure gets a
    /// full burst again, however long the session. Given up for the session (GiveUp) when the
    /// presenter finds DXGI cannot work on this device. Times are Stopwatch ticks.
    internal sealed class DxgiRestarts
    {
        public const int MaxPerBurst = 10;
        public const double MaxDelaySeconds = 8, ResetAfterSeconds = 60;

        long next, startedAt = -1;
        bool givenUp;

        /// Tries in this burst so far.
        public int Tries { get; private set; }

        /// No try is left in this burst.
        public bool Exhausted => givenUp || Tries >= MaxPerBurst;

        /// DXGI has presented a picture this session.
        public bool EverPresented { get; private set; }

        /// No more tries this session.
        public void GiveUp() => givenUp = true;

        /// The delay after the `tries`th try: 1, 2, 4, then 8 s.
        public static double DelaySeconds(int tries) => Math.Min(MaxDelaySeconds, Math.Pow(2, Math.Max(0, tries - 1)));

        /// A try may be made now.
        public bool MayTry(long now) => !Exhausted && now >= next;

        /// A try was made now.
        public void Tried(long now)
        {
            Tries++;
            next = now + (long)(DelaySeconds(Tries) * Stopwatch.Frequency);
            startedAt = -1;
        }

        /// The try succeeded (DXGI started again).
        public void Started(long now) => startedAt = now;

        /// DXGI presented a picture now: ResetAfterSeconds after a successful restart, the burst
        /// is over.
        public void Presented(long now)
        {
            EverPresented = true;
            if (Tries == 0 || startedAt < 0 || now - startedAt < (long)(ResetAfterSeconds * Stopwatch.Frequency)) return;
            Tries = 0;
            next = 0;
            startedAt = -1;
        }
    }
}
