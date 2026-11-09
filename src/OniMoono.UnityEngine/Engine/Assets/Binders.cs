using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OniMoono.Assets.Files;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Serialization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Engine.Assets
{
    internal sealed class GameObjectBinder : Binder
    {
        public override Object Create(LoadRequest request)
        {
            var f = request.ReadNative();
            var native = new GameObjectNative
            {
                Name = f.String("m_Name"),
                Layer = (int)f.Get<uint>("m_Layer"),
                Tag = Tags.FromIndex(f.Get<ushort>("m_Tag")),
                ActiveSelf = f.Get<bool>("m_IsActive"),
                Serialized = f,
            };
            return new GameObject(native);
        }

        public override void Fill(Object obj, LoadRequest request)
        {
            var native = (GameObjectNative)obj.NativeOrThrow;
            var pairs = native.Serialized.Array("m_Component");
            var components = new List<Component>(pairs.Length);
            foreach (Fields pair in pairs)
            {
                var c = request.Resolver.Resolve(pair.PPtr("component")) as Component;
                if (c == null) throw new InvalidDataException($"GameObject {native.Name}: missing component {pair.PPtr("component")}.");
                components.Add(c);
            }
            native.Components = components.ToArray();
            native.Transform = (TransformNative)components[0].NativeOrThrow;
            native.Serialized = null;
        }
    }

    internal sealed class TransformBinder : Binder
    {
        public override Object Create(LoadRequest request)
        {
            var f = request.ReadNative();
            bool rect = request.ClassId == 224;
            var native = rect ? new RectTransformNative() : new TransformNative();
            native.LocalRotation = ToQuaternion(f.Struct("m_LocalRotation"));
            native.LocalPosition = ToVector3(f.Struct("m_LocalPosition"));
            native.LocalScale = ToVector3(f.Struct("m_LocalScale"));
            if (native is RectTransformNative r)
            {
                r.AnchorMin = ToVector2(f.Struct("m_AnchorMin"));
                r.AnchorMax = ToVector2(f.Struct("m_AnchorMax"));
                r.AnchoredPosition = ToVector2(f.Struct("m_AnchoredPosition"));
                r.SizeDelta = ToVector2(f.Struct("m_SizeDelta"));
                r.Pivot = ToVector2(f.Struct("m_Pivot"));
            }
            native.Serialized = f;
            return ObjectLoader.CreateNative<Transform>(rect ? typeof(RectTransform) : typeof(Transform), native);
        }

        public override void Fill(Object obj, LoadRequest request)
        {
            var native = (TransformNative)obj.NativeOrThrow;
            var f = native.Serialized;
            native.Serialized = null;
            native.GameObject = (GameObjectNative)request.Resolver.Resolve(f.PPtr("m_GameObject")).NativeOrThrow;
            var parent = request.Resolver.Resolve(f.PPtr("m_Father")) as Transform;
            native.Parent = parent == null ? null : (TransformNative)parent.NativeOrThrow;
            var children = f.Array("m_Children");
            var list = new List<TransformNative>(children.Length);
            foreach (PPtr child in children)
                if (request.Resolver.Resolve(child) is Transform t) list.Add((TransformNative)t.NativeOrThrow);
            native.SetChildren(list.ToArray());
            native.MarkDirty(false);
        }

        internal static Vector3 ToVector3(Fields f) => new(f.Float("x"), f.Float("y"), f.Float("z"));
        internal static Vector2 ToVector2(Fields f) => new(f.Float("x"), f.Float("y"));
        internal static Quaternion ToQuaternion(Fields f) => new(f.Float("x"), f.Float("y"), f.Float("z"), f.Float("w"));
    }

    /// MonoBehaviour payloads: a component when attached to a GameObject, otherwise a
    /// ScriptableObject. Fields follow SerializedLayout for the script's class.
    internal sealed class ScriptBinder : Binder
    {
        sealed class Pending
        {
            public Fields Base;
            public int DataStart;
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Object, Pending> pending = new();

        /// The MonoBehaviour fields before a script's own (m_GameObject, m_Enabled, m_Script, m_Name).
        static readonly int BaseFieldCount = TypeTreeDatabase.Get(114).Tree!.Children.Count;

        public override Object Create(LoadRequest request)
        {
            var r = request.Open();
            var baseFields = TypeTreeReader.ReadFields(TypeTreeDatabase.Get(114).Tree!, r);
            var type = ScriptTable.Resolve(request.File, baseFields.PPtr("m_Script"), out var className);
            Object obj;
            if (type == null && !baseFields.PPtr("m_GameObject").IsNull)
            {
                // A behaviour whose script class is not loaded (a mod's bundle built with classes
                // the mod does not ship) stays on its GameObject as a missing script, with Unity's
                // warnings (measured in the original: one naming the class, and one naming the
                // object once the behaviour is attached).
                if (className != null) EngineLog.Message(LogType.Warning, $"The referenced script ({className}) on this Behaviour is missing!", "");
                type = typeof(MissingScript);
            }
            if (type == null) throw new InvalidDataException($"{request.File.Name}:{request.Info.PathId}: script type not found.");
            if (!baseFields.PPtr("m_GameObject").IsNull)
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(type)) throw new InvalidDataException($"{type} attached to a GameObject is not a MonoBehaviour.");
                var native = (BehaviourNative)ComponentNatives.Create(type);
                native.Enabled = baseFields.Get<byte>("m_Enabled") != 0;
                native.Script = ScriptInfo.Get(type);
                native.ExecutionOrder = native.Script.ExecutionOrder;
                obj = Object.ConstructBound<Component>(type, native);
            }
            else
            {
                if (!typeof(ScriptableObject).IsAssignableFrom(type)) throw new InvalidDataException($"{type} without a GameObject is not a ScriptableObject.");
                obj = Object.ConstructBound<ScriptableObject>(type, new NativeObject { Name = baseFields.String("m_Name") });
            }
            pending.Add(obj, new Pending { Base = baseFields, DataStart = r.Position });
            return obj;
        }

        public override void Fill(Object obj, LoadRequest request)
        {
            pending.TryGetValue(obj, out var p);
            pending.Remove(obj);
            if (obj.Native is ComponentNative cn)
                cn.GameObject = (GameObjectNative)request.Resolver.Resolve(p.Base.PPtr("m_GameObject")).NativeOrThrow;
            if (obj is MissingScript)
            {
                // Its data has no class to go to.
                EngineLog.Message(LogType.Warning, $"The referenced script on this Behaviour (Game Object '{((ComponentNative)obj.Native).GameObject.Name}') is missing!", "");
                return;
            }
            var r = request.Open();
            r.Position = p.DataStart;
            var fields = SerializedLayout.ScriptFields(obj.GetType());
            // A file whose tree lays the script out differently (a mod's bundle built against
            // another version of the class) is read by its tree, field by name, as Unity reads it.
            var tree = request.File.TypeOf(request.Info).Tree;
            var nodes = tree?.Children.GetRange(BaseFieldCount, tree.Children.Count - BaseFieldCount);
            if (nodes != null && !ScriptData.Matches(fields, nodes)) ScriptData.ReadFieldsByTree(obj, fields, nodes, r, request.Resolver);
            else ScriptData.ReadFields(obj, fields, r, request.Resolver);
            if (r.Remaining != 0) throw new InvalidDataException($"{obj.GetType()}: {r.Remaining} bytes of script data left.");
            if (obj is ISerializationCallbackReceiver receiver)
            {
                try { receiver.OnAfterDeserialize(); }
                catch (Exception e) { Debug.LogException(e, obj); }
            }
            if (obj is ScriptableObject so) ObjectLoader.AfterLoad(() => ScriptableObjectLifecycle.Created(so));
        }
    }

    internal sealed class TextAssetBinder : Binder
    {
        /// m_Name, then m_Script as raw bytes (kanim data is binary; text is decoded on demand).
        public override Object Create(LoadRequest request)
        {
            var r = request.Open();
            var name = System.Text.Encoding.UTF8.GetString(r.ReadSpan(r.ReadInt32()));
            r.Align(4);
            var bytes = TextAssetNative.Pinned(r.ReadSpan(r.ReadInt32()));
            r.Align(4);
            if (r.Remaining != 0) throw new InvalidDataException($"TextAsset {name}: {r.Remaining} bytes left.");
            return ObjectLoader.CreateNative<TextAsset>(typeof(TextAsset), new TextAssetNative { Name = name, Bytes = bytes });
        }

        public override void Fill(Object obj, LoadRequest request) { }
    }

    /// Components of engine classes whose subsystem consumes the serialized data (Camera,
    /// Canvas, renderers…): the component is created and attached; its data stays on the native
    /// record for that subsystem.
    internal sealed class StoredComponentBinder(Type engineType) : Binder
    {
        public override Object Create(LoadRequest request)
        {
            var f = request.ReadNative();
            var native = ComponentNatives.Create(engineType);
            native.Serialized = f;
            if (native is BehaviourNative bn && f.Contains("m_Enabled")) bn.Enabled = f.Get<byte>("m_Enabled") != 0;
            return ObjectLoader.CreateNative<Component>(engineType, native);
        }

        public override void Fill(Object obj, LoadRequest request)
        {
            var native = (ComponentNative)obj.NativeOrThrow;
            native.GameObject = (GameObjectNative)request.Resolver.Resolve(native.Serialized.PPtr("m_GameObject")).NativeOrThrow;
            native.Bind(native.Serialized, request.Resolver);
        }
    }

    /// Assets of engine classes (materials, textures, meshes…): created with their data; the
    /// owning subsystem builds engine resources from it.
    internal sealed class StoredAssetBinder(Type engineType) : Binder
    {
        public override Object Create(LoadRequest request)
        {
            var f = request.ReadNative();
            var native = AssetNatives.Create(engineType);
            native.Name = f.Contains("m_Name") ? f.String("m_Name") : "";
            native.Serialized = f;
            return ObjectLoader.CreateNative<Object>(engineType, native);
        }

        public override void Fill(Object obj, LoadRequest request)
        {
            var native = obj.NativeOrThrow;
            native.Bind(native.Serialized, request.Resolver);
        }
    }

    /// MonoScript → managed type. Player files reference the MonoScripts stored in
    /// globalgamemanagers.assets; bundles carry their own.
    internal static class ScriptTable
    {
        static readonly Dictionary<(string, long), (Type Type, string Name)> cache = new();

        public static Type Resolve(SerializedFile from, PPtr script) => Resolve(from, script, out _);

        /// The script's class, or null when it is not loaded; `className` is the class the
        /// MonoScript names (null for a null reference).
        public static Type Resolve(SerializedFile from, PPtr script, out string className)
        {
            className = null;
            if (script.IsNull) return null;
            var file = AssetSystem.ResolveExternal(from, script.FileId);
            var key = (AssetSystem.KeyOf(file), script.PathId);
            if (cache.TryGetValue(key, out var t))
            {
                className = t.Name;
                return t.Type;
            }
            var info = file.ObjectsById[script.PathId];
            var tree = file.TypeOf(info).Tree;
            var f = tree != null ? TypeTreeReader.ReadObject(tree, file.OpenObject(info)) : TypeTreeReader.ReadNative(115, file.OpenObject(info));
            var asmName = f.String("m_AssemblyName");
            if (asmName.EndsWith(".dll", StringComparison.Ordinal)) asmName = asmName[..^4];
            var ns = f.String("m_Namespace");
            var fullName = ns.Length > 0 ? ns + "." + f.String("m_ClassName") : f.String("m_ClassName");
            Type type;
            try
            {
                type = Assembly.Load(asmName).GetType(fullName, false);
            }
            catch (FileNotFoundException)
            {
                type = null;
            }
            // UnityEngine and its modules are facades over this assembly; without them (no host
            // facade directory) their script classes (e.g. GUISkin) are found here directly.
            if (type == null && (asmName == "UnityEngine" || asmName.StartsWith("UnityEngine.", StringComparison.Ordinal)))
                type = typeof(Object).Assembly.GetType(fullName, false);
            if (type != null) ScriptInfo.SetExecutionOrder(type.FullName!, f.Int("m_ExecutionOrder"));
            cache[key] = (type, fullName);
            className = fullName;
            return type;
        }
    }
}
