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
    /// (CanvasRenderer.SetMesh) reuses arrays only once the device has run every upload that
    /// could still read them, and gives what fresh arrays gave.
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
    }
}
