using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OniMoono.Engine;

namespace UnityEngine
{
    public partial class Behaviour
    {
        // The behaviour's engine state, kept on the behaviour itself (docs/research/facade-merge.md,
        // step 2): private, so no derived type's reflected fields include them, and named apart
        // from Unity's members. Set before the constructor body runs (InitBehaviour).
        bool m_EngineEnabledSelf;
        bool m_EngineActiveAndEnabled;
        bool m_EngineAwakeCalled;
        bool m_EngineInAwake;
        bool m_EngineStartCalled;
        bool m_EngineStartPending;
        bool m_EngineUseGUILayout;
        int m_EngineExecutionOrder;
        ScriptInfo m_EngineScript;
        List<CoroutineState> m_EngineCoroutines;

        /// Behaviour.enabled as set (Unity's m_Enabled).
        internal bool EngineEnabledSelf { get => m_EngineEnabledSelf; set => m_EngineEnabledSelf = value; }
        /// OnEnable has run and OnDisable has not run since (Unity's "is in enabled state").
        internal bool EngineActiveAndEnabled { get => m_EngineActiveAndEnabled; set => m_EngineActiveAndEnabled = value; }
        internal bool EngineAwakeCalled { get => m_EngineAwakeCalled; set => m_EngineAwakeCalled = value; }
        /// Its Awake is running, while its GameObject activates (Lifecycle.AwakeAndEnable).
        internal bool EngineInAwake { get => m_EngineInAwake; set => m_EngineInAwake = value; }
        internal bool EngineStartCalled { get => m_EngineStartCalled; set => m_EngineStartCalled = value; }
        /// Scheduled for Start in the next Start phase.
        internal bool EngineStartPending { get => m_EngineStartPending; set => m_EngineStartPending = value; }
        /// MonoBehaviour.useGUILayout: OnGUI gets Layout events and may use GUILayout.
        internal bool EngineUseGUILayout { get => m_EngineUseGUILayout; set => m_EngineUseGUILayout = value; }
        internal int EngineExecutionOrder { get => m_EngineExecutionOrder; set => m_EngineExecutionOrder = value; }
        /// The script's messages (MonoBehaviours only).
        internal ScriptInfo EngineScript { get => m_EngineScript; set => m_EngineScript = value; }
        /// Its coroutines (MonoBehaviours only).
        internal List<CoroutineState> EngineCoroutines { get => m_EngineCoroutines; set => m_EngineCoroutines = value; }

        /// The state a behaviour starts with, as its record started (enabled, using GUILayout).
        internal void InitBehaviour(in ComponentInit init)
        {
            m_EngineEnabledSelf = !init.Disabled;
            m_EngineUseGUILayout = !init.NoGUILayout;
            m_EngineScript = init.Script;
            m_EngineExecutionOrder = init.ExecutionOrder;
        }

        public bool enabled
        {
            get
            {
                _ = EngineGameObjectOrThrow;
                return !EngineDestroyPending && m_EngineEnabledSelf;
            }
            set
            {
                Threads.AssertMainThread("set_enabled");
                var go = EngineGameObjectOrThrow;
                if (EngineDestroyPending || m_EngineEnabledSelf == value) return;
                m_EngineEnabledSelf = value;
                if (!go.ActiveInHierarchy) return;
                if (value) Lifecycle.AwakeAndEnable(this);
                else Lifecycle.Disable(this);
            }
        }

        public bool isActiveAndEnabled => Alive && m_EngineActiveAndEnabled;
    }

    public partial class MonoBehaviour
    {
        public bool didAwake
        {
            get
            {
                _ = EngineGameObjectOrThrow;
                return EngineAwakeCalled;
            }
        }

        /// Whether OnGUI gets Layout events. Turned off, OnGUI skips the layout pass and may not
        /// use GUILayout.
        public bool useGUILayout
        {
            get
            {
                _ = EngineGameObjectOrThrow;
                return EngineUseGUILayout;
            }
            set
            {
                _ = EngineGameObjectOrThrow;
                EngineUseGUILayout = value;
            }
        }
        public bool didStart
        {
            get
            {
                _ = EngineGameObjectOrThrow;
                return EngineStartCalled;
            }
        }

        // The StartCoroutine overloads are not inlined: their frames are in logged traces (LogTraces).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Coroutine StartCoroutine(IEnumerator routine)
        {
            Threads.AssertMainThread("StartCoroutine");
            return Coroutines.Start(this, routine);
        }

        public void Invoke(string methodName, float time)
        {
            Threads.AssertMainThread("Invoke");
            DelayedCalls.Add(this, methodName, time, -1f);
        }

        public void InvokeRepeating(string methodName, float time, float repeatRate)
        {
            Threads.AssertMainThread("InvokeRepeating");
            DelayedCalls.Add(this, methodName, time, repeatRate);
        }

        public void CancelInvoke() => DelayedCalls.Cancel(this, null);

        public void CancelInvoke(string methodName) => DelayedCalls.Cancel(this, methodName);

        public bool IsInvoking() => DelayedCalls.IsPending(this, null);

        public bool IsInvoking(string methodName) => DelayedCalls.IsPending(this, methodName);

        // StartCoroutine(string) passes this for its value: no value, unlike a null one.
        static readonly object NoValue = new();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Coroutine StartCoroutine(string methodName) => StartCoroutine(methodName, NoValue);

        // As Unity's, StartCoroutine(string) calls this one (the logged traces show both).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Coroutine StartCoroutine(string methodName, object value)
        {
            bool hasValue = !ReferenceEquals(value, NoValue);
            if (!hasValue) value = null;
            Threads.AssertMainThread("StartCoroutine");
            if (string.IsNullOrEmpty(methodName)) throw new NullReferenceException("methodName is null or empty");
            var method = ScriptInfo.FindByName(GetType(), methodName, value, hasValue);
            if (method == null || !typeof(IEnumerator).IsAssignableFrom(method.ReturnType))
            {
                Debug.LogError($"Coroutine '{methodName}' couldn't be started!", this);
                return null;
            }
            var routine = (IEnumerator)method.Invoke(this, method.GetParameters().Length == 0 ? null : new[] { value });
            return Coroutines.Start(this, routine, methodName);
        }

        public void StopCoroutine(IEnumerator routine)
        {
            if (routine == null) throw new NullReferenceException("routine is null");
            Coroutines.StopByEnumerator(this.AliveOrThrow(), routine);
        }

        public void StopCoroutine(Coroutine routine)
        {
            if (routine == null) throw new NullReferenceException("routine is null");
            Coroutines.Stop(routine.State);
        }

        public void StopCoroutine(string methodName) => Coroutines.StopByName(this.AliveOrThrow(), methodName);

        public void StopAllCoroutines() => Coroutines.StopAll(this.AliveOrThrow());

        public static void print(object message) => Debug.Log(message);
    }

    public partial class YieldInstruction
    {
    }

    public partial class Coroutine
    {
        // Unity's field: a reference to the engine-side coroutine. Here a weak GCHandle: the
        // scheduler keeps a running coroutine alive; a finished one may be collected, after which
        // the handle reads as finished.
        internal IntPtr m_Ptr;

        internal Coroutine(CoroutineState state) => m_Ptr = GCHandle.ToIntPtr(GCHandle.Alloc(state, GCHandleType.Weak));

        internal CoroutineState State => m_Ptr == IntPtr.Zero ? null : (CoroutineState)GCHandle.FromIntPtr(m_Ptr).Target;

        ~Coroutine()
        {
            if (m_Ptr != IntPtr.Zero) GCHandle.FromIntPtr(m_Ptr).Free();
        }
    }

    public partial class WaitForSeconds
    {
        internal float m_Seconds;
        public WaitForSeconds(float seconds) => m_Seconds = seconds;
    }

    public partial class WaitForFixedUpdate
    {
    }

    public partial class WaitForEndOfFrame
    {
    }

    /// A yield instruction driven by polling keepWaiting each frame. When yielded it runs as a
    /// nested enumerator: MoveNext returns keepWaiting and Current is null.
    public partial class CustomYieldInstruction
    {
        protected CustomYieldInstruction() { }

        public object Current => null;
        public bool MoveNext() => keepWaiting;
        public virtual void Reset() { }
    }

    /// Waits for an amount of unscaled real time. The deadline belongs to the instance: it is
    /// armed by the first keepWaiting query and cleared once it has passed, so every coroutine
    /// yielding the same instance at once shares one deadline, and the instance can be yielded
    /// again afterwards.
    public partial class WaitForSecondsRealtime
    {
        bool deadlineArmed;
        float deadline;

        public WaitForSecondsRealtime(float time) => waitTime = time;

        public float waitTime { get; set; }

        public override bool keepWaiting
        {
            get
            {
                float now = Time.realtimeSinceStartup;
                if (!deadlineArmed)
                {
                    deadline = now + waitTime;
                    deadlineArmed = true;
                }
                if (now < deadline) return true;
                Reset();
                return false;
            }
        }

        public override void Reset() => deadlineArmed = false;
    }

    /// Suspends a coroutine until the condition returns true; it is evaluated once per query.
    public partial class WaitUntil
    {
        readonly Func<bool> condition;

        public WaitUntil(Func<bool> predicate) => condition = predicate;

        public override bool keepWaiting => !condition();
    }
}
