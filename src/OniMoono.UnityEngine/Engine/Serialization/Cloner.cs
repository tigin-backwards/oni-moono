using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Engine.Serialization
{
    /// Object.Instantiate: a copy shaped by serialization, as in Unity.
    ///
    /// - Only serialized fields are copied (SerializedLayout); other fields of the clone keep the
    ///   values its constructor and field initializers gave them.
    /// - References to objects inside the cloned hierarchy point to their clones; other references
    ///   are kept.
    /// - Values are copied by their declared type: a null serializable class becomes a new
    ///   instance, null arrays and lists become empty ones, a null string becomes "" (the
    ///   source keeps its null), and a subclass instance in a field of a
    ///   base type becomes an instance of the base type (no polymorphism without
    ///   [SerializeReference]).
    /// - ISerializationCallbackReceiver: OnBeforeSerialize on the originals, OnAfterDeserialize on
    ///   the clones.
    /// - The cloned hierarchy gets Awake/OnEnable before Instantiate returns if it is active.
    internal static class Cloner
    {
        public static Object Instantiate(Object original, Transform parent, bool worldPositionStays, Vector3? position, Quaternion? rotation)
        {
            if (original == null) throw new ArgumentException("The Object you want to instantiate is null.");
            Threads.AssertMainThread("Instantiate");
            Platform.MainThreadPump.Maybe();
            if (original is GameObject || original is Component) return InstantiateHierarchy(original, parent, worldPositionStays, position, rotation);
            if (original is ScriptableObject so) return CloneScriptableObject(so);
            var clone = original.EngineCloneAsset();
            if (clone == null) throw new InvalidOperationException($"Instantiate is not available for {original.GetType().FullName} objects in this engine.");
            clone.NativeOrThrow.Name = original.name + "(Clone)";
            return clone;
        }

        static Object CloneScriptableObject(ScriptableObject original)
        {
            var native = new NativeObject { Name = original.name + "(Clone)", HideFlags = original.hideFlags };
            var clone = Object.ConstructBound<ScriptableObject>(original.GetType(), native);
            var context = CloneContext.Rent();
            try
            {
                context.Remap[original] = clone;
                CopyScript(original, clone, context.Remap);
            }
            finally
            {
                context.Return();
            }
            ScriptableObjectLifecycle.Created(clone);
            return clone;
        }

        static Object InstantiateHierarchy(Object original, Transform parent, bool worldPositionStays, Vector3? position, Quaternion? rotation)
        {
            var context = CloneContext.Rent();
            try
            {
                return InstantiateHierarchy(original, parent, worldPositionStays, position, rotation, context);
            }
            finally
            {
                context.Return();
            }
        }

        /// The clone map of one Instantiate, with its remapping function made once, kept for the
        /// next Instantiate (each nested one, from a callback or Awake, takes another): the game
        /// instantiates continuously, and each call made a dictionary, its entries and a
        /// delegate for every engine component.
        sealed class CloneContext
        {
            public readonly Dictionary<Object, Object> Remap = new(ReferenceEqualityComparer.Instance);
            public readonly Func<Object, Object> Map;

            CloneContext() => Map = o => o is not null && Remap.TryGetValue(o, out var mapped) ? mapped : o;

            static readonly Stack<CloneContext> free = new();

            /// Main thread (Instantiate asserts it).
            public static CloneContext Rent() => free.Count > 0 ? free.Pop() : new CloneContext();

            /// Empty again (Clear keeps insertion order for the next use, as a new dictionary has);
            /// one grown large by a big hierarchy is dropped rather than cleared each time.
            public void Return()
            {
                if (Remap.Count > 4096 || free.Count >= 8) return;
                Remap.Clear();
                free.Push(this);
            }
        }

        static Object InstantiateHierarchy(Object original, Transform parent, bool worldPositionStays, Vector3? position, Quaternion? rotation, CloneContext context)
        {
            var rootOriginal = original as GameObject ?? ((Component)original).gameObject;
            var rootNative = (GameObjectNative)rootOriginal.NativeOrThrow;
            var remap = context.Remap;

            // 1. Structure: GameObjects, transforms and components, without lifecycle calls.
            var rootClone = CloneStructure(rootNative, null, remap);
            rootClone.Name = rootNative.Name + "(Clone)";

            // 2. Serialized state of scripts, with references remapped into the clone; references
            //    held by engine components are remapped the same way.
            foreach (var (src, dst) in remap)
                if (src is MonoBehaviour && dst is MonoBehaviour) CopyScript(src, dst, remap);
            foreach (var dst in remap.Values)
                if (dst is Component && dst is not MonoBehaviour) dst.NativeOrThrow.RemapReferences(context.Map);

            // 3. Placement.
            var t = rootClone.Transform;
            var originalTransform = rootNative.Transform;
            if (position.HasValue)
            {
                t.LocalPosition = position.Value;
                t.LocalRotation = rotation ?? Quaternion.identity;
            }
            TransformNative parentNative = null;
            if (parent != null)
            {
                var p = parentNative = parent.TransformNative;
                bool keepWorld = position.HasValue || worldPositionStays;
                if (keepWorld)
                {
                    var worldPos = position ?? Hierarchy.Position(originalTransform);
                    var worldRot = rotation ?? originalTransform.WorldRotation;
                    var worldScale = Hierarchy.LossyScale(originalTransform);
                    t.LocalPosition = p.LocalToWorld.inverse.MultiplyPoint3x4(worldPos);
                    t.LocalRotation = Quaternion.Inverse(p.WorldRotation) * worldRot;
                    var ps = Hierarchy.LossyScale(p);
                    t.LocalScale = new Vector3(ps.x == 0 ? 0 : worldScale.x / ps.x, ps.y == 0 ? 0 : worldScale.y / ps.y, ps.z == 0 ? 0 : worldScale.z / ps.z);
                }
                AttachUnder(t, p, keepWorld);
                Scenes.SetSceneRecursive(rootClone, p.GameObject.Scene);
            }
            else
            {
                var scene = Scenes.Active;
                if (scene != null)
                {
                    Scenes.AddRoot(scene, rootClone);
                    Scenes.SetSceneRecursive(rootClone, scene);
                }
            }
            t.MarkDirty(false);
            if (t is RectTransformNative rt) rt.ParentChangedInternal(position.HasValue || (parent != null && worldPositionStays));

            // 4. Lifecycle.
            Lifecycle.UpdateActiveInHierarchy(rootClone);
            // The parent's child list changed (measured in the original player; the order against
            // the clone's Awake is not).
            if (parentNative is { GameObject: { Destroyed: false } parentObject }) Messages.SendToComponents(parentObject, s => s.OnTransformChildrenChanged);

            return remap[original];
        }

        static void AttachUnder(TransformNative child, TransformNative parent, bool keepWorld)
        {
            parent.InsertChildAt(child, parent.Children.Length);
            child.Parent = parent;
            Hierarchy.Touch(parent);
        }

        static GameObjectNative CloneStructure(GameObjectNative src, TransformNative parentClone, Dictionary<Object, Object> remap)
        {
            var go = new GameObjectNative
            {
                Name = src.Name,
                HideFlags = src.HideFlags,
                Layer = src.Layer,
                Tag = src.Tag,
                ActiveSelf = src.ActiveSelf,
                IsStatic = src.IsStatic,
            };
            var goManaged = new GameObject(go);
            remap[src.Managed] = goManaged;

            var components = src.Components;
            var clones = new Component[components.Length];
            for (int i = 0; i < components.Length; i++)
            {
                var c = components[i];
                var srcNative = (ComponentNative)c.NativeOrThrow;
                var native = ComponentNatives.Create(c.GetType());
                native.GameObject = go;
                native.HideFlags = srcNative.HideFlags;
                native.CopyStateFrom(srcNative);
                if (native is BehaviourNative bn && c is MonoBehaviour)
                {
                    bn.Script = ScriptInfo.Get(c.GetType());
                    bn.ExecutionOrder = bn.Script.ExecutionOrder;
                }
                clones[i] = Object.ConstructBound<Component>(c.GetType(), native);
                remap[c] = clones[i];
            }
            go.Components = clones;
            go.Transform = (TransformNative)clones[0].NativeOrThrow;
            go.Transform.Parent = parentClone;

            var srcChildren = src.Transform.Children;
            var childClones = new TransformNative[srcChildren.Length];
            for (int i = 0; i < srcChildren.Length; i++)
                childClones[i] = CloneStructure(srcChildren[i].GameObject, go.Transform, remap).Transform;
            go.Transform.SetChildren(childClones);
            return go;
        }

        /// Copies the serialized fields of a MonoBehaviour or ScriptableObject.
        static void CopyScript(Object src, Object dst, Dictionary<Object, Object> remap)
        {
            if (src is ISerializationCallbackReceiver before)
            {
                try { before.OnBeforeSerialize(); }
                catch (Exception e) { Debug.LogException(e, src); }
            }
            ScriptCopier(src.GetType())(src, dst, remap);
            if (dst is ISerializationCallbackReceiver after)
            {
                try { after.OnAfterDeserialize(); }
                catch (Exception e) { Debug.LogException(e, dst); }
            }
        }

        static readonly ConcurrentDictionary<Type, Action<object, object, Dictionary<Object, Object>>> scriptCopiers = new();

        /// A script type's field copy, compiled once: Instantiate copies every serialized field of
        /// every script in the hierarchy, and the game instantiates continuously (resource chunks,
        /// splits). It copies field by field, in field order: values (primitives,
        /// strings, enums, built-in structs) and arrays and lists of them are copied without
        /// boxing, references are remapped, everything else goes through CopyExpression.
        static Action<object, object, Dictionary<Object, Object>> ScriptCopier(Type type)
            => scriptCopiers.GetOrAdd(type, static t => CompileScriptCopier(t));

        static Action<object, object, Dictionary<Object, Object>> CompileScriptCopier(Type type)
        {
            var srcArg = Expression.Parameter(typeof(object), "src");
            var dstArg = Expression.Parameter(typeof(object), "dst");
            var remapArg = Expression.Parameter(typeof(Dictionary<Object, Object>), "remap");
            var src = Expression.Variable(type, "s");
            var dst = Expression.Variable(type, "d");
            var body = new List<Expression>
            {
                Expression.Assign(src, Expression.Convert(srcArg, type)),
                Expression.Assign(dst, Expression.Convert(dstArg, type)),
            };
            foreach (var f in SerializedLayout.ScriptFields(type))
            {
                var field = f.Field;
                var read = Expression.Field(src, field);
                Expression value = f.Shape.Kind switch
                {
                    ValueKind.ObjectReference => Expression.Convert(Expression.Call(typeof(Cloner).GetMethod(nameof(Remap), BindingFlags.Static | BindingFlags.NonPublic), Expression.Convert(read, typeof(Object)), remapArg), field.FieldType),
                    ValueKind.Array when f.Shape.Element.Kind == ValueKind.String => Expression.Call(typeof(Cloner).GetMethod(nameof(CopyStringArray), BindingFlags.Static | BindingFlags.NonPublic), read),
                    ValueKind.List when f.Shape.Element.Kind == ValueKind.String => Expression.Call(typeof(Cloner).GetMethod(nameof(CopyStringList), BindingFlags.Static | BindingFlags.NonPublic), read),
                    ValueKind.Array when IsPlainValue(f.Shape.Element) => Expression.Call(typeof(Cloner).GetMethod(nameof(CopyPlainArray), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(field.FieldType.GetElementType()), read),
                    ValueKind.List when IsPlainValue(f.Shape.Element) => Expression.Call(typeof(Cloner).GetMethod(nameof(CopyPlainList), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(field.FieldType.GetGenericArguments()[0]), read),
                    ValueKind.String => Expression.Coalesce(read, Expression.Constant("")),
                    _ when IsPlainValue(f.Shape) => read,
                    // A null serializable class becomes an instance in the source too (below).
                    ValueKind.Composite when !f.Shape.IsValueType => CopyExpression(f.Shape,
                        Expression.Assign(read, Expression.Convert(Expression.Call(typeof(Cloner).GetMethod(nameof(SourceInstance), BindingFlags.Static | BindingFlags.NonPublic),
                            Expression.Convert(read, typeof(object)), Expression.Constant(f.Shape)), field.FieldType)), remapArg, Expression.Constant(1)),
                    _ => CopyExpression(f.Shape, read, remapArg, Expression.Constant(1)),
                };
                body.Add(Expression.Assign(Expression.Field(dst, field), value));
            }
            return Expression.Lambda<Action<object, object, Dictionary<Object, Object>>>(Expression.Block([src, dst], body), srcArg, dstArg, remapArg).Compile();
        }

        /// Values copied as they are.
        static bool IsPlainValue(ValueShape shape) => shape.Kind is not (ValueKind.ObjectReference or ValueKind.BuiltinClass or ValueKind.Composite or ValueKind.Array or ValueKind.List);

        static Object Remap(Object value, Dictionary<Object, Object> remap)
            => value is not null && remap.TryGetValue(value, out var mapped) ? mapped : value;

        static T[] CopyPlainArray<T>(T[] src) => src == null || src.Length == 0 ? [] : (T[])src.Clone();

        static List<T> CopyPlainList<T>(List<T> src) => src == null ? [] : new List<T>(src);

        // Unity's serializer has no null string: it writes one as "", measured in the original
        // player (issue #17: WannaPrint's side screen, added with AddComponent, keeps a null
        // titleKey and its clone reads "").
        static string[] CopyStringArray(string[] src)
        {
            if (src == null || src.Length == 0) return [];
            var dst = new string[src.Length];
            for (int i = 0; i < src.Length; i++) dst[i] = src[i] ?? "";
            return dst;
        }

        static List<string> CopyStringList(List<string> src)
        {
            if (src == null) return [];
            var dst = new List<string>(src.Count);
            foreach (var s in src) dst.Add(s ?? "");
            return dst;
        }

        /// Unity's serializer has no null for a serializable class: serializing a null one
        /// (Instantiate serializes its source) gives the source a new instance of its own, as
        /// measured in the original player (PerfProbe measure.txt).
        static object SourceInstance(object value, ValueShape shape) => value ?? NewInstance(shape.Type);

        /// A copy of a serialized value nested in a script field, by its declared type, as
        /// Unity's serializer makes one: references remapped, a null string as "", a null array
        /// or list as an empty new one, composites through CompositeCopier. Nothing is boxed:
        /// the game's structs (Tag, HashedString, CellOffset) and the values in them were boxed
        /// and copied through reflection field by field.
        static Expression CopyExpression(ValueShape shape, Expression read, Expression remap, Expression depth)
        {
            var type = shape.Type;
            switch (shape.Kind)
            {
                case ValueKind.ObjectReference:
                    return Expression.Convert(Expression.Call(Method(nameof(Remap)), Expression.Convert(read, typeof(Object)), remap), type);
                case ValueKind.BuiltinClass:
                    return Expression.Convert(Expression.Call(Method(nameof(CopyBuiltinClass)), Expression.Convert(read, typeof(object)), Expression.Constant(type, typeof(Type))), type);
                case ValueKind.Composite:
                    return Expression.Call(Method(nameof(CopyComposite)).MakeGenericMethod(type), read, remap, depth);
                case ValueKind.Array:
                    return Expression.Call(Method(nameof(CopyArray)).MakeGenericMethod(shape.Element.Type), read, remap, depth);
                case ValueKind.List:
                    return Expression.Call(Method(nameof(CopyList)).MakeGenericMethod(shape.Element.Type), read, remap, depth);
                case ValueKind.String:
                    return Expression.Coalesce(read, Expression.Constant(""));
                default:
                    // Primitives, enums and built-in structs are values.
                    return read;
            }
        }

        static MethodInfo Method(string name) => typeof(Cloner).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        /// A new array of the declared element type, each element copied (the elements of an
        /// array share the array's depth).
        static T[] CopyArray<T>(T[] src, Dictionary<Object, Object> remap, int depth)
        {
            int n = src?.Length ?? 0;
            var dst = new T[n];
            var copy = ElementCopier<T>.Copy;
            for (int i = 0; i < n; i++) dst[i] = copy(src[i], remap, depth);
            return dst;
        }

        static List<T> CopyList<T>(List<T> src, Dictionary<Object, Object> remap, int depth)
        {
            var dst = new List<T>();
            if (src != null)
            {
                var copy = ElementCopier<T>.Copy;
                foreach (var item in src) dst.Add(copy(item, remap, depth));
            }
            return dst;
        }

        static class ElementCopier<T>
        {
            public static readonly Func<T, Dictionary<Object, Object>, int, T> Copy = Compile();

            static Func<T, Dictionary<Object, Object>, int, T> Compile()
            {
                var value = Expression.Parameter(typeof(T), "value");
                var remap = Expression.Parameter(typeof(Dictionary<Object, Object>), "remap");
                var depth = Expression.Parameter(typeof(int), "depth");
                var body = CopyExpression(SerializedLayout.ShapeOf(typeof(T)), value, remap, depth);
                return Expression.Lambda<Func<T, Dictionary<Object, Object>, int, T>>(body, value, remap, depth).Compile();
            }
        }

        internal static T CopyComposite<T>(T value, Dictionary<Object, Object> remap, int depth) => CompositeCopier<T>.Copy(value, remap, depth);

        /// A serializable class or struct copied field by field in field order, compiled once
        /// per type. Past the depth limit a struct is its default and a class null. Otherwise a
        /// new instance (NewInstance) gets each field's copy; OnBeforeSerialize runs on the
        /// source before its fields are read and OnAfterDeserialize on the copy after (a struct
        /// source's on the struct handed in, whose changes the copy keeps and the source does not). A null serializable class in a field of a
        /// class source gets an instance in the source too, as at the top level; a null source's
        /// copy keeps its new instance's value-type fields.
        static class CompositeCopier<T>
        {
            public static readonly Func<T, Dictionary<Object, Object>, int, T> Copy = Compile();

            static Func<T, Dictionary<Object, Object>, int, T> Compile()
            {
                var type = typeof(T);
                var shape = SerializedLayout.ShapeOf(type);
                bool isStruct = type.IsValueType;
                bool callbacks = typeof(ISerializationCallbackReceiver).IsAssignableFrom(type);
                var value = Expression.Parameter(type, "value");
                var remap = Expression.Parameter(typeof(Dictionary<Object, Object>), "remap");
                var depth = Expression.Parameter(typeof(int), "depth");
                var dst = Expression.Variable(type, "dst");
                var ret = Expression.Label(type);
                var newInstance = Expression.Convert(Expression.Call(typeof(Cloner).GetMethod(nameof(NewInstance)), Expression.Constant(type, typeof(Type))), type);
                // A struct without a constructor of its own is its default; NewInstance would box it.
                Expression made = isStruct && type.GetConstructor(Type.EmptyTypes) == null ? Expression.Default(type) : newInstance;
                var body = new List<Expression>
                {
                    Expression.IfThen(Expression.GreaterThan(depth, Expression.Constant(SerializedLayout.MaxDepth)),
                        Expression.Return(ret, isStruct ? made : Expression.Default(type))),
                    Expression.Assign(dst, made),
                };
                var isNull = isStruct ? null : Expression.ReferenceEqual(value, Expression.Constant(null, type));
                var notNull = isStruct ? null : Expression.Not(isNull);
                if (!isStruct)
                    body.Add(Expression.IfThen(notNull, Expression.Call(typeof(SerializedLayout).GetMethod(nameof(SerializedLayout.BeforeSerialize)), value)));
                else if (callbacks)
                    body.Add(Callback(nameof(ISerializationCallbackReceiver.OnBeforeSerialize), value));
                var nextDepth = Expression.Add(depth, Expression.Constant(1));
                var vars = new List<ParameterExpression> { dst };
                foreach (var f in SerializedLayout.CompositeFields(shape))
                {
                    var fieldType = f.Field.FieldType;
                    var fv = Expression.Variable(fieldType, f.Name);
                    vars.Add(fv);
                    if (isStruct)
                    {
                        body.Add(Expression.Assign(fv, Expression.Field(value, f.Field)));
                    }
                    else
                    {
                        body.Add(Expression.Assign(fv, Expression.Condition(notNull, Expression.Field(value, f.Field),
                            f.Shape.IsValueType ? Expression.Field(dst, f.Field) : Expression.Default(fieldType))));
                        if (f.Shape.Kind == ValueKind.Composite && !f.Shape.IsValueType)
                        {
                            var fieldNew = Expression.Convert(Expression.Call(typeof(Cloner).GetMethod(nameof(NewInstance)), Expression.Constant(fieldType, typeof(Type))), fieldType);
                            body.Add(Expression.IfThen(
                                Expression.AndAlso(Expression.AndAlso(Expression.ReferenceEqual(fv, Expression.Constant(null, fieldType)), notNull),
                                    Expression.LessThanOrEqual(nextDepth, Expression.Constant(SerializedLayout.MaxDepth))),
                                Expression.Block(Expression.Assign(fv, fieldNew), Expression.Assign(Expression.Field(value, f.Field), fv))));
                        }
                    }
                    body.Add(Expression.Assign(Expression.Field(dst, f.Field), CopyExpression(f.Shape, fv, remap, nextDepth)));
                }
                if (!isStruct)
                    body.Add(Expression.Call(typeof(SerializedLayout).GetMethod(nameof(SerializedLayout.AfterDeserialize)), dst));
                else if (callbacks)
                    body.Add(Callback(nameof(ISerializationCallbackReceiver.OnAfterDeserialize), dst));
                body.Add(Expression.Label(ret, dst));
                return Expression.Lambda<Func<T, Dictionary<Object, Object>, int, T>>(Expression.Block(vars, body), value, remap, depth).Compile();
            }

            /// A struct's callback, called on the struct itself (the game's Tag and HashedString
            /// have them), whose changes it keeps; an exception is logged, as SerializedLayout's are.
            static Expression Callback(string name, ParameterExpression target)
            {
                var map = typeof(T).GetInterfaceMap(typeof(ISerializationCallbackReceiver));
                var method = map.TargetMethods[Array.FindIndex(map.InterfaceMethods, m => m.Name == name)];
                var e = Expression.Variable(typeof(Exception), "e");
                return Expression.TryCatch(Expression.Call(target, method),
                    Expression.Catch(e, Expression.Call(typeof(Debug).GetMethod(nameof(Debug.LogException), [typeof(Exception)]), e)));
            }
        }

        /// A new instance as Unity's serializer creates it: through the parameterless constructor
        /// when there is one (field initializers run), otherwise uninitialized.
        public static object NewInstance(Type type)
        {
            if (type.IsValueType) return Activator.CreateInstance(type);
            var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            return ctor != null ? ctor.Invoke(null) : RuntimeHelpers.GetUninitializedObject(type);
        }

        static object CopyBuiltinClass(object value, Type type)
        {
            if (type == typeof(AnimationCurve))
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
            if (type == typeof(Gradient))
            {
                var dst = new Gradient();
                if (value is Gradient src) dst.CopyFrom(src);
                return dst;
            }
            if (type == typeof(RectOffset))
            {
                var src = value as RectOffset;
                return src == null ? new RectOffset() : new RectOffset(src.left, src.right, src.top, src.bottom);
            }
            if (type == typeof(GUIStyle)) return value is GUIStyle style ? new GUIStyle(style) : new GUIStyle();
            throw new InvalidOperationException("Unknown built-in serialized class " + type);
        }
    }
}
