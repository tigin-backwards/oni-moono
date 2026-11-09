using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using OniMoono.Engine.Serialization;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// Instantiate copies serialized structs, and arrays and lists of composites and references,
    /// through code compiled per type rather than boxed values and reflection. The clones and the
    /// sources come out as the reflective copy (kept below as the reference) made them.
    public class InstantiateTypedCopyTests : EngineTest
    {
        public enum Mode { Off, On, Both }

        [Serializable]
        public struct Leaf
        {
            public int i;
            public string s;
            public Mode mode;
            public Vector3 v;
            public GameObject go;
            public float f;
        }

        [Serializable]
        public struct Counted : ISerializationCallbackReceiver
        {
            public int before, after, value;
            public void OnBeforeSerialize() { before++; value *= 2; }
            public void OnAfterDeserialize() => after++;
        }

        [Serializable]
        public class Inner
        {
            public int n = 7;
            public Leaf leaf = new() { i = 3, s = "init" };
            public string text;
            public List<int> numbers;
            public Counted counted;
        }

        [Serializable]
        public struct Middle
        {
            public Leaf leaf;
            public Leaf[] leaves;
            public List<Leaf> leafList;
            public int[] ints;
            public List<int> intList;
            public string[] texts;
            public List<string> textList;
            public Inner inner;
            public Inner[] inners;
            public GameObject[] objects;
            public List<Transform> transforms;
            public AnimationCurve curve;
            public Counted counted;
        }

        [Serializable]
        public class Nest<T>
        {
            public T inner;
            public int depth;
        }

        public class Holder : MonoBehaviour
        {
            public Middle middle;
            public Middle[] middles;
            public List<Middle> middleList;
            public Leaf leaf;
            public Inner inner;
            public Inner missing;
            public List<Inner> innerList;
            public Counted counted;
            public Nest<Nest<Nest<Nest<Nest<Nest<Nest<Nest<Nest<Nest<Nest<Nest<int>>>>>>>>>>>> deep;
            [NonSerialized] public int unserialized = 5;
        }

        static void Fill(Holder h, GameObject own, GameObject outside)
        {
            Leaf L(int i) => new() { i = i, s = i % 3 == 0 ? null : "s" + i, mode = (Mode)(i % 3), v = new Vector3(i, -i, 0.5f * i), go = i % 2 == 0 ? own : outside, f = i * 0.25f };
            Middle M(int i) => new()
            {
                leaf = L(i),
                leaves = i == 2 ? null : [L(i + 1), L(i + 2)],
                leafList = i == 1 ? null : [L(i + 3)],
                ints = [i, i + 1],
                intList = i == 3 ? null : [i * 10],
                texts = [null, "t" + i],
                textList = [null],
                inner = i == 1 ? null : new Inner { n = i, text = null, numbers = null, counted = new Counted { value = i } },
                inners = [null, new Inner { n = 40 + i }],
                objects = [own, outside, null],
                transforms = [own.transform, outside.transform],
                curve = i == 2 ? null : AnimationCurve.Linear(0f, i, 1f, 2 * i),
                counted = new Counted { value = 100 + i },
            };
            h.middle = M(1);
            h.middles = [M(2), M(3)];
            h.middleList = [M(4)];
            h.leaf = L(5);
            h.inner = new Inner { leaf = L(6), numbers = [1, 2, 3] };
            h.innerList = [null, new Inner()];
            h.counted = new Counted { value = 21 };
            h.deep = new();
            h.unserialized = 9;
        }

        [Fact]
        public void ClonesAndSourcesAreThoseOfTheReflectiveCopy()
        {
            var outside = new GameObject("outside");
            var prefab = new GameObject("prefab");
            var source = prefab.AddComponent<Holder>();
            Fill(source, prefab, outside);
            var clone = Object.Instantiate(prefab).GetComponent<Holder>();

            var prefab2 = new GameObject("prefab");
            var source2 = prefab2.AddComponent<Holder>();
            Fill(source2, prefab2, outside);
            var cloneObject = new GameObject("clone");
            var expected = cloneObject.AddComponent<Holder>();
            var remap = new Dictionary<Object, Object>(ReferenceEqualityComparer.Instance)
            {
                [prefab2] = cloneObject,
                [prefab2.transform] = cloneObject.transform,
                [source2] = expected,
            };
            Reference.CopyScript(source2, expected, remap);

            // Clones: references to the prefab are to the clone, others are kept.
            SameScript(expected, clone, "clone", (e, a) => e == (object)cloneObject ? a == (object)clone.gameObject
                : e == (object)cloneObject.transform ? a == (object)clone.transform : ReferenceEquals(e, a));
            // Sources: null serializable classes got instances of their own; struct callbacks ran on copies.
            SameScript(source2, source, "source", (e, a) => e == (object)prefab2 ? a == (object)prefab
                : e == (object)prefab2.transform ? a == (object)prefab.transform : ReferenceEquals(e, a));

            // A few values spelled out.
            Assert.Equal(1, clone.counted.before);
            Assert.Equal(1, clone.counted.after);
            Assert.Equal(42, clone.counted.value);
            Assert.Equal(0, source.counted.before);
            Assert.Equal(21, source.counted.value);
            Assert.Equal("s5", clone.leaf.s);
            Assert.Null(source.middles[1].leaf.s);
            Assert.Equal("", clone.middles[1].leaf.s);
            Assert.Same(clone.gameObject, clone.middle.objects[0]);
            Assert.Same(outside, clone.middle.objects[1]);
            Assert.Equal(7, clone.middle.inners[0].n);
            Assert.Equal(3, clone.middle.inners[0].leaf.i);
            Assert.NotNull(source.missing);
            Assert.NotSame(source.missing, clone.missing);
            Assert.Equal(5, clone.unserialized);
            Assert.Empty(clone.middles[0].leaves);
        }

        // A struct field's copy boxes nothing.
        [Fact]
        public void CopyingAStructAllocatesNothing()
        {
            var remap = new Dictionary<Object, Object>(ReferenceEqualityComparer.Instance);
            var leaf = new Leaf { i = 4, s = "x", mode = Mode.On, v = Vector3.one };
            Cloner.CopyComposite(leaf, remap, 1);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Leaf copy = default;
            for (int i = 0; i < 1000; i++) copy = Cloner.CopyComposite(leaf, remap, 1);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(leaf.s, copy.s);
            Assert.Equal(leaf.v, copy.v);

            // Nor its callbacks (the game's Tag and HashedString have them).
            var counted = new Counted { value = 3 };
            Cloner.CopyComposite(counted, remap, 1);
            before = GC.GetAllocatedBytesForCurrentThread();
            Counted copied = default;
            for (int i = 0; i < 1000; i++) copied = Cloner.CopyComposite(counted, remap, 1);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal((1, 1, 6), (copied.before, copied.after, copied.value));
            Assert.Equal((0, 0, 3), (counted.before, counted.after, counted.value));
        }

        [Serializable]
        public struct Throwing : ISerializationCallbackReceiver
        {
            public int value;
            public void OnBeforeSerialize() => throw new InvalidOperationException("before");
            public void OnAfterDeserialize() { value++; throw new InvalidOperationException("after"); }
        }

        // A callback that throws is logged, and the copy goes on, as with the reflective copy.
        [Fact]
        public void AStructCallbackThatThrowsIsLogged()
        {
            var logged = new List<string>();
            void Log(string message, string trace, LogType type) { if (type == LogType.Exception) logged.Add(message); }
            Application.logMessageReceived += Log;
            try
            {
                var copy = Cloner.CopyComposite(new Throwing { value = 4 }, new Dictionary<Object, Object>(ReferenceEqualityComparer.Instance), 1);
                Assert.Equal(5, copy.value);
            }
            finally
            {
                Application.logMessageReceived -= Log;
            }
            Assert.Equal(2, logged.Count);
            Assert.Contains("before", logged[0]);
            Assert.Contains("after", logged[1]);
        }

        static void SameScript(Component expected, Component actual, string path, Func<object, object, bool> sameObject)
        {
            var type = expected.GetType();
            foreach (var f in SerializedLayout.ScriptFields(type)) Same(f.Field.GetValue(expected), f.Field.GetValue(actual), $"{path}.{f.Name}", sameObject);
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (f.IsDefined(typeof(NonSerializedAttribute))) Same(f.GetValue(expected), f.GetValue(actual), $"{path}.{f.Name}", sameObject);
        }

        static void Same(object expected, object actual, string path, Func<object, object, bool> sameObject)
        {
            if (expected == null || actual == null)
            {
                Assert.True(expected == null && actual == null, $"{path}: {expected ?? "null"} vs {actual ?? "null"}");
                return;
            }
            var type = expected.GetType();
            Assert.True(type == actual.GetType(), $"{path}: {type} vs {actual.GetType()}");
            if (expected is Object && type != typeof(AnimationCurve))
            {
                Assert.True(sameObject(expected, actual), $"{path}: {expected} vs {actual}");
                return;
            }
            if (type.IsPrimitive || type.IsEnum || expected is string)
            {
                Assert.True(expected.Equals(actual), $"{path}: {expected} vs {actual}");
                return;
            }
            if (expected is AnimationCurve ec)
            {
                var ac = (AnimationCurve)actual;
                Assert.Equal(ec.keys, ac.keys);
                return;
            }
            if (expected is IList el)
            {
                var al = (IList)actual;
                Assert.True(el.Count == al.Count, $"{path}: {el.Count} vs {al.Count} items");
                if (expected is not Array) Assert.Equal(expected.GetType().GetProperty("Capacity").GetValue(expected), actual.GetType().GetProperty("Capacity").GetValue(actual));
                for (int i = 0; i < el.Count; i++) Same(el[i], al[i], $"{path}[{i}]", sameObject);
                return;
            }
            for (var t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    Same(f.GetValue(expected), f.GetValue(actual), $"{path}.{f.Name}", sameObject);
        }

        /// The reflective copy Instantiate made before the typed one.
        static class Reference
        {
            public static void CopyScript(Object src, Object dst, Dictionary<Object, Object> remap)
            {
                foreach (var f in SerializedLayout.ScriptFields(src.GetType()))
                {
                    var value = f.Field.GetValue(src);
                    if (f.Shape.Kind == ValueKind.Composite && !f.Shape.IsValueType)
                    {
                        value ??= Cloner.NewInstance(f.Shape.Type);
                        f.Field.SetValue(src, value);
                    }
                    f.Field.SetValue(dst, CopyValue(value, f.Shape, remap, 1));
                }
            }

            static object CopyValue(object value, ValueShape shape, Dictionary<Object, Object> remap, int depth)
            {
                switch (shape.Kind)
                {
                    case ValueKind.ObjectReference:
                        if (value is Object o && remap.TryGetValue(o, out var mapped)) return mapped;
                        return value;
                    case ValueKind.BuiltinClass:
                    {
                        var src = value as AnimationCurve;
                        var dst = new AnimationCurve();
                        if (src != null)
                        {
                            dst.keys = src.keys;
                            dst.preWrapMode = src.preWrapMode;
                            dst.postWrapMode = src.postWrapMode;
                        }
                        return dst;
                    }
                    case ValueKind.Composite:
                        return CopyComposite(value, shape, remap, depth);
                    case ValueKind.Array:
                    {
                        var src = (Array)value;
                        int n = src?.Length ?? 0;
                        var dst = Array.CreateInstance(shape.Element.Type, n);
                        for (int i = 0; i < n; i++) dst.SetValue(CopyValue(src.GetValue(i), shape.Element, remap, depth), i);
                        return dst;
                    }
                    case ValueKind.List:
                    {
                        var src = (IList)value;
                        var dst = (IList)Activator.CreateInstance(shape.Type);
                        if (src != null)
                            foreach (var item in src) dst.Add(CopyValue(item, shape.Element, remap, depth));
                        return dst;
                    }
                    case ValueKind.String:
                        return value ?? "";
                    default:
                        return value;
                }
            }

            static object CopyComposite(object value, ValueShape shape, Dictionary<Object, Object> remap, int depth)
            {
                if (depth > SerializedLayout.MaxDepth) return shape.IsValueType ? Activator.CreateInstance(shape.Type) : null;
                var dst = Cloner.NewInstance(shape.Type);
                if (value != null) SerializedLayout.BeforeSerialize(value);
                foreach (var f in SerializedLayout.CompositeFields(shape))
                {
                    var fieldValue = value == null ? null : f.Field.GetValue(value);
                    if (fieldValue == null && value != null && !shape.IsValueType && f.Shape.Kind == ValueKind.Composite && !f.Shape.IsValueType && depth + 1 <= SerializedLayout.MaxDepth)
                    {
                        fieldValue = Cloner.NewInstance(f.Shape.Type);
                        f.Field.SetValue(value, fieldValue);
                    }
                    if (value == null && f.Shape.IsValueType) fieldValue = f.Field.GetValue(dst);
                    f.Field.SetValue(dst, CopyValue(fieldValue, f.Shape, remap, depth + 1));
                }
                SerializedLayout.AfterDeserialize(dst);
                return dst;
            }
        }
    }
}
