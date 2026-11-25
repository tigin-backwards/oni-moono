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
    /// GLCore program compiled to SPIR-V) is made a module and a pipeline for its pass's state;
    /// on Direct3D 11 every subprogram's shader object and its program's input layouts are made
    /// (D3d11Device.WarmupShaders).
    /// Failures are counted, and the run goes on to the end.
    internal static class ShaderCheck
    {
        static readonly string[] files =
        [
            "level0", "level1", "level2", "globalgamemanagers.assets", "resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets",
            "Resources/unity default resources", "Resources/unity_builtin_extra",
        ];

        public static int Run(string dataDir, Options options)
        {
            AssetSystem.Initialize(dataDir);
            ProjectSettings.Load();
            PlayerLoop.Initialize();
            OniMoono.Desktop.Vulkan.VkContext.Validation = options.VulkanValidation;
            OniMoono.Desktop.Vulkan.VkDevice.CheckPipelines = true;
            OniMoono.Desktop.D3d11.D3d11Context.Validation = options.D3d11Debug;
            OniMoono.Desktop.D3d11.D3d11Context.FeatureLevel110Only = options.D3d11FeatureLevel110;
            OniMoono.Desktop.D3d11.D3d11Device.CheckPrograms = true;
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
            // The Spaced Out bundle's own copies of Klei/LiquidSubstance and Klei/Substance
            // (d3d11-plan.md M3 part C): loaded as the game loads the bundle (AssetBundle.
            // LoadFromFile), each of its shaders made an object, so the device checks them too.
            var bundlePath = System.IO.Path.Combine(dataDir, "StreamingAssets", "expansion1_bundle");
            if (System.IO.File.Exists(bundlePath) && AssetBundle.LoadFromFile(bundlePath) is { } bundle)
            {
                var bundleFile = ((AssetBundleNative)bundle.NativeOrThrow).File;
                int fromBundle = 0;
                foreach (var info in bundleFile.Objects)
                    if (bundleFile.TypeOf(info).ClassId == 48 && ObjectLoader.Load(bundleFile, info.PathId) is Shader s)
                    {
                        shaders++;
                        fromBundle++;
                        EngineLog.Engine($"shader check: expansion1_bundle shader '{s.name}'");
                    }
                Console.WriteLine($"expansion1_bundle: {fromBundle} shaders");
            }
            // Mods' bundles (d3d11-plan.md M7, done criterion 6): ONI_CHECK_BUNDLES names folders
            // (separated as PATH is), and every Unity bundle under them is loaded as a mod loads
            // it (AssetBundle.LoadFromFile) and its shaders made objects, so the device checks
            // them as the game's. Their data is only read; no mod code runs.
            foreach (var dir in (Environment.GetEnvironmentVariable("ONI_CHECK_BUNDLES") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] paths;
                try
                {
                    paths = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Order(System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories), StringComparer.Ordinal));
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"shader check: bundle folder {dir} not read: {e.Message}");
                    errors++;
                    continue;
                }
                foreach (var path in paths)
                    try
                    {
                        if (!IsUnityFs(path)) continue;
                        // Null when a bundle of the same files is loaded already (logged by the
                        // engine): counted, as its shaders are not checked from this copy.
                        if (AssetBundle.LoadFromFile(path) is not { } loaded)
                        {
                            Console.WriteLine($"shader check: bundle {path} not loaded");
                            continue;
                        }
                        var modFile = ((AssetBundleNative)loaded.NativeOrThrow).File;
                        foreach (var info in modFile.Objects)
                            if (modFile.TypeOf(info).ClassId == 48 && ObjectLoader.Load(modFile, info.PathId) is Shader s)
                            {
                                shaders++;
                                EngineLog.Engine($"shader check: bundle {path} shader '{s.name}'");
                            }
                    }
                    catch (Exception e) when (e is System.IO.InvalidDataException or System.IO.IOException or NotSupportedException or UnauthorizedAccessException)
                    {
                        Console.WriteLine($"shader check: bundle {path} not read: {e.Message}");
                        errors++;
                    }
            }
            // Every program built before the count is read (the game's warm-up does not wait on
            // OpenGL).
            if (device is OniMoono.Desktop.Gl.GlDevice gl) gl.WarmupShaders(wait: true);
            else Shader.WarmupAllShaders();
            // The Vulkan device logs what it cannot build without raising errors.
            errors += OniMoono.Desktop.Vulkan.VkDevice.CheckFailures;
            // The Direct3D 11 device counts what it cannot build or refuses, and the debug
            // layer's messages (D3d11Device.WarmupShaders).
            errors += OniMoono.Desktop.D3d11.D3d11Device.CheckFailures;
            Console.WriteLine($"{shaders} shaders warmed up, {errors} compile/link errors{(device is OniMoono.Desktop.Vulkan.VkDevice ? $" ({OniMoono.Desktop.Vulkan.VkDevice.CheckFailures} Vulkan programs or pipelines not built)" : "")}");
            platform.Dispose();
            return errors == 0 ? 0 : 1;
        }

        static bool IsUnityFs(string path)
        {
            var head = new byte[8];
            using var f = System.IO.File.OpenRead(path);
            return f.Read(head) == 8 && System.Text.Encoding.ASCII.GetString(head) == "UnityFS\0";
        }
    }
}
