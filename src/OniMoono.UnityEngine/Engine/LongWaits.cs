using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace OniMoono.Engine
{
    /// One wait made of short timed slices (a GPU fence waited for 100 ms at a time until it
    /// signals), told when a slice times out. A wait that goes on is reported (LongWaits); the
    /// wait itself is the caller's and does not change. A wait whose first slice succeeds costs
    /// nothing more than this struct's null check in End.
    internal struct LongWait
    {
        LongWaits.Tag tag;

        /// A slice of the wait at SITE, for WHAT (the sync object, the frame), timed out.
        public void Slice(string site, string what) => LongWaits.Slice(ref tag, site, what, Stopwatch.GetTimestamp());

        /// The wait is over.
        public void End()
        {
            if (tag != null) LongWaits.End(ref tag, Stopwatch.GetTimestamp());
        }
    }

    /// Sliced waits that go on. From its first timed-out slice a wait is tagged (site, what it
    /// waits for, thread, since when), and the tags of the waits going on are what the quit
    /// deadline's thread dump prints (Current). A wait still going after 10 s is logged, and
    /// again each minute, as one [engine] line; one that was logged logs its end too. A hang in
    /// a GPU wait (a fence on WSL's /dev/dxg that never signalled) once left no word of where.
    internal static class LongWaits
    {
        public const double FirstReport = 10;

        /// Where the lines go (a test's recorder in place of the engine log).
        internal static Action<string> Log = EngineLog.Engine;

        /// ONI_TEST_STALL_FENCE=1: the presenter's frame fence wait never ends, its slices told
        /// as a real wait's are (a manual check of the reports and the quit's thread dump).
        public static readonly bool StallFence = Environment.GetEnvironmentVariable("ONI_TEST_STALL_FENCE") == "1";

        static readonly ConcurrentDictionary<Tag, byte> current = new();

        internal sealed class Tag
        {
            public readonly string Site, What, Thread;
            public readonly int ManagedId, Tid;
            public readonly long Since;
            public double NextReport = FirstReport;

            public Tag(string site, string what, long since)
            {
                Site = site;
                What = what;
                Since = since;
                var thread = System.Threading.Thread.CurrentThread;
                Thread = thread.Name ?? "unnamed";
                ManagedId = thread.ManagedThreadId;
                Tid = OsThreadId();
            }

            public double Seconds(long now) => (now - Since) / (double)Stopwatch.Frequency;

            public string Describe(long now) =>
                $"{Site} ({What}) on thread \"{Thread}\" (tid {Tid}, managed {ManagedId}), waiting {Seconds(now):0.0} s";
        }

        /// The report after the one at AFTER seconds: 10 s, then each whole minute.
        public static double NextReport(double after) => after < 60 ? 60 : after + 60;

        internal static void Slice(ref Tag tag, string site, string what, long now)
        {
            if (tag == null)
            {
                tag = new Tag(site, what, now);
                current[tag] = 0;
                return;
            }
            double seconds = tag.Seconds(now);
            if (seconds < tag.NextReport) return;
            while (tag.NextReport <= seconds) tag.NextReport = NextReport(tag.NextReport);
            Log("long wait: still waiting at " + tag.Describe(now));
        }

        internal static void End(ref Tag tag, long now)
        {
            current.TryRemove(tag, out _);
            if (tag.NextReport > FirstReport) Log("long wait: over at " + tag.Describe(now));
            tag = null;
        }

        /// The waits going on, one line each, with the thread's OS ID first.
        public static List<(int Tid, string Line)> Current()
        {
            var all = new List<(int, string)>();
            long now = Stopwatch.GetTimestamp();
            foreach (var tag in current.Keys) all.Add((tag.Tid, tag.Describe(now)));
            return all;
        }

        /// The kernel's ID of this thread (/proc/self/task/<tid>, or Windows' thread ID); 0 if
        /// unknown.
        static int OsThreadId()
        {
            try
            {
                if (OperatingSystem.IsWindows()) return (int)GetCurrentThreadId();
                if (OperatingSystem.IsLinux()) return GetTid();
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { }
            return 0;
        }

        [DllImport("libc", EntryPoint = "gettid")]
        static extern int GetTid();

        [DllImport("kernel32")]
        static extern uint GetCurrentThreadId();
    }
}
