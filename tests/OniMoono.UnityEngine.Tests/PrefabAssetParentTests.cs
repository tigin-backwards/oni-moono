using System;
using System.Collections.Generic;
using OniMoono.Engine;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// SetParent between scene objects and a prefab asset's transforms. ONI: Retro Edition
    /// instantiates a RawImage of each ProgressBarsConfig bar prefab (assets from Resources) and
    /// parents the copy under the prefab: the original logs nothing and the prefab holds the
    /// copy (tests/e2e/issue-133-retro-progress-bars.yaml, #133). An asset's own transform is
    /// still refused a scene parent, with Unity's message.
    public class PrefabAssetParentTests : EngineTest
    {
        static List<(string message, LogType type)> Logged(Action action)
        {
            var logged = new List<(string, LogType)>();
            Application.LogCallback main = (m, s, t) => logged.Add((m, t));
            Application.logMessageReceived += main;
            try { action(); }
            finally { Application.logMessageReceived -= main; }
            return logged;
        }

        /// A prefab as loaded from the game's files: no scene, persistent, inactive in the hierarchy.
        static GameObject Asset(string name, params string[] children)
        {
            var go = new GameObject(name);
            foreach (var c in children) new GameObject(c).transform.SetParent(go.transform, false);
            Scenes.RemoveRoot(go.Scene, go);
            Scenes.SetSceneRecursive(go, null);
            MarkPersistent(go);
            Lifecycle.UpdateActiveInHierarchy(go);
            return go;
        }

        static void MarkPersistent(GameObject go)
        {
            go.IsPersistent = true;
            foreach (var c in go.GetComponents<Component>()) c.EngineIsPersistent = true;
            foreach (var child in go.Transform.Children) MarkPersistent(child.GameObject);
        }

        [Fact]
        public void ASceneObjectMovesUnderAPrefabAssetWithoutAMessage()
        {
            var prefab = Asset("ProgressBar", "RawImage", "Bar");
            Assert.True(prefab.IsPersistent);
            var copy = Object.Instantiate(prefab.transform.Find("RawImage").gameObject);
            Assert.False(copy.IsPersistent);

            var logged = Logged(() => copy.transform.SetParent(prefab.transform, false));

            Assert.Empty(logged);
            Assert.Same(prefab.transform, copy.transform.parent);
            Assert.Equal(3, prefab.transform.childCount);
            Assert.Same(copy.transform, prefab.transform.GetChild(2));
            Assert.False(copy.activeInHierarchy);

            // An instance of the prefab has the copy too.
            var bar = Object.Instantiate(prefab);
            Assert.Equal(3, bar.transform.childCount);
            Assert.Equal("RawImage(Clone)", bar.transform.GetChild(2).name);
            Assert.True(bar.transform.GetChild(2).gameObject.activeInHierarchy);
        }

        [Fact]
        public void APrefabAssetsTransformIsRefusedASceneParent()
        {
            var prefab = Asset("ProgressBar", "RawImage");
            var child = prefab.transform.GetChild(0);
            var scene = new GameObject("scene").transform;

            var logged = Logged(() => child.SetParent(scene, false));

            Assert.Equal([("Setting the parent of a transform which resides in a Prefab Asset is disabled to prevent data corruption (GameObject: 'RawImage').", LogType.Error)], logged);
            Assert.Same(prefab.transform, child.parent);
            Assert.Equal(0, scene.childCount);
        }
    }
}
