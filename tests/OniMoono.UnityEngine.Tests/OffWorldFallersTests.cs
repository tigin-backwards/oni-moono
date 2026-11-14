using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The off-world-fallers game tweak's passes (src/OniMoono.GameTweaks/OffWorldFallersCore.cs,
    /// compiled in here) against a copy of the game's GravityComponents.FixedUpdate (U59 744825,
    /// U60 763023: the parts an object with no liquid near it runs) and of the transpiler the
    /// passes replaced: the same objects landed after the same steps in the same order, and the
    /// same positions, speeds and list of falling objects after every step.
    public class OffWorldFallersTests
    {
        const float Dt = 0.033f;

        /// A grid of worlds with falling objects: GameComps.Gravities' list (entries are removed
        /// at the frame's end, the last entry taking the freed place), the game's step, and
        /// either the transpiler or the passes around it.
        sealed class Colony : IFallerWorld<int>
        {
            public struct Component
            {
                public int Id;
                public float VelocityX, VelocityY, Elapsed, GroundOffset;
                public bool MayLeaveWorld, Item;
            }

            public readonly int Width, Height;
            public readonly byte[] WorldIdx;
            public readonly bool[] Solid;
            public readonly List<Component> Data = new List<Component>();
            readonly Dictionary<int, int> index = new Dictionary<int, int>();
            readonly HashSet<int> removing = new HashSet<int>();
            readonly List<int> removals = new List<int>();
            public readonly Dictionary<int, (float X, float Y)> Position = new Dictionary<int, (float, float)>();
            public float MaxVelocity { get; set; } = 28f;
            /// Each step's landings by the tweak (not the ones on a floor), in order.
            public readonly List<int> Landed = new List<int>();
            /// Called in the step, after entry `i`'s turn.
            public Action<int> DuringStep;
            public int PositionReads;

            public Colony(int width, int height)
            {
                Width = width;
                Height = height;
                WorldIdx = Enumerable.Repeat(byte.MaxValue, width * height).ToArray();
                Solid = new bool[width * height];
            }

            public void World(byte world, int x0, int y0, int x1, int y1)
            {
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++) WorldIdx[y * Width + x] = world;
            }

            public void Floor(int x0, int x1, int y)
            {
                for (int x = x0; x <= x1; x++) Solid[y * Width + x] = true;
            }

            // Grid.
            public int PosToCell(float x, float y) => (int)(y + 0.05f) * Width + (int)x;
            public bool IsValidCell(int cell) => cell >= 0 && cell < Width * Height;
            public byte World(int cell) => WorldIdx[cell];
            bool IsValidCellInWorld(int cell, int world) => cell >= 0 && cell < Width * Height && WorldIdx[cell] == world;

            // GameComps.Gravities.
            public bool Has(int id) => !removing.Contains(id) && index.ContainsKey(id);

            public void Add(int id, float x, float y, float vx = 0f, float vy = 0f, float groundOffset = 0.95f, bool item = true, bool mayLeaveWorld = false)
            {
                Position[id] = (x, y);
                removing.Remove(id);
                removals.Remove(id);
                var component = new Component { Id = id, VelocityX = vx, VelocityY = vy, GroundOffset = groundOffset, Item = item, MayLeaveWorld = mayLeaveWorld };
                if (index.TryGetValue(id, out int at)) Data[at] = component;
                else
                {
                    index[id] = Data.Count;
                    Data.Add(component);
                }
            }

            public void Remove(int id)
            {
                if (removing.Add(id)) removals.Add(id);
            }

            /// The object is destroyed (its Transform is null from here on).
            public void Destroy(int id)
            {
                Position.Remove(id);
                if (index.ContainsKey(id)) Remove(id);
            }

            /// The frame's end: KComponentManager.CleanUp.
            public void CleanUp()
            {
                foreach (int id in removals)
                {
                    int at = index[id], last = Data.Count - 1;
                    index.Remove(id);
                    if (at < last)
                    {
                        Data[at] = Data[last];
                        index[Data[at].Id] = at;
                    }
                    Data.RemoveAt(last);
                }
                removals.Clear();
                removing.Clear();
            }

            // IFallerWorld.
            public int Count => Data.Count;
            public float Elapsed(int i) => Data[i].Elapsed;

            public bool Item(int i, out int key, out Faller faller)
            {
                var component = Data[i];
                key = component.Id;
                faller = default;
                if (!component.Item || component.MayLeaveWorld || !Position.ContainsKey(key)) return false;
                faller = Read(component, i);
                return true;
            }

            public bool Find(int key, bool falling, out Faller faller)
            {
                faller = default;
                if (!Position.ContainsKey(key) || !index.TryGetValue(key, out int at) || (falling && !Has(key))) return false;
                faller = Read(Data[at], at);
                return true;
            }

            Faller Read(Component component, int i)
            {
                PositionReads++;
                var (x, y) = Position[component.Id];
                return new Faller
                {
                    X = x, Y = y, VelocityX = component.VelocityX, VelocityY = component.VelocityY, Elapsed = component.Elapsed, GroundOffset = component.GroundOffset,
                    MayLeaveWorld = component.MayLeaveWorld, ItemLanding = component.Item, Index = i,
                };
            }

            /// GravityComponents.FixedUpdate, without the liquid, the fake floors and the walls
            /// (objects here have no width). `transpiled`: the transpiler's InWorld in the place
            /// of Grid.IsValidCellInWorld, its landing list filled.
            public void FixedUpdate(float dt, List<int> transpiled)
            {
                float num = MaxVelocity * MaxVelocity;
                for (int i = 0; i < Data.Count; i++)
                {
                    var c = Data[i];
                    if (c.Elapsed < 0f || !Position.ContainsKey(c.Id) || removing.Contains(c.Id))
                    {
                        DuringStep?.Invoke(i);
                        continue;
                    }
                    var (x, y) = Position[c.Id];
                    float vx = c.VelocityX, vy = c.VelocityY + -9.8f * dt;
                    float sqrMagnitude = vx * vx + vy * vy;
                    if (sqrMagnitude > num)
                    {
                        float scale = MaxVelocity / (float)Math.Sqrt(sqrMagnitude);
                        vx *= scale;
                        vy *= scale;
                    }
                    int num2 = PosToCell(x, y);
                    float groundOffset = c.GroundOffset;
                    c.VelocityX = vx;
                    c.VelocityY = vy;
                    c.Elapsed += dt;
                    float nextX = x + vx * dt, nextY = y + vy * dt;
                    bool landed = false;
                    int num6 = PosToCell(nextX, nextY - groundOffset);
                    if (IsValidCell(num6) && Solid[num6])
                    {
                        nextY = (num6 + Width) / Width + groundOffset; // CellToPosCBC(CellAbove(num6)).y
                        c.VelocityX = 0f;
                        landed = true;
                    }
                    Data[i] = c;
                    int cell = PosToCell(nextX, nextY);
                    bool left = !c.MayLeaveWorld && IsValidCell(num2) && WorldIdx[num2] != byte.MaxValue
                        && !(transpiled != null ? InWorld(cell, WorldIdx[num2], c, x, y, groundOffset, dt, transpiled) : IsValidCellInWorld(cell, WorldIdx[num2]));
                    if (!left)
                    {
                        Position[c.Id] = (nextX, nextY);
                        // onLanded: an item's is FallerComponents.OnLanded, which removes its gravity.
                        if (landed && c.Item && Has(c.Id)) Remove(c.Id);
                    }
                    DuringStep?.Invoke(i);
                }
            }

            /// The transpiler's InWorld (OffWorldFallers.Transpiled).
            bool InWorld(int cell, int world, Component component, float x, float y, float groundOffset, float dt, List<int> landing)
            {
                if (IsValidCellInWorld(cell, world)) return true;
                if (component.Item && component.VelocityX == 0f && component.VelocityY < 0f && !IsValidCell(PosToCell(x - 0f, y - groundOffset)))
                {
                    float slowestX = x + 0f * dt, slowestY = y + component.VelocityY * 0.5f * dt;
                    if (PosToCell(slowestX, slowestY) < 0 && PosToCell(slowestX - 0f, slowestY - groundOffset) < 0) landing.Add(component.Id);
                }
                return false;
            }

            readonly List<int> transpilerLanding = new List<int>();

            /// A step with the transpiler and its postfix.
            public void StepTranspiled(float dt)
            {
                Landed.Clear();
                FixedUpdate(dt, transpilerLanding);
                foreach (int id in transpilerLanding)
                {
                    if (!Position.ContainsKey(id) || !Has(id)) continue;
                    Remove(id);
                    Landed.Add(id);
                }
                transpilerLanding.Clear();
            }

            public readonly FallerCandidates<int> Candidates = new FallerCandidates<int>();
            readonly List<(int Index, int Key)> landing = new List<(int, int)>();
            public int Sweeps;

            /// A step with the tweak's passes around the game's own.
            public void StepWatched(float dt)
            {
                Landed.Clear();
                var world = this;
                if (Candidates.SweepDue(MaxVelocity, dt)) Sweeps++;
                OffWorldFallersCore.Before(Candidates, ref world, dt);
                FixedUpdate(dt, null);
                OffWorldFallersCore.After(Candidates, ref world, dt, landing);
                foreach (var (_, id) in landing)
                {
                    if (!Position.ContainsKey(id) || !Has(id)) continue;
                    Remove(id);
                    Landed.Add(id);
                }
                landing.Clear();
            }

            public string State() => string.Join(";", Data.Select(c =>
                $"{c.Id}:{(Position.TryGetValue(c.Id, out var p) ? $"{p.X:R},{p.Y:R}" : "gone")}:{c.VelocityX:R},{c.VelocityY:R}:{c.Elapsed:R}:{(Has(c.Id) ? "" : "removing")}"));
        }

        /// A 40 by 30 grid: world 0 on columns 0-23 with a floor on rows 0-1 but for a hole at
        /// columns 10-11 (a chute through the bottom); a rocket interior, world 1, at columns
        /// 26-33 on the grid's bottom rows 0-7 with a floor at row 1 and a hole at columns 29-30;
        /// another, world 2, at columns 26-33 on rows 12-19 with a floor at row 13 and a hole at
        /// columns 29-30; no world elsewhere.
        static Colony NewColony()
        {
            var colony = new Colony(40, 30);
            colony.World(0, 0, 0, 23, 29);
            colony.Floor(0, 9, 0);
            colony.Floor(12, 23, 0);
            colony.Floor(0, 9, 1);
            colony.Floor(12, 23, 1);
            colony.World(1, 26, 0, 33, 7);
            colony.Floor(26, 28, 1);
            colony.Floor(31, 33, 1);
            colony.Floor(26, 28, 0);
            colony.Floor(31, 33, 0);
            colony.World(2, 26, 12, 33, 19);
            colony.Floor(26, 28, 13);
            colony.Floor(31, 33, 13);
            colony.Floor(26, 28, 12);
            colony.Floor(31, 33, 12);
            return colony;
        }

        /// Runs one object with both forms; the step (from 1) after which each landed it, 0 for never.
        static (int Transpiled, int Watched, Colony After) Drop(Action<Colony> setUp, int steps = 200, Action<Colony, int> between = null)
        {
            var a = NewColony();
            var b = NewColony();
            setUp(a);
            setUp(b);
            int landedA = 0, landedB = 0;
            for (int step = 1; step <= steps; step++)
            {
                between?.Invoke(a, step);
                between?.Invoke(b, step);
                a.StepTranspiled(Dt);
                b.StepWatched(Dt);
                if (landedA == 0 && a.Landed.Count > 0) landedA = step;
                if (landedB == 0 && b.Landed.Count > 0) landedB = step;
                if (step % 3 == 0)
                {
                    a.CleanUp();
                    b.CleanUp();
                }
            }
            return (landedA, landedB, b);
        }

        [Fact]
        public void AnItemThroughTheBottomOfARocketInteriorLandsAfterTheSameStep()
        {
            var (transpiled, watched, colony) = Drop(c => c.Add(1, 29.5f, 5.5f));
            Assert.True(transpiled > 0);
            Assert.Equal(transpiled, watched);
            Assert.False(colony.Has(1));
            // It hangs with the cell under it below the grid's bottom row.
            var (_, y) = colony.Position[1];
            Assert.InRange(y, -1.05f, 0f);
            Assert.True(colony.PosToCell(29.5f, y - 0.95f) < 0);
        }

        [Fact]
        public void AnItemThroughTheBottomOfTheMainWorldLandsToo()
        {
            var (transpiled, watched, _) = Drop(c => c.Add(1, 10.5f, 20.5f));
            Assert.True(transpiled > 0);
            Assert.Equal(transpiled, watched);
        }

        /// It hangs in the bottom row (the step that would take it below y = -1.05 leaves it at
        /// about -1.03) and the cell under it, here its own, is still on the grid: liquid there
        /// could slow it, so neither form lands it.
        [Fact]
        public void AnItemHangingWithItsFootOnTheBottomRowStaysFalling()
        {
            var (transpiled, watched, colony) = Drop(c => c.Add(1, 29.5f, 0.8f, groundOffset: 0f));
            Assert.Equal(0, transpiled);
            Assert.Equal(0, watched);
            Assert.True(colony.Has(1));
            var (_, y) = colony.Position[1];
            Assert.InRange(y, -1.05f, -0.95f);
            Assert.True(colony.IsValidCell(colony.PosToCell(29.5f, y)));
            Assert.True(colony.Data[0].Elapsed > 6f);
        }

        /// The same object with its foot a quarter of a cell lower: that is below the grid where
        /// it hangs, and it lands.
        [Fact]
        public void TheSameItemWithItsFootBelowTheBottomRowLands()
        {
            var (transpiled, watched, colony) = Drop(c => c.Add(1, 29.5f, 0.8f, groundOffset: 0.25f));
            Assert.True(transpiled > 0);
            Assert.Equal(transpiled, watched);
            Assert.False(colony.Has(1));
            Assert.InRange(colony.Position[1].Y, -1.05f, -0.95f);
        }

        /// An interior that is not on the grid's bottom rows: the item hangs in its bottom row
        /// with grid cells of no world under it, stepped forever, and neither form lands it.
        [Fact]
        public void AnItemBelowAnInteriorAboveTheBottomRowsStaysFalling()
        {
            var (transpiled, watched, colony) = Drop(c => c.Add(1, 29.5f, 17.5f));
            Assert.Equal(0, transpiled);
            Assert.Equal(0, watched);
            Assert.True(colony.Has(1));
            var (_, y) = colony.Position[1];
            Assert.Equal(2, colony.WorldIdx[colony.PosToCell(29.5f, y)]);
            Assert.InRange(y, 11.95f, 13f);
        }

        [Fact]
        public void OnlyItemsThatMayNotLeaveTheirWorldLand()
        {
            // A critter (its landing is not the item one) hangs and stays falling.
            var critter = Drop(c => c.Add(1, 29.5f, 5.5f, item: false));
            Assert.Equal((0, 0), (critter.Transpiled, critter.Watched));
            Assert.True(critter.After.Has(1));
            // A Duplicant may leave its world: it falls out of the grid.
            var dupe = Drop(c => c.Add(1, 29.5f, 5.5f, mayLeaveWorld: true));
            Assert.Equal((0, 0), (dupe.Transpiled, dupe.Watched));
            Assert.True(dupe.After.Position[1].Y < -100f);
        }

        [Fact]
        public void AnItemWithSidewaysSpeedStaysFalling()
        {
            var (transpiled, watched, colony) = Drop(c => c.Add(1, 29.2f, 3.5f, vx: 0.5f));
            Assert.Equal((0, 0), (transpiled, watched));
            Assert.True(colony.Has(1));
        }

        /// The interior is destroyed (its cells are of no world) while the item falls: the step
        /// moves it out of the grid, and nothing lands it.
        [Fact]
        public void AnItemWhoseWorldIsRemovedFallsOutOfTheGrid()
        {
            var (transpiled, watched, colony) = Drop(c => c.Add(1, 29.5f, 5.5f), between: (c, step) =>
            {
                if (step == 5) c.World(byte.MaxValue, 26, 0, 33, 7);
            });
            Assert.Equal((0, 0), (transpiled, watched));
            Assert.True(colony.Position[1].Y < -100f);
        }

        /// The interior is destroyed after the landing: the item stays where it was landed, as
        /// with the transpiler.
        [Fact]
        public void ALandedItemStaysWhenItsWorldIsRemoved()
        {
            float landedAt = 0f;
            var (transpiled, watched, colony) = Drop(c => c.Add(1, 29.5f, 5.5f), between: (c, step) =>
            {
                if (step == 100)
                {
                    landedAt = c.Position[1].Y;
                    c.World(byte.MaxValue, 26, 0, 33, 7);
                }
            });
            Assert.Equal(transpiled, watched);
            Assert.InRange(watched, 1, 99);
            Assert.Equal(landedAt, colony.Position[1].Y);
        }

        /// Another system lifts a watched item back into its world just before the step that
        /// would land it: it is not landed then, and falls again. Where it hangs the second time
        /// decides whether it lands (it comes faster, and may stop too high for the half-speed
        /// test); both forms agree, step for step.
        [Fact]
        public void AnItemMovedBackIntoItsWorldFallsAgain()
        {
            var plain = Drop(c => c.Add(1, 29.5f, 5.5f));
            Assert.True(plain.Watched > 1);
            foreach (float y in new[] { 0.5f, 3.5f, 6.5f })
            {
                var moved = Drop(c => c.Add(1, 29.5f, 5.5f), between: (c, step) =>
                {
                    if (step == plain.Watched) c.Position[1] = (29.5f, y);
                });
                Assert.Equal(moved.Transpiled, moved.Watched);
                Assert.True(moved.Watched == 0 || moved.Watched > plain.Watched, $"lifted to {y}: landed after step {moved.Watched}, unlifted {plain.Watched}");
            }
        }

        /// A watched item taken out of reach is watched no longer after the next sweep.
        [Fact]
        public void AnItemMovedOutOfReachIsDroppedAtTheNextSweep()
        {
            var colony = NewColony();
            colony.Add(1, 5.5f, 10.5f);
            colony.StepWatched(Dt);
            Assert.Equal(1, colony.Candidates.Count);
            colony.Position[1] = (25.5f, 25.5f); // in no world: it falls freely from there
            for (int i = 0; i < 16; i++) colony.StepWatched(Dt);
            Assert.Equal(0, colony.Candidates.Count);
            Assert.True(colony.Has(1));
        }

        /// A landed, destroyed or removed object is watched no longer at the next step.
        [Fact]
        public void ObjectsThatStoppedFallingAreDropped()
        {
            var colony = NewColony();
            colony.Add(1, 5.5f, 3.5f);
            colony.Add(2, 6.5f, 3.5f);
            colony.Add(3, 7.5f, 3.5f);
            colony.StepWatched(Dt);
            Assert.Equal(3, colony.Candidates.Count);
            colony.Destroy(1);
            colony.Remove(2);
            colony.CleanUp();
            colony.StepWatched(Dt);
            Assert.Equal(1, colony.Candidates.Count);
            Assert.Equal(3, colony.Candidates[0].Key);
        }

        /// Items far above the bottom rows are read at the sweeps, every 15 steps at the game's
        /// top speed and step time, and once more after their first step; the watched ones'
        /// positions are read before and after every step.
        [Fact]
        public void ItemsOutOfReachAreReadOnlyAtTheSweeps()
        {
            var colony = new Colony(40, 3000);
            colony.World(0, 0, 0, 39, 2999);
            for (int i = 0; i < 100; i++) colony.Add(i, 20.5f, 2900f);
            colony.StepWatched(Dt);
            Assert.Equal(1, colony.Sweeps);
            Assert.Equal(0, colony.Candidates.Count);
            Assert.Equal(100, colony.PositionReads);
            for (int i = 0; i < 29; i++) colony.StepWatched(Dt);
            Assert.Equal(2, colony.Sweeps);
            Assert.Equal(0, colony.Candidates.Count);
            Assert.Equal(300, colony.PositionReads);
        }

        [Fact]
        public void ASweepComesBeforeAnItemOutOfReachCouldHang()
        {
            var candidates = new FallerCandidates<int>();
            Assert.True(candidates.SweepDue(28f, Dt));
            candidates.BeginSweep();
            candidates.Step(28f, Dt);
            float fallen = 28f * Dt;
            int steps = 1;
            while (!candidates.SweepDue(28f, Dt))
            {
                // This step runs with no sweep before it: an object left out of the last one (at
                // Reach or above then) is still above the rows it could hang at (below y = 1).
                Assert.True(OffWorldFallersCore.Reach - fallen >= 1f);
                candidates.Step(28f, Dt);
                fallen += 28f * Dt;
                steps++;
            }
            Assert.Equal(15, steps);
            // A top speed of any size only brings the sweeps closer.
            candidates.BeginSweep();
            candidates.Step(1000f, Dt);
            Assert.True(candidates.SweepDue(1000f, Dt));
            candidates.BeginSweep();
            Assert.True(candidates.SweepDue(float.NaN, Dt));
        }

        [Fact]
        public void AnObjectIsWatchedOnce()
        {
            var candidates = new FallerCandidates<int>();
            Assert.True(candidates.Add(7, 1f, 2f, 0f));
            Assert.False(candidates.Add(7, 1f, 2f, Dt));
            Assert.Equal(1, candidates.Count);
            candidates.Drop(0);
            Assert.True(candidates.Add(7, 1f, 2f, 0f));
        }

        [Fact]
        public void NearIsTheBottomRowsAndTheLeftEdge()
        {
            Assert.True(OffWorldFallersCore.Near(100f, 15.9f, 0.25f));
            Assert.True(OffWorldFallersCore.Near(100f, 16.1f, 0.25f));  // its foot is within reach
            Assert.False(OffWorldFallersCore.Near(100f, 16.3f, 0.25f));
            Assert.True(OffWorldFallersCore.Near(15f, 200f, 0.25f));
            Assert.True(OffWorldFallersCore.Near(-3f, 200f, 0.25f));
            Assert.True(OffWorldFallersCore.Near(float.NaN, 200f, 0.25f));
        }

        /// An item that starts falling during a step (from another object's landing), in place to
        /// hang at once, is stepped in that step: the transpiler landed it after it, the passes
        /// land it after the next.
        [Fact]
        public void AnItemAddedDuringAStepLandsAStepLater()
        {
            (int, int) Run(float y)
            {
                var result = Drop(c =>
                {
                    c.Add(1, 5.5f, 5.5f, item: false);
                    bool added = false;
                    c.DuringStep = i =>
                    {
                        if (added || c.Data[0].Elapsed < 0.3f) return;
                        added = true;
                        c.Add(2, 29.5f, y, vy: -20f);
                    };
                });
                return (result.Transpiled, result.Watched);
            }
            var (transpiled, watched) = Run(-0.9f);
            Assert.True(transpiled > 0);
            Assert.Equal(transpiled + 1, watched);
        }

        /// An item another system puts where it hangs between two sweeps lands at the next sweep's
        /// step: within 16 steps of the transpiler.
        [Fact]
        public void AnItemMovedToWhereItHangsLandsByTheNextSweep()
        {
            var (transpiled, watched, _) = Drop(c => c.Add(1, 5.5f, 25.5f), between: (c, step) =>
            {
                if (step == 3) c.Position[1] = (29.5f, -0.9f);
            });
            Assert.True(transpiled > 0);
            Assert.InRange(watched - transpiled, 0, 16);
        }

        /// Many objects of every kind, dropped at random places and times over the worlds, the
        /// holes and the cells of no world, some destroyed or removed in flight: both forms land
        /// the same objects after the same steps in the same order, and leave the same list.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void RandomColoniesLandTheSameObjectsInTheSameOrder(int seed)
        {
            var random = new Random(seed);
            var a = NewColony();
            var b = NewColony();
            int next = 0, landed = 0;
            float[] columns = { 10.5f, 11.2f, 29.5f, 30.4f, 29.01f, 5.5f, 20.5f, 24.5f, 36.5f, 27.5f };
            for (int step = 1; step <= 3000; step++)
            {
                int adds = random.Next(4) == 0 ? random.Next(1, 6) : 0;
                if (step == 1) adds = 300; // a save's objects, falling again after the load
                for (int i = 0; i < adds; i++)
                {
                    int id = next++;
                    float x = columns[random.Next(columns.Length)], y = step == 1 ? -1f + (float)random.NextDouble() * 1.2f : (float)random.NextDouble() * 28f;
                    if (step == 1) x = random.Next(2) == 0 ? 29.5f : 10.5f;
                    float vx = random.Next(5) == 0 ? (float)random.NextDouble() - 0.5f : 0f, vy = random.Next(3) == 0 ? (float)random.NextDouble() * 6f - 3f : 0f;
                    float groundOffset = new[] { 0f, 0.25f, 0.4f, 0.95f }[random.Next(4)];
                    bool item = random.Next(8) != 0, mayLeave = random.Next(12) == 0;
                    a.Add(id, x, y, vx, vy, groundOffset, item, mayLeave);
                    b.Add(id, x, y, vx, vy, groundOffset, item, mayLeave);
                }
                if (next > 0 && random.Next(10) == 0)
                {
                    int id = random.Next(next);
                    if (random.Next(2) == 0) { a.Destroy(id); b.Destroy(id); }
                    else if (a.Has(id)) { a.Remove(id); b.Remove(id); }
                }
                a.StepTranspiled(Dt);
                b.StepWatched(Dt);
                Assert.Equal(a.Landed, b.Landed);
                Assert.Equal(a.State(), b.State());
                landed += b.Landed.Count;
                if (random.Next(3) == 0)
                {
                    a.CleanUp();
                    b.CleanUp();
                    Assert.Equal(a.State(), b.State());
                }
            }
            Assert.True(landed > 200, $"{landed} landed");
        }
    }
}
