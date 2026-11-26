using System;
using System.IO;
using OniMoono.Desktop.Vulkan;
using Xunit;

namespace OniMoono.Tests
{
    /// The Vulkan start-up guard (a start that never got its first frames on screen keeps auto
    /// on OpenGL for that GPU, driver and build) and the driver deny list.
    public class VkStartGuardTests
    {
        static readonly VkGpuIdentity Rtx = new(0x10DE, 0x2204, 0x98C14000, 4, "NVIDIA", "NVIDIA GeForce RTX 3090");

        [Fact]
        public void AnOldCppRuntimeKeepsVulkanFromStarting()
        {
            // #126: VMA's first mutex lock faults in MSVCP140.dll 14.36; 14.40 has the constexpr mutex.
            Assert.StartsWith("the Microsoft Visual C++ runtime is too old", VkContext.CppRuntimeReason(14, 36, 32532, 0));
            Assert.Contains("MSVCP140.dll 14.36.32532.0", VkContext.CppRuntimeReason(14, 36, 32532, 0));
            Assert.NotNull(VkContext.CppRuntimeReason(14, 39, 33519, 0));
            Assert.Null(VkContext.CppRuntimeReason(14, 40, 33810, 0));
            Assert.Null(VkContext.CppRuntimeReason(14, 44, 35211, 0));
        }

        [Fact]
        public void TheRuntimeCheckReadsTheImportTable()
        {
            // #126: only a vma.dll that imports MSVCP140.dll is checked; a managed assembly
            // imports mscoree.dll alone.
            string self = typeof(VkStartGuardTests).Assembly.Location;
            Assert.True(VkContext.ImportsLibrary(self, "MSCOREE.dll"));
            Assert.False(VkContext.ImportsLibrary(self, "msvcp140.dll"));
            string text = Path.GetTempFileName();
            try
            {
                File.WriteAllText(text, "not a PE file");
                Assert.True(VkContext.ImportsLibrary(text, "msvcp140.dll"));
            }
            finally
            {
                File.Delete(text);
            }
        }

        [Fact]
        public void TheKeyNamesGpuDriverAndBuild()
        {
            Assert.Equal("gpu 10de:2204 driver 98c14000 (NVIDIA) build 1.0+abc", VkStartGuard.KeyOf(Rtx, "1.0+abc"));
            Assert.NotEqual(VkStartGuard.KeyOf(Rtx, "1.0+abc"), VkStartGuard.KeyOf(Rtx with { DriverVersion = 0x98C18000 }, "1.0+abc"));
            Assert.NotEqual(VkStartGuard.KeyOf(Rtx, "1.0+abc"), VkStartGuard.KeyOf(Rtx with { DeviceId = 0x2206 }, "1.0+abc"));
        }

        [Fact]
        public void AStartThatNeverClearedKeepsAutoOnOpenGL()
        {
            string key = VkStartGuard.KeyOf(Rtx, "b1"), other = VkStartGuard.KeyOf(Rtx, "b2");
            // First start: recorded.
            var (refusal, lines) = VkStartGuard.Enter([], key, honour: true);
            Assert.Null(refusal);
            Assert.Equal(["starting\t" + key], lines);
            // It crashed (the line is still there): auto refuses, and the crash is kept.
            (refusal, lines) = VkStartGuard.Enter(lines, key, honour: true);
            Assert.StartsWith("Vulkan did not finish starting", refusal);
            Assert.Equal(["crashed\t" + key], lines);
            (refusal, lines) = VkStartGuard.Enter(lines, key, honour: true);
            Assert.NotNull(refusal);
            // Another build tries Vulkan, the earlier crash still recorded.
            (refusal, var tried) = VkStartGuard.Enter(lines, other, honour: true);
            Assert.Null(refusal);
            Assert.Equal(["crashed\t" + key, "starting\t" + other], tried);
            // --renderer vulkan tries, and its clean start clears the key.
            (refusal, lines) = VkStartGuard.Enter(lines, key, honour: false);
            Assert.Null(refusal);
            Assert.Empty(VkStartGuard.Cleared(lines, key));
        }

        /// #114: a run that ran out of video memory, crashed or not, keeps auto on OpenGL.
        [Fact]
        public void ARunOutOfVideoMemoryKeepsAutoOnOpenGL()
        {
            string key = VkStartGuard.KeyOf(Rtx, "b1"), other = VkStartGuard.KeyOf(Rtx, "b2");
            var (refusal, lines) = VkStartGuard.Enter([], key, honour: true);
            lines = VkStartGuard.WithOutOfMemory(lines, key);
            Assert.Equal(lines, VkStartGuard.WithOutOfMemory(lines, key));
            // Its first frames got on screen after it ran out: the mark stays.
            lines = VkStartGuard.Cleared(lines, key, keepOutOfMemory: true);
            Assert.Equal(["out of memory\t" + key], lines);
            (refusal, lines) = VkStartGuard.Enter(lines, key, honour: true);
            Assert.StartsWith("Vulkan ran out of video memory", refusal);
            Assert.Equal(["out of memory\t" + key], lines);
            // Another build tries Vulkan; --renderer vulkan tries, and a clean start clears it.
            (refusal, _) = VkStartGuard.Enter(lines, other, honour: true);
            Assert.Null(refusal);
            (refusal, lines) = VkStartGuard.Enter(lines, key, honour: false);
            Assert.Null(refusal);
            Assert.Empty(VkStartGuard.Cleared(lines, key));
        }

        [Fact]
        public void TheGuardFileKeepsARunOutOfVideoMemory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "oni-guard-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = VkStartGuard.FileIn(dir);
                var guard = new VkStartGuard(path, VkStartGuard.KeyOf(Rtx, "b1"));
                Assert.Null(guard.Enter(honour: true));
                guard.RanOutOfMemory();
                guard.Clear();
                Assert.StartsWith("Vulkan ran out of video memory", new VkStartGuard(path, guard.Key).Enter(honour: true));
                // Cleared first, then out of memory: the same.
                File.Delete(path);
                guard = new VkStartGuard(path, guard.Key);
                Assert.Null(guard.Enter(honour: true));
                guard.Clear();
                guard.RanOutOfMemory();
                Assert.NotNull(new VkStartGuard(path, guard.Key).Enter(honour: true));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void TheGuardFileFollowsAStart()
        {
            var dir = Path.Combine(Path.GetTempPath(), "oni-guard-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = VkStartGuard.FileIn(dir);
                var guard = new VkStartGuard(path, VkStartGuard.KeyOf(Rtx, "b1"));
                Assert.Null(guard.Enter(honour: true));
                Assert.True(File.Exists(path));
                guard.Clear();
                Assert.False(File.Exists(path));
                // A start that fell back cleanly (Vulkan unavailable) is no crash.
                Assert.Null(guard.Enter(honour: true));
                guard.Abandon();
                Assert.Null(new VkStartGuard(path, guard.Key).Enter(honour: true));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        static uint Intel(int major, int build) => (uint)(major << 14 | build);

        [Fact]
        public void IntelsWindowsVersionDecodes()
        {
            // 31.0.101.2135 and 31.0.101.5333 as the driver reports them.
            Assert.Equal((101, 2135), VkDenyList.IntelWindowsVersion(1656919));
            Assert.Equal((101, 5333), VkDenyList.IntelWindowsVersion(1660117));
            Assert.Equal(1656919u, Intel(101, 2135));
        }

        [Fact]
        public void TheDenyList()
        {
            var arc = new VkGpuIdentity(0x8086, 0x56A0, Intel(101, 2135), VkDenyList.IntelWindows, "Intel Corporation", "Intel(R) Arc(TM) A770 Graphics");
            Assert.Contains("101.2135, older than 101.5333", VkDenyList.Reason(arc));
            Assert.Null(VkDenyList.Reason(arc with { DriverVersion = Intel(101, 5333) }));
            Assert.Null(VkDenyList.Reason(arc with { DriverVersion = Intel(101, 6130) }));
            Assert.NotNull(VkDenyList.Reason(arc with { DriverVersion = Intel(100, 9466) }));
            // Intel's Linux driver (Mesa's ANV, VK_DRIVER_ID_INTEL_OPEN_SOURCE_MESA) encodes its
            // version otherwise: not denied.
            Assert.Null(VkDenyList.Reason(arc with { DriverId = 6 }));
            var dozen = new VkGpuIdentity(0x10DE, 0x2204, 0, VkDenyList.MesaDozen, "Dozen", "Microsoft Direct3D12 (NVIDIA GeForce RTX 3090)");
            Assert.Contains("Dozen", VkDenyList.Reason(dozen));
            Assert.Null(VkDenyList.Reason(new VkGpuIdentity(0x10DE, 0x2204, 0x98C14000, 4, "NVIDIA", "RTX")));
        }
    }
}
