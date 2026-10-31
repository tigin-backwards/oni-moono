using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Engine;

namespace OniMoono.Desktop.Vulkan
{
    /// The GPU and driver a Vulkan start would use (VkContext, read before the device is made).
    /// `DriverVersion` is the driver's own encoding of its version; `DriverId` is VkDriverId.
    internal readonly record struct VkGpuIdentity(uint VendorId, uint DeviceId, uint DriverVersion, int DriverId, string DriverName, string DeviceName);

    /// Start-up crash guard, as Firefox's DriverCrashGuard (ARCHITECTURE §3.5, *Starting*):
    /// before the Vulkan device is made, a line `starting KEY` is written to
    /// `vulkan-start-guard.txt` in the player's cache folder, KEY naming the GPU (vendor and
    /// device ids), the driver's version and Moono's build; once the first FramesToClear frames
    /// are presented (first pipelines and presents behind it), or the game ends cleanly before
    /// that, the key's lines go. A `starting` line found at the next launch means that start
    /// never got there (a crash, a hang the user ended): it becomes `crashed KEY`, and under
    /// auto every start with that key uses OpenGL. Another GPU, driver or build tries Vulkan
    /// again; `--renderer vulkan` always tries, and its clean start clears the key.
    internal sealed class VkStartGuard
    {
        public const int FramesToClear = 60;
        const string Starting = "starting\t", Crashed = "crashed\t";

        readonly string path;
        public readonly string Key;

        public VkStartGuard(string path, string key)
        {
            this.path = path;
            Key = key;
        }

        public static string FileIn(string cacheDirectory) => Path.Combine(cacheDirectory, "vulkan-start-guard.txt");

        public static string KeyOf(VkGpuIdentity id, string build)
            => $"gpu {id.VendorId:x4}:{id.DeviceId:x4} driver {id.DriverVersion:x8} ({id.DriverName}) build {build}";

        /// The guard's verdict on a start with `key`, from the file's lines: OpenGL's reason (when
        /// `honour`, under auto, and that key crashed before), and the lines to write: every
        /// unfinished start becomes a crash, and this start, unless refused, is `starting`.
        public static (string Refusal, List<string> Lines) Enter(IReadOnlyList<string> lines, string key, bool honour)
        {
            var crashed = new List<string>();
            foreach (var line in lines)
            {
                string k = line.StartsWith(Starting, StringComparison.Ordinal) ? line[Starting.Length..]
                    : line.StartsWith(Crashed, StringComparison.Ordinal) ? line[Crashed.Length..] : null;
                if (k != null && !crashed.Contains(k)) crashed.Add(k);
            }
            var kept = crashed.Select(k => Crashed + k).ToList();
            if (honour && crashed.Contains(key))
                return ($"Vulkan did not finish starting in an earlier run with this GPU, driver and Moono build ({key}); OpenGL is used until one of them changes (--renderer vulkan tries Vulkan again)", kept);
            kept.Add(Starting + key);
            return (null, kept);
        }

        /// The lines without `key`'s (a start that got its first frames on screen).
        public static List<string> Cleared(IReadOnlyList<string> lines, string key)
            => lines.Where(l => l != Starting + key && l != Crashed + key && l.Length > 0).ToList();

        /// Enter, on the file: the refusal, or null with this start recorded.
        public string Enter(bool honour)
        {
            var (refusal, lines) = Enter(Read(), Key, honour);
            Write(lines);
            return refusal;
        }

        /// The start ended before the device existed, cleanly (Vulkan unavailable, OpenGL used):
        /// not a crash, so its `starting` line goes; earlier crashes stay.
        public void Abandon() => Write(Read().Where(l => l != Starting + Key).ToList());

        /// The start got its first frames on screen (or ended cleanly before): the key's lines go.
        public void Clear()
        {
            if (cleared) return;
            cleared = true;
            Write(Cleared(Read(), Key));
        }

        bool cleared;

        List<string> Read()
        {
            try
            {
                return File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                EngineLog.Engine($"vulkan: start guard {path}: {e.Message}");
                return new List<string>();
            }
        }

        void Write(List<string> lines)
        {
            try
            {
                if (lines.Count == 0)
                {
                    File.Delete(path);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path, lines);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                EngineLog.Engine($"vulkan: start guard {path}: {e.Message}");
            }
        }
    }

    /// Drivers auto does not start Vulkan on (`--renderer vulkan` still does).
    internal static class VkDenyList
    {
        /// VkDriverId of Mesa's Dozen (Vulkan over Direct3D 12) and of Intel's Windows driver
        /// (VK_DRIVER_ID_INTEL_PROPRIETARY_WINDOWS; vk.xml). The 1.3 devices this needs all report
        /// one (VkPhysicalDeviceDriverProperties is core since 1.2).
        public const int MesaDozen = 23, IntelWindows = 5;

        /// Why auto refuses the driver, or null. Intel's Windows drivers before 101.5333 (the
        /// threshold Unreal's DriverDenyList uses for its Vulkan and D3D12 problems), known by
        /// their driver ID, which is what the version encoding below belongs to, and Dozen, whose
        /// Vulkan is a translation layer.
        public static string Reason(VkGpuIdentity id)
        {
            if (id.DriverId == MesaDozen) return "Mesa's Dozen driver (Vulkan over Direct3D 12)";
            if (id.DriverId == IntelWindows)
            {
                var (major, build) = IntelWindowsVersion(id.DriverVersion);
                if (major < 101 || major == 101 && build < 5333) return $"Intel's driver {major}.{build}, older than 101.5333";
            }
            return null;
        }

        /// Intel's Windows Vulkan driver version, as vulkan.gpuinfo.org decodes it: the last two
        /// fields of the driver's version (31.0.101.5333 is 101.5333) as `major << 14 | build`.
        public static (int Major, int Build) IntelWindowsVersion(uint driverVersion) => ((int)(driverVersion >> 14), (int)(driverVersion & 0x3FFF));
    }
}
