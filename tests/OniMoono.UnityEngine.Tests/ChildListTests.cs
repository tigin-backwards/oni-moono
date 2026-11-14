using System;
using System.Collections.Generic;
using OniMoono.Engine;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// A transform's child list: sibling order and indices as Unity keeps them, a list read before
    /// a change keeps the children it had, and a parent that grows at run time gets spare slots
    /// instead of a copy of all its children for each new one (the game's popup texts).
    public class ChildListTests : EngineTest
    {
        static string[] Names(Transform parent)
        {
            var names = new string[parent.childCount];
            for (int i = 0; i < names.Length; i++) names[i] = parent.GetChild(i).name;
            return names;
        }

        /// A sibling move is one copy of the list: every from/to pair leaves the order a removal
        /// and an insertion would, and a list read before the move keeps what it had.
        [Fact]
        public void SiblingMovesKeepTheOrderOfRemoveAndInsert()
        {
            for (int n = 1; n <= 9; n++)
                for (int from = 0; from < n; from++)
                    for (int to = 0; to < n; to++)
                    {
                        var parent = new GameObject("parent").transform;
                        var expected = new List<string>();
                        Transform moved = null;
                        for (int i = 0; i < n; i++)
                        {
                            var t = new GameObject("c" + i).transform;
                            t.SetParent(parent, false);
                            expected.Add(t.name);
                            if (i == from) moved = t;
                        }
                        var before = parent.TransformNative.Children;
                        var beforeNames = expected.ToArray();
                        moved.SetSiblingIndex(to);
                        expected.RemoveAt(from);
                        expected.Insert(to, moved.name);
                        Assert.Equal(expected.ToArray(), Names(parent));
                        Assert.Equal(to, moved.GetSiblingIndex());
                        Assert.Same(parent, moved.parent);
                        Assert.Equal(n, before.Length);
                        for (int i = 0; i < n; i++) Assert.Equal(beforeNames[i], before[i].GameObject.Name);
                        Object.DestroyImmediate(parent.gameObject);
                    }
        }

        [Fact]
        public void AppendsInsertsAndRemovalsKeepSiblingOrder()
        {
            var parent = new GameObject("parent").transform;
            var children = new List<Transform>();
            var expected = new List<string>();
            for (int i = 0; i < 40; i++)
            {
                var t = new GameObject("c" + i).transform;
                t.SetParent(parent, false);
                children.Add(t);
                expected.Add(t.name);
                Assert.Equal(i, t.GetSiblingIndex());
            }
            Assert.Equal(expected.ToArray(), Names(parent));

            children[3].SetParent(null);
            expected.Remove("c3");
            children[17].SetAsFirstSibling();
            expected.Remove("c17");
            expected.Insert(0, "c17");
            children[0].SetSiblingIndex(10);
            expected.Remove("c0");
            expected.Insert(10, "c0");
            children[39].SetParent(null);
            expected.Remove("c39");
            Object.DestroyImmediate(children[20].gameObject);
            expected.Remove("c20");
            var late = new GameObject("late").transform;
            late.SetParent(parent, false);
            expected.Add("late");
            children[3].SetParent(parent, false);
            expected.Add("c3");

            Assert.Equal(expected.ToArray(), Names(parent));
            for (int i = 0; i < expected.Count; i++) Assert.Equal(i, parent.GetChild(i).GetSiblingIndex());
            Assert.Throws<UnityException>(() => parent.GetChild(expected.Count));
            Assert.Equal(expected.Count + 1, parent.hierarchyCount);
        }

        [Fact]
        public void AListReadBeforeAChangeKeepsItsChildren()
        {
            var parent = new GameObject("parent").transform;
            for (int i = 0; i < 6; i++) new GameObject("c" + i).transform.SetParent(parent, false);
            var native = parent.TransformNative;
            var before = native.Children;
            // Appends fill spare slots of the same array; the list read before does not see them.
            for (int i = 6; i < 9; i++) new GameObject("c" + i).transform.SetParent(parent, false);
            Assert.Equal(6, before.Length);
            var afterAppends = native.Children;
            parent.GetChild(2).SetParent(null);
            parent.GetChild(0).SetAsLastSibling();
            new GameObject("c9").transform.SetParent(parent, false);

            var seen = new List<string>();
            foreach (var c in before) seen.Add(c.GameObject.Name);
            Assert.Equal(["c0", "c1", "c2", "c3", "c4", "c5"], seen);
            seen.Clear();
            foreach (var c in afterAppends) seen.Add(c.GameObject.Name);
            Assert.Equal(["c0", "c1", "c2", "c3", "c4", "c5", "c6", "c7", "c8"], seen);
            Assert.Equal(["c1", "c3", "c4", "c5", "c6", "c7", "c8", "c0", "c9"], Names(parent));
            Assert.Equal(-1, before.IndexOf(native));
            Assert.Throws<IndexOutOfRangeException>(() => before[6]);
        }

        // Unity's DetachChildren and a walk that reparents as it goes (the game's storage) see
        // each child once, as with the copied arrays.
        [Fact]
        public void DetachingWhileWalkingSeesEachChildOnce()
        {
            var parent = new GameObject("parent").transform;
            var other = new GameObject("other").transform;
            for (int i = 0; i < 12; i++) new GameObject("c" + i).transform.SetParent(parent, false);
            var moved = new List<string>();
            foreach (var c in parent.TransformNative.Children)
            {
                moved.Add(c.GameObject.Name);
                Hierarchy.SetParent(c, other.TransformNative, false);
                new GameObject("extra").transform.SetParent(parent, false);
            }
            Assert.Equal(12, moved.Count);
            Assert.Equal(12, other.childCount);
            Assert.Equal(12, parent.childCount);
            parent.DetachChildren();
            Assert.Equal(0, parent.childCount);
        }

        [Fact]
        public void AppendingThousandsOfChildrenDoesNotCopyTheListEachTime()
        {
            var parent = new GameObject("parent").transform;
            const int Count = 4000;
            var children = new Transform[Count];
            for (int i = 0; i < Count; i++) children[i] = new GameObject("c" + i).transform;
            children[0].SetParent(parent, false);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 1; i < Count; i++) children[i].SetParent(parent, false);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            // A copy per child would be 8 bytes × Count² / 2 = 64 MB; spare slots make it about 3 ×
            // 8 × Count, plus what each SetParent allocates.
            Assert.True(allocated < 2_000_000, $"{allocated} bytes allocated");
            Assert.Equal(Count, parent.childCount);
            for (int i = 0; i < Count; i += 397) Assert.Same(children[i], parent.GetChild(i));
        }

        [Fact]
        public void InstantiatedHierarchiesKeepTheirOrder()
        {
            var root = new GameObject("root");
            for (int i = 0; i < 7; i++)
            {
                var c = new GameObject("c" + i);
                c.transform.SetParent(root.transform, false);
                new GameObject("g" + i).transform.SetParent(c.transform, false);
            }
            var container = new GameObject("container").transform;
            for (int i = 0; i < 9; i++) Object.Instantiate(root, container).name = "clone" + i;
            Assert.Equal(["c0", "c1", "c2", "c3", "c4", "c5", "c6"], Names(container.GetChild(4)));
            Assert.Equal("g3", container.GetChild(8).GetChild(3).GetChild(0).name);
            for (int i = 0; i < 9; i++) Assert.Equal("clone" + i, container.GetChild(i).name);
        }

        [Fact]
        public void ARectTransformAddedInPlaceKeepsItsSiblingIndexAndChildren()
        {
            var parent = new GameObject("parent").transform;
            for (int i = 0; i < 6; i++) new GameObject("c" + i).transform.SetParent(parent, false);
            var middle = parent.GetChild(3).gameObject;
            new GameObject("m0").transform.SetParent(middle.transform, false);
            new GameObject("m1").transform.SetParent(middle.transform, false);
            var rect = middle.AddComponent<RectTransform>();
            Assert.Same(rect, parent.GetChild(3));
            Assert.Equal(3, rect.GetSiblingIndex());
            Assert.Equal(["m0", "m1"], Names(rect));
            Assert.Same(rect, rect.GetChild(1).parent);
            new GameObject("c6").transform.SetParent(parent, false);
            Assert.Equal(["c0", "c1", "c2", "c3", "c4", "c5", "c6"], Names(parent));
        }
    }
}
