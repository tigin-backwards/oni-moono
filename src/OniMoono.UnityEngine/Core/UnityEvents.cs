using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using OniMoono.Engine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace UnityEngine.Events
{
    /// The constant a persistent call passes in the Int, Float, String, Bool and Object modes.
    /// The serialized fields, in their serialized order, are fixed by the game's data.
    partial class ArgumentCache
    {
        [SerializeField] Object m_ObjectArgument;
        [SerializeField] string m_ObjectArgumentAssemblyTypeName;
        [SerializeField] int m_IntArgument;
        [SerializeField] float m_FloatArgument;
        [SerializeField] string m_StringArgument;
        [SerializeField] bool m_BoolArgument;

        internal Object ObjectArgument => m_ObjectArgument;
        internal int IntArgument => m_IntArgument;
        internal float FloatArgument => m_FloatArgument;
        internal string StringArgument => m_StringArgument;
        internal bool BoolArgument => m_BoolArgument;

        /// The parameter type of an Object-mode method: the named type, or UnityEngine.Object
        /// when the name is empty or names no loaded type.
        internal Type ObjectArgumentType => EventCallbacks.ResolveType(m_ObjectArgumentAssemblyTypeName) ?? typeof(Object);

        // Nothing is derived from the fields here: the owning event binds its calls lazily after
        // its own OnAfterDeserialize.
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() { }
    }

    /// One serialized listener: a method by name on a target, and how it is called.
    partial class PersistentCall
    {
        [SerializeField] Object m_Target;
        [SerializeField] string m_TargetAssemblyTypeName;
        [SerializeField] string m_MethodName;
        [SerializeField] PersistentListenerMode m_Mode = PersistentListenerMode.EventDefined;
        [SerializeField] ArgumentCache m_Arguments = new ArgumentCache();
        [SerializeField] UnityEventCallState m_CallState = UnityEventCallState.RuntimeOnly;

        internal Object Target => m_Target;
        internal string MethodName => m_MethodName ?? "";

        internal UnityEventCallState CallState
        {
            get => m_CallState;
            set => m_CallState = value;
        }

        /// The method this call names, for `owner`'s argument types in the EventDefined mode, or
        /// null when the call does not take part (incomplete, off, or no such method).
        MethodInfo FindMethod(UnityEventBase owner)
        {
            if (string.IsNullOrEmpty(m_MethodName)) return null;
            if (m_CallState == UnityEventCallState.Off) return null;
            // The target's own type when there is one (destroyed or not), whatever the type
            // name says, even when it is empty (measured: the debug element picker's filter
            // field, saved without one, runs its call); otherwise the named type, whose static
            // methods need no target.
            Type type;
            if (m_Target is not null) type = m_Target.GetType();
            else if (string.IsNullOrEmpty(m_TargetAssemblyTypeName)) return null;
            else type = EventCallbacks.ResolveType(m_TargetAssemblyTypeName);
            if (type == null) return null;
            switch (m_Mode)
            {
                case PersistentListenerMode.EventDefined: return owner.FindEventDefinedMethod(m_MethodName, type);
                case PersistentListenerMode.Void: return UnityEventBase.GetValidMethodInfo(type, m_MethodName, Type.EmptyTypes);
                case PersistentListenerMode.Int: return UnityEventBase.GetValidMethodInfo(type, m_MethodName, [typeof(int)]);
                case PersistentListenerMode.Float: return UnityEventBase.GetValidMethodInfo(type, m_MethodName, [typeof(float)]);
                case PersistentListenerMode.String: return UnityEventBase.GetValidMethodInfo(type, m_MethodName, [typeof(string)]);
                case PersistentListenerMode.Bool: return UnityEventBase.GetValidMethodInfo(type, m_MethodName, [typeof(bool)]);
                case PersistentListenerMode.Object:
                {
                    // A value type can never hold a UnityEngine.Object.
                    var argumentType = m_Arguments.ObjectArgumentType;
                    return argumentType.IsValueType ? null : UnityEventBase.GetValidMethodInfo(type, m_MethodName, [argumentType]);
                }
                default: return null;
            }
        }

        /// Binds the call into a listener of `owner`'s delegate type; false when it does not take
        /// part. An instance method needs a live target.
        internal bool TryBind(UnityEventBase owner, out EventListener listener)
        {
            listener = default;
            var method = FindMethod(owner);
            if (method == null) return false;
            if (!method.IsStatic && m_Target == null) return false;
            object target = method.IsStatic ? null : m_Target;
            var args = m_Arguments;
            Delegate callback = m_Mode switch
            {
                PersistentListenerMode.EventDefined => owner.BindEventDefined(target, method),
                PersistentListenerMode.Void => owner.BindParameterless(EventCallbacks.Create<UnityAction>(target, method)),
                PersistentListenerMode.Int => owner.BindParameterless(EventCallbacks.Bind(target, method, args.IntArgument)),
                PersistentListenerMode.Float => owner.BindParameterless(EventCallbacks.Bind(target, method, args.FloatArgument)),
                PersistentListenerMode.String => owner.BindParameterless(EventCallbacks.Bind(target, method, args.StringArgument)),
                PersistentListenerMode.Bool => owner.BindParameterless(EventCallbacks.Bind(target, method, args.BoolArgument)),
                _ => owner.BindParameterless(EventCallbacks.BindObject(target, method, args.ObjectArgumentType, args.ObjectArgument)),
            };
            listener = new EventListener(callback, method.IsStatic ? null : m_Target);
            return true;
        }

        // The fields are the whole state; see ArgumentCache.
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() { }
    }

    partial class PersistentCallGroup
    {
        [SerializeField] List<PersistentCall> m_Calls = new List<PersistentCall>();

        internal int Count => m_Calls.Count;

        /// The call at `index`; ArgumentOutOfRangeException outside the serialized calls.
        internal PersistentCall this[int index] => m_Calls[index];

        /// The calls that take part, bound for `owner`, in their serialized order.
        internal EventListener[] Bind(UnityEventBase owner)
        {
            var bound = new List<EventListener>(m_Calls.Count);
            foreach (var call in m_Calls)
                if (call != null && call.TryBind(owner, out var listener)) bound.Add(listener);
            return bound.ToArray();
        }
    }

    /// An Invoke runs the persistent listeners (bound lazily from the serialized calls) in their
    /// serialized order, then the runtime listeners in the order they were added. It runs a
    /// snapshot: listeners added or removed meanwhile take effect from the next Invoke.
    public abstract partial class UnityEventBase
    {
        [SerializeField] PersistentCallGroup m_PersistentCalls = new PersistentCallGroup();

        readonly List<Delegate> runtimeListeners = new List<Delegate>();
        EventListener[] persistentListeners; // null: bind before the next Invoke
        EventListener[] invocation;          // null: rebuild before the next Invoke

        /// Every serialized call, whether it takes part or not.
        public int GetPersistentEventCount() => m_PersistentCalls.Count;

        public Object GetPersistentTarget(int index) => m_PersistentCalls[index].Target;

        public string GetPersistentMethodName(int index) => m_PersistentCalls[index].MethodName;

        public UnityEventCallState GetPersistentListenerState(int index) => m_PersistentCalls[index].CallState;

        public void SetPersistentListenerState(int index, UnityEventCallState state)
        {
            m_PersistentCalls[index].CallState = state;
            RebindPersistentCalls();
        }

        /// Removes the runtime listeners; persistent ones stay.
        public void RemoveAllListeners()
        {
            if (runtimeListeners.Count == 0) return;
            runtimeListeners.Clear();
            invocation = null;
        }

        public override string ToString() => base.ToString() + " " + GetType().FullName;

        public static MethodInfo GetValidMethodInfo(object obj, string functionName, Type[] argumentTypes)
            => GetValidMethodInfo(obj.GetType(), functionName, argumentTypes);

        /// The method a persistent call binds to: searched from `objectType` up its base types
        /// (System.Object's own methods excluded), public or not, instance or static. On each
        /// type, the method the default binder picks for `argumentTypes` (widening primitive and
        /// reference conversions allowed) is taken unless a parameter's primitiveness differs from
        /// its argument type's (an int does not bind to an object parameter); then the search
        /// goes on with the base type.
        public static MethodInfo GetValidMethodInfo(Type objectType, string functionName, Type[] argumentTypes)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var type = objectType; type != null && type != typeof(object); type = type.BaseType)
            {
                MethodInfo method;
                try
                {
                    method = type.GetMethod(functionName, flags, null, argumentTypes, null);
                }
                catch (AmbiguousMatchException)
                {
                    // Several equally good overloads on this type: none is chosen here.
                    method = null;
                }
                if (method != null && PrimitivenessMatches(method.GetParameters(), argumentTypes)) return method;
            }
            return null;
        }

        static bool PrimitivenessMatches(ParameterInfo[] parameters, Type[] types)
        {
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].ParameterType.IsPrimitive != types[i].IsPrimitive) return false;
            return true;
        }

        internal MethodInfo FindEventDefinedMethod(string name, Type targetObjType) => FindMethod_Impl(name, targetObjType);

        /// A delegate of this event's own UnityAction type for an event-defined method.
        internal abstract Delegate BindEventDefined(object target, MethodInfo method);

        /// A delegate of this event's own UnityAction type that ignores the event's arguments.
        internal abstract Delegate BindParameterless(UnityAction call);

        private protected void AddRuntimeListener(Delegate call)
        {
            ArgumentNullException.ThrowIfNull(call);
            runtimeListeners.Add(call);
            invocation = null;
        }

        /// Removes every runtime listener with the same target and method as `call`.
        private protected void RemoveRuntimeListener(Delegate call)
        {
            if (call is null) return;
            var target = call.Target;
            var method = call.Method;
            for (int i = runtimeListeners.Count - 1; i >= 0; i--)
            {
                var d = runtimeListeners[i];
                if (!ReferenceEquals(d.Target, target) || !d.Method.Equals(method)) continue;
                runtimeListeners.RemoveAt(i);
                invocation = null;
            }
        }

        /// The listeners of the next Invoke. While nothing changes, every Invoke gets the same
        /// array, so invoking allocates nothing.
        private protected EventListener[] PrepareInvocation()
        {
            if (invocation != null) return invocation;
            persistentListeners ??= m_PersistentCalls.Bind(this);
            var list = new EventListener[persistentListeners.Length + runtimeListeners.Count];
            persistentListeners.CopyTo(list, 0);
            for (int i = 0; i < runtimeListeners.Count; i++)
                list[persistentListeners.Length + i] = EventListener.ForRuntime(runtimeListeners[i]);
            return invocation = list;
        }

        void RebindPersistentCalls()
        {
            persistentListeners = null;
            invocation = null;
        }

        // The serialized calls are the persistent state; nothing is written back.
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        // New serialized calls (loading, Instantiate): they bind on the next Invoke.
        void ISerializationCallbackReceiver.OnAfterDeserialize() => RebindPersistentCalls();
    }

    public partial class UnityEvent
    {
        public void AddListener(UnityAction call) => AddRuntimeListener(call);

        public void RemoveListener(UnityAction call) => RemoveRuntimeListener(call);

        public void Invoke()
        {
            var listeners = PrepareInvocation();
            for (int i = 0; i < listeners.Length; i++)
                if (listeners[i].IsAlive) ((UnityAction)listeners[i].Callback)();
        }

        protected override MethodInfo FindMethod_Impl(string name, Type targetObjType) => GetValidMethodInfo(targetObjType, name, Type.EmptyTypes);

        internal override Delegate BindEventDefined(object target, MethodInfo method) => EventCallbacks.Create<UnityAction>(target, method);

        internal override Delegate BindParameterless(UnityAction call) => call;
    }

    public partial class UnityEvent<T0>
    {
        public void AddListener(UnityAction<T0> call) => AddRuntimeListener(call);

        public void RemoveListener(UnityAction<T0> call) => RemoveRuntimeListener(call);

        public void Invoke(T0 arg0)
        {
            var listeners = PrepareInvocation();
            for (int i = 0; i < listeners.Length; i++)
                if (listeners[i].IsAlive) ((UnityAction<T0>)listeners[i].Callback)(arg0);
        }

        protected override MethodInfo FindMethod_Impl(string name, Type targetObjType) => GetValidMethodInfo(targetObjType, name, [typeof(T0)]);

        internal override Delegate BindEventDefined(object target, MethodInfo method) => EventCallbacks.Create<UnityAction<T0>>(target, method);

        internal override Delegate BindParameterless(UnityAction call) => new UnityAction<T0>(_ => call());
    }

    public partial class UnityEvent<T0, T1>
    {
        public void AddListener(UnityAction<T0, T1> call) => AddRuntimeListener(call);

        public void RemoveListener(UnityAction<T0, T1> call) => RemoveRuntimeListener(call);

        public void Invoke(T0 arg0, T1 arg1)
        {
            var listeners = PrepareInvocation();
            for (int i = 0; i < listeners.Length; i++)
                if (listeners[i].IsAlive) ((UnityAction<T0, T1>)listeners[i].Callback)(arg0, arg1);
        }

        protected override MethodInfo FindMethod_Impl(string name, Type targetObjType) => GetValidMethodInfo(targetObjType, name, [typeof(T0), typeof(T1)]);

        internal override Delegate BindEventDefined(object target, MethodInfo method) => EventCallbacks.Create<UnityAction<T0, T1>>(target, method);

        internal override Delegate BindParameterless(UnityAction call) => new UnityAction<T0, T1>((_, _) => call());
    }

    public partial class UnityEvent<T0, T1, T2>
    {
        public void AddListener(UnityAction<T0, T1, T2> call) => AddRuntimeListener(call);

        public void RemoveListener(UnityAction<T0, T1, T2> call) => RemoveRuntimeListener(call);

        public void Invoke(T0 arg0, T1 arg1, T2 arg2)
        {
            var listeners = PrepareInvocation();
            for (int i = 0; i < listeners.Length; i++)
                if (listeners[i].IsAlive) ((UnityAction<T0, T1, T2>)listeners[i].Callback)(arg0, arg1, arg2);
        }

        protected override MethodInfo FindMethod_Impl(string name, Type targetObjType) => GetValidMethodInfo(targetObjType, name, [typeof(T0), typeof(T1), typeof(T2)]);

        internal override Delegate BindEventDefined(object target, MethodInfo method) => EventCallbacks.Create<UnityAction<T0, T1, T2>>(target, method);

        internal override Delegate BindParameterless(UnityAction call) => new UnityAction<T0, T1, T2>((_, _, _) => call());
    }
}

namespace OniMoono.Engine
{
    /// One listener of an invocation: the delegate, of the event's own UnityAction type, and the
    /// UnityEngine.Object whose destruction skips it (an instance method's target; none for
    /// static methods and for targets that are not UnityEngine.Objects).
    internal readonly struct EventListener
    {
        public readonly Delegate Callback;
        readonly Object owner;

        public EventListener(Delegate callback, Object owner)
        {
            Callback = callback;
            this.owner = owner;
        }

        public static EventListener ForRuntime(Delegate call) => new(call, call.Method.IsStatic ? null : call.Target as Object);

        public bool IsAlive => owner is null || owner != null;
    }

    /// Delegates for persistent calls.
    internal static class EventCallbacks
    {
        /// A loaded type by assembly-qualified name, or null.
        public static Type ResolveType(string assemblyQualifiedName)
        {
            if (string.IsNullOrEmpty(assemblyQualifiedName)) return null;
            try
            {
                return Type.GetType(assemblyQualifiedName, throwOnError: false);
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or ArgumentException)
            {
                // The named assembly exists but cannot be loaded, or the name is malformed.
                return null;
            }
        }

        /// A delegate of type TDelegate for `method` on `target` (null for a static method). The
        /// method's parameters accept the delegate's through the default binder's conversions
        /// (GetValidMethodInfo); the arguments are converted the same way, and a method's result,
        /// if it has one, is discarded.
        public static TDelegate Create<TDelegate>(object target, MethodInfo method) where TDelegate : Delegate
        {
            var delegateParameters = typeof(TDelegate).GetMethod("Invoke").GetParameters();
            var methodParameters = method.GetParameters();
            bool direct = method.ReturnType == typeof(void);
            for (int i = 0; direct && i < methodParameters.Length; i++)
                direct = methodParameters[i].ParameterType == delegateParameters[i].ParameterType;
            if (direct)
                return method.IsStatic ? method.CreateDelegate<TDelegate>() : method.CreateDelegate<TDelegate>(target);
            var parameters = Array.ConvertAll(delegateParameters, p => Expression.Parameter(p.ParameterType, p.Name));
            var arguments = new Expression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var type = methodParameters[i].ParameterType;
                arguments[i] = parameters[i].Type == type ? parameters[i] : Expression.Convert(parameters[i], type);
            }
            var instance = method.IsStatic ? null : Expression.Constant(target, method.DeclaringType);
            return Expression.Lambda<TDelegate>(Expression.Call(instance, method, arguments), parameters).Compile();
        }

        /// A one-parameter method called with a constant.
        public static UnityAction Bind<T>(object target, MethodInfo method, T argument)
        {
            var callback = Create<UnityAction<T>>(target, method);
            return () => callback(argument);
        }

        /// An Object-mode method: the argument is passed when it is an instance of the named
        /// argument type, null otherwise. The method's parameter accepts that type (a reference
        /// type: the same or a base type).
        public static UnityAction BindObject(object target, MethodInfo method, Type argumentType, Object argument)
        {
            object value = argumentType.IsInstanceOfType(argument) ? argument : null;
            var bind = typeof(EventCallbacks).GetMethod(nameof(BindReference), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(argumentType);
            return (UnityAction)bind.Invoke(null, [target, method, value]);
        }

        static UnityAction BindReference<T>(object target, MethodInfo method, object argument) where T : class
            => Bind(target, method, (T)argument);
    }
}
