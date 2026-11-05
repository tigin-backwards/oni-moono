using System;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;

namespace UnityEngine
{
    public partial class CapsuleCollider
    {
        CapsuleColliderNative Data => (CapsuleColliderNative)NativeOrThrow;

        public Vector3 center { get => Data.Center; set => Data.Center = value; }
        public float radius { get => Data.Radius; set => Data.Radius = value; }
        public float height { get => Data.Height; set => Data.Height = value; }

        /// The axis of the capsule's height: 0 = X, 1 = Y, 2 = Z.
        public int direction { get => Data.Direction; set => Data.Direction = Math.Clamp(value, 0, 2); }
    }

    public partial class MeshCollider
    {
        MeshColliderNative Data => (MeshColliderNative)NativeOrThrow;

        public Mesh sharedMesh { get => Data.Mesh; set => Data.Mesh = value; }
        public bool convex { get => Data.Convex; set => Data.Convex = value; }
    }

    public partial class GameObject
    {
        /// A GameObject with Unity's built-in mesh for the primitive, the default material and
        /// the matching collider (Plane and Quad get a MeshCollider; Cylinder a capsule).
        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            string name = type.ToString();
            var go = new GameObject(name);
            var mesh = Resources.GetBuiltinResource<Mesh>(name + ".fbx");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = BuiltinAssets.DefaultMaterial;
            switch (type)
            {
                case PrimitiveType.Sphere:
                    go.AddComponent<SphereCollider>();
                    break;
                case PrimitiveType.Capsule:
                case PrimitiveType.Cylinder:
                    go.AddComponent<CapsuleCollider>();
                    break;
                case PrimitiveType.Cube:
                    go.AddComponent<BoxCollider>();
                    break;
                default:
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                    break;
            }
            return go;
        }
    }
}

namespace OniMoono.Engine
{
    using UnityEngine;

    internal static class BuiltinAssets
    {
        static Material defaultMaterial;

        /// "Default-Material" of the built-in extra resources; null when the build stripped it
        /// (unreferenced built-ins are not included in a player).
        public static Material DefaultMaterial
        {
            get
            {
                if (defaultMaterial != null) return defaultMaterial;
                var file = AssetSystem.Get("unity_builtin_extra");
                foreach (var info in file.Objects)
                {
                    if (file.TypeOf(info).ClassId != 21) continue;
                    if (ObjectLoader.Load(file, info.PathId) is Material m && m.name == "Default-Material") return defaultMaterial = m;
                }
                return null;
            }
        }
    }

    /// Capsule: a segment along `Direction` of length height - 2·radius, swept by the radius.
    internal sealed class CapsuleColliderNative : ColliderNative
    {
        public Vector3 Center;
        public float Radius = 0.5f;
        public float Height = 2f;
        public int Direction = 1;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var s = (CapsuleColliderNative)source;
            Center = s.Center;
            Radius = s.Radius;
            Height = s.Height;
            Direction = s.Direction;
        }

        protected override void BindShape(Fields data)
        {
            Center = TransformBinder.ToVector3(data.Struct("m_Center"));
            Radius = data.Float("m_Radius");
            Height = data.Float("m_Height");
            Direction = data.Int("m_Direction");
        }

        /// PhysX scales the radius by the larger of the two cross-axis scales and the height by
        /// the axis scale.
        /// The capsule's segment along its axis (height and radius scaled as PhysX does: the
        /// height by the axis's scale, the radius by the larger of the other two).
        public override Bounds WorldBounds(TransformNative t)
        {
            var scale = Hierarchy.LossyScale(t);
            var abs = new Vector3(Math.Abs(scale.x), Math.Abs(scale.y), Math.Abs(scale.z));
            float axisScale = abs[Direction], r = Radius * Math.Max(abs[(Direction + 1) % 3], abs[(Direction + 2) % 3]);
            float halfSegment = Math.Max(0f, Height * axisScale * 0.5f - r);
            var axis = Vector3.zero;
            axis[Direction] = 1f;
            var a = t.WorldRotation * axis * halfSegment;
            var e = new Vector3(Math.Abs(a.x) + r, Math.Abs(a.y) + r, Math.Abs(a.z) + r);
            return new Bounds(t.LocalToWorld.MultiplyPoint3x4(Center), e * 2f);
        }

        public override bool Raycast(Vector3 origin, Vector3 direction, out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = default;
            var t = GameObject.Transform;
            var s = Hierarchy.LossyScale(t);
            var abs = new Vector3(Math.Abs(s.x), Math.Abs(s.y), Math.Abs(s.z));
            float axisScale = abs[Direction];
            float radius = Radius * Math.Max(abs[(Direction + 1) % 3], abs[(Direction + 2) % 3]);
            float half = Math.Max(0f, Height * axisScale * 0.5f - radius);
            var axis = Vector3.zero;
            axis[Direction] = 1f;
            var worldAxis = (t.WorldRotation * axis).normalized;
            var c = t.LocalToWorld.MultiplyPoint3x4(Center);
            var a = c - worldAxis * half;
            var b = c + worldAxis * half;
            if (Inside(origin, a, b, radius)) return false;
            float best = float.PositiveInfinity;
            // Cylinder side.
            var ab = b - a;
            float abLen = ab.magnitude;
            if (abLen > 0f)
            {
                var n = ab / abLen;
                var ao = origin - a;
                var dPerp = direction - n * Vector3.Dot(direction, n);
                var oPerp = ao - n * Vector3.Dot(ao, n);
                float qa = Vector3.Dot(dPerp, dPerp), qb = 2f * Vector3.Dot(dPerp, oPerp), qc = Vector3.Dot(oPerp, oPerp) - radius * radius;
                float disc = qb * qb - 4f * qa * qc;
                if (qa > 1e-12f && disc >= 0f)
                {
                    float hit = (-qb - MathF.Sqrt(disc)) / (2f * qa);
                    float along = Vector3.Dot(origin + direction * hit - a, n);
                    if (hit > 0f && along >= 0f && along <= abLen && hit < best)
                    {
                        best = hit;
                        var p = origin + direction * hit;
                        normal = (p - (a + n * along)).normalized;
                    }
                }
            }
            // End spheres.
            foreach (var centre in new[] { a, b })
            {
                var oc = origin - centre;
                float qb = Vector3.Dot(oc, direction), qc = Vector3.Dot(oc, oc) - radius * radius;
                float disc = qb * qb - qc;
                if (disc < 0f) continue;
                float hit = -qb - MathF.Sqrt(disc);
                if (hit > 0f && hit < best)
                {
                    best = hit;
                    normal = (origin + direction * hit - centre).normalized;
                }
            }
            if (float.IsPositiveInfinity(best)) return false;
            distance = best;
            return true;
        }

        static bool Inside(Vector3 p, Vector3 a, Vector3 b, float r)
        {
            var ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 0f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2) : 0f;
            return (p - (a + ab * t)).sqrMagnitude < r * r;
        }
    }

    /// Triangle mesh: queries hit front faces only (PhysX's default, as Physics.queriesHitBackfaces
    /// is off). Convex meshes are not used by the game; they query as their triangles.
    internal sealed class MeshColliderNative : ColliderNative
    {
        public Mesh Mesh;
        public bool Convex;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var s = (MeshColliderNative)source;
            Mesh = s.Mesh;
            Convex = s.Convex;
        }

        protected override void BindShape(Fields data) => Convex = data.Bool("m_Convex");

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            Mesh = resolver.Resolve(data.PPtr("m_Mesh")) as Mesh;
            base.Bind(data, resolver);
        }

        /// Möller–Trumbore against each triangle in world space; front faces wind clockwise.
        /// The AABB of the mesh's vertices under the transform.
        public override Bounds WorldBounds(TransformNative t)
        {
            var vertices = Mesh != null ? Mesh.vertices : Array.Empty<Vector3>();
            if (vertices.Length == 0) return new Bounds(Hierarchy.Position(t), Vector3.zero);
            return ShapeBounds.Of(t.LocalToWorld, vertices);
        }

        public override bool Raycast(Vector3 origin, Vector3 direction, out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = default;
            if (Mesh == null) return false;
            var m = (MeshNative)Mesh.NativeOrThrow;
            if (m.Positions == null) return false;
            var world = GameObject.Transform.LocalToWorld;
            float best = float.PositiveInfinity;
            foreach (var sub in m.SubMeshes)
            {
                if (sub.Topology != MeshTopology.Triangles) continue;
                var idx = sub.Indices;
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    var p0 = world.MultiplyPoint3x4(m.Positions[idx[i] + sub.BaseVertex]);
                    var p1 = world.MultiplyPoint3x4(m.Positions[idx[i + 1] + sub.BaseVertex]);
                    var p2 = world.MultiplyPoint3x4(m.Positions[idx[i + 2] + sub.BaseVertex]);
                    var e1 = p1 - p0;
                    var e2 = p2 - p0;
                    // A clockwise (front) triangle's outward normal in Unity's left-handed space.
                    var faceNormal = Vector3.Cross(e1, e2);
                    if (Vector3.Dot(faceNormal, direction) >= 0f) continue;
                    var pv = Vector3.Cross(direction, e2);
                    float det = Vector3.Dot(e1, pv);
                    if (Math.Abs(det) < 1e-12f) continue;
                    float inv = 1f / det;
                    var tv = origin - p0;
                    float u = Vector3.Dot(tv, pv) * inv;
                    if (u < 0f || u > 1f) continue;
                    var qv = Vector3.Cross(tv, e1);
                    float v = Vector3.Dot(direction, qv) * inv;
                    if (v < 0f || u + v > 1f) continue;
                    float hit = Vector3.Dot(e2, qv) * inv;
                    if (hit > 0f && hit < best)
                    {
                        best = hit;
                        normal = faceNormal.normalized;
                    }
                }
            }
            if (float.IsPositiveInfinity(best)) return false;
            distance = best;
            return true;
        }
    }
}
