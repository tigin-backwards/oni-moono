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
// - M4 (presentation and frame pacing): the frame-latency waitable's handle is taken from the
//   game's swap chain (IDXGISwapChain2::GetFrameLatencyWaitableObject) and the wait functions
//   UnityPlayer.dll and the executable import (WaitForSingleObject(Ex), WaitForMultipleObjects(Ex),
//   MsgWaitForMultipleObjects(Ex)) are hooked in their import tables (pn_wait_hook); a wait on a
//   handle of the same kernel object (CompareObjectHandles) is tallied per thread. The swap
//   chain's GetFrameStatistics and GetLastPresentCount are hooked too; each Present records the
//   gap since the last and whether the bound render target is swap-chain buffer 0
//   (pn_pace_report prints and resets the tallies).
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
#include <string.h>
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


// ---- M4: buffer 0, Present gaps, the frame-latency waitable ----

static void *bb0ptr;              // the presenting chain's buffer 0 at the last Present (pointer only, no reference kept)
static void *presentRT;           // the texture bound as rtv0 at the last Present (pointer only)
static void *mainSC;              // the first swap chain seen presenting
static HANDLE waitH;              // our handle to its frame-latency waitable object
static DWORD presentThread;
static LONGLONG maxGap, gapSum;   // QPC ticks
static long maxGapAt, gapsOver100, gapsOver250, gapPresents;
static long presentChecks;
static long rtIsBB, rtNotBB, rtNone;
static long phasePresents;
static LONG waitsSincePresent;
static long waitsPerFrame[4];     // presents preceded by 0, 1, 2, 3+ waitable waits

static double Ms(LONGLONG t)
{
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    return (double)t * 1000.0 / (double)f.QuadPart;
}

static void TexDesc(const char *what, ID3D11Resource *r, void *chain)
{
    if (!r) { Log("BB %s null", what); return; }
    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    ID3D11Resource_GetType(r, &dim);
    if (dim != D3D11_RESOURCE_DIMENSION_TEXTURE2D) { Log("BB %s %p dimension=%d", what, (void *)r, (int)dim); return; }
    D3D11_TEXTURE2D_DESC t;
    ID3D11Texture2D_GetDesc((ID3D11Texture2D *)r, &t);
    Log("BB %s %p (chain %p) %ux%u format=%d mips=%u array=%u sample=%u/%u usage=%d bind=0x%x cpu=0x%x misc=0x%x", what, (void *)r, chain, t.Width, t.Height, (int)t.Format,
        t.MipLevels, t.ArraySize, t.SampleDesc.Count, t.SampleDesc.Quality, (int)t.Usage, t.BindFlags, t.CPUAccessFlags, t.MiscFlags);
}

/// Before the real Present (on the presenting thread, the context's): buffer 0 and the render
/// target bound now. Detailed for the first 3 Presents and the first 3 after each pace report.
static void PresentChecks(IDXGISwapChain *sc)
{
    if (!mainSC) mainSC = sc;
    if (sc != mainSC) return;
    ID3D11Texture2D *b0 = NULL;
    HRESULT hr = IDXGISwapChain_GetBuffer(sc, 0, &IID_ID3D11Texture2D, (void **)&b0);
    void *prev = bb0ptr;
    bb0ptr = b0;
    int detail = presentChecks++ < 3 || phasePresents < 3;
    if (b0 && prev && prev != (void *)b0 && presentChecks < 50) Log("BB buffer 0 object changed %p -> %p at present %ld", prev, (void *)b0, presents);
    ID3D11RenderTargetView *rtv = NULL;
    ID3D11DepthStencilView *dsv = NULL;
    if (context) ID3D11DeviceContext_OMGetRenderTargets(context, 1, &rtv, &dsv);
    ID3D11Resource *r = NULL;
    if (rtv) ID3D11RenderTargetView_GetResource(rtv, &r);
    presentRT = r;
    if (!r) rtNone++;
    else if ((void *)r == (void *)b0) rtIsBB++;
    else rtNotBB++;
    if (detail)
    {
        Log("BB present %ld: GetBuffer(0) hr=0x%08lx buffer0=%p, bound rtv0=%p resource=%p (%s)", presents, (unsigned long)hr, (void *)b0, (void *)rtv, (void *)r,
            !r ? "none bound" : (void *)r == (void *)b0 ? "IS buffer 0" : "NOT buffer 0");
        if (rtv) { D3D11_RENDER_TARGET_VIEW_DESC d; ID3D11RenderTargetView_GetDesc(rtv, &d); Log("BB bound rtv0 view format=%d dimension=%d", (int)d.Format, (int)d.ViewDimension); }
        TexDesc("buffer0", (ID3D11Resource *)b0, sc);
        if (r && (void *)r != (void *)b0) TexDesc("bound", r, sc);
        if (presentChecks == 1)
        {
            ID3D11Texture2D *b1 = NULL;
            HRESULT h1 = IDXGISwapChain_GetBuffer(sc, 1, &IID_ID3D11Texture2D, (void **)&b1);
            Log("BB GetBuffer(1) hr=0x%08lx %p", (unsigned long)h1, (void *)b1);
            if (b1) ID3D11Texture2D_Release(b1);
        }
    }
    if (r) ID3D11Resource_Release(r);
    if (rtv) ID3D11RenderTargetView_Release(rtv);
    if (dsv) ID3D11DepthStencilView_Release(dsv);
    if (b0) ID3D11Texture2D_Release(b0);
}

// The wait tallies, per thread and function.
#define WT 32
static struct { DWORD thread; int fn; long calls, ok, timeout, other; LONGLONG total, max, sinceSum, sinceMax; DWORD timeouts[4]; int ntimeouts; } waits[WT];
static long waitsOther, waitsLogged;
static const char *const waitNames[6] = {"WaitForSingleObject", "WaitForSingleObjectEx", "WaitForMultipleObjects", "WaitForMultipleObjectsEx",
                                         "MsgWaitForMultipleObjects", "MsgWaitForMultipleObjectsEx"};
static int (*compareHandles)(HANDLE, HANDLE);

/// Whether h is (a handle to the object of) the frame-latency waitable.
static int IsWaitable(HANDLE h)
{
    if (!waitH || !h || h == INVALID_HANDLE_VALUE) return 0;
    if (h == waitH) return 1;
    return compareHandles && compareHandles(h, waitH);
}

static DWORD waitCount, waitIndex;
static BOOL waitAll;
static void TallyWait(int fn, DWORD timeout, DWORD result, LONGLONG waited, LONGLONG since)
{
    DWORD th = GetCurrentThreadId();
    EnterCriticalSection(&lock);
    int i;
    for (i = 0; i < WT && waits[i].calls; i++)
        if (waits[i].thread == th && waits[i].fn == fn) break;
    if (i == WT) waitsOther++;
    else
    {
        waits[i].thread = th;
        waits[i].fn = fn;
        waits[i].calls++;
        if (result == WAIT_OBJECT_0) waits[i].ok++;
        else if (result == WAIT_TIMEOUT) waits[i].timeout++;
        else waits[i].other++;
        waits[i].total += waited;
        if (waited > waits[i].max) waits[i].max = waited;
        waits[i].sinceSum += since;
        if (since > waits[i].sinceMax) waits[i].sinceMax = since;
        int k;
        for (k = 0; k < waits[i].ntimeouts && waits[i].timeouts[k] != timeout; k++) {}
        if (k == waits[i].ntimeouts && k < 4) waits[i].timeouts[waits[i].ntimeouts++] = timeout;
    }
    waitsSincePresent++;
    long n = waitsLogged++;
    LeaveCriticalSection(&lock);
    if (n < 12)
        Log("WAIT %ld %s on the waitable thread=%lu (present thread %lu) timeout=%lu result=0x%lx waited %.3f ms, %.3f ms after the last Present (present %ld); handles %lu, "
            "the waitable at index %lu, wait all %d", n, waitNames[fn], th, presentThread, timeout, result, Ms(waited), Ms(since), presents, fn >= 2 ? waitCount : 1,
            fn >= 2 ? waitIndex : 0, fn >= 2 ? waitAll : 0);
}

typedef DWORD(WINAPI *WFSO)(HANDLE, DWORD);
typedef DWORD(WINAPI *WFSOEx)(HANDLE, DWORD, BOOL);
typedef DWORD(WINAPI *WFMO)(DWORD, const HANDLE *, BOOL, DWORD);
typedef DWORD(WINAPI *WFMOEx)(DWORD, const HANDLE *, BOOL, DWORD, BOOL);
typedef DWORD(WINAPI *MWFMO)(DWORD, const HANDLE *, BOOL, DWORD, DWORD);
typedef DWORD(WINAPI *MWFMOEx)(DWORD, const HANDLE *, DWORD, DWORD, DWORD);
static WFSO oWFSO;
static WFSOEx oWFSOEx;
static WFMO oWFMO;
static WFMOEx oWFMOEx;
static MWFMO oMWFMO;
static MWFMOEx oMWFMOEx;

static LONGLONG Now(void) { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart; }

static int AnyWaitable(DWORD n, const HANDLE *h, BOOL all)
{
    for (DWORD i = 0; h && i < n; i++)
        if (IsWaitable(h[i])) { waitCount = n; waitIndex = i; waitAll = all; return 1; }
    return 0;
}

#define WAITHOOK(T, o, fn, isw, timeoutArg, call) \
    { \
        int w_ = (isw); \
        LONGLONG t0_ = w_ ? Now() : 0; \
        DWORD r_ = (o) call; \
        if (w_) { LONGLONG t1_ = Now(); TallyWait(fn, timeoutArg, r_, t1_ - t0_, t0_ - lastPresent.QuadPart); } \
        return r_; \
    }
static DWORD WINAPI HWFSO(HANDLE h, DWORD ms) WAITHOOK(WFSO, oWFSO, 0, IsWaitable(h), ms, (h, ms))
static DWORD WINAPI HWFSOEx(HANDLE h, DWORD ms, BOOL a) WAITHOOK(WFSOEx, oWFSOEx, 1, IsWaitable(h), ms, (h, ms, a))
static DWORD WINAPI HWFMO(DWORD n, const HANDLE *h, BOOL all, DWORD ms) WAITHOOK(WFMO, oWFMO, 2, AnyWaitable(n, h, all), ms, (n, h, all, ms))
static DWORD WINAPI HWFMOEx(DWORD n, const HANDLE *h, BOOL all, DWORD ms, BOOL a) WAITHOOK(WFMOEx, oWFMOEx, 3, AnyWaitable(n, h, all), ms, (n, h, all, ms, a))
static DWORD WINAPI HMWFMO(DWORD n, const HANDLE *h, BOOL all, DWORD ms, DWORD m) WAITHOOK(MWFMO, oMWFMO, 4, AnyWaitable(n, h, all), ms, (n, h, all, ms, m))
static DWORD WINAPI HMWFMOEx(DWORD n, const HANDLE *h, DWORD ms, DWORD m, DWORD f) WAITHOOK(MWFMOEx, oMWFMOEx, 5, AnyWaitable(n, h, (f & MWMO_WAITALL) != 0), ms, (n, h, ms, m, f))

/// Patches module m's import address table entries named `name` to `hook`; the count patched.
static int PatchImport(HMODULE m, const char *name, void *hook)
{
    BYTE *base = (BYTE *)m;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + dos->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return 0;
    int patched = 0;
    for (IMAGE_IMPORT_DESCRIPTOR *d = (IMAGE_IMPORT_DESCRIPTOR *)(base + dir.VirtualAddress); d->Name; d++)
    {
        if (!d->OriginalFirstThunk) continue;
        IMAGE_THUNK_DATA *names = (IMAGE_THUNK_DATA *)(base + d->OriginalFirstThunk), *addrs = (IMAGE_THUNK_DATA *)(base + d->FirstThunk);
        for (; names->u1.AddressOfData; names++, addrs++)
        {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            IMAGE_IMPORT_BY_NAME *ibn = (IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData);
            if (strcmp((const char *)ibn->Name, name) != 0) continue;
            DWORD old;
            if (!VirtualProtect(&addrs->u1.Function, sizeof(void *), PAGE_READWRITE, &old)) continue;
            addrs->u1.Function = (ULONG_PTR)hook;
            VirtualProtect(&addrs->u1.Function, sizeof(void *), old, &old);
            Log("WAITHOOK %s!%s imported from %s patched", m == GetModuleHandleW(NULL) ? "exe" : "module", name, (const char *)(base + d->Name));
            patched++;
        }
    }
    return patched;
}

typedef HRESULT(STDMETHODCALLTYPE *FrameStatsFn)(IDXGISwapChain *, DXGI_FRAME_STATISTICS *);
typedef HRESULT(STDMETHODCALLTYPE *LastPresentCountFn)(IDXGISwapChain *, UINT *);
static FrameStatsFn oFrameStats;
static LastPresentCountFn oLastCount;
static long frameStatsCalls, frameStatsOk, lastCountCalls;
static HRESULT frameStatsLastHr;
static HRESULT STDMETHODCALLTYPE HookFrameStats(IDXGISwapChain *sc, DXGI_FRAME_STATISTICS *s)
{
    HRESULT hr = oFrameStats(sc, s);
    long n = InterlockedIncrement(&frameStatsCalls);
    if (SUCCEEDED(hr)) InterlockedIncrement(&frameStatsOk);
    frameStatsLastHr = hr;
    if (n <= 3) Log("STATS GetFrameStatistics %ld on %p thread=%lu hr=0x%08lx presentCount=%u refreshCount=%u", n, (void *)sc, GetCurrentThreadId(), (unsigned long)hr,
                    SUCCEEDED(hr) ? s->PresentCount : 0, SUCCEEDED(hr) ? s->PresentRefreshCount : 0);
    return hr;
}
static HRESULT STDMETHODCALLTYPE HookLastCount(IDXGISwapChain *sc, UINT *c)
{
    HRESULT hr = oLastCount(sc, c);
    long n = InterlockedIncrement(&lastCountCalls);
    if (n <= 3) Log("STATS GetLastPresentCount %ld thread=%lu hr=0x%08lx count=%u", n, GetCurrentThreadId(), (unsigned long)hr, SUCCEEDED(hr) && c ? *c : 0);
    return hr;
}

/// Compile-time check of the hooks' types against the headers' members.
_Static_assert(offsetof(IDXGISwapChain1Vtbl, GetFrameStatistics) == offsetof(IDXGISwapChainVtbl, GetFrameStatistics), "swap chain vtable prefix");
_Static_assert(offsetof(IDXGISwapChain1Vtbl, GetLastPresentCount) == offsetof(IDXGISwapChainVtbl, GetLastPresentCount), "swap chain vtable prefix");
void CheckPaceTypes(IDXGISwapChainVtbl *v)
{
    v->GetFrameStatistics = HookFrameStats;
    v->GetLastPresentCount = HookLastCount;
    WFSO a = WaitForSingleObject; WFSOEx b = WaitForSingleObjectEx; WFMO c = WaitForMultipleObjects; WFMOEx d = WaitForMultipleObjectsEx;
    MWFMO e = MsgWaitForMultipleObjects; MWFMOEx f = MsgWaitForMultipleObjectsEx;
    (void)a; (void)b; (void)c; (void)d; (void)e; (void)f;
    BOOL(WINAPI * g)(HANDLE, HANDLE) = CompareObjectHandles;
    (void)g;
}

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
    if (presents > 0 && sc == mainSC)
    {
        LONGLONG gap = now.QuadPart - lastPresent.QuadPart;
        gapSum += gap;
        gapPresents++;
        if (gap > maxGap) { maxGap = gap; maxGapAt = presents; }
        if (Ms(gap) > 100) gapsOver100++;
        if (Ms(gap) > 250)
        {
            gapsOver250++;
            if (gapsOver250 <= 40) Log("GAP %.1f ms before present %ld", Ms(gap), presents);
        }
    }
    if (presents++ == 0) firstPresent = now;
    if (sc == mainSC || !mainSC)
    {
        lastPresent = now;
        presentThread = thread;
        phasePresents++;
        LONG w = InterlockedExchange(&waitsSincePresent, 0);
        waitsPerFrame[w > 3 ? 3 : w]++;
    }
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
    PresentChecks(sc);
    Count(sc, sync, flags, 0);
    FrameBoundary();
    return present0(sc, sync, flags);
}

static HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1 *sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS *p)
{
    PresentChecks((IDXGISwapChain *)sc);
    Count(sc, sync, flags, 1);
    FrameBoundary();
    return present1(sc, sync, flags, p);
}

static HRESULT STDMETHODCALLTYPE HookResize(IDXGISwapChain *sc, UINT count, UINT w, UINT h, DXGI_FORMAT f, UINT flags)
{
    HRESULT hr = resize0(sc, count, w, h, f, flags);
    Log("RESIZE %p ResizeBuffers(count=%u, %ux%u, format=%d, flags=0x%x) hr=0x%08lx thread=%lu", (void *)sc, count, w, h, (int)f, flags, (unsigned long)hr, GetCurrentThreadId());
    // M4: the chain's descriptions after each resize (its full-screen state included).
    Describe(sc);
    return hr;
}

// M4: DXGI's own full-screen calls, should the player make them (exclusive full screen).
typedef HRESULT(STDMETHODCALLTYPE *SetFullscreenStateFn)(IDXGISwapChain *, BOOL, IDXGIOutput *);
typedef HRESULT(STDMETHODCALLTYPE *ResizeTargetFn)(IDXGISwapChain *, const DXGI_MODE_DESC *);
static SetFullscreenStateFn oSetFullscreen;
static ResizeTargetFn oResizeTarget;
static HRESULT STDMETHODCALLTYPE HookSetFullscreen(IDXGISwapChain *sc, BOOL fs, IDXGIOutput *out)
{
    HRESULT hr = oSetFullscreen(sc, fs, out);
    Log("FULLSCREEN %p SetFullscreenState(%d, output %p) hr=0x%08lx thread=%lu", (void *)sc, fs, (void *)out, (unsigned long)hr, GetCurrentThreadId());
    Describe(sc);
    return hr;
}
static HRESULT STDMETHODCALLTYPE HookResizeTarget(IDXGISwapChain *sc, const DXGI_MODE_DESC *m)
{
    HRESULT hr = oResizeTarget(sc, m);
    if (m)
        Log("FULLSCREEN %p ResizeTarget(%ux%u refresh=%u/%u format=%d scanline=%d scaling=%d) hr=0x%08lx thread=%lu", (void *)sc, m->Width, m->Height, m->RefreshRate.Numerator,
            m->RefreshRate.Denominator, (int)m->Format, (int)m->ScanlineOrdering, (int)m->Scaling, (unsigned long)hr, GetCurrentThreadId());
    else Log("FULLSCREEN %p ResizeTarget(NULL) hr=0x%08lx", (void *)sc, (unsigned long)hr);
    return hr;
}
void CheckFullscreenTypes(IDXGISwapChainVtbl *v)
{
    v->SetFullscreenState = HookSetFullscreen;
    v->ResizeTarget = HookResizeTarget;
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
    Log("HOOK adapter %ls vendor=0x%04x device=0x%04x luid=%lx:%08lx", ad.Description, ad.VendorId, ad.DeviceId,
        (long)ad.AdapterLuid.HighPart, (unsigned long)ad.AdapterLuid.LowPart);
    // Where the device's adapter is in EnumAdapters1's order and in each GPU preference's (the
    // D3D11 plan's M5, the GPU choice rule).
    IDXGIFactory6 *f6 = NULL;
    for (UINT i = 0; i < 16; i++)
    {
        IDXGIAdapter1 *e = NULL;
        if (FAILED(IDXGIFactory2_EnumAdapters1(factory, i, &e))) break;
        DXGI_ADAPTER_DESC1 ed;
        IDXGIAdapter1_GetDesc1(e, &ed);
        Log("HOOK EnumAdapters1 %u: %ls luid=%lx:%08lx flags=0x%x%s", i, ed.Description, (long)ed.AdapterLuid.HighPart, (unsigned long)ed.AdapterLuid.LowPart,
            ed.Flags, memcmp(&ed.AdapterLuid, &ad.AdapterLuid, sizeof(LUID)) == 0 ? " (the device's)" : "");
        IDXGIAdapter1_Release(e);
    }
    if (SUCCEEDED(IDXGIFactory2_QueryInterface(factory, &IID_IDXGIFactory6, (void **)&f6)))
    {
        for (int p = DXGI_GPU_PREFERENCE_MINIMUM_POWER; p <= DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE; p++)
        {
            IDXGIAdapter1 *e = NULL;
            if (SUCCEEDED(IDXGIFactory6_EnumAdapterByGpuPreference(f6, 0, (DXGI_GPU_PREFERENCE)p, &IID_IDXGIAdapter1, (void **)&e)))
            {
                DXGI_ADAPTER_DESC1 ed;
                IDXGIAdapter1_GetDesc1(e, &ed);
                Log("HOOK GPU preference %d first: %ls luid=%lx:%08lx%s", p, ed.Description, (long)ed.AdapterLuid.HighPart, (unsigned long)ed.AdapterLuid.LowPart,
                    memcmp(&ed.AdapterLuid, &ad.AdapterLuid, sizeof(LUID)) == 0 ? " (the device's)" : "");
                IDXGIAdapter1_Release(e);
            }
        }
        IDXGIFactory6_Release(f6);
    }
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
        Patch(vtables[i], offsetof(IDXGISwapChainVtbl, SetFullscreenState) / sizeof(void *), (void *)HookSetFullscreen, (void **)&oSetFullscreen);
        Patch(vtables[i], offsetof(IDXGISwapChainVtbl, ResizeTarget) / sizeof(void *), (void *)HookResizeTarget, (void **)&oResizeTarget);
        Patch(vtables[i], offsetof(IDXGISwapChainVtbl, GetFrameStatistics) / sizeof(void *), (void *)HookFrameStats, (void **)&oFrameStats);
        Patch(vtables[i], offsetof(IDXGISwapChainVtbl, GetLastPresentCount) / sizeof(void *), (void *)HookLastCount, (void **)&oLastCount);
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

/// Takes the frame-latency waitable of the presenting swap chain and hooks the wait functions
/// UnityPlayer.dll and the executable import. Call after the first hooked Present. The number of
/// import entries patched; -1 without a swap chain or waitable.
EXPORT int pn_wait_hook(void)
{
    if (!mainSC) { Log("WAITHOOK no swap chain seen presenting yet"); return -1; }
    IDXGISwapChain2 *s2 = NULL;
    if (FAILED(IDXGISwapChain_QueryInterface((IDXGISwapChain *)mainSC, &IID_IDXGISwapChain2, (void **)&s2)) || !s2) { Log("WAITHOOK no IDXGISwapChain2"); return -1; }
    waitH = IDXGISwapChain2_GetFrameLatencyWaitableObject(s2);
    IDXGISwapChain2_Release(s2);
    Log("WAITHOOK GetFrameLatencyWaitableObject -> %p; called on thread %lu (the probe's caller: Unity's main thread), Presents seen on thread %lu", (void *)waitH,
        GetCurrentThreadId(), presentThread);
    if (!waitH) return -1;
    // CompareObjectHandles (Windows 10 on): "Compares two object handles to determine if they
    // refer to the same underlying kernel object." Looked up, so a missing export says so.
    HMODULE kb = GetModuleHandleW(L"kernelbase.dll");
    compareHandles = kb ? (int (*)(HANDLE, HANDLE))(void *)GetProcAddress(kb, "CompareObjectHandles") : NULL;
    Log("WAITHOOK CompareObjectHandles %p", (void *)compareHandles);
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll"), u32 = GetModuleHandleW(L"user32.dll");
    oWFSO = (WFSO)(void *)GetProcAddress(k32, "WaitForSingleObject");
    oWFSOEx = (WFSOEx)(void *)GetProcAddress(k32, "WaitForSingleObjectEx");
    oWFMO = (WFMO)(void *)GetProcAddress(k32, "WaitForMultipleObjects");
    oWFMOEx = (WFMOEx)(void *)GetProcAddress(k32, "WaitForMultipleObjectsEx");
    oMWFMO = u32 ? (MWFMO)(void *)GetProcAddress(u32, "MsgWaitForMultipleObjects") : NULL;
    oMWFMOEx = u32 ? (MWFMOEx)(void *)GetProcAddress(u32, "MsgWaitForMultipleObjectsEx") : NULL;
    HMODULE mods[2] = {GetModuleHandleW(L"UnityPlayer.dll"), GetModuleHandleW(NULL)};
    int n = 0;
    for (int i = 0; i < 2; i++)
    {
        if (!mods[i]) { Log("WAITHOOK module %d not loaded", i); continue; }
        n += PatchImport(mods[i], "WaitForSingleObject", (void *)HWFSO);
        n += PatchImport(mods[i], "WaitForSingleObjectEx", (void *)HWFSOEx);
        n += PatchImport(mods[i], "WaitForMultipleObjects", (void *)HWFMO);
        n += PatchImport(mods[i], "WaitForMultipleObjectsEx", (void *)HWFMOEx);
        if (oMWFMO) n += PatchImport(mods[i], "MsgWaitForMultipleObjects", (void *)HMWFMO);
        if (oMWFMOEx) n += PatchImport(mods[i], "MsgWaitForMultipleObjectsEx", (void *)HMWFMOEx);
    }
    Log("WAITHOOK %d import entries patched", n);
    return n;
}

/// Logs the pacing tallies since the last call under `phase`, then resets them.
EXPORT void pn_pace_report(const char *phase)
{
    EnterCriticalSection(&lock);
    Log("PACE %s: presents %ld, gap mean %.3f ms, longest %.1f ms (before present %ld), gaps over 100 ms %ld, over 250 ms %ld", phase, phasePresents,
        gapPresents ? Ms(gapSum) / (double)gapPresents : 0, Ms(maxGap), maxGapAt, gapsOver100, gapsOver250);
    Log("PACE %s: at Present the bound rtv0 was buffer 0 %ld times, another texture %ld, none %ld", phase, rtIsBB, rtNotBB, rtNone);
    Log("PACE %s: waitable waits between two Presents: 0 in %ld frames, 1 in %ld, 2 in %ld, 3+ in %ld", phase, waitsPerFrame[0], waitsPerFrame[1], waitsPerFrame[2],
        waitsPerFrame[3]);
    for (int i = 0; i < WT && waits[i].calls; i++)
    {
        char tos[64];
        int u = 0;
        tos[0] = 0;
        for (int k = 0; k < waits[i].ntimeouts; k++) u += sprintf_s(tos + u, sizeof tos - (size_t)u, "%s%lu", k ? "," : "", waits[i].timeouts[k]);
        Log("PACE %s: waitable %s thread=%lu%s calls=%ld signalled=%ld timeout=%ld other=%ld waited mean %.3f ms max %.3f ms, after the last Present mean %.3f ms max %.3f ms, "
            "timeouts asked %s", phase, waitNames[waits[i].fn], waits[i].thread, waits[i].thread == presentThread ? " (the Present thread)" : "", waits[i].calls, waits[i].ok,
            waits[i].timeout, waits[i].other, Ms(waits[i].total) / (double)waits[i].calls, Ms(waits[i].max), Ms(waits[i].sinceSum) / (double)waits[i].calls, Ms(waits[i].sinceMax),
            tos);
    }
    if (waitsOther) Log("PACE %s: %ld waits not tallied (table full)", phase, waitsOther);
    Log("PACE %s: GetFrameStatistics calls %ld (succeeded %ld, last hr 0x%08lx), GetLastPresentCount calls %ld", phase, frameStatsCalls, frameStatsOk,
        (unsigned long)frameStatsLastHr, lastCountCalls);
    memset(waits, 0, sizeof waits);
    memset(waitsPerFrame, 0, sizeof waitsPerFrame);
    waitsOther = 0;
    maxGap = gapSum = 0;
    maxGapAt = gapsOver100 = gapsOver250 = gapPresents = 0;
    rtIsBB = rtNotBB = rtNone = 0;
    phasePresents = 0;
    frameStatsCalls = frameStatsOk = lastCountCalls = 0;
    LeaveCriticalSection(&lock);
}

#include "ctx_hook.c"
