using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace E2eDriver
{
    /// stopwatch {methods?, over_ms?} (README.md): the wall time inside game methods, through a
    /// Harmony prefix and postfix on each (inclusive: a method calling itself counts twice), and
    /// the frames longer than `over_ms` (default 100), both since the previous stopwatch. Each
    /// call logs and returns those totals, then restarts them. Measurement only: the methods run
    /// as they would.
    public static class MethodTimes
    {
        sealed class Slot { public string Name; public int Calls; public long Ticks; }

        static readonly Dictionary<System.Reflection.MethodBase, Slot> slots = new Dictionary<System.Reflection.MethodBase, Slot>();
        static readonly Dictionary<string, Slot> names = new Dictionary<string, Slot>();
        static readonly List<string> longFrames = new List<string>();
        static double overMs = 100;
        static long lastFrame;
        static bool armed;

        /// `hold: N`: then answers N frames later (the driver doing nothing else meanwhile), logging
        /// those frames' mean and longest time.
        internal static JToken Start(Request request)
        {
            var result = Report(request.Args);
            int hold = (int?)request.Args["hold"] ?? 0;
            if (hold <= 0) return result;
            long start = System.Diagnostics.Stopwatch.GetTimestamp(), previous = start;
            int frames = 0;
            double longest = 0;
            request.Pending = r =>
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                longest = System.Math.Max(longest, Ms(now - previous));
                previous = now;
                if (++frames < hold) return false;
                double mean = Ms(now - start) / frames;
                Debug.Log($"E2E stopwatch hold {frames} frames: mean {mean:F1} ms, longest {longest:F1} ms");
                r.State = new JObject { ["frames"] = frames, ["mean_ms"] = System.Math.Round(mean, 1), ["longest_ms"] = System.Math.Round(longest, 1), ["before"] = result };
                return true;
            };
            return null;
        }

        static JObject Report(JObject a)
        {
            var result = new JObject();
            foreach (var slot in names.Values)
            {
                result[slot.Name] = new JObject { ["calls"] = slot.Calls, ["ms"] = System.Math.Round(Ms(slot.Ticks), 1) };
                Debug.Log($"E2E stopwatch {slot.Name}: {slot.Calls} calls, {Ms(slot.Ticks):F1} ms");
                slot.Calls = 0;
                slot.Ticks = 0;
            }
            result["long_frames"] = new JArray(longFrames.ToArray());
            result["driver_ms"] = System.Math.Round(Ms(driverTicks), 1);
            if (armed) Debug.Log($"E2E stopwatch frames over {overMs} ms: {string.Join(", ", longFrames)}; the driver's own work {Ms(driverTicks):F0} ms");
            driverTicks = 0;
            longFrames.Clear();
            if (a["over_ms"] != null) overMs = (double)a["over_ms"];
            foreach (var token in a["methods"] as JArray ?? new JArray())
            {
                var name = (string)token;
                if (names.ContainsKey(name)) continue;
                var method = Counting.Resolve(name) ?? throw new DriverError("no method " + name);
                var slot = new Slot { Name = name };
                slots[method] = slot;
                names[name] = slot;
                Mod.Harmony.Patch(method, prefix: new HarmonyLib.HarmonyMethod(typeof(MethodTimes), nameof(Prefix)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(MethodTimes), nameof(Postfix)));
            }
            armed = true;
            lastFrame = System.Diagnostics.Stopwatch.GetTimestamp();
            return result;
        }

        static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// Called at the start of the driver's Update, once a frame.
        public static void Frame()
        {
            if (!armed) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = Ms(now - lastFrame);
            lastFrame = now;
            if (ms > overMs) longFrames.Add(frameDriverTicks > 0 ? $"f{Time.frameCount} {ms:F0} (driver {Ms(frameDriverTicks):F0})" : $"f{Time.frameCount} {ms:F0}");
            frameDriverTicks = 0;
        }

        static long driverTicks, frameDriverTicks;

        /// The driver's own work in a frame (its queries cost the frame too), from `started`.
        public static void Driver(long started)
        {
            if (!armed) return;
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            driverTicks += ticks;
            frameDriverTicks += ticks;
        }

        static void Prefix(out long __state) => __state = System.Diagnostics.Stopwatch.GetTimestamp();

        static void Postfix(System.Reflection.MethodBase __originalMethod, long __state)
        {
            if (!slots.TryGetValue(__originalMethod, out var slot)) return;
            slot.Calls++;
            slot.Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - __state;
        }
    }
}
