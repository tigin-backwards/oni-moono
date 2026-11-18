using System;
using System.Collections.Generic;
using OniMoono.Engine;
using OniMoono.Engine.Assets;

namespace UnityEngine
{
    public partial class Resources
    {
        /// Assets under a "Resources" folder, by path relative to it, without extension. Paths are
        /// matched case-insensitively (the container stores them lower-cased); when several assets
        /// share a path the first one of the requested type is returned.
        public static Object Load(string path) => Load(path, typeof(Object));

        /// Resource paths compare case-insensitively, and backslashes separate folders as slashes do
        /// (the game's tutorial video ids use them: "tutorials\locomotion").
        static string Key(string path) => path.Replace('\\', '/').ToLowerInvariant();

        public static T Load<T>(string path) where T : Object => (T)Load(path, typeof(T));

        public static Object Load(string path, Type systemTypeInstance)
        {
            Threads.AssertMainThread("Resources.Load");
            if (path == null) throw new ArgumentNullException(nameof(path));
            var key = Key(path);
            foreach (var entry in ProjectSettings.ResourceContainer)
            {
                if (entry.Path != key || ObjectLoader.IsScriptAsset(ProjectSettings.Managers, entry.Target)) continue;
                var obj = ObjectLoader.Resolve(ProjectSettings.Managers, entry.Target);
                if (obj != null && systemTypeInstance.IsInstanceOfType(obj)) return obj;
            }
            return null;
        }

        public static T[] LoadAll<T>(string path) where T : Object
        {
            var list = new List<T>();
            foreach (var o in LoadAll(path, typeof(T))) list.Add((T)o);
            return list.ToArray();
        }

        public static Object[] LoadAll(string path) => LoadAll(path, typeof(Object));

        /// Every asset at `path` or in folders below it.
        public static Object[] LoadAll(string path, Type systemTypeInstance)
        {
            Threads.AssertMainThread("Resources.LoadAll");
            var key = Key(path).TrimEnd('/');
            var result = new List<Object>();
            foreach (var entry in ProjectSettings.ResourceContainer)
            {
                if (!(key.Length == 0 || entry.Path == key || entry.Path.StartsWith(key + "/", StringComparison.Ordinal))) continue;
                if (ObjectLoader.IsScriptAsset(ProjectSettings.Managers, entry.Target)) continue;
                var obj = ObjectLoader.Resolve(ProjectSettings.Managers, entry.Target);
                if (obj != null && systemTypeInstance.IsInstanceOfType(obj) && !result.Contains(obj)) result.Add(obj);
            }
            var typed = (Object[])Array.CreateInstance(systemTypeInstance, result.Count);
            for (int i = 0; i < result.Count; i++) typed.SetValue(result[i], i);
            return typed;
        }

        /// Built-in engine assets by name (meshes such as "Quad.fbx", the default font…).
        public static T GetBuiltinResource<T>(string path) where T : Object => (T)GetBuiltinResource(typeof(T), path);

        /// "Arial.ttf" is the legacy built-in font's name before Unity renamed it LegacyRuntime.
        /// The shipped "unity default resources" holds one font, LegacyRuntime, whose m_FontNames
        /// is ["Arial"]; mods built for older Unity ask for Arial.ttf (every GetBuiltinResource
        /// call in the Workshop builds of out/ws does), and the original player gives them a font
        /// without an error (Geyser Swapper's labels draw in it), so the name resolves to that font.
        static readonly Dictionary<string, string> BuiltinAliases = new(StringComparer.Ordinal)
        {
            ["Arial.ttf"] = "LegacyRuntime.ttf",
        };

        public static Object GetBuiltinResource(Type type, string path)
        {
            var file = AssetSystem.Get("unity default resources");
            var name = System.IO.Path.GetFileNameWithoutExtension(BuiltinAliases.GetValueOrDefault(path ?? "", path));
            foreach (var info in file.Objects)
            {
                var obj = BuiltinCandidate(file, info, name);
                if (obj != null && type.IsInstanceOfType(obj)) return obj;
            }
            Debug.LogError($"The resource {path} could not be loaded from the resource file!");
            return null;
        }

        static Object BuiltinCandidate(OniMoono.Assets.Files.SerializedFile file, OniMoono.Assets.Files.SerializedFile.ObjectInfo info, string name)
        {
            // Named built-in assets carry m_Name first; read just that to avoid loading everything.
            var r = file.OpenObject(info);
            if (r.Length < 4) return null;
            int length = r.ReadInt32();
            if (length < 0 || length > r.Remaining) return null;
            var objName = System.Text.Encoding.UTF8.GetString(r.ReadSpan(length));
            return objName == name ? ObjectLoader.Load(file, info.PathId) : null;
        }

        public static T[] FindObjectsOfTypeAll<T>() where T : Object
        {
            var list = new List<T>();
            foreach (var o in FindObjectsOfTypeAll(typeof(T))) list.Add((T)o);
            return list.ToArray();
        }

        /// Every live object of the type: loaded assets, scene objects and inactive ones, in
        /// ascending instance-id order, as measured in the original player (UnityProbe
        /// FindOrder): the newest object made at run time first (ids count down from -1), then
        /// older ones, then the objects loaded from files. Objects destroyed in between, and
        /// whether they were components or ScriptableObjects, do not change the order. Mods take
        /// the first match (Duplicant Status Bar clones the first usable LocText), so the order
        /// of the table's slots, which are reused and filled from the loading thread, must not
        /// show through.
        public static Object[] FindObjectsOfTypeAll(Type type)
        {
            var result = new List<Object>();
            foreach (var o in ObjectTable.Objects())
                if (o && type.IsInstanceOfType(o)) result.Add(o);
            result.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            var typed = (Object[])Array.CreateInstance(type, result.Count);
            for (int i = 0; i < result.Count; i++) typed.SetValue(result[i], i);
            return typed;
        }

        /// Unloads every asset nothing references (see AssetUnloader); completes at once.
        public static AsyncOperation UnloadUnusedAssets()
        {
            AssetUnloader.UnloadUnused();
            var op = new AsyncOperation();
            op.Complete();
            return op;
        }
    }
}
