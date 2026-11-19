using System;
using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// CritterNeedsFirst's threat turns: the critters that look for a target or are under threat,
    /// each due for an extra brain turn once its last one is a gap old. Plain .NET: the unit
    /// tests compile it in.
    ///
    /// The critters wait in one queue in the order they came due (the gap changes only with the
    /// critter count, so later turns are due later). A turn moves a critter's time without
    /// touching the queue: when its old place comes up, it goes to the back with its new time.
    /// Serve then costs a few queue steps a frame, and allocates nothing once the queue and
    /// table have grown to the critters tracked.
    ///
    /// Bounds, each frame: at most ChecksPerFrame critters tested, and asks paid from a credit
    /// that grows by one for every TurnsPerAsk of the group's turns and never holds more than
    /// one. So threat turns take at most one in TurnsPerAsk of the group's turns (one of the
    /// game's 5 a frame), however many critters are due, and the round robin keeps the rest. A
    /// critter asked for or tested is not tested again before a gap has passed.
    ///
    /// A critter whose `ready` test throws is dropped (counted, the exception kept): a bad key
    /// costs one test, not an exception every frame (#93).
    public sealed class ThreatTurns<T> where T : class
    {
        public const int ChecksPerFrame = 4;
        public const float TurnsPerAsk = 5f;

        struct Entry
        {
            public T Key;
            public float Due;
            public int Stamp;
        }

        struct Slot
        {
            public float Due;
            public int Stamp;
        }

        readonly Queue<Entry> queue = new Queue<Entry>();
        readonly Dictionary<T, Slot> slots = new Dictionary<T, Slot>();
        int stamps;
        float credit;

        /// The critters tracked.
        public int Count => slots.Count;

        /// The keys dropped since the last Clear because `ready` threw, and the last exception.
        public int Dropped { get; private set; }
        public Exception LastError { get; private set; }

        /// A critter starts looking for a target or is under threat: due at `due`.
        public void Track(T key, float due)
        {
            if (slots.TryGetValue(key, out var slot))
            {
                slot.Due = due;
                slots[key] = slot;
                return;
            }
            slots[key] = new Slot { Due = due, Stamp = ++stamps };
            queue.Enqueue(new Entry { Key = key, Due = due, Stamp = stamps });
        }

        /// A tracked critter's turn: due again at `due`. Untracked keys are passed over.
        public void Turned(T key, float due)
        {
            if (!slots.TryGetValue(key, out var slot)) return;
            slot.Due = due;
            slots[key] = slot;
        }

        /// It stops looking or is no longer under threat; its queue place is dropped when reached.
        public void Remove(T key) => slots.Remove(key);

        public void Clear()
        {
            queue.Clear();
            slots.Clear();
            credit = 0f;
            Dropped = 0;
            LastError = null;
        }

        /// One frame: `turns` is the group's turns this frame. Tests the critters due at `now`, in
        /// order, until one is `ready` (then `ask` for its turn) or the frame's checks are
        /// spent. A critter tested is due again `gap` later. `ready` may Remove its key. Returns
        /// the asks made (0 or 1).
        public int Serve(float now, float gap, int turns, Func<T, bool> ready, Action<T> ask)
        {
            credit = Math.Min(credit + turns / TurnsPerAsk, 1f);
            if (credit < 1f) return 0;
            int checks = 0;
            // Each place is visited at most once a call: one moved to the back is not reached again.
            for (int visits = queue.Count; visits > 0 && queue.Count > 0; visits--)
            {
                var entry = queue.Peek();
                if (!slots.TryGetValue(entry.Key, out var slot) || slot.Stamp != entry.Stamp)
                {
                    queue.Dequeue();
                    continue;
                }
                if (entry.Due > now) break;
                if (slot.Due > now)
                {
                    // It had a turn since: its new time, at the back.
                    queue.Dequeue();
                    entry.Due = slot.Due;
                    queue.Enqueue(entry);
                    continue;
                }
                if (checks == ChecksPerFrame) break;
                checks++;
                queue.Dequeue();
                bool isReady;
                try { isReady = ready(entry.Key); }
                catch (Exception e)
                {
                    slots.Remove(entry.Key);
                    Dropped++;
                    LastError = e;
                    continue;
                }
                if (slots.TryGetValue(entry.Key, out slot) && slot.Stamp == entry.Stamp)
                {
                    slot.Due = entry.Due = now + gap;
                    slots[entry.Key] = slot;
                    queue.Enqueue(entry);
                }
                if (isReady)
                {
                    ask(entry.Key);
                    credit -= 1f;
                    return 1;
                }
            }
            return 0;
        }
    }
}
