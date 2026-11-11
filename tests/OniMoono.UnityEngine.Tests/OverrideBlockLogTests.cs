using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// fast-symbol-overrides' block bookkeeping (src/OniMoono.GameTweaks/OverrideBlockLog.cs,
    /// compiled in here). The model: a symbol-override block of 48-byte entries, written by the
    /// game's two setters (skipped when every value already holds, as part 1 of the tweak does,
    /// else the version raised), an application that writes its overrides' ranges and then a face
    /// entry, and a batch that uploads the block when its version differs from the one it
    /// uploaded. The kept application (restore the logged entries, put the version back when the
    /// block is as it was) must leave the same block, and upload the same texture bytes, as the
    /// full one, frame after frame, with other writes between frames.
    public class OverrideBlockLogTests
    {
        const int Entries = 40;

        /// An entry's values for `seed` (seed -1: seed 0's values with -0 where seed 0 has 0,
        /// equal as floats, different as bytes).
        static float[] Values(int seed)
        {
            var v = new float[12];
            v[0] = seed == -1 ? -0f : seed * 0.5f;
            v[1] = 1;
            for (int k = 4; k < 12; k++) v[k] = Math.Max(seed, 0) + k;
            return v;
        }

        sealed class Block
        {
            public readonly byte[] Bytes = new byte[Entries * OverrideBlockLog.EntryBytes];
            public int Version = 1;
            public OverrideBlockLog Log;
            public bool Own;
            public byte[] Texture = new byte[Entries * OverrideBlockLog.EntryBytes];
            public int Uploaded = -1;
            public int Uploads;

            float Get(int entry, int k) => BitConverter.ToSingle(Bytes, entry * 48 + k * 4);

            bool Holds(int entry, float[] v)
            {
                // The setters write all but the two unused floats; part 1 compares as floats.
                for (int k = 0; k < 12; k++)
                    if (k != 2 && k != 3 && Get(entry, k) != v[k]) return false;
                return true;
            }

            void Store(int entry, float[] v)
            {
                for (int k = 0; k < 12; k++)
                    if (k != 2 && k != 3) BitConverter.GetBytes(v[k]).CopyTo(Bytes, entry * 48 + k * 4);
            }

            /// SetSymbolOverrideInfo(start, count, ...): entry start + i gets seeds[i].
            public void SetRange(int start, int[] seeds)
            {
                bool all = true;
                for (int i = 0; i < seeds.Length; i++) all &= Holds(start + i, Values(seeds[i]));
                if (all) return;
                if (Log != null)
                    for (int i = 0; i < seeds.Length; i++)
                    {
                        if (Log.Open) Log.Writing(Bytes, start + i);
                        if (!Own) Log.Written(start + i);
                    }
                for (int i = 0; i < seeds.Length; i++) Store(start + i, Values(seeds[i]));
                Version++;
            }

            public void SetOne(int entry, int seed) => SetRange(entry, new[] { seed });

            /// The batch's write check.
            public void Upload()
            {
                if (Uploaded == Version) return;
                Bytes.CopyTo(Texture, 0);
                Uploaded = Version;
                Uploads++;
            }
        }

        /// An override: entries [Start, Start + Seeds.Length) get Seeds.
        sealed record Write(int Start, int[] Seeds);

        /// The application as the game makes it.
        static void Full(Block block, List<Write> writes, int face, int faceSeed)
        {
            foreach (var w in writes) block.SetRange(w.Start, w.Seeds);
            if (faceSeed != int.MinValue) block.SetOne(face, faceSeed);
        }

        /// The application as the tweak makes it: in full when `full` or two writes overlap, else
        /// the writes covering a logged entry redone, in order.
        static void Kept(Block block, List<Write> writes, int face, int faceSeed, bool full)
        {
            var log = block.Log;
            int before = block.Version;
            var starts = writes.Select(w => w.Start).ToArray();
            var lengths = writes.Select(w => w.Seeds.Length).ToArray();
            full |= OverrideBlockLog.Overlap(starts, lengths, writes.Count, new int[writes.Count]);
            log.Begin();
            block.Own = true;
            if (full)
            {
                log.ClearWritten();
                foreach (var w in writes) block.SetRange(w.Start, w.Seeds);
            }
            else
            {
                int n = log.TakeWritten(out var entries);
                var redo = new SortedSet<int>();
                for (int e = 0; e < n; e++)
                {
                    int i = OverrideBlockLog.LastWriting(starts, lengths, writes.Count, entries[e]);
                    if (i >= 0) redo.Add(i);
                }
                foreach (int i in redo) block.SetRange(writes[i].Start, writes[i].Seeds);
            }
            block.Own = false;
            if (faceSeed != int.MinValue) block.SetOne(face, faceSeed);
            if (!log.End(block.Bytes)) block.Version = before;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void KeptApplicationsLeaveTheSameBlockAndTexture(int seed)
        {
            var random = new Random(seed);
            List<Write> NewWrites()
            {
                // Now and then overlapping ranges (made in full: the last write of an entry wins).
                bool overlap = random.Next(4) == 0;
                var list = new List<Write>();
                for (int start = 0; start < Entries - 6; start += random.Next(3, 8))
                    list.Add(new Write(start, Enumerable.Range(0, overlap ? random.Next(1, 6) : random.Next(1, 4)).Select(_ => random.Next(-1, 4)).ToArray()));
                return list;
            }
            var game = new Block();
            var tweak = new Block { Log = new OverrideBlockLog() };
            var writes = NewWrites();
            int face = writes[0].Start, faceSeed = 7;
            bool full = true;
            int keptCount = 0;
            for (int frame = 0; frame < 3000; frame++)
            {
                // Between frames: other writers, a new face, a blink (no face), new overrides.
                int r = random.Next(100);
                if (r < 10)
                {
                    int entry = random.Next(Entries), s = random.Next(-1, 9);
                    game.SetOne(entry, s);
                    tweak.SetOne(entry, s);
                }
                else if (r < 15) faceSeed = random.Next(-1, 9);
                else if (r < 18) faceSeed = int.MinValue;
                else if (r < 20)
                {
                    writes = NewWrites();
                    face = writes[random.Next(writes.Count)].Start;
                    full = true;
                }
                Full(game, writes, face, faceSeed);
                Kept(tweak, writes, face, faceSeed, full);
                if (!full) keptCount++;
                full = false;
                Assert.Equal(game.Bytes, tweak.Bytes);
                game.Upload();
                tweak.Upload();
                Assert.Equal(game.Texture, tweak.Texture);
            }
            Assert.True(keptCount > 1000, $"{keptCount} kept");
            // The face over an override, every frame: the game uploads nearly every frame.
            Assert.True(tweak.Uploads < game.Uploads / 2, $"{tweak.Uploads} uploads, the game's {game.Uploads}");
        }

        [Fact]
        public void TheFaceOverAnOverrideLeavesTheBlockUnchanged()
        {
            var block = new Block { Log = new OverrideBlockLog() };
            var writes = new List<Write> { new Write(0, new[] { 1, 2, 3 }), new Write(3, new[] { 4 }) };
            Kept(block, writes, 0, 5, full: true);
            block.Upload();
            int version = block.Version;
            for (int i = 0; i < 10; i++)
            {
                Kept(block, writes, 0, 5, full: false);
                Assert.Equal(version, block.Version);
            }
            // A new face is a change.
            Kept(block, writes, 0, 6, full: false);
            Assert.NotEqual(version, block.Version);
        }

        [Fact]
        public void BytesCountNotFloatEquality()
        {
            // Seed 0 then seed -1 over the same entry: equal as floats (part 1 skips the write),
            // so the entry keeps seed 0's bytes; seed -1 written over seed 1 does change them.
            var block = new Block { Log = new OverrideBlockLog() };
            block.Log.Begin();
            block.SetOne(0, 1);
            block.SetOne(0, -1);
            Assert.True(block.Log.End(block.Bytes));
            var start = (byte[])block.Bytes.Clone();
            block.Log.Begin();
            block.SetOne(0, 2);
            block.SetOne(0, -1);
            Assert.False(block.Log.End(block.Bytes));
            Assert.Equal(start, block.Bytes);
        }

        [Fact]
        public void TooManyWritesAreReported()
        {
            var log = new OverrideBlockLog();
            for (int i = 0; i < OverrideBlockLog.MaxWritten; i++) log.Written(i);
            Assert.False(log.WrittenOverflow);
            log.Written(0);
            Assert.True(log.WrittenOverflow);
            Assert.Equal(OverrideBlockLog.MaxWritten, log.TakeWritten(out var entries));
            Assert.Equal(Enumerable.Range(0, OverrideBlockLog.MaxWritten), entries.Take(OverrideBlockLog.MaxWritten));
            Assert.False(log.WrittenOverflow);
            Assert.Equal(0, log.WrittenCount);

            var bytes = new byte[(OverrideBlockLog.MaxSaved + 1) * OverrideBlockLog.EntryBytes];
            log.Begin();
            for (int i = 0; i <= OverrideBlockLog.MaxSaved; i++) log.Writing(bytes, i);
            // Unchecked: reported as changed though nothing changed.
            Assert.True(log.End(bytes));
        }

        /// Overlap sorts few writes by insertion and many with Array.Sort: either way it answers
        /// whether two writes share an entry, ties in start and empty writes included.
        [Fact]
        public void OverlapMatchesEveryPairCompared()
        {
            var random = new Random(7);
            foreach (int count in new[] { 0, 1, 2, 5, 31, 32, 33, 80 })
                for (int round = 0; round < 300; round++)
                {
                    var starts = new int[count];
                    var lengths = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        starts[i] = random.Next(count * 4 + 1);
                        lengths[i] = random.Next(4) == 0 ? 0 : random.Next(1, 3);
                    }
                    bool expected = false;
                    for (int a = 0; a < count; a++)
                        for (int b = a + 1; b < count; b++)
                            if (lengths[a] > 0 && lengths[b] > 0 && starts[a] < starts[b] + lengths[b] && starts[b] < starts[a] + lengths[a]) expected = true;
                    Assert.Equal(expected, OverrideBlockLog.Overlap(starts, lengths, count, new int[count]));
                }
        }

        /// Many writes sort without allocating, once the thread's keys are made.
        [Fact]
        public void OverlapOfManyWritesAllocatesNothingOnceWarm()
        {
            var starts = Enumerable.Range(0, 80).Select(i => (i * 37) % 80 * 2).ToArray();
            var lengths = Enumerable.Repeat(2, 80).ToArray();
            var order = new int[80];
            Assert.False(OverrideBlockLog.Overlap(starts, lengths, 80, order));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) Assert.False(OverrideBlockLog.Overlap(starts, lengths, 80, order));
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Fact]
        public void LastWritingFindsTheLastRange()
        {
            int[] starts = { 0, 4, 2, 9 }, lengths = { 4, 2, 3, 0 };
            Assert.True(OverrideBlockLog.Overlap(starts, lengths, 4, new int[4]));
            Assert.False(OverrideBlockLog.Overlap(new[] { 5, 0, 9, 3 }, new[] { 4, 3, 0, 2 }, 4, new int[4]));
            Assert.True(OverrideBlockLog.Overlap(new[] { 5, 0, 8 }, new[] { 4, 3, 1 }, 3, new int[3]));
            Assert.Equal(0, OverrideBlockLog.LastWriting(starts, lengths, 4, 1));
            Assert.Equal(2, OverrideBlockLog.LastWriting(starts, lengths, 4, 3));
            Assert.Equal(2, OverrideBlockLog.LastWriting(starts, lengths, 4, 4));
            Assert.Equal(1, OverrideBlockLog.LastWriting(starts, lengths, 4, 5));
            Assert.Equal(-1, OverrideBlockLog.LastWriting(starts, lengths, 4, 9));
            Assert.Equal(-1, OverrideBlockLog.LastWriting(starts, lengths, 2, 6));
        }
    }
}
