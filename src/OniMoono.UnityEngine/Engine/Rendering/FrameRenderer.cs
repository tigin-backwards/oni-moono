using System.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace OniMoono.Engine.Rendering
{
    /// One thing to draw for a camera: a submesh with a material, plus what it sorts by.
    internal sealed class DrawItem
    {
        public RendererNative Renderer;
        public MeshNative Mesh;
        public int SubMesh;
        public Matrix4x4 ObjectToWorld;
        public Material Material;
        public int Pass = -1;
        public PropertySheet Properties;
        public string[] Keywords;
        public int Queue;
        public int SortingLayerValue;
        public int SortingOrder;
        public float Distance;
        public Bounds WorldBounds;
        public long Sequence;
        /// A Graphics.DrawMesh draw: in the transparent queue, ties on distance between two of
        /// them go by material (FrameRenderer.TransparentOrder).
        public bool Queued;

        public void Clear()
        {
            Renderer = null;
            Mesh = null;
            SubMesh = 0;
            ObjectToWorld = default;
            Material = null;
            Pass = -1;
            Properties = null;
            Keywords = null;
            Queue = 0;
            SortingLayerValue = 0;
            SortingOrder = 0;
            Distance = 0f;
            WorldBounds = default;
            Sequence = 0;
            Queued = false;
        }
    }

    /// A Graphics.DrawMesh request, drawn by the cameras of the frame it was made in.
    internal struct QueuedMesh
    {
        public Mesh Mesh;
        public int SubMesh;
        public Matrix4x4 Matrix;
        public Material Material;
        public int Layer;
        public Camera Camera;
        public PropertySheet Properties;
        /// Queued by a camera's own rendering callbacks (OnPreCull…), which queue it again
        /// when that camera renders again.
        public bool DuringRender;
    }

    /// Unity's built-in forward renderer, independent of the graphics device (ARCHITECTURE §3.5).
    ///
    /// Per frame (when the device renders frames): cameras that render into textures, then
    /// cameras that render to the screen, each set in ascending depth (enable order on ties);
    /// then screen-space overlay canvases. Per camera: OnPreCull, culling, OnPreRender, target
    /// and clear, command buffers at their CameraEvents around the opaque and transparent
    /// passes, OnPostRender, then the OnRenderImage chain.
    internal static class FrameRenderer
    {
        public static Camera Current { get; private set; }

        /// The cull candidates: renderers that are enabled on an active GameObject, one list per
        /// layer, so a camera walks only the layers its culling mask holds. Unity's culling
        /// costs nothing per disabled renderer or renderer on an inactive object, and it draws
        /// renderers tied on everything they sort by in the order they last became candidates
        /// (a renderer disabled and enabled again, even between two renders, draws after the
        /// others; measured: UnityProbe renderercull): each candidate keeps the stamp of when
        /// it joined, which orders its draws. Fast Track's mesh renderers leave a tenth of
        /// 30,000 enabled, spread over the layers 15 cameras cull. A renderer leaving a list
        /// leaves a hole (null), compacted away before a pass, so toggling one costs O(1).
        sealed class CandidateList
        {
            public readonly List<RendererNative> Items = new();
            public int Holes;
        }

        static readonly CandidateList[] candidates = Enumerable.Range(0, 32).Select(_ => new CandidateList()).ToArray();
        static long nextStamp;
        /// Departures from a candidate list (moves between two included), since the start.
        static long candidateDepartures;
        static int liveCandidates;
        /// Cull passes running: a camera rendered from OnBecameVisible culls inside another.
        static int culling;
        static readonly List<QueuedMesh> queued = new();
        static readonly HashSet<RendererNative> visibleThisFrame = new();
        static readonly HashSet<RendererNative> visibleLastFrame = new();

        /// Additional draws for a camera (world-space and camera-space canvases, particles…).
        public static event Action<Camera, CameraNative, List<DrawItem>> CollectDraws;

        /// Screen-space overlay canvases, after every camera.
        public static event Action RenderOverlays;

        static bool installed;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            Particles.ParticleSystemRendererNative.CameraProjection = camera => ((CameraNative)camera.NativeOrThrow).Projection;
            PlayerLoop.Render += RenderFrame;
            PlayerLoop.Present += EndFrame;
        }

        /// A renderer's enabled flag, its GameObject's activation or its destruction changed:
        /// it joins the cull candidates (last in their order) or leaves them.
        public static void UpdateCandidate(RendererNative r)
        {
            bool wanted = r.Enabled && r.EngineEnabled && !r.Destroyed;
            if (wanted == r.CandidateSlot >= 0) return;
            candidateChanges++;
            if (wanted)
            {
                r.CandidateStamp = ++nextStamp;
                AddCandidate(r, r.GameObject.Layer);
                liveCandidates++;
            }
            else
            {
                RemoveCandidate(r);
                liveCandidates--;
            }
        }

        static void AddCandidate(RendererNative r, int layer)
        {
            var list = candidates[layer];
            r.CandidateLayer = layer;
            r.CandidateSlot = list.Items.Count;
            list.Items.Add(r);
        }

        static void RemoveCandidate(RendererNative r)
        {
            candidateDepartures++;
            var list = candidates[r.CandidateLayer];
            list.Items[r.CandidateSlot] = null;
            r.CandidateSlot = -1;
            // Bounded while no camera culls (loading screens).
            if (++list.Holes > 1024 && list.Holes * 2 > list.Items.Count) Compact(list);
        }

        /// A GameObject's layer changed: its candidate renderers move to that layer's list,
        /// keeping their place in the order.
        public static void LayerChanged(GameObjectNative go)
        {
            foreach (var c in go.Components)
            {
                if (c?.Native is not RendererNative { CandidateSlot: >= 0 } r || r.CandidateLayer == go.Layer) continue;
                RemoveCandidate(r);
                AddCandidate(r, go.Layer);
                layerMoves++;
            }
        }

        public static void Unregister(RendererNative r)
        {
            if (r.CandidateSlot >= 0)
            {
                RemoveCandidate(r);
                liveCandidates--;
            }
            visibleThisFrame.Remove(r);
            visibleLastFrame.Remove(r);
        }

        static void Compact(CandidateList list)
        {
            if (list.Holes == 0 || culling > 0) return;
            var items = list.Items;
            int kept = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var r = items[i];
                if (r == null) continue;
                r.CandidateSlot = kept;
                items[kept++] = r;
            }
            items.RemoveRange(kept, items.Count - kept);
            list.Holes = 0;
        }

        /// Renderers Cull has looked at, all cameras together, since the start (tests, and
        /// ONI_RENDER_TIMING): it grows with the enabled renderers on active objects in the
        /// layers the cameras cull, never with the disabled, inactive or other layers' ones.
        internal static long CullVisits;

        /// The candidate lists' lengths, holes included (tests).
        internal static int CandidateSlots => candidates.Sum(l => l.Items.Count);

        /// The cull candidates in their order (tests).
        internal static IEnumerable<RendererNative> Candidates
            => candidates.SelectMany(l => l.Items).Where(r => r != null).OrderBy(r => r.CandidateStamp);

        public static void Queue(QueuedMesh m)
        {
            m.DuringRender = Current != null;
            queued.Add(m);
        }

        /// The frame's meshes queued by scripts outside camera rendering. Like Unity's
        /// intermediate renderers they last until the frame ends: cameras rendered by scripts
        /// after the frame's rendering (end-of-frame coroutines) draw them too, and captures
        /// render them again.
        static readonly List<QueuedMesh> frameQueued = new();

        /// The frame's own camera pass is running.
        static bool inFrameRender;

        /// Diagnostics (ONI_RENDER_TIMING=1): the main thread's time in rendering and in draw
        /// queues, logged every TimingFrames frames.
        static readonly bool timing = Environment.GetEnvironmentVariable("ONI_RENDER_TIMING") == "1";
        const int TimingFrames = 600;
        static long renderTicks, queueTicks, queueDraws, windowStart, candidateChanges, windowVisits, layerMoves;
        static TimeSpan lastCpu;
        static int timedFrames;

        static void RenderFrame()
        {
            long start = timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                RenderFrameCore();
            }
            finally
            {
                if (timing) EndTiming(start);
            }
        }

        static void EndTiming(long start)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            renderTicks += now - start;
            if (windowStart == 0) windowStart = start;
            if (++timedFrames < TimingFrames) return;
            double ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / timedFrames;
            double fps = timedFrames / ((now - windowStart) / (double)System.Diagnostics.Stopwatch.Frequency);
            var device = GraphicsDevice.Current.TakeQueueCounters(timedFrames);
            var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            double cores = (cpu - lastCpu).TotalSeconds / ((now - windowStart) / (double)System.Diagnostics.Stopwatch.Frequency);
            lastCpu = cpu;
            EngineLog.Engine($"render timing: frame {Time.frameCount}, {fps:F2} fps, rendering {ms(renderTicks):F3} ms a frame, draw queues {ms(queueTicks):F3} ms ({queueDraws / timedFrames} draws a frame), cull candidates {liveCandidates} ({candidateChanges / timedFrames} changes, {layerMoves / timedFrames} layer moves and {(CullVisits - windowVisits) / timedFrames} visits a frame){(device != null ? ", " + device : "")}"
                + $"; pool threads {System.Threading.ThreadPool.ThreadCount}, pending {System.Threading.ThreadPool.PendingWorkItemCount}, process threads {System.Diagnostics.Process.GetCurrentProcess().Threads.Count}, cpu {cores:F1} cores, gc {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, heap {GC.GetTotalMemory(false) >> 20} MB");
            renderTicks = queueTicks = queueDraws = candidateChanges = layerMoves = 0;
            windowVisits = CullVisits;
            timedFrames = 0;
            windowStart = now;
        }

        /// Draws a sorted queue (a camera's pass, an overlay canvas's batches).
        internal static void DrawQueue(RenderDevice device, List<DrawItem> items)
        {
            if (!timing)
            {
                device.DrawQueue(items);
                return;
            }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            device.DrawQueue(items);
            queueTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            queueDraws += items.Count;
        }

        static void RenderFrameCore()
        {
            if (GraphicsDevice.Current.RendersFrames)
            {
                GraphicsDevice.UploadPending();
                VideoPlanes.CopyRenderTextureVideos();
                inFrameRender = true;
                try
                {
                    RenderScreen();
                }
                finally
                {
                    inFrameRender = false;
                }
            }
            // The frame's rendering ends with the screen as the active target, whatever scripts
            // left active before it (measured in the original, UnityProbe's activetarget
            // scenario: the game's property-texture blits in LateUpdate leave their target
            // active, and at WaitForEndOfFrame it is null; set there, it lasts until the next
            // frame's rendering ends). The Timelapser reads the screen with ReadPixels at
            // WaitForEndOfFrame for the frame it shows while a save writes its preview: from the
            // blit's target instead, that frame was black.
            RenderTexture.active = null;
            frameQueued.Clear();
            foreach (var q in queued)
                if (!q.DuringRender) frameQueued.Add(q);
            queued.Clear();
            UpdateVisibility();
        }

        /// Cameras rendering into textures, then those rendering to the screen, each set in
        /// ascending depth (enable order on ties); then overlay canvases.
        static void RenderScreen()
        {
            var cameras = Cameras.Enabled.ToArray();
            var order = new List<(int Key, float Depth, int Index, Camera Camera)>();
            for (int i = 0; i < cameras.Length; i++)
            {
                var n = (CameraNative)cameras[i].Native;
                if (n == null || !n.GameObject.ActiveInHierarchy) continue;
                bool offscreen = n.TargetTexture != null || n.TargetColorBuffers != null;
                order.Add((offscreen ? 0 : 1, n.Depth, i, cameras[i]));
            }
            order.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.Index.CompareTo(b.Index));
            foreach (var entry in order)
                if (entry.Camera != null) RenderCamera(entry.Camera);
            RenderOverlays?.Invoke();
        }

        // ----- The screen, and super-sampled captures of it -----

        static readonly List<(string Path, int Scale)> captures = new();
        static RenderTextureNative screenOverride;
        static int screenScale = 1;

        /// Where screen rendering goes: the back buffer, or a capture's off-screen target.
        public static RenderTargetBinding ScreenBinding => screenOverride == null ? RenderTargetBinding.BackBuffer : RenderTargetBinding.Of(screenOverride);

        /// Pixels per screen pixel of the current screen target (superSize during captures).
        public static int ScreenScale => screenScale;

        /// ScreenCapture.CaptureScreenshot: written once this frame is complete.
        public static void RequestCapture(string path, int superSize) => captures.Add((path, Math.Max(1, superSize)));

        /// Writes the frame as PNG: the back buffer itself, or for a super-sized capture the frame
        /// rendered again into a target `scale` times the screen size.
        static void Capture(string path, int scale)
        {
            var p = Platform.PlatformServices.Current;
            if (scale == 1)
            {
                // The finished frame, as it is about to be presented.
                try
                {
                    var frame = new Texture2D(p.FramebufferWidth, p.FramebufferHeight, TextureFormat.RGBA32, false);
                    GraphicsDevice.Current.ReadPixels(null, new RectInt(0, 0, p.FramebufferWidth, p.FramebufferHeight), (Texture2DNative)frame.NativeOrThrow, 0, 0);
                    System.IO.File.WriteAllBytes(path, frame.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(frame);
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
                {
                    Debug.LogError($"Failed to store screenshot: {path} ({e.Message})");
                }
                return;
            }
            int w = p.FramebufferWidth * scale, h = p.FramebufferHeight * scale;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var native = (RenderTextureNative)rt.NativeOrThrow;
            screenOverride = native;
            screenScale = scale;
            queued.Clear();
            queued.AddRange(frameQueued);
            try
            {
                RenderScreen();
                RenderScreenLate?.Invoke();
                var image = new Texture2D(w, h, TextureFormat.RGBA32, false);
                GraphicsDevice.Current.ReadPixels(native, new RectInt(0, 0, w, h), (Texture2DNative)image.NativeOrThrow, 0, 0);
                System.IO.File.WriteAllBytes(path, image.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(image);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                Debug.LogError($"Failed to store screenshot: {path} ({e.Message})");
            }
            finally
            {
                queued.Clear();
                screenOverride = null;
                screenScale = 1;
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        /// Draw queues are recorded serially, whatever the device would do (diagnostics).
        public static bool SerialQueues;

        /// Diagnostics (ONI_CAPTURE_TWICE=serial|parallel|full-draws|full-uploads): each super-sized
        /// capture renders the frame a second time, with draw queues recorded serially, as usual,
        /// (full-draws) with the Vulkan draw path's shortcuts off (FullDrawWork), or
        /// (full-uploads) with every raw-loaded texture uploaded in full (RawTextureLoads), into
        /// PATH.again.png: the two must be identical.
        static readonly string captureTwice = Environment.GetEnvironmentVariable("ONI_CAPTURE_TWICE");

        /// The capture being rendered again takes the Vulkan device's draw path without its
        /// shortcuts (VkDrawAlternation; the device thread reads it during the capture, which
        /// waits for its readback).
        public static bool FullDrawWork;

        /// Screen drawing after the frame renderer's (IMGUI); captures repeat it.
        public static event Action RenderScreenLate;

        /// Diagnostics (`--dump-render-textures FRAME:DIR`): every created 2D render texture and
        /// the screen written as PNG at the end of that frame, and again (into DIR/fFRAME) whenever
        /// a file DIR.now appears, which is then removed.
        public static int DumpFrame = -1;
        public static string DumpDirectory;

        static void DumpRenderTextures()
        {
            System.IO.Directory.CreateDirectory(DumpDirectory);
            var device = GraphicsDevice.Current;
            var p = Platform.PlatformServices.Current;
            void Write(RenderTextureNative source, int w, int h, string name)
            {
                try
                {
                    var image = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    device.ReadPixels(source, new RectInt(0, 0, w, h), (Texture2DNative)image.NativeOrThrow, 0, 0);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(DumpDirectory, name + ".png"), image.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(image);
                }
                catch (Exception e)
                {
                    EngineLog.Engine($"render texture dump: {name}: {e.Message}");
                }
            }
            Write(null, p.FramebufferWidth, p.FramebufferHeight, "_screen");
            foreach (var native in ObjectTable.Snapshot())
            {
                if (native is not RenderTextureNative rt || !rt.Created || rt.Dimension != TextureKind.Tex2D || rt.Width <= 0 || rt.Height <= 0) continue;
                if (rt.ColorFormat == UnityEngine.Experimental.Rendering.GraphicsFormat.None) continue;
                var safe = string.Concat((rt.Name.Length > 0 ? rt.Name : "rt").Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
                Write(rt, rt.Width, rt.Height, $"{safe}_{rt.InstanceId}_{rt.Width}x{rt.Height}_{rt.ColorFormat}");
            }
        }

        // Draw items and draw lists live until the frame ends (they are collected, sorted and
        // drawn within a camera render, the overlay pass or a command buffer's execution), so the
        // frame's are reused by the next frame.
        static readonly List<DrawItem> itemPool = new();
        static int itemsUsed;

        static readonly List<List<DrawItem>> listPool = new();
        static int listsUsed;

        /// An empty draw list for this frame (camera renders, canvas batching).
        public static List<DrawItem> NewDrawList()
        {
            if (listsUsed < listPool.Count)
            {
                var list = listPool[listsUsed++];
                list.Clear();
                return list;
            }
            var made = new List<DrawItem>();
            listPool.Add(made);
            listsUsed++;
            return made;
        }

        /// A cleared draw item for this frame.
        public static DrawItem NewDrawItem()
        {
            DrawItem d;
            if (itemsUsed < itemPool.Count)
            {
                d = itemPool[itemsUsed];
                d.Clear();
            }
            else
            {
                d = new DrawItem();
                itemPool.Add(d);
            }
            itemsUsed++;
            return d;
        }

        static void EndFrame()
        {
            if (Time.frameCount == DumpFrame && GraphicsDevice.Current.RendersFrames) DumpRenderTextures();
            if (DumpDirectory != null && Time.frameCount % 30 == 0 && System.IO.File.Exists(DumpDirectory + ".now") && GraphicsDevice.Current.RendersFrames)
            {
                System.IO.File.Delete(DumpDirectory + ".now");
                var directory = DumpDirectory;
                DumpDirectory = System.IO.Path.Combine(directory, "f" + Time.frameCount);
                try { DumpRenderTextures(); }
                finally { DumpDirectory = directory; }
            }
            // Captures show the finished frame, OnGUI included.
            if (GraphicsDevice.Current.RendersFrames)
                foreach (var (path, scale) in captures)
                {
                    Capture(path, scale);
                    if (captureTwice == null || scale == 1) continue;
                    // Diagnostics: the same frame rendered again, into PATH.again.png.
                    SerialQueues = captureTwice == "serial";
                    System.Threading.Volatile.Write(ref FullDrawWork, captureTwice == "full-draws");
                    if (captureTwice == "full-uploads") RawTextureLoads.UploadAll();
                    try { Capture(path + ".again.png", scale); }
                    finally
                    {
                        SerialQueues = false;
                        System.Threading.Volatile.Write(ref FullDrawWork, false);
                    }
                }
            captures.Clear();
            frameQueued.Clear();
            // The frame's queued draws are done with the property blocks they share.
            PropertySheet.RenderEpoch++;
            itemsUsed = 0;
            listsUsed = 0;
            TemporaryRenderTextures.EndFrame();
            // Device resources of objects the collector reclaimed (private meshes of destroyed
            // renderers and the like).
            GraphicsDevice.ReleaseOrphans();
            TextureMemoryLog.Frame();
            GraphicsDevice.Current.Present();
        }

        /// Renderers that no camera saw this frame become invisible.
        static void UpdateVisibility()
        {
            foreach (var r in visibleLastFrame)
            {
                if (visibleThisFrame.Contains(r) || r.Destroyed) continue;
                r.IsVisible = false;
                SendToRendererObject(r, s => s.OnBecameInvisible);
            }
            visibleLastFrame.Clear();
            visibleLastFrame.UnionWith(visibleThisFrame);
            visibleThisFrame.Clear();
        }

        static void SendToRendererObject(RendererNative r, Func<ScriptInfo, Action<MonoBehaviour>> select) => SendToEnabled(r.GameObject, select);

        /// Camera and render messages go to the enabled behaviours of an active GameObject.
        static void SendToEnabled(GameObjectNative go, Func<ScriptInfo, Action<MonoBehaviour>> select)
        {
            if (!go.ActiveInHierarchy) return;
            foreach (var c in go.Components)
            {
                if (c is not MonoBehaviour mb || c.Native is not BehaviourNative { IsActiveAndEnabledState: true } bn || bn.Script == null) continue;
                var m = select(bn.Script);
                if (m != null) Lifecycle.Invoke(m, mb);
            }
        }

        public static void RenderCamera(Camera camera)
        {
            var cam = (CameraNative)camera.NativeOrThrow;
            var previous = Current;
            Current = camera;
            var previousActive = RenderTexture.active;
            var previousView = GLState.View;
            try
            {
                RenderCameraCore(camera, cam);
            }
            finally
            {
                Current = previous;
                RenderTexture.active = previousActive;
                GLState.View = previousView;
            }
        }

        static void RenderCameraCore(Camera camera, CameraNative cam)
        {
            var device = GraphicsDevice.Current;
            SendToEnabled(cam.GameObject, s => s.OnPreCull);
            if (cam.Destroyed) return;
            Camera.onPreCull?.Invoke(camera);

            long departuresAtCull = candidateDepartures;
            var draws = Cull(camera, cam);

            SendToEnabled(cam.GameObject, s => s.OnPreRender);
            if (cam.Destroyed) return;
            Camera.onPreRender?.Invoke(camera);

            // Image effects render the camera into a temporary target first.
            var effects = new List<(MonoBehaviour Behaviour, Action<MonoBehaviour, RenderTexture, RenderTexture> Effect)>();
            foreach (var c in cam.GameObject.Components)
                if (c is MonoBehaviour mb && c.Native is BehaviourNative { IsActiveAndEnabledState: true } bn && bn.Script?.OnRenderImage is { } effect)
                    effects.Add((mb, effect));

            RenderTexture source = null;
            bool toScreen = cam.TargetTexture == null && cam.TargetColorBuffers == null;
            int pixelScale = toScreen ? screenScale : 1;
            var size = cam.TargetSize * pixelScale;
            if (effects.Count > 0)
            {
                source = RenderTexture.GetTemporary(Math.Max(1, (int)size.x), Math.Max(1, (int)size.y), 24,
                    cam.AllowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                Bind(device, RenderTargetBinding.Of((RenderTextureNative)source.NativeOrThrow));
            }
            var cameraTarget = source != null ? RenderTargetBinding.Of((RenderTextureNative)source.NativeOrThrow) : CameraTarget(cam);
            if (source == null) Bind(device, cameraTarget);
            var viewport = cam.PixelRect;
            var pixelViewport = new Rect(viewport.x * pixelScale, viewport.y * pixelScale, viewport.width * pixelScale, viewport.height * pixelScale);
            device.SetViewport(pixelViewport);
            var constants = Constants(cam);
            device.SetCamera(constants);
            // Camera-event command buffers may retarget or change matrices; the camera's own
            // passes continue on its target with its matrices afterwards, as in Unity.
            void Event(CameraEvent evt)
            {
                if (!Execute(camera, cam, evt)) return;
                Bind(device, cameraTarget);
                device.SetViewport(pixelViewport);
                device.SetCamera(constants);
            }
            GLState.BeginCamera(cam);

            switch (cam.ClearFlags)
            {
                case CameraClearFlags.Skybox:
                case CameraClearFlags.SolidColor:
                    // No skybox material is set in this project's RenderSettings: the background
                    // colour fills the target, as Unity does in that case.
                    device.Clear(true, true, cam.BackgroundColor, 1f);
                    break;
                case CameraClearFlags.Depth:
                    device.Clear(false, true, cam.BackgroundColor, 1f);
                    break;
            }

            VideoPlanes.Draw(camera, cam, nearPlane: false);
            Event(CameraEvent.BeforeForwardOpaque);
            DrawQueue(device, draws, opaque: true, departed: candidateDepartures != departuresAtCull);
            Event(CameraEvent.AfterForwardOpaque);
            Event(CameraEvent.BeforeSkybox);
            Event(CameraEvent.AfterSkybox);
            Event(CameraEvent.BeforeImageEffectsOpaque);
            Event(CameraEvent.AfterImageEffectsOpaque);
            Event(CameraEvent.BeforeForwardAlpha);
            DrawQueue(device, draws, opaque: false, departed: candidateDepartures != departuresAtCull);
            Event(CameraEvent.AfterForwardAlpha);
            VideoPlanes.Draw(camera, cam, nearPlane: true);

            // In onPostRender the camera's target is the active one (measured in the original,
            // UnityProbe's apigaps "rt"); it is bound already, so only the field changes.
            RenderTexture.SetActiveBound(source ?? cam.TargetTexture);
            SendToEnabled(cam.GameObject, s => s.OnPostRender);
            Camera.onPostRender?.Invoke(camera);

            if (effects.Count > 0 && source != null) ImageEffects(camera, cam, effects, source);
            Execute(camera, cam, CameraEvent.AfterEverything);
        }

        // The OnRenderImage chain; Lifecycle.Invoke passes each effect its textures, so no
        // closure is allocated per effect.
        static void ImageEffects(Camera camera, CameraNative cam, List<(MonoBehaviour Behaviour, Action<MonoBehaviour, RenderTexture, RenderTexture> Effect)> effects, RenderTexture source)
        {
            Execute(camera, cam, CameraEvent.BeforeImageEffects);
            var finalTarget = cam.TargetTexture;
            for (int i = 0; i < effects.Count; i++)
            {
                bool last = i == effects.Count - 1;
                var destination = last ? finalTarget : RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
                RenderTexture.active = destination;
                Lifecycle.Invoke(effects[i].Effect, effects[i].Behaviour, source, destination);
                RenderTexture.ReleaseTemporary(source);
                source = destination;
            }
            Execute(camera, cam, CameraEvent.AfterImageEffects);
        }

        static RenderTargetBinding CameraTarget(CameraNative cam)
        {
            if (cam.TargetColorBuffers != null)
            {
                var colors = new RenderTextureNative[cam.TargetColorBuffers.Length];
                for (int i = 0; i < colors.Length; i++) colors[i] = ObjectTable.FindByInstanceId(cam.TargetColorBuffers[i].m_RenderTextureInstanceID) as RenderTextureNative;
                var depth = ObjectTable.FindByInstanceId(cam.TargetDepthBuffer.m_RenderTextureInstanceID) as RenderTextureNative;
                return new RenderTargetBinding(colors, depth, 0);
            }
            return cam.TargetTexture != null ? RenderTargetBinding.Of((RenderTextureNative)cam.TargetTexture.NativeOrThrow) : ScreenBinding;
        }

        /// Binds a target, creating render textures on first use.
        internal static void Bind(RenderDevice device, in RenderTargetBinding target)
        {
            if (target.Colors != null)
                foreach (var t in target.Colors)
                    if (t != null) t.Created = true;
            if (target.Depth != null) target.Depth.Created = true;
            device.SetRenderTarget(target);
        }

        static CameraConstants Constants(CameraNative cam)
            => new(cam.WorldToCamera, cam.Projection, Hierarchy.Position(cam.GameObject.Transform), cam.Near, cam.Far, cam.Orthographic, cam.TargetSize);

        /// Runs the camera's command buffers for an event; true when any ran.
        static bool Execute(Camera camera, CameraNative cam, CameraEvent evt)
        {
            if (!cam.CommandBuffers.TryGetValue(evt, out var list) || list.Count == 0) return false;
            foreach (var buffer in list.ToArray()) CommandExecution.Run(buffer, camera);
            return true;
        }

        // ----- Culling and draw lists -----

        static List<DrawItem> Cull(Camera camera, CameraNative cam)
        {
            var draws = NewDrawList();
            var frustum = new Frustum(cam.Projection * cam.WorldToCamera);
            // A renderer's draws go by when it became a candidate; then the queued meshes and
            // the other draws, in submission order.
            long sequence = 1L << 62;
            // Renderers that OnBecameVisible enables or creates are drawn from the next render
            // on, and those it disables are not drawn in this one (measured: UnityProbe
            // renderercull): the pass covers the candidates it started with.
            int mask = cam.CullingMask;
            Span<int> counts = stackalloc int[32];
            for (int layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) == 0) continue;
                Compact(candidates[layer]);
                counts[layer] = candidates[layer].Items.Count;
            }
            culling++;
            try
            {
                for (int layer = 0; layer < 32; layer++)
                {
                    var items = candidates[layer].Items;
                    for (int c = 0; c < counts[layer]; c++)
                    {
                        var r = items[c];
                        if (r == null) continue;
                        CullVisits++;
                        if (r.Destroyed || !r.Enabled || !r.GameObject.ActiveInHierarchy) continue;
                        var bounds = r.WorldBounds;
                        if (!frustum.Intersects(bounds)) continue;
                        int before = draws.Count;
                        RendererDraws.Collect(r, camera, draws);
                        if (draws.Count == before) continue;
                        MarkVisible(r);
                        for (int i = before; i < draws.Count; i++) draws[i].Sequence = (r.CandidateStamp << 16) + (i - before);
                    }
                }
            }
            finally
            {
                culling--;
            }
            // Cameras rendered by scripts after the frame's pass also draw the frame's meshes
            // (captures have already put them back in the queue).
            bool afterFrame = !inFrameRender && screenOverride == null;
            // Each list in turn: through IEnumerable (Concat, or the conditional's type) the
            // enumerators were boxed for every camera. Read in place, by index: the loop runs
            // no script, so neither list changes under it, and a foreach copied each queued
            // mesh (about 110 bytes) out of the list.
            for (int list = afterFrame ? 0 : 1; list < 2; list++)
            {
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list == 0 ? frameQueued : queued);
                for (int k = 0; k < span.Length; k++)
                {
                    ref readonly var q = ref span[k];
                    if (q.Camera != null && q.Camera != camera) continue;
                    if ((cam.CullingMask & (1 << q.Layer)) == 0 || q.Mesh == null || q.Material == null) continue;
                    var mesh = (MeshNative)q.Mesh.NativeOrThrow;
                    var bounds = RendererNative.Transform(mesh.Bounds, q.Matrix);
                    if (!frustum.Intersects(bounds)) continue;
                    var item = NewDrawItem();
                    item.Mesh = mesh;
                    item.SubMesh = Math.Clamp(q.SubMesh, 0, Math.Max(0, mesh.SubMeshes.Count - 1));
                    item.ObjectToWorld = q.Matrix;
                    item.Material = q.Material;
                    item.Properties = q.Properties;
                    item.Queue = q.Material.renderQueue;
                    item.WorldBounds = bounds;
                    item.Sequence = sequence++;
                    item.Queued = true;
                    draws.Add(item);
                }
            }
            int collected = draws.Count;
            CollectDraws?.Invoke(camera, cam, draws);
            for (int i = collected; i < draws.Count; i++) draws[i].Sequence = sequence++;

            var position = Hierarchy.Position(cam.GameObject.Transform);
            var forward = cam.GameObject.Transform.WorldRotation * Vector3.forward;
            var mode = cam.TransparencySortMode == TransparencySortMode.Default
                ? (cam.Orthographic ? TransparencySortMode.Orthographic : TransparencySortMode.Perspective)
                : cam.TransparencySortMode;
            foreach (var d in draws)
            {
                var center = d.WorldBounds.center;
                d.Distance = mode switch
                {
                    TransparencySortMode.Orthographic => Vector3.Dot(center - position, forward),
                    TransparencySortMode.CustomAxis => Vector3.Dot(center, cam.TransparencySortAxis),
                    _ => (center - position).sqrMagnitude,
                };
            }
            return draws;
        }

        static void MarkVisible(RendererNative r)
        {
            if (!visibleThisFrame.Add(r)) return;
            if (r.IsVisible) return;
            r.IsVisible = true;
            SendToRendererObject(r, s => s.OnBecameVisible);
        }

        /// Opaque (queue ≤ 2500): queue, then sorting layer/order, then front to back.
        /// Transparent: sorting layer/order, queue, then back to front. Ties keep submission order,
        /// except between two Graphics.DrawMesh draws, which go by their materials' instance IDs,
        /// ascending (the material made last first, under the others), whatever order they were
        /// submitted in (measured: UnityProbe draworder, as KAnimBatchManager draws batches whose
        /// z offsets fall within a float step of each other at the camera's distance).
        static void DrawQueue(RenderDevice device, List<DrawItem> all, bool opaque, bool departed)
        {
            var list = NewDrawList();
            foreach (var d in all)
            {
                // A renderer disabled, deactivated or destroyed after culling (from OnPreRender,
                // say) is not drawn (measured: UnityProbe renderercull). Looked at only when a
                // renderer has left the candidates since: reading each draw's renderer costs a
                // cache miss a draw.
                if (departed && d.Renderer is { CandidateSlot: < 0 }) continue;
                if (opaque ? d.Queue <= 2500 : d.Queue > 2500) list.Add(d);
            }
            list.Sort(opaque ? OpaqueOrder : TransparentOrder);
            DrawQueue(device, list);
        }

        static readonly Comparison<DrawItem> OpaqueOrder = (a, b) =>
        {
            int c;
            if ((c = a.Queue.CompareTo(b.Queue)) != 0) return c;
            if ((c = a.SortingLayerValue.CompareTo(b.SortingLayerValue)) != 0) return c;
            if ((c = a.SortingOrder.CompareTo(b.SortingOrder)) != 0) return c;
            if ((c = a.Distance.CompareTo(b.Distance)) != 0) return c;
            return a.Sequence.CompareTo(b.Sequence);
        };

        static readonly Comparison<DrawItem> TransparentOrder = (a, b) =>
        {
            int c;
            if ((c = a.SortingLayerValue.CompareTo(b.SortingLayerValue)) != 0) return c;
            if ((c = a.SortingOrder.CompareTo(b.SortingOrder)) != 0) return c;
            if ((c = a.Queue.CompareTo(b.Queue)) != 0) return c;
            if ((c = b.Distance.CompareTo(a.Distance)) != 0) return c;
            if (a.Queued && b.Queued && (c = a.Material.GetInstanceID().CompareTo(b.Material.GetInstanceID())) != 0) return c;
            return a.Sequence.CompareTo(b.Sequence);
        };
    }

    /// The draws each renderer class submits.
    internal static class RendererDraws
    {
        static readonly int MainTex = ShaderPropertyIds.Get("_MainTex");
        static readonly int AlphaTex = ShaderPropertyIds.Get("_AlphaTex");
        static readonly int EnableExternalAlpha = ShaderPropertyIds.Get("_EnableExternalAlpha");
        static readonly int RendererColor = ShaderPropertyIds.Get("_RendererColor");
        static readonly int Flip = ShaderPropertyIds.Get("_Flip");

        /// Per-renderer geometry for renderer classes outside this file (particles…).
        public static Func<RendererNative, Camera, List<DrawItem>, bool> External;

        public static void Collect(RendererNative r, Camera camera, List<DrawItem> draws)
        {
            switch (r)
            {
                case MeshRendererNative m:
                    CollectMesh(m, draws);
                    break;
                case SpriteRendererNative s:
                    CollectSprite(s, draws);
                    break;
                case Particles.ParticleSystemRendererNative p:
                    CollectParticles(p, camera, draws);
                    break;
                case LineRendererNative l:
                    CollectLine(l, camera, draws);
                    break;
                default:
                    External?.Invoke(r, camera, draws);
                    break;
            }
        }

        static DrawItem Item(RendererNative r, MeshNative mesh, int subMesh, Material material, PropertySheet properties)
        {
            var d = FrameRenderer.NewDrawItem();
            d.Renderer = r;
            d.Mesh = mesh;
            d.SubMesh = subMesh;
            d.ObjectToWorld = r.GameObject.Transform.LocalToWorld;
            d.Material = material;
            d.Properties = properties;
            d.Queue = material.renderQueue;
            d.SortingLayerValue = SortingLayers.ValueOf(r.SortingLayerId);
            d.SortingOrder = r.SortingOrder;
            d.WorldBounds = r.WorldBounds;
            return d;
        }


        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RendererNative, MeshNative> particleMeshes = new();

        /// The particles built for this camera (world space): submesh i with material i.
        static void CollectParticles(Particles.ParticleSystemRendererNative r, Camera camera, List<DrawItem> draws)
        {
            var mesh = particleMeshes.GetValue(r, _ => new MeshNative());
            if (!r.BuildGeometry(camera, mesh, out var objectToWorld)) return;
            for (int i = 0; i < mesh.SubMeshes.Count && i < r.Materials.Length; i++)
            {
                var material = r.Materials[i];
                if (material == null || mesh.SubMeshes[i].Indices.Length == 0) continue;
                var item = Item(r, mesh, i, material, r.PropertyBlock);
                item.ObjectToWorld = objectToWorld;
                draws.Add(item);
            }
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RendererNative, MeshNative> lineMeshes = new();

        /// The line's strip built for this camera (world space), drawn with each material.
        static void CollectLine(LineRendererNative r, Camera camera, List<DrawItem> draws)
        {
            var mesh = lineMeshes.GetValue(r, _ => new MeshNative());
            var view = (TransformNative)camera.transform.NativeOrThrow;
            var rotation = view.WorldRotation;
            if (!r.BuildGeometry(Hierarchy.Position(view), rotation * Vector3.forward, rotation * Vector3.right, rotation * Vector3.up, !camera.orthographic, mesh)) return;
            foreach (var material in r.Materials)
            {
                if (material == null) continue;
                var item = Item(r, mesh, 0, material, r.PropertyBlock);
                item.ObjectToWorld = Matrix4x4.identity;
                draws.Add(item);
            }
        }

        /// Material i draws submesh i; extra materials draw the last submesh again. Without a
        /// MeshFilter the renderer draws its TextMesh's text.
        static void CollectMesh(MeshRendererNative r, List<DrawItem> draws)
        {
            Mesh mesh = null;
            foreach (var c in r.GameObject.Components)
                if (c is MeshFilter f && f.Native is MeshFilterNative fn) { mesh = fn.Mesh; break; }
            var m = mesh != null ? (MeshNative)mesh.NativeOrThrow : MeshRendererNative.TextMesh(r.GameObject)?.Geometry();
            if (m == null) return;
            int subMeshes = m.SubMeshes.Count;
            for (int i = 0; i < r.Materials.Length; i++)
            {
                var material = r.Materials[i];
                if (material == null || subMeshes == 0) continue;
                draws.Add(Item(r, m, Math.Min(i, subMeshes - 1), material, r.PropertyBlock));
            }
        }

        /// The sprite's mesh with the renderer colour baked into vertex colours and flips into
        /// positions; _RendererColor and _Flip are neutral so shaders using either path agree.
        static void CollectSprite(SpriteRendererNative r, List<DrawItem> draws)
        {
            var sprite = r.Sprite;
            if (sprite == null || r.Materials.Length == 0) return;
            var s = (SpriteNative)sprite.NativeOrThrow;
            var render = s.Render;
            if (render.Texture == null) return;
            var mesh = r.GeometryMesh(s);
            var props = new PropertySheet();
            if (r.PropertyBlock != null) props.CopyFrom(r.PropertyBlock);
            props.SetTexture(MainTex, render.Texture);
            if (render.AlphaTexture != null)
            {
                props.SetTexture(AlphaTex, render.AlphaTexture);
                props.SetFloat(EnableExternalAlpha, 1f);
            }
            props.SetVector(RendererColor, Vector4.one);
            props.SetVector(Flip, new Vector4(1f, 1f, 0f, 0f));
            foreach (var material in r.Materials)
                if (material != null) draws.Add(Item(r, mesh, 0, material, props));
        }
    }
}
