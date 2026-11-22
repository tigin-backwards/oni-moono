using System;
using System.Runtime.InteropServices;

namespace OniMoono.Desktop.D3d11
{
    // The debug layer's interfaces (d3d11sdklayers.h), queried from a device created with
    // D3D11_CREATE_DEVICE_DEBUG: its stored messages and the live-object report at shutdown.

    [Native(Header = "d3d11sdklayers.h", Base = typeof(IUnknown))]
    internal unsafe struct ID3D11Debug
    {
        public static readonly Guid IID = new("79cf2233-7536-4948-9d36-1e4692dc5760");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(10 * 8)] public delegate* unmanaged<ID3D11Debug*, D3D11_RLDO_FLAGS, int> ReportLiveDeviceObjects;
        }
    }

    [Native(Header = "d3d11sdklayers.h", Base = typeof(IUnknown))]
    internal unsafe struct ID3D11InfoQueue
    {
        public static readonly Guid IID = new("6543dbb6-1b48-42f5-ab82-e97ec74326f6");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(4 * 8)] public delegate* unmanaged<ID3D11InfoQueue*, void> ClearStoredMessages;
            [FieldOffset(5 * 8)] public delegate* unmanaged<ID3D11InfoQueue*, ulong, D3D11_MESSAGE*, nuint*, int> GetMessage;
            [FieldOffset(8 * 8)] public delegate* unmanaged<ID3D11InfoQueue*, ulong> GetNumStoredMessages;
            [FieldOffset(9 * 8)] public delegate* unmanaged<ID3D11InfoQueue*, ulong> GetNumStoredMessagesAllowedByRetrievalFilter;
        }
    }

    [Native(Header = "d3d11sdklayers.h"), Flags]
    internal enum D3D11_RLDO_FLAGS
    {
        D3D11_RLDO_SUMMARY = 0x1,
        D3D11_RLDO_DETAIL = 0x2,
        D3D11_RLDO_IGNORE_INTERNAL = 0x4,
    }

    [Native(Header = "d3d11sdklayers.h")]
    internal enum D3D11_MESSAGE_SEVERITY
    {
        D3D11_MESSAGE_SEVERITY_CORRUPTION = 0,
        D3D11_MESSAGE_SEVERITY_ERROR = 1,
        D3D11_MESSAGE_SEVERITY_WARNING = 2,
        D3D11_MESSAGE_SEVERITY_INFO = 3,
        D3D11_MESSAGE_SEVERITY_MESSAGE = 4,
    }

    /// D3D11_MESSAGE: what GetMessage writes, the description after it in the same buffer.
    [Native(Header = "d3d11sdklayers.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_MESSAGE
    {
        [As("D3D11_MESSAGE_CATEGORY")] public int Category;
        public D3D11_MESSAGE_SEVERITY Severity;
        [As("D3D11_MESSAGE_ID")] public int ID;
        [As("const char*")] public sbyte* pDescription;
        public nuint DescriptionByteLength;
    }
}
