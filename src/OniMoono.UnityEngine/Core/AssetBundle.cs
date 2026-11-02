using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OniMoono.Assets.Files;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Serialization;

namespace UnityEngine
{
    public partial class AssetBundle
    {
        AssetBundleNative Data => (AssetBundleNative)NativeOrThrow;

        /// Loads the bundle at path; null (with Unity's error) when the file cannot be opened or
        /// its contents are already loaded from another bundle.
        public static AssetBundle LoadFromFile(string path)
        {
            Threads.AssertMainThread("AssetBundle.LoadFromFile");
            List<(string Key, ArraySegment<byte> Data)> nodes;
            try
            {
                nodes = BundlePrefetch.Take(path) ?? AssetSystem.ReadNodes(UnityFsArchive.Open(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Debug.LogError("Unable to open archive file: " + path);
                return null;
            }
            StartupProgress.Checkpoint("bundle");
            return AssetBundles.Mount(path, nodes);
        }

        /// Loads a bundle from its file's bytes, as LoadFromFile does (measured in the original
        /// player: null for null; Unity's errors, naming the bundle 'Memory', for bytes that are no
        /// bundle and for a bundle whose files are already loaded).
        public static AssetBundle LoadFromMemory(byte[] binary)
        {
            Threads.AssertMainThread("AssetBundle.LoadFromMemory");
            if (binary == null) return null;
            List<(string Key, ArraySegment<byte> Data)> nodes;
            try
            {
                nodes = AssetSystem.ReadNodes(UnityFsArchive.Parse(binary));
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Debug.LogError("Failed to decompress data for the AssetBundle 'Memory'.");
                return null;
            }
            return AssetBundles.Mount("Memory", nodes);
        }

        /// Loads a bundle from a stream of its file's bytes, read from its current position (a
        /// mod's embedded resource). Measured in the original player: a null stream throws
        /// ArgumentNullException (its message "stream", its parameter name Unity's sentence);
        /// bytes that are no bundle give null with "Unable to read header from archive file: " and
        /// "Failed to read data for the AssetBundle 'IO.Stream'."; a bundle whose files are already
        /// loaded gives null with Unity's error naming it 'IO.Stream'.
        public static AssetBundle LoadFromStream(Stream stream) => LoadFromStream(stream, 0, 0);

        public static AssetBundle LoadFromStream(Stream stream, uint crc) => LoadFromStream(stream, crc, 0);

        public static AssetBundle LoadFromStream(Stream stream, uint crc, uint managedReadBufferSize)
        {
            if (stream == null) throw new ArgumentNullException("ManagedStream object must be non-null", "stream");
            Threads.AssertMainThread("AssetBundle.LoadFromStream");
            var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            List<(string Key, ArraySegment<byte> Data)> nodes;
            try
            {
                nodes = AssetSystem.ReadNodes(UnityFsArchive.Parse(bytes.ToArray()));
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Debug.LogError("Unable to read header from archive file: ");
                Debug.LogError("Failed to read data for the AssetBundle 'IO.Stream'.");
                return null;
            }
            return AssetBundles.Mount("IO.Stream", nodes);
        }

        /// Reads and decompresses the bundle on a worker thread; it is mounted on the main thread
        /// at the start of a later frame, when the request completes.
        public static AssetBundleCreateRequest LoadFromFileAsync(string path)
        {
            Threads.AssertMainThread("AssetBundle.LoadFromFileAsync");
            var request = new AssetBundleCreateRequest();
            AssetBundles.StartAsync(request, path);
            return request;
        }

        public Object LoadAsset(string name) => LoadAsset(name, typeof(Object));

        public T LoadAsset<T>(string name) where T : Object => (T)LoadAsset(name, typeof(T));

        /// An asset of the bundle by path ("assets/…/x.prefab") or file name, with or without
        /// extension, case-insensitively; the first of the requested type.
        public Object LoadAsset(string name, Type type)
        {
            if (name == null) throw new NullReferenceException("The input asset name cannot be null.");
            if (name.Length == 0) throw new ArgumentException("The input asset name cannot be empty.");
            if (type == null) throw new NullReferenceException("The input type cannot be null.");
            var d = Data;
            var key = name.ToLowerInvariant();
            foreach (var (path, pptr) in d.Container)
            {
                if (path != key && Path.GetFileName(path) != key && Path.GetFileNameWithoutExtension(path) != key) continue;
                if (ObjectLoader.IsScriptAsset(d.File, pptr)) continue;
                var obj = ObjectLoader.Resolve(d.File, pptr);
                if (obj != null && type.IsInstanceOfType(obj)) return obj;
            }
            return null;
        }

        public string[] GetAllAssetNames() => Data.Container.Select(c => c.Path).Distinct().ToArray();

        /// The scenes of a streamed-scene bundle; none for an asset bundle (measured).
        public string[] GetAllScenePaths() => Data.IsStreamedSceneBundle ? Data.Container.Select(c => c.Path).Distinct().ToArray() : Array.Empty<string>();

        public Object[] LoadAllAssets() => LoadAllAssets(typeof(Object));

        public T[] LoadAllAssets<T>() where T : Object => LoadAllAssets(typeof(T)).Cast<T>().ToArray();

        /// Every asset of the bundle of the type, sub-assets included, in the bundle's order.
        public Object[] LoadAllAssets(Type type)
        {
            if (type == null) throw new NullReferenceException("The input type cannot be null.");
            var d = Data;
            var result = new List<Object>();
            var seen = new HashSet<Object>();
            foreach (var (_, pptr) in d.Container)
            {
                if (ObjectLoader.IsScriptAsset(d.File, pptr)) continue;
                var obj = ObjectLoader.Resolve(d.File, pptr);
                if (obj != null && type.IsInstanceOfType(obj) && seen.Add(obj)) result.Add(obj);
            }
            return result.ToArray();
        }

        /// Every bundle loaded and not yet unloaded, in load order.
        public static IEnumerable<AssetBundle> GetAllLoadedAssetBundles() => AssetBundles.Loaded.ToArray();

        /// Unloads every loaded bundle.
        public static void UnloadAllAssetBundles(bool unloadAllObjects)
        {
            Threads.AssertMainThread("AssetBundle.UnloadAllAssetBundles");
            foreach (var bundle in AssetBundles.Loaded.ToArray()) AssetBundles.Unload(bundle, unloadAllObjects);
        }

        /// Releases the bundle's files; with unloadAllLoadedObjects, every object loaded from it
        /// is destroyed as well.
        public void Unload(bool unloadAllLoadedObjects)
        {
            Threads.AssertMainThread("AssetBundle.Unload");
            AssetBundles.Unload(this, unloadAllLoadedObjects);
        }
    }

    public partial class AssetBundleCreateRequest
    {
        internal AssetBundle Result;

        public AssetBundle assetBundle => Result;
    }
}

namespace OniMoono.Engine.Assets
{
    using UnityEngine;

    internal sealed class AssetBundleNative : NativeObject
    {
        public List<(string Path, PPtr Pointer)> Container = new();
        public SerializedFile File;
        public List<string> NodeKeys = new();
        public bool IsStreamedSceneBundle;

        /// AssetBundle (class 142).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            IsStreamedSceneBundle = f.Contains("m_IsStreamedSceneAssetBundle") && f.Bool("m_IsStreamedSceneAssetBundle");
            foreach (Fields pair in f.Array("m_Container"))
                Container.Add((pair.String("first"), pair.Struct("second").PPtr("asset")));
        }
    }

    internal static class AssetBundles
    {
        static readonly List<(AssetBundleCreateRequest Request, string Path, Task<List<(string, ArraySegment<byte>)>> Read)> pending = new();
        static bool hooked;

        /// The bundles mounted and not unloaded (AssetBundle.GetAllLoadedAssetBundles).
        public static readonly List<AssetBundle> Loaded = new();

        public static AssetBundle Mount(string path, List<(string Key, ArraySegment<byte> Data)> nodes)
        {
            foreach (var (key, _) in nodes)
                if (AssetSystem.IsMounted(key))
                {
                    Debug.LogError($"The AssetBundle '{path}' can't be loaded because another AssetBundle with the same files is already loaded.");
                    return null;
                }
            var serialized = AssetSystem.Mount(nodes);
            foreach (var key in serialized)
            {
                var file = AssetSystem.Get(key);
                foreach (var info in file.Objects)
                {
                    if (file.TypeOf(info).ClassId != 142) continue;
                    var bundle = (AssetBundle)ObjectLoader.Load(file, info.PathId);
                    var native = (AssetBundleNative)bundle.NativeOrThrow;
                    native.File = file;
                    native.NodeKeys = nodes.Select(n => n.Key).ToList();
                    Loaded.Add(bundle);
                    return bundle;
                }
            }
            AssetSystem.UnmountBundle(nodes.Select(n => n.Key));
            Debug.LogError("Unable to open archive file: " + path);
            return null;
        }

        public static void StartAsync(AssetBundleCreateRequest request, string path)
        {
            if (!hooked)
            {
                hooked = true;
                PlayerLoop.EarlyUpdate += Pump;
            }
            pending.Add((request, path, Task.Run(() => AssetSystem.ReadNodes(UnityFsArchive.Open(path)))));
        }

        static void Pump()
        {
            for (int i = 0; i < pending.Count; i++)
            {
                var (request, path, read) = pending[i];
                if (!read.IsCompleted) continue;
                pending.RemoveAt(i--);
                if (read.IsFaulted) Debug.LogError("Unable to open archive file: " + path);
                else request.Result = Mount(path, read.Result);
                request.Complete();
            }
        }

        public static void Unload(AssetBundle bundle, bool unloadAllLoadedObjects)
        {
            var native = (AssetBundleNative)bundle.NativeOrThrow;
            if (unloadAllLoadedObjects)
            {
                var files = new HashSet<string>(native.NodeKeys, StringComparer.Ordinal);
                // Components go with their GameObjects (destroying one directly is refused).
                foreach (var obj in ObjectLoader.LoadedFrom(files))
                    if (obj != bundle && obj is not Component && obj != null) Object.DestroyImmediate(obj, true);
            }
            foreach (var key in native.NodeKeys) ObjectLoader.ForgetFile(key);
            AssetSystem.UnmountBundle(native.NodeKeys);
            Loaded.Remove(bundle);
            Object.DestroyImmediate(bundle, true);
        }
    }
}
