using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine
{
    /// Object searches over the loaded scenes (FindObjectsByType, GameObject.Find, …).
    internal static class Finder
    {
        /// Every GameObject in loaded scenes and DontDestroyOnLoad, in hierarchy order.
        public static IEnumerable<GameObject> AllSceneGameObjects() => Collect(activeOnly: false);

        public static IEnumerable<GameObject> ActiveGameObjects() => Collect(activeOnly: true);

        /// The scenes' GameObjects in hierarchy order (pre-order from each root), collected before
        /// they are returned so callers may change the hierarchy. With activeOnly, inactive
        /// subtrees are skipped whole: no descendant of an inactive object is active.
        static List<GameObject> Collect(bool activeOnly)
        {
            var result = new List<GameObject>();
            var stack = new Stack<Transform>();
            foreach (var scene in Scenes.WithDontDestroyOnLoad())
                foreach (var root in scene.Roots.ToArray())
                {
                    stack.Push(root.Transform);
                    while (stack.Count > 0)
                    {
                        var t = stack.Pop();
                        var go = t.GameObject;
                        if (activeOnly && !go.ActiveInHierarchy) continue;
                        result.Add(go);
                        var children = t.Children;
                        for (int i = children.Length - 1; i >= 0; i--) stack.Push(children[i]);
                    }
                }
            return result;
        }

        /// Objects of `type` that live in scenes: GameObjects and their components. Those on
        /// inactive GameObjects are included only when asked. A disabled Behaviour on an active
        /// GameObject is always included, as measured in the original (UnityProbe CanvasProbe:
        /// FindObjectsByType and FindObjectsOfType with Exclude or without the flag, and
        /// FindFirstObjectByType, return a disabled Canvas and a disabled MonoBehaviour).
        public static IEnumerable<UnityEngine.Object> SceneObjects(Type type, bool includeInactive, FindObjectsSortMode sortMode)
        {
            var results = new List<UnityEngine.Object>();
            bool gameObjects = type.IsAssignableFrom(typeof(GameObject));
            bool components = type == typeof(UnityEngine.Object) || typeof(Component).IsAssignableFrom(type) || type.IsInterface;
            // Whether a concrete component class matches: the same few classes recur thousands of times.
            var matches = new Dictionary<Type, bool>();
            foreach (var go in Collect(activeOnly: !includeInactive))
            {
                if (gameObjects) results.Add(go.Managed);
                if (!components) continue;
                foreach (var c in go.Components)
                {
                    // A missing script has no object to find.
                    if (c is null or MissingScript) continue;
                    var ct = c.GetType();
                    if (!matches.TryGetValue(ct, out bool match)) matches[ct] = match = type.IsAssignableFrom(ct);
                    if (!match) continue;
                    results.Add(c);
                }
            }
            if (sortMode == FindObjectsSortMode.InstanceID)
                results.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            return results;
        }

        /// GameObject.Find: by name among active objects, or by '/'-separated path from a root.
        public static GameObject FindGameObject(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.Contains('/'))
            {
                var parts = name.TrimStart('/').Split('/');
                bool fromRoot = name.StartsWith('/');
                foreach (var go in ActiveGameObjects())
                {
                    if (fromRoot && go.Transform.Parent is not null) continue;
                    if (go.Name != parts[0]) continue;
                    var t = FindPath(go.Transform, parts, 1);
                    if (t is not null && t.GameObject.ActiveInHierarchy) return (GameObject)t.GameObject.Managed;
                }
                return null;
            }
            // Among active objects of that name, the one that became active first (measured in
            // the original: a scene object found before a later DontDestroyOnLoad one and after an
            // earlier one, and an object deactivated and activated again found after one activated
            // in between).
            GameObject first = null;
            foreach (var go in ActiveGameObjects())
                if (go.Name == name && (first is null || go.ActivatedAt < first.ActivatedAt)) first = go;
            return (GameObject)first?.Managed;
        }

        /// Transform.Find semantics: each path segment names a direct child.
        public static Transform FindPath(Transform from, string[] parts, int index)
        {
            if (index == parts.Length) return from;
            foreach (var child in from.Children)
            {
                // A transform's own name was its record's, always "": its GameObject's counts.
                if (parts[index] != "" && child.GameObject.Name != parts[index]) continue;
                var found = FindPath(child, parts, index + 1);
                if (found is not null) return found;
            }
            return null;
        }
    }
}
