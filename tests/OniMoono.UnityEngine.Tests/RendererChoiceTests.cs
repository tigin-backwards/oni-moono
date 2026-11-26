using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using OniMoono.Desktop.Gl;
using OniMoono.Desktop.Vulkan;
using OniMoono.Host;
using OniMoono.Host.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The renderer option (settings file, command line) and the Vulkan device's GPU selection.
    public class RendererChoiceTests
    {
        [Fact]
        public void TheTweaksScreenTakesARendererItDoesNotOffer()
        {
            // --renderer d3d11 off Windows (not one of the row's choices there): the row shows
            // locked, at what the settings file keeps, else the default, and never indexes
            // outside its choices.
            var row = System.Linq.Enumerable.First(GameTweakNames.Rows, r => r.Id == GameTweakNames.RendererRow) with
            {
                Choices = OniMoono.Desktop.Renderers.TweakChoices(windows: false),
            };
            var file = PlayerSettings.Parse("""{ "renderer": "opengl" }""");
            var o = EffectiveOptions.Resolve([], [], [], null, false, file, commandLineRenderer: "d3d11");
            var state = TweakRowState.Of(row, o, file);
            Assert.Equal((System.Array.IndexOf(row.Choices!, "opengl"), true), (state.Value, state.Locked));
            Assert.Equal("auto", row.Choices![state.Default]);
            o = EffectiveOptions.Resolve([], [], [], null, false, new PlayerSettings(), commandLineRenderer: "d3d11");
            state = TweakRowState.Of(row, o, new PlayerSettings());
            Assert.Equal(("auto", true), (row.Choices![state.Value], state.Locked));
            // A choice the row offers is shown as before.
            o = EffectiveOptions.Resolve([], [], [], null, false, file, commandLineRenderer: "vulkan");
            Assert.Equal(("vulkan", true), (row.Choices![TweakRowState.Of(row, o, file).Value], TweakRowState.Of(row, o, file).Locked));
        }

        [Fact]
        public void TheSettingsFileAndCommandLineChooseIt()
        {
            var o = EffectiveOptions.Resolve([], [], [], null, false, new PlayerSettings());
            Assert.Equal(("auto", OptionSource.Default), (o.Renderer, o.RendererSource));
            var file = PlayerSettings.Parse("""{ "renderer": "opengl" }""");
            o = EffectiveOptions.Resolve([], [], [], null, false, file);
            Assert.Equal(("opengl", OptionSource.SettingsFile), (o.Renderer, o.RendererSource));
            o = EffectiveOptions.Resolve([], [], [], null, false, file, commandLineRenderer: "vulkan");
            Assert.Equal(("vulkan", OptionSource.CommandLine), (o.Renderer, o.RendererSource));
            Assert.Contains("renderer vulkan (the command line)", o.Describe());
            Assert.Throws<FormatException>(() => PlayerSettings.Parse("""{ "renderer": "metal" }"""));
            o = EffectiveOptions.Resolve([], [], [], null, false, file, commandLineRenderer: "d3d11");
            Assert.Equal(("d3d11", OptionSource.CommandLine), (o.Renderer, o.RendererSource));
            // Saved only when it isn't the default; kept when the screen didn't set it.
            Assert.Equal("{\"renderer\":\"vulkan\"}", EffectiveOptions.Apply(new PlayerSettings(), [], new Dictionary<string, bool>(), null, null, renderer: "vulkan").ToJson(indented: false));
            Assert.Equal("{}", EffectiveOptions.Apply(file, [], new Dictionary<string, bool>(), null, null, renderer: "auto").ToJson(indented: false));
            Assert.Equal("{\"renderer\":\"opengl\"}", EffectiveOptions.Apply(file, [], new Dictionary<string, bool>(), null, null).ToJson(indented: false));
        }

        [Fact]
        public void Direct3D11IsAnOptInOfTheRowOnWindowsOnly()
        {
            // The user's decision (2026-11-25): Direct3D 11 is a choice to opt in to, on Windows.
            Assert.Equal(["auto", "vulkan", "d3d11", "opengl"], OniMoono.Desktop.Renderers.TweakChoices(windows: true));
            Assert.Equal(["auto", "vulkan", "opengl"], OniMoono.Desktop.Renderers.TweakChoices(windows: false));
            var row = System.Linq.Enumerable.First(GameTweakNames.Rows, r => r.Id == GameTweakNames.RendererRow);
            Assert.Equal(OniMoono.Desktop.Renderers.TweakChoices(OperatingSystem.IsWindows()), row.Choices);
            // Its strings are in the catalog on every OS.
            Assert.Contains(TweakStrings.All, t => t.Key == TweakStrings.ChoiceKey(row, "d3d11"));
            // Chosen there, it is saved and read back as --renderer d3d11's choice: Direct3D 11
            // first, then the others as auto (Renderers.Attempts).
            var saved = EffectiveOptions.Apply(new PlayerSettings(), [], new Dictionary<string, bool>(), null, null, renderer: "d3d11");
            Assert.Equal("{\"renderer\":\"d3d11\"}", saved.ToJson(indented: false));
            var o = EffectiveOptions.Resolve([], [], [], null, false, PlayerSettings.Parse(saved.ToJson(indented: false)));
            Assert.Equal(("d3d11", OptionSource.SettingsFile), (o.Renderer, o.RendererSource));
            var windowsRow = row with { Choices = OniMoono.Desktop.Renderers.TweakChoices(windows: true) };
            var state = TweakRowState.Of(windowsRow, o, PlayerSettings.Parse(saved.ToJson(indented: false)));
            Assert.Equal(("d3d11", false), (windowsRow.Choices![state.Value], state.Locked));
            Assert.Equal("d3d11", OniMoono.Desktop.Renderers.Attempts("d3d11", windows: true, integrated: false)[0].Renderer);
        }

        static readonly VkCandidate Discrete = new("RTX", VkPhysicalDeviceType.DiscreteGpu, VkContext.RequiredApi, null);
        static readonly VkCandidate Integrated = new("Radeon", VkPhysicalDeviceType.IntegratedGpu, VkContext.RequiredApi, null);
        static readonly VkCandidate Software = new("llvmpipe", VkPhysicalDeviceType.Cpu, VkContext.RequiredApi, null);
        static readonly VkCandidate Old = new("old", VkPhysicalDeviceType.DiscreteGpu, VkContext.RequiredApi, "Vulkan 1.1.0, 1.3 needed");

        [Fact]
        public void TheGpuOptionPicksByType()
        {
            Assert.Equal(1, VkContext.Choose([Integrated, Discrete], "default", false, false, out _));
            Assert.Equal(1, VkContext.Choose([Integrated, Discrete], "discrete", false, false, out _));
            Assert.Equal(0, VkContext.Choose([Integrated, Discrete], "integrated", false, false, out var reason));
            Assert.Equal("Radeon (integrated, gpu option integrated)", reason);
            // Integrated asked for on a machine with only a discrete GPU: that one.
            Assert.Equal(0, VkContext.Choose([Discrete, Software], "integrated", false, false, out reason));
            Assert.Equal("RTX (discrete; gpu option integrated prefers an integrated GPU, and none is usable)", reason);
            // Default and discrete on a machine with only an integrated GPU: that one.
            Assert.Equal(0, VkContext.Choose([Integrated, Software], "default", false, false, out reason));
            Assert.Equal("Radeon (integrated; gpu option default prefers a discrete GPU, and none is usable)", reason);
            Assert.Equal(1, VkContext.Choose([Software, Integrated], "discrete", true, false, out _));
            // Two of a kind: the first Vulkan lists (the order PRIME variables steer).
            Assert.Equal(1, VkContext.Choose([Integrated, Discrete, Discrete with { Name = "second" }], "discrete", false, false, out _));
        }

        /// Linux: the original sets nothing for "default", so its OpenGL device is the driver's;
        /// Vulkan takes the first hardware GPU the driver lists. The other choices pick by type.
        [Fact]
        public void LinuxDefaultTakesTheDriversOrder()
        {
            Assert.Equal(0, VkContext.Choose([Integrated, Discrete], "default", false, true, out var reason));
            Assert.Equal("Radeon (integrated, gpu option default: the first GPU the driver lists)", reason);
            Assert.Equal(0, VkContext.Choose([Discrete, Integrated], "default", false, true, out _));
            Assert.Equal(1, VkContext.Choose([Software, Integrated], "default", true, true, out _));
            Assert.Equal(1, VkContext.Choose([Integrated, Discrete], "discrete", false, true, out _));
            Assert.Equal(0, VkContext.Choose([Integrated, Discrete], "integrated", false, true, out _));
        }

        [Fact]
        public void AutoRefusesSoftwareAndDevicesMissingRequirements()
        {
            Assert.Equal(-1, VkContext.Choose([Software], "default", false, false, out var reason));
            Assert.Equal("llvmpipe: a software rasterizer (only used with --renderer vulkan)", reason);
            Assert.Equal(0, VkContext.Choose([Software], "default", true, false, out _));
            Assert.Equal(-1, VkContext.Choose([Old], "default", true, false, out reason));
            Assert.Equal("old: Vulkan 1.1.0, 1.3 needed", reason);
            Assert.Equal(1, VkContext.Choose([Old, Software], "discrete", true, false, out _));
            Assert.Equal(-1, VkContext.Choose([], "default", true, false, out reason));
            Assert.Equal("no Vulkan device", reason);
        }

        /// Auto never moves off a discrete GPU whose driver lacks a requirement to a lesser GPU:
        /// OpenGL, which the drivers steer to the discrete GPU as for the original, instead.
        [Fact]
        public void AutoKeepsAnOldDiscreteGpuOnOpenGl()
        {
            Assert.Equal(-1, VkContext.Choose([Integrated, Old], "default", false, false, out var reason));
            Assert.Equal("old (discrete, the GPU gpu option default asks for): Vulkan 1.1.0, 1.3 needed; OpenGL draws on it instead (--renderer vulkan uses Radeon)", reason);
            Assert.Equal(-1, VkContext.Choose([Old, Integrated], "discrete", false, false, out _));
            Assert.Equal(-1, VkContext.Choose([Old, Integrated], "discrete", false, true, out _));
            Assert.Equal(-1, VkContext.Choose([Old], "default", false, false, out reason));
            Assert.Equal("old (discrete, the GPU gpu option default asks for): Vulkan 1.1.0, 1.3 needed; OpenGL draws on it instead", reason);
            // --renderer vulkan and gpu option integrated take the integrated GPU, as before.
            Assert.Equal(0, VkContext.Choose([Integrated, Old], "default", true, false, out _));
            Assert.Equal(0, VkContext.Choose([Integrated, Old], "integrated", false, false, out _));
            // Another discrete GPU that meets them is taken (no lesser GPU).
            Assert.Equal(1, VkContext.Choose([Old, Discrete, Integrated], "default", false, false, out _));
            // Linux, default: only when the discrete GPU is the first listed (OpenGL's GPU);
            // with the integrated GPU first, OpenGL would draw there too.
            Assert.Equal(-1, VkContext.Choose([Old, Integrated], "default", false, true, out _));
            Assert.Equal(0, VkContext.Choose([Integrated, Old], "default", false, true, out _));
        }

        static Dxgi.Adapter A(string name, long luid, uint vendor = 0x10DE, bool software = false, uint device = 0) => new(name, vendor, device, luid, software);

        /// GPUs Windows lists that Vulkan doesn't, by LUID: a log line only.
        [Fact]
        public void GpusWithoutAVulkanDriverAreNamed()
        {
            var vulkan = new[] { Integrated with { Luid = 1 } };
            Assert.Equal(["GTX 680"], VkContext.UnlistedByVulkan([A("Radeon", 1, 0x1002), A("GTX 680", 2), A("Microsoft Basic Render Driver", 3, 0x1414, true)], vulkan));
            Assert.Empty(VkContext.UnlistedByVulkan([A("Radeon", 1, 0x1002), A("WARP", 4, 0x1414)], vulkan));
            // A Vulkan device without a LUID might be any of them: nothing said.
            Assert.Empty(VkContext.UnlistedByVulkan([A("GTX 680", 2)], [Integrated]));
            Assert.Empty(VkContext.UnlistedByVulkan([A("GTX 680", 2)], [Discrete with { Luid = 2 }, Software]));
            // One GPU DXGI lists twice (the second LUID unknown to Vulkan): matched by its ids.
            var rtx = Discrete with { Luid = 0x125AD, VendorId = 0x10DE, DeviceId = 0x2204 };
            Assert.Empty(VkContext.UnlistedByVulkan([A("RTX 3090", 0x125AD, device: 0x2204), A("RTX 3090", 0x1F659, device: 0x2204)], [rtx]));
            Assert.Equal(["GTX 680"], VkContext.UnlistedByVulkan([A("RTX 3090", 0x1F659, device: 0x2204), A("GTX 680", 2, device: 0x1180)], [rtx]));
        }

        [Fact]
        public void EachChoiceTriesItsDevicesInOrder()
        {
            static string Of(string choice, bool windows, bool integrated = false)
                => string.Join(" ", System.Linq.Enumerable.Select(OniMoono.Desktop.Renderers.Attempts(choice, windows, integrated), a => a.Renderer + (a.Auto ? "(auto)" : "")));
            // Auto on Windows: Direct3D 11 after Vulkan (the M6 rule), first on an integrated
            // GPU (the user's decision), its guard honoured either way.
            Assert.Equal("vulkan(auto) d3d11(auto) opengl", Of("auto", windows: true));
            Assert.Equal("d3d11(auto) vulkan(auto) opengl", Of("auto", windows: true, integrated: true));
            Assert.Equal("vulkan(auto) opengl", Of("auto", windows: false));
            Assert.Equal("vulkan(auto) opengl", Of("auto", windows: false, integrated: true));
            // Explicit choices whatever the GPU; --renderer d3d11 goes on as auto without trying
            // Direct3D 11 again.
            foreach (bool integrated in new[] { false, true })
            {
                Assert.Equal("d3d11 vulkan(auto) opengl", Of("d3d11", windows: true, integrated));
                Assert.Equal("vulkan opengl", Of("vulkan", windows: true, integrated));
                Assert.Equal("opengl", Of("opengl", windows: true, integrated));
            }
        }

    }
}
