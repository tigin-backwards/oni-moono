using System;
using System.Collections.Generic;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The fast-conduit-sorts game tweak's replay (src/OniMoono.GameTweaks/SortReplayCore.cs): for
    /// lists of updaters with few distinct priorities (most equal, as the game's are), sorted again
    /// and again, the replay gives List.Sort's order every time, item by item.
    public class SortReplayTests
    {
        sealed class Updater
        {
            public int Priority;
            public int Id;
        }

        static readonly Comparison<Updater> byPriority = (a, b) => a.Priority - b.Priority;

        static List<Updater> Updaters(Random random, int n, int priorities)
        {
            var list = new List<Updater>();
            for (int i = 0; i < n; i++) list.Add(new Updater { Priority = random.Next(priorities) - 1, Id = i });
            return list;
        }

        static int[] Keys(List<Updater> list)
        {
            var keys = new int[list.Count];
            for (int i = 0; i < list.Count; i++) keys[i] = list[i].Priority;
            return keys;
        }

        static void AssertSame(List<Updater> expected, List<Updater> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++) Assert.Same(expected[i], actual[i]);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(16, 3)]
        [InlineData(17, 3)]
        [InlineData(300, 1)]
        [InlineData(2857, 4)]
        [InlineData(5000, 2)]
        public void RepeatedSortsMatchListSort(int n, int priorities)
        {
            var random = new Random(n * 31 + priorities);
            var game = Updaters(random, n, priorities);
            var ours = new List<Updater>(game);
            var replay = new SortReplay<Updater>();
            for (int round = 0; round < 6; round++)
            {
                game.Sort(byPriority);
                replay.Sort(ours, Keys(ours), byPriority);
                AssertSame(game, ours);
            }
            // The first sort sees the list's own order and the second the sorted one: from then on
            // the priorities are the same each time, and the permutation is replayed.
            if (n >= 2) { Assert.True(replay.Built <= 2); Assert.Equal(6, replay.Built + replay.Replayed); }
        }

        /// The game's sort of a sorted list with equal priorities moves updaters: why the sort
        /// can't simply be left out.
        [Fact]
        public void TheGamesSortReordersEqualPriorities()
        {
            var list = Updaters(new Random(7), 2857, 3);
            list.Sort(byPriority);
            var before = new List<Updater>(list);
            list.Sort(byPriority);
            bool moved = false;
            for (int i = 0; i < list.Count; i++) moved |= !ReferenceEquals(before[i], list[i]);
            Assert.True(moved);
        }

        [Fact]
        public void AddedAndRemovedUpdatersSortAsTheGames()
        {
            var random = new Random(11);
            var game = Updaters(random, 1000, 3);
            var ours = new List<Updater>(game);
            var replay = new SortReplay<Updater>();
            for (int round = 0; round < 40; round++)
            {
                if (round % 4 == 1)
                {
                    var u = new Updater { Priority = random.Next(3) - 1, Id = 10000 + round };
                    game.Add(u);
                    ours.Add(u);
                }
                else if (round % 4 == 2)
                {
                    int at = random.Next(game.Count);
                    var u = game[at];
                    game.RemoveAt(at);
                    ours.Remove(u);
                }
                else if (round % 4 == 3)
                {
                    // Same length and priorities, another updater: still the same permutation.
                    int at = random.Next(game.Count);
                    var u = new Updater { Priority = game[at].Priority, Id = 20000 + round };
                    int theirs = ours.IndexOf(game[at]);
                    game[at] = u;
                    ours[theirs] = u;
                }
                game.Sort(byPriority);
                replay.Sort(ours, Keys(ours), byPriority);
                AssertSame(game, ours);
            }
        }
    }
}
