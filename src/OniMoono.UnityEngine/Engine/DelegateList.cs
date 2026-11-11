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
    internal sealed class DelegateList<T> where T : Delegate
    {
        readonly List<T> entries = new();
        readonly ListSnapshot<T> snapshot = new();

        public int Count => entries.Count;

        public void Add(T value)
        {
            if (value == null) return;
            if (!value.HasSingleTarget)
            {
                foreach (var d in value.GetInvocationList()) entries.Add((T)d);
                return;
            }
            entries.Add(value);
        }

        public void Remove(T value)
        {
            if (value == null) return;
            if (value.HasSingleTarget)
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (!entries[i].Equals(value)) continue;
                    entries.RemoveAt(i);
                    return;
                }
                return;
            }
            var run = value.GetInvocationList();
            for (int start = entries.Count - run.Length; start >= 0; start--)
            {
                bool match = true;
                for (int k = 0; match && k < run.Length; k++) match = entries[start + k].Equals(run[k]);
                if (!match) continue;
                entries.RemoveRange(start, run.Length);
                return;
            }
        }

        /// The subscribers as they are now, for a call (Dispose after it).
        public ListSnapshot<T>.Lease Take() => snapshot.Take(entries);
    }
}
