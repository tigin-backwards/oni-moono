using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PerfProbe
{
    /// TEST INFRASTRUCTURE: `ab.txt` beside the probe compares a change with itself switched
    /// off, in one run: its first line names a static bool field as `TYPE, ASSEMBLY FIELD` (our
    /// player's switch for the change; absent from the original player, which then logs that
    /// and measures nothing), the next lines optionally the seconds to settle (25), each
    /// segment's seconds (18) and the number of segments (8). After settling in the colony the
    /// segments take the field's two values in the order A B B A A B B A… (A: the value it does
    /// not start with), which cancels a steady drift such as the frame rate still rising after
    /// a load, and each is logged as `PERF ab FIELD=VALUE: N frames, FPS fps, MS ms a
    /// frame, worst1% MS ms, allocated MiB, gc-pause MS ms`. The engine's frame stats lines
    /// between `PERF ab start` lines belong to that segment.
    static class Toggle
    {
        public static IEnumerator Run(string path)
        {
            var lines = System.IO.File.ReadAllLines(path);
            var spec = lines[0].Trim();
            int split = spec.LastIndexOf(' ');
            var type = System.Type.GetType(spec.Substring(0, split).Trim());
            var field = type?.GetField(spec.Substring(split + 1), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(bool))
            {
                Debug.Log($"PERF ab: no static bool field {spec}");
                yield break;
            }
            float Seconds(int i, float fallback) => lines.Length > i && float.TryParse(lines[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
            float settle = Seconds(1, 25f), segment = Seconds(2, 18f);
            int count = (int)Seconds(3, 8f);
            while (Game.Instance == null) yield return null;
            float until = Time.realtimeSinceStartup + settle;
            while (Time.realtimeSinceStartup < until) yield return null;
            var times = new List<float>();
            bool a = !(bool)field.GetValue(null);
            for (int s = 0; s < count; s++)
            {
                bool value = a ^ (((s + 1) >> 1 & 1) == 1);
                field.SetValue(null, value);
                string label = $"{field.Name}={value}";
                Debug.Log($"PERF ab start {label}");
                times.Clear();
                long allocated = RuntimeCounters.Allocated(), paused = RuntimeCounters.GcPauseMs();
                float start = Time.realtimeSinceStartup, last = start;
                while (Time.realtimeSinceStartup - start < segment)
                {
                    yield return null;
                    float now = Time.realtimeSinceStartup;
                    times.Add(now - last);
                    last = now;
                }
                float total = last - start;
                times.Sort();
                int tail = System.Math.Max(1, times.Count / 100);
                float worst = 0f;
                for (int i = times.Count - tail; i < times.Count; i++) worst += times[i];
                Debug.Log($"PERF ab {label}: {times.Count} frames, {times.Count / total:F2} fps, {total * 1000f / times.Count:F2} ms a frame, worst1% {worst * 1000f / tail:F1} ms, allocated {(RuntimeCounters.Allocated() - allocated) / 1048576.0:F0} MiB, gc-pause {RuntimeCounters.GcPauseMs() - paused} ms");
            }
            Debug.Log("PERF ab done");
        }
    }
}
