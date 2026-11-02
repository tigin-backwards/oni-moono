using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using OniMoono.Desktop.Vulkan;
using Xunit;

namespace OniMoono.Tests
{
    /// Set 0 of the programs the descriptor cache keeps (VkDescriptorCache.SetFor): a draw is
    /// looked up by its identities alone, writes are built only for a new set, and they are the
    /// writes the earlier order of work (writes and identities together, for every draw) built.
    public unsafe class VkTextureSetTests
    {
        /// The draw's textures by sampler index; counts the lookups.
        sealed class Textures
        {
            public VkTexture[] BySampler;
            public int Lookups;
        }

        readonly struct Source(Textures t) : ITextureSlots
        {
            public VkTexture Texture(VkBindingSlot slot)
            {
                t.Lookups++;
                return slot.Index < 0 ? null : t.BySampler[slot.Index];
            }
        }

        /// A written descriptor, with what its pointers name.
        readonly record struct Written(int Type, nint Set, int Binding, int Count, VkDescriptorType Kind,
                                       nint Sampler, nint View, VkImageLayout Layout, nint Buffer, Brutal.ByteSize64 Offset, Brutal.ByteSize64 Range);

        static Written Read(in VkWriteDescriptorSet w) => new(SType(w), (nint)w.DstSet.VkHandle, w.DstBinding, w.DescriptorCount, w.DescriptorType,
            w.ImageInfo == null ? 0 : (nint)w.ImageInfo->Sampler.VkHandle, w.ImageInfo == null ? 0 : (nint)w.ImageInfo->ImageView.VkHandle,
            w.ImageInfo == null ? 0 : w.ImageInfo->ImageLayout,
            w.BufferInfo == null ? 0 : (nint)w.BufferInfo->Buffer.VkHandle, w.BufferInfo == null ? default : w.BufferInfo->Offset, w.BufferInfo == null ? default : w.BufferInfo->Range);

        /// The structure's type, its first member (private in the bindings).
        static int SType(in VkWriteDescriptorSet w) => System.Runtime.CompilerServices.Unsafe.As<VkWriteDescriptorSet, int>(ref System.Runtime.CompilerServices.Unsafe.AsRef(in w));

        sealed class FakeSets : IDescriptorSets
        {
            long next = 1000;
            public readonly List<Written[]> Writes = new();
            public readonly List<nint> Freed = new();
            public int Allocated;

            public (VkDescriptorPool Pool, VkDescriptorSet Set) Allocate(VkDescriptorSetLayout layout)
            {
                Allocated++;
                return (new VkDescriptorPool((nint)7), new VkDescriptorSet((nint)next++));
            }

            public void Write(VkWriteDescriptorSet* writes, int count)
            {
                var copy = new Written[count];
                for (int i = 0; i < count; i++) copy[i] = Read(writes[i]);
                Writes.Add(copy);
            }

            public void Free(VkDescriptorPool pool, VkDescriptorSet set) => Freed.Add((nint)set.VkHandle);

            public void Dispose() { }
        }

        static readonly VkSampler DefaultSampler = new((nint)0x5A);
        static readonly VkDescriptorBufferInfo Storage = new() { Buffer = new VkBuffer((nint)0xB0), Offset = default, Range = (Brutal.ByteSize64)256 };
        static long viewHandles = 0x10000;

        static VkTexture Texture(nint sampler = 0) => new()
        {
            Image = new VkImageResource { View = new VkImageView((nint)viewHandles++) },
            Sampler = new VkSampler(sampler),
        };

        /// Set 0 as Klei's batched animation programs lay it out: separate images and samplers,
        /// a combined one, a sampler with no texture of its own, and an empty storage buffer.
        static VkProgram Program() => new()
        {
            Name = "test",
            TextureSet = new VkDescriptorSetLayout((nint)0x77),
            Slots =
            [
                new(0, VkDescriptorType.SampledImage, 0),
                new(1, VkDescriptorType.Sampler, 0),
                new(2, VkDescriptorType.SampledImage, 1),
                new(3, VkDescriptorType.Sampler, 1),
                new(4, VkDescriptorType.CombinedImageSampler, 2),
                new(5, VkDescriptorType.StorageBuffer, -1),
                new(6, VkDescriptorType.Sampler, -1),
                new(7, VkDescriptorType.SampledImage, 3),
            ],
        };

        static Textures Draw() => new() { BySampler = [Texture(0x51), Texture(), Texture(0x52), Texture(0x53)] };

        /// The loop BindTextures ran before the identities came first, verbatim but for its
        /// texture lookup: writes and identities for every draw.
        static (Written[] Writes, long[] Ids) Before(VkProgram program, Textures t, VkDescriptorSet set)
        {
            var slots = program.Slots;
            int n = slots.Length;
            var writes = stackalloc VkWriteDescriptorSet[n];
            var bufferInfos = stackalloc VkDescriptorBufferInfo[n];
            var imageInfos = stackalloc VkDescriptorImageInfo[n];
            var ids = new long[2 * n];
            for (int i = 0; i < n; i++)
            {
                var slot = slots[i];
                writes[i] = new VkWriteDescriptorSet { DstBinding = slot.Binding, DescriptorCount = 1, DescriptorType = slot.Type };
                if (slot.Type == VkDescriptorType.StorageBuffer)
                {
                    bufferInfos[i] = new VkDescriptorBufferInfo { Buffer = Storage.Buffer, Offset = default, Range = Storage.Range };
                    writes[i].BufferInfo = &bufferInfos[i];
                    ids[2 * i] = ids[2 * i + 1] = 0;
                    continue;
                }
                VkTexture vt = slot.Index >= 0 ? t.BySampler[slot.Index] : null;
                var sampler = vt == null || vt.Sampler.IsNull() ? DefaultSampler : vt.Sampler;
                imageInfos[i] = slot.Type switch
                {
                    VkDescriptorType.Sampler => new VkDescriptorImageInfo { Sampler = sampler },
                    VkDescriptorType.SampledImage => new VkDescriptorImageInfo { ImageView = vt.Image.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
                    _ => new VkDescriptorImageInfo { Sampler = sampler, ImageView = vt.Image.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
                };
                writes[i].ImageInfo = &imageInfos[i];
                ids[2 * i] = slot.Type == VkDescriptorType.Sampler ? 0 : vt.Image.Serial;
                ids[2 * i + 1] = slot.Type == VkDescriptorType.SampledImage ? 0 : (long)sampler.VkHandle;
            }
            for (int i = 0; i < n; i++) writes[i].DstSet = set;
            var read = new Written[n];
            for (int i = 0; i < n; i++) read[i] = Read(writes[i]);
            return (read, ids);
        }

        static VkDescriptorSet SetFor(VkDescriptorCache cache, VkProgram program, Textures t, long frame)
        {
            var source = new Source(t);
            return cache.SetFor(program, ref source, DefaultSampler, Storage, frame);
        }

        [Fact]
        public void AFoundSetBuildsNoWrites()
        {
            var sets = new FakeSets();
            var cache = new VkDescriptorCache(sets);
            var program = Program();
            var t = Draw();
            var first = SetFor(cache, program, t, 0);
            Assert.Equal(1, sets.Allocated);
            Assert.Single(sets.Writes);
            // The lookup resolves each texture slot once; the miss resolves them again to write.
            int textureSlots = program.Slots.Count(s => s.Type != VkDescriptorType.StorageBuffer);
            Assert.Equal(2 * textureSlots, t.Lookups);
            t.Lookups = 0;
            for (int frame = 1; frame < 5; frame++) Assert.Equal(first.VkHandle, SetFor(cache, program, t, frame).VkHandle);
            Assert.Equal(1, sets.Allocated);
            Assert.Single(sets.Writes);
            Assert.Equal(4 * textureSlots, t.Lookups);
            Assert.Equal(1, cache.Made);
            Assert.Equal(4, cache.Found);
        }

        [Fact]
        public void ANewSetIsWrittenAsBefore()
        {
            var program = Program();
            var t = Draw();
            var sets = new FakeSets();
            var set = SetFor(new VkDescriptorCache(sets), program, t, 0);
            var (expected, _) = Before(program, t, set);
            Assert.Equal(expected, sets.Writes.Single());
            Assert.All(expected, w => Assert.Equal((int)VkStructureType.WriteDescriptorSet, w.Type));

            // The measured-against order writes the same (a program of the device's one cache:
            // another program for another cache).
            var older = new FakeSets();
            var source = new Source(t);
            var olderProgram = Program();
            var olderSet = new VkDescriptorCache(older).SetForWritesFirst(olderProgram, ref source, DefaultSampler, Storage, 0);
            Assert.Equal(Before(olderProgram, t, olderSet).Writes, older.Writes.Single());
        }

        [Fact]
        public void PushedWritesAreAsBefore()
        {
            var program = Program();
            var t = Draw();
            int n = program.Slots.Length;
            var writes = stackalloc VkWriteDescriptorSet[n];
            var bufferInfos = stackalloc VkDescriptorBufferInfo[n];
            var imageInfos = stackalloc VkDescriptorImageInfo[n];
            var source = new Source(t);
            VkTextureSet.Writes(ref source, program.Slots, DefaultSampler, Storage, default, writes, bufferInfos, imageInfos);
            var (expected, _) = Before(program, t, default);
            for (int i = 0; i < n; i++) Assert.Equal(expected[i], Read(writes[i]));
        }

        [Fact]
        public void IdentitiesAreThoseOfBefore()
        {
            var program = Program();
            var t = Draw();
            var ids = new long[2 * program.Slots.Length];
            var source = new Source(t);
            VkTextureSet.Identities(ref source, program.Slots, DefaultSampler, ids);
            Assert.Equal(Before(program, t, default).Ids, ids);
            var together = new long[ids.Length];
            int n = program.Slots.Length;
            var writes = stackalloc VkWriteDescriptorSet[n];
            var bufferInfos = stackalloc VkDescriptorBufferInfo[n];
            var imageInfos = stackalloc VkDescriptorImageInfo[n];
            VkTextureSet.Writes(ref source, program.Slots, DefaultSampler, Storage, default, writes, bufferInfos, imageInfos, together);
            Assert.Equal(ids, together);
        }

        /// Every descriptor the set holds is in its key: another image or sampler at any binding
        /// that names one is another set; what a binding does not name is not.
        [Fact]
        public void TheKeyCoversEveryDescriptor()
        {
            var program = Program();
            var sets = new FakeSets();
            var cache = new VkDescriptorCache(sets);
            var t = Draw();
            var baseSet = SetFor(cache, program, t, 0).VkHandle;

            Textures With(int index, VkTexture texture)
            {
                var copy = Draw();
                copy.BySampler = (VkTexture[])t.BySampler.Clone();
                copy.BySampler[index] = texture;
                return copy;
            }

            void Differs(Textures changed, string what)
            {
                int made = sets.Allocated;
                var set = SetFor(cache, program, changed, 1);
                Assert.True(set.VkHandle != baseSet, what);
                Assert.Equal(made + 1, sets.Allocated);
                Assert.Equal(Before(program, changed, set).Writes, sets.Writes[^1]);
            }

            void Same(Textures changed, string what) => Assert.True(SetFor(cache, program, changed, 1).VkHandle == baseSet, what);

            for (int i = 0; i < t.BySampler.Length; i++)
            {
                // Another image (a new serial) with the same sampler, at every texture.
                var image = Texture(t.BySampler[i].Sampler.VkHandle);
                Differs(With(i, image), $"texture {i}'s image");
                // The same image with another sampler: texture 3 is only sampled as an image.
                var sampler = new VkTexture { Image = t.BySampler[i].Image, Sampler = new VkSampler((nint)(0x900 + i)) };
                if (i == 3) Same(With(i, sampler), "texture 3 has no sampler binding");
                else Differs(With(i, sampler), $"texture {i}'s sampler");
            }
            // Another texture object naming the same image and sampler is the same set.
            Same(With(0, new VkTexture { Image = t.BySampler[0].Image, Sampler = t.BySampler[0].Sampler }), "same descriptors");
            // Texture 1 has no sampler of its own: it samples with the default one, named as that.
            Same(With(1, new VkTexture { Image = t.BySampler[1].Image, Sampler = DefaultSampler }), "default sampler");
            // Another program (another layout) never shares a set.
            int before = sets.Allocated;
            var other = Program();
            Assert.NotEqual(baseSet, SetFor(cache, other, t, 1).VkHandle);
            Assert.Equal(before + 1, sets.Allocated);
            // The storage buffer is the device's one empty buffer, written as given.
            Assert.Contains(sets.Writes[0], w => w.Kind == VkDescriptorType.StorageBuffer && w.Buffer == (nint)Storage.Buffer.VkHandle && w.Range.Equals((Brutal.ByteSize64)256));
        }

        [Fact]
        public void SetsUnusedForSixtyFramesAreFreedAndMadeAgain()
        {
            var program = Program();
            var sets = new FakeSets();
            var cache = new VkDescriptorCache(sets);
            Textures a = Draw(), b = Draw();
            var setA = SetFor(cache, program, a, 0);
            var setB = SetFor(cache, program, b, 0);
            Assert.Equal(VkDescriptorCache.MaxAge, 60);
            // Used again before the sweeps: B at 70, A not.
            SetFor(cache, program, b, 70);
            cache.Sweep(59);
            cache.Sweep(60);
            Assert.Empty(sets.Freed);
            cache.Sweep(119);
            Assert.Empty(sets.Freed);
            cache.Sweep(120);
            Assert.Equal([(nint)setA.VkHandle], sets.Freed);
            Assert.Equal(1, cache.Count);
            // A's draw makes and writes a new set; B's is still found.
            int writes = sets.Writes.Count;
            var again = SetFor(cache, program, a, 121);
            Assert.NotEqual(setA.VkHandle, again.VkHandle);
            Assert.Equal(writes + 1, sets.Writes.Count);
            Assert.Equal(setB.VkHandle, SetFor(cache, program, b, 121).VkHandle);
            Assert.Equal(writes + 1, sets.Writes.Count);
        }

        [Fact]
        public void AlternationNames()
        {
            Assert.Equal(0, VkDrawAlternation.Parse(null));
            Assert.Equal(0, VkDrawAlternation.Parse("nothing"));
            Assert.Equal(VkDrawAlternation.WritesFirst, VkDrawAlternation.Parse(" writes-first ,x"));
            Assert.Equal(VkDrawAlternation.WritesFirst | VkDrawAlternation.PrepareEveryTexture, VkDrawAlternation.Parse("prepare-every-texture,writes-first"));
        }
    }
}
