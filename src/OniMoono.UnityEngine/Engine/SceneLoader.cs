using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Assets;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Engine
{
    /// Loads build scenes (levelN files) with Unity's single-mode semantics.
    internal static class SceneLoader
    {
        static int pendingBuildIndex = -1;

        /// A scene load is starting (main thread), the scenes it replaces still loaded.
        public static Action<int> LoadStarting;
        static bool firstSceneLoaded;

        static SceneLoader() => PlayerLoop.FrameStart += CompletePendingLoad;

        public static int BuildIndexOf(string nameOrPath)
        {
            var scenes = ProjectSettings.BuildScenes;
            for (int i = 0; i < scenes.Count; i++)
            {
                var path = scenes[i];
                if (string.Equals(path, nameOrPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(path), nameOrPath, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        /// SceneManager.LoadScene: the load completes at the start of the next frame.
        public static void Request(int buildIndex)
        {
            if (buildIndex < 0 || buildIndex >= ProjectSettings.BuildScenes.Count)
                throw new ArgumentException($"Scene with build index {buildIndex} couldn't be loaded because it has not been added to the build settings.");
            pendingBuildIndex = buildIndex;
        }

        static void CompletePendingLoad()
        {
            if (pendingBuildIndex < 0) return;
            int index = pendingBuildIndex;
            pendingBuildIndex = -1;
            LoadNow(index);
        }

        /// Replaces the loaded scenes with build scene `index` (the first scene uses this directly).
        public static SceneRecord LoadNow(int index)
        {
            // Before anything of the loaded scenes goes (game tweak loading-animation).
            LoadStarting?.Invoke(index);
            var previous = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (var old in Scenes.Loaded.ToArray()) Unload(old, notify: true);

            var path = ProjectSettings.BuildScenes[index];
            var scene = Scenes.Create(Path.GetFileNameWithoutExtension(path), path, index);
            Scenes.SetActive(scene);
            StartupStats.Mark($"scene {index} load starts");
            // Content bundles the game has not loaded by now it does not load at start-up.
            if (firstSceneLoaded)
            {
                Assets.BundlePrefetch.Drop();
                StartupProgress.Checkpoint("scene1");
            }
            firstSceneLoaded = true;
            var file = AssetSystem.Get("level" + index);
            ObjectLoader.BeginSceneFile(file, scene);
            var gameObjects = new List<GameObjectNative>();
            try
            {
                int done = 0;
                foreach (var info in file.Objects)
                {
                    Platform.MainThreadPump.Maybe();
                    if (index == 0) StartupProgress.PhaseFraction((double)done++ / file.Objects.Count);
                    var obj = ObjectLoader.Load(file, info.PathId);
                    if (obj is GameObject go) gameObjects.Add((GameObjectNative)go.Native);
                }
            }
            finally
            {
                ObjectLoader.EndSceneFile(file);
            }

            foreach (var go in gameObjects)
            {
                go.Scene = scene;
                if (go.Transform.Parent == null) scene.Roots.Add(go);
            }
            StartupStats.Mark($"scene {index} objects loaded");
            Time.LevelLoaded();
            Activate(scene);
            StartupStats.Mark($"scene {index} awake and enabled");
            LoadProgress.Mark(LoadProgress.SceneLoaded);
            EngineLog.Engine($"Loaded scene {scene.Name} ({gameObjects.Count} GameObjects)");
            PlayerLoop.LogVideoMemory($"scene {scene.Name} loaded");
            UnityEngine.SceneManagement.SceneManager.RaiseActiveSceneChanged(previous, scene);
            UnityEngine.SceneManagement.SceneManager.RaiseSceneLoaded(scene);
            return scene;
        }

        /// Awake/OnEnable for a freshly loaded scene: active flags for every object first, then
        /// engine components, then scripts in script execution order (load order within an order).
        static void Activate(SceneRecord scene)
        {
            var active = new List<GameObjectNative>();
            void Walk(GameObjectNative go, bool parentActive)
            {
                go.ActiveInHierarchy = parentActive && go.ActiveSelf;
                if (go.ActiveInHierarchy) active.Add(go);
                foreach (var child in go.Transform.Children) Walk(child.GameObject, go.ActiveInHierarchy);
            }
            foreach (var root in scene.Roots) Walk(root, true);

            Lifecycle.AwakeAndEnableAll(active);
        }

        /// Player shutdown: every scene's objects are destroyed (OnDisable, OnDestroy), the
        /// DontDestroyOnLoad ones included.
        public static void UnloadAll()
        {
            foreach (var scene in Scenes.WithDontDestroyOnLoad().ToArray()) Unload(scene, notify: false);
        }

        /// Destroys a scene's objects (DontDestroyOnLoad objects live in their own scene); a scene
        /// a load replaces is announced (SceneManager.sceneUnloaded) before it goes.
        static void Unload(SceneRecord scene, bool notify)
        {
            foreach (var root in scene.Roots.ToArray())
                if (root.Managed != null && root.Managed.Native != null) Lifecycle.DestroyNow(root.Managed, unloading: true);
            if (notify) UnityEngine.SceneManagement.SceneManager.RaiseSceneUnloaded(scene);
            Scenes.Remove(scene);
        }
    }

    /// Scene-level settings objects (RenderSettings, LightmapSettings, NavMeshSettings); the
    /// renderer reads RenderSettings data from the active scene.
    internal sealed class SceneSettingsObject : Object
    {
    }

    internal sealed class SceneSettingsBinder : Binder
    {
        public override Object Create(LoadRequest request)
        {
            var native = new NativeObject { Serialized = request.ReadNative() };
            return ObjectLoader.CreateNative<Object>(typeof(SceneSettingsObject), native);
        }

        public override void Fill(Object obj, LoadRequest request) { }
    }
}
