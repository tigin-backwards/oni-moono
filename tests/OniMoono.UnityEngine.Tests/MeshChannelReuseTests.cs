using System;
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
            var first = (m.Positions, m.Uvs[0], m.Colors32, Indices: m.SubMeshes[0].IndexArray);
            // The device records an upload of these arrays (GlMeshes.Prepare) and has not run it.
            int upload = ++m.UploadsPosted;
            Rebuild(mesh, 8, 2f);
            Assert.NotSame(first.Positions, m.Positions);
            Assert.NotSame(first.Item2, m.Uvs[0]);
            Assert.NotSame(first.Colors32, m.Colors32);
            Assert.NotSame(first.Indices, m.SubMeshes[0].IndexArray);
            AssertContent(mesh, 8, 2f);
            // The upload left the first arrays as they were.
            Assert.Equal(Positions(8, 1f), first.Positions);
            m.UploadRan(upload);
            var second = m.Positions;
            Rebuild(mesh, 8, 3f);
            Assert.True(ReferenceEquals(m.Positions, first.Positions) || ReferenceEquals(m.Positions, second));
            Assert.True(ReferenceEquals(m.SubMeshes[0].IndexArray, first.Indices) || m.SubMeshes[0].IndexCount == 12);
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

        /// A rebuild with another vertex count, once the device has run the uploads, after a
        /// larger one.
        static Mesh Shrunk(out MeshNative m)
        {
            var mesh = new Mesh();
            m = mesh.Data;
            Rebuild(mesh, 16, 1f);
            m.UploadRan(++m.UploadsPosted);
            Rebuild(mesh, 16, 2f);
            m.UploadRan(++m.UploadsPosted);
            Rebuild(mesh, 12, 3f);
            return mesh;
        }

        [Fact]
        public void AMeshRebuiltSmallerReusesItsLargerArrays()
        {
            var mesh = Shrunk(out var m);
            // The setters' arrays hold more than the vertex count: the first 12 are the mesh's.
            Assert.Equal(12, m.VertexCount);
            Assert.True(m.Positions.Length >= 12);
            Assert.True(m.Positions.Length > 12 || m.Uvs[0].Length > 12 || m.Colors32.Length > 12, "no array was reused");
            Assert.Equal(12 / 4 * 6, m.SubMeshes[0].IndexCount);
            AssertContent(mesh, 12, 3f);
        }

        [Fact]
        public void GettersReturnNewArraysOfExactlyTheVertexCount()
        {
            var mesh = Shrunk(out var m);
            mesh.SetNormals(Enumerable.Range(0, 12).Select(i => Vector3.up * i).ToList());
            mesh.SetTangents(Enumerable.Range(0, 12).Select(i => Vector4.one * i).ToList());
            mesh.SetUVs(1, Enumerable.Range(0, 12).Select(i => new Vector2(i, -i)).ToList());
            var vertices = mesh.vertices;
            Assert.Equal(12, vertices.Length);
            Assert.NotSame(m.Positions, vertices);
            vertices[0] = new Vector3(99f, 99f, 99f);
            Assert.Equal(Positions(12, 3f), mesh.vertices);
            Assert.Equal(12, mesh.normals.Length);
            Assert.Equal(Vector3.up * 11, mesh.normals[11]);
            Assert.Equal(12, mesh.tangents.Length);
            Assert.Equal(12, mesh.colors32.Length);
            Assert.Equal(new Color32(11, 2, 3, 4), mesh.colors32[11]);
            Assert.Equal(12, mesh.colors.Length);
            Assert.Equal(12, mesh.uv.Length);
            Assert.Equal(12, mesh.uv2.Length);
            Assert.Equal(new Vector2(11, -11), mesh.uv2[11]);
            var list = new List<Vector3>();
            mesh.GetVertices(list);
            Assert.Equal(12, list.Count);
            var uvs = new List<Vector2>();
            mesh.GetUVs(1, uvs);
            Assert.Equal(12, uvs.Count);
            Assert.Equal(18, mesh.triangles.Length);
            Assert.Equal(18, mesh.GetIndices(0).Length);
            Assert.Equal(12, mesh.vertexCount);
        }

        [Fact]
        public void ACopyOfTheMeshHoldsOnlyItsData()
        {
            var mesh = Shrunk(out _);
            var copy = UnityEngine.Object.Instantiate(mesh);
            var c = copy.Data;
            Assert.Equal(12, c.Positions.Length);
            Assert.Equal(12, c.Uvs[0].Length);
            Assert.Equal(12, c.Colors32.Length);
            Assert.Equal(18, c.SubMeshes[0].IndexArray.Length);
            AssertContent(copy, 12, 3f);
        }

        [Fact]
        public void GrowingTheVertexCountKeepsOnlyTheData()
        {
            var mesh = Shrunk(out var m);
            Assert.True(m.Uvs[0].Length > 12 || m.Positions.Length > 12);
            // Fourteen positions without a Clear: the other channels keep their 12 values and
            // get zeros, not what their longer arrays held beyond the count.
            mesh.SetVertices(Positions(14, 4f));
            var uvs = new List<Vector4>();
            mesh.GetUVs(0, uvs);
            Assert.Equal(14, uvs.Count);
            Assert.Equal(new Vector4(11, 3f, 1f, 0f), uvs[11]);
            Assert.Equal(Vector4.zero, uvs[12]);
            Assert.Equal(Vector4.zero, uvs[13]);
            Assert.Equal(default, mesh.colors32[13]);
        }

        [Fact]
        public void ACanvasRendererCopiesOnlyTheMeshsData()
        {
            var mesh = Shrunk(out _);
            var g = OniMoono.Engine.UI.CanvasGeometry.From(mesh.Data);
            Assert.Equal(12, g.Positions.Length);
            Assert.Equal(12, g.Colors.Length);
            Assert.Equal(12, g.Uvs[0].Length);
            Assert.Equal(18, g.Indices.Length);
        }

        [Fact]
        public void AVertexCountThatChangesEveryFrameStillReusesArrays()
        {
            var mesh = new Mesh();
            var m = mesh.Data;
            int[] counts = [60, 64, 56, 62, 58, 64, 60];
            var lists = counts.Select(n => (Positions(n, 1f), new List<Vector2>(new Vector2[n]), new List<Color32>(new Color32[n]), Enumerable.Range(0, n / 4 * 6).Select(i => i % n).ToList())).ToArray();
            void Build(int k)
            {
                var (p, uv, c, t) = lists[k % lists.Length];
                mesh.Clear();
                mesh.SetVertices(p);
                mesh.SetUVs(0, uv);
                mesh.SetColors(c);
                mesh.SetTriangles(t, 0);
                // The device ran this frame's upload.
                m.UploadRan(++m.UploadsPosted);
            }
            for (int k = 0; k < 14; k++) Build(k);
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int k = 0; k < 14; k++) Build(k);
            long bytes = System.GC.GetAllocatedBytesForCurrentThread() - before;
            // Each build's channels alone would be about 60 * (12 + 16 + 4) + 90 * 4 bytes.
            Assert.True(bytes < 14 * 512, $"{bytes} bytes for 14 builds");
            for (int k = 0; k < lists.Length; k++)
            {
                Build(k);
                Assert.Equal(lists[k].Item1, mesh.vertices);
                Assert.Equal(lists[k].Item4, mesh.GetTriangles(0));
            }
        }

        [Fact]
        public void SizeClassesWasteAtMostAQuarter()
        {
            Assert.Equal(3, MeshNative.SizeClass(3));
            Assert.Equal(5, MeshNative.SizeClass(5));
            Assert.Equal(10, MeshNative.SizeClass(9));
            Assert.Equal(112, MeshNative.SizeClass(100));
            for (int n = 1; n < 5000; n++)
            {
                int c = MeshNative.SizeClass(n);
                Assert.InRange(c, n, Math.Max(n, n + n / 4));
            }
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
