using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Serialization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Engine.Mecanim
{
    /// One animated property of a scene object, with up to four float components (a Vector3
    /// position, a Color field, a single float…). Components are staged during an evaluation and
    /// written together, so a quaternion or vector is never written half-updated.
    internal sealed class PropertyTarget
    {
        public readonly Object Owner;
        /// The script told OnDidApplyAnimationProperties after animated values were written to it.
        public readonly MonoBehaviour Notify;
        readonly Func<Vector4> read;
        readonly Action<Vector4> write;
        Vector4 staged;
        bool isStaged;

        public PropertyTarget(Object owner, Func<Vector4> read, Action<Vector4> write, MonoBehaviour notify = null)
        {
            Owner = owner;
            this.read = read;
            this.write = write;
            Notify = notify;
        }

        public bool Alive => (bool)Owner;

        public float Read(int component) => read()[component];

        /// Stages one component; returns true for the first staged component since the last commit.
        public bool Stage(int component, float value)
        {
            bool first = !isStaged;
            if (first)
            {
                staged = read();
                isStaged = true;
            }
            staged[component] = value;
            return first;
        }

        public void Commit()
        {
            isStaged = false;
            if (Owner) write(staged);
        }
    }

    /// Resolves clip bindings against the hierarchy under an Animator, as Unity's generic binding
    /// cache does: the path hash selects a transform (CRC32 of its path relative to the Animator,
    /// names joined by '/'), the class ID selects a component on it (the script for
    /// MonoBehaviours), and the attribute selects the property. Bindings that resolve to nothing
    /// stay unbound and are ignored, as in Unity.
    internal sealed class AnimationBinder
    {
        readonly Dictionary<uint, Transform> paths = new();
        readonly Dictionary<(uint path, int classId, uint attribute, byte custom, Type script), PropertyTarget> resolved = new();
        /// One target per property of an object, whichever bindings name its components.
        readonly Dictionary<(Object owner, string property), PropertyTarget> shared = new(ByReference.FirstOfPair<Object, string>.Comparer);

        public AnimationBinder(Transform root) => Collect(root, "");

        void Collect(Transform t, string path)
        {
            paths.TryAdd(Crc32.Of(path), t);
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                Collect(child, path.Length == 0 ? child.name : path + "/" + child.name);
            }
        }

        /// The target of a binding, shared by every clip binding the same property; null if unbound.
        public PropertyTarget Resolve(ClipBinding b)
        {
            var key = (b.Path, b.ClassId, b.Attribute, b.CustomType, b.Script);
            if (resolved.TryGetValue(key, out var existing)) return existing;
            var target = b.IsPPtrCurve || !paths.TryGetValue(b.Path, out var t) ? null : Bind(t, b);
            resolved[key] = target;
            return target;
        }

        PropertyTarget Shared(Object owner, string property, Func<PropertyTarget> create)
        {
            if (!shared.TryGetValue((owner, property), out var target)) shared[(owner, property)] = target = create();
            return target;
        }

        static readonly uint mIsActive = Crc32.Of("m_IsActive"), mEnabled = Crc32.Of("m_Enabled");
        const int GameObjectClassId = 1, MonoBehaviourClassId = 114, RectTransformClassId = 224, CanvasGroupClassId = 225;
        const byte RectTransformCustomType = 28;

        PropertyTarget Bind(Transform t, ClipBinding b)
        {
            switch (b.ClassId)
            {
                case GameObjectClassId:
                {
                    var go = t.gameObject;
                    if (b.CustomType != 0 || b.Attribute != mIsActive) return null;
                    return Shared(go, "m_IsActive", () => new PropertyTarget(go, () => new Vector4(go.activeSelf ? 1f : 0f, 0, 0, 0), v => go.SetActive(FloatToBool(v.x))));
                }
                case ClipBinding.TransformClassId:
                    return BindTransform(t, b);
                case MonoBehaviourClassId:
                {
                    if (b.Script == null || b.CustomType != 0) return null;
                    MonoBehaviour target = null;
                    foreach (var c in t.GetComponents<MonoBehaviour>())
                        if (c.GetType() == b.Script)
                        {
                            target = c;
                            break;
                        }
                    return !target ? null : BindScriptProperty(target, b.Attribute);
                }
            }
            if (b.ClassId == RectTransformClassId && b.CustomType == RectTransformCustomType)
                return t is RectTransform rt ? BindRectTransform(rt, b.Attribute) : null;
            if (b.CustomType != 0) return null;

            // Properties of other engine components, matched by exact native class.
            if (!TypeTreeDatabase.TryGet(b.ClassId, out var info)) return null;
            var engineType = Binders.EngineTypeOf(info.Name);
            if (engineType == null) return null;
            Component component = null;
            foreach (var c in t.GetComponents<Component>())
                if (c.GetType() == engineType)
                {
                    component = c;
                    break;
                }
            if (!component) return null;
            if (b.ClassId == CanvasGroupClassId) return BindCanvasGroup((CanvasGroup)component, b.Attribute);
            if (component is Behaviour behaviour && b.Attribute == mEnabled) return BindEnabled(behaviour);
            return null;
        }

        /// Unity's float → bool rule for animated booleans.
        public static bool FloatToBool(float v) => v > 0.001f || v < -0.001f;

        /// Unity's float → int rule for animated integers (round half up).
        public static int FloatToInt(float v) => (int)MathF.Floor(v + 0.5f);

        PropertyTarget BindEnabled(Behaviour b)
            => Shared(b, "m_Enabled", () => new PropertyTarget(b, () => new Vector4(b.enabled ? 1f : 0f, 0, 0, 0), v => b.enabled = FloatToBool(v.x)));

        PropertyTarget BindTransform(Transform t, ClipBinding b)
        {
            switch (b.Attribute)
            {
                case 1: return Shared(t, "position", () => new PropertyTarget(t, () => t.localPosition, v => t.localPosition = v));
                case 2:
                    return Shared(t, "rotation", () => new PropertyTarget(t, () => { var q = t.localRotation; return new Vector4(q.x, q.y, q.z, q.w); },
                        v => t.localRotation = Quaternion.Normalize(new Quaternion(v.x, v.y, v.z, v.w))));
                case 3: return Shared(t, "scale", () => new PropertyTarget(t, () => t.localScale, v => t.localScale = v));
                case 4:
                {
                    int order = b.CustomType;
                    return Shared(t, "euler", () => new PropertyTarget(t, () => t.localEulerAngles, v => t.localRotation = EulerToQuaternion(v, order)));
                }
                default: return null;
            }
        }

        /// Euler angles (degrees) in Unity's RotationOrder (XYZ, XZY, YZX, YXZ, ZXY, ZYX: the axis
        /// applied first comes first); ZXY is Quaternion.Euler's order.
        static Quaternion EulerToQuaternion(Vector4 e, int order)
        {
            var qx = Quaternion.AngleAxis(e.x, Vector3.right);
            var qy = Quaternion.AngleAxis(e.y, Vector3.up);
            var qz = Quaternion.AngleAxis(e.z, Vector3.forward);
            return order switch
            {
                0 => qz * qy * qx,
                1 => qy * qz * qx,
                2 => qx * qz * qy,
                3 => qz * qx * qy,
                5 => qx * qy * qz,
                _ => qy * qx * qz,
            };
        }

        static readonly Dictionary<uint, (int property, int component)> rectAttributes = BuildRectAttributes();

        static Dictionary<uint, (int, int)> BuildRectAttributes()
        {
            var map = new Dictionary<uint, (int, int)>();
            string[] names = ["m_AnchoredPosition", "m_SizeDelta", "m_AnchorMin", "m_AnchorMax", "m_Pivot"];
            for (int i = 0; i < names.Length; i++)
            {
                map[Crc32.Of(names[i] + ".x")] = (i, 0);
                map[Crc32.Of(names[i] + ".y")] = (i, 1);
            }
            return map;
        }

        PropertyTarget BindRectTransform(RectTransform rt, uint attribute)
        {
            if (!rectAttributes.TryGetValue(attribute, out var a)) return null;
            return Shared(rt, "rect" + a.property, () => a.property switch
            {
                0 => new PropertyTarget(rt, () => rt.anchoredPosition, v => rt.anchoredPosition = v),
                1 => new PropertyTarget(rt, () => rt.sizeDelta, v => rt.sizeDelta = v),
                2 => new PropertyTarget(rt, () => rt.anchorMin, v => rt.anchorMin = v),
                3 => new PropertyTarget(rt, () => rt.anchorMax, v => rt.anchorMax = v),
                _ => new PropertyTarget(rt, () => rt.pivot, v => rt.pivot = v),
            });
        }

        /// The component of a binding attribute within its property target.
        public static int ComponentOf(ClipBinding b, int curveInBinding)
        {
            if (b.ClassId == ClipBinding.TransformClassId) return curveInBinding;
            if (b.ClassId == RectTransformClassId && b.CustomType == RectTransformCustomType)
                return rectAttributes.TryGetValue(b.Attribute, out var a) ? a.component : 0;
            if (b.ClassId == MonoBehaviourClassId && b.Script != null)
                return ScriptProperties(b.Script).TryGetValue(b.Attribute, out var p) ? p.Component : 0;
            return 0;
        }

        PropertyTarget BindCanvasGroup(CanvasGroup g, uint attribute)
        {
            if (attribute == Crc32.Of("m_Alpha")) return Shared(g, "m_Alpha", () => new PropertyTarget(g, () => new Vector4(g.alpha, 0, 0, 0), v => g.alpha = v.x));
            if (attribute == Crc32.Of("m_Interactable")) return Shared(g, "m_Interactable", () => new PropertyTarget(g, () => new Vector4(g.interactable ? 1 : 0, 0, 0, 0), v => g.interactable = FloatToBool(v.x)));
            if (attribute == Crc32.Of("m_BlocksRaycasts")) return Shared(g, "m_BlocksRaycasts", () => new PropertyTarget(g, () => new Vector4(g.blocksRaycasts ? 1 : 0, 0, 0, 0), v => g.blocksRaycasts = FloatToBool(v.x)));
            if (attribute == Crc32.Of("m_IgnoreParentGroups")) return Shared(g, "m_IgnoreParentGroups", () => new PropertyTarget(g, () => new Vector4(g.ignoreParentGroups ? 1 : 0, 0, 0, 0), v => g.ignoreParentGroups = FloatToBool(v.x)));
            if (attribute == mEnabled) return BindEnabled(g);
            return null;
        }

        // ---- Script fields ----

        enum LeafKind { Float, Int, Bool, Vector2, Vector3, Vector4, Color, Quaternion }

        /// A serialized numeric value inside a script: the field chain to it, its kind, and which
        /// component of it the attribute names ("m_fontColor.a" → m_fontColor, Color, 3).
        sealed class ScriptProperty
        {
            public FieldInfo[] Chain;
            /// The property path of the value without its component ("m_fontColor").
            public string Path;
            public LeafKind Kind;
            public int Component;
        }

        static readonly ConcurrentDictionary<Type, Dictionary<uint, ScriptProperty>> scriptProperties = new();

        static Dictionary<uint, ScriptProperty> ScriptProperties(Type type) => scriptProperties.GetOrAdd(type, static t =>
        {
            var map = new Dictionary<uint, ScriptProperty>();
            Collect(map, SerializedLayout.ScriptFields(t), "", Array.Empty<FieldInfo>(), 0);
            return map;
        });

        static void Collect(Dictionary<uint, ScriptProperty> map, FieldShape[] fields, string prefix, FieldInfo[] chain, int depth)
        {
            foreach (var f in fields)
            {
                var name = prefix + f.Name;
                var path = new FieldInfo[chain.Length + 1];
                Array.Copy(chain, path, chain.Length);
                path[^1] = f.Field;
                void Add(string attr, LeafKind kind, int component) => map.TryAdd(Crc32.Of(attr), new ScriptProperty { Chain = path, Path = name, Kind = kind, Component = component });
                var shape = f.Shape;
                switch (shape.Kind)
                {
                    case ValueKind.Float: Add(name, LeafKind.Float, 0); break;
                    case ValueKind.Int: Add(name, LeafKind.Int, 0); break;
                    case ValueKind.Bool: Add(name, LeafKind.Bool, 0); break;
                    case ValueKind.BuiltinStruct:
                        if (shape.Type == typeof(Vector2)) { Add(name + ".x", LeafKind.Vector2, 0); Add(name + ".y", LeafKind.Vector2, 1); }
                        else if (shape.Type == typeof(Vector3)) { Add(name + ".x", LeafKind.Vector3, 0); Add(name + ".y", LeafKind.Vector3, 1); Add(name + ".z", LeafKind.Vector3, 2); }
                        else if (shape.Type == typeof(Vector4)) { Add(name + ".x", LeafKind.Vector4, 0); Add(name + ".y", LeafKind.Vector4, 1); Add(name + ".z", LeafKind.Vector4, 2); Add(name + ".w", LeafKind.Vector4, 3); }
                        else if (shape.Type == typeof(Quaternion)) { Add(name + ".x", LeafKind.Quaternion, 0); Add(name + ".y", LeafKind.Quaternion, 1); Add(name + ".z", LeafKind.Quaternion, 2); Add(name + ".w", LeafKind.Quaternion, 3); }
                        else if (shape.Type == typeof(Color)) { Add(name + ".r", LeafKind.Color, 0); Add(name + ".g", LeafKind.Color, 1); Add(name + ".b", LeafKind.Color, 2); Add(name + ".a", LeafKind.Color, 3); }
                        break;
                    case ValueKind.Composite when depth < SerializedLayout.MaxDepth:
                        Collect(map, SerializedLayout.CompositeFields(shape), name + ".", path, depth + 1);
                        break;
                }
            }
        }

        PropertyTarget BindScriptProperty(MonoBehaviour target, uint attribute)
        {
            if (attribute == mEnabled) return BindEnabled(target);
            if (!ScriptProperties(target.GetType()).TryGetValue(attribute, out var p)) return null;
            var chain = p.Chain;
            var kind = p.Kind;
            var notify = target.Native is BehaviourNative { Script.OnDidApplyAnimationProperties: not null } ? target : null;
            return Shared(target, p.Path, () => new PropertyTarget(target, () => ToVector(GetLeaf(target, chain), kind), v => SetLeaf(target, chain, FromVector(v, kind)), notify));
        }

        static object GetLeaf(object root, FieldInfo[] chain)
        {
            object o = root;
            foreach (var f in chain)
            {
                if (o == null) return null;
                o = f.GetValue(o);
            }
            return o;
        }

        /// Writes the leaf and stores modified struct containers back into their parents.
        static void SetLeaf(object root, FieldInfo[] chain, object value)
        {
            var containers = new object[chain.Length];
            containers[0] = root;
            for (int i = 1; i < chain.Length; i++)
            {
                containers[i] = chain[i - 1].GetValue(containers[i - 1]);
                if (containers[i] == null) return;
            }
            for (int i = chain.Length - 1; i >= 0; i--)
            {
                chain[i].SetValue(containers[i], value);
                if (i == 0 || !containers[i].GetType().IsValueType) break;
                value = containers[i];
            }
        }

        static Vector4 ToVector(object value, LeafKind kind) => value switch
        {
            null => default,
            _ => kind switch
            {
                LeafKind.Float => new Vector4((float)value, 0, 0, 0),
                LeafKind.Int => new Vector4((int)value, 0, 0, 0),
                LeafKind.Bool => new Vector4((bool)value ? 1f : 0f, 0, 0, 0),
                LeafKind.Vector2 => (Vector2)value,
                LeafKind.Vector3 => (Vector3)value,
                LeafKind.Vector4 => (Vector4)value,
                LeafKind.Color => (Vector4)(Color)value,
                _ => QuaternionToVector((Quaternion)value),
            },
        };

        static Vector4 QuaternionToVector(Quaternion q) => new(q.x, q.y, q.z, q.w);

        static object FromVector(Vector4 v, LeafKind kind) => kind switch
        {
            LeafKind.Float => v.x,
            LeafKind.Int => FloatToInt(v.x),
            LeafKind.Bool => FloatToBool(v.x),
            LeafKind.Vector2 => (Vector2)v,
            LeafKind.Vector3 => (Vector3)v,
            LeafKind.Vector4 => v,
            LeafKind.Color => (Color)v,
            _ => new Quaternion(v.x, v.y, v.z, v.w),
        };
    }
}
