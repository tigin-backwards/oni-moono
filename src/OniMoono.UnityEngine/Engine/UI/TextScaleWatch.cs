using System;
using System.Collections.Generic;

using Transform = UnityEngine.Transform;

namespace OniMoono.Engine.UI
{
    /// TextMeshPro's scale check (TextMeshProUGUI.RescaleIfScaleChanged, a
    /// Canvas.willRenderCanvases subscriber per enabled text) called only for the texts whose
    /// scale may have changed: 2,000 calls a frame on a late-game colony, 0.5 to 1 ms, where a
    /// few texts ever rescale.
    ///
    /// Each text's transform gets a slot. Every invalidation of the transform's cached world
    /// data (Transform.WorldChanged: its own position, rotation, scale or parent set, or an
    /// ancestor's) arms the slot; the text disarms it when its check finds the scale settled, so
    /// that the same check with the same scale would do nothing again. The text's subscription
    /// carries the slot (Canvas.AddWillRenderCanvases), and the call skips it while it is
    /// disarmed, keeping its place among the other subscribers.
    ///
    /// Exact: the check reads the transform's cached lossy scale, which changes only through an
    /// invalidation, so a disarmed text would read the scale it settled with. Texts arm their slot
    /// on enable and on every new SDF scale. Test runs (ONI_VERIFY_TEXT_SCALES=1) call the skipped
    /// ones too and log each that would have acted. Main thread only.
    internal static class TextScaleWatch
    {
        /// Armed[slot] != 0: check the text. Slot 0 is no slot (an ungated subscriber); its byte
        /// is written by Disarm(0) and never read.
        internal static byte[] Armed = new byte[1024];
        static Transform[] owners = new Transform[1024];
        static int next = 1;
        static readonly Stack<int> free = new();

        public static int Watched => next - 1 - free.Count;

        /// A slot for `t`, armed; 0 when `t` already has one (a second watcher is called every
        /// frame, as before).
        public static int Watch(Transform t)
        {
            if (t is null || t.ScaleWatchSlot != 0) return 0;
            int slot;
            if (free.Count > 0) slot = free.Pop();
            else
            {
                slot = next++;
                if (slot == Armed.Length)
                {
                    Array.Resize(ref owners, slot * 2);
                    var grown = new byte[slot * 2];
                    Array.Copy(Armed, grown, slot);
                    Armed = grown;
                }
            }
            owners[slot] = t;
            t.ScaleWatchSlot = slot;
            Armed[slot] = 1;
            return slot;
        }

        /// Frees the slot (a text destroyed). Left armed: a call already under way may still
        /// hold it, and a new owner starts armed anyway.
        public static void Unwatch(int slot)
        {
            if (slot <= 0) return;
            var t = owners[slot];
            if (t is null) return;
            if (t.ScaleWatchSlot == slot) t.ScaleWatchSlot = 0;
            owners[slot] = null;
            Armed[slot] = 1;
            free.Push(slot);
        }

        /// The transform's native object was replaced (Lifecycle.ConvertTransform): the slot
        /// follows it, armed.
        public static void Moved(Transform from, Transform to)
        {
            int slot = from.ScaleWatchSlot;
            if (slot == 0) return;
            from.ScaleWatchSlot = 0;
            if (to.ScaleWatchSlot != 0)
            {
                // Cannot happen (a fresh native); a second watcher would be ungated instead.
                Armed[slot] = 1;
                return;
            }
            to.ScaleWatchSlot = slot;
            owners[slot] = to;
            Armed[slot] = 1;
        }

        public static void Arm(int slot) => Armed[slot] = 1;

        public static void Disarm(int slot) => Armed[slot] = 0;

        // ----- ONI_VERIFY_TEXT_SCALES -----

        public static readonly bool Verifying = Environment.GetEnvironmentVariable("ONI_VERIFY_TEXT_SCALES") == "1";

        /// Inside a call the fast path skips, made only in verify mode.
        public static bool InSkippedCall;
        static long skippedCalls, wrong, calls;

        /// A skipped subscriber called anyway (verify mode).
        public static void CallSkipped(UnityEngine.Canvas.WillRenderCanvases d)
        {
            InSkippedCall = true;
            try { d(); }
            finally { InSkippedCall = false; }
            if (++skippedCalls % 200000 == 0) LogTotals();
        }

        public static void Called() => calls++;

        /// The text the fast path skipped acted: its scale changed with no invalidation.
        public static void Missed(string name, float generated, float scale)
        {
            EngineLog.Engine($"text scales wrong ({++wrong}): {name}, skipped, rescales from {generated:R} to {scale:R}");
        }

        public static void LogTotals()
        {
            if (!Verifying) return;
            EngineLog.Engine($"text scales: {skippedCalls} skipped checks run, {calls} checks called, {Watched} texts watched, {wrong} wrong");
        }
    }
}
