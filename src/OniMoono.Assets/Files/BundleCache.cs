using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OniMoono.Assets.IO;

namespace OniMoono.Assets.Files
{
    /// AssetBundles opened from files keep their decompressed data in a cache file, mapped,
    /// instead of in a managed array (docs/research/heap-reduction-designs.md §1): a bundle is
    /// decompressed once into `Folder/<name>-<path hash>-<content hash>.v<N>.bin` and mapped
    /// read-only from then on, so its 0.03-0.5 GB are clean file pages the system can drop and
    /// reread, not managed heap or commit. The windows on it (UnityFsArchive.Slice) hold the same
    /// bytes the in-memory decode would.
    ///
    /// - The key is the SHA-256 of the bundle file (in the name in part, in the header whole) and
    ///   FormatVersion: a changed bundle never meets an old cache file, whatever its size and time.
    /// - A miss decodes into a writable mapping of a temporary file (`.<pid>-<n>.tmp`, opened
    ///   exclusively, its space reserved up front), writes the header last, flushes it to disk
    ///   and renames it into place, then maps that. A cache file is read only when its header
    ///   (magic, version, key, length) and its length agree; any other is rebuilt.
    /// - A failure to write (disk full, a read-only folder) falls back to the managed array, as
    ///   before the cache, logged once; damage in the bundle itself is reported as without it.
    /// - Another launch writing the same file at once writes its own temporary file; the last
    ///   rename wins with the same bytes, or (Windows, where a mapped file cannot be replaced)
    ///   finds the file in place and maps it.
    /// - Clean-up: writing a bundle's cache file deletes that bundle's older ones (same path,
    ///   other content), and once a process temporary files no launch holds (older than two
    ///   minutes), older formats' files and the files of bundles no longer on disk.
    /// - A bundle whose blocks are all stored is mapped from its own file; none is written.
    /// - With Verify each bundle is also decoded in memory and compared byte for byte with the
    ///   mapping (a difference is logged, and the in-memory data used).
    /// LoadFromMemory and LoadFromStream bundles keep their arrays (UnityFsArchive.Parse).
    public static unsafe class BundleCache
    {
        public const int FormatVersion = 1;
        /// The cache file's header: magic, version, header size, data length, key, source path.
        public const int HeaderSize = 4096;
        static readonly byte[] Magic = "MoonoBC\0"u8.ToArray();
        static readonly TimeSpan TemporaryAge = TimeSpan.FromMinutes(2);

        /// The cache folder; null keeps every bundle in memory (UnityFsArchive.Open).
        public static string? Folder { get; private set; }
        public static bool Verify { get; private set; }
        static Action<string> log = _ => { };

        static readonly object gate = new();
        /// Mappings made, by the file mapped (a bundle loaded again maps nothing new).
        static readonly Dictionary<string, MappedMemory> mappings = new(StringComparer.Ordinal);
        static int temporaries, fallbacks;
        static bool cleanupStarted;

        public static void Configure(string? folder, bool verify, Action<string>? logLine)
        {
            Folder = string.IsNullOrEmpty(folder) ? null : Path.GetFullPath(folder);
            Verify = verify;
            log = logLine ?? (_ => { });
            lock (gate) cleanupStarted = false;
        }

        /// Tests: the next Open maps its file again instead of reusing this process's mapping.
        internal static void ForgetMappings()
        {
            lock (gate) mappings.Clear();
        }

        /// The archive at `path`, its data mapped from the cache (or, without a folder, after a
        /// failure to write, decoded into memory). Invalid data for a damaged bundle, as Open.
        public static UnityFsArchive Open(string path)
        {
            var folder = Folder;
            if (folder == null) return UnityFsArchive.Open(path);
            var file = File.ReadAllBytes(path);
            var layout = UnityFsArchive.ReadLayout(file);
            if (layout.DataLength == 0 || layout.DataLength > int.MaxValue) return UnityFsArchive.Parse(file);
            string name = Path.GetFileName(path);
            if (layout.Stored)
            {
                var stored = MapStored(path, layout);
                return stored == null ? UnityFsArchive.Parse(file) : Checked(name, file, layout, UnityFsArchive.FromData(layout, stored.Memory), null);
            }
            var key = SHA256.HashData(file);
            var cache = Path.Combine(folder, CacheName(path, key));
            StartCleanup(folder);
            var mapped = Map(cache, key, layout) ?? Write(folder, cache, path, file, key, layout);
            if (mapped == null) return UnityFsArchive.FromData(layout, DecodeInMemory(file, layout));
            return Checked(name, file, layout, UnityFsArchive.FromData(layout, mapped.Memory), cache);
        }

        /// `<file name>-<8 hex of the path's hash>-<32 hex of the content's>.v<N>.bin`.
        public static string CacheName(string path, byte[] key) => $"{Prefix(path)}{Convert.ToHexString(key, 0, 16).ToLowerInvariant()}.v{FormatVersion}.bin";

        /// The part of a bundle's cache file names that names its file (its older keys share it).
        static string Prefix(string path)
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
            var where = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)), 0, 4).ToLowerInvariant();
            var name = new StringBuilder();
            foreach (char c in Path.GetFileName(path)) name.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_');
            return $"{name}-{where}-";
        }

        static byte[] DecodeInMemory(byte[] file, UnityFsArchive.Layout layout)
        {
            var data = new byte[layout.DataLength];
            UnityFsArchive.Decode(file, layout, data);
            return data;
        }

        /// The mapping of a valid cache file, or null (none, or one to rebuild: deleted).
        static MappedMemory? Map(string cache, byte[] key, UnityFsArchive.Layout layout)
        {
            lock (gate)
                if (mappings.TryGetValue(cache, out var made)) return made;
            FileStream stream;
            try
            {
                stream = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"bundle cache: {Path.GetFileName(cache)} could not be opened ({e.GetType().Name}: {e.Message}); rebuilding it");
                return null;
            }
            string? wrong;
            try
            {
                wrong = Validate(stream, key, layout.DataLength);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                wrong = e.Message;
            }
            if (wrong != null)
            {
                stream.Dispose();
                log($"bundle cache: {Path.GetFileName(cache)} {wrong}; rebuilding it");
                TryDelete(cache);
                return null;
            }
            try
            {
                var mapped = new MappedMemory(stream, HeaderSize, checked((int)layout.DataLength));
                lock (gate)
                {
                    if (mappings.TryGetValue(cache, out var other)) return other;
                    mappings[cache] = mapped;
                }
                return mapped;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"bundle cache: {Path.GetFileName(cache)} could not be mapped ({e.GetType().Name}: {e.Message})");
                return null;
            }
        }

        /// Why the open cache file is not the one for `key`, or null when it is.
        static string? Validate(FileStream stream, byte[] key, long dataLength)
        {
            if (stream.Length != HeaderSize + dataLength) return $"is {stream.Length} bytes, not {HeaderSize + dataLength}";
            var header = new byte[HeaderSize];
            stream.Position = 0;
            stream.ReadExactly(header);
            var h = header.AsSpan();
            if (!h[..8].SequenceEqual(Magic)) return "has no cache header";
            if (BitConverter.ToInt32(header, 8) != FormatVersion || BitConverter.ToInt32(header, 12) != HeaderSize) return "is another format";
            if (BitConverter.ToInt64(header, 16) != dataLength) return "has another length";
            if (!h.Slice(24, 32).SequenceEqual(key)) return "has another key";
            return null;
        }

        static void WriteHeader(Span<byte> header, byte[] key, long dataLength, string source)
        {
            header.Clear();
            Magic.CopyTo(header);
            BitConverter.TryWriteBytes(header[8..], FormatVersion);
            BitConverter.TryWriteBytes(header[12..], HeaderSize);
            BitConverter.TryWriteBytes(header[16..], dataLength);
            key.CopyTo(header[24..]);
            var path = Encoding.UTF8.GetBytes(Path.GetFullPath(source));
            if (path.Length <= HeaderSize - 60)
            {
                BitConverter.TryWriteBytes(header[56..], path.Length);
                path.CopyTo(header[60..]);
            }
        }

        /// Decodes the bundle into a new cache file and maps it; null when the file cannot be
        /// written (the caller decodes into memory).
        static MappedMemory? Write(string folder, string cache, string source, byte[] file, byte[] key, UnityFsArchive.Layout layout)
        {
            var temporary = $"{cache}.{Environment.ProcessId}-{Interlocked.Increment(ref temporaries)}.tmp";
            long size = HeaderSize + layout.DataLength;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                Directory.CreateDirectory(folder);
                // Exclusive (on Linux an flock, which clean-up tests), with its space reserved,
                // so that a full disk fails here and not as a fault writing the mapping.
                using (var stream = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, PreallocationSize = size,
                }))
                {
                    stream.SetLength(size);
                    using (var map = MemoryMappedFile.CreateFromFile(stream, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true))
                    using (var view = map.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite))
                    {
                        byte* p = null;
                        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                        try
                        {
                            p += view.PointerOffset;
                            UnityFsArchive.Decode(file, layout, new Span<byte>(p + HeaderSize, checked((int)layout.DataLength)));
                            WriteHeader(new Span<byte>(p, HeaderSize), key, layout.DataLength, source);
                        }
                        finally
                        {
                            view.SafeMemoryMappedViewHandle.ReleasePointer();
                        }
                        view.Flush();
                    }
                    stream.Flush(flushToDisk: true);
                }
                try
                {
                    File.Move(temporary, cache, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Windows: another launch maps the file this would replace; it has the same
                    // bytes when its header says so (Map checks).
                    TryDelete(temporary);
                }
            }
            catch (InvalidDataException)
            {
                TryDelete(temporary);
                throw;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                TryDelete(temporary);
                if (Interlocked.Increment(ref fallbacks) == 1)
                    log($"bundle cache: {Path.GetFileName(source)} not cached in {folder} ({e.GetType().Name}: {e.Message}); bundles are kept in memory");
                return null;
            }
            var mapped = Map(cache, key, layout);
            if (mapped != null)
            {
                log($"bundle cache: {Path.GetFileName(source)} written ({layout.DataLength / 1e6:F1} MB in {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms)");
                DeleteOlder(folder, source, cache);
            }
            return mapped;
        }

        /// The bundle file's own data, mapped: its blocks are stored as they are.
        static MappedMemory? MapStored(string path, UnityFsArchive.Layout layout)
        {
            var full = Path.GetFullPath(path);
            try
            {
                var info = new FileInfo(full);
                string id = $"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                lock (gate)
                    if (mappings.TryGetValue(id, out var made)) return made;
                var mapped = new MappedMemory(full, layout.DataStart, checked((int)layout.DataLength));
                lock (gate)
                {
                    if (mappings.TryGetValue(id, out var other)) return other;
                    mappings[id] = mapped;
                }
                return mapped;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"bundle cache: {Path.GetFileName(path)} could not be mapped ({e.GetType().Name}: {e.Message}); it is kept in memory");
                return null;
            }
        }

        /// Verify: the mapping against the bundle decoded in memory, byte for byte.
        static UnityFsArchive Checked(string name, byte[] file, UnityFsArchive.Layout layout, UnityFsArchive archive, string? cache)
        {
            if (!Verify) return archive;
            var memory = DecodeInMemory(file, layout);
            var mapped = archive.Data.Span;
            int differs = mapped.SequenceEqual(memory) ? -1 : FirstDifference(mapped, memory);
            if (differs < 0)
            {
                log($"bundle cache verify: {name}: {memory.Length} bytes, the same");
                return archive;
            }
            log($"bundle cache verify: {name}: {memory.Length} bytes, DIFFERENT from byte {differs}; using the decoded bytes");
            if (cache != null)
            {
                lock (gate) mappings.Remove(cache);
                TryDelete(cache);
            }
            return UnityFsArchive.FromData(layout, memory);
        }

        static int FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) return i;
            return Math.Min(a.Length, b.Length);
        }

        /// Deletes the bundle's cache files of other content (an earlier game or mod version).
        static void DeleteOlder(string folder, string source, string keep)
        {
            try
            {
                foreach (var other in Directory.EnumerateFiles(folder, Prefix(source) + "*.bin"))
                    if (other.EndsWith(".bin", StringComparison.Ordinal) && !string.Equals(Path.GetFullPath(other), Path.GetFullPath(keep), StringComparison.Ordinal))
                    {
                        lock (gate) mappings.Remove(other);
                        if (TryDelete(other)) log($"bundle cache: deleted {Path.GetFileName(other)} (an older version of {Path.GetFileName(source)})");
                    }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        /// Tests: false leaves the clean-up to them (Cleanup).
        internal static bool AutoCleanup = true;

        static void StartCleanup(string folder)
        {
            lock (gate)
            {
                if (cleanupStarted || !AutoCleanup) return;
                cleanupStarted = true;
            }
            new Thread(() => Cleanup(folder)) { Name = "Bundle cache clean-up", IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }

        /// Temporary files of launches that ended without renaming them, cache files of older
        /// formats, and cache files whose bundle is gone (an uninstalled DLC or mod, a removed
        /// install). A file mapped by a running launch cannot be deleted on Windows: it stays.
        internal static void Cleanup(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    try
                    {
                        if (path.EndsWith(".tmp", StringComparison.Ordinal))
                        {
                            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TemporaryAge) continue;
                            // Held by a launch writing it: the exclusive open fails.
                            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                            if (TryDelete(path)) log($"bundle cache: deleted {Path.GetFileName(path)} (left by an earlier launch)");
                        }
                        else if (path.EndsWith(".bin", StringComparison.Ordinal))
                        {
                            var why = Stale(path);
                            if (why != null && TryDelete(path)) log($"bundle cache: deleted {Path.GetFileName(path)} ({why})");
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        /// Why a cache file is of no use any more, or null: an older format, or its bundle gone.
        static string? Stale(string path)
        {
            var header = new byte[HeaderSize];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length < HeaderSize) return "damaged";
                stream.ReadExactly(header);
            }
            if (!header.AsSpan(0, 8).SequenceEqual(Magic)) return "damaged";
            int version = BitConverter.ToInt32(header, 8);
            // A newer format's (a newer player's beside this one) is left to it.
            if (version < FormatVersion) return "an older format";
            if (version > FormatVersion) return null;
            int length = BitConverter.ToInt32(header, 56);
            if (length <= 0 || length > HeaderSize - 60) return null;
            var source = Encoding.UTF8.GetString(header, 60, length);
            return File.Exists(source) ? null : "its bundle is gone";
        }

        static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
