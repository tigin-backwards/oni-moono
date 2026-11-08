using System.Collections.Generic;
using UnityEngine.EventSystems;

namespace UnityEngine.UI
{
    /// Raycasts the pointer against the graphics of its canvas.
    public partial class GraphicRaycaster
    {
        [System.NonSerialized] Canvas cachedCanvas;
        [System.NonSerialized] readonly List<Graphic> hits = new();
        [System.NonSerialized] readonly Pass pass = new();

        protected GraphicRaycaster()
        {
            m_IgnoreReversedGraphics = true;
            m_BlockingObjects = BlockingObjects.None;
            m_BlockingMask = -1;
        }

        /// An overlay canvas ranks by its sorting order and render order among the raycasters.
        public override int sortOrderPriority => canvas.renderMode == RenderMode.ScreenSpaceOverlay ? canvas.sortingOrder : base.sortOrderPriority;

        public override int renderOrderPriority => canvas.renderMode == RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.renderOrder : base.renderOrderPriority;

        public bool ignoreReversedGraphics
        {
            get => m_IgnoreReversedGraphics;
            set => m_IgnoreReversedGraphics = value;
        }

        public BlockingObjects blockingObjects
        {
            get => m_BlockingObjects;
            set => m_BlockingObjects = value;
        }

        public LayerMask blockingMask
        {
            get => m_BlockingMask;
            set => m_BlockingMask = value;
        }

        Canvas canvas => cachedCanvas != null ? cachedCanvas : cachedCanvas = GetComponent<Canvas>();

        /// Null for an overlay canvas (and a camera canvas without its camera), else the
        /// canvas's camera, or the main camera.
        public override Camera eventCamera
        {
            get
            {
                var c = canvas;
                var mode = c.renderMode;
                if (mode == RenderMode.ScreenSpaceOverlay || (mode == RenderMode.ScreenSpaceCamera && c.worldCamera == null)) return null;
                return c.worldCamera != null ? c.worldCamera : Camera.main;
            }
        }

        /// Hits of the canvas's raycast targets under the pointer on the canvas's display, front
        /// first; graphics facing away (with ignoreReversedGraphics), behind the camera or behind
        /// a blocking physics hit are left out.
        public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
        {
            var c = canvas;
            if (c == null) return;
            var graphics = GraphicRegistry.GetRaycastableGraphicsForCanvas(c, out long listed);
            if (graphics == null || graphics.Count == 0) return;

            var camera = eventCamera;
            int display = c.renderMode == RenderMode.ScreenSpaceOverlay || camera == null ? c.targetDisplay : camera.targetDisplay;
            // The display under the pointer (z); where the platform cannot tell (a zero result), the
            // pointer's own display.
            Vector3 position = Display.RelativeMouseAt(eventData.position);
            if (position == Vector3.zero) position = new Vector3(eventData.position.x, eventData.position.y, eventData.displayIndex);
            if ((int)position.z != display) return;

            // Points outside the display hit nothing.
            Vector2 viewport;
            if (camera == null)
            {
                float w = Screen.width, h = Screen.height;
                if (display > 0 && display < Display.displays.Length)
                {
                    w = Display.displays[display].systemWidth;
                    h = Display.displays[display].systemHeight;
                }
                viewport = new Vector2(position.x / w, position.y / h);
            }
            else viewport = camera.ScreenToViewportPoint(position);
            if (viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f) return;

            float blockedAt = float.MaxValue;
            var ray = new Ray();
            if (camera != null) ray = camera.ScreenPointToRay(position);
            if (c.renderMode != RenderMode.ScreenSpaceOverlay && blockingObjects != BlockingObjects.None)
            {
                float reach = 100f;
                if (camera != null)
                {
                    float dz = ray.direction.z;
                    reach = Mathf.Approximately(0f, dz) ? Mathf.Infinity : Mathf.Abs((camera.farClipPlane - camera.nearClipPlane) / dz);
                }
                if ((blockingObjects == BlockingObjects.ThreeD || blockingObjects == BlockingObjects.All)
                    && Physics.Raycast(ray, out var hit, reach, m_BlockingMask))
                    blockedAt = hit.distance;
                // 2D blocking (TwoD, All) needs a 2D physics ray query, which the engine does not
                // have: it keeps 2D collider shapes but simulates and queries no 2D physics
                // (Physics/Physics2D.cs). 2D colliders therefore block nothing, with the shipped
                // package on this engine as well.
            }

            hits.Clear();
            if (OniMoono.Engine.UI.RaycastInputs.Enabled) pass.Collect(c, camera, position, graphics, listed, hits);
            else CollectHits(camera, position, graphics, hits, null);
            foreach (var g in hits)
            {
                var go = g.gameObject;
                var t = go.transform;
                if (m_IgnoreReversedGraphics && !FacesCamera(t, camera)) continue;
                float distance = 0f;
                if (camera != null && c.renderMode != RenderMode.ScreenSpaceOverlay)
                {
                    // Along the ray to the graphic's plane; behind the camera it is not hit.
                    var forward = t.forward;
                    distance = Vector3.Dot(forward, t.position - ray.origin) / Vector3.Dot(forward, ray.direction);
                    if (distance < 0f) continue;
                }
                if (distance >= blockedAt) continue;
                resultAppendList.Add(new RaycastResult
                {
                    gameObject = go,
                    module = this,
                    distance = distance,
                    screenPosition = position,
                    displayIndex = display,
                    index = resultAppendList.Count,
                    depth = g.depth,
                    sortingLayer = c.sortingLayerID,
                    sortingOrder = c.sortingOrder,
                    worldPosition = ray.origin + ray.direction * distance,
                    worldNormal = -t.forward,
                });
            }
        }

        static bool FacesCamera(Transform t, Camera camera)
        {
            if (camera == null) return Vector3.Dot(Vector3.forward, t.rotation * Vector3.forward) > 0f;
            var ct = camera.transform;
            var nearPlane = ct.rotation * Vector3.forward * camera.nearClipPlane;
            return Vector3.Dot(t.position - ct.position - nearPlane, t.forward) >= 0f;
        }

        /// The last pass over the graphics, kept for the next raycast while nothing it read before
        /// the graphics' raycast filters has changed: the graphics the filters were asked about
        /// (`passed`, in list order), for the same point, list, camera mapping and far plane,
        /// and RaycastInputs' numbers. A reuse asks the same graphics' filters (Graphic.Raycast:
        /// their enabled state, CanvasGroups, masks, alpha tests and the game's filters, which may
        /// read anything) and sorts what they accept by depth as the pass would; one whose
        /// filters changed what it read is not kept. ONI_VERIFY_RAYCAST_CACHE=1 makes each
        /// reused pass afresh too and logs any difference.
        sealed class Pass
        {
            readonly List<Graphic> passed = new();
            readonly List<Graphic> freshPassed = new(), freshHits = new();
            bool kept;
            IList<Graphic> list;
            long listed;
            int count;
            float x, y, far;
            Camera camera;
            OniMoono.Engine.Rendering.CameraNative.ScreenMapping mapping;
            OniMoono.Engine.UI.RaycastInputs.Stamp stamp = new(), stampNow = new(), stampAfter = new();

            public void Collect(Canvas canvas, Camera cam, Vector2 point, IList<Graphic> graphics, long listedNow, List<Graphic> results)
            {
                stampNow.Take(canvas);
                OniMoono.Engine.Rendering.CameraNative.ScreenMapping mappingNow = default;
                float farNow = 0f;
                if (cam != null)
                {
                    mappingNow = cam.Data.Mapping;
                    farNow = cam.farClipPlane;
                }
                if (kept && stampNow.IsSet && stamp.Same(stampNow) && ReferenceEquals(list, graphics) && listed == listedNow && count == graphics.Count
                    && Same(x, point.x) && Same(y, point.y) && ReferenceEquals(camera, cam)
                    && (cam == null || (Same(far, farNow) && mapping.Same(mappingNow))))
                {
                    foreach (var g in passed)
                        if (g.Raycast(point, cam)) results.Add(g);
                    results.Sort(ByDepth);
                    OniMoono.Engine.UI.RaycastInputs.Reused();
                    if (OniMoono.Engine.UI.RaycastInputs.Verifying) Verify(canvas, cam, point, graphics, results);
                    return;
                }
                if (OniMoono.Engine.UI.RaycastInputs.Verifying) OniMoono.Engine.UI.RaycastInputs.Made(MissReason(graphics, listedNow, point, cam, farNow, mappingNow));
                else OniMoono.Engine.UI.RaycastInputs.Made(null);
                passed.Clear();
                CollectHits(cam, point, graphics, results, passed);
                // Kept unless the filters changed what the pass read.
                stampAfter.Take(canvas);
                kept = stampNow.IsSet && stampNow.Same(stampAfter) && listedNow == Listed(canvas);
                (stamp, stampNow) = (stampNow, stamp);
                list = graphics;
                listed = listedNow;
                count = graphics.Count;
                x = point.x;
                y = point.y;
                camera = cam;
                mapping = mappingNow;
                far = farNow;
                if (!kept) passed.Clear();
            }

            string MissReason(IList<Graphic> graphics, long listedNow, Vector2 point, Camera cam, float farNow, OniMoono.Engine.Rendering.CameraNative.ScreenMapping mappingNow)
            {
                if (!kept) return list == null ? "first" : "not kept";
                if (!stampNow.IsSet) return "no top object";
                if (stamp.Differs(stampNow) is { } d) return d;
                if (!ReferenceEquals(list, graphics) || listed != listedNow || count != graphics.Count) return "graphic list";
                if (!Same(x, point.x) || !Same(y, point.y)) return "pointer";
                if (!ReferenceEquals(camera, cam)) return "camera";
                return "camera mapping";
            }

            static long Listed(Canvas canvas)
            {
                GraphicRegistry.GetRaycastableGraphicsForCanvas(canvas, out long version);
                return version;
            }

            void Verify(Canvas canvas, Camera cam, Vector2 point, IList<Graphic> graphics, List<Graphic> results)
            {
                freshPassed.Clear();
                freshHits.Clear();
                CollectHits(cam, point, graphics, freshHits, freshPassed);
                string difference = Differ("filtered", passed, freshPassed) ?? Differ("hits", results, freshHits);
                OniMoono.Engine.UI.RaycastInputs.Verified(canvas.name, difference);
                freshPassed.Clear();
                freshHits.Clear();
            }

            static string Differ(string what, List<Graphic> kept, List<Graphic> fresh)
            {
                for (int i = 0; i < kept.Count || i < fresh.Count; i++)
                {
                    var a = i < kept.Count ? kept[i] : null;
                    var b = i < fresh.Count ? fresh[i] : null;
                    if (ReferenceEquals(a, b)) continue;
                    return $"{what} {kept.Count} reused vs {fresh.Count} afresh, first at {i}: {Name(a)} vs {Name(b)}";
                }
                return null;
            }

            static string Name(Graphic g) => g is null ? "none" : g == null ? "destroyed" : $"{g.name} (depth {g.depth})";

            static bool Same(float a, float b) => System.BitConverter.SingleToInt32Bits(a) == System.BitConverter.SingleToInt32Bits(b);
        }

        static readonly System.Comparison<Graphic> ByDepth = (a, b) => b.depth.CompareTo(a.depth);

        /// The drawn, unculled raycast targets containing the point (with their raycast padding)
        /// that accept it, by depth from the front; `passed` (when given) gets those whose filters
        /// were asked.
        static void CollectHits(Camera camera, Vector2 point, IList<Graphic> graphics, List<Graphic> results, List<Graphic> passed)
        {
            // Graphics under a closed screen (hidden by a CanvasGroup that stops raycasts) would
            // fail their own raycast: they are left out before the rect test
            // (OniMoono.Engine.UI.CanvasSystem.Unreachable). Test runs test them all and check.
            OniMoono.Engine.UI.CanvasSystem.BeginRaycast();
            bool verify = OniMoono.Engine.UI.CanvasSystem.VerifyingSkips;
            for (int i = 0; i < graphics.Count; i++)
            {
                var g = graphics[i];
                if (!g.raycastTarget || g.canvasRenderer.cull || g.depth == -1) continue;
                bool unreachable = OniMoono.Engine.UI.CanvasSystem.Unreachable(g.gameObject);
                if (unreachable && !verify) continue;
                bool inside = RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, point, camera, g.raycastPadding)
                    && (camera == null || !(camera.WorldToScreenPoint(g.rectTransform.position).z > camera.farClipPlane));
                if (inside && !unreachable) passed?.Add(g);
                bool hit = inside && g.Raycast(point, camera);
                if (unreachable)
                {
                    OniMoono.Engine.UI.CanvasSystem.VerifyUnreachable(g.gameObject, hit);
                    continue;
                }
                if (hit) results.Add(g);
            }
            results.Sort(ByDepth);
        }
    }
}
