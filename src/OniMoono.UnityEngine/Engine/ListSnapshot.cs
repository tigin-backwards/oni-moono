using System;
using System.Collections.Generic;

namespace OniMoono.Engine
{
    /// A copy of a list for a per-frame pass whose callbacks may change the list (the player
    /// loop's passes over particle systems, cameras, canvases, pending Starts): the copy is
    /// made into one array kept for the pass, instead of a new array each frame. A pass started
    /// while the last one still holds the array (from one of its callbacks) gets an array of
    /// its own. Main thread only.
    internal sealed class ListSnapshot<T> where T : class
    {
        T[] array = Array.Empty<T>();
        bool taken;

        /// The list's items as they are now; Dispose gives the array back.
        public Lease Take(List<T> source)
        {
            int n = source.Count;
            if (taken) return new Lease(null, source.ToArray(), n);
            if (array.Length < n) array = new T[Math.Max(n, array.Length * 2)];
            source.CopyTo(array);
            taken = true;
            return new Lease(this, array, n);
        }

        public ref struct Lease
        {
            readonly ListSnapshot<T> owner;
            readonly T[] items;
            readonly int count;

            internal Lease(ListSnapshot<T> owner, T[] items, int count)
            {
                this.owner = owner;
                this.items = items;
                this.count = count;
            }

            public readonly ReadOnlySpan<T> Items => new(items, 0, count);

            /// Clears the copy (it holds no objects past the pass) and gives the array back.
            public readonly void Dispose()
            {
                if (owner == null) return;
                Array.Clear(items, 0, count);
                owner.taken = false;
            }
        }
    }
}
