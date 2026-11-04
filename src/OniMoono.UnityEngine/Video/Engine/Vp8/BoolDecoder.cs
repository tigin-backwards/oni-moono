// Port of the VP8 reference decoder ("dixie") from RFC 6386, Section 20.
// Copyright (c) 2010, 2011, Google Inc. All rights reserved. BSD-style license; see
// src/ThirdParty/rfc6386/LICENSE and PATENTS.

using System;
using System.Runtime.CompilerServices;

namespace OniMoono.Engine.Video.Vp8
{
    /// The boolean entropy decoder (bool_decoder.h). Reading past the end of the input shifts
    /// in zeros, as in the C.
    internal unsafe struct BoolDecoder
    {
        public byte* Input;     // next compressed data byte
        public nuint InputLen;  // length of the input buffer
        public uint Range;      // identical to encoder's range
        public uint Value;      // contains at least 8 significant bits
        public int BitCount;    // # of bits shifted out of value, max 7

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Init(BoolDecoder* d, byte* startPartition, nuint sz)
        {
            if (sz >= 2)
            {
                d->Value = (uint)((startPartition[0] << 8) // first 2 input bytes
                                  | startPartition[1]);
                d->Input = startPartition + 2;             // ptr to next byte
                d->InputLen = sz - 2;
            }
            else
            {
                d->Value = 0;
                d->Input = null;
                d->InputLen = 0;
            }

            d->Range = 255;    // initial range is full
            d->BitCount = 0;   // have not yet shifted out any bits
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int Get(BoolDecoder* d, int probability)
        {
            // range and split are identical to the corresponding values used by the encoder
            // when this bool was written
            uint split = 1 + (((d->Range - 1) * (uint)probability) >> 8);
            uint bigSplit = split << 8;
            int retval; // will be 0 or 1

            if (d->Value >= bigSplit) // encoded a one
            {
                retval = 1;
                d->Range -= split;    // reduce range
                d->Value -= bigSplit; // subtract off left endpoint of interval
            }
            else // encoded a zero
            {
                retval = 0;
                d->Range = split;     // reduce range, no change in left endpoint
            }

            while (d->Range < 128) // shift out irrelevant value bits
            {
                d->Value <<= 1;
                d->Range <<= 1;

                if (++d->BitCount == 8) // shift in new bits 8 at a time
                {
                    d->BitCount = 0;

                    if (d->InputLen != 0)
                    {
                        d->Value |= *d->Input++;
                        d->InputLen--;
                    }
                }
            }

            return retval;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int GetBit(BoolDecoder* br) => Get(br, 128);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int GetUint(BoolDecoder* br, int bits)
        {
            int z = 0;

            for (int bit = bits - 1; bit >= 0; bit--)
                z |= GetBit(br) << bit;

            return z;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int GetInt(BoolDecoder* br, int bits)
        {
            int z = 0;

            for (int bit = bits - 1; bit >= 0; bit--)
                z |= GetBit(br) << bit;

            return GetBit(br) != 0 ? -z : z;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int MaybeGetInt(BoolDecoder* br, int bits) => GetBit(br) != 0 ? GetInt(br, bits) : 0;

        /// bool_read_tree: walks tree t with node probabilities p; returns the negated leaf.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int ReadTree(BoolDecoder* bd, ReadOnlySpan<int> t, ReadOnlySpan<byte> p)
        {
            int i = 0;

            while ((i = t[i + Get(bd, p[i >> 1])]) > 0)
            {
            }

            return -i;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int ReadTree(BoolDecoder* bd, ReadOnlySpan<int> t, byte* p, int probCount) =>
            ReadTree(bd, t, new ReadOnlySpan<byte>(p, probCount));
    }
}
