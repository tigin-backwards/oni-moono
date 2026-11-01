using System;

namespace UnityEngine.Profiling
{
    /// Profiler samples record nothing: a player without an attached profiler. The game's release
    /// build compiles its calls out; a mod built with ENABLE_PROFILER still makes them.
    ///
    /// The memory counters mods log (System Performance Observer, Cooperative Colonies) are all
    /// positive in the original player (UnityProbe's frametiming). Here the process's resident
    /// memory stands for what the engine allocated, and the managed heap for Mono's.
    public static partial class Profiler
    {
        public static void BeginSample(string name) { }
        public static void EndSample() { }

        public static long GetTotalAllocatedMemoryLong() => Environment.WorkingSet;

        /// What is allocated, and the managed heap's committed space not in use.
        public static long GetTotalReservedMemoryLong()
        {
            var gc = GC.GetGCMemoryInfo();
            return Environment.WorkingSet + Math.Max(0L, gc.TotalCommittedBytes - gc.HeapSizeBytes);
        }

        public static long GetMonoUsedSizeLong() => GC.GetTotalMemory(false);

        public static long GetMonoHeapSizeLong() => Math.Max(GC.GetGCMemoryInfo().TotalCommittedBytes, GC.GetTotalMemory(false));
    }
}
