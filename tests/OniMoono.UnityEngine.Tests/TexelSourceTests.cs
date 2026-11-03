using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using Xunit;

namespace OniMoono.Tests
{
    /// A texture's texels still to be read from an AssetBundle stay readable after the bundle is
    /// unmounted (AssetBundle.Unload(false)): the source holds the bundle's resource node, as an
    /// upload queued for the next frame or handed to background readers reads it later.
    public class TexelSourceTests
    {
        [Fact]
        public unsafe void UnreadTexelsOutliveTheirBundle()
        {
            const string key = "archive:/CAB-texelsourcetest/CAB-texelsourcetest.resS";
            var bytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            AssetSystem.Mount(new List<(string, System.ReadOnlyMemory<byte>)> { (key, bytes) });
            var source = new TexelSource(null, key, 16, 8, AssetSystem.MountedNode(key));
            AssetSystem.UnmountBundle([key]);
            Assert.Throws<DirectoryNotFoundException>(() => AssetSystem.ReadStream(key, 16, 8));
            Assert.Equal(bytes[16..24], source.Read());
            var memory = source.ReadMemory();
            Assert.Equal(bytes[16..24], memory.Span.ToArray());
            memory.Free();
        }

        /// A node in place (a window on its bundle's decompressed data, as AssetSystem.ReadNodes
        /// mounts it) reads from the window's start: texels and streamed reads see the node's
        /// bytes, not the bytes before it in the array.
        [Fact]
        public void NodesInPlaceReadFromTheirWindow()
        {
            const string key = "archive:/CAB-texelwindowtest/CAB-texelwindowtest.resS";
            var data = Enumerable.Range(0, 96).Select(i => (byte)i).ToArray();
            var node = new System.ReadOnlyMemory<byte>(data, 32, 48);
            AssetSystem.Mount(new List<(string, System.ReadOnlyMemory<byte>)> { (key, node) });
            try
            {
                Assert.Equal(data[48..56], AssetSystem.ReadStream(key, 16, 8));
                var source = new TexelSource(null, key, 16, 8, AssetSystem.MountedNode(key));
                Assert.Equal(data[48..56], source.Read());
                var memory = source.ReadMemory();
                Assert.Equal(data[48..56], memory.Span.ToArray());
                memory.Free();
                Assert.Throws<System.ArgumentOutOfRangeException>(() => AssetSystem.ReadStream(key, 44, 8));
            }
            finally
            {
                AssetSystem.UnmountBundle([key]);
            }
        }

        /// A node mapped from a cache file (BundleCache) reads as an array's: texels, streamed
        /// reads, and the bounds of its window.
        [Fact]
        public void MappedNodesReadAsArrays()
        {
            const string key = "archive:/CAB-texelmappedtest/CAB-texelmappedtest.resS";
            var data = Enumerable.Range(0, 96).Select(i => (byte)i).ToArray();
            var file = Path.Combine(Directory.CreateTempSubdirectory("moono-texel-").FullName, "node.bin");
            File.WriteAllBytes(file, data);
            var node = new OniMoono.Assets.IO.MappedMemory(file, 32, 48).Memory;
            AssetSystem.Mount(new List<(string, System.ReadOnlyMemory<byte>)> { (key, node) });
            try
            {
                Assert.Equal(data[48..56], AssetSystem.ReadStream(key, 16, 8));
                var source = new TexelSource(null, key, 16, 8, AssetSystem.MountedNode(key));
                AssetSystem.UnmountBundle([key]);
                Assert.Equal(data[48..56], source.Read());
                var memory = source.ReadMemory();
                Assert.Equal(data[48..56], memory.Span.ToArray());
                memory.Free();
            }
            finally
            {
                AssetSystem.UnmountBundle([key]);
            }
        }
    }
}
