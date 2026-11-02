using TMPro;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // Superscript and subscript offsets, as Unity's player 761211 lays them out (UnityProbe
    // suptext: NotoSans 14 pt, extra padding and kerning, top left, 338.22 wide). The offset is at
    // the script's reduced size, so a superscript's top meets the line's ascender in NotoSans and
    // the line keeps its height.
    public class TmpScriptOffsetTests : EngineTest
    {
        static TextMeshProUGUI Text(string text, bool wrap = true)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform));
            canvas.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("Su", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas.transform, false);
            rt.sizeDelta = new Vector2(338.22f, 200f);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.enableKerning = true;
            t.extraPadding = true;
            t.font = TmpTestSupport.FindAsset<TMP_FontAsset>("NotoSans-Regular");
            t.fontSize = 14f;
            t.alignment = TextAlignmentOptions.TopLeft;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.text = text;
            t.ForceMeshUpdate();
            return t;
        }

        [Theory]
        [InlineData("Survive 100 cycles after Demolior asteroid collides with RelicAAAA<sup>AAAGHH</sup>.", true, 38.1f)]
        [InlineData("Survive 100 cycles after Demolior asteroid collides with RelicAAAA<sup>AAAGHH</sup>.", false, 19.04f)]
        [InlineData("A<sup>B</sup>", true, 19.04f)]
        [InlineData("A<sub>B</sub>", true, 19.04f)]
        [InlineData("A\nB<sup>C</sup>", true, 38.1f)]
        public void AScriptBesideTextKeepsTheLinesHeight(string text, bool wrap, float height)
        {
            Assert.Equal(height, Text(text, wrap).preferredHeight, 0.001f);
        }

        [Fact]
        public void TheOffsetIsAtTheScriptsSize()
        {
            var sup = Text("A<sup>B</sup>").textInfo;
            Assert.Equal(7.474f, sup.characterInfo[1].baseLine - sup.lineInfo[0].baseline, 0.01f);
            Assert.Equal(sup.lineInfo[0].ascender, sup.characterInfo[1].ascender, 0.01f);
            var sub = Text("A<sub>B</sub>").textInfo;
            Assert.Equal(-0.7007f, sub.characterInfo[1].baseLine - sub.lineInfo[0].baseline, 0.01f);
        }
    }
}
