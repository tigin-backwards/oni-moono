using System;
using System.Collections.Generic;

namespace OniMoono.Engine
{
    /// An event's subscribers as a list, called in order as a multicast delegate's invocation
    /// list is: adding appends (each of a multicast delegate's entries), removing takes out the
    /// last run of entries equal to the removed delegate's (Delegate.Remove's rule; nothing when
    /// there is none), and a call goes to the subscribers there were when it began, stopping at
    /// the first exception. A field-like event made a new invocation list at every removal:
    /// TextMeshPro's texts leave Canvas.willRenderCanvases when disabled, copying the list of
    /// every other text's handler each time. Main thread only.
    ///
    /// A removal finds its entry through the delegate's target: every enabled text subscribes
    /// to Canvas.willRenderCanvases (and leaves it first, subscribed or not), so a screen that
    /// enables 1,500 texts searched a list of thousands for each, 46 ms of every opening of
    /// the Skills screen on a colony of 200 Duplicants. A removed entry leaves a gap (null)
    /// that the next call, or the next removal of a multicast delegate, closes.
    internal sealed class DelegateList<T> where T : Delegate
    {
        /// The subscribers in order; a null is one removed since the last Compact.
        readonly List<T> entries = new();
        /// Each target's entries, as indices into `entries` in rising order (delegates without
        /// a target, static methods', under `noTarget`). Equal single-target delegates have the
        /// same target, so an entry equal to a delegate is among its target's.
        readonly Dictionary<object, List<int>> byTarget = new(ReferenceEqualityComparer.Instance);
        readonly Stack<List<int>> spare = new();
        static readonly object noTarget = new();
        int live;
        readonly ListSnapshot<T> snapshot = new();

        public int Count => live;

        public void Add(T value)
        {
            if (value == null) return;
            if (!value.HasSingleTarget)
            {
                foreach (var d in value.GetInvocationList()) Append((T)d);
                return;
            }
            Append(value);
        }

        void Append(T d)
        {
            Index(d, entries.Count);
            entries.Add(d);
            live++;
        }

        void Index(T d, int at)
        {
            var key = d.Target ?? noTarget;
            if (!byTarget.TryGetValue(key, out var indices)) byTarget[key] = indices = spare.Count > 0 ? spare.Pop() : new List<int>(1);
            indices.Add(at);
        }

        public void Remove(T value)
        {
            if (value == null) return;
            if (value.HasSingleTarget)
            {
                var key = value.Target ?? noTarget;
                if (!byTarget.TryGetValue(key, out var indices)) return;
                // The last entry equal to it: the target's entries, last first.
                for (int k = indices.Count - 1; k >= 0; k--)
                {
                    int i = indices[k];
                    if (!entries[i].Equals(value)) continue;
                    entries[i] = null;
                    live--;
                    indices.RemoveAt(k);
                    if (indices.Count == 0)
                    {
                        byTarget.Remove(key);
                        spare.Push(indices);
                    }
                    return;
                }
                return;
            }
            Compact();
            var run = value.GetInvocationList();
            for (int start = entries.Count - run.Length; start >= 0; start--)
            {
                bool match = true;
                for (int k = 0; match && k < run.Length; k++) match = entries[start + k].Equals(run[k]);
                if (!match) continue;
                entries.RemoveRange(start, run.Length);
                live = entries.Count;
                Reindex();
                return;
            }
        }

        /// Closes the gaps removals left.
        void Compact()
        {
            if (entries.Count == live) return;
            int kept = 0;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i] is { } d) entries[kept++] = d;
            entries.RemoveRange(kept, entries.Count - kept);
            Reindex();
        }

        void Reindex()
        {
            foreach (var indices in byTarget.Values)
            {
                indices.Clear();
                spare.Push(indices);
            }
            byTarget.Clear();
            for (int i = 0; i < entries.Count; i++) Index(entries[i], i);
        }

        /// The subscribers as they are now, for a call (Dispose after it).
        public ListSnapshot<T>.Lease Take()
        {
            Compact();
            return snapshot.Take(entries);
        }
    }
}
