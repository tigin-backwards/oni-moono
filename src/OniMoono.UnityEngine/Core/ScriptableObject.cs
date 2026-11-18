using System;
using System.Collections.Concurrent;
using System.Reflection;
using OniMoono.Engine;

namespace UnityEngine
{
    public partial class ScriptableObject
    {
        /// CreateInstance, asset loading and Instantiate bind the native object before this runs.
        /// Plain `new` on a ScriptableObject still makes a live object (observed in Unity's
        /// player): one warning, then Awake and OnEnable, all before the subclass constructor's
        /// body; the object has an instance id, an empty name and no hide flags, and Destroy
        /// ends it as usual. Mods build TextStyleSettings this way and test them against null
        /// (Ronivan's Legacy, issue #7). `new` on a MonoBehaviour stays a dead object, with no
        /// log, as in the player.
        public ScriptableObject()
        {
            if (Native is not null) return;
            var type = GetType();
            Debug.LogWarning($"{type.FullName} must be instantiated using the ScriptableObject.CreateInstance method instead of new {type.Name}.");
            Bind(new NativeObject { Name = "" });
            ScriptableObjectLifecycle.Created(this);
        }

        public static T CreateInstance<T>() where T : ScriptableObject => (T)CreateInstance(typeof(T));

        public static ScriptableObject CreateInstance(Type type)
        {
            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters)
            {
                Debug.LogError($"Instance of {type?.Name} couldn't be created because there is no script with that name.");
                return null;
            }
            var native = new NativeObject { Name = "" };
            var so = Object.ConstructBound<ScriptableObject>(type, native);
            ScriptableObjectLifecycle.Created(so);
            return so;
        }

        public static ScriptableObject CreateInstance(string className)
        {
            var type = ScriptableObjectLifecycle.FindType(className);
            if (type == null)
            {
                Debug.LogError($"Instance of {className} couldn't be created because there is no script with that name.");
                return null;
            }
            return CreateInstance(type);
        }

        /// Editor-only bookkeeping in Unity; in a player there is nothing to mark.
        public void SetDirty() { }
    }
}

namespace OniMoono.Engine
{
    /// Awake/OnEnable when a ScriptableObject is created or loaded; OnDisable/OnDestroy when it
    /// is destroyed. Same lookup rule as MonoBehaviour messages (most-derived, any access).
    internal static class ScriptableObjectLifecycle
    {
        sealed record Messages(MethodInfo Awake, MethodInfo OnEnable, MethodInfo OnDisable, MethodInfo OnDestroy);

        static readonly ConcurrentDictionary<Type, Messages> cache = new();

        static Messages Get(Type t) => cache.GetOrAdd(t, static type => new Messages(
            Find(type, "Awake"), Find(type, "OnEnable"), Find(type, "OnDisable"), Find(type, "OnDestroy")));

        static MethodInfo Find(Type type, string name)
        {
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = type; t != null && t.Assembly != typeof(UnityEngine.ScriptableObject).Assembly; t = t.BaseType)
            {
                var m = t.GetMethod(name, declared, null, Type.EmptyTypes, null);
                if (m != null) return m;
            }
            return null;
        }

        static void Call(MethodInfo m, UnityEngine.ScriptableObject so)
        {
            if (m == null) return;
            try
            {
                m.Invoke(so, null);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                UnityEngine.Debug.LogException(e.InnerException, so);
            }
        }

        public static void Created(UnityEngine.ScriptableObject so)
        {
            var m = Get(so.GetType());
            Call(m.Awake, so);
            if (so.Native is not null) Call(m.OnEnable, so);
        }

        public static void Destroy(UnityEngine.ScriptableObject so)
        {
            var m = Get(so.GetType());
            Call(m.OnDisable, so);
            Call(m.OnDestroy, so);
        }

        /// A ScriptableObject subclass by class name (with or without namespace) in any loaded assembly.
        public static Type FindType(string className)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null)!; }
                foreach (var t in types)
                    if (typeof(UnityEngine.ScriptableObject).IsAssignableFrom(t) && (t.Name == className || t.FullName == className))
                        return t;
            }
            return null;
        }
    }
}
