using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace OniMoono.Engine
{
    /// Component queries with Unity's matching and traversal rules. Read-only over copy-on-write
    /// arrays, so safe from worker threads.
    ///
    /// Which components match a type depends only on the components' types, and objects made from
    /// one prefab share their list of component types: queries look up the matches in that shared
    /// layout (ComponentLayout) instead of testing every component. In a large colony an object has
    /// about 30 components and the game queries them constantly (GetComponent, and every event
    /// handler's GetComponents); testing each one read each component object.
    /// Queries give null where a MissingScript holds a component's place.
    internal static class Components
    {
        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_VERIFY_COMPONENT_LAYOUTS") == "1";

        /// The layout of the object's current component list.
        static ComponentLayout LayoutOf(GameObjectNative go, Component[] components)
        {
            var cached = go.Layout;
            if (cached != null && ReferenceEquals(cached.Source, components)) return cached.Layout;
            var layout = ComponentLayout.Of(components);
            go.Layout = new ComponentLayout.Cached(components, layout);
            return layout;
        }

        /// The object's component layout (shared by every object with the same component types
        /// in the same order), for what the UI package keeps with it (LayoutKinds).
        internal static ComponentLayout LayoutFor(GameObjectNative go) => LayoutOf(go, go.Components);

        /// First component, in component order, assignable to `type` (classes or interfaces).
        public static object First(GameObjectNative go, Type type)
        {
            var components = go.Components;
            var matches = LayoutOf(go, components).Matches(QueryIds.Of(type), type);
            var found = matches.Length == 0 ? null : components[matches[0]];
            if (verify) Verify(go, components, type, found);
            return found is MissingScript ? null : found;
        }

        /// First<T> and All<T>: the generic queries the game makes constantly (GetComponent<T> is
        /// among its most frequent engine calls).
        public static T First<T>(GameObjectNative go)
        {
            var components = go.Components;
            var matches = LayoutOf(go, components).Matches(QueryIds.For<T>.Id, typeof(T));
            if (matches.Length == 0)
            {
                if (verify) Verify(go, components, typeof(T), null);
                return default;
            }
            var found = components[matches[0]];
            if (verify) Verify(go, components, typeof(T), found);
            if (found is MissingScript) return default;
            // A match is a component of a type assignable to T.
            return System.Runtime.CompilerServices.Unsafe.As<Component, T>(ref found);
        }

        public static void All<T>(GameObjectNative go, List<T> results)
        {
            var components = go.Components;
            var matches = LayoutOf(go, components).Matches(QueryIds.For<T>.Id, typeof(T));
            foreach (int i in matches)
            {
                var c = components[i];
                if (c is MissingScript) c = null;
                results.Add(System.Runtime.CompilerServices.Unsafe.As<Component, T>(ref c));
            }
            if (verify)
            {
                int expected = 0;
                foreach (var c in components) if (c is T) expected++;
                if (expected != matches.Length) EngineLog.Engine($"component layouts: {go.Name} has {expected} {typeof(T).Name}, the layout {matches.Length} ({System.Threading.Interlocked.Increment(ref wrong)})");
            }
        }

        static long verified, wrong;

        static void Verify(GameObjectNative go, Component[] components, Type type, Component found)
        {
            Component expected = null;
            foreach (var c in components)
                if (type.IsInstanceOfType(c)) { expected = c; break; }
            if (!ReferenceEquals(expected, found)) EngineLog.Engine($"component layouts: {go.Name} {type.Name} gave {found?.GetType().Name ?? "null"}, expected {expected?.GetType().Name ?? "null"} ({++wrong})");
            if (System.Threading.Interlocked.Increment(ref verified) % 1000000 == 0) EngineLog.Engine($"component layouts: {verified} queries verified, {wrong} wrong");
        }

        /// Unity matches the component's class name (script or engine class).
        public static Component FirstByName(GameObjectNative go, string typeName)
        {
            foreach (var c in go.Components)
            {
                if (c is MissingScript) continue;
                var t = c.GetType();
                if (t.Name == typeName || t.FullName == typeName) return c;
            }
            return null;
        }

        public static void All(GameObjectNative go, Type type, IList results)
        {
            var components = go.Components;
            foreach (int i in LayoutOf(go, components).Matches(QueryIds.Of(type), type)) results.Add(components[i] is MissingScript ? null : components[i]);
        }

        /// Depth-first pre-order over this object and its descendants. The object the search
        /// starts from is always searched; without includeInactive, a descendant that is not
        /// active itself is skipped with its subtree, so activity is judged relative to the
        /// starting object (Unity's rule: the shipped MainMenu deactivates its DLC ad and then
        /// finds the ad's KButton with GetComponentInChildren).
        public static object InChildren(GameObjectNative go, Type type, bool includeInactive)
        {
            var found = First(go, type);
            return found ?? InDescendants(go, type, includeInactive);
        }

        static object InDescendants(GameObjectNative go, Type type, bool includeInactive)
        {
            foreach (var child in go.Transform.Children)
            {
                var g = child.GameObject;
                if (!includeInactive && !g.ActiveSelf) continue;
                var found = First(g, type) ?? InDescendants(g, type, includeInactive);
                if (found != null) return found;
            }
            return null;
        }

        public static void AllInChildren(GameObjectNative go, Type type, bool includeInactive, IList results)
        {
            All(go, type, results);
            AllInDescendants(go, type, includeInactive, results);
        }

        static void AllInDescendants(GameObjectNative go, Type type, bool includeInactive, IList results)
        {
            foreach (var child in go.Transform.Children)
            {
                var g = child.GameObject;
                if (!includeInactive && !g.ActiveSelf) continue;
                All(g, type, results);
                AllInDescendants(g, type, includeInactive, results);
            }
        }

        /// This object, then its ancestors.
        public static object InParent(GameObjectNative go, Type type, bool includeInactive)
        {
            for (var t = go.Transform; t != null; t = t.Parent)
            {
                var g = t.GameObject;
                if (!includeInactive && !g.ActiveInHierarchy) continue;
                var found = First(g, type);
                if (found != null) return found;
            }
            return null;
        }

        public static void AllInParent(GameObjectNative go, Type type, bool includeInactive, IList results)
        {
            for (var t = go.Transform; t != null; t = t.Parent)
            {
                var g = t.GameObject;
                if (!includeInactive && !g.ActiveInHierarchy) continue;
                All(g, type, results);
            }
        }
    }

    /// Tags known to the project: Unity's built-ins plus TagManager's custom tags.
    internal static class Tags
    {
        static readonly HashSet<string> builtIn = new(StringComparer.Ordinal)
        {
            "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController",
        };

        static HashSet<string> custom = new(StringComparer.Ordinal);

        public static void Install(IEnumerable<string> customTags) => custom = new HashSet<string>(customTags, StringComparer.Ordinal);

        public static bool IsDefined(string tag) => tag != null && (builtIn.Contains(tag) || custom.Contains(tag));

        static string[] customList = Array.Empty<string>();

        public static void InstallList(string[] customTags)
        {
            customList = customTags;
            Install(customTags);
        }

        /// GameObject.m_Tag: Unity's built-in tag indices, then custom tags from 20000.
        public static string FromIndex(ushort index) => index switch
        {
            0 => "Untagged",
            1 => "Respawn",
            2 => "Finish",
            3 => "EditorOnly",
            5 => "MainCamera",
            6 => "Player",
            7 => "GameController",
            >= 20000 when index - 20000 < customList.Length => customList[index - 20000],
            _ => throw new System.IO.InvalidDataException($"Unknown tag index {index}."),
        };
    }

    /// SendMessage and BroadcastMessage.
    internal static class Messaging
    {
        public static void Send(GameObjectNative go, string methodName, object value, bool hasValue, SendMessageOptions options)
        {
            Threads.AssertMainThread("SendMessage");
            bool received = false;
            foreach (var c in go.Components)
            {
                if (c is not MonoBehaviour b) continue;
                var m = ScriptInfo.FindByName(b.GetType(), methodName, value, hasValue);
                if (m == null) continue;
                received = true;
                Invoke(b, m, value);
            }
            if (!received && options == SendMessageOptions.RequireReceiver)
                Debug.LogError($"SendMessage {methodName} has no receiver!", (UnityEngine.Object)go.Managed);
        }

        public static void Broadcast(GameObjectNative go, string methodName, object value, bool hasValue, SendMessageOptions options)
        {
            bool received = false;
            void Visit(GameObjectNative g)
            {
                if (!g.ActiveInHierarchy) return;
                foreach (var c in g.Components)
                {
                    if (c is not MonoBehaviour b) continue;
                    var m = ScriptInfo.FindByName(b.GetType(), methodName, value, hasValue);
                    if (m == null) continue;
                    received = true;
                    Invoke(b, m, value);
                }
                foreach (var child in g.Transform.Children) Visit(child.GameObject);
            }
            Visit(go);
            if (!received && options == SendMessageOptions.RequireReceiver)
                Debug.LogError($"BroadcastMessage {methodName} has no receiver!", (UnityEngine.Object)go.Managed);
        }

        static void Invoke(MonoBehaviour b, MethodInfo m, object value)
        {
            try
            {
                m.Invoke(b, m.GetParameters().Length == 0 ? null : new[] { value });
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                Debug.LogException(e.InnerException, b);
            }
        }
    }

    /// Small integer ids for the types components are queried by.
    internal static class QueryIds
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, int> ids = new();
        static int next = -1;

        public static int Of(Type type) => ids.GetOrAdd(type, static _ => System.Threading.Interlocked.Increment(ref next));

        /// How many ids have been handed out (ids are global, so other queries count too).
        public static int Count => System.Threading.Volatile.Read(ref next) + 1;

        public static class For<T>
        {
            public static readonly int Id = Of(typeof(T));
        }
    }

    /// A list of component types, shared by every object whose components have those types in
    /// that order, with the indices of the components matching each queried type.
    internal sealed class ComponentLayout
    {
        /// An object's layout with the component list it was made for.
        internal sealed record Cached(Component[] Source, ComponentLayout Layout);

        readonly Type[] types;
        /// UnityEngine.UI's LayoutRebuilder: 0 until asked, 1 when every component type here
        /// with layout members has only the UI packages' own, 2 when one has its own.
        internal byte LayoutKinds;
        /// By query id: matching indices in component order, or null until first asked.
        int[][] matches = Array.Empty<int[]>();

        ComponentLayout(Type[] types) => this.types = types;

        /// Entries in the match table (tests: it grows with the query ids, not per query).
        internal int TableLength => matches.Length;

        public int[] Matches(int id, Type type)
        {
            var table = System.Threading.Volatile.Read(ref matches);
            if (id < table.Length && table[id] is { } found) return found;
            return Compute(id, type);
        }

        int[] Compute(int id, Type type)
        {
            var list = new List<int>();
            for (int i = 0; i < types.Length; i++)
                if (type.IsAssignableFrom(types[i])) list.Add(i);
            var result = list.Count == 0 ? Array.Empty<int>() : list.ToArray();
            lock (this)
            {
                // Copy on write: readers hold the table they read. It grows only for an id past
                // its end (ids are dense, one per queried type).
                var table = matches;
                var copy = new int[id < table.Length ? table.Length : Math.Max(id + 1, table.Length * 2)][];
                Array.Copy(table, copy, table.Length);
                copy[id] = result;
                System.Threading.Volatile.Write(ref matches, copy);
            }
            return result;
        }

        static readonly Dictionary<Type[], ComponentLayout> interned = new(new TypesComparer());

        public static ComponentLayout Of(Component[] components)
        {
            var types = new Type[components.Length];
            for (int i = 0; i < components.Length; i++) types[i] = components[i].GetType();
            lock (interned)
            {
                if (interned.TryGetValue(types, out var layout)) return layout;
                interned[types] = layout = new ComponentLayout(types);
                return layout;
            }
        }

        sealed class TypesComparer : IEqualityComparer<Type[]>
        {
            public bool Equals(Type[] a, Type[] b) => a.AsSpan().SequenceEqual(b);

            public int GetHashCode(Type[] types)
            {
                var hash = new HashCode();
                foreach (var t in types) hash.Add(t);
                return hash.ToHashCode();
            }
        }
    }
}
