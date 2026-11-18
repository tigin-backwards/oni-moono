using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine
{
    /// A scene's root GameObjects in hierarchy order. ONI keeps most of its objects at the root
    /// (every building, item and critter), and objects leave the root constantly (destroyed, or
    /// stored: parented to a storage), so removal must not scan the list: each root remembers its
    /// slot (GameObject.RootSlot), a removal empties the slot, and the slots are compacted
    /// into a new array once empty ones outnumber the roots. Positional operations (sibling
    /// index), which the game rarely uses on roots, are linear. Main thread only.
    internal sealed class SceneRoots
    {
        GameObject[] slots = new GameObject[16];
        int used; // slots in use, live or emptied
        int count;

        public int Count => count;

        public void Add(GameObject go)
        {
            if (used == slots.Length) Compact(Math.Max(16, count * 2));
            go.RootSlot = used;
            slots[used++] = go;
            count++;
        }

        public void Remove(GameObject go)
        {
            int slot = go.RootSlot;
            if (slot < 0 || slot >= used || !ReferenceEquals(slots[slot], go)) return;
            slots[slot] = null;
            go.RootSlot = -1;
            count--;
            if (used - count > Math.Max(64, count)) Compact(Math.Max(16, count * 2));
        }

        /// Position among the roots, or -1.
        public int IndexOf(GameObject go)
        {
            int slot = go.RootSlot;
            if (slot < 0 || slot >= used || !ReferenceEquals(slots[slot], go)) return -1;
            int index = 0;
            for (int i = 0; i < slot; i++)
                if (slots[i] is not null) index++;
            return index;
        }

        /// Inserts at a position among the roots (clamped).
        public void Insert(int index, GameObject go)
        {
            Compact(Math.Max(16, (count + 1) * 2));
            index = Math.Clamp(index, 0, count);
            Array.Copy(slots, index, slots, index + 1, count - index);
            slots[index] = go;
            used = ++count;
            for (int i = index; i < count; i++) slots[i].RootSlot = i;
        }

        public GameObject[] ToArray()
        {
            var result = new GameObject[count];
            int n = 0;
            for (int i = 0; i < used; i++)
                if (slots[i] is not null) result[n++] = slots[i];
            return result;
        }

        /// Compaction writes a new array, so an enumeration in progress keeps its own; roots
        /// removed meanwhile are skipped, roots added meanwhile are not visited.
        public Enumerator GetEnumerator() => new(slots, used);

        void Compact(int capacity)
        {
            var next = new GameObject[Math.Max(capacity, count)];
            int n = 0;
            for (int i = 0; i < used; i++)
            {
                var go = slots[i];
                if (go is null) continue;
                go.RootSlot = n;
                next[n++] = go;
            }
            slots = next;
            used = n;
        }

        public struct Enumerator
        {
            readonly GameObject[] slots;
            readonly int end;
            int i;

            internal Enumerator(GameObject[] slots, int end)
            {
                this.slots = slots;
                this.end = end;
                i = -1;
                Current = null;
            }

            public GameObject Current { get; private set; }

            public bool MoveNext()
            {
                while (++i < end)
                {
                    var go = slots[i];
                    if (go is not null && go.RootSlot >= 0)
                    {
                        Current = go;
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
