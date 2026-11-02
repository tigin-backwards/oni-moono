using System;
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

        const VkAccessFlags2 Writes = VkAccessFlags2.ShaderWriteBit | VkAccessFlags2.ColorAttachmentWriteBit | VkAccessFlags2.DepthStencilAttachmentWriteBit
                                      | VkAccessFlags2.TransferWriteBit | VkAccessFlags2.HostWriteBit | VkAccessFlags2.MemoryWriteBit;

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

        public static bool IsDepth(VkFormat f) => f is VkFormat.D16UNorm or VkFormat.D24UNormS8UInt or VkFormat.D32SFloat or VkFormat.D32SFloatS8UInt;

        public static bool HasStencil(VkFormat f) => f is VkFormat.D24UNormS8UInt or VkFormat.D32SFloatS8UInt;

        /// A device-local image with a view of all its levels (and a level-0 view when it is an
        /// attachment).
        public static VkImageResource Create(VkContext vk, VkFormat format, VkImageViewType viewType, int width, int height, int depth, int levels, int layers,
            VkImageUsageFlags usage, int samples = 1, VkComponentMapping? swizzle = null, string name = null)
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
            var alloc = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice };
            VkCheck.Ok(vk.Allocator.CreateImage(info, alloc, out r.Image, out r.Allocation, out _), $"vmaCreateImage {width}x{height} {format}{(name != null ? " (" + name + ")" : "")}");
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
            if (!Image.IsNull()) vk.Allocator.DestroyImage(Image, Allocation);
            View = AttachmentView = default;
            Image = default;
        }
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
