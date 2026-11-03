using System;
using System.Collections.Generic;
using OniMoono.Engine.Rendering;
using Brutal.OpenGlApi;
using UnityEngine;
using N = Brutal.OpenGlApi.OpenGl.Native;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Gl
{
    /// Vertex channels in shader input naming order: in_POSITION0, in_NORMAL0, in_TANGENT0,
    /// in_COLOR0, in_TEXCOORD0…7.
    internal enum Channel
    {
        Position, Normal, Tangent, Color, Uv0, Uv1, Uv2, Uv3, Uv4, Uv5, Uv6, Uv7,
        Count,
    }

    /// The vertex channels a mesh provides: components per channel (0: absent; negative: UVs
    /// stored as Vector4 whatever their dimension) and whether colours are bytes. Replaced, not
    /// changed, when a mesh's channels change; vertex arrays are built per layout.
    internal sealed class MeshLayout
    {
        public readonly int[] Components = new int[(int)Channel.Count];
        /// Colours given as Color32 are unsigned bytes, normalized by the shader input.
        public bool ByteColors;

        public bool SameAs(MeshLayout o) => o != null && ByteColors == o.ByteColors && Components.AsSpan().SequenceEqual(o.Components);
    }

    /// A mesh on the device: one buffer per channel, one index buffer holding every submesh.
    /// The buffers belong to the GL thread; the main thread records versions, layout and
    /// submesh ranges.
    internal sealed class DeviceMesh
    {
        // Main thread.
        public int RecordedVersion = -1;
        public MeshLayout Layout = new();
        public (int Offset, int Count, uint Mode, int BaseVertex)[] SubMeshes = Array.Empty<(int, int, uint, int)>();

        // GL thread.
        public readonly uint[] Buffers = new uint[(int)Channel.Count];
        /// Allocated sizes in bytes; uploads that fit reuse the storage (glBufferSubData).
        public readonly long[] Capacities = new long[(int)Channel.Count];
        public long IndexCapacity;
        public uint IndexBuffer;
        /// Vertex arrays by program attribute layout, with the mesh layout they were built for.
        public Dictionary<int, (uint VertexArray, MeshLayout Layout)> VertexArrays;

        public void Delete()
        {
            if (VertexArrays != null)
            {
                foreach (var (vao, _) in VertexArrays.Values) GlBindings.DeleteVertexArray(vao);
                VertexArrays = null;
            }
            for (int i = 0; i < Buffers.Length; i++)
                if (Buffers[i] != 0)
                {
                    GlBindings.DeleteBuffer(Buffers[i]);
                    Buffers[i] = 0;
                    Capacities[i] = 0;
                }
            if (IndexBuffer != 0) GlBindings.DeleteBuffer(IndexBuffer);
            IndexBuffer = 0;
            IndexCapacity = 0;
        }
    }

    /// A mesh's vertex and index data as it was when recorded, for the GL thread. The channel
    /// arrays are the mesh's own: MeshNative never writes into a channel array it has published
    /// (a change replaces the array), so they hold what was recorded until the upload runs.
    internal sealed class MeshUpload
    {
        public readonly Array[] Channels = new Array[(int)Channel.Count];
        public int[] Indices;
        public uint Usage;
    }

    internal static unsafe class GlMeshes
    {
        /// The device mesh (main thread), with changed data recorded for the GL thread.
        public static DeviceMesh Prepare(GlWorker worker, MeshNative m)
        {
            if (m.Gpu is not DeviceMesh d)
            {
                d = new DeviceMesh();
                m.Gpu = d;
                GraphicsDevice.Adopt(m, d);
            }
            if (d.RecordedVersion == m.Version) return d;
            var upload = new MeshUpload { Usage = m.Dynamic ? DynamicDraw : StaticDraw };
            var layout = new MeshLayout();
            AddChannel(upload, layout, Channel.Position, m.Positions, 3);
            AddChannel(upload, layout, Channel.Normal, m.Normals, 3);
            AddChannel(upload, layout, Channel.Tangent, m.Tangents, 4);
            if (m.ColorsAreBytes && m.Colors32 is { Length: > 0 } given)
            {
                upload.Channels[(int)Channel.Color] = given;
                layout.Components[(int)Channel.Color] = 4;
                layout.ByteColors = true;
            }
            else if (m.Colors != null && m.Colors.Length > 0 && m.ColorsAreBytes)
            {
                var bytes = new Color32[m.Colors.Length];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = m.Colors[i];
                upload.Channels[(int)Channel.Color] = bytes;
                layout.Components[(int)Channel.Color] = 4;
                layout.ByteColors = true;
            }
            else AddChannel(upload, layout, Channel.Color, m.Colors, 4);
            for (int u = 0; u < MeshNative.UvChannels; u++)
            {
                var uv = m.Uvs[u];
                // UVs are stored as Vector4 whatever their dimension.
                AddChannel(upload, layout, Channel.Uv0 + u, uv, uv == null ? 0 : -Math.Max(1, m.UvDimensions[u]));
            }
            d.SubMeshes = Indices(m, out upload.Indices);
            if (!layout.SameAs(d.Layout)) d.Layout = layout;
            d.RecordedVersion = m.Version;
            PostUpload(worker, d, upload);
            return d;
        }

        /// The mesh's device data is recorded as it is now: Prepare would record nothing.
        public static bool IsCurrent(MeshNative m) => m.Gpu is DeviceMesh d && d.RecordedVersion == m.Version;

        // A StateCommand from the frame's arena: no closure or delegate per changed mesh.
        static void PostUpload(GlWorker worker, DeviceMesh d, MeshUpload upload) => worker.Post(worker.Arena.Command(runUpload, (d, upload)));

        static readonly System.Action<(DeviceMesh Mesh, MeshUpload Upload)> runUpload = RunUpload;

        static void RunUpload((DeviceMesh Mesh, MeshUpload Upload) recorded)
        {
            var (d, upload) = recorded;
            long t = GlTimings.Now;
            uint q = GlGpuProfile.Enabled ? GlGpuProfile.Begin() : 0;
            Upload(d, upload);
            if (GlGpuProfile.Enabled) GlGpuProfile.End("mesh upload", q);
            GlTimings.MeshUploads += GlTimings.Now - t;
        }

        static void AddChannel<T>(MeshUpload upload, MeshLayout layout, Channel c, T[] data, int components) where T : unmanaged
        {
            if (data == null || data.Length == 0) return;
            upload.Channels[(int)c] = data;
            layout.Components[(int)c] = components;
        }

        /// Writes the recorded data into the mesh's buffers (GL thread).
        static void Upload(DeviceMesh d, MeshUpload upload)
        {
            for (int i = 0; i < upload.Channels.Length; i++)
            {
                switch (upload.Channels[i])
                {
                    case null: continue;
                    case Vector3[] v: Store(d, i, v, upload.Usage); break;
                    case Vector4[] v: Store(d, i, v, upload.Usage); break;
                    case Color[] v: Store(d, i, v, upload.Usage); break;
                    case Color32[] v: Store(d, i, v, upload.Usage); break;
                }
            }
            if (d.IndexBuffer == 0) d.IndexBuffer = GlApi.CreateBuffer();
            d.IndexCapacity = Fill(d.IndexBuffer, upload.Indices, d.IndexCapacity, upload.Usage);
        }

        static void Store<T>(DeviceMesh d, int i, T[] data, uint usage) where T : unmanaged
        {
            if (d.Buffers[i] == 0) d.Buffers[i] = GlApi.CreateBuffer();
            d.Capacities[i] = Fill(d.Buffers[i], data, d.Capacities[i], usage);
        }

        /// The mesh's vertex array for a program's attribute layout and a mesh layout (GL
        /// thread): buffers, formats and the index buffer recorded once, so a draw binds one
        /// object. Channels the mesh lacks are disabled and read the context's constant
        /// attribute values.
        public static uint VertexArray(DeviceMesh d, MeshLayout layout, DeviceProgram p)
        {
            d.VertexArrays ??= new();
            if (d.VertexArrays.TryGetValue(p.LayoutId, out var entry) && entry.Layout == layout) return entry.VertexArray;
            uint vao = entry.VertexArray;
            if (vao == 0)
            {
                GlVertexArray created = default;
                N.glCreateVertexArrays(1, &created);
                vao = created.Id;
            }
            for (int c = 0; c < (int)Channel.Count; c++)
            {
                int location = p.Attributes[c];
                if (location < 0) continue;
                uint loc = (uint)location;
                int components = layout.Components[c];
                if (components == 0)
                {
                    N.glDisableVertexArrayAttrib(vao, loc);
                    continue;
                }
                N.glEnableVertexArrayAttrib(vao, loc);
                if (c == (int)Channel.Color && layout.ByteColors)
                {
                    N.glVertexArrayVertexBuffer(vao, loc, new GlBuffer(d.Buffers[c]), 0, 4);
                    N.glVertexArrayAttribFormat(vao, loc, 4, UnsignedByte, true, 0);
                }
                else
                {
                    int size = Math.Abs(components);
                    N.glVertexArrayVertexBuffer(vao, loc, new GlBuffer(d.Buffers[c]), 0, components < 0 ? 16 : size * 4);
                    N.glVertexArrayAttribFormat(vao, loc, size, Float, false, 0);
                }
                N.glVertexArrayAttribBinding(vao, loc, loc);
            }
            N.glVertexArrayElementBuffer(vao, new GlBuffer(d.IndexBuffer));
            d.VertexArrays[p.LayoutId] = (vao, layout);
            return vao;
        }

        /// Every submesh's indices in one list; quads become two triangles each, as Unity's GL
        /// device draws them.
        static uint Mode(MeshTopology topology) => topology switch
        {
            MeshTopology.Lines => Lines,
            MeshTopology.LineStrip => LineStrip,
            MeshTopology.Points => Points,
            _ => Triangles, // quads are drawn as triangles
        };

        /// Every submesh's indices in one array (quads as two triangles each), with each
        /// submesh's range. A mesh of one submesh that needs no conversion uploads its own index
        /// array (index arrays, like the channels, are never written once published).
        static (int, int, uint, int)[] Indices(MeshNative m, out int[] indices)
        {
            var subs = new (int, int, uint, int)[m.SubMeshes.Count];
            if (subs.Length == 1 && m.SubMeshes[0].Topology != MeshTopology.Quads)
            {
                var only = m.SubMeshes[0];
                indices = only.Indices;
                subs[0] = (0, only.Indices.Length, Mode(only.Topology), only.BaseVertex);
                return subs;
            }
            int total = 0;
            foreach (var sm in m.SubMeshes)
                total += sm.Topology == MeshTopology.Quads ? sm.Indices.Length / 4 * 6 : sm.Indices.Length;
            indices = new int[total];
            int at = 0;
            for (int s = 0; s < subs.Length; s++)
            {
                var sm = m.SubMeshes[s];
                int start = at;
                if (sm.Topology == MeshTopology.Quads)
                    for (int q = 0; q + 3 < sm.Indices.Length; q += 4)
                    {
                        indices[at++] = sm.Indices[q];
                        indices[at++] = sm.Indices[q + 1];
                        indices[at++] = sm.Indices[q + 2];
                        indices[at++] = sm.Indices[q];
                        indices[at++] = sm.Indices[q + 2];
                        indices[at++] = sm.Indices[q + 3];
                    }
                else
                {
                    sm.Indices.CopyTo(indices, at);
                    at += sm.Indices.Length;
                }
                subs[s] = (start, at - start, Mode(sm.Topology), sm.BaseVertex);
            }
            return subs;
        }

        /// Writes data into a buffer (direct state access: no binding changes, so the bound
        /// vertex array keeps its index buffer): in place when it fits the current storage, else
        /// into new storage. Returns the storage size.
        static long Fill<T>(uint buffer, T[] data, long capacity, uint usage) where T : unmanaged
        {
            long bytes = (long)data.Length * sizeof(T);
            fixed (T* p = data)
            {
                if (bytes > 0 && bytes <= capacity)
                {
                    N.glNamedBufferSubData(new GlBuffer(buffer), 0, (nint)bytes, p);
                    return capacity;
                }
                N.glNamedBufferData(new GlBuffer(buffer), (nint)bytes, p, usage);
            }
            return bytes;
        }
    }
}
