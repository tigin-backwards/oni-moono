using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OniMoono.Desktop.D3d11;

namespace OniMoono.Desktop.Gl
{
    /// The few DXGI and Direct3D 11 calls DxgiTarget makes, as COM vtable calls through function
    /// pointers (no interop package): the interfaces' slots are declared in D3d11/ (DxgiInterfaces.cs,
    /// D3d11.cs). Every slot, signature, GUID, constant, struct layout and DLL function here and
    /// there is checked against the Windows SDK headers with MSVC (tools/ComCheckGen).
    [Native(Header = "dxgi1_6.h")]
    internal static unsafe class Dxgi
    {
        // ----- HRESULTs -----
        [Native("S_OK", Header = "winerror.h")] public const int S_OK = 0;
        /// What CreateSwapChainForHwnd returns for a window that already has a flip-model swap
        /// chain (not in its documented list; measured, DxgiWindowReport).
        [Native("E_ACCESSDENIED", Header = "winerror.h")] public const int AccessDenied = unchecked((int)0x80070005);
        [Native("DXGI_STATUS_OCCLUDED", Header = "winerror.h")] public const int StatusOccluded = 0x087A0001;
        [Native("DXGI_ERROR_DEVICE_REMOVED", Header = "winerror.h")] public const int ErrorDeviceRemoved = unchecked((int)0x887A0005);
        [Native("DXGI_ERROR_DEVICE_HUNG", Header = "winerror.h")] public const int ErrorDeviceHung = unchecked((int)0x887A0006);
        [Native("DXGI_ERROR_DEVICE_RESET", Header = "winerror.h")] public const int ErrorDeviceReset = unchecked((int)0x887A0007);
        [Native("DXGI_ERROR_DRIVER_INTERNAL_ERROR", Header = "winerror.h")] public const int ErrorDriverInternal = unchecked((int)0x887A0020);
        /// D3DDDIERR_DEVICEREMOVED: IDXGISwapChain::Present may return it in place of
        /// DXGI_ERROR_DEVICE_REMOVED after the card is removed or its driver upgraded (Microsoft).
        [NotNative("D3DDDIERR_DEVICEREMOVED is in the WDK's d3dumddi.h, not the SDK")]
        public const int DdiErrorDeviceRemoved = unchecked((int)0x88760870);

        // ----- Constants -----
        [Native("DXGI_FORMAT_R8G8B8A8_UNORM")] public const uint FormatR8G8B8A8Unorm = 28;
        [Native("DXGI_USAGE_SHADER_INPUT")] public const uint UsageShaderInput = 0x10;
        [Native("DXGI_USAGE_RENDER_TARGET_OUTPUT")] public const uint UsageRenderTargetOutput = 0x20;
        [Native("DXGI_SCALING_STRETCH")] public const uint ScalingStretch = 0;
        [Native("DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL")] public const uint SwapEffectFlipSequential = 3;
        [Native("DXGI_SWAP_EFFECT_FLIP_DISCARD")] public const uint SwapEffectFlipDiscard = 4;
        [Native("DXGI_ALPHA_MODE_IGNORE")] public const uint AlphaModeIgnore = 3;
        [Native("DXGI_ALPHA_MODE_UNSPECIFIED")] public const uint AlphaModeUnspecified = 0;
        [Native("DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT")] public const uint SwapChainFlagFrameLatencyWaitable = 0x40;
        [Native("DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING")] public const uint SwapChainFlagAllowTearing = 0x800;
        [Native("DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH")] public const uint SwapChainFlagAllowModeSwitch = 2;
        [Native("DXGI_PRESENT_ALLOW_TEARING")] public const uint PresentAllowTearing = 0x200;
        [Native("DXGI_MWA_NO_WINDOW_CHANGES")] public const uint WindowAssociationNoWindowChanges = 1;
        [Native("DXGI_MWA_NO_ALT_ENTER")] public const uint WindowAssociationNoAltEnter = 2;
        [Native("DXGI_FEATURE_PRESENT_ALLOW_TEARING")] public const uint FeaturePresentAllowTearing = 0;
        [Native("DXGI_ADAPTER_FLAG_SOFTWARE")] public const uint AdapterFlagSoftware = 2;
        [Native("D3D_DRIVER_TYPE_UNKNOWN")] public const int DriverTypeUnknown = 0;
        [Native("D3D11_SDK_VERSION", Header = "d3d11.h")] public const uint D3D11SdkVersion = 7;
        [Native("D3D11_BIND_SHADER_RESOURCE", Header = "d3d11.h")] public const uint BindShaderResource = 0x8;
        [Native("D3D11_BIND_RENDER_TARGET", Header = "d3d11.h")] public const uint BindRenderTarget = 0x20;
        [Native("D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX", Header = "d3d11.h")] public const uint MiscSharedKeyedMutex = 0x100;
        [Native("D3D11_RESOURCE_MISC_SHARED_NTHANDLE", Header = "d3d11.h")] public const uint MiscSharedNtHandle = 0x800;
        [Native("DXGI_SHARED_RESOURCE_READ")] public const uint SharedResourceRead = 0x80000000;
        [Native("DXGI_SHARED_RESOURCE_WRITE")] public const uint SharedResourceWrite = 1;
        /// IDXGIKeyedMutex::AcquireSync's results that are not S_OK (success codes, not failures).
        [Native("WAIT_ABANDONED", Header = "winbase.h")] public const int WaitAbandoned = 0x80;
        [Native("WAIT_TIMEOUT", Header = "winerror.h")] public const int WaitTimedOut = 0x102;

        // ----- Interface IDs -----
        [Native("IDXGIFactory2")] public static readonly Guid IIDFactory2 = IDXGIFactory2.IID;
        [Native("IDXGIFactory4")] public static readonly Guid IIDFactory4 = IDXGIFactory4.IID;
        [Native("ID3D10Device", Header = "d3d10.h")] public static readonly Guid IIDD3D10Device = new("9b7e4c0f-342c-4106-a19f-4f2704f689f0");
        [Native("IDXGIFactory5")] public static readonly Guid IIDFactory5 = IDXGIFactory5.IID;
        [Native("IDXGIAdapter1")] public static readonly Guid IIDAdapter1 = IDXGIAdapter1.IID;
        [Native("IDXGIAdapter3")] public static readonly Guid IIDAdapter3 = IDXGIAdapter3.IID;
        [Native("IDXGISwapChain2")] public static readonly Guid IIDSwapChain2 = IDXGISwapChain2.IID;
        [Native("ID3D11Texture2D")] public static readonly Guid IIDTexture2D = ID3D11Texture2D.IID;
        [Native("IDXGIResource1")] public static readonly Guid IIDResource1 = IDXGIResource1.IID;
        [Native("IDXGIKeyedMutex")] public static readonly Guid IIDKeyedMutex = IDXGIKeyedMutex.IID;

        // ----- Structs -----
        /// DXGI_SWAP_CHAIN_DESC1 (dxgi1_2.h).
        [Native("DXGI_SWAP_CHAIN_DESC1", Header = "dxgi1_2.h")]
        [StructLayout(LayoutKind.Sequential)]
        public struct SwapChainDesc1
        {
            public uint Width, Height;
            [As("DXGI_FORMAT")] public uint Format;
            public int Stereo;
            [Native("SampleDesc.Count")] public uint SampleCount;
            [Native("SampleDesc.Quality")] public uint SampleQuality;
            [As("DXGI_USAGE")] public uint BufferUsage;
            public uint BufferCount;
            [As("DXGI_SCALING")] public uint Scaling;
            [As("DXGI_SWAP_EFFECT")] public uint SwapEffect;
            [As("DXGI_ALPHA_MODE")] public uint AlphaMode;
            public uint Flags;
        }

        /// D3D11_TEXTURE2D_DESC (d3d11.h).
        [Native("D3D11_TEXTURE2D_DESC", Header = "d3d11.h")]
        [StructLayout(LayoutKind.Sequential)]
        public struct Texture2DDesc
        {
            public uint Width, Height, MipLevels, ArraySize;
            [As("DXGI_FORMAT")] public uint Format;
            [Native("SampleDesc.Count")] public uint SampleCount;
            [Native("SampleDesc.Quality")] public uint SampleQuality;
            [As("D3D11_USAGE")] public uint Usage;
            public uint BindFlags;
            [Native("CPUAccessFlags")] public uint CpuAccessFlags;
            public uint MiscFlags;
        }

        /// D3D11_BOX (d3d11.h).
        [Native("D3D11_BOX", Header = "d3d11.h")]
        [StructLayout(LayoutKind.Sequential)]
        public struct Box
        {
            [Native("left")] public uint Left;
            [Native("top")] public uint Top;
            [Native("front")] public uint Front;
            [Native("right")] public uint Right;
            [Native("bottom")] public uint Bottom;
            [Native("back")] public uint Back;
        }

        /// DXGI_ADAPTER_DESC1 (312 bytes): the fields read.
        [Native("DXGI_ADAPTER_DESC1", Header = "dxgi.h")]
        [StructLayout(LayoutKind.Explicit, Size = 312)]
        public struct AdapterDesc1
        {
            [FieldOffset(0)] public fixed char Description[128];
            [FieldOffset(256)] public uint VendorId;
            [FieldOffset(260)] public uint DeviceId;
            [FieldOffset(272)] public nuint DedicatedVideoMemory;
            [FieldOffset(296)] public LUID AdapterLuid;
            [FieldOffset(304)] public uint Flags;

            public readonly long Luid => AdapterLuid.Value;

            public string Name
            {
                get
                {
                    fixed (char* p = Description) return new string(p, 0, 128).TrimEnd('\0');
                }
            }
        }

        /// DXGI_OUTPUT_DESC (96 bytes): the output's device name and the monitor it shows.
        [Native("DXGI_OUTPUT_DESC", Header = "dxgi.h")]
        [StructLayout(LayoutKind.Explicit, Size = 96)]
        public struct OutputDesc
        {
            [FieldOffset(0)] public fixed char DeviceName[32];
            [FieldOffset(88), As("HMONITOR", Opaque = true)] public IntPtr Monitor;

            public string Name
            {
                get
                {
                    fixed (char* p = DeviceName) return new string(p, 0, 32).TrimEnd('\0');
                }
            }
        }

        /// DISPLAYCONFIG_PATH_INFO (72 bytes): the source's adapter and id, the target's refresh rate.
        [Native("DISPLAYCONFIG_PATH_INFO", Header = "wingdi.h")]
        [StructLayout(LayoutKind.Explicit, Size = 72)]
        public struct DisplayConfigPath
        {
            [FieldOffset(0), Native("sourceInfo.adapterId")] public LUID SourceAdapter;
            [FieldOffset(8), Native("sourceInfo.id")] public uint SourceId;
            [FieldOffset(48), Native("targetInfo.refreshRate.Numerator")] public uint RefreshNumerator;
            [FieldOffset(52), Native("targetInfo.refreshRate.Denominator")] public uint RefreshDenominator;
        }

        /// DISPLAYCONFIG_DEVICE_INFO_HEADER: what DisplayConfigGetDeviceInfo takes, at the start of
        /// each request.
        [Native("DISPLAYCONFIG_DEVICE_INFO_HEADER", Header = "wingdi.h")]
        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigDeviceInfoHeader
        {
            [Native("type"), As("DISPLAYCONFIG_DEVICE_INFO_TYPE")] public int Type;
            [Native("size")] public uint Size;
            [Native("adapterId")] public LUID Adapter;
            [Native("id")] public uint Id;
        }

        /// DISPLAYCONFIG_SOURCE_DEVICE_NAME (84 bytes).
        [Native("DISPLAYCONFIG_SOURCE_DEVICE_NAME", Header = "wingdi.h")]
        [StructLayout(LayoutKind.Explicit, Size = 84)]
        public struct DisplayConfigSourceName
        {
            [FieldOffset(0), Native("header")] public DisplayConfigDeviceInfoHeader Header;
            [FieldOffset(20), Native("viewGdiDeviceName")] public fixed char GdiName[32];
        }

        [Native("QDC_ONLY_ACTIVE_PATHS", Header = "wingdi.h")] public const uint QdcOnlyActivePaths = 2;
        [Native("DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME", Header = "wingdi.h")] public const int DisplayConfigGetSourceName = 1;
        [NativeExpression("sizeof(DISPLAYCONFIG_PATH_INFO)"), Native(Header = "wingdi.h")] public const uint DisplayConfigPathInfoSize = 72;
        [NativeExpression("sizeof(DISPLAYCONFIG_MODE_INFO)"), Native(Header = "wingdi.h")] public const uint DisplayConfigModeInfoSize = 64;

        [DllImport("user32.dll"), Native(Header = "winuser.h"), As(AsAttribute.Return, "LONG")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, uint* paths, uint* modes);

        [DllImport("user32.dll"), Native(Header = "winuser.h"), As(AsAttribute.Return, "LONG"), As(4, "DISPLAYCONFIG_MODE_INFO*", Opaque = true), As(5, "DISPLAYCONFIG_TOPOLOGY_ID*")]
        public static extern int QueryDisplayConfig(uint flags, uint* pathCount, DisplayConfigPath* paths, uint* modeCount, void* modes, int* topology);

        [DllImport("user32.dll"), Native(Header = "winuser.h"), As(AsAttribute.Return, "LONG")]
        public static extern int DisplayConfigGetDeviceInfo(DisplayConfigDeviceInfoHeader* request);

        /// DXGI_QUERY_VIDEO_MEMORY_INFO.
        [Native("DXGI_QUERY_VIDEO_MEMORY_INFO", Header = "dxgi1_4.h")]
        [StructLayout(LayoutKind.Sequential)]
        public struct VideoMemoryInfo
        {
            public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
        }

        // ----- Entry points -----
        public static int CreateDXGIFactory2(uint flags, Guid* riid, void** factory) => DXGI.CreateDXGIFactory2(flags, riid, factory);

        public static int D3D11CreateDevice(void* adapter, int driverType, IntPtr software, uint flags, int* featureLevels, uint featureLevelCount,
            uint sdkVersion, void** device, int* featureLevel, void** immediateContext)
            => D3D11.D3D11CreateDevice((IDXGIAdapter*)adapter, (D3D_DRIVER_TYPE)driverType, software, (D3D11_CREATE_DEVICE_FLAG)flags, (D3D_FEATURE_LEVEL*)featureLevels,
                featureLevelCount, sdkVersion, (ID3D11Device**)device, (D3D_FEATURE_LEVEL*)featureLevel, (ID3D11DeviceContext**)immediateContext);

        [DllImport("kernel32.dll"), Native(Header = "synchapi.h"), As(AsAttribute.Return, "DWORD"), As(0, "HANDLE", Opaque = true), As(1, "DWORD"), As(2, "BOOL")]
        public static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, int alertable);

        [DllImport("kernel32.dll"), Native(Header = "handleapi.h"), As(AsAttribute.Return, "BOOL"), As(0, "HANDLE", Opaque = true)]
        public static extern int CloseHandle(IntPtr handle);

        [Native("WAIT_OBJECT_0", Header = "winbase.h")] public const uint WaitObject0 = 0;
        [Native("WAIT_TIMEOUT", Header = "winerror.h")] public const uint WaitTimeout = 0x102;

        /// The system timer's resolution for this process, in ms (0: done; TIMERR_NOCANDO
        /// otherwise); each timeBeginPeriod is matched by a timeEndPeriod.
        [DllImport("winmm.dll"), Native(Header = "timeapi.h"), As(AsAttribute.Return, "MMRESULT")]
        public static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll"), Native(Header = "timeapi.h"), As(AsAttribute.Return, "MMRESULT")]
        public static extern uint timeEndPeriod(uint milliseconds);

        // ----- Vtable calls (the slots: D3d11/DxgiInterfaces.cs, D3d11/D3d11.cs) -----

        public static int QueryInterface(void* o, in Guid iid, void** result)
        {
            fixed (Guid* g = &iid) return ((IUnknown*)o)->lpVtbl->QueryInterface((IUnknown*)o, g, result);
        }

        /// Releases the reference `o` holds, once: `o` is cleared first.
        public static void Release(ref void* o)
        {
            var p = (IUnknown*)o;
            o = null;
            if (p != null) p->lpVtbl->Release(p);
        }

        // IDXGIFactory1/2/4/5
        public static int EnumAdapters1(void* factory, uint index, void** adapter)
            => ((IDXGIFactory1*)factory)->lpVtbl->EnumAdapters1((IDXGIFactory1*)factory, index, (IDXGIAdapter1**)adapter);
        public static int MakeWindowAssociation(void* factory, IntPtr hwnd, uint flags)
            => ((IDXGIFactory*)factory)->lpVtbl->MakeWindowAssociation((IDXGIFactory*)factory, hwnd, flags);
        public static int CreateSwapChainForHwnd(void* factory, void* device, IntPtr hwnd, SwapChainDesc1* desc, void** swapChain)
            => ((IDXGIFactory2*)factory)->lpVtbl->CreateSwapChainForHwnd((IDXGIFactory2*)factory, (IUnknown*)device, hwnd, desc, null, null, (IDXGISwapChain1**)swapChain);
        public static int EnumAdapterByLuid(void* factory4, long luid, in Guid iid, void** adapter)
        {
            fixed (Guid* g = &iid) return ((IDXGIFactory4*)factory4)->lpVtbl->EnumAdapterByLuid((IDXGIFactory4*)factory4, LUID.Of(luid), g, adapter);
        }
        public static int CheckFeatureSupport(void* factory5, uint feature, void* data, uint size)
            => ((IDXGIFactory5*)factory5)->lpVtbl->CheckFeatureSupport((IDXGIFactory5*)factory5, feature, data, size);

        // IDXGIAdapter/1/3, IDXGIOutput
        public static int EnumOutputs(void* adapter, uint index, void** output)
            => ((IDXGIAdapter*)adapter)->lpVtbl->EnumOutputs((IDXGIAdapter*)adapter, index, (IDXGIOutput**)output);
        public static int GetDesc(void* output, OutputDesc* desc) => ((IDXGIOutput*)output)->lpVtbl->GetDesc((IDXGIOutput*)output, desc);
        public static int GetDesc1(void* adapter, AdapterDesc1* desc) => ((IDXGIAdapter1*)adapter)->lpVtbl->GetDesc1((IDXGIAdapter1*)adapter, desc);
        public static int QueryVideoMemoryInfo(void* adapter3, uint node, uint segmentGroup, VideoMemoryInfo* info)
            => ((IDXGIAdapter3*)adapter3)->lpVtbl->QueryVideoMemoryInfo((IDXGIAdapter3*)adapter3, node, segmentGroup, info);

        // IDXGISwapChain1/2
        public static int Present(void* swapChain, uint syncInterval, uint flags)
            => ((IDXGISwapChain*)swapChain)->lpVtbl->Present((IDXGISwapChain*)swapChain, syncInterval, flags);
        public static int GetBuffer(void* swapChain, uint index, in Guid iid, void** surface)
        {
            fixed (Guid* g = &iid) return ((IDXGISwapChain*)swapChain)->lpVtbl->GetBuffer((IDXGISwapChain*)swapChain, index, g, surface);
        }
        public static int ResizeBuffers(void* swapChain, uint count, uint width, uint height, uint format, uint flags)
            => ((IDXGISwapChain*)swapChain)->lpVtbl->ResizeBuffers((IDXGISwapChain*)swapChain, count, width, height, (DXGI_FORMAT)format, flags);
        public static int GetContainingOutput(void* swapChain, void** output)
            => ((IDXGISwapChain*)swapChain)->lpVtbl->GetContainingOutput((IDXGISwapChain*)swapChain, (IDXGIOutput**)output);
        public static int GetDesc1(void* swapChain, SwapChainDesc1* desc) => ((IDXGISwapChain1*)swapChain)->lpVtbl->GetDesc1((IDXGISwapChain1*)swapChain, desc);
        public static int SetMaximumFrameLatency(void* swapChain2, uint latency)
            => ((IDXGISwapChain2*)swapChain2)->lpVtbl->SetMaximumFrameLatency((IDXGISwapChain2*)swapChain2, latency);
        public static IntPtr GetFrameLatencyWaitableObject(void* swapChain2)
            => ((IDXGISwapChain2*)swapChain2)->lpVtbl->GetFrameLatencyWaitableObject((IDXGISwapChain2*)swapChain2);

        // IDXGIResource1 / IDXGIKeyedMutex
        public static int CreateSharedHandle(void* resource1, uint access, IntPtr* handle)
            => ((IDXGIResource1*)resource1)->lpVtbl->CreateSharedHandle((IDXGIResource1*)resource1, null, access, null, handle);
        public static int AcquireSync(void* keyedMutex, ulong key, uint milliseconds)
            => ((IDXGIKeyedMutex*)keyedMutex)->lpVtbl->AcquireSync((IDXGIKeyedMutex*)keyedMutex, key, milliseconds);
        public static int ReleaseSync(void* keyedMutex, ulong key) => ((IDXGIKeyedMutex*)keyedMutex)->lpVtbl->ReleaseSync((IDXGIKeyedMutex*)keyedMutex, key);

        // ID3D11Device / ID3D11DeviceContext
        public static int CreateTexture2D(void* device, Texture2DDesc* desc, void** texture)
            => ((ID3D11Device*)device)->lpVtbl->CreateTexture2D((ID3D11Device*)device, (D3D11_TEXTURE2D_DESC*)desc, null, (ID3D11Texture2D**)texture);
        public static int GetDeviceRemovedReason(void* device) => ((ID3D11Device*)device)->lpVtbl->GetDeviceRemovedReason((ID3D11Device*)device);
        public static void CopySubresourceRegion(void* context, void* destination, uint destinationSubresource, uint x, uint y, uint z, void* source, uint sourceSubresource, Box* box)
            => ((ID3D11DeviceContext*)context)->lpVtbl->CopySubresourceRegion((ID3D11DeviceContext*)context, (ID3D11Resource*)destination, destinationSubresource, x, y, z,
                (ID3D11Resource*)source, sourceSubresource, (D3D11_BOX*)box);
        public static void ClearState(void* context) => ((ID3D11DeviceContext*)context)->lpVtbl->ClearState((ID3D11DeviceContext*)context);
        public static void Flush(void* context) => ((ID3D11DeviceContext*)context)->lpVtbl->Flush((ID3D11DeviceContext*)context);

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

        /// An adapter as DXGI lists it.
        public readonly record struct Adapter(string Name, uint VendorId, uint DeviceId, long Luid, bool Software);

        /// The adapters DXGI lists, in its order (empty when DXGI can't list them).
        public static IReadOnlyList<Adapter> Adapters()
        {
            var found = new List<Adapter>();
            void* factory = null;
            try
            {
                var iid = IIDFactory2;
                if (Failed(CreateDXGIFactory2(0, &iid, &factory))) return found;
                for (uint i = 0; ; i++)
                {
                    void* a;
                    if (Failed(EnumAdapters1(factory, i, &a))) break;
                    AdapterDesc1 desc = default;
                    if (!Failed(GetDesc1(a, &desc))) found.Add(new Adapter(desc.Name, desc.VendorId, desc.DeviceId, desc.Luid, (desc.Flags & AdapterFlagSoftware) != 0));
                    Release(ref a);
                }
                return found;
            }
            finally
            {
                Release(ref factory);
            }
        }

        /// The calling process's video memory use on the adapter `luid` (local, then non-local)
        /// and the local budget Windows gives it (past it, Windows moves allocations to system
        /// memory), in bytes; null when DXGI can't tell. Any thread: it makes and releases its own objects.
        public static (ulong Local, ulong NonLocal, ulong LocalBudget)? ProcessVideoMemory(long luid)
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
                return (local.CurrentUsage, nonLocal.CurrentUsage, local.Budget);
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
