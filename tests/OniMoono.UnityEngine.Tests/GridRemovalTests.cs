using System;
using System.Collections.Generic;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The fast-overlay-removals game tweak's removal (src/OniMoono.GameTweaks/GridRemovalCore.cs):
    /// for a live item it leaves a UniformGrid's cell lists and its list of all entries as the
    /// game's UniformGrid.Remove does (List.IndexOf and List.Remove, whose Equals is
    /// UnityEngine.Object's), without that Equals.
    public class GridRemovalTests
    {
        /// Stands in for a UnityEngine.Object: Equals as Object.Equals (SameOrBothNull).
        sealed class Item
        {
            public bool Destroyed;
            public override bool Equals(object other) => other is Item o && (!Destroyed && !o.Destroyed ? ReferenceEquals(this, o) : Destroyed && o.Destroyed);
            public override int GetHashCode() => 0;
        }

        sealed class Grid
        {
            public const int Width = 6, Height = 5;
            public List<Item>[] Cells = new List<Item>[Width * Height];
            public List<Item> Items = new List<Item>();

            /// UniformGrid.Add over a cell range.
            public void Add(Item item, int x0, int y0, int x1, int y1)
            {
                for (int i = y0; i <= y1; i++)
                    for (int j = x0; j <= x1; j++)
                    {
                        (Cells[i * Width + j] ??= new List<Item>()).Add(item);
                        Items.Add(item);
                    }
            }

            /// UniformGrid.Remove over a cell range, as the game writes it.
            public void GameRemove(Item item, int x0, int y0, int x1, int y1)
            {
                for (int i = y0; i <= y1; i++)
                    for (int j = x0; j <= x1; j++)
                    {
                        var list = Cells[i * Width + j];
                        if (list != null)
                        {
                            int at = list.IndexOf(item);
                            if (at != -1)
                            {
                                list.Remove(item);
                                Items.Remove(item);
                            }
                        }
                    }
            }

            public Grid Copy()
            {
                var copy = new Grid { Items = new List<Item>(Items) };
                for (int i = 0; i < Cells.Length; i++)
                    if (Cells[i] != null) copy.Cells[i] = new List<Item>(Cells[i]);
                return copy;
            }
        }

        static void AssertSame(Grid expected, Grid actual)
        {
            AssertSame(expected.Items, actual.Items);
            for (int i = 0; i < expected.Cells.Length; i++)
            {
                Assert.Equal(expected.Cells[i] == null, actual.Cells[i] == null);
                if (expected.Cells[i] != null) AssertSame(expected.Cells[i], actual.Cells[i]);
            }
        }

        static void AssertSame(List<Item> expected, List<Item> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++) Assert.Same(expected[i], actual[i]);
        }

        [Fact]
        public void RemovesWhatTheGameRemoves()
        {
            var grid = new Grid();
            var items = new List<Item>();
            for (int n = 0; n < 40; n++)
            {
                var item = new Item();
                items.Add(item);
                grid.Add(item, n % 5, n % 4, n % 5 + 1, n % 4 + 1);
            }
            for (int n = 0; n < 40; n += 3)
            {
                var theirs = grid.Copy();
                theirs.GameRemove(items[n], n % 5, n % 4, n % 5 + 1, n % 4 + 1);
                int removed = GridRemovalCore.Remove(grid.Cells, grid.Items, Grid.Width, n % 5, n % 4, n % 5 + 1, n % 4 + 1, items[n]);
                Assert.Equal(4, removed);
                AssertSame(theirs, grid);
            }
        }

        /// An item that moved since it was added is removed only from the cells it covers now, and
        /// one added twice loses its earliest entries first: both as in the game.
        [Fact]
        public void MovedAndTwiceAddedItems()
        {
            var random = new Random(7);
            var grid = new Grid();
            var items = new List<Item>();
            for (int n = 0; n < 30; n++) items.Add(new Item());
            for (int step = 0; step < 600; step++)
            {
                var item = items[random.Next(items.Count)];
                int x = random.Next(Grid.Width - 1), y = random.Next(Grid.Height - 1), w = random.Next(2), h = random.Next(2);
                if (random.Next(3) > 0) { grid.Add(item, x, y, x + w, y + h); continue; }
                var theirs = grid.Copy();
                theirs.GameRemove(item, x, y, x + w, y + h);
                GridRemovalCore.Remove(grid.Cells, grid.Items, Grid.Width, x, y, x + w, y + h, item);
                AssertSame(theirs, grid);
            }
        }

        /// Destroyed entries left in the lists (the game never takes them out) do not change which
        /// entries a live item's removal finds.
        [Fact]
        public void DestroyedEntriesAreNotMatched()
        {
            var grid = new Grid();
            var dead1 = new Item();
            var dead2 = new Item();
            var live = new Item();
            grid.Add(dead1, 0, 0, 1, 1);
            grid.Add(live, 0, 0, 1, 1);
            grid.Add(dead2, 0, 0, 1, 1);
            dead1.Destroyed = dead2.Destroyed = true;
            var theirs = grid.Copy();
            theirs.GameRemove(live, 0, 0, 1, 1);
            GridRemovalCore.Remove(grid.Cells, grid.Items, Grid.Width, 0, 0, 1, 1, live);
            AssertSame(theirs, grid);
            Assert.Equal(8, grid.Items.Count);
        }

        [Fact]
        public void AnItemThatIsNotThereRemovesNothing()
        {
            var grid = new Grid();
            grid.Add(new Item(), 0, 0, 1, 1);
            Assert.Equal(0, GridRemovalCore.Remove(grid.Cells, grid.Items, Grid.Width, 0, 0, 2, 2, new Item()));
            Assert.Equal(4, grid.Items.Count);
        }
    }
}
