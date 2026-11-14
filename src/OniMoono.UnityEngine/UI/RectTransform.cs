using System;
using OniMoono.Engine;

namespace UnityEngine
{
    public partial class RectTransform
    {
        public RectTransform() { }

        internal RectTransformNative Rect => (RectTransformNative)NativeOrThrow;

        public Rect rect => Rect.ComputeRect();

        public Vector2 anchorMin
        {
            get => Rect.AnchorMin;
            set { var r = Rect; if (r.AnchorMin == value) return; r.AnchorMin = value; r.Resolve(); }
        }

        public Vector2 anchorMax
        {
            get => Rect.AnchorMax;
            set { var r = Rect; if (r.AnchorMax == value) return; r.AnchorMax = value; r.Resolve(); }
        }

        public Vector2 anchoredPosition
        {
            get => Rect.AnchoredPosition;
            set { var r = Rect; if (r.AnchoredPosition == value) return; r.AnchoredPosition = value; r.Resolve(); }
        }

        public Vector2 sizeDelta
        {
            get => Rect.SizeDelta;
            set { var r = Rect; if (r.SizeDelta == value) return; r.SizeDelta = value; r.Resolve(); }
        }

        public Vector2 pivot
        {
            get => Rect.Pivot;
            set { var r = Rect; if (r.Pivot == value) return; r.Pivot = value; r.Resolve(); }
        }

        public Vector3 anchoredPosition3D
        {
            get
            {
                var r = Rect;
                return new Vector3(r.AnchoredPosition.x, r.AnchoredPosition.y, r.LocalPosition.z);
            }
            set
            {
                var r = Rect;
                r.AnchoredPosition = new Vector2(value.x, value.y);
                r.LocalPosition.z = value.z;
                r.Resolve();
            }
        }

        public Vector2 offsetMin
        {
            get => anchoredPosition - Vector2.Scale(sizeDelta, pivot);
            set
            {
                Vector2 offset = value - offsetMin;
                sizeDelta -= offset;
                anchoredPosition += Vector2.Scale(offset, Vector2.one - pivot);
            }
        }

        public Vector2 offsetMax
        {
            get => anchoredPosition + Vector2.Scale(sizeDelta, Vector2.one - pivot);
            set
            {
                Vector2 offset = value - offsetMax;
                sizeDelta += offset;
                anchoredPosition += Vector2.Scale(offset, pivot);
            }
        }

        public Object drivenByObject
        {
            get => Rect.DrivenByObject;
            set => Rect.DrivenByObject = value;
        }

        internal DrivenTransformProperties drivenProperties
        {
            get => Rect.DrivenProperties;
            set => Rect.DrivenProperties = value;
        }

        public bool sendChildDimensionsChange
        {
            get => Rect.SendChildDimensionsChange;
            set => Rect.SendChildDimensionsChange = value;
        }

        public static event ReapplyDrivenProperties reapplyDrivenProperties;

        internal static void SendReapplyDrivenProperties(RectTransform driven) => reapplyDrivenProperties?.Invoke(driven);

        public void ForceUpdateRectTransforms() => Rect.Resolve();

        public void GetLocalCorners(Vector3[] fourCornersArray)
        {
            if (fourCornersArray == null || fourCornersArray.Length < 4)
            {
                Debug.LogError("Calling GetLocalCorners with an array that is null or has less than 4 elements.");
                return;
            }
            var r = rect;
            fourCornersArray[0] = new Vector3(r.x, r.y, 0f);
            fourCornersArray[1] = new Vector3(r.x, r.yMax, 0f);
            fourCornersArray[2] = new Vector3(r.xMax, r.yMax, 0f);
            fourCornersArray[3] = new Vector3(r.xMax, r.y, 0f);
        }

        public void GetWorldCorners(Vector3[] fourCornersArray)
        {
            if (fourCornersArray == null || fourCornersArray.Length < 4)
            {
                Debug.LogError("Calling GetWorldCorners with an array that is null or has less than 4 elements.");
                return;
            }
            GetLocalCorners(fourCornersArray);
            var m = localToWorldMatrix;
            for (int i = 0; i < 4; i++) fourCornersArray[i] = m.MultiplyPoint(fourCornersArray[i]);
        }

        public void SetSizeWithCurrentAnchors(Axis axis, float size)
        {
            var r = Rect;
            int i = (int)axis;
            var sizeDelta = r.SizeDelta;
            sizeDelta[i] = size - r.ParentSize()[i] * (r.AnchorMax[i] - r.AnchorMin[i]);
            r.SizeDelta = sizeDelta;
            r.Resolve();
        }

        /// Pins the rect to one edge of its parent: both anchors move to that edge on its axis
        /// (0 for Left and Bottom, 1 for Right and Top), the size on that axis becomes size, and
        /// the near side sits inset from the edge whatever the pivot. The other axis is kept.
        public void SetInsetAndSizeFromParentEdge(Edge edge, float inset, float size)
        {
            var r = Rect;
            int axis = edge == Edge.Top || edge == Edge.Bottom ? 1 : 0;
            bool farEdge = edge == Edge.Right || edge == Edge.Top;
            float anchor = farEdge ? 1f : 0f;
            float pivotOnAxis = r.Pivot[axis];
            var anchorMin = r.AnchorMin;
            var anchorMax = r.AnchorMax;
            var sizeDelta = r.SizeDelta;
            var anchoredPosition = r.AnchoredPosition;
            anchorMin[axis] = anchor;
            anchorMax[axis] = anchor;
            // With both anchors on the edge the anchor span is empty, so sizeDelta is the size.
            sizeDelta[axis] = size;
            anchoredPosition[axis] = farEdge ? -inset - size * (1f - pivotOnAxis) : inset + size * pivotOnAxis;
            r.AnchorMin = anchorMin;
            r.AnchorMax = anchorMax;
            r.SizeDelta = sizeDelta;
            r.AnchoredPosition = anchoredPosition;
            r.Resolve();
        }

        internal Rect GetRectInParentSpace()
        {
            var r = rect;
            var lp = localPosition;
            return new Rect(r.x + lp.x, r.y + lp.y, r.width, r.height);
        }
    }
}

namespace OniMoono.Engine
{
    /// RectTransform state. anchoredPosition and sizeDelta are authoritative; localPosition.xy
    /// is resolved from them and the parent rect, and re-resolved whenever the parent rect changes.
    internal sealed class RectTransformNative : TransformNative
    {
        UnityEngine.Vector2 anchorMin = new(0.5f, 0.5f), anchorMax = new(0.5f, 0.5f), sizeDelta = new(100f, 100f), pivot = new(0.5f, 0.5f);

        // The rect (ComputeRect) reads these, the parent's and the parents' above: each write of
        // one, and each change of any transform's parent (TransformNative.Parent), starts a new
        // rect epoch, and a rect computed in the current epoch is returned as it was computed.
        // A write of the value already there (the same bits: a layout group placing its children
        // where they are) changes no rect and keeps the epoch.
        public UnityEngine.Vector2 AnchorMin { get => anchorMin; set { if (!SameBits(anchorMin, value)) { anchorMin = value; RectEpoch++; } } }
        public UnityEngine.Vector2 AnchorMax { get => anchorMax; set { if (!SameBits(anchorMax, value)) { anchorMax = value; RectEpoch++; } } }
        public UnityEngine.Vector2 SizeDelta { get => sizeDelta; set { if (!SameBits(sizeDelta, value)) { sizeDelta = value; RectEpoch++; } } }
        public UnityEngine.Vector2 Pivot { get => pivot; set { if (!SameBits(pivot, value)) { pivot = value; RectEpoch++; } } }

        static bool SameBits(UnityEngine.Vector2 a, UnityEngine.Vector2 b) =>
            System.BitConverter.SingleToInt32Bits(a.x) == System.BitConverter.SingleToInt32Bits(b.x)
            && System.BitConverter.SingleToInt32Bits(a.y) == System.BitConverter.SingleToInt32Bits(b.y);
        public UnityEngine.Vector2 AnchoredPosition;

        /// The rect epoch: any write that can change any rect takes a new one (main thread only).
        internal static long RectEpoch = 1;
        UnityEngine.Rect epochRect;
        long epochRectAt;
        public UnityEngine.Object DrivenByObject;
        public UnityEngine.DrivenTransformProperties DrivenProperties;
        public bool SendChildDimensionsChange = true;

        UnityEngine.Rect lastRect;
        bool hasLastRect;

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            if (source is not RectTransformNative r) return;
            AnchorMin = r.AnchorMin;
            AnchorMax = r.AnchorMax;
            AnchoredPosition = r.AnchoredPosition;
            SizeDelta = r.SizeDelta;
            Pivot = r.Pivot;
            SendChildDimensionsChange = r.SendChildDimensionsChange;
        }

        /// Size of the parent's rect; zero when the parent is not a RectTransform.
        public UnityEngine.Vector2 ParentSize() => Parent is RectTransformNative p ? p.ComputeRect().size : UnityEngine.Vector2.zero;

        UnityEngine.Rect ParentRect() => Parent is RectTransformNative p ? p.ComputeRect() : default;

        /// The rect from the parent's size, the anchors, sizeDelta and the pivot: computed once in
        /// a rect epoch (a mask's pass reads the rect of every graphic it clips each frame, and a
        /// rect's computation walks every parent up to the canvas: about 2 ms a frame on a
        /// table screen with 50 rows), the same value as computed afresh.
        public UnityEngine.Rect ComputeRect()
        {
            if (epochRectAt == RectEpoch)
            {
                if (UI.UiCacheVerify.On) UI.UiCacheVerify.Rect(this, epochRect, RectFrom(FreshParentSize()));
                return epochRect;
            }
            epochRect = RectFrom(ParentSize());
            epochRectAt = RectEpoch;
            return epochRect;
        }

        UnityEngine.Rect RectFrom(UnityEngine.Vector2 parentSize)
        {
            var size = UnityEngine.Vector2.Scale(parentSize, anchorMax - anchorMin) + sizeDelta;
            return new UnityEngine.Rect(-pivot.x * size.x, -pivot.y * size.y, size.x, size.y);
        }

        /// The parent's size computed afresh up the whole chain, for the verify mode.
        UnityEngine.Vector2 FreshParentSize() => Parent is RectTransformNative p ? p.RectFrom(p.FreshParentSize()).size : UnityEngine.Vector2.zero;

        /// The anchor reference point in the parent's local space: the anchors, interpolated by the
        /// pivot when they are apart.
        UnityEngine.Vector2 AnchorReference()
        {
            var pr = ParentRect();
            return new UnityEngine.Vector2(
                pr.x + pr.width * (AnchorMin.x + (AnchorMax.x - AnchorMin.x) * Pivot.x),
                pr.y + pr.height * (AnchorMin.y + (AnchorMax.y - AnchorMin.y) * Pivot.y));
        }

        /// Recomputes localPosition and the rect, then propagates to child RectTransforms.
        public void Resolve()
        {
            var reference = AnchorReference();
            var newPos = new UnityEngine.Vector3(reference.x + AnchoredPosition.x, reference.y + AnchoredPosition.y, LocalPosition.z);
            bool moved = !newPos.Equals(LocalPosition);
            LocalPosition = newPos;
            var r = ComputeRect();
            bool rectChanged = !hasLastRect || !r.Equals(lastRect);
            lastRect = r;
            hasLastRect = true;
            if (moved || rectChanged) MarkDirty(moved);
            if (!rectChanged) return;
            foreach (var child in Children)
                if (child is RectTransformNative rc) rc.Resolve();
            SendDimensionsChange();
        }

        /// A move of a rect with a CanvasRenderer is told to it after the canvas update when its
        /// canvas is pixel perfect (CanvasSystem.NoteMoved).
        protected override void OnMoved() => UI.CanvasSystem.NoteMoved(this);

        /// OnRectTransformDimensionsChange to the object, and OnChildRectTransformDimensionsChange
        /// to its parent RectTransform's components when sendChildDimensionsChange is set (layout
        /// groups re-lay out their children on it).
        public void SendDimensionsChange()
        {
            Messages.SendToComponents(GameObject, s => s.OnRectTransformDimensionsChange);
            if (Parent is RectTransformNative && SendChildDimensionsChange)
                Messages.SendToComponents(Parent.GameObject, s => s.OnChildRectTransformDimensionsChange);
        }

        public override void SetLocalPositionInternal(UnityEngine.Vector3 value)
        {
            var reference = AnchorReference();
            AnchoredPosition = new UnityEngine.Vector2(value.x - reference.x, value.y - reference.y);
            bool moved = value != LocalPosition;
            LocalPosition = value;
            MarkDirty(moved);
        }

        public override void ParentChangedInternal(bool worldPositionStays)
        {
            // Keeping the world position means the new localPosition is authoritative; keeping the
            // local values means the anchoring is.
            if (worldPositionStays) SetLocalPositionInternal(LocalPosition);
            hasLastRect = false;
            Resolve();
        }
    }
}
