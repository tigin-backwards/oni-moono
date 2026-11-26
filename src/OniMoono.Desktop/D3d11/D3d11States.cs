using System;
using System.Collections.Generic;
using OniMoono.Desktop.Gl;

namespace OniMoono.Desktop.D3d11
{
    /// The rasterizer, depth-stencil and blend state objects for the pass states ShaderLab
    /// resolves (ResolvedState), made on first use and kept, with the original's mapping as
    /// measured (docs/research/d3d11-captures.md §2):
    /// - Rasterizer: Cull Off/Front/Back as CULL_NONE/FRONT/BACK; DepthBias = -trunc(units),
    ///   SlopeScaledDepthBias = -factor, DepthBiasClamp 0 (reversed Z, truncated toward zero);
    ///   FillMode SOLID, DepthClipEnable 1, MultisampleEnable 1, AntialiasedLineEnable 0.
    ///   FrontCounterClockwise 1 into a render texture (measured on the probe's draws into render
    ///   textures) and 0 into the back buffer (the colony frame's UI draws had 0; that they are
    ///   the screen's, d3d11-captures.md item 6, settled in M3; a
    ///   render texture is rendered upside down, which turns its winding over, as VkPipelines'
    ///   front face). ScissorEnable as the draw's scissor (Unity's player keeps it 0 with no
    ///   scissor).
    /// - Depth-stencil: the comparison reversed (Less as GREATER, LEqual as GREATER_EQUAL...),
    ///   ZTest Always keeps DepthEnable 1 (measured); with no depth surface bound, or ZTest
    ///   Disabled (0), no test (as VkPipelines); ZWrite as the write mask; stencil enabled when a
    ///   face's comparison is set and the surface has stencil, ops and masks as ShaderLab gives
    ///   them.
    /// - Blend: One/Zero with Add for colour and alpha is off; Unity's factors and ops as D3D11's
    ///   (BlendMode and BlendOp numbering, d3d11.h's D3D11_BLEND and D3D11_BLEND_OP). Colour
    ///   factors in the alpha slots become their alpha forms: "Blend options that end in _COLOR
    ///   are not allowed" there (D3D11_RENDER_TARGET_BLEND_DESC), and the original sets Zero /
    ///   SrcColor's alpha as Zero / SrcAlpha (measured). One state for every render target
    ///   (IndependentBlendEnable 0), as OpenGL's one blend state.
    internal sealed unsafe class D3d11States(ID3D11Device* device)
    {
        readonly Dictionary<(int Cull, float Units, float Factor, bool Flipped, bool Scissor), nint> rasterizers = new();
        readonly Dictionary<(int Src, int Dst, int SrcA, int DstA, int Op, int OpA, int Mask, int AlphaToMask), nint> blends = new();
        readonly Dictionary<(int ZTest, int ZWrite, int ReadMask, int WriteMask, int FComp, int FPass, int FFail, int FZFail, int BComp, int BPass, int BFail, int BZFail, bool Depth, bool Stencil), nint> depths = new();

        public int Made => rasterizers.Count + blends.Count + depths.Count;

        public ID3D11RasterizerState* Rasterizer(in ResolvedState s, bool flipped, bool scissor)
        {
            var key = (s.Cull, s.OffsetUnits, s.OffsetFactor, flipped, scissor);
            if (rasterizers.TryGetValue(key, out var known)) return (ID3D11RasterizerState*)known;
            var desc = RasterizerDesc(s, flipped, scissor);
            ID3D11RasterizerState* state;
            D3d11Context.Check(device->lpVtbl->CreateRasterizerState(device, &desc, &state), "CreateRasterizerState");
            rasterizers[key] = (nint)state;
            return state;
        }

        public static D3D11_RASTERIZER_DESC RasterizerDesc(in ResolvedState s, bool flipped, bool scissor) => new D3D11_RASTERIZER_DESC
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = s.Cull == 1 ? D3D11_CULL_MODE.D3D11_CULL_FRONT : s.Cull == 2 ? D3D11_CULL_MODE.D3D11_CULL_BACK : D3D11_CULL_MODE.D3D11_CULL_NONE,
            FrontCounterClockwise = flipped ? 1 : 0,
            DepthBias = -(int)MathF.Truncate(s.OffsetUnits),
            SlopeScaledDepthBias = -s.OffsetFactor,
            DepthBiasClamp = 0f,
            DepthClipEnable = 1,
            ScissorEnable = scissor ? 1 : 0,
            MultisampleEnable = 1,
            AntialiasedLineEnable = 0,
        };

        public ID3D11BlendState* Blend(in ResolvedState s)
        {
            var key = (s.Src, s.Dst, s.SrcA, s.DstA, s.Op, s.OpA, s.Mask, s.AlphaToMask);
            if (blends.TryGetValue(key, out var known)) return (ID3D11BlendState*)known;
            var desc = BlendDesc(s);
            ID3D11BlendState* state;
            D3d11Context.Check(device->lpVtbl->CreateBlendState(device, &desc, &state), "CreateBlendState");
            blends[key] = (nint)state;
            return state;
        }

        public static D3D11_BLEND_DESC BlendDesc(in ResolvedState s)
        {
            bool on = !(s.Src == 1 && s.Dst == 0 && s.SrcA == 1 && s.DstA == 0 && s.Op == 0 && s.OpA == 0);
            byte mask = (byte)(((s.Mask & 8) != 0 ? 1 : 0) | ((s.Mask & 4) != 0 ? 2 : 0) | ((s.Mask & 2) != 0 ? 4 : 0) | ((s.Mask & 1) != 0 ? 8 : 0));
            var target = new D3D11_RENDER_TARGET_BLEND_DESC
            {
                BlendEnable = on ? 1 : 0,
                SrcBlend = Factor(s.Src), DestBlend = Factor(s.Dst), BlendOp = Op(s.Op),
                SrcBlendAlpha = AlphaFactor(s.SrcA), DestBlendAlpha = AlphaFactor(s.DstA), BlendOpAlpha = Op(s.OpA),
                RenderTargetWriteMask = mask,
            };
            var desc = new D3D11_BLEND_DESC { AlphaToCoverageEnable = s.AlphaToMask != 0 ? 1 : 0, IndependentBlendEnable = 0 };
            for (int i = 0; i < 8; i++) desc.RenderTarget[i] = target;
            return desc;
        }

        public ID3D11DepthStencilState* DepthStencil(in ResolvedState s, bool depth, bool stencil)
        {
            var key = (s.ZTest, s.ZWrite, s.ReadMask, s.WriteMask, s.FComp, s.FPass, s.FFail, s.FZFail, s.BComp, s.BPass, s.BFail, s.BZFail, depth, stencil);
            if (depths.TryGetValue(key, out var known)) return (ID3D11DepthStencilState*)known;
            var desc = DepthStencilDesc(s, depth, stencil);
            ID3D11DepthStencilState* state;
            D3d11Context.Check(device->lpVtbl->CreateDepthStencilState(device, &desc, &state), "CreateDepthStencilState");
            depths[key] = (nint)state;
            return state;
        }

        public static D3D11_DEPTH_STENCIL_DESC DepthStencilDesc(in ResolvedState s, bool depth, bool stencil)
        {
            bool test = depth && s.ZTest != 0;
            return new D3D11_DEPTH_STENCIL_DESC
            {
                DepthEnable = test ? 1 : 0,
                DepthWriteMask = test && s.ZWrite != 0 ? D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL : D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO,
                DepthFunc = Compare(Vulkan.VkPipelines.Reversed(s.ZTest)),
                StencilEnable = stencil && (s.FComp != 0 || s.BComp != 0) ? 1 : 0,
                StencilReadMask = (byte)s.ReadMask,
                StencilWriteMask = (byte)s.WriteMask,
                FrontFace = Face(s.FComp, s.FPass, s.FFail, s.FZFail),
                BackFace = Face(s.BComp, s.BPass, s.BFail, s.BZFail),
            };
        }

        static D3D11_DEPTH_STENCILOP_DESC Face(int comp, int pass, int fail, int zFail) => new()
        {
            StencilFunc = Compare(comp == 0 ? 8 : comp),
            StencilPassOp = StencilOp(pass),
            StencilFailOp = StencilOp(fail),
            StencilDepthFailOp = StencilOp(zFail),
        };

        /// Unity CompareFunction (1 Never … 8 Always) is D3D11_COMPARISON_FUNC's numbering; 0
        /// (Disabled) and others as ALWAYS.
        static D3D11_COMPARISON_FUNC Compare(int c) => c is >= 1 and <= 8 ? (D3D11_COMPARISON_FUNC)c : D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS;

        /// Unity StencilOp (0 Keep, 1 Zero, 2 Replace, 3 IncrementSaturate, 4 DecrementSaturate,
        /// 5 Invert, 6 IncrementWrap, 7 DecrementWrap) is D3D11_STENCIL_OP's order from 1.
        static D3D11_STENCIL_OP StencilOp(int op) => op is >= 0 and <= 7 ? (D3D11_STENCIL_OP)(op + 1) : D3D11_STENCIL_OP.D3D11_STENCIL_OP_KEEP;

        /// Unity BlendMode → D3D11_BLEND.
        static D3D11_BLEND Factor(int m) => m switch
        {
            0 => D3D11_BLEND.D3D11_BLEND_ZERO,
            1 => D3D11_BLEND.D3D11_BLEND_ONE,
            2 => D3D11_BLEND.D3D11_BLEND_DEST_COLOR,
            3 => D3D11_BLEND.D3D11_BLEND_SRC_COLOR,
            4 => D3D11_BLEND.D3D11_BLEND_INV_DEST_COLOR,
            5 => D3D11_BLEND.D3D11_BLEND_SRC_ALPHA,
            6 => D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR,
            7 => D3D11_BLEND.D3D11_BLEND_DEST_ALPHA,
            8 => D3D11_BLEND.D3D11_BLEND_INV_DEST_ALPHA,
            9 => D3D11_BLEND.D3D11_BLEND_SRC_ALPHA_SAT,
            10 => D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
            _ => D3D11_BLEND.D3D11_BLEND_ONE,
        };

        static D3D11_BLEND AlphaFactor(int m) => Factor(m) switch
        {
            D3D11_BLEND.D3D11_BLEND_SRC_COLOR => D3D11_BLEND.D3D11_BLEND_SRC_ALPHA,
            D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR => D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
            D3D11_BLEND.D3D11_BLEND_DEST_COLOR => D3D11_BLEND.D3D11_BLEND_DEST_ALPHA,
            D3D11_BLEND.D3D11_BLEND_INV_DEST_COLOR => D3D11_BLEND.D3D11_BLEND_INV_DEST_ALPHA,
            var f => f,
        };

        /// Unity BlendOp (0 Add, 1 Subtract, 2 ReverseSubtract, 3 Min, 4 Max) → D3D11_BLEND_OP;
        /// the logical and advanced ops as Add (as VkPipelines).
        static D3D11_BLEND_OP Op(int op) => op is >= 0 and <= 4 ? (D3D11_BLEND_OP)(op + 1) : D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;

        public void Release()
        {
            foreach (var p in rasterizers.Values) { var s = (ID3D11RasterizerState*)p; Com.Release(ref s); }
            foreach (var p in blends.Values) { var s = (ID3D11BlendState*)p; Com.Release(ref s); }
            foreach (var p in depths.Values) { var s = (ID3D11DepthStencilState*)p; Com.Release(ref s); }
            rasterizers.Clear();
            blends.Clear();
            depths.Clear();
        }
    }
}
