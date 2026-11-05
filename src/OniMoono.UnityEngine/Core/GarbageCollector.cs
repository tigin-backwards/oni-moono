using System;
using System.Runtime;
using OniMoono.Engine;

namespace UnityEngine.Scripting
{
    public static partial class GarbageCollector
    {
        public static event Action<Mode> GCModeChanged;

        /// Unity's modes: Enabled collects automatically; Disabled performs no collection at all
        /// (System.GC.Collect included); Manual collects only on explicit GC.Collect. Setting a
        /// different mode raises GCModeChanged. See ManagedHeap for how this maps onto CoreCLR.
        public static Mode GCMode
        {
            get => ManagedHeap.Mode;
            set
            {
                if (value == ManagedHeap.Mode) return;
                ManagedHeap.SetMode(value);
                GCModeChanged?.Invoke(value);
            }
        }

        /// False: the original player's build does not use the incremental collector (measured,
        /// tools/UnityProbe PROBE_SCENARIO=statics), and the engine offers none.
        public static bool isIncremental => false;

        static ulong s_TimeSlice = 50000000;

        /// 50 ms at first, kept as set; with no incremental collector nothing reads it (the
        /// original's values, UnityProbe's apigaps "misc").
        public static ulong incrementalTimeSliceNanoseconds
        {
            get => s_TimeSlice;
            set => s_TimeSlice = value;
        }

        /// False and no work: the collector is not incremental (as in the original).
        public static bool CollectIncremental(ulong nanoseconds) => false;
    }
}

namespace Unity.Jobs.LowLevel.Unsafe
{
    public static partial class JobsUtility
    {
        /// Whether the calling thread is running a scripting job. The engine has no job system
        /// (nothing can schedule an IJob), so no thread ever is.
        public static bool IsExecutingJob => false;
    }
}

namespace OniMoono.Engine
{
    using UnityEngine.Scripting;

    /// GarbageCollector.GCMode on CoreCLR.
    ///
    /// CoreCLR cannot switch its collector off. The closest primitive is a no-GC region
    /// (GC.TryStartNoGCRegion): while it holds, the runtime performs no collection, which is
    /// exactly Unity's Disabled/Manual state. Its limits, and how they are handled:
    ///
    /// - The region has an allocation budget. We ask for a quarter of the memory available to the
    ///   process. When the budget is exhausted the runtime collects and leaves the region (Unity's
    ///   Boehm heap would instead keep growing). Entering a region may itself run one blocking
    ///   collection first to make room for the budget.
    /// - An explicit GC.Collect inside the region collects and ends it. In Manual mode that is
    ///   Unity's behaviour; in Disabled mode Unity would ignore the call, which cannot be
    ///   intercepted here.
    /// - After either, the region is entered again at the start of the next frame (EarlyUpdate),
    ///   so the mode keeps holding from frame to frame.
    ///
    /// Enabled ends the region and restores automatic collection.
    internal static class ManagedHeap
    {
        public static GarbageCollector.Mode Mode { get; private set; } = GarbageCollector.Mode.Enabled;
        static bool hooked;

        public static void SetMode(GarbageCollector.Mode mode)
        {
            Mode = mode;
            if (mode == GarbageCollector.Mode.Enabled)
            {
                EndRegion();
                return;
            }
            if (!hooked)
            {
                PlayerLoop.EarlyUpdate += Maintain;
                hooked = true;
            }
            StartRegion();
        }

        static void Maintain()
        {
            if (Mode != GarbageCollector.Mode.Enabled) StartRegion();
        }

        static void StartRegion()
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) return;
            long budget = Math.Max(16L << 20, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);
            // The runtime caps the budget by its heap configuration and may fail to commit it;
            // halve until it is accepted.
            for (; budget >= 16L << 20; budget /= 2)
            {
                try
                {
                    if (GC.TryStartNoGCRegion(budget)) return;
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
        }

        static void EndRegion()
        {
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion) return;
            try
            {
                GC.EndNoGCRegion();
            }
            catch (InvalidOperationException)
            {
                // The region already ended (a collection was induced); nothing to restore.
            }
        }
    }
}
