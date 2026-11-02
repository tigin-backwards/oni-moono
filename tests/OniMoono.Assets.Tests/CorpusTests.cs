using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Assets.Files;
using OniMoono.Assets.TypeTrees;
using Xunit;

namespace OniMoono.Assets.Tests
{
    /// Every native object in the game's data parses to exactly its byte size with the release
    /// type trees (MonoBehaviour payloads are checked by the engine, which knows script layouts).
    public class CorpusTests
    {
        const int MonoBehaviourId = 114;

        [Fact]
        public void EveryNativeObjectParsesExactly()
        {
            var failures = new List<string>();
            int parsed = 0;
            foreach (var name in GameData.SerializedFiles)
            {
                var file = SerializedFile.Open(Path.Combine(GameData.DataDir, name));
                if (!name.StartsWith("Resources/")) Assert.Equal(TypeTreeDatabase.UnityVersion, file.UnityVersion); // built-in resources carry Unity's own build stamp
                foreach (var obj in file.Objects)
                {
                    var type = file.TypeOf(obj);
                    if (type.ClassId == MonoBehaviourId) continue;
                    var info = TypeTreeDatabase.Get(type.ClassId);
                    try
                    {
                        TypeTreeReader.ReadNative(type.ClassId, file.OpenObject(obj));
                        parsed++;
                    }
                    catch (System.Exception e)
                    {
                        failures.Add($"{name}:{obj.PathId} {info.Name}: {e.Message}");
                    }
                }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} failures (parsed {parsed}):\n" + string.Join("\n", failures.Take(30)));
            Assert.True(parsed > 10000);
        }

        [Fact]
        public void HiResBundleOpensAndItsEmbeddedTreesMatchItsObjects()
        {
            var archive = UnityFsArchive.Open(Path.Combine(GameData.DataDir, "StreamingAssets", "hires_base_bundle"));
            var cabNode = archive.Nodes.First(n => !n.Path.EndsWith(".resS"));
            var file = SerializedFile.Parse(cabNode.Path, archive.Read(cabNode));
            Assert.True(file.EnableTypeTree);
            foreach (var obj in file.Objects)
                TypeTreeReader.ReadObject(file.TypeOf(obj).Tree!, file.OpenObject(obj));
            // 390 high-resolution textures and their bundle's own objects: 395 in U59, 396 in U60.
            Assert.InRange(file.Objects.Count, 395, 396);
        }

        /// A node read in place (a window on the archive's data, as bundles are mounted) holds the
        /// bytes of its copy, and its serialized file reads every object as the copy's does.
        [Fact]
        public void BundleNodesInPlaceReadAsTheirCopies()
        {
            var archive = UnityFsArchive.Open(Path.Combine(GameData.DataDir, "StreamingAssets", "hires_base_bundle"));
            Assert.True(archive.Nodes.Count >= 2);
            foreach (var node in archive.Nodes)
                Assert.True(archive.Slice(node).AsSpan().SequenceEqual(archive.Read(node)), node.Path);
            var cabNode = archive.Nodes.First(n => !n.Path.EndsWith(".resS"));
            var slice = archive.Slice(cabNode);
            var copy = SerializedFile.Parse(cabNode.Path, archive.Read(cabNode));
            var inPlace = SerializedFile.Parse(cabNode.Path, slice);
            Assert.Equal(copy.Objects, inPlace.Objects);
            Assert.Equal(copy.Externals, inPlace.Externals);
            foreach (var obj in copy.Objects)
            {
                var a = copy.OpenObject(obj);
                var b = inPlace.OpenObject(obj);
                Assert.Equal(a.ReadBytes(a.Length), b.ReadBytes(b.Length));
            }
        }

        /// A serialized file parsed from a window reads only inside it: an object past the window's
        /// end fails as one past the end of a file of that size does, though the array goes on.
        [Fact]
        public void SerializedFileInAWindowStaysInside()
        {
            var archive = UnityFsArchive.Open(Path.Combine(GameData.DataDir, "StreamingAssets", "hires_base_bundle"));
            var cabNode = archive.Nodes.First(n => !n.Path.EndsWith(".resS"));
            var bytes = archive.Read(cabNode);
            // The node in the middle of a larger array, with bytes before and after it.
            var padded = new byte[bytes.Length + 64];
            bytes.CopyTo(padded, 32);
            var file = SerializedFile.Parse(cabNode.Path, new System.ArraySegment<byte>(padded, 32, bytes.Length));
            var last = file.Objects.OrderBy(o => o.ByteStart).Last();
            var r = file.OpenObject(last);
            Assert.Equal(last.ByteSize, r.Length);
            Assert.Equal(22, file.Version);
            // Version 22's header: the data offset is the big-endian Int64 at byte 32.
            long dataOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(32));
            int cut = checked((int)(dataOffset + last.ByteStart + last.ByteSize - 1));
            var truncated = SerializedFile.Parse(cabNode.Path, new System.ArraySegment<byte>(padded, 32, cut));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => truncated.OpenObject(last));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => SerializedFile.Parse(cabNode.Path, bytes[..cut]).OpenObject(last));
        }
    }
}
