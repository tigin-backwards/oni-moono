using OniMoono.Engine.Rendering;

namespace OniMoono.Desktop.Gl
{
    /// The resource work recording posts for the device thread (GlWorker.Resources): what the
    /// main thread records about textures, meshes and programs is the same for every device
    /// (GlTextures.Prepare, GlMeshes.Prepare, GlDrawRecorder), what executes it is the device's.
    /// Every method runs on the device thread.
    internal interface IResourceWork
    {
        /// (Re)creates a render texture's surfaces for its shape.
        void CreateRenderTexture(DeviceTexture texture, RenderShape shape, string name);

        /// Uploads texels into a texture and frees them.
        void UploadTexture(DeviceTexture texture, TexelUpload upload);

        void ApplySampler(DeviceTexture texture, GlTextures.SamplerSettings sampler);

        void UploadMesh(DeviceMesh mesh, MeshUpload upload);

        /// The shader's program for a variant blob, built on first use (its Id is 0 when it
        /// cannot be built).
        DeviceProgram Program(ShaderNative shader, int blobIndex);
    }

    /// One texture upload, sampler change or mesh upload recorded for the device thread. It
    /// comes from the frame's arena (FrameArena.Command) with its delegate made once, so
    /// recording one allocates nothing: a closure and a delegate each were about a tenth of a
    /// large colony's allocations a frame. It lets go of what it refers to when it runs.
    internal sealed class ResourceCommand
    {
        internal enum Kind { UploadTexture, ApplySampler, UploadMesh }

        public readonly System.Action Run;
        Kind kind;

        /// What the command was last recorded to do (tests).
        internal Kind Recorded => kind;
        IResourceWork work;
        DeviceTexture texture;
        TexelUpload upload;
        GlTextures.SamplerSettings sampler;
        DeviceMesh mesh;
        MeshUpload meshUpload;
        /// The upload record's generation when the command was recorded: records come from the
        /// frame's arena (FrameArena.Record), and one handed out again before the command ran
        /// would hold another upload's data.
        int generation;

        /// Upload records found with another generation than their command's (device thread).
        internal static long StaleRecords;

        public ResourceCommand() => Run = Execute;

        public System.Action UploadTexture(IResourceWork work, DeviceTexture texture, TexelUpload upload)
        {
            (kind, this.work, this.texture, this.upload, generation) = (Kind.UploadTexture, work, texture, upload, upload.Generation);
            return Run;
        }

        public System.Action ApplySampler(IResourceWork work, DeviceTexture texture, GlTextures.SamplerSettings sampler)
        {
            (kind, this.work, this.texture, this.sampler) = (Kind.ApplySampler, work, texture, sampler);
            return Run;
        }

        public System.Action UploadMesh(IResourceWork work, DeviceMesh mesh, MeshUpload upload)
        {
            (kind, this.work, this.mesh, meshUpload, generation) = (Kind.UploadMesh, work, mesh, upload, upload.Generation);
            return Run;
        }

        void Execute()
        {
            var (w, t, u, s, m, mu) = (work, texture, upload, sampler, mesh, meshUpload);
            (work, texture, upload, mesh, meshUpload) = (null, null, null, null, null);
            int recorded = kind switch { Kind.UploadTexture => u.Generation, Kind.UploadMesh => mu.Generation, _ => generation };
            if (recorded != generation)
            {
                long n = System.Threading.Interlocked.Increment(ref StaleRecords);
                OniMoono.Engine.EngineLog.Engine($"upload record wrong ({n}): a {(kind == Kind.UploadMesh ? "mesh" : "texture")} upload's record was handed out again before it ran (generation {recorded}, given {generation})");
                return;
            }
            switch (kind)
            {
                case Kind.UploadTexture: w.UploadTexture(t, u); break;
                case Kind.ApplySampler: w.ApplySampler(t, s); break;
                case Kind.UploadMesh: w.UploadMesh(m, mu); break;
            }
        }
    }

    /// A device's own work with its state, recorded for the device thread (FrameArena.Command):
    /// as a ResourceCommand, reused with its delegate, and it lets go of both when it runs.
    internal sealed class StateCommand<T>
    {
        public readonly System.Action Run;
        System.Action<T> work;
        T state;

        public StateCommand() => Run = Execute;

        public System.Action Set(System.Action<T> work, T state)
        {
            (this.work, this.state) = (work, state);
            return Run;
        }

        void Execute()
        {
            var (w, s) = (work, state);
            (work, state) = (null, default);
            w(s);
        }
    }

    /// The OpenGL device's resource work.
    internal sealed class GlResourceWork : IResourceWork
    {
        public static readonly GlResourceWork Instance = new();

        public void CreateRenderTexture(DeviceTexture texture, RenderShape shape, string name)
        {
            long t = GlTimings.Now;
            GlTextures.EnsureRenderTexture(texture, shape, name);
            if (GlBatchProfile.Enabled) GlBatchProfile.Add($"render target {name}", GlTimings.Now - t);
            GlTimings.Targets += GlTimings.Now - t;
        }

        public void UploadTexture(DeviceTexture texture, TexelUpload upload)
        {
            long t = GlTimings.Now;
            uint q = GlGpuProfile.Enabled ? GlGpuProfile.Begin() : 0;
            var (key, bytes) = GlBatchProfile.Enabled ? ($"texture {(texture.Id == 0 ? "made" : "updated")} {upload.Width}x{upload.Height} {upload.Format}", upload.Memory?.Size ?? 0) : (null, 0);
            upload.Execute(texture);
            if (key != null) GlBatchProfile.Add(key, GlTimings.Now - t, bytes);
            if (GlGpuProfile.Enabled) GlGpuProfile.End($"texture upload {upload.Width}x{upload.Height} {upload.Format}", q);
            GlTimings.TextureUploads += GlTimings.Now - t;
            System.Threading.Interlocked.Add(ref OniMoono.Engine.StartupStats.GlUploadTicks, GlTimings.Now - t);
        }

        public void ApplySampler(DeviceTexture texture, GlTextures.SamplerSettings sampler) => GlTextures.ApplySamplerNow(sampler, texture);

        public void UploadMesh(DeviceMesh mesh, MeshUpload upload)
        {
            long t = GlTimings.Now;
            uint q = GlGpuProfile.Enabled ? GlGpuProfile.Begin() : 0;
            try
            {
                if (MeshNative.VerifyReuse) MeshReuseVerify.Uploading(upload);
                string key = GlBatchProfile.Enabled ? GlBatchProfile.MeshKey(mesh.IndexBuffer == 0, upload.Mesh?.Name, upload.VertexCount) : null;
                GlMeshes.Upload(mesh, upload);
                if (key != null) GlBatchProfile.Add(key, GlTimings.Now - t);
            }
            finally
            {
                upload.Ran();
            }
            if (GlGpuProfile.Enabled) GlGpuProfile.End("mesh upload", q);
            GlTimings.MeshUploads += GlTimings.Now - t;
        }

        public DeviceProgram Program(ShaderNative shader, int blobIndex) => GlPrograms.Get(shader, blobIndex);
    }
}
