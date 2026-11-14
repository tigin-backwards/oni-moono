using System.Collections.Generic;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// The grid as GravityComponents.FixedUpdate reads it (OffWorldFallers, and the unit tests'
    /// own grids).
    internal interface IFallerGrid
    {
        /// Grid.PosToCell(Vector2): the row is `(int)(y + 0.05f)`, so it is below the grid (the
        /// cell negative) only from y = -1.05 down.
        int PosToCell(float x, float y);
        bool IsValidCell(int cell);
        /// Grid.WorldIdx of a valid cell: 255 for none.
        byte World(int cell);
    }

    /// A falling object as the tweak reads it: its position, the speed and the time fallen that
    /// the step stored, and its place in the step's list.
    internal struct Faller
    {
        public float X, Y, VelocityX, VelocityY, Elapsed, GroundOffset;
        public bool MayLeaveWorld, ItemLanding;
        public int Index;
    }

    /// The falling objects (GameComps.Gravities) and the grid, for the tweak's two passes. `T`
    /// names an object (its Transform).
    internal interface IFallerWorld<T> : IFallerGrid
    {
        /// The entries of the step's list.
        int Count { get; }
        /// Entry `i`'s time fallen (no object is read).
        float Elapsed(int i);
        /// Entry `i`, when it is an item that may not leave its world and its object is alive.
        bool Item(int i, out T key, out Faller faller);
        /// An object's entry now; false when it is gone or has none. `falling`: false too when
        /// its entry waits to be removed (GameComps.Gravities.Has).
        bool Find(T key, bool falling, out Faller faller);
        /// GravityComponents.Tuning.maxVelocity.
        float MaxVelocity { get; }
    }

    /// The off-world-fallers tweak's tests, its list of the objects to test and its two passes
    /// around the game's step, without the game types (the unit tests run them against a copy of
    /// the game's step and of the transpiler this replaced).
    internal static class OffWorldFallersCore
    {
        /// Before the step: the objects to test after it, as they are now. A sweep looks at
        /// every falling item; otherwise the watched ones are read again and the ones that just
        /// started falling are added.
        public static void Before<T, W>(FallerCandidates<T> candidates, ref W world, float dt) where W : IFallerWorld<T>
        {
            float maxVelocity = world.MaxVelocity;
            int count = world.Count;
            if (candidates.SweepDue(maxVelocity, dt))
            {
                candidates.BeginSweep();
                for (int i = 0; i < count; i++) Watch(candidates, ref world, i);
            }
            else
            {
                for (int i = candidates.Count - 1; i >= 0; i--)
                {
                    if (world.Find(candidates[i].Key, falling: false, out var faller)) candidates.Set(i, faller.X, faller.Y, faller.Elapsed);
                    else candidates.Drop(i);
                }
                for (int i = 0; i < count; i++)
                    if (Fresh(world.Elapsed(i), dt)) Watch(candidates, ref world, i);
            }
            candidates.Step(maxVelocity, dt);
        }

        static void Watch<T, W>(FallerCandidates<T> candidates, ref W world, int i) where W : IFallerWorld<T>
        {
            if (world.Item(i, out var key, out var faller) && Near(faller.X, faller.Y, faller.GroundOffset)) candidates.Add(key, faller.X, faller.Y, faller.Elapsed);
        }

        /// After the step: the watched objects it left in place for good, into `landing` in the
        /// step's order (the order the transpiler's postfix landed them in).
        public static void After<T, W>(FallerCandidates<T> candidates, ref W world, float dt, List<(int Index, T Key)> landing) where W : IFallerWorld<T>
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var entry = candidates[i];
                if (!world.Find(entry.Key, falling: true, out var f)) continue;
                if (Lands(ref world, entry.Before, f.X, f.Y, f.VelocityX, f.VelocityY, f.Elapsed, f.GroundOffset, dt, f.MayLeaveWorld, f.ItemLanding))
                    landing.Add((f.Index, entry.Key));
            }
            if (landing.Count > 1) landing.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        /// How near the grid's bottom row or its left edge, in cells, an object is watched from.
        public const float Reach = 16f;
        /// The height an object not watched keeps above the rows it could hang at (they are below
        /// y = 1), with room for rounding.
        public const float Slack = 2f;

        /// The step's last test: an object that may not leave its world, in a cell of a world, is
        /// left where it is when its next position is outside that world. `vx`, `vy`: its speed as
        /// the step stored it. The step tests the position it made from the speed before a floor
        /// or a wall changed it, which is this one wherever the cell under the object is off the
        /// grid and it has no sideways speed (HangsForGood), inside the grid's columns.
        [MirrorsGame("System.Void GravityComponents::FixedUpdate(System.Single)")]
        public static bool LeftInPlace<G>(ref G grid, float x, float y, float vx, float vy, float dt, bool mayLeaveWorld) where G : IFallerGrid
        {
            if (mayLeaveWorld) return false;
            int cell = grid.PosToCell(x, y);
            if (!grid.IsValidCell(cell)) return false;
            byte world = grid.World(cell);
            if (world == byte.MaxValue) return false;
            int next = grid.PosToCell(x + vx * dt, y + vy * dt);
            return !(grid.IsValidCell(next) && grid.World(next) == world);
        }

        /// No later step can move an object the step left in place: it is an item (`itemLanding`:
        /// its landing is FallerComponents' item landing), it falls straight down, the cell under
        /// it is off the grid (no liquid can slow it), and at half its speed the next position
        /// would still be below the grid's bottom row, and so would the cell under it (later steps
        /// are no slower; a step's time is fixed for its update rate). The transpiler's test as it
        /// was, in its order of float operations.
        public static bool HangsForGood<G>(ref G grid, float x, float y, float vx, float vy, float groundOffset, float dt, bool itemLanding) where G : IFallerGrid
        {
            if (!itemLanding || vx != 0f || !(vy < 0f)) return false;
            if (grid.IsValidCell(grid.PosToCell(x, y - groundOffset))) return false;
            float slowest = y + vy * 0.5f * dt;
            return grid.PosToCell(x, slowest) < 0 && grid.PosToCell(x, slowest - groundOffset) < 0;
        }

        /// Whether the step that just ran left a watched object in place for good, from what it
        /// was before the step (`before`) and is after it: the step reached it (its time went on
        /// by the step's), did not move it, and both tests hold. Then the transpiler landed it
        /// after this step.
        public static bool Lands<G>(ref G grid, in FallerBefore before, float x, float y, float vx, float vy, float elapsed, float groundOffset, float dt,
            bool mayLeaveWorld, bool itemLanding) where G : IFallerGrid
        {
            float stepped = before.Elapsed + dt;
            if (elapsed != stepped || x != before.X || y != before.Y) return false;
            return LeftInPlace(ref grid, x, y, vx, vy, dt, mayLeaveWorld) && HangsForGood(ref grid, x, y, vx, vy, groundOffset, dt, itemLanding);
        }

        /// Whether an object is watched: near the bottom row, or near the left edge (a cell left
        /// of the grid is negative in the bottom row too).
        public static bool Near(float x, float y, float groundOffset) => !(y >= Reach && y - groundOffset >= Reach && x >= Reach);

        /// Whether an object has had at most one step: it started falling since the step before
        /// the last (during that step, it had one at once and was never seen with none).
        public static bool Fresh(float elapsed, float dt) => elapsed <= dt;
    }

    /// A watched object before a step: where it was and the time it had fallen for.
    internal struct FallerBefore
    {
        public float X, Y, Elapsed;
    }

    /// The falling objects the tweak tests after each step: those near the grid's bottom row. Every
    /// falling item is looked at when the list is made (a sweep) and again once one outside the
    /// list could have fallen into reach: `fallen` adds the furthest a step can move an object,
    /// its top speed times the step's time, and a sweep is due before that passes Reach less
    /// Slack. Between sweeps the objects that just started falling are added (Fresh). An object
    /// another system moves into reach is found at the next sweep.
    internal sealed class FallerCandidates<T>
    {
        public struct Entry
        {
            public T Key;
            public FallerBefore Before;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly HashSet<T> watched = new HashSet<T>();
        float fallen;
        bool swept;

        public int Count => entries.Count;
        public Entry this[int i] => entries[i];

        /// Whether the step about to run needs a sweep first: there was none, or after this step
        /// an object left out of the last one could be within Slack of the bottom row.
        public bool SweepDue(float maxVelocity, float dt) => !swept || !(fallen + Furthest(maxVelocity, dt) <= OffWorldFallersCore.Reach - OffWorldFallersCore.Slack);

        static float Furthest(float maxVelocity, float dt) => (maxVelocity < 0f ? -maxVelocity : maxVelocity) * dt;

        /// A sweep starts: the list is made again.
        public void BeginSweep()
        {
            entries.Clear();
            watched.Clear();
            swept = true;
            fallen = 0f;
        }

        /// The step about to run moves an object at most this far.
        public void Step(float maxVelocity, float dt) => fallen += Furthest(maxVelocity, dt);

        /// Watches an object as it is before the step; false when it is watched already.
        public bool Add(T key, float x, float y, float elapsed)
        {
            if (!watched.Add(key)) return false;
            entries.Add(new Entry { Key = key, Before = new FallerBefore { X = x, Y = y, Elapsed = elapsed } });
            return true;
        }

        /// A watched object as it is before the next step.
        public void Set(int i, float x, float y, float elapsed)
        {
            var entry = entries[i];
            entry.Before = new FallerBefore { X = x, Y = y, Elapsed = elapsed };
            entries[i] = entry;
        }

        /// Stops watching entry `i` (the last entry takes its place).
        public void Drop(int i)
        {
            watched.Remove(entries[i].Key);
            entries[i] = entries[entries.Count - 1];
            entries.RemoveAt(entries.Count - 1);
        }

        public void Clear()
        {
            entries.Clear();
            watched.Clear();
            swept = false;
            fallen = 0f;
        }
    }
}
