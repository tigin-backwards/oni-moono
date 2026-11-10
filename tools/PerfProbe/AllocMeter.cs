using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace PerfProbe
{
    /// `alloc.txt`: managed allocation counters, sampled every frame of the colony loop and logged
    /// (cumulative) after each frame-rate window as a `PERF alloc` line, the difference of two lines
    /// giving a span's rates (frames n, real time rt, game time gt):
    /// - mono: the runtime's own allocation total. In the original, Boehm's GC_get_total_bytes
    ///   (libmonobdwgc, found in /proc/self/maps): heap bytes as Boehm hands them out, rounded up to
    ///   its size classes with a byte added per object (calibration: a plain object 32 B, byte[100]
    ///   144 B). In ours, .NET's GC.GetTotalAllocatedBytes(true) (a plain object 24 B). Also the
    ///   collection number and Boehm's full-collection time (ms, -1 in ours);
    /// - hg: a heap-growth estimate, the same calls in both players: each frame's rise in
    ///   Profiler.GetMonoUsedSizeLong summed over frames without a collection (hgf; gcf with one),
    ///   to be scaled to all frames. Within 0.3% of .NET's counter in ours; about 0.6 of Boehm's in
    ///   the original, whose lazy sweep reuses freed slots without "used" rising;
    /// - rec/recn: Unity's "GC Allocated In Frame" and "GC Allocation In Frame Count" summed
    ///   (development players only; about 0.86 of Boehm's total: object sizes, main thread), and the
    ///   time of each GC.* profiler marker found (ns);
    /// - dnpause, dc: .NET's total GC pause (ms) and gen0/1/2 collection counts (ours).
    /// The calibration line at the start allocates known objects into a static (.NET stack-allocates
    /// objects that never escape) and logs what the counter saw for each.
    static class AllocMeter
    {
        [DllImport("libdl.so.2")] static extern IntPtr dlopen(string file, int mode);
        [DllImport("libdl.so.2")] static extern IntPtr dlsym(IntPtr handle, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate UIntPtr WordFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();

        static WordFn totalBytes, gcNo, fullGcTime;
        static readonly System.Reflection.MethodInfo dnAllocated = typeof(GC).GetMethod("GetTotalAllocatedBytes", new[] { typeof(bool) });
        static readonly System.Reflection.MethodInfo dnPause = typeof(GC).GetMethod("GetTotalPauseDuration", Type.EmptyTypes);
        static bool started;
        static long lastUsed; static int lastGc;
        static long hgSum, hgFrames, gcFrames, frames;
        static ProfilerRecorder allocRec, countRec;
        static long countSum;
        static long recSum, recFrames;
        static readonly List<(string name, ProfilerRecorder rec)> markers = new List<(string, ProfilerRecorder)>();
        static long[] markerNs = new long[0];
        static long[] markerFrames = new long[0];

        public static bool Mono => totalBytes != null;

        static bool native;
        static void EnsureNative()
        {
            if (native) return;
            native = true;
            try
            {
                string lib = null;
                foreach (var line in System.IO.File.ReadAllLines("/proc/self/maps"))
                {
                    int at = line.IndexOf('/');
                    if (at >= 0 && line.Contains("libmonobdwgc")) { lib = line.Substring(at).Trim(); break; }
                }
                if (lib != null)
                {
                    IntPtr h = dlopen(lib, 2 | 4); // RTLD_NOW | RTLD_NOLOAD: the runtime's own copy
                    T Fn<T>(string name) where T : class
                    {
                        IntPtr p = dlsym(h, name);
                        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
                    }
                    totalBytes = Fn<WordFn>("GC_get_total_bytes");
                    gcNo = Fn<WordFn>("GC_get_gc_no");
                    fullGcTime = Fn<WordFn>("GC_get_full_gc_total_time");
                    Fn<VoidFn>("GC_start_performance_measurement")?.Invoke();
                    Debug.Log($"PERF alloc mono {lib}: total {(totalBytes != null)} gcno {(gcNo != null)} fulltime {(fullGcTime != null)}");
                }
                else Debug.Log("PERF alloc no libmonobdwgc in the process");
            }
            catch (Exception e) { Debug.Log($"PERF alloc native lookup failed: {e}"); }
        }

        public static Func<long> MonoTotal() { EnsureNative(); return totalBytes == null ? null : (Func<long>)(() => (long)totalBytes().ToUInt64()); }

        public static void Start()
        {
            if (started) return;
            started = true;
            EnsureNative();
            Debug.Log($"PERF alloc unity {Application.unityVersion} debug {Debug.isDebugBuild} dotnet {(dnAllocated != null)}");
            if (dnAllocated == null)
            {
                try
                {
                    allocRec = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
                    countRec = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocation In Frame Count");
                    Debug.Log($"PERF alloc recorder GC Allocated In Frame valid {allocRec.Valid}, count {countRec.Valid}");
                    var handles = new List<ProfilerRecorderHandle>();
                    ProfilerRecorderHandle.GetAvailable(handles);
                    var names = new List<string>();
                    foreach (var handle in handles)
                    {
                        var d = ProfilerRecorderHandle.GetDescription(handle);
                        if (d.Name.IndexOf("GC", StringComparison.Ordinal) < 0) continue;
                        names.Add($"{d.Name} [{d.Category.Name} {d.UnitType} {d.DataType}]");
                        if (d.UnitType == ProfilerMarkerDataUnit.TimeNanoseconds && d.Name.StartsWith("GC.", StringComparison.Ordinal) && markers.Count < 24)
                            markers.Add((d.Name, new ProfilerRecorder(d.Category, d.Name, 1, ProfilerRecorderOptions.Default)));
                    }
                    Debug.Log($"PERF alloc {handles.Count} recorder handles; GC ones: {string.Join("; ", names)}");
                    markerNs = new long[markers.Count];
                    markerFrames = new long[markers.Count];
                }
                catch (Exception e) { Debug.Log($"PERF alloc recorders failed: {e.GetType().Name} {e.Message}"); }
            }
            lastUsed = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            lastGc = GC.CollectionCount(0);
        }

        static long Total() => totalBytes != null ? (long)totalBytes().ToUInt64() : dnAllocated != null ? (long)dnAllocated.Invoke(null, new object[] { true }) : -1;

        /// Known allocations, counted by the runtime's own total: a million plain objects, 100,000
        /// byte[100] and 2,000 byte[100000].
        static object sink; // a static: .NET stack-allocates objects that never escape
        public static void Calibrate()
        {
            long t0 = Total();
            for (int i = 0; i < 1000000; i++) sink = new object();
            long t1 = Total();
            for (int i = 0; i < 100000; i++) sink = new byte[100];
            long t2 = Total();
            for (int i = 0; i < 2000; i++) sink = new byte[100000];
            long t3 = Total();
            Debug.Log($"PERF alloc calibrate objects {(t1 - t0) / 1e6:F2} B each, byte[100] {(t2 - t1) / 1e5:F2} B each, byte[100000] {(t3 - t2) / 2e3:F1} B each (pointer {IntPtr.Size})");
        }

        public static void Frame()
        {
            if (!started) return;
            frames++;
            long used = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            int gc = GC.CollectionCount(0);
            if (gc == lastGc) { if (used > lastUsed) hgSum += used - lastUsed; hgFrames++; }
            else gcFrames++;
            lastUsed = used; lastGc = gc;
            if (allocRec.Valid) { recSum += allocRec.LastValue; recFrames++; }
            if (countRec.Valid) countSum += countRec.LastValue;
            for (int i = 0; i < markers.Count; i++)
            {
                long v = markers[i].rec.LastValue;
                markerNs[i] += v;
                if (v > 0) markerFrames[i]++;
            }
        }

        public static string Window()
        {
            if (!started) return "";
            var gt = GameClock.Instance.GetTime();
            string s = $"PERF alloc f{Time.frameCount} n {frames} rt {Time.realtimeSinceStartup:F3} gt {gt:F2} mono {Total()} gcno {(gcNo != null ? (long)gcNo().ToUInt64() : GC.CollectionCount(0))} fullms {(fullGcTime != null ? (long)fullGcTime().ToUInt64() : -1)}"
                + $" hg {hgSum} hgf {hgFrames} gcf {gcFrames} used {lastUsed} heap {UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong()} rec {recSum} recf {recFrames} recn {countSum}";
            if (dnAllocated != null)
                s += $" dnpause {(dnPause == null ? -1 : (long)((TimeSpan)dnPause.Invoke(null, null)).TotalMilliseconds)} dc {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}";
            for (int i = 0; i < markers.Count; i++)
                if (markerNs[i] > 0) s += $" [{markers[i].name}]={markerNs[i]}/{markerFrames[i]}";
            return s;
        }
    }
}
