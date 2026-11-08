using UnityEngine;

namespace OniMoono.Engine.Rendering
{
    /// The six clip planes of a view-projection matrix (Gribb-Hartmann; normals point inwards),
    /// held in the struct itself: a camera makes one for every cull.
    internal readonly struct Frustum
    {
        readonly Vector4 p0, p1, p2, p3, p4, p5;

        public Frustum(Matrix4x4 m)
        {
            Vector4 Row(int i) => new(m[i, 0], m[i, 1], m[i, 2], m[i, 3]);
            var r0 = Row(0);
            var r1 = Row(1);
            var r2 = Row(2);
            var r3 = Row(3);
            p0 = r3 + r0;
            p1 = r3 - r0;
            p2 = r3 + r1;
            p3 = r3 - r1;
            p4 = r3 + r2;
            p5 = r3 - r2;
        }

        /// False only when the box lies entirely outside one plane (tested in the order above).
        public bool Intersects(Bounds b)
        {
            var c = b.center;
            var e = b.extents;
            return !Outside(p0, c, e) && !Outside(p1, c, e) && !Outside(p2, c, e) && !Outside(p3, c, e) && !Outside(p4, c, e) && !Outside(p5, c, e);
        }

        static bool Outside(in Vector4 p, in Vector3 c, in Vector3 e)
        {
            float distance = p.x * c.x + p.y * c.y + p.z * c.z + p.w;
            float radius = Mathf.Abs(p.x) * e.x + Mathf.Abs(p.y) * e.y + Mathf.Abs(p.z) * e.z;
            return distance + radius < 0f;
        }
    }
}
