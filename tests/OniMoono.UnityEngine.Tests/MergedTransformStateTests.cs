using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OniMoono.Engine;
using OniMoono.Engine.UI;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// A Transform's engine state and hierarchy are on the Transform itself, merged from what was
    /// its record (docs/research/facade-merge.md, step 4): it has no record, and the values the
    /// game and the engine read are the record's were.
    public class MergedTransformStateTests : EngineTest
    {
        public class Script : MonoBehaviour { }

        [Fact]
        public void ANewTransformStartsAsItsRecordDid()
        {
            var go = new GameObject("plain");
            var t = go.transform;
            Assert.Null(t.NativeRecord);
            Assert.Null(t.EngineState);
            Assert.Same(t, go.Transform);
            Assert.Same(go, t.GameObject);
            Assert.Null(t.Parent);
            Assert.Equal(0, t.Children.Length);
            Assert.Equal(Vector3.zero, t.localPosition);
            Assert.Equal(Quaternion.identity, t.localRotation);
            Assert.Equal(Vector3.one, t.localScale);
            Assert.True(t.hasChanged);
            Assert.Equal(Matrix4x4.identity, t.localToWorldMatrix);
            Assert.Equal(Matrix4x4.identity, t.worldToLocalMatrix);
            Assert.Equal(Vector3.one, t.lossyScale);

            var rect = (RectTransform)new GameObject("rect", typeof(RectTransform)).transform;
            Assert.Null(rect.NativeRecord);
            Assert.Equal(new Vector2(0.5f, 0.5f), rect.anchorMin);
            Assert.Equal(new Vector2(0.5f, 0.5f), rect.anchorMax);
            Assert.Equal(new Vector2(100f, 100f), rect.sizeDelta);
            Assert.Equal(new Vector2(0.5f, 0.5f), rect.pivot);
            Assert.Equal(Vector2.zero, rect.anchoredPosition);
            Assert.True(rect.sendChildDimensionsChange);
            Assert.Null(rect.drivenByObject);
            Assert.Equal(new Rect(-50f, -50f, 100f, 100f), rect.rect);
            Assert.Equal(Quaternion.identity, rect.localRotation);
            Assert.Equal(Vector3.one, rect.localScale);
        }

        [Fact]
        public void ALoadedPrefabsTransformsStartAsTheirRecordsDid()
        {
            var (go, _) = MergedStateLifetimeTests.LoadPrefabWithScript();
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                Assert.Null(t.NativeRecord);
                Assert.Same(t, t.GameObject.Transform);
                Assert.Null(t.Serialized);
                Assert.True(t.hasChanged);
                if (t.parent is { } p) Assert.Contains(t, Enumerable.Range(0, p.childCount).Select(p.GetChild));
            }
        }

        [Fact]
        public void ARectTransformTakesATransformsPlaceInTheHierarchy()
        {
            var parent = new GameObject("parent").transform;
            var before = new GameObject("before").transform;
            var go = new GameObject("x");
            var after = new GameObject("after").transform;
            before.SetParent(parent, false);
            go.transform.SetParent(parent, false);
            after.SetParent(parent, false);
            var c1 = new GameObject("c1").transform;
            var c2 = new GameObject("c2").transform;
            c1.SetParent(go.transform, false);
            c2.SetParent(go.transform, false);
            parent.position = new Vector3(10f, 0f, 0f);
            go.transform.localPosition = new Vector3(1f, 2f, 3f);
            go.transform.localRotation = Quaternion.Euler(0f, 0f, 30f);
            go.transform.localScale = new Vector3(2f, 2f, 1f);
            var old = go.transform;
            var world = old.position;
            int slot = TextScaleWatch.Watch(old.TransformNative);
            try
            {
                var rect = go.AddComponent<RectTransform>();
                Assert.False(old);
                Assert.Same(rect, go.transform);
                Assert.Same(rect, go.Transform);
                Assert.Same(go, rect.EngineGameObject);
                Assert.Same(parent, rect.parent);
                Assert.Equal(1, rect.GetSiblingIndex());
                Assert.Same(rect, parent.GetChild(1));
                Assert.Equal(2, rect.childCount);
                Assert.Same(c1, rect.GetChild(0));
                Assert.Same(c2, rect.GetChild(1));
                Assert.Same(rect, c1.parent);
                Assert.Same(rect, c2.parent);
                Assert.Equal(new Vector3(1f, 2f, 3f), rect.localPosition);
                Assert.True(Quaternion.Euler(0f, 0f, 30f) == rect.localRotation);
                Assert.Equal(old.LocalRotation, rect.LocalRotation);
                Assert.Equal(new Vector3(2f, 2f, 1f), rect.localScale);
                Assert.Equal(world, rect.position);
                Assert.Equal(new Vector2(100f, 100f), rect.sizeDelta);
                // The text-scale slot follows the transform, armed, and later changes arm it.
                Assert.Equal(slot, rect.ScaleWatchSlot);
                Assert.Equal(0, old.ScaleWatchSlot);
                TextScaleWatch.Disarm(slot);
                rect.localScale = new Vector3(3f, 3f, 1f);
                Assert.NotEqual(0, TextScaleWatch.Armed[slot]);
                // The replaced transform keeps nothing of the hierarchy.
                Assert.Null(old.Parent);
                Assert.Equal(0, old.Children.Length);
                Assert.Throws<NullReferenceException>(() => old.position);
            }
            finally
            {
                TextScaleWatch.Unwatch(slot);
                Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Fact]
        public void SetParentAndSiblingOrderKeepWorldAndLocalValues()
        {
            var a = new GameObject("a").transform;
            var b = new GameObject("b").transform;
            var child = new GameObject("child").transform;
            a.position = new Vector3(5f, 0f, 0f);
            b.position = new Vector3(0f, 7f, 0f);
            b.localScale = new Vector3(2f, 2f, 2f);
            child.SetParent(a, false);
            child.localPosition = new Vector3(1f, 1f, 0f);
            Assert.Equal(new Vector3(6f, 1f, 0f), child.position);

            child.SetParent(b, true);
            Assert.Equal(new Vector3(6f, 1f, 0f), child.position);
            Assert.Equal(new Vector3(3f, -3f, 0f), child.localPosition);
            Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), child.localScale);
            Assert.Equal(Vector3.one, child.lossyScale);

            child.SetParent(a, false);
            Assert.Equal(new Vector3(3f, -3f, 0f), child.localPosition);
            Assert.Equal(new Vector3(8f, -3f, 0f), child.position);

            var others = Enumerable.Range(0, 3).Select(i => new GameObject("o" + i).transform).ToArray();
            foreach (var o in others) o.SetParent(a, false);
            Assert.Equal(0, child.GetSiblingIndex());
            child.SetAsLastSibling();
            Assert.Equal(3, child.GetSiblingIndex());
            Assert.Same(child, a.GetChild(3));
            others[2].SetSiblingIndex(0);
            Assert.Equal(new[] { others[2], others[0], others[1], child }, Enumerable.Range(0, 4).Select(a.GetChild));
            Assert.Equal(5, a.hierarchyCount);
            Assert.Same(a, child.root);
            Assert.True(child.IsChildOf(a));
            Assert.False(child.IsChildOf(b));

            a.DetachChildren();
            Assert.Equal(0, a.childCount);
            Assert.Null(child.parent);
            Assert.Equal(new Vector3(8f, -3f, 0f), child.position);
        }

        [Fact]
        public void AnAncestorsChangeInvalidatesTheWorldValuesBelowIt()
        {
            var root = new GameObject("root").transform;
            var mid = new GameObject("mid").transform;
            var leaf = new GameObject("leaf").transform;
            var sibling = new GameObject("sibling").transform;
            mid.SetParent(root, false);
            leaf.SetParent(mid, false);
            sibling.SetParent(root, false);
            leaf.localPosition = new Vector3(1f, 0f, 0f);
            _ = leaf.localToWorldMatrix;
            long stamp = leaf.WorldStamp;
            long siblingStamp = sibling.WorldStamp;

            root.localRotation = Quaternion.Euler(0f, 0f, 90f);
            Assert.NotEqual(stamp, leaf.WorldStamp);
            var rotated = leaf.position;
            Assert.Equal(0f, rotated.x, 5);
            Assert.Equal(1f, rotated.y, 5);
            Assert.True(Quaternion.Euler(0f, 0f, 90f) == leaf.rotation);
            Assert.Equal(new Vector3(1f, 0f, 0f), leaf.InverseTransformPoint(leaf.TransformPoint(new Vector3(1f, 0f, 0f))), new Vector3Close());

            stamp = leaf.WorldStamp;
            mid.localScale = new Vector3(3f, 3f, 3f);
            Assert.NotEqual(stamp, leaf.WorldStamp);
            Assert.Equal(new Vector3(3f, 3f, 3f), leaf.lossyScale, new Vector3Close());

            // A sibling's or a child's change leaves the world values as they were.
            siblingStamp = sibling.WorldStamp;
            stamp = mid.WorldStamp;
            leaf.localPosition = new Vector3(5f, 0f, 0f);
            Assert.Equal(stamp, mid.WorldStamp);
            Assert.Equal(siblingStamp, sibling.WorldStamp);

            // hasChanged: set on the changed transform and below, not on its parent.
            root.hasChanged = mid.hasChanged = leaf.hasChanged = false;
            mid.localPosition = new Vector3(0f, 1f, 0f);
            Assert.False(root.hasChanged);
            Assert.True(mid.hasChanged);
            Assert.True(leaf.hasChanged);
        }

        [Fact]
        public void ARectsChangeStartsANewRectEpochAndResolvesItsChildren()
        {
            var parent = (RectTransform)new GameObject("parent", typeof(RectTransform)).transform;
            var child = (RectTransform)new GameObject("child", typeof(RectTransform)).transform;
            child.SetParent(parent, false);
            child.anchorMin = Vector2.zero;
            child.anchorMax = Vector2.one;
            child.sizeDelta = Vector2.zero;
            Assert.Equal(new Rect(-50f, -50f, 100f, 100f), child.rect);
            long epoch = RectTransform.RectEpoch;
            parent.sizeDelta = new Vector2(200f, 40f);
            Assert.NotEqual(epoch, RectTransform.RectEpoch);
            Assert.Equal(new Rect(-100f, -20f, 200f, 40f), child.rect);
            epoch = RectTransform.RectEpoch;
            parent.sizeDelta = new Vector2(200f, 40f);
            Assert.Equal(epoch, RectTransform.RectEpoch);
            child.SetParent(null, false);
            Assert.NotEqual(epoch, RectTransform.RectEpoch);
            Assert.Equal(new Rect(0f, 0f, 0f, 0f), child.rect);
        }

        [Fact]
        public void InstantiateCopiesThePlacementAndTheAnchoring()
        {
            var src = new GameObject("src", typeof(RectTransform));
            var rect = (RectTransform)src.transform;
            rect.anchorMin = new Vector2(0.1f, 0.2f);
            rect.anchorMax = new Vector2(0.3f, 0.4f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(12f, 34f);
            rect.anchoredPosition = new Vector2(5f, 6f);
            rect.sendChildDimensionsChange = false;
            rect.localRotation = Quaternion.Euler(0f, 0f, 45f);
            rect.localScale = new Vector3(2f, 3f, 4f);
            var child = new GameObject("child").transform;
            child.SetParent(rect, false);
            child.localPosition = new Vector3(7f, 8f, 9f);

            var clone = Object.Instantiate(src);
            var c = (RectTransform)clone.transform;
            Assert.Null(c.NativeRecord);
            Assert.NotSame(rect, c);
            Assert.Same(clone, c.GameObject);
            Assert.Same(c, clone.Transform);
            Assert.Equal(rect.anchorMin, c.anchorMin);
            Assert.Equal(rect.anchorMax, c.anchorMax);
            Assert.Equal(rect.pivot, c.pivot);
            Assert.Equal(rect.sizeDelta, c.sizeDelta);
            Assert.Equal(rect.anchoredPosition, c.anchoredPosition);
            Assert.False(c.sendChildDimensionsChange);
            Assert.Equal(rect.localPosition, c.localPosition);
            Assert.Equal(rect.localRotation, c.localRotation);
            Assert.Equal(rect.localScale, c.localScale);
            Assert.Equal(1, c.childCount);
            var cc = c.GetChild(0);
            Assert.NotSame(child, cc);
            Assert.Same(c, cc.parent);
            Assert.Equal(new Vector3(7f, 8f, 9f), cc.localPosition);
            Assert.Equal(child.position, cc.position);
        }

        [Fact]
        public void ADestroyedTransformHoldsNothingOfTheHierarchy()
        {
            var root = new GameObject("root");
            var mid = new GameObject("mid").transform;
            var leaf = new GameObject("leaf").transform;
            mid.SetParent(root.transform, false);
            leaf.SetParent(mid, false);
            var rootTransform = root.transform;
            Object.DestroyImmediate(root);
            foreach (var t in new[] { rootTransform, mid, leaf })
            {
                Assert.False(t);
                Assert.Null(t.Parent);
                Assert.Equal(0, t.Children.Length);
                Assert.Null(t.EngineGameObject);
                Assert.Throws<NullReferenceException>(() => t.parent);
                Assert.Throws<NullReferenceException>(() => t.childCount);
                Assert.Throws<NullReferenceException>(() => t.localPosition);
            }
        }

        /// Draw recording, culling and bounds read transforms on worker threads, several at once,
        /// while nothing changes the hierarchy: each worker computes the world values the main
        /// thread does, and a child list read before a change keeps the children it had.
        [Fact]
        public void WorkerThreadsReadTheWorldValuesTheMainThreadReads()
        {
            var root = new GameObject("root").transform;
            var all = new List<Transform> { root };
            var rng = new System.Random(4);
            for (int i = 0; i < 400; i++)
            {
                var t = new GameObject("t" + i).transform;
                t.SetParent(all[rng.Next(all.Count)], false);
                t.localPosition = new Vector3(rng.Next(-5, 6), rng.Next(-5, 6), 0f);
                t.localRotation = Quaternion.Euler(0f, 0f, rng.Next(0, 360));
                t.localScale = new Vector3(1f + rng.Next(0, 3) * 0.5f, 1f, 1f);
                all.Add(t);
            }
            for (int round = 0; round < 20; round++)
            {
                // A change on the main thread, then the workers read the dirty caches at once.
                root.localPosition = new Vector3(round, 0f, 0f);
                all[1 + rng.Next(all.Count - 1)].localScale = new Vector3(1f + round * 0.1f, 1f, 1f);
                var expected = new (Matrix4x4 m, Quaternion r, Vector3 s)[all.Count];
                var results = new (Matrix4x4 m, Quaternion r, Vector3 s)[4][];
                using (var start = new Barrier(4))
                {
                    Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, w =>
                    {
                        start.SignalAndWait();
                        var mine = new (Matrix4x4, Quaternion, Vector3)[all.Count];
                        for (int k = 0; k < all.Count; k++)
                        {
                            int i = (k * 7 + w * 101) % all.Count;
                            var t = all[i];
                            mine[i] = (t.LocalToWorld, t.WorldRotation, t.LossyScale);
                            foreach (var child in t.Children) Assert.Same(t, child.Parent);
                        }
                        results[w] = mine;
                    });
                }
                for (int i = 0; i < all.Count; i++) expected[i] = (all[i].localToWorldMatrix, all[i].rotation, all[i].lossyScale);
                for (int w = 0; w < 4; w++)
                    for (int i = 0; i < all.Count; i++)
                        Assert.True(expected[i].Equals(results[w][i]), $"round {round}, worker {w}, transform {i}");
            }
            Object.DestroyImmediate(root.gameObject);
        }

        /// A child list read before a change keeps its children on another thread while the main
        /// thread appends, moves and removes (copy-on-write). (Lists read during a change are not
        /// promised: the workers read the hierarchy while nothing changes it.)
        [Fact]
        public void AChildListReadBeforeAChangeKeepsItsChildren()
        {
            var parent = new GameObject("parent").transform;
            var kids = Enumerable.Range(0, 8).Select(i => new GameObject("k" + i).transform).ToList();
            foreach (var k in kids) k.SetParent(parent, false);
            var held = parent.Children;
            var snapshot = held.ToArray();
            bool stop = false;
            int reads = 0;
            Exception failure = null;
            var reader = new Thread(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop) || reads == 0)
                    {
                        for (int i = 0; i < held.Length; i++) Assert.Same(snapshot[i], held[i]);
                        reads++;
                    }
                }
                catch (Exception e) { failure = e; }
            });
            reader.Start();
            for (int i = 0; i < 2000; i++)
            {
                var extra = new GameObject("e").transform;
                extra.SetParent(parent, false);
                extra.SetAsFirstSibling();
                Object.DestroyImmediate(extra.gameObject);
            }
            Volatile.Write(ref stop, true);
            reader.Join();
            Assert.Null(failure);
            Assert.True(reads > 0);
            Assert.Equal(snapshot, Enumerable.Range(0, parent.childCount).Select(parent.GetChild));
            Object.DestroyImmediate(parent.gameObject);
        }

        sealed class Vector3Close : IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-8f;
            public int GetHashCode(Vector3 v) => 0;
        }
    }
}
