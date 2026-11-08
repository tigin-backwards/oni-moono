using System;
using UnityEngine;
using System.Collections.Generic;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;
using static OniMoono.Engine.Particles.ParticleFields;

namespace UnityEngine
{
    /// Mods draw lasers and debug lines with it (Capsule 2.0's mining laser). The game has none.
    public partial class LineRenderer
    {
        LineRendererNative Data => (LineRendererNative)NativeOrThrow;

        public int positionCount
        {
            get => Data.Positions.Count;
            set
            {
                var p = Data.Positions;
                int count = Math.Max(0, value);
                if (count < p.Count) p.RemoveRange(count, p.Count - count);
                while (p.Count < count) p.Add(Vector3.zero);
            }
        }

        public int numPositions
        {
            get => positionCount;
            set => positionCount = value;
        }

        public void SetVertexCount(int count) => positionCount = count;

        public void SetPosition(int index, Vector3 position)
        {
            var p = Data.Positions;
            if (index < 0 || index >= p.Count)
            {
                Debug.LogError($"LineRenderer.SetPosition index out of bounds!");
                return;
            }
            p[index] = position;
        }

        public Vector3 GetPosition(int index)
        {
            var p = Data.Positions;
            if (index < 0 || index >= p.Count)
            {
                Debug.LogError($"LineRenderer.GetPosition index out of bounds!");
                return Vector3.zero;
            }
            return p[index];
        }

        /// Sets as many positions as the array holds, up to positionCount.
        public void SetPositions(Vector3[] positions)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            var p = Data.Positions;
            for (int i = 0; i < p.Count && i < positions.Length; i++) p[i] = positions[i];
        }

        /// Copies as many positions as the array holds; returns how many there are.
        public int GetPositions(Vector3[] positions)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            var p = Data.Positions;
            for (int i = 0; i < p.Count && i < positions.Length; i++) positions[i] = p[i];
            return p.Count;
        }

        public float widthMultiplier
        {
            get => Data.WidthMultiplier;
            set => Data.WidthMultiplier = value;
        }

        /// A copy, as Unity returns.
        public AnimationCurve widthCurve
        {
            get => new(Data.WidthCurve.keys) { preWrapMode = Data.WidthCurve.preWrapMode, postWrapMode = Data.WidthCurve.postWrapMode };
            set => Data.WidthCurve = value == null ? new AnimationCurve(new Keyframe(0f, 1f)) : new AnimationCurve(value.keys) { preWrapMode = value.preWrapMode, postWrapMode = value.postWrapMode };
        }

        /// The width at the start: the multiplier times the curve's first value. Setting it (or
        /// endWidth) makes the curve a straight line from start to end.
        public float startWidth
        {
            get => Data.WidthMultiplier * Data.WidthCurve.Evaluate(0f);
            set => Data.SetWidths(value, endWidth);
        }

        public float endWidth
        {
            get => Data.WidthMultiplier * Data.WidthCurve.Evaluate(1f);
            set => Data.SetWidths(startWidth, value);
        }

        public void SetWidth(float start, float end) => Data.SetWidths(start, end);

        /// A copy, as Unity returns.
        public Gradient colorGradient
        {
            get
            {
                var g = new Gradient();
                g.CopyFrom(Data.ColorGradient);
                return g;
            }
            set
            {
                var g = new Gradient();
                if (value != null) g.CopyFrom(value);
                Data.ColorGradient = g;
            }
        }

        /// The gradient's colour at the start; setting it replaces the first keys (colour and
        /// alpha), keeping the end's.
        public Color startColor
        {
            get => Data.ColorGradient.Evaluate(0f);
            set => Data.SetColors(value, endColor);
        }

        public Color endColor
        {
            get => Data.ColorGradient.Evaluate(1f);
            set => Data.SetColors(startColor, value);
        }

        public void SetColors(Color start, Color end) => Data.SetColors(start, end);

        public int numCornerVertices
        {
            get => Data.CornerVertices;
            set => Data.CornerVertices = Math.Max(0, value);
        }

        public int numCapVertices
        {
            get => Data.CapVertices;
            set => Data.CapVertices = Math.Max(0, value);
        }

        public bool useWorldSpace
        {
            get => Data.UseWorldSpace;
            set => Data.UseWorldSpace = value;
        }

        public bool loop
        {
            get => Data.Loop;
            set => Data.Loop = value;
        }

        public LineAlignment alignment
        {
            get => Data.Alignment;
            set => Data.Alignment = value;
        }

        public LineTextureMode textureMode
        {
            get => Data.TextureMode;
            set => Data.TextureMode = value;
        }

        public Vector2 textureScale
        {
            get => Data.TextureScale;
            set => Data.TextureScale = value;
        }

        /// Shadows are not drawn by this renderer; the value is kept.
        public float shadowBias
        {
            get => Data.ShadowBias;
            set => Data.ShadowBias = value;
        }

        /// Lit line shaders' normals and tangents; no line shader the engine draws uses them,
        /// so the value is kept.
        public bool generateLightingData
        {
            get => Data.GenerateLightingData;
            set => Data.GenerateLightingData = value;
        }

        public bool applyActiveColorSpace
        {
            get => Data.ApplyActiveColorSpace;
            set => Data.ApplyActiveColorSpace = value;
        }

        public SpriteMaskInteraction maskInteraction
        {
            get => (SpriteMaskInteraction)Data.MaskInteraction;
            set => Data.MaskInteraction = (int)value;
        }
    }
}

namespace OniMoono.Engine.Rendering
{
    /// Engine side of a LineRenderer (class 120): a strip through its positions, built for each
    /// camera. The geometry follows Unity's as measured in the original player (LineRenderer.BakeMesh):
    /// - two vertices a point, at p − side × w/2 (uv v 1) and p + side × w/2 (uv v 0), side being
    ///   the unit cross of the view direction (camera forward, or the transform's Z for
    ///   TransformZ) with the line's direction; two triangles a segment;
    /// - width = widthMultiplier × widthCurve, colour = colorGradient, both at the point's share
    ///   of the line's length;
    /// - corners are mitered, the offset at most three half-widths; a loop closes back to the first
    ///   point, mitered there too;
    /// - uv u: Stretch the length share, Tile the distance × textureScale.x, RepeatPerSegment the
    ///   point's index, DistributePerSegment the index over the last index;
    /// - fewer than two points draw nothing; repeated points take the next segment's direction.
    /// Rounded corners and caps (numCornerVertices, numCapVertices) are drawn mitered and square.
    internal sealed class LineRendererNative : RendererNative
    {
        public List<Vector3> Positions = [Vector3.zero, new Vector3(0f, 0f, 1f)];
        public float WidthMultiplier = 1f;
        public AnimationCurve WidthCurve = new(new Keyframe(0f, 1f));
        public Gradient ColorGradient = new();
        public int CornerVertices, CapVertices;
        public LineAlignment Alignment = LineAlignment.View;
        public LineTextureMode TextureMode = LineTextureMode.Stretch;
        public Vector2 TextureScale = Vector2.one;
        public float ShadowBias;
        public bool GenerateLightingData;
        public bool UseWorldSpace = true;
        public bool Loop;
        public bool ApplyActiveColorSpace = true;
        public int MaskInteraction;
        bool roundingLogged;

        public void SetWidths(float start, float end)
        {
            float m = WidthMultiplier != 0f ? WidthMultiplier : 1f;
            if (WidthMultiplier == 0f) WidthMultiplier = 1f;
            float slope = (end - start) / m;
            WidthCurve = new AnimationCurve(new Keyframe(0f, start / m, slope, slope), new Keyframe(1f, end / m, slope, slope));
        }

        public void SetColors(Color start, Color end)
        {
            var g = new Gradient();
            g.SetKeys([new GradientColorKey(start, 0f), new GradientColorKey(end, 1f)], [new GradientAlphaKey(start.a, 0f), new GradientAlphaKey(end.a, 1f)]);
            ColorGradient = g;
        }

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var r = (LineRendererNative)source;
            Positions = new List<Vector3>(r.Positions);
            WidthMultiplier = r.WidthMultiplier;
            WidthCurve = new AnimationCurve(r.WidthCurve.keys) { preWrapMode = r.WidthCurve.preWrapMode, postWrapMode = r.WidthCurve.postWrapMode };
            ColorGradient = new Gradient();
            ColorGradient.CopyFrom(r.ColorGradient);
            CornerVertices = r.CornerVertices;
            CapVertices = r.CapVertices;
            Alignment = r.Alignment;
            TextureMode = r.TextureMode;
            TextureScale = r.TextureScale;
            ShadowBias = r.ShadowBias;
            GenerateLightingData = r.GenerateLightingData;
            UseWorldSpace = r.UseWorldSpace;
            Loop = r.Loop;
            ApplyActiveColorSpace = r.ApplyActiveColorSpace;
            MaskInteraction = r.MaskInteraction;
        }

        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            base.Bind(f, resolver);
            Serialized = null;
            Positions = new List<Vector3>();
            foreach (Fields p in f.Array("m_Positions")) Positions.Add(new Vector3(F(p, "x"), F(p, "y"), F(p, "z")));
            var p2 = f.Struct("m_Parameters");
            WidthMultiplier = F(p2, "widthMultiplier");
            WidthCurve = Curve(p2.Struct("widthCurve"));
            ColorGradient = Gradient(p2.Struct("colorGradient"));
            CornerVertices = I(p2, "numCornerVertices");
            CapVertices = I(p2, "numCapVertices");
            Alignment = (LineAlignment)I(p2, "alignment");
            TextureMode = (LineTextureMode)I(p2, "textureMode");
            // Bundles from before Unity 2022 (a mod's) have no texture scale.
            TextureScale = p2.Contains("textureScale") ? V2(p2, "textureScale") : Vector2.one;
            ShadowBias = F(p2, "shadowBias");
            GenerateLightingData = B(p2, "generateLightingData");
            UseWorldSpace = B(f, "m_UseWorldSpace");
            Loop = B(f, "m_Loop");
            ApplyActiveColorSpace = !f.Contains("m_ApplyActiveColorSpace") || B(f, "m_ApplyActiveColorSpace");
            MaskInteraction = f.Contains("m_MaskInteraction") ? I(f, "m_MaskInteraction") : 0;
        }

        /// A blend-mode gradient as the original evaluates line colours: between keys quantized
        /// to bytes, rounded (measured: alpha 255 → 128 halfway gives 192). Other modes as the
        /// gradient evaluates them.
        static Color32 Evaluate(Gradient gradient, float t)
        {
            if (gradient.Mode != 0) return gradient.Evaluate(t);
            static byte Q(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
            static byte Lerp(byte a, byte b, float f) => (byte)Math.Clamp(MathF.Round(a + (b - a) * f), 0f, 255f);
            var colors = gradient.colorKeys;
            var alphas = gradient.alphaKeys;
            int hi = 0;
            while (hi < colors.Length - 1 && colors[hi].time < t) hi++;
            Color32 c;
            if (t <= colors[0].time) c = colors[0].color;
            else if (t >= colors[^1].time) c = colors[^1].color;
            else
            {
                var a = colors[hi - 1];
                var b = colors[hi];
                float f = (t - a.time) / (b.time - a.time);
                c = new Color32(Lerp(Q(a.color.r), Q(b.color.r), f), Lerp(Q(a.color.g), Q(b.color.g), f), Lerp(Q(a.color.b), Q(b.color.b), f), 255);
            }
            hi = 0;
            while (hi < alphas.Length - 1 && alphas[hi].time < t) hi++;
            if (t <= alphas[0].time) c.a = Q(alphas[0].alpha);
            else if (t >= alphas[^1].time) c.a = Q(alphas[^1].alpha);
            else
            {
                var a = alphas[hi - 1];
                var b = alphas[hi];
                c.a = Lerp(Q(a.alpha), Q(b.alpha), (t - a.time) / (b.time - a.time));
            }
            return c;
        }

        /// The positions in world space.
        List<Vector3> WorldPoints()
        {
            var points = new List<Vector3>(Positions.Count);
            if (UseWorldSpace) points.AddRange(Positions);
            else
            {
                var toWorld = GameObject.Transform.LocalToWorld;
                foreach (var p in Positions) points.Add(toWorld.MultiplyPoint3x4(p));
            }
            return points;
        }

        /// The renderer's bounds: the points widened by the largest width, back in local space.
        protected override Bounds GeometryBounds
        {
            get
            {
                var points = WorldPoints();
                if (points.Count == 0) return default;
                var world = new Bounds(points[0], Vector3.zero);
                foreach (var p in points) world.Encapsulate(p);
                float widest = 0f;
                foreach (var k in WidthCurve.keys) widest = Math.Max(widest, Math.Abs(k.value));
                world.Expand(widest * Math.Abs(WidthMultiplier));
                return Transform(world, GameObject.Transform.LocalToWorld.inverse);
            }
        }

        /// Fills target with the strip as seen from a camera at viewPosition looking along
        /// viewForward (orthographic when perspective is false): world-space vertices (objectToWorld
        /// identity), positions, normals (towards the viewer), byte colours and uv0, one submesh.
        /// False (target untouched) when there is nothing to draw.
        internal bool BuildGeometry(Vector3 viewPosition, Vector3 viewForward, Vector3 viewRight, Vector3 viewUp, bool perspective, MeshNative target)
        {
            var points = WorldPoints();
            if (points.Count < 2) return false;
            if ((CornerVertices > 0 || CapVertices > 0) && !roundingLogged)
            {
                roundingLogged = true;
                EngineLog.Engine($"line renderer: LineRenderer on {GameObject.Name}: rounded corners and caps are drawn mitered and square");
            }
            bool loop = Loop && points.Count > 2;
            if (loop) points.Add(points[0]);
            int n = points.Count;

            var along = new float[n];
            for (int i = 1; i < n; i++) along[i] = along[i - 1] + (points[i] - points[i - 1]).magnitude;
            float total = along[n - 1];

            // Each segment's direction; a zero-length one takes the next (else the previous) one's.
            var directions = new Vector3[n - 1];
            for (int i = 0; i < n - 1; i++) directions[i] = points[i + 1] - points[i];
            for (int i = n - 3; i >= 0; i--) if (directions[i].sqrMagnitude == 0f) directions[i] = directions[i + 1];
            for (int i = 1; i < n - 1; i++) if (directions[i].sqrMagnitude == 0f) directions[i] = directions[i - 1];

            var transformZ = GameObject.Transform.WorldRotation * Vector3.forward;
            Vector3 Facing(Vector3 point) => Alignment == LineAlignment.TransformZ ? transformZ
                : perspective ? (point - viewPosition).normalized : viewForward;
            // A segment along the view direction has no side; Unity's falls back to this one
            // (measured: right minus up, normalized, for a camera with the world's axes).
            var fallback = (viewRight - viewUp).normalized;
            Vector3 Side(Vector3 direction, Vector3 facing)
            {
                var s = Vector3.Cross(facing, direction);
                return s.sqrMagnitude > 1e-12f ? s.normalized : fallback;
            }

            var positions = new Vector3[n * 2];
            var normals = new Vector3[n * 2];
            var colors = new Color[n * 2];
            var uvs = new Vector4[n * 2];
            for (int i = 0; i < n; i++)
            {
                var p = points[i];
                var facing = Facing(p);
                float share = total > 0f ? along[i] / total : 0f;
                float half = WidthMultiplier * WidthCurve.Evaluate(share) * 0.5f;
                // The incoming and outgoing segments (a loop's ends join its last and first).
                Vector3? incoming = i > 0 ? directions[i - 1] : loop ? directions[n - 2] : null;
                Vector3? outgoing = i < n - 1 ? directions[i] : loop ? directions[0] : null;
                Vector3 offset;
                if (incoming is { } a && outgoing is { } b)
                {
                    var sa = Side(a, facing);
                    var sb = Side(b, facing);
                    var miter = sa + sb;
                    float cos = miter.sqrMagnitude > 1e-12f ? Vector3.Dot(miter.normalized, sa) : 0f;
                    offset = cos > 1e-6f ? miter.normalized * Math.Min(half / cos, half * 3f) : sb * half;
                }
                else offset = Side((incoming ?? outgoing).Value, facing) * half;

                float u = TextureMode switch
                {
                    LineTextureMode.Tile => along[i] * TextureScale.x,
                    LineTextureMode.RepeatPerSegment => i,
                    LineTextureMode.DistributePerSegment => n > 1 ? i / (float)(n - 1) : 0f,
                    _ => share,
                };
                var color = (Color)Evaluate(ColorGradient, share);
                var normal = -facing;
                positions[i * 2] = p - offset;
                positions[i * 2 + 1] = p + offset;
                normals[i * 2] = normals[i * 2 + 1] = normal;
                colors[i * 2] = colors[i * 2 + 1] = color;
                uvs[i * 2] = new Vector4(u, 1f, 0f, 0f);
                uvs[i * 2 + 1] = new Vector4(u, 0f, 0f, 0f);
            }
            var indices = new int[(n - 1) * 6];
            for (int i = 0; i < n - 1; i++)
            {
                int v = i * 2, k = i * 6;
                indices[k] = v; indices[k + 1] = v + 1; indices[k + 2] = v + 2;
                indices[k + 3] = v + 1; indices[k + 4] = v + 3; indices[k + 5] = v + 2;
            }

            target.Clear();
            target.VertexCount = positions.Length;
            target.Positions = positions;
            target.Normals = normals;
            target.Colors = colors;
            target.ColorsAreBytes = true;
            target.Uvs[0] = uvs;
            target.UvDimensions[0] = 2;
            target.IndexFormat = positions.Length > ushort.MaxValue ? 1 : 0;
            target.Dynamic = true;
            target.SubMeshes = new List<SubMeshData> { new(indices) };
            target.RecalculateBounds();
            return true;
        }
    }
}
