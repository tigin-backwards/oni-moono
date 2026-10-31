using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Win32;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;

namespace OniMoono.Desktop.Vulkan
{
    /// Windows, Vulkan: the presenter's picture goes to the screen through DxgiSwapChain, as the
    /// original's does, instead of a VkSwapchainKHR (which the driver may layer on DXGI as it
    /// likes; Windows then cannot tell the game is SDR). ARCHITECTURE §3.5, "Presentation on
    /// Windows".
    ///
    /// The picture is a D3D11 texture of ours made with an NT handle and a keyed mutex, imported
    /// as a Vulkan image (VK_KHR_external_memory_win32, a dedicated allocation). The presenter's
    /// submission that fills it acquires the mutex with key 0 and releases it with key 1
    /// (VK_KHR_win32_keyed_mutex); D3D11 then acquires key 1, copies it into the swap chain's back
    /// buffer and releases key 0 before presenting. The mutex orders the two APIs' GPU work both
    /// ways, with no CPU wait between them. Vulkan's and D3D11's images are both stored top row
    /// first: no flip. The texture is made again only when the window outgrows it.
    ///
    /// Threads: made on the thread that makes the presenter, then used and released by the
    /// presenter thread alone.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal sealed unsafe class VkDxgiTarget : IDisposable
    {
        const uint MutexTimeoutMilliseconds = 1000;

        readonly VkContext vk;
        readonly IntPtr hwnd;
        DxgiSwapChain chain;
        // The shared texture and its keyed mutex (one COM reference each, released once).
        void* shared, keyedMutex;
        // Its Vulkan import.
        VkImage image;
        VkDeviceMemory memory;
        VkImageView view;
        int capacityWidth, capacityHeight, acquires, imports;
        string failure;

        public VkImage Image => image;
        public VkImageView View => view;
        public VkDeviceMemory Memory => memory;
        public const VkFormat Format = VkFormat.R8G8B8A8UNorm;
        public int Width => chain.Width;
        public int Height => chain.Height;
        public long AdapterLuid => chain?.AdapterLuid ?? 0;
        public string Failure => failure ?? chain?.Failure;
        public long LastWait => chain?.LastWait ?? 0;

        VkDxgiTarget(VkContext vk, IntPtr hwnd)
        {
            this.vk = vk;
            this.hwnd = hwnd;
        }

        /// The target for window `hwnd`, for a first picture of `width` x `height`; null, with the
        /// reason, when any step fails (what was made is released; the device is idle).
        public static VkDxgiTarget Create(VkContext vk, IntPtr hwnd, int width, int height, out string reason, out bool windowTouched)
        {
            var target = new VkDxgiTarget(vk, hwnd);
            try
            {
                reason = target.Start(Math.Max(1, width), Math.Max(1, height));
            }
            catch (Exception e) when (e is VkResultError or DllNotFoundException or EntryPointNotFoundException)
            {
                reason = $"{e.GetType().Name}: {e.Message}";
            }
            windowTouched = target.chain?.WindowTouched ?? false;
            if (reason == null) return target;
            target.Dispose();
            return null;
        }

        string Start(int w, int h)
        {
            if (DxgiSwapChain.RestartRefused) return "a restart that fails";
            if (!vk.DxgiInterop) return "the Vulkan driver has no VK_KHR_external_memory_win32 and VK_KHR_win32_keyed_mutex";
            if (DxgiSwapChain.Faulty("adapter") || vk.Luid == 0) return "the Vulkan device gives no LUID to find its DXGI adapter by";
            if (!Importable(out string why)) return why;
            // The device and the import first, the swap chain last: once the window has had one,
            // the device's own presentation is not reliably shown on it.
            chain = DxgiSwapChain.Open(vk.Luid, out string reason);
            if (chain == null) return reason;
            if (!MakeShared(w, h)) return Failure;
            reason = chain.AttachWindow(hwnd, w, h);
            if (reason != null) return reason;
            EngineLog.Engine(chain.Describe("the Vulkan device's LUID", $"shared texture {capacityWidth}x{capacityHeight} (VK_KHR_external_memory_win32, keyed mutex)"));
            return null;
        }

        static VkImageCreateInfo ImageInfo(int width, int height, VkExternalMemoryImageCreateInfo* external) => new()
        {
            Next = external,
            ImageType = VkImageType._2D,
            Format = Format,
            Extent = new VkExtent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.ColorAttachmentBit,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        };

        /// The driver can import a D3D11 texture as such an image.
        bool Importable(out string why)
        {
            var external = new VkPhysicalDeviceExternalImageFormatInfo { HandleType = VkExternalMemoryHandleTypeFlags.D3D11TextureBit };
            var info = new VkPhysicalDeviceImageFormatInfo2
            {
                Next = &external, Format = Format, Type = VkImageType._2D, Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.ColorAttachmentBit,
            };
            var externalProps = new VkExternalImageFormatProperties();
            var props = new VkImageFormatProperties2 { Next = &externalProps };
            var r = vk.Instance.PInvoke.vkGetPhysicalDeviceImageFormatProperties2.Invoke(vk.Physical.Handle, &info, &props);
            if (r != VkResult.Success)
            {
                why = $"the Vulkan driver cannot import a D3D11 texture as an image ({r})";
                return false;
            }
            if ((externalProps.ExternalMemoryProperties.ExternalMemoryFeatures & VkExternalMemoryFeatureFlags.ImportableBit) == 0)
            {
                why = "the Vulkan driver does not import D3D11 textures";
                return false;
            }
            why = null;
            return true;
        }

        public bool Fail(string reason)
        {
            failure ??= reason;
            return false;
        }

        /// The shared texture, at least `w` x `h`, and its Vulkan import, made anew when the one
        /// there is smaller. The caller has made sure no submitted work still uses the old one.
        bool MakeShared(int w, int h)
        {
            ReleaseShared();
            capacityWidth = capacityWidth == 0 ? w : Dxgi.Capacity(capacityWidth, w);
            capacityHeight = capacityHeight == 0 ? h : Dxgi.Capacity(capacityHeight, h);
            var desc = new Dxgi.Texture2DDesc
            {
                Width = (uint)capacityWidth, Height = (uint)capacityHeight, MipLevels = 1, ArraySize = 1, Format = Dxgi.FormatR8G8B8A8Unorm,
                SampleCount = 1, BindFlags = Dxgi.BindRenderTarget | Dxgi.BindShaderResource,
                MiscFlags = Dxgi.MiscSharedNtHandle | Dxgi.MiscSharedKeyedMutex,
            };
            void* t;
            int hr = Dxgi.CreateTexture2D(chain.Device, &desc, &t);
            if (Dxgi.Failed(hr)) return Fail($"CreateTexture2D {capacityWidth}x{capacityHeight} (shared) failed ({Dxgi.Hr(hr)}{chain.DeviceState(hr)})");
            shared = t;
            void* m;
            hr = Dxgi.QueryInterface(shared, Dxgi.IIDKeyedMutex, &m);
            if (Dxgi.Failed(hr)) return Fail($"no IDXGIKeyedMutex ({Dxgi.Hr(hr)})");
            keyedMutex = m;
            void* resource1;
            hr = Dxgi.QueryInterface(shared, Dxgi.IIDResource1, &resource1);
            if (Dxgi.Failed(hr)) return Fail($"no IDXGIResource1 ({Dxgi.Hr(hr)})");
            IntPtr handle = IntPtr.Zero;
            hr = Dxgi.CreateSharedHandle(resource1, Dxgi.SharedResourceRead | Dxgi.SharedResourceWrite, &handle);
            Dxgi.Release(ref resource1);
            if (Dxgi.Failed(hr)) return Fail($"CreateSharedHandle failed ({Dxgi.Hr(hr)})");
            try
            {
                return Import(handle);
            }
            finally
            {
                // Importing an NT handle does not take it over: it is closed once imported.
                Dxgi.CloseHandle(handle);
            }
        }

        bool Import(IntPtr handle)
        {
            if (DxgiSwapChain.Faulty("import")) return Fail("the Vulkan import of the shared texture failed");
            var device = vk.Device.Handle;
            var external = new VkExternalMemoryImageCreateInfo { HandleTypes = VkExternalMemoryHandleTypeFlags.D3D11TextureBit };
            var info = ImageInfo(capacityWidth, capacityHeight, &external);
            VkImage img;
            var r = vk.Device.PInvoke.vkCreateImage.Invoke(device, &info, null, &img);
            if (r != VkResult.Success) return Fail($"vkCreateImage (shared texture) failed ({r})");
            image = img;
            var handleProps = new VkMemoryWin32HandlePropertiesKHR();
            r = vk.Device.PInvoke.vkGetMemoryWin32HandlePropertiesKHR.Invoke(device, VkExternalMemoryHandleTypeFlags.D3D11TextureBit, handle, &handleProps);
            if (r != VkResult.Success) return Fail($"vkGetMemoryWin32HandlePropertiesKHR failed ({r})");
            VkMemoryRequirements requirements;
            vk.Device.PInvoke.vkGetImageMemoryRequirements.Invoke(device, image, &requirements);
            uint types = (uint)requirements.MemoryTypeBits & (uint)handleProps.MemoryTypeBits;
            var memoryProperties = vk.Physical.GetMemoryProperties();
            int type = -1;
            for (int i = 0; i < memoryProperties.MemoryTypeCount; i++)
            {
                if ((types & (1u << i)) == 0) continue;
                if (type < 0) type = i;
                if ((memoryProperties.MemoryTypes[i].PropertyFlags & VkMemoryPropertyFlags.DeviceLocalBit) != 0)
                {
                    type = i;
                    break;
                }
            }
            if (type < 0) return Fail($"no memory type for the shared texture (image {requirements.MemoryTypeBits:X}, handle {handleProps.MemoryTypeBits:X})");
            var dedicated = new VkMemoryDedicatedAllocateInfo { Image = image };
            var import = new VkImportMemoryWin32HandleInfoKHR { Next = &dedicated, HandleType = VkExternalMemoryHandleTypeFlags.D3D11TextureBit, Handle = handle };
            var allocate = new VkMemoryAllocateInfo { Next = &import, AllocationSize = requirements.Size, MemoryTypeIndex = type };
            VkDeviceMemory mem;
            r = vk.Device.PInvoke.vkAllocateMemory.Invoke(device, &allocate, null, &mem);
            if (r != VkResult.Success) return Fail($"vkAllocateMemory (import of the shared texture) failed ({r})");
            memory = mem;
            r = vk.Device.PInvoke.vkBindImageMemory.Invoke(device, image, memory, default);
            if (r != VkResult.Success) return Fail($"vkBindImageMemory (shared texture) failed ({r})");
            // In play too (the window outgrew the texture): a failure here, out of memory, say, is
            // DXGI's to recover from (a restart), not the game's end.
            try
            {
                if (DxgiSwapChain.Faulty("importview", ++imports)) throw new VkResultError(VkResult.ErrorOutOfDeviceMemory, "--dxgi-fault importview");
                view = vk.Device.CreateImageView(new VkImageViewCreateInfo
                {
                    Image = image, ViewType = VkImageViewType._2D, Format = Format,
                    SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
                }, null);
            }
            catch (VkResultError e)
            {
                return Fail($"vkCreateImageView (shared texture) failed ({e.Result})");
            }
            if (PresentTrace.Enabled) PresentTrace.Log($"dxgi: shared texture {capacityWidth}x{capacityHeight} imported into Vulkan");
            return true;
        }

        void ReleaseShared()
        {
            if (!view.IsNull()) vk.Device.DestroyImageView(view, null);
            if (!image.IsNull()) vk.Device.DestroyImage(image, null);
            if (!memory.IsNull()) vk.Device.FreeMemory(memory, null);
            view = default;
            image = default;
            memory = default;
            Dxgi.Release(ref keyedMutex);
            Dxgi.Release(ref shared);
        }

        /// Makes the swap chain `w` x `h` and the shared texture at least that (presenter thread,
        /// before recording a picture). `idle` is called first when the texture is made anew: no
        /// submitted work may still use the old one.
        public bool Prepare(int w, int h, Action idle)
        {
            if (Failure != null) return false;
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (!chain.Resize(w, h)) return false;
            if (w > capacityWidth || h > capacityHeight)
            {
                idle();
                if (!MakeShared(w, h)) return false;
            }
            return true;
        }

        /// The keyed mutex's part of the submission that fills the texture (acquire key 0,
        /// release key 1), written into `info` (kept alive by the caller until the submit).
        public void MutexInfo(VkWin32KeyedMutexAcquireReleaseInfoKHR* info, VkDeviceMemory* memoryCell, nint* acquireKey, nint* releaseKey, int* timeout)
        {
            *memoryCell = memory;
            *acquireKey = 0;
            *releaseKey = 1;
            *timeout = (int)MutexTimeoutMilliseconds;
            *info = new VkWin32KeyedMutexAcquireReleaseInfoKHR
            {
                AcquireCount = 1, AcquireSyncs = memoryCell, AcquireKeys = acquireKey, AcquireTimeouts = timeout,
                ReleaseCount = 1, ReleaseSyncs = memoryCell, ReleaseKeys = releaseKey,
            };
        }

        /// After the submission that filled the texture: D3D11 takes it (key 1), copies it into
        /// the swap chain's back buffer, gives it back (key 0) and presents.
        public bool Show(int interval)
        {
            if (Failure != null) return false;
            acquires++;
            int hr = DxgiSwapChain.Faulty("lock", acquires) ? Dxgi.WaitTimedOut : Dxgi.AcquireSync(keyedMutex, 1, MutexTimeoutMilliseconds);
            if (hr != Dxgi.S_OK) return Fail($"IDXGIKeyedMutex::AcquireSync(1) failed ({(hr == Dxgi.WaitTimedOut ? "timed out" : hr == Dxgi.WaitAbandoned ? "abandoned" : Dxgi.Hr(hr))})");
            chain.CopyToBackBuffer(shared);
            hr = Dxgi.ReleaseSync(keyedMutex, 0);
            if (Dxgi.Failed(hr)) return Fail($"IDXGIKeyedMutex::ReleaseSync(0) failed ({Dxgi.Hr(hr)})");
            return chain.PresentCopied(interval);
        }

        /// Releases everything (presenter thread, or the thread that made it when it failed).
        /// The caller has waited for the queue to be idle. Safe more than once.
        public void Dispose()
        {
            ReleaseShared();
            chain?.Dispose();
            chain = null;
        }
    }
}
