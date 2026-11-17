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
    ///
    /// An entry may carry a gate, a slot of UI.TextScaleWatch (0: none), which the call reads
    /// to skip it while the slot is disarmed.
    internal sealed class DelegateList<T> where T : Delegate
    {
        /// The subscribers in order; a null is one removed since the last Compact.
        readonly List<T> entries = new();
        /// Each entry's gate (0: none), beside `entries`.
        readonly List<int> gates = new();
        /// Each target's entries, as indices into `entries` in rising order (delegates without
        /// a target, static methods', under `noTarget`). Equal single-target delegates have the
        /// same target, so an entry equal to a delegate is among its target's.
        readonly Dictionary<object, List<int>> byTarget = new(ReferenceEqualityComparer.Instance);
        readonly Stack<List<int>> spare = new();
        static readonly object noTarget = new();
        int live;
        T[] snapshotItems = Array.Empty<T>();
        int[] snapshotGates = Array.Empty<int>();
        bool snapshotTaken;

        public int Count => live;

        public void Add(T value)
        {
            if (value == null) return;
            if (!value.HasSingleTarget)
            {
                foreach (var d in value.GetInvocationList()) Append((T)d, 0);
                return;
            }
            Append(value, 0);
        }

        /// Adds a single-target delegate with a gate (Add for a multicast one).
        public void AddGated(T value, int gate)
        {
            if (value == null) return;
            if (!value.HasSingleTarget)
            {
                Add(value);
                return;
            }
            Append(value, gate);
        }

        void Append(T d, int gate)
        {
            Index(d, entries.Count);
            entries.Add(d);
            gates.Add(gate);
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
                gates.RemoveRange(start, run.Length);
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
                if (entries[i] is { } d)
                {
                    gates[kept] = gates[i];
                    entries[kept++] = d;
                }
            entries.RemoveRange(kept, entries.Count - kept);
            gates.RemoveRange(kept, gates.Count - kept);
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

        /// The subscribers as they are now, with their gates, for a call (Dispose after it). The
        /// copy is kept for the next call; a call made from inside one gets a copy of its own.
        public Lease Take()
        {
            Compact();
            int n = entries.Count;
            if (snapshotTaken) return new Lease(null, entries.ToArray(), gates.ToArray(), n);
            if (snapshotItems.Length < n)
            {
                int size = Math.Max(n, snapshotItems.Length * 2);
                snapshotItems = new T[size];
                snapshotGates = new int[size];
            }
            entries.CopyTo(snapshotItems);
            gates.CopyTo(snapshotGates);
            snapshotTaken = true;
            return new Lease(this, snapshotItems, snapshotGates, n);
        }

        public ref struct Lease
        {
            readonly DelegateList<T> owner;
            readonly T[] items;
            readonly int[] gates;
            readonly int count;

            internal Lease(DelegateList<T> owner, T[] items, int[] gates, int count)
            {
                this.owner = owner;
                this.items = items;
                this.gates = gates;
                this.count = count;
            }

            public readonly ReadOnlySpan<T> Items => new(items, 0, count);

            /// Each item's gate (0: none).
            public readonly ReadOnlySpan<int> Gates => new(gates, 0, count);

            /// Clears the copy (it holds no delegates past the call) and gives it back.
            public readonly void Dispose()
            {
                if (owner == null) return;
                Array.Clear(items, 0, count);
                owner.snapshotTaken = false;
            }
        }
    }
}
