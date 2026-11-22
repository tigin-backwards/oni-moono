namespace OniMoono.Assets.Shaders
{
    /// The values DxbcShader and DxbcContainer read the tokenized program format by, under the
    /// names of microsoft/DirectX-Headers' include/directx/D3D12TokenizedProgramFormat.hpp (commit
    /// 61c7718954ec73dbdd43c723240fe1a0452a3724, committed as
    /// src/ThirdParty/DirectX-Headers/D3D12TokenizedProgramFormat.hpp), or of the Windows SDK's
    /// d3dcommon.h for the D3D_* ones. scripts/gen-dxbc-check.py turns every constant here into a
    /// static_assert against those headers (src/OniMoono.NativeHost/dxbc_check.cpp, compiled by the
    /// Windows build and by tests/scripts/test_dxbc_check.py): a constant named as in a header must
    /// equal it, and any other name carries its header expression in a `// = EXPR` comment.
    internal static class DxbcTokenFormat
    {
        // Version token.
        public const uint D3D10_SB_TOKENIZED_PROGRAM_TYPE_MASK = 0xffff0000;
        public const int D3D10_SB_TOKENIZED_PROGRAM_TYPE_SHIFT = 16;
        public const uint D3D10_SB_TOKENIZED_PROGRAM_MAJOR_VERSION_MASK = 0xf0;
        public const int D3D10_SB_TOKENIZED_PROGRAM_MAJOR_VERSION_SHIFT = 4;
        public const uint D3D10_SB_TOKENIZED_PROGRAM_MINOR_VERSION_MASK = 0xf;
        public const int D3D10_SB_PIXEL_SHADER = 0, D3D10_SB_VERTEX_SHADER = 1, D3D10_SB_GEOMETRY_SHADER = 2;

        // Opcode token.
        public const uint D3D10_SB_OPCODE_TYPE_MASK = 0x7ff;
        public const uint D3D10_SB_TOKENIZED_INSTRUCTION_LENGTH_MASK = 0x7f000000;
        public const int D3D10_SB_TOKENIZED_INSTRUCTION_LENGTH_SHIFT = 24;
        /// The length bits vkd3d-shader does not read (its VKD3D_SM4_INSTRUCTION_LENGTH_MASK is
        /// 0x1f << 24, tpf.c at 698cbfbd) while the header gives the length as [30:24].
        public const uint LengthBitsReadersDisagreeOn = 0x60000000; // = D3D10_SB_TOKENIZED_INSTRUCTION_LENGTH_MASK & ~(0x1fu << D3D10_SB_TOKENIZED_INSTRUCTION_LENGTH_SHIFT)
        public const uint D3D10_SB_OPCODE_EXTENDED_MASK = 0x80000000;

        // Opcodes (D3D10_SB_OPCODE_TYPE).
        public const int D3D10_SB_OPCODE_CUSTOMDATA = 53;
        public const int D3D10_SB_OPCODE_DCL_RESOURCE = 88;
        public const int D3D10_SB_OPCODE_DCL_CONSTANT_BUFFER = 89;
        public const int D3D10_SB_OPCODE_DCL_SAMPLER = 90;
        public const int D3D10_SB_OPCODE_DCL_INDEX_RANGE = 91;
        public const int D3D10_SB_OPCODE_DCL_GS_OUTPUT_PRIMITIVE_TOPOLOGY = 92;
        public const int D3D10_SB_OPCODE_DCL_GS_INPUT_PRIMITIVE = 93;
        public const int D3D10_SB_OPCODE_DCL_MAX_OUTPUT_VERTEX_COUNT = 94;
        public const int D3D10_SB_OPCODE_DCL_INPUT = 95;
        public const int D3D10_SB_OPCODE_DCL_INPUT_SGV = 96;
        public const int D3D10_SB_OPCODE_DCL_INPUT_SIV = 97;
        public const int D3D10_SB_OPCODE_DCL_INPUT_PS = 98;
        public const int D3D10_SB_OPCODE_DCL_INPUT_PS_SGV = 99;
        public const int D3D10_SB_OPCODE_DCL_INPUT_PS_SIV = 100;
        public const int D3D10_SB_OPCODE_DCL_OUTPUT = 101;
        public const int D3D10_SB_OPCODE_DCL_OUTPUT_SGV = 102;
        public const int D3D10_SB_OPCODE_DCL_OUTPUT_SIV = 103;
        public const int D3D10_SB_OPCODE_DCL_TEMPS = 104;
        public const int D3D10_SB_OPCODE_DCL_INDEXABLE_TEMP = 105;
        public const int D3D10_SB_OPCODE_DCL_GLOBAL_FLAGS = 106;
        public const int D3D11_SB_OPCODE_HS_DECLS = 113;
        public const int D3D11_SB_OPCODE_DCL_STREAM = 143;
        public const int D3D11_SB_OPCODE_DCL_FUNCTION_BODY = 144;
        public const int D3D11_SB_OPCODE_DCL_FUNCTION_TABLE = 145;
        public const int D3D11_SB_OPCODE_DCL_INTERFACE = 146;
        public const int D3D11_SB_OPCODE_DCL_INPUT_CONTROL_POINT_COUNT = 147;
        public const int D3D11_SB_OPCODE_DCL_OUTPUT_CONTROL_POINT_COUNT = 148;
        public const int D3D11_SB_OPCODE_DCL_TESS_DOMAIN = 149;
        public const int D3D11_SB_OPCODE_DCL_TESS_PARTITIONING = 150;
        public const int D3D11_SB_OPCODE_DCL_TESS_OUTPUT_PRIMITIVE = 151;
        public const int D3D11_SB_OPCODE_DCL_HS_MAX_TESSFACTOR = 152;
        public const int D3D11_SB_OPCODE_DCL_HS_FORK_PHASE_INSTANCE_COUNT = 153;
        public const int D3D11_SB_OPCODE_DCL_HS_JOIN_PHASE_INSTANCE_COUNT = 154;
        public const int D3D11_SB_OPCODE_DCL_THREAD_GROUP = 155;
        public const int D3D11_SB_OPCODE_DCL_UNORDERED_ACCESS_VIEW_TYPED = 156;
        public const int D3D11_SB_OPCODE_DCL_UNORDERED_ACCESS_VIEW_RAW = 157;
        public const int D3D11_SB_OPCODE_DCL_UNORDERED_ACCESS_VIEW_STRUCTURED = 158;
        public const int D3D11_SB_OPCODE_DCL_THREAD_GROUP_SHARED_MEMORY_RAW = 159;
        public const int D3D11_SB_OPCODE_DCL_THREAD_GROUP_SHARED_MEMORY_STRUCTURED = 160;
        public const int D3D11_SB_OPCODE_DCL_RESOURCE_RAW = 161;
        public const int D3D11_SB_OPCODE_DCL_RESOURCE_STRUCTURED = 162;
        public const int D3D11_SB_OPCODE_DCL_GS_INSTANCE_COUNT = 206;

        // Custom-data blocks.
        public const uint D3D10_SB_CUSTOMDATA_CLASS_MASK = 0xfffff800;
        public const int D3D10_SB_CUSTOMDATA_CLASS_SHIFT = 11;
        public const int D3D10_SB_CUSTOMDATA_DCL_IMMEDIATE_CONSTANT_BUFFER = 3;

        // Operand token.
        public const uint D3D10_SB_OPERAND_TYPE_MASK = 0x000ff000;
        public const int D3D10_SB_OPERAND_TYPE_SHIFT = 12;
        public const uint D3D10_SB_OPERAND_INDEX_DIMENSION_MASK = 0x00300000;
        public const int D3D10_SB_OPERAND_INDEX_DIMENSION_SHIFT = 20;
        public const int D3D10_SB_OPERAND_INDEX_1D = 1, D3D10_SB_OPERAND_INDEX_2D = 2;
        /// The three indices' representation fields, [24:22], [27:25] and [30:28] (each 0,
        /// D3D10_SB_OPERAND_INDEX_IMMEDIATE32, for an immediate index).
        public const uint IndexRepresentationBits = 0x7fc00000; // = (0x7u << D3D10_SB_OPERAND_INDEX_REPRESENTATION_SHIFT(0)) | (0x7u << D3D10_SB_OPERAND_INDEX_REPRESENTATION_SHIFT(1)) | (0x7u << D3D10_SB_OPERAND_INDEX_REPRESENTATION_SHIFT(2))
        public const int D3D10_SB_OPERAND_INDEX_IMMEDIATE32 = 0;
        public const uint D3D10_SB_OPERAND_EXTENDED_MASK = 0x80000000;
        public const int D3D10_SB_OPERAND_TYPE_SAMPLER = 6, D3D10_SB_OPERAND_TYPE_RESOURCE = 7, D3D10_SB_OPERAND_TYPE_CONSTANT_BUFFER = 8;

        // Declaration fields.
        public const uint D3D10_SB_RESOURCE_DIMENSION_MASK = 0x0000f800;
        public const int D3D10_SB_RESOURCE_DIMENSION_SHIFT = 11;
        public const uint D3D10_SB_RESOURCE_SAMPLE_COUNT_MASK = 0x007f0000;
        public const int D3D10_SB_RESOURCE_SAMPLE_COUNT_SHIFT = 16;
        public const int D3D10_SB_RESOURCE_DIMENSION_TEXTURE2D = 3, D3D10_SB_RESOURCE_DIMENSION_TEXTURE2DMS = 4, D3D10_SB_RESOURCE_DIMENSION_TEXTURE3D = 5,
            D3D10_SB_RESOURCE_DIMENSION_TEXTURECUBE = 6, D3D10_SB_RESOURCE_DIMENSION_TEXTURE2DARRAY = 8, D3D10_SB_RESOURCE_DIMENSION_TEXTURE2DMSARRAY = 9,
            D3D11_SB_RESOURCE_DIMENSION_STRUCTURED_BUFFER = 12;
        public const uint D3D10_SB_SAMPLER_MODE_MASK = 0x00007800;
        public const int D3D10_SB_SAMPLER_MODE_SHIFT = 11;
        public const int D3D10_SB_SAMPLER_MODE_DEFAULT = 0, D3D10_SB_SAMPLER_MODE_COMPARISON = 1;
        public const uint D3D10_SB_CONSTANT_BUFFER_ACCESS_PATTERN_MASK = 0x00000800;
        public const int D3D10_SB_CONSTANT_BUFFER_DYNAMIC_INDEXED = 1;
        public const uint D3D10_SB_GLOBAL_FLAGS_MASK = 0x00fff800;

        // Signature values (d3dcommon.h).
        public const uint D3D_REGISTER_COMPONENT_FLOAT32 = 3;
        public const uint D3D_MIN_PRECISION_DEFAULT = 0;
    }
}
