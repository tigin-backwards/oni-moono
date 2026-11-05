using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Engine;

namespace UnityEngine
{
    public partial struct RaycastHit2D
    {
        private Vector2 m_Centroid;
        private Vector2 m_Point;
        private Vector2 m_Normal;
        private float m_Distance;
        private float m_Fraction;
        private EntityId m_Collider;

        public Vector2 centroid { get => m_Centroid; set => m_Centroid = value; }
        public Vector2 point { get => m_Point; set => m_Point = value; }
        public Vector2 normal { get => m_Normal; set => m_Normal = value; }
        public float distance { get => m_Distance; set => m_Distance = value; }
        public float fraction { get => m_Fraction; set => m_Fraction = value; }

        public Collider2D collider => PhysicsWorld.FindCollider(m_Collider) as Collider2D;

        /// A hit converts to true when it hit a collider.
        public static implicit operator bool(RaycastHit2D hit) => hit.collider != null;

        /// Hits without a collider sort last; otherwise by fraction along the cast.
        public int CompareTo(RaycastHit2D other)
        {
            if (collider == null) return 1;
            if (other.collider == null) return -1;
            return fraction.CompareTo(other.fraction);
        }
    }

    public partial class Physics2D
    {
        /// Which pairs of layers ignore each other's contacts (the layer collision matrix). Nothing
        /// simulates 2D contacts here, so it is only kept, as GetIgnoreLayerCollision reports it.
        static readonly uint[] ignoredLayers = new uint[32];

        public static void IgnoreLayerCollision(int layer1, int layer2, bool ignore)
        {
            CheckLayer(layer1, nameof(layer1));
            CheckLayer(layer2, nameof(layer2));
            if (ignore)
            {
                ignoredLayers[layer1] |= 1u << layer2;
                ignoredLayers[layer2] |= 1u << layer1;
            }
            else
            {
                ignoredLayers[layer1] &= ~(1u << layer2);
                ignoredLayers[layer2] &= ~(1u << layer1);
            }
        }

        public static void IgnoreLayerCollision(int layer1, int layer2) => IgnoreLayerCollision(layer1, layer2, true);

        public static bool GetIgnoreLayerCollision(int layer1, int layer2)
        {
            CheckLayer(layer1, nameof(layer1));
            CheckLayer(layer2, nameof(layer2));
            return (ignoredLayers[layer1] & (1u << layer2)) != 0;
        }

        static void CheckLayer(int layer, string name)
        {
            if (layer < 0 || layer > 31) throw new ArgumentOutOfRangeException(name, $"{name} is out of range. Layer numbers must be in the range 0 to 31.");
        }
    }

    public partial class Collider2D
    {
        /// Stored; nothing simulates 2D contacts, so no trigger messages are sent.
        public bool isTrigger
        {
            get => ((Collider2DNative)NativeOrThrow).IsTrigger;
            set => ((Collider2DNative)NativeOrThrow).IsTrigger = value;
        }

        /// The shape's offset from the transform in local space (stored; nothing simulates 2D physics).
        public Vector2 offset
        {
            get => ((Collider2DNative)NativeOrThrow).Offset;
            set => ((Collider2DNative)NativeOrThrow).Offset = value;
        }
    }

    public sealed partial class PolygonCollider2D
    {
        PolygonCollider2DNative Data => (PolygonCollider2DNative)NativeOrThrow;

        /// Setting fewer paths drops the last ones; more adds empty ones.
        public int pathCount
        {
            get => Data.Paths.Count;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Path count must be zero or positive.");
                var paths = Data.Paths;
                if (value < paths.Count) paths.RemoveRange(value, paths.Count - value);
                while (paths.Count < value) paths.Add(Array.Empty<Vector2>());
            }
        }

        public Vector2[] GetPath(int index)
        {
            if (index < 0 || index >= Data.Paths.Count) throw new ArgumentOutOfRangeException(nameof(index), $"Path {index} does not exist.");
            return (Vector2[])Data.Paths[index].Clone();
        }

        /// Sets a path, adding paths up to index as needed.
        public void SetPath(int index, Vector2[] points)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "Negative path index is invalid.");
            if (points == null) throw new ArgumentNullException(nameof(points));
            var paths = Data.Paths;
            while (paths.Count <= index) paths.Add(Array.Empty<Vector2>());
            paths[index] = (Vector2[])points.Clone();
        }
    }

    public sealed partial class BoxCollider2D
    {
        public Vector2 size
        {
            get => ((BoxCollider2DNative)NativeOrThrow).Size;
            set => ((BoxCollider2DNative)NativeOrThrow).Size = value;
        }
    }

    public sealed partial class CircleCollider2D
    {
        public float radius
        {
            get => ((CircleCollider2DNative)NativeOrThrow).Radius;
            set => ((CircleCollider2DNative)NativeOrThrow).Radius = value;
        }
    }

    public sealed partial class Rigidbody2D
    {
        /// No simulation steps bodies (the game creates none): the velocity changes only when a
        /// script sets it. Velocity is not serialized, so a loaded body starts at rest.
        public Vector2 linearVelocity
        {
            get => ((Rigidbody2DNative)NativeOrThrow).LinearVelocity;
            set => ((Rigidbody2DNative)NativeOrThrow).LinearVelocity = value;
        }

        // The body's settings, kept as given (Unity's defaults until a script sets them). No
        // simulation steps bodies, so none of them moves one.
        Rigidbody2DNative Body => (Rigidbody2DNative)NativeOrThrow;
        public RigidbodyType2D bodyType { get => Body.BodyType; set => Body.BodyType = value; }
        public bool simulated { get => Body.Simulated; set => Body.Simulated = value; }
        public bool useAutoMass { get => Body.UseAutoMass; set => Body.UseAutoMass = value; }
        public float mass { get => Body.Mass; set => Body.Mass = value; }
        public float linearDamping { get => Body.LinearDamping; set => Body.LinearDamping = value; }
        public float angularDamping { get => Body.AngularDamping; set => Body.AngularDamping = value; }
        public float gravityScale { get => Body.GravityScale; set => Body.GravityScale = value; }
        public bool freezeRotation { get => Body.FreezeRotation; set => Body.FreezeRotation = value; }
        public CollisionDetectionMode2D collisionDetectionMode { get => Body.CollisionDetection; set => Body.CollisionDetection = value; }
        public RigidbodyInterpolation2D interpolation { get => Body.Interpolation; set => Body.Interpolation = value; }
    }
}

namespace OniMoono.Engine
{
    using OniMoono.Assets.TypeTrees;
    using OniMoono.Engine.Assets;
    using OniMoono.Engine.Serialization;
    using UnityEngine;

    /// Engine-side state shared by 2D colliders. Nothing simulates or queries 2D physics, so the
    /// shapes are only stored.
    internal abstract class Collider2DNative : BehaviourNative
    {
        public bool IsTrigger;
        public Vector2 Offset;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var s = (Collider2DNative)source;
            IsTrigger = s.IsTrigger;
            Offset = s.Offset;
        }

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            IsTrigger = data.Bool("m_IsTrigger");
            Offset = TransformBinder.ToVector2(data.Struct("m_Offset"));
            Serialized = null;
        }

        /// The shape's AABB under the transform (x and y; Collider2D.bounds sets z).
        public abstract Bounds WorldBounds(Matrix4x4 localToWorld);

        protected static Bounds PointBounds(Matrix4x4 m, Vector2[] points, Vector2 offset, float radius)
        {
            var p = new Vector3[points.Length];
            for (int i = 0; i < p.Length; i++) p[i] = points[i] + offset;
            var b = ShapeBounds.Of(m, p);
            if (radius > 0f) b.Expand(new Vector3(radius, radius, 0f) * 2f);
            return b;
        }
    }

    internal sealed class BoxCollider2DNative : Collider2DNative
    {
        public Vector2 Size = Vector2.one;
        public float EdgeRadius;

        public BoxCollider2DNative() => PhysicsWorld.Register(this);

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var s = (BoxCollider2DNative)source;
            Size = s.Size;
            EdgeRadius = s.EdgeRadius;
        }

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            base.Bind(data, resolver);
            Size = TransformBinder.ToVector2(data.Struct("m_Size"));
            EdgeRadius = data.Float("m_EdgeRadius");
        }

        /// The box's centre under the transform, and its half size through the absolute
        /// rotation and scale (plus the edge radius).
        public override Bounds WorldBounds(Matrix4x4 m)
        {
            float hx = Size.x * 0.5f, hy = Size.y * 0.5f;
            var e = new Vector3(Math.Abs(m.m00) * hx + Math.Abs(m.m01) * hy + EdgeRadius, Math.Abs(m.m10) * hx + Math.Abs(m.m11) * hy + EdgeRadius, 0f);
            return new Bounds(m.MultiplyPoint3x4(Offset), e * 2f);
        }
    }

    /// A PolygonCollider2D's paths (closed outlines in local space).
    internal sealed class PolygonCollider2DNative : Collider2DNative
    {
        /// Unity's default outline, a pentagon of radius 1 (as the original's GetPath(0) gives it
        /// for one added at runtime, UnityProbe's apigaps "bounds").
        public List<Vector2[]> Paths = new()
        {
            new Vector2[] { new(0f, 1f), new(-0.95105654f, 0.309016973f), new(-0.5877852f, -0.809017062f), new(0.587785363f, -0.809016943f), new(0.9510565f, 0.309017122f) },
        };

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            Paths = ((PolygonCollider2DNative)source).Paths.Select(p => (Vector2[])p.Clone()).ToList();
        }

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            base.Bind(data, resolver);
            Paths = new List<Vector2[]>();
            foreach (var path in data.Struct("m_Points").Array("m_Paths"))
                Paths.Add(((object[])path).Cast<Fields>().Select(TransformBinder.ToVector2).ToArray());
        }

        public override Bounds WorldBounds(Matrix4x4 m) => PointBounds(m, Paths.SelectMany(p => p).ToArray(), Offset, 0f);
    }

    /// An EdgeCollider2D's open line (points in local space) and its radius.
    internal sealed class EdgeCollider2DNative : Collider2DNative
    {
        /// Unity's default line: (-0.5, 0) to (0.5, 0).
        public Vector2[] Points = [new(-0.5f, 0f), new(0.5f, 0f)];
        public float EdgeRadius;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var s = (EdgeCollider2DNative)source;
            Points = (Vector2[])s.Points.Clone();
            EdgeRadius = s.EdgeRadius;
        }

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            base.Bind(data, resolver);
            Points = data.Array("m_Points").Cast<Fields>().Select(TransformBinder.ToVector2).ToArray();
            EdgeRadius = data.Float("m_EdgeRadius");
        }

        public override Bounds WorldBounds(Matrix4x4 m) => PointBounds(m, Points, Offset, EdgeRadius);
    }

    internal sealed class Rigidbody2DNative : ComponentNative
    {
        public Vector2 LinearVelocity;
        public RigidbodyType2D BodyType = RigidbodyType2D.Dynamic;
        public bool Simulated = true;
        public bool UseAutoMass;
        public float Mass = 1f;
        public float LinearDamping;
        public float AngularDamping = 0.05f;
        public float GravityScale = 1f;
        public bool FreezeRotation;
        public CollisionDetectionMode2D CollisionDetection = CollisionDetectionMode2D.Discrete;
        public RigidbodyInterpolation2D Interpolation = RigidbodyInterpolation2D.None;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var s = (Rigidbody2DNative)source;
            BodyType = s.BodyType; Simulated = s.Simulated; UseAutoMass = s.UseAutoMass; Mass = s.Mass;
            LinearDamping = s.LinearDamping; AngularDamping = s.AngularDamping; GravityScale = s.GravityScale;
            FreezeRotation = s.FreezeRotation; CollisionDetection = s.CollisionDetection; Interpolation = s.Interpolation;
        }

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            if (data.Contains("m_BodyType")) BodyType = (RigidbodyType2D)data.Int("m_BodyType");
            if (data.Contains("m_Simulated")) Simulated = data.Bool("m_Simulated");
            if (data.Contains("m_UseAutoMass")) UseAutoMass = data.Bool("m_UseAutoMass");
            if (data.Contains("m_Mass")) Mass = data.Float("m_Mass");
            if (data.Contains("m_GravityScale")) GravityScale = data.Float("m_GravityScale");
            if (data.Contains("m_Interpolate")) Interpolation = (RigidbodyInterpolation2D)data.Int("m_Interpolate");
            if (data.Contains("m_CollisionDetection")) CollisionDetection = (CollisionDetectionMode2D)data.Int("m_CollisionDetection");
            Serialized = null;
        }
    }

    /// A CircleCollider2D's radius in local space.
    internal sealed class CircleCollider2DNative : Collider2DNative
    {
        public float Radius = 0.5f;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            Radius = ((CircleCollider2DNative)source).Radius;
        }

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            base.Bind(data, resolver);
            Radius = data.Float("m_Radius");
        }

        /// The radius scales by the larger of the x and y scales.
        public override Bounds WorldBounds(Matrix4x4 m)
        {
            float sx = new Vector3(m.m00, m.m10, m.m20).magnitude, sy = new Vector3(m.m01, m.m11, m.m21).magnitude;
            float r = Radius * Math.Max(sx, sy);
            return new Bounds(m.MultiplyPoint3x4(Offset), new Vector3(r, r, 0f) * 2f);
        }
    }
}
