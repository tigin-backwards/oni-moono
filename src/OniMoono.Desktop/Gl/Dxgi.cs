using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OniMoono.Desktop.Gl
{
    /// The few DXGI and Direct3D 11 calls DxgiTarget makes, as COM vtable calls through function
    /// pointers (no interop package). Slots, GUIDs, constants and struct layouts are those of the
    /// Windows SDK 10.0.26100 headers (checked with offsetof on the vtables and structs, compiled
    /// with MSVC); a slot is the method's index in the interface's vtable, inherited ones first.
    internal static unsafe class Dxgi
    {
        // ----- HRESULTs -----
        public const int S_OK = 0;
        public const int StatusOccluded = 0x087A0001;
        public const int ErrorDeviceRemoved = unchecked((int)0x887A0005), ErrorDeviceHung = unchecked((int)0x887A0006),
            ErrorDeviceReset = unchecked((int)0x887A0007), ErrorDriverInternal = unchecked((int)0x887A0020);
        /// D3DDDIERR_DEVICEREMOVED: IDXGISwapChain::Present may return it in place of
        /// DXGI_ERROR_DEVICE_REMOVED after the card is removed or its driver upgraded (Microsoft).
        public const int DdiErrorDeviceRemoved = unchecked((int)0x88760870);

        // ----- Constants -----
        public const uint FormatR8G8B8A8Unorm = 28;
        public const uint UsageRenderTargetOutput = 0x20;
        public const uint ScalingStretch = 0;
        public const uint SwapEffectFlipSequential = 3, SwapEffectFlipDiscard = 4;
        public const uint AlphaModeIgnore = 3;
        public const uint SwapChainFlagFrameLatencyWaitable = 0x40, SwapChainFlagAllowTearing = 0x800;
        public const uint PresentAllowTearing = 0x200;
        public const uint WindowAssociationNoWindowChanges = 1, WindowAssociationNoAltEnter = 2;
        public const uint FeaturePresentAllowTearing = 0;
        public const uint AdapterFlagSoftware = 2;
        public const int DriverTypeUnknown = 0;
        public const uint D3D11SdkVersion = 7;
        public const uint BindShaderResource = 0x8, BindRenderTarget = 0x20;
        public const uint MiscSharedKeyedMutex = 0x100, MiscSharedNtHandle = 0x800;
        public const uint SharedResourceRead = 0x80000000, SharedResourceWrite = 1;
        /// IDXGIKeyedMutex::AcquireSync's results that are not S_OK (success codes, not failures).
        public const int WaitAbandoned = 0x80, WaitTimedOut = 0x102;

        // ----- Interface IDs -----
        public static readonly Guid IIDFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        public static readonly Guid IIDFactory4 = new("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");
        public static readonly Guid IIDFactory5 = new("7632e1f5-ee65-4dca-87fd-84cd75f8838d");
        public static readonly Guid IIDAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
        public static readonly Guid IIDAdapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");
        public static readonly Guid IIDSwapChain2 = new("a8be2ac4-199f-4946-b331-79599fb98de7");
        public static readonly Guid IIDTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        public static readonly Guid IIDResource1 = new("30961379-4609-4a41-998e-54fe567ee0c1");
        public static readonly Guid IIDKeyedMutex = new("9d8e1289-d7b3-465f-8126-250e349af85d");

        // ----- Structs -----
        [StructLayout(LayoutKind.Sequential)]
        public struct SwapChainDesc1
        {
            public uint Width, Height, Format, Stereo, SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Texture2DDesc
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Box
        {
            public uint Left, Top, Front, Right, Bottom, Back;
        }

        /// DXGI_ADAPTER_DESC1 (312 bytes).
        [StructLayout(LayoutKind.Explicit, Size = 312)]
        public struct AdapterDesc1
        {
            [FieldOffset(0)] public fixed char Description[128];
            [FieldOffset(256)] public uint VendorId;
            [FieldOffset(260)] public uint DeviceId;
            [FieldOffset(272)] public nuint DedicatedVideoMemory;
            [FieldOffset(296)] public long Luid;
            [FieldOffset(304)] public uint Flags;

            public string Name
            {
                get
                {
                    fixed (char* p = Description) return new string(p, 0, 128).TrimEnd('\0');
                }
            }
        }

        /// DXGI_QUERY_VIDEO_MEMORY_INFO.
        [StructLayout(LayoutKind.Sequential)]
        public struct VideoMemoryInfo
        {
            public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
        }

        // ----- Entry points -----
        [DllImport("dxgi.dll")]
        public static extern int CreateDXGIFactory2(uint flags, Guid* riid, void** factory);

        [DllImport("d3d11.dll")]
        public static extern int D3D11CreateDevice(void* adapter, int driverType, IntPtr software, uint flags, int* featureLevels, uint featureLevelCount,
            uint sdkVersion, void** device, int* featureLevel, void** immediateContext);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, int alertable);

        [DllImport("kernel32.dll")]
        public static extern int CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern uint GetLastError();

        public const uint WaitObject0 = 0, WaitTimeout = 0x102;

        // ----- Vtable calls -----
        static void** Vtbl(void* o) => *(void***)o;

        public static int QueryInterface(void* o, in Guid iid, void** result)
        {
            fixed (Guid* g = &iid) return ((delegate* unmanaged<void*, Guid*, void**, int>)Vtbl(o)[0])(o, g, result);
        }

        /// Releases the reference `o` holds, once: `o` is cleared first.
        public static void Release(ref void* o)
        {
            var p = o;
            o = null;
            if (p != null) ((delegate* unmanaged<void*, uint>)Vtbl(p)[2])(p);
        }

        // IDXGIFactory1/2/4/5
        public static int EnumAdapters1(void* factory, uint index, void** adapter) => ((delegate* unmanaged<void*, uint, void**, int>)Vtbl(factory)[12])(factory, index, adapter);
        public static int MakeWindowAssociation(void* factory, IntPtr hwnd, uint flags) => ((delegate* unmanaged<void*, IntPtr, uint, int>)Vtbl(factory)[8])(factory, hwnd, flags);
        public static int CreateSwapChainForHwnd(void* factory, void* device, IntPtr hwnd, SwapChainDesc1* desc, void** swapChain)
            => ((delegate* unmanaged<void*, void*, IntPtr, SwapChainDesc1*, void*, void*, void**, int>)Vtbl(factory)[15])(factory, device, hwnd, desc, null, null, swapChain);
        public static int EnumAdapterByLuid(void* factory4, long luid, in Guid iid, void** adapter)
        {
            fixed (Guid* g = &iid) return ((delegate* unmanaged<void*, long, Guid*, void**, int>)Vtbl(factory4)[26])(factory4, luid, g, adapter);
        }
        public static int CheckFeatureSupport(void* factory5, uint feature, void* data, uint size) => ((delegate* unmanaged<void*, uint, void*, uint, int>)Vtbl(factory5)[28])(factory5, feature, data, size);

        // IDXGIAdapter1/3
        public static int GetDesc1(void* adapter, AdapterDesc1* desc) => ((delegate* unmanaged<void*, AdapterDesc1*, int>)Vtbl(adapter)[10])(adapter, desc);
        public static int QueryVideoMemoryInfo(void* adapter3, uint node, uint segmentGroup, VideoMemoryInfo* info)
            => ((delegate* unmanaged<void*, uint, uint, VideoMemoryInfo*, int>)Vtbl(adapter3)[14])(adapter3, node, segmentGroup, info);

        // IDXGISwapChain1/2
        public static int Present(void* swapChain, uint syncInterval, uint flags) => ((delegate* unmanaged<void*, uint, uint, int>)Vtbl(swapChain)[8])(swapChain, syncInterval, flags);
        public static int GetBuffer(void* swapChain, uint index, in Guid iid, void** surface)
        {
            fixed (Guid* g = &iid) return ((delegate* unmanaged<void*, uint, Guid*, void**, int>)Vtbl(swapChain)[9])(swapChain, index, g, surface);
        }
        public static int ResizeBuffers(void* swapChain, uint count, uint width, uint height, uint format, uint flags)
            => ((delegate* unmanaged<void*, uint, uint, uint, uint, uint, int>)Vtbl(swapChain)[13])(swapChain, count, width, height, format, flags);
        public static int GetDesc1(void* swapChain, SwapChainDesc1* desc) => ((delegate* unmanaged<void*, SwapChainDesc1*, int>)Vtbl(swapChain)[18])(swapChain, desc);
        public static int SetMaximumFrameLatency(void* swapChain2, uint latency) => ((delegate* unmanaged<void*, uint, int>)Vtbl(swapChain2)[31])(swapChain2, latency);
        public static IntPtr GetFrameLatencyWaitableObject(void* swapChain2) => ((delegate* unmanaged<void*, IntPtr>)Vtbl(swapChain2)[33])(swapChain2);

        // IDXGIResource1 / IDXGIKeyedMutex
        public static int CreateSharedHandle(void* resource1, uint access, IntPtr* handle)
            => ((delegate* unmanaged<void*, void*, uint, char*, IntPtr*, int>)Vtbl(resource1)[13])(resource1, null, access, null, handle);
        public static int AcquireSync(void* keyedMutex, ulong key, uint milliseconds) => ((delegate* unmanaged<void*, ulong, uint, int>)Vtbl(keyedMutex)[8])(keyedMutex, key, milliseconds);
        public static int ReleaseSync(void* keyedMutex, ulong key) => ((delegate* unmanaged<void*, ulong, int>)Vtbl(keyedMutex)[9])(keyedMutex, key);

        // ID3D11Device / ID3D11DeviceContext
        public static int CreateTexture2D(void* device, Texture2DDesc* desc, void** texture) => ((delegate* unmanaged<void*, Texture2DDesc*, void*, void**, int>)Vtbl(device)[5])(device, desc, null, texture);
        public static int GetDeviceRemovedReason(void* device) => ((delegate* unmanaged<void*, int>)Vtbl(device)[39])(device);
        public static void CopySubresourceRegion(void* context, void* destination, uint destinationSubresource, uint x, uint y, uint z, void* source, uint sourceSubresource, Box* box)
            => ((delegate* unmanaged<void*, void*, uint, uint, uint, uint, void*, uint, Box*, void>)Vtbl(context)[46])(context, destination, destinationSubresource, x, y, z, source, sourceSubresource, box);
        public static void ClearState(void* context) => ((delegate* unmanaged<void*, void>)Vtbl(context)[110])(context);
        public static void Flush(void* context) => ((delegate* unmanaged<void*, void>)Vtbl(context)[111])(context);

        // ----- Helpers (pure, tested) -----

        public static bool Failed(int hr) => hr < 0;

        /// A failure that means the device is gone (removed, reset, hung, or the driver failed):
        /// nothing made on it works again.
        public static bool DeviceLost(int hr) => hr is ErrorDeviceRemoved or ErrorDeviceReset or ErrorDeviceHung or ErrorDriverInternal or DdiErrorDeviceRemoved;

        public static string Hr(int hr) => hr switch
        {
            ErrorDeviceRemoved => "DXGI_ERROR_DEVICE_REMOVED",
            ErrorDeviceReset => "DXGI_ERROR_DEVICE_RESET",
            ErrorDeviceHung => "DXGI_ERROR_DEVICE_HUNG",
            ErrorDriverInternal => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
            DdiErrorDeviceRemoved => "D3DDDIERR_DEVICEREMOVED",
            StatusOccluded => "DXGI_STATUS_OCCLUDED",
            _ => $"0x{hr:X8}",
        };

        /// PCI vendor IDs from GL_VENDOR: NVIDIA, AMD ("ATI Technologies Inc." on Windows) and
        /// Intel; 0 for anything else (Microsoft's GDI renderer among them).
        public static uint VendorOf(string glVendor)
        {
            if (glVendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return 0x10DE;
            if (glVendor.Contains("ATI", StringComparison.Ordinal) || glVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return 0x1002;
            if (glVendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return 0x8086;
            return 0;
        }

        /// The DXGI adapter the OpenGL context renders on, when the driver gives no LUID: the
        /// hardware adapter of GL_VENDOR's vendor whose description GL_RENDERER starts with
        /// ("NVIDIA GeForce RTX 3090/PCIe/SSE2" and "NVIDIA GeForce RTX 3090"), else the only
        /// one of that vendor; -1 when none, or several of the vendor and no name decides.
        public static int MatchAdapter(string glVendor, string glRenderer, IReadOnlyList<(uint VendorId, string Description, bool Software)> adapters)
        {
            uint vendor = VendorOf(glVendor);
            if (vendor == 0) return -1;
            int byName = -1, byVendor = -1, ofVendor = 0;
            for (int i = 0; i < adapters.Count; i++)
            {
                var a = adapters[i];
                if (a.Software || a.VendorId != vendor) continue;
                ofVendor++;
                if (byVendor < 0) byVendor = i;
                if (byName < 0 && a.Description.Length > 0 && glRenderer.StartsWith(a.Description.Trim(), StringComparison.OrdinalIgnoreCase)) byName = i;
            }
            return byName >= 0 ? byName : ofVendor == 1 ? byVendor : -1;
        }

        /// The shared texture's size for a picture of `needed` pixels on a side: what it has
        /// when that is enough, else the need with a quarter more, rounded up to 64, so a window
        /// dragged larger re-creates it only now and then. At most D3D11's 16384.
        public static int Capacity(int current, int needed)
        {
            if (needed <= current) return current;
            long grown = ((long)needed * 5 / 4 + 63) / 64 * 64;
            return (int)Math.Min(16384, Math.Max(needed, grown));
        }

        /// The calling process's video memory use on the adapter `luid` (local, then non-local),
        /// in bytes; null when DXGI can't tell. Any thread: it makes and releases its own objects.
        public static (ulong Local, ulong NonLocal)? ProcessVideoMemory(long luid)
        {
            void* factory = null, factory4 = null, adapter3 = null;
            try
            {
                var iid = IIDFactory2;
                if (Failed(CreateDXGIFactory2(0, &iid, &factory))) return null;
                if (Failed(QueryInterface(factory, IIDFactory4, &factory4))) return null;
                if (Failed(EnumAdapterByLuid(factory4, luid, IIDAdapter3, &adapter3))) return null;
                VideoMemoryInfo local, nonLocal;
                if (Failed(QueryVideoMemoryInfo(adapter3, 0, 0, &local)) || Failed(QueryVideoMemoryInfo(adapter3, 0, 1, &nonLocal))) return null;
                return (local.CurrentUsage, nonLocal.CurrentUsage);
            }
            finally
            {
                Release(ref adapter3);
                Release(ref factory4);
                Release(ref factory);
            }
        }
    }
}
