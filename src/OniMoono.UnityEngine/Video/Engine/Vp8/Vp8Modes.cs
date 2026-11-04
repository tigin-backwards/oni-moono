// Port of the VP8 reference decoder ("dixie") from RFC 6386, Section 20.
// Copyright (c) 2010, 2011, Google Inc. All rights reserved. BSD-style license; see
// src/ThirdParty/rfc6386/LICENSE and PATENTS.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static OniMoono.Engine.Video.Vp8.Vp8Constants;

namespace OniMoono.Engine.Video.Vp8
{
    /// Macroblock modes and motion vectors (modemv.c).
    internal static unsafe class Vp8Modes
    {
        struct MvClampRect
        {
            public int ToLeft, ToRight, ToTop, ToBottom;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static Mv ClampMv(Mv raw, MvClampRect* bounds)
        {
            Mv newmv;

            newmv.X = (raw.X < bounds->ToLeft)
                      ? (short)bounds->ToLeft : raw.X;
            newmv.X = (raw.X > bounds->ToRight)
                      ? (short)bounds->ToRight : newmv.X;
            newmv.Y = (raw.Y < bounds->ToTop)
                      ? (short)bounds->ToTop : raw.Y;
            newmv.Y = (raw.Y > bounds->ToBottom)
                      ? (short)bounds->ToBottom : newmv.Y;
            return newmv;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int ReadSegmentId(BoolDecoder* bd, SegmentHdr* seg)
        {
            return BoolDecoder.Get(bd, (int)seg->TreeProbs[0]) != 0
                   ? 2 + BoolDecoder.Get(bd, (int)seg->TreeProbs[2])
                   : BoolDecoder.Get(bd, (int)seg->TreeProbs[1]);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int AboveBlockMode(MbInfo* self, MbInfo* above, int b)
        {
            if (b < 4)
            {
                switch (above->YMode)
                {
                    case DcPred:
                        return BDcPred;
                    case VPred:
                        return BVePred;
                    case HPred:
                        return BHePred;
                    case TmPred:
                        return BTmPred;
                    case BPred:
                        return MbInfo.SplitModes(above)[b + 12];
                    default:
                        throw new InvalidDataException("VP8: unexpected above macroblock mode."); // assert(0)
                }
            }

            return MbInfo.SplitModes(self)[b - 4];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int LeftBlockMode(MbInfo* self, MbInfo* left, int b)
        {
            if ((b & 3) == 0)
            {
                switch (left->YMode)
                {
                    case DcPred:
                        return BDcPred;
                    case VPred:
                        return BVePred;
                    case HPred:
                        return BHePred;
                    case TmPred:
                        return BTmPred;
                    case BPred:
                        return MbInfo.SplitModes(left)[b + 3];
                    default:
                        throw new InvalidDataException("VP8: unexpected left macroblock mode."); // assert(0)
                }
            }

            return MbInfo.SplitModes(self)[b - 1];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeKfMbMode(MbInfo* self, MbInfo* left, MbInfo* above, BoolDecoder* bd)
        {
            int yMode, uvMode;

            yMode = BoolDecoder.ReadTree(bd, Vp8Tables.KfYModeTree, Vp8Tables.KfYModeProbs);

            if (yMode == BPred)
            {
                ReadOnlySpan<byte> kfBModeProbs = Vp8Tables.KfBModeProbs;

                for (int i = 0; i < 16; i++)
                {
                    int a = AboveBlockMode(self, above, i);
                    int l = LeftBlockMode(self, left, i);
                    int b;

                    b = BoolDecoder.ReadTree(bd, Vp8Tables.BModeTree,
                                             kfBModeProbs.Slice((a * 10 + l) * 9, 9));
                    MbInfo.SplitModes(self)[i] = b;
                }
            }

            uvMode = BoolDecoder.ReadTree(bd, Vp8Tables.UvModeTree, Vp8Tables.KfUvModeProbs);

            self->YMode = (byte)yMode;
            self->UvMode = (byte)uvMode;
            self->Mv.Raw = 0;
            self->RefFrame = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeIntraMbMode(MbInfo* self, EntropyHdr* hdr, BoolDecoder* bd)
        {
            // Like decode_kf_mb_mode, but with probabilities transmitted in the bitstream and
            // no context on the above/left block mode.
            int yMode, uvMode;

            yMode = BoolDecoder.ReadTree(bd, Vp8Tables.YModeTree, hdr->YModeProbs, 4);

            if (yMode == BPred)
            {
                for (int i = 0; i < 16; i++)
                {
                    int b;

                    b = BoolDecoder.ReadTree(bd, Vp8Tables.BModeTree, Vp8Tables.DefaultBModeProbs);
                    MbInfo.SplitModes(self)[i] = b;
                }
            }

            uvMode = BoolDecoder.ReadTree(bd, Vp8Tables.UvModeTree, hdr->UvModeProbs, 3);

            self->YMode = (byte)yMode;
            self->UvMode = (byte)uvMode;
            self->Mv.Raw = 0;
            self->RefFrame = CurrentFrame;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int ReadMvComponent(BoolDecoder* bd, byte* mvc)
        {
            const int IsShort = 0, Sign = 1, Short = 2, Bits = Short + 8 - 1, LongWidth = 10;
            int x = 0;

            if (BoolDecoder.Get(bd, mvc[IsShort]) != 0) // Large
            {
                int i;

                for (i = 0; i < 3; i++)
                    x += BoolDecoder.Get(bd, mvc[Bits + i]) << i;

                // Skip bit 3, which is sometimes implicit
                for (i = LongWidth - 1; i > 3; i--)
                    x += BoolDecoder.Get(bd, mvc[Bits + i]) << i;

                if ((x & 0xFFF0) == 0 || BoolDecoder.Get(bd, mvc[Bits + 3]) != 0)
                    x += 8;
            }
            else // small
                x = BoolDecoder.ReadTree(bd, Vp8Tables.SmallMvTree, mvc + Short, 8 - 1);

            if (x != 0 && BoolDecoder.Get(bd, mvc[Sign]) != 0)
                x = -x;

            return x << 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static Mv AboveBlockMv(MbInfo* self, MbInfo* above, int b)
        {
            if (b < 4)
            {
                if (above->YMode == SplitMv)
                    return MbInfo.SplitMvs(above)[b + 12];

                return above->Mv;
            }

            return MbInfo.SplitMvs(self)[b - 4];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static Mv LeftBlockMv(MbInfo* self, MbInfo* left, int b)
        {
            if ((b & 3) == 0)
            {
                if (left->YMode == SplitMv)
                    return MbInfo.SplitMvs(left)[b + 3];

                return left->Mv;
            }

            return MbInfo.SplitMvs(self)[b - 1];
        }

        // enum subblock_mv_ref
        const int SubMvRefNormal = 0;
        const int SubMvRefLeftZed = 1;
        const int SubMvRefAboveZed = 2;
        const int SubMvRefLeftAboveSame = 3;
        const int SubMvRefLeftAboveZed = 4;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int SubMvRef(BoolDecoder* bd, Mv l, Mv a)
        {
            bool lez = l.Raw == 0;
            bool aez = a.Raw == 0;
            bool lea = l.Raw == a.Raw;
            int ctx = SubMvRefNormal;

            if (lea && lez)
                ctx = SubMvRefLeftAboveZed;
            else if (lea)
                ctx = SubMvRefLeftAboveSame;
            else if (aez)
                ctx = SubMvRefAboveZed;
            else if (lez)
                ctx = SubMvRefLeftZed;

            return BoolDecoder.ReadTree(bd, Vp8Tables.SubMvRefTree,
                                        Vp8Tables.SubMvRefProbs2.Slice(ctx * 3, 3));
        }

        /// mvc: mv_component_probs_t[2], flattened.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ReadMv(BoolDecoder* bd, Mv* mv, byte* mvc)
        {
            mv->Y = (short)ReadMvComponent(bd, mvc);
            mv->X = (short)ReadMvComponent(bd, mvc + MvProbCnt);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void MvBias(MbInfo* mb, uint* signBias, int refFrame, Mv* mv)
        {
            if ((signBias[mb->RefFrame] ^ signBias[refFrame]) != 0)
            {
                mv->X = (short)(mv->X * -1);
                mv->Y = (short)(mv->Y * -1);
            }
        }

        // enum near_mv_v
        const int CntBest = 0;
        const int CntZeroZero = 0;
        const int CntNearest = 1;
        const int CntNear = 2;
        const int CntSplitMv = 3;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FindNearMvs(MbInfo* self, MbInfo* left, MbInfo* above, uint* signBias, Mv* nearMvs, int* cnt)
        {
            MbInfo* aboveleft = above - 1;
            Mv* mv = nearMvs;
            int* cntx = cnt;

            // Zero accumulators
            mv[0].Raw = mv[1].Raw = mv[2].Raw = 0;
            cnt[0] = cnt[1] = cnt[2] = cnt[3] = 0;

            // Process above
            if (above->RefFrame != CurrentFrame)
            {
                if (above->Mv.Raw != 0)
                {
                    (++mv)->Raw = above->Mv.Raw;
                    MvBias(above, signBias, self->RefFrame, mv);
                    ++cntx;
                }

                *cntx += 2;
            }

            // Process left
            if (left->RefFrame != CurrentFrame)
            {
                if (left->Mv.Raw != 0)
                {
                    Mv thisMv;

                    thisMv = default;
                    thisMv.Raw = left->Mv.Raw;
                    MvBias(left, signBias, self->RefFrame, &thisMv);

                    if (thisMv.Raw != mv->Raw)
                    {
                        (++mv)->Raw = thisMv.Raw;
                        ++cntx;
                    }

                    *cntx += 2;
                }
                else
                    cnt[CntZeroZero] += 2;
            }

            // Process above left
            if (aboveleft->RefFrame != CurrentFrame)
            {
                if (aboveleft->Mv.Raw != 0)
                {
                    Mv thisMv;

                    thisMv = default;
                    thisMv.Raw = aboveleft->Mv.Raw;
                    MvBias(aboveleft, signBias, self->RefFrame, &thisMv);

                    if (thisMv.Raw != mv->Raw)
                    {
                        (++mv)->Raw = thisMv.Raw;
                        ++cntx;
                    }

                    *cntx += 1;
                }
                else
                    cnt[CntZeroZero] += 1;
            }

            // If we have three distinct MVs ...
            if (cnt[CntSplitMv] != 0)
            {
                // See if above-left MV can be merged with NEAREST
                if (mv->Raw == nearMvs[CntNearest].Raw)
                    cnt[CntNearest] += 1;
            }

            cnt[CntSplitMv] = ((above->YMode == SplitMv ? 1 : 0)
                               + (left->YMode == SplitMv ? 1 : 0)) * 2
                              + (aboveleft->YMode == SplitMv ? 1 : 0);

            // Swap near and nearest if necessary
            if (cnt[CntNear] > cnt[CntNearest])
            {
                int tmp;
                tmp = cnt[CntNearest];
                cnt[CntNearest] = cnt[CntNear];
                cnt[CntNear] = tmp;
                uint tmpMv = nearMvs[CntNearest].Raw;
                nearMvs[CntNearest].Raw = nearMvs[CntNear].Raw;
                nearMvs[CntNear].Raw = tmpMv;
            }

            // Use near_mvs[CNT_BEST] to store the "best" MV. Note that this storage shares the
            // same address as near_mvs[CNT_ZEROZERO].
            if (cnt[CntNearest] >= cnt[CntBest])
                nearMvs[CntBest] = nearMvs[CntNearest];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeSplitMv(MbInfo* self, MbInfo* left, MbInfo* above, EntropyHdr* hdr, Mv* bestMv, BoolDecoder* bd)
        {
            ReadOnlySpan<int> partition;
            int j, k, mask, partitionId;

            partitionId = BoolDecoder.ReadTree(bd, Vp8Tables.SplitMvTree, Vp8Tables.SplitMvProbs);
            partition = Vp8Tables.MvPartitions.Slice(partitionId * 16, 16);
            self->Partitioning = (byte)partitionId;

            for (j = 0, mask = 0; mask < 65535; j++)
            {
                Mv mv, leftMv, aboveMv;
                int subblockMode;

                // Find the first subblock in this partition.
                for (k = 0; j != partition[k]; k++)
                {
                }

                // Decode the next MV
                leftMv = LeftBlockMv(self, left, k);
                aboveMv = AboveBlockMv(self, above, k);
                subblockMode = SubMvRef(bd, leftMv, aboveMv);

                switch (subblockMode)
                {
                    case Left4x4:
                        mv = leftMv;
                        break;
                    case Above4x4:
                        mv = aboveMv;
                        break;
                    case Zero4x4:
                        mv = default;
                        break;
                    case New4x4:
                        mv = default;
                        ReadMv(bd, &mv, hdr->MvProbs);
                        mv.X = (short)(mv.X + bestMv->X);
                        mv.Y = (short)(mv.Y + bestMv->Y);
                        break;
                    default:
                        throw new InvalidDataException("VP8: unexpected sub-block MV mode."); // assert(0)
                }

                // Fill the MVs for this partition
                for (; k < 16; k++)
                    if (j == partition[k])
                    {
                        MbInfo.SplitMvs(self)[k] = mv;
                        mask |= 1 << k;
                    }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static bool NeedMcBorder(Mv mv, int l, int t, int bW, int w, int h)
        {
            int b, r;

            // Get distance to edge for top-left pixel
            l += mv.X >> 3;
            t += mv.Y >> 3;

            // Get distance to edge for bottom-right pixel
            r = w - (l + bW);
            b = h - (t + bW);

            return (l >> 1 < 2 || r >> 1 < 3 || t >> 1 < 2 || b >> 1 < 3);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeMvs(DecoderCtx* ctx, MbInfo* self, MbInfo* left, MbInfo* above, MvClampRect* bounds, BoolDecoder* bd)
        {
            const int Best = 0, Nearest = 1, Near = 2;
            EntropyHdr* hdr = &ctx->EntropyHdr;
            Mv* nearMvs = stackalloc Mv[4];
            Mv clampedBestMv;
            int* mvCnts = stackalloc int[4];
            byte* probs = stackalloc byte[4];
            int x, y, w, h, b;

            self->RefFrame = (byte)(BoolDecoder.Get(bd, hdr->ProbLast) != 0
                                    ? 2 + BoolDecoder.Get(bd, hdr->ProbGf)
                                    : 1);

            nearMvs[3] = default;
            FindNearMvs(self, self - 1, above, ctx->ReferenceHdr.SignBias, nearMvs, mvCnts);
            ReadOnlySpan<byte> countsToProbs = Vp8Tables.MvCountsToProbs;
            probs[0] = countsToProbs[mvCnts[0] * 4 + 0];
            probs[1] = countsToProbs[mvCnts[1] * 4 + 1];
            probs[2] = countsToProbs[mvCnts[2] * 4 + 2];
            probs[3] = countsToProbs[mvCnts[3] * 4 + 3];

            self->YMode = (byte)BoolDecoder.ReadTree(bd, Vp8Tables.MvRefTree, probs, 4);
            self->UvMode = self->YMode;

            self->NeedMcBorder = 0;
            x = (-bounds->ToLeft - 128) >> 3;
            y = (-bounds->ToTop - 128) >> 3;
            w = (int)ctx->MbCols * 16;
            h = (int)ctx->MbRows * 16;

            switch (self->YMode)
            {
                case NearestMv:
                    self->Mv = ClampMv(nearMvs[Nearest], bounds);
                    break;
                case NearMv:
                    self->Mv = ClampMv(nearMvs[Near], bounds);
                    break;
                case ZeroMv:
                    self->Mv.Raw = 0;
                    return; // skip need_mc_border check
                case NewMv:
                    clampedBestMv = ClampMv(nearMvs[Best], bounds);
                    ReadMv(bd, &self->Mv, hdr->MvProbs);
                    self->Mv.X = (short)(self->Mv.X + clampedBestMv.X);
                    self->Mv.Y = (short)(self->Mv.Y + clampedBestMv.Y);
                    break;
                case SplitMv:
                {
                    Mv* chromaMv = stackalloc Mv[4];
                    chromaMv[0] = chromaMv[1] = chromaMv[2] = chromaMv[3] = default;

                    clampedBestMv = ClampMv(nearMvs[Best], bounds);
                    DecodeSplitMv(self, left, above, hdr, &clampedBestMv, bd);
                    Mv* mvs = MbInfo.SplitMvs(self);
                    self->Mv = mvs[15];

                    for (b = 0; b < 16; b++)
                    {
                        Mv* c = &chromaMv[(b >> 1 & 1) + (b >> 2 & 2)];
                        c->X = (short)(c->X + mvs[b].X);
                        c->Y = (short)(c->Y + mvs[b].Y);

                        if (NeedMcBorder(mvs[b], x + (b & 3) * 4, y + (b & ~3), 4, w, h))
                        {
                            self->NeedMcBorder = 1;
                            break;
                        }
                    }

                    for (b = 0; b < 4; b++)
                    {
                        Mv* c = &chromaMv[b];
                        c->X = (short)(c->X + 4 + 8 * (c->X >> 31));
                        c->Y = (short)(c->Y + 4 + 8 * (c->Y >> 31));
                        c->X = (short)(c->X / 4);
                        c->Y = (short)(c->Y / 4);

                        // note we're passing in non-subsampled coordinates
                        if (NeedMcBorder(*c, x + (b & 1) * 8, y + (b >> 1) * 8, 16, w, h))
                        {
                            self->NeedMcBorder = 1;
                            break;
                        }
                    }

                    return; // skip need_mc_border check
                }
                default:
                    throw new InvalidDataException("VP8: unexpected inter macroblock mode."); // assert(0)
            }

            if (NeedMcBorder(self->Mv, x, y, 16, w, h))
                self->NeedMcBorder = 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ProcessRow(DecoderCtx* ctx, BoolDecoder* bd, int row, int startCol, int numCols)
        {
            MbInfo* above, self;
            int col;
            MvClampRect bounds;

            self = ctx->MbInfoRows[row] + startCol;
            above = ctx->MbInfoRows[row - 1] + startCol;

            // Calculate the eighth-pel MV bounds using a 1 MB border.
            bounds.ToLeft = -((startCol + 1) << 7);
            bounds.ToRight = ((int)ctx->MbCols - startCol) << 7;
            bounds.ToTop = -((row + 1) << 7);
            bounds.ToBottom = ((int)ctx->MbRows - row) << 7;

            for (col = startCol; col < startCol + numCols; col++)
            {
                if (ctx->SegmentHdr.UpdateMap != 0)
                    self->SegmentId = (byte)ReadSegmentId(bd, &ctx->SegmentHdr);

                if (ctx->EntropyHdr.CoeffSkipEnabled != 0)
                    self->SkipCoeff = (byte)BoolDecoder.Get(bd, ctx->EntropyHdr.CoeffSkipProb);

                if (ctx->FrameHdr.IsKeyframe != 0)
                {
                    if (ctx->SegmentHdr.UpdateMap == 0)
                        self->SegmentId = 0;

                    DecodeKfMbMode(self, self - 1, above, bd);
                }
                else
                {
                    if (BoolDecoder.Get(bd, ctx->EntropyHdr.ProbInter) != 0)
                        DecodeMvs(ctx, self, self - 1, above, &bounds, bd);
                    else
                        DecodeIntraMbMode(self, &ctx->EntropyHdr, bd);

                    bounds.ToLeft -= 16 << 3;
                    bounds.ToRight -= 16 << 3;
                }

                // Advance to next mb
                self++;
                above++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Init(DecoderCtx* ctx)
        {
            uint mbiW, mbiH, i;
            MbInfo* mbi;

            mbiW = ctx->MbCols + 1; // For left border col
            mbiH = ctx->MbRows + 1; // For above border row

            if (ctx->FrameHdr.FrameSizeUpdated != 0)
            {
                NativeMemory.Free(ctx->MbInfoStorage);
                ctx->MbInfoStorage = null;
                NativeMemory.Free(ctx->MbInfoRowsStorage);
                ctx->MbInfoRowsStorage = null;
            }

            if (ctx->MbInfoStorage == null)
                ctx->MbInfoStorage = (MbInfo*)NativeMemory.AllocZeroed(mbiW * mbiH, (nuint)sizeof(MbInfo));

            if (ctx->MbInfoRowsStorage == null)
                ctx->MbInfoRowsStorage = (MbInfo**)NativeMemory.AllocZeroed(mbiH, (nuint)sizeof(MbInfo*));

            // Set up row pointers
            mbi = ctx->MbInfoStorage + 1;

            for (i = 0; i < mbiH; i++)
            {
                ctx->MbInfoRowsStorage[i] = mbi;
                mbi += mbiW;
            }

            ctx->MbInfoRows = ctx->MbInfoRowsStorage + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Destroy(DecoderCtx* ctx)
        {
            NativeMemory.Free(ctx->MbInfoStorage);
            ctx->MbInfoStorage = null;
            NativeMemory.Free(ctx->MbInfoRowsStorage);
            ctx->MbInfoRowsStorage = null;
        }
    }
}
