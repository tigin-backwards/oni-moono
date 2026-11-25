using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using OniMoono.Desktop.Vulkan;
using Xunit;

namespace OniMoono.Tests
{
    /// Where images ask VMA to put them (VkImageResource.MemoryFor, #115): render targets on
    /// memory of their own at the highest priority, everything else in the shared blocks.
    public class VkImageAllocationTests
    {
        const VkImageUsageFlags Texture = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit;

        [Theory]
        [InlineData(VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit)]
        [InlineData(VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.TransferDstBit)]
        [InlineData(VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit)]
        public void RenderTargetsGetMemoryOfTheirOwnAtHighPriority(VkImageUsageFlags usage)
        {
            var a = VkImageResource.MemoryFor(usage, ownTargetMemory: true);
            Assert.Equal(VmaMemoryUsage.AutoPreferDevice, a.Usage);
            Assert.Equal(VmaAllocationCreateFlags.DedicatedMemoryBit, a.Flags);
            Assert.Equal(1f, a.Priority);
        }

        [Fact]
        public void TexturesShareTheBlocks()
        {
            var a = VkImageResource.MemoryFor(Texture, ownTargetMemory: true);
            Assert.Equal(VmaMemoryUsage.AutoPreferDevice, a.Usage);
            Assert.Equal(VmaAllocationCreateFlags.None, a.Flags);
            Assert.Equal(0f, a.Priority);
        }

        [Fact]
        public void PooledTargetsShareTheBlocksAsBefore()
        {
            var a = VkImageResource.MemoryFor(VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit, ownTargetMemory: false);
            Assert.Equal(VmaAllocationCreateFlags.None, a.Flags);
            Assert.Equal(0f, a.Priority);
        }
    }
}
