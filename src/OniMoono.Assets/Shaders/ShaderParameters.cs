using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OniMoono.Assets.TypeTrees;

namespace OniMoono.Assets.Shaders
{
    /// A constant (uniform) of a program: a vector, scalar or matrix, possibly an array, at a byte
    /// offset in its constant buffer. `Type` is the data type (ShaderParamType: 0 float, 1 int,
    /// 2 bool, 3 half, 4 short, 5 uint, as far as this build's data shows: float, int and uint
    /// occur). Rows by columns: a vector or scalar has one row, a matrix `Rows` rows.
    public readonly record struct ShaderConstant(string Name, int Type, int Rows, int Columns, bool IsMatrix, int ArraySize, int Offset)
    {
        /// Elements of the array, 1 for a non-array (Unity records 0 there).
        public int Elements => Math.Max(1, ArraySize);
    }

    /// A constant buffer and its members, in the order the program records them. The parameter
    /// blob's first buffer is an unnamed root: the program's loose constants, outside any buffer
    /// (GLCore's plain uniforms; empty on Metal, where loose constants live in VGlobals or FGlobals).
    public sealed record ShaderConstantBuffer(string Name, int Size, ShaderConstant[] Members);

    /// A texture parameter: its texture slot, its sampler slot (negative when the texture is only
    /// read, `texture.read`, with no sampler) and TextureDimension (2 = 2D, 3 = 3D, 4 = cube,
    /// 5 = 2D array).
    public readonly record struct ShaderTextureParameter(string Name, int Index, int SamplerIndex, int Dimension, bool MultiSampled);

    /// A sampler declared on its own, not through a texture (SerializedProgramParameters'
    /// `m_Samplers`, a SamplerParameter: `sampler`, an unsigned int whose encoding is not read
    /// here, and `bindPoint`, the slot). Empty in every game shader of U60; mod shaders with inline
    /// sampler states have them (a 2022.3 mod's Hidden/Dither: bind point 0 is its DXBC's s0).
    public readonly record struct ShaderSamplerParameter(uint Sampler, int BindPoint);

    /// A buffer bound to a slot: a constant buffer (`[[buffer(n)]]` on Metal, a uniform-block binding
    /// on GL) or a structured buffer.
    public readonly record struct ShaderBufferParameter(string Name, int Index, int ArraySize);

    /// What a program variant reads, as Unity records it: the pass stage's common parameters (shared
    /// by its variants, in the type tree) merged with the variant's parameter blob (in the platform's
    /// blob table). Names are the program's own (`hlslcc_mtx4x4` prefixes kept).
    public sealed class ShaderParameters
    {
        /// The unnamed root first when the blob has one, then the named buffers.
        public readonly List<ShaderConstantBuffer> ConstantBuffers = new();
        public readonly List<ShaderTextureParameter> Textures = new();
        public readonly List<ShaderBufferParameter> ConstantBufferBindings = new();
        public readonly List<ShaderBufferParameter> StructuredBuffers = new();
        public readonly List<ShaderSamplerParameter> Samplers = new();

        public const uint Magic = 0x0C0A75BA;

        /// Reads a variant's parameter blob (layout inferred from the data; every GLCore and Metal
        /// blob of the game parses to its end): magic; a constant-buffer count, then per buffer its
        /// name, byte size, members (name and six ints: type, rows, columns, matrix flag, array size,
        /// byte offset) and a count of struct members (0 in every blob of the game; anything else is
        /// refused); then a count of bindings, each a name and a kind: 0 texture (slot, sampler slot,
        /// TextureDimension << 1 | multisampled), 1 constant buffer (slot, and an int, 0 in every
        /// blob of the game), 2 structured buffer (slot, and an int, 1 in every blob of the game).
        /// Strings are length-prefixed and padded to four bytes.
        public static ShaderParameters Read(ReadOnlySpan<byte> blob)
        {
            var r = new BlobReader(blob);
            if ((uint)r.Int() != Magic) throw new InvalidDataException("Not a shader parameter blob.");
            var p = new ShaderParameters();
            int buffers = r.Count();
            for (int c = 0; c < buffers; c++)
            {
                string name = r.String();
                int size = r.Int();
                var members = new ShaderConstant[r.Count()];
                for (int m = 0; m < members.Length; m++)
                {
                    string member = r.String();
                    int type = r.Int(), rows = r.Int(), columns = r.Int(), matrix = r.Int(), arraySize = r.Int(), offset = r.Int();
                    members[m] = new ShaderConstant(member, type, rows, columns, matrix != 0, arraySize, offset);
                }
                if (r.Int() != 0) throw new InvalidDataException($"Shader parameter blob: constant buffer '{name}' has struct members.");
                p.ConstantBuffers.Add(new ShaderConstantBuffer(name, size, members));
            }
            int bindings = r.Count();
            for (int i = 0; i < bindings; i++)
            {
                string name = r.String();
                switch (r.Int())
                {
                    case 0:
                    {
                        // The blob packs TextureDimension and multisampling as dimension << 1 | ms
                        // (2D 4, 2D MS 5, 3D 6, cube 8, 2D array 10, 2D MS array 11).
                        int index = r.Int(), sampler = r.Int(), dimension = r.Int();
                        p.Textures.Add(new ShaderTextureParameter(name, index, sampler, dimension >> 1, (dimension & 1) != 0));
                        break;
                    }
                    case 1:
                    {
                        int index = r.Int(), arraySize = r.Int();
                        p.ConstantBufferBindings.Add(new ShaderBufferParameter(name, index, arraySize));
                        break;
                    }
                    case 2:
                    {
                        int index = r.Int(), arraySize = r.Int();
                        p.StructuredBuffers.Add(new ShaderBufferParameter(name, index, arraySize));
                        break;
                    }
                    case var kind: throw new InvalidDataException($"Shader parameter blob: binding kind {kind} for '{name}'.");
                }
            }
            if (!r.AtEnd) throw new InvalidDataException($"Shader parameter blob: {r.Remaining} trailing bytes.");
            return p;
        }

        /// A pass stage's common parameters (SerializedProgramParameters, `m_CommonParameters`),
        /// with names through the pass's name table (`m_NameIndices`). Loose vector and matrix
        /// parameters go into an unnamed root buffer at their recorded index, as a blob's root.
        public static ShaderParameters ReadCommon(Fields parameters, IReadOnlyDictionary<int, string> names)
        {
            var p = new ShaderParameters();
            string Name(Fields f) => names.TryGetValue(f.Int("m_NameIndex"), out var n) ? n : throw new InvalidDataException($"Shader parameter name index {f.Int("m_NameIndex")} has no name.");
            var root = Constants(parameters, Name);
            if (root.Length > 0) p.ConstantBuffers.Add(new ShaderConstantBuffer("", 0, root));
            foreach (var cb in Items(parameters.Array("m_ConstantBuffers")))
            {
                if (cb.Array("m_StructParams").Length != 0) throw new InvalidDataException($"Shader common constant buffer '{Name(cb)}' has struct members.");
                p.ConstantBuffers.Add(new ShaderConstantBuffer(Name(cb), cb.Int("m_Size"), Constants(cb, Name)));
            }
            foreach (var t in Items(parameters.Array("m_TextureParams")))
                p.Textures.Add(new ShaderTextureParameter(Name(t), t.Int("m_Index"), t.Int("m_SamplerIndex"), t.Int("m_Dim"), t.Bool("m_MultiSampled")));
            foreach (var b in Items(parameters.Array("m_ConstantBufferBindings")))
                p.ConstantBufferBindings.Add(new ShaderBufferParameter(Name(b), b.Int("m_Index"), b.Int("m_ArraySize")));
            foreach (var b in Items(parameters.Array("m_BufferParams")))
                p.StructuredBuffers.Add(new ShaderBufferParameter(Name(b), b.Int("m_Index"), b.Int("m_ArraySize")));
            if (parameters.Contains("m_Samplers"))
                foreach (var s in Items(parameters.Array("m_Samplers")))
                    p.Samplers.Add(new ShaderSamplerParameter(s.Get<uint>("sampler"), s.Int("bindPoint")));
            return p;
        }

        // Vector (m_Dim columns) and matrix (m_RowCount rows of four) parameters: m_Index is the
        // byte offset in a buffer, a location for loose ones.
        static ShaderConstant[] Constants(Fields owner, Func<Fields, string> name)
        {
            var result = new List<ShaderConstant>();
            foreach (var v in Items(owner.Array("m_VectorParams")))
                result.Add(new ShaderConstant(name(v), v.Int("m_Type"), 1, v.Int("m_Dim"), false, v.Int("m_ArraySize"), v.Int("m_Index")));
            foreach (var m in Items(owner.Array("m_MatrixParams")))
                result.Add(new ShaderConstant(name(m), m.Int("m_Type"), m.Int("m_RowCount"), 4, true, m.Int("m_ArraySize"), m.Int("m_Index")));
            return result.ToArray();
        }

        /// The common parameters and a variant's blob together (common ones first). Buffers of the
        /// same name are merged, the blob's members after the common ones', with the blob's size:
        /// the common size is the pass's largest variant's (9 Metal programs of the game have a
        /// smaller buffer than their common parameters record; a blob's is never past the MSL's).
        public static ShaderParameters Merge(ShaderParameters common, ShaderParameters variant)
        {
            var p = new ShaderParameters();
            var buffers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var source in new[] { common, variant })
            {
                foreach (var cb in source.ConstantBuffers)
                {
                    if (buffers.TryGetValue(cb.Name, out int at))
                    {
                        var prior = p.ConstantBuffers[at];
                        var members = new ShaderConstant[prior.Members.Length + cb.Members.Length];
                        prior.Members.CopyTo(members, 0);
                        cb.Members.CopyTo(members, prior.Members.Length);
                        p.ConstantBuffers[at] = new ShaderConstantBuffer(cb.Name, cb.Size, members);
                    }
                    else
                    {
                        buffers[cb.Name] = p.ConstantBuffers.Count;
                        p.ConstantBuffers.Add(cb);
                    }
                }
                p.Textures.AddRange(source.Textures);
                p.ConstantBufferBindings.AddRange(source.ConstantBufferBindings);
                p.StructuredBuffers.AddRange(source.StructuredBuffers);
                p.Samplers.AddRange(source.Samplers);
            }
            return p;
        }

        /// The named buffer, or null.
        public ShaderConstantBuffer? Buffer(string name)
        {
            foreach (var cb in ConstantBuffers)
                if (cb.Name == name) return cb;
            return null;
        }

        static IEnumerable<Fields> Items(object?[] array)
        {
            foreach (var item in array) yield return (Fields)item!;
        }

        ref struct BlobReader
        {
            readonly ReadOnlySpan<byte> data;
            int pos;

            public BlobReader(ReadOnlySpan<byte> data)
            {
                this.data = data;
                pos = 0;
            }

            public readonly bool AtEnd => pos == data.Length;
            public readonly int Remaining => data.Length - pos;

            public int Int()
            {
                if (pos + 4 > data.Length) throw new InvalidDataException("Shader parameter blob ends early.");
                int v = BitConverter.ToInt32(data[pos..]);
                pos += 4;
                return v;
            }

            public int Count()
            {
                int n = Int();
                if (n < 0 || n > Remaining) throw new InvalidDataException($"Shader parameter blob: bad count {n}.");
                return n;
            }

            public string String()
            {
                int n = Int();
                if (n < 0 || n > Remaining) throw new InvalidDataException($"Shader parameter blob: bad string length {n}.");
                var s = Encoding.UTF8.GetString(data.Slice(pos, n));
                pos += (n + 3) & ~3;
                if (pos > data.Length) throw new InvalidDataException("Shader parameter blob ends inside a string's padding.");
                return s;
            }
        }
    }
}
