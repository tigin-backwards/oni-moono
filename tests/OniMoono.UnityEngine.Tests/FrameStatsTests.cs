using System;
using System.IO;
using System.Linq;
using System.Threading;
using OniMoono.Engine;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// Frame stats' 600-frame lines: the game's update as FrameStatsProbes' patches time it
    /// (GameFrameStats, compiled in), the runtime's counts over a window (FrameStatsWindow) and
    /// the machine's state as /proc, /sys and Windows report it (MachineStats).
    public class FrameStatsTests
    {
        static void Spin(int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { }
        }

        /// One frame of the game's sim step: SimEveryTick around UnsafeSim200ms around
        /// StepTheSim, which calls into SimDLL for `nativeMs` (once from inside another call).
        static void SimFrame(float dt, int nativeMs, int managedMs)
        {
            GameFrameStats.SimFrame(dt);
            GameFrameStats.Begin(GameFrameStats.SimEveryTick);
            GameFrameStats.SimStep(dt > 0 ? 0.2f : 0f);
            GameFrameStats.Begin(GameFrameStats.UnsafeSim200ms);
            GameFrameStats.Begin(GameFrameStats.StepTheSim);
            GameFrameStats.NativeBegin();
            GameFrameStats.NativeBegin(); // a callback's call into SimDLL: part of the outer one
            GameFrameStats.NativeEnd();
            Spin(nativeMs);
            GameFrameStats.NativeEnd();
            Spin(managedMs);
            GameFrameStats.End(GameFrameStats.StepTheSim);
            GameFrameStats.End(GameFrameStats.UnsafeSim200ms);
            GameFrameStats.End(GameFrameStats.SimEveryTick);
        }

        static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        [Fact]
        public void TheSimStepSplitsIntoSimDllAndTheManagedRest()
        {
            GameFrameStats.Enabled = true;
            GameFrameStats.Reset();
            for (int i = 0; i < 3; i++) SimFrame(0.25f, 6, 3);
            var step = GameFrameStats.StepTheSim;
            Assert.Equal(3, GameFrameStats.Calls[step]);
            Assert.Equal(3, GameFrameStats.NativeCalls); // the nested calls count once
            Assert.InRange(Ms(GameFrameStats.Native[step]), 18, 60);
            Assert.InRange(Ms(GameFrameStats.Ticks[step] - GameFrameStats.Native[step]), 9, 60);
            // The spans around it hold the same SimDLL time.
            Assert.Equal(GameFrameStats.Native[step], GameFrameStats.Native[GameFrameStats.SimEveryTick]);
            Assert.Equal(GameFrameStats.NativeTicks, GameFrameStats.Native[GameFrameStats.UnsafeSim200ms]);
            Assert.True(GameFrameStats.Ticks[GameFrameStats.SimEveryTick] >= GameFrameStats.Ticks[step]);

            var lines = GameFrameStats.Take(1.5, 3, 3);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("game speed (over 3 frames, 1.5 s): speed 3, 0.40 game s a real s (asked 0.50); 200 ms sim steps 1.00 a frame, sim calls 1.00 a frame; frames at the game's 200 ms a frame limit 100%; paused 0%", lines[0]);
            Assert.Matches(@"^sim \(ms/frame over 3\): SimEveryTick [0-9.]+ \(SimDLL [0-9.]+, managed [0-9.]+\), UnsafeSim200ms .*StepTheSim [0-9.]+ \(SimDLL [0-9.]+, managed [0-9.]+\); SimDLL on the main thread [0-9.]+ in 1.00 calls a frame$", lines[1]);
            Assert.StartsWith("game update (ms/frame, calls a frame over 3): state machines render every tick 0.00 (0.00), render 200ms", lines[2]);
            Assert.EndsWith("jobs 0.00 (0.00) waiting for workers 0.00", lines[2]);
            // Taken: the next window starts empty.
            Assert.Equal(0, GameFrameStats.Calls[step]);
            Assert.Empty(GameFrameStats.Take(1, 3, 3));
        }

        [Fact]
        public void PausedFramesAndBucketsAreCounted()
        {
            GameFrameStats.Enabled = true;
            GameFrameStats.Reset();
            SimFrame(0, 0, 0);
            SimFrame(0.05f, 0, 0);
            for (int i = 0; i < 12; i++)
            {
                GameFrameStats.Begin(GameFrameStats.FirstBucket + 5);
                GameFrameStats.End(GameFrameStats.FirstBucket + 5);
            }
            var lines = GameFrameStats.Take(1, 2, 0);
            Assert.StartsWith("game speed (over 2 frames, 1.0 s): 0.05 game s a real s (asked 0.05); 200 ms sim steps 0.50 a frame, sim calls 1.00 a frame; frames at the game's 200 ms a frame limit 0%; paused 50%", lines[0]);
            Assert.Contains(", sim 200ms 0.00 (6.00),", lines[2]);
        }

        [Fact]
        public void OtherThreadsAndUnfinishedSpansAreLeftOut()
        {
            GameFrameStats.Enabled = true;
            GameFrameStats.Reset();
            GameFrameStats.SimFrame(0.1f);
            var worker = new Thread(() =>
            {
                GameFrameStats.Begin(GameFrameStats.Jobs);
                GameFrameStats.NativeBegin();
                GameFrameStats.NativeEnd();
                GameFrameStats.End(GameFrameStats.Jobs);
                GameFrameStats.AddJobWait(1);
            });
            worker.Start();
            worker.Join();
            Assert.Equal(0, GameFrameStats.Calls[GameFrameStats.Jobs]);
            Assert.Equal(0, GameFrameStats.NativeCalls);
            Assert.Equal(0, GameFrameStats.JobWait);
            // A span whose method threw has no end; the next Begin starts it again.
            GameFrameStats.Begin(GameFrameStats.AnimEvents);
            GameFrameStats.Begin(GameFrameStats.AnimEvents);
            GameFrameStats.End(GameFrameStats.AnimEvents);
            GameFrameStats.End(GameFrameStats.AnimEvents);
            Assert.Equal(1, GameFrameStats.Calls[GameFrameStats.AnimEvents]);
            // A SimDLL call left open by an exception ends with the frame.
            GameFrameStats.NativeBegin();
            GameFrameStats.SimFrame(0.1f);
            GameFrameStats.NativeBegin();
            GameFrameStats.NativeEnd();
            Assert.Equal(1, GameFrameStats.NativeCalls);
            GameFrameStats.Reset();
        }

        [Fact]
        public void ProcStatFieldsAreReadAfterTheCommandName()
        {
            var stat = "4242 (Oni (Moono) x) S 1 4242 4242 0 -1 4194560 1500 0 7 0 250 40 0 0 20 0 60 0 123 0 0";
            var parsed = MachineStats.ParseProcStat(stat);
            Assert.Equal(new MachineStats.ProcStat(1500, 7, 250, 40), parsed);
            Assert.Null(MachineStats.ParseProcStat("4242 (short) S 1"));
            Assert.Null(MachineStats.ParseProcStat("no parentheses"));
            // The machine's own, as the player reads it.
            if (OperatingSystem.IsLinux())
            {
                var self = MachineStats.ParseProcStat(File.ReadAllText("/proc/self/stat"));
                Assert.NotNull(self);
                Assert.True(self.Value.MinorFaults > 0);
            }
        }

        [Fact]
        public void MemInfoGivesBytes()
        {
            var info = MachineStats.ParseMemInfo("MemTotal:       16304068 kB\nMemFree:  1 kB\nMemAvailable:    9876543 kB\nCommitLimit:    20000000 kB\nCommitted_AS:   12345678 kB\nHugePages_Total:       0\n");
            Assert.Equal(new MachineStats.MemInfo(16304068L * 1024, 9876543L * 1024, 12345678L * 1024, 20000000L * 1024), info);
            Assert.Equal(new MachineStats.MemInfo(-1, -1, -1, -1), MachineStats.ParseMemInfo(""));
        }

        static string Folder(params (string Path, string Text)[] files)
        {
            var dir = Path.Combine(Path.GetTempPath(), "oni-frame-stats-" + Guid.NewGuid().ToString("N"));
            foreach (var (path, text) in files)
            {
                var full = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, text + "\n");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void LinuxPowerSuppliesAreDescribed()
        {
            var laptop = Folder(("AC/type", "Mains"), ("AC/online", "0"), ("BAT0/type", "Battery"), ("BAT0/capacity", "63"));
            var charging = Folder(("ADP1/type", "Mains"), ("ADP1/online", "1"), ("BAT1/type", "Battery"), ("BAT1/capacity", "100"), ("hid-mouse/type", "Battery"));
            var desktop = Folder(("ucsi-source-psy/type", "USB"));
            try
            {
                Assert.Equal("on battery 63%", MachineStats.LinuxPower(laptop));
                Assert.Equal("on AC, battery 100%", MachineStats.LinuxPower(charging));
                Assert.Null(MachineStats.LinuxPower(desktop));
                Assert.Null(MachineStats.LinuxPower(Path.Combine(desktop, "none")));
            }
            finally
            {
                foreach (var d in new[] { laptop, charging, desktop }) Directory.Delete(d, true);
            }
        }

        [Fact]
        public void LinuxClockIsTheCpusMean()
        {
            var cpus = Folder(("cpu0/cpufreq/scaling_cur_freq", "1400000"), ("cpu0/cpufreq/cpuinfo_max_freq", "4200000"), ("cpu0/cpufreq/base_frequency", "3000000"),
                ("cpu1/cpufreq/scaling_cur_freq", "1600000"), ("cpu1/cpufreq/cpuinfo_max_freq", "4200000"), ("cpu1/cpufreq/base_frequency", "3000000"),
                ("cpufreq/boost", "1"), ("cpuidle/current_driver", "none"));
            var amd = Folder(("cpu0/cpufreq/scaling_cur_freq", "3300000"), ("cpu0/cpufreq/cpuinfo_max_freq", "4000000"));
            var virtualMachine = Folder(("cpu0/topology/core_id", "0"));
            try
            {
                Assert.Equal("CPU clock 1.50 GHz (mean of 2), base 3.00, max 4.20", MachineStats.LinuxClock(cpus));
                Assert.Equal("CPU clock 3.30 GHz (mean of 1), base 4.00, max 4.00", MachineStats.LinuxClock(amd));
                Assert.Null(MachineStats.LinuxClock(virtualMachine));
            }
            finally
            {
                foreach (var d in new[] { cpus, amd, virtualMachine }) Directory.Delete(d, true);
            }
        }

        [Fact]
        public void WindowsPowerStatusIsDescribed()
        {
            Assert.Equal("on battery, battery 63%, battery saver on", MachineStats.WindowsPower(0, 0, 63, 1));
            Assert.Equal("on AC, battery 100%, battery saver off", MachineStats.WindowsPower(1, 8, 100, 0));
            Assert.Equal("on AC, battery saver off", MachineStats.WindowsPower(1, 1, 255, 0));
            Assert.Null(MachineStats.WindowsPower(1, 128, 255, 0)); // no system battery: a desktop
            Assert.Equal("best performance", MachineStats.PowerMode(new Guid("ded574b5-45a0-4f42-8737-46345c09c238")));
            Assert.Equal("balanced", MachineStats.PowerMode(Guid.Empty));
        }

        [Fact]
        public void MachineLineGivesRatesOverTheWindow()
        {
            var then = new MachineSample { ProcessCpu = 10, ThreadCpu = 4, Faults = 4294967000, HardFaults = 5, HardFaultsSystemWide = true };
            var now = new MachineSample
            {
                ProcessCpu = 40, ThreadCpu = 13.5, Faults = 704, HardFaults = 25, HardFaultsSystemWide = true,
                RamAvailable = 3L << 30, RamTotal = 16L << 30, Committed = 20L << 30, CommitLimit = 32L << 30,
                Power = "on battery, battery 63%, battery saver off", Plan = "power plan Balanced, power mode balanced", Clock = "CPU clock 2.10 GHz (mean of 12), rated 3.30, limit 3.30",
            };
            Assert.Equal("machine (over 10.0 s): process CPU 3.00 cores, main thread CPU 95% of the time; page faults 100/s, the system's hard fault reads 2.0/s; "
                + "RAM 3.0 GiB available of 16.0, committed 20.0 GiB of 32.0; on battery, battery 63%, battery saver off; power plan Balanced, power mode balanced; CPU clock 2.10 GHz (mean of 12), rated 3.30, limit 3.30",
                MachineStats.Describe(then, now, 10));
            Assert.Equal("machine (over 10.0 s): process CPU 0.00 cores", MachineStats.Describe(new MachineSample { ProcessCpu = 1 }, new MachineSample { ProcessCpu = 1 }, 10));
            // This machine's, as the player samples it.
            var a = MachineStats.Sample();
            Spin(20);
            var b = MachineStats.Sample();
            Assert.StartsWith("machine (over 0.0 s): process CPU ", MachineStats.Describe(a, b, 0.02));
            Assert.True(b.ProcessCpu >= a.ProcessCpu);
        }

        [Fact]
        public void RuntimeCountsAreTheWindowsAndEachGenerationsOwn()
        {
            var then = new FrameStatsWindow.RuntimeSample { Gen0 = 100, Gen1 = 20, Gen2 = 5, PauseMs = 50, PoolThreads = 8, PoolCompleted = 1000 };
            var now = new FrameStatsWindow.RuntimeSample { Gen0 = 110, Gen1 = 23, Gen2 = 6, PauseMs = 62.5, PoolThreads = 6, PoolQueued = 3, PoolCompleted = 1600 };
            var d = now - then;
            Assert.Equal((7L, 2L, 1L), (d.Gen0, d.Gen1, d.Gen2));
            Assert.Equal(12.5, d.PauseMs);
            Assert.Equal((6, -2, 3L, 600L), (d.PoolThreads, d.PoolThreadsChange, d.PoolQueued, d.PoolCompleted));
        }

        [Fact]
        public void WindowLinesFollowTheFirstWindow()
        {
            var seen = new System.Collections.Generic.List<int>();
            Func<double, int, string[]> report = (seconds, frames) =>
            {
                seen.Add(frames);
                return ["report " + frames];
            };
            FrameStatsWindow.Reports.Add(report);
            try
            {
                FrameStatsWindow.Take(0); // starts the window (or ends one another test started)
                seen.Clear();
                var lines = FrameStatsWindow.Take(600);
                Assert.Equal("report 600", lines[0]);
                Assert.StartsWith("main thread waits (ms/frame over 600): device frame 0.00, queued frames 0.00 (on for 0.00%), device calls 0.00 (0.00 a frame), texel copies 0.00; device thread waiting for the presenter 0.00; GC paused ", lines[1]);
                Assert.StartsWith("machine (over ", lines[2]);
                Assert.Equal(3, lines.Count);
                Assert.Equal([600], seen);
            }
            finally
            {
                FrameStatsWindow.Reports.Remove(report);
            }
        }
    }
}
