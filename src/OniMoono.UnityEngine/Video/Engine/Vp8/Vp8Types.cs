// Port of the VP8 reference decoder ("dixie") from RFC 6386, Section 20.
// Copyright (c) 2010, 2011, Google Inc. All rights reserved. BSD-style license; see
// src/ThirdParty/rfc6386/LICENSE and PATENTS.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OniMoono.Engine.Video.Vp8
{
    /// Constants and enumerations of dixie.h, as plain integers so that the arithmetic on them
    /// reads as in the C.
    internal static class Vp8Constants
    {
        public const int MbFeatureTreeProbs = 3;
        public const int MaxMbSegments = 4;
        public const int BlockContexts = 4;
        public const int MaxPartitions = 8;

        public const int BlockTypes = 4;
        public const int PrevCoeffContexts = 3;
        public const int CoeffBands = 8;
        public const int EntropyNodes = 11;
        /// Bytes in a coeff_probs_table_t[BLOCK_TYPES][COEFF_BANDS][PREV_COEFF_CONTEXTS][ENTROPY_NODES].
        public const int CoeffProbsSize = BlockTypes * CoeffBands * PrevCoeffContexts * EntropyNodes;

        /// MV_PROB_CNT: 2 + 8 - 1 + 10 (from entropymv.h).
        public const int MvProbCnt = 2 + 8 - 1 + 10;

        // enum reference_frame
        public const int CurrentFrame = 0;
        public const int LastFrame = 1;
        public const int GoldenFrame = 2;
        public const int AltrefFrame = 3;
        public const int NumRefFrames = 4;

        // enum prediction_mode: 16x16 intra modes
        public const int DcPred = 0;
        public const int VPred = 1;
        public const int HPred = 2;
        public const int TmPred = 3;
        public const int BPred = 4;
        // 16x16 inter modes
        public const int NearestMv = 5;
        public const int NearMv = 6;
        public const int ZeroMv = 7;
        public const int NewMv = 8;
        public const int SplitMv = 9;
        public const int MbModeCount = 10;
        // 4x4 intra modes
        public const int BDcPred = 0;
        public const int BTmPred = 1;
        public const int BVePred = 2;
        public const int BHePred = 3;
        public const int BLdPred = 4;
        public const int BRdPred = 5;
        public const int BVrPred = 6;
        public const int BVlPred = 7;
        public const int BHdPred = 8;
        public const int BHuPred = 9;
        // 4x4 inter modes
        public const int Left4x4 = 10;
        public const int Above4x4 = 11;
        public const int Zero4x4 = 12;
        public const int New4x4 = 13;
        public const int BModeCount = 14;

        // enum splitmv_partitioning
        public const int SplitMv16x8 = 0;
        public const int SplitMv8x16 = 1;
        public const int SplitMv8x8 = 2;
        public const int SplitMv4x4 = 3;

        // enum token_block_type
        public const int TokenBlockY1 = 0;
        public const int TokenBlockUv = 1;
        public const int TokenBlockY2 = 2;
        public const int TokenBlockTypes = 3;

        /// A "token entropy context" (token_entropy_ctx_t) has 4 Y values, 2 U, 2 V, and 1 Y2.
        public const int TokenEntropyCtxSize = 4 + 2 + 2 + 1;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int Clamp255(int x) => x < 0 ? 0 : (x > 255 ? 255 : x);
    }

    /// struct vp8_frame_hdr (with the nested vp8_kf_hdr flattened).
    internal struct FrameHdr
    {
        public uint IsKeyframe;      // Frame is a keyframe
        public uint IsExperimental;  // Frame is a keyframe
        public uint Version;         // Bitstream version
        public uint IsShown;         // Frame is to be displayed.
        public uint Part0Sz;         // Partition 0 length, in bytes

        public uint KfW;             // Width
        public uint KfH;             // Height
        public uint KfScaleW;        // Scaling factor, Width
        public uint KfScaleH;        // Scaling factor, Height

        public uint FrameSizeUpdated; // Flag to indicate a resolution update.
    }

    /// struct vp8_segment_hdr
    internal unsafe struct SegmentHdr
    {
        public uint Enabled;
        public uint UpdateData;
        public uint UpdateMap;
        public uint Abs; // 0=deltas, 1=absolute values
        public fixed uint TreeProbs[Vp8Constants.MbFeatureTreeProbs];
        public fixed int LfLevel[Vp8Constants.MaxMbSegments];
        public fixed int QuantIdx[Vp8Constants.MaxMbSegments];
    }

    /// struct vp8_loopfilter_hdr
    internal unsafe struct LoopfilterHdr
    {
        public uint UseSimple;
        public uint Level;
        public uint Sharpness;
        public uint DeltaEnabled;
        public fixed int RefDelta[Vp8Constants.BlockContexts];
        public fixed int ModeDelta[Vp8Constants.BlockContexts];
    }

    /// struct vp8_token_hdr
    internal unsafe struct TokenHdr
    {
        public uint Partitions;
        public fixed uint PartitionSz[Vp8Constants.MaxPartitions];
    }

    /// struct vp8_quant_hdr
    internal struct QuantHdr
    {
        public uint QIndex;
        public int DeltaUpdate;
        public int Y1DcDeltaQ;
        public int Y2DcDeltaQ;
        public int Y2AcDeltaQ;
        public int UvDcDeltaQ;
        public int UvAcDeltaQ;
    }

    /// struct vp8_reference_hdr
    internal unsafe struct ReferenceHdr
    {
        public uint RefreshLast;
        public uint RefreshGf;
        public uint RefreshArf;
        public uint CopyGf;
        public uint CopyArf;
        public fixed uint SignBias[4];
        public uint RefreshEntropy;
    }

    /// struct vp8_entropy_hdr. Copied by value to save and restore the probabilities when
    /// refresh_entropy_probs is 0.
    internal unsafe struct EntropyHdr
    {
        /// coeff_probs_table_t, flattened [BLOCK_TYPES][COEFF_BANDS][PREV_COEFF_CONTEXTS][ENTROPY_NODES].
        public fixed byte CoeffProbs[Vp8Constants.CoeffProbsSize];
        /// mv_component_probs_t mv_probs[2], flattened [2][MV_PROB_CNT].
        public fixed byte MvProbs[2 * Vp8Constants.MvProbCnt];
        public uint CoeffSkipEnabled;
        public byte CoeffSkipProb;
        public fixed byte YModeProbs[4];
        public fixed byte UvModeProbs[3];
        public byte ProbInter;
        public byte ProbLast;
        public byte ProbGf;
    }

    /// union mv: int16 x, y; "raw" is the two as one 32-bit value (x in the low half).
    [StructLayout(LayoutKind.Sequential)]
    internal struct Mv
    {
        public short X;
        public short Y;

        public uint Raw
        {
            readonly get => (ushort)X | ((uint)(ushort)Y << 16);
            set
            {
                X = (short)value;
                Y = (short)(value >> 16);
            }
        }
    }

    /// struct mb_info: struct mb_base_info followed by the split union. The C bitfields are
    /// bytes here; every value stored fits its C field width.
    internal unsafe struct MbInfo
    {
        public byte YMode;          // : 4
        public byte UvMode;         // : 4
        public byte SegmentId;      // : 2
        public byte RefFrame;       // : 2
        public byte SkipCoeff;      // : 1
        public byte NeedMcBorder;   // : 1
        public byte Partitioning;   // enum splitmv_partitioning : 2
        public Mv Mv;
        public uint EobMask;

        /// union { union mv mvs[16]; enum prediction_mode modes[16]; } split; read through
        /// SplitMvs and SplitModes.
        public fixed uint Split[16];

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static Mv* SplitMvs(MbInfo* mbi) => (Mv*)mbi->Split;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int* SplitModes(MbInfo* mbi) => (int*)mbi->Split;
    }

    /// struct token_decoder
    internal unsafe struct TokenDecoder
    {
        public BoolDecoder Bool;
        public fixed int LeftTokenEntropyCtx[Vp8Constants.TokenEntropyCtxSize];
        public short* Coeffs;
    }

    /// struct dequant_factors
    internal unsafe struct DequantFactors
    {
        public int QuantIdx;
        /// short factor[TOKEN_BLOCK_TYPES][2], flattened: [ Y1, UV, Y2 ] x [ DC, AC ].
        public fixed short Factor[Vp8Constants.TokenBlockTypes * 2];
    }

    /// The parts of vpx_image_t the decoder uses: an I420 image allocated as vpx_img_alloc
    /// does (Y plane, then U, then V, in one buffer) with its display rectangle set by
    /// vpx_img_set_rect.
    internal unsafe struct Vp8Image
    {
        public byte* ImgData;
        public uint W;
        public uint H;
        public uint DW;
        public uint DH;
        public int StrideY;
        public int StrideUv;
        public byte* PlaneY;
        public byte* PlaneU;
        public byte* PlaneV;
    }

    /// struct ref_cnt_img
    internal struct RefCntImg
    {
        public Vp8Image Img;
        public uint RefCnt;
    }

    /// struct vp8_decoder_ctx. Lives in native memory; its arrays of structs are separate
    /// native allocations made with it.
    internal unsafe struct DecoderCtx
    {
        public uint FrameCnt;

        public FrameHdr FrameHdr;
        public SegmentHdr SegmentHdr;
        public LoopfilterHdr LoopfilterHdr;
        public TokenHdr TokenHdr;
        public QuantHdr QuantHdr;
        public ReferenceHdr ReferenceHdr;
        public EntropyHdr EntropyHdr;

        public EntropyHdr SavedEntropy;
        public uint SavedEntropyValid;

        public uint MbRows;
        public uint MbCols;
        public MbInfo* MbInfoStorage;
        public MbInfo** MbInfoRowsStorage;
        public MbInfo** MbInfoRows;

        /// token_entropy_ctx_t *above_token_entropy_ctx: TokenEntropyCtxSize ints per column.
        public int* AboveTokenEntropyCtx;
        /// struct token_decoder tokens[MAX_PARTITIONS]
        public TokenDecoder* Tokens;
        /// struct dequant_factors dequant_factors[MAX_MB_SEGMENTS]
        public DequantFactors* DequantFactors;

        /// struct ref_cnt_img frame_strg[NUM_REF_FRAMES]
        public RefCntImg* FrameStrg;
        /// struct ref_cnt_img *ref_frames[NUM_REF_FRAMES]
        public RefCntImg** RefFrames;
        public fixed long RefFrameOffsets[4];

        /// The subpixel filter set in use (const filter_t *subpixel_filters), copied:
        /// filter_t[8], flattened [8][6].
        public fixed short SubpixelFilters[8 * 6];

        /// Macroblock dimensions the frame buffers, mode info and token storage were last
        /// allocated for (not in the C; see Vp8Decoder.DecodeFrame).
        public uint AllocMbCols;
        public uint AllocMbRows;
    }
}
