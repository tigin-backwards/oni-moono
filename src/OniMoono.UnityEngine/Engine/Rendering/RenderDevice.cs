using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace OniMoono.Engine.Rendering
{
    /// What the graphics device is and can do (SystemInfo). The OpenGL backend reports the real
    /// device; without graphics the Null device answers like Unity's -nographics player.
    internal abstract class GraphicsCaps
    {
        public abstract GraphicsDeviceType DeviceType { get; }
        public abstract string DeviceName { get; }
        public abstract string DeviceVendor { get; }
        public abstract string DeviceVersion { get; }
        public abstract int DeviceId { get; }
        public abstract int VendorId { get; }
        public abstract int MemorySizeMB { get; }
        public abstract int ShaderLevel { get; }
        public abstract int MaxTextureSize { get; }
        public abstract int MaxRenderTargets { get; }
        public abstract bool Supports2DArrayTextures { get; }
        public abstract bool Supports3DTextures { get; }
        public abstract bool SupportsInstancing { get; }
        public abstract bool SupportsComputeShaders { get; }
        public abstract bool SupportsTexture(TextureFormat format);
        public abstract bool SupportsRenderTexture(RenderTextureFormat format);
    }

    internal sealed class NullCaps : GraphicsCaps
    {
        public override GraphicsDeviceType DeviceType => GraphicsDeviceType.Null;
        public override string DeviceName => "Null Device";
        public override string DeviceVendor => "Unity Technologies";
        public override string DeviceVersion => "NULL 1.0 [1.0]";
        public override int DeviceId => 0;
        public override int VendorId => 0;
        public override int MemorySizeMB => 0;
        public override int ShaderLevel => 50;
        public override int MaxTextureSize => 16384;
        public override int MaxRenderTargets => 8;
        public override bool Supports2DArrayTextures => true;
        public override bool Supports3DTextures => true;
        public override bool SupportsInstancing => false;
        public override bool SupportsComputeShaders => false;
        // The null device accepts every format (nothing is created on a GPU).
        public override bool SupportsTexture(TextureFormat format) => true;
        public override bool SupportsRenderTexture(RenderTextureFormat format) => true;
    }

    /// The graphics device: owns GPU resources and executes rendering work. Engine objects keep
    /// their data on the CPU side (textures, meshes, materials) with version counters; the device
    /// creates and updates its resources from them when they are used, and keeps its handle in
    /// the object's `Gpu` slot.
    internal abstract class RenderDevice
    {
        public abstract GraphicsCaps Caps { get; }

        /// The ShaderCompilerPlatform whose programs the device runs (ShaderNative.ProgramsFor):
        /// OpenGLCore unless a device says otherwise.
        public virtual uint ShaderPlatform => ShaderNative.OpenGLCorePlatform;

        /// False for the null device: the player renders no frames (cameras and overlay canvases
        /// are not drawn), as Unity's -nographics player.
        public abstract bool RendersFrames { get; }

        /// Device commands that failed so far (a render texture the driver could not make, and
        /// the like); each was logged.
        public virtual long FailedCommands => 0;

        /// Copies pixels from a render target (null: the back buffer) into a texture's CPU data.
        public abstract void ReadPixels(RenderTextureNative source, RectInt rect, Texture2DNative destination, int destX, int destY);

        /// Frees the device resource held in an object's Gpu slot.
        public abstract void ReleaseResource(object gpu);

        /// Makes a texture resident on the device with its current content.
        public virtual void Upload(TextureNative texture) { }

        /// Activates a material pass for immediate drawing (GL, Graphics.DrawMeshNow). The pass
        /// index has been validated against the material's shader.
        public abstract bool SetPass(Material material, int pass);

        /// Compiles every shader program in advance (Shader.WarmupAllShaders).
        public abstract void WarmupShaders();

        /// Subsequent drawing goes to these surfaces.
        public abstract void SetRenderTarget(in RenderTargetBinding target);

        /// Viewport in pixels of the current target.
        public abstract void SetViewport(Rect pixels);

        /// Restricts drawing to a pixel rectangle of the target (origin bottom left); null draws
        /// everywhere. Clears are not restricted.
        public abstract void SetScissor(Rect? pixels);

        public abstract void Clear(bool color, bool depth, Color backgroundColor, float depthValue);

        /// View and projection (Unity convention; the device applies its clip-space rules) for
        /// the following draws, with the camera's built-in shader constants.
        public void SetCamera(in CameraConstants camera)
        {
            Camera = camera;
            ApplyCamera(camera);
        }

        /// The constants the following draws use (the last SetCamera).
        public CameraConstants Camera { get; private set; }

        protected abstract void ApplyCamera(in CameraConstants camera);

        /// Draws one submesh with a material pass (-1: every pass), the renderer's property
        /// block (may be null) and global shader state.
        public abstract void Draw(in DrawRequest draw);

        /// Draws a queue's items in order, as Draw does each (pass -1 unless the item names one).
        /// No game code runs between them, so nothing they read changes until the last is drawn:
        /// a device may record them in parallel, with the same result.
        public virtual void DrawQueue(List<DrawItem> items)
        {
            foreach (var d in items) Draw(new DrawRequest(d.Mesh, d.SubMesh, d.ObjectToWorld, d.Material, d.Pass, d.Properties, d.Keywords));
        }

        /// Diagnostics (ONI_RENDER_TIMING): how the device recorded its draw queues since the
        /// last call; null when it has nothing to say.
        public virtual string TakeQueueCounters(int frames) => null;

        /// Draws GL immediate-mode geometry with the pass activated by SetPass.
        public abstract void DrawImmediate(ImmediatePrimitive primitive, ReadOnlySpan<ImmediateVertex> vertices, Matrix4x4 modelView, Matrix4x4 projection);

        /// Draws a mesh's sub-mesh (-1: every sub-mesh, in order) with the pass activated by
        /// SetPass, placed by objectToWorld under that view and projection (Graphics.DrawMeshNow).
        public abstract void DrawMeshNow(MeshNative mesh, int subMesh, Matrix4x4 objectToWorld, Matrix4x4 view, Matrix4x4 projection);

        /// Shows the frame (swap buffers).
        public abstract void Present();

        /// Hands the work recorded so far to the device's thread without ending the frame
        /// (a device that records nothing does nothing).
        public virtual void FlushRecorded() { }

        /// Takes a texture loaded from an asset file to upload in the background, in load order,
        /// instead of before the next frame renders; a draw that samples it first uploads it at
        /// once (main thread). False when the device does not (then Upload does it now).
        public virtual bool UploadInBackground(TextureNative texture) => false;

        /// Diagnostics: the average GPU time per presented frame since the last call, in
        /// milliseconds; -1 when unknown. The first call starts the measurement.
        public virtual double TakeGpuFrameMilliseconds() => -1;

        /// Diagnostics: device work per frame (draws, binds) averaged over `frames`, since the
        /// last call; null when not counted.
        public virtual string TakeFrameCounters(int frames) => null;

        /// Diagnostics: the video memory the driver reports free (and, where it counts them,
        /// evictions to system memory); null when the driver reports none.
        public virtual string MemoryCounters() => null;
    }

    /// The colour and depth surfaces drawing goes to; null colours are the back buffer.
    internal readonly record struct RenderTargetBinding(RenderTextureNative[] Colors, RenderTextureNative Depth, int MipLevel)
    {
        public static readonly RenderTargetBinding BackBuffer = new(null, null, 0);
        public static RenderTargetBinding Of(RenderTextureNative texture) => texture == null ? BackBuffer : new([texture], texture, 0);
    }

    internal readonly record struct CameraConstants(Matrix4x4 View, Matrix4x4 Projection, Vector3 Position, float Near, float Far, bool Orthographic, Vector2 TargetSize);

    /// Keywords: enabled in addition to the material's and the global ones (UI rect clipping).
    /// Pass -1 draws the forward-rendering passes of the active subshader, or every pass when
    /// AllPasses is set (Graphics.Blit).
    internal readonly record struct DrawRequest(MeshNative Mesh, int SubMesh, Matrix4x4 ObjectToWorld, Material Material, int Pass, PropertySheet Properties, string[] Keywords = null, bool AllPasses = false);

    /// GL.Begin modes (GL.TRIANGLES = 4, TRIANGLE_STRIP = 5, QUADS = 7, LINES = 1, LINE_STRIP = 2).
    internal enum ImmediatePrimitive
    {
        Lines = 1,
        LineStrip = 2,
        Triangles = 4,
        TriangleStrip = 5,
        Quads = 7,
    }

    internal struct ImmediateVertex
    {
        public Vector3 Position;
        public Color Color;
        /// Texture coordinates per unit (GL.MultiTexCoord); unit 0 is GL.TexCoord.
        public Vector4 Uv0, Uv1, Uv2, Uv3, Uv4, Uv5, Uv6, Uv7;
    }

    /// Unity's null graphics device (-nographics): it accepts all work and produces nothing; no
    /// GPU resources are ever created.
    internal sealed class NullRenderDevice : RenderDevice
    {
        public override GraphicsCaps Caps { get; } = new NullCaps();
        public override bool RendersFrames => false;

        // There is no framebuffer to read: the destination keeps its contents.
        public override void ReadPixels(RenderTextureNative source, RectInt rect, Texture2DNative destination, int destX, int destY) { }

        public override void ReleaseResource(object gpu) { }

        public override bool SetPass(Material material, int pass) => true;

        // The null device has no programs to compile.
        public override void WarmupShaders() { }

        // The null device accepts all rendering work and produces no output.
        public override void SetRenderTarget(in RenderTargetBinding target) { }
        public override void SetViewport(Rect pixels) { }
        public override void SetScissor(Rect? pixels) { }
        public override void Clear(bool color, bool depth, Color backgroundColor, float depthValue) { }
        protected override void ApplyCamera(in CameraConstants camera) { }
        public override void Draw(in DrawRequest draw) { }
        public override void DrawImmediate(ImmediatePrimitive primitive, ReadOnlySpan<ImmediateVertex> vertices, Matrix4x4 modelView, Matrix4x4 projection) { }
        public override void DrawMeshNow(MeshNative mesh, int subMesh, Matrix4x4 objectToWorld, Matrix4x4 view, Matrix4x4 projection) { }
        public override void Present() { }
    }

    internal static class GraphicsDevice
    {
        public static RenderDevice Current { get; private set; } = new NullRenderDevice();

        public static GraphicsCaps Caps => Current.Caps;

        public static void Install(RenderDevice device) => Current = device;

        /// Device resources by the engine object whose Gpu slot holds them. An owner the garbage
        /// collector reclaims without being destroyed (an asset reclaimed by
        /// Resources.UnloadUnusedAssets, a renderer's private mesh) has its resource released by
        /// ReleaseOrphans. Main thread only, like all device work.
        static readonly Dictionary<object, WeakReference<object>> owners = new(ReferenceEqualityComparer.Instance);

        /// Records that `owner`'s Gpu slot now holds `gpu` (called by the device on creation).
        public static void Adopt(object owner, object gpu) => owners[gpu] = new WeakReference<object>(owner);

        /// Releases an object's device resource and clears the slot.
        public static void Release(ref object gpu)
        {
            if (gpu == null) return;
            owners.Remove(gpu);
            Current.ReleaseResource(gpu);
            gpu = null;
        }

        /// Textures loaded since the last frame, uploaded before it renders (any thread may load).
        static readonly System.Collections.Concurrent.ConcurrentQueue<WeakReference<TextureNative>> pendingUploads = new();

        public static void QueueUpload(TextureNative texture)
        {
            if (!Current.RendersFrames) return;
            // Loaded on the main thread: handed to the device's background uploads at once, so
            // they go up while loading goes on (a DLC bundle's, during the game's start-up).
            if (Threads.IsMainThread && Current.UploadInBackground(texture)) return;
            pendingUploads.Enqueue(new WeakReference<TextureNative>(texture));
        }

        const int ParallelReadThreshold = 32;
        internal static readonly bool Pipelined = Environment.GetEnvironmentVariable("ONI_UPLOAD_PIPELINE") != "0";
        const long FlushBytes = 64L << 20;

        /// Uploads the textures loaded since the last call (main thread).
        public static void UploadPending()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            if (pendingUploads.IsEmpty) return;
            var textures = new List<TextureNative>();
            while (pendingUploads.TryDequeue(out var weak))
                if (weak.TryGetTarget(out var texture) && !texture.Destroyed && !Current.UploadInBackground(texture)) textures.Add(texture);
            // Many textures at once (a scene or bundle load): their texels are read from the
            // asset files on worker threads first. Each texture's own reads, nothing else.
            if (Pipelined && textures.Count >= ParallelReadThreshold)
            {
                var unread = new List<TexelTextureNative>();
                foreach (var t in textures)
                    if (t is TexelTextureNative tt && tt.TexelsUnread) unread.Add(tt);
                System.Threading.Tasks.Parallel.ForEach(unread, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
                    t => _ = t.Memory);
            }
            long sinceFlush = 0;
            foreach (var texture in textures)
            {
                long bytes = texture is TexelTextureNative t ? t.SliceSize * t.Depth : 0;
                if (StartupStats.Enabled)
                {
                    StartupStats.Uploads++;
                    StartupStats.UploadBytes += bytes;
                }
                Current.Upload(texture);
                Platform.MainThreadPump.Maybe();
                // Large batches go to the device as they are recorded, so it uploads while the
                // rest are recorded.
                sinceFlush += bytes;
                if (Pipelined && sinceFlush >= FlushBytes)
                {
                    Current.FlushRecorded();
                    sinceFlush = 0;
                }
            }
            StartupStats.UploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }

        static int collectionsAtLastScan = -1;

        /// Releases the resources of owners that no longer exist. Returns how many. An owner can
        /// only stop existing in a garbage collection, so the owners are looked at only when one
        /// has run since the last look.
        public static int ReleaseOrphans()
        {
            int collections = GC.CollectionCount(0);
            if (collections == collectionsAtLastScan) return 0;
            collectionsAtLastScan = collections;
            List<object> orphans = null;
            foreach (var (gpu, owner) in owners)
                if (!owner.TryGetTarget(out _)) (orphans ??= new()).Add(gpu);
            if (orphans == null) return 0;
            foreach (var gpu in orphans)
            {
                owners.Remove(gpu);
                Current.ReleaseResource(gpu);
            }
            return orphans.Count;
        }
    }
}
