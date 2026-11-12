using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OniMoono.Desktop.Vulkan
{
    /// The pipeline cache's file (vulkan-pipelines.bin in the run home's cache folder): the
    /// driver's cache data behind our own header, which holds its length and SHA-256, so that a
    /// truncated or damaged file is never handed to the driver. Drivers must refuse data from
    /// another device or driver (the data's own header, VkPipelineCacheHeaderVersionOne), but a
    /// driver reading damaged data past a good header is on its own; both are checked here
    /// before vkCreatePipelineCache sees the data. A file that fails a check is discarded (the
    /// cache starts empty and is written anew at shutdown); one from before this header is too.
    ///
    /// Layout: "MoonoVkPipelines" (16 bytes), the data's length (int64, little endian), its
    /// SHA-256 (32 bytes), the data.
    internal static class VkPipelineCacheFile
    {
        static ReadOnlySpan<byte> Magic => "MoonoVkPipelines"u8;
        const int HeaderSize = 16 + 8 + 32;

        /// Vulkan's VkPipelineCacheHeaderVersionOne: header size, version (1), vendor, device,
        /// pipelineCacheUUID.
        const int VulkanHeaderSize = 4 + 4 + 4 + 4 + 16;

        /// The file for the driver's data.
        public static byte[] Wrap(ReadOnlySpan<byte> data)
        {
            var file = new byte[HeaderSize + data.Length];
            Magic.CopyTo(file);
            BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(16), data.Length);
            SHA256.HashData(data, file.AsSpan(24, 32));
            data.CopyTo(file.AsSpan(HeaderSize));
            return file;
        }

        /// The driver's data in a file, when it is whole and made by this device and driver
        /// (vendor, device and pipelineCacheUUID); otherwise null and why.
        public static byte[] Unwrap(ReadOnlySpan<byte> file, uint vendor, uint device, ReadOnlySpan<byte> cacheUuid, out string why)
        {
            if (file.Length < HeaderSize || !file[..16].SequenceEqual(Magic))
            {
                why = "not a pipeline cache file of this version";
                return null;
            }
            long length = BinaryPrimitives.ReadInt64LittleEndian(file[16..]);
            if (length != file.Length - HeaderSize)
            {
                why = $"its length is wrong ({file.Length - HeaderSize} bytes of {length})";
                return null;
            }
            var data = file[HeaderSize..];
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(data, hash);
            if (!hash.SequenceEqual(file.Slice(24, 32)))
            {
                why = "its contents are damaged";
                return null;
            }
            if (data.Length < VulkanHeaderSize)
            {
                why = "the driver's data has no header";
                return null;
            }
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            if (headerSize < VulkanHeaderSize || headerSize > data.Length || version != 1)
            {
                why = $"the driver's header is not one Vulkan describes (size {headerSize}, version {version})";
                return null;
            }
            uint fileVendor = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]), fileDevice = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
            if (fileVendor != vendor || fileDevice != device)
            {
                why = $"made on another GPU ({fileVendor:x4}:{fileDevice:x4}, this one {vendor:x4}:{device:x4})";
                return null;
            }
            if (!data.Slice(16, 16).SequenceEqual(cacheUuid))
            {
                why = "made by another driver version (its pipelineCacheUUID differs)";
                return null;
            }
            why = null;
            return data.ToArray();
        }
    }
}
