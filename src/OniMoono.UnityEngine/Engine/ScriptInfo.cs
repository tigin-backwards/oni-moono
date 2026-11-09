using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace OniMoono.Engine
{
    /// The engine messages a MonoBehaviour type implements, resolved once per type.
    ///
    /// Unity's rule: a message is the most-derived instance method with that name and an
    /// accepted parameter list, whatever its accessibility; a base class's method of the same name
    /// runs only if the derived one calls it.
    internal sealed class ScriptInfo
    {
        public readonly Type Type;
        public Action<MonoBehaviour> Awake, OnEnable, OnDisable, OnDestroy;
        public Action<MonoBehaviour> Start;
        public Func<MonoBehaviour, IEnumerator> StartCoroutine; // Start declared as IEnumerator
        public Action<MonoBehaviour> Update, LateUpdate, FixedUpdate;
        public Action<MonoBehaviour> OnPreCull, OnPreRender, OnPostRender, OnGUI;
        public Action<MonoBehaviour, RenderTexture, RenderTexture> OnRenderImage;
        public Action<MonoBehaviour, bool> OnApplicationFocus, OnApplicationPause;
        public Action<MonoBehaviour> OnApplicationQuit;
        public Action<MonoBehaviour> OnTransformParentChanged, OnTransformChildrenChanged;
        public Action<MonoBehaviour> OnRectTransformDimensionsChange, OnChildRectTransformDimensionsChange, OnBeforeTransformParentChanged;
        public Action<MonoBehaviour> OnCanvasGroupChanged, OnCanvasHierarchyChanged, OnDidApplyAnimationProperties;
        public Action<MonoBehaviour> OnBecameVisible, OnBecameInvisible;

        /// Script execution order: the MonoScript's m_ExecutionOrder, or for a type without one (a
        /// mod's) its [DefaultExecutionOrder], inherited; 0 when neither sets one.
        public int ExecutionOrder;

        static readonly ConcurrentDictionary<Type, ScriptInfo> cache = new();
        static readonly ConcurrentDictionary<string, int> executionOrders = new(StringComparer.Ordinal);

        ScriptInfo(Type type) => Type = type;

        public static ScriptInfo Get(Type type) => cache.GetOrAdd(type, static t => Build(t));

        /// Execution orders from the project's MonoScripts, keyed by full type name. Installed by
        /// the asset layer before any script runs.
        public static void SetExecutionOrder(string fullTypeName, int order) => executionOrders[fullTypeName] = order;

        static ScriptInfo Build(Type type)
        {
            var info = new ScriptInfo(type);
            info.ExecutionOrder = executionOrders.TryGetValue(type.FullName ?? type.Name, out var order) ? order : DefaultOrder(type);
            info.Awake = Void(type, "Awake");
            info.OnEnable = Void(type, "OnEnable");
            info.OnDisable = Void(type, "OnDisable");
            info.OnDestroy = Void(type, "OnDestroy");
            info.Update = Void(type, "Update");
            info.LateUpdate = Void(type, "LateUpdate");
            info.FixedUpdate = Void(type, "FixedUpdate");
            info.OnPreCull = Void(type, "OnPreCull");
            info.OnPreRender = Void(type, "OnPreRender");
            info.OnPostRender = Void(type, "OnPostRender");
            info.OnGUI = Void(type, "OnGUI");
            info.OnApplicationQuit = Void(type, "OnApplicationQuit");
            info.OnTransformParentChanged = Void(type, "OnTransformParentChanged");
            info.OnTransformChildrenChanged = Void(type, "OnTransformChildrenChanged");
            info.OnRectTransformDimensionsChange = Void(type, "OnRectTransformDimensionsChange");
            info.OnChildRectTransformDimensionsChange = Void(type, "OnChildRectTransformDimensionsChange");
            info.OnBeforeTransformParentChanged = Void(type, "OnBeforeTransformParentChanged");
            info.OnCanvasGroupChanged = Void(type, "OnCanvasGroupChanged");
            info.OnCanvasHierarchyChanged = Void(type, "OnCanvasHierarchyChanged");
            info.OnDidApplyAnimationProperties = Void(type, "OnDidApplyAnimationProperties");
            info.OnBecameVisible = Void(type, "OnBecameVisible");
            info.OnBecameInvisible = Void(type, "OnBecameInvisible");
            info.OnApplicationFocus = Bool(type, "OnApplicationFocus");
            info.OnApplicationPause = Bool(type, "OnApplicationPause");

            var start = Find(type, "Start", Type.EmptyTypes);
            if (start != null)
            {
                if (typeof(IEnumerator).IsAssignableFrom(start.ReturnType)) info.StartCoroutine = Bind<Func<MonoBehaviour, IEnumerator>>(start, nameof(Thunks.Func));
                else info.Start = Bind<Action<MonoBehaviour>>(start, nameof(Thunks.Action0));
            }

            var renderImage = Find(type, "OnRenderImage", [typeof(RenderTexture), typeof(RenderTexture)]);
            if (renderImage != null) info.OnRenderImage = Bind<Action<MonoBehaviour, RenderTexture, RenderTexture>>(renderImage, nameof(Thunks.Action2));
            return info;
        }

        /// A type no MonoScript describes (one from an assembly loaded at runtime) takes its order
        /// from [DefaultExecutionOrder], its base classes' included. Measured in the original
        /// player (UnityProbe's execorder): a mod's -1000 and 1000 types, and a subclass of the
        /// -1000 one, run FixedUpdate, Update and LateUpdate in that order around unmarked ones,
        /// and an object's Awake and OnEnable go by it too; Starts of separate objects do not.
        static int DefaultOrder(Type type)
        {
            try
            {
                return Attribute.GetCustomAttribute(type, typeof(DefaultExecutionOrder), true) is DefaultExecutionOrder a ? a.order : 0;
            }
            catch (Exception e) when (e is TypeLoadException or FileNotFoundException or CustomAttributeFormatException)
            {
                // Another of the type's attributes cannot be loaded: no order read.
                return 0;
            }
        }

        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// Most-derived declaration of `name` with exactly these parameters, searching up to (not
        /// including) the engine's own MonoBehaviour/Behaviour/Component classes.
        public static MethodInfo Find(Type type, string name, Type[] parameters)
        {
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t.Assembly != typeof(MonoBehaviour).Assembly; t = t.BaseType)
            {
                var m = t.GetMethod(name, Declared, null, parameters, null);
                if (m != null && !m.IsGenericMethodDefinition) return m;
            }
            return null;
        }

        static Action<MonoBehaviour> Void(Type type, string name)
        {
            var m = Find(type, name, Type.EmptyTypes);
            return m == null ? null : Bind<Action<MonoBehaviour>>(m, nameof(Thunks.Action0));
        }

        static Action<MonoBehaviour, bool> Bool(Type type, string name)
        {
            var m = Find(type, name, [typeof(bool)]);
            return m == null ? null : Bind<Action<MonoBehaviour, bool>>(m, nameof(Thunks.Action1Bool));
        }

        /// Builds an open delegate over the declaring type and wraps it to take MonoBehaviour.
        static TDelegate Bind<TDelegate>(MethodInfo method, string thunk) where TDelegate : Delegate
        {
            var declaring = method.DeclaringType!;
            var factory = typeof(Thunks).GetMethod(thunk, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.MakeGenericMethod(declaring);
            return (TDelegate)factory.Invoke(null, [method])!;
        }

        // The thunks are compiled without profile-guided optimization (AggressiveOptimization), so
        // the JIT never inlines a script's message into them: its frame stays in the stack traces
        // of its exceptions (LogTraces), as on Mono.
        static class Thunks
        {
            public static Action<MonoBehaviour> Action0<T>(MethodInfo m) where T : MonoBehaviour
            {
                if (m.ReturnType == typeof(void))
                {
                    var d = m.CreateDelegate<Action<T>>();
                    return [MethodImpl(MethodImplOptions.AggressiveOptimization)] (MonoBehaviour b) => d((T)b);
                }
                // Messages with a non-void return (e.g. bool Awake()) still run; the result is ignored.
                return b => m.Invoke(b, null);
            }

            public static Action<MonoBehaviour, bool> Action1Bool<T>(MethodInfo m) where T : MonoBehaviour
            {
                if (m.ReturnType == typeof(void))
                {
                    var d = m.CreateDelegate<Action<T, bool>>();
                    return [MethodImpl(MethodImplOptions.AggressiveOptimization)] (MonoBehaviour b, bool v) => d((T)b, v);
                }
                return (b, v) => m.Invoke(b, [v]);
            }

            public static Action<MonoBehaviour, RenderTexture, RenderTexture> Action2<T>(MethodInfo m) where T : MonoBehaviour
            {
                var d = m.CreateDelegate<Action<T, RenderTexture, RenderTexture>>();
                return [MethodImpl(MethodImplOptions.AggressiveOptimization)] (MonoBehaviour b, RenderTexture s, RenderTexture t) => d((T)b, s, t);
            }

            public static Func<MonoBehaviour, IEnumerator> Func<T>(MethodInfo m) where T : MonoBehaviour
                => b => (IEnumerator)m.Invoke(b, null);
        }

        /// SendMessage / StartCoroutine(string) lookup: by name with zero or one parameter.
        public static MethodInfo FindByName(Type type, string name, object argument, bool hasArgument)
        {
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t.Assembly != typeof(MonoBehaviour).Assembly; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(Declared))
                {
                    if (m.Name != name || m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0) return m;
                    if (ps.Length == 1 && hasArgument && (argument == null ? !ps[0].ParameterType.IsValueType : ps[0].ParameterType.IsInstanceOfType(argument)))
                        return m;
                }
            }
            return null;
        }
    }
}
