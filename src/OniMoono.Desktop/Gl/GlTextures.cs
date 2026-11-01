using System.Collections.Generic;
using System;
using Brutal.OpenGlApi;
using OniMoono.Engine.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using N = Brutal.OpenGlApi.OpenGl.Native;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Gl
{
    /// The device side of a texture or render texture. The GL fields belong to the GL thread;
    /// the main thread keeps its own record of what it has asked for (Recorded*).
    internal sealed class DeviceTexture
    {
        // Main thread: the versions and render texture shape already recorded for the GL thread,
        // and the target the texture has (sampler matching).
        public int RecordedContent = -1, RecordedSampler = -1;
        /// The texture has no image (zero width or height, as `new Texture2D(0, 0)` before it is
        /// filled): GL can make none, and samplers read it as no texture.
        public bool RecordedEmpty;
        public RenderShape? RecordedShape;
        public uint RecordedTarget = TexTarget2D;
        /// Main thread: the background upload of its loaded texels, until the main thread takes
        /// it back (GlBackgroundUploads.Claim).
        public BackgroundUpload Background;

        public uint Id;
        public uint Target = TexTarget2D;
        public int Width, Height, Depth;
        public int Levels = 1;
        /// The format the 2D storage was specified with (uploads into unchanged storage update it
        /// in place).
        public TextureFormat StorageFormat = (TextureFormat)(-1);

        // Render textures.
        public uint Framebuffer;
        public uint DepthRenderbuffer;
        public uint MultisampleColor;
        public uint MultisampleFramebuffer;
        public int Samples = 1;
        public bool DepthOnly;
        /// Rendered to since the last resolve / mip generation.
        public bool Dirty;

        /// Bindless (GlBindless): the sampler object the texture's sampler settings select, and the
        /// resident handles made for it by sampler object.
        public uint Sampler;
        public System.Collections.Generic.Dictionary<uint, ulong> Handles;

        /// The texels last uploaded, for a texture re-uploaded into unchanged storage
        /// (GlTextures.UploadChanged): the next upload sends only the rows that differ.
        public byte[] UploadedTexels;

        /// Multi-target framebuffers this texture is attached to (GlDevice.ForgetFramebuffers).
        public List<GlDevice.MrtFramebuffer> MrtFramebuffers;

        public void Delete()
        {
            GlDevice.ForgetFramebuffers(this);
            // A target deleted while bound: the next draw binds the current target again.
            if (Framebuffer != 0) GlDevice.InvalidateFramebufferBinding();
            GlBindless.Release(this);
            if (Id != 0) GlBindings.DeleteTexture(Id);
            if (Framebuffer != 0) GlApi.DeleteFramebuffer(Framebuffer);
            if (MultisampleFramebuffer != 0) GlApi.DeleteFramebuffer(MultisampleFramebuffer);
            if (DepthRenderbuffer != 0) GlApi.DeleteRenderbuffer(DepthRenderbuffer);
            if (MultisampleColor != 0) GlApi.DeleteRenderbuffer(MultisampleColor);
            Id = Framebuffer = MultisampleFramebuffer = DepthRenderbuffer = MultisampleColor = 0;
        }
    }

    /// The surfaces a render texture asks for.
    internal readonly record struct RenderShape(int Width, int Height, int Levels, int Samples, GraphicsFormat ColorFormat, int DepthBits)
    {
        public static RenderShape Of(RenderTextureNative rt) => new(rt.Width, rt.Height,
            rt.UseMipMap ? TextureFormats.FullMipCount(rt.Width, rt.Height) : 1, Math.Max(1, rt.AntiAliasing), rt.ColorFormat, rt.DepthBits);
    }

    /// A texture's texels as they were when recorded, for the GL thread to upload: a copy, or
    /// for a non-readable texture its memory itself (Unity keeps no CPU copy of those).
    internal sealed unsafe class TexelUpload
    {
        public TextureFormat Format;
        public int Width, Height, Depth, MipCount;
        public uint Target;
        public PixelMemory Memory;

        public static TexelUpload Take(TexelTextureNative t)
        {
            var u = new TexelUpload
            {
                Format = t.Format, Width = t.Width, Height = t.Height, Depth = t.Depth, MipCount = t.MipCount,
                Target = t switch { Texture2DArrayNative => TexTarget2DArray, Texture3DNative => TexTarget3D, _ => TexTarget2D },
            };
            if (!t.Readable) u.Memory = t.TakeTexels();
            else if (t.Memory is { } m)
            {
                u.Memory = UploadBuffers.Rent(m.Size);
                u.Pooled = true;
                u.Copying = TexelCopies.Start(t, m, u.Memory);
            }
            return u;
        }

        /// Memory is a pooled copy (returned after the upload) rather than the texture's own.
        public bool Pooled;

        /// The copy into Memory, when it is still being made on another thread (TexelCopies).
        public System.Threading.Tasks.Task Copying;

        public long MipSize(int mip) => TextureFormats.MipSize(Format, Math.Max(1, Width >> mip), Math.Max(1, Height >> mip))
                                        * (Target == TexTarget3D ? Math.Max(1, Depth >> mip) : 1);

        public long MipOffset(int mip)
        {
            if (Target != TexTarget3D) return TextureFormats.MipOffset(Format, Width, Height, mip);
            long offset = 0;
            for (int m = 0; m < mip; m++) offset += MipSize(m);
            return offset;
        }

        /// Uploads into the device texture (GL thread) and frees the texels.
        public void Execute(DeviceTexture g)
        {
            Copying?.Wait();
            try
            {
                if (Target == TexTarget2DArray) GlTextures.UploadArray(this, g);
                else if (Target == TexTarget3D) GlTextures.Upload3D(this, g);
                else GlTextures.Upload2D(this, g);
            }
            finally
            {
                if (Pooled) UploadBuffers.Return(Memory);
                else Memory?.Free();
                Memory = null;
            }
        }
    }

    /// Buffers for the texel copies of readable textures, reused by size: the game re-applies
    /// some textures every frame (a large colony's animation batches, about 20 MB a frame), and a
    /// fresh allocation for each copy page-faults on every page it writes (4 ms a frame against
    /// 0.3 for reused memory, measured). The main thread rents, the GL thread returns after
    /// uploading. At most PoolLimit bytes are kept; beyond that the sizes rented least recently
    /// are released first, so buffers of textures uploaded once (at load) do not crowd out the
    /// ones re-uploaded every frame.
    internal static class UploadBuffers
    {
        const long PoolLimit = 128L << 20;

        sealed class SizeClass
        {
            public readonly Stack<PixelMemory> Free = new();
            public long LastRent;
        }

        static readonly Dictionary<long, SizeClass> classes = new();
        static long pooled, rents;

        public static PixelMemory Rent(long size)
        {
            lock (classes)
            {
                if (!classes.TryGetValue(size, out var c)) classes[size] = c = new SizeClass();
                c.LastRent = ++rents;
                if (c.Free.Count > 0)
                {
                    pooled -= size;
                    return c.Free.Pop();
                }
            }
            return new PixelMemory(size);
        }

        public static void Return(PixelMemory memory)
        {
            if (memory == null) return;
            List<PixelMemory> released = null;
            lock (classes)
            {
                var own = classes[memory.Size];
                while (pooled + memory.Size > PoolLimit)
                {
                    // The class rented least recently that still holds buffers.
                    SizeClass oldest = null;
                    foreach (var c in classes.Values)
                        if (c.Free.Count > 0 && (oldest == null || c.LastRent < oldest.LastRent)) oldest = c;
                    if (oldest == null || oldest.LastRent >= own.LastRent) break;
                    var victim = oldest.Free.Pop();
                    pooled -= victim.Size;
                    (released ??= new()).Add(victim);
                }
                if (pooled + memory.Size <= PoolLimit)
                {
                    own.Free.Push(memory);
                    pooled += memory.Size;
                    memory = null;
                }
            }
            memory?.Free();
            if (released != null) foreach (var m in released) m.Free();
        }
    }

    /// How a Unity texture format is stored and uploaded in GL.
    /// `Decompress`: a compressed format the context cannot sample, uploaded decoded (as `Format`/`Type`).
    internal readonly record struct GlFormat(uint Internal, uint Format, uint Type, bool Compressed, uint[] Swizzle = null, bool Decompress = false);

    internal static unsafe class GlTextures
    {
        static readonly uint[] AlphaOnly = [Zero, Zero, Zero, Red];
        static readonly uint[] ArgbBytes = [Green, Blue, Alpha, Red];

        /// The context's compression support (set when the device starts).
        public static GlCaps Caps;

        /// Unity's texture formats in GL: ARGB32 is stored as its bytes and swizzled; Alpha8 is a
        /// red-channel texture read as (0, 0, 0, a); gamma colour space uses no sRGB formats.
        /// Compressed formats the context cannot sample are decoded on upload, as Unity
        /// decompresses them: to RGBA8, BC6H to half-float RGBA.
        /// `volume`: for a 3D texture (TEXTURE_3D). S3TC and RGTC images are 2D only:
        /// CompressedTexImage3D with an S3TC format is INVALID_ENUM (EXT_texture_compression_s3tc,
        /// Errors), and with an RGTC format and a target other than a 2D array or cube map
        /// (array) INVALID_OPERATION (OpenGL 4.6 section 8.7), so 3D ones are decoded. BPTC has
        /// no such restriction: its 3D images are slices of 2D blocks (ARB_texture_compression_bptc).
        public static GlFormat Of(TextureFormat f, bool volume = false)
        {
            bool sampled = f switch
            {
                TextureFormat.DXT1 or TextureFormat.DXT5 => !volume && (Caps?.S3tc ?? true),
                TextureFormat.BC6H or TextureFormat.BC7 => Caps?.Bptc ?? true,
                TextureFormat.BC4 or TextureFormat.BC5 => !volume && (Caps?.Rgtc ?? true),
                _ => true,
            };
            if (sampled && Native(f) is { } native) return native;
            if (TextureFormats.IsBlockCompressed(f) && TextureFormats.CanDecode(f))
                return f == TextureFormat.BC6H ? new(Rgba16F, Rgba, HalfFloat, false, null, true) : new(Rgba8, Rgba, UnsignedByte, false, null, true);
            throw new NotSupportedException($"Texture format {f} has no OpenGL equivalent in this device.");
        }

        static GlFormat? Native(TextureFormat f) => f switch
        {
            TextureFormat.Alpha8 => new(R8, RedFormat, UnsignedByte, false, AlphaOnly),
            TextureFormat.R8 => new(R8, RedFormat, UnsignedByte, false),
            TextureFormat.RG16 => new(Rg8, Rg, UnsignedByte, false),
            TextureFormat.RGB24 => new(Rgb8, Rgb, UnsignedByte, false),
            TextureFormat.RGBA32 => new(Rgba8, Rgba, UnsignedByte, false),
            TextureFormat.ARGB32 => new(Rgba8, Rgba, UnsignedByte, false, ArgbBytes),
            TextureFormat.BGRA32 => new(Rgba8, Bgra, UnsignedByte, false),
            TextureFormat.ARGB4444 => new(Rgba4, Bgra, UnsignedShort4444Rev, false),
            TextureFormat.RGBA4444 => new(Rgba4, Rgba, UnsignedShort4444, false),
            TextureFormat.RGB565 => new(Rgb565, Rgb, UnsignedShort565, false),
            TextureFormat.R16 => new(R16, RedFormat, UnsignedShort, false),
            TextureFormat.RG32 => new(Rg16, Rg, UnsignedShort, false),
            TextureFormat.RGB48 => new(Rgb16, Rgb, UnsignedShort, false),
            TextureFormat.RGBA64 => new(Rgba16, Rgba, UnsignedShort, false),
            TextureFormat.RHalf => new(R16F, RedFormat, HalfFloat, false),
            TextureFormat.RGHalf => new(Rg16F, Rg, HalfFloat, false),
            TextureFormat.RGBAHalf => new(Rgba16F, Rgba, HalfFloat, false),
            TextureFormat.RFloat => new(R32F, RedFormat, Float, false),
            TextureFormat.RGFloat => new(Rg32F, Rg, Float, false),
            TextureFormat.RGBAFloat => new(Rgba32F, Rgba, Float, false),
            TextureFormat.DXT1 => new(CompressedRgbS3tcDxt1, 0, 0, true),
            TextureFormat.DXT5 => new(CompressedRgbaS3tcDxt5, 0, 0, true),
            TextureFormat.BC4 => new(CompressedRedRgtc1, 0, 0, true),
            TextureFormat.BC5 => new(CompressedRgRgtc2, 0, 0, true),
            TextureFormat.BC6H => new(CompressedRgbBptcUnsignedFloat, 0, 0, true),
            TextureFormat.BC7 => new(CompressedRgbaBptcUnorm, 0, 0, true),
            _ => null,
        };

        /// One level (or slice) of texels in the GL format: decoded first when the format is not sampled.
        static void Texels(TextureFormat format, GlFormat f, byte* source, long length, int w, int h, Action<IntPtr> upload)
        {
            if (!f.Decompress)
            {
                upload((IntPtr)source);
                return;
            }
            var decoded = new byte[(long)w * h * (f.Type == HalfFloat ? 8 : 4)];
            fixed (byte* d = decoded)
            {
                TextureFormats.DecodeLevel(format, source, length, w, h, d, halfFloat: f.Type == HalfFloat);
                upload((IntPtr)d);
            }
        }

        /// Render texture colour formats.
        public static uint InternalOf(GraphicsFormat f) => f switch
        {
            GraphicsFormat.R8G8B8A8_UNorm or GraphicsFormat.R8G8B8A8_SRGB or GraphicsFormat.B8G8R8A8_UNorm or GraphicsFormat.B8G8R8A8_SRGB => Rgba8,
            GraphicsFormat.R16G16B16A16_SFloat => Rgba16F,
            GraphicsFormat.R32G32B32A32_SFloat => Rgba32F,
            GraphicsFormat.R32G32_SFloat => Rg32F,
            GraphicsFormat.R16G16_SFloat => Rg16F,
            GraphicsFormat.R32_SFloat => R32F,
            GraphicsFormat.R16_SFloat => R16F,
            GraphicsFormat.R8_UNorm => R8,
            GraphicsFormat.R8G8_UNorm => Rg8,
            GraphicsFormat.R16_UNorm => R16,
            GraphicsFormat.R16G16_UNorm => Rg16,
            GraphicsFormat.R16G16B16A16_UNorm => Rgba16,
            GraphicsFormat.B5G6R5_UNormPack16 => Rgb565,
            GraphicsFormat.B4G4R4A4_UNormPack16 => Rgba4,
            GraphicsFormat.B5G5R5A1_UNormPack16 => Rgb5A1,
            GraphicsFormat.A2B10G10R10_UNormPack32 => Rgb10A2,
            GraphicsFormat.B10G11R11_UFloatPack32 => R11G11B10F,
            // Integer formats are not offered: their storage, clears, filtering and blending all
            // differ from the float path this device renders with (no game target uses them).
            _ => throw new NotSupportedException($"Render texture format {f} has no OpenGL equivalent in this device."),
        };

        /// The device texture for an engine texture (main thread): its creation, changed texels
        /// and sampler settings are recorded for the GL thread.
        public static DeviceTexture Prepare(GlWorker worker, TextureNative t)
        {
            if (t.Gpu is not DeviceTexture g)
            {
                g = new DeviceTexture();
                t.Gpu = g;
                GraphicsDevice.Adopt(t, g);
            }
            // Still on its way in the background: uploaded in this frame, ahead of what follows.
            if (g.Background != null) GlBackgroundUploads.Claim(g, worker);
            switch (t)
            {
                case RenderTextureNative rt:
                {
                    var shape = RenderShape.Of(rt);
                    g.RecordedTarget = TexTarget2D;
                    if (g.RecordedShape != shape)
                    {
                        g.RecordedShape = shape;
                        g.RecordedSampler = -1;
                        PostRenderTexture(worker, g, shape, rt.Name);
                    }
                    break;
                }
                case TexelTextureNative tt when g.RecordedContent != tt.ContentVersion:
                {
                    g.RecordedEmpty = tt.Width <= 0 || tt.Height <= 0 || tt.Depth <= 0;
                    if (g.RecordedEmpty)
                    {
                        g.RecordedContent = tt.ContentVersion;
                        break;
                    }
                    var upload = TexelUpload.Take(tt);
                    g.RecordedContent = tt.ContentVersion;
                    g.RecordedTarget = upload.Target;
                    g.RecordedSampler = -1;
                    PostUpload(worker, g, upload);
                    break;
                }
            }
            if (!g.RecordedEmpty && g.RecordedSampler != t.SamplerVersion)
            {
                g.RecordedSampler = t.SamplerVersion;
                PostSampler(worker, g, new SamplerSettings(t.FilterMode, t.WrapU, t.WrapV, t.WrapW, t.MipBias, t.Aniso));
            }
            return g;
        }

        /// The texture's device side is recorded as it is now (shape, content, sampler settings):
        /// Prepare would record nothing. Reads only.
        public static bool IsCurrent(TextureNative t)
        {
            if (t.Gpu is not DeviceTexture g || g.Background != null) return false;
            switch (t)
            {
                case RenderTextureNative rt:
                    if (g.RecordedShape != RenderShape.Of(rt)) return false;
                    break;
                case TexelTextureNative tt:
                    if (g.RecordedContent != tt.ContentVersion) return false;
                    break;
            }
            return g.RecordedEmpty || g.RecordedSampler == t.SamplerVersion;
        }

        // The GL-thread work Prepare posts, each in its own method so that its closure is
        // allocated only when there is work (Prepare runs for every texture of every draw).
        static void PostRenderTexture(GlWorker worker, DeviceTexture g, RenderShape shape, string name) => worker.Post(() =>
        {
            long t = GlTimings.Now;
            EnsureRenderTexture(g, shape, name);
            GlTimings.Targets += GlTimings.Now - t;
        });

        internal static void PostUpload(GlWorker worker, DeviceTexture g, TexelUpload upload) => worker.Post(() =>
        {
            long t = GlTimings.Now;
            uint q = GlGpuProfile.Enabled ? GlGpuProfile.Begin() : 0;
            upload.Execute(g);
            if (GlGpuProfile.Enabled) GlGpuProfile.End($"texture upload {upload.Width}x{upload.Height} {upload.Format}", q);
            GlTimings.TextureUploads += GlTimings.Now - t;
            System.Threading.Interlocked.Add(ref OniMoono.Engine.StartupStats.GlUploadTicks, GlTimings.Now - t);
        });

        internal static void PostSampler(GlWorker worker, DeviceTexture g, SamplerSettings sampler) => worker.Post(() => ApplySampler(sampler, g));

        internal static void ApplySamplerNow(SamplerSettings sampler, DeviceTexture g) => ApplySampler(sampler, g);

        /// Texture content uploads and their bytes since the last frame counters (--frame-stats),
        /// and re-uploads whose texels had not changed (none sent).
        public static long Uploads, UploadBytes, UnchangedUploads;

        /// Binds the texture for an upload; one that will specify its image again (respecify)
        /// and has bindless handles, which fix its layout, gets a new name first.
        static void Begin(DeviceTexture g, uint target, bool respecify = true)
        {
            if (g.Id == 0 || g.Target != target || (respecify && GlBindless.HasHandles(g)))
            {
                if (g.Id != 0) { GlBindless.Release(g); GlBindings.DeleteTexture(g.Id); }
                g.Id = GlApi.GenTexture();
                g.Target = target;
                // The texels kept for UploadChanged belong to the old texture: compared with the
                // next upload into the new one (the same byte count in another shape or format,
                // 256x128 after 128x256), they would hide rows that changed.
                g.UploadedTexels = null;
            }
            GlBindings.BindTexture(target, g.Id);
            N.glPixelStorei(UnpackAlignment, 1);
        }

        static void Swizzle(uint target, uint[] s)
        {
            s ??= [Red, Green, Blue, Alpha];
            N.glTexParameteri(target, TextureSwizzleR, (int)s[0]);
            N.glTexParameteri(target, TextureSwizzleG, (int)s[1]);
            N.glTexParameteri(target, TextureSwizzleB, (int)s[2]);
            N.glTexParameteri(target, TextureSwizzleA, (int)s[3]);
        }

        /// Storage is allocated once per size, format and mip count (immutable, glTexStorage2D)
        /// and each level is filled with sub-image uploads: defining levels one by one
        /// (glTexImage2D) makes drivers reallocate the texture as the chain grows. A texture whose
        /// size, format or mip count changes gets a new texture name. Texels are passed from
        /// memory, which GL copies before the call returns. (Staging them through a persistently
        /// mapped pixel-unpack ring, fenced per frame, corrupted textures under Mesa's d3d12
        /// driver once the GPU ran a frame or two behind the render thread: background textures
        /// turned white for some frames. Explicit flushing did not help, a full wait per frame
        /// did.)
        internal static void Upload2D(TexelUpload t, DeviceTexture g)
        {
            var f = Of(t.Format);
            int levels = Math.Max(1, t.MipCount);
            bool sameStorage = g.Id != 0 && g.Target == TexTarget2D && g.StorageFormat == t.Format
                               && g.Width == t.Width && g.Height == t.Height && g.Levels == levels;
            if (!sameStorage && g.Id != 0)
            {
                GlBindless.Release(g);
                GlBindings.DeleteTexture(g.Id);
                g.Id = 0;
            }
            Begin(g, TexTarget2D, respecify: !sameStorage);
            if (!sameStorage)
            {
                N.glTexStorage2D(TexTarget2D, levels, f.Internal, t.Width, t.Height);
                N.glTexParameteri(TexTarget2D, TextureBaseLevel, 0);
                N.glTexParameteri(TexTarget2D, TextureMaxLevel, levels - 1);
                Swizzle(TexTarget2D, f.Swizzle);
                g.StorageFormat = t.Format;
                g.Width = t.Width;
                g.Height = t.Height;
                g.Levels = levels;
            }
            var memory = t.Memory;
            if (memory != null && sameStorage && UploadChanged(t, g, f, levels)) return;
            if (memory != null) Uploads++;
            if (memory != null && GlGpuProfile.Enabled) GlGpuProfile.CountRepeat(g, $"{t.Width}x{t.Height} {t.Format}", new ReadOnlySpan<byte>(memory.Pointer, (int)memory.Size));
            if (memory != null)
                for (int m = 0; m < levels; m++)
                {
                    int w = Math.Max(1, t.Width >> m), h = Math.Max(1, t.Height >> m);
                    UploadBytes += t.MipSize(m);
                    long length = t.MipSize(m);
                    byte* p = memory.Pointer + t.MipOffset(m);
                    if (f.Compressed) N.glCompressedTexSubImage2D(TexTarget2D, m, 0, 0, w, h, f.Internal, (int)length, p);
                    else Texels(t.Format, f, p, length, w, h, d => N.glTexSubImage2D(TexTarget2D, m, 0, 0, w, h, f.Format, f.Type, (void*)d));
                }
        }

        /// Largest texture whose last upload is kept to compare with the next.
        const long UploadedTexelsLimit = 4 << 20;

        static readonly bool VerifyPartialUploads = Environment.GetEnvironmentVariable("ONI_VERIFY_PARTIAL_UPLOADS") == "1";
        static long partialVerified, partialWrong;

        /// Uploads only the rows between the first and the last that differ from the texture's
        /// previous upload, or none: the game re-applies its animation batches' data textures every
        /// frame, mostly unchanged (2minnow: half of the 256x256 ones identical, about 2% of the
        /// others' rows changed). One level, uploaded as stored; the first re-upload into unchanged
        /// storage keeps a copy of the texels and returns false (a full upload).
        static bool UploadChanged(TexelUpload t, DeviceTexture g, GlFormat f, int levels)
        {
            long length = t.MipSize(0);
            if (levels != 1 || f.Compressed || f.Decompress || length > UploadedTexelsLimit || length % t.Height != 0) return false;
            var now = new ReadOnlySpan<byte>(t.Memory.Pointer, (int)length);
            var before = g.UploadedTexels;
            if (before == null || before.Length != length)
            {
                g.UploadedTexels = now.ToArray();
                return false;
            }
            int rowBytes = (int)(length / t.Height), first = 0, last = t.Height - 1;
            while (first < t.Height && now.Slice(first * rowBytes, rowBytes).SequenceEqual(before.AsSpan(first * rowBytes, rowBytes))) first++;
            if (first < t.Height)
            {
                while (now.Slice(last * rowBytes, rowBytes).SequenceEqual(before.AsSpan(last * rowBytes, rowBytes))) last--;
                int rows = last - first + 1;
                N.glTexSubImage2D(TexTarget2D, 0, 0, first, t.Width, rows, f.Format, f.Type, t.Memory.Pointer + (long)first * rowBytes);
                now.Slice(first * rowBytes, rows * rowBytes).CopyTo(before.AsSpan(first * rowBytes));
                Uploads++;
                UploadBytes += (long)rows * rowBytes;
            }
            else UnchangedUploads++;
            if (VerifyPartialUploads) VerifyUploaded(g, f, now);
            return true;
        }

        /// The texture's whole level read back from GL against the texels a full upload would
        /// have sent (ONI_VERIFY_PARTIAL_UPLOADS).
        static void VerifyUploaded(DeviceTexture g, GlFormat f, ReadOnlySpan<byte> expected)
        {
            var read = new byte[expected.Length];
            // Rows packed without padding, as the texels are (GL pads them to 4 bytes by default).
            N.glPixelStorei(PackAlignment, 1);
            fixed (byte* p = read) N.glGetTexImage(TexTarget2D, 0, f.Format, f.Type, p);
            N.glPixelStorei(PackAlignment, 4);
            partialVerified++;
            if (!expected.SequenceEqual(read)) partialWrong++;
            if (partialVerified % 2000 == 0) OniMoono.Engine.EngineLog.Engine($"partial uploads: {partialVerified} verified, {partialWrong} wrong");
        }

        internal static void UploadArray(TexelUpload t, DeviceTexture g)
        {
            // The format first: one with no GL equivalent throws before a texture name is made,
            // which would have no image (incomplete, and no bindless handle).
            var f = Of(t.Format);
            Begin(g, TexTarget2DArray);
            var memory = t.Memory;
            int levels = Math.Max(1, t.MipCount);
            long sliceSize = TextureFormats.ImageSize(t.Format, t.Width, t.Height, t.MipCount);
            for (int m = 0; m < levels; m++)
            {
                int w = Math.Max(1, t.Width >> m), h = Math.Max(1, t.Height >> m);
                long mipOffset = TextureFormats.MipOffset(t.Format, t.Width, t.Height, m);
                int mipSize = (int)TextureFormats.MipSize(t.Format, w, h);
                if (f.Compressed) N.glCompressedTexImage3D(TexTarget2DArray, m, f.Internal, w, h, t.Depth, 0, mipSize * t.Depth, null);
                else N.glTexImage3D(TexTarget2DArray, m, (int)f.Internal, w, h, t.Depth, 0, f.Format, f.Type, null);
                if (memory == null) continue;
                // Slices are stored one after another, each with its whole mip chain.
                for (int s = 0; s < t.Depth; s++)
                {
                    byte* p = memory.Pointer + s * sliceSize + mipOffset;
                    if (f.Compressed) N.glCompressedTexSubImage3D(TexTarget2DArray, m, 0, 0, s, w, h, 1, f.Internal, mipSize, p);
                    else Texels(t.Format, f, p, mipSize, w, h, d => N.glTexSubImage3D(TexTarget2DArray, m, 0, 0, s, w, h, 1, f.Format, f.Type, (void*)d));
                }
            }
            N.glTexParameteri(TexTarget2DArray, TextureBaseLevel, 0);
            N.glTexParameteri(TexTarget2DArray, TextureMaxLevel, levels - 1);
            Swizzle(TexTarget2DArray, f.Swizzle);
            g.Width = t.Width;
            g.Height = t.Height;
            g.Depth = t.Depth;
            g.Levels = levels;
        }

        internal static void Upload3D(TexelUpload t, DeviceTexture g)
        {
            // The format first: one with no GL equivalent throws before a texture name is made,
            // which would have no image (incomplete, and no bindless handle).
            var f = Of(t.Format, volume: true);
            Begin(g, TexTarget3D);
            var memory = t.Memory;
            int levels = Math.Max(1, t.MipCount);
            for (int m = 0; m < levels; m++)
            {
                int w = Math.Max(1, t.Width >> m), h = Math.Max(1, t.Height >> m), d = Math.Max(1, t.Depth >> m);
                byte* p = memory == null ? null : memory.Pointer + t.MipOffset(m);
                if (f.Compressed) N.glCompressedTexImage3D(TexTarget3D, m, f.Internal, w, h, d, 0, (int)t.MipSize(m), p);
                else if (f.Decompress && p != null)
                {
                    // Compressed 3D levels are slices of 2D blocks, one after another.
                    N.glTexImage3D(TexTarget3D, m, (int)f.Internal, w, h, d, 0, f.Format, f.Type, null);
                    long slice = TextureFormats.MipSize(t.Format, w, h);
                    for (int z = 0; z < d; z++)
                        Texels(t.Format, f, p + z * slice, slice, w, h, s => N.glTexSubImage3D(TexTarget3D, m, 0, 0, z, w, h, 1, f.Format, f.Type, (void*)s));
                }
                else N.glTexImage3D(TexTarget3D, m, (int)f.Internal, w, h, d, 0, f.Format, f.Type, p);
            }
            N.glTexParameteri(TexTarget3D, TextureBaseLevel, 0);
            N.glTexParameteri(TexTarget3D, TextureMaxLevel, levels - 1);
            Swizzle(TexTarget3D, f.Swizzle);
            g.Width = t.Width;
            g.Height = t.Height;
            g.Depth = t.Depth;
            g.Levels = levels;
        }

        /// Point: nearest; Bilinear: linear within a level, nearest level; Trilinear: linear
        /// across levels.
        /// A texture's sampling settings, as recorded.
        internal readonly record struct SamplerSettings(FilterMode FilterMode, TextureWrapMode WrapU, TextureWrapMode WrapV, TextureWrapMode WrapW, float MipBias, int Aniso);

        static void ApplySampler(SamplerSettings t, DeviceTexture g)
        {
            var target = g.Target;
            if (target == TexTarget2DMultisample) return;
            if (GlBindless.Enabled)
            {
                g.Sampler = GlSamplers.Get(t, g.Levels > 1, target);
                return;
            }
            GlBindings.BindTexture(target, g.Id);
            bool mips = g.Levels > 1;
            uint min = t.FilterMode switch
            {
                FilterMode.Point => mips ? NearestMipmapNearest : Nearest,
                FilterMode.Bilinear => mips ? LinearMipmapNearest : Linear,
                _ => mips ? LinearMipmapLinear : Linear,
            };
            N.glTexParameteri(target, TextureMinFilter, (int)min);
            N.glTexParameteri(target, TextureMagFilter, (int)(t.FilterMode == FilterMode.Point ? Nearest : Linear));
            N.glTexParameteri(target, TextureWrapS, (int)Wrap(t.WrapU));
            N.glTexParameteri(target, TextureWrapT, (int)Wrap(t.WrapV));
            if (target is TexTarget3D) N.glTexParameteri(target, TextureWrapR, (int)Wrap(t.WrapW));
            N.glTexParameterf(target, TextureLodBias, t.MipBias);
            if (GlDevice.MaxAnisotropy > 1f)
                N.glTexParameterf(target, TextureMaxAnisotropy, t.FilterMode == FilterMode.Point ? 1f : Math.Clamp(t.Aniso, 1, GlDevice.MaxAnisotropy));
        }

        internal static uint Wrap(TextureWrapMode m) => m switch
        {
            TextureWrapMode.Clamp => ClampToEdge,
            TextureWrapMode.Mirror => MirroredRepeat,
            TextureWrapMode.MirrorOnce => MirrorClampToEdge,
            _ => Repeat,
        };

        /// Colour texture (or depth texture for depth-only formats), depth-stencil buffer,
        /// framebuffer, and multisampled surfaces when anti-aliased.
        static void EnsureRenderTexture(DeviceTexture g, RenderShape rt, string name)
        {
            bool depthOnly = rt.ColorFormat == GraphicsFormat.None;
            int levels = rt.Levels;
            int samples = rt.Samples;
            // A colour format with no GL equivalent throws before the old surfaces are replaced
            // by a texture name with no image.
            uint internalFormat = depthOnly ? 0 : InternalOf(rt.ColorFormat);
            uint depthFormat = DepthFormat(rt.DepthBits);
            if (samples > 1 && !depthOnly) samples = SupportedSamples(samples, internalFormat, rt.DepthBits > 0 ? depthFormat : 0);
            g.Delete();
            g.Id = GlApi.GenTexture();
            g.Target = TexTarget2D;
            g.Width = rt.Width;
            g.Height = rt.Height;
            g.Levels = levels;
            g.Samples = samples;
            g.DepthOnly = depthOnly;
            GlBindings.BindTexture(TexTarget2D, g.Id);
            if (depthOnly)
                N.glTexImage2D(TexTarget2D, 0, (int)DepthComponent32F, rt.Width, rt.Height, 0, DepthComponent, Float, null);
            else
            {
                for (int m = 0; m < levels; m++)
                    N.glTexImage2D(TexTarget2D, m, (int)internalFormat, Math.Max(1, rt.Width >> m), Math.Max(1, rt.Height >> m), 0, Rgba, Float, null);
            }
            N.glTexParameteri(TexTarget2D, TextureMaxLevel, levels - 1);
            g.Framebuffer = GlApi.GenFramebuffer();
            GlApi.BindFramebuffer(Framebuffer, g.Framebuffer);
            if (depthOnly)
            {
                N.glFramebufferTexture2D(Framebuffer, DepthAttachment, TexTarget2D, g.Id, 0);
                N.glDrawBuffer(NoneBuffer);
                N.glReadBuffer(NoneBuffer);
            }
            else
            {
                N.glFramebufferTexture2D(Framebuffer, ColorAttachment0, TexTarget2D, g.Id, 0);
                // Multisampled, the depth buffer is the multisampled framebuffer's (below): this
                // one only receives the colour resolve.
                if (rt.DepthBits > 0 && samples <= 1) AttachDepth(g, depthFormat, 0, rt);
            }
            CheckFramebuffer(rt, name);
            ClearNewSurfaces(depthOnly, rt.DepthBits > 0 && samples <= 1);
            if (samples > 1 && !depthOnly)
            {
                // Rendering goes to multisampled surfaces, resolved into the texture when it is read.
                g.MultisampleFramebuffer = GlApi.GenFramebuffer();
                GlApi.BindFramebuffer(Framebuffer, g.MultisampleFramebuffer);
                g.MultisampleColor = GlApi.GenRenderbuffer();
                N.glBindRenderbuffer(Renderbuffer, new GlRenderbuffer(g.MultisampleColor));
                N.glRenderbufferStorageMultisample(Renderbuffer, samples, internalFormat, rt.Width, rt.Height);
                N.glFramebufferRenderbuffer(Framebuffer, ColorAttachment0, Renderbuffer, new GlRenderbuffer(g.MultisampleColor));
                if (rt.DepthBits > 0) AttachDepth(g, depthFormat, samples, rt);
                CheckFramebuffer(rt, name);
                ClearNewSurfaces(false, rt.DepthBits > 0);
            }
            GlApi.BindFramebuffer(Framebuffer, 0);
            GlDevice.InvalidateFramebufferBinding();
        }

        /// The depth buffer format for a render texture's depth bits: 32-bit float, 24-bit or
        /// 16-bit depth, the first two with 8 bits of stencil.
        static uint DepthFormat(int depthBits) => depthBits > 24 ? Depth32FStencil8 : depthBits > 16 ? Depth24Stencil8 : DepthComponent16;

        /// A depth renderbuffer (multisampled when `samples` > 0) on the bound framebuffer.
        static void AttachDepth(DeviceTexture g, uint format, int samples, RenderShape rt)
        {
            g.DepthRenderbuffer = GlApi.GenRenderbuffer();
            N.glBindRenderbuffer(Renderbuffer, new GlRenderbuffer(g.DepthRenderbuffer));
            N.glRenderbufferStorageMultisample(Renderbuffer, samples, format, rt.Width, rt.Height);
            N.glFramebufferRenderbuffer(Framebuffer, format == DepthComponent16 ? DepthAttachment : DepthStencilAttachment, Renderbuffer, new GlRenderbuffer(g.DepthRenderbuffer));
        }

        /// The largest sample count up to `requested` that both formats support (depthFormat 0:
        /// none) and MAX_SAMPLES allows, 1 when there is none. A count above a format's maximum is
        /// INVALID_OPERATION (OpenGL 4.6 section 9.2.4); GetInternalformativ gives each format's
        /// supported counts, largest first (section 22.3). The same count for both, one both
        /// list: storage gets at least the count asked for, up to the next one supported
        /// (section 9.2.4), and attachments whose sample counts differ leave the framebuffer
        /// incomplete (FRAMEBUFFER_INCOMPLETE_MULTISAMPLE, section 9.4.2).
        static int SupportedSamples(int requested, uint colorFormat, uint depthFormat)
        {
            int max = GlApi.GetInteger(MaxSamples);
            int[] color = SampleCounts(colorFormat), depth = depthFormat != 0 ? SampleCounts(depthFormat) : null;
            foreach (int n in color)
                if (n <= requested && n <= max && (depth == null || Array.IndexOf(depth, n) >= 0)) return n;
            return 1;
        }

        static int[] SampleCounts(uint format)
        {
            const uint NumSampleCounts = 0x9380, SamplesQuery = 0x80A9;
            int count = 0;
            N.glGetInternalformativ(Renderbuffer, format, NumSampleCounts, 1, &count);
            var counts = new int[Math.Max(0, count)];
            if (count > 0) fixed (int* p = counts) N.glGetInternalformativ(Renderbuffer, format, SamplesQuery, count, p);
            return counts;
        }

        /// New render surfaces start cleared, as Unity's: colour (0, 0, 0, 0), depth at the far
        /// plane, stencil 0. GL leaves new storage undefined; a pass that depth-tests against a
        /// never-written depth buffer (an image effect blitting into a fresh camera target)
        /// must find it at the far plane.
        static void ClearNewSurfaces(bool depthOnly, bool hasDepth)
        {
            uint mask = 0;
            if (!depthOnly)
            {
                N.glColorMask(true, true, true, true);
                N.glClearColor(0f, 0f, 0f, 0f);
                mask |= ColorBufferBit;
            }
            if (depthOnly || hasDepth)
            {
                N.glDepthMask(true);
                N.glStencilMask(0xFF);
                N.glClearDepth(1.0);
                N.glClearStencil(0);
                mask |= DepthBufferBit | (depthOnly ? 0 : StencilBufferBit);
            }
            if (GlDevice.ScissorActive) GlApi.Enable(ScissorTest, false);
            N.glClear(mask);
            if (GlDevice.ScissorActive) GlApi.Enable(ScissorTest, true);
            GlStateCache.Invalidate();
        }

        static void CheckFramebuffer(RenderShape rt, string name)
        {
            var status = N.glCheckFramebufferStatus(Framebuffer).Id;
            if (status != FramebufferComplete)
                throw new InvalidOperationException($"Render texture '{name}' ({rt.Width}x{rt.Height} {rt.ColorFormat}, depth {rt.DepthBits}): framebuffer incomplete (0x{status:X}).");
        }

        /// Before a render texture is sampled: resolve multisampling and rebuild mips.
        public static void PrepareForSampling(DeviceTexture g, bool autoGenerateMips)
        {
            if (!g.Dirty) return;
            g.Dirty = false;
            uint q = GlGpuProfile.Enabled ? GlGpuProfile.Begin() : 0;
            PrepareDirty(g, autoGenerateMips);
            if (GlGpuProfile.Enabled) GlGpuProfile.End($"render texture resolve/mips {g.Width}x{g.Height}", q);
        }

        static void PrepareDirty(DeviceTexture g, bool autoGenerateMips)
        {
            if (g.MultisampleFramebuffer != 0)
            {
                GlApi.BindFramebuffer(ReadFramebuffer, g.MultisampleFramebuffer);
                GlApi.BindFramebuffer(DrawFramebuffer, g.Framebuffer);
                // Blits are scissored: the whole texture resolves whatever the scissor in effect.
                if (GlDevice.ScissorActive) GlApi.Enable(ScissorTest, false);
                N.glBlitFramebuffer(0, 0, g.Width, g.Height, 0, 0, g.Width, g.Height, ColorBufferBit, Nearest);
                if (GlDevice.ScissorActive) GlApi.Enable(ScissorTest, true);
                GlDevice.InvalidateFramebufferBinding();
            }
            if (g.Levels > 1 && autoGenerateMips)
            {
                GlBindings.BindTexture(TexTarget2D, g.Id);
                N.glGenerateMipmap(TexTarget2D);
            }
        }

        /// The framebuffer to render a render texture into.
        public static uint RenderFramebuffer(DeviceTexture g) => g.MultisampleFramebuffer != 0 ? g.MultisampleFramebuffer : g.Framebuffer;
    }
}

