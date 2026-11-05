using OniMoono.Engine.Assets;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    public class PrimitiveTests : EngineTest
    {
        // The game's data as the other suites find it: ONI_GAME_DIR, else the frozen 761211 install.
        // The asset system opens one folder per process, so a suite naming another folder fails.
        static readonly string DataDir = System.IO.Path.Combine(
            System.Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762107"),
            "OxygenNotIncluded_Data");

        [Theory]
        [InlineData(PrimitiveType.Cube, typeof(BoxCollider))]
        [InlineData(PrimitiveType.Sphere, typeof(SphereCollider))]
        [InlineData(PrimitiveType.Capsule, typeof(CapsuleCollider))]
        [InlineData(PrimitiveType.Cylinder, typeof(CapsuleCollider))]
        [InlineData(PrimitiveType.Plane, typeof(MeshCollider))]
        [InlineData(PrimitiveType.Quad, typeof(MeshCollider))]
        public void PrimitivesHaveMeshMaterialAndCollider(PrimitiveType type, System.Type collider)
        {
            AssetSystem.Initialize(DataDir);
            var go = GameObject.CreatePrimitive(type);
            Assert.Equal(type.ToString(), go.name);
            Assert.Equal(type.ToString(), go.GetComponent<MeshFilter>().sharedMesh.name);
            // This build strips the unreferenced built-in Default-Material, so the renderer has
            // none (as in Unity's player for this build).
            Assert.Null(go.GetComponent<MeshRenderer>().sharedMaterial);
            Assert.NotNull(go.GetComponent(collider));
        }

        [Fact]
        public void RaysHitPrimitivesFromTheFront()
        {
            AssetSystem.Initialize(DataDir);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Assert.True(Physics.Raycast(new Ray(new Vector3(0f, 0f, -5f), Vector3.forward), out var hit, 100f, -1));
            Assert.Equal(4.5f, hit.distance, 4);
            Object.DestroyImmediate(cube);

            // A Quad faces -Z: hit from the front, not from behind.
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Assert.True(Physics.Raycast(new Ray(new Vector3(0.1f, 0.1f, -5f), Vector3.forward), out hit, 100f, -1));
            Assert.Equal(5f, hit.distance, 4);
            Assert.False(Physics.Raycast(new Ray(new Vector3(0.1f, 0.1f, 5f), Vector3.back), out _, 100f, -1));
            Object.DestroyImmediate(quad);

            var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Assert.True(Physics.Raycast(new Ray(new Vector3(0f, 5f, 0f), Vector3.down), out hit, 100f, -1));
            Assert.Equal(4f, hit.distance, 4);
            Object.DestroyImmediate(capsule);
        }
    }
}
