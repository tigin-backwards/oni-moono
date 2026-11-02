using System;
using OniMoono.Desktop.Gl;

namespace OniMoono.Desktop.Vulkan
{
    /// A mesh's device side on Vulkan (DeviceMesh.Vk), device thread only: one buffer holding
    /// every channel and the indices. Each upload goes to another buffer than the one draws
    /// already recorded read (the old one is set aside when the frames using it are done), so a
    /// draw recorded before a change reads the data it was recorded with, as on OpenGL, without
    /// the rendering waiting for a copy. A mesh changed again and again takes turns between a
    /// few buffers (its spares) rather than making and destroying one per upload.
    internal sealed class VkMesh
    {
        public VkBufferAlloc Buffer;
        /// Each channel's offset in Buffer (-1: absent), and the indices' offset.
        public readonly long[] ChannelOffsets = new long[(int)Channel.Count];
        public long IndexOffset;
        /// Released by the game: buffers its frames are done with are destroyed, not kept.
        public bool Released;

        public const int MaxSpares = 2;
        readonly VkBufferAlloc[] spares = new VkBufferAlloc[MaxSpares];

        /// The size a buffer is made with for a mesh that has changed: room for growth by an
        /// eighth, so that a mesh whose size moves a little keeps its spares.
        public static long Capacity(long size) => (size + size / 8 + 255) & ~255L;

        /// A spare of at least `size` bytes (the smallest), or null.
        public VkBufferAlloc TakeSpare(long size)
        {
            int best = -1;
            for (int i = 0; i < MaxSpares; i++)
                if (spares[i] is { } s && s.Size >= size && (best < 0 || s.Size < spares[best].Size)) best = i;
            if (best < 0) return null;
            var taken = spares[best];
            spares[best] = null;
            return taken;
        }

        /// Keeps a buffer no frame reads any more; false when there is no room (the caller
        /// destroys it).
        public bool AddSpare(VkBufferAlloc buffer)
        {
            for (int i = 0; i < MaxSpares; i++)
                if (spares[i] == null)
                {
                    spares[i] = buffer;
                    return true;
                }
            return false;
        }

        public void DestroySpares(VkContext vk)
        {
            for (int i = 0; i < MaxSpares; i++)
            {
                spares[i]?.Destroy(vk);
                spares[i] = null;
            }
        }
    }
}
