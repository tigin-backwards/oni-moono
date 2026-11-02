using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Numerics;
using Brutal.VulkanApi;

namespace OniMoono.Desktop.Vulkan
{
    /// Values found by an owner and a run of identities (device thread only). A lookup hashes
    /// and compares the run in place, so finding a known one allocates nothing; entries a sweep
    /// finds unused for a while are released.
    internal sealed class IdentityTable<TValue>
    {
        sealed class Entry
        {
            public object Owner;
            public long[] Ids;
            public int Hash;
            public TValue Value;
            public long LastUsed;
            public bool Released;
            public Entry Next;
        }

        readonly Dictionary<int, Entry> buckets = new();
        public int Count { get; private set; }

        static int HashOf(object owner, ReadOnlySpan<long> ids)
        {
            // Four independent lanes of multiply-xor over the words (HashCode.AddBytes cost
            // several times as much for the 78 words of a batched animation draw).
            const ulong K = 0x9E3779B97F4A7C15;
            ulong a = (ulong)RuntimeHelpers.GetHashCode(owner) + K, b = (ulong)ids.Length, c = 0, d = 0;
            int i = 0;
            for (; i + 4 <= ids.Length; i += 4)
            {
                a = (a ^ (ulong)ids[i]) * K;
                b = (b ^ (ulong)ids[i + 1]) * K;
                c = (c ^ (ulong)ids[i + 2]) * K;
                d = (d ^ (ulong)ids[i + 3]) * K;
            }
            for (; i < ids.Length; i++) a = (a ^ (ulong)ids[i]) * K;
            ulong h = a ^ BitOperations.RotateLeft(b, 17) ^ BitOperations.RotateLeft(c, 31) ^ BitOperations.RotateLeft(d, 47);
            h ^= h >> 29;
            return (int)h ^ (int)(h >> 32);
        }

        /// The value for the owner and identities, marked used at `now`. `last` (the caller's,
        /// per owner) remembers the entry found, so that a lookup repeating the one before it
        /// only compares.
        public bool TryGet(object owner, ReadOnlySpan<long> ids, long now, out TValue value, ref object last)
        {
            if (last is Entry m && !m.Released && m.Owner == owner && ids.SequenceEqual(m.Ids))
            {
                m.LastUsed = now;
                value = m.Value;
                return true;
            }
            int hash = HashOf(owner, ids);
            if (buckets.TryGetValue(hash, out var e))
                for (; e != null; e = e.Next)
                    if (e.Owner == owner && ids.SequenceEqual(e.Ids))
                    {
                        e.LastUsed = now;
                        value = e.Value;
                        last = e;
                        return true;
                    }
            value = default;
            return false;
        }

        public bool TryGet(object owner, ReadOnlySpan<long> ids, long now, out TValue value)
        {
            object last = null;
            return TryGet(owner, ids, now, out value, ref last);
        }

        public void Add(object owner, ReadOnlySpan<long> ids, TValue value, long now) => Add(owner, ids, value, now, out _);

        public void Add(object owner, ReadOnlySpan<long> ids, TValue value, long now, out object entry)
        {
            int hash = HashOf(owner, ids);
            buckets.TryGetValue(hash, out var head);
            var e = new Entry { Owner = owner, Ids = ids.ToArray(), Hash = hash, Value = value, LastUsed = now, Next = head };
            buckets[hash] = e;
            entry = e;
            Count++;
        }

        readonly List<int> emptied = new();
        readonly List<(int Hash, Entry Head)> moved = new();

        /// Releases the entries last used before `before`.
        public void Sweep(long before, Action<TValue> release)
        {
            emptied.Clear();
            moved.Clear();
            foreach (var (hash, head) in buckets)
            {
                Entry kept = null, last = null;
                for (var e = head; e != null; e = e.Next)
                {
                    if (e.LastUsed < before)
                    {
                        e.Released = true;
                        release(e.Value);
                        Count--;
                        continue;
                    }
                    if (last == null) kept = e;
                    else last.Next = e;
                    last = e;
                }
                if (last != null) last.Next = null;
                if (kept == null) emptied.Add(hash);
                else if (kept != head) moved.Add((hash, kept));
            }
            // Applied after the enumeration, which a store into the dictionary would end.
            foreach (var h in emptied) buckets.Remove(h);
            foreach (var (h, head) in moved) buckets[h] = head;
        }

        public void Clear(Action<TValue> release)
        {
            foreach (var head in buckets.Values)
                for (var e = head; e != null; e = e.Next)
                {
                    e.Released = true;
                    release(e.Value);
                }
            buckets.Clear();
            Count = 0;
        }
    }

    /// Set 0 of programs whose bindings are more than the device pushes (Klei's batched
    /// animation shaders bind 39), kept across frames by what they hold: a draw whose textures,
    /// samplers and buffers a set already names binds that set instead of allocating and writing
    /// one. Sets come from pools kept for the device's life and are freed once unused for
    /// `MaxAge` frames, far longer than a frame stays in flight, so no recorded command still
    /// names a freed set. Images are named by their serial, never reused, so a set naming an image
    /// since destroyed is never bound again; it only waits to age out.
    internal sealed unsafe class VkDescriptorCache : IDisposable
    {
        const int MaxAge = 60;
        readonly VkContext vk;
        readonly IdentityTable<(VkDescriptorPool Pool, VkDescriptorSet Set)> table = new();
        readonly List<VkDescriptorPool> pools = new();
        long lastSweep;

        /// Diagnostics: sets made and found since the counters were last taken.
        public long Made, Found;

        public VkDescriptorCache(VkContext vk) => this.vk = vk;

        public int Count => table.Count;

        public bool TryGet(VkProgram program, ReadOnlySpan<long> ids, long frame, out VkDescriptorSet set)
        {
            if (table.TryGet(program, ids, frame, out var known, ref program.LastTextureSet))
            {
                Found++;
                set = known.Set;
                return true;
            }
            set = default;
            return false;
        }

        /// A new set for the program, remembered for these identities; the caller writes it.
        public VkDescriptorSet Add(VkProgram program, ReadOnlySpan<long> ids, long frame)
        {
            var layout = program.TextureSet;
            VkDescriptorSet set = default;
            for (bool full = pools.Count == 0; ; full = true)
            {
                if (full)
                {
                    Span<VkDescriptorPoolSize> sizes =
                    [
                        new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 16384 },
                        new VkDescriptorPoolSize { Type = VkDescriptorType.SampledImage, DescriptorCount = 16384 },
                        new VkDescriptorPoolSize { Type = VkDescriptorType.Sampler, DescriptorCount = 16384 },
                        new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 4096 },
                    ];
                    fixed (VkDescriptorPoolSize* p = sizes)
                        pools.Add(vk.Device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo
                        {
                            Flags = VkDescriptorPoolCreateFlags.FreeDescriptorSetBit, MaxSets = 1024, PoolSizeCount = sizes.Length, PoolSizes = p,
                        }, null));
                }
                var info = new VkDescriptorSetAllocateInfo { DescriptorPool = pools[^1], DescriptorSetCount = 1, SetLayouts = &layout };
                var r = vk.Device.PInvoke.vkAllocateDescriptorSets.Invoke(vk.Device.Handle, &info, &set);
                if (r == VkResult.Success) break;
                if (r != VkResult.ErrorOutOfPoolMemory && r != VkResult.ErrorFragmentedPool) VkCheck.Ok(r, "vkAllocateDescriptorSets");
                // Freed sets leave room in older pools: they are tried before a new one is made.
                bool placed = false;
                for (int i = pools.Count - 2; i >= 0 && !placed; i--)
                {
                    info.DescriptorPool = pools[i];
                    if (vk.Device.PInvoke.vkAllocateDescriptorSets.Invoke(vk.Device.Handle, &info, &set) == VkResult.Success)
                    {
                        pools.Add(pools[i]);
                        pools.RemoveAt(i);
                        placed = true;
                    }
                }
                if (placed) break;
            }
            Made++;
            table.Add(program, ids, (pools[^1], set), frame, out program.LastTextureSet);
            return set;
        }

        /// Frees the sets unused for MaxAge frames (checked every MaxAge frames).
        public void Sweep(long frame)
        {
            if (frame - lastSweep < MaxAge) return;
            lastSweep = frame;
            table.Sweep(frame - MaxAge, Free);
        }

        void Free((VkDescriptorPool Pool, VkDescriptorSet Set) e)
        {
            var s = e.Set;
            vk.Device.PInvoke.vkFreeDescriptorSets.Invoke(vk.Device.Handle, e.Pool, 1, &s);
        }

        public void Dispose()
        {
            table.Clear(_ => { });
            foreach (var p in pools) vk.Device.DestroyDescriptorPool(p, null);
            pools.Clear();
        }
    }
}
