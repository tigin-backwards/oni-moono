using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace OniMoono.Engine
{
    /// The engine's "native" object table. A UnityEngine.Object stores only a handle into it
    /// (in m_CachedPtr), like Unity's managed wrappers store a pointer to the native object.
    ///
    /// A handle packs (slot index, generation). Lookups are lock-free and safe from any thread:
    /// a destroyed object's slot is cleared or reused with a new generation, so a stale handle
    /// resolves to null instead of to the wrong object. Mutation happens on the main thread
    /// (object creation may also happen on the loading thread under the table lock).
    ///
    /// A slot holds its object strongly, except while Resources.UnloadUnusedAssets probes
    /// reachability: then the probed objects' slots hold weak references (see Weaken). The table
    /// holds the objects themselves (UnityEngine.Object), which keep their slot, generation and
    /// instance id; an object's record is its m_Native.
    internal static class ObjectTable
    {
        static readonly object gate = new();
        static object[] slots = new object[4096];
        static int[] freeSlots = new int[256];
        static int freeCount;
        static int highWater = 1; // slot 0 is never used, so a zero handle means "none"
        static int nextGeneration;
        static int nextRuntimeId;   // runtime-created objects: negative ids, like Unity
        static int nextPersistentId; // objects loaded from files: positive ids
        /// Instance id → slot index.
        static readonly Dictionary<int, int> byInstanceId = new();

        /// A slot holds null, a UnityEngine.Object or a WeakReference<UnityEngine.Object>, nothing
        /// else. Every handle lookup comes through here, so it tests the sealed weak-reference type
        /// (one method-table compare) instead of casting to Object, whose derived types make `as`
        /// walk the class hierarchy.
        static UnityEngine.Object At(object entry)
            => entry is WeakReference<UnityEngine.Object> w ? (w.TryGetTarget(out var o) ? o : null) : Unsafe.As<UnityEngine.Object>(entry);

        public static IntPtr Register(UnityEngine.Object obj)
        {
            lock (gate)
            {
                int index;
                if (freeCount > 0) index = freeSlots[--freeCount];
                else
                {
                    index = highWater++;
                    if (index >= slots.Length)
                    {
                        var grown = new object[slots.Length * 2];
                        Array.Copy(slots, grown, slots.Length);
                        Volatile.Write(ref slots, grown);
                    }
                }
                obj.EngineGeneration = ++nextGeneration;
                obj.EngineSlot = index;
                Volatile.Write(ref slots[index], obj);
                byInstanceId[obj.EngineInstanceId] = index;
                return Pack(index, obj.EngineGeneration);
            }
        }

        public static void Unregister(UnityEngine.Object obj)
        {
            lock (gate)
            {
                if (obj.EngineSlot <= 0 || !ReferenceEquals(At(slots[obj.EngineSlot]), obj)) return;
                Free(obj.EngineSlot, obj.EngineInstanceId);
                obj.EngineSlot = 0;
            }
        }

        /// Gives a registered object a different instance id (objects loaded from files).
        public static void SetInstanceId(UnityEngine.Object obj, int instanceId)
        {
            lock (gate)
            {
                if (byInstanceId.TryGetValue(obj.EngineInstanceId, out var i) && i == obj.EngineSlot) byInstanceId.Remove(obj.EngineInstanceId);
                obj.EngineInstanceId = instanceId;
                if (obj.EngineSlot > 0) byInstanceId[instanceId] = obj.EngineSlot;
            }
        }

        static void Free(int index, int instanceId)
        {
            Volatile.Write(ref slots[index], null);
            if (byInstanceId.TryGetValue(instanceId, out var i) && i == index) byInstanceId.Remove(instanceId);
            if (freeCount == freeSlots.Length) Array.Resize(ref freeSlots, freeSlots.Length * 2);
            freeSlots[freeCount++] = index;
        }

        /// An object whose slot is weak during a probe.
        public readonly record struct Probe(int Slot, int InstanceId);

        /// Makes the given objects' slots weak references; the caller then drops its own
        /// references, collects, and calls Strengthen. Resolution keeps working meanwhile.
        public static List<Probe> Weaken(List<UnityEngine.Object> objects)
        {
            var probed = new List<Probe>(objects.Count);
            lock (gate)
                foreach (var o in objects)
                {
                    if (o.EngineSlot <= 0 || !ReferenceEquals(slots[o.EngineSlot], o)) continue;
                    Volatile.Write(ref slots[o.EngineSlot], new WeakReference<UnityEngine.Object>(o));
                    probed.Add(new Probe(o.EngineSlot, o.EngineInstanceId));
                }
            return probed;
        }

        /// Ends a probe: surviving objects' slots become strong again, the slots of collected
        /// objects are freed. Returns the number freed.
        public static int Strengthen(List<Probe> probed)
        {
            int freed = 0;
            lock (gate)
                foreach (var (slot, instanceId) in probed)
                {
                    if (slots[slot] is not WeakReference<UnityEngine.Object> weak) continue;
                    if (weak.TryGetTarget(out var o)) Volatile.Write(ref slots[slot], o);
                    else
                    {
                        Free(slot, instanceId);
                        freed++;
                    }
                }
            return freed;
        }

        public static UnityEngine.Object Resolve(IntPtr handle)
        {
            long h = (long)handle;
            if (h == 0) return null;
            int index = (int)(h & 0xFFFFFFFF);
            int generation = (int)(h >> 32);
            var table = Volatile.Read(ref slots);
            if ((uint)index >= (uint)table.Length) return null;
            var obj = At(Volatile.Read(ref table[index]));
            return obj is not null && obj.EngineGeneration == generation ? obj : null;
        }

        /// The live object with this instance id, if any.
        public static UnityEngine.Object FindByInstanceId(int instanceId)
        {
            lock (gate) return byInstanceId.TryGetValue(instanceId, out var index) ? At(slots[index]) : null;
        }

        static IntPtr Pack(int index, int generation) => (IntPtr)(((long)generation << 32) | (uint)index);

        public static int NewInstanceId(bool persistent)
            => persistent ? Interlocked.Increment(ref nextPersistentId) : Interlocked.Decrement(ref nextRuntimeId);

        /// Snapshot of all live objects (for FindObjectsOfTypeAll and friends).
        public static UnityEngine.Object[] Objects()
        {
            lock (gate)
            {
                var result = new List<UnityEngine.Object>(highWater);
                for (int i = 1; i < highWater; i++)
                {
                    var o = At(slots[i]);
                    if (o is not null) result.Add(o);
                }
                return result.ToArray();
            }
        }

        /// The records of all live objects that have one, in slot order.
        public static NativeObject[] Snapshot()
        {
            lock (gate)
            {
                var result = new List<NativeObject>(highWater);
                for (int i = 1; i < highWater; i++)
                    if (At(slots[i])?.NativeRecord is { } n) result.Add(n);
                return result.ToArray();
            }
        }
    }
}
