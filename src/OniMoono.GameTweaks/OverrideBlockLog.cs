using System;

namespace OniMoono.GameTweaks
{
    /// fast-symbol-overrides' bookkeeping for one symbol-override block (SymbolOverrideInfoGpuData:
    /// 48 bytes an entry), with no game types (tests compile it in):
    /// - the entries written since the controller's own override application last ran, by
    ///   anyone but that application (FaceGraph's face, SpeechMonitor's mouth, a mod): the next
    ///   application redoes only its writes that cover these, as the rest still hold what it
    ///   wrote;
    /// - while an application runs (Begin to End), each written entry's bytes before its first
    ///   write in it, so that End can tell whether the application left the block as it found it.
    public sealed class OverrideBlockLog
    {
        public const int EntryBytes = 48;
        /// More writes than this between two applications: the next one is a full one.
        public const int MaxWritten = 64;
        /// More entries written than this in one application: End reports a change unchecked.
        public const int MaxSaved = 512;

        int[] written = new int[MaxWritten], taken = new int[MaxWritten];
        int writtenCount;
        bool writtenOverflow;

        // Entry e was saved in this application when savedIn[e] == application.
        int[] savedIn = new int[0];
        int application;
        int[] savedEntry = new int[16];
        byte[] saved = new byte[16 * EntryBytes];
        int savedCount;
        bool savedOverflow;

        /// An application is running.
        public bool Open { get; private set; }

        /// Entry `entry` is being written by someone other than the application.
        public void Written(int entry)
        {
            if (writtenCount < MaxWritten) written[writtenCount++] = entry;
            else writtenOverflow = true;
        }

        /// More entries were written than the log holds since the last Take.
        public bool WrittenOverflow => writtenOverflow;

        public int WrittenCount => writtenCount;

        /// The entries written since the last call (`entries[0..count)`, in order, repeats kept),
        /// valid until the next call; the log starts empty again.
        public int TakeWritten(out int[] entries)
        {
            entries = written;
            int n = writtenCount;
            written = taken;
            taken = entries;
            writtenCount = 0;
            writtenOverflow = false;
            return n;
        }

        public void ClearWritten()
        {
            writtenCount = 0;
            writtenOverflow = false;
        }

        public void Begin()
        {
            Open = true;
            savedCount = 0;
            savedOverflow = false;
            if (++application == int.MaxValue)
            {
                Array.Clear(savedIn, 0, savedIn.Length);
                application = 1;
            }
        }

        /// Entry `entry` of `block` is about to be written: its bytes are kept the first time.
        public void Writing(byte[] block, int entry)
        {
            if (!Open || savedOverflow) return;
            if (savedIn.Length * EntryBytes < block.Length) Array.Resize(ref savedIn, block.Length / EntryBytes);
            if (savedIn[entry] == application) return;
            if (savedCount == MaxSaved)
            {
                savedOverflow = true;
                return;
            }
            if (savedCount == savedEntry.Length)
            {
                Array.Resize(ref savedEntry, savedCount * 2);
                Array.Resize(ref saved, savedCount * 2 * EntryBytes);
            }
            savedIn[entry] = application;
            savedEntry[savedCount] = entry;
            Buffer.BlockCopy(block, entry * EntryBytes, saved, savedCount * EntryBytes, EntryBytes);
            savedCount++;
        }

        /// The write among `count` (write i: entries [starts[i], starts[i] + lengths[i])) that
        /// writes `entry`, the last if several do, or -1.
        public static int LastWriting(int[] starts, int[] lengths, int count, int entry)
        {
            for (int i = count - 1; i >= 0; i--)
                if (entry >= starts[i] && entry < starts[i] + lengths[i]) return i;
            return -1;
        }

        /// Whether two of the `count` writes write a same entry. `order` is scratch of at least
        /// `count` ints.
        public static bool Overlap(int[] starts, int[] lengths, int count, int[] order)
        {
            int n = 0;
            for (int i = 0; i < count; i++)
                if (lengths[i] > 0) order[n++] = i;
            // By start (the answer does not depend on the order of equal starts). A few writes,
            // the usual case, by insertion: Array.Sort with a comparer allocates a delegate.
            if (n <= 32)
                for (int k = 1; k < n; k++)
                {
                    int v = order[k], j = k - 1;
                    for (; j >= 0 && starts[order[j]] > starts[v]; j--) order[j + 1] = order[j];
                    order[j + 1] = v;
                }
            else Array.Sort(order, 0, n, Comparer.Of(starts));
            for (int k = 1; k < n; k++)
                if (starts[order[k]] < starts[order[k - 1]] + lengths[order[k - 1]]) return true;
            return false;
        }

        sealed class Comparer : System.Collections.Generic.IComparer<int>
        {
            [ThreadStatic] static Comparer shared;
            int[] starts;

            public static Comparer Of(int[] starts)
            {
                var c = shared ??= new Comparer();
                c.starts = starts;
                return c;
            }

            public int Compare(int a, int b) => starts[a].CompareTo(starts[b]);
        }

        /// Ends the application: true when some entry's bytes differ from what they were at
        /// Begin, or more entries were written than were kept.
        public bool End(byte[] block)
        {
            Open = false;
            if (savedOverflow) return true;
            for (int i = 0; i < savedCount; i++)
                if (!new ReadOnlySpan<byte>(block, savedEntry[i] * EntryBytes, EntryBytes)
                        .SequenceEqual(new ReadOnlySpan<byte>(saved, i * EntryBytes, EntryBytes)))
                    return true;
            return false;
        }
    }
}
