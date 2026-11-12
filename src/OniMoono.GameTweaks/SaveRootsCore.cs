using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// fast-save-roots' search, without game types (unit-tested): the index of `item` in `list`
    /// by reference, the first one. For an item that is alive (a UnityEngine.Object not
    /// destroyed), List.IndexOf's Equals is reference equality (Object.Equals: two live objects
    /// are equal only when they are the same), so the answer is List.IndexOf's without reading
    /// the other elements.
    public static class SaveRootsCore
    {
        public static int IndexOfSame<T>(List<T> list, T item) where T : class
        {
            for (int i = 0, n = list.Count; i < n; i++)
                if (ReferenceEquals(list[i], item)) return i;
            return -1;
        }
    }
}
