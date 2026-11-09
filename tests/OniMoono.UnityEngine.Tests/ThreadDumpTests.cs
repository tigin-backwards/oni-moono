using System;
using System.Diagnostics;
using System.IO;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// The quit deadline's thread dump (src/OniMoono.Host/ThreadDump.cs, compiled in here), read
    /// from a made-up /proc/<pid>/task tree.
    public class ThreadDumpTests : IDisposable
    {
        readonly string tasks = Path.Combine(Path.GetTempPath(), "threaddump-" + Guid.NewGuid().ToString("N"));

        public ThreadDumpTests()
        {
            Task(1200, "OniMoono", "1200 (OniMoono) S 1 1200 1200 0 -1", "futex_do_wait");
            Task(1234, "GL presenter", "1234 (GL presenter) D 1 1200 1200 0 -1", "dxgkio_wait_sync_object_cpu");
            Task(1300, "a) b (c", "1300 (a) b (c) R 1 1200", null);
            Directory.CreateDirectory(Path.Combine(tasks, "not-a-thread"));
        }

        void Task(int tid, string comm, string stat, string wchan)
        {
            string dir = Path.Combine(tasks, tid.ToString());
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "comm"), comm + "\n");
            File.WriteAllText(Path.Combine(dir, "stat"), stat + "\n");
            if (wchan != null) File.WriteAllText(Path.Combine(dir, "wchan"), wchan);
        }

        public void Dispose() => Directory.Delete(tasks, true);

        [Fact]
        public void EachThreadsNameStateAndWaitChannel()
        {
            var lines = ThreadDump.Lines(tasks, [(1234, "presenter: the frame's fence (sync 0x5, frame 9) on thread \"GL presenter\" (tid 1234, managed 12), waiting 12.0 s")],
                Stopwatch.StartNew(), TimeSpan.FromSeconds(5));
            Assert.Equal([
                "  waiting: presenter: the frame's fence (sync 0x5, frame 9) on thread \"GL presenter\" (tid 1234, managed 12), waiting 12.0 s",
                "  1200 \"OniMoono\": S (sleeping), wchan futex_do_wait",
                "  1234 \"GL presenter\": D (uninterruptible wait), wchan dxgkio_wait_sync_object_cpu (a long wait, above)",
                "  1300 \"a) b (c\": R (running), wchan ?",
            ], lines);
        }

        [Fact]
        public void StopsWhenTheBudgetIsGone()
        {
            var lines = ThreadDump.Lines(tasks, [], Stopwatch.StartNew(), TimeSpan.Zero);
            Assert.Equal(["  (the rest of 3 threads left out: 0 ms gone)"], lines);
        }

        [Fact]
        public void AMissingTreeIsALineNotAnException()
        {
            var lines = ThreadDump.Lines(Path.Combine(tasks, "gone"), [], Stopwatch.StartNew(), TimeSpan.FromSeconds(5));
            Assert.Single(lines);
            Assert.Contains("unreadable: DirectoryNotFoundException", lines[0]);
        }

        [Fact]
        public void States()
        {
            Assert.Equal("?", ThreadDump.State(""));
            Assert.Equal("?", ThreadDump.State("12 (x)"));
            Assert.Equal("t (tracing stop)", ThreadDump.State("12 (x) t 1"));
            Assert.Equal("Q", ThreadDump.State("12 (x) Q 1"));
        }

        [Fact]
        public void ThisProcessesOwnThreads()
        {
            if (!OperatingSystem.IsLinux()) return;
            var lines = ThreadDump.Lines("/proc/self/task", [], Stopwatch.StartNew(), TimeSpan.FromSeconds(5));
            Assert.Contains(lines, l => l.StartsWith($"  {Environment.ProcessId} \"") && !l.EndsWith("wchan ?"));
        }
    }
}
