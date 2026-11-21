using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace UnpauseProbeTestMod
{
    /// Times a loaded colony's frames from loading through play while the camera pans, the same in
    /// both players (tests/e2e/unpause-probe.yaml): a measurement, not a check. After `Still`
    /// paused frames at the save's view, the probe unpauses and pans the camera left and right
    /// `Step` cells a frame, `Reach` cells either side of that view (as #105's reporter did in
    /// play): `PanFrames` frames each at speed 1 and ortho 20, speed 1 and ortho 50 (screenshot
    /// mode, past the normal maximum), speed 3 and ortho 20, speed 3 and ortho 50. Each frame
    /// from the colony's first (Game.OnSpawn) logs an `Unpause:` line: its phase and camera,
    /// whether the visible animation chunks changed (CROSS), paused or not, the frame's real time
    /// (between two Game.Update calls), the inclusive times of the game's parts below, the JIT's
    /// work in the frame (on .NET: compile time on all threads and on the main thread, methods
    /// compiled) and the GC's (collections, pause). Then summary lines by phase: the first ten
    /// frames summed, and the crossings and the other frames past the first 60 (median and max).
    public sealed class Mod : UserMod2
    {
        const int Still = 60, PanFrames = 384, Cols = 12;
        const float Step = 3f, Reach = 96f;

        public static int Done;

        static readonly List<(string name, MethodBase m)> parts = new List<(string, MethodBase)>();
        static readonly Dictionary<MethodBase, int> slot = new Dictionary<MethodBase, int>();
        static long[] times;
        static int[] calls;

        static bool recording;
        static int frame = -1, unpausedAt = -1, firstFrame;
        static long lastStamp;
        static readonly List<double[]> rows = new List<double[]>();

        static MethodInfo jitTime, jitCount, pauseDuration;
        // The harness's own hitch: E2eDriver's SyntheticInput re-attaches every 60 frames with
        // FindObjectsOfType (150-190 ms on a late-game colony, in both players); held off while
        // recording, as the probe sends no input.
        static FieldInfo attachedFrame;
        static double jitAll, jitMain, gcPause;
        static long jitMethods;
        static int gc0, gc1, gc2;

        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            Part(harmony, "Game", typeof(Game), "Update");
            Part(harmony, "SimTick", typeof(Game), "SimEveryTick");
            Part(harmony, "U200", typeof(Game), "UnsafeSim200ms");
            Part(harmony, "Step", typeof(Game), "StepTheSim");
            Part(harmony, "CellInfo", typeof(World), "UpdateCellInfo");
            Part(harmony, "Nav", typeof(Pathfinding), "UpdateNavGrids");
            Part(harmony, "SubTick", typeof(StateMachineUpdater), "AdvanceOneSimSubTick");
            Part(harmony, "Ccm", typeof(CellChangeMonitor), "RenderEveryTick");
            Part(harmony, "Elec", typeof(CircuitManager), "RenderEveryTick");
            Part(harmony, "Logic", typeof(LogicCircuitManager), "RenderEveryTick");
            Part(harmony, "Prober", typeof(AsyncPathProber), "TickFrame");
            Part(harmony, "GameLate", typeof(Game), "LateUpdate");
            Part(harmony, "SmRender", typeof(StateMachineUpdater), "Render");
            Part(harmony, "SmRet", typeof(StateMachineUpdater), "RenderEveryTick");
            Part(harmony, "Brain", typeof(BrainScheduler), "RenderEveryTick");
            Part(harmony, "Kbau", typeof(KBatchedAnimUpdater), "LateUpdate");
            Part(harmony, "Vis", typeof(KBatchedAnimUpdater), "UpdateVisibility");
            Part(harmony, "WorldLate", typeof(World), "LateUpdate");
            Part(harmony, "Active", typeof(KAnimBatchManager), "UpdateActiveArea");
            Part(harmony, "BatchDirty", typeof(KAnimBatchManager), "UpdateDirty");
            Part(harmony, "BatchRender", typeof(KAnimBatchManager), "Render");
            Part(harmony, "Ground", typeof(GroundRenderer), "Render");
            Part(harmony, "PropTex", typeof(PropertyTextures), "LateUpdate");
            Part(harmony, "Canvas", AccessTools.TypeByName("UnityEngine.UI.CanvasUpdateRegistry"), "PerformUpdate");
            times = new long[parts.Count];
            calls = new int[parts.Count];
            // The sim sub-tick's buckets, each timed: the same loop as BucketGroup.AdvanceOneSubTick
            // (the same calls in the same order), so a slow sub-tick names its buckets.
            harmony.Patch(AccessTools.Method(typeof(StateMachineUpdater.BucketGroup), "AdvanceOneSubTick"), prefix: new HarmonyMethod(typeof(Mod), nameof(TimedSubTick)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), postfix: new HarmonyMethod(typeof(Mod), nameof(Spawned)));
            harmony.Patch(AccessTools.Method(typeof(KBatchedAnimUpdater), "LateUpdate"), postfix: new HarmonyMethod(typeof(Mod), nameof(Drive)) { priority = Priority.Last });
            var jit = System.Type.GetType("System.Runtime.JitInfo");
            jitTime = jit?.GetMethod("GetCompilationTime", new[] { typeof(bool) });
            jitCount = jit?.GetMethod("GetCompiledMethodCount", new[] { typeof(bool) });
            pauseDuration = typeof(System.GC).GetMethod("GetTotalPauseDuration", System.Type.EmptyTypes);
            Debug.Log($"Unpause: installed, {parts.Count} parts, JIT counters {(jitTime != null ? "on" : "off")}");
        }

        static void Part(Harmony harmony, string name, System.Type type, string method)
        {
            var m = type == null ? null : AccessTools.Method(type, method);
            if (m == null) { Debug.Log($"Unpause: no method {name}"); return; }
            slot[m] = parts.Count;
            parts.Add((name, m));
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(Mod), nameof(Pre)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(Mod), nameof(Post)) { priority = Priority.Last });
        }

        static void Spawned()
        {
            recording = true;
            firstFrame = UnityEngine.Time.frameCount;
            Snapshot(out _, out _, out _, out _, out _, out _, out _);
            lastStamp = 0;
            var input = AccessTools.TypeByName("E2eDriver.SyntheticInput");
            attachedFrame = input == null ? null : AccessTools.Field(input, "attachedFrame");
            Debug.Log($"Unpause: colony spawned at frame {firstFrame}, E2eDriver re-attach held off: {attachedFrame != null}");
        }

        // The pan: frames driven since the colony's first, the phase the current frame is in
        // (0 still, then speed 1 at ortho 20 and 50, speed 3 at ortho 20 and 50) and the save's
        // view, the pan's centre.
        static int driven;
        static int phase;
        static int phaseFrame;
        static Vector3 centre;
        static Vector2I lastMin, lastMax;
        static bool crossed;
        static readonly string[] phaseNames = { "still", "s1o20", "s1o50", "s3o20", "s3o50" };

        /// After the anim updater's LateUpdate: the phase, the speed and the next frame's camera.
        static void Drive()
        {
            if (!recording || Done != 0) return;
            var cc = CameraController.Instance;
            var speed = SpeedControlScreen.Instance;
            if (cc == null || Camera.main == null || speed == null) return;
            if (driven == 0) centre = Camera.main.transform.position;
            int k = driven++;
            int next = k < Still ? 0 : 1 + (k - Still) / PanFrames;
            if (next > 4) { Finish(); return; }
            if (next != phase)
            {
                phaseFrame = 0;
                bool zoomedOut = next == 2 || next == 4;
                if (zoomedOut != DebugHandler.ScreenshotMode) DebugHandler.ToggleScreenshotMode();
                speed.SetSpeed(next <= 2 ? 0 : 2);
            }
            phase = next;
            if (phase == 0) return;
            // Each pause the harness holds (its load dialogs) takes an Unpause of its own.
            if (speed.IsPaused) speed.Unpause(false);
            cc.SnapTo(new Vector3(centre.x + Sweep(k - Still), centre.y, centre.z), phase == 2 || phase == 4 ? 50f : 20f);
        }

        /// The pan's offset at frame i: 0, left to -Reach, right to +Reach, back, Step a frame.
        static float Sweep(int i)
        {
            float s = (Step * i) % (4 * Reach);
            return s < Reach ? -s : s < 3 * Reach ? s - 2 * Reach : 4 * Reach - s;
        }

        static readonly Dictionary<StateMachineUpdater.BaseUpdateBucket, (double ms, int calls, int entries, double jit)> buckets = new Dictionary<StateMachineUpdater.BaseUpdateBucket, (double, int, int, double)>();
        static readonly Dictionary<StateMachineUpdater.BaseUpdateBucket, string> rates = new Dictionary<StateMachineUpdater.BaseUpdateBucket, string>();

        static readonly AccessTools.FieldRef<StateMachineUpdater.BucketGroup, List<List<StateMachineUpdater.BaseUpdateBucket>>> bucketFrames =
            AccessTools.FieldRefAccess<StateMachineUpdater.BucketGroup, List<List<StateMachineUpdater.BaseUpdateBucket>>>("bucketFrames");
        static readonly AccessTools.FieldRef<StateMachineUpdater.BucketGroup, int> nextUpdateIndex =
            AccessTools.FieldRefAccess<StateMachineUpdater.BucketGroup, int>("nextUpdateIndex");

        static bool TimedSubTick(StateMachineUpdater.BucketGroup __instance, float dt)
        {
            if (!recording) return true;
            var frames = bucketFrames(__instance);
            ref int next = ref nextUpdateIndex(__instance);
            var list = frames[next];
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                var bucket = list[i];
                int entries = bucket.count;
                if (entries == 0) continue;
                double jit0 = MainJit();
                long start = Stopwatch.GetTimestamp();
                bucket.Update(dt);
                double ms = Ms(Stopwatch.GetTimestamp() - start);
                double jit = MainJit() - jit0;
                buckets.TryGetValue(bucket, out var b);
                buckets[bucket] = (b.ms + ms, b.calls + 1, b.entries + entries, b.jit + jit);
                if (!rates.ContainsKey(bucket)) rates[bucket] = __instance.updateRate.ToString();
            }
            next = (next + 1) % frames.Count;
            return false;
        }

        static double MainJit() => jitTime != null ? ((System.TimeSpan)jitTime.Invoke(null, new object[] { true })).TotalMilliseconds : 0;

        /// A slow sub-tick frame's costliest buckets: rate, name, data type, ms, calls, entries,
        /// and the main thread's JIT time inside them.
        static void TopBuckets()
        {
            double total = 0;
            foreach (var b in buckets.Values) total += b.ms;
            if (total >= 100)
            {
                var list = new List<KeyValuePair<StateMachineUpdater.BaseUpdateBucket, (double ms, int calls, int entries, double jit)>>(buckets);
                list.Sort((a, b) => b.Value.ms.CompareTo(a.Value.ms));
                var sb = new StringBuilder($"Unpause: f={frame} buckets {total:F1} ms in {buckets.Count}:");
                for (int i = 0; i < list.Count && i < 15; i++)
                {
                    var k = list[i].Key;
                    var v = list[i].Value;
                    var type = k.GetType();
                    string data = type.IsGenericType ? type.GetGenericArguments()[0].FullName : type.Name;
                    sb.Append($" [{rates[k]} {k.name} <{data}> {v.ms:F1} ms x{v.calls} n={v.entries} jit={v.jit:F1}]");
                }
                Debug.Log(sb.ToString());
            }
            buckets.Clear();
        }

        static void Pre(out long __state)
        {
            if (!recording) { __state = 0; return; }
            Roll();
            __state = Stopwatch.GetTimestamp();
        }

        static void Post(long __state, MethodBase __originalMethod)
        {
            if (__state == 0) return;
            int i = slot[__originalMethod];
            times[i] += Stopwatch.GetTimestamp() - __state;
            calls[i]++;
        }

        static void Roll()
        {
            int f = UnityEngine.Time.frameCount;
            if (f == frame) return;
            if (frame >= 0) Flush();
            frame = f;
            attachedFrame?.SetValue(null, f);
        }

        static void Snapshot(out double all, out double main, out long methods, out int n0, out int n1, out int n2, out double pause)
        {
            all = jitTime != null ? ((System.TimeSpan)jitTime.Invoke(null, new object[] { false })).TotalMilliseconds : 0;
            main = jitTime != null ? ((System.TimeSpan)jitTime.Invoke(null, new object[] { true })).TotalMilliseconds : 0;
            methods = jitCount != null ? (long)jitCount.Invoke(null, new object[] { false }) : 0;
            n0 = System.GC.CollectionCount(0); n1 = System.GC.CollectionCount(1); n2 = System.GC.CollectionCount(2);
            pause = pauseDuration != null ? ((System.TimeSpan)pauseDuration.Invoke(null, null)).TotalMilliseconds : 0;
            double dAll = all - jitAll, dMain = main - jitMain, dPause = pause - gcPause;
            long dMethods = methods - jitMethods;
            int d0 = n0 - gc0, d1 = n1 - gc1, d2 = n2 - gc2;
            jitAll = all; jitMain = main; jitMethods = methods; gc0 = n0; gc1 = n1; gc2 = n2; gcPause = pause;
            all = dAll; main = dMain; methods = dMethods; n0 = d0; n1 = d1; n2 = d2; pause = dPause;
        }

        /// The previous frame's line (this runs at the first timed call of the next frame).
        static void Flush()
        {
            long now = Stopwatch.GetTimestamp();
            double dt = lastStamp == 0 ? 0 : Ms(now - lastStamp);
            lastStamp = now;
            Snapshot(out var all, out var main, out var methods, out var n0, out var n1, out var n2, out var pause);
            bool paused = SpeedControlScreen.Instance == null || SpeedControlScreen.Instance.IsPaused;
            if (!paused && unpausedAt < 0) unpausedAt = frame;
            var updater = Singleton<KBatchedAnimUpdater>.Instance;
            crossed = false;
            if (updater != null)
            {
                updater.GetVisibleArea(out var min, out var max);
                crossed = phase != 0 && phaseFrame > 0 && (min != lastMin || max != lastMax);
                lastMin = min; lastMax = max;
            }
            var row = new double[Cols + parts.Count];
            row[9] = phase; row[10] = phaseFrame++; row[11] = crossed ? 1 : 0;
            row[0] = paused ? 1 : 0; row[1] = dt; row[2] = all; row[3] = main; row[4] = methods;
            row[5] = n0 + n1 + n2; row[6] = pause; row[7] = unpausedAt < 0 ? -1 : frame - unpausedAt; row[8] = frame;
            for (int i = 0; i < parts.Count; i++) row[Cols + i] = Ms(times[i]);
            rows.Add(row);
            var sb = new StringBuilder();
            sb.Append($"Unpause: f={frame} rel={(unpausedAt < 0 ? "p" + (frame - firstFrame) : "u" + (frame - unpausedAt))}{(crossed ? " CROSS" : "")}{(paused ? " paused" : "")} phase={phaseNames[phase]}:{row[10]} x={(Camera.main != null ? Camera.main.transform.position.x : 0):F1} ortho={(Camera.main != null ? Camera.main.orthographicSize : 0):F1} dt={dt:F1} jit={all:F1}/{main:F1}/{methods} gc={n0}/{n1}/{n2} gcPause={pause:F1}");
            for (int i = 0; i < parts.Count; i++)
                if (calls[i] > 0) sb.Append($" {parts[i].name}={Ms(times[i]):F2}" + (calls[i] > 1 ? "x" + calls[i] : ""));
            Debug.Log(sb.ToString());
            TopBuckets();
            System.Array.Clear(times, 0, times.Length);
            System.Array.Clear(calls, 0, calls.Length);
        }

        static void Finish()
        {
            recording = false;
            Sum("still (paused, loading)", r => r[9] == 0);
            for (int p = 1; p <= 4; p++)
            {
                int q = p;
                Sum($"{phaseNames[q]} first 10", r => r[9] == q && r[10] < 10);
                Spread($"{phaseNames[q]} crossings", r => r[9] == q && r[10] >= 60 && r[11] != 0);
                Spread($"{phaseNames[q]} other", r => r[9] == q && r[10] >= 60 && r[11] == 0);
            }
            Done = 1;
        }

        static void Sum(string name, System.Func<double[], bool> pick)
        {
            int n = 0;
            var sum = new double[Cols + parts.Count];
            double maxDt = 0;
            foreach (var r in rows)
            {
                if (!pick(r)) continue;
                n++;
                for (int k = 1; k < r.Length; k++) sum[k] += r[k];
                if (r[1] > maxDt) maxDt = r[1];
            }
            var sb = new StringBuilder($"Unpause summary {name} ({n} frames, summed): dt {sum[1]:F0} (max {maxDt:F0}) jit {sum[2]:F0}/{sum[3]:F0}/{sum[4]:F0} gc {sum[5]:F0} gcPause {sum[6]:F0}");
            for (int i = 0; i < parts.Count; i++) sb.Append($" {parts[i].name} {sum[Cols + i]:F1}");
            Debug.Log(sb.ToString());
        }

        /// Median/max of each column over the frames picked.
        static void Spread(string name, System.Func<double[], bool> pick)
        {
            var set = rows.FindAll(r => pick(r));
            if (set.Count == 0) return;
            string Col(int c)
            {
                var v = set.ConvertAll(r => r[c]);
                v.Sort();
                return $"{v[v.Count / 2]:F1}/{v[v.Count - 1]:F1}";
            }
            var sb = new StringBuilder($"Unpause summary {name} ({set.Count} frames, median/max): dt {Col(1)} jit {Col(2)}/{Col(3)}/{Col(4)} gcPause {Col(6)}");
            for (int i = 0; i < parts.Count; i++) sb.Append($" {parts[i].name} {Col(Cols + i)}");
            Debug.Log(sb.ToString());
        }

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
