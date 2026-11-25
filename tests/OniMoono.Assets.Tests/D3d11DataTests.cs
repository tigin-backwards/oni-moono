using System;
using System.IO;
using ShaderSurvey;
using Xunit;

namespace OniMoono.Assets.Tests
{
    /// ShaderSurvey's D3D11 check (tools/ShaderSurvey/D3d11Survey.cs, compiled in) over every
    /// D3D11 program of the frozen U60 764025 Windows build and of the U60-layout mod bundles:
    /// each entry read and checked against its parameters, inputs and Vulkan variants, with no
    /// failure. Skipped, with the folder named, on a machine without them.
    public class D3d11DataTests
    {
        /// ONI_WIN_DATA: the frozen Windows build's data folder.
        static string WindowsData => Environment.GetEnvironmentVariable("ONI_WIN_DATA") ?? "/mnt/e/oni-builds/BrutalNotIncluded-764025/BrutalNotIncluded-764025_Data";

        /// ONI_WS: the Workshop mods (scripts/check.sh's default too).
        static string Mods => Environment.GetEnvironmentVariable("ONI_WS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-brutal", "out", "ws");

        public const string NeedsWindowsData = "needs the frozen Windows build's data (ONI_WIN_DATA): ";
        public const string NeedsMods = "needs the Workshop mods (ONI_WS): ";

        [Fact]
        public void EveryGameProgramReadsAndAgrees()
        {
            if (!File.Exists(Path.Combine(WindowsData, "globalgamemanagers"))) Assert.Skip(NeedsWindowsData + WindowsData);
            var log = new StringWriter();
            var t = D3d11Survey.Game(WindowsData, log);
            Assert.True(t.Failures == 0, log.ToString());
            Assert.Equal((121, 1070, 960, 590), (t.Shaders, t.Subprograms, t.Entries, t.VulkanKeys));
        }

        [Fact]
        public void EveryModProgramOfU60sAnd2020sLayoutReadsAndAgrees()
        {
            if (!Directory.Exists(Mods)) Assert.Skip(NeedsMods + Mods);
            var log = new StringWriter();
            var t = D3d11Survey.Bundles(Mods, log);
            Assert.True(t.Failures == 0, log.ToString());
            // The mod set grows; at least the 652 entries of U60's layout of the 2026-11-22
            // survey and the 1,449 of Unity 2020.3's of 2026-11-25 (d3d11-plan.md M7).
            Assert.True(t.Entries >= 652 + 1449, $"{t.Entries} entries");
        }
    }
}
