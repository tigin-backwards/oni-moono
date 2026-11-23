using System;
using System.Linq;
using OniMoono.Desktop;
using OniMoono.Desktop.Vulkan;
using Xunit;

namespace OniMoono.Tests
{
    /// The Vulkan device's handling of damaged input and failures that need no GPU: SPIR-V
    /// reflection of a damaged module and the fault-injection settings.
    public class VkRobustnessTests
    {
        static byte[] Module(params uint[] instructions)
        {
            uint[] header = [0x07230203, 0x00010000, 0, 100, 0];
            return header.Concat(instructions).SelectMany(BitConverter.GetBytes).ToArray();
        }

        /// A damaged mod bundle's module whose array type is its own element: an error, not a
        /// device thread spinning for ever.
        [Fact]
        public void ASelfReferencingArrayIsRefused()
        {
            var module = Module(
                (4u << 16) | 28, 10, 10, 11,      // %10 = OpTypeArray %10 %11
                (4u << 16) | 32, 12, 0, 10,       // %12 = OpTypePointer UniformConstant %10
                (4u << 16) | 59, 12, 13, 0,       // %13 = OpVariable %12 UniformConstant
                (4u << 16) | 71, 13, 33, 0);      // OpDecorate %13 Binding 0
            Assert.Throws<FormatException>(() => SpirvReflection.Interface(module));
        }

        [Fact]
        public void FaultSettingsParse()
        {
            var f = VkFault.Parse("presenter-create, alloc-every:40,present-after:600,guard-build:test");
            Assert.Equal("", f["presenter-create"]);
            Assert.Equal("40", f["alloc-every"]);
            Assert.Equal("600", f["present-after"]);
            Assert.Equal("test", f["guard-build"]);
            Assert.Empty(VkFault.Parse(null));
        }

        /// heap-limit:MB caps the device-local heaps alone, as a small iGPU's would be (#114).
        [Fact]
        public void HeapLimitCapsDeviceLocalHeapsOnly()
        {
            Assert.Equal("1879", VkFault.Parse("heap-limit:1879")["heap-limit"]);
            Assert.Equal(1_879_000_000UL, VkContext.HeapSizeLimit(1_879_000_000, 24_000_000_000, deviceLocal: true));
            Assert.Equal(256_000_000UL, VkContext.HeapSizeLimit(1_879_000_000, 256_000_000, deviceLocal: true));
            Assert.Equal(ulong.MaxValue, VkContext.HeapSizeLimit(1_879_000_000, 27_000_000_000, deviceLocal: false));
            Assert.Equal(ulong.MaxValue, VkContext.HeapSizeLimit(0, 24_000_000_000, deviceLocal: true));
        }
    }
}
