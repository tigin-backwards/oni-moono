using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OniMoono.Engine.Platform;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Engine.UI
{
    /// Unity's canvas manager: drives root canvas RectTransforms, runs the will-render callbacks,
    /// builds each canvas's draw list (element order and depth) and draws canvases.
    ///
    /// Per frame (PostLateUpdate): root canvas transforms, Canvas.preWillRenderCanvases,
    /// Canvas.willRenderCanvases (uGUI layout and graphic rebuilds), then the build. The build
    /// runs whether or not frames are rendered (depth feeds uGUI raycasting).
    internal static class CanvasSystem
    {
        static readonly List<CanvasNative> enabled = new();
        static int sequence;
        static bool installed;

        static Material defaultMaterial, etc1Material;

        public static Material DefaultMaterial => defaultMaterial != null ? defaultMaterial : defaultMaterial = DefaultFor("UI/Default", "Default UI Material");
        public static Material Etc1Material => etc1Material != null ? etc1Material : etc1Material = DefaultFor("UI/DefaultETC1", "ETC1 Supported UI Material");

        static Material DefaultFor(string shader, string name)
        {
            var s = Shader.Find(shader);
            return s == null ? null : new Material(s) { name = name, hideFlags = HideFlags.HideAndDontSave };
        }

        /// The sort roots built last, in render order.
        static readonly List<CanvasBatch> batches = new();

        public static void Install()
        {
            if (installed) return;
            installed = true;
            PlayerLoop.PostLateUpdate += Update;
            FrameRenderer.RenderOverlays += RenderOverlays;
            FrameRenderer.CollectDraws += CollectCameraDraws;
            Hierarchy.ParentChanged += ParentChanged;
        }

        public static void Enable(Canvas canvas)
        {
            var c = canvas.Data;
            if (!enabled.Contains(c)) enabled.Add(c);
            c.EnableSequence = ++sequence;
            // A root screen-space canvas is laid out the moment it becomes enabled (activation,
            // the component's enable, an instantiated active copy), as measured in the original
            // player; the canvas update keeps it current after that. A world-space or nested
            // canvas is not, so a Dropdown's template canvas (world space, as AddComponent leaves
            // it) cloned at the root and nested again keeps its own anchoring.
            if (c.RenderMode != RenderMode.WorldSpace) UpdateRootTransform(c);
            Rehomed(c);
            HierarchyChanged(c);
        }

        // ----- Order among canvases of the same sorting layer and order -----
        //
        // As measured in the original player (UnityProbe canvasorder, `CO` lines): canvases that
        // tie on sorting layer and order draw in list order. A canvas enabled (its component, or
        // its object activated) goes on top of its ties. Some changes put the whole list back in
        // creation order, the canvas component's instance ID ascending (the one made last at the
        // bottom): setting sortingOrder, whatever the value, on a canvas with no canvas component
        // above it (enabled, active or not), or on a nested canvas that overrides sorting and was
        // added by AddComponent to an active object (one added to an inactive object and
        // activated later does not re-sort; canvases instantiated from prefabs were not measured
        // and are treated as the latter); and an object moving to another parent with a live
        // canvas at or below it, nested or not.
        // Nothing else does: a canvas created and left at its order, destroyed, disabled,
        // instantiated, sibling order, overrideSorting, renderMode or Canvas.ForceUpdateCanvases.

        /// Puts the live canvases back in creation order (see above).
        public static void Resort()
        {
            if (enabled.Count == 0) return;
            var list = enabled.ToArray();
            Array.Sort(list, (a, b) => a.InstanceId.CompareTo(b.InstanceId));
            foreach (var c in list) c.EnableSequence = ++sequence;
        }

        /// Canvas.sortingOrder was set.
        public static void SortingOrderSet(CanvasNative c)
        {
            if (c.Destroyed) return;
            if (!UnderACanvas(c) || c.OverrideSorting && c.AddedToActiveObject) Resort();
        }

        /// Whether a canvas component, enabled or not, is on an object above this one, active or
        /// not: a canvas made on an inactive object under a canvas does not count as a root here.
        static bool UnderACanvas(CanvasNative c)
        {
            for (var t = c.GameObject?.Transform?.Parent; t != null; t = t.Parent)
                if (AnyCanvasOn(t.GameObject) != null) return true;
            return false;
        }

        static void ParentChanged(TransformNative t)
        {
            if (HasLiveCanvas(t)) Resort();
        }

        static bool HasLiveCanvas(TransformNative t)
        {
            if (!t.GameObject.ActiveInHierarchy) return false;
            if (PartsOf(t.GameObject).Canvas is { } c && IsLive(c)) return true;
            foreach (var child in t.Children)
                if (HasLiveCanvas(child)) return true;
            return false;
        }

        public static void Disable(Canvas canvas)
        {
            enabled.Remove(canvas.Data);
            Rehomed(canvas.Data);
            HierarchyChanged(canvas.Data);
        }

        /// overrideSorting changed: the canvas's elements move to another sort root.
        public static void SortingChanged(CanvasNative c) => Rehomed(c);

        // ----- Depth numbering -----
        //
        // As measured in the original (UnityProbe UiSemantics, `UIS depth`): a sort root numbers
        // the CanvasRenderers of the active objects it draws from 0, depth first in hierarchy
        // order, when it renders (after Canvas.willRenderCanvases), and only when its structure
        // changed since it last did: a renderer became active, was added or destroyed, a sibling
        // order changed, a nested canvas or a Mask was enabled or disabled, or the canvas itself
        // was. Nothing else renumbers it: an object deactivated keeps its number (and its gap)
        // until the next renumbering, which leaves it -1. A canvas enabled or disabled, or its
        // overrideSorting changed, leaves the elements at and below it -1 at once.

        /// A structural change at or below a transform: its sort root renumbers at the next build.
        /// The nearest canvas that overrides sorting (or is disabled: a root of its own) holds the
        /// change; a nested canvas that sorts with its parent passes it up.
        public static void Restructured(TransformNative t)
        {
            for (; t != null; t = t.Parent)
            {
                if (PartsOf(t.GameObject).Canvas is not { Destroyed: false } c) continue;
                c.DepthDirty = true;
                if (c.OverrideSorting || !IsLive(c)) return;
            }
        }

        /// A canvas enabled or disabled (or one that stopped or started sorting on its own): the
        /// elements at and below it read -1 until numbered again, and the sort roots among them
        /// and the one above renumber. Measured: disabling a nested canvas leaves its elements -1
        /// at once; disabling and enabling the root leaves every element -1 until it renders.
        static void Rehomed(CanvasNative c)
        {
            if (c.GameObject?.Transform is not { } t) return;
            Reset(t);
            Restructured(t.Parent);
        }

        static void Reset(TransformNative t)
        {
            var parts = PartsOf(t.GameObject);
            if (parts.Canvas is { Destroyed: false } c) c.DepthDirty = true;
            foreach (var r in parts.Renderers) r.DepthBatch = null;
            foreach (var child in t.Children) Reset(child);
        }

        /// Unity tells the scripts on the canvas's object and below (uGUI's graphics, masks and
        /// raycasters listen) that the canvas they belong to may have changed.
        static void HierarchyChanged(CanvasNative c) => Messages.SendToSubtree(c.GameObject, s => s.OnCanvasHierarchyChanged);

        static bool IsLive(CanvasNative c) => c.IsActiveAndEnabledState && !c.Destroyed;

        static CanvasNative CanvasOn(GameObjectNative go) => PartsOf(go).Canvas is { } c && IsLive(c) ? c : null;

        /// A GameObject's canvas, enabled or not.
        internal static CanvasNative AnyCanvasOn(GameObjectNative go) => PartsOf(go).Canvas is { } c && !c.Destroyed ? c : null;

        /// A GameObject's canvas components, found once per component list: GameObject.Components
        /// is copy-on-write, so the list is the same array until a component is added or removed.
        /// The canvas walks run every frame over the whole UI; their state (enabled, destroyed,
        /// alpha) is read from the components each time.
        static UiParts PartsOf(GameObjectNative go)
        {
            var parts = go.UiParts;
            var components = go.Components;
            if (parts != null && parts.Source == components) return parts;
            parts = new UiParts { Source = components };
            List<CanvasGroupNative> groups = null;
            List<CanvasRendererNative> renderers = null;
            List<BehaviourNative> raycasters = null;
            foreach (var comp in components)
                switch (comp)
                {
                    case Behaviour when comp.Native is BehaviourNative b && IsRaycaster(comp.GetType()):
                        (raycasters ??= new()).Add(b);
                        break;
                    case Canvas when comp.Native is CanvasNative c:
                        parts.Canvas ??= c;
                        break;
                    case CanvasGroup when comp.Native is CanvasGroupNative g:
                        (groups ??= new()).Add(g);
                        break;
                    case CanvasRenderer when comp.Native is CanvasRendererNative r:
                        (renderers ??= new()).Add(r);
                        break;
                }
            parts.Groups = groups?.ToArray() ?? Array.Empty<CanvasGroupNative>();
            parts.Renderers = renderers?.ToArray() ?? Array.Empty<CanvasRendererNative>();
            parts.Raycasters = raycasters?.ToArray() ?? Array.Empty<BehaviourNative>();
            go.UiParts = parts;
            return parts;
        }

        /// An EventSystems.BaseRaycaster (the UI package's; GraphicRaycaster derives from it), by name:
        /// the engine does not reference the package.
        static bool IsRaycaster(Type type)
        {
            lock (raycasterTypes)
            {
                if (raycasterTypes.TryGetValue(type, out bool known)) return known;
                bool found = false;
                for (var t = type; t != null && !found; t = t.BaseType) found = t.FullName == "UnityEngine.EventSystems.BaseRaycaster";
                raycasterTypes[type] = found;
                return found;
            }
        }

        static readonly Dictionary<Type, bool> raycasterTypes = new();

        static int EnabledRaycasters(UiParts parts)
        {
            int n = 0;
            foreach (var r in parts.Raycasters)
                if (r.IsActiveAndEnabledState) n++;
            return n;
        }

        /// Whether an enabled raycaster could test graphics below an object through the canvas
        /// they would belong to above it: one on an object above it, up to its nearest live canvas
        /// (a graphic's canvas is the nearest live one; uGUI's GraphicRaycaster tests the graphics
        /// of its own canvas). False when the object has a live canvas of its own.
        static bool RaycasterAbove(GameObjectNative go)
        {
            if (CanvasOn(go) != null) return false;
            for (var t = go.Transform.Parent; t != null; t = t.Parent)
            {
                var parts = PartsOf(t.GameObject);
                if (EnabledRaycasters(parts) > 0) return true;
                if (parts.Canvas is { } c && IsLive(c)) return false;
            }
            return false;
        }

        /// A canvas's root: the topmost canvas above it, up to and including the first disabled
        /// one. As measured in the original (UnityProbe CanvasProbe): a disabled canvas, like one
        /// on an inactive object, is a root of its own (isRootCanvas, its own render mode and
        /// scale factor), but it is still the root of the live canvases below it, which keep its
        /// render mode, scale factor and sorting. Its override-sorting descendants draw and take
        /// raycasts in its space; nothing else under it draws.
        public static CanvasNative RootOf(CanvasNative canvas)
        {
            var root = canvas;
            if (!IsLive(canvas)) return root;
            for (var t = canvas.GameObject.Transform.Parent; t != null; t = t.Parent)
                if (AnyCanvasOn(t.GameObject) is { } c)
                {
                    root = c;
                    if (!IsLive(c)) break;
                }
            return root;
        }

        /// The canvas whose sorting layer and order a canvas uses: the nearest canvas at or above
        /// it that overrides sorting, or its root (RootOf).
        public static CanvasNative SortingCanvasOf(CanvasNative canvas)
        {
            if (canvas.OverrideSorting || !IsLive(canvas)) return canvas;
            var result = canvas;
            for (var t = canvas.GameObject.Transform.Parent; t != null; t = t.Parent)
                if (AnyCanvasOn(t.GameObject) is { } c)
                {
                    result = c;
                    if (c.OverrideSorting || !IsLive(c)) return c;
                }
            return result;
        }

        /// The nearest live canvas at or above a GameObject.
        public static CanvasNative NearestCanvas(GameObjectNative go)
        {
            for (var t = go.Transform; t != null; t = t.Parent)
                if (CanvasOn(t.GameObject) is { } c) return c;
            return null;
        }

        public static Matrix4x4 ToCanvas(CanvasRendererNative r)
        {
            var canvas = NearestCanvas(r.GameObject);
            if (canvas == null) return r.GameObject.Transform.LocalToWorld;
            var space = canvas.GameObject.Transform;
            long spaceStamp = space.WorldStamp;
            return Relative(ref r.ToCanvasCache, default, spaceStamp, r.GameObject.Transform, lazySpace: space);
        }

        // ----- Products kept -----
        //
        // Matrix4x4's products sum in double as the original's do (ARCHITECTURE D25), at about
        // three times the cost of float arithmetic; the canvas update takes a few of them for each
        // element every frame, nearly all of the same matrices as the frame before (on Windows,
        // U57: 0.6 ms a frame). Each is kept with what it was computed from and computed again
        // only when that changed, so it is always the value a new computation would give.

        /// An element's world matrix taken into a space: the inverse of the space's world matrix
        /// times the element's, kept while both world matrices stand (TransformNative.WorldStamp;
        /// each computation of one takes a new number, never 0).
        internal struct RelativeCache
        {
            public long SpaceStamp, Stamp;
            public Matrix4x4 Value;
        }

        /// worldToSpace × t's world matrix, where worldToSpace is the inverse of the world matrix
        /// numbered spaceStamp. With lazySpace (the transform whose world matrix that is) the
        /// inverse passed is not used and is taken from it only when the product is computed.
        static Matrix4x4 Relative(ref RelativeCache cache, in Matrix4x4 worldToSpace, long spaceStamp, TransformNative t, TransformNative lazySpace = null)
        {
            long stamp = t.WorldStamp;
            if (cache.Stamp == stamp && cache.SpaceStamp == spaceStamp && keepProducts)
            {
                if (verifyProducts) VerifyProduct(cache.Value, (lazySpace != null ? lazySpace.LocalToWorld.inverse : worldToSpace) * t.LocalToWorld, t);
                return cache.Value;
            }
            cache.Value = (lazySpace != null ? lazySpace.LocalToWorld.inverse : worldToSpace) * t.LocalToWorld;
            cache.Stamp = stamp;
            cache.SpaceStamp = spaceStamp;
            return cache.Value;
        }

        /// An object's placement in its nested canvas's space (Collect): its parent's times its own
        /// local matrix, kept while the parent's stands (a number for each computation, 0 for the
        /// identity at the canvas) and its own position, rotation and scale have the same bits.
        static Matrix4x4 ToNearest(UiParts parts, in Matrix4x4 parentToNearest, long parentStamp, TransformNative t, out long stamp)
        {
            if (parts.ToNearestStamp != 0 && parts.ToNearestParent == parentStamp && SameBits(parts.ToNearestPosition, t.LocalPosition)
                && SameBits(parts.ToNearestRotation, t.LocalRotation) && SameBits(parts.ToNearestScale, t.LocalScale) && keepProducts)
            {
                if (verifyProducts) VerifyProduct(parts.ToNearest, parentToNearest * Matrix4x4.TRS(t.LocalPosition, t.LocalRotation, t.LocalScale), t);
                stamp = parts.ToNearestStamp;
                return parts.ToNearest;
            }
            parts.ToNearestPosition = t.LocalPosition;
            parts.ToNearestRotation = t.LocalRotation;
            parts.ToNearestScale = t.LocalScale;
            parts.ToNearest = parentToNearest * Matrix4x4.TRS(t.LocalPosition, t.LocalRotation, t.LocalScale);
            parts.ToNearestParent = parentStamp;
            parts.ToNearestStamp = stamp = ++toNearestStamps;
            return parts.ToNearest;
        }

        static long toNearestStamps;

        /// Tests: false computes every product again (as without the kept ones), and stores it.
        internal static bool keepProducts = true;

        /// ONI_VERIFY_CANVAS_PRODUCTS=1: each kept product used is computed again and must have
        /// the same bits.
        static readonly bool verifyProducts = Environment.GetEnvironmentVariable("ONI_VERIFY_CANVAS_PRODUCTS") == "1";
        static long productsVerified, productsWrong;

        static void VerifyProduct(in Matrix4x4 kept, in Matrix4x4 computed, TransformNative t)
        {
            if (!SameBits(kept, computed)) EngineLog.Engine($"canvas products: wrong ({++productsWrong}) at {t.GameObject.Name}: kept {kept}, computed {computed}");
            if (++productsVerified % 100000 == 0) EngineLog.Engine($"canvas products: {productsVerified} kept products verified, {productsWrong} wrong");
        }

        internal static bool SameBits(in Matrix4x4 a, in Matrix4x4 b)
            => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in a))
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in b)));

        static bool SameBits(Vector3 a, Vector3 b)
            => Unsafe.As<Vector3, long>(ref a) == Unsafe.As<Vector3, long>(ref b)
               && BitConverter.SingleToInt32Bits(a.z) == BitConverter.SingleToInt32Bits(b.z);

        static bool SameBits(Quaternion a, Quaternion b)
            => Unsafe.As<Quaternion, Int128>(ref a) == Unsafe.As<Quaternion, Int128>(ref b);

        static Vector2 ScreenSize
        {
            get
            {
                var p = PlatformServices.Current;
                return new Vector2(p.FramebufferWidth, p.FramebufferHeight);
            }
        }

        public static Vector2 DisplaySize(CanvasNative root)
        {
            if (root.RenderMode == RenderMode.ScreenSpaceCamera && root.WorldCamera != null)
            {
                var cam = (CameraNative)root.WorldCamera.NativeOrThrow;
                return new Vector2(cam.PixelWidth, cam.PixelHeight);
            }
            return ScreenSize;
        }

        public static Rect PixelRect(CanvasNative root)
        {
            if (root.RenderMode == RenderMode.ScreenSpaceCamera && root.WorldCamera != null)
                return root.WorldCamera.pixelRect;
            var size = ScreenSize;
            return new Rect(0f, 0f, size.x, size.y);
        }

        /// Screen-space root canvases fill the screen (or their camera's view at planeDistance):
        /// size = screen / scaleFactor, scale = scaleFactor (overlay), anchored at the centre.
        public static void UpdateRootTransform(CanvasNative canvas)
        {
            if (!IsLive(canvas) || RootOf(canvas) != canvas) return;
            if (canvas.GameObject.Transform is not RectTransformNative rt) return;
            if (canvas.RenderMode == RenderMode.WorldSpace) return;
            var size = DisplaySize(canvas);
            float scale = canvas.ScaleFactor;
            var sizeDelta = size / scale;
            Vector3 position;
            Quaternion rotation;
            Vector3 localScale;
            if (canvas.RenderMode == RenderMode.ScreenSpaceCamera && canvas.WorldCamera != null)
            {
                var cam = (CameraNative)canvas.WorldCamera.NativeOrThrow;
                var camTransform = cam.GameObject.Transform;
                float distance = canvas.PlaneDistance;
                rotation = camTransform.WorldRotation;
                position = Hierarchy.Position(camTransform) + rotation * new Vector3(0f, 0f, distance);
                float worldHeight = cam.Orthographic ? cam.OrthographicSize * 2f : 2f * distance * Mathf.Tan(cam.FieldOfView * 0.5f * Mathf.Deg2Rad);
                float s = worldHeight / sizeDelta.y;
                localScale = new Vector3(s, s, s);
            }
            else
            {
                position = new Vector3(size.x * 0.5f, size.y * 0.5f, 0f);
                rotation = Quaternion.identity;
                localScale = new Vector3(scale, scale, scale);
            }
            bool changed = rt.SizeDelta != sizeDelta || rt.LocalScale != localScale || rt.LocalRotation != rotation
                           || rt.AnchorMin != Vector2.zero || rt.AnchorMax != Vector2.zero || rt.Pivot != new Vector2(0.5f, 0.5f)
                           || new Vector2(rt.LocalPosition.x, rt.LocalPosition.y) != new Vector2(position.x, position.y) || rt.LocalPosition.z != position.z;
            if (!changed) return;
            rt.AnchorMin = Vector2.zero;
            rt.AnchorMax = Vector2.zero;
            rt.Pivot = new Vector2(0.5f, 0.5f);
            rt.SizeDelta = sizeDelta;
            rt.LocalScale = localScale;
            rt.LocalRotation = rotation;
            rt.AnchoredPosition = new Vector2(position.x, position.y);
            rt.LocalPosition = position;
            rt.MarkDirty();
            rt.Resolve();
        }

        /// Whether elements of the canvas snap to whole screen pixels, and the camera that
        /// projects them (null: overlay, where world space is screen pixels).
        public static bool SnapsToPixels(Canvas canvas, out Camera camera)
        {
            camera = null;
            if (canvas == null || !canvas.pixelPerfect) return false;
            var root = RootOf(canvas.Data);
            if (root.RenderMode == RenderMode.WorldSpace) return false;
            if (root.RenderMode == RenderMode.ScreenSpaceCamera) camera = root.WorldCamera;
            return true;
        }

        // ----- Per-frame update -----

        /// Canvas.preWillRenderCanvases and willRenderCanvases (uGUI's layout and graphic
        /// rebuilds). Nothing is sent to the rects changed while they run once they are over: a
        /// RectTransform hears of a change of its size from the change itself, and of a move not
        /// at all (measured in the original player, docs/cleanroom/ugui.md "Moved children of
        /// layout groups"), except on pixel-perfect canvases (SendMovesOnPixelPerfectCanvases,
        /// after it in the frame).
        public static void RunCanvasUpdate()
        {
            Canvas.SendPreWillRenderCanvases();
            Canvas.SendWillRenderCanvases();
        }

        static readonly List<RectTransformNative> movedRects = new();
        static readonly HashSet<RectTransformNative> movedSet = new();

        /// A RectTransform's place in the world changed (a position, rotation, scale or parent
        /// set, its own or an ancestor's). Kept for the next pass when the object is active and
        /// already has a CanvasRenderer: a move made before the renderer exists or while the
        /// object is inactive is never told, a move made after it is, in the frame the object
        /// was made too (measured, docs/cleanroom/ugui.md "Moves on pixel-perfect canvases").
        public static void NoteMoved(RectTransformNative rect)
        {
            var go = rect.GameObject;
            if (go == null || go.Components.Length < 2 || !go.ActiveInHierarchy || PartsOf(go).Renderers.Length == 0) return;
            if (movedSet.Add(rect)) movedRects.Add(rect);
        }

        /// After the canvas update: every active GameObject with a CanvasRenderer that moved since
        /// the last pass gets OnRectTransformDimensionsChange once, when its canvas is pixel
        /// perfect. As measured in the original (docs/cleanroom/ugui.md, "Moves on pixel-perfect
        /// canvases"): any position, rotation, scale or parent change counts, even one undone in
        /// the same frame, and so does an ancestor's; a size change alone, a sibling index, an
        /// activation and the placement of an object made or cloned do not. On other canvases a
        /// move sends nothing.
        static readonly ListSnapshot<RectTransformNative> movedSnapshot = new();
        static readonly ListSnapshot<CanvasNative> enabledSnapshot = new();

        static void SendMovesOnPixelPerfectCanvases()
        {
            if (movedRects.Count == 0) return;
            using var snapshot = movedSnapshot.Take(movedRects);
            movedRects.Clear();
            movedSet.Clear();
            foreach (var rect in snapshot.Items)
            {
                var go = rect.GameObject;
                if (rect.Destroyed || go == null || go.Destroyed || !go.ActiveInHierarchy) continue;
                if (NearestCanvas(go) is not { } canvas || !IsPixelPerfect(canvas)) continue;
                Messages.SendToComponents(go, s => s.OnRectTransformDimensionsChange);
            }
        }

        /// Canvas.pixelPerfect: the canvas's own setting when it overrides it or is a root,
        /// else its root's; never in world space.
        static bool IsPixelPerfect(CanvasNative canvas)
        {
            var root = RootOf(canvas);
            if (root.RenderMode == RenderMode.WorldSpace) return false;
            return canvas.OverridePixelPerfect || root == canvas ? canvas.PixelPerfect : root.PixelPerfect;
        }

        static void Update()
        {
            using (var snapshot = enabledSnapshot.Take(enabled))
                foreach (var c in snapshot.Items) UpdateRootTransform(c);
            RunCanvasUpdate();
            SendMovesOnPixelPerfectCanvases();
            if (stats)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                Build();
                statsTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                if (++statsFrames == 600)
                {
                    EngineLog.Engine($"canvas build: {statsTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / statsFrames:F3} ms a frame, {statsVisits / statsFrames} objects walked a frame, {statsRenumbered / (double)statsFrames:F2} sort roots renumbered");
                    statsFrames = 0; statsTicks = 0; statsVisits = 0; statsRenumbered = 0;
                }
            }
            else Build();
        }

        /// A root canvas, or a nested canvas that overrides sorting: drawn and depth-counted as
        /// a unit.
        internal sealed class CanvasBatch
        {
            public CanvasNative Canvas;
            public CanvasNative Root;
            public readonly List<CanvasDraw> Draws = new();
            /// The root canvas's local-to-world matrix and its inverse (geometry space).
            public Matrix4x4 SpaceToWorld, WorldToSpace;
            /// SpaceToWorld's number (TransformNative.WorldStamp).
            public long SpaceStamp;
        }

        internal readonly struct CanvasDraw(CanvasRendererNative renderer, CanvasNative space, Material material, bool pop, float groupAlpha,
            CanvasNative nearest = null, Matrix4x4 toNearest = default)
        {
            public readonly CanvasRendererNative Renderer = renderer;
            public readonly CanvasNative Space = space;
            public readonly Material Material = material;
            public readonly bool Pop = pop;
            /// Alpha inherited from CanvasGroups.
            public readonly float GroupAlpha = groupAlpha;
            /// The nested canvas the element is in, when it is not the root canvas, and the
            /// element's transform in that canvas's space.
            public readonly CanvasNative Nearest = nearest;
            public readonly Matrix4x4 ToNearest = toNearest;
        }

        /// The space an element's geometry is baked in: the element to that space, and that space
        /// to the world. An element in a nested canvas is baked in the nested canvas's space and
        /// drawn through the canvas's transform, so moving or scaling that canvas leaves its
        /// elements' geometry as it is. Unity's player batches a nested canvas the same way:
        /// observed in it, the research screen's tree (a nested canvas of about 5,000 elements)
        /// zooms at the frame time of a still screen, where rebaking and uploading every element
        /// each frame took 30 to 130 ms more, and the screen's zoom, which steps by the frame
        /// time, overshot its target. A rect-clipped element (RectMask2D) stays in the root
        /// canvas's space, where uGUI gives its clip rect. So does an element whose shader reads
        /// no vertex position: such a program places its geometry from unity_ObjectToWorld
        /// alone, which a UI draw sets to the root canvas's transform in the original. Klei's
        /// BatchedAnimationUI is one; its game code computes the Duplicant portraits' positions
        /// around the root canvas's centre, and in the Consumables table (a nested canvas
        /// whose pivot is its top right corner) they were drawn off the screen's corner.
        static (Matrix4x4 ToSpace, Matrix4x4 SpaceToWorld) SpaceOf(CanvasBatch b, in CanvasDraw d)
        {
            if (NestedSpace(d) is { } nested) return (d.ToNearest, nested.GameObject.Transform.LocalToWorld);
            return (Relative(ref d.Renderer.ToSpaceCache, b.WorldToSpace, b.SpaceStamp, d.Renderer.GameObject.Transform), b.SpaceToWorld);
        }

        /// The nested canvas a draw's geometry is baked in, or null for the root canvas's space.
        static CanvasNative NestedSpace(in CanvasDraw d)
            => d.Nearest != null && d.Renderer.ClipRect == null && !d.Nearest.Destroyed && ReadsVertexPosition(d.Material) ? d.Nearest : null;

        static bool ReadsVertexPosition(Material material)
        {
            var shader = material == null ? null : material.shader;
            return shader == null || shader.Native is not ShaderNative native || native.ReadsVertexPosition;
        }

        /// Objects the last build walked (tests).
        internal static int Walked;

        static void Build()
        {
            buildNumber++;
            Walked = 0;
            lastSortRoots.Clear();
            foreach (var b in batches) lastSortRoots.Add(b.Canvas);
            batches.Clear();
            int reused = 0;
            foreach (var c in enabled)
                if (IsLive(c) && c.GameObject.ActiveInHierarchy && (RootOf(c) == c || c.OverrideSorting))
                {
                    // Batch objects (and their draw lists) are reused from build to build.
                    CanvasBatch batch;
                    if (reused < batchPool.Count) batch = batchPool[reused];
                    else batchPool.Add(batch = new CanvasBatch());
                    reused++;
                    batch.Canvas = c;
                    batch.Root = RootOf(c);
                    batch.Draws.Clear();
                    batches.Add(batch);
                }
            // Geometry is in the root canvas's space: RectMask2D (shipped uGUI) computes its clip
            // rects in the root-most canvas's space, nested canvases included.
            foreach (var b in batches)
            {
                b.SpaceToWorld = b.Root.GameObject.Transform.LocalToWorld;
                b.SpaceStamp = b.Root.GameObject.Transform.WorldStamp;
                b.WorldToSpace = b.SpaceToWorld.inverse;
                var above = b.Canvas.GameObject.Transform.Parent;
                var (alpha, blocks) = above == null ? (1f, true) : InheritedGroups(above.GameObject);
                var c = b.Canvas;
                var depth = counter;
                depth.Value = 0;
                depth.RelativeBase = 0;
                depth.IgnoringGroups = 0;
                depth.Raycasters = 0;
                // A canvas that was not a sort root at the last build (one moved to the top level)
                // numbers its elements, whatever numbered them before.
                depth.Renumber = c.DepthDirty || c.SortRootBuild != buildNumber - 1;
                c.SortRootBuild = buildNumber;
                if (depth.Renumber)
                {
                    c.DepthDirty = false;
                    c.DepthEpoch++; // a raycast over its graphics reads it (RaycastInputs.Stamp)
                    if (stats) statsRenumbered++;
                }
                var top = c.GameObject.Transform;
                popRenderers.Clear();
                Collect(b, c.GameObject, b.Root, isBatchRoot: true, depth, c, top.LocalToWorld.inverse, top.WorldStamp, Matrix4x4.identity, 0, alpha, blocks);
            }
            // A canvas no longer a sort root (one moved under a canvas that draws it, or under a
            // disabled one) takes its numbers back, so uGUI does not raycast into it.
            foreach (var c in lastSortRoots)
                if (c.SortRootBuild != buildNumber)
                {
                    c.DepthEpoch++;
                    RaycastInputs.Changed();
                    c.DepthDirty = true;
                }
            // Render order: by sorting layer, sorting order, then list order (Resort).
            batches.Sort((a, b) =>
            {
                int c = SortingLayers.ValueOf(SortingOf(a).SortingLayerId).CompareTo(SortingLayers.ValueOf(SortingOf(b).SortingLayerId));
                if (c != 0) return c;
                if ((c = SortingOf(a).SortingOrder.CompareTo(SortingOf(b).SortingOrder)) != 0) return c;
                return a.Canvas.EnableSequence.CompareTo(b.Canvas.EnableSequence);
            });
            for (int i = 0; i < batches.Count; i++) batches[i].Canvas.RenderOrder = i;
        }

        static readonly List<CanvasBatch> batchPool = new();
        static readonly List<CanvasNative> lastSortRoots = new();
        static int buildNumber;

        static CanvasNative SortingOf(CanvasBatch b) => b.Canvas.OverrideSorting || b.Canvas == b.Root ? b.Canvas : b.Root;

        sealed class Counter
        {
            /// Renumbering the batch (its canvas's DepthDirty), and the next number.
            public bool Renumber;
            public int Value;
            /// Value where the nearest canvas began: relativeDepth counts from there.
            public int RelativeBase;
            /// Live CanvasGroups that ignore their parents' groups, met so far in the walk.
            public int IgnoringGroups;
            /// Enabled raycasters met so far in the walk.
            public int Raycasters;
        }

        static readonly Counter counter = new();

        /// Depth-first in hierarchy order; nested sort roots are skipped (they are batches of
        /// their own); an element's pop materials draw after its children. When renumbering
        /// (depth.Renumber), every element on an active object is numbered, as in Unity
        /// (measured: one without a Graphic or geometry, a disabled Graphic, alpha 0 by colour or
        /// through a CanvasGroup whether it blocks raycasts or not, culled by a RectMask2D,
        /// off-screen, zero-size, empty text); an element whose renderer has a pop instruction (a
        /// Mask's) takes one more number after its subtree. A nested canvas that sorts with its
        /// parent continues the parent's numbering, and its relativeDepth starts at 0. Only
        /// unculled elements with geometry that are not fully transparent through their
        /// CanvasGroups are drawn; a group below that ignores its parents' groups can make part of
        /// a transparent subtree visible again.
        ///
        /// A hidden subtree whose groups also stop raycasts can never be drawn or hit, so unless
        /// the batch renumbers there is nothing to do in it. Its top object records when it was
        /// walked so (CanvasHiddenAt) if no group in it ignores its parents; while nothing at or
        /// below it changes (SubtreeVersion), the walk skips it. Most of a large colony's UI is
        /// screens hidden this way (about 8,900 of 11,500 objects visited a frame in U57). So is a
        /// hidden subtree whose groups let raycasts through when no enabled raycaster could test
        /// its graphics: none in it, and none above it up to its nearest live canvas (read at each
        /// skip: an ancestor's raycaster enabled again changes nothing below). Hiding the research
        /// screen, the game sets its group's alpha to 0 and disables its two raycasters, and keeps
        /// it active with the group still blocking raycasts (U57: about 6,400 objects a frame).
        /// Such a top (CanvasNoRaycastersAt) is not one of OutOfSight's: a raycaster's Raycast,
        /// called while it is disabled, still tests its graphics. Test runs
        /// (ONI_VERIFY_CANVAS_SKIPS=1) check each skipped subtree as the walk would have seen it
        /// and log differences.
        static readonly List<CanvasRendererNative> popRenderers = new();

        static void Collect(CanvasBatch batch, GameObjectNative go, CanvasNative space, bool isBatchRoot, Counter depth,
            CanvasNative nearest, Matrix4x4 worldToNearest, long nearestStamp, Matrix4x4 parentToNearest, long parentStamp, float groupAlpha, bool blocksRaycasts)
        {
            if (!go.ActiveInHierarchy) return;
            if (stats) statsVisits++;
            Walked++;
            var parts = PartsOf(go);
            var canvasHere = parts.Canvas is { } here && !here.Destroyed ? here : null;
            // A nested sort root is a batch of its own; a disabled canvas draws nothing (its
            // override-sorting descendants are batches rooted at it).
            if (!isBatchRoot && canvasHere != null && (canvasHere.OverrideSorting || !IsLive(canvasHere))) return;
            // CanvasGroups multiply down the hierarchy; one that ignores its parents restarts it.
            bool skippedAbove = groupAlpha <= 0f && !blocksRaycasts;
            bool hiddenAbove = groupAlpha <= 0f;
            foreach (var g in parts.Groups)
                if (g.IsActiveAndEnabledState)
                {
                    if (g.IgnoreParentGroups) depth.IgnoringGroups++;
                    groupAlpha = g.IgnoreParentGroups ? g.Alpha : groupAlpha * g.Alpha;
                    blocksRaycasts = g.IgnoreParentGroups ? g.BlocksRaycasts : blocksRaycasts && g.BlocksRaycasts;
                }
            bool hidden = groupAlpha <= 0f;
            // The top of a hidden subtree that stops raycasts: skipped while unchanged since a walk
            // found it all hidden (only a group ignoring its parents could let raycasts in again,
            // and a subtree with one is never skipped).
            bool hiddenTop = hidden && !blocksRaycasts && !skippedAbove;
            // The top of a hidden subtree that lets raycasts through, where the parent is not
            // hidden: skipped likewise while no enabled raycaster can test it.
            bool unraycastTop = hidden && blocksRaycasts && !hiddenAbove;
            if (!depth.Renumber && (hiddenTop && go.CanvasHiddenAt == go.SubtreeVersion
                                    || unraycastTop && go.CanvasNoRaycastersAt == go.SubtreeVersion && !RaycasterAbove(go)))
            {
                if (verifySkips) VerifySkipped(batch.Canvas, go, isTop: true, raycasters: unraycastTop);
                return;
            }
            long hiddenWas = go.CanvasHiddenAt;
            go.CanvasHiddenAt = -1;
            go.CanvasNoRaycastersAt = -1;
            long version = go.SubtreeVersion;
            int ignoringBefore = depth.IgnoringGroups;
            int raycastersBefore = depth.Raycasters;
            depth.Raycasters += EnabledRaycasters(parts);
            int relativeBefore = depth.RelativeBase;
            if (canvasHere != null && canvasHere != nearest)
            {
                nearest = canvasHere;
                worldToNearest = go.Transform.LocalToWorld.inverse;
                nearestStamp = go.Transform.WorldStamp;
                depth.RelativeBase = depth.Value;
            }
            // Below a canvas other than the root, the object's placement in that canvas's space:
            // the product of the local transforms from the canvas down, so it stays exactly the
            // same while the canvas itself moves or scales (SpaceOf).
            var own = nearest != space ? nearest : null;
            long toNearestStamp = 0;
            var toNearest = own == null || go == own.GameObject ? Matrix4x4.identity
                : ToNearest(parts, parentToNearest, parentStamp, go.Transform, out toNearestStamp);
            // The renderers whose pop materials draw after the children: this call's stretch of
            // one list (each call removes its own before it returns).
            int popsFrom = popRenderers.Count;
            bool numbersPop = false;
            foreach (var r in parts.Renderers)
            {
                if (r.Destroyed) continue;
                if (depth.Renumber)
                {
                    // Numbered by another sort root before and still so: whether it had a depth
                    // followed that one's numbering, which a raycast here does not read (it reads
                    // the numbering of the sort roots above it).
                    if (r.DepthBatch != null && r.DepthBatch != batch.Canvas && r.HasDepth) RaycastInputs.Changed();
                    r.DepthBatch = batch.Canvas;
                    r.DepthEpoch = batch.Canvas.DepthEpoch;
                    r.AbsoluteDepth = depth.Value;
                    r.RelativeDepth = depth.Value - depth.RelativeBase;
                    depth.Value++;
                    numbersPop |= r.HasPopInstruction;
                }
                if (hidden && !blocksRaycasts) continue;
                r.BuiltToCanvas = Relative(ref r.ToNearestCache, worldToNearest, nearestStamp, r.GameObject.Transform);
                if (r.Cull || hidden || r.Geometry == null || r.Geometry.Indices.Length == 0) continue;
                if (CulledTransparent(r, groupAlpha)) continue;
                foreach (var m in r.Materials) batch.Draws.Add(new CanvasDraw(r, space, m, pop: false, groupAlpha, own, toNearest));
                if (r.HasPopInstruction) popRenderers.Add(r);
            }
            foreach (var child in go.Transform.Children)
                Collect(batch, child.GameObject, space, isBatchRoot: false, depth, nearest, worldToNearest, nearestStamp, toNearest, toNearestStamp, groupAlpha, blocksRaycasts);
            for (int i = popsFrom; i < popRenderers.Count; i++)
            {
                var r = popRenderers[i];
                foreach (var m in r.PopMaterials) batch.Draws.Add(new CanvasDraw(r, space, m, pop: true, groupAlpha, own, toNearest));
            }
            popRenderers.RemoveRange(popsFrom, popRenderers.Count - popsFrom);
            if (numbersPop)
                foreach (var r in parts.Renderers)
                    if (!r.Destroyed && r.HasPopInstruction) depth.Value++;
            depth.RelativeBase = relativeBefore;
            if (hiddenTop && depth.IgnoringGroups == ignoringBefore) go.CanvasHiddenAt = version;
            if (go.CanvasHiddenAt != hiddenWas) RaycastInputs.Changed(); // the raycasts' closed-screen skip reads it
            if (unraycastTop && depth.IgnoringGroups == ignoringBefore && depth.Raycasters == raycastersBefore && !RaycasterAbove(go))
                go.CanvasNoRaycastersAt = version;
        }

        // ----- Transparent meshes -----
        //
        // As measured in the original (UnityProbe cullalpha, `CA` lines; docs/cleanroom/ugui.md,
        // "CanvasRenderer.cullTransparentMesh"): a renderer with the flag on draws nothing, its pop
        // instruction included, while every vertex of its mesh has an alpha the flag counts as zero
        // (CanvasGeometry.ZeroAlpha). The renderer's colour and the CanvasGroups above it do not
        // enter the test (their alpha at 0 hides an element whatever the flag, in Item). It is
        // still numbered and still takes raycasts. A renderer added at run time has the flag on.
        // Nothing of it is collected, so its geometry is neither baked nor batched; the walk still
        // visits it for its depth and its placement (hasMoved).

        /// Skipped by its cullTransparentMesh. ONI_VERIFY_CANVAS_CULL=1 tests every vertex again
        /// for each renderer with the flag, in each build, and logs differences.
        static bool CulledTransparent(CanvasRendererNative r, float groupAlpha)
        {
            if (!r.CullTransparentMesh || !canvasCull) return false;
            bool culled = r.Geometry.Transparent;
            if (verifyCull) VerifyCull(r, culled, groupAlpha);
            return culled;
        }

        /// ONI_CANVAS_CULL=0 draws transparent meshes whatever their flag (for measuring).
        static readonly bool canvasCull = Environment.GetEnvironmentVariable("ONI_CANVAS_CULL") != "0";
        static readonly bool verifyCull = Environment.GetEnvironmentVariable("ONI_VERIFY_CANVAS_CULL") == "1";
        static long cullVerified, cullCulled, cullWrong;

        /// The element's vertices tested one by one, as its draw would carry them: culled exactly
        /// when the alpha of every one is zero for the flag, and so is the alpha its draw would
        /// bake (BuildMesh: each vertex's colour times the renderer's colour and the groups'): under
        /// an alpha-blended material such a draw changes an 8-bit target by under half a step.
        static void VerifyCull(CanvasRendererNative r, bool culled, float groupAlpha)
        {
            var g = r.Geometry;
            bool fresh = g.AllTransparent();
            var tint = new Color(r.Color.r, r.Color.g, r.Color.b, r.Color.a * groupAlpha);
            bool baked = g.Colors != null && g.Colors.Length >= g.Positions.Length;
            for (int i = 0; baked && i < g.Positions.Length; i++) baked = tint.a <= 0f || CanvasGeometry.ZeroAlpha((g.Colors[i] * tint).a);
            if (fresh != culled || culled && !baked)
                EngineLog.Engine($"canvas cull wrong ({++cullWrong}): {r.GameObject.Name}, culled {culled}, its vertices say {fresh}, its baked colours {baked}");
            if (culled) cullCulled++;
            if (++cullVerified % 10000 == 0)
                EngineLog.Engine($"canvas cull: {cullVerified} verified, {cullCulled} culled, {cullWrong} wrong");
        }

        // ----- Clipping out of sight -----
        //
        // A RectMask2D clips and culls its graphics every frame, closed screens' too: the game
        // keeps its closed screens active, hidden by a CanvasGroup, and a table screen once
        // opened holds a mask per row (2minnow, after Consumables, Vitals and Priorities: about
        // 5 ms a frame on Linux). Under a hidden subtree that stops raycasts nothing it clips
        // can be drawn or hit, so the mask skips its pass there and does a full one when it next
        // runs: its graphics' clip rects and culling are then set from their state at that
        // time, as the passes skipped would have left them (each is the clip and the graphic's
        // place then, whatever came before).

        /// Whether nothing at or below the object can be drawn or hit by a raycast: it is under
        /// the top of a subtree that the last build found hidden by CanvasGroups that stop
        /// raycasts, with no group in it that ignores its parents, nothing in it changed since
        /// (the walk's own skip), and still hidden so; and no canvas that sorts on its own lies
        /// between them (a graphic's raycast stops looking up at one, so the groups above it
        /// would not stop the raycast).
        internal static bool OutOfSight(GameObject gameObject)
        {
            if (!clipOutOfSight || gameObject == null || gameObject.Native is not GameObjectNative go) return false;
            for (var t = go.Transform; t != null; t = t.Parent)
            {
                var o = t.GameObject;
                if (o.CanvasHiddenAt >= 0 && o.CanvasHiddenAt == o.SubtreeVersion) return HiddenTop(o);
                if (AnyCanvasOn(o) is { OverrideSorting: true }) return false;
            }
            return false;
        }

        /// A marked top still hidden: its CanvasGroups' alpha is 0 (as the build inherits it, across
        /// canvases), and a raycast from below it meets a group that stops raycasts (as a
        /// graphic's raycast looks up: groups up to one that ignores its parents, stopping at a
        /// canvas that sorts on its own).
        static bool HiddenTop(GameObjectNative top) => InheritedGroups(top).Alpha <= 0f && !Reachable(top);

        static long unreachablePass, unreachableTouches = -1;

        /// OutOfSight for the graphics a GraphicRaycaster tests, each answer kept for the others
        /// below the same object while the hierarchy is unchanged. A graphic out of sight would
        /// fail its own raycast (Graphic.Raycast: a group that stops raycasts above it), so the
        /// raycaster leaves it out before its rect test; only its raycast filters, which decide
        /// nothing once a group has, are not asked.
        internal static bool Unreachable(GameObject gameObject)
        {
            if (!raycastOutOfSight || gameObject == null || gameObject.Native is not GameObjectNative go) return false;
            if (unreachableTouches != Hierarchy.Touches)
            {
                unreachableTouches = Hierarchy.Touches;
                unreachablePass++;
            }
            return Unreachable(go, unreachablePass);
        }

        static bool Unreachable(GameObjectNative o, long pass)
        {
            if (o.UnreachablePass == pass) return o.Unreachable;
            bool answer;
            if (o.CanvasHiddenAt >= 0 && o.CanvasHiddenAt == o.SubtreeVersion) answer = HiddenTop(o);
            else if (AnyCanvasOn(o) is { OverrideSorting: true }) answer = false;
            else answer = o.Transform.Parent is { } parent && Unreachable(parent.GameObject, pass);
            o.UnreachablePass = pass;
            o.Unreachable = answer;
            return answer;
        }

        /// A new raycast: answers kept from an earlier one are not used (group states can change
        /// without a hierarchy change only through Touch, but a frame apart they are read again).
        internal static void BeginRaycast() => unreachableTouches = -1;

        static long raycastSkipsVerified, raycastSkipsWrong;

        /// ONI_VERIFY_CANVAS_SKIPS: a graphic the raycaster left out was tested as before, and it
        /// must not have been hit.
        internal static void VerifyUnreachable(GameObject gameObject, bool hit)
        {
            if (hit) EngineLog.Engine($"canvas skip wrong ({++raycastSkipsWrong}): {gameObject.name}, left out of a raycast, was hit");
            if (++raycastSkipsVerified % 100000 == 0) EngineLog.Engine($"canvas skips: {raycastSkipsVerified} graphics left out of raycasts, {raycastSkipsWrong} wrong");
        }

        /// ONI_CLIP_OUT_OF_SIGHT=0 turns the skip off (for measuring).
        static readonly bool clipOutOfSight = Environment.GetEnvironmentVariable("ONI_CLIP_OUT_OF_SIGHT") != "0";
        /// ONI_RAYCAST_OUT_OF_SIGHT=0 turns the raycasts' skip off (for measuring).
        static readonly bool raycastOutOfSight = Environment.GetEnvironmentVariable("ONI_RAYCAST_OUT_OF_SIGHT") != "0";
        static long clipSkipsVerified, clipSkipsWrong;

        /// ONI_VERIFY_CANVAS_SKIPS: a graphic whose mask skipped its pass is neither drawn (its
        /// CanvasGroups' alpha is 0) nor reachable by a raycast (looking up from it as its
        /// raycast filters do, a group that stops raycasts comes before any canvas that sorts on
        /// its own and any group that ignores its parents).
        internal static void VerifyOutOfSight(GameObject gameObject)
        {
            if (gameObject == null || gameObject.Native is not GameObjectNative go) return;
            if (InheritedGroups(go).Alpha > 0f || Reachable(go))
                EngineLog.Engine($"canvas skip wrong ({++clipSkipsWrong}): {go.Name}, clipped out of sight, can be drawn or hit");
            if (++clipSkipsVerified % 100000 == 0) EngineLog.Engine($"canvas skips: {clipSkipsVerified} graphics skipped by masks, {clipSkipsWrong} wrong");
        }

        static bool Reachable(GameObjectNative go)
        {
            bool groupsIgnored = false;
            for (var t = go.Transform; t != null; t = t.Parent)
            {
                var parts = PartsOf(t.GameObject);
                foreach (var g in parts.Groups)
                {
                    if (!g.IsActiveAndEnabledState || groupsIgnored) continue;
                    if (!g.BlocksRaycasts) return false;
                    if (g.IgnoreParentGroups) groupsIgnored = true;
                }
                if (AnyCanvasOn(t.GameObject) is { OverrideSorting: true }) return true;
            }
            return true;
        }

        static readonly bool verifySkips = Environment.GetEnvironmentVariable("ONI_VERIFY_CANVAS_SKIPS") == "1";
        internal static bool VerifyingSkips => verifySkips;

        /// ONI_VERIFY_CANVAS_SKIPS: each check's totals at exit, as its periodic line, so one that
        /// ran fewer times than that line's step (U60's screens keep few masked graphics out of
        /// sight) still reports.
        internal static void LogSkipTotals()
        {
            if (!verifySkips) return;
            EngineLog.Engine($"canvas skips: {skipsVerified} verified, {skipsWrong} wrong");
            EngineLog.Engine($"canvas skips: {clipSkipsVerified} graphics skipped by masks, {clipSkipsWrong} wrong");
            EngineLog.Engine($"canvas skips: {raycastSkipsVerified} graphics left out of raycasts, {raycastSkipsWrong} wrong");
        }
        /// ONI_CANVAS_STATS=1: the build's time and the objects it walks, a frame, every 600 frames.
        static readonly bool stats = Environment.GetEnvironmentVariable("ONI_CANVAS_STATS") == "1";
        static int statsFrames;
        static long statsTicks, statsVisits, statsRenumbered;
        static long skipsVerified, skipsWrong;

        /// A skipped hidden subtree, as the walk would visit it: every element numbered by the
        /// batch's last renumbering and no live group that ignores its parents.
        static void VerifySkipped(CanvasNative batch, GameObjectNative go, bool isTop, bool raycasters = false)
        {
            if (isTop && ++skipsVerified % 10000 == 0) EngineLog.Engine($"canvas skips: {skipsVerified} verified, {skipsWrong} wrong");
            if (!go.ActiveInHierarchy) return;
            if (stats) statsVisits++;
            var parts = PartsOf(go);
            if (!isTop && parts.Canvas is { } c && !c.Destroyed && (c.OverrideSorting || !IsLive(c))) return;
            // The top's own groups set its alpha (one there may ignore its parents, as the
            // research screen's does); one below that ignores its parents could show it again.
            string why = null;
            if (!isTop)
                foreach (var g in parts.Groups)
                    if (g.IsActiveAndEnabledState && g.IgnoreParentGroups) why = "a group ignores its parents";
            foreach (var r in parts.Renderers)
                if (!r.Destroyed && (r.DepthBatch != batch || !r.HasDepth)) why = "an element without its depth";
            // Skipped for want of raycasters: none enabled in it, or above it to its canvas.
            if (raycasters && EnabledRaycasters(parts) > 0) why = "an enabled raycaster";
            if (raycasters && isTop && RaycasterAbove(go)) why = "an enabled raycaster above";
            if (why != null) EngineLog.Engine($"canvas skip wrong ({++skipsWrong}) at {go.Name}: {why}");
            foreach (var child in go.Transform.Children) VerifySkipped(batch, child.GameObject, isTop: false, raycasters);
        }

        /// Diagnostics (`--probe-ui`): the elements of the last build drawn at a screen point
        /// (pixels from the bottom left), in drawing order, with what they draw.
        public static string Probe(Vector2 screen)
        {
            var text = new System.Text.StringBuilder($"canvas elements at ({screen.x}, {screen.y}):");
            foreach (var b in batches)
            {
                if (!IsOverlay(b.Root)) continue;
                foreach (var d in b.Draws)
                {
                    var p = SpaceOf(b, d).SpaceToWorld.inverse.MultiplyPoint3x4(new Vector3(screen.x, screen.y, 0f));
                    var mesh = d.Renderer.Baked;
                    if (mesh == null || mesh.VertexCount == 0) continue;
                    var bounds = mesh.Bounds;
                    if (p.x < bounds.min.x || p.x > bounds.max.x || p.y < bounds.min.y || p.y > bounds.max.y) continue;
                    var path = new System.Text.StringBuilder();
                    for (var t = d.Renderer.GameObject.Transform; t != null; t = t.Parent) path.Insert(0, "/" + t.GameObject.Name);
                    var texture = d.Renderer.Texture ?? d.Material?.MainTextureQuiet;
                    var tnat = texture == null ? null : texture.Native as TextureNative;
                    text.Append($"\n  {path} depth {d.Renderer.AbsoluteDepth} material {d.Material?.name} ({d.Material?.shader?.name}) texture {texture?.name ?? "none"}{(texture == null ? "" : $" [native {(tnat == null ? "null" : $"#{tnat.InstanceId} v{tnat.ContentVersion} gpu {tnat.Gpu?.GetType().Name ?? "none"}")}]")} colour {d.Renderer.Color} group alpha {d.GroupAlpha}{(d.Pop ? " pop" : "")}");
                }
            }
            return text.ToString();
        }

        // ----- Drawing -----

        static readonly int MainTex = ShaderPropertyIds.Get("_MainTex");
        static readonly int AlphaTex = ShaderPropertyIds.Get("_AlphaTex");
        static readonly int ClipRectId = ShaderPropertyIds.Get("_ClipRect");
        static readonly int SoftnessX = ShaderPropertyIds.Get("_UIMaskSoftnessX");
        static readonly int SoftnessY = ShaderPropertyIds.Get("_UIMaskSoftnessY");
        static readonly int ZTestMode = ShaderPropertyIds.Get("unity_GUIZTestMode");
        static readonly int TextureSampleAdd = ShaderPropertyIds.Get("_TextureSampleAdd");

        const float CompareAlways = 8f, CompareLessEqual = 4f;

        /// Group alpha inherited through CanvasGroups up to (and including) one that ignores its
        /// parents.
        /// The CanvasGroups' alpha at an object, and whether they let raycasts through.
        internal static (float Alpha, bool BlocksRaycasts) InheritedGroups(GameObjectNative go)
        {
            float alpha = 1f;
            bool blocks = true;
            for (var t = go.Transform; t != null; t = t.Parent)
                foreach (var g in PartsOf(t.GameObject).Groups)
                    if (g.IsActiveAndEnabledState)
                    {
                        alpha *= g.Alpha;
                        blocks &= g.BlocksRaycasts;
                        if (g.IgnoreParentGroups) return (alpha, blocks);
                    }
            return (alpha, blocks);
        }

        static DrawItem Item(CanvasBatch b, in CanvasDraw d, bool overlay)
        {
            var r = d.Renderer;
            // An element destroyed, culled or cleared after the build (later in PostLateUpdate:
            // the game's video screen clears and tears itself down when its video ends) is not
            // drawn, as the build would not have collected it.
            if (r.Destroyed || r.Cull || r.Geometry == null || r.Geometry.Indices.Length == 0
                || r.CullTransparentMesh && canvasCull && r.Geometry.Transparent) return null;
            float alpha = r.Color.a * d.GroupAlpha;
            // An element whose inherited alpha (renderer alpha × CanvasGroups) is zero is not
            // drawn, as in Unity (screens hidden through a CanvasGroup stay active).
            if (d.Material == null || alpha <= 0f) return null;
            var (toSpace, spaceToWorld) = SpaceOf(b, d);
            var mesh = BuildMesh(r, toSpace, new Color(r.Color.r, r.Color.g, r.Color.b, alpha));
            var texture = r.Texture ?? d.Material.MainTextureQuiet;
            var state = new UiDrawState(
                d.Pop ? null : r.Texture,
                r.AlphaTexture,
                r.SecondaryTextures,
                texture is Texture2D t2 && t2.format == TextureFormat.Alpha8 ? new Vector4(1f, 1f, 1f, 0f) : Vector4.zero,
                r.ClipRect is { } clip ? new Vector4(clip.xMin, clip.yMin, clip.xMax, clip.yMax) : new Vector4(-32767f, -32767f, 32767f, 32767f),
                r.ClipRect != null ? r.ClippingSoftness : Vector2.zero,
                r.ClipRect != null,
                overlay ? CompareAlways : CompareLessEqual);
            var props = SheetFor(r, d.Material, d.Pop, state);
            var item = FrameRenderer.NewDrawItem();
            item.Mesh = mesh;
            item.ObjectToWorld = spaceToWorld;
            item.Material = d.Material;
            item.Properties = props;
            item.Keywords = r.ClipRect != null ? ClipKeywords : null;
            item.Queue = d.Material.renderQueue;
            return item;
        }

        static readonly string[] ClipKeywords = ["UNITY_UI_CLIP_RECT"];

        /// What an element's draw puts in its property sheet: the sheet's whole content, so an
        /// unchanged state leaves the sheet as it is, and equal states mean equal sheets.
        internal readonly record struct UiDrawState(Texture MainTexture, Texture AlphaTexture, (string Name, Texture2D Texture)[] Secondary,
                                           Vector4 SampleAdd, Vector4 Clip, Vector2 Softness, bool Clipped, float ZTest)
        {
            public void Fill(PropertySheet props)
            {
                props.Clear();
                if (MainTexture != null) props.SetTexture(MainTex, MainTexture);
                if (AlphaTexture != null) props.SetTexture(AlphaTex, AlphaTexture);
                foreach (var (name, tex) in Secondary)
                    if (name != null) props.SetTexture(ShaderPropertyIds.Get(name), tex);
                props.SetVector(TextureSampleAdd, SampleAdd);
                props.SetVector(ClipRectId, Clip);
                if (Clipped)
                {
                    props.SetFloat(SoftnessX, Softness.x);
                    props.SetFloat(SoftnessY, Softness.y);
                }
                props.SetFloat(ZTestMode, ZTest);
            }

            /// Same sheet content. Secondary texture lists are compared by reference; lists equal
            /// only in content are compared as sheets (SameState).
            public bool Same(in UiDrawState o)
                => ReferenceEquals(MainTexture, o.MainTexture) && ReferenceEquals(AlphaTexture, o.AlphaTexture) && ReferenceEquals(Secondary, o.Secondary)
                   && SampleAdd == o.SampleAdd && Clip == o.Clip && Clipped == o.Clipped && (!Clipped || Softness == o.Softness) && ZTest == o.ZTest;
        }

        /// The property sheet an element's draw with a material fills, kept on the element: the
        /// frame's draws are recorded before the next frame refills it. Refilled only when the
        /// draw's state changed: over 700 elements are drawn each frame in a large colony, nearly
        /// all of them unchanged.
        static PropertySheet SheetFor(CanvasRendererNative r, Material material, bool pop, in UiDrawState state)
        {
            var sheets = r.DrawSheets ??= new();
            for (int i = 0; i < sheets.Count; i++)
            {
                var (m, p, sheet, filled) = sheets[i];
                if (!ReferenceEquals(m, material) || p != pop) continue;
                if (!filled.Same(state))
                {
                    state.Fill(sheet);
                    sheets[i] = (m, p, sheet, state);
                }
                return sheet;
            }
            // Materials an element no longer uses are dropped once a few have gathered.
            if (sheets.Count >= 8) sheets.Clear();
            var created = new PropertySheet();
            state.Fill(created);
            sheets.Add((material, pop, created, state));
            return created;
        }

        // ----- Batching -----

        /// The batch's draws with consecutive draws of identical state (material, keywords,
        /// property values) merged into one mesh, as Unity's canvas batching does; the drawing
        /// order is unchanged.
        static List<DrawItem> Batched(CanvasBatch b, bool overlay)
        {
            var items = FrameRenderer.NewDrawList();
            // The run list is this call's (a call from inside it, none expected, takes its own).
            var run = batchRun ?? new List<(DrawItem Item, CanvasRendererNative Renderer)>();
            batchRun = null;
            UiDrawState runState = default;
            CanvasNative runSpace = null;
            nestedDraws.Clear();
            void Flush()
            {
                if (run.Count == 0) return;
                var item = run.Count == 1 ? run[0].Item : Merge(run);
                items.Add(item);
                if (runSpace != null) nestedDraws.Add((runSpace, item));
                run.Clear();
            }
            foreach (var d in b.Draws)
            {
                if (Item(b, d, overlay) is not { } item) continue;
                var state = StateOf(d.Renderer, item);
                var space = NestedSpace(d);
                // Elements baked in different spaces (a nested canvas's, the root's) draw apart.
                if (run.Count > 0 && (!SameState(run[0].Item, runState, item, state) || runSpace != space)) Flush();
                if (run.Count == 0)
                {
                    runState = state;
                    runSpace = space;
                }
                run.Add((item, d.Renderer));
            }
            Flush();
            batchRun = run;
            if (nestedDraws.Count > 0) ShareNestedMeshes();
            return items;
        }

        static List<(DrawItem Item, CanvasRendererNative Renderer)> batchRun = new();

        static readonly List<(CanvasNative Space, DrawItem Item)> nestedDraws = new();
        static readonly List<DrawItem> nestedGroup = new();

        sealed class NestedMesh
        {
            public MeshNative[] Parts = Array.Empty<MeshNative>();
            public int[] Versions = Array.Empty<int>();
            public readonly MeshNative Mesh = new() { Dynamic = true };
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CanvasNative, NestedMesh> nestedMeshes = new();

        /// A nested canvas's draws take their geometry from one mesh, a submesh each, as Unity
        /// keeps a canvas's batch in one buffer: the device then makes and fills a few buffers
        /// for the canvas rather than a few for each of its elements. The research screen's tree
        /// (about 3,800 draws) took 25,000 buffers when first shown, 370 to 470 ms on the GL
        /// thread where the original shows it within a frame of 84 ms. The mesh is rebuilt when
        /// one of its parts changed; a nested canvas that moves or scales does not change them.
        static void ShareNestedMeshes()
        {
            var group = nestedGroup;
            while (nestedDraws.Count > 0)
            {
                // Each nested canvas's draws, in drawing order (they need not be consecutive:
                // a canvas nested in another one draws between its parent's elements). The
                // others keep their order (a plain loop: RemoveAll's capturing predicate cost a
                // closure and a delegate for every nested canvas in every frame).
                var space = nestedDraws[0].Space;
                group.Clear();
                int kept = 0;
                for (int i = 0; i < nestedDraws.Count; i++)
                {
                    var e = nestedDraws[i];
                    if (e.Space == space) group.Add(e.Item);
                    else nestedDraws[kept++] = e;
                }
                nestedDraws.RemoveRange(kept, nestedDraws.Count - kept);
                var shared = nestedMeshes.GetValue(space, _ => new NestedMesh());
                bool same = shared.Parts.Length == group.Count;
                for (int i = 0; same && i < group.Count; i++)
                    same = ReferenceEquals(shared.Parts[i], group[i].Mesh) && shared.Versions[i] == group[i].Mesh.Version;
                if (!same)
                {
                    // Main-thread lists of the parts: rewritten in place at the same length.
                    if (shared.Parts.Length != group.Count)
                    {
                        shared.Parts = new MeshNative[group.Count];
                        shared.Versions = new int[group.Count];
                    }
                    for (int i = 0; i < group.Count; i++)
                    {
                        shared.Parts[i] = group[i].Mesh;
                        shared.Versions[i] = group[i].Mesh.Version;
                    }
                    Concatenate(shared.Parts, shared.Mesh, subMeshEach: true);
                }
                for (int i = 0; i < group.Count; i++)
                {
                    group[i].Mesh = shared.Mesh;
                    group[i].SubMesh = i;
                }
            }
            group.Clear();
        }

        static UiDrawState StateOf(CanvasRendererNative r, DrawItem item)
        {
            foreach (var (_, _, sheet, state) in r.DrawSheets)
                if (ReferenceEquals(sheet, item.Properties)) return state;
            throw new InvalidOperationException("A canvas draw's sheet is not the element's.");
        }

        static bool SameState(DrawItem a, in UiDrawState sa, DrawItem b, in UiDrawState sb)
        {
            if (!ReferenceEquals(a.Material, b.Material) || !ReferenceEquals(a.Keywords, b.Keywords)) return false;
            if (sa.Same(sb)) return true;
            // Different states mean different sheets, unless secondary textures are involved
            // (one may name a texture the state also sets): then compare the sheets themselves.
            if (sa.Secondary.Length == 0 && sb.Secondary.Length == 0) return false;
            var x = a.Properties;
            var y = b.Properties;
            if (x.Textures.Count != y.Textures.Count || x.Vectors.Count != y.Vectors.Count || x.Floats.Count != y.Floats.Count) return false;
            foreach (var (id, t) in x.Textures)
                if (!y.Textures.TryGetValue(id, out var u) || !ReferenceEquals(t.Texture, u.Texture) || t.ScaleOffset != u.ScaleOffset) return false;
            foreach (var (id, v) in x.Vectors)
                if (!y.Vectors.TryGetValue(id, out var w) || v != w) return false;
            foreach (var (id, f) in x.Floats)
                if (!y.Floats.TryGetValue(id, out var g) || f != g) return false;
            return true;
        }

        sealed class MergedRun
        {
            public MeshNative[] Parts = Array.Empty<MeshNative>();
            public int[] Versions = Array.Empty<int>();
            public readonly MeshNative Mesh = new() { Dynamic = true };
        }

        /// Merged meshes by the run's first renderer and material, reused while the parts are
        /// the same meshes at the same versions.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CanvasRendererNative, Dictionary<Material, MergedRun>> merged = new();

        static DrawItem Merge(List<(DrawItem Item, CanvasRendererNative Renderer)> run)
        {
            var first = run[0];
            var runs = merged.GetValue(first.Renderer, _ => new Dictionary<Material, MergedRun>());
            if (!runs.TryGetValue(first.Item.Material, out var m)) runs[first.Item.Material] = m = new MergedRun();
            bool same = m.Parts.Length == run.Count;
            for (int i = 0; same && i < run.Count; i++)
                same = ReferenceEquals(m.Parts[i], run[i].Item.Mesh) && m.Versions[i] == run[i].Item.Mesh.Version;
            if (!same)
            {
                if (m.Parts.Length != run.Count)
                {
                    m.Parts = new MeshNative[run.Count];
                    m.Versions = new int[run.Count];
                }
                for (int i = 0; i < run.Count; i++)
                {
                    m.Parts[i] = run[i].Item.Mesh;
                    m.Versions[i] = run[i].Item.Mesh.Version;
                }
                Concatenate(m.Parts, m.Mesh);
            }
            var item = first.Item;
            item.Mesh = m.Mesh;
            return item;
        }

        /// The parts' geometry in one mesh: one submesh, or one submesh for each part. The
        /// channel and index arrays are the merged mesh's own, from its spares
        /// (MeshNative.NewChannelAtLeast): Clear hands its previous ones back, and the mesh
        /// reuses them only once the device has run every upload that could hold them. A
        /// reused array can be longer than the vertex count and holds old values, so every
        /// value up to the count is written here, zeros where a part lacks a UV set.
        internal static void Concatenate(MeshNative[] parts, MeshNative mesh, bool subMeshEach = false)
        {
            int vertices = 0, indices = 0;
            bool normals = false, tangents = false;
            Span<bool> uvs = stackalloc bool[4];
            foreach (var p in parts)
            {
                vertices += p.VertexCount;
                indices += p.SubMeshes[0].IndexCount;
                normals |= p.Normals != null;
                tangents |= p.Tangents != null;
                for (int u = 0; u < 4; u++) uvs[u] |= p.Uvs[u] != null;
            }
            // The submeshes after the first, which Clear drops, are this merge's again.
            var spareSubs = mergeSubMeshes;
            spareSubs.Clear();
            for (int i = 1; i < mesh.SubMeshes.Count; i++) spareSubs.Add(mesh.SubMeshes[i]);
            mesh.Clear();
            var positions = mesh.NewChannelAtLeast<Vector3>(vertices);
            var colors = mesh.NewChannelAtLeast<Color>(vertices);
            var normalData = normals ? mesh.NewChannelAtLeast<Vector3>(vertices) : null;
            var tangentData = tangents ? mesh.NewChannelAtLeast<Vector4>(vertices) : null;
            var uvData = mergeUvs;
            for (int u = 0; u < 4; u++) uvData[u] = uvs[u] ? mesh.NewChannelAtLeast<Vector4>(vertices) : null;
            var indexData = subMeshEach ? null : mesh.NewChannelAtLeast<int>(indices);
            int v = 0, k = 0, sub = 0;
            foreach (var p in parts)
            {
                var source = p.SubMeshes[0].Indices;
                if (subMeshEach)
                {
                    // An array of each part's own: as spares, one for each of hundreds of parts
                    // would push the merged mesh's channel arrays out of its spares.
                    var part = new int[source.Length];
                    for (int i = 0; i < part.Length; i++) part[i] = source[i] + v;
                    // Clear left one empty submesh: the first part's; the others are the
                    // previous merge's, emptied as new ones are.
                    SubMeshData s;
                    if (sub++ == 0) s = mesh.SubMeshes[0];
                    else if (spareSubs.Count > 0)
                    {
                        s = spareSubs[^1];
                        spareSubs.RemoveAt(spareSubs.Count - 1);
                        s.Reset();
                    }
                    else s = new SubMeshData();
                    s.SetIndices(part);
                    if (s != mesh.SubMeshes[0]) mesh.SubMeshes.Add(s);
                }
                else
                    foreach (int index in source) indexData[k++] = index + v;
                int n = p.VertexCount;
                Array.Copy(p.Positions, 0, positions, v, n);
                Array.Copy(p.Colors, 0, colors, v, n);
                if (normalData != null)
                    for (int i = 0; i < n; i++) normalData[v + i] = p.Normals != null ? p.Normals[i] : new Vector3(0f, 0f, -1f);
                if (tangentData != null)
                    for (int i = 0; i < n; i++) tangentData[v + i] = p.Tangents != null ? p.Tangents[i] : new Vector4(1f, 0f, 0f, -1f);
                for (int u = 0; u < 4; u++)
                {
                    if (uvData[u] == null) continue;
                    if (p.Uvs[u] != null) Array.Copy(p.Uvs[u], 0, uvData[u], v, n);
                    else Array.Clear(uvData[u], v, n);
                }
                v += n;
            }
            mesh.VertexCount = vertices;
            mesh.Positions = positions;
            mesh.Give(MeshNative.SlotPositions, positions);
            mesh.Colors = colors;
            mesh.Give(MeshNative.SlotColors, colors);
            mesh.Normals = normalData;
            if (normalData != null) mesh.Give(MeshNative.SlotNormals, normalData);
            mesh.Tangents = tangentData;
            if (tangentData != null) mesh.Give(MeshNative.SlotTangents, tangentData);
            for (int u = 0; u < 4; u++)
            {
                mesh.Uvs[u] = uvData[u];
                mesh.UvDimensions[u] = uvData[u] != null ? 4 : 0;
                if (uvData[u] != null) mesh.Give(MeshNative.SlotUv0 + u, uvData[u]);
                uvData[u] = null;
            }
            spareSubs.Clear();
            if (!subMeshEach)
            {
                mesh.SubMeshes[0].SetIndices(indexData, indices);
                mesh.GiveIndices(mesh.SubMeshes[0]);
            }
            mesh.RecalculateBounds();
            mesh.Changed();
        }

        static readonly Vector4[][] mergeUvs = new Vector4[4][];
        static readonly List<SubMeshData> mergeSubMeshes = new();

        /// The element's geometry in its canvas's space with its colour applied; rebuilt only
        /// when the geometry, its placement or its colour changed.
        internal static MeshNative BuildMesh(CanvasRendererNative r, Matrix4x4 toSpace, Color color)
        {
            var g = r.Geometry;
            if (r.Baked != null && ReferenceEquals(r.BakedGeometry, g) && r.BakedToSpace.Equals(toSpace) && r.BakedColor.Equals(color))
                return r.Baked;
            int n = g.Positions.Length;
            var mesh = r.Baked ??= new MeshNative { Dynamic = true };
            // An element that only moved (world-space canvases follow their objects every frame)
            // keeps its colours: the array is never written once made, so the new version can
            // hold it too. The positions and colours made here are the baked mesh's alone and go
            // back to the canvas copies' arrays (CanvasArrays), reused once the device has run
            // the uploads that hold them.
            bool sameColors = !MeshNative.ReuseOff && ReferenceEquals(r.BakedGeometry, g) && r.BakedColor.Equals(color) && mesh.Colors is { } kept && kept.Length == n;
            mesh.VertexCount = n;
            CanvasArrays.Give(mesh.Positions, mesh);
            mesh.Positions = CanvasArrays.Take<Vector3>(n);
            if (!sameColors)
            {
                CanvasArrays.Give(mesh.Colors, mesh);
                var colors = CanvasArrays.Take<Color>(n);
                for (int i = 0; i < n; i++)
                {
                    var c = g.Colors != null && i < g.Colors.Length ? g.Colors[i] : Color.white;
                    colors[i] = c * color;
                }
                mesh.Colors = colors;
            }
            // The geometry baked before is no longer the element's (SetMesh made another): its
            // arrays go back to the baked mesh, whose uploads hold them, once it holds the new ones.
            var released = r.BakedGeometry is { } old && !ReferenceEquals(old, g) && !ReferenceEquals(old, r.CopiedGeometry) ? old : null;
            r.BakedGeometry = g;
            r.BakedToSpace = toSpace;
            r.BakedColor = color;
            toSpace.MultiplyPoints3x4(g.Positions, mesh.Positions);
            for (int u = 0; u < 4; u++)
            {
                mesh.Uvs[u] = g.Uvs[u];
                mesh.UvDimensions[u] = g.Uvs[u] != null ? 4 : 0;
            }
            mesh.Normals = g.Normals;
            mesh.Tangents = g.Tangents;
            // One submesh with the geometry's indices; its object is kept (nothing holds it
            // past the device's recording).
            if (mesh.SubMeshes.Count == 1) mesh.SubMeshes[0].SetIndices(g.Indices);
            else mesh.SubMeshes = new List<SubMeshData> { new SubMeshData(g.Indices) };
            if (released != null)
            {
                released.Release(mesh);
                r.SpareGeometry = released;
            }
            mesh.RecalculateBounds();
            mesh.Changed();
            return mesh;
        }

        static bool IsOverlay(CanvasNative root) => root.RenderMode == RenderMode.ScreenSpaceOverlay
                                                     || (root.RenderMode == RenderMode.ScreenSpaceCamera && root.WorldCamera == null);

        /// Overlay canvases draw after every camera, in render order, in screen pixels, over a
        /// depth buffer cleared once before the first of them. Measured in the original
        /// (UnityProbe UiZTest): a shader that tests depth itself (the Colony Summary's chart lines,
        /// "Lines/Colored Blended", ZTest LEqual) draws over what the cameras left at any depth,
        /// while a nearer depth-writing draw before it, in its canvas or a lower one, hides it.
        static void RenderOverlays()
        {
            var device = GraphicsDevice.Current;
            var size = ScreenSize;
            bool bound = false;
            foreach (var b in batches)
            {
                if (!IsOverlay(b.Root) || b.Draws.Count == 0) continue;
                if (!bound)
                {
                    FrameRenderer.Bind(device, FrameRenderer.ScreenBinding);
                    var pixels = FrameRenderer.ScreenPixelSize;
                    device.SetViewport(new Rect(0f, 0f, pixels.x, pixels.y));
                    device.SetCamera(new CameraConstants(Matrix4x4.identity, FrameRenderer.ScreenProjection(Matrix4x4.Ortho(0f, size.x, 0f, size.y, -1000f, 1000f)), Vector3.zero, -1000f, 1000f, true, size));
                    device.Clear(false, true, default, 1f);
                    bound = true;
                }
                // Canvas items name no pass (-1).
                FrameRenderer.DrawQueue(device, Batched(b, overlay: true));
            }
        }

        /// Screen Space - Camera canvases draw with their camera; World Space canvases with
        /// every camera that sees their layer. Each canvas sorts as one unit.
        static void CollectCameraDraws(Camera camera, CameraNative cam, List<DrawItem> draws)
        {
            foreach (var b in batches)
            {
                var root = b.Root;
                if (IsOverlay(root)) continue;
                if (root.RenderMode == RenderMode.ScreenSpaceCamera && root.WorldCamera != camera) continue;
                if (root.RenderMode == RenderMode.WorldSpace && (cam.CullingMask & (1 << b.Canvas.GameObject.Layer)) == 0) continue;
                var sorting = SortingOf(b);
                var center = Hierarchy.Position(b.Canvas.GameObject.Transform);
                foreach (var item in Batched(b, overlay: false))
                {
                    item.SortingLayerValue = SortingLayers.ValueOf(sorting.SortingLayerId);
                    item.SortingOrder = sorting.SortingOrder;
                    item.WorldBounds = new Bounds(center, Vector3.zero);
                    draws.Add(item);
                }
            }
        }
    }

    /// The canvas components of a GameObject's component list (Source), in component order.
    internal sealed class UiParts
    {
        public Component[] Source;
        public CanvasNative Canvas;
        public CanvasGroupNative[] Groups;
        public CanvasRendererNative[] Renderers;
        /// Its raycasters (EventSystems.BaseRaycaster), enabled or not.
        public BehaviourNative[] Raycasters;
        /// The object's placement in its nested canvas's space, kept (CanvasSystem.ToNearest).
        public Matrix4x4 ToNearest;
        public long ToNearestStamp, ToNearestParent;
        public Vector3 ToNearestPosition, ToNearestScale;
        public Quaternion ToNearestRotation;
    }
}
