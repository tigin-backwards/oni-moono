using System;

namespace Unity.Jobs.LowLevel.Unsafe
{
    /// The job system's worker counts. The engine has no job system, so the counts only report:
    /// measured in the original player (tools/UnityProbe, PROBE_SCENARIO=atcall) with a boot.config
    /// that sets no `job-worker-count`, 16 logical processors gave a maximum and a default count of
    /// 15. ONI Boost Kit's GCSentinel reads JobWorkerCount for its status line.
    public static partial class JobsUtility
    {
        /// Literal fields in the original's metadata.
        public const int MaxJobThreadCount = 128;
        public const int CacheLineSize = 64;

        static int workerCount = -1;

        /// One fewer than the logical processors, as measured.
        public static int JobWorkerMaximumCount => Math.Clamp(Environment.ProcessorCount - 1, 0, MaxJobThreadCount - 1);

        /// The maximum unless set. Setting a value outside 0 to the maximum throws, as the original
        /// does ("Invalid JobWorkerCount -1 must be in the range 0 -> 15", parameter
        /// JobWorkerCount), and leaves the count as it was.
        public static int JobWorkerCount
        {
            get => workerCount < 0 ? JobWorkerMaximumCount : workerCount;
            set
            {
                int maximum = JobWorkerMaximumCount;
                if (value < 0 || value > maximum)
                    throw new ArgumentOutOfRangeException(nameof(JobWorkerCount), $"Invalid JobWorkerCount {value} must be in the range 0 -> {maximum}");
                workerCount = value;
            }
        }

        /// Back to the default count.
        public static void ResetJobWorkerCount() => workerCount = -1;
    }
}
