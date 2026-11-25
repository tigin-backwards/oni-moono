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
    /// "renderer", `--renderer`): auto is Vulkan, else OpenGL; on Windows Direct3D 11 comes
    /// after Vulkan, or first on an integrated GPU (Attempts). ARCHITECTURE §3.5. `d3d11`
    /// (Windows; `--renderer d3d11`, not in the Tweaks row) is the Direct3D 11 device being
    /// built (docs/research/d3d11-plan.md); when it cannot start, the choice goes on as auto.
    internal static class Renderers
    {
        public const string Auto = "auto", Vulkan = "vulkan", OpenGl = "opengl", D3d11 = "d3d11";
        public static readonly string[] Choices = [Auto, Vulkan, OpenGl, D3d11];
        public static bool IsChoice(string value) => Array.IndexOf(Choices, value) >= 0;

        /// What settings.json may hold (the Tweaks screen's choices): d3d11 stays a command-line
        /// choice until the device ships.
        public static readonly string[] SettingsChoices = [Auto, Vulkan, OpenGl];
        public static bool IsSettingsChoice(string value) => Array.IndexOf(SettingsChoices, value) >= 0;

        /// One device to try: the renderer, and whether it is tried as `auto` (its start-up guard
        /// honoured, software devices and Vulkan's deny list refused).
        internal readonly record struct Attempt(string Renderer, bool Auto);

        /// The devices a choice tries, in order, until one starts. `d3d11` tries Direct3D 11,
        /// then goes on as auto without it; `auto` on Windows tries Direct3D 11, Vulkan, then
        /// OpenGL on an integrated GPU, and Vulkan, Direct3D 11, then OpenGL otherwise (the D3D11
        /// plan's M6: the default/fallback rule made Direct3D 11 the fallback, and the user
        /// decided on 2026-11-25 that integrated GPUs default to it); `auto` elsewhere and
        /// `vulkan` try Vulkan, then OpenGL; `opengl` OpenGL alone. OpenGL, the last, always opens.
        internal static Attempt[] Attempts(string choice, bool windows, bool integrated)
        {
            var list = new System.Collections.Generic.List<Attempt>();
            if (choice == D3d11)
            {
                list.Add(new Attempt(D3d11, false));
                choice = Auto;
            }
            if (choice != OpenGl)
            {
                bool direct3d = windows && choice == Auto && list.Count == 0;
                if (direct3d && integrated) list.Add(new Attempt(D3d11, true));
                list.Add(new Attempt(Vulkan, choice == Auto));
                if (direct3d && !integrated) list.Add(new Attempt(D3d11, true));
            }
            list.Add(new Attempt(OpenGl, false));
            return list.ToArray();
        }

        /// Opens the window and its device for a choice, logging which renderer it is and why.
        /// The choice's devices are tried in order (Attempts). Vulkan: the instance and a GPU that
        /// meets the requirements before a window exists, then the window's surface and swapchain;
        /// Direct3D 11 the same way (its adapter by the GPU choice rule and its device, then the
        /// window and swap chain); any failure releases what was made and the next is tried, with
        /// the reason in the log. As auto, Vulkan also refuses software devices (lavapipe), the
        /// drivers on the deny list (VkDenyList) and a GPU, driver and build whose start never got
        /// its first frames on screen (VkStartGuard); Direct3D 11 refuses a software adapter and
        /// honours its own start-up guard. `source` says where the choice came from.
        ///
        /// Integrated: auto on Windows starts with Vulkan, whose GPU choice (VkContext.Choose: a
        /// discrete GPU first, or the `--gpu` preference) is the GPU the game would draw on.
        /// When that is VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU ("the device is typically one
        /// embedded in or tightly coupled with the host", the Vulkan specification's
        /// VkPhysicalDeviceType) Vulkan stops before its deny list, guard and device
        /// (VkGpuIdentity.Integrated), and the order becomes Direct3D 11, Vulkan, OpenGL. A hybrid
        /// laptop or a desktop with a discrete card is not integrated: Vulkan picks the discrete
        /// GPU. Nothing more is made to tell (the instance is Vulkan's own first step).
        public static (GlfwPlatform Platform, RenderDevice Device) Open(WindowSettings settings, string choice, string source, string gpu, string cacheDirectory)
        {
            var reasons = new System.Collections.Generic.List<string>();
            if (choice == OpenGl) reasons.Add($"OpenGL chosen ({source})");
            bool windows = OperatingSystem.IsWindows(), integrated = false, vulkanTried = false;
            string order = null;
            if (choice == Auto && windows)
            {
                var (platform, device, failure, integratedGpu) = TryVulkan(settings, gpu, auto: true, cacheDirectory, yieldIfIntegrated: true);
                if (device != null)
                {
                    EngineLog.Engine($"renderer: Vulkan (auto, {source}); {integratedGpu ?? "the GPU"} is not integrated, so Vulkan first");
                    return (platform, device);
                }
                if (integratedGpu != null)
                {
                    integrated = true;
                    order = $"{failure}, so Direct3D 11 first";
                }
                else
                {
                    vulkanTried = true;
                    reasons.Add($"Vulkan unavailable: {failure}");
                }
            }
            foreach (var attempt in Attempts(choice, windows, integrated))
            {
                if (attempt.Renderer == OpenGl) break;
                bool d3d = attempt.Renderer == D3d11;
                if (!d3d && attempt.Auto && vulkanTried) continue;
                GlfwPlatform platform;
                RenderDevice device;
                string failure;
                if (d3d) (platform, device, failure) = TryD3d11(settings, gpu, attempt.Auto, cacheDirectory);
                else (platform, device, failure, _) = TryVulkan(settings, gpu, attempt.Auto, cacheDirectory);
                string name = d3d ? "Direct3D 11" : "Vulkan";
                if (device != null)
                {
                    // Why auto took this order, and the devices tried before it.
                    EngineLog.Engine($"renderer: {name} ({(attempt.Auto ? Auto : attempt.Renderer)}, {source}){(order != null ? "; " + order : "")}{(reasons.Count > 0 ? "; " + string.Join("; ", reasons) : "")}");
                    return (platform, device);
                }
                // `--renderer d3d11` that cannot start goes on as auto (M2).
                if (d3d && !attempt.Auto) EngineLog.Engine($"renderer: Direct3D 11 unavailable ({failure}); trying the others as auto");
                reasons.Add($"{name} unavailable: {failure}");
            }
            EngineLog.Engine($"renderer: OpenGL ({(order != null ? order + "; " : "")}{string.Join("; ", reasons)})");
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

        /// The fourth value: with `yieldIfIntegrated`, the GPU's name when it is integrated (then
        /// nothing was made and the failure says why), else its name when it started; null when
        /// Vulkan stopped for another reason.
        static (GlfwPlatform, RenderDevice, string, string) TryVulkan(WindowSettings settings, string gpu, bool auto, string cacheDirectory, bool yieldIfIntegrated = false)
        {
            VkContext context;
            VkStartGuard guard = null;
            string build = BuildId();
            string integrated = null;
            // Before the device is made: an integrated GPU given way (Open), the deny list (auto
            // only), then the start-up guard.
            string Admit(VkGpuIdentity id)
            {
                if (yieldIfIntegrated && id.Integrated)
                {
                    integrated = id.DeviceName;
                    return $"{id.DeviceName} is integrated (VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU)";
                }
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
                return (null, null, e.Message, integrated);
            }
            catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
            {
                guard?.Abandon();
                return (null, null, $"{e.GetType().Name}: {e.Message}", null);
            }
            GlfwPlatform platform = null;
            try
            {
                platform = new GlfwPlatform(settings with { Api = RenderApi.Vulkan });
                return (platform, new VkDevice(platform, context, cacheDirectory, guard), null, context.DeviceName);
            }
            catch (Exception e)
            {
                // VkDevice released what it made; the window and the context go too. A start that
                // fell back cleanly is no crash.
                platform?.Dispose();
                context.Dispose();
                guard?.Abandon();
                return (null, null, e is VulkanUnavailableException ? e.Message : $"{e.GetType().Name}: {e.Message}", null);
            }
        }

        /// A Direct3D 11 device made and its start-up guard entered, not yet started.
        sealed class D3dStart(D3d.D3d11Context context, VkStartGuard guard)
        {
            public readonly D3d.D3d11Context Context = context;
            public readonly VkStartGuard Guard = guard;

            /// Not to be started: the device goes, and the guard's line with it.
            public void Release()
            {
                Context.Dispose();
                Guard?.Abandon();
            }
        }

        /// Direct3D 11 as TryVulkan, in two steps. This one: the adapter chosen, then (before its
        /// device is made) the start-up guard (`d3d11-start-guard.txt`, honoured under `auto`
        /// only; `--renderer d3d11` always tries and its clean start clears the key); a software
        /// adapter only when not `auto`; then the device. Null, with the reason, having released
        /// what was made (the guard's line with it). No driver is on a deny list: Vulkan's two
        /// entries are Vulkan drivers' (VkDenyList), and no Direct3D 11 driver is known to need
        /// one (d3d11-plan.md, "M5").
        static D3dStart OpenD3d11(string gpu, bool auto, string cacheDirectory, out string failure)
        {
            failure = null;
            foreach (var unknown in D3d.D3d11Fault.Unknown())
                EngineLog.Engine($"d3d11: {D3d.D3d11Fault.Variable}: no fault '{unknown}' in this build");
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
                return new D3dStart(new D3d.D3d11Context(gpu, allowSoftware: !auto, Admit), guard);
            }
            catch (Exception e)
            {
                // Any failure: auto on Windows asks this first, so nothing here may stop a start
                // another renderer can make (the context released what it made).
                guard?.Abandon();
                failure = e is D3d.D3d11UnavailableException or DllNotFoundException or EntryPointNotFoundException ? e.Message : $"{e.GetType().Name}: {e.Message}";
                return null;
            }
        }

        /// Direct3D 11's two steps (OpenD3d11, StartD3d11).
        static (GlfwPlatform, RenderDevice, string) TryD3d11(WindowSettings settings, string gpu, bool auto, string cacheDirectory)
        {
            var start = OpenD3d11(gpu, auto, cacheDirectory, out var failure);
            return start == null ? (null, null, failure) : StartD3d11(settings, start);
        }

        /// The second step: the window and the device's swap chain and presenter. Any failure
        /// releases the window, the context and the guard's line.
        static (GlfwPlatform, RenderDevice, string) StartD3d11(WindowSettings settings, D3dStart start)
        {
            GlfwPlatform platform = null;
            try
            {
                platform = new GlfwPlatform(settings with { Api = RenderApi.Direct3D11 });
                return (platform, new D3d.D3d11Device(platform, start.Context, start.Guard), null);
            }
            catch (Exception e)
            {
                platform?.Dispose();
                start.Release();
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
