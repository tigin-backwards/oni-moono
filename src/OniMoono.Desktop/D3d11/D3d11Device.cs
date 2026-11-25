using System;
using System.Collections.Generic;
using OniMoono.Desktop.Gl;
using OniMoono.Desktop.Vulkan;
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

        /// SupportsTextureFormat: what this device makes, as VkCaps answers: a format
        /// D3d11Formats.Of has without decoding on the CPU, that the engine can encode or is block
        /// compressed, and that the device samples as a 2D texture (CheckFormatSupport:
        /// D3D11_FORMAT_SUPPORT_TEXTURE2D and SHADER_SAMPLE). The original says yes to more
        /// (OriginalSupportsTexture); the formats where they differ are logged at start-up and
        /// left to M7 (SystemInfo parity, done criterion 2).
        public override bool SupportsTexture(TextureFormat format)
            => textures.GetOrAdd(format, f =>
            {
                try
                {
                    var d = D3d11Formats.Of(f);
                    if (d.Conversion == Vulkan.TexelConversion.Decode || !(TextureFormats.IsBlockCompressed(f) || TextureFormats.CanEncode(f))) return false;
                    return Has(d.Format, D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_TEXTURE2D | D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_SHADER_SAMPLE);
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            });

        /// SupportsRenderTextureFormat: a colour format D3d11Formats.Color makes that the device
        /// renders to and samples (D3D11_FORMAT_SUPPORT_RENDER_TARGET and SHADER_SAMPLE); depth-only
        /// formats always. The original says yes to all 28 (d3d11-unity-probes.md §1).
        public override bool SupportsRenderTexture(RenderTextureFormat format)
            => renderTextures.GetOrAdd(format, f =>
            {
                try
                {
                    var g = RenderTextureFormats.ToGraphicsFormat(f, false);
                    if (g == UnityEngine.Experimental.Rendering.GraphicsFormat.None) return true;
                    return Has(D3d11Formats.Color(g), D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_RENDER_TARGET | D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_SHADER_SAMPLE);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException)
                {
                    return false;
                }
            });

        readonly System.Collections.Concurrent.ConcurrentDictionary<TextureFormat, bool> textures = new();
        readonly System.Collections.Concurrent.ConcurrentDictionary<RenderTextureFormat, bool> renderTextures = new();

        /// Whether the device supports every use in `wanted` for `format`. ID3D11Device's methods
        /// are free-threaded ("Introduction to multithreading in Direct3D 11"), so any thread asks.
        unsafe bool Has(DXGI_FORMAT format, D3D11_FORMAT_SUPPORT wanted)
        {
            D3D11_FORMAT_SUPPORT support = 0;
            var device = d3d.Device;
            return device->lpVtbl->CheckFormatSupport(device, format, &support) >= 0 && (support & wanted) == wanted;
        }

        /// The original's SupportsTextureFormat on Direct3D 11: all of the 64 values but the ETC,
        /// EAC, PVRTC and ASTC formats and the crunched ETC ones (d3d11-unity-probes.md §1).
        internal static bool OriginalSupportsTexture(TextureFormat format)
        {
            string name = format.ToString();
            return !(name.StartsWith("ETC", StringComparison.Ordinal) || name.StartsWith("EAC", StringComparison.Ordinal)
                     || name.StartsWith("PVRTC", StringComparison.Ordinal) || name.StartsWith("ASTC", StringComparison.Ordinal));
        }

        /// The formats this device answers otherwise than the original (logged at start-up).
        public string DifferencesFromOriginal()
        {
            var textureNo = new List<string>();
            foreach (TextureFormat f in Enum.GetValues<TextureFormat>())
                if (SupportsTexture(f) != OriginalSupportsTexture(f)) textureNo.Add($"{f} {(SupportsTexture(f) ? "yes" : "no")}");
            var renderNo = new List<string>();
            foreach (RenderTextureFormat f in Enum.GetValues<RenderTextureFormat>())
                if (!SupportsRenderTexture(f)) renderNo.Add(f.ToString());
            return $"textures [{string.Join(", ", textureNo)}], render textures no [{string.Join(", ", renderNo)}]";
        }
    }

    /// The Direct3D 11 render device (docs/research/d3d11-plan.md, milestone M2): Unity's shipped
    /// Direct3D 11 programs (shader platform 4, DXBC unmodified) with Unity's Direct3D 11
    /// conventions, rendering the pixels the OpenGL device renders. Its main-thread side is the
    /// other devices': GlDrawRecorder records each draw (program, resolved state, constant-buffer
    /// bytes, textures, mesh) and GlWorker hands frames to the device thread, whose resource work
    /// is this device's (IResourceWork). The device thread alone uses the immediate context, and
    /// presents (the original makes every call on one thread, its immediate context only, and no
    /// deferred contexts: d3d11-captures.md §1 and §5); the swap chain is the device's own
    /// (D3d11SwapChain), and its presentation, repeats, splash and loading animation are the
    /// device thread's too (D3d11Present).
    ///
    /// The conventions are Vulkan's, as measured on the original (d3d11-unity-probes.md §2, §3):
    /// reversed Z (`GetGPUProjectionMatrix`'s z row is the Vulkan device's GpuProjection, a clear
    /// to v stores 1 - v), render textures rendered upside down (`_ProjectionParams.x` -1 and the
    /// projection's y negated into one, not on the screen), so RenderConventions(Vulkan: true,
    /// Flipped). Direct3D's viewport puts clip-space y = 1 at its top row ("Y rt = (1 - Y) *
    /// Viewport.Height * 0.5 + Viewport.TopLeftY", Direct3D 11.3 Functional Specification §15.6
    /// Viewport), so with no y negation in the programs (Vulkan's carry one; that Unity's DXBC
    /// has none follows from the original drawing the screen upright with the projection not
    /// negated, and the pixel diff against OpenGL shows it) the screen comes out upright and a
    /// render texture's rows in OpenGL's order, as on Vulkan.
    internal sealed unsafe partial class D3d11Device : RenderDevice, IResourceWork
    {
        /// `--vsync N`: the swap interval in place of QualitySettings.vSyncCount.
        public static int SwapIntervalOverride = -1;

        readonly GlfwPlatform platform;
        readonly D3d11Context d3d;
        readonly D3d11SwapChain chain;
        readonly D3d11Caps caps;
        readonly GlWorker worker;
        readonly GlDrawRecorder recorder;
        readonly D3d11States states;
        CameraConstants camera;
        Material passMaterial;
        int passIndex;
        bool quitRequested;
        long debugMessages;
        /// Why the device stopped (set on the device thread; the main thread quits at its next
        /// present).
        volatile string lost;
        long failedCommands;
        readonly HashSet<string> failuresLogged = new();
        readonly VkStartGuard guard;

        /// Makes the device for a window created without a GL context (WindowSettings.Api
        /// Direct3D11); throws (D3d11UnavailableException with the reason) having released what it
        /// made, so the caller can go on to another renderer. `guard` (the start-up guard) is
        /// cleared once the first frames are on screen. Fault hooks (D3d11Fault): `swapchain`,
        /// `presenter`, `screen` fail after their step.
        public D3d11Device(GlfwPlatform platform, D3d11Context d3d, VkStartGuard guard = null)
        {
            this.platform = platform;
            this.d3d = d3d;
            this.guard = guard;
            caps = new D3d11Caps(d3d);
            states = new D3d11States(d3d.Device);
            chain = D3d11SwapChain.Create(d3d, platform.Win32Handle, platform.WindowFramebufferWidth, platform.WindowFramebufferHeight);
            try
            {
                D3d11Fault.FailAt("swapchain");
                // The first frame waits too (D3d11SwapChain.WaitForFrame); the rest wait after
                // each Present (PresentNow), before the next frame renders.
                chain.WaitForFrame();
                CreateShared();
                CreatePresenter();
                D3d11Fault.FailAt("presenter");
                // The first frame's screen texture, at the rendering size (D3d11Present).
                EnsureScreen(CurrentScreen, platform.FramebufferWidth, platform.FramebufferHeight);
                // The back buffer is the target until the game sets another.
                SetTarget(lastTarget);
                D3d11Fault.FailAt("screen");
            }
            catch
            {
                ReleasePresenter();
                ReleaseShared();
                chain.Dispose();
                throw;
            }
            EngineLog.Engine($"d3d11: {d3d.Adapter.Name} (vendor 0x{d3d.Adapter.VendorId:x4}, device 0x{d3d.Adapter.DeviceId:x4}), {d3d.VersionString}, "
                             + $"{(D3d11Context.Validation ? "debug layer on" : "no debug layer")}; {chain.Describe()}");
            EngineLog.Engine($"d3d11: format answers unlike the original's (M7): {caps.DifferencesFromOriginal()}");
            DrainDebugMessages();
            // From here the immediate context belongs to the device thread.
            worker = new GlWorker("Direct3D 11", () => { }, () => { }, CommandFailed) { Resources = this };
            recorder = new GlDrawRecorder(worker, DefaultTexture, RunDraw)
            {
                PlatformOf = D3d11Programs.PlatformOf,
                SelectStages = D3d11Programs.Select,
                Conventions = new RenderConventions(Vulkan: true, Flipped: false),
            };
            platform.Closing += Shutdown;
            StartPresenting();
            if (OperatingSystem.IsWindows()) platform.AdoptDeviceExclusive(this);
        }

        public override GraphicsCaps Caps => caps;
        public override bool RendersFrames => true;
        public override bool Supports(ShaderNative shader) => shader.HasProgramsFor(ShaderNative.D3D11Platform);

        /// Cameras to the screen draw into the frame renderer's screen-sized render texture
        /// (FrameRenderer.CameraScreen), as the original's do, so Unity's programs read a render
        /// texture's projection there: Klei/BatchedAnimation, BatchedAnimationHuman and
        /// BatchedAnimationConduit take the light buffer at (unity_MatrixVP · world
        /// position).xy · (0.5, −0.5) + 0.5 (their shipped pixel programs, `fxc /dumpbin`),
        /// right only with a render texture's y-negated projection. D3D11 cannot turn the
        /// viewport over instead, as the Vulkan device does: "In all cases, Width and Height
        /// must be >= 0" (D3D11_VIEWPORT, Remarks).
        public override bool CameraPassesOffscreen => true;

        /// Unity's Direct3D 11 programs cannot take CaptureTiles' changes (as Vulkan's).
        public override bool CaptureTileShaders => false;

        public override long FailedCommands => System.Threading.Interlocked.Read(ref failedCommands) + (lost != null ? 1 : 0);

        /// A recorded command threw (device thread): logged once per kind of failure, and the
        /// frame goes on.
        void CommandFailed(Exception e)
        {
            System.Threading.Interlocked.Increment(ref failedCommands);
            if (failuresLogged.Add($"{e.GetType().FullName}: {e.Message}")) Debug.LogException(e);
        }

        // ----- Targets, viewport, clears (main thread records) -----

        /// Name: the first colour texture's, for diagnostics.
        internal sealed record BoundTarget(DeviceTexture[] Colors, DeviceTexture Depth, int Level, bool ScreenRows, string Name)
        {
            /// The target as the logs name it, made only when a log line needs it (a bind names
            /// the texture only: SetRenderTarget runs for every bind).
            public string Label => Colors == null ? "the back buffer" : $"'{Name}'";
        }

        Action<BoundTarget> setTarget;
        Action<Rect> setViewportNow;
        Action<Rect?> setScissorNow;
        Action<(bool Color, bool Depth, Color Background, float DepthValue)> clearNow;

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
            // Unity's Direct3D 11 programs render into render textures upside down
            // (RenderConventions), but for one rendered as the back buffer is (ScreenRows).
            bool screenRows = colors != null && binding.Colors[0].ScreenRows;
            recorder.Conventions = new RenderConventions(Vulkan: true, Flipped: colors != null && !screenRows);
            worker.Post(worker.Arena.Command(setTarget ??= SetTarget, new BoundTarget(colors, depth, binding.MipLevel, screenRows, colors == null ? null : binding.Colors[0].Name)));
        }

        public override void SetViewport(Rect pixels) => worker.Post(worker.Arena.Command(setViewportNow ??= SetViewportNow, pixels));

        public override void SetScissor(Rect? pixels) => worker.Post(worker.Arena.Command(setScissorNow ??= SetScissorNow, pixels));

        public override void Clear(bool color, bool depth, Color backgroundColor, float depthValue)
            => worker.Post(worker.Arena.Command(clearNow ??= c => ClearNow(c.Color, c.Depth, c.Background, c.DepthValue), (color, depth, backgroundColor, depthValue)));

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

        /// Records the pass set by SetPass for one immediate draw (as VkDevice.RecordPass).
        ProgramInputs RecordPass(Matrix4x4 objectToWorld, Matrix4x4 view, Matrix4x4 projection)
        {
            if (!passMaterial) return null;
            var material = (MaterialNative)passMaterial.NativeOrThrow;
            if (!material.Shader) return null;
            var shader = (ShaderNative)material.Shader.NativeOrThrow;
            var sub = shader.ActiveSubShader;
            if (sub == null || passIndex >= sub.Passes.Length) return null;
            var pass = sub.Passes[passIndex];
            if (D3d11Programs.PlatformOf(shader) == 0) return null;
            int key = recorder.Variant(material, shader, pass, null);
            if (key < 0) return null;
            var program = recorder.Program(shader, key);
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

        /// GL.Begin geometry, interleaved as VkDevice.DrawImmediate writes it (position, colour,
        /// eight texture coordinates), quads as two triangles each.
        public override void DrawImmediate(ImmediatePrimitive primitive, ReadOnlySpan<ImmediateVertex> vertices, Matrix4x4 modelView, Matrix4x4 projection)
        {
            if (vertices.Length == 0) return;
            var recorded = RecordPass(modelView, Matrix4x4.identity, projection);
            if (recorded == null) return;
            const int floats = Vulkan.VkPipelines.ImmediateStride / 4;
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
                var recorded = RecordPass(objectToWorld, view, projection);
                if (recorded == null) return;
                recorded.Mesh = device;
                recorded.Layout = device.Layout;
                recorded.SubMesh = device.SubMeshes[s];
                worker.Post(recorded.RunDraw);
            }
        }

        // ----- Read-back and resources -----

        public override void ReadPixels(RenderTextureNative source, RectInt rect, Texture2DNative destination, int destX, int destY)
        {
            int w = Math.Min(rect.width, destination.Width - destX), h = Math.Min(rect.height, destination.Height - destY);
            if (w <= 0 || h <= 0 || !TextureFormats.CanEncode(destination.Format) || destination.Memory == null) return;
            var device = source != null ? GlTextures.Prepare(worker, source) : null;
            bool autoMips = source?.AutoGenerateMips ?? false, screenRows = source?.ScreenRows ?? false;
            // A band of rows at a time (about a million pixels, as VkDevice reads).
            int band = Math.Clamp((1 << 20) / w, 1, h);
            for (int y0 = 0; y0 < h; y0 += band)
            {
                int rows = Math.Min(band, h - y0), bandY = y0;
                var pixels = worker.Call(() => ReadNow(device, autoMips, screenRows, rect.x, rect.y + bandY, w, rows));
                var mip = destination.Memory.Slice(destination.MipOffset(0), destination.MipSize(0));
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 4;
                        TextureFormats.Encode(destination.Format, mip, destination.Width, destX + x, destY + y0 + y, new Color(pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]));
                    }
            }
        }

        /// Reads as 8-bit RGBA straight into the array (a capture's band): a read that fails on
        /// the device thread counts as a failed command.
        public override void ReadPixelsRgba8(RenderTextureNative source, RectInt rect, byte[] destination, int offset, int stride)
        {
            if (rect.width <= 0 || rect.height <= 0) return;
            if (stride % 4 != 0 || offset < 0 || (long)offset + (long)stride * (rect.height - 1) + rect.width * 4L > destination.Length)
                throw new ArgumentOutOfRangeException(nameof(stride));
            var device = source != null ? GlTextures.Prepare(worker, source) : null;
            bool autoMips = source?.AutoGenerateMips ?? false, screenRows = source?.ScreenRows ?? false;
            int w = rect.width, h = rect.height;
            worker.TryCall(() =>
            {
                float[] pixels;
                try
                {
                    pixels = ReadNow(device, autoMips, screenRows, rect.x, rect.y, w, h);
                }
                catch (Exception e)
                {
                    CommandFailed(e);
                    return 0;
                }
                for (int y = 0; y < h; y++)
                {
                    var row = destination.AsSpan(offset + y * stride, w * 4);
                    for (int i = 0; i < row.Length; i++) row[i] = (byte)Math.Clamp((int)MathF.Round(pixels[y * w * 4 + i] * 255f), 0, 255);
                }
                return 0;
            }, out _);
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

        public override void FlushRecorded() => worker.Submit();

        // ----- Present -----

        public override void Present()
        {
            if (lost != null && !quitRequested)
            {
                quitRequested = true;
                string error = $"Direct3D 11 stopped working: {lost}, so the game quits. If this keeps happening, start the game without --renderer d3d11 "
                               + $"(Vulkan or OpenGL draws then). The log is {EngineLog.FilePath ?? Application.consoleLogPath}.";
                Debug.LogError(error);
                // On screen until the player closes it, once the window has gone (Player).
                PlayerLoop.RequestQuit(1, error);
            }
            int interval = SwapIntervalOverride >= 0 ? SwapIntervalOverride : QualitySettings.vSyncCount;
            // The window's size, which the buffers take, and the rendering size the next frame's
            // screen texture takes (smaller in a fullscreen window the game asked a smaller
            // resolution of: the frame is stretched over the window, D3d11Present).
            int width = platform.WindowFramebufferWidth, height = platform.WindowFramebufferHeight;
            int renderWidth = platform.FramebufferWidth, renderHeight = platform.FramebufferHeight;
            int frame = Time.frameCount;
            worker.Post(() => PresentNow(frame, interval, width, height, renderWidth, renderHeight));
            worker.Submit();
        }

        // ----- Diagnostics and shutdown -----

        public override string TakeQueueCounters(int frames) => GlDrawRecorder.TakeCounters(frames);

        public override string TakeFrameCounters(int n) => worker.Call(() =>
        {
            string text = D3d11Timings.Take(n) + $"; d3d11: draws {draws / n}, constant-buffer maps {constantMaps / n}, shader changes {shaderChanges / n}, programs {livePrograms.Count}, state objects {states.Made}, "
                          + $"textures {liveTextures.Count}, meshes {liveMeshes.Count}, texture uploads of changed rows {partialUploads / (double)n:F1}, unchanged {unchangedUploads / (double)n:F1}, debug-layer messages {debugMessages}";
            draws = constantMaps = shaderChanges = 0;
            partialUploads = unchangedUploads = 0;
            return text;
        });

        /// The debug layer's stored messages, read as ID3D11InfoQueue::GetMessage's documentation
        /// says (an index below GetNumStoredMessagesAllowedByRetrievalFilter, the size first), then
        /// cleared. Each is counted by its ID and `context` (the draw that made it, when drained
        /// right after a draw: DrainAfterDraw), and logged in full the first time that pair is
        /// seen; the counts are logged every 600 frames while they grow (DebugTally) and at the
        /// end. Messages the queue dropped past its message count limit are not stored:
        /// GetNumMessagesDiscardedByMessageCountLimit says how many, logged when it grows and
        /// counted in the run's total (whether ClearStoredMessages resets it is not documented,
        /// so a smaller value starts a new count). On the device thread once it runs.
        void DrainDebugMessages(string context = null)
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
                    // INFO and MESSAGE are not errors or warnings (done criterion 1): counted
                    // and logged by ID, apart (D3d11Context.SetFilters stores every message).
                    if (m->Severity >= D3D11_MESSAGE_SEVERITY.D3D11_MESSAGE_SEVERITY_INFO)
                    {
                        infoMessages++;
                        infoTally.TryGetValue((m->Severity, m->ID), out long count);
                        infoTally[(m->Severity, m->ID)] = count + 1;
                        if (count == 0) EngineLog.Engine($"d3d11 debug layer: {m->Severity} {m->ID} (first; counted apart): {new string(m->pDescription, 0, (int)m->DescriptionByteLength - 1)}");
                        continue;
                    }
                    debugMessages++;
                    var key = ($"{m->Severity} {m->ID}", context ?? "outside a draw");
                    debugTally.TryGetValue(key, out long seen);
                    debugTally[key] = seen + 1;
                    if (seen == 0 && debugTally.Count <= 500)
                        EngineLog.Engine($"d3d11 debug layer: {m->Severity} {m->ID} ({key.Item2}): {new string(m->pDescription, 0, (int)m->DescriptionByteLength - 1)}");
                }
            }
            ulong discarded = queue->lpVtbl->GetNumMessagesDiscardedByMessageCountLimit(queue);
            if (discarded != lastDiscarded)
            {
                ulong more = discarded > lastDiscarded ? discarded - lastDiscarded : discarded;
                discardedMessages += (long)more;
                debugMessages += (long)more;
                EngineLog.Engine($"d3d11 debug layer: {more} messages dropped past the message count limit (GetNumMessagesDiscardedByMessageCountLimit {discarded})");
                lastDiscarded = discarded;
            }
            if (n > 0) queue->lpVtbl->ClearStoredMessages(queue);
        }

        readonly Dictionary<(string Message, string Context), long> debugTally = new();
        readonly Dictionary<(D3D11_MESSAGE_SEVERITY, int), long> infoTally = new();
        long infoMessages;
        long debugTallied;

        /// The debug layer's counts by message and draw, when they grew (device thread).
        void DebugTally(bool always = false)
        {
            var queue = d3d.InfoQueue;
            ulong denied = queue != null ? queue->lpVtbl->GetNumMessagesDeniedByStorageFilter(queue) : 0;
            if (debugMessages == debugTallied && denied == deniedTallied && !always) return;
            debugTallied = debugMessages;
            deniedTallied = denied;
            EngineLog.Engine($"d3d11 debug layer tally: {debugMessages} messages: "
                             + string.Join("; ", System.Linq.Enumerable.Select(debugTally, kv => $"{kv.Key.Message} ({kv.Key.Context}) x{kv.Value}"))
                             + $"; {denied} messages denied by the storage filter (GetNumMessagesDeniedByStorageFilter: DEVICE_DRAW_RENDERTARGETVIEW_NOT_SET alone)"
                             + $"; {infoMessages} INFO or MESSAGE: " + string.Join("; ", System.Linq.Enumerable.Select(infoTally, kv => $"{kv.Key.Item1} {kv.Key.Item2} x{kv.Value}")));
        }

        ulong deniedTallied;

        ulong lastDiscarded;
        long discardedMessages;

        public override void StopForQuit()
        {
            StopPresenting();
            worker.Dispose();
            EndPresenting();
            DrainDebugMessages();
            DebugTally(always: true);
            EngineLog.Engine($"d3d11: {debugMessages} debug-layer messages in this run ({discardedMessages} of them dropped past the message count limit)");
            // A clean quit before the first frames were on screen is no crash (VkDevice's rule).
            if (lost == null) guard?.Clear();
        }

        void Shutdown()
        {
            // Out of DXGI's full-screen state on the device thread, before it stops (the chain
            // may not be released full screen: "DXGI overview", Destroying a swap chain). A crash
            // takes no such path: there is no hook that runs then with a live device thread.
            Leave();
            platform.AdoptDeviceExclusive(null);
            StopPresenting();
            worker.Dispose();
            // After the device thread has ended, as VkDevice.Shutdown: VkStartGuard.Clear is not
            // safe beside GuardFrame, and a loss found while the worker drains is seen.
            if (lost == null) guard?.Clear();
            EndPresenting();
            DrainDebugMessages();
            Ctx->lpVtbl->ClearState(Ctx);
            ReleasePresenter();
            ReleaseShared();
            exclusive?.Dispose();
            exclusive = null;
            chain.Dispose();
            Ctx->lpVtbl->Flush(Ctx);
            d3d.Dispose();
        }
    }

    /// Diagnostics (--frame-stats): the device thread's time by kind of work, as VkTimings, in
    /// the frame counts. Busy is GlWorker's (GlTimings.Busy: every handed-over batch), less the
    /// frame-latency wait after each frame's Present (FrameStatsWindow.PresenterQueueWait, the
    /// "device thread waiting for the presenter" of the main thread's waits). Present includes
    /// that wait. Timed only with frame stats on.
    internal static class D3d11Timings
    {
        public static long Draws, Constants, TextureUploads, MeshUploads, Targets, Present;
        static long waitTaken;

        public static long Start() => PlayerLoop.FrameStats ? GlTimings.Now : 0;

        public static void Add(ref long into, long started)
        {
            if (started != 0) into += GlTimings.Now - started;
        }

        public static string Take(int frames)
        {
            static string Ms(long ticks, int frames) => (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            long wait = System.Threading.Interlocked.Read(ref OniMoono.Engine.DeviceThreadStats.PresentWaitTicks);
            long waited = wait - waitTaken;
            waitTaken = wait;
            var text = $"d3d11 thread busy {Ms(GlTimings.Busy - waited, frames)}: draws {Ms(Draws, frames)} (constant-buffer writes {Ms(Constants, frames)}), texture uploads {Ms(TextureUploads, frames)}, "
                       + $"mesh uploads {Ms(MeshUploads, frames)}, targets {Ms(Targets, frames)}, present {Ms(Present - waited, frames)}; frame-latency wait {Ms(waited, frames)}";
            Draws = Constants = TextureUploads = MeshUploads = Targets = Present = 0;
            GlTimings.Busy = 0;
            return text;
        }
    }
}
