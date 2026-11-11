using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Brutal.VulkanApi;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Desktop.Vulkan
{
    internal sealed unsafe partial class VkDevice
    {
        /// AsyncGPUReadback: the render texture is copied into a host buffer with the frame's
        /// work; the frame's fence covers the copy, and once the device thread has seen it
        /// signalled (the frame slot's recycle, two frames on) the buffer's texels are the
        /// request's (VkReadback). The main thread does not wait for the GPU.
        public override DeviceReadback BeginReadback(RenderTextureNative source, int width, int height, TextureFormat format)
        {
            if (!TextureFormats.CanEncode(format)) return null;
            var device = GlTextures.Prepare(worker, source);
            bool autoMips = source.AutoGenerateMips;
            var read = new VkReadback(this, width, height, format);
            worker.Post(() => StartReadback(read, device, autoMips));
            return read;
        }

        /// Device thread: the copy, after everything recorded before it.
        void StartReadback(VkReadback read, DeviceTexture device, bool autoMips)
        {
            var vt = device.Vk;
            if (deviceLost || vt?.Image == null || vt.DepthOnly)
            {
                read.Fail();
                return;
            }
            var cb = OutsideRendering();
            PrepareForSampling(cb, vt, autoMips);
            var image = vt.Image;
            if (image.Width < read.Width || image.Height < read.Height)
            {
                read.Fail();
                return;
            }
            int texel = VkFormats.TexelBytes(image.Format);
            var buffer = VkBufferAlloc.Create(vk, (long)read.Width * read.Height * texel, VkBufferUsageFlags.TransferDstBit, hostWrite: false, readback: true, kind: "readback buffers");
            image.ForTransferRead(cb);
            var region = new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageOffset = new VkOffset3D(0, 0, 0),
                ImageExtent = new VkExtent3D(read.Width, read.Height, 1),
            };
            vk.Device.PInvoke.vkCmdCopyImageToBuffer.Invoke(cb.Handle, image.Image, VkImageLayout.TransferSrcOptimal, buffer.Buffer, 1, &region);
            read.Buffer = buffer;
            read.ImageFormat = image.Format;
            read.TexelBytes = texel;
            frames.Retire(() => read.GpuDone(vk));
        }

        /// Main thread: the GPU's copy done now (submitting what was recorded and waiting).
        internal void WaitReadback(VkReadback read)
        {
            if (!read.Ready && !read.Converting)
                if (!worker.TryCall(() =>
                {
                    if (!read.Seen && read.Buffer != null && !deviceLost)
                    {
                        FlushStreams();
                        frames.Submit(endFrame: false);
                        vk.Queue.WaitIdle();
                    }
                    read.GpuDone(vk);
                    return 0;
                }, out _))
                    read.Fail();
            read.WaitConversion();
        }

        /// Main thread: frees the buffer (on the device thread, after the copy is done).
        internal void ReleaseReadback(VkReadback read)
        {
            read.FreeConverted();
            worker.Post(() =>
            {
                read.Seen = true;
                read.Buffer?.Destroy(vk);
                read.Buffer = null;
            });
        }
    }

    /// A render texture's copy into a host buffer (VkDevice.BeginReadback). An RGBA8 image read
    /// as RGBA32 is the buffer's bytes; any other is converted on a pool thread as ReadPixels
    /// converts it (VkFormats.ReadTexel, then TextureFormats.Encode).
    internal sealed unsafe class VkReadback(VkDevice device, int width, int height, TextureFormat format) : DeviceReadback
    {
        public readonly int Width = width, Height = height;
        // Device thread.
        public VkBufferAlloc Buffer;
        public VkFormat ImageFormat;
        public int TexelBytes;
        /// The copy's completion was seen (or the buffer released): GpuDone does nothing again.
        public bool Seen;
        Task conversion;
        byte* converted;

        public bool Converting => conversion != null;

        public void Fail()
        {
            Failed = true;
            Ready = true;
        }

        /// Device thread, once the GPU has done the copy.
        public void GpuDone(VkContext vk)
        {
            if (Seen) return;
            Seen = true;
            if (Buffer == null)
            {
                Fail();
                return;
            }
            Buffer.Invalidate(vk);
            var mapped = Buffer.Mapped;
            if (format == TextureFormat.RGBA32 && ImageFormat == VkFormat.R8G8B8A8UNorm)
            {
                Data = mapped;
                Size = Width * Height * 4;
                Ready = true;
                return;
            }
            var imageFormat = ImageFormat;
            int texel = TexelBytes;
            conversion = Task.Run(() =>
            {
                try
                {
                    long size = TextureFormats.MipSize(format, Width, Height);
                    var into = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, size));
                    var span = new Span<byte>(into, (int)size);
                    for (int y = 0; y < Height; y++)
                        for (int x = 0; x < Width; x++)
                            TextureFormats.Encode(format, span, Width, x, y, VkFormats.ReadTexel(imageFormat, mapped + ((long)y * Width + x) * texel));
                    converted = into;
                    Data = into;
                    Size = (int)size;
                }
                catch (Exception)
                {
                    Failed = true;
                }
                Ready = true;
            });
        }

        public void WaitConversion() => conversion?.Wait();

        public void FreeConverted()
        {
            conversion?.Wait();
            if (converted != null) NativeMemory.Free(converted);
            converted = null;
            Data = null;
        }

        public override void Wait() => device.WaitReadback(this);

        public override void Release() => device.ReleaseReadback(this);
    }
}
