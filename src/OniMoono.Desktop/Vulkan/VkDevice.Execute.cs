using System;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using UnityEngine;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Vulkan
{
    /// The device thread's rendering: render targets as dynamic rendering passes, begun when a
    /// draw or clear needs one and ended when the target changes, a copy must run outside it
    /// (an upload into a texture this batch already used, a resolve, mips, a readback) or the
    /// frame ends; draws as pipelines, pushed (or per-frame) descriptor sets and vertex
    /// bindings, with Unity's Vulkan conventions (RenderConventions): the back buffer is stored
    /// top row first, as the window shows it, so viewport and scissor rectangles (given from
    /// the bottom left, as OpenGL's) and readbacks are turned over there; render textures are
    /// rendered flipped, so their rows keep OpenGL's order and their rectangles OpenGL's
    /// origin. Depth is reversed: cleared to 1 - the value asked for.
    internal sealed unsafe partial class VkDevice
    {
        BoundTarget target = new(null, null, 1, 1);
        bool rendering;
        VkPipeline boundPipeline;
        VkTargetFormats targetFormats;
        int renderWidth = 1, renderHeight = 1;
        bool toBackBuffer;
        UnityEngine.Rect viewport = new(0, 0, 1, 1);
        Rect? scissor;
        long draws, pipelineBinds, renderPasses, uploadBreaks;
        bool feedbackReported;

        // ----- Targets -----

        void SetTarget(BoundTarget bound)
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            EndRendering();
            target = bound;
            VkTimings.Targets += System.Diagnostics.Stopwatch.GetTimestamp() - t;
        }

        void EndRendering()
        {
            if (!rendering) return;
            frames.Main.EndRendering();
            rendering = false;
        }

        /// The rendering command buffer outside any pass, for a copy that must come at this
        /// point of the frame.
        CommandBuffer OutsideRendering()
        {
            if (rendering)
            {
                EndRendering();
                uploadBreaks++;
            }
            return frames.Main;
        }

        /// Begins a pass on the current target: its colours (null entries stay empty) and
        /// depth-stencil, combined as GlDevice.BindTarget combines them, loaded or cleared.
        void BeginRendering(Color? clearColor = null, float? clearDepth = null)
        {
            var cb = frames.Main;
            var t = target;
            Span<VkRenderingAttachmentInfo> colors = stackalloc VkRenderingAttachmentInfo[8];
            Span<VkFormat> formats = stackalloc VkFormat[8];
            int count = 0;
            VkImageResource depth = null;
            int width = int.MaxValue, height = int.MaxValue, samples = 1;
            void Size(VkImageResource image)
            {
                width = Math.Min(width, image.Width);
                height = Math.Min(height, image.Height);
                samples = image.Samples;
            }
            toBackBuffer = t.Colors == null;
            if (toBackBuffer)
            {
                var bb = presenter.BackBuffer(t.BackBufferWidth, t.BackBufferHeight, frames);
                bb.Color.ForAttachment(cb);
                colors[0] = Attachment(bb.Color, clearColor);
                formats[0] = bb.Color.Format;
                count = 1;
                depth = bb.Depth;
                Size(bb.Color);
            }
            else
            {
                VkTexture depthTarget;
                if (t.Colors.Length == 1 && (t.Depth == null || t.Depth == t.Colors[0])) depthTarget = t.Colors[0]?.Vk;
                else depthTarget = t.Depth?.Vk;
                bool depthOnly = t.Colors.Length == 1 && t.Colors[0]?.Vk is { DepthOnly: true };
                if (!depthOnly)
                    for (int i = 0; i < t.Colors.Length && i < 8; i++)
                    {
                        var vt = t.Colors[i]?.Vk;
                        var image = vt?.RenderImage;
                        if (image == null || vt.DepthOnly)
                        {
                            colors[count] = new VkRenderingAttachmentInfo { ImageLayout = VkImageLayout.ColorAttachmentOptimal };
                            formats[count++] = VkFormat.Undefined;
                            continue;
                        }
                        image.ForAttachment(cb);
                        vt.MultiTarget |= t.Colors.Length > 1;
                        vt.Dirty = true;
                        vt.LastUsedBatch = frames.Batch;
                        colors[count] = Attachment(image, clearColor);
                        formats[count++] = image.Format;
                        Size(image);
                    }
                if (depthTarget != null)
                {
                    depth = depthTarget.DepthOnly ? depthTarget.Image : depthTarget.DepthStencil;
                    if (depthTarget.DepthOnly)
                    {
                        depthTarget.Dirty = true;
                        depthTarget.LastUsedBatch = frames.Batch;
                    }
                }
            }
            VkRenderingAttachmentInfo depthInfo = default, stencilInfo = default;
            if (depth != null)
            {
                depth.ForAttachment(cb);
                Size(depth);
                var clear = new VkClearValue();
                clear.DepthStencil = new VkClearDepthStencilValue { Depth = clearDepth ?? 1f, Stencil = 0 };
                depthInfo = new VkRenderingAttachmentInfo
                {
                    ImageView = depth.AttachmentView, ImageLayout = VkImageLayout.DepthStencilAttachmentOptimal,
                    LoadOp = clearDepth != null ? VkAttachmentLoadOp.Clear : VkAttachmentLoadOp.Load, StoreOp = VkAttachmentStoreOp.Store, ClearValue = clear,
                };
                stencilInfo = depthInfo;
            }
            if (width == int.MaxValue) (width, height) = (1, 1);
            renderWidth = width;
            renderHeight = height;
            bool stencil = depth != null && VkImageResource.HasStencil(depth.Format);
            targetFormats = VkTargetFormats.Of(formats[..count], depth?.Format ?? VkFormat.Undefined, stencil ? depth.Format : VkFormat.Undefined, samples, flipped: !toBackBuffer);
            fixed (VkRenderingAttachmentInfo* c = colors)
            {
                var info = new VkRenderingInfo
                {
                    RenderArea = new VkRect2D(width, height), LayerCount = 1, ColorAttachmentCount = count, ColorAttachments = c,
                    DepthAttachment = depth != null ? &depthInfo : null, StencilAttachment = stencil ? &stencilInfo : null,
                };
                cb.BeginRendering(info);
            }
            rendering = true;
            boundPipeline = default;
            renderPasses++;
            ApplyViewport();
            ApplyScissor();
        }

        static VkRenderingAttachmentInfo Attachment(VkImageResource image, Color? clear)
        {
            var value = new VkClearValue();
            if (clear is { } c) value.Color.Float32 = new Brutal.Numerics.float4(c.r, c.g, c.b, c.a);
            return new VkRenderingAttachmentInfo
            {
                ImageView = image.AttachmentView, ImageLayout = VkImageLayout.ColorAttachmentOptimal,
                LoadOp = clear != null ? VkAttachmentLoadOp.Clear : VkAttachmentLoadOp.Load, StoreOp = VkAttachmentStoreOp.Store, ClearValue = value,
            };
        }

        void SetViewportNow(UnityEngine.Rect pixels)
        {
            viewport = pixels;
            if (rendering) ApplyViewport();
        }

        /// The viewport (from the bottom left): the same rectangle in a render texture, turned
        /// over in the back buffer.
        void ApplyViewport()
        {
            int h = Math.Max(1, (int)viewport.height);
            var v = new VkViewport
            {
                X = (int)viewport.x, Y = toBackBuffer ? renderHeight - (int)viewport.y - h : (int)viewport.y,
                Width = Math.Max(1, (int)viewport.width), Height = h, MaxDepth = 1f,
            };
            frames.Main.SetViewport(0, new ReadOnlySpan<VkViewport>(&v, 1));
        }

        bool ViewportEmpty => (int)viewport.width <= 0 || (int)viewport.height <= 0;

        void SetScissorNow(Rect? pixels)
        {
            scissor = pixels;
            if (rendering) ApplyScissor();
        }

        void ApplyScissor()
        {
            VkRect2D r;
            if (scissor is { } s)
            {
                int x0 = Math.Clamp((int)s.x, 0, renderWidth), y0 = Math.Clamp((int)s.y, 0, renderHeight);
                int x1 = Math.Clamp((int)s.x + Math.Max(0, (int)s.width), 0, renderWidth), y1 = Math.Clamp((int)s.y + Math.Max(0, (int)s.height), 0, renderHeight);
                if (toBackBuffer) (y0, y1) = (renderHeight - y1, renderHeight - y0);
                r = new VkRect2D { Offset = new VkOffset2D(x0, y0), Extent = new VkExtent2D(x1 - x0, y1 - y0) };
            }
            else r = new VkRect2D(renderWidth, renderHeight);
            frames.Main.SetScissor(0, new ReadOnlySpan<VkRect2D>(&r, 1));
        }

        /// Clears fill the whole target, whatever the scissor (as GlDevice.Clear).
        void ClearNow(bool color, bool depth, Color background, float depthValue)
        {
            if (deviceLost || !color && !depth) return;
            depthValue = 1f - depthValue; // reversed depth
            if (!rendering) BeginRendering(color ? background : null, depth ? depthValue : null);
            else
            {
                Span<VkClearAttachment> clears = stackalloc VkClearAttachment[9];
                int n = 0;
                if (color)
                    for (int i = 0; i < targetFormats.Count; i++)
                    {
                        if (targetFormats.Color(i) == VkFormat.Undefined) continue;
                        clears[n] = new VkClearAttachment { AspectMask = VkImageAspectFlags.ColorBit, ColorAttachment = i };
                        clears[n++].ClearValue.Color.Float32 = new Brutal.Numerics.float4(background.r, background.g, background.b, background.a);
                    }
                if (depth && targetFormats.Depth != VkFormat.Undefined)
                {
                    clears[n] = new VkClearAttachment { AspectMask = VkImageAspectFlags.DepthBit | (targetFormats.Stencil != VkFormat.Undefined ? VkImageAspectFlags.StencilBit : 0) };
                    clears[n++].ClearValue.DepthStencil = new VkClearDepthStencilValue { Depth = depthValue, Stencil = 0 };
                }
                var rect = new VkClearRect { Rect = new VkRect2D(renderWidth, renderHeight), LayerCount = 1 };
                if (n > 0) frames.Main.ClearAttachments(clears[..n], new ReadOnlySpan<VkClearRect>(&rect, 1));
            }
            if (color && toBackBuffer) presenter.Drew(clear: scissor == null);
        }

        // ----- Draws -----

        void RunDraw(ProgramInputs r)
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            var mesh = r.Mesh?.Vk;
            if (!deviceLost && r.Program.Vk is { } program && mesh?.Buffer != null && r.SubMesh.Count > 0 && !ViewportEmpty)
            {
                PrepareTextures(r);
                if (!rendering) BeginRendering();
                if (Bind(r, program, VkPipelines.LayoutKey(r.Layout), r.Layout, Topology(r.SubMesh.Mode)))
                {
                    var cb = frames.Main;
                    var inputs = program.Inputs;
                    Span<VkBuffer> buffers = stackalloc VkBuffer[inputs.Length];
                    Span<Brutal.ByteSize64> offsets = stackalloc Brutal.ByteSize64[inputs.Length];
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        int channel = inputs[i].Channel;
                        if (channel >= 0 && r.Layout.Components[channel] != 0)
                        {
                            buffers[i] = mesh.Buffer.Buffer;
                            offsets[i] = (Brutal.ByteSize64)mesh.ChannelOffsets[channel];
                        }
                        else
                        {
                            buffers[i] = constants.Buffer;
                            offsets[i] = (Brutal.ByteSize64)ConstantOffset(channel);
                        }
                    }
                    BindVertexBuffers(cb, inputs, buffers, offsets);
                    cb.BindIndexBuffer(mesh.Buffer.Buffer, (Brutal.ByteSize64)mesh.IndexOffset, VkIndexType.UInt32);
                    cb.DrawIndexed(r.SubMesh.Count, 1, r.SubMesh.Offset, r.SubMesh.BaseVertex, 0);
                    draws++;
                    if (toBackBuffer) presenter.Drew(clear: false);
                }
            }
            VkTimings.Draws += System.Diagnostics.Stopwatch.GetTimestamp() - t;
        }

        /// The constant an absent channel reads, as OpenGL's constant attributes (GlDevice
        /// SetAbsentChannels): white colour, position (0, 0, 0, 1), zero normal, tangent and
        /// texture coordinates; inputs no channel feeds OpenGL's initial (0, 0, 0, 1).
        static int ConstantOffset(int channel) => channel switch
        {
            (int)Channel.Color => 16,
            (int)Channel.Position => 32,
            < 0 => 32,
            _ => 0,
        };

        /// Binds each input's buffer at its binding (the input's index: VkPipelines.Create), all
        /// in one call.
        void BindVertexBuffers(CommandBuffer cb, (int Location, int Channel, char Kind)[] inputs, Span<VkBuffer> buffers, Span<Brutal.ByteSize64> offsets)
        {
            if (inputs.Length == 0) return;
            fixed (VkBuffer* b = buffers)
            fixed (Brutal.ByteSize64* o = offsets)
                vk.Device.PInvoke.vkCmdBindVertexBuffers.Invoke(cb.Handle, 0, inputs.Length, b, o);
        }

        static VkPrimitiveTopology Topology(uint mode) => mode switch
        {
            Lines => VkPrimitiveTopology.LineList,
            LineStrip => VkPrimitiveTopology.LineStrip,
            Points => VkPrimitiveTopology.PointList,
            TriangleStrip => VkPrimitiveTopology.TriangleStrip,
            _ => VkPrimitiveTopology.TriangleList,
        };

        /// Render textures a draw samples are made ready first (resolve, mips) and every
        /// sampled image is moved to its sampling layout; work that must not run inside a pass
        /// ends the current one.
        void PrepareTextures(ProgramInputs r)
        {
            bool outside = false;
            for (int i = 0; i < r.TextureCount; i++)
            {
                var vt = r.Textures[i].Texture.Vk;
                if (vt?.Image == null || IsAttached(vt)) continue;
                if (vt.RenderTarget && vt.Dirty && (vt.Multisampled != null || vt.Image.Levels > 1 && r.Textures[i].AutoGenerateMips)) outside = true;
                else if (vt.Image.Layout != VkImageLayout.ShaderReadOnlyOptimal) outside = true;
            }
            var cb = outside ? OutsideRendering() : frames.Main;
            for (int i = 0; i < r.TextureCount; i++)
            {
                var vt = r.Textures[i].Texture.Vk;
                if (vt?.Image == null || IsAttached(vt)) continue;
                vt.LastUsedBatch = frames.Batch;
                if (!outside) continue;
                if (vt.RenderTarget) PrepareForSampling(cb, vt, r.Textures[i].AutoGenerateMips);
                vt.Image.ForSampling(cb);
            }
        }

        /// The texture is (or is about to be, once the pass begins) an attachment of the
        /// current target.
        bool IsAttached(VkTexture vt)
        {
            var t = target;
            if (t.Colors == null) return false;
            bool hit = t.Depth?.Vk == vt;
            foreach (var c in t.Colors) hit |= c?.Vk == vt;
            if (!hit) return false;
            // A feedback loop, undefined on OpenGL: the draw reads no texture there.
            if (!feedbackReported)
            {
                feedbackReported = true;
                OniMoono.Engine.EngineLog.Engine("vulkan: a draw samples the texture it renders into; it reads no texture there (undefined on OpenGL)");
            }
            return true;
        }

        /// Pipeline and descriptors for a draw; false when the draw cannot be made (a pipeline
        /// the driver refused, logged once per program by VkPipelines).
        bool Bind(ProgramInputs r, VkProgram program, long layoutKey, MeshLayout layout, VkPrimitiveTopology topology)
        {
            var key = new VkPipelineKey(program, r.State, layoutKey, topology, targetFormats);
            var pipeline = pipelines.Get(key, layout);
            if (pipeline.IsNull()) return false;
            var cb = frames.Main;
            if (pipeline.VkHandle != boundPipeline.VkHandle)
            {
                cb.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
                boundPipeline = pipeline;
                pipelineBinds++;
            }
            var textureSet = BindTextures(r, program, cb);
            BindUniforms(r, program, cb, textureSet);
            return true;
        }

        /// A draw's set-0 textures: the program's texture at each slot, or the unbound stand-in
        /// for one without an image or rendered into by this pass.
        readonly struct DrawTextures(VkDevice device, ProgramInputs r) : ITextureSlots
        {
            public VkTexture Texture(VkBindingSlot slot)
            {
                if (slot.Index < 0) return null;
                var vt = r.Textures[slot.Index].Texture.Vk;
                if (vt?.Image == null || device.IsAttached(vt)) vt = device.DefaultTexture(Unbound, r.Program.Samplers[slot.Index].Target).Vk;
                return vt;
            }
        }

        /// Set 0: each binding's texture, its sampler, or an empty storage buffer; pushed, or
        /// a set kept by the descriptor cache, returned for BindUniforms to bind with set 1.
        VkDescriptorSet BindTextures(ProgramInputs r, VkProgram program, CommandBuffer cb)
        {
            var slots = program.Slots;
            int n = slots.Length;
            if (n == 0) return default;
            var source = new DrawTextures(this, r);
            var storage = new VkDescriptorBufferInfo { Buffer = emptyStorage.Buffer, Offset = default, Range = (Brutal.ByteSize64)emptyStorage.Size };
            if (program.Push)
            {
                var writes = stackalloc VkWriteDescriptorSet[n];
                var bufferInfos = stackalloc VkDescriptorBufferInfo[n];
                var imageInfos = stackalloc VkDescriptorImageInfo[n];
                VkTextureSet.Writes(ref source, slots, defaultSampler, storage, default, writes, bufferInfos, imageInfos);
                vk.Device.PInvoke.vkCmdPushDescriptorSetKHR.Invoke(cb.Handle, VkPipelineBindPoint.Graphics, program.Layout, 0, n, writes);
                return default;
            }
            // A set naming the same descriptors is bound again; else one is made and kept.
            var set = VkDrawAlternation.Before(VkDrawAlternation.WritesFirst)
                ? descriptorCache.SetForWritesFirst(program, ref source, defaultSampler, storage, presented)
                : descriptorCache.SetFor(program, ref source, defaultSampler, storage, presented);
            if (program.Uniforms.Length == 0) vk.Device.PInvoke.vkCmdBindDescriptorSets.Invoke(cb.Handle, VkPipelineBindPoint.Graphics, program.Layout, 0, 1, &set, 0, null);
            return set;
        }

        /// Set 1: the draw's constant buffers, written together to the uniform stream and
        /// bound by dynamic offsets into the program's set for that buffer; with set 0 in the
        /// same call when the descriptor cache gave one (`textureSet`).
        void BindUniforms(ProgramInputs r, VkProgram program, CommandBuffer cb, VkDescriptorSet textureSet)
        {
            var uniforms = program.Uniforms;
            int n = uniforms.Length;
            if (n == 0) return;
            long align = frames.UniformAlignment;
            Span<long> starts = stackalloc long[n];
            long total = 0;
            for (int i = 0; i < n; i++)
            {
                starts[i] = total;
                total += (uniforms[i].Size + align - 1) / align * align;
            }
            var (buffer, offset, pointer) = frames.UniformStream(total);
            var sizes = r.Program.BlockSizes;
            Span<int> blockStarts = stackalloc int[sizes.Length];
            for (int b = 0, at = r.BlocksOffset; b < sizes.Length; b++)
            {
                blockStarts[b] = at;
                at += Math.Max(sizes[b], 16);
            }
            for (int i = 0; i < n; i++)
                r.Blocks.AsSpan(blockStarts[uniforms[i].Block], uniforms[i].Size).CopyTo(new Span<byte>((byte*)pointer + starts[i], uniforms[i].Size));
            var sets = stackalloc VkDescriptorSet[2] { textureSet, default };
            bool both = !textureSet.IsNull();
            if (program.DynamicUniforms)
            {
                sets[1] = programs.UniformSet(program, buffer);
                var dynamicOffsets = stackalloc int[n];
                for (int i = 0; i < n; i++) dynamicOffsets[i] = (int)(offset + starts[i]);
                vk.Device.PInvoke.vkCmdBindDescriptorSets.Invoke(cb.Handle, VkPipelineBindPoint.Graphics, program.Layout, both ? 0 : 1, both ? 2 : 1, both ? sets : sets + 1, n, dynamicOffsets);
            }
            else
            {
                sets[1] = frames.AllocateSet(program.UniformSet);
                programs.Write(program, sets[1], buffer, offset, VkDescriptorType.UniformBuffer, starts);
                vk.Device.PInvoke.vkCmdBindDescriptorSets.Invoke(cb.Handle, VkPipelineBindPoint.Graphics, program.Layout, both ? 0 : 1, both ? 2 : 1, both ? sets : sets + 1, 0, null);
            }
        }

        void ExecuteImmediate(ProgramInputs recorded, ImmediatePrimitive primitive, float[] data, int count, int[] indices)
        {
            if (deviceLost || recorded.Program.Vk is not { } program || ViewportEmpty) return;
            PrepareTextures(recorded);
            if (!rendering) BeginRendering();
            var topology = indices != null ? VkPrimitiveTopology.TriangleList : primitive switch
            {
                ImmediatePrimitive.Lines => VkPrimitiveTopology.LineList,
                ImmediatePrimitive.LineStrip => VkPrimitiveTopology.LineStrip,
                ImmediatePrimitive.TriangleStrip => VkPrimitiveTopology.TriangleStrip,
                _ => VkPrimitiveTopology.TriangleList,
            };
            if (!Bind(recorded, program, VkPipelines.ImmediateLayout, null, topology)) return;
            var cb = frames.Main;
            var (vertices, at) = frames.Stream(MemoryMarshal.AsBytes(data.AsSpan()), 16);
            var inputs = program.Inputs;
            Span<VkBuffer> buffers = stackalloc VkBuffer[inputs.Length];
            Span<Brutal.ByteSize64> offsets = stackalloc Brutal.ByteSize64[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                int channel = inputs[i].Channel;
                bool constant = channel < 0 || channel == (int)Channel.Normal || channel == (int)Channel.Tangent;
                buffers[i] = constant ? constants.Buffer : vertices;
                offsets[i] = (Brutal.ByteSize64)(constant ? ConstantOffset(channel)
                    : at + (channel == (int)Channel.Position ? 0 : channel == (int)Channel.Color ? 12 : 28 + (channel - (int)Channel.Uv0) * 16));
            }
            BindVertexBuffers(cb, inputs, buffers, offsets);
            if (indices != null)
            {
                var (indexBuffer, indexAt) = frames.Stream(MemoryMarshal.AsBytes(indices.AsSpan()), 4);
                cb.BindIndexBuffer(indexBuffer, (Brutal.ByteSize64)indexAt, VkIndexType.UInt32);
                cb.DrawIndexed(indices.Length, 1, 0, 0, 0);
            }
            else cb.Draw(count, 1, 0, 0);
            draws++;
            if (toBackBuffer) presenter.Drew(clear: false);
        }

        // ----- Read-back and present -----

        /// Pixels of a render texture (or the back buffer) as OpenGL's ReadPixels(RGBA, FLOAT)
        /// gives them, rows from the bottom; texels outside the image read 0. Submits what was
        /// recorded and waits for the GPU.
        float[] ReadNow(DeviceTexture device, bool autoMips, int x, int y, int w, int h, int fbWidth, int fbHeight)
        {
            var read = new float[w * h * 4];
            if (deviceLost) return read;
            var cb = OutsideRendering();
            VkImageResource image;
            if (device != null)
            {
                var vt = device.Vk;
                if (vt?.Image == null || vt.DepthOnly) return read;
                PrepareForSampling(cb, vt, autoMips);
                image = vt.Image;
            }
            else image = presenter.ReadBuffer(fbWidth, fbHeight, frames).Color;
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(image.Width, x + w), y1 = Math.Min(image.Height, y + h);
            if (x1 <= x0 || y1 <= y0) return read;
            int texel = VkFormats.TexelBytes(image.Format);
            int rw = x1 - x0, rh = y1 - y0;
            // The back buffer is stored top row first: its rows are read turned over.
            bool turned = device == null;
            var buffer = VkBufferAlloc.Create(vk, (long)rw * rh * texel, VkBufferUsageFlags.TransferDstBit, hostWrite: false, readback: true);
            try
            {
                image.ForTransferRead(cb);
                var region = new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageOffset = new VkOffset3D(x0, turned ? image.Height - y1 : y0, 0),
                    ImageExtent = new VkExtent3D(rw, rh, 1),
                };
                vk.Device.PInvoke.vkCmdCopyImageToBuffer.Invoke(cb.Handle, image.Image, VkImageLayout.TransferSrcOptimal, buffer.Buffer, 1, &region);
                FlushStreams();
                frames.Submit(endFrame: false);
                vk.Queue.WaitIdle();
                buffer.Invalidate(vk);
                for (int row = 0; row < rh; row++)
                    for (int col = 0; col < rw; col++)
                    {
                        var c = VkFormats.ReadTexel(image.Format, buffer.Mapped + ((long)row * rw + col) * texel);
                        int glRow = turned ? y1 - 1 - row : y0 + row;
                        int o = ((glRow - y) * w + (x0 - x + col)) * 4;
                        read[o] = c.r;
                        read[o + 1] = c.g;
                        read[o + 2] = c.b;
                        read[o + 3] = c.a;
                    }
            }
            finally
            {
                buffer.Destroy(vk);
            }
            if (PresentTrace.Enabled && device == null)
            {
                double sum = 0;
                for (int i = 0; i < read.Length; i += 4) sum += 0.299 * read[i] + 0.587 * read[i + 1] + 0.114 * read[i + 2];
                PresentTrace.Log($"frame {Time.frameCount}: ReadPixels of the screen ({x}, {y}, {w}, {h}): mean luminance {255 * sum / (w * h):F1}");
            }
            return read;
        }

        void PresentNow(int frame, int interval, int width, int height, long started)
        {
            // The frame's texel buffers, copied into the stream by now.
            UploadBuffers.ReturnAll(returnedUploads);
            if (deviceLost) return;
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            EndRendering();
            presenter.Frame = frame;
            FlushStreams();
            frames.Submit(endFrame: true);
            if (frame == failPresentAt) presenter.FailNextRecreate = true;
            if (frame == presentOomAt) presenter.OutOfMemoryRecreates = 3;
            if (presenter.Failure is { } failed)
            {
                PresenterFailed(failed);
                return;
            }
            presenter.FrameStarted = started;
            presenter.Present(interval, width, height);
            presenter.Frame = frame + 1;
            Presented();
            descriptorCache.Sweep(presented);
            VkTimings.Present += System.Diagnostics.Stopwatch.GetTimestamp() - t;
        }

        /// The frame's stream writes made visible to the device (a no-op on coherent memory).
        void FlushStreams() => frames.FlushStream();
    }
}
