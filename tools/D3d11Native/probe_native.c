// Native half of UnityProbe's `rdcapture` scenario (tools/UnityProbe/RdCapture.cs), loaded by
// the probe into the ORIGINAL game's process. Research tool for the D3D11 plan (M0), not part of
// the product.
// - RenderDoc's in-application API (renderdoc_app.h, shipped with RenderDoc): when renderdoc.dll
//   is already in the process, its RENDERDOC_GetAPI gives TriggerCapture, Start/EndFrameCapture
//   and the capture list. No supported launch puts it there: `renderdoccmd capture` bypassed the
//   run's isolation (j0008144) and is not used; RdCapture.cs calls pn_rd_init only on an explicit
//   opt-in with a capture path inside the run home.
// - A rendering-event callback for CommandBuffer.IssuePluginEvent (Unity calls it on its render
//   thread with the event id): 1 starts a RenderDoc capture, 2 ends it, 100+n logs the immediate
//   context's rasterizer, depth-stencil and blend state and viewport after the previous draw.
// - The swap chain the player presents with: DXGI's swap chain objects share one vtable per class,
//   so a swap chain made here on a hidden window gives the vtable, whose Present, Present1 and
//   ResizeBuffers slots are pointed at hooks that log each call's arguments and, once per swap
//   chain, its descriptions.
//   Slots come from the SDK headers' C vtables (offsetof), not numbers.
// - ctx_hook.c: a one-frame recorder of the context's calls (counts, state changes, per-draw
//   state).
// Every struct and enum is the Windows SDK's (10.0.26100.0).
#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#define CINTERFACE
#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_6.h>
#include <stddef.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdbool.h>
#include "renderdoc_app.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "dxguid.lib")

#define EXPORT __declspec(dllexport)

static CRITICAL_SECTION lock;
static char logbuf[16 << 20];
static void FrameBoundary(void);
static size_t loglen;

static void Log(const char *fmt, ...)
{
    char line[2048];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(line, sizeof line, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n >= (int)sizeof line) n = (int)sizeof line - 1;
    EnterCriticalSection(&lock);
    if (loglen + (size_t)n + 2 < sizeof logbuf)
    {
        memcpy(logbuf + loglen, line, (size_t)n);
        loglen += (size_t)n;
        logbuf[loglen++] = '\n';
        logbuf[loglen] = 0;
    }
    LeaveCriticalSection(&lock);
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID r)
{
    (void)h; (void)r;
    if (reason == DLL_PROCESS_ATTACH) InitializeCriticalSection(&lock);
    return TRUE;
}

/// Moves the log gathered so far into buf (NUL-terminated); returns the bytes copied.
EXPORT int pn_log(char *buf, int len)
{
    EnterCriticalSection(&lock);
    int n = (int)loglen < len - 1 ? (int)loglen : len - 1;
    memcpy(buf, logbuf, (size_t)n);
    buf[n] = 0;
    memmove(logbuf, logbuf + n, loglen - (size_t)n);
    loglen -= (size_t)n;
    LeaveCriticalSection(&lock);
    return n;
}

// ---- RenderDoc ----

static RENDERDOC_API_1_6_0 *rd;

/// 1 when renderdoc.dll is loaded in the process.
EXPORT int pn_rd_present(void) { return GetModuleHandleA("renderdoc.dll") != NULL; }

/// The in-application API of an injected renderdoc.dll: its version (e.g. 10600), 0 when the
/// module is not loaded, -1 when RENDERDOC_GetAPI refuses 1.6.0.
EXPORT int pn_rd_init(const char *template_path)
{
    HMODULE m = GetModuleHandleA("renderdoc.dll");
    if (!m) return 0;
    pRENDERDOC_GetAPI get = (pRENDERDOC_GetAPI)(void *)GetProcAddress(m, "RENDERDOC_GetAPI");
    if (!get || get(eRENDERDOC_API_Version_1_6_0, (void **)&rd) != 1 || !rd) return -1;
    if (template_path && *template_path) rd->SetCaptureFilePathTemplate(template_path);
    int major = 0, minor = 0, patch = 0;
    rd->GetAPIVersion(&major, &minor, &patch);
    return major * 10000 + minor * 100 + patch;
}

EXPORT void pn_rd_trigger(void) { if (rd) rd->TriggerCapture(); }

/// The captures written so far, one "index path timestamp" line each, into buf.
EXPORT int pn_rd_captures(char *buf, int len)
{
    int used = 0;
    buf[0] = 0;
    if (!rd) return 0;
    uint32_t n = rd->GetNumCaptures();
    for (uint32_t i = 0; i < n; i++)
    {
        char path[1024];
        uint32_t plen = sizeof path;
        uint64_t ts = 0;
        if (!rd->GetCapture(i, path, &plen, &ts)) continue;
        int w = _snprintf_s(buf + used, (size_t)(len - used), _TRUNCATE, "%u %s %llu\n", i, path, (unsigned long long)ts);
        if (w < 0) break;
        used += w;
    }
    return used;
}

static char title[256];
EXPORT void pn_rd_title(const char *t) { strncpy_s(title, sizeof title, t, _TRUNCATE); }

// ---- The device ----

static ID3D11Device *device;
static ID3D11DeviceContext *context;

/// Takes the device from a texture's native pointer (an ID3D11Resource on Direct3D 11).
EXPORT int pn_device(void *native_texture)
{
    ID3D11Resource *r = (ID3D11Resource *)native_texture;
    if (!r) return 0;
    ID3D11Resource_GetDevice(r, &device);
    if (!device) return 0;
    ID3D11Device_GetImmediateContext(device, &context);
    IDXGIDevice1 *dx = NULL;
    if (SUCCEEDED(ID3D11Device_QueryInterface(device, &IID_IDXGIDevice1, (void **)&dx)))
    {
        UINT lat = 0;
        HRESULT hr = IDXGIDevice1_GetMaximumFrameLatency(dx, &lat);
        Log("DEV IDXGIDevice1::GetMaximumFrameLatency hr=0x%08lx latency=%u", (unsigned long)hr, lat);
        INT prio = 0;
        hr = IDXGIDevice1_GetGPUThreadPriority(dx, &prio);
        Log("DEV IDXGIDevice::GetGPUThreadPriority hr=0x%08lx priority=%d", (unsigned long)hr, prio);
        IDXGIDevice1_Release(dx);
    }
    Log("DEV level=0x%x flags=0x%x", (unsigned)ID3D11Device_GetFeatureLevel(device), ID3D11Device_GetCreationFlags(device));
    return 1;
}

static void LogState(int tag)
{
    if (!context) { Log("STATE %d no device", tag); return; }
    ID3D11RasterizerState *rs = NULL;
    ID3D11DeviceContext_RSGetState(context, &rs);
    if (rs)
    {
        D3D11_RASTERIZER_DESC d;
        ID3D11RasterizerState_GetDesc(rs, &d);
        Log("STATE %d RS fill=%d cull=%d frontCCW=%d depthBias=%d depthBiasClamp=%.9g slopeScaledDepthBias=%.9g depthClip=%d scissor=%d msaa=%d aaLine=%d",
            tag, (int)d.FillMode, (int)d.CullMode, d.FrontCounterClockwise, d.DepthBias, d.DepthBiasClamp, d.SlopeScaledDepthBias, d.DepthClipEnable,
            d.ScissorEnable, d.MultisampleEnable, d.AntialiasedLineEnable);
        ID3D11RasterizerState_Release(rs);
    }
    else Log("STATE %d RS null (the default state)", tag);
    ID3D11DepthStencilState *ds = NULL;
    UINT ref = 0;
    ID3D11DeviceContext_OMGetDepthStencilState(context, &ds, &ref);
    if (ds)
    {
        D3D11_DEPTH_STENCIL_DESC d;
        ID3D11DepthStencilState_GetDesc(ds, &d);
        Log("STATE %d DS depth=%d write=%d func=%d stencil=%d read=0x%x write=0x%x ref=%u front=%d/%d/%d/%d back=%d/%d/%d/%d", tag, d.DepthEnable, (int)d.DepthWriteMask,
            (int)d.DepthFunc, d.StencilEnable, d.StencilReadMask, d.StencilWriteMask, ref, (int)d.FrontFace.StencilFailOp, (int)d.FrontFace.StencilDepthFailOp,
            (int)d.FrontFace.StencilPassOp, (int)d.FrontFace.StencilFunc, (int)d.BackFace.StencilFailOp, (int)d.BackFace.StencilDepthFailOp, (int)d.BackFace.StencilPassOp,
            (int)d.BackFace.StencilFunc);
        ID3D11DepthStencilState_Release(ds);
    }
    else Log("STATE %d DS null", tag);
    ID3D11BlendState *bs = NULL;
    FLOAT factor[4];
    UINT mask = 0;
    ID3D11DeviceContext_OMGetBlendState(context, &bs, factor, &mask);
    if (bs)
    {
        D3D11_BLEND_DESC d;
        ID3D11BlendState_GetDesc(bs, &d);
        const D3D11_RENDER_TARGET_BLEND_DESC *t = &d.RenderTarget[0];
        Log("STATE %d BS alphaToCoverage=%d independent=%d rt0 enable=%d src=%d dst=%d op=%d srcA=%d dstA=%d opA=%d mask=0x%x sampleMask=0x%x", tag, d.AlphaToCoverageEnable,
            d.IndependentBlendEnable, t->BlendEnable, (int)t->SrcBlend, (int)t->DestBlend, (int)t->BlendOp, (int)t->SrcBlendAlpha, (int)t->DestBlendAlpha, (int)t->BlendOpAlpha,
            t->RenderTargetWriteMask, mask);
        ID3D11BlendState_Release(bs);
    }
    else Log("STATE %d BS null", tag);
    D3D11_VIEWPORT vp[2];
    UINT nvp = 2;
    ID3D11DeviceContext_RSGetViewports(context, &nvp, vp);
    if (nvp) Log("STATE %d VP n=%u (%g, %g, %g, %g) depth %g..%g", tag, nvp, vp[0].TopLeftX, vp[0].TopLeftY, vp[0].Width, vp[0].Height, vp[0].MinDepth, vp[0].MaxDepth);
    ID3D11DepthStencilView *dsv = NULL;
    ID3D11RenderTargetView *rtv = NULL;
    ID3D11DeviceContext_OMGetRenderTargets(context, 1, &rtv, &dsv);
    if (dsv)
    {
        D3D11_DEPTH_STENCIL_VIEW_DESC d;
        ID3D11DepthStencilView_GetDesc(dsv, &d);
        Log("STATE %d DSV format=%d dimension=%d flags=0x%x", tag, (int)d.Format, (int)d.ViewDimension, d.Flags);
        ID3D11DepthStencilView_Release(dsv);
    }
    else Log("STATE %d DSV null", tag);
    if (rtv)
    {
        D3D11_RENDER_TARGET_VIEW_DESC d;
        ID3D11RenderTargetView_GetDesc(rtv, &d);
        Log("STATE %d RTV format=%d dimension=%d", tag, (int)d.Format, (int)d.ViewDimension);
        ID3D11RenderTargetView_Release(rtv);
    }
}

static void __stdcall OnRenderEvent(int id)
{
    if (id == 1)
    {
        if (rd) { rd->StartFrameCapture(NULL, NULL); if (title[0]) rd->SetCaptureTitle(title); }
        Log("EVENT start capture (renderdoc %s)", rd ? "present" : "absent");
    }
    else if (id == 2)
    {
        uint32_t ok = rd ? rd->EndFrameCapture(NULL, NULL) : 0;
        Log("EVENT end capture -> %u", ok);
    }
    else if (id >= 100) LogState(id - 100);
}

/// The UnityRenderingEvent for CommandBuffer.IssuePluginEvent.
EXPORT void *pn_render_event_func(void) { return (void *)OnRenderEvent; }

// ---- The swap chain's Present ----

typedef HRESULT(STDMETHODCALLTYPE *PresentFn)(IDXGISwapChain *, UINT, UINT);
typedef HRESULT(STDMETHODCALLTYPE *Present1Fn)(IDXGISwapChain1 *, UINT, UINT, const DXGI_PRESENT_PARAMETERS *);
typedef HRESULT(STDMETHODCALLTYPE *ResizeFn)(IDXGISwapChain *, UINT, UINT, UINT, DXGI_FORMAT, UINT);

static void **vtables[4];
static int nvtables;
static PresentFn present0;
static Present1Fn present1;
static ResizeFn resize0;
static void *seen[8];
static int nseen;
#define KINDS 16
static struct { void *sc; UINT sync, flags, which; DWORD thread; long count; } kinds[KINDS];
static long presents;
static LARGE_INTEGER firstPresent, lastPresent;

static void Describe(IDXGISwapChain *sc)
{
    DXGI_SWAP_CHAIN_DESC d;
    HRESULT hr = IDXGISwapChain_GetDesc(sc, &d);
    Log("SC %p GetDesc hr=0x%08lx %ux%u format=%d refresh=%u/%u scanline=%d scaling=%d sample=%u/%u usage=0x%x buffers=%u hwnd=%p windowed=%d swapEffect=%d flags=0x%x", (void *)sc,
        (unsigned long)hr, d.BufferDesc.Width, d.BufferDesc.Height, (int)d.BufferDesc.Format, d.BufferDesc.RefreshRate.Numerator, d.BufferDesc.RefreshRate.Denominator,
        (int)d.BufferDesc.ScanlineOrdering, (int)d.BufferDesc.Scaling, d.SampleDesc.Count, d.SampleDesc.Quality, d.BufferUsage, d.BufferCount, (void *)d.OutputWindow, d.Windowed,
        (int)d.SwapEffect, d.Flags);
    IDXGISwapChain1 *s1 = NULL;
    if (SUCCEEDED(IDXGISwapChain_QueryInterface(sc, &IID_IDXGISwapChain1, (void **)&s1)))
    {
        DXGI_SWAP_CHAIN_DESC1 e;
        hr = IDXGISwapChain1_GetDesc1(s1, &e);
        Log("SC %p GetDesc1 hr=0x%08lx %ux%u format=%d stereo=%d sample=%u/%u usage=0x%x buffers=%u scaling=%d swapEffect=%d alphaMode=%d flags=0x%x", (void *)sc, (unsigned long)hr,
            e.Width, e.Height, (int)e.Format, e.Stereo, e.SampleDesc.Count, e.SampleDesc.Quality, e.BufferUsage, e.BufferCount, (int)e.Scaling, (int)e.SwapEffect, (int)e.AlphaMode,
            e.Flags);
        DXGI_SWAP_CHAIN_FULLSCREEN_DESC f;
        hr = IDXGISwapChain1_GetFullscreenDesc(s1, &f);
        Log("SC %p GetFullscreenDesc hr=0x%08lx refresh=%u/%u scanline=%d scaling=%d windowed=%d", (void *)sc, (unsigned long)hr, f.RefreshRate.Numerator, f.RefreshRate.Denominator,
            (int)f.ScanlineOrdering, (int)f.Scaling, f.Windowed);
        IDXGISwapChain1_Release(s1);
    }
    else Log("SC %p no IDXGISwapChain1", (void *)sc);
    static const IID *const ifaces[] = {&IID_IDXGISwapChain2, &IID_IDXGISwapChain3, &IID_IDXGISwapChain4};
    for (int i = 0; i < 3; i++)
    {
        IUnknown *u = NULL;
        hr = IDXGISwapChain_QueryInterface(sc, ifaces[i], (void **)&u);
        Log("SC %p QueryInterface(IDXGISwapChain%d) hr=0x%08lx", (void *)sc, i + 2, (unsigned long)hr);
        if (u)
        {
            if (i == 0)
            {
                UINT lat = 0;
                HRESULT lh = IDXGISwapChain2_GetMaximumFrameLatency((IDXGISwapChain2 *)u, &lat);
                Log("SC %p IDXGISwapChain2::GetMaximumFrameLatency hr=0x%08lx latency=%u", (void *)sc, (unsigned long)lh, lat);
            }
            IUnknown_Release(u);
        }
    }
    BOOL fs = FALSE;
    IDXGIOutput *out = NULL;
    hr = IDXGISwapChain_GetFullscreenState(sc, &fs, &out);
    Log("SC %p GetFullscreenState hr=0x%08lx fullscreen=%d", (void *)sc, (unsigned long)hr, fs);
    if (out) IDXGIOutput_Release(out);
    ID3D11Texture2D *bb = NULL;
    if (SUCCEEDED(IDXGISwapChain_GetBuffer(sc, 0, &IID_ID3D11Texture2D, (void **)&bb)))
    {
        D3D11_TEXTURE2D_DESC t;
        ID3D11Texture2D_GetDesc(bb, &t);
        Log("SC %p buffer0 %ux%u format=%d mips=%u array=%u sample=%u usage=%d bind=0x%x cpu=0x%x misc=0x%x", (void *)sc, t.Width, t.Height, (int)t.Format, t.MipLevels, t.ArraySize,
            t.SampleDesc.Count, (int)t.Usage, t.BindFlags, t.CPUAccessFlags, t.MiscFlags);
        ID3D11Texture2D_Release(bb);
    }
    IDXGIFactory5 *f5 = NULL;
    if (SUCCEEDED(IDXGISwapChain_GetParent(sc, &IID_IDXGIFactory5, (void **)&f5)))
    {
        BOOL tearing = FALSE;
        hr = IDXGIFactory5_CheckFeatureSupport(f5, DXGI_FEATURE_PRESENT_ALLOW_TEARING, &tearing, sizeof tearing);
        Log("SC %p factory CheckFeatureSupport(PRESENT_ALLOW_TEARING) hr=0x%08lx %d", (void *)sc, (unsigned long)hr, tearing);
        IDXGIFactory5_Release(f5);
    }
}

static long kindsOverflow;

static void Count(void *sc, UINT sync, UINT flags, UINT which)
{
    int describe = 0;
    DWORD thread = GetCurrentThreadId();
    EnterCriticalSection(&lock);
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    if (presents++ == 0) firstPresent = now;
    lastPresent = now;
    int i;
    for (i = 0; i < nseen && seen[i] != sc; i++) {}
    if (i == nseen && nseen < 8) { seen[nseen++] = sc; describe = 1; }
    for (i = 0; i < KINDS; i++)
    {
        if (kinds[i].count && kinds[i].sc == sc && kinds[i].sync == sync && kinds[i].flags == flags && kinds[i].which == which && kinds[i].thread == thread) { kinds[i].count++; break; }
        if (!kinds[i].count) { kinds[i].sc = sc; kinds[i].sync = sync; kinds[i].flags = flags; kinds[i].which = which; kinds[i].thread = thread; kinds[i].count = 1; break; }
    }
    if (i == KINDS) kindsOverflow++;
    LeaveCriticalSection(&lock);
    if (describe)
    {
        Log("PRESENT first call on %p via Present%s sync=%u flags=0x%x thread=%lu", sc, which ? "1" : "", sync, flags, GetCurrentThreadId());
        Describe((IDXGISwapChain *)sc);
    }
}

static HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain *sc, UINT sync, UINT flags)
{
    Count(sc, sync, flags, 0);
    FrameBoundary();
    return present0(sc, sync, flags);
}

static HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1 *sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS *p)
{
    Count(sc, sync, flags, 1);
    FrameBoundary();
    return present1(sc, sync, flags, p);
}

static HRESULT STDMETHODCALLTYPE HookResize(IDXGISwapChain *sc, UINT count, UINT w, UINT h, DXGI_FORMAT f, UINT flags)
{
    HRESULT hr = resize0(sc, count, w, h, f, flags);
    Log("RESIZE %p ResizeBuffers(count=%u, %ux%u, format=%d, flags=0x%x) hr=0x%08lx", (void *)sc, count, w, h, (int)f, flags, (unsigned long)hr);
    return hr;
}

static void Patch(void **vt, size_t slot, void *hook, void **orig)
{
    DWORD old;
    if (!VirtualProtect(&vt[slot], sizeof(void *), PAGE_EXECUTE_READWRITE, &old)) { Log("HOOK VirtualProtect failed %lu", GetLastError()); return; }
    if (vt[slot] != hook)
    {
        if (!*orig) *orig = vt[slot];
        else if (*orig != vt[slot]) Log("HOOK slot %zu of vtable %p holds %p, not the first vtable's %p", slot, (void *)vt, vt[slot], *orig);
        vt[slot] = hook;
    }
    VirtualProtect(&vt[slot], sizeof(void *), old, &old);
    FlushInstructionCache(GetCurrentProcess(), &vt[slot], sizeof(void *));
}

static void AddVtable(IDXGISwapChain *sc, const char *how)
{
    void **vt = *(void ***)sc;
    Log("HOOK swap chain made by %s: vtable %p", how, (void *)vt);
    for (int i = 0; i < nvtables; i++)
        if (vtables[i] == vt) return;
    if (nvtables < 4) vtables[nvtables++] = vt;
}

/// Makes swap chains on a hidden window with the player's factory and hooks their class's
/// Present, Present1 and ResizeBuffers. 1 when hooked.
EXPORT int pn_sc_hook(void)
{
    if (!device) { Log("HOOK no device"); return 0; }
    IDXGIDevice *dx = NULL;
    IDXGIAdapter *adapter = NULL;
    IDXGIFactory2 *factory = NULL;
    if (FAILED(ID3D11Device_QueryInterface(device, &IID_IDXGIDevice, (void **)&dx)) || FAILED(IDXGIDevice_GetAdapter(dx, &adapter)) ||
        FAILED(IDXGIAdapter_GetParent(adapter, &IID_IDXGIFactory2, (void **)&factory)))
    {
        Log("HOOK no factory");
        return 0;
    }
    DXGI_ADAPTER_DESC ad;
    IDXGIAdapter_GetDesc(adapter, &ad);
    Log("HOOK adapter %ls vendor=0x%04x device=0x%04x", ad.Description, ad.VendorId, ad.DeviceId);
    // Never shown: no ShowWindow, so the window never appears and never takes the focus.
    HWND w = CreateWindowExW(0, L"STATIC", L"probe", WS_POPUP, 0, 0, 64, 64, NULL, NULL, GetModuleHandleW(NULL), NULL);
    if (!w) { Log("HOOK CreateWindowEx failed %lu", GetLastError()); return 0; }
    DXGI_SWAP_CHAIN_DESC1 d1 = {0};
    d1.Width = 64; d1.Height = 64; d1.Format = DXGI_FORMAT_R8G8B8A8_UNORM; d1.SampleDesc.Count = 1;
    d1.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; d1.BufferCount = 2; d1.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    IDXGISwapChain1 *a = NULL;
    HRESULT hr = IDXGIFactory2_CreateSwapChainForHwnd(factory, (IUnknown *)device, w, &d1, NULL, NULL, &a);
    Log("HOOK CreateSwapChainForHwnd(flip discard) hr=0x%08lx", (unsigned long)hr);
    if (a) { AddVtable((IDXGISwapChain *)a, "CreateSwapChainForHwnd flip"); IDXGISwapChain1_Release(a); a = NULL; }
    d1.SwapEffect = DXGI_SWAP_EFFECT_DISCARD; d1.BufferCount = 1;
    hr = IDXGIFactory2_CreateSwapChainForHwnd(factory, (IUnknown *)device, w, &d1, NULL, NULL, &a);
    Log("HOOK CreateSwapChainForHwnd(blt discard) hr=0x%08lx", (unsigned long)hr);
    if (a) { AddVtable((IDXGISwapChain *)a, "CreateSwapChainForHwnd blt"); IDXGISwapChain1_Release(a); a = NULL; }
    DXGI_SWAP_CHAIN_DESC d0 = {0};
    d0.BufferDesc.Width = 64; d0.BufferDesc.Height = 64; d0.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; d0.SampleDesc.Count = 1;
    d0.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; d0.BufferCount = 1; d0.OutputWindow = w; d0.Windowed = TRUE; d0.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    IDXGISwapChain *b = NULL;
    hr = IDXGIFactory2_CreateSwapChain(factory, (IUnknown *)device, &d0, &b);
    Log("HOOK CreateSwapChain(legacy) hr=0x%08lx", (unsigned long)hr);
    if (b) { AddVtable(b, "CreateSwapChain"); IDXGISwapChain_Release(b); }
    DestroyWindow(w);
    IDXGIFactory2_Release(factory);
    IDXGIAdapter_Release(adapter);
    IDXGIDevice_Release(dx);
    const size_t sPresent = offsetof(IDXGISwapChain1Vtbl, Present) / sizeof(void *);
    const size_t sPresent1 = offsetof(IDXGISwapChain1Vtbl, Present1) / sizeof(void *);
    const size_t sResize = offsetof(IDXGISwapChain1Vtbl, ResizeBuffers) / sizeof(void *);
    Log("HOOK slots Present=%zu Present1=%zu ResizeBuffers=%zu, %d vtable(s)", sPresent, sPresent1, sResize, nvtables);
    for (int i = 0; i < nvtables; i++)
    {
        Patch(vtables[i], sPresent, (void *)HookPresent, (void **)&present0);
        Patch(vtables[i], sPresent1, (void *)HookPresent1, (void **)&present1);
        Patch(vtables[i], sResize, (void *)HookResize, (void **)&resize0);
    }
    return nvtables > 0;
}

/// Logs the Present calls counted so far by swap chain, sync interval, flags and entry point.
EXPORT void pn_sc_report(void)
{
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    EnterCriticalSection(&lock);
    long n = presents;
    double secs = n > 1 ? (double)(lastPresent.QuadPart - firstPresent.QuadPart) / (double)f.QuadPart : 0;
    LeaveCriticalSection(&lock);
    Log("PRESENTS total=%ld over %.3f s (%.2f a second), %ld not tallied (table full)", n, secs, secs > 0 ? (n - 1) / secs : 0, kindsOverflow);
    for (int i = 0; i < KINDS && kinds[i].count; i++)
        Log("PRESENTS %p Present%s sync=%u flags=0x%x thread=%lu count=%ld", kinds[i].sc, kinds[i].which ? "1" : "", kinds[i].sync, kinds[i].flags, kinds[i].thread, kinds[i].count);
}

#include "ctx_hook.c"
