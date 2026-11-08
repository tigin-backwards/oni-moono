using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace E2eDriver
{
    /// The runtime's GC pauses per frame, from `gc.watch` on (README.md): ours only, through
    /// GC.GetTotalPauseDuration (.NET 7 and later; the original's Mono has none, and reads 0). A
    /// spec reads them as state: `type:E2eDriver.GcWatch::MostInFrameMs`.
    public static class GcWatch
    {
        static readonly Func<TimeSpan> totalPause = Find();
        static bool on;
        static int lastFrame = -1, gen1Start, gen2Start;
        static double lastTotal;
        static float started;

        /// The most a frame was paused by collections since the watch started, in milliseconds,
        /// and when that frame ended (seconds of real time after the start).
        public static double MostInFrameMs { get; private set; }
        public static double MostInFrameAt { get; private set; }
        /// The frames seen, and the gen1 and gen2 collections since the start.
        public static int Frames { get; private set; }
        public static int Gen1 => on ? GC.CollectionCount(1) - gen1Start : 0;
        public static int Gen2 => on ? GC.CollectionCount(2) - gen2Start : 0;
        /// Whether this runtime reports pauses (false in the original's Mono).
        public static bool Supported => totalPause != null;

        static Func<TimeSpan> Find()
        {
            var m = typeof(GC).GetMethod("GetTotalPauseDuration", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            return m == null ? null : (Func<TimeSpan>)Delegate.CreateDelegate(typeof(Func<TimeSpan>), m);
        }

        public static JToken Start(JObject a)
        {
            on = (bool?)a["on"] ?? true;
            MostInFrameMs = MostInFrameAt = 0;
            Frames = 0;
            lastFrame = -1;
            lastTotal = Total();
            gen1Start = GC.CollectionCount(1);
            gen2Start = GC.CollectionCount(2);
            started = Time.realtimeSinceStartup;
            return new JObject { ["watching"] = on, ["supported"] = Supported };
        }

        static double Total() => totalPause == null ? 0 : totalPause().TotalMilliseconds;

        /// Once a frame, from Driver.Update.
        public static void Frame()
        {
            if (!on || lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            double total = Total();
            double paused = total - lastTotal;
            lastTotal = total;
            Frames++;
            if (paused > MostInFrameMs)
            {
                MostInFrameMs = paused;
                MostInFrameAt = Time.realtimeSinceStartup - started;
            }
        }
    }
}
