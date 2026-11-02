using OniMoono.Desktop.Vulkan;
using Xunit;

namespace OniMoono.Tests
{
    /// A mesh's spare buffers (VkMesh): replaced buffers its frames are done with take later
    /// uploads that fit, the smallest first, and only a few are kept.
    public class VkMeshSpareTests
    {
        static VkBufferAlloc Buffer(long size) => new() { Size = size };

        [Fact]
        public void TakesTheSmallestSpareThatFits()
        {
            var m = new VkMesh();
            var big = Buffer(4096);
            var small = Buffer(1024);
            Assert.True(m.AddSpare(big));
            Assert.True(m.AddSpare(small));
            Assert.Null(m.TakeSpare(8192));
            Assert.Same(small, m.TakeSpare(1000));
            Assert.Same(big, m.TakeSpare(1000));
            Assert.Null(m.TakeSpare(16));
        }

        [Fact]
        public void KeepsAtMostTwo()
        {
            var m = new VkMesh();
            Assert.True(m.AddSpare(Buffer(16)));
            Assert.True(m.AddSpare(Buffer(16)));
            Assert.False(m.AddSpare(Buffer(16)));
        }

        [Fact]
        public void CapacityLeavesRoomForGrowth()
        {
            Assert.Equal(256, VkMesh.Capacity(16));
            Assert.True(VkMesh.Capacity(100_000) >= 112_500);
            Assert.Equal(0, VkMesh.Capacity(100_000) % 256);
        }
    }
}
