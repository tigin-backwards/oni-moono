using System;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// The engine's per-transform move flags (OniMoono.Host.TransformMoves, read by name: this
    /// assembly is built against the game's Unity, not ours). Unavailable, the tweaks that need
    /// them stay off.
    internal static class EngineMoves
    {
        static Func<Transform, object, bool> watch;
        static Action<Transform> unwatch;
        static Action<Action<object>> drain;
        static System.Action clear;
        static Func<int> count;
        static readonly Action<object> moved = InstantReachability.TransformMoved;

        public static bool Available => watch != null;

        public static void Bind()
        {
            try
            {
                var type = Type.GetType("OniMoono.Host.TransformMoves, OniMoono", false);
                if (type == null) return;
                watch = (Func<Transform, object, bool>)Delegate.CreateDelegate(typeof(Func<Transform, object, bool>), type.GetMethod("Watch"));
                unwatch = (Action<Transform>)Delegate.CreateDelegate(typeof(Action<Transform>), type.GetMethod("Unwatch"));
                drain = (Action<Action<object>>)Delegate.CreateDelegate(typeof(Action<Action<object>>), type.GetMethod("Drain"));
                clear = (System.Action)Delegate.CreateDelegate(typeof(System.Action), type.GetMethod("Clear"));
                count = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), type.GetProperty("Count").GetGetMethod());
            }
            catch (Exception e)
            {
                watch = null;
                global::Debug.LogWarning($"[Moono] transform moves: not bound: {e.Message}");
            }
        }

        public static bool Watch(Transform t, object token) => watch != null && watch(t, token);
        public static void Unwatch(Transform t) => unwatch?.Invoke(t);
        public static void Drain() => drain?.Invoke(moved);
        public static void Clear() => clear?.Invoke();
        public static int Count => count?.Invoke() ?? 0;
    }
}
