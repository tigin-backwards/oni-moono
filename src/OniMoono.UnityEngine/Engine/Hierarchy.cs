using System;
using UnityEngine;

namespace OniMoono.Engine
{
    /// Transform parenting and sibling order. Main thread only.
    internal static class Hierarchy
    {
        /// Subscribers notified after a transform's parent changed (canvas hierarchy tracking).
        public static event Action<TransformNative> ParentChanged;

        /// Records a change to what lies at or below a transform (children, activation, components,
        /// enabled state, canvas and group settings) on it and every ancestor. CanvasSystem skips
        /// a subtree hidden through its CanvasGroups when nothing in it changed since its last walk.
        public static void Touch(TransformNative t)
        {
            Touches++;
            if (t != null) t.GameObject.SelfVersion++;
            for (; t != null; t = t.Parent) t.GameObject.SubtreeVersion++;
        }

        /// Touch calls so far: answers kept from the hierarchy stand while it is unchanged.
        public static long Touches;

        public static void RemoveChild(TransformNative parent, TransformNative child)
        {
            int index = parent.Children.IndexOf(child);
            if (index < 0) return;
            parent.RemoveChildAt(index);
            child.Parent = null;
            Touch(parent);
            ChildrenRestructured(parent, child);
        }

        /// An active UI object leaving or joining a parent (or moving among its siblings)
        /// renumbers the canvas's depths.
        static void ChildrenRestructured(TransformNative parent, TransformNative child)
        {
            if (child is RectTransformNative && child.GameObject.ActiveInHierarchy) UI.CanvasSystem.Restructured(parent);
        }

        static void InsertChild(TransformNative parent, TransformNative child, int index)
        {
            parent.InsertChildAt(child, Math.Clamp(index, 0, parent.Children.Length));
            child.Parent = parent;
            Touch(parent);
            ChildrenRestructured(parent, child);
        }

        public static bool IsAncestorOrSelf(TransformNative ancestor, TransformNative t)
        {
            for (var p = t; p != null; p = p.Parent)
                if (p == ancestor) return true;
            return false;
        }

        public static void SetParent(TransformNative t, TransformNative newParent, bool worldPositionStays)
        {
            if (t.Parent == newParent) return;
            // A transform made its own parent is left as it is, without a message: Unity logs
            // nothing (Momentary Switch's side screen does this twice, spec
            // tests/e2e/mods/2624509351-momentary-switch.yaml).
            if (newParent == t) return;
            var go = t.GameObject;
            if (newParent != null && IsAncestorOrSelf(t, newParent))
            {
                Debug.LogError("Cannot set the parent of the GameObject '" + go.Name + "' to itself or one of its children.", go.Managed);
                return;
            }
            if (newParent != null && newParent.GameObject.IsPersistent != go.IsPersistent)
            {
                Debug.LogError("Setting the parent of a transform which resides in a Prefab Asset is disabled to prevent data corruption (GameObject: '" + go.Name + "').", go.Managed);
                return;
            }

            Vector3 worldPos = default, worldScale = default;
            Quaternion worldRot = default;
            if (worldPositionStays)
            {
                worldPos = Position(t);
                worldRot = t.WorldRotation;
                worldScale = LossyScale(t);
            }

            Messages.SendToSubtree(go, s => s.OnBeforeTransformParentChanged);

            var oldParent = t.Parent;
            if (oldParent != null) RemoveChild(oldParent, t);
            else if (go.Scene != null) Scenes.RemoveRoot(go.Scene, go);

            if (newParent != null)
            {
                InsertChild(newParent, t, newParent.Children.Length);
                if (newParent.GameObject.Scene != go.Scene) Scenes.SetSceneRecursive(go, newParent.GameObject.Scene);
            }
            else if (go.Scene != null)
            {
                Scenes.AddRoot(go.Scene, go);
            }

            if (worldPositionStays)
            {
                if (newParent == null)
                {
                    t.LocalPosition = worldPos;
                    t.LocalRotation = worldRot;
                    t.LocalScale = worldScale;
                }
                else
                {
                    t.LocalPosition = newParent.LocalToWorld.inverse.MultiplyPoint3x4(worldPos);
                    t.LocalRotation = Quaternion.Inverse(newParent.WorldRotation) * worldRot;
                    var parentScale = LossyScale(newParent);
                    t.LocalScale = new Vector3(Divide(worldScale.x, parentScale.x), Divide(worldScale.y, parentScale.y), Divide(worldScale.z, parentScale.z));
                }
            }
            t.ParentChangedInternal(worldPositionStays);
            t.MarkDirty();

            Messages.SendToSubtree(go, s => s.OnTransformParentChanged);
            if (oldParent != null) Messages.SendToComponents(oldParent.GameObject, s => s.OnTransformChildrenChanged);
            if (newParent != null) Messages.SendToComponents(newParent.GameObject, s => s.OnTransformChildrenChanged);
            ParentChanged?.Invoke(t);
            Lifecycle.UpdateActiveInHierarchy(go);
        }

        static float Divide(float a, float b) => b == 0f ? 0f : a / b;

        public static Vector3 Position(TransformNative t)
        {
            // A root's world translation is its local position (Matrix4x4.TRS copies it in): no
            // matrix needed, which a moved object would otherwise rebuild. Most world objects in
            // the game are roots.
            if (t.Parent == null) return t.LocalPosition;
            var m = t.LocalToWorld;
            return new Vector3(m.m03, m.m13, m.m23);
        }

        /// A world point in t's local space as the original player's InverseTransformPoint and
        /// position setter compute it, when no level of t's chain is rotated: from the root down,
        /// each level's ((p - localPosition) + 0) * InverseScale.Of(localScale), per axis in
        /// float. The + 0 stands for the identity inverse rotation, which turns a -0 difference
        /// into +0 before the scale. tools/UnityProbe `invscale` IS7: 4,000 chains of 1 to 8
        /// levels, the setter and InverseTransformPoint bit for bit, signs of zeros included.
        /// False when a level is rotated: that form is not known (IS8), and the callers keep
        /// their matrix products.
        public static bool TryInverseTransformPoint(TransformNative t, Vector3 point, out Vector3 local)
        {
            local = point;
            return InverseUnrotated(t, ref local);
        }

        static bool InverseUnrotated(TransformNative t, ref Vector3 p)
        {
            if (t.Parent != null && !InverseUnrotated(t.Parent, ref p)) return false;
            var q = t.LocalRotation;
            if (q.x != 0f || q.y != 0f || q.z != 0f || q.w != 1f) return false;
            var lp = t.LocalPosition;
            var s = t.LocalScale;
            // Scale (1, 1, 1), most transforms', needs no inverse: its own local inverse goes
            // stale with every position set, and a parent's is read for each set of a child.
            var f = s.x == 1f && s.y == 1f && s.z == 1f ? new Vector3(InverseOfOne, InverseOfOne, InverseOfOne) : t.UnrotatedInverseScale();
            p = new Vector3(PlusZero(p.x - lp.x) * f.x, PlusZero(p.y - lp.y) * f.y, PlusZero(p.z - lp.z) * f.z);
            return true;
        }

        static readonly float InverseOfOne = InverseScale.Of(1f);

        /// x + 0 in IEEE arithmetic: -0 becomes +0, everything else is unchanged. Not inlined
        /// away: the JIT keeps a float add of 0 (it is not an identity for -0).
        static float PlusZero(float x) => x + 0f;

        /// World scale with the world rotation removed (exact without skew).
        public static Vector3 LossyScale(TransformNative t) => t.LossyScale;

        /// Root objects report 0: Unity's player does not expose an order of scene roots (the
        /// editor's root order); observed with tools/UnityProbe, including after SetAsFirstSibling.
        public static int GetSiblingIndex(TransformNative t) => t.Parent != null ? t.Parent.Children.IndexOf(t) : 0;

        public static void SetSiblingIndex(TransformNative t, int index)
        {
            if (t.Parent != null)
            {
                var parent = t.Parent;
                int current = parent.Children.IndexOf(t);
                index = Math.Clamp(index, 0, parent.Children.Length - 1);
                if (current == index) return;
                RemoveChild(parent, t);
                InsertChild(parent, t, index);
                t.SiblingOrderChangedInternal();
                Messages.SendToComponents(parent.GameObject, s => s.OnTransformChildrenChanged);
                return;
            }
            var scene = t.GameObject.Scene;
            if (scene == null) return;
            int at = scene.Roots.IndexOf(t.GameObject);
            if (at < 0) return;
            index = Math.Clamp(index, 0, scene.Roots.Count - 1);
            if (at == index) return;
            scene.Roots.Remove(t.GameObject);
            scene.Roots.Insert(index, t.GameObject);
        }
    }

    /// Engine messages sent to the MonoBehaviours of a GameObject or subtree.
    internal static class Messages
    {
        /// Script messages reach only GameObjects that are active in the hierarchy.
        public static void SendToComponents(GameObjectNative go, Func<ScriptInfo, Action<MonoBehaviour>> select)
        {
            if (!go.ActiveInHierarchy) return;
            foreach (var c in go.Components)
            {
                if (c is not MonoBehaviour mb || c.Native is not BehaviourNative bn || bn.Script == null) continue;
                var m = select(bn.Script);
                if (m != null) Lifecycle.Invoke(m, mb);
            }
        }

        public static void SendToSubtree(GameObjectNative go, Func<ScriptInfo, Action<MonoBehaviour>> select)
        {
            SendToComponents(go, select);
            foreach (var child in go.Transform.Children) SendToSubtree(child.GameObject, select);
        }
    }
}
