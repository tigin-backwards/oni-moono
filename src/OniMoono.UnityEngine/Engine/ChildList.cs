using System;

namespace OniMoono.Engine
{
    /// A transform's children as they were when read (TransformNative.Children): later changes
    /// to the parent do not show in it, as with the copied array it replaces.
    internal readonly struct ChildList
    {
        readonly TransformNative[] items;
        readonly int count;

        public ChildList(TransformNative[] items, int count)
        {
            this.items = items;
            this.count = Math.Min(count, items.Length);
        }

        public int Length => count;

        public TransformNative this[int index] => (uint)index < (uint)count ? items[index] : throw new IndexOutOfRangeException();

        public int IndexOf(TransformNative child) => Array.IndexOf(items, child, 0, count);

        public TransformNative[] ToArray() => items.AsSpan(0, count).ToArray();

        public Enumerator GetEnumerator() => new(items, count);

        /// Slots for `count` children in an array made at run time: exact up to 4, then half as
        /// many again spare, so appending is amortized.
        public static int Capacity(int count) => count <= 4 ? count : count + (count >> 1);

        public struct Enumerator
        {
            readonly TransformNative[] items;
            readonly int count;
            int index;

            internal Enumerator(TransformNative[] items, int count)
            {
                this.items = items;
                this.count = count;
                index = -1;
            }

            public bool MoveNext() => ++index < count;

            public readonly TransformNative Current => items[index];
        }
    }
}
