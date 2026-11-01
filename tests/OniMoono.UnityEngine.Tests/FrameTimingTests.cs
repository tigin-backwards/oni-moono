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
    // read, as UnityProbe's frametiming measured it in the original U59 Linux player.
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
        public void FrameTimingsAreOffAsInU59()
        {
            Assert.False(FrameTimingManager.IsFeatureEnabled());
            Assert.Equal(0f, FrameTimingManager.GetVSyncsPerSecond());
            Assert.Equal(1000000000UL, FrameTimingManager.GetGpuTimerFrequency());
            var marker = new FrameTiming { cpuFrameTime = -7, frameStartTimestamp = 7, widthScale = -7 };
            var t = new[] { marker, marker, marker, marker };
            for (int i = 0; i < 4; i++)
            {
                FrameTimingManager.CaptureFrameTimings();
                Frame();
            }
            // Nothing is recorded, and the array is left as it was.
            Assert.Equal(0u, FrameTimingManager.GetLatestTimings(4, t));
            Assert.Equal(0u, FrameTimingManager.GetLatestTimings(1, t));
            Assert.Equal(0u, FrameTimingManager.GetLatestTimings(0, t));
            Assert.All(t, x => Assert.Equal(marker, x));
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
