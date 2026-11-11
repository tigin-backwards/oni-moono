using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace TimelapseTimingTestMod
{
    /// Times the Timelapser's snapshots and colony previews (U60's flow: the render, an
    /// asynchronous read-back whose callback copies the pixels, then a task that encodes the PNG
    /// and writes it). For each capture, from the frame it is asked for until its task has
    /// written the file: the main thread's time in the render (RenderForTimelapser), in
    /// CapturePng (the read-back request) and in the read-back callback; the task's time in the
    /// encode (ImageConversion.EncodeArrayToPNG) and in all of it (encode and writes); the
    /// longest frames and how far the longest stood above the frames before; the peak resident
    /// memory over the capture (VmHWM, reset first through /proc/self/clear_refs) and the managed
    /// heap's peak (sampled every 2 ms); the file's size. Logs a line a capture,
    /// `TimelapseTiming: {json}`, and keeps the last in `Last`.
    ///
    /// `Run(sizes, preview)`, from a spec's setup or by hand, takes snapshots one after the other
    /// at each size ("512x768,8192x12288"), then with `preview` a colony preview of the save, as a
    /// save takes it; `Done` is then true. Without it the mod only times the captures the game
    /// takes on its own (the cycle's snapshot, a save's preview).
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            var timelapser = typeof(Timelapser);
            Wrap(harmony, AccessTools.Method(typeof(CameraController), "RenderForTimelapser"), nameof(RenderPre), nameof(RenderPost));
            Wrap(harmony, AccessTools.Method(timelapser, "CapturePng"), nameof(CapturePre), nameof(CapturePost));
            // The read-back callback and the encode-and-write task are lambdas in closures.
            foreach (var nested in timelapser.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                foreach (var m in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (m.Name.StartsWith("<CapturePng>")) Wrap(harmony, m, nameof(CallbackPre), nameof(CallbackPost));
                    // The task's body; not RemoveAll's predicate (which takes a Task).
                    else if (m.Name.StartsWith("<QueueEncodeAndWrite>") && m.GetParameters().Length == 0) Wrap(harmony, m, nameof(TaskPre), nameof(TaskPost));
                }
            var encode = AccessTools.Method(typeof(ImageConversion), "EncodeArrayToPNG",
                new[] { typeof(Array), typeof(UnityEngine.Experimental.Rendering.GraphicsFormat), typeof(uint), typeof(uint), typeof(uint) });
            Wrap(harmony, encode, nameof(EncodePre), nameof(EncodePost));
            harmony.Patch(AccessTools.Method(typeof(RetireColonyUtility), nameof(RetireColonyUtility.LoadColonyPreview)),
                postfix: new HarmonyMethod(typeof(Mod), nameof(PreviewLoaded)));
            Debug.Log("TimelapseTiming: installed");
        }

        static void Wrap(Harmony harmony, MethodBase method, string pre, string post)
        {
            if (method == null) { Debug.LogWarning($"TimelapseTiming: no method for {pre}"); return; }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Mod), pre), postfix: new HarmonyMethod(typeof(Mod), post));
        }

        // ----- What a capture measured -----

        sealed class Capture
        {
            public string Kind;
            public int Width, Height, AskedFrame;
            public long AskedAt;
            public double RenderMs, CaptureMs, CallbackMs, EncodeMs, TaskMs, WallMs;
            public bool CallbackDone, TaskDone;
            public string Path;
        }

        static readonly object gate = new object();
        static Capture current;
        static long renderStart, captureStart, callbackStart;
        [ThreadStatic] static long encodeStart, taskStart;

        static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        static long Now => System.Diagnostics.Stopwatch.GetTimestamp();

        static void RenderPre() => renderStart = Now;
        static void RenderPost(RenderTexture tex)
        {
            var c = Current(tex);
            if (c != null) c.RenderMs += Ms(Now - renderStart);
        }

        /// With `Verify(true)`: each colony preview is also read here, synchronously, before the game's
        /// asynchronous read-back (Texture2D.ReadPixels, then EncodeArrayToPNG on a task, as the
        /// Timelapser encodes), and once the game has written its file, the file is compared with
        /// that: its bytes, and its texels as LoadImage gives them. (The synchronous read makes that capture's frame longer: a run
        /// that measures the hitch leaves it off.)
        public static void Verify(bool on) => verify = on;

        /// Verify for the daily snapshots as well (their frames are then no measure of the hitch).
        public static void VerifySnapshots(bool on) => verifySnapshots = on;

        static bool verify, verifySnapshots;
        static byte[] syncPng;
        static Color32[] syncPixels;
        public static bool Verified, BytesMatch, PixelsMatch;

        static void CapturePre(RenderTexture renderTex)
        {
            if (verify && renderTex != null && (verifySnapshots || renderTex.name == "Timelapser.PreviewScreenshot"))
            {
                var active = RenderTexture.active;
                RenderTexture.active = renderTex;
                var read = new Texture2D(renderTex.width, renderTex.height, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, renderTex.width, renderTex.height), 0, 0);
                read.Apply();
                RenderTexture.active = active;
                // Encoded as the Timelapser encodes its read-back, on a task: EncodeToPNG on the
                // main thread deflates at another level (PngWriter.FrameLevel), so its bytes differ.
                var raw = read.GetRawTextureData();
                int width = read.width, height = read.height;
                syncPng = System.Threading.Tasks.Task.Run(() => ImageConversion.EncodeArrayToPNG(raw, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height)).Result;
                syncPixels = read.GetPixels32();
                UnityEngine.Object.Destroy(read);
            }
            captureStart = Now;
        }
        static void CapturePost(RenderTexture renderTex, string path)
        {
            var c = current;
            if (c == null) return;
            c.CaptureMs += Ms(Now - captureStart);
            c.Path = path;
        }

        static void CallbackPre() => callbackStart = Now;
        static void CallbackPost()
        {
            var c = current;
            if (c == null) return;
            c.CallbackMs += Ms(Now - callbackStart);
            c.CallbackDone = true;
        }

        static void EncodePre() => encodeStart = Now;
        static void EncodePost()
        {
            var c = current;
            if (c != null) lock (gate) c.EncodeMs += Ms(Now - encodeStart);
        }

        static void TaskPre()
        {
            taskStart = Now;
            // A write held back as a slow disk would (SlowWrites): a preview's only.
            var c = current;
            if (c != null && c.Kind == "preview" && Snapshots >= 1 && Interlocked.Decrement(ref slowWrites) >= 0) Thread.Sleep(slowMs);
        }

        static int slowWrites, slowMs;

        /// The next `count` colony previews' writes after the first daily snapshot (not an
        /// autosave's before it) start `ms` later, on their task: the window in which the game
        /// could read a preview that is not written yet.
        public static void SlowWrites(int count, int ms)
        {
            slowWrites = count;
            slowMs = ms;
        }

        /// SaveGame.TimelapseResolution, as the save options' slider sets it.
        public static void SetResolution(int width, int height) => SaveGame.Instance.TimelapseResolution = new Vector2I(width, height);

        /// RetireColonyUtility.LoadColonyPreview's calls (the Load screen's previews): how many,
        /// and whether the last gave a picture, with the size of the texture it made.
        public static int PreviewReads;
        public static bool LastPreviewLoaded;
        public static int LastPreviewWidth;

        static void PreviewLoaded(Sprite __result)
        {
            PreviewReads++;
            LastPreviewLoaded = __result != null;
            LastPreviewWidth = __result != null && __result.texture != null ? __result.texture.width : 0;
        }
        static void TaskPost()
        {
            var c = current;
            if (c == null) return;
            lock (gate)
            {
                c.TaskMs += Ms(Now - taskStart);
                c.WallMs = Ms(Now - c.AskedAt);
                c.TaskDone = true;
            }
        }

        /// The capture a render belongs to: the one asked for, or one the game took on its own.
        static Capture Current(RenderTexture tex)
        {
            if (tex == null) return null;
            if (current == null || current.TaskDone)
                Begin(tex.name == "Timelapser.PreviewScreenshot" ? "preview" : "snapshot", tex.width, tex.height);
            return current;
        }

        static void Begin(string kind, int width, int height)
        {
            current = new Capture { Kind = kind, Width = width, Height = height, AskedFrame = Time.frameCount, AskedAt = Now };
            Watch.Start();
        }

        // ----- The run -----

        public static bool Done;
        public static int Captures, Snapshots, Previews, LastWidth, LastHeight;
        public static string Last = "";
        public static double LastHitchMs, LastWallMs, LastPeakRssMb, LastPeakManagedMb;
        public static long LastFileBytes;

        public static void Run(string sizes, bool preview)
        {
            var go = new GameObject("TimelapseTiming");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var runner = go.AddComponent<Runner>();
            runner.Sizes = new List<Vector2I>();
            foreach (var part in (sizes ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var wh = part.Trim().Split('x');
                runner.Sizes.Add(new Vector2I(int.Parse(wh[0]), int.Parse(wh[1])));
            }
            runner.Preview = preview;
            Done = false;
            // The frames before the first capture, for its comparison.
            Watch.Create();
        }

        /// Takes the captures one at a time: each after the frames have settled, the next once
        /// the last has written its file and 30 frames have passed.
        sealed class Runner : MonoBehaviour
        {
            public List<Vector2I> Sizes;
            public bool Preview;
            int next, waitFrames = 120;
            bool waiting;

            void Update()
            {
                if (Done) return;
                if (waiting)
                {
                    if (current == null || !current.TaskDone || !Watch.Reported) return;
                    waiting = false;
                    waitFrames = 30;
                }
                if (waitFrames-- > 0) return;
                var timelapser = Game.Instance != null ? Game.Instance.timelapser : null;
                if (timelapser == null) return;
                var t = Traverse.Create(timelapser);
                if (t.Field("screenshotPending").GetValue<bool>()) return;
                if (next < Sizes.Count)
                {
                    var size = Sizes[next++];
                    SaveGame.Instance.TimelapseResolution = size;
                    Game.Instance.Trigger(75424175);
                    t.Field("worldsToScreenshot").GetValue<List<int>>().Add(ClusterManager.Instance.GetStartWorld().id);
                    Begin("snapshot", size.x, size.y);
                    t.Method("SaveScreenshot").GetValue();
                    waiting = true;
                    waitFrames = 120;
                    return;
                }
                if (Preview)
                {
                    Preview = false;
                    var res = t.Field("previewScreenshotResolution").GetValue<Vector2Int>();
                    Begin("preview", res.x, res.y);
                    timelapser.SaveColonyPreview(SaveLoader.GetActiveSaveFilePath());
                    waiting = true;
                    waitFrames = 120;
                    return;
                }
                Done = true;
                Debug.Log("TimelapseTiming: done");
            }
        }

        /// Frame times, the managed heap's peak and the resident set's over a capture, and the
        /// report once the capture's task has written its file.
        sealed class FrameWatch : MonoBehaviour
        {
            readonly List<double> before = new List<double>();
            readonly List<(int Frame, double Ms)> during = new List<(int, double)>();
            long last;
            bool reported = true;
            int tail;

            public bool Idle => reported;

            public void Arm()
            {
                during.Clear();
                reported = false;
                tail = 30;
            }

            void Update()
            {
                long now = Now;
                double ms = last == 0 ? 0 : Ms(now - last);
                last = now;
                if (reported)
                {
                    if (ms > 0) before.Add(ms);
                    if (before.Count > 120) before.RemoveAt(0);
                    return;
                }
                during.Add((Time.frameCount, ms));
                var c = current;
                if (c == null || !c.TaskDone) return;
                if (tail-- > 0) return;
                Report(c);
                reported = true;
            }

            void Report(Capture c)
            {
                Sampler.Stop(out double peakManaged, out double peakRssSampled);
                double hwm = Watch.PeakRssMb();
                double peakRss = (hwm > 0 ? hwm : peakRssSampled) - Watch.RssAtStart;
                var sorted = new List<double>(before);
                sorted.Sort();
                double median = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0;
                var longest = new List<(int Frame, double Ms)>(during);
                longest.Sort((a, b) => b.Ms.CompareTo(a.Ms));
                var top = new StringBuilder();
                for (int i = 0; i < Math.Min(5, longest.Count); i++)
                    top.Append(i > 0 ? "," : "").Append($"[{longest[i].Frame - c.AskedFrame},{longest[i].Ms:F1}]");
                long bytes = c.Path != null && File.Exists(c.Path) ? new FileInfo(c.Path).Length : -1;
                if (syncPng != null && bytes > 0)
                {
                    var file = File.ReadAllBytes(c.Path);
                    BytesMatch = file.Length == syncPng.Length;
                    for (int i = 0; BytesMatch && i < file.Length; i++) BytesMatch = file[i] == syncPng[i];
                    var loaded = new Texture2D(2, 2);
                    PixelsMatch = loaded.LoadImage(file) && loaded.width == c.Width && loaded.height == c.Height;
                    if (PixelsMatch)
                    {
                        var texels = loaded.GetPixels32();
                        for (int i = 0; PixelsMatch && i < texels.Length; i++)
                            PixelsMatch = texels[i].r == syncPixels[i].r && texels[i].g == syncPixels[i].g && texels[i].b == syncPixels[i].b && texels[i].a == syncPixels[i].a;
                    }
                    UnityEngine.Object.Destroy(loaded);
                    Verified = true;
                    syncPng = null;
                    syncPixels = null;
                }
                double hitch = longest.Count > 0 ? longest[0].Ms - median : 0;
                Captures++;
                if (c.Kind == "preview") Previews++;
                else Snapshots++;
                LastWidth = c.Width;
                LastHeight = c.Height;
                LastHitchMs = hitch;
                LastWallMs = c.WallMs;
                LastPeakRssMb = peakRss;
                LastPeakManagedMb = peakManaged - Watch.ManagedAtStart;
                LastFileBytes = bytes;
                Last = $"{{\"kind\":\"{c.Kind}\",\"width\":{c.Width},\"height\":{c.Height},\"render_ms\":{c.RenderMs:F1},\"capture_ms\":{c.CaptureMs:F1},"
                    + $"\"callback_ms\":{c.CallbackMs:F1},\"encode_ms\":{c.EncodeMs:F1},\"task_ms\":{c.TaskMs:F1},\"wall_ms\":{c.WallMs:F1},"
                    + $"\"median_frame_ms\":{median:F1},\"longest_frames\":[{top}],\"hitch_ms\":{hitch:F1},"
                    + $"\"gc_pause_ms\":{Watch.Pause() - Watch.PauseAtStart:F1},\"peak_rss_mb\":{peakRss:F0},\"peak_managed_mb\":{LastPeakManagedMb:F0},\"file_bytes\":{bytes},"
                    + (Verified ? $"\"bytes_match\":{(BytesMatch ? "true" : "false")},\"pixels_match\":{(PixelsMatch ? "true" : "false")}," : "")
                    + $"\"path\":\"{(c.Path ?? "").Replace("\\", "/")}\"}}";
                Debug.Log("TimelapseTiming: " + Last);
            }
        }

        static class Watch
        {
            static FrameWatch frames;
            public static double RssAtStart, ManagedAtStart, PauseAtStart;

            /// The runtime's GC pauses so far in ms (GC.GetTotalPauseDuration, .NET 7 and later;
            /// 0 in the original's Mono).
            public static double Pause()
            {
                var m = typeof(GC).GetMethod("GetTotalPauseDuration", Type.EmptyTypes);
                return m != null ? ((TimeSpan)m.Invoke(null, null)).TotalMilliseconds : 0;
            }

            /// The last capture's report is out.
            public static bool Reported => frames == null || frames.Idle;

            public static void Create()
            {
                if (frames != null) return;
                var go = new GameObject("TimelapseTimingFrames");
                UnityEngine.Object.DontDestroyOnLoad(go);
                frames = go.AddComponent<FrameWatch>();
            }

            public static void Start()
            {
                Create();
                try { File.WriteAllText("/proc/self/clear_refs", "5"); } catch (Exception) { }
                RssAtStart = Status("VmRSS:");
                PauseAtStart = Pause();
                ManagedAtStart = GC.GetTotalMemory(false) / 1048576.0;
                Sampler.Start();
                frames.Arm();
            }

            public static double PeakRssMb() => Status("VmHWM:");

            /// A /proc/self/status line's value in MB (0 where there is none).
            public static double Status(string key)
            {
                try
                {
                    foreach (var line in File.ReadAllLines("/proc/self/status"))
                        if (line.StartsWith(key)) return double.Parse(line.Substring(key.Length).Trim().Split(' ')[0]) / 1024.0;
                }
                catch (Exception) { }
                return 0;
            }
        }

        /// Samples the managed heap and the resident set every 2 ms on a thread of its own.
        static class Sampler
        {
            static Thread thread;
            static volatile bool running;
            static double peakManaged, peakRss;

            public static void Start()
            {
                Stop(out _, out _);
                peakManaged = peakRss = 0;
                running = true;
                thread = new Thread(() =>
                {
                    while (running)
                    {
                        peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false) / 1048576.0);
                        peakRss = Math.Max(peakRss, Rss());
                        Thread.Sleep(2);
                    }
                })
                { IsBackground = true, Name = "TimelapseTiming sampler" };
                thread.Start();
            }

            public static void Stop(out double managed, out double rss)
            {
                running = false;
                thread?.Join();
                thread = null;
                managed = peakManaged;
                rss = peakRss;
            }

            static double Rss()
            {
                try
                {
                    var parts = File.ReadAllText("/proc/self/statm").Split(' ');
                    return long.Parse(parts[1]) * 4096 / 1048576.0;
                }
                catch (Exception) { return 0; }
            }
        }
    }
}
