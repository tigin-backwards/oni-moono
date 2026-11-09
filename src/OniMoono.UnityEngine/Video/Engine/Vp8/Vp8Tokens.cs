// Port of the VP8 reference decoder ("dixie") from RFC 6386, Section 20.
// Copyright (c) 2010, 2011, Google Inc. All rights reserved. BSD-style license; see
// src/ThirdParty/rfc6386/LICENSE and PATENTS.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static OniMoono.Engine.Video.Vp8.Vp8Constants;

namespace OniMoono.Engine.Video.Vp8
{
    /// DCT token decoding and dequantization (tokens.c). The C's macros are expanded in place;
    /// the gotos are the C's.
    internal static unsafe class Vp8Tokens
    {
        // Tree node indices
        const int EobContextNode = 0;
        const int ZeroContextNode = 1;
        const int OneContextNode = 2;
        const int LowValContextNode = 3;
        const int TwoContextNode = 4;
        const int ThreeContextNode = 5;
        const int HighLowContextNode = 6;
        const int CatOneContextNode = 7;
        const int CatThreeFourContextNode = 8;
        const int CatThreeContextNode = 9;
        const int CatFiveContextNode = 10;

        // Tokens
        const int DctValCategory1 = 5;
        const int DctValCategory2 = 6;
        const int DctValCategory3 = 7;
        const int DctValCategory4 = 8;
        const int DctValCategory5 = 9;
        const int DctValCategory6 = 10;

        /// extrabits[t].probs[bitsCount]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int ExtraBitProb(ReadOnlySpan<byte> extraBitsProbs, int t, int bitsCount) => extraBitsProbs[t * 12 + bitsCount];

        /// decode_mb_tokens. left and above are token_entropy_ctx_t; probs is the
        /// coeff_probs_table_t; factor is short[TOKEN_BLOCK_TYPES][2].
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int DecodeMbTokens(BoolDecoder* bd, int* left, int* above, short* tokens, int mode, byte* probs, short* factor)
        {
            int i, stop, type;
            int c = 0, t, v = 0;
            int val, bitsCount;
            int eobMask;
            short* bTokens;   // tokens for this block
            byte* typeProbs;  // probabilities for this block type
            byte* prob;
            short* dqf;

            ReadOnlySpan<int> leftContextIndex = Vp8Tables.LeftContextIndex;
            ReadOnlySpan<int> aboveContextIndex = Vp8Tables.AboveContextIndex;
            ReadOnlySpan<int> bandsX = Vp8Tables.BandsX;
            ReadOnlySpan<int> zigzag = Vp8Tables.Zigzag;
            ReadOnlySpan<byte> extraBitsProbs = Vp8Tables.ExtraBitsProbs;
            ReadOnlySpan<short> extraBitsMinVal = Vp8Tables.ExtraBitsMinVal;
            ReadOnlySpan<short> extraBitsLength = Vp8Tables.ExtraBitsLength;

            eobMask = 0;

            if (mode != BPred && mode != SplitMv)
            {
                i = 24;
                stop = 24;
                type = 1;
                bTokens = tokens + 24 * 16;
                dqf = factor + TokenBlockY2 * 2;
            }
            else
            {
                i = 0;
                stop = 16;
                type = 3;
                bTokens = tokens;
                dqf = factor + TokenBlockY1 * 2;
            }

            // Save a pointer to the coefficient probs for the current type. Need to repeat this
            // whenever type changes.
            typeProbs = probs + type * (CoeffBands * PrevCoeffContexts * EntropyNodes);

        BLOCK_LOOP:
            t = left[leftContextIndex[i]] + above[aboveContextIndex[i]];
            c = type == 0 ? 1 : 0; // all blocks start at 0 except type 0, which starts at 1.

            prob = typeProbs;
            prob += t * EntropyNodes;

        DO_WHILE:
            prob += bandsX[c];
            // DECODE_AND_BRANCH_IF_ZERO(prob[EOB_CONTEXT_NODE], BLOCK_FINISHED)
            if (BoolDecoder.Get(bd, prob[EobContextNode]) == 0) goto BLOCK_FINISHED;

        CHECK_0_:
            // DECODE_AND_LOOP_IF_ZERO(prob[ZERO_CONTEXT_NODE], CHECK_0_)
            if (BoolDecoder.Get(bd, prob[ZeroContextNode]) == 0)
            {
                prob = typeProbs;
                if (c < 15)
                {
                    ++c;
                    prob += bandsX[c];
                    goto CHECK_0_;
                }
                else
                    goto BLOCK_FINISHED; // for malformed input
            }
            if (BoolDecoder.Get(bd, prob[OneContextNode]) == 0) goto ONE_CONTEXT_NODE_0_;
            if (BoolDecoder.Get(bd, prob[LowValContextNode]) == 0) goto LOW_VAL_CONTEXT_NODE_0_;
            if (BoolDecoder.Get(bd, prob[HighLowContextNode]) == 0) goto HIGH_LOW_CONTEXT_NODE_0_;
            if (BoolDecoder.Get(bd, prob[CatThreeFourContextNode]) == 0) goto CAT_THREEFOUR_CONTEXT_NODE_0_;
            if (BoolDecoder.Get(bd, prob[CatFiveContextNode]) == 0) goto CAT_FIVE_CONTEXT_NODE_0_;

            val = extraBitsMinVal[DctValCategory6];
            bitsCount = extraBitsLength[DctValCategory6];

            do
            {
                val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory6, bitsCount)) << bitsCount;
                bitsCount--;
            }
            while (bitsCount >= 0);

            goto SIGN_WRITE_COEFF_AND_CHECK_EXIT;

        CAT_FIVE_CONTEXT_NODE_0_:
            val = extraBitsMinVal[DctValCategory5];
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory5, 4)) << 4;
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory5, 3)) << 3;
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory5, 2)) << 2;
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory5, 1)) << 1;
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory5, 0)) << 0;
            goto SIGN_WRITE_COEFF_AND_CHECK_EXIT;

        CAT_THREEFOUR_CONTEXT_NODE_0_:
            if (BoolDecoder.Get(bd, prob[CatThreeContextNode]) == 0) goto CAT_THREE_CONTEXT_NODE_0_;
            val = extraBitsMinVal[DctValCategory4];
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory4, 3)) << 3;
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory4, 2)) << 2;
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory4, 1)) << 1;
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory4, 0)) << 0;
            goto SIGN_WRITE_COEFF_AND_CHECK_EXIT;

        CAT_THREE_CONTEXT_NODE_0_:
            val = extraBitsMinVal[DctValCategory3];
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory3, 2)) << 2;
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory3, 1)) << 1;
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory3, 0)) << 0;
            goto SIGN_WRITE_COEFF_AND_CHECK_EXIT;

        HIGH_LOW_CONTEXT_NODE_0_:
            if (BoolDecoder.Get(bd, prob[CatOneContextNode]) == 0) goto CAT_ONE_CONTEXT_NODE_0_;

            val = extraBitsMinVal[DctValCategory2];
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory2, 1)) << 1;
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory2, 0)) << 0;
            goto SIGN_WRITE_COEFF_AND_CHECK_EXIT;

        CAT_ONE_CONTEXT_NODE_0_:
            val = extraBitsMinVal[DctValCategory1];
            val += BoolDecoder.Get(bd, ExtraBitProb(extraBitsProbs, DctValCategory1, 0)) << 0;
            goto SIGN_WRITE_COEFF_AND_CHECK_EXIT;

        LOW_VAL_CONTEXT_NODE_0_:
            if (BoolDecoder.Get(bd, prob[TwoContextNode]) == 0) goto TWO_CONTEXT_NODE_0_;
            if (BoolDecoder.Get(bd, prob[ThreeContextNode]) == 0) goto THREE_CONTEXT_NODE_0_;
            val = 4;
            goto SIGN_WRITE_COEFF_AND_CHECK_EXIT;

        THREE_CONTEXT_NODE_0_:
            val = 3;
            goto SIGN_WRITE_COEFF_AND_CHECK_EXIT;

        TWO_CONTEXT_NODE_0_:
            val = 2;
            goto SIGN_WRITE_COEFF_AND_CHECK_EXIT;

            // DECODE_SIGN_WRITE_COEFF_AND_CHECK_EXIT(val), shared by every token of value 2 or more.
        SIGN_WRITE_COEFF_AND_CHECK_EXIT:
            // DECODE_AND_APPLYSIGN(val)
            v = (BoolDecoder.GetBit(bd) != 0 ? -val : val) * dqf[c != 0 ? 1 : 0];
            prob = typeProbs + (EntropyNodes * 2);
            if (c < 15)
            {
                bTokens[zigzag[c]] = (short)v;
                ++c;
                goto DO_WHILE;
            }
            bTokens[zigzag[15]] = (short)v;
            goto BLOCK_FINISHED;

        ONE_CONTEXT_NODE_0_:
            // DECODE_AND_APPLYSIGN(1)
            v = (BoolDecoder.GetBit(bd) != 0 ? -1 : 1) * dqf[c != 0 ? 1 : 0];
            prob = typeProbs + EntropyNodes;
            if (c < 15)
            {
                bTokens[zigzag[c]] = (short)v;
                ++c;
                goto DO_WHILE;
            }
            bTokens[zigzag[15]] = (short)v;

        BLOCK_FINISHED:
            eobMask |= (c > 1 ? 1 : 0) << i;
            t = c != (type == 0 ? 1 : 0) ? 1 : 0; // any non-zero data?
            eobMask |= t << 31;

            left[leftContextIndex[i]] = above[aboveContextIndex[i]] = t;
            bTokens += 16;

            i++;

            if (i < stop)
                goto BLOCK_LOOP;

            if (i == 25)
            {
                type = 0;
                i = 0;
                stop = 16;
                typeProbs = probs + type * (CoeffBands * PrevCoeffContexts * EntropyNodes);
                bTokens = tokens;
                dqf = factor + TokenBlockY1 * 2;
                goto BLOCK_LOOP;
            }

            if (i == 16)
            {
                type = 2;
                typeProbs = probs + type * (CoeffBands * PrevCoeffContexts * EntropyNodes);
                stop = 24;
                dqf = factor + TokenBlockUv * 2;
                goto BLOCK_LOOP;
            }

            return eobMask;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ResetRowContext(int* left)
        {
            new Span<int>(left, TokenEntropyCtxSize).Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ResetAboveContext(int* above, int cols)
        {
            new Span<int>(above, cols * TokenEntropyCtxSize).Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ResetMbContext(int* left, int* above, int mode)
        {
            // Reset the macroblock context on the left and right. We have to preserve the
            // context of the second order block if this mode would not have updated it.
            new Span<int>(left, 8).Clear();
            new Span<int>(above, 8).Clear();

            if (mode != BPred && mode != SplitMv)
            {
                left[8] = 0;
                above[8] = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ProcessRow(DecoderCtx* ctx, int partition, int row, int startCol, int numCols)
        {
            TokenDecoder* tokens = &ctx->Tokens[partition];
            short* coeffs = tokens->Coeffs + 25 * 16 * startCol;
            int col;
            int* above = ctx->AboveTokenEntropyCtx + startCol * TokenEntropyCtxSize;
            int* left = tokens->LeftTokenEntropyCtx;
            MbInfo* mbi = ctx->MbInfoRows[row] + startCol;

            if (row == 0)
                ResetAboveContext(above, numCols);

            if (startCol == 0)
                ResetRowContext(left);

            for (col = startCol; col < startCol + numCols; col++)
            {
                new Span<short>(coeffs, 25 * 16).Clear();

                if (mbi->SkipCoeff != 0)
                {
                    ResetMbContext(left, above, mbi->YMode);
                    mbi->EobMask = 0;
                }
                else
                {
                    DequantFactors* dqf;

                    dqf = ctx->DequantFactors + mbi->SegmentId;
                    mbi->EobMask = (uint)DecodeMbTokens(&tokens->Bool,
                                                        left, above,
                                                        coeffs,
                                                        mbi->YMode,
                                                        ctx->EntropyHdr.CoeffProbs,
                                                        dqf->Factor);
                }

                above += TokenEntropyCtxSize;
                mbi++;
                coeffs += 25 * 16;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Init(DecoderCtx* ctx)
        {
            if (ctx->FrameHdr.FrameSizeUpdated != 0)
            {
                nuint coeffRowSz = (nuint)(ctx->MbCols * 25 * 16 * sizeof(short));

                // The C allocates only the frame's partitions here, leaving a later frame with
                // more partitions without coefficient storage; allocate them all.
                for (int i = 0; i < MaxPartitions; i++)
                {
                    NativeMemory.AlignedFree(ctx->Tokens[i].Coeffs);
                    ctx->Tokens[i].Coeffs = (short*)NativeMemory.AlignedAlloc(coeffRowSz, 16);
                }

                NativeMemory.Free(ctx->AboveTokenEntropyCtx);
                ctx->AboveTokenEntropyCtx = (int*)NativeMemory.AllocZeroed(ctx->MbCols, (nuint)(TokenEntropyCtxSize * sizeof(int)));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Destroy(DecoderCtx* ctx)
        {
            for (int i = 0; i < MaxPartitions; i++)
            {
                NativeMemory.AlignedFree(ctx->Tokens[i].Coeffs);
                ctx->Tokens[i].Coeffs = null;
            }

            NativeMemory.Free(ctx->AboveTokenEntropyCtx);
            ctx->AboveTokenEntropyCtx = null;
        }
    }
}
