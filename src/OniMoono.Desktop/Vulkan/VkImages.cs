using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

namespace OniMoono.Desktop.Vulkan
{
    /// An image with its views and the state its last use left it in (layout, and the stages
    /// and accesses a barrier must wait for), tracked on the device thread so that each use
    /// can make the barrier it needs.
    internal sealed unsafe class VkImageResource
    {
        static long nextSerial;

        /// Names this image for the life of the process (never reused, unlike its handles):
        /// VkDescriptorCache's sets are found by it.
        public readonly long Serial = System.Threading.Interlocked.Increment(ref nextSerial);
        public VkImage Image;
        public VmaAllocation Allocation;
        /// All levels and layers, for sampling.
        public VkImageView View;
        /// Level 0, layer 0: a render attachment (null when not renderable).
        public VkImageView AttachmentView;
        public VkFormat Format;
        public VkImageViewType ViewType;
        public VkImageAspectFlags Aspect;
        public int Width, Height, Depth = 1, Levels = 1, Layers = 1, Samples = 1;
        public VkImageLayout Layout = VkImageLayout.Undefined;
        public VkPipelineStageFlags2 Stages;
        public VkAccessFlags2 Access;
        /// The pass in which a draw found it ready to sample (VkReadyStamps).
        public long ReadyPass;
        /// Its allocation's size, and its kind in VideoMemoryLedger.
        public long Bytes;
        string kind;

        const VkAccessFlags2 Writes = VkAccessFlags2.ShaderWriteBit | VkAccessFlags2.ColorAttachmentWriteBit | VkAccessFlags2.DepthStencilAttachmentWriteBit
                                      | VkAccessFlags2.TransferWriteBit | VkAccessFlags2.HostWriteBit | VkAccessFlags2.MemoryWriteBit;

        /// Barriers Use made, and those of them from UNDEFINED (frame counts; any thread).
        public static long Barriers, FromUndefined;

        /// Makes the image ready for a use: a barrier unless it is read as it was last read.
        public void Use(CommandBuffer cb, VkImageLayout layout, VkPipelineStageFlags2 stages, VkAccessFlags2 access)
        {
            if (layout == Layout && (Access & Writes) == 0 && (access & Writes) == 0)
            {
                Stages |= stages;
                Access |= access;
                return;
            }
            var barrier = new VkImageMemoryBarrier2
            {
                SrcStageMask = Stages == 0 ? VkPipelineStageFlags2.None : Stages,
                SrcAccessMask = Access,
                DstStageMask = stages,
                DstAccessMask = access,
                OldLayout = Layout,
                NewLayout = layout,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = Image,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = Aspect, LevelCount = Levels, LayerCount = Layers },
            };
            var dependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier };
            cb.PipelineBarrier2(dependency);
            System.Threading.Interlocked.Increment(ref Barriers);
            if (Layout == VkImageLayout.Undefined) System.Threading.Interlocked.Increment(ref FromUndefined);
            Layout = layout;
            Stages = stages;
            Access = access;
        }

        public const VkPipelineStageFlags2 ShaderStages = VkPipelineStageFlags2.VertexShaderBit | VkPipelineStageFlags2.FragmentShaderBit;

        public void ForSampling(CommandBuffer cb) => Use(cb, VkImageLayout.ShaderReadOnlyOptimal, ShaderStages, VkAccessFlags2.ShaderSampledReadBit);

        public void ForTransferWrite(CommandBuffer cb) => Use(cb, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags2.AllTransferBit, VkAccessFlags2.TransferWriteBit);

        public void ForTransferRead(CommandBuffer cb) => Use(cb, VkImageLayout.TransferSrcOptimal, VkPipelineStageFlags2.AllTransferBit, VkAccessFlags2.TransferReadBit);

        public void ForAttachment(CommandBuffer cb)
        {
            if ((Aspect & VkImageAspectFlags.DepthBit) != 0)
                Use(cb, VkImageLayout.DepthStencilAttachmentOptimal, VkPipelineStageFlags2.EarlyFragmentTestsBit | VkPipelineStageFlags2.LateFragmentTestsBit,
                    VkAccessFlags2.DepthStencilAttachmentReadBit | VkAccessFlags2.DepthStencilAttachmentWriteBit);
            else
                Use(cb, VkImageLayout.ColorAttachmentOptimal, VkPipelineStageFlags2.ColorAttachmentOutputBit,
                    VkAccessFlags2.ColorAttachmentReadBit | VkAccessFlags2.ColorAttachmentWriteBit);
        }

        /// Device memory for an image. One rendered into gets memory of its own at the highest
        /// priority (VkContext.OwnTargetMemory): render targets are what the GPU reads and
        /// writes most, many times a frame; in VMA's shared blocks they lie among textures, and
        /// a block the system moves out of video memory takes them along (#115). Vendors advise
        /// dedicated allocations and the highest residency priority for render and depth
        /// targets. The priority counts only with VK_EXT_memory_priority on.
        public static VmaAllocationCreateInfo MemoryFor(VkImageUsageFlags usage, bool ownTargetMemory)
        {
            var alloc = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice };
            if (ownTargetMemory && (usage & (VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.DepthStencilAttachmentBit)) != 0)
            {
                alloc.Flags = VmaAllocationCreateFlags.DedicatedMemoryBit;
                alloc.Priority = 1f;
            }
            return alloc;
        }

        public static bool IsDepth(VkFormat f) => f is VkFormat.D16UNorm or VkFormat.D24UNormS8UInt or VkFormat.D32SFloat or VkFormat.D32SFloatS8UInt;

        public static bool HasStencil(VkFormat f) => f is VkFormat.D24UNormS8UInt or VkFormat.D32SFloatS8UInt;

        /// A device-local image with a view of all its levels (and a level-0 view when it is an
        /// attachment).
        public static VkImageResource Create(VkContext vk, VkFormat format, VkImageViewType viewType, int width, int height, int depth, int levels, int layers,
            VkImageUsageFlags usage, int samples = 1, VkComponentMapping? swizzle = null, string name = null, string kind = "other images")
        {
            var r = new VkImageResource
            {
                Format = format, ViewType = viewType, Width = width, Height = height, Depth = Math.Max(1, depth), Levels = Math.Max(1, levels),
                Layers = Math.Max(1, layers), Samples = samples,
            };
            r.Aspect = IsDepth(format) ? VkImageAspectFlags.DepthBit | (HasStencil(format) ? VkImageAspectFlags.StencilBit : 0) : VkImageAspectFlags.ColorBit;
            var info = new VkImageCreateInfo
            {
                ImageType = viewType == VkImageViewType._3D ? VkImageType._3D : VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D(width, height, r.Depth),
                MipLevels = r.Levels,
                ArrayLayers = r.Layers,
                Samples = (VkSampleCountFlags)samples,
                Tiling = VkImageTiling.Optimal,
                Usage = usage,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
                Flags = viewType == VkImageViewType.Cube ? VkImageCreateFlags.CubeCompatibleBit : 0,
            };
            var alloc = MemoryFor(usage, vk.OwnTargetMemory);
            VkCheck.Ok(vk.Allocator.CreateImage(info, alloc, out r.Image, out r.Allocation, out var allocation), $"vmaCreateImage {width}x{height} {format}{(name != null ? " (" + name + ")" : "")}");
            r.Bytes = (long)allocation.Size.Value;
            r.kind = VkMemoryPlacement.Note(vk, $"images for {usage}", $"{name ?? "an image"} {width}x{height} {format}", kind, allocation.MemoryType, r.Bytes, expectHost: false);
            OniMoono.Desktop.Gl.VideoMemoryLedger.Add(r.kind, r.Bytes, 1);
            try
            {
                // Depth-and-stencil images are sampled through their depth.
                var viewAspect = (r.Aspect & VkImageAspectFlags.DepthBit) != 0 ? VkImageAspectFlags.DepthBit : r.Aspect;
                if ((usage & VkImageUsageFlags.SampledBit) != 0)
                    r.View = vk.Device.CreateImageView(new VkImageViewCreateInfo
                    {
                        Image = r.Image,
                        ViewType = viewType,
                        Format = format,
                        Components = swizzle ?? default,
                        SubresourceRange = new VkImageSubresourceRange { AspectMask = viewAspect, LevelCount = r.Levels, LayerCount = r.Layers },
                    }, null);
                if ((usage & (VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.DepthStencilAttachmentBit)) != 0)
                    r.AttachmentView = vk.Device.CreateImageView(new VkImageViewCreateInfo
                    {
                        Image = r.Image,
                        ViewType = VkImageViewType._2D,
                        Format = format,
                        SubresourceRange = new VkImageSubresourceRange { AspectMask = r.Aspect, LevelCount = 1, LayerCount = 1 },
                    }, null);
            }
            catch
            {
                r.Destroy(vk);
                throw;
            }
            return r;
        }

        public void Destroy(VkContext vk)
        {
            if (!View.IsNull()) vk.Device.DestroyImageView(View, null);
            if (!AttachmentView.IsNull()) vk.Device.DestroyImageView(AttachmentView, null);
            if (!Image.IsNull())
            {
                vk.Allocator.DestroyImage(Image, Allocation);
                OniMoono.Desktop.Gl.VideoMemoryLedger.Add(kind, -Bytes, -1);
            }
            View = AttachmentView = default;
            Image = default;
        }
    }

    /// Diagnostics (#115): where VMA put the device's resources. The memory type of the first
    /// image or buffer of each usage is logged, and a resource the GPU reads that VMA put outside
    /// device-local memory is counted under its kind with " in system memory" in the video
    /// memory line (--frame-stats), the first few, and then each power of two, logged by name.
    /// VMA takes the next memory type when one is full and says nothing (#114): that next type
    /// is system memory, which the GPU reads across the bus.
    internal static class VkMemoryPlacement
    {
        static readonly HashSet<string> logged = new();
        static int outside;

        /// The ledger kind for the allocation: `kind`, or `kind` in system memory when the GPU
        /// will read it from there and `expectHost` is false (staging and readback memory is
        /// host memory by choice).
        public static string Note(VkContext vk, string usage, string name, string kind, uint type, long bytes, bool expectHost)
        {
            var flags = vk.MemoryTypeFlags(type);
            bool first;
            lock (logged) first = logged.Add(usage);
            if (first) OniMoono.Engine.EngineLog.Engine($"vulkan: {usage} in memory type {type} ({flags})");
            // An integrated GPU's memory is system memory: nothing to tell.
            if (expectHost || (flags & VkMemoryPropertyFlags.DeviceLocalBit) != 0 || vk.Properties.DeviceType == VkPhysicalDeviceType.IntegratedGpu) return kind;
            int n = System.Threading.Interlocked.Increment(ref outside);
            if (n <= 8 || (n & (n - 1)) == 0)
                OniMoono.Engine.EngineLog.Engine($"vulkan: {name} ({kind}, {bytes / 1_000_000.0:F1} MB) went into memory type {type} ({flags}), outside device-local memory "
                                                 + $"({n} {(n == 1 ? "resource" : "resources")} so far)");
            return kind + SystemMemory;
        }

        public const string SystemMemory = " in system memory";
    }

    /// Which images the current pass has found ready to sample (VkDevice.PrepareTextures), so a
    /// later draw of the pass checks one number instead of the texture's state. Found means: not
    /// an attachment of the pass, in its sampling layout, no resolve or mips pending, used by
    /// this batch. Within one pass none of that can change: the target is fixed, layout changes
    /// and copies of an image this batch uses end the pass first, an upload into new storage is
    /// a new image, and only a pass's start marks a texture rendered into. Every pass, begun or
    /// ended, has a number of its own, and nothing is found outside a pass, so a stamp is good
    /// for the pass that made it only.
    internal sealed class VkReadyStamps
    {
        long pass = 1;
        bool open;

        public void PassBegun()
        {
            pass++;
            open = true;
        }

        public void PassEnded()
        {
            pass++;
            open = false;
        }

        public bool IsReady(VkImageResource image) => image.ReadyPass == pass;

        /// The texture needed nothing for a draw of this pass. A render texture rendered into
        /// since it was last prepared is not stamped: whether it needs a resolve or mips depends
        /// on the draw (Unity's autoGenerateMips).
        public void Found(VkTexture vt)
        {
            if (open && !(vt.RenderTarget && vt.Dirty)) vt.Image.ReadyPass = pass;
        }
    }

    /// A texture's device side on Vulkan (DeviceTexture.Vk), device thread only: the image it
    /// is sampled from, its sampler, and for render textures the surfaces rendered into.
    internal sealed class VkTexture
    {
        public VkImageResource Image;
        public VkSampler Sampler;
        /// Render textures: the depth-stencil surface (null without depth), and the
        /// multisampled colour surface rendered into before a resolve into Image.
        public VkImageResource DepthStencil, Multisampled;
        public bool RenderTarget, DepthOnly;
        /// Rendered into together with other colour targets (logged when released).
        public bool MultiTarget;
        /// Rendered into since the last resolve or mip generation.
        public bool Dirty;
        /// The last batch whose commands use it (VkFrames.Batch): an update in a later batch
        /// can go ahead of that batch's rendering.
        public long LastUsedBatch = -1;
        /// Uploads into unchanged storage update it in place.
        public UnityEngine.TextureFormat StorageFormat = (UnityEngine.TextureFormat)(-1);
        /// The texels last uploaded (partial updates, as GlTextures.UploadChanged).
        public byte[] UploadedTexels;

        /// The image draws render into (the multisampled one when there is one).
        public VkImageResource RenderImage => Multisampled ?? Image;
    }
}
