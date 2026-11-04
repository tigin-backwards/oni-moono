using System;
using OniMoono.Engine.Rendering;
using Unity.Collections;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // The game hands its property textures to LoadRawTextureData and Apply every frame, often
    // unchanged. An Apply after bytes equal to the texture's own asks the device for no upload
    // (the device already has them); any other way the texels may have changed still does.
    public class RawTextureReloadTests : EngineTest
    {
        static int Version(Texture2D t) => ((Texture2DNative)t.Native).ContentVersion;

        static byte[] Bytes(int length, int seed)
        {
            var b = new byte[length];
            new System.Random(seed).NextBytes(b);
            return b;
        }

        [Fact]
        public void CopyChangedCopiesAndReportsChanges()
        {
            foreach (int length in new[] { 0, 1, 4095, 4096, 3 * 8192 + 17 })
            {
                var from = Bytes(length, length);
                var into = (byte[])from.Clone();
                Assert.False(RawTextureLoads.CopyChanged(from, into));
                if (length == 0) continue;
                foreach (int at in new[] { 0, length / 2, length - 1 })
                {
                    from[at] ^= 0x5A;
                    Assert.True(RawTextureLoads.CopyChanged(from, into));
                    Assert.Equal(from, into);
                    Assert.False(RawTextureLoads.CopyChanged(from, into));
                }
            }
            Assert.Throws<ArgumentException>(() => RawTextureLoads.CopyChanged(new byte[4], new byte[5]));
        }

        [Fact]
        public void SameBytesNeedNoUpload()
        {
            var t = new Texture2D(40, 30, TextureFormat.RGBA32, false);
            var data = Bytes(40 * 30 * 4, 1);
            t.LoadRawTextureData(data);
            t.Apply();
            int v = Version(t);
            uint updates = t.updateCount;
            t.LoadRawTextureData(data);
            t.Apply();
            Assert.Equal(v, Version(t));
            Assert.Equal(updates + 1, t.updateCount);
            Assert.Equal(data, t.GetRawTextureData());
        }

        [Fact]
        public void ChangedBytesAreUploaded()
        {
            var t = new Texture2D(40, 30, TextureFormat.RGBA32, false);
            var data = Bytes(40 * 30 * 4, 2);
            t.LoadRawTextureData(data);
            t.Apply();
            int v = Version(t);
            data[data.Length - 1] ^= 1;
            t.LoadRawTextureData(data);
            t.Apply();
            Assert.Equal(v + 1, Version(t));
            Assert.Equal(data, t.GetRawTextureData());
        }

        [Fact]
        public void ChangesBeforeAnUnchangedLoadAreUploaded()
        {
            var t = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var data = Bytes(8 * 8 * 4, 3);
            t.LoadRawTextureData(data);
            t.Apply();
            // A change, then bytes equal to the texels as changed: the Apply still uploads.
            data[5] ^= 1;
            t.LoadRawTextureData(data);
            t.LoadRawTextureData(data);
            int v = Version(t);
            t.Apply();
            Assert.Equal(v + 1, Version(t));
        }

        [Fact]
        public void OtherWritesAreUploaded()
        {
            var t = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var data = Bytes(8 * 8 * 4, 4);
            t.LoadRawTextureData(data);
            t.Apply();
            int v = Version(t);
            t.SetPixel(1, 1, Color.red);
            t.LoadRawTextureData(t.GetRawTextureData());
            t.Apply();
            Assert.Equal(v + 1, Version(t));
        }

        [Fact]
        public void AliasedTexelsAreAlwaysUploaded()
        {
            var t = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var data = Bytes(8 * 8 * 4, 5);
            NativeArray<byte> alias = t.GetRawTextureData<byte>();
            t.LoadRawTextureData(data);
            t.Apply();
            int v = Version(t);
            // A write through the alias after the Apply, then the same bytes loaded again.
            alias[0] ^= 1;
            data[0] = alias[0];
            t.LoadRawTextureData(data);
            t.Apply();
            Assert.Equal(v + 1, Version(t));
            t.LoadRawTextureData(data);
            t.Apply();
            Assert.Equal(v + 2, Version(t));
        }

        [Fact]
        public void BecomingUnreadableIsUploaded()
        {
            var t = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var data = Bytes(8 * 8 * 4, 6);
            t.LoadRawTextureData(data);
            t.Apply();
            int v = Version(t);
            t.LoadRawTextureData(data);
            t.Apply(false, true);
            Assert.Equal(v + 1, Version(t));
        }

        [Fact]
        public void MipmappedTexturesStillMatchTheirLevels()
        {
            var t = new Texture2D(8, 8, TextureFormat.RGBA32, true);
            int size = (int)((Texture2DNative)t.Native).SliceSize;
            var data = Bytes(size, 7);
            t.LoadRawTextureData(data);
            t.Apply();
            var built = t.GetRawTextureData();
            int v = Version(t);
            // Apply rebuilt the mips: loading the original bytes again changes those levels.
            t.LoadRawTextureData(data);
            t.Apply();
            Assert.Equal(v + 1, Version(t));
            Assert.Equal(built, t.GetRawTextureData());
        }
    }
}
