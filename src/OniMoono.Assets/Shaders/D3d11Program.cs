using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace OniMoono.Assets.Shaders
{
    /// One Direct3D 11 program entry (platform 4): a single stage, as a DXBC container. Its
    /// layout, measured on U60's Windows data (docs/research/d3d11/programs.md §2: all 960 entries
    /// parse to their exact end):
    /// - the 24-byte entry header GLCore, Metal and Vulkan entries have (magic 0x0C0A75BA, GPU
    ///   program type 15–22, four statistics ints whose meaning is unknown) and the stage's
    ///   keywords (a count, then length-prefixed strings padded to four bytes);
    /// - a length-prefixed payload: a 38-byte Unity prefix, then one DXBC container that ends
    ///   the payload exactly;
    /// - padding to four bytes, the vertex channel mask, and a count of (channel, vertex
    ///   component) pairs (0 and none for pixel programs), as the Vulkan and Metal entries end;
    ///   nothing after them.
    ///
    /// The DXBC's program type and major shader model must be the ones the GPU program type
    /// names (measured: they are in all 960).
    ///
    /// Of the prefix only bytes 1–3 have a measured meaning: the program's SRV (t#), constant
    /// buffer (b#) and sampler (s#) slot counts (960/960; D3d11Bindings.Check compares them with
    /// the DXBC declarations). Byte 0 (2 in every entry) and bytes 4–37 (zero in all but the
    /// geometry program) are kept as raw, unknown values.
    public sealed class D3d11Program
    {
        public const int PrefixBytes = 38;

        public int GpuProgramType { get; private init; }
        /// The header's four statistics ints (meaning unknown; programs.md §2).
        public int[] Statistics { get; private init; } = [];
        public string[] Keywords { get; private init; } = [];
        /// The 38-byte Unity prefix, raw.
        public byte[] Prefix { get; private init; } = [];
        public int PrefixResourceCount => Prefix[1];
        public int PrefixConstantBufferCount => Prefix[2];
        public int PrefixSamplerCount => Prefix[3];
        /// The DXBC container, checked and unmodified (its Bytes are what the device creates the
        /// shader from).
        public DxbcContainer Dxbc { get; private init; } = null!;
        public uint ChannelMask { get; private init; }
        public (int Channel, int Component)[] Inputs { get; private init; } = [];

        /// Throws InvalidDataException for an entry of another layout or a damaged one.
        public static D3d11Program Read(ReadOnlySpan<byte> entry)
        {
            try
            {
                return ReadEntry(entry);
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                throw new InvalidDataException($"D3D11 program: {e.Message}", e);
            }
        }

        static D3d11Program ReadEntry(ReadOnlySpan<byte> entry)
        {
            int pos = 0;
            int Int(ReadOnlySpan<byte> d)
            {
                if (pos < 0 || pos > d.Length - 4) throw new InvalidDataException("D3D11 program ends early.");
                int v = BinaryPrimitives.ReadInt32LittleEndian(d[pos..]);
                pos += 4;
                return v;
            }
            if ((uint)Int(entry) != MetalProgram.EntryMagic) throw new InvalidDataException("Not a shader program entry.");
            int type = Int(entry);
            if (!GpuProgramTypes.IsD3D11(type)) throw new InvalidDataException($"Shader program entry of GPU program type {type} is not a D3D11 program.");
            var statistics = new[] { Int(entry), Int(entry), Int(entry), Int(entry) };
            int keywordCount = Int(entry);
            if (keywordCount < 0 || keywordCount > entry.Length / 4) throw new InvalidDataException($"D3D11 program: bad keyword count {keywordCount}.");
            var keywords = new string[keywordCount];
            for (int i = 0; i < keywordCount; i++)
            {
                int len = Int(entry);
                if (len < 0 || len > entry.Length - pos) throw new InvalidDataException("D3D11 program: bad keyword.");
                keywords[i] = Encoding.UTF8.GetString(entry.Slice(pos, len));
                pos = (int)Math.Min(entry.Length, pos + (((long)len + 3) & ~3L));
            }
            int payloadLength = Int(entry);
            if (payloadLength < PrefixBytes + DxbcContainer.HeaderBytes || payloadLength > entry.Length - pos)
                throw new InvalidDataException($"D3D11 program: bad payload length {payloadLength}.");
            var payload = entry.Slice(pos, payloadLength);
            pos = (int)Math.Min(entry.Length, pos + (((long)payloadLength + 3) & ~3L));
            var container = payload[PrefixBytes..];
            // The container's own size field must name the rest of the payload; DxbcContainer.Read
            // then checks it against the bytes it is given.
            uint total = BinaryPrimitives.ReadUInt32LittleEndian(container[24..]);
            if (total != (uint)container.Length) throw new InvalidDataException($"D3D11 program: DXBC of {total} bytes after the prefix, but {container.Length} bytes remain in the payload.");
            var dxbc = DxbcContainer.Read(container);
            var (stage, major) = StageOf(type);
            if (dxbc.Shader.ProgramType != stage || dxbc.Shader.Major != major)
                throw new InvalidDataException($"D3D11 program: GPU program type {type}, but the DXBC is program type {dxbc.Shader.ProgramType} model {dxbc.Shader.Major}.{dxbc.Shader.Minor}.");
            uint channels = (uint)Int(entry);
            int inputCount = Int(entry);
            if (inputCount < 0 || inputCount > 32) throw new InvalidDataException($"D3D11 program: bad vertex input count {inputCount}.");
            var inputs = new (int, int)[inputCount];
            for (int i = 0; i < inputCount; i++) inputs[i] = (Int(entry), Int(entry));
            if (pos != entry.Length) throw new InvalidDataException($"D3D11 program: {entry.Length - pos} bytes after the vertex inputs.");
            return new D3d11Program
            {
                GpuProgramType = type,
                Statistics = statistics,
                Keywords = keywords,
                Prefix = payload[..PrefixBytes].ToArray(),
                Dxbc = dxbc,
                ChannelMask = channels,
                Inputs = inputs,
            };
        }

        /// The DXBC program type (D3D10_SB_TOKENIZED_PROGRAM_TYPE) and shader model a GPU program
        /// type names (AssetStudio's ShaderGpuProgramType names: DX11VertexSM40 … DX11DomainSM50).
        public static (int ProgramType, int Major) StageOf(int gpuProgramType) => gpuProgramType switch
        {
            GpuProgramTypes.D3D11VertexSM40 => (DxbcShader.VertexProgram, 4),
            GpuProgramTypes.D3D11VertexSM50 => (DxbcShader.VertexProgram, 5),
            GpuProgramTypes.D3D11PixelSM40 => (DxbcShader.PixelProgram, 4),
            GpuProgramTypes.D3D11PixelSM50 => (DxbcShader.PixelProgram, 5),
            GpuProgramTypes.D3D11GeometrySM40 => (DxbcShader.GeometryProgram, 4),
            GpuProgramTypes.D3D11GeometrySM50 => (DxbcShader.GeometryProgram, 5),
            GpuProgramTypes.D3D11HullSM50 => (3, 5),
            GpuProgramTypes.D3D11DomainSM50 => (4, 5),
            _ => throw new ArgumentOutOfRangeException(nameof(gpuProgramType), gpuProgramType, "Not a D3D11 GPU program type."),
        };
    }
}
