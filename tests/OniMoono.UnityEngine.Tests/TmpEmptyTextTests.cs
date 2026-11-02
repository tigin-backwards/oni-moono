#pragma warning disable CS0618 // enableKerning, as the game's SetTextStyleSetting sets it
using TMPro;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // Texts with nothing to lay out (docs/cleanroom/textmeshpro.md, "Texts with nothing to lay
    // out"): "" and null keep the last overflow and clear the rest, a text of tags alone is laid
    // out with a preferred size of a hundredth, white space alone is one line, and auto-size
    // gives these texts the size set or the maximum. NotoSans-Regular 16 pt, 95.7 wide, top left,
    // set up as the game's SetTextStyleSetting does (kerning, extra padding).
    public class TmpEmptyTextTests : EngineTest
    {
        const string Noto = "NotoSans-Regular";
        const float W = 95.7f;
        // One line of NotoSans 16 pt is 21.75 high.
        const float OneLine = 21.76f;

        static TextMeshProUGUI Label(float h, TextOverflowModes mode = TextOverflowModes.Overflow, bool wrap = false, bool auto = false,
            float size = 16f, float? max = null)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform));
            canvas.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("Et", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas.transform, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(W, h);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.enableKerning = true;
            t.extraPadding = true;
            t.fontSize = size;
            t.font = TmpTestSupport.FindAsset<TMP_FontAsset>(Noto);
            t.alignment = TextAlignmentOptions.TopLeft;
            t.overflowMode = mode;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            if (auto)
            {
                t.fontSizeMin = 4f;
                t.fontSizeMax = max ?? size;
                t.enableAutoSizing = true;
            }
            return t;
        }

        static TextMeshProUGUI Laid(string text, float h, TextOverflowModes mode = TextOverflowModes.Overflow, bool wrap = false, bool auto = false,
            float size = 16f, float? max = null)
        {
            var t = Label(h, mode, wrap, auto, size, max);
            Set(t, text);
            return t;
        }

        static void Set(TMP_Text t, string text)
        {
            t.text = text;
            t.ForceMeshUpdate(true);
        }

        static void Resize(TMP_Text t, float h)
        {
            ((RectTransform)t.transform).sizeDelta = new Vector2(W, h);
            t.ForceMeshUpdate(true);
        }

        static void AssertCleared(TMP_Text t)
        {
            Assert.False(t.isTextTruncated);
            var info = t.textInfo;
            Assert.Equal(0, info.characterCount);
            Assert.Equal(0, info.lineCount);
            Assert.Equal(0, info.spaceCount);
            Assert.Equal(0, info.wordCount);
            Assert.Equal(0, info.pageCount);
            Assert.Equal(0f, t.preferredWidth);
            Assert.Equal(0f, t.preferredHeight);
        }

        // The section's table.

        [Fact]
        public void TheSweepersUnitsLabelKeepsItsOverflowWhenEmptied()
        {
            var t = Laid("kg", 15f);
            Assert.True(t.isTextOverflowing);
            Assert.Equal(0, t.firstOverflowCharacterIndex);
            Set(t, "");
            Assert.True(t.isTextOverflowing);
            Assert.Equal(0, t.firstOverflowCharacterIndex);
            AssertCleared(t);
            // Read again after the preferred sizes.
            Assert.Equal(0, t.firstOverflowCharacterIndex);
        }

        [Fact]
        public void ResizingAnEmptyTextChangesNothing()
        {
            var t = Laid("kg", 15f);
            Set(t, "");
            foreach (float h in new[] { 100f, 0f, 15f })
            {
                Resize(t, h);
                Assert.True(t.isTextOverflowing);
                Assert.Equal(0, t.firstOverflowCharacterIndex);
                AssertCleared(t);
            }
        }

        [Fact]
        public void AnEmptyTextAfterTruncationKeepsTheOverflowButIsNotTruncated()
        {
            var t = Laid("kg", 15f, TextOverflowModes.Truncate);
            Assert.True(t.isTextTruncated);
            Set(t, "");
            Assert.True(t.isTextOverflowing);
            Assert.Equal(0, t.firstOverflowCharacterIndex);
            AssertCleared(t);
        }

        [Theory]
        [InlineData(TextOverflowModes.Overflow)]
        [InlineData(TextOverflowModes.Truncate)]
        [InlineData(TextOverflowModes.Ellipsis)]
        public void AnEmptyTextKeepsAnOverflowOnTheSecondLine(TextOverflowModes mode)
        {
            var t = Laid("AB\nCD", OneLine + 0.01f, mode);
            Assert.Equal(3, t.firstOverflowCharacterIndex);
            Set(t, "");
            Assert.True(t.isTextOverflowing);
            Assert.Equal(3, t.firstOverflowCharacterIndex);
            AssertCleared(t);
        }

        [Theory]
        [InlineData(100f)]
        [InlineData(OneLine + 0.01f)]
        public void AnEmptyTextAfterATextThatFittedDoesNotOverflow(float h)
        {
            var t = Laid("kg", h);
            Set(t, "");
            Assert.False(t.isTextOverflowing);
            Assert.Equal(-1, t.firstOverflowCharacterIndex);
            AssertCleared(t);
        }

        [Fact]
        public void NullBehavesAsTheEmptyText()
        {
            var t = Laid("kg", 15f);
            Set(t, null);
            Assert.True(t.isTextOverflowing);
            Assert.Equal(0, t.firstOverflowCharacterIndex);
            AssertCleared(t);
        }

        [Theory]
        [InlineData("", 0f, TextOverflowModes.Overflow, false)]
        [InlineData("", 5f, TextOverflowModes.Truncate, true)]
        [InlineData("", 15f, TextOverflowModes.Ellipsis, false)]
        [InlineData("", 100f, TextOverflowModes.Page, true)]
        [InlineData(null, 0f, TextOverflowModes.Masking, false)]
        [InlineData(null, 15f, TextOverflowModes.ScrollRect, true)]
        public void AnEmptyTextNeverLaidOutOtherwiseDoesNotOverflow(string text, float h, TextOverflowModes mode, bool auto)
        {
            foreach (bool wrap in new[] { false, true })
            {
                var t = Laid(text, h, mode, wrap, auto);
                Assert.False(t.isTextOverflowing);
                Assert.Equal(-1, t.firstOverflowCharacterIndex);
                AssertCleared(t);
                Assert.Equal(16f, t.fontSize);
            }
        }

        [Fact]
        public void ATextOfTagsAloneClearsTheOverflow()
        {
            var t = Laid("kg", 15f);
            Set(t, "<b></b>");
            Assert.False(t.isTextOverflowing);
            Assert.Equal(-1, t.firstOverflowCharacterIndex);
            Assert.Equal(0, t.textInfo.characterCount);
            Assert.Equal(0.01f, t.preferredWidth);
            Assert.Equal(0.01f, t.preferredHeight);
        }

        [Theory]
        [InlineData("<b></b>")]
        [InlineData("<color=#FF0000></color>")]
        [InlineData("<size=150%></size>")]
        [InlineData("<i></i><u></u>")]
        public void ATextOfTagsAloneIsLaidOutWithoutCharacters(string text)
        {
            foreach (var mode in new[] { TextOverflowModes.Overflow, TextOverflowModes.Truncate, TextOverflowModes.Ellipsis, TextOverflowModes.Page })
                foreach (float h in new[] { 0f, 15f, 100f })
                    foreach (bool wrap in new[] { false, true })
                    {
                        var t = Laid(text, h, mode, wrap);
                        Assert.False(t.isTextOverflowing);
                        Assert.Equal(-1, t.firstOverflowCharacterIndex);
                        Assert.False(t.isTextTruncated);
                        var info = t.textInfo;
                        Assert.Equal(0, info.characterCount);
                        Assert.Equal(0, info.lineCount);
                        Assert.Equal(0, info.pageCount);
                        Assert.Equal(0, info.wordCount);
                        Assert.Equal(0.01f, t.preferredWidth);
                        Assert.Equal(0.01f, t.preferredHeight);
                    }
        }

        [Fact]
        public void ASpaceAtHeightZeroTruncatedKeepsItsCharacter()
        {
            var t = Laid(" ", 0f, TextOverflowModes.Truncate);
            Assert.False(t.isTextOverflowing);
            Assert.Equal(-1, t.firstOverflowCharacterIndex);
            Assert.Equal(1, t.textInfo.characterCount);
        }

        [Theory]
        [InlineData(" ", 1)]
        [InlineData("<b> </b>", 1)]
        [InlineData("<br>", 1)]
        [InlineData("​", 0)]
        public void WhiteSpaceAloneIsOneLineThatNeverOverflows(string text, int spaces)
        {
            foreach (var mode in new[] { TextOverflowModes.Overflow, TextOverflowModes.Truncate, TextOverflowModes.Ellipsis })
                foreach (float h in new[] { 0f, 15f, 100f })
                {
                    var t = Label(h, mode);
                    Set(t, "kg");
                    Set(t, text);
                    Assert.False(t.isTextOverflowing);
                    Assert.Equal(-1, t.firstOverflowCharacterIndex);
                    var info = t.textInfo;
                    Assert.Equal(1, info.characterCount);
                    Assert.Equal(1, info.lineCount);
                    Assert.Equal(1, info.pageCount);
                    Assert.Equal(spaces, info.spaceCount);
                    Assert.Equal(1, info.wordCount);
                    Assert.Equal(0.01f, t.preferredWidth);
                    Assert.Equal(0.01f, t.preferredHeight);
                }
        }

        [Theory]
        [InlineData(" ", 15f)]
        [InlineData(" ", 0f)]
        [InlineData("<b></b>", 15f)]
        [InlineData("<b></b>", 0f)]
        public void AutoSizeGivesATextWithNothingVisibleTheMaximum(string text, float h)
        {
            Assert.Equal(24f, Laid(text, h, auto: true, max: 24f).fontSize);
            Assert.Equal(12f, Laid(text, h, auto: true, max: 12f).fontSize);
        }

        [Fact]
        public void AutoSizeGivesAnEmptiedTextTheSizeSet()
        {
            foreach (float max in new[] { 16f, 24f })
            {
                var t = Laid("kg", 15f, auto: true, max: max);
                Assert.Equal(11f, t.fontSize);
                Set(t, "");
                Assert.Equal(16f, t.fontSize);
                Assert.False(t.isTextOverflowing);
            }
            var low = Laid("kg", 15f, auto: true, max: 12f);
            Set(low, "");
            Assert.Equal(12f, low.fontSize);
        }

        [Fact]
        public void AutoSizeGivesAnEmptyTextNoMoreThanTheMaximum()
        {
            Assert.Equal(12f, Laid("", 15f, auto: true, max: 12f).fontSize);
            Assert.Equal(16f, Laid("", 15f, auto: true, max: 24f).fontSize);
        }

        [Fact]
        public void TheGrayStrokeTextEmptiedTakesAMaximumBelowTheSizeSet()
        {
            var t = Label(15f, auto: true, size: 12f, max: 8f);
            t.font = TmpTestSupport.FindAsset<TMP_FontAsset>("GRAYSTROKE REGULAR SDF");
            Set(t, "kg");
            Set(t, "");
            Assert.Equal(8f, t.fontSize);
        }

        [Theory]
        [InlineData(16f, 18.38f, 21.76f)]
        [InlineData(24f, 27.57f, 32.63f)]
        [InlineData(12f, 13.79f, 16.32f)]
        public void AnUnwrappedAutoSizedTextsPreferredSizeIsAtTheMaximum(float max, float w, float h)
        {
            var t = Laid("kg", 15f, auto: true, max: max);
            Assert.Equal(11f, t.fontSize);
            Assert.Equal(w, t.preferredWidth, 0.001f);
            Assert.Equal(h, t.preferredHeight, 0.001f);
        }

        // A wrapped auto-sized text's preferred height is at the size auto-size chooses for the
        // width alone, whatever the rect's height (UnityProbe autosizepref, Unity's player
        // 761211: NotoSans 8 to 14 pt, extra padding and kerning, wrapping, top right, 72 wide).
        [Theory]
        [InlineData("0/0", 0f, 8f, 19.04f)]
        [InlineData("0/0", 10.88f, 8f, 19.04f)]
        [InlineData("0/0", 100f, 14f, 19.04f)]
        [InlineData("0.0 J", 5f, 8f, 19.04f)]
        [InlineData("Avg. 0 rads", 19.04f, 13.85f, 38.1f)]
        [InlineData("Avg. 0 rads", 100f, 14f, 38.1f)]
        [InlineData("PowerUseDiagnosticKey", 100f, 8f, 21.78f)]
        [InlineData("one two three four five six", 10.88f, 8f, 57.17f)]
        [InlineData("one two three four five six", 100f, 14f, 57.17f)]
        public void AWrappedAutoSizedTextsPreferredHeightIgnoresTheRectsHeight(string text, float h, float size, float height)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform));
            canvas.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("Ap", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas.transform, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(72f, h);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.enableKerning = true;
            t.extraPadding = true;
            t.font = TmpTestSupport.FindAsset<TMP_FontAsset>(Noto);
            t.fontSize = 14f;
            t.alignment = TextAlignmentOptions.TopRight;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.fontSizeMin = 8f;
            t.fontSizeMax = 14f;
            t.enableAutoSizing = true;
            Set(t, text);
            Assert.Equal(size, t.fontSize, 0.001f);
            Assert.Equal(height, t.preferredHeight, 0.001f);
            Assert.Equal(height, t.GetPreferredValues(72f, 0f).y, 0.001f);
        }
    }
}
