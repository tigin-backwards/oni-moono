using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Engine.Assets;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    public class TextTests : EngineTest
    {
        static string DataDir
        {
            get
            {
                var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762107");
                var dir = Path.Combine(game, "OxygenNotIncluded_Data");
                if (!Directory.Exists(dir)) throw new InvalidOperationException($"Game data not found at {dir}; set ONI_GAME_DIR.");
                return dir;
            }
        }

        /// The asset named `name` in `file` (path ids change between game builds).
        static T Load<T>(string file, string name) where T : Object
        {
            AssetSystem.Initialize(DataDir);
            return (T)ObjectLoader.Load(AssetSystem.Get(file), TmpTestSupport.FindId(file, name, typeof(T)));
        }

        // sharedassets0.assets: the game's Liberation Sans font (TMP's dynamic fallback source).
        static Font LiberationSans => Load<Font>("sharedassets0.assets", "LiberationSans");

        [Fact]
        public void EveryGameFontLoads()
        {
            AssetSystem.Initialize(DataDir);
            int fonts = 0;
            foreach (var name in new[] { "resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets", "Resources/unity default resources" })
            {
                var file = AssetSystem.Get(name);
                foreach (var info in file.Objects)
                {
                    if (file.TypeOf(info).ClassId != 128) continue;
                    var font = (Font)ObjectLoader.Load(file, info.PathId);
                    fonts++;
                    Assert.True(font.dynamic, font.name);
                    Assert.True(font.fontSize > 0, font.name);
                    Assert.NotNull(font.material);
                    Assert.True(font.HasCharacter('A'), font.name);
                }
            }
            Assert.Equal(15, fonts);
        }

        [Fact]
        public void LegacyRuntimeIsTheBuiltinFont()
        {
            AssetSystem.Initialize(DataDir);
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Assert.NotNull(font);
            Assert.True(font.dynamic);
            Assert.Equal(13, font.fontSize);
            Assert.Equal(new[] { "Arial" }, font.fontNames);
            Assert.True(font.HasCharacter('g'));
            // A font without data has every character (measured: U+4E2D, which no face of its
            // glyph chain has).
            Assert.True(font.HasCharacter((char)0x4E2D));
            Assert.Equal(FontEngineError.Success, FontEngine.LoadFontFace(font, 13f, 0));
            Assert.Equal("Liberation Sans", FontEngine.GetFaceInfo().familyName);
        }

        // Mods built for older Unity ask for the built-in font by its old name (Geyser Swapper,
        // Antigravity, RomenH's buildings…); the original player gives them LegacyRuntime.
        [Fact]
        public void ArialIsTheLegacyRuntimeFont()
        {
            AssetSystem.Initialize(DataDir);
            var font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            Assert.NotNull(font);
            Assert.Same(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"), font);
            Assert.Same(font, Resources.GetBuiltinResource(typeof(Font), "Arial.ttf"));
        }

        // Measured in the original: Geyser Swapper's 10-point status Text, on the game's canvas
        // scaled 0.86, gives its layout group (16 units of padding) a preferred width of 322.98:
        // the line at 8 pixels (264), not 9 (331), divided by the scale.
        [Fact]
        public void ScaledDynamicFontSizeIsTruncated()
        {
            AssetSystem.Initialize(DataDir);
            var font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            const string status = "Stage: Analyze required\nStudy this geyser first - sealing and conversion will not start until it is analyzed.";
            float Width(int size, float scale) => new TextGenerator().GetPreferredWidth(status, new TextGenerationSettings
            {
                font = font, fontSize = size, scaleFactor = scale, lineSpacing = 1, richText = true, color = Color.white,
                pivot = new Vector2(0.5f, 0.5f), textAnchor = TextAnchor.MiddleLeft, horizontalOverflow = HorizontalWrapMode.Wrap,
            });
            Assert.Equal(264f, Width(8, 1f));
            Assert.Equal(264f, Width(10, 0.86f));
            Assert.Equal(322.98f, Width(10, 0.86f) / 0.86f + 16f, 2);
        }

        [Fact]
        public void FaceInfoAndGlyphMetrics()
        {
            var font = LiberationSans;
            Assert.Equal(FontEngineError.Success, FontEngine.LoadFontFace(font, 90f, 0));
            var info = FontEngine.GetFaceInfo();
            Assert.Equal("Liberation Sans", info.familyName);
            Assert.Equal("Regular", info.styleName);
            Assert.Equal(90f, info.pointSize);
            Assert.Equal(2048, info.unitsPerEM);
            // Liberation Sans: ascender 1854, descender -434 (hhea), cap height 1409.
            Assert.Equal(1854f * 90f / 2048f, info.ascentLine, 3);
            Assert.Equal(-434f * 90f / 2048f, info.descentLine, 3);
            // Cap and mean lines are the hinted 'H' and 'x' tops, the tab width the hinted space's
            // advance: whole pixels, as in Unity's player.
            Assert.Equal(62f, info.capLine, 3);
            Assert.Equal(48f, info.meanLine, 3);
            Assert.Equal(25f, info.tabWidth, 3);
            // The game's LiberationSans SDF, made by Unity at 86: cap 59, mean 45, tab 24.
            Assert.Equal(FontEngineError.Success, FontEngine.LoadFontFace(font, 86f, 0));
            var at86 = FontEngine.GetFaceInfo();
            Assert.Equal(59f, at86.capLine, 3);
            Assert.Equal(45f, at86.meanLine, 3);
            Assert.Equal(24f, at86.tabWidth, 3);
            Assert.Equal(FontEngineError.Success, FontEngine.LoadFontFace(font, 90f, 0));
            Assert.True(info.lineHeight > info.ascentLine - info.descentLine - 0.01f);

            uint a = FontEngine.GetGlyphIndex('A');
            Assert.NotEqual(0u, a);
            Assert.Equal(0u, FontEngine.GetGlyphIndex(0xE000));
            Assert.True(FontEngine.TryGetGlyphWithUnicodeValue('A', GlyphLoadFlags.LOAD_NO_HINTING | GlyphLoadFlags.LOAD_NO_BITMAP, out var glyph));
            Assert.Equal(a, glyph.index);
            // 'A' advance: 1366 units.
            Assert.Equal(1366f * 90f / 2048f, glyph.metrics.horizontalAdvance, 2);
            Assert.True(glyph.metrics.height > 60f && glyph.metrics.height < 66f);
            Assert.True(FontEngine.TryGetGlyphWithIndexValue(a, GlyphLoadFlags.LOAD_NO_SCALE, out var unscaled));
            Assert.Equal(1366f, unscaled.metrics.horizontalAdvance);
            Assert.False(FontEngine.TryGetGlyphWithUnicodeValue(0xE000, GlyphLoadFlags.LOAD_DEFAULT, out _));

            var faces = FontEngine.GetFontFaces();
            Assert.Equal(new[] { "Liberation Sans - Regular" }, faces);
        }

        [Fact]
        public void SdfAtlasPacksGlyphsWithPadding()
        {
            var font = LiberationSans;
            Assert.Equal(FontEngineError.Success, FontEngine.LoadFontFace(font, 48f, 0));
            const int size = 256, padding = 5;
            var atlas = new Texture2D(size, size, TextureFormat.Alpha8, false);
            FontEngine.ResetAtlasTexture(atlas);
            var free = new List<GlyphRect> { new GlyphRect(0, 0, size - 1, size - 1) };
            var used = new List<GlyphRect>();
            var indices = "ABCDEFGHIJ abcdefghij0123".Select(c => FontEngine.GetGlyphIndex(c)).Distinct().ToList();
            Assert.True(FontEngine.TryAddGlyphsToTexture(indices, padding, GlyphPackingMode.BestShortSideFit, free, used, GlyphRenderMode.SDFAA, atlas, out var glyphs));
            var added = glyphs.TakeWhile(g => g != null).ToList();
            Assert.Equal(indices.Count, added.Count);
            Assert.Null(glyphs[added.Count]);

            var pixels = atlas.GetRawTextureData();
            var padded = new List<RectInt>();
            foreach (var g in added)
            {
                var r = g.glyphRect;
                if (g.index == FontEngine.GetGlyphIndex(' '))
                {
                    Assert.Equal(GlyphRect.zero, r);
                    Assert.True(g.metrics.horizontalAdvance > 0f);
                    continue;
                }
                // The glyph and its padding lie inside the atlas.
                Assert.True(r.x - padding >= 0 && r.y - padding >= 0);
                Assert.True(r.x + r.width + padding <= size - 1 && r.y + r.height + padding <= size - 1);
                Assert.Equal(r.width, (int)g.metrics.width);
                Assert.Equal(r.height, (int)g.metrics.height);
                padded.Add(new RectInt(r.x - padding, r.y - padding, r.width + 2 * padding, r.height + 2 * padding));

                // Field: background (alpha 0) at the padded rectangle's corner, ~0.5 + up to
                // padding/(2(padding+1)) inside the glyph; the outline is where it crosses 0.5.
                int corner = pixels[(r.y - padding) * size + (r.x - padding)];
                Assert.True(corner < 20, $"corner {corner}");
                int max = 0;
                for (int y = r.y; y < r.y + r.height; y++)
                    for (int x = r.x; x < r.x + r.width; x++)
                        max = Math.Max(max, pixels[y * size + x]);
                Assert.True(max > 140, $"glyph {g.index} max {max}");
            }
            // Packed rectangles never overlap and are what the used list records.
            for (int i = 0; i < padded.Count; i++)
                for (int j = i + 1; j < padded.Count; j++)
                    Assert.False(padded[i].Overlaps(padded[j]));
            Assert.Equal(padded.Count, used.Count);

            // A single glyph that cannot fit fails and leaves the lists alone.
            var tinyFree = new List<GlyphRect> { new GlyphRect(0, 0, 8, 8) };
            var tinyUsed = new List<GlyphRect>();
            Assert.False(FontEngine.TryAddGlyphToTexture(FontEngine.GetGlyphIndex('W'), padding, GlyphPackingMode.BestShortSideFit, tinyFree, tinyUsed, GlyphRenderMode.SDFAA, atlas, out var none));
            Assert.Null(none);
            Assert.Single(tinyFree);
            Assert.Empty(tinyUsed);
        }

        [Fact]
        public void SdfGradientMatchesTheShaderGradientScale()
        {
            // Across a vertical stem edge the field changes by 1/(2·(padding+1)) per texel.
            var font = LiberationSans;
            Assert.Equal(FontEngineError.Success, FontEngine.LoadFontFace(font, 90f, 0));
            const int size = 256, padding = 9;
            var atlas = new Texture2D(size, size, TextureFormat.Alpha8, false);
            FontEngine.ResetAtlasTexture(atlas);
            var free = new List<GlyphRect> { new GlyphRect(0, 0, size - 1, size - 1) };
            var used = new List<GlyphRect>();
            Assert.True(FontEngine.TryAddGlyphToTexture(FontEngine.GetGlyphIndex('l'), padding, GlyphPackingMode.BestShortSideFit, free, used, GlyphRenderMode.SDFAA, atlas, out var glyph));
            var pixels = atlas.GetRawTextureData();
            var r = glyph.glyphRect;
            int row = r.y + r.height / 2;
            var values = Enumerable.Range(r.x - padding, r.width + 2 * padding).Select(x => (int)pixels[row * size + x]).ToArray();
            // Steepest rise between background and inside.
            int rise = 0;
            for (int i = 1; i < values.Length / 2; i++) rise = Math.Max(rise, values[i] - values[i - 1]);
            float expected = 255f / (2f * (padding + 1));
            Assert.InRange(rise, expected - 3f, expected + 3f);
        }

        [Fact]
        public void PairAdjustmentsComeFromGposKern()
        {
            var font = LiberationSans;
            Assert.Equal(FontEngineError.Success, FontEngine.LoadFontFace(font, 90f, 0));
            uint a = FontEngine.GetGlyphIndex('A'), v = FontEngine.GetGlyphIndex('V');
            var all = FontEngine.GetAllPairAdjustmentRecords();
            var legacy = FontEngine.GetGlyphPairAdjustmentTable(new[] { a, v });
            bool gpos = all != null && all.Any(p => p.firstAdjustmentRecord.glyphIndex == a && p.secondAdjustmentRecord.glyphIndex == v);
            bool kern = legacy != null && legacy.Any(p => p.firstAdjustmentRecord.glyphIndex == a && p.secondAdjustmentRecord.glyphIndex == v);
            Assert.True(gpos || kern);
            if (gpos)
            {
                var some = FontEngine.GetPairAdjustmentRecords(new List<uint> { a, v });
                Assert.NotNull(some);
                Assert.Contains(some, p => p.firstAdjustmentRecord.glyphIndex == a && p.secondAdjustmentRecord.glyphIndex == v && p.firstAdjustmentRecord.glyphValueRecord.xAdvance < 0f);
                // Terminated by an empty record.
                Assert.Equal(0u, some[^1].firstAdjustmentRecord.glyphIndex);
            }
        }

        [Fact]
        public void SystemFontsAndLigatures()
        {
            var paths = Font.GetPathsToOSFonts();
            var dejavu = paths.FirstOrDefault(p => Path.GetFileName(p) == "DejaVuSans.ttf");
            if (dejavu == null) Assert.Skip("DejaVu Sans is not installed.");
            Assert.True(FontEngine.TryGetSystemFontReference("dejavu sans", "BOOK", out var reference));
            Assert.Equal(dejavu, reference.filePath);
            // As in Unity's player: no "Regular" face, and none is found by that name; an empty or
            // null style still finds one of the family's faces.
            Assert.False(FontEngine.TryGetSystemFontReference("DejaVu Sans", "Regular", out _));
            Assert.True(FontEngine.TryGetSystemFontReference("DejaVu Sans", "", out var any));
            Assert.Equal("DejaVu Sans", any.familyName);
            Assert.True(FontEngine.TryGetSystemFontReference("DejaVu Sans", null, out any));
            Assert.Equal("DejaVu Sans", any.familyName);
            Assert.False(FontEngine.TryGetSystemFontReference("Segoe UI Symbol (not installed)", "", out _));
            Assert.Equal(FontEngineError.Success, FontEngine.LoadFontFace("DejaVu Sans", "Book", 32f));
            Assert.Equal(FontEngineError.Invalid_File_Path, FontEngine.LoadFontFace("/nonexistent/font.ttf"));
            Assert.Equal(FontEngineError.Success, FontEngine.LoadFontFace(dejavu, 32f, 0));
            Assert.Equal(new[] { "DejaVu Sans - Book" }, FontEngine.GetFontFaces());
            uint f = FontEngine.GetGlyphIndex('f'), i = FontEngine.GetGlyphIndex('i');
            var records = FontEngine.GetLigatureSubstitutionRecords(f);
            Assert.NotNull(records);
            Assert.Contains(records, r => r.componentGlyphIDs != null && r.componentGlyphIDs.SequenceEqual(new[] { f, i }) && r.ligatureGlyphID != 0);
            Assert.Null(records[^1].componentGlyphIDs);
        }

        static TextGenerationSettings Settings(Font font, Vector2 extents, TextAnchor anchor = TextAnchor.UpperLeft) => new()
        {
            font = font,
            color = Color.white,
            fontSize = 14,
            lineSpacing = 1f,
            richText = true,
            scaleFactor = 1f,
            fontStyle = FontStyle.Normal,
            textAnchor = anchor,
            horizontalOverflow = HorizontalWrapMode.Wrap,
            verticalOverflow = VerticalWrapMode.Overflow,
            generationExtents = extents,
            pivot = new Vector2(0.5f, 0.5f),
        };

        [Fact]
        public void TextGeneratorWrapsAndAligns()
        {
            var font = LiberationSans;
            var gen = new TextGenerator();
            const string text = "The quick brown fox jumps over the lazy dog";
            Assert.True(gen.PopulateWithErrors(text, Settings(font, new Vector2(100f, 200f)), null));
            Assert.True(gen.lineCount > 2);
            Assert.Equal(text.Length + 1, gen.characterCount);
            Assert.Equal(text.Length, gen.characterCountVisible);
            int visible = text.Count(c => c != ' ');
            Assert.Equal(visible * 4, gen.verts.Count);
            // Upper left: lines start at the rect's left edge, the first at its top.
            Assert.Equal(-50f, gen.characters[0].cursorPos.x, 3);
            Assert.Equal(100f, gen.lines[0].topY, 3);
            Assert.Equal(0, gen.lines[0].startCharIdx);
            foreach (var v in gen.verts) Assert.True(v.position.x <= 52f, $"vertex at {v.position.x}");
            // Lines break after spaces, and move down by the line height.
            for (int i = 1; i < gen.lineCount; i++)
            {
                Assert.Equal(' ', text[gen.lines[i].startCharIdx - 1]);
                Assert.True(gen.lines[i].topY < gen.lines[i - 1].topY);
            }

            // Right alignment puts each line's last glyph against the right edge.
            Assert.True(gen.Populate(text, Settings(font, new Vector2(100f, 200f), TextAnchor.UpperRight)));
            float right = gen.verts.Max(v => v.position.x);
            // (Ink may extend a pixel past the last advance.)
            Assert.InRange(right, 45f, 52f);

            // Preferred sizes: one line without wrapping, whole pixels; its height the font's
            // ascent and descent (18.398 at its size 16) at 14 pixels, each rounded.
            float width = gen.GetPreferredWidth("Hello", Settings(font, Vector2.zero));
            Assert.InRange(width, 25f, 40f);
            Assert.Equal(MathF.Round(width), width);
            float height = gen.GetPreferredHeight("Hello", Settings(font, new Vector2(500f, 0f)));
            Assert.Equal(16f, height);
        }

        [Fact]
        public void TextGeneratorCachesUntilTheInputChanges()
        {
            var font = LiberationSans;
            var gen = new TextGenerator(4);
            var settings = Settings(font, new Vector2(300f, 100f));
            Assert.True(gen.Populate("Cached", settings));
            var first = gen.GetVerticesArray();
            Assert.Equal(first.Length, gen.vertexCount);
            Assert.Equal(14, gen.fontSizeUsedForBestFit);
            Assert.True(gen.Populate("Cached", settings));
            Assert.Equal(first, gen.GetVerticesArray());
            Assert.True(gen.Populate("Changed!", settings));
            Assert.NotEqual(first.Length, gen.vertexCount);
            var copies = new List<UILineInfo>();
            gen.GetLines(copies);
            Assert.Equal(gen.lineCount, copies.Count);

            // Best fit picks the largest size that fits, within the limits; lines below the
            // rect count only when they are truncated.
            settings.resizeTextForBestFit = true;
            settings.resizeTextMinSize = 4;
            settings.resizeTextMaxSize = 60;
            settings.generationExtents = new Vector2(500f, 20f);
            Assert.True(gen.Populate("Fit", settings));
            Assert.Equal(60, gen.fontSizeUsedForBestFit);
            settings.verticalOverflow = VerticalWrapMode.Truncate;
            Assert.True(gen.Populate("Fit", settings));
            int size = gen.fontSizeUsedForBestFit;
            Assert.InRange(size, 4, 59);
            Assert.True(gen.rectExtents.height <= 20.001f);
            settings.generationExtents = new Vector2(500f, 1f);
            gen.Populate("Fit", settings);
            Assert.Equal(4, gen.fontSizeUsedForBestFit);
        }

        [Fact]
        public void TextGeneratorFailsWithoutAFont()
        {
            var gen = new TextGenerator();
            var settings = Settings(LiberationSans, new Vector2(300f, 100f));
            Assert.True(gen.Populate("Text", settings));
            settings.font = null;
            Assert.False(gen.Populate("Text", settings));
            Assert.Empty(gen.verts);
            Assert.Equal(0, gen.lineCount);
        }

        [Fact]
        public void FontNamedIsEmpty()
        {
            var font = new Font("fonts/NotoSansUI-Regular.ttf");
            Assert.Equal("fonts/NotoSansUI-Regular.ttf", font.name);
            Assert.False(font.HasCharacter('A'));
            Assert.False(font.dynamic);
        }

        [Fact]
        public void TextGeneratorDropsOverridesOnNonDynamicFonts()
        {
            var font = new Font();
            font.name = "Bitmap";
            var logs = new List<(string message, LogType type)>();
            Application.LogCallback record = (message, _, type) => logs.Add((message, type));
            Application.logMessageReceived += record;
            try
            {
                var gen = new TextGenerator();
                var settings = Settings(font, new Vector2(300f, 100f));
                settings.fontStyle = FontStyle.Bold;
                settings.resizeTextForBestFit = true;
                settings.fontSize = 20;
                Assert.True(gen.PopulateWithErrors("Text", settings, null));
                Assert.Contains(("Font size and style overrides are only supported for dynamic fonts. Font 'Bitmap' is not dynamic.", LogType.Warning), logs);
                Assert.Contains(("BestFit is only supported for dynamic fonts. Font 'Bitmap' is not dynamic.", LogType.Warning), logs);
                Assert.DoesNotContain(logs, l => l.type == LogType.Error);
                Assert.True(new TextGenerator().Populate("Text", settings));
            }
            finally
            {
                Application.logMessageReceived -= record;
                Object.DestroyImmediate(font);
            }
        }

        [Fact]
        public void TextGeneratorTruncatesAndParsesRichText()
        {
            var font = LiberationSans;
            var gen = new TextGenerator();
            var s = Settings(font, new Vector2(300f, 10f));
            s.verticalOverflow = VerticalWrapMode.Truncate;
            gen.Populate("Hello", s);
            Assert.Equal(0, gen.lineCount);
            Assert.Empty(gen.verts);

            s = Settings(font, new Vector2(300f, 100f));
            gen.Populate("<b>A</b><color=#ff0000>B</color><i>unclosed", s);
            Assert.Equal(1, gen.lineCount);
            // Tags produce no quads; the unclosed <i> is text.
            Assert.Equal(4 * (2 + "<i>unclosed".Length), gen.verts.Count);
            Assert.Equal(new Color32(255, 0, 0, 255), gen.verts[4].color);
            Assert.Equal(new Color32(255, 255, 255, 255), gen.verts[0].color);
        }

        [Fact]
        public void DynamicFontTextureHoldsRequestedGlyphs()
        {
            var font = LiberationSans;
            var rebuilt = new List<Font>();
            Action<Font> handler = f => rebuilt.Add(f);
            Font.textureRebuilt += handler;
            try
            {
                var gen = new TextGenerator();
                gen.Populate("Sample text", Settings(font, new Vector2(300f, 100f)));
                var texture = (Texture2D)font.material.mainTexture;
                Assert.Equal(TextureFormat.Alpha8, texture.format);
                Assert.True(texture.width >= 256);
                // Every quad samples inked texels of the font texture.
                var pixels = texture.GetRawTextureData();
                for (int q = 0; q < gen.verts.Count; q += 4)
                {
                    var tl = gen.verts[q].uv0;
                    var br = gen.verts[q + 2].uv0;
                    int x0 = (int)(tl.x * texture.width), x1 = (int)(br.x * texture.width);
                    int y0 = (int)(br.y * texture.height), y1 = (int)(tl.y * texture.height);
                    int ink = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                            ink = Math.Max(ink, pixels[y * texture.width + x]);
                    Assert.True(ink > 128);
                }

                // Filling the texture forces a rebuild and growth.
                int before = texture.width * texture.height;
                rebuilt.Clear();
                var big = new string(Enumerable.Range(0x21, 0x5E).Select(c => (char)c).ToArray());
                for (int size = 40; size <= 120; size += 20) font.RequestCharactersInTexture(big, size, FontStyle.Normal);
                Assert.Contains(font, rebuilt);
                Assert.True(texture.width * texture.height > before);
            }
            finally
            {
                Font.textureRebuilt -= handler;
            }
        }

        [Fact]
        public void TextMeshBuildsAnchoredGeometryForItsMeshRenderer()
        {
            var font = LiberationSans;
            var go = new GameObject("label");
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = font.material;
            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontSize = 20;
            tm.text = "Ab\nCd";
            tm.color = Color.red;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.characterSize = 0.5f;

            var native = (OniMoono.Engine.Text.TextMeshNative)tm.Native;
            var mesh = native.Geometry();
            Assert.Equal(16, mesh.VertexCount);
            Assert.Equal(24, mesh.SubMeshes[0].Indices.Length);
            Assert.All(mesh.Colors, c => Assert.Equal(Color.red, c));
            // Middle centre: the block is centred on the origin; 20 px at 0.05 units per pixel.
            var b = mesh.Bounds;
            Assert.InRange(b.center.x, -0.15f, 0.15f);
            Assert.InRange(b.center.y, -0.3f, 0.3f);
            Assert.InRange(b.size.y, 1.2f, 2.2f);
            Assert.Equal(b.size, renderer.localBounds.size);

            // Unchanged state reuses the mesh; a text change regenerates it.
            int version = mesh.Version;
            native.Geometry();
            Assert.Equal(version, mesh.Version);
            tm.anchor = TextAnchor.UpperLeft;
            tm.text = "Ab";
            native.Geometry();
            Assert.Equal(8, mesh.VertexCount);
            // The quads include the font's one-pixel padding: 0.05 units here.
            Assert.True(mesh.Bounds.max.y <= 0.051f);
            Assert.True(mesh.Bounds.min.x >= -0.051f);
            Object.DestroyImmediate(go);
        }
    }
}
