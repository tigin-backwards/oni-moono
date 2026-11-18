using System;

using Transform = UnityEngine.Transform;

namespace OniMoono.Engine
{
    /// A transform's children as they were when read (Transform.Children): later changes
    /// to the parent do not show in it, as with the copied array it replaces.
    internal readonly struct ChildList
    {
        readonly Transform[] items;
        readonly int count;

        public ChildList(Transform[] items, int count)
        {
            this.items = items;
            this.count = Math.Min(count, items.Length);
        }

        public int Length => count;

        public Transform this[int index] => (uint)index < (uint)count ? items[index] : throw new IndexOutOfRangeException();

        /// By reference (a Transform is equal only to itself): Array.IndexOf would call
        /// Equals on every child before it.
        public int IndexOf(Transform child)
        {
            var list = items;
            for (int i = 0; i < count && i < list.Length; i++)
                if (ReferenceEquals(list[i], child)) return i;
            return -1;
        }

        public Transform[] ToArray() => items.AsSpan(0, count).ToArray();

        public Enumerator GetEnumerator() => new(items, count);

        /// Slots for `count` children in an array made at run time: exact up to 4, then half as
        /// many again spare, so appending is amortized.
        public static int Capacity(int count) => count <= 4 ? count : count + (count >> 1);

        public struct Enumerator
        {
            readonly Transform[] items;
            readonly int count;
            int index;

            internal Enumerator(Transform[] items, int count)
            {
                this.items = items;
                this.count = count;
                index = -1;
            }

            public bool MoveNext() => ++index < count;

            public readonly Transform Current => items[index];
        }
    }
}
