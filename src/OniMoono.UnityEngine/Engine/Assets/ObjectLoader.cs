using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using OniMoono.Assets.Files;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Serialization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Engine.Assets
{
    /// One object being loaded.
    internal sealed class LoadRequest
    {
        public SerializedFile File;
        public SerializedFile.ObjectInfo Info;
        public int ClassId;
        public IReferenceResolver Resolver;
        /// Scene objects get the scene; everything else is a persistent asset.
        public SceneRecord Scene;
        public bool Persistent => Scene == null;
        public OniMoono.Assets.IO.DataReader Open() => File.OpenObject(Info);

        /// The object's fields, read as the file lays them out: with the type tree the file
        /// carries (an asset bundle, a mod's included, can come from another Unity version, whose
        /// layout of a class differs; Unity reads such a file by its tree), else the release one.
        public OniMoono.Assets.TypeTrees.Fields ReadNative()
        {
            var tree = File.TypeOf(Info).Tree;
            return tree != null
                ? OniMoono.Assets.TypeTrees.TypeTreeReader.ReadObject(tree, Open(), OniMoono.Assets.TypeTrees.TypeTreeDatabase.TrailingBytes(ClassId))
                : OniMoono.Assets.TypeTrees.TypeTreeReader.ReadNative(ClassId, Open());
        }
    }

    /// Materializes engine objects from asset files, lazily by PPtr. Each object is registered
    /// before its data is read, so references between objects (including cycles) resolve to the
    /// same instance. Loading is main-thread only, as in Unity.
    internal static class ObjectLoader
    {
        readonly record struct Key(string File, long PathId);

        /// Loaded objects by identity, as table handles: the cache does not keep them alive, so
        /// Resources.UnloadUnusedAssets can reclaim assets nothing else references.
        static readonly Dictionary<Key, IntPtr> loaded = new();
        /// Instance ids of assets, kept when they are unloaded so a reload gets the same id (as
        /// Unity's persistent-object remapper does).
        static readonly Dictionary<Key, int> assetIds = new();

        static readonly Queue<Action> afterLoad = new();
        static int depth;

        /// The scene a level file is being loaded into (null when loading assets).
        static readonly Dictionary<string, SceneRecord> sceneOfFile = new(StringComparer.Ordinal);

        /// Loading a scene makes its objects afresh: what survived the previous load of the same
        /// level (DontDestroyOnLoad objects, scene settings) is not taken into the new scene.
        public static void BeginSceneFile(SerializedFile file, SceneRecord scene)
        {
            ForgetFile(AssetSystem.KeyOf(file));
            sceneOfFile[AssetSystem.KeyOf(file)] = scene;
        }
        public static void EndSceneFile(SerializedFile file) => sceneOfFile.Remove(AssetSystem.KeyOf(file));

        public static Object Load(SerializedFile file, long pathId)
        {
            if (pathId == 0) return null;
            Platform.MainThreadPump.Maybe();
            var key = new Key(AssetSystem.KeyOf(file), pathId);
            if (loaded.TryGetValue(key, out var handle))
            {
                if (ObjectTable.Resolve(handle) is { } existing) return existing;
                loaded.Remove(key);
            }
            if (!file.ObjectsById.TryGetValue(pathId, out var info)) return null; // missing reference

            var type = file.TypeOf(info);
            var request = new LoadRequest
            {
                File = file,
                Info = info,
                ClassId = type.ClassId,
                Resolver = new FileResolver(file),
                Scene = sceneOfFile.GetValueOrDefault(key.File),
            };
            var binder = Binders.For(type.ClassId);
            depth++;
            try
            {
                var obj = binder.Create(request).AliveOrThrow();
                // A component's record is its engine state (none for scripts): its name is "".
                var native = obj.NativeRecord;
                // Objects loaded from files have positive instance ids; an asset keeps its id.
                int id;
                if (!request.Persistent) id = ObjectTable.NewInstanceId(true);
                else if (!assetIds.TryGetValue(key, out id)) assetIds[key] = id = ObjectTable.NewInstanceId(true);
                ObjectTable.SetInstanceId(obj, id);
                obj.EngineIsPersistent = request.Persistent;
                obj.EngineSource = new AssetSource(key.File, pathId);
                // Cached before filling: the object's own references can lead back to it.
                loaded[key] = obj.Handle;
                try
                {
                    binder.Fill(obj, request);
                }
                catch
                {
                    // Not left cached half-built: a later load reads it again (and fails again).
                    loaded.Remove(key);
                    throw;
                }
                if (key.File.StartsWith("Resources/", StringComparison.Ordinal))
                    lock (builtinLoads) builtinLoads.Add((key.File, pathId, type.ClassId, obj.GetType().Name, native is Rendering.ShaderNative shader ? shader.ShaderName : obj is Component ? "" : native.Name));
                return obj;
            }
            finally
            {
                depth--;
                if (depth == 0)
                    while (afterLoad.Count > 0) afterLoad.Dequeue()();
            }
        }

        /// Diagnostics: every object loaded from Unity's built-in resource files
        /// (`unity default resources`, `unity_builtin_extra`): file, path id, class id, type, name.
        static readonly HashSet<(string File, long PathId, int ClassId, string Type, string Name)> builtinLoads = new();

        public static List<(string File, long PathId, int ClassId, string Type, string Name)> BuiltinLoads()
        {
            lock (builtinLoads) return builtinLoads.OrderBy(b => b.File).ThenBy(b => b.Type).ThenBy(b => b.Name).ToList();
        }

        /// Work to run once the outermost load finishes (e.g. ScriptableObject Awake/OnEnable,
        /// which Unity calls after the whole object graph is in place).
        public static void AfterLoad(Action action) => afterLoad.Enqueue(action);

        /// The native class of the object a PPtr designates, without loading it (-1 if missing).
        public static int ClassIdOf(SerializedFile from, PPtr pptr)
        {
            if (pptr.IsNull) return -1;
            var file = AssetSystem.ResolveExternal(from, pptr.FileId);
            return file.ObjectsById.TryGetValue(pptr.PathId, out var info) ? file.TypeOf(info).ClassId : -1;
        }

        /// MonoScripts exist in player data but have no managed object in a player.
        public static bool IsScriptAsset(SerializedFile from, PPtr pptr) => ClassIdOf(from, pptr) == 115;

        /// A reference into another bundle that is not loaded resolves to null, as in Unity (a mod's
        /// prefab referring to a material in a dependency bundle the mod never loads; the original
        /// player loads such prefabs without complaint).
        public static Object Resolve(SerializedFile from, PPtr pptr)
        {
            if (pptr.IsNull) return null;
            var file = AssetSystem.TryResolveExternal(from, pptr.FileId);
            return file == null ? null : Load(file, pptr.PathId);
        }

        /// Forgets a destroyed or unloaded object.
        public static void Forget(AssetSource source) => loaded.Remove(new Key(source.File, source.PathId));

        /// Forgets every object loaded from a file (its bundle was unloaded).
        public static void ForgetFile(string fileKey)
        {
            var keys = new List<Key>();
            foreach (var k in loaded.Keys)
                if (k.File == fileKey) keys.Add(k);
            foreach (var k in keys) loaded.Remove(k);
        }

        /// The live objects loaded from any of the given files.
        public static List<Object> LoadedFrom(HashSet<string> fileKeys)
        {
            var result = new List<Object>();
            foreach (var (key, handle) in loaded)
                if (fileKeys.Contains(key.File) && ObjectTable.Resolve(handle) is { } obj && obj) result.Add(obj);
            return result;
        }

        /// Drops cache entries of objects that no longer exist.
        public static void Prune()
        {
            var dead = new List<Key>();
            foreach (var (key, handle) in loaded)
                if (ObjectTable.Resolve(handle) is null) dead.Add(key);
            foreach (var key in dead) loaded.Remove(key);
        }

        sealed class FileResolver(SerializedFile file) : IReferenceResolver
        {
            public Object Resolve(PPtr pptr) => ObjectLoader.Resolve(file, pptr);
        }

        /// Creates an engine object of a native class without running a managed constructor
        /// (Unity creates wrappers for native objects the same way) and binds it.
        public static T CreateNative<T>(Type type, NativeObject native) where T : Object
        {
            var obj = (T)RuntimeHelpers.GetUninitializedObject(type);
            obj.Bind(native);
            return obj;
        }

        /// CreateNative for a component: bound to its engine state (null for those without one),
        /// starting as INIT says. Its GameObject is set when the component is filled.
        public static Component CreateComponent(Type type, ComponentNative state, in ComponentInit init)
        {
            var obj = (Component)RuntimeHelpers.GetUninitializedObject(type);
            obj.BindComponent(state, in init);
            return obj;
        }
    }

    /// Turns one serialized object into an engine object.
    internal abstract class Binder
    {
        public abstract Object Create(LoadRequest request);
        public abstract void Fill(Object obj, LoadRequest request);
    }

    internal static partial class Binders
    {
        const int GameObjectId = 1, TransformId = 4, MonoBehaviourId = 114, RectTransformId = 224, TextAssetId = 49, MonoScriptId = 115;

        static readonly Dictionary<int, Binder> binders = new()
        {
            [GameObjectId] = new GameObjectBinder(),
            [TransformId] = new TransformBinder(),
            [RectTransformId] = new TransformBinder(),
            [MonoBehaviourId] = new ScriptBinder(),
            [TextAssetId] = new TextAssetBinder(),
            [74] = new Mecanim.AnimationClipBinder(), // AnimationClip
            [104] = new SceneSettingsBinder(), // RenderSettings
            [157] = new SceneSettingsBinder(), // LightmapSettings
            [196] = new SceneSettingsBinder(), // NavMeshSettings
        };

        static readonly Dictionary<int, Binder> generic = new();

        public static void Register(int classId, Binder binder) => binders[classId] = binder;

        static Dictionary<string, Type> engineTypes;

        /// The engine's UnityEngine.Object subclass for a native class name (any UnityEngine
        /// namespace: VideoPlayer lives in UnityEngine.Video, SpriteAtlas in UnityEngine.U2D…).
        public static Type EngineTypeOf(string nativeClassName)
        {
            if (engineTypes == null)
            {
                var map = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (var t in typeof(Object).Assembly.GetTypes())
                    if (typeof(Object).IsAssignableFrom(t) && t.Namespace != null && t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal) && !map.ContainsKey(t.Name))
                        map[t.Name] = t;
                engineTypes = map;
            }
            // Native classes exposed through another managed type at runtime.
            if (nativeClassName == "AnimatorController") return typeof(RuntimeAnimatorController);
            return engineTypes.GetValueOrDefault(nativeClassName);
        }

        public static Binder For(int classId)
        {
            if (binders.TryGetValue(classId, out var b)) return b;
            if (generic.TryGetValue(classId, out b)) return b;
            if (classId == MonoScriptId) throw new InvalidDataException("MonoScripts are resolved by the script table, not loaded as objects.");
            var info = TypeTreeDatabase.Get(classId);
            var engineType = EngineTypeOf(info.Name)
                ?? throw new InvalidDataException($"No engine type for native class {info.Name} ({classId}).");
            b = typeof(Component).IsAssignableFrom(engineType) ? new StoredComponentBinder(engineType) : new StoredAssetBinder(engineType);
            generic[classId] = b;
            return b;
        }
    }
}
