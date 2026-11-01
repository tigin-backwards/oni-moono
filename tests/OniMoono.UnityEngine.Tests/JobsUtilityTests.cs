using System;
using Unity.Jobs.LowLevel.Unsafe;
using Xunit;

namespace OniMoono.Tests
{
    /// JobsUtility's worker counts as the original reports them (tools/UnityProbe,
    /// PROBE_SCENARIO=atcall: 15 and 15 with 16 processors; 3, 1 and 0 kept; -1, -5, 1000 and
    /// 100000 refused, the count unchanged).
    public class JobsUtilityTests
    {
        [Fact]
        public void WorkerCountsFollowTheProcessorsAndRefuseOutOfRange()
        {
            int maximum = JobsUtility.JobWorkerMaximumCount;
            Assert.Equal(Math.Clamp(Environment.ProcessorCount - 1, 0, JobsUtility.MaxJobThreadCount - 1), maximum);
            Assert.Equal(maximum, JobsUtility.JobWorkerCount);
            try
            {
                JobsUtility.JobWorkerCount = 0;
                Assert.Equal(0, JobsUtility.JobWorkerCount);
                var e = Assert.Throws<ArgumentOutOfRangeException>(() => JobsUtility.JobWorkerCount = -1);
                Assert.Equal("JobWorkerCount", e.ParamName);
                Assert.StartsWith($"Invalid JobWorkerCount -1 must be in the range 0 -> {maximum}", e.Message);
                Assert.Throws<ArgumentOutOfRangeException>(() => JobsUtility.JobWorkerCount = maximum + 1);
                Assert.Equal(0, JobsUtility.JobWorkerCount);
            }
            finally
            {
                JobsUtility.ResetJobWorkerCount();
            }
            Assert.Equal(maximum, JobsUtility.JobWorkerCount);
            Assert.Equal(128, JobsUtility.MaxJobThreadCount);
            Assert.Equal(64, JobsUtility.CacheLineSize);
        }
    }
}
