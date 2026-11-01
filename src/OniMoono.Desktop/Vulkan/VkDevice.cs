using System;
using System.Collections.Generic;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace OniMoono.Desktop.Vulkan
{
    internal sealed class VkCaps : GraphicsCaps
    {
        public string Name = "", Vendor = "", VersionString = "";
        public int MaxTexture = 16384, MaxTargets = 8, MemoryMB, Device, VendorNumber;
        public bool Bc;
        public Func<VkFormat, VkFormatFeatureFlags> Features = _ => 0;

        /// The game sees the Vulkan player it runs the programs of: this device executes Unity's
        /// Vulkan programs with Unity's Vulkan conventions (reversed depth, render textures
        /// rendered flipped), so code that branches on the device type sees the conventions it
        /// renders with (ARCHITECTURE D54).
        public override GraphicsDeviceType DeviceType => GraphicsDeviceType.Vulkan;
        public override string DeviceName => Name;
        public override string DeviceVendor => Vendor;
        public override string DeviceVersion => VersionString;
        public override int DeviceId => Device;
        public override int VendorId => VendorNumber;
        public override int MemorySizeMB => MemoryMB;
        public override int ShaderLevel => 50;
        public override int MaxTextureSize => MaxTexture;
        public override int MaxRenderTargets => MaxTargets;
        public override bool Supports2DArrayTextures => true;
        public override bool Supports3DTextures => true;
        public override bool SupportsInstancing => true;
        public override bool SupportsComputeShaders => false;

        public override bool SupportsTexture(TextureFormat format)
        {
            // As GlCaps: compressed formats when the device samples them, others when the
            // engine can encode them.
            try
            {
                var f = VkFormats.Of(format, Bc);
                if (f.Conversion == TexelConversion.Decode) return false;
                bool sampled = (Features(f.Format) & VkFormatFeatureFlags.SampledImageBit) != 0;
                return sampled && (TextureFormats.IsBlockCompressed(format) || TextureFormats.CanEncode(format));
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        public override bool SupportsRenderTexture(RenderTextureFormat format)
        {
            try
            {
                var g = RenderTextureFormats.ToGraphicsFormat(format, false);
                if (g == UnityEngine.Experimental.Rendering.GraphicsFormat.None) return true;
                return (Features(VkFormats.Color(g)) & VkFormatFeatureFlags.ColorAttachmentBit) != 0;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
    }

    /// The Vulkan device (ARCHITECTURE §3.5, Vulkan): Unity's shipped Vulkan programs (shader
    /// platform 18) executed with Unity's Vulkan conventions, rendering the pixels the OpenGL
    /// device renders. Its main-thread side is the OpenGL device's: GlDrawRecorder records each
    /// draw (program variant, resolved state, uniform values, textures, mesh) and GlWorker
    /// hands frames to the device thread, whose resource work is this device's (IResourceWork).
    /// The device thread records Vulkan command buffers and submits each frame; VkPresenter
    /// presents from its own thread.
    internal sealed unsafe partial class VkDevice : RenderDevice, IResourceWork
    {
        /// Diagnostics (--vsync-count N): the swap interval whatever QualitySettings says.
        public static int SwapIntervalOverride = -1;

        readonly GlfwPlatform platform;
        readonly VkContext vk;
        readonly VkCaps caps = new();
        readonly GlWorker worker;
        readonly GlDrawRecorder recorder;
        readonly VkPresenter presenter;
        readonly VkFrames frames;
        readonly VkPrograms programs;
        readonly VkPipelines pipelines;
        readonly VkSurfaceKHR surface;
        readonly Dictionary<string, DeviceTexture> defaults = new();
        readonly VkStartGuard guard;
        int presented;
        CameraConstants camera;
        Material passMaterial;
        int passIndex;

        /// Makes the device for a window created without a GL context (WindowSettings.Api
        /// Vulkan); throws VulkanUnavailableException with the reason, having released what it
        /// made, so the caller can open an OpenGL window instead. `guard` (VkStartGuard) is cleared
        /// once the first frames are on screen.
        public VkDevice(GlfwPlatform platform, VkContext vk, string cacheDirectory, VkStartGuard guard = null)
        {
            this.platform = platform;
            this.vk = vk;
            this.guard = guard;
            VkSplash splash = null;
            try
            {
                if (Glfw.CreateWindowSurface(vk.Instance.Handle.VkHandle, platform.Window, out ulong rawSurface) != 0)
                    throw new VulkanUnavailableException("no Vulkan surface for the window");
                surface = new VkSurfaceKHR((nint)rawSurface);
                FillCaps();
                frames = new VkFrames(vk);
                programs = new VkPrograms(vk);
                pipelines = new VkPipelines(vk);
                splash = GlDevice.Splash != null ? new VkSplash(vk, programs, GlDevice.Splash) : null;
                VkContext.FailAt("device");
                presenter = new VkPresenter(vk, platform.Window, surface, splash, platform.FramebufferWidth, platform.FramebufferHeight, depthFormat);
                CreateShared();
                VkContext.FailAt("presenter");
            }
            catch (Exception e)
            {
                // The presenter owns the splash once made; before that it is released here.
                if (presenter == null) splash?.Dispose();
                ReleaseAll();
                if (e is VulkanUnavailableException) throw;
                throw new VulkanUnavailableException($"{e.GetType().Name}: {e.Message}", e);
            }
            platform.ContentsDamaged += presenter.Refresh;
            worker = new GlWorker("Vulkan", () => { }, () => { }, CommandFailed) { Resources = this };
            recorder = new GlDrawRecorder(worker, DefaultTexture, RunDraw)
            {
                PlatformOf = VkPrograms.PlatformOf,
                Conventions = new RenderConventions(Vulkan: true, Flipped: false),
            };
            platform.Closing += Shutdown;
            EngineLog.Engine($"vulkan: device ready ({caps.Name}, depth {depthFormat}, swapchain {presenter.SwapchainFormat})");
        }

        void FillCaps()
        {
            var p = vk.Properties;
            caps.Name = vk.DeviceName;
            caps.VendorNumber = p.VendorID;
            caps.Device = p.DeviceID;
            caps.Vendor = p.VendorID switch { 0x10DE => "NVIDIA Corporation", 0x1002 => "ATI Technologies Inc.", 0x8086 => "Intel", 0x10005 => "Mesa", _ => $"0x{p.VendorID:X4}" };
            caps.VersionString = vk.DriverInfo;
            caps.MaxTexture = p.Limits.MaxImageDimension2D;
            caps.MaxTargets = Math.Min(8, p.Limits.MaxColorAttachments);
            caps.Bc = vk.TextureCompressionBC;
            var features = new Dictionary<VkFormat, VkFormatFeatureFlags>();
            caps.Features = f =>
            {
                lock (features)
                {
                    if (!features.TryGetValue(f, out var v)) features[f] = v = vk.Physical.GetFormatProperties(f).OptimalTilingFeatures;
                    return v;
                }
            };
            var memory = vk.Physical.GetMemoryProperties();
            long deviceLocal = 0;
            for (int i = 0; i < memory.MemoryHeapCount; i++)
                if ((memory.MemoryHeaps[i].Flags & VkMemoryHeapFlags.DeviceLocalBit) != 0) deviceLocal = Math.Max(deviceLocal, (long)memory.MemoryHeaps[i].Size.Value);
            caps.MemoryMB = (int)(deviceLocal >> 20);
            maxAnisotropy = vk.SamplerAnisotropy ? p.Limits.MaxSamplerAnisotropy : 1f;
            maxLodBias = p.Limits.MaxSamplerLodBias;
            depthFormat = (caps.Features(VkFormat.D24UNormS8UInt) & VkFormatFeatureFlags.DepthStencilAttachmentBit) != 0 ? VkFormat.D24UNormS8UInt : VkFormat.D32SFloatS8UInt;
            VkFormats.Sampled = f => (caps.Features(f) & (VkFormatFeatureFlags.SampledImageBit | VkFormatFeatureFlags.TransferDstBit)) == (VkFormatFeatureFlags.SampledImageBit | VkFormatFeatureFlags.TransferDstBit);
        }

        public override GraphicsCaps Caps => caps;
        public override bool RendersFrames => true;
        public override bool Supports(ShaderNative shader) => VkPrograms.PlatformOf(shader) != 0;

        public static bool IsDeviceLost(Exception e)
            => e is VkResultError { Result: VkResult.ErrorDeviceLost } || e.InnerException is VkResultError { Result: VkResult.ErrorDeviceLost };

        readonly HashSet<string> failuresLogged = new();

        /// A recorded command threw (device thread): logged once per kind of failure, the open
        /// pass ended, and the frame goes on; a lost device ends the game (see DeviceLost).
        void CommandFailed(Exception e)
        {
            if (IsDeviceLost(e))
            {
                if (deviceLost) return;
                deviceLost = true;
                EngineLog.Engine($"vulkan: the device was lost ({caps.Name}, {vk.DriverInfo}): {e.Message}\n{e.StackTrace}");
                return;
            }
            if (failuresLogged.Add($"{e.GetType().FullName}: {e.Message}")) Debug.LogException(e);
            if (rendering && frames.MainOpen)
            {
                try
                {
                    frames.Main.EndRendering();
                }
                catch (Exception end) when (!IsDeviceLost(end))
                {
                }
            }
            rendering = false;
            boundPipeline = default;
        }

        /// Set on the device thread when the device is lost: no more work is recorded, and the
        /// main thread ends the game at its next present with an error, as Unity's player does
        /// when its graphics device is removed. A device that is lost cannot be recovered in
        /// place (every texture, mesh and program would have to be made again, and the game holds
        /// no copy of the non-readable textures), and switching to OpenGL mid-game would need the
        /// same. Nothing is recorded for the next start: the error says how to choose OpenGL.
        volatile bool deviceLost;
        /// What ended the device ("was lost", or presenting failed for good).
        string lostWhat = "was lost";

        /// Presenting ended for good (VkPresenter.Failure: a lost surface, a swapchain that
        /// could not be made again): the game quits with the error, as for a lost device, rather
        /// than running on with a black window.
        void PresenterFailed(Exception e)
        {
            if (deviceLost) return;
            lostWhat = "could no longer present to the window";
            deviceLost = true;
            EngineLog.Engine($"vulkan: presenting failed ({caps.Name}, {vk.DriverInfo}): {e.GetType().Name}: {e.Message}");
        }

        /// A presented frame (device thread): the start-up guard is cleared once the first
        /// VkStartGuard.FramesToClear are on screen; ONI_VULKAN_FAIL=crash-at:N ends the process
        /// at frame N as a crash would (no shutdown, the guard left in place).
        void Presented()
        {
            presented++;
            if (presented == crashAt)
            {
                EngineLog.Engine($"vulkan: ONI_VULKAN_FAIL=crash-at:{crashAt}: the process ends as a crash would");
                EngineLog.Close();
                Environment.Exit(70);
            }
            if (presented == VkStartGuard.FramesToClear && guard != null)
            {
                guard.Clear();
                EngineLog.Engine($"vulkan: the first {VkStartGuard.FramesToClear} frames are on screen; start-up guard cleared");
            }
        }

        static readonly int crashAt = VkFault.Number("crash-at");

        /// Test hooks (VkFault): presenting fails for good at frame N (present-after:N), or the
        /// swapchain runs out of memory three times from frame N (present-oom-after:N).
        static readonly int failPresentAt = VkFault.Number("present-after"), presentOomAt = VkFault.Number("present-oom-after");

        // ----- Targets, viewport, clears (main thread records) -----

        sealed record BoundTarget(DeviceTexture[] Colors, DeviceTexture Depth, int BackBufferWidth, int BackBufferHeight);

        public override void SetRenderTarget(in RenderTargetBinding binding)
        {
            DeviceTexture[] colors = null;
            DeviceTexture depth = null;
            if (binding.Colors != null && binding.Colors.Length > 0 && binding.Colors[0] != null)
            {
                colors = new DeviceTexture[binding.Colors.Length];
                for (int i = 0; i < colors.Length; i++) colors[i] = binding.Colors[i] == null ? null : GlTextures.Prepare(worker, binding.Colors[i]);
                if (binding.Depth != null) depth = binding.Depth == binding.Colors[0] ? colors[0] : GlTextures.Prepare(worker, binding.Depth);
            }
            var bound = new BoundTarget(colors, depth, platform.FramebufferWidth, platform.FramebufferHeight);
            // Unity's Vulkan programs render into render textures upside down (RenderConventions).
            recorder.Conventions = new RenderConventions(Vulkan: true, Flipped: colors != null);
            worker.Post(() => SetTarget(bound));
        }

        public override void SetViewport(UnityEngine.Rect pixels) => worker.Post(() => SetViewportNow(pixels));

        public override void SetScissor(Rect? pixels) => worker.Post(() => SetScissorNow(pixels));

        public override void Clear(bool color, bool depth, Color backgroundColor, float depthValue) => worker.Post(() => ClearNow(color, depth, backgroundColor, depthValue));

        protected override void ApplyCamera(in CameraConstants c)
        {
            camera = c;
            recorder.Camera = c;
        }

        // ----- Drawing -----

        public override void Draw(in DrawRequest draw) => recorder.Draw(draw);

        public override void DrawQueue(List<DrawItem> items) => recorder.DrawQueue(items);

        public override bool SetPass(Material material, int pass)
        {
            passMaterial = material;
            passIndex = pass;
            return true;
        }

        /// Records the pass set by SetPass for one immediate draw (as GlDevice.RecordPass).
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
            int blob = ShaderPrograms.SelectVariant(recorder.VariantsOf(shader, pass), k => GlDrawRecorder.KeywordEnabled(k, material, null));
            if (blob < 0) return null;
            var program = recorder.Program(shader, blob);
            if (program.Id == 0) return null;
            var inputs = new DrawInputs
            {
                ObjectToWorld = objectToWorld,
                Camera = camera with { View = view, Projection = projection },
                Conventions = recorder.Conventions,
                Material = material,
                Shader = shader,
            };
            return recorder.Record(program, recorder.StateOf(pass), inputs, worker.Arena);
        }

        public override void DrawImmediate(ImmediatePrimitive primitive, ReadOnlySpan<ImmediateVertex> vertices, Matrix4x4 modelView, Matrix4x4 projection)
        {
            if (vertices.Length == 0) return;
            var recorded = RecordPass(modelView, Matrix4x4.identity, projection, out _);
            if (recorded == null) return;
            const int floats = VkPipelines.ImmediateStride / 4;
            var data = new float[vertices.Length * floats];
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                int o = i * floats;
                data[o] = v.Position.x; data[o + 1] = v.Position.y; data[o + 2] = v.Position.z;
                data[o + 3] = v.Color.r; data[o + 4] = v.Color.g; data[o + 5] = v.Color.b; data[o + 6] = v.Color.a;
                Uv(data, o, 0, v.Uv0); Uv(data, o, 1, v.Uv1); Uv(data, o, 2, v.Uv2); Uv(data, o, 3, v.Uv3);
                Uv(data, o, 4, v.Uv4); Uv(data, o, 5, v.Uv5); Uv(data, o, 6, v.Uv6); Uv(data, o, 7, v.Uv7);
            }
            int[] indices = null;
            if (primitive == ImmediatePrimitive.Quads)
            {
                int quads = vertices.Length / 4;
                indices = new int[quads * 6];
                for (int q = 0; q < quads; q++)
                {
                    int b = q * 4;
                    indices.AsSpan(q * 6, 6).SetValues(b, b + 1, b + 2, b, b + 2, b + 3);
                }
            }
            int count = vertices.Length;
            worker.Post(() => ExecuteImmediate(recorded, primitive, data, count, indices));
        }

        static void Uv(float[] data, int vertex, int set, Vector4 uv)
        {
            int at = vertex + 7 + set * 4;
            data[at] = uv.x;
            data[at + 1] = uv.y;
            data[at + 2] = uv.z;
            data[at + 3] = uv.w;
        }

        public override void DrawMeshNow(MeshNative mesh, int subMesh, Matrix4x4 objectToWorld, Matrix4x4 view, Matrix4x4 projection)
        {
            int count = mesh.SubMeshes.Count;
            if (count == 0 || subMesh >= count) return;
            int first = subMesh < 0 ? 0 : subMesh, last = subMesh < 0 ? count - 1 : subMesh;
            var device = GlMeshes.Prepare(worker, mesh);
            for (int s = first; s <= last; s++)
            {
                var recorded = RecordPass(objectToWorld, view, projection, out _);
                if (recorded == null) return;
                recorded.Mesh = device;
                recorded.Layout = device.Layout;
                recorded.SubMesh = device.SubMeshes[s];
                worker.Post(recorded.RunDraw);
            }
        }

        // ----- Read-back, resources, presentation -----

        public override void ReadPixels(RenderTextureNative source, RectInt rect, Texture2DNative destination, int destX, int destY)
        {
            int w = Math.Min(rect.width, destination.Width - destX), h = Math.Min(rect.height, destination.Height - destY);
            if (w <= 0 || h <= 0 || !TextureFormats.CanEncode(destination.Format) || destination.Memory == null) return;
            var device = source != null ? GlTextures.Prepare(worker, source) : null;
            bool autoMips = source?.AutoGenerateMips ?? false;
            int fbWidth = platform.FramebufferWidth, fbHeight = platform.FramebufferHeight;
            var pixels = worker.Call(() => ReadNow(device, autoMips, rect.x, rect.y, w, h, fbWidth, fbHeight));
            var mip = destination.Memory.Slice(destination.MipOffset(0), destination.MipSize(0));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    TextureFormats.Encode(destination.Format, mip, destination.Width, destX + x, destY + y,
                        new Color(pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]));
                }
        }

        public override void Upload(TextureNative texture) => GlTextures.Prepare(worker, texture);

        public override void ReleaseResource(object gpu)
        {
            switch (gpu)
            {
                case DeviceTexture t: worker.Post(() => ReleaseTexture(t)); break;
                case DeviceMesh m: worker.Post(() => ReleaseMesh(m)); break;
                case DeviceProgramSet p: worker.Post(() => ReleasePrograms(p)); break;
            }
        }

        /// --check-shaders: the warm-up also makes each program's pipeline for its pass's state
        /// (with no material), immediate geometry and the back buffer's formats.
        public static bool CheckPipelines;

        /// Programs that could not be built and pipelines the driver refused (--check-shaders).
        public static int CheckFailures => VkPrograms.Failures + VkPipelines.Refused;

        /// Builds every variant of every loaded shader (Shader.WarmupAllShaders), as
        /// GlDevice.WarmupShaders.
        public override void WarmupShaders()
        {
            var shaders = new List<ShaderNative>();
            foreach (var native in ObjectTable.Snapshot())
                if (native is ShaderNative shader && VkPrograms.PlatformOf(shader) != 0) shaders.Add(shader);
            var work = new List<(ShaderNative Shader, int Blob, ResolvedState State)>();
            foreach (var shader in shaders)
                foreach (var sub in shader.SubShaders)
                    foreach (var pass in sub.Passes)
                    {
                        if (pass.Type != 0) continue;
                        var state = CheckPipelines ? ResolvedState.Of(recorder.StateOf(pass), new DrawInputs { Shader = shader }, null) : default;
                        foreach (var variant in recorder.VariantsOf(shader, pass))
                            if (!GlDevice.StereoOnly(variant.Keywords)) work.Add((shader, variant.BlobIndex, state));
                    }
            worker.Call(() =>
            {
                var formats = VkTargetFormats.Of([VkFormat.R8G8B8A8UNorm], depthFormat, depthFormat, 1, flipped: false);
                foreach (var (shader, blob, state) in work)
                    if (Program(shader, blob) is { Vk: { } program } && CheckPipelines)
                        pipelines.Get(new VkPipelineKey(program, state, VkPipelines.ImmediateLayout, VkPrimitiveTopology.TriangleList, formats), null);
            });
        }

        public override void FlushRecorded() => worker.Submit();

        /// Hands the frame to the device thread, which submits it and gives the back buffer to
        /// the presenter with QualitySettings.vSyncCount as the swap interval.
        public override void Present()
        {
            if (deviceLost && !quitRequested)
            {
                quitRequested = true;
                Debug.LogError($"Vulkan stopped working: the graphics device {lostWhat}, so the game quits. If this keeps happening, choose OpenGL "
                               + $"under Renderer in Moono Tweaks, or start the game with --renderer opengl. The log is {EngineLog.FilePath ?? Application.consoleLogPath}.");
                PlayerLoop.RequestQuit(1);
            }
            int interval = SwapIntervalOverride >= 0 ? SwapIntervalOverride : QualitySettings.vSyncCount;
            int width = platform.FramebufferWidth, height = platform.FramebufferHeight;
            int frame = Time.frameCount;
            long started = PlayerLoop.FrameStartedAt;
            worker.Post(() => PresentNow(frame, interval, width, height, started));
            worker.Submit();
        }

        bool quitRequested;

        // ----- Diagnostics -----

        public override string TakeQueueCounters(int frames) => GlDrawRecorder.TakeCounters(frames);

        public override string TakeFrameCounters(int n) => worker.Call(() =>
        {
            string text = VkTimings.Take(n) + $"; draws {draws / n}, pipeline binds {pipelineBinds / n}, render passes {renderPasses / n}, passes broken by uploads {uploadBreaks / n}, "
                + $"submits {frames.Submits / (double)n:F1}, programs built {programs.Built}, pipelines made {pipelines.Created} ({pipelines.CreateTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms), "
                + $"texture uploads {textureUploads / (double)n:F1} ({textureUploadBytes / n / 1024} KiB), unchanged {unchangedUploads / (double)n:F1}; "
                + OniMoono.Desktop.Gl.PresenterTimings.Take();
            draws = pipelineBinds = renderPasses = uploadBreaks = 0;
            frames.Submits = 0;
            textureUploads = textureUploadBytes = unchangedUploads = 0;
            return text;
        });

        public override double TakeGpuFrameMilliseconds() => worker.Call(() =>
        {
            double ms = frames.GpuFrames > 0 ? frames.GpuMilliseconds / frames.GpuFrames : -1;
            frames.GpuMilliseconds = 0;
            frames.GpuFrames = 0;
            return ms;
        });

        public override string MemoryCounters() => worker.Call(() =>
        {
            var memory = vk.Physical.GetMemoryProperties();
            Span<Brutal.VulkanApi.Vma.VmaBudget> budgets = new Brutal.VulkanApi.Vma.VmaBudget[memory.MemoryHeapCount];
            vk.Allocator.GetHeapBudgets(budgets);
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < budgets.Length; i++)
            {
                if ((memory.MemoryHeaps[i].Flags & VkMemoryHeapFlags.DeviceLocalBit) == 0) continue;
                text.Append(text.Length == 0 ? "" : "; ").Append($"heap {i}: {(long)budgets[i].Usage.Value >> 20} MiB used of {(long)budgets[i].Budget.Value >> 20} MiB budget");
            }
            // Windows: the process's own video memory on the device's adapter (all of it,
            // DXGI's swap chain and shared texture too), as Windows counts it.
            if (OperatingSystem.IsWindows() && vk.DxgiInterop && vk.Luid != 0 && OniMoono.Desktop.Gl.Dxgi.ProcessVideoMemory(vk.Luid) is var (local, nonLocal))
                text.Append(text.Length == 0 ? "" : "; ").Append($"process {local >> 20} MiB local, {nonLocal >> 20} MiB non-local");
            return text.Length == 0 ? null : text.ToString();
        });

        // ----- Shutdown -----

        void Shutdown()
        {
            worker.Dispose();
            // The presenter's thread submits too: it ends before the queue is waited for.
            presenter?.Dispose();
            try
            {
                vk.Device.WaitIdle();
            }
            catch (VkResultError)
            {
                // A lost device: its objects are destroyed regardless.
            }
            vk.SavePipelineCache();
            ReleaseAll();
            vk.Dispose();
            // Ended cleanly before its first frames were all on screen: no crash either.
            if (!deviceLost) guard?.Clear();
        }

        /// Destroys everything the device made (also after a failed start, from whatever was
        /// made by then).
        void ReleaseAll()
        {
            presenter?.Dispose();
            try
            {
                vk.Device?.WaitIdle();
            }
            catch (VkResultError)
            {
            }
            foreach (var t in defaults.Values) DestroyTexture(t.Vk);
            defaults.Clear();
            DestroyShared();
            frames?.Dispose();
            foreach (var t in liveTextures) DestroyTexture(t);
            liveTextures.Clear();
            foreach (var m in liveMeshes) m.Buffer?.Destroy(vk);
            liveMeshes.Clear();
            pipelines?.Dispose();
            foreach (var p in livePrograms) p.Destroy(vk);
            livePrograms.Clear();
            programs?.Dispose();
            foreach (var s in samplers.Values) vk.Device.DestroySampler(s, null);
            samplers.Clear();
            if (!surface.IsNull()) vk.Instance.DestroySurfaceKHR(surface, null);
        }
    }

    /// Diagnostics (--frame-stats): device-thread time by kind of work, as GlTimings.
    internal static class VkTimings
    {
        public static long Busy, Draws, TextureUploads, MeshUploads, Targets, Present;

        public static string Take(int frames)
        {
            static string Ms(long ticks, int frames) => (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            // The thread's whole time executing frames is GlWorker's (GlTimings.Busy).
            var text = $"vulkan thread busy {Ms(GlTimings.Busy, frames)}: draws {Ms(Draws, frames)}, texture uploads {Ms(TextureUploads, frames)}, mesh uploads {Ms(MeshUploads, frames)}, targets {Ms(Targets, frames)}, present {Ms(Present, frames)}";
            Draws = TextureUploads = MeshUploads = Targets = Present = 0;
            GlTimings.Busy = 0;
            return text;
        }
    }
}
