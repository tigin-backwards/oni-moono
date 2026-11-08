using System.Collections.Generic;
using System.Linq;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// fetch-by-cell's choices within a pile (src/OniMoono.GameTweaks/FetchByCellCore.cs,
    /// compiled in here): the item a pile offers a Duplicant, as the game's pass over every item
    /// would take it, and when a cell's items share where their work spots come from.
    public class FetchByCellCoreTests
    {
        sealed class Item
        {
            public string Name;
            public int Quality, Freshness;
            public bool Pickable = true;
            public override string ToString() => Name;
        }

        struct Rules : IPileRules<Item>
        {
            public bool CanPick(Item item) => item.Pickable;
            public bool Better(Item a, Item b) => a.Quality > b.Quality || (a.Quality == b.Quality && a.Freshness > b.Freshness);
        }

        static Item I(string name, int quality, int freshness, bool pickable = true) => new Item { Name = name, Quality = quality, Freshness = freshness, Pickable = pickable };

        /// The game's choice: the best of the items that may be picked up, the first of equals.
        static Item Game(IEnumerable<Item> items)
        {
            Item chosen = null;
            foreach (var i in items)
                if (i.Pickable && (chosen == null || new Rules().Better(i, chosen))) chosen = i;
            return chosen;
        }

        static Item Choose(List<Item> members, List<Item> specials, out long bestFailed)
        {
            long failed = 0, checks = 0;
            var best = PileChoice.Best(members, new Rules());
            var chosen = PileChoice.Choose(best, members, specials, new Rules(), ref failed, ref checks);
            bestFailed = failed;
            return chosen;
        }

        [Fact]
        public void BestIsTheFirstOfEquals()
        {
            var members = new List<Item> { I("a", 5, 0), I("b", 6, 1), I("c", 6, 3), I("d", 6, 3) };
            Assert.Equal("c", PileChoice.Best(members, new Rules()).Name);
            Assert.Null(PileChoice.Best(new List<Item>(), new Rules()));
        }

        [Fact]
        public void ABestThatPassesIsTaken()
        {
            var members = new List<Item> { I("a", 5, 0), I("b", 6, 1) };
            Assert.Equal("b", Choose(members, new List<Item>(), out long failed).Name);
            Assert.Equal(0, failed);
        }

        /// The path no colony took in testing: the kept best may not be picked up (an item with no
        /// mass left, say), so every member is checked, as the game checks each item.
        [Fact]
        public void ABestThatFailsChecksTheWholePile()
        {
            var members = new List<Item> { I("a", 5, 0), I("best", 7, 9, pickable: false), I("b", 6, 1), I("c", 6, 4), I("d", 5, 8) };
            var chosen = Choose(members, new List<Item>(), out long failed);
            Assert.Equal("c", chosen.Name);
            Assert.Equal(Game(members), chosen);
            Assert.Equal(1, failed);
        }

        [Fact]
        public void APileWhoseItemsAllFailOffersNone()
        {
            var members = new List<Item> { I("a", 5, 0, pickable: false), I("b", 6, 1, pickable: false) };
            Assert.Null(Choose(members, new List<Item>(), out long failed));
            Assert.Equal(1, failed);
        }

        [Fact]
        public void SpecialItemsAreCheckedOneByOne()
        {
            var members = new List<Item> { I("a", 5, 0), I("b", 6, 1) };
            // Reserved by this Duplicant (pickable for it) and better; reserved by another (not).
            var specials = new List<Item> { I("mine", 6, 5), I("theirs", 9, 9, pickable: false) };
            Assert.Equal("mine", Choose(members, specials, out _).Name);
            // A special item no better than the best member leaves the member chosen.
            Assert.Equal("b", Choose(members, new List<Item> { I("worse", 6, 1) }, out _).Name);
            // Only special items: the best of those that pass.
            Assert.Equal("mine", Choose(new List<Item>(), specials, out _).Name);
        }

        /// Every pile against the game's choice over the same items, with failing items anywhere.
        [Fact]
        public void TheChoiceIsTheGamesUpToTies()
        {
            var rng = new System.Random(7);
            for (int round = 0; round < 2000; round++)
            {
                var members = new List<Item>();
                var specials = new List<Item>();
                int n = rng.Next(0, 6), m = rng.Next(0, 3);
                for (int i = 0; i < n; i++) members.Add(I("m" + i, rng.Next(3, 6), rng.Next(0, 3), rng.Next(4) != 0));
                for (int i = 0; i < m; i++) specials.Add(I("s" + i, rng.Next(3, 6), rng.Next(0, 3), rng.Next(2) == 0));
                var chosen = Choose(members, specials, out _);
                var game = Game(members.Concat(specials));
                if (game == null) Assert.Null(chosen);
                else
                {
                    Assert.NotNull(chosen);
                    Assert.Equal((game.Quality, game.Freshness), (chosen.Quality, chosen.Freshness));
                    Assert.True(chosen.Pickable);
                }
            }
        }

        [Fact]
        public void CellSourcesTellWhenEveryItemShares()
        {
            var table = new object();
            var cell = new CellSources();
            Assert.True(cell.Uniform);
            cell.Add(table);
            cell.Add(table);
            Assert.True(cell.Uniform);
            cell.Add("0,1;");
            Assert.False(cell.Uniform);
            cell.Add("0,1;");
            Assert.False(cell.Uniform);
            cell.Remove(table);
            Assert.False(cell.Uniform);
            cell.Remove(table);
            Assert.True(cell.Uniform);
            Assert.Equal(2, cell.Count);
            // Fixed offsets compare by value.
            cell.Add(string.Concat("0,", "1;"));
            Assert.True(cell.Uniform);
            cell.Remove("0,1;");
            cell.Remove("0,1;");
            cell.Remove("0,1;");
            Assert.Equal(0, cell.Count);
        }
    }
}
