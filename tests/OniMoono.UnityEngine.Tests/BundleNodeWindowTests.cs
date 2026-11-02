using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Engine.Assets;
using Xunit;

namespace OniMoono.Tests
{
    /// A mounted bundle node in place (a window on its bundle's decompressed data, as
    /// AssetSystem.ReadNodes mounts it) reads from the window's start: streamed reads see the
    /// node's bytes, not the bytes before it in the array, and end at the window's end.
    public class BundleNodeWindowTests
    {
        [Fact]
        public void StreamedReadsStayInTheirWindow()
        {
            const string key = "archive:/CAB-nodewindowtest/CAB-nodewindowtest.resS";
            var data = Enumerable.Range(0, 96).Select(i => (byte)i).ToArray();
            AssetSystem.Mount(new List<(string, ArraySegment<byte>)> { (key, new ArraySegment<byte>(data, 32, 48)) });
            try
            {
                Assert.Equal(data[48..56], AssetSystem.ReadStream(key, 16, 8));
                Assert.Equal(data[72..80], AssetSystem.ReadStream(key, 40, 8));
                Assert.Throws<ArgumentOutOfRangeException>(() => AssetSystem.ReadStream(key, 44, 8));
            }
            finally
            {
                AssetSystem.UnmountBundle([key]);
            }
        }
    }
}
