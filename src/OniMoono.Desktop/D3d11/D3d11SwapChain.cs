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
    /// Present(0, ALLOW_TEARING) without; never in DXGI's full-screen state. Exclusive full screen
    /// is a second chain of this class's exclusive kind (CreateExclusive, D3d11Exclusive).
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
        /// The exclusive full-screen kind (CreateExclusive): the legacy blt model, as the
        /// original's exclusive chain; no frame-latency waitable.
        public bool IsExclusive { get; }

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// The back buffer's render target view for this frame (valid until the next Resize).
        public ID3D11RenderTargetView* View => view;

        /// The back buffer (buffer 0; valid until the next Resize).
        public ID3D11Texture2D* Buffer => buffer;

        D3d11SwapChain(ID3D11Device* device, ID3D11DeviceContext* context, bool tearing, bool exclusive = false)
        {
            this.device = device;
            this.context = context;
            IsExclusive = exclusive;
            this.tearing = tearing && !exclusive;
            flags = exclusive ? Dxgi.SwapChainFlagAllowModeSwitch
                : Dxgi.SwapChainFlagAllowModeSwitch | Dxgi.SwapChainFlagFrameLatencyWaitable | (tearing ? Dxgi.SwapChainFlagAllowTearing : 0);
        }

        /// The exclusive full-screen chain for `hwnd`, as the original's (j0009185, captures
        /// UNVERIFIED 10): DXGI_SWAP_EFFECT_DISCARD, 1 buffer, R8G8B8A8_UNORM, usage SHADER_INPUT |
        /// RENDER_TARGET_OUTPUT, flags ALLOW_MODE_SWITCH; then, as it does, ResizeTarget to the
        /// mode and SetFullscreenState(TRUE) (ResizeTarget "should be called before calling
        /// SetFullscreenState", "DXGI best practices", Full-Screen Issues), and ResizeBuffers to
        /// the mode's size once it is full screen ("call IDXGISwapChain::ResizeBuffers to resize
        /// the back buffer to the size that was passed as parameters in WM_SIZE", the same).
        /// Returns SetFullscreenState's result: a failure (DXGI_ERROR_NOT_CURRENTLY_AVAILABLE
        /// when the window lacks the focus, as the original's did) leaves nothing made (out
        /// null). Throws D3d11UnavailableException when the chain cannot be made or sized.
        public static int CreateExclusive(D3d11Context d3d, IntPtr hwnd, int w, int h, uint refreshNumerator, uint refreshDenominator, out D3d11SwapChain made)
        {
            made = null;
            var s = new D3d11SwapChain(d3d.Device, d3d.Context, false, exclusive: true);
            try
            {
                s.Start(d3d.Factory, hwnd, Math.Max(1, w), Math.Max(1, h));
                int hr = s.SetFullscreen(true, w, h, refreshNumerator, refreshDenominator);
                // Entered only on S_OK with DXGI holding the chain full screen (GetFullscreenState):
                // a success code such as DXGI_STATUS_MODE_CHANGE_IN_PROGRESS is not entered. Refused,
                // the chain is taken out of the full-screen state first, as the original did after
                // its refusal (SetFullscreenState(0) next, j0009178), then released.
                if (hr != 0 || !s.InFullscreenState())
                {
                    var sc = (IDXGISwapChain*)s.chain;
                    sc->lpVtbl->SetFullscreenState(sc, 0, null);
                    s.fullscreen = false;
                    s.Dispose();
                    return hr < 0 ? hr : NotEntered;
                }
                made = s;
                return hr;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }

        /// ResizeTarget (on) and SetFullscreenState; on, the buffers then made the mode's size.
        /// Returns SetFullscreenState's result.
        public int SetFullscreen(bool on, int w, int h, uint refreshNumerator, uint refreshDenominator)
        {
            var sc = (IDXGISwapChain*)chain;
            if (on)
            {
                // As the original's: the format, scanline order and scaling left unspecified (its
                // ResizeTarget logged "format=0 scanline=0 scaling=0", j0009128, j0009185: 0 is
                // DXGI_FORMAT_UNKNOWN and the two orders' UNSPECIFIED). Its refresh was the
                // display's (59951/1000); here it is what the game asked (0/0 when none).
                var mode = new Dxgi.ModeDesc { Width = (uint)w, Height = (uint)h, RefreshRate = new Dxgi.Rational { Numerator = refreshNumerator, Denominator = refreshDenominator } };
                int target = sc->lpVtbl->ResizeTarget(sc, &mode);
                if (target < 0) return target;
            }
            int hr;
            if (on && D3d11Fault.Has("exclusive-refused"))
            {
                // What the original's SetFullscreenState(TRUE) returned while its window lacked
                // the focus (j0009128, j0009178), without the call.
                EngineLog.Engine($"d3d11: {D3d11Fault.Variable}=exclusive-refused: SetFullscreenState(TRUE) not called; DXGI_ERROR_NOT_CURRENTLY_AVAILABLE returned (a test)");
                hr = Dxgi.ErrorNotCurrentlyAvailable;
            }
            else hr = sc->lpVtbl->SetFullscreenState(sc, on ? 1 : 0, null);
            fullscreen = on && hr == 0;
            if (fullscreen)
            {
                Com.Release(ref view);
                Com.Release(ref buffer);
                context->lpVtbl->ClearState(context);
                context->lpVtbl->Flush(context);
                D3d11Context.Check(sc->lpVtbl->ResizeBuffers(sc, 0, (uint)w, (uint)h, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, flags), $"ResizeBuffers to {w}x{h} (exclusive)");
                Width = w;
                Height = h;
                TakeBuffer();
            }
            if (on && enterHold > 0)
            {
                // ONI_D3D11_FAIL=enter-hold:MS: Enter takes MS longer here, the main thread
                // answering sent messages meanwhile (a focus change during Enter, d3d11-plan.md "M5").
                EngineLog.Engine($"d3d11: {D3d11Fault.Variable}=enter-hold:{enterHold}: SetFullscreenState {Dxgi.Hr(hr)}; held {enterHold} ms inside Enter (a test)");
                System.Threading.Thread.Sleep(enterHold);
            }
            return hr;
        }

        static readonly int enterHold = D3d11Fault.Number("enter-hold");
        bool fullscreen;

        /// CreateExclusive's result for a success code that did not leave the chain full screen
        /// (E_FAIL's value; the code itself is logged by the caller).
        public const int NotEntered = unchecked((int)0x80004005);

        /// Whether DXGI holds the chain in its full-screen state now (GetFullscreenState).
        public bool InFullscreenState()
        {
            int state = 0;
            var sc = (IDXGISwapChain*)chain;
            return sc->lpVtbl->GetFullscreenState(sc, &state, null) >= 0 && state != 0;
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
                BufferUsage = Dxgi.UsageShaderInput | Dxgi.UsageRenderTargetOutput, BufferCount = IsExclusive ? 1u : BufferCount, Scaling = Dxgi.ScalingStretch,
                SwapEffect = IsExclusive ? Dxgi.SwapEffectDiscard : Dxgi.SwapEffectFlipSequential, AlphaMode = Dxgi.AlphaModeUnspecified, Flags = flags,
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
            if (IsExclusive)
            {
                // No frame-latency waitable: the original's exclusive chain has none
                // (GetMaximumFrameLatency on it is DXGI_ERROR_INVALID_CALL, j0009185), and the
                // flag "isn't supported in full-screen mode, unless the render API is Direct3D
                // 12" (DXGI_SWAP_CHAIN_FLAG).
                TakeBuffer();
                return;
            }
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
        /// `again`: resized even at the same size (DXGI_STATUS_MODE_CHANGED's "should call
        /// IDXGISwapChain::ResizeBuffers to match the new display mode", D3d11Present).
        public void Resize(int w, int h, bool again = false)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (w == Width && h == Height && !again) return;
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
            int n = ++presentCalls;
            if (n == removedAt)
            {
                // ONI_D3D11_FAIL=removed-after:N: what a removed device's Present returns
                // (IDXGISwapChain::Present, Return value), without presenting.
                EngineLog.Engine($"d3d11: {D3d11Fault.Variable}=removed-after:{n}: Present {n} returns DXGI_ERROR_DEVICE_REMOVED without presenting (a test; the device itself is not removed)");
                return Dxgi.ErrorDeviceRemoved;
            }
            long t = GlTimings.Now;
            int hr = ((IDXGISwapChain*)chain)->lpVtbl->Present((IDXGISwapChain*)chain, (uint)interval, presentFlags);
            if (n == modeChangedAt && hr == 0)
            {
                EngineLog.Engine($"d3d11: {D3d11Fault.Variable}=mode-changed-after:{n}: Present {n} presented; DXGI_STATUS_MODE_CHANGED returned in place of S_OK (a test)");
                hr = Dxgi.StatusModeChanged;
            }
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

        /// Game pictures and redraws presented on any chain (Present; device thread), for the
        /// fault hooks that count them (D3d11Fault: removed-after, mode-changed-after).
        static int presentCalls;
        static readonly int removedAt = D3d11Fault.Number("removed-after"), modeChangedAt = D3d11Fault.Number("mode-changed-after");

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
                resync = repeatSent = true;
            }
            return hr;
        }

        /// Repeats presented (StallRepeats).
        public long Repeats { get; private set; }

        /// Whether the frame-latency waitable is signalled, without waiting: a repeat goes only
        /// then, so it does not queue behind a picture (as DxgiSwapChain.LastPictureRetired).
        /// Takes the signal; the next wait recounts (WaitForFrame's resync), so nothing rests on
        /// how a repeat discarded for a newer picture signals.
        public bool LastPictureRetired() => waitable != IntPtr.Zero && Dxgi.WaitForSingleObjectEx(waitable, 0, 0) == Dxgi.WaitObject0;

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
        /// MaximumFrameLatency frames are queued.
        ///
        /// The count is not trusted across repeats or a timed-out wait (`resync`), as
        /// DxgiSwapChain does not trust it (its resync): whether a repeat discarded for a newer
        /// picture signals the waitable is not documented. The next wait then first takes every
        /// signal there is (up to 16, without waiting): one or more means the queue has room, and
        /// the count starts anew from there; none after a repeat means the repeat may still be
        /// queued, and the wait is bounded to a refresh and 2 ms (34 ms when the refresh is not
        /// known), as DxgiSwapChain's after a repeat. A wait that times out is logged (the first
        /// three) and the frame goes on, resyncing next time. Device thread (the constructor's
        /// thread before the first frame).
        public void WaitForFrame()
        {
            if (waitable == IntPtr.Zero) return; // the exclusive chain: Present itself blocks

            long t = GlTimings.Now;
            uint waited = Dxgi.WaitObject0;
            bool waitedAtAll = true;
            if (resync)
            {
                int drained = 0;
                while (drained < 16 && Dxgi.WaitForSingleObjectEx(waitable, 0, 0) == Dxgi.WaitObject0) drained++;
                resync = false;
                resyncs++;
                bool afterRepeat = repeatSent;
                repeatSent = false;
                if (drained > 0) waitedAtAll = false;
                else if (afterRepeat)
                {
                    long period = System.Threading.Interlocked.Read(ref refreshPeriod);
                    uint bound = (uint)(period > 0 ? period * 1000 / System.Diagnostics.Stopwatch.Frequency : 34) + 2;
                    waited = Dxgi.WaitForSingleObjectEx(waitable, bound, 0);
                    waitedAtAll = false;
                    // Not signalled within a refresh: the repeat was discarded without a signal,
                    // or is still to retire; either way the next wait counts anew.
                    if (waited != Dxgi.WaitObject0) resync = true;
                }
            }
            if (waitedAtAll)
            {
                waited = Dxgi.WaitForSingleObjectEx(waitable, WaitMilliseconds, 0);
                if (waited != Dxgi.WaitObject0)
                {
                    resync = true;
                    if (timeouts++ < 3)
                        EngineLog.Engine($"d3d11: present {presents}: the frame-latency wait ended with 0x{waited:X} ({(waited == Dxgi.WaitTimeout ? $"timed out after {WaitMilliseconds} ms" : "not signalled")}); going on");
                }
            }
            long ticks = GlTimings.Now - t;
            if (repeatsUnwaited > 0 && PresentTrace.Enabled)
                PresentTrace.Log($"d3d11: the first frame-latency wait after {repeatsUnwaited} repeats: {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F2} ms, "
                                 + $"{(waited == Dxgi.WaitObject0 ? "signalled" : $"0x{waited:X}")}; timeouts {timeouts}, resyncs {resyncs}");
            repeatsUnwaited = 0;
            waitTicks += ticks;
            waitMax = Math.Max(waitMax, ticks);
        }

        bool resync, repeatSent;

        long presents;
        int timeouts, resyncs, repeatsUnwaited;

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
                                 + $"largest {waitMax * ms:F2}; Present mean {this.presentTicks * ms / traced:F2} ms, largest {presentMax * ms:F2}; timeouts {timeouts}, resyncs {resyncs}, repeats {Repeats}");
                traced = 0;
                waitTicks = waitMax = this.presentTicks = presentMax = 0;
            }
            tracedInterval = interval;
            this.presentTicks += presentTicks;
            presentMax = Math.Max(presentMax, presentTicks);
            traced++;
        }

        public string Describe()
            => IsExclusive ? $"exclusive swap chain {Width}x{Height} R8G8B8A8_UNORM, DISCARD, 1 buffer, ALLOW_MODE_SWITCH, full screen {InFullscreenState()}" : $"swap chain {Width}x{Height} R8G8B8A8_UNORM, FLIP_SEQUENTIAL, {BufferCount} buffers, maximum frame latency {MaximumFrameLatency} (waitable), "
               + $"tearing without vsync {(tearing ? "allowed" : "not supported")}";

        public void Dispose()
        {
            FramePacing.UnpacedPresent();
            // A chain in DXGI's full-screen state is taken out of it first ("Before releasing a
            // swap chain, first switch to windowed mode", "DXGI overview", Destroying a swap
            // chain).
            if (fullscreen && chain != null)
            {
                var sc = (IDXGISwapChain*)chain;
                sc->lpVtbl->SetFullscreenState(sc, 0, null);
                fullscreen = false;
            }
            Com.Release(ref view);
            Com.Release(ref buffer);
            if (waitable != IntPtr.Zero) Dxgi.CloseHandle(waitable);
            waitable = IntPtr.Zero;
            Com.Release(ref chain2);
            Com.Release(ref chain);
        }
    }
}
