using System;
using System.Linq;
using OniMoono.Engine.Rendering;
using Xunit;

namespace OniMoono.Tests
{
    /// SortingLayers.ValueOf: a layer's place in the list relative to Default (id 0), 0 for an
    /// unknown id, read from the layers installed at the time of the call.
    public class SortingLayerValueTests : IDisposable
    {
        readonly (string, int)[] saved = SortingLayers.Ids.Select(id => (SortingLayers.NameOf(id), id)).ToArray();

        public void Dispose() => SortingLayers.Install(saved);

        [Fact]
        public void ValuesCountFromTheDefaultLayer()
        {
            SortingLayers.Install([("Back", 7), ("Default", 0), ("Front", 3), ("Top", -5)]);
            Assert.Equal(-1, SortingLayers.ValueOf(7));
            Assert.Equal(0, SortingLayers.ValueOf(0));
            Assert.Equal(1, SortingLayers.ValueOf(3));
            Assert.Equal(2, SortingLayers.ValueOf(-5));
        }

        [Fact]
        public void AnUnknownIdIsZero()
        {
            SortingLayers.Install([("Back", 7), ("Default", 0), ("Front", 3)]);
            Assert.Equal(0, SortingLayers.ValueOf(12345));
            Assert.False(SortingLayers.Contains(12345));
            Assert.Equal("<unknown layer>", SortingLayers.NameOf(12345));
        }

        /// Without a Default layer the values count from one before the first layer, and id 0 is
        /// unknown (0).
        [Fact]
        public void WithoutADefaultLayerValuesStartAtOne()
        {
            SortingLayers.Install([("A", 4), ("B", 9)]);
            Assert.Equal(1, SortingLayers.ValueOf(4));
            Assert.Equal(2, SortingLayers.ValueOf(9));
            Assert.Equal(0, SortingLayers.ValueOf(0));
        }

        /// A repeated id takes its first place.
        [Fact]
        public void ARepeatedIdTakesItsFirstPlace()
        {
            SortingLayers.Install([("A", 4), ("Default", 0), ("B", 9), ("A again", 4)]);
            Assert.Equal(-1, SortingLayers.ValueOf(4));
            Assert.Equal("A", SortingLayers.NameOf(4));
        }

        [Fact]
        public void LayersInstalledLaterChangeTheValues()
        {
            SortingLayers.Install([("Default", 0), ("Front", 3)]);
            Assert.Equal(1, SortingLayers.ValueOf(3));
            SortingLayers.Install([("Front", 3), ("Middle", 8), ("Default", 0)]);
            Assert.Equal(-2, SortingLayers.ValueOf(3));
            Assert.Equal(-1, SortingLayers.ValueOf(8));
            SortingLayers.Install([("Default", 0)]);
            Assert.Equal(0, SortingLayers.ValueOf(3));
            Assert.Equal(0, SortingLayers.ValueOf(8));
        }

        /// An empty list installs Default alone.
        [Fact]
        public void AnEmptyListLeavesDefaultAlone()
        {
            SortingLayers.Install([("Default", 0), ("Front", 3)]);
            SortingLayers.Install([]);
            Assert.Equal(new[] { 0 }, SortingLayers.Ids.ToArray());
            Assert.Equal(0, SortingLayers.ValueOf(3));
        }

        /// Draw items look layers up every frame: the lookup allocates nothing.
        [Fact]
        public void TheLookupAllocatesNothing()
        {
            SortingLayers.Install([("Back", 7), ("Default", 0), ("Front", 3)]);
            int sum = SortingLayers.ValueOf(3) + SortingLayers.ValueOf(99);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) sum += SortingLayers.ValueOf(i % 2 == 0 ? 3 : 7) + SortingLayers.ValueOf(99);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(1, sum);
        }
    }
}
