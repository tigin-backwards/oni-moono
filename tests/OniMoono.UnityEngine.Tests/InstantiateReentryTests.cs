using System.Collections.Generic;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// Instantiate's clone map and the lifecycle walks' lists are kept for the next call
    /// (Cloner, ScratchList): an Instantiate or activation started from a callback of another
    /// (Awake, OnEnable, OnAfterDeserialize) gets maps and lists of its own, and each clone's
    /// references point into its own hierarchy.
    public class InstantiateReentryTests : EngineTest
    {
        public class Spawner : MonoBehaviour
        {
            public GameObject prefab;
            public Spawner self;
            public GameObject spawned;
            public static readonly List<string> Order = new();

            void Awake()
            {
                Order.Add("awake " + name);
                if (prefab != null) spawned = Instantiate(prefab);
            }

            void OnEnable() => Order.Add("enable " + name);
        }

        public class Callbacks : MonoBehaviour, ISerializationCallbackReceiver
        {
            public GameObject prefab;
            public Callbacks self;
            public GameObject spawned;

            public void OnBeforeSerialize() { }

            public void OnAfterDeserialize()
            {
                // Unity forbids most API here; the engine's map must survive it all the same.
                if (prefab != null) spawned = (GameObject)Object.Instantiate((Object)prefab);
            }
        }

        [Fact]
        public void AnInstantiateFromAwakeGetsAMapOfItsOwn()
        {
            var inner = new GameObject("Inner");
            var innerSpawner = inner.AddComponent<Spawner>();
            innerSpawner.self = innerSpawner;
            var outer = new GameObject("Outer");
            outer.SetActive(false);
            var child = new GameObject("Child");
            child.transform.SetParent(outer.transform, false);
            var outerSpawner = outer.AddComponent<Spawner>();
            outerSpawner.prefab = inner;
            outerSpawner.self = outerSpawner;
            var childSpawner = child.AddComponent<Spawner>();
            childSpawner.self = childSpawner;
            outer.SetActive(true);
            try
            {
                Spawner.Order.Clear();
                for (int round = 0; round < 3; round++)
                {
                    var clone = Object.Instantiate(outer);
                    var s = clone.GetComponent<Spawner>();
                    Assert.Same(s, s.self);
                    Assert.NotNull(s.spawned);
                    var spawned = s.spawned.GetComponent<Spawner>();
                    Assert.Same(spawned, spawned.self);
                    Assert.NotSame(innerSpawner, spawned);
                    var c = clone.transform.GetChild(0).GetComponent<Spawner>();
                    Assert.Same(c, c.self);
                    Object.DestroyImmediate(s.spawned);
                    Object.DestroyImmediate(clone);
                }
                // Each clone wakes and enables its hierarchy, and the nested clone its own, in
                // the same order each time.
                Assert.Equal(new[] { "awake Outer(Clone)", "awake Inner(Clone)", "enable Inner(Clone)", "enable Outer(Clone)", "awake Child", "enable Child" }, Spawner.Order.GetRange(0, 6));
                Assert.Equal(Spawner.Order.GetRange(0, 6), Spawner.Order.GetRange(6, 6));
                Assert.Equal(Spawner.Order.GetRange(0, 6), Spawner.Order.GetRange(12, 6));
            }
            finally
            {
                Object.DestroyImmediate(outer);
                Object.DestroyImmediate(inner);
            }
        }

        [Fact]
        public void AnInstantiateFromOnAfterDeserializeGetsAMapOfItsOwn()
        {
            var inner = new GameObject("Inner");
            var innerCallbacks = inner.AddComponent<Callbacks>();
            innerCallbacks.self = innerCallbacks;
            var outer = new GameObject("Outer");
            var outerCallbacks = outer.AddComponent<Callbacks>();
            outerCallbacks.prefab = inner;
            outerCallbacks.self = outerCallbacks;
            try
            {
                for (int round = 0; round < 3; round++)
                {
                    var clone = Object.Instantiate(outer);
                    var c = clone.GetComponent<Callbacks>();
                    Assert.Same(c, c.self);
                    Assert.Same(inner, c.prefab);
                    var nested = c.spawned.GetComponent<Callbacks>();
                    Assert.Same(nested, nested.self);
                    Object.DestroyImmediate(c.spawned);
                    Object.DestroyImmediate(clone);
                }
            }
            finally
            {
                Object.DestroyImmediate(outer);
                Object.DestroyImmediate(inner);
            }
        }
    }
}
