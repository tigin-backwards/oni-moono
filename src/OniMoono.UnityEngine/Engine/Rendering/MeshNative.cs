using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Serialization;
using UnityEngine;

namespace OniMoono.Engine.Rendering
{
    internal sealed class SubMeshData
    {
        int[] array = Array.Empty<int>();
        int count;

        public SubMeshData() { }
        public SubMeshData(int[] indices) => SetIndices(indices);

        /// The submesh's indices: the first IndexCount elements of IndexArray. A mesh setter's
        /// array can be longer (MeshNative.NewChannelAtLeast); what lies beyond the count is
        /// not the submesh's and is never read.
        public ReadOnlySpan<int> Indices => new(array, 0, count);
        public int IndexCount => count;
        public int[] IndexArray => array;

        public void SetIndices(int[] indices) => SetIndices(indices, indices.Length);

        public void SetIndices(int[] indices, int count)
        {
            if ((uint)count > (uint)indices.Length) throw new ArgumentOutOfRangeException(nameof(count));
            array = indices;
            this.count = count;
        }

        public MeshTopology Topology = MeshTopology.Triangles;
        public int BaseVertex;
        public Bounds Bounds;
        /// The index array a mesh setter made for this submesh (MeshNative.Retire).
        internal int[] Given;

        /// As a new submesh: no indices, triangles, no base vertex, empty bounds.
        public void Reset()
        {
            array = Array.Empty<int>();
            count = 0;
            Topology = MeshTopology.Triangles;
            BaseVertex = 0;
            Bounds = default;
            Given = null;
        }
    }

    /// A mesh's data, kept as per-attribute arrays (Unity's channel model). The device builds its
    /// buffers from them when Version changes. A channel or submesh index array is filled before
    /// it is published and never written afterwards (a change replaces it, and getters hand out
    /// copies): the device's uploads hold the arrays themselves until they run on the render
    /// thread.
    ///
    /// A channel holds VertexCount values, and a submesh IndexCount indices, at the start of its
    /// array. An array the mesh setters made can be longer (NewChannelAtLeast: a mesh rebuilt
    /// every frame with another vertex count reuses its larger arrays); what lies beyond the
    /// count is not the mesh's, so every reader goes by the counts, and getters copy exactly
    /// that many. Arrays from anywhere else are exactly as long as their count.
    internal sealed class MeshNative : NativeObject
    {
        public const int UvChannels = 8;

        public int VertexCount;
        public Vector3[] Positions;
        Vector3[] normals;
        Vector4[] tangents;
        Color[] colors;
        Color32[] colors32;

        // Channels a layout-keeping Clear left without data: each comes back at the vertex count
        // when something reads it before it is set again (measured in the original player: after
        // Clear() and 4 new positions a mesh that had normals, uv and colours reads 4 of each,
        // their values whatever memory held; here zeros). Made only when read, so the usual
        // Clear, SetVertices, SetColors, SetUVs sequence allocates nothing it then drops.
        int kept;
        const int KeptNormals = 1, KeptTangents = 2, KeptColors = 4, KeptColorBytes = 8, KeptUv0 = 16;

        T[] Materialize<T>(int bit)
        {
            if (VertexCount == 0) return null;
            kept &= ~bit;
            return new T[VertexCount];
        }

        public Vector3[] Normals
        {
            get
            {
                if ((kept & KeptNormals) != 0) normals = Materialize<Vector3>(KeptNormals);
                return normals;
            }
            set { normals = value; kept &= ~KeptNormals; }
        }

        public Vector4[] Tangents
        {
            get
            {
                if ((kept & KeptTangents) != 0) tangents = Materialize<Vector4>(KeptTangents);
                return tangents;
            }
            set { tangents = value; kept &= ~KeptTangents; }
        }

        void MaterializeColors()
        {
            if ((kept & KeptColors) == 0 || VertexCount == 0) return;
            if ((kept & KeptColorBytes) != 0) colors32 = new Color32[VertexCount];
            else colors = new Color[VertexCount];
            kept &= ~(KeptColors | KeptColorBytes);
        }

        /// The vertex colours. Colours given as bytes (SetColors32) are kept as given and turned
        /// into Colors only when something reads them: the device uploads the bytes.
        public Color[] Colors
        {
            get
            {
                MaterializeColors();
                if (colors == null && colors32 != null)
                {
                    var expanded = new Color[VertexCount];
                    for (int i = 0; i < expanded.Length; i++) expanded[i] = colors32[i];
                    colors = expanded;
                }
                return colors;
            }
            set
            {
                colors = value;
                colors32 = null;
                kept &= ~(KeptColors | KeptColorBytes);
            }
        }

        /// Colours held as Color values (given so, or expanded from bytes when read).
        internal bool HasFloatColors => colors != null;

        /// The colours as given in bytes, or null when they were given otherwise.
        public Color32[] Colors32
        {
            get
            {
                MaterializeColors();
                return colors32;
            }
        }

        public void SetColors32(Color32[] values)
        {
            colors = null;
            colors32 = values;
            kept &= ~(KeptColors | KeptColorBytes);
        }

        /// The UV channels by index; a channel kept by a layout-keeping Clear comes back when read.
        internal sealed class UvSet
        {
            readonly MeshNative owner;
            internal readonly Vector4[][] Raw = new Vector4[UvChannels][];
            public UvSet(MeshNative owner) => this.owner = owner;
            public int Length => UvChannels;

            public Vector4[] this[int i]
            {
                get
                {
                    int bit = KeptUv0 << i;
                    if ((owner.kept & bit) != 0) Raw[i] = owner.Materialize<Vector4>(bit);
                    return Raw[i];
                }
                set
                {
                    Raw[i] = value;
                    owner.kept &= ~(KeptUv0 << i);
                }
            }
        }

        /// Colours were given as Color32 (UNorm8 storage).
        public bool ColorsAreBytes;
        public readonly UvSet Uvs;
        public readonly int[] UvDimensions = new int[UvChannels];
        public List<SubMeshData> SubMeshes = new() { new SubMeshData() };

        /// The mesh's bounds. A change of them, to other bits, recomputes the culling bounds of
        /// the renderers that read them (FrameRenderer).
        public Bounds Bounds
        {
            get => bounds;
            set
            {
                bool same = CullCache.SameBits(bounds, value);
                bounds = value;
                if (!same) BoundsReadersChanged();
            }
        }

        Bounds bounds;

        /// Renderers whose culling bounds were made from these bounds; each one is in the list
        /// of the last mesh it read, at its BoundsMeshIndex.
        List<RendererNative> boundsReaders;

        public void AddBoundsReader(RendererNative r)
        {
            if (r.BoundsMesh == this) return;
            r.BoundsMesh?.RemoveBoundsReader(r);
            boundsReaders ??= new();
            r.BoundsMesh = this;
            r.BoundsMeshIndex = boundsReaders.Count;
            boundsReaders.Add(r);
        }

        public void RemoveBoundsReader(RendererNative r)
        {
            var list = boundsReaders;
            int i = r.BoundsMeshIndex, last = list.Count - 1;
            if (i != last)
            {
                var moved = list[last];
                list[i] = moved;
                moved.BoundsMeshIndex = i;
            }
            list.RemoveAt(last);
            r.BoundsMesh = null;
            r.BoundsMeshIndex = -1;
        }

        /// The bounds changed, or the mesh was destroyed (a destroyed mesh counts as none).
        public void BoundsReadersChanged()
        {
            var list = boundsReaders;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++) FrameRenderer.BoundsChanged(list[i]);
        }
        public bool IsReadable = true;
        public bool Dynamic;
        /// 0 = 16-bit, 1 = 32-bit (IndexFormat).
        public int IndexFormat;

        public int Version;
        public object Gpu;

        public MeshNative() => Uvs = new UvSet(this);

        public void Changed()
        {
            Version++;
            // Data for a mesh no draw has used yet: the device may upload it ahead of its first draw.
            if (Gpu == null && !AheadNoted && Threads.IsMainThread) GraphicsDevice.NoteUndrawn(this);
        }

        /// Main thread: noted for GraphicsDevice.PrepareUndrawn at the end of this frame's rendering.
        public bool AheadNoted;

        // ----- Channel arrays for reuse -----

        /// Uploads of this mesh's arrays the device has been given (main thread) and the last of
        /// them it has run (written by the device thread; they run in order).
        public int UploadsPosted;
        public int UploadsRan;

        /// The device has copied out the arrays of upload `sequence` (device thread).
        public void UploadRan(int sequence) => System.Threading.Volatile.Write(ref UploadsRan, sequence);

        public const int SlotPositions = 0, SlotNormals = 1, SlotTangents = 2, SlotColors = 3, SlotColors32 = 4, SlotUv0 = 5;

        /// The channel arrays the mesh setters (UnityEngine.Mesh) made, by slot: the mesh's own,
        /// as nothing else is handed them (getters return copies). Only these are reused.
        Array[] given;

        /// Arrays the mesh let go of, with the uploads posted by then: a channel array is reused
        /// only once the device has run every upload that could hold it.
        List<(Array Array, int Posted, int Frame)> spares;

        /// Meshes holding spares, swept once a frame (SweepSpares), and whether this one is.
        static readonly List<MeshNative> withSpares = new();
        bool listed;

        const int MaxSpares = 16, SpareFrames = 4;

        /// A mesh never drawn (uGUI's shared worker mesh, filled for every element rebuilt and
        /// copied by its renderer) keeps more spares, longer: no upload holds them, and its
        /// fills alternate between elements of every size, the larger ones a few times a second.
        const int MaxWorkerSpares = 32, WorkerSpareFrames = 120;

        /// A mesh the engine refills itself in each frame it is seen, for each camera that sees
        /// it (a particle system's): it keeps spares as the worker mesh does. A fill retires
        /// about six arrays, and while the device has yet to run the uploads holding the last
        /// fills, MaxSpares made room by dropping the oldest spares, the ones it was done with;
        /// a system that pauses between bursts found its spares swept.
        internal bool KeepsSpares;

        bool ManySpares => UploadsPosted == 0 || KeepsSpares;

        /// Reuse statistics (ONI_MESH_SPARES=stats): arrays wanted, taken from spares, and
        /// arrays retired.
        internal static readonly bool Stats = Environment.GetEnvironmentVariable("ONI_MESH_SPARES") == "stats" || Environment.GetEnvironmentVariable("ONI_VERIFY_MESH_REUSE") == "1";
        internal static long Wanted, Taken, Retired;

        /// Reuse off (ONI_MESH_SPARES=off, or set while running by a measurement): every new
        /// channel array is allocated, and canvas elements rebake their colours (CanvasSystem).
        internal static bool ReuseOff = Environment.GetEnvironmentVariable("ONI_MESH_SPARES") == "off";

        /// A new channel array of at least `length` for the mesh setters, which record the count
        /// beside it (VertexCount, SubMeshData.IndexCount): the smallest spare no upload holds
        /// any more that is no more than twice as long, else a new one. A mesh that has let go of
        /// arrays before (one rebuilt again and again) gets a new one at the next size class, so
        /// that a vertex count that changes a little every frame still finds a spare; others get
        /// one of exactly `length`. Its contents are the caller's to fill up to `length`.
        internal T[] NewChannelAtLeast<T>(int length) where T : struct
        {
            if (Stats) Wanted++;
            if (spares != null && !ReuseOff)
            {
                int ran = System.Threading.Volatile.Read(ref UploadsRan);
                int best = -1, bestLength = int.MaxValue;
                long most = 2L * length;
                for (int i = spares.Count - 1; i >= 0; i--)
                {
                    var (array, posted, _) = spares[i];
                    if (posted > ran || array is not T[] typed || typed.Length < length || typed.Length > most || typed.Length >= bestLength) continue;
                    best = i;
                    bestLength = typed.Length;
                }
                if (best >= 0)
                {
                    var typed = (T[])spares[best].Array;
                    spares.RemoveAt(best);
                    if (Stats) Taken++;
                    // What a setter fails to write shows as NaNs (verify mode).
                    if (VerifyReuse) System.Runtime.InteropServices.MemoryMarshal.AsBytes(typed.AsSpan()).Fill(0xFF);
                    return typed;
                }
            }
            return new T[spares != null && !ReuseOff ? SizeClass(length) : length];
        }

        /// The smallest of 4, 5, 6, 7 times a power of two that holds `length` (at most a quarter
        /// more); below 4, `length`.
        internal static int SizeClass(int length)
        {
            if (length <= 4) return length;
            int shift = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)length) - 2;
            int step = 1 << shift;
            long size = ((long)length + step - 1) / step * step;
            return size > Array.MaxLength ? length : (int)size;
        }

        /// Reuse counts for the verify report.
        internal static string ReuseCounts() => $"{Wanted} arrays wanted, {Taken} reused; canvas copies {UI.CanvasArrays.Wanted} wanted, {UI.CanvasArrays.Taken} reused";

        /// ONI_VERIFY_MESH_REUSE=1: each mesh upload checksums the arrays it holds when it is
        /// recorded and again on the render thread before copying them; a reused array written in
        /// between (one handed out again too early) shows as a difference. Reused arrays are
        /// filled with NaN bytes when taken, so values a setter failed to write show too.
        internal static readonly bool VerifyReuse = Environment.GetEnvironmentVariable("ONI_VERIFY_MESH_REUSE") == "1";

        /// Records `array` (made by NewChannelAtLeast and filled) as the setter's for `slot`.
        internal void Give(int slot, Array array)
        {
            if (ReuseOff) return;
            given ??= new Array[SlotUv0 + UvChannels];
            given[slot] = array;
        }

        /// The slot's array is being replaced or dropped: kept as a spare when it is the one a
        /// setter made. Read as it is: a channel kept without data is not made for this.
        internal void Retire(int slot)
        {
            if (given == null) return;
            Retire(slot, slot switch
            {
                SlotPositions => Positions,
                SlotNormals => normals,
                SlotTangents => tangents,
                SlotColors => colors,
                SlotColors32 => colors32,
                _ => Uvs.Raw[slot - SlotUv0],
            });
        }

        void Retire(int slot, Array current)
        {
            if (given == null || current == null || !ReferenceEquals(given[slot], current)) return;
            given[slot] = null;
            AddSpare(current);
        }

        /// Either colour setter replaces both colour arrays.
        internal void RetireColors()
        {
            Retire(SlotColors, colors);
            Retire(SlotColors32, colors32);
        }

        /// Records the submesh's index array (made by NewChannelAtLeast and filled) as a setter's.
        internal void GiveIndices(SubMeshData s)
        {
            if (!ReuseOff) s.Given = s.IndexArray;
        }

        /// A submesh's index array is being replaced or dropped (as Retire).
        internal void RetireIndices(SubMeshData s)
        {
            if (s.Given == null) return;
            if (ReferenceEquals(s.Given, s.IndexArray)) AddSpare(s.Given);
            s.Given = null;
        }

        /// Every array a setter made, before Clear drops them.
        internal void RetireAll()
        {
            if (given != null)
            {
                Retire(SlotPositions, Positions);
                Retire(SlotNormals, normals);
                Retire(SlotTangents, tangents);
                Retire(SlotColors, colors);
                Retire(SlotColors32, colors32);
                for (int i = 0; i < UvChannels; i++) Retire(SlotUv0 + i, Uvs.Raw[i]);
            }
            foreach (var s in SubMeshes) RetireIndices(s);
        }

        void AddSpare(Array array) => AddSpare(array, UploadsPosted);

        /// An array the device made for one of this mesh's uploads (`sequence`, its place in
        /// UploadsPosted) from NewChannelAtLeast, given back: reused once that upload has run.
        internal void RetireUploaded(Array array, int sequence)
        {
            if (!ReuseOff) AddSpare(array, sequence);
        }

        void AddSpare(Array array, int posted)
        {
            if (array.Length == 0) return;
            spares ??= new();
            if (!listed)
            {
                listed = true;
                withSpares.Add(this);
            }
            if (spares.Count >= (ManySpares ? MaxWorkerSpares : MaxSpares)) spares.RemoveAt(0);
            spares.Add((array, posted, Time.frameCount));
            if (Stats) Retired++;
        }

        /// Drops spares not taken within a few frames (start of a frame): a mesh that stopped
        /// changing keeps none.
        internal static void SweepSpares()
        {
            if (withSpares.Count == 0) return;
            int frame = Time.frameCount;
            for (int m = withSpares.Count - 1; m >= 0; m--)
            {
                var mesh = withSpares[m];
                var s = mesh.spares;
                // Oldest first: they were added in frame order.
                int stale = 0;
                int keep = mesh.ManySpares ? WorkerSpareFrames : SpareFrames;
                while (stale < s.Count && frame - s[stale].Frame > keep) stale++;
                s.RemoveRange(0, stale);
                if (s.Count > 0) continue;
                mesh.listed = false;
                withSpares.RemoveAt(m);
            }
            if (Stats && frame % 600 == 0)
                EngineLog.Engine($"mesh spares: {Wanted} arrays wanted, {Taken} reused, {Retired} retired, {withSpares.Count} meshes holding spares");
        }

        /// Removes the vertices and indices. With `keepVertexLayout` the channels the mesh had
        /// are kept without data (see `kept`); without, they are dropped.
        public void Clear(bool keepVertexLayout = false)
        {
            RetireAll();
            VertexCount = 0;
            Positions = null;
            if (keepVertexLayout)
            {
                if (normals != null) kept |= KeptNormals;
                if (tangents != null) kept |= KeptTangents;
                if (colors32 != null) kept |= KeptColors | KeptColorBytes;
                else if (colors != null) kept |= KeptColors;
                for (int i = 0; i < UvChannels; i++)
                    if (Uvs.Raw[i] != null) kept |= KeptUv0 << i;
            }
            else
            {
                kept = 0;
                ColorsAreBytes = false;
                Array.Clear(UvDimensions);
            }
            normals = null;
            tangents = null;
            colors = null;
            colors32 = null;
            Array.Clear(Uvs.Raw);
            // One empty submesh, as a new one is: the list and its first submesh are kept (they
            // are the mesh's own; the device copies what it needs when it records a draw or an
            // upload), so meshes rebuilt every frame (uGUI's, particles') allocate neither.
            if (SubMeshes.Count > 1) SubMeshes.RemoveRange(1, SubMeshes.Count - 1);
            if (SubMeshes.Count == 1) SubMeshes[0].Reset();
            else SubMeshes.Add(new SubMeshData());
            // The bounds stay (measured in the original player).
            Changed();
        }

        /// Gives the channels that hold data the new vertex count, keeping their leading values
        /// (new ones zero). Kept channels without data stay so: they are made at the count when read.
        /// Called before VertexCount changes: the old count is the arrays' data.
        public void ResizeChannels(int count)
        {
            // The old arrays are dropped (Resize copies into new ones).
            Retire(SlotNormals, normals);
            Retire(SlotTangents, tangents);
            Retire(SlotColors, colors);
            Retire(SlotColors32, colors32);
            for (int i = 0; i < UvChannels; i++) Retire(SlotUv0 + i, Uvs.Raw[i]);
            int old = VertexCount;
            normals = Resize(normals, old, count);
            tangents = Resize(tangents, old, count);
            colors = Resize(colors, old, count);
            colors32 = Resize(colors32, old, count);
            for (int i = 0; i < UvChannels; i++) Uvs.Raw[i] = Resize(Uvs.Raw[i], old, count);
        }

        /// The first `old` values (the data, however long the array) in an array of `count`,
        /// new ones zero.
        static T[] Resize<T>(T[] array, int old, int count)
        {
            if (array == null || count == 0) return null;
            var result = new T[count];
            Array.Copy(array, result, Math.Min(Math.Min(old, array.Length), count));
            return result;
        }

        /// The channel's data as an array of its own (VertexCount values), or null.
        internal T[] Exact<T>(T[] channel)
        {
            if (channel == null) return null;
            return channel.Length == VertexCount ? (T[])channel.Clone() : channel.AsSpan(0, VertexCount).ToArray();
        }

        public void RecalculateBounds()
        {
            Bounds = BoundsOf(Positions, VertexCount);
            foreach (var s in SubMeshes) s.Bounds = SubMeshBounds(s);
            Changed();
        }

        static Bounds BoundsOf(Vector3[] points, int count)
        {
            if (points == null || count == 0) return default;
            Vector3 min = points[0], max = points[0];
            for (int i = 1; i < count; i++)
            {
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        public Bounds SubMeshBounds(SubMeshData s)
        {
            var indices = s.Indices;
            if (Positions == null || indices.Length == 0) return default;
            Vector3 min = Positions[indices[0] + s.BaseVertex], max = min;
            foreach (int i in indices)
            {
                var p = Positions[i + s.BaseVertex];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        public void CopyFrom(MeshNative o)
        {
            VertexCount = o.VertexCount;
            Positions = o.Exact(o.Positions);
            normals = o.Exact(o.normals);
            tangents = o.Exact(o.tangents);
            colors32 = o.Exact(o.colors32);
            colors = colors32 != null ? null : o.Exact(o.colors);
            kept = o.kept;
            ColorsAreBytes = o.ColorsAreBytes;
            for (int i = 0; i < UvChannels; i++)
            {
                Uvs.Raw[i] = o.Exact(o.Uvs.Raw[i]);
                UvDimensions[i] = o.UvDimensions[i];
            }
            SubMeshes = o.SubMeshes.ConvertAll(s => new SubMeshData(s.Indices.ToArray()) { Topology = s.Topology, BaseVertex = s.BaseVertex, Bounds = s.Bounds });
            Bounds = o.Bounds;
            IndexFormat = o.IndexFormat;
            Dynamic = o.Dynamic;
            Changed();
        }

        /// Mesh (class 43): vertex streams, the index buffer and submeshes.
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            if (f.Get<byte>("m_MeshCompression") != 0) throw new InvalidDataException($"Mesh {Name}: compressed meshes are not supported.");
            if (f.Struct("m_StreamData").String("path").Length > 0) throw new InvalidDataException($"Mesh {Name}: streamed mesh data is not supported.");
            IsReadable = f.Bool("m_IsReadable");
            IndexFormat = f.Int("m_IndexFormat");
            var vertices = VertexData.Decode(f.Struct("m_VertexData"));
            vertices.ApplyTo(this);
            var indexBytes = f.Get<byte[]>("m_IndexBuffer");
            int indexSize = IndexFormat == 0 ? 2 : 4;
            SubMeshes = new List<SubMeshData>();
            foreach (Fields s in f.Array("m_SubMeshes"))
            {
                int first = (int)s.Get<uint>("firstByte") / indexSize;
                int count = (int)s.Get<uint>("indexCount");
                var indices = new int[count];
                for (int i = 0; i < count; i++)
                    indices[i] = indexSize == 2
                        ? BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.AsSpan((first + i) * 2))
                        : (int)BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.AsSpan((first + i) * 4));
                SubMeshes.Add(new SubMeshData(indices)
                {
                    Topology = (MeshTopology)s.Int("topology"),
                    BaseVertex = (int)s.Get<uint>("baseVertex"),
                    Bounds = ReadAabb(s.Struct("localAABB")),
                });
            }
            if (SubMeshes.Count == 0) SubMeshes.Add(new SubMeshData());
            Bounds = ReadAabb(f.Struct("m_LocalAABB"));
            Changed();
        }

        internal static Bounds ReadAabb(Fields aabb)
        {
            var c = aabb.Struct("m_Center");
            var e = aabb.Struct("m_Extent");
            return new Bounds(new Vector3(c.Float("x"), c.Float("y"), c.Float("z")), 2f * new Vector3(e.Float("x"), e.Float("y"), e.Float("z")));
        }

        public void ReleaseResources() => GraphicsDevice.Release(ref Gpu);
    }

    /// Unity's serialized VertexData: channels (Position, Normal, Tangent, Color, TexCoord0-7,
    /// BlendWeight, BlendIndices) laid out in streams; each stream holds its channels interleaved
    /// and starts 16-byte aligned after the previous one.
    internal sealed class VertexData
    {
        public int VertexCount;
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public Color[] Colors;
        public bool ColorsAreBytes;
        public readonly Vector4[][] Uvs = new Vector4[MeshNative.UvChannels][];
        public readonly int[] UvDimensions = new int[MeshNative.UvChannels];

        readonly record struct Channel(int Stream, int Offset, int Format, int Dimension);

        static int FormatSize(int format) => format switch
        {
            0 => 4,                 // Float32
            1 => 2,                 // Float16
            2 or 3 or 6 or 7 => 1,  // UNorm8, SNorm8, UInt8, SInt8
            4 or 5 or 8 or 9 => 2,  // UNorm16, SNorm16, UInt16, SInt16
            10 or 11 => 4,          // UInt32, SInt32
            _ => throw new InvalidDataException("Unknown vertex attribute format " + format),
        };

        static float Component(ReadOnlySpan<byte> p, int format) => format switch
        {
            0 => BinaryPrimitives.ReadSingleLittleEndian(p),
            1 => (float)BinaryPrimitives.ReadHalfLittleEndian(p),
            2 => p[0] / 255f,
            3 => Math.Max((sbyte)p[0] / 127f, -1f),
            4 => BinaryPrimitives.ReadUInt16LittleEndian(p) / 65535f,
            5 => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(p) / 32767f, -1f),
            6 => p[0],
            7 => (sbyte)p[0],
            8 => BinaryPrimitives.ReadUInt16LittleEndian(p),
            9 => BinaryPrimitives.ReadInt16LittleEndian(p),
            10 => BinaryPrimitives.ReadUInt32LittleEndian(p),
            11 => BinaryPrimitives.ReadInt32LittleEndian(p),
            _ => throw new InvalidDataException("Unknown vertex attribute format " + format),
        };

        public static VertexData Decode(Fields vertexData)
        {
            var result = new VertexData { VertexCount = (int)vertexData.Get<uint>("m_VertexCount") };
            var channels = new List<Channel>();
            foreach (Fields c in vertexData.Array("m_Channels"))
                channels.Add(new Channel(c.Get<byte>("stream"), c.Get<byte>("offset"), c.Get<byte>("format"), c.Get<byte>("dimension") & 0xF));
            var data = vertexData.Get<byte[]>("m_DataSize");
            int n = result.VertexCount;

            int streamCount = 0;
            foreach (var c in channels) if (c.Dimension > 0) streamCount = Math.Max(streamCount, c.Stream + 1);
            var strides = new int[streamCount];
            foreach (var c in channels) if (c.Dimension > 0) strides[c.Stream] += FormatSize(c.Format) * c.Dimension;
            var starts = new int[streamCount];
            int offset = 0;
            for (int s = 0; s < streamCount; s++)
            {
                starts[s] = offset;
                offset += strides[s] * n;
                offset = (offset + 15) & ~15;
            }

            Vector4 Read(Channel c, int vertex)
            {
                int size = FormatSize(c.Format);
                var p = data.AsSpan(starts[c.Stream] + vertex * strides[c.Stream] + c.Offset);
                var v = new Vector4();
                for (int k = 0; k < c.Dimension && k < 4; k++) v[k] = Component(p[(k * size)..], c.Format);
                return v;
            }

            for (int ci = 0; ci < channels.Count; ci++)
            {
                var c = channels[ci];
                if (c.Dimension == 0) continue;
                switch (ci)
                {
                    case 0:
                        result.Positions = new Vector3[n];
                        for (int i = 0; i < n; i++) result.Positions[i] = Read(c, i);
                        break;
                    case 1:
                        result.Normals = new Vector3[n];
                        for (int i = 0; i < n; i++) result.Normals[i] = Read(c, i);
                        break;
                    case 2:
                        result.Tangents = new Vector4[n];
                        for (int i = 0; i < n; i++) result.Tangents[i] = Read(c, i);
                        break;
                    case 3:
                        result.Colors = new Color[n];
                        result.ColorsAreBytes = c.Format == 2;
                        for (int i = 0; i < n; i++) result.Colors[i] = Read(c, i);
                        break;
                    case >= 4 and <= 11:
                    {
                        int uv = ci - 4;
                        var values = new Vector4[n];
                        for (int i = 0; i < n; i++) values[i] = Read(c, i);
                        result.Uvs[uv] = values;
                        result.UvDimensions[uv] = c.Dimension;
                        break;
                    }
                    // Skinning channels: the game has no skinned meshes.
                }
            }
            return result;
        }

        public void ApplyTo(MeshNative mesh)
        {
            mesh.VertexCount = VertexCount;
            mesh.Positions = Positions;
            mesh.Normals = Normals;
            mesh.Tangents = Tangents;
            mesh.Colors = Colors;
            mesh.ColorsAreBytes = ColorsAreBytes;
            for (int i = 0; i < MeshNative.UvChannels; i++)
            {
                mesh.Uvs[i] = Uvs[i];
                mesh.UvDimensions[i] = UvDimensions[i];
            }
        }
    }
}
