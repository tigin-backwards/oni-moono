using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Brutal.VulkanApi;
using OniMoono.Engine.Rendering;

namespace OniMoono.Desktop.Vulkan
{
    /// The loading splash (SplashRain) drawn by the Vulkan presenter thread into the swapchain
    /// image after the frame is copied there: premultiplied comets from the kanim atlas, and the
    /// progress bar as cleared rectangles.
    internal sealed unsafe class VkSplash : IDisposable
    {
        readonly VkContext vk;
        readonly SplashRain rain;
        readonly VkShaderModule vertexShader, fragmentShader;
        VkImageResource atlas;
        VkSampler sampler;
        VkDescriptorSetLayout setLayout;
        VkDescriptorPool descriptorPool;
        VkDescriptorSet set;
        VkPipelineLayout layout;
        VkPipeline pipeline;
        VkFormat pipelineFormat;
        readonly VkBufferAlloc[] vertexBuffers = new VkBufferAlloc[2];
        readonly List<float> vertices = new();
        readonly List<SplashRain.BarRect> bar = new();

        // The splash's two programs, compiled once with shaderc (Vulkan 1.1, optimised for size)
        // from this GLSL:
        //
        //   #version 450
        //   layout(location = 0) in vec2 pos;
        //   layout(location = 1) in vec3 uva;
        //   layout(push_constant) uniform Size { vec2 size; };
        //   layout(location = 0) out vec3 v;
        //   void main() { v = uva; gl_Position = vec4(pos.x / size.x * 2.0 - 1.0, pos.y / size.y * 2.0 - 1.0, 0.0, 1.0); }
        //
        //   #version 450
        //   layout(location = 0) in vec3 v;
        //   layout(set = 0, binding = 0) uniform sampler2D atlas;
        //   layout(location = 0) out vec4 color;
        //   void main() { vec4 c = texture(atlas, v.xy); color = c * v.z; }
        const string VertexSpirv =
            "AwIjBwADAQALAA0AMwAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACQAAAAAABAAAAG1haW4AAAAACQAAAAsAAAATAAAAGAAAAEcABAAJAAAAHgAAAAAAAABHAAQACwAAAB4AAAABAAAARwADABEAAAACAAAASAAFABEAAAAAAAAACwAAAAAAAABIAAUAEQAAAAEAAAALAAAAAQAAAEgABQARAAAAAgAAAAsAAAADAAAASAAFABEAAAADAAAACwAAAAQAAABHAAQAGAAAAB4AAAAAAAAARwADAB0AAAACAAAASAAFAB0AAAAAAAAAIwAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAMAAAAgAAQACAAAAAMAAAAHAAAAOwAEAAgAAAAJAAAAAwAAACAABAAKAAAAAQAAAAcAAAA7AAQACgAAAAsAAAABAAAAFwAEAA0AAAAGAAAABAAAABUABAAOAAAAIAAAAAAAAAArAAQADgAAAA8AAAABAAAAHAAEABAAAAAGAAAADwAAAB4ABgARAAAADQAAAAYAAAAQAAAAEAAAACAABAASAAAAAwAAABEAAAA7AAQAEgAAABMAAAADAAAAFQAEABQAAAAgAAAAAQAAACsABAAUAAAAFQAAAAAAAAAXAAQAFgAAAAYAAAACAAAAIAAEABcAAAABAAAAFgAAADsABAAXAAAAGAAAAAEAAAArAAQADgAAABkAAAAAAAAAIAAEABoAAAABAAAABgAAAB4AAwAdAAAAFgAAACAABAAeAAAACQAAAB0AAAA7AAQAHgAAAB8AAAAJAAAAIAAEACAAAAAJAAAABgAAACsABAAGAAAAJAAAAAAAAEArAAQABgAAACYAAAAAAIA/KwAEAAYAAAAvAAAAAAAAACAABAAxAAAAAwAAAA0AAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAPQAEAAcAAAAMAAAACwAAAD4AAwAJAAAADAAAAEEABQAaAAAAGwAAABgAAAAZAAAAPQAEAAYAAAAcAAAAGwAAAEEABgAgAAAAIQAAAB8AAAAVAAAAGQAAAD0ABAAGAAAAIgAAACEAAACIAAUABgAAACMAAAAcAAAAIgAAAIUABQAGAAAAJQAAACMAAAAkAAAAgwAFAAYAAAAnAAAAJQAAACYAAABBAAUAGgAAACgAAAAYAAAADwAAAD0ABAAGAAAAKQAAACgAAABBAAYAIAAAACoAAAAfAAAAFQAAAA8AAAA9AAQABgAAACsAAAAqAAAAiAAFAAYAAAAsAAAAKQAAACsAAACFAAUABgAAAC0AAAAsAAAAJAAAAIMABQAGAAAALgAAAC0AAAAmAAAAUAAHAA0AAAAwAAAAJwAAAC4AAAAvAAAAJgAAAEEABQAxAAAAMgAAABMAAAAVAAAAPgADADIAAAAwAAAA/QABADgAAQA=";
        const string FragmentSpirv =
            "AwIjBwADAQALAA0AHwAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAAEQAAABcAAAAQAAMABAAAAAcAAABHAAQADQAAACEAAAAAAAAARwAEAA0AAAAiAAAAAAAAAEcABAARAAAAHgAAAAAAAABHAAQAFwAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAGQAJAAoAAAAGAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAGwADAAsAAAAKAAAAIAAEAAwAAAAAAAAACwAAADsABAAMAAAADQAAAAAAAAAXAAQADwAAAAYAAAADAAAAIAAEABAAAAABAAAADwAAADsABAAQAAAAEQAAAAEAAAAXAAQAEgAAAAYAAAACAAAAIAAEABYAAAADAAAABwAAADsABAAWAAAAFwAAAAMAAAAVAAQAGQAAACAAAAAAAAAAKwAEABkAAAAaAAAAAgAAACAABAAbAAAAAQAAAAYAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAPQAEAAsAAAAOAAAADQAAAD0ABAAPAAAAEwAAABEAAABPAAcAEgAAABQAAAATAAAAEwAAAAAAAAABAAAAVwAFAAcAAAAVAAAADgAAABQAAABBAAUAGwAAABwAAAARAAAAGgAAAD0ABAAGAAAAHQAAABwAAACOAAUABwAAAB4AAAAVAAAAHQAAAD4AAwAXAAAAHgAAAP0AAQA4AAEA";

        public VkSplash(VkContext vk, VkPrograms programs, Task<SplashArt> art)
        {
            this.vk = vk;
            rain = new SplashRain(art);
            vertexShader = programs.Module(Convert.FromBase64String(VertexSpirv));
            fragmentShader = programs.Module(Convert.FromBase64String(FragmentSpirv));
        }

        public bool Active => rain.Active;

        public void GameFrame() => rain.GameFrame();

        bool Ready()
        {
            if (atlas != null) return true;
            var a = rain.Art;
            if (a == null) return false;
            try
            {
                Upload(a);
            }
            catch (VkResultError e)
            {
                rain.Fail($"atlas upload failed: {e.Message}");
                return false;
            }
            rain.Start();
            return true;
        }

        /// The atlas with its mips, uploaded once and waited for (presenter thread).
        void Upload(SplashArt a)
        {
            int levels = TextureFormats.FullMipCount(a.Width, a.Height);
            atlas = VkImageResource.Create(vk, VkFormat.R8G8B8A8UNorm, VkImageViewType._2D, a.Width, a.Height, 1, levels, 1,
                VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit, name: "splash atlas", kind: "splash");
            var texels = SplashRain.PremultipliedTexels(a);
            var staging = VkBufferAlloc.Create(vk, texels.Length, VkBufferUsageFlags.TransferSrcBit, hostWrite: true);
            var pool = vk.Device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = vk.QueueFamily, Flags = VkCommandPoolCreateFlags.TransientBit }, null);
            var fence = vk.Device.CreateFence(new VkFenceCreateInfo(), null);
            try
            {
                texels.CopyTo(new Span<byte>(staging.Mapped, texels.Length));
                var cb = Brutal.VulkanApi.Abstractions.DeviceExtensions.AllocateCommandBuffer(vk.Device, new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary });
                var begin = new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
                VkCheck.Ok(vk.Device.PInvoke.vkBeginCommandBuffer.Invoke(cb.Handle, &begin), "vkBeginCommandBuffer (splash)");
                atlas.ForTransferWrite(cb);
                var region = new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageExtent = new VkExtent3D(a.Width, a.Height, 1),
                };
                cb.CopyBufferToImage(staging.Buffer, atlas.Image, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkBufferImageCopy>(&region, 1));
                VkTextureWork.GenerateMips(cb, atlas);
                atlas.ForSampling(cb);
                VkCheck.Ok(vk.Device.PInvoke.vkEndCommandBuffer.Invoke(cb.Handle), "vkEndCommandBuffer (splash)");
                var info = new VkCommandBufferSubmitInfo { CommandBuffer = cb.Handle };
                var submit = new VkSubmitInfo2 { CommandBufferInfoCount = 1, CommandBufferInfos = &info };
                vk.Queue.Submit2(new Span<VkSubmitInfo2>(&submit, 1), fence);
                VkCheck.Ok(VkFenceWait.Wait(vk, fence, "splash: the atlas upload"), "vkWaitForFences (splash)");
            }
            finally
            {
                vk.Device.DestroyFence(fence, null);
                vk.Device.DestroyCommandPool(pool, null);
                staging.Destroy(vk);
            }
            sampler = vk.Device.CreateSampler(new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear, MinFilter = VkFilter.Linear, MipmapMode = VkSamplerMipmapMode.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToEdge, AddressModeV = VkSamplerAddressMode.ClampToEdge, AddressModeW = VkSamplerAddressMode.ClampToEdge,
                MaxLod = levels,
            }, null);
            var binding = new VkDescriptorSetLayoutBinding
            {
                Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit,
            };
            setLayout = vk.Device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
            var size = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 };
            descriptorPool = vk.Device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size }, null);
            var sl = setLayout;
            var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = descriptorPool, DescriptorSetCount = 1, SetLayouts = &sl };
            VkDescriptorSet s;
            VkCheck.Ok(vk.Device.PInvoke.vkAllocateDescriptorSets.Invoke(vk.Device.Handle, &allocate, &s), "vkAllocateDescriptorSets (splash)");
            set = s;
            var image = new VkDescriptorImageInfo { Sampler = sampler, ImageView = atlas.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            var write = new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image };
            vk.Device.PInvoke.vkUpdateDescriptorSets.Invoke(vk.Device.Handle, 1, &write, 0, null);
            var range = new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit, Size = (Brutal.ByteSize32)8 };
            layout = vk.Device.CreatePipelineLayout(new VkPipelineLayoutCreateInfo { SetLayoutCount = 1, SetLayouts = &sl, PushConstantRangeCount = 1, PushConstantRanges = &range }, null);
        }

        VkPipeline Pipeline(VkFormat format)
        {
            if (!pipeline.IsNull() && pipelineFormat == format) return pipeline;
            if (!pipeline.IsNull()) vk.Device.DestroyPipeline(pipeline, null);
            var bindings = new VkVertexInputBindingDescription { Binding = 0, Stride = (Brutal.ByteSize32)20, InputRate = VkVertexInputRate.Vertex };
            var attributes = stackalloc VkVertexInputAttributeDescription[2];
            attributes[0] = new VkVertexInputAttributeDescription { Location = 0, Binding = 0, Format = VkFormat.R32G32SFloat, Offset = (Brutal.ByteSize32)0 };
            attributes[1] = new VkVertexInputAttributeDescription { Location = 1, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = (Brutal.ByteSize32)8 };
            var vertexInput = new VkPipelineVertexInputStateCreateInfo { VertexBindingDescriptionCount = 1, VertexBindingDescriptions = &bindings, VertexAttributeDescriptionCount = 2, VertexAttributeDescriptions = attributes };
            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
            var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
            var raster = new VkPipelineRasterizationStateCreateInfo { PolygonMode = VkPolygonMode.Fill, CullMode = VkCullModeFlags.None, LineWidth = 1f };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
            var depth = new VkPipelineDepthStencilStateCreateInfo();
            var blend = new VkPipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = VkBlendFactor.One, DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha, ColorBlendOp = VkBlendOp.Add,
                SrcAlphaBlendFactor = VkBlendFactor.One, DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha, AlphaBlendOp = VkBlendOp.Add,
                ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
            };
            var colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blend };
            var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };
            var rendering = new VkPipelineRenderingCreateInfo { ColorAttachmentCount = 1, ColorAttachmentFormats = &format };
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            fixed (byte* main = "main\0"u8)
            {
                stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.VertexBit, Module = vertexShader, Name = main };
                stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.FragmentBit, Module = fragmentShader, Name = main };
                var info = new VkGraphicsPipelineCreateInfo
                {
                    Next = &rendering, StageCount = 2, Stages = stages, VertexInputState = &vertexInput, InputAssemblyState = &inputAssembly,
                    ViewportState = &viewport, RasterizationState = &raster, MultisampleState = &multisample, DepthStencilState = &depth,
                    ColorBlendState = &colorBlend, DynamicState = &dynamic, Layout = layout,
                };
                VkPipeline p;
                VkCheck.Ok(vk.Device.PInvoke.vkCreateGraphicsPipelines.Invoke(vk.Device.Handle, default, 1, &info, null, &p), "vkCreateGraphicsPipelines (splash)");
                pipeline = p;
            }
            pipelineFormat = format;
            return pipeline;
        }

        /// Draws into a swapchain image in colour-attachment layout (presenter thread). `slot`
        /// is the presenter's frame in flight, whose vertex buffer it may overwrite.
        public void Draw(CommandBuffer cb, VkImageView target, VkFormat format, int width, int height, int slot)
        {
            if (!Ready() || !Active || width <= 0 || height <= 0) return;
            float fade = rain.Vertices(width, height, vertices);
            SplashRain.Bar(width, height, fade, bar);
            var color = new VkRenderingAttachmentInfo
            {
                ImageView = target, ImageLayout = VkImageLayout.ColorAttachmentOptimal, LoadOp = VkAttachmentLoadOp.Load, StoreOp = VkAttachmentStoreOp.Store,
            };
            cb.BeginRendering(new VkRenderingInfo { RenderArea = new VkRect2D(width, height), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &color });
            foreach (var r in bar)
            {
                // The bar's rectangles are in OpenGL's window coordinates (bottom left).
                int x = Math.Max(0, r.X), y = Math.Max(0, height - (r.Y + r.Height));
                int w = Math.Min(width - x, r.Width), h = Math.Min(height - y, r.Height);
                if (w <= 0 || h <= 0) continue;
                var clear = new VkClearAttachment { AspectMask = VkImageAspectFlags.ColorBit, ColorAttachment = 0 };
                clear.ClearValue.Color.Float32 = new Brutal.Numerics.float4(r.R, r.G, r.B, 1f);
                var rect = new VkClearRect { Rect = new VkRect2D { Offset = new VkOffset2D(x, y), Extent = new VkExtent2D(w, h) }, LayerCount = 1 };
                cb.ClearAttachments(new ReadOnlySpan<VkClearAttachment>(&clear, 1), new ReadOnlySpan<VkClearRect>(&rect, 1));
            }
            if (vertices.Count > 0)
            {
                long bytes = vertices.Count * 4L;
                var vb = vertexBuffers[slot];
                if (vb == null || vb.Size < bytes)
                {
                    vb?.Destroy(vk);
                    vb = vertexBuffers[slot] = VkBufferAlloc.Create(vk, Math.Max(bytes, 64 << 10), VkBufferUsageFlags.VertexBufferBit, hostWrite: true);
                }
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices).CopyTo(new Span<float>(vb.Mapped, vertices.Count));
                cb.BindPipeline(VkPipelineBindPoint.Graphics, Pipeline(format));
                var viewport = new VkViewport { Width = width, Height = height, MaxDepth = 1f };
                cb.SetViewport(0, new ReadOnlySpan<VkViewport>(&viewport, 1));
                var scissor = new VkRect2D(width, height);
                cb.SetScissor(0, new ReadOnlySpan<VkRect2D>(&scissor, 1));
                var s = set;
                vk.Device.PInvoke.vkCmdBindDescriptorSets.Invoke(cb.Handle, VkPipelineBindPoint.Graphics, layout, 0, 1, &s, 0, null);
                var size = stackalloc float[] { width, height };
                vk.Device.PInvoke.vkCmdPushConstants.Invoke(cb.Handle, layout, VkShaderStageFlags.VertexBit, 0, 8, size);
                var buffer = vb.Buffer;
                ulong offset = 0;
                vk.Device.PInvoke.vkCmdBindVertexBuffers.Invoke(cb.Handle, 0, 1, &buffer, (Brutal.ByteSize64*)&offset);
                cb.Draw(vertices.Count / 5, 1, 0, 0);
            }
            cb.EndRendering();
        }

        bool disposed;

        /// Releases its objects; safe to call again.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var vb in vertexBuffers) vb?.Destroy(vk);
            if (!pipeline.IsNull()) vk.Device.DestroyPipeline(pipeline, null);
            if (!layout.IsNull()) vk.Device.DestroyPipelineLayout(layout, null);
            if (!descriptorPool.IsNull()) vk.Device.DestroyDescriptorPool(descriptorPool, null);
            if (!setLayout.IsNull()) vk.Device.DestroyDescriptorSetLayout(setLayout, null);
            if (!sampler.IsNull()) vk.Device.DestroySampler(sampler, null);
            atlas?.Destroy(vk);
            if (!vertexShader.IsNull()) vk.Device.DestroyShaderModule(vertexShader, null);
            if (!fragmentShader.IsNull()) vk.Device.DestroyShaderModule(fragmentShader, null);
        }
    }
}
