// Part of probe_native.c (included there): a one-frame recorder of the Direct3D 11 calls the
// original's player makes, for the D3D11 plan's state and CPU-cost questions without RenderDoc.
// - The device's CreateVertexShader / CreatePixelShader / CreateGeometryShader / CreateInputLayout
//   / CreateDeferredContext slots are hooked from pn_ctx_hook on: each shader is logged with its
//   DXBC container checksum (bytes 4..19, which the shipped programs carry), each input layout
//   with its elements, each deferred context made, so a frame's draws can be named afterwards.
// - The immediate context's vtable (shared by every context of its class) has its draw, state,
//   binding, Map/Unmap, update, copy and clear slots hooked. Outside a recorded frame the hooks
//   only forward. pn_ctx_arm() records the frame between the next two Present calls: every call
//   is counted by entry point and context, state settings are counted as changes only when the
//   object differs from the one the context had, Map and UpdateSubresource are classed by
//   resource kind (constant buffer, other buffer, texture) and map type, and each draw whose
//   state combination is new (and the first 40) is logged with its full state: shaders, input
//   layout, topology, vertex buffers, constant buffers (size and range) per stage, shader
//   resources and samplers per slot, rasterizer, blend and depth-stencil descriptions,
//   viewport, scissor and targets.
// Slots come from the SDK headers' C vtables (offsetof); each hook's type is checked against the
// header's member type by assignment in CheckTypes (a mismatch is an error under /W4 /WX).

enum Fn
{
    F_Draw, F_DrawIndexed, F_DrawInstanced, F_DrawIndexedInstanced, F_DrawAuto, F_DrawIndexedInstancedIndirect, F_DrawInstancedIndirect, F_Dispatch,
    F_VSSetShader, F_PSSetShader, F_GSSetShader, F_VSSetConstantBuffers, F_PSSetConstantBuffers, F_GSSetConstantBuffers, F_VSSetConstantBuffers1,
    F_PSSetConstantBuffers1, F_GSSetConstantBuffers1, F_VSSetShaderResources, F_PSSetShaderResources, F_GSSetShaderResources, F_VSSetSamplers, F_PSSetSamplers,
    F_GSSetSamplers, F_IASetInputLayout, F_IASetVertexBuffers, F_IASetIndexBuffer, F_IASetPrimitiveTopology, F_RSSetState, F_RSSetViewports,
    F_RSSetScissorRects, F_OMSetBlendState, F_OMSetDepthStencilState, F_OMSetRenderTargets, F_OMSetRenderTargetsAndUAVs, F_Map, F_Unmap,
    F_UpdateSubresource, F_UpdateSubresource1, F_CopyResource, F_CopySubresourceRegion, F_ClearRenderTargetView, F_ClearDepthStencilView, F_GenerateMips,
    F_ResolveSubresource, F_ExecuteCommandList, F_FinishCommandList, F_Count
};
static const char *const fnNames[F_Count] = {
    "Draw", "DrawIndexed", "DrawInstanced", "DrawIndexedInstanced", "DrawAuto", "DrawIndexedInstancedIndirect", "DrawInstancedIndirect", "Dispatch",
    "VSSetShader", "PSSetShader", "GSSetShader", "VSSetConstantBuffers", "PSSetConstantBuffers", "GSSetConstantBuffers", "VSSetConstantBuffers1",
    "PSSetConstantBuffers1", "GSSetConstantBuffers1", "VSSetShaderResources", "PSSetShaderResources", "GSSetShaderResources", "VSSetSamplers", "PSSetSamplers",
    "GSSetSamplers", "IASetInputLayout", "IASetVertexBuffers", "IASetIndexBuffer", "IASetPrimitiveTopology", "RSSetState", "RSSetViewports",
    "RSSetScissorRects", "OMSetBlendState", "OMSetDepthStencilState", "OMSetRenderTargets", "OMSetRenderTargetsAndUnorderedAccessViews", "Map", "Unmap",
    "UpdateSubresource", "UpdateSubresource1", "CopyResource", "CopySubresourceRegion", "ClearRenderTargetView", "ClearDepthStencilView", "GenerateMips",
    "ResolveSubresource", "ExecuteCommandList", "FinishCommandList"};

static volatile LONG recording; // 1 while a frame is recorded
static volatile LONG armed;
static LONG calls[F_Count], changes[F_Count];
#define CTXS 8
static void *ctxSeen[CTXS];
static LONG ctxDraws[CTXS], ctxCalls[CTXS];
// Map: [kind 0 cbuffer, 1 other buffer, 2 texture][map type 1..5]; bytes for constant buffers.
static LONG maps[3][6], updates[3];
static LONGLONG cbMapBytes, cbUpdateBytes;
static LONG cbSizes[8]; // constant buffers mapped by size: <=64, <=128, <=256, <=512, <=1024, <=4096, <=16384, more
static LONG drawsLogged, combos;
#define COMBOS 2048
static struct { void *vs, *ps, *gs, *il, *rs, *bs, *ds; int topo; } comboSeen[COMBOS];
#define OBJS 4096
static void *objSeen[OBJS];
static int nobj;
// The last object each context set, per kind (for change counting); one context is the usual case.
static void *lastVS, *lastPS, *lastGS, *lastIL, *lastRS, *lastBS, *lastDS;
static int lastTopo = -1;
static LARGE_INTEGER recStart;

static void CtxCount(void *ctx, int fn, int draw)
{
    InterlockedIncrement(&calls[fn]);
    for (int i = 0; i < CTXS; i++)
    {
        if (ctxSeen[i] == ctx || (!ctxSeen[i] && InterlockedCompareExchangePointer(&ctxSeen[i], ctx, NULL) == NULL))
        {
            InterlockedIncrement(&ctxCalls[i]);
            if (draw) InterlockedIncrement(&ctxDraws[i]);
            return;
        }
    }
}

#define CHANGED(fn, last, value) do { if ((void *)(value) != (last)) { (last) = (void *)(value); InterlockedIncrement(&changes[fn]); } } while (0)

/// 1 the first time an object (a state object, a shader, a layout) is seen in a recorded frame.
static int FirstSight(void *p)
{
    if (!p) return 0;
    for (int i = 0; i < nobj; i++)
        if (objSeen[i] == p) return 0;
    if (nobj < OBJS) objSeen[nobj++] = p;
    return 1;
}

// ---- The device's creation hooks ----

typedef HRESULT(STDMETHODCALLTYPE *CreateVSFn)(ID3D11Device *, const void *, SIZE_T, ID3D11ClassLinkage *, ID3D11VertexShader **);
typedef HRESULT(STDMETHODCALLTYPE *CreatePSFn)(ID3D11Device *, const void *, SIZE_T, ID3D11ClassLinkage *, ID3D11PixelShader **);
typedef HRESULT(STDMETHODCALLTYPE *CreateGSFn)(ID3D11Device *, const void *, SIZE_T, ID3D11ClassLinkage *, ID3D11GeometryShader **);
typedef HRESULT(STDMETHODCALLTYPE *CreateILFn)(ID3D11Device *, const D3D11_INPUT_ELEMENT_DESC *, UINT, const void *, SIZE_T, ID3D11InputLayout **);
typedef HRESULT(STDMETHODCALLTYPE *CreateDCFn)(ID3D11Device *, UINT, ID3D11DeviceContext **);
static CreateVSFn oCreateVS;
static CreatePSFn oCreatePS;
static CreateGSFn oCreateGS;
static CreateILFn oCreateIL;
static CreateDCFn oCreateDC;

static void LogShader(const char *stage, const void *code, SIZE_T len, void *out)
{
    const unsigned char *b = (const unsigned char *)code;
    char sum[33] = "-";
    if (code && len >= 20 && memcmp(b, "DXBC", 4) == 0)
        for (int i = 0; i < 16; i++) sprintf_s(sum + i * 2, 3, "%02x", b[4 + i]);
    Log("SHADER %s %p dxbc=%s len=%zu", stage, out, sum, (size_t)len);
}

static HRESULT STDMETHODCALLTYPE HookCreateVS(ID3D11Device *d, const void *code, SIZE_T len, ID3D11ClassLinkage *l, ID3D11VertexShader **out)
{
    HRESULT hr = oCreateVS(d, code, len, l, out);
    if (SUCCEEDED(hr) && out) LogShader("vs", code, len, *out);
    return hr;
}
static HRESULT STDMETHODCALLTYPE HookCreatePS(ID3D11Device *d, const void *code, SIZE_T len, ID3D11ClassLinkage *l, ID3D11PixelShader **out)
{
    HRESULT hr = oCreatePS(d, code, len, l, out);
    if (SUCCEEDED(hr) && out) LogShader("ps", code, len, *out);
    return hr;
}
static HRESULT STDMETHODCALLTYPE HookCreateGS(ID3D11Device *d, const void *code, SIZE_T len, ID3D11ClassLinkage *l, ID3D11GeometryShader **out)
{
    HRESULT hr = oCreateGS(d, code, len, l, out);
    if (SUCCEEDED(hr) && out) LogShader("gs", code, len, *out);
    return hr;
}
static HRESULT STDMETHODCALLTYPE HookCreateIL(ID3D11Device *d, const D3D11_INPUT_ELEMENT_DESC *e, UINT n, const void *code, SIZE_T len, ID3D11InputLayout **out)
{
    HRESULT hr = oCreateIL(d, e, n, code, len, out);
    if (SUCCEEDED(hr) && out)
    {
        char line[1800];
        int used = sprintf_s(line, sizeof line, "LAYOUT %p n=%u", (void *)*out, n);
        for (UINT i = 0; i < n && used > 0 && used < (int)sizeof line - 120; i++)
            used += sprintf_s(line + used, sizeof line - (size_t)used, " | %s%u fmt=%d slot=%u off=%u class=%d step=%u", e[i].SemanticName, e[i].SemanticIndex,
                              (int)e[i].Format, e[i].InputSlot, e[i].AlignedByteOffset, (int)e[i].InputSlotClass, e[i].InstanceDataStepRate);
        Log("%s", line);
        LogShader("layout-vs", code, len, *out);
    }
    return hr;
}

static void HookContext(ID3D11DeviceContext *c, const char *how);

static HRESULT STDMETHODCALLTYPE HookCreateDC(ID3D11Device *d, UINT flags, ID3D11DeviceContext **out)
{
    HRESULT hr = oCreateDC(d, flags, out);
    Log("DEFERRED CreateDeferredContext(flags=0x%x) hr=0x%08lx %p thread=%lu", flags, (unsigned long)hr, out ? (void *)*out : NULL, GetCurrentThreadId());
    if (SUCCEEDED(hr) && out && *out) HookContext(*out, "deferred");
    return hr;
}

// ---- The context hooks ----

#define CTX ID3D11DeviceContext
#define CTX1 ID3D11DeviceContext1
typedef void(STDMETHODCALLTYPE *DrawFn)(CTX *, UINT, UINT);
typedef void(STDMETHODCALLTYPE *DrawIndexedFn)(CTX *, UINT, UINT, INT);
typedef void(STDMETHODCALLTYPE *DrawInstancedFn)(CTX *, UINT, UINT, UINT, UINT);
typedef void(STDMETHODCALLTYPE *DrawIndexedInstancedFn)(CTX *, UINT, UINT, UINT, INT, UINT);
typedef void(STDMETHODCALLTYPE *DrawAutoFn)(CTX *);
typedef void(STDMETHODCALLTYPE *DrawIndirectFn)(CTX *, ID3D11Buffer *, UINT);
typedef void(STDMETHODCALLTYPE *DispatchFn)(CTX *, UINT, UINT, UINT);
typedef void(STDMETHODCALLTYPE *SetVSFn)(CTX *, ID3D11VertexShader *, ID3D11ClassInstance *const *, UINT);
typedef void(STDMETHODCALLTYPE *SetPSFn)(CTX *, ID3D11PixelShader *, ID3D11ClassInstance *const *, UINT);
typedef void(STDMETHODCALLTYPE *SetGSFn)(CTX *, ID3D11GeometryShader *, ID3D11ClassInstance *const *, UINT);
typedef void(STDMETHODCALLTYPE *SetCBFn)(CTX *, UINT, UINT, ID3D11Buffer *const *);
typedef void(STDMETHODCALLTYPE *SetCB1Fn)(CTX1 *, UINT, UINT, ID3D11Buffer *const *, const UINT *, const UINT *);
typedef void(STDMETHODCALLTYPE *SetSRVFn)(CTX *, UINT, UINT, ID3D11ShaderResourceView *const *);
typedef void(STDMETHODCALLTYPE *SetSamplersFn)(CTX *, UINT, UINT, ID3D11SamplerState *const *);
typedef void(STDMETHODCALLTYPE *SetILFn)(CTX *, ID3D11InputLayout *);
typedef void(STDMETHODCALLTYPE *SetVBFn)(CTX *, UINT, UINT, ID3D11Buffer *const *, const UINT *, const UINT *);
typedef void(STDMETHODCALLTYPE *SetIBFn)(CTX *, ID3D11Buffer *, DXGI_FORMAT, UINT);
typedef void(STDMETHODCALLTYPE *SetTopoFn)(CTX *, D3D11_PRIMITIVE_TOPOLOGY);
typedef void(STDMETHODCALLTYPE *SetRSFn)(CTX *, ID3D11RasterizerState *);
typedef void(STDMETHODCALLTYPE *SetVPFn)(CTX *, UINT, const D3D11_VIEWPORT *);
typedef void(STDMETHODCALLTYPE *SetScissorFn)(CTX *, UINT, const D3D11_RECT *);
typedef void(STDMETHODCALLTYPE *SetBSFn)(CTX *, ID3D11BlendState *, const FLOAT[4], UINT);
typedef void(STDMETHODCALLTYPE *SetDSFn)(CTX *, ID3D11DepthStencilState *, UINT);
typedef void(STDMETHODCALLTYPE *SetRTFn)(CTX *, UINT, ID3D11RenderTargetView *const *, ID3D11DepthStencilView *);
typedef void(STDMETHODCALLTYPE *SetRTUAVFn)(CTX *, UINT, ID3D11RenderTargetView *const *, ID3D11DepthStencilView *, UINT, UINT, ID3D11UnorderedAccessView *const *, const UINT *);
typedef HRESULT(STDMETHODCALLTYPE *MapFn)(CTX *, ID3D11Resource *, UINT, D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE *);
typedef void(STDMETHODCALLTYPE *UnmapFn)(CTX *, ID3D11Resource *, UINT);
typedef void(STDMETHODCALLTYPE *UpdateFn)(CTX *, ID3D11Resource *, UINT, const D3D11_BOX *, const void *, UINT, UINT);
typedef void(STDMETHODCALLTYPE *Update1Fn)(CTX1 *, ID3D11Resource *, UINT, const D3D11_BOX *, const void *, UINT, UINT, UINT);
typedef void(STDMETHODCALLTYPE *CopyResFn)(CTX *, ID3D11Resource *, ID3D11Resource *);
typedef void(STDMETHODCALLTYPE *CopyRegionFn)(CTX *, ID3D11Resource *, UINT, UINT, UINT, UINT, ID3D11Resource *, UINT, const D3D11_BOX *);
typedef void(STDMETHODCALLTYPE *ClearRTVFn)(CTX *, ID3D11RenderTargetView *, const FLOAT[4]);
typedef void(STDMETHODCALLTYPE *ClearDSVFn)(CTX *, ID3D11DepthStencilView *, UINT, FLOAT, UINT8);
typedef void(STDMETHODCALLTYPE *GenMipsFn)(CTX *, ID3D11ShaderResourceView *);
typedef void(STDMETHODCALLTYPE *ResolveFn)(CTX *, ID3D11Resource *, UINT, ID3D11Resource *, UINT, DXGI_FORMAT);
typedef void(STDMETHODCALLTYPE *ExecFn)(CTX *, ID3D11CommandList *, BOOL);
typedef HRESULT(STDMETHODCALLTYPE *FinishFn)(CTX *, BOOL, ID3D11CommandList **);

static DrawFn oDraw;
static DrawIndexedFn oDrawIndexed;
static DrawInstancedFn oDrawInstanced;
static DrawIndexedInstancedFn oDrawIndexedInstanced;
static DrawAutoFn oDrawAuto;
static DrawIndirectFn oDrawIndexedInstancedIndirect, oDrawInstancedIndirect;
static DispatchFn oDispatch;
static SetVSFn oVSSetShader;
static SetPSFn oPSSetShader;
static SetGSFn oGSSetShader;
static SetCBFn oVSSetCB, oPSSetCB, oGSSetCB;
static SetCB1Fn oVSSetCB1, oPSSetCB1, oGSSetCB1;
static SetSRVFn oVSSetSRV, oPSSetSRV, oGSSetSRV;
static SetSamplersFn oVSSetSamp, oPSSetSamp, oGSSetSamp;
static SetILFn oIASetInputLayout;
static SetVBFn oIASetVertexBuffers;
static SetIBFn oIASetIndexBuffer;
static SetTopoFn oIASetTopology;
static SetRSFn oRSSetState;
static SetVPFn oRSSetViewports;
static SetScissorFn oRSSetScissorRects;
static SetBSFn oOMSetBlendState;
static SetDSFn oOMSetDepthStencilState;
static SetRTFn oOMSetRenderTargets;
static SetRTUAVFn oOMSetRTUAV;
static MapFn oMap;
static UnmapFn oUnmap;
static UpdateFn oUpdate;
static Update1Fn oUpdate1;
static CopyResFn oCopyResource;
static CopyRegionFn oCopyRegion;
static ClearRTVFn oClearRTV;
static ClearDSVFn oClearDSV;
static GenMipsFn oGenMips;
static ResolveFn oResolve;
static ExecFn oExec;
static FinishFn oFinish;

static void LogDraw(CTX *c, const char *what);

#define REC (recording)

// The immediate context's vtable is a copy inside the object (measured: vtable = object + 8 on
// the RTX 3090's runtime), and its Draw entries do not keep a hook (measured: a frame recorded
// with them hooked once counted no draws while every Set and Map hook counted). So while a frame
// is recorded, after every hooked call each hooked slot is checked and, when the runtime has put
// another function there, that function becomes the slot's forward target and the hook goes back.
#define HOOKS 64
static struct { size_t slot; void *hook; void **orig; LONG rewrites; const char *name; } hooks[HOOKS];
static int nhooks;
static void Repatch(CTX *c)
{
    void **vt = *(void ***)c;
    for (int i = 0; i < nhooks; i++)
        if (vt[hooks[i].slot] != hooks[i].hook)
        {
            *hooks[i].orig = vt[hooks[i].slot];
            vt[hooks[i].slot] = hooks[i].hook;
            hooks[i].rewrites++;
        }
}
static void STDMETHODCALLTYPE HDraw(CTX *c, UINT a, UINT b) { if (REC) { CtxCount(c, F_Draw, 1); LogDraw(c, "Draw"); } oDraw(c, a, b); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HDrawIndexed(CTX *c, UINT a, UINT b, INT d) { if (REC) { CtxCount(c, F_DrawIndexed, 1); LogDraw(c, "DrawIndexed"); } oDrawIndexed(c, a, b, d); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HDrawInstanced(CTX *c, UINT a, UINT b, UINT d, UINT e) { if (REC) { CtxCount(c, F_DrawInstanced, 1); LogDraw(c, "DrawInstanced"); } oDrawInstanced(c, a, b, d, e); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HDrawIndexedInstanced(CTX *c, UINT a, UINT b, UINT d, INT e, UINT f) { if (REC) { CtxCount(c, F_DrawIndexedInstanced, 1); LogDraw(c, "DrawIndexedInstanced"); } oDrawIndexedInstanced(c, a, b, d, e, f); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HDrawAuto(CTX *c) { if (REC) { CtxCount(c, F_DrawAuto, 1); LogDraw(c, "DrawAuto"); } oDrawAuto(c); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HDrawIII(CTX *c, ID3D11Buffer *b, UINT o) { if (REC) { CtxCount(c, F_DrawIndexedInstancedIndirect, 1); LogDraw(c, "DrawIndexedInstancedIndirect"); } oDrawIndexedInstancedIndirect(c, b, o); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HDrawII(CTX *c, ID3D11Buffer *b, UINT o) { if (REC) { CtxCount(c, F_DrawInstancedIndirect, 1); LogDraw(c, "DrawInstancedIndirect"); } oDrawInstancedIndirect(c, b, o); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HDispatch(CTX *c, UINT x, UINT y, UINT z) { if (REC) CtxCount(c, F_Dispatch, 0); oDispatch(c, x, y, z); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HVSSetShader(CTX *c, ID3D11VertexShader *s, ID3D11ClassInstance *const *i, UINT n) { if (REC) { CtxCount(c, F_VSSetShader, 0); CHANGED(F_VSSetShader, lastVS, s); } oVSSetShader(c, s, i, n); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HPSSetShader(CTX *c, ID3D11PixelShader *s, ID3D11ClassInstance *const *i, UINT n) { if (REC) { CtxCount(c, F_PSSetShader, 0); CHANGED(F_PSSetShader, lastPS, s); } oPSSetShader(c, s, i, n); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HGSSetShader(CTX *c, ID3D11GeometryShader *s, ID3D11ClassInstance *const *i, UINT n) { if (REC) { CtxCount(c, F_GSSetShader, 0); CHANGED(F_GSSetShader, lastGS, s); } oGSSetShader(c, s, i, n); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HVSSetCB(CTX *c, UINT s, UINT n, ID3D11Buffer *const *b) { if (REC) CtxCount(c, F_VSSetConstantBuffers, 0); oVSSetCB(c, s, n, b); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HPSSetCB(CTX *c, UINT s, UINT n, ID3D11Buffer *const *b) { if (REC) CtxCount(c, F_PSSetConstantBuffers, 0); oPSSetCB(c, s, n, b); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HGSSetCB(CTX *c, UINT s, UINT n, ID3D11Buffer *const *b) { if (REC) CtxCount(c, F_GSSetConstantBuffers, 0); oGSSetCB(c, s, n, b); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HVSSetCB1(CTX1 *c, UINT s, UINT n, ID3D11Buffer *const *b, const UINT *f, const UINT *k) { if (REC) CtxCount(c, F_VSSetConstantBuffers1, 0); oVSSetCB1(c, s, n, b, f, k); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HPSSetCB1(CTX1 *c, UINT s, UINT n, ID3D11Buffer *const *b, const UINT *f, const UINT *k) { if (REC) CtxCount(c, F_PSSetConstantBuffers1, 0); oPSSetCB1(c, s, n, b, f, k); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HGSSetCB1(CTX1 *c, UINT s, UINT n, ID3D11Buffer *const *b, const UINT *f, const UINT *k) { if (REC) CtxCount(c, F_GSSetConstantBuffers1, 0); oGSSetCB1(c, s, n, b, f, k); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HVSSetSRV(CTX *c, UINT s, UINT n, ID3D11ShaderResourceView *const *v) { if (REC) CtxCount(c, F_VSSetShaderResources, 0); oVSSetSRV(c, s, n, v); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HPSSetSRV(CTX *c, UINT s, UINT n, ID3D11ShaderResourceView *const *v) { if (REC) CtxCount(c, F_PSSetShaderResources, 0); oPSSetSRV(c, s, n, v); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HGSSetSRV(CTX *c, UINT s, UINT n, ID3D11ShaderResourceView *const *v) { if (REC) CtxCount(c, F_GSSetShaderResources, 0); oGSSetSRV(c, s, n, v); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HVSSetSamp(CTX *c, UINT s, UINT n, ID3D11SamplerState *const *v) { if (REC) CtxCount(c, F_VSSetSamplers, 0); oVSSetSamp(c, s, n, v); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HPSSetSamp(CTX *c, UINT s, UINT n, ID3D11SamplerState *const *v) { if (REC) CtxCount(c, F_PSSetSamplers, 0); oPSSetSamp(c, s, n, v); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HGSSetSamp(CTX *c, UINT s, UINT n, ID3D11SamplerState *const *v) { if (REC) CtxCount(c, F_GSSetSamplers, 0); oGSSetSamp(c, s, n, v); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HSetIL(CTX *c, ID3D11InputLayout *l) { if (REC) { CtxCount(c, F_IASetInputLayout, 0); CHANGED(F_IASetInputLayout, lastIL, l); } oIASetInputLayout(c, l); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HSetVB(CTX *c, UINT s, UINT n, ID3D11Buffer *const *b, const UINT *st, const UINT *o) { if (REC) CtxCount(c, F_IASetVertexBuffers, 0); oIASetVertexBuffers(c, s, n, b, st, o); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HSetIB(CTX *c, ID3D11Buffer *b, DXGI_FORMAT f, UINT o) { if (REC) CtxCount(c, F_IASetIndexBuffer, 0); oIASetIndexBuffer(c, b, f, o); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HSetTopo(CTX *c, D3D11_PRIMITIVE_TOPOLOGY t)
{
    if (REC) { CtxCount(c, F_IASetPrimitiveTopology, 0); if ((int)t != lastTopo) { lastTopo = (int)t; InterlockedIncrement(&changes[F_IASetPrimitiveTopology]); } }
    oIASetTopology(c, t);
    if (REC) Repatch((CTX *)c);
}
static void STDMETHODCALLTYPE HSetRS(CTX *c, ID3D11RasterizerState *s) { if (REC) { CtxCount(c, F_RSSetState, 0); CHANGED(F_RSSetState, lastRS, s); } oRSSetState(c, s); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HSetVP(CTX *c, UINT n, const D3D11_VIEWPORT *v) { if (REC) CtxCount(c, F_RSSetViewports, 0); oRSSetViewports(c, n, v); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HSetScissor(CTX *c, UINT n, const D3D11_RECT *r) { if (REC) CtxCount(c, F_RSSetScissorRects, 0); oRSSetScissorRects(c, n, r); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HSetBS(CTX *c, ID3D11BlendState *s, const FLOAT f[4], UINT m) { if (REC) { CtxCount(c, F_OMSetBlendState, 0); CHANGED(F_OMSetBlendState, lastBS, s); } oOMSetBlendState(c, s, f, m); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HSetDS(CTX *c, ID3D11DepthStencilState *s, UINT r) { if (REC) { CtxCount(c, F_OMSetDepthStencilState, 0); CHANGED(F_OMSetDepthStencilState, lastDS, s); } oOMSetDepthStencilState(c, s, r); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HSetRT(CTX *c, UINT n, ID3D11RenderTargetView *const *v, ID3D11DepthStencilView *d) { if (REC) CtxCount(c, F_OMSetRenderTargets, 0); oOMSetRenderTargets(c, n, v, d); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HSetRTUAV(CTX *c, UINT n, ID3D11RenderTargetView *const *v, ID3D11DepthStencilView *d, UINT s, UINT k, ID3D11UnorderedAccessView *const *u, const UINT *i)
{
    if (REC) CtxCount(c, F_OMSetRenderTargetsAndUAVs, 0);
    oOMSetRTUAV(c, n, v, d, s, k, u, i);
    if (REC) Repatch((CTX *)c);
}

/// 0 constant buffer, 1 other buffer, 2 texture; the buffer's size in *size.
static int KindOf(ID3D11Resource *r, UINT *size)
{
    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    ID3D11Resource_GetType(r, &dim);
    *size = 0;
    if (dim != D3D11_RESOURCE_DIMENSION_BUFFER) return 2;
    D3D11_BUFFER_DESC d;
    ID3D11Buffer_GetDesc((ID3D11Buffer *)r, &d);
    *size = d.ByteWidth;
    return (d.BindFlags & D3D11_BIND_CONSTANT_BUFFER) ? 0 : 1;
}

static HRESULT STDMETHODCALLTYPE HMap(CTX *c, ID3D11Resource *r, UINT s, D3D11_MAP t, UINT f, D3D11_MAPPED_SUBRESOURCE *m)
{
    if (REC)
    {
        CtxCount(c, F_Map, 0);
        UINT size;
        int k = KindOf(r, &size);
        if ((int)t >= 1 && (int)t <= 5) InterlockedIncrement(&maps[k][(int)t]);
        if (k == 0)
        {
            InterlockedAdd64(&cbMapBytes, size);
            int b = size <= 64 ? 0 : size <= 128 ? 1 : size <= 256 ? 2 : size <= 512 ? 3 : size <= 1024 ? 4 : size <= 4096 ? 5 : size <= 16384 ? 6 : 7;
            InterlockedIncrement(&cbSizes[b]);
        }
    }
    HRESULT hr = oMap(c, r, s, t, f, m);
    if (REC) Repatch(c);
    return hr;
}
static void STDMETHODCALLTYPE HUnmap(CTX *c, ID3D11Resource *r, UINT s) { if (REC) CtxCount(c, F_Unmap, 0); oUnmap(c, r, s); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HUpdate(CTX *c, ID3D11Resource *r, UINT s, const D3D11_BOX *b, const void *d, UINT rp, UINT dp)
{
    if (REC)
    {
        CtxCount(c, F_UpdateSubresource, 0);
        UINT size;
        int k = KindOf(r, &size);
        InterlockedIncrement(&updates[k]);
        if (k == 0) InterlockedAdd64(&cbUpdateBytes, size);
    }
    oUpdate(c, r, s, b, d, rp, dp);
    if (REC) Repatch((CTX *)c);
}
static void STDMETHODCALLTYPE HUpdate1(CTX1 *c, ID3D11Resource *r, UINT s, const D3D11_BOX *b, const void *d, UINT rp, UINT dp, UINT fl)
{
    if (REC)
    {
        CtxCount(c, F_UpdateSubresource1, 0);
        UINT size;
        int k = KindOf(r, &size);
        InterlockedIncrement(&updates[k]);
        if (k == 0) InterlockedAdd64(&cbUpdateBytes, size);
    }
    oUpdate1(c, r, s, b, d, rp, dp, fl);
    if (REC) Repatch((CTX *)c);
}
static void STDMETHODCALLTYPE HCopyRes(CTX *c, ID3D11Resource *d, ID3D11Resource *s) { if (REC) CtxCount(c, F_CopyResource, 0); oCopyResource(c, d, s); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HCopyRegion(CTX *c, ID3D11Resource *d, UINT ds, UINT x, UINT y, UINT z, ID3D11Resource *s, UINT ss, const D3D11_BOX *b)
{
    if (REC) CtxCount(c, F_CopySubresourceRegion, 0);
    oCopyRegion(c, d, ds, x, y, z, s, ss, b);
    if (REC) Repatch((CTX *)c);
}
static void STDMETHODCALLTYPE HClearRTV(CTX *c, ID3D11RenderTargetView *v, const FLOAT col[4]) { if (REC) CtxCount(c, F_ClearRenderTargetView, 0); oClearRTV(c, v, col); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HClearDSV(CTX *c, ID3D11DepthStencilView *v, UINT f, FLOAT d, UINT8 s)
{
    if (REC) { CtxCount(c, F_ClearDepthStencilView, 0); Log("CLEARDSV flags=0x%x depth=%.9g stencil=%u", f, d, s); }
    oClearDSV(c, v, f, d, s);
    if (REC) Repatch((CTX *)c);
}
static void STDMETHODCALLTYPE HGenMips(CTX *c, ID3D11ShaderResourceView *v) { if (REC) CtxCount(c, F_GenerateMips, 0); oGenMips(c, v); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HResolve(CTX *c, ID3D11Resource *d, UINT ds, ID3D11Resource *s, UINT ss, DXGI_FORMAT f) { if (REC) CtxCount(c, F_ResolveSubresource, 0); oResolve(c, d, ds, s, ss, f); if (REC) Repatch((CTX *)c); }
static void STDMETHODCALLTYPE HExec(CTX *c, ID3D11CommandList *l, BOOL r) { if (REC) CtxCount(c, F_ExecuteCommandList, 0); oExec(c, l, r); if (REC) Repatch((CTX *)c); }
static HRESULT STDMETHODCALLTYPE HFinish(CTX *c, BOOL r, ID3D11CommandList **l) { if (REC) CtxCount(c, F_FinishCommandList, 0); HRESULT hr = oFinish(c, r, l); if (REC) Repatch(c); return hr; }

/// Compile-time check: every hook has its header member's exact type (C4113/C4133 otherwise).
void CheckTypes(ID3D11DeviceContextVtbl *v, ID3D11DeviceContext1Vtbl *v1, ID3D11DeviceVtbl *d)
{
    v->Draw = HDraw; v->DrawIndexed = HDrawIndexed; v->DrawInstanced = HDrawInstanced; v->DrawIndexedInstanced = HDrawIndexedInstanced;
    v->DrawAuto = HDrawAuto; v->DrawIndexedInstancedIndirect = HDrawIII; v->DrawInstancedIndirect = HDrawII; v->Dispatch = HDispatch;
    v->VSSetShader = HVSSetShader; v->PSSetShader = HPSSetShader; v->GSSetShader = HGSSetShader;
    v->VSSetConstantBuffers = HVSSetCB; v->PSSetConstantBuffers = HPSSetCB; v->GSSetConstantBuffers = HGSSetCB;
    v1->VSSetConstantBuffers1 = HVSSetCB1; v1->PSSetConstantBuffers1 = HPSSetCB1; v1->GSSetConstantBuffers1 = HGSSetCB1;
    v->VSSetShaderResources = HVSSetSRV; v->PSSetShaderResources = HPSSetSRV; v->GSSetShaderResources = HGSSetSRV;
    v->VSSetSamplers = HVSSetSamp; v->PSSetSamplers = HPSSetSamp; v->GSSetSamplers = HGSSetSamp;
    v->IASetInputLayout = HSetIL; v->IASetVertexBuffers = HSetVB; v->IASetIndexBuffer = HSetIB; v->IASetPrimitiveTopology = HSetTopo;
    v->RSSetState = HSetRS; v->RSSetViewports = HSetVP; v->RSSetScissorRects = HSetScissor;
    v->OMSetBlendState = HSetBS; v->OMSetDepthStencilState = HSetDS; v->OMSetRenderTargets = HSetRT; v->OMSetRenderTargetsAndUnorderedAccessViews = HSetRTUAV;
    v->Map = HMap; v->Unmap = HUnmap; v->UpdateSubresource = HUpdate; v1->UpdateSubresource1 = HUpdate1;
    v->CopyResource = HCopyRes; v->CopySubresourceRegion = HCopyRegion; v->ClearRenderTargetView = HClearRTV; v->ClearDepthStencilView = HClearDSV;
    v->GenerateMips = HGenMips; v->ResolveSubresource = HResolve; v->ExecuteCommandList = HExec; v->FinishCommandList = HFinish;
    d->CreateVertexShader = HookCreateVS; d->CreatePixelShader = HookCreatePS; d->CreateGeometryShader = HookCreateGS;
    d->CreateInputLayout = HookCreateIL; d->CreateDeferredContext = HookCreateDC;
}

// ID3D11DeviceContext1's vtable starts with ID3D11DeviceContext's.
#define SLOT1(m) (offsetof(ID3D11DeviceContext1Vtbl, m) / sizeof(void *))
_Static_assert(offsetof(ID3D11DeviceContext1Vtbl, FinishCommandList) == offsetof(ID3D11DeviceContextVtbl, FinishCommandList), "context vtable prefix");
_Static_assert(offsetof(ID3D11DeviceContext1Vtbl, Draw) == offsetof(ID3D11DeviceContextVtbl, Draw), "context vtable prefix");
static void **ctxVtables[4];
static int nctxVtables;

static void HookContext(ID3D11DeviceContext *c, const char *how)
{
    void **vt = *(void ***)c;
    Log("CTXHOOK %s context %p type=%d vtable %p", how, (void *)c, (int)ID3D11DeviceContext_GetType(c), (void *)vt);
    for (int i = 0; i < nctxVtables; i++)
        if (ctxVtables[i] == vt) return;
    if (nctxVtables == 4) return;
    ctxVtables[nctxVtables++] = vt;
    MEMORY_BASIC_INFORMATION mbi;
    if (VirtualQuery(vt, &mbi, sizeof mbi)) Log("CTXHOOK vtable memory protect=0x%lx type=0x%lx", mbi.Protect, mbi.Type);
#define P(m, h, o) do { Patch(vt, SLOT1(m), (void *)(h), (void **)&(o)); if (nhooks < HOOKS) { hooks[nhooks].slot = SLOT1(m); hooks[nhooks].hook = (void *)(h); hooks[nhooks].orig = (void **)&(o); hooks[nhooks].name = #m; nhooks++; } } while (0)
    P(Draw, HDraw, oDraw); P(DrawIndexed, HDrawIndexed, oDrawIndexed); P(DrawInstanced, HDrawInstanced, oDrawInstanced);
    P(DrawIndexedInstanced, HDrawIndexedInstanced, oDrawIndexedInstanced); P(DrawAuto, HDrawAuto, oDrawAuto);
    P(DrawIndexedInstancedIndirect, HDrawIII, oDrawIndexedInstancedIndirect); P(DrawInstancedIndirect, HDrawII, oDrawInstancedIndirect); P(Dispatch, HDispatch, oDispatch);
    P(VSSetShader, HVSSetShader, oVSSetShader); P(PSSetShader, HPSSetShader, oPSSetShader); P(GSSetShader, HGSSetShader, oGSSetShader);
    P(VSSetConstantBuffers, HVSSetCB, oVSSetCB); P(PSSetConstantBuffers, HPSSetCB, oPSSetCB); P(GSSetConstantBuffers, HGSSetCB, oGSSetCB);
    P(VSSetConstantBuffers1, HVSSetCB1, oVSSetCB1); P(PSSetConstantBuffers1, HPSSetCB1, oPSSetCB1); P(GSSetConstantBuffers1, HGSSetCB1, oGSSetCB1);
    P(VSSetShaderResources, HVSSetSRV, oVSSetSRV); P(PSSetShaderResources, HPSSetSRV, oPSSetSRV); P(GSSetShaderResources, HGSSetSRV, oGSSetSRV);
    P(VSSetSamplers, HVSSetSamp, oVSSetSamp); P(PSSetSamplers, HPSSetSamp, oPSSetSamp); P(GSSetSamplers, HGSSetSamp, oGSSetSamp);
    P(IASetInputLayout, HSetIL, oIASetInputLayout); P(IASetVertexBuffers, HSetVB, oIASetVertexBuffers); P(IASetIndexBuffer, HSetIB, oIASetIndexBuffer);
    P(IASetPrimitiveTopology, HSetTopo, oIASetTopology); P(RSSetState, HSetRS, oRSSetState); P(RSSetViewports, HSetVP, oRSSetViewports);
    P(RSSetScissorRects, HSetScissor, oRSSetScissorRects); P(OMSetBlendState, HSetBS, oOMSetBlendState); P(OMSetDepthStencilState, HSetDS, oOMSetDepthStencilState);
    P(OMSetRenderTargets, HSetRT, oOMSetRenderTargets); P(OMSetRenderTargetsAndUnorderedAccessViews, HSetRTUAV, oOMSetRTUAV);
    P(Map, HMap, oMap); P(Unmap, HUnmap, oUnmap); P(UpdateSubresource, HUpdate, oUpdate); P(UpdateSubresource1, HUpdate1, oUpdate1);
    P(CopyResource, HCopyRes, oCopyResource); P(CopySubresourceRegion, HCopyRegion, oCopyRegion); P(ClearRenderTargetView, HClearRTV, oClearRTV);
    P(ClearDepthStencilView, HClearDSV, oClearDSV); P(GenerateMips, HGenMips, oGenMips); P(ResolveSubresource, HResolve, oResolve);
    P(ExecuteCommandList, HExec, oExec); P(FinishCommandList, HFinish, oFinish);
#undef P
}

/// Hooks the device's creation slots and the immediate context. 1 when hooked.
EXPORT int pn_ctx_hook(void)
{
    if (!device || !context) { Log("CTXHOOK no device"); return 0; }
    ID3D11DeviceContext1 *c1 = NULL;
    if (FAILED(ID3D11DeviceContext_QueryInterface(context, &IID_ID3D11DeviceContext1, (void **)&c1))) { Log("CTXHOOK no ID3D11DeviceContext1"); return 0; }
    if (*(void ***)c1 != *(void ***)context) Log("CTXHOOK ID3D11DeviceContext1 has another vtable %p", *(void **)c1);
    ID3D11DeviceContext1_Release(c1);
    void **dv = *(void ***)device;
    Patch(dv, offsetof(ID3D11DeviceVtbl, CreateVertexShader) / sizeof(void *), (void *)HookCreateVS, (void **)&oCreateVS);
    Patch(dv, offsetof(ID3D11DeviceVtbl, CreatePixelShader) / sizeof(void *), (void *)HookCreatePS, (void **)&oCreatePS);
    Patch(dv, offsetof(ID3D11DeviceVtbl, CreateGeometryShader) / sizeof(void *), (void *)HookCreateGS, (void **)&oCreateGS);
    Patch(dv, offsetof(ID3D11DeviceVtbl, CreateInputLayout) / sizeof(void *), (void *)HookCreateIL, (void **)&oCreateIL);
    Patch(dv, offsetof(ID3D11DeviceVtbl, CreateDeferredContext) / sizeof(void *), (void *)HookCreateDC, (void **)&oCreateDC);
    Log("CTXHOOK device vtable %p hooked", (void *)dv);
    HookContext(context, "immediate");
    return 1;
}

/// Records the frame between the next two Present calls.
EXPORT void pn_ctx_arm(void) { InterlockedExchange(&armed, 1); }

/// 1 once the armed frame has been recorded and reported.
static volatile LONG reported;
EXPORT int pn_ctx_done(void) { return InterlockedExchange(&reported, 0); }

static void LogBuffers(CTX *c, const char *stage, int which)
{
    ID3D11Buffer *b[14] = {0};
    UINT first[14] = {0}, num[14] = {0};
    ID3D11DeviceContext1 *c1 = (ID3D11DeviceContext1 *)c; // the context answers ID3D11DeviceContext1 (checked in pn_ctx_hook)
    if (which == 0) ID3D11DeviceContext1_VSGetConstantBuffers1(c1, 0, 14, b, first, num);
    else if (which == 1) ID3D11DeviceContext1_PSGetConstantBuffers1(c1, 0, 14, b, first, num);
    else ID3D11DeviceContext1_GSGetConstantBuffers1(c1, 0, 14, b, first, num);
    char line[1200];
    int used = sprintf_s(line, sizeof line, "  %s cb", stage), any = 0;
    for (int i = 0; i < 14; i++)
    {
        if (!b[i]) continue;
        D3D11_BUFFER_DESC d;
        ID3D11Buffer_GetDesc(b[i], &d);
        used += sprintf_s(line + used, sizeof line - (size_t)used, " b%d=%p size=%u usage=%d cpu=0x%x first=%u num=%u", i, (void *)b[i], d.ByteWidth, (int)d.Usage, d.CPUAccessFlags,
                          first[i], num[i]);
        ID3D11Buffer_Release(b[i]);
        any = 1;
    }
    if (any) Log("%s", line);
}

static void LogViews(CTX *c, const char *stage, int which)
{
    ID3D11ShaderResourceView *v[16] = {0};
    ID3D11SamplerState *s[16] = {0};
    if (which == 0) { ID3D11DeviceContext_VSGetShaderResources(c, 0, 16, v); ID3D11DeviceContext_VSGetSamplers(c, 0, 16, s); }
    else if (which == 1) { ID3D11DeviceContext_PSGetShaderResources(c, 0, 16, v); ID3D11DeviceContext_PSGetSamplers(c, 0, 16, s); }
    else { ID3D11DeviceContext_GSGetShaderResources(c, 0, 16, v); ID3D11DeviceContext_GSGetSamplers(c, 0, 16, s); }
    char line[2000];
    int used = sprintf_s(line, sizeof line, "  %s srv", stage), any = 0;
    for (int i = 0; i < 16 && used < (int)sizeof line - 160; i++)
    {
        if (!v[i]) continue;
        D3D11_SHADER_RESOURCE_VIEW_DESC d;
        ID3D11ShaderResourceView_GetDesc(v[i], &d);
        ID3D11Resource *r = NULL;
        ID3D11ShaderResourceView_GetResource(v[i], &r);
        UINT w = 0, h = 0;
        D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
        if (r)
        {
            ID3D11Resource_GetType(r, &dim);
            if (dim == D3D11_RESOURCE_DIMENSION_TEXTURE2D) { D3D11_TEXTURE2D_DESC t; ID3D11Texture2D_GetDesc((ID3D11Texture2D *)r, &t); w = t.Width; h = t.Height; }
            ID3D11Resource_Release(r);
        }
        used += sprintf_s(line + used, sizeof line - (size_t)used, " t%d=%p fmt=%d dim=%d %ux%u", i, (void *)v[i], (int)d.Format, (int)d.ViewDimension, w, h);
        ID3D11ShaderResourceView_Release(v[i]);
        any = 1;
    }
    if (any) Log("%s", line);
    used = sprintf_s(line, sizeof line, "  %s samplers", stage);
    any = 0;
    for (int i = 0; i < 16; i++)
    {
        if (!s[i]) continue;
        if (FirstSight(s[i]))
        {
            D3D11_SAMPLER_DESC d;
            ID3D11SamplerState_GetDesc(s[i], &d);
            Log("SAMPLERDESC %p filter=0x%x address=%d/%d/%d mipLODBias=%.9g maxAniso=%u comparison=%d border=%g,%g,%g,%g minLOD=%g maxLOD=%g", (void *)s[i], (int)d.Filter,
                (int)d.AddressU, (int)d.AddressV, (int)d.AddressW, d.MipLODBias, d.MaxAnisotropy, (int)d.ComparisonFunc, d.BorderColor[0], d.BorderColor[1], d.BorderColor[2],
                d.BorderColor[3], d.MinLOD, d.MaxLOD);
        }
        used += sprintf_s(line + used, sizeof line - (size_t)used, " s%d=%p", i, (void *)s[i]);
        ID3D11SamplerState_Release(s[i]);
        any = 1;
    }
    if (any) Log("%s", line);
}

static void LogDraw(CTX *c, const char *what)
{
    ID3D11VertexShader *vs = NULL;
    ID3D11PixelShader *ps = NULL;
    ID3D11GeometryShader *gs = NULL;
    ID3D11InputLayout *il = NULL;
    ID3D11RasterizerState *rs = NULL;
    ID3D11BlendState *bs = NULL;
    ID3D11DepthStencilState *ds = NULL;
    D3D11_PRIMITIVE_TOPOLOGY topo = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
    FLOAT factor[4];
    UINT mask = 0, sref = 0;
    ID3D11DeviceContext_VSGetShader(c, &vs, NULL, NULL);
    ID3D11DeviceContext_PSGetShader(c, &ps, NULL, NULL);
    ID3D11DeviceContext_GSGetShader(c, &gs, NULL, NULL);
    ID3D11DeviceContext_IAGetInputLayout(c, &il);
    ID3D11DeviceContext_RSGetState(c, &rs);
    ID3D11DeviceContext_OMGetBlendState(c, &bs, factor, &mask);
    ID3D11DeviceContext_OMGetDepthStencilState(c, &ds, &sref);
    ID3D11DeviceContext_IAGetPrimitiveTopology(c, &topo);
    EnterCriticalSection(&lock);
    int n = drawsLogged, isNew = 1;
    for (int i = 0; i < combos; i++)
        if (comboSeen[i].vs == vs && comboSeen[i].ps == ps && comboSeen[i].gs == gs && comboSeen[i].il == il && comboSeen[i].rs == rs && comboSeen[i].bs == bs &&
            comboSeen[i].ds == ds && comboSeen[i].topo == (int)topo) { isNew = 0; break; }
    if (isNew && combos < COMBOS)
    {
        comboSeen[combos].vs = vs; comboSeen[combos].ps = ps; comboSeen[combos].gs = gs; comboSeen[combos].il = il;
        comboSeen[combos].rs = rs; comboSeen[combos].bs = bs; comboSeen[combos].ds = ds; comboSeen[combos].topo = (int)topo;
        combos++;
    }
    int detail = isNew || n < 40;
    drawsLogged++;
    LeaveCriticalSection(&lock);
    LONG draws = 0;
    for (int f = F_Draw; f <= F_DrawInstancedIndirect; f++) draws += calls[f];
    Log("DRAW %ld %s ctx=%p vs=%p ps=%p gs=%p il=%p topo=%d rs=%p bs=%p factor=%g,%g,%g,%g mask=0x%x ds=%p stencilRef=%u%s", draws, what, (void *)c, (void *)vs, (void *)ps,
        (void *)gs, (void *)il, (int)topo, (void *)rs, (void *)bs, factor[0], factor[1], factor[2], factor[3], mask, (void *)ds, sref, isNew ? " new" : "");
    if (detail)
    {
        if (rs && FirstSight(rs))
        {
            D3D11_RASTERIZER_DESC d;
            ID3D11RasterizerState_GetDesc(rs, &d);
            Log("RSDESC %p fill=%d cull=%d frontCCW=%d depthBias=%d depthBiasClamp=%.9g slopeScaledDepthBias=%.9g depthClip=%d scissor=%d msaa=%d aaLine=%d", (void *)rs,
                (int)d.FillMode, (int)d.CullMode, d.FrontCounterClockwise, d.DepthBias, d.DepthBiasClamp, d.SlopeScaledDepthBias, d.DepthClipEnable, d.ScissorEnable,
                d.MultisampleEnable, d.AntialiasedLineEnable);
        }
        if (bs && FirstSight(bs))
        {
            D3D11_BLEND_DESC d;
            ID3D11BlendState_GetDesc(bs, &d);
            const D3D11_RENDER_TARGET_BLEND_DESC *t = &d.RenderTarget[0];
            Log("BSDESC %p alphaToCoverage=%d independent=%d rt0 enable=%d src=%d dst=%d op=%d srcA=%d dstA=%d opA=%d mask=0x%x", (void *)bs, d.AlphaToCoverageEnable,
                d.IndependentBlendEnable, t->BlendEnable, (int)t->SrcBlend, (int)t->DestBlend, (int)t->BlendOp, (int)t->SrcBlendAlpha, (int)t->DestBlendAlpha, (int)t->BlendOpAlpha,
                t->RenderTargetWriteMask);
        }
        if (ds && FirstSight(ds))
        {
            D3D11_DEPTH_STENCIL_DESC d;
            ID3D11DepthStencilState_GetDesc(ds, &d);
            Log("DSDESC %p depth=%d write=%d func=%d stencil=%d read=0x%x write=0x%x front=%d/%d/%d/%d back=%d/%d/%d/%d", (void *)ds, d.DepthEnable, (int)d.DepthWriteMask,
                (int)d.DepthFunc, d.StencilEnable, d.StencilReadMask, d.StencilWriteMask, (int)d.FrontFace.StencilFailOp, (int)d.FrontFace.StencilDepthFailOp,
                (int)d.FrontFace.StencilPassOp, (int)d.FrontFace.StencilFunc, (int)d.BackFace.StencilFailOp, (int)d.BackFace.StencilDepthFailOp, (int)d.BackFace.StencilPassOp,
                (int)d.BackFace.StencilFunc);
        }
        ID3D11Buffer *vb[4] = {0};
        UINT stride[4] = {0}, off[4] = {0};
        ID3D11DeviceContext_IAGetVertexBuffers(c, 0, 4, vb, stride, off);
        ID3D11Buffer *ib = NULL;
        DXGI_FORMAT ibf = DXGI_FORMAT_UNKNOWN;
        UINT iboff = 0;
        ID3D11DeviceContext_IAGetIndexBuffer(c, &ib, &ibf, &iboff);
        Log("  ia vb0=%p stride=%u off=%u vb1=%p stride=%u off=%u vb2=%p stride=%u ib=%p fmt=%d off=%u", (void *)vb[0], stride[0], off[0], (void *)vb[1], stride[1], off[1],
            (void *)vb[2], stride[2], (void *)ib, (int)ibf, iboff);
        for (int i = 0; i < 4; i++) if (vb[i]) ID3D11Buffer_Release(vb[i]);
        if (ib) ID3D11Buffer_Release(ib);
        LogBuffers(c, "vs", 0);
        LogBuffers(c, "ps", 1);
        if (gs) LogBuffers(c, "gs", 2);
        LogViews(c, "vs", 0);
        LogViews(c, "ps", 1);
        D3D11_VIEWPORT vp[1];
        UINT nvp = 1;
        ID3D11DeviceContext_RSGetViewports(c, &nvp, vp);
        D3D11_RECT sc[1];
        UINT nsc = 1;
        ID3D11DeviceContext_RSGetScissorRects(c, &nsc, sc);
        ID3D11RenderTargetView *rtv[2] = {0};
        ID3D11DepthStencilView *dsv = NULL;
        ID3D11DeviceContext_OMGetRenderTargets(c, 2, rtv, &dsv);
        int rf = -1, df = -1;
        UINT rw = 0, rh = 0;
        if (rtv[0])
        {
            D3D11_RENDER_TARGET_VIEW_DESC d;
            ID3D11RenderTargetView_GetDesc(rtv[0], &d);
            rf = (int)d.Format;
            ID3D11Resource *r = NULL;
            ID3D11RenderTargetView_GetResource(rtv[0], &r);
            if (r) { D3D11_TEXTURE2D_DESC t; ID3D11Texture2D_GetDesc((ID3D11Texture2D *)r, &t); rw = t.Width; rh = t.Height; ID3D11Resource_Release(r); }
        }
        if (dsv) { D3D11_DEPTH_STENCIL_VIEW_DESC d; ID3D11DepthStencilView_GetDesc(dsv, &d); df = (int)d.Format; }
        Log("  om rtv0=%p fmt=%d %ux%u rtv1=%p dsv=%p fmt=%d vp=(%g,%g,%g,%g,%g..%g) n=%u scissor n=%u (%ld,%ld,%ld,%ld)", (void *)rtv[0], rf, rw, rh, (void *)rtv[1], (void *)dsv,
            df, nvp ? vp[0].TopLeftX : 0, nvp ? vp[0].TopLeftY : 0, nvp ? vp[0].Width : 0, nvp ? vp[0].Height : 0, nvp ? vp[0].MinDepth : 0, nvp ? vp[0].MaxDepth : 0, nvp, nsc,
            nsc ? sc[0].left : 0, nsc ? sc[0].top : 0, nsc ? sc[0].right : 0, nsc ? sc[0].bottom : 0);
        for (int i = 0; i < 2; i++) if (rtv[i]) ID3D11RenderTargetView_Release(rtv[i]);
        if (dsv) ID3D11DepthStencilView_Release(dsv);
    }
    if (vs) ID3D11VertexShader_Release(vs);
    if (ps) ID3D11PixelShader_Release(ps);
    if (gs) ID3D11GeometryShader_Release(gs);
    if (il) ID3D11InputLayout_Release(il);
    if (rs) ID3D11RasterizerState_Release(rs);
    if (bs) ID3D11BlendState_Release(bs);
    if (ds) ID3D11DepthStencilState_Release(ds);
}

static void Report(void)
{
    LARGE_INTEGER now, f;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&f);
    Log("FRAME recorded %.3f ms between Presents", (double)(now.QuadPart - recStart.QuadPart) * 1000.0 / (double)f.QuadPart);
    for (int i = 0; i < F_Count; i++)
        if (calls[i]) Log("FRAME %s calls=%ld changes=%ld", fnNames[i], calls[i], changes[i]);
    for (int i = 0; i < CTXS && ctxSeen[i]; i++) Log("FRAME context %p calls=%ld draws=%ld", ctxSeen[i], ctxCalls[i], ctxDraws[i]);
    static const char *const kinds3[3] = {"constant buffer", "other buffer", "texture"};
    for (int k = 0; k < 3; k++)
        Log("FRAME Map %s: READ=%ld WRITE=%ld READ_WRITE=%ld WRITE_DISCARD=%ld WRITE_NO_OVERWRITE=%ld; UpdateSubresource=%ld", kinds3[k], maps[k][1], maps[k][2], maps[k][3],
            maps[k][4], maps[k][5], updates[k]);
    Log("FRAME constant buffer bytes: mapped %lld, updated %lld; mapped by size <=64:%ld <=128:%ld <=256:%ld <=512:%ld <=1K:%ld <=4K:%ld <=16K:%ld more:%ld", cbMapBytes,
        cbUpdateBytes, cbSizes[0], cbSizes[1], cbSizes[2], cbSizes[3], cbSizes[4], cbSizes[5], cbSizes[6], cbSizes[7]);
    Log("FRAME distinct state combinations=%ld draws=%ld", combos, drawsLogged);
    for (int i = 0; i < nhooks; i++)
        if (hooks[i].rewrites) Log("FRAME slot %s rewritten by the runtime %ld times", hooks[i].name, hooks[i].rewrites);
}

/// Called by the Present hooks before the real Present: ends a recording, starts an armed one.
static void FrameBoundary(void)
{
    if (recording)
    {
        InterlockedExchange(&recording, 0);
        Report();
        InterlockedExchange(&reported, 1);
    }
    if (InterlockedExchange(&armed, 0))
    {
        memset(calls, 0, sizeof calls); memset(changes, 0, sizeof changes); memset(ctxSeen, 0, sizeof ctxSeen); memset(ctxDraws, 0, sizeof ctxDraws);
        memset(ctxCalls, 0, sizeof ctxCalls); memset(maps, 0, sizeof maps); memset(updates, 0, sizeof updates); memset(cbSizes, 0, sizeof cbSizes);
        cbMapBytes = cbUpdateBytes = 0; drawsLogged = combos = 0; nobj = 0;
        lastVS = lastPS = lastGS = lastIL = lastRS = lastBS = lastDS = NULL; lastTopo = -1;
        for (int i = 0; i < nhooks; i++) hooks[i].rewrites = 0;
        if (context) Repatch(context);
        QueryPerformanceCounter(&recStart);
        InterlockedExchange(&recording, 1);
    }
}
