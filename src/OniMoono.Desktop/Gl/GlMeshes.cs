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

        public void Clear()
        {
            Array.Clear(Components);
            ByteColors = false;
        }

        public MeshLayout Copy()
        {
            var copy = new MeshLayout { ByteColors = ByteColors };
            Components.CopyTo(copy.Components, 0);
            return copy;
        }
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

        /// The Vulkan device's buffers (null on OpenGL, whose fields above it leaves unused).
        public Vulkan.VkMesh Vk;

        public const string Kind = "mesh buffers";

        /// Main thread: the mesh's first upload, queued to run ahead of its first draw (GlAhead)
        /// and not yet posted into a frame. Running it twice runs it once.
        public Action Ahead;

        /// Released (GL thread): an upload queued ahead of use leaves it alone.
        public bool Deleted;

        /// The bytes of its buffers' storage (VideoMemoryLedger).
        public long Bytes()
        {
            long bytes = IndexCapacity;
            foreach (long c in Capacities) bytes += c;
            return bytes;
        }

        public void Delete()
        {
            Deleted = true;
            long bytes = Bytes();
            if (bytes != 0) VideoMemoryLedger.Add(Kind, -bytes, -1);
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
    /// The first VertexCount elements of each channel and IndexCount of the indices are the
    /// data: a mesh setter's arrays can be longer (MeshNative.NewChannelAtLeast).
    ///
    /// Records come from the recording frame's arena (FrameArena.Record) and are reused once
    /// that frame has run on the GL thread; the command carries the record's generation, and a
    /// record found with another one (handed out again too early) is reported, not uploaded.
    internal sealed class MeshUpload : IFrameRecord
    {
        public int Generation { get; set; }
        public readonly Array[] Channels = new Array[(int)Channel.Count];
        public int VertexCount;
        public int[] Indices;
        public int IndexCount;
        /// The data's checksum when recorded (MeshReuseVerify).
        public ulong Checksum;
        public uint Usage;
        /// The mesh and this upload's place in its uploads: once it has run, the arrays it held
        /// may be reused (MeshNative.NewChannelAtLeast, CanvasArrays).
        public MeshNative Mesh;
        public int Sequence;

        /// The device has copied the arrays out (device thread; also when the upload failed).
        public void Ran() => Mesh?.UploadRan(Sequence);
    }

    /// ONI_VERIFY_MESH_REUSE=1 (MeshNative.VerifyReuse): every mesh upload's data is
    /// checksummed when it is recorded (main thread) and again on the GL thread just before it
    /// is copied into buffers. The arrays are the mesh's own until the upload has run, and
    /// MeshNative hands one out again only after that; a difference means an array was written
    /// (reused) while an upload still held it.
    internal static class MeshReuseVerify
    {
        static long recorded, checkedUploads, wrong;
        static int reportedFrame;

        public static void Recorded(MeshUpload upload)
        {
            upload.Checksum = Checksum(upload);
            recorded++;
            int frame = Time.frameCount;
            if (frame % 600 == 0 && frame != reportedFrame)
            {
                reportedFrame = frame;
                OniMoono.Engine.EngineLog.Engine($"mesh reuse: {recorded} uploads recorded, {System.Threading.Interlocked.Read(ref checkedUploads)} checked, {System.Threading.Interlocked.Read(ref wrong)} wrong, {System.Threading.Interlocked.Read(ref ResourceCommand.StaleRecords)} stale upload records; {MeshNative.ReuseCounts()}");
            }
        }

        public static void Uploading(MeshUpload upload)
        {
            System.Threading.Interlocked.Increment(ref checkedUploads);
            if (Checksum(upload) == upload.Checksum) return;
            long n = System.Threading.Interlocked.Increment(ref wrong);
            OniMoono.Engine.EngineLog.Engine($"mesh reuse wrong ({n}): mesh '{upload.Mesh?.Name}' upload {upload.Sequence}: its arrays changed between recording and upload");
        }

        static ulong Checksum(MeshUpload upload)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < upload.Channels.Length; i++)
            {
                h = Mix(h, (ulong)i);
                switch (upload.Channels[i])
                {
                    case Vector3[] v: h = Mix(h, v.AsSpan(0, Math.Min(upload.VertexCount, v.Length))); break;
                    case Vector4[] v: h = Mix(h, v.AsSpan(0, Math.Min(upload.VertexCount, v.Length))); break;
                    case Color[] v: h = Mix(h, v.AsSpan(0, Math.Min(upload.VertexCount, v.Length))); break;
                    case Color32[] v: h = Mix(h, v.AsSpan(0, Math.Min(upload.VertexCount, v.Length))); break;
                }
            }
            if (upload.Indices != null) h = Mix(h, upload.Indices.AsSpan(0, upload.IndexCount));
            return h;
        }

        static ulong Mix(ulong h, ulong v) => (h ^ v) * 1099511628211UL;

        static ulong Mix<T>(ulong h, ReadOnlySpan<T> values) where T : unmanaged
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(values);
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes);
            foreach (ulong w in words) h = Mix(h, w);
            for (int i = words.Length * 8; i < bytes.Length; i++) h = Mix(h, bytes[i]);
            return Mix(h, (ulong)bytes.Length);
        }
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
            // Its upload queued ahead of use goes into the frame first (it runs only once).
            if (d.Ahead != null)
            {
                worker.Post(d.Ahead);
                d.Ahead = null;
            }
            if (d.RecordedVersion == m.Version) return d;
            var upload = worker.Arena.Record<MeshUpload>();
            Record(m, d, upload);
            PostUpload(worker, d, upload);
            return d;
        }

        /// A mesh never drawn, whose data is set (main thread): its buffers are made and filled
        /// on the GL thread while it has no frame to execute (GlAhead), not in the frame that
        /// first draws it. Its first draw posts the upload into that frame if it has not run.
        public static void PrepareAhead(GlWorker worker, MeshNative m)
        {
            if (m.Gpu != null) return;
            var d = new DeviceMesh();
            m.Gpu = d;
            GraphicsDevice.Adopt(m, d);
            var upload = new MeshUpload();
            Record(m, d, upload);
            var command = new ResourceCommand().UploadMesh(worker.Resources, d, upload);
            bool ran = false;
            d.Ahead = () =>
            {
                if (ran) return;
                ran = true;
                if (d.Deleted) upload.Ran();
                else command();
            };
            GlAhead.Enqueue(d.Ahead);
        }

        /// Records the mesh's data as it is now into `upload` (main thread).
        static void Record(MeshNative m, DeviceMesh d, MeshUpload upload)
        {
            Array.Clear(upload.Channels);
            upload.Usage = m.Dynamic ? DynamicDraw : StaticDraw;
            upload.Mesh = m;
            upload.Sequence = ++m.UploadsPosted;
            upload.VertexCount = m.VertexCount;
            upload.Indices = null;
            upload.IndexCount = 0;
            upload.Checksum = 0;
            // Built in a scratch layout (main thread): the device mesh keeps its layout object
            // while the channels stay the same, and gets a copy of its own when they change (the
            // GL thread reads the layouts it was given, and vertex arrays are kept per layout).
            var layout = scratchLayout;
            layout.Clear();
            AddChannel(upload, layout, Channel.Position, m.Positions, 3);
            AddChannel(upload, layout, Channel.Normal, m.Normals, 3);
            AddChannel(upload, layout, Channel.Tangent, m.Tangents, 4);
            if (m.ColorsAreBytes && m.Colors32 is { Length: > 0 } given)
            {
                upload.Channels[(int)Channel.Color] = given;
                layout.Components[(int)Channel.Color] = 4;
                layout.ByteColors = true;
            }
            else if (m.Colors != null && m.VertexCount > 0 && m.ColorsAreBytes)
            {
                var bytes = new Color32[m.VertexCount];
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
            d.SubMeshes = Indices(m, d.SubMeshes, out upload.Indices, out upload.IndexCount);
            if (!layout.SameAs(d.Layout)) d.Layout = layout.Copy();
            d.RecordedVersion = m.Version;
            if (MeshNative.VerifyReuse) MeshReuseVerify.Recorded(upload);
        }

        /// The mesh's device data is recorded as it is now, in a frame: Prepare would record nothing.
        public static bool IsCurrent(MeshNative m) => m.Gpu is DeviceMesh d && d.RecordedVersion == m.Version && d.Ahead == null;

        // A ResourceCommand from the frame's arena: no closure or delegate per changed mesh.
        static void PostUpload(GlWorker worker, DeviceMesh d, MeshUpload upload)
            => worker.Post(worker.Arena.Command().UploadMesh(worker.Resources, d, upload));

        static readonly MeshLayout scratchLayout = new();

        static void AddChannel<T>(MeshUpload upload, MeshLayout layout, Channel c, T[] data, int components) where T : unmanaged
        {
            if (data == null || data.Length == 0) return;
            upload.Channels[(int)c] = data;
            layout.Components[(int)c] = components;
        }

        /// Writes the recorded data into the mesh's buffers (GL thread).
        internal static void Upload(DeviceMesh d, MeshUpload upload)
        {
            long before = d.Bytes();
            try
            {
                Fill(d, upload);
            }
            finally
            {
                long after = d.Bytes();
                VideoMemoryLedger.Add(DeviceMesh.Kind, after - before, (after != 0 ? 1 : 0) - (before != 0 ? 1 : 0));
            }
        }

        static void Fill(DeviceMesh d, MeshUpload upload)
        {
            int n = upload.VertexCount;
            for (int i = 0; i < upload.Channels.Length; i++)
            {
                switch (upload.Channels[i])
                {
                    case null: continue;
                    case Vector3[] v: Store(d, i, v, n, upload.Usage); break;
                    case Vector4[] v: Store(d, i, v, n, upload.Usage); break;
                    case Color[] v: Store(d, i, v, n, upload.Usage); break;
                    case Color32[] v: Store(d, i, v, n, upload.Usage); break;
                }
            }
            if (d.IndexBuffer == 0) d.IndexBuffer = GlApi.CreateBuffer();
            d.IndexCapacity = Fill(d.IndexBuffer, upload.Indices, upload.IndexCount, d.IndexCapacity, upload.Usage);
        }

        static void Store<T>(DeviceMesh d, int i, T[] data, int count, uint usage) where T : unmanaged
        {
            if (d.Buffers[i] == 0) d.Buffers[i] = GlApi.CreateBuffer();
            // Channels hold at least VertexCount values (MeshNative).
            d.Capacities[i] = Fill(d.Buffers[i], data, Math.Min(count, data.Length), d.Capacities[i], usage);
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
        /// The ranges go into `previous` when it has the length (the device mesh's last ranges:
        /// read on the main thread only, as values, by the draws it records).
        static (int, int, uint, int)[] Indices(MeshNative m, (int, int, uint, int)[] previous, out int[] indices, out int count)
        {
            var subs = previous.Length == m.SubMeshes.Count ? previous : new (int, int, uint, int)[m.SubMeshes.Count];
            if (subs.Length == 1 && m.SubMeshes[0].Topology != MeshTopology.Quads)
            {
                var only = m.SubMeshes[0];
                indices = only.IndexArray;
                count = only.IndexCount;
                subs[0] = (0, count, Mode(only.Topology), only.BaseVertex);
                return subs;
            }
            int total = 0;
            foreach (var sm in m.SubMeshes)
                total += sm.Topology == MeshTopology.Quads ? sm.IndexCount / 4 * 6 : sm.IndexCount;
            indices = new int[total];
            count = total;
            int at = 0;
            for (int s = 0; s < subs.Length; s++)
            {
                var sm = m.SubMeshes[s];
                var source = sm.Indices;
                int start = at;
                if (sm.Topology == MeshTopology.Quads)
                    for (int q = 0; q + 3 < source.Length; q += 4)
                    {
                        indices[at++] = source[q];
                        indices[at++] = source[q + 1];
                        indices[at++] = source[q + 2];
                        indices[at++] = source[q];
                        indices[at++] = source[q + 2];
                        indices[at++] = source[q + 3];
                    }
                else
                {
                    source.CopyTo(indices.AsSpan(at));
                    at += source.Length;
                }
                subs[s] = (start, at - start, Mode(sm.Topology), sm.BaseVertex);
            }
            return subs;
        }

        /// Buffers this small are made with the dynamic usage hint whatever the mesh's: on Mesa's
        /// D3D12 driver making a small static buffer took about 0.3 ms, a dynamic one about
        /// 10 µs (measured: a view's first sight of a few hundred buildings' and labels' meshes
        /// was 0.4 s of one frame). The hint changes nothing a draw reads.
        const long SmallBufferBytes = 64 << 10;

        /// Writes data into a buffer (direct state access: no binding changes, so the bound
        /// vertex array keeps its index buffer): in place when it fits the current storage, else
        /// into new storage. Returns the storage size.
        static long Fill<T>(uint buffer, T[] data, int count, long capacity, uint usage) where T : unmanaged
        {
            long bytes = (long)count * sizeof(T);
            fixed (T* p = data)
            {
                long t0 = GlBatchProfile.Enabled ? GlTimings.Now : 0;
                if (bytes > 0 && bytes <= capacity)
                {
                    N.glNamedBufferSubData(new GlBuffer(buffer), 0, (nint)bytes, p);
                    if (GlBatchProfile.Enabled) GlBatchProfile.Add("  glNamedBufferSubData", GlTimings.Now - t0, bytes);
                    return capacity;
                }
                N.glNamedBufferData(new GlBuffer(buffer), (nint)bytes, p, bytes <= SmallBufferBytes ? DynamicDraw : usage);
                if (GlBatchProfile.Enabled) GlBatchProfile.Add($"  glNamedBufferData {(bytes < 4096 ? "under" : "over")} 4 KB", GlTimings.Now - t0, bytes);
            }
            return bytes;
        }
    }
}
