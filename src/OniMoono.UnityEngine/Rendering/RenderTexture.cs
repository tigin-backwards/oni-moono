using System;
using System.Collections.Generic;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityEngine
{
    public partial class Texture3D
    {
        public Texture3D(int width, int height, int depth, TextureFormat format, bool mipChain)
        {
            if (!ValidateFormat(format)) return;
            if (width <= 0 || height <= 0 || depth <= 0) throw new UnityException("Failed to create texture because of invalid parameters.");
            var t = new Texture3DNative
            {
                Width = width,
                Height = height,
                Depth = depth,
                Format = format,
                Dimension = TextureKind.Tex3D,
                MipCount = mipChain ? TextureFormats.FullMipCount(width, height, depth) : 1,
                Readable = true,
            };
            t.Allocate(t.SliceSize, 0xCD);
            Bind(t);
        }

        Texture3DNative Data => (Texture3DNative)NativeOrThrow;

        public int depth => Data.Depth;

        public TextureFormat format => Data.Format;

        public override bool isReadable => Data.Readable;

        Texture3DNative Writable()
        {
            var t = Data;
            if (!t.Readable) throw CreateNonReadableException(this);
            if (!TextureFormats.CanEncode(t.Format)) throw TextureFormats.UnsupportedFormat(t.Format);
            return t;
        }

        /// Level `miplevel` in x-fastest, then y, then z order.
        public void SetPixels(Color[] colors) => SetPixels(colors, 0);

        public void SetPixels(Color[] colors, int miplevel)
        {
            var t = Writable();
            Write(t, miplevel, colors?.Length ?? 0, (span, w, i, x, y) => TextureFormats.Encode(t.Format, span, w, x, y, colors[i]));
        }

        public void SetPixels32(Color32[] colors) => SetPixels32(colors, 0);

        public void SetPixels32(Color32[] colors, int miplevel)
        {
            var t = Writable();
            Write(t, miplevel, colors?.Length ?? 0, (span, w, i, x, y) => TextureFormats.Encode(t.Format, span, w, x, y, colors[i]));
        }

        delegate void TexelWriter(Span<byte> slab, int width, int index, int x, int y);

        static void Write(Texture3DNative t, int mip, int count, TexelWriter write)
        {
            if (mip < 0 || mip >= t.MipCount) throw new ArgumentException("Invalid mip level " + mip);
            int w = Math.Max(1, t.Width >> mip), h = Math.Max(1, t.Height >> mip), d = Math.Max(1, t.Depth >> mip);
            if (count < w * h * d) throw new ArgumentException("Array size must be at least width*height*depth");
            var level = t.Memory.Slice(t.MipOffset(mip), t.MipSize(mip));
            // A 3D level is d slices of w×h texels: address it as one w×(h·d) image.
            for (int z = 0; z < d; z++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        write(level, w, (z * h + y) * w + x, x, z * h + y);
        }

        public void Apply() => Apply(true, false);

        public void Apply(bool updateMipmaps) => Apply(updateMipmaps, false);

        /// Marks the data for upload; mip levels of a 3D texture are rebuilt by averaging 2×2×2
        /// texels of the level above.
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable)
        {
            var t = Data;
            if (!t.Readable) throw CreateNonReadableException(this);
            if (updateMipmaps && TextureFormats.CanEncode(t.Format))
            {
                for (int m = 1; m < t.MipCount; m++)
                {
                    int sw = Math.Max(1, t.Width >> (m - 1)), sh = Math.Max(1, t.Height >> (m - 1)), sd = Math.Max(1, t.Depth >> (m - 1));
                    int dw = Math.Max(1, t.Width >> m), dh = Math.Max(1, t.Height >> m), dd = Math.Max(1, t.Depth >> m);
                    var src = t.Memory.Slice(t.MipOffset(m - 1), t.MipSize(m - 1));
                    var dst = t.Memory.Slice(t.MipOffset(m), t.MipSize(m));
                    for (int z = 0; z < dd; z++)
                        for (int y = 0; y < dh; y++)
                            for (int x = 0; x < dw; x++)
                            {
                                var sum = new Color(0, 0, 0, 0);
                                for (int k = 0; k < 8; k++)
                                {
                                    int sx = Math.Min(2 * x + (k & 1), sw - 1), sy = Math.Min(2 * y + ((k >> 1) & 1), sh - 1), sz = Math.Min(2 * z + (k >> 2), sd - 1);
                                    sum += TextureFormats.Decode(t.Format, src, sw, sx, sz * sh + sy);
                                }
                                TextureFormats.Encode(t.Format, dst, dw, x, z * dh + y, sum * 0.125f);
                            }
                }
            }
            if (makeNoLongerReadable) t.Readable = false;
            t.ContentChanged();
        }
    }

    public partial class Texture2DArray
    {
        Texture2DArrayNative Data => (Texture2DArrayNative)NativeOrThrow;

        public int depth => Data.Depth;

        public TextureFormat format => Data.Format;

        public override bool isReadable => Data.Readable;
    }

    public partial class RenderTexture
    {
        protected internal RenderTexture() { }

        public RenderTexture(int width, int height, int depth)
            : this(width, height, depth, RenderTextureFormat.Default) { }

        public RenderTexture(int width, int height, int depth, RenderTextureFormat format)
        {
            format = RenderTextureFormats.Resolve(format);
            if (!ValidateFormat(format)) return;
            Bind(Describe(width, height, depth, format));
        }

        // The constructors below were measured in the original (UnityProbe's apigaps "rtctor",
        // GLCore, gamma colour space): none checks its arguments; Create does (sizes, mip count,
        // formats), and an unsupported colour format leaves the object destroyed.

        /// readWrite asks for sRGB only in a linear-colour-space project (in the game's gamma
        /// space sRGB is false whatever it asks for); a BGRA32 format falls back to ARGB32 with
        /// a warning; mipCount above 0 is the descriptor's mipCount, else -1.
        public RenderTexture(int width, int height, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite)
            => Construct(width, height, depth, format, readWrite, -1);

        public RenderTexture(int width, int height, int depth, RenderTextureFormat format, int mipCount)
            => Construct(width, height, depth, format, RenderTextureReadWrite.Default, mipCount);

        void Construct(int width, int height, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite, int mipCount)
        {
            format = RenderTextureFormats.Resolve(format);
            if (format == RenderTextureFormat.BGRA32)
            {
                Debug.LogWarning("'B8G8R8A8_UNorm' is not supported. RenderTexture::GetTemporary fallbacks to R8G8B8A8_UNorm format on this platform. Use 'SystemInfo.IsFormatSupported' C# API to check format support.");
                format = RenderTextureFormat.ARGB32;
            }
            if (!ValidateFormat(format)) return;
            if (format == RenderTextureFormat.Depth && depth <= 0)
                Debug.LogWarning("Depth RenderTexture requested without a depth buffer. Changing to a 16 bit depth buffer. To resolve this warning, please specify the desired number of depth bits when creating the render texture.");
            bool srgb = QualitySettings.activeColorSpace == ColorSpace.Linear && readWrite != RenderTextureReadWrite.Linear;
            var t = Describe(width, height, depth, format, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            t.MipRequest = mipCount > 0 ? mipCount : -1;
            Bind(t);
        }

        /// DefaultFormat.LDR and HDR are colour formats with the depth asked for; DepthStencil
        /// (24 bits) and Shadow (16) are depth-only formats of their own depth; Video's YUV2
        /// cannot be rendered to.
        public RenderTexture(int width, int height, int depth, DefaultFormat format)
        {
            var g = SystemInfo.GetGraphicsFormat(format);
            switch (format)
            {
                case DefaultFormat.DepthStencil:
                case DefaultFormat.Shadow:
                {
                    var t = Describe(width, height, RenderTextureFormats.DepthBits(g), format == DefaultFormat.Shadow ? RenderTextureFormat.Shadowmap : RenderTextureFormat.Depth);
                    t.DepthStencilFormat = g;
                    Bind(t);
                    return;
                }
                default:
                    ConstructGraphics(width, height, g, RenderTextureFormats.DepthFormat(DepthBufferBits(depth)), -1);
                    return;
            }
        }

        public RenderTexture(int width, int height, int depth, GraphicsFormat format)
            : this(width, height, depth, format, -1) { }

        /// A depth-stencil format as the colour format is an error and gives R8G8B8A8_UNorm.
        public RenderTexture(int width, int height, int depth, GraphicsFormat format, int mipCount)
            => ConstructGraphics(width, height, format, RenderTextureFormats.DepthFormat(DepthBufferBits(depth)), mipCount);

        public RenderTexture(int width, int height, GraphicsFormat colorFormat, GraphicsFormat depthStencilFormat)
            : this(width, height, colorFormat, depthStencilFormat, -1) { }

        /// A depth-stencil format that is not one is an error and gives D24_UNorm_S8_UInt;
        /// D16_UNorm_S8_UInt gives it too, silently.
        public RenderTexture(int width, int height, GraphicsFormat colorFormat, GraphicsFormat depthStencilFormat, int mipCount)
        {
            if (depthStencilFormat != GraphicsFormat.None && !RenderTextureFormats.IsDepthStencil(depthStencilFormat))
            {
                Debug.LogError("Invalid Depth/Stencil GraphicsFormat");
                depthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt;
            }
            else if (depthStencilFormat == GraphicsFormat.D16_UNorm_S8_UInt) depthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt;
            ConstructGraphics(width, height, colorFormat, depthStencilFormat, mipCount);
        }

        static int DepthBufferBits(int depth) => depth <= 0 ? 0 : depth <= 16 ? 16 : depth <= 24 ? 24 : 32;

        void ConstructGraphics(int width, int height, GraphicsFormat color, GraphicsFormat depthStencil, int mipCount)
        {
            if (RenderTextureFormats.IsDepthStencil(color))
            {
                Debug.LogError("RenderTexture color format cannot be set to a depth/stencil format");
                color = GraphicsFormat.R8G8B8A8_UNorm;
            }
            if (color != GraphicsFormat.None && !RenderTextureFormats.CanRenderTo(color))
            {
                Debug.LogError($"Texture creation failed. '{color}' is not supported for Render usage on this platform. Use 'SystemInfo.IsFormatSupported' C# API to check format support.", this);
                return;
            }
            int bits = RenderTextureFormats.DepthBits(depthStencil);
            Bind(new RenderTextureNative
            {
                Width = width,
                Height = height,
                Format = color == GraphicsFormat.None ? RenderTextureFormat.Depth : RenderTextureFormats.FromGraphicsFormat(color),
                ColorFormat = color,
                Srgb = RenderTextureFormats.IsSrgb(color),
                DepthBits = bits,
                DepthStencilFormat = depthStencil,
                Dimension = TextureKind.Tex2D,
                MipRequest = mipCount > 0 ? mipCount : -1,
                SrgbFormatAsked = RenderTextureFormats.IsSrgb(color),
            });
        }

        /// A new render texture with the other's description (not its contents).
        public RenderTexture(RenderTexture textureToCopy)
        {
            if (textureToCopy == null) throw new ArgumentNullException(nameof(textureToCopy));
            var src = (RenderTextureNative)textureToCopy.NativeOrThrow;
            var t = new RenderTextureNative();
            CopyDescription(src, t);
            Bind(t);
        }

        /// RenderTextureReadWrite.Default: sRGB in a linear-colour-space project only.
        internal static RenderTextureNative Describe(int width, int height, int depth, RenderTextureFormat format)
            => Describe(width, height, depth, format, RenderTextureReadWrite.Default);

        internal static RenderTextureNative Describe(int width, int height, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite)
            => Describe(new RenderTextureNative(), width, height, depth, format, readWrite);

        /// The description written into `into`, which no other code writes: GetTemporary looks
        /// its pool up with one kept description and makes a new one only for a texture it creates.
        static RenderTextureNative Describe(RenderTextureNative into, int width, int height, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite)
        {
            bool srgb = readWrite switch
            {
                RenderTextureReadWrite.Linear => false,
                RenderTextureReadWrite.sRGB => true,
                _ => QualitySettings.activeColorSpace == ColorSpace.Linear,
            };
            if ((format == RenderTextureFormat.Depth || format == RenderTextureFormat.Shadowmap) && depth < 16) depth = 16;
            into.Width = width;
            into.Height = height;
            into.Format = format;
            into.ColorFormat = RenderTextureFormats.ToGraphicsFormat(format, srgb);
            into.Srgb = srgb;
            into.DepthBits = depth <= 0 ? 0 : depth <= 16 ? 16 : depth <= 24 ? 24 : 32;
            into.DepthStencilFormat = RenderTextureFormats.DepthFormat(depth <= 0 ? 0 : depth <= 16 ? 16 : depth <= 24 ? 24 : 32);
            into.Dimension = TextureKind.Tex2D;
            return into;
        }

        /// GetTemporary's lookup description (main thread).
        static readonly RenderTextureNative temporaryLookup = new();

        static void CopyDescription(RenderTextureNative src, RenderTextureNative t)
        {
            t.Width = src.Width;
            t.Height = src.Height;
            t.Format = src.Format;
            t.ColorFormat = src.ColorFormat;
            t.Srgb = src.Srgb;
            t.DepthBits = src.DepthBits;
            t.DepthStencilFormat = src.DepthStencilFormat;
            t.AntiAliasing = src.AntiAliasing;
            t.UseMipMap = src.UseMipMap;
            t.AutoGenerateMips = src.AutoGenerateMips;
            t.EnableRandomWrite = src.EnableRandomWrite;
            t.Dimension = src.Dimension;
            t.VolumeDepth = src.VolumeDepth;
            t.MipCount = src.MipCount;
            t.MipRequest = src.MipRequest;
        }

        RenderTextureNative Data => (RenderTextureNative)NativeOrThrow;

        /// Description changes are only allowed while the texture is not created.
        RenderTextureNative Describable(string property)
        {
            var t = Data;
            if (t.Created)
            {
                Debug.LogError($"Setting {property} of already created render texture is not supported!", this);
                return null;
            }
            return t;
        }

        public override int width
        {
            get => Data.Width;
            set { var t = Describable("width"); if (t != null) { t.Width = value; t.MipsChanged(); } }
        }

        public override int height
        {
            get => Data.Height;
            set { var t = Describable("height"); if (t != null) { t.Height = value; t.MipsChanged(); } }
        }

        public int volumeDepth
        {
            get => Data.VolumeDepth;
            set { var t = Describable("volumeDepth"); if (t != null) t.VolumeDepth = value; }
        }

        public int depth
        {
            get => Data.DepthBits;
            set
            {
                var t = Describable("depth");
                if (t == null) return;
                t.DepthBits = value <= 0 ? 0 : value <= 16 ? 16 : value <= 24 ? 24 : 32;
                t.DepthStencilFormat = RenderTextureFormats.DepthFormat(t.DepthBits);
            }
        }

        public RenderTextureFormat format
        {
            get => Data.Format;
            set
            {
                var t = Describable("format");
                if (t == null) return;
                t.Format = RenderTextureFormats.Resolve(value);
                t.ColorFormat = RenderTextureFormats.ToGraphicsFormat(t.Format, t.Srgb);
            }
        }

        public GraphicsFormat graphicsFormat => Data.ColorFormat;

        public GraphicsFormat depthStencilFormat => Data.DepthStencilFormat;

        /// Render textures here have no separate stencil view.
        public GraphicsFormat stencilFormat => GraphicsFormat.None;

        public override TextureDimension dimension => (TextureDimension)Data.Dimension;

        public int antiAliasing
        {
            get => Data.AntiAliasing;
            set
            {
                if (value is not (1 or 2 or 4 or 8)) throw new ArgumentException("Invalid antiAliasing value (must be 1, 2, 4 or 8)");
                var t = Describable("antiAliasing");
                if (t != null) t.AntiAliasing = value;
            }
        }

        public bool useMipMap
        {
            get => Data.UseMipMap;
            set
            {
                var t = Describable("useMipMap");
                if (t == null) return;
                // The mip count a constructor asked for stays (measured: mipCount 3, then
                // useMipMap, gives 3 levels; UnityProbe's apigaps "rtctor").
                t.UseMipMap = value;
                t.MipsChanged();
            }
        }

        public bool autoGenerateMips
        {
            get => Data.AutoGenerateMips;
            set => Data.AutoGenerateMips = value;
        }

        public bool enableRandomWrite
        {
            get => Data.EnableRandomWrite;
            set { var t = Describable("enableRandomWrite"); if (t != null) t.EnableRandomWrite = value; }
        }

        public bool sRGB => Data.Srgb;

        /// True, as measured in the original (UnityProbe's apigaps "rtctor"), although only
        /// ReadPixels reads one back.
        public override bool isReadable => true;

        public bool IsCreated() => Data.Created;

        /// The device resource is created on first use; Create marks the texture as created.
        /// It fails, with the original's errors, for a size of 0 or less, a mip count over the
        /// full chain, and neither a colour nor a depth format; an sRGB GraphicsFormat given to a
        /// constructor in the game's gamma colour space is a warning.
        public bool Create()
        {
            var t = Data;
            if (t.Created) return true;
            string error = t.Width <= 0 ? "Texture must have width greater than 0."
                : t.Height <= 0 ? "Texture must have height greater than 0."
                : t.ColorFormat == GraphicsFormat.None && t.DepthStencilFormat == GraphicsFormat.None ? "colorFormat & depthStencilFormat cannot both be none."
                : t.MipRequest > TextureFormats.FullMipCount(t.Width, t.Height) ? $"Invalid mip count ({t.MipRequest}) for Texture with a max mip count of {TextureFormats.FullMipCount(t.Width, t.Height)}."
                : null;
            if (error != null)
            {
                Debug.LogError("RenderTexture.Create failed: " + error, this);
                return false;
            }
            if (t.SrgbFormatAsked && QualitySettings.activeColorSpace == ColorSpace.Gamma)
                Debug.LogWarning("Requested RenderTexture with sRGB format. sRGB formats are not supported in gamma mode, fallback to a UNorm format. Use a UNorm format instead of sRGB to silence this warning.", this);
            t.Created = true;
            return true;
        }

        /// Frees the device resource; the texture is re-created when used again.
        public void Release() => Data.ReleaseResources();

        // Contents become undefined; keeping them is a valid undefined state.
        public void DiscardContents() { }

        public void DiscardContents(bool discardColor, bool discardDepth) { }

        /// Unity's RenderBuffer identifies a render texture's colour or depth surface.
        public RenderBuffer colorBuffer => new() { m_RenderTextureInstanceID = GetInstanceID(), m_BufferPtr = RenderBufferTags.Color };

        public RenderBuffer depthBuffer => new() { m_RenderTextureInstanceID = GetInstanceID(), m_BufferPtr = RenderBufferTags.Depth };

        static RenderTexture s_Active;

        /// The render target of immediate drawing (GL, Graphics.Blit targets, ReadPixels source);
        /// null is the back buffer. Setting it is Graphics.SetRenderTarget: GL.Clear, GL drawing
        /// and ReadPixels go to it, the viewport covers it whole (also when it was active
        /// already), and it is created (measured in the original, UnityProbe's apigaps "rt").
        public static RenderTexture active
        {
            get => s_Active != null ? s_Active : null;
            set
            {
                if (value != null) value.Create();
                s_Active = value;
                if (Threads.IsMainThread) Blits.Bind(value);
            }
        }

        /// RenderTexture.active for a target the device has bound already.
        internal static void SetActiveBound(RenderTexture value) => s_Active = value;

        // ----- Temporary render textures -----

        public static RenderTexture GetTemporary(int width, int height) => GetTemporary(width, height, 0);

        public static RenderTexture GetTemporary(int width, int height, int depthBuffer) => GetTemporary(width, height, depthBuffer, RenderTextureFormat.Default);

        /// Default read/write mode, no anti-aliasing.
        public static RenderTexture GetTemporary(int width, int height, int depthBuffer, RenderTextureFormat format)
        {
            if (width <= 0) throw new ArgumentException("RenderTextureDesc width must be greater than zero.", "desc.width");
            if (height <= 0) throw new ArgumentException("RenderTextureDesc height must be greater than zero.", "desc.height");
            var resolved = RenderTextureFormats.Resolve(format);
            return TemporaryRenderTextures.Find(Describe(temporaryLookup, width, height, depthBuffer, resolved, RenderTextureReadWrite.Default))
                   ?? TemporaryRenderTextures.Get(Describe(width, height, depthBuffer, resolved));
        }

        public static RenderTexture GetTemporary(int width, int height, int depthBuffer, RenderTextureFormat format, RenderTextureReadWrite readWrite)
        {
            if (width <= 0) throw new ArgumentException("RenderTextureDesc width must be greater than zero.", "desc.width");
            if (height <= 0) throw new ArgumentException("RenderTextureDesc height must be greater than zero.", "desc.height");
            var resolved = RenderTextureFormats.Resolve(format);
            return TemporaryRenderTextures.Find(Describe(temporaryLookup, width, height, depthBuffer, resolved, readWrite))
                   ?? TemporaryRenderTextures.Get(Describe(width, height, depthBuffer, resolved, readWrite));
        }

        public static RenderTexture GetTemporary(RenderTextureDescriptor desc)
        {
            if (desc.width <= 0) throw new ArgumentException("RenderTextureDesc width must be greater than zero.", "desc.width");
            if (desc.height <= 0) throw new ArgumentException("RenderTextureDesc height must be greater than zero.", "desc.height");
            return TemporaryRenderTextures.Get(desc.ToNative());
        }

        public RenderTexture(RenderTextureDescriptor desc)
        {
            if (desc.width <= 0) throw new ArgumentException("RenderTextureDesc width must be greater than zero.", "desc.width");
            if (desc.height <= 0) throw new ArgumentException("RenderTextureDesc height must be greater than zero.", "desc.height");
            Bind(desc.ToNative());
        }

        /// The texture's description.
        public RenderTextureDescriptor descriptor => RenderTextureDescriptor.Of(Data);

        public static void ReleaseTemporary(RenderTexture temp)
        {
            if (temp == null) return;
            TemporaryRenderTextures.Release(temp);
        }
    }

    /// Everything that describes a render texture, for GetTemporary and the constructor.
    public partial struct RenderTextureDescriptor
    {
        public int width { get; set; }
        public int height { get; set; }
        public int msaaSamples { get; set; }
        public int volumeDepth { get; set; }
        public int mipCount { get; set; }
        public GraphicsFormat graphicsFormat { get; set; }
        public bool useMipMap { get; set; }
        public bool autoGenerateMips { get; set; }
        public bool enableRandomWrite { get; set; }

        int depthBits;

        /// 0, 16, 24 or 32 (other values round up to the next of these).
        public int depthBufferBits
        {
            get => depthBits;
            set => depthBits = value <= 0 ? 0 : value <= 16 ? 16 : value <= 24 ? 24 : 32;
        }

        public RenderTextureFormat colorFormat
        {
            get => shadowmap && graphicsFormat == GraphicsFormat.None ? RenderTextureFormat.Shadowmap : RenderTextureFormats.FromGraphicsFormat(graphicsFormat);
            set
            {
                graphicsFormat = RenderTextureFormats.ToGraphicsFormat(value, sRGB);
                shadowmap = value == RenderTextureFormat.Shadowmap;
            }
        }

        /// The colour format stores sRGB-encoded values; setting it picks the sRGB or linear
        /// variant of the format.
        public bool sRGB
        {
            get => graphicsFormat is GraphicsFormat.R8G8B8A8_SRGB or GraphicsFormat.B8G8R8A8_SRGB;
            set => graphicsFormat = (graphicsFormat, value) switch
            {
                (GraphicsFormat.R8G8B8A8_UNorm, true) => GraphicsFormat.R8G8B8A8_SRGB,
                (GraphicsFormat.R8G8B8A8_SRGB, false) => GraphicsFormat.R8G8B8A8_UNorm,
                (GraphicsFormat.B8G8R8A8_UNorm, true) => GraphicsFormat.B8G8R8A8_SRGB,
                (GraphicsFormat.B8G8R8A8_SRGB, false) => GraphicsFormat.B8G8R8A8_UNorm,
                _ => graphicsFormat,
            };
        }

        public RenderTextureDescriptor(int width, int height) : this(width, height, RenderTextureFormat.Default, 0) { }

        public RenderTextureDescriptor(int width, int height, RenderTextureFormat colorFormat) : this(width, height, colorFormat, 0) { }

        /// RenderTextureReadWrite.Default: sRGB in a linear-colour-space project only.
        public RenderTextureDescriptor(int width, int height, RenderTextureFormat colorFormat, int depthBufferBits)
            : this(width, height, RenderTextureFormats.ToGraphicsFormat(colorFormat, QualitySettings.activeColorSpace == ColorSpace.Linear), depthBufferBits) { }

        public RenderTextureDescriptor(int width, int height, GraphicsFormat colorFormat, int depthBufferBits)
        {
            this = default;
            this.width = width;
            this.height = height;
            graphicsFormat = colorFormat;
            this.depthBufferBits = depthBufferBits;
            msaaSamples = 1;
            volumeDepth = 1;
            mipCount = -1;
            autoGenerateMips = true;
        }

        internal RenderTextureNative ToNative()
        {
            if (msaaSamples is not (1 or 2 or 4 or 8)) throw new ArgumentException("RenderTextureDesc msaaSamples must be 1, 2, 4, or 8.", "desc.msaaSamples");
            return new RenderTextureNative
            {
                Width = width,
                Height = height,
                Format = colorFormat,
                ColorFormat = graphicsFormat,
                Srgb = sRGB,
                DepthBits = depthBufferBits,
                DepthStencilFormat = RenderTextureFormats.DepthFormat(depthBufferBits),
                AntiAliasing = msaaSamples,
                UseMipMap = useMipMap,
                AutoGenerateMips = autoGenerateMips,
                EnableRandomWrite = enableRandomWrite,
                Dimension = TextureKind.Tex2D,
                VolumeDepth = volumeDepth,
                // Unity: with mip maps, mipCount -1 is the full chain; without, one level.
                MipCount = !useMipMap ? 1 : mipCount > 0 ? mipCount : TextureFormats.FullMipCount(width, height),
                MipRequest = mipCount > 0 ? mipCount : -1,
            };
        }

        /// The depth/stencil format: the one depthBufferBits gives unless one was set.
        public GraphicsFormat depthStencilFormat
        {
            get => depthStencil ?? RenderTextureFormats.DepthFormat(depthBufferBits);
            set => depthStencil = value;
        }

        GraphicsFormat? depthStencil;

        public TextureDimension dimension => TextureDimension.Tex2D;

        /// mipCount is the count asked for, -1 for none (measured: -1 after the constructors
        /// without one, with useMipMap false).
        // Shadowmap has no GraphicsFormat of its own (None, as Depth): kept to report it.
        bool shadowmap;

        internal static RenderTextureDescriptor Of(RenderTextureNative t) => new(t.Width, t.Height, t.ColorFormat, t.DepthBits)
        {
            shadowmap = t.Format == RenderTextureFormat.Shadowmap,
            depthStencil = t.DepthStencilFormat,
            msaaSamples = Math.Max(1, t.AntiAliasing),
            volumeDepth = t.VolumeDepth,
            mipCount = t.MipRequest,
            useMipMap = t.UseMipMap,
            autoGenerateMips = t.AutoGenerateMips,
            enableRandomWrite = t.EnableRandomWrite,
        };
    }

    public partial struct RenderBuffer
    {
        public IntPtr GetNativeRenderBufferPtr() => m_BufferPtr;
    }
}

namespace OniMoono.Engine.Rendering
{
    using UnityEngine;

    /// RenderBuffer.m_BufferPtr values: which surface of the render texture.
    internal static class RenderBufferTags
    {
        public static readonly IntPtr Color = 1;
        public static readonly IntPtr Depth = 2;
    }

    /// RenderTexture.GetTemporary's pool: a released texture is reused by the next request with
    /// the same description, and destroyed once it has been unused for a few frames.
    internal static class TemporaryRenderTextures
    {
        const int UnusedFramesBeforeDestroy = 15;

        static readonly List<RenderTexture> pool = new();

        /// A released texture of the description, now in use; null when the pool has none.
        /// The description is only read.
        public static RenderTexture Find(RenderTextureNative description)
        {
            Threads.AssertMainThread("RenderTexture.GetTemporary");
            foreach (var rt in pool)
            {
                var t = (RenderTextureNative)rt.Native;
                if (t == null || t.TemporaryInUse || !SameDescription(t, description)) continue;
                t.TemporaryInUse = true;
                t.TemporaryLastFrame = Time.frameCount;
                return rt;
            }
            return null;
        }

        /// A texture of the description: a released one from the pool, else one made from
        /// `description` (which becomes the new texture's).
        public static RenderTexture Get(RenderTextureNative description)
        {
            if (Find(description) is { } pooled) return pooled;
            // A temporary's anisoLevel reads 0, a constructed one's 1 (measured in the original,
            // UnityProbe's apigaps "rtctor").
            description.Aniso = 0;
            description.IsTemporary = true;
            description.TemporaryInUse = true;
            description.TemporaryLastFrame = Time.frameCount;
            description.Name = "TempBuffer " + (pool.Count + 1) + " " + description.Width + "x" + description.Height;
            var created = Object.ConstructBound<RenderTexture>(typeof(RenderTexture), description);
            created.Create();
            pool.Add(created);
            return created;
        }

        public static void Release(RenderTexture rt)
        {
            if (rt.Native is not RenderTextureNative { IsTemporary: true } t)
            {
                Debug.LogError("Attempting to release RenderTexture that was not gotten as a temp buffer");
                return;
            }
            t.TemporaryInUse = false;
            t.TemporaryLastFrame = Time.frameCount;
        }

        /// Destroys pooled textures released long enough ago (end of each frame).
        public static void EndFrame()
        {
            int frame = Time.frameCount;
            for (int i = pool.Count - 1; i >= 0; i--)
            {
                var rt = pool[i];
                if (rt.Native is not RenderTextureNative t)
                {
                    pool.RemoveAt(i);
                    continue;
                }
                if (t.TemporaryInUse || frame - t.TemporaryLastFrame < UnusedFramesBeforeDestroy) continue;
                pool.RemoveAt(i);
                Object.DestroyImmediate(rt);
            }
        }

        static bool SameDescription(RenderTextureNative a, RenderTextureNative b)
            => a.Width == b.Width && a.Height == b.Height && a.ColorFormat == b.ColorFormat && a.DepthStencilFormat == b.DepthStencilFormat
               && a.AntiAliasing == b.AntiAliasing && a.Dimension == b.Dimension && a.VolumeDepth == b.VolumeDepth && a.UseMipMap == b.UseMipMap
               && a.EnableRandomWrite == b.EnableRandomWrite && a.Srgb == b.Srgb;
    }
}
