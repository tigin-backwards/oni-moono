using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Unity.Collections
{
    /// A view over unmanaged memory. Arrays the engine hands out (Texture2D.GetRawTextureData,
    /// GetPixelData) alias engine-owned memory and are not freed by Dispose; arrays the engine
    /// allocates for the caller own their memory.
    public unsafe partial struct NativeArray<T>
    {
        internal void* m_Buffer;
        internal int m_Length;
        internal int m_AllocatorLabel; // 0 = not owned (Unity's Allocator.None), 1 = owned

        internal NativeArray(void* buffer, int length, bool owned)
        {
            m_Buffer = buffer;
            m_Length = length;
            m_AllocatorLabel = owned ? 1 : 0;
        }

        /// Allocates zeroed memory owned by the array.
        internal static NativeArray<T> Allocate(int length)
        {
            nuint bytes = (nuint)length * (nuint)Unsafe.SizeOf<T>();
            void* p = NativeMemory.AlignedAlloc(bytes == 0 ? 1 : bytes, 16);
            NativeMemory.Clear(p, bytes);
            return new NativeArray<T>(p, length, owned: true);
        }

        /// Memory of the Temp, TempJob or Persistent allocator (all the same here), zeroed
        /// unless UninitializedMemory. Any other allocator throws as the original's does
        /// (ArgumentNullException for "destination", NullReferenceException uninitialized:
        /// UnityProbe's apigaps "misc"); a negative length, which ends the original's player,
        /// throws ArgumentOutOfRangeException.
        public NativeArray(int length, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "Length must be >= 0");
            if (allocator is not (Allocator.Temp or Allocator.TempJob or Allocator.Persistent))
            {
                if ((options & NativeArrayOptions.ClearMemory) != 0) throw new ArgumentNullException("destination");
                throw new NullReferenceException();
            }
            nuint bytes = (nuint)length * (nuint)Unsafe.SizeOf<T>();
            m_Buffer = NativeMemory.AlignedAlloc(bytes == 0 ? 1 : bytes, 16);
            if ((options & NativeArrayOptions.ClearMemory) != 0) NativeMemory.Clear(m_Buffer, bytes);
            m_Length = length;
            m_AllocatorLabel = 1;
        }

        /// A copy of the array.
        public NativeArray(T[] array, Allocator allocator) : this(array?.Length ?? throw new ArgumentNullException(nameof(array)), allocator, NativeArrayOptions.UninitializedMemory)
        {
            for (int i = 0; i < array.Length; i++) Unsafe.Write((byte*)m_Buffer + (long)i * Unsafe.SizeOf<T>(), array[i]);
        }

        public int Length => m_Length;
        public bool IsCreated => m_Buffer != null;

        /// Unity's release player doesn't check the index and reads or writes the memory past
        /// the array; here an out-of-range write is dropped and an out-of-range read gives
        /// default(T), logged once a process (NativeArrayRange). An array that isn't created
        /// (or is disposed) still throws.
        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)m_Length) return OutOfRangeRead(index);
                return Unsafe.Read<T>((byte*)m_Buffer + (long)index * Unsafe.SizeOf<T>());
            }
            set
            {
                if ((uint)index >= (uint)m_Length) { OutOfRangeWrite(index); return; }
                Unsafe.Write((byte*)m_Buffer + (long)index * Unsafe.SizeOf<T>(), value);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        T OutOfRangeRead(int index)
        {
            CheckCreated(index);
            NativeArrayRange.Report($"read of index {index} in a NativeArray<{typeof(T).Name}> of length {m_Length} gives default");
            return default;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void OutOfRangeWrite(int index)
        {
            CheckCreated(index);
            NativeArrayRange.Report($"write to index {index} in a NativeArray<{typeof(T).Name}> of length {m_Length} is dropped");
        }

        void CheckCreated(int index)
        {
            if (m_Buffer == null)
                throw new IndexOutOfRangeException($"Index {index} is out of range of '{m_Length}' Length.");
        }

        public void Dispose()
        {
            if (m_Buffer == null) throw new ObjectDisposedException("The NativeArray is already disposed.");
            if (m_AllocatorLabel == 1) NativeMemory.AlignedFree(m_Buffer);
            m_Buffer = null;
            m_Length = 0;
        }

        public T[] ToArray()
        {
            // Every element is copied over: the array needs no clearing first. A large array (a
            // read-back of a large snapshot, copied on the main thread) is copied by several
            // threads, which also share the first touch of its pages.
            var result = GC.AllocateUninitializedArray<T>(m_Length);
            long bytes = (long)m_Length * Unsafe.SizeOf<T>();
            if (bytes < OniMoono.Engine.ParallelCopy.MinBytes || RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                Copy(this, 0, result, 0, m_Length);
                return result;
            }
            var handle = GCHandle.Alloc(result, GCHandleType.Pinned);
            try
            {
                OniMoono.Engine.ParallelCopy.Copy((byte*)m_Buffer, (byte*)handle.AddrOfPinnedObject(), bytes);
            }
            finally
            {
                handle.Free();
            }
            return result;
        }

        // Unity's release player checks none of the copies' or GetSubArray's ranges (measured:
        // tools/UnityProbe/NativeRanges.cs, ARCHITECTURE D46): it copies the source's length (the
        // two-array Copy, CopyFrom, CopyTo) or the length given, before, past or over either
        // array, and a negative length copies garbage or crashes. Here only the part within both
        // arrays is copied (or viewed), logged once a process as the indexer is.

        public void CopyFrom(T[] array)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            Copy(array, 0, this, 0, array.Length);
        }

        public void CopyTo(T[] array)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            Copy(this, 0, array, 0, m_Length);
        }

        public NativeArray<T> GetSubArray(int start, int length)
        {
            // Unity's player gives a view before the array for a negative start, and one of a
            // negative Length for a negative length. No view here can keep the caller's indices
            // for those, so they give an empty one (its reads give default, its writes drop).
            if (start < 0 || length < 0)
            {
                NativeArrayRange.Report($"GetSubArray({start}, {length}) of a NativeArray<{typeof(T).Name}> of length {m_Length} is empty");
                return new NativeArray<T>(m_Buffer, 0, owned: false);
            }
            if (start > m_Length - length)
            {
                int clipped = Math.Max(0, m_Length - start);
                NativeArrayRange.Report($"GetSubArray({start}, {length}) of a NativeArray<{typeof(T).Name}> of length {m_Length} is cut to length {clipped}");
                start = Math.Min(start, m_Length);
                length = clipped;
            }
            return new NativeArray<T>((byte*)m_Buffer + (long)start * Unsafe.SizeOf<T>(), length, owned: false);
        }

        /// Narrows a copy of `length` elements to the part that lies within both arrays; false
        /// when nothing is left to copy.
        static bool ClipRange(ref int srcIndex, int srcTotal, ref int dstIndex, int dstTotal, ref int length)
        {
            if (srcIndex >= 0 && dstIndex >= 0 && length >= 0 && srcIndex <= srcTotal - length && dstIndex <= dstTotal - length)
                return length > 0;
            long skip = Math.Max(0L, -(long)Math.Min(srcIndex, dstIndex));
            long end = Math.Min(length, Math.Min((long)srcTotal - srcIndex, (long)dstTotal - dstIndex));
            int count = (int)Math.Max(0L, end - skip);
            NativeArrayRange.Report($"copy of {length} elements from index {srcIndex} of {srcTotal} to index {dstIndex} of {dstTotal} (NativeArray<{typeof(T).Name}>) copies only the {count} in range");
            if (count == 0) return false;
            srcIndex += (int)skip;
            dstIndex += (int)skip;
            length = count;
            return true;
        }

        public static void Copy(T[] src, int srcIndex, NativeArray<T> dst, int dstIndex, int length)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (!ClipRange(ref srcIndex, src.Length, ref dstIndex, dst.m_Length, ref length)) return;
            new ReadOnlySpan<T>(src, srcIndex, length).CopyTo(new Span<T>((byte*)dst.m_Buffer + (long)dstIndex * Unsafe.SizeOf<T>(), length));
        }

        public static void Copy(NativeArray<T> src, int srcIndex, T[] dst, int dstIndex, int length)
        {
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            if (!ClipRange(ref srcIndex, src.m_Length, ref dstIndex, dst.Length, ref length)) return;
            new ReadOnlySpan<T>((byte*)src.m_Buffer + (long)srcIndex * Unsafe.SizeOf<T>(), length).CopyTo(new Span<T>(dst, dstIndex, length));
        }

        public static void Copy(NativeArray<T> src, int srcIndex, NativeArray<T> dst, int dstIndex, int length)
        {
            if (!ClipRange(ref srcIndex, src.m_Length, ref dstIndex, dst.m_Length, ref length)) return;
            Buffer.MemoryCopy((byte*)src.m_Buffer + (long)srcIndex * Unsafe.SizeOf<T>(), (byte*)dst.m_Buffer + (long)dstIndex * Unsafe.SizeOf<T>(),
                (long)length * Unsafe.SizeOf<T>(), (long)length * Unsafe.SizeOf<T>());
        }

        public static void Copy(T[] src, NativeArray<T> dst) => Copy(src, 0, dst, 0, src.Length);
        public static void Copy(NativeArray<T> src, T[] dst) => Copy(src, 0, dst, 0, src.m_Length);
        public static void Copy(NativeArray<T> src, NativeArray<T> dst) => Copy(src, 0, dst, 0, src.m_Length);

        public Enumerator GetEnumerator() => new Enumerator(ref this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(ref this);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<T>
        {
            private NativeArray<T> m_Array;
            private int m_Index;

            public Enumerator(ref NativeArray<T> array)
            {
                m_Array = array;
                m_Index = -1;
            }

            public void Dispose() { }
            public bool MoveNext() => ++m_Index < m_Array.m_Length;
            public void Reset() => m_Index = -1;
            public T Current => m_Array[m_Index];
            object IEnumerator.Current => Current;
        }

        public bool Equals(NativeArray<T> other) => m_Buffer == other.m_Buffer && m_Length == other.m_Length;
        public override bool Equals(object obj) => obj is NativeArray<T> other && Equals(other);
        public override int GetHashCode() => ((int)(nint)m_Buffer * 397) ^ m_Length;
        public static bool operator ==(NativeArray<T> left, NativeArray<T> right) => left.Equals(right);
        public static bool operator !=(NativeArray<T> left, NativeArray<T> right) => !left.Equals(right);
    }
}

namespace Unity.Collections
{
    /// The first out-of-range NativeArray access in the process, logged once: Unity's release
    /// player doesn't check it, so the original carries on past the array's memory; ours drops
    /// it instead of throwing.
    internal static class NativeArrayRange
    {
        static int logged;

        internal static void Report(string what)
        {
            if (System.Threading.Interlocked.Exchange(ref logged, 1) != 0) return;
            OniMoono.Engine.EngineLog.Engine($"NativeArray: {what} (Unity's release player doesn't check the range and reaches past the array; later ones aren't logged)\n{OniMoono.Engine.EngineLog.CaptureStackTrace()}");
        }

        /// For tests: the next one is logged again.
        internal static void ResetForTests() => logged = 0;
    }
}

namespace Unity.Collections.LowLevel.Unsafe
{
    public static unsafe partial class NativeArrayUnsafeUtility
    {
        public static void* GetUnsafePtr<T>(NativeArray<T> nativeArray) where T : struct => nativeArray.m_Buffer;
        public static void* GetUnsafeReadOnlyPtr<T>(NativeArray<T> nativeArray) where T : struct => nativeArray.m_Buffer;
        public static void* GetUnsafeBufferPointerWithoutChecks<T>(NativeArray<T> nativeArray) where T : struct => nativeArray.m_Buffer;
    }
}


namespace UnityEngine.Pool
{
    /// A stack-based pool: Get hands out the most recently released object or creates one;
    /// Release keeps objects up to maxSize and destroys the rest.
    public partial class ObjectPool<T>
    {
        readonly Stack<T> idle;
        readonly Func<T> create;
        readonly Action<T> onGet;
        readonly Action<T> onRelease;
        readonly Action<T> onDestroy;
        readonly bool checkDoubleRelease;
        readonly int capacityLimit;

        public ObjectPool(Func<T> createFunc, Action<T> actionOnGet = null, Action<T> actionOnRelease = null, Action<T> actionOnDestroy = null,
            bool collectionCheck = true, int defaultCapacity = 10, int maxSize = 10000)
        {
            if (createFunc == null) throw new ArgumentNullException(nameof(createFunc));
            if (maxSize <= 0) throw new ArgumentException("Max Size must be greater than 0", nameof(maxSize));
            idle = new Stack<T>(Math.Max(0, defaultCapacity));
            create = createFunc;
            onGet = actionOnGet;
            onRelease = actionOnRelease;
            onDestroy = actionOnDestroy;
            checkDoubleRelease = collectionCheck;
            capacityLimit = maxSize;
        }

        /// Every object this pool created and has not destroyed, active or inactive.
        public int CountAll { get; private set; }

        public int CountActive => CountAll - CountInactive;

        public int CountInactive => idle.Count;

        public T Get()
        {
            T item;
            if (idle.Count > 0)
                item = idle.Pop();
            else
            {
                item = create();
                CountAll++;
            }
            onGet?.Invoke(item);
            return item;
        }

        /// The returned handle releases v back to this pool when disposed.
        public PooledObject<T> Get(out T v) => new PooledObject<T>(v = Get(), this);

        public void Release(T element)
        {
            if (checkDoubleRelease && IsIdle(element))
                throw new InvalidOperationException("Trying to release an object that has already been released to the pool.");
            onRelease?.Invoke(element);
            if (idle.Count < capacityLimit)
                idle.Push(element);
            else
            {
                onDestroy?.Invoke(element);
                CountAll--;
            }
        }

        public void Clear()
        {
            if (onDestroy != null)
            {
                foreach (var item in idle)
                    onDestroy(item);
            }
            idle.Clear();
            CountAll = 0;
        }

        public void Dispose() => Clear();

        bool IsIdle(T element)
        {
            foreach (var item in idle)
            {
                if (ReferenceEquals(item, element))
                    return true;
            }
            return false;
        }
    }

    /// Returned by the pools' Get(out value): disposing it releases the value to its pool.
    public partial struct PooledObject<T>
    {
        readonly T value;
        readonly ObjectPool<T> owner;

        internal PooledObject(T value, ObjectPool<T> pool)
        {
            this.value = value;
            owner = pool;
        }

        void IDisposable.Dispose() => owner.Release(value);
    }

    /// One shared pool per collection type; collections are cleared when released.
    public partial class CollectionPool<TCollection, TItem>
    {
        static readonly ObjectPool<TCollection> shared = new ObjectPool<TCollection>(() => new TCollection(), null, c => c.Clear());

        public static TCollection Get() => shared.Get();

        public static PooledObject<TCollection> Get(out TCollection value) => shared.Get(out value);

        public static void Release(TCollection toRelease) => shared.Release(toRelease);
    }
}

namespace OniMoono.Engine
{
    /// A large copy split between pool threads (half the cores, at most 4), 4 MB a piece.
    internal static unsafe class ParallelCopy
    {
        public const long MinBytes = 32L << 20;
        const long Piece = 4L << 20;

        public static void Copy(byte* from, byte* to, long bytes)
        {
            long pieces = (bytes + Piece - 1) / Piece;
            var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
            System.Threading.Tasks.Parallel.For(0L, pieces, options, i =>
            {
                long at = i * Piece, n = Math.Min(Piece, bytes - at);
                Buffer.MemoryCopy(from + at, to + at, n, n);
            });
        }
    }
}
