using System.Collections.Generic;
using System.Linq;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// A mesh's first upload queued ahead of its first draw (GlAhead, GlMeshes.PrepareAhead):
    /// the draw that comes first posts it into its frame ahead of anything recorded after, and
    /// a released mesh's never uploads.
    public class AheadOfUseTests : EngineTest
    {
        static Mesh Quad(int vertices)
        {
            var mesh = new Mesh();
            mesh.SetVertices(Enumerable.Range(0, vertices).Select(i => new Vector3(i, 0f, 0f)).ToList());
            mesh.SetTriangles(new List<int> { 0, 1, 2 }, 0, calculateBounds: false);
            return mesh;
        }

        [Fact]
        public void TheFirstDrawPostsTheUploadQueuedAhead()
        {
            var worker = new GlWorker();
            var mesh = Quad(4);
            var m = mesh.Data;
            GlMeshes.PrepareAhead(worker, m);
            var d = (DeviceMesh)m.Gpu;
            var queued = d.Ahead;
            Assert.NotNull(queued);
            // Recorded but not run: no frame may draw it as it is.
            Assert.False(GlMeshes.IsCurrent(m));
            Assert.Same(d, GlMeshes.Prepare(worker, m));
            Assert.Null(d.Ahead);
            Assert.True(GlMeshes.IsCurrent(m));
            Assert.Equal(new System.Action[] { queued }, worker.Recording.Commands);
            // A change after it: its upload follows the queued one.
            mesh.SetVertices(Enumerable.Range(0, 6).Select(i => new Vector3(i, 1f, 0f)).ToList());
            GlMeshes.Prepare(worker, m);
            Assert.Equal(2, worker.Recording.Commands.Count);
            Assert.Same(queued, worker.Recording.Commands[0]);
            // A mesh with its device mesh is not queued again.
            GlMeshes.PrepareAhead(worker, m);
            Assert.Null(d.Ahead);
        }

        [Fact]
        public void AReleasedMeshIsNotUploaded()
        {
            var worker = new GlWorker();
            var m = Quad(4).Data;
            GlMeshes.PrepareAhead(worker, m);
            var d = (DeviceMesh)m.Gpu;
            d.Deleted = true;
            d.Ahead();
            Assert.Equal(0u, d.IndexBuffer);
            // Its arrays are free to reuse all the same.
            Assert.Equal(m.UploadsPosted, m.UploadsRan);
        }
    }
}
