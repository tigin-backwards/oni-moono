using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Text;
using UnityEngine;
using UnityEngine.UI;
using Xunit;

namespace OniMoono.Tests
{
    /// docs/cleanroom/ugui.md, "Legacy Text glyph placement" (measured 2026-10-24 with
    /// tools/UnityProbe PROBE_SCENARIO=legacytext): the acceptance tests. The built-in font draws
    /// its glyphs from FreeSans here; they skip where FreeSans 0412.4271 is not installed.
    public class LegacyTextPlacementTests : EngineTest
    {
        const string FreeSansVersion = "Version 0412.4271";

        static Font Builtin()
        {
            var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-761211");
            AssetSystem.Initialize(Path.Combine(game, "OxygenNotIncluded_Data"));
            if (!OperatingSystem.IsLinux()) Assert.Skip("The face chain was measured on Linux.");
            if (!SystemFonts.TryFindStyled("FreeSans", 0, out var r) || NameString(File.ReadAllBytes(r.filePath), 5) != FreeSansVersion)
                Assert.Skip($"FreeSans {FreeSansVersion} is not installed.");
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        /// A name table string (platform 3 UTF-16BE, else platform 1 Roman).
        static string NameString(byte[] data, int nameId)
        {
            var t = new SfntTables(data, 0);
            if (!t.TryGet("name", out int o)) return null;
            int count = t.U16(o + 2), strings = o + t.U16(o + 4);
            string mac = null;
            for (int i = 0; i < count; i++)
            {
                int rec = o + 6 + 12 * i;
                if (t.U16(rec + 6) != nameId) continue;
                int platform = t.U16(rec), length = t.U16(rec + 8), offset = strings + t.U16(rec + 10);
                if (platform == 3) return Encoding.BigEndianUnicode.GetString(data, offset, length);
                if (platform == 1) mac ??= Encoding.Latin1.GetString(data, offset, length);
            }
            return mac;
        }

        static TextGenerationSettings Settings(Font font, int size, float scale, TextAnchor anchor = TextAnchor.UpperLeft, float width = 4000f, float height = 400f,
            HorizontalWrapMode wrap = HorizontalWrapMode.Overflow, bool updateBounds = true, Vector2? pivot = null) => new()
        {
            font = font, color = Color.white, fontSize = size, lineSpacing = 1f, richText = true, scaleFactor = scale, fontStyle = FontStyle.Normal,
            textAnchor = anchor, updateBounds = updateBounds, verticalOverflow = VerticalWrapMode.Overflow, horizontalOverflow = wrap,
            generationExtents = new Vector2(width, height), pivot = pivot ?? new Vector2(0f, 1f), generateOutOfBounds = true,
        };

        static TextGenerator Generate(string text, TextGenerationSettings settings)
        {
            var gen = new TextGenerator();
            Assert.True(gen.Populate(text, settings));
            return gen;
        }

        static float[] Cursors(TextGenerator gen) => gen.characters.Select(c => c.cursorPos.x).ToArray();

        static float[] Ints(string list) => list.Split(',').Select(float.Parse).ToArray();

        [Theory]
        [InlineData(11, 0.86f, 9)]
        [InlineData(10, 0.95f, 9)]
        [InlineData(9, 0.99f, 8)]
        [InlineData(16, 0.86f, 13)]
        public void PixelSizeIsTheScaledSizeRoundedDown(int size, float scale, int pixels)
        {
            var font = Builtin();
            Assert.Equal(pixels, Generate("Target type", Settings(font, size, scale)).fontSizeUsedForBestFit);
        }

        [Theory]
        [InlineData("Choose a target type and start conversion.", 11, 0.86f, "0,6,11,16,21,25,30,32,37,39,42,47,50,55,60,63,65,68,72,77,82,84,89,94,99,101,105,108,113,116,119,121,126,131,136,140,145,148,152,154,159,164,166", 166)]
        [InlineData("Choose a target type and start conversion.", 10, 0.86f, "0,6,10,14,18,22,26,28,32,34,36,40,43,47,51,53,55,57,61,65,69,71,75,79,83,85,89,91,95,98,100,102,106,110,114,118,122,125,129,131,135,139,141", 141)]
        [InlineData("Choose a target type and start conversion.", 13, 0.86f, "0,8,14,20,26,31,37,40,46,49,52,58,62,68,74,77,80,83,88,94,100,103,109,115,121,124,129,132,138,142,145,148,154,160,166,171,177,181,186,188,194,200,203", 203)]
        [InlineData("Target type", 11, 0.86f, "0,6,10,13,18,23,26,28,31,35,40,45", 45)]
        [InlineData("Target type", 10, 0.86f, "0,5,8,11,15,19,21,23,25,29,33,37", 37)]
        [InlineData("Select type", 11, 0.86f, "0,6,11,13,18,23,26,28,31,35,40,45", 45)]
        [InlineData("Geyser Converter", 13, 0.86f, "0,8,14,19,24,30,34,37,45,51,57,62,68,72,75,81,85", 85)]
        [InlineData("Geyser Converter", 11, 0.86f, "0,7,12,16,20,25,28,30,36,41,46,50,55,58,61,66,69", 69)]
        [InlineData("Geyser Converter", 10, 0.86f, "0,6,10,14,18,22,25,27,33,37,41,45,49,52,54,58,61", 61)]
        [InlineData("AVAWAYTaToLTPAFaVa", 20, 1f, "0,13,26,39,58,71,85,98,107,120,129,140,153,166,179,191,202,215,226", 226)]
        public void PanelLabelsGetTheMeasuredCursorsAndWidths(string text, int size, float scale, string cursors, float width)
        {
            var font = Builtin();
            var settings = Settings(font, size, scale);
            Assert.Equal(Ints(cursors), Cursors(Generate(text, settings)));
            Assert.Equal(width, new TextGenerator().GetPreferredWidth(text, settings));
        }

        [Fact]
        public void KerningComesFromTheSerializedPairsAndMovesTheQuadNotTheCursor()
        {
            var font = Builtin();
            var gen = Generate("Target type", Settings(font, 11, 0.86f));
            Assert.Equal(Ints("6,5,3,5,5,3,2,3,4,5,5,0"), gen.characters.Select(c => c.charWidth).ToArray());
            Assert.Equal(6f, gen.characters[1].cursorPos.x);
            Assert.True(font.GetCharacterInfo('a', out var a, 9, FontStyle.Normal));
            Assert.Equal(6 - 1 + a.minX, gen.verts[4].position.x);
            Assert.Equal(4f, gen.verts[4].position.x);

            float Kerned(int px)
            {
                var s = Settings(font, px, 1f);
                return new TextGenerator().GetPreferredWidth("Ta", s) - new TextGenerator().GetPreferredWidth("T", s) - new TextGenerator().GetPreferredWidth("a", s);
            }
            Assert.Equal(0f, Kerned(5));
            Assert.Equal(0f, Kerned(6));
            Assert.Equal(-1f, Kerned(7));
            Assert.Equal(-1f, Kerned(19));
            Assert.Equal(-2f, Kerned(20));
            // FreeSans's own kerning (AV) does not apply.
            Assert.Equal(0f, new TextGenerator().GetPreferredWidth("AV", Settings(font, 20, 1f))
                             - new TextGenerator().GetPreferredWidth("A", Settings(font, 20, 1f)) - new TextGenerator().GetPreferredWidth("V", Settings(font, 20, 1f)));
        }

        [Fact]
        public void SpacesHaveCharacterInfosButNoQuads()
        {
            var font = Builtin();
            var gen = Generate("Target type", Settings(font, 11, 0.86f));
            Assert.Equal(12, gen.characterCount);
            Assert.Equal(40, gen.vertexCount);
            gen = Generate("Choose a target type and start conversion.", Settings(font, 11, 0.86f));
            Assert.Equal(43, gen.characterCount);
            Assert.Equal(36 * 4, gen.vertexCount);
        }

        [Fact]
        public void CharacterInfoIsTheUnhintedBitmapGrownByOnePixel()
        {
            var font = Builtin();
            font.RequestCharactersInTexture("C ", 9, FontStyle.Normal);
            font.RequestCharactersInTexture("!", 20, FontStyle.Normal);
            font.RequestCharactersInTexture("A", 0, FontStyle.Normal);
            Assert.True(font.GetCharacterInfo('C', out var c, 9, FontStyle.Normal));
            Assert.Equal((6, -1, 8, -2, 8), (c.advance, c.minX, c.maxX, c.minY, c.maxY));
            Assert.Equal((9, 10), (c.glyphWidth, c.glyphHeight));
            Assert.True(font.GetCharacterInfo(' ', out var space, 9, FontStyle.Normal));
            Assert.Equal((2, -1, 1, -1, 1), (space.advance, space.minX, space.maxX, space.minY, space.maxY));
            Assert.True(font.GetCharacterInfo('!', out var bang, 20, FontStyle.Normal));
            Assert.Equal((7, 1, 6), (bang.advance, bang.minX, bang.maxX));
            Assert.True(font.GetCharacterInfo('A', out var a0, 0, FontStyle.Normal));
            Assert.Equal((9, -1, 10, -1, 11), (a0.advance, a0.minX, a0.maxX, a0.minY, a0.maxY));
            Assert.Equal(0, a0.size);
#pragma warning disable CS0618 // the float advance
            Assert.Equal(9f, a0.width);
#pragma warning restore CS0618
            Assert.True(font.GetCharacterInfo('A', out var a13, 13, FontStyle.Normal));
            Assert.Equal((a0.advance, a0.minX, a0.maxX, a0.minY, a0.maxY), (a13.advance, a13.minX, a13.maxX, a13.minY, a13.maxY));
            // Not requested at that size: not in the texture.
            Assert.False(font.GetCharacterInfo('C', out _, 47, FontStyle.Normal));

            // Every printable ASCII character at 5 to 26 pixels: FreeType's unhinted advance,
            // rounded, and its rendered bitmap's box grown by one pixel.
            SystemFonts.TryFindStyled("FreeSans", 0, out var r);
            Assert.Equal(UnityEngine.TextCore.LowLevel.FontEngineError.Success, FontFace.TryOpen(File.ReadAllBytes(r.filePath), 0, out var face));
            int matched = 0;
            using (face)
                for (int size = 5; size <= 26; size++)
                {
                    var ascii = new string(Enumerable.Range(32, 95).Select(i => (char)i).ToArray());
                    font.RequestCharactersInTexture(ascii, size, FontStyle.Normal);
                    foreach (char ch in ascii)
                    {
                        Assert.True(font.GetCharacterInfo(ch, out var info, size, FontStyle.Normal));
                        Assert.Equal((ch, size, FontStyle.Normal), ((char)info.index, info.size, info.style));
                        Assert.Equal(info.minX, info.bearing);
                        Assert.Equal(Expected(face, ch, size), (info.advance, info.minX, info.maxX, info.minY, info.maxY));
                        matched++;
                    }
                }
            Assert.Equal(2090, matched);
        }

        static unsafe (int, int, int, int, int) Expected(FontFace face, char c, int size)
        {
            face.SetPixelSize(size);
            Assert.True(face.Load(face.GlyphIndex(c), FT.LOAD_NO_HINTING));
            var slot = face.Slot;
            long advance = (long)slot->advance.x.Value;
            FT.FT_Render_Glyph(slot, FT.RENDER_MODE_NORMAL);
            int l = slot->bitmap_left, t = slot->bitmap_top, w = (int)slot->bitmap.width, h = (int)slot->bitmap.rows;
            return ((int)((advance + 32) >> 6), l - 1, l + w + 1, t - h - 1, t + 1);
        }

        [Fact]
        public void GlyphsFallBackAlongTheChain()
        {
            var font = Builtin();
            font.RequestCharactersInTexture("℥中", 20, FontStyle.Normal);
            if (SystemFonts.TryFindStyled("DejaVu Sans", 0, out _))
            {
                Assert.True(font.GetCharacterInfo('℥', out var dejavu, 20, FontStyle.Normal));
                Assert.Equal(12, dejavu.advance);
            }
            Assert.True(font.GetCharacterInfo('中', out var none, 20, FontStyle.Normal));
            Assert.Equal((0, -1, 1, -1, 1), (none.advance, none.minX, none.maxX, none.minY, none.maxY));
            Assert.True(font.HasCharacter('中'));
        }

        // The chain degrades as measured with faces hidden from the original: FreeSans, then
        // DejaVu Sans, then Liberation Sans, then none; an installed name comes first.
        [Theory]
        [InlineData("FreeSans,DejaVu Sans,Liberation Sans,Ubuntu", "Arial", true, "FreeSans,DejaVu Sans,Liberation Sans")]
        [InlineData("DejaVu Sans,Liberation Sans,Ubuntu", "Arial", true, "DejaVu Sans,Liberation Sans")]
        [InlineData("Liberation Sans,Ubuntu", "Arial", true, "Liberation Sans")]
        [InlineData("Ubuntu,Loma", "Arial", true, "")]
        [InlineData("FreeSans,DejaVu Sans,Liberation Sans", "NoSuchFont,Liberation Sans", true, "Liberation Sans,FreeSans,DejaVu Sans")]
        [InlineData("FreeSans,DejaVu Sans,Liberation Sans", "Arial,Liberation Sans", true, "Liberation Sans,FreeSans,DejaVu Sans")]
        [InlineData("Arial,Liberation Sans", "Arial", false, "Arial")]
        [InlineData("Liberation Sans", "Arial", false, "")]
        public void TheFaceChainFollowsTheInstalledFonts(string installed, string names, bool linux, string chain)
        {
            var have = installed.Split(',');
            bool Resolve(string name, out string family, out int style)
            {
                family = Array.IndexOf(have, name) >= 0 ? name : null;
                style = 0;
                return family != null;
            }
            var families = FontNative.OSFamilies(names.Split(','), linux, Resolve, f => Array.IndexOf(have, f) >= 0);
            Assert.Equal(chain, string.Join(",", families.Select(f => f.Family)));
        }

        [Fact]
        public void LineMetricsAreWholePixels()
        {
            var font = Builtin();
            var gen = new TextGenerator();
            var s9 = Settings(font, 9, 1f);
            Assert.Equal(10f, gen.GetPreferredHeight("Target type", s9));
            gen = Generate("Target type", s9);
            Assert.Equal((10, 0f), (gen.lines[0].height, gen.lines[0].leading));
            Assert.All(gen.verts, v => Assert.Equal(MathF.Round(v.position.y), v.position.y));
            // The baseline is 8 below the top: 'T' spans baseline + minY .. baseline + maxY.
            Assert.True(font.GetCharacterInfo('T', out var t, 9, FontStyle.Normal));
            Assert.Equal(-8f + t.maxY, gen.verts[0].position.y);
            Assert.Equal(-8f + t.minY, gen.verts[2].position.y);

            var s11 = Settings(font, 11, 1f);
            Assert.Equal(12f, new TextGenerator().GetPreferredHeight("Target type", s11));
            gen = Generate("Target type", s11);
            Assert.Equal((12, 1f), (gen.lines[0].height, gen.lines[0].leading));
            var wrapped = Settings(font, 11, 1f, width: 80f, wrap: HorizontalWrapMode.Wrap);
            gen = Generate("Choose a target type and start conversion.", wrapped);
            Assert.Equal(3, gen.lineCount);
            Assert.Equal(new[] { 0f, -13f, -26f }, gen.lines.Select(l => l.topY).ToArray());
            Assert.Equal(38f, new TextGenerator().GetPreferredHeight("Choose a target type and start conversion.", wrapped));
        }

        [Theory]
        // "Target type" (L 63), pivot (0, 1).
        [InlineData("Target type", 0f, 1f, TextAnchor.UpperCenter, 100f, 30f, 18f, null)]
        [InlineData("Target type", 0f, 1f, TextAnchor.UpperCenter, 100.875f, 30f, 18f, null)]
        [InlineData("Target type", 0f, 1f, TextAnchor.UpperCenter, 101f, 30f, 19f, null)]
        [InlineData("Target type", 0f, 1f, TextAnchor.UpperRight, 100.375f, 30f, 37f, null)]
        [InlineData("Target type", 0f, 1f, TextAnchor.UpperRight, 100.5f, 30f, 38f, null)]
        [InlineData("Target type", 0f, 1f, TextAnchor.LowerLeft, 100f, 30.375f, null, -15f)]
        [InlineData("Target type", 0f, 1f, TextAnchor.LowerLeft, 100f, 30.5f, null, -16f)]
        [InlineData("Target type", 0f, 1f, TextAnchor.MiddleLeft, 100f, 31.875f, null, -8f)]
        [InlineData("Target type", 0f, 1f, TextAnchor.MiddleLeft, 100f, 32f, null, -9f)]
        // Pivot (0.5, 0.5).
        [InlineData("Target type", 0.5f, 0.5f, TextAnchor.UpperLeft, 100.875f, 30f, -50f, null)]
        [InlineData("Target type", 0.5f, 0.5f, TextAnchor.UpperLeft, 101f, 30f, -51f, null)]
        [InlineData("Target type", 0.5f, 0.5f, TextAnchor.UpperCenter, 100f, 30f, -32f, null)]
        [InlineData("Target type", 0.5f, 0.5f, TextAnchor.UpperCenter, 100.5f, 30f, -32f, null)]
        [InlineData("Target type", 0.5f, 0.5f, TextAnchor.UpperCenter, 101.875f, 30f, -32f, null)]
        [InlineData("Target type", 0.5f, 0.5f, TextAnchor.MiddleLeft, 100f, 30f, null, 7f)]
        [InlineData("Target type", 0.5f, 0.5f, TextAnchor.MiddleLeft, 100f, 30.5f, null, 7f)]
        [InlineData("Target type", 0.5f, 0.5f, TextAnchor.MiddleLeft, 100f, 31.875f, null, 7f)]
        [InlineData("Target type", 0.5f, 0.5f, TextAnchor.UpperRight, 101f, 30f, -12f, null)]
        // Pivot (1, 0).
        [InlineData("Target type", 1f, 0f, TextAnchor.UpperCenter, 101f, 30f, -83f, null)]
        [InlineData("Target type", 1f, 0f, TextAnchor.UpperLeft, 100.5f, 30f, -101f, null)]
        // "Steam Vent" (L 67).
        [InlineData("Steam Vent", 0f, 1f, TextAnchor.UpperCenter, 100f, 30f, 16f, null)]
        [InlineData("Steam Vent", 1f, 0f, TextAnchor.UpperCenter, 101f, 30f, -85f, null)]
        public void AlignedLinesRoundTheirStartAndTop(string text, float pivotX, float pivotY, TextAnchor anchor, float w, float h, float? x, float? top)
        {
            var font = Builtin();
            var gen = Generate(text, Settings(font, 13, 1f, anchor, w, h, updateBounds: false, pivot: new Vector2(pivotX, pivotY)));
            if (x is { } startX) Assert.Equal(startX, gen.characters[0].cursorPos.x);
            if (top is { } topY) Assert.Equal(topY, gen.lines[0].topY);
            Assert.Equal(new Rect(0f, 0f, w + 0.0001f, h + 0.0001f), gen.rectExtents);
        }

        // With updateBounds the rect is the text's own size (the block's height; the widest line's
        // width unless the text wraps), placed by the pivot; the alignment rules then apply in it.
        [Theory]
        [InlineData(TextAnchor.UpperLeft, -31f)]
        [InlineData(TextAnchor.MiddleCenter, -32f)]
        [InlineData(TextAnchor.LowerRight, -31f)]
        [InlineData(TextAnchor.UpperRight, -31f)]
        public void UpdateBoundsMakesTheExtentsTheTextsOwn(TextAnchor anchor, float centredStart)
        {
            var font = Builtin();
            var gen = Generate("Target type", Settings(font, 13, 1f, anchor, 201.3f, 30f));
            Assert.Equal(new Vector2(0f, 0f), gen.characters[0].cursorPos);
            Assert.Equal(new Rect(0f, 0f, 63f, 15f), gen.rectExtents);
            gen = Generate("Target type", Settings(font, 13, 1f, anchor, 201.3f, 30f, pivot: new Vector2(0.5f, 0.5f)));
            Assert.Equal(centredStart, gen.characters[0].cursorPos.x);
            Assert.Equal(7f, gen.lines[0].topY);
            // Wrapping keeps the extents' width: "Target type" at 9 pixels in 201.3 × 0.86.
            gen = Generate("Target type", Settings(font, 11, 0.86f, TextAnchor.UpperLeft, 201.3f, 30f, HorizontalWrapMode.Wrap, pivot: new Vector2(0.5f, 0.5f)));
            Assert.Equal(-87f, gen.characters[0].cursorPos.x);
            Assert.Equal(201.3f * 0.86f + 0.0001f, gen.rectExtents.width, 4);
            Assert.Equal(10f, gen.rectExtents.height);
        }

        sealed class MeshSpy : BaseMeshEffect
        {
            public readonly List<Vector3> Positions = new();

            public override void ModifyMesh(VertexHelper vh)
            {
                Positions.Clear();
                var v = new UIVertex();
                for (int i = 0; i < vh.currentVertCount; i++)
                {
                    vh.PopulateUIVertex(ref v, i);
                    Positions.Add(v.position);
                }
            }
        }

        [Fact]
        public void TextOnAScaledCanvasLaysOutInPixels()
        {
            var font = Builtin();
            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.scaleFactor = 0.86f;
            canvas.pixelPerfect = false;
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(232f, 17f);
            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = 11;
            text.alignment = TextAnchor.MiddleCenter;
            var spy = go.AddComponent<MeshSpy>();
            text.text = "Target type";
            Frame();
            Canvas.ForceUpdateCanvases();
            var gen = text.cachedTextGenerator;
            Assert.Equal(-23f, gen.characters[0].cursorPos.x);
            Assert.Equal(52.32558f, text.preferredWidth, 4);
            Assert.Equal(gen.vertexCount, spy.Positions.Count);
            for (int i = 0; i < gen.vertexCount; i++) Assert.Equal(gen.verts[i].position.x / 0.86f, spy.Positions[i].x, 4);
        }
    }
}
