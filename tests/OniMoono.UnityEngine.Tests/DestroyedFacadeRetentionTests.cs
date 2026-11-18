using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// The game keeps destroyed objects' facades (fake null) for as long as it likes, in fields,
    /// lists and closures. A facade it keeps must not keep the destroyed object's engine state
    /// alive: its record, its game object's other components, the children, their records, a
    /// coroutine. Today a destroyed component keeps only its cached game object and transform
    /// facades (Component.m_GameObject, m_Transform); the records go with Unbind. Once the
    /// records are merged into the facades, Unbind must clear what they held
    /// (docs/research/facade-merge.md, category 1).
    public class DestroyedFacadeRetentionTests : EngineTest
    {
        public class Held : MonoBehaviour
        {
            public int Updates;
            void Update() => Updates++;
            public IEnumerator Waits()
            {
                while (true) yield return null;
            }
        }

        public class Other : MonoBehaviour { }

        /// Weak references to everything a destroyed object had, but the kept facades.
        sealed class Watched
        {
            public readonly List<(string What, WeakReference Ref)> Refs = new();
            public void Add(string what, object o) => Refs.Add((what, new WeakReference(o)));
        }

        public enum Kept { Behaviour, GameObject, Transform, ChildTransform }

        /// Builds a parent with components, a child with its own and a grandchild, starts a
        /// coroutine and destroys the parent, keeping only the facade asked for. Not inlined, so no
        /// local of it outlives the call.
        [MethodImpl(MethodImplOptions.NoInlining)]
        Object Build(Kept kept, bool immediate, Watched watched)
        {
            var parent = new GameObject("parent");
            var held = parent.AddComponent<Held>();
            var other = parent.AddComponent<Other>();
            var collider = parent.AddComponent<BoxCollider2D>();
            var child = new GameObject("child");
            child.transform.SetParent(parent.transform);
            var childOther = child.AddComponent<Other>();
            var grandchild = new GameObject("grandchild");
            grandchild.transform.SetParent(child.transform);
            held.StartCoroutine(held.Waits());
            // The caches a game fills by reading them.
            _ = held.gameObject;
            _ = held.transform;
            _ = childOther.transform;
            Frame();

            Object keep = kept switch
            {
                Kept.Behaviour => held,
                Kept.GameObject => parent,
                Kept.Transform => parent.transform,
                _ => child.transform,
            };
            foreach (var o in new Object[] { parent, held, other, collider, child, childOther, grandchild, parent.transform, child.transform, grandchild.transform })
            {
                // Scripts and plain components have no record: their state is on them.
                if (o.NativeRecord is { } record) watched.Add($"{o.name}'s {o.GetType().Name} record", record);
                if (!ReferenceEquals(o, keep) && !Allowed(keep, o)) watched.Add($"{o.name}'s {o.GetType().Name}", o);
            }
            if (immediate) Object.DestroyImmediate(parent);
            else
            {
                Object.Destroy(parent);
                Frame();
            }
            Assert.False(keep);
            return keep;
        }

        /// What a kept facade may hold of the destroyed object, as today: a component its cached
        /// game object and transform.
        static bool Allowed(Object keep, Object o) => keep is Component c && (ReferenceEquals(o, c.gameObjectCached()) || ReferenceEquals(o, c.transformCached()));

        [Theory]
        [InlineData(Kept.Behaviour, false)]
        [InlineData(Kept.Behaviour, true)]
        [InlineData(Kept.GameObject, false)]
        [InlineData(Kept.GameObject, true)]
        [InlineData(Kept.Transform, true)]
        [InlineData(Kept.ChildTransform, true)]
        public void AKeptDestroyedFacadeKeepsNothingElse(Kept kept, bool immediate)
        {
            var watched = new Watched();
            var keep = Build(kept, immediate, watched);
            // Enough frames for the engine's own per-frame lists to pass their copies on.
            for (int i = 0; i < 3; i++) Frame();
            // The physics world drops destroyed colliders at its next query, which the game makes
            // every frame (the cursor's raycast); until then it holds the whole hierarchy.
            OniMoono.Engine.PhysicsWorld.Snapshot();
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            var alive = new List<string>();
            foreach (var (what, r) in watched.Refs)
                if (r.IsAlive) alive.Add(what);
            Assert.True(alive.Count == 0, $"kept {kept}: still alive: {string.Join(", ", alive)}");
            GC.KeepAlive(keep);
        }
    }

    static class CachedFacades
    {
        static readonly System.Reflection.FieldInfo gameObject = typeof(Component).GetField("m_GameObject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        static readonly System.Reflection.FieldInfo transform = typeof(Component).GetField("m_Transform", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        public static object gameObjectCached(this Component c) => gameObject.GetValue(c);
        public static object transformCached(this Component c) => transform.GetValue(c);
    }
}
