using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Xunit;

namespace OniMoono.Tests
{
    // What System Performance Observer (Workshop 3800923048) and Cooperative Colonies (3824584976)
    // read, as UnityProbe's frametiming measured it in the original U60 Linux player.
    public class FrameTimingTests : EngineTest
    {
        [Fact]
        public void FrameTimingHasUnitysFields()
        {
            Assert.Equal(
                ["cpuFrameTime", "cpuMainThreadFrameTime", "cpuMainThreadPresentWaitTime", "cpuRenderThreadFrameTime", "gpuFrameTime",
                 "frameStartTimestamp", "firstSubmitTimestamp", "cpuTimePresentCalled", "cpuTimeFrameComplete", "heightScale", "widthScale", "syncInterval"],
                typeof(FrameTiming).GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name));
        }

        [Fact]
        public void LatestTimingsAreTheCompletedFramesNewestFirst()
        {
            Assert.True(FrameTimingManager.IsFeatureEnabled());
            Assert.Equal(0f, FrameTimingManager.GetVSyncsPerSecond());
            for (int i = 0; i < 4; i++) Frame();
            // Recorded without CaptureFrameTimings, as in the original.
            var t = new FrameTiming[3];
            Assert.Equal(3u, FrameTimingManager.GetLatestTimings(3, t));
            Assert.True(t[0].frameStartTimestamp > t[1].frameStartTimestamp && t[1].frameStartTimestamp > t[2].frameStartTimestamp);
            double ms = 1000.0 / FrameTimingManager.GetCpuTimerFrequency();
            for (int i = 0; i < 3; i++)
            {
                Assert.True(t[i].cpuFrameTime > 0);
                Assert.True(t[i].cpuMainThreadFrameTime <= t[i].cpuFrameTime);
                Assert.Equal(t[i].cpuFrameTime, t[i].cpuMainThreadFrameTime + t[i].cpuMainThreadPresentWaitTime, 9);
                Assert.Equal(0.0, t[i].gpuFrameTime);
                Assert.Equal(1f, t[i].widthScale);
                Assert.True(t[i].cpuTimePresentCalled >= t[i].firstSubmitTimestamp && t[i].firstSubmitTimestamp >= t[i].frameStartTimestamp);
            }
            // A sample's frame runs to the next one's start.
            Assert.Equal((t[1].frameStartTimestamp + t[1].cpuFrameTime / ms), t[0].frameStartTimestamp, 0);
            Assert.Equal(0u, FrameTimingManager.GetLatestTimings(0, t));
            Assert.Equal(1u, FrameTimingManager.GetLatestTimings(4, new FrameTiming[1]));
            Assert.Equal(0u, FrameTimingManager.GetLatestTimings(1, null));
        }

        [Fact]
        public void TheProfilersMemoryCountersArePositive()
        {
            Assert.True(Profiler.GetTotalAllocatedMemoryLong() > 0);
            Assert.True(Profiler.GetTotalReservedMemoryLong() >= Profiler.GetTotalAllocatedMemoryLong());
            Assert.True(Profiler.GetMonoUsedSizeLong() > 0);
            Assert.True(Profiler.GetMonoHeapSizeLong() > 0);
        }

        [Fact]
        public void GetSceneAtListsTheLoadedScenes()
        {
            Assert.Contains(SceneManager.GetActiveScene(), Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt));
            var e = Assert.Throws<IndexOutOfRangeException>(() => SceneManager.GetSceneAt(SceneManager.sceneCount));
            Assert.Equal($"Scene index \"{SceneManager.sceneCount}\" is out of range.", e.Message);
            Assert.Throws<IndexOutOfRangeException>(() => SceneManager.GetSceneAt(-1));
        }
    }
}
