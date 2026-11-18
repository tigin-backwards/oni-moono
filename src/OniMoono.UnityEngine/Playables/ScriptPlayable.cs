using System;

namespace UnityEngine.Playables
{
    public partial struct ScriptPlayable<T>
    {
        private PlayableHandle m_Handle;

        private static readonly ScriptPlayable<T> NullInstance = new ScriptPlayable<T>(PlayableHandle.Null);

        public static ScriptPlayable<T> Null => NullInstance;

        /// A playable owning a new T (created with `new T()`, or ScriptableObject.CreateInstance
        /// for ScriptableObject behaviours).
        public static ScriptPlayable<T> Create(PlayableGraph graph, int inputCount = 0)
            => new ScriptPlayable<T>(CreateHandle(graph, null, inputCount));

        /// A playable owning a clone of `template` (Object.Instantiate for engine objects,
        /// ICloneable.Clone otherwise), or a new T if `template` is null.
        public static ScriptPlayable<T> Create(PlayableGraph graph, T template, int inputCount = 0)
            => new ScriptPlayable<T>(CreateHandle(graph, template, inputCount));

        private static PlayableHandle CreateHandle(PlayableGraph graph, T template, int inputCount)
        {
            object instance = template != null ? CloneScriptInstance(template) : CreateScriptInstance();
            if (instance == null)
            {
                Debug.LogError("Could not create a ScriptPlayable of Type " + typeof(T));
                return PlayableHandle.Null;
            }
            PlayableHandle handle = graph.CreatePlayableHandle();
            if (!handle.IsValid()) return PlayableHandle.Null;
            handle.SetInputCount(inputCount);
            handle.SetScriptInstance(instance);
            return handle;
        }

        private static object CreateScriptInstance()
        {
            if (typeof(ScriptableObject).IsAssignableFrom(typeof(T)))
                return ScriptableObject.CreateInstance(typeof(T)) as T;
            return new T();
        }

        private static object CloneScriptInstance(IPlayableBehaviour source)
        {
            if (source is Object engineObject && engineObject)
            {
                Object clone = Object.Instantiate(engineObject);
                if (clone) clone.hideFlags |= HideFlags.DontSave;
                return clone;
            }
            if (source is ICloneable cloneable) return cloneable.Clone();
            return null;
        }

        internal ScriptPlayable(PlayableHandle handle)
        {
            if (handle.IsValid() && !typeof(T).IsAssignableFrom(handle.GetPlayableType()))
                throw new InvalidCastException($"Incompatible handle: Trying to assign a playable data of type `{handle.GetPlayableType()}` that is not compatible with the PlayableBehaviour of type `{typeof(T)}`.");
            m_Handle = handle;
        }

        public PlayableHandle GetHandle() => m_Handle;

        public T GetBehaviour() => m_Handle.GetObject<T>();

        public static implicit operator Playable(ScriptPlayable<T> playable) => new Playable(playable.GetHandle());

        public static explicit operator ScriptPlayable<T>(Playable playable) => new ScriptPlayable<T>(playable.GetHandle());

        public bool Equals(ScriptPlayable<T> other) => m_Handle == other.m_Handle;
    }
}
