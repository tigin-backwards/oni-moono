using Brutal.VulkanApi;
using OniMoono.Desktop.Vulkan;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// The Vulkan device's sampler filters (VkDevice.Filtering): GlTextures.ApplySampler's
    /// filters and anisotropy, except that NVIDIA's driver gets an anisotropic texture as
    /// trilinear (D3D11's ANISOTROPIC samples across levels too).
    public class VkSamplerFilteringTests
    {
        const VkFilter Nearest = VkFilter.Nearest, Linear = VkFilter.Linear;
        const VkSamplerMipmapMode MipNearest = VkSamplerMipmapMode.Nearest, MipLinear = VkSamplerMipmapMode.Linear;

        [Theory]
        // Point: nearest, never anisotropic.
        [InlineData(FilterMode.Point, 4, true, 16f, false, Nearest, MipNearest, 1f)]
        [InlineData(FilterMode.Point, 4, true, 16f, true, Nearest, MipNearest, 1f)]
        // Bilinear and Trilinear with the texture's level, up to the device's limit.
        [InlineData(FilterMode.Bilinear, 0, true, 16f, false, Linear, MipNearest, 1f)]
        [InlineData(FilterMode.Bilinear, 1, true, 16f, false, Linear, MipNearest, 1f)]
        [InlineData(FilterMode.Bilinear, 4, true, 16f, false, Linear, MipNearest, 4f)]
        [InlineData(FilterMode.Trilinear, 4, true, 16f, false, Linear, MipLinear, 4f)]
        [InlineData(FilterMode.Bilinear, 16, true, 8f, false, Linear, MipNearest, 8f)]
        // Without the samplerAnisotropy feature.
        [InlineData(FilterMode.Bilinear, 4, true, 1f, false, Linear, MipNearest, 1f)]
        // NVIDIA: an anisotropic texture is trilinear, without anisotropy; the others as elsewhere.
        [InlineData(FilterMode.Bilinear, 4, true, 16f, true, Linear, MipLinear, 1f)]
        [InlineData(FilterMode.Trilinear, 4, true, 16f, true, Linear, MipLinear, 1f)]
        [InlineData(FilterMode.Bilinear, 1, true, 16f, true, Linear, MipNearest, 1f)]
        [InlineData(FilterMode.Trilinear, 1, true, 16f, true, Linear, MipLinear, 1f)]
        // Without mips there is one level to read.
        [InlineData(FilterMode.Bilinear, 4, false, 16f, true, Linear, MipNearest, 1f)]
        [InlineData(FilterMode.Trilinear, 1, false, 16f, false, Linear, MipNearest, 1f)]
        public void Filters(FilterMode mode, int aniso, bool mips, float maxAnisotropy, bool nvidia, VkFilter filter, VkSamplerMipmapMode mipmapMode, float anisotropy)
            => Assert.Equal((filter, mipmapMode, anisotropy), OniMoono.Desktop.Vulkan.VkDevice.Filtering(mode, aniso, mips, maxAnisotropy, nvidia));
    }
}
