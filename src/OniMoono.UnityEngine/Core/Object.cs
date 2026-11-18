using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using OniMoono.Engine;

namespace UnityEngine
{
    public partial class Object
    {
        // Unity's only instance field. Here it holds the engine handle (OniMoono.Engine.ObjectTable);
        // zero means "no native object": never created, or destroyed (fake null).
        private IntPtr m_CachedPtr;

        /// The record m_CachedPtr's handle names, held directly: Native is read on nearly every
        /// engine call the game makes (transform, gameObject, GetComponent), and a field read
        /// is cheaper than resolving the handle. It keeps the record alive exactly as long as
        /// this object, as the table's probe link does (ObjectTable.Weaken), and is cleared with
        /// the handle.
        private NativeObject m_Native;

        // The object's engine identity and lifetime (NativeObject reads them here). Private, so
        // no derived type's reflected fields include them, and named apart from Unity's members.
        // Few and small: a component's own fields follow them, and the game's components' fields
        // after those. The table slot is the handle's (m_CachedPtr); where an object was loaded
        // from, which few objects are, is boxed.
        private int m_EngineInstanceId;
        private HideFlags m_EngineHideFlags;
        private bool m_EngineIsPersistent;
        private bool m_EngineDestroyRequested;
        private bool m_EngineDestroyed;
        private object m_EngineSource;

        internal int EngineInstanceId { get => m_EngineInstanceId; set => m_EngineInstanceId = value; }
        internal HideFlags EngineHideFlags { get => m_EngineHideFlags; set => m_EngineHideFlags = value; }
        internal bool EngineIsPersistent { get => m_EngineIsPersistent; set => m_EngineIsPersistent = value; }
        internal bool EngineDestroyRequested { get => m_EngineDestroyRequested; set => m_EngineDestroyRequested = value; }
        internal bool EngineDestroyed { get => m_EngineDestroyed; set => m_EngineDestroyed = value; }
        internal AssetSource EngineSource
        {
            get => m_EngineSource is AssetSource s ? s : default;
            set => m_EngineSource = value == default ? null : value;
        }

        /// Set by the engine around construction of a component or ScriptableObject so that the
        /// base constructor binds the managed object to its native record (Unity creates the
        /// native object first and then runs the managed constructor). A component's record is
        /// its engine state (null for scripts and plain components) and its starting state.
        [ThreadStatic] static NativeObject s_PendingNative;
        [ThreadStatic] static bool s_PendingIsComponent;
        [ThreadStatic] static ComponentInit s_PendingInit;
        /// The type being constructed; null when nothing is pending. A class's field initializers
        /// run before its base constructor, so an object they create (a mod's component holding
        /// `new ColorStyleSetting()`) reaches this constructor first and must not take the record.
        [ThreadStatic] static Type s_PendingType;

        public Object()
        {
            var pendingType = s_PendingType;
            if (pendingType is not null && GetType() == pendingType)
            {
                var pending = s_PendingNative;
                s_PendingNative = null;
                s_PendingType = null;
                if (s_PendingIsComponent) Unsafe.As<Component>(this).BindComponent((ComponentNative)pending, in s_PendingInit);
                else Bind(pending);
            }
        }

        internal static T ConstructBound<T>(Type type, NativeObject native) where T : Object
        {
            // A class without a parameterless constructor is created with no constructor run at
            // all, its base classes' and field initializers included (UnityProbe's missingtypes:
            // Trash Cans' components have only a constructor taking a state).
            if (!HasParameterlessConstructor(type))
            {
                var bare = (T)RuntimeHelpers.GetUninitializedObject(type);
                bare.Bind(native);
                return bare;
            }
            return Construct<T>(type, native, false, default);
        }

        /// Constructs a component bound to its engine state (null for scripts and plain
        /// components), starting as INIT says.
        internal static Component ConstructComponent(Type type, ComponentNative state, in ComponentInit init)
        {
            if (!HasParameterlessConstructor(type))
            {
                var bare = (Component)RuntimeHelpers.GetUninitializedObject(type);
                bare.BindComponent(state, in init);
                return bare;
            }
            return Construct<Component>(type, state, true, init);
        }

        static T Construct<T>(Type type, NativeObject native, bool component, ComponentInit init) where T : Object
        {
            // Field initializers may construct bound objects too (CreateInstance, AddComponent),
            // before the outer object's base constructor runs: its pending binding is put back
            // afterwards.
            var outerNative = s_PendingNative;
            var outerType = s_PendingType;
            var outerIsComponent = s_PendingIsComponent;
            var outerInit = s_PendingInit;
            s_PendingNative = native;
            s_PendingType = type;
            s_PendingIsComponent = component;
            s_PendingInit = init;
            try
            {
                var obj = (T)Activator.CreateInstance(type, nonPublic: true);
                // Bound: the base constructor took the pending binding.
                if (s_PendingType is not null) throw new InvalidOperationException($"{type} did not chain to UnityEngine.Object's constructor");
                return obj;
            }
            finally
            {
                s_PendingNative = outerNative;
                s_PendingType = outerType;
                s_PendingIsComponent = outerIsComponent;
                s_PendingInit = outerInit;
            }
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> parameterless = new();

        static bool HasParameterlessConstructor(Type type) => parameterless.GetOrAdd(type, static t => t.IsAbstract
            || t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null);

        /// Attaches a fresh native record to this managed object.
        internal void Bind(NativeObject native)
        {
            native.HandOver(this);
            if (m_EngineInstanceId == 0) m_EngineInstanceId = ObjectTable.NewInstanceId(m_EngineIsPersistent);
            native.Managed = this;
            m_Native = native;
            Volatile.Write(ref m_CachedPtr, ObjectTable.Register(this));
            if (ObjectCensus.Sites) ObjectCensus.Created(native);
        }

        /// A component's binding: its engine state (null for scripts and plain components), the
        /// state it starts with, its id and its slot. As for a record, all of it is in place
        /// before the component's constructor body runs.
        internal void BindAsComponent(ComponentNative state)
        {
            if (state is not null)
            {
                state.HandOver(this);
                state.Managed = this;
            }
            if (m_EngineInstanceId == 0) m_EngineInstanceId = ObjectTable.NewInstanceId(m_EngineIsPersistent);
            m_Native = state;
            Volatile.Write(ref m_CachedPtr, ObjectTable.Register(this));
        }

        /// Detaches from the native record: from now on this object compares equal to null.
        internal virtual void Unbind()
        {
            var handle = Handle;
            Volatile.Write(ref m_CachedPtr, IntPtr.Zero);
            m_Native = null;
            if (handle != IntPtr.Zero) ObjectTable.Unregister(this, handle);
        }

        /// Whether the object is alive: bound to the engine and not destroyed (Unity's bool
        /// conversion). Safe from any thread.
        internal bool Alive
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref m_CachedPtr) != IntPtr.Zero;
        }

        /// The record, alive or not (the object table's snapshot of its live objects).
        internal NativeObject NativeRecord => m_Native;

        /// The table handle (zero once destroyed).
        internal IntPtr Handle => Volatile.Read(ref m_CachedPtr);

        /// The native record, or null if this object is not alive. Safe from any thread.
        internal NativeObject Native
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref m_CachedPtr) != IntPtr.Zero ? m_Native : null;
        }

        /// The native record of an object that must be alive; throws Unity's error otherwise.
        internal NativeObject NativeOrThrow
        {
            get
            {
                var n = Native;
                if (n is null) ThrowNullReference(this);
                return n;
            }
        }

        /// A player build throws a plain NullReferenceException for a destroyed object (measured:
        /// the original player on GetComponent of a destroyed KCanvasScaler); the detailed
        /// MissingReferenceException is the Editor's.
        static void ThrowNullReference(Object obj) => throw new NullReferenceException();

        /// 0 once the object is destroyed, as in Unity 6.
        public int GetInstanceID() => Alive ? m_EngineInstanceId : 0;

        public EntityId GetEntityId() => Alive ? EntityId.From(m_EngineInstanceId) : EntityId.None;

        public override int GetHashCode() => base.GetHashCode();

        /// A component's name is its GameObject's name.
        public string name
        {
            get
            {
                if (this is Component c) return c.EngineGameObjectOrThrow.Name;
                return NativeOrThrow.Name;
            }
            set
            {
                if (this is Component c) c.EngineGameObjectOrThrow.Name = value ?? "";
                else NativeOrThrow.Name = value ?? "";
            }
        }

        public HideFlags hideFlags
        {
            get
            {
                if (!Alive) ThrowNullReference(this);
                return m_EngineHideFlags;
            }
            set
            {
                if (!Alive) ThrowNullReference(this);
                m_EngineHideFlags = value;
            }
        }

        public override string ToString()
        {
            if (!Alive) return "null";
            return (this is Component c ? c.EngineGameObject.Name : m_Native.Name) + " (" + GetType().FullName + ")";
        }

        // Inlined into callers: the game null-checks objects everywhere (1.4% of a large colony's
        // main thread was calls to the comparison).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsAlive(Object o) => (object)o != null && Volatile.Read(ref o.m_CachedPtr) != IntPtr.Zero;

        /// Unity's equality: a destroyed object, or one without a native object, counts as null,
        /// so it equals null and any other such object; live objects are equal only to themselves.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool SameOrBothNull(Object x, Object y)
        {
            bool xAlive = IsAlive(x), yAlive = IsAlive(y);
            if (xAlive && yAlive) return ReferenceEquals(x, y);
            return !xAlive && !yAlive;
        }

        public override bool Equals(object other)
        {
            if (other is Object o) return SameOrBothNull(this, o);
            // Unity: comparing to anything that isn't a UnityEngine.Object is false, except null.
            return other == null && !IsAlive(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Object x, Object y) => SameOrBothNull(x, y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Object x, Object y) => !SameOrBothNull(x, y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator bool(Object exists) => IsAlive(exists);

        // ---- Destruction ----

        public static void Destroy(Object obj) => Destroy(obj, 0f);

        public static void Destroy(Object obj, float t)
        {
            Threads.AssertMainThread("Destroy");
            if (!IsAlive(obj)) return;
            Lifecycle.ScheduleDestroy(obj, t);
        }

        public static void DestroyImmediate(Object obj) => DestroyImmediate(obj, false);

        /// In the player an asset is destroyed whether or not allowDestroyingAssets is set (measured
        /// in the original player on bundle and game-data prefabs; the refusal is the editor's).
        public static void DestroyImmediate(Object obj, bool allowDestroyingAssets)
        {
            Threads.AssertMainThread("DestroyImmediate");
            if (!IsAlive(obj)) return;
            Lifecycle.DestroyNow(obj);
        }

        public static void DontDestroyOnLoad(Object target)
        {
            Threads.AssertMainThread("DontDestroyOnLoad");
            if (!IsAlive(target)) return;
            GameObject go = target as GameObject ?? (target as Component)?.gameObject;
            if (go == null)
            {
                // Unity accepts only GameObjects and components; other objects are unaffected.
                return;
            }
            Scenes.MoveToDontDestroyOnLoad(go);
        }

        // ---- Finding ----

        public static T FindFirstObjectByType<T>() where T : Object => FindFirstObjectByType<T>(FindObjectsInactive.Exclude);

        public static T FindFirstObjectByType<T>(FindObjectsInactive findObjectsInactive) where T : Object
        {
            foreach (var o in Finder.SceneObjects(typeof(T), findObjectsInactive == FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
                return (T)o;
            return null;
        }

        public static T[] FindObjectsByType<T>(FindObjectsSortMode sortMode) where T : Object
            => FindObjectsByType<T>(FindObjectsInactive.Exclude, sortMode);

        public static T[] FindObjectsByType<T>(FindObjectsInactive findObjectsInactive, FindObjectsSortMode sortMode) where T : Object
        {
            var list = new List<T>();
            foreach (var o in Finder.SceneObjects(typeof(T), findObjectsInactive == FindObjectsInactive.Include, sortMode)) list.Add((T)o);
            return list.ToArray();
        }

        public static T FindAnyObjectByType<T>() where T : Object => FindAnyObjectByType<T>(FindObjectsInactive.Exclude);

        public static T FindAnyObjectByType<T>(FindObjectsInactive findObjectsInactive) where T : Object
        {
            foreach (var o in Finder.SceneObjects(typeof(T), findObjectsInactive == FindObjectsInactive.Include, FindObjectsSortMode.None))
                return (T)o;
            return null;
        }

        public static Object FindFirstObjectByType(Type type) => FindFirstObjectByType(type, FindObjectsInactive.Exclude);

        public static Object FindFirstObjectByType(Type type, FindObjectsInactive findObjectsInactive)
        {
            foreach (var o in Finder.SceneObjects(type, findObjectsInactive == FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
                return o;
            return null;
        }

        public static Object FindAnyObjectByType(Type type) => FindAnyObjectByType(type, FindObjectsInactive.Exclude);

        public static Object FindAnyObjectByType(Type type, FindObjectsInactive findObjectsInactive)
        {
            foreach (var o in Finder.SceneObjects(type, findObjectsInactive == FindObjectsInactive.Include, FindObjectsSortMode.None))
                return o;
            return null;
        }

        // The pre-Unity 6 names, still in Unity 6 as wrappers of the calls above (the game does
        // not use them; mods do).
        public static T FindObjectOfType<T>() where T : Object => FindFirstObjectByType<T>();
        public static T FindObjectOfType<T>(bool includeInactive) where T : Object
            => FindFirstObjectByType<T>(includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);
        public static Object FindObjectOfType(Type type) => FindFirstObjectByType(type);
        public static Object FindObjectOfType(Type type, bool includeInactive)
            => FindFirstObjectByType(type, includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);
        public static T[] FindObjectsOfType<T>() where T : Object => FindObjectsByType<T>(FindObjectsSortMode.InstanceID);
        public static T[] FindObjectsOfType<T>(bool includeInactive) where T : Object
            => FindObjectsByType<T>(includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.InstanceID);
        public static Object[] FindObjectsOfType(Type type) => FindObjectsByType(type, FindObjectsSortMode.InstanceID);
        public static Object[] FindObjectsOfType(Type type, bool includeInactive)
            => FindObjectsByType(type, includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.InstanceID);

        public static Object[] FindObjectsByType(Type type, FindObjectsSortMode sortMode)
            => FindObjectsByType(type, FindObjectsInactive.Exclude, sortMode);

        public static Object[] FindObjectsByType(Type type, FindObjectsInactive findObjectsInactive, FindObjectsSortMode sortMode)
        {
            var list = new List<Object>();
            foreach (var o in Finder.SceneObjects(type, findObjectsInactive == FindObjectsInactive.Include, sortMode)) list.Add(o);
            var result = (Object[])Array.CreateInstance(type, list.Count);
            list.CopyTo(result);
            return result;
        }
    }

    [Serializable]
    public class MissingReferenceException : SystemException
    {
        const int Result = unchecked((int)0x80004003);

        public MissingReferenceException() : base("A Unity Runtime error occurred!") { HResult = Result; }
        public MissingReferenceException(string message) : base(message) { HResult = Result; }
        public MissingReferenceException(string message, Exception innerException) : base(message, innerException) { HResult = Result; }

#pragma warning disable SYSLIB0051 // Unity declares the serialization constructor.
        protected MissingReferenceException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
#pragma warning restore SYSLIB0051
    }
}

namespace OniMoono.Engine
{
    internal static class ObjectChecks
    {
        /// The object, which must be alive; throws Unity's error otherwise.
        public static T AliveOrThrow<T>(this T obj) where T : UnityEngine.Object
        {
            if (!obj.Alive) throw new System.NullReferenceException();
            return obj;
        }
    }
}
