using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OniMoono.Assets.Files;

namespace OniMoono.Engine.Assets
{
    /// Opens the game's content bundles (StreamingAssets/*_bundle: the expansion's and each DLC's)
    /// on worker threads from start-up, so that AssetBundle.LoadFromFile, which the game calls for
    /// them one after another on the main thread during its start-up, finds them ready. U59's are
    /// each a single LZMA stream (about 1 GiB decompressed with every DLC), which only one thread
    /// can decode; U60's are LZ4 in 128 KiB blocks, read block by block (AssetSystem.OpenBundle),
    /// so a thread only reads the file through once, as the whole decode did, to have it in the
    /// system's cache when the blocks are read. A bundle is installed only with its DLC, so the files present are the
    /// ones the game loads; what it has not loaded by the time a scene other than the first
    /// loads is dropped. LoadFromFile gets the same nodes it would decode itself; a bundle whose
    /// decode failed, or whose file changed, is opened by LoadFromFile as before.
    /// ONI_BUNDLE_PREFETCH=0 turns it off. Hi-res texture bundles (hires_*) are left out: the
    /// game loads them asynchronously, and only with high-resolution textures on.
    internal static class BundlePrefetch
    {
        sealed class Entry
        {
            public string Path;
            public long Length;
            public DateTime Modified;
            public List<(string Key, NodeData Data)> Nodes;
            public bool Failed, Dropped;
            public readonly ManualResetEventSlim Done = new();
        }

        static readonly object gate = new();
        static readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

        public static bool Enabled = Environment.GetEnvironmentVariable("ONI_BUNDLE_PREFETCH") != "0";

        public static void Start(string streamingAssetsDir)
        {
            if (!Enabled || !Directory.Exists(streamingAssetsDir)) return;
            foreach (var path in Directory.EnumerateFiles(streamingAssetsDir, "*_bundle"))
            {
                var name = System.IO.Path.GetFileName(path);
                if (name.StartsWith("hires_", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(path);
                var entry = new Entry { Path = System.IO.Path.GetFullPath(path), Length = info.Length, Modified = info.LastWriteTimeUtc };
                lock (gate) entries[entry.Path] = entry;
                // A thread each: a decode takes seconds, and the pool's threads are the game's
                // and the engine's for short work.
                new Thread(() => Decode(entry)) { Name = "Bundle prefetch " + name, IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
            }
        }

        static void Decode(Entry entry)
        {
            try
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                var archive = AssetSystem.OpenBundle(entry.Path);
                if (archive.ReadsBlocks) ReadThrough(entry.Path);
                var nodes = AssetSystem.ReadNodes(archive);
                lock (gate)
                {
                    // Dropped meanwhile: nobody will take it.
                    if (!entry.Dropped) entry.Nodes = nodes;
                }
                StartupStats.Mark($"bundle {System.IO.Path.GetFileName(entry.Path)} {(archive.ReadsBlocks ? "opened" : "decoded")} in {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms (prefetch)");
            }
            catch (Exception)
            {
                // LoadFromFile opens it itself and reports what is wrong, as without the prefetch.
                entry.Failed = true;
            }
            finally
            {
                entry.Done.Set();
            }
        }

        /// Reads the file through (into one small buffer), so its pages are in the system's cache.
        static void ReadThrough(string path)
        {
            var buffer = new byte[1 << 20];
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            long at = 0;
            int read;
            while ((read = RandomAccess.Read(file, buffer, at)) > 0) at += read;
        }

        /// The decoded nodes of the bundle at `path`, waiting for its decode if it is under way;
        /// null when it was not prefetched, failed, or the file changed since.
        public static List<(string Key, NodeData Data)> Take(string path)
        {
            Entry entry;
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
            lock (gate)
            {
                if (!entries.Remove(full, out entry))
                {
                    StartupStats.Mark($"bundle {full}: not prefetched");
                    return null;
                }
            }
            long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!entry.Done.Wait(100)) Platform.MainThreadPump.Maybe();
            StartupStats.Mark($"bundle {System.IO.Path.GetFileName(full)} taken (waited {System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds:F0} ms, failed {entry.Failed})");
            if (entry.Failed || entry.Nodes == null) return null;
            try
            {
                var info = new FileInfo(full);
                if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc != entry.Modified) return null;
            }
            catch (IOException)
            {
                return null;
            }
            return entry.Nodes;
        }

        /// Forgets the bundles not taken (a scene other than the first has loaded).
        public static void Drop()
        {
            lock (gate)
            {
                if (entries.Count == 0) return;
                foreach (var entry in entries.Values)
                {
                    entry.Dropped = true;
                    entry.Nodes = null;
                }
                entries.Clear();
            }
        }
    }
}
