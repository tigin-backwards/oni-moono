using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using OniMoono.Engine;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// The engine's hashed collections of its objects compare them by reference
    /// (docs/research/facade-merge.md, category 2). Under a facade's Equals every destroyed object
    /// equals every other, so two destroyed objects whose hashes collide would stand for each
    /// other: a set would keep one of them for the other, a dictionary give one's value for the
    /// other. Object hashes are random, so the tests below make two behaviours whose keys collide
    /// and put them through the phase lists together.
    public class IdentityCollisionTests : EngineTest
    {
        public class Ticks : MonoBehaviour
        {
            public int Updates, LateUpdates;
            void Update() => Updates++;
            void LateUpdate() => LateUpdates++;
        }

        /// The object the behaviour manager keeps for a behaviour: the behaviour itself.
        static object Key(MonoBehaviour b) => b;

        /// Two behaviours, each on an inactive object of its own, whose keys have the same hash
        /// (a hash has 26 bits: about 10,000 objects give a collision).
        static (Ticks, Ticks) Colliding()
        {
            var byHash = new Dictionary<int, Ticks>();
            var made = new List<GameObject>();
            try
            {
                for (int i = 0; i < 400_000; i++)
                {
                    var go = new GameObject("candidate");
                    go.SetActive(false);
                    var t = go.AddComponent<Ticks>();
                    int hash = RuntimeHelpers.GetHashCode(Key(t));
                    if (byHash.TryGetValue(hash, out var first))
                    {
                        made.Remove(first.gameObject);
                        return (first, t);
                    }
                    byHash[hash] = t;
                    made.Add(go);
                }
                throw new InvalidOperationException("no two hashes collided");
            }
            finally
            {
                foreach (var go in made) Object.DestroyImmediate(go);
            }
        }

        static int Members(string phase)
        {
            var list = typeof(BehaviourManager).GetField(phase, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var members = list.GetType().GetField("members", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(list);
            return (int)members.GetType().GetProperty("Count").GetValue(members);
        }

        /// The members after the removals earlier tests left pending.
        static int Settled()
        {
            Frame();
            return Members("update");
        }

        [Fact]
        public void TwoCollidingBehavioursDestroyedTogetherBothLeaveThePhaseLists()
        {
            var (a, b) = Colliding();
            Assert.Equal(RuntimeHelpers.GetHashCode(Key(a)), RuntimeHelpers.GetHashCode(Key(b)));
            int before = Settled();
            a.gameObject.SetActive(true);
            b.gameObject.SetActive(true);
            Frame();
            Assert.Equal((1, 1), (a.Updates, b.Updates));
            Assert.Equal(before + 2, Members("update"));
            Object.Destroy(a.gameObject);
            Object.Destroy(b.gameObject);
            Frame();
            Frame();
            Assert.Equal((1, 1), (a.Updates, b.Updates));
            Assert.Equal((1, 1), (a.LateUpdates, b.LateUpdates));
            Assert.Equal(before, Members("update"));
            Assert.Equal(before, Members("lateUpdate"));
        }

        [Fact]
        public void TwoCollidingBehavioursAddedAndRemovedInOneFrameNeverUpdate()
        {
            var (a, b) = Colliding();
            int before = Settled();
            // Both pending addition, one destroyed and one disabled before the next phase.
            a.gameObject.SetActive(true);
            b.gameObject.SetActive(true);
            Object.DestroyImmediate(a.gameObject);
            b.enabled = false;
            Frame();
            Assert.Equal((0, 0), (a.Updates, b.Updates));
            Assert.Equal(before, Members("update"));
            b.enabled = true;
            Frame();
            Assert.Equal(1, b.Updates);
            Assert.Equal(before + 1, Members("update"));
        }

        [Fact]
        public void ACollidingBehaviourDestroyedWhileTheOtherIsRemovedLeavesTheOtherRunning()
        {
            var (a, b) = Colliding();
            int before = Settled();
            a.gameObject.SetActive(true);
            b.gameObject.SetActive(true);
            Frame();
            Object.DestroyImmediate(a.gameObject);
            b.enabled = false;
            b.enabled = true;
            Frame();
            Frame();
            Assert.Equal((1, 3), (a.Updates, b.Updates));
            Assert.Equal(before + 1, Members("update"));
        }

        /// Every hashed collection of the engine's that holds its objects compares by reference.
        [Theory]
        [InlineData(typeof(BehaviourManager), "update", "pendingAddAt", "members", "pendingRemove")]
        [InlineData(typeof(BehaviourManager), "lateUpdate", "pendingAddAt", "members", "pendingRemove")]
        [InlineData(typeof(BehaviourManager), "fixedUpdate", "pendingAddAt", "members", "pendingRemove")]
        [InlineData(typeof(OniMoono.Engine.IMGUI.GUIManager), null, "byBehaviour")]
        [InlineData(typeof(TransformMoveWatch), null, "tokens", "queued")]
        [InlineData(typeof(OniMoono.Engine.UI.CanvasSystem), null, "movedSet")]
        [InlineData(typeof(OniMoono.Engine.Rendering.FrameRenderer), null, "visibleThisFrame", "visibleLastFrame")]
        public void TheCollectionComparesByReference(Type owner, string holder, params string[] fields)
        {
            object target = null;
            if (holder != null) target = owner.GetField(holder, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            foreach (var name in fields)
            {
                var field = (target?.GetType() ?? owner).GetField(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(field);
                var collection = field.GetValue(target);
                var comparer = collection.GetType().GetProperty("Comparer").GetValue(collection);
                Assert.Same(ReferenceEqualityComparer.Instance, comparer);
            }
        }
    }
}
