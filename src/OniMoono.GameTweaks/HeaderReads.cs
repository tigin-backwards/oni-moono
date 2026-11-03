using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace OniMoono.GameTweaks
{
    /// The reads of `fast-load-screen` (FastLoadScreen) for one refresh of the Load screen, with
    /// no game types (the unit tests compile it in): `THeader` and `TInfo` are the game's
    /// SaveGame.Header and SaveGame.GameInfo.
    ///
    /// - A memo of each file's parsed header, kept for the refresh. An entry is used only for a
    ///   path in the listing being read now, and only while that listing's last write time is the
    ///   one the entry was read under; each use returns its own copy (`clone`). Failed reads are
    ///   never kept: whoever asks again reads the file again.
    /// - The raw headers (the 16-byte prefix and the header it sizes, read as the game reads them)
    ///   of a listing's files, read ahead on worker threads. Each is taken once, in whatever order
    ///   the caller asks; one the workers have not started is the caller's to read itself (so the
    ///   caller never waits behind the queue), and one that failed is handed back as null.
    public sealed class HeaderReads<THeader, TInfo>
    {
        /// Files read ahead on this many threads (the reads wait on the file system, not the CPU).
        public const int Threads = 8;
        /// Raw headers larger than this are not read ahead: the game's own read decides (real
        /// headers are under 1 KB; a corrupt size field can claim gigabytes).
        public const int MaxHeader = 1 << 20;

        readonly struct Entry
        {
            public readonly System.DateTime Stamp;
            public readonly THeader Header;
            public readonly TInfo Info;
            public Entry(System.DateTime stamp, THeader header, TInfo info) { Stamp = stamp; Header = header; Info = info; }
        }

        readonly Func<TInfo, TInfo> clone;
        readonly Dictionary<string, Entry> memo = new Dictionary<string, Entry>(StringComparer.Ordinal);
        Dictionary<string, (System.DateTime Stamp, int Slot)> listing;
        ReadAhead ahead;

        public int Hits { get; private set; }
        public int Stored { get; private set; }
        public int ReadAheadTaken { get; private set; }

        public HeaderReads(Func<TInfo, TInfo> clone) => this.clone = clone;

        /// A listing's files are about to be read, in this order: their read-ahead starts (files
        /// the memo holds for the same time are left out), unless `threads` is 0.
        public void Begin(IReadOnlyList<string> paths, IReadOnlyList<System.DateTime> stamps, int threads = Threads)
        {
            End();
            listing = new Dictionary<string, (System.DateTime, int)>(paths.Count, StringComparer.Ordinal);
            var toRead = new List<string>();
            for (int i = 0; i < paths.Count; i++)
            {
                int slot = -1;
                if (threads > 0 && !(memo.TryGetValue(paths[i], out var e) && e.Stamp == stamps[i]))
                {
                    slot = toRead.Count;
                    toRead.Add(paths[i]);
                }
                listing[paths[i]] = (stamps[i], slot);
            }
            if (toRead.Count > 0) ahead = new ReadAhead(toRead.ToArray(), threads);
        }

        /// The listing's reads are over: read-ahead not taken is dropped (its workers stop).
        public void End()
        {
            ahead?.Stop();
            ahead = null;
            listing = null;
        }

        /// The memo's header of `path`, for the listing being read: its own copy of the info.
        public bool TryGet(string path, out THeader header, out TInfo info)
        {
            if (listing != null && listing.TryGetValue(path, out var l) && memo.TryGetValue(path, out var e) && e.Stamp == l.Stamp)
            {
                Hits++;
                header = e.Header;
                info = clone(e.Info);
                return true;
            }
            header = default;
            info = default;
            return false;
        }

        /// A header read whole (`info` the caller's; the memo keeps a copy).
        public void Store(string path, THeader header, TInfo info)
        {
            if (listing == null || !listing.TryGetValue(path, out var l)) return;
            memo[path] = new Entry(l.Stamp, header, clone(info));
            Stored++;
        }

        /// The raw header read ahead for `path`, or null: the caller reads the file itself (not
        /// read ahead, not started yet, already taken, or the read failed).
        public byte[] TakeRaw(string path)
        {
            if (ahead == null || listing == null || !listing.TryGetValue(path, out var l) || l.Slot < 0) return null;
            var raw = ahead.Take(l.Slot);
            if (raw != null) ReadAheadTaken++;
            return raw;
        }

        /// What the game's SaveLoader.LoadHeader reads of a file, read the same way: one read of
        /// 16 bytes, the header's size from bytes 4-7 (little-endian, as FastReader reads it),
        /// then one read of that many bytes at offset 16, short reads leaving zeros. Throws where
        /// the game's read throws; also for a size over MaxHeader.
        public static byte[] ReadRaw(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var prefix = new byte[16];
                _ = stream.Read(prefix, 0, 16); // one read, as the game's (a short one leaves zeros)
                int size = BitConverter.ToInt32(prefix, 4);
                if (size < 0 || size > MaxHeader) throw new InvalidDataException("header size " + size);
                var raw = new byte[16 + size];
                Array.Copy(prefix, raw, 16);
                stream.Position = 16L;
                _ = stream.Read(raw, 16, size); // one read, as the game's (a short one leaves zeros)
                return raw;
            }
        }

        /// Raw headers read on worker threads, in the listing's order.
        sealed class ReadAhead
        {
            const int Free = 0, Reading = 1, Done = 2, Callers = 3;
            readonly string[] paths;
            readonly byte[][] raws;
            readonly int[] states;
            readonly object gate = new object();
            int next;
            volatile bool stopped;

            public ReadAhead(string[] paths, int threads)
            {
                this.paths = paths;
                raws = new byte[paths.Length][];
                states = new int[paths.Length];
                for (int t = Math.Min(threads, paths.Length); t > 0; t--)
                    new Thread(Work) { IsBackground = true, Name = "fast-load-screen reads" }.Start();
            }

            void Work()
            {
                while (!stopped)
                {
                    int i = Interlocked.Increment(ref next) - 1;
                    if (i >= paths.Length) return;
                    if (Interlocked.CompareExchange(ref states[i], Reading, Free) != Free) continue;
                    byte[] raw = null;
                    try { raw = ReadRaw(paths[i]); }
                    catch (Exception) { }
                    raws[i] = raw;
                    lock (gate)
                    {
                        Volatile.Write(ref states[i], Done);
                        Monitor.PulseAll(gate);
                    }
                }
            }

            public byte[] Take(int i)
            {
                int state = Interlocked.CompareExchange(ref states[i], Callers, Free);
                if (state == Free || state == Callers) return null;
                if (Volatile.Read(ref states[i]) != Done)
                    lock (gate)
                        while (Volatile.Read(ref states[i]) != Done) Monitor.Wait(gate);
                var raw = raws[i];
                raws[i] = null;
                return raw;
            }

            public void Stop() => stopped = true;
        }
    }
}
