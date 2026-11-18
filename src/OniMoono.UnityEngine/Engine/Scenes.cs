using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine
{
    /// A loaded scene: its identity and its root GameObjects in hierarchy order.
    internal sealed class SceneRecord
    {
        public int Handle;
        public string Name = "";
        public string Path = "";
        public int BuildIndex = -1;
        public bool IsLoaded;
        public bool IsDontDestroyOnLoad;
        /// Root GameObjects in hierarchy order. Main thread only.
        public readonly SceneRoots Roots = new();
    }

    /// Registry of loaded scenes, the active scene and the DontDestroyOnLoad scene.
    internal static class Scenes
    {
        static int nextHandle = 1;
        static readonly List<SceneRecord> loaded = new();

        /// Unity's special scene that holds DontDestroyOnLoad objects. It never unloads and is
        /// not listed by SceneManager.
        public static readonly SceneRecord DontDestroyOnLoadScene = new()
        {
            Handle = -12, Name = "DontDestroyOnLoad", BuildIndex = -1, IsLoaded = true, IsDontDestroyOnLoad = true,
        };

        public static SceneRecord Active { get; private set; }

        public static IReadOnlyList<SceneRecord> Loaded => loaded;

        public static SceneRecord Create(string name, string path, int buildIndex)
        {
            var s = new SceneRecord { Handle = nextHandle++, Name = name, Path = path, BuildIndex = buildIndex, IsLoaded = true };
            loaded.Add(s);
            return s;
        }

        public static void SetActive(SceneRecord scene) => Active = scene;

        public static void Remove(SceneRecord scene)
        {
            loaded.Remove(scene);
            scene.IsLoaded = false;
            if (Active == scene) Active = loaded.Count > 0 ? loaded[0] : null;
        }

        /// All scenes whose objects count as "in the world": loaded scenes plus DontDestroyOnLoad.
        public static IEnumerable<SceneRecord> WithDontDestroyOnLoad()
        {
            foreach (var s in loaded) yield return s;
            yield return DontDestroyOnLoadScene;
        }

        /// Makes a root GameObject a member of a scene (appended last among the roots).
        public static void AddRoot(SceneRecord scene, GameObject go)
        {
            scene.Roots.Add(go);
        }

        public static void RemoveRoot(SceneRecord scene, GameObject go)
        {
            scene.Roots.Remove(go);
        }

        /// Sets the scene of a GameObject and all its descendants.
        public static void SetSceneRecursive(GameObject go, SceneRecord scene)
        {
            go.Scene = scene;
            foreach (var child in go.Transform.Children) SetSceneRecursive(child.GameObject, scene);
        }

        public static void MoveToDontDestroyOnLoad(GameObject go)
        {
            var native = go.Alive ? go : null;
            var t = native.Transform;
            // Unity applies DontDestroyOnLoad to the root of the hierarchy only and warns otherwise.
            if (t.Parent is not null)
            {
                Debug.LogWarning("DontDestroyOnLoad only works for root GameObjects or components on root GameObjects.");
                return;
            }
            if (native.Scene == DontDestroyOnLoadScene || native.Scene == null && native.IsPersistent) return;
            if (native.Scene != null) RemoveRoot(native.Scene, native);
            AddRoot(DontDestroyOnLoadScene, native);
            SetSceneRecursive(native, DontDestroyOnLoadScene);
        }
    }
}
