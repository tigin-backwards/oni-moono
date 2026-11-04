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
    /// Intra and inter prediction with reconstruction, the frame buffers and their reference
    /// counts (predict.c).
    internal static unsafe class Vp8Predict
    {
        const int BorderPixels = 16;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictHNxN(byte* predict, int stride, int n)
        {
            byte* left = predict - 1;
            int i, j;

            for (i = 0; i < n; i++)
                for (j = 0; j < n; j++)
                    predict[i * stride + j] = left[i * stride];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictVNxN(byte* predict, int stride, int n)
        {
            byte* above = predict - stride;
            int i, j;

            for (i = 0; i < n; i++)
                for (j = 0; j < n; j++)
                    predict[i * stride + j] = above[j];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictTmNxN(byte* predict, int stride, int n)
        {
            // Transposes the left column to the top row for later consumption by the
            // idct/recon stage
            byte* left = predict - 1;
            byte* above = predict - stride;
            byte p = above[-1];
            int i, j;

            for (j = 0; j < n; j++)
            {
                for (i = 0; i < n; i++)
                    predict[i] = (byte)Clamp255(*left + above[i] - p);

                predict += stride;
                left += stride;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictDcNxN(byte* predict, int stride, int n)
        {
            byte* left = predict - 1;
            byte* above = predict - stride;
            int i, j, dc = 0;

            for (i = 0; i < n; i++)
            {
                dc += *left + above[i];
                left += stride;
            }

            switch (n)
            {
                case 16:
                    dc = (dc + 16) >> 5;
                    break;
                case 8:
                    dc = (dc + 8) >> 4;
                    break;
                case 4:
                    dc = (dc + 4) >> 3;
                    break;
            }

            for (i = 0; i < n; i++)
                for (j = 0; j < n; j++)
                    predict[i * stride + j] = (byte)dc;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictVe4x4(byte* predict, int stride)
        {
            byte* above = predict - stride;
            int i, j;

            predict[0] = (byte)((above[-1] + 2 * above[0] + above[1] + 2) >> 2);
            predict[1] = (byte)((above[0] + 2 * above[1] + above[2] + 2) >> 2);
            predict[2] = (byte)((above[1] + 2 * above[2] + above[3] + 2) >> 2);
            predict[3] = (byte)((above[2] + 2 * above[3] + above[4] + 2) >> 2);

            for (i = 1; i < 4; i++)
                for (j = 0; j < 4; j++)
                    predict[i * stride + j] = predict[j];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictHe4x4(byte* predict, int stride)
        {
            byte* left = predict - 1;

            predict[0] =
            predict[1] =
            predict[2] =
            predict[3] = (byte)((left[-stride] + 2 * left[0] +
                                 left[stride] + 2) >> 2);
            predict += stride;
            left += stride;

            predict[0] =
            predict[1] =
            predict[2] =
            predict[3] = (byte)((left[-stride] + 2 * left[0] +
                                 left[stride] + 2) >> 2);
            predict += stride;
            left += stride;

            predict[0] =
            predict[1] =
            predict[2] =
            predict[3] = (byte)((left[-stride] + 2 * left[0] +
                                 left[stride] + 2) >> 2);
            predict += stride;
            left += stride;

            predict[0] =
            predict[1] =
            predict[2] =
            predict[3] = (byte)((left[-stride] + 2 * left[0] + left[0] + 2) >> 2);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictLd4x4(byte* predict, int stride)
        {
            byte* above = predict - stride;
            int pred0, pred1, pred2, pred3, pred4, pred5, pred6;

            predict[0] = (byte)(pred0 = (above[0] + 2 * above[1] +
                                         above[2] + 2) >> 2);
            predict[1] = (byte)(pred1 = (above[1] + 2 * above[2] +
                                         above[3] + 2) >> 2);
            predict[2] = (byte)(pred2 = (above[2] + 2 * above[3] +
                                         above[4] + 2) >> 2);
            predict[3] = (byte)(pred3 = (above[3] + 2 * above[4] +
                                         above[5] + 2) >> 2);
            predict += stride;

            predict[0] = (byte)pred1;
            predict[1] = (byte)pred2;
            predict[2] = (byte)pred3;
            predict[3] = (byte)(pred4 = (above[4] + 2 * above[5] +
                                         above[6] + 2) >> 2);
            predict += stride;

            predict[0] = (byte)pred2;
            predict[1] = (byte)pred3;
            predict[2] = (byte)pred4;
            predict[3] = (byte)(pred5 = (above[5] + 2 * above[6] +
                                         above[7] + 2) >> 2);
            predict += stride;

            predict[0] = (byte)pred3;
            predict[1] = (byte)pred4;
            predict[2] = (byte)pred5;
            predict[3] = (byte)(pred6 = (above[6] + 2 * above[7] +
                                         above[7] + 2) >> 2);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictRd4x4(byte* predict, int stride)
        {
            byte* left = predict - 1;
            byte* above = predict - stride;
            int pred0, pred1, pred2, pred3, pred4, pred5, pred6;

            predict[0] = (byte)(pred0 =
                (left[0] + 2 * above[-1] + above[0] + 2) >> 2);
            predict[1] = (byte)(pred1 =
                (above[-1] + 2 * above[0] + above[1] + 2) >> 2);
            predict[2] = (byte)(pred2 =
                (above[0] + 2 * above[1] + above[2] + 2) >> 2);
            predict[3] = (byte)(pred3 =
                (above[1] + 2 * above[2] + above[3] + 2) >> 2);
            predict += stride;

            predict[0] = (byte)(pred4 =
                (left[stride] + 2 * left[0] + above[-1] + 2) >> 2);
            predict[1] = (byte)pred0;
            predict[2] = (byte)pred1;
            predict[3] = (byte)pred2;
            predict += stride;

            predict[0] = (byte)(pred5 =
                (left[stride * 2] + 2 * left[stride] + left[0] + 2) >> 2);
            predict[1] = (byte)pred4;
            predict[2] = (byte)pred0;
            predict[3] = (byte)pred1;
            predict += stride;

            predict[0] = (byte)(pred6 = (left[stride * 3] + 2 * left[stride * 2] +
                                         left[stride] + 2) >> 2);
            predict[1] = (byte)pred5;
            predict[2] = (byte)pred4;
            predict[3] = (byte)pred0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictVr4x4(byte* predict, int stride)
        {
            byte* left = predict - 1;
            byte* above = predict - stride;
            int pred0, pred1, pred2, pred3, pred4, pred5, pred6,
                pred7, pred8, pred9;

            predict[0] = (byte)(pred0 = (above[-1] + above[0] + 1) >> 1);
            predict[1] = (byte)(pred1 = (above[0] + above[1] + 1) >> 1);
            predict[2] = (byte)(pred2 = (above[1] + above[2] + 1) >> 1);
            predict[3] = (byte)(pred3 = (above[2] + above[3] + 1) >> 1);
            predict += stride;

            predict[0] = (byte)(pred4 = (left[0] + 2 * above[-1] +
                                         above[0] + 2) >> 2);
            predict[1] = (byte)(pred5 = (above[-1] + 2 * above[0] +
                                         above[1] + 2) >> 2);
            predict[2] = (byte)(pred6 = (above[0] + 2 * above[1] +
                                         above[2] + 2) >> 2);
            predict[3] = (byte)(pred7 = (above[1] + 2 * above[2] +
                                         above[3] + 2) >> 2);
            predict += stride;

            predict[0] = (byte)(pred8 =
                (left[stride] + 2 * left[0] + above[-1] + 2) >> 2);
            predict[1] = (byte)pred0;
            predict[2] = (byte)pred1;
            predict[3] = (byte)pred2;
            predict += stride;

            predict[0] = (byte)(pred9 =
                (left[stride * 2] + 2 * left[stride] + left[0] + 2) >> 2);
            predict[1] = (byte)pred4;
            predict[2] = (byte)pred5;
            predict[3] = (byte)pred6;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictVl4x4(byte* predict, int stride)
        {
            byte* above = predict - stride;
            int pred0, pred1, pred2, pred3, pred4, pred5, pred6,
                pred7, pred8, pred9;

            predict[0] = (byte)(pred0 = (above[0] + above[1] + 1) >> 1);
            predict[1] = (byte)(pred1 = (above[1] + above[2] + 1) >> 1);
            predict[2] = (byte)(pred2 = (above[2] + above[3] + 1) >> 1);
            predict[3] = (byte)(pred3 = (above[3] + above[4] + 1) >> 1);
            predict += stride;

            predict[0] = (byte)(pred4 = (above[0] + 2 * above[1] +
                                         above[2] + 2) >> 2);
            predict[1] = (byte)(pred5 = (above[1] + 2 * above[2] +
                                         above[3] + 2) >> 2);
            predict[2] = (byte)(pred6 = (above[2] + 2 * above[3] +
                                         above[4] + 2) >> 2);
            predict[3] = (byte)(pred7 = (above[3] + 2 * above[4] +
                                         above[5] + 2) >> 2);
            predict += stride;

            predict[0] = (byte)pred1;
            predict[1] = (byte)pred2;
            predict[2] = (byte)pred3;
            predict[3] = (byte)(pred8 = (above[4] + 2 * above[5] +
                                         above[6] + 2) >> 2);
            predict += stride;

            predict[0] = (byte)pred5;
            predict[1] = (byte)pred6;
            predict[2] = (byte)pred7;
            predict[3] = (byte)(pred9 = (above[5] + 2 * above[6] +
                                         above[7] + 2) >> 2);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictHd4x4(byte* predict, int stride)
        {
            byte* left = predict - 1;
            byte* above = predict - stride;
            int pred0, pred1, pred2, pred3, pred4, pred5, pred6,
                pred7, pred8, pred9;

            predict[0] = (byte)(pred0 = (left[0] + above[-1] + 1) >> 1);
            predict[1] = (byte)(pred1 = (left[0] + 2 * above[-1] +
                                         above[0] + 2) >> 2);
            predict[2] = (byte)(pred2 = (above[-1] + 2 * above[0] +
                                         above[1] + 2) >> 2);
            predict[3] = (byte)(pred3 = (above[0] + 2 * above[1] +
                                         above[2] + 2) >> 2);
            predict += stride;

            predict[0] = (byte)(pred4 = (left[stride] + left[0] + 1) >> 1);
            predict[1] = (byte)(pred5 = (left[stride] + 2 * left[0] +
                                         above[-1] + 2) >> 2);
            predict[2] = (byte)pred0;
            predict[3] = (byte)pred1;
            predict += stride;

            predict[0] = (byte)(pred6 = (left[stride * 2] + left[stride] + 1) >> 1);
            predict[1] = (byte)(pred7 = (left[stride * 2] + 2 * left[stride] +
                                         left[0] + 2) >> 2);
            predict[2] = (byte)pred4;
            predict[3] = (byte)pred5;
            predict += stride;

            predict[0] = (byte)(pred8 = (left[stride * 3] + left[stride * 2] + 1) >> 1);
            predict[1] = (byte)(pred9 = (left[stride * 3] + 2 * left[stride * 2] +
                                         left[stride] + 2) >> 2);
            predict[2] = (byte)pred6;
            predict[3] = (byte)pred7;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictHu4x4(byte* predict, int stride)
        {
            byte* left = predict - 1;
            int pred0, pred1, pred2, pred3, pred4, pred5, pred6;

            predict[0] = (byte)(pred0 = (left[stride * 0] +
                                         left[stride * 1] + 1) >> 1);
            predict[1] = (byte)(pred1 = (left[stride * 0] + 2 * left[stride * 1] +
                                         left[stride * 2] + 2) >> 2);
            predict[2] = (byte)(pred2 = (left[stride * 1] + left[stride * 2] + 1) >> 1);
            predict[3] = (byte)(pred3 = (left[stride * 1] + 2 * left[stride * 2] +
                                         left[stride * 3] + 2) >> 2);
            predict += stride;

            predict[0] = (byte)pred2;
            predict[1] = (byte)pred3;
            predict[2] = (byte)(pred4 = (left[stride * 2] + left[stride * 3] + 1) >> 1);
            predict[3] = (byte)(pred5 = (left[stride * 2] + 2 * left[stride * 3] +
                                         left[stride * 3] + 2) >> 2);
            predict += stride;

            predict[0] = (byte)pred4;
            predict[1] = (byte)pred5;
            predict[2] = (byte)(pred6 = left[stride * 3]);
            predict[3] = (byte)pred6;
            predict += stride;

            predict[0] = (byte)pred6;
            predict[1] = (byte)pred6;
            predict[2] = (byte)pred6;
            predict[3] = (byte)pred6;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictH16x16(byte* predict, int stride) => PredictHNxN(predict, stride, 16);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictV16x16(byte* predict, int stride) => PredictVNxN(predict, stride, 16);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictTm16x16(byte* predict, int stride) => PredictTmNxN(predict, stride, 16);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictH8x8(byte* predict, int stride) => PredictHNxN(predict, stride, 8);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictV8x8(byte* predict, int stride) => PredictVNxN(predict, stride, 8);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictTm8x8(byte* predict, int stride) => PredictTmNxN(predict, stride, 8);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictTm4x4(byte* predict, int stride) => PredictTmNxN(predict, stride, 4);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void CopyDown(byte* recon, int stride)
        {
            // Copy the four pixels above-right of subblock 3 to above-right of subblocks 7,
            // 11, and 15
            uint tmp;
            uint* copy = (uint*)(recon + 16 - stride);

            stride = stride / sizeof(uint);
            tmp = *copy;
            copy += stride * 4;
            *copy = tmp;
            copy += stride * 4;
            *copy = tmp;
            copy += stride * 4;
            *copy = tmp;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void BPredict(byte* predict, int stride, MbInfo* mbi, short* coeffs)
        {
            int i;
            int* modes = MbInfo.SplitModes(mbi);

            CopyDown(predict, stride);

            for (i = 0; i < 16; i++)
            {
                byte* bPredict = predict + (i & 3) * 4;

                switch (modes[i])
                {
                    case BDcPred:
                        PredictDcNxN(bPredict, stride, 4);
                        break;
                    case BTmPred:
                        PredictTm4x4(bPredict, stride);
                        break;
                    case BVePred:
                        PredictVe4x4(bPredict, stride);
                        break;
                    case BHePred:
                        PredictHe4x4(bPredict, stride);
                        break;
                    case BLdPred:
                        PredictLd4x4(bPredict, stride);
                        break;
                    case BRdPred:
                        PredictRd4x4(bPredict, stride);
                        break;
                    case BVrPred:
                        PredictVr4x4(bPredict, stride);
                        break;
                    case BVlPred:
                        PredictVl4x4(bPredict, stride);
                        break;
                    case BHdPred:
                        PredictHd4x4(bPredict, stride);
                        break;
                    case BHuPred:
                        PredictHu4x4(bPredict, stride);
                        break;
                    default:
                        throw new InvalidDataException("VP8: unexpected sub-block intra mode."); // assert(0)
                }

                Vp8Idct.IdctAdd(bPredict, bPredict, stride, coeffs);
                coeffs += 16;

                if ((i & 3) == 3)
                {
                    predict += stride * 4;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FixupDcCoeffs(MbInfo* mbi, short* coeffs)
        {
            short* y2 = stackalloc short[16];
            int i;

            Vp8Idct.Walsh(coeffs + 24 * 16, y2);

            for (i = 0; i < 16; i++)
                coeffs[i * 16] = y2[i];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictIntraLuma(byte* predict, int stride, MbInfo* mbi, short* coeffs)
        {
            if (mbi->YMode == BPred)
                BPredict(predict, stride, mbi, coeffs);
            else
            {
                int i;

                switch (mbi->YMode)
                {
                    case DcPred:
                        PredictDcNxN(predict, stride, 16);
                        break;
                    case VPred:
                        PredictV16x16(predict, stride);
                        break;
                    case HPred:
                        PredictH16x16(predict, stride);
                        break;
                    case TmPred:
                        PredictTm16x16(predict, stride);
                        break;
                    default:
                        throw new InvalidDataException("VP8: unexpected luma intra mode."); // assert(0)
                }

                FixupDcCoeffs(mbi, coeffs);

                for (i = 0; i < 16; i++)
                {
                    Vp8Idct.IdctAdd(predict, predict, stride, coeffs);
                    coeffs += 16;
                    predict += 4;

                    if ((i & 3) == 3)
                        predict += stride * 4 - 16;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictIntraChroma(byte* predictU, byte* predictV, int stride, MbInfo* mbi, short* coeffs)
        {
            int i;

            switch (mbi->UvMode)
            {
                case DcPred:
                    PredictDcNxN(predictU, stride, 8);
                    PredictDcNxN(predictV, stride, 8);
                    break;
                case VPred:
                    PredictV8x8(predictU, stride);
                    PredictV8x8(predictV, stride);
                    break;
                case HPred:
                    PredictH8x8(predictU, stride);
                    PredictH8x8(predictV, stride);
                    break;
                case TmPred:
                    PredictTm8x8(predictU, stride);
                    PredictTm8x8(predictV, stride);
                    break;
                default:
                    throw new InvalidDataException("VP8: unexpected chroma intra mode."); // assert(0)
            }

            coeffs += 16 * 16;

            for (i = 16; i < 20; i++)
            {
                Vp8Idct.IdctAdd(predictU, predictU, stride, coeffs);
                coeffs += 16;
                predictU += 4;

                if ((i & 1) != 0)
                    predictU += stride * 4 - 8;
            }

            for (i = 20; i < 24; i++)
            {
                Vp8Idct.IdctAdd(predictV, predictV, stride, coeffs);
                coeffs += 16;
                predictV += 4;

                if ((i & 1) != 0)
                    predictV += stride * 4 - 8;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void SixtapHoriz(byte* output, int outputStride, byte* reference, int referenceStride, int cols, int rows, short* filter)
        {
            int r, c, temp;

            for (r = 0; r < rows; r++)
            {
                for (c = 0; c < cols; c++)
                {
                    temp = (reference[-2] * filter[0]) +
                           (reference[-1] * filter[1]) +
                           (reference[0] * filter[2]) +
                           (reference[1] * filter[3]) +
                           (reference[2] * filter[4]) +
                           (reference[3] * filter[5]) +
                           64;
                    temp >>= 7;
                    output[c] = (byte)Clamp255(temp);
                    reference++;
                }

                reference += referenceStride - cols;
                output += outputStride;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void SixtapVert(byte* output, int outputStride, byte* reference, int referenceStride, int cols, int rows, short* filter)
        {
            int r, c, temp;

            for (r = 0; r < rows; r++)
            {
                for (c = 0; c < cols; c++)
                {
                    temp = (reference[-2 * referenceStride] * filter[0]) +
                           (reference[-1 * referenceStride] * filter[1]) +
                           (reference[0 * referenceStride] * filter[2]) +
                           (reference[1 * referenceStride] * filter[3]) +
                           (reference[2 * referenceStride] * filter[4]) +
                           (reference[3 * referenceStride] * filter[5]) +
                           64;
                    temp >>= 7;
                    output[c] = (byte)Clamp255(temp);
                    reference++;
                }

                reference += referenceStride - cols;
                output += outputStride;
            }
        }

        /// filters: filter_t[8], flattened [8][6].
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Sixtap2D(byte* output, int outputStride, byte* reference, int referenceStride, int cols, int rows, int mx, int my, short* filters)
        {
            byte* temp = stackalloc byte[16 * (16 + 5)];

            SixtapHoriz(temp, 16,
                        reference - 2 * referenceStride, referenceStride,
                        cols, rows + 5, filters + mx * 6);
            SixtapVert(output, outputStride,
                       temp + 2 * 16, 16,
                       cols, rows, filters + my * 6);
        }

        struct ImgIndex
        {
            public byte* Y, U, V;
            public int Stride, UvStride;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static byte* FilterBlock(byte* output, byte* reference, int stride, Mv* mv, short* filters)
        {
            int mx, my;

            // Handle 0,0 as a special case. TODO: Does this make it any faster?
            if (mv->Raw == 0)
                return reference;

            mx = mv->X & 7;
            my = mv->Y & 7;
            reference += ((mv->Y >> 3) * stride) + (mv->X >> 3);

            if ((mx | my) != 0)
            {
                Sixtap2D(output, stride, reference, stride, 4, 4, mx, my, filters);
                reference = output;
            }

            return reference;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Recon1Block(byte* output, byte* reference, int stride, Mv* mv, short* filters, short* coeffs, MbInfo* mbi, int b)
        {
            byte* predict;

            predict = FilterBlock(output, reference, stride, mv, filters);
            Vp8Idct.IdctAdd(output, predict, stride, coeffs + 16 * b);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static Mv CalculateChromaSplitMv(MbInfo* mbi, int b, bool fullPixel)
        {
            int temp;
            Mv mv;
            Mv* mvs = MbInfo.SplitMvs(mbi);

            temp = mvs[b].X +
                   mvs[b + 1].X +
                   mvs[b + 4].X +
                   mvs[b + 5].X;

            if (temp < 0)
                temp -= 4;
            else
                temp += 4;

            mv.X = (short)(temp / 8);

            temp = mvs[b].Y +
                   mvs[b + 1].Y +
                   mvs[b + 4].Y +
                   mvs[b + 5].Y;

            if (temp < 0)
                temp -= 4;
            else
                temp += 4;

            mv.Y = (short)(temp / 8);

            if (fullPixel)
            {
                mv.X = (short)(mv.X & ~7);
                mv.Y = (short)(mv.Y & ~7);
            }

            return mv;
        }

        /// Note: We rely on the reconstructed border having the same stride as the reference
        /// buffer because the filter_block can't adjust the stride with its return value, only
        /// the reference pointer.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void BuildMcBorder(byte* dst, byte* src, int stride, int x, int y, int bW, int bH, int w, int h)
        {
            byte* refRow;

            // Get a pointer to the start of the real data for this row
            refRow = src - x - (long)y * stride;

            if (y >= h)
                refRow += (long)(h - 1) * stride;
            else if (y > 0)
                refRow += (long)y * stride;

            do
            {
                int left, right = 0, copy;

                left = x < 0 ? -x : 0;

                if (left > bW)
                    left = bW;

                if (x + bW > w)
                    right = x + bW - w;

                if (right > bW)
                    right = bW;

                copy = bW - left - right;

                if (left != 0)
                    NativeMemory.Fill(dst, (nuint)left, refRow[0]);

                if (copy != 0)
                    Buffer.MemoryCopy(refRow + x + left, dst + left, copy, copy);

                if (right != 0)
                    NativeMemory.Fill(dst + left + copy, (nuint)right, refRow[w - 1]);

                dst += stride;
                y++;

                if (y < h && y > 0)
                    refRow += stride;
            }
            while (--bH != 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Recon1EdgeBlock(byte* output, byte* emulBlock, byte* reference, int stride, Mv* mv, short* filters, short* coeffs, MbInfo* mbi, int x, int y, int w, int h, int startB)
        {
            byte* predict;
            int b = startB;
            const int bW = 4;
            const int bH = 4;

            x += mv->X >> 3;
            y += mv->Y >> 3;

            // Need two pixels left/above, 3 right/below for 6-tap
            if (x < 2 || x + bW - 1 + 3 >= w || y < 2 ||
                y + bH - 1 + 3 >= h)
            {
                reference += (mv->X >> 3) + (mv->Y >> 3) * stride;
                BuildMcBorder(emulBlock,
                              reference - 2 - 2 * stride, stride,
                              x - 2, y - 2, bW + 5, bH + 5, w, h);
                reference = emulBlock + 2 * stride + 2;
                reference -= (mv->X >> 3) + (mv->Y >> 3) * stride;
            }

            predict = FilterBlock(output, reference, stride, mv, filters);
            Vp8Idct.IdctAdd(output, predict, stride, coeffs + 16 * b);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictInterEmulatedEdge(DecoderCtx* ctx, ImgIndex* img, short* coeffs, MbInfo* mbi, int mbCol, int mbRow)
        {
            // TODO: Move this into its own buffer. This only works because we still have a
            // border allocated.
            byte* emulBlock = ctx->FrameStrg[0].Img.ImgData;
            byte* reference;
            byte* output;
            long referenceOffset;
            int w, h, x, y, b;
            Mv* chromaMv = stackalloc Mv[4];
            byte* u = img->U, v = img->V;
            bool fullPixel = ctx->FrameHdr.Version == 3;

            x = mbCol * 16;
            y = mbRow * 16;
            w = (int)ctx->MbCols * 16;
            h = (int)ctx->MbRows * 16;
            output = img->Y;
            referenceOffset = ctx->RefFrameOffsets[mbi->RefFrame];
            reference = output + referenceOffset;

            if (mbi->YMode != SplitMv)
            {
                Mv uvmv;

                uvmv = mbi->Mv;
                uvmv.X = (short)((uvmv.X + 1 + (uvmv.X >> 31) * 2) / 2);
                uvmv.Y = (short)((uvmv.Y + 1 + (uvmv.Y >> 31) * 2) / 2);

                if (fullPixel)
                {
                    uvmv.X = (short)(uvmv.X & ~7);
                    uvmv.Y = (short)(uvmv.Y & ~7);
                }

                chromaMv[0] = uvmv;
                chromaMv[1] = uvmv;
                chromaMv[2] = uvmv;
                chromaMv[3] = uvmv;
            }
            else
            {
                chromaMv[0] = CalculateChromaSplitMv(mbi, 0, fullPixel);
                chromaMv[1] = CalculateChromaSplitMv(mbi, 2, fullPixel);
                chromaMv[2] = CalculateChromaSplitMv(mbi, 8, fullPixel);
                chromaMv[3] = CalculateChromaSplitMv(mbi, 10, fullPixel);
            }

            // Luma
            for (b = 0; b < 16; b++)
            {
                Mv* ymv;

                if (mbi->YMode != SplitMv)
                    ymv = &mbi->Mv;
                else
                    ymv = MbInfo.SplitMvs(mbi) + b;

                Recon1EdgeBlock(output, emulBlock, reference,
                                img->Stride, ymv, ctx->SubpixelFilters, coeffs,
                                mbi, x, y, w, h, b);

                x += 4;
                output += 4;
                reference += 4;

                if ((b & 3) == 3)
                {
                    x -= 16;
                    y += 4;
                    output += 4 * img->Stride - 16;
                    reference += 4 * img->Stride - 16;
                }
            }

            x = mbCol * 16;
            y = mbRow * 16;

            // Chroma
            x >>= 1;
            y >>= 1;
            w >>= 1;
            h >>= 1;

            for (b = 0; b < 4; b++)
            {
                Recon1EdgeBlock(u, emulBlock, u + referenceOffset,
                                img->UvStride,
                                &chromaMv[b], ctx->SubpixelFilters,
                                coeffs, mbi, x, y, w, h, b + 16);
                Recon1EdgeBlock(v, emulBlock, v + referenceOffset,
                                img->UvStride,
                                &chromaMv[b], ctx->SubpixelFilters,
                                coeffs, mbi, x, y, w, h, b + 20);
                u += 4;
                v += 4;
                x += 4;

                if ((b & 1) != 0)
                {
                    x -= 8;
                    y += 4;
                    u += 4 * img->UvStride - 8;
                    v += 4 * img->UvStride - 8;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void PredictInter(DecoderCtx* ctx, ImgIndex* img, short* coeffs, MbInfo* mbi)
        {
            byte* y = img->Y;
            byte* u = img->U;
            byte* v = img->V;
            long referenceOffset;
            Mv* chromaMv = stackalloc Mv[4];
            bool fullPixel = ctx->FrameHdr.Version == 3;
            int b;

            if (mbi->YMode != SplitMv)
            {
                Mv uvmv;

                uvmv = mbi->Mv;
                uvmv.X = (short)((uvmv.X + 1 + (uvmv.X >> 31) * 2) / 2);
                uvmv.Y = (short)((uvmv.Y + 1 + (uvmv.Y >> 31) * 2) / 2);

                if (fullPixel)
                {
                    uvmv.X = (short)(uvmv.X & ~7);
                    uvmv.Y = (short)(uvmv.Y & ~7);
                }

                chromaMv[0] =
                    chromaMv[1] =
                        chromaMv[2] =
                            chromaMv[3] = uvmv;
            }
            else
            {
                chromaMv[0] = CalculateChromaSplitMv(mbi, 0, fullPixel);
                chromaMv[1] = CalculateChromaSplitMv(mbi, 2, fullPixel);
                chromaMv[2] = CalculateChromaSplitMv(mbi, 8, fullPixel);
                chromaMv[3] = CalculateChromaSplitMv(mbi, 10, fullPixel);
            }

            referenceOffset = ctx->RefFrameOffsets[mbi->RefFrame];

            for (b = 0; b < 16; b++)
            {
                Mv* ymv;

                if (mbi->YMode != SplitMv)
                    ymv = &mbi->Mv;
                else
                    ymv = MbInfo.SplitMvs(mbi) + b;

                Recon1Block(y, y + referenceOffset, img->Stride,
                            ymv, ctx->SubpixelFilters, coeffs, mbi, b);
                y += 4;

                if ((b & 3) == 3)
                    y += 4 * img->Stride - 16;
            }

            for (b = 0; b < 4; b++)
            {
                Recon1Block(u, u + referenceOffset,
                            img->UvStride, &chromaMv[b],
                            ctx->SubpixelFilters, coeffs, mbi, b + 16);
                Recon1Block(v, v + referenceOffset,
                            img->UvStride, &chromaMv[b],
                            ctx->SubpixelFilters, coeffs, mbi, b + 20);
                u += 4;
                v += 4;

                if ((b & 1) != 0)
                {
                    u += 4 * img->UvStride - 8;
                    v += 4 * img->UvStride - 8;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ReleaseRefFrame(RefCntImg* rcimg)
        {
            if (rcimg != null)
            {
                // assert(rcimg->ref_cnt)
                rcimg->RefCnt--;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static RefCntImg* RefFrame(RefCntImg* rcimg)
        {
            rcimg->RefCnt++;
            return rcimg;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static RefCntImg* FindFreeRefFrame(RefCntImg* frames)
        {
            int i;

            for (i = 0; i < NumRefFrames; i++)
                if (frames[i].RefCnt == 0)
                {
                    frames[i].RefCnt = 1;
                    return &frames[i];
                }

            throw new InvalidOperationException("VP8: no free frame buffer."); // assert(0)
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FixupLeft(byte* predict, int width, int stride, int row, int mode)
        {
            // The left column of out-of-frame pixels is taken to be 129, unless we're doing
            // DC_PRED, in which case we duplicate the above row, unless this is also row 0, in
            // which case we use 129.
            byte* left = predict - 1;
            int i;

            if (mode == DcPred && row != 0)
            {
                byte* above = predict - stride;

                for (i = 0; i < width; i++)
                {
                    *left = above[i];
                    left += stride;
                }
            }
            else
            {
                // Need to re-set the above row, in case the above MB was DC_PRED.
                left -= stride;

                for (i = -1; i < width; i++)
                {
                    *left = 129;
                    left += stride;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FixupAbove(byte* predict, int width, int stride, int col, int mode)
        {
            // The above row of out-of-frame pixels is taken to be 127, unless we're doing
            // DC_PRED, in which case we duplicate the left col, unless this is also col 0, in
            // which case we use 127.
            byte* above = predict - stride;
            int i;

            if (mode == DcPred && col != 0)
            {
                byte* left = predict - 1;

                for (i = 0; i < width; i++)
                {
                    above[i] = *left;
                    left += stride;
                }
            }
            else
                // Need to re-set the left col, in case the last MB was DC_PRED.
                NativeMemory.Fill(above - 1, (nuint)(width + 1), 127);

            NativeMemory.Fill(above + width, 4, 127); // for above-right subblock modes
        }

        /// vpx_img_alloc(img, IMG_FMT_I420, w, h, align) followed by
        /// vpx_img_set_rect(img, x, y, dW, dH). The buffer is zeroed (malloc in the C).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ImgAlloc(Vp8Image* img, uint w, uint h, uint strideAlign, uint x, uint y, uint dW, uint dH)
        {
            // Calculate storage sizes given the chroma subsampling
            w = (w + 1) & ~1u;
            h = (h + 1) & ~1u;
            uint s = (w + strideAlign - 1) & ~(strideAlign - 1);

            *img = default;
            img->ImgData = (byte*)NativeMemory.AllocZeroed((nuint)h * s * 3 / 2);
            img->W = w;
            img->H = h;
            img->StrideY = (int)s;
            img->StrideUv = (int)(s >> 1);

            // vpx_img_set_rect: calculate plane pointers
            img->DW = dW;
            img->DH = dH;
            byte* data = img->ImgData;
            img->PlaneY = data + x + (nuint)y * s;
            data += (nuint)h * s;
            img->PlaneU = data + (x >> 1) + (nuint)(y >> 1) * (s >> 1);
            data += (nuint)(h >> 1) * (s >> 1);
            img->PlaneV = data + (x >> 1) + (nuint)(y >> 1) * (s >> 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ImgFree(Vp8Image* img)
        {
            NativeMemory.Free(img->ImgData);
            *img = default;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Init(DecoderCtx* ctx)
        {
            int i;
            byte* thisFrameBase;

            if (ctx->FrameHdr.FrameSizeUpdated != 0)
            {
                for (i = 0; i < NumRefFrames; i++)
                {
                    uint w = ctx->MbCols * 16 + BorderPixels * 2;
                    uint h = ctx->MbRows * 16 + BorderPixels * 2;

                    ImgFree(&ctx->FrameStrg[i].Img);
                    ctx->FrameStrg[i].RefCnt = 0;
                    ctx->RefFrames[i] = null;

                    ImgAlloc(&ctx->FrameStrg[i].Img, w, h, 16,
                             BorderPixels, BorderPixels,
                             ctx->FrameHdr.KfW, ctx->FrameHdr.KfH);
                }

                ReadOnlySpan<short> filters = ctx->FrameHdr.Version != 0
                    ? Vp8Tables.BilinearFilters
                    : Vp8Tables.SixtapFilters;
                filters.CopyTo(new Span<short>(ctx->SubpixelFilters, 8 * 6));
            }

            // Find a free framebuffer to predict into
            if (ctx->RefFrames[CurrentFrame] != null)
                ReleaseRefFrame(ctx->RefFrames[CurrentFrame]);

            ctx->RefFrames[CurrentFrame] = FindFreeRefFrame(ctx->FrameStrg);
            thisFrameBase = ctx->RefFrames[CurrentFrame]->Img.ImgData;

            // Calculate offsets to the other reference frames
            for (i = 0; i < NumRefFrames; i++)
            {
                RefCntImg* refImg = ctx->RefFrames[i];

                ctx->RefFrameOffsets[i] = refImg != null ? refImg->Img.ImgData - thisFrameBase : 0;
            }

            // TODO: No need to do this on every frame...
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Destroy(DecoderCtx* ctx)
        {
            for (int i = 0; i < NumRefFrames; i++)
            {
                ImgFree(&ctx->FrameStrg[i].Img);
                ctx->FrameStrg[i].RefCnt = 0;
                ctx->RefFrames[i] = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ProcessRow(DecoderCtx* ctx, int row, int startCol, int numCols)
        {
            ImgIndex img;
            MbInfo* mbi;
            int col;
            short* coeffs;
            Vp8Image* current = &ctx->RefFrames[CurrentFrame]->Img;

            // Adjust pointers based on row, start_col
            img.Stride = current->StrideY;
            img.UvStride = current->StrideUv;
            img.Y = current->PlaneY;
            img.U = current->PlaneU;
            img.V = current->PlaneV;
            img.Y += (img.Stride * row + startCol) * 16;
            img.U += (img.UvStride * row + startCol) * 8;
            img.V += (img.UvStride * row + startCol) * 8;
            mbi = ctx->MbInfoRows[row] + startCol;
            coeffs = ctx->Tokens[row & (ctx->TokenHdr.Partitions - 1)].Coeffs +
                     25 * 16 * startCol;

            // Fix up the out-of-frame pixels
            if (startCol == 0)
            {
                FixupLeft(img.Y, 16, img.Stride, row, mbi->YMode);
                FixupLeft(img.U, 8, img.UvStride, row, mbi->UvMode);
                FixupLeft(img.V, 8, img.UvStride, row, mbi->UvMode);

                if (row == 0)
                    *(img.Y - img.Stride - 1) = 127;
            }

            for (col = startCol; col < startCol + numCols; col++)
            {
                if (row == 0)
                {
                    FixupAbove(img.Y, 16, img.Stride, col, mbi->YMode);
                    FixupAbove(img.U, 8, img.UvStride, col, mbi->UvMode);
                    FixupAbove(img.V, 8, img.UvStride, col, mbi->UvMode);
                }

                if (mbi->YMode <= BPred)
                {
                    PredictIntraLuma(img.Y, img.Stride, mbi, coeffs);
                    PredictIntraChroma(img.U, img.V, img.UvStride, mbi, coeffs);
                }
                else
                {
                    if (mbi->YMode != SplitMv) // && != BPRED
                        FixupDcCoeffs(mbi, coeffs);

                    if (mbi->NeedMcBorder != 0)
                        PredictInterEmulatedEdge(ctx, &img, coeffs, mbi, col, row);
                    else
                        PredictInter(ctx, &img, coeffs, mbi);
                }

                // Advance to the next macroblock
                mbi++;
                img.Y += 16;
                img.U += 8;
                img.V += 8;
                coeffs += 25 * 16;
            }

            if (col == ctx->MbCols)
            {
                // Extend the last row by four pixels for intra-prediction. This will be
                // propagated later by copy_down.
                uint* extend = (uint*)(img.Y + 15 * img.Stride);
                uint val = 0x01010101u * img.Y[-1 + 15 * img.Stride];
                *extend = val;
            }
        }
    }
}
