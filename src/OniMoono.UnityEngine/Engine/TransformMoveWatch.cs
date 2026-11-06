using System;
using System.Collections.Generic;

namespace OniMoono.Engine
{
    /// The transforms whose moves something outside the engine follows (the host's
    /// TransformMoves, for the instant-reachability game tweak): each watched transform carries
    /// a token, and every change of its place in the world, through its own position, rotation,
    /// scale or parent or an ancestor's (MarkDirty with moved), queues its token once until the
    /// next Drain. A transform not watched pays one test of its flag on a move and nothing else.
    internal static class TransformMoveWatch
    {
        static readonly object gate = new object();
        static readonly Dictionary<TransformNative, object> tokens = new Dictionary<TransformNative, object>();
        static readonly HashSet<TransformNative> queued = new HashSet<TransformNative>();
        static List<object> pending = new List<object>(), spare = new List<object>();

        public static int Count
        {
            get { lock (gate) return tokens.Count; }
        }

        public static void Watch(TransformNative t, object token)
        {
            lock (gate)
            {
                tokens[t] = token;
                t.MoveWatched = true;
            }
        }

        public static void Unwatch(TransformNative t)
        {
            lock (gate)
            {
                tokens.Remove(t);
                queued.Remove(t);
                t.MoveWatched = false;
            }
        }

        internal static void Note(TransformNative t)
        {
            lock (gate)
            {
                if (tokens.TryGetValue(t, out var token) && queued.Add(t)) pending.Add(token);
            }
        }

        /// Hands each token queued since the last Drain to `each`, once (a token unwatched since
        /// may still come: its owner ignores it).
        public static void Drain(Action<object> each)
        {
            List<object> taken;
            lock (gate)
            {
                if (pending.Count == 0) return;
                taken = pending;
                pending = spare;
                spare = taken;
                queued.Clear();
            }
            try
            {
                foreach (var token in taken) each(token);
            }
            finally
            {
                taken.Clear();
            }
        }

        public static void Clear()
        {
            lock (gate)
            {
                foreach (var t in tokens.Keys) t.MoveWatched = false;
                tokens.Clear();
                queued.Clear();
                pending.Clear();
            }
        }
    }
}
