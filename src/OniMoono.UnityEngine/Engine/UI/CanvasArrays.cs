using System;
using System.Collections.Generic;
using OniMoono.Engine.Rendering;

namespace OniMoono.Engine.UI
{
    /// Arrays that canvas renderers' geometry copies (CanvasGeometry) let go of, shared by every
    /// renderer: a copy takes one of the exact length it needs from whichever renderer gave it.
    /// The renderer's own baked mesh kept them before, for 4 frames: most elements rebuild less
    /// often than that (text redrawn when its value changes, a few times a second), and one that
    /// changes length never found its own (a late-game colony made about 11 arrays a frame).
    ///
    /// An array given back may still be in uploads of the mesh it was baked into: it is handed
    /// out only once that mesh's device has run every upload posted before it was given (as
    /// MeshNative's own spares are), and is dropped once unused for `KeptFrames` frames. Main
    /// thread only. ONI_VERIFY_MESH_REUSE's upload checksums cover it: an array handed out too
    /// early and filled shows as a difference.
    internal static class CanvasArrays
    {
        const int KeptFrames = 120, MaxPerLength = 32;

        static readonly List<Action<int>> sweeps = new();

        /// Arrays wanted and taken from the pool (ONI_MESH_SPARES=stats, the verify report).
        internal static long Wanted, Taken;

        /// An array of exactly `length` for a geometry copy (its contents are the caller's to fill).
        public static T[] Take<T>(int length) where T : struct => length == 0 ? Array.Empty<T>() : Pool<T>.Take(length);

        /// `array` (a released geometry's) is no copy's any more. `owner` is the mesh whose uploads
        /// may hold it, null when it was in none. A shared uniform array stays out.
        public static void Give(Array array, MeshNative owner)
        {
            if (array == null || array.Length == 0 || MeshNative.ReuseOff || shared.TryGetValue(array, out _)) return;
            switch (array)
            {
                case UnityEngine.Vector3[] a: Pool<UnityEngine.Vector3>.Give(a, owner); break;
                case UnityEngine.Vector4[] a: Pool<UnityEngine.Vector4>.Give(a, owner); break;
                case UnityEngine.Color[] a: Pool<UnityEngine.Color>.Give(a, owner); break;
                case int[] a: Pool<int>.Give(a, owner); break;
            }
        }

        /// A copy of `values` (VertexCount values, every one of the same bits: uGUI's unused UV
        /// sets, its default normal and tangent, a plain image's colour) as an array shared by
        /// every copy of that length and value, or null when they differ. Copies are never
        /// written once made, so they can share one; a shared array never goes back to the pool.
        public static T[] Uniform<T>(ReadOnlySpan<T> values) where T : unmanaged
            => IsUniform(values) ? Shared(values[0], values.Length) : null;

        /// At least 4 values, every one of the same bits (and reuse on).
        public static bool IsUniform<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            if (values.Length < 4 || MeshNative.ReuseOff) return false;
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<T, uint>(values);
            int k = words.Length / values.Length;
            // Every value equals the one before it: the words repeat with the value's length.
            return k > 0 && words.Length == k * values.Length && words[k..].SequenceEqual(words[..^k]);
        }

        /// The shared array of `length` values of `value`'s bits (see Uniform).
        public static T[] Shared<T>(T value, int length) where T : struct => Pool<T>.Shared(value, length);

        /// Arrays handed out by Uniform, which stay out of the pool for as long as they live.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Array, object> shared = new();

        /// Drops arrays unused for KeptFrames (start of each frame).
        public static void Sweep()
        {
            int frame = UnityEngine.Time.frameCount;
            foreach (var sweep in sweeps) sweep(frame);
        }

        /// Arrays held, for tests.
        internal static int Count<T>() where T : struct => Pool<T>.Held;

        static class Pool<T> where T : struct
        {
            // By length; each list oldest first.
            static readonly Dictionary<int, List<(T[] Array, MeshNative Owner, int Posted, int Frame)>> byLength = new();
            static readonly List<int> emptied = new();
            internal static int Held;
            // Uniform arrays by length, each with the frame it was last handed out.
            static readonly Dictionary<int, List<(T[] Array, int Frame)>> uniform = new();

            public static T[] Shared(T value, int length)
            {
                int frame = UnityEngine.Time.frameCount;
                if (!uniform.TryGetValue(length, out var list)) uniform[length] = list = new();
                var bits = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value));
                for (int i = 0; i < list.Count; i++)
                {
                    var array = list[i].Array;
                    if (!System.Runtime.InteropServices.MemoryMarshal.AsBytes(array.AsSpan(0, 1)).SequenceEqual(bits)) continue;
                    list[i] = (array, frame);
                    return array;
                }
                var made = new T[length];
                made.AsSpan().Fill(value);
                shared.Add(made, null);
                list.Add((made, frame));
                return made;
            }

            static Pool() => sweeps.Add(Sweep);

            public static T[] Take(int length)
            {
                if (MeshNative.Stats) Wanted++;
                if (!MeshNative.ReuseOff && byLength.TryGetValue(length, out var list))
                    // The newest free one: the arrays of an element rebuilt every frame come back to it.
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var (array, owner, posted, _) = list[i];
                        if (owner != null && posted > System.Threading.Volatile.Read(ref owner.UploadsRan)) continue;
                        list.RemoveAt(i);
                        Held--;
                        if (MeshNative.Stats) Taken++;
                        // What a copy fails to write shows as NaNs (verify mode).
                        if (MeshNative.VerifyReuse) System.Runtime.InteropServices.MemoryMarshal.AsBytes(array.AsSpan()).Fill(0xFF);
                        return array;
                    }
                return new T[length];
            }

            public static void Give(T[] array, MeshNative owner)
            {
                if (!byLength.TryGetValue(array.Length, out var list)) byLength[array.Length] = list = new();
                if (list.Count >= MaxPerLength)
                {
                    list.RemoveAt(0);
                    Held--;
                }
                list.Add((array, owner, owner?.UploadsPosted ?? 0, UnityEngine.Time.frameCount));
                Held++;
            }

            static void Sweep(int frame)
            {
                if (uniform.Count > 0)
                {
                    foreach (var (length, list) in uniform)
                    {
                        int kept = 0;
                        for (int i = 0; i < list.Count; i++)
                            if (frame - list[i].Frame <= KeptFrames) list[kept++] = list[i];
                        list.RemoveRange(kept, list.Count - kept);
                        if (list.Count == 0) emptied.Add(length);
                    }
                    foreach (var length in emptied) uniform.Remove(length);
                    emptied.Clear();
                }
                if (Held == 0) return;
                foreach (var (length, list) in byLength)
                {
                    int stale = 0;
                    while (stale < list.Count && frame - list[stale].Frame > KeptFrames) stale++;
                    if (stale == 0) continue;
                    list.RemoveRange(0, stale);
                    Held -= stale;
                    if (list.Count == 0) emptied.Add(length);
                }
                foreach (var length in emptied) byLength.Remove(length);
                emptied.Clear();
            }
        }
    }
}
