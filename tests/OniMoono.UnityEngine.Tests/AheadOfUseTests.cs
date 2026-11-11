using System.Collections.Generic;
using System.Linq;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// A mesh's first upload queued ahead of its first draw (GlAhead, GlMeshes.PrepareAhead):
    /// the draw that comes first posts it into its frame ahead of anything recorded after, it
    /// runs once whichever runs it first, and not at all for a released mesh.
    public class AheadOfUseTests : EngineTest
    {
        sealed class Work : IResourceWork
        {
            public readonly List<(DeviceMesh Mesh, int Vertices)> Meshes = new();
            public void CreateRenderTexture(DeviceTexture texture, RenderShape shape, string name) { }
            public void UploadTexture(DeviceTexture texture, TexelUpload upload) { }
            public void ApplySampler(DeviceTexture texture, GlTextures.SamplerSettings sampler) { }
            public void UploadMesh(DeviceMesh mesh, MeshUpload upload)
            {
                Meshes.Add((mesh, upload.VertexCount));
                upload.Ran();
            }
            public DeviceProgram Program(ShaderNative shader, int blobIndex) => null;
        }

        static Mesh Quad(int vertices)
        {
            var mesh = new Mesh();
            mesh.SetVertices(Enumerable.Range(0, vertices).Select(i => new Vector3(i, 0f, 0f)).ToList());
            mesh.SetTriangles(new List<int> { 0, 1, 2 }, 0, calculateBounds: false);
            return mesh;
        }

        static void RunFrame(GlWorker worker)
        {
            foreach (var command in worker.Recording.Commands) command();
            worker.Recording.Reset();
        }

        [Fact]
        public void TheFirstDrawPostsTheUploadQueuedAheadOnce()
        {
            var work = new Work();
            var worker = new GlWorker { Resources = work };
            var m = Quad(4).Data;
            GlMeshes.PrepareAhead(worker, m);
            var d = (DeviceMesh)m.Gpu;
            var queued = d.Ahead;
            Assert.NotNull(queued);
            // Recorded but not run: no frame may draw it as it is.
            Assert.False(GlMeshes.IsCurrent(m));
            Assert.Same(d, GlMeshes.Prepare(worker, m));
            Assert.Null(d.Ahead);
            Assert.True(GlMeshes.IsCurrent(m));
            Assert.Single(worker.Recording.Commands);
            RunFrame(worker);
            Assert.Equal(new[] { (d, 4) }, work.Meshes);
            // The queued job, its turn after the frame: nothing more.
            queued();
            Assert.Single(work.Meshes);
            // A mesh with its device mesh is not queued again.
            GlMeshes.PrepareAhead(worker, m);
            Assert.Null(d.Ahead);
        }

        [Fact]
        public void AChangeAfterTheQueuedUploadIsUploadedAfterIt()
        {
            var work = new Work();
            var worker = new GlWorker { Resources = work };
            var mesh = Quad(4);
            var m = mesh.Data;
            GlMeshes.PrepareAhead(worker, m);
            var d = (DeviceMesh)m.Gpu;
            var queued = d.Ahead;
            mesh.SetVertices(Enumerable.Range(0, 6).Select(i => new Vector3(i, 1f, 0f)).ToList());
            GlMeshes.Prepare(worker, m);
            Assert.Equal(2, worker.Recording.Commands.Count);
            RunFrame(worker);
            Assert.Equal(new[] { (d, 4), (d, 6) }, work.Meshes);
            // The idle thread's turn comes later: the stale upload does not run again.
            queued();
            Assert.Equal(2, work.Meshes.Count);
        }

        [Fact]
        public void AReleasedMeshIsNotUploaded()
        {
            var work = new Work();
            var worker = new GlWorker { Resources = work };
            var m = Quad(4).Data;
            GlMeshes.PrepareAhead(worker, m);
            var d = (DeviceMesh)m.Gpu;
            int posted = m.UploadsPosted;
            d.Deleted = true;
            d.Ahead();
            Assert.Empty(work.Meshes);
            // Its arrays are free to reuse all the same.
            Assert.Equal(posted, m.UploadsRan);
        }
    }
}
