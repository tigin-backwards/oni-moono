using System;
using System.Collections.Generic;
using Brutal.GlfwApi;
using OniMoono.Engine;
using static OniMoono.Desktop.Gl.GlConst;
using N = Brutal.OpenGlApi.OpenGl.Native;

namespace OniMoono.Desktop.Gl
{
    /// Windows, OpenGL: the window's picture goes to the screen through DxgiSwapChain, as the
    /// original's does, so that Windows knows it is SDR: OpenGL's own presentation has no swap
    /// chain Windows can see, and Auto HDR, RTX HDR or the driver's "layered on DXGI" present can
    /// then treat the game as HDR content. ARCHITECTURE §3.5, "Presentation on Windows".
    ///
    /// The presenter draws into a GL texture that is a D3D11 texture of ours, shared through
    /// WGL_NV_DX_interop2 (locked while GL draws into it); Show copies it into the swap chain's
    /// back buffer and presents. The swap chain's own buffers are never registered with GL:
    /// re-registering them after a resize is suspected of a use-after-free in NVIDIA's driver
    /// (Godot PR #94503). The shared texture is registered once and made anew only when the
    /// window outgrows it.
    ///
    /// Threads: everything here is made, used and released on the presenter thread, with the
    /// window's GL context current; no other thread holds any of it.
    internal sealed unsafe class DxgiTarget : IWindowTarget
    {
        // Read and write: the splash blends over what it drew, which reads the texture; WRITE_DISCARD
        // promises the GL never reads it.
        const uint WglAccessReadWrite = 1;

        readonly IntPtr hwnd;
        DxgiSwapChain chain;
        // The shared texture (one COM reference, released once).
        void* shared;
        // Win32 handles: the interop device and object.
        IntPtr interopDevice, interopObject;
        // GL objects: the texture registered with `shared`, and the framebuffer it is attached to.
        uint texture, framebuffer, attached;
        int capacityWidth, capacityHeight;
        bool locked;
        int begins;
        string failure;

        public long AdapterLuid => chain?.AdapterLuid ?? 0;
        public string Name => "DXGI flip model";
        public uint Framebuffer => framebuffer;
        public bool FlipY => true;
        public string Failure => failure ?? chain?.Failure;
        public long LastWait => chain?.LastWait ?? 0;

        delegate* unmanaged<void*, IntPtr> dxOpenDevice;
        delegate* unmanaged<IntPtr, int> dxCloseDevice;
        delegate* unmanaged<IntPtr, void*, uint, uint, uint, IntPtr> dxRegisterObject;
        delegate* unmanaged<IntPtr, IntPtr, int> dxUnregisterObject;
        delegate* unmanaged<IntPtr, int, IntPtr*, int> dxLockObjects, dxUnlockObjects;

        DxgiTarget(IntPtr hwnd) => this.hwnd = hwnd;

        /// The target for window `hwnd`, whose GL context is current, for a first picture of
        /// `width` x `height`; null, with the reason, when any step fails (what was made is
        /// released).
        public static DxgiTarget Create(IntPtr hwnd, int width, int height, out string reason, out bool windowTouched)
        {
            var target = new DxgiTarget(hwnd);
            try
            {
                reason = target.Start(Math.Max(1, width), Math.Max(1, height));
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                reason = e.Message;
            }
            windowTouched = target.chain?.WindowTouched ?? false;
            if (reason == null) return target;
            target.Dispose();
            return null;
        }

        string Start(int w, int h)
        {
            if (DxgiSwapChain.RestartRefused) return "a restart that fails";
            if (DxgiSwapChain.Faulty("extension") || !Glfw.ExtensionSupported("WGL_NV_DX_interop2")) return "the OpenGL driver has no WGL_NV_DX_interop2";
            dxOpenDevice = (delegate* unmanaged<void*, IntPtr>)Glfw.GetProcAddress("wglDXOpenDeviceNV"u8);
            dxCloseDevice = (delegate* unmanaged<IntPtr, int>)Glfw.GetProcAddress("wglDXCloseDeviceNV"u8);
            dxRegisterObject = (delegate* unmanaged<IntPtr, void*, uint, uint, uint, IntPtr>)Glfw.GetProcAddress("wglDXRegisterObjectNV"u8);
            dxUnregisterObject = (delegate* unmanaged<IntPtr, IntPtr, int>)Glfw.GetProcAddress("wglDXUnregisterObjectNV"u8);
            dxLockObjects = (delegate* unmanaged<IntPtr, int, IntPtr*, int>)Glfw.GetProcAddress("wglDXLockObjectsNV"u8);
            dxUnlockObjects = (delegate* unmanaged<IntPtr, int, IntPtr*, int>)Glfw.GetProcAddress("wglDXUnlockObjectsNV"u8);
            if (dxOpenDevice == null || dxCloseDevice == null || dxRegisterObject == null || dxUnregisterObject == null || dxLockObjects == null || dxUnlockObjects == null)
                return "WGL_NV_DX_interop2's entry points are missing";

            long luid = FindAdapter(out string how);
            if (luid == 0) return how;
            // Every interop step first, the swap chain last: once the window has had a flip-model
            // swap chain, OpenGL's own SwapBuffers no longer reaches the screen.
            chain = DxgiSwapChain.Open(luid, out string reason);
            if (chain == null) return reason;

            interopDevice = dxOpenDevice(chain.Device);
            if (interopDevice == IntPtr.Zero) return $"wglDXOpenDeviceNV failed (error 0x{Dxgi.GetLastError():X})";
            if (!MakeShared(w, h)) return Failure;
            // The whole round once, cleared to black, so that a driver that cannot do it fails
            // here rather than on the first frame.
            if (!Lock()) return Failure;
            GlApi.BindFramebuffer(DrawFramebuffer, framebuffer);
            Attach();
            uint status = N.glCheckFramebufferStatus(DrawFramebuffer).Id;
            N.glClearColor(0f, 0f, 0f, 1f);
            N.glClear(ColorBufferBit);
            if (!Unlock()) return Failure;
            if (status != FramebufferComplete) return $"the shared texture's framebuffer is incomplete (0x{status:X})";
            reason = chain.AttachWindow(hwnd, w, h);
            if (reason != null) return reason;
            EngineLog.Engine(chain.Describe(how, $"shared texture {capacityWidth}x{capacityHeight} (WGL_NV_DX_interop2)"));
            return null;
        }

        /// The LUID of the adapter the GL context renders on: the one the GL driver gives
        /// (EXT_external_objects_win32), else the DXGI adapter GL_VENDOR and GL_RENDERER name
        /// (Dxgi.MatchAdapter). 0, with the reason in `how`, when none matches.
        long FindAdapter(out string how)
        {
            how = null;
            if (DxgiSwapChain.Faulty("adapter"))
            {
                how = "no DXGI adapter matches the OpenGL renderer";
                return 0;
            }
            if (GlPrograms.Extensions.Contains("GL_EXT_memory_object_win32") || GlPrograms.Extensions.Contains("GL_EXT_semaphore_win32"))
            {
                var getUnsignedBytes = (delegate* unmanaged<uint, byte*, void>)Glfw.GetProcAddress("glGetUnsignedBytevEXT"u8);
                long luid = 0;
                const uint DeviceLuid = 0x9599;
                if (getUnsignedBytes != null) getUnsignedBytes(DeviceLuid, (byte*)&luid);
                if (luid != 0 && DxgiHasAdapter(luid))
                {
                    how = "the OpenGL context's LUID";
                    return luid;
                }
            }
            string glVendor = GlApi.GetString(Vendor), glRenderer = GlApi.GetString(RendererName);
            var found = new List<(uint, string, bool)>();
            var luids = new List<long>();
            var iid = Dxgi.IIDFactory2;
            void* factory;
            int hr = Dxgi.CreateDXGIFactory2(0, &iid, &factory);
            if (Dxgi.Failed(hr))
            {
                how = $"CreateDXGIFactory2 failed ({Dxgi.Hr(hr)})";
                return 0;
            }
            for (uint i = 0; ; i++)
            {
                void* a;
                if (Dxgi.Failed(Dxgi.EnumAdapters1(factory, i, &a))) break;
                Dxgi.AdapterDesc1 desc = default;
                bool ok = !Dxgi.Failed(Dxgi.GetDesc1(a, &desc));
                found.Add(ok ? (desc.VendorId, desc.Name, (desc.Flags & Dxgi.AdapterFlagSoftware) != 0) : (0u, "", true));
                luids.Add(desc.Luid);
                Dxgi.Release(ref a);
            }
            Dxgi.Release(ref factory);
            int index = Dxgi.MatchAdapter(glVendor, glRenderer, found);
            if (index < 0 || luids[index] == 0)
            {
                how = $"no DXGI adapter matches the OpenGL renderer \"{glRenderer}\" ({glVendor}); adapters: {string.Join(", ", found.ConvertAll(f => f.Item2))}";
                return 0;
            }
            how = "matched by GL_RENDERER";
            return luids[index];
        }

        /// DXGI has an adapter with this LUID (else the GL driver's LUID is not used: GL_RENDERER
        /// decides).
        static bool DxgiHasAdapter(long luid)
        {
            void* factory = null, factory4 = null, adapter = null;
            try
            {
                var iid = Dxgi.IIDFactory2;
                return !Dxgi.Failed(Dxgi.CreateDXGIFactory2(0, &iid, &factory)) && !Dxgi.Failed(Dxgi.QueryInterface(factory, Dxgi.IIDFactory4, &factory4))
                       && !Dxgi.Failed(Dxgi.EnumAdapterByLuid(factory4, luid, Dxgi.IIDAdapter1, &adapter));
            }
            finally
            {
                Dxgi.Release(ref adapter);
                Dxgi.Release(ref factory4);
                Dxgi.Release(ref factory);
            }
        }

        bool Fail(string reason)
        {
            failure ??= reason;
            return false;
        }

        /// The shared texture, at least `w` x `h`, made anew (registered with GL) when the one
        /// there is smaller.
        bool MakeShared(int w, int h)
        {
            ReleaseShared();
            capacityWidth = capacityWidth == 0 ? w : Dxgi.Capacity(capacityWidth, w);
            capacityHeight = capacityHeight == 0 ? h : Dxgi.Capacity(capacityHeight, h);
            var desc = new Dxgi.Texture2DDesc
            {
                Width = (uint)capacityWidth, Height = (uint)capacityHeight, MipLevels = 1, ArraySize = 1, Format = Dxgi.FormatR8G8B8A8Unorm,
                SampleCount = 1, BindFlags = Dxgi.BindRenderTarget | Dxgi.BindShaderResource,
            };
            void* t;
            int hr = Dxgi.CreateTexture2D(chain.Device, &desc, &t);
            if (Dxgi.Failed(hr)) return Fail($"CreateTexture2D {capacityWidth}x{capacityHeight} failed ({Dxgi.Hr(hr)}{chain.DeviceState(hr)})");
            shared = t;
            texture = GlApi.GenTexture();
            interopObject = DxgiSwapChain.Faulty("register") ? IntPtr.Zero : dxRegisterObject(interopDevice, shared, texture, TexTarget2D, WglAccessReadWrite);
            if (interopObject == IntPtr.Zero) return Fail($"wglDXRegisterObjectNV failed (error 0x{Dxgi.GetLastError():X})");
            if (PresentTrace.Enabled) PresentTrace.Log($"dxgi: shared texture {capacityWidth}x{capacityHeight} for a {w}x{h} picture");
            return true;
        }

        void ReleaseShared()
        {
            if (interopObject != IntPtr.Zero)
            {
                // An object still locked cannot be unregistered (ERROR_BUSY, the one error
                // WGL_NV_DX_interop lists for wglDXUnregisterObjectNV), and an unlock that fails
                // unlocks nothing: it is then left registered (the extension gives no other way to
                // release it), and the interop device is closed regardless (Dispose).
                if (locked && !Unlock())
                    EngineLog.Engine($"present: the shared texture stays locked ({Failure}); it is not unregistered");
                else if (dxUnregisterObject(interopDevice, interopObject) == 0)
                    EngineLog.Engine($"present: wglDXUnregisterObjectNV failed (error 0x{Dxgi.GetLastError():X})");
                interopObject = IntPtr.Zero;
            }
            if (texture != 0)
            {
                GlApi.DeleteTexture(texture);
                texture = 0;
                // The next texture may get the same name: it is attached anew all the same.
                attached = 0;
            }
            Dxgi.Release(ref shared);
        }

        bool Lock()
        {
            var o = interopObject;
            if (DxgiSwapChain.Faulty("lock", begins) || dxLockObjects(interopDevice, 1, &o) == 0) return Fail($"wglDXLockObjectsNV failed (error 0x{Dxgi.GetLastError():X})");
            locked = true;
            return true;
        }

        bool Unlock()
        {
            var o = interopObject;
            // A FALSE return unlocks none of the objects (WGL_NV_DX_interop): it stays locked.
            if (dxUnlockObjects(interopDevice, 1, &o) == 0) return Fail($"wglDXUnlockObjectsNV failed (error 0x{Dxgi.GetLastError():X})");
            locked = false;
            return true;
        }

        /// The shared texture on the framebuffer (bound for drawing), once per texture.
        void Attach()
        {
            if (attached == texture) return;
            if (framebuffer == 0) framebuffer = GlApi.GenFramebuffer();
            GlApi.BindFramebuffer(DrawFramebuffer, framebuffer);
            N.glFramebufferTexture2D(DrawFramebuffer, ColorAttachment0, TexTarget2D, texture, 0);
            attached = texture;
        }

        public bool Begin(int w, int h)
        {
            if (Failure != null) return false;
            begins++;
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (!chain.Resize(w, h)) return false;
            if ((w > capacityWidth || h > capacityHeight) && !MakeShared(w, h)) return false;
            if (!Lock()) return false;
            GlApi.BindFramebuffer(DrawFramebuffer, framebuffer);
            Attach();
            return true;
        }

        public bool Show(int interval, bool again)
        {
            if (Failure != null) return false;
            if (!Unlock()) return false;
            return chain.Present(shared, interval);
        }

        /// Releases everything, in the reverse order of making it (presenter thread, the window's
        /// context current). Safe after a failure at any step, and more than once.
        public void Dispose()
        {
            ReleaseShared();
            if (framebuffer != 0)
            {
                GlApi.DeleteFramebuffer(framebuffer);
                framebuffer = attached = 0;
            }
            if (interopDevice != IntPtr.Zero)
            {
                if (dxCloseDevice(interopDevice) == 0) EngineLog.Engine($"present: wglDXCloseDeviceNV failed (error 0x{Dxgi.GetLastError():X})");
                interopDevice = IntPtr.Zero;
            }
            chain?.Dispose();
            chain = null;
        }
    }
}
