#pragma warning disable CS0618 // enableKerning, as the game's SetTextStyleSetting sets it
using TMPro;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // Klei's KForceUpdateDirty lays an active text out at once (docs/cleanroom/textmeshpro.md,
    // "When it lays out"). HoverTextDrawer reads renderedWidth right after it, and Thermal
    // Tooltips Reworked measures strings for its hover card with GetRenderedValues() on a text it
    // has just instantiated and force-updated (#70). Values measured on the shipped package
    // (tools/UnityProbe/RenderedSize.cs): "°C: 0.000000000" in NotoSans-Regular 14 pt, set up as
    // SetTextStyleSetting does, renders 106.203133 x 19.0312462, preferred width 106.21. (The
    // height is compared to 1e-4: the shipped package's differs from ours in the last bits, in
    // a rect of another size.)
    public class TmpForceUpdateDirtyTests : EngineTest
    {
        const string Text = "°C: 0.000000000";
        const float Width = 106.203133f, Height = 19.0312462f;

        static TextMeshProUGUI NewText(bool active = true)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform));
            canvas.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("Rs", typeof(RectTransform));
            if (!active) go.SetActive(false);
            go.transform.SetParent(canvas.transform, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.enableKerning = true;
            t.extraPadding = true;
            t.font = TmpTestSupport.FindAsset<TMP_FontAsset>("NotoSans-Regular");
            t.fontSize = 14f;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            return t;
        }

        [Fact]
        public void AFreshTextIsLaidOutWhenItReturns()
        {
            var t = NewText();
            t.text = Text;
            t.KForceUpdateDirty();
            Assert.False(t.havePropertiesChanged);
            Assert.Equal(15, t.textInfo.characterCount);
            var rv = t.GetRenderedValues();
            Assert.Equal(Width, rv.x);
            Assert.Equal(Height, rv.y, 4);
            Assert.Equal(rv, t.GetRenderedValues(false));
            Assert.Equal(Width, t.renderedWidth);
            Assert.Equal(106.21f, t.preferredWidth, 4);
            Assert.Equal(Width, t.textBounds.size.x, 4);
        }

        [Fact]
        public void AChangedTextReportsItsNewSizeAtOnce()
        {
            var t = NewText();
            t.text = "ABC";
            t.ForceMeshUpdate();
            Assert.Equal(26.90625f, t.renderedWidth);
            t.text = Text;
            t.KForceUpdateDirty();
            Assert.Equal(Width, t.renderedWidth);
            Assert.Equal(Height, t.renderedHeight, 4);
        }

        [Fact]
        public void WithoutItAChangedTextKeepsItsLastLayout()
        {
            var t = NewText();
            t.text = "ABC";
            t.ForceMeshUpdate();
            t.text = Text;
            Assert.True(t.havePropertiesChanged);
            Assert.Equal(26.90625f, t.renderedWidth);
            Assert.Equal(3, t.textInfo.characterCount);
        }

        [Fact]
        public void AnInactiveTextStaysChanged()
        {
            var t = NewText(active: false);
            t.text = Text;
            t.KForceUpdateDirty();
            Assert.True(t.havePropertiesChanged);
            Assert.Equal(0, t.textInfo.characterCount);
        }
    }
}
