using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OniMoono.Engine;

namespace UnityEngine
{
    public partial class Object
    {
        /// Engine hook: the object is being destroyed (release engine resources).
        internal virtual void EngineOnDestroy() { }
    }

    public partial class Component
    {
        // The component's engine state, kept on the component itself (docs/research/facade-merge.md,
        // step 2): private, so no derived type's reflected fields include them, and named apart
        // from Unity's members. Engine components (transforms, renderers, cameras…) keep the
        // state of their subsystem in their record, the object's m_Native; scripts and plain
        // components have none.
        // (Declared after m_GameObject and m_Transform, below: Localization.SetFont's walk of a
        // field declared as Component meets those first, as before.)

        /// Its GameObject's record. Set before the constructor body runs, cleared when the
        /// component is destroyed (so a destroyed component the game keeps holds nothing of its
        /// object's hierarchy).
        internal GameObject EngineGameObject
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.As<GameObject>(m_EngineGameObject);
        }

        /// EngineGameObject of a component that must be alive; throws Unity's error otherwise.
        internal GameObject EngineGameObjectOrThrow
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (!Alive) ThrowDestroyed();
                return Unsafe.As<GameObject>(m_EngineGameObject);
            }
        }

        /// EngineAwake has run (components that are not Behaviours).
        internal bool EngineAwakeCalledForComponent { get => m_EngineAwakeCalledForComponent; set => m_EngineAwakeCalledForComponent = value; }
        /// EngineOnEnable has run and EngineOnDisable has not run since (components that are not
        /// Behaviours).
        internal bool EngineEnableRan { get => m_EngineEnableRan; set => m_EngineEnableRan = value; }
        /// Destroy was called on the component or its GameObject (or an ancestor) and it awaits
        /// destruction: as in Unity it is disabled at once and is not enabled again.
        internal bool EngineDestroyPending { get => m_EngineDestroyPending; set => m_EngineDestroyPending = value; }

        /// The engine component's record (its subsystem's state) while alive, else null; null for
        /// scripts and plain components.
        internal ComponentNative EngineState
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Alive ? Unsafe.As<ComponentNative>(NativeRecord) : null;
        }

        /// EngineState of a component that must be alive; throws Unity's error otherwise.
        internal ComponentNative EngineStateOrThrow
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (!Alive) ThrowDestroyed();
                return Unsafe.As<ComponentNative>(NativeRecord);
            }
        }

        static void ThrowDestroyed() => throw new NullReferenceException();

        /// A component has no record of its own; these say so at compile time.
        [Obsolete("A component's state is on the component: Alive, EngineState, EngineGameObject.", true)]
        internal new NativeObject Native => throw new InvalidOperationException();
        [Obsolete("A component's state is on the component: EngineStateOrThrow, EngineGameObjectOrThrow.", true)]
        internal new NativeObject NativeOrThrow => throw new InvalidOperationException();

        /// A loaded component's GameObject, known once the component is filled (ObjectLoader).
        internal void SetEngineGameObject(GameObject go)
        {
            m_EngineGameObject = go;
            if (NativeRecord is ComponentNative state) state.GameObject = go;
        }

        /// Binds a component being constructed (Object's constructor, or a class without a
        /// parameterless constructor): its state, then its slot.
        internal void BindComponent(ComponentNative state, in ComponentInit init)
        {
            // What the record kept before it was bound first, then the starting state over it
            // (Instantiate's copy of the source's hide flags, which a record of an engine
            // component would otherwise put back to its own, none).
            if (state is not null)
            {
                state.HandOver(this);
                state.GameObject = init.GameObject;
            }
            m_EngineGameObject = init.GameObject;
            EngineHideFlags = init.HideFlags;
            if (this is Behaviour b) b.InitBehaviour(in init);
            else if (this is Transform t) t.InitTransformState();
            BindAsComponent(state);
        }

        internal override void Unbind()
        {
            base.Unbind();
            m_EngineGameObject = null;
        }

        /// Engine hooks for engine-implemented components (cameras, renderers, canvases…).
        internal virtual void EngineAwake() { }
        /// Applies the defaults of a component created by AddComponent (not loaded from data).
        internal virtual void EngineReset() { }
        internal virtual void EngineOnEnable() { }
        internal virtual void EngineOnDisable() { }

        // The game reads transform and gameObject in nearly every per-object update; kept on the
        // component (one load from an object the caller already holds) instead of walking native
        // record, GameObject and component list each time. A component's GameObject never
        // changes; its transform changes only when a Transform becomes a RectTransform
        // (Lifecycle clears the cache). A destroyed component still throws, as before.
        GameObject m_GameObject;
        Transform m_Transform;
        // Untyped: Localization.SetFont follows a GameObject-typed field into every component of
        // that object, and m_GameObject is to stay the only one (SetFontWalkTests).
        object m_EngineGameObject;
        bool m_EngineAwakeCalledForComponent;
        bool m_EngineEnableRan;
        bool m_EngineDestroyPending;

        public GameObject gameObject
        {
            get
            {
                var go = m_GameObject;
                if (go is not null && Handle != IntPtr.Zero) return go;
                return m_GameObject = EngineGameObjectOrThrow;
            }
        }

        public Transform transform
        {
            get
            {
                var t = m_Transform;
                if (t is not null && Handle != IntPtr.Zero) return t;
                return m_Transform = (Transform)EngineGameObjectOrThrow.Components[0];
            }
        }

        /// The GameObject's transform component was replaced (Transform to RectTransform).
        internal void ForgetTransform() => m_Transform = null;

        public string tag
        {
            get => gameObject.tag;
            set => gameObject.tag = value;
        }

        public bool CompareTag(string tag) => gameObject.CompareTag(tag);

        public Component GetComponent(Type type) => gameObject.GetComponent(type);
        public T GetComponent<T>() => Components.First<T>(EngineGameObjectOrThrow);
        public Component GetComponent(string type) => gameObject.GetComponent(type);

        public bool TryGetComponent(Type type, out Component component) => gameObject.TryGetComponent(type, out component);
        public bool TryGetComponent<T>(out T component) => gameObject.TryGetComponent(out component);

        public Component GetComponentInChildren(Type t, bool includeInactive) => gameObject.GetComponentInChildren(t, includeInactive);
        public Component GetComponentInChildren(Type t) => gameObject.GetComponentInChildren(t, false);
        public T GetComponentInChildren<T>(bool includeInactive) => gameObject.GetComponentInChildren<T>(includeInactive);
        public T GetComponentInChildren<T>() => gameObject.GetComponentInChildren<T>(false);

        public Component[] GetComponentsInChildren(Type t, bool includeInactive) => gameObject.GetComponentsInChildren(t, includeInactive);
        public Component[] GetComponentsInChildren(Type t) => gameObject.GetComponentsInChildren(t, false);
        public T[] GetComponentsInChildren<T>(bool includeInactive) => gameObject.GetComponentsInChildren<T>(includeInactive);
        public void GetComponentsInChildren<T>(bool includeInactive, List<T> result) => gameObject.GetComponentsInChildren(includeInactive, result);
        public T[] GetComponentsInChildren<T>() => gameObject.GetComponentsInChildren<T>(false);
        public void GetComponentsInChildren<T>(List<T> results) => gameObject.GetComponentsInChildren(false, results);

        public Component GetComponentInParent(Type t, bool includeInactive) => gameObject.GetComponentInParent(t, includeInactive);
        public Component GetComponentInParent(Type t) => gameObject.GetComponentInParent(t, false);
        public T GetComponentInParent<T>(bool includeInactive) => gameObject.GetComponentInParent<T>(includeInactive);
        public T GetComponentInParent<T>() => gameObject.GetComponentInParent<T>(false);

        public Component[] GetComponentsInParent(Type t, bool includeInactive) => gameObject.GetComponentsInParent(t, includeInactive);
        public Component[] GetComponentsInParent(Type t) => gameObject.GetComponentsInParent(t, false);
        public T[] GetComponentsInParent<T>(bool includeInactive) => gameObject.GetComponentsInParent<T>(includeInactive);
        public void GetComponentsInParent<T>(bool includeInactive, List<T> results) => gameObject.GetComponentsInParent(includeInactive, results);
        public T[] GetComponentsInParent<T>() => gameObject.GetComponentsInParent<T>(false);

        public Component[] GetComponents(Type type) => gameObject.GetComponents(type);
        public void GetComponents(Type type, List<Component> results) => gameObject.GetComponents(type, results);
        public void GetComponents<T>(List<T> results) => gameObject.GetComponents(results);
        public T[] GetComponents<T>() => gameObject.GetComponents<T>();

        public int GetComponentIndex() => gameObject.GetComponentIndex(this);

        public void SendMessage(string methodName, object value, SendMessageOptions options) => gameObject.SendMessage(methodName, value, options);
        public void SendMessage(string methodName) => gameObject.SendMessage(methodName, null, SendMessageOptions.RequireReceiver);
        public void SendMessage(string methodName, object value) => gameObject.SendMessage(methodName, value, SendMessageOptions.RequireReceiver);
        public void SendMessage(string methodName, SendMessageOptions options) => gameObject.SendMessage(methodName, options);
        public void BroadcastMessage(string methodName, object parameter, SendMessageOptions options) => gameObject.BroadcastMessage(methodName, parameter, options);
        public void BroadcastMessage(string methodName, SendMessageOptions options) => gameObject.BroadcastMessage(methodName, options);
    }
}
