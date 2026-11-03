using System.Collections.Generic;
using System.Linq;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// The mesh setters' channel arrays: a mesh rebuilt every frame (the conduit flow meshes)
    /// reuses the arrays it let go of, but only once the device has run every upload that could
    /// still read them, and never an array a setter did not make.
    public class MeshChannelReuseTests : EngineTest
    {
        static List<Vector3> Positions(int n, float at) => Enumerable.Range(0, n).Select(i => new Vector3(i + at, at, 0f)).ToList();

        static void Rebuild(Mesh mesh, int n, float at)
        {
            mesh.Clear();
            mesh.SetVertices(Positions(n, at));
            mesh.SetUVs(0, Enumerable.Range(0, n).Select(i => new Vector4(i, at, 1f, 0f)).ToList());
            mesh.SetColors(Enumerable.Range(0, n).Select(i => new Color32((byte)i, 2, 3, 4)).ToList());
            mesh.SetTriangles(Enumerable.Range(0, n / 4 * 6).Select(i => i % n).ToList(), 0, calculateBounds: false);
        }

        static void AssertContent(Mesh mesh, int n, float at)
        {
            Assert.Equal(Positions(n, at), mesh.vertices);
            var uvs = new List<Vector4>();
            mesh.GetUVs(0, uvs);
            Assert.Equal(Enumerable.Range(0, n).Select(i => new Vector4(i, at, 1f, 0f)), uvs);
            Assert.Equal(Enumerable.Range(0, n / 4 * 6).Select(i => i % n), mesh.GetTriangles(0));
        }

        [Fact]
        public void ArraysAreReusedOnlyAfterTheUploadHoldingThemRan()
        {
            var mesh = new Mesh();
            var m = mesh.Data;
            Rebuild(mesh, 8, 1f);
            var first = (m.Positions, m.Uvs[0], m.Colors32, m.SubMeshes[0].Indices);
            // The device records an upload of these arrays (GlMeshes.Prepare) and has not run it.
            int upload = ++m.UploadsPosted;
            Rebuild(mesh, 8, 2f);
            Assert.NotSame(first.Positions, m.Positions);
            Assert.NotSame(first.Item2, m.Uvs[0]);
            Assert.NotSame(first.Colors32, m.Colors32);
            Assert.NotSame(first.Indices, m.SubMeshes[0].Indices);
            AssertContent(mesh, 8, 2f);
            // The upload left the first arrays as they were.
            Assert.Equal(Positions(8, 1f), first.Positions);
            m.UploadRan(upload);
            var second = m.Positions;
            Rebuild(mesh, 8, 3f);
            Assert.True(ReferenceEquals(m.Positions, first.Positions) || ReferenceEquals(m.Positions, second));
            Assert.True(ReferenceEquals(m.SubMeshes[0].Indices, first.Indices) || m.SubMeshes[0].Indices.Length == 12);
            AssertContent(mesh, 8, 3f);
        }

        [Fact]
        public void ARebuildWithoutUploadsAllocatesNoChannelArrays()
        {
            var mesh = new Mesh();
            var positions = Positions(64, 1f);
            var uvs = new List<Vector2>(new Vector2[64]);
            var colors = new List<Color32>(new Color32[64]);
            var indices = Enumerable.Range(0, 96).Select(i => i % 64).ToList();
            void Build()
            {
                mesh.Clear();
                mesh.SetVertices(positions);
                mesh.SetUVs(0, uvs);
                mesh.SetColors(colors);
                mesh.SetTriangles(indices, 0);
            }
            Build();
            Build();
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            Build();
            long bytes = System.GC.GetAllocatedBytesForCurrentThread() - before;
            // The channels alone would be 64 * (12 + 16 + 4) + 96 * 4 bytes.
            Assert.True(bytes < 1024, $"{bytes} bytes");
        }

        [Fact]
        public void ArraysTheSettersDidNotMakeAreNeverReused()
        {
            var mesh = new Mesh();
            var m = mesh.Data;
            Rebuild(mesh, 4, 1f);
            // Engine code that puts its own array into a mesh (TextMesh, the canvases) keeps it.
            var own = new Vector3[4];
            m.Positions = own;
            Rebuild(mesh, 4, 2f);
            Rebuild(mesh, 4, 3f);
            Assert.NotSame(own, m.Positions);
            Assert.Equal(new Vector3[4], own);
        }

        [Fact]
        public void PartialRangesCopyOnlyTheirElements()
        {
            var mesh = new Mesh();
            mesh.SetVertices(Positions(6, 0f));
            mesh.SetUVs(0, new List<Vector2> { new(9, 9), new(1, 2), new(3, 4), new(5, 6), new(7, 8), new(0, 1), new(9, 9) }, 1, 6);
            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);
            Assert.Equal(new Vector2[] { new(1, 2), new(3, 4), new(5, 6), new(7, 8), new(0, 1) }.Append(new Vector2(9, 9)), uvs);
            mesh.SetUVs(1, new Vector3[] { new(1, 2, 3), new(4, 5, 6), new(1, 1, 1), new(2, 2, 2), new(3, 3, 3), new(4, 4, 4), new(5, 5, 5) }, 1, 6);
            var uv1 = new List<Vector3>();
            mesh.GetUVs(1, uv1);
            Assert.Equal(new Vector3(4, 5, 6), uv1[0]);
            Assert.Equal(new Vector3(5, 5, 5), uv1[5]);
            mesh.SetTriangles(new List<int> { 0, 1, 2, 3, 4, 5 }, 0);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, mesh.triangles);
            // Out-of-range indices still fail as before.
            mesh.SetTriangles(new List<int> { 0, 1, 9 }, 0);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, mesh.triangles);
        }
    }
}
