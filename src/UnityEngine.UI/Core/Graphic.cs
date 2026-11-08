using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace UnityEngine.UI
{
    /// The base of everything uGUI draws. It keeps the element's dirty state, rebuilds its mesh
    /// and material in the canvas update, and registers it with its canvas for raycasting.
    public abstract partial class Graphic
    {
        protected static Material s_DefaultUI = null;
        protected static Texture2D s_WhiteTexture = null;

        static Mesh sharedMesh;
        static readonly VertexHelper sharedVertices = new();

        bool verticesDirty;
        bool materialDirty;
        [NonSerialized] protected UnityAction m_OnDirtyLayoutCallback;
        [NonSerialized] protected UnityAction m_OnDirtyVertsCallback;
        [NonSerialized] protected UnityAction m_OnDirtyMaterialCallback;
        RectTransform cachedRect;
        CanvasRenderer cachedRenderer;
        Canvas cachedCanvas;
        IEnumerator fade;

        protected Graphic()
        {
            m_Color = Color.white;
            m_RaycastTarget = true;
            m_RaycastPadding = Vector4.zero;
        }

        /// The engine's default UI material ("UI/Default").
        public static Material defaultGraphicMaterial
        {
            get
            {
                if (s_DefaultUI == null) s_DefaultUI = Canvas.GetDefaultCanvasMaterial();
                return s_DefaultUI;
            }
        }

        /// The mesh every graphic fills before handing it to its canvas renderer (which copies it).
        protected static Mesh workerMesh
        {
            get
            {
                if (sharedMesh == null)
                    sharedMesh = new Mesh { name = "Shared UI Mesh", hideFlags = HideFlags.HideAndDontSave };
                return sharedMesh;
            }
        }

        /// Whether the mesh is generated through OnPopulateMesh(Mesh) and IMeshModifier.ModifyMesh(Mesh).
        protected bool useLegacyMeshGeneration { get; set; }

        public virtual Color color
        {
            get => m_Color;
            set
            {
                if (m_Color.r == value.r && m_Color.g == value.g && m_Color.b == value.b && m_Color.a == value.a) return;
                m_Color = value;
                SetVerticesDirty();
            }
        }

        /// Raycast targets are listed separately on their canvas for the GraphicRaycaster.
        public virtual bool raycastTarget
        {
            get => m_RaycastTarget;
            set
            {
                if (m_RaycastTarget == value) return;
                if (m_RaycastTarget) GraphicRegistry.UnregisterRaycastGraphicForCanvas(cachedCanvas, this);
                m_RaycastTarget = value;
                OniMoono.Engine.UI.RaycastInputs.Changed();
                if (m_RaycastTarget && isActiveAndEnabled) GraphicRegistry.RegisterRaycastGraphicForCanvas(canvas, this);
            }
        }

        /// Insets of the raycast area (left, bottom, right, top); negative values extend it.
        public Vector4 raycastPadding
        {
            get => m_RaycastPadding;
            set
            {
                if (!m_RaycastPadding.Equals(value)) OniMoono.Engine.UI.RaycastInputs.Changed();
                m_RaycastPadding = value;
            }
        }

        public void SetRaycastDirty()
        {
            if (!IsActive()) return;
            GraphicRegistry.UnregisterRaycastGraphicForCanvas(cachedCanvas, this);
            if (m_RaycastTarget) GraphicRegistry.RegisterRaycastGraphicForCanvas(canvas, this);
        }

        public virtual void SetAllDirty()
        {
            SetLayoutDirty();
            SetVerticesDirty();
            SetMaterialDirty();
        }

        public virtual void SetLayoutDirty()
        {
            if (!IsActive()) return;
            LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            m_OnDirtyLayoutCallback?.Invoke();
        }

        public virtual void SetVerticesDirty()
        {
            if (!IsActive()) return;
            verticesDirty = true;
            CanvasUpdateRegistry.RegisterCanvasElementForGraphicRebuild(this);
            m_OnDirtyVertsCallback?.Invoke();
        }

        public virtual void SetMaterialDirty()
        {
            if (!IsActive()) return;
            materialDirty = true;
            CanvasUpdateRegistry.RegisterCanvasElementForGraphicRebuild(this);
            m_OnDirtyMaterialCallback?.Invoke();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            if (!gameObject.activeInHierarchy) return;
            // During a layout rebuild the layout is being settled already.
            if (CanvasUpdateRegistry.IsRebuildingLayout()) SetVerticesDirty();
            else
            {
                SetVerticesDirty();
                SetLayoutDirty();
            }
        }

        protected override void OnBeforeTransformParentChanged()
        {
            GraphicRegistry.UnregisterGraphicForCanvas(cachedCanvas, this);
            LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            cachedCanvas = null;
            if (!IsActive()) return;
            FindCanvas();
            GraphicRegistry.RegisterGraphicForCanvas(cachedCanvas, this);
            SetAllDirty();
        }

        /// The element's draw order in its canvas (-1 when it is not drawn).
        public int depth => canvasRenderer.absoluteDepth;

        public RectTransform rectTransform
        {
            get
            {
                if (ReferenceEquals(cachedRect, null)) cachedRect = GetComponent<RectTransform>();
                return cachedRect;
            }
        }

        /// The nearest active and enabled canvas at or above the element.
        public Canvas canvas
        {
            get
            {
                if (cachedCanvas == null) FindCanvas();
                return cachedCanvas;
            }
        }

        void FindCanvas()
        {
            cachedCanvas = null;
            for (var t = transform; t != null; t = t.parent)
            {
                var c = t.GetComponent<Canvas>();
                if (c != null && c.isActiveAndEnabled)
                {
                    cachedCanvas = c;
                    return;
                }
            }
        }

        public CanvasRenderer canvasRenderer
        {
            get
            {
                if (ReferenceEquals(cachedRenderer, null))
                {
                    cachedRenderer = GetComponent<CanvasRenderer>();
                    if (ReferenceEquals(cachedRenderer, null)) cachedRenderer = gameObject.AddComponent<CanvasRenderer>();
                }
                return cachedRenderer;
            }
        }

        public virtual Material defaultMaterial => defaultGraphicMaterial;

        public virtual Material material
        {
            get => m_Material != null ? m_Material : defaultMaterial;
            set
            {
                if (m_Material == value) return;
                m_Material = value;
                SetMaterialDirty();
            }
        }

        /// The material after every IMaterialModifier on the object (masks, stencils) in
        /// component order.
        public virtual Material materialForRendering
        {
            get
            {
                var result = material;
                var list = Scratch<Component>.Take();
                GetComponents(typeof(IMaterialModifier), list);
                foreach (var c in list) result = ((IMaterialModifier)c).GetModifiedMaterial(result);
                Scratch<Component>.Return(list);
                return result;
            }
        }

        public virtual Texture mainTexture => s_WhiteTexture != null ? s_WhiteTexture : (s_WhiteTexture = Texture2D.whiteTexture);

        protected override void OnEnable()
        {
            base.OnEnable();
            FindCanvas();
            GraphicRegistry.RegisterGraphicForCanvas(cachedCanvas, this);
            if (s_WhiteTexture == null) s_WhiteTexture = Texture2D.whiteTexture;
            canvasRenderer.cull = false;
            SetAllDirty();
        }

        protected override void OnDisable()
        {
            GraphicRegistry.DisableGraphicForCanvas(cachedCanvas, this);
            CanvasUpdateRegistry.DisableCanvasElementForRebuild(this);
            if (canvasRenderer != null) canvasRenderer.Clear();
            LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            GraphicRegistry.UnregisterGraphicForCanvas(cachedCanvas, this);
            CanvasUpdateRegistry.UnRegisterCanvasElementForRebuild(this);
            base.OnDestroy();
        }

        protected override void OnCanvasHierarchyChanged()
        {
            var previous = cachedCanvas;
            cachedCanvas = null;
            if (!IsActive())
            {
                GraphicRegistry.UnregisterGraphicForCanvas(previous, this);
                return;
            }
            FindCanvas();
            if (previous == cachedCanvas) return;
            GraphicRegistry.UnregisterGraphicForCanvas(previous, this);
            if (IsActive()) GraphicRegistry.RegisterGraphicForCanvas(cachedCanvas, this);
        }

        /// A culled element skips its rebuilds; uncovered again it rebuilds what changed meanwhile.
        public virtual void OnCullingChanged()
        {
            if (!canvasRenderer.cull && (verticesDirty || materialDirty))
                CanvasUpdateRegistry.RegisterCanvasElementForGraphicRebuild(this);
        }

        public virtual void Rebuild(CanvasUpdate update)
        {
            if (canvasRenderer == null || canvasRenderer.cull) return;
            if (update != CanvasUpdate.PreRender) return;
            if (verticesDirty)
            {
                UpdateGeometry();
                verticesDirty = false;
            }
            if (materialDirty)
            {
                UpdateMaterial();
                materialDirty = false;
            }
        }

        public virtual void LayoutComplete() { }

        public virtual void GraphicUpdateComplete() { }

        protected virtual void UpdateMaterial()
        {
            if (!IsActive()) return;
            canvasRenderer.materialCount = 1;
            canvasRenderer.SetMaterial(materialForRendering, 0);
            canvasRenderer.SetTexture(mainTexture);
        }

        protected virtual void UpdateGeometry()
        {
            if (useLegacyMeshGeneration) GenerateThroughMesh();
            else Generate();
        }

        /// OnPopulateMesh fills the shared helper (nothing for a negative rect), the mesh modifiers
        /// edit it in component order, and the canvas renderer takes a copy.
        void Generate()
        {
            var rt = rectTransform;
            if (rt != null && rt.rect.width >= 0f && rt.rect.height >= 0f) OnPopulateMesh(sharedVertices);
            else sharedVertices.Clear();
            var modifiers = Scratch<Component>.Take();
            GetComponents(typeof(IMeshModifier), modifiers);
            foreach (var c in modifiers) ((IMeshModifier)c).ModifyMesh(sharedVertices);
            Scratch<Component>.Return(modifiers);
            sharedVertices.FillMesh(workerMesh);
            canvasRenderer.SetMesh(workerMesh);
        }

        void GenerateThroughMesh()
        {
            var rt = rectTransform;
#pragma warning disable 618
            if (rt != null && rt.rect.width >= 0f && rt.rect.height >= 0f) OnPopulateMesh(workerMesh);
            else workerMesh.Clear();
            var modifiers = Scratch<Component>.Take();
            GetComponents(typeof(IMeshModifier), modifiers);
            foreach (var c in modifiers) ((IMeshModifier)c).ModifyMesh(workerMesh);
            Scratch<Component>.Return(modifiers);
#pragma warning restore 618
            canvasRenderer.SetMesh(workerMesh);
        }

        [Obsolete("Use OnPopulateMesh(VertexHelper vh) instead.", false)]
        protected virtual void OnPopulateMesh(Mesh m)
        {
            OnPopulateMesh(sharedVertices);
            sharedVertices.FillMesh(m);
        }

        /// One quad over the pixel-adjusted rect in the graphic's colour: corners bottom left,
        /// top left, top right, bottom right.
        protected virtual void OnPopulateMesh(VertexHelper vh)
        {
            var r = GetPixelAdjustedRect();
            Color32 c = color;
            vh.Clear();
            vh.AddVert(new Vector3(r.xMin, r.yMin), c, new Vector2(0f, 0f));
            vh.AddVert(new Vector3(r.xMin, r.yMax), c, new Vector2(0f, 1f));
            vh.AddVert(new Vector3(r.xMax, r.yMax), c, new Vector2(1f, 1f));
            vh.AddVert(new Vector3(r.xMax, r.yMin), c, new Vector2(1f, 0f));
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        protected override void OnDidApplyAnimationProperties() => SetAllDirty();

        /// Sizes the rect to the graphic's natural size; the base graphic has none.
        public virtual void SetNativeSize() { }

        /// Every raycast filter from the element up must accept the point. CanvasGroups above one
        /// that ignores its parents are not asked; the walk ends at a canvas that overrides
        /// sorting (it is raycast as a unit of its own).
        public virtual bool Raycast(Vector2 sp, Camera eventCamera)
        {
            if (!isActiveAndEnabled) return false;
            var components = Scratch<Component>.Take();
            try
            {
                return PassesFilters(components, sp, eventCamera);
            }
            finally { Scratch<Component>.Return(components); }
        }

        bool PassesFilters(List<Component> components, Vector2 sp, Camera eventCamera)
        {
            bool groupsIgnored = false;
            for (var t = transform; t != null;)
            {
                t.GetComponents(components);
                bool last = false;
                foreach (var c in components)
                {
                    if (c is Canvas cv && cv.overrideSorting) last = true;
                    if (c is not ICanvasRaycastFilter filter) continue;
                    if (c is CanvasGroup group)
                    {
                        if (!group.enabled || groupsIgnored) continue;
                        if (group.ignoreParentGroups) groupsIgnored = true;
                    }
                    else if (c is Behaviour b && !b.enabled) continue;
                    if (!filter.IsRaycastLocationValid(sp, eventCamera)) return false;
                }
                t = last ? null : t.parent;
            }
            return true;
        }

        public Vector2 PixelAdjustPoint(Vector2 point)
        {
            var c = canvas;
            if (c == null || c.renderMode == RenderMode.WorldSpace || c.scaleFactor == 0f || !c.pixelPerfect) return point;
            return RectTransformUtility.PixelAdjustPoint(point, transform, c);
        }

        /// The rect, snapped to whole pixels on a pixel-perfect screen-space canvas.
        public Rect GetPixelAdjustedRect()
        {
            var c = canvas;
            if (c == null || c.renderMode == RenderMode.WorldSpace || c.scaleFactor == 0f || !c.pixelPerfect) return rectTransform.rect;
            return RectTransformUtility.PixelAdjustRect(rectTransform, c);
        }

        public virtual void CrossFadeColor(Color targetColor, float duration, bool ignoreTimeScale, bool useAlpha)
            => CrossFadeColor(targetColor, duration, ignoreTimeScale, useAlpha, true);

        /// Tweens the canvas renderer's colour (not color) to the target, in RGB, alpha or both;
        /// a new fade replaces a running one.
        public virtual void CrossFadeColor(Color targetColor, float duration, bool ignoreTimeScale, bool useAlpha, bool useRGB)
        {
            if (canvasRenderer == null || (!useRGB && !useAlpha)) return;
            var from = canvasRenderer.GetColor();
            if (from.Equals(targetColor))
            {
                StopFade();
                return;
            }
            StartFade(from, targetColor, duration, ignoreTimeScale, useRGB, useAlpha);
        }

        public virtual void CrossFadeAlpha(float alpha, float duration, bool ignoreTimeScale)
            => CrossFadeColor(new Color(1f, 1f, 1f, alpha), duration, ignoreTimeScale, true, false);

        void StopFade()
        {
            if (fade == null) return;
            StopCoroutine(fade);
            fade = null;
        }

        /// Faded colour at t in [0, 1]: the channels not faded keep their starting values.
        static Color FadeAt(Color from, Color to, float t, bool rgb, bool alpha)
        {
            var c = Color.Lerp(from, to, t);
            if (!rgb) { c.r = from.r; c.g = from.g; c.b = from.b; }
            if (!alpha) c.a = from.a;
            return c;
        }

        void StartFade(Color from, Color to, float duration, bool unscaled, bool rgb, bool alpha)
        {
            StopFade();
            // An inactive object runs no coroutines: the fade ends at once.
            if (!gameObject.activeInHierarchy)
            {
                canvasRenderer.SetColor(FadeAt(from, to, 1f, rgb, alpha));
                return;
            }
            fade = Fade(from, to, duration, unscaled, rgb, alpha);
            StartCoroutine(fade);
        }

        /// Each step (the first at once) advances by the frame's delta time.
        IEnumerator Fade(Color from, Color to, float duration, bool unscaled, bool rgb, bool alpha)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += unscaled ? Time.unscaledDeltaTime : Time.deltaTime;
                canvasRenderer.SetColor(FadeAt(from, to, Mathf.Clamp01(elapsed / duration), rgb, alpha));
                yield return null;
            }
            canvasRenderer.SetColor(FadeAt(from, to, 1f, rgb, alpha));
            fade = null;
        }

        public void RegisterDirtyLayoutCallback(UnityAction action) => m_OnDirtyLayoutCallback += action;
        public void UnregisterDirtyLayoutCallback(UnityAction action) => m_OnDirtyLayoutCallback -= action;
        public void RegisterDirtyVerticesCallback(UnityAction action) => m_OnDirtyVertsCallback += action;
        public void UnregisterDirtyVerticesCallback(UnityAction action) => m_OnDirtyVertsCallback -= action;
        public void RegisterDirtyMaterialCallback(UnityAction action) => m_OnDirtyMaterialCallback += action;
        public void UnregisterDirtyMaterialCallback(UnityAction action) => m_OnDirtyMaterialCallback -= action;
    }
}
