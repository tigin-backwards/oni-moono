using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using OniMoono.Assets.Shaders;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Vulkan
{
    /// What a draw's texture set (set 0) holds at one binding: a sampler's texture (by its index
    /// in DeviceProgram.Samplers), that texture's sampler (a separate sampler object), or an
    /// empty storage buffer.
    internal readonly record struct VkBindingSlot(int Binding, VkDescriptorType Type, int Index);

    /// A program on Vulkan (DeviceProgram.Vk): one of Unity's shipped Vulkan programs, its shader
    /// modules and layouts. Unity's programs keep textures, samplers and storage buffers in
    /// descriptor set 0 and constant buffers in set 1, at the bindings their parameter tables
    /// record (index = stages << 24 | set << 16 | binding). Set 0 is pushed (or allocated per
    /// frame); set 1's buffers are dynamic uniform buffers, one set per uniform stream buffer,
    /// whose offsets each draw gives (or, past the device's limit on dynamic buffers, plain
    /// uniform buffers in a set allocated per draw).
    internal sealed class VkProgram
    {
        public VkShaderModule Vertex, Fragment;
        public VkDescriptorSetLayout TextureSet, UniformSet;
        public VkPipelineLayout Layout;
        public VkBindingSlot[] Slots;
        /// Set 1: each binding with its block (DeviceProgram.BlockSizes index) and size, by
        /// binding.
        public (int Binding, int Block, int Size)[] Uniforms;
        public bool Push, DynamicUniforms;
        /// Vertex inputs: location, the mesh channel feeding it (-1: none, a constant) and
        /// whether the shader reads integers.
        public (int Location, int Channel, char Kind)[] Inputs;
        /// Set 1 per uniform stream buffer the draws' constants were written to, with the pool
        /// it came from.
        public readonly Dictionary<nint, (VkDescriptorPool Pool, VkDescriptorSet Set)> UniformSets = new();
        public string Name;
        public int Id;

        public unsafe void Destroy(VkContext vk)
        {
            foreach (var (pool, set) in UniformSets.Values)
            {
                var s = set;
                vk.Device.PInvoke.vkFreeDescriptorSets.Invoke(vk.Device.Handle, pool, 1, &s);
            }
            UniformSets.Clear();
            if (!Layout.IsNull()) vk.Device.DestroyPipelineLayout(Layout, null);
            if (!TextureSet.IsNull()) vk.Device.DestroyDescriptorSetLayout(TextureSet, null);
            if (!UniformSet.IsNull()) vk.Device.DestroyDescriptorSetLayout(UniformSet, null);
            if (!Vertex.IsNull()) vk.Device.DestroyShaderModule(Vertex, null);
            if (!Fragment.IsNull()) vk.Device.DestroyShaderModule(Fragment, null);
            Layout = default;
            TextureSet = UniformSet = default;
            Vertex = Fragment = default;
        }
    }

    /// Builds DevicePrograms (device thread) from Unity's shipped Vulkan programs (shader
    /// platform 18), decoded from SMOL-V and bound as their parameter tables say, so that
    /// GlUniforms writes each draw's constants into the programs' constant buffers and
    /// GlDrawRecorder resolves their textures as for OpenGL. A shader without usable Vulkan
    /// programs (a mod's, from a bundle built with another Unity version) runs its GLCore
    /// programs instead, compiled to SPIR-V laid out the same way (VkShaderCompiler). The route
    /// is chosen once per shader (PlatformOf) and logged. A variant that cannot be built is
    /// logged once per shader and kept as a program with Id 0, which draws nothing.
    internal sealed unsafe class VkPrograms
    {
        readonly VkContext vk;
        static readonly Dictionary<string, int> layouts = new();
        static int nextId = 1;
        readonly HashSet<string> reported = new();
        VkShaderCompiler compiler;

        /// Variants that could not be built (--check-shaders).
        public static int Failures;
        static readonly HashSet<ShaderNative> routesLogged = new();

        /// The platform whose programs the device draws a shader with (any thread): Vulkan when
        /// every pass with programs has Vulkan variants that read, else OpenGLCore when the shader
        /// has GLCore programs, else 0 (not drawn).
        public static uint PlatformOf(ShaderNative shader)
        {
            uint known = shader.DevicePlatform;
            if (known != 0) return known == uint.MaxValue ? 0 : known;
            var (platform, why) = Route(shader);
            shader.DevicePlatform = platform == 0 ? uint.MaxValue : platform;
            lock (routesLogged)
                if (routesLogged.Add(shader))
                    EngineLog.Engine(platform == ShaderNative.VulkanPlatform ? $"vulkan: shader '{shader.ShaderName}': Unity's Vulkan programs"
                        : platform == ShaderNative.OpenGLCorePlatform ? $"vulkan: shader '{shader.ShaderName}': its GLCore programs compiled to SPIR-V ({why})"
                        : $"vulkan: shader '{shader.ShaderName}' is not drawn ({why})");
            return platform;
        }

        static readonly bool ForceGlCore = Environment.GetEnvironmentVariable("ONI_VULKAN_ROUTE") == "glcore";

        static (uint Platform, string Why) Route(ShaderNative shader)
        {
            uint fallback = shader.HasOpenGLCorePrograms ? ShaderNative.OpenGLCorePlatform : 0;
            // Diagnostic: every shader takes the GLCore route.
            if (ForceGlCore && fallback != 0) return (fallback, "ONI_VULKAN_ROUTE=glcore");
            if (!shader.HasProgramsFor(ShaderNative.VulkanPlatform)) return (fallback, "no Vulkan programs");
            try
            {
                var programs = shader.ProgramsFor(ShaderNative.VulkanPlatform);
                if (programs == null) return (fallback, "no Vulkan programs");
                foreach (var sub in shader.SubShaders)
                    foreach (var pass in sub.Passes)
                    {
                        if (pass.Type != 0) continue;
                        if (pass.VulkanVariants.Length == 0)
                        {
                            if (pass.Variants.Length > 0) return (fallback, "a pass without Vulkan variants");
                            continue;
                        }
                        // The layout of this Unity version's entries (decoded once, kept).
                        programs.Vulkan(pass.VulkanVariants[0].BlobIndex);
                    }
                return (ShaderNative.VulkanPlatform, null);
            }
            catch (Exception e) when (e is System.IO.InvalidDataException or NotSupportedException or ArgumentException)
            {
                return (fallback, $"Vulkan programs not read: {e.Message}");
            }
        }

        /// Programs built and their build time (diagnostics).
        public int Built;
        public long BuildTicks;

        public VkPrograms(VkContext vk) => this.vk = vk;

        /// Pools for the programs' set 1, kept for the device's life; sets are freed with
        /// their programs.
        readonly List<VkDescriptorPool> uniformPools = new();

        /// The program's set 1 naming a uniform stream buffer (made on first use).
        public VkDescriptorSet UniformSet(VkProgram p, VkBuffer buffer)
        {
            if (p.UniformSets.TryGetValue(buffer.VkHandle, out var known)) return known.Set;
            var layout = p.UniformSet;
            VkDescriptorSet set = default;
            VkDescriptorPool pool = default;
            for (bool full = uniformPools.Count == 0; ; full = true)
            {
                if (full)
                {
                    var size = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBufferDynamic, DescriptorCount = 8192 };
                    uniformPools.Add(vk.Device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo
                    {
                        Flags = VkDescriptorPoolCreateFlags.FreeDescriptorSetBit, MaxSets = 2048, PoolSizeCount = 1, PoolSizes = &size,
                    }, null));
                }
                pool = uniformPools[^1];
                var info = new VkDescriptorSetAllocateInfo { DescriptorPool = pool, DescriptorSetCount = 1, SetLayouts = &layout };
                var r = vk.Device.PInvoke.vkAllocateDescriptorSets.Invoke(vk.Device.Handle, &info, &set);
                if (r == VkResult.Success) break;
                if (r != VkResult.ErrorOutOfPoolMemory && r != VkResult.ErrorFragmentedPool) VkCheck.Ok(r, "vkAllocateDescriptorSets");
            }
            Write(p, set, buffer, 0, VkDescriptorType.UniformBufferDynamic);
            p.UniformSets[buffer.VkHandle] = (pool, set);
            return set;
        }

        /// Points a set 1 at a buffer: each constant buffer at `offset` plus its place among
        /// the draw's blocks (offset 0 for dynamic sets, whose offsets each draw gives).
        public void Write(VkProgram p, VkDescriptorSet set, VkBuffer buffer, long offset, VkDescriptorType type, ReadOnlySpan<long> starts = default)
        {
            int n = p.Uniforms.Length;
            var writes = stackalloc VkWriteDescriptorSet[n];
            var infos = stackalloc VkDescriptorBufferInfo[n];
            for (int i = 0; i < n; i++)
            {
                infos[i] = new VkDescriptorBufferInfo
                {
                    Buffer = buffer, Offset = (Brutal.ByteSize64)(offset + (starts.IsEmpty ? 0 : starts[i])), Range = (Brutal.ByteSize64)p.Uniforms[i].Size,
                };
                writes[i] = new VkWriteDescriptorSet { DstSet = set, DstBinding = p.Uniforms[i].Binding, DescriptorCount = 1, DescriptorType = type, BufferInfo = &infos[i] };
            }
            vk.Device.PInvoke.vkUpdateDescriptorSets.Invoke(vk.Device.Handle, n, writes, 0, null);
        }

        public void Dispose()
        {
            foreach (var pool in uniformPools) vk.Device.DestroyDescriptorPool(pool, null);
            uniformPools.Clear();
            compiler?.Dispose();
            compiler = null;
        }

        public DeviceProgram Get(ShaderNative shader, int blobIndex)
        {
            if (shader.Gpu is not DeviceProgramSet set)
            {
                set = new DeviceProgramSet();
                shader.Gpu = set;
                GraphicsDevice.Adopt(shader, set);
            }
            if (set.Programs.TryGetValue(blobIndex, out var p)) return p;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                VkFault.OutOfMemoryEvery("program-every");
                p = PlatformOf(shader) == ShaderNative.VulkanPlatform ? BuildShipped(shader, blobIndex) : BuildCompiled(shader, blobIndex);
            }
            catch (Exception e) when (VkFault.IsOutOfMemory(e))
            {
                // Out of memory now, maybe not at the next draw: logged, not remembered.
                if (reported.Add($"{shader.ShaderName}\noom"))
                    EngineLog.Engine($"vulkan: shader '{shader.ShaderName}' variant (blob {blobIndex}) ran out of memory being built; tried again at its next draw");
                return new DeviceProgram();
            }
            catch (Exception e) when (!VkDevice.IsDeviceLost(e))
            {
                // Damaged data, a reflection or module the driver refuses: never drawn, and kept
                // so the next draw does not try again.
                System.Threading.Interlocked.Increment(ref Failures);
                if (reported.Add($"{shader.ShaderName}\nfailed"))
                    EngineLog.Engine($"vulkan: shader '{shader.ShaderName}' variant (blob {blobIndex}) could not be built, its draws are left out: {e.GetType().Name}: {e.Message}");
                p = new DeviceProgram();
            }
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            BuildTicks += ticks;
            Built++;
            System.Threading.Interlocked.Add(ref StartupStats.ProgramTicks, ticks);
            System.Threading.Interlocked.Increment(ref StartupStats.Programs);
            set.Programs[blobIndex] = p;
            return p;
        }

        void NotDrawn(ShaderNative shader, IEnumerable<string> keywords, string why)
        {
            // Once per shader and reason (the warm-up meets every variant).
            if (reported.Add($"{shader.ShaderName}\n{why}"))
                EngineLog.Engine($"vulkan: shader '{shader.ShaderName}' variants with {why} are not drawn (first: [{string.Join(" ", keywords)}])");
        }

        /// The pass holding a Vulkan variant, and the variant (its parameter blob).
        static (ShaderPass Pass, ProgramVariant Variant) Find(ShaderNative shader, int blobIndex)
        {
            foreach (var sub in shader.SubShaders)
                foreach (var pass in sub.Passes)
                    foreach (var v in pass.VulkanVariants)
                        if (v.BlobIndex == blobIndex) return (pass, v);
            return (null, default);
        }

        DeviceProgram BuildShipped(ShaderNative shader, int blobIndex)
        {
            var program = new DeviceProgram();
            var programs = shader.ProgramsFor(ShaderNative.VulkanPlatform);
            var (pass, variant) = Find(shader, blobIndex);
            if (programs == null || pass == null) return program;
            var vp = programs.Vulkan(blobIndex);
            var parameters = programs.Parameters(pass.Data, "progVertex", variant.ParameterBlobIndex);
            var vertex = vp.Stages.FirstOrDefault(s => s.Index == 0);
            var fragment = vp.Stages.FirstOrDefault(s => s.Index == 1);
            if (vertex == null || fragment == null || vp.Stages.Length != 2)
            {
                NotDrawn(shader, variant.Keywords, "stages other than a vertex and a fragment stage");
                return program;
            }
            var vi = SpirvReflection.Interface(vertex.Spirv);
            var fi = SpirvReflection.Interface(fragment.Spirv);
            if (vi.DepthCompare || fi.DepthCompare)
            {
                NotDrawn(shader, variant.Keywords, "shadow (depth-comparison) samplers");
                return program;
            }
            if (Uses(vertex.Spirv, "MultiView") || Uses(fragment.Spirv, "MultiView"))
            {
                NotDrawn(shader, variant.Keywords, "XR stereo (multiview)");
                return program;
            }
            // Every descriptor either stage reads, by set and binding.
            var descriptors = new Dictionary<(int Set, int Binding), SpirvReflection.Descriptor>();
            foreach (var d in vi.Descriptors.Concat(fi.Descriptors)) descriptors[(d.Set, d.Binding)] = d;
            if (descriptors.Values.Any(d => d.Multisampled || d.Kind == SpirvReflection.DescriptorKind.Other || d.Set > 1))
            {
                NotDrawn(shader, variant.Keywords, "multisample textures or descriptors outside sets 0 and 1");
                return program;
            }

            // Constant buffers: set 1, one block each (DeviceProgram.BlockSizes).
            var bufferBindings = parameters.ConstantBufferBindings.ToDictionary(b => b.Name, b => b.Index);
            var blockSizes = new List<int>();
            var uniforms = new List<(int Binding, int Block, int Size)>();
            foreach (var cb in parameters.ConstantBuffers)
            {
                if (cb.Name.Length == 0 || !bufferBindings.TryGetValue(cb.Name, out int index)) continue;
                int binding = index & 0xFFFF;
                int block = blockSizes.Count;
                int size = Math.Max(16, (cb.Size + 15) & ~15);
                blockSizes.Add(size);
                uniforms.Add((binding, block, size));
                foreach (var m in cb.Members) program.Uniforms.Add(Uniform(m, block));
            }
            foreach (var d in descriptors.Values)
                if (d.Set == 1 && !uniforms.Any(u => u.Binding == d.Binding))
                {
                    NotDrawn(shader, variant.Keywords, $"a constant buffer at binding {d.Binding} its parameters do not name");
                    return program;
                }

            // Textures, their samplers, storage buffers: set 0.
            var slots = new Dictionary<int, VkBindingSlot>();
            foreach (var t in parameters.Textures)
            {
                int binding = t.Index & 0xFFFF;
                if (!descriptors.TryGetValue((0, binding), out var d)) continue; // read by no stage
                uint target = t.Dimension switch { 3 => TexTarget3D, 4 => TexTargetCubeMap, 5 => TexTarget2DArray, _ => TexTarget2D };
                int sampler = program.Samplers.Count;
                program.Samplers.Add(new SamplerBinding { Location = binding, Unit = sampler, Target = target, PropertyId = ShaderPropertyIds.Get(t.Name) });
                var type = d.Kind == SpirvReflection.DescriptorKind.SampledImage ? VkDescriptorType.SampledImage : VkDescriptorType.CombinedImageSampler;
                slots[binding] = new VkBindingSlot(binding, type, sampler);
                if (t.SamplerIndex >= 0 && descriptors.TryGetValue((0, t.SamplerIndex & 0xFFFF), out var s) && s.Kind == SpirvReflection.DescriptorKind.Sampler)
                    slots.TryAdd(t.SamplerIndex & 0xFFFF, new VkBindingSlot(t.SamplerIndex & 0xFFFF, VkDescriptorType.Sampler, sampler));
            }
            foreach (var d in descriptors.Values)
            {
                if (d.Set != 0 || slots.ContainsKey(d.Binding)) continue;
                // Read as OpenGL's device leaves them: an empty buffer, a sampler of its own.
                var type = d.Kind switch
                {
                    SpirvReflection.DescriptorKind.StorageBuffer => VkDescriptorType.StorageBuffer,
                    SpirvReflection.DescriptorKind.Sampler => VkDescriptorType.Sampler,
                    _ => (VkDescriptorType)(-1),
                };
                if ((int)type < 0)
                {
                    NotDrawn(shader, variant.Keywords, $"a texture at binding {d.Binding} its parameters do not name");
                    return program;
                }
                slots[d.Binding] = new VkBindingSlot(d.Binding, type, -1);
            }
            program.BlockSizes = blockSizes.ToArray();
            foreach (var size in program.BlockSizes) program.BlocksLength += size;

            // Vertex inputs: Unity's attribute table maps channels to vertex components, 13 + n
            // being location n.
            var inputs = new List<(int, int, char)>();
            foreach (var input in vi.Inputs)
            {
                int channel = -1;
                foreach (var (c, component) in vp.Inputs)
                    if (component - 13 == input.Location) channel = c;
                if (channel >= (int)Channel.Count) channel = -1;
                inputs.Add((input.Location, channel, input.Kind));
                if (channel >= 0) program.Attributes[channel] = input.Location;
            }
            for (int c = 0; c < program.Attributes.Length; c++)
                if (!inputs.Any(i => i.Item2 == c)) program.Attributes[c] = -1;
            inputs.Sort();
            var vkProgram = new VkProgram
            {
                Name = shader.ShaderName,
                Slots = slots.Values.OrderBy(s => s.Binding).ToArray(),
                Uniforms = uniforms.OrderBy(u => u.Binding).ToArray(),
                Inputs = inputs.ToArray(),
            };
            return Finish(program, vkProgram, vertex.Spirv, fragment.Spirv);
        }

        /// The program's ids and Vulkan objects; on failure nothing is kept (the caller logs).
        DeviceProgram Finish(DeviceProgram program, VkProgram vkProgram, byte[] vertex, byte[] fragment)
        {
            try
            {
                CreateObjects(vkProgram, vertex, fragment);
            }
            catch
            {
                vkProgram.Destroy(vk);
                throw;
            }
            var layoutKey = string.Join(",", vkProgram.Inputs);
            lock (layouts)
            {
                if (!layouts.TryGetValue(layoutKey, out program.LayoutId)) layouts[layoutKey] = program.LayoutId = layouts.Count;
                program.Id = (uint)nextId++;
            }
            vkProgram.Id = (int)program.Id;
            program.Vk = vkProgram;
            return program;
        }

        /// A GLCore variant compiled to SPIR-V (VkGlsl): its loose uniforms and blocks in set 1,
        /// samplers and storage buffers in set 0, inputs at their channels' locations.
        DeviceProgram BuildCompiled(ShaderNative shader, int blobIndex)
        {
            var program = new DeviceProgram();
            var variant = shader.Programs?.Variant(blobIndex);
            if (variant == null) return program;
            if (variant.GeometrySource != null)
            {
                NotDrawn(shader, variant.Keywords, "a geometry stage");
                return program;
            }
            compiler ??= new VkShaderCompiler();
            VkShaderCompiler.Compiled compiled;
            try
            {
                compiled = compiler.Compile(variant.VertexSource, variant.FragmentSource, shader.ShaderName);
            }
            catch (NotSupportedException e)
            {
                NotDrawn(shader, variant.Keywords, e.Message);
                return program;
            }
            var t = compiled.Program;
            foreach (var r in t.Resources)
                if (r.Kind == VkGlsl.ResourceKind.Sampler && SamplerTarget(r.Type) == 0)
                {
                    NotDrawn(shader, variant.Keywords, $"sampler '{r.Name}' of type {r.Type}");
                    return program;
                }
            var blocks = SpirvReflection.UniformBlocks(compiled.Vertex);
            foreach (var b in SpirvReflection.UniformBlocks(compiled.Fragment))
                if (!blocks.Exists(k => k.Binding == b.Binding)) blocks.Add(b);
            var blockSizes = new List<int>();
            var uniforms = new List<(int Binding, int Block, int Size)>();
            foreach (var b in blocks.OrderBy(b => b.Binding))
            {
                int block = blockSizes.Count, size = Math.Max(16, (b.Size + 15) & ~15);
                blockSizes.Add(size);
                uniforms.Add((b.Binding, block, size));
                foreach (var m in b.Members)
                {
                    var u = new UniformBinding
                    {
                        Name = m.Name, Type = GlType(m), Size = m.ArrayLength > 0 ? m.ArrayLength : 1, Block = block, Offset = m.Offset,
                        ArrayStride = m.ArrayLength > 0 ? m.ArrayStride : 0, OpenGlClip = true,
                    };
                    GlPrograms.Classify(u, m.Name);
                    program.Uniforms.Add(u);
                }
            }
            var slots = new List<VkBindingSlot>();
            foreach (var r in t.Resources)
                switch (r.Kind)
                {
                    case VkGlsl.ResourceKind.StorageBuffer:
                        slots.Add(new VkBindingSlot(r.Binding, VkDescriptorType.StorageBuffer, -1));
                        break;
                    case VkGlsl.ResourceKind.Sampler:
                        slots.Add(new VkBindingSlot(r.Binding, VkDescriptorType.CombinedImageSampler, program.Samplers.Count));
                        program.Samplers.Add(new SamplerBinding
                        {
                            Location = r.Binding, Unit = program.Samplers.Count, Target = SamplerTarget(r.Type), PropertyId = ShaderPropertyIds.Get(r.Name),
                        });
                        break;
                }
            program.BlockSizes = blockSizes.ToArray();
            foreach (var size in program.BlockSizes) program.BlocksLength += size;
            // Inputs at their channels' locations (VkGlsl.AttributeNames), others after.
            var inputs = new List<(int, int, char)>();
            for (int c = 0; c < program.Attributes.Length; c++) program.Attributes[c] = -1;
            foreach (var (location, (_, type)) in t.Inputs)
            {
                int channel = location < (int)Channel.Count ? location : -1;
                if (channel >= 0) program.Attributes[channel] = location;
                inputs.Add((location, channel, type.StartsWith('i') ? 'i' : type.StartsWith('u') ? 'u' : 'f'));
            }
            var vkProgram = new VkProgram
            {
                Name = shader.ShaderName,
                Slots = slots.OrderBy(s => s.Binding).ToArray(),
                Uniforms = uniforms.ToArray(),
                Inputs = inputs.ToArray(),
            };
            return Finish(program, vkProgram, compiled.Vertex, compiled.Fragment);
        }

        /// The OpenGL uniform type a compiled block member has (GlUniforms writes values by it).
        static uint GlType(SpirvReflection.Member m)
        {
            if (m.Columns == 4 && m.Components == 4) return FloatMat4;
            int n = m.Components;
            return m.Type switch
            {
                SpirvReflection.Scalar.Float => n switch { 1 => FloatType, 2 => FloatVec2, 3 => FloatVec3, _ => FloatVec4 },
                SpirvReflection.Scalar.Int => n switch { 1 => IntType, 2 => IntVec2, 3 => IntVec3, _ => IntVec4 },
                SpirvReflection.Scalar.Uint => n switch { 1 => UnsignedIntType, 2 => UintVec2, 3 => UintVec3, _ => UintVec4 },
                _ => n switch { 1 => BoolType, 2 => BoolVec2, 3 => BoolVec3, _ => BoolVec4 },
            };
        }

        /// The texture target a GLCore sampler reads; 0 for samplers this device does not bind
        /// (shadow comparison and multisample samplers).
        static uint SamplerTarget(string type) => type switch
        {
            "sampler2D" or "isampler2D" or "usampler2D" => TexTarget2D,
            "sampler3D" => TexTarget3D,
            "sampler2DArray" => TexTarget2DArray,
            "samplerCube" => TexTargetCubeMap,
            _ => 0,
        };

        /// A constant of a constant buffer as GlUniforms writes it: matrices as four vec4
        /// columns (Unity's Vulkan programs lay them out as HLSLcc's GLSL does), arrays at a
        /// 16-byte stride.
        static UniformBinding Uniform(ShaderConstant m, int block)
        {
            var u = new UniformBinding { Name = m.Name, Block = block, Offset = m.Offset };
            if (m.IsMatrix)
            {
                u.IsMatrix = true;
                u.Type = FloatVec4;
                u.Size = m.Columns * m.Elements;
                u.ArrayStride = 16;
            }
            else
            {
                int n = m.Columns;
                u.Type = m.Type switch
                {
                    1 or 2 => n switch { 1 => IntType, 2 => IntVec2, 3 => IntVec3, _ => IntVec4 },
                    5 => n switch { 1 => UnsignedIntType, 2 => UintVec2, 3 => UintVec3, _ => UintVec4 },
                    _ => n switch { 1 => FloatType, 2 => FloatVec2, 3 => FloatVec3, _ => FloatVec4 },
                };
                u.Size = m.ArraySize > 0 ? m.ArraySize : 1;
                u.ArrayStride = m.ArraySize > 0 ? 16 : 0;
            }
            GlPrograms.Classify(u, m.Name);
            return u;
        }

        /// Whether a module declares a capability (by name: MultiView is 4439).
        static bool Uses(byte[] spirv, string capability)
        {
            int wanted = capability == "MultiView" ? 4439 : -1;
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(spirv);
            for (int i = 5; i < words.Length;)
            {
                int count = (int)(words[i] >> 16), op = (int)(words[i] & 0xFFFF);
                if (op == 17 && (int)words[i + 1] == wanted) return true;
                if (op != 17 && op != 11 && op != 14 && op != 10) break; // past the capabilities
                i += count;
            }
            return false;
        }

        void CreateObjects(VkProgram p, byte[] vertex, byte[] fragment)
        {
            p.Vertex = Module(vertex);
            p.Fragment = Module(fragment);
            const VkShaderStageFlags stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;
            p.Push = vk.PushDescriptors && p.Slots.Length <= vk.MaxPushDescriptors;
            p.DynamicUniforms = p.Uniforms.Length <= vk.Limits.MaxDescriptorSetUniformBuffersDynamic;
            var uniformType = p.DynamicUniforms ? VkDescriptorType.UniformBufferDynamic : VkDescriptorType.UniformBuffer;
            var textures = stackalloc VkDescriptorSetLayoutBinding[Math.Max(1, p.Slots.Length)];
            for (int i = 0; i < p.Slots.Length; i++)
                textures[i] = new VkDescriptorSetLayoutBinding { Binding = p.Slots[i].Binding, DescriptorType = p.Slots[i].Type, DescriptorCount = 1, StageFlags = stages };
            p.TextureSet = vk.Device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo
            {
                BindingCount = p.Slots.Length, Bindings = textures, Flags = p.Push ? VkDescriptorSetLayoutCreateFlags.PushDescriptorBitKHR : 0,
            }, null);
            var uniforms = stackalloc VkDescriptorSetLayoutBinding[Math.Max(1, p.Uniforms.Length)];
            for (int i = 0; i < p.Uniforms.Length; i++)
                uniforms[i] = new VkDescriptorSetLayoutBinding { Binding = p.Uniforms[i].Binding, DescriptorType = uniformType, DescriptorCount = 1, StageFlags = stages };
            p.UniformSet = vk.Device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = p.Uniforms.Length, Bindings = uniforms }, null);
            var sets = stackalloc VkDescriptorSetLayout[2] { p.TextureSet, p.UniformSet };
            p.Layout = vk.Device.CreatePipelineLayout(new VkPipelineLayoutCreateInfo { SetLayoutCount = 2, SetLayouts = sets }, null);
        }

        public VkShaderModule Module(byte[] spirv)
        {
            fixed (byte* code = spirv)
                return vk.Device.CreateShaderModule(new VkShaderModuleCreateInfo { CodeSize = (Brutal.ByteSize64)spirv.Length, Code = (int*)code }, null);
        }
    }
}
