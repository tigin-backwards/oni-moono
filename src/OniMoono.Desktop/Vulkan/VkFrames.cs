using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

using OniMoono.Desktop.Gl;

namespace OniMoono.Desktop.Vulkan
{
    /// A buffer and its VMA allocation; `Mapped` is its host address when host visible.
    internal sealed unsafe class VkBufferAlloc
    {
        public VkBuffer Buffer;
        public VmaAllocation Allocation;
        public byte* Mapped;
        public long Size;

        /// `hostWrite`: written by the CPU (mapped; sequential writes); `deviceLocalPreferred`:
        /// VMA may pick device memory that the CPU cannot map (the caller then copies into it).
        /// The ledger kind of its allocation and the allocation's size (VideoMemoryLedger).
        string kind;
        long bytes;

        /// Moves the allocation to another kind in the ledger (a mesh's buffer kept as a spare).
        public void Relabel(string to)
        {
            if (Buffer.IsNull() || to == kind) return;
            VideoMemoryLedger.Add(kind, -bytes, -1);
            kind = to;
            VideoMemoryLedger.Add(kind, bytes, 1);
        }

        public static VkBufferAlloc Create(VkContext vk, long size, VkBufferUsageFlags usage, bool hostWrite, bool allowTransferInstead = false, bool readback = false,
            bool preferHost = false, string kind = "other buffers")
        {
            var info = new VkBufferCreateInfo { Size = (Brutal.ByteSize64)size, Usage = usage, SharingMode = VkSharingMode.Exclusive };
            var flags = VmaAllocationCreateFlags.None;
            if (readback) flags = VmaAllocationCreateFlags.HostAccessRandomBit | VmaAllocationCreateFlags.MappedBit;
            else if (hostWrite) flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit
                                        | (allowTransferInstead ? VmaAllocationCreateFlags.HostAccessAllowTransferInsteadBit : 0);
            var alloc = new VmaAllocationCreateInfo { Usage = preferHost ? VmaMemoryUsage.AutoPreferHost : VmaMemoryUsage.Auto, Flags = flags };
            var result = vk.Allocator.CreateBuffer(info, alloc, out var buffer, out var allocation, out var allocationInfo);
            VkCheck.Ok(result, "vmaCreateBuffer");
            var made = new VkBufferAlloc { Buffer = buffer, Allocation = allocation, Mapped = (byte*)allocationInfo.PMappedData.Pointer, Size = size };
            made.kind = kind;
            made.bytes = (long)allocationInfo.Size.Value;
            VideoMemoryLedger.Add(kind, made.bytes, 1);
            bool first;
            lock (loggedUsages) first = loggedUsages.Add(usage);
            if (first)
            {
                var memory = vk.Physical.GetMemoryProperties();
                OniMoono.Engine.EngineLog.Engine($"vulkan: buffers for {usage} in memory type {allocationInfo.MemoryType} ({memory.MemoryTypes[(int)allocationInfo.MemoryType].PropertyFlags})");
            }
            return made;
        }

        /// Buffer usages whose memory type was logged (diagnostics).
        static readonly HashSet<VkBufferUsageFlags> loggedUsages = new();

        /// Host writes made visible to the device (a no-op on coherent memory).
        public void Flush(VkContext vk, long size = -1) => VkCheck.Ok(vk.Allocator.FlushAllocation(Allocation, default, new Brutal.ByteSize((nuint)(size < 0 ? Size : size))), "vmaFlushAllocation");

        /// Device writes made visible to the host (a no-op on coherent memory).
        public void Invalidate(VkContext vk) => VkCheck.Ok(vk.Allocator.InvalidateAllocation(Allocation, default, new Brutal.ByteSize((nuint)Size)), "vmaInvalidateAllocation");

        public void Destroy(VkContext vk)
        {
            if (Buffer.IsNull()) return;
            vk.Allocator.DestroyBuffer(Buffer, Allocation);
            Buffer = default;
            VideoMemoryLedger.Add(kind, -bytes, -1);
        }
    }

    /// The device thread's frames in flight (two): each has its command buffers, a fence
    /// signalled when the GPU has executed it, a host-visible stream for the frame's transient
    /// data (uniform blocks, staging copies, immediate geometry), descriptor pools, and the
    /// resources released while it was recorded, destroyed once its fence has signalled. A frame
    /// is recorded in batches: the work recorded since the last submission, submitted when the
    /// main thread needs a result (a readback) or at present. Each batch has two command
    /// buffers, submitted in order: uploads into resources the batch has not used yet (fresh
    /// meshes, new or untouched textures) and the batch's rendering, so that an upload in the
    /// middle of rendering does not interrupt it.
    internal sealed unsafe class VkFrames : IDisposable
    {
        public const int InFlight = 2;
        const long ChunkSize = 8L << 20;

        sealed class Chunk
        {
            public VkBufferAlloc Buffer;
            public long Used;
            /// Recycles of its frame slot in a row that did not use it.
            public int Idle;
        }

        /// Staging chunks a slot keeps however long they go unused (a frame's usual uploads),
        /// and how many recycles an extra chunk (a load's burst of uploads) survives unused.
        const int KeptChunks = 2, IdleRecycles = 8;

        sealed class Slot
        {
            public VkCommandPool Pool;
            public readonly List<CommandBuffer> Buffers = new();
            public int NextBuffer;
            public VkFence Fence;
            public bool Pending;
            public readonly List<Chunk> Chunks = new();
            public int ChunkIndex;
            /// Copy sources only (texels, staged meshes), in host memory (HostStaging).
            public readonly List<Chunk> Staging = new();
            public int StagingIndex;
            public readonly List<Chunk> Uniforms = new();
            public int UniformIndex;
            public readonly List<Action> Retired = new();
            public readonly List<VkDescriptorPool> DescriptorPools = new();
            public int DescriptorPoolIndex;
            public VkQueryPool Timestamps;
            public bool TimestampsWritten;
        }

        readonly VkContext vk;
        readonly Slot[] slots = new Slot[InFlight];
        int current;
        CommandBuffer main, uploads;
        bool mainOpen, uploadsOpen;
        readonly VkBufferUsageFlags streamUsage = VkBufferUsageFlags.TransferSrcBit | VkBufferUsageFlags.UniformBufferBit
            | VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.StorageBufferBit;
        readonly long uniformAlignment;
        readonly bool timestamps;
        readonly double timestampPeriodNs;

        /// The batch being recorded: textures used by it are updated in place in its rendering
        /// command buffer, others ahead of it.
        public long Batch { get; private set; }

        /// Batches submitted so far (diagnostics).
        public long Submits;

        /// GPU milliseconds of completed frames (between the first and the last command of each
        /// frame's batches), added up since the last take.
        public double GpuMilliseconds;
        public int GpuFrames;

        public VkFrames(VkContext vk)
        {
            this.vk = vk;
            uniformAlignment = Math.Max(16, (long)vk.Limits.MinUniformBufferOffsetAlignment.Value);
            timestamps = vk.Limits.TimestampComputeAndGraphics && vk.Limits.TimestampPeriod > 0;
            timestampPeriodNs = vk.Limits.TimestampPeriod;
            for (int i = 0; i < InFlight; i++)
            {
                var s = slots[i] = new Slot();
                s.Pool = vk.Device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = vk.QueueFamily, Flags = VkCommandPoolCreateFlags.TransientBit }, null);
                s.Fence = vk.Device.CreateFence(new VkFenceCreateInfo(), null);
                if (timestamps) s.Timestamps = vk.Device.CreateQueryPool(new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = 2 }, null);
            }
        }

        Slot Current => slots[current];

        /// The batch's rendering command buffer, begun on first use.
        public CommandBuffer Main
        {
            get
            {
                if (!mainOpen)
                {
                    main = Begin();
                    mainOpen = true;
                    var s = Current;
                    if (timestamps && !s.TimestampsWritten)
                    {
                        // The frame's first command: the start of its GPU time.
                        vk.Device.PInvoke.vkCmdResetQueryPool.Invoke(main.Handle, s.Timestamps, 0, 2);
                        vk.Device.PInvoke.vkCmdWriteTimestamp2.Invoke(main.Handle, VkPipelineStageFlags2.TopOfPipeBit, s.Timestamps, 0);
                        s.TimestampsWritten = true;
                    }
                }
                return main;
            }
        }

        public bool MainOpen => mainOpen;

        /// The batch's upload command buffer (executed before Main), begun on first use.
        public CommandBuffer Uploads
        {
            get
            {
                if (!uploadsOpen)
                {
                    uploads = Begin();
                    uploadsOpen = true;
                }
                return uploads;
            }
        }

        CommandBuffer Begin()
        {
            var s = Current;
            CommandBuffer cb;
            if (s.NextBuffer < s.Buffers.Count) cb = s.Buffers[s.NextBuffer];
            else
            {
                cb = Brutal.VulkanApi.Abstractions.DeviceExtensions.AllocateCommandBuffer(vk.Device,
                    new VkCommandBufferAllocateInfo { CommandPool = s.Pool, Level = VkCommandBufferLevel.Primary });
                s.Buffers.Add(cb);
            }
            s.NextBuffer++;
            var begin = new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
            VkCheck.Ok(vk.Device.PInvoke.vkBeginCommandBuffer.Invoke(cb.Handle, &begin), "vkBeginCommandBuffer");
            return cb;
        }

        /// Submits the batch (uploads, then rendering). `endFrame` signals the frame's fence and
        /// moves on to the next frame, waiting until the GPU has finished the frame that last
        /// used its resources.
        public void Submit(bool endFrame)
        {
            var s = Current;
            if (endFrame && timestamps && s.TimestampsWritten)
                vk.Device.PInvoke.vkCmdWriteTimestamp2.Invoke(Main.Handle, VkPipelineStageFlags2.BottomOfPipeBit, s.Timestamps, 1);
            Span<VkCommandBufferSubmitInfo> buffers = stackalloc VkCommandBufferSubmitInfo[2];
            int n = 0;
            if (uploadsOpen)
            {
                // Copies into fresh buffers (meshes) before the rendering reads them; images
                // have barriers of their own.
                var barrier = new VkMemoryBarrier2
                {
                    SrcStageMask = VkPipelineStageFlags2.AllTransferBit, SrcAccessMask = VkAccessFlags2.TransferWriteBit,
                    DstStageMask = VkPipelineStageFlags2.VertexInputBit | VkPipelineStageFlags2.IndexInputBit,
                    DstAccessMask = VkAccessFlags2.VertexAttributeReadBit | VkAccessFlags2.IndexReadBit,
                };
                uploads.PipelineBarrier2(new VkDependencyInfo { MemoryBarrierCount = 1, MemoryBarriers = &barrier });
                buffers[n++] = new VkCommandBufferSubmitInfo { CommandBuffer = End(uploads) };
            }
            if (mainOpen) buffers[n++] = new VkCommandBufferSubmitInfo { CommandBuffer = End(main) };
            uploadsOpen = mainOpen = false;
            if (n > 0 || endFrame)
            {
                fixed (VkCommandBufferSubmitInfo* b = buffers)
                {
                    var submit = new VkSubmitInfo2 { CommandBufferInfoCount = n, CommandBufferInfos = b };
                    vk.Queue.Submit2(new Span<VkSubmitInfo2>(&submit, 1), endFrame ? s.Fence : default);
                }
                Submits++;
            }
            Batch++;
            if (!endFrame) return;
            s.Pending = true;
            current = (current + 1) % InFlight;
            Recycle(Current);
        }

        VkCommandBuffer End(CommandBuffer cb)
        {
            VkCheck.Ok(vk.Device.PInvoke.vkEndCommandBuffer.Invoke(cb.Handle), "vkEndCommandBuffer");
            return cb.Handle;
        }

        /// Waits for a frame's GPU work, then reuses its resources.
        void Recycle(Slot s)
        {
            if (s.Pending)
            {
                var fence = s.Fence;
                VkCheck.Ok(vk.Device.PInvoke.vkWaitForFences.Invoke(vk.Device.Handle, 1, &fence, true, -1), "vkWaitForFences");
                vk.Device.PInvoke.vkResetFences.Invoke(vk.Device.Handle, 1, &fence);
                s.Pending = false;
                if (s.TimestampsWritten) CollectTimestamps(s);
            }
            s.TimestampsWritten = false;
            foreach (var release in s.Retired) release();
            s.Retired.Clear();
            vk.Device.PInvoke.vkResetCommandPool.Invoke(vk.Device.Handle, s.Pool, 0);
            s.NextBuffer = 0;
            RecycleChunks(s.Chunks);
            s.ChunkIndex = 0;
            RecycleChunks(s.Staging);
            s.StagingIndex = 0;
            Staged = 0;
            foreach (var c in s.Uniforms) c.Used = 0;
            s.UniformIndex = 0;
            foreach (var pool in s.DescriptorPools) vk.Device.PInvoke.vkResetDescriptorPool.Invoke(vk.Device.Handle, pool, 0);
            s.DescriptorPoolIndex = 0;
        }

        void CollectTimestamps(Slot s)
        {
            ulong* values = stackalloc ulong[2];
            var r = vk.Device.PInvoke.vkGetQueryPoolResults.Invoke(vk.Device.Handle, s.Timestamps, 0, 2, 16, values, (Brutal.ByteSize64)8, VkQueryResultFlags._64Bit);
            if (r != VkResult.Success) return;
            GpuMilliseconds += (values[1] - values[0]) * timestampPeriodNs / 1e6;
            GpuFrames++;
        }

        /// Releases `release` once the GPU has finished the frame being recorded (resources the
        /// recorded commands may still use).
        public void Retire(Action release) => Current.Retired.Add(release);

        void RecycleChunks(List<Chunk> chunks)
        {
            foreach (var c in chunks)
            {
                c.Idle = c.Used > 0 ? 0 : c.Idle + 1;
                c.Used = 0;
            }
            // A burst of uploads (a save loading) leaves chunks behind: past the first few, those
            // unused for a while are released, so the stream's memory follows what frames use.
            // Chunks made for one large copy go once the frame is over.
            for (int i = chunks.Count - 1; i >= 0; i--)
                if (i >= KeptChunks && chunks[i].Idle > IdleRecycles || chunks[i].Buffer.Size > ChunkSize)
                {
                    chunks[i].Buffer.Destroy(vk);
                    chunks.RemoveAt(i);
                }
        }

        /// Copies data into the frame's stream: the buffer, offset and address it went to.
        public (VkBuffer Buffer, long Offset, nint Pointer) Stream(long size, long alignment)
        {
            var s = Current;
            return Take(s.Chunks, ref s.ChunkIndex, size, alignment, streamUsage, preferHost: false, "stream chunks");
        }

        /// Copy sources in host memory (texels, meshes staged for a copy): on a GPU whose
        /// memory the CPU maps (resizable BAR) the stream is device memory written across the
        /// bus, which a copy source needs not be. ONI_VULKAN_STAGING=device: the stream.
        public static readonly bool HostStaging = Environment.GetEnvironmentVariable("ONI_VULKAN_STAGING") != "device";

        /// Room for a copy source (texels, a staged mesh) for this frame's commands.
        public (VkBuffer Buffer, long Offset, nint Pointer) Staging(long size, long alignment)
        {
            if (!HostStaging) return Stream(size, alignment);
            var s = Current;
            Staged += size;
            return Take(s.Staging, ref s.StagingIndex, size, alignment, VkBufferUsageFlags.TransferSrcBit, preferHost: true, "staging chunks");
        }

        /// Bytes staged since the frame began or the staging was last reused (ReuseStaging).
        public long Staged { get; private set; }

        /// The frame's staging chunks taken again from their start: the caller has submitted
        /// everything recorded and the GPU has finished it (as a readback waits for it), so no
        /// copy still reads them. Only the host staging (HostStaging): the stream also holds
        /// the frame's uniform blocks and geometry.
        public void ReuseStaging()
        {
            if (!HostStaging) return;
            var s = Current;
            foreach (var c in s.Staging) c.Used = 0;
            s.StagingIndex = 0;
            Staged = 0;
        }

        public (VkBuffer Buffer, long Offset) Staging(ReadOnlySpan<byte> data, long alignment)
        {
            var (buffer, offset, p) = Staging(data.Length, alignment);
            data.CopyTo(new Span<byte>((void*)p, data.Length));
            return (buffer, offset);
        }

        (VkBuffer Buffer, long Offset, nint Pointer) Take(List<Chunk> chunks, ref int index, long size, long alignment, VkBufferUsageFlags usage, bool preferHost, string kind)
        {
            while (true)
            {
                if (index >= chunks.Count)
                {
                    var buffer = VkBufferAlloc.Create(vk, Math.Max(ChunkSize, size), usage, hostWrite: true, preferHost: preferHost, kind: kind);
                    if (buffer.Mapped == null) throw new InvalidOperationException("the stream buffer is not host visible");
                    chunks.Add(new Chunk { Buffer = buffer });
                }
                var c = chunks[index];
                long at = (c.Used + alignment - 1) / alignment * alignment;
                if (at + size <= c.Buffer.Size)
                {
                    c.Used = at + size;
                    return (c.Buffer.Buffer, at, (nint)(c.Buffer.Mapped + at));
                }
                index++;
            }
        }

        const long UniformChunkSize = 4L << 20;

        /// Room for a draw's constant buffers, together in one buffer (a descriptor set of
        /// dynamic uniform buffers names one buffer for all of them): the buffer, offset and
        /// address. The frame's uniform chunks are kept for the device's life, so descriptor sets
        /// made for them stay valid.
        public (VkBuffer Buffer, long Offset, nint Pointer) UniformStream(long size)
        {
            if (size > UniformChunkSize) throw new ArgumentOutOfRangeException(nameof(size), size, "constant buffers larger than a uniform chunk");
            var s = Current;
            while (true)
            {
                if (s.UniformIndex >= s.Uniforms.Count)
                {
                    var buffer = VkBufferAlloc.Create(vk, UniformChunkSize, VkBufferUsageFlags.UniformBufferBit, hostWrite: true, kind: "uniform chunks");
                    if (buffer.Mapped == null) throw new InvalidOperationException("the uniform stream is not host visible");
                    s.Uniforms.Add(new Chunk { Buffer = buffer });
                }
                var c = s.Uniforms[s.UniformIndex];
                long at = (c.Used + uniformAlignment - 1) / uniformAlignment * uniformAlignment;
                if (at + size <= c.Buffer.Size)
                {
                    c.Used = at + size;
                    return (c.Buffer.Buffer, at, (nint)(c.Buffer.Mapped + at));
                }
                s.UniformIndex++;
            }
        }

        /// The largest range a dynamic uniform buffer descriptor of the uniform stream may name.
        public long UniformRange => UniformChunkSize;

        public (VkBuffer Buffer, long Offset) Stream(ReadOnlySpan<byte> data, long alignment)
        {
            var (buffer, offset, p) = Stream(data.Length, alignment);
            data.CopyTo(new Span<byte>((void*)p, data.Length));
            return (buffer, offset);
        }

        public long UniformAlignment => uniformAlignment;

        /// The stream's writes since the last flush made visible to the device.
        public void FlushStream()
        {
            var s = Current;
            for (int i = 0; i <= s.ChunkIndex && i < s.Chunks.Count; i++)
                if (s.Chunks[i].Used > 0) s.Chunks[i].Buffer.Flush(vk, s.Chunks[i].Used);
            for (int i = 0; i <= s.StagingIndex && i < s.Staging.Count; i++)
                if (s.Staging[i].Used > 0) s.Staging[i].Buffer.Flush(vk, s.Staging[i].Used);
            for (int i = 0; i <= s.UniformIndex && i < s.Uniforms.Count; i++)
                if (s.Uniforms[i].Used > 0) s.Uniforms[i].Buffer.Flush(vk, s.Uniforms[i].Used);
        }

        /// A descriptor set from the frame's pools (sets not pushed, or not kept).
        public VkDescriptorSet AllocateSet(VkDescriptorSetLayout layout)
        {
            var s = Current;
            while (true)
            {
                if (s.DescriptorPoolIndex >= s.DescriptorPools.Count)
                {
                    Span<VkDescriptorPoolSize> sizes =
                    [
                        new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 4096 },
                        new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 8192 },
                        new VkDescriptorPoolSize { Type = VkDescriptorType.SampledImage, DescriptorCount = 8192 },
                        new VkDescriptorPoolSize { Type = VkDescriptorType.Sampler, DescriptorCount = 8192 },
                        new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 256 },
                    ];
                    fixed (VkDescriptorPoolSize* p = sizes)
                        s.DescriptorPools.Add(vk.Device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = 2048, PoolSizeCount = sizes.Length, PoolSizes = p }, null));
                }
                var pool = s.DescriptorPools[s.DescriptorPoolIndex];
                VkDescriptorSet set = default;
                var info = new VkDescriptorSetAllocateInfo { DescriptorPool = pool, DescriptorSetCount = 1, SetLayouts = &layout };
                var r = vk.Device.PInvoke.vkAllocateDescriptorSets.Invoke(vk.Device.Handle, &info, &set);
                if (r == VkResult.Success) return set;
                if (r != VkResult.ErrorOutOfPoolMemory && r != VkResult.ErrorFragmentedPool) VkCheck.Ok(r, "vkAllocateDescriptorSets");
                s.DescriptorPoolIndex++;
            }
        }

        public void Dispose()
        {
            foreach (var s in slots)
            {
                if (s == null) continue;
                if (s.Pending)
                {
                    var fence = s.Fence;
                    vk.Device.PInvoke.vkWaitForFences.Invoke(vk.Device.Handle, 1, &fence, true, -1);
                    s.Pending = false;
                }
                foreach (var release in s.Retired) release();
                s.Retired.Clear();
                foreach (var c in s.Chunks) c.Buffer.Destroy(vk);
                foreach (var c in s.Staging) c.Buffer.Destroy(vk);
                foreach (var c in s.Uniforms) c.Buffer.Destroy(vk);
                foreach (var pool in s.DescriptorPools) vk.Device.DestroyDescriptorPool(pool, null);
                if (!s.Timestamps.IsNull()) vk.Device.DestroyQueryPool(s.Timestamps, null);
                vk.Device.DestroyFence(s.Fence, null);
                vk.Device.DestroyCommandPool(s.Pool, null);
            }
        }
    }
}
