// Port of the VP8 reference decoder ("dixie") from RFC 6386, Section 20.
// Copyright (c) 2010, 2011, Google Inc. All rights reserved. BSD-style license; see
// src/ThirdParty/rfc6386/LICENSE and PATENTS.

using System.Runtime.CompilerServices;
using static OniMoono.Engine.Video.Vp8.Vp8Constants;

namespace OniMoono.Engine.Video.Vp8
{
    /// The normal and simple loop filters (dixie_loopfilter.c). The C's p3..q3 macros are the
    /// pixels at -4..3 strides from the edge.
    internal static unsafe class Vp8LoopFilter
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int Abs(int x) => x >= 0 ? x : -x;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int SaturateInt8(int x)
        {
            if (x < -128)
                return -128;

            if (x > 127)
                return 127;

            return x;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int SaturateUint8(int x)
        {
            if (x < 0)
                return 0;

            if (x > 255)
                return 255;

            return x;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static bool HighEdgeVariance(byte* pixels, int stride, int hevThreshold)
        {
            int p1 = pixels[-2 * stride], p0 = pixels[-1 * stride];
            int q0 = pixels[0 * stride], q1 = pixels[1 * stride];

            return Abs(p1 - p0) > hevThreshold ||
                   Abs(q1 - q0) > hevThreshold;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static bool SimpleThreshold(byte* pixels, int stride, int filterLimit)
        {
            int p1 = pixels[-2 * stride], p0 = pixels[-1 * stride];
            int q0 = pixels[0 * stride], q1 = pixels[1 * stride];

            return (Abs(p0 - q0) * 2 + (Abs(p1 - q1) >> 1)) <= filterLimit;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static bool NormalThreshold(byte* pixels, int stride, int edgeLimit, int interiorLimit)
        {
            int p3 = pixels[-4 * stride], p2 = pixels[-3 * stride];
            int p1 = pixels[-2 * stride], p0 = pixels[-1 * stride];
            int q0 = pixels[0 * stride], q1 = pixels[1 * stride];
            int q2 = pixels[2 * stride], q3 = pixels[3 * stride];
            int e = edgeLimit;
            int i = interiorLimit;

            return SimpleThreshold(pixels, stride, 2 * e + i)
                   && Abs(p3 - p2) <= i && Abs(p2 - p1) <= i
                   && Abs(p1 - p0) <= i && Abs(q3 - q2) <= i
                   && Abs(q2 - q1) <= i && Abs(q1 - q0) <= i;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FilterCommon(byte* pixels, int stride, bool useOuterTaps)
        {
            int a, f1, f2;
            int p1 = pixels[-2 * stride], p0 = pixels[-1 * stride];
            int q0 = pixels[0 * stride], q1 = pixels[1 * stride];

            a = 3 * (q0 - p0);

            if (useOuterTaps)
                a += SaturateInt8(p1 - q1);

            a = SaturateInt8(a);

            f1 = ((a + 4 > 127) ? 127 : a + 4) >> 3;
            f2 = ((a + 3 > 127) ? 127 : a + 3) >> 3;

            pixels[-1 * stride] = (byte)SaturateUint8(p0 + f2);
            pixels[0 * stride] = (byte)SaturateUint8(q0 - f1);

            if (!useOuterTaps)
            {
                // This handles the case of subblock_filter() (from the bitstream guide.
                a = (f1 + 1) >> 1;
                pixels[-2 * stride] = (byte)SaturateUint8(p1 + a);
                pixels[1 * stride] = (byte)SaturateUint8(q1 - a);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FilterMbEdge(byte* pixels, int stride)
        {
            int w, a;
            int p2 = pixels[-3 * stride];
            int p1 = pixels[-2 * stride], p0 = pixels[-1 * stride];
            int q0 = pixels[0 * stride], q1 = pixels[1 * stride];
            int q2 = pixels[2 * stride];

            w = SaturateInt8(SaturateInt8(p1 - q1) + 3 * (q0 - p0));

            a = (27 * w + 63) >> 7;
            pixels[-1 * stride] = (byte)SaturateUint8(p0 + a);
            pixels[0 * stride] = (byte)SaturateUint8(q0 - a);

            a = (18 * w + 63) >> 7;
            pixels[-2 * stride] = (byte)SaturateUint8(p1 + a);
            pixels[1 * stride] = (byte)SaturateUint8(q1 - a);

            a = (9 * w + 63) >> 7;
            pixels[-3 * stride] = (byte)SaturateUint8(p2 + a);
            pixels[2 * stride] = (byte)SaturateUint8(q2 - a);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FilterMbVEdge(byte* src, int stride, int edgeLimit, int interiorLimit, int hevThreshold, int size)
        {
            int i;

            for (i = 0; i < 8 * size; i++)
            {
                if (NormalThreshold(src, 1, edgeLimit, interiorLimit))
                {
                    if (HighEdgeVariance(src, 1, hevThreshold))
                        FilterCommon(src, 1, true);
                    else
                        FilterMbEdge(src, 1);
                }

                src += stride;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FilterSubblockVEdge(byte* src, int stride, int edgeLimit, int interiorLimit, int hevThreshold, int size)
        {
            int i;

            for (i = 0; i < 8 * size; i++)
            {
                if (NormalThreshold(src, 1, edgeLimit, interiorLimit))
                    FilterCommon(src, 1,
                                 HighEdgeVariance(src, 1, hevThreshold));

                src += stride;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FilterMbHEdge(byte* src, int stride, int edgeLimit, int interiorLimit, int hevThreshold, int size)
        {
            int i;

            for (i = 0; i < 8 * size; i++)
            {
                if (NormalThreshold(src, stride, edgeLimit,
                                    interiorLimit))
                {
                    if (HighEdgeVariance(src, stride, hevThreshold))
                        FilterCommon(src, stride, true);
                    else
                        FilterMbEdge(src, stride);
                }

                src += 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FilterSubblockHEdge(byte* src, int stride, int edgeLimit, int interiorLimit, int hevThreshold, int size)
        {
            int i;

            for (i = 0; i < 8 * size; i++)
            {
                if (NormalThreshold(src, stride, edgeLimit,
                                    interiorLimit))
                    FilterCommon(src, stride,
                                 HighEdgeVariance(src, stride,
                                                  hevThreshold));

                src += 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FilterVEdgeSimple(byte* src, int stride, int filterLimit)
        {
            int i;

            for (i = 0; i < 16; i++)
            {
                if (SimpleThreshold(src, 1, filterLimit))
                    FilterCommon(src, 1, true);

                src += stride;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FilterHEdgeSimple(byte* src, int stride, int filterLimit)
        {
            int i;

            for (i = 0; i < 16; i++)
            {
                if (SimpleThreshold(src, stride, filterLimit))
                    FilterCommon(src, stride, true);

                src += 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void CalculateFilterParameters(DecoderCtx* ctx, MbInfo* mbi, int* edgeLimitOut, int* interiorLimitOut, int* hevThresholdOut)
        {
            int filterLevel, interiorLimit, hevThreshold;

            // Reference code/spec seems to conflate filter_level and edge_limit
            filterLevel = (int)ctx->LoopfilterHdr.Level;

            if (ctx->SegmentHdr.Enabled != 0)
            {
                if (ctx->SegmentHdr.Abs == 0)
                    filterLevel +=
                        ctx->SegmentHdr.LfLevel[mbi->SegmentId];
                else
                    filterLevel =
                        ctx->SegmentHdr.LfLevel[mbi->SegmentId];
            }

            if (filterLevel > 63)
                filterLevel = 63;
            else if (filterLevel < 0)
                filterLevel = 0;

            if (ctx->LoopfilterHdr.DeltaEnabled != 0)
            {
                filterLevel +=
                    ctx->LoopfilterHdr.RefDelta[mbi->RefFrame];

                if (mbi->RefFrame == CurrentFrame)
                {
                    if (mbi->YMode == BPred)
                        filterLevel += ctx->LoopfilterHdr.ModeDelta[0];
                }
                else if (mbi->YMode == ZeroMv)
                    filterLevel += ctx->LoopfilterHdr.ModeDelta[1];
                else if (mbi->YMode == SplitMv)
                    filterLevel += ctx->LoopfilterHdr.ModeDelta[3];
                else
                    filterLevel += ctx->LoopfilterHdr.ModeDelta[2];
            }

            if (filterLevel > 63)
                filterLevel = 63;
            else if (filterLevel < 0)
                filterLevel = 0;

            interiorLimit = filterLevel;

            if (ctx->LoopfilterHdr.Sharpness != 0)
            {
                interiorLimit >>= ctx->LoopfilterHdr.Sharpness > 4 ? 2 : 1;

                if (interiorLimit > 9 - (int)ctx->LoopfilterHdr.Sharpness)
                    interiorLimit = 9 - (int)ctx->LoopfilterHdr.Sharpness;
            }

            if (interiorLimit < 1)
                interiorLimit = 1;

            hevThreshold = filterLevel >= 15 ? 1 : 0;

            if (filterLevel >= 40)
                hevThreshold++;

            if (filterLevel >= 20 && ctx->FrameHdr.IsKeyframe == 0)
                hevThreshold++;

            *edgeLimitOut = filterLevel;
            *interiorLimitOut = interiorLimit;
            *hevThresholdOut = hevThreshold;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FilterRowNormal(DecoderCtx* ctx, int row, int startCol, int numCols)
        {
            byte* y, u, v;
            int stride, uvStride;
            MbInfo* mbi;
            int col;
            Vp8Image* current = &ctx->RefFrames[CurrentFrame]->Img;

            // Adjust pointers based on row, start_col
            stride = current->StrideY;
            uvStride = current->StrideUv;
            y = current->PlaneY;
            u = current->PlaneU;
            v = current->PlaneV;
            y += (stride * row + startCol) * 16;
            u += (uvStride * row + startCol) * 8;
            v += (uvStride * row + startCol) * 8;
            mbi = ctx->MbInfoRows[row] + startCol;

            for (col = startCol; col < startCol + numCols; col++)
            {
                int edgeLimit, interiorLimit, hevThreshold;

                // TODO: Only need to recalculate every MB if segmentation is enabled.
                CalculateFilterParameters(ctx, mbi, &edgeLimit,
                                          &interiorLimit, &hevThreshold);

                if (edgeLimit != 0)
                {
                    if (col != 0)
                    {
                        FilterMbVEdge(y, stride, edgeLimit + 2,
                                      interiorLimit, hevThreshold, 2);
                        FilterMbVEdge(u, uvStride, edgeLimit + 2,
                                      interiorLimit, hevThreshold, 1);
                        FilterMbVEdge(v, uvStride, edgeLimit + 2,
                                      interiorLimit, hevThreshold, 1);
                    }

                    // NOTE: This conditional is actually dependent on the number of
                    // coefficients decoded, not the skip flag as coded in the bitstream. The
                    // tokens task is expected to set 31 if there is *any* non-zero data.
                    if (mbi->EobMask != 0
                        || mbi->YMode == SplitMv
                        || mbi->YMode == BPred)
                    {
                        FilterSubblockVEdge(y + 4, stride, edgeLimit,
                                            interiorLimit, hevThreshold,
                                            2);
                        FilterSubblockVEdge(y + 8, stride, edgeLimit,
                                            interiorLimit, hevThreshold,
                                            2);
                        FilterSubblockVEdge(y + 12, stride, edgeLimit,
                                            interiorLimit, hevThreshold,
                                            2);
                        FilterSubblockVEdge(u + 4, uvStride, edgeLimit,
                                            interiorLimit, hevThreshold,
                                            1);
                        FilterSubblockVEdge(v + 4, uvStride, edgeLimit,
                                            interiorLimit, hevThreshold,
                                            1);
                    }

                    if (row != 0)
                    {
                        FilterMbHEdge(y, stride, edgeLimit + 2,
                                      interiorLimit, hevThreshold, 2);
                        FilterMbHEdge(u, uvStride, edgeLimit + 2,
                                      interiorLimit, hevThreshold, 1);
                        FilterMbHEdge(v, uvStride, edgeLimit + 2,
                                      interiorLimit, hevThreshold, 1);
                    }

                    if (mbi->EobMask != 0
                        || mbi->YMode == SplitMv
                        || mbi->YMode == BPred)
                    {
                        FilterSubblockHEdge(y + 4 * stride, stride,
                                            edgeLimit, interiorLimit,
                                            hevThreshold, 2);
                        FilterSubblockHEdge(y + 8 * stride, stride,
                                            edgeLimit, interiorLimit,
                                            hevThreshold, 2);
                        FilterSubblockHEdge(y + 12 * stride, stride,
                                            edgeLimit, interiorLimit,
                                            hevThreshold, 2);
                        FilterSubblockHEdge(u + 4 * uvStride, uvStride,
                                            edgeLimit, interiorLimit,
                                            hevThreshold, 1);
                        FilterSubblockHEdge(v + 4 * uvStride, uvStride,
                                            edgeLimit, interiorLimit,
                                            hevThreshold, 1);
                    }
                }

                y += 16;
                u += 8;
                v += 8;
                mbi++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FilterRowSimple(DecoderCtx* ctx, int row, int startCol, int numCols)
        {
            byte* y;
            int stride;
            MbInfo* mbi;
            int col;
            Vp8Image* current = &ctx->RefFrames[CurrentFrame]->Img;

            // Adjust pointers based on row, start_col
            stride = current->StrideY;
            y = current->PlaneY;
            y += (stride * row + startCol) * 16;
            mbi = ctx->MbInfoRows[row] + startCol;

            for (col = startCol; col < startCol + numCols; col++)
            {
                int edgeLimit, interiorLimit, hevThreshold;

                // TODO: Only need to recalculate every MB if segmentation is enabled.
                CalculateFilterParameters(ctx, mbi, &edgeLimit,
                                          &interiorLimit, &hevThreshold);

                if (edgeLimit != 0)
                {
                    // NOTE: This conditional is actually dependent on the number of
                    // coefficients decoded, not the skip flag as coded in the bitstream. The
                    // tokens task is expected to set 31 if there is *any* non-zero data.
                    bool filterSubblocks = mbi->EobMask != 0
                                           || mbi->YMode == SplitMv
                                           || mbi->YMode == BPred;
                    int mbLimit = (edgeLimit + 2) * 2 + interiorLimit;
                    int bLimit = edgeLimit * 2 + interiorLimit;

                    if (col != 0)
                        FilterVEdgeSimple(y, stride, mbLimit);

                    if (filterSubblocks)
                    {
                        FilterVEdgeSimple(y + 4, stride, bLimit);
                        FilterVEdgeSimple(y + 8, stride, bLimit);
                        FilterVEdgeSimple(y + 12, stride, bLimit);
                    }

                    if (row != 0)
                        FilterHEdgeSimple(y, stride, mbLimit);

                    if (filterSubblocks)
                    {
                        FilterHEdgeSimple(y + 4 * stride, stride,
                                          bLimit);
                        FilterHEdgeSimple(y + 8 * stride, stride,
                                          bLimit);
                        FilterHEdgeSimple(y + 12 * stride, stride,
                                          bLimit);
                    }
                }

                y += 16;
                mbi++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ProcessRow(DecoderCtx* ctx, int row, int startCol, int numCols)
        {
            if (ctx->LoopfilterHdr.UseSimple != 0)
                FilterRowSimple(ctx, row, startCol, numCols);
            else
                FilterRowNormal(ctx, row, startCol, numCols);
        }
    }
}
