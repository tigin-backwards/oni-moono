// Lists every DXGI adapter and, for each hardware one, creates a Direct3D 11 device on it and
// prints its feature level and the CheckFeatureSupport answers a renderer's threading and
// constant-buffer design depend on. Research tool for the D3D11 plan (M0), not part of the
// product. Every struct, enum and flag is the Windows SDK's (d3d11.h, d3d11_4.h, dxgi1_6.h).
//   caps.exe
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <stdio.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

static const char *Level(D3D_FEATURE_LEVEL l)
{
    switch (l)
    {
    case D3D_FEATURE_LEVEL_12_2: return "12_2";
    case D3D_FEATURE_LEVEL_12_1: return "12_1";
    case D3D_FEATURE_LEVEL_12_0: return "12_0";
    case D3D_FEATURE_LEVEL_11_1: return "11_1";
    case D3D_FEATURE_LEVEL_11_0: return "11_0";
    case D3D_FEATURE_LEVEL_10_1: return "10_1";
    case D3D_FEATURE_LEVEL_10_0: return "10_0";
    default: return "other";
    }
}

template <typename T> static bool Feature(ID3D11Device *d, D3D11_FEATURE f, T &data, const char *name)
{
    ZeroMemory(&data, sizeof data);
    HRESULT hr = d->CheckFeatureSupport(f, &data, sizeof data);
    if (FAILED(hr)) printf("  %s: CheckFeatureSupport(%d) failed 0x%08lx\n", name, (int)f, (unsigned long)hr);
    return SUCCEEDED(hr);
}

static void Device(IDXGIAdapter1 *adapter, UINT flags)
{
    // The levels Unity's player could ask for; the device gets the highest the adapter has.
    const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
    ID3D11Device *d = nullptr;
    ID3D11DeviceContext *c = nullptr;
    D3D_FEATURE_LEVEL got = (D3D_FEATURE_LEVEL)0;
    HRESULT hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &d, &got, &c);
    printf(" device flags=0x%x: D3D11CreateDevice 0x%08lx level %s (0x%x)\n", flags, (unsigned long)hr, SUCCEEDED(hr) ? Level(got) : "-", (unsigned)got);
    if (FAILED(hr)) return;
    printf("  GetCreationFlags=0x%x\n", d->GetCreationFlags());

    D3D11_FEATURE_DATA_THREADING t;
    if (Feature(d, D3D11_FEATURE_THREADING, t, "THREADING"))
        printf("  THREADING DriverConcurrentCreates=%d DriverCommandLists=%d\n", t.DriverConcurrentCreates, t.DriverCommandLists);
    D3D11_FEATURE_DATA_D3D11_OPTIONS o;
    if (Feature(d, D3D11_FEATURE_D3D11_OPTIONS, o, "OPTIONS"))
        printf("  OPTIONS OutputMergerLogicOp=%d UAVOnlyRenderingForcedSampleCount=%d DiscardAPIsSeenByDriver=%d FlagsForUpdateAndCopySeenByDriver=%d ClearView=%d CopyWithOverlap=%d "
               "ConstantBufferPartialUpdate=%d ConstantBufferOffsetting=%d MapNoOverwriteOnDynamicConstantBuffer=%d MapNoOverwriteOnDynamicBufferSRV=%d "
               "MultisampleRTVWithForcedSampleCountOne=%d SAD4ShaderInstructions=%d ExtendedDoublesShaderInstructions=%d ExtendedResourceSharing=%d\n",
               o.OutputMergerLogicOp, o.UAVOnlyRenderingForcedSampleCount, o.DiscardAPIsSeenByDriver, o.FlagsForUpdateAndCopySeenByDriver, o.ClearView, o.CopyWithOverlap,
               o.ConstantBufferPartialUpdate, o.ConstantBufferOffsetting, o.MapNoOverwriteOnDynamicConstantBuffer, o.MapNoOverwriteOnDynamicBufferSRV,
               o.MultisampleRTVWithForcedSampleCountOne, o.SAD4ShaderInstructions, o.ExtendedDoublesShaderInstructions, o.ExtendedResourceSharing);
    D3D11_FEATURE_DATA_D3D11_OPTIONS1 o1;
    if (Feature(d, D3D11_FEATURE_D3D11_OPTIONS1, o1, "OPTIONS1"))
        printf("  OPTIONS1 TiledResourcesTier=%d MinMaxFiltering=%d ClearViewAlsoSupportsDepthOnlyFormats=%d MapOnDefaultBuffers=%d\n",
               (int)o1.TiledResourcesTier, o1.MinMaxFiltering, o1.ClearViewAlsoSupportsDepthOnlyFormats, o1.MapOnDefaultBuffers);
    D3D11_FEATURE_DATA_D3D11_OPTIONS2 o2;
    if (Feature(d, D3D11_FEATURE_D3D11_OPTIONS2, o2, "OPTIONS2"))
        printf("  OPTIONS2 PSSpecifiedStencilRefSupported=%d TypedUAVLoadAdditionalFormats=%d ROVsSupported=%d ConservativeRasterizationTier=%d TiledResourcesTier=%d "
               "MapOnDefaultTextures=%d StandardSwizzle=%d UnifiedMemoryArchitecture=%d\n",
               o2.PSSpecifiedStencilRefSupported, o2.TypedUAVLoadAdditionalFormats, o2.ROVsSupported, (int)o2.ConservativeRasterizationTier, (int)o2.TiledResourcesTier,
               o2.MapOnDefaultTextures, o2.StandardSwizzle, o2.UnifiedMemoryArchitecture);
    D3D11_FEATURE_DATA_D3D11_OPTIONS3 o3;
    if (Feature(d, D3D11_FEATURE_D3D11_OPTIONS3, o3, "OPTIONS3"))
        printf("  OPTIONS3 VPAndRTArrayIndexFromAnyShaderFeedingRasterizer=%d\n", o3.VPAndRTArrayIndexFromAnyShaderFeedingRasterizer);
    D3D11_FEATURE_DATA_D3D11_OPTIONS4 o4;
    if (Feature(d, D3D11_FEATURE_D3D11_OPTIONS4, o4, "OPTIONS4"))
        printf("  OPTIONS4 ExtendedNV12SharedTextureSupported=%d\n", o4.ExtendedNV12SharedTextureSupported);
    D3D11_FEATURE_DATA_D3D11_OPTIONS5 o5;
    if (Feature(d, D3D11_FEATURE_D3D11_OPTIONS5, o5, "OPTIONS5"))
        printf("  OPTIONS5 SharedResourceTier=%d\n", (int)o5.SharedResourceTier);
    D3D11_FEATURE_DATA_ARCHITECTURE_INFO a;
    if (Feature(d, D3D11_FEATURE_ARCHITECTURE_INFO, a, "ARCHITECTURE_INFO"))
        printf("  ARCHITECTURE_INFO TileBasedDeferredRenderer=%d\n", a.TileBasedDeferredRenderer);
    D3D11_FEATURE_DATA_DOUBLES db;
    if (Feature(d, D3D11_FEATURE_DOUBLES, db, "DOUBLES"))
        printf("  DOUBLES DoublePrecisionFloatShaderOps=%d\n", db.DoublePrecisionFloatShaderOps);

    // The format the original's depth targets use (D32_FLOAT_S8X24_UINT) and its typeless and
    // sampled views.
    const DXGI_FORMAT formats[] = {DXGI_FORMAT_D32_FLOAT_S8X24_UINT, DXGI_FORMAT_R32G8X24_TYPELESS, DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS, DXGI_FORMAT_D24_UNORM_S8_UINT, DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT_R16G16B16A16_FLOAT};
    for (DXGI_FORMAT f : formats)
    {
        UINT s = 0;
        HRESULT fh = d->CheckFormatSupport(f, &s);
        printf("  CheckFormatSupport(%d) 0x%08lx support=0x%08x\n", (int)f, (unsigned long)fh, s);
    }

    IDXGIDevice1 *dx = nullptr;
    if (SUCCEEDED(d->QueryInterface(__uuidof(IDXGIDevice1), (void **)&dx)))
    {
        UINT lat = 0;
        HRESULT lh = dx->GetMaximumFrameLatency(&lat);
        printf("  IDXGIDevice1::GetMaximumFrameLatency 0x%08lx %u\n", (unsigned long)lh, lat);
        dx->Release();
    }
    ID3D11Multithread *mt = nullptr;
    if (SUCCEEDED(c->QueryInterface(__uuidof(ID3D11Multithread), (void **)&mt)))
    {
        printf("  ID3D11Multithread::GetMultithreadProtected=%d\n", mt->GetMultithreadProtected());
        mt->Release();
    }
    c->Release();
    d->Release();
}

int main()
{
    IDXGIFactory1 *f = nullptr;
    HRESULT hr = CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void **)&f);
    if (FAILED(hr)) { printf("CreateDXGIFactory1 failed 0x%08lx\n", (unsigned long)hr); return 1; }
    IDXGIFactory5 *f5 = nullptr;
    if (SUCCEEDED(f->QueryInterface(__uuidof(IDXGIFactory5), (void **)&f5)))
    {
        BOOL tearing = FALSE;
        HRESULT th = f5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &tearing, sizeof tearing);
        printf("IDXGIFactory5::CheckFeatureSupport(PRESENT_ALLOW_TEARING) 0x%08lx %d\n", (unsigned long)th, tearing);
        f5->Release();
    }
    IDXGIAdapter1 *adapter = nullptr;
    for (UINT i = 0; f->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; i++)
    {
        DXGI_ADAPTER_DESC1 desc;
        adapter->GetDesc1(&desc);
        LARGE_INTEGER umd = {};
        HRESULT vh = adapter->CheckInterfaceSupport(__uuidof(IDXGIDevice), &umd);
        printf("adapter %u: %ls vendor=0x%04x device=0x%04x subsys=0x%08x rev=%u dedicatedVideo=%llu dedicatedSystem=%llu sharedSystem=%llu flags=0x%x luid=%08lx:%08lx umd=%s%u.%u.%u.%u\n",
               i, desc.Description, desc.VendorId, desc.DeviceId, desc.SubSysId, desc.Revision, (unsigned long long)desc.DedicatedVideoMemory,
               (unsigned long long)desc.DedicatedSystemMemory, (unsigned long long)desc.SharedSystemMemory, desc.Flags, (unsigned long)desc.AdapterLuid.HighPart,
               (unsigned long)desc.AdapterLuid.LowPart, SUCCEEDED(vh) ? "" : "(failed) ", HIWORD(umd.HighPart), LOWORD(umd.HighPart), HIWORD(umd.LowPart), LOWORD(umd.LowPart));
        if (!(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE))
        {
            // As the original's device (BGRA_SUPPORT only, d3d11-unity-probes.md §5), then with no flags.
            Device(adapter, D3D11_CREATE_DEVICE_BGRA_SUPPORT);
            Device(adapter, 0);
        }
        adapter->Release();
    }
    f->Release();
    return 0;
}
