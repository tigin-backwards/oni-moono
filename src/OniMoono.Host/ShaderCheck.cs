using System;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Host
{
    /// Device gate: every OpenGLCore variant of every shader in the player data compiles and
    /// links on this machine's GL driver.
    internal static class ShaderCheck
    {
        static readonly string[] files =
        [
            "level0", "level1", "level2", "resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets",
            "Resources/unity default resources", "Resources/unity_builtin_extra",
        ];

        public static int Run(string dataDir)
        {
            AssetSystem.Initialize(dataDir);
            ProjectSettings.Load();
            PlayerLoop.Initialize();
            var platform = new OniMoono.Desktop.GlfwPlatform(new OniMoono.Desktop.WindowSettings("shader check", 64, 64, FullScreenMode.Windowed));
            var device = new OniMoono.Desktop.Gl.GlDevice(platform);
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
            // Every program built before the count is read (the game's warm-up does not wait).
            device.WarmupShaders(wait: true);
            Console.WriteLine($"{shaders} shaders warmed up, {errors} compile/link errors");
            platform.Dispose();
            return errors == 0 ? 0 : 1;
        }
    }
}
