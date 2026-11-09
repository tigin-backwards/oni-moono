using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OniMoono.Engine;
using Xunit;

namespace OniMoono.Tests
{
    /// A sliced wait (a GPU fence waited for 100 ms at a time) is logged after 10 s and each
    /// minute after, and tagged for the quit's thread dump while it goes on (LongWaits).
    public class LongWaitsTests
    {
        static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

        static List<string> Run(params double[] slices)
        {
            var lines = new List<string>();
            var log = LongWaits.Log;
            LongWaits.Log = lines.Add;
            try
            {
                LongWaits.Tag tag = null;
                foreach (double s in slices) LongWaits.Slice(ref tag, "test site", "sync 0x1", At(s));
                LongWaits.End(ref tag, At(slices[^1] + 0.1));
                Assert.Null(tag);
            }
            finally { LongWaits.Log = log; }
            return lines;
        }

        [Fact]
        public void AWaitShorterThanTenSecondsIsNotLogged()
        {
            Assert.Empty(Run(Enumerable.Range(0, 99).Select(i => i * 0.1).ToArray()));
        }

        [Fact]
        public void LogsAtTenSecondsThenEachMinuteThenTheEnd()
        {
            var slices = Enumerable.Range(0, 1300).Select(i => i * 0.1).ToArray(); // 0 to 129.9 s
            var lines = Run(slices);
            Assert.Equal(4, lines.Count);
            Assert.StartsWith("long wait: still waiting at test site (sync 0x1) on thread ", lines[0]);
            Assert.EndsWith("waiting 10.0 s", lines[0]);
            Assert.EndsWith("waiting 60.0 s", lines[1]);
            Assert.EndsWith("waiting 120.0 s", lines[2]);
            Assert.StartsWith("long wait: over at test site", lines[3]);
            Assert.Contains("(tid ", lines[0]);
        }

        [Fact]
        public void ASliceLateByMinutesLogsOnce()
        {
            var lines = Run(0, 1, 200, 201);
            Assert.Equal(2, lines.Count);
            Assert.EndsWith("waiting 200.0 s", lines[0]);
        }

        [Fact]
        public void ReportTimes()
        {
            var times = new List<double> { LongWaits.FirstReport };
            for (int i = 0; i < 3; i++) times.Add(LongWaits.NextReport(times[^1]));
            Assert.Equal([10, 60, 120, 180], times);
        }

        [Fact]
        public void AWaitIsTaggedWhileItGoesOn()
        {
            LongWaits.Tag tag = null;
            LongWaits.Slice(ref tag, "tagged site", "frame 7", Stopwatch.GetTimestamp());
            try
            {
                var mine = LongWaits.Current().Where(w => w.Line.StartsWith("tagged site (frame 7) on thread ")).ToList();
                Assert.Single(mine);
                if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) Assert.NotEqual(0, mine[0].Tid);
            }
            finally { LongWaits.End(ref tag, Stopwatch.GetTimestamp()); }
            Assert.DoesNotContain(LongWaits.Current(), w => w.Line.StartsWith("tagged site"));
        }
    }
}
