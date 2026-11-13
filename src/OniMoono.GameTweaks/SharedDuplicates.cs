using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `shared-duplicates` (exact): identical copies the game makes of the same
    /// immutable data become one object. On a late-game colony (cycle 17,000) about 0.3 GiB of
    /// its 6.5 GiB heap was such copies (docs/research/heap-reduction-designs.md §4):
    ///
    /// - Strings read while a save loads. The serializer reads every string with
    ///   FastReader.ReadKleiString, which decodes a new string each time: the colony's report
    ///   history alone held 3.7 M copies of the Duplicants' and critters' names and of note
    ///   contexts (about 107,000 copies of each name). While SaveLoader.Load(string) runs, the
    ///   loading thread keeps a pool keyed by the string's UTF-8 bytes, and a read of bytes
    ///   already seen returns the string decoded from them the first time (the same decoder,
    ///   the same bytes: the same characters). The pool keeps its own copy of the bytes and is
    ///   dropped when the load ends, so it keeps nothing alive. Strings over 512 bytes, empty
    ///   or null ones and reads that would fail are left to the game's own code.
    /// - Reach tables. OffsetGroups.BuildReachabilityTable caches its table by the array of
    ///   placement offsets it is given, but Deconstructable, Demolishable and rotated
    ///   Constructables pass a new array for every building, so each building got its own
    ///   table (and the cache kept every one, forever). The offsets are made one array per
    ///   distinct content before the call, so buildings of the same footprint share the table
    ///   the game builds for it: the same rows in the same order, as the table depends only on
    ///   the offsets' values.
    /// - Default work animations. Every Workable carries its own arrays {working_pre,
    ///   working_loop} and {working_pst} (workAnims, workingPstComplete, workingPstFailed;
    ///   copied for every clone). When a Workable spawns, arrays holding exactly those
    ///   defaults are replaced with one shared copy each.
    ///
    /// Why sharing changes nothing (checked in the game's code and the IL of 2,900 Workshop
    /// mods; heap-reduction-designs.md §4.2): strings are immutable, and nothing locks on a
    /// string, keys an identity table with one, mutates one through unsafe code or compares
    /// two read strings by reference (the few mods that compare strings by reference use it as
    /// a fast path before comparing values, or for their own payloads over 512 bytes); a
    /// Duplicant renamed after the load gets a new string, and its old report entries keep the
    /// old name, as before. No code writes into the reach tables, their rows or the default
    /// animation arrays: the game and mods only read them, or replace the whole array.
    [GameMember(GameUse.Prefix | GameUse.Finalizer, "System.Boolean SaveLoader::Load(System.String)")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.String FastReader::ReadKleiString()", "a586e97c36a864c9 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Uses, "System.Int32 FastReader::idx")]
    [GameMember(GameUse.Uses, "System.Byte[] FastReader::bytes")]
    [GameMember(GameUse.Prefix, "CellOffset[][] OffsetGroups::BuildReachabilityTable(CellOffset[],CellOffset[][],CellOffset[])")]
    [GameMember(GameUse.Postfix, "System.Void Workable::OnSpawn()")]
    [GameMember(GameUse.Uses, "HashedString[] Workable::workAnims")]
    [GameMember(GameUse.Uses, "HashedString[] Workable::workingPstComplete")]
    [GameMember(GameUse.Uses, "HashedString[] Workable::workingPstFailed")]
    public static class SharedDuplicates
    {
        public const string Name = "shared-duplicates";

        /// Longer strings are rarely repeated, and some mods keep their own state in one.
        const int MaxPooledBytes = 512;

        public static void Install(Harmony harmony)
        {
            var load = AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.Load), [typeof(string)]);
            harmony.Patch(load, prefix: new HarmonyMethod(typeof(SharedDuplicates), nameof(LoadStarting)),
                finalizer: new HarmonyMethod(typeof(SharedDuplicates), nameof(LoadEnded)));
            harmony.Patch(AccessTools.Method(typeof(FastReader), nameof(FastReader.ReadKleiString)),
                prefix: new HarmonyMethod(typeof(SharedDuplicates), nameof(ReadKleiString)));
            harmony.Patch(AccessTools.Method(typeof(OffsetGroups), nameof(OffsetGroups.BuildReachabilityTable)),
                prefix: new HarmonyMethod(typeof(SharedDuplicates), nameof(BuildingReachabilityTable)));
            harmony.Patch(AccessTools.Method(typeof(Workable), "OnSpawn"),
                postfix: new HarmonyMethod(typeof(SharedDuplicates), nameof(WorkableSpawned)));
        }

        // ---- Strings read while a save loads ----

        /// The loading thread's pool, between SaveLoader.Load's start and end.
        [ThreadStatic] static StringPool pool;

        static void LoadStarting() => pool = new StringPool();

        static Exception LoadEnded(Exception __exception)
        {
            var done = pool;
            pool = null;
            if (done != null)
                global::Debug.Log($"{Name}: {done.Reads:N0} strings read while loading, {done.Count:N0} distinct; "
                                  + $"{done.SavedBytes / (1024.0 * 1024.0):F1} MiB of copies not made");
            return __exception;
        }

        /// FastReader.ReadKleiString: an int length (-1 for null), then that many UTF-8 bytes.
        static bool ReadKleiString(FastReader __instance, ref string __result)
        {
            var p = pool;
            if (p == null) return true;
            var bytes = __instance.bytes;
            int at = __instance.idx;
            if (bytes == null || at < 0 || at > bytes.Length - 4) return true;
            // As the game reads it: the machine's byte order.
            int length = MemoryMarshal.Read<int>(new ReadOnlySpan<byte>(bytes, at, 4));
            if (length <= 0 || length > MaxPooledBytes || length > bytes.Length - at - 4) return true;
            __result = p.Get(bytes, at + 4, length);
            __instance.idx = at + 4 + length;
            return false;
        }

        /// Strings by their UTF-8 bytes. The keys are the pool's own copies of the bytes.
        sealed class StringPool
        {
            readonly Dictionary<Utf8Key, string> strings = new Dictionary<Utf8Key, string>(1 << 16, Utf8Key.Comparer);
            byte[] arena = new byte[1 << 16];
            int arenaUsed;
            public long Reads, SavedBytes;
            public int Count => strings.Count;

            public string Get(byte[] bytes, int offset, int length)
            {
                Reads++;
                var key = new Utf8Key(bytes, offset, length);
                if (strings.TryGetValue(key, out var known))
                {
                    // A string's size in .NET: 22 bytes of header and length, 2 a character, 8-aligned.
                    SavedBytes += (22 + 2 * known.Length + 7) & ~7;
                    return known;
                }
                var value = Encoding.UTF8.GetString(bytes, offset, length);
                if (arenaUsed + length > arena.Length)
                {
                    arena = new byte[Math.Max(arena.Length, length) * 2];
                    arenaUsed = 0;
                }
                Buffer.BlockCopy(bytes, offset, arena, arenaUsed, length);
                strings.Add(new Utf8Key(arena, arenaUsed, length, key.Hash), value);
                arenaUsed += length;
                return value;
            }
        }

        readonly struct Utf8Key
        {
            public static readonly IEqualityComparer<Utf8Key> Comparer = new KeyComparer();
            public readonly byte[] Bytes;
            public readonly int Offset, Length, Hash;

            public Utf8Key(byte[] bytes, int offset, int length)
            {
                Bytes = bytes;
                Offset = offset;
                Length = length;
                // FNV-1a.
                uint h = 2166136261;
                for (int i = offset, end = offset + length; i < end; i++) h = (h ^ bytes[i]) * 16777619;
                Hash = (int)h;
            }

            public Utf8Key(byte[] bytes, int offset, int length, int hash)
            {
                Bytes = bytes;
                Offset = offset;
                Length = length;
                Hash = hash;
            }

            sealed class KeyComparer : IEqualityComparer<Utf8Key>
            {
                public bool Equals(Utf8Key a, Utf8Key b) =>
                    a.Hash == b.Hash && a.Length == b.Length
                    && new ReadOnlySpan<byte>(a.Bytes, a.Offset, a.Length).SequenceEqual(new ReadOnlySpan<byte>(b.Bytes, b.Offset, b.Length));

                public int GetHashCode(Utf8Key k) => k.Hash;
            }
        }

        // ---- Reach tables ----

        /// One array per distinct list of placement offsets (main thread: buildings spawn there).
        static readonly Dictionary<CellOffset[], CellOffset[]> placements = new Dictionary<CellOffset[], CellOffset[]>(new OffsetsComparer());

        static void BuildingReachabilityTable(ref CellOffset[] area_offsets)
        {
            if (area_offsets == null) return;
            if (placements.TryGetValue(area_offsets, out var shared)) area_offsets = shared;
            else
            {
                // A copy stands for the content: the caller's array stays its own, as before.
                shared = (CellOffset[])area_offsets.Clone();
                placements.Add(shared, shared);
                area_offsets = shared;
            }
        }

        sealed class OffsetsComparer : IEqualityComparer<CellOffset[]>
        {
            public bool Equals(CellOffset[] a, CellOffset[] b) =>
                MemoryMarshal.AsBytes(new ReadOnlySpan<CellOffset>(a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<CellOffset>(b)));

            public int GetHashCode(CellOffset[] offsets)
            {
                int h = offsets.Length;
                foreach (var o in offsets) h = (h * 31 + o.x) * 31 + o.y;
                return h;
            }
        }

        // ---- Default work animations ----

        static readonly HashedString[] defaultWork = { "working_pre", "working_loop" };
        static readonly HashedString[] defaultPst = { "working_pst" };
        static readonly HashedString[] defaultPstFailed = { "working_pst" };

        static void WorkableSpawned(Workable __instance)
        {
            if (Same(__instance.workAnims, defaultWork)) __instance.workAnims = defaultWork;
            if (Same(__instance.workingPstComplete, defaultPst)) __instance.workingPstComplete = defaultPst;
            if (Same(__instance.workingPstFailed, defaultPstFailed)) __instance.workingPstFailed = defaultPstFailed;
        }

        static bool Same(HashedString[] a, HashedString[] b)
        {
            if (a == null || a == b || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].HashValue != b[i].HashValue) return false;
            return true;
        }
    }
}
