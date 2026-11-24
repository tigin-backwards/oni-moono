using System;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;

namespace OniMoono.Desktop.D3d11
{
    /// The D3D11 device's own flip-model swap chain on the player window, made on the device that
    /// renders (CreateSwapChainForHwnd's pDevice "is a pointer to the Direct3D device for the swap
    /// chain"), so no shared texture, keyed mutex or interop is needed. Its settings are the
    /// original's, measured (docs/research/d3d11-captures.md §1): 2 buffers, FLIP_SEQUENTIAL,
    /// R8G8B8A8_UNORM, BufferUsage SHADER_INPUT | RENDER_TARGET_OUTPUT (0x30; buffer 0 binds as SRV
    /// and RTV), AlphaMode UNSPECIFIED, scaling STRETCH, flags ALLOW_MODE_SWITCH |
    /// FRAME_LATENCY_WAITABLE_OBJECT | ALLOW_TEARING (the last where the factory reports
    /// PRESENT_ALLOW_TEARING), maximum frame latency 2; Present(1, 0) with vsync,
    /// Present(0, ALLOW_TEARING) without. Windowed only: full screen is the window's (D3d11Device's
    /// presentation, ARCHITECTURE §3.5).
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

        /// The back buffer (buffer 0; valid until the next Resize).
        public ID3D11Texture2D* Buffer => buffer;

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
                BufferUsage = Dxgi.UsageShaderInput | Dxgi.UsageRenderTargetOutput, BufferCount = BufferCount, Scaling = Dxgi.ScalingStretch,
                SwapEffect = Dxgi.SwapEffectFlipSequential, AlphaMode = Dxgi.AlphaModeUnspecified, Flags = flags,
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
        /// Nothing waits here: the device waits before it renders a frame (WaitForFrame). Returns
        /// Present's result. A game picture or a redraw with vsync tells FramePacing the display's
        /// refresh period (as DxgiSwapChain.PresentCopied); one without vsync, that it is unpaced.
        public int Present(int interval)
        {
            interval = Math.Clamp(interval, 0, 4);
            uint presentFlags = interval == 0 && tearing ? Dxgi.PresentAllowTearing : 0;
            long t = GlTimings.Now;
            int hr = ((IDXGISwapChain*)chain)->lpVtbl->Present((IDXGISwapChain*)chain, (uint)interval, presentFlags);
            Trace(interval, presentTicks: GlTimings.Now - t);
            presents++;
            if (hr >= 0)
            {
                if (interval == 0) FramePacing.UnpacedPresent();
                else
                {
                    MeasureRefresh();
                    FramePacing.PacedPresent(System.Threading.Interlocked.Read(ref refreshPeriod));
                }
            }
            return hr;
        }

        /// Presents the back buffer again while no frame comes (StallRepeats), after
        /// LastPictureRetired: at sync interval 0 without ALLOW_TEARING, so in the flip model it
        /// is discarded when a newer picture is queued before it is shown ("discard this frame if
        /// a newer frame is queued", IDXGISwapChain::Present's SyncInterval). Not counted as a
        /// present, nor by frame pacing (as DxgiSwapChain.PresentRepeatCopied).
        public int PresentRepeat()
        {
            int hr = ((IDXGISwapChain*)chain)->lpVtbl->Present((IDXGISwapChain*)chain, 0, 0);
            if (hr >= 0)
            {
                Repeats++;
                repeatsUnwaited++;
            }
            return hr;
        }

        /// Repeats presented (StallRepeats).
        public long Repeats { get; private set; }

        /// Whether the frame-latency waitable is signalled, without waiting: a repeat goes only
        /// then. Takes the signal, which stands for the repeat's own wait, so every Present, the
        /// repeat's included, is paired with one wait (WaitForFrame's rule). That pairing assumes
        /// each Present, a repeat discarded for a newer picture included, signals the waitable
        /// once as it leaves the queue: no Microsoft page says so (UNVERIFIED, d3d11-plan.md "M4
        /// review fixes", item 2). DxgiSwapChain.LastPictureRetired does not assume it: it takes
        /// the signal the same way, but the next game picture drains and re-counts the waitable
        /// (its resync) and bounds the wait after a repeat to a refresh. Here a miscount would
        /// show as WaitForFrame timeouts (one more queued than the latency) or as frames
        /// running a picture ahead (one fewer), both in ONI_PRESENT_TRACE's lines.
        public bool LastPictureRetired() => Dxgi.WaitForSingleObjectEx(waitable, 0, 0) == Dxgi.WaitObject0;

        /// The refresh period of the display the swap chain is on (Stopwatch ticks; 0: not
        /// known), for FramePacing: its current mode's refresh rate, looked up again every
        /// RefreshLookupPresents pictures on the thread pool (DxgiSwapChain.MeasureRefresh, whose
        /// reasons apply: frame statistics are updated only now and then for a windowed swap
        /// chain).
        long refreshPeriod;
        int refreshLookupIn;
        const int RefreshLookupPresents = 300;

        void MeasureRefresh()
        {
            if (--refreshLookupIn > 0) return;
            refreshLookupIn = RefreshLookupPresents;
            string name = DxgiSwapChain.OutputNameOf(chain);
            if (name == null)
            {
                System.Threading.Interlocked.Exchange(ref refreshPeriod, 0);
                return;
            }
            System.Threading.Tasks.Task.Run(() =>
            {
                long period = DxgiSwapChain.LookUpRefreshPeriod(name);
                long before = System.Threading.Interlocked.Exchange(ref refreshPeriod, period);
                if (period != before)
                    EngineLog.Engine(period > 0 ? $"d3d11: {name} refreshes every {period * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F4} ms; vsynced frames last whole refreshes"
                        : $"d3d11: {name}'s refresh rate not known; frames timed by the wall clock");
            });
        }

        /// Waits until the swap chain takes a new frame, before the device renders one, the first
        /// included, with vsync or without: "Your rendering loop should wait for the swap chain
        /// to signal via the waitable object before it begins rendering every frame. This
        /// includes the first frame rendered with the swap chain" (Microsoft, "Reduce latency
        /// with DXGI 1.3 swap chains", step 4, whose sample waits at most 1 second). So at most
        /// MaximumFrameLatency frames are queued. A wait that times out is logged (the first
        /// three) and the frame goes on; the signal it missed is taken when it comes (`owed`),
        /// so the frames in flight stay at the latency set. Device thread (the constructor's
        /// thread before the first frame).
        public void WaitForFrame()
        {
            long t = GlTimings.Now;
            uint waited = Dxgi.WaitForSingleObjectEx(waitable, WaitMilliseconds, 0);
            if (waited != Dxgi.WaitObject0)
            {
                owed++;
                if (timeouts++ < 3)
                    EngineLog.Engine($"d3d11: present {presents}: the frame-latency wait ended with 0x{waited:X} ({(waited == Dxgi.WaitTimeout ? $"timed out after {WaitMilliseconds} ms" : "not signalled")}); going on");
            }
            else
                while (owed > 0 && Dxgi.WaitForSingleObjectEx(waitable, 0, 0) == Dxgi.WaitObject0)
                    owed--;
            long ticks = GlTimings.Now - t;
            if (repeatsUnwaited > 0 && PresentTrace.Enabled)
                PresentTrace.Log($"d3d11: the first frame-latency wait after {repeatsUnwaited} repeats: {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F2} ms, "
                                 + $"{(waited == Dxgi.WaitObject0 ? "signalled" : $"0x{waited:X}")}; timeouts {timeouts}, owed {owed}");
            repeatsUnwaited = 0;
            waitTicks += ticks;
            waitMax = Math.Max(waitMax, ticks);
        }

        long presents;
        int timeouts, owed, repeatsUnwaited;

        // ONI_PRESENT_TRACE: every 120 presents (and at each change of interval) the frame-latency
        // waits and the Present calls, mean and largest, in ms.
        long waitTicks, waitMax, presentTicks, presentMax;
        int traced, tracedInterval;

        void Trace(int interval, long presentTicks)
        {
            if (!PresentTrace.Enabled) return;
            if (traced >= 120 || traced > 0 && interval != tracedInterval)
            {
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                PresentTrace.Log($"d3d11: the {traced} presents to {presents} at interval {tracedInterval}, {Width}x{Height}: frame-latency wait mean {waitTicks * ms / traced:F2} ms, "
                                 + $"largest {waitMax * ms:F2}; Present mean {this.presentTicks * ms / traced:F2} ms, largest {presentMax * ms:F2}; timeouts {timeouts}, owed {owed}, repeats {Repeats}");
                traced = 0;
                waitTicks = waitMax = this.presentTicks = presentMax = 0;
            }
            tracedInterval = interval;
            this.presentTicks += presentTicks;
            presentMax = Math.Max(presentMax, presentTicks);
            traced++;
        }

        public string Describe()
            => $"swap chain {Width}x{Height} R8G8B8A8_UNORM, FLIP_SEQUENTIAL, {BufferCount} buffers, maximum frame latency {MaximumFrameLatency} (waitable), "
               + $"tearing without vsync {(tearing ? "allowed" : "not supported")}";

        public void Dispose()
        {
            FramePacing.UnpacedPresent();
            Com.Release(ref view);
            Com.Release(ref buffer);
            if (waitable != IntPtr.Zero) Dxgi.CloseHandle(waitable);
            waitable = IntPtr.Zero;
            Com.Release(ref chain2);
            Com.Release(ref chain);
        }
    }
}
