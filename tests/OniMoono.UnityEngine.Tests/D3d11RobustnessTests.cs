using System;
using System.IO;
using OniMoono.Desktop;
using OniMoono.Desktop.D3d11;
using OniMoono.Desktop.Vulkan;
using Xunit;

namespace OniMoono.Tests
{
    /// The Direct3D 11 start-up guard (VkStartGuard with GuardNames.D3d11: a start that never got
    /// its first frames on screen keeps auto off Direct3D 11 for that GPU, driver and build) and
    /// the fault hooks' names (ONI_D3D11_FAIL).
    public class D3d11RobustnessTests
    {
        static readonly D3d11GpuIdentity Rtx = new(0x10DE, 0x2204, "32.0.16.1088", "NVIDIA GeForce RTX 3090", false);

        [Fact]
        public void TheKeyNamesGpuDriverAndBuild()
        {
            Assert.Equal("gpu 10de:2204 driver 32.0.16.1088 build b1", Renderers.D3d11GuardKey(Rtx, "b1"));
            Assert.NotEqual(Renderers.D3d11GuardKey(Rtx, "b1"), Renderers.D3d11GuardKey(Rtx with { Driver = "32.0.16.1089" }, "b1"));
            Assert.NotEqual(Renderers.D3d11GuardKey(Rtx, "b1"), Renderers.D3d11GuardKey(Rtx with { DeviceId = 0x2206 }, "b1"));
        }

        [Fact]
        public void AStartThatNeverClearedKeepsAutoOffDirect3D11()
        {
            string key = Renderers.D3d11GuardKey(Rtx, "b1"), other = Renderers.D3d11GuardKey(Rtx, "b2");
            var names = GuardNames.D3d11;
            var (refusal, lines) = VkStartGuard.Enter([], key, honour: true, names);
            Assert.Null(refusal);
            Assert.Equal(["starting\t" + key], lines);
            (refusal, lines) = VkStartGuard.Enter(lines, key, honour: true, names);
            Assert.Equal($"Direct3D 11 did not finish starting in an earlier run with this GPU, driver and Moono build ({key}); the next renderer is used "
                         + "until one of them changes (--renderer d3d11 tries Direct3D 11 again)", refusal);
            Assert.Equal(["crashed\t" + key], lines);
            (refusal, _) = VkStartGuard.Enter(lines, other, honour: true, names);
            Assert.Null(refusal);
            // --renderer d3d11 (not auto) tries, and its clean start clears the key.
            (refusal, lines) = VkStartGuard.Enter(lines, key, honour: false, names);
            Assert.Null(refusal);
            Assert.Empty(VkStartGuard.Cleared(lines, key));
        }

        [Fact]
        public void TheGuardFileIsDirect3D11sOwn()
        {
            var dir = Path.Combine(Path.GetTempPath(), "oni-guard-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = VkStartGuard.FileIn(dir, GuardNames.D3d11);
                Assert.Equal("d3d11-start-guard.txt", Path.GetFileName(path));
                Assert.NotEqual(VkStartGuard.FileIn(dir), path);
                var guard = new VkStartGuard(path, Renderers.D3d11GuardKey(Rtx, "b1"), GuardNames.D3d11);
                Assert.Null(guard.Enter(honour: true));
                // A crash: the next start under auto is refused, Vulkan's file untouched.
                Assert.StartsWith("Direct3D 11 did not finish", new VkStartGuard(path, guard.Key, GuardNames.D3d11).Enter(honour: true));
                Assert.False(File.Exists(VkStartGuard.FileIn(dir)));
                var tried = new VkStartGuard(path, guard.Key, GuardNames.D3d11);
                Assert.Null(tried.Enter(honour: false));
                tried.Clear();
                Assert.False(File.Exists(path));
                // A clean fallback (the device could not start) leaves no line.
                Assert.Null(guard.Enter(honour: true));
                guard.Abandon();
                Assert.False(File.Exists(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void FaultNamesAreChecked()
        {
            Assert.Empty(D3d11Fault.Unknown("factory,adapter,device,debug,swapchain,presenter,screen,exclusive-refused"));
            Assert.Empty(D3d11Fault.Unknown("removed-after:1, mode-changed-after:300,enter-hold:2000,crash-at:30,guard-build:x"));
            Assert.Empty(D3d11Fault.Unknown(null));
            Assert.Equal(["swapchian", "removed-after:0", "crash-at:x", "guard-build", "device:3"],
                D3d11Fault.Unknown("swapchian,removed-after:0,crash-at:x,guard-build,device:3"));
        }
    }
}
