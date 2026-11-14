using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// fast-overlay-removals' removal, without game types (unit-tested): UniformGrid<T>.Remove's
    /// walk over the cells [x0..x1] × [y0..y1] for an item that is alive. The game's walk, for
    /// each cell whose list holds the item (List.IndexOf), takes the first one out of the cell's
    /// list and the first one out of the list of all entries (List.Remove twice). For a live
    /// UnityEngine.Object, Equals is reference equality, so the same indices are found by
    /// comparing references, without reading the other objects.
    ///
    /// The list of all entries holds one entry per cell an item was added to, and the game
    /// searches it from its start once per cell. Taking the first entry out k times takes out
    /// the first k, so they are found in one pass: after an entry leaves index i, no entry
    /// before i is the item, and the search goes on from i. Entries next to each other (one
    /// add's, as a rule) leave together, which moves the rest of the list once.
    public static class GridRemovalCore
    {
        /// Returns the number of entries taken out of `items`.
        public static int Remove<T>(List<T>[] cells, List<T> items, int numXCells, int x0, int y0, int x1, int y1, T item) where T : class
        {
            int found = 0;
            for (int i = y0; i <= y1; i++)
                for (int j = x0; j <= x1; j++)
                {
                    var list = cells[i * numXCells + j];
                    if (list == null) continue;
                    int at = IndexOfSame(list, item, 0);
                    if (at == -1) continue;
                    list.RemoveAt(at);
                    found++;
                }
            int removed = 0, from = 0;
            while (removed < found)
            {
                int index = IndexOfSame(items, item, from);
                if (index < 0) break;
                int run = 1;
                while (removed + run < found && index + run < items.Count && ReferenceEquals(items[index + run], item)) run++;
                items.RemoveRange(index, run);
                removed += run;
                from = index;
            }
            return removed;
        }

        public static int IndexOfSame<T>(List<T> list, T item, int from) where T : class
        {
            for (int i = from, n = list.Count; i < n; i++)
                if (ReferenceEquals(list[i], item)) return i;
            return -1;
        }
    }
}
