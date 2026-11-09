#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OniMoono.Host
{
    /// SIGTERM and SIGINT (a test runner's teardown, `timeout`, ^C) quit the player the way
    /// Application.Quit does: the frame loop ends at the next frame boundary, OnApplicationQuit
    /// runs, and the graphics device and window go before the process ends, so no thread is cut
    /// off in the middle of a GPU wait. CoreCLR's own handling ends the process from the signal
    /// thread with exit(), under render threads that may be waiting on the GPU (on WSL, a wait
    /// on /dev/dxg cut off that way has been followed by a kernel soft lockup).
    /// A quit that has not ended the process within the deadline (ONI_QUIT_DEADLINE seconds,
    /// default 8: a hung main thread, a long load) ends it at once; so does a second signal.
    /// Exit code 128 + the signal's number either way.
    internal sealed class QuitSignals
    {
        public const double DefaultDeadline = 8;

        readonly Action<int> requestQuit;
        readonly Action<int> exit;
        readonly Action<string> log;
        readonly TimeSpan deadline;
        int received;

        public QuitSignals(Action<int> requestQuit, Action<int> exit, Action<string> log, TimeSpan deadline)
        {
            this.requestQuit = requestQuit;
            this.exit = exit;
            this.log = log;
            this.deadline = deadline;
        }

        static IDisposable? installed;

        /// The player's handlers (not on Windows, where no test runner signals the player).
        public static void Install()
        {
            if (OperatingSystem.IsWindows() || installed != null) return;
            installed = new QuitSignals(OniMoono.Engine.PlayerLoop.RequestQuit, ExitNow, OniMoono.Engine.EngineLog.Engine,
                TimeSpan.FromSeconds(Deadline(Environment.GetEnvironmentVariable("ONI_QUIT_DEADLINE")))).Register();
        }

        /// This instance's SIGTERM and SIGINT handlers, in place of the runtime's until disposed.
        public IDisposable Register() => new Registrations(
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; Received(15, "SIGTERM"); }),
            PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; Received(2, "SIGINT"); }));

        sealed class Registrations(params PosixSignalRegistration[] all) : IDisposable
        {
            public void Dispose() { foreach (var r in all) r.Dispose(); }
        }

        /// ONI_QUIT_DEADLINE's seconds: a positive number, else the default.
        public static double Deadline(string? value) =>
            double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : DefaultDeadline;

        /// One signal (SIGNO, NAME): the first asks for the quit and starts the deadline, a later
        /// one ends the process.
        public void Received(int signo, string name)
        {
            int status = 128 + signo;
            if (Interlocked.Increment(ref received) > 1)
            {
                log($"{name} again: exiting now");
                exit(status);
                return;
            }
            log($"{name}: quitting at the end of the frame (exiting within {deadline.TotalSeconds:0.#} s)");
            requestQuit(status);
            new Thread(() =>
            {
                Thread.Sleep(deadline);
                log($"{name}: the quit did not end the process within {deadline.TotalSeconds:0.#} s; exiting now");
                exit(status);
            })
            { IsBackground = true, Name = "Quit deadline" }.Start();
        }

        /// The log flushed (for up to a second: a thread hung inside it holds its lock), then
        /// _exit, as the player's own exit (Player.ProcessExit).
        static void ExitNow(int status)
        {
            var flush = new Thread(() =>
            {
                try { OniMoono.Engine.EngineLog.Close(); Console.Out.Flush(); }
                catch (Exception) { }
            })
            { IsBackground = true };
            flush.Start();
            flush.Join(TimeSpan.FromSeconds(1));
            LinuxExit(status);
        }

        [DllImport("libc", EntryPoint = "_exit")]
        static extern void LinuxExit(int status);
    }
}
