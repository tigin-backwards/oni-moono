using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OniMoono.Assets.Shaders
{
    /// A texture a D3D11 program reads: its SRV slot (t#), its sampler slot (s#), and its
    /// TextureDimension (2 2D, 3 3D, 4 cube, 5 2D array, 6 cube array) and multisampling, as
    /// Unity's parameters record them.
    public readonly record struct D3d11TextureBinding(string Name, int Slot, int Sampler, int Dimension, bool MultiSampled);

    /// A constant buffer a D3D11 program reads: its slot (b#), its byte size and members from
    /// Unity's parameters, and the binding's second int (0 or 1 in U60's data; meaning unknown,
    /// programs.md §2), raw.
    public readonly record struct D3d11ConstantBufferBinding(string Name, int Slot, int Size, ShaderConstant[] Members, int Unknown);

    /// A sampler a D3D11 program declares on its own (an inline sampler state, ShaderSamplerParameter):
    /// its slot (s#) and Unity's `sampler` value, raw (its encoding is not established).
    public readonly record struct D3d11SamplerBinding(int Slot, uint State);

    /// A structured buffer a D3D11 program reads, on an SRV slot (t#), with the binding's second
    /// int raw (its meaning on D3D11 is not established).
    public readonly record struct D3d11BufferBinding(string Name, int Slot, int Unknown);

    /// What a D3D11 program variant binds, from Unity's parameters for it (the pass stage's
    /// `m_CommonParameters` merged with the variant's platform-4 parameter blob; ShaderParameters).
    /// The indices Unity records for D3D11 are the D3D registers themselves (measured on U60's
    /// data against the DXBC declarations, 960/960: texture index = t#, sampler index = s#,
    /// constant buffer index = b#, structured buffer index = t#), unlike Vulkan's packed values.
    public sealed class D3d11Bindings
    {
        public D3d11TextureBinding[] Textures { get; private init; } = [];
        public D3d11ConstantBufferBinding[] ConstantBuffers { get; private init; } = [];
        public D3d11BufferBinding[] StructuredBuffers { get; private init; } = [];
        public D3d11SamplerBinding[] Samplers { get; private init; } = [];

        /// Throws InvalidDataException when a constant buffer binding names no buffer.
        public static D3d11Bindings From(ShaderParameters parameters)
        {
            var cbs = new List<D3d11ConstantBufferBinding>();
            foreach (var b in parameters.ConstantBufferBindings)
            {
                var layout = parameters.Buffer(b.Name) ?? throw new InvalidDataException($"D3D11 bindings: constant buffer binding '{b.Name}' names no constant buffer.");
                cbs.Add(new D3d11ConstantBufferBinding(b.Name, b.Index, layout.Size, layout.Members, b.ArraySize));
            }
            return new D3d11Bindings
            {
                Textures = parameters.Textures.Select(t => new D3d11TextureBinding(t.Name, t.Index, t.SamplerIndex, t.Dimension, t.MultiSampled)).ToArray(),
                ConstantBuffers = cbs.ToArray(),
                StructuredBuffers = parameters.StructuredBuffers.Select(b => new D3d11BufferBinding(b.Name, b.Index, b.ArraySize)).ToArray(),
                Samplers = parameters.Samplers.Select(s => new D3d11SamplerBinding(s.BindPoint, s.Sampler)).ToArray(),
            };
        }

        /// The D3D10_SB_RESOURCE_DIMENSION a texture of this TextureDimension is declared with
        /// (d3d12TokenizedProgramFormat.hpp; TextureDimension as ShaderParameters reads it); 0
        /// for a dimension with no SRV form.
        public static int ResourceDimensionOf(int textureDimension, bool multiSampled) => (textureDimension, multiSampled) switch
        {
            (2, false) => 3, // TEXTURE2D
            (2, true) => 4, // TEXTURE2DMS
            (3, false) => 5, // TEXTURE3D
            (4, false) => 6, // TEXTURECUBE
            (5, false) => 8, // TEXTURE2DARRAY
            (5, true) => 9, // TEXTURE2DMSARRAY
            (6, false) => 10, // TEXTURECUBEARRAY
            _ => 0,
        };

        /// Every disagreement between these bindings and the program: each binding's slot must be
        /// declared in the DXBC with the matching kind (a texture by `dcl_resource` of its
        /// dimension, its sampler and every inline sampler by `dcl_sampler`, a structured buffer by
        /// `dcl_resource_structured` or `dcl_resource_raw`, a constant buffer by
        /// `dcl_constantbuffer` no larger than Unity's size rounded up to 16 bytes), every
        /// declared slot must have a binding, and the prefix's slot counts must equal the
        /// declared counts with the slots dense from 0. Empty when they agree.
        public List<string> Check(D3d11Program program)
        {
            var errors = new List<string>();
            var shader = program.Dxbc.Shader;
            var resources = shader.Resources.ToDictionary(r => r.Slot);
            var samplers = shader.Samplers.ToDictionary(s => s.Slot);
            var cbs = shader.ConstantBuffers.ToDictionary(c => c.Slot);
            var usedT = new Dictionary<int, string>();
            var usedS = new HashSet<int>();
            var usedB = new Dictionary<int, string>();
            foreach (var t in Textures)
            {
                if (!usedT.TryAdd(t.Slot, t.Name)) errors.Add($"t{t.Slot} bound to both {usedT[t.Slot]} and {t.Name}");
                if (!resources.TryGetValue(t.Slot, out var r)) errors.Add($"texture {t.Name} t{t.Slot} is not declared");
                else if (r.Dimension != ResourceDimensionOf(t.Dimension, t.MultiSampled))
                    errors.Add($"texture {t.Name} t{t.Slot} is dimension {t.Dimension}{(t.MultiSampled ? " ms" : "")}, declared as resource dimension {r.Dimension}");
                if (t.Sampler < 0) continue;
                usedS.Add(t.Sampler);
                if (!samplers.ContainsKey(t.Sampler)) errors.Add($"texture {t.Name}'s sampler s{t.Sampler} is not declared");
            }
            foreach (var s in Samplers)
            {
                usedS.Add(s.Slot);
                if (!samplers.ContainsKey(s.Slot)) errors.Add($"inline sampler s{s.Slot} is not declared");
            }
            foreach (var b in StructuredBuffers)
            {
                if (!usedT.TryAdd(b.Slot, b.Name)) errors.Add($"t{b.Slot} bound to both {usedT[b.Slot]} and {b.Name}");
                if (!resources.TryGetValue(b.Slot, out var r)) errors.Add($"structured buffer {b.Name} t{b.Slot} is not declared");
                else if (r.Dimension is not (11 or 12)) errors.Add($"structured buffer {b.Name} t{b.Slot} is declared as resource dimension {r.Dimension}");
            }
            foreach (var b in ConstantBuffers)
            {
                if (!usedB.TryAdd(b.Slot, b.Name)) errors.Add($"b{b.Slot} bound to both {usedB[b.Slot]} and {b.Name}");
                if (!cbs.TryGetValue(b.Slot, out var c)) errors.Add($"constant buffer {b.Name} b{b.Slot} is not declared");
                else if (c.Vectors * 16L > ((long)b.Size + 15) / 16 * 16) errors.Add($"constant buffer {b.Name} b{b.Slot} is declared as {c.Vectors} vectors, Unity's size is {b.Size} bytes");
            }
            foreach (var r in shader.Resources)
                if (!usedT.ContainsKey(r.Slot)) errors.Add($"declared t{r.Slot} has no binding");
            foreach (var s in shader.Samplers)
                if (!usedS.Contains(s.Slot)) errors.Add($"declared s{s.Slot} has no texture or inline sampler using it");
            foreach (var c in shader.ConstantBuffers)
                if (!usedB.ContainsKey(c.Slot)) errors.Add($"declared b{c.Slot} has no binding");
            void Count(string what, int prefix, IEnumerable<int> slots)
            {
                var list = slots.ToList();
                if (prefix != list.Count) errors.Add($"prefix {what} count {prefix}, {list.Count} declared");
                if (list.Count > 0 && list.Max() != list.Count - 1) errors.Add($"{what} slots are not dense from 0 (highest {list.Max()}, {list.Count} declared)");
            }
            Count("SRV", program.PrefixResourceCount, shader.Resources.Select(r => r.Slot));
            Count("constant buffer", program.PrefixConstantBufferCount, shader.ConstantBuffers.Select(c => c.Slot));
            Count("sampler", program.PrefixSamplerCount, shader.Samplers.Select(s => s.Slot));
            return errors;
        }
    }
}
