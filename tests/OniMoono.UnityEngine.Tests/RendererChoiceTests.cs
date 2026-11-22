using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using OniMoono.Desktop.Gl;
using OniMoono.Desktop.Vulkan;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// The renderer option (settings file, command line) and the Vulkan device's GPU selection.
    public class RendererChoiceTests
    {
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
            // The Direct3D 11 device being built (Renderers.D3d11) is a choice, though not on the Tweaks screen.
            Assert.Equal("d3d11", PlayerSettings.Parse("""{ "renderer": "d3d11" }""").Renderer);
            // Saved only when it isn't the default; kept when the screen didn't set it.
            Assert.Equal("{\"renderer\":\"vulkan\"}", EffectiveOptions.Apply(new PlayerSettings(), [], new Dictionary<string, bool>(), null, null, renderer: "vulkan").ToJson(indented: false));
            Assert.Equal("{}", EffectiveOptions.Apply(file, [], new Dictionary<string, bool>(), null, null, renderer: "auto").ToJson(indented: false));
            Assert.Equal("{\"renderer\":\"opengl\"}", EffectiveOptions.Apply(file, [], new Dictionary<string, bool>(), null, null).ToJson(indented: false));
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
    }
}
