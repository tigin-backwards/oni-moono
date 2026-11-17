using System;
using System.Collections.Generic;
using System.Reflection;
using OniMoono.Engine;
using OniMoono.Engine.UI;
using TMPro;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// TextMeshPro's scale check called only for the texts whose scale may have changed
    /// (TextScaleWatch): which changes arm a text's slot, and how the gated subscribers are called.
    public class TextScaleWatchTests : EngineTest
    {
        static bool Armed(int slot) => TextScaleWatch.Armed[slot] != 0;

        [Fact]
        public void AChangeOfTheTransformOrAnAncestorArmsItsSlot()
        {
            var root = new GameObject("Root", typeof(RectTransform));
            var mid = new GameObject("Mid", typeof(RectTransform));
            var leaf = new GameObject("Leaf", typeof(RectTransform));
            var sibling = new GameObject("Sibling", typeof(RectTransform));
            var below = new GameObject("Below", typeof(RectTransform));
            mid.transform.SetParent(root.transform, false);
            leaf.transform.SetParent(mid.transform, false);
            sibling.transform.SetParent(mid.transform, false);
            below.transform.SetParent(leaf.transform, false);
            var native = leaf.transform.TransformNative;
            int slot = TextScaleWatch.Watch(native);
            try
            {
                Assert.NotEqual(0, slot);
                Assert.True(Armed(slot));
                // A second watcher of the same transform gets no slot (it is called every frame).
                Assert.Equal(0, TextScaleWatch.Watch(native));

                var changes = new (string what, Action change, bool arms)[]
                {
                    ("its scale", () => leaf.transform.localScale = new Vector3(2f, 2f, 1f), true),
                    ("its position", () => leaf.transform.localPosition = new Vector3(3f, 0f, 0f), true),
                    ("its rotation", () => leaf.transform.localRotation = Quaternion.Euler(0f, 0f, 10f), true),
                    ("the parent's scale", () => mid.transform.localScale = new Vector3(0.5f, 0.5f, 1f), true),
                    ("the root's position", () => root.transform.position = new Vector3(1f, 1f, 0f), true),
                    ("its parent", () => leaf.transform.SetParent(root.transform, true), true),
                    ("its size", () => ((RectTransform)leaf.transform).sizeDelta = new Vector2(40f, 10f), true),
                    ("a sibling's scale", () => sibling.transform.localScale = new Vector3(4f, 4f, 1f), false),
                    ("a child's scale", () => below.transform.localScale = new Vector3(4f, 4f, 1f), false),
                    ("its sibling index", () => leaf.transform.SetAsFirstSibling(), false),
                };
                foreach (var (what, change, arms) in changes)
                {
                    TextScaleWatch.Disarm(slot);
                    var before = leaf.transform.lossyScale;
                    change();
                    Assert.True(arms == Armed(slot), what);
                    // Whatever leaves it disarmed leaves the cached scale as it was.
                    if (!arms) Assert.Equal(before, leaf.transform.lossyScale);
                }
            }
            finally
            {
                TextScaleWatch.Unwatch(slot);
            }
            Assert.Equal(0, native.ScaleWatchSlot);
        }

        [Fact]
        public void TheSlotFollowsATransformReplacedByARectTransform()
        {
            var go = new GameObject("Plain");
            var old = go.transform.TransformNative;
            int slot = TextScaleWatch.Watch(old);
            try
            {
                go.AddComponent<RectTransform>();
                var now = go.transform.TransformNative;
                Assert.NotSame(old, now);
                Assert.Equal(0, old.ScaleWatchSlot);
                Assert.Equal(slot, now.ScaleWatchSlot);
                TextScaleWatch.Disarm(slot);
                go.transform.localScale = new Vector3(2f, 2f, 2f);
                Assert.True(Armed(slot));
            }
            finally
            {
                TextScaleWatch.Unwatch(slot);
            }
        }

        [Fact]
        public void AFreedSlotIsGivenOutAgainArmed()
        {
            var a = new GameObject("A", typeof(RectTransform));
            var b = new GameObject("B", typeof(RectTransform));
            int slot = TextScaleWatch.Watch(a.transform.TransformNative);
            TextScaleWatch.Disarm(slot);
            TextScaleWatch.Unwatch(slot);
            Assert.True(Armed(slot));
            int again = TextScaleWatch.Watch(b.transform.TransformNative);
            try
            {
                Assert.Equal(slot, again);
                Assert.True(Armed(again));
                // The first transform no longer arms it.
                TextScaleWatch.Disarm(again);
                a.transform.localScale = new Vector3(3f, 3f, 3f);
                Assert.False(Armed(again));
            }
            finally
            {
                TextScaleWatch.Unwatch(again);
            }
        }

        /// A gated subscriber is skipped while its slot is disarmed and keeps its place among the
        /// others; a removal and the compaction after it keep each entry's gate.
        [Fact]
        public void GatedSubscribersAreCalledInTheirPlaceWhileArmed()
        {
            var calls = new List<string>();
            var a = new GameObject("A", typeof(RectTransform));
            var c = new GameObject("C", typeof(RectTransform));
            int ga = TextScaleWatch.Watch(a.transform.TransformNative);
            int gc = TextScaleWatch.Watch(c.transform.TransformNative);
            Canvas.WillRenderCanvases first = () => calls.Add("first"), b = () => calls.Add("b"), last = () => calls.Add("last");
            Canvas.WillRenderCanvases ad = () => calls.Add("a"), cd = () => calls.Add("c");
            // The middle subscriber scales C (anew each time: setting the same scale changes
            // nothing): C, after it, is called in the same pass.
            float scale = 1f;
            Canvas.WillRenderCanvases scaler = () =>
            {
                calls.Add("scaler");
                scale += 1f;
                c.transform.localScale = new Vector3(scale, scale, 1f);
            };
            try
            {
                Canvas.willRenderCanvases += first;
                Canvas.AddWillRenderCanvases(ad, ga);
                Canvas.willRenderCanvases += b;
                Canvas.willRenderCanvases += scaler;
                Canvas.AddWillRenderCanvases(cd, gc);
                Canvas.willRenderCanvases += last;
                Canvas.SendWillRenderCanvases();
                Assert.Equal(["first", "a", "b", "scaler", "c", "last"], calls);

                calls.Clear();
                TextScaleWatch.Disarm(ga);
                TextScaleWatch.Disarm(gc);
                Canvas.SendWillRenderCanvases();
                Assert.Equal(["first", "b", "scaler", "c", "last"], calls);

                calls.Clear();
                Canvas.willRenderCanvases -= b;
                Canvas.willRenderCanvases -= scaler;
                TextScaleWatch.Disarm(ga);
                TextScaleWatch.Disarm(gc);
                a.transform.localPosition = Vector3.one;
                Canvas.SendWillRenderCanvases();
                Assert.Equal(["first", "a", "last"], calls);

                // Removed and added again, a gated subscriber goes to the end with its gate.
                calls.Clear();
                Canvas.willRenderCanvases -= ad;
                Canvas.AddWillRenderCanvases(ad, ga);
                TextScaleWatch.Disarm(ga);
                Canvas.SendWillRenderCanvases();
                Assert.Equal(["first", "last"], calls);
                calls.Clear();
                TextScaleWatch.Arm(ga);
                Canvas.SendWillRenderCanvases();
                Assert.Equal(["first", "last", "a"], calls);
            }
            finally
            {
                Canvas.willRenderCanvases -= first;
                Canvas.willRenderCanvases -= ad;
                Canvas.willRenderCanvases -= b;
                Canvas.willRenderCanvases -= scaler;
                Canvas.willRenderCanvases -= cd;
                Canvas.willRenderCanvases -= last;
                TextScaleWatch.Unwatch(ga);
                TextScaleWatch.Unwatch(gc);
            }
        }

        [Fact]
        public void ADelegateListKeepsEachEntrysGateThroughRemovals()
        {
            var list = new DelegateList<Action>();
            var calls = new List<int>();
            var actions = new Action[12];
            for (int i = 0; i < actions.Length; i++)
            {
                int id = i;
                actions[i] = () => calls.Add(id);
                list.AddGated(actions[i], i % 3 == 0 ? 0 : 100 + i);
            }
            for (int i = 1; i < actions.Length; i += 4) list.Remove(actions[i]);
            using var s = list.Take();
            var expected = new List<(int, int)>();
            for (int i = 0; i < actions.Length; i++)
                if (i % 4 != 1) expected.Add((i, i % 3 == 0 ? 0 : 100 + i));
            var got = new List<(int, int)>();
            for (int k = 0; k < s.Items.Length; k++)
            {
                calls.Clear();
                s.Items[k]();
                got.Add((calls[0], s.Gates[k]));
            }
            Assert.Equal(expected, got);
        }

        static int SlotOf(TextMeshProUGUI t) =>
            (int)typeof(TextMeshProUGUI).GetField("m_ScaleSlot", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(t);

        /// A text is checked while its scale may have changed and until its check finds the scale
        /// settled; then not until it or an ancestor changes again.
        [Fact]
        public void ATextIsCheckedOnlyAfterAChangeUntilItsScaleSettles()
        {
            var t = TmpTestSupport.NewText("abc", TmpTestSupport.Graystroke, 14f, new Vector2(300f, 100f));
            var mid = new GameObject("Mid", typeof(RectTransform));
            mid.transform.SetParent(t.canvas.transform, false);
            t.transform.SetParent(mid.transform, false);
            int slot = SlotOf(t);
            Assert.NotEqual(0, slot);
            Canvas.ForceUpdateCanvases();
            Assert.False(Armed(slot));
            float w = t.textInfo.meshInfo[0].uvs0[0].w;

            // Within the 1.25 band: armed, checked, settled.
            mid.transform.localScale = new Vector3(0.9f, 0.9f, 1f);
            Assert.True(Armed(slot));
            Canvas.ForceUpdateCanvases();
            Assert.False(Armed(slot));
            Assert.Equal(w, t.textInfo.meshInfo[0].uvs0[0].w);

            // A rescale takes a new SDF scale: the next check settles it.
            mid.transform.localScale = new Vector3(0.5f, 0.5f, 1f);
            Canvas.ForceUpdateCanvases();
            Assert.Equal(w * 0.5f, t.textInfo.meshInfo[0].uvs0[0].w, 6);
            Assert.True(Armed(slot));
            Canvas.ForceUpdateCanvases();
            Assert.False(Armed(slot));

            // A text marked static stays armed (its flag can change without a move).
            t.isTextObjectScaleStatic = true;
            mid.transform.localScale = new Vector3(0.2f, 0.2f, 1f);
            Canvas.ForceUpdateCanvases();
            Assert.True(Armed(slot));
            t.isTextObjectScaleStatic = false;
            Canvas.ForceUpdateCanvases();
            Assert.Equal(w * 0.2f, t.textInfo.meshInfo[0].uvs0[0].w, 5);

            // Disabled and enabled: armed again; destroyed: the slot is free.
            Canvas.ForceUpdateCanvases();
            Assert.False(Armed(slot));
            t.enabled = false;
            t.enabled = true;
            Assert.True(Armed(slot));
            var native = t.transform.TransformNative;
            UnityEngine.Object.DestroyImmediate(t);
            Assert.Equal(0, native.ScaleWatchSlot);
        }
    }
}
