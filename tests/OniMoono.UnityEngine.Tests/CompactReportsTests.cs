using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// compact-report-history's store (src/OniMoono.GameTweaks/CompactReportsCore.cs, compiled
    /// in here), driven as CompactReports' patches drive it, against the game's own NoteEntries
    /// (ReportManager.NoteStorage.NoteEntries, U59 744825 and U60, copied below): every save's
    /// bytes and every entry's notes must be the game's.
    public class CompactReportsTests
    {
        struct Key
        {
            public int noteHash;
            public bool isPositive;
        }

        /// The game's NoteEntryKeyComparer.
        sealed class KeyComparer : IEqualityComparer<Key>
        {
            public bool Equals(Key a, Key b) => a.noteHash == b.noteHash && a.isPositive == b.isPositive;
            public int GetHashCode(Key a) => a.noteHash * (a.isPositive ? 1 : -1);
        }

        static readonly KeyComparer comparer = new KeyComparer();

        struct Keys : INoteKeys<Key>
        {
            public Key Key(int hash, bool positive) => new Key { noteHash = hash, isPositive = positive };
            public int Hash(Key key) => key.noteHash;
            public bool Positive(Key key) => key.isPositive;
            public IEqualityComparer<Key> Comparer => comparer;
            public void WriteValue(BinaryWriter writer, float value) => writer.Write(value);
        }

        /// The game's NoteEntries: Add, Serialize, Deserialize (version 6) and IterateNotes as it
        /// has them.
        sealed class GameNotes
        {
            public Dictionary<int, Dictionary<Key, float>> entries = new Dictionary<int, Dictionary<Key, float>>();

            public void Add(int report_entry_id, float value, int note_id)
            {
                if (!entries.TryGetValue(report_entry_id, out var value2))
                {
                    value2 = new Dictionary<Key, float>(comparer);
                    entries[report_entry_id] = value2;
                }
                var key = new Key { noteHash = note_id, isPositive = value > 0f };
                if (value2.ContainsKey(key)) value2[key] += value;
                else value2[key] = value;
            }

            public void Serialize(BinaryWriter writer)
            {
                writer.Write(entries.Count);
                foreach (var entry in entries)
                {
                    writer.Write(entry.Key);
                    writer.Write(entry.Value.Count);
                    foreach (var item in entry.Value)
                    {
                        writer.Write(item.Key.noteHash);
                        writer.Write(item.Key.isPositive);
                        writer.Write(item.Value);
                    }
                }
            }

            public void Deserialize(BinaryReader reader)
            {
                int num = reader.ReadInt32();
                entries = new Dictionary<int, Dictionary<Key, float>>(num);
                for (int j = 0; j < num; j++)
                {
                    int key = reader.ReadInt32();
                    int num2 = reader.ReadInt32();
                    var dictionary = new Dictionary<Key, float>(num2, comparer);
                    entries[key] = dictionary;
                    for (int k = 0; k < num2; k++)
                    {
                        var key2 = new Key { noteHash = reader.ReadInt32(), isPositive = reader.ReadBoolean() };
                        dictionary[key2] = reader.ReadSingle();
                    }
                }
            }

            public List<(int, int)> Notes(int id)
            {
                var notes = new List<(int, int)>();
                if (entries.TryGetValue(id, out var value))
                    foreach (var item in value) notes.Add((BitConverter.SingleToInt32Bits(item.Value), item.Key.noteHash));
                return notes;
            }

            public byte[] Save()
            {
                var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms)) Serialize(w);
                return ms.ToArray();
            }

            public static GameNotes Load(byte[] bytes)
            {
                var g = new GameNotes();
                using (var r = new BinaryReader(new MemoryStream(bytes))) g.Deserialize(r);
                return g;
            }
        }

        /// The game's NoteEntries with the tweak's patches (CompactReports): `Game.entries` is the
        /// field the patches share with the game.
        sealed class Tweaked
        {
            public GameNotes Game = new GameNotes();
            public ReportNoteStore<Key, Keys> Store;

            /// NoteEntries.Deserialize's prefix, then ReportManager.OnDeserialized's postfix
            /// (today's report back into `entries`).
            public static Tweaked Load(byte[] bytes, IEnumerable<int> today)
            {
                var t = new Tweaked();
                using (var r = new BinaryReader(new MemoryStream(bytes)))
                {
                    t.Store = ReportNoteStore<Key, Keys>.Read(r);
                    Assert.Equal(bytes.Length, r.BaseStream.Position);
                }
                t.Game.entries = new Dictionary<int, Dictionary<Key, float>>();
                foreach (var id in today) t.Store.MoveOut(id, t.Game.entries);
                return t;
            }

            /// An older save: the game's reading, then NoteEntries.Deserialize's postfix.
            public static Tweaked LoadRead(GameNotes read, IEnumerable<int> today)
            {
                var t = new Tweaked { Game = read };
                t.Store = ReportNoteStore<Key, Keys>.Adopt(read.entries);
                t.Store.MoveAllIn(read.entries);
                foreach (var id in today) t.Store.MoveOut(id, t.Game.entries);
                return t;
            }

            public void Add(int id, float value, int note)
            {
                Store?.Adding(id, Game.entries);
                Game.Add(id, value, note);
            }

            /// ReportManager.OnNightTime's postfix: the day that ended into the store.
            public void Night(IEnumerable<int> day)
            {
                Store ??= ReportNoteStore<Key, Keys>.Adopt(Game.entries);
                foreach (var id in day) Store.MoveIn(id, Game.entries);
            }

            public List<(int, int)> Notes(int id)
            {
                if (Store == null || !Store.TryNotes(id, Game.entries, out int start, out int n)) return Game.Notes(id);
                var notes = new List<(int, int)>();
                for (int i = start; i < start + n; i++) notes.Add((BitConverter.SingleToInt32Bits(Store.NoteValue(i)), Store.NoteHash(i)));
                return notes;
            }

            public byte[] Save()
            {
                if (Store == null) return Game.Save();
                var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms)) Store.Write(w, Game.entries);
                return ms.ToArray();
            }
        }

        static void Same(GameNotes game, Tweaked tweaked, IEnumerable<int> ids)
        {
            Assert.Equal(game.Save(), tweaked.Save());
            foreach (var id in ids) Assert.Equal(game.Notes(id), tweaked.Notes(id));
        }

        /// Saved notes as the game writes them, with repeats it would not write itself.
        static byte[] Bytes(params (int Id, (int Hash, bool Positive, float Value)[] Notes)[] entries)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(entries.Length);
                foreach (var (id, notes) in entries)
                {
                    w.Write(id);
                    w.Write(notes.Length);
                    foreach (var (hash, positive, value) in notes)
                    {
                        w.Write(hash);
                        w.Write(positive);
                        w.Write(value);
                    }
                }
            }
            return ms.ToArray();
        }

        static (int, bool, float) N(int hash, float value) => (hash, value > 0, value);

        /// An id saved twice keeps its first place with its last notes; a key twice in an entry,
        /// its first place with its last value (`entries[id] = ...`, `dictionary[key] = ...`); in
        /// a short entry (scanned) and a long one (over 64 notes, mapped).
        [Fact]
        public void RepeatedIdsAndKeysOnLoadReadAsTheGameReadsThem()
        {
            var longNotes = Enumerable.Range(0, 100).Select(i => N(i % 70, i + 1)).Concat(new[] { N(5, -2), N(5, -3), N(69, 9) }).ToArray();
            var bytes = Bytes(
                (7, new[] { N(1, 1), N(2, -1), N(1, 4), N(3, 2) }),
                (9, new[] { N(4, 1) }),
                (7, new[] { N(8, 1), N(8, 2), N(1, -5) }),
                (11, longNotes),
                (9, new[] { N(4, 3), N(4, -3) }),
                (13, Array.Empty<(int, bool, float)>()));
            var game = GameNotes.Load(bytes);
            var tweaked = Tweaked.Load(bytes, Array.Empty<int>());
            Same(game, tweaked, new[] { 7, 9, 11, 13, 99 });
            Assert.Equal(4, tweaked.Store.Records);
            // The game's own save of what it read reads back the same way.
            var again = Tweaked.Load(game.Save(), Array.Empty<int>());
            Same(game, again, new[] { 7, 9, 11, 13 });
        }

        [Fact]
        public void ASaveWithNoNotes()
        {
            var bytes = Bytes();
            var game = GameNotes.Load(bytes);
            var tweaked = Tweaked.Load(bytes, Array.Empty<int>());
            Same(game, tweaked, new[] { 1 });
            game.Add(1, 2, 3);
            tweaked.Add(1, 2, 3);
            Same(game, tweaked, new[] { 1 });
            tweaked.Night(new[] { 1 });
            Same(game, tweaked, new[] { 1 });
            Assert.Equal(1, tweaked.Store.Records);
            Assert.Empty(tweaked.Game.entries);
        }

        /// Today's entries are back in `entries` after the load, written to there; new entries
        /// take the places the game's dictionary gives them.
        [Fact]
        public void EntriesAddedAfterTheLoad()
        {
            var bytes = Bytes((1, new[] { N(1, 1) }), (2, new[] { N(2, 2), N(3, -1) }), (3, new[] { N(1, 5) }));
            var game = GameNotes.Load(bytes);
            var tweaked = Tweaked.Load(bytes, new[] { 2, 3 });
            Assert.Equal(new[] { 2, 3 }, tweaked.Game.entries.Keys.OrderBy(k => k));
            void Both(int id, float value, int note)
            {
                game.Add(id, value, note);
                tweaked.Add(id, value, note);
            }
            Both(2, 1, 2);    // a key there
            Both(2, 4, 9);    // a new key
            Both(2, -1, 2);   // the same note, the other sign
            Both(5, 1, 1);    // a new entry
            Both(4, -2, 1);   // another, after it in the game's dictionary
            Both(3, 0, 1);    // zero counts as negative
            Same(game, tweaked, new[] { 1, 2, 3, 4, 5 });
            Assert.Equal(tweaked.Game.entries[2].Keys.Select(k => (k.noteHash, k.isPositive)), game.entries[2].Keys.Select(k => (k.noteHash, k.isPositive)));
        }

        /// A write into a past day (only a mod would): the entry goes back to `entries` first, as a
        /// dictionary of the game's order, so the note lands where the game puts it.
        [Fact]
        public void AnAddIntoAPastDayMovesItBack()
        {
            var bytes = Bytes((1, new[] { N(1, 1), N(2, -1), N(3, 1) }), (2, new[] { N(2, 2) }));
            var game = GameNotes.Load(bytes);
            var tweaked = Tweaked.Load(bytes, new[] { 2 });
            Assert.False(tweaked.Game.entries.ContainsKey(1));
            game.Add(1, 2, 2);
            tweaked.Add(1, 2, 2);
            game.Add(1, -1, 2);
            tweaked.Add(1, -1, 2);
            Assert.True(tweaked.Game.entries.ContainsKey(1));
            Same(game, tweaked, new[] { 1, 2 });
            // The next night leaves it where it is; a later one finds it in `entries` still.
            tweaked.Night(new[] { 2 });
            Same(game, tweaked, new[] { 1, 2 });
            tweaked.Night(new[] { 1 });
            Same(game, tweaked, new[] { 1, 2 });
        }

        /// Nights move each day that ended into the store; saves and reads in between and after
        /// a reload stay the game's.
        [Fact]
        public void NightBoundaries()
        {
            var bytes = Bytes((1, new[] { N(1, 1) }), (2, new[] { N(1, 2) }), (3, new[] { N(2, -2) }));
            var game = GameNotes.Load(bytes);
            var tweaked = Tweaked.Load(bytes, new[] { 3 });
            var all = new List<int> { 1, 2, 3 };
            int next = 4;
            var today = new List<int> { 3 };
            for (int day = 0; day < 5; day++)
            {
                for (int k = 0; k < 6; k++)
                {
                    int id = k % 3 == 0 ? next++ : today[k % today.Count];
                    if (!all.Contains(id))
                    {
                        all.Add(id);
                        today.Add(id);
                    }
                    game.Add(id, (k % 2 == 0 ? 1 : -1) * (day + k + 0.5f), k % 4);
                    tweaked.Add(id, (k % 2 == 0 ? 1 : -1) * (day + k + 0.5f), k % 4);
                }
                Same(game, tweaked, all);
                tweaked.Night(today);
                Assert.Empty(tweaked.Game.entries);
                Same(game, tweaked, all);
                today = new List<int>();
            }
            var reloaded = Tweaked.Load(tweaked.Save(), today);
            Same(game, reloaded, all);
        }

        /// FastSave and RemoveDailyReports trim the daily reports at night (one drops old days from
        /// the list, the other clears their entries); the game keeps their notes and writes them
        /// out all the same, and so does the store.
        [Fact]
        public void TrimmedDailyReportsKeepTheirNotes()
        {
            var game = new GameNotes();
            var tweaked = new Tweaked();
            var days = new List<int[]>();
            int id = 0;
            for (int day = 0; day < 8; day++)
            {
                var ids = new[] { ++id, ++id };
                foreach (var i in ids)
                {
                    game.Add(i, day + 1, i % 3);
                    tweaked.Add(i, day + 1, i % 3);
                }
                days.Add(ids);
                // The night: the game's postfix runs on the day that ended, whatever the mods
                // did to the days before it.
                tweaked.Night(ids);
                if (days.Count > 4) days.RemoveAt(0);
                Same(game, tweaked, Enumerable.Range(1, id));
            }
            var reloaded = Tweaked.Load(tweaked.Save(), Array.Empty<int>());
            Same(game, reloaded, Enumerable.Range(1, id));
        }

        /// A colony started in this session: the store is made at the first night from the
        /// dictionary as the game built it.
        [Fact]
        public void AColonyStartedInTheSessionGetsItsStoreAtNight()
        {
            var game = new GameNotes();
            var tweaked = new Tweaked();
            foreach (var (i, v, n) in new[] { (3, 1f, 1), (1, -1f, 2), (2, 3f, 1), (1, 2f, 2) })
            {
                game.Add(i, v, n);
                tweaked.Add(i, v, n);
            }
            Assert.Null(tweaked.Store);
            Same(game, tweaked, new[] { 1, 2, 3 });
            tweaked.Night(new[] { 1, 3 });
            Assert.Equal(new[] { 2 }, tweaked.Game.entries.Keys);
            Same(game, tweaked, new[] { 1, 2, 3 });
            game.Add(4, 1, 1);
            tweaked.Add(4, 1, 1);
            Same(game, tweaked, new[] { 1, 2, 3, 4 });
        }

        /// An older save (version 5): the game reads it through Add; everything then moves in.
        [Fact]
        public void AnOlderSaveMovesIntoTheStore()
        {
            var read = new GameNotes();
            var game = new GameNotes();
            foreach (var (i, v, n) in new[] { (5, 1f, 1), (2, -1f, 2), (5, 3f, 1), (9, 2f, 2) })
            {
                read.Add(i, v, n);
                game.Add(i, v, n);
            }
            var tweaked = Tweaked.LoadRead(read, new[] { 9 });
            Assert.Equal(new[] { 9 }, tweaked.Game.entries.Keys);
            Same(game, tweaked, new[] { 2, 5, 9 });
        }

        /// An entry put into `entries` by other code than Add: written from there, in its record's
        /// place when the store knows the id, else after the others.
        [Fact]
        public void EntriesWrittenStraightIntoTheDictionary()
        {
            var bytes = Bytes((1, new[] { N(1, 1) }), (2, new[] { N(1, 2) }));
            var game = GameNotes.Load(bytes);
            var tweaked = Tweaked.Load(bytes, Array.Empty<int>());
            var replaced = new Dictionary<Key, float>(comparer) { [new Key { noteHash = 7, isPositive = true }] = 3 };
            game.entries[1] = replaced;
            tweaked.Game.entries[1] = new Dictionary<Key, float>(replaced, comparer);
            var added = new Dictionary<Key, float>(comparer) { [new Key { noteHash = 8 }] = -1 };
            game.entries[6] = added;
            tweaked.Game.entries[6] = new Dictionary<Key, float>(added, comparer);
            Same(game, tweaked, new[] { 1, 2, 6 });
        }

        /// Random days of adds, nights, saves and reloads, as a colony plays them.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void RandomColoniesMatchTheGame(int seed)
        {
            var rng = new Random(seed);
            var game = new GameNotes();
            var tweaked = new Tweaked();
            var all = new List<int>();
            var today = new List<int>();
            int next = 1;
            for (int step = 0; step < 3000; step++)
            {
                int op = rng.Next(100);
                if (op < 80)
                {
                    int id;
                    int pick = rng.Next(20);
                    // A mod's write into any day: that entry stays out of today's report.
                    if (pick == 0 && all.Count > 0) id = all[rng.Next(all.Count)];
                    else
                    {
                        id = pick < 6 || today.Count == 0 ? next++ : today[rng.Next(today.Count)];
                        if (!all.Contains(id)) all.Add(id);
                        if (!today.Contains(id)) today.Add(id);
                    }
                    float value = (float)Math.Round((rng.NextDouble() - 0.4) * 100, rng.Next(3));
                    if (rng.Next(30) == 0) value = 0;
                    int note = rng.Next(rng.Next(10) == 0 ? 200 : 6);
                    game.Add(id, value, note);
                    tweaked.Add(id, value, note);
                }
                else if (op < 92)
                {
                    tweaked.Night(today);
                    today = new List<int>();
                }
                else if (op < 97) Assert.Equal(game.Save(), tweaked.Save());
                else
                {
                    // Save and load: both sides read the bytes the game wrote.
                    var bytes = game.Save();
                    Assert.Equal(bytes, tweaked.Save());
                    game = GameNotes.Load(bytes);
                    tweaked = Tweaked.Load(bytes, today);
                }
                if (step % 250 == 0) Same(game, tweaked, all);
            }
            Same(game, tweaked, all.Append(next));
        }
    }
}
