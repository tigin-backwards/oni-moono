using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Desktop.D3d11
{
    internal sealed unsafe partial class D3d11Device
    {
        /// AsyncGPUReadback: the render texture's level 0 is copied into a STAGING texture with
        /// the frame's work (CopySubresourceRegion); after each Present the device thread tries
        /// `Map(READ, D3D11_MAP_FLAG_DO_NOT_WAIT)`: "Map should return DXGI_ERROR_WAS_STILL_DRAWING
        /// when the GPU blocks the CPU from accessing a resource" (D3D11_MAP_FLAG), and once it maps, the texels are copied
        /// out and the request is Ready. The main thread does not wait for the GPU unless it asks
        /// (Wait: Map without the flag, which waits).
        public override DeviceReadback BeginReadback(RenderTextureNative source, int width, int height, TextureFormat format)
        {
            if (!TextureFormats.CanEncode(format)) return null;
            var device = GlTextures.Prepare(worker, source);
            bool autoMips = source.AutoGenerateMips;
            var read = new D3d11Readback(this, width, height, format) { TopFirst = source.ScreenRows };
            worker.Post(() => StartReadback(read, device, autoMips));
            return read;
        }

        readonly List<D3d11Readback> pendingReadbacks = new();
        bool readbackLogged;

        /// Device thread: the copy, after everything recorded before it.
        void StartReadback(D3d11Readback read, DeviceTexture device, bool autoMips)
        {
            var t = device.D3d;
            if (lost != null || t?.Resource == null || t.DepthOnly || t.Target != GlConst.TexTarget2D || t.Width < read.Width || t.Height < read.Height)
            {
                read.Fail();
                return;
            }
            PrepareForSampling(t, autoMips);
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)read.Width, Height = (uint)read.Height, MipLevels = 1, ArraySize = 1, Format = t.Format, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            };
            ID3D11Texture2D* staging;
            if (Dev->lpVtbl->CreateTexture2D(Dev, &desc, null, &staging) < 0)
            {
                read.Fail();
                return;
            }
            // Level 0's bottom rows: a ScreenRows texture's are its last, stored top row first
            // (read turned over in Copy), any other's its first (D3d11Device.ReadNow).
            uint top = read.TopFirst ? (uint)(t.Height - read.Height) : 0;
            var box = new D3D11_BOX { left = 0, top = top, front = 0, right = (uint)read.Width, bottom = top + (uint)read.Height, back = 1 };
            Ctx->lpVtbl->CopySubresourceRegion(Ctx, (ID3D11Resource*)staging, 0, 0, 0, 0, t.Resource, 0, &box);
            read.Staging = staging;
            read.StagingFormat = t.Format;
            read.StartPresent = presents;
            pendingReadbacks.Add(read);
        }

        /// Device thread, after each Present: the copies the GPU has finished are read.
        void PollReadbacks()
        {
            for (int i = pendingReadbacks.Count - 1; i >= 0; i--)
                if (TryFinish(pendingReadbacks[i], wait: false)) pendingReadbacks.RemoveAt(i);
        }

        /// Maps the staging texture (waiting for the copy only with `wait`), copies its rows out
        /// and releases it; false while the GPU still works on the copy.
        bool TryFinish(D3d11Readback read, bool wait)
        {
            if (read.Staging == null) return true;
            D3D11_MAPPED_SUBRESOURCE mapped = default;
            int hr = lost != null ? -1 : Ctx->lpVtbl->Map(Ctx, (ID3D11Resource*)read.Staging, 0, D3D11_MAP.D3D11_MAP_READ, wait ? 0 : D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
            if (hr == D3D11.DXGI_ERROR_WAS_STILL_DRAWING) return false;
            if (hr < 0)
            {
                Com.Release(ref read.Staging);
                read.Fail();
                return true;
            }
            if (!readbackLogged)
            {
                // Diagnostics: the first one, and how many presents its copy took.
                readbackLogged = true;
                EngineLog.Engine($"d3d11: AsyncGPUReadback of {read.Width}x{read.Height} {read.StagingFormat} read {presents - read.StartPresent} present(s) after its copy{(wait ? ", waited for" : "")}");
            }
            try
            {
                read.Copy((byte*)mapped.pData, mapped.RowPitch);
            }
            finally
            {
                Ctx->lpVtbl->Unmap(Ctx, (ID3D11Resource*)read.Staging, 0);
                Com.Release(ref read.Staging);
            }
            return true;
        }

        /// Main thread: the copy finished now (Map waits for it).
        internal void WaitReadback(D3d11Readback read)
        {
            if (!read.Ready && !read.Converting)
                if (!worker.TryCall(() =>
                {
                    if (pendingReadbacks.Remove(read)) TryFinish(read, wait: true);
                    else if (!read.Ready && read.Staging == null && !read.Converting) read.Fail();
                    return 0;
                }, out _))
                    read.Fail();
            read.WaitConversion();
        }

        /// Main thread: frees the texels, and the staging texture if the copy was never read.
        /// The texels are freed on the device thread, after the request left the pending list:
        /// there the device thread's Copy (from PollReadbacks after a Present, or a Wait) cannot
        /// be setting them at the same time, and a conversion it started is waited for first.
        internal void ReleaseReadback(D3d11Readback read)
        {
            worker.Post(() =>
            {
                pendingReadbacks.Remove(read);
                Com.Release(ref read.Staging);
                read.FreeConverted();
            });
        }

        // ----- CopyTargetRect -----

        /// The rectangle lies inside the source (sw x sh) and, placed at `to`, inside the
        /// destination (dw x dh), with a positive size.
        internal static bool CopyFits(RectInt from, int sw, int sh, Vector2Int to, int dw, int dh)
            => from.width > 0 && from.height > 0 && from.x >= 0 && from.y >= 0 && to.x >= 0 && to.y >= 0
               && (long)from.x + from.width <= sw && (long)from.y + from.height <= sh
               && (long)to.x + from.width <= dw && (long)to.y + from.height <= dh;

        public override bool CopiesTargetRects => true;

        /// CopySubresourceRegion between the two textures' level 0. The resources "Must have
        /// compatible DXGI formats (identical or from the same type group)"
        /// (ID3D11DeviceContext::CopySubresourceRegion): this device asks for identical ones
        /// (its render-texture formats are typed, one per group), else logs a failed command. Rows from the bottom as stored: turned
        /// over in a texture stored top row first (ScreenRows), as VkDevice.CopyTargetRect. A
        /// rectangle outside either texture is refused and logged as a failed command: "If you
        /// try and copy outside the destination resource or specify a source box that is larger
        /// than the source resource, the behavior of CopySubresourceRegion is undefined", which
        /// "might result in incorrect rendering, clipping, no copy, or even the removal of the
        /// rendering device" (CopySubresourceRegion, Remarks; CopyFits). Only
        /// the loading animation uses it, for a loading screen that plays a kanim (D3d11Present).
        public override void CopyTargetRect(RenderTextureNative source, RectInt from, RenderTextureNative target, Vector2Int to)
        {
            if (source.ScreenRows != target.ScreenRows) throw new ArgumentException("the textures are rendered the other way round from each other");
            var src = GlTextures.Prepare(worker, source);
            var dst = GlTextures.Prepare(worker, target);
            worker.Post(() =>
            {
                var s = src.D3d;
                var d = dst.D3d;
                if (lost != null || s?.Resource == null || d?.Resource == null || s.DepthOnly || d.DepthOnly) return;
                if (s.Format != d.Format)
                {
                    CommandFailed(new InvalidOperationException($"d3d11: CopyTargetRect from {s.Format} into {d.Format}"));
                    return;
                }
                if (!CopyFits(from, s.Width, s.Height, to, d.Width, d.Height))
                {
                    CommandFailed(new InvalidOperationException($"d3d11: CopyTargetRect of {from} from {s.Width}x{s.Height} to {to} in {d.Width}x{d.Height}: outside a texture"));
                    return;
                }
                PrepareForSampling(s, autoGenerateMips: false);
                int sy = source.ScreenRows ? s.Height - (from.y + from.height) : from.y;
                int dy = target.ScreenRows ? d.Height - (to.y + from.height) : to.y;
                var box = new D3D11_BOX { left = (uint)from.x, top = (uint)sy, front = 0, right = (uint)(from.x + from.width), bottom = (uint)(sy + from.height), back = 1 };
                Ctx->lpVtbl->CopySubresourceRegion(Ctx, d.Resource, 0, (uint)to.x, (uint)dy, 0, s.Resource, 0, &box);
                // Its levels are rebuilt before it is sampled; a multisampled one keeps its
                // multisampled surface, which a resolve would copy over this.
                if (d.Multisampled == null) d.Dirty = true;
            });
        }
    }

    /// A render texture's copy into a STAGING texture (D3d11Device.BeginReadback). An RGBA8
    /// texture read as RGBA32 is copied row by row; any other is converted on a pool thread as
    /// ReadPixels converts it (D3d11Formats.ReadTexel, then TextureFormats.Encode).
    internal sealed unsafe class D3d11Readback(D3d11Device device, int width, int height, TextureFormat format) : DeviceReadback
    {
        public readonly int Width = width, Height = height;
        // Device thread.
        public ID3D11Texture2D* Staging;
        public DXGI_FORMAT StagingFormat;
        /// The texture is stored top row first (ScreenRows): its rows are turned over.
        public bool TopFirst;
        /// The device's present count when the copy was recorded (diagnostics).
        public long StartPresent;
        Task conversion;
        byte* owned;

        public bool Converting => conversion != null;

        public void Fail()
        {
            Failed = true;
            Ready = true;
        }

        /// Device thread, with the staging texture mapped: its rows (RowPitch apart) copied out.
        /// Rows from the bottom, as ReadNow reads them.
        public void Copy(byte* mapped, uint rowPitch)
        {
            int texel = D3d11Formats.TexelBytes(StagingFormat);
            long bytes = (long)Width * Height * texel;
            var rows = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes));
            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy(mapped + (long)(TopFirst ? Height - 1 - y : y) * rowPitch, rows + (long)y * Width * texel, Width * texel, Width * texel);
            if (format == TextureFormat.RGBA32 && StagingFormat == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM)
            {
                owned = rows;
                Data = rows;
                Size = (int)bytes;
                Ready = true;
                return;
            }
            var from = StagingFormat;
            conversion = Task.Run(() =>
            {
                try
                {
                    long size = TextureFormats.MipSize(format, Width, Height);
                    var into = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, size));
                    var span = new Span<byte>(into, (int)size);
                    for (int y = 0; y < Height; y++)
                        for (int x = 0; x < Width; x++)
                            TextureFormats.Encode(format, span, Width, x, y, D3d11Formats.ReadTexel(from, rows + ((long)y * Width + x) * texel));
                    owned = into;
                    Data = into;
                    Size = (int)size;
                }
                catch (Exception)
                {
                    Failed = true;
                }
                finally
                {
                    NativeMemory.Free(rows);
                }
                Ready = true;
            });
        }

        public void WaitConversion() => conversion?.Wait();

        public void FreeConverted()
        {
            conversion?.Wait();
            if (owned != null) NativeMemory.Free(owned);
            owned = null;
            Data = null;
        }

        public override void Wait() => device.WaitReadback(this);

        public override void Release() => device.ReleaseReadback(this);
    }
}
