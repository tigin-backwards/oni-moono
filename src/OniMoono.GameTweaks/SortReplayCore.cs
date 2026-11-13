using System;
using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// fast-conduit-sorts' replay without game types (unit-tested). List<T>.Sort(Comparison<T>) is
    /// an introsort whose moves depend only on the comparison's results, so for a comparison that
    /// reads one key per item (the conduit updaters' priority), the permutation it applies depends
    /// only on the keys in list order. One list's last keys and permutation are kept: the same keys
    /// again apply the same permutation without sorting. New keys sort indices with the same
    /// comparison (Array.Sort<int>(int[], Comparison<int>) runs the same generic introsort), which
    /// finds the permutation the list's own sort would apply.
    public sealed class SortReplay<T>
    {
        int[] keys = Array.Empty<int>();
        int[] perm = Array.Empty<int>();
        T[] items = Array.Empty<T>();
        int count = -1;

        /// Sorts made by replaying the kept permutation, and permutations worked out.
        public long Replayed, Built;

        /// Sorts `list` with `compare`, where `keys[i]` (for i < list.Count) is the key `compare`
        /// reads from list[i].
        public void Sort(List<T> list, int[] keyOf, Comparison<T> compare)
        {
            int n = list.Count;
            if (n < 2) { list.Sort(compare); return; }
            if (items.Length < n) items = new T[Math.Max(n, items.Length * 2)];
            list.CopyTo(items);
            bool same = n == count;
            for (int i = 0; same && i < n; i++)
                if (keyOf[i] != keys[i]) same = false;
            if (!same)
            {
                if (keys.Length < n) { keys = new int[Math.Max(n, keys.Length * 2)]; perm = new int[keys.Length]; }
                Array.Copy(keyOf, keys, n);
                var p = new int[n];
                for (int i = 0; i < n; i++) p[i] = i;
                var snapshot = items;
                Array.Sort(p, (a, b) => compare(snapshot[a], snapshot[b]));
                Array.Copy(p, perm, n);
                count = n;
                Built++;
            }
            else Replayed++;
            for (int i = 0; i < n; i++) list[i] = items[perm[i]];
            Array.Clear(items, 0, n);
        }

        /// Forgets the kept permutation (the next sort works one out).
        public void Reset() => count = -1;
    }
}
