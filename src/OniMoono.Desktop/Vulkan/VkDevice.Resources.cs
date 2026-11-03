using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Vulkan
{
    internal static unsafe class VkTextureWork
    {
        /// Fills levels 1… from level 0 by halving blits (as glGenerateMipmap's box filter);
        /// leaves every level in TRANSFER_SRC_OPTIMAL.
        public static void GenerateMips(CommandBuffer cb, VkImageResource image)
        {
            image.ForTransferWrite(cb);
            for (int level = 1; level <= image.Levels; level++)
            {
                // Level - 1 is written: it becomes the copy's source.
                var barrier = new VkImageMemoryBarrier2
                {
                    SrcStageMask = VkPipelineStageFlags2.AllTransferBit, SrcAccessMask = VkAccessFlags2.TransferWriteBit,
                    DstStageMask = VkPipelineStageFlags2.AllTransferBit, DstAccessMask = VkAccessFlags2.TransferReadBit,
                    OldLayout = VkImageLayout.TransferDstOptimal, NewLayout = VkImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image.Image,
                    SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, BaseMipLevel = level - 1, LevelCount = 1, LayerCount = image.Layers },
                };
                cb.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier });
                if (level == image.Levels) break;
                var blit = new VkImageBlit
                {
                    SrcSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level - 1, LayerCount = image.Layers },
                    DstSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level, LayerCount = image.Layers },
                };
                blit.SrcOffsets[1] = new VkOffset3D(Math.Max(1, image.Width >> (level - 1)), Math.Max(1, image.Height >> (level - 1)), 1);
                blit.DstOffsets[1] = new VkOffset3D(Math.Max(1, image.Width >> level), Math.Max(1, image.Height >> level), 1);
                cb.BlitImage(image.Image, VkImageLayout.TransferSrcOptimal, image.Image, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageBlit>(&blit, 1), VkFilter.Linear);
            }
            image.Layout = VkImageLayout.TransferSrcOptimal;
            image.Stages = VkPipelineStageFlags2.AllTransferBit;
            image.Access = VkAccessFlags2.TransferReadBit | VkAccessFlags2.TransferWriteBit;
        }
    }

    /// The device thread's resources: textures (uploads, render textures, samplers), meshes and
    /// programs, the work GlTextures, GlMeshes and GlDrawRecorder record (IResourceWork).
    internal sealed unsafe partial class VkDevice
    {
        VkFormat depthFormat = VkFormat.D24UNormS8UInt;
        float maxAnisotropy = 1f, maxLodBias;
        readonly HashSet<VkTexture> liveTextures = new();
        readonly HashSet<VkMesh> liveMeshes = new();
        readonly HashSet<VkProgram> livePrograms = new();
        readonly Dictionary<(GlTextures.SamplerSettings, bool, bool), VkSampler> samplers = new();
        VkBufferAlloc constants, emptyStorage;
        VkSampler defaultSampler;
        long textureUploads, textureUploadBytes, unchangedUploads;
        byte[] conversionScratch;
        const string Unbound = "<unbound>";

        /// What every draw may need: constant attribute values, an empty storage buffer, a
        /// default sampler and the shader properties' default textures (main thread, before the
        /// device thread starts: their uploads go in the first frame).
        void CreateShared()
        {
            constants = VkBufferAlloc.Create(vk, 48, VkBufferUsageFlags.VertexBufferBit, hostWrite: true);
            if (constants.Mapped == null) throw new VulkanUnavailableException("no host-visible memory for vertex constants");
            var values = new float[] { 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 1 };
            values.AsSpan().CopyTo(new Span<float>(constants.Mapped, 12));
            constants.Flush(vk);
            emptyStorage = VkBufferAlloc.Create(vk, 256, VkBufferUsageFlags.StorageBufferBit, hostWrite: true);
            new Span<byte>(emptyStorage.Mapped, 256).Clear();
            emptyStorage.Flush(vk);
            defaultSampler = vk.Device.CreateSampler(new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear, MinFilter = VkFilter.Linear, MipmapMode = VkSamplerMipmapMode.Nearest,
                AddressModeU = VkSamplerAddressMode.Repeat, AddressModeV = VkSamplerAddressMode.Repeat, AddressModeW = VkSamplerAddressMode.Repeat,
            }, null);
            MakeDefault("white", 255, 255, 255, 255);
            MakeDefault("black", 0, 0, 0, 0);
            MakeDefault("gray", 128, 128, 128, 128);
            MakeDefault("grey", 128, 128, 128, 128);
            MakeDefault("", 128, 128, 128, 128);
            MakeDefault("bump", 128, 128, 255, 128);
            MakeDefault("red", 255, 0, 0, 255);
            MakeDefault(Unbound, 0, 0, 0, 255);
        }

        /// A 1x1 texture of a colour for each sampler kind (as GlDevice's defaults).
        void MakeDefault(string name, byte r, byte g, byte b, byte a)
        {
            var cb = frames.Uploads;
            void Make(string key, VkImageViewType type, int layers, uint glTarget)
            {
                var image = VkImageResource.Create(vk, VkFormat.R8G8B8A8UNorm, type, 1, 1, 1, 1, layers, VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit, name: "default " + name, kind: "default textures");
                var vt = new VkTexture { Image = image, Sampler = defaultSampler };
                image.ForTransferWrite(cb);
                var color = new VkClearColorValue();
                color.Float32 = new Brutal.Numerics.float4(r / 255f, g / 255f, b / 255f, a / 255f);
                var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = layers };
                vk.Device.PInvoke.vkCmdClearColorImage.Invoke(cb.Handle, image.Image, VkImageLayout.TransferDstOptimal, &color, 1, &range);
                image.ForSampling(cb);
                defaults[key] = new DeviceTexture { Target = glTarget, RecordedTarget = glTarget, Width = 1, Height = 1, Vk = vt };
            }
            Make(name, VkImageViewType._2D, 1, TexTarget2D);
            Make("array:" + name, VkImageViewType._2DArray, 1, TexTarget2DArray);
            Make("3d:" + name, VkImageViewType._3D, 1, TexTarget3D);
            Make("cube:" + name, VkImageViewType.Cube, 6, TexTargetCubeMap);
        }

        DeviceTexture DefaultTexture(string name, uint target)
        {
            name ??= "";
            string prefix = target == TexTarget2DArray ? "array:" : target == TexTarget3D ? "3d:" : target == TexTargetCubeMap ? "cube:" : "";
            return defaults.TryGetValue(prefix + name, out var t) ? t : defaults[prefix + "white"];
        }

        void DestroyShared()
        {
            constants?.Destroy(vk);
            emptyStorage?.Destroy(vk);
            if (!defaultSampler.IsNull()) vk.Device.DestroySampler(defaultSampler, null);
            defaultSampler = default;
        }

        void DestroyTexture(VkTexture vt)
        {
            if (vt == null) return;
            vt.Image?.Destroy(vk);
            vt.DepthStencil?.Destroy(vk);
            vt.Multisampled?.Destroy(vk);
            vt.Image = vt.DepthStencil = vt.Multisampled = null;
        }

        /// The command buffer an update of `vt` goes into: ahead of the batch's rendering when the
        /// batch has not used the texture yet, else at this point of the rendering.
        CommandBuffer ForUpdate(VkTexture vt) => vt.LastUsedBatch == frames.Batch ? OutsideRendering() : frames.Uploads;

        /// Mesh buffers kept as spares (VkMesh), in VideoMemoryLedger.
        const string SpareKind = "mesh spare buffers";

        static readonly Dictionary<(TextureFormat, VkFormat, VkImageViewType), string> textureKinds = new();

        /// A texture's kind in VideoMemoryLedger: its type and format, and the format it is
        /// stored as when converted.
        static string TextureKind(TextureFormat format, VkTextureFormat f, VkImageViewType type)
        {
            var key = (format, f.Format, type);
            if (!textureKinds.TryGetValue(key, out var kind))
                textureKinds[key] = kind = (type == VkImageViewType._2DArray ? "texture array " : type == VkImageViewType._3D ? "texture 3D " : "texture ") + format
                                           + (f.Conversion != TexelConversion.None ? " (converted to " + f.Format + ")" : "");
            return kind;
        }

        // ----- IResourceWork (device thread) -----

        public DeviceProgram Program(ShaderNative shader, int blobIndex)
        {
            var p = programs.Get(shader, blobIndex);
            if (p.Vk != null) livePrograms.Add(p.Vk);
            return p;
        }

        public void CreateRenderTexture(DeviceTexture g, RenderShape shape, string name)
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            var vt = g.Vk ??= new VkTexture();
            liveTextures.Add(vt);
            var (image, depth, multi) = (vt.Image, vt.DepthStencil, vt.Multisampled);
            if (image != null) frames.Retire(() => { image.Destroy(vk); depth?.Destroy(vk); multi?.Destroy(vk); });
            vt.Image = vt.DepthStencil = vt.Multisampled = null;
            vt.Sampler = default;
            vt.RenderTarget = true;
            vt.DepthOnly = shape.ColorFormat == GraphicsFormat.None;
            vt.Dirty = false;
            vt.LastUsedBatch = -1;
            int samples = Math.Max(1, shape.Samples);
            var cb = frames.Uploads;
            if (vt.DepthOnly)
            {
                vt.Image = VkImageResource.Create(vk, VkFormat.D32SFloat, VkImageViewType._2D, shape.Width, shape.Height, 1, 1, 1,
                    VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit, name: name,
                    kind: GlTextures.RenderTargetKind(GraphicsFormat.None));
                ClearDepth(cb, vt.Image);
            }
            else
            {
                var format = VkFormats.Color(shape.ColorFormat);
                vt.Image = VkImageResource.Create(vk, format, VkImageViewType._2D, shape.Width, shape.Height, 1, shape.Levels, 1,
                    VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit, name: name,
                    kind: GlTextures.RenderTargetKind(shape.ColorFormat));
                ClearColor(cb, vt.Image);
                if (samples > 1)
                {
                    vt.Multisampled = VkImageResource.Create(vk, format, VkImageViewType._2D, shape.Width, shape.Height, 1, 1, 1,
                        VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit, samples, name: name + " (multisampled)",
                        kind: DeviceTexture.MultisampledKind);
                    ClearColor(cb, vt.Multisampled);
                }
                if (shape.DepthBits > 0)
                {
                    // As OpenGL's depth renderbuffers (a multisampled one is 24-bit with stencil).
                    var depthFormat = samples > 1 ? this.depthFormat : shape.DepthBits > 24 ? VkFormat.D32SFloatS8UInt : shape.DepthBits > 16 ? this.depthFormat : VkFormat.D16UNorm;
                    vt.DepthStencil = VkImageResource.Create(vk, depthFormat, VkImageViewType._2D, shape.Width, shape.Height, 1, 1, 1,
                        VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.TransferDstBit, samples, name: name + " (depth)",
                        kind: DeviceTexture.DepthKind);
                    ClearDepth(cb, vt.DepthStencil);
                }
            }
            VkTimings.Targets += System.Diagnostics.Stopwatch.GetTimestamp() - t;
        }

        /// New render surfaces start cleared, as Unity's (and GlTextures.ClearNewSurfaces):
        /// colour (0, 0, 0, 0), depth at the far plane, stencil 0.
        void ClearColor(CommandBuffer cb, VkImageResource image)
        {
            image.ForTransferWrite(cb);
            var color = new VkClearColorValue();
            var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = image.Levels, LayerCount = image.Layers };
            vk.Device.PInvoke.vkCmdClearColorImage.Invoke(cb.Handle, image.Image, VkImageLayout.TransferDstOptimal, &color, 1, &range);
        }

        void ClearDepth(CommandBuffer cb, VkImageResource image)
        {
            image.ForTransferWrite(cb);
            // Reversed depth: far is 0 (as OpenGL's initial depth of 1).
            var value = new VkClearDepthStencilValue { Depth = 0f, Stencil = 0 };
            var range = new VkImageSubresourceRange { AspectMask = image.Aspect, LevelCount = 1, LayerCount = 1 };
            vk.Device.PInvoke.vkCmdClearDepthStencilImage.Invoke(cb.Handle, image.Image, VkImageLayout.TransferDstOptimal, &value, 1, &range);
        }

        /// Before a render texture is read: resolve multisampling and rebuild mips (as
        /// GlTextures.PrepareForSampling).
        void PrepareForSampling(CommandBuffer cb, VkTexture vt, bool autoGenerateMips)
        {
            if (!vt.Dirty) return;
            vt.Dirty = false;
            if (vt.Multisampled != null)
            {
                vt.Multisampled.ForTransferRead(cb);
                vt.Image.ForTransferWrite(cb);
                var region = new VkImageResolve
                {
                    SrcSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    DstSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    Extent = new VkExtent3D(vt.Image.Width, vt.Image.Height, 1),
                };
                vk.Device.PInvoke.vkCmdResolveImage.Invoke(cb.Handle, vt.Multisampled.Image, VkImageLayout.TransferSrcOptimal, vt.Image.Image, VkImageLayout.TransferDstOptimal, 1, &region);
            }
            if (vt.Image.Levels > 1 && autoGenerateMips) VkTextureWork.GenerateMips(cb, vt.Image);
        }

        public void UploadTexture(DeviceTexture g, TexelUpload u)
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            u.Copying?.Wait();
            try
            {
                Upload(g, u);
            }
            finally
            {
                // Returned together at present: one take of the pool's lock a frame.
                if (u.Pooled) { if (u.Memory != null) returnedUploads.Add(u.Memory); }
                else u.Memory?.Free();
                u.Memory = null;
                VkTimings.TextureUploads += System.Diagnostics.Stopwatch.GetTimestamp() - t;
                System.Threading.Interlocked.Add(ref OniMoono.Engine.StartupStats.GlUploadTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t);
            }
        }

        /// Storage is made once per size, format and mip count and each level is copied in;
        /// a texture re-uploaded into unchanged storage sends only the rows that changed (as
        /// GlTextures.Upload2D and UploadChanged).
        void Upload(DeviceTexture g, TexelUpload u)
        {
            var f = VkFormats.Of(u.Format, vk.TextureCompressionBC);
            if (!VkFormats.Sampled(f.Format)) throw new NotSupportedException($"Texture format {u.Format} ({f.Format}) is not sampled by this Vulkan device.");
            int levels = Math.Max(1, u.MipCount);
            var type = u.Target == TexTarget2DArray ? VkImageViewType._2DArray : u.Target == TexTarget3D ? VkImageViewType._3D : VkImageViewType._2D;
            int layers = type == VkImageViewType._2DArray ? Math.Max(1, u.Depth) : 1, depth = type == VkImageViewType._3D ? Math.Max(1, u.Depth) : 1;
            var vt = g.Vk ??= new VkTexture();
            liveTextures.Add(vt);
            var image = vt.Image;
            bool same = image != null && !vt.RenderTarget && vt.StorageFormat == u.Format && image.ViewType == type && image.Width == u.Width && image.Height == u.Height
                        && image.Levels == levels && image.Layers == layers && image.Depth == depth;
            if (!same)
            {
                // Made before the old storage is retired: a failure (out of memory) leaves the
                // texture as it was, its images still live.
                VkFault.OutOfMemoryEvery("alloc-every");
                var made = VkImageResource.Create(vk, f.Format, type, u.Width, u.Height, depth, levels, layers,
                    VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit, swizzle: f.Swizzle, kind: TextureKind(u.Format, f, type));
                if (image != null)
                {
                    var (old, oldDepth, oldMulti) = (image, vt.DepthStencil, vt.Multisampled);
                    frames.Retire(() => { old.Destroy(vk); oldDepth?.Destroy(vk); oldMulti?.Destroy(vk); });
                }
                vt.DepthStencil = vt.Multisampled = null;
                vt.RenderTarget = vt.DepthOnly = false;
                vt.Image = image = made;
                vt.StorageFormat = u.Format;
                vt.UploadedTexels = null;
                vt.LastUsedBatch = -1;
                vt.Sampler = default;
            }
            var memory = u.Memory;
            var cb = ForUpdate(vt);
            if (memory == null)
            {
                // No texels (a texture made and not filled): cleared, as drivers do new OpenGL
                // storage; compressed formats cannot be cleared and stay undefined.
                image.ForTransferWrite(cb);
                if (f.BlockSize == 1)
                {
                    var color = new VkClearColorValue();
                    var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = levels, LayerCount = layers };
                    vk.Device.PInvoke.vkCmdClearColorImage.Invoke(cb.Handle, image.Image, VkImageLayout.TransferDstOptimal, &color, 1, &range);
                }
                image.ForSampling(cb);
                return;
            }
            if (same && type == VkImageViewType._2D && UploadChanged(cb, u, vt, f, levels)) return;
            textureUploads++;
            image.ForTransferWrite(cb);
            // Regions and their buffers pair up from here; a failure partway leaves none behind.
            stagedBuffers.Clear();
            var regions = new List<VkBufferImageCopy>();
            for (int m = 0; m < levels; m++)
            {
                int w = Math.Max(1, u.Width >> m), h = Math.Max(1, u.Height >> m);
                if (type == VkImageViewType._2DArray)
                {
                    long sliceSize = TextureFormats.ImageSize(u.Format, u.Width, u.Height, u.MipCount);
                    long mipOffset = TextureFormats.MipOffset(u.Format, u.Width, u.Height, m);
                    long mipSize = TextureFormats.MipSize(u.Format, w, h);
                    for (int s = 0; s < layers; s++)
                        regions.Add(Stage(memory.Pointer + s * sliceSize + mipOffset, mipSize, u.Format, f, w, h, 1, m, s, 0));
                }
                else if (type == VkImageViewType._3D)
                {
                    int d = Math.Max(1, u.Depth >> m);
                    long slice = TextureFormats.MipSize(u.Format, w, h);
                    for (int z = 0; z < d; z++) regions.Add(Stage(memory.Pointer + u.MipOffset(m) + z * slice, slice, u.Format, f, w, h, 1, m, 0, z));
                }
                else regions.Add(Stage(memory.Pointer + u.MipOffset(m), u.MipSize(m), u.Format, f, w, h, 1, m, 0, 0));
            }
            var array = regions.ToArray();
            fixed (VkBufferImageCopy* r = array)
            {
                // All regions come from the frame's stream; each names its buffer.
                for (int i = 0; i < array.Length; i++)
                    vk.Device.PInvoke.vkCmdCopyBufferToImage.Invoke(cb.Handle, stagedBuffers[i], image.Image, VkImageLayout.TransferDstOptimal, 1, r + i);
            }
            stagedBuffers.Clear();
            image.ForSampling(cb);
        }

        readonly List<VkBuffer> stagedBuffers = new();
        readonly List<PixelMemory> returnedUploads = new();

        /// One level (or slice) copied into the stream, converted when the format needs it; the
        /// region that copies it.
        VkBufferImageCopy Stage(byte* texels, long length, TextureFormat format, VkTextureFormat f, int w, int h, int d, int level, int layer, int z)
        {
            var data = VkFormats.Convert(f.Conversion, format, texels, length, w, h, ref conversionScratch);
            var (buffer, offset) = frames.Staging(data, Math.Max(16, f.BlockBytes));
            textureUploadBytes += data.Length;
            stagedBuffers.Add(buffer);
            return new VkBufferImageCopy
            {
                BufferOffset = (Brutal.ByteSize64)offset,
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level, BaseArrayLayer = layer, LayerCount = 1 },
                ImageOffset = new VkOffset3D(0, 0, z),
                ImageExtent = new VkExtent3D(w, h, d),
            };
        }

        const long UploadedTexelsLimit = 4 << 20;

        /// GlTextures.UploadChanged: only the rows between the first and the last that differ
        /// from the previous upload, or none. False (a full upload) the first time.
        bool UploadChanged(CommandBuffer cb, TexelUpload u, VkTexture vt, VkTextureFormat f, int levels)
        {
            long length = u.MipSize(0);
            if (levels != 1 || f.Conversion != TexelConversion.None || f.BlockSize > 1 || length > UploadedTexelsLimit || length % u.Height != 0) return false;
            var now = new ReadOnlySpan<byte>(u.Memory.Pointer, (int)length);
            var before = vt.UploadedTexels;
            if (before == null || before.Length != length)
            {
                vt.UploadedTexels = now.ToArray();
                return false;
            }
            int rowBytes = (int)(length / u.Height), first = 0, last = u.Height - 1;
            while (first < u.Height && now.Slice(first * rowBytes, rowBytes).SequenceEqual(before.AsSpan(first * rowBytes, rowBytes))) first++;
            if (first >= u.Height)
            {
                unchangedUploads++;
                return true;
            }
            while (now.Slice(last * rowBytes, rowBytes).SequenceEqual(before.AsSpan(last * rowBytes, rowBytes))) last--;
            int rows = last - first + 1;
            var changed = now.Slice(first * rowBytes, rows * rowBytes);
            var (buffer, offset) = frames.Staging(changed, 16);
            changed.CopyTo(before.AsSpan(first * rowBytes));
            textureUploads++;
            textureUploadBytes += changed.Length;
            vt.Image.ForTransferWrite(cb);
            var region = new VkBufferImageCopy
            {
                BufferOffset = (Brutal.ByteSize64)offset,
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageOffset = new VkOffset3D(0, first, 0),
                ImageExtent = new VkExtent3D(u.Width, rows, 1),
            };
            vk.Device.PInvoke.vkCmdCopyBufferToImage.Invoke(cb.Handle, buffer, vt.Image.Image, VkImageLayout.TransferDstOptimal, 1, &region);
            vt.Image.ForSampling(cb);
            return true;
        }

        /// Point: nearest; Bilinear: linear within a level, nearest level; Trilinear: linear
        /// across levels (as GlTextures.ApplySampler).
        public void ApplySampler(DeviceTexture g, GlTextures.SamplerSettings s)
        {
            var vt = g.Vk;
            if (vt?.Image == null) return;
            bool mips = vt.Image.Levels > 1, volume = vt.Image.ViewType == VkImageViewType._3D;
            var key = (s, mips, volume);
            if (!samplers.TryGetValue(key, out var sampler))
            {
                var filter = s.FilterMode == FilterMode.Point ? VkFilter.Nearest : VkFilter.Linear;
                float anisotropy = maxAnisotropy > 1f && s.FilterMode != FilterMode.Point ? Math.Clamp(s.Aniso, 1, maxAnisotropy) : 1f;
                sampler = vk.Device.CreateSampler(new VkSamplerCreateInfo
                {
                    MagFilter = filter, MinFilter = filter,
                    MipmapMode = s.FilterMode == FilterMode.Trilinear && mips ? VkSamplerMipmapMode.Linear : VkSamplerMipmapMode.Nearest,
                    AddressModeU = Wrap(s.WrapU), AddressModeV = Wrap(s.WrapV), AddressModeW = volume ? Wrap(s.WrapW) : VkSamplerAddressMode.Repeat,
                    MipLodBias = Math.Clamp(s.MipBias, -maxLodBias, maxLodBias),
                    AnisotropyEnable = anisotropy > 1f, MaxAnisotropy = anisotropy,
                    // A texture without mips is read from its one level (OpenGL's non-mipmap
                    // minification filters).
                    MaxLod = mips ? 1000f : 0f,
                }, null);
                samplers[key] = sampler;
            }
            vt.Sampler = sampler;
        }

        VkSamplerAddressMode Wrap(TextureWrapMode m) => m switch
        {
            TextureWrapMode.Clamp => VkSamplerAddressMode.ClampToEdge,
            TextureWrapMode.Mirror => VkSamplerAddressMode.MirroredRepeat,
            TextureWrapMode.MirrorOnce => vk.MirrorClampToEdge ? VkSamplerAddressMode.MirrorClampToEdge : VkSamplerAddressMode.MirroredRepeat,
            _ => VkSamplerAddressMode.Repeat,
        };

        /// A new buffer per upload with every channel and the indices, written directly when
        /// the memory is host visible, else staged and copied ahead of the batch's rendering.
        public void UploadMesh(DeviceMesh d, MeshUpload upload)
        {
            try
            {
                CopyMesh(d, upload);
            }
            finally
            {
                upload.Ran();
            }
        }

        void CopyMesh(DeviceMesh d, MeshUpload upload)
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            var vm = d.Vk ??= new VkMesh();
            liveMeshes.Add(vm);
            long total = 0;
            Span<long> offsets = stackalloc long[vm.ChannelOffsets.Length];
            for (int c = 0; c < upload.Channels.Length; c++)
            {
                var array = upload.Channels[c];
                offsets[c] = -1;
                if (array == null) continue;
                offsets[c] = total;
                total = Align(total + Bytes(array), 16);
            }
            long indexOffset = total;
            total += (long)(upload.Indices?.Length ?? 0) * 4;
            var old = vm.Buffer;
            long size = Math.Max(16, total);
            // A buffer of the mesh's that the GPU is done with (a spare) takes the upload when it
            // is large enough; else a new one, with room for some growth once the mesh changes.
            VkBufferAlloc buffer = vm.TakeSpare(size);
            buffer?.Relabel(DeviceMesh.Kind);
            if (buffer == null)
                try
                {
                    VkFault.OutOfMemoryEvery("alloc-every");
                    buffer = VkBufferAlloc.Create(vk, old != null ? VkMesh.Capacity(size) : size,
                        VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.TransferDstBit, hostWrite: !StagedMeshes, allowTransferInstead: true,
                        kind: DeviceMesh.Kind);
                }
                catch
                {
                    // The old data no longer matches the mesh's submeshes (their index ranges are the
                    // new upload's): the mesh draws nothing until its next upload. The old buffer is
                    // retired, never destroyed while recorded draws may still read it.
                    if (old != null) RetireMeshBuffer(vm, old);
                    vm.Buffer = null;
                    throw;
                }
            if (old != null) RetireMeshBuffer(vm, old);
            vm.Buffer = buffer;
            offsets.CopyTo(vm.ChannelOffsets);
            vm.IndexOffset = indexOffset;
            byte* target;
            VkBuffer staging = default;
            long stagingOffset = 0;
            if (buffer.Mapped != null) target = buffer.Mapped;
            else
            {
                (staging, stagingOffset, var p) = frames.Staging(size, 16);
                target = (byte*)p;
            }
            for (int c = 0; c < upload.Channels.Length; c++)
            {
                var array = upload.Channels[c];
                if (array == null) continue;
                var bytes = AsBytes(array);
                bytes.CopyTo(new Span<byte>(target + vm.ChannelOffsets[c], bytes.Length));
            }
            if (upload.Indices != null) MemoryMarshal.AsBytes(upload.Indices.AsSpan()).CopyTo(new Span<byte>(target + vm.IndexOffset, upload.Indices.Length * 4));
            if (buffer.Mapped != null) buffer.Flush(vk, size);
            else
            {
                var copy = new VkBufferCopy { SrcOffset = (Brutal.ByteSize64)stagingOffset, DstOffset = default, Size = (Brutal.ByteSize64)size };
                vk.Device.PInvoke.vkCmdCopyBuffer.Invoke(frames.Uploads.Handle, staging, buffer.Buffer, 1, &copy);
            }
            VkTimings.MeshUploads += System.Diagnostics.Stopwatch.GetTimestamp() - t;
        }

        /// Mesh buffers in device memory the CPU does not map, written by a copy from the
        /// staging stream, on a discrete GPU: memory it lets the CPU map (resizable BAR) is
        /// written across the bus, which made each upload about five times as long (NVIDIA RTX
        /// 3090, U57: 0.8 ms of mesh uploads a frame against 0.15 staged). An integrated GPU's
        /// memory is the CPU's own: written directly. ONI_VULKAN_MESHES=staged|mapped decides.
        bool StagedMeshes => stagedMeshes ??= Environment.GetEnvironmentVariable("ONI_VULKAN_MESHES") switch
        {
            "staged" => true,
            "mapped" => false,
            _ => vk.Properties.DeviceType == VkPhysicalDeviceType.DiscreteGpu,
        };

        bool? stagedMeshes;

        static long Align(long v, long a) => (v + a - 1) / a * a;

        static long Bytes(Array a) => a switch
        {
            Vector3[] v => v.Length * 12L,
            Vector4[] v => v.Length * 16L,
            Color[] v => v.Length * 16L,
            Color32[] v => v.Length * 4L,
            _ => throw new NotSupportedException(a.GetType().Name),
        };

        static ReadOnlySpan<byte> AsBytes(Array a) => a switch
        {
            Vector3[] v => MemoryMarshal.AsBytes(v.AsSpan()),
            Vector4[] v => MemoryMarshal.AsBytes(v.AsSpan()),
            Color[] v => MemoryMarshal.AsBytes(v.AsSpan()),
            Color32[] v => MemoryMarshal.AsBytes(v.AsSpan()),
            _ => throw new NotSupportedException(a.GetType().Name),
        };

        // ----- Releases (device thread) -----

        void ReleaseTexture(DeviceTexture g)
        {
            var vt = g.Vk;
            if (vt == null) return;
            g.Vk = null;
            liveTextures.Remove(vt);
            // As the OpenGL device's multi-target framebuffer line (the QA's resize scenario).
            if (vt.MultiTarget) OniMoono.Engine.EngineLog.Engine("vulkan: multi-target render texture released");
            frames.Retire(() => DestroyTexture(vt));
        }

        void ReleaseMesh(DeviceMesh d)
        {
            var vm = d.Vk;
            if (vm == null) return;
            d.Vk = null;
            liveMeshes.Remove(vm);
            vm.Released = true;
            // Spares no frame reads: destroyed now; the buffer once its frames are done.
            vm.DestroySpares(vk);
            var buffer = vm.Buffer;
            if (buffer != null) frames.Retire(() => buffer.Destroy(vk));
        }

        /// A mesh's replaced buffer becomes a spare once the frames reading it are done (or is
        /// destroyed then, when the mesh has spares enough or is gone).
        void RetireMeshBuffer(VkMesh vm, VkBufferAlloc old)
        {
            frames.Retire(() =>
            {
                if (vm.Released || !vm.AddSpare(old)) old.Destroy(vk);
                else old.Relabel(SpareKind);
            });
        }

        void ReleasePrograms(DeviceProgramSet set)
        {
            var retired = new List<VkPipeline>();
            var released = new List<VkProgram>();
            foreach (var p in set.Programs.Values)
                if (p.Vk != null)
                {
                    pipelines.Forget(p.Vk, retired);
                    livePrograms.Remove(p.Vk);
                    released.Add(p.Vk);
                    p.Vk = null;
                }
            set.Programs.Clear();
            if (retired.Count > 0 || released.Count > 0)
                frames.Retire(() =>
                {
                    foreach (var pipeline in retired) vk.Device.DestroyPipeline(pipeline, null);
                    foreach (var program in released) program.Destroy(vk);
                });
            if (retired.Contains(boundPipeline)) boundPipeline = default;
        }
    }
}
