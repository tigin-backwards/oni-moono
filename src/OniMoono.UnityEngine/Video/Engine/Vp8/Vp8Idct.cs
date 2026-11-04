// Port of the VP8 reference decoder ("dixie") from RFC 6386, Section 20.
// Copyright (c) 2010, 2011, Google Inc. All rights reserved. BSD-style license; see
// src/ThirdParty/rfc6386/LICENSE and PATENTS.

using System.Runtime.CompilerServices;
using static OniMoono.Engine.Video.Vp8.Vp8Constants;

namespace OniMoono.Engine.Video.Vp8
{
    /// The inverse Walsh-Hadamard transform of the Y2 block and the inverse DCT with
    /// reconstruction (idct_add.c).
    internal static unsafe class Vp8Idct
    {
        const int Cospi8Sqrt2Minus1 = 20091;
        const int Sinpi8Sqrt2 = 35468;
        const int Rounding = 0;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Walsh(short* input, short* output)
        {
            int i;
            int a1, b1, c1, d1;
            int a2, b2, c2, d2;
            short* ip = input;
            short* op = output;

            for (i = 0; i < 4; i++)
            {
                a1 = ip[0] + ip[12];
                b1 = ip[4] + ip[8];
                c1 = ip[4] - ip[8];
                d1 = ip[0] - ip[12];

                op[0] = (short)(a1 + b1);
                op[4] = (short)(c1 + d1);
                op[8] = (short)(a1 - b1);
                op[12] = (short)(d1 - c1);
                ip++;
                op++;
            }

            ip = output;
            op = output;

            for (i = 0; i < 4; i++)
            {
                a1 = ip[0] + ip[3];
                b1 = ip[1] + ip[2];
                c1 = ip[1] - ip[2];
                d1 = ip[0] - ip[3];

                a2 = a1 + b1;
                b2 = c1 + d1;
                c2 = a1 - b1;
                d2 = d1 - c1;

                op[0] = (short)((a2 + 3) >> 3);
                op[1] = (short)((b2 + 3) >> 3);
                op[2] = (short)((c2 + 3) >> 3);
                op[3] = (short)((d2 + 3) >> 3);

                ip += 4;
                op += 4;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void IdctColumns(short* input, short* output)
        {
            int i;
            int a1, b1, c1, d1;
            short* ip = input;
            short* op = output;
            int temp1, temp2;
            const int shortpitch = 4;

            for (i = 0; i < 4; i++)
            {
                a1 = ip[0] + ip[8];
                b1 = ip[0] - ip[8];

                temp1 = (ip[4] * Sinpi8Sqrt2 + Rounding) >> 16;
                temp2 = ip[12] +
                    ((ip[12] * Cospi8Sqrt2Minus1 + Rounding) >> 16);
                c1 = temp1 - temp2;

                temp1 = ip[4] +
                    ((ip[4] * Cospi8Sqrt2Minus1 + Rounding) >> 16);
                temp2 = (ip[12] * Sinpi8Sqrt2 + Rounding) >> 16;
                d1 = temp1 + temp2;

                op[shortpitch * 0] = (short)(a1 + d1);
                op[shortpitch * 3] = (short)(a1 - d1);

                op[shortpitch * 1] = (short)(b1 + c1);
                op[shortpitch * 2] = (short)(b1 - c1);

                ip++;
                op++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void IdctAdd(byte* recon, byte* predict, int stride, short* coeffs)
        {
            int i;
            int a1, b1, c1, d1, temp1, temp2;
            short* tmp = stackalloc short[16];

            IdctColumns(coeffs, tmp);
            coeffs = tmp;

            for (i = 0; i < 4; i++)
            {
                a1 = coeffs[0] + coeffs[2];
                b1 = coeffs[0] - coeffs[2];

                temp1 = (coeffs[1] * Sinpi8Sqrt2 + Rounding) >> 16;
                temp2 = coeffs[3] +
                    ((coeffs[3] * Cospi8Sqrt2Minus1 + Rounding) >> 16);
                c1 = temp1 - temp2;

                temp1 = coeffs[1] +
                    ((coeffs[1] * Cospi8Sqrt2Minus1 + Rounding) >> 16);
                temp2 = (coeffs[3] * Sinpi8Sqrt2 + Rounding) >> 16;
                d1 = temp1 + temp2;

                recon[0] = (byte)Clamp255(predict[0] + ((a1 + d1 + 4) >> 3));
                recon[3] = (byte)Clamp255(predict[3] + ((a1 - d1 + 4) >> 3));
                recon[1] = (byte)Clamp255(predict[1] + ((b1 + c1 + 4) >> 3));
                recon[2] = (byte)Clamp255(predict[2] + ((b1 - c1 + 4) >> 3));

                coeffs += 4;
                recon += stride;
                predict += stride;
            }
        }
    }
}
