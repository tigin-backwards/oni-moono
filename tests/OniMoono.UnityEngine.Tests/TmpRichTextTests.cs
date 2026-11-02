using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Xunit;

namespace OniMoono.Tests
{
    // Rich text, style sheets, sprites and font assets.
    public class TmpRichTextTests : EngineTest
    {
        static TextMeshProUGUI Text(string text, float width = 500f, float height = 200f)
            => TmpTestSupport.NewText(text, TmpTestSupport.Graystroke, 14f, new Vector2(width, height));

        static string Characters(TMP_Text t) => new(t.textInfo.characterInfo.Take(t.textInfo.characterCount).Select(c => c.character).ToArray());

        [Fact]
        public void TagsAreRemovedAndApplied()
        {
            var t = Text("a<b>b</b><i>c</i><color=#FF000080>d</color><color=red>e</color><#00FF00>f</color>g");
            Assert.Equal("abcdefg", Characters(t));
            var c = t.textInfo.characterInfo;
            Assert.Equal(FontStyles.Bold, c[1].style & FontStyles.Bold);
            Assert.Equal(FontStyles.Normal, c[2].style & FontStyles.Bold);
            Assert.Equal(FontStyles.Italic, c[2].style & FontStyles.Italic);
            Assert.Equal(new Color32(255, 0, 0, 128), c[3].color);
            Assert.Equal(new Color32(255, 0, 0, 255), c[4].color);
            Assert.Equal(new Color32(0, 255, 0, 255), c[5].color);
            Assert.Equal(new Color32(255, 255, 255, 255), c[6].color);
            // Bold glyphs carry a negative SDF scale (the shader's weight switch).
            Assert.True(t.textInfo.meshInfo[0].uvs0[c[1].vertexIndex].w < 0f);
            Assert.True(t.textInfo.meshInfo[0].uvs0[c[0].vertexIndex].w > 0f);
        }

        static TextMeshProUGUI Noto(string text, float width)
        {
            var t = TmpTestSupport.NewText(text, TmpTestSupport.FindAsset<TMP_FontAsset>("NotoSans-Regular"), 14f, new Vector2(width, 60f));
            // As the probe set it up: the rect's top left at its pivot, so origins count from 0.
            ((RectTransform)t.transform).pivot = new Vector2(0f, 1f);
            t.alignment = TextAlignmentOptions.TopLeft;
            t.enableKerning = true;
            t.extraPadding = true;
            t.textWrappingMode = width < 400f ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.ForceMeshUpdate(true);
            return t;
        }

        static float[] Origins(TMP_Text t) => t.textInfo.characterInfo.Take(t.textInfo.characterCount).Select(c => c.origin).ToArray();

        /// An <indent> that opens part-way along a line moves the pen to the indent, forward or
        /// back, and later lines start there; its closing tag leaves the pen where it is. Measured
        /// in the original player (UnityProbe richtext, NotoSans-Regular 14 pt, 1 em = 14).
        [Fact]
        public void AnIndentPartWayAlongALineMovesThePen()
        {
            var t = Noto("ab<indent=3em>cd", 400f);
            Assert.Equal(new[] { 0f, 7.838542f, 42f, 48.70833f }, Origins(t), new FloatTolerance(0.001f));
            Assert.Equal(57.32f, t.preferredWidth, 2);
            t = Noto("abcdefghijkl<indent=2em>mn", 400f);
            Assert.Equal(28f, Origins(t)[12], 3);
            Assert.Equal(41.08854f, Origins(t)[13], 3);
            Assert.Equal(80.03f, t.preferredWidth, 2);
            t = Noto("ab<indent=40>cd</indent>ef", 400f);
            Assert.Equal(new[] { 40f, 46.70833f, 55.3125f, 63.22396f }, Origins(t).Skip(2).ToArray(), new FloatTolerance(0.001f));
            t = Noto("ab<indent=3em>cd\nef", 400f);
            Assert.Equal(42f, Origins(t)[5], 3);
            Assert.Equal(1, t.textInfo.characterInfo[5].lineNumber);
            // The bullets of a list: the text and its wrapped lines start at the indent.
            t = Noto("•<indent=1em>Big bases with Fast Track draw faster: each camera now looks only at renderers.</indent>", 300f);
            var c = t.textInfo.characterInfo;
            Assert.Equal(14f, c[1].origin, 3);
            int second = Enumerable.Range(0, t.textInfo.characterCount).First(i => c[i].lineNumber == 1);
            Assert.Equal(14f, c[second].origin, 3);
            Assert.Equal(539.15f, t.preferredWidth, 2);
            t = Noto("<indent=1em>–<indent=2em>A nested bullet that wraps onto a second line here.</indent></indent>", 300f);
            c = t.textInfo.characterInfo;
            Assert.Equal(14f, c[0].origin, 3);
            Assert.Equal(28f, c[1].origin, 3);
            second = Enumerable.Range(0, t.textInfo.characterCount).First(i => c[i].lineNumber == 1);
            Assert.Equal(28f, c[second].origin, 3);
        }

        sealed class FloatTolerance(float tolerance) : System.Collections.Generic.IEqualityComparer<float>
        {
            public bool Equals(float a, float b) => System.Math.Abs(a - b) <= tolerance;
            public int GetHashCode(float v) => 0;
        }

        [Fact]
        public void SourceIndicesSkipTheTags()
        {
            var t = Text("x<b>y</b>z");
            var c = t.textInfo.characterInfo;
            Assert.Equal(0, c[0].index);
            Assert.Equal(4, c[1].index);
            Assert.Equal(9, c[2].index);
        }

        [Fact]
        public void UnknownTagsAndNoParseAreDrawnAsText()
        {
            Assert.Equal("<foo>bar", Characters(Text("<foo>bar")));
            Assert.Equal("<b>x</b>", Characters(Text("<noparse><b>x</b></noparse>")));
            var plain = Text("<b>x</b>");
            plain.richText = false;
            plain.ForceMeshUpdate(true);
            Assert.Equal("<b>x</b>", Characters(plain));
        }

        [Fact]
        public void SizeTagsScaleTheCharacters()
        {
            var t = Text("a<size=28>b</size><size=50%>c</size><size=+7>d</size><size=2em>e</size>");
            var c = t.textInfo.characterInfo;
            Assert.Equal(14f, c[0].pointSize, 3);
            Assert.Equal(28f, c[1].pointSize, 3);
            Assert.Equal(7f, c[2].pointSize, 3);
            Assert.Equal(21f, c[3].pointSize, 3);
            Assert.Equal(28f, c[4].pointSize, 3);
        }

        [Fact]
        public void CaseTagsConvertTheCharacters()
        {
            Assert.Equal("ABc", Characters(Text("<uppercase>ab</uppercase>c")));
            Assert.Equal("abC", Characters(Text("<lowercase>AB</lowercase>C")));
            var t = Text("<smallcaps>aB</smallcaps>");
            Assert.Equal("AB", Characters(t));
            // The small capitals are drawn at 80%; the characters keep the text's size.
            Assert.Equal(14f, t.textInfo.characterInfo[0].pointSize, 3);
            Assert.Equal(t.textInfo.characterInfo[1].scale * 0.8f, t.textInfo.characterInfo[0].scale, 5);
        }

        [Fact]
        public void EscapesAndLineBreaks()
        {
            var t = Text("a\\nb<br>c\\u0041");
            Assert.Equal("a\nb\ncA", Characters(t));
            Assert.Equal(3, t.textInfo.lineCount);
            t.parseCtrlCharacters = false;
            t.ForceMeshUpdate(true);
            Assert.StartsWith("a\\nb", Characters(t));
        }

        [Fact]
        public void SurrogatePairsAreOneCharacter()
        {
            var t = Text("a\U0001F60Ab");
            Assert.Equal(3, t.textInfo.characterCount);
            Assert.Equal(3, t.textInfo.characterInfo[2].index);
        }

        [Fact]
        public void LinksRecordTheirIdAndText()
        {
            var t = Text("see <link=\"WATER\">Water</link> and <link=OXYGEN>Oxygen</link>");
            var info = t.textInfo;
            Assert.Equal(2, info.linkCount);
            Assert.Equal("WATER", info.linkInfo[0].GetLinkID());
            Assert.Equal("Water", info.linkInfo[0].GetLinkText());
            Assert.Equal("OXYGEN", info.linkInfo[1].GetLinkID());
            Assert.Equal("Oxygen", info.linkInfo[1].GetLinkText());
            Assert.Equal(4, info.linkInfo[0].linkTextfirstCharacterIndex);
        }

        [Fact]
        public void StylesInsertTheirDefinitions()
        {
            TmpTestSupport.LoadGameData();
            var sheet = TMP_StyleSheet.instance;
            Assert.NotNull(sheet);
            // The game's sheet: "H1" opens <size=2em><b><#40ff80>.
            Assert.Equal("<size=2em><b><#40ff80>", sheet.GetStyle("H1").styleOpeningDefinition);
            var t = Text("<style=\"H1\">Big</style> small");
            var c = t.textInfo.characterInfo;
            Assert.Equal("Big small", Characters(t));
            Assert.Equal(28f, c[0].pointSize, 3);
            Assert.Equal(FontStyles.Bold, c[0].style & FontStyles.Bold);
            Assert.Equal(new Color32(0x40, 0xff, 0x80, 255), c[0].color);
            Assert.Equal(14f, c[4].pointSize, 3);

            // Styles added at run time, as the game adds them.
            sheet.AddStyle(new TMP_Style("zztest", "<color=#0000FF>", "</color>"));
            sheet.RefreshStyles();
            var added = Text("<style=zztest>x</style>y");
            Assert.Equal(new Color32(0, 0, 255, 255), added.textInfo.characterInfo[0].color);
            Assert.Equal(new Color32(255, 255, 255, 255), added.textInfo.characterInfo[1].color);
            Assert.Same(sheet.GetStyle("zztest"), sheet.GetStyle(sheet.GetStyle("zztest").hashCode));
        }

        [Fact]
        public void SpritesComeFromTheSpriteAsset()
        {
            var sprites = TmpTestSupport.FindAsset<TMP_SpriteAsset>("oni_sprite_assets");
            var t = Text("a<sprite=\"oni_sprite_assets\" index=1>b");
            t.spriteAsset = sprites;
            t.ForceMeshUpdate(true);
            var info = t.textInfo;
            Assert.Equal(3, info.characterCount);
            Assert.Equal(TMP_TextElementType.Sprite, info.characterInfo[1].elementType);
            // The sprite is drawn by a sub-mesh with the sprite asset's material.
            Assert.Equal(2, info.materialCount);
            Assert.Equal(1, info.characterInfo[1].materialReferenceIndex);
            Assert.Same(sprites.material, info.meshInfo[1].material);
            Assert.Equal(4, info.meshInfo[1].vertexCount);
            var sub = t.GetComponentInChildren<TMP_SubMeshUI>(true);
            Assert.NotNull(sub);
            Assert.Same(sprites.material, sub.sharedMaterial);
            // Untinted sprites are white.
            Assert.Equal(new Color32(255, 255, 255, 255), info.meshInfo[1].colors32[0]);
        }

        [Fact]
        public void MissingCharactersUseFallbacksThenTheMissingGlyph()
        {
            TmpTestSupport.LoadGameData();
            // "ж" is not in GRAYSTROKE: a global fallback (Noto Sans) draws it in a sub-mesh.
            var t = Text("aж");
            var info = t.textInfo;
            Assert.Equal(2, info.materialCount);
            Assert.NotSame(t.font, info.characterInfo[1].fontAsset);
            Assert.True(info.characterInfo[1].fontAsset.HasCharacter('ж'));
        }

        [Fact]
        public void DynamicFontAssetsAddCharactersThroughTheFontEngine()
        {
            var font = TmpTestSupport.Load<Font>("sharedassets0.assets", "LiberationSans");
            var asset = TMP_FontAsset.CreateFontAsset(font, 48, 5, GlyphRenderMode.SDFAA, 256, 256, AtlasPopulationMode.Dynamic, true);
            Assert.NotNull(asset);
            Assert.Equal(AtlasPopulationMode.Dynamic, asset.atlasPopulationMode);
            Assert.Equal(TextureFormat.Alpha8, asset.atlasTexture.format);
            Assert.Equal("TextMeshPro/Mobile/Distance Field", asset.material.shader.name);
            Assert.Equal(6f, asset.material.GetFloat(ShaderUtilities.ID_GradientScale));
            Assert.False(asset.HasCharacter('Q'));
            Assert.True(asset.TryAddCharacters("QuiT", out string missing));
            Assert.Equal("", missing);
            Assert.True(asset.HasCharacter('Q'));
            var q = asset.characterLookupTable['Q'];
            Assert.True(q.glyph.glyphRect.width > 0);
            Assert.Equal(48f, asset.faceInfo.pointSize);
            // Text in a dynamic asset adds its characters on demand.
            var t = TmpTestSupport.NewText("Wxyz", asset, 20f, new Vector2(300f, 50f));
            Assert.Equal(4, t.textInfo.characterCount);
            Assert.True(asset.HasCharacter('W'));
            Assert.All(t.textInfo.characterInfo.Take(4), c => Assert.Same(asset, c.fontAsset));
        }

        [Fact]
        public void GlyphsInLaterAtlasesDrawFromTheirOwnAtlas()
        {
            // A 64 × 64 atlas holds one 48 pt glyph (with its padding): with multi-atlas support
            // every further glyph opens a new atlas.
            var font = TmpTestSupport.Load<Font>("sharedassets0.assets", "LiberationSans");
            var asset = TMP_FontAsset.CreateFontAsset(font, 48, 5, GlyphRenderMode.SDFAA, 64, 64, AtlasPopulationMode.Dynamic, true);
            var t = TmpTestSupport.NewText("ABCDEF", asset, 20f, new Vector2(300f, 50f));
            var info = t.textInfo;
            Assert.Equal(6, info.characterCount);
            Assert.True(asset.atlasTextureCount >= 6);
            var atlases = info.characterInfo.Take(6).Select(c => c.textElement.glyph.atlasIndex).ToArray();
            Assert.Equal(6, atlases.Distinct().Count());
            // One material (and sub-mesh) per atlas, whose main texture is that atlas; the text's
            // own material is the first atlas's.
            Assert.Equal(atlases.Distinct().Append(0).Distinct().Count(), info.materialCount);
            for (int i = 0; i < 6; i++)
            {
                var c = info.characterInfo[i];
                var atlas = asset.atlasTextures[atlases[i]];
                Assert.Same(atlas, c.material.mainTexture);
                Assert.Same(atlas, info.meshInfo[c.materialReferenceIndex].material.mainTexture);
                Assert.Equal(4, info.meshInfo[c.materialReferenceIndex].vertexCount);
                Assert.Equal(atlases[i] == 0, c.materialReferenceIndex == 0);
            }
            var subTextures = t.GetComponentsInChildren<TMP_SubMeshUI>(true).Select(s => s.sharedMaterial.mainTexture).ToArray();
            foreach (int a in atlases.Where(a => a > 0)) Assert.Contains(asset.atlasTextures[a], subTextures);
        }

        // Sets a serialized field of the text (as the game's data does when it loads).
        static void SetSerialized(TMP_Text text, string name, object value)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            for (var t = text.GetType(); t != null; t = t.BaseType)
                if (t.GetField(name, flags) is { } f) { f.SetValue(text, value); return; }
            throw new System.MissingFieldException(name);
        }

        static TextMeshProUGUI Loaded(TMP_FontAsset font, Material material)
        {
            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("Text", typeof(RectTransform));
            go.SetActive(false);
            go.transform.SetParent(canvasGo.transform, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            SetSerialized(t, "m_fontAsset", font);
            SetSerialized(t, "m_sharedMaterial", material);
            SetSerialized(t, "m_text", "Stress");
            go.SetActive(true);
            t.ForceMeshUpdate(true);
            return t;
        }

        [Fact]
        public void ASavedMaterialOfAnotherAtlasGivesWayToTheFontsMaterial()
        {
            // The Duplicant cards' expectation lines are saved with one font and another font's
            // material: the shipped package draws them with their font's material.
            var noto = TmpTestSupport.FindAsset<TMP_FontAsset>("NotoSans-Regular");
            var roboto = TmpTestSupport.FindAsset<TMP_FontAsset>("RobotoCondensed-Regular");
            var t = Loaded(noto, roboto.material);
            Assert.Same(noto.material, t.fontSharedMaterial);
            Assert.Same(noto.atlasTexture, t.textInfo.meshInfo[0].material.mainTexture);
            Canvas.ForceUpdateCanvases();
            Assert.Same(noto.atlasTexture, t.canvasRenderer.GetMaterial(0).mainTexture);
            t = Loaded(roboto, noto.material);
            Assert.Same(roboto.material, t.fontSharedMaterial);

            // A saved material on the font's atlas is kept, whichever material it is.
            var onAtlas = new Material(roboto.material) { name = "On Noto's atlas" };
            onAtlas.SetTexture(ShaderUtilities.ID_MainTex, noto.atlasTexture);
            Assert.Same(onAtlas, Loaded(noto, onAtlas).fontSharedMaterial);
            // So is a material set at run time, whatever its atlas.
            var set = TmpTestSupport.NewText("Stress", noto, 14f, new Vector2(300f, 50f));
            set.fontSharedMaterial = roboto.material;
            set.ForceMeshUpdate(true);
            Assert.Same(roboto.material, set.fontSharedMaterial);
        }

        [Fact]
        public void ShaderRatiosFollowTheMaterialEffects()
        {
            // The game's assets carry the ratios their materials' effects need: recomputing
            // them gives the stored values.
            var noto = TmpTestSupport.FindAsset<TMP_FontAsset>("NotoSans-Regular");
            var material = new Material(noto.material);
            float a = material.GetFloat(ShaderUtilities.ID_ScaleRatio_A), b = material.GetFloat(ShaderUtilities.ID_ScaleRatio_B), c = material.GetFloat(ShaderUtilities.ID_ScaleRatio_C);
            ShaderUtilities.UpdateShaderRatios(material);
            Assert.Equal(a, material.GetFloat(ShaderUtilities.ID_ScaleRatio_A), 4);
            Assert.Equal(b, material.GetFloat(ShaderUtilities.ID_ScaleRatio_B), 4);
            Assert.Equal(c, material.GetFloat(ShaderUtilities.ID_ScaleRatio_C), 4);
            // More outline: a smaller face ratio.
            material.SetFloat(ShaderUtilities.ID_OutlineWidth, 1f);
            ShaderUtilities.UpdateShaderRatios(material);
            Assert.True(material.GetFloat(ShaderUtilities.ID_ScaleRatio_A) < a);
        }

        [Fact]
        public void FormattedSetTextReplacesPlaceholders()
        {
            var t = Text("");
            t.SetText("{0} of {1:2}", 3f, 1.23456f);
            Assert.Equal("3 of 1.23", t.text);
            t.SetText("{0:1}%", 99.96f);
            Assert.Equal("100.0%", t.text);
        }

        [Fact]
        public void TextChangedEventFollowsEachLayout()
        {
            Object changed = null;
            System.Action<Object> listener = o => changed = o;
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(listener);
            try
            {
                var t = Text("abc");
                Assert.Same(t, changed);
                Assert.False(t.havePropertiesChanged);
                t.text = "abcd";
                Assert.True(t.havePropertiesChanged);
            }
            finally { TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(listener); }
        }

        [Fact]
        public void HitTestsFindCharactersWordsLinesAndLinks()
        {
            var t = Text("one <link=\"L\">two</link>\nthree", width: 400f, height: 200f);
            t.alignment = TextAlignmentOptions.TopLeft;
            t.ForceMeshUpdate(true);
            var info = t.textInfo;
            // The overlay canvas maps screen points to its local space (no camera).
            Vector3 ScreenOf(Vector3 local) => t.transform.TransformPoint(local);
            var w = info.characterInfo[5];
            var inside = ScreenOf((w.bottomLeft + w.topRight) / 2f);
            Assert.Equal(5, TMP_TextUtilities.FindIntersectingCharacter(t, inside, null, true));
            Assert.Equal(1, TMP_TextUtilities.FindIntersectingWord(t, inside, null));
            Assert.Equal(0, TMP_TextUtilities.FindIntersectingLine(t, inside, null));
            Assert.Equal(0, TMP_TextUtilities.FindIntersectingLink(t, inside, null));
            var first = info.characterInfo[0];
            var onOne = ScreenOf((first.bottomLeft + first.topRight) / 2f);
            Assert.Equal(-1, TMP_TextUtilities.FindIntersectingLink(t, onOne, null));
            var third = info.characterInfo[info.lineInfo[1].firstCharacterIndex];
            Assert.Equal(1, TMP_TextUtilities.FindIntersectingLine(t, ScreenOf((third.bottomLeft + third.topRight) / 2f), null));
            Assert.Equal(-1, TMP_TextUtilities.FindIntersectingCharacter(t, ScreenOf(new Vector3(190f, -90f)), null, true));
            Assert.True(TMP_TextUtilities.IsIntersectingRectTransform(t.rectTransform, ScreenOf(Vector3.zero), null));
            Assert.False(TMP_TextUtilities.IsIntersectingRectTransform(t.rectTransform, ScreenOf(new Vector3(500f, 0f)), null));
        }

        [Fact]
        public void SpacingIsInHundredthsOfTheTextsFontSize()
        {
            // <size> does not change what the spacing properties and bold spacing measure in.
            var plain = Text("xx");
            plain.characterSpacing = 10f;
            plain.ForceMeshUpdate();
            var sized = Text("<size=+10>xx");
            sized.characterSpacing = 10f;
            sized.ForceMeshUpdate();
            float spacing = 10f * 0.01f * plain.fontSize;
            float glyph = plain.textInfo.characterInfo[1].origin - plain.textInfo.characterInfo[0].origin - spacing;
            float scale = (plain.fontSize + 10f) / plain.fontSize;
            Assert.Equal(glyph * scale + spacing, sized.textInfo.characterInfo[1].origin - sized.textInfo.characterInfo[0].origin, 3);
        }

        [Fact]
        public void WithExtraPaddingABoldQuadIsAsWideAsARegularOne()
        {
            var t = Text("x<b>x</b>");
            t.extraPadding = true;
            t.ForceMeshUpdate();
            var v = t.textInfo.meshInfo[0].vertices;
            var uv = t.textInfo.meshInfo[0].uvs0;
            Assert.Equal(v[2].x - v[0].x, v[6].x - v[4].x, 3);
            Assert.True(v[5].y - v[4].y < v[1].y - v[0].y);
            Assert.Equal(uv[2].x - uv[0].x, uv[6].x - uv[4].x, 5);
        }

        [Fact]
        public void ATextThatNeverLaidOutHasOneMaterial()
        {
            var go = new GameObject("Text", typeof(RectTransform));
            var t = go.AddComponent<TextMeshProUGUI>();
            Assert.Equal(1, t.textInfo.materialCount);
            Assert.Equal(0, t.textInfo.characterCount);
        }

        [Fact]
        public void ItalicGlyphsLeanAroundHalfTheCapHeight()
        {
            var upright = Text("a");
            var italic = Text("<i>a</i>");
            var u = upright.textInfo.meshInfo[0].vertices;
            var v = italic.textInfo.meshInfo[0].vertices;
            var c = italic.textInfo.characterInfo[0];
            float shear = (v[1].x - v[0].x) / (v[1].y - v[0].y);
            float pivot = c.baseLine + italic.font.faceInfo.capLine * 0.5f * (14f / italic.font.faceInfo.pointSize * italic.font.faceInfo.scale);
            Assert.Equal(u[0].x + shear * (v[0].y - pivot), v[0].x, 3);
            Assert.Equal(u[1].x + shear * (v[1].y - pivot), v[1].x, 3);
        }
    }
}
