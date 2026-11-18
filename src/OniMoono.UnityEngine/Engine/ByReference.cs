using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace OniMoono.Engine
{
    /// The searches of List and Array by reference, for engine records and facades
    /// (IdentityAnalyzer, MOONO005): a facade's Equals is Unity's, under which every destroyed
    /// object equals every other. Each finds the same item as its List or Array counterpart would
    /// with reference equality.
    internal static class ByReference
    {
        public static int IndexOf<T>(List<T> list, T item) where T : class
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return i;
            return -1;
        }

        public static int IndexOf<T>(T[] array, T item) where T : class
        {
            for (int i = 0; i < array.Length; i++)
                if (ReferenceEquals(array[i], item)) return i;
            return -1;
        }

        public static bool Contains<T>(List<T> list, T item) where T : class => IndexOf(list, item) >= 0;

        /// Removes the first occurrence, as List.Remove does.
        public static bool Remove<T>(List<T> list, T item) where T : class
        {
            int i = IndexOf(list, item);
            if (i < 0) return false;
            list.RemoveAt(i);
            return true;
        }

        /// Pairs whose first item compares by reference and the second by its default comparer,
        /// with the hash ValueTuple gives them (an object's hash is its reference's).
        public sealed class FirstOfPair<TA, TB> : IEqualityComparer<(TA, TB)> where TA : class
        {
            public static readonly FirstOfPair<TA, TB> Comparer = new();

            public bool Equals((TA, TB) x, (TA, TB) y) => ReferenceEquals(x.Item1, y.Item1) && EqualityComparer<TB>.Default.Equals(x.Item2, y.Item2);

            public int GetHashCode((TA, TB) pair)
                => System.HashCode.Combine(pair.Item1 is null ? 0 : RuntimeHelpers.GetHashCode(pair.Item1), pair.Item2?.GetHashCode() ?? 0);
        }
    }
}
