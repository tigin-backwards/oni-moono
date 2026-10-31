using System;
using OniMoono.Engine;

namespace OniMoono.Desktop.Gl
{
    /// Windows: the D3D11 device and flip-model swap chain both devices present through
    /// (DxgiTarget for OpenGL, Vulkan.VkDxgiTarget for Vulkan), as the original (Unity 6 on D3D11)
    /// presents, so that Windows knows the game is SDR: R8G8B8A8_UNORM, FLIP_DISCARD, two buffers,
    /// the default colour space, a frame-latency waitable object at a latency of 2, tearing only
    /// without vsync. Each device renders into a D3D11 texture of the target's, shared with it;
    /// Present copies it into the back buffer (CopySubresourceRegion) and presents.
    /// ARCHITECTURE §3.5, "Presentation on Windows".
    ///
    /// Threads: made on one thread and from then on used and released by the presenter thread
    /// alone. DXGI is told to leave the window alone (DXGI_MWA_NO_WINDOW_CHANGES | NO_ALT_ENTER)
    /// and the swap chain is never put in DXGI's fullscreen state, so neither Present nor
    /// ResizeBuffers sends the window a message the main thread (the window's, which may be
    /// waiting on the device thread, which may be waiting on the presenter) would have to answer.
    /// A window resized by the main thread is stretched by DXGI (DXGI_SCALING_STRETCH) until the
    /// next picture resizes the buffers.
    internal sealed unsafe class DxgiSwapChain : IDisposable
    {
        /// Present through DXGI (the Moono Tweaks screen's "dxgi" row, `--dxgi on|off`); set by the
        /// host before the device starts.
        public static bool Enabled;

        /// Diagnostics (`--dxgi-fault WHAT[,WHAT]`): failures made up, for testing the ways back.
        /// At start-up (the device's own presentation instead): "adapter", "device", "swapchain",
        /// "association" (after the swap chain is made: DXGI is then restarted);
        /// OpenGL's "extension" (no WGL_NV_DX_interop2) and "register"; Vulkan's "import" and
        /// "importview:N" (the Nth image view of the shared texture: 2, a grown one, in play). While
        /// running (DXGI is then restarted): "lock:N" (the Nth interop lock, or keyed-mutex
        /// acquire), "resize:N", "removed:N" (the Nth Present failing with
        /// DXGI_ERROR_DEVICE_REMOVED). "norestart": every restart fails too. Each happens once,
        /// so a restart succeeds. Vulkan's "fencetimeout:N" stands for a keyed-mutex acquire in
        /// the submission that never completes: the Nth wait for a DXGI picture's batch times out
        /// at once, and presenting ends for good (VkPresenter.WaitDxgiBatch).
        public static string Fault;

        static bool faultDone;

        static bool HasFault(string token) => Fault != null && Array.IndexOf(Fault.Split(','), token) >= 0;

        /// The made-up fault at `step` (start-up), once.
        public static bool Faulty(string step)
        {
            if (faultDone || !HasFault(step)) return false;
            faultDone = true;
            return true;
        }

        /// The made-up fault at the Nth time of `step` (1-based), once.
        public static bool Faulty(string step, int count)
        {
            if (faultDone || !HasFault($"{step}:{count}")) return false;
            faultDone = true;
            return true;
        }

        /// A restart after a made-up fault, with "norestart": it fails.
        public static bool RestartRefused => faultDone && HasFault("norestart");

        public const uint BufferCount = 2;
        /// Pictures in flight with vsync: 2, Unity's default of two queued frames. Measured on U60
        /// at 60 Hz (a light colony): with 1, Vulkan on the AMD iGPU ran at 55.2 fps against 59.9
        /// on its own swapchain; with 2, at 59.5; on the RTX 3090 both held 60. Diagnostics:
        /// ONI_DXGI_LATENCY=1 to 3.
        public static readonly uint MaximumFrameLatency = uint.TryParse(Environment.GetEnvironmentVariable("ONI_DXGI_LATENCY"), out uint l) && l is >= 1 and <= 3 ? l : 2;
        /// Presents that may still go before one is waited for (MaximumFrameLatency - 1 after a
        /// resync).
        int freeSlots;
        /// How long a present waits for its picture to be on its way before going on regardless.
        const uint WaitMilliseconds = 1000;

        // COM objects (one reference each, released once in Dispose).
        void* factory, device, context, swapChain, swapChain2, backBuffer;
        IntPtr waitable;
        uint swapFlags;
        bool tearing, occluded;
        int featureLevel;
        /// The waitable object's count may be off by pictures not waited for (it starts at the
        /// latency; an occluded picture; a wait that timed out): taken down to nothing before the
        /// next Present.
        bool resync = true;
        int resizes, presents, waitTimeouts;

        public void* Device => device;
        public void* Context => context;
        public long AdapterLuid { get; }
        public string AdapterName { get; private set; } = "";
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string Failure { get; private set; }
        /// The part of the last Present spent waiting for the picture to be on its way.
        public long LastWait { get; private set; }

        /// A swap chain was made for the window: from then on, Windows will not show the device's
        /// own presentation reliably on it (OpenGL's SwapBuffers stays frozen), so a failure after
        /// this point is treated as one while running (DXGI is restarted).
        public bool WindowTouched { get; private set; }

        DxgiSwapChain(long luid) => AdapterLuid = luid;

        /// A D3D11 device on the adapter `luid`, without a swap chain yet (AttachWindow makes it,
        /// once the device's interop has been shown to work); null, with the reason, when any step
        /// fails (what was made is released).
        public static DxgiSwapChain Open(long luid, out string reason)
        {
            var chain = new DxgiSwapChain(luid);
            try
            {
                reason = chain.OpenDevice();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                reason = e.Message;
            }
            if (reason == null) return chain;
            chain.Dispose();
            return null;
        }

        string OpenDevice()
        {
            var iid = Dxgi.IIDFactory2;
            void* f;
            int hr = Dxgi.CreateDXGIFactory2(0, &iid, &f);
            if (Dxgi.Failed(hr)) return $"CreateDXGIFactory2 failed ({Dxgi.Hr(hr)})";
            factory = f;
            void* f4 = null, adapter = null;
            hr = Dxgi.QueryInterface(factory, Dxgi.IIDFactory4, &f4);
            if (Dxgi.Failed(hr)) return $"no IDXGIFactory4 ({Dxgi.Hr(hr)})";
            hr = Dxgi.EnumAdapterByLuid(f4, AdapterLuid, Dxgi.IIDAdapter1, &adapter);
            Dxgi.Release(ref f4);
            if (Dxgi.Failed(hr)) return $"no DXGI adapter with the device's LUID {AdapterLuid:X} ({Dxgi.Hr(hr)})";
            Dxgi.AdapterDesc1 desc1;
            if (!Dxgi.Failed(Dxgi.GetDesc1(adapter, &desc1))) AdapterName = desc1.Name;
            void* d = null, c = null;
            int level = 0;
            hr = Faulty("device") ? unchecked((int)0x887A0004) : Dxgi.D3D11CreateDevice(adapter, Dxgi.DriverTypeUnknown, IntPtr.Zero, 0, null, 0, Dxgi.D3D11SdkVersion, &d, &level, &c);
            Dxgi.Release(ref adapter);
            if (Dxgi.Failed(hr)) return $"D3D11CreateDevice on {AdapterName} failed ({Dxgi.Hr(hr)})";
            device = d;
            context = c;
            featureLevel = level;
            void* f5;
            if (!Dxgi.Failed(Dxgi.QueryInterface(factory, Dxgi.IIDFactory5, &f5)))
            {
                int allow = 0;
                tearing = !Dxgi.Failed(Dxgi.CheckFeatureSupport(f5, Dxgi.FeaturePresentAllowTearing, &allow, sizeof(int))) && allow != 0;
                Dxgi.Release(ref f5);
            }
            return null;
        }

        /// The swap chain for window `hwnd`, `w` x `h`: the last step of a start, once the
        /// device's interop has been shown to work. Null on success, else the reason;
        /// WindowTouched says whether the failure came after the swap chain was made.
        public string AttachWindow(IntPtr hwnd, int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            swapFlags = Dxgi.SwapChainFlagFrameLatencyWaitable | (tearing ? Dxgi.SwapChainFlagAllowTearing : 0);
            var desc = new Dxgi.SwapChainDesc1
            {
                Width = (uint)w, Height = (uint)h, Format = Dxgi.FormatR8G8B8A8Unorm, SampleCount = 1,
                BufferUsage = Dxgi.UsageRenderTargetOutput, BufferCount = BufferCount, Scaling = Dxgi.ScalingStretch,
                SwapEffect = Dxgi.SwapEffectFlipDiscard, AlphaMode = Dxgi.AlphaModeIgnore, Flags = swapFlags,
            };
            void* s = null;
            int hr = Faulty("swapchain") ? unchecked((int)0x80070005) : Dxgi.CreateSwapChainForHwnd(factory, device, hwnd, &desc, &s);
            if (Dxgi.Failed(hr)) return $"CreateSwapChainForHwnd failed ({Dxgi.Hr(hr)})";
            swapChain = s;
            WindowTouched = true;
            Width = w;
            Height = h;
            hr = Faulty("association") ? unchecked((int)0x887A0001) : Dxgi.MakeWindowAssociation(factory, hwnd, Dxgi.WindowAssociationNoWindowChanges | Dxgi.WindowAssociationNoAltEnter);
            if (Dxgi.Failed(hr)) return $"MakeWindowAssociation failed ({Dxgi.Hr(hr)})";
            void* s2;
            hr = Dxgi.QueryInterface(swapChain, Dxgi.IIDSwapChain2, &s2);
            if (Dxgi.Failed(hr)) return $"no IDXGISwapChain2 ({Dxgi.Hr(hr)})";
            swapChain2 = s2;
            hr = Dxgi.SetMaximumFrameLatency(swapChain2, MaximumFrameLatency);
            if (Dxgi.Failed(hr)) return $"SetMaximumFrameLatency failed ({Dxgi.Hr(hr)})";
            waitable = Dxgi.GetFrameLatencyWaitableObject(swapChain2);
            if (waitable == IntPtr.Zero) return "no frame latency waitable object";
            if (!GetBackBuffer()) return Failure;
            return null;
        }

        /// The start-up log line: the adapter (`how` it was found), the swap chain's settings as
        /// DXGI reports them, and `shared`, what the device renders into.
        public string Describe(string how, string shared)
        {
            var actual = new Dxgi.SwapChainDesc1();
            int hr = Dxgi.GetDesc1(swapChain, &actual);
            if (Dxgi.Failed(hr)) return $"present: DXGI on {AdapterName} ({how}); IDXGISwapChain1::GetDesc1 failed ({Dxgi.Hr(hr)})";
            return $"present: DXGI on {AdapterName} ({how}), feature level {featureLevel >> 12}_{(featureLevel >> 8) & 0xF}: swap chain {actual.Width}x{actual.Height} "
                + $"{(actual.Format == Dxgi.FormatR8G8B8A8Unorm ? "R8G8B8A8_UNORM" : actual.Format.ToString())}, "
                + $"{(actual.SwapEffect == Dxgi.SwapEffectFlipDiscard ? "FLIP_DISCARD" : actual.SwapEffect == Dxgi.SwapEffectFlipSequential ? "FLIP_SEQUENTIAL" : actual.SwapEffect.ToString())}, "
                + $"{actual.BufferCount} buffers, colour space RGB_FULL_G22_NONE_P709 (default, SDR), scaling {(actual.Scaling == Dxgi.ScalingStretch ? "STRETCH" : actual.Scaling.ToString())}, "
                + $"maximum frame latency {MaximumFrameLatency} (waitable), tearing without vsync {(tearing ? "allowed" : "not supported")}; {shared}";
        }

        public bool Fail(string reason)
        {
            Failure ??= reason;
            return false;
        }

        /// For a failed call's message: why the device was removed, when it was.
        public string DeviceState(int hr)
            => Dxgi.DeviceLost(hr) && device != null ? $"; device removed reason {Dxgi.Hr(Dxgi.GetDeviceRemovedReason(device))}" : "";

        bool GetBackBuffer()
        {
            void* b;
            int hr = Dxgi.GetBuffer(swapChain, 0, Dxgi.IIDTexture2D, &b);
            if (Dxgi.Failed(hr)) return Fail($"GetBuffer failed ({Dxgi.Hr(hr)}{DeviceState(hr)})");
            backBuffer = b;
            return true;
        }

        /// The buffers made `w` x `h` when they are not.
        public bool Resize(int w, int h)
        {
            if (Failure != null) return false;
            if (w == Width && h == Height) return true;
            resizes++;
            Dxgi.Release(ref backBuffer);
            // Nothing of the swap chain's may still be referenced, bound or queued.
            Dxgi.ClearState(context);
            Dxgi.Flush(context);
            int hr = Faulty("resize", resizes) ? Dxgi.ErrorDeviceRemoved : Dxgi.ResizeBuffers(swapChain, 0, (uint)w, (uint)h, 0, swapFlags);
            if (Dxgi.Failed(hr)) return Fail($"ResizeBuffers to {w}x{h} failed ({Dxgi.Hr(hr)}{DeviceState(hr)})");
            if (PresentTrace.Enabled) PresentTrace.Log($"dxgi: buffers resized {Width}x{Height} -> {w}x{h}");
            Width = w;
            Height = h;
            return GetBackBuffer();
        }

        /// Copies the top-left Width x Height of `source` (a D3D11 texture of the device's) into
        /// the back buffer, presents it `interval` vertical blanks after the last (0: at once)
        /// and, with vsync, waits until the picture before it is on its way (MaximumFrameLatency
        /// pictures in flight): the waitable object is signalled as each presented picture
        /// retires; an occluded picture was not queued and is not waited for. The
        /// caller has made `source` ready for D3D11 (the interop unlock, the keyed mutex).
        public bool Present(void* source, int interval)
        {
            if (Failure != null) return false;
            CopyToBackBuffer(source);
            return PresentCopied(interval);
        }

        /// Presents what was copied into the back buffer (Present's second half, for a caller
        /// that must release `source` between the copy and Present: the keyed mutex).
        public bool PresentCopied(int interval)
        {
            LastWait = 0;
            if (Failure != null) return false;
            presents++;
            if (resync)
            {
                for (int i = 0; i < 16 && Dxgi.WaitForSingleObjectEx(waitable, 0, 0) == Dxgi.WaitObject0; i++) { }
                resync = false;
                freeSlots = (int)MaximumFrameLatency - 1;
            }
            uint sync = (uint)Math.Clamp(interval, 0, 4);
            uint flags = sync == 0 && tearing ? Dxgi.PresentAllowTearing : 0;
            int hr = Faulty("removed", presents) ? Dxgi.ErrorDeviceRemoved : Dxgi.Present(swapChain, sync, flags);
            bool nowOccluded = hr == Dxgi.StatusOccluded;
            if (nowOccluded != occluded && PresentTrace.Enabled) PresentTrace.Log($"dxgi: present {presents}: {(nowOccluded ? "occluded (not shown)" : "shown again")}");
            occluded = nowOccluded;
            if (Dxgi.Failed(hr)) return Fail($"Present failed ({Dxgi.Hr(hr)}{DeviceState(hr)})");
            // Without vsync nothing waits, as on the swapchain paths: retiring can take a frame or
            // two (measured on the AMD iGPU, its picture copied to a monitor on another GPU: 29 ms
            // a picture), which would cap the frame rate. The next Present resyncs the count; the
            // interop lock or keyed mutex keeps at most a picture in flight meanwhile.
            if (nowOccluded || sync == 0) resync = true;
            else if (freeSlots > 0) freeSlots--;
            else
            {
                long t = GlTimings.Now;
                if (Dxgi.WaitForSingleObjectEx(waitable, WaitMilliseconds, 0) != Dxgi.WaitObject0)
                {
                    resync = true;
                    if (waitTimeouts++ < 3) EngineLog.Engine($"present: DXGI picture {presents} not on its way after {WaitMilliseconds} ms; going on");
                }
                LastWait = GlTimings.Now - t;
            }
            return true;
        }

        /// Copies into the back buffer without presenting (see PresentCopied).
        public void CopyToBackBuffer(void* source)
        {
            var box = new Dxgi.Box { Right = (uint)Width, Bottom = (uint)Height, Back = 1 };
            Dxgi.CopySubresourceRegion(context, backBuffer, 0, 0, 0, 0, source, 0, &box);
        }

        /// Releases everything, in the reverse order of making it. Safe after a failure at any
        /// step, and more than once. The caller releases its shared texture first.
        public void Dispose()
        {
            Dxgi.Release(ref backBuffer);
            Dxgi.Release(ref swapChain2);
            Dxgi.Release(ref swapChain);
            if (context != null)
            {
                Dxgi.ClearState(context);
                Dxgi.Flush(context);
                Dxgi.Release(ref context);
            }
            Dxgi.Release(ref device);
            Dxgi.Release(ref factory);
            if (waitable != IntPtr.Zero)
            {
                Dxgi.CloseHandle(waitable);
                waitable = IntPtr.Zero;
            }
        }
    }
}
