using System.Linq;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using Brutal.OpenGlApi;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using N = Brutal.OpenGlApi.OpenGl.Native;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Gl
{
    internal sealed class GlCaps : GraphicsCaps
    {
        public string Name = "", Vendor = "", VersionString = "";
        public int MaxTexture = 16384, MaxTargets = 8, MemoryMB;
        public bool S3tc, Bptc, Rgtc;

        public override GraphicsDeviceType DeviceType => GraphicsDeviceType.OpenGLCore;
        public override string DeviceName => Name;
        public override string DeviceVendor => Vendor;
        public override string DeviceVersion => "OpenGL " + VersionString;
        public override int DeviceId => 0;
        public override int VendorId => 0;
        public override int MemorySizeMB => MemoryMB;
        public override int ShaderLevel => 50;
        public override int MaxTextureSize => MaxTexture;
        public override int MaxRenderTargets => MaxTargets;
        public override bool Supports2DArrayTextures => true;
        public override bool Supports3DTextures => true;
        public override bool SupportsInstancing => true;
        public override bool SupportsComputeShaders => false;

        public override bool SupportsTexture(TextureFormat format) => format switch
        {
            TextureFormat.DXT1 or TextureFormat.DXT5 => S3tc,
            TextureFormat.BC7 or TextureFormat.BC6H => Bptc,
            TextureFormat.BC4 or TextureFormat.BC5 => Rgtc,
            _ => TextureFormats.CanEncode(format),
        };

        public override bool SupportsRenderTexture(RenderTextureFormat format)
        {
            try
            {
                var g = RenderTextureFormats.ToGraphicsFormat(format, false);
                if (g == UnityEngine.Experimental.Rendering.GraphicsFormat.None) return true;
                GlTextures.InternalOf(g);
                return true;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
    }

    /// Unity's OpenGLCore device: executes the game's GLSL programs with Unity's conventions
    /// (clip space and texture origin as OpenGL, clockwise front faces, no projection flip).
    ///
    /// The device's methods run on the main thread and record; GL work runs on the GL thread
    /// (GlWorker). Recording resolves everything a draw reads from engine objects (programs,
    /// state, uniform values, textures, mesh data) so that the main thread can go on changing
    /// them; methods marked "GL thread" run only inside recorded commands.
    internal sealed unsafe class GlDevice : RenderDevice
    {
        public static float MaxAnisotropy;

        /// Diagnostics (--gl-debug): log the driver's debug output (each distinct message the
        /// first few times, with the frame), from a debug context.
        public static bool DebugOutput;

        /// Diagnostics (--vsync-count N): the swap interval whatever QualitySettings says; -1 for
        /// QualitySettings.vSyncCount.
        public static int SwapIntervalOverride = -1;

        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, int> debugCounts = new();

        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        static void OnDebugMessage(uint source, uint type, uint id, uint severity, int length, byte* message, void* user)
        {
            int n = debugCounts.AddOrUpdate(id, 1, (_, c) => c + 1);
            if (n > 3) return;
            var text = length >= 0 ? System.Text.Encoding.UTF8.GetString(message, length) : new string((sbyte*)message);
            const uint DebugTypeError = 0x824C;
            // Errors come with the GL thread's stack: the command that caused them.
            var where = type == DebugTypeError ? "\n" + Environment.StackTrace : "";
            EngineLog.Engine($"gl debug (frame {UnityEngine.Time.frameCount}, source 0x{source:X}, type 0x{type:X}, id {id}, severity 0x{severity:X}, #{n}): {text}{where}");
        }

        static void EnableDebugOutput()
        {
            const uint DebugOutputSynchronous = 0x8242, DebugOutputState = 0x92E0;
            N.glEnable((GlCapability)DebugOutputState);
            N.glEnable((GlCapability)DebugOutputSynchronous);
            N.glDebugMessageCallback((nint*)(delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, byte*, void*, void>)&OnDebugMessage, null);
            EngineLog.Engine("GL debug output enabled");
        }

        readonly GlfwPlatform platform;
        readonly GlCaps caps = new();
        readonly GlWorker worker;
        readonly Dictionary<string, DeviceTexture> defaults = new();
        CameraConstants camera;
        Material passMaterial;
        int passIndex;
        static readonly GlGetProcAddressFunc loader = name => Glfw.GetProcAddress(new ReadOnlySpan<byte>(name, int.MaxValue)[..new ReadOnlySpan<byte>(name, int.MaxValue).IndexOf((byte)0)]);

        public GlDevice(GlfwPlatform platform)
        {
            this.platform = platform;
            platform.RenderWindow.MakeContextCurrent();
            if (!Glad.Initialize(loader)) throw new InvalidOperationException("OpenGL entry points could not be loaded.");
            caps.Name = GlApi.GetString(RendererName);
            caps.Vendor = GlApi.GetString(Vendor);
            caps.VersionString = GlApi.GetString(GlConst.Version);
            caps.MaxTexture = GlApi.GetInteger(MaxTextureSize);
            caps.MaxTargets = GlApi.GetInteger(MaxDrawBuffers);
            int extensions = GlApi.GetInteger(NumExtensions);
            for (uint i = 0; i < extensions; i++)
            {
                var e = GlApi.GetStringi(Extensions, i);
                GlPrograms.Extensions.Add(e);
                if (e == "GL_EXT_texture_compression_s3tc") caps.S3tc = true;
                if (e == "GL_ARB_texture_compression_bptc") caps.Bptc = true;
                if (e == "GL_ARB_texture_compression_rgtc") caps.Rgtc = true;
                if (e is "GL_EXT_texture_filter_anisotropic" or "GL_ARB_texture_filter_anisotropic")
                {
                    float max = 0f;
                    N.glGetFloatv(0x84FF, &max);
                    MaxAnisotropy = max;
                }
            }
            caps.Rgtc |= true; // core since OpenGL 3.0
            // Core since OpenGL 4.2 (its spec, appendix L.1), where a driver need not list the
            // extension. (S3TC is never core: only its extension says.)
            const uint MajorVersion = 0x821B, MinorVersion = 0x821C;
            int major = GlApi.GetInteger(MajorVersion), minor = GlApi.GetInteger(MinorVersion);
            if (major > 4 || (major == 4 && minor >= 2)) caps.Bptc = true;
            GlTextures.Caps = caps;
            caps.MemoryMB = QueryVideoMemoryMB();
            if (GlPrograms.Extensions.Contains("GL_NVX_gpu_memory_info")) (startEvictions, startEvicted) = NvxEvictions();
            GlBindless.Initialize(caps.Vendor);
            EngineLog.Engine(GlBindless.Enabled ? "textures: bindless (ARB_bindless_texture)" : $"textures: bound to units ({GlBindless.Reason})");
            if (DebugOutput) EnableDebugOutput();
            EngineLog.Engine($"OpenGL: {caps.VersionString} / {caps.Name} / {caps.Vendor}");

            GlVertexArray vao = default;
            N.glGenVertexArrays(1, &vao);
            N.glBindVertexArray(vao);
            immediateVertexArray = vao.Id;
            if (Environment.GetEnvironmentVariable("ONI_BINDLESS_STRESS") != null)
            {
                GlBindlessStress.RunIfAsked();
                if (DebugOutput) EnableDebugOutput();
                N.glBindVertexArray(vao);
            }
            N.glFrontFace((GlFrontFace)Cw);
            immediateBuffer = GlApi.GenBuffer();
            immediateIndices = GlApi.GenBuffer();
            GlBindings.Invalidate();
            CreateDefaults();
            GlStateCache.Invalidate();
            presenter = new GlPresenter(platform.Window, platform.PresentWindow, platform.ContextFreeWindow, Splash != null ? new GlSplash(Splash) : null, platform.WindowFramebufferWidth, platform.WindowFramebufferHeight);
            platform.ContentsDamaged += presenter.Refresh;
            LoadingScreen.Changed = presenter.Poke;
            // From here on the context belongs to the GL thread.
            worker = new GlWorker(platform.RenderWindow, CommandFailed);
            recorder = new GlDrawRecorder(worker, DefaultTexture, RunDraw);
            GlBackgroundUploads.Start(worker);
            GlTextures.FlushUploadBytes = Environment.GetEnvironmentVariable("ONI_UPLOAD_FLUSH") == "0" ? 0 : 32L << 20;
            if (GlAhead.Enabled) GlAhead.Start(worker);
            if (GlWarmup.Enabled) GlWarmup.Start(worker);
            // A frame's GPU time is measured from its first commands, not from the previous
            // present: in between the GL thread waits for the main thread's next frame.
            worker.BatchStarting = () =>
            {
                if (GlAhead.Enabled) GlAhead.BetweenBatches();
                if (!gpuTimingArmed) return;
                gpuTimingArmed = false;
                BeginGpuTiming();
            };
            platform.Closing += () =>
            {
                worker.Dispose();
                presenter.Dispose();
            };
        }

        readonly GlPresenter presenter;

        /// The art of the player's loading splash (--splash moo), loading; null: no splash.
        public static System.Threading.Tasks.Task<OniMoono.Engine.Rendering.SplashArt> Splash;

        /// A recorded command threw (GL thread): the error is logged and the GL thread's caches
        /// of context state are dropped, since the command may have stopped part way.
        void CommandFailed(Exception e)
        {
            System.Threading.Interlocked.Increment(ref failedCommands);
            Debug.LogException(e);
            GlBindings.Invalidate();
            GlStateCache.Invalidate();
            framebufferValid = false;
            Array.Clear(constants);
        }

        long failedCommands;

        public override long FailedCommands => System.Threading.Interlocked.Read(ref failedCommands);

        /// Video memory in MB (SystemInfo.graphicsMemorySize), 0 when the driver reports none:
        /// - the dedicated video memory of NVX_gpu_memory_info (NVIDIA's driver, and Mesa's
        ///   drivers that track memory);
        /// - the RAM of the context's GPU from WGL_AMD_gpu_association or GLX_AMD_gpu_association
        ///   (AMD's drivers);
        /// - GLX_MESA_query_renderer's video memory "available to the renderer" (Mesa; after the
        ///   extensions because on a unified-memory GPU it can include shared memory);
        /// - last, ATI_meminfo's TEXTURE_FREE_MEMORY_ATI. That is the memory free in the texture
        ///   pool, and only a hint (ATI_meminfo, section 6.1.x), not the total: an approximation
        ///   lower by what is already allocated.
        static int QueryVideoMemoryMB()
        {
            if (GlPrograms.Extensions.Contains("GL_NVX_gpu_memory_info"))
            {
                int kb = 0;
                N.glGetIntegerv(0x9047, &kb); // GPU_MEMORY_INFO_DEDICATED_VIDMEM_NVX
                if (kb > 0) return kb / 1024;
            }
            if (AmdGpuRamMB() is > 0 and var amd) return amd;
            // An address from glXGetProcAddress does not mean the function is supported; the
            // extension string says whether it is (GLX 1.4, section 3.3.12).
            if (Glfw.ExtensionSupported("GLX_MESA_query_renderer"))
            {
                const int GlxRendererVideoMemoryMesa = 0x8187;
                var query = (delegate* unmanaged[Cdecl]<int, uint*, int>)Glfw.GetProcAddress("glXQueryCurrentRendererIntegerMESA"u8);
                uint mb = 0;
                if (query != null && query(GlxRendererVideoMemoryMesa, &mb) != 0 && mb > 0) return (int)mb;
            }
            if (GlPrograms.Extensions.Contains("GL_ATI_meminfo"))
            {
                int* info = stackalloc int[4];
                N.glGetIntegerv(0x87FC, info); // TEXTURE_FREE_MEMORY_ATI, param[0]: KB free in the pool
                if (info[0] > 0) return info[0] / 1024;
            }
            return 0;
        }

        /// WGL_GPU_RAM_AMD (GLX_GPU_RAM_AMD) of the GPU the current context is on: "the amount of
        /// RAM available to GPU in MB" (WGL_AMD_gpu_association and GLX_AMD_gpu_association,
        /// wglGetGPUInfoAMD), a property of the GPU rather than of its use. 0 without the
        /// extension or when a query fails (the context's GPU id is 0 then, the info query -1).
        static int AmdGpuRamMB()
        {
            const int GpuRamAmd = 0x21A3;
            const uint UnsignedIntType = 0x1405;
            bool windows = OperatingSystem.IsWindows();
            if (!Glfw.ExtensionSupported(windows ? "WGL_AMD_gpu_association" : "GLX_AMD_gpu_association")) return 0;
            var contextGpu = (delegate* unmanaged<IntPtr, uint>)Glfw.GetProcAddress(windows ? "wglGetContextGPUIDAMD"u8 : "glXGetContextGPUIDAMD"u8);
            var info = (delegate* unmanaged<uint, int, uint, uint, void*, int>)Glfw.GetProcAddress(windows ? "wglGetGPUInfoAMD"u8 : "glXGetGPUInfoAMD"u8);
            // glXGetProcAddress gives core GLX functions too (GLX 1.4, section 3.3.12), from the
            // libGL GLFW loaded; wglGetProcAddress does not, so WGL's comes from opengl32.dll.
            var glxCurrent = windows ? null : (delegate* unmanaged<IntPtr>)Glfw.GetProcAddress("glXGetCurrentContext"u8);
            if (contextGpu == null || info == null || (!windows && glxCurrent == null)) return 0;
            uint id = contextGpu(windows ? WglGetCurrentContext() : glxCurrent());
            if (id == 0) return 0;
            uint mb = 0;
            return info(id, GpuRamAmd, UnsignedIntType, 1, &mb) >= 1 && mb is > 0 and <= int.MaxValue ? (int)mb : 0;
        }

        [DllImport("opengl32.dll", EntryPoint = "wglGetCurrentContext")]
        static extern IntPtr WglGetCurrentContext();

        /// The NVIDIA extension's free dedicated memory and evictions (count and size since the
        /// context was made), or the AMD one's free memory in the texture pool and the auxiliary
        /// (system memory) pool. A driver short of video memory moves allocations to system
        /// memory, after which drawing from them is much slower.
        public override string MemoryCounters()
        {
            var gl = GlMemoryCounters();
            // Windows: the process's own video memory on the GL context's adapter (all of it: the
            // GL driver's allocations and DXGI's), as Windows counts it.
            if (OperatingSystem.IsWindows() && presenter.DxgiAdapterLuid != 0 && Dxgi.ProcessVideoMemory(presenter.DxgiAdapterLuid) is var (local, nonLocal))
                return (gl != null ? gl + "; " : "") + $"process {local >> 20} MiB local, {nonLocal >> 20} MiB non-local";
            return gl;
        }

        string GlMemoryCounters() => worker.TryCall(GlThreadMemoryCounters, out var counters) ? counters : "the OpenGL device has stopped";

        string GlThreadMemoryCounters()
        {
            string textures = $"textures {GlTextures.LiveTextures} ({GlTextures.LiveBytes >> 20} MiB)";
            if (GlBindless.Enabled) textures += ", " + BindlessResidency.TakeCounters();
            var memory = GlThreadVideoMemory();
            textures = (memory != null ? memory + "; " + textures : textures) + "; by kind: " + VideoMemoryLedger.Take();
            // The CPU memory uploads go through (not video memory): readable textures' copies,
            // texels read ahead of their background upload, and all texel memory.
            return textures + $"; upload staging: copies {UploadBuffers.Take()}, background {GlBackgroundUploads.TakeCounters()}, texel memory {PixelMemory.LiveBytes >> 20} MiB (peak {PixelMemory.TakePeak() >> 20})";
        }

        /// The NVIDIA extension's eviction count and evicted KiB when the device started: they
        /// count every eviction "seen by system" (NVX_gpu_memory_info), other processes' and
        /// earlier ones too (14,584 evictions, 49.7 GB, before this player's first frame on an
        /// RTX 3090 with 14 GB free), so the log gives them since the device started as well.
        long startEvictions, startEvicted;

        static (long Count, long KiB) NvxEvictions()
        {
            int evictions = 0, evicted = 0;
            N.glGetIntegerv(0x904A, &evictions); // GPU_MEMORY_INFO_EVICTION_COUNT_NVX
            N.glGetIntegerv(0x904B, &evicted); // GPU_MEMORY_INFO_EVICTED_MEMORY_NVX
            return ((uint)evictions, (uint)evicted);
        }

        string GlThreadVideoMemory()
        {
            if (GlPrograms.Extensions.Contains("GL_NVX_gpu_memory_info"))
            {
                int free = 0;
                N.glGetIntegerv(0x9049, &free); // GPU_MEMORY_INFO_CURRENT_AVAILABLE_VIDMEM_NVX
                var (evictions, evicted) = NvxEvictions();
                return $"{free >> 10} MiB free of {caps.MemoryMB} MiB, {evictions - startEvictions} evictions ({(evicted - startEvicted) >> 10} MiB) since the device started, {evictions} ({evicted >> 10} MiB) seen by the system";
            }
            if (GlPrograms.Extensions.Contains("GL_ATI_meminfo"))
            {
                int* info = stackalloc int[4];
                N.glGetIntegerv(0x87FC, info); // TEXTURE_FREE_MEMORY_ATI
                return $"{info[0] >> 10} MiB free (largest block {info[1] >> 10} MiB), auxiliary {info[2] >> 10} MiB free";
            }
            return null;
        }

        public override GraphicsCaps Caps => caps;
        public override bool RendersFrames => true;

        // ----- Default textures (shader property defaults) -----

        void CreateDefaults()
        {
            foreach (var (name, r, g, b, a) in DefaultTextures.Colors) Make(name, r, g, b, a);
        }

        void Make(string name, byte r, byte g, byte b, byte a)
        {
            var t = new DeviceTexture { Id = GlApi.GenTexture(), Target = TexTarget2D, RecordedTarget = TexTarget2D, Width = 1, Height = 1 };
            byte* px = stackalloc byte[4] { r, g, b, a };
            GlBindings.BindTexture(TexTarget2D, t.Id);
            N.glTexImage2D(TexTarget2D, 0, (int)Rgba8, 1, 1, 0, Rgba, UnsignedByte, px);
            N.glTexParameteri(TexTarget2D, TextureMinFilter, (int)Linear);
            N.glTexParameteri(TexTarget2D, TextureMaxLevel, 0);
            defaults[name] = t;
            // Array and 3D defaults with the same colour.
            var array = new DeviceTexture { Id = GlApi.GenTexture(), Target = TexTarget2DArray, RecordedTarget = TexTarget2DArray };
            GlBindings.BindTexture(TexTarget2DArray, array.Id);
            N.glTexImage3D(TexTarget2DArray, 0, (int)Rgba8, 1, 1, 1, 0, Rgba, UnsignedByte, px);
            N.glTexParameteri(TexTarget2DArray, TextureMaxLevel, 0);
            defaults["array:" + name] = array;
            var volume = new DeviceTexture { Id = GlApi.GenTexture(), Target = TexTarget3D, RecordedTarget = TexTarget3D };
            GlBindings.BindTexture(TexTarget3D, volume.Id);
            N.glTexImage3D(TexTarget3D, 0, (int)Rgba8, 1, 1, 1, 0, Rgba, UnsignedByte, px);
            N.glTexParameteri(TexTarget3D, TextureMaxLevel, 0);
            defaults["3d:" + name] = volume;
            // Cube samplers (Standard-shader reflections, point-light cookies): the textures
            // never give them a cube map, so they read a default cube of the same colour.
            var cube = new DeviceTexture { Id = GlApi.GenTexture(), Target = TexTargetCubeMap, RecordedTarget = TexTargetCubeMap, Width = 1, Height = 1 };
            GlBindings.BindTexture(TexTargetCubeMap, cube.Id);
            for (uint face = 0; face < 6; face++)
                N.glTexImage2D(TexCubeMapPositiveX + face, 0, (int)Rgba8, 1, 1, 0, Rgba, UnsignedByte, px);
            N.glTexParameteri(TexTargetCubeMap, TextureMaxLevel, 0);
            defaults["cube:" + name] = cube;
        }

        DeviceTexture DefaultTexture(string name, uint target)
        {
            name ??= "";
            string prefix = target == TexTarget2DArray ? "array:" : target == TexTarget3D ? "3d:" : target == TexTargetCubeMap ? "cube:" : "";
            return defaults.TryGetValue(prefix + name, out var t) ? t : defaults[prefix + DefaultTextures.Unnamed];
        }

        // ----- Targets -----

        /// A render target as recorded: the device textures (null colours: the back buffer, of
        /// the window's framebuffer size when recorded).
        /// Never written once made: the GL thread only reads it, so one with the same values can
        /// be recorded again (DeviceTexture.BoundAlone, backBufferTarget).
        internal sealed record BoundTarget(DeviceTexture[] Colors, DeviceTexture Depth, int BackBufferWidth, int BackBufferHeight);

        /// The back buffer as the last target recorded for it (main thread).
        BoundTarget backBufferTarget;

        public override void SetRenderTarget(in RenderTargetBinding binding)
        {
            DeviceTexture[] colors = null;
            DeviceTexture depth = null;
            int width = platform.FramebufferWidth, height = platform.FramebufferHeight;
            BoundTarget bound;
            if (binding.Colors is { Length: 1 } single && single[0] != null && (binding.Depth == null || binding.Depth == single[0]))
            {
                // One colour texture, as its own depth or with none (a camera's texture, a
                // blit's destination): the target recorded for it last, when the same.
                var texture = GlTextures.Prepare(worker, single[0]);
                bool withDepth = binding.Depth != null;
                bound = withDepth ? texture.BoundWithDepth : texture.BoundAlone;
                if (bound == null || bound.Colors[0] != texture || bound.BackBufferWidth != width || bound.BackBufferHeight != height)
                {
                    bound = new BoundTarget([texture], withDepth ? texture : null, width, height);
                    if (withDepth) texture.BoundWithDepth = bound;
                    else texture.BoundAlone = bound;
                }
            }
            else if (binding.Colors == null || binding.Colors.Length == 0 || binding.Colors[0] == null)
            {
                bound = backBufferTarget;
                if (bound == null || bound.BackBufferWidth != width || bound.BackBufferHeight != height)
                    backBufferTarget = bound = new BoundTarget(null, null, width, height);
            }
            else
            {
                colors = new DeviceTexture[binding.Colors.Length];
                // A null target after the first (a destroyed buffer) leaves its attachment empty.
                for (int i = 0; i < colors.Length; i++) colors[i] = binding.Colors[i] == null ? null : GlTextures.Prepare(worker, binding.Colors[i]);
                if (binding.Depth != null) depth = binding.Depth == binding.Colors[0] ? colors[0] : GlTextures.Prepare(worker, binding.Depth);
                bound = new BoundTarget(colors, depth, width, height);
            }
            recorder.RenderTexture = bound.Colors != null;
            worker.Post(worker.Arena.Command(setTargetNow ??= SetTargetNow, bound));
        }

        // The target, viewport, scissor and clear work (GL thread), recorded as StateCommands
        // with these delegates, made once.
        Action<BoundTarget> setTargetNow;
        Action<UnityEngine.Rect> setViewportNow;
        Action<Rect?> setScissorNow;
        Action<(bool Color, bool Depth, Color Background, float DepthValue)> clearNow;

        void SetTargetNow(BoundTarget bound)
        {
            long t = GlTimings.Now;
            target = bound;
            framebufferValid = false;
            BindTarget();
            GlTimings.Targets += GlTimings.Now - t;
        }

        // GL thread.
        static bool framebufferValid;
        BoundTarget target = new(null, null, 1, 1);
        /// A framebuffer combining several colour textures (and a depth buffer), with what is
        /// attached to it.
        internal sealed class MrtFramebuffer
        {
            public string Key;
            public uint Name;
            public List<DeviceTexture> Attached = new();
        }

        /// Multi-target framebuffers by the textures' names and the depth buffer's; each texture
        /// lists the ones it is attached to, which go when it is deleted (GL thread).
        static readonly Dictionary<string, MrtFramebuffer> mrtFramebuffers = new();

        public static void InvalidateFramebufferBinding() => framebufferValid = false;

        /// Deletes the multi-target framebuffers a texture is attached to, before the texture's
        /// own names are deleted (they could be reused by new textures under the same key). Each
        /// leaves the lists of all its textures, so a GL name reused later is not taken for it.
        internal static void ForgetFramebuffers(DeviceTexture texture)
        {
            if (texture.MrtFramebuffers == null || texture.MrtFramebuffers.Count == 0) return;
            foreach (var fb in new List<MrtFramebuffer>(texture.MrtFramebuffers))
            {
                mrtFramebuffers.Remove(fb.Key);
                GlApi.DeleteFramebuffer(fb.Name);
                foreach (var t in fb.Attached) t.MrtFramebuffers?.Remove(fb);
                OniMoono.Engine.EngineLog.Engine($"multi-target framebuffer deleted with its textures; {mrtFramebuffers.Count} kept");
            }
            framebufferValid = false;
        }

        /// The framebuffer standing in for the back buffer this frame (GL thread).
        uint BackBuffer => presenter.BackBuffer(target.BackBufferWidth, target.BackBufferHeight);

        /// Binds the current target's framebuffer (GL thread).
        void BindTarget()
        {
            if (framebufferValid) return;
            framebufferValid = true;
            var t = target;
            if (t.Colors == null)
            {
                GlApi.BindFramebuffer(Framebuffer, BackBuffer);
                return;
            }
            if (t.Colors.Length == 1 && (t.Depth == null || t.Depth == t.Colors[0]))
            {
                t.Colors[0].Dirty = true;
                GlApi.BindFramebuffer(Framebuffer, GlTextures.RenderFramebuffer(t.Colors[0]));
                return;
            }
            // Multiple render targets: a framebuffer combining the colour textures with the depth
            // buffer of the depth target.
            var key = new System.Text.StringBuilder();
            foreach (var c in t.Colors)
            {
                if (c != null) c.Dirty = true;
                key.Append(c?.Id ?? 0).Append(',');
            }
            key.Append(t.Depth?.DepthRenderbuffer ?? 0);
            if (!mrtFramebuffers.TryGetValue(key.ToString(), out var mrt))
            {
                mrt = new MrtFramebuffer { Key = key.ToString(), Name = GlApi.GenFramebuffer() };
                uint fb = mrt.Name;
                GlApi.BindFramebuffer(Framebuffer, fb);
                var buffers = stackalloc GlEnum[t.Colors.Length];
                for (int i = 0; i < t.Colors.Length; i++)
                {
                    if (t.Colors[i] == null)
                    {
                        buffers[i] = NoneBuffer;
                        continue;
                    }
                    N.glFramebufferTexture2D(Framebuffer, ColorAttachment0 + (uint)i, TexTarget2D, t.Colors[i].Id, 0);
                    buffers[i] = ColorAttachment0 + (uint)i;
                    if (!mrt.Attached.Contains(t.Colors[i])) mrt.Attached.Add(t.Colors[i]);
                }
                N.glDrawBuffers(t.Colors.Length, buffers);
                if (t.Depth is { DepthRenderbuffer: not 0 })
                    N.glFramebufferRenderbuffer(Framebuffer, DepthStencilAttachment, Renderbuffer, new GlRenderbuffer(t.Depth.DepthRenderbuffer));
                if (t.Depth != null && !mrt.Attached.Contains(t.Depth)) mrt.Attached.Add(t.Depth);
                foreach (var a in mrt.Attached) (a.MrtFramebuffers ??= new()).Add(mrt);
                mrtFramebuffers[mrt.Key] = mrt;
                OniMoono.Engine.EngineLog.Engine($"multi-target framebuffer for {t.Colors.Length} colour targets made; {mrtFramebuffers.Count} kept");
            }
            GlApi.BindFramebuffer(Framebuffer, mrt.Name);
        }

        public override void SetViewport(UnityEngine.Rect pixels) => worker.Post(worker.Arena.Command(setViewportNow ??= SetViewportNow, pixels));

        void SetViewportNow(UnityEngine.Rect pixels)
        {
            BindTarget();
            N.glViewport((int)pixels.x, (int)pixels.y, (int)pixels.width, (int)pixels.height);
        }

        /// The scissor rectangle in effect (CommandBuffer.EnableScissorRect), or null (GL thread).
        static Rect? scissor;

        public static bool ScissorActive => scissor != null;

        public override void SetScissor(Rect? pixels) => worker.Post(worker.Arena.Command(setScissorNow ??= SetScissorNow, pixels));

        static void SetScissorNow(Rect? pixels)
        {
            scissor = pixels;
            if (pixels is { } r)
            {
                GlApi.Enable(ScissorTest, true);
                N.glScissor((int)r.x, (int)r.y, Math.Max(0, (int)r.width), Math.Max(0, (int)r.height));
            }
            else GlApi.Enable(ScissorTest, false);
        }

        public override void Clear(bool color, bool depth, Color backgroundColor, float depthValue)
            => worker.Post(worker.Arena.Command(clearNow ??= ClearNow, (color, depth, backgroundColor, depthValue)));

        void ClearNow((bool Color, bool Depth, Color Background, float DepthValue) clear)
        {
            var (color, depth, backgroundColor, depthValue) = clear;
            uint q = GlGpuProfile.Enabled ? GlGpuProfile.Begin() : 0;
            BindTarget();
            // Clears fill the whole target.
            if (scissor != null) GlApi.Enable(ScissorTest, false);
            uint mask = 0;
            if (color)
            {
                N.glColorMask(true, true, true, true);
                N.glClearColor(backgroundColor.r, backgroundColor.g, backgroundColor.b, backgroundColor.a);
                mask |= ColorBufferBit;
            }
            if (depth)
            {
                N.glDepthMask(true);
                N.glStencilMask(0xFF);
                N.glClearDepth(depthValue);
                N.glClearStencil(0);
                mask |= DepthBufferBit | StencilBufferBit;
            }
            if (mask != 0) N.glClear(mask);
            if (color && target.Colors == null) presenter.Drew(clear: scissor == null);
            if (scissor != null) GlApi.Enable(ScissorTest, true);
            GlStateCache.Invalidate();
            if (GlGpuProfile.Enabled) GlGpuProfile.End("clear", q);
        }

        protected override void ApplyCamera(in CameraConstants c)
        {
            camera = c;
            recorder.Camera = c;
        }

        // ----- Drawing -----

        /// Records draws (main thread); its commands run ExecuteDraw.
        readonly GlDrawRecorder recorder;

        public override void Draw(in DrawRequest draw)
        {
            recorder.Draw(draw);
            GlTextures.FlushUploads(worker);
        }

        public override void DrawQueue(List<DrawItem> items)
        {
            recorder.DrawQueue(items);
            GlTextures.FlushUploads(worker);
        }

        void RunDraw(ProgramInputs r)
        {
            long t = GlTimings.Now;
            uint q = GlGpuProfile.Enabled ? GlGpuProfile.Begin() : 0;
            ExecuteDraw(r, r.Mesh, r.Layout, r.SubMesh);
            if (GlGpuProfile.Enabled) GlGpuProfile.End(r.Label, q);
            GlTimings.Draws += GlTimings.Now - t;
        }

        // GL thread.
        readonly uint immediateBuffer, immediateIndices;
        /// The vertex array immediate geometry is drawn with (mesh draws use the mesh's own).
        readonly uint immediateVertexArray;
        readonly bool[] attributeEnabled = new bool[16];
        /// Every draw's uniform blocks are written to a stream buffer and bound by range.
        const int UniformStreamSize = 16 << 20;
        GlStreamBuffer uniformStream;

        void BindUniformBlock(uint index, ReadOnlySpan<byte> data)
        {
            uniformStream ??= new GlStreamBuffer(UniformBuffer, UniformStreamSize, GlApi.GetInteger(UniformBufferOffsetAlignment));
            int offset = uniformStream.Write(data);
            N.glBindBufferRange(UniformBuffer, index, new GlBuffer(uniformStream.Id), offset, data.Length);
        }

        /// Draws skipped for a mesh with no index buffer (GL thread).
        long unuploadedDraws;

        void ExecuteDraw(ProgramInputs recorded, DeviceMesh mesh, MeshLayout layout, (int Offset, int Count, uint Mode, int BaseVertex) sm)
        {
            BindTarget();
            GlStateCache.Apply(recorded.State);
            GlStateCache.Outputs(recorded.Program.Outputs, target.Colors?.Length ?? 1);
            Bind(recorded);
            if (sm.Count == 0) return;
            // A mesh with no index buffer has had no upload run (or was deleted): its draw would
            // read the indices from client memory at the offset, address 0 on, and NVIDIA's
            // driver faults there; its vertex array would also keep the missing index buffer.
            if (mesh.IndexBuffer == 0)
            {
                if (unuploadedDraws++ == 0) EngineLog.Engine($"gl: a draw of a mesh with no index buffer was skipped ({recorded.Label ?? recorded.Program.Id.ToString()}; logged once)");
                return;
            }
            var program = recorded.Program;
            GlBindings.BindVertexArray(GlMeshes.VertexArray(mesh, layout, program));
            SetAbsentChannels(program, layout);
            draws++;
            if (target.Colors == null) presenter.Drew(clear: false);
            N.glDrawElementsBaseVertex(sm.Mode, sm.Count, UnsignedInt, (void*)(sm.Offset * 4L), sm.BaseVertex);
            ForgetConstantsOfArrays(program, layout);
        }

        /// Program, uniforms and textures of a recorded draw (GL thread).
        void Bind(ProgramInputs recorded)
        {
            var program = recorded.Program;
            GlBindings.UseProgram(program.Id);
            GlUniforms.Send(program, recorded.Values.AsSpan(recorded.ValuesOffset, program.LooseLength));
            int at = recorded.BlocksOffset;
            for (int i = 0; i < program.BlockSizes.Length; i++)
            {
                int size = Math.Max(program.BlockSizes[i], 16);
                BindUniformBlock((uint)i, recorded.Blocks.AsSpan(at, size));
                at += size;
            }
            // Render textures are made ready for sampling first (MSAA resolve, mip generation):
            // that work binds framebuffers and textures of its own.
            for (int i = 0; i < recorded.TextureCount; i++)
            {
                var t = recorded.Textures[i];
                if (t.RenderTexture) GlTextures.PrepareForSampling(t.Texture, t.AutoGenerateMips);
            }
            if (GlBindless.Enabled)
            {
                var handles = program.SamplerHandles ??= new ulong[program.Samplers.Count];
                for (int i = 0; i < recorded.TextureCount; i++)
                {
                    // A sampler of a target the device has no textures of reads its unit (where
                    // nothing of that target is bound) rather than a handle of another target,
                    // whose results are undefined (ARB_bindless_texture, section 2.14.6).
                    if (!GlPrograms.HasTextures(program.Samplers[i].Target)) continue;
                    var texture = recorded.Textures[i].Texture;
                    ulong h = GlBindless.Handle(texture, out bool bind);
                    // No handle (an incomplete texture): the default the sampler reads unbound.
                    if (h == 0 && !bind)
                    {
                        texture = DefaultTexture(DefaultTextures.Unbound, texture.Target);
                        h = GlBindless.Handle(texture, out bind);
                    }
                    if (BindlessVerify.Enabled) BindlessVerify.Sampler(program, i, texture, h, bind);
                    if (bind)
                    {
                        // Not resident: sampled from the sampler's unit. The uniform now holds the
                        // unit, recorded as 0 (never a handle), so the next handle sets it again.
                        handles[i] = 0;
                        GlBindless.BindToUnit(program.Id, program.Samplers[i].Location, program.Samplers[i].Unit, texture);
                        continue;
                    }
                    if (h == 0 || handles[i] == h) continue;
                    handles[i] = h;
                    GlBindless.SetSampler(program.Id, program.Samplers[i].Location, h);
                }
                if (BindlessVerify.Enabled) BindlessVerify.Drawn();
            }
            else
                for (int i = 0; i < recorded.TextureCount; i++)
                {
                    var t = recorded.Textures[i].Texture;
                    GlBindings.BindTexture(program.Samplers[i].Unit, t.Target, t.Id);
                }
            // Drawing goes to the current target even if preparing a texture rebound it.
            BindTarget();
        }

        void SetAttribute(int location, bool enabled)
        {
            if (location < 0 || location >= attributeEnabled.Length || attributeEnabled[location] == enabled) return;
            attributeEnabled[location] = enabled;
            if (enabled) N.glEnableVertexAttribArray((uint)location);
            else N.glDisableVertexAttribArray((uint)location);
        }

        /// Channels a mesh lacks read Unity's defaults (white colour, position (0,0,0,1), zero
        /// normal/tangent/uv) from the context's constant attribute values, which the vertex
        /// array leaves disabled.
        void SetAbsentChannels(DeviceProgram program, MeshLayout layout)
        {
            for (int c = 0; c < (int)Channel.Count; c++)
            {
                int location = program.Attributes[c];
                if (location < 0 || layout.Components[c] != 0) continue;
                SetConstant(location, c == (int)Channel.Color ? ConstantWhite : c == (int)Channel.Position ? ConstantPoint : ConstantZero);
            }
        }

        /// The constants of the attributes a draw read from enabled arrays are set again before
        /// their next use. OpenGL leaves them as they were: "If an array is enabled, the
        /// corresponding current vertex attribute value is unaffected by the execution of"
        /// the draw (OpenGL 4.5 section 10.4). Observed otherwise: with them cached across such
        /// draws, the background planes' colour, a constant attribute, washed out to white on
        /// long runs of Regrettable Dimension, on the Linux lane's Mesa d3d12 driver then, and
        /// setting them again ended it (871f6e2b, PARITY.md).
        void ForgetConstantsOfArrays(DeviceProgram program, MeshLayout layout)
        {
            for (int c = 0; c < (int)Channel.Count; c++)
            {
                int location = program.Attributes[c];
                if (location >= 0 && location < constants.Length && layout.Components[c] != 0) constants[location] = 0;
            }
        }

        const byte ConstantZero = 1, ConstantWhite = 2, ConstantPoint = 3;
        /// The constant value each attribute location holds (0: unknown).
        readonly byte[] constants = new byte[16];

        void SetConstant(int location, byte value)
        {
            if (location < constants.Length && constants[location] == value) return;
            if (location < constants.Length) constants[location] = value;
            switch (value)
            {
                case ConstantWhite: N.glVertexAttrib4f((uint)location, 1f, 1f, 1f, 1f); break;
                case ConstantPoint: N.glVertexAttrib4f((uint)location, 0f, 0f, 0f, 1f); break;
                default: N.glVertexAttrib4f((uint)location, 0f, 0f, 0f, 0f); break;
            }
        }

        public override bool SetPass(Material material, int pass)
        {
            passMaterial = material;
            passIndex = pass;
            return true;
        }

        /// GL immediate geometry with the pass set by SetPass: the model-view matrix acts as
        /// the object transform under an identity view.
        public override void DrawImmediate(ImmediatePrimitive primitive, ReadOnlySpan<ImmediateVertex> vertices, Matrix4x4 modelView, Matrix4x4 projection)
        {
            if (vertices.Length == 0) return;
            var recorded = RecordPass(modelView, Matrix4x4.identity, projection, out var shader);
            if (recorded == null) return;

            // Interleaved: position, colour, eight texture coordinates; in the frame's arena,
            // which the GL thread reads until the frame has run.
            const int stride = sizeof(float) * (3 + 4 + 32);
            int length = vertices.Length * (stride / 4);
            var (array, at) = worker.Arena.Floats(length);
            var data = array.AsSpan(at, length);
            for (int i = 0; i < vertices.Length; i++)
            {
                ref readonly var v = ref vertices[i];
                int o = i * (stride / 4);
                data[o] = v.Position.x; data[o + 1] = v.Position.y; data[o + 2] = v.Position.z;
                data[o + 3] = v.Color.r; data[o + 4] = v.Color.g; data[o + 5] = v.Color.b; data[o + 6] = v.Color.a;
                Uv(data, o + 7, v.Uv0);
                Uv(data, o + 11, v.Uv1);
                Uv(data, o + 15, v.Uv2);
                Uv(data, o + 19, v.Uv3);
                Uv(data, o + 23, v.Uv4);
                Uv(data, o + 27, v.Uv5);
                Uv(data, o + 31, v.Uv6);
                Uv(data, o + 35, v.Uv7);
            }
            int count = vertices.Length;
            if (GlGpuProfile.Enabled) recorded.Label = "immediate " + shader.ShaderName;
            worker.Post(worker.Arena.Command(immediateNow ??= ImmediateNow, (recorded, primitive, array, at, length, count)));
            GlTextures.FlushUploads(worker);
        }

        // The immediate draw's work (GL thread), recorded as a StateCommand with this delegate, made once.
        Action<(ProgramInputs Recorded, ImmediatePrimitive Primitive, float[] Array, int At, int Length, int Count)> immediateNow;

        void ImmediateNow((ProgramInputs Recorded, ImmediatePrimitive Primitive, float[] Array, int At, int Length, int Count) d)
        {
            uint q = GlGpuProfile.Enabled ? GlGpuProfile.Begin() : 0;
            ExecuteImmediate(d.Recorded, d.Primitive, d.Array.AsSpan(d.At, d.Length), d.Count);
            if (GlGpuProfile.Enabled) GlGpuProfile.End(d.Recorded.Label, q);
        }

        static void Uv(Span<float> data, int o, in Vector4 uv)
        {
            data[o] = uv.x;
            data[o + 1] = uv.y;
            data[o + 2] = uv.z;
            data[o + 3] = uv.w;
        }

        /// The quads' triangle indices (GL thread): grown, never shrunk; entries depend only on
        /// their place, so a longer array's first ones are the same.
        int[] quadIndices = [];

        ReadOnlySpan<int> QuadIndices(int quads)
        {
            if (quadIndices.Length < quads * 6)
            {
                var indices = new int[Math.Max(quads * 6, quadIndices.Length * 2)];
                for (int q = 0; q < indices.Length / 6; q++)
                {
                    int b = q * 4;
                    indices.AsSpan(q * 6, 6).SetValues(b, b + 1, b + 2, b, b + 2, b + 3);
                }
                quadIndices = indices;
            }
            return quadIndices.AsSpan(0, quads * 6);
        }

        /// Records the pass set by SetPass for one immediate draw. Null when the pass has nothing
        /// to draw with (no shader, no program for its keywords).
        ProgramInputs RecordPass(Matrix4x4 objectToWorld, Matrix4x4 view, Matrix4x4 projection, out ShaderNative shader)
        {
            shader = null;
            if (passMaterial == null) return null;
            var material = (MaterialNative)passMaterial.NativeOrThrow;
            if (material.Shader == null) return null;
            shader = (ShaderNative)material.Shader.NativeOrThrow;
            var sub = shader.ActiveSubShader;
            if (sub == null || passIndex >= sub.Passes.Length) return null;
            var pass = sub.Passes[passIndex];
            int blob = GlDrawRecorder.Variant(material, shader, pass, null);
            if (blob < 0) return null;
            var program = recorder.Program(shader, blob);
            if (program.Id == 0) return null;
            // One instance (main thread): recording copies out what it reads.
            var inputs = immediateInputs;
            inputs.ObjectToWorld = objectToWorld;
            inputs.Camera = camera with { View = view, Projection = projection };
            inputs.RenderTexture = recorder.RenderTexture;
            inputs.Block = null;
            inputs.Material = material;
            inputs.Shader = shader;
            try
            {
                return recorder.Record(program, recorder.StateOf(pass), inputs, worker.Arena);
            }
            finally
            {
                inputs.Material = null;
                inputs.Shader = null;
            }
        }

        /// Graphics.DrawMeshNow: the mesh's sub-mesh (-1: each in turn) with the pass set by
        /// SetPass, drawn as a mesh draw is.
        public override void DrawMeshNow(MeshNative mesh, int subMesh, Matrix4x4 objectToWorld, Matrix4x4 view, Matrix4x4 projection)
        {
            int count = mesh.SubMeshes.Count;
            if (count == 0 || subMesh >= count) return;
            int first = subMesh < 0 ? 0 : subMesh, last = subMesh < 0 ? count - 1 : subMesh;
            var device = GlMeshes.Prepare(worker, mesh);
            for (int s = first; s <= last; s++)
            {
                var recorded = RecordPass(objectToWorld, view, projection, out var shader);
                if (recorded == null) return;
                if (GlGpuProfile.Enabled) recorded.Label = "mesh now " + shader.ShaderName;
                recorded.Mesh = device;
                recorded.Layout = device.Layout;
                recorded.SubMesh = device.SubMeshes[s];
                worker.Post(recorded.RunDraw);
            }
            GlTextures.FlushUploads(worker);
        }

        readonly DrawInputs immediateInputs = new();

        void ExecuteImmediate(ProgramInputs recorded, ImmediatePrimitive primitive, ReadOnlySpan<float> data, int count)
        {
            const int stride = sizeof(float) * (3 + 4 + 32);
            var program = recorded.Program;
            BindTarget();
            if (target.Colors == null) presenter.Drew(clear: false);
            GlStateCache.Apply(recorded.State);
            GlStateCache.Outputs(recorded.Program.Outputs, target.Colors?.Length ?? 1);
            Bind(recorded);
            GlBindings.BindVertexArray(immediateVertexArray);
            GlBindings.BindBuffer(ArrayBuffer, immediateBuffer);
            GlApi.BufferData<float>(ArrayBuffer, data, StreamDraw);
            for (int c = 0; c < (int)Channel.Count; c++)
            {
                int location = program.Attributes[c];
                if (location < 0) continue;
                if (c == (int)Channel.Normal || c == (int)Channel.Tangent)
                {
                    SetAttribute(location, false);
                    SetConstant(location, ConstantZero);
                    continue;
                }
                SetAttribute(location, true);
                int offset = c == (int)Channel.Position ? 0 : c == (int)Channel.Color ? 12 : 28 + (c - (int)Channel.Uv0) * 16;
                int size = c == (int)Channel.Position ? 3 : 4;
                N.glVertexAttribPointer((uint)location, size, Float, false, stride, (void*)offset);
            }
            if (primitive == ImmediatePrimitive.Quads)
            {
                var indices = QuadIndices(count / 4);
                GlBindings.BindBuffer(ElementArrayBuffer, immediateIndices);
                GlApi.BufferData<int>(ElementArrayBuffer, indices, StreamDraw);
                draws++;
                N.glDrawElements(Triangles, indices.Length, UnsignedInt, null);
            }
            else
            {
                uint mode = primitive switch
                {
                    ImmediatePrimitive.Lines => Lines,
                    ImmediatePrimitive.LineStrip => LineStrip,
                    ImmediatePrimitive.TriangleStrip => TriangleStrip,
                    _ => Triangles,
                };
                draws++;
                N.glDrawArrays(mode, 0, count);
            }
            Array.Clear(constants);
        }

        // ----- Read-back, resources, presentation -----

        /// Rows a read-back takes at once: about a million pixels (16 MB of floats), so a large
        /// read (a super-sized capture's) needs no buffer of its size.
        const int ReadBandPixels = 1 << 20;

        /// Waits for the GL thread to render everything recorded so far, then reads, a band of
        /// rows at a time. The band is native memory, freed after the read: a read of the screen
        /// (the Timelapser's each day, 3.7 MB at 1280x720) hands the managed heap nothing. Once
        /// the device has stopped, a read that did not run is not known to be past its band, so
        /// that band is left allocated.
        public override void ReadPixels(RenderTextureNative source, RectInt rect, Texture2DNative destination, int destX, int destY)
        {
            int w = Math.Min(rect.width, destination.Width - destX), h = Math.Min(rect.height, destination.Height - destY);
            if (w <= 0 || h <= 0 || !TextureFormats.CanEncode(destination.Format) || destination.Memory == null) return;
            // An 8-bit target into an 8-bit texture: read as bytes, a quarter of the floats'
            // memory and no conversion; the texels are the same. (The present trace logs the
            // screen's reads from the float path.)
            if (source == null ? !PresentTrace.Enabled : GlReadback.EightBit(source))
                if (TextureFormats.ReadPixelsBytes(destination.Format))
                {
                    int byteBand = Math.Clamp(ReadBandPixels / w, 1, h);
                    long bandBytes = (long)w * byteBand * 4;
                    var bytes = (byte*)NativeMemory.Alloc((nuint)bandBytes);
                    bool ran = true;
                    try
                    {
                        for (int y0 = 0; y0 < h && ran; y0 += byteBand)
                        {
                            int rows = Math.Min(byteBand, h - y0);
                            ran = ReadRgba8(source, new RectInt(rect.x, rect.y + y0, w, rows), null, (nint)bytes, w * 4);
                            if (ran)
                                TextureFormats.StoreRgba8(destination.Format, destination.Memory.Slice(destination.MipOffset(0), destination.MipSize(0)), destination.Width,
                                    destX, destY + y0, new ReadOnlySpan<byte>(bytes, w * rows * 4), w, rows);
                        }
                    }
                    finally
                    {
                        if (ran) NativeMemory.Free(bytes);
                    }
                    return;
                }
            var device = source != null ? GlTextures.Prepare(worker, source) : null;
            bool autoMips = source?.AutoGenerateMips ?? false;
            int fbWidth = platform.FramebufferWidth, fbHeight = platform.FramebufferHeight;
            int band = Math.Clamp(ReadBandPixels / w, 1, h);
            var read = (float*)NativeMemory.Alloc((nuint)((long)w * band * 4), sizeof(float));
            nint at = (nint)read;
            bool stopped = false;
            try
            {
                for (int y0 = 0; y0 < h; y0 += band)
                {
                    int rows = Math.Min(band, h - y0);
                    int bandY = y0;
                    if (!worker.TryCall(() =>
                    {
                        uint framebuffer;
                        if (device != null)
                        {
                            GlTextures.PrepareForSampling(device, autoMips);
                            framebuffer = device.Framebuffer;
                        }
                        else framebuffer = presenter.ReadBuffer(fbWidth, fbHeight);
                        GlApi.BindFramebuffer(ReadFramebuffer, framebuffer);
                        N.glPixelStorei(PackAlignment, 4);
                        var into = (float*)at;
                        N.glReadPixels(rect.x, rect.y + bandY, w, rows, Rgba, Float, into);
                        if (PresentTrace.Enabled && device == null)
                        {
                            double sum = 0;
                            for (int i = 0; i < w * rows * 4; i += 4) sum += 0.299 * into[i] + 0.587 * into[i + 1] + 0.114 * into[i + 2];
                            PresentTrace.Log($"frame {Time.frameCount}: ReadPixels of the screen {rect} rows {bandY}+{rows} from framebuffer {framebuffer}: mean luminance {255 * sum / (w * rows):F1}");
                        }
                        framebufferValid = false;
                        return 0;
                    }, out _))
                    {
                        // The window has closed: the destination keeps its texels.
                        if (!loggedStoppedRead) EngineLog.Engine("ReadPixels after the OpenGL device stopped: nothing read (logged once)");
                        loggedStoppedRead = true;
                        stopped = true;
                        return;
                    }
                    var mip = destination.Memory.Slice(destination.MipOffset(0), destination.MipSize(0));
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int o = (y * w + x) * 4;
                            TextureFormats.Encode(destination.Format, mip, destination.Width, destX + x, destY + y0 + y,
                                new Color(read[o], read[o + 1], read[o + 2], read[o + 3]));
                        }
                }
            }
            finally
            {
                if (!stopped) NativeMemory.Free(read);
            }
        }

        /// AsyncGPUReadback: the read goes with the frame's work, into a pixel buffer the GPU
        /// fills behind a fence (GlReadback); the main thread does not wait for the GPU.
        public override DeviceReadback BeginReadback(RenderTextureNative source, int width, int height, TextureFormat format)
        {
            if (!TextureFormats.CanEncode(format)) return null;
            var device = GlTextures.Prepare(worker, source);
            bool autoMips = source.AutoGenerateMips;
            var read = new GlReadback(worker, width, height, format, GlReadback.ReadsBytes(source, format));
            worker.Post(() => read.Start(device, autoMips));
            return read;
        }

        /// Reads straight into the array, as GL_UNSIGNED_BYTE: a capture's band needs no float
        /// copy and no texture.
        public override void ReadPixelsRgba8(RenderTextureNative source, RectInt rect, byte[] destination, int offset, int stride)
        {
            if (rect.width <= 0 || rect.height <= 0) return;
            if (stride % 4 != 0 || offset < 0 || (long)offset + (long)stride * (rect.height - 1) + rect.width * 4L > destination.Length)
                throw new ArgumentOutOfRangeException(nameof(stride));
            ReadRgba8(source, rect, destination, offset, stride);
        }

        /// ReadPixelsRgba8 into `array` from `offset`, or with no array into native memory at
        /// `offset` (an address); false once the device has stopped (nothing read).
        bool ReadRgba8(RenderTextureNative source, RectInt rect, byte[] array, nint offset, int stride)
        {
            if (rect.width <= 0 || rect.height <= 0) return true;
            var device = source != null ? GlTextures.Prepare(worker, source) : null;
            bool autoMips = source?.AutoGenerateMips ?? false;
            int fbWidth = platform.FramebufferWidth, fbHeight = platform.FramebufferHeight;
            if (worker.TryCall(() =>
            {
                uint framebuffer;
                if (device != null)
                {
                    GlTextures.PrepareForSampling(device, autoMips);
                    framebuffer = device.Framebuffer;
                }
                else framebuffer = presenter.ReadBuffer(fbWidth, fbHeight);
                GlApi.BindFramebuffer(ReadFramebuffer, framebuffer);
                N.glPixelStorei(PackAlignment, 4);
                N.glPixelStorei(PackRowLength, stride / 4);
                if (array == null) N.glReadPixels(rect.x, rect.y, rect.width, rect.height, Rgba, UnsignedByte, (void*)offset);
                else fixed (byte* p = array) N.glReadPixels(rect.x, rect.y, rect.width, rect.height, Rgba, UnsignedByte, p + offset);
                N.glPixelStorei(PackRowLength, 0);
                framebufferValid = false;
                return 0;
            }, out _)) return true;
            if (!loggedStoppedRead) EngineLog.Engine("ReadPixels after the OpenGL device stopped: nothing read (logged once)");
            loggedStoppedRead = true;
            return false;
        }

        bool loggedStoppedRead;

        public override void Upload(TextureNative texture)
        {
            GlTextures.Prepare(worker, texture);
            GlTextures.FlushUploads(worker);
        }

        public override void PrepareAhead(TextureNative texture)
        {
            if (!GlAhead.Enabled || texture is not Texture2DNative t || t.Gpu != null || t.Width <= 0 || t.Height <= 0 || t.Placeholder) return;
            if (t.SliceSize > GlAhead.MaxTextureBytes) return;
            var g = new DeviceTexture();
            t.Gpu = g;
            GraphicsDevice.Adopt(t, g);
            var (format, width, height, levels) = (t.Format, t.Width, t.Height, Math.Max(1, t.MipCount));
            GlAhead.Enqueue(() => GlTextures.SpecifyAhead(g, format, width, height, levels));
            // Now: a texture made during a long frame (a load's) is made while the frame goes on.
            GlAhead.Wake();
        }

        public override void PrepareAhead(MeshNative mesh)
        {
            if (GlAhead.Enabled) GlMeshes.PrepareAhead(worker, mesh);
        }

        public override void AheadQueued() => GlAhead.Wake();

        /// The first upload of a texture whose storage was made ahead (GlAhead) is recorded when
        /// the script applies it, not at the first draw: a view's first sight of much (the
        /// animation batches a colony preview shows, whose data the game applies in the frame's
        /// late update) then uploads while the main thread goes on with the frame. Later uploads
        /// wait for a draw, as before.
        public override void Applied(TextureNative texture)
        {
            if (texture.Gpu is not DeviceTexture g || g.RecordedContent >= 0 || g.Background != null || g.RecordedShape != null) return;
            GlTextures.Prepare(worker, texture);
            GlTextures.FlushUploads(worker);
        }

        public override void ReleaseResource(object gpu)
        {
            switch (gpu)
            {
                case DeviceTexture t:
                    if (t.Background != null) GlBackgroundUploads.Claim(t, null);
                    worker.Post(t.Release);
                    break;
                case DeviceMesh m: worker.Post(m.Delete); break;
                case DeviceProgramSet p: worker.Post(p.Delete); break;
            }
        }

        /// Compiles every variant of every loaded shader: on the GL thread while it is idle, the
        /// main thread going on (GlWarmup); with ONI_WARMUP_AHEAD=0, or `wait`, the main thread
        /// waits for them all.
        public override void WarmupShaders() => WarmupShaders(wait: !GlWarmup.Enabled);

        internal void WarmupShaders(bool wait)
        {
            var variants = new List<(ShaderNative, int)>();
            foreach (var native in ObjectTable.Snapshot())
                if (native is ShaderNative shader && shader.HasOpenGLCorePrograms)
                {
                    // Loaded here: the GL thread reads them while the main thread goes on.
                    if (shader.Programs == null) continue;
                    foreach (var sub in shader.SubShaders)
                        foreach (var pass in sub.Passes)
                        {
                            if (pass.Type != 0) continue;
                            foreach (var variant in pass.Variants)
                                if (!StereoOnly(variant.Keywords)) variants.Add((shader, variant.BlobIndex));
                        }
                }
            if (!wait)
            {
                GlWarmup.Queue(variants);
                return;
            }
            worker.Call(() =>
            {
                foreach (var (shader, blob) in variants) GlPrograms.Get(shader, blob);
            });
        }

        /// A variant for XR stereo rendering, which only an XR session selects (never this player).
        /// The warm-up leaves them out: AMD's Windows driver lists GL_AMD_vertex_shader_layer, which
        /// STEREO_INSTANCING_ON requires, but its compiler rejects it, and the failure was logged
        /// as an error on every start (issue #20). Should one be selected, it compiles then.
        internal static bool StereoOnly(IEnumerable<string> keywords)
        {
            foreach (var k in keywords)
                if (k is "STEREO_INSTANCING_ON" or "STEREO_MULTIVIEW_ON" or "UNITY_SINGLE_PASS_STEREO") return true;
            return false;
        }

        public override void FlushRecorded() => worker.Submit();

        /// After the commands that render the loading screen's kanim frames: a fence, and the
        /// frames to the presenter (LoadingScreen.RenderKanim).
        public override bool AnimatesLoading => true;

        public override void HandOverLoadingFrames(LoadingScreen.KanimFrames frames)
        {
            var strip = frames.Strip.Gpu as DeviceTexture;
            if (strip == null)
            {
                frames.Done = true;
                return;
            }
            worker.Post(() =>
            {
                if (strip.Id == 0 || strip.Deleted)
                {
                    frames.Done = true;
                    return;
                }
                var fence = N.glFenceSync(0x9117 /* SYNC_GPU_COMMANDS_COMPLETE */, default);
                N.glFlush();
                presenter.HandOverLoading(frames, strip.Id, fence);
            });
        }

        public override bool UploadInBackground(TextureNative texture) => GlBackgroundUploads.Queue(texture);

        /// Hands the frame to the GL thread, which presents it through the presenter with
        /// QualitySettings.vSyncCount as the swap interval.
        public override void Present()
        {
            if (GlWarmup.Enabled) GlWarmup.Publish();
            int interval = SwapIntervalOverride >= 0 ? SwapIntervalOverride : QualitySettings.vSyncCount;
            // The window's size: the frame, drawn at the rendering size (smaller in a fullscreen
            // window the game asked a smaller resolution of), is stretched over it.
            int width = platform.WindowFramebufferWidth, height = platform.WindowFramebufferHeight;
            int frame = Time.frameCount;
            long started = PlayerLoop.FrameStartedAt;
            long mainBusy = GlTimings.Now - started;
            worker.Post(() =>
            {
                presenter.Frame = frame;
                presenter.FrameStarted = started;
                if (gpuTiming) EndGpuTiming();
                uniformStream?.FenceFrame();
                if (GlGpuProfile.Enabled) GlGpuProfile.FrameEnd();
                if (GlBindless.Enabled) GlBindless.EndFrame();
                // The back buffer is not allocated anew here for a window whose size changed
                // since the frame drew: that would present undefined content (black) in place of
                // the frame. The presenter scales the frame to the window instead.
                long t = GlTimings.Now;
                presenter.Present(interval, width, height, mainBusy + worker.TakeFrameBusy());
                worker.NotBusy();
                presenter.Frame = frame + 1;
                GlTimings.Present += GlTimings.Now - t;
                framebufferValid = false;
                gpuTimingArmed = gpuTiming;
            });
            worker.Submit();
            // With vsync, while frames leave time to spare, the next frame starts once this one's
            // predecessor (at the default of two queued frames) has been presented, as in Unity's
            // player (QueuedFrames).
            if (interval > 0) WaitForQueuedFrames(presenter.Presented, frame, interval);
        }

        /// The main thread's wait before the next frame (QueuedFrames), counted in frame stats.
        internal static void WaitForQueuedFrames(QueuedFrames presented, int frame, int interval)
        {
            long waited = presented.WaitFor(frame, QualitySettings.maxQueuedFrames, FramePacing.PeriodTicks * interval, out bool on);
            if (!PlayerLoop.FrameStats) return;
            OniMoono.Engine.FrameStatsWindow.QueuedFrameWait += waited;
            if (on) OniMoono.Engine.FrameStatsWindow.QueuedFrameWaitsOn++;
        }

        // ----- GPU frame timing (diagnostics, GL thread) -----

        const uint TimeElapsed = 0x88BF, QueryResult = 0x8866, QueryResultAvailable = 0x8867;
        const int TimingSlots = 4;
        readonly uint[] timingQueries = new uint[TimingSlots];
        readonly bool[] timingPending = new bool[TimingSlots];
        int timingSlot = -1;
        int draws;

        public override string TakeQueueCounters(int frames) => GlDrawRecorder.TakeCounters(frames);

        public override string TakeFrameCounters(int frames) => worker.TryCall(() =>
        {
            var profile = GlGpuProfile.Take(frames);
            string text = (profile != null ? profile + "\n" : "") + GlTimings.Take(frames) + $"; draws {draws / frames}, program binds {GlBindings.ProgramBinds / frames}, texture binds {GlBindings.TextureBinds / frames}, buffer binds {GlBindings.BufferBinds / frames}, vertex array binds {GlBindings.VertexArrayBinds / frames}, skipped binds {GlBindings.Skipped / frames}, uniforms sent {GlUniforms.UniformsSent / frames}, unchanged uniforms {GlUniforms.UniformsSkipped / frames}, texture uploads {GlTextures.Uploads / (double)frames:F1} ({GlTextures.UploadBytes / frames / 1024} KiB), unchanged {GlTextures.UnchangedUploads / (double)frames:F1}; " + PresenterTimings.Take();
            draws = 0;
            GlUniforms.UniformsSent = GlUniforms.UniformsSkipped = 0;
            GlTextures.Uploads = GlTextures.UploadBytes = GlTextures.UnchangedUploads = 0;
            GlBindings.ProgramBinds = GlBindings.TextureBinds = GlBindings.BufferBinds = GlBindings.VertexArrayBinds = GlBindings.Skipped = 0;
            return text;
        }, out var counters) ? counters : "the OpenGL device has stopped";

        bool gpuTiming, gpuTimingArmed;
        double gpuTotal;
        int gpuFrames;

        /// GPU time of a frame's commands (from the first to the present), averaged over the frames
        /// whose results arrived since the last call; -1 before any, and once the device stopped.
        public override double TakeGpuFrameMilliseconds() => worker.TryCall(() =>
        {
            if (!gpuTiming)
            {
                gpuTiming = true;
                fixed (uint* q = timingQueries) N.glGenQueries(TimingSlots, (GlQuery*)q);
                BeginGpuTiming();
                return -1;
            }
            double ms = gpuFrames > 0 ? gpuTotal / gpuFrames : -1;
            gpuTotal = 0;
            gpuFrames = 0;
            return ms;
        }, out var ms) ? ms : -1;

        void BeginGpuTiming()
        {
            timingSlot = (timingSlot + 1) % TimingSlots;
            if (timingPending[timingSlot]) CollectGpuTiming(timingSlot);
            N.glBeginQuery(TimeElapsed, new GlQuery(timingQueries[timingSlot]));
        }

        void EndGpuTiming()
        {
            N.glEndQuery(TimeElapsed);
            timingPending[timingSlot] = true;
            for (int i = 0; i < TimingSlots; i++)
                if (i != timingSlot && timingPending[i])
                {
                    uint available = 0;
                    N.glGetQueryObjectuiv(timingQueries[i], QueryResultAvailable, &available);
                    if (available != 0) CollectGpuTiming(i);
                }
        }

        void CollectGpuTiming(int slot)
        {
            ulong ns = 0;
            N.glGetQueryObjectui64v(timingQueries[slot], QueryResult, &ns);
            timingPending[slot] = false;
            gpuTotal += ns / 1e6;
            gpuFrames++;
        }
    }

    internal static class SpanExtensions
    {
        public static void SetValues(this Span<int> span, params ReadOnlySpan<int> values) => values.CopyTo(span);
    }
}
