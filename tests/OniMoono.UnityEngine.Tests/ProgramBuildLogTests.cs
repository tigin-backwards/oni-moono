using System.Diagnostics;
using System.Linq;
using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// Where program builds' time went (ProgramBuildLog), the warm-up's lines (GlWarmup.Summary)
    /// and the start-up lines about the driver and the processors.
    public class ProgramBuildLogTests
    {
        static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

        static ProgramBuildSplit Split(double prepare, double vertex, double fragment, double link, double status, double reflect, double cpu = 0, double geometry = 0) => new()
        {
            Prepare = Ticks(prepare), Vertex = Ticks(vertex), Fragment = Ticks(fragment), Geometry = Ticks(geometry), Link = Ticks(link), LinkStatus = Ticks(status),
            Reflect = Ticks(reflect), Total = Ticks(prepare + vertex + fragment + geometry + link + status + reflect), Cpu = Ticks(cpu),
        };

        [Fact]
        public void SumsTheSplitsAndKeepsTheSlowestThree()
        {
            var log = new ProgramBuildLog();
            log.Add("A", 1, Split(1, 10, 20, 5, 2, 1));
            log.Add("B", 2, Split(1, 100, 200, 50, 20, 1));
            log.Add("C ", 3, Split(0, 1, 1, 1, 0, 0));
            log.Add("D", 4, Split(2, 40, 40, 10, 0, 2));
            Assert.Equal(4, log.Count);
            Assert.Equal("preparation 4 ms, vertex compile 151 ms, fragment compile 261 ms, link 66 ms, link status 22 ms, reflection 4 ms; "
                + "slowest: B variant 2 372 ms (preparation 1, compile 300, link 70), D variant 4 94 ms (preparation 2, compile 80, link 10), A variant 1 39 ms (preparation 1, compile 30, link 7)", log.Describe());
            log.Reset();
            Assert.Equal(0, log.Count);
            Assert.DoesNotContain("slowest", log.Describe());
        }

        [Fact]
        public void AGeometryStageIsNamedOnlyWhenThereWasOne()
        {
            Assert.DoesNotContain("geometry", Split(1, 2, 3, 4, 5, 6).Describe());
            Assert.Contains("geometry compile 7 ms, link 4 ms", Split(1, 2, 3, 4, 5, 6, geometry: 7).Describe());
        }

        [Fact]
        public void CpuTimeIsUnknownOnceOneBuildsIs()
        {
            var sum = Split(0, 1, 1, 1, 0, 0, cpu: 2);
            sum.Add(Split(0, 1, 1, 1, 0, 0, cpu: 3));
            Assert.Equal("5 ms of CPU", sum.CpuText());
            var unknown = Split(0, 1, 1, 1, 0, 0);
            unknown.Cpu = -1;
            sum.Add(unknown);
            Assert.Equal("CPU time not known", sum.CpuText());
        }

        [Fact]
        public void TheWarmupSaysWhereItsTimeWentAndWhatItCostTheFrames()
        {
            var warmed = new ProgramBuildLog();
            warmed.Add("Klei/Liquid", 7, Split(1, 100, 120, 20, 0, 2, cpu: 60));
            var firstUse = new ProgramBuildLog();
            var lines = GlWarmup.Summary(ran: 1, skipped: 2, ticks: Ticks(244), sinceAskedMs: 900, warmed, firstUse, heldFrames: 3, heldTicks: Ticks(150),
                batches: 4, spanTicks: Ticks(800), processCpuMs: 1500, processors: 2).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Equal("shader warm-up: 1 programs built in 244 ms on the GL thread while it was idle (60 ms of CPU on that thread), 2 queued twice or built at first use already, done 900 ms after the warm-up was asked", lines[0]);
            Assert.Equal("shader warm-up: preparation 1 ms, vertex compile 100 ms, fragment compile 120 ms, link 20 ms, link status 0 ms, reflection 2 ms; "
                + "slowest: Klei/Liquid variant 7 243 ms (preparation 1, compile 220, link 20); 3 frames or calls were handed over during a build and waited 150 ms in all; "
                + "over the warm-up's 800 ms the GL thread executed 4 frames or calls and the process used 1500 ms of CPU on 2 logical processors", lines[1]);

            firstUse.Add("UI/Default", 0, Split(0, 4, 5, 1, 0, 0, cpu: 10));
            lines = GlWarmup.Summary(1, 2, Ticks(244), 900, warmed, firstUse, 3, Ticks(150), 4, Ticks(800), 1500, 2).ToArray();
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("shader programs at first use: 1 built in 10 ms while the main thread waited (10 ms of CPU on the GL thread): preparation 0 ms, vertex compile 4 ms", lines[2]);
        }

        [Fact]
        public void AWarmupThatBuiltNothingIsOneLine()
        {
            var lines = GlWarmup.Summary(0, 5, 0, 12, new ProgramBuildLog(), new ProgramBuildLog(), 0, 0, 0, 0, 0, 8).ToArray();
            Assert.Single(lines);
            Assert.StartsWith("shader warm-up: 0 programs built in 0 ms", lines[0]);
        }

        [Fact]
        public void TheDriverLineSaysWhatItOffersForPrograms()
        {
            Assert.Equal("OpenGL programs: 0 program binary formats (programs cannot be saved), parallel compile not offered",
                GlDevice.ProgramSupport(0, false, false, _ => throw new Xunit.Sdk.XunitException("no limit to read")));
            Assert.Equal("OpenGL programs: 1 program binary format (programs can be saved and loaded), parallel compile offered (KHR, ARB; compiler thread limit the driver's choice)",
                GlDevice.ProgramSupport(1, true, true, name => name == 0x91B0 ? -1 : 0));
            Assert.Equal("OpenGL programs: 2 program binary formats (programs can be saved and loaded), parallel compile offered (ARB; compiler thread limit 4)",
                GlDevice.ProgramSupport(2, false, true, _ => 4));
        }

        [Fact]
        public void ThreadCpuTimeAdvancesWithWork()
        {
            long before = ThreadCpuTime.Now;
            if (before < 0) return;
            var clock = Stopwatch.StartNew();
            double x = 0;
            while (clock.ElapsedMilliseconds < 80) x += System.Math.Sqrt(x + 1);
            long used = ThreadCpuTime.Now - before;
            Assert.InRange(used, 1, Ticks(2000));
        }
    }
}
