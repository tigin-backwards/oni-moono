using System;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;

namespace OniMoono.Desktop.D3d11
{
    /// Exclusive full screen as the original's Direct3D 11 makes it (d3d11-plan.md, "M4 exclusive
    /// full screen"; observed j0009185): a second swap chain of the legacy blt model, DISCARD, 1
    /// buffer, flags ALLOW_MODE_SWITCH, taken into DXGI's full-screen state (ResizeTarget, then
    /// SetFullscreenState(TRUE), then ResizeBuffers), presented to while full screen; the flip
    /// chain is kept for the other modes. Losing the focus, the window goes windowed (the
    /// platform: GlfwPlatform.PollEvents), as the original's does; refused
    /// (DXGI_ERROR_NOT_CURRENTLY_AVAILABLE, the window not focused), a fullscreen window stays.
    ///
    /// Threads. The device thread alone uses the context and both chains (M2's rule), so the
    /// full-screen calls and every Present run there. DXGI's full-screen state makes them send
    /// the window messages: "Present1 calls ::SetWindowPos() or ::SetWindowStyle() and either of
    /// these methods call ::SendMessage()", and "never have the message-pump thread wait on the
    /// render thread when you use full-screen swap chains" ("DXGI overview", Multithread
    /// considerations); "never block the message pump" ("DXGI best practices"). So while a chain
    /// may be full screen, and while one is entered or left, the main thread's waits for the
    /// device thread answer sent messages every millisecond (GlWorker.WaitPump,
    /// GlfwPlatform.PumpSentMessages, PeekMessage with PM_QS_SENDMESSAGE).
    ///
    /// Pacing without the waitable: the flag "isn't supported in full-screen mode, unless the
    /// render API is Direct3D 12" (DXGI_SWAP_CHAIN_FLAG), and the original's chain has none
    /// (j0009185), so its Present paces the frames as the original's does; no repeat goes then
    /// (they need the waitable's "last picture retired", MayRepeat), the splash and the loading
    /// animation present as frames do.
    internal sealed unsafe partial class D3d11Device : IExclusiveFullScreen
    {
        D3d11SwapChain exclusive;

        /// The chain frames are presented to: the exclusive one while full screen.
        D3d11SwapChain Out => exclusive ?? chain;

        /// Exclusive full screen is on (main thread reads; the device thread sets).
        public bool Active => exclusiveActive;
        volatile bool exclusiveActive;

        /// Main thread (GlfwPlatform.SetResolution): the exclusive chain made and taken full
        /// screen on the device thread, the main thread answering sent messages meanwhile.
        public int Enter(int width, int height, uint refreshNumerator, uint refreshDenominator)
        {
            worker.WaitPump = platform.PumpSentMessages;
            int hr;
            try
            {
                hr = worker.Call(() => EnterNow(width, height, refreshNumerator, refreshDenominator));
            }
            catch (GlDeviceStoppedException)
            {
                hr = -1;
            }
            if (hr != 0) worker.WaitPump = null;
            return hr;
        }

        /// Main thread: out of exclusive full screen, the flip chain presenting again.
        public void Leave()
        {
            if (!exclusiveActive) return;
            try
            {
                worker.Call(LeaveNow);
            }
            catch (GlDeviceStoppedException)
            {
            }
            worker.WaitPump = null;
        }

        int EnterNow(int width, int height, uint refreshNumerator, uint refreshDenominator)
        {
            if (lost != null || exclusive != null) return -1;
            int hr;
            try
            {
                hr = D3d11SwapChain.CreateExclusive(d3d, platform.Win32Handle, width, height, refreshNumerator, refreshDenominator, out exclusive);
            }
            catch (D3d11UnavailableException e)
            {
                EngineLog.Engine($"d3d11: exclusive full screen {width}x{height} could not be made: {e.Message}");
                return -1;
            }
            // ResizeBuffers cleared the context's state (D3d11SwapChain.SetFullscreen).
            ForgetBindings();
            SetTarget(lastTarget);
            EngineLog.Engine($"d3d11: exclusive full screen {width}x{height} at {refreshNumerator}/{refreshDenominator}: SetFullscreenState {Dxgi.Hr(hr)}; "
                             + (exclusive != null ? exclusive.Describe() : "a fullscreen window instead"));
            exclusiveActive = exclusive != null;
            resizedSinceShown = true;
            return hr;
        }

        void LeaveNow()
        {
            if (exclusive == null) return;
            exclusive.Dispose(); // SetFullscreenState(FALSE) first
            exclusive = null;
            exclusiveActive = false;
            // Nothing of the exclusive chain's stays bound (its buffer's view went with it); the
            // device's target is set again on the flip chain's side.
            Ctx->lpVtbl->ClearState(Ctx);
            ForgetBindings();
            resizedSinceShown = true;
            SetTarget(lastTarget);
            EngineLog.Engine("d3d11: exclusive full screen left; the flip chain presents again");
        }
    }
}
