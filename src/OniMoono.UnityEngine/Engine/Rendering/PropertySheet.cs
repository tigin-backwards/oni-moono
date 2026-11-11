using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine.Rendering
{
    /// Shader.PropertyToID: one process-wide id per property name.
    internal static class ShaderPropertyIds
    {
        static readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
        static readonly List<string> names = new();

        public static int Get(string name)
        {
            name ??= "";
            lock (ids)
            {
                if (ids.TryGetValue(name, out int id)) return id;
                id = names.Count + 1;
                ids.Add(name, id);
                names.Add(name);
                return id;
            }
        }

        public static string NameOf(int id)
        {
            lock (ids) return id >= 1 && id <= names.Count ? names[id - 1] : null;
        }
    }

    /// A texture binding with its tiling: scale in xy, offset in zw (the "_ST" vector).
    internal struct TextureBinding
    {
        public Texture Texture;
        public Vector4 ScaleOffset;

        public static readonly Vector4 IdentityScaleOffset = new(1f, 1f, 0f, 0f);
    }

    /// Property values of a material, property block or the global shader state. Colours and
    /// vectors share one store, as do SetFloat and SetInt (Unity's SetInt writes a float).
    ///
    /// Each store is made at its first write: most sheets set one or two kinds (a block's
    /// textures and vectors), and the read accessors give a shared empty store until then.
    /// Writes go through the setters and the *ToWrite accessors, never through the read
    /// accessors (ONI_VERIFY_PROPERTY_SHEETS checks that the shared empty stores stay empty).
    internal sealed class PropertySheet
    {
        Dictionary<int, float> floats;
        Dictionary<int, int> ints;
        Dictionary<int, Vector4> vectors;
        Dictionary<int, Matrix4x4> matrices;
        Dictionary<int, TextureBinding> textures;
        Dictionary<int, float[]> floatArrays;
        Dictionary<int, Vector4[]> vectorArrays;
        Dictionary<int, Matrix4x4[]> matrixArrays;

        static class None<T>
        {
            public static readonly Dictionary<int, T> Store = new();
        }

        public Dictionary<int, float> Floats => floats ?? None<float>.Store;
        public Dictionary<int, int> Ints => ints ?? None<int>.Store;
        public Dictionary<int, Vector4> Vectors => vectors ?? None<Vector4>.Store;
        public Dictionary<int, Matrix4x4> Matrices => matrices ?? None<Matrix4x4>.Store;
        public Dictionary<int, TextureBinding> Textures => textures ?? None<TextureBinding>.Store;
        public Dictionary<int, float[]> FloatArrays => floatArrays ?? None<float[]>.Store;
        public Dictionary<int, Vector4[]> VectorArrays => vectorArrays ?? None<Vector4[]>.Store;
        public Dictionary<int, Matrix4x4[]> MatrixArrays => matrixArrays ?? None<Matrix4x4[]>.Store;

        public Dictionary<int, float> FloatsToWrite => floats ??= new();
        public Dictionary<int, int> IntsToWrite => ints ??= new();
        public Dictionary<int, Vector4> VectorsToWrite => vectors ??= new();
        public Dictionary<int, TextureBinding> TexturesToWrite => textures ??= new(textureCapacity);

        /// The size the texture store is made with: a new MaterialPropertyBlock's sheet is made
        /// for the textures of an animation batch (BlockTextureCapacity: its data textures and
        /// one or two atlases), which grew its store from 3 to 7 while the game set them.
        readonly int textureCapacity;

        public const int BlockTextureCapacity = 7;

        public PropertySheet() { }

        public PropertySheet(int textureCapacity) => this.textureCapacity = textureCapacity;

        /// Bumped on every change, so dependents (the device, TMP's CRC checks) can tell.
        public int Version;

        /// The render epoch until which a queued draw shares this sheet (MaterialPropertyBlock).
        public int SharedEpoch = -1;

        /// Counts rendered frames: draws queued during an epoch are consumed when it ends
        /// (FrameRenderer.EndFrame). Main thread only.
        public static int RenderEpoch;

        public bool IsEmpty => Empty(floats) && Empty(ints) && Empty(vectors) && Empty(matrices) && Empty(textures)
                               && Empty(floatArrays) && Empty(vectorArrays) && Empty(matrixArrays);

        static bool Empty<T>(Dictionary<int, T> store) => store == null || store.Count == 0;

        public void SetFloat(int id, float v) { (floats ??= new())[id] = v; Version++; }
        public void SetInt(int id, int v) { (ints ??= new())[id] = v; Version++; }
        public void SetVector(int id, Vector4 v) { (vectors ??= new())[id] = v; Version++; }
        public void SetMatrix(int id, Matrix4x4 v) { (matrices ??= new())[id] = v; Version++; }

        public void SetTexture(int id, Texture texture)
        {
            var store = TexturesToWrite;
            var binding = store.TryGetValue(id, out var b) ? b : new TextureBinding { ScaleOffset = TextureBinding.IdentityScaleOffset };
            binding.Texture = texture;
            store[id] = binding;
            Version++;
        }

        public void SetTextureScaleOffset(int id, Vector4 scaleOffset)
        {
            var store = TexturesToWrite;
            var binding = store.TryGetValue(id, out var b) ? b : default;
            binding.ScaleOffset = scaleOffset;
            store[id] = binding;
            Version++;
        }

        /// Arrays keep the first `count` elements; an array property's size is fixed by its first
        /// assignment (Unity: later, larger arrays are truncated to that size); a shorter one
        /// leaves the rest zero.
        public void SetFloatArray(int id, float[] values, int count)
        {
            floats?.Remove(id);
            CopyArray(floatArrays ??= new(), id, values, count);
            Version++;
        }

        public void SetVectorArray(int id, Vector4[] values, int count)
        {
            CopyArray(vectorArrays ??= new(), id, values, count);
            Version++;
        }

        public void SetMatrixArray(int id, Matrix4x4[] values, int count)
        {
            CopyArray(matrixArrays ??= new(), id, values, count);
            Version++;
        }

        /// The values go into the property's array, made at its first assignment and written over
        /// after that, as Unity copies them into the property's own storage: nothing outside the
        /// sheet holds the array (a queued draw shares a block's sheet, not its arrays alone,
        /// and a block shared with one moves to a copy before it changes; the device reads the
        /// values while it records draws, and copies of a sheet clone them), so a game that sets
        /// an array each frame (TerrainBG's background, 255 vectors) allocates nothing.
        static void CopyArray<T>(Dictionary<int, T[]> store, int id, T[] values, int count)
        {
            if (!store.TryGetValue(id, out var array)) store[id] = array = new T[count];
            int n = Math.Min(array.Length, count);
            Array.Copy(values, array, n);
            if (n < array.Length) Array.Clear(array, n, array.Length - n);
        }

        /// Empties the stores, keeping their storage (a recycled sheet fills them again).
        public void Clear()
        {
            floats?.Clear();
            ints?.Clear();
            vectors?.Clear();
            matrices?.Clear();
            textures?.Clear();
            floatArrays?.Clear();
            vectorArrays?.Clear();
            matrixArrays?.Clear();
            Version++;
        }

        public void CopyFrom(PropertySheet other)
        {
            Clear();
            Copy(other.floats, ref floats);
            Copy(other.ints, ref ints);
            Copy(other.vectors, ref vectors);
            Copy(other.matrices, ref matrices);
            Copy(other.textures, ref textures);
            if (!Empty(other.floatArrays)) foreach (var kv in other.floatArrays) (floatArrays ??= new())[kv.Key] = (float[])kv.Value.Clone();
            if (!Empty(other.vectorArrays)) foreach (var kv in other.vectorArrays) (vectorArrays ??= new())[kv.Key] = (Vector4[])kv.Value.Clone();
            if (!Empty(other.matrixArrays)) foreach (var kv in other.matrixArrays) (matrixArrays ??= new())[kv.Key] = (Matrix4x4[])kv.Value.Clone();
            Version++;
        }

        static void Copy<T>(Dictionary<int, T> from, ref Dictionary<int, T> to)
        {
            if (Empty(from)) return;
            to ??= new(from.Count);
            foreach (var kv in from) to[kv.Key] = kv.Value;
        }

        // ----- Recycling (MaterialPropertyBlock copies, per-frame sprite sheets) -----

        /// A block's sheet that a queued draw may still read is retired with the epoch it was
        /// shared in, not dropped. Draws queued in an epoch are drawn by the end of the next one
        /// (the queue of draws made after the frame's rendering is drawn by the next frame's, and
        /// the frame's queue is cleared when it ends), and nothing else keeps a retired sheet: the
        /// device reads sheets only while it records draws, on the main thread and the threads it
        /// records a queue with (the GL thread gets values copied out of them). So a sheet
        /// retired in epoch E is free once epoch E + 2 begins, and Rent hands it out again,
        /// cleared. Rent, Retire and EndEpoch lock (the setters may be called off the main
        /// thread by mods).
        static readonly Queue<(PropertySheet Sheet, int Epoch)> retired = new();
        static readonly Queue<(PropertySheet Sheet, int Freed)> free = new();
        static readonly object pool = new();
        const int MaxFree = 4096;

        /// ONI_VERIFY_PROPERTY_SHEETS=1: free sheets are held back 8 more epochs before they are
        /// handed out again, marked free meanwhile; a draw recorded with a free sheet, or a
        /// shared empty store that is not empty, is reported.
        public static readonly bool Verifying = Environment.GetEnvironmentVariable("ONI_VERIFY_PROPERTY_SHEETS") == "1";
        const int VerifyHold = 8;

        /// In the free list (verify mode only).
        public bool IsFree;

        static long rented, reused, retiredCount, checkedDraws, wrong;

        /// Sheets rented so far.
        public static long Rented { get { lock (pool) return rented; } }

        /// A cleared sheet: a free one, else a new one.
        public static PropertySheet Rent()
        {
            lock (pool)
            {
                rented++;
                if (free.Count > 0 && (!Verifying || free.Peek().Freed + VerifyHold <= RenderEpoch))
                {
                    var sheet = free.Dequeue().Sheet;
                    sheet.IsFree = false;
                    reused++;
                    return sheet;
                }
            }
            return new PropertySheet();
        }

        /// The sheet's holder lets go of it; queued draws of this epoch may still read it.
        public static void Retire(PropertySheet sheet)
        {
            lock (pool)
            {
                retired.Enqueue((sheet, RenderEpoch));
                retiredCount++;
            }
        }

        /// A sheet for this frame's draws only (a sprite's): retired at once.
        public static PropertySheet RentForFrame()
        {
            var sheet = Rent();
            Retire(sheet);
            return sheet;
        }

        /// A new epoch has begun (FrameRenderer.EndFrame): sheets retired two epochs ago are free.
        public static void EndEpoch()
        {
            lock (pool)
            {
                while (retired.Count > 0 && retired.Peek().Epoch + 2 <= RenderEpoch)
                {
                    var sheet = retired.Dequeue().Sheet;
                    if (free.Count >= MaxFree) continue;
                    sheet.Clear();
                    sheet.SharedEpoch = -1;
                    sheet.IsFree = Verifying;
                    free.Enqueue((sheet, RenderEpoch));
                }
            }
            if (!Verifying) return;
            if (None<float>.Store.Count + None<int>.Store.Count + None<Vector4>.Store.Count + None<Matrix4x4>.Store.Count + None<TextureBinding>.Store.Count
                + None<float[]>.Store.Count + None<Vector4[]>.Store.Count + None<Matrix4x4[]>.Store.Count != 0)
            {
                long n = System.Threading.Interlocked.Increment(ref wrong);
                EngineLog.Engine($"property sheets wrong ({n}): a shared empty store was written to");
                None<float>.Store.Clear(); None<int>.Store.Clear(); None<Vector4>.Store.Clear(); None<Matrix4x4>.Store.Clear();
                None<TextureBinding>.Store.Clear(); None<float[]>.Store.Clear(); None<Vector4[]>.Store.Clear(); None<Matrix4x4[]>.Store.Clear();
            }
            if (RenderEpoch % 600 == 0)
                EngineLog.Engine($"property sheets: {rented} rented, {reused} reused, {retiredCount} retired, {System.Threading.Interlocked.Read(ref checkedDraws)} draws checked, {System.Threading.Interlocked.Read(ref wrong)} wrong");
        }

        /// Verify mode: a draw is being recorded with `sheet` (any recording thread).
        public static void CheckDraw(PropertySheet sheet)
        {
            if (sheet == null) return;
            System.Threading.Interlocked.Increment(ref checkedDraws);
            if (!sheet.IsFree) return;
            long n = System.Threading.Interlocked.Increment(ref wrong);
            EngineLog.Engine($"property sheets wrong ({n}): a draw was recorded with a sheet that had been freed");
        }

        /// Content hash: equal sheets hash equally regardless of insertion order.
        public void Hash(Crc32 crc)
        {
            using var stream = new System.IO.MemoryStream();
            using (var w = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                void V(Vector4 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }
                void M(Matrix4x4 m) { for (int i = 0; i < 16; i++) w.Write(m[i]); }
                foreach (var id in Sorted(Floats.Keys)) { w.Write(id); w.Write(Floats[id]); }
                foreach (var id in Sorted(Ints.Keys)) { w.Write(id); w.Write(Ints[id]); }
                foreach (var id in Sorted(Vectors.Keys)) { w.Write(id); V(Vectors[id]); }
                foreach (var id in Sorted(Matrices.Keys)) { w.Write(id); M(Matrices[id]); }
                foreach (var id in Sorted(Textures.Keys))
                {
                    var t = Textures[id];
                    w.Write(id);
                    w.Write(t.Texture != null ? t.Texture.GetInstanceID() : 0);
                    V(t.ScaleOffset);
                }
                foreach (var id in Sorted(FloatArrays.Keys)) { w.Write(id); foreach (var f in FloatArrays[id]) w.Write(f); }
                foreach (var id in Sorted(VectorArrays.Keys)) { w.Write(id); foreach (var v in VectorArrays[id]) V(v); }
                foreach (var id in Sorted(MatrixArrays.Keys)) { w.Write(id); foreach (var m in MatrixArrays[id]) M(m); }
            }
            crc.Append(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        }

        static int[] Sorted(IEnumerable<int> keys)
        {
            var list = new List<int>(keys);
            list.Sort();
            return list.ToArray();
        }
    }

    /// Global shader properties and keywords (Shader.SetGlobal*, Shader.EnableKeyword).
    internal static class ShaderGlobals
    {
        public static readonly PropertySheet Properties = new();
        public static readonly KeywordSet Keywords = new();
        public static int MaximumLOD = int.MaxValue;
    }

    /// An ordered set of enabled shader keywords.
    internal sealed class KeywordSet
    {
        readonly List<string> list = new();
        public int Version;

        public bool Contains(string keyword) => list.Contains(keyword);

        public void Enable(string keyword)
        {
            if (string.IsNullOrEmpty(keyword) || list.Contains(keyword)) return;
            list.Add(keyword);
            Version++;
        }

        public void Disable(string keyword)
        {
            if (list.Remove(keyword)) Version++;
        }

        public string[] ToArray() => list.ToArray();

        /// Replaces the set; Unity drops empty names and duplicates.
        public void Set(IEnumerable<string> keywords)
        {
            list.Clear();
            if (keywords != null)
                foreach (var k in keywords)
                    if (!string.IsNullOrEmpty(k) && !list.Contains(k)) list.Add(k);
            Version++;
        }

        public IReadOnlyList<string> Items => list;
    }
}
