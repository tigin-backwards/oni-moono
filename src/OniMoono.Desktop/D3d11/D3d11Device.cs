using System;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace OniMoono.Desktop.D3d11
{
    /// What SystemInfo reports on the Direct3D 11 device, as the original's D3D11 player
    /// (docs/research/d3d11-unity-probes.md §1, measured on the RTX 3090).
    internal sealed class D3d11Caps(D3d11Context d3d) : GraphicsCaps
    {
        public override GraphicsDeviceType DeviceType => GraphicsDeviceType.Direct3D11;
        public override string DeviceName => d3d.Adapter.Name;
        /// "NVIDIA" on NVIDIA (measured); other vendors' strings are UNVERIFIED (M7), so they
        /// read empty rather than guessed.
        public override string DeviceVendor => d3d.Adapter.VendorId == 0x10DE ? "NVIDIA" : "";
        public override string DeviceVersion => d3d.VersionString;
        public override int DeviceId => (int)d3d.Adapter.DeviceId;
        public override int VendorId => (int)d3d.Adapter.VendorId;
        /// The adapter's dedicated video memory in MB. The original reported 24322 on the 3090,
        /// which this formula's source is UNVERIFIED against (M7).
        public override int MemorySizeMB => (int)(d3d.Adapter.DedicatedVideoMemory / (1024 * 1024));
        public override int ShaderLevel => 50;
        public override int MaxTextureSize => 16384;
        public override int MaxRenderTargets => 8;
        public override bool Supports2DArrayTextures => true;
        public override bool Supports3DTextures => true;
        public override bool SupportsInstancing => true;
        /// The original reports True; this device runs no compute (M7 decides parity).
        public override bool SupportsComputeShaders => false;

        /// The original's SupportsTextureFormat: all of the 64 values but the ETC, EAC, PVRTC and
        /// ASTC formats and the crunched ETC ones (d3d11-unity-probes.md §1).
        public override bool SupportsTexture(TextureFormat format)
        {
            string name = format.ToString();
            return !(name.StartsWith("ETC", StringComparison.Ordinal) || name.StartsWith("EAC", StringComparison.Ordinal)
                     || name.StartsWith("PVRTC", StringComparison.Ordinal) || name.StartsWith("ASTC", StringComparison.Ordinal));
        }

        /// All 28 SupportsRenderTextureFormat values are True on the original (§1).
        public override bool SupportsRenderTexture(RenderTextureFormat format) => true;
    }

    /// The Direct3D 11 render device (docs/research/d3d11-plan.md, milestone M2). So far: the
    /// device, its own swap chain, clears of the back buffer and presents, with the debug layer's
    /// messages in the log under `--d3d11-debug`. Draws, textures, meshes, render textures and
    /// readbacks are not yet made: they are counted and dropped. Everything runs on the main
    /// thread (Present on the immediate context's thread, as DXGI asks: "Multithreading
    /// Considerations"); the device thread comes with the draws.
    internal sealed unsafe class D3d11Device : RenderDevice
    {
        /// `--vsync N`: the swap interval in place of QualitySettings.vSyncCount.
        public static int SwapIntervalOverride = -1;

        readonly GlfwPlatform platform;
        readonly D3d11Context d3d;
        readonly D3d11SwapChain chain;
        readonly D3d11Caps caps;
        bool toBackBuffer = true, quitRequested;
        long dropped, debugMessages;
        string lost;

        public D3d11Device(GlfwPlatform platform, D3d11Context d3d)
        {
            this.platform = platform;
            this.d3d = d3d;
            caps = new D3d11Caps(d3d);
            chain = D3d11SwapChain.Create(d3d, platform.Win32Handle, platform.WindowFramebufferWidth, platform.WindowFramebufferHeight);
            EngineLog.Engine($"d3d11: {d3d.Adapter.Name} (vendor 0x{d3d.Adapter.VendorId:x4}, device 0x{d3d.Adapter.DeviceId:x4}), {d3d.VersionString}, "
                             + $"{(D3d11Context.Validation ? "debug layer on" : "no debug layer")}; {chain.Describe()}");
            DrainDebugMessages();
        }

        public override GraphicsCaps Caps => caps;
        public override bool RendersFrames => true;
        public override long FailedCommands => lost != null ? 1 : 0;

        // ----- Not made yet: counted, and said once -----

        void Dropped(string what)
        {
            if (dropped++ == 0) EngineLog.Engine($"d3d11: {what} and the rest of drawing are not implemented yet: dropped (M2 in progress)");
        }

        public override void ReadPixels(RenderTextureNative source, RectInt rect, Texture2DNative destination, int destX, int destY) => Dropped("ReadPixels");
        public override void ReleaseResource(object gpu) { }
        public override bool SetPass(Material material, int pass) => true;
        public override void WarmupShaders() { }
        protected override void ApplyCamera(in CameraConstants camera) { }
        public override void Draw(in DrawRequest draw) => Dropped("draws");
        public override void DrawImmediate(ImmediatePrimitive primitive, ReadOnlySpan<ImmediateVertex> vertices, Matrix4x4 modelView, Matrix4x4 projection) => Dropped("immediate draws");
        public override void DrawMeshNow(MeshNative mesh, int subMesh, Matrix4x4 objectToWorld, Matrix4x4 view, Matrix4x4 projection) => Dropped("DrawMeshNow");

        // ----- Targets and clears -----

        public override void SetRenderTarget(in RenderTargetBinding target) => toBackBuffer = target.Colors == null;
        public override void SetViewport(Rect pixels) { }
        public override void SetScissor(Rect? pixels) { }

        /// Clears the back buffer to the colour as given: the game renders in gamma space, so the
        /// UNORM back buffer takes it unconverted (d3d11-plan.md "Conventions": no sRGB).
        public override void Clear(bool color, bool depth, Color backgroundColor, float depthValue)
        {
            if (!toBackBuffer || !color || lost != null) return;
            var rgba = stackalloc float[] { backgroundColor.r, backgroundColor.g, backgroundColor.b, backgroundColor.a };
            d3d.Context->lpVtbl->ClearRenderTargetView(d3d.Context, chain.View, rgba);
        }

        // ----- Present -----

        public override void Present()
        {
            if (lost != null)
            {
                if (!quitRequested)
                {
                    quitRequested = true;
                    Debug.LogError($"Direct3D 11 stopped working: {lost}, so the game quits. The log is {EngineLog.FilePath ?? Application.consoleLogPath}.");
                    PlayerLoop.RequestQuit(1);
                }
                return;
            }
            int interval = SwapIntervalOverride >= 0 ? SwapIntervalOverride : QualitySettings.vSyncCount;
            int hr = chain.Present(interval);
            DrainDebugMessages();
            if (hr < 0)
            {
                // Present reports a removed or reset device (DXGI_ERROR_DEVICE_REMOVED/RESET); the
                // reason is asked of the device before it is released ("Handle device removed
                // scenarios in Direct3D 11").
                lost = Dxgi.DeviceLost(hr)
                    ? $"the graphics device was lost ({Dxgi.Hr(hr)}, reason {Dxgi.Hr(d3d.Device->lpVtbl->GetDeviceRemovedReason(d3d.Device))})"
                    : $"Present failed ({Dxgi.Hr(hr)})";
                EngineLog.Engine($"d3d11: {lost}");
                return;
            }
            try
            {
                chain.Resize(platform.WindowFramebufferWidth, platform.WindowFramebufferHeight);
            }
            catch (D3d11UnavailableException e)
            {
                lost = e.Message;
                EngineLog.Engine($"d3d11: {lost}");
                return;
            }
            // The flip model unbinds the back buffer at Present: bound again for the next frame.
            var view = chain.View;
            d3d.Context->lpVtbl->OMSetRenderTargets(d3d.Context, 1, &view, null);
            toBackBuffer = true;
        }

        /// The debug layer's stored messages, each logged (up to 100), then cleared; read as
        /// ID3D11InfoQueue::GetMessage's documentation says (an index below
        /// GetNumStoredMessagesAllowedByRetrievalFilter, the size first).
        void DrainDebugMessages()
        {
            var queue = d3d.InfoQueue;
            if (queue == null) return;
            ulong n = queue->lpVtbl->GetNumStoredMessagesAllowedByRetrievalFilter(queue);
            for (ulong i = 0; i < n; i++)
            {
                nuint length = 0;
                if (queue->lpVtbl->GetMessage(queue, i, null, &length) < 0 || length < (nuint)sizeof(D3D11_MESSAGE)) continue;
                var bytes = new byte[(int)length];
                fixed (byte* b = bytes)
                {
                    var m = (D3D11_MESSAGE*)b;
                    if (queue->lpVtbl->GetMessage(queue, i, m, &length) < 0 || m->pDescription == null || m->DescriptionByteLength == 0) continue;
                    if (debugMessages++ < 100)
                        EngineLog.Engine($"d3d11 debug layer: {m->Severity} {m->ID}: {new string(m->pDescription, 0, (int)m->DescriptionByteLength - 1)}");
                }
            }
            queue->lpVtbl->ClearStoredMessages(queue);
        }

        public override string TakeFrameCounters(int frames) => dropped == 0 ? null : $"d3d11: {dropped} draw calls dropped so far, {debugMessages} debug-layer messages";

        public override void StopForQuit()
        {
            DrainDebugMessages();
            if (debugMessages > 0) EngineLog.Engine($"d3d11: {debugMessages} debug-layer messages in this run");
        }
    }
}
