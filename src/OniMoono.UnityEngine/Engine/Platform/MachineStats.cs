using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OniMoono.Engine
{
    /// The machine's state at a moment, for frame stats' 600-frame lines (FrameStatsWindow): the
    /// process's and main thread's CPU time, page faults, memory, and on a laptop the power
    /// source, power plan and CPU clock, as the operating system reports them. Each part is
    /// read once a window (a few system calls, or files under /proc and /sys on Linux); a part
    /// the system does not offer, or whose call fails, is left out from then on.
    internal sealed class MachineSample
    {
        public long Ticks;
        /// Seconds of CPU time: the process's, and the thread that sampled (the main thread);
        /// negative when unknown.
        public double ProcessCpu = -1, ThreadCpu = -1;
        /// Page faults of the process (Windows: soft and hard), and hard faults (Linux: the
        /// process's major faults; Windows: the system's page reads); negative when unknown.
        public long Faults = -1, HardFaults = -1;
        public bool HardFaultsSystemWide;
        /// Bytes; negative when unknown.
        public long RamAvailable = -1, RamTotal = -1, Committed = -1, CommitLimit = -1;
        /// Power source and battery ("on AC", "on battery 63%", with ", battery saver on"), the
        /// power plan or profile, and the CPU's clock against its rated one; null when unknown.
        public string Power, Plan, Clock;
    }

    internal static class MachineStats
    {
        static bool linuxFailed, windowsFailed, planFailed, overlayFailed, clockFailed, pageReadsFailed;

        public static MachineSample Sample()
        {
            var s = new MachineSample { Ticks = Stopwatch.GetTimestamp() };
            try
            {
                if (OperatingSystem.IsLinux() && !linuxFailed) SampleLinux(s);
                else if (OperatingSystem.IsWindows() && !windowsFailed) SampleWindows(s);
            }
            catch (Exception e)
            {
                if (OperatingSystem.IsLinux()) linuxFailed = true;
                else windowsFailed = true;
                EngineLog.Engine($"frame stats: the machine's state could not be read, so it is left out: {e.GetType().Name}: {e.Message}");
            }
            if (s.ProcessCpu < 0)
            {
                using var self = Process.GetCurrentProcess();
                s.ProcessCpu = self.TotalProcessorTime.TotalSeconds;
            }
            return s;
        }

        /// The window's line: rates between two samples, and the later one's state.
        public static string Describe(MachineSample then, MachineSample now, double seconds)
        {
            var c = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            text.Append(string.Format(c, "machine (over {0:F1} s):", seconds));
            string sep = " ";
            void Part(string part)
            {
                text.Append(sep).Append(part);
                sep = "; ";
            }
            if (seconds <= 0) seconds = 1e-9;
            if (now.ProcessCpu >= 0 && then.ProcessCpu >= 0)
            {
                var cpu = string.Format(c, "process CPU {0:F2} cores", (now.ProcessCpu - then.ProcessCpu) / seconds);
                if (now.ThreadCpu >= 0 && then.ThreadCpu >= 0)
                    cpu += string.Format(c, ", main thread CPU {0:F0}% of the time", 100 * (now.ThreadCpu - then.ThreadCpu) / seconds);
                Part(cpu);
            }
            if (now.Faults >= 0 && then.Faults >= 0)
            {
                var faults = string.Format(c, "page faults {0:F0}/s", Counted(now.Faults, then.Faults) / seconds);
                if (now.HardFaults >= 0 && then.HardFaults >= 0)
                    faults += string.Format(c, now.HardFaultsSystemWide ? ", the system's hard fault reads {0:F1}/s" : ", hard {0:F1}/s", Counted(now.HardFaults, then.HardFaults) / seconds);
                Part(faults);
            }
            if (now.RamAvailable >= 0 && now.RamTotal > 0)
            {
                var ram = string.Format(c, "RAM {0:F1} GiB available of {1:F1}", now.RamAvailable / Gib, now.RamTotal / Gib);
                if (now.Committed >= 0 && now.CommitLimit > 0) ram += string.Format(c, ", committed {0:F1} GiB of {1:F1}", now.Committed / Gib, now.CommitLimit / Gib);
                Part(ram);
            }
            if (now.Power != null) Part(now.Power);
            if (now.Plan != null) Part(now.Plan);
            if (now.Clock != null) Part(now.Clock);
            return text.ToString();
        }

        const double Gib = 1024.0 * 1024 * 1024;

        /// A count's increase, for Windows' 32-bit counts across their wrap-around too.
        internal static long Counted(long now, long then) => now >= then ? now - then : now - then + (1L << 32);

        // ----- Linux -----

        /// USER_HZ, the unit of /proc's CPU times: 100 on every Linux the player runs on.
        const double ClockTicks = 100;

        static void SampleLinux(MachineSample s)
        {
            if (ParseProcStat(File.ReadAllText("/proc/self/stat")) is { } process)
            {
                s.ProcessCpu = (process.UserTicks + process.SystemTicks) / ClockTicks;
                s.Faults = process.MinorFaults + process.MajorFaults;
                s.HardFaults = process.MajorFaults;
            }
            if (ParseProcStat(File.ReadAllText("/proc/thread-self/stat")) is { } thread)
                s.ThreadCpu = (thread.UserTicks + thread.SystemTicks) / ClockTicks;
            var memory = ParseMemInfo(File.ReadAllText("/proc/meminfo"));
            s.RamAvailable = memory.Available;
            s.RamTotal = memory.Total;
            s.Committed = memory.Committed;
            s.CommitLimit = memory.CommitLimit;
            s.Power = LinuxPower("/sys/class/power_supply");
            s.Plan = LinuxPlan();
            if (!clockFailed)
            {
                try
                {
                    s.Clock = LinuxClock("/sys/devices/system/cpu");
                }
                catch (Exception)
                {
                    clockFailed = true;
                }
            }
        }

        internal readonly record struct ProcStat(long MinorFaults, long MajorFaults, long UserTicks, long SystemTicks);

        /// /proc/[pid]/stat (and a thread's): "pid (comm) state ppid …", where comm may hold
        /// spaces and parentheses; minflt is field 10, majflt 12, utime 14, stime 15.
        internal static ProcStat? ParseProcStat(string text)
        {
            int close = text.LastIndexOf(')');
            if (close < 0) return null;
            var fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // fields[0] is field 3 (state).
            long Field(int n) => long.Parse(fields[n - 3], CultureInfo.InvariantCulture);
            if (fields.Length < 13) return null;
            return new ProcStat(Field(10), Field(12), Field(14), Field(15));
        }

        internal readonly record struct MemInfo(long Total, long Available, long Committed, long CommitLimit);

        /// /proc/meminfo's MemTotal, MemAvailable, Committed_AS and CommitLimit, in bytes (-1
        /// for one it lacks).
        internal static MemInfo ParseMemInfo(string text)
        {
            long total = -1, available = -1, committed = -1, limit = -1;
            foreach (var line in text.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                var value = line[(colon + 1)..].Trim();
                if (value.EndsWith(" kB", StringComparison.Ordinal)) value = value[..^3];
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long kib)) continue;
                switch (line[..colon])
                {
                    case "MemTotal": total = kib * 1024; break;
                    case "MemAvailable": available = kib * 1024; break;
                    case "Committed_AS": committed = kib * 1024; break;
                    case "CommitLimit": limit = kib * 1024; break;
                }
            }
            return new MemInfo(total, available, committed, limit);
        }

        /// The power supplies under `dir` (/sys/class/power_supply): "on AC" or "on battery",
        /// with the batteries' charge; null without a battery (a desktop) or the folder.
        internal static string LinuxPower(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            bool? mains = null;
            var batteries = new StringBuilder();
            foreach (var supply in Directory.GetDirectories(dir))
            {
                string type = ReadLine(Path.Combine(supply, "type"));
                if (type == "Mains")
                    mains = (mains ?? false) || ReadLine(Path.Combine(supply, "online")) == "1";
                else if (type == "Battery" && ReadLine(Path.Combine(supply, "capacity")) is { } capacity)
                    batteries.Append(batteries.Length == 0 ? "" : ", ").Append(capacity).Append('%');
            }
            if (batteries.Length == 0) return null;
            return (mains == false ? "on battery " : mains == true ? "on AC, battery " : "battery ") + batteries;
        }

        /// The platform profile (ACPI: low-power, balanced, performance) and the CPU frequency
        /// governor, as far as the system has them.
        static string LinuxPlan()
        {
            var profile = ReadLine("/sys/firmware/acpi/platform_profile");
            var governor = ReadLine("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor");
            if (profile == null && governor == null) return null;
            if (profile == null) return "CPU governor " + governor;
            return "platform profile " + profile + (governor != null ? ", CPU governor " + governor : "");
        }

        /// The CPUs' mean current clock against their mean rated (base, else maximum) one, from
        /// cpufreq under `dir` (/sys/devices/system/cpu); null without cpufreq (virtual machines).
        internal static string LinuxClock(string dir)
        {
            double current = 0, rated = 0, max = 0;
            int n = 0;
            foreach (var cpu in Directory.GetDirectories(dir, "cpu*"))
            {
                var freq = Path.Combine(cpu, "cpufreq");
                if (!Directory.Exists(freq) || !long.TryParse(ReadLine(Path.Combine(freq, "scaling_cur_freq")), out long khz)) continue;
                long.TryParse(ReadLine(Path.Combine(freq, "cpuinfo_max_freq")), out long maxKhz);
                if (!long.TryParse(ReadLine(Path.Combine(freq, "base_frequency")), out long baseKhz)) baseKhz = maxKhz;
                current += khz;
                rated += baseKhz;
                max += maxKhz;
                n++;
            }
            if (n == 0) return null;
            var c = CultureInfo.InvariantCulture;
            return string.Format(c, "CPU clock {0:F2} GHz (mean of {1}), base {2:F2}, max {3:F2}", current / n / 1e6, n, rated / n / 1e6, max / n / 1e6);
        }

        static string ReadLine(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ----- Windows -----

        static void SampleWindows(MachineSample s)
        {
            var process = GetCurrentProcess();
            if (GetProcessTimes(process, out _, out _, out long kernel, out long user)) s.ProcessCpu = (kernel + user) / 1e7;
            if (GetThreadTimes(GetCurrentThread(), out _, out _, out long threadKernel, out long threadUser)) s.ThreadCpu = (threadKernel + threadUser) / 1e7;
            var counters = new ProcessMemoryCounters { cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            if (K32GetProcessMemoryInfo(process, ref counters, counters.cb)) s.Faults = counters.PageFaultCount;
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                s.RamAvailable = (long)status.ullAvailPhys;
                s.RamTotal = (long)status.ullTotalPhys;
                s.CommitLimit = (long)status.ullTotalPageFile;
                s.Committed = (long)(status.ullTotalPageFile - status.ullAvailPageFile);
            }
            if (!pageReadsFailed)
            {
                try
                {
                    s.HardFaults = SystemPageReads();
                    s.HardFaultsSystemWide = true;
                }
                catch (Exception)
                {
                    pageReadsFailed = true;
                }
            }
            if (GetSystemPowerStatus(out var power)) s.Power = WindowsPower(power.ACLineStatus, power.BatteryFlag, power.BatteryLifePercent, power.SystemStatusFlag);
            s.Plan = WindowsPlan();
            if (!clockFailed)
            {
                try
                {
                    s.Clock = WindowsClock();
                }
                catch (Exception)
                {
                    clockFailed = true;
                }
            }
        }

        /// GetSystemPowerStatus's fields as words: null without a system battery (a desktop).
        internal static string WindowsPower(byte acLine, byte batteryFlag, byte percent, byte statusFlag)
        {
            bool noBattery = (batteryFlag & 128) != 0 || batteryFlag == 255;
            string source = acLine == 1 ? "on AC" : acLine == 0 ? "on battery" : "power source unknown";
            if (noBattery) return acLine == 0 ? source : null;
            var text = source + (percent <= 100 ? $", battery {percent}%" : "");
            return text + ((statusFlag & 1) != 0 ? ", battery saver on" : ", battery saver off");
        }

        /// The active power plan's name, and the power mode (Windows 10 and 11's slider) when it
        /// is one of the known ones.
        static string WindowsPlan()
        {
            string plan = null, mode = null;
            if (!planFailed)
            {
                try
                {
                    if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr scheme) == 0)
                    {
                        try
                        {
                            uint size = 0;
                            if (PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) == 0 && size > 0 && size < 4096)
                            {
                                var buffer = new byte[size];
                                if (PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0)
                                    plan = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
                            }
                        }
                        finally
                        {
                            LocalFree(scheme);
                        }
                    }
                }
                catch (Exception)
                {
                    planFailed = true;
                }
            }
            if (!overlayFailed)
            {
                try
                {
                    if (PowerGetEffectiveOverlayScheme(out Guid overlay) == 0) mode = PowerMode(overlay);
                }
                catch (Exception)
                {
                    overlayFailed = true;
                }
            }
            if (plan == null && mode == null) return null;
            return (plan != null ? "power plan " + plan : "power plan unknown") + (mode != null ? ", power mode " + mode : "");
        }

        /// The power mode overlays' names (Windows 11's: Settings > System > Power).
        internal static string PowerMode(Guid overlay) => overlay.ToString() switch
        {
            "961cc777-2547-4f9d-8174-7d86181b8a7a" => "best power efficiency",
            "00000000-0000-0000-0000-000000000000" => "balanced",
            "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => "better performance",
            "ded574b5-45a0-4f42-8737-46345c09c238" => "best performance",
            var other => other,
        };

        /// The CPUs' mean current clock against their mean rated one and the mean limit
        /// (CallNtPowerInformation's ProcessorInformation; Windows may report the rated clock as
        /// current on some CPUs).
        static string WindowsClock()
        {
            int n = Environment.ProcessorCount;
            var info = new ProcessorPowerInformation[n];
            int size = n * Marshal.SizeOf<ProcessorPowerInformation>();
            if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, info, (uint)size) != 0) return null;
            double current = 0, max = 0, limit = 0;
            foreach (var p in info)
            {
                current += p.CurrentMhz;
                max += p.MaxMhz;
                limit += p.MhzLimit;
            }
            var c = CultureInfo.InvariantCulture;
            return string.Format(c, "CPU clock {0:F2} GHz (mean of {1}), rated {2:F2}, limit {3:F2}", current / n / 1e3, n, max / n / 1e3, limit / n / 1e3);
        }

        /// The system's reads from disk for hard page faults so far (NtQuerySystemInformation's
        /// SystemPerformanceInformation, PageReadIoCount: perfmon's Memory, Page Reads).
        static unsafe long SystemPageReads()
        {
            // SYSTEM_PERFORMANCE_INFORMATION: IdleProcessTime and three transfer counts (8 bytes
            // each), then 32-bit counts: three operation counts, AvailablePages, CommittedPages,
            // CommitLimit, PeakCommitment, PageFaultCount, CopyOnWriteCount, TransitionCount,
            // CacheTransitionCount, DemandZeroCount, PageReadCount, PageReadIoCount (at 84).
            const int PageReadIoCount = 32 + 13 * 4, Size = 1024;
            var buffer = stackalloc byte[Size];
            int status = NtQuerySystemInformation(SystemPerformanceInformation, buffer, Size, out uint length);
            if (status != 0 || length < PageReadIoCount + 4) throw new InvalidOperationException($"NtQuerySystemInformation: 0x{status:X8}, {length} bytes");
            return *(uint*)(buffer + PageReadIoCount);
        }

        const int SystemPerformanceInformation = 2, ProcessorInformation = 11;

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessMemoryCounters
        {
            public uint cb, PageFaultCount;
            public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SystemPowerStatus
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public uint BatteryLifeTime, BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessorPowerInformation
        {
            public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
        }

        [DllImport("kernel32")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32")] static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32")] static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32")] static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
        [DllImport("kernel32")] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
        [DllImport("kernel32")] static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
        [DllImport("kernel32")] static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("powrprof")] static extern uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr scheme);
        [DllImport("powrprof")] static extern uint PowerReadFriendlyName(IntPtr rootKey, IntPtr scheme, IntPtr subGroup, IntPtr setting, byte[] buffer, ref uint size);
        [DllImport("powrprof")] static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
        [DllImport("powrprof")] static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputSize, [Out] ProcessorPowerInformation[] output, uint outputSize);
        [DllImport("ntdll")] static extern unsafe int NtQuerySystemInformation(int infoClass, byte* info, uint size, out uint returned);
    }
}
