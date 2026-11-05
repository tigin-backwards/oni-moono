using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    public class BlitMultiTapTests : EngineTest
    {
        /// Shaders written for BlitMultiTap (the game's bloom blur, Hidden/BlurEffectConeTap) build
        /// their taps from _BlurOffsets; measured in the original (tools/UnityProbe, MULTITAP), it
        /// is the first offset. Left unset, the bloom was copied shifted instead of spread (#4).
        [Fact]
        public void TheFirstOffsetIsGivenAsBlurOffsets()
        {
            var game = System.Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                       ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762107");
            OniMoono.Engine.Assets.AssetSystem.Initialize(System.IO.Path.Combine(game, "OxygenNotIncluded_Data"));
            OniMoono.Engine.Assets.ProjectSettings.Load();
            var source = new RenderTexture(32, 32, 0);
            var dest = new RenderTexture(32, 32, 0);
            var material = new Material(Shader.Find("Hidden/Internal-GUITexture") ?? Shader.Find("UI/Default"));
            Graphics.BlitMultiTap(source, dest, material, new Vector2(3f, 1f), new Vector2(-5f, 4f), new Vector2(6f, -7f), new Vector2(0f, 2f));
            Assert.Equal(new Vector4(3f, 1f, 0f, 0f), Shader.GetGlobalVector("_BlurOffsets"));
        }
    }
}
