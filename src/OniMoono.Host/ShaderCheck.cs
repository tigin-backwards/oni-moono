using System;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Host
{
    /// Device gate: every variant of every shader in the player data builds on this machine's
    /// driver, with the device the renderer option opens: on OpenGL each GLCore variant compiles
    /// and links; on Vulkan each of U60's Vulkan programs (or, for a shader without them, its
    /// GLCore program compiled to SPIR-V) is made a module and a pipeline for its pass's state.
    /// Failures are counted, and the run goes on to the end.
    internal static class ShaderCheck
    {
        static readonly string[] files =
        [
            "level0", "level1", "level2", "resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets",
            "Resources/unity default resources", "Resources/unity_builtin_extra",
        ];

        public static int Run(string dataDir, Options options)
        {
            AssetSystem.Initialize(dataDir);
            ProjectSettings.Load();
            PlayerLoop.Initialize();
            OniMoono.Desktop.Vulkan.VkContext.Validation = options.VulkanValidation;
            OniMoono.Desktop.Vulkan.VkDevice.CheckPipelines = true;
            var (platform, device) = Player.OpenDevice(options, new OniMoono.Desktop.WindowSettings("shader check", 64, 64, FullScreenMode.Windowed));
            GraphicsDevice.Install(device);
            int errors = 0;
            // The GL thread logs compile and link errors: only the threaded callback sees them.
            Application.logMessageReceivedThreaded += (condition, stack, type) =>
            {
                if (type == LogType.Error) System.Threading.Interlocked.Increment(ref errors);
            };
            int shaders = 0;
            foreach (var name in files)
            {
                var file = AssetSystem.Get(name);
                foreach (var info in file.Objects)
                    if (file.TypeOf(info).ClassId == 48 && ObjectLoader.Load(file, info.PathId) is Shader) shaders++;
            }
            // Every program built before the count is read (the game's warm-up does not wait on
            // OpenGL).
            if (device is OniMoono.Desktop.Gl.GlDevice gl) gl.WarmupShaders(wait: true);
            else Shader.WarmupAllShaders();
            // The Vulkan device logs what it cannot build without raising errors.
            errors += OniMoono.Desktop.Vulkan.VkDevice.CheckFailures;
            Console.WriteLine($"{shaders} shaders warmed up, {errors} compile/link errors{(device is OniMoono.Desktop.Vulkan.VkDevice ? $" ({OniMoono.Desktop.Vulkan.VkDevice.CheckFailures} Vulkan programs or pipelines not built)" : "")}");
            platform.Dispose();
            return errors == 0 ? 0 : 1;
        }
    }
}
