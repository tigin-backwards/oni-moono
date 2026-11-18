using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// The game's Localization.SetFont (Assembly-CSharp, U59 and U60) walks an object's fields with
    /// Instance | Public | NonPublic | FlattenHierarchy and no visited set: a GameObject-typed
    /// field sends it into every component of that object by its actual type, any other field
    /// whose type differs from its declaring class into the field's declared type with the
    /// field's value. A field the engine adds to a facade is seen by that walk, so the facades
    /// keep three rules (docs/research/facade-merge.md, category 5):
    ///
    /// 1. The fields of Object, Component, Behaviour and MonoBehaviour are private: a derived
    ///    type's walk does not see them.
    /// 2. No facade but Component declares a GameObject-typed field (Component.m_GameObject, the
    ///    cached gameObject, is the one).
    /// 3. A walk from a facade type with a live object never comes back to an object on its own
    ///    path, which would recurse until the stack overflows.
    public class SetFontWalkTests : EngineTest
    {
        const BindingFlags Walked = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        static IEnumerable<Type> Facades() => typeof(Object).Assembly.GetTypes()
            .Where(t => t.IsPublic && typeof(Object).IsAssignableFrom(t) && t.Namespace?.StartsWith("UnityEngine", StringComparison.Ordinal) == true);

        [Fact]
        public void TheBaseClassesFieldsArePrivate()
        {
            foreach (var type in new[] { typeof(Object), typeof(Component), typeof(Behaviour), typeof(MonoBehaviour) })
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    Assert.True(f.IsPrivate, $"{type.Name}.{f.Name} is not private: SetFont's walk of every derived type sees it");
        }

        [Fact]
        public void OnlyComponentDeclaresAGameObjectField()
        {
            var found = new List<string>();
            foreach (var type in Facades())
                foreach (var f in type.GetFields(Walked))
                    if (f.FieldType == typeof(GameObject) && !(f.DeclaringType == typeof(Component) && f.Name == "m_GameObject"))
                        found.Add($"{type.Name} sees {f.DeclaringType.Name}.{f.Name}");
            Assert.Empty(found);
        }

        /// What the walks find, each by the declared type it starts from. The game walks only its
        /// confirm dialog's fields (LanguageOptionsScreen), which reach neither
        /// (tests/e2e/translation-screen.yaml), but a field declared as Component would. Both come
        /// from a field Unity's facades do not have: Component.m_GameObject is filled on the first
        /// gameObject read, so until then a walk of a component as Component finds it null and
        /// throws (after it, the walk would go round the object's components back to the start).
        /// The component's other engine fields are declared after it, so the walk meets it first.
        /// A walk of an object that has a record (a transform, an engine component) as
        /// UnityEngine.Object goes from the record (Object.m_Native) back to the object and
        /// overflows the stack. Scripts have no record since step 2 of the merge and GameObjects
        /// since step 3 (before them, those walked as Object did the same).
        static readonly string[] KnownCycles =
        [
            "Component: throws, a null GameObject: Component.m_GameObject",
            "WalkedBehaviour: throws, a null GameObject: WalkedBehaviour.other -> Component.m_GameObject",
        ];

        [Fact]
        public void TheWalksFromTheFacadesFindOnlyWhatIsKnown()
        {
            var go = new GameObject("walked");
            var child = new GameObject("child");
            child.transform.SetParent(go.transform);
            var roots = new List<(Type Declared, object Value)>();
            foreach (var type in Facades().Where(t => typeof(Component).IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters))
            {
                if (type == typeof(Transform) || type == typeof(RectTransform)) continue;
                var holder = new GameObject(type.Name);
                holder.transform.SetParent(go.transform);
                var c = holder.AddComponent(type);
                if (c) roots.Add((type, c));
            }
            Assert.True(roots.Count > 20, $"{roots.Count} components added");
            // A GameObject-typed field: SetFont walks each component by its actual type.
            foreach (var c in go.GetComponents<Component>()) roots.Add((c.GetType(), c));
            roots.Add((typeof(Transform), child.transform));
            var rect = new GameObject("rect", typeof(RectTransform));
            rect.transform.SetParent(go.transform);
            roots.Add((typeof(RectTransform), rect.transform));
            var behaviour = go.AddComponent<WalkedBehaviour>();
            // The base types a game field may be declared as.
            foreach (var declared in new[] { typeof(Object), typeof(Component), typeof(Behaviour), typeof(MonoBehaviour), typeof(WalkedBehaviour) })
                roots.Add((declared, behaviour));
            // Objects with a record, as UnityEngine.Object.
            roots.Add((typeof(Object), go));
            roots.Add((typeof(Object), child.transform));

            var cycles = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (declared, value) in roots)
            {
                var walk = new Walk();
                try
                {
                    walk.Run(declared, value, []);
                }
                catch (Thrown)
                {
                    // SetFont throws out of the whole walk there.
                }
                foreach (var cycle in walk.Cycles) cycles.Add($"{declared.Name}: {cycle}");
                Assert.True(walk.Visits < Walk.MaxVisits, $"the walk from {declared.Name} visited {walk.Visits} objects");
            }
            Assert.True(KnownCycles.SequenceEqual(cycles), "the walks found:\n" + string.Join("\n", cycles));
        }

        public class WalkedBehaviour : MonoBehaviour
        {
            public Transform target;
            Component other;
            void Awake() => other = transform;
        }

        sealed class Thrown : Exception { }

        /// SetFont's walk, recording instead of recursing where it would never end, and ending
        /// where SetFont throws.
        sealed class Walk
        {
            public const int MaxVisits = 200_000;
            public int Visits;
            public readonly SortedSet<string> Cycles = new(StringComparer.Ordinal);
            /// The objects on the path, with the trail's length where each was entered.
            readonly Dictionary<object, int> path = new(ReferenceEqualityComparer.Instance);

            public void Run(Type type, object target, List<string> trail)
            {
                // SetFont returns at a null target (a reference test) and a null font.
                if (type == null || target == null || ++Visits >= MaxVisits) return;
                bool tracked = !target.GetType().IsValueType;
                if (tracked && path.TryGetValue(target, out int entered))
                {
                    // Back at an object on the path: SetFont would recurse until the stack
                    // overflows. Named by the fields from that object round to it again.
                    Cycles.Add($"{target.GetType().Name}: {string.Join(" -> ", trail.Skip(entered))}");
                    return;
                }
                if (tracked) path[target] = trail.Count;
                try
                {
                    foreach (var f in type.GetFields(Walked))
                    {
                        if (f.FieldType.IsPointer || f.FieldType.IsByRefLike) continue;
                        var step = new List<string>(trail) { $"{f.DeclaringType.Name}.{f.Name}" };
                        if (f.FieldType == typeof(GameObject))
                        {
                            var go = (GameObject)f.GetValue(target);
                            // SetFont throws on a null or destroyed one (GetComponents).
                            if (!go)
                            {
                                Cycles.Add($"throws, a null GameObject: {string.Join(" -> ", step)}");
                                throw new Thrown();
                            }
                            foreach (var c in go.GetComponents<Component>()) Run(c.GetType(), c, step);
                        }
                        else if (f.FieldType != f.DeclaringType)
                            Run(f.FieldType, f.GetValue(target), step);
                    }
                }
                finally
                {
                    if (tracked) path.Remove(target);
                }
            }
        }
    }
}
