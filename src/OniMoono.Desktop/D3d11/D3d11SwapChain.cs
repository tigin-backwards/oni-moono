using System;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;

namespace OniMoono.Desktop.D3d11
{
    /// The D3D11 device's own flip-model swap chain on the player window, made on the device that
    /// renders (CreateSwapChainForHwnd's pDevice "is a pointer to the Direct3D device for the swap
    /// chain"), so no shared texture, keyed mutex or interop is needed. Its settings are the
    /// original's, measured (docs/research/d3d11-captures.md §1): 2 buffers, FLIP_SEQUENTIAL,
    /// R8G8B8A8_UNORM, flags ALLOW_MODE_SWITCH | FRAME_LATENCY_WAITABLE_OBJECT | ALLOW_TEARING (the
    /// last where the factory reports PRESENT_ALLOW_TEARING), maximum frame latency 2; Present(1, 0)
    /// with vsync, Present(0, ALLOW_TEARING) without. Windowed only (full screen is M4).
    internal sealed unsafe class D3d11SwapChain : IDisposable
    {
        const uint BufferCount = 2, MaximumFrameLatency = 2, WaitMilliseconds = 1000;

        readonly ID3D11Device* device;
        readonly ID3D11DeviceContext* context;
        IDXGISwapChain1* chain;
        IDXGISwapChain2* chain2;
        ID3D11Texture2D* buffer;
        ID3D11RenderTargetView* view;
        IntPtr waitable;
        readonly uint flags;
        readonly bool tearing;

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// The back buffer's render target view for this frame (valid until the next Resize).
        public ID3D11RenderTargetView* View => view;

        D3d11SwapChain(ID3D11Device* device, ID3D11DeviceContext* context, bool tearing)
        {
            this.device = device;
            this.context = context;
            this.tearing = tearing;
            flags = Dxgi.SwapChainFlagAllowModeSwitch | Dxgi.SwapChainFlagFrameLatencyWaitable | (tearing ? Dxgi.SwapChainFlagAllowTearing : 0);
        }

        /// The swap chain for `hwnd`, `w` x `h`; throws D3d11UnavailableException with the
        /// failing step (what was made is released).
        public static D3d11SwapChain Create(D3d11Context d3d, IntPtr hwnd, int w, int h)
        {
            var s = new D3d11SwapChain(d3d.Device, d3d.Context, d3d.Tearing);
            try
            {
                s.Start(d3d.Factory, hwnd, Math.Max(1, w), Math.Max(1, h));
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }

        void Start(IDXGIFactory2* factory, IntPtr hwnd, int w, int h)
        {
            var desc = new Dxgi.SwapChainDesc1
            {
                Width = (uint)w, Height = (uint)h, Format = Dxgi.FormatR8G8B8A8Unorm, SampleCount = 1,
                BufferUsage = Dxgi.UsageRenderTargetOutput, BufferCount = BufferCount, Scaling = Dxgi.ScalingStretch,
                SwapEffect = Dxgi.SwapEffectFlipSequential, AlphaMode = Dxgi.AlphaModeIgnore, Flags = flags,
            };
            IDXGISwapChain1* c;
            D3d11Context.Check(factory->lpVtbl->CreateSwapChainForHwnd(factory, (IUnknown*)device, hwnd, &desc, null, null, &c), "CreateSwapChainForHwnd");
            chain = c;
            Width = w;
            Height = h;
            // As the DXGI presenter: DXGI leaves the window alone (no Alt+Enter, no mode changes of
            // its own); the player handles full screen.
            D3d11Context.Check(((IDXGIFactory*)factory)->lpVtbl->MakeWindowAssociation((IDXGIFactory*)factory, hwnd,
                Dxgi.WindowAssociationNoWindowChanges | Dxgi.WindowAssociationNoAltEnter), "MakeWindowAssociation");
            IDXGISwapChain2* c2;
            D3d11Context.Check(Com.QueryInterface(chain, IDXGISwapChain2.IID, (void**)&c2), "QueryInterface IDXGISwapChain2");
            chain2 = c2;
            D3d11Context.Check(chain2->lpVtbl->SetMaximumFrameLatency(chain2, MaximumFrameLatency), "SetMaximumFrameLatency");
            // "When you're done with the handle, use CloseHandle" (GetFrameLatencyWaitableObject).
            waitable = chain2->lpVtbl->GetFrameLatencyWaitableObject(chain2);
            if (waitable == IntPtr.Zero) throw new D3d11UnavailableException("no frame latency waitable object");
            TakeBuffer();
        }

        void TakeBuffer()
        {
            ID3D11Texture2D* b;
            var iid = ID3D11Texture2D.IID;
            D3d11Context.Check(((IDXGISwapChain*)chain)->lpVtbl->GetBuffer((IDXGISwapChain*)chain, 0, &iid, (void**)&b), "GetBuffer");
            buffer = b;
            ID3D11RenderTargetView* v;
            D3d11Context.Check(device->lpVtbl->CreateRenderTargetView(device, (ID3D11Resource*)buffer, null, &v), "CreateRenderTargetView (back buffer)");
            view = v;
        }

        /// The buffers made `w` x `h` when they are not. ResizeBuffers needs every reference to
        /// the buffers released first, the context's included, and the flags the swap chain was
        /// made with (IDXGISwapChain::ResizeBuffers remarks; ALLOW_TEARING "must be specified" on
        /// it when made so, DXGI_SWAP_CHAIN_FLAG).
        public void Resize(int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (w == Width && h == Height) return;
            Com.Release(ref view);
            Com.Release(ref buffer);
            context->lpVtbl->ClearState(context);
            context->lpVtbl->Flush(context);
            D3d11Context.Check(((IDXGISwapChain*)chain)->lpVtbl->ResizeBuffers((IDXGISwapChain*)chain, 0, (uint)w, (uint)h, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, flags),
                $"ResizeBuffers to {w}x{h}");
            Width = w;
            Height = h;
            TakeBuffer();
        }

        /// Presents the back buffer: `interval` vertical blanks after the last (with vsync), or at
        /// once, tearing where allowed. A flip-model back buffer must be bound again after Present
        /// ("For best performance, use DXGI flip model"): the device sets its targets each frame.
        /// Then waits for the frame-latency object, as an app made with
        /// FRAME_LATENCY_WAITABLE_OBJECT does before rendering the next frame ("Reduce latency
        /// with DXGI 1.3 swap chains"). Returns Present's result.
        public int Present(int interval)
        {
            interval = Math.Clamp(interval, 0, 4);
            uint presentFlags = interval == 0 && tearing ? Dxgi.PresentAllowTearing : 0;
            int hr = ((IDXGISwapChain*)chain)->lpVtbl->Present((IDXGISwapChain*)chain, (uint)interval, presentFlags);
            if (hr >= 0) Dxgi.WaitForSingleObjectEx(waitable, WaitMilliseconds, 1);
            return hr;
        }

        public string Describe()
            => $"swap chain {Width}x{Height} R8G8B8A8_UNORM, FLIP_SEQUENTIAL, {BufferCount} buffers, maximum frame latency {MaximumFrameLatency} (waitable), "
               + $"tearing without vsync {(tearing ? "allowed" : "not supported")}";

        public void Dispose()
        {
            Com.Release(ref view);
            Com.Release(ref buffer);
            if (waitable != IntPtr.Zero) Dxgi.CloseHandle(waitable);
            waitable = IntPtr.Zero;
            Com.Release(ref chain2);
            Com.Release(ref chain);
        }
    }
}
