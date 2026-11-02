using System.Collections.Generic;
using OniMoono.Desktop.Vulkan;
using Xunit;

namespace OniMoono.Tests
{
    /// The descriptor cache's table (VkDescriptorCache): sets found by program and what they
    /// name, and released once unused for a while.
    public class VkDescriptorCacheTests
    {
        [Fact]
        public void FindsByOwnerAndIdentities()
        {
            var t = new IdentityTable<int>();
            object a = new(), b = new();
            t.Add(a, [1, 2, 3], 10, now: 0);
            t.Add(b, [1, 2, 3], 20, now: 0);
            t.Add(a, [1, 2, 4], 30, now: 0);
            Assert.True(t.TryGet(a, [1, 2, 3], 1, out var v));
            Assert.Equal(10, v);
            Assert.True(t.TryGet(b, [1, 2, 3], 1, out v));
            Assert.Equal(20, v);
            Assert.True(t.TryGet(a, [1, 2, 4], 1, out v));
            Assert.Equal(30, v);
            Assert.False(t.TryGet(a, [1, 2], 1, out _));
            Assert.False(t.TryGet(new object(), [1, 2, 3], 1, out _));
            Assert.Equal(3, t.Count);
        }

        [Fact]
        public void KeepsItsOwnCopyOfTheIdentities()
        {
            var t = new IdentityTable<int>();
            object a = new();
            long[] ids = [5, 6];
            t.Add(a, ids, 1, now: 0);
            ids[0] = 7;
            Assert.True(t.TryGet(a, [5, 6], 0, out _));
            Assert.False(t.TryGet(a, [7, 6], 0, out _));
        }

        [Fact]
        public void SweepReleasesOnlyTheUnused()
        {
            var t = new IdentityTable<int>();
            object a = new();
            for (int i = 0; i < 100; i++) t.Add(a, [i], i, now: 0);
            for (int i = 0; i < 100; i += 2) Assert.True(t.TryGet(a, [i], 50, out _));
            var released = new List<int>();
            t.Sweep(before: 10, released.Add);
            Assert.Equal(50, released.Count);
            Assert.All(released, v => Assert.Equal(1, v % 2));
            Assert.Equal(50, t.Count);
            for (int i = 0; i < 100; i++) Assert.Equal(i % 2 == 0, t.TryGet(a, [i], 60, out _));
            released.Clear();
            t.Clear(released.Add);
            Assert.Equal(50, released.Count);
            Assert.Equal(0, t.Count);
        }

        /// The caller's last entry answers a repeated lookup, never once released.
        [Fact]
        public void TheLastEntryIsForgottenOnceReleased()
        {
            var t = new IdentityTable<int>();
            object a = new();
            t.Add(a, [1], 10, now: 0, out var last);
            t.Add(a, [2], 20, now: 0);
            Assert.True(t.TryGet(a, [1], 5, out var v, ref last));
            Assert.Equal(10, v);
            Assert.True(t.TryGet(a, [2], 5, out v, ref last));
            Assert.Equal(20, v);
            Assert.True(t.TryGet(a, [2], 100, out _, ref last));
            t.Sweep(before: 50, _ => { });
            Assert.True(t.TryGet(a, [2], 101, out _, ref last));
            Assert.False(t.TryGet(a, [1], 101, out _, ref last));
            t.Sweep(before: 200, _ => { });
            Assert.False(t.TryGet(a, [2], 201, out _, ref last));
            Assert.Equal(0, t.Count);
        }

        [Fact]
        public void CollidingHashesStayApart()
        {
            // Many entries in few buckets would still each be found (chains).
            var t = new IdentityTable<long>();
            object a = new();
            for (long i = 0; i < 5000; i++) t.Add(a, [i, i * 31], i, now: i);
            t.Sweep(before: 2500, _ => { });
            Assert.Equal(2500, t.Count);
            for (long i = 0; i < 5000; i++)
            {
                bool found = t.TryGet(a, [i, i * 31], 9999, out var v);
                Assert.Equal(i >= 2500, found);
                if (found) Assert.Equal(i, v);
            }
        }
    }
}
