using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// Checks to make once a time has come, one per key: a later Set for a key replaces its time
    /// (CritterNeedsFirst: a critter's excrete check, moved by each meal). Take hands out the keys
    /// whose time has come and forgets them. Plain .NET: the unit tests compile it in.
    public sealed class DueChecks<T> where T : class
    {
        readonly Dictionary<T, float> due = new Dictionary<T, float>();
        readonly List<T> ready = new List<T>();
        float earliest = float.PositiveInfinity;

        public int Count => due.Count;

        public void Set(T key, float time)
        {
            due[key] = time;
            if (time < earliest) earliest = time;
        }

        public void Clear()
        {
            due.Clear();
            earliest = float.PositiveInfinity;
        }

        /// The keys due at `now`, removed; the same list each call, empty when none is due.
        public List<T> Take(float now)
        {
            ready.Clear();
            // Nothing can be due before the earliest time set since the last pass.
            if (now < earliest) return ready;
            float next = float.PositiveInfinity;
            foreach (var pair in due)
            {
                if (pair.Value <= now) ready.Add(pair.Key);
                else if (pair.Value < next) next = pair.Value;
            }
            foreach (var key in ready) due.Remove(key);
            earliest = next;
            return ready;
        }
    }
}
