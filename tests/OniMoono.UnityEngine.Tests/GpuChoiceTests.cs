using System;
using System.Collections.Generic;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// The GPU option (src/OniMoono.Host/GpuChoice.cs, compiled in here): what each choice sets per
    /// platform, how it treats variables the user or an earlier run set, and that the variables
    /// reach the native environment libGL reads.
    public class GpuChoiceTests
    {
        [Fact]
        public void LinuxVariablesPerChoiceAndDriver()
        {
            Assert.Empty(GpuChoice.LinuxVariables("default", nvidiaOffload: false));
            Assert.Empty(GpuChoice.LinuxVariables("default", nvidiaOffload: true));
            Assert.Equal([("DRI_PRIME", "1")], GpuChoice.LinuxVariables("discrete", nvidiaOffload: false));
            Assert.Equal([("__NV_PRIME_RENDER_OFFLOAD", "1"), ("__GLX_VENDOR_LIBRARY_NAME", "nvidia")], GpuChoice.LinuxVariables("discrete", nvidiaOffload: true));
            Assert.Equal([("DRI_PRIME", "0")], GpuChoice.LinuxVariables("integrated", nvidiaOffload: false));
            Assert.Equal([("DRI_PRIME", "0")], GpuChoice.LinuxVariables("integrated", nvidiaOffload: true));
        }

        static Func<string, string> Env(params (string Name, string Value)[] vars)
        {
            var map = new Dictionary<string, string>();
            foreach (var (n, v) in vars) map[n] = v;
            return n => map.TryGetValue(n, out var v) ? v : null;
        }

        [Fact]
        public void DefaultSetsNothing()
        {
            var plan = GpuChoice.PlanLinux("default", false, Env());
            Assert.Empty(plan.Changes);
            Assert.Empty(plan.UserSet);
            Assert.Null(plan.Marker);
        }

        [Fact]
        public void AVariableTheUserSetIsKept()
        {
            var plan = GpuChoice.PlanLinux("discrete", false, Env(("DRI_PRIME", "pci-0000_01_00_0")));
            Assert.Empty(plan.Changes);
            Assert.Equal([("DRI_PRIME", "pci-0000_01_00_0")], plan.UserSet);
            Assert.Null(plan.Marker);
            // Only the one the user set: NVIDIA's other variable is still set.
            plan = GpuChoice.PlanLinux("discrete", true, Env(("__GLX_VENDOR_LIBRARY_NAME", "mesa")));
            Assert.Equal([("__NV_PRIME_RENDER_OFFLOAD", "1")], plan.Changes);
            Assert.Equal([("__GLX_VENDOR_LIBRARY_NAME", "mesa")], plan.UserSet);
            Assert.Equal("__NV_PRIME_RENDER_OFFLOAD", plan.Marker);
        }

        [Fact]
        public void VariablesAnEarlierRunSetAreReplacedOrUnset()
        {
            // The run App.Restart starts inherits the previous choice's variables and marker.
            var inherited = Env(("DRI_PRIME", "1"), (GpuChoice.MarkerVariable, "DRI_PRIME"));
            var plan = GpuChoice.PlanLinux("integrated", false, inherited);
            Assert.Equal([("DRI_PRIME", "0")], plan.Changes);
            Assert.Empty(plan.UserSet);
            Assert.Equal("DRI_PRIME", plan.Marker);
            plan = GpuChoice.PlanLinux("default", false, inherited);
            Assert.Equal([("DRI_PRIME", (string)null)], plan.Changes);
            Assert.Null(plan.Marker);
            plan = GpuChoice.PlanLinux("discrete", true, inherited);
            Assert.Equal([("__NV_PRIME_RENDER_OFFLOAD", "1"), ("__GLX_VENDOR_LIBRARY_NAME", "nvidia"), ("DRI_PRIME", null)], plan.Changes);
            Assert.Equal("__NV_PRIME_RENDER_OFFLOAD,__GLX_VENDOR_LIBRARY_NAME", plan.Marker);
        }

        /// Why libc's setenv: .NET's own setter leaves the native environment alone on Unix, and
        /// GpuChoice's reaches it (what Mesa and libglvnd read with getenv).
        [Fact]
        public void TheNativeEnvironmentSeesTheVariables()
        {
            if (!OperatingSystem.IsLinux()) return;
            const string probe = "ONI_MOONO_GPU_TEST_PROBE";
            Environment.SetEnvironmentVariable(probe, "managed");
            Assert.Null(GpuChoice.NativeGetenv(probe));
            Environment.SetEnvironmentVariable(probe, null);

            var saved = new Dictionary<string, string>();
            foreach (var name in new[] { "DRI_PRIME", GpuChoice.MarkerVariable }) saved[name] = GpuChoice.NativeGetenv(name);
            try
            {
                GpuChoice.SetBoth("DRI_PRIME", null);
                GpuChoice.SetBoth(GpuChoice.MarkerVariable, null);
                var log = GpuChoice.ApplyLinux("discrete", nvidiaOffload: false);
                Assert.Equal("1", GpuChoice.NativeGetenv("DRI_PRIME"));
                Assert.Equal("1", Environment.GetEnvironmentVariable("DRI_PRIME"));
                Assert.Equal("DRI_PRIME", GpuChoice.NativeGetenv(GpuChoice.MarkerVariable));
                Assert.Contains("gpu: discrete: set DRI_PRIME=1", log);
                // As the restarted run: back to the default, so Moono's variable goes.
                log = GpuChoice.ApplyLinux("default", nvidiaOffload: false);
                Assert.Null(GpuChoice.NativeGetenv("DRI_PRIME"));
                Assert.Null(GpuChoice.NativeGetenv(GpuChoice.MarkerVariable));
                Assert.Contains("gpu: default: unset DRI_PRIME (set by Moono before the restart)", log);
                // One the user set stays.
                GpuChoice.SetBoth("DRI_PRIME", "pci-0000_03_00_0");
                log = GpuChoice.ApplyLinux("integrated", nvidiaOffload: false);
                Assert.Equal("pci-0000_03_00_0", GpuChoice.NativeGetenv("DRI_PRIME"));
                Assert.Contains("gpu: integrated: left DRI_PRIME=pci-0000_03_00_0 as it was set", log);
            }
            finally
            {
                foreach (var (name, value) in saved) GpuChoice.SetBoth(name, value);
            }
        }

        [Fact]
        public void TheSettingsFileAndCommandLineChooseIt()
        {
            var o = EffectiveOptions.Resolve([], [], [], null, false, new PlayerSettings());
            Assert.Equal(("default", OptionSource.Default), (o.Gpu, o.GpuSource));
            var file = PlayerSettings.Parse("""{ "gpu": "discrete" }""");
            o = EffectiveOptions.Resolve([], [], [], null, false, file);
            Assert.Equal(("discrete", OptionSource.SettingsFile), (o.Gpu, o.GpuSource));
            o = EffectiveOptions.Resolve([], [], [], null, false, file, commandLineGpu: "integrated");
            Assert.Equal(("integrated", OptionSource.CommandLine), (o.Gpu, o.GpuSource));
            Assert.Contains("gpu integrated (the command line)", o.Describe());
            Assert.Throws<FormatException>(() => PlayerSettings.Parse("""{ "gpu": "fastest" }"""));
            // Saved: only when it isn't the default; kept when the screen didn't set it.
            Assert.Equal("{\"gpu\":\"integrated\"}", EffectiveOptions.Apply(new PlayerSettings(), [], new Dictionary<string, bool>(), null, null, gpu: "integrated").ToJson(indented: false));
            Assert.Equal("{}", EffectiveOptions.Apply(file, [], new Dictionary<string, bool>(), null, null, gpu: "default").ToJson(indented: false));
            Assert.Equal("{\"gpu\":\"discrete\"}", EffectiveOptions.Apply(file, [], new Dictionary<string, bool>(), null, null).ToJson(indented: false));
        }
    }
}
