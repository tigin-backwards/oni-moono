using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// Property sheets made often: a new block's texture store has room for an animation batch's
    /// textures from the start, and a blit's sheet is rented for the frame rather than made.
    public class PropertySheetStorageTests : EngineTest
    {
        [Fact]
        public void ANewBlocksTextureStoreHoldsABatchsTexturesWithoutGrowing()
        {
            var block = new MaterialPropertyBlock();
            var texture = new Texture2D(2, 2);
            block.SetTexture(Shader.PropertyToID("_Tex0"), texture);
            var store = block.Sheet.Textures;
            Assert.True(store.EnsureCapacity(0) >= PropertySheet.BlockTextureCapacity);
            for (int i = 1; i < PropertySheet.BlockTextureCapacity; i++) block.SetTexture(Shader.PropertyToID("_Tex" + i), texture);
            Assert.Same(store, block.Sheet.Textures);
            Assert.Equal(PropertySheet.BlockTextureCapacity, store.Count);
            // Insertion order and values are what a store made small and grown would hold.
            int n = 0;
            foreach (var (id, binding) in store)
            {
                Assert.Equal(Shader.PropertyToID("_Tex" + n++), id);
                Assert.Same(texture, binding.Texture);
                Assert.Equal(TextureBinding.IdentityScaleOffset, binding.ScaleOffset);
            }
            // Other sheets are made as small as before.
            var sheet = new PropertySheet();
            sheet.SetTexture(Shader.PropertyToID("_Tex0"), texture);
            Assert.True(sheet.Textures.EnsureCapacity(0) < PropertySheet.BlockTextureCapacity);
        }

        [Fact]
        public void ABlitRentsItsSheet()
        {
            AssetSystem.Initialize(GameInstall.DataDir);
            ProjectSettings.Load();
            FrameRenderer.Install();
            var device = new RecordingDevice();
            var previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            try
            {
                var material = new Material(Shader.Find("Hidden/Internal-GUITextureClip"));
                var source = new Texture2D(8, 8);
                long before = PropertySheet.Rented;
                Graphics.Blit(source, (RenderTexture)null, material);
                Assert.Equal(before + 1, PropertySheet.Rented);
                Assert.Same(material, Assert.Single(device.MeshDraws).Material);
            }
            finally
            {
                GraphicsDevice.Install(previous);
            }
        }
    }
}
