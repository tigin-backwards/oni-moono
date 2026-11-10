using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// Resources.UnloadAsset: a texture's texels are freed, the object stays valid, and its next
    /// use reads it from the asset file again.
    public class UnloadAssetTests : EngineTest
    {
        // resources.assets: the first vista's outline (U60's VistaLayers loads and unloads these).
        static Texture2D LoadVistaTexture()
        {
            var id = TmpTestSupport.FindId("resources.assets", "outline_0", typeof(Texture2D));
            return (Texture2D)ObjectLoader.Load(AssetSystem.Get("resources.assets"), id);
        }

        static TexelTextureNative NativeOf(Texture2D t) => (TexelTextureNative)t.Native;

        [Fact]
        public void TexelsAreFreedAndReadAgainOnNextUse()
        {
            var texture = LoadVistaTexture();
            var native = NativeOf(texture);
            byte[] before = native.Memory.Span.ToArray();
            int id = texture.GetInstanceID();
            long live = PixelMemory.LiveBytes;

            Resources.UnloadAsset(texture);

            Assert.True(native.Unloaded);
            Assert.True(PixelMemory.LiveBytes <= live - before.Length);
            // The object stays valid, with its id and description.
            Assert.True(texture != null);
            Assert.Equal(id, texture.GetInstanceID());
            Assert.True(texture.width > 0);
            Assert.Equal(before, native.Memory.Span.ToArray());
            Assert.False(native.Unloaded);
        }

        [Fact]
        public void LoadingAgainGivesTheSameObject()
        {
            var texture = LoadVistaTexture();
            Resources.UnloadAsset(texture);
            Assert.Same(texture, LoadVistaTexture());
            Assert.True(NativeOf(texture).HasTexels);
        }

        [Fact]
        public void ReloadBringsBackTheFilesSettings()
        {
            var texture = LoadVistaTexture();
            var mode = texture.filterMode;
            var changed = mode == FilterMode.Point ? FilterMode.Bilinear : FilterMode.Point;
            texture.filterMode = changed;
            Resources.UnloadAsset(texture);
            _ = NativeOf(texture).Memory;
            Assert.Equal(mode, texture.filterMode);
        }

        [Fact]
        public void TexelsHandedToAnUploadComeFromTheFile()
        {
            GameInstall.RequirePayloads();
            var texture = LoadVistaTexture();
            var native = NativeOf(texture);
            byte[] before = native.Memory.Span.ToArray();
            Resources.UnloadAsset(texture);
            Assert.True(native.TexelsUnread);
            var (memory, source) = native.DetachTexels();
            var bytes = memory != null ? memory.Span.ToArray() : source.Value.Read();
            Assert.Equal(before, bytes);
            // The texture is shared with the other tests (the loader hands out one object per
            // asset): unloaded again, its next use reads the file instead of finding no texels.
            Resources.UnloadAsset(texture);
            Assert.Equal(before, native.Memory.Span.ToArray());
        }

        [Fact]
        public void TexturesMadeAtRunTimeKeepTheirTexels()
        {
            var texture = new Texture2D(4, 4);
            texture.SetPixel(1, 1, Color.red);
            texture.Apply();
            Resources.UnloadAsset(texture);
            Assert.False(NativeOf(texture).Unloaded);
            Assert.Equal(Color.red, texture.GetPixel(1, 1));
            Object.DestroyImmediate(texture);
        }

        [Fact]
        public void ADestroyedUnloadedTextureIsNotReadAgain()
        {
            var texture = LoadVistaTexture();
            var native = NativeOf(texture);
            Resources.UnloadAsset(texture);
            Object.DestroyImmediate(texture, true);
            Assert.False(native.Unloaded);
            Assert.False(native.HasTexels);
        }

        [Fact]
        public void OtherObjectsAndNullAreIgnored()
        {
            Resources.UnloadAsset(null);
            var go = new GameObject("not an asset");
            Resources.UnloadAsset(go);
            Assert.True(go != null);
        }
    }
}
