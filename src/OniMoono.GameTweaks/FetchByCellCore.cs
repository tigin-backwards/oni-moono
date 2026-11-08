using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// What fetch-by-cell asks of a pile's items: whether this Duplicant may pick one up (the
    /// game's CouldBePickedUpByMinion) and whether one comes before another by the game's order
    /// within one cell (quality, then freshness, both descending). A struct, so the choice below
    /// is compiled for it and calls it directly.
    public interface IPileRules<T>
    {
        bool CanPick(T item);
        bool Better(T a, T b);
    }

    /// The choices fetch-by-cell makes within one pile (FetchByCell): plain .NET, with no game
    /// types (the tests compile it in).
    public static class PileChoice
    {
        /// The best of `members` by the rules' order, the first of equals; null for none.
        public static T Best<T, TRules>(List<T> members, TRules rules) where T : class where TRules : struct, IPileRules<T>
        {
            T best = null;
            foreach (var m in members)
                if (best == null || rules.Better(m, best)) best = m;
            return best;
        }

        /// The item a pile offers this Duplicant, as the game's pass over its items would take it:
        /// its kept best member when that one may be picked up; when it may not, the best of the
        /// members that may (the game checks each; `bestFailed` counts these); then any special
        /// item (one whose answer differs by Duplicant) that may be picked up and comes before it.
        /// Null when none may be picked up.
        public static T Choose<T, TRules>(T best, List<T> members, List<T> specials, TRules rules, ref long bestFailed, ref long specialChecks)
            where T : class where TRules : struct, IPileRules<T>
        {
            T chosen = null;
            if (best != null)
            {
                if (rules.CanPick(best)) chosen = best;
                else
                {
                    bestFailed++;
                    foreach (var m in members)
                        if (!ReferenceEquals(m, best) && (chosen == null || rules.Better(m, chosen)) && rules.CanPick(m)) chosen = m;
                }
            }
            foreach (var s in specials)
            {
                specialChecks++;
                if ((chosen == null || rules.Better(s, chosen)) && rules.CanPick(s)) chosen = s;
            }
            return chosen;
        }
    }

    /// Where the items of one cell (of one prefab) take their work spots from: each item's source
    /// is its offset table, its fixed offsets (by value) or, for one it can't tell, the item
    /// itself. While all share one, any of them gives the cell the path cost the game gives it
    /// (through the first item there in its list that may be picked up); otherwise that cost is
    /// found as the game finds it.
    public sealed class CellSources
    {
        object single;
        int count;
        Dictionary<object, int> mixed;

        public int Count => count;

        /// All the cell's items have the same source.
        public bool Uniform => mixed == null;

        public void Add(object source)
        {
            if (count == 0) single = source;
            else if (mixed == null && !Equals(source, single)) mixed = new Dictionary<object, int> { [single] = count };
            if (mixed != null) mixed[source] = mixed.TryGetValue(source, out int n) ? n + 1 : 1;
            count++;
        }

        public void Remove(object source)
        {
            count--;
            if (mixed == null) return;
            if (mixed.TryGetValue(source, out int n))
            {
                if (n <= 1) mixed.Remove(source);
                else mixed[source] = n - 1;
            }
            if (mixed.Count == 1)
            {
                foreach (var key in mixed.Keys) single = key;
                mixed = null;
            }
        }
    }
}
