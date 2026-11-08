using System;
using System.Collections.Generic;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine.Rendering;

namespace UnityEngine
{
    public partial class Mesh
    {
        public Mesh() => Bind(new MeshNative { Name = "" });

        internal MeshNative Data => (MeshNative)NativeOrThrow;

        internal override Object EngineCloneAsset()
        {
            var clone = new Mesh();
            clone.Data.CopyFrom(Data);
            return clone;
        }

        internal override void EngineOnDestroy()
        {
            var m = Data;
            m.BoundsReadersChanged();
            m.ReleaseResources();
        }

        public bool isReadable => Data.IsReadable;

        public int vertexCount => Data.VertexCount;

        public Bounds bounds
        {
            get => Data.Bounds;
            set { var m = Data; m.Bounds = value; m.Changed(); }
        }

        /// The index buffer's element size. Indices are kept as 32-bit values either way; the
        /// format is recorded for the device and for serialization.
        public UnityEngine.Rendering.IndexFormat indexFormat
        {
            get => (UnityEngine.Rendering.IndexFormat)Data.IndexFormat;
            set { var m = Data; m.IndexFormat = (int)value; m.Changed(); }
        }

        /// Marks the mesh for frequent updates (a device buffer usage hint).
        public void MarkDynamic() { var m = Data; m.Dynamic = true; m.Changed(); }

        public void Clear() => Clear(true);

        /// Removes all vertex data and indices (one empty submesh remains). With
        /// `keepVertexLayout` the channels the mesh had come back with the next vertices.
        public void Clear(bool keepVertexLayout) => Data.Clear(keepVertexLayout);

        public void RecalculateBounds() => Data.RecalculateBounds();

        // ----- Channels -----

        bool CanAccess(string channel)
        {
            if (Data.IsReadable) return true;
            Debug.LogError($"Not allowed to access {channel} on mesh '{name}' (isReadable is false; Read/Write must be enabled in import settings)", this);
            return false;
        }

        static void CheckRange(int arrayLength, int start, int count)
        {
            if (start < 0) throw new ArgumentOutOfRangeException("valuesStart", start, "Mesh data array start index can't be negative.");
            if (count < 0) throw new ArgumentOutOfRangeException("valuesCount", count, "Mesh data array length can't be negative.");
            if (start >= arrayLength && count != 0) throw new ArgumentOutOfRangeException("valuesStart", start, "Mesh data array start is outside of array size.");
            if (start + count > arrayLength) throw new ArgumentOutOfRangeException("valuesCount", start + count, "Mesh data array start+count is outside of array size.");
        }

        /// Non-position channels must match the vertex count (an empty array removes the
        /// channel); Unity logs an error and leaves the channel unchanged otherwise.
        bool ChannelSizeOk(string channel, int count)
        {
            var m = Data;
            if (count == 0 || count == m.VertexCount) return true;
            Debug.LogError($"Mesh.{channel} is out of bounds. The supplied array needs to be the same size as the Mesh.vertices array.", this);
            return false;
        }

        /// A copy of values[start..start+count] as a channel: the mesh's array to keep, new or
        /// reused (MeshNative.NewChannelAtLeast, so it can be longer than `count`; the mesh's
        /// counts say how much of it is data), since the device's uploads hold the arrays
        /// themselves until they run.
        static T[] Slice<T>(MeshNative m, IList<T> values, int start, int count) where T : struct
        {
            var result = m.NewChannelAtLeast<T>(count);
            if (TrySpan(values, out var span)) span.Slice(start, count).CopyTo(result);
            else for (int i = 0; i < count; i++) result[i] = values[start + i];
            return result;
        }

        /// The contents of an array or List as a span: the mesh setters copy whole channels, and
        /// the game rebuilds some meshes from lists every frame (conveyor items), where a copy
        /// through IList's indexer costs an interface call per element.
        static bool TrySpan<T>(IList<T> values, out ReadOnlySpan<T> span)
        {
            switch (values)
            {
                case T[] array: span = array; return true;
                case List<T> list: span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list); return true;
                default: span = default; return false;
            }
        }

        void SetPositions(IList<Vector3> values, int start, int count, MeshUpdateFlags flags = MeshUpdateFlags.Default)
        {
            if (!CanAccess("vertices")) return;
            CheckRange(values?.Count ?? 0, start, count);
            var m = Data;
            int maxIndex = -1;
            foreach (var s in m.SubMeshes)
                foreach (int i in s.Indices) maxIndex = Math.Max(maxIndex, i + s.BaseVertex);
            if (maxIndex >= count)
            {
                Debug.LogError("Mesh.vertices is too small. The supplied vertex array has less vertices than are referenced by the triangles array.", this);
                return;
            }
            bool resized = count != m.VertexCount;
            // Other channels follow the new vertex count, keeping their leading values.
            if (resized) m.ResizeChannels(count);
            m.VertexCount = count;
            m.Retire(MeshNative.SlotPositions);
            m.Positions = count == 0 ? null : Slice(m, values, start, count);
            m.Give(MeshNative.SlotPositions, m.Positions);
            // The bounds follow the vertices only when their number changes (measured in the
            // original player: the same number of new positions keeps the bounds).
            if (resized && (flags & MeshUpdateFlags.DontRecalculateBounds) == 0) m.RecalculateBounds();
            else m.Changed();
        }

        public Vector3[] vertices
        {
            get => !CanAccess("vertices") || Data.Positions == null ? Array.Empty<Vector3>() : Data.Exact(Data.Positions);
            set => SetPositions(value, 0, value?.Length ?? 0);
        }

        public void SetVertices(List<Vector3> inVertices) => SetPositions(inVertices, 0, inVertices?.Count ?? 0);
        public void SetVertices(List<Vector3> inVertices, int start, int length) => SetPositions(inVertices, start, length);
        public void SetVertices(Vector3[] inVertices) => SetPositions(inVertices, 0, inVertices?.Length ?? 0);
        public void SetVertices(Vector3[] inVertices, int start, int length) => SetPositions(inVertices, start, length);
        // Of the update flags only DontRecalculateBounds changes anything here: the index check
        // stays (as in Unity, DontValidateIndices does not skip it), and nothing caches what the
        // others would skip.
        public void SetVertices(List<Vector3> inVertices, int start, int length, MeshUpdateFlags flags) => SetPositions(inVertices, start, length, flags);
        public void SetVertices(Vector3[] inVertices, int start, int length, MeshUpdateFlags flags) => SetPositions(inVertices, start, length, flags);

        public void GetVertices(List<Vector3> vertices)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices), "The result vertices list cannot be null.");
            vertices.Clear();
            if (CanAccess("vertices") && Data.Positions != null) vertices.AddRange(Data.Positions.AsSpan(0, Data.VertexCount));
        }

        public Vector3[] normals
        {
            get => !CanAccess("normals") || Data.Normals == null ? Array.Empty<Vector3>() : Data.Exact(Data.Normals);
            set => SetNormalsImpl(value, 0, value?.Length ?? 0);
        }

        /// Each vertex's normal is the normalized sum of the face normals of the triangles that
        /// index it, weighted by their area (the unnormalized cross products), as measured in the
        /// original player. Vertices are not merged by position; a vertex no triangle uses, or
        /// only degenerate ones, gets a zero normal.
        public void RecalculateNormals()
        {
            if (!CanAccess("normals")) return;
            var m = Data;
            var positions = m.Positions ?? Array.Empty<Vector3>();
            var sums = new Vector3[m.Positions == null ? 0 : m.VertexCount];
            foreach (var s in m.SubMeshes)
            {
                if (s.Topology != MeshTopology.Triangles) continue;
                var indices = s.Indices;
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i] + s.BaseVertex, b = indices[i + 1] + s.BaseVertex, c = indices[i + 2] + s.BaseVertex;
                    var face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    sums[a] += face;
                    sums[b] += face;
                    sums[c] += face;
                }
            }
            for (int i = 0; i < sums.Length; i++) sums[i] = sums[i].normalized;
            m.Normals = sums.Length == 0 ? null : sums;
            m.Changed();
        }

        public void SetNormals(List<Vector3> inNormals) => SetNormalsImpl(inNormals, 0, inNormals?.Count ?? 0);
        public void SetNormals(List<Vector3> inNormals, int start, int length) => SetNormalsImpl(inNormals, start, length);
        public void SetNormals(Vector3[] inNormals) => SetNormalsImpl(inNormals, 0, inNormals?.Length ?? 0);
        public void SetNormals(List<Vector3> inNormals, int start, int length, MeshUpdateFlags flags) => SetNormalsImpl(inNormals, start, length);
        public void SetNormals(Vector3[] inNormals, int start, int length, MeshUpdateFlags flags) => SetNormalsImpl(inNormals, start, length);

        void SetNormalsImpl(IList<Vector3> values, int start, int count)
        {
            if (!CanAccess("normals")) return;
            CheckRange(values?.Count ?? 0, start, count);
            if (!ChannelSizeOk("normals", count)) return;
            var m = Data;
            m.Retire(MeshNative.SlotNormals);
            m.Normals = count == 0 ? null : Slice(m, values, start, count);
            m.Give(MeshNative.SlotNormals, m.Normals);
            m.Changed();
        }

        public Vector4[] tangents
        {
            get => !CanAccess("tangents") || Data.Tangents == null ? Array.Empty<Vector4>() : Data.Exact(Data.Tangents);
            set => SetTangentsImpl(value, 0, value?.Length ?? 0);
        }

        public void SetTangents(List<Vector4> inTangents) => SetTangentsImpl(inTangents, 0, inTangents?.Count ?? 0);
        public void SetTangents(List<Vector4> inTangents, int start, int length) => SetTangentsImpl(inTangents, start, length);
        public void SetTangents(Vector4[] inTangents) => SetTangentsImpl(inTangents, 0, inTangents?.Length ?? 0);
        public void SetTangents(List<Vector4> inTangents, int start, int length, MeshUpdateFlags flags) => SetTangentsImpl(inTangents, start, length);
        public void SetTangents(Vector4[] inTangents, int start, int length, MeshUpdateFlags flags) => SetTangentsImpl(inTangents, start, length);

        void SetTangentsImpl(IList<Vector4> values, int start, int count)
        {
            if (!CanAccess("tangents")) return;
            CheckRange(values?.Count ?? 0, start, count);
            if (!ChannelSizeOk("tangents", count)) return;
            var m = Data;
            m.Retire(MeshNative.SlotTangents);
            m.Tangents = count == 0 ? null : Slice(m, values, start, count);
            m.Give(MeshNative.SlotTangents, m.Tangents);
            m.Changed();
        }

        public Color[] colors
        {
            get => !CanAccess("colors") || Data.Colors == null ? Array.Empty<Color>() : Data.Exact(Data.Colors);
            set => SetColorsImpl(value, 0, value?.Length ?? 0);
        }

        public Color32[] colors32
        {
            get
            {
                if (!CanAccess("colors")) return Array.Empty<Color32>();
                if (Data.Colors32 != null) return Data.Exact(Data.Colors32);
                if (Data.Colors == null) return Array.Empty<Color32>();
                var c = Data.Colors;
                var result = new Color32[Data.VertexCount];
                for (int i = 0; i < result.Length; i++) result[i] = c[i];
                return result;
            }
            set => SetColors32Impl(value, 0, value?.Length ?? 0);
        }

        public void SetColors(List<Color> inColors) => SetColorsImpl(inColors, 0, inColors?.Count ?? 0);
        public void SetColors(List<Color> inColors, int start, int length) => SetColorsImpl(inColors, start, length);
        public void SetColors(Color[] inColors) => SetColorsImpl(inColors, 0, inColors?.Length ?? 0);
        public void SetColors(List<Color32> inColors) => SetColors32Impl(inColors, 0, inColors?.Count ?? 0);
        public void SetColors(List<Color32> inColors, int start, int length) => SetColors32Impl(inColors, start, length);
        public void SetColors(Color32[] inColors) => SetColors32Impl(inColors, 0, inColors?.Length ?? 0);
        public void SetColors(List<Color> inColors, int start, int length, MeshUpdateFlags flags) => SetColorsImpl(inColors, start, length);
        public void SetColors(Color[] inColors, int start, int length, MeshUpdateFlags flags) => SetColorsImpl(inColors, start, length);
        public void SetColors(List<Color32> inColors, int start, int length, MeshUpdateFlags flags) => SetColors32Impl(inColors, start, length);
        public void SetColors(Color32[] inColors, int start, int length, MeshUpdateFlags flags) => SetColors32Impl(inColors, start, length);

        void SetColorsImpl(IList<Color> values, int start, int count)
        {
            if (!CanAccess("colors")) return;
            CheckRange(values?.Count ?? 0, start, count);
            if (!ChannelSizeOk("colors", count)) return;
            var m = Data;
            m.RetireColors();
            m.Colors = count == 0 ? null : Slice(m, values, start, count);
            m.Give(MeshNative.SlotColors, m.Colors);
            m.ColorsAreBytes = false;
            m.Changed();
        }

        void SetColors32Impl(IList<Color32> values, int start, int count)
        {
            if (!CanAccess("colors")) return;
            CheckRange(values?.Count ?? 0, start, count);
            if (!ChannelSizeOk("colors", count)) return;
            var m = Data;
            m.RetireColors();
            if (count == 0) m.Colors = null;
            else
            {
                m.SetColors32(Slice(m, values, start, count));
                m.Give(MeshNative.SlotColors32, m.Colors32);
            }
            m.ColorsAreBytes = true;
            m.Changed();
        }

        // ----- UV channels -----

        Vector2[] GetUv2(int channel)
        {
            if (!CanAccess("uv")) return Array.Empty<Vector2>();
            var uv = Data.Uvs[channel];
            if (uv == null) return Array.Empty<Vector2>();
            var result = new Vector2[Data.VertexCount];
            for (int i = 0; i < result.Length; i++) result[i] = uv[i];
            return result;
        }

        /// values[start..start+count] as the mesh's UV array (Vector4 whatever the dimension),
        /// which can be longer (as Slice).
        static Vector4[] Widen(MeshNative m, IList<Vector2> v, int start, int count)
        {
            var r = m.NewChannelAtLeast<Vector4>(count);
            if (TrySpan(v, out var span)) for (int i = 0; i < count; i++) r[i] = span[start + i];
            else for (int i = 0; i < count; i++) r[i] = v[start + i];
            return r;
        }

        static Vector4[] Widen(MeshNative m, IList<Vector3> v, int start, int count)
        {
            var r = m.NewChannelAtLeast<Vector4>(count);
            if (TrySpan(v, out var span)) for (int i = 0; i < count; i++) r[i] = span[start + i];
            else for (int i = 0; i < count; i++) r[i] = v[start + i];
            return r;
        }

        public void SetUVs(int channel, List<Vector2> uvs) => SetUvsList(channel, 2, uvs, 0, uvs?.Count ?? 0);
        public void SetUVs(int channel, List<Vector3> uvs) => SetUvsList(channel, 3, uvs, 0, uvs?.Count ?? 0);
        public void SetUVs(int channel, List<Vector4> uvs) => SetUvsList(channel, 4, uvs, 0, uvs?.Count ?? 0);
        public void SetUVs(int channel, List<Vector2> uvs, int start, int length) => SetUvsList(channel, 2, uvs, start, length);
        public void SetUVs(int channel, List<Vector3> uvs, int start, int length) => SetUvsList(channel, 3, uvs, start, length);
        public void SetUVs(int channel, List<Vector4> uvs, int start, int length) => SetUvsList(channel, 4, uvs, start, length);
        public void SetUVs(int channel, List<Vector2> uvs, int start, int length, MeshUpdateFlags flags) => SetUvsList(channel, 2, uvs, start, length);
        public void SetUVs(int channel, List<Vector3> uvs, int start, int length, MeshUpdateFlags flags) => SetUvsList(channel, 3, uvs, start, length);
        public void SetUVs(int channel, List<Vector4> uvs, int start, int length, MeshUpdateFlags flags) => SetUvsList(channel, 4, uvs, start, length);

        public void SetUVs(int channel, Vector2[] uvs) => SetUvsArray(channel, 2, uvs, 0, uvs?.Length ?? 0);
        public void SetUVs(int channel, Vector3[] uvs) => SetUvsArray(channel, 3, uvs, 0, uvs?.Length ?? 0);
        public void SetUVs(int channel, Vector4[] uvs) => SetUvsArray(channel, 4, uvs, 0, uvs?.Length ?? 0);
        public void SetUVs(int channel, Vector2[] uvs, int start, int length) => SetUvsArray(channel, 2, uvs, start, length);
        public void SetUVs(int channel, Vector3[] uvs, int start, int length) => SetUvsArray(channel, 3, uvs, start, length);
        public void SetUVs(int channel, Vector4[] uvs, int start, int length) => SetUvsArray(channel, 4, uvs, start, length);
        public void SetUVs(int channel, Vector2[] uvs, int start, int length, MeshUpdateFlags flags) => SetUvsArray(channel, 2, uvs, start, length);
        public void SetUVs(int channel, Vector3[] uvs, int start, int length, MeshUpdateFlags flags) => SetUvsArray(channel, 3, uvs, start, length);
        public void SetUVs(int channel, Vector4[] uvs, int start, int length, MeshUpdateFlags flags) => SetUvsArray(channel, 4, uvs, start, length);

        public Vector2[] uv { get => GetUv2(0); set => SetUvsImpl(0, 2, value, 0, value?.Length ?? 0); }
        public Vector2[] uv2 { get => GetUv2(1); set => SetUvsImpl(1, 2, value, 0, value?.Length ?? 0); }
        public Vector2[] uv3 { get => GetUv2(2); set => SetUvsImpl(2, 2, value, 0, value?.Length ?? 0); }
        public Vector2[] uv4 { get => GetUv2(3); set => SetUvsImpl(3, 2, value, 0, value?.Length ?? 0); }

        /// List overloads log an invalid channel; array overloads throw (as in Unity).
        void SetUvsList<T>(int channel, int dimension, List<T> values, int start, int count) where T : struct
        {
            if (channel < 0 || channel > 7)
            {
                Debug.LogError("The uv index is invalid. Must be in the range 0 to 7.");
                return;
            }
            SetUvsImpl(channel, dimension, values, start, count);
        }

        void SetUvsArray<T>(int channel, int dimension, T[] values, int start, int count) where T : struct
        {
            if (channel < 0 || channel > 7) throw new ArgumentOutOfRangeException("uvIndex", channel, "The uv index is invalid. Must be in the range 0 to 7.");
            SetUvsImpl(channel, dimension, values, start, count);
        }

        void SetUvsImpl<T>(int channel, int dimension, IList<T> values, int start, int count) where T : struct
        {
            if (!CanAccess("uv")) return;
            CheckRange(values?.Count ?? 0, start, count);
            if (!ChannelSizeOk("uv", count)) return;
            var m = Data;
            m.Retire(MeshNative.SlotUv0 + channel);
            m.Uvs[channel] = count == 0 ? null : values switch
            {
                IList<Vector2> v2 => Widen(m, v2, start, count),
                IList<Vector3> v3 => Widen(m, v3, start, count),
                IList<Vector4> v4 => Slice(m, v4, start, count),
                _ => throw new ArgumentException("UVs are Vector2, Vector3 or Vector4 values."),
            };
            m.Give(MeshNative.SlotUv0 + channel, m.Uvs.Raw[channel]);
            m.UvDimensions[channel] = count == 0 ? 0 : dimension;
            m.Changed();
        }

        public void GetUVs(int channel, List<Vector2> uvs) => GetUVsImpl(channel, uvs, v => v);
        public void GetUVs(int channel, List<Vector3> uvs) => GetUVsImpl(channel, uvs, v => v);
        public void GetUVs(int channel, List<Vector4> uvs) => GetUVsImpl(channel, uvs, v => v);

        void GetUVsImpl<T>(int channel, List<T> uvs, Func<Vector4, T> convert)
        {
            if (uvs == null) throw new ArgumentNullException(nameof(uvs), "The result uvs list cannot be null.");
            if (channel < 0 || channel > 7) throw new IndexOutOfRangeException("The uv index is invalid. Must be in the range 0 to 7.");
            uvs.Clear();
            if (!CanAccess("uv")) return;
            var values = Data.Uvs[channel];
            if (values == null) return;
            foreach (var v in values.AsSpan(0, Data.VertexCount)) uvs.Add(convert(v));
        }

        // ----- Indices -----

        public int subMeshCount
        {
            get => Data.SubMeshes.Count;
            set
            {
                if (value < 0)
                {
                    Debug.LogError("Mesh.subMeshCount can't be set to negative value.", this);
                    return;
                }
                var m = Data;
                while (m.SubMeshes.Count > value)
                {
                    m.RetireIndices(m.SubMeshes[^1]);
                    m.SubMeshes.RemoveAt(m.SubMeshes.Count - 1);
                }
                while (m.SubMeshes.Count < value) m.SubMeshes.Add(new SubMeshData());
                m.Changed();
            }
        }

        /// All triangle-topology submeshes' indices, base vertex applied.
        public int[] triangles
        {
            get
            {
                if (!CanAccess("triangles")) return Array.Empty<int>();
                var result = new List<int>();
                foreach (var s in Data.SubMeshes)
                    if (s.Topology == MeshTopology.Triangles)
                        foreach (int i in s.Indices) result.Add(i + s.BaseVertex);
                return result.ToArray();
            }
            set
            {
                if (!CanAccess("triangles")) return;
                if (!ValidateIndices(value ?? Array.Empty<int>(), MeshTopology.Triangles, 0)) return;
                var m = Data;
                foreach (var old in m.SubMeshes) m.RetireIndices(old);
                var s = new SubMeshData((int[])(value ?? Array.Empty<int>()).Clone());
                s.Bounds = m.SubMeshBounds(s);
                m.SubMeshes = new List<SubMeshData> { s };
                m.Changed();
            }
        }

        public int[] GetTriangles(int submesh) => GetTriangles(submesh, true);

        public int[] GetTriangles(int submesh, bool applyBaseVertex)
        {
            var s = SubMesh(submesh);
            if (s == null || !CanAccess("triangles") || s.Topology != MeshTopology.Triangles) return Array.Empty<int>();
            return Indices(s, applyBaseVertex);
        }

        public int[] GetIndices(int submesh) => GetIndices(submesh, true);

        public int[] GetIndices(int submesh, bool applyBaseVertex)
        {
            var s = SubMesh(submesh);
            if (s == null || !CanAccess("indices")) return Array.Empty<int>();
            return Indices(s, applyBaseVertex);
        }

        static int[] Indices(SubMeshData s, bool applyBaseVertex)
        {
            var result = s.Indices.ToArray();
            if (applyBaseVertex && s.BaseVertex != 0)
                for (int i = 0; i < result.Length; i++) result[i] += s.BaseVertex;
            return result;
        }

        public MeshTopology GetTopology(int submesh) => SubMesh(submesh)?.Topology ?? MeshTopology.Triangles;

        SubMeshData SubMesh(int submesh)
        {
            var m = Data;
            if (submesh < 0 || submesh >= m.SubMeshes.Count)
            {
                Debug.LogError("Failed getting triangles. Submesh index is out of bounds.", this);
                return null;
            }
            return m.SubMeshes[submesh];
        }

        public void SetTriangles(int[] triangles, int submesh) => SetIndicesImpl(triangles, MeshTopology.Triangles, submesh, true, 0);
        public void SetTriangles(int[] triangles, int submesh, bool calculateBounds) => SetIndicesImpl(triangles, MeshTopology.Triangles, submesh, calculateBounds, 0);
        public void SetTriangles(int[] triangles, int submesh, bool calculateBounds, int baseVertex) => SetIndicesImpl(triangles, MeshTopology.Triangles, submesh, calculateBounds, baseVertex);
        public void SetTriangles(List<int> triangles, int submesh) => SetIndicesImpl(AsSpan(triangles), MeshTopology.Triangles, submesh, true, 0);
        public void SetTriangles(List<int> triangles, int submesh, bool calculateBounds) => SetIndicesImpl(AsSpan(triangles), MeshTopology.Triangles, submesh, calculateBounds, 0);
        public void SetTriangles(List<int> triangles, int submesh, bool calculateBounds, int baseVertex) => SetIndicesImpl(AsSpan(triangles), MeshTopology.Triangles, submesh, calculateBounds, baseVertex);

        public void SetIndices(int[] indices, MeshTopology topology, int submesh) => SetIndicesImpl(indices, topology, submesh, true, 0);
        public void SetIndices(int[] indices, MeshTopology topology, int submesh, bool calculateBounds) => SetIndicesImpl(indices, topology, submesh, calculateBounds, 0);
        public void SetIndices(int[] indices, MeshTopology topology, int submesh, bool calculateBounds, int baseVertex) => SetIndicesImpl(indices, topology, submesh, calculateBounds, baseVertex);
        public void SetIndices(List<int> indices, MeshTopology topology, int submesh) => SetIndicesImpl(AsSpan(indices), topology, submesh, true, 0);
        public void SetIndices(List<int> indices, MeshTopology topology, int submesh, bool calculateBounds) => SetIndicesImpl(AsSpan(indices), topology, submesh, calculateBounds, 0);
        public void SetIndices(List<int> indices, MeshTopology topology, int submesh, bool calculateBounds, int baseVertex) => SetIndicesImpl(AsSpan(indices), topology, submesh, calculateBounds, baseVertex);

        /// A list's indices in place (they are copied once, into the submesh's array).
        static ReadOnlySpan<int> AsSpan(List<int> list) => list == null ? default : System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);

        void SetIndicesImpl(ReadOnlySpan<int> indices, MeshTopology topology, int submesh, bool calculateBounds, int baseVertex)
        {
            if (!CanAccess("indices")) return;
            var m = Data;
            if (submesh < 0 || submesh >= m.SubMeshes.Count)
            {
                Debug.LogError("Failed setting triangles. Submesh index is out of bounds.", this);
                return;
            }
            if (!ValidateIndices(indices, topology, baseVertex)) return;
            var s = m.SubMeshes[submesh];
            m.RetireIndices(s);
            if (indices.Length == 0) s.SetIndices(Array.Empty<int>());
            else
            {
                var array = m.NewChannelAtLeast<int>(indices.Length);
                indices.CopyTo(array);
                s.SetIndices(array, indices.Length);
                m.GiveIndices(s);
            }
            s.Topology = topology;
            s.BaseVertex = baseVertex;
            // The submesh's bounds only: the mesh's stay as they were (measured in the original
            // player, after setting them and after a RecalculateBounds alike).
            if (calculateBounds) s.Bounds = m.SubMeshBounds(s);
            m.Changed();
        }

        bool ValidateIndices(ReadOnlySpan<int> indices, MeshTopology topology, int baseVertex)
        {
            int per = topology switch { MeshTopology.Triangles => 3, MeshTopology.Quads => 4, MeshTopology.Lines => 2, _ => 1 };
            if (indices.Length % per != 0)
            {
                Debug.LogError(topology == MeshTopology.Triangles
                    ? "Failed setting triangles. The number of supplied triangle indices must be a multiple of 3."
                    : $"Failed setting indices. The number of supplied indices must be a multiple of {per}.", this);
                return false;
            }
            int vertexCount = Data.VertexCount;
            foreach (int i in indices)
                if (i + baseVertex < 0 || i + baseVertex >= vertexCount)
                {
                    Debug.LogError($"Failed setting triangles. Some indices are referencing out of bounds vertices. IndexCount: {indices.Length}, VertexCount: {vertexCount}", this);
                    return false;
                }
            return true;
        }
    }
}
