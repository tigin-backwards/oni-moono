// FrameTimingManager, the Profiler's memory counters and SceneManager.GetSceneAt, as Workshop mods
// read them (System Performance Observer 3800923048, Cooperative Colonies 3824584976), lines
// "FT ...", 5 s into the main menu: IsFeatureEnabled, the timer frequencies, GetVSyncsPerSecond;
// for five frames CaptureFrameTimings then GetLatestTimings with 1 and 4 frames and every field
// of what they wrote (each array is filled with a marker first); GetLatestTimings with 0 frames;
// the Profiler's memory values (sign only:
// "neg", "zero" or "pos"); GetSceneAt for each loaded scene and one past either end. With
// PROBE_SCENARIO=frametiming only this probe runs; it ends with a FrameTimingProbeDone canvas.
using System.Collections;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class FrameTimings : MonoBehaviour
    {
        static void L(string s) => Rec.Log("FT " + s);

        static string Try(System.Func<string> f)
        {
            try { return f(); }
            catch (System.Exception e) { return "EXC " + e.GetType().Name + ": " + e.Message.Replace('\n', ' '); }
        }

        static string Sign(long v) => v < 0 ? "neg" : v == 0 ? "zero" : "pos";

        static FrameTiming Marker()
        {
            var t = new FrameTiming();
            t.cpuFrameTime = -7; t.cpuMainThreadFrameTime = -7; t.cpuMainThreadPresentWaitTime = -7;
            t.cpuRenderThreadFrameTime = -7; t.gpuFrameTime = -7; t.frameStartTimestamp = 7;
            t.firstSubmitTimestamp = 7; t.cpuTimePresentCalled = 7; t.cpuTimeFrameComplete = 7;
            t.heightScale = -7; t.widthScale = -7; t.syncInterval = 7;
            return t;
        }

        static string Show(FrameTiming t) =>
            "cpu=" + t.cpuFrameTime.ToString("R") + " main=" + t.cpuMainThreadFrameTime.ToString("R")
            + " wait=" + t.cpuMainThreadPresentWaitTime.ToString("R") + " render=" + t.cpuRenderThreadFrameTime.ToString("R")
            + " gpu=" + t.gpuFrameTime.ToString("R") + " start=" + t.frameStartTimestamp + " submit=" + t.firstSubmitTimestamp
            + " present=" + t.cpuTimePresentCalled + " complete=" + t.cpuTimeFrameComplete
            + " h=" + t.heightScale.ToString("R") + " w=" + t.widthScale.ToString("R") + " sync=" + t.syncInterval;

        static string Latest(uint n, int length)
        {
            var a = new FrameTiming[length];
            for (int i = 0; i < a.Length; i++) a[i] = Marker();
            uint got = FrameTimingManager.GetLatestTimings(n, a);
            var s = "returned " + got;
            for (int i = 0; i < a.Length; i++) s += " [" + i + "] " + Show(a[i]);
            return s;
        }

        IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(5f);
            L("begin");
            L("IsFeatureEnabled " + Try(() => FrameTimingManager.IsFeatureEnabled().ToString()));
            L("GetCpuTimerFrequency " + Try(() => FrameTimingManager.GetCpuTimerFrequency().ToString()));
            L("GetGpuTimerFrequency " + Try(() => FrameTimingManager.GetGpuTimerFrequency().ToString()));
            L("GetVSyncsPerSecond " + Try(() => FrameTimingManager.GetVSyncsPerSecond().ToString("R")));
            L("before capture latest(1) " + Try(() => Latest(1, 1)));
            for (int f = 0; f < 5; f++)
            {
                L("frame " + f + " capture " + Try(() => { FrameTimingManager.CaptureFrameTimings(); return "ok"; }));
                L("frame " + f + " latest(1) " + Try(() => Latest(1, 1)));
                L("frame " + f + " latest(4) " + Try(() => Latest(4, 4)));
                yield return null;
            }
            L("latest(0) " + Try(() => Latest(0, 1)));
            // Not asked: more frames than the array holds and a null array. In U60's Linux player
            // GetLatestTimings(4) into a one-element array returned 4 (it writes past the array),
            // and the null array after it ended the player with a segmentation fault in
            // FrameTimingManager::GetLatestTimings.

            L("GetTotalAllocatedMemoryLong " + Try(() => Sign(Profiler.GetTotalAllocatedMemoryLong())));
            L("GetTotalReservedMemoryLong " + Try(() => Sign(Profiler.GetTotalReservedMemoryLong())));
            L("GetTotalUnusedReservedMemoryLong " + Try(() => Sign(Profiler.GetTotalUnusedReservedMemoryLong())));
            L("GetMonoUsedSizeLong " + Try(() => Sign(Profiler.GetMonoUsedSizeLong())));
            L("GetMonoHeapSizeLong " + Try(() => Sign(Profiler.GetMonoHeapSizeLong())));
            L("usedHeapSizeLong " + Try(() => Sign(Profiler.usedHeapSizeLong)));
            L("GetAllocatedMemoryForGraphicsDriver " + Try(() => Sign(Profiler.GetAllocatedMemoryForGraphicsDriver())));
            L("Profiler.enabled " + Try(() => Profiler.enabled.ToString()));
            L("Profiler.supported " + Try(() => Profiler.supported.ToString()));

            L("sceneCount " + SceneManager.sceneCount + " active " + SceneManager.GetActiveScene().name);
            for (int i = -1; i <= SceneManager.sceneCount; i++)
            {
                int k = i;
                L("GetSceneAt(" + k + ") " + Try(() =>
                {
                    var s = SceneManager.GetSceneAt(k);
                    return "valid=" + s.IsValid() + " name=" + s.name + " loaded=" + s.isLoaded + " index=" + s.buildIndex
                        + " active=" + (s == SceneManager.GetActiveScene());
                }));
            }

            var done = new GameObject("FrameTimingProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("done");
        }
    }
}
