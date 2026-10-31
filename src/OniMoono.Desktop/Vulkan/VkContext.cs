using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using OniMoono.Engine;

namespace OniMoono.Desktop.Vulkan
{
    /// Why the Vulkan device could not start: the message goes to the log as the reason the
    /// OpenGL device was used instead.
    internal sealed class VulkanUnavailableException(string message, Exception inner = null) : Exception(message, inner);

    /// A physical device as the selection sees it.
    internal sealed record VkCandidate(string Name, VkPhysicalDeviceType Type, int ApiVersion, string Missing)
    {
        public bool Software => Type == VkPhysicalDeviceType.Cpu;
    }

    /// The Vulkan instance, the chosen GPU, its logical device with one graphics queue, and the
    /// memory allocator (VMA). Created before the window exists (no surface: presentation is
    /// checked once the window's surface is made, VkPresenter), so a machine without a usable
    /// Vulkan device falls back to OpenGL before a window is opened for Vulkan.
    ///
    /// Requirements, all core in Vulkan 1.3: dynamic rendering and synchronization2. Optional: BC texture compression (otherwise decoded on upload, as on OpenGL
    /// without S3TC), anisotropic filtering, mirror-clamp-to-edge, push descriptors.
    internal sealed unsafe class VkContext : IDisposable
    {
        public Instance Instance;
        public PhysicalDevice Physical;
        public Device Device;
        public Queue Queue;
        public int QueueFamily;
        public VmaAllocator Allocator;
        public VkPhysicalDeviceProperties Properties;
        public VkPhysicalDeviceLimits Limits => Properties.Limits;
        public string DeviceName = "", DriverName = "", DriverInfo = "";
        public bool TextureCompressionBC, SamplerAnisotropy, MirrorClampToEdge, PushDescriptors;
        /// Windows: the device's LUID (0 when the driver gives none), and whether the DXGI
        /// interop extensions are on (VK_KHR_external_memory_win32, VK_KHR_win32_keyed_mutex).
        public long Luid;
        public bool DxgiInterop;
        public VkPipelineCache PipelineCache;
        VkDebugUtilsMessengerEXT messenger;
        readonly string pipelineCachePath;

        /// The Khronos validation layer is on (ONI_VULKAN_VALIDATION=1, or --vulkan-validation).
        public static bool Validation;
        /// Validation messages of error severity since start (diagnostics, tests).
        public static int ValidationErrors;

        static bool loaderResolverInstalled;

        /// Makes the loader loadable by Brutal.Vulkan, which asks for "vulkan" (only the -dev
        /// package's libvulkan.so on Linux) or "vulkan-1": false when the system has none.
        public static bool LoaderPresent(out string detail)
        {
            string name = OperatingSystem.IsWindows() ? "vulkan-1.dll" : OperatingSystem.IsMacOS() ? "libvulkan.1.dylib" : "libvulkan.so.1";
            if (!NativeLibrary.TryLoad(name, out var handle))
            {
                detail = $"no Vulkan loader ({name})";
                return false;
            }
            NativeLibrary.Free(handle);
            if (OperatingSystem.IsLinux() && !loaderResolverInstalled)
            {
                loaderResolverInstalled = true;
                System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, library) =>
                    library == "vulkan" && assembly == typeof(Brutal.VulkanApi.Vulkan).Assembly ? NativeLibrary.Load("libvulkan.so.1") : IntPtr.Zero;
            }
            detail = name;
            return true;
        }

        /// Picks a GPU for the GPU option: "integrated" prefers an integrated GPU and "discrete" a
        /// discrete one; "default" a discrete one as the original's exe asks the Windows drivers
        /// for, or, with `defaultInDriverOrder` (Linux, where the original sets nothing), the
        /// first the driver lists, as OpenGL's default device is the driver's. Among GPUs ranked
        /// alike the first listed wins. Never one that lacks a requirement, and a software
        /// rasterizer (Mesa's lavapipe) only when `allowSoftware` (an explicit `--renderer
        /// vulkan`): auto would otherwise trade a hardware OpenGL device for a CPU one. Returns
        /// -1 with the reason when none fits.
        public static int Choose(IReadOnlyList<VkCandidate> candidates, string gpuChoice, bool allowSoftware, bool defaultInDriverOrder, out string reason)
        {
            bool driverOrder = defaultInDriverOrder && gpuChoice is not ("integrated" or "discrete");
            int Rank(VkCandidate c) => (c.Type, gpuChoice) switch
            {
                (VkPhysicalDeviceType.Cpu, _) => 4,
                _ when driverOrder => 0,
                (VkPhysicalDeviceType.IntegratedGpu, "integrated") => 0,
                (VkPhysicalDeviceType.DiscreteGpu, "integrated") => 1,
                (VkPhysicalDeviceType.DiscreteGpu, _) => 0,
                (VkPhysicalDeviceType.IntegratedGpu, _) => 1,
                (VkPhysicalDeviceType.VirtualGpu, _) => 2,
                _ => 3,
            };
            int best = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Missing != null || (c.Software && !allowSoftware)) continue;
                if (best < 0 || Rank(c) < Rank(candidates[best])) best = i;
            }
            if (best >= 0)
            {
                var chosen = candidates[best];
                var wanted = gpuChoice == "integrated" ? VkPhysicalDeviceType.IntegratedGpu : VkPhysicalDeviceType.DiscreteGpu;
                reason = driverOrder ? $"{chosen.Name} ({TypeName(chosen.Type)}, gpu option {gpuChoice}: the first GPU the driver lists)"
                    : chosen.Type == wanted
                    ? $"{chosen.Name} ({TypeName(chosen.Type)}, gpu option {gpuChoice})"
                    : $"{chosen.Name} ({TypeName(chosen.Type)}; gpu option {gpuChoice} prefers {(wanted == VkPhysicalDeviceType.IntegratedGpu ? "an integrated" : "a discrete")} GPU, and none is usable)";
                return best;
            }
            if (candidates.Count == 0) reason = "no Vulkan device";
            else reason = string.Join("; ", candidates.Select(c => c.Missing != null ? $"{c.Name}: {c.Missing}"
                : $"{c.Name}: a software rasterizer (only used with --renderer vulkan)"));
            return -1;
        }

        static string TypeName(VkPhysicalDeviceType t) => t switch
        {
            VkPhysicalDeviceType.DiscreteGpu => "discrete",
            VkPhysicalDeviceType.IntegratedGpu => "integrated",
            VkPhysicalDeviceType.VirtualGpu => "virtual",
            VkPhysicalDeviceType.Cpu => "software",
            _ => "other",
        };

        public static readonly int RequiredApi = (1 << 22) | (3 << 12);

        /// Tests of the fallback (ONI_VULKAN_FAIL=context|device|presenter): start-up fails at
        /// that step, after it has made what it makes, as a driver failing there would.
        public static void FailAt(string step)
        {
            if (VkFault.Has(step))
                throw new VulkanUnavailableException($"ONI_VULKAN_FAIL={step} (a test of the fallback)");
        }

        /// The chosen GPU and its driver (read before the device is made).
        public VkGpuIdentity Identity;

        /// Instance, device and allocator; throws VulkanUnavailableException with the reason,
        /// having released whatever it made. `admit`, given the chosen GPU and driver before the
        /// device is made, returns why not to go on (the deny list, the start-up guard) or null.
        public VkContext(string gpuChoice, bool allowSoftware, string cacheDirectory, Func<VkGpuIdentity, string> admit = null)
        {
            pipelineCachePath = cacheDirectory == null ? null : System.IO.Path.Combine(cacheDirectory, "vulkan-pipelines.bin");
            try
            {
                Create(gpuChoice, allowSoftware, admit);
            }
            catch (Exception e)
            {
                Dispose();
                if (e is VulkanUnavailableException) throw;
                throw new VulkanUnavailableException($"{e.GetType().Name}: {e.Message}", e);
            }
        }

        void Create(string gpuChoice, bool allowSoftware, Func<VkGpuIdentity, string> admit)
        {
            if (!LoaderPresent(out var loader)) throw new VulkanUnavailableException(loader);
            GlfwPlatform.Initialize();
            if (!Glfw.VulkanSupported()) throw new VulkanUnavailableException("GLFW finds no Vulkan support for windows");
            int instanceVersion = 0;
            Brutal.VulkanApi.Vulkan.EnumerateInstanceVersion(&instanceVersion);
            if (instanceVersion < RequiredApi) throw new VulkanUnavailableException($"the Vulkan loader is version {Version(instanceVersion)}; 1.3 is needed");
            var extensions = new HashSet<string>();
            byte** required = Glfw.GetRequiredInstanceExtensions(out uint count);
            if (required == null) throw new VulkanUnavailableException("GLFW names no surface extensions");
            for (int i = 0; i < count; i++) extensions.Add(Marshal.PtrToStringUTF8((nint)required[i]));
            var layers = new HashSet<string>();
            // "info": the layer's and the loader's informational messages too (to see it is there);
            // "sync": synchronization validation too (VK_EXT_validation_features), off by default.
            var validationSetting = Environment.GetEnvironmentVariable("ONI_VULKAN_VALIDATION");
            Validation |= validationSetting is "1" or "info" or "sync";
            bool syncValidation = false;
            if (Validation)
            {
                if (LayerAvailable(VK.LAYER_KHRONOS_VALIDATION) || (AddLocalValidationLayer() && LayerAvailable(VK.LAYER_KHRONOS_VALIDATION)))
                {
                    layers.Add(VK.LAYER_KHRONOS_VALIDATION);
                    extensions.Add(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
                    if (validationSetting == "sync")
                    {
                        extensions.Add("VK_EXT_validation_features");
                        syncValidation = true;
                    }
                }
                else
                {
                    EngineLog.Engine("vulkan: validation asked for, but the Khronos validation layer is not installed (scripts/get-vulkan-validation.sh fetches it)");
                    Validation = false;
                }
            }
            fixed (byte* appName = "Oxygen Not Included\0"u8)
            fixed (byte* engineName = "Moono\0"u8)
            {
                var app = new VkApplicationInfo { ApplicationName = appName, EngineName = engineName, ApiVersion = RequiredApi };
                var syncFeature = VkValidationFeatureEnableEXT.SynchronizationValidationEXT;
                var validationFeatures = new VkValidationFeaturesEXT { EnabledValidationFeatureCount = 1, EnabledValidationFeatures = &syncFeature };
                Instance = Brutal.VulkanApi.Vulkan.CreateInstance(new Instance.CreateInfo
                {
                    AppInfo = app, EnabledExtensions = extensions, EnabledLayers = layers, Next = syncValidation ? &validationFeatures : null,
                });
            }
            if (Validation)
            {
                var info = new VkDebugUtilsMessengerCreateInfoEXT
                {
                    MessageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT
                                      | (validationSetting == "info" ? VkDebugUtilsMessageSeverityFlagsEXT.InfoBitEXT : 0),
                    MessageType = VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT,
                    PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&OnValidationMessage),
                };
                messenger = Instance.CreateDebugUtilsMessengerEXT(info, null);
                EngineLog.Engine(syncValidation ? "vulkan: Khronos validation layer on, with synchronization validation" : "vulkan: Khronos validation layer on");
            }

            var candidates = new List<VkCandidate>();
            foreach (var pd in Instance.PhysicalDevices)
            {
                var p = pd.GetProperties();
                candidates.Add(new VkCandidate(Utf8(p.DeviceName, 256), p.DeviceType, p.ApiVersion, MissingRequirements(pd, p)));
            }
            foreach (var c in candidates)
                EngineLog.Engine($"vulkan: device {c.Name} ({TypeName(c.Type)}, Vulkan {Version(c.ApiVersion)}){(c.Missing != null ? ": " + c.Missing : "")}");
            int chosen = Choose(candidates, gpuChoice, allowSoftware, OperatingSystem.IsLinux(), out var why);
            if (chosen < 0) throw new VulkanUnavailableException(why);
            Physical = Instance.PhysicalDevices[chosen];
            Properties = Physical.GetProperties();
            DeviceName = candidates[chosen].Name;

            var features = new VkPhysicalDeviceFeatures2();
            var f12 = new VkPhysicalDeviceVulkan12Features();
            var f13 = new VkPhysicalDeviceVulkan13Features();
            features.Next = &f12;
            f12.Next = &f13;
            Physical.GetFeatures2(ref features);
            TextureCompressionBC = features.Features.TextureCompressionBC;
            SamplerAnisotropy = features.Features.SamplerAnisotropy;
            MirrorClampToEdge = f12.SamplerMirrorClampToEdge;
            PushDescriptors = Physical.SupportsExtension("VK_KHR_push_descriptor"u8);
            // Windows, presenting through DXGI (VkDxgiTarget): a D3D11 texture imported as an
            // image, its keyed mutex taken and given back by the presenter's submissions.
            DxgiInterop = OperatingSystem.IsWindows() && OniMoono.Desktop.Gl.DxgiSwapChain.Enabled
                          && Physical.SupportsExtension("VK_KHR_external_memory_win32"u8) && Physical.SupportsExtension("VK_KHR_win32_keyed_mutex"u8);
            DriverInfo = DriverDescription(out int driverId);
            Identity = new VkGpuIdentity((uint)Properties.VendorID, (uint)Properties.DeviceID, (uint)Properties.DriverVersion, driverId, DriverName, DeviceName);
            if (admit?.Invoke(Identity) is { } refused) throw new VulkanUnavailableException(refused);

            var enable = new VkPhysicalDeviceFeatures2();
            enable.Features.TextureCompressionBC = TextureCompressionBC;
            enable.Features.SamplerAnisotropy = SamplerAnisotropy;
            var e12 = new VkPhysicalDeviceVulkan12Features { SamplerMirrorClampToEdge = MirrorClampToEdge };
            var e13 = new VkPhysicalDeviceVulkan13Features { DynamicRendering = true, Synchronization2 = true };
            enable.Next = &e12;
            e12.Next = &e13;
            QueueFamily = Physical.GetFirstQueueFamilyWithFlag(VkQueueFlags.GraphicsBit);
            float priority = 1f;
            var queueInfo = new VkDeviceQueueCreateInfo { QueueFamilyIndex = QueueFamily, QueueCount = 1, QueuePriorities = &priority };
            var deviceExtensions = new HashSet<string> { VK.KHR_SWAPCHAIN_EXTENSION_NAME };
            if (PushDescriptors) deviceExtensions.Add("VK_KHR_push_descriptor");
            if (DxgiInterop)
            {
                deviceExtensions.Add("VK_KHR_external_memory_win32");
                deviceExtensions.Add("VK_KHR_win32_keyed_mutex");
            }
            Device = Physical.CreateDevice(new Device.CreateInfo
            {
                Extensions = deviceExtensions,
                QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(&queueInfo, 1),
                Next = new Brutal.Pointers.Ptr(&enable),
            });
            Queue = Device.GetQueue(QueueFamily, 0);
            Allocator = Device.CreateVmaAllocator(new VmaAllocatorCreateInfo
            {
                PhysicalDevice = Physical,
                Device = Device,
                VulkanApiVersion = (uint)RequiredApi,
            });
            PipelineCache = CreatePipelineCache();
            FailAt("context");
            EngineLog.Engine($"vulkan: using {why}; {DriverInfo}; BC textures {(TextureCompressionBC ? "sampled" : "decoded on upload")}, "
                             + $"descriptors {(PushDescriptors ? "pushed" : "from per-frame pools")}");
        }

        /// Descriptors a pushed set may hold.
        public int MaxPushDescriptors;

        string DriverDescription(out int driverId)
        {
            var ids = new VkPhysicalDeviceIdProperties();
            var driver = new VkPhysicalDeviceDriverProperties { Next = &ids };
            var push = new VkPhysicalDevicePushDescriptorProperties();
            var props = new VkPhysicalDeviceProperties2 { Next = &driver };
            if (PushDescriptors)
            {
                driver.Next = &push;
                push.Next = &ids;
            }
            Physical.GetProperties2(ref props);
            Luid = ids.DeviceLUIDValid ? *(long*)ids.DeviceLUID : 0;
            MaxPushDescriptors = PushDescriptors ? push.MaxPushDescriptors : 0;
            DriverName = Utf8(driver.DriverName, 256);
            driverId = (int)driver.DriverID;
            return $"Vulkan {Version(Properties.ApiVersion)}, driver {DriverName} {Utf8(driver.DriverInfo, 256)}".TrimEnd();
        }

        static string Utf8(byte* p, int max)
        {
            int n = 0;
            while (n < max && p[n] != 0) n++;
            return System.Text.Encoding.UTF8.GetString(p, n);
        }

        /// What a device lacks of this device's requirements (null: nothing).
        static string MissingRequirements(PhysicalDevice pd, VkPhysicalDeviceProperties p)
        {
            if (p.ApiVersion < RequiredApi) return $"Vulkan {Version(p.ApiVersion)}, 1.3 needed";
            var features = new VkPhysicalDeviceFeatures2();
            var f12 = new VkPhysicalDeviceVulkan12Features();
            var f13 = new VkPhysicalDeviceVulkan13Features();
            features.Next = &f12;
            f12.Next = &f13;
            pd.GetFeatures2(ref features);
            var missing = new List<string>();
            if (!f13.DynamicRendering) missing.Add("dynamic rendering");
            if (!f13.Synchronization2) missing.Add("synchronization2");
            if (!pd.SupportsExtension(VK.KHR_SWAPCHAIN_EXTENSION_NAME)) missing.Add("swapchains");
            if (pd.GetFirstQueueFamilyWithFlag(VkQueueFlags.GraphicsBit) < 0) missing.Add("a graphics queue");
            return missing.Count == 0 ? null : "lacks " + string.Join(", ", missing);
        }

        public static string Version(int v) => $"{(v >> 22) & 0x7F}.{(v >> 12) & 0x3FF}.{v & 0xFFF}";

        /// A validation layer fetched by scripts/get-vulkan-validation.sh, when the system has none:
        /// `vulkan-validation/` beside the executable (a staged Windows player), else a checkout's
        /// out/local-tools/vulkan-validation/{windows,linux} above it; the loader is pointed there
        /// with VK_ADD_LAYER_PATH (in the native environment, which the loader reads), unless that
        /// is set already. False when there is none.
        static bool AddLocalValidationLayer()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_ADD_LAYER_PATH"))) return false;
            string os = OperatingSystem.IsWindows() ? "windows" : "linux";
            const string manifest = "VkLayer_khronos_validation.json";
            string found = null;
            var beside = System.IO.Path.Combine(AppContext.BaseDirectory, "vulkan-validation");
            if (System.IO.File.Exists(System.IO.Path.Combine(beside, manifest))) found = beside;
            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); found == null && dir != null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "out", "local-tools", "vulkan-validation", os);
                if (System.IO.File.Exists(System.IO.Path.Combine(candidate, manifest))) found = candidate;
            }
            if (found == null) return false;
            if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("VK_ADD_LAYER_PATH", found);
            else if (setenv("VK_ADD_LAYER_PATH", found, 1) != 0) return false;
            EngineLog.Engine($"vulkan: validation layer from {found}");
            return true;
        }

        // .NET keeps its own copy of the environment on Unix: the loader reads the C library's.
        [DllImport("libc", CharSet = CharSet.Ansi)]
        static extern int setenv(string name, string value, int overwrite);

        static bool LayerAvailable(string name)
        {
            int count = Brutal.VulkanApi.Vulkan.EnumerateInstanceLayerProperties_Count();
            var layers = new VkLayerProperties[count];
            Brutal.VulkanApi.Vulkan.EnumerateInstanceLayerProperties_Write(layers);
            foreach (var l in layers)
            {
                var copy = l;
                if (Utf8(copy.LayerName, 256) == name) return true;
            }
            return false;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        static Brutal.Bool32 OnValidationMessage(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT type, VkDebugUtilsMessengerCallbackDataEXT* data, void* user)
        {
            bool error = (severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0;
            if (error) System.Threading.Interlocked.Increment(ref ValidationErrors);
            var message = data->Message != null ? Marshal.PtrToStringUTF8((nint)data->Message) : "";
            // Each distinct message the first few times.
            int seen = messages.AddOrUpdate(data->MessageIdNumber, 1, (_, n) => n + 1);
            string kind = error ? "error" : (severity & VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT) != 0 ? "warning" : "info";
            if (seen <= 3) EngineLog.Engine($"vulkan validation {kind} (frame {UnityEngine.Time.frameCount}, #{seen}): {message}");
            return false;
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> messages = new();

        VkPipelineCache CreatePipelineCache()
        {
            byte[] data = null;
            try
            {
                if (pipelineCachePath != null && System.IO.File.Exists(pipelineCachePath)) data = System.IO.File.ReadAllBytes(pipelineCachePath);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                EngineLog.Engine($"vulkan: pipeline cache unreadable ({e.Message})");
            }
            fixed (byte* p = data)
            {
                var info = new VkPipelineCacheCreateInfo { InitialDataSize = (nuint)(data?.Length ?? 0), InitialData = p };
                try
                {
                    return Device.CreatePipelineCache(info, null);
                }
                catch (VkResultError e) when (data != null)
                {
                    // Data from another driver or GPU gives an empty cache, not an error (the
                    // specification), and vkCreatePipelineCache's errors are out of memory and
                    // unknown: tried once more without the data, as a guard against a driver that
                    // fails on it anyway.
                    EngineLog.Engine($"vulkan: the pipeline cache was not made from its file ({e.Result}); starting empty");
                    return Device.CreatePipelineCache(new VkPipelineCacheCreateInfo(), null);
                }
            }
        }

        /// Writes the pipeline cache for the next launch (the driver validates it on load).
        public void SavePipelineCache()
        {
            if (pipelineCachePath == null || PipelineCache.IsNull()) return;
            try
            {
                nuint size = Device.GetPipelineCacheData_Count(PipelineCache);
                var data = new byte[(int)size];
                fixed (byte* p = data)
                {
                    nuint written = size;
                    // VK_INCOMPLETE (the cache grew since its size was asked) is a success too:
                    // what was written is valid cache data (the specification).
                    var r = Device.PInvoke.vkGetPipelineCacheData.Invoke(Device.Handle, PipelineCache, &written, p);
                    if (r is not (VkResult.Success or VkResult.Incomplete))
                    {
                        EngineLog.Engine($"vulkan: pipeline cache not saved (vkGetPipelineCacheData: {r})");
                        return;
                    }
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pipelineCachePath)!);
                    System.IO.File.WriteAllBytes(pipelineCachePath, data.AsSpan(0, (int)written).ToArray());
                }
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or VkResultError)
            {
                EngineLog.Engine($"vulkan: pipeline cache not saved ({e.Message})");
            }
        }

        bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Device != null)
            {
                try { Device.WaitIdle(); } catch (VkResultError) { }
                if (!PipelineCache.IsNull()) Device.DestroyPipelineCache(PipelineCache, null);
                if (Allocator != null) Device.DestroyVmaAllocator(Allocator);
                Device.Dispose();
            }
            if (Instance != null)
            {
                if (!messenger.IsNull()) Instance.DestroyDebugUtilsMessengerEXT(messenger, null);
                Instance.Dispose();
            }
            Device = null;
            Instance = null;
        }
    }
}
