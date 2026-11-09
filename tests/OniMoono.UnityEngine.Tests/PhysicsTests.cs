using System.IO;
using System.Linq;
using OniMoono.Assets.Files;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    public class PhysicsTests : EngineTest
    {
        static void Near(float expected, float actual, float tolerance = 1e-4f) => Assert.InRange(actual, expected - tolerance, expected + tolerance);

        static SphereCollider Sphere(Vector3 position, int layer = 0)
        {
            var go = new GameObject("sphere") { layer = layer };
            go.transform.position = position;
            return go.AddComponent<SphereCollider>();
        }

        [Fact]
        public void RayHitsNearestSphereSurface()
        {
            var near = Sphere(new Vector3(0f, 0f, 5f));
            Sphere(new Vector3(0f, 0f, 10f));
            Assert.True(Physics.Raycast(new Ray(Vector3.zero, Vector3.forward), out var hit, float.PositiveInfinity, ~0));
            Near(4.5f, hit.distance);
            Near(4.5f, hit.point.z);
            Near(-1f, hit.normal.z);
            Assert.Same(near, hit.collider);
            Assert.Same(near.transform, hit.transform);
        }

        [Fact]
        public void RespectsLayerMaskDistanceAndEnabled()
        {
            var s = Sphere(new Vector3(0f, 0f, 5f), layer: 5);
            var ray = new Ray(Vector3.zero, Vector3.forward);
            Assert.False(Physics.Raycast(ray, out _, float.PositiveInfinity, 1 << 0));
            Assert.True(Physics.Raycast(ray, out _, float.PositiveInfinity, 1 << 5));
            Assert.False(Physics.Raycast(ray, out _, 4f, ~0));
            s.enabled = false;
            Assert.False(Physics.Raycast(ray, out _, float.PositiveInfinity, ~0));
            s.enabled = true;
            s.gameObject.SetActive(false);
            Assert.False(Physics.Raycast(ray, out _, float.PositiveInfinity, ~0));
        }

        [Fact]
        public void OriginInsideIsNotAHit()
        {
            Sphere(Vector3.zero);
            Assert.False(Physics.Raycast(new Ray(Vector3.zero, Vector3.forward), out var hit, float.PositiveInfinity, ~0));
            Assert.Null(hit.collider);
        }

        [Fact]
        public void SphereRadiusScalesByLargestAxis()
        {
            var s = Sphere(new Vector3(0f, 0f, 10f));
            s.transform.localScale = new Vector3(1f, 4f, 2f);
            Assert.True(Physics.Raycast(new Ray(Vector3.zero, Vector3.forward), out var hit, float.PositiveInfinity, ~0));
            Near(8f, hit.distance);
        }

        [Fact]
        public void RotatedBoxHit()
        {
            var go = new GameObject("box");
            go.transform.position = new Vector3(0f, 0f, 10f);
            go.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            go.AddComponent<BoxCollider>();
            Assert.True(Physics.Raycast(new Ray(Vector3.zero, Vector3.forward), out var hit, float.PositiveInfinity, ~0));
            Near(10f - Mathf.Sqrt(0.5f), hit.distance);
        }

        [Fact]
        public void HitTransformPrefersAttachedRigidbody()
        {
            var parent = new GameObject("body");
            var body = parent.AddComponent<Rigidbody>();
            var s = Sphere(new Vector3(0f, 0f, 5f));
            s.transform.SetParent(parent.transform, true);
            Assert.Same(body, s.attachedRigidbody);
            Assert.True(Physics.Raycast(new Ray(Vector3.zero, Vector3.forward), out var hit, float.PositiveInfinity, ~0));
            Assert.Same(parent.transform, hit.transform);
            Assert.Equal(Vector3.zero, body.linearVelocity);
            s.enabled = false;
            Assert.Null(s.attachedRigidbody);
        }

        [Fact]
        public void DestroyedColliderIsNotFound()
        {
            var s = Sphere(new Vector3(0f, 0f, 5f));
            Assert.True(Physics.Raycast(new Ray(Vector3.zero, Vector3.forward), out var hit, float.PositiveInfinity, ~0));
            Object.DestroyImmediate(s.gameObject);
            Assert.True(hit.collider == null);
            Assert.False(Physics.Raycast(new Ray(Vector3.zero, Vector3.forward), out _, float.PositiveInfinity, ~0));
        }

        [Fact]
        public void BoxCollider2DAndRigidbody2D()
        {
            var go = new GameObject("2d");
            var box = go.AddComponent<BoxCollider2D>();
            box.size = new Vector2(1f, 1.5f);
            Assert.Equal(new Vector2(1f, 1.5f), ((BoxCollider2DNative)box.Native).Size);
            Assert.Equal(Vector2.zero, go.AddComponent<Rigidbody2D>().linearVelocity);
            Assert.False(default(RaycastHit2D));
        }

        [Fact]
        public void EdgeCollider2DKeepsItsLineWhenCloned()
        {
            var go = new GameObject("edge");
            var edge = (EdgeCollider2DNative)go.AddComponent<EdgeCollider2D>().Native;
            edge.Points = new[] { Vector2.zero, new Vector2(2f, 1f) };
            edge.EdgeRadius = 0.25f;
            var copy = (EdgeCollider2DNative)Object.Instantiate(go).GetComponent<EdgeCollider2D>().Native;
            Assert.Equal(edge.Points, copy.Points);
            Assert.NotSame(edge.Points, copy.Points);
            Assert.Equal(0.25f, copy.EdgeRadius);
        }

        [Fact]
        public void GravityComesFromThePhysicsManager()
        {
            var file = SerializedFile.Open(Path.Combine(GameInstall.DataDir, "globalgamemanagers"));
            var obj = file.Objects.First(o => file.TypeOf(o).ClassId == 55);
            PhysicsWorld.Configure(TypeTreeReader.ReadNative(55, file.OpenObject(obj)));
            Near(-9.81f, Physics.gravity.y);
            Assert.True(PhysicsWorld.QueriesHitTriggers);
        }
    }
}
