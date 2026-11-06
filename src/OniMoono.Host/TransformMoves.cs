using System;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// The engine's transform move flags, for the game tweaks (instant-reachability, which reads
    /// this class by name: the tweaks are built against the game's assemblies, not ours).
    /// Main thread for Watch, Unwatch and Drain; a move may be noted on any thread.
    public static class TransformMoves
    {
        /// Follows `t`'s moves with `token`; false for a destroyed transform.
        public static bool Watch(UnityEngine.Transform t, object token)
        {
            if (t == null) return false;
            TransformMoveWatch.Watch(t.TransformNative, token);
            return true;
        }

        public static void Unwatch(UnityEngine.Transform t)
        {
            if (t is not null && t.Native is TransformNative native) TransformMoveWatch.Unwatch(native);
        }

        /// Each token whose transform moved since the last call, once.
        public static void Drain(Action<object> each) => TransformMoveWatch.Drain(each);

        /// Forgets every watched transform (a new game).
        public static void Clear() => TransformMoveWatch.Clear();

        public static int Count => TransformMoveWatch.Count;
    }
}
