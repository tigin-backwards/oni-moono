using System;
using System.Collections.Generic;
using OniMoono.Engine;

namespace UnityEngine
{
    public partial class GameObject
    {
        public GameObject() : this("New Game Object") { }

        public GameObject(string name)
        {
            Threads.AssertMainThread("GameObject constructor");
            Lifecycle.CreateGameObject(this, name, null);
        }

        public GameObject(string name, params Type[] components)
        {
            Threads.AssertMainThread("GameObject constructor");
            Lifecycle.CreateGameObject(this, name, components);
        }

        /// Engine constructor: the caller binds a native record (deserialization, cloning).
        internal GameObject(NativeObject native) : base() => Bind(native);

        internal GameObjectNative GameObjectNative => (GameObjectNative)NativeOrThrow;

        public Transform transform => (Transform)GameObjectNative.Components[0];

        public GameObject gameObject => this;

        public int layer
        {
            get => GameObjectNative.Layer;
            set
            {
                if (value < 0 || value > 31)
                {
                    Debug.LogError("A game object can only be in one layer. The layer needs to be in the range [0...31]");
                    return;
                }
                var go = GameObjectNative;
                if (go.Layer == value) return;
                go.Layer = value;
                OniMoono.Engine.Rendering.FrameRenderer.LayerChanged(go);
            }
        }

        public bool activeSelf => GameObjectNative.ActiveSelf;
        public bool activeInHierarchy => GameObjectNative.ActiveInHierarchy;

        // The scene the object belongs to; invalid (handle 0) for assets such as prefabs. Mods use
        // it (the game does not).
        public SceneManagement.Scene scene => new(GameObjectNative.Scene);

        public void SetActive(bool value)
        {
            Threads.AssertMainThread("SetActive");
            Lifecycle.SetActive(GameObjectNative, value);
        }

        public bool isStatic
        {
            get => GameObjectNative.IsStatic;
            set => GameObjectNative.IsStatic = value;
        }

        public string tag
        {
            get => GameObjectNative.Tag;
            set
            {
                if (!Tags.IsDefined(value)) throw new UnityException("Tag: " + value + " is not defined.");
                GameObjectNative.Tag = value;
            }
        }

        public bool CompareTag(string tag)
        {
            if (!Tags.IsDefined(tag))
            {
                Debug.LogError("Tag: " + tag + " is not defined.");
                return false;
            }
            return GameObjectNative.Tag == tag;
        }

        // ---- Components ----

        public T AddComponent<T>() where T : Component => (T)AddComponent(typeof(T));

        public Component AddComponent(Type componentType)
        {
            Threads.AssertMainThread("AddComponent");
            return Lifecycle.AddComponent(GameObjectNative, componentType);
        }

        public T GetComponent<T>() => Components.First<T>(GameObjectNative);
        public Component GetComponent(Type type) => (Component)Components.First(GameObjectNative, type);
        public Component GetComponent(string type) => Components.FirstByName(GameObjectNative, type);

        public bool TryGetComponent<T>(out T component)
        {
            component = Components.First<T>(GameObjectNative);
            return component != null;
        }

        public bool TryGetComponent(Type type, out Component component)
        {
            component = (Component)Components.First(GameObjectNative, type);
            return component != null;
        }

        public T[] GetComponents<T>()
        {
            var list = new List<T>();
            Components.All(GameObjectNative, list);
            return list.ToArray();
        }

        public Component[] GetComponents(Type type)
        {
            var list = new List<Component>();
            Components.All(GameObjectNative, type, list);
            return list.ToArray();
        }

        public void GetComponents<T>(List<T> results)
        {
            results.Clear();
            Components.All(GameObjectNative, results);
        }

        public void GetComponents(Type type, List<Component> results)
        {
            results.Clear();
            Components.All(GameObjectNative, type, results);
        }

        public T GetComponentInChildren<T>() => GetComponentInChildren<T>(false);
        public T GetComponentInChildren<T>(bool includeInactive) => (T)(object)Components.InChildren(GameObjectNative, typeof(T), includeInactive);
        public Component GetComponentInChildren(Type type) => GetComponentInChildren(type, false);
        public Component GetComponentInChildren(Type type, bool includeInactive) => (Component)Components.InChildren(GameObjectNative, type, includeInactive);

        public T GetComponentInParent<T>() => GetComponentInParent<T>(false);
        public T GetComponentInParent<T>(bool includeInactive) => (T)(object)Components.InParent(GameObjectNative, typeof(T), includeInactive);
        public Component GetComponentInParent(Type type) => GetComponentInParent(type, false);
        public Component GetComponentInParent(Type type, bool includeInactive) => (Component)Components.InParent(GameObjectNative, type, includeInactive);

        public T[] GetComponentsInChildren<T>() => GetComponentsInChildren<T>(false);

        public T[] GetComponentsInChildren<T>(bool includeInactive)
        {
            var list = new List<T>();
            Components.AllInChildren(GameObjectNative, typeof(T), includeInactive, list);
            return list.ToArray();
        }

        public void GetComponentsInChildren<T>(List<T> results) => GetComponentsInChildren(false, results);

        public void GetComponentsInChildren<T>(bool includeInactive, List<T> results)
        {
            results.Clear();
            Components.AllInChildren(GameObjectNative, typeof(T), includeInactive, results);
        }

        public Component[] GetComponentsInChildren(Type type) => GetComponentsInChildren(type, false);

        public Component[] GetComponentsInChildren(Type type, bool includeInactive)
        {
            var list = new List<Component>();
            Components.AllInChildren(GameObjectNative, type, includeInactive, list);
            return list.ToArray();
        }

        public T[] GetComponentsInParent<T>() => GetComponentsInParent<T>(false);

        public T[] GetComponentsInParent<T>(bool includeInactive)
        {
            var list = new List<T>();
            Components.AllInParent(GameObjectNative, typeof(T), includeInactive, list);
            return list.ToArray();
        }

        public void GetComponentsInParent<T>(bool includeInactive, List<T> results)
        {
            results.Clear();
            Components.AllInParent(GameObjectNative, typeof(T), includeInactive, results);
        }

        public Component[] GetComponentsInParent(Type type) => GetComponentsInParent(type, false);

        public Component[] GetComponentsInParent(Type type, bool includeInactive)
        {
            var list = new List<Component>();
            Components.AllInParent(GameObjectNative, type, includeInactive, list);
            return list.ToArray();
        }

        public int GetComponentCount() => GameObjectNative.Components.Length;

        public Component GetComponentAtIndex(int index)
        {
            var comps = GameObjectNative.Components;
            if ((uint)index >= (uint)comps.Length) throw new ArgumentOutOfRangeException(nameof(index), "Valid range is 0 to GetComponentCount() - 1.");
            return comps[index];
        }

        public int GetComponentIndex(Component component) => Array.IndexOf(GameObjectNative.Components, component);

        // ---- Messages ----

        public void SendMessage(string methodName) => SendMessage(methodName, null, SendMessageOptions.RequireReceiver);
        public void SendMessage(string methodName, object value) => SendMessage(methodName, value, SendMessageOptions.RequireReceiver);
        public void SendMessage(string methodName, SendMessageOptions options) => Messaging.Send(GameObjectNative, methodName, null, false, options);
        public void SendMessage(string methodName, object value, SendMessageOptions options) => Messaging.Send(GameObjectNative, methodName, value, true, options);

        public void BroadcastMessage(string methodName, object parameter, SendMessageOptions options) => Messaging.Broadcast(GameObjectNative, methodName, parameter, true, options);
        public void BroadcastMessage(string methodName, SendMessageOptions options) => Messaging.Broadcast(GameObjectNative, methodName, null, false, options);

        // ---- Finding ----

        /// Finds an active GameObject by name, or by hierarchy path when the name contains '/'.
        public static GameObject Find(string name) => Finder.FindGameObject(name);

        public static GameObject FindWithTag(string tag) => FindGameObjectWithTag(tag);

        public static GameObject FindGameObjectWithTag(string tag)
        {
            if (!Tags.IsDefined(tag)) throw new UnityException("Tag: " + tag + " is not defined.");
            foreach (var go in Finder.ActiveGameObjects())
                if (go.Tag == tag) return (GameObject)go.Managed;
            return null;
        }

        public static GameObject[] FindGameObjectsWithTag(string tag)
        {
            if (!Tags.IsDefined(tag)) throw new UnityException("Tag: " + tag + " is not defined.");
            var list = new List<GameObject>();
            foreach (var go in Finder.ActiveGameObjects())
                if (go.Tag == tag) list.Add((GameObject)go.Managed);
            return list.ToArray();
        }
    }
}
