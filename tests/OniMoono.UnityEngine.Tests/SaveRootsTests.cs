using System.Collections.Generic;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The fast-save-roots game tweak's search (src/OniMoono.GameTweaks/SaveRootsCore.cs): for a
    /// live item it finds List.IndexOf's index, whose Equals is UnityEngine.Object's (two live
    /// objects are equal only when the same; two destroyed ones are equal), without that Equals.
    public class SaveRootsTests
    {
        /// Stands in for a UnityEngine.Object: Equals as Object.Equals (SameOrBothNull).
        sealed class Root
        {
            public bool Destroyed;
            public override bool Equals(object other) => other is Root o && (!Destroyed && !o.Destroyed ? ReferenceEquals(this, o) : Destroyed && o.Destroyed);
            public override int GetHashCode() => 0;
        }

        static List<Root> Roots(int n)
        {
            var list = new List<Root>();
            for (int i = 0; i < n; i++) list.Add(new Root());
            return list;
        }

        [Fact]
        public void FindsListIndexOfsIndexForALiveRoot()
        {
            var list = Roots(50);
            list[3].Destroyed = true;
            list[10].Destroyed = true;
            foreach (var root in list)
                if (!root.Destroyed) Assert.Equal(list.IndexOf(root), SaveRootsCore.IndexOfSame(list, root));
        }

        [Fact]
        public void FindsTheFirstOfTwo()
        {
            var list = Roots(5);
            var root = list[1];
            list.Add(root);
            Assert.Equal(1, SaveRootsCore.IndexOfSame(list, root));
            Assert.Equal(list.IndexOf(root), SaveRootsCore.IndexOfSame(list, root));
        }

        [Fact]
        public void FindsNothingWhereListIndexOfFindsNothing()
        {
            var list = Roots(5);
            var other = new Root();
            Assert.Equal(-1, list.IndexOf(other));
            Assert.Equal(-1, SaveRootsCore.IndexOfSame(list, other));
            Assert.Equal(-1, SaveRootsCore.IndexOfSame(new List<Root>(), other));
        }

        /// Why the tweak leaves a destroyed root to the game: List.IndexOf's Equals then matches the
        /// first destroyed root, which a reference test does not.
        [Fact]
        public void ADestroyedRootIsWhereTheTwoDiffer()
        {
            var list = Roots(5);
            list[1].Destroyed = true;
            list[3].Destroyed = true;
            Assert.Equal(1, list.IndexOf(list[3]));
            Assert.Equal(3, SaveRootsCore.IndexOfSame(list, list[3]));
        }

        [Fact]
        public void RemoveAtTheIndexLeavesTheListAsListRemoveDoes()
        {
            var a = Roots(20);
            var b = new List<Root>(a);
            var root = a[7];
            a.Add(root);
            b.Add(root);
            a.Remove(root);
            b.RemoveAt(SaveRootsCore.IndexOfSame(b, root));
            Assert.Equal(a, b);
        }
    }
}
