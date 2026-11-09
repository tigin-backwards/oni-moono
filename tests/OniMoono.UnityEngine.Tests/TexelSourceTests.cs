using System;
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
        // The data directory set first: a read of an unmounted node falls to the data directory
        // and fixes it for the process (AssetSystem.Initialize), as the other suites read it.
        public TexelSourceTests() => TmpTestSupport.LoadGameData();

        [Fact]
        public unsafe void UnreadTexelsOutliveTheirBundle()
        {
            const string key = "archive:/CAB-texelsourcetest/CAB-texelsourcetest.resS";
            var bytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            AssetSystem.Mount(new List<(string, OniMoono.Assets.Files.NodeData)> { (key, new System.ArraySegment<byte>(bytes)) });
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
            var node = new System.ArraySegment<byte>(data, 32, 48);
            AssetSystem.Mount(new List<(string, OniMoono.Assets.Files.NodeData)> { (key, node) });
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

        /// A bundle read block by block (U60's chunked LZ4, as LoadFromFile opens it): texels read
        /// through a texture's source and streamed reads are the whole decode's bytes, also once
        /// the bundle is unmounted.
        [Fact]
        public void TexelsOfABundleReadByBlocksAreTheWholeDecodes()
        {
            var path = Path.Combine(GameInstall.DataDir, "StreamingAssets", "hires_base_bundle");
            var archive = OniMoono.Assets.Files.UnityFsArchive.OpenFile(path);
            Assert.True(archive.ReadsBlocks);
            var whole = OniMoono.Assets.Files.UnityFsArchive.Open(path);
            var nodes = AssetSystem.ReadNodes(archive);
            var key = nodes.Select(n => n.Key).First(k => k.EndsWith(".resS"));
            var data = whole.Read(whole.Nodes.First(n => n.Path.EndsWith(".resS")));
            AssetSystem.Mount(nodes);
            TexelSource source;
            try
            {
                // Across blocks (128 KiB), within one, and at the node's end.
                foreach (var (offset, size) in new[] { (100_000L, 1_000_000L), (131_072L * 3 + 5, 1000L), (data.Length - 4096L, 4096L) })
                    Assert.Equal(data.AsSpan((int)offset, (int)size).ToArray(), AssetSystem.ReadStream(key, offset, size));
                source = new TexelSource(null, key, 262_000, 300_000, AssetSystem.MountedNode(key));
            }
            finally
            {
                AssetSystem.UnmountBundle(nodes.Select(n => n.Key));
            }
            var expected = data.AsSpan(262_000, 300_000).ToArray();
            Assert.Equal(expected, source.Read());
            var memory = source.ReadMemory();
            Assert.Equal(expected, memory.Span.ToArray());
            memory.Free();
        }
    }
}
