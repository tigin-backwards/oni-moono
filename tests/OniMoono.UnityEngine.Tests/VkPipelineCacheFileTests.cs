using System;
using System.Buffers.Binary;
using OniMoono.Desktop.Vulkan;
using Xunit;

namespace OniMoono.Tests
{
    /// The pipeline cache's file: the driver's data comes back only when the file is whole and
    /// the data's Vulkan header names this GPU and driver.
    public class VkPipelineCacheFileTests
    {
        static readonly byte[] Uuid = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

        /// Driver data as vkGetPipelineCacheData writes it: VkPipelineCacheHeaderVersionOne, then
        /// the driver's own bytes.
        static byte[] DriverData(uint vendor = 0x10DE, uint device = 0x2204, byte[] uuid = null, uint version = 1, int body = 100)
        {
            var data = new byte[32 + body];
            BinaryPrimitives.WriteUInt32LittleEndian(data, 32);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), version);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), vendor);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), device);
            (uuid ?? Uuid).CopyTo(data, 16);
            for (int i = 32; i < data.Length; i++) data[i] = (byte)i;
            return data;
        }

        static byte[] Read(byte[] file, out string why) => VkPipelineCacheFile.Unwrap(file, 0x10DE, 0x2204, Uuid, out why);

        [Fact]
        public void AWholeFileGivesTheDriversDataBack()
        {
            var data = DriverData();
            Assert.Equal(data, Read(VkPipelineCacheFile.Wrap(data), out var why));
            Assert.Null(why);
        }

        [Fact]
        public void ATruncatedOrDamagedFileIsDiscarded()
        {
            var file = VkPipelineCacheFile.Wrap(DriverData());
            Assert.Null(Read(file.AsSpan(0, file.Length - 1).ToArray(), out var why));
            Assert.Contains("length", why);
            var damaged = (byte[])file.Clone();
            damaged[^5] ^= 0x40;
            Assert.Null(Read(damaged, out why));
            Assert.Contains("damaged", why);
            Assert.Null(Read([], out why));
        }

        [Fact]
        public void AnOlderFileIsDiscarded()
        {
            // Before the header: the driver's data alone.
            Assert.Null(Read(DriverData(), out var why));
            Assert.Contains("not a pipeline cache file", why);
        }

        [Fact]
        public void DataFromAnotherGpuOrDriverIsDiscarded()
        {
            Assert.Null(Read(VkPipelineCacheFile.Wrap(DriverData(vendor: 0x1002)), out var why));
            Assert.Contains("another GPU", why);
            Assert.Null(Read(VkPipelineCacheFile.Wrap(DriverData(device: 0x2206)), out why));
            Assert.Contains("another GPU", why);
            var otherUuid = (byte[])Uuid.Clone();
            otherUuid[15] = 0;
            Assert.Null(Read(VkPipelineCacheFile.Wrap(DriverData(uuid: otherUuid)), out why));
            Assert.Contains("another driver version", why);
        }

        [Fact]
        public void AHeaderVulkanDoesNotDescribeIsDiscarded()
        {
            Assert.Null(Read(VkPipelineCacheFile.Wrap(DriverData(version: 2)), out var why));
            Assert.Contains("header", why);
            Assert.Null(Read(VkPipelineCacheFile.Wrap(new byte[8]), out why));
            Assert.Contains("no header", why);
            var data = DriverData(body: 0);
            BinaryPrimitives.WriteUInt32LittleEndian(data, 64);
            Assert.Null(Read(VkPipelineCacheFile.Wrap(data), out why));
            Assert.Contains("header", why);
        }
    }
}
