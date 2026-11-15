using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OniMoono.Engine
{
    /// Where a quit's time goes, for the log's "Quit" lines: the steps the host and the device
    /// name (Mark), the scripts' OnDisable and OnDestroy with the single calls that took 5 ms
    /// or more, the engine's side of destroying each object, the garbage collections meanwhile
    /// and the process's memory before. Counted only while the player quits (On).
    /// ONI_QUIT_STATS=1 adds the script types whose callbacks took longest in all.
    internal static class QuitStats
    {
        public static bool On;
        public static long DisableTicks, DestroyTicks, EngineTicks, ReleaseTicks;
        public static int Disables, Destroys, GameObjects, Components;

        static readonly bool byType = Environment.GetEnvironmentVariable("ONI_QUIT_STATS") == "1";
        static readonly Dictionary<Type, (long Ticks, int Calls)> types = new();
        static readonly List<(string Name, double Ms)> steps = new();
        static readonly List<(string Name, long Ticks)> slow = new();
        static int slowDropped;
        static readonly long slowTicks = Stopwatch.Frequency / 200;
        static long last;
        static int gen0, gen2;
        static TimeSpan paused;

        public static long Now => Stopwatch.GetTimestamp();

        public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        static string memory = "";

        public static void Start()
        {
            On = true;
            // Before the quit touches every object: a working set far under the private bytes
            // is a process the system paged out, whose quit waits for the disk.
            try
            {
                using var process = Process.GetCurrentProcess();
                var gc = GC.GetGCMemoryInfo();
                memory = $"Quit memory: working set {process.WorkingSet64 / 1e6:0} MB, private {process.PrivateMemorySize64 / 1e6:0} MB, managed heap {gc.HeapSizeBytes / 1e6:0} MB; "
                    + $"system memory load {gc.MemoryLoadBytes / 1e6:0} of {gc.TotalAvailableMemoryBytes / 1e6:0} MB at the last collection";
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                memory = "";
            }
            gen0 = GC.CollectionCount(0);
            gen2 = GC.CollectionCount(2);
            paused = GC.GetTotalPauseDuration();
            last = Now;
        }

        /// The time since the last mark belongs to the step NAME.
        public static void Mark(string name)
        {
            if (!On) return;
            long now = Now;
            lock (steps) steps.Add((name, Ms(now - last)));
            last = now;
        }

        public static void Script(Type type, string callback, long ticks)
        {
            if (ticks >= slowTicks)
            {
                if (slow.Count < 12) slow.Add(($"{type.FullName}.{callback}", ticks));
                else slowDropped++;
            }
            if (!byType) return;
            types.TryGetValue(type, out var t);
            types[type] = (t.Ticks + ticks, t.Calls + 1);
        }

        /// The log lines: the steps in order, then the destruction's parts.
        public static IEnumerable<string> Lines()
        {
            if (memory.Length > 0) yield return memory;
            lock (steps) yield return "Quit steps (ms): " + string.Join(", ", steps.Select(s => $"{s.Name} {s.Ms:0}"));
            yield return $"Quit destroyed {GameObjects:N0} GameObjects and {Components:N0} components: OnDisable {Ms(DisableTicks):0} ms in {Disables:N0} calls, "
                + $"OnDestroy {Ms(DestroyTicks):0} ms in {Destroys:N0} calls, engine components {Ms(EngineTicks):0} ms, releasing {Ms(ReleaseTicks):0} ms; "
                + $"{GC.CollectionCount(0) - gen0} garbage collections meanwhile ({GC.CollectionCount(2) - gen2} full), paused {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:0} ms";
            if (slow.Count > 0)
                yield return "Quit callbacks of 5 ms or more: " + string.Join(", ", slow.OrderByDescending(s => s.Ticks).Select(s => $"{s.Name} {Ms(s.Ticks):0}"))
                    + (slowDropped > 0 ? $" and {slowDropped} more" : "");
            if (byType)
                foreach (var (type, t) in types.OrderByDescending(t => t.Value.Ticks).Take(25))
                    yield return $"Quit callbacks of {type.FullName}: {Ms(t.Ticks):0.0} ms in {t.Calls:N0} calls";
        }
    }
}
