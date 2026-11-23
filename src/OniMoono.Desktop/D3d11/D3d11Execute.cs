using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.D3d11
{
    /// The device thread's rendering on the immediate context. Viewport and scissor rectangles
    /// come from the bottom left (as OpenGL's); D3D11's have their origin at the top left
    /// (D3D11_VIEWPORT, D3D11_RECT), so they are turned over in the back buffer and in a
    /// ScreenRows render texture (stored top row first), and kept in a render texture, rendered
    /// upside down with its rows in OpenGL's order (as VkDevice's). Depth is reversed: cleared to
    /// 1 - the value asked for (the original's clears are depth 0 for OpenGL's 1,
    /// d3d11-captures.md §2).
    ///
    /// A texture bound for sampling is never also bound as a target: the runtime would unbind it
    /// and the debug layer report the hazard. Binding a target first unbinds the sampled views of
    /// its textures; a draw that samples the texture it renders into reads the unbound default
    /// (a feedback loop, undefined on OpenGL; as VkDevice).
    internal sealed unsafe partial class D3d11Device
    {
        const int Stages = 3;
        /// Texture and sampler slots tracked per stage (a binding past it is still made).
        const int TrackedSlots = 32;

        // The current target.
        readonly ID3D11RenderTargetView*[] targetViews = new ID3D11RenderTargetView*[8];
        int targetCount;
        ID3D11DepthStencilView* targetDepth;
        readonly HashSet<D3d11Texture> targetTextures = new();
        bool topFirst = true, flipped, hasDepth = true, hasStencil = true;
        int renderWidth = 1, renderHeight = 1;
        Rect viewport = new(0, 0, 1, 1);
        Rect? scissor;
        // The back buffer's depth surface (D32_FLOAT_S8X24, DSV only, as the original's screen
        // depth: d3d11-unity-probes.md §2).
        ID3D11Texture2D* backDepth;
        ID3D11DepthStencilView* backDepthView;
        int backDepthWidth, backDepthHeight;

        // What is bound, so unchanged bindings are not set again.
        D3d11Shaders boundShaders;
        ID3D11InputLayout* boundLayout;
        D3D11_PRIMITIVE_TOPOLOGY boundTopology;
        ID3D11RasterizerState* boundRasterizer;
        ID3D11BlendState* boundBlend;
        ID3D11DepthStencilState* boundDepth;
        uint boundStencilRef;
        readonly nint[][] boundViews = [new nint[TrackedSlots], new nint[TrackedSlots], new nint[TrackedSlots]];
        readonly D3d11Texture[][] boundViewTextures = [new D3d11Texture[TrackedSlots], new D3d11Texture[TrackedSlots], new D3d11Texture[TrackedSlots]];
        readonly nint[][] boundSamplers = [new nint[TrackedSlots], new nint[TrackedSlots], new nint[TrackedSlots]];
        readonly nint[][] boundBuffers = [new nint[16], new nint[16], new nint[16]];

        long draws, constantMaps, shaderChanges;
        bool feedbackReported;

        // ----- Targets -----

        void BackDepth(int w, int h)
        {
            if (backDepth != null && backDepthWidth == w && backDepthHeight == h) return;
            ReleaseBackDepth();
            var t = new D3d11Texture { Width = w, Height = h };
            MakeDepth(t, 24, 1, shaderView: false);
            backDepth = t.DepthStencil;
            backDepthView = t.Dsv;
            backDepthWidth = w;
            backDepthHeight = h;
        }

        void ReleaseBackDepth()
        {
            Com.Release(ref backDepthView);
            Com.Release(ref backDepth);
        }

        BoundTarget lastTarget = new(null, null, 0, false);

        void SetTarget(BoundTarget t)
        {
            lastTarget = t;
            targetTextures.Clear();
            for (int i = 0; i < targetViews.Length; i++) targetViews[i] = null;
            targetCount = 0;
            targetDepth = null;
            int width = int.MaxValue, height = int.MaxValue;
            if (t.Colors == null)
            {
                topFirst = true;
                targetViews[0] = chain.View;
                targetCount = 1;
                BackDepth(chain.Width, chain.Height);
                targetDepth = backDepthView;
                hasDepth = hasStencil = true;
                (width, height) = (chain.Width, chain.Height);
            }
            else
            {
                topFirst = t.ScreenRows;
                var depthTexture = t.Colors.Length == 1 && (t.Depth == null || t.Depth == t.Colors[0]) ? t.Colors[0]?.D3d : t.Depth?.D3d;
                bool depthOnly = t.Colors.Length == 1 && t.Colors[0]?.D3d is { DepthOnly: true };
                if (!depthOnly)
                    for (int i = 0; i < t.Colors.Length && i < 8; i++)
                    {
                        var c = t.Colors[i]?.D3d;
                        targetCount = i + 1;
                        if (c?.Resource == null || c.DepthOnly) continue;
                        int level = Math.Clamp(t.Level, 0, c.Levels - 1);
                        targetViews[i] = c.MultisampledRtv != null ? c.MultisampledRtv : Rtv(c, level);
                        targetTextures.Add(c);
                        width = Math.Min(width, Math.Max(1, c.Width >> level));
                        height = Math.Min(height, Math.Max(1, c.Height >> level));
                    }
                hasDepth = hasStencil = false;
                if (depthTexture?.Dsv != null)
                {
                    targetDepth = depthTexture.Dsv;
                    targetTextures.Add(depthTexture);
                    hasDepth = true;
                    hasStencil = depthTexture.HasStencil;
                    width = Math.Min(width, depthTexture.Width);
                    height = Math.Min(height, depthTexture.Height);
                }
            }
            if (width == int.MaxValue) (width, height) = (1, 1);
            renderWidth = width;
            renderHeight = height;
            flipped = !topFirst;
            // The sampled views of the new target's textures go first (no hazard).
            for (int s = 0; s < Stages; s++)
                for (int slot = 0; slot < TrackedSlots; slot++)
                    if (boundViewTextures[s][slot] is { } bound && targetTextures.Contains(bound)) SetView(s, slot, null, null);
            fixed (ID3D11RenderTargetView** views = targetViews)
                Ctx->lpVtbl->OMSetRenderTargets(Ctx, (uint)targetCount, views, targetDepth);
            ApplyViewport();
            ApplyScissor();
        }

        /// A texture whose storage goes is unbound from every stage and from the target; true
        /// when it was the target's (set again once it is made again).
        bool Unbind(D3d11Texture t)
        {
            for (int s = 0; s < Stages; s++)
                for (int slot = 0; slot < TrackedSlots; slot++)
                    if (boundViewTextures[s][slot] == t) SetView(s, slot, null, null);
            if (targetTextures.Remove(t))
            {
                // Its views go with it: the target is set again without them.
                for (int i = 0; i < targetViews.Length; i++) targetViews[i] = null;
                targetDepth = null;
                targetCount = 0;
                Ctx->lpVtbl->OMSetRenderTargets(Ctx, 0, null, null);
                targetTextures.Clear();
                hasDepth = hasStencil = false;
                return true;
            }
            return false;
        }

        void SetViewportNow(Rect pixels)
        {
            viewport = pixels;
            ApplyViewport();
        }

        /// The viewport (from the bottom left): the same rectangle in a render texture, turned
        /// over in the back buffer. Depth 0..1, as the original's.
        void ApplyViewport()
        {
            int h = Math.Max(1, (int)viewport.height);
            var v = new D3D11_VIEWPORT
            {
                TopLeftX = (int)viewport.x, TopLeftY = topFirst ? renderHeight - (int)viewport.y - h : (int)viewport.y,
                Width = Math.Max(1, (int)viewport.width), Height = h, MinDepth = 0f, MaxDepth = 1f,
            };
            Ctx->lpVtbl->RSSetViewports(Ctx, 1, &v);
        }

        bool ViewportEmpty => (int)viewport.width <= 0 || (int)viewport.height <= 0;

        void SetScissorNow(Rect? pixels)
        {
            scissor = pixels;
            ApplyScissor();
        }

        void ApplyScissor()
        {
            if (scissor is not { } s) return;
            int x0 = Math.Clamp((int)s.x, 0, renderWidth), y0 = Math.Clamp((int)s.y, 0, renderHeight);
            int x1 = Math.Clamp((int)s.x + Math.Max(0, (int)s.width), 0, renderWidth), y1 = Math.Clamp((int)s.y + Math.Max(0, (int)s.height), 0, renderHeight);
            if (topFirst) (y0, y1) = (renderHeight - y1, renderHeight - y0);
            var r = new D3D11_RECT { left = x0, top = y0, right = x1, bottom = y1 };
            Ctx->lpVtbl->RSSetScissorRects(Ctx, 1, &r);
        }

        /// Clears fill the whole target, whatever the scissor (as GlDevice.Clear):
        /// ClearRenderTargetView and ClearDepthStencilView take no rectangle.
        void ClearNow(bool color, bool depth, Color background, float depthValue)
        {
            if (lost != null) return;
            if (color)
            {
                var rgba = stackalloc float[] { background.r, background.g, background.b, background.a };
                for (int i = 0; i < targetCount; i++)
                    if (targetViews[i] != null) Ctx->lpVtbl->ClearRenderTargetView(Ctx, targetViews[i], rgba);
            }
            if (depth && targetDepth != null)
                Ctx->lpVtbl->ClearDepthStencilView(Ctx, targetDepth, D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH | (hasStencil ? D3D11_CLEAR_FLAG.D3D11_CLEAR_STENCIL : 0), 1f - depthValue, 0);
            MarkDrawn();
        }

        void MarkDrawn()
        {
            foreach (var t in targetTextures) t.Dirty = true;
        }

        // ----- Draws -----

        void RunDraw(ProgramInputs r)
        {
            var mesh = r.Mesh?.D3d;
            if (lost != null || r.Program.D3d is not { } program || mesh?.Vertices == null || r.SubMesh.Count <= 0 || ViewportEmpty) return;
            var layout = Layout(program, Vulkan.VkPipelines.LayoutKey(r.Layout), r.Layout);
            if (layout == null) return;
            Bind(r, program, layout, Topology(r.SubMesh.Mode));
            // Slots 0-11 the mesh's channels, each at its offset in the one buffer; the slots
            // after them the constants (D3d11Layout.Constants).
            int n = (int)Channel.Count + layout.Constants.Length;
            var buffers = stackalloc ID3D11Buffer*[n];
            var strides = stackalloc uint[n];
            var offsets = stackalloc uint[n];
            for (int c = 0; c < (int)Channel.Count; c++)
            {
                bool present = r.Layout.Components[c] != 0 && mesh.ChannelOffsets[c] >= 0;
                buffers[c] = present ? mesh.Vertices : null;
                strides[c] = present ? (uint)ChannelFormat(r.Layout, c).Stride : 0;
                offsets[c] = present ? (uint)mesh.ChannelOffsets[c] : 0;
            }
            for (int i = 0; i < layout.Constants.Length; i++)
            {
                buffers[(int)Channel.Count + i] = constants;
                strides[(int)Channel.Count + i] = 16;
                offsets[(int)Channel.Count + i] = (uint)layout.Constants[i];
            }
            Ctx->lpVtbl->IASetVertexBuffers(Ctx, 0, (uint)n, buffers, strides, offsets);
            Ctx->lpVtbl->IASetIndexBuffer(Ctx, mesh.Indices, DXGI_FORMAT.DXGI_FORMAT_R32_UINT, 0);
            Ctx->lpVtbl->DrawIndexed(Ctx, (uint)r.SubMesh.Count, (uint)r.SubMesh.Offset, r.SubMesh.BaseVertex);
            draws++;
            MarkDrawn();
        }

        ID3D11Buffer* immediateVertices, immediateIndices;
        long immediateVertexCapacity, immediateIndexCapacity;

        void ExecuteImmediate(ProgramInputs recorded, ImmediatePrimitive primitive, float[] data, int count, int[] indices)
        {
            if (lost != null || recorded.Program.D3d is not { } program || ViewportEmpty) return;
            var layout = Layout(program, Vulkan.VkPipelines.ImmediateLayout, null);
            if (layout == null) return;
            var topology = indices != null ? D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST : primitive switch
            {
                ImmediatePrimitive.Lines => D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_LINELIST,
                ImmediatePrimitive.LineStrip => D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_LINESTRIP,
                ImmediatePrimitive.TriangleStrip => D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP,
                _ => D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
            };
            Bind(recorded, program, layout, topology);
            var bytes = MemoryMarshal.AsBytes(data.AsSpan());
            var into = Write(ref immediateVertices, ref immediateVertexCapacity, bytes.Length, D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER);
            bytes.CopyTo(new Span<byte>(into, bytes.Length));
            Ctx->lpVtbl->Unmap(Ctx, (ID3D11Resource*)immediateVertices, 0);
            int n = 1 + layout.Constants.Length;
            var buffers = stackalloc ID3D11Buffer*[n];
            var strides = stackalloc uint[n];
            var offsets = stackalloc uint[n];
            buffers[0] = immediateVertices;
            strides[0] = Vulkan.VkPipelines.ImmediateStride;
            offsets[0] = 0;
            for (int i = 0; i < layout.Constants.Length; i++)
            {
                buffers[1 + i] = constants;
                strides[1 + i] = 16;
                offsets[1 + i] = (uint)layout.Constants[i];
            }
            Ctx->lpVtbl->IASetVertexBuffers(Ctx, 0, (uint)n, buffers, strides, offsets);
            if (indices != null)
            {
                var ib = MemoryMarshal.AsBytes(indices.AsSpan());
                var at = Write(ref immediateIndices, ref immediateIndexCapacity, ib.Length, D3D11_BIND_FLAG.D3D11_BIND_INDEX_BUFFER);
                ib.CopyTo(new Span<byte>(at, ib.Length));
                Ctx->lpVtbl->Unmap(Ctx, (ID3D11Resource*)immediateIndices, 0);
                Ctx->lpVtbl->IASetIndexBuffer(Ctx, immediateIndices, DXGI_FORMAT.DXGI_FORMAT_R32_UINT, 0);
                Ctx->lpVtbl->DrawIndexed(Ctx, (uint)indices.Length, 0, 0);
            }
            else Ctx->lpVtbl->Draw(Ctx, (uint)count, 0);
            draws++;
            MarkDrawn();
        }

        static D3D11_PRIMITIVE_TOPOLOGY Topology(uint mode) => mode switch
        {
            Lines => D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_LINELIST,
            LineStrip => D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_LINESTRIP,
            Points => D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_POINTLIST,
            TriangleStrip => D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP,
            _ => D3D11_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
        };

        /// A channel's input format and stride as the mesh stores it (VkPipelines.ChannelFormat):
        /// byte colours as R8G8B8A8_UNORM, floats by their count, UVs kept as Vector4 16 bytes apart.
        static (DXGI_FORMAT Format, int Stride) ChannelFormat(MeshLayout layout, int c)
        {
            if (c == (int)Channel.Color && layout.ByteColors) return (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, 4);
            int components = layout.Components[c], size = Math.Abs(components);
            var format = size switch
            {
                1 => DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT, 2 => DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, 3 => DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
                _ => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
            };
            return (format, components < 0 ? 16 : size * 4);
        }

        /// The constant an input no channel feeds reads, as OpenGL's constant attributes
        /// (VkDevice.ConstantOffset): white colour, position (0, 0, 0, 1), zero normal, tangent
        /// and texture coordinates; inputs no channel names (0, 0, 0, 1), as integers for an
        /// integer input. Offsets into the constants buffer (CreateShared).
        static int ConstantOffset(int channel, uint componentType) => componentType is 1 or 2
            ? (channel < 0 ? 48 : 0)
            : channel switch
            {
                (int)Channel.Color => 16,
                (int)Channel.Position => 32,
                < 0 => 32,
                _ => 0,
            };

        static readonly Dictionary<(string, uint), nint> semanticNames = new();

        /// A semantic name as the null-terminated ANSI string D3D11_INPUT_ELEMENT_DESC points to
        /// (kept for the device's life).
        static sbyte* SemanticName(string name)
        {
            var key = (name, 0u);
            if (!semanticNames.TryGetValue(key, out var p)) semanticNames[key] = p = Marshal.StringToHGlobalAnsi(name);
            return (sbyte*)p;
        }

        /// The program's input layout for a mesh layout (made on first use): an element for each
        /// input of the vertex stage's signature, as CreateInputLayout validates the elements
        /// against it. A channel the mesh has: slot = the channel, its format, per vertex. One it
        /// lacks: a slot of its own after the channels', per instance with InstanceDataStepRate 0,
        /// which "takes special meaning, that the instance data should never be stepped at all"
        /// (Direct3D 11.3 Functional Specification §8.1.3, Input Layout), reading the constant
        /// (ConstantOffset). Components a format lacks read 0, and alpha 1 (§19.1.3.3, Defaults
        /// for Missing Components), as OpenGL's. The original always declares 14 elements
        /// (d3d11-captures.md §3), what its extra ones read is UNVERIFIED there (7); this layout
        /// names only what the program reads. Null when the layout cannot be made (logged).
        D3d11Layout Layout(D3d11Shaders program, long key, MeshLayout layout)
        {
            if (program.Layouts.TryGetValue(key, out var known)) return known.Layout == null ? null : known;
            var made = new D3d11Layout();
            var inputs = program.Inputs;
            var elements = stackalloc D3D11_INPUT_ELEMENT_DESC[Math.Max(1, inputs.Length)];
            var constantOffsets = new List<int>();
            bool immediate = key == Vulkan.VkPipelines.ImmediateLayout;
            for (int i = 0; i < inputs.Length; i++)
            {
                var input = inputs[i];
                int c = input.Channel;
                bool fed = immediate ? c >= 0 && c != (int)Channel.Normal && c != (int)Channel.Tangent : c >= 0 && layout.Components[c] != 0;
                var e = new D3D11_INPUT_ELEMENT_DESC { SemanticName = SemanticName(input.Semantic), SemanticIndex = input.Index };
                if (fed && immediate)
                {
                    e.Format = c == (int)Channel.Position ? DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT : DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT;
                    e.InputSlot = 0;
                    e.AlignedByteOffset = (uint)(c == (int)Channel.Position ? 0 : c == (int)Channel.Color ? 12 : 28 + (c - (int)Channel.Uv0) * 16);
                }
                else if (fed)
                {
                    e.Format = ChannelFormat(layout, c).Format;
                    e.InputSlot = (uint)c;
                }
                else
                {
                    e.Format = input.ComponentType switch
                    {
                        1 => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_UINT,
                        2 => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_SINT,
                        _ => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
                    };
                    e.InputSlot = (uint)((immediate ? 1 : (int)Channel.Count) + constantOffsets.Count);
                    e.InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_INSTANCE_DATA;
                    e.InstanceDataStepRate = 0;
                    constantOffsets.Add(ConstantOffset(c, input.ComponentType));
                }
                elements[i] = e;
            }
            made.Constants = constantOffsets.ToArray();
            try
            {
                ID3D11InputLayout* l;
                fixed (byte* code = program.VertexBytecode)
                    D3d11Context.Check(Dev->lpVtbl->CreateInputLayout(Dev, elements, (uint)inputs.Length, code, (nuint)program.VertexBytecode.Length, &l),
                        $"CreateInputLayout ({program.Name})");
                made.Layout = l;
            }
            catch (D3d11UnavailableException e)
            {
                EngineLog.Engine($"d3d11: shader '{program.Name}': {e.Message}; its draws with that mesh layout are left out");
            }
            program.Layouts[key] = made;
            return made.Layout == null ? null : made;
        }

        /// Shaders, input layout, topology, the state objects, constant buffers, textures and
        /// samplers for a draw.
        void Bind(ProgramInputs r, D3d11Shaders program, D3d11Layout layout, D3D11_PRIMITIVE_TOPOLOGY topology)
        {
            if (boundShaders != program)
            {
                Ctx->lpVtbl->VSSetShader(Ctx, program.Vertex, null, 0);
                Ctx->lpVtbl->PSSetShader(Ctx, program.Pixel, null, 0);
                if (boundShaders?.Geometry != null || program.Geometry != null) Ctx->lpVtbl->GSSetShader(Ctx, program.Geometry, null, 0);
                boundShaders = program;
                shaderChanges++;
            }
            if (boundLayout != layout.Layout)
            {
                Ctx->lpVtbl->IASetInputLayout(Ctx, layout.Layout);
                boundLayout = layout.Layout;
            }
            if (boundTopology != topology)
            {
                Ctx->lpVtbl->IASetPrimitiveTopology(Ctx, topology);
                boundTopology = topology;
            }
            var rasterizer = states.Rasterizer(r.State, flipped, scissor != null);
            if (boundRasterizer != rasterizer)
            {
                Ctx->lpVtbl->RSSetState(Ctx, rasterizer);
                boundRasterizer = rasterizer;
            }
            var blend = states.Blend(r.State);
            if (boundBlend != blend)
            {
                // No blend factor is used (no BLEND_FACTOR blend), and every sample is written.
                Ctx->lpVtbl->OMSetBlendState(Ctx, blend, null, D3D11.D3D11_DEFAULT_SAMPLE_MASK);
                boundBlend = blend;
            }
            var depth = states.DepthStencil(r.State, hasDepth, hasStencil);
            if (boundDepth != depth || boundStencilRef != (uint)r.State.StencilRef)
            {
                Ctx->lpVtbl->OMSetDepthStencilState(Ctx, depth, (uint)r.State.StencilRef);
                boundDepth = depth;
                boundStencilRef = (uint)r.State.StencilRef;
            }
            var starts = stackalloc int[Math.Max(1, r.Program.BlockSizes.Length)];
            for (int b = 0, at = r.BlocksOffset; b < r.Program.BlockSizes.Length; b++)
            {
                starts[b] = at;
                at += Math.Max(r.Program.BlockSizes[b], 16);
            }
            for (int s = 0; s < Stages; s++)
            {
                if (program.Stages[s] is not { } stage) continue;
                foreach (var (slot, block, size) in stage.ConstantBuffers) BindConstants(s, slot, size, r.Blocks.AsSpan(starts[block], size));
                foreach (var (slot, index) in stage.Textures)
                {
                    var tex = SampledTexture(r, index);
                    SetView(s, slot, tex.Srv, tex);
                }
                foreach (var (slot, index) in stage.Samplers)
                {
                    var tex = SampledTexture(r, index);
                    SetSampler(s, slot, tex.Sampler != null ? tex.Sampler : defaultSampler);
                }
            }
        }

        /// The texture a sampler reads: render textures made ready first; the unbound default for
        /// one with no storage, or one of the current target.
        D3d11Texture SampledTexture(ProgramInputs r, int index)
        {
            var sampled = r.Textures[index];
            var t = sampled.Texture?.D3d;
            if (t?.Srv != null && targetTextures.Contains(t))
            {
                if (!feedbackReported)
                {
                    feedbackReported = true;
                    EngineLog.Engine("d3d11: a draw samples the texture it renders into; it reads no texture there (undefined on OpenGL)");
                }
                t = null;
            }
            if (t?.Srv == null) return DefaultTexture(DefaultTextures.Unbound, r.Program.Samplers[index].Target).D3d;
            if (sampled.RenderTexture) PrepareForSampling(t, sampled.AutoGenerateMips);
            return t;
        }

        void SetView(int stage, int slot, ID3D11ShaderResourceView* view, D3d11Texture texture)
        {
            if (slot < TrackedSlots)
            {
                if (boundViews[stage][slot] == (nint)view) return;
                boundViews[stage][slot] = (nint)view;
                boundViewTextures[stage][slot] = texture;
            }
            switch (stage)
            {
                case 0: Ctx->lpVtbl->VSSetShaderResources(Ctx, (uint)slot, 1, &view); break;
                case 1: Ctx->lpVtbl->PSSetShaderResources(Ctx, (uint)slot, 1, &view); break;
                default: Ctx->lpVtbl->GSSetShaderResources(Ctx, (uint)slot, 1, &view); break;
            }
        }

        void SetSampler(int stage, int slot, ID3D11SamplerState* sampler)
        {
            if (slot < TrackedSlots)
            {
                if (boundSamplers[stage][slot] == (nint)sampler) return;
                boundSamplers[stage][slot] = (nint)sampler;
            }
            switch (stage)
            {
                case 0: Ctx->lpVtbl->VSSetSamplers(Ctx, (uint)slot, 1, &sampler); break;
                case 1: Ctx->lpVtbl->PSSetSamplers(Ctx, (uint)slot, 1, &sampler); break;
                default: Ctx->lpVtbl->GSSetSamplers(Ctx, (uint)slot, 1, &sampler); break;
            }
        }

        /// One DYNAMIC constant buffer per stage, slot and size, of the cbuffer's size, bound whole
        /// and written with Map(WRITE_DISCARD) when the draw's bytes differ from what it holds
        /// (the original's way: d3d11-captures.md §3 and §5, one buffer of the cbuffer's exact size
        /// per slot, WRITE_DISCARD before the draws that use it).
        sealed class ConstantBuffer
        {
            public ID3D11Buffer* Buffer;
            public byte[] Held;
        }

        readonly Dictionary<(int Stage, int Slot, int Size), ConstantBuffer> constantBuffers = new();

        void BindConstants(int stage, int slot, int size, ReadOnlySpan<byte> bytes)
        {
            if (!constantBuffers.TryGetValue((stage, slot, size), out var cb))
            {
                var desc = new D3D11_BUFFER_DESC
                {
                    ByteWidth = (uint)size, Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                    CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
                };
                ID3D11Buffer* b;
                D3d11Context.Check(Dev->lpVtbl->CreateBuffer(Dev, &desc, null, &b), $"CreateBuffer (constant buffer of {size} bytes)");
                cb = new ConstantBuffer { Buffer = b };
                constantBuffers[(stage, slot, size)] = cb;
            }
            if (cb.Held == null || !bytes.SequenceEqual(cb.Held))
            {
                D3D11_MAPPED_SUBRESOURCE mapped;
                D3d11Context.Check(Ctx->lpVtbl->Map(Ctx, (ID3D11Resource*)cb.Buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map (constant buffer)");
                bytes.CopyTo(new Span<byte>(mapped.pData, size));
                Ctx->lpVtbl->Unmap(Ctx, (ID3D11Resource*)cb.Buffer, 0);
                cb.Held ??= new byte[size];
                bytes.CopyTo(cb.Held);
                constantMaps++;
            }
            if (slot < boundBuffers[stage].Length && boundBuffers[stage][slot] == (nint)cb.Buffer) return;
            if (slot < boundBuffers[stage].Length) boundBuffers[stage][slot] = (nint)cb.Buffer;
            var buffer = cb.Buffer;
            switch (stage)
            {
                case 0: Ctx->lpVtbl->VSSetConstantBuffers(Ctx, (uint)slot, 1, &buffer); break;
                case 1: Ctx->lpVtbl->PSSetConstantBuffers(Ctx, (uint)slot, 1, &buffer); break;
                default: Ctx->lpVtbl->GSSetConstantBuffers(Ctx, (uint)slot, 1, &buffer); break;
            }
        }

        void ReleaseConstantBuffers()
        {
            foreach (var cb in constantBuffers.Values) Com.Release(ref cb.Buffer);
            constantBuffers.Clear();
        }

        // ----- Read-back and present -----

        /// Pixels of a render texture (or the back buffer) as OpenGL's ReadPixels(RGBA, FLOAT)
        /// gives them, rows from the bottom; texels outside the image read 0. The rectangle is
        /// copied into a STAGING texture (CopySubresourceRegion) and mapped for reading; Map waits
        /// for the copy (without D3D11_MAP_FLAG_DO_NOT_WAIT, which would return
        /// DXGI_ERROR_WAS_STILL_DRAWING instead: D3D11_MAP_FLAG).
        float[] ReadNow(DeviceTexture device, bool autoMips, int x, int y, int w, int h)
        {
            var read = new float[w * h * 4];
            if (lost != null) return read;
            ID3D11Resource* source;
            DXGI_FORMAT format;
            int width, height;
            bool turned;
            if (device != null)
            {
                var t = device.D3d;
                if (t?.Resource == null || t.DepthOnly) return read;
                PrepareForSampling(t, autoMips);
                source = t.Resource;
                (format, width, height, turned) = (t.Format, t.Width, t.Height, false);
            }
            else
            {
                source = (ID3D11Resource*)chain.Buffer;
                (format, width, height, turned) = (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, chain.Width, chain.Height, true);
            }
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(width, x + w), y1 = Math.Min(height, y + h);
            if (x1 <= x0 || y1 <= y0) return read;
            int rw = x1 - x0, rh = y1 - y0, texel = D3d11Formats.TexelBytes(format);
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)rw, Height = (uint)rh, MipLevels = 1, ArraySize = 1, Format = format, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            };
            ID3D11Texture2D* staging;
            D3d11Context.Check(Dev->lpVtbl->CreateTexture2D(Dev, &desc, null, &staging), "CreateTexture2D (readback)");
            try
            {
                // The back buffer is stored top row first: its rows are read turned over (a
                // render texture's as stored, as VkDevice.ReadNow).
                var box = new D3D11_BOX { left = (uint)x0, right = (uint)x1, top = (uint)(turned ? height - y1 : y0), bottom = (uint)(turned ? height - y0 : y1), front = 0, back = 1 };
                Ctx->lpVtbl->CopySubresourceRegion(Ctx, (ID3D11Resource*)staging, 0, 0, 0, 0, source, 0, &box);
                D3D11_MAPPED_SUBRESOURCE mapped;
                D3d11Context.Check(Ctx->lpVtbl->Map(Ctx, (ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map (readback)");
                try
                {
                    for (int row = 0; row < rh; row++)
                        for (int col = 0; col < rw; col++)
                        {
                            var c = D3d11Formats.ReadTexel(format, (byte*)mapped.pData + (long)row * mapped.RowPitch + (long)col * texel);
                            int glRow = turned ? y1 - 1 - row : y0 + row;
                            int o = ((glRow - y) * w + (x0 - x + col)) * 4;
                            read[o] = c.r;
                            read[o + 1] = c.g;
                            read[o + 2] = c.b;
                            read[o + 3] = c.a;
                        }
                }
                finally
                {
                    Ctx->lpVtbl->Unmap(Ctx, (ID3D11Resource*)staging, 0);
                }
            }
            finally
            {
                Com.Release(ref staging);
            }
            return read;
        }

        void PresentNow(int interval, int width, int height)
        {
            if (lost != null) return;
            int hr = chain.Present(interval);
            DrainDebugMessages();
            if (hr < 0)
            {
                // Present reports a removed or reset device (DXGI_ERROR_DEVICE_REMOVED/RESET); the
                // reason is asked of the device ("Handle device removed scenarios in Direct3D 11").
                lost = Dxgi.DeviceLost(hr)
                    ? $"the graphics device was lost ({Dxgi.Hr(hr)}, reason {Dxgi.Hr(Dev->lpVtbl->GetDeviceRemovedReason(Dev))})"
                    : $"Present failed ({Dxgi.Hr(hr)})";
                EngineLog.Engine($"d3d11: {lost}");
                return;
            }
            try
            {
                if (width != chain.Width || height != chain.Height)
                {
                    // ResizeBuffers needs every reference to the buffers released, the context's
                    // too: Resize clears the context's state, so nothing is bound any more.
                    chain.Resize(width, height);
                    ForgetBindings();
                }
            }
            catch (D3d11UnavailableException e)
            {
                lost = e.Message;
                EngineLog.Engine($"d3d11: {lost}");
                return;
            }
            // The flip model unbinds the back buffer at Present ("For best performance, use DXGI
            // flip model"): the target is set again, as the next frame finds it on OpenGL.
            SetTarget(lastTarget);
        }

        /// After ClearState: every binding at its default, the target the back buffer.
        void ForgetBindings()
        {
            boundShaders = null;
            boundLayout = null;
            boundTopology = 0;
            boundRasterizer = null;
            boundBlend = null;
            boundDepth = null;
            boundStencilRef = 0;
            for (int s = 0; s < Stages; s++)
            {
                Array.Clear(boundViews[s]);
                Array.Clear(boundViewTextures[s]);
                Array.Clear(boundSamplers[s]);
                Array.Clear(boundBuffers[s]);
            }
        }
    }
}
