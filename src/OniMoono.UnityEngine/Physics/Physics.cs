using System;
using OniMoono.Engine;

namespace UnityEngine
{
    public partial class Physics
    {
        /// The project's PhysicsManager gravity until a script sets it.
        public static Vector3 gravity
        {
            get => PhysicsWorld.Gravity;
            set => PhysicsWorld.Gravity = value;
        }

        /// Closest hit along the ray among enabled colliders of active GameObjects whose layer is
        /// in layerMask (triggers per Physics.queriesHitTriggers). A collider that contains the
        /// ray origin is not hit, and a zero-length direction hits nothing.
        public static bool Raycast(Ray ray, out RaycastHit hitInfo, float maxDistance, int layerMask)
        {
            Threads.AssertMainThread("Raycast");
            return PhysicsWorld.Raycast(ray.origin, ray.direction, maxDistance, layerMask, out hitInfo);
        }
    }

    public partial struct RaycastHit
    {
        internal Vector3 m_Point;
        internal Vector3 m_Normal;
        internal uint m_FaceID;
        internal float m_Distance;
        internal Vector2 m_UV;
        internal EntityId m_Collider;

        public Collider collider => PhysicsWorld.FindCollider(m_Collider) as Collider;

        public Vector3 point { get => m_Point; set => m_Point = value; }
        public Vector3 normal { get => m_Normal; set => m_Normal = value; }
        public float distance { get => m_Distance; set => m_Distance = value; }

        public Transform transform
        {
            get
            {
                Rigidbody body = rigidbody;
                if (body != null) return body.transform;
                if (collider != null) return collider.transform;
                return null;
            }
        }

        public Rigidbody rigidbody => collider != null ? collider.attachedRigidbody : null;
    }

    public partial class Collider
    {
        /// An enabled collider takes part in queries; disabling it sends no messages.
        public bool enabled
        {
            get => Native3D.Enabled;
            set => Native3D.Enabled = value;
        }

        /// The nearest Rigidbody on this GameObject or a parent. A collider that is disabled or on
        /// an inactive GameObject has no physics shape and so no body.
        public Rigidbody attachedRigidbody
        {
            get
            {
                var native = Native3D;
                if (!native.Enabled || native.GameObject == null || !native.GameObject.ActiveInHierarchy) return null;
                for (var t = native.GameObject.Transform; t != null; t = t.Parent)
                    foreach (var c in t.GameObject.Components)
                        if (c is Rigidbody body) return body;
                return null;
            }
        }

        internal ColliderNative Native3D => (ColliderNative)NativeOrThrow;
    }

    public partial class Rigidbody
    {
        /// No simulation steps bodies (the game creates none): the velocity changes only when a
        /// script sets it. Velocity is not serialized, so a loaded body starts at rest.
        public Vector3 linearVelocity
        {
            get => ((RigidbodyNative)NativeOrThrow).LinearVelocity;
            set => ((RigidbodyNative)NativeOrThrow).LinearVelocity = value;
        }
    }

    public partial class SphereCollider : Collider
    {
        public SphereCollider() { }
    }
}

namespace OniMoono.Engine
{
    using OniMoono.Assets.TypeTrees;
    using OniMoono.Engine.Assets;
    using OniMoono.Engine.Serialization;
    using UnityEngine;

    /// Engine-side state of a 3D collider. Runtime-added colliders start with Unity's default
    /// shape sizes; Unity additionally fits a collider added next to a renderer to the renderer's
    /// bounds, which the game never relies on (it adds no 3D colliders at runtime).
    internal abstract class ColliderNative : ComponentNative
    {
        public bool Enabled = true;
        public bool IsTrigger;

        protected ColliderNative() => PhysicsWorld.Register(this);

        public override void CopyStateFrom(NativeObject source)
        {
            var c = (ColliderNative)source;
            Enabled = c.Enabled;
            IsTrigger = c.IsTrigger;
        }

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            Enabled = data.Bool("m_Enabled");
            IsTrigger = data.Bool("m_IsTrigger");
            BindShape(data);
            Serialized = null;
        }

        protected abstract void BindShape(Fields data);

        /// Ray (unit direction) against the shape in world space. Hits from inside are not
        /// reported, as in Unity.
        public abstract bool Raycast(Vector3 origin, Vector3 direction, out float distance, out Vector3 normal);

        /// The shape's world AABB (Collider.bounds of an enabled, active collider).
        public abstract Bounds WorldBounds(TransformNative t);
    }

    internal sealed class SphereColliderNative : ColliderNative
    {
        public Vector3 Center;
        public float Radius = 0.5f;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var s = (SphereColliderNative)source;
            Center = s.Center;
            Radius = s.Radius;
        }

        protected override void BindShape(Fields data)
        {
            Radius = data.Float("m_Radius");
            Center = TransformBinder.ToVector3(data.Struct("m_Center"));
        }

        /// PhysX scales a sphere by the largest absolute component of the lossy scale.
        public override Bounds WorldBounds(TransformNative t)
        {
            var scale = Hierarchy.LossyScale(t);
            float r = Radius * Math.Max(Math.Abs(scale.x), Math.Max(Math.Abs(scale.y), Math.Abs(scale.z)));
            return new Bounds(t.LocalToWorld.MultiplyPoint3x4(Center), new Vector3(r, r, r) * 2f);
        }

        public override bool Raycast(Vector3 origin, Vector3 direction, out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = default;
            var t = GameObject.Transform;
            var scale = Hierarchy.LossyScale(t);
            float r = Math.Abs(Radius) * Math.Max(Math.Abs(scale.x), Math.Max(Math.Abs(scale.y), Math.Abs(scale.z)));
            var c = t.LocalToWorld.MultiplyPoint3x4(Center);
            var oc = origin - c;
            float cc = Vector3.Dot(oc, oc) - r * r;
            if (cc <= 0f) return false; // origin inside
            float b = Vector3.Dot(oc, direction);
            if (b > 0f) return false; // pointing away
            float disc = b * b - cc;
            if (disc < 0f) return false;
            distance = -b - MathF.Sqrt(disc);
            var p = origin + direction * distance;
            normal = r > 0f ? (p - c) / r : -direction;
            return true;
        }
    }

    internal sealed class BoxColliderNative : ColliderNative
    {
        public Vector3 Center;
        public Vector3 Size = Vector3.one;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var s = (BoxColliderNative)source;
            Center = s.Center;
            Size = s.Size;
        }

        protected override void BindShape(Fields data)
        {
            Size = TransformBinder.ToVector3(data.Struct("m_Size"));
            Center = TransformBinder.ToVector3(data.Struct("m_Center"));
        }

        public override Bounds WorldBounds(TransformNative t)
        {
            var scale = Hierarchy.LossyScale(t);
            var half = new Vector3(Math.Abs(Size.x * scale.x), Math.Abs(Size.y * scale.y), Math.Abs(Size.z * scale.z)) * 0.5f;
            return ShapeBounds.Box(t.LocalToWorld.MultiplyPoint3x4(Center), t.WorldRotation, half);
        }

        /// An oriented box: the transform's world rotation, half extents scaled by the absolute
        /// lossy scale (PhysX boxes do not shear). Slab test in the box's frame.
        public override bool Raycast(Vector3 origin, Vector3 direction, out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = default;
            var t = GameObject.Transform;
            var scale = Hierarchy.LossyScale(t);
            var half = new Vector3(Math.Abs(Size.x * scale.x), Math.Abs(Size.y * scale.y), Math.Abs(Size.z * scale.z)) * 0.5f;
            var rotation = t.WorldRotation;
            var inverse = Quaternion.Inverse(rotation);
            var c = t.LocalToWorld.MultiplyPoint3x4(Center);
            var o = inverse * (origin - c);
            var d = inverse * direction;

            float tMin = float.NegativeInfinity, tMax = float.PositiveInfinity;
            int axis = -1;
            float sign = 0f;
            for (int i = 0; i < 3; i++)
            {
                float oi = o[i], di = d[i], hi = half[i];
                if (Math.Abs(di) < 1e-12f)
                {
                    if (oi < -hi || oi > hi) return false;
                    continue;
                }
                float t1 = (-hi - oi) / di, t2 = (hi - oi) / di;
                float near = Math.Min(t1, t2), far = Math.Max(t1, t2);
                if (near > tMin)
                {
                    tMin = near;
                    axis = i;
                    sign = di > 0f ? -1f : 1f;
                }
                if (far < tMax) tMax = far;
                if (tMin > tMax) return false;
            }
            if (axis < 0 || tMin <= 0f) return false; // origin inside, or box behind the ray
            distance = tMin;
            var local = Vector3.zero;
            local[axis] = sign;
            normal = rotation * local;
            return true;
        }
    }

    internal sealed class RigidbodyNative : ComponentNative
    {
        public Vector3 LinearVelocity;

        public override void Bind(Fields data, IReferenceResolver resolver) => Serialized = null;
    }

    /// The default physics scene: project settings and the colliders queries run against.
    internal static class PhysicsWorld
    {
        public static Vector3 Gravity = new(0f, -9.81f, 0f);
        public static bool QueriesHitTriggers = true;

        static readonly System.Collections.Generic.List<ComponentNative> colliders = new();

        /// From the project's PhysicsManager.
        public static void Configure(Fields manager)
        {
            Gravity = TransformBinder.ToVector3(manager.Struct("m_Gravity"));
            QueriesHitTriggers = manager.Bool("m_QueriesHitTriggers");
        }

        /// 3D and 2D colliders, so hit structs can find their collider by instance id. Destroyed
        /// ones are dropped on the next query.
        public static void Register(ComponentNative collider)
        {
            lock (colliders) colliders.Add(collider);
        }

        static ComponentNative[] Snapshot()
        {
            lock (colliders)
            {
                colliders.RemoveAll(c => c.Destroyed);
                return colliders.ToArray();
            }
        }

        public static UnityEngine.Object FindCollider(EntityId id)
        {
            if (id == EntityId.None) return null;
            foreach (var c in Snapshot())
                if (c.InstanceId == id && c.Managed != null) return c.Managed;
            return null;
        }

        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, int layerMask, out RaycastHit hit)
        {
            hit = default;
            float length = direction.magnitude;
            if (!(length > float.Epsilon)) return false;
            direction /= length;
            bool found = false;
            foreach (var native in Snapshot())
            {
                if (native is not ColliderNative c || !c.Enabled || c.Managed == null) continue;
                var go = c.GameObject;
                if (go == null || !go.ActiveInHierarchy || (layerMask & (1 << go.Layer)) == 0) continue;
                if (c.IsTrigger && !QueriesHitTriggers) continue;
                if (!c.Raycast(origin, direction, out float distance, out var normal)) continue;
                if (distance > maxDistance || (found && distance >= hit.m_Distance)) continue;
                found = true;
                hit = new RaycastHit
                {
                    m_Point = origin + direction * distance,
                    m_Normal = normal,
                    m_FaceID = uint.MaxValue, // not a mesh: triangleIndex -1
                    m_Distance = distance,
                    m_Collider = EntityId.From(c.InstanceId),
                };
            }
            return found;
        }
    }
}
