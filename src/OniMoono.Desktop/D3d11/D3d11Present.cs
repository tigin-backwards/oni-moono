using System;
using System.Collections.Generic;
using System.Diagnostics;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Desktop.D3d11
{
    /// The Direct3D 11 device's presentation (docs/research/d3d11-plan.md, M4), on the device
    /// thread, which alone uses the immediate context and the swap chain:
    ///
    /// - **The screen** is drawn into one of two screen textures of the device's own, at the
    ///   rendering size (Screen.width/height: smaller than the window in a fullscreen window the
    ///   game asked a lower resolution of), never into the swap chain's buffer. At its present the
    ///   frame's texture is copied into the back buffer (CopyResource), or stretched over it
    ///   bilinearly when the sizes differ (as GlPresenter and VkPresenter stretch), and the next
    ///   frame draws into the other one. So the last frame shown stays whole while the next is
    ///   drawn, which the repeats, the splash and the loading animation show again. A frame that
    ///   drew nothing into the screen shows the last frame again (GlPresenter.Choose).
    /// - **Between frames** (GlWorker's idle work, while no frame is handed over) the device
    ///   thread shows the last frame again: with the splash over it about 60 times a second while
    ///   the start-up splash runs (SplashRain); with the loading animation over it 30 times a
    ///   second while a save loads (D3d11LoadAnimation, LoadingScreen); and once no frame has
    ///   come for a while, unchanged, as a repeat (StallRepeats). As the presenters of the other
    ///   devices, in that order of precedence.
    /// - Those pictures are drawn with the presenter's own program (window-space quads, texel *
    ///   mul + add), as VkSplash and GlSplash draw with their own: they are the player's
    ///   presentation, not the game's. After one, the device's tracked bindings are forgotten and
    ///   its target set again (RestoreAfterPresenter), so a frame drawn in parts (a flush in the
    ///   middle of it) goes on as it was.
    internal sealed unsafe partial class D3d11Device
    {
        /// The screen textures' views (and reads); the textures themselves are R8G8B8A8_TYPELESS,
        /// as the original's screen texture is, with R8G8B8A8_UNORM render target views
        /// (j0009056, j0009060: "format=27 ... bind=0x28", its view "format=28").
        const DXGI_FORMAT ScreenFormat = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;

        /// One screen texture: what the game draws into for the back buffer (SetTarget).
        internal sealed class ScreenTexture
        {
            public ID3D11Texture2D* Texture;
            public ID3D11RenderTargetView* Rtv;
            public ID3D11ShaderResourceView* Srv;
            public int Width, Height, Index, DrawnFrame = -1;
            public bool DrawnThisFrame, DrawnSinceAllocation;

            public void Drew() => DrawnThisFrame = DrawnSinceAllocation = true;

            public void Release()
            {
                Com.Release(ref Srv);
                Com.Release(ref Rtv);
                Com.Release(ref Texture);
            }
        }

        readonly ScreenTexture[] screens = [new() { Index = 0 }, new() { Index = 1 }];
        int currentScreen;
        /// The screen texture whose frame was presented last (null before the first).
        ScreenTexture shownScreen;
        /// The swap chain was resized since the frame shown: its picture is not repeated (a
        /// repeat then shows it stretched or cut, as VkPresenter's do not).
        bool resizedSinceShown;

        ScreenTexture CurrentScreen => screens[currentScreen];

        /// Makes `s` `w` x `h` when it is not (device thread, between frames): cleared, nothing
        /// drawn since.
        void EnsureScreen(ScreenTexture s, int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (s.Texture != null && s.Width == w && s.Height == h) return;
            if (PresentTrace.Enabled) PresentTrace.Log($"d3d11: screen texture {s.Index} made {w}x{h} (was {s.Width}x{s.Height})");
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };
            var rtvDesc = new D3D11_RENDER_TARGET_VIEW_DESC { Format = ScreenFormat, ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D };
            var srvDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC { Format = ScreenFormat, ViewDimension = D3D11_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2D };
            srvDesc.Texture2D.MipLevels = 1;
            ID3D11Texture2D* t;
            D3d11Context.Check(Dev->lpVtbl->CreateTexture2D(Dev, &desc, null, &t), $"CreateTexture2D (screen {w}x{h})");
            ID3D11RenderTargetView* rtv = null;
            ID3D11ShaderResourceView* srv = null;
            try
            {
                D3d11Context.Check(Dev->lpVtbl->CreateRenderTargetView(Dev, (ID3D11Resource*)t, &rtvDesc, &rtv), "CreateRenderTargetView (screen)");
                D3d11Context.Check(Dev->lpVtbl->CreateShaderResourceView(Dev, (ID3D11Resource*)t, &srvDesc, &srv), "CreateShaderResourceView (screen)");
            }
            catch
            {
                Com.Release(ref rtv);
                Com.Release(ref t);
                throw;
            }
            s.Release();
            s.Texture = t;
            s.Rtv = rtv;
            s.Srv = srv;
            (s.Width, s.Height) = (w, h);
            s.DrawnThisFrame = s.DrawnSinceAllocation = false;
            s.DrawnFrame = -1;
            // Cleared, so a texture shown before anything is drawn into it is black.
            var black = stackalloc float[] { 0f, 0f, 0f, 0f };
            Ctx->lpVtbl->ClearRenderTargetView(Ctx, rtv, black);
            if (s == shownScreen) shownScreen = null;
        }

        void ReleaseScreens()
        {
            foreach (var s in screens) s.Release();
            shownScreen = null;
        }

        // ----- Present (device thread) -----

        /// Presents the frame (`frame`, the main thread's frame number), then makes the buffers
        /// the window's framebuffer size and the next screen texture the rendering size when they
        /// changed, sets the target again, and waits until the swap chain takes another frame
        /// before the next one renders (D3d11SwapChain.WaitForFrame).
        ///
        /// A minimized window: the platform keeps the framebuffer size it had (Unity's player
        /// does, GlfwPlatform.KeptFramebufferSize), so the buffers keep theirs and nothing is
        /// resized; a window that has never had a size (0x0, minimized from the start) keeps the
        /// swap chain's 1x1 or last size and is not resized every frame either. Frames go on, and
        /// Present and the wait as when shown (a flip-model swap chain does not report
        /// DXGI_STATUS_OCCLUDED: Microsoft's DXGI_STATUS list).
        void PresentNow(int frame, int interval, int windowWidth, int windowHeight, int renderWidth, int renderHeight)
        {
            if (lost != null) return;
            var b = CurrentScreen;
            var choice = GlPresenter.Choose(b.Texture != null, b.DrawnSinceAllocation, b.DrawnThisFrame, shownScreen != null);
            if (PresentTrace.Enabled && choice != GlPresenter.PresentChoice.Frame)
                PresentTrace.Log($"d3d11: frame {frame}: nothing drawn into screen texture {b.Index}: {(shownScreen != null ? $"frame {shownScreen.DrawnFrame} shown again" : "not presented")}");
            b.DrawnThisFrame = false;
            swapInterval = interval;
            if (choice != GlPresenter.PresentChoice.Nothing)
            {
                // The frame's screen texture is the device's target still: unbound before it is
                // read (set again below, RestoreAfterPresenter).
                Ctx->lpVtbl->OMSetRenderTargets(Ctx, 0, null, null);
                bool gameFrame = choice == GlPresenter.PresentChoice.Frame;
                if (gameFrame)
                {
                    b.DrawnFrame = frame;
                    shownScreen = b;
                    resizedSinceShown = false;
                }
                if (!Show(shownScreen, interval, gameFrame ? ShowKind.Frame : ShowKind.Again)) return;
                if (++presents % 600 == 0) DebugTally();
                PollReadbacks();
                if (gameFrame)
                {
                    FramePresented();
                    currentScreen = (currentScreen + 1) % screens.Length;
                }
            }
            try
            {
                if (windowWidth > 0 && windowHeight > 0 && (windowWidth != Out.Width || windowHeight != Out.Height))
                {
                    // ResizeBuffers needs every reference to the buffers released, the context's
                    // too: Resize clears the context's state, so nothing is bound any more.
                    Out.Resize(windowWidth, windowHeight);
                    ForgetBindings();
                    resizedSinceShown = true;
                }
                if (renderWidth > 0 && renderHeight > 0) EnsureScreen(CurrentScreen, renderWidth, renderHeight);
            }
            catch (D3d11UnavailableException e)
            {
                lost = e.Message;
                EngineLog.Engine($"d3d11: {lost}");
                return;
            }
            RestoreAfterPresenter(always: true);
            if (choice != GlPresenter.PresentChoice.Nothing) Out.WaitForFrame();
        }

        enum ShowKind { Frame, Again, Redraw, Repeat }

        /// The last QualitySettings.vSyncCount a frame was presented with (redraws take it).
        int swapInterval = -1;

        /// Puts `picture` (null: black) into the back buffer, the splash or the loading
        /// animation over it where they run, and presents it. False when presenting failed (the
        /// device is then lost).
        bool Show(ScreenTexture picture, int interval, ShowKind kind)
        {
            bool gameFrame = kind == ShowKind.Frame;
            bool splash = rain != null && rain.Active;
            if (gameFrame && splash) rain.GameFrame();
            // Nothing is drawn over a game frame; one after the loading screen's ends its animation.
            if (gameFrame) loading.GameFrame(GlLoadAnimation.Wanted(picture.DrawnFrame, LoadingScreen.ArmedFrame, LoadingScreen.OverlayFrame, 0));
            try
            {
                Picture(picture);
                if (splash && kind != ShowKind.Repeat) DrawSplash();
                else if (kind == ShowKind.Redraw && loading.Running) DrawLoading();
            }
            catch (D3d11UnavailableException e)
            {
                // A call the picture needs failed (a buffer the presenter makes or maps): the
                // device stops, as when IdleWork's picture fails, rather than presenting half a
                // picture and counting a failed command.
                lost = e.Message;
                EngineLog.Engine($"d3d11: {lost}");
                return false;
            }
            int hr = kind == ShowKind.Repeat ? Out.PresentRepeat() : Out.Present(interval);
            DrainDebugMessages(kind == ShowKind.Frame ? "Present" : $"Present ({kind})");
            if (hr < 0)
            {
                // Present reports a removed or reset device (DXGI_ERROR_DEVICE_REMOVED/RESET); the
                // reason is asked of the device ("Handle device removed scenarios in Direct3D 11").
                lost = Dxgi.DeviceLost(hr)
                    ? $"the graphics device was lost ({Dxgi.Hr(hr)}, reason {Dxgi.Hr(Dev->lpVtbl->GetDeviceRemovedReason(Dev))})"
                    : $"Present failed ({Dxgi.Hr(hr)})";
                EngineLog.Engine($"d3d11: {lost}");
                return false;
            }
            lastPicture = GlTimings.Now;
            if (kind == ShowKind.Redraw) loading.Shown();
            return true;
        }

        /// The last stretch traced (ONI_PRESENT_TRACE), so each size pair is logged once in a row.
        (int W, int H) stretchedFrom, stretchedTo;

        /// When the last picture was presented (Stopwatch ticks).
        long lastPicture;

        /// The picture into the back buffer: copied when it is the buffer's size, else stretched
        /// over it bilinearly; black when there is none.
        void Picture(ScreenTexture picture)
        {
            if (picture?.Texture == null)
            {
                var black = stackalloc float[] { 0f, 0f, 0f, 1f };
                Ctx->lpVtbl->ClearRenderTargetView(Ctx, Out.View, black);
                return;
            }
            if (picture.Width == Out.Width && picture.Height == Out.Height)
            {
                // "Must be the same type", "Must have identical dimensions", "Must have compatible
                // DXGI formats, which means the formats must be identical or at least from the
                // same type group" (ID3D11DeviceContext::CopyResource, Remarks): the swap chain's
                // R8G8B8A8_UNORM and the screen textures' R8G8B8A8_TYPELESS are of one group, and
                // the sizes are equal here. (The original draws its texture into buffer 0
                // with one draw instead, j0009060; a copy of the same size keeps every texel.)
                Ctx->lpVtbl->CopyResource(Ctx, (ID3D11Resource*)Out.Buffer, (ID3D11Resource*)picture.Texture);
                return;
            }
            if (PresentTrace.Enabled && (stretchedFrom.W != picture.Width || stretchedFrom.H != picture.Height || stretchedTo.W != Out.Width || stretchedTo.H != Out.Height))
            {
                stretchedFrom = (picture.Width, picture.Height);
                stretchedTo = (Out.Width, Out.Height);
                PresentTrace.Log($"d3d11: screen texture {picture.Index} ({picture.Width}x{picture.Height}) stretched over the {Out.Width}x{Out.Height} buffer");
            }
            quads.Clear();
            Quad(0, 0, Out.Width, Out.Height, 0, 0, 1, 1, Opaque, OpaqueAdd);
            DrawQuads(picture.Srv, presentLinear);
        }

        // ----- The presenter's program -----

        // The presenter's own program, which the plan's Rules allow for the player's own
        // presentation (the user's decision, d3d11-plan.md "Rules").
        // scripts/gen-d3d11-present.py compiles it with the Windows SDK's fxc 10.1
        // (10.0.26100.0: `fxc /O3 /Qstrip_reflect /Qstrip_debug /T vs_4_0 /E vs` and
        // `/T ps_4_0 /E ps`) and checks, or with --write sets, the blobs below; the scripts'
        // tests compare them (test_d3d11_present.py). Compiled from this HLSL:
        //
        //   struct VSIn { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 mul : COLOR0; float4 add : COLOR1; };
        //   struct PSIn { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 mul : COLOR0; float4 add : COLOR1; };
        //   PSIn vs(VSIn i) { PSIn o; o.pos = float4(i.pos, 0, 1); o.uv = i.uv; o.mul = i.mul; o.add = i.add; return o; }
        //   Texture2D t : register(t0);
        //   SamplerState s : register(s0);
        //   float4 ps(PSIn i) : SV_Target { return t.Sample(s, i.uv) * i.mul + i.add; }
        //
        // Model 4.0 is feature level 10_0's ("Direct3D feature levels", the shader model row);
        // this device makes 11_0 or 11_1.
        const string PresentVertexDxbc =
            "RFhCQ6JMrVvR8sC+P/YFwNCFb9gBAAAAKAIAAAMAAAAsAAAAtAAAAEABAABJU0dOgAAAAAQAAAAIAAAAaAAAAAAAAAAAAAAAAwAAAAAAAAADAwAAcQAAAAAAAAAAAAAAAwAAAAEAAAADAwAAegAAAAAAAAAAAAAAAwAAAAIAAAAPDwAAegAAAAEAAAAAAAAAAwAAAAMAAAAPDwAAUE9TSVRJT04AVEVYQ09PUkQAQ09MT1IAT1NHToQAAAAEAAAACAAAAGgAAAAAAAAAAQAAAAMAAAAAAAAADwAAAHQAAAAAAAAAAAAAAAMAAAABAAAAAwwAAH0AAAAAAAAAAAAAAAMAAAACAAAADwAAAH0AAAABAAAAAAAAAAMAAAADAAAADwAAAFNWX1Bvc2l0aW9uAFRFWENPT1JEAENPTE9SAKtTSERS4AAAAEAAAQA4AAAAXwAAAzIQEAAAAAAAXwAAAzIQEAABAAAAXwAAA/IQEAACAAAAXwAAA/IQEAADAAAAZwAABPIgEAAAAAAAAQAAAGUAAAMyIBAAAQAAAGUAAAPyIBAAAgAAAGUAAAPyIBAAAwAAADYAAAUyIBAAAAAAAEYQEAAAAAAANgAACMIgEAAAAAAAAkAAAAAAAAAAAAAAAAAAAAAAgD82AAAFMiAQAAEAAABGEBAAAQAAADYAAAXyIBAAAgAAAEYeEAACAAAANgAABfIgEAADAAAARh4QAAMAAAA+AAAB";
        const string PresentPixelDxbc =
            "RFhCQ86kmn5rrrWNCOjlGj0hn3cBAAAAnAEAAAMAAAAsAAAAuAAAAOwAAABJU0dOhAAAAAQAAAAIAAAAaAAAAAAAAAABAAAAAwAAAAAAAAAPAAAAdAAAAAAAAAAAAAAAAwAAAAEAAAADAwAAfQAAAAAAAAAAAAAAAwAAAAIAAAAPDwAAfQAAAAEAAAAAAAAAAwAAAAMAAAAPDwAAU1ZfUG9zaXRpb24AVEVYQ09PUkQAQ09MT1IAq09TR04sAAAAAQAAAAgAAAAgAAAAAAAAAAAAAAADAAAAAAAAAA8AAABTVl9UYXJnZXQAq6tTSERSqAAAAEAAAAAqAAAAWgAAAwBgEAAAAAAAWBgABABwEAAAAAAAVVUAAGIQAAMyEBAAAQAAAGIQAAPyEBAAAgAAAGIQAAPyEBAAAwAAAGUAAAPyIBAAAAAAAGgAAAIBAAAARQAACfIAEAAAAAAARhAQAAEAAABGfhAAAAAAAABgEAAAAAAAMgAACfIgEAAAAAAARg4QAAAAAABGHhAAAgAAAEYeEAADAAAAPgAAAQ==";

        /// Floats per vertex: position (clip space), texture coordinates, mul, add.
        const int PresentStride = 12;

        ID3D11VertexShader* presentVs;
        ID3D11PixelShader* presentPs;
        ID3D11InputLayout* presentLayout;
        ID3D11BlendState* presentBlend;
        ID3D11RasterizerState* presentRaster;
        ID3D11DepthStencilState* presentDepth;
        ID3D11SamplerState* presentLinear, presentLinearMips, presentPoint;
        ID3D11Buffer* presentVertices;
        int presentVertexBytes;
        readonly List<float> quads = new();
        /// The presenter drew since the device's bindings were last set (RestoreAfterPresenter).
        bool presenterDrew;

        static readonly (float, float, float, float) Opaque = (1, 1, 1, 0), OpaqueAdd = (0, 0, 0, 1);

        /// The presenter's program and states (device thread, at start-up: a failure there
        /// leaves the device unmade, as any other start-up step).
        void CreatePresenter()
        {
            var vs = Convert.FromBase64String(PresentVertexDxbc);
            var ps = Convert.FromBase64String(PresentPixelDxbc);
            fixed (byte* v = vs)
            fixed (byte* p = ps)
            {
                ID3D11VertexShader* vsh;
                D3d11Context.Check(Dev->lpVtbl->CreateVertexShader(Dev, v, (nuint)vs.Length, null, &vsh), "CreateVertexShader (presenter)");
                presentVs = vsh;
                ID3D11PixelShader* psh;
                D3d11Context.Check(Dev->lpVtbl->CreatePixelShader(Dev, p, (nuint)ps.Length, null, &psh), "CreatePixelShader (presenter)");
                presentPs = psh;
                fixed (byte* pos = "POSITION\0"u8, tex = "TEXCOORD\0"u8, col = "COLOR\0"u8)
                {
                    var elements = stackalloc D3D11_INPUT_ELEMENT_DESC[4];
                    elements[0] = new D3D11_INPUT_ELEMENT_DESC { SemanticName = (sbyte*)pos, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, AlignedByteOffset = 0 };
                    elements[1] = new D3D11_INPUT_ELEMENT_DESC { SemanticName = (sbyte*)tex, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, AlignedByteOffset = 8 };
                    elements[2] = new D3D11_INPUT_ELEMENT_DESC { SemanticName = (sbyte*)col, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT, AlignedByteOffset = 16 };
                    elements[3] = new D3D11_INPUT_ELEMENT_DESC { SemanticName = (sbyte*)col, SemanticIndex = 1, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT, AlignedByteOffset = 32 };
                    ID3D11InputLayout* layout;
                    D3d11Context.Check(Dev->lpVtbl->CreateInputLayout(Dev, elements, 4, v, (nuint)vs.Length, &layout), "CreateInputLayout (presenter)");
                    presentLayout = layout;
                }
            }
            // Premultiplied "over": the splash's comets (SplashRain.PremultipliedTexels), and with
            // alpha 1 everything else (the picture, the tile, the bar) replaces what is there.
            var blend = new D3D11_BLEND_DESC();
            blend.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
            {
                BlendEnable = 1, SrcBlend = D3D11_BLEND.D3D11_BLEND_ONE, DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA, BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE, DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA, BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
            };
            ID3D11BlendState* bs;
            D3d11Context.Check(Dev->lpVtbl->CreateBlendState(Dev, &blend, &bs), "CreateBlendState (presenter)");
            presentBlend = bs;
            var raster = new D3D11_RASTERIZER_DESC { FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID, CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE, DepthClipEnable = 1 };
            ID3D11RasterizerState* rs;
            D3d11Context.Check(Dev->lpVtbl->CreateRasterizerState(Dev, &raster, &rs), "CreateRasterizerState (presenter)");
            presentRaster = rs;
            var depth = new D3D11_DEPTH_STENCIL_DESC { DepthEnable = 0, DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO, DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS };
            ID3D11DepthStencilState* ds;
            D3d11Context.Check(Dev->lpVtbl->CreateDepthStencilState(Dev, &depth, &ds), "CreateDepthStencilState (presenter)");
            presentDepth = ds;
            presentLinear = PresentSampler(D3D11_FILTER.D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT, 0);
            presentLinearMips = PresentSampler(D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR, float.MaxValue);
            presentPoint = PresentSampler(D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_POINT, 0);
        }

        ID3D11SamplerState* PresentSampler(D3D11_FILTER filter, float maxLod)
        {
            var desc = new D3D11_SAMPLER_DESC
            {
                Filter = filter, AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP, AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP, MaxAnisotropy = 1, ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
                MinLOD = 0, MaxLOD = maxLod,
            };
            ID3D11SamplerState* s;
            D3d11Context.Check(Dev->lpVtbl->CreateSamplerState(Dev, &desc, &s), "CreateSamplerState (presenter)");
            return s;
        }

        void ReleasePresenter()
        {
            Com.Release(ref presentVertices);
            Com.Release(ref presentPoint);
            Com.Release(ref presentLinearMips);
            Com.Release(ref presentLinear);
            Com.Release(ref presentDepth);
            Com.Release(ref presentRaster);
            Com.Release(ref presentBlend);
            Com.Release(ref presentLayout);
            Com.Release(ref presentPs);
            Com.Release(ref presentVs);
            Com.Release(ref splashAtlasSrv);
            Com.Release(ref splashAtlas);
            ReleaseScreens();
        }

        /// A quad into `quads`: the rectangle (x0, y0)-(x1, y1) in the back buffer's pixels, rows
        /// top first, with texture coordinates (u0, v0) at its top left and (u1, v1) at its
        /// bottom right (Direct3D's: v down the texture's rows as stored), and the colour's
        /// `mul` and `add` (texel * mul + add).
        void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, (float, float, float, float) mul, (float, float, float, float) add)
        {
            float w = Out.Width, h = Out.Height;
            // Window pixels to clip space: x right, y up (Direct3D 11.3 Functional Specification
            // §15.6, the viewport transform, with the viewport the whole buffer).
            float cx0 = x0 / w * 2 - 1, cx1 = x1 / w * 2 - 1, cy0 = 1 - y0 / h * 2, cy1 = 1 - y1 / h * 2;
            Vertex(cx0, cy0, u0, v0, mul, add);
            Vertex(cx1, cy0, u1, v0, mul, add);
            Vertex(cx1, cy1, u1, v1, mul, add);
            Vertex(cx0, cy0, u0, v0, mul, add);
            Vertex(cx1, cy1, u1, v1, mul, add);
            Vertex(cx0, cy1, u0, v1, mul, add);
        }

        void Vertex(float x, float y, float u, float v, (float R, float G, float B, float A) mul, (float R, float G, float B, float A) add)
        {
            quads.Add(x); quads.Add(y); quads.Add(u); quads.Add(v);
            quads.Add(mul.R); quads.Add(mul.G); quads.Add(mul.B); quads.Add(mul.A);
            quads.Add(add.R); quads.Add(add.G); quads.Add(add.B); quads.Add(add.A);
        }

        /// A bar rectangle (OpenGL's window coordinates, from the bottom left), opaque, clipped to
        /// the buffer.
        void BarQuad(SplashRain.BarRect r)
        {
            int h = Out.Height;
            int x = Math.Max(0, r.X), top = Math.Max(0, h - (r.Y + r.Height));
            int x1 = Math.Min(Out.Width, r.X + r.Width), bottom = Math.Min(h, h - r.Y);
            if (x1 <= x || bottom <= top) return;
            Quad(x, top, x1, bottom, 0, 0, 0, 0, (0, 0, 0, 0), (r.R, r.G, r.B, 1));
        }

        /// Draws `quads` into the back buffer, sampling `view` with `sampler` (any texture for
        /// quads whose mul is 0).
        void DrawQuads(ID3D11ShaderResourceView* view, ID3D11SamplerState* sampler)
        {
            int floats = quads.Count;
            if (floats == 0) return;
            int bytes = floats * 4;
            if (presentVertices == null || presentVertexBytes < bytes)
            {
                Com.Release(ref presentVertices);
                int size = Math.Max(bytes, 64 << 10);
                var desc = new D3D11_BUFFER_DESC
                {
                    ByteWidth = (uint)size, Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER,
                    CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
                };
                ID3D11Buffer* b;
                D3d11Context.Check(Dev->lpVtbl->CreateBuffer(Dev, &desc, null, &b), "CreateBuffer (presenter vertices)");
                presentVertices = b;
                presentVertexBytes = size;
            }
            D3D11_MAPPED_SUBRESOURCE mapped;
            D3d11Context.Check(Ctx->lpVtbl->Map(Ctx, (ID3D11Resource*)presentVertices, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map (presenter vertices)");
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(quads).CopyTo(new Span<float>(mapped.pData, floats));
            Ctx->lpVtbl->Unmap(Ctx, (ID3D11Resource*)presentVertices, 0);
            presenterDrew = true;
            var vb = presentVertices;
            uint stride = PresentStride * 4, offset = 0;
            Ctx->lpVtbl->IASetInputLayout(Ctx, presentLayout);
            Ctx->lpVtbl->IASetPrimitiveTopology(Ctx, D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            Ctx->lpVtbl->IASetVertexBuffers(Ctx, 0, 1, &vb, &stride, &offset);
            Ctx->lpVtbl->VSSetShader(Ctx, presentVs, null, 0);
            Ctx->lpVtbl->GSSetShader(Ctx, null, null, 0);
            Ctx->lpVtbl->PSSetShader(Ctx, presentPs, null, 0);
            // The target first, then the view: the screen texture the stretch reads is the
            // device's target until OMSetRenderTargets replaces it, and a subresource bound for
            // writing and reading at once has one of its bind points "set to NULL, in order to
            // prevent the same subresource from being read and written simultaneously"
            // (ID3D11DeviceContext::OMSetRenderTargets, Remarks).
            var target = Out.View;
            Ctx->lpVtbl->OMSetRenderTargets(Ctx, 1, &target, null);
            Ctx->lpVtbl->PSSetShaderResources(Ctx, 0, 1, &view);
            Ctx->lpVtbl->PSSetSamplers(Ctx, 0, 1, &sampler);
            var blendFactor = stackalloc float[] { 0, 0, 0, 0 };
            Ctx->lpVtbl->OMSetBlendState(Ctx, presentBlend, blendFactor, 0xffffffff);
            Ctx->lpVtbl->OMSetDepthStencilState(Ctx, presentDepth, 0);
            Ctx->lpVtbl->RSSetState(Ctx, presentRaster);
            var vp = new D3D11_VIEWPORT { Width = Out.Width, Height = Out.Height, MaxDepth = 1f };
            Ctx->lpVtbl->RSSetViewports(Ctx, 1, &vp);
            Ctx->lpVtbl->Draw(Ctx, (uint)(floats / PresentStride), 0);
            // The view goes, so a later target of its texture finds nothing bound (no hazard),
            // and the back buffer, so Present finds it unbound (with it bound the debug layer
            // says, as INFO, that Present forces the slot to NULL: j0009072).
            ID3D11ShaderResourceView* none = null;
            Ctx->lpVtbl->PSSetShaderResources(Ctx, 0, 1, &none);
            Ctx->lpVtbl->OMSetRenderTargets(Ctx, 0, null, null);
            quads.Clear();
        }

        /// After the presenter drew (or always): the tracked bindings the presenter's draw changed
        /// forgotten, and the device's target set again, with its viewport and scissor, as before
        /// the picture.
        void RestoreAfterPresenter(bool always = false)
        {
            if (!presenterDrew && !always) return;
            if (presenterDrew) ForgetPresenterBindings();
            presenterDrew = false;
            // "After Present calls, the back buffer needs to explicitly be re-bound to the D3D11
            // immediate context" ("For best performance, use DXGI flip model"); the device binds
            // its screen texture instead, set again with the rest of the target.
            SetTarget(lastTarget);
        }

        /// What DrawQuads set, and only that, marked as not what the device last bound: the input
        /// layout and topology, the shaders (the geometry stage set to none), the pixel stage's
        /// view and sampler at slot 0 (the view unbound again), and the blend, depth-stencil and
        /// rasterizer states. The vertex buffers are set by every draw, and the target, viewport
        /// and scissor by SetTarget. Every other tracked view, sampler and constant buffer is
        /// still bound as tracked, so SetTarget's hazard pass still finds a target's texture
        /// bound for sampling in another slot or stage (ForgetBindings, after ClearState, would
        /// lose them).
        void ForgetPresenterBindings()
        {
            boundShaders = null;
            boundLayout = null;
            boundTopology = 0;
            boundRasterizer = null;
            boundBlend = null;
            boundDepth = null;
            boundStencilRef = 0;
            const int pixel = 1;
            boundViews[pixel][0] = 0;
            boundViewTextures[pixel][0] = null;
            boundSamplers[pixel][0] = 0;
        }

        // ----- The splash (SplashRain) -----

        SplashRain rain;
        ID3D11Texture2D* splashAtlas;
        ID3D11ShaderResourceView* splashAtlasSrv;
        readonly List<float> rainVertices = new();
        readonly List<SplashRain.BarRect> bar = new();

        /// The atlas once its art has loaded: made with its mips (GENERATE_MIPS, as VkSplash
        /// generates them), texels premultiplied, rows bottom first as SplashArt has them, so
        /// SplashRain's texture coordinates (OpenGL's, v up from the first row) read it as they
        /// are.
        bool SplashReady()
        {
            if (splashAtlasSrv != null) return true;
            var a = rain.Art;
            if (a == null) return false;
            try
            {
                // Its mips where the format has MIP_AUTOGEN ("Automatic generation of mipmaps is
                // supported", D3D11_FORMAT_SUPPORT), else level 0 alone (minified unfiltered).
                D3D11_FORMAT_SUPPORT support = 0;
                bool mips = Dev->lpVtbl->CheckFormatSupport(Dev, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, &support) >= 0
                            && (support & D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_MIP_AUTOGEN) != 0;
                int levels = mips ? TextureFormats.FullMipCount(a.Width, a.Height) : 1;
                var desc = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)a.Width, Height = (uint)a.Height, MipLevels = (uint)levels, ArraySize = 1, Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | (mips ? D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET : 0),
                    MiscFlags = mips ? D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS : 0,
                };
                ID3D11Texture2D* t;
                D3d11Context.Check(Dev->lpVtbl->CreateTexture2D(Dev, &desc, null, &t), "CreateTexture2D (splash atlas)");
                splashAtlas = t;
                ID3D11ShaderResourceView* srv;
                D3d11Context.Check(Dev->lpVtbl->CreateShaderResourceView(Dev, (ID3D11Resource*)t, null, &srv), "CreateShaderResourceView (splash atlas)");
                var texels = SplashRain.PremultipliedTexels(a);
                fixed (byte* p = texels) Ctx->lpVtbl->UpdateSubresource(Ctx, (ID3D11Resource*)t, 0, null, p, (uint)a.Width * 4, 0);
                // GENERATE_MIPS "must be created with the bind flags that specify that the
                // resource is a render target and a shader resource" (D3D11_RESOURCE_MISC_FLAG).
                if (mips) Ctx->lpVtbl->GenerateMips(Ctx, srv);
                splashAtlasSrv = srv;
            }
            catch (D3d11UnavailableException e)
            {
                Com.Release(ref splashAtlas);
                rain.Fail($"atlas upload failed: {e.Message}");
                return false;
            }
            rain.Start();
            return true;
        }

        /// The bar under the comets, then the comets over the picture (VkSplash.Draw's order).
        void DrawSplash()
        {
            if (!SplashReady() || !rain.Active) return;
            int w = Out.Width, h = Out.Height;
            float fade = rain.Vertices(w, h, rainVertices);
            SplashRain.Bar(w, h, fade, bar);
            quads.Clear();
            foreach (var r in bar) BarQuad(r);
            // Per vertex: x, y in pixels from the top left, u, v, opacity (SplashRain.Vertices).
            float fw = w, fh = h;
            for (int i = 0; i + 5 <= rainVertices.Count; i += 5)
            {
                float a = rainVertices[i + 4];
                Vertex(rainVertices[i] / fw * 2 - 1, 1 - rainVertices[i + 1] / fh * 2, rainVertices[i + 2], rainVertices[i + 3], (a, a, a, a), (0, 0, 0, 0));
            }
            DrawQuads(splashAtlasSrv, presentLinearMips);
        }

        // ----- The loading animation -----

        readonly D3d11LoadAnimation loading = new();
        readonly List<SplashRain.BarRect> loadingBar = new();

        /// The kanim's tile (opaque, unfiltered when not scaled) and the bar.
        void DrawLoading()
        {
            int w = Out.Width, h = Out.Height;
            var tile = loading.Redraw(w, h, out double t);
            quads.Clear();
            ID3D11ShaderResourceView* view = splashAtlasSrv != null ? splashAtlasSrv : shownScreen != null ? shownScreen.Srv : null;
            bool scaled = false;
            if (tile is { } k)
            {
                if (k.First && !k.Scaled && verifyReplay) VerifyReplay(k);
                float sw = k.Strip.Width, sh = k.Strip.Height;
                Quad(k.X0, k.Top, k.X1, k.Bottom, k.SrcX / sw, k.SrcTop / sh, (k.SrcX + k.Frames.TileWidth) / sw, k.SrcBottom / sh, Opaque, OpaqueAdd);
                view = k.Strip.Srv;
                scaled = k.Scaled;
                // The tile is drawn in its own draw (its texture); the bar's quads below in another.
                DrawQuads(view, scaled ? presentLinear : presentPoint);
            }
            loading.Bar(w, h, t, loadingBar);
            foreach (var r in loadingBar) BarQuad(r);
            if (quads.Count > 0 && view != null) DrawQuads(view, presentPoint);
            quads.Clear();
        }

        /// LoadingScreen: the frames rendered into their strip are handed to the presenter by a
        /// command after the commands that render them (main thread records; device thread runs).
        public override void HandOverLoadingFrames(LoadingScreen.KanimFrames frames)
        {
            if (frames.Strip.Gpu is not DeviceTexture strip)
            {
                frames.Done = true;
                return;
            }
            worker.Post(() =>
            {
                var t = strip.D3d;
                if (lost != null || t?.Resource == null || t.DepthOnly)
                {
                    frames.Done = true;
                    return;
                }
                PrepareForSampling(t, autoGenerateMips: false);
                loading.HandOver(frames, t);
            });
        }

        public override bool AnimatesLoading => true;

        // ONI_TWEAK_VERIFY=1: the first replayed tile against the frame shown (as VkLoadAnimation).
        static readonly bool verifyReplay = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Reads the shown frame's area and the tile it replays first (both copied into staging
        /// textures and mapped, which waits for the copies: D3D11_MAP_FLAG), compares them as
        /// VkLoadAnimation.VerifyCompare does, and says which tile of the strip the frame shown is
        /// closest to.
        void VerifyReplay(D3d11LoadAnimation.Tile k)
        {
            var shown = shownScreen;
            var f = k.Frames;
            if (shown?.Texture == null || shown.Width != Out.Width || shown.Height != Out.Height) return;
            int w = f.TileWidth, h = f.TileHeight;
            bool bottomFirst = D3d11LoadAnimation.BottomFirst(f);
            try
            {
                var a = ReadRegion((ID3D11Resource*)shown.Texture, ScreenFormat, k.X0, k.Top, w, h);
                // The tile the screen showed when the frames were rendered (f.Start), as
                // VkLoadAnimation.VerifyQueued compares: the first redraw's own tile is a frame
                // or more on by the time it is drawn (j0009133: tile 3 drawn, 2 shown).
                var start = D3d11LoadAnimation.TileOf(f, f.Start, Out.Width, Out.Height, k.Strip.Height, first: true);
                int srcRow = bottomFirst ? start.SrcBottom : start.SrcTop;
                var b = ReadRegion(k.Strip.Resource, k.Strip.Format, start.SrcX, srcRow, w, h);
                int differ = 0, largest = 0;
                // Where they differ, by row and column parity, and the first few (diagnostics).
                var parity = new int[4];
                System.Text.StringBuilder samples = null;
                for (int row = 0; row < h; row++)
                {
                    int tileRow = bottomFirst ? h - 1 - row : row;
                    for (int col = 0; col < w; col++)
                    {
                        int d = 0;
                        for (int c = 0; c < 3; c++) d = Math.Max(d, Math.Abs(a[(row * w + col) * 4 + c] - b[(tileRow * w + col) * 4 + c]));
                        if (d > 2)
                        {
                            parity[(row & 1) * 2 + (col & 1)]++;
                            if (differ < 6)
                            {
                                int o = (row * w + col) * 4, q = (tileRow * w + col) * 4;
                                (samples ??= new System.Text.StringBuilder()).Append($" ({col}, {row}) {a[o]},{a[o + 1]},{a[o + 2]} against {b[q]},{b[q + 1]},{b[q + 2]}");
                            }
                            differ++;
                        }
                        largest = Math.Max(largest, d);
                    }
                }
                if (differ > 0)
                    EngineLog.Engine($"loading animation: replay check: differing pixels by (row, column) parity even-even {parity[0]}, even-odd {parity[1]}, odd-even {parity[2]}, odd-odd {parity[3]}; "
                                     + $"shown against replayed, RGB, rows top first:{samples}; strip {k.Strip.Width}x{k.Strip.Height} {k.Strip.Format}, start tile {f.Start} ({start.SrcX}, rows {start.SrcTop}-{start.SrcBottom}), area ({k.X0}, {k.Top})-({k.X1}, {k.Bottom}), {(bottomFirst ? "bottom" : "top")} first");
                LoadingScreen.LastReplayDiffer = differ;
                var all = ReadRegion(k.Strip.Resource, k.Strip.Format, 0, 0, k.Strip.Width, k.Strip.Height);
                int best = -1, bestDiffer = int.MaxValue;
                for (int i = 0; i < f.Count; i++)
                {
                    int tx = f.TileX(i), ty = D3d11LoadAnimation.TileTop(f, f.TileY(i), k.Strip.Height), n = 0;
                    for (int row = 0; row < h && n < bestDiffer; row += 2)
                        for (int col = 0; col < w; col += 2)
                        {
                            int srow = bottomFirst ? ty + (h - 1 - row) : ty + row;
                            int d = 0;
                            for (int c = 0; c < 3; c++) d = Math.Max(d, Math.Abs(a[(row * w + col) * 4 + c] - all[((long)srow * k.Strip.Width + tx + col) * 4 + c]));
                            if (d > 2) n++;
                        }
                    if (n < bestDiffer)
                    {
                        bestDiffer = n;
                        best = i;
                    }
                }
                EngineLog.Engine($"loading animation: replay check: the frame shown is closest to tile {best} ({bestDiffer} of {(w / 2) * (h / 2)} sampled pixels differ); the replay starts at tile {f.Start}");
                EngineLog.Engine($"loading animation: replay check: the first replayed frame against the frame shown, {differ} of {w * h} pixels differ by more than 2 (largest difference {largest})");
            }
            catch (Exception e) when (e is D3d11UnavailableException or NotSupportedException)
            {
                EngineLog.Engine($"loading animation: replay check failed: {e.Message}");
            }
        }

        /// The texels of a rectangle of level 0 (rows as stored), as 8-bit RGBA.
        byte[] ReadRegion(ID3D11Resource* source, DXGI_FORMAT format, int x, int y, int w, int h)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            };
            ID3D11Texture2D* staging;
            D3d11Context.Check(Dev->lpVtbl->CreateTexture2D(Dev, &desc, null, &staging), "CreateTexture2D (replay check)");
            try
            {
                var box = new D3D11_BOX { left = (uint)x, top = (uint)y, right = (uint)(x + w), bottom = (uint)(y + h), front = 0, back = 1 };
                Ctx->lpVtbl->CopySubresourceRegion(Ctx, (ID3D11Resource*)staging, 0, 0, 0, 0, source, 0, &box);
                D3D11_MAPPED_SUBRESOURCE mapped;
                D3d11Context.Check(Ctx->lpVtbl->Map(Ctx, (ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map (replay check)");
                var result = new byte[w * h * 4];
                try
                {
                    int texel = D3d11Formats.TexelBytes(format);
                    for (int row = 0; row < h; row++)
                        for (int col = 0; col < w; col++)
                        {
                            var c = D3d11Formats.ReadTexel(format, (byte*)mapped.pData + (long)row * mapped.RowPitch + (long)col * texel);
                            int o = (row * w + col) * 4;
                            result[o] = (byte)Math.Clamp((int)MathF.Round(c.r * 255f), 0, 255);
                            result[o + 1] = (byte)Math.Clamp((int)MathF.Round(c.g * 255f), 0, 255);
                            result[o + 2] = (byte)Math.Clamp((int)MathF.Round(c.b * 255f), 0, 255);
                            result[o + 3] = (byte)Math.Clamp((int)MathF.Round(c.a * 255f), 0, 255);
                        }
                }
                finally
                {
                    Ctx->lpVtbl->Unmap(Ctx, (ID3D11Resource*)staging, 0);
                }
                return result;
            }
            finally
            {
                Com.Release(ref staging);
            }
        }

        // ----- Between frames (GlWorker's idle work, device thread) -----

        enum IdleKind { None, Splash, Loading, Refresh, Repeat }

        IdleKind idleKind;
        readonly StallRepeats stall = new();
        bool timerRaised;
        /// Set by the main thread at shutdown: no idle work starts any more.
        volatile bool presentStopped;
        /// The window's contents were damaged (GlfwPlatform.ContentsDamaged, main thread): the
        /// last frame is shown again (as VkPresenter.Refresh), between frames.
        volatile bool refreshWanted;

        void Refresh()
        {
            refreshWanted = true;
            worker.Wake();
        }

        /// Milliseconds until the next picture between frames is due (0: due now; -1: none is
        /// coming), and which (idleKind): the splash about 60 times a second while it runs
        /// (PresenterTimings.SplashWait's 16 ms after the last picture); else the loading
        /// animation (LoadingWait); else a repeat once no frame has come for a while
        /// (StallRepeats, MayRepeat). As VkPresenter.Run's wait, in the same order.
        int IdleDue()
        {
            idleKind = IdleKind.None;
            if (lost != null || presentStopped) return -1;
            // A refresh first, as VkPresenter.Run's wait ends on it before the others (and the
            // loading watchdog's wait would otherwise hold it back).
            if (refreshWanted && shownScreen != null)
            {
                idleKind = IdleKind.Refresh;
                return 0;
            }
            if (rain != null && rain.Active)
            {
                idleKind = IdleKind.Splash;
                long since = (GlTimings.Now - lastPicture) * 1000 / Stopwatch.Frequency;
                return (int)Math.Clamp(16 - since, 0, 16);
            }
            int loadingWait = LoadingWait();
            if (loadingWait == 0)
            {
                idleKind = IdleKind.Loading;
                return loading.DueMs;
            }
            if (loadingWait > 0) return loadingWait;
            if (MayRepeat)
            {
                idleKind = IdleKind.Repeat;
                int ms = stall.WaitMilliseconds(GlTimings.Now);
                return ms == int.MaxValue ? -1 : ms;
            }
            return -1;
        }

        /// 0 when the loading screen's animation is due over the frame shown now (its run begun),
        /// else milliseconds until it may be, -1 never (VkPresenter.LoadingWait).
        int LoadingWait()
        {
            if (!LoadingScreen.Enabled || shownScreen == null) return -1;
            int armed = LoadingScreen.ArmedFrame, overlay = LoadingScreen.OverlayFrame;
            double idle = loading.IdleMs;
            if (GlLoadAnimation.Wanted(shownScreen.DrawnFrame, armed, overlay, idle))
            {
                loading.Begin(armed, byWatchdog: !(armed >= 0 && shownScreen.DrawnFrame < armed));
                return 0;
            }
            return GlLoadAnimation.WatchdogDue(shownScreen.DrawnFrame, overlay, idle);
        }

        /// Repeats are on (StallRepeats: DxgiSwapChain.Repeats, Moono Tweaks' "Steady display
        /// during saves and loads"), with a frame shown, the splash gone, and the swap chain not
        /// resized since that frame.
        bool MayRepeat => DxgiSwapChain.Repeats && shownScreen != null && !resizedSinceShown && exclusive == null;

        /// One picture between frames (GlWorker.IdleWork).
        bool IdleWork()
        {
            if (lost != null || presentStopped) return true;
            var kind = idleKind;
            try
            {
                switch (kind)
                {
                    case IdleKind.Splash:
                    case IdleKind.Loading:
                        if (kind == IdleKind.Loading && OperatingSystem.IsWindows() && !timerRaised) timerRaised = Dxgi.timeBeginPeriod(1) == 0;
                        if (!Show(shownScreen, swapInterval < 0 ? 1 : swapInterval, ShowKind.Redraw)) return true;
                        RestoreAfterPresenter();
                        Out.WaitForFrame();
                        break;
                    case IdleKind.Refresh:
                        refreshWanted = false;
                        // A redraw, as VkPresenter's refresh: the splash or the loading animation
                        // over the frame where they run.
                        if (!Show(shownScreen, swapInterval < 0 ? 1 : swapInterval, ShowKind.Redraw)) return true;
                        RestoreAfterPresenter();
                        Out.WaitForFrame();
                        break;
                    case IdleKind.Repeat:
                        Repeat();
                        break;
                }
            }
            catch (D3d11UnavailableException e)
            {
                lost = e.Message;
                EngineLog.Engine($"d3d11: {lost}");
            }
            return true;
        }

        /// Presents the last picture again once the display has taken the last picture (else
        /// tries again 2 ms on, StallRepeats.Skipped). The waits between repeats need a 1 ms timer
        /// resolution, raised for the run of repeats only (VkPresenter.Repeat).
        void Repeat()
        {
            if (OperatingSystem.IsWindows() && !timerRaised) timerRaised = Dxgi.timeBeginPeriod(1) == 0;
            if (!Out.LastPictureRetired())
            {
                stall.Skipped(GlTimings.Now);
                return;
            }
            if (!Show(shownScreen, 0, ShowKind.Repeat)) return;
            RestoreAfterPresenter();
            stall.Repeated(GlTimings.Now);
            PresenterTimings.Repeated();
        }

        /// A game frame was presented: repeats start over; the timer resolution raised for the
        /// repeats or the loading animation goes back (VkPresenter.FramePresented).
        void FramePresented()
        {
            long now = GlTimings.Now;
            if (stall.Run > 0 && PresentTrace.Enabled)
                PresentTrace.Log($"d3d11: frame {shownScreen?.DrawnFrame ?? -1} presented again {stall.Run} times over {(now - stall.RunStarted) * 1000.0 / Stopwatch.Frequency:F0} ms while no frame came (every {stall.Cadence * 1000.0 / Stopwatch.Frequency:F1} ms)");
            stall.Frame(now);
            if (timerRaised && OperatingSystem.IsWindows() && !loading.Running)
            {
                timerRaised = false;
                _ = Dxgi.timeEndPeriod(1);
            }
        }

        /// The idle work's hooks on the device thread's worker, the splash's rain, and the loading
        /// screen's wake-up (main thread, at start-up).
        void StartPresenting()
        {
            if (GlDevice.Splash != null) rain = new SplashRain(GlDevice.Splash);
            worker.HasIdleWork = () => IdleDue() == 0;
            worker.IdleWaitMs = IdleDue;
            worker.IdleWork = IdleWork;
            LoadingScreen.Changed = worker.Wake;
            platform.ContentsDamaged += Refresh;
        }

        /// Ends the idle work (main thread, before the worker stops) and gives back what the
        /// loading animation holds (device thread, as the worker stops).
        void StopPresenting()
        {
            presentStopped = true;
            platform.ContentsDamaged -= Refresh;
            if (LoadingScreen.Changed == worker.Wake) LoadingScreen.Changed = null;
            worker.Wake();
        }

        void EndPresenting()
        {
            loading.End();
            if (timerRaised && OperatingSystem.IsWindows()) _ = Dxgi.timeEndPeriod(1);
            timerRaised = false;
        }
    }
}
