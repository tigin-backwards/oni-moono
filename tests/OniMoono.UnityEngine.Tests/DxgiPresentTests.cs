using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// Windows' DXGI presentation (src/OniMoono.Desktop/Gl/DxgiTarget.cs, Dxgi.cs): the parts that
    /// run anywhere. The COM calls themselves are measured on Windows (ARCHITECTURE §3.5,
    /// "Presentation on Windows").
    public class DxgiPresentTests
    {
        static readonly (uint, string, bool)[] Adapters =
        [
            (0x10DE, "NVIDIA GeForce RTX 3090", false),
            (0x1002, "AMD Radeon(TM) Graphics", false),
            (0x1414, "Microsoft Basic Render Driver", true),
        ];

        [Fact]
        public void TheAdapterIsTheOneTheGlRendererNames()
        {
            Assert.Equal(0, Dxgi.MatchAdapter("NVIDIA Corporation", "NVIDIA GeForce RTX 3090/PCIe/SSE2", Adapters));
            Assert.Equal(1, Dxgi.MatchAdapter("ATI Technologies Inc.", "AMD Radeon(TM) Graphics", Adapters));
            // The only adapter of the vendor, whatever its name.
            Assert.Equal(1, Dxgi.MatchAdapter("ATI Technologies Inc.", "AMD Radeon 780M", Adapters));
        }

        [Fact]
        public void NoAdapterWhenNothingDecides()
        {
            // Microsoft's GDI renderer (no driver), an unknown vendor, a vendor with no adapter.
            Assert.Equal(-1, Dxgi.MatchAdapter("Microsoft Corporation", "GDI Generic", Adapters));
            Assert.Equal(-1, Dxgi.MatchAdapter("Mesa", "llvmpipe", Adapters));
            Assert.Equal(-1, Dxgi.MatchAdapter("Intel", "Intel(R) UHD Graphics 630", Adapters));
            // Two of the vendor and no name match: no guess.
            (uint, string, bool)[] twoNvidia = [(0x10DE, "NVIDIA GeForce RTX 3090", false), (0x10DE, "NVIDIA GeForce RTX 4090", false)];
            Assert.Equal(-1, Dxgi.MatchAdapter("NVIDIA Corporation", "NVIDIA GeForce GTX 1080/PCIe/SSE2", twoNvidia));
            Assert.Equal(1, Dxgi.MatchAdapter("NVIDIA Corporation", "NVIDIA GeForce RTX 4090/PCIe/SSE2", twoNvidia));
            // A software adapter never matches.
            Assert.Equal(-1, Dxgi.MatchAdapter("NVIDIA Corporation", "x", [(0x10DE, "NVIDIA", true)]));
        }

        [Theory]
        [InlineData("NVIDIA Corporation", 0x10DEu)]
        [InlineData("ATI Technologies Inc.", 0x1002u)]
        [InlineData("AMD", 0x1002u)]
        [InlineData("Intel", 0x8086u)]
        [InlineData("Microsoft Corporation", 0u)]
        public void VendorsByGlVendor(string glVendor, uint id) => Assert.Equal(id, Dxgi.VendorOf(glVendor));

        [Fact]
        public void TheSharedTextureGrowsWithHeadroomOnly()
        {
            Assert.Equal(1280, Dxgi.Capacity(1280, 1280));
            Assert.Equal(1280, Dxgi.Capacity(1280, 800));
            // 1281 * 5 / 4 = 1601, rounded up to 64: 1664.
            Assert.Equal(1664, Dxgi.Capacity(1280, 1281));
            Assert.Equal(3200, Dxgi.Capacity(1280, 2560));
            Assert.Equal(16384, Dxgi.Capacity(1280, 16000));
            Assert.Equal(16384, Dxgi.Capacity(16384, 16384));
        }

        [Fact]
        public void LostDevicesAreTold()
        {
            Assert.True(Dxgi.DeviceLost(Dxgi.ErrorDeviceRemoved));
            Assert.True(Dxgi.DeviceLost(Dxgi.ErrorDeviceReset));
            Assert.True(Dxgi.DeviceLost(Dxgi.ErrorDeviceHung));
            Assert.True(Dxgi.DeviceLost(Dxgi.ErrorDriverInternal));
            Assert.False(Dxgi.DeviceLost(Dxgi.StatusOccluded));
            Assert.False(Dxgi.Failed(Dxgi.StatusOccluded));
            Assert.True(Dxgi.Failed(Dxgi.ErrorDeviceRemoved));
            Assert.Equal("DXGI_ERROR_DEVICE_REMOVED", Dxgi.Hr(Dxgi.ErrorDeviceRemoved));
            Assert.Equal("0x80070005", Dxgi.Hr(unchecked((int)0x80070005)));
        }

        /// The layouts the vtable calls hand to DXGI and Direct3D: the Windows SDK's sizes and
        /// offsets (checked with offsetof against the 10.0.26100 headers).
        [Fact]
        public void StructsHaveTheSdkLayouts()
        {
            Assert.Equal(48, Marshal.SizeOf<Dxgi.SwapChainDesc1>());
            Assert.Equal(28, Marshal.OffsetOf<Dxgi.SwapChainDesc1>(nameof(Dxgi.SwapChainDesc1.BufferCount)).ToInt32());
            Assert.Equal(44, Marshal.OffsetOf<Dxgi.SwapChainDesc1>(nameof(Dxgi.SwapChainDesc1.Flags)).ToInt32());
            Assert.Equal(44, Marshal.SizeOf<Dxgi.Texture2DDesc>());
            Assert.Equal(40, Marshal.OffsetOf<Dxgi.Texture2DDesc>(nameof(Dxgi.Texture2DDesc.MiscFlags)).ToInt32());
            Assert.Equal(24, Marshal.SizeOf<Dxgi.Box>());
            Assert.Equal(312, Marshal.SizeOf<Dxgi.AdapterDesc1>());
            Assert.Equal(32, Marshal.SizeOf<Dxgi.VideoMemoryInfo>());
        }

        [Fact]
        public void TheAdapterDescriptionIsReadUpToItsNul()
        {
            var desc = new Dxgi.AdapterDesc1();
            unsafe
            {
                var name = "NVIDIA GeForce RTX 3090";
                for (int i = 0; i < name.Length; i++) desc.Description[i] = name[i];
            }
            Assert.Equal("NVIDIA GeForce RTX 3090", desc.Name);
        }

        /// The splash's redraws: every 16 ms after the last picture, counting from when it was
        /// issued, so a present that waits for the vertical blank does not add its wait to that.
        [Fact]
        public void TheSplashWaitsWhatIsLeftOf16MsWhenPresentsArePaced()
        {
            Assert.Equal(16, PresenterTimings.SplashWait(pacedByPresent: false));
            PresenterTimings.Shown(waited: System.Diagnostics.Stopwatch.Frequency / 50); // 20 ms ago
            Assert.Equal(1, PresenterTimings.SplashWait(pacedByPresent: true));
            Assert.Equal(16, PresenterTimings.SplashWait(pacedByPresent: false));
            PresenterTimings.Shown(waited: 0);
            Assert.InRange(PresenterTimings.SplashWait(pacedByPresent: true), 10, 16);
        }

        /// Restarts after DXGI fails: ten tries a burst, 1, 2, 4 then 8 s apart (about a minute,
        /// outlasting a slow driver reset); a restart that presents for 60 s ends the burst.
        [Fact]
        public void RestartsComeInBurstsThatEndAfterAMinuteOfPresenting()
        {
            long s = System.Diagnostics.Stopwatch.Frequency, now = 1000 * s;
            var r = new DxgiRestarts();
            Assert.True(r.MayTry(now));
            double total = 0;
            for (int i = 1; i <= DxgiRestarts.MaxPerBurst; i++)
            {
                Assert.True(r.MayTry(now));
                r.Tried(now);
                Assert.False(r.MayTry(now + s / 2));
                total += DxgiRestarts.DelaySeconds(i);
                now += (long)(DxgiRestarts.DelaySeconds(i) * s);
            }
            Assert.Equal([1.0, 2, 4, 8, 8], new[] { 1, 2, 3, 4, 9 }.Select(DxgiRestarts.DelaySeconds));
            Assert.InRange(total - DxgiRestarts.DelaySeconds(10), 50, 60); // the span the ten tries cover
            Assert.True(r.Exhausted);
            Assert.False(r.MayTry(now + 3600 * s));
            // Presenting does not end a burst whose last try failed.
            r.Presented(now + 3600 * s);
            Assert.True(r.Exhausted);

            r = new DxgiRestarts();
            r.Tried(now);
            r.Started(now);
            r.Presented(now + 30 * s);
            Assert.Equal(1, r.Tries);
            r.Presented(now + 61 * s);
            Assert.Equal(0, r.Tries);
            Assert.True(r.MayTry(now + 61 * s));
        }

        /// Vulkan's own path, minimized with vsync: pictures a period apart from when the last was
        /// due, so a late wake-up shortens the next wait; one more than a period late starts over.
        [Fact]
        public void MinimizedPicturesArePacedAPeriodApart()
        {
            long due = 0;
            Assert.Equal(0, OniMoono.Desktop.Vulkan.VkPresenter.MinimizedWait(ref due, 1000, 16));
            Assert.Equal(1016, due);
            Assert.Equal(16, OniMoono.Desktop.Vulkan.VkPresenter.MinimizedWait(ref due, 1000, 16));
            // Woke at 1020, 4 late: the next wait is 4 shorter.
            Assert.Equal(12, OniMoono.Desktop.Vulkan.VkPresenter.MinimizedWait(ref due, 1020, 16));
            // Restored and minimized again much later: no catching up.
            Assert.Equal(0, OniMoono.Desktop.Vulkan.VkPresenter.MinimizedWait(ref due, 5000, 16));
            Assert.Equal(5016, due);
        }

        /// vSyncCount 2: a frame is shown twice, each minimized picture one refresh, so a frame
        /// takes two refreshes (30 fps), not four.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public void AMinimizedFrameTakesItsIntervalInRefreshes(int interval)
        {
            long f = System.Diagnostics.Stopwatch.Frequency, refresh = f / 60;
            Assert.Equal(0, OniMoono.Desktop.Vulkan.VkPresenter.MinimizedPeriod(0));
            long due = 0, now = 10 * f, start = now;
            for (int frame = 0; frame < 30; frame++)
                for (int shown = 0; shown < interval; shown++) // VkPresenter's Show loop
                    now += OniMoono.Desktop.Vulkan.VkPresenter.MinimizedWait(ref due, now, OniMoono.Desktop.Vulkan.VkPresenter.MinimizedPeriod(interval));
            Assert.Equal(30 * interval - 1, (now - start) / refresh);
        }

        /// Frame stats' time from a game frame's start to its picture's present: counted for the
        /// picture of a game frame (FrameStarted set), up to when it was issued, not for redraws.
        [Fact]
        public void FrameStartToPresentCountsGameFramesUpToTheirIssue()
        {
            bool was = OniMoono.Engine.PlayerLoop.FrameStats;
            OniMoono.Engine.PlayerLoop.FrameStats = true;
            try
            {
                PresenterTimings.Take();
                long f = System.Diagnostics.Stopwatch.Frequency, now = System.Diagnostics.Stopwatch.GetTimestamp();
                PresenterTimings.FrameStarted = now - f * 40 / 1000; // began 40 ms ago
                PresenterTimings.Shown(waited: f * 10 / 1000);         // issued 10 ms ago
                Assert.Equal(0, PresenterTimings.FrameStarted);
                PresenterTimings.Shown(waited: 0);                     // a redraw: not counted
                var line = PresenterTimings.Take();
                var m = System.Text.RegularExpressions.Regex.Match(line, @"frame start to present mean ([0-9.]+)");
                Assert.True(m.Success, line);
                Assert.InRange(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 29.5, 35);
            }
            finally { OniMoono.Engine.PlayerLoop.FrameStats = was; }
        }

        /// --dxgi-fault takes this build's (OpenGL's) faults only: the shared DxgiSwapChain also
        /// lists the Vulkan branch's, which would do nothing here.
        [Fact]
        public void EveryFaultThisBuildHasIsAccepted()
        {
            foreach (var ok in new[] { "extension", "adapter", "device", "register", "swapchain", "association", "norestart",
                                       "import", "lock:1", "resize:3", "removed:300", "importview:2",
                                       "removed:300,norestart", "association,norestart" })
                Assert.Null(OniMoono.Host.DxgiFaultOption.Unknown(ok));
            Assert.Equal("bogus", OniMoono.Host.DxgiFaultOption.Unknown("removed:3,bogus"));
            foreach (var bad in new[] { "", "lock", "lock:", "lock:0", "lock:-1", "lock:x", "removed", "norestart:1", "Removed:3", "import:1" })
                Assert.Equal(bad, OniMoono.Host.DxgiFaultOption.Unknown(bad));
            Assert.Equal("extension, adapter, device, register, swapchain, association, import, norestart, "
                + "lock:N, resize:N, removed:N, importview:N", OniMoono.Host.DxgiFaultOption.Known);
        }

        [Fact]
        public void TheSlowestPercentIsTheMeanOfTheLongestIntervals()
        {
            var values = new double[200];
            for (int i = 0; i < values.Length; i++) values[i] = 16;
            values[7] = 50;
            values[90] = 40;
            var (mean, slowest) = PresenterTimings.MeanAndSlowest(values);
            Assert.Equal((198 * 16 + 90) / 200.0, mean, 9);
            Assert.Equal(45, slowest, 9);
            Assert.Equal((0.0, 0.0), PresenterTimings.MeanAndSlowest([]));
            Assert.Equal((3.0, 3.0), PresenterTimings.MeanAndSlowest([3]));
        }
    }
}
