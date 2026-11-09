using System;
using System.Linq;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.UI;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// The canvas's per-element and per-batch arrays (CanvasSystem): an element's copy of its mesh
    /// (CanvasRenderer.SetMesh) and the merged meshes of its batches reuse arrays only once the
    /// device has run every upload that could still read them, and give what fresh arrays gave.
    public class CanvasBufferReuseTests : EngineTest
    {
        static Mesh Quad(float at, int uvSets = 1)
        {
            var mesh = new Mesh
            {
                vertices = [new Vector3(at, 0f), new Vector3(at, 10f), new Vector3(at + 10f, 10f), new Vector3(at + 10f, 0f)],
                colors = [new Color(1f, 0.5f, 0.25f, at / 100f), Color.white, Color.red, Color.green],
                triangles = [0, 1, 2, 2, 3, 0],
            };
            for (int u = 0; u < uvSets; u++)
                mesh.SetUVs(u, Enumerable.Range(0, 4).Select(i => new Vector4(i + at, u, at, 1f)).ToList());
            return mesh;
        }

        static (GameObject Go, CanvasRenderer Renderer, CanvasRendererNative Data) Element()
        {
            var go = new GameObject("Element", typeof(RectTransform));
            var renderer = go.AddComponent<CanvasRenderer>();
            return (go, renderer, (CanvasRendererNative)renderer.Native);
        }

        static void AssertGeometry(Mesh source, CanvasGeometry g)
        {
            Assert.Equal(source.vertices, g.Positions);
            Assert.Equal(source.colors, g.Colors);
            Assert.Equal(source.triangles, g.Indices);
            var uvs = new System.Collections.Generic.List<Vector4>();
            source.GetUVs(0, uvs);
            Assert.Equal(uvs, g.Uvs[0]);
        }

        /// A copy the element no longer has and its baked mesh has moved on from is emptied, and
        /// its arrays come back for a later copy only once the baked mesh's uploads have run.
        [Fact]
        public void ACopyIsReusedOnlyOnceTheBakedMeshsUploadsRan()
        {
            var (go, renderer, d) = Element();
            try
            {
                var a = Quad(1f);
                renderer.SetMesh(a);
                var first = d.Geometry;
                var firstPositions = first.Positions;
                var firstUvs = first.Uvs[0];
                var baked = CanvasSystem.BuildMesh(d, Matrix4x4.identity, Color.white);
                Assert.Same(firstUvs, baked.Uvs[0]);
                // The device records an upload of the baked mesh and has not run it.
                int upload = ++baked.UploadsPosted;

                var b = Quad(2f);
                renderer.SetMesh(b);
                var second = d.Geometry;
                var secondUvs = second.Uvs[0];
                AssertGeometry(b, second);
                // Nothing came from the first copy: the baked mesh still holds its arrays.
                Assert.NotSame(firstPositions, second.Positions);
                Assert.NotSame(firstUvs, second.Uvs[0]);
                CanvasSystem.BuildMesh(d, Matrix4x4.identity, Color.white);
                Assert.Same(second.Uvs[0], baked.Uvs[0]);
                // The first copy is released: emptied, its arrays left as the upload recorded them.
                Assert.Null(first.Positions);
                Assert.Null(first.Uvs[0]);
                Assert.Equal(a.vertices, firstPositions);

                // Before the upload ran, a third copy gets new arrays.
                var c = Quad(3f);
                renderer.SetMesh(c);
                Assert.NotSame(firstUvs, d.Geometry.Uvs[0]);
                AssertGeometry(c, d.Geometry);
                CanvasSystem.BuildMesh(d, Matrix4x4.identity, Color.white);

                // Once it ran, the next copy may take the first copy's arrays, and holds the mesh.
                baked.UploadRan(upload);
                var e = Quad(4f);
                renderer.SetMesh(e);
                AssertGeometry(e, d.Geometry);
                if (!MeshNative.ReuseOff) Assert.True(ReferenceEquals(firstUvs, d.Geometry.Uvs[0]) || ReferenceEquals(secondUvs, d.Geometry.Uvs[0]));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// The copy the element still has (kept through Clear, given again by SetMesh of the same
        /// mesh unchanged) is never released, however its baked mesh changes.
        [Fact]
        public void TheElementsCurrentCopyIsNeverReleased()
        {
            var (go, renderer, d) = Element();
            try
            {
                var a = Quad(1f);
                renderer.SetMesh(a);
                var copy = d.Geometry;
                CanvasSystem.BuildMesh(d, Matrix4x4.identity, Color.white);
                // Moved and recoloured: rebaked from the same copy.
                CanvasSystem.BuildMesh(d, Matrix4x4.Translate(new Vector3(5f, 0f, 0f)), Color.white);
                CanvasSystem.BuildMesh(d, Matrix4x4.identity, Color.gray);
                renderer.Clear();
                renderer.SetMesh(a);
                Assert.Same(copy, d.Geometry);
                CanvasSystem.BuildMesh(d, Matrix4x4.identity, Color.white);
                AssertGeometry(a, copy);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// A copy the element was never drawn with (set again before a build, or hidden) is in no
        /// mesh and no upload: the next copy can take its arrays at once.
        [Fact]
        public void ACopyNeverBakedIsReusedAtOnce()
        {
            var (go, renderer, d) = Element();
            try
            {
                renderer.SetMesh(Quad(1f));
                var baked = CanvasSystem.BuildMesh(d, Matrix4x4.identity, Color.white);
                // An upload of the baked copy that never runs: only unbaked copies can be reused.
                ++baked.UploadsPosted;
                renderer.SetMesh(Quad(2f));
                var unbaked = d.Geometry;
                var uvs = unbaked.Uvs[0];
                var c = Quad(3f);
                renderer.SetMesh(c);
                Assert.Null(unbaked.Positions);
                AssertGeometry(c, d.Geometry);
                var e = Quad(4f);
                renderer.SetMesh(e);
                AssertGeometry(e, d.Geometry);
                if (!MeshNative.ReuseOff) Assert.Same(uvs, d.Geometry.Uvs[0]);
                CanvasSystem.BuildMesh(d, Matrix4x4.identity, Color.white);
                AssertGeometry(e, d.Geometry);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// Colours given as bytes are copied as the mesh's colours read.
        [Fact]
        public void ByteColoursAreCopiedAsTheMeshReadsThem()
        {
            var (go, renderer, d) = Element();
            try
            {
                var mesh = Quad(1f);
                mesh.colors32 = [new Color32(255, 128, 1, 0), new Color32(3, 77, 254, 255), new Color32(0, 0, 0, 1), new Color32(200, 100, 50, 25)];
                renderer.SetMesh(mesh);
                Assert.Equal(mesh.colors, d.Geometry.Colors);
                mesh.colors = [Color.red, Color.blue, Color.green, new Color(0.1f, 0.2f, 0.3f, 0.4f)];
                renderer.SetMesh(mesh);
                Assert.Equal(mesh.colors, d.Geometry.Colors);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// The baked mesh gives the copy's values in the canvas's space whether its arrays were
        /// fresh or reused.
        [Fact]
        public void AReusedCopyBakesAsAFreshOne()
        {
            var (go, renderer, d) = Element();
            try
            {
                var space = Matrix4x4.TRS(new Vector3(3f, 4f, 0f), Quaternion.Euler(0f, 0f, 30f), new Vector3(2f, 2f, 1f));
                for (int i = 0; i < 6; i++)
                {
                    var mesh = Quad(i * 7f);
                    renderer.SetMesh(mesh);
                    var baked = CanvasSystem.BuildMesh(d, space, new Color(1f, 1f, 1f, 0.5f));
                    baked.UploadRan(++baked.UploadsPosted);
                    var expected = mesh.vertices.Select(v => space.MultiplyPoint3x4(v)).ToArray();
                    Assert.Equal(expected, baked.Positions.Take(baked.VertexCount));
                    Assert.Equal(mesh.colors.Select(c => c * new Color(1f, 1f, 1f, 0.5f)), baked.Colors.Take(baked.VertexCount));
                    Assert.Equal(mesh.triangles, baked.SubMeshes[0].Indices.ToArray());
                    AssertGeometry(mesh, d.Geometry);
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static MeshNative Part(float at, int uvSets, bool normals)
        {
            var m = new MeshNative { VertexCount = 4 };
            m.Positions = Enumerable.Range(0, 4).Select(i => new Vector3(i, at, 0f)).ToArray();
            m.Colors = Enumerable.Range(0, 4).Select(i => new Color(i / 4f, at / 10f, 0f, 1f)).ToArray();
            if (normals) m.Normals = Enumerable.Range(0, 4).Select(i => new Vector3(0f, i, 1f)).ToArray();
            for (int u = 0; u < uvSets; u++) m.Uvs[u] = Enumerable.Range(0, 4).Select(i => new Vector4(i, u, at, 2f)).ToArray();
            m.SubMeshes[0].SetIndices([0, 1, 2, 2, 3, 0]);
            return m;
        }

        /// What the merge gave with new arrays for every channel (CanvasSystem.Concatenate before
        /// its arrays were reused).
        static void AssertMerged(MeshNative[] parts, MeshNative merged, bool subMeshEach)
        {
            int n = parts.Sum(p => p.VertexCount);
            Assert.Equal(n, merged.VertexCount);
            Assert.Equal(parts.SelectMany(p => p.Positions), merged.Positions.Take(n));
            Assert.Equal(parts.SelectMany(p => p.Colors), merged.Colors.Take(n));
            bool normals = parts.Any(p => p.Normals != null);
            if (normals) Assert.Equal(parts.SelectMany(p => p.Normals ?? Enumerable.Repeat(new Vector3(0f, 0f, -1f), p.VertexCount)), merged.Normals.Take(n));
            else Assert.Null(merged.Normals);
            for (int u = 0; u < 4; u++)
            {
                if (!parts.Any(p => p.Uvs[u] != null))
                {
                    Assert.Null(merged.Uvs[u]);
                    Assert.Equal(0, merged.UvDimensions[u]);
                    continue;
                }
                Assert.Equal(4, merged.UvDimensions[u]);
                Assert.Equal(parts.SelectMany(p => p.Uvs[u] ?? new Vector4[p.VertexCount]), merged.Uvs[u].Take(n));
            }
            int at = 0;
            var all = parts.SelectMany(p => { int first = at; at += p.VertexCount; return p.SubMeshes[0].Indices.ToArray().Select(i => i + first).ToArray(); }).ToArray();
            if (subMeshEach)
            {
                Assert.Equal(parts.Length, merged.SubMeshes.Count);
                Assert.Equal(all, merged.SubMeshes.SelectMany(s => s.Indices.ToArray()));
            }
            else
            {
                Assert.Single(merged.SubMeshes);
                Assert.Equal(all, merged.SubMeshes[0].Indices.ToArray());
            }
        }

        /// Merging again into arrays reused from an earlier merge, whose parts had other UV sets
        /// and normals, gives what new arrays gave: a part without a UV set the others have
        /// reads zeros, not what the reused array held.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AMergeIntoReusedArraysGivesWhatNewOnesGave(bool subMeshEach)
        {
            var merged = new MeshNative { Dynamic = true };
            MeshNative[][] runs =
            [
                [Part(1f, 2, true), Part(2f, 2, true), Part(3f, 2, false)],
                [Part(4f, 2, false), Part(5f, 0, false), Part(6f, 1, true)],
                [Part(7f, 1, false), Part(8f, 2, false)],
                [Part(9f, 0, false), Part(10f, 2, true), Part(11f, 0, false), Part(12f, 1, false)],
            ];
            foreach (var parts in runs)
            {
                CanvasSystem.Concatenate(parts, merged, subMeshEach);
                AssertMerged(parts, merged, subMeshEach);
                merged.UploadRan(++merged.UploadsPosted);
            }
        }

        /// The merged mesh's arrays are reused only once the uploads holding them have run, and
        /// those uploads keep reading what they were given.
        [Fact]
        public void MergedArraysAreReusedOnlyAfterTheUploadHoldingThemRan()
        {
            var merged = new MeshNative { Dynamic = true };
            var parts = new[] { Part(1f, 1, false), Part(2f, 1, false) };
            CanvasSystem.Concatenate(parts, merged);
            var first = (merged.Positions, Uv: merged.Uvs[0], Indices: merged.SubMeshes[0].IndexArray);
            var firstValues = first.Positions.Take(8).ToArray();
            int upload = ++merged.UploadsPosted;
            var others = new[] { Part(3f, 1, false), Part(4f, 1, false) };
            CanvasSystem.Concatenate(others, merged);
            var secondPositions = merged.Positions;
            Assert.NotSame(first.Positions, merged.Positions);
            Assert.NotSame(first.Uv, merged.Uvs[0]);
            Assert.NotSame(first.Indices, merged.SubMeshes[0].IndexArray);
            Assert.Equal(firstValues, first.Positions.Take(8));
            merged.UploadRan(upload);
            merged.UploadRan(++merged.UploadsPosted);
            CanvasSystem.Concatenate(parts, merged);
            AssertMerged(parts, merged, subMeshEach: false);
            if (!MeshNative.ReuseOff) Assert.True(ReferenceEquals(first.Positions, merged.Positions) || ReferenceEquals(secondPositions, merged.Positions));
        }

        /// A merge of unchanged size into a mesh whose uploads ran allocates no channel arrays.
        [Fact]
        public void ASteadyMergeAllocatesNoChannelArrays()
        {
            if (MeshNative.ReuseOff) return;
            var merged = new MeshNative { Dynamic = true };
            var parts = Enumerable.Range(0, 16).Select(i => Part(i, 2, true)).ToArray();
            for (int i = 0; i < 3; i++)
            {
                CanvasSystem.Concatenate(parts, merged);
                merged.UploadRan(++merged.UploadsPosted);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            CanvasSystem.Concatenate(parts, merged);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            // The channels alone would be 64 * (12 + 16 + 12 + 2 * 16) + 96 * 4 bytes.
            Assert.True(bytes < 1024, $"{bytes} bytes");
        }
    }
}
