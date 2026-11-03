using System;
using Brutal.VulkanApi;

namespace OniMoono.Desktop.Vulkan
{
    /// The texture a draw's set-0 binding samples (VkDevice's draws; tests' fakes): the
    /// program's texture at the slot's index, or the stand-in for one that cannot be sampled.
    /// Null for a binding that names no texture (a sampler slot without one).
    internal interface ITextureSlots
    {
        VkTexture Texture(VkBindingSlot slot);
    }

    /// A draw's set 0 as descriptor writes, and as the identities the descriptor cache finds a
    /// set by: per binding the image's serial and the sampler's handle, 0 where the binding's
    /// type names none. A storage buffer is always the device's one empty buffer and names
    /// neither; the program (the cache's owner) fixes the layout.
    internal static unsafe class VkTextureSet
    {
        static VkSampler SamplerOf(VkTexture vt, VkSampler fallback) => vt == null || vt.Sampler.IsNull() ? fallback : vt.Sampler;

        /// Two longs per slot (2n), without building any write.
        public static void Identities<T>(ref T source, VkBindingSlot[] slots, VkSampler defaultSampler, Span<long> ids) where T : struct, ITextureSlots
        {
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot.Type == VkDescriptorType.StorageBuffer)
                {
                    ids[2 * i] = ids[2 * i + 1] = 0;
                    continue;
                }
                var vt = source.Texture(slot);
                ids[2 * i] = slot.Type == VkDescriptorType.Sampler ? 0 : vt.Image.Serial;
                ids[2 * i + 1] = slot.Type == VkDescriptorType.SampledImage ? 0 : (long)SamplerOf(vt, defaultSampler).VkHandle;
            }
        }

        /// One write per slot into `writes` (with the buffer and image infos they point at),
        /// for the set `dst` (default for a push). With `ids` (2n) the identities too, in the
        /// same pass: the order of work before the cache was looked up first, kept for
        /// measuring against (VkDrawAlternation).
        public static void Writes<T>(ref T source, VkBindingSlot[] slots, VkSampler defaultSampler, in VkDescriptorBufferInfo storage, VkDescriptorSet dst,
                                     VkWriteDescriptorSet* writes, VkDescriptorBufferInfo* bufferInfos, VkDescriptorImageInfo* imageInfos, Span<long> ids = default) where T : struct, ITextureSlots
        {
            bool withIds = !ids.IsEmpty;
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                writes[i] = new VkWriteDescriptorSet { DstSet = dst, DstBinding = slot.Binding, DescriptorCount = 1, DescriptorType = slot.Type };
                if (slot.Type == VkDescriptorType.StorageBuffer)
                {
                    bufferInfos[i] = storage;
                    writes[i].BufferInfo = &bufferInfos[i];
                    if (withIds) ids[2 * i] = ids[2 * i + 1] = 0;
                    continue;
                }
                var vt = source.Texture(slot);
                var sampler = SamplerOf(vt, defaultSampler);
                imageInfos[i] = slot.Type switch
                {
                    VkDescriptorType.Sampler => new VkDescriptorImageInfo { Sampler = sampler },
                    VkDescriptorType.SampledImage => new VkDescriptorImageInfo { ImageView = vt.Image.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
                    _ => new VkDescriptorImageInfo { Sampler = sampler, ImageView = vt.Image.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
                };
                writes[i].ImageInfo = &imageInfos[i];
                if (withIds)
                {
                    ids[2 * i] = slot.Type == VkDescriptorType.Sampler ? 0 : vt.Image.Serial;
                    ids[2 * i + 1] = slot.Type == VkDescriptorType.SampledImage ? 0 : (long)sampler.VkHandle;
                }
            }
        }

        /// The bytes of one slot in a push template's data (TemplateEntries): an image or a
        /// buffer info, whichever is larger.
        public static readonly int InfoStride = Math.Max(sizeof(VkDescriptorImageInfo), sizeof(VkDescriptorBufferInfo));

        /// The program's set 0 as descriptor update template entries: slot i's info at
        /// i * InfoStride of the data, as Infos writes it.
        public static void TemplateEntries(VkBindingSlot[] slots, VkDescriptorUpdateTemplateEntry* entries)
        {
            for (int i = 0; i < slots.Length; i++)
                entries[i] = new VkDescriptorUpdateTemplateEntry
                {
                    DstBinding = slots[i].Binding, DstArrayElement = 0, DescriptorCount = 1, DescriptorType = slots[i].Type,
                    Offset = (nuint)(i * InfoStride), Stride = (nuint)InfoStride,
                };
        }

        /// One info per slot, at i * InfoStride of `data` (slots.Length * InfoStride bytes), for
        /// a push with the program's template: what Writes' infos hold, without the writes.
        public static void Infos<T>(ref T source, VkBindingSlot[] slots, VkSampler defaultSampler, in VkDescriptorBufferInfo storage, byte* data) where T : struct, ITextureSlots
        {
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                byte* at = data + i * InfoStride;
                if (slot.Type == VkDescriptorType.StorageBuffer)
                {
                    *(VkDescriptorBufferInfo*)at = storage;
                    continue;
                }
                var vt = source.Texture(slot);
                *(VkDescriptorImageInfo*)at = slot.Type switch
                {
                    VkDescriptorType.Sampler => new VkDescriptorImageInfo { Sampler = SamplerOf(vt, defaultSampler) },
                    VkDescriptorType.SampledImage => new VkDescriptorImageInfo { ImageView = vt.Image.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
                    _ => new VkDescriptorImageInfo { Sampler = SamplerOf(vt, defaultSampler), ImageView = vt.Image.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
                };
            }
        }
    }
}
