using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Scripting;
using Xunit;

namespace OniMoono.Tests
{
    /// Unity members mods call that ours lacked, as measured in the original (UnityProbe's
    /// apigaps "curve", "misc" and "bounds").
    public class ModApiGapTests : EngineTest
    {
        static string Keys(AnimationCurve c)
            => string.Join(" ", Array.ConvertAll(c.keys, k => $"({k.time:R},{k.value:R} {k.inTangent:R}/{k.outTangent:R} {k.weightedMode} {k.inWeight:R}/{k.outWeight:R})"));

        [Fact]
        public void RemoveKeyKeepsTheOthersAndThrowsOutOfRange()
        {
            var c = new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.5f, 1, 2, -1), new Keyframe(1, 0.25f));
            Assert.Equal("(0,0 0/0 None 0/0) (0.5,1 2/-1 None 0/0) (1,0.25 0/0 None 0/0)", Keys(c));
            c.RemoveKey(1);
            Assert.Equal("(0,0 0/0 None 0/0) (1,0.25 0/0 None 0/0)", Keys(c));
            Assert.Equal(0.125f, c.Evaluate(0.5f));
            foreach (int bad in new[] { -1, 2, 5 })
                Assert.Equal("RemoveKey", Assert.Throws<IndexOutOfRangeException>(() => c.RemoveKey(bad)).Message);
            var one = new AnimationCurve(new Keyframe(0, 3));
            one.RemoveKey(0);
            Assert.Equal(0, one.length);
        }

        [Fact]
        public void SmoothTangentsMixesTheNeighbouringSlopes()
        {
            string[] measured =
            [
                "(0,0 2/2 None 0/0.33333334) (1,2 0.75/0.75 None 0.33333334/0.33333334) (3,1 1.75/1.75 None 0.33333334/0.33333334) (4,5 4/4 None 0.33333334/0)",
                "(0,0 2/2 None 0/0.33333334) (1,2 1.375/1.375 None 0.33333334/0.33333334) (3,1 0.625/0.625 None 0.33333334/0.33333334) (4,5 4/4 None 0.33333334/0)",
                "(0,0 2/2 None 0/0.33333334) (1,2 2/2 None 0.33333334/0.33333334) (3,1 -0.5/-0.5 None 0.33333334/0.33333334) (4,5 4/4 None 0.33333334/0)",
                "(0,0 2/2 None 0/0.33333334) (1,2 -0.5/-0.5 None 0.33333334/0.33333334) (3,1 4/4 None 0.33333334/0.33333334) (4,5 4/4 None 0.33333334/0)",
                "(0,0 2/2 None 0/0.33333334) (1,2 3.25/3.25 None 0.33333334/0.33333334) (3,1 -2.75/-2.75 None 0.33333334/0.33333334) (4,5 4/4 None 0.33333334/0)",
            ];
            float[] weights = [0f, 0.5f, 1f, -1f, 2f];
            for (int w = 0; w < weights.Length; w++)
            {
                var s = new AnimationCurve(new Keyframe(0, 0, 5, 5), new Keyframe(1, 2, 7, -3), new Keyframe(3, 1, 0, 4), new Keyframe(4, 5));
                for (int i = 0; i < s.length; i++) s.SmoothTangents(i, weights[w]);
                Assert.Equal(measured[w], Keys(s));
            }
            var u = new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 2), new Keyframe(1.5f, -1), new Keyframe(4, 4));
            u.SmoothTangents(1, 0);
            u.SmoothTangents(2, 0.3f);
            Assert.Equal("(0,0 0/0 None 0/0) (1,2 -2/-2 None 0.33333334/0.33333334) (1.5,-1 -3.1999998/-3.1999998 None 0.33333334/0.33333334) (4,4 0/0 None 0/0)", Keys(u));
            var weighted = new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 1, 3, 3, 0.2f, 0.7f) { weightedMode = WeightedMode.Both }, new Keyframe(2, 0));
            weighted.SmoothTangents(1, 0);
            Assert.Equal("(0,0 0/0 None 0/0) (1,1 0/0 Both 0.33333334/0.33333334) (2,0 0/0 None 0/0)", Keys(weighted));
            var single = new AnimationCurve(new Keyframe(0, 0, 2, 3));
            single.SmoothTangents(0, 0);
            Assert.Equal("(0,0 2/3 None 0/0)", Keys(single));
            Assert.Equal("SmoothTangents", Assert.Throws<IndexOutOfRangeException>(() => single.SmoothTangents(3, 0)).Message);
        }

        [Fact]
        public void IncrementalCollectionIsOffAndItsSliceIsKept()
        {
            Assert.Equal(50000000ul, GarbageCollector.incrementalTimeSliceNanoseconds);
            Assert.False(GarbageCollector.CollectIncremental(0));
            GarbageCollector.incrementalTimeSliceNanoseconds = 1234567;
            Assert.Equal(1234567ul, GarbageCollector.incrementalTimeSliceNanoseconds);
            GarbageCollector.incrementalTimeSliceNanoseconds = 50000000;
        }

        [Fact]
        public void CullingMatrixFollowsTheCameraUntilSetAndAfterReset()
        {
            var cam = new GameObject("cull").AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 3;
            cam.transform.position = new Vector3(1, 2, -10);
            Assert.Equal(cam.projectionMatrix * cam.worldToCameraMatrix, cam.cullingMatrix);
            var set = Matrix4x4.Scale(new Vector3(2, 2, 2));
            cam.cullingMatrix = set;
            cam.transform.position = new Vector3(5, 5, -10);
            Assert.Equal(set, cam.cullingMatrix);
            cam.ResetCullingMatrix();
            cam.orthographicSize = 5;
            Assert.Equal(cam.projectionMatrix * cam.worldToCameraMatrix, cam.cullingMatrix);
        }

        [Fact]
        public void SpriteDrawModeKeepsTheSize()
        {
            var sr = new GameObject("sprite").AddComponent<SpriteRenderer>();
            Assert.Equal(SpriteDrawMode.Simple, sr.drawMode);
            var tex = new Texture2D(4, 4);
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 2f);
            Assert.Equal(new Vector2(2, 2), sr.size);
            sr.drawMode = SpriteDrawMode.Sliced;
            Assert.Equal(new Vector2(2, 2), sr.size);
            sr.size = new Vector2(3, 1);
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, 2, 2), Vector2.zero, 1f);
            Assert.Equal(new Vector2(3, 1), sr.size);
            Assert.Equal(new Vector3(1.5f, 0.5f, 0f), sr.bounds.center);
            sr.drawMode = (SpriteDrawMode)7;
            Assert.Equal(7, (int)sr.drawMode);
        }

        [Fact]
        public void NativeArraysWithAnAllocator()
        {
            foreach (var a in new[] { Allocator.Temp, Allocator.TempJob, Allocator.Persistent })
            {
                var arr = new NativeArray<int>(4, a);
                Assert.True(arr.IsCreated);
                Assert.Equal(new[] { 0, 0, 0, 0 }, arr.ToArray());
                arr[1] = 7;
                Assert.Equal(7, arr[1]);
                arr.Dispose();
                Assert.False(arr.IsCreated);
            }
            foreach (var a in new[] { Allocator.None, Allocator.Invalid, (Allocator)9 })
            {
                Assert.Equal("destination", Assert.Throws<ArgumentNullException>(() => new NativeArray<int>(4, a)).ParamName);
                Assert.Throws<NullReferenceException>(() => new NativeArray<int>(4, a, NativeArrayOptions.UninitializedMemory));
            }
            var copy = new NativeArray<int>(new[] { 1, 2, 3 }, Allocator.Persistent);
            Assert.Equal(new[] { 1, 2, 3 }, copy.ToArray());
            copy.Dispose();
        }

        static string B(Bounds b) => $"c=({b.center.x:R},{b.center.y:R},{b.center.z:R}) e=({b.extents.x:R},{b.extents.y:R},{b.extents.z:R})";

        [Fact]
        public void ColliderBounds()
        {
            var root = new GameObject("box");
            root.transform.position = new Vector3(1, 2, 3);
            root.transform.rotation = Quaternion.Euler(0, 0, 30);
            root.transform.localScale = new Vector3(2, 1, 1);
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0.5f, 0, 0);
            box.size = new Vector3(1, 2, 3);
            // The original's PhysX widens these by a few ulps (1.3660257, 1.5000005).
            AssertNear(new Vector3(1.8660254f, 2.5f, 3f), new Vector3(1.3660254f, 1.3660254f, 1.5f), box.bounds);
            box.enabled = false;
            Assert.Equal("c=(1,2,3) e=(0,0,0)", B(box.bounds));

            var sgo = new GameObject("sphere");
            sgo.transform.position = new Vector3(-1, 0, 0);
            sgo.transform.localScale = new Vector3(1, 3, 2);
            var sphere = sgo.AddComponent<SphereCollider>();
            sphere.radius = 0.75f;
            sphere.center = new Vector3(0, 1, 0);
            Assert.Equal("c=(-1,3,0) e=(2.25,2.25,2.25)", B(sphere.bounds));

            var cgo = new GameObject("capsule");
            cgo.transform.rotation = Quaternion.Euler(0, 0, 45);
            var capsule = cgo.AddComponent<CapsuleCollider>();
            capsule.radius = 0.5f;
            capsule.height = 3f;
            AssertNear(Vector3.zero, new Vector3(1.2071071f, 1.2071068f, 0.5f), capsule.bounds);

            var inactive = new GameObject("inactive");
            inactive.SetActive(false);
            inactive.transform.position = new Vector3(7, 7, 7);
            Assert.Equal("c=(7,7,7) e=(0,0,0)", B(inactive.AddComponent<BoxCollider>().bounds));

            var b2 = new GameObject("box2d");
            b2.transform.position = new Vector3(1, 2, 3);
            b2.transform.rotation = Quaternion.Euler(0, 0, 30);
            b2.transform.localScale = new Vector3(2, 1, 1);
            var box2 = b2.AddComponent<BoxCollider2D>();
            box2.offset = new Vector2(0.5f, 0);
            box2.size = new Vector2(1, 2);
            AssertNear(new Vector3(1.8660254f, 2.5f, 3f), new Vector3(1.3660254f, 1.3660254f, 0f), box2.bounds);
            box2.size = new Vector2(3, 3);
            AssertNear(new Vector3(1.8660254f, 2.5f, 3f), new Vector3(3.3480763f, 2.7990384f, 0f), box2.bounds);
            box2.enabled = false;
            Assert.Equal("c=(1,2,3) e=(0,0,0)", B(box2.bounds));

            var c2 = new GameObject("circle");
            c2.transform.localScale = new Vector3(1, 3, 1);
            var circle = c2.AddComponent<CircleCollider2D>();
            circle.radius = 0.5f;
            circle.offset = new Vector2(1, 1);
            Assert.Equal("c=(1,3,0) e=(1.5,1.5,0)", B(circle.bounds));

            var poly = new GameObject("poly").AddComponent<PolygonCollider2D>();
            Assert.Equal(1, poly.pathCount);
            AssertNear(new Vector3(-2.98023224E-08f, 0.09549147f, 0f), new Vector3(0.9510565f, 0.904508531f, 0f), poly.bounds);
            poly.SetPath(0, [new(0, 0), new(2, 0), new(0, 1)]);
            Assert.Equal("c=(1,0.5,0) e=(1,0.5,0)", B(poly.bounds));
            Assert.Equal("c=(0,0,0) e=(0.5,0,0)", B(new GameObject("edge").AddComponent<EdgeCollider2D>().bounds));
        }

        static void AssertNear(Vector3 center, Vector3 extents, Bounds b)
        {
            Assert.True((b.center - center).magnitude < 1e-5f && (b.extents - extents).magnitude < 1e-5f, $"{B(b)}, the original c={center} e={extents}");
        }
    }
}
