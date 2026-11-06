using System;
using System.Collections;
using OniMoono.Engine;

namespace UnityEngine
{
    public partial class Transform
    {
        protected Transform() { }

        internal TransformNative TransformNative => (TransformNative)NativeOrThrow;

        static Transform Wrap(TransformNative t) => t == null ? null : (Transform)t.GameObject.Components[0];

        // ---- Local and world TRS ----

        public Vector3 localPosition
        {
            get => TransformNative.LocalPosition;
            set
            {
                var t = TransformNative;
                t.SetLocalPositionInternal(value);
            }
        }

        public Quaternion localRotation
        {
            get => TransformNative.LocalRotation;
            set
            {
                var t = TransformNative;
                var rotation = HierarchyMath.Normalize(value);
                bool moved = rotation != t.LocalRotation;
                t.LocalRotation = rotation;
                t.MarkDirty(moved);
            }
        }

        public Vector3 localScale
        {
            get => TransformNative.LocalScale;
            set
            {
                var t = TransformNative;
                bool moved = value != t.LocalScale;
                t.LocalScale = value;
                t.MarkDirty(moved);
            }
        }

        public Vector3 position
        {
            get => Hierarchy.Position(TransformNative);
            set
            {
                var t = TransformNative;
                if (t.Parent == null) t.SetLocalPositionInternal(value);
                else if (Hierarchy.TryInverseTransformPoint(t.Parent, value, out var local)) t.SetLocalPositionInternal(local);
                else t.SetLocalPositionInternal(t.Parent.LocalToWorld.inverse.MultiplyPoint3x4(value));
            }
        }

        public Quaternion rotation
        {
            get => TransformNative.WorldRotation;
            set
            {
                var t = TransformNative;
                var rotation = Quaternion.Normalize(t.Parent == null ? value : Quaternion.Inverse(t.Parent.WorldRotation) * value);
                bool moved = rotation != t.LocalRotation;
                t.LocalRotation = rotation;
                t.MarkDirty(moved);
            }
        }

        public Vector3 eulerAngles
        {
            get => rotation.eulerAngles;
            set => rotation = Quaternion.Euler(value);
        }

        public Vector3 localEulerAngles
        {
            get => localRotation.eulerAngles;
            set => localRotation = Quaternion.Euler(value);
        }

        public Vector3 lossyScale => Hierarchy.LossyScale(TransformNative);

        public Vector3 right
        {
            get => rotation * Vector3.right;
            set => rotation = Quaternion.FromToRotation(Vector3.right, value);
        }

        public Vector3 up
        {
            get => rotation * Vector3.up;
            set => rotation = Quaternion.FromToRotation(Vector3.up, value);
        }

        public Vector3 forward
        {
            get => rotation * Vector3.forward;
            set => rotation = Quaternion.LookRotation(value);
        }

        public Matrix4x4 localToWorldMatrix => TransformNative.LocalToWorld;
        public Matrix4x4 worldToLocalMatrix => TransformNative.WorldToLocal;

        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }

        public void SetLocalPositionAndRotation(Vector3 localPosition, Quaternion localRotation)
        {
            this.localPosition = localPosition;
            this.localRotation = localRotation;
        }

        public void GetPositionAndRotation(out Vector3 position, out Quaternion rotation)
        {
            position = this.position;
            rotation = this.rotation;
        }

        public void GetLocalPositionAndRotation(out Vector3 localPosition, out Quaternion localRotation)
        {
            localPosition = this.localPosition;
            localRotation = this.localRotation;
        }

        public bool hasChanged
        {
            get => TransformNative.HasChanged;
            set => TransformNative.HasChanged = value;
        }

        // ---- Space conversions ----

        public Vector3 TransformPoint(Vector3 position) => TransformNative.LocalToWorld.MultiplyPoint3x4(position);
        public Vector3 TransformPoint(float x, float y, float z) => TransformPoint(new Vector3(x, y, z));
        /// Without rotations in the chain, the original's per-level form (Hierarchy.
        /// TryInverseTransformPoint); with them, the worldToLocalMatrix product.
        public Vector3 InverseTransformPoint(Vector3 position)
            => Hierarchy.TryInverseTransformPoint(TransformNative, position, out var local) ? local : TransformNative.WorldToLocal.MultiplyPoint3x4(position);
        public Vector3 InverseTransformPoint(float x, float y, float z) => InverseTransformPoint(new Vector3(x, y, z));

        /// Rotation only; scale does not apply to directions.
        public Vector3 TransformDirection(Vector3 direction) => rotation * direction;
        public Vector3 TransformDirection(float x, float y, float z) => TransformDirection(new Vector3(x, y, z));
        public Vector3 InverseTransformDirection(Vector3 direction) => Quaternion.Inverse(rotation) * direction;
        public Vector3 InverseTransformDirection(float x, float y, float z) => InverseTransformDirection(new Vector3(x, y, z));

        /// Rotation and scale, no translation.
        public Vector3 TransformVector(Vector3 vector) => TransformNative.LocalToWorld.MultiplyVector(vector);
        public Vector3 TransformVector(float x, float y, float z) => TransformVector(new Vector3(x, y, z));
        public Vector3 InverseTransformVector(Vector3 vector) => TransformNative.WorldToLocal.MultiplyVector(vector);
        public Vector3 InverseTransformVector(float x, float y, float z) => InverseTransformVector(new Vector3(x, y, z));

        // ---- Movement ----

        public void Translate(Vector3 translation) => Translate(translation, Space.Self);

        public void Translate(Vector3 translation, Space relativeTo)
            => position += relativeTo == Space.World ? translation : TransformDirection(translation);

        public void Translate(float x, float y, float z) => Translate(new Vector3(x, y, z), Space.Self);
        public void Translate(float x, float y, float z, Space relativeTo) => Translate(new Vector3(x, y, z), relativeTo);

        public void Translate(Vector3 translation, Transform relativeTo)
            => position += relativeTo != null ? relativeTo.TransformDirection(translation) : translation;

        public void Rotate(Vector3 eulers) => Rotate(eulers, Space.Self);

        public void Rotate(Vector3 eulers, Space relativeTo)
        {
            var eulerRot = Quaternion.Euler(eulers.x, eulers.y, eulers.z);
            if (relativeTo == Space.Self) localRotation = localRotation * eulerRot;
            else rotation = eulerRot * rotation;
        }

        public void Rotate(float xAngle, float yAngle, float zAngle) => Rotate(new Vector3(xAngle, yAngle, zAngle), Space.Self);
        public void Rotate(float xAngle, float yAngle, float zAngle, Space relativeTo) => Rotate(new Vector3(xAngle, yAngle, zAngle), relativeTo);

        public void Rotate(Vector3 axis, float angle) => Rotate(axis, angle, Space.Self);

        public void Rotate(Vector3 axis, float angle, Space relativeTo)
        {
            if (relativeTo == Space.Self) RotateAroundWorld(TransformDirection(axis), angle);
            else RotateAroundWorld(axis, angle);
        }

        void RotateAroundWorld(Vector3 worldAxis, float angleDegrees)
            => rotation = Quaternion.AngleAxis(angleDegrees, worldAxis) * rotation;

        public void RotateAround(Vector3 point, Vector3 axis, float angle)
        {
            var q = Quaternion.AngleAxis(angle, axis);
            position = point + q * (position - point);
            RotateAroundWorld(axis, angle);
        }

        public void LookAt(Transform target) => LookAt(target, Vector3.up);

        public void LookAt(Transform target, Vector3 worldUp)
        {
            if (target != null) LookAt(target.position, worldUp);
        }

        public void LookAt(Vector3 worldPosition) => LookAt(worldPosition, Vector3.up);

        public void LookAt(Vector3 worldPosition, Vector3 worldUp)
        {
            var forwardDir = worldPosition - position;
            if (forwardDir.sqrMagnitude < Vector3.kEpsilon * Vector3.kEpsilon) return;
            rotation = Quaternion.LookRotation(forwardDir, worldUp);
        }

        // ---- Hierarchy ----

        public Transform parent
        {
            get => Wrap(TransformNative.Parent);
            set => SetParent(value, true);
        }

        public void SetParent(Transform p) => SetParent(p, true);

        public void SetParent(Transform parent, bool worldPositionStays)
        {
            Threads.AssertMainThread("SetParent");
            Hierarchy.SetParent(TransformNative, parent == null ? null : parent.TransformNative, worldPositionStays);
        }

        public Transform root
        {
            get
            {
                var t = TransformNative;
                while (t.Parent != null) t = t.Parent;
                return Wrap(t);
            }
        }

        public int childCount => TransformNative.Children.Length;

        public Transform GetChild(int index)
        {
            var children = TransformNative.Children;
            if ((uint)index >= (uint)children.Length) throw new UnityException("Transform child out of bounds");
            return Wrap(children[index]);
        }

        public void DetachChildren()
        {
            foreach (var child in TransformNative.Children) Hierarchy.SetParent(child, null, true);
        }

        public bool IsChildOf(Transform parent)
            => parent != null && Hierarchy.IsAncestorOrSelf(parent.TransformNative, TransformNative);

        /// Finds a descendant by '/'-separated path of child names. As measured in the original
        /// (tools/UnityProbe): an empty path is the transform itself, one trailing '/' is ignored,
        /// and any other empty name ("/a", "a//b", "/") finds nothing, even under a child named "".
        public Transform Find(string n)
        {
            if (n == null) throw new ArgumentNullException("Name cannot be null");
            if (n.Length == 0) return this;
            var parts = (n[^1] == '/' ? n[..^1] : n).Split('/');
            if (Array.IndexOf(parts, "") >= 0) return null;
            return Wrap(Finder.FindPath(TransformNative, parts, 0));
        }

        public int GetSiblingIndex() => Hierarchy.GetSiblingIndex(TransformNative);

        public void SetSiblingIndex(int index)
        {
            Threads.AssertMainThread("SetSiblingIndex");
            Hierarchy.SetSiblingIndex(TransformNative, index);
        }

        public void SetAsFirstSibling() => SetSiblingIndex(0);
        public void SetAsLastSibling() => SetSiblingIndex(int.MaxValue);

        public int hierarchyCount
        {
            get
            {
                var t = TransformNative;
                while (t.Parent != null) t = t.Parent;
                return Count(t);
                static int Count(TransformNative x)
                {
                    int n = 1;
                    foreach (var c in x.Children) n += Count(c);
                    return n;
                }
            }
        }

        /// Enumerates the children by index, re-reading the child list at each step.
        public IEnumerator GetEnumerator() => new Enumerator(this);

        sealed class Enumerator(Transform outer) : IEnumerator
        {
            int currentIndex = -1;
            public object Current => outer.GetChild(currentIndex);
            public bool MoveNext() => ++currentIndex < outer.childCount;
            public void Reset() => currentIndex = -1;
        }
    }
}

namespace OniMoono.Engine
{
    internal partial class TransformNative
    {
        /// Sets the local position (RectTransforms also update their anchored position).
        public virtual void SetLocalPositionInternal(UnityEngine.Vector3 value)
        {
            bool moved = value != LocalPosition;
            LocalPosition = value;
            MarkDirty(moved);
        }
    }
}
