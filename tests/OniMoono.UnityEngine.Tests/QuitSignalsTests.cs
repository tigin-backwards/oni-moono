using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// SIGTERM and SIGINT quit the player at a frame boundary (src/OniMoono.Host/QuitSignals.cs,
    /// compiled in here): the first asks for the quit, and the deadline ends the process if the
    /// quit does not; a second signal ends it at once.
    public class QuitSignalsTests
    {
        sealed class Recorder
        {
            readonly BlockingCollection<string> events = new();
            public QuitSignals Make(TimeSpan deadline) => new(
                status => events.Add($"quit {status}"), status => events.Add($"exit {status}"), _ => { }, deadline,
                () => { events.Add("dump"); throw new InvalidOperationException("a dump that throws"); });
            public string Next(int ms = 5000) => events.TryTake(out var e, ms) ? e : "nothing";
        }

        [Fact]
        public void FirstSignalRequestsTheQuitAndTheDeadlineEndsTheProcess()
        {
            var r = new Recorder();
            var quit = r.Make(TimeSpan.FromMilliseconds(300));
            quit.Received(15, "SIGTERM");
            Assert.Equal("quit 143", r.Next(0));
            Assert.Equal("nothing", r.Next(50)); // the quit has its time first
            Assert.Equal("dump", r.Next()); // each thread's state, then the exit even if the dump throws
            Assert.Equal("exit 143", r.Next());
        }

        [Fact]
        public void SecondSignalEndsTheProcessAtOnce()
        {
            var r = new Recorder();
            var quit = r.Make(TimeSpan.FromMinutes(5));
            quit.Received(15, "SIGTERM");
            quit.Received(2, "SIGINT");
            Assert.Equal(["quit 143", "exit 130"], new[] { r.Next(0), r.Next(0) });
        }

        [Fact]
        public void Deadline()
        {
            Assert.Equal(QuitSignals.DefaultDeadline, QuitSignals.Deadline(null));
            Assert.Equal(QuitSignals.DefaultDeadline, QuitSignals.Deadline("soon"));
            Assert.Equal(QuitSignals.DefaultDeadline, QuitSignals.Deadline("0"));
            Assert.Equal(2.5, QuitSignals.Deadline("2.5"));
        }

        [DllImport("libc", EntryPoint = "kill")]
        static extern int Kill(int pid, int signal);

        [Fact]
        public void ARealSigtermReachesTheHandlerNotTheRuntimes()
        {
            if (OperatingSystem.IsWindows()) return;
            var r = new Recorder();
            using (r.Make(TimeSpan.FromMinutes(5)).Register())
            {
                Assert.Equal(0, Kill(Environment.ProcessId, 15));
                Assert.Equal("quit 143", r.Next());
            }
        }
    }
}
