using System;
using System.Runtime.InteropServices;
using System.Threading;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Serialization;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace OniMoono.Engine.Rendering
{
    /// Unity's TextureDimension values (the enum itself is not part of the game's API surface).
    internal enum TextureKind
    {
        Unknown = -1,
        None = 0,
        Any = 1,
        Tex2D = 2,
        Tex3D = 3,
        Cube = 4,
        Tex2DArray = 5,
        CubeArray = 6,
    }

    /// Native side of every Texture: dimensions, sampler state and the device resource.
    internal abstract class TextureNative : NativeObject
    {
        public int Width;
        public int Height;
        public TextureKind Dimension = TextureKind.Tex2D;
        public int MipCount = 1;
        public FilterMode FilterMode = FilterMode.Bilinear;
        public int Aniso = 1;
        public float MipBias;
        public TextureWrapMode WrapU, WrapV, WrapW;
        /// Data is sRGB-encoded (only meaningful in a linear-colour-space project).
        public bool Srgb = true;

        /// Bumped when sampler state changes; the device re-applies it on next use.
        public int SamplerVersion;
        /// Bumped when the contents change on the CPU side (Apply…); the device re-uploads.
        public int ContentVersion;
        /// Texture.updateCount: incremented whenever the texture is updated.
        public uint UpdateCount;

        /// The device's resource for this texture.
        public object Gpu;

        public virtual bool IsReadable => false;

        public void SamplerChanged() => SamplerVersion++;

        public void ContentChanged()
        {
            ContentVersion++;
            UpdateCount++;
        }

        protected void ReadSettings(Fields settings)
        {
            FilterMode = (FilterMode)settings.Int("m_FilterMode");
            Aniso = settings.Int("m_Aniso");
            MipBias = settings.Float("m_MipBias");
            WrapU = (TextureWrapMode)settings.Int("m_WrapU");
            WrapV = (TextureWrapMode)settings.Int("m_WrapV");
            WrapW = (TextureWrapMode)settings.Int("m_WrapW");
        }

        public virtual void ReleaseResources() => GraphicsDevice.Release(ref Gpu);
    }

    /// Texel memory of a texture: unmanaged so that NativeArrays can alias it
    /// (Texture2D.GetRawTextureData&lt;T&gt;, GetPixelData&lt;T&gt;). Freed explicitly (Destroy, a
    /// non-readable texture's upload), or when collected: UnloadUnusedAssets reclaims textures
    /// nothing references, and those are never destroyed.
    internal sealed unsafe class PixelMemory
    {
        public byte* Pointer { get; private set; }
        public long Size { get; private set; }

        /// Bytes allocated and not yet freed, process-wide (leak checks).
        static long liveBytes;
        public static long LiveBytes => Interlocked.Read(ref liveBytes);

        public PixelMemory(long size, byte fill) : this(size)
        {
            new Span<byte>(Pointer, checked((int)size)).Fill(fill);
        }

        /// Uninitialized memory (a pooled upload buffer that a copy fills).
        public PixelMemory(long size)
        {
            Size = size;
            Pointer = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(size, 1), 16);
            Interlocked.Add(ref liveBytes, size);
        }

        public PixelMemory(ReadOnlySpan<byte> data) : this(data.Length)
        {
            data.CopyTo(new Span<byte>(Pointer, data.Length));
        }

        ~PixelMemory() => Free();

        public Span<byte> Span => new(Pointer, checked((int)Size));

        public Span<byte> Slice(long offset, long length) => new(Pointer + offset, checked((int)length));

        public void Free()
        {
            if (Pointer == null) return;
            NativeMemory.AlignedFree(Pointer);
            Interlocked.Add(ref liveBytes, -Size);
            Pointer = null;
            Size = 0;
            GC.SuppressFinalize(this);
        }
    }

    /// Where a loaded texture's texels come from until they are first needed.
    internal readonly record struct TexelSource(byte[] Inline, string StreamPath, long Offset, long Size)
    {
        public static TexelSource From(Fields texture)
        {
            var stream = texture.Struct("m_StreamData");
            var path = stream.String("path");
            // The offset is a UInt32 in older bundles (Unity 2019.4's, as Spooky Pumpkin SO's), a UInt64 in newer ones.
            if (path.Length > 0) return new TexelSource(null, path, Convert.ToInt64(stream["offset"]), stream.Get<uint>("size"));
            return new TexelSource(texture.Get<byte[]>("image data"), null, 0, 0);
        }

        public byte[] Read()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var bytes = StreamPath != null ? AssetSystem.ReadStream(StreamPath, Offset, Size) : Inline;
            System.Threading.Interlocked.Add(ref StartupStats.ReadTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
            return bytes;
        }

        /// The texels in new memory, read straight into it.
        public PixelMemory ReadMemory()
        {
            if (StreamPath == null) return new PixelMemory(Inline);
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var memory = new PixelMemory(Size);
            AssetSystem.ReadStreamInto(StreamPath, Offset, memory.Span);
            System.Threading.Interlocked.Add(ref StartupStats.ReadTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
            return memory;
        }
    }

    /// Texture2D, Texture2DArray and Texture3D: texels kept on the CPU side in one block of
    /// memory laid out like Unity's (see TextureFormats).
    internal abstract class TexelTextureNative : TextureNative
    {
        public TextureFormat Format = TextureFormat.RGBA32;
        public bool Readable = true;
        /// Depth of a 3D texture or slice count of an array (1 otherwise).
        public int Depth = 1;

        PixelMemory memory;
        TexelSource? source;

        public override bool IsReadable => Readable;

        /// The device's snapshot of the texels still being copied (TexelCopies), finished before
        /// anything reads, writes or replaces them.
        public System.Threading.Tasks.Task PendingCopy;

        void AwaitCopy()
        {
            var p = PendingCopy;
            if (p == null) return;
            p.Wait();
            PendingCopy = null;
        }

        /// Texels, loading them from the asset file on first use. Null after the device took
        /// a non-readable texture's data.
        public PixelMemory Memory
        {
            get
            {
                AwaitCopy();
                if (memory == null && source is { } s)
                {
                    memory = GraphicsDevice.Pipelined ? s.ReadMemory() : new PixelMemory(s.Read());
                    source = null;
                }
                return memory;
            }
        }

        public bool HasTexels => memory != null || source != null;

        /// Hands over the texels as they are: their memory, or where to read them from (one of
        /// the two, or neither when there are none); the texture keeps none (background uploads of
        /// non-readable textures, which keep no CPU copy once uploaded).
        public (PixelMemory Memory, TexelSource? Source) DetachTexels()
        {
            AwaitCopy();
            var taken = (memory, source);
            memory = null;
            source = null;
            return taken;
        }

        /// Texels still to be read from an asset file.
        public bool TexelsUnread => memory == null && source != null;

        /// Texels from an asset file. As in Unity, a loaded texture is uploaded to the graphics
        /// device as part of loading (on the main thread before the next frame renders), not on
        /// its first draw.
        protected void SetSource(TexelSource s)
        {
            AwaitCopy();
            memory?.Free();
            memory = null;
            source = s;
            GraphicsDevice.QueueUpload(this);
        }

        /// Replaces the texels with freshly allocated memory of `size` bytes, filled with `fill`.
        public void Allocate(long size, byte fill)
        {
            AwaitCopy();
            memory?.Free();
            source = null;
            memory = new PixelMemory(size, fill);
        }

        /// Replaces the texels with `texels`, which the texture now owns (filled by the caller,
        /// SliceSize bytes: LoadImage's decode).
        public void Adopt(PixelMemory texels)
        {
            AwaitCopy();
            memory?.Free();
            source = null;
            memory = texels;
        }

        /// Called by the device after uploading a texture whose CPU copy is not kept
        /// (non-readable textures, as in Unity).
        public void DropTexels()
        {
            AwaitCopy();
            memory?.Free();
            memory = null;
            source = null;
        }

        /// The texels, now owned by the caller (the device uploading a non-readable texture);
        /// the texture keeps none, as after DropTexels.
        public PixelMemory TakeTexels()
        {
            AwaitCopy();
            var taken = Memory;
            memory = null;
            source = null;
            return taken;
        }

        public abstract long SliceSize { get; }

        public override void ReleaseResources()
        {
            base.ReleaseResources();
            DropTexels();
        }
    }

    internal sealed class Texture2DNative : TexelTextureNative
    {
        public override long SliceSize => TextureFormats.ImageSize(Format, Width, Height, MipCount);

        public long MipOffset(int mip) => TextureFormats.MipOffset(Format, Width, Height, mip);
        public long MipSize(int mip) => TextureFormats.MipSize(Format, Math.Max(1, Width >> mip), Math.Max(1, Height >> mip));

        /// Texture2D (class 28).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            Width = f.Int("m_Width");
            Height = f.Int("m_Height");
            Format = (TextureFormat)f.Int("m_TextureFormat");
            MipCount = f.Int("m_MipCount");
            Readable = f.Bool("m_IsReadable");
            Srgb = f.Int("m_ColorSpace") == 1;
            Dimension = (TextureKind)f.Int("m_TextureDimension");
            ReadSettings(f.Struct("m_TextureSettings"));
            // No image at all: a placeholder saved without texels (a dynamic font's atlas, 0×0,
            // filled at run time), which loads empty.
            int images = f.Int("m_ImageCount");
            if (images != 1 && !(images == 0 && f.Int("m_CompleteImageSize") == 0))
                throw new System.IO.InvalidDataException($"Texture2D {Name}: {images} images.");
            // Older bundles (Unity 2019.4's) have no m_MipsStripped: none stripped.
            if (f.Contains("m_MipsStripped") && f.Int("m_MipsStripped") != 0) throw new System.IO.InvalidDataException($"Texture2D {Name}: stripped mips are not supported.");
            SetSource(TexelSource.From(f));
        }
    }

    /// Texture2DArray: slices stored one after another, each with its full mip chain.
    internal sealed class Texture2DArrayNative : TexelTextureNative
    {
        public override long SliceSize => TextureFormats.ImageSize(Format, Width, Height, MipCount);

        /// Texture2DArray (class 187).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            Dimension = TextureKind.Tex2DArray;
            Width = f.Int("m_Width");
            Height = f.Int("m_Height");
            Depth = f.Int("m_Depth");
            var graphicsFormat = (GraphicsFormat)f.Int("m_Format");
            Format = TextureFormats.ToTextureFormat(graphicsFormat);
            if (Format == 0) throw new System.IO.InvalidDataException($"Texture2DArray {Name}: unsupported format {graphicsFormat}.");
            Srgb = TextureFormats.IsSrgb(graphicsFormat);
            MipCount = f.Int("m_MipCount");
            Readable = f.Bool("m_IsReadable");
            ReadSettings(f.Struct("m_TextureSettings"));
            // Older bundles (Unity 2019.4's) have no m_MipsStripped: none stripped.
            if (f.Contains("m_MipsStripped") && f.Int("m_MipsStripped") != 0) throw new System.IO.InvalidDataException($"Texture2DArray {Name}: stripped mips are not supported.");
            SetSource(TexelSource.From(f));
        }
    }

    /// Texture3D: mip levels stored largest first, each holding all depth slices.
    internal sealed class Texture3DNative : TexelTextureNative
    {
        public override long SliceSize => MipOffset(MipCount);

        public long MipOffset(int mip)
        {
            long offset = 0;
            for (int m = 0; m < mip; m++) offset += MipSize(m);
            return offset;
        }

        public long MipSize(int mip)
            => TextureFormats.MipSize(Format, Math.Max(1, Width >> mip), Math.Max(1, Height >> mip)) * Math.Max(1, Depth >> mip);
    }

    /// RenderTexture: a device render target described on the CPU side.
    internal sealed class RenderTextureNative : TextureNative
    {
        public int VolumeDepth = 1;
        public RenderTextureFormat Format = RenderTextureFormat.ARGB32;
        public GraphicsFormat ColorFormat = GraphicsFormat.R8G8B8A8_UNorm;
        /// Depth buffer bits as given to the constructor (0, 16, 24 or 32).
        public int DepthBits;
        public GraphicsFormat DepthStencilFormat;
        public int AntiAliasing = 1;
        public bool UseMipMap;
        public bool AutoGenerateMips = true;
        public bool EnableRandomWrite;
        public bool Created;

        /// Temporary render texture pool bookkeeping (RenderTexture.GetTemporary).
        public bool IsTemporary;
        public bool TemporaryInUse;
        public int TemporaryLastFrame;

        public override bool IsReadable => false;

        /// RenderTexture (class 84).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            Width = f.Int("m_Width");
            Height = f.Int("m_Height");
            AntiAliasing = Math.Max(1, f.Int("m_AntiAliasing"));
            MipCount = Math.Max(1, f.Int("m_MipCount"));
            DepthStencilFormat = (GraphicsFormat)f.Int("m_DepthStencilFormat");
            DepthBits = RenderTextureFormats.DepthBits(DepthStencilFormat);
            ColorFormat = (GraphicsFormat)f.Int("m_ColorFormat");
            Format = RenderTextureFormats.FromGraphicsFormat(ColorFormat);
            UseMipMap = f.Bool("m_MipMap");
            AutoGenerateMips = f.Bool("m_GenerateMips");
            Srgb = f.Bool("m_SRGB");
            EnableRandomWrite = f.Bool("m_EnableRandomWrite");
            ReadSettings(f.Struct("m_TextureSettings"));
            Dimension = (TextureKind)f.Int("m_Dimension");
            VolumeDepth = f.Int("m_VolumeDepth");
        }

        public override void ReleaseResources()
        {
            base.ReleaseResources();
            Created = false;
        }
    }

    internal static class RenderTextureFormats
    {
        /// RenderTextureFormat.Default/DefaultHDR resolve to the platform defaults (desktop GL:
        /// ARGB32 and ARGBHalf).
        public static RenderTextureFormat Resolve(RenderTextureFormat format) => format switch
        {
            RenderTextureFormat.Default => RenderTextureFormat.ARGB32,
            RenderTextureFormat.DefaultHDR => RenderTextureFormat.ARGBHalf,
            _ => format,
        };

        public static GraphicsFormat ToGraphicsFormat(RenderTextureFormat format, bool srgb) => Resolve(format) switch
        {
            RenderTextureFormat.ARGB32 => srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm,
            RenderTextureFormat.BGRA32 => srgb ? GraphicsFormat.B8G8R8A8_SRGB : GraphicsFormat.B8G8R8A8_UNorm,
            RenderTextureFormat.ARGBHalf => GraphicsFormat.R16G16B16A16_SFloat,
            RenderTextureFormat.ARGBFloat => GraphicsFormat.R32G32B32A32_SFloat,
            RenderTextureFormat.RGFloat => GraphicsFormat.R32G32_SFloat,
            RenderTextureFormat.RGHalf => GraphicsFormat.R16G16_SFloat,
            RenderTextureFormat.RFloat => GraphicsFormat.R32_SFloat,
            RenderTextureFormat.RHalf => GraphicsFormat.R16_SFloat,
            RenderTextureFormat.R8 => GraphicsFormat.R8_UNorm,
            RenderTextureFormat.RG16 => GraphicsFormat.R8G8_UNorm,
            RenderTextureFormat.R16 => GraphicsFormat.R16_UNorm,
            RenderTextureFormat.RG32 => GraphicsFormat.R16G16_UNorm,
            RenderTextureFormat.ARGB64 => GraphicsFormat.R16G16B16A16_UNorm,
            RenderTextureFormat.RGB565 => GraphicsFormat.B5G6R5_UNormPack16,
            RenderTextureFormat.ARGB4444 => GraphicsFormat.B4G4R4A4_UNormPack16,
            RenderTextureFormat.ARGB1555 => GraphicsFormat.B5G5R5A1_UNormPack16,
            RenderTextureFormat.ARGB2101010 => GraphicsFormat.A2B10G10R10_UNormPack32,
            RenderTextureFormat.RGB111110Float => GraphicsFormat.B10G11R11_UFloatPack32,
            RenderTextureFormat.ARGBInt => GraphicsFormat.R32G32B32A32_SInt,
            RenderTextureFormat.RGInt => GraphicsFormat.R32G32_SInt,
            RenderTextureFormat.RInt => GraphicsFormat.R32_SInt,
            RenderTextureFormat.RGBAUShort => GraphicsFormat.R16G16B16A16_UInt,
            // Depth-only render textures have no colour buffer.
            RenderTextureFormat.Depth or RenderTextureFormat.Shadowmap => GraphicsFormat.None,
            var f => throw new ArgumentException($"RenderTextureFormat {f} is not supported."),
        };

        public static RenderTextureFormat FromGraphicsFormat(GraphicsFormat format) => format switch
        {
            GraphicsFormat.R8G8B8A8_SRGB or GraphicsFormat.R8G8B8A8_UNorm => RenderTextureFormat.ARGB32,
            GraphicsFormat.B8G8R8A8_SRGB or GraphicsFormat.B8G8R8A8_UNorm => RenderTextureFormat.BGRA32,
            GraphicsFormat.R16G16B16A16_SFloat => RenderTextureFormat.ARGBHalf,
            GraphicsFormat.R32G32B32A32_SFloat => RenderTextureFormat.ARGBFloat,
            GraphicsFormat.R32G32_SFloat => RenderTextureFormat.RGFloat,
            GraphicsFormat.R16G16_SFloat => RenderTextureFormat.RGHalf,
            GraphicsFormat.R32_SFloat => RenderTextureFormat.RFloat,
            GraphicsFormat.R16_SFloat => RenderTextureFormat.RHalf,
            GraphicsFormat.R8_UNorm => RenderTextureFormat.R8,
            GraphicsFormat.R8G8_UNorm => RenderTextureFormat.RG16,
            GraphicsFormat.R16_UNorm => RenderTextureFormat.R16,
            GraphicsFormat.R16G16_UNorm => RenderTextureFormat.RG32,
            GraphicsFormat.R16G16B16A16_UNorm => RenderTextureFormat.ARGB64,
            GraphicsFormat.B5G6R5_UNormPack16 => RenderTextureFormat.RGB565,
            GraphicsFormat.B4G4R4A4_UNormPack16 => RenderTextureFormat.ARGB4444,
            GraphicsFormat.B5G5R5A1_UNormPack16 => RenderTextureFormat.ARGB1555,
            GraphicsFormat.A2B10G10R10_UNormPack32 => RenderTextureFormat.ARGB2101010,
            GraphicsFormat.B10G11R11_UFloatPack32 => RenderTextureFormat.RGB111110Float,
            GraphicsFormat.None => RenderTextureFormat.Depth,
            var f => throw new ArgumentException($"GraphicsFormat {f} has no RenderTextureFormat."),
        };

        /// The depth/stencil format Unity picks for a depth-buffer bit count.
        public static GraphicsFormat DepthFormat(int bits) => bits switch
        {
            0 => GraphicsFormat.None,
            16 => GraphicsFormat.D16_UNorm,
            24 => GraphicsFormat.D24_UNorm_S8_UInt,
            _ => GraphicsFormat.D32_SFloat_S8_UInt,
        };

        public static int DepthBits(GraphicsFormat depthStencil) => depthStencil switch
        {
            GraphicsFormat.None => 0,
            GraphicsFormat.D16_UNorm or GraphicsFormat.D16_UNorm_S8_UInt => 16,
            GraphicsFormat.D32_SFloat or GraphicsFormat.D32_SFloat_S8_UInt => 32,
            _ => 24,
        };
    }
}
