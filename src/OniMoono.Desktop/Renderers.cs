using System;
using System.IO;
using OniMoono.Desktop.Gl;
using D3d = OniMoono.Desktop.D3d11;
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

        /// What settings.json may hold (the Tweaks screen's choices): d3d11 stays a command-line
        /// choice until the device ships.
        public static readonly string[] SettingsChoices = [Auto, Vulkan, OpenGl];
        public static bool IsSettingsChoice(string value) => Array.IndexOf(SettingsChoices, value) >= 0;

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
                var (d3dPlatform, d3dDevice, d3dFailure) = TryD3d11(settings, gpu, auto: false, cacheDirectory);
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

        /// Direct3D 11 as TryVulkan: the adapter chosen, then (before its device is made) the
        /// start-up guard (`d3d11-start-guard.txt`, honoured under `auto` only; `--renderer d3d11`
        /// always tries and its clean start clears the key); a software adapter only when not
        /// `auto`. Any failure releases what was made, the guard's line going with it. No driver
        /// is on a deny list: Vulkan's two entries are Vulkan drivers' (VkDenyList), and no
        /// Direct3D 11 driver is known to need one (d3d11-plan.md, "M5"). Auto does not try
        /// Direct3D 11 yet (M6 decides where it goes in auto's order).
        static (GlfwPlatform, RenderDevice, string) TryD3d11(WindowSettings settings, string gpu, bool auto, string cacheDirectory)
        {
            foreach (var unknown in D3d.D3d11Fault.Unknown())
                EngineLog.Engine($"d3d11: {D3d.D3d11Fault.Variable}: no fault '{unknown}' in this build");
            D3d.D3d11Context context;
            VkStartGuard guard = null;
            string build = BuildId(D3d.D3d11Fault.Text("guard-build"));
            string Admit(D3d.D3d11GpuIdentity id)
            {
                if (cacheDirectory == null) return null;
                guard = new VkStartGuard(VkStartGuard.FileIn(cacheDirectory, GuardNames.D3d11), D3d11GuardKey(id, build), GuardNames.D3d11);
                var refused = guard.Enter(honour: auto);
                if (refused != null) guard = null;
                return refused;
            }
            try
            {
                context = new D3d.D3d11Context(gpu, allowSoftware: !auto, Admit);
            }
            catch (Exception e) when (e is D3d.D3d11UnavailableException or DllNotFoundException or EntryPointNotFoundException)
            {
                guard?.Abandon();
                return (null, null, e.Message);
            }
            GlfwPlatform platform = null;
            try
            {
                platform = new GlfwPlatform(settings with { Api = RenderApi.Direct3D11 });
                return (platform, new D3d.D3d11Device(platform, context, guard), null);
            }
            catch (Exception e)
            {
                platform?.Dispose();
                context.Dispose();
                guard?.Abandon();
                return (null, null, e is D3d.D3d11UnavailableException ? e.Message : $"{e.GetType().Name}: {e.Message}");
            }
        }

        /// The Direct3D 11 start-up guard's key: the adapter's vendor and device ids, its driver's
        /// version and Moono's build, as VkStartGuard.KeyOf for Vulkan.
        internal static string D3d11GuardKey(D3d.D3d11GpuIdentity id, string build)
            => $"gpu {id.VendorId:x4}:{id.DeviceId:x4} driver {id.Driver} build {build}";

        /// Moono's build, as the start-up guard keys it: the host's informational version (its
        /// commit), or a `guard-build:X` fault (tests): ONI_VULKAN_FAIL's, or `fault` (ONI_D3D11_FAIL's).
        static string BuildId(string fault = null)
        {
            fault ??= VkFault.Text("guard-build");
            if (!string.IsNullOrEmpty(fault)) return fault;
            var assembly = System.Reflection.Assembly.GetEntryAssembly();
            return assembly == null ? "unknown"
                : System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion ?? "unknown";
        }
    }
}
