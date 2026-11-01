#pragma warning disable CS0618 // enableKerning, as the game's SetTextStyleSetting sets it
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Xunit;

namespace OniMoono.Tests
{
    // Preferred sizes to the next hundredth above the widest line's pen, measured from the line's
    // start (docs/cleanroom/textmeshpro.md, "Preferred sizes to the hundredth"). Labels are set
    // up as the game's SetTextStyleSetting does (kerning, extra padding, no wrapping) in a
    // 400 × 40 rect, as the measurements were.
    public class TmpPreferredSizeTests : EngineTest
    {
        static RectTransform Canvas()
        {
            var go = new GameObject("Canvas", typeof(RectTransform));
            go.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            return (RectTransform)go.transform;
        }

        static TextMeshProUGUI Label(Transform parent, string font, float size, string text, FontStyles style = FontStyles.Normal)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.enableKerning = true;
            t.extraPadding = true;
            t.fontSize = size;
            t.font = TmpTestSupport.FindAsset<TMP_FontAsset>(font);
            t.fontStyle = style;
            t.text = text;
            return t;
        }

        static TextMeshProUGUI Alone(string font, float size, string text, float pivot = 0.5f, FontStyles style = FontStyles.Normal)
        {
            var canvas = Canvas();
            var t = Label(canvas, font, size, text, style);
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(pivot, 0.5f);
            rt.sizeDelta = new Vector2(400f, 40f);
            return t;
        }

        [Theory]
        [InlineData("NotoSans-Regular", 12f, "Ethanol Gas", 67.26f)]
        [InlineData("NotoSans-Regular", 12f, "Super Coolant Gas", 104.26f)]
        [InlineData("NotoSans-Regular", 12f, "Nuclear Fallout", 85.1f)]
        [InlineData("NotoSans-Regular", 12f, "Room", 33.19f)]
        [InlineData("NotoSans-Regular", 16f, "Super Coolant Gas", 139.01f)]
        [InlineData("NotoSans-Regular", 16f, "Room", 44.26f)]
        [InlineData("GRAYSTROKE REGULAR SDF", 16f, "Hydrogen", 67.51f)]
        [InlineData("GRAYSTROKE OUTLINE SDF", 14f, "A", 8.51f)]
        [InlineData("NotoSansCJKjp-Regular", 12f, "Oxygen", 41.86f)]
        [InlineData("NotoSansCJKjp-Regular", 12f, "i", 3.3f)]
        // The pen ends a float step past the hundredth in the shipped package (102.400009,
        // 59.1000023, 65.5600052), where advances rounded to float before the sum fell on or
        // under it (measured 2026-11-01, PROBE_SCENARIO=textwidth).
        [InlineData("GRAYSTROKE REGULAR SDF", 16f, "Light Collector", 102.41f)]
        [InlineData("GRAYSTROKE OUTLINE SDF", 14f, "Hydrogen", 59.11f)]
        [InlineData("NotoSansCJKjp-Regular", 12f, "Natural Gas", 65.57f)]
        public void TheWidthIsTheNextHundredthAboveThePen(string font, float size, string text, float expected)
        {
            var t = Alone(font, size, text);
            Assert.Equal(expected, t.preferredWidth);
            Assert.Equal(expected, t.GetPreferredValues(text).x);
        }

        [Fact]
        public void TheWidthIsThePenAtTheEndOfTheLastVisibleCharacter()
        {
            // At pivot 0 the rect's left edge is at 0, so the last character's xAdvance is the
            // pen from the line's start; the width is (floor(x * 100) + 1) / 100 with x * 100
            // taken in double.
            foreach (var font in new[] { "NotoSans-Regular", "GRAYSTROKE REGULAR SDF", "NotoSansCJKjp-Regular" })
                foreach (float size in new[] { 12f, 14f, 16f })
                    foreach (var text in new[] { "Ethanol Gas", "Polluted Oxygen", "Carbon Dioxide", "Search", "W" })
                    {
                        var t = Alone(font, size, text, pivot: 0f);
                        t.ForceMeshUpdate(true);
                        var info = t.textInfo;
                        double x = info.characterInfo[info.characterCount - 1].xAdvance;
                        Assert.Equal((float)((System.Math.Floor(x * 100.0) + 1.0) / 100.0), t.preferredWidth);
                    }
        }

        [Theory]
        [InlineData("GRAYSTROKE REGULAR SDF", 16f, "Hydrogen")]
        [InlineData("NotoSansCJKjp-Regular", 12f, "i")]
        [InlineData("NotoSans-Regular", 14f, "Polluted Oxygen")]
        [InlineData("NotoSans-Regular", 12f, "Ethanol Gas")]
        public void TheValueDoesNotMoveWithTheRect(string font, float size, string text)
        {
            float centred = Alone(font, size, text).preferredWidth;
            Assert.Equal(centred, Alone(font, size, text, pivot: 0f).preferredWidth);
            Assert.Equal(centred, Alone(font, size, text, pivot: 0.37f).preferredWidth);
            var moved = Alone(font, size, text, pivot: 0f);
            moved.rectTransform.anchoredPosition = new Vector2(123.456f, -7.89f);
            Assert.Equal(centred, moved.preferredWidth);

            // Under a HorizontalLayoutGroup beside a 24-wide icon (the overlay legend's row).
            var row = new GameObject("Row", typeof(RectTransform));
            var rrt = (RectTransform)row.transform;
            rrt.SetParent(Canvas(), false);
            rrt.sizeDelta = new Vector2(222f, 24f);
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(4, 4, 0, 0);
            hlg.spacing = 12;
            hlg.childControlWidth = hlg.childControlHeight = true;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;
            var icon = new GameObject("Icon", typeof(RectTransform));
            icon.transform.SetParent(rrt, false);
            var le = icon.AddComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = le.minHeight = le.preferredHeight = 24;
            var child = Label(rrt, font, size, text);
            LayoutRebuilder.ForceRebuildLayoutImmediate(rrt);
            Assert.Equal(centred, child.preferredWidth);
            Assert.Equal(44f + centred, hlg.preferredWidth);
        }

        [Fact]
        public void LayoutGroupsAndElementsTakeTheValueAsItIs()
        {
            var row = new GameObject("Row", typeof(RectTransform));
            var rrt = (RectTransform)row.transform;
            rrt.SetParent(Canvas(), false);
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(4, 4, 0, 0);
            hlg.spacing = 12;
            hlg.childControlWidth = hlg.childControlHeight = true;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;
            var icon = new GameObject("Icon", typeof(RectTransform));
            icon.transform.SetParent(rrt, false);
            var le = icon.AddComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = le.minHeight = le.preferredHeight = 24;
            var child = Label(rrt, "NotoSans-Regular", 12f, "Super Coolant Gas");
            LayoutRebuilder.ForceRebuildLayoutImmediate(rrt);
            Assert.Equal(148.26001f, hlg.preferredWidth);
            child.text = " ";
            LayoutRebuilder.ForceRebuildLayoutImmediate(rrt);
            Assert.Equal(0.01f, LayoutUtility.GetPreferredWidth(child.rectTransform));
            // The shipped group's sum is the float 44.01; this group's order of addition gives
            // the float above it (the uGUI side, not the text's).
            Assert.Equal(44.01f, hlg.preferredWidth, 4);

            var copy = new GameObject("Le", typeof(RectTransform)).AddComponent<LayoutElement>();
            copy.preferredWidth = Alone("NotoSans-Regular", 12f, "Ethanol Gas").preferredWidth;
            Assert.Equal(67.26f, copy.preferredWidth);
        }

        [Theory]
        [InlineData("NotoSans-Regular", 14f, " ")]
        [InlineData("NotoSans-Regular", 14f, "  ")]
        [InlineData("GRAYSTROKE REGULAR SDF", 12f, " ")]
        public void WhiteSpaceAloneIsAHundredthSquare(string font, float size, string text)
        {
            var t = Alone(font, size, text);
            Assert.Equal(0.01f, t.preferredWidth);
            Assert.Equal(0.01f, t.preferredHeight);
            Assert.Equal(new Vector2(0.01f, 0.01f), t.GetPreferredValues());
        }

        [Fact]
        public void AnEmptyTextHasNoSize()
        {
            var t = Alone("NotoSans-Regular", 14f, "");
            Assert.Equal(0f, t.preferredWidth);
            Assert.Equal(0f, t.preferredHeight);
        }

        [Theory]
        [InlineData(16f, 21.76f)]   // 21.75, already on a hundredth
        [InlineData(12f, 16.32f)]   // 16.3125
        public void HeightsGoToTheNextHundredthAbove(float size, float expected)
        {
            var t = Alone("NotoSans-Regular", size, "Room");
            Assert.Equal(expected, t.preferredHeight);
            var face = t.font.faceInfo;
            double x = (double)(face.ascentLine * (size / face.pointSize * face.scale)) - face.descentLine * (size / face.pointSize * face.scale);
            Assert.Equal((float)((System.Math.Floor(x * 100.0) + 1.0) / 100.0), t.preferredHeight);
        }

        // Each pen is the previous one plus the glyph's advance × scale, summed in double and
        // rounded to float once: the shipped package's pens at pivot 0, bit for bit (measured
        // 2026-11-01). Rounding the advance to float first put "Light Collector"'s "C" one
        // float step short (48.246773) and its end two (102.399994).
        [Theory]
        [InlineData("GRAYSTROKE REGULAR SDF", 16f, "Light Collector", new[] { 8.637096f, 13.0080643f, 21.64516f, 30.2822571f, 36.25f, 39.2870979f, 48.2467766f, 56.883873f, 61.25484f, 65.62581f, 74.26291f, 81.82742f, 87.795166f, 96.43227f, 102.400009f })]
        [InlineData("GRAYSTROKE OUTLINE SDF", 14f, "Hydrogen", new[] { 8.5f, 16.0625f, 23.625f, 28.85f, 36.4125023f, 43.9750023f, 51.5375023f, 59.1000023f })]
        [InlineData("NotoSansCJKjp-Regular", 12f, "Natural Gas", new[] { 8.675f, 15.4500008f, 19.975f, 27.25f, 31.9f, 38.675f, 42.1f, 44.885f, 53.16f, 59.935f, 65.5600052f })]
        public void PensSumTheAdvancesInDouble(string font, float size, string text, float[] pens)
        {
            var t = Alone(font, size, text, pivot: 0f);
            t.ForceMeshUpdate();
            var info = t.textInfo;
            Assert.Equal(pens.Length, info.characterCount);
            for (int i = 0; i < pens.Length; i++)
            {
                Assert.Equal(pens[i], info.characterInfo[i].xAdvance);
                if (i + 1 < pens.Length) Assert.Equal(pens[i], info.characterInfo[i + 1].origin);
            }
        }

        [Fact]
        public void BoldEndsAtTheLastGlyphAdvanceWithoutTheBoldSpacing()
        {
            var t = Alone("NotoSans-Regular", 12f, "Ethanol Gas", style: FontStyles.Bold);
            Assert.Equal(70.85f, t.preferredWidth);
            Assert.Equal(70.85f, Alone("NotoSans-Regular", 12f, "Ethanol Gas", pivot: 0f, style: FontStyles.Bold).preferredWidth);
        }
    }
}
