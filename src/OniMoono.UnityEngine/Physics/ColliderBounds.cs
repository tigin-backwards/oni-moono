using System;
using OniMoono.Engine;

// Collider bounds as measured in the original (UnityProbe's apigaps "bounds"): the world AABB of
// the shape under the transform (PhysX and Box2D shapes do not shear: a box keeps its own axes,
// a sphere's or circle's radius scales by the largest scale), and for a disabled collider or one
// on an inactive object an empty box at the transform's position. 2D shapes lie in the plane of
// the transform's z and are flat.
namespace UnityEngine
{
    public partial class Collider
    {
        public Bounds bounds
        {
            get
            {
                var n = Native3D;
                var t = n.GameObject.Transform;
                if (!n.Enabled || !n.GameObject.ActiveInHierarchy) return new Bounds(Hierarchy.Position(t), Vector3.zero);
                return n.WorldBounds(t);
            }
        }
    }

    public partial class BoxCollider
    {
        public Vector3 center
        {
            get => ((BoxColliderNative)NativeOrThrow).Center;
            set => ((BoxColliderNative)NativeOrThrow).Center = value;
        }

        public Vector3 size
        {
            get => ((BoxColliderNative)NativeOrThrow).Size;
            set => ((BoxColliderNative)NativeOrThrow).Size = value;
        }
    }

    public partial class SphereCollider
    {
        public Vector3 center
        {
            get => ((SphereColliderNative)NativeOrThrow).Center;
            set => ((SphereColliderNative)NativeOrThrow).Center = value;
        }

        public float radius
        {
            get => ((SphereColliderNative)NativeOrThrow).Radius;
            set => ((SphereColliderNative)NativeOrThrow).Radius = value;
        }
    }

    public partial class Collider2D
    {
        public Bounds bounds
        {
            get
            {
                var n = (Collider2DNative)NativeOrThrow;
                var t = n.GameObject.Transform;
                if (!n.Enabled || !n.GameObject.ActiveInHierarchy) return new Bounds(Hierarchy.Position(t), Vector3.zero);
                var b = n.WorldBounds(t.LocalToWorld);
                var c = b.center;
                c.z = Hierarchy.Position(t).z;
                var e = b.extents;
                e.z = 0f;
                return new Bounds { center = c, extents = e };
            }
        }
    }
}

namespace OniMoono.Engine
{
    using UnityEngine;

    internal static class ShapeBounds
    {
        /// The AABB of points under m (z of 2D points 0).
        public static Bounds Of(Matrix4x4 m, ReadOnlySpan<Vector3> points)
        {
            if (points.Length == 0) return new Bounds(m.MultiplyPoint3x4(Vector3.zero), Vector3.zero);
            Vector3 min = m.MultiplyPoint3x4(points[0]), max = min;
            for (int i = 1; i < points.Length; i++)
            {
                var p = m.MultiplyPoint3x4(points[i]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        /// An oriented box (centre and half extents in the shape's frame, already scaled) under
        /// the rotation, at a world centre.
        public static Bounds Box(Vector3 worldCenter, Quaternion rotation, Vector3 half)
        {
            var r = Matrix4x4.Rotate(rotation);
            var e = new Vector3(
                Math.Abs(r.m00) * half.x + Math.Abs(r.m01) * half.y + Math.Abs(r.m02) * half.z,
                Math.Abs(r.m10) * half.x + Math.Abs(r.m11) * half.y + Math.Abs(r.m12) * half.z,
                Math.Abs(r.m20) * half.x + Math.Abs(r.m21) * half.y + Math.Abs(r.m22) * half.z);
            return new Bounds(worldCenter, e * 2f);
        }

        public static float MaxAbs(float a, float b) => Math.Max(Math.Abs(a), Math.Abs(b));
    }
}
