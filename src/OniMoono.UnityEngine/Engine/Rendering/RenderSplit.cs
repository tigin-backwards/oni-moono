using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace OniMoono.Engine.Rendering
{
    /// Diagnostics (ONI_RENDER_TIMING=1): the main thread's rendering split into its parts, each
    /// counted exclusive of the parts inside it (a stack of parts: entering one charges the time
    /// so far to the one it interrupts), with the counts behind them and each camera's inclusive
    /// time. Logged with FrameRenderer's `render timing` line every 600 frames. Main thread only;
    /// outside the frame's render phase the stack is off and only the per-camera times count.
    internal static class RenderSplit
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("ONI_RENDER_TIMING") == "1";

        public enum Part
        {
            /// In the render phase, in no part below (camera ordering, binds, clears, sets).
            Other,
            /// Textures loaded since the last frame, video planes.
            Uploads,
            /// OnPreCull, onPreCull, OnPreRender, onPreRender, OnPostRender, onPostRender (game code).
            CameraCallbacks,
            /// Culling: the candidates' bounds against the frustum, their draws collected.
            CullRenderers,
            /// Culling: Graphics.DrawMesh's queued meshes (the animation batches).
            CullQueued,
            /// Culling: camera and world-space canvases batched (CanvasSystem.CollectCameraDraws).
            CullCanvas,
            /// Culling: the draws' distances to the camera.
            CullDistances,
            /// A camera's draws split into opaque and transparent and sorted.
            QueueSort,
            /// Draw queues recorded for the device (GlDrawRecorder.DrawQueue).
            Record,
            /// Camera-event command buffers (CommandExecution; game-made).
            CommandBuffers,
            /// OnRenderImage chains (game code: blits).
            ImageEffects,
            /// Overlay canvases: their draws' meshes baked, ordered and merged (CanvasSystem.Batched).
            OverlayBatch,
            /// OnBecameVisible/Invisible, meshes made but not drawn handed to the device.
            Visibility,
            Count,
        }

        static readonly long[] ticks = new long[(int)Part.Count];
        static readonly int[] stack = new int[64];
        static int depth;
        static long last;
        static bool active;

        // Post-late parts (canvases), timed directly.
        public static long CanvasRootsTicks, CanvasMovesTicks, CanvasBuildTicks;
        static readonly Dictionary<System.Reflection.MethodInfo, (long Ticks, long Calls)> willRender = new();
        static readonly Dictionary<System.Reflection.MethodInfo, (long Ticks, long Calls)> preWillRender = new();

        // Counts.
        public static long CamerasRendered, CamerasOutside, Visible, QueuedSeen, QueuedDrawn, CanvasGathered, CanvasItems, CanvasMerges, CanvasMergesRebuilt,
            CanvasMergedVertices, CanvasBaked, CanvasBakedVertices, CanvasNestedRebuilt, UiMeshesSet, UiMeshesCopied, TextMeshesCopied, OverlayBatches, CameraBatches,
            SortedDraws;

        sealed class CameraStat
        {
            public string Name;
            public long Ticks, Renders, Visits, Draws, Outside;
        }

        static readonly Dictionary<object, CameraStat> cameras = new(ReferenceEqualityComparer.Instance);

        public static long Now => Stopwatch.GetTimestamp();

        /// The render phase begins: the stack starts with Other.
        public static void Begin()
        {
            active = true;
            depth = 0;
            stack[0] = (int)Part.Other;
            last = Now;
        }

        public static void End()
        {
            long now = Now;
            ticks[stack[depth]] += now - last;
            active = false;
            depth = 0;
        }

        public static void Enter(Part p)
        {
            if (!active || depth == stack.Length - 1) return;
            long now = Now;
            ticks[stack[depth]] += now - last;
            last = now;
            stack[++depth] = (int)p;
        }

        public static void Exit()
        {
            if (!active || depth == 0) return;
            long now = Now;
            ticks[stack[depth]] += now - last;
            last = now;
            depth--;
        }

        public static bool InFrame => active;

        public static void Camera(object key, string name, long ticksTaken, long visits, int draws)
        {
            if (!cameras.TryGetValue(key, out var s)) cameras[key] = s = new CameraStat();
            s.Name = name;
            s.Ticks += ticksTaken;
            s.Renders++;
            s.Visits += visits;
            s.Draws += draws;
            if (!active) s.Outside++;
        }

        /// A willRenderCanvases (or preWillRenderCanvases) subscriber's time.
        public static void WillRender(System.Reflection.MethodInfo method, long t, bool pre)
        {
            var d = pre ? preWillRender : willRender;
            d.TryGetValue(method, out var v);
            d[method] = (v.Ticks + t, v.Calls + 1);
        }

        static double Ms(long t, int frames) => t * 1000.0 / Stopwatch.Frequency / Math.Max(1, frames);

        public static string Take(int frames)
        {
            var b = new StringBuilder("render split (ms a frame, exclusive): ");
            long all = 0;
            foreach (var t in ticks) all += t;
            for (int i = 0; i < ticks.Length; i++)
                b.Append(i == 0 ? "" : ", ").Append((Part)i).Append(' ').Append(Ms(ticks[i], frames).ToString("F3"));
            b.Append($"; all {Ms(all, frames):F3}");
            double per(long n) => n / (double)Math.Max(1, frames);
            b.Append($"; counts a frame: cameras {per(CamerasRendered):F1} ({per(CamerasOutside):F1} outside the render phase), visible renderers {per(Visible):F0}, queued meshes {per(QueuedSeen):F0} ({per(QueuedDrawn):F0} in view), sorted draws {per(SortedDraws):F0}, "
                     + $"canvas batches {per(OverlayBatches):F1} overlay and {per(CameraBatches):F1} camera, canvas draws {per(CanvasGathered):F0} into {per(CanvasItems):F0} items, merges {per(CanvasMerges):F0} ({per(CanvasMergesRebuilt):F1} rebuilt, {per(CanvasMergedVertices):F0} vertices), "
                     + $"nested meshes rebuilt {per(CanvasNestedRebuilt):F2}, elements baked {per(CanvasBaked):F1} ({per(CanvasBakedVertices):F0} vertices), UI meshes set {per(UiMeshesSet):F0} ({per(UiMeshesCopied):F1} changed, text {per(TextMeshesCopied):F1})");
            b.Append($"; post-late: canvas roots {Ms(CanvasRootsTicks, frames):F3}, moves {Ms(CanvasMovesTicks, frames):F3}, build {Ms(CanvasBuildTicks, frames):F3}");
            foreach (var (label, d) in new[] { ("preWillRender", preWillRender), ("willRender", willRender) })
                foreach (var (method, (t, calls)) in d)
                    b.Append($", {label} {method.DeclaringType?.Name}.{method.Name} {Ms(t, frames):F3} ({per(calls):F0} calls)");
            var list = new List<CameraStat>(cameras.Values);
            list.Sort((x, y) => y.Ticks.CompareTo(x.Ticks));
            b.Append("; cameras (inclusive ms a frame, renders, visits and draws a frame):");
            foreach (var s in list)
                b.Append($" {s.Name} {Ms(s.Ticks, frames):F3} ({per(s.Renders):F2}{(s.Outside > 0 ? $", {per(s.Outside):F2} outside" : "")}, {per(s.Visits):F0}, {per(s.Draws):F0});");
            Array.Clear(ticks);
            CanvasRootsTicks = CanvasMovesTicks = CanvasBuildTicks = 0;
            willRender.Clear();
            preWillRender.Clear();
            cameras.Clear();
            CamerasRendered = CamerasOutside = Visible = QueuedSeen = QueuedDrawn = CanvasGathered = CanvasItems = CanvasMerges = CanvasMergesRebuilt = CanvasMergedVertices
                = CanvasBaked = CanvasBakedVertices = CanvasNestedRebuilt = UiMeshesSet = UiMeshesCopied = TextMeshesCopied = OverlayBatches = CameraBatches = SortedDraws = 0;
            return b.ToString();
        }
    }
}
