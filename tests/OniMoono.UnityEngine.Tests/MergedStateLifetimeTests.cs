using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// The merged state (docs/research/facade-merge.md, steps 2 and 3) through Instantiate of every
    /// engine component, destruction with components added on the way, and assets loaded from the
    /// game's files and unloaded again.
    public class MergedStateLifetimeTests : EngineTest
    {
        static readonly Type[] EngineComponents =
        [
            typeof(Camera), typeof(Canvas), typeof(CanvasGroup), typeof(CanvasRenderer), typeof(MeshRenderer), typeof(MeshFilter),
            typeof(SpriteRenderer), typeof(Light), typeof(UnityEngine.Rendering.SortingGroup), typeof(SphereCollider), typeof(BoxCollider),
            typeof(CapsuleCollider), typeof(Rigidbody), typeof(BoxCollider2D), typeof(PolygonCollider2D), typeof(EdgeCollider2D),
            typeof(CircleCollider2D), typeof(Rigidbody2D), typeof(Animator), typeof(Animation), typeof(ParticleSystem),
            typeof(LineRenderer), typeof(UnityEngine.Video.VideoPlayer), typeof(UnityEngine.Playables.PlayableDirector), typeof(TextMesh),
        ];

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void InstantiateCopiesEveryEngineComponentsHideFlagsAndEnabled(bool active, bool enabled)
        {
            foreach (var type in EngineComponents)
            {
                var go = new GameObject(type.Name);
                var c = go.AddComponent(type);
                // After AddComponent: a canvas component turns the Transform into a RectTransform.
                go.transform.hideFlags = HideFlags.HideInInspector;
                Assert.True(c, type.Name);
                c.hideFlags = HideFlags.DontSave;
                if (c is Behaviour b) b.enabled = enabled;
                go.SetActive(active);

                var clone = Object.Instantiate(go);
                var copy = clone.GetComponent(type);
                Assert.True(copy, type.Name);
                Assert.True(HideFlags.DontSave == copy.hideFlags, type.Name);
                Assert.True(HideFlags.HideInInspector == clone.transform.hideFlags, type.Name);
                if (copy is Behaviour cb) Assert.Equal(enabled, cb.enabled);
                if (copy is Behaviour active2) Assert.Equal(active && enabled, active2.isActiveAndEnabled);
                Assert.Equal(active, clone.activeSelf);
                Assert.Same(copy, copy.EngineState.Managed);
                Assert.Same(clone, copy.EngineState.GameObject);
                Assert.Same(clone, copy.gameObject);
                Assert.Same(clone.transform, copy.transform);
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(clone);
            }
        }

        public class AddsInOnDestroy : MonoBehaviour
        {
            public static Component Added;
            void OnDestroy() => Added = gameObject.AddComponent<BoxCollider2D>();
        }

        [Fact]
        public void AComponentAddedByAnOnDestroyGoesWithItsObject()
        {
            var go = new GameObject("doomed", typeof(BoxCollider2D));
            go.AddComponent<AddsInOnDestroy>();
            Object.Destroy(go);
            Frame();
            Assert.False(go);
            Assert.NotNull(AddsInOnDestroy.Added);
            Assert.False(AddsInOnDestroy.Added);
            Assert.True(AddsInOnDestroy.Added.EngineDestroyed);
            // The physics world's queries pass over it (its object has no transform any more).
            Assert.False(Physics.Raycast(new Ray(new Vector3(0f, 0f, -10f), Vector3.forward), out _, 100f, ~0));
            AddsInOnDestroy.Added = null;
        }

        /// The first GameObject of resources.assets with a script on it, and its file and path id.
        internal static (GameObject Go, long PathId) LoadPrefabWithScript()
        {
            TmpTestSupport.LoadGameData();
            var file = AssetSystem.Get("resources.assets");
            foreach (var info in file.ObjectsById.Values.Where(i => file.TypeOf(i).ClassId == 1).Take(400))
                if (ObjectLoader.Load(file, info.PathId) is GameObject go && go.GetComponents<MonoBehaviour>().Length > 0)
                    return (go, info.PathId);
            Assert.Fail("no prefab with a script in resources.assets");
            return default;
        }

        [Fact]
        public void APrefabLoadedFromTheGamesFilesStartsAsItsRecordsDid()
        {
            var (go, pathId) = LoadPrefabWithScript();
            Assert.Null(go.NativeRecord);
            Assert.True(go.IsPersistent);
            Assert.Equal(pathId, go.Source.PathId);
            Assert.True(go.GetInstanceID() > 0);
            Assert.Null(go.Scene);
            Assert.False(go.activeInHierarchy);
            Assert.Same(go.transform, go.Transform);
            Assert.Null(go.transform.EngineState);
            foreach (var c in go.GetComponents<Component>())
            {
                Assert.Same(go, c.EngineGameObject);
                Assert.True(c.GetInstanceID() > 0, c.GetType().Name);
                Assert.True(c.EngineIsPersistent, c.GetType().Name);
                if (c.EngineState is { } state) Assert.Same(go, state.GameObject);
                if (c is MonoBehaviour mb)
                {
                    Assert.Null(mb.EngineState);
                    Assert.NotNull(mb.EngineScript);
                    Assert.False(mb.didAwake);
                }
            }
            // Instantiated, the copy is a scene object of its own.
            var clone = Object.Instantiate(go);
            Assert.False(clone.IsPersistent);
            Assert.True(clone.GetInstanceID() < 0);
            Assert.Equal(go.GetComponents<MonoBehaviour>().Select(m => m.enabled), clone.GetComponents<MonoBehaviour>().Select(m => m.enabled));
            Object.DestroyImmediate(clone);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (List<WeakReference> Refs, int Id, long PathId) LoadPrefabAndDrop()
        {
            var (go, pathId) = LoadPrefabWithScript();
            var refs = go.GetComponents<Component>().Select(c => new WeakReference(c)).Append(new WeakReference(go)).ToList();
            return (refs, go.GetInstanceID(), pathId);
        }

        [Fact]
        public void APrefabsScriptsAndObjectUnloadWithIt()
        {
            var (refs, id, pathId) = LoadPrefabAndDrop();
            Resources.UnloadUnusedAssets();
            Assert.All(refs, r => Assert.False(r.IsAlive));
            Assert.Null(ObjectTable.FindByInstanceId(id));
            var again = (GameObject)ObjectLoader.Load(AssetSystem.Get("resources.assets"), pathId);
            Assert.Equal(id, again.GetInstanceID());
            Assert.NotEmpty(again.GetComponents<MonoBehaviour>());
        }
    }
}
