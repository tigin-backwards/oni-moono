#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace OniMoono.Host
{
    /// Each thread's state, for the log when a quit did not end the process in time (QuitSignals):
    /// the waits the engine knows go on (LongWaits.Current), then on Linux each
    /// /proc/self/task/<tid>'s name, state and kernel wait channel (wchan names a wait inside the
    /// kernel, such as dxg's for a fence on WSL's /dev/dxg), elsewhere the IDs and states the
    /// process's thread list gives. The files are read directly (no ptrace, no tools), within a
    /// time budget, and nothing here throws.
    internal static class ThreadDump
    {
        /// The whole dump's time, on a thread of its own: the log may be held by a hung thread.
        public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);

        /// Writes the dump to LOG, waiting for it at most Budget.
        public static void Write(Action<string> log)
        {
            var dump = new Thread(() =>
            {
                try
                {
                    var time = Stopwatch.StartNew();
                    var waits = OniMoono.Engine.LongWaits.Current();
                    var lines = OperatingSystem.IsLinux() ? Lines("/proc/self/task", waits, time, Budget) : Others(waits);
                    log("threads at the quit deadline:\n" + string.Join("\n", lines));
                }
                catch (Exception) { }
            })
            { IsBackground = true, Name = "Thread dump" };
            try
            {
                dump.Start();
                dump.Join(Budget);
            }
            catch (Exception) { }
        }

        /// The dump from TASKS (a /proc/<pid>/task directory), with WAITS marked on their threads,
        /// stopping once TIME passes BUDGET.
        public static List<string> Lines(string tasks, IReadOnlyList<(int Tid, string Line)> waits, Stopwatch time, TimeSpan budget)
        {
            var lines = new List<string>();
            foreach (var w in waits) lines.Add("  waiting: " + w.Line);
            List<int> tids;
            try
            {
                tids = Directory.EnumerateDirectories(tasks)
                    .Select(d => int.TryParse(Path.GetFileName(d), out int tid) ? tid : -1)
                    .Where(tid => tid >= 0).OrderBy(tid => tid).ToList();
            }
            catch (Exception e)
            {
                lines.Add($"  {tasks} unreadable: {e.GetType().Name}");
                return lines;
            }
            foreach (int tid in tids)
            {
                if (time.Elapsed > budget)
                {
                    lines.Add($"  (the rest of {tids.Count} threads left out: {budget.TotalMilliseconds:0} ms gone)");
                    break;
                }
                string dir = Path.Combine(tasks, tid.ToString());
                string comm = Read(Path.Combine(dir, "comm")).Trim();
                string state = State(Read(Path.Combine(dir, "stat")));
                string wchan = Read(Path.Combine(dir, "wchan")).Trim();
                var line = $"  {tid} \"{comm}\": {state}, wchan {(wchan.Length == 0 ? "?" : wchan)}";
                foreach (var w in waits)
                    if (w.Tid == tid) line += " (a long wait, above)";
                lines.Add(line);
            }
            return lines;
        }

        /// The state letter from a stat line, with its name (proc(5)), or "?". The name in
        /// parentheses may itself hold spaces and parentheses, so the fields start after the last ')'.
        public static string State(string stat)
        {
            int close = stat.LastIndexOf(')');
            if (close < 0) return "?";
            var rest = stat.AsSpan(close + 1).TrimStart();
            if (rest.Length == 0) return "?";
            char c = rest[0];
            string name = c switch
            {
                #pragma warning disable format // a hand-laid table
                'R' => "running", 'S' => "sleeping", 'D' => "uninterruptible wait", 'Z' => "zombie",
                'T' => "stopped", 't' => "tracing stop", 'X' => "dead", 'I' => "idle", 'P' => "parked",
                'W' => "waking", 'K' => "wakekill", _ => "",
                #pragma warning restore format
            };
            return name.Length == 0 ? c.ToString() : $"{c} ({name})";
        }

        /// A small /proc file's text, or "" when it cannot be read.
        static string Read(string path)
        {
            try
            {
                using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
                var buffer = new byte[512];
                int n = 0, got;
                while (n < buffer.Length && (got = f.Read(buffer, n, buffer.Length - n)) > 0) n += got;
                return System.Text.Encoding.UTF8.GetString(buffer, 0, n);
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// Without /proc: the process's thread list (IDs and states; no names).
        static List<string> Others(IReadOnlyList<(int Tid, string Line)> waits)
        {
            var lines = new List<string>();
            foreach (var w in waits) lines.Add("  waiting: " + w.Line);
            try
            {
                using var self = Process.GetCurrentProcess();
                foreach (ProcessThread t in self.Threads)
                {
                    using (t)
                    {
                        string state;
                        try { state = t.ThreadState == System.Diagnostics.ThreadState.Wait ? $"Wait ({t.WaitReason})" : t.ThreadState.ToString(); }
                        catch (Exception) { state = "?"; }
                        lines.Add($"  {t.Id}: {state}{(waits.Any(w => w.Tid == t.Id) ? " (a long wait, above)" : "")}");
                    }
                }
            }
            catch (Exception e)
            {
                lines.Add($"  threads unreadable: {e.GetType().Name}");
            }
            return lines;
        }
    }
}
