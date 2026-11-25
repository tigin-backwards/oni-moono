using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OniMoono.Desktop.D3d11
{
    // The Direct3D 11 API the D3D11 render device needs (docs/research/d3d11-plan.md): what the
    // original's device calls (docs/research/d3d11-captures.md §3 and §5) and what the device
    // inventory's checklist needs (docs/research/d3d11/device-inventory.md §2). Names, slots,
    // signatures, layouts and values are the Windows SDK's d3d11.h, d3dcommon.h and dxgiformat.h
    // (10.0.26100.0); tools/ComCheckGen checks every one against them with MSVC.

    /// D3D11CreateDevice and the constants and return codes the device uses.
    [Native(Header = "d3d11.h")]
    internal static unsafe class D3D11
    {
        [DllImport("d3d11.dll"), As(2, "HMODULE", Opaque = true), As(3, "UINT"), Const(4)]
        public static extern int D3D11CreateDevice(IDXGIAdapter* pAdapter, D3D_DRIVER_TYPE DriverType, IntPtr Software, D3D11_CREATE_DEVICE_FLAG Flags,
            D3D_FEATURE_LEVEL* pFeatureLevels, uint FeatureLevels, uint SDKVersion, ID3D11Device** ppDevice, D3D_FEATURE_LEVEL* pFeatureLevel,
            ID3D11DeviceContext** ppImmediateContext);

        public const uint D3D11_SDK_VERSION = 7;
        public const uint D3D11_APPEND_ALIGNED_ELEMENT = 0xffffffff;
        public const uint D3D11_DEFAULT_SAMPLE_MASK = 0xffffffff;
        public const uint D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT = 14;
        public const uint D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT = 128;
        public const uint D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT = 16;
        public const uint D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT = 32;
        public const uint D3D11_IA_VERTEX_INPUT_STRUCTURE_ELEMENT_COUNT = 32;
        public const uint D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT = 8;
        public const uint D3D11_REQ_CONSTANT_BUFFER_ELEMENT_COUNT = 4096;
        public const uint D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION = 16384;
        public const uint D3D11_REQ_TEXTURE3D_U_V_OR_W_DIMENSION = 2048;
        /// The size of a BOOL, as CheckFeatureSupport's PRESENT_ALLOW_TEARING answer takes it.
        [NativeExpression("sizeof(BOOL)"), Native(Header = "minwindef.h")] public const uint SizeOfBool = 4;

        [Native(Header = "winerror.h")] public const int S_FALSE = 1;
        [Native(Header = "winerror.h")] public const int E_INVALIDARG = unchecked((int)0x80070057);
        [Native(Header = "winerror.h")] public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
        [Native(Header = "winerror.h")] public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        [Native(Header = "winerror.h")] public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
        [Native(Header = "winerror.h")] public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
        [Native(Header = "winerror.h")] public const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int)0x887A000A);
        [Native(Header = "winerror.h")] public const int DXGI_ERROR_SDK_COMPONENT_MISSING = unchecked((int)0x887A002D);
    }

    // ----- Enums -----

    [Native(Header = "d3dcommon.h")]
    internal enum D3D_DRIVER_TYPE
    {
        D3D_DRIVER_TYPE_UNKNOWN = 0,
        D3D_DRIVER_TYPE_HARDWARE = 1,
        D3D_DRIVER_TYPE_WARP = 5,
    }

    [Native(Header = "d3dcommon.h")]
    internal enum D3D_FEATURE_LEVEL
    {
        D3D_FEATURE_LEVEL_10_0 = 0xa000,
        D3D_FEATURE_LEVEL_10_1 = 0xa100,
        D3D_FEATURE_LEVEL_11_0 = 0xb000,
        D3D_FEATURE_LEVEL_11_1 = 0xb100,
    }

    /// A signature element's system value (DXBC signatures store it: Dxbc.cs's SystemValue).
    [Native(Header = "d3dcommon.h")]
    internal enum D3D_NAME
    {
        D3D_NAME_UNDEFINED = 0,
        D3D_NAME_TARGET = 64,
    }

    /// A signature element's component type (Dxbc.cs's ComponentType).
    [Native(Header = "d3dcommon.h")]
    internal enum D3D_REGISTER_COMPONENT_TYPE
    {
        D3D_REGISTER_COMPONENT_UNKNOWN = 0,
        D3D_REGISTER_COMPONENT_UINT32 = 1,
        D3D_REGISTER_COMPONENT_SINT32 = 2,
        D3D_REGISTER_COMPONENT_FLOAT32 = 3,
    }

    [Native(Header = "d3d11.h"), Flags]
    internal enum D3D11_CREATE_DEVICE_FLAG
    {
        D3D11_CREATE_DEVICE_SINGLETHREADED = 0x1,
        D3D11_CREATE_DEVICE_DEBUG = 0x2,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20,
    }

    /// The formats a device's textures, render textures, depth surfaces, vertex inputs and
    /// index buffers use.
    [Native(Header = "dxgiformat.h")]
    internal enum DXGI_FORMAT
    {
        DXGI_FORMAT_UNKNOWN = 0,
        DXGI_FORMAT_R32G32B32A32_TYPELESS = 1,
        DXGI_FORMAT_R32G32B32A32_FLOAT = 2,
        DXGI_FORMAT_R32G32B32A32_UINT = 3,
        DXGI_FORMAT_R32G32B32A32_SINT = 4,
        DXGI_FORMAT_R32G32B32_FLOAT = 6,
        DXGI_FORMAT_R16G16B16A16_TYPELESS = 9,
        DXGI_FORMAT_R16G16B16A16_FLOAT = 10,
        DXGI_FORMAT_R16G16B16A16_UNORM = 11,
        DXGI_FORMAT_R32G32_FLOAT = 16,
        DXGI_FORMAT_R32G8X24_TYPELESS = 19,
        DXGI_FORMAT_D32_FLOAT_S8X24_UINT = 20,
        DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS = 21,
        DXGI_FORMAT_X32_TYPELESS_G8X24_UINT = 22,
        DXGI_FORMAT_R10G10B10A2_UNORM = 24,
        DXGI_FORMAT_R11G11B10_FLOAT = 26,
        DXGI_FORMAT_R8G8B8A8_TYPELESS = 27,
        DXGI_FORMAT_R8G8B8A8_UNORM = 28,
        DXGI_FORMAT_R8G8B8A8_UNORM_SRGB = 29,
        DXGI_FORMAT_R16G16_FLOAT = 34,
        DXGI_FORMAT_R16G16_UNORM = 35,
        DXGI_FORMAT_R32_TYPELESS = 39,
        DXGI_FORMAT_D32_FLOAT = 40,
        DXGI_FORMAT_R32_FLOAT = 41,
        DXGI_FORMAT_R32_UINT = 42,
        DXGI_FORMAT_R24G8_TYPELESS = 44,
        DXGI_FORMAT_D24_UNORM_S8_UINT = 45,
        DXGI_FORMAT_R24_UNORM_X8_TYPELESS = 46,
        DXGI_FORMAT_X24_TYPELESS_G8_UINT = 47,
        DXGI_FORMAT_R8G8_UNORM = 49,
        DXGI_FORMAT_R16_TYPELESS = 53,
        DXGI_FORMAT_R16_FLOAT = 54,
        DXGI_FORMAT_D16_UNORM = 55,
        DXGI_FORMAT_R16_UNORM = 56,
        DXGI_FORMAT_R16_UINT = 57,
        DXGI_FORMAT_R8_UNORM = 61,
        DXGI_FORMAT_A8_UNORM = 65,
        DXGI_FORMAT_BC1_UNORM = 71,
        DXGI_FORMAT_BC1_UNORM_SRGB = 72,
        DXGI_FORMAT_BC3_UNORM = 77,
        DXGI_FORMAT_BC3_UNORM_SRGB = 78,
        DXGI_FORMAT_BC4_UNORM = 80,
        DXGI_FORMAT_BC5_UNORM = 83,
        DXGI_FORMAT_B5G6R5_UNORM = 85,
        DXGI_FORMAT_B5G5R5A1_UNORM = 86,
        DXGI_FORMAT_B8G8R8A8_UNORM = 87,
        DXGI_FORMAT_B8G8R8A8_UNORM_SRGB = 91,
        DXGI_FORMAT_BC6H_UF16 = 95,
        DXGI_FORMAT_BC7_UNORM = 98,
        DXGI_FORMAT_B4G4R4A4_UNORM = 115,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_USAGE
    {
        D3D11_USAGE_DEFAULT = 0,
        D3D11_USAGE_IMMUTABLE = 1,
        D3D11_USAGE_DYNAMIC = 2,
        D3D11_USAGE_STAGING = 3,
    }

    [Native(Header = "d3d11.h"), Flags]
    internal enum D3D11_BIND_FLAG
    {
        D3D11_BIND_VERTEX_BUFFER = 0x1,
        D3D11_BIND_INDEX_BUFFER = 0x2,
        D3D11_BIND_CONSTANT_BUFFER = 0x4,
        D3D11_BIND_SHADER_RESOURCE = 0x8,
        D3D11_BIND_RENDER_TARGET = 0x20,
        D3D11_BIND_DEPTH_STENCIL = 0x40,
    }

    [Native(Header = "d3d11.h"), Flags]
    internal enum D3D11_CPU_ACCESS_FLAG
    {
        D3D11_CPU_ACCESS_WRITE = 0x10000,
        D3D11_CPU_ACCESS_READ = 0x20000,
    }

    [Native(Header = "d3d11.h"), Flags]
    internal enum D3D11_RESOURCE_MISC_FLAG
    {
        D3D11_RESOURCE_MISC_GENERATE_MIPS = 0x1,
        D3D11_RESOURCE_MISC_TEXTURECUBE = 0x4,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_MAP
    {
        D3D11_MAP_READ = 1,
        D3D11_MAP_WRITE = 2,
        D3D11_MAP_READ_WRITE = 3,
        D3D11_MAP_WRITE_DISCARD = 4,
        D3D11_MAP_WRITE_NO_OVERWRITE = 5,
    }

    [Native(Header = "d3d11.h"), Flags]
    internal enum D3D11_MAP_FLAG
    {
        D3D11_MAP_FLAG_DO_NOT_WAIT = 0x100000,
    }

    [Native(Header = "d3d11.h"), Flags]
    internal enum D3D11_FORMAT_SUPPORT
    {
        D3D11_FORMAT_SUPPORT_BUFFER = 0x1,
        D3D11_FORMAT_SUPPORT_IA_VERTEX_BUFFER = 0x2,
        D3D11_FORMAT_SUPPORT_IA_INDEX_BUFFER = 0x4,
        D3D11_FORMAT_SUPPORT_TEXTURE2D = 0x20,
        D3D11_FORMAT_SUPPORT_TEXTURE3D = 0x40,
        D3D11_FORMAT_SUPPORT_TEXTURECUBE = 0x80,
        D3D11_FORMAT_SUPPORT_SHADER_LOAD = 0x100,
        D3D11_FORMAT_SUPPORT_SHADER_SAMPLE = 0x200,
        D3D11_FORMAT_SUPPORT_SHADER_SAMPLE_COMPARISON = 0x400,
        D3D11_FORMAT_SUPPORT_MIP = 0x1000,
        D3D11_FORMAT_SUPPORT_MIP_AUTOGEN = 0x2000,
        D3D11_FORMAT_SUPPORT_RENDER_TARGET = 0x4000,
        D3D11_FORMAT_SUPPORT_BLENDABLE = 0x8000,
        D3D11_FORMAT_SUPPORT_DEPTH_STENCIL = 0x10000,
        D3D11_FORMAT_SUPPORT_CPU_LOCKABLE = 0x20000,
        D3D11_FORMAT_SUPPORT_MULTISAMPLE_RESOLVE = 0x40000,
        D3D11_FORMAT_SUPPORT_MULTISAMPLE_RENDERTARGET = 0x200000,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_FEATURE
    {
        D3D11_FEATURE_THREADING = 0,
        D3D11_FEATURE_D3D11_OPTIONS = 5,
    }

    [Native(Header = "d3dcommon.h")]
    internal enum D3D11_SRV_DIMENSION
    {
        D3D11_SRV_DIMENSION_UNKNOWN = 0,
        D3D11_SRV_DIMENSION_TEXTURE2D = 4,
        D3D11_SRV_DIMENSION_TEXTURE2DARRAY = 5,
        D3D11_SRV_DIMENSION_TEXTURE2DMS = 6,
        D3D11_SRV_DIMENSION_TEXTURE3D = 8,
        D3D11_SRV_DIMENSION_TEXTURECUBE = 9,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_RTV_DIMENSION
    {
        D3D11_RTV_DIMENSION_UNKNOWN = 0,
        D3D11_RTV_DIMENSION_TEXTURE2D = 4,
        D3D11_RTV_DIMENSION_TEXTURE2DARRAY = 5,
        D3D11_RTV_DIMENSION_TEXTURE2DMS = 6,
        D3D11_RTV_DIMENSION_TEXTURE3D = 8,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_DSV_DIMENSION
    {
        D3D11_DSV_DIMENSION_UNKNOWN = 0,
        D3D11_DSV_DIMENSION_TEXTURE2D = 3,
        D3D11_DSV_DIMENSION_TEXTURE2DARRAY = 4,
        D3D11_DSV_DIMENSION_TEXTURE2DMS = 5,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_INPUT_CLASSIFICATION
    {
        D3D11_INPUT_PER_VERTEX_DATA = 0,
        D3D11_INPUT_PER_INSTANCE_DATA = 1,
    }

    [Native(Header = "d3dcommon.h")]
    internal enum D3D11_PRIMITIVE_TOPOLOGY
    {
        D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED = 0,
        D3D11_PRIMITIVE_TOPOLOGY_POINTLIST = 1,
        D3D11_PRIMITIVE_TOPOLOGY_LINELIST = 2,
        D3D11_PRIMITIVE_TOPOLOGY_LINESTRIP = 3,
        D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST = 4,
        D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP = 5,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_BLEND
    {
        D3D11_BLEND_ZERO = 1,
        D3D11_BLEND_ONE = 2,
        D3D11_BLEND_SRC_COLOR = 3,
        D3D11_BLEND_INV_SRC_COLOR = 4,
        D3D11_BLEND_SRC_ALPHA = 5,
        D3D11_BLEND_INV_SRC_ALPHA = 6,
        D3D11_BLEND_DEST_ALPHA = 7,
        D3D11_BLEND_INV_DEST_ALPHA = 8,
        D3D11_BLEND_DEST_COLOR = 9,
        D3D11_BLEND_INV_DEST_COLOR = 10,
        D3D11_BLEND_SRC_ALPHA_SAT = 11,
        D3D11_BLEND_BLEND_FACTOR = 14,
        D3D11_BLEND_INV_BLEND_FACTOR = 15,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_BLEND_OP
    {
        D3D11_BLEND_OP_ADD = 1,
        D3D11_BLEND_OP_SUBTRACT = 2,
        D3D11_BLEND_OP_REV_SUBTRACT = 3,
        D3D11_BLEND_OP_MIN = 4,
        D3D11_BLEND_OP_MAX = 5,
    }

    [Native(Header = "d3d11.h"), Flags]
    internal enum D3D11_COLOR_WRITE_ENABLE
    {
        D3D11_COLOR_WRITE_ENABLE_RED = 1,
        D3D11_COLOR_WRITE_ENABLE_GREEN = 2,
        D3D11_COLOR_WRITE_ENABLE_BLUE = 4,
        D3D11_COLOR_WRITE_ENABLE_ALPHA = 8,
        D3D11_COLOR_WRITE_ENABLE_ALL = 15,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_COMPARISON_FUNC
    {
        D3D11_COMPARISON_NEVER = 1,
        D3D11_COMPARISON_LESS = 2,
        D3D11_COMPARISON_EQUAL = 3,
        D3D11_COMPARISON_LESS_EQUAL = 4,
        D3D11_COMPARISON_GREATER = 5,
        D3D11_COMPARISON_NOT_EQUAL = 6,
        D3D11_COMPARISON_GREATER_EQUAL = 7,
        D3D11_COMPARISON_ALWAYS = 8,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_DEPTH_WRITE_MASK
    {
        D3D11_DEPTH_WRITE_MASK_ZERO = 0,
        D3D11_DEPTH_WRITE_MASK_ALL = 1,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_STENCIL_OP
    {
        D3D11_STENCIL_OP_KEEP = 1,
        D3D11_STENCIL_OP_ZERO = 2,
        D3D11_STENCIL_OP_REPLACE = 3,
        D3D11_STENCIL_OP_INCR_SAT = 4,
        D3D11_STENCIL_OP_DECR_SAT = 5,
        D3D11_STENCIL_OP_INVERT = 6,
        D3D11_STENCIL_OP_INCR = 7,
        D3D11_STENCIL_OP_DECR = 8,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_FILL_MODE
    {
        D3D11_FILL_WIREFRAME = 2,
        D3D11_FILL_SOLID = 3,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_CULL_MODE
    {
        D3D11_CULL_NONE = 1,
        D3D11_CULL_FRONT = 2,
        D3D11_CULL_BACK = 3,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_FILTER
    {
        D3D11_FILTER_MIN_MAG_MIP_POINT = 0,
        D3D11_FILTER_MIN_MAG_POINT_MIP_LINEAR = 0x1,
        D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT = 0x14,
        D3D11_FILTER_MIN_MAG_MIP_LINEAR = 0x15,
        D3D11_FILTER_ANISOTROPIC = 0x55,
        D3D11_FILTER_COMPARISON_MIN_MAG_MIP_POINT = 0x80,
        D3D11_FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT = 0x94,
        D3D11_FILTER_COMPARISON_MIN_MAG_MIP_LINEAR = 0x95,
        D3D11_FILTER_COMPARISON_ANISOTROPIC = 0xd5,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_TEXTURE_ADDRESS_MODE
    {
        D3D11_TEXTURE_ADDRESS_WRAP = 1,
        D3D11_TEXTURE_ADDRESS_MIRROR = 2,
        D3D11_TEXTURE_ADDRESS_CLAMP = 3,
        D3D11_TEXTURE_ADDRESS_BORDER = 4,
        D3D11_TEXTURE_ADDRESS_MIRROR_ONCE = 5,
    }

    [Native(Header = "d3d11.h")]
    internal enum D3D11_QUERY
    {
        D3D11_QUERY_EVENT = 0,
        D3D11_QUERY_OCCLUSION = 1,
        D3D11_QUERY_TIMESTAMP = 2,
        D3D11_QUERY_TIMESTAMP_DISJOINT = 3,
    }

    [Native(Header = "d3d11.h"), Flags]
    internal enum D3D11_ASYNC_GETDATA_FLAG
    {
        D3D11_ASYNC_GETDATA_DONOTFLUSH = 0x1,
    }

    [Native(Header = "d3d11.h"), Flags]
    internal enum D3D11_CLEAR_FLAG
    {
        D3D11_CLEAR_DEPTH = 0x1,
        D3D11_CLEAR_STENCIL = 0x2,
    }

    // ----- Structs -----

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_BUFFER_DESC
    {
        public uint ByteWidth;
        public D3D11_USAGE Usage;
        [As("UINT")] public D3D11_BIND_FLAG BindFlags;
        [As("UINT")] public D3D11_CPU_ACCESS_FLAG CPUAccessFlags;
        [As("UINT")] public D3D11_RESOURCE_MISC_FLAG MiscFlags;
        public uint StructureByteStride;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_SUBRESOURCE_DATA
    {
        [As("const void*")] public void* pSysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_MAPPED_SUBRESOURCE
    {
        public void* pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [Native(Header = "dxgicommon.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public DXGI_FORMAT Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public D3D11_USAGE Usage;
        [As("UINT")] public D3D11_BIND_FLAG BindFlags;
        [As("UINT")] public D3D11_CPU_ACCESS_FLAG CPUAccessFlags;
        [As("UINT")] public D3D11_RESOURCE_MISC_FLAG MiscFlags;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEXTURE3D_DESC
    {
        public uint Width;
        public uint Height;
        public uint Depth;
        public uint MipLevels;
        public DXGI_FORMAT Format;
        public D3D11_USAGE Usage;
        [As("UINT")] public D3D11_BIND_FLAG BindFlags;
        [As("UINT")] public D3D11_CPU_ACCESS_FLAG CPUAccessFlags;
        [As("UINT")] public D3D11_RESOURCE_MISC_FLAG MiscFlags;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_BOX
    {
        public uint left, top, front, right, bottom, back;
    }

    /// D3D11_SHADER_RESOURCE_VIEW_DESC: Format and ViewDimension, then a union of the views the
    /// device makes (2D, 2D array, 2D multisampled, 3D, cube).
    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct D3D11_SHADER_RESOURCE_VIEW_DESC
    {
        [FieldOffset(0)] public DXGI_FORMAT Format;
        [FieldOffset(4)] public D3D11_SRV_DIMENSION ViewDimension;
        [FieldOffset(8)] public D3D11_TEX2D_SRV Texture2D;
        [FieldOffset(8)] public D3D11_TEX2D_ARRAY_SRV Texture2DArray;
        [FieldOffset(8)] public D3D11_TEX2DMS_SRV Texture2DMS;
        [FieldOffset(8)] public D3D11_TEX3D_SRV Texture3D;
        [FieldOffset(8)] public D3D11_TEXCUBE_SRV TextureCube;
    }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX2D_SRV { public uint MostDetailedMip, MipLevels; }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX2D_ARRAY_SRV { public uint MostDetailedMip, MipLevels, FirstArraySlice, ArraySize; }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX2DMS_SRV { public uint UnusedField_NothingToDefine; }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX3D_SRV { public uint MostDetailedMip, MipLevels; }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEXCUBE_SRV { public uint MostDetailedMip, MipLevels; }

    /// D3D11_RENDER_TARGET_VIEW_DESC: the views the device makes (2D, 2D array, 2D multisampled, 3D).
    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct D3D11_RENDER_TARGET_VIEW_DESC
    {
        [FieldOffset(0)] public DXGI_FORMAT Format;
        [FieldOffset(4)] public D3D11_RTV_DIMENSION ViewDimension;
        [FieldOffset(8)] public D3D11_TEX2D_RTV Texture2D;
        [FieldOffset(8)] public D3D11_TEX2D_ARRAY_RTV Texture2DArray;
        [FieldOffset(8)] public D3D11_TEX2DMS_RTV Texture2DMS;
        [FieldOffset(8)] public D3D11_TEX3D_RTV Texture3D;
    }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX2D_RTV { public uint MipSlice; }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX2D_ARRAY_RTV { public uint MipSlice, FirstArraySlice, ArraySize; }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX2DMS_RTV { public uint UnusedField_NothingToDefine; }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX3D_RTV { public uint MipSlice, FirstWSlice, WSize; }

    /// D3D11_DEPTH_STENCIL_VIEW_DESC: the views the device makes (2D, 2D array, 2D multisampled).
    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct D3D11_DEPTH_STENCIL_VIEW_DESC
    {
        [FieldOffset(0)] public DXGI_FORMAT Format;
        [FieldOffset(4)] public D3D11_DSV_DIMENSION ViewDimension;
        [FieldOffset(8)] public uint Flags;
        [FieldOffset(12)] public D3D11_TEX2D_DSV Texture2D;
        [FieldOffset(12)] public D3D11_TEX2D_ARRAY_DSV Texture2DArray;
        [FieldOffset(12)] public D3D11_TEX2DMS_DSV Texture2DMS;
    }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX2D_DSV { public uint MipSlice; }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX2D_ARRAY_DSV { public uint MipSlice, FirstArraySlice, ArraySize; }

    [Native(Header = "d3d11.h"), StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEX2DMS_DSV { public uint UnusedField_NothingToDefine; }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_INPUT_ELEMENT_DESC
    {
        [As("LPCSTR")] public sbyte* SemanticName;
        public uint SemanticIndex;
        public DXGI_FORMAT Format;
        public uint InputSlot;
        public uint AlignedByteOffset;
        public D3D11_INPUT_CLASSIFICATION InputSlotClass;
        public uint InstanceDataStepRate;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_RENDER_TARGET_BLEND_DESC
    {
        public int BlendEnable;
        public D3D11_BLEND SrcBlend;
        public D3D11_BLEND DestBlend;
        public D3D11_BLEND_OP BlendOp;
        public D3D11_BLEND SrcBlendAlpha;
        public D3D11_BLEND DestBlendAlpha;
        public D3D11_BLEND_OP BlendOpAlpha;
        public byte RenderTargetWriteMask;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_BLEND_DESC
    {
        public int AlphaToCoverageEnable;
        public int IndependentBlendEnable;
        public RenderTargets RenderTarget;

        [InlineArray(8)]
        public struct RenderTargets
        {
            D3D11_RENDER_TARGET_BLEND_DESC element;
        }
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_DEPTH_STENCILOP_DESC
    {
        public D3D11_STENCIL_OP StencilFailOp;
        public D3D11_STENCIL_OP StencilDepthFailOp;
        public D3D11_STENCIL_OP StencilPassOp;
        public D3D11_COMPARISON_FUNC StencilFunc;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_DEPTH_STENCIL_DESC
    {
        public int DepthEnable;
        public D3D11_DEPTH_WRITE_MASK DepthWriteMask;
        public D3D11_COMPARISON_FUNC DepthFunc;
        public int StencilEnable;
        public byte StencilReadMask;
        public byte StencilWriteMask;
        public D3D11_DEPTH_STENCILOP_DESC FrontFace;
        public D3D11_DEPTH_STENCILOP_DESC BackFace;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_RASTERIZER_DESC
    {
        public D3D11_FILL_MODE FillMode;
        public D3D11_CULL_MODE CullMode;
        public int FrontCounterClockwise;
        public int DepthBias;
        public float DepthBiasClamp;
        public float SlopeScaledDepthBias;
        public int DepthClipEnable;
        public int ScissorEnable;
        public int MultisampleEnable;
        public int AntialiasedLineEnable;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_SAMPLER_DESC
    {
        public D3D11_FILTER Filter;
        public D3D11_TEXTURE_ADDRESS_MODE AddressU;
        public D3D11_TEXTURE_ADDRESS_MODE AddressV;
        public D3D11_TEXTURE_ADDRESS_MODE AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy;
        public D3D11_COMPARISON_FUNC ComparisonFunc;
        public fixed float BorderColor[4];
        public float MinLOD;
        public float MaxLOD;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_QUERY_DESC
    {
        public D3D11_QUERY Query;
        public uint MiscFlags;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_QUERY_DATA_TIMESTAMP_DISJOINT
    {
        public ulong Frequency;
        public int Disjoint;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIEWPORT
    {
        public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
    }

    /// D3D11_RECT (RECT, windef.h).
    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_RECT
    {
        [As("LONG")] public int left;
        [As("LONG")] public int top;
        [As("LONG")] public int right;
        [As("LONG")] public int bottom;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_FEATURE_DATA_THREADING
    {
        public int DriverConcurrentCreates;
        public int DriverCommandLists;
    }

    [Native(Header = "d3d11.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_FEATURE_DATA_D3D11_OPTIONS
    {
        public int OutputMergerLogicOp;
        public int UAVOnlyRenderingForcedSampleCount;
        public int DiscardAPIsSeenByDriver;
        public int FlagsForUpdateAndCopySeenByDriver;
        public int ClearView;
        public int CopyWithOverlap;
        public int ConstantBufferPartialUpdate;
        public int ConstantBufferOffsetting;
        public int MapNoOverwriteOnDynamicConstantBuffer;
        public int MapNoOverwriteOnDynamicBufferSRV;
        public int MultisampleRTVWithForcedSampleCountOne;
        public int SAD4ShaderInstructions;
        public int ExtendedDoublesShaderInstructions;
        public int ExtendedResourceSharing;
    }

    // ----- Interfaces -----
    // Each is a pointer type: `lpVtbl` first, as the header's C struct. Bases are the C++ classes'
    // (checked with std::is_base_of), so a pointer may be passed as any of them.

    [Native(Header = "d3d11.h", Base = typeof(IUnknown))]
    internal unsafe struct ID3D11Device
    {
        public static readonly Guid IID = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(3 * 8), Const(1, 2)] public delegate* unmanaged<ID3D11Device*, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, ID3D11Buffer**, int> CreateBuffer;
            [FieldOffset(5 * 8), Const(1, 2)] public delegate* unmanaged<ID3D11Device*, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, ID3D11Texture2D**, int> CreateTexture2D;
            [FieldOffset(6 * 8), Const(1, 2)] public delegate* unmanaged<ID3D11Device*, D3D11_TEXTURE3D_DESC*, D3D11_SUBRESOURCE_DATA*, ID3D11Texture3D**, int> CreateTexture3D;
            [FieldOffset(7 * 8), Const(2)] public delegate* unmanaged<ID3D11Device*, ID3D11Resource*, D3D11_SHADER_RESOURCE_VIEW_DESC*, ID3D11ShaderResourceView**, int> CreateShaderResourceView;
            [FieldOffset(9 * 8), Const(2)] public delegate* unmanaged<ID3D11Device*, ID3D11Resource*, D3D11_RENDER_TARGET_VIEW_DESC*, ID3D11RenderTargetView**, int> CreateRenderTargetView;
            [FieldOffset(10 * 8), Const(2)] public delegate* unmanaged<ID3D11Device*, ID3D11Resource*, D3D11_DEPTH_STENCIL_VIEW_DESC*, ID3D11DepthStencilView**, int> CreateDepthStencilView;
            [FieldOffset(11 * 8), Const(1, 3)] public delegate* unmanaged<ID3D11Device*, D3D11_INPUT_ELEMENT_DESC*, uint, void*, nuint, ID3D11InputLayout**, int> CreateInputLayout;
            [FieldOffset(12 * 8), Const(1)] public delegate* unmanaged<ID3D11Device*, void*, nuint, ID3D11ClassLinkage*, ID3D11VertexShader**, int> CreateVertexShader;
            [FieldOffset(13 * 8), Const(1)] public delegate* unmanaged<ID3D11Device*, void*, nuint, ID3D11ClassLinkage*, ID3D11GeometryShader**, int> CreateGeometryShader;
            [FieldOffset(15 * 8), Const(1)] public delegate* unmanaged<ID3D11Device*, void*, nuint, ID3D11ClassLinkage*, ID3D11PixelShader**, int> CreatePixelShader;
            [FieldOffset(20 * 8), Const(1)] public delegate* unmanaged<ID3D11Device*, D3D11_BLEND_DESC*, ID3D11BlendState**, int> CreateBlendState;
            [FieldOffset(21 * 8), Const(1)] public delegate* unmanaged<ID3D11Device*, D3D11_DEPTH_STENCIL_DESC*, ID3D11DepthStencilState**, int> CreateDepthStencilState;
            [FieldOffset(22 * 8), Const(1)] public delegate* unmanaged<ID3D11Device*, D3D11_RASTERIZER_DESC*, ID3D11RasterizerState**, int> CreateRasterizerState;
            [FieldOffset(23 * 8), Const(1)] public delegate* unmanaged<ID3D11Device*, D3D11_SAMPLER_DESC*, ID3D11SamplerState**, int> CreateSamplerState;
            [FieldOffset(24 * 8), Const(1)] public delegate* unmanaged<ID3D11Device*, D3D11_QUERY_DESC*, ID3D11Query**, int> CreateQuery;
            [FieldOffset(29 * 8), As(2, "UINT*")] public delegate* unmanaged<ID3D11Device*, DXGI_FORMAT, D3D11_FORMAT_SUPPORT*, int> CheckFormatSupport;
            [FieldOffset(30 * 8)] public delegate* unmanaged<ID3D11Device*, DXGI_FORMAT, uint, uint*, int> CheckMultisampleQualityLevels;
            [FieldOffset(33 * 8)] public delegate* unmanaged<ID3D11Device*, D3D11_FEATURE, void*, uint, int> CheckFeatureSupport;
            [FieldOffset(37 * 8)] public delegate* unmanaged<ID3D11Device*, D3D_FEATURE_LEVEL> GetFeatureLevel;
            [FieldOffset(38 * 8)] public delegate* unmanaged<ID3D11Device*, uint> GetCreationFlags;
            [FieldOffset(39 * 8)] public delegate* unmanaged<ID3D11Device*, int> GetDeviceRemovedReason;
            [FieldOffset(40 * 8)] public delegate* unmanaged<ID3D11Device*, ID3D11DeviceContext**, void> GetImmediateContext;
        }
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11DeviceContext
    {
        public static readonly Guid IID = new("c0bfa96c-e089-44fb-8eaf-26f8796190da");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(7 * 8), Const(3)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, ID3D11Buffer**, void> VSSetConstantBuffers;
            [FieldOffset(8 * 8), Const(3)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, ID3D11ShaderResourceView**, void> PSSetShaderResources;
            [FieldOffset(9 * 8), Const(2)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11PixelShader*, ID3D11ClassInstance**, uint, void> PSSetShader;
            [FieldOffset(10 * 8), Const(3)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, ID3D11SamplerState**, void> PSSetSamplers;
            [FieldOffset(11 * 8), Const(2)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11VertexShader*, ID3D11ClassInstance**, uint, void> VSSetShader;
            [FieldOffset(12 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, int, void> DrawIndexed;
            [FieldOffset(13 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, void> Draw;
            [FieldOffset(14 * 8), As(4, "UINT")] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11Resource*, uint, D3D11_MAP, D3D11_MAP_FLAG, D3D11_MAPPED_SUBRESOURCE*, int> Map;
            [FieldOffset(15 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11Resource*, uint, void> Unmap;
            [FieldOffset(16 * 8), Const(3)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, ID3D11Buffer**, void> PSSetConstantBuffers;
            [FieldOffset(17 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11InputLayout*, void> IASetInputLayout;
            [FieldOffset(18 * 8), Const(3, 4, 5)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, ID3D11Buffer**, uint*, uint*, void> IASetVertexBuffers;
            [FieldOffset(19 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11Buffer*, DXGI_FORMAT, uint, void> IASetIndexBuffer;
            [FieldOffset(20 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, uint, int, uint, void> DrawIndexedInstanced;
            [FieldOffset(21 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, uint, uint, void> DrawInstanced;
            [FieldOffset(22 * 8), Const(3)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, ID3D11Buffer**, void> GSSetConstantBuffers;
            [FieldOffset(23 * 8), Const(2)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11GeometryShader*, ID3D11ClassInstance**, uint, void> GSSetShader;
            [FieldOffset(24 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, D3D11_PRIMITIVE_TOPOLOGY, void> IASetPrimitiveTopology;
            [FieldOffset(25 * 8), Const(3)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, ID3D11ShaderResourceView**, void> VSSetShaderResources;
            [FieldOffset(26 * 8), Const(3)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, ID3D11SamplerState**, void> VSSetSamplers;
            [FieldOffset(27 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11Asynchronous*, void> Begin;
            [FieldOffset(28 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11Asynchronous*, void> End;
            [FieldOffset(29 * 8), As(4, "UINT")] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11Asynchronous*, void*, uint, D3D11_ASYNC_GETDATA_FLAG, int> GetData;
            [FieldOffset(31 * 8), Const(3)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, ID3D11ShaderResourceView**, void> GSSetShaderResources;
            [FieldOffset(32 * 8), Const(3)] public delegate* unmanaged<ID3D11DeviceContext*, uint, uint, ID3D11SamplerState**, void> GSSetSamplers;
            [FieldOffset(33 * 8), Const(2)] public delegate* unmanaged<ID3D11DeviceContext*, uint, ID3D11RenderTargetView**, ID3D11DepthStencilView*, void> OMSetRenderTargets;
            [FieldOffset(35 * 8), Const(2)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11BlendState*, float*, uint, void> OMSetBlendState;
            [FieldOffset(36 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11DepthStencilState*, uint, void> OMSetDepthStencilState;
            [FieldOffset(43 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11RasterizerState*, void> RSSetState;
            [FieldOffset(44 * 8), Const(2)] public delegate* unmanaged<ID3D11DeviceContext*, uint, D3D11_VIEWPORT*, void> RSSetViewports;
            [FieldOffset(45 * 8), Const(2)] public delegate* unmanaged<ID3D11DeviceContext*, uint, D3D11_RECT*, void> RSSetScissorRects;
            [FieldOffset(46 * 8), Const(8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11Resource*, uint, uint, uint, uint, ID3D11Resource*, uint, D3D11_BOX*, void> CopySubresourceRegion;
            [FieldOffset(47 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11Resource*, ID3D11Resource*, void> CopyResource;
            [FieldOffset(48 * 8), Const(3, 4)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11Resource*, uint, D3D11_BOX*, void*, uint, uint, void> UpdateSubresource;
            [FieldOffset(50 * 8), Const(2)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11RenderTargetView*, float*, void> ClearRenderTargetView;
            [FieldOffset(53 * 8), As(2, "UINT")] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11DepthStencilView*, D3D11_CLEAR_FLAG, float, byte, void> ClearDepthStencilView;
            [FieldOffset(54 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11ShaderResourceView*, void> GenerateMips;
            [FieldOffset(57 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, ID3D11Resource*, uint, ID3D11Resource*, uint, DXGI_FORMAT, void> ResolveSubresource;
            [FieldOffset(110 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, void> ClearState;
            [FieldOffset(111 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, void> Flush;
        }
    }

    [Native(Header = "d3d11.h", Base = typeof(IUnknown))]
    internal unsafe struct ID3D11DeviceChild
    {
        public static readonly Guid IID = new("1841e5c8-16b0-489b-bcc8-44cfb0d5deae");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11Resource
    {
        public static readonly Guid IID = new("dc8e63f3-d12b-4952-b47b-5e45026a862d");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11Resource))]
    internal unsafe struct ID3D11Buffer
    {
        public static readonly Guid IID = new("48570b85-d1ee-4fcd-a250-eb350722b037");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11Resource))]
    internal unsafe struct ID3D11Texture2D
    {
        public static readonly Guid IID = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(10 * 8)] public delegate* unmanaged<ID3D11Texture2D*, D3D11_TEXTURE2D_DESC*, void> GetDesc;
        }
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11Resource))]
    internal unsafe struct ID3D11Texture3D
    {
        public static readonly Guid IID = new("037e866e-f56d-4357-a8af-9dabbe6e250e");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11View
    {
        public static readonly Guid IID = new("839d1216-bb2e-412b-b7f4-a9dbebe08ed1");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11View))]
    internal unsafe struct ID3D11ShaderResourceView
    {
        public static readonly Guid IID = new("b0e06fe0-8192-4e1a-b1ca-36d7414710b2");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11View))]
    internal unsafe struct ID3D11RenderTargetView
    {
        public static readonly Guid IID = new("dfdba067-0b8d-4865-875b-d7b4516cc164");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11View))]
    internal unsafe struct ID3D11DepthStencilView
    {
        public static readonly Guid IID = new("9fdac92a-1876-48c3-afad-25b94f84a9b6");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11InputLayout
    {
        public static readonly Guid IID = new("e4819ddc-4cf0-4025-bd26-5de82a3e07b7");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11VertexShader
    {
        public static readonly Guid IID = new("3b301d64-d678-4289-8897-22f8928b72f3");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11GeometryShader
    {
        public static readonly Guid IID = new("38325b96-effb-4022-ba02-2e795b70275c");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11PixelShader
    {
        public static readonly Guid IID = new("ea82e40d-51dc-4f33-93d4-db7c9125ae8c");
        public void* lpVtbl;
    }

    /// Only ever passed as null (no class linkage in the shipped programs).
    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11ClassLinkage
    {
        public static readonly Guid IID = new("ddf57cba-9543-46e4-a12b-f207a0fe7fed");
        public void* lpVtbl;
    }

    /// Only ever passed as null (no class instances in the shipped programs).
    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11ClassInstance
    {
        public static readonly Guid IID = new("a6cd7faa-b0b7-4a2f-9436-8662a65797cb");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11BlendState
    {
        public static readonly Guid IID = new("75b68faa-347d-4159-8f45-a0640f01cd9a");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11DepthStencilState
    {
        public static readonly Guid IID = new("03823efb-8d8f-4e1c-9aa2-f64bb2cbfdf1");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11RasterizerState
    {
        public static readonly Guid IID = new("9bb4ab81-ab1a-4d8f-b506-fc04200b6ee7");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11SamplerState
    {
        public static readonly Guid IID = new("da6fea51-564c-4487-9810-f0d0f9b4e3a5");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11DeviceChild))]
    internal unsafe struct ID3D11Asynchronous
    {
        public static readonly Guid IID = new("4b35d0cd-1e15-4258-9c98-1b1333f6dd3b");
        public void* lpVtbl;
    }

    [Native(Header = "d3d11.h", Base = typeof(ID3D11Asynchronous))]
    internal unsafe struct ID3D11Query
    {
        public static readonly Guid IID = new("d6c00747-87b7-425e-b84d-44d108560afd");
        public void* lpVtbl;
    }
}
