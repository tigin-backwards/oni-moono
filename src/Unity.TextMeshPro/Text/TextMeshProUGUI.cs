using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TMPro
{
    // A text drawn by the canvas: its mesh goes to its CanvasRenderer, the characters of other
    // materials (fallback fonts, sprites) to TMP_SubMeshUI children. It lays out in the canvas
    // graphic rebuild when it changed, through GenerateTextMesh.
    public partial class TextMeshProUGUI
    {
        bool m_VerticesDirty, m_MaterialDirty;
        TMP_SubMeshUI[] m_SubMeshes = new TMP_SubMeshUI[0];
        Vector2 m_LastRectSize;
        Canvas m_CanvasCache;
        CanvasRenderer m_canvasRenderer;

        // Declared here as on the shipped type (mods compiled against it call this getter): the
        // GameObject's CanvasRenderer, the one Graphic.canvasRenderer returns.
        public new CanvasRenderer canvasRenderer
        {
            get
            {
                if (ReferenceEquals(m_canvasRenderer, null)) m_canvasRenderer = base.canvasRenderer;
                return m_canvasRenderer;
            }
        }

        // ----- Lifecycle -----

        protected override void Awake()
        {
            base.Awake();
            // A rect of the default size (100 x 100) becomes 200 x 50 (measured: the shipped
            // package's texts added at run time are).
            if (rectTransform.sizeDelta == new Vector2(100f, 100f)) rectTransform.sizeDelta = new Vector2(200f, 50f);
            if (MeshCache == null) MeshCache = new Mesh { name = "TextMeshPro UI Mesh" };
            // The text info starts new (the data's serialized one is not used: a text that never
            // laid out reports one material, as the shipped package's does).
            m_textInfo = new TMP_TextInfo(this);
            m_Woken = true;
            if (m_fontAsset == null) m_fontAsset = TMP_Settings.defaultFontAsset;
            if (m_sharedMaterial == null && m_fontAsset != null) m_sharedMaterial = m_fontAsset.material;
            // A saved material that does not draw the font's atlas is replaced by the font's
            // (measured on the shipped package: the game's data has texts whose font was changed
            // without their material, drawn with the font's). Only at load: a material set at
            // run time is kept, whatever its atlas.
            if (m_fontAsset != null && m_sharedMaterial != null && m_fontAsset.atlasTexture != null
                && m_sharedMaterial.HasProperty(ShaderUtilities.ID_MainTex)
                && m_sharedMaterial.GetTexture(ShaderUtilities.ID_MainTex) != m_fontAsset.atlasTexture)
                m_sharedMaterial = m_fontAsset.material;
            // The colour is m_fontColor; data may hold an older m_fontColor32.
            m_fontColor32 = m_fontColor;
            ConvertAlignment();
            // Sub-meshes saved with the object are adopted, in the order of their material slots.
            // Those made at run time (DontSave) are not: a clone of a text leaves its copy of the
            // original's sub-mesh empty and makes its own (measured on the shipped package).
            var existing = GetComponentsInChildren<TMP_SubMeshUI>(true);
            var own = new List<TMP_SubMeshUI>();
            foreach (var s in existing)
                if (s.transform.parent == transform && (s.gameObject.hideFlags & HideFlags.DontSaveInBuild) == 0) own.Add(s);
            own.Sort((a, b) => a.MaterialReferenceIndex.CompareTo(b.MaterialReferenceIndex));
            m_SubMeshes = own.ToArray();
            m_ParseRequired = true;
            m_havePropertiesChanged = true;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (MeshCache == null) MeshCache = new Mesh { name = "TextMeshPro UI Mesh" };
            m_CanvasCache = null;
            m_havePropertiesChanged = true;
            m_ParseRequired = true;
            // The sub-meshes disabled with the text are enabled with it (measured).
            foreach (var s in m_SubMeshes)
            {
                if (s == null || !s.DisabledWithText) continue;
                s.DisabledWithText = false;
                s.enabled = true;
            }
            SetAllDirty();
            Canvas.willRenderCanvases -= RescaleIfScaleChanged;
            Canvas.willRenderCanvases += RescaleIfScaleChanged;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            Canvas.willRenderCanvases -= RescaleIfScaleChanged;
            // Its sub-meshes are disabled with it, also when its object is deactivated (measured).
            foreach (var s in m_SubMeshes)
            {
                if (s == null || !s.enabled) continue;
                s.enabled = false;
                s.DisabledWithText = true;
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            Canvas.willRenderCanvases -= RescaleIfScaleChanged;
            if (MeshCache != null) Destroy(MeshCache);
            MeshCache = null;
        }

        protected override void OnRectTransformDimensionsChange()
        {
            FollowPivot();
            if (!gameObject.activeInHierarchy) return;
            var size = rectTransform.rect.size;
            if (size != m_LastRectSize)
            {
                m_LastRectSize = size;
                InvalidateLayoutCache();
                m_havePropertiesChanged = true;
            }
            base.OnRectTransformDimensionsChange();
        }

        // The sub-meshes' pivot is the text's, so that their local origin stays the text's
        // (measured: it follows a change of the text's pivot at once).
        void FollowPivot()
        {
            var pivot = rectTransform.pivot;
            foreach (var s in m_SubMeshes)
                if (s != null && s.rectTransform.pivot != pivot) s.rectTransform.pivot = pivot;
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            m_CanvasCache = null;
            m_havePropertiesChanged = true;
            SetVerticesDirty();
        }

        protected override void OnCanvasHierarchyChanged()
        {
            base.OnCanvasHierarchyChanged();
            m_CanvasCache = null;
        }

        // Klei's addition: marks the text changed and, when it is active, lays it out and
        // regenerates its mesh at once, so that its rendered size, text info and bounds are
        // current when it returns (measured: HoverTextDrawer reads renderedWidth right after
        // it). An inactive text stays changed until it is next laid out.
        public void KForceUpdateDirty()
        {
            m_havePropertiesChanged = true;
            m_ParseRequired = true;
            InvalidateLayoutCache();
            SetVerticesDirty();
            SetLayoutDirty();
            ForceMeshUpdate();
        }

        // ----- Canvas rebuild -----

        public override void SetVerticesDirty()
        {
            if (this == null || !IsActive()) return;
            m_VerticesDirty = true;
            base.SetVerticesDirty();
        }

        public override void SetLayoutDirty()
        {
            if (this == null || !IsActive()) return;
            InvalidateLayoutCache();
            m_CullWithNextMesh = true;
            base.SetLayoutDirty();
        }

        public override void SetMaterialDirty()
        {
            if (this == null || !IsActive()) return;
            m_MaterialDirty = true;
            base.SetMaterialDirty();
        }

        public override void Rebuild(CanvasUpdate update)
        {
            if (this == null) return;
            // A text outside any canvas waits, still dirty, until it is parented to one
            // (measured: an input field's label built outside a canvas is laid out in the first
            // rebuild after its parenting).
            if (canvas == null) return;
            if (update == CanvasUpdate.PreRender)
            {
                if (m_havePropertiesChanged || m_VerticesDirty)
                {
                    m_VerticesDirty = false;
                    bool cullNow = m_CullWithNextMesh;
                    m_CullWithNextMesh = false;
                    var before = MeshBounds;
                    GenerateTextMesh();
                    CullAfterMesh(before, cullNow);
                }
                if (m_MaterialDirty)
                {
                    m_MaterialDirty = false;
                    UpdateMaterial();
                }
            }
        }

        // The mesh is generated by GenerateTextMesh; the Graphic's own vertex generation is
        // not used.
        protected override void UpdateGeometry() { }

        protected override void UpdateMaterial()
        {
            if (canvasRenderer == null) return;
            if (m_sharedMaterial == null && m_fontAsset != null) m_sharedMaterial = m_fontAsset.material;
            canvasRenderer.materialCount = 1;
            canvasRenderer.SetMaterial(materialForRendering, 0);
            foreach (var s in m_SubMeshes)
                if (s != null && s.isActiveAndEnabled) s.SetMaterialDirty();
        }

        // Lays the text out and uploads its geometry (LocText overrides this).
        protected virtual void GenerateTextMesh()
        {
            if (m_fontAsset == null || (!m_IgnoreActiveStateNow && !IsActive()))
            {
                m_havePropertiesChanged = false;
                return;
            }
            if (m_sharedMaterial == null) m_sharedMaterial = m_fontAsset.material;
            LayoutForRendering();
            if (textInfo.characterCount == 0)
            {
                // The buffers exist (one spare quad); the renderers draw nothing.
                EnsureBuffers();
                // A material the text uses has its entry even when nothing is drawn (an
                // ellipsis's, when the cut leaves nothing).
                if (textInfo.meshInfo.Length < Parsed.Materials.Count) System.Array.Resize(ref textInfo.meshInfo, Parsed.Materials.Count);
                m_GeneratedScale = CurrentScale();
                ClearRenderedGeometry();
                for (int i = 0; i < textInfo.meshInfo.Length; i++) textInfo.meshInfo[i].vertexCount = 0;
                UploadEmptySubMeshes();
                m_havePropertiesChanged = false;
                TMPro_EventManager.ON_TEXT_CHANGED(this);
                return;
            }
            m_GeneratedScale = CurrentScale();
            BuildGeometry();
            InvokePreRender();
            UploadGeometry(Mathf.Max(1, Parsed.Materials.Count));
            m_havePropertiesChanged = false;
            TMPro_EventManager.ON_TEXT_CHANGED(this);
        }

        bool m_IgnoreActiveStateNow;

        // The text's lossy scale (y) its geometry has the SDF scale for (-1: none generated).
        float m_GeneratedScale = -1f;

        // Before the canvases render: when the text's lossy scale (y) changed by more than a
        // factor of 1.25 (either way) since its SDF scale was last set, the SDF scale of its
        // vertices (uv0.w) is multiplied by the change and uploaded, without a new layout;
        // smaller changes are left alone. From or to a scale of 0 the text is generated again.
        // A text marked isTextObjectScaleStatic is not checked. (All measured on the shipped
        // package.)
        void RescaleIfScaleChanged()
        {
            if (this == null || !isActiveAndEnabled || m_IsTextObjectScaleStatic || m_GeneratedScale < 0f || m_textInfo == null) return;
            float scale = CurrentScale();
            if (scale == m_GeneratedScale) return;
            if (scale == 0f || m_GeneratedScale == 0f)
            {
                GenerateTextMesh();
                return;
            }
            float ratio = scale / m_GeneratedScale;
            if (ratio >= 0.8f && ratio <= 1.25f) return;
            m_GeneratedScale = scale;
            var info = m_textInfo;
            int count = Mathf.Min(Mathf.Max(1, Parsed.Materials.Count), info.meshInfo.Length);
            for (int m = 0; m < count; m++)
            {
                var uvs = info.meshInfo[m].uvs0;
                if (uvs == null) continue;
                for (int i = 0; i < uvs.Length; i++) uvs[i].w *= ratio;
            }
            UpdateVertexData(TMP_VertexDataUpdateFlags.Uv0);
        }

        private protected override void RegenerateNow()
        {
            // The mesh made now is current: the next canvas rebuild does not lay the text out
            // again (measured: the shipped package's doesn't, after ForceMeshUpdate).
            m_VerticesDirty = false;
            m_IgnoreActiveStateNow = true;
            var before = MeshBounds;
            try { GenerateTextMesh(); }
            finally { m_IgnoreActiveStateNow = false; }
            CullAfterMesh(before, false);
        }

        // ----- Culling under a RectMask2D -----
        //
        // A text culls with the bounds of the mesh its canvas renderer has (every vertex, the
        // unused ones at the local origin included), not with its rect. The RectMask2D culls it
        // when the clip changes, or when the text moves or changes size; the text culls itself
        // again when its mesh changes: in the same canvas update when the mesh followed a change
        // of its layout (text, font size, a new text), otherwise in the next one. A mesh made
        // for an empty text keeps the cull it had. (docs/cleanroom/ugui.md, "RectMask2D culling".)

        // The clip its RectMask2D last culled it with, while it is under one.
        Rect m_CullClip;
        bool m_CullClipValid, m_HasCullClip;
        // Its layout changed since its last mesh.
        bool m_CullWithNextMesh;
        bool m_CullPending;
        static readonly List<TextMeshProUGUI> s_CullPending = new();
        static bool s_CullHooked;

        Bounds MeshBounds => MeshCache != null ? MeshCache.bounds : default;

        public override void SetClipRect(Rect clipRect, bool validRect)
        {
            base.SetClipRect(clipRect, validRect);
            // An invalid rect: the mask let go of the text, or (culling it next) has no area.
            if (!validRect) m_HasCullClip = false;
        }

        public override void Cull(Rect clipRect, bool validRect)
        {
            m_CullClip = clipRect;
            m_CullClipValid = validRect;
            m_HasCullClip = true;
            bool cull = !validRect || !clipRect.Overlaps(MeshRectInRootCanvas(), true);
            if (canvasRenderer.cull == cull) return;
            canvasRenderer.cull = cull;
            onCullStateChanged.Invoke(cull);
            // Within the graphic rebuild the text has just made its mesh: nothing is left to queue.
            if (!CanvasUpdateRegistry.IsRebuildingGraphics()) OnCullingChanged();
        }

        // The mesh's bounds in the root canvas's space: the box of its corners mapped there.
        Rect MeshRectInRootCanvas()
        {
            var b = MeshBounds;
            var m = transform.localToWorldMatrix;
            var c = canvas;
            if (c != null) m = c.rootCanvas.transform.worldToLocalMatrix * m;
            var p0 = m.MultiplyPoint(new Vector3(b.min.x, b.min.y, b.center.z));
            var p1 = m.MultiplyPoint(new Vector3(b.min.x, b.max.y, b.center.z));
            var p2 = m.MultiplyPoint(new Vector3(b.max.x, b.max.y, b.center.z));
            var p3 = m.MultiplyPoint(new Vector3(b.max.x, b.min.y, b.center.z));
            float xMin = Mathf.Min(Mathf.Min(p0.x, p1.x), Mathf.Min(p2.x, p3.x));
            float xMax = Mathf.Max(Mathf.Max(p0.x, p1.x), Mathf.Max(p2.x, p3.x));
            float yMin = Mathf.Min(Mathf.Min(p0.y, p1.y), Mathf.Min(p2.y, p3.y));
            float yMax = Mathf.Max(Mathf.Max(p0.y, p1.y), Mathf.Max(p2.y, p3.y));
            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        void CullAfterMesh(Bounds before, bool now)
        {
            if (!m_HasCullClip || m_textInfo == null || m_textInfo.characterCount == 0) return;
            if (MeshBounds == before) return;
            if (now)
            {
                Cull(m_CullClip, m_CullClipValid);
                return;
            }
            if (m_CullPending) return;
            m_CullPending = true;
            s_CullPending.Add(this);
            if (s_CullHooked) return;
            s_CullHooked = true;
            Canvas.preWillRenderCanvases += CullPendingTexts;
        }

        // Before the next canvas update's layout and clipping, as the mask would in its clipping.
        static void CullPendingTexts()
        {
            if (s_CullPending.Count == 0) return;
            var pending = s_CullPending.ToArray();
            s_CullPending.Clear();
            foreach (var t in pending)
            {
                if (t == null) continue;
                t.m_CullPending = false;
                if (t.isActiveAndEnabled && t.m_HasCullClip) t.Cull(t.m_CullClip, t.m_CullClipValid);
            }
        }

        // ----- Materials -----

        public override Material material
        {
            get => m_sharedMaterial;
            set
            {
                if (m_sharedMaterial == value) return;
                fontSharedMaterial = value;
            }
        }

        public override Material materialForRendering
        {
            get
            {
                var current = material;
                var modifiers = new List<IMaterialModifier>();
                GetComponents(modifiers);
                foreach (var m in modifiers)
                    if (m is Behaviour b && !b.isActiveAndEnabled) continue;
                    else current = m.GetModifiedMaterial(current);
                return current;
            }
        }

        public override Texture mainTexture => m_sharedMaterial != null ? m_sharedMaterial.GetTexture(ShaderUtilities.ID_MainTex) : null;

        public override Material defaultMaterial => m_fontAsset != null ? m_fontAsset.material : null;

        // ----- Geometry -----

        private protected override Rect LayoutRect => rectTransform.rect;

        float CurrentScale() => Mathf.Abs(rectTransform.lossyScale.y);

        // The SDF scale of the vertices includes the text's lossy scale: in a screen space
        // overlay canvas relative to the canvas's scale factor (1 for a text scaled only by its
        // canvas), in camera and world space canvases as it is (measured on the shipped package).
        private protected override float SdfScaleFactor()
        {
            var c = CanvasCache;
            float scale = CurrentScale();
            if (c == null || c.scaleFactor == 0f || c.renderMode != RenderMode.ScreenSpaceOverlay) return scale;
            return scale / c.scaleFactor;
        }

        Canvas CanvasCache
        {
            get
            {
                if (m_CanvasCache == null) m_CanvasCache = canvas;
                return m_CanvasCache;
            }
        }

        // Materials are numbered by first use in each layout; sub-mesh k draws material k. A
        // sub-mesh that already has a material the layout uses moves to that material's number
        // (trading places with the one there), so it keeps its material. Sub-meshes a layout
        // does not use stay, drawing nothing.
        void AssignSubMeshes(int materialCount)
        {
            for (int k = 1; k < materialCount; k++)
            {
                var material = Parsed.Materials[k].Material;
                if (k - 1 < m_SubMeshes.Length && m_SubMeshes[k - 1] != null && m_SubMeshes[k - 1].sharedMaterial == material) continue;
                for (int j = k; j < m_SubMeshes.Length; j++)
                {
                    if (m_SubMeshes[j] == null || m_SubMeshes[j].sharedMaterial != material) continue;
                    if (m_SubMeshes.Length < k) System.Array.Resize(ref m_SubMeshes, k);
                    (m_SubMeshes[k - 1], m_SubMeshes[j]) = (m_SubMeshes[j], m_SubMeshes[k - 1]);
                    break;
                }
            }
        }

        private protected override Mesh MeshForMaterial(int index)
        {
            if (index == 0) return MeshCache ??= new Mesh { name = "TextMeshPro UI Mesh" };
            if (index == 1)
            {
                AssignSubMeshes(Parsed.Materials.Count);
                MakeSubMeshesInDrawOrder(Parsed.Materials.Count);
            }
            var sub = SubMesh(index);
            return sub.mesh;
        }

        // Sub-meshes are made in the order of the characters drawn with them, those of
        // materials nothing is drawn with (an ellipsis's) after them (measured on the shipped
        // package: a fallback's sub-mesh comes before the ellipsis's typeface's).
        void MakeSubMeshesInDrawOrder(int materialCount)
        {
            var info = textInfo;
            var chars = info.characterInfo;
            for (int c = 0; c < info.characterCount; c++)
            {
                int m = chars[c].materialReferenceIndex;
                if (m > 0 && m < materialCount && chars[c].isVisible) SubMesh(m);
            }
        }

        TMP_SubMeshUI SubMesh(int index)
        {
            if (m_SubMeshes.Length < index) System.Array.Resize(ref m_SubMeshes, index);
            var sub = m_SubMeshes[index - 1];
            if (sub == null)
            {
                sub = TMP_SubMeshUI.AddSubTextObject(this, index);
                m_SubMeshes[index - 1] = sub;
            }
            return sub;
        }

        private protected override void UploadGeometry(int materialCount)
        {
            var info = textInfo;
            var mesh = MeshCache;
            ref var main = ref info.meshInfo[0];
            UploadMesh(ref main, mesh, TMP_VertexDataUpdateFlags.All);
            canvasRenderer.SetMesh(mesh);
            AssignSubMeshes(materialCount);
            MakeSubMeshesInDrawOrder(materialCount);
            for (int i = 1; i < materialCount; i++) UploadSubMesh(i);
            // Sub-meshes this layout does not use draw nothing.
            for (int i = materialCount; i <= m_SubMeshes.Length; i++)
            {
                var sub = m_SubMeshes[i - 1];
                if (sub == null) continue;
                if (i < info.meshInfo.Length) info.meshInfo[i].vertexCount = 0;
                sub.ClearGeometry();
            }
        }

        void UploadSubMesh(int i)
        {
            var sub = SubMesh(i);
            var slot = Parsed.Materials[i];
            sub.Configure(slot.Material, slot.Asset, i);
            if (!sub.gameObject.activeSelf) sub.gameObject.SetActive(true);
            if (!sub.enabled) sub.enabled = true;
            sub.DisabledWithText = false;
            // Every generation that fills the sub-mesh gives it the text's raycastTarget,
            // through the property: a change to true lists it as a raycast target (measured).
            sub.raycastTarget = raycastTarget;
            if (sub.rectTransform.pivot != rectTransform.pivot) sub.rectTransform.pivot = rectTransform.pivot;
            ref var mi = ref textInfo.meshInfo[i];
            UploadMesh(ref mi, sub.mesh, TMP_VertexDataUpdateFlags.All);
            sub.canvasRenderer.SetMesh(sub.mesh);
        }

        // A text with nothing laid out still has the sub-meshes of the materials it has from
        // the start (an ellipsis's), each with an empty quad (measured on the shipped package:
        // "The ellipsis glyph in Bold and Italic texts").
        void UploadEmptySubMeshes()
        {
            int count = Parsed.Materials.Count;
            if (count <= 1) return;
            AssignSubMeshes(count);
            for (int m = 1; m < count; m++)
            {
                ref var mi = ref textInfo.meshInfo[m];
                var mesh = SubMesh(m).mesh;
                if (mi.vertices == null || mi.mesh != mesh || mi.QuadCapacity == 0) mi = new TMP_MeshInfo(mesh, 1);
                mi.material = Parsed.Materials[m].Material;
                mi.vertexCount = 0;
                mi.ClearUnusedVertices(0);
                UploadSubMesh(m);
            }
        }

        static void UploadMesh(ref TMP_MeshInfo mi, Mesh mesh, TMP_VertexDataUpdateFlags flags)
        {
            if (mesh == null || mi.vertices == null) return;
            if (mesh.vertexCount != mi.vertices.Length)
            {
                mesh.Clear();
                mesh.vertices = mi.vertices;
                mesh.SetUVs(0, mi.uvs0);
                mesh.uv2 = mi.uvs2;
                mesh.colors32 = mi.colors32;
                mesh.normals = mi.normals;
                mesh.tangents = mi.tangents;
                mesh.triangles = mi.triangles;
            }
            else
            {
                if ((flags & TMP_VertexDataUpdateFlags.Vertices) != 0) mesh.vertices = mi.vertices;
                if ((flags & TMP_VertexDataUpdateFlags.Uv0) != 0) mesh.SetUVs(0, mi.uvs0);
                if ((flags & TMP_VertexDataUpdateFlags.Uv2) != 0) mesh.uv2 = mi.uvs2;
                if ((flags & TMP_VertexDataUpdateFlags.Colors32) != 0) mesh.colors32 = mi.colors32;
            }
            mesh.RecalculateBounds();
        }

        private protected override void ClearRenderedGeometry()
        {
            var mesh = MeshCache;
            if (mesh != null)
            {
                mesh.Clear();
                if (canvasRenderer != null) canvasRenderer.SetMesh(mesh);
            }
            foreach (var s in m_SubMeshes)
                if (s != null) s.ClearGeometry();
        }

        public override void UpdateGeometry(Mesh mesh, int index)
        {
            mesh.RecalculateBounds();
            if (index == 0) canvasRenderer.SetMesh(mesh);
            else if (index - 1 < m_SubMeshes.Length && m_SubMeshes[index - 1] != null) m_SubMeshes[index - 1].canvasRenderer.SetMesh(mesh);
        }

        public override void UpdateVertexData(TMP_VertexDataUpdateFlags flags)
        {
            var info = textInfo;
            int count = Mathf.Max(1, Parsed.Materials.Count);
            for (int i = 0; i < count && i < info.meshInfo.Length; i++)
            {
                ref var mi = ref info.meshInfo[i];
                var mesh = i == 0 ? MeshCache : (i - 1 < m_SubMeshes.Length && m_SubMeshes[i - 1] != null ? m_SubMeshes[i - 1].mesh : null);
                if (mesh == null) continue;
                UploadMesh(ref mi, mesh, flags);
                if (i == 0) canvasRenderer.SetMesh(mesh);
                else m_SubMeshes[i - 1].canvasRenderer.SetMesh(mesh);
            }
        }

        public override void UpdateVertexData() => UpdateVertexData(TMP_VertexDataUpdateFlags.All);

        public override void SetVertices(Vector3[] vertices)
        {
            var mesh = MeshCache;
            if (mesh == null) return;
            mesh.vertices = vertices;
            canvasRenderer.SetMesh(mesh);
        }

        public override Mesh mesh => MeshCache;

        void InvokePreRender() => RaisePreRender();

        // ----- Layout element -----

        public void CalculateLayoutInputHorizontal() { }

        public void CalculateLayoutInputVertical() { }

        public override float preferredWidth => base.preferredWidth;

        public override float preferredHeight => base.preferredHeight;

        public override bool autoSizeTextContainer
        {
            get => m_AutoSizeTextContainer;
            set
            {
                if (m_AutoSizeTextContainer == value) return;
                m_AutoSizeTextContainer = value;
                if (value) LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            }
        }

        bool m_AutoSizeTextContainer;

        public override Vector4 margin
        {
            get => m_margin;
            set => base.margin = value;
        }

        // ----- Masking -----

        public override void RecalculateClipping()
        {
            base.RecalculateClipping();
            foreach (var s in m_SubMeshes)
                if (s != null && s.isActiveAndEnabled) s.RecalculateClipping();
        }

        protected override void OnDidApplyAnimationProperties()
        {
            m_havePropertiesChanged = true;
            m_ParseRequired = true;
            SetVerticesDirty();
            SetLayoutDirty();
        }
    }
}
