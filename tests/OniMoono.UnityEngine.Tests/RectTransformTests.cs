using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    public class RectTransformTests : EngineTest
    {
        [Fact]
        public void InsetAndSizeFromParentEdgePlacesTheRectInsideThatEdge()
        {
            var parent = new GameObject("Parent", typeof(RectTransform)).GetComponent<RectTransform>();
            parent.sizeDelta = new Vector2(200, 100);
            var child = new GameObject("Child", typeof(RectTransform)).GetComponent<RectTransform>();
            child.SetParent(parent, false);
            child.pivot = new Vector2(0.5f, 0f);

            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Right, 10, 40);
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 5, 20);

            Assert.Equal(new Vector2(1, 1), child.anchorMin);
            Assert.Equal(new Vector2(1, 1), child.anchorMax);
            Assert.Equal(new Vector2(40, 20), child.sizeDelta);
            Assert.Equal(new Vector2(-30, -25), child.anchoredPosition);
            var corners = new Vector3[4];
            child.GetWorldCorners(corners);
            Assert.Equal(new Vector3(50, 25, 0), corners[0]); // parent spans (-100, -50)..(100, 50)
            Assert.Equal(new Vector3(90, 45, 0), corners[2]);

            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, 10, 40);
            child.GetWorldCorners(corners);
            Assert.Equal(new Vector2(0, 1), child.anchorMin);
            Assert.Equal(-90f, corners[0].x);
            Assert.Equal(-50f, corners[2].x);
        }

        [Theory]
        [InlineData(RectTransform.Edge.Left, 0f)]
        [InlineData(RectTransform.Edge.Left, 0.25f)]
        [InlineData(RectTransform.Edge.Right, 0f)]
        [InlineData(RectTransform.Edge.Right, 1f)]
        [InlineData(RectTransform.Edge.Top, 0.5f)]
        [InlineData(RectTransform.Edge.Top, 0.75f)]
        [InlineData(RectTransform.Edge.Bottom, 0.5f)]
        [InlineData(RectTransform.Edge.Bottom, 1f)]
        public void InsetAndSizeFromEachEdgeAndPivot(RectTransform.Edge edge, float pivot)
        {
            var parent = new GameObject("Parent", typeof(RectTransform)).GetComponent<RectTransform>();
            parent.sizeDelta = new Vector2(200, 100);
            var child = new GameObject("Child", typeof(RectTransform)).GetComponent<RectTransform>();
            child.SetParent(parent, false);
            child.anchorMin = new Vector2(0.2f, 0.3f);
            child.anchorMax = new Vector2(0.6f, 0.7f);
            child.sizeDelta = new Vector2(7, 9);
            child.anchoredPosition = new Vector2(3, 4);
            child.pivot = new Vector2(pivot, pivot);

            child.SetInsetAndSizeFromParentEdge(edge, 10, 30);

            int axis = edge is RectTransform.Edge.Left or RectTransform.Edge.Right ? 0 : 1;
            int other = 1 - axis;
            bool far = edge is RectTransform.Edge.Right or RectTransform.Edge.Top;
            Assert.Equal(far ? 1f : 0f, child.anchorMin[axis]);
            Assert.Equal(far ? 1f : 0f, child.anchorMax[axis]);
            Assert.Equal(30f, child.sizeDelta[axis]);
            Assert.Equal(far ? -10f - 30f * (1f - pivot) : 10f + 30f * pivot, child.anchoredPosition[axis], 4);
            // The other axis is untouched.
            Assert.Equal(new Vector2(0.2f, 0.3f)[other], child.anchorMin[other]);
            Assert.Equal(new Vector2(0.6f, 0.7f)[other], child.anchorMax[other]);
            Assert.Equal(new Vector2(7, 9)[other], child.sizeDelta[other]);
            Assert.Equal(new Vector2(3, 4)[other], child.anchoredPosition[other]);

            // The near side sits 10 from the edge (parent spans -100..100 by -50..50).
            var corners = new Vector3[4];
            child.GetWorldCorners(corners);
            float half = axis == 0 ? 100f : 50f;
            float near = far ? corners[2][axis] : corners[0][axis];
            Assert.Equal(far ? half - 10f : -half + 10f, near, 3);
        }

        /// A rect read again is the same until something it depends on changes, and then follows
        /// it: the parent's size, the anchors, the pivot, sizeDelta and the parent itself, at
        /// any depth (RectTransformNative keeps a rect per rect epoch).
        [Fact]
        public void KeptRectsFollowEveryChangeAbove()
        {
            var root = new GameObject("Root", typeof(RectTransform)).GetComponent<RectTransform>();
            root.sizeDelta = new Vector2(200, 100);
            var mid = new GameObject("Mid", typeof(RectTransform)).GetComponent<RectTransform>();
            mid.SetParent(root, false);
            mid.anchorMin = Vector2.zero;
            mid.anchorMax = Vector2.one;
            mid.sizeDelta = Vector2.zero;
            var leaf = new GameObject("Leaf", typeof(RectTransform)).GetComponent<RectTransform>();
            leaf.SetParent(mid, false);
            leaf.anchorMin = Vector2.zero;
            leaf.anchorMax = new Vector2(0.5f, 1f);
            leaf.sizeDelta = new Vector2(-10, 0);
            Assert.Equal(new Rect(-45, -50, 90, 100), leaf.rect);
            Assert.Equal(leaf.rect, leaf.rect);

            root.sizeDelta = new Vector2(400, 60);
            Assert.Equal(new Rect(-95, -30, 190, 60), leaf.rect);
            mid.sizeDelta = new Vector2(-100, 0);
            Assert.Equal(new Rect(-70, -30, 140, 60), leaf.rect);
            leaf.pivot = Vector2.zero;
            Assert.Equal(new Rect(0, 0, 140, 60), leaf.rect);
            leaf.anchorMax = Vector2.one;
            Assert.Equal(new Rect(0, 0, 290, 60), leaf.rect);

            var other = new GameObject("Other", typeof(RectTransform)).GetComponent<RectTransform>();
            other.sizeDelta = new Vector2(50, 20);
            mid.SetParent(other, false);
            Assert.Equal(new Rect(0, 0, -60, 20), leaf.rect);
            leaf.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 30);
            Assert.Equal(new Rect(0, 0, 30, 20), leaf.rect);
        }

        /// A write of the value a rect already has (a layout group placing its children where
        /// they are) starts no new rect epoch; a value that differs does.
        [Fact]
        public void WritingTheValueThereKeepsTheKeptRects()
        {
            var rect = new GameObject("Rect", typeof(RectTransform)).GetComponent<RectTransform>();
            var parent = new GameObject("Parent", typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.sizeDelta = new Vector2(10, 20);
            long epoch = OniMoono.Engine.RectTransformNative.RectEpoch;
            rect.sizeDelta = new Vector2(10, 20);
            rect.anchorMin = rect.anchorMin;
            rect.anchorMax = rect.anchorMax;
            rect.pivot = rect.pivot;
            rect.SetParent(parent, false);
            Assert.Equal(epoch, OniMoono.Engine.RectTransformNative.RectEpoch);
            Assert.Equal(new Rect(-5, -10, 10, 20), rect.rect);

            rect.sizeDelta = new Vector2(0, 20);
            Assert.NotEqual(epoch, OniMoono.Engine.RectTransformNative.RectEpoch);
            Assert.Equal(new Rect(0, -10, 0, 20), rect.rect);
            epoch = OniMoono.Engine.RectTransformNative.RectEpoch;
            // A layout group pins a child by its edge (the anchors and the size written each time).
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, 3, 7);
            Assert.NotEqual(epoch, OniMoono.Engine.RectTransformNative.RectEpoch);
            epoch = OniMoono.Engine.RectTransformNative.RectEpoch;
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, 3, 7);
            Assert.Equal(epoch, OniMoono.Engine.RectTransformNative.RectEpoch);
            Assert.Equal(7f, rect.rect.width);
            rect.pivot = new Vector2(0.5f, 1f);
            Assert.NotEqual(epoch, OniMoono.Engine.RectTransformNative.RectEpoch);
            Assert.Equal(-20f, rect.rect.y);
        }
    }
}
