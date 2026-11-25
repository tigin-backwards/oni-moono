using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.D3d11
{
    /// A texture or render texture on Direct3D 11 (DeviceTexture.D3d), device thread only.
    /// - A texture: one Texture2D (a 2D array's slices as its array) or Texture3D, usage DEFAULT,
    ///   bound as a shader resource, with a view of the whole resource.
    /// - A render texture: its colour Texture2D (shader resource and render target, with
    ///   GENERATE_MIPS when it has levels), a render target view per level made on first use, a
    ///   multisampled Texture2D and its view when it has samples (resolved into the colour one
    ///   before it is read), and a depth surface (DepthStencil, with Dsv; DepthOnly: the texture
    ///   is the depth surface, sampled through Srv).
    internal sealed unsafe class D3d11Texture
    {
        public ID3D11Resource* Resource;
        public ID3D11ShaderResourceView* Srv;
        public ID3D11Texture2D* Multisampled;
        public ID3D11RenderTargetView* MultisampledRtv;
        public ID3D11RenderTargetView*[] Rtvs = [];
        public ID3D11Texture2D* DepthStencil;
        public ID3D11DepthStencilView* Dsv;
        public DXGI_FORMAT Format;
        public int Width, Height, Depth = 1, Levels = 1, Layers = 1, Samples = 1;
        public uint Target = TexTarget2D;
        public bool RenderTarget, DepthOnly, HasStencil, Dirty;
        /// Made with GENERATE_MIPS (levels, and a format with MIP_AUTOGEN support).
        public bool AutoMips;
        public TextureFormat StorageFormat = (TextureFormat)(-1);
        /// The sampler its settings select (owned by the device's cache).
        public ID3D11SamplerState* Sampler;

        public void Release()
        {
            for (int i = 0; i < Rtvs.Length; i++)
            {
                var v = Rtvs[i];
                Com.Release(ref v);
            }
            Rtvs = [];
            Com.Release(ref MultisampledRtv);
            Com.Release(ref Multisampled);
            Com.Release(ref Dsv);
            Com.Release(ref DepthStencil);
            Com.Release(ref Srv);
            Com.Release(ref Resource);
            Sampler = null;
        }
    }

    /// A mesh's buffers on Direct3D 11 (DeviceMesh.D3d): every channel in one DYNAMIC vertex
    /// buffer at its own offset (each channel its own input slot), the indices (32-bit) in a
    /// DYNAMIC index buffer; an upload that fits is written with Map(WRITE_DISCARD).
    internal sealed unsafe class D3d11Mesh
    {
        public ID3D11Buffer* Vertices, Indices;
        public long VertexCapacity, IndexCapacity;
        public readonly long[] ChannelOffsets = new long[(int)Channel.Count];

        public void Release()
        {
            Com.Release(ref Vertices);
            Com.Release(ref Indices);
            VertexCapacity = IndexCapacity = 0;
        }
    }

    internal sealed unsafe partial class D3d11Device
    {
        readonly HashSet<D3d11Texture> liveTextures = new();
        readonly HashSet<D3d11Mesh> liveMeshes = new();
        readonly HashSet<D3d11Shaders> livePrograms = new();
        readonly Dictionary<(GlTextures.SamplerSettings, bool, bool), nint> samplers = new();
        readonly Dictionary<string, DeviceTexture> defaults = new();
        ID3D11SamplerState* defaultSampler;
        /// What inputs no mesh channel feeds read (D3d11Layouts.ConstantOffset).
        ID3D11Buffer* constants;
        byte[] conversionScratch;

        ID3D11Device* Dev => d3d.Device;
        ID3D11DeviceContext* Ctx => d3d.Context;

        /// The shared objects every draw may need (device thread, before the first frame).
        void CreateShared()
        {
            // Zeros, white, (0, 0, 0, 1) as floats, (0, 0, 0, 1) as integers (D3d11Layouts).
            var values = stackalloc uint[16];
            var f = (float*)values;
            for (int i = 0; i < 16; i++) values[i] = 0;
            f[4] = f[5] = f[6] = f[7] = 1f;
            f[11] = 1f;
            values[15] = 1;
            var desc = new D3D11_BUFFER_DESC { ByteWidth = 64, Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER };
            var data = new D3D11_SUBRESOURCE_DATA { pSysMem = values };
            ID3D11Buffer* b;
            D3d11Context.Check(Dev->lpVtbl->CreateBuffer(Dev, &desc, &data, &b), "CreateBuffer (vertex constants)");
            constants = b;
            defaultSampler = Sampler(new GlTextures.SamplerSettings(FilterMode.Bilinear, TextureWrapMode.Repeat, TextureWrapMode.Repeat, TextureWrapMode.Repeat, 0, 1), false, false);
            foreach (var (name, r, g, bl, a) in DefaultTextures.Colors) MakeDefault(name, r, g, bl, a);
        }

        /// A 1x1 texture of a colour for each sampler kind (as GlDevice's defaults).
        void MakeDefault(string name, byte r, byte g, byte b, byte a)
        {
            uint texel = (uint)(r | g << 8 | b << 16 | a << 24);
            var texels = stackalloc uint[6] { texel, texel, texel, texel, texel, texel };
            void Make(string key, uint target, uint* texels)
            {
                var t = new D3d11Texture { Target = target, Width = 1, Height = 1, Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, Sampler = defaultSampler };
                if (target == TexTarget3D)
                {
                    var desc = new D3D11_TEXTURE3D_DESC
                    {
                        Width = 1, Height = 1, Depth = 1, MipLevels = 1, Format = t.Format, Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
                        BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                    };
                    var data = new D3D11_SUBRESOURCE_DATA { pSysMem = texels, SysMemPitch = 4, SysMemSlicePitch = 4 };
                    ID3D11Texture3D* tex;
                    D3d11Context.Check(Dev->lpVtbl->CreateTexture3D(Dev, &desc, &data, &tex), $"CreateTexture3D (default {name})");
                    t.Resource = (ID3D11Resource*)tex;
                }
                else
                {
                    uint layers = target == TexTargetCubeMap ? 6u : 1u;
                    var desc = new D3D11_TEXTURE2D_DESC
                    {
                        Width = 1, Height = 1, MipLevels = 1, ArraySize = layers, Format = t.Format, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                        Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                        MiscFlags = target == TexTargetCubeMap ? D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_TEXTURECUBE : 0,
                    };
                    var data = stackalloc D3D11_SUBRESOURCE_DATA[6];
                    for (int i = 0; i < 6; i++) data[i] = new D3D11_SUBRESOURCE_DATA { pSysMem = texels + i, SysMemPitch = 4 };
                    ID3D11Texture2D* tex;
                    D3d11Context.Check(Dev->lpVtbl->CreateTexture2D(Dev, &desc, data, &tex), $"CreateTexture2D (default {name})");
                    t.Resource = (ID3D11Resource*)tex;
                    t.Layers = (int)layers;
                }
                t.Srv = ShaderView(t.Resource, t.Format, target, 1, t.Layers);
                defaults[key] = new DeviceTexture { Target = target, RecordedTarget = target, Width = 1, Height = 1, D3d = t };
            }
            Make(name, TexTarget2D, texels);
            Make("array:" + name, TexTarget2DArray, texels);
            Make("3d:" + name, TexTarget3D, texels);
            Make("cube:" + name, TexTargetCubeMap, texels);
        }

        DeviceTexture DefaultTexture(string name, uint target)
        {
            name ??= "";
            string prefix = target == TexTarget2DArray ? "array:" : target == TexTarget3D ? "3d:" : target == TexTargetCubeMap ? "cube:" : "";
            return defaults.TryGetValue(prefix + name, out var t) ? t : defaults[prefix + DefaultTextures.Unnamed];
        }

        /// A shader resource view of a whole texture: its levels, and its slices as an array (or
        /// a cube's six faces).
        ID3D11ShaderResourceView* ShaderView(ID3D11Resource* resource, DXGI_FORMAT format, uint target, int levels, int layers)
        {
            var desc = new D3D11_SHADER_RESOURCE_VIEW_DESC { Format = format };
            switch (target)
            {
                case TexTarget3D:
                    desc.ViewDimension = D3D11_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE3D;
                    desc.Texture3D = new D3D11_TEX3D_SRV { MipLevels = (uint)levels };
                    break;
                case TexTargetCubeMap:
                    desc.ViewDimension = D3D11_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURECUBE;
                    desc.TextureCube = new D3D11_TEXCUBE_SRV { MipLevels = (uint)levels };
                    break;
                case TexTarget2DArray:
                    desc.ViewDimension = D3D11_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
                    desc.Texture2DArray = new D3D11_TEX2D_ARRAY_SRV { MipLevels = (uint)levels, ArraySize = (uint)layers };
                    break;
                default:
                    desc.ViewDimension = D3D11_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2D;
                    desc.Texture2D = new D3D11_TEX2D_SRV { MipLevels = (uint)levels };
                    break;
            }
            ID3D11ShaderResourceView* v;
            D3d11Context.Check(Dev->lpVtbl->CreateShaderResourceView(Dev, resource, &desc, &v), $"CreateShaderResourceView ({format})");
            return v;
        }

        // ----- IResourceWork (device thread) -----

        public DeviceProgram Program(ShaderNative shader, int key)
        {
            var p = D3d11Programs.Get(Dev, shader, key);
            if (p.D3d != null) livePrograms.Add(p.D3d);
            return p;
        }

        /// New render surfaces start cleared, as Unity's (and GlTextures.ClearNewSurfaces):
        /// colour (0, 0, 0, 0), depth at the far plane (0, reversed), stencil 0.
        public void CreateRenderTexture(DeviceTexture g, RenderShape shape, string name)
        {
            var t = g.D3d ??= new D3d11Texture();
            liveTextures.Add(t);
            bool wasTarget = Unbind(t);
            t.Release();
            try
            {
                MakeRenderTexture(t, shape, name);
            }
            finally
            {
                if (wasTarget) SetTarget(lastTarget);
            }
        }

        void MakeRenderTexture(D3d11Texture t, RenderShape shape, string name)
        {
            t.RenderTarget = true;
            t.DepthOnly = shape.ColorFormat == GraphicsFormat.None;
            t.Dirty = false;
            t.Width = Math.Max(1, shape.Width);
            t.Height = Math.Max(1, shape.Height);
            t.Levels = Math.Max(1, shape.Levels);
            t.Samples = Math.Max(1, shape.Samples);
            t.Target = TexTarget2D;
            t.StorageFormat = (TextureFormat)(-1);
            t.Sampler = defaultSampler;
            if (t.DepthOnly)
            {
                MakeDepth(t, shape.DepthBits, 1, shaderView: true);
                return;
            }
            t.Format = D3d11Formats.Color(shape.ColorFormat);
            // GENERATE_MIPS only where the format has D3D11_FORMAT_SUPPORT_MIP_AUTOGEN
            // ("Automatic generation of mipmaps is supported", D3D11_FORMAT_SUPPORT); otherwise
            // the levels above 0 keep what they were cleared to, logged once per format.
            t.AutoMips = t.Levels > 1 && FormatHas(t.Format, D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_MIP_AUTOGEN);
            if (t.Levels > 1 && !t.AutoMips && noAutoMips.Add(t.Format))
                EngineLog.Engine($"d3d11: {t.Format} has no automatic mip generation on this device; render texture '{name}' keeps its levels above 0 as cleared");
            // A sample count the device has for the format (CheckMultisampleQualityLevels: a
            // combination it lacks gives a failure or 0 quality levels), and its depth's: the
            // next lower power of two otherwise, logged once.
            if (t.Samples > 1)
            {
                int asked = t.Samples;
                var depthView = shape.DepthBits > 0 ? D3d11Formats.Depth(shape.DepthBits).View : DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;
                while (t.Samples > 1 && !(Multisamples(t.Format, t.Samples) && (depthView == DXGI_FORMAT.DXGI_FORMAT_UNKNOWN || Multisamples(depthView, t.Samples)))) t.Samples /= 2;
                if (t.Samples != asked && fewerSamples.Add((t.Format, asked)))
                    EngineLog.Engine($"d3d11: {t.Format} with {asked} samples is not supported on this device; render texture '{name}' gets {t.Samples}");
            }
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)t.Width, Height = (uint)t.Height, MipLevels = (uint)t.Levels, ArraySize = 1, Format = t.Format, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
                // GenerateMips needs it, with both binds (ID3D11DeviceContext::GenerateMips).
                MiscFlags = t.AutoMips ? D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS : 0,
            };
            ID3D11Texture2D* tex;
            D3d11Context.Check(Dev->lpVtbl->CreateTexture2D(Dev, &desc, null, &tex), $"CreateTexture2D (render texture {name}, {t.Width}x{t.Height} {t.Format})");
            t.Resource = (ID3D11Resource*)tex;
            t.Srv = ShaderView(t.Resource, t.Format, TexTarget2D, t.Levels, 1);
            t.Rtvs = new ID3D11RenderTargetView*[t.Levels];
            var zero = stackalloc float[4];
            for (int level = 0; level < t.Levels; level++) Ctx->lpVtbl->ClearRenderTargetView(Ctx, Rtv(t, level), zero);
            if (t.Samples > 1)
            {
                var ms = desc with { MipLevels = 1, SampleDesc = new DXGI_SAMPLE_DESC { Count = (uint)t.Samples }, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET, MiscFlags = 0 };
                ID3D11Texture2D* m;
                D3d11Context.Check(Dev->lpVtbl->CreateTexture2D(Dev, &ms, null, &m), $"CreateTexture2D (render texture {name}, {t.Samples} samples)");
                t.Multisampled = m;
                ID3D11RenderTargetView* v;
                D3d11Context.Check(Dev->lpVtbl->CreateRenderTargetView(Dev, (ID3D11Resource*)m, null, &v), "CreateRenderTargetView (multisampled)");
                t.MultisampledRtv = v;
                Ctx->lpVtbl->ClearRenderTargetView(Ctx, v, zero);
            }
            if (shape.DepthBits > 0) MakeDepth(t, shape.DepthBits, t.Samples, shaderView: false);
        }

        /// A depth surface for `t` (its own DepthStencil and Dsv; with `shaderView`, a depth-only
        /// texture, also its Resource and Srv), cleared to the far plane.
        void MakeDepth(D3d11Texture t, int bits, int samples, bool shaderView)
        {
            var (resource, view, shader, stencil) = D3d11Formats.Depth(bits);
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)t.Width, Height = (uint)t.Height, MipLevels = 1, ArraySize = 1, Format = resource, SampleDesc = new DXGI_SAMPLE_DESC { Count = (uint)samples },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL | (shaderView ? D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE : 0),
            };
            ID3D11Texture2D* tex;
            D3d11Context.Check(Dev->lpVtbl->CreateTexture2D(Dev, &desc, null, &tex), $"CreateTexture2D (depth, {t.Width}x{t.Height} {resource})");
            t.DepthStencil = tex;
            t.HasStencil = stencil;
            var dsv = new D3D11_DEPTH_STENCIL_VIEW_DESC
            {
                Format = view,
                ViewDimension = samples > 1 ? D3D11_DSV_DIMENSION.D3D11_DSV_DIMENSION_TEXTURE2DMS : D3D11_DSV_DIMENSION.D3D11_DSV_DIMENSION_TEXTURE2D,
            };
            ID3D11DepthStencilView* v;
            D3d11Context.Check(Dev->lpVtbl->CreateDepthStencilView(Dev, (ID3D11Resource*)tex, &dsv, &v), $"CreateDepthStencilView ({view})");
            t.Dsv = v;
            Ctx->lpVtbl->ClearDepthStencilView(Ctx, v, D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH | (stencil ? D3D11_CLEAR_FLAG.D3D11_CLEAR_STENCIL : 0), 0f, 0);
            if (!shaderView) return;
            ((IUnknown*)tex)->lpVtbl->AddRef((IUnknown*)tex);
            t.Resource = (ID3D11Resource*)tex;
            t.Format = shader;
            t.Srv = ShaderView(t.Resource, shader, TexTarget2D, 1, 1);
        }

        readonly HashSet<DXGI_FORMAT> noAutoMips = new();
        readonly HashSet<(DXGI_FORMAT, int)> fewerSamples = new();

        /// Whether the device supports every use in `wanted` for `format` (CheckFormatSupport).
        bool FormatHas(DXGI_FORMAT format, D3D11_FORMAT_SUPPORT wanted)
        {
            D3D11_FORMAT_SUPPORT support = 0;
            return Dev->lpVtbl->CheckFormatSupport(Dev, format, &support) >= 0 && (support & wanted) == wanted;
        }

        /// Whether `format` takes `samples` samples (CheckMultisampleQualityLevels: S_OK and a
        /// positive quality-level count).
        bool Multisamples(DXGI_FORMAT format, int samples)
        {
            uint levels = 0;
            return Dev->lpVtbl->CheckMultisampleQualityLevels(Dev, format, (uint)samples, &levels) >= 0 && levels > 0;
        }

        /// The render target view of a level (made on first use).
        ID3D11RenderTargetView* Rtv(D3d11Texture t, int level)
        {
            level = Math.Clamp(level, 0, t.Rtvs.Length - 1);
            if (t.Rtvs[level] != null) return t.Rtvs[level];
            var desc = new D3D11_RENDER_TARGET_VIEW_DESC
            {
                Format = t.Format, ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D, Texture2D = new D3D11_TEX2D_RTV { MipSlice = (uint)level },
            };
            ID3D11RenderTargetView* v;
            D3d11Context.Check(Dev->lpVtbl->CreateRenderTargetView(Dev, t.Resource, &desc, &v), $"CreateRenderTargetView (level {level})");
            t.Rtvs[level] = v;
            return v;
        }

        /// Before a render texture is read: multisampling resolved, mips rebuilt (as
        /// GlTextures.PrepareForSampling).
        void PrepareForSampling(D3d11Texture t, bool autoGenerateMips)
        {
            if (!t.Dirty || !t.RenderTarget || t.DepthOnly) return;
            t.Dirty = false;
            if (t.Multisampled != null) Ctx->lpVtbl->ResolveSubresource(Ctx, t.Resource, 0, (ID3D11Resource*)t.Multisampled, 0, t.Format);
            if (t.AutoMips && autoGenerateMips) Ctx->lpVtbl->GenerateMips(Ctx, t.Srv);
        }

        public void UploadTexture(DeviceTexture g, TexelUpload u)
        {
            long t = D3d11Timings.Start();
            u.Copying.Wait();
            try
            {
                Upload(g, u);
            }
            finally
            {
                if (u.Pooled) { if (u.Memory != null) UploadBuffers.Return(u.Memory); }
                else u.Memory?.Free();
                u.Memory = null;
                D3d11Timings.Add(ref D3d11Timings.TextureUploads, t);
            }
        }

        /// Storage is made once per size, format and shape, with the texels as its initial data;
        /// a texture uploaded again into unchanged storage has each level written by
        /// UpdateSubresource.
        void Upload(DeviceTexture g, TexelUpload u)
        {
            var f = D3d11Formats.Of(u.Format);
            // D3D11 makes a block-compressed texture only of whole blocks at level 0
            // (D3D11_TEXTURE2D_DESC remarks): others are decoded, as formats nothing samples.
            if (f.BlockSize > 1 && (u.Width % f.BlockSize != 0 || u.Height % f.BlockSize != 0)) f = D3d11Formats.Decoded(u.Format);
            int levels = Math.Max(1, u.MipCount);
            uint target = u.Target == TexTarget2DArray ? TexTarget2DArray : u.Target == TexTarget3D ? TexTarget3D : TexTarget2D;
            int layers = target == TexTarget2DArray ? Math.Max(1, u.Depth) : 1, depth = target == TexTarget3D ? Math.Max(1, u.Depth) : 1;
            var t = g.D3d ??= new D3d11Texture();
            liveTextures.Add(t);
            bool same = t.Resource != null && !t.RenderTarget && t.StorageFormat == u.Format && t.Target == target && t.Width == u.Width && t.Height == u.Height
                        && t.Levels == levels && t.Layers == layers && t.Depth == depth;
            // Each subresource's texels (converted when the format needs it), level by level
            // within each slice: D3D11CalcSubresource's order (MipSlice + ArraySlice * MipLevels).
            int subresources = levels * layers;
            var datas = new D3D11_SUBRESOURCE_DATA[subresources];
            var kept = new List<byte[]>();
            var handles = new List<GCHandle>();
            byte* zeros = null;
            try
            {
                for (int s = 0; s < layers; s++)
                    for (int m = 0; m < levels; m++)
                    {
                        int w = Math.Max(1, u.Width >> m), h = Math.Max(1, u.Height >> m), d = Math.Max(1, depth >> m);
                        int blocksWide = (w + f.BlockSize - 1) / f.BlockSize, blocksHigh = (h + f.BlockSize - 1) / f.BlockSize;
                        uint pitch = (uint)(blocksWide * f.BlockBytes), slice = (uint)(pitch * blocksHigh);
                        void* p;
                        if (u.Memory == null)
                        {
                            // No texels (a texture made and not filled): zero, as drivers clear
                            // new OpenGL storage.
                            if (zeros == null)
                            {
                                long most = (long)Math.Max(1, (u.Width + f.BlockSize - 1) / f.BlockSize) * f.BlockBytes * Math.Max(1, (u.Height + f.BlockSize - 1) / f.BlockSize) * depth;
                                zeros = (byte*)NativeMemory.AllocZeroed((nuint)most);
                            }
                            p = zeros;
                        }
                        else
                        {
                            long offset = target == TexTarget2DArray ? s * u.SliceBytes + u.MipOffset(m) : u.MipOffset(m);
                            long length = target == TexTarget3D ? u.MipSize(m) : TextureFormats.MipSize(u.Format, w, h);
                            var data = D3d11Formats.Convert(f, u.Format, u.Memory.Pointer + offset, length, w, h * d, ref conversionScratch);
                            // The converted bytes live in the scratch array, reused by the next
                            // level: each kept in its own copy until the texture is made.
                            var copy = data.ToArray();
                            kept.Add(copy);
                            var handle = GCHandle.Alloc(copy, GCHandleType.Pinned);
                            handles.Add(handle);
                            p = (void*)handle.AddrOfPinnedObject();
                        }
                        datas[m + s * levels] = new D3D11_SUBRESOURCE_DATA { pSysMem = p, SysMemPitch = pitch, SysMemSlicePitch = slice };
                    }
                if (same)
                {
                    for (int i = 0; i < subresources; i++)
                        Ctx->lpVtbl->UpdateSubresource(Ctx, t.Resource, (uint)i, null, datas[i].pSysMem, datas[i].SysMemPitch, datas[i].SysMemSlicePitch);
                    return;
                }
                ID3D11Resource* made;
                fixed (D3D11_SUBRESOURCE_DATA* initial = datas)
                {
                    if (target == TexTarget3D)
                    {
                        var desc = new D3D11_TEXTURE3D_DESC
                        {
                            Width = (uint)u.Width, Height = (uint)u.Height, Depth = (uint)depth, MipLevels = (uint)levels, Format = f.Format,
                            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                        };
                        ID3D11Texture3D* tex;
                        D3d11Context.Check(Dev->lpVtbl->CreateTexture3D(Dev, &desc, initial, &tex), $"CreateTexture3D ({u.Width}x{u.Height}x{depth} {f.Format})");
                        made = (ID3D11Resource*)tex;
                    }
                    else
                    {
                        var desc = new D3D11_TEXTURE2D_DESC
                        {
                            Width = (uint)u.Width, Height = (uint)u.Height, MipLevels = (uint)levels, ArraySize = (uint)layers, Format = f.Format,
                            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                        };
                        ID3D11Texture2D* tex;
                        D3d11Context.Check(Dev->lpVtbl->CreateTexture2D(Dev, &desc, initial, &tex), $"CreateTexture2D ({u.Width}x{u.Height}x{layers} {f.Format}, {levels} levels)");
                        made = (ID3D11Resource*)tex;
                    }
                }
                ID3D11ShaderResourceView* srv;
                try
                {
                    srv = ShaderView(made, f.Format, target, levels, layers);
                }
                catch
                {
                    Com.Release(ref made);
                    throw;
                }
                // Made before the old storage goes: a failure leaves the texture as it was.
                Unbind(t);
                t.Release();
                t.Resource = made;
                t.Srv = srv;
                t.Format = f.Format;
                t.Target = target;
                t.Width = u.Width;
                t.Height = u.Height;
                t.Levels = levels;
                t.Layers = layers;
                t.Depth = depth;
                t.RenderTarget = t.DepthOnly = false;
                t.StorageFormat = u.Format;
                t.Sampler = defaultSampler;
            }
            finally
            {
                foreach (var h in handles) h.Free();
                if (zeros != null) NativeMemory.Free(zeros);
            }
        }

        /// Point: nearest; Bilinear: linear within a level, nearest level; Trilinear: linear
        /// across levels (as GlTextures.ApplySampler); anisotropy above 1 with a filter other
        /// than Point is D3D11's ANISOTROPIC (as the original's MaxAnisotropy 4 sampler,
        /// d3d11-captures.md §2). The other fields as the original's samplers: MinLOD -FLT_MAX,
        /// MaxLOD FLT_MAX, ComparisonFunc NEVER, border 0.
        public void ApplySampler(DeviceTexture g, GlTextures.SamplerSettings s)
        {
            var t = g.D3d;
            if (t?.Resource == null) return;
            t.Sampler = Sampler(s, t.Levels > 1, t.Target == TexTarget3D);
        }

        ID3D11SamplerState* Sampler(GlTextures.SamplerSettings s, bool mips, bool volume)
        {
            var key = (s, mips, volume);
            if (samplers.TryGetValue(key, out var known)) return (ID3D11SamplerState*)known;
            // d3d11.h: D3D11_REQ_MAXANISOTROPY 16, D3D11_MIP_LOD_BIAS_MIN -16.0f and _MAX 15.99f.
            uint anisotropy = (uint)Math.Clamp(s.Aniso, 1, 16);
            var filter = s.FilterMode == FilterMode.Point ? D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_POINT
                : anisotropy > 1 ? D3D11_FILTER.D3D11_FILTER_ANISOTROPIC
                : s.FilterMode == FilterMode.Trilinear && mips ? D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR
                : D3D11_FILTER.D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
            var desc = new D3D11_SAMPLER_DESC
            {
                Filter = filter, AddressU = Wrap(s.WrapU), AddressV = Wrap(s.WrapV), AddressW = volume ? Wrap(s.WrapW) : D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP,
                MipLODBias = Math.Clamp(s.MipBias, -16f, 15.99f), MaxAnisotropy = filter == D3D11_FILTER.D3D11_FILTER_ANISOTROPIC ? anisotropy : 1,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER, MinLOD = -float.MaxValue, MaxLOD = float.MaxValue,
            };
            ID3D11SamplerState* state;
            D3d11Context.Check(Dev->lpVtbl->CreateSamplerState(Dev, &desc, &state), "CreateSamplerState");
            samplers[key] = (nint)state;
            return state;
        }

        static D3D11_TEXTURE_ADDRESS_MODE Wrap(TextureWrapMode m) => m switch
        {
            TextureWrapMode.Clamp => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            TextureWrapMode.Mirror => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_MIRROR,
            TextureWrapMode.MirrorOnce => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_MIRROR_ONCE,
            _ => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP,
        };

        public void UploadMesh(DeviceMesh d, MeshUpload upload)
        {
            long t = D3d11Timings.Start();
            try
            {
                if (MeshNative.VerifyReuse) MeshReuseVerify.Uploading(upload);
                CopyMesh(d, upload);
            }
            finally
            {
                upload.Ran();
                D3d11Timings.Add(ref D3d11Timings.MeshUploads, t);
            }
        }

        void CopyMesh(DeviceMesh d, MeshUpload upload)
        {
            var m = d.D3d ??= new D3d11Mesh();
            liveMeshes.Add(m);
            long total = 0;
            Span<long> offsets = stackalloc long[m.ChannelOffsets.Length];
            for (int c = 0; c < upload.Channels.Length; c++)
            {
                offsets[c] = -1;
                if (upload.Channels[c] is not { } array) continue;
                offsets[c] = total;
                total = (total + AsBytes(array, upload.VertexCount).Length + 15) / 16 * 16;
            }
            long vertexBytes = Math.Max(16, total), indexBytes = Math.Max(16L, (long)(upload.Indices != null ? upload.IndexCount : 0) * 4);
            var vertices = Write(ref m.Vertices, ref m.VertexCapacity, vertexBytes, D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER);
            for (int c = 0; c < upload.Channels.Length; c++)
                if (upload.Channels[c] is { } array) AsBytes(array, upload.VertexCount).CopyTo(new Span<byte>(vertices + offsets[c], (int)(vertexBytes - offsets[c])));
            Ctx->lpVtbl->Unmap(Ctx, (ID3D11Resource*)m.Vertices, 0);
            offsets.CopyTo(m.ChannelOffsets);
            var indices = Write(ref m.Indices, ref m.IndexCapacity, indexBytes, D3D11_BIND_FLAG.D3D11_BIND_INDEX_BUFFER);
            if (upload.Indices != null) MemoryMarshal.AsBytes(upload.Indices.AsSpan(0, upload.IndexCount)).CopyTo(new Span<byte>(indices, (int)indexBytes));
            Ctx->lpVtbl->Unmap(Ctx, (ID3D11Resource*)m.Indices, 0);
        }

        /// The buffer mapped for `bytes` of new contents (Unmap is the caller's): the one there
        /// when large enough, mapped with WRITE_DISCARD ("the previous contents of the resource
        /// will be undefined", D3D11_MAP), else a new DYNAMIC buffer with CPU write access, which
        /// that map needs, with room for growth when it replaces one.
        byte* Write(ref ID3D11Buffer* buffer, ref long capacity, long bytes, D3D11_BIND_FLAG bind)
        {
            if (buffer == null || capacity < bytes)
            {
                long size = buffer == null ? bytes : Math.Max(bytes, capacity * 3 / 2);
                size = (size + 15) / 16 * 16;
                var desc = new D3D11_BUFFER_DESC
                {
                    ByteWidth = checked((uint)size), Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC, BindFlags = bind, CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
                };
                ID3D11Buffer* made;
                D3d11Context.Check(Dev->lpVtbl->CreateBuffer(Dev, &desc, null, &made), $"CreateBuffer ({size} bytes, {bind})");
                // Released while draws recorded before may still read it: "Direct3D 11 defers
                // the destruction of objects" (ID3D11DeviceContext::Flush remarks).
                Com.Release(ref buffer);
                buffer = made;
                capacity = size;
                ForgetInputs();
            }
            D3D11_MAPPED_SUBRESOURCE mapped;
            D3d11Context.Check(Ctx->lpVtbl->Map(Ctx, (ID3D11Resource*)buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map (WRITE_DISCARD)");
            return (byte*)mapped.pData;
        }

        /// A channel's first `count` values (a mesh setter's array can be longer: MeshNative).
        static ReadOnlySpan<byte> AsBytes(Array a, int count) => a switch
        {
            Vector3[] v => MemoryMarshal.AsBytes(v.AsSpan(0, Math.Min(count, v.Length))),
            Vector4[] v => MemoryMarshal.AsBytes(v.AsSpan(0, Math.Min(count, v.Length))),
            Color[] v => MemoryMarshal.AsBytes(v.AsSpan(0, Math.Min(count, v.Length))),
            Color32[] v => MemoryMarshal.AsBytes(v.AsSpan(0, Math.Min(count, v.Length))),
            _ => throw new NotSupportedException(a.GetType().Name),
        };

        // ----- Releases (device thread) -----

        void ReleaseTexture(DeviceTexture g)
        {
            var t = g.D3d;
            if (t == null) return;
            g.D3d = null;
            liveTextures.Remove(t);
            Unbind(t);
            t.Release();
        }

        void ReleaseMesh(DeviceMesh d)
        {
            var m = d.D3d;
            if (m == null) return;
            d.D3d = null;
            liveMeshes.Remove(m);
            m.Release();
            ForgetInputs();
        }

        void ReleasePrograms(DeviceProgramSet set)
        {
            foreach (var p in set.Programs.Values)
                if (p.D3d != null)
                {
                    if (boundShaders == p.D3d) boundShaders = null;
                    livePrograms.Remove(p.D3d);
                    p.D3d.Release();
                    p.D3d = null;
                }
            set.Programs.Clear();
        }

        void ReleaseShared()
        {
            foreach (var r in pendingReadbacks)
            {
                Com.Release(ref r.Staging);
                r.Fail();
            }
            pendingReadbacks.Clear();
            foreach (var t in liveTextures) t.Release();
            liveTextures.Clear();
            foreach (var m in liveMeshes) m.Release();
            liveMeshes.Clear();
            foreach (var p in livePrograms) p.Release();
            livePrograms.Clear();
            foreach (var t in defaults.Values) t.D3d?.Release();
            defaults.Clear();
            foreach (var s in samplers.Values)
            {
                var state = (ID3D11SamplerState*)s;
                Com.Release(ref state);
            }
            samplers.Clear();
            defaultSampler = null;
            Com.Release(ref constants);
            states.Release();
            Com.Release(ref immediateVertices);
            Com.Release(ref immediateIndices);
            ReleaseConstantBuffers();
            ReleaseBackDepth();
        }
    }
}
