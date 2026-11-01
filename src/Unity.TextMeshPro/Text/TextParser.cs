using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TMPro
{
    // Turns a text's source into parsed elements: rich-text tags applied as states, styles
    // expanded, escape sequences decoded, case conversions applied, and each character resolved
    // to a glyph of a font asset (fallbacks searched) or to a sprite, with its material.
    internal sealed class TextParser
    {
        // Rich-text tags are at most this long, attributes included.
        const int MaxTagLength = 256;

        TMP_Text m_Text;
        ParsedText m_Out;
        TextState m_State;
        readonly List<TextState> m_ColorStack = new();
        readonly List<TextState> m_SizeStack = new();
        readonly List<(string Name, TextState State)> m_Stacks = new();
        readonly Stack<TMP_Style> m_StyleStack = new();
        int m_StyleDepth;
        int m_Bold, m_Italic, m_Underline, m_Strike, m_Superscript, m_Subscript, m_Highlight, m_SmallCaps, m_Lower, m_Upper, m_NoBreak;
        bool m_RichText, m_Escapes;
        int m_CurrentLink = -1;
        int m_LinkTextStart;
        int m_SourceBase;

        static readonly HashSet<uint> s_ReportedMissing = new();

        // The last parse had a sprite or font tag whose asset nothing provided: the shipped
        // package asks again at each layout (OnSpriteAssetRequest is raised each time), so the
        // text is parsed again at its next layout rather than kept.
        public bool MissedAsset { get; private set; }

        public void Parse(TMP_Text text, string source, ParsedText output)
        {
            m_Text = text;
            m_Out = output;
            output.Clear();
            MissedAsset = false;
            m_RichText = text.richText;
            m_Escapes = text.parseCtrlCharacters;
            m_Bold = m_Italic = m_Underline = m_Strike = m_Superscript = m_Subscript = m_Highlight = m_SmallCaps = m_Lower = m_Upper = m_NoBreak = 0;
            m_Stacks.Clear();
            m_StyleStack.Clear();
            m_StyleDepth = 0;
            m_CurrentLink = -1;

            var primary = text.font;
            // A dynamic font asset gets "…" and "_" before the text's own characters, whatever
            // the overflow mode.
            if (primary != null && primary.atlasPopulationMode != AtlasPopulationMode.Static)
            {
                primary.TryGetLocalCharacter(0x2026, out _);
                primary.TryGetLocalCharacter('_', out _);
            }
            output.Materials.Add(new MaterialSlot { Material = text.fontSharedMaterial, Asset = primary });

            var s = new TextState
            {
                Font = primary,
                Color = text.color,
                UnderlineColor = text.color,
                StrikethroughColor = text.color,
                Highlight = new HighlightState(new Color32(255, 255, 0, 64), TMP_Offset.zero),
            };
            var style = text.fontStyle;
            s.Style = style & (FontStyles.Bold | FontStyles.Italic | FontStyles.Underline | FontStyles.Strikethrough | FontStyles.Superscript | FontStyles.Subscript | FontStyles.Highlight | FontStyles.LowerCase | FontStyles.UpperCase | FontStyles.SmallCaps);
            s.Weight = text.fontWeight;
            m_State = s;
            m_ColorStack.Clear();
            m_SizeStack.Clear();

            // The ellipsis of the Ellipsis overflow mode is resolved up front (its material
            // takes a slot whether or not the text overflows).
            if (text.overflowMode == TextOverflowModes.Ellipsis) ResolveEllipsis(s);
            if (source == null) source = "";
            // The text's default style (TMP_Text.textStyle) wraps the source.
            var textStyle = text.textStyle;
            if (textStyle != null && m_RichText && !IsNormalStyle(textStyle))
            {
                ParseRange(textStyle.styleOpeningDefinition ?? "", 0, true, 0);
                ParseRange(source, 0, false, 0);
                ParseRange(textStyle.styleClosingDefinition ?? "", source.Length, true, 0);
            }
            else ParseRange(source, 0, false, 0);
            CloseLink(source.Length);
        }

        static bool IsNormalStyle(TMP_Style style) =>
            string.IsNullOrEmpty(style.styleOpeningDefinition) && string.IsNullOrEmpty(style.styleClosingDefinition);

        // Parses `s`. Inserted text (a style definition) maps every element to source index
        // `insertedAt` with no length.
        void ParseRange(string s, int insertedAt, bool inserted, int depth)
        {
            int i = 0;
            bool noParse = false;
            while (i < s.Length)
            {
                char c = s[i];
                int start = i;
                if (c == '<' && m_RichText)
                {
                    if (noParse)
                    {
                        if (MatchesClose(s, i, "noparse", out int end))
                        {
                            noParse = false;
                            i = end;
                            continue;
                        }
                    }
                    else if (TryTag(s, i, inserted ? insertedAt : i, inserted, depth, out int end))
                    {
                        if (end < 0)
                        {
                            noParse = true;
                            i = -end;
                        }
                        else i = end;
                        continue;
                    }
                }
                uint unicode = c;
                int length = 1;
                if (c == '\\' && m_Escapes && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    switch (n)
                    {
                        case 'n': unicode = 10; length = 2; break;
                        case 'r': unicode = 13; length = 2; break;
                        case 't': unicode = 9; length = 2; break;
                        case 'v': unicode = 11; length = 2; break;
                        case 'u':
                            if (i + 5 < s.Length && TryHex(s, i + 2, 4, out uint u4)) { unicode = u4; length = 6; }
                            break;
                        case 'U':
                            if (i + 9 < s.Length && TryHex(s, i + 2, 8, out uint u8)) { unicode = u8; length = 10; }
                            break;
                    }
                }
                if (length == 1 && char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    unicode = (uint)char.ConvertToUtf32(c, s[i + 1]);
                    length = 2;
                }
                AddCharacter(unicode, inserted ? insertedAt : start, inserted ? 0 : length);
                i += length;
            }
        }

        static bool TryHex(string s, int at, int digits, out uint value)
        {
            value = 0;
            for (int k = 0; k < digits; k++)
            {
                int d = HexDigit(s[at + k]);
                if (d < 0) return false;
                value = value << 4 | (uint)d;
            }
            return true;
        }

        static int HexDigit(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        static bool MatchesClose(string s, int i, string name, out int end)
        {
            end = i;
            int len = name.Length + 3;
            if (i + len > s.Length || s[i + 1] != '/' || s[i + len - 1] != '>') return false;
            if (string.Compare(s, i + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            end = i + len;
            return true;
        }

        // ----- Characters -----

        void AddCharacter(uint unicode, int sourceIndex, int sourceLength)
        {
            var state = m_State;
            uint shown = unicode;
            float caseScale = 1f;
            var style = state.Style;
            if ((style & FontStyles.UpperCase) != 0) shown = ToUpper(unicode);
            else if ((style & FontStyles.LowerCase) != 0) shown = ToLower(unicode);
            else if ((style & FontStyles.SmallCaps) != 0)
            {
                uint upper = ToUpper(unicode);
                if (upper != unicode)
                {
                    shown = upper;
                    caseScale = 0.8f;
                }
            }

            ref var e = ref m_Out.Add();
            e.Kind = ElementKind.Character;
            e.Unicode = shown;
            e.SourceUnicode = unicode;
            e.SourceIndex = sourceIndex;
            e.SourceLength = sourceLength;
            e.State = state;
            e.CaseScale = caseScale;

            // Line feeds and other controls need no glyph.
            if (shown == 10 || shown == 11 || shown == 0x2028 || shown == 0x2029 || shown == 3)
            {
                e.MaterialIndex = 0;
                return;
            }
            // CR is an ordinary character of its line, not visible, as wide as the text font's
            // glyph for it, or of no width when that font has none; no fallback is looked in
            // (measured on the shipped package: 7.146 in NotoSans 14 pt, 0 in GRAYSTROKE 12 pt).
            if (shown == 13)
            {
                if ((state.Font ?? m_Text.font) is { } crFont)
                {
                    e.Element = crFont.TryGetLocalCharacter(13, out var cr) ? cr : EmptyCharacter(crFont);
                    e.Asset = crFont;
                    e.MaterialIndex = MaterialFor(crFont, state, false, AtlasOf(e.Element));
                }
                return;
            }
            // A zero-width space the text's font does not have is an empty glyph of that font,
            // not a fallback's (measured on the shipped package).
            if (shown == 0x200B && (state.Font ?? m_Text.font) is { } zwFont && !zwFont.TryGetLocalCharacter(shown, out _))
            {
                e.Element = EmptyCharacter(zwFont);
                e.Asset = zwFont;
                e.MaterialIndex = MaterialFor(zwFont, state, false);
                return;
            }
            if (!ResolveGlyph(shown, state, out var character, out var asset, out bool alternate))
            {
                // A tab the fonts do not have is not replaced: it stays a tab, not visible, of
                // the text's font (measured on the shipped package: "\t" in NotoSans is
                // character 9, not visible, and one line of the font high). Its width comes from
                // the font's space.
                if (shown == 9 && (state.Font ?? m_Text.font) is { } tabFont)
                {
                    e.Element = tabFont.TryGetLocalCharacter(32, out var space) ? space : EmptyCharacter(tabFont);
                    e.Asset = tabFont;
                    e.MaterialIndex = MaterialFor(tabFont, state, false);
                    return;
                }
                // Emoji and other characters may come from the sprite assets.
                if (ResolveSpriteByUnicode(shown, ref e)) return;
                uint missing = (uint)TMP_Settings.missingGlyphCharacter;
                if (missing == 0) missing = 0x25A1;
                if (!TMP_Settings.warningsDisabled && s_ReportedMissing.Add(shown))
                    Debug.LogWarning($"The character with Unicode value \\u{shown:X4} was not found in the [{state.Font?.name}] font asset or any potential fallbacks. It was replaced by Unicode character \\u{missing:X4} in text object [{m_Text.name}].", m_Text);
                if (!ResolveGlyph(missing, state, out character, out asset, out alternate))
                {
                    // Nothing to draw: the element still takes its place as an empty character.
                    e.MaterialIndex = 0;
                    return;
                }
                e.Unicode = missing;
            }
            e.Element = character;
            e.Asset = asset;
            e.AlternateTypeface = alternate;
            e.MaterialIndex = MaterialFor(asset, state, false, AtlasOf(character));
        }

        static readonly Dictionary<int, TMP_Character> s_EmptyCharacters = new();

        static TMP_Character EmptyCharacter(TMP_FontAsset font)
        {
            int id = font.GetInstanceID();
            if (!s_EmptyCharacters.TryGetValue(id, out var c))
                s_EmptyCharacters[id] = c = new TMP_Character(0x200B, font, new UnityEngine.TextCore.Glyph(0, new UnityEngine.TextCore.GlyphMetrics(0f, 0f, 0f, 0f, 0f), new UnityEngine.TextCore.GlyphRect(0, 0, 0, 0)));
            return c;
        }

        void ResolveEllipsis(TextState state)
        {
            ref var e = ref m_Out.Ellipsis;
            e = default;
            e.CaseScale = 1f;
            e.Kind = ElementKind.Character;
            e.Unicode = e.SourceUnicode = 0x2026;
            e.State = state;
            // In a style the text's font has no typeface for, the ellipsis comes from the first
            // typeface for that style among the fonts a missing character is looked for in
            // (measured on the shipped package: "The ellipsis glyph in Bold and Italic texts").
            var primary = state.Font ?? m_Text.font;
            if (primary != null && NeedsTypeface(state) && TypefaceFor(primary, state) == null
                && FindStyledEllipsis(primary, state, out var styled, out var styledFont))
            {
                e.Element = styled;
                e.Asset = styledFont;
                e.AlternateTypeface = true;
                e.MaterialIndex = MaterialFor(styledFont, state, false, AtlasOf(styled));
                return;
            }
            if (!ResolveGlyph(0x2026, state, out var character, out var asset, out bool alternate)) return;
            e.Element = character;
            e.Asset = asset;
            e.AlternateTypeface = alternate;
            e.MaterialIndex = MaterialFor(asset, state, false, AtlasOf(character));
        }

        // Whether the state's style asks for a typeface of a font's weight table.
        static bool NeedsTypeface(TextState state) =>
            (state.Style & (FontStyles.Bold | FontStyles.Italic)) != 0 || state.Weight != FontWeight.Regular;

        // The typeface of a font's weight table for the state's style (null: none).
        static TMP_FontAsset TypefaceFor(TMP_FontAsset font, TextState state)
        {
            bool bold = (state.Style & FontStyles.Bold) != 0;
            bool italic = (state.Style & FontStyles.Italic) != 0;
            int weight = bold ? 7 : (int)state.Weight / 100;
            var weights = font.fontWeightTable;
            if (weights == null || weight < 0 || weight >= weights.Length) return null;
            return italic ? weights[weight].italicTypeface : weights[weight].regularTypeface;
        }

        // The ellipsis of the first typeface for the state's style in the primary font's
        // fallbacks (depth-first), the global fallbacks and the default font asset.
        static bool FindStyledEllipsis(TMP_FontAsset primary, TextState state, out TMP_Character character, out TMP_FontAsset typeface)
        {
            character = null;
            typeface = null;
            var visited = new HashSet<int> { primary.GetInstanceID() };
            if (StyledEllipsisIn(primary.fallbackFontAssetTable, state, visited, out character, out typeface)) return true;
            var global = TMP_Settings.fallbackFontAssets;
            if (global != null && StyledEllipsisIn(global, state, visited, out character, out typeface)) return true;
            var fallback = TMP_Settings.defaultFontAsset;
            return fallback != null && StyledEllipsisIn(new List<TMP_FontAsset> { fallback }, state, visited, out character, out typeface);
        }

        static bool StyledEllipsisIn(List<TMP_FontAsset> fonts, TextState state, HashSet<int> visited, out TMP_Character character, out TMP_FontAsset typeface)
        {
            character = null;
            typeface = null;
            if (fonts == null) return false;
            foreach (var f in fonts)
            {
                if (f == null || !visited.Add(f.GetInstanceID())) continue;
                typeface = TypefaceFor(f, state);
                if (typeface != null && typeface.TryGetLocalCharacter(0x2026, out character)) return true;
                if (StyledEllipsisIn(f.fallbackFontAssetTable, state, visited, out character, out typeface)) return true;
            }
            return false;
        }

        static uint ToUpper(uint u) => u < 0x10000 ? char.ToUpperInvariant((char)u) : u;

        static uint ToLower(uint u) => u < 0x10000 ? char.ToLowerInvariant((char)u) : u;

        // The glyph of a character in the state's font (or its bold/italic typeface), its
        // fallbacks depth-first, the global fallbacks and the default font asset.
        bool ResolveGlyph(uint unicode, TextState state, out TMP_TextElement character, out TMP_Asset asset, out bool alternate)
        {
            character = null;
            asset = null;
            alternate = false;
            var primary = state.Font ?? m_Text.font;
            if (primary == null) return false;
            var font = primary;
            bool bold = (state.Style & FontStyles.Bold) != 0;
            bool italic = (state.Style & FontStyles.Italic) != 0;
            int weight = bold ? 7 : (int)state.Weight / 100;
            var weights = primary.fontWeightTable;
            if (weights != null && weight >= 0 && weight < weights.Length && (bold || italic || state.Weight != FontWeight.Regular))
            {
                var pair = weights[weight];
                var typeface = italic ? pair.italicTypeface : pair.regularTypeface;
                if (typeface != null)
                {
                    font = typeface;
                    alternate = true;
                }
            }
            if (font.TryGetLocalCharacter(unicode, out var found))
            {
                character = found;
                asset = font;
                return true;
            }
            alternate = false;
            var visited = new HashSet<int> { font.GetInstanceID() };
            if (font != primary && primary.TryGetLocalCharacter(unicode, out found) && visited.Add(primary.GetInstanceID()))
            {
                character = found;
                asset = primary;
                return true;
            }
            if (SearchFallbacks(primary, unicode, visited, out found, out var owner)
                || (font != primary && SearchFallbacks(font, unicode, visited, out found, out owner)))
            {
                character = found;
                asset = owner;
                return true;
            }
            var global = TMP_Settings.fallbackFontAssets;
            if (global != null)
                foreach (var f in global)
                {
                    if (f == null || !visited.Add(f.GetInstanceID())) continue;
                    if (f.TryGetLocalCharacter(unicode, out found) || SearchFallbacks(f, unicode, visited, out found, out _))
                    {
                        character = found;
                        asset = found.textAsset ?? f;
                        return true;
                    }
                }
            var fallback = TMP_Settings.defaultFontAsset;
            if (fallback != null && visited.Add(fallback.GetInstanceID()))
            {
                if (fallback.TryGetLocalCharacter(unicode, out found) || SearchFallbacks(fallback, unicode, visited, out found, out _))
                {
                    character = found;
                    asset = found.textAsset ?? fallback;
                    return true;
                }
            }
            return false;
        }

        static bool SearchFallbacks(TMP_FontAsset font, uint unicode, HashSet<int> visited, out TMP_Character character, out TMP_FontAsset owner)
        {
            character = null;
            owner = null;
            var list = font.fallbackFontAssetTable;
            if (list == null) return false;
            foreach (var f in list)
            {
                if (f == null || !visited.Add(f.GetInstanceID())) continue;
                if (f.TryGetLocalCharacter(unicode, out character))
                {
                    owner = f;
                    return true;
                }
                if (SearchFallbacks(f, unicode, visited, out character, out owner)) return true;
            }
            return false;
        }

        bool ResolveSpriteByUnicode(uint unicode, ref ParsedElement e)
        {
            if (!TMP_Settings.enableEmojiSupport) return false;
            var sprites = m_Text.spriteAsset ?? TMP_Settings.defaultSpriteAsset;
            var asset = TMP_SpriteAsset.SearchForSpriteByUnicode(sprites, unicode, true, out int index);
            if (asset == null && sprites != TMP_Settings.defaultSpriteAsset)
                asset = TMP_SpriteAsset.SearchForSpriteByUnicode(TMP_Settings.defaultSpriteAsset, unicode, true, out index);
            if (asset == null) return false;
            e.Kind = ElementKind.Sprite;
            e.Element = asset.spriteCharacterTable[index];
            e.Asset = asset;
            e.MaterialIndex = MaterialFor(asset, e.State, true);
            return true;
        }

        // The atlas a character's glyph is drawn from.
        static int AtlasOf(TMP_TextElement character) => character?.glyph != null ? character.glyph.atlasIndex : 0;

        int MaterialFor(TMP_Asset asset, TextState state, bool sprite, int atlas = 0)
        {
            Material material;
            if (sprite) material = asset.material;
            else if (state.Material != null && asset == state.Font) material = state.Material;
            else if (asset == m_Text.font && atlas == 0) return 0;
            else if (asset == m_Text.font) material = m_Text.fontSharedMaterial;
            else if (asset == state.Font) material = asset.material;
            // A fallback font draws with the text's material on its own atlas (match material
            // presets) or with its own material.
            else material = TMP_Settings.matchMaterialPreset ? FallbackMaterials.Get(m_Text.fontSharedMaterial, (TMP_FontAsset)asset) : asset.material;
            // A glyph in a later atlas of its font draws with that material on its atlas, in a
            // sub-mesh of its own (measured on the shipped package).
            if (atlas > 0 && asset is TMP_FontAsset font && font.atlasTextures != null && atlas < font.atlasTextures.Length && font.atlasTextures[atlas] != null)
                material = FallbackMaterials.ForAtlas(material, font.atlasTextures[atlas]);
            var slots = m_Out.Materials;
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].Material == material && slots[i].Asset == asset) return i;
            slots.Add(new MaterialSlot { Material = material, Asset = asset, IsSprite = sprite, IsFallback = !sprite && asset != state.Font });
            return slots.Count - 1;
        }

        // ----- Tags -----

        // Applies the tag at `i` when it is a known rich-text tag. `end` is the index after it,
        // or minus that for <noparse>.
        bool TryTag(string s, int i, int sourceIndex, bool inserted, int depth, out int end)
        {
            end = i;
            int close = s.IndexOf('>', i + 1);
            if (close < 0 || close - i > MaxTagLength) return false;
            var tag = new TagReader(s, i + 1, close);
            if (!tag.Valid) return false;
            end = close + 1;
            int tagLength = inserted ? 0 : end - i;
            string name = tag.Name;
            bool closing = tag.Closing;
            switch (name)
            {
                case "b": return Flag(ref m_Bold, closing, FontStyles.Bold);
                case "i": return Flag(ref m_Italic, closing, FontStyles.Italic);
                case "u": return Flag(ref m_Underline, closing, FontStyles.Underline, underline: true);
                case "s": case "strikethrough": return Flag(ref m_Strike, closing, FontStyles.Strikethrough, strike: true);
                case "sup": return Flag(ref m_Superscript, closing, FontStyles.Superscript);
                case "sub": return Flag(ref m_Subscript, closing, FontStyles.Subscript);
                case "lowercase": return Flag(ref m_Lower, closing, FontStyles.LowerCase);
                case "uppercase": case "allcaps": return Flag(ref m_Upper, closing, FontStyles.UpperCase);
                case "smallcaps": return Flag(ref m_SmallCaps, closing, FontStyles.SmallCaps);
                case "mark":
                    if (closing) return Flag(ref m_Highlight, true, FontStyles.Highlight);
                    {
                        var st = Push("mark");
                        if (tag.HasValue && TryColor(tag.Value, out var mc)) st.Highlight = new HighlightState(mc, st.Highlight.padding);
                        if (tag.TryAttribute("color", out var colorText) && TryColor(colorText, out var mc2)) st.Highlight = new HighlightState(mc2, st.Highlight.padding);
                        if (tag.TryAttribute("padding", out var pad) && TryPadding(pad, out var offset)) st.Highlight = new HighlightState(st.Highlight.color, offset);
                        m_Highlight++;
                        st.Style |= FontStyles.Highlight;
                        return true;
                    }
                case "font-weight":
                    if (closing) return Pop("font-weight");
                    if (!int.TryParse(tag.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) return false;
                    Push("font-weight").Weight = (FontWeight)w;
                    return true;
                case "color":
                    if (closing) return PopColor();
                    if (!TryColor(tag.Value, out var color)) return false;
                    PushColor(color);
                    return true;
                case "alpha":
                    if (closing) return Pop("alpha");
                    if (!TryHexByte(tag.Value, out byte alpha)) return false;
                    var stA = Push("alpha");
                    stA.AlphaOverride = alpha;
                    stA.HasAlphaOverride = true;
                    return true;
                case "font":
                    if (closing) return Pop("font");
                    {
                        var font = FindFont(tag.Value);
                        if (font == null) return false;
                        var stF = Push("font");
                        stF.Font = font;
                        stF.Material = null;
                        if (tag.TryAttribute("material", out var matName)) stF.Material = FindMaterial(matName, font);
                        return true;
                    }
                case "material":
                    if (closing) return Pop("material");
                    {
                        var material = FindMaterial(tag.Value, m_State.Font ?? m_Text.font);
                        if (material == null) return false;
                        Push("material").Material = material;
                        return true;
                    }
                case "gradient":
                    if (closing) return Pop("gradient");
                    {
                        if (!MaterialReferenceManager.TryGetColorGradientPreset(TMP_TextUtilities.GetHashCode(tag.Value), out var gradient) || gradient == null)
                            gradient = Resources.Load<TMP_ColorGradient>((TMP_Settings.defaultColorGradientPresetsPath ?? "") + tag.Value);
                        if (gradient == null) return false;
                        Push("gradient").Gradient = gradient;
                        return true;
                    }
                case "size":
                    if (closing) return PopSize();
                    return PushSize(tag.Value);
                case "cspace":
                    if (closing) return Pop("cspace");
                    { if (!TryMeasure(tag.Value, out var m)) return false; Push("cspace").CharacterSpacing = m; return true; }
                case "mspace":
                    if (closing) return Pop("mspace");
                    { if (!TryMeasure(tag.Value, out var m)) return false; Push("mspace").Monospace = m; return true; }
                case "voffset":
                    if (closing) return Pop("voffset");
                    { if (!TryMeasure(tag.Value, out var m)) return false; Push("voffset").VerticalOffset = m; return true; }
                case "indent":
                    if (closing) return Pop("indent");
                    {
                        if (!TryMeasure(tag.Value, out var m)) return false;
                        Push("indent").Indent = m;
                        // An indent opened part-way along a line moves the pen to it, ahead or
                        // back; one closed leaves the pen where it is (measured on the shipped
                        // package).
                        AddInstruction(ElementKind.Indent, m, sourceIndex, tagLength);
                        return true;
                    }
                case "line-indent":
                    if (closing) return Pop("line-indent");
                    { if (!TryMeasure(tag.Value, out var m)) return false; Push("line-indent").LineIndent = m; return true; }
                case "margin":
                    if (closing) return Pop("margin");
                    {
                        var st = Push("margin");
                        if (tag.HasValue)
                        {
                            if (!TryMeasure(tag.Value, out var m)) { Pop("margin"); return false; }
                            st.MarginLeft = st.MarginRight = m;
                        }
                        if (tag.TryAttribute("left", out var l) && TryMeasure(l, out var ml)) st.MarginLeft = ml;
                        if (tag.TryAttribute("right", out var r) && TryMeasure(r, out var mr)) st.MarginRight = mr;
                        return true;
                    }
                case "margin-left":
                    if (closing) return Pop("margin-left");
                    { if (!TryMeasure(tag.Value, out var m)) return false; Push("margin-left").MarginLeft = m; return true; }
                case "margin-right":
                    if (closing) return Pop("margin-right");
                    { if (!TryMeasure(tag.Value, out var m)) return false; Push("margin-right").MarginRight = m; return true; }
                case "line-height":
                    if (closing) return Pop("line-height");
                    {
                        if (!TryMeasure(tag.Value, out var m)) return false;
                        var st = Push("line-height");
                        st.LineHeight = m;
                        st.HasLineHeight = true;
                        return true;
                    }
                case "width":
                    if (closing) return Pop("width");
                    {
                        if (!TryMeasure(tag.Value, out var m)) return false;
                        var st = Push("width");
                        st.Width = m;
                        st.HasWidth = true;
                        return true;
                    }
                case "align":
                    if (closing) return Pop("align");
                    {
                        HorizontalAlignmentOptions a;
                        switch (tag.Value.ToLowerInvariant())
                        {
                            case "left": a = HorizontalAlignmentOptions.Left; break;
                            case "center": a = HorizontalAlignmentOptions.Center; break;
                            case "right": a = HorizontalAlignmentOptions.Right; break;
                            case "justified": a = HorizontalAlignmentOptions.Justified; break;
                            case "flush": a = HorizontalAlignmentOptions.Flush; break;
                            default: return false;
                        }
                        Push("align").Align = a;
                        return true;
                    }
                case "rotate":
                    if (closing) return Pop("rotate");
                    {
                        if (!float.TryParse(tag.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float deg)) return false;
                        Push("rotate").Rotation = deg;
                        return true;
                    }
                case "nobr":
                    if (closing)
                    {
                        if (m_NoBreak == 0) return false;
                        m_NoBreak--;
                        if (m_NoBreak == 0) Modify().NoBreak = false;
                        return true;
                    }
                    m_NoBreak++;
                    Modify().NoBreak = true;
                    return true;
                case "noparse":
                    if (closing) return false;
                    end = -end;
                    return true;
                case "space":
                    if (closing) return false;
                    {
                        if (!TryMeasure(tag.Value, out var m)) return false;
                        AddInstruction(ElementKind.Space, m, sourceIndex, tagLength);
                        return true;
                    }
                case "pos":
                    if (closing) return false;
                    {
                        if (!TryMeasure(tag.Value, out var m)) return false;
                        AddInstruction(ElementKind.Position, m, sourceIndex, tagLength);
                        return true;
                    }
                case "page":
                    if (closing) return false;
                    AddInstruction(ElementKind.PageBreak, default, sourceIndex, tagLength);
                    return true;
                case "br":
                    if (closing) return false;
                    AddCharacter(10, sourceIndex, tagLength);
                    return true;
                case "zwsp":
                    if (closing) return false;
                    AddCharacter(0x200B, sourceIndex, tagLength);
                    return true;
                case "nbsp":
                    if (closing) return false;
                    AddCharacter(0xA0, sourceIndex, tagLength);
                    return true;
                case "sprite":
                    if (closing) return false;
                    return AddSprite(tag, sourceIndex, tagLength);
                case "link":
                    if (closing)
                    {
                        if (m_CurrentLink < 0) return false;
                        CloseLink(sourceIndex);
                        return true;
                    }
                    {
                        CloseLink(sourceIndex);
                        var link = new LinkRecord { Id = tag.Value ?? "", IdSourceIndex = inserted ? sourceIndex : i + 1 + tag.ValueStart, IdLength = inserted ? 0 : tag.ValueLength };
                        m_Out.Links.Add(link);
                        m_CurrentLink = m_Out.Links.Count - 1;
                        Modify().Link = m_CurrentLink;
                        return true;
                    }
                case "style":
                    if (closing)
                    {
                        if (m_StyleStack.Count == 0) return true;
                        var popped = m_StyleStack.Pop();
                        if (!string.IsNullOrEmpty(popped.styleClosingDefinition) && depth < 8)
                            ParseRange(popped.styleClosingDefinition, sourceIndex, true, depth + 1);
                        return true;
                    }
                    {
                        var sheet = m_Text.styleSheet ?? TMP_StyleSheet.instance;
                        var style = sheet?.GetStyle(tag.Value);
                        if (style == null && sheet != TMP_StyleSheet.instance) style = TMP_StyleSheet.instance?.GetStyle(tag.Value);
                        if (style == null) return false;
                        m_StyleStack.Push(style);
                        if (!string.IsNullOrEmpty(style.styleOpeningDefinition) && depth < 8)
                            ParseRange(style.styleOpeningDefinition, sourceIndex, true, depth + 1);
                        return true;
                    }
            }
            // Shorthand colour: <#RRGGBB>.
            if (!closing && name.Length > 1 && name[0] == '#' && !tag.HasValue && TryColor(tag.RawName, out var shorthand))
            {
                PushColor(shorthand);
                return true;
            }
            end = i;
            return false;
        }

        void CloseLink(int sourceIndex)
        {
            if (m_CurrentLink < 0) return;
            m_CurrentLink = -1;
            Modify().Link = -1;
        }

        void AddInstruction(ElementKind kind, Measure amount, int sourceIndex, int sourceLength)
        {
            ref var e = ref m_Out.Add();
            e.Kind = kind;
            e.SourceIndex = sourceIndex;
            e.SourceLength = sourceLength;
            e.State = m_State;
            e.Amount = amount;
            e.MaterialIndex = 0;
        }

        bool AddSprite(TagReader tag, int sourceIndex, int sourceLength)
        {
            var defaultSprites = m_Text.spriteAsset ?? TMP_Settings.defaultSpriteAsset;
            TMP_SpriteAsset asset = null;
            int index = -1;
            // An unquoted name runs to the tag's end (measured: <sprite=a index=0> names "a
            // index=0").
            string assetName = tag.HasValue && !IsInteger(tag.Value) ? tag.UnquotedValueToEnd ?? tag.Value : null;
            if (assetName != null)
            {
                asset = FindSpriteAsset(assetName);
                if (asset == null) return false;
            }
            else asset = defaultSprites;
            if (asset == null) return false;
            if (tag.HasValue && IsInteger(tag.Value)) index = int.Parse(tag.Value, CultureInfo.InvariantCulture);
            if (tag.TryAttribute("index", out var indexText) && int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) index = idx;
            if (tag.TryAttribute("name", out var spriteName))
            {
                var found = TMP_SpriteAsset.SearchForSpriteByName(asset, spriteName, true, out index);
                if (found != null) asset = found;
                // In an asset the tag names, a name it lacks gives sprite 0; in the default
                // sprite asset the tag stays as text (measured).
                else if (assetName != null) index = 0;
                else return false;
            }
            if (index < 0 || index >= asset.spriteCharacterTable.Count) return false;
            ref var e = ref m_Out.Add();
            e.Kind = ElementKind.Sprite;
            e.Unicode = 0xE000 + (uint)index;
            e.SourceUnicode = e.Unicode;
            e.SourceIndex = sourceIndex;
            e.SourceLength = sourceLength;
            e.State = m_State;
            e.Element = asset.spriteCharacterTable[index];
            e.Asset = asset;
            e.MaterialIndex = MaterialFor(asset, m_State, true);
            if (tag.TryAttribute("tint", out var tint)) e.SpriteTint = tint == "1";
            if (tag.TryAttribute("color", out var colorText) && TryColor(colorText, out var c))
            {
                e.HasSpriteColor = true;
                e.SpriteColor = c;
            }
            return true;
        }

        static bool IsInteger(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        // A <font="NAME"> tag's asset: "default" is the text's font; otherwise the font
        // MaterialReferenceManager holds under GetHashCode(NAME), whatever case NAME is in. A font
        // that is not registered is not found by its name (measured: not even the text's own).
        // Then TMP_Text.OnFontAssetRequest is raised, and then the font is looked for under the
        // Resources path for font assets, where the game's one font tag finds Bangers SDF; what
        // either finds is registered under that hash (measured).
        TMP_FontAsset FindFont(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.Equals("default", StringComparison.OrdinalIgnoreCase)) return m_Text.font;
            int hash = TMP_TextUtilities.GetHashCode(name);
            if (MaterialReferenceManager.TryGetFontAsset(hash, out var font) && font != null) return font;
            font = TMP_Text.RequestFontAsset(hash, name);
            if (font == null) font = FromResources<TMP_FontAsset>(s_FontMisses, hash, (TMP_Settings.defaultFontAssetPath ?? DefaultFontPath) + name);
            if (font != null) MaterialReferenceManager.AddRequestedFont(hash, font);
            else MissedAsset = true;
            return font;
        }

        const string DefaultFontPath = "Fonts & Materials/";

        // Names (by hash) that Resources does not hold, so that a tag naming one does not search
        // the resources again at every layout.
        static readonly HashSet<int> s_FontMisses = new();
        static readonly HashSet<int> s_SpriteMisses = new();
        static readonly HashSet<int> s_MaterialMisses = new();

        static T FromResources<T>(HashSet<int> misses, int hash, string path) where T : UnityEngine.Object
        {
            if (misses.Contains(hash)) return null;
            var asset = Resources.Load<T>(path);
            if (asset == null) misses.Add(hash);
            return asset;
        }

        // A material named in a tag: the registry's under the name's hash, the font's own by name,
        // or the one under the Resources path for font materials, which is then registered under
        // that hash (measured: "Bangers SDF - Drop Shadow").
        static Material FindMaterial(string name, TMP_FontAsset font)
        {
            if (string.IsNullOrEmpty(name)) return null;
            int hash = TMP_TextUtilities.GetHashCode(name);
            if (MaterialReferenceManager.TryGetMaterial(hash, out var material) && material != null) return material;
            if (font != null && font.material != null && font.material.name == name) return font.material;
            material = FromResources<Material>(s_MaterialMisses, hash, (TMP_Settings.defaultFontAssetPath ?? DefaultFontPath) + name);
            if (material != null) MaterialReferenceManager.AddFontMaterial(hash, material);
            return material;
        }

        // A <sprite="NAME"> tag's asset: the one MaterialReferenceManager holds under
        // GetHashCode(NAME). Not the text's own sprite asset or any other loaded one by its name
        // (measured). Then TMP_Text.OnSpriteAssetRequest is raised (its last handler's result
        // counts), and then the asset is looked for under the Resources path for sprite assets,
        // where the game's tags find oni_sprite_assets; what either finds is registered under
        // that hash. With neither, the event is raised again at the next layout (measured).
        TMP_SpriteAsset FindSpriteAsset(string name)
        {
            int hash = TMP_TextUtilities.GetHashCode(name);
            if (MaterialReferenceManager.TryGetSpriteAsset(hash, out var asset) && asset != null) return asset;
            asset = TMP_Text.RequestSpriteAsset(hash, name);
            if (asset == null) asset = FromResources<TMP_SpriteAsset>(s_SpriteMisses, hash, (TMP_Settings.defaultSpriteAssetPath ?? "") + name);
            if (asset != null) MaterialReferenceManager.AddSpriteAsset(hash, asset);
            else MissedAsset = true;
            return asset;
        }

        // ----- State changes -----

        // A copy of the current state that becomes current.
        TextState Modify()
        {
            m_State = m_State.Clone();
            return m_State;
        }

        TextState Push(string name)
        {
            m_Stacks.Add((name, m_State));
            return Modify();
        }

        // Restores the property of the latest tag of this name to its value before that tag;
        // other properties keep their current values (tags may close out of order).
        bool Pop(string name)
        {
            for (int k = m_Stacks.Count - 1; k >= 0; k--)
            {
                if (m_Stacks[k].Name != name) continue;
                var saved = m_Stacks[k].State;
                m_Stacks.RemoveAt(k);
                CopyProperty(name, saved, Modify());
                return true;
            }
            return true;
        }

        static void CopyProperty(string name, TextState from, TextState to)
        {
            switch (name)
            {
                case "mark": to.Highlight = from.Highlight; break;
                case "font-weight": to.Weight = from.Weight; break;
                case "alpha": to.AlphaOverride = from.AlphaOverride; to.HasAlphaOverride = from.HasAlphaOverride; break;
                case "font": to.Font = from.Font; to.Material = from.Material; break;
                case "material": to.Material = from.Material; break;
                case "gradient": to.Gradient = from.Gradient; break;
                case "cspace": to.CharacterSpacing = from.CharacterSpacing; break;
                case "mspace": to.Monospace = from.Monospace; break;
                case "voffset": to.VerticalOffset = from.VerticalOffset; break;
                case "indent": to.Indent = from.Indent; break;
                case "line-indent": to.LineIndent = from.LineIndent; break;
                case "margin": to.MarginLeft = from.MarginLeft; to.MarginRight = from.MarginRight; break;
                case "margin-left": to.MarginLeft = from.MarginLeft; break;
                case "margin-right": to.MarginRight = from.MarginRight; break;
                case "line-height": to.LineHeight = from.LineHeight; to.HasLineHeight = from.HasLineHeight; break;
                case "width": to.Width = from.Width; to.HasWidth = from.HasWidth; break;
                case "align": to.Align = from.Align; break;
                case "rotate": to.Rotation = from.Rotation; break;
            }
        }

        bool Flag(ref int counter, bool closing, FontStyles flag, bool underline = false, bool strike = false)
        {
            if (closing)
            {
                if (counter > 0) counter--;
                // The text's own style keeps the flag on.
                if (counter == 0 && (m_Text.fontStyle & flag) == 0) Modify().Style &= ~flag;
                if (flag == FontStyles.Highlight) Pop("mark");
                return true;
            }
            counter++;
            var s = Modify();
            s.Style |= flag;
            if (underline) s.UnderlineColor = s.Color;
            if (strike) s.StrikethroughColor = s.Color;
            return true;
        }

        void PushColor(Color32 color)
        {
            m_ColorStack.Add(m_State);
            var st = Modify();
            st.Color = color;
            st.ColorFromTag = true;
        }

        bool PopColor()
        {
            if (m_ColorStack.Count == 0) return true;
            var previous = m_ColorStack[^1];
            m_ColorStack.RemoveAt(m_ColorStack.Count - 1);
            var st = Modify();
            st.Color = previous.Color;
            st.ColorFromTag = previous.ColorFromTag;
            return true;
        }

        bool PushSize(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            float scale = m_State.SizeScale, offset = m_State.SizeOffset;
            string v = value.Trim();
            if (v.EndsWith("%", StringComparison.Ordinal))
            {
                if (!TryFloat(v[..^1], out float p)) return false;
                scale *= p / 100f;
                offset *= p / 100f;
            }
            else if (v.EndsWith("em", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryFloat(v[..^2], out float em)) return false;
                scale *= em;
                offset *= em;
            }
            else
            {
                if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)) v = v[..^2];
                if (!TryFloat(v, out float px)) return false;
                if (v[0] == '+' || v[0] == '-') offset += px;
                else
                {
                    scale = 0f;
                    offset = px;
                }
            }
            m_SizeStack.Add(m_State);
            var st = Modify();
            st.SizeScale = scale;
            st.SizeOffset = offset;
            return true;
        }

        bool PopSize()
        {
            if (m_SizeStack.Count == 0) return true;
            var previous = m_SizeStack[^1];
            m_SizeStack.RemoveAt(m_SizeStack.Count - 1);
            var st = Modify();
            st.SizeScale = previous.SizeScale;
            st.SizeOffset = previous.SizeOffset;
            return true;
        }

        // ----- Values -----

        static bool TryFloat(string s, out float value) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        static bool TryMeasure(string s, out Measure measure)
        {
            measure = default;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            var unit = MeasureUnit.Pixels;
            if (s.EndsWith("em", StringComparison.OrdinalIgnoreCase)) { unit = MeasureUnit.Em; s = s[..^2]; }
            else if (s.EndsWith("%", StringComparison.Ordinal)) { unit = MeasureUnit.Percent; s = s[..^1]; }
            else if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
            if (!TryFloat(s, out float v)) return false;
            measure = new Measure(v, unit);
            return true;
        }

        static bool TryPadding(string s, out TMP_Offset offset)
        {
            offset = default;
            var parts = s.Split(',');
            var values = new float[4];
            if (parts.Length != 4 && parts.Length != 1) return false;
            for (int k = 0; k < parts.Length; k++)
            {
                if (!TryMeasure(parts[k], out var m)) return false;
                values[k] = m.Value;
            }
            offset = parts.Length == 1 ? new TMP_Offset(values[0], values[0], values[0], values[0]) : new TMP_Offset(values[0], values[1], values[2], values[3]);
            return true;
        }

        static bool TryHexByte(string s, out byte value)
        {
            value = 0;
            if (s == null) return false;
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s[1..];
            if (s.Length != 2) return false;
            int hi = HexDigit(s[0]), lo = HexDigit(s[1]);
            if (hi < 0 || lo < 0) return false;
            value = (byte)(hi << 4 | lo);
            return true;
        }

        // Named colours and #RGB, #RGBA, #RRGGBB, #RRGGBBAA.
        internal static bool TryColor(string s, out Color32 color)
        {
            color = default;
            if (string.IsNullOrEmpty(s)) return false;
            if (s[0] == '#')
            {
                int n = s.Length - 1;
                var d = new int[n];
                for (int k = 0; k < n; k++)
                {
                    d[k] = HexDigit(s[k + 1]);
                    if (d[k] < 0) return false;
                }
                switch (n)
                {
                    case 3: color = new Color32((byte)(d[0] * 17), (byte)(d[1] * 17), (byte)(d[2] * 17), 255); return true;
                    case 4: color = new Color32((byte)(d[0] * 17), (byte)(d[1] * 17), (byte)(d[2] * 17), (byte)(d[3] * 17)); return true;
                    case 6: color = new Color32((byte)(d[0] << 4 | d[1]), (byte)(d[2] << 4 | d[3]), (byte)(d[4] << 4 | d[5]), 255); return true;
                    case 8: color = new Color32((byte)(d[0] << 4 | d[1]), (byte)(d[2] << 4 | d[3]), (byte)(d[4] << 4 | d[5]), (byte)(d[6] << 4 | d[7])); return true;
                    default: return false;
                }
            }
            switch (s.ToLowerInvariant())
            {
                case "red": color = new Color32(255, 0, 0, 255); return true;
                case "lightblue": color = new Color32(173, 216, 230, 255); return true;
                case "blue": color = new Color32(0, 0, 255, 255); return true;
                case "grey": case "gray": color = new Color32(128, 128, 128, 255); return true;
                case "black": color = new Color32(0, 0, 0, 255); return true;
                case "green": color = new Color32(0, 255, 0, 255); return true;
                case "white": color = new Color32(255, 255, 255, 255); return true;
                case "orange": color = new Color32(255, 128, 0, 255); return true;
                case "purple": color = new Color32(160, 32, 240, 255); return true;
                case "yellow": color = new Color32(255, 255, 0, 255); return true;
            }
            return false;
        }

        // A tag's name, its value (after '=') and its attributes (name=value pairs).
        readonly struct TagReader
        {
            public readonly bool Valid;
            public readonly bool Closing;
            public readonly string Name;
            public readonly string RawName;
            public readonly string Value;
            public readonly int ValueStart;
            public readonly int ValueLength;
            // An unquoted value with everything after it to the tag's end ("a index=0" in
            // <sprite=a index=0>); null for a quoted one.
            public readonly string UnquotedValueToEnd;
            readonly List<(string, string)> m_Attributes;

            public bool HasValue => Value != null;

            public TagReader(string s, int from, int to)
            {
                this = default;
                int i = from;
                if (i < to && s[i] == '/')
                {
                    Closing = true;
                    i++;
                }
                int nameStart = i;
                while (i < to && s[i] != '=' && s[i] != ' ' && s[i] != '\t') i++;
                if (i == nameStart) return;
                RawName = s.Substring(nameStart, i - nameStart);
                Name = RawName.ToLowerInvariant();
                if (i < to && s[i] == '=')
                {
                    i++;
                    bool quoted = i < to && (s[i] == '"' || s[i] == '\'');
                    if (!ReadValue(s, ref i, to, out Value, out ValueStart, out ValueLength)) return;
                    if (!quoted) UnquotedValueToEnd = s.Substring(ValueStart, to - ValueStart);
                    ValueStart -= from;
                }
                m_Attributes = null;
                while (i < to)
                {
                    while (i < to && (s[i] == ' ' || s[i] == '\t')) i++;
                    if (i >= to) break;
                    int a = i;
                    while (i < to && s[i] != '=' && s[i] != ' ') i++;
                    var attr = s.Substring(a, i - a).ToLowerInvariant();
                    string value = "";
                    if (i < to && s[i] == '=')
                    {
                        i++;
                        if (!ReadValue(s, ref i, to, out value, out _, out _)) return;
                    }
                    (m_Attributes ??= new List<(string, string)>()).Add((attr, value));
                }
                if (Closing && Value != null) return;
                Valid = true;
            }

            static bool ReadValue(string s, ref int i, int to, out string value, out int start, out int length)
            {
                value = null;
                start = i;
                length = 0;
                if (i >= to) { value = ""; return true; }
                char q = s[i];
                if (q == '"' || q == '\'')
                {
                    int close = s.IndexOf(q, i + 1);
                    if (close < 0 || close > to) return false;
                    start = i + 1;
                    length = close - start;
                    value = s.Substring(start, length);
                    i = close + 1;
                    return true;
                }
                int b = i;
                while (i < to && s[i] != ' ') i++;
                start = b;
                length = i - b;
                value = s.Substring(b, length);
                return true;
            }

            public bool TryAttribute(string name, out string value)
            {
                value = null;
                if (m_Attributes == null) return false;
                foreach (var (n, v) in m_Attributes)
                    if (n == name)
                    {
                        value = v;
                        return true;
                    }
                return false;
            }
        }
    }
}
