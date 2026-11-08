using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;
using UnityEngine.Rendering;

namespace UnityEngine
{
    public partial struct SortingLayer
    {
        private int m_Id;

        public int id => m_Id;
        public string name => IDToName(m_Id);
        public int value => GetLayerValueFromID(m_Id);

        public static SortingLayer[] layers => SortingLayers.Ids.Select(i => new SortingLayer { m_Id = i }).ToArray();

        /// Position in the project's layer order relative to the Default layer (unknown ids sort
        /// as Default).
        public static int GetLayerValueFromID(int id) => SortingLayers.ValueOf(id);
        public static int GetLayerValueFromName(string name) => SortingLayers.ValueOf(SortingLayers.IdOf(name));
        public static int NameToID(string name) => SortingLayers.IdOf(name);
        public static string IDToName(int id) => SortingLayers.NameOf(id);
        public static bool IsValid(int id) => SortingLayers.Contains(id);
    }

    public partial class Renderer
    {
        internal RendererNative RendererData => (RendererNative)NativeOrThrow;

        public bool enabled
        {
            get => RendererData.Enabled;
            set => RendererData.Enabled = value;
        }

        /// Whether any camera rendered it in the last frame.
        public bool isVisible => RendererData.IsVisible;

        public ShadowCastingMode shadowCastingMode
        {
            get => RendererData.ShadowCasting;
            set => RendererData.ShadowCasting = value;
        }

        public bool receiveShadows
        {
            get => RendererData.ReceiveShadows;
            set => RendererData.ReceiveShadows = value;
        }

        public LightProbeUsage lightProbeUsage
        {
            get => RendererData.LightProbeUsage;
            set => RendererData.LightProbeUsage = value;
        }

        public ReflectionProbeUsage reflectionProbeUsage
        {
            get => RendererData.ReflectionProbeUsage;
            set => RendererData.ReflectionProbeUsage = value;
        }

        public uint renderingLayerMask
        {
            get => RendererData.RenderingLayerMask;
            set => RendererData.RenderingLayerMask = value;
        }

        public int rendererPriority
        {
            get => RendererData.RendererPriority;
            set => RendererData.RendererPriority = value;
        }

        /// Kept for the scripts that set it: this engine does no occlusion culling.
        public bool allowOcclusionWhenDynamic
        {
            get => RendererData.AllowOcclusionWhenDynamic;
            set => RendererData.AllowOcclusionWhenDynamic = value;
        }

        /// Kept for the scripts that set it: this engine renders no motion vectors.
        public MotionVectorGenerationMode motionVectorGenerationMode
        {
            get => RendererData.MotionVectors;
            set => RendererData.MotionVectors = value;
        }

        public int sortingLayerID
        {
            get => RendererData.SortingLayerId;
            set => RendererData.SortingLayerId = value;
        }

        public string sortingLayerName
        {
            get => SortingLayers.NameOf(RendererData.SortingLayerId);
            set => RendererData.SortingLayerId = SortingLayers.IdOf(value);
        }

        /// Stored as a 16-bit value, as in Unity.
        public int sortingOrder
        {
            get => RendererData.SortingOrder;
            set => RendererData.SortingOrder = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }

        /// Index of the enclosing active SortingGroup, or invalidSortingGroupID.
        internal int sortingGroupID => SortingGroups.GroupOf(RendererData) is { } g ? g.Index : SortingGroup.invalidSortingGroupID;

        internal int sortingGroupOrder => SortingGroups.GroupOf(RendererData) is { } g ? g.OrderOf(RendererData) : 0;

        public Matrix4x4 localToWorldMatrix => RendererData.GameObject.Transform.LocalToWorld;
        public Matrix4x4 worldToLocalMatrix => RendererData.GameObject.Transform.WorldToLocal;

        /// World-space bounds; an assigned value overrides them until ResetBounds.
        public Bounds bounds
        {
            get => RendererData.WorldBounds;
            set => RendererData.BoundsOverride = value;
        }

        public Bounds localBounds
        {
            get => RendererData.LocalBounds;
            set => RendererData.LocalBoundsOverride = value;
        }

        public void ResetBounds() => RendererData.BoundsOverride = null;
        public void ResetLocalBounds() => RendererData.LocalBoundsOverride = null;

        public Material sharedMaterial
        {
            get
            {
                var m = RendererData.Materials;
                return m.Length > 0 ? m[0] : null;
            }
            set
            {
                var r = RendererData;
                if (r.Materials.Length == 0) r.SetMaterials([value]);
                else
                {
                    var copy = (Material[])r.Materials.Clone();
                    copy[0] = value;
                    r.SetMaterials(copy);
                }
            }
        }

        public Material[] sharedMaterials
        {
            get => (Material[])RendererData.Materials.Clone();
            set => RendererData.SetMaterials(value ?? Array.Empty<Material>());
        }

        public void GetSharedMaterials(List<Material> m)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            m.Clear();
            m.AddRange(RendererData.Materials);
        }

        public void SetSharedMaterials(List<Material> materials)
        {
            if (materials == null) throw new ArgumentNullException(nameof(materials));
            RendererData.SetMaterials(materials.ToArray());
        }

        /// The first material, instantiated for this renderer on first access ("(Instance)").
        public Material material
        {
            get
            {
                var r = RendererData;
                if (r.Materials.Length == 0) return null;
                return r.InstanceAt(0);
            }
            set => sharedMaterial = value;
        }

        /// All materials, each instantiated for this renderer on first access.
        public Material[] materials
        {
            get
            {
                var r = RendererData;
                var result = new Material[r.Materials.Length];
                for (int i = 0; i < result.Length; i++) result[i] = r.InstanceAt(i);
                return result;
            }
            set => sharedMaterials = value;
        }

        public void GetMaterials(List<Material> m)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            m.Clear();
            m.AddRange(materials);
        }

        public void SetMaterials(List<Material> materials) => SetSharedMaterials(materials);

        internal override void EngineOnEnable() => FrameRenderer.UpdateCandidate(RendererData);

        internal override void EngineOnDisable() => FrameRenderer.UpdateCandidate(RendererData);

        internal override void EngineOnDestroy() => FrameRenderer.Unregister(RendererData);

        public bool HasPropertyBlock() => RendererData.PropertyBlock is { IsEmpty: false };

        /// Copies the block's values (null clears them).
        public void SetPropertyBlock(MaterialPropertyBlock properties)
        {
            var r = RendererData;
            if (properties == null) r.PropertyBlock = null;
            else
            {
                r.PropertyBlock ??= new PropertySheet();
                r.PropertyBlock.CopyFrom(properties.Sheet);
            }
        }

        public void GetPropertyBlock(MaterialPropertyBlock properties)
        {
            if (properties == null) throw new ArgumentNullException(nameof(properties));
            var r = RendererData;
            if (r.PropertyBlock == null) properties.Clear();
            else properties.Writable.CopyFrom(r.PropertyBlock);
        }
    }

    public partial class MeshRenderer
    {
        public Mesh additionalVertexStreams
        {
            get => ((MeshRendererNative)RendererData).AdditionalVertexStreams;
            set => ((MeshRendererNative)RendererData).AdditionalVertexStreams = value;
        }
    }

    public partial class SpriteRenderer
    {
        SpriteRendererNative Data => (SpriteRendererNative)NativeOrThrow;

        /// Unity gives a SpriteRenderer created by AddComponent the project's default sprite
        /// material.
        internal override void EngineReset()
        {
            var d = Data;
            var material = ProjectSettings.GraphicsSettings == null ? null
                : ObjectLoader.Resolve(ProjectSettings.Managers, ProjectSettings.GraphicsSettings.PPtr("m_SpritesDefaultMaterial")) as Material;
            d.SetMaterials([material]);
        }

        /// In Simple draw mode, assigning a sprite sets the size to the sprite's size in units.
        public Sprite sprite
        {
            get => Data.Sprite;
            set
            {
                var d = Data;
                d.Sprite = value;
                if (value != null && d.DrawMode == SpriteDrawKind.Simple) d.Size = value.rect.size / value.pixelsPerUnit;
            }
        }


        public Vector2 size
        {
            get => Data.Size;
            set => Data.Size = value;
        }

        /// Simple at first; changing it keeps the size (measured in the original, UnityProbe's
        /// apigaps "misc"). Sliced draws the 9-slice grid of size; Tiled is kept but drawn as
        /// Sliced (tiling is not drawn).
        public SpriteDrawMode drawMode
        {
            get => (SpriteDrawMode)Data.DrawMode;
            set => Data.DrawMode = (SpriteDrawKind)value;
        }



        public Color color
        {
            get => Data.Color;
            set => Data.Color = value;
        }


        public bool flipX
        {
            get => Data.FlipX;
            set => Data.FlipX = value;
        }

        public bool flipY
        {
            get => Data.FlipY;
            set => Data.FlipY = value;
        }

    }

    public partial class MeshFilter
    {
        MeshFilterNative Data => (MeshFilterNative)NativeOrThrow;

        public Mesh sharedMesh
        {
            get => Data.Mesh;
            set { var d = Data; d.Mesh = value; d.MeshIsInstance = false; }
        }

        /// The mesh, copied for this filter on first access ("(Instance)").
        public Mesh mesh
        {
            get
            {
                var d = Data;
                if (d.Mesh == null) return null;
                if (!d.MeshIsInstance)
                {
                    var copy = (Mesh)Instantiate(d.Mesh);
                    copy.name = d.Mesh.name + " Instance";
                    d.Mesh = copy;
                    d.MeshIsInstance = true;
                }
                return d.Mesh;
            }
            set => sharedMesh = value;
        }
    }

    public partial class Light
    {
        LightNative Data => (LightNative)NativeOrThrow;

        public Color color
        {
            get => Data.Color;
            set => Data.Color = value;
        }

        public float intensity
        {
            get => Data.Intensity;
            set => Data.Intensity = value;
        }
    }
}

namespace UnityEngine.Rendering
{
    public partial class SortingGroup
    {
        /// Renderers outside any sorting group report this id.
        internal static int invalidSortingGroupID => SortingGroups.InvalidId;

        internal static SortingGroup GetSortingGroupByIndex(int index) => SortingGroups.ByIndex(index);

        SortingGroupNative Data => (SortingGroupNative)NativeOrThrow;

        public int sortingLayerID
        {
            get => Data.SortingLayerId;
            set => Data.SortingLayerId = value;
        }

        public string sortingLayerName
        {
            get => SortingLayers.NameOf(Data.SortingLayerId);
            set => Data.SortingLayerId = SortingLayers.IdOf(value);
        }

        public int sortingOrder
        {
            get => Data.SortingOrder;
            set => Data.SortingOrder = value;
        }

        public bool sortAtRoot
        {
            get => Data.SortAtRoot;
            set => Data.SortAtRoot = value;
        }

        // Group membership is computed on demand, so there is nothing to refresh.
        public static void UpdateAllSortingGroups() { }

        internal override void EngineOnEnable() => SortingGroups.Add(this);
        internal override void EngineOnDisable() => SortingGroups.Remove(this);
    }
}

namespace OniMoono.Engine.Rendering
{
    using UnityEngine;

    /// The project's sorting layers (TagManager), in render order.
    internal static class SortingLayers
    {
        static (string Name, int Id)[] layers = [("Default", 0)];

        public static void Install(IEnumerable<(string Name, int Id)> list)
        {
            var l = list.ToArray();
            layers = l.Length > 0 ? l : [("Default", 0)];
        }

        public static IEnumerable<int> Ids => layers.Select(l => l.Id);

        // A loop rather than Array.FindIndex with a lambda: draw items look layers up twice
        // each, and the capturing lambda allocated a closure and a delegate per lookup.
        static int IndexOf(int id)
        {
            var l = layers;
            for (int i = 0; i < l.Length; i++)
                if (l[i].Id == id) return i;
            return -1;
        }

        public static bool Contains(int id) => IndexOf(id) >= 0;

        public static int ValueOf(int id)
        {
            int index = IndexOf(id);
            int defaultIndex = IndexOf(0);
            return index < 0 ? 0 : index - defaultIndex;
        }

        public static string NameOf(int id)
        {
            int index = IndexOf(id);
            return index < 0 ? "<unknown layer>" : layers[index].Name;
        }

        public static int IdOf(string name)
        {
            foreach (var l in layers)
                if (l.Name == name) return l.Id;
            return 0;
        }
    }

    internal class RendererNative : ComponentNative
    {
        bool enabled = true;

        public bool Enabled
        {
            get => enabled;
            set
            {
                if (enabled == value) return;
                enabled = value;
                FrameRenderer.UpdateCandidate(this);
            }
        }

        /// Its index in FrameRenderer's cull candidates of its layer (-1: not one), that layer,
        /// and when it joined them (which orders its draws among ties).
        public int CandidateSlot = -1;
        public int CandidateLayer;
        public long CandidateStamp;
        public bool IsVisible;
        public ShadowCastingMode ShadowCasting = ShadowCastingMode.On;
        public bool ReceiveShadows = true;
        public LightProbeUsage LightProbeUsage = LightProbeUsage.BlendProbes;
        public ReflectionProbeUsage ReflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
        public uint RenderingLayerMask = 1;
        public int RendererPriority;
        public bool AllowOcclusionWhenDynamic = true;
        public MotionVectorGenerationMode MotionVectors = MotionVectorGenerationMode.Object;
        public int SortingLayerId;
        public short SortingOrder;
        public Material[] Materials = Array.Empty<Material>();
        /// Materials this renderer instantiated (Renderer.material).
        public bool[] Instanced = Array.Empty<bool>();
        public PropertySheet PropertyBlock;

        public Bounds? BoundsOverride
        {
            get => boundsOverride;
            set
            {
                boundsOverride = value;
                FrameRenderer.BoundsChanged(this);
            }
        }

        public Bounds? LocalBoundsOverride
        {
            get => localBoundsOverride;
            set
            {
                localBoundsOverride = value;
                FrameRenderer.BoundsChanged(this);
            }
        }

        Bounds? boundsOverride, localBoundsOverride;

        /// The mesh whose bounds its culling bounds were last made from, and its place in that
        /// mesh's readers (MeshNative.AddBoundsReader).
        public MeshNative BoundsMesh;
        public int BoundsMeshIndex = -1;

        public void SetMaterials(Material[] materials)
        {
            Materials = (Material[])materials.Clone();
            Instanced = new bool[materials.Length];
        }

        public Material InstanceAt(int index)
        {
            var m = Materials[index];
            if (m == null || Instanced[index]) return m;
            var copy = new Material(m) { name = m.name + " (Instance)" };
            Materials[index] = copy;
            Instanced[index] = true;
            return copy;
        }

        /// Bounds of the drawn geometry in local space (empty when there is none).
        protected virtual Bounds GeometryBounds => default;

        public Bounds LocalBounds => LocalBoundsOverride ?? GeometryBounds;

        public Bounds WorldBounds => BoundsOverride ?? Transform(LocalBounds, GameObject.Transform.LocalToWorld);

        /// WorldBounds for culling, and whether they may be kept until FrameRenderer.BoundsChanged
        /// is called for this renderer: then they are made only from what notifies it (the
        /// assigned bounds, the transform's world matrix, the GameObject's components and the
        /// mesh this reads), which this sets up.
        public Bounds CullBounds(out bool keep)
        {
            if (boundsOverride is { } b)
            {
                keep = true;
                return b;
            }
            Bounds local;
            if (localBoundsOverride is { } l)
            {
                local = l;
                keep = true;
            }
            else local = KeptGeometryBounds(out keep);
            var t = GameObject.Transform;
            if (keep)
            {
                GameObject.CullBoundsWatched = true;
                t.CullBoundsWatched = true;
            }
            return Transform(local, t.LocalToWorld);
        }

        /// GeometryBounds, and whether they are made only from the GameObject's components and
        /// the mesh they name (the readers of which this joins).
        protected virtual Bounds KeptGeometryBounds(out bool keep)
        {
            keep = false;
            return GeometryBounds;
        }

        /// The axis-aligned box enclosing a transformed box.
        public static Bounds Transform(Bounds b, Matrix4x4 m)
        {
            var center = m.MultiplyPoint3x4(b.center);
            var e = b.extents;
            var extents = new Vector3(
                Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
            return new Bounds(center, extents * 2f);
        }

        public override void CopyStateFrom(NativeObject source)
        {
            var r = (RendererNative)source;
            Enabled = r.Enabled;
            ShadowCasting = r.ShadowCasting;
            ReceiveShadows = r.ReceiveShadows;
            LightProbeUsage = r.LightProbeUsage;
            ReflectionProbeUsage = r.ReflectionProbeUsage;
            RenderingLayerMask = r.RenderingLayerMask;
            RendererPriority = r.RendererPriority;
            AllowOcclusionWhenDynamic = r.AllowOcclusionWhenDynamic;
            MotionVectors = r.MotionVectors;
            SortingLayerId = r.SortingLayerId;
            SortingOrder = r.SortingOrder;
            Materials = (Material[])r.Materials.Clone();
            Instanced = new bool[Materials.Length];
            if (r.PropertyBlock != null)
            {
                PropertyBlock = new PropertySheet();
                PropertyBlock.CopyFrom(r.PropertyBlock);
            }
            BoundsOverride = r.BoundsOverride;
            LocalBoundsOverride = r.LocalBoundsOverride;
        }

        /// Fields shared by every renderer class.
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Enabled = f.Bool("m_Enabled");
            ShadowCasting = (ShadowCastingMode)f.Get<byte>("m_CastShadows");
            ReceiveShadows = f.Get<byte>("m_ReceiveShadows") != 0;
            LightProbeUsage = (LightProbeUsage)f.Get<byte>("m_LightProbeUsage");
            ReflectionProbeUsage = (ReflectionProbeUsage)f.Get<byte>("m_ReflectionProbeUsage");
            RenderingLayerMask = f.Get<uint>("m_RenderingLayerMask");
            RendererPriority = f.Int("m_RendererPriority");
            if (f.Contains("m_DynamicOccludee")) AllowOcclusionWhenDynamic = f.Get<byte>("m_DynamicOccludee") != 0;
            if (f.Contains("m_MotionVectors")) MotionVectors = (MotionVectorGenerationMode)f.Get<byte>("m_MotionVectors");
            SetMaterials(f.Array("m_Materials").Cast<OniMoono.Assets.TypeTrees.PPtr>().Select(p => resolver.Resolve(p) as Material).ToArray());
            SortingLayerId = f.Int("m_SortingLayerID");
            SortingOrder = f.Get<short>("m_SortingOrder");
        }
    }

    internal sealed class MeshRendererNative : RendererNative
    {
        /// A MeshRenderer without a MeshFilter draws its GameObject's TextMesh.
        public static OniMoono.Engine.Text.TextMeshNative TextMesh(GameObjectNative go)
        {
            foreach (var c in go.Components)
                if (c is TextMesh && c.Native is OniMoono.Engine.Text.TextMeshNative t) return t;
            return null;
        }

        public Mesh AdditionalVertexStreams;

        protected override Bounds GeometryBounds
        {
            get
            {
                var mesh = FilterMesh();
                if (mesh != null) return mesh.Bounds;
                return TextMesh(GameObject)?.Geometry().Bounds ?? default;
            }
        }

        /// The mesh of the first mesh filter that has one.
        MeshNative FilterMesh()
        {
            foreach (var c in GameObject.Components)
                if (c is MeshFilter && c.Native is MeshFilterNative { Mesh: { } mesh } && mesh != null)
                    return (MeshNative)mesh.NativeOrThrow;
            return null;
        }

        /// A filter's mesh is kept (its bounds and destruction notify); a TextMesh's geometry is
        /// made again on every read.
        protected override Bounds KeptGeometryBounds(out bool keep)
        {
            var mesh = FilterMesh();
            if (mesh != null)
            {
                mesh.AddBoundsReader(this);
                keep = true;
                return mesh.Bounds;
            }
            var text = TextMesh(GameObject);
            keep = text == null;
            return text?.Geometry().Bounds ?? default;
        }

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            AdditionalVertexStreams = ((MeshRendererNative)source).AdditionalVertexStreams;
        }

        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            base.Bind(f, resolver);
            Serialized = null;
            AdditionalVertexStreams = resolver.Resolve(f.PPtr("m_AdditionalVertexStreams")) as Mesh;
        }
    }

    /// Unity's SpriteDrawMode values.
    internal enum SpriteDrawKind
    {
        Simple = 0,
        Sliced = 1,
        Tiled = 2,
    }

    internal sealed class SpriteRendererNative : RendererNative
    {
        public Sprite Sprite;
        public Color Color = Color.white;
        public bool FlipX, FlipY;
        public SpriteDrawKind DrawMode;
        public Vector2 Size = Vector2.one;
        public float AdaptiveModeThreshold = 0.5f;
        /// SpriteTileMode: 0 Continuous, 1 Adaptive.
        public int TileMode;
        /// SpriteMaskInteraction: 0 None, 1 VisibleInsideMask, 2 VisibleOutsideMask.
        public int MaskInteraction;
        /// SpriteSortPoint: 0 Center, 1 Pivot.
        public int SortPoint;

        /// The sprite's geometry bounds, or the drawn size in sliced/tiled mode; flips mirror
        /// them about the pivot.
        protected override Bounds GeometryBounds
        {
            get
            {
                if (Sprite == null) return default;
                Bounds b;
                if (DrawMode == SpriteDrawKind.Simple) b = Sprite.bounds;
                else
                {
                    var s = Sprite;
                    var pivot = s.pivot / s.rect.size;
                    b = new Bounds(new Vector3((0.5f - pivot.x) * Size.x, (0.5f - pivot.y) * Size.y, 0f), new Vector3(Size.x, Size.y, 0f));
                }
                var c = b.center;
                if (FlipX) c.x = -c.x;
                if (FlipY) c.y = -c.y;
                b.center = c;
                return b;
            }
        }

        MeshNative geometry;
        (Sprite Sprite, Color Color, bool FlipX, bool FlipY, SpriteDrawKind Mode, Vector2 Size, SpriteRenderData Render) geometryKey;

        /// The drawn geometry: the sprite's mesh (Simple) or a 9-slice grid of `Size` (Sliced),
        /// with flips applied to positions and the colour in vertex colours.
        public MeshNative GeometryMesh(SpriteNative s)
        {
            var key = (Sprite, Color, FlipX, FlipY, DrawMode, Size, s.Render);
            if (geometry != null && key.Equals(geometryKey)) return geometry;
            geometryKey = key;
            geometry ??= new MeshNative();
            Vector2[] positions, uvs;
            int[] triangles;
            if (DrawMode != SpriteDrawKind.Simple) SlicedGrid(s, out positions, out uvs, out triangles);
            else
            {
                positions = s.Vertices;
                uvs = s.ComputeUvs();
                triangles = Array.ConvertAll(s.Triangles, t => (int)t);
            }
            float fx = FlipX ? -1f : 1f, fy = FlipY ? -1f : 1f;
            var g = geometry;
            g.VertexCount = positions.Length;
            g.Positions = new Vector3[positions.Length];
            g.Colors = new Color[positions.Length];
            var uv0 = new Vector4[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                g.Positions[i] = new Vector3(positions[i].x * fx, positions[i].y * fy, 0f);
                g.Colors[i] = Color;
                uv0[i] = uvs[i];
            }
            g.Uvs[0] = uv0;
            g.UvDimensions[0] = 2;
            // Flipping one axis reverses the winding; keep triangles front-facing.
            if (FlipX != FlipY)
                for (int i = 0; i + 2 < triangles.Length; i += 3) (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
            g.SubMeshes = new List<SubMeshData> { new SubMeshData { Indices = triangles } };
            g.RecalculateBounds();
            return g;
        }

        /// 4×4 vertices: borders keep their size in units (scaled down when Size is smaller than
        /// both borders), the centre stretches.
        void SlicedGrid(SpriteNative s, out Vector2[] positions, out Vector2[] uvs, out int[] triangles)
        {
            float ppu = s.PixelsPerUnit;
            var border = s.Border / ppu;
            var pivot = s.Pivot;
            float left = -pivot.x * Size.x, bottom = -pivot.y * Size.y;
            float w = Math.Abs(Size.x), h = Math.Abs(Size.y);
            float bl = border.x, br = border.z, bb = border.y, bt = border.w;
            if (bl + br > w && bl + br > 0f) { float k = w / (bl + br); bl *= k; br *= k; }
            if (bb + bt > h && bb + bt > 0f) { float k = h / (bb + bt); bb *= k; bt *= k; }
            float[] xs = [left, left + bl, left + w - br, left + w];
            float[] ys = [bottom, bottom + bb, bottom + h - bt, bottom + h];
            var outer = s.OuterUvs();
            var inner = s.InnerUvs();
            float[] us = [outer.x, inner.x, inner.z, outer.z];
            float[] vs = [outer.y, inner.y, inner.w, outer.w];
            positions = new Vector2[16];
            uvs = new Vector2[16];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    positions[y * 4 + x] = new Vector2(xs[x], ys[y]);
                    uvs[y * 4 + x] = new Vector2(us[x], vs[y]);
                }
            var list = new List<int>(54);
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                {
                    int i = y * 4 + x;
                    list.AddRange([i, i + 4, i + 1, i + 1, i + 4, i + 5]);
                }
            triangles = list.ToArray();
        }

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var s = (SpriteRendererNative)source;
            Sprite = s.Sprite;
            Color = s.Color;
            FlipX = s.FlipX;
            FlipY = s.FlipY;
            DrawMode = s.DrawMode;
            Size = s.Size;
            AdaptiveModeThreshold = s.AdaptiveModeThreshold;
            TileMode = s.TileMode;
            MaskInteraction = s.MaskInteraction;
            SortPoint = s.SortPoint;
        }

        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            base.Bind(f, resolver);
            Serialized = null;
            Sprite = resolver.Resolve(f.PPtr("m_Sprite")) as Sprite;
            var c = f.Struct("m_Color");
            Color = new Color(c.Float("r"), c.Float("g"), c.Float("b"), c.Float("a"));
            FlipX = f.Bool("m_FlipX");
            FlipY = f.Bool("m_FlipY");
            DrawMode = (SpriteDrawKind)f.Int("m_DrawMode");
            if (DrawMode == SpriteDrawKind.Tiled) throw new System.IO.InvalidDataException("Tiled sprite renderers are not supported.");
            var size = f.Struct("m_Size");
            Size = new Vector2(size.Float("x"), size.Float("y"));
            AdaptiveModeThreshold = f.Float("m_AdaptiveModeThreshold");
            TileMode = f.Int("m_SpriteTileMode");
            MaskInteraction = f.Int("m_MaskInteraction");
            SortPoint = f.Int("m_SpriteSortPoint");
        }
    }

    internal sealed class MeshFilterNative : ComponentNative
    {
        public Mesh Mesh
        {
            get => mesh;
            set
            {
                mesh = value;
                if (GameObject is { CullBoundsWatched: true } go) FrameRenderer.BoundsInputsChanged(go);
            }
        }

        Mesh mesh;
        public bool MeshIsInstance;

        public override void CopyStateFrom(NativeObject source) => Mesh = ((MeshFilterNative)source).Mesh;

        /// MeshFilter (class 33).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            Mesh = resolver.Resolve(f.PPtr("m_Mesh")) as Mesh;
        }
    }

    internal sealed class LightNative : BehaviourNative
    {
        public Color Color = new(1f, 0.95686275f, 0.8392157f, 1f);
        public float Intensity = 1f;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var l = (LightNative)source;
            Color = l.Color;
            Intensity = l.Intensity;
        }

        /// Light (class 108): colour and intensity (the game reads only the colour).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            var c = f.Struct("m_Color");
            Color = new Color(c.Float("r"), c.Float("g"), c.Float("b"), c.Float("a"));
            Intensity = f.Float("m_Intensity");
        }
    }

    internal sealed class SortingGroupNative : BehaviourNative
    {
        public int SortingLayerId;
        public int SortingOrder;
        public bool SortAtRoot;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var g = (SortingGroupNative)source;
            SortingLayerId = g.SortingLayerId;
            SortingOrder = g.SortingOrder;
            SortAtRoot = g.SortAtRoot;
        }

        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            SortingLayerId = f.Int("m_SortingLayerID");
            SortingOrder = f.Get<short>("m_SortingOrder");
            SortAtRoot = f.Bool("m_SortAtRoot");
        }
    }

    /// Active sorting groups. A renderer belongs to its nearest active ancestor group; within a
    /// group, renderers are ordered by hierarchy (depth-first) order.
    internal static class SortingGroups
    {
        public const int InvalidId = 0xFFFFF;

        static readonly List<SortingGroup> active = new();

        public static void Add(SortingGroup g)
        {
            if (!active.Contains(g)) active.Add(g);
        }

        public static void Remove(SortingGroup g) => active.Remove(g);

        public static SortingGroup ByIndex(int index) => index >= 0 && index < active.Count ? active[index] : null;

        internal readonly record struct Membership(int Index, SortingGroup Group)
        {
            /// Position of the renderer among the group's renderers in hierarchy order.
            public int OrderOf(RendererNative renderer)
            {
                int order = 0;
                int found = -1;
                void Visit(GameObjectNative go)
                {
                    if (found >= 0) return;
                    foreach (var c in go.Components)
                        if (c.Native is RendererNative r)
                        {
                            if (r == renderer) found = order;
                            order++;
                        }
                    foreach (var child in go.Transform.Children) Visit(child.GameObject);
                }
                Visit(((ComponentNative)Group.NativeOrThrow).GameObject);
                return Math.Max(found, 0);
            }
        }

        public static Membership? GroupOf(RendererNative renderer)
        {
            for (var t = renderer.GameObject.Transform; t != null; t = t.Parent)
                foreach (var c in t.GameObject.Components)
                    if (c is SortingGroup g && c.Native is SortingGroupNative { IsActiveAndEnabledState: true })
                    {
                        int index = active.IndexOf(g);
                        if (index >= 0) return new Membership(index, g);
                    }
            return null;
        }
    }
}
