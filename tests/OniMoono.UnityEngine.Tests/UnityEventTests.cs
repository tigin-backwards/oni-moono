using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// Methods for persistent calls: some declared on a base type, some non-public.
    public class EventReceiverBase : MonoBehaviour
    {
        public static readonly List<string> Log = new();
        public string label = "";

        void Hidden() => Log.Add(label + ":Hidden");
        protected void TakeValue(int v) => Log.Add(label + ":TakeValue " + v);
        public void Take(int v) => Log.Add(label + ":Take(int) " + v);
    }

    public class EventReceiver : EventReceiverBase
    {
        public int total;

        public void Ping() => Log.Add(label + ":Ping");
        public void Pong() => Log.Add(label + ":Pong");
        public void Throw() => throw new InvalidOperationException("listener failed");
        // A binder would accept an int for this parameter; persistent calls do not.
        public void Take(object v) => Log.Add(label + ":Take(object)");
        void SetFloat(float f) => Log.Add(label + ":SetFloat " + f.ToString(System.Globalization.CultureInfo.InvariantCulture));
        void SetString(string s) => Log.Add(label + ":SetString " + (s ?? "null"));
        void SetBool(bool b) => Log.Add(label + ":SetBool " + b);
        void SetTransform(Transform t) => Log.Add(label + ":SetTransform " + (t == null ? "null" : t.name));
        void SetObject(Object o) => Log.Add(label + ":SetObject " + (o == null ? "null" : o.name));
        void SetComponent(Component c) => Log.Add(label + ":SetComponent " + (c == null ? "null" : c.GetType().Name));
        void Pair(int a, string b) => Log.Add(label + ":Pair " + a + " " + b);
        public void Add(int v) => total += v;
        public void Count() => total++;
        public static void StaticPing() => Log.Add("static:Ping");
    }

    public class EventHolder : MonoBehaviour
    {
        public UnityEvent onFire = new();
        public UnityEvent<int> onValue = new();
    }

    public class UnityEventTests : EngineTest
    {
        static List<string> Log => EventReceiverBase.Log;

        public UnityEventTests() => Log.Clear();

        static EventReceiver Receiver(string label, Transform parent = null)
        {
            var r = new GameObject(label).AddComponent<EventReceiver>();
            r.label = label;
            if (parent != null) r.transform.SetParent(parent, false);
            return r;
        }

        /// Appends a serialized call the way the serializer fills one (by field name), without
        /// telling the event; Deserialized then runs the callback the serializer would run.
        static void AddCall(UnityEventBase evt, Object target, string method, PersistentListenerMode mode = PersistentListenerMode.EventDefined,
            UnityEventCallState state = UnityEventCallState.RuntimeOnly, Type targetType = null, Action<ArgumentCache> arguments = null)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var group = typeof(UnityEventBase).GetField("m_PersistentCalls", flags).GetValue(evt);
            var calls = (List<PersistentCall>)typeof(PersistentCallGroup).GetField("m_Calls", flags).GetValue(group);
            var call = new PersistentCall();
            void Set(string name, object value) => typeof(PersistentCall).GetField(name, flags).SetValue(call, value);
            Set("m_Target", target);
            Set("m_TargetAssemblyTypeName", (targetType ?? target?.GetType())?.AssemblyQualifiedName ?? "");
            Set("m_MethodName", method);
            Set("m_Mode", mode);
            Set("m_CallState", state);
            var args = (ArgumentCache)typeof(PersistentCall).GetField("m_Arguments", flags).GetValue(call);
            arguments?.Invoke(args);
            calls.Add(call);
        }

        static void SetArgument(ArgumentCache args, string field, object value)
            => typeof(ArgumentCache).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(args, value);

        static void Deserialized(UnityEventBase evt) => ((ISerializationCallbackReceiver)evt).OnAfterDeserialize();

        [Fact]
        public void PersistentListenersRunBeforeRuntimeListenersInTheirOrder()
        {
            var a = Receiver("a");
            var b = Receiver("b");
            var evt = new UnityEvent();
            evt.AddListener(() => Log.Add("runtime1"));
            AddCall(evt, b, "Ping", PersistentListenerMode.Void);
            AddCall(evt, a, "Pong", PersistentListenerMode.Void);
            Deserialized(evt);
            evt.AddListener(() => Log.Add("runtime2"));
            evt.Invoke();
            Assert.Equal(["b:Ping", "a:Pong", "runtime1", "runtime2"], Log);
        }

        [Fact]
        public void EveryModeBindsItsMethodAndArgument()
        {
            var r = Receiver("r");
            var other = Receiver("other");
            var evt = new UnityEvent<int>();
            // Event-defined: the event's own argument, to a protected method declared on the base type.
            AddCall(evt, r, "TakeValue");
            // Void: no argument whatever the event carries, to a private method of the base type.
            AddCall(evt, r, "Hidden", PersistentListenerMode.Void);
            AddCall(evt, r, "Take", PersistentListenerMode.Int, arguments: c => SetArgument(c, "m_IntArgument", 42));
            AddCall(evt, r, "SetFloat", PersistentListenerMode.Float, arguments: c => SetArgument(c, "m_FloatArgument", 1.5f));
            AddCall(evt, r, "SetString", PersistentListenerMode.String, arguments: c => SetArgument(c, "m_StringArgument", "text"));
            AddCall(evt, r, "SetBool", PersistentListenerMode.Bool, arguments: c => SetArgument(c, "m_BoolArgument", true));
            // Object: the parameter type is the named argument type.
            AddCall(evt, r, "SetTransform", PersistentListenerMode.Object, arguments: c =>
            {
                SetArgument(c, "m_ObjectArgument", other.transform);
                SetArgument(c, "m_ObjectArgumentAssemblyTypeName", typeof(Transform).AssemblyQualifiedName);
            });
            // An argument that is not an instance of the named type is passed as null.
            AddCall(evt, r, "SetTransform", PersistentListenerMode.Object, arguments: c =>
            {
                SetArgument(c, "m_ObjectArgument", other);
                SetArgument(c, "m_ObjectArgumentAssemblyTypeName", typeof(Transform).AssemblyQualifiedName);
            });
            // An empty or unknown argument type name means UnityEngine.Object.
            AddCall(evt, r, "SetObject", PersistentListenerMode.Object, arguments: c => SetArgument(c, "m_ObjectArgument", other.gameObject));
            AddCall(evt, r, "SetObject", PersistentListenerMode.Object, arguments: c =>
            {
                SetArgument(c, "m_ObjectArgument", other.gameObject);
                SetArgument(c, "m_ObjectArgumentAssemblyTypeName", "No.Such.Type, Nowhere");
            });
            Deserialized(evt);
            evt.Invoke(7);
            Assert.Equal(["r:TakeValue 7", "r:Hidden", "r:Take(int) 42", "r:SetFloat 1.5", "r:SetString text", "r:SetBool True",
                "r:SetTransform other", "r:SetTransform null", "r:SetObject other", "r:SetObject other"], Log);
        }

        [Fact]
        public void EventDefinedCallsTakeAllTheEventsArguments()
        {
            var r = Receiver("r");
            var evt = new UnityEvent<int, string>();
            AddCall(evt, r, "Pair");
            AddCall(evt, r, "Ping"); // event-defined needs (int, string): no match, skipped
            Deserialized(evt);
            evt.Invoke(3, "x");
            Assert.Equal(["r:Pair 3 x"], Log);
        }

        [Fact]
        public void ParameterPrimitivenessMustMatch()
        {
            // The derived Take(object) would accept an int through a binder; it is passed over for
            // the base type's Take(int).
            var found = UnityEventBase.GetValidMethodInfo(typeof(EventReceiver), "Take", [typeof(int)]);
            Assert.Equal(typeof(EventReceiverBase), found.DeclaringType);
            Assert.Equal(typeof(int), found.GetParameters()[0].ParameterType);

            var r = Receiver("r");
            Assert.Equal(typeof(EventReceiver), UnityEventBase.GetValidMethodInfo(r, "Take", [typeof(object)]).DeclaringType);
            // The default binder's conversions apply: a widening primitive conversion and a
            // reference conversion to a base type.
            Assert.Equal(typeof(float), UnityEventBase.GetValidMethodInfo(typeof(EventReceiver), "SetFloat", [typeof(int)]).GetParameters()[0].ParameterType);
            Assert.Equal(typeof(Component), UnityEventBase.GetValidMethodInfo(typeof(EventReceiver), "SetComponent", [typeof(Transform)]).GetParameters()[0].ParameterType);
            Assert.Null(UnityEventBase.GetValidMethodInfo(typeof(EventReceiver), "SetFloat", [typeof(string)]));
            // The search stops before System.Object.
            Assert.Null(UnityEventBase.GetValidMethodInfo(typeof(EventReceiver), "MemberwiseClone", Type.EmptyTypes));

            var evt = new UnityEvent();
            AddCall(evt, r, "Take", PersistentListenerMode.Int, arguments: c => SetArgument(c, "m_IntArgument", 5));
            Deserialized(evt);
            evt.Invoke();
            Assert.Equal(["r:Take(int) 5"], Log);
        }

        [Fact]
        public void ConvertedParametersAreCalledWithConvertedArguments()
        {
            var r = Receiver("r");
            var other = Receiver("other");
            var ints = new UnityEvent<int>();
            AddCall(ints, r, "SetFloat");
            AddCall(ints, r, "SetFloat", PersistentListenerMode.Int, arguments: c => SetArgument(c, "m_IntArgument", 3));
            Deserialized(ints);
            ints.Invoke(2);
            var transforms = new UnityEvent<Transform>();
            AddCall(transforms, r, "SetComponent");
            AddCall(transforms, r, "SetComponent", PersistentListenerMode.Object, arguments: c =>
            {
                SetArgument(c, "m_ObjectArgument", other);
                SetArgument(c, "m_ObjectArgumentAssemblyTypeName", typeof(EventReceiver).AssemblyQualifiedName);
            });
            Deserialized(transforms);
            transforms.Invoke(other.transform);
            Assert.Equal(["r:SetFloat 2", "r:SetFloat 3", "r:SetComponent Transform", "r:SetComponent EventReceiver"], Log);
        }

        [Fact]
        public void OffAndIncompleteCallsAreSkipped()
        {
            var r = Receiver("r");
            var evt = new UnityEvent();
            AddCall(evt, r, "Ping", PersistentListenerMode.Void, UnityEventCallState.Off);
            AddCall(evt, r, "Pong", PersistentListenerMode.Void, UnityEventCallState.EditorAndRuntime);
            AddCall(evt, r, "", PersistentListenerMode.Void);
            AddCall(evt, r, "Missing", PersistentListenerMode.Void);
            // No target and no target type name: nothing names a type.
            AddCall(evt, null, "StaticPing", PersistentListenerMode.Void);
            // An instance method without a target.
            AddCall(evt, null, "Ping", PersistentListenerMode.Void, targetType: typeof(EventReceiver));
            Deserialized(evt);
            evt.Invoke();
            Assert.Equal(["r:Pong"], Log);
            Assert.Equal(6, evt.GetPersistentEventCount());
        }

        [Fact]
        public void ATargetNeedsNoTypeName()
        {
            // As the debug element picker's filter field has it (#54): a String-mode call on a
            // string event, saved with a target but no target type name, runs on the target's
            // own type (measured in the original player).
            var r = Receiver("r");
            var evt = new UnityEvent<string>();
            AddCall(evt, r, "SetString", PersistentListenerMode.String, arguments: c => SetArgument(c, "m_StringArgument", "stored"));
            AddCall(evt, r, "Ping", PersistentListenerMode.Void);
            var group = typeof(UnityEventBase).GetField("m_PersistentCalls", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(evt);
            var calls = (List<PersistentCall>)typeof(PersistentCallGroup).GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(group);
            foreach (var call in calls)
                typeof(PersistentCall).GetField("m_TargetAssemblyTypeName", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(call, "");
            Deserialized(evt);
            evt.Invoke("typed");
            Assert.Equal(["r:SetString stored", "r:Ping"], Log);
        }

        [Fact]
        public void DestroyedTargetsAreSkipped()
        {
            var persistent = Receiver("persistent");
            var runtime = Receiver("runtime");
            var evt = new UnityEvent();
            AddCall(evt, persistent, "Ping", PersistentListenerMode.Void);
            // A static method needs no target: the type comes from the type name.
            AddCall(evt, null, "StaticPing", PersistentListenerMode.Void, targetType: typeof(EventReceiver));
            Deserialized(evt);
            evt.AddListener(runtime.Pong);
            evt.AddListener(EventReceiver.StaticPing);
            evt.AddListener(() => Log.Add("closure"));
            evt.Invoke();
            Assert.Equal(["persistent:Ping", "static:Ping", "runtime:Pong", "static:Ping", "closure"], Log);

            Log.Clear();
            Object.DestroyImmediate(persistent.gameObject);
            Object.DestroyImmediate(runtime.gameObject);
            evt.Invoke();
            Assert.Equal(["static:Ping", "static:Ping", "closure"], Log);

            // A target destroyed before resolution is skipped as well.
            Log.Clear();
            Deserialized(evt);
            evt.Invoke();
            Assert.Equal(["static:Ping", "static:Ping", "closure"], Log);
        }

        [Fact]
        public void ChangesDuringAnInvocationApplyFromTheNext()
        {
            var evt = new UnityEvent();
            UnityAction third = () => Log.Add("third");
            bool changed = false;
            evt.AddListener(() =>
            {
                Log.Add("first");
                if (changed) return;
                changed = true;
                evt.AddListener(() => Log.Add("added"));
                evt.RemoveListener(third);
            });
            evt.AddListener(() => Log.Add("second"));
            evt.AddListener(third);
            evt.Invoke();
            Assert.Equal(["first", "second", "third"], Log);
            Log.Clear();
            evt.Invoke();
            Assert.Equal(["first", "second", "added"], Log);
        }

        [Fact]
        public void AListenersExceptionStopsTheInvocation()
        {
            var r = Receiver("r");
            var evt = new UnityEvent();
            AddCall(evt, r, "Ping", PersistentListenerMode.Void);
            AddCall(evt, r, "Throw", PersistentListenerMode.Void);
            Deserialized(evt);
            evt.AddListener(() => Log.Add("runtime"));
            Assert.Throws<InvalidOperationException>(evt.Invoke);
            Assert.Equal(["r:Ping"], Log);
        }

        [Fact]
        public void RemoveListenerMatchesTargetAndMethod()
        {
            var r = Receiver("r");
            var other = Receiver("other");
            var evt = new UnityEvent();
            AddCall(evt, r, "Ping", PersistentListenerMode.Void);
            Deserialized(evt);
            evt.AddListener(r.Ping);
            evt.AddListener(other.Ping);
            evt.AddListener(r.Pong);
            evt.AddListener(r.Ping);
            // A new delegate for the same target and method removes every copy, runtime only.
            evt.RemoveListener(new UnityAction(r.Ping));
            evt.Invoke();
            Assert.Equal(["r:Ping", "other:Ping", "r:Pong"], Log);

            Log.Clear();
            evt.RemoveAllListeners();
            evt.Invoke();
            Assert.Equal(["r:Ping"], Log);
        }

        [Fact]
        public void GenericRemoveListenerAndRemoveAllListeners()
        {
            var r = Receiver("r");
            var evt = new UnityEvent<int>();
            evt.AddListener(r.Add);
            evt.AddListener(v => Log.Add("closure " + v));
            evt.RemoveListener(r.Add);
            evt.Invoke(1);
            Assert.Equal(["closure 1"], Log);
            Assert.Equal(0, r.total);
            evt.RemoveAllListeners();
            evt.Invoke(2);
            Assert.Equal(["closure 1"], Log);
        }

        [Fact]
        public void SetPersistentListenerStateReresolves()
        {
            var r = Receiver("r");
            var evt = new UnityEvent();
            AddCall(evt, r, "Ping", PersistentListenerMode.Void);
            Deserialized(evt);
            Assert.Same(r, evt.GetPersistentTarget(0));
            Assert.Equal("Ping", evt.GetPersistentMethodName(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => evt.GetPersistentTarget(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => evt.GetPersistentMethodName(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => evt.GetPersistentListenerState(1));
            // A null method name reads as empty.
            AddCall(evt, r, null, PersistentListenerMode.Void);
            Assert.Equal("", evt.GetPersistentMethodName(1));
            Deserialized(evt);
            Assert.Equal(UnityEventCallState.RuntimeOnly, evt.GetPersistentListenerState(0));

            evt.Invoke();
            evt.SetPersistentListenerState(0, UnityEventCallState.Off);
            Assert.Equal(UnityEventCallState.Off, evt.GetPersistentListenerState(0));
            evt.Invoke();
            evt.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
            evt.Invoke();
            Assert.Equal(["r:Ping", "r:Ping"], Log);
        }

        [Fact]
        public void InstantiateRemapsPersistentTargetsAndDropsRuntimeListeners()
        {
            var root = new GameObject("root");
            var holder = root.AddComponent<EventHolder>();
            var inside = Receiver("inside", root.transform);
            var outside = Receiver("outside");
            AddCall(holder.onFire, inside, "Ping", PersistentListenerMode.Void);
            AddCall(holder.onFire, outside, "Pong", PersistentListenerMode.Void);
            AddCall(holder.onValue, inside, "TakeValue");
            Deserialized(holder.onFire);
            Deserialized(holder.onValue);
            holder.onFire.AddListener(() => Log.Add("runtime"));
            holder.onFire.Invoke(); // the original resolves first

            Log.Clear();
            var clone = Object.Instantiate(holder);
            var cloneInside = clone.GetComponentInChildren<EventReceiver>();
            Assert.NotSame(inside, cloneInside);
            cloneInside.label = "clone";
            Assert.NotSame(holder.onFire, clone.onFire);
            Assert.Same(cloneInside, clone.onFire.GetPersistentTarget(0));
            Assert.Same(outside, clone.onFire.GetPersistentTarget(1));

            clone.onFire.Invoke();
            clone.onValue.Invoke(4);
            Assert.Equal(["clone:Ping", "outside:Pong", "clone:TakeValue 4"], Log);

            Log.Clear();
            holder.onFire.Invoke();
            holder.onValue.Invoke(5);
            Assert.Equal(["inside:Ping", "outside:Pong", "runtime", "inside:TakeValue 5"], Log);
        }

        [Fact]
        public void RepeatedInvokeAllocatesNothing()
        {
            var r = Receiver("r");
            var evt = new UnityEvent<int>();
            AddCall(evt, r, "Add");
            AddCall(evt, r, "Add", PersistentListenerMode.Int, arguments: c => SetArgument(c, "m_IntArgument", 100));
            Deserialized(evt);
            evt.AddListener(r.Add);
            var plain = new UnityEvent();
            plain.AddListener(EventReceiver.StaticPing);
            plain.RemoveAllListeners();
            plain.AddListener(r.Count);

            evt.Invoke(1);
            plain.Invoke();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                evt.Invoke(1);
                plain.Invoke();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(101 * 103, r.total);
        }

        [Fact]
        public void ToStringAppendsTheEventsTypeName()
        {
            Assert.Equal("UnityEngine.Events.UnityEvent UnityEngine.Events.UnityEvent", new UnityEvent().ToString());
            Assert.Equal(typeof(UnityEvent<int>).ToString() + " " + typeof(UnityEvent<int>).FullName, new UnityEvent<int>().ToString());
        }
    }
}
