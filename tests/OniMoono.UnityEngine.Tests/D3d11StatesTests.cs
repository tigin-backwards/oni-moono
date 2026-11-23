using OniMoono.Desktop.D3d11;
using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// The Direct3D 11 device's state descriptions against the original's, as measured
    /// (docs/research/d3d11-captures.md §2: Hidden/Internal-Colored drawn into render textures
    /// with a range of `_ZBias`, `_Cull`, `_ZTest` and `_ZWrite`, the state read back).
    public class D3d11StatesTests
    {
        static ResolvedState Colored(float zBias = 0, int cull = 0, int zTest = 4, int zWrite = 1) => new()
        {
            Src = 5, Dst = 10, SrcA = 5, DstA = 10, Mask = 15, ZTest = zTest, ZWrite = zWrite, Cull = cull, OffsetFactor = zBias, OffsetUnits = zBias,
            ReadMask = 255, WriteMask = 255,
        };

        [Theory]
        // _ZBias (factor = units) → DepthBias, SlopeScaledDepthBias: the measured table.
        [InlineData(0f, 0, -0f)]
        [InlineData(1f, -1, -1f)]
        [InlineData(-1f, 1, 1f)]
        [InlineData(2f, -2, -2f)]
        [InlineData(0.5f, 0, -0.5f)]
        [InlineData(-3.25f, 3, 3.25f)]
        [InlineData(10f, -10, -10f)]
        [InlineData(100f, -100, -100f)]
        [InlineData(1.5f, -1, -1.5f)]
        [InlineData(-1.5f, 1, 1.5f)]
        [InlineData(2.5f, -2, -2.5f)]
        [InlineData(-2.5f, 2, 2.5f)]
        [InlineData(0.75f, 0, -0.75f)]
        [InlineData(-0.75f, 0, 0.75f)]
        public void DepthBiasIsTheOriginals(float zBias, int depthBias, float slope)
        {
            var d = D3d11States.RasterizerDesc(Colored(zBias), flipped: true, scissor: false);
            Assert.Equal((depthBias, slope, 0f), (d.DepthBias, d.SlopeScaledDepthBias, d.DepthBiasClamp));
        }

        [Fact]
        public void RasterizerIntoARenderTextureIsTheOriginals()
        {
            // Cull Off/Front/Back → NONE/FRONT/BACK; FillMode SOLID, FrontCounterClockwise 1,
            // DepthClipEnable 1, ScissorEnable 0, MultisampleEnable 1, AntialiasedLineEnable 0.
            foreach (var (cull, mode) in new[] { (0, D3D11_CULL_MODE.D3D11_CULL_NONE), (1, D3D11_CULL_MODE.D3D11_CULL_FRONT), (2, D3D11_CULL_MODE.D3D11_CULL_BACK) })
            {
                var d = D3d11States.RasterizerDesc(Colored(cull: cull), flipped: true, scissor: false);
                Assert.Equal((D3D11_FILL_MODE.D3D11_FILL_SOLID, mode, 1, 1, 0, 1, 0),
                    (d.FillMode, d.CullMode, d.FrontCounterClockwise, d.DepthClipEnable, d.ScissorEnable, d.MultisampleEnable, d.AntialiasedLineEnable));
            }
            // The back buffer's (the colony's UI draws, FrontCounterClockwise 0).
            Assert.Equal(0, D3d11States.RasterizerDesc(Colored(), flipped: false, scissor: false).FrontCounterClockwise);
        }

        [Theory]
        // ZTest Less, Equal, LEqual, Greater, NotEqual, GEqual, Always → the measured DepthFunc.
        [InlineData(2, 5)] // GREATER
        [InlineData(3, 3)] // EQUAL
        [InlineData(4, 7)] // GREATER_EQUAL
        [InlineData(5, 2)] // LESS
        [InlineData(6, 6)] // NOT_EQUAL
        [InlineData(7, 4)] // LESS_EQUAL
        [InlineData(8, 8)] // ALWAYS
        public void DepthTestIsTheOriginals(int zTest, int measured)
        {
            var func = (D3D11_COMPARISON_FUNC)measured;
            var d = D3d11States.DepthStencilDesc(Colored(zTest: zTest), depth: true, stencil: true);
            // ZTest Always keeps DepthEnable 1; no stencil block: StencilEnable 0, masks 0xFF,
            // KEEP/KEEP/KEEP, ALWAYS.
            Assert.Equal((1, func, D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL, 0, (byte)255, (byte)255), (d.DepthEnable, d.DepthFunc, d.DepthWriteMask, d.StencilEnable, d.StencilReadMask, d.StencilWriteMask));
            Assert.Equal((D3D11_STENCIL_OP.D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP.D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP.D3D11_STENCIL_OP_KEEP, D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS),
                (d.FrontFace.StencilFailOp, d.FrontFace.StencilDepthFailOp, d.FrontFace.StencilPassOp, d.FrontFace.StencilFunc));
            Assert.Equal(D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO, D3d11States.DepthStencilDesc(Colored(zTest: zTest, zWrite: 0), true, true).DepthWriteMask);
        }

        [Fact]
        public void BlendIsTheOriginals()
        {
            // Blend SrcAlpha OneMinusSrcAlpha: BlendEnable 1, SRC_ALPHA / INV_SRC_ALPHA, ADD, the
            // same for alpha, write mask 0xF.
            var t = D3d11States.BlendDesc(Colored()).RenderTarget[0];
            Assert.Equal((1, D3D11_BLEND.D3D11_BLEND_SRC_ALPHA, D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA, D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                    D3D11_BLEND.D3D11_BLEND_SRC_ALPHA, D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA, D3D11_BLEND_OP.D3D11_BLEND_OP_ADD, (byte)0xF),
                (t.BlendEnable, t.SrcBlend, t.DestBlend, t.BlendOp, t.SrcBlendAlpha, t.DestBlendAlpha, t.BlendOpAlpha, t.RenderTargetWriteMask));
            // Zero / SrcColor with alpha Zero / SrcColor: the colony's (src 1, dst 3) with alpha
            // Zero / SrcAlpha.
            var mul = D3d11States.BlendDesc(new ResolvedState { Src = 0, Dst = 3, SrcA = 0, DstA = 3, Mask = 15 }).RenderTarget[0];
            Assert.Equal((D3D11_BLEND.D3D11_BLEND_ZERO, D3D11_BLEND.D3D11_BLEND_SRC_COLOR, D3D11_BLEND.D3D11_BLEND_ZERO, D3D11_BLEND.D3D11_BLEND_SRC_ALPHA),
                (mul.SrcBlend, mul.DestBlend, mul.SrcBlendAlpha, mul.DestBlendAlpha));
            // One One with alpha op Max (Unity BlendOp 4).
            var max = D3d11States.BlendDesc(new ResolvedState { Src = 1, Dst = 1, SrcA = 1, DstA = 1, OpA = 4, Mask = 15 }).RenderTarget[0];
            Assert.Equal(D3D11_BLEND_OP.D3D11_BLEND_OP_MAX, max.BlendOpAlpha);
            // One Zero (off) and the colour mask: RGB (Unity 14) is 0x7.
            var off = D3d11States.BlendDesc(new ResolvedState { Src = 1, Dst = 0, SrcA = 1, DstA = 0, Mask = 14 }).RenderTarget[0];
            Assert.Equal((0, (byte)0x7), (off.BlendEnable, off.RenderTargetWriteMask));
        }
    }
}
