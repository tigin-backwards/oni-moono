using System;
using System.Runtime.CompilerServices;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using Unity.Collections;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityEngine
{
    public partial class Texture
    {
        public static readonly int GenerateAllMips = -1;

        /// Whether Unity may create textures' GPU resources off its render thread (false by
        /// default, measured in the original player). It changes when Unity's native code does
        /// that work, not what scripts observe; the engine uploads textures created on any thread
        /// before the next frame renders either way, so the value is only kept.
        public static bool allowThreadedTextureCreation { get; set; }

        protected Texture() { }

        internal TextureNative TextureData => (TextureNative)NativeOrThrow;

        // Unity's base Texture has no settable size; RenderTexture overrides these.
        public virtual int width
        {
            get => TextureData.Width;
            set => throw new NotImplementedException();
        }

        public virtual int height
        {
            get => TextureData.Height;
            set => throw new NotImplementedException();
        }

        public virtual bool isReadable => TextureData.IsReadable;

        public int mipmapCount => TextureData.MipCount;

        public virtual UnityEngine.Rendering.TextureDimension dimension => (UnityEngine.Rendering.TextureDimension)TextureData.Dimension;

        public uint updateCount => TextureData.UpdateCount;

        public void IncrementUpdateCount() => TextureData.UpdateCount++;

        /// Reading returns the U mode; assigning sets U, V and W.
        public TextureWrapMode wrapMode
        {
            get => TextureData.WrapU;
            set
            {
                var t = TextureData;
                t.WrapU = t.WrapV = t.WrapW = value;
                t.SamplerChanged();
            }
        }

        public TextureWrapMode wrapModeU
        {
            get => TextureData.WrapU;
            set { var t = TextureData; t.WrapU = value; t.SamplerChanged(); }
        }

        public TextureWrapMode wrapModeV
        {
            get => TextureData.WrapV;
            set { var t = TextureData; t.WrapV = value; t.SamplerChanged(); }
        }

        public TextureWrapMode wrapModeW
        {
            get => TextureData.WrapW;
            set { var t = TextureData; t.WrapW = value; t.SamplerChanged(); }
        }

        public FilterMode filterMode
        {
            get => TextureData.FilterMode;
            set { var t = TextureData; t.FilterMode = value; t.SamplerChanged(); }
        }

        public int anisoLevel
        {
            get => TextureData.Aniso;
            set { var t = TextureData; t.Aniso = value; t.SamplerChanged(); }
        }

        public float mipMapBias
        {
            get => TextureData.MipBias;
            set { var t = TextureData; t.MipBias = value; t.SamplerChanged(); }
        }

        public Vector2 texelSize
        {
            get
            {
                var t = TextureData;
                return new Vector2(1f / t.Width, 1f / t.Height);
            }
        }

        internal UnityException CreateNonReadableException(Texture t)
            => new($"Texture '{t.name}' is not readable, the texture memory can not be accessed from scripts. You can make the texture readable in the Texture Import Settings.");

        internal UnityException CreateNativeArrayLengthOverflowException()
            => new("Failed to create NativeArray, length exceeds the allowed maximum of Int32.MaxValue. Use a larger type as template argument to reduce the array length.");

        internal bool ValidateFormat(TextureFormat format)
        {
            if (SystemInfo.SupportsTextureFormat(format)) return true;
            // As Unity: a compressed format the GPU cannot sample is decompressed for it.
            if (TextureFormats.IsBlockCompressed(format) && TextureFormats.CanDecode(format))
            {
                Debug.LogWarning($"'{format}' is not supported on this platform. Decompressing texture. Use 'SystemInfo.SupportsTextureFormat' C# API to check format support.", this);
                return true;
            }
            Debug.LogError($"Texture creation failed. '{format}' is not supported on this platform. Use 'SystemInfo.SupportsTextureFormat' C# API to check format support.", this);
            return false;
        }

        internal bool ValidateFormat(RenderTextureFormat format)
        {
            if (SystemInfo.SupportsRenderTextureFormat(format)) return true;
            Debug.LogError($"RenderTexture creation failed. '{format}' is not supported on this platform. Use 'SystemInfo.SupportsRenderTextureFormat' C# API to check format support.", this);
            return false;
        }

        internal override void EngineOnDestroy() => TextureData.ReleaseResources();
    }

    public partial class Texture2D
    {
        /// EncodeToEXR's options (the engine writes no EXR; mods name the type).
        [Flags]
        public enum EXRFlags
        {
            None = 0,
            OutputAsFloat = 1,
            CompressZIP = 2,
            CompressRLE = 4,
            CompressPIZ = 8,
        }

        public Texture2D(int width, int height)
            : this(width, height, TextureFormat.RGBA32, GenerateAllMips, linear: false, createUninitialized: false) { }

        public Texture2D(int width, int height, TextureFormat textureFormat, bool mipChain)
            : this(width, height, textureFormat, mipChain ? GenerateAllMips : 1, linear: false, createUninitialized: false) { }

        public Texture2D(int width, int height, TextureFormat textureFormat, bool mipChain, bool linear)
            : this(width, height, textureFormat, mipChain ? GenerateAllMips : 1, linear, createUninitialized: false) { }

        public Texture2D(int width, int height, TextureFormat textureFormat, bool mipChain, bool linear, bool createUninitialized)
            : this(width, height, textureFormat, mipChain ? GenerateAllMips : 1, linear, createUninitialized) { }

        public Texture2D(int width, int height, TextureFormat textureFormat, int mipCount, bool linear)
            : this(width, height, textureFormat, mipCount, linear, createUninitialized: false) { }

        public Texture2D(int width, int height, TextureFormat textureFormat, int mipCount, bool linear, bool createUninitialized)
        {
            if (!ValidateFormat(textureFormat)) return;
            Create(width, height, textureFormat, !linear, mipCount, initialize: !createUninitialized);
        }

        public Texture2D(int width, int height, GraphicsFormat format, TextureCreationFlags flags)
            : this(width, height, format, (flags & TextureCreationFlags.MipChain) != 0 ? GenerateAllMips : 1, flags) { }

        public Texture2D(int width, int height, GraphicsFormat format, int mipCount, TextureCreationFlags flags)
        {
            var textureFormat = TextureFormats.ToTextureFormat(format);
            if (textureFormat == 0)
            {
                Debug.LogError($"Texture creation failed. '{format}' is not supported for Sample usage on this platform. Use 'SystemInfo.IsFormatSupported' C# API to check format support.", this);
                return;
            }
            Create(width, height, textureFormat, TextureFormats.IsSrgb(format), mipCount, initialize: (flags & TextureCreationFlags.DontInitializePixels) == 0);
        }

        /// Unity creates the texture with uninitialized-memory fill 0xCD (205) unless asked not to
        /// initialize; a 0×0 request creates an empty texture.
        void Create(int width, int height, TextureFormat format, bool srgb, int mipCount, bool initialize)
        {
            if (width < 0 || height < 0) throw new UnityException("Failed to create texture because of invalid parameters.");
            var t = new Texture2DNative
            {
                Width = width,
                Height = height,
                Format = format,
                Srgb = srgb,
                MipCount = width == 0 && height == 0 ? 1 : TextureFormats.ResolveMipCount(mipCount, width, height),
                Readable = true,
            };
            t.Allocate(width == 0 && height == 0 ? 0 : t.SliceSize, initialize ? (byte)0xCD : (byte)0);
            Bind(t);
            if (Threads.IsMainThread) GraphicsDevice.Current.PrepareAhead(t);
        }

        Texture2DNative Data => (Texture2DNative)NativeOrThrow;

        public TextureFormat format => Data.Format;

        public override bool isReadable => Data.Readable;

        static Texture2D s_White, s_Gray, s_Black;

        /// 4×4 opaque white, shared engine texture.
        public static Texture2D whiteTexture => s_White ??= Builtin("UnityWhite", new Color32(255, 255, 255, 255));

        /// 4×4 transparent black (alpha 0), shared engine texture, readable (measured).
        public static Texture2D blackTexture => s_Black ??= Builtin("UnityBlack", new Color32(0, 0, 0, 0), readable: true);

        /// 4×4 half-grey with half alpha, shared engine texture.
        public static Texture2D grayTexture => s_Gray ??= Builtin("UnityGrey", new Color32(128, 128, 128, 128));

        static Texture2D Builtin(string name, Color32 color, bool readable = false)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = name, hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[16];
            Array.Fill(pixels, color);
            t.SetPixels32(pixels);
            t.Apply(false, !readable);
            return t;
        }

        // ----- Pixel access -----

        Texture2DNative Readable()
        {
            var t = Data;
            if (!t.Readable) throw CreateNonReadableException(this);
            return t;
        }

        static int MipDim(int size, int mip) => Math.Max(1, size >> mip);

        static void CheckMip(Texture2DNative t, int mip)
        {
            if (mip < 0 || mip >= t.MipCount)
                throw new ArgumentException("The passed in miplevel " + mip + " is invalid. It needs to be in the range 0 and " + (t.MipCount - 1));
        }

        Span<byte> MipSpan(Texture2DNative t, int mip)
        {
            var memory = t.Memory ?? throw new UnityException("Texture '" + name + "' has no data.");
            return memory.Slice(t.MipOffset(mip), t.MipSize(mip));
        }

        public Color GetPixel(int x, int y) => GetPixel(x, y, 0);

        /// Out-of-range coordinates wrap or clamp by the texture's wrap modes.
        public Color GetPixel(int x, int y, int mipLevel)
        {
            var t = Readable();
            CheckMip(t, mipLevel);
            if (!TextureFormats.CanDecode(t.Format)) throw TextureFormats.UnsupportedFormat(t.Format);
            int w = MipDim(t.Width, mipLevel), h = MipDim(t.Height, mipLevel);
            return TextureFormats.Decode(t.Format, MipSpan(t, mipLevel), w, TexelAddressing.Wrap(x, w, t.WrapU), TexelAddressing.Wrap(y, h, t.WrapV));
        }

        public Color GetPixelBilinear(float u, float v) => GetPixelBilinear(u, v, 0);

        /// Bilinear sample at normalized coordinates, texel centres at (i + 0.5) / size.
        public Color GetPixelBilinear(float u, float v, int mipLevel)
        {
            var t = Readable();
            CheckMip(t, mipLevel);
            if (!TextureFormats.CanDecode(t.Format)) throw TextureFormats.UnsupportedFormat(t.Format);
            int w = MipDim(t.Width, mipLevel), h = MipDim(t.Height, mipLevel);
            var mip = MipSpan(t, mipLevel);
            float fx = u * w - 0.5f, fy = v * h - 0.5f;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            var bottom = Color.LerpUnclamped(Texel(t, mip, w, h, x0, y0), Texel(t, mip, w, h, x0 + 1, y0), tx);
            var top = Color.LerpUnclamped(Texel(t, mip, w, h, x0, y0 + 1), Texel(t, mip, w, h, x0 + 1, y0 + 1), tx);
            return Color.LerpUnclamped(bottom, top, ty);
        }

        static Color Texel(Texture2DNative t, ReadOnlySpan<byte> mip, int w, int h, int x, int y)
            => TextureFormats.Decode(t.Format, mip, w, TexelAddressing.Wrap(x, w, t.WrapU), TexelAddressing.Wrap(y, h, t.WrapV));

        public Color[] GetPixels() => GetPixels(0);

        public Color[] GetPixels(int miplevel)
        {
            var t = Data;
            return GetPixels(0, 0, MipDim(t.Width, miplevel), MipDim(t.Height, miplevel), miplevel);
        }

        public Color[] GetPixels(int x, int y, int blockWidth, int blockHeight) => GetPixels(x, y, blockWidth, blockHeight, 0);

        public Color[] GetPixels(int x, int y, int blockWidth, int blockHeight, int miplevel)
        {
            var t = Readable();
            CheckMip(t, miplevel);
            if (!TextureFormats.CanDecode(t.Format)) throw TextureFormats.UnsupportedFormat(t.Format);
            int w = MipDim(t.Width, miplevel), h = MipDim(t.Height, miplevel);
            CheckBlock(x, y, blockWidth, blockHeight, w, h);
            var mip = MipSpan(t, miplevel);
            var result = new Color[blockWidth * blockHeight];
            for (int j = 0; j < blockHeight; j++)
                for (int i = 0; i < blockWidth; i++)
                    result[j * blockWidth + i] = TextureFormats.Decode(t.Format, mip, w, x + i, y + j);
            return result;
        }

        public Color32[] GetPixels32() => GetPixels32(0);

        public Color32[] GetPixels32(int miplevel)
        {
            var colors = GetPixels(miplevel);
            var result = new Color32[colors.Length];
            for (int i = 0; i < colors.Length; i++) result[i] = colors[i];
            return result;
        }

        static void CheckBlock(int x, int y, int blockWidth, int blockHeight, int w, int h)
        {
            if (x < 0 || y < 0 || blockWidth < 0 || blockHeight < 0 || x + blockWidth > w || y + blockHeight > h)
                throw new ArgumentException($"Texture rectangle is out of bounds ({x} + {blockWidth} > {w} or {y} + {blockHeight} > {h})");
        }

        public void SetPixel(int x, int y, Color color) => SetPixel(x, y, color, 0);

        /// Out-of-range coordinates wrap or clamp by the texture's wrap modes.
        public void SetPixel(int x, int y, Color color, int mipLevel)
        {
            var t = Readable();
            CheckMip(t, mipLevel);
            if (!TextureFormats.CanEncode(t.Format)) throw TextureFormats.UnsupportedFormat(t.Format);
            int w = MipDim(t.Width, mipLevel), h = MipDim(t.Height, mipLevel);
            TextureFormats.Encode(t.Format, MipSpan(t, mipLevel), w, TexelAddressing.Wrap(x, w, t.WrapU), TexelAddressing.Wrap(y, h, t.WrapV), color);
        }

        public void SetPixels(Color[] colors) => SetPixels(0, 0, width, height, colors, 0);

        public void SetPixels(Color[] colors, int miplevel)
        {
            var t = Data;
            SetPixels(0, 0, MipDim(t.Width, miplevel), MipDim(t.Height, miplevel), colors, miplevel);
        }

        public void SetPixels(int x, int y, int blockWidth, int blockHeight, Color[] colors) => SetPixels(x, y, blockWidth, blockHeight, colors, 0);

        public void SetPixels(int x, int y, int blockWidth, int blockHeight, Color[] colors, int miplevel)
        {
            var t = Readable();
            WriteBlock(t, x, y, blockWidth, blockHeight, colors?.Length ?? 0, miplevel, (mip, w, i, px, py) => TextureFormats.Encode(t.Format, mip, w, px, py, colors[i]));
        }

        public void SetPixels32(Color32[] colors) => SetPixels32(colors, 0);

        public void SetPixels32(Color32[] colors, int miplevel)
        {
            var t = Readable();
            WriteBlock(t, 0, 0, MipDim(t.Width, miplevel), MipDim(t.Height, miplevel), colors?.Length ?? 0, miplevel, (mip, w, i, px, py) => TextureFormats.Encode(t.Format, mip, w, px, py, colors[i]));
        }

        public void SetPixels32(int x, int y, int blockWidth, int blockHeight, Color32[] colors) => SetPixels32(x, y, blockWidth, blockHeight, colors, 0);

        public void SetPixels32(int x, int y, int blockWidth, int blockHeight, Color32[] colors, int miplevel)
        {
            var t = Readable();
            WriteBlock(t, x, y, blockWidth, blockHeight, colors?.Length ?? 0, miplevel, (mip, w, i, px, py) => TextureFormats.Encode(t.Format, mip, w, px, py, colors[i]));
        }

        delegate void TexelWriter(Span<byte> mip, int mipWidth, int index, int x, int y);

        void WriteBlock(Texture2DNative t, int x, int y, int blockWidth, int blockHeight, int count, int miplevel, TexelWriter write)
        {
            CheckMip(t, miplevel);
            if (!TextureFormats.CanEncode(t.Format)) throw TextureFormats.UnsupportedFormat(t.Format);
            int w = MipDim(t.Width, miplevel), h = MipDim(t.Height, miplevel);
            CheckBlock(x, y, blockWidth, blockHeight, w, h);
            if (count < blockWidth * blockHeight) throw new ArgumentException("Array size must be at least width*height");
            var mip = MipSpan(t, miplevel);
            for (int j = 0; j < blockHeight; j++)
                for (int i = 0; i < blockWidth; i++)
                    write(mip, w, j * blockWidth + i, x + i, y + j);
        }

        // ----- Raw data -----

        public byte[] GetRawTextureData()
        {
            var t = Readable();
            return t.Memory?.Span.ToArray() ?? Array.Empty<byte>();
        }

        public unsafe NativeArray<T> GetRawTextureData<T>() where T : struct
        {
            var t = Readable();
            var memory = t.Memory;
            t.MarkAliased();
            ulong length = (ulong)(memory?.Size ?? 0) / (ulong)Unsafe.SizeOf<T>();
            if (length > int.MaxValue) throw CreateNativeArrayLengthOverflowException();
            return new NativeArray<T>(memory?.Pointer, (int)length, owned: false);
        }

        public unsafe NativeArray<T> GetPixelData<T>(int mipLevel) where T : struct
        {
            var t = Readable();
            if (mipLevel < 0 || mipLevel >= t.MipCount)
                throw new ArgumentException("The passed in miplevel " + mipLevel + " is invalid. It needs to be in the range 0 and " + (t.MipCount - 1));
            var memory = t.Memory;
            if (memory == null || memory.Pointer == null) throw new UnityException("Texture '" + name + "' has no data.");
            t.MarkAliased();
            ulong length = (ulong)t.MipSize(mipLevel) / (ulong)Unsafe.SizeOf<T>();
            if (length > int.MaxValue) throw CreateNativeArrayLengthOverflowException();
            return new NativeArray<T>(memory.Pointer + t.MipOffset(mipLevel), (int)length, owned: false);
        }

        public void LoadRawTextureData(byte[] data)
        {
            var t = Readable();
            if (data == null || data.Length == 0)
            {
                Debug.LogError("No texture data provided to LoadRawTextureData", this);
                return;
            }
            if (!LoadRaw(t, data)) throw new UnityException("LoadRawTextureData: not enough data provided (will result in overread).");
        }

        public unsafe void LoadRawTextureData(IntPtr data, int size)
        {
            var t = Readable();
            if (data == IntPtr.Zero || size == 0)
            {
                Debug.LogError("No texture data provided to LoadRawTextureData", this);
                return;
            }
            if (!LoadRaw(t, new ReadOnlySpan<byte>((void*)data, size))) throw new UnityException("LoadRawTextureData: not enough data provided (will result in overread).");
        }

        public unsafe void LoadRawTextureData<T>(NativeArray<T> data) where T : struct
        {
            var t = Readable();
            if (!data.IsCreated || data.Length == 0) throw new UnityException("No texture data provided to LoadRawTextureData");
            long bytes = (long)data.Length * Unsafe.SizeOf<T>();
            if (!LoadRaw(t, new ReadOnlySpan<byte>(data.m_Buffer, checked((int)bytes)))) throw new UnityException("LoadRawTextureData: not enough data provided (will result in overread).");
        }

        /// Copies the whole texture's data; extra input bytes are ignored. The texels count as
        /// changed (TexelsChanged) only when some byte differed: the game hands its property
        /// textures over every frame, often unchanged (RawTextureLoads).
        static bool LoadRaw(Texture2DNative t, ReadOnlySpan<byte> data)
        {
            long size = t.SliceSize;
            if (data.Length < size) return false;
            long started = RawTextureLoads.Start();
            if (t.Texels == null) t.Allocate(size, 0);
            var bytes = data[..checked((int)size)];
            bool changed = RawTextureLoads.CopyChanged(bytes, t.Texels.Span);
            if (changed) t.MarkTexelsChanged();
            RawTextureLoads.Loaded(started, changed);
            if (RawTextureLoads.Verify) RawTextureLoads.VerifyLoad(t, bytes, t.Texels.Span);
            RawTextureLoads.Register(t);
            return true;
        }

        // ----- Apply / Reinitialize / ReadPixels -----

        public void Apply() => Apply(true, false);

        public void Apply(bool updateMipmaps) => Apply(updateMipmaps, false);

        /// Marks the CPU data for upload; optionally rebuilds mip levels 1… from level 0 (box
        /// filter, uncompressed formats) and drops the CPU copy after upload. Texels unchanged
        /// since the last upload was marked (TexelsChanged) need none: the device already has
        /// them. updateCount counts every Apply.
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable)
        {
            var t = Readable();
            if (updateMipmaps) TexelAddressing.BuildMips(t);
            if (makeNoLongerReadable) t.Readable = false;
            if (!t.TexelsChanged && !makeNoLongerReadable)
            {
                if (RawTextureLoads.Verify) RawTextureLoads.VerifySkip(t);
                t.UpdateCount++;
                return;
            }
            t.ContentChanged();
            if (RawTextureLoads.Verify) RawTextureLoads.VerifyUpload(t);
            if (Threads.IsMainThread) GraphicsDevice.Current.Applied(t);
        }

        public bool Reinitialize(int width, int height)
        {
            var t = Readable();
            return Reinit(t, width, height, t.Format, t.MipCount > 1);
        }

        public bool Reinitialize(int width, int height, TextureFormat format, bool hasMipMap) => Reinit(Data, width, height, format, hasMipMap);

        public bool Reinitialize(int width, int height, GraphicsFormat format, bool hasMipMap)
        {
            var t = Readable();
            var textureFormat = TextureFormats.ToTextureFormat(format);
            if (textureFormat == 0) return false;
            t.Srgb = TextureFormats.IsSrgb(format);
            return Reinit(t, width, height, textureFormat, hasMipMap);
        }

        /// New size/format with uninitialized contents; the texture becomes readable again.
        static bool Reinit(Texture2DNative t, int width, int height, TextureFormat format, bool hasMipMap)
        {
            if (width < 0 || height < 0 || !TextureFormats.TryGetBlock(format, out _, out _, out _)) return false;
            t.Width = width;
            t.Height = height;
            t.Format = format;
            t.MipCount = hasMipMap ? TextureFormats.FullMipCount(width, height) : 1;
            t.Readable = true;
            t.Allocate(t.SliceSize, 0xCD);
            t.ContentChanged();
            return true;
        }

        public void ReadPixels(Rect source, int destX, int destY) => ReadPixels(source, destX, destY, true);

        /// Reads from RenderTexture.active (the back buffer when null) into level 0.
        public void ReadPixels(Rect source, int destX, int destY, bool recalculateMipMaps)
        {
            var t = Readable();
            Threads.AssertMainThread("ReadPixels");
            var rect = new RectInt((int)source.x, (int)source.y, (int)source.width, (int)source.height);
            var active = RenderTexture.active;
            GraphicsDevice.Current.ReadPixels(active == null ? null : (RenderTextureNative)active.NativeOrThrow, rect, t, destX, destY);
            if (recalculateMipMaps) TexelAddressing.BuildMips(t);
        }
    }
}

namespace OniMoono.Engine.Rendering
{
    using UnityEngine;

    internal static class TexelAddressing
    {
        /// A texel coordinate addressed by a wrap mode.
        public static int Wrap(int i, int size, TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Repeat:
                    i %= size;
                    return i < 0 ? i + size : i;
                case TextureWrapMode.Mirror:
                {
                    int period = 2 * size;
                    i %= period;
                    if (i < 0) i += period;
                    return i < size ? i : period - 1 - i;
                }
                case TextureWrapMode.MirrorOnce:
                    if (i < 0) i = -1 - i;
                    return Math.Min(i, size - 1);
                default:
                    return Math.Clamp(i, 0, size - 1);
            }
        }

        /// Rebuilds levels 1… of an uncompressed texture by averaging 2×2 texels of the level
        /// above (edge texels repeat for odd sizes).
        public static void BuildMips(Texture2DNative t)
        {
            if (t.MipCount <= 1 || !TextureFormats.CanEncode(t.Format) || t.Memory == null) return;
            for (int m = 1; m < t.MipCount; m++)
            {
                int sw = Math.Max(1, t.Width >> (m - 1)), sh = Math.Max(1, t.Height >> (m - 1));
                int dw = Math.Max(1, t.Width >> m), dh = Math.Max(1, t.Height >> m);
                var src = t.Memory.Slice(t.MipOffset(m - 1), t.MipSize(m - 1));
                var dst = t.Memory.Slice(t.MipOffset(m), t.MipSize(m));
                for (int y = 0; y < dh; y++)
                {
                    int y0 = Math.Min(2 * y, sh - 1), y1 = Math.Min(2 * y + 1, sh - 1);
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = Math.Min(2 * x, sw - 1), x1 = Math.Min(2 * x + 1, sw - 1);
                        var c = (TextureFormats.Decode(t.Format, src, sw, x0, y0) + TextureFormats.Decode(t.Format, src, sw, x1, y0)
                               + TextureFormats.Decode(t.Format, src, sw, x0, y1) + TextureFormats.Decode(t.Format, src, sw, x1, y1)) * 0.25f;
                        TextureFormats.Encode(t.Format, dst, dw, x, y, c);
                    }
                }
            }
        }
    }
}
