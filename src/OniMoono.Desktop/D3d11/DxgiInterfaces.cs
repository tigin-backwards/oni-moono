using System;
using System.Runtime.InteropServices;
using OniMoono.Desktop.Gl;

namespace OniMoono.Desktop.D3d11
{
    // The DXGI interfaces the presenter (Gl/Dxgi.cs, which also has the DXGI structs) and the
    // D3D11 device call, with only the methods they call. Slots are the headers' C vtables'
    // (inherited methods first); each is checked against them by tools/ComCheckGen.

    /// DXGI's entry point (dxgi1_3.h).
    [Native(Header = "dxgi1_3.h")]
    internal static unsafe class DXGI
    {
        [DllImport("dxgi.dll"), As(1, "REFIID")]
        public static extern int CreateDXGIFactory2(uint Flags, Guid* riid, void** ppFactory);
    }

    [Native(Header = "dxgi.h", Base = typeof(IUnknown))]
    internal unsafe struct IDXGIFactory
    {
        public static readonly Guid IID = new("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(8 * 8), As(1, "HWND", Opaque = true)] public delegate* unmanaged<IDXGIFactory*, IntPtr, uint, int> MakeWindowAssociation;
        }
    }

    [Native(Header = "dxgi.h", Base = typeof(IDXGIFactory))]
    internal unsafe struct IDXGIFactory1
    {
        public static readonly Guid IID = new("770aae78-f26f-4dba-a829-253c83d1b387");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(12 * 8)] public delegate* unmanaged<IDXGIFactory1*, uint, IDXGIAdapter1**, int> EnumAdapters1;
        }
    }

    [Native(Header = "dxgi1_2.h", Base = typeof(IDXGIFactory1))]
    internal unsafe struct IDXGIFactory2
    {
        public static readonly Guid IID = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(15 * 8), As(2, "HWND", Opaque = true), Const(3), As(4, "const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*", Opaque = true)]
            public delegate* unmanaged<IDXGIFactory2*, IUnknown*, IntPtr, Dxgi.SwapChainDesc1*, void*, IDXGIOutput*, IDXGISwapChain1**, int> CreateSwapChainForHwnd;
        }
    }

    [Native(Header = "dxgi1_4.h", Base = typeof(IDXGIFactory2))]
    internal unsafe struct IDXGIFactory4
    {
        public static readonly Guid IID = new("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(26 * 8), As(2, "REFIID")] public delegate* unmanaged<IDXGIFactory4*, LUID, Guid*, void**, int> EnumAdapterByLuid;
        }
    }

    [Native(Header = "dxgi1_5.h", Base = typeof(IDXGIFactory4))]
    internal unsafe struct IDXGIFactory5
    {
        public static readonly Guid IID = new("7632e1f5-ee65-4dca-87fd-84cd75f8838d");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(28 * 8), As(1, "DXGI_FEATURE")] public delegate* unmanaged<IDXGIFactory5*, uint, void*, uint, int> CheckFeatureSupport;
        }
    }

    [Native(Header = "dxgi.h", Base = typeof(IUnknown))]
    internal unsafe struct IDXGIAdapter
    {
        public static readonly Guid IID = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(7 * 8)] public delegate* unmanaged<IDXGIAdapter*, uint, IDXGIOutput**, int> EnumOutputs;
        }
    }

    [Native(Header = "dxgi.h", Base = typeof(IDXGIAdapter))]
    internal unsafe struct IDXGIAdapter1
    {
        public static readonly Guid IID = new("29038f61-3839-4626-91fd-086879011a05");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(10 * 8)] public delegate* unmanaged<IDXGIAdapter1*, Dxgi.AdapterDesc1*, int> GetDesc1;
        }
    }

    [Native(Header = "dxgi1_4.h", Base = typeof(IDXGIAdapter1))]
    internal unsafe struct IDXGIAdapter3
    {
        public static readonly Guid IID = new("645967a4-1392-4310-a798-8053ce3e93fd");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(14 * 8), As(2, "DXGI_MEMORY_SEGMENT_GROUP")]
            public delegate* unmanaged<IDXGIAdapter3*, uint, uint, Dxgi.VideoMemoryInfo*, int> QueryVideoMemoryInfo;
        }
    }

    [Native(Header = "dxgi.h", Base = typeof(IUnknown))]
    internal unsafe struct IDXGIOutput
    {
        public static readonly Guid IID = new("ae02eedb-c735-4690-8d52-5a8dc20213aa");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(7 * 8)] public delegate* unmanaged<IDXGIOutput*, Dxgi.OutputDesc*, int> GetDesc;
        }
    }

    [Native(Header = "dxgi.h", Base = typeof(IUnknown))]
    internal unsafe struct IDXGISwapChain
    {
        public static readonly Guid IID = new("310d36a0-d2e7-4c0a-aa04-6a9d23b8886a");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(8 * 8)] public delegate* unmanaged<IDXGISwapChain*, uint, uint, int> Present;
            [FieldOffset(9 * 8), As(2, "REFIID")] public delegate* unmanaged<IDXGISwapChain*, uint, Guid*, void**, int> GetBuffer;
            [FieldOffset(13 * 8)] public delegate* unmanaged<IDXGISwapChain*, uint, uint, uint, DXGI_FORMAT, uint, int> ResizeBuffers;
            [FieldOffset(15 * 8)] public delegate* unmanaged<IDXGISwapChain*, IDXGIOutput**, int> GetContainingOutput;
        }
    }

    [Native(Header = "dxgi1_2.h", Base = typeof(IDXGISwapChain))]
    internal unsafe struct IDXGISwapChain1
    {
        public static readonly Guid IID = new("790a45f7-0d42-4876-983a-0a55cfe6f4aa");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(18 * 8)] public delegate* unmanaged<IDXGISwapChain1*, Dxgi.SwapChainDesc1*, int> GetDesc1;
        }
    }

    [Native(Header = "dxgi1_3.h", Base = typeof(IDXGISwapChain1))]
    internal unsafe struct IDXGISwapChain2
    {
        public static readonly Guid IID = new("a8be2ac4-199f-4946-b331-79599fb98de7");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(31 * 8)] public delegate* unmanaged<IDXGISwapChain2*, uint, int> SetMaximumFrameLatency;
            [FieldOffset(33 * 8), As(AsAttribute.Return, "HANDLE", Opaque = true)] public delegate* unmanaged<IDXGISwapChain2*, IntPtr> GetFrameLatencyWaitableObject;
        }
    }

    [Native(Header = "dxgi1_2.h", Base = typeof(IUnknown))]
    internal unsafe struct IDXGIResource1
    {
        public static readonly Guid IID = new("30961379-4609-4a41-998e-54fe567ee0c1");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(13 * 8), As(1, "const SECURITY_ATTRIBUTES*", Opaque = true), As(2, "DWORD"), Const(3), As(4, "HANDLE*", Opaque = true)]
            public delegate* unmanaged<IDXGIResource1*, void*, uint, char*, IntPtr*, int> CreateSharedHandle;
        }
    }

    [Native(Header = "dxgi.h", Base = typeof(IUnknown))]
    internal unsafe struct IDXGIKeyedMutex
    {
        public static readonly Guid IID = new("9d8e1289-d7b3-465f-8126-250e349af85d");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(8 * 8), As(2, "DWORD")] public delegate* unmanaged<IDXGIKeyedMutex*, ulong, uint, int> AcquireSync;
            [FieldOffset(9 * 8)] public delegate* unmanaged<IDXGIKeyedMutex*, ulong, int> ReleaseSync;
        }
    }
}
