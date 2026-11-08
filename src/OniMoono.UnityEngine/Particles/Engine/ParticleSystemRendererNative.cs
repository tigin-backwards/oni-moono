using System;
using System.Collections.Generic;
using System.IO;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;
using UnityEngine;
using static OniMoono.Engine.Particles.ParticleFields;

namespace OniMoono.Engine.Particles
{
    /// What particle geometry needs to know about the viewer: the camera's world pose and,
    /// when known, its projection (for the min/max particle size limits, which are fractions of
    /// the viewport height).
    internal readonly struct ParticleView
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Matrix4x4? Projection;

        public ParticleView(Vector3 position, Quaternion rotation, Matrix4x4? projection)
        {
            Position = position;
            Rotation = rotation;
            Projection = projection;
        }

        public Vector3 Forward => Rotation * Vector3.forward;
        public Vector3 Right => Rotation * Vector3.right;
        public Vector3 Up => Rotation * Vector3.up;

        /// Orthographic when the projection has no perspective row (m32 = 0).
        public bool IsPerspective => Projection is { } p && p.m32 != 0f;

        /// World height of the viewport at a point, or null without a projection. Unity's
        /// projection gives m11 = 1/tan(fov/2) (perspective) or 1/orthographicSize.
        public float? ViewportHeightAt(Vector3 point)
        {
            if (Projection is not { } p || p.m11 == 0f) return null;
            if (p.m32 == 0f) return 2f / Math.Abs(p.m11);
            float depth = Vector3.Dot(point - Position, Forward);
            return 2f * Math.Max(depth, 0f) / Math.Abs(p.m11);
        }
    }

    /// Engine side of a ParticleSystemRenderer (class 199): how the sibling ParticleSystem's
    /// particles are drawn.
    internal sealed class ParticleSystemRendererNative : RendererNative
    {
        /// ParticleSystemRenderMode: 0 Billboard, 1 Stretch, 2 HorizontalBillboard,
        /// 3 VerticalBillboard, 4 Mesh, 5 None.
        public int RenderMode;
        /// ParticleSystemSortMode: 0 None, 1 Distance, 2 OldestInFront, 3 YoungestInFront, 4 Depth.
        public int SortMode;
        /// ParticleSystemRenderSpace: 0 View, 1 World, 2 Local, 3 Facing, 4 Velocity.
        public int Alignment;
        public float MinParticleSize;
        public float MaxParticleSize = 0.5f;
        public float LengthScale = 2f;
        public float VelocityScale;
        public float CameraVelocityScale;
        public float NormalDirection = 1f;
        public float SortingFudge;
        public Vector3 Pivot;

        /// Installed by the camera module: a camera's projection matrix, so geometry built for a
        /// Camera can apply the min/max particle size limits. Without it the limits are skipped.
        public static Func<Camera, Matrix4x4> CameraProjection;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var r = (ParticleSystemRendererNative)source;
            RenderMode = r.RenderMode;
            SortMode = r.SortMode;
            Alignment = r.Alignment;
            MinParticleSize = r.MinParticleSize;
            MaxParticleSize = r.MaxParticleSize;
            LengthScale = r.LengthScale;
            VelocityScale = r.VelocityScale;
            CameraVelocityScale = r.CameraVelocityScale;
            NormalDirection = r.NormalDirection;
            SortingFudge = r.SortingFudge;
            Pivot = r.Pivot;
        }

        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            base.Bind(f, resolver);
            Serialized = null;
            string owner = GameObject?.Name ?? "?";
            // As ParticleConfig.Read: what the game's data never uses is logged and left out.
            void Unsupported(string what) => EngineLog.Engine($"particles: ParticleSystemRenderer on {owner}: {what} is not drawn as Unity draws it");
            RenderMode = I(f, "m_RenderMode");
            SortMode = I(f, "m_SortMode");
            Alignment = I(f, "m_RenderAlignment");
            MinParticleSize = F(f, "m_MinParticleSize");
            MaxParticleSize = F(f, "m_MaxParticleSize");
            CameraVelocityScale = F(f, "m_CameraVelocityScale");
            VelocityScale = F(f, "m_VelocityScale");
            LengthScale = F(f, "m_LengthScale");
            SortingFudge = F(f, "m_SortingFudge");
            NormalDirection = F(f, "m_NormalDirection");
            Pivot = V3(f, "m_Pivot");
            if (RenderMode is 4 or < 0 or > 5)
            {
                Unsupported(RenderMode == 4 ? "mesh particles (not drawn)" : $"render mode {RenderMode} (not drawn)");
                RenderMode = 5;
            }
            if (SortMode is < 0 or > 4)
            {
                Unsupported($"sort mode {SortMode} (unsorted)");
                SortMode = 0;
            }
            if (Alignment == 4 && RenderMode == 0)
            {
                Unsupported("velocity-aligned billboards (view-aligned)");
                Alignment = 0;
            }
            if (V3(f, "m_Flip") != Vector3.zero) Unsupported("particle flipping");
            if (CameraVelocityScale != 0f) Unsupported("camera velocity stretching");
            if (B(f, "m_FreeformStretching")) Unsupported("freeform stretching");
            if (B(f, "m_UseCustomVertexStreams")) Unsupported("custom vertex streams");
        }

        ParticleSystemNative System
        {
            get
            {
                foreach (var c in GameObject.Components)
                    if (c is ParticleSystem && c.Native is ParticleSystemNative ps) return ps;
                return null;
            }
        }

        bool Stretched => RenderMode == 1;

        /// Local-space bounds (Renderer.localBounds) of the particles' world AABB.
        protected override Bounds GeometryBounds
        {
            get
            {
                var ps = System;
                if (ps == null || !ps.WorldBounds(Stretched ? LengthScale : 0f, Stretched ? VelocityScale : 0f, out var world)) return default;
                return Transform(world, GameObject.Transform.LocalToWorld.inverse);
            }
        }

        /// Geometry for a camera; see the overload taking a ParticleView.
        internal bool BuildGeometry(Camera camera, MeshNative target, out Matrix4x4 objectToWorld)
        {
            var t = (TransformNative)camera.transform.NativeOrThrow;
            var projection = CameraProjection != null ? CameraProjection(camera) : (Matrix4x4?)null;
            return BuildGeometry(new ParticleView(Hierarchy.Position(t), t.WorldRotation, projection), target, out objectToWorld);
        }

        /// Fills target with this frame's particle geometry as seen from view, for the frame
        /// renderer. Contract:
        /// - Returns false (target untouched) when there is nothing to draw: render mode None,
        ///   no ParticleSystem, or no live particles or trails.
        /// - Vertices are in world space and objectToWorld is identity (Unity builds particle
        ///   geometry in world space). Streams: positions, normals, colors (Color32-quantized,
        ///   ColorsAreBytes) and uv0 (2D). Quads are clockwise as seen from the camera.
        /// - Submesh 0 holds the particles (drawn with material 0); when the trail module is
        ///   enabled submesh 1 holds the trail ribbons (material 1, Unity's trail material
        ///   slot). Particles are ordered for back-to-front blending per the sort mode.
        /// - The mesh is marked Dynamic and its Version bumped (Changed).
        internal bool BuildGeometry(in ParticleView view, MeshNative target, out Matrix4x4 objectToWorld)
        {
            objectToWorld = Matrix4x4.identity;
            var ps = System;
            if (ps == null || RenderMode is 4 or 5) return false;
            var particles = ps.Particles;
            bool trails = ps.Config.TrailsEnabled || ps.OrphanTrails.Count > 0;
            if (particles.Length == 0 && ps.OrphanTrails.Count == 0) return false;

            // The builder and the per-particle scratch are reused from system to system (main
            // thread: the frame renderer's culling); the mesh gets arrays of its own (Fill).
            var b = builder;
            b.Reset();
            var emitter = ps.CaptureEmitter();
            int n = particles.Length;
            var looks = Grow(ref b.Looks, n);
            for (int i = 0; i < n; i++) looks[i] = ps.Look(particles[i], emitter);
            var order = SortOrder(particles, looks, view, b);

            var quads = b.Quads;
            foreach (int i in order) AddParticle(b, quads, looks[i], view);

            List<int> ribbons = null;
            if (trails)
            {
                ribbons = b.Ribbons;
                var toWorld = ps.Config.SimulationSpace == 1 ? Matrix4x4.identity : emitter.LocalToWorld;
                for (int i = 0; i < particles.Length; i++)
                    if (particles[i].Trail is { } trail)
                        AddTrail(b, ribbons, ps.Config, trail, looks[i].Position, looks[i].Size.x, looks[i].Color, looks[i].AgeNormalized, true, toWorld, view);
                foreach (var trail in ps.OrphanTrails)
                    AddTrail(b, ribbons, ps.Config, trail, default, trail.LastWidth, trail.LastColor, trail.LastAgeNormalized, false, toWorld, view);
            }

            b.Fill(target, ribbons != null);
            target.RecalculateBounds();
            return true;
        }

        static readonly GeometryBuilder builder = new();

        static T[] Grow<T>(ref T[] scratch, int length)
        {
            if (scratch.Length < length) scratch = new T[Math.Max(length, scratch.Length * 2)];
            return scratch;
        }

        /// The draw order of the particles (in the builder's scratch, valid until its next use).
        ReadOnlySpan<int> SortOrder(ReadOnlySpan<Particle> particles, ParticleSystemNative.ParticleLook[] looks, in ParticleView view, GeometryBuilder b)
        {
            int n = particles.Length;
            var order = Grow(ref b.Order, n).AsSpan(0, n);
            for (int i = 0; i < order.Length; i++) order[i] = i;
            if (SortMode == 0 || order.Length < 2) return order;
            var keys = Grow(ref b.Keys, n).AsSpan(0, n);
            var forward = view.Forward;
            for (int i = 0; i < keys.Length; i++)
                keys[i] = SortMode switch
                {
                    // Farthest first, so nearer particles blend over them.
                    1 => -(looks[i].Position - view.Position).sqrMagnitude,
                    4 => -Vector3.Dot(looks[i].Position - view.Position, forward),
                    // Oldest in front: youngest drawn first.
                    2 => particles[i].Age,
                    // Youngest in front: oldest drawn first.
                    _ => -particles[i].Age,
                };
            // Stable, so equal keys keep buffer order (the comparison orders every pair, so any
            // sort gives this one order).
            var indexKeys = Grow(ref b.IndexKeys, n).AsSpan(0, n);
            for (int i = 0; i < order.Length; i++) indexKeys[i] = (keys[i], i);
            indexKeys.Sort(byKeyThenIndex);
            for (int i = 0; i < order.Length; i++) order[i] = indexKeys[i].Index;
            return order;
        }

        static readonly Comparison<(float Key, int Index)> byKeyThenIndex = (x, y) => x.Key != y.Key ? x.Key.CompareTo(y.Key) : x.Index.CompareTo(y.Index);

        /// Clamps a particle's drawn size to [min, max] × the viewport height at its position.
        Vector2 Clamped(Vector2 size, Vector3 position, in ParticleView view)
        {
            if (view.ViewportHeightAt(position) is not { } height) return size;
            float min = MinParticleSize * height, max = MaxParticleSize * height;
            return new Vector2(Math.Clamp(size.x, min, Math.Max(min, max)), Math.Clamp(size.y, min, Math.Max(min, max)));
        }

        void AddParticle(GeometryBuilder b, List<int> indices, in ParticleSystemNative.ParticleLook p, in ParticleView view)
        {
            var size = Clamped(new Vector2(p.Size.x, p.Size.y), p.Position, view);
            var toCamera = view.IsPerspective ? (view.Position - p.Position).normalized : -view.Forward;
            Vector3 right, up, normal, center;

            if (Stretched)
            {
                // The quad trails the particle: its head is at the particle, its length is
                // lengthScale × size plus velocityScale × speed, its width the particle size.
                float speed = p.Velocity.magnitude;
                var axis = speed > 0f ? p.Velocity / speed : view.Up;
                float length = size.x * LengthScale + speed * VelocityScale;
                var side = Vector3.Cross(axis, toCamera);
                side = side.sqrMagnitude > 0f ? side.normalized : view.Right;
                right = side * (size.y * 0.5f);
                up = axis * (length * 0.5f);
                center = p.Position - up + up * (2f * Pivot.y) + right * (2f * Pivot.x);
                normal = toCamera;
            }
            else
            {
                Vector3 baseRight, baseUp;
                switch (RenderMode)
                {
                    case 2:
                        // Horizontal billboard: flat on the XZ plane, facing up.
                        baseRight = Vector3.right;
                        baseUp = Vector3.forward;
                        normal = Vector3.up;
                        break;
                    case 3:
                        // Vertical billboard: faces the camera but stays upright.
                        baseUp = Vector3.up;
                        var flat = new Vector3(toCamera.x, 0f, toCamera.z);
                        normal = flat.sqrMagnitude > 0f ? flat.normalized : Vector3.back;
                        baseRight = Vector3.Cross(baseUp, normal);
                        break;
                    default:
                        switch (Alignment)
                        {
                            case 1: // World: facing -Z.
                                baseRight = Vector3.right;
                                baseUp = Vector3.up;
                                normal = Vector3.back;
                                break;
                            case 2: // Local: the emitter's orientation.
                            {
                                var rotation = ((TransformNative)GameObject.Transform).WorldRotation;
                                baseRight = rotation * Vector3.right;
                                baseUp = rotation * Vector3.up;
                                normal = rotation * Vector3.back;
                                break;
                            }
                            case 3: // Facing: toward the camera position.
                            {
                                normal = (view.Position - p.Position).normalized;
                                var r = Vector3.Cross(view.Up, normal);
                                baseRight = r.sqrMagnitude > 0f ? r.normalized : view.Right;
                                baseUp = Vector3.Cross(normal, baseRight);
                                break;
                            }
                            default: // View: parallel to the camera plane.
                                baseRight = view.Right;
                                baseUp = view.Up;
                                normal = -view.Forward;
                                break;
                        }
                        break;
                }
                // Positive rotation turns the particle clockwise as seen by the viewer.
                float cos = MathF.Cos(p.Rotation), sin = MathF.Sin(p.Rotation);
                right = (baseRight * cos - baseUp * sin) * (size.x * 0.5f);
                up = (baseRight * sin + baseUp * cos) * (size.y * 0.5f);
                // The pivot offsets the quad in units of the particle's size.
                center = p.Position + right * (2f * Pivot.x) + up * (2f * Pivot.y) + normal * (Pivot.z * size.x);
            }

            // Normals bend from the face normal toward the corners as normalDirection drops
            // below 1 (spherical lighting).
            Vector3 CornerNormal(Vector3 offset) => Vector3.LerpUnclamped(offset.normalized, normal, NormalDirection).normalized;
            var c32 = (Color)(Color32)p.Color;
            var uv = p.Uv;
            int v = b.Count;
            b.Add(center - right - up, CornerNormal(-right - up), c32, new Vector2(uv.xMin, uv.yMin));
            b.Add(center - right + up, CornerNormal(-right + up), c32, new Vector2(uv.xMin, uv.yMax));
            b.Add(center + right + up, CornerNormal(right + up), c32, new Vector2(uv.xMax, uv.yMax));
            b.Add(center + right - up, CornerNormal(right - up), c32, new Vector2(uv.xMax, uv.yMin));
            indices.Add(v); indices.Add(v + 1); indices.Add(v + 2);
            indices.Add(v); indices.Add(v + 2); indices.Add(v + 3);
        }

        /// A camera-facing ribbon through the trail's points (newest at the head). Width is the
        /// particle size (when sizeAffectsWidth) × widthOverTrail; color is the particle color
        /// (when inheritParticleColor) × colorOverLifetime × colorOverTrail; the texture is
        /// stretched once along the trail, u = 0 at the head.
        static void AddTrail(GeometryBuilder b, List<int> indices, ParticleConfig c, TrailPath trail, Vector3 head, float particleWidth,
            Color particleColor, float ageNormalized, bool live, Matrix4x4 toWorld, in ParticleView view)
        {
            var points = b.Points;
            points.Clear();
            if (live) points.Add(head);
            for (int i = trail.Points.Count - 1; i >= 0; i--)
            {
                var w = toWorld.MultiplyPoint3x4(trail.Points[i].Position);
                if (points.Count == 0 || (w - points[^1]).sqrMagnitude > 1e-12f) points.Add(w);
            }
            if (points.Count < 2) return;

            float total = 0f;
            var along = Grow(ref b.Along, points.Count);
            along[0] = 0f;
            for (int i = 1; i < points.Count; i++) along[i] = total += (points[i] - points[i - 1]).magnitude;
            if (total <= 0f) return;

            float widthRandom = ParticleRandom.Hash01(trail.Seed, Salt.TrailWidth);
            float colorRandom = ParticleRandom.Hash01(trail.Seed, Salt.TrailColor);
            var baseColor = (c.TrailInheritParticleColor ? particleColor : Color.white) * c.TrailColorOverLifetime.Evaluate(ageNormalized, colorRandom);
            int start = b.Count;
            for (int i = 0; i < points.Count; i++)
            {
                float u = along[i] / total;
                var tangent = i == 0 ? points[0] - points[1] : i == points.Count - 1 ? points[i - 1] - points[i] : points[i - 1] - points[i + 1];
                var toCamera = view.IsPerspective ? (view.Position - points[i]).normalized : -view.Forward;
                var side = Vector3.Cross(tangent, toCamera);
                side = side.sqrMagnitude > 0f ? side.normalized : view.Right;
                float width = (c.TrailSizeAffectsWidth ? particleWidth : 1f) * c.TrailWidthOverTrail.Evaluate(u, widthRandom);
                var color = (Color)(Color32)(baseColor * c.TrailColorOverTrail.Evaluate(u, colorRandom));
                b.Add(points[i] - side * (width * 0.5f), toCamera, color, new Vector2(u, 0f));
                b.Add(points[i] + side * (width * 0.5f), toCamera, color, new Vector2(u, 1f));
            }
            for (int i = 0; i + 1 < points.Count; i++)
            {
                int v = start + i * 2;
                indices.Add(v); indices.Add(v + 1); indices.Add(v + 3);
                indices.Add(v); indices.Add(v + 3); indices.Add(v + 2);
            }
        }

        /// One system's geometry as it is built, and the scratch for building it: one builder,
        /// reused for every system (main thread). Fill copies it into arrays of the mesh's own,
        /// taken from the mesh's spares (MeshNative.NewChannelAtLeast): the device's upload holds
        /// those until it has run, and the mesh reuses them only after that.
        sealed class GeometryBuilder
        {
            readonly List<Vector3> positions = new();
            readonly List<Vector3> normals = new();
            readonly List<Color32> colors = new();
            readonly List<Vector4> uvs = new();
            public readonly List<int> Quads = new(), Ribbons = new();
            public readonly List<Vector3> Points = new();
            public ParticleSystemNative.ParticleLook[] Looks = [];
            public int[] Order = [];
            public float[] Keys = [];
            public (float Key, int Index)[] IndexKeys = [];
            public float[] Along = [];

            public int Count => positions.Count;

            public void Reset()
            {
                positions.Clear();
                normals.Clear();
                colors.Clear();
                uvs.Clear();
                Quads.Clear();
                Ribbons.Clear();
            }

            /// Colours are given quantized to bytes (Color)(Color32), and uploaded as the bytes
            /// they convert back to, as the device converted them before.
            public void Add(Vector3 position, Vector3 normal, Color color, Vector2 uv)
            {
                positions.Add(position);
                normals.Add(normal);
                colors.Add(color);
                uvs.Add(new Vector4(uv.x, uv.y, 0f, 0f));
            }

            public void Fill(MeshNative mesh, bool ribbons)
            {
                mesh.Clear();
                int n = positions.Count;
                mesh.VertexCount = n;
                mesh.Positions = Channel(mesh, positions, MeshNative.SlotPositions);
                mesh.Normals = Channel(mesh, normals, MeshNative.SlotNormals);
                var colors32 = Copy(mesh, colors);
                mesh.SetColors32(colors32);
                if (colors32 != null) mesh.Give(MeshNative.SlotColors32, colors32);
                mesh.ColorsAreBytes = true;
                mesh.Uvs[0] = Channel(mesh, uvs, MeshNative.SlotUv0);
                mesh.UvDimensions[0] = 2;
                mesh.IndexFormat = n > ushort.MaxValue ? 1 : 0;
                mesh.Dynamic = true;
                Indices(mesh, mesh.SubMeshes[0], Quads);
                if (ribbons)
                {
                    var trail = new SubMeshData();
                    mesh.SubMeshes.Add(trail);
                    Indices(mesh, trail, Ribbons);
                }
            }

            static T[] Channel<T>(MeshNative mesh, List<T> values, int slot) where T : struct
            {
                var array = Copy(mesh, values);
                if (array != null) mesh.Give(slot, array);
                return array;
            }

            static T[] Copy<T>(MeshNative mesh, List<T> values) where T : struct
            {
                if (values.Count == 0) return null;
                var array = mesh.NewChannelAtLeast<T>(values.Count);
                global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values).CopyTo(array);
                return array;
            }

            static void Indices(MeshNative mesh, SubMeshData s, List<int> indices)
            {
                if (indices.Count == 0)
                {
                    s.SetIndices(Array.Empty<int>());
                    return;
                }
                var array = mesh.NewChannelAtLeast<int>(indices.Count);
                global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices).CopyTo(array);
                s.SetIndices(array, indices.Count);
                mesh.GiveIndices(s);
            }
        }
    }
}
