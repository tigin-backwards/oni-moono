using System;
using System.Collections.Generic;
using System.IO;

namespace OniMoono.GameTweaks
{
    /// What compact-report-history's store asks of the game's note key
    /// (ReportManager.NoteStorage.NoteEntries.NoteEntryKey): making one, its two fields, the
    /// comparer the game's dictionaries use, and the game's way of writing a value. A struct, so
    /// the store is compiled for it.
    public interface INoteKeys<TKey>
    {
        TKey Key(int hash, bool positive);
        int Hash(TKey key);
        bool Positive(TKey key);
        IEqualityComparer<TKey> Comparer { get; }
        void WriteValue(BinaryWriter writer, float value);
    }

    /// compact-report-history's store of report notes (CompactReports): plain .NET, with no game
    /// types (the tests compile it in), beside the game's own `entries`
    /// (Dictionary<entry id, Dictionary<key, value>>).
    ///
    /// A record per entry: its id, where its notes start and how many there are, or -1 while the
    /// entry is in `entries`. Records are in the order the game's dictionary holds its entries
    /// (the order each id was first added: the game never removes one); a record's notes are in
    /// the order of the entry's dictionary. Ids are found through a sorted index of the records
    /// made at the load and a dictionary of those added since.
    public sealed class ReportNoteStore<TKey, TKeys> where TKeys : struct, INoteKeys<TKey>
    {
        // Per record.
        int[] recId;
        int[] recStart;
        int[] recCount;
        int records;
        // Per note.
        int[] noteHash;
        float[] noteValue;
        byte[] notePositive;
        int notes;
        // Record by id.
        int[] sortedIds = Array.Empty<int>(), sortedRecs = Array.Empty<int>();
        readonly Dictionary<int, int> later = new Dictionary<int, int>();
        // While loading: record by id, and a long entry's notes by key.
        Dictionary<int, int> loading;
        Dictionary<long, int> longEntry;
        readonly TKeys keys = default;

        ReportNoteStore(int capacity)
        {
            recId = new int[Math.Max(capacity, 16)];
            recStart = new int[recId.Length];
            recCount = new int[recId.Length];
            noteHash = new int[Math.Max(capacity * 2, 16)];
            noteValue = new float[noteHash.Length];
            notePositive = new byte[noteHash.Length];
            loading = new Dictionary<int, int>(capacity);
        }

        public int Records => records;
        public int Notes => notes;
        public long Bytes => recId.Length * 12L + noteHash.Length * 9L + sortedIds.Length * 8L;

        /// NoteEntries.Deserialize's version 6 reading, into a store: `entries[id] = dictionary`
        /// keeps an id's first place with its last notes, `dictionary[key] = value` a key's first
        /// place with its last value.
        public static ReportNoteStore<TKey, TKeys> Read(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0) throw new ArgumentOutOfRangeException("capacity"); // the game's `new Dictionary(count)`
            var s = new ReportNoteStore<TKey, TKeys>(count);
            for (int j = 0; j < count; j++)
            {
                int id = reader.ReadInt32();
                int n = reader.ReadInt32();
                if (n < 0) throw new ArgumentOutOfRangeException("capacity");
                s.ReadEntry(reader, id, n);
            }
            s.FinishLoad();
            return s;
        }

        void ReadEntry(BinaryReader reader, int id, int count)
        {
            int start = notes;
            for (int k = 0; k < count; k++)
            {
                int hash = reader.ReadInt32();
                bool positive = reader.ReadBoolean();
                float value = reader.ReadSingle();
                int at;
                if (count <= 64) at = FindNote(start, notes - start, hash, positive);
                else
                {
                    // A long entry: a map of the keys seen, not a scan.
                    long key = ((long)hash << 1) | (positive ? 1L : 0L);
                    longEntry ??= new Dictionary<long, int>();
                    if (!longEntry.TryGetValue(key, out at))
                    {
                        at = -1;
                        longEntry[key] = notes;
                    }
                }
                if (at >= 0) noteValue[at] = value;
                else AppendNote(hash, positive, value);
            }
            longEntry?.Clear();
            if (loading.TryGetValue(id, out int rec))
            {
                recStart[rec] = start;
                recCount[rec] = notes - start;
            }
            else loading.Add(id, AppendRecord(id, start, notes - start));
        }

        /// A store for `entries` as they are (an older save the game read itself, or a colony
        /// started in this session): a record for each, in its order, all still in `entries`.
        public static ReportNoteStore<TKey, TKeys> Adopt(Dictionary<int, Dictionary<TKey, float>> entries)
        {
            var s = new ReportNoteStore<TKey, TKeys>(entries.Count);
            foreach (var id in entries.Keys) s.loading.Add(id, s.AppendRecord(id, 0, -1));
            s.FinishLoad();
            return s;
        }

        /// The sorted index of the records; the loading map goes.
        void FinishLoad()
        {
            sortedIds = new int[records];
            sortedRecs = new int[records];
            for (int i = 0; i < records; i++)
            {
                sortedIds[i] = recId[i];
                sortedRecs[i] = i;
            }
            Array.Sort(sortedIds, sortedRecs);
            loading = null;
            longEntry = null;
            // Room for a few days more; the arrays double past that.
            int roomRecords = records + 4096, roomNotes = notes + 16384;
            if (recId.Length > roomRecords)
            {
                Array.Resize(ref recId, roomRecords);
                Array.Resize(ref recStart, roomRecords);
                Array.Resize(ref recCount, roomRecords);
            }
            if (noteHash.Length > roomNotes)
            {
                Array.Resize(ref noteHash, roomNotes);
                Array.Resize(ref noteValue, roomNotes);
                Array.Resize(ref notePositive, roomNotes);
            }
        }

        int FindNote(int start, int count, int hash, bool positive)
        {
            byte p = positive ? (byte)1 : (byte)0;
            for (int i = start; i < start + count; i++)
                if (noteHash[i] == hash && notePositive[i] == p) return i;
            return -1;
        }

        void AppendNote(int hash, bool positive, float value)
        {
            if (notes == noteHash.Length)
            {
                int size = noteHash.Length * 2;
                Array.Resize(ref noteHash, size);
                Array.Resize(ref noteValue, size);
                Array.Resize(ref notePositive, size);
            }
            noteHash[notes] = hash;
            noteValue[notes] = value;
            notePositive[notes] = positive ? (byte)1 : (byte)0;
            notes++;
        }

        int AppendRecord(int id, int start, int count)
        {
            if (records == recId.Length)
            {
                int size = recId.Length * 2;
                Array.Resize(ref recId, size);
                Array.Resize(ref recStart, size);
                Array.Resize(ref recCount, size);
            }
            recId[records] = id;
            recStart[records] = start;
            recCount[records] = count;
            return records++;
        }

        bool TryFind(int id, out int rec)
        {
            if (later.TryGetValue(id, out rec)) return true;
            int i = Array.BinarySearch(sortedIds, id);
            if (i >= 0)
            {
                rec = sortedRecs[i];
                return true;
            }
            rec = -1;
            return false;
        }

        /// Before NoteEntries.Add(id, ...): an entry the store holds goes back to `entries`, so the
        /// write lands on the dictionary the game would have; an id the store does not know gets
        /// its record now, in the place the game's dictionary gives it.
        public void Adding(int id, Dictionary<int, Dictionary<TKey, float>> entries)
        {
            if (entries.ContainsKey(id)) return;
            if (!TryFind(id, out int rec)) later[id] = AppendRecord(id, 0, -1);
            else if (recCount[rec] >= 0) MoveRecordOut(rec, entries);
        }

        /// An entry the store holds into `entries` (today's report after the load, an entry a
        /// mod writes to).
        public void MoveOut(int id, Dictionary<int, Dictionary<TKey, float>> entries)
        {
            if (!entries.ContainsKey(id) && TryFind(id, out int rec) && recCount[rec] >= 0) MoveRecordOut(rec, entries);
        }

        /// As a dictionary built in the store's order, with the game's comparer: what the game
        /// would hold (a dictionary nothing was removed from enumerates in its order of adding).
        void MoveRecordOut(int rec, Dictionary<int, Dictionary<TKey, float>> entries)
        {
            int start = recStart[rec], count = recCount[rec];
            var d = new Dictionary<TKey, float>(count, keys.Comparer);
            for (int i = start; i < start + count; i++) d[keys.Key(noteHash[i], notePositive[i] != 0)] = noteValue[i];
            entries[recId[rec]] = d;
            recCount[rec] = -1;
        }

        /// An entry of `entries` into the store, out of `entries` (the day that ended); its record
        /// keeps its place.
        public void MoveIn(int id, Dictionary<int, Dictionary<TKey, float>> entries)
        {
            if (!entries.TryGetValue(id, out var d)) return;
            if (!TryFind(id, out int rec)) rec = later[id] = AppendRecord(id, 0, -1);
            int start = notes;
            foreach (var note in d) AppendNote(keys.Hash(note.Key), keys.Positive(note.Key), note.Value);
            recStart[rec] = start;
            recCount[rec] = notes - start;
            entries.Remove(id);
        }

        /// Every entry of `entries` into the store.
        public void MoveAllIn(Dictionary<int, Dictionary<TKey, float>> entries)
        {
            var ids = new int[entries.Count];
            entries.Keys.CopyTo(ids, 0);
            foreach (var id in ids) MoveIn(id, entries);
        }

        /// Where an entry's notes are when the store holds it (false: `entries` has it, or no one
        /// does; the game's own code reads it).
        public bool TryNotes(int id, Dictionary<int, Dictionary<TKey, float>> entries, out int start, out int count)
        {
            start = count = 0;
            if (entries.ContainsKey(id) || !TryFind(id, out int rec) || recCount[rec] < 0) return false;
            start = recStart[rec];
            count = recCount[rec];
            return true;
        }

        public int NoteHash(int note) => noteHash[note];
        public float NoteValue(int note) => noteValue[note];

        /// NoteEntries.Serialize's bytes: every entry in its record's place, from `entries` when it
        /// is there, else from the store; then any entry only `entries` knows (one put there by
        /// code other than NoteEntries.Add), in its order there.
        public void Write(BinaryWriter writer, Dictionary<int, Dictionary<TKey, float>> entries)
        {
            int stray = 0;
            foreach (var id in entries.Keys)
                if (!TryFind(id, out _)) stray++;
            int count = stray;
            for (int r = 0; r < records; r++)
                if (recCount[r] >= 0 || entries.ContainsKey(recId[r])) count++;
            writer.Write(count);
            for (int r = 0; r < records; r++)
            {
                if (entries.TryGetValue(recId[r], out var d)) WriteEntry(writer, recId[r], d);
                else if (recCount[r] >= 0)
                {
                    writer.Write(recId[r]);
                    writer.Write(recCount[r]);
                    for (int i = recStart[r], end = recStart[r] + recCount[r]; i < end; i++)
                    {
                        writer.Write(noteHash[i]);
                        writer.Write(notePositive[i] != 0);
                        keys.WriteValue(writer, noteValue[i]);
                    }
                }
            }
            if (stray > 0)
                foreach (var entry in entries)
                    if (!TryFind(entry.Key, out _)) WriteEntry(writer, entry.Key, entry.Value);
        }

        void WriteEntry(BinaryWriter writer, int id, Dictionary<TKey, float> d)
        {
            writer.Write(id);
            writer.Write(d.Count);
            foreach (var note in d)
            {
                writer.Write(keys.Hash(note.Key));
                writer.Write(keys.Positive(note.Key));
                keys.WriteValue(writer, note.Value);
            }
        }
    }
}
