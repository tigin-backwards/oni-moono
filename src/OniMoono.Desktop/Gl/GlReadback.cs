using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Brutal.OpenGlApi;
using OniMoono.Engine.Rendering;
using UnityEngine;
using N = Brutal.OpenGlApi.OpenGl.Native;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Gl
{
    /// A render texture read back without waiting (AsyncGPUReadback): glReadPixels into a pixel
    /// buffer and a fence after it, on the GL thread with the frame's other work; once a frame
    /// (Poll) the GL thread looks at the fence, and when the GPU has passed it maps the buffer.
    /// An 8-bit colour target read as RGBA32 is read as bytes, and the mapping is the request's
    /// data; past BandBytes it is first copied on the GPU and the copy read a band a frame. Any
    /// other read is read as floats, as ReadPixels reads it, and converted on a pool thread.
    /// Nothing here makes the main thread wait, but Wait.
    internal sealed unsafe class GlReadback : DeviceReadback
    {
        const uint PixelPackBuffer = 0x88EB, StreamRead = 0x88E1, MapRead = 0x0001;
        const uint SyncGpuCommandsComplete = 0x9117, SyncFlushCommands = 0x0001, TimeoutExpired = 0x911B, WaitFailed = 0x911D;
        const ulong WaitSliceNanoseconds = 100_000_000;

        readonly GlWorker worker;
        readonly int width, height;
        readonly TextureFormat format;
        readonly bool bytes;
        readonly long bufferBytes;
        // GL thread: the pixel buffer, its fence, its mapping.
        uint buffer;
        GlSync fence;
        bool fenced;
        byte* mapped;
        // Main thread: a Check is queued.
        bool checkQueued;
        // The conversion of float texels into `format`, and where it went.
        Task conversion;
        byte* converted;

        /// Whether a read-back of `source` as `format` goes as bytes: an 8-bit colour target
        /// (GL_RGBA8, as GlTextures makes them) read as RGBA32, whose bytes are the texels.
        public static bool ReadsBytes(RenderTextureNative source, TextureFormat format) => format == TextureFormat.RGBA32 && EightBit(source);

        /// An 8-bit RGBA colour target (GL_RGBA8 for each of these formats): read as bytes, its
        /// texels are what reading them as floats and rounding gives.
        public static bool EightBit(RenderTextureNative source)
            => source.ColorFormat is UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm or UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB
                or UnityEngine.Experimental.Rendering.GraphicsFormat.B8G8R8A8_UNorm or UnityEngine.Experimental.Rendering.GraphicsFormat.B8G8R8A8_SRGB;

        public GlReadback(GlWorker worker, int width, int height, TextureFormat format, bool bytes)
        {
            this.worker = worker;
            this.width = width;
            this.height = height;
            this.format = format;
            this.bytes = bytes;
            bufferBytes = (long)width * height * (bytes ? 4 : 16);
        }

        /// Bytes read back in one frame, at most: a larger 8-bit read is copied on the GPU first
        /// (the target as it is now), and the copy read a band of rows a frame, so that no frame of
        /// the GL thread's carries the whole read (on Mesa's D3D12 driver, 400 MB of a timelapse
        /// snapshot hold the GL thread about 230 ms).
        internal const long BandBytes = 32L << 20;

        // GL thread: the copy read in bands, and the rows read so far.
        uint copy, copyFramebuffer;
        int rowsRead;

        /// GL thread: the read, after everything recorded before it.
        public void Start(DeviceTexture device, bool autoMips)
        {
            try
            {
                GlTextures.PrepareForSampling(device, autoMips);
                buffer = GlApi.GenBuffer();
                N.glBindBuffer(PixelPackBuffer, new GlBuffer(buffer));
                N.glBufferData(PixelPackBuffer, (nint)bufferBytes, null, StreamRead);
                N.glBindBuffer(PixelPackBuffer, default);
                VideoMemoryLedger.Add("readback buffers", bufferBytes, 1);
                if (bytes && bufferBytes > BandBytes && device.Target == TexTarget2D && device.Id != 0)
                {
                    copy = GlApi.GenTexture();
                    GlBindings.BindTexture(TexTarget2D, copy);
                    N.glTexStorage2D(TexTarget2D, 1, Rgba8, width, height);
                    N.glCopyImageSubData(device.Id, TexTarget2D, 0, 0, 0, 0, copy, TexTarget2D, 0, 0, 0, 0, width, height, 1);
                    copyFramebuffer = GlApi.GenFramebuffer();
                    GlApi.BindFramebuffer(ReadFramebuffer, copyFramebuffer);
                    N.glFramebufferTexture2D(ReadFramebuffer, ColorAttachment0, TexTarget2D, copy, 0);
                    VideoMemoryLedger.Add("readback buffers", bufferBytes, 1);
                    ReadBand();
                    return;
                }
                GlApi.BindFramebuffer(ReadFramebuffer, device.Framebuffer);
                Read(0, height);
            }
            finally
            {
                GlDevice.InvalidateFramebufferBinding();
            }
        }

        /// GL thread: rows [y, y + rows) of the bound read framebuffer into the buffer, and the
        /// fence once every row is read.
        void Read(int y, int rows)
        {
            N.glBindBuffer(PixelPackBuffer, new GlBuffer(buffer));
            N.glPixelStorei(PackAlignment, 4);
            N.glReadPixels(0, y, width, rows, Rgba, bytes ? UnsignedByte : Float, (void*)((long)y * width * (bytes ? 4 : 16)));
            N.glBindBuffer(PixelPackBuffer, default);
            rowsRead = y + rows;
            if (rowsRead < height) return;
            fence = N.glFenceSync(SyncGpuCommandsComplete, default);
            fenced = true;
            N.glFlush();
        }

        /// GL thread: the copy's next band.
        void ReadBand()
        {
            GlApi.BindFramebuffer(ReadFramebuffer, copyFramebuffer);
            int band = (int)Math.Clamp(BandBytes / ((long)width * 4), 1, height);
            Read(rowsRead, Math.Min(band, height - rowsRead));
            GlDevice.InvalidateFramebufferBinding();
            if (rowsRead == height) DropCopy();
        }

        void DropCopy()
        {
            if (copy == 0) return;
            GlApi.DeleteFramebuffer(copyFramebuffer);
            GlApi.DeleteTexture(copy);
            copy = copyFramebuffer = 0;
            VideoMemoryLedger.Add("readback buffers", -bufferBytes, -1);
        }

        public override void Poll()
        {
            if (checkQueued || Ready) return;
            checkQueued = true;
            worker.Post(() =>
            {
                checkQueued = false;
                Check(wait: false);
            });
        }

        /// GL thread: maps the buffer once the GPU has written it (`wait`: waits for that).
        void Check(bool wait)
        {
            if (Ready || mapped != null || conversion != null) return;
            // The copy's bands: one a check, all at once when waited for.
            while (copy != 0 && rowsRead < height)
            {
                ReadBand();
                if (!wait) return;
            }
            if (!fenced)
            {
                Fail();
                return;
            }
            uint status = N.glClientWaitSync(fence, SyncFlushCommands, wait ? WaitSliceNanoseconds : 0);
            if (wait)
            {
                var slow = new OniMoono.Engine.LongWait();
                while (status == TimeoutExpired)
                {
                    slow.Slice("async readback: its fence", $"buffer {buffer}, {width}x{height}");
                    status = N.glClientWaitSync(fence, SyncFlushCommands, WaitSliceNanoseconds);
                }
                slow.End();
            }
            if (status == TimeoutExpired) return;
            N.glDeleteSync(fence);
            fenced = false;
            if (status == WaitFailed)
            {
                Fail();
                return;
            }
            N.glBindBuffer(PixelPackBuffer, new GlBuffer(buffer));
            mapped = (byte*)N.glMapBufferRange(PixelPackBuffer, 0, (nint)bufferBytes, MapRead);
            N.glBindBuffer(PixelPackBuffer, default);
            if (mapped == null)
            {
                Fail();
                return;
            }
            if (bytes)
            {
                Data = mapped;
                Size = (int)bufferBytes;
                Ready = true;
                return;
            }
            var floats = (float*)mapped;
            conversion = Task.Run(() => Convert(floats));
        }

        /// Pool thread: float texels into `format`, as ReadPixels writes them into a texture.
        void Convert(float* read)
        {
            try
            {
                long size = TextureFormats.MipSize(format, width, height);
                var into = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, size));
                var span = new Span<byte>(into, (int)size);
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        long o = ((long)y * width + x) * 4;
                        TextureFormats.Encode(format, span, width, x, y, new Color(read[o], read[o + 1], read[o + 2], read[o + 3]));
                    }
                converted = into;
                Data = into;
                Size = (int)size;
            }
            catch (Exception)
            {
                Failed = true;
            }
            Ready = true;
        }

        void Fail()
        {
            Failed = true;
            Ready = true;
        }

        public override void Wait()
        {
            if (!Ready && conversion == null)
                if (!worker.TryCall(() =>
                {
                    Check(wait: true);
                    return 0;
                }, out _))
                    Fail();
            conversion?.Wait();
        }

        public override void Release()
        {
            conversion?.Wait();
            if (converted != null) NativeMemory.Free(converted);
            converted = null;
            Data = null;
            worker.Post(() =>
            {
                DropCopy();
                if (buffer == 0) return;
                if (fenced) N.glDeleteSync(fence);
                fenced = false;
                if (mapped != null)
                {
                    N.glBindBuffer(PixelPackBuffer, new GlBuffer(buffer));
                    N.glUnmapBuffer(PixelPackBuffer);
                    N.glBindBuffer(PixelPackBuffer, default);
                }
                mapped = null;
                GlApi.DeleteBuffer(buffer);
                buffer = 0;
                VideoMemoryLedger.Add("readback buffers", -bufferBytes, -1);
            });
        }
    }
}
