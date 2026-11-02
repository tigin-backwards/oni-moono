using OniMoono.Desktop.Vulkan;
using Xunit;

namespace OniMoono.Tests
{
    /// Images a pass found ready to sample (VkReadyStamps): good for that pass only.
    public class VkReadyStampsTests
    {
        static VkTexture Texture() => new() { Image = new VkImageResource() };

        [Fact]
        public void FoundIsReadyUntilThePassEnds()
        {
            var stamps = new VkReadyStamps();
            var t = Texture();
            Assert.False(stamps.IsReady(t.Image));
            stamps.PassBegun();
            Assert.False(stamps.IsReady(t.Image));
            stamps.Found(t);
            Assert.True(stamps.IsReady(t.Image));
            stamps.PassEnded();
            Assert.False(stamps.IsReady(t.Image));
            // The next pass, even on the same target, finds it again.
            stamps.PassBegun();
            Assert.False(stamps.IsReady(t.Image));
        }

        [Fact]
        public void NothingIsFoundOutsideAPass()
        {
            var stamps = new VkReadyStamps();
            var t = Texture();
            stamps.Found(t);
            Assert.False(stamps.IsReady(t.Image));
            stamps.PassBegun();
            stamps.PassEnded();
            stamps.Found(t);
            Assert.False(stamps.IsReady(t.Image));
            // Not by the next pass either.
            stamps.PassBegun();
            Assert.False(stamps.IsReady(t.Image));
        }

        [Fact]
        public void NewStorageIsNotReady()
        {
            var stamps = new VkReadyStamps();
            var t = Texture();
            stamps.PassBegun();
            stamps.Found(t);
            t.Image = new VkImageResource();
            Assert.False(stamps.IsReady(t.Image));
        }

        /// A render texture rendered into since it was prepared may need a resolve or mips,
        /// depending on the draw: never stamped.
        [Fact]
        public void ARenderedIntoRenderTextureIsNeverStamped()
        {
            var stamps = new VkReadyStamps();
            var t = Texture();
            t.RenderTarget = true;
            t.Dirty = true;
            stamps.PassBegun();
            stamps.Found(t);
            Assert.False(stamps.IsReady(t.Image));
            t.Dirty = false;
            stamps.Found(t);
            Assert.True(stamps.IsReady(t.Image));
        }
    }
}
