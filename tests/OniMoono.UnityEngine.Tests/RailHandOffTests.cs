using System.Collections.Generic;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The direct-rail-bridges game tweak's hand-off (src/OniMoono.GameTweaks/RailHandOffCore.cs)
    /// on a fake rail that records what is asked of it: a whole item moves under its handle
    /// with no game call, and everything else is the game's removal and add, with a started
    /// removal finished the game's way first.
    public class RailHandOffTests
    {
        sealed class Item
        {
            public bool Eligible = true;
        }

        sealed class Rail
        {
            public readonly HashSet<int> Conduits = new();
            public readonly Dictionary<int, int> Contents = new();
            public readonly Dictionary<int, Item> Items = new();
            public readonly List<string> Log = new();
            int nextHandle = 100;

            public void Put(int cell, int handle, Item item)
            {
                Conduits.Add(cell);
                Contents[cell] = handle;
                Items[handle] = item;
            }

            public int NewHandle(Item item)
            {
                Items[nextHandle] = item;
                return nextHandle++;
            }
        }

        readonly struct Ops : IRailOps<Item>
        {
            readonly Rail rail;

            public Ops(Rail rail) => this.rail = rail;

            public bool HasConduit(int cell) => rail.Conduits.Contains(cell);
            public int HandleAt(int cell) => rail.Contents.TryGetValue(cell, out int handle) ? handle : -1;
            public Item ItemOf(int handle) => rail.Items[handle];
            public bool Eligible(Item item, int handle, int cell) => item.Eligible;

            public void Empty(int cell, int handle)
            {
                rail.Contents.Remove(cell);
                rail.Log.Add($"empty {cell}");
            }

            public void Unstore(Item item, int handle)
            {
                rail.Items.Remove(handle);
                rail.Log.Add($"unstore {handle}");
            }

            public void Place(int cell, Item item, int handle)
            {
                rail.Contents[cell] = handle;
                rail.Log.Add($"place {cell} {handle}");
            }

            // The game's removal: nothing without a rail or an item; a destroyed item's handle is
            // freed and null returned.
            public Item GameRemove(int cell)
            {
                rail.Log.Add($"game-remove {cell}");
                if (!rail.Conduits.Contains(cell) || !rail.Contents.Remove(cell, out int handle)) return null;
                var item = rail.Items[handle];
                rail.Items.Remove(handle);
                return item;
            }

            // The game's add: an item for a cell without a rail, or with an item, is dropped.
            public void GameAdd(int cell, Item item)
            {
                rail.Log.Add($"game-add {cell}");
                if (!rail.Conduits.Contains(cell) || rail.Contents.ContainsKey(cell)) return;
                rail.Contents[cell] = rail.NewHandle(item);
            }
        }

        static (Rail Rail, Item Item) Bridge(bool output = true)
        {
            var rail = new Rail();
            var item = new Item();
            rail.Put(10, 7, item);
            if (output) rail.Conduits.Add(12);
            return (rail, item);
        }

        [Fact]
        public void AWholeItemMovesUnderItsHandle()
        {
            var (rail, item) = Bridge();
            var ops = new Ops(rail);
            var move = new RailHandOff<Item>();
            Assert.Same(item, move.Remove(ref ops, 10));
            Assert.True(move.Pending);
            move.Add(ref ops, 12, item);
            Assert.False(move.Pending);
            Assert.Equal(new[] { "empty 10", "place 12 7" }, rail.Log);
            Assert.False(rail.Contents.ContainsKey(10));
            Assert.Equal(7, rail.Contents[12]);
            Assert.Same(item, rail.Items[7]);
            Assert.Equal(1, move.Direct);
            Assert.Equal(0, move.Undone);
        }

        [Fact]
        public void AFullOutputIsTheGames()
        {
            var (rail, item) = Bridge();
            var other = new Item();
            var ops = new Ops(rail);
            var move = new RailHandOff<Item>();
            Assert.Same(item, move.Remove(ref ops, 10));
            rail.Put(12, 8, other);
            move.Add(ref ops, 12, item);
            // Unstored as the game's removal would have, then the game's add, which drops it.
            Assert.Equal(new[] { "empty 10", "unstore 7", "game-add 12" }, rail.Log);
            Assert.Equal(8, rail.Contents[12]);
            Assert.False(rail.Items.ContainsKey(7));
            Assert.False(move.Pending);
            Assert.Equal(0, move.Direct);
            Assert.Equal(1, move.Undone);
        }

        [Fact]
        public void AMissingOutputConduitIsTheGames()
        {
            var (rail, item) = Bridge(output: false);
            var ops = new Ops(rail);
            var move = new RailHandOff<Item>();
            Assert.Same(item, move.Remove(ref ops, 10));
            move.Add(ref ops, 12, item);
            Assert.Equal(new[] { "empty 10", "unstore 7", "game-add 12" }, rail.Log);
            Assert.False(rail.Contents.ContainsKey(12));
            Assert.Equal(0, move.Direct);
        }

        [Fact]
        public void AMissingInputConduitOrAnEmptyCellIsTheGames()
        {
            var rail = new Rail();
            var ops = new Ops(rail);
            var move = new RailHandOff<Item>();
            Assert.Null(move.Remove(ref ops, 10));
            rail.Conduits.Add(10);
            Assert.Null(move.Remove(ref ops, 10));
            Assert.Equal(new[] { "game-remove 10", "game-remove 10" }, rail.Log);
            Assert.False(move.Pending);
        }

        [Fact]
        public void ADestroyedItemIsTheGames()
        {
            var rail = new Rail();
            rail.Put(10, 7, null);
            rail.Conduits.Add(12);
            var ops = new Ops(rail);
            var move = new RailHandOff<Item>();
            Assert.Null(move.Remove(ref ops, 10));
            Assert.Equal(new[] { "game-remove 10" }, rail.Log);
            Assert.False(move.Pending);
            Assert.False(rail.Contents.ContainsKey(10));
        }

        [Fact]
        public void AnItemThatIsNotEligibleIsTheGames()
        {
            var (rail, item) = Bridge();
            item.Eligible = false;
            var ops = new Ops(rail);
            var move = new RailHandOff<Item>();
            Assert.Same(item, move.Remove(ref ops, 10));
            Assert.False(move.Pending);
            move.Add(ref ops, 12, item);
            Assert.Equal(new[] { "game-remove 10", "game-add 12" }, rail.Log);
            // The game's add gives it a new handle.
            Assert.Equal(100, rail.Contents[12]);
        }

        [Fact]
        public void ThePartialBranchsAddIsTheGames()
        {
            // The bridge's partial move adds a split-off item with no removal before it; the
            // whole item stays in the input cell.
            var (rail, item) = Bridge();
            var part = new Item();
            var ops = new Ops(rail);
            var move = new RailHandOff<Item>();
            move.Add(ref ops, 12, part);
            Assert.Equal(new[] { "game-add 12" }, rail.Log);
            Assert.Equal(7, rail.Contents[10]);
            Assert.Same(item, rail.Items[7]);
            Assert.Same(part, rail.Items[rail.Contents[12]]);
            Assert.Equal(0, move.Direct);
        }

        [Fact]
        public void AnAddOfAnotherItemFinishesTheRemovalTheGamesWay()
        {
            var (rail, item) = Bridge();
            var other = new Item();
            var ops = new Ops(rail);
            var move = new RailHandOff<Item>();
            Assert.Same(item, move.Remove(ref ops, 10));
            move.Add(ref ops, 12, other);
            Assert.Equal(new[] { "empty 10", "unstore 7", "game-add 12" }, rail.Log);
            Assert.Same(other, rail.Items[rail.Contents[12]]);
            Assert.False(move.Pending);
        }

        [Fact]
        public void ARemovalNothingFollowedIsFinishedBeforeTheNext()
        {
            var (rail, item) = Bridge();
            var second = new Item();
            rail.Put(20, 9, second);
            rail.Conduits.Add(22);
            var ops = new Ops(rail);
            var move = new RailHandOff<Item>();
            Assert.Same(item, move.Remove(ref ops, 10));
            Assert.Same(second, move.Remove(ref ops, 20));
            move.Add(ref ops, 22, second);
            Assert.Equal(new[] { "empty 10", "unstore 7", "empty 20", "place 22 9" }, rail.Log);
            Assert.Equal(1, move.Direct);
            Assert.Equal(1, move.Undone);
        }

        [Fact]
        public void ChainedBridgesMoveAnItemTwiceInOneStep()
        {
            var (rail, item) = Bridge();
            rail.Conduits.Add(14);
            var ops = new Ops(rail);
            var move = new RailHandOff<Item>();
            move.Add(ref ops, 12, move.Remove(ref ops, 10));
            move.Add(ref ops, 14, move.Remove(ref ops, 12));
            Assert.Equal(new[] { "empty 10", "place 12 7", "empty 12", "place 14 7" }, rail.Log);
            Assert.Equal(7, rail.Contents[14]);
            Assert.Equal(2, move.Direct);
        }
    }
}
