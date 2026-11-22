using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OniMoono.Assets.Shaders
{
    /// A chunk of a DXBC container: its FourCC and where its data is (after the chunk's 8-byte
    /// tag and size header), relative to the container's start.
    public readonly record struct DxbcChunk(uint Tag, int Offset, int Size)
    {
        public string Name => Encoding.ASCII.GetString(BitConverter.GetBytes(Tag));
    }

    /// An element of a DXBC signature chunk (ISGN, OSGN, ISG1, OSG1, OSG5), as
    /// D3D11_SIGNATURE_PARAMETER_DESC in the Windows SDK's d3d11shader.h describes it.
    /// `SystemValue` is a D3D_NAME and `ComponentType` a D3D_REGISTER_COMPONENT_TYPE (d3dcommon.h);
    /// `Mask` is the components the register has; `ReadWriteMask` the components an input always
    /// reads, or an output never writes. `Stream` is 0 and `MinPrecision` 0 (D3D_MIN_PRECISION_DEFAULT)
    /// in the signature kinds that do not store them. `Register` is raw: an SV_Depth output's is
    /// 0xFFFFFFFF (observed in U60's data; depth has no output register).
    public readonly record struct DxbcSignatureElement(string SemanticName, uint SemanticIndex, uint SystemValue, uint ComponentType,
        uint Register, byte Mask, byte ReadWriteMask, uint Stream, uint MinPrecision);

    /// A constant buffer declaration (`dcl_constantbuffer cb#[size]`): its slot (b#), its size in
    /// 16-byte vectors (0: any size) and whether it is dynamically indexed.
    public readonly record struct DxbcConstantBufferDeclaration(int Slot, int Vectors, bool DynamicIndexed);

    /// A shader resource declaration on a t# slot: `dcl_resource` (Dimension a
    /// D3D10_SB_RESOURCE_DIMENSION 2–10, SampleCount for the multisampled ones, ReturnType the raw
    /// resource return type token), `dcl_resource_raw` (Dimension 11) or `dcl_resource_structured`
    /// (Dimension 12, with the structure's byte Stride).
    public readonly record struct DxbcResourceDeclaration(int Slot, int Dimension, int SampleCount, uint ReturnType, int Stride);

    /// A sampler declaration (`dcl_sampler s#, mode`): Mode is a D3D10_SB_SAMPLER_MODE (0 default,
    /// 1 comparison, 2 mono).
    public readonly record struct DxbcSamplerDeclaration(int Slot, int Mode);

    /// A DXBC container (the D3D shader bytecode format of shader models 4 and 5), read as the
    /// open-source readers read it and checked, never changed: the bytes stay as given, for the
    /// device. The layout and the checks below follow vkd3d-shader's `parse_dxbc`
    /// (libs/vkd3d-shader/dxbc.c at commit 698cbfbd0ecfd8b2c9f07a59ceeec41cfcd34aa1,
    /// https://gitlab.winehq.org/wine/vkd3d):
    /// - a 32-byte header: magic "DXBC", a 16-byte checksum, a version field (1), the container's
    ///   total size and a chunk count; then the count's 4-byte chunk offsets;
    /// - at each offset a chunk: FourCC, size, then the size's bytes, inside the container.
    ///
    /// The checksum is checked (DxbcChecksum). The signature chunks and the shader code chunk
    /// (SHDR or SHEX; both hold the tokenized program, and U60 has shader model 4.0 programs in
    /// SHEX chunks too) are decoded; SFI0's flags are read as a raw value; every other chunk (RDEF,
    /// STAT, SPDB…) is kept as its tag and extent only.
    ///
    /// Beyond vkd3d-shader's checks, a container must span the bytes given exactly, and hold one
    /// code chunk, one input and one output signature, and at most one SFI0. Every container of
    /// U60's data (960) and of the U60-layout mod bundles (652) has that shape; a second chunk of
    /// a kind would leave which one applies open, so it is refused.
    public sealed class DxbcContainer
    {
        public const uint Magic = 0x43425844; // "DXBC"
        public const int HeaderBytes = 32;

        public static readonly uint ISGN = Tag("ISGN"), OSGN = Tag("OSGN"), ISG1 = Tag("ISG1"), OSG1 = Tag("OSG1"), OSG5 = Tag("OSG5"),
            SHDR = Tag("SHDR"), SHEX = Tag("SHEX"), SFI0 = Tag("SFI0"), RDEF = Tag("RDEF"), STAT = Tag("STAT");

        public static uint Tag(string fourCc) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(fourCc));

        /// The container's bytes, as given.
        public byte[] Bytes { get; private init; } = [];
        public DxbcChunk[] Chunks { get; private init; } = [];
        public DxbcSignatureElement[] Inputs { get; private init; } = [];
        public DxbcSignatureElement[] Outputs { get; private init; } = [];
        /// The input signature chunk's tag (ISGN or ISG1) and the output's (OSGN, OSG1 or OSG5).
        public uint InputSignatureTag { get; private init; }
        public uint OutputSignatureTag { get; private init; }
        public DxbcShader Shader { get; private init; } = null!;
        /// SFI0's 64-bit feature flags (D3D_SHADER_FEATURE_* in d3dcommon.h), null without SFI0.
        public ulong? Features { get; private init; }

        public bool Has(uint tag)
        {
            foreach (var c in Chunks)
                if (c.Tag == tag) return true;
            return false;
        }

        /// Reads and checks a container that spans `data` exactly. Throws InvalidDataException
        /// for anything else.
        public static DxbcContainer Read(ReadOnlySpan<byte> data)
        {
            try
            {
                return ReadContainer(data);
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                throw new InvalidDataException($"DXBC: {e.Message}", e);
            }
        }

        static DxbcContainer ReadContainer(ReadOnlySpan<byte> data)
        {
            if (data.Length < HeaderBytes) throw new InvalidDataException($"DXBC: {data.Length} bytes is less than the {HeaderBytes}-byte header.");
            if (U32(data, 0) != Magic) throw new InvalidDataException("DXBC: bad magic.");
            uint version = U32(data, 20), total = U32(data, 24), count = U32(data, 28);
            if (version != 1) throw new InvalidDataException($"DXBC: version {version}, not 1.");
            if (total != (uint)data.Length) throw new InvalidDataException($"DXBC: total size {total}, but the container is {data.Length} bytes.");
            Span<byte> checksum = stackalloc byte[16];
            DxbcChecksum.Compute(data, checksum);
            if (!checksum.SequenceEqual(data.Slice(4, 16))) throw new InvalidDataException("DXBC: bad checksum.");
            if (count > (uint)(data.Length - HeaderBytes) / 4) throw new InvalidDataException($"DXBC: {count} chunks do not fit.");
            var chunks = new DxbcChunk[count];
            for (int i = 0; i < chunks.Length; i++)
            {
                uint offset = U32(data, HeaderBytes + 4 * i);
                if (offset > (uint)data.Length - 8) throw new InvalidDataException($"DXBC: chunk {i} at {offset} is outside the container.");
                uint size = U32(data, (int)offset + 4);
                if (size > (uint)data.Length - offset - 8) throw new InvalidDataException($"DXBC: chunk {i} of {size} bytes at {offset} ends past the container.");
                chunks[i] = new DxbcChunk(U32(data, (int)offset), (int)offset + 8, (int)size);
            }
            DxbcChunk? Single(params uint[] tags)
            {
                DxbcChunk? found = null;
                foreach (var c in chunks)
                    if (Array.IndexOf(tags, c.Tag) >= 0)
                    {
                        if (found != null) throw new InvalidDataException($"DXBC: chunks {found.Value.Name} and {c.Name} both present.");
                        found = c;
                    }
                return found;
            }
            var code = Single(SHDR, SHEX) ?? throw new InvalidDataException("DXBC: no SHDR or SHEX chunk.");
            var input = Single(ISGN, ISG1) ?? throw new InvalidDataException("DXBC: no input signature.");
            var output = Single(OSGN, OSG1, OSG5) ?? throw new InvalidDataException("DXBC: no output signature.");
            var sfi0 = Single(SFI0);
            ulong? features = null;
            if (sfi0 is { } f)
            {
                // vkd3d-shader's shdr_parse_features reads the first 8 bytes and needs no more.
                if (f.Size < 8) throw new InvalidDataException($"DXBC: SFI0 of {f.Size} bytes, less than 8.");
                features = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(f.Offset, 8));
            }
            var shader = DxbcShader.Read(data.Slice(code.Offset, code.Size));
            return new DxbcContainer
            {
                Bytes = data.ToArray(),
                Chunks = chunks,
                InputSignatureTag = input.Tag,
                OutputSignatureTag = output.Tag,
                Inputs = Signature(data.Slice(input.Offset, input.Size), input.Tag),
                Outputs = Signature(data.Slice(output.Offset, output.Size), output.Tag),
                Shader = shader,
                Features = features,
            };
        }

        /// A signature chunk, as vkd3d-shader's `vsir_signature_parse` (dxbc.c, same commit) reads
        /// it: an element count and the byte offset of the first element (at least 8, a multiple
        /// of 4), then per element: the stream (OSG5, ISG1 and OSG1 only), the semantic name's
        /// offset in the chunk (a NUL-terminated string), semantic index, system value, component
        /// type, register, a mask word (byte 0 the mask, byte 1 the read/write mask) and the
        /// minimum precision (ISG1 and OSG1 only).
        static DxbcSignatureElement[] Signature(ReadOnlySpan<byte> chunk, uint tag)
        {
            if (chunk.Length < 8) throw new InvalidDataException("DXBC: signature chunk shorter than its header.");
            uint count = U32(chunk, 0), first = U32(chunk, 4);
            if (first < 8 || first % 4 != 0 || first > (uint)chunk.Length) throw new InvalidDataException($"DXBC: signature elements at {first}.");
            bool minPrecision = tag == ISG1 || tag == OSG1;
            bool stream = minPrecision || tag == OSG5;
            int elementBytes = 24 + (stream ? 4 : 0) + (minPrecision ? 4 : 0);
            if (count > ((uint)chunk.Length - first) / (uint)elementBytes) throw new InvalidDataException($"DXBC: {count} signature elements do not fit.");
            var elements = new DxbcSignatureElement[count];
            int pos = (int)first;
            for (int i = 0; i < elements.Length; i++)
            {
                uint streamIndex = stream ? U32(chunk, pos) : 0;
                if (stream) pos += 4;
                uint name = U32(chunk, pos);
                if (name >= (uint)chunk.Length) throw new InvalidDataException($"DXBC: signature element {i}'s name at {name} is outside its chunk.");
                int end = chunk[(int)name..].IndexOf((byte)0);
                if (end < 0) throw new InvalidDataException($"DXBC: signature element {i}'s name is not terminated.");
                uint mask = U32(chunk, pos + 20);
                elements[i] = new DxbcSignatureElement(Encoding.ASCII.GetString(chunk.Slice((int)name, end)), U32(chunk, pos + 4), U32(chunk, pos + 8), U32(chunk, pos + 12),
                    U32(chunk, pos + 16), (byte)mask, (byte)(mask >> 8), streamIndex, minPrecision ? U32(chunk, pos + 24) : 0);
                pos += minPrecision ? 28 : 24;
            }
            return elements;
        }

        static uint U32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(at, 4));
    }

    /// The shader code chunk (SHDR or SHEX) of a DXBC container: its version token, and the
    /// declarations a device binds by, read from the token stream as the Windows SDK's tokenized
    /// program format defines it (d3d12TokenizedProgramFormat.hpp in microsoft/DirectX-Headers at
    /// commit 61c7718954ec73dbdd43c723240fe1a0452a3724, which keeps D3D10/D3D11's D3D10_SB_* and
    /// D3D11_SB_* definitions):
    /// - the version token ([31:16] program type, [7:4] major, [3:0] minor) and a length token
    ///   (the program's DWORDs, both tokens included);
    /// - instructions: an opcode token ([10:0] opcode, [30:24] length in DWORDs including the
    ///   token), except a custom-data block (opcode 53), whose length is the next DWORD.
    ///
    /// Every instruction is stepped over by its length; the stream must end exactly at the
    /// program's length. Declarations are decoded in the shader model 4.0–5.0 forms (5.1 changed
    /// them; such programs are refused).
    public sealed class DxbcShader
    {
        /// D3D10_SB_TOKENIZED_PROGRAM_TYPE: 0 pixel, 1 vertex, 2 geometry, 3 hull, 4 domain, 5 compute.
        public int ProgramType { get; private init; }
        public int Major { get; private init; }
        public int Minor { get; private init; }
        /// `dcl_globalFlags`' flags as DECODE_D3D10_SB_GLOBAL_FLAGS gives them (the token masked
        /// with 0x00fff800: D3D10_SB_GLOBAL_FLAG_REFACTORING_ALLOWED is 1 &lt;&lt; 11), 0 without one.
        public uint GlobalFlags { get; private init; }
        public int Instructions { get; private init; }
        public DxbcConstantBufferDeclaration[] ConstantBuffers { get; private init; } = [];
        public DxbcResourceDeclaration[] Resources { get; private init; } = [];
        public DxbcSamplerDeclaration[] Samplers { get; private init; } = [];

        public const int PixelProgram = 0, VertexProgram = 1, GeometryProgram = 2;

        // D3D10_SB_OPCODE_TYPE values (the enum's order).
        const int CustomData = 53, DclResource = 88, DclConstantBuffer = 89, DclSampler = 90, DclGlobalFlags = 106, DclResourceRaw = 161, DclResourceStructured = 162;
        // D3D10_SB_OPERAND_TYPE values.
        const int OperandSampler = 6, OperandResource = 7, OperandConstantBuffer = 8;

        /// Throws InvalidDataException for a damaged or unsupported program.
        public static DxbcShader Read(ReadOnlySpan<byte> chunk)
        {
            try
            {
                return ReadProgram(chunk);
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                throw new InvalidDataException($"DXBC: {e.Message}", e);
            }
        }

        static DxbcShader ReadProgram(ReadOnlySpan<byte> chunk)
        {
            // As vkd3d-shader's shader_sm4_init (tpf.c, same commit): whole DWORDs, at least the
            // version and length tokens, and a length within them.
            if (chunk.Length < 8) throw new InvalidDataException($"DXBC: shader code chunk of {chunk.Length} bytes.");
            var tokens = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(chunk[..(chunk.Length / 4 * 4)]);
            uint version = tokens[0], length = tokens[1];
            if (length < 2 || length > (uint)tokens.Length) throw new InvalidDataException($"DXBC: program length {length} for a chunk of {tokens.Length} DWORDs.");
            int type = (int)(version >> 16), major = (int)((version >> 4) & 0xF), minor = (int)(version & 0xF);
            if (!((major == 4 && minor <= 1) || (major == 5 && minor == 0))) throw new InvalidDataException($"DXBC: shader model {major}.{minor} is not read (4.0, 4.1 and 5.0 are).");
            var cbs = new List<DxbcConstantBufferDeclaration>();
            var resources = new List<DxbcResourceDeclaration>();
            var samplers = new List<DxbcSamplerDeclaration>();
            uint globalFlags = 0;
            int count = 0;
            for (int pos = 2; pos < length;)
            {
                uint token = tokens[pos];
                int opcode = (int)(token & 0x7FF);
                uint size;
                if (opcode == CustomData)
                {
                    if (pos + 1 >= length) throw new InvalidDataException("DXBC: custom-data block without its length.");
                    size = tokens[pos + 1];
                    if (size < 2) throw new InvalidDataException($"DXBC: custom-data block of {size} DWORDs.");
                }
                else
                {
                    size = (token >> 24) & 0x7F;
                    if (size == 0) throw new InvalidDataException($"DXBC: instruction {count} (opcode {opcode}) of length 0.");
                }
                if (size > length - (uint)pos) throw new InvalidDataException($"DXBC: instruction {count} (opcode {opcode}) runs past the program.");
                var ins = tokens.Slice(pos, (int)size);
                switch (opcode)
                {
                    case DclConstantBuffer:
                    {
                        // Operand cb#[size]: 2D, both indices immediate (SM 4.0–5.0 form).
                        var (slot, vectors) = Operand2D(ins, OperandConstantBuffer, "dcl_constantbuffer");
                        cbs.Add(new DxbcConstantBufferDeclaration(slot, vectors, ((token >> 11) & 1) != 0));
                        break;
                    }
                    case DclResource:
                    {
                        // [15:11] dimension, [22:16] sample count; operand t#, then the return type token.
                        int slot = Operand1D(ins, OperandResource, 4, "dcl_resource");
                        int dimension = (int)((token >> 11) & 0x1F);
                        if (dimension < 1 || dimension > 10) throw new InvalidDataException($"DXBC: dcl_resource t{slot} of dimension {dimension}.");
                        resources.Add(new DxbcResourceDeclaration(slot, dimension, (int)((token >> 16) & 0x7F), ins[3], 0));
                        break;
                    }
                    case DclResourceRaw:
                        resources.Add(new DxbcResourceDeclaration(Operand1D(ins, OperandResource, 3, "dcl_resource_raw"), 11, 0, 0, 0));
                        break;
                    case DclResourceStructured:
                    {
                        int slot = Operand1D(ins, OperandResource, 4, "dcl_resource_structured");
                        if (ins[3] > int.MaxValue) throw new InvalidDataException($"DXBC: dcl_resource_structured t{slot} stride {ins[3]}.");
                        resources.Add(new DxbcResourceDeclaration(slot, 12, 0, 0, (int)ins[3]));
                        break;
                    }
                    case DclSampler:
                        samplers.Add(new DxbcSamplerDeclaration(Operand1D(ins, OperandSampler, 3, "dcl_sampler"), (int)((token >> 11) & 0xF)));
                        break;
                    case DclGlobalFlags:
                        globalFlags = token & 0x00FFF800;
                        break;
                }
                pos += (int)size;
                count++;
            }
            // No program of U60's data or mods declares a slot twice; one that did would leave the
            // slot's kind open, so it is refused.
            Unique(cbs.ConvertAll(c => c.Slot), "constant buffer");
            Unique(resources.ConvertAll(r => r.Slot), "resource");
            Unique(samplers.ConvertAll(s => s.Slot), "sampler");
            return new DxbcShader
            {
                ProgramType = type,
                Major = major,
                Minor = minor,
                GlobalFlags = globalFlags,
                Instructions = count,
                ConstantBuffers = cbs.ToArray(),
                Resources = resources.ToArray(),
                Samplers = samplers.ToArray(),
            };
        }

        /// A declaration whose instruction is the opcode token, an operand token of `type` with one
        /// immediate 32-bit index (the slot), and `length - 3` further DWORDs.
        static int Operand1D(ReadOnlySpan<uint> ins, int type, int length, string what)
        {
            if (ins.Length != length) throw new InvalidDataException($"DXBC: {what} of {ins.Length} DWORDs, not {length}.");
            CheckOperand(ins[1], type, 1, what);
            return Index(ins[2], what);
        }

        static (int, int) Operand2D(ReadOnlySpan<uint> ins, int type, string what)
        {
            if (ins.Length != 4) throw new InvalidDataException($"DXBC: {what} of {ins.Length} DWORDs, not 4.");
            CheckOperand(ins[1], type, 2, what);
            return (Index(ins[2], what), Index(ins[3], what));
        }

        /// OperandToken0: [19:12] operand type, [21:20] index dimension, [24:22], [27:25] and
        /// [30:28] each index's representation (0: an immediate 32-bit DWORD), [31] extended (no
        /// declaration is extended, by the format's comments).
        static void CheckOperand(uint operand, int type, int dimension, string what)
        {
            if (((operand >> 12) & 0xFF) != type) throw new InvalidDataException($"DXBC: {what} operand of type {(operand >> 12) & 0xFF}.");
            if (((operand >> 20) & 3) != dimension) throw new InvalidDataException($"DXBC: {what} operand of index dimension {(operand >> 20) & 3}.");
            if ((operand >> 22) != 0) throw new InvalidDataException($"DXBC: {what} operand with non-immediate indices or an extension (token {operand:x8}).");
        }

        static int Index(uint value, string what) => value <= int.MaxValue ? (int)value : throw new InvalidDataException($"DXBC: {what} index {value}.");

        static void Unique(List<int> slots, string what)
        {
            var seen = new HashSet<int>();
            foreach (int s in slots)
                if (!seen.Add(s)) throw new InvalidDataException($"DXBC: {what} slot {s} declared twice.");
        }
    }

    /// The DXBC checksum: MD5's compression function over the container from byte 20 (after the
    /// magic and the checksum itself), with DXBC's own final block, as vkd3d-shader computes it
    /// (libs/vkd3d-shader/checksum.c `md5_final` with VKD3D_MD5_DXBC, and dxbc.c
    /// `compute_dxbc_checksum`, commit 698cbfbd0ecfd8b2c9f07a59ceeec41cfcd34aa1): after the 0x80
    /// pad byte, the message's bit count (low 32 bits) goes in the final block's first DWORD,
    /// with the remaining bytes and pad after it, and `bits >> 2 | 1` in its last DWORD. When
    /// fewer than 8 bytes are left after the pad byte, the block is finished with zeros and
    /// compressed, and the final block holds only those two DWORDs. The digest is the four state
    /// words, little-endian.
    public static class DxbcChecksum
    {
        public static void Compute(ReadOnlySpan<byte> container, Span<byte> digest)
        {
            if (container.Length <= 20 || digest.Length != 16) throw new ArgumentException("DXBC checksum: container too small or digest not 16 bytes.");
            var data = container[20..];
            Span<uint> state = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476];
            Span<uint> block = stackalloc uint[16];
            int full = data.Length / 64 * 64;
            for (int at = 0; at < full; at += 64)
            {
                Load(data.Slice(at, 64), block);
                Transform(state, block);
            }
            var rest = data[full..];
            uint bits = unchecked((uint)data.Length * 8u);
            Span<byte> last = stackalloc byte[64];
            last.Clear();
            int count = rest.Length + 1; // with the 0x80 pad byte
            if (64 - count < 8)
            {
                rest.CopyTo(last);
                last[rest.Length] = 0x80;
                Load(last, block);
                Transform(state, block);
                last.Clear();
            }
            else
            {
                rest.CopyTo(last[4..]);
                last[4 + rest.Length] = 0x80;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(last, bits);
            BinaryPrimitives.WriteUInt32LittleEndian(last[60..], (bits >> 2) | 1);
            Load(last, block);
            Transform(state, block);
            for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(digest[(4 * i)..], state[i]);
        }

        static void Load(ReadOnlySpan<byte> bytes, Span<uint> block)
        {
            for (int i = 0; i < 16; i++) block[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 * i)..]);
        }

        static readonly uint[] K =
        [
            0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
            0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
            0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
            0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
            0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
            0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
            0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
            0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
        ];

        static readonly int[] S = [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21];

        /// MD5's compression of one 64-byte block (RFC 1321 §3.4).
        static void Transform(Span<uint> state, ReadOnlySpan<uint> x)
        {
            uint a = state[0], b = state[1], c = state[2], d = state[3];
            for (int i = 0; i < 64; i++)
            {
                uint f;
                int g;
                switch (i >> 4)
                {
                    case 0: f = (b & c) | (~b & d); g = i; break;
                    case 1: f = (d & b) | (~d & c); g = (5 * i + 1) & 15; break;
                    case 2: f = b ^ c ^ d; g = (3 * i + 5) & 15; break;
                    default: f = c ^ (b | ~d); g = (7 * i) & 15; break;
                }
                uint t = d;
                d = c;
                c = b;
                b += uint.RotateLeft(a + f + K[i] + x[g], S[(i >> 4) * 4 + (i & 3)]);
                a = t;
            }
            state[0] += a;
            state[1] += b;
            state[2] += c;
            state[3] += d;
        }
    }
}
