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

        /// Set by the engine around construction of a component or ScriptableObject so that the
        /// base constructor binds the managed object to its native record (Unity creates the
        /// native object first and then runs the managed constructor).
        [ThreadStatic] static NativeObject s_PendingNative;
        /// The type being constructed for s_PendingNative. A class's field initializers run before
        /// its base constructor, so an object they create (a mod's component holding
        /// `new ColorStyleSetting()`) reaches this constructor first and must not take the record.
        [ThreadStatic] static Type s_PendingType;

        public Object()
        {
            var pending = s_PendingNative;
            if (pending is not null && GetType() == s_PendingType)
            {
                s_PendingNative = null;
                s_PendingType = null;
                Bind(pending);
            }
        }

        internal static T ConstructBound<T>(Type type, NativeObject native) where T : Object
        {
            // Field initializers may construct bound objects too (CreateInstance), before the
            // outer object's base constructor runs: its record is put back afterwards.
            var outerNative = s_PendingNative;
            var outerType = s_PendingType;
            // A class without a parameterless constructor is created with no constructor run at
            // all, its base classes' and field initializers included (UnityProbe's missingtypes:
            // Trash Cans' components have only a constructor taking a state).
            if (!HasParameterlessConstructor(type))
            {
                var bare = (T)RuntimeHelpers.GetUninitializedObject(type);
                bare.Bind(native);
                return bare;
            }
            s_PendingNative = native;
            s_PendingType = type;
            try
            {
                var obj = (T)Activator.CreateInstance(type, nonPublic: true);
                if (!ReferenceEquals(native.Managed, obj)) throw new InvalidOperationException($"{type} did not chain to UnityEngine.Object's constructor");
                return obj;
            }
            finally
            {
                s_PendingNative = outerNative;
                s_PendingType = outerType;
            }
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> parameterless = new();

        static bool HasParameterlessConstructor(Type type) => parameterless.GetOrAdd(type, static t => t.IsAbstract
            || t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null);

        /// Attaches a fresh native record to this managed object.
        internal void Bind(NativeObject native)
        {
            if (native.InstanceId == 0) native.InstanceId = ObjectTable.NewInstanceId(native.IsPersistent);
            native.Managed = this;
            m_Native = native;
            Volatile.Write(ref m_CachedPtr, ObjectTable.Register(native));
            if (ObjectCensus.Sites) ObjectCensus.Created(native);
        }

        /// Detaches from the native record: from now on this object compares equal to null.
        internal void Unbind()
        {
            var native = Native;
            Volatile.Write(ref m_CachedPtr, IntPtr.Zero);
            m_Native = null;
            if (native is not null) ObjectTable.Unregister(native);
        }

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
        public int GetInstanceID() => Native?.InstanceId ?? 0;

        public EntityId GetEntityId()
        {
            var n = Native;
            return n is null ? EntityId.None : EntityId.From(n.InstanceId);
        }

        public override int GetHashCode() => base.GetHashCode();

        /// A component's name is its GameObject's name.
        public string name
        {
            get
            {
                var n = NativeOrThrow;
                return n is ComponentNative c ? c.GameObject.Name : n.Name;
            }
            set
            {
                var n = NativeOrThrow;
                if (n is ComponentNative c) c.GameObject.Name = value ?? "";
                else n.Name = value ?? "";
            }
        }

        public HideFlags hideFlags
        {
            get => NativeOrThrow.HideFlags;
            set => NativeOrThrow.HideFlags = value;
        }

        public override string ToString()
        {
            var n = Native;
            if (n is null) return "null";
            return (n is ComponentNative c ? c.GameObject.Name : n.Name) + " (" + GetType().FullName + ")";
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
