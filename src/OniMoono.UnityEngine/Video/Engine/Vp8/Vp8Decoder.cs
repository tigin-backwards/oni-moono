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
    /// A VP8 decoder (RFC 6386). Not thread-safe: one decoder per decode thread.
    ///
    /// A port of dixie.c: frame header parsing, the segmentation, loop filter, quantizer,
    /// reference and entropy headers, token partitions, the per-row decode loop and reference
    /// frame management. The decoder state is a DecoderCtx in native memory.
    ///
    /// Every method of the decoder (and VideoFrameConverter's) is compiled optimized at its
    /// first call (AggressiveOptimization). Tiered compilation would start them as unoptimized
    /// code and optimize them only once the runtime's call counting begins, which it puts off
    /// while start-up compiles new methods: the main menu's intro, playing from start-up, was
    /// decoded about four times slower than real time for its first seconds and ran up to
    /// 1.5 s behind its audio, every fourth frame shown. The decoder is integer code; the
    /// pictures are the same.
    internal sealed unsafe class Vp8Decoder : IDisposable
    {
        const int FrameHeaderSz = 3;
        const int KeyframeHeaderSz = 7;

        // vpx_codec_err_t values returned by ParseFrameHeader.
        const int CodecOk = 0;
        const int CodecUnsupBitstream = 1;
        const int CodecCorruptFrame = 2;

        DecoderCtx* ctx;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public Vp8Decoder()
        {
            ctx = (DecoderCtx*)NativeMemory.AllocZeroed((nuint)sizeof(DecoderCtx));
            ctx->Tokens = (TokenDecoder*)NativeMemory.AllocZeroed((nuint)(MaxPartitions * sizeof(TokenDecoder)));
            ctx->DequantFactors = (DequantFactors*)NativeMemory.AllocZeroed((nuint)(MaxMbSegments * sizeof(DequantFactors)));
            ctx->FrameStrg = (RefCntImg*)NativeMemory.AllocZeroed((nuint)(NumRefFrames * sizeof(RefCntImg)));
            ctx->RefFrames = (RefCntImg**)NativeMemory.AllocZeroed((nuint)(NumRefFrames * sizeof(RefCntImg*)));
            DecodeInit(ctx);
        }

        ~Vp8Decoder()
        {
            Free();
        }

        /// Decodes one compressed frame (one WebM block). Returns true when the frame is to be
        /// shown; the image fields below then describe it until the next call.
        /// Throws InvalidDataException for corrupt or unsupported data.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool Decode(ReadOnlySpan<byte> frame)
        {
            ObjectDisposedException.ThrowIf(ctx == null, this);
            fixed (byte* data = frame)
                DecodeFrame(ctx, data, (uint)frame.Length);
            return ctx->FrameHdr.IsShown != 0;
        }

        /// Display width (frame header w & 0x3fff).
        public int Width => (int)ctx->FrameHdr.KfW;

        public int Height => (int)ctx->FrameHdr.KfH;

        /// The frame just decoded (ref_frames[CURRENT_FRAME]), or null before the first frame.
        Vp8Image* Image
        {
            get
            {
                ObjectDisposedException.ThrowIf(ctx == null, this);
                RefCntImg* current = ctx->RefFrames[CurrentFrame];
                return current != null ? &current->Img : null;
            }
        }

        public byte* PlaneY => Image != null ? Image->PlaneY : null;

        public byte* PlaneU => Image != null ? Image->PlaneU : null;

        public byte* PlaneV => Image != null ? Image->PlaneV : null;

        public int StrideY => Image != null ? Image->StrideY : 0;

        public int StrideU => Image != null ? Image->StrideUv : 0;

        public int StrideV => Image != null ? Image->StrideUv : 0;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Dispose()
        {
            Free();
            GC.SuppressFinalize(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Free()
        {
            if (ctx == null) return;
            DecodeDestroy(ctx);
            NativeMemory.Free(ctx->Tokens);
            NativeMemory.Free(ctx->DequantFactors);
            NativeMemory.Free(ctx->FrameStrg);
            NativeMemory.Free(ctx->RefFrames);
            NativeMemory.Free(ctx);
            ctx = null;
        }

        /// vpx_internal_error
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static InvalidDataException Error(string message) => new("VP8: " + message);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeEntropyHeader(DecoderCtx* ctx, BoolDecoder* bd, EntropyHdr* hdr)
        {
            int i, j, k, l;
            ReadOnlySpan<byte> updateProbs = Vp8Tables.CoeffEntropyUpdateProbs;

            // Read coefficient probability updates
            for (i = 0; i < BlockTypes; i++)
                for (j = 0; j < CoeffBands; j++)
                    for (k = 0; k < PrevCoeffContexts; k++)
                        for (l = 0; l < EntropyNodes; l++)
                        {
                            int n = ((i * CoeffBands + j) * PrevCoeffContexts + k) * EntropyNodes + l;
                            if (BoolDecoder.Get(bd, updateProbs[n]) != 0)
                                hdr->CoeffProbs[n] = (byte)BoolDecoder.GetUint(bd, 8);
                        }

            // Read coefficient skip mode probability
            hdr->CoeffSkipEnabled = (uint)BoolDecoder.GetBit(bd);

            if (hdr->CoeffSkipEnabled != 0)
                hdr->CoeffSkipProb = (byte)BoolDecoder.GetUint(bd, 8);

            // Parse interframe probability updates
            if (ctx->FrameHdr.IsKeyframe == 0)
            {
                hdr->ProbInter = (byte)BoolDecoder.GetUint(bd, 8);
                hdr->ProbLast = (byte)BoolDecoder.GetUint(bd, 8);
                hdr->ProbGf = (byte)BoolDecoder.GetUint(bd, 8);

                if (BoolDecoder.GetBit(bd) != 0)
                    for (i = 0; i < 4; i++)
                        hdr->YModeProbs[i] = (byte)BoolDecoder.GetUint(bd, 8);

                if (BoolDecoder.GetBit(bd) != 0)
                    for (i = 0; i < 3; i++)
                        hdr->UvModeProbs[i] = (byte)BoolDecoder.GetUint(bd, 8);

                ReadOnlySpan<byte> mvUpdateProbs = Vp8Tables.MvEntropyUpdateProbs;
                for (i = 0; i < 2; i++)
                    for (j = 0; j < MvProbCnt; j++)
                        if (BoolDecoder.Get(bd, mvUpdateProbs[i * MvProbCnt + j]) != 0)
                        {
                            int x = BoolDecoder.GetUint(bd, 7);
                            hdr->MvProbs[i * MvProbCnt + j] = (byte)(x != 0 ? x << 1 : 1);
                        }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeReferenceHeader(DecoderCtx* ctx, BoolDecoder* bd, ReferenceHdr* hdr)
        {
            bool key = ctx->FrameHdr.IsKeyframe != 0;

            hdr->RefreshGf = key ? 1u : (uint)BoolDecoder.GetBit(bd);
            hdr->RefreshArf = key ? 1u : (uint)BoolDecoder.GetBit(bd);
            hdr->CopyGf = key ? 0u : hdr->RefreshGf == 0
                ? (uint)BoolDecoder.GetUint(bd, 2) : 0u;
            hdr->CopyArf = key ? 0u : hdr->RefreshArf == 0
                ? (uint)BoolDecoder.GetUint(bd, 2) : 0u;
            hdr->SignBias[GoldenFrame] = key ? 0u : (uint)BoolDecoder.GetBit(bd);
            hdr->SignBias[AltrefFrame] = key ? 0u : (uint)BoolDecoder.GetBit(bd);
            hdr->RefreshEntropy = (uint)BoolDecoder.GetBit(bd);
            hdr->RefreshLast = key ? 1u : (uint)BoolDecoder.GetBit(bd);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeQuantizerHeader(BoolDecoder* bd, QuantHdr* hdr)
        {
            int update;
            int lastQ = (int)hdr->QIndex;

            hdr->QIndex = (uint)BoolDecoder.GetUint(bd, 7);
            update = lastQ != hdr->QIndex ? 1 : 0;
            update |= hdr->Y1DcDeltaQ = BoolDecoder.MaybeGetInt(bd, 4);
            update |= hdr->Y2DcDeltaQ = BoolDecoder.MaybeGetInt(bd, 4);
            update |= hdr->Y2AcDeltaQ = BoolDecoder.MaybeGetInt(bd, 4);
            update |= hdr->UvDcDeltaQ = BoolDecoder.MaybeGetInt(bd, 4);
            update |= hdr->UvAcDeltaQ = BoolDecoder.MaybeGetInt(bd, 4);
            hdr->DeltaUpdate = update;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeAndInitTokenPartitions(DecoderCtx* ctx, BoolDecoder* bd, byte* data, uint sz, TokenHdr* hdr)
        {
            int i;

            hdr->Partitions = 1u << BoolDecoder.GetUint(bd, 2);

            if (sz < 3 * (hdr->Partitions - 1))
                throw Error("Truncated packet found parsing partition lengths.");

            sz -= 3 * (hdr->Partitions - 1);

            for (i = 0; i < hdr->Partitions; i++)
            {
                if (i < hdr->Partitions - 1)
                {
                    hdr->PartitionSz[i] = (uint)((data[2] << 16) | (data[1] << 8) | data[0]);
                    data += 3;
                }
                else
                    hdr->PartitionSz[i] = sz;

                if (sz < hdr->PartitionSz[i])
                    throw Error("Truncated partition " + i);

                sz -= hdr->PartitionSz[i];
            }

            for (i = 0; i < ctx->TokenHdr.Partitions; i++)
            {
                BoolDecoder.Init(&ctx->Tokens[i].Bool, data, ctx->TokenHdr.PartitionSz[i]);
                data += ctx->TokenHdr.PartitionSz[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeLoopfilterHeader(DecoderCtx* ctx, BoolDecoder* bd, LoopfilterHdr* hdr)
        {
            if (ctx->FrameHdr.IsKeyframe != 0)
                *hdr = default;

            hdr->UseSimple = (uint)BoolDecoder.GetBit(bd);
            hdr->Level = (uint)BoolDecoder.GetUint(bd, 6);
            hdr->Sharpness = (uint)BoolDecoder.GetUint(bd, 3);
            hdr->DeltaEnabled = (uint)BoolDecoder.GetBit(bd);

            if (hdr->DeltaEnabled != 0 && BoolDecoder.GetBit(bd) != 0)
            {
                int i;

                for (i = 0; i < BlockContexts; i++)
                    hdr->RefDelta[i] = BoolDecoder.MaybeGetInt(bd, 6);

                for (i = 0; i < BlockContexts; i++)
                    hdr->ModeDelta[i] = BoolDecoder.MaybeGetInt(bd, 6);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeSegmentationHeader(DecoderCtx* ctx, BoolDecoder* bd, SegmentHdr* hdr)
        {
            if (ctx->FrameHdr.IsKeyframe != 0)
                *hdr = default;

            hdr->Enabled = (uint)BoolDecoder.GetBit(bd);

            if (hdr->Enabled != 0)
            {
                int i;

                hdr->UpdateMap = (uint)BoolDecoder.GetBit(bd);
                hdr->UpdateData = (uint)BoolDecoder.GetBit(bd);

                if (hdr->UpdateData != 0)
                {
                    hdr->Abs = (uint)BoolDecoder.GetBit(bd);

                    for (i = 0; i < MaxMbSegments; i++)
                        hdr->QuantIdx[i] = BoolDecoder.MaybeGetInt(bd, 7);

                    for (i = 0; i < MaxMbSegments; i++)
                        hdr->LfLevel[i] = BoolDecoder.MaybeGetInt(bd, 6);
                }

                if (hdr->UpdateMap != 0)
                {
                    for (i = 0; i < MbFeatureTreeProbs; i++)
                        hdr->TreeProbs[i] = BoolDecoder.GetBit(bd) != 0
                            ? (uint)BoolDecoder.GetUint(bd, 8)
                            : 255u;
                }
            }
            else
            {
                hdr->UpdateMap = 0;
                hdr->UpdateData = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DequantGlobalInit(DequantFactors* dqf)
        {
            for (int i = 0; i < MaxMbSegments; i++)
                dqf[i].QuantIdx = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int ClampQ(int q)
        {
            if (q < 0) return 0;
            else if (q > 127) return 127;

            return q;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int DcQ(int q) => Vp8Tables.DcQLookup[ClampQ(q)];

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int AcQ(int q) => Vp8Tables.AcQLookup[ClampQ(q)];

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DequantInit(DequantFactors* factors, SegmentHdr* seg, QuantHdr* quantHdr)
        {
            int i, q;
            DequantFactors* dqf = factors;

            for (i = 0; i < (seg->Enabled != 0 ? MaxMbSegments : 1); i++)
            {
                q = (int)quantHdr->QIndex;

                if (seg->Enabled != 0)
                    q = seg->Abs == 0 ? q + seg->QuantIdx[i]
                                      : seg->QuantIdx[i];

                if (dqf->QuantIdx != q || quantHdr->DeltaUpdate != 0)
                {
                    short* f = dqf->Factor;
                    f[TokenBlockY1 * 2 + 0] = (short)DcQ(q + quantHdr->Y1DcDeltaQ);
                    f[TokenBlockY1 * 2 + 1] = (short)AcQ(q);
                    f[TokenBlockUv * 2 + 0] = (short)DcQ(q + quantHdr->UvDcDeltaQ);
                    f[TokenBlockUv * 2 + 1] = (short)AcQ(q + quantHdr->UvAcDeltaQ);
                    f[TokenBlockY2 * 2 + 0] = (short)(DcQ(q + quantHdr->Y2DcDeltaQ) * 2);
                    f[TokenBlockY2 * 2 + 1] = (short)(AcQ(q + quantHdr->Y2AcDeltaQ) * 155 / 100);

                    if (f[TokenBlockY2 * 2 + 1] < 8)
                        f[TokenBlockY2 * 2 + 1] = 8;

                    if (f[TokenBlockUv * 2 + 0] > 132)
                        f[TokenBlockUv * 2 + 0] = 132;

                    dqf->QuantIdx = q;
                }

                dqf++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeFrame(DecoderCtx* ctx, byte* data, uint sz)
        {
            int res;
            BoolDecoder bd;
            int row, partition;

            ctx->SavedEntropyValid = 0;

            if ((res = ParseFrameHeader(data, sz, &ctx->FrameHdr)) != CodecOk)
                throw Error(res == CodecCorruptFrame
                    ? "Failed to parse frame header (corrupt frame)."
                    : "Failed to parse frame header (unsupported bitstream).");

            if (ctx->FrameHdr.IsExperimental != 0)
                throw Error("Experimental bitstreams not supported.");

            // Not in the C, which would run on unallocated (or, after a keyframe that failed
            // early, wrongly sized) frame buffers: an interframe needs a decoded keyframe before
            // it.
            if (ctx->FrameHdr.IsKeyframe == 0
                && (ctx->FrameStrg[0].Img.ImgData == null
                    || ctx->MbCols != ctx->AllocMbCols || ctx->MbRows != ctx->AllocMbRows))
                throw Error("Interframe without a preceding keyframe.");

            data += FrameHeaderSz;
            sz -= FrameHeaderSz;

            if (ctx->FrameHdr.IsKeyframe != 0)
            {
                data += KeyframeHeaderSz;
                sz -= KeyframeHeaderSz;
                ctx->MbCols = (ctx->FrameHdr.KfW + 15) / 16;
                ctx->MbRows = (ctx->FrameHdr.KfH + 15) / 16;

                // Not in the C: when a keyframe that changed the size failed before its buffers
                // were reallocated, a later keyframe of that size reports no size update; the
                // C would then decode into buffers of the old size. Reallocate instead.
                if (ctx->MbCols != ctx->AllocMbCols || ctx->MbRows != ctx->AllocMbRows)
                    ctx->FrameHdr.FrameSizeUpdated = 1;
            }

            // Start the bitreader for the header/entropy partition
            BoolDecoder.Init(&bd, data, ctx->FrameHdr.Part0Sz);

            // Skip the colorspace and clamping bits
            if (ctx->FrameHdr.IsKeyframe != 0)
                if (BoolDecoder.GetUint(&bd, 2) != 0)
                    throw Error("Reserved bits not supported.");

            DecodeSegmentationHeader(ctx, &bd, &ctx->SegmentHdr);
            DecodeLoopfilterHeader(ctx, &bd, &ctx->LoopfilterHdr);
            DecodeAndInitTokenPartitions(ctx,
                                         &bd,
                                         data + ctx->FrameHdr.Part0Sz,
                                         sz - ctx->FrameHdr.Part0Sz,
                                         &ctx->TokenHdr);
            DecodeQuantizerHeader(&bd, &ctx->QuantHdr);
            DecodeReferenceHeader(ctx, &bd, &ctx->ReferenceHdr);

            // Set keyframe entropy defaults. These get updated on keyframes regardless of the
            // refresh_entropy setting.
            if (ctx->FrameHdr.IsKeyframe != 0)
            {
                Vp8Tables.DefaultCoeffProbs.CopyTo(new Span<byte>(ctx->EntropyHdr.CoeffProbs, CoeffProbsSize));
                Vp8Tables.DefaultMvProbs.CopyTo(new Span<byte>(ctx->EntropyHdr.MvProbs, 2 * MvProbCnt));
                Vp8Tables.DefaultYModeProbs.CopyTo(new Span<byte>(ctx->EntropyHdr.YModeProbs, 4));
                Vp8Tables.DefaultUvModeProbs.CopyTo(new Span<byte>(ctx->EntropyHdr.UvModeProbs, 3));
            }

            if (ctx->ReferenceHdr.RefreshEntropy == 0)
            {
                ctx->SavedEntropy = ctx->EntropyHdr;
                ctx->SavedEntropyValid = 1;
            }

            DecodeEntropyHeader(ctx, &bd, &ctx->EntropyHdr);

            Vp8Modes.Init(ctx);
            Vp8Tokens.Init(ctx);
            Vp8Predict.Init(ctx);
            DequantInit(ctx->DequantFactors, &ctx->SegmentHdr, &ctx->QuantHdr);
            ctx->AllocMbCols = ctx->MbCols;
            ctx->AllocMbRows = ctx->MbRows;

            for (row = 0, partition = 0; row < ctx->MbRows; row++)
            {
                Vp8Modes.ProcessRow(ctx, &bd, row, 0, (int)ctx->MbCols);
                Vp8Tokens.ProcessRow(ctx, partition, row, 0, (int)ctx->MbCols);
                Vp8Predict.ProcessRow(ctx, row, 0, (int)ctx->MbCols);

                if (ctx->LoopfilterHdr.Level != 0 && row != 0)
                    Vp8LoopFilter.ProcessRow(ctx, row - 1, 0, (int)ctx->MbCols);

                if (++partition == ctx->TokenHdr.Partitions)
                    partition = 0;
            }

            if (ctx->LoopfilterHdr.Level != 0)
                Vp8LoopFilter.ProcessRow(ctx, row - 1, 0, (int)ctx->MbCols);

            ctx->FrameCnt++;

            if (ctx->ReferenceHdr.RefreshEntropy == 0)
            {
                ctx->EntropyHdr = ctx->SavedEntropy;
                ctx->SavedEntropyValid = 0;
            }

            // Handle reference frame updates
            RefCntImg** refFrames = ctx->RefFrames;

            if (ctx->ReferenceHdr.CopyArf == 1)
            {
                Vp8Predict.ReleaseRefFrame(refFrames[AltrefFrame]);
                refFrames[AltrefFrame] = Vp8Predict.RefFrame(refFrames[LastFrame]);
            }
            else if (ctx->ReferenceHdr.CopyArf == 2)
            {
                Vp8Predict.ReleaseRefFrame(refFrames[AltrefFrame]);
                refFrames[AltrefFrame] = Vp8Predict.RefFrame(refFrames[GoldenFrame]);
            }

            if (ctx->ReferenceHdr.CopyGf == 1)
            {
                Vp8Predict.ReleaseRefFrame(refFrames[GoldenFrame]);
                refFrames[GoldenFrame] = Vp8Predict.RefFrame(refFrames[LastFrame]);
            }
            else if (ctx->ReferenceHdr.CopyGf == 2)
            {
                Vp8Predict.ReleaseRefFrame(refFrames[GoldenFrame]);
                refFrames[GoldenFrame] = Vp8Predict.RefFrame(refFrames[AltrefFrame]);
            }

            if (ctx->ReferenceHdr.RefreshGf != 0)
            {
                Vp8Predict.ReleaseRefFrame(refFrames[GoldenFrame]);
                refFrames[GoldenFrame] = Vp8Predict.RefFrame(refFrames[CurrentFrame]);
            }

            if (ctx->ReferenceHdr.RefreshArf != 0)
            {
                Vp8Predict.ReleaseRefFrame(refFrames[AltrefFrame]);
                refFrames[AltrefFrame] = Vp8Predict.RefFrame(refFrames[CurrentFrame]);
            }

            if (ctx->ReferenceHdr.RefreshLast != 0)
            {
                Vp8Predict.ReleaseRefFrame(refFrames[LastFrame]);
                refFrames[LastFrame] = Vp8Predict.RefFrame(refFrames[CurrentFrame]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeInit(DecoderCtx* ctx)
        {
            DequantGlobalInit(ctx->DequantFactors);
        }

        /// CHECK_FOR_UPDATE(lval, rval, update_flag)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void CheckForUpdate(ref uint lval, uint rval, ref uint updateFlag)
        {
            uint old = lval;
            updateFlag |= old != (lval = rval) ? 1u : 0u;
        }

        /// BITS_GET(val, bit, len): len bits, with the LSB at position bit.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static uint BitsGet(uint val, int bit, int len) => (val >> bit) & ((1u << len) - 1);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int ParseFrameHeader(byte* data, uint sz, FrameHdr* hdr)
        {
            uint raw;

            if (sz < 10)
                return CodecCorruptFrame;

            // The frame header is defined as a three-byte little endian value
            raw = (uint)(data[0] | (data[1] << 8) | (data[2] << 16));
            hdr->IsKeyframe = BitsGet(raw, 0, 1) == 0 ? 1u : 0u;
            hdr->Version = BitsGet(raw, 1, 2);
            hdr->IsExperimental = BitsGet(raw, 3, 1);
            hdr->IsShown = BitsGet(raw, 4, 1);
            hdr->Part0Sz = BitsGet(raw, 5, 19);

            if (sz <= hdr->Part0Sz + (hdr->IsKeyframe != 0 ? 10u : 3u))
                return CodecCorruptFrame;

            hdr->FrameSizeUpdated = 0;

            if (hdr->IsKeyframe != 0)
            {
                uint update = 0;

                // Keyframe header consists of a three-byte sync code followed by the width
                // and height and associated scaling factors.
                if (data[3] != 0x9d || data[4] != 0x01 || data[5] != 0x2a)
                    return CodecUnsupBitstream;

                raw = (uint)(data[6] | (data[7] << 8)
                             | (data[8] << 16) | (data[9] << 24));
                CheckForUpdate(ref hdr->KfW, BitsGet(raw, 0, 14), ref update);
                CheckForUpdate(ref hdr->KfScaleW, BitsGet(raw, 14, 2), ref update);
                CheckForUpdate(ref hdr->KfH, BitsGet(raw, 16, 14), ref update);
                CheckForUpdate(ref hdr->KfScaleH, BitsGet(raw, 30, 2), ref update);

                hdr->FrameSizeUpdated = update;

                if (hdr->KfW == 0 || hdr->KfH == 0)
                    return CodecUnsupBitstream;
            }

            return CodecOk;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeDestroy(DecoderCtx* ctx)
        {
            Vp8Predict.Destroy(ctx);
            Vp8Tokens.Destroy(ctx);
            Vp8Modes.Destroy(ctx);
        }
    }
}
