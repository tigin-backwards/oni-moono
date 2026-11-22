using System;
using System.IO;
using OniMoono.Desktop.Gl;
using OniMoono.Desktop.Vulkan;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;

namespace OniMoono.Desktop
{
    /// The renderer choice (the Moono Tweaks screen's "Renderer" row, settings.json's
    /// "renderer", `--renderer`): Vulkan by default, OpenGL when Vulkan cannot start.
    /// ARCHITECTURE §3.5. `d3d11` (Windows; `--renderer d3d11` only, not in the Tweaks row) is the
    /// Direct3D 11 device being built (docs/research/d3d11-plan.md); when it cannot start, the
    /// choice goes on as auto.
    internal static class Renderers
    {
        public const string Auto = "auto", Vulkan = "vulkan", OpenGl = "opengl", D3d11 = "d3d11";
        public static readonly string[] Choices = [Auto, Vulkan, OpenGl, D3d11];
        public static bool IsChoice(string value) => Array.IndexOf(Choices, value) >= 0;

        /// Opens the window and its device for a choice, logging which renderer it is and why.
        /// "auto" and "vulkan" try Vulkan first: the instance and a GPU that meets the
        /// requirements before a window exists, then the window's surface and swapchain; any
        /// failure releases what was made and opens an OpenGL window instead, with the reason in
        /// the log. "auto" also refuses software Vulkan devices (lavapipe), the drivers on the deny
        /// list (VkDenyList) and a GPU, driver and build whose start never got its first frames on
        /// screen (VkStartGuard). `source` says where the choice came from.
        public static (GlfwPlatform Platform, RenderDevice Device) Open(WindowSettings settings, string choice, string source, string gpu, string cacheDirectory)
        {
            string reason = null;
            if (choice == D3d11)
            {
                var (d3dPlatform, d3dDevice, d3dFailure) = TryD3d11(settings, gpu);
                if (d3dDevice != null)
                {
                    EngineLog.Engine($"renderer: Direct3D 11 ({choice}, {source})");
                    return (d3dPlatform, d3dDevice);
                }
                EngineLog.Engine($"renderer: Direct3D 11 unavailable ({d3dFailure}); trying the others as auto");
                choice = Auto;
            }
            if (choice == OpenGl) reason = $"OpenGL chosen ({source})";
            else
            {
                var (platform, device, failure) = TryVulkan(settings, gpu, auto: choice == Auto, cacheDirectory);
                if (device != null)
                {
                    EngineLog.Engine($"renderer: Vulkan ({choice}, {source})");
                    return (platform, device);
                }
                reason = $"Vulkan unavailable: {failure}";
            }
            EngineLog.Engine($"renderer: OpenGL ({reason})");
            var glPlatform = new GlfwPlatform(settings with { Api = RenderApi.OpenGl });
            try
            {
                return (glPlatform, new GlDevice(glPlatform));
            }
            catch
            {
                glPlatform.Dispose();
                throw;
            }
        }

        static (GlfwPlatform, RenderDevice, string) TryVulkan(WindowSettings settings, string gpu, bool auto, string cacheDirectory)
        {
            VkContext context;
            VkStartGuard guard = null;
            string build = BuildId();
            // Before the device is made: the deny list (auto only), then the start-up guard.
            string Admit(VkGpuIdentity id)
            {
                if (auto && VkDenyList.Reason(id) is { } denied)
                    return $"{denied} is on the deny list (--renderer vulkan tries it)";
                if (cacheDirectory == null) return null;
                guard = new VkStartGuard(VkStartGuard.FileIn(cacheDirectory), VkStartGuard.KeyOf(id, build));
                var refused = guard.Enter(honour: auto);
                if (refused != null) guard = null;
                return refused;
            }
            try
            {
                context = new VkContext(gpu, allowSoftware: !auto, cacheDirectory, Admit);
            }
            catch (VulkanUnavailableException e)
            {
                guard?.Abandon();
                return (null, null, e.Message);
            }
            catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
            {
                guard?.Abandon();
                return (null, null, $"{e.GetType().Name}: {e.Message}");
            }
            GlfwPlatform platform = null;
            try
            {
                platform = new GlfwPlatform(settings with { Api = RenderApi.Vulkan });
                return (platform, new VkDevice(platform, context, cacheDirectory, guard), null);
            }
            catch (Exception e)
            {
                // VkDevice released what it made; the window and the context go too. A start that
                // fell back cleanly is no crash.
                platform?.Dispose();
                context.Dispose();
                guard?.Abandon();
                return (null, null, e is VulkanUnavailableException ? e.Message : $"{e.GetType().Name}: {e.Message}");
            }
        }

        static (GlfwPlatform, RenderDevice, string) TryD3d11(WindowSettings settings, string gpu)
        {
            global::OniMoono.Desktop.D3d11.D3d11Context context;
            try
            {
                context = new global::OniMoono.Desktop.D3d11.D3d11Context(gpu);
            }
            catch (Exception e) when (e is global::OniMoono.Desktop.D3d11.D3d11UnavailableException or DllNotFoundException or EntryPointNotFoundException)
            {
                return (null, null, e.Message);
            }
            GlfwPlatform platform = null;
            try
            {
                platform = new GlfwPlatform(settings with { Api = RenderApi.Direct3D11 });
                return (platform, new global::OniMoono.Desktop.D3d11.D3d11Device(platform, context), null);
            }
            catch (Exception e)
            {
                platform?.Dispose();
                context.Dispose();
                return (null, null, e is global::OniMoono.Desktop.D3d11.D3d11UnavailableException ? e.Message : $"{e.GetType().Name}: {e.Message}");
            }
        }

        /// Moono's build, as the start-up guard keys it: the host's informational version (its
        /// commit), or ONI_VULKAN_FAIL=guard-build:X (tests).
        static string BuildId()
        {
            var fault = VkFault.Text("guard-build");
            if (!string.IsNullOrEmpty(fault)) return fault;
            var assembly = System.Reflection.Assembly.GetEntryAssembly();
            return assembly == null ? "unknown"
                : System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion ?? "unknown";
        }
    }
}
