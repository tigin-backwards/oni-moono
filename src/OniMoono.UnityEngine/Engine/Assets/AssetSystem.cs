using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using OniMoono.Assets.Files;

namespace OniMoono.Engine.Assets
{
    /// The asset files the player can read: the data directory's SerializedFiles, the built-in
    /// resource files, and the contents of mounted AssetBundles. Files open lazily by name, as
    /// they are referenced through PPtr externals.
    internal static class AssetSystem
    {
        static string dataDir = "";
        /// A file of the data directory was opened or mapped (Initialize).
        static bool dataDirUsed;
        static readonly object gate = new();
        static readonly Dictionary<string, SerializedFile> files = new(StringComparer.Ordinal);
        /// Each node a window on its bundle's decompressed data (UnityFsArchive.Slice), an array or
        /// a mapped cache file (BundleCache), which stays alive while any of its nodes is mounted or held.
        static readonly Dictionary<string, ReadOnlyMemory<byte>> bundleNodes = new(StringComparer.Ordinal);
        /// Resource files mapped once each: concurrent first reads (texture and video threads) share
        /// one mapping (GetOrAdd's factory alone could run twice and leak a whole-file view).
        static int mappingsMade;
        /// Resource files mapped so far (tests).
        internal static int MappingsMade => mappingsMade;
        static readonly ConcurrentDictionary<string, Lazy<MemoryMappedViewAccessor>> streams = new(StringComparer.Ordinal);

        public static string DataDir => dataDir;

        /// Sets the data directory. Files opened and mapped are kept by name, so once one has been
        /// read the directory cannot change: another game build's .assets with this build's .resS
        /// (or the reverse) would read texels from the wrong places.
        public static void Initialize(string gameDataDir)
        {
            lock (gate)
            {
                if (dataDirUsed && Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDataDir)) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)))
                    throw new InvalidOperationException($"The asset files were read from {dataDir}; they cannot come from {gameDataDir} as well.");
                dataDir = gameDataDir;
            }
        }

        /// Maps a name as it appears in externals or loads (`sharedassets0.assets`,
        /// `library/unity default resources`, `archive:/CAB-…/CAB-…`) to a file key.
        public static string Canonical(string name)
        {
            var n = name.Replace('\\', '/');
            if (n.StartsWith("archive:/", StringComparison.OrdinalIgnoreCase))
                return "archive:/" + n["archive:/".Length..];
            var file = Path.GetFileName(n);
            if (file.Equals("unity default resources", StringComparison.OrdinalIgnoreCase)) return "Resources/unity default resources";
            if (file.Equals("unity_builtin_extra", StringComparison.OrdinalIgnoreCase)) return "Resources/unity_builtin_extra";
            return file;
        }

        public static SerializedFile Get(string name)
        {
            var key = Canonical(name);
            lock (gate)
            {
                if (files.TryGetValue(key, out var f)) return f;
                if (key.StartsWith("archive:/", StringComparison.Ordinal))
                {
                    if (!bundleNodes.TryGetValue(key, out var bytes)) throw new FileNotFoundException("Asset file not mounted: " + name);
                    f = SerializedFile.Parse(key, bytes);
                }
                else
                {
                    dataDirUsed = true;
                    f = SerializedFile.Open(Path.Combine(dataDir, key));
                }
                files[key] = f;
                return f;
            }
        }

        /// The canonical key of a file (as used in object identities).
        public static string KeyOf(SerializedFile file) => Canonical(file.Name);

        /// The file a PPtr's fileId designates from `from`.
        public static SerializedFile ResolveExternal(SerializedFile from, int fileId)
            => fileId == 0 ? from : Get(from.Externals[fileId - 1].Path);

        /// ResolveExternal, or null for another bundle's file that is not mounted.
        public static SerializedFile TryResolveExternal(SerializedFile from, int fileId)
        {
            if (fileId == 0) return from;
            var path = from.Externals[fileId - 1].Path;
            var key = Canonical(path);
            if (key.StartsWith("archive:/", StringComparison.Ordinal))
                lock (gate)
                    if (!files.ContainsKey(key) && !bundleNodes.ContainsKey(key)) return null;
            return Get(path);
        }

        /// Mounts an AssetBundle's nodes (SerializedFiles and resource streams).
        public static List<string> MountBundle(UnityFsArchive archive) => Mount(ReadNodes(archive));

        /// Decompresses an archive's nodes (any thread): (node key, bytes in place). As in Unity,
        /// a bundle's nodes live under a directory named after its serialized file: `CAB-x` and
        /// `CAB-x.resS` are `archive:/CAB-x/CAB-x` and `archive:/CAB-x/CAB-x.resS` (the paths its
        /// objects and other bundles' externals use).
        public static List<(string Key, ReadOnlyMemory<byte> Data)> ReadNodes(UnityFsArchive archive)
        {
            var main = archive.Nodes.FirstOrDefault(n => !IsResourceNode(n.Path))?.Path ?? archive.Nodes.FirstOrDefault()?.Path ?? "";
            var nodes = new List<(string, ReadOnlyMemory<byte>)>();
            foreach (var node in archive.Nodes) nodes.Add(("archive:/" + main + "/" + node.Path, archive.Slice(node)));
            return nodes;
        }

        static bool IsResourceNode(string path) => path.EndsWith(".resS", StringComparison.Ordinal) || path.EndsWith(".resource", StringComparison.Ordinal);

        /// Mounts decompressed nodes; returns the keys of the serialized files among them.
        public static List<string> Mount(List<(string Key, ReadOnlyMemory<byte> Data)> nodes)
        {
            var serialized = new List<string>();
            lock (gate)
            {
                foreach (var (key, data) in nodes)
                {
                    bundleNodes[key] = data;
                    if (!IsResourceNode(key)) serialized.Add(key);
                }
            }
            return serialized;
        }

        /// Forgets a bundle's nodes and the files parsed from them.
        public static void UnmountBundle(IEnumerable<string> nodeKeys)
        {
            lock (gate)
                foreach (var key in nodeKeys)
                {
                    bundleNodes.Remove(key);
                    files.Remove(key);
                }
        }

        /// A mounted bundle node's bytes (a resource file a texture reads from), or empty for a file
        /// of the game's data folder or a node not mounted.
        public static ReadOnlyMemory<byte> MountedNode(string path)
        {
            var key = Canonical(path);
            if (!key.StartsWith("archive:/", StringComparison.Ordinal)) return default;
            lock (gate) return bundleNodes.GetValueOrDefault(key);
        }

        public static bool IsMounted(string nodeKey)
        {
            lock (gate) return bundleNodes.ContainsKey(nodeKey);
        }

        /// Reads `size` bytes at `offset` of a streamed resource file (.resS / .resource).
        public static byte[] ReadStream(string path, long offset, long size)
        {
            var result = new byte[size];
            ReadStreamInto(path, offset, result);
            return result;
        }

        /// Reads `destination.Length` bytes at `offset` of a streamed resource file into
        /// `destination`. Thread-safe.
        public static unsafe void ReadStreamInto(string path, long offset, Span<byte> destination)
        {
            var key = Canonical(path);
            lock (gate)
            {
                if (bundleNodes.TryGetValue(key, out var node))
                {
                    node.Span.Slice(checked((int)offset), destination.Length).CopyTo(destination);
                    return;
                }
            }
            var view = streams.GetOrAdd(key, k => new Lazy<MemoryMappedViewAccessor>(() =>
            {
                System.Threading.Interlocked.Increment(ref mappingsMade);
                string path;
                lock (gate)
                {
                    dataDirUsed = true;
                    path = Path.Combine(dataDir, k);
                }
                var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                try
                {
                    return file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));
            MemoryMappedViewAccessor mapped;
            try
            {
                mapped = view.Value;
            }
            catch
            {
                // A failed mapping is tried again by the next read (only this one is removed: another
                // thread may have mapped the file since).
                streams.TryRemove(new KeyValuePair<string, Lazy<MemoryMappedViewAccessor>>(key, view));
                throw;
            }
            // A range past the mapped file (a .assets file that does not go with this .resS)
            // throws instead of reading unmapped memory.
            if (offset < 0 || offset > mapped.Capacity - destination.Length)
                throw new InvalidDataException($"{key}: {destination.Length} bytes at {offset} are past its end ({mapped.Capacity} bytes).");
            // Straight from the mapping (no intermediate copy).
            byte* start = null;
            mapped.SafeMemoryMappedViewHandle.AcquirePointer(ref start);
            try
            {
                new ReadOnlySpan<byte>(start + mapped.PointerOffset + offset, destination.Length).CopyTo(destination);
            }
            finally
            {
                mapped.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }
}
