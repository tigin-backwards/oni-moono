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

    /// A renderer's custom vertex streams (ParticleSystemVertexStream values, as m_VertexStreams
    /// stores them): which vertex attributes the geometry has, and where each stream that goes
    /// to a TEXCOORD lands. Measured (UnityProbe particlestreams, BakeMesh, j0010421 and
    /// j0010426): the streams other than Position, Normal, Tangent and Color are packed one after
    /// the other into TEXCOORD0.xyzw, TEXCOORD1.xyzw, …, a stream running on into the next
    /// register when it does not fit (UV, StableRandomXYZW and Custom1XYZ: TEXCOORD0.xy,
    /// TEXCOORD0.zw and TEXCOORD1.xy, TEXCOORD1.zw and TEXCOORD2.x); the last register has the
    /// components used (TEXCOORD1 one float for UV, StableRandomXY, Custom1X). Unity's manual
    /// gives the same for UV, UV2 and AnimBlend (Particle System vertex streams).
    internal sealed class VertexStreams
    {
        public const int Position = 0, Normal = 1, Tangent = 2, Color = 3, UV = 4, UV2 = 5, AnimBlend = 8, AnimFrame = 9,
            Center = 10, SizeX = 12, SizeXY = 13, SizeXYZ = 14, Velocity = 19, Speed = 20, AgePercent = 21, InvStartLifetime = 22,
            StableRandomX = 23, StableRandomXYZW = 26, Custom1X = 31, Custom1XYZW = 34, Custom2X = 35, Custom2XYZW = 38;

        public bool HasNormal, HasTangent, HasColor;
        /// The TEXCOORD streams in order: the stream, its first component (TEXCOORD n's x is 4n)
        /// and its component count.
        public (int Stream, int Offset, int Count)[] Texcoords = [];
        public int Components;
        /// Where the UV stream starts (-1: none).
        public int UvOffset = -1;
        public bool NeedsStableRandom, NeedsCustom1, NeedsCustom2;

        public int Channels => (Components + 3) / 4;
        public int Dimension(int channel) => Math.Min(4, Components - channel * 4);

        /// Components of a stream packed into the TEXCOORDs, by its name (X, XY, XYZ, XYZW; UVs
        /// two, 3D values three; the probe's: UV2 2, Center 3, SizeX 1, SizeXYZ 3, Velocity 3,
        /// Rotation 1, AgePercent 1, AnimBlend 1, AnimFrame 1, StableRandomXY 2, StableRandomXYZW 4,
        /// Custom1X 1, Custom1XYZ 3, Custom1XYZW 4, Custom2XYZ 3); 0 for Position, Normal, Tangent
        /// and Color; -1 for streams whose count is not known.
        static int Count(int stream) => stream switch
        {
            Position or Normal or Tangent or Color => 0,
            UV or UV2 or 6 or 7 => 2,
            AnimBlend or AnimFrame or 11 or SizeX or 15 or 17 or Speed or AgePercent or InvStartLifetime => 1,
            Center or SizeXYZ or 16 or 18 or Velocity => 3,
            SizeXY => 2,
            >= StableRandomX and <= Custom2XYZW => (stream - StableRandomX) % 4 + 1,
            >= 39 and <= 44 => (stream - 39) % 3 + 1,
            _ => -1,
        };

        /// Streams whose values the geometry fills; the others read 0.
        static bool Filled(int stream) => stream is UV or UV2 or AnimBlend or AnimFrame or Center or SizeX or SizeXY or SizeXYZ
            or Velocity or Speed or AgePercent or InvStartLifetime or (>= StableRandomX and <= Custom2XYZW and not (>= 27 and <= 30));

        /// ParticleSystemVertexStream's names (Unity 6000.3, j0010421), for the log.
        static readonly string[] names =
        [
            "Position", "Normal", "Tangent", "Color", "UV", "UV2", "UV3", "UV4", "AnimBlend", "AnimFrame", "Center", "VertexID",
            "SizeX", "SizeXY", "SizeXYZ", "Rotation", "Rotation3D", "RotationSpeed", "RotationSpeed3D", "Velocity", "Speed",
            "AgePercent", "InvStartLifetime", "StableRandomX", "StableRandomXY", "StableRandomXYZ", "StableRandomXYZW",
            "VaryingRandomX", "VaryingRandomXY", "VaryingRandomXYZ", "VaryingRandomXYZW", "Custom1X", "Custom1XY", "Custom1XYZ",
            "Custom1XYZW", "Custom2X", "Custom2XY", "Custom2XYZ", "Custom2XYZW", "NoiseSumX", "NoiseSumXY", "NoiseSumXYZ",
            "NoiseImpulseX", "NoiseImpulseXY", "NoiseImpulseXYZ", "MeshIndex", "ParticleIndex", "ColorPackedAsTwoFloats",
            "MeshAxisOfRotation", "NextTrailCenter", "PreviousTrailCenter", "PercentageAlongTrail", "TrailWidth",
        ];

        static string Name(int stream) => stream >= 0 && stream < names.Length ? names[stream] : stream.ToString();

        /// The layout of a stream list, or null (logged) when a stream's size is not known.
        public static VertexStreams Of(IReadOnlyList<int> streams, Action<string> unsupported)
        {
            var v = new VertexStreams();
            var texcoords = new List<(int, int, int)>();
            foreach (int s in streams)
            {
                int n = Count(s);
                if (n < 0)
                {
                    unsupported($"custom vertex stream {Name(s)} (default streams drawn)");
                    return null;
                }
                switch (s)
                {
                    case Normal: v.HasNormal = true; continue;
                    case Tangent: v.HasTangent = true; continue;
                    case Color: v.HasColor = true; continue;
                    case Position: continue;
                }
                if (!Filled(s)) unsupported($"custom vertex stream {Name(s)} (zeros)");
                if (s == UV && v.UvOffset < 0) v.UvOffset = v.Components;
                v.NeedsStableRandom |= s is >= StableRandomX and <= StableRandomXYZW;
                v.NeedsCustom1 |= s is >= Custom1X and <= Custom1XYZW;
                v.NeedsCustom2 |= s is >= Custom2X and <= Custom2XYZW;
                texcoords.Add((s, v.Components, n));
                v.Components += n;
            }
            if (v.Components > MeshNative.UvChannels * 4)
            {
                unsupported("custom vertex streams past TEXCOORD7 (default streams drawn)");
                return null;
            }
            v.Texcoords = texcoords.ToArray();
            return v;
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
        /// The proportion of particles mirrored on each axis (renderer.flip).
        public Vector3 Flip;
        /// The mesh of mesh particles (m_Mesh).
        public Mesh Mesh;
        /// The custom vertex streams, or null for the default ones (Position, Normal, Color, UV:
        /// GetActiveVertexStreams of a new renderer, j0010421).
        public VertexStreams Streams;

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
            Flip = r.Flip;
            Mesh = r.Mesh;
            Streams = r.Streams;
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
            Flip = V3(f, "m_Flip");
            Mesh = f.Contains("m_Mesh") ? resolver.Resolve(f.PPtr("m_Mesh")) as Mesh : null;
            if (RenderMode is < 0 or > 5)
            {
                Unsupported($"render mode {RenderMode} (not drawn)");
                RenderMode = 5;
            }
            if (RenderMode == 4)
            {
                if (!Mesh) Unsupported("mesh particles without a mesh (not drawn)");
                foreach (var other in new[] { "m_Mesh1", "m_Mesh2", "m_Mesh3" })
                    if (f.Contains(other) && !f.PPtr(other).IsNull) Unsupported("mesh particles of several meshes (the first drawn)");
                if (Alignment is 3 or 4)
                {
                    Unsupported($"mesh particles aligned to {(Alignment == 3 ? "Facing" : "Velocity")} (world-aligned)");
                    Alignment = 1;
                }
                if (Flip.y != 0f || Flip.z != 0f) Unsupported("mesh particles flipped on y or z (x only)");
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
            if (CameraVelocityScale != 0f) Unsupported("camera velocity stretching");
            if (B(f, "m_FreeformStretching")) Unsupported("freeform stretching");
            Streams = null;
            if (B(f, "m_UseCustomVertexStreams"))
            {
                var list = new List<int>();
                switch (f["m_VertexStreams"])
                {
                    case byte[] bytes: foreach (var b in bytes) list.Add(b); break;
                    case object[] items: foreach (var item in items) list.Add(Convert.ToInt32(item)); break;
                }
                Streams = VertexStreams.Of(list, Unsupported);
                if (Streams != null && Streams.HasTangent && RenderMode == 1) Unsupported("tangents of stretched particles (along their texture's u)");
            }
        }

        ParticleSystemNative System
        {
            get
            {
                foreach (var c in GameObject.Components)
                    if (c is ParticleSystem && c.EngineState is ParticleSystemNative ps) return ps;
                return null;
            }
        }

        bool Stretched => RenderMode == 1;

        /// The mesh of mesh particles, readable, or null.
        MeshNative ParticleMesh => RenderMode == 4 && Mesh && Mesh.NativeOrThrow is MeshNative m && m.Positions != null && m.SubMeshes.Count > 0
            && m.SubMeshes[0].Topology == MeshTopology.Triangles ? m : null;

        /// A mesh particle's pivot offset at size 1, in the mesh's space: the pivot in units of
        /// the mesh's bounds, z turned toward -z (measured: UnityProbe particlestreams, j0010424,
        /// a mesh whose bounds start away from its origin).
        static Vector3 MeshPivot(Vector3 pivot, MeshNative mesh)
        {
            var size = mesh.Bounds.size;
            return new Vector3(pivot.x * size.x, pivot.y * size.y, -pivot.z * size.z);
        }

        /// Local-space bounds (Renderer.localBounds) of the particles' world AABB.
        protected override Bounds GeometryBounds
        {
            get
            {
                var ps = System;
                float meshRadius = 0f;
                if (ParticleMesh is { } mesh)
                {
                    var b = mesh.Bounds;
                    var pivot = MeshPivot(Pivot, mesh);
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new Vector3((i & 1) != 0 ? b.max.x : b.min.x, (i & 2) != 0 ? b.max.y : b.min.y, (i & 4) != 0 ? b.max.z : b.min.z);
                        meshRadius = Math.Max(meshRadius, (corner + pivot).magnitude);
                    }
                    meshRadius = Math.Max(meshRadius, 1e-6f);
                }
                if (ps is null || !ps.WorldBounds(Stretched ? LengthScale : 0f, Stretched ? VelocityScale : 0f, Pivot.magnitude, meshRadius, out var world)) return default;
                return Transform(world, GameObject.Transform.LocalToWorld.inverse);
            }
        }

        /// Geometry for a camera; see the overload taking a ParticleView.
        internal bool BuildGeometry(Camera camera, MeshNative target, out Matrix4x4 objectToWorld)
        {
            var t = camera.transform.TransformNative;
            var projection = CameraProjection != null ? CameraProjection(camera) : (Matrix4x4?)null;
            return BuildGeometry(new ParticleView(Hierarchy.Position(t), t.WorldRotation, projection), target, out objectToWorld);
        }

        /// Fills target with this frame's particle geometry as seen from view, for the frame
        /// renderer. Contract:
        /// - Returns false (target untouched) when there is nothing to draw: render mode None,
        ///   mesh particles without a readable mesh, no ParticleSystem, or no live particles or
        ///   trails.
        /// - Vertices are in world space and objectToWorld is identity (Unity builds particle
        ///   geometry in world space). Streams: positions, normals, colors (Color32-quantized,
        ///   ColorsAreBytes) and uv0 (2D); with custom vertex streams, the ones they name
        ///   (VertexStreams). Quads are clockwise as seen from the camera.
        /// - Submesh 0 holds the particles (drawn with material 0); when the trail module is
        ///   enabled submesh 1 holds the trail ribbons (material 1, Unity's trail material
        ///   slot). Particles are ordered for back-to-front blending per the sort mode.
        /// - The mesh is marked Dynamic and its Version bumped (Changed).
        internal bool BuildGeometry(in ParticleView view, MeshNative target, out Matrix4x4 objectToWorld)
        {
            objectToWorld = Matrix4x4.identity;
            var ps = System;
            if (ps is null || RenderMode == 5) return false;
            var mesh = ParticleMesh;
            if (RenderMode == 4 && mesh == null) return false;
            var particles = ps.Particles;
            bool trails = ps.Config.TrailsEnabled || ps.OrphanTrails.Count > 0;
            if (particles.Length == 0 && ps.OrphanTrails.Count == 0) return false;

            // The builder and the per-particle scratch are reused from system to system (main
            // thread: the frame renderer's culling); the mesh gets arrays of its own (Fill).
            var b = builder;
            b.Reset(Streams);
            var emitter = ps.CaptureEmitter();
            int n = particles.Length;
            var looks = Grow(ref b.Looks, n);
            for (int i = 0; i < n; i++) looks[i] = ps.Look(particles[i], emitter);
            var order = SortOrder(particles, looks, view, b);

            var quads = b.Quads;
            if (mesh != null)
            {
                var meshRotation = Alignment switch
                {
                    0 => view.Rotation,
                    2 => emitter.Rotation,
                    _ => Quaternion.identity,
                };
                foreach (int i in order) AddMeshParticle(b, quads, looks[i], mesh, meshRotation, ps);
            }
            else
                foreach (int i in order) AddParticle(b, quads, looks[i], view, ps);

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

        /// Whether a particle is mirrored on x and on y: each axis for that proportion of the
        /// particles, picked at random per particle (measured: UnityProbe particlestreams,
        /// j0010421: flip 0.5 mirrored some of eight particles, 1 all of them, 0 none).
        void Flips(uint seed, out bool x, out bool y)
        {
            x = Flip.x > 0f && ParticleRandom.Hash01(seed, Salt.FlipX) < Flip.x;
            y = Flip.y > 0f && ParticleRandom.Hash01(seed, Salt.FlipY) < Flip.y;
        }

        void AddParticle(GeometryBuilder b, List<int> indices, in ParticleSystemNative.ParticleLook p, in ParticleView view, ParticleSystemNative ps)
        {
            var size = Clamped(new Vector2(p.Size.x, p.Size.y), p.Position, view);
            var toCamera = view.IsPerspective ? (view.Position - p.Position).normalized : -view.Forward;
            Vector3 right, up, normal, center;
            // Corners in order: (-right, -up), (-right, +up), (+right, +up), (+right, -up); u runs
            // along right and v along up.
            var uv = p.Uv;
            var uv2 = p.Uv2;

            if (Stretched)
            {
                // Measured (UnityProbe particlestreams, j0010424): the quad trails the particle,
                // its head at the particle; its length is lengthScale × size plus velocityScale ×
                // speed, its width the particle size. u runs from the head (0) to the tail (1)
                // and v across, 1 on the side cross(toCamera, velocity); the rotation is not
                // applied. The pivot moves it by pivot.x widths across and pivot.y lengths along.
                float speed = p.Velocity.magnitude;
                var axis = speed > 0f ? p.Velocity / speed : view.Up;
                float length = size.x * LengthScale + speed * VelocityScale;
                var side = Vector3.Cross(toCamera, axis);
                side = side.sqrMagnitude > 0f ? side.normalized : view.Right;
                // right runs from the head to the tail (u), up across (v).
                right = -axis * (length * 0.5f);
                up = side * (size.y * 0.5f);
                center = p.Position + axis * (length * (Pivot.y - 0.5f)) + side * (size.y * Pivot.x);
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
                                var rotation = ((Transform)GameObject.Transform).WorldRotation;
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

            // A flipped particle mirrors its texture coordinates, not its corners (measured:
            // UnityProbe particlestreams, j0010421, billboards and stretched particles).
            if (Flip != Vector3.zero)
            {
                Flips(p.Seed, out bool flipX, out bool flipY);
                if (flipX)
                {
                    uv = new Rect(uv.xMax, uv.y, -uv.width, uv.height);
                    uv2 = new Rect(uv2.xMax, uv2.y, -uv2.width, uv2.height);
                }
                if (flipY)
                {
                    uv = new Rect(uv.x, uv.yMax, uv.width, -uv.height);
                    uv2 = new Rect(uv2.x, uv2.yMax, uv2.width, -uv2.height);
                }
            }

            // Normals bend from the face normal toward the corners as normalDirection drops
            // below 1 (spherical lighting).
            Vector3 CornerNormal(Vector3 offset) => Vector3.LerpUnclamped(offset.normalized, normal, NormalDirection).normalized;
            var c32 = (Color)(Color32)p.Color;
            int v = b.Count;
            if (b.Streams is { } streams)
            {
                // The tangent is the quad's right (its u axis unflipped), w -1 (measured:
                // j0010421, (1, 0, 0, -1) on a view-aligned billboard).
                var tangentAxis = right.sqrMagnitude > 0f ? right.normalized : view.Right;
                var tangent = new Vector4(tangentAxis.x, tangentAxis.y, tangentAxis.z, -1f);
                var values = ParticleValues.Of(streams, p, ps);
                b.AddCustom(center - right - up, CornerNormal(-right - up), tangent, c32, new Vector2(uv.xMin, uv.yMin), new Vector2(uv2.xMin, uv2.yMin), p, values);
                b.AddCustom(center - right + up, CornerNormal(-right + up), tangent, c32, new Vector2(uv.xMin, uv.yMax), new Vector2(uv2.xMin, uv2.yMax), p, values);
                b.AddCustom(center + right + up, CornerNormal(right + up), tangent, c32, new Vector2(uv.xMax, uv.yMax), new Vector2(uv2.xMax, uv2.yMax), p, values);
                b.AddCustom(center + right - up, CornerNormal(right - up), tangent, c32, new Vector2(uv.xMax, uv.yMin), new Vector2(uv2.xMax, uv2.yMin), p, values);
            }
            else
            {
                b.Add(center - right - up, CornerNormal(-right - up), c32, new Vector2(uv.xMin, uv.yMin));
                b.Add(center - right + up, CornerNormal(-right + up), c32, new Vector2(uv.xMin, uv.yMax));
                b.Add(center + right + up, CornerNormal(right + up), c32, new Vector2(uv.xMax, uv.yMax));
                b.Add(center + right - up, CornerNormal(right - up), c32, new Vector2(uv.xMax, uv.yMin));
            }
            indices.Add(v); indices.Add(v + 1); indices.Add(v + 2);
            indices.Add(v); indices.Add(v + 2); indices.Add(v + 3);
        }

        /// A mesh particle, as measured (UnityProbe particlestreams, j0010421, j0010424, j0010426):
        /// each mesh vertex scaled by the particle's 3D size, moved by the pivot (MeshPivot),
        /// turned by the particle's rotation (Quaternion.Euler of its 3D rotation; its z rotation
        /// alone without 3D rotation) and then by the alignment's (View: the camera's, Local: the
        /// emitter's, World: none), and placed at the particle. Normals and tangents turn with it
        /// (tangents keep w); a particle flipped on x mirrors the mesh's x. Colours are the mesh's
        /// times the particle's, or the particle's; UV is the mesh's uv, UV2 its second; channels
        /// the mesh lacks read 0. A texture sheet maps UV into the frame's tile and UV2 into the
        /// next frame's (j0010438).
        void AddMeshParticle(GeometryBuilder b, List<int> indices, in ParticleSystemNative.ParticleLook p, MeshNative mesh, Quaternion alignment, ParticleSystemNative ps)
        {
            var rotation = alignment * Quaternion.Euler(p.Rotation3 * Mathf.Rad2Deg);
            var size = p.Size;
            var pivot = MeshPivot(Pivot, mesh);
            bool flipX = false;
            if (Flip.x > 0f) Flips(p.Seed, out flipX, out _);
            var mirror = new Vector3(flipX ? -1f : 1f, 1f, 1f);
            var positions = mesh.Positions;
            var normals = mesh.Normals;
            var tangents = mesh.Tangents;
            // Colours given as bytes are read as bytes: reading Colors would keep a float copy
            // on the mesh.
            var bytes = mesh.Colors32;
            var colors = bytes == null ? mesh.Colors : null;
            var uvs = mesh.Uvs[0];
            var uvs2 = mesh.Uvs[1];
            var streams = b.Streams;
            var values = streams != null ? ParticleValues.Of(streams, p, ps) : default;
            int v = b.Count;
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var local = Vector3.Scale(Vector3.Scale(positions[i], mirror), size) + Vector3.Scale(pivot, size);
                var position = p.Position + rotation * local;
                var normal = normals != null ? rotation * Vector3.Scale(normals[i], mirror) : Vector3.zero;
                var color = (Color)(Color32)(bytes != null ? (Color)bytes[i] * p.Color : colors != null ? colors[i] * p.Color : p.Color);
                var uv = uvs != null ? new Vector2(p.Uv.x + uvs[i].x * p.Uv.width, p.Uv.y + uvs[i].y * p.Uv.height) : Vector2.zero;
                if (streams != null)
                {
                    var tangent = Vector4.zero;
                    if (tangents != null)
                    {
                        var t = rotation * Vector3.Scale(tangents[i], mirror);
                        tangent = new Vector4(t.x, t.y, t.z, tangents[i].w);
                    }
                    var uv2 = uvs2 != null ? new Vector2(p.Uv2.x + uvs2[i].x * p.Uv2.width, p.Uv2.y + uvs2[i].y * p.Uv2.height) : Vector2.zero;
                    b.AddCustom(position, normal, tangent, color, uv, uv2, p, values);
                }
                else b.Add(position, normal, color, uv);
            }
            var triangles = mesh.SubMeshes[0];
            foreach (int index in triangles.Indices) indices.Add(v + triangles.BaseVertex + index);
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

        /// A particle's values for the custom streams that are the same at each of its vertices.
        readonly struct ParticleValues
        {
            public readonly Vector4 StableRandom, Custom1, Custom2;

            ParticleValues(Vector4 stableRandom, Vector4 custom1, Vector4 custom2)
            {
                StableRandom = stableRandom;
                Custom1 = custom1;
                Custom2 = custom2;
            }

            /// Stable random numbers are the particle's own, fixed for its life, in [0, 1) (Unity's
            /// generator is not reproduced: its values are other numbers).
            public static ParticleValues Of(VertexStreams streams, in ParticleSystemNative.ParticleLook p, ParticleSystemNative ps)
            {
                var random = streams.NeedsStableRandom
                    ? new Vector4(ParticleRandom.Hash01(p.Seed, Salt.StableRandom), ParticleRandom.Hash01(p.Seed, Salt.StableRandom + 1),
                        ParticleRandom.Hash01(p.Seed, Salt.StableRandom + 2), ParticleRandom.Hash01(p.Seed, Salt.StableRandom + 3))
                    : default;
                return new ParticleValues(random,
                    streams.NeedsCustom1 ? ps.CustomData(0, p.Seed, p.AgeNormalized) : default,
                    streams.NeedsCustom2 ? ps.CustomData(1, p.Seed, p.AgeNormalized) : default);
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
            readonly List<Vector4> tangents = new();
            readonly List<Color32> colors = new();
            readonly List<Vector4> uvs = new();
            readonly List<Vector4>[] channels = new List<Vector4>[MeshNative.UvChannels];
            readonly float[] components = new float[MeshNative.UvChannels * 4];
            public readonly List<int> Quads = new(), Ribbons = new();
            public readonly List<Vector3> Points = new();
            public ParticleSystemNative.ParticleLook[] Looks = [];
            public int[] Order = [];
            public float[] Keys = [];
            public (float Key, int Index)[] IndexKeys = [];
            public float[] Along = [];
            /// The custom vertex streams of the system being built, or null.
            public VertexStreams Streams;

            public int Count => positions.Count;

            public void Reset(VertexStreams streams)
            {
                Streams = streams;
                positions.Clear();
                normals.Clear();
                tangents.Clear();
                colors.Clear();
                uvs.Clear();
                foreach (var c in channels) c?.Clear();
                Quads.Clear();
                Ribbons.Clear();
            }

            /// Colours are given quantized to bytes (Color)(Color32), and uploaded as the bytes
            /// they convert back to, as the device converted them before. With custom streams a
            /// vertex (a trail's) gets its uv in the UV stream and 0 in the others.
            public void Add(Vector3 position, Vector3 normal, Color color, Vector2 uv)
            {
                if (Streams != null)
                {
                    AddCustom(position, normal, Vector4.zero, color, uv, uv, default, default, onlyUv: true);
                    return;
                }
                positions.Add(position);
                normals.Add(normal);
                colors.Add(color);
                uvs.Add(new Vector4(uv.x, uv.y, 0f, 0f));
            }

            /// A vertex with the custom streams' values: uv and uv2 are the vertex's (UV, UV2), the
            /// rest the particle's (look) or its stream values.
            public void AddCustom(Vector3 position, Vector3 normal, Vector4 tangent, Color color, Vector2 uv, Vector2 uv2,
                in ParticleSystemNative.ParticleLook p, in ParticleValues values, bool onlyUv = false)
            {
                var s = Streams;
                positions.Add(position);
                if (s.HasNormal) normals.Add(normal);
                if (s.HasTangent) tangents.Add(tangent);
                if (s.HasColor) colors.Add(color);
                var c = components;
                Array.Clear(c, 0, s.Channels * 4);
                foreach (var (stream, offset, count) in s.Texcoords)
                {
                    if (onlyUv && stream != VertexStreams.UV) continue;
                    Vector4 value = stream switch
                    {
                        VertexStreams.UV => uv,
                        VertexStreams.UV2 => uv2,
                        VertexStreams.AnimBlend => new Vector4(p.Frame - MathF.Floor(p.Frame), 0f),
                        VertexStreams.AnimFrame => new Vector4(p.Frame, 0f),
                        VertexStreams.Center => p.Position,
                        VertexStreams.SizeX or VertexStreams.SizeXY or VertexStreams.SizeXYZ => p.Size,
                        VertexStreams.Velocity => p.Velocity,
                        VertexStreams.Speed => new Vector4(p.Velocity.magnitude, 0f),
                        VertexStreams.AgePercent => new Vector4(p.AgeNormalized, 0f),
                        VertexStreams.InvStartLifetime => new Vector4(p.Lifetime > 0f ? 1f / p.Lifetime : 0f, 0f),
                        >= VertexStreams.StableRandomX and <= VertexStreams.StableRandomXYZW => values.StableRandom,
                        >= VertexStreams.Custom1X and <= VertexStreams.Custom1XYZW => values.Custom1,
                        >= VertexStreams.Custom2X and <= VertexStreams.Custom2XYZW => values.Custom2,
                        _ => Vector4.zero,
                    };
                    for (int k = 0; k < count; k++) c[offset + k] = value[k];
                }
                for (int ch = 0; ch < s.Channels; ch++)
                    (channels[ch] ??= new()).Add(new Vector4(c[ch * 4], c[ch * 4 + 1], c[ch * 4 + 2], c[ch * 4 + 3]));
            }

            public void Fill(MeshNative mesh, bool ribbons)
            {
                mesh.Clear();
                int n = positions.Count;
                var s = Streams;
                mesh.VertexCount = n;
                mesh.Positions = Channel(mesh, positions, MeshNative.SlotPositions);
                mesh.Normals = s == null || s.HasNormal ? Channel(mesh, normals, MeshNative.SlotNormals) : null;
                if (s != null && s.HasTangent) mesh.Tangents = Channel(mesh, tangents, MeshNative.SlotTangents);
                var colors32 = s == null || s.HasColor ? Copy(mesh, colors) : null;
                mesh.SetColors32(colors32);
                if (colors32 != null) mesh.Give(MeshNative.SlotColors32, colors32);
                mesh.ColorsAreBytes = true;
                if (s == null)
                {
                    mesh.Uvs[0] = Channel(mesh, uvs, MeshNative.SlotUv0);
                    mesh.UvDimensions[0] = 2;
                }
                else
                    for (int ch = 0; ch < s.Channels; ch++)
                    {
                        mesh.Uvs[ch] = Channel(mesh, channels[ch], MeshNative.SlotUv0 + ch);
                        mesh.UvDimensions[ch] = s.Dimension(ch);
                    }
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
