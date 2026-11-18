using System;
using System.Collections.Generic;
using OniMoono.Engine.Text;

namespace UnityEngine
{
    /// The legacy text generator: lays a string out with TextGenerationSettings into vertices,
    /// character infos and line infos. The result is cached: Populate regenerates only when the
    /// string or the settings change, or after Invalidate.
    public sealed partial class TextGenerator
    {
        readonly List<UIVertex> vertexList;
        readonly List<UICharInfo> characterList;
        readonly List<UILineInfo> lineList;
        readonly IList<UIVertex> vertexView;
        readonly IList<UICharInfo> characterView;
        readonly IList<UILineInfo> lineView;

        bool cacheValid;
        string cachedText;
        TextGenerationSettings cachedSettings;
        TextGenerationError cachedError;
        Rect extents;
        int sizeUsed;

        public TextGenerator() : this(50)
        {
        }

        /// initialCapacity: the number of characters the output lists are sized for.
        public TextGenerator(int initialCapacity)
        {
            int characters = Math.Max(0, initialCapacity) + 1;
            vertexList = new List<UIVertex>(characters * 4);
            characterList = new List<UICharInfo>(characters);
            lineList = new List<UILineInfo>(8);
            vertexView = vertexList.AsReadOnly();
            characterView = characterList.AsReadOnly();
            lineView = lineList.AsReadOnly();
        }

        /// The tab stop in multiples of the space advance (TextMesh sets its own).
        internal float TabSize { get; set; } = 4f;

        /// IMGUI's text layout (docs/cleanroom/imgui.md, "GUILayout, measured", part 5): a
        /// dynamic font's advances unrounded, and wrapping that treats a space like any other
        /// character, so a space that does not fit starts the next line.
        internal bool GuiMetrics { get; init; }

        public IList<UIVertex> verts => vertexView;
        public IList<UICharInfo> characters => characterView;
        public IList<UILineInfo> lines => lineView;
        public int vertexCount => vertexList.Count;
        public int characterCount => characterList.Count;
        /// The character infos include one for the caret position after the last character.
        public int characterCountVisible => characterList.Count - 1;
        public int lineCount => lineList.Count;
        public Rect rectExtents => extents;

        float widestLine;

        /// The widest line of the last generation, in pixels (a wrapping text's rectExtents
        /// keeps the wrap width).
        internal float WidestLine => widestLine;
        public int fontSizeUsedForBestFit => sizeUsed;

        public void GetVertices(List<UIVertex> vertices) => CopyInto(vertexList, vertices);
        public void GetCharacters(List<UICharInfo> characters) => CopyInto(characterList, characters);
        public void GetLines(List<UILineInfo> lines) => CopyInto(lineList, lines);
        public UIVertex[] GetVerticesArray() => vertexList.ToArray();
        public UICharInfo[] GetCharactersArray() => characterList.ToArray();
        public UILineInfo[] GetLinesArray() => lineList.ToArray();

        static void CopyInto<T>(List<T> source, List<T> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            destination.AddRange(source);
        }

        /// Marks the cached result stale, so the next Populate regenerates.
        public void Invalidate() => cacheValid = false;

        // The outputs are managed lists; there is nothing to release.
        public void Dispose()
        {
        }

        /// The width of the widest line, unwrapped, in pixels (whole pixels for a dynamic font).
        /// With best fit, unwrapped and unbounded text fits at the largest size (measured).
        public float GetPreferredWidth(string str, TextGenerationSettings settings)
        {
            settings.horizontalOverflow = HorizontalWrapMode.Overflow;
            settings.verticalOverflow = VerticalWrapMode.Overflow;
            settings.updateBounds = true;
            Populate(str, settings);
            return extents.width;
        }

        /// The height of the text's lines in pixels. With best fit, at the largest size whose
        /// wrapped lines break no word (measured: 13 points at 0.86 in 60 units, whose 11-pixel
        /// "conversion." is wider than the line, measure at 12 points).
        public float GetPreferredHeight(string str, TextGenerationSettings settings)
        {
            settings.verticalOverflow = VerticalWrapMode.Overflow;
            settings.updateBounds = true;
            Populate(str, settings);
            return extents.height;
        }

        public bool Populate(string str, TextGenerationSettings settings) => PopulateCached(str, settings) == TextGenerationError.None;

        /// As Populate, logging why generation failed against context. Generation drops size and
        /// style overrides on non-dynamic fonts before laying out, so the two override errors do
        /// not occur in practice; a missing font fails without a message.
        public bool PopulateWithErrors(string str, TextGenerationSettings settings, GameObject context)
        {
            var error = PopulateCached(str, settings);
            if (error == TextGenerationError.None)
                return true;
            if ((error & TextGenerationError.CustomSizeOnNonDynamicFont) != 0)
                Debug.LogError($"Font '{settings.font}' is not dynamic, which is required to override its size", context);
            if ((error & TextGenerationError.CustomStyleOnNonDynamicFont) != 0)
                Debug.LogError($"Font '{settings.font}' is not dynamic, which is required to override its style", context);
            return false;
        }

        TextGenerationError PopulateCached(string str, TextGenerationSettings settings)
        {
            if (cacheValid && str == cachedText && settings.Equals(cachedSettings))
                return cachedError;
            cachedError = Generate(str, settings);
            cachedText = str;
            cachedSettings = settings;
            cacheValid = true;
            return cachedError;
        }

        TextGenerationError Generate(string str, TextGenerationSettings settings)
        {
            vertexList.Clear();
            characterList.Clear();
            lineList.Clear();
            extents = default;
            sizeUsed = 0;
            widestLine = 0f;

            var font = settings.font;
            if (!font)
                return TextGenerationError.NoFont;
            var native = (FontNative)font.NativeOrThrow;

            if (!native.Dynamic)
            {
                // Size and style come from a non-dynamic font's own texture; overrides are dropped
                // with a warning, and generation goes on and succeeds.
                if (settings.fontSize != 0 || settings.fontStyle != FontStyle.Normal)
                {
                    Debug.LogWarning($"Font size and style overrides are only supported for dynamic fonts. Font '{font.name}' is not dynamic.", font);
                    settings.fontSize = 0;
                    settings.fontStyle = FontStyle.Normal;
                }
                if (settings.resizeTextForBestFit)
                {
                    Debug.LogWarning($"BestFit is only supported for dynamic fonts. Font '{font.name}' is not dynamic.", font);
                    settings.resizeTextForBestFit = false;
                }
            }

            int size = native.Dynamic && settings.fontSize > 0 ? settings.fontSize : (int)native.FontSize;
            float scale = settings.scaleFactor > 0f ? settings.scaleFactor : 1f;
            // Unity's native generator takes no null string; it lays out as empty text.
            var layout = new TextLayout(native, str ?? string.Empty, settings, scale, TabSize, GuiMetrics);
            if (settings.resizeTextForBestFit)
                size = LargestFittingSize(layout, settings.resizeTextMinSize, Math.Max(settings.resizeTextMaxSize, settings.resizeTextMinSize));
            else
                layout.Run(size);
            // The pixel size laid out at, with best fit or without (measured).
            sizeUsed = layout.PixelSize(size);

            if (layout.Emit(vertexList, characterList, lineList, out extents))
                Font.InvokeTextureRebuilt_Internal(font);
            widestLine = layout.WidestLine;
            return TextGenerationError.None;
        }

        /// The largest size in [min, max] at which the text fits, or min when none does; the
        /// layout is left run at that size. Bisects, since a larger size never fits where a
        /// smaller one does not.
        static int LargestFittingSize(TextLayout layout, int min, int max)
        {
            int best = min, low = min, high = max, lastRun = int.MinValue;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                layout.Run(mid);
                lastRun = mid;
                if (layout.Fits)
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                    high = mid - 1;
            }
            if (lastRun != best) layout.Run(best);
            return best;
        }
    }
}

namespace OniMoono.Engine.Text
{
    using UnityEngine;

    [Flags]
    internal enum TextGenerationError
    {
        None = 0,
        CustomSizeOnNonDynamicFont = 1,
        CustomStyleOnNonDynamicFont = 2,
        NoFont = 4,
    }

    /// Line breaking and placement of one text for TextGenerator, in pixels (docs/cleanroom/ugui.md,
    /// "Legacy Text glyph placement"): a dynamic font is laid out at floor(size × scale) pixels,
    /// with whole-pixel advances, m_KerningValues scaled to the size, glyph quads of the padded
    /// box, line metrics rounded to whole pixels, and line starts and the block's top rounded.
    internal sealed class TextLayout
    {
        enum Kind : byte { Glyph, Tag, NewLine, Tab, Quad, Hidden }

        struct Item
        {
            public Kind Kind;
            public int CodePoint;
            public int Size;
            public FontStyle Style;
            public Color32 Color;
            public LegacyGlyph Glyph;
            public float Advance;
            /// Dynamic fonts: the kerning with the glyph before, applied to the pen before this
            /// glyph is drawn (its cursor is the pen before it).
            public int Kern;
            /// Quad: uv rectangle.
            public Rect QuadUv;
            public bool Whitespace;
            /// A line may break before this item (CJK ideographs).
            public bool BreakBefore;
        }

        struct Line
        {
            public int Start, End; // item range [Start, End)
            public float Width;
            /// Ascent (baseline below the top), block height (ascent + descent), pitch to the
            /// next line and the line spacing rounded down, in pixels.
            public int Ascent, Block, Pitch, SpacingFloor;
            public bool Wrapped;
        }

        readonly FontNative font;
        readonly string text;
        readonly TextGenerationSettings settings;
        readonly float scale;
        readonly float maxWidth, maxHeight;
        readonly List<Line> lines = new();
        Item[] items = Array.Empty<Item>();
        int keptLines;

        public bool Fits { get; private set; }

        /// The widest laid-out line in pixels (after Emit).
        public float WidestLine { get; private set; }

        readonly float tabSize;
        readonly bool gui;

        public TextLayout(FontNative font, string text, TextGenerationSettings settings, float scale, float tabSize, bool gui = false)
        {
            this.tabSize = tabSize;
            this.gui = gui;
            this.font = font;
            this.text = text;
            this.settings = settings;
            this.scale = scale;
            maxWidth = settings.generationExtents.x * scale;
            maxHeight = settings.generationExtents.y * scale;
        }

        /// A dynamic font is laid out at the size times the scale factor (float), rounded down:
        /// 11 points on the game's 0.86 canvas are 9 pixels (measured).
        public int PixelSize(int size) => font.Dynamic ? Math.Max(1, (int)(size * scale)) : (int)font.FontSize;

        /// Lays the text out at a base font size (unscaled).
        public void Run(int baseSize)
        {
            BuildItems(baseSize);
            BreakLines(PixelSize(baseSize));
            float total = 0f;
            keptLines = 0;
            bool verticalFits = true;
            for (int i = 0; i < lines.Count; i++)
            {
                float height = total + lines[i].Block;
                total += lines[i].Pitch * settings.lineSpacing;
                if (height > maxHeight + 0.001f)
                {
                    verticalFits = false;
                    if (settings.verticalOverflow == VerticalWrapMode.Truncate && !settings.generateOutOfBounds) break;
                }
                keptLines = i + 1;
            }
            bool horizontalFits = true;
            foreach (var line in lines)
                if (line.Width > maxWidth + 0.001f) horizontalFits = false;
            // Each axis counts against a size only when it is bounded (measured: with vertical
            // overflow best fit takes the largest size however many lines wrap; unwrapped, the
            // largest size whatever the width).
            Fits = (verticalFits || settings.verticalOverflow == VerticalWrapMode.Overflow)
                   && (horizontalFits && !brokeWord || settings.horizontalOverflow == HorizontalWrapMode.Overflow);
        }

        bool brokeWord;

        void BuildItems(int baseSize)
        {
            int n = text.Length;
            if (items.Length != n) items = new Item[n];
            else Array.Clear(items);
            var color = (Color32)settings.color;
            int size = baseSize;
            bool bold = settings.fontStyle is FontStyle.Bold or FontStyle.BoldAndItalic;
            bool italic = settings.fontStyle is FontStyle.Italic or FontStyle.BoldAndItalic;
            Stack<Color32> colors = null;
            Stack<int> sizes = null;
            int boldDepth = 0, italicDepth = 0;
            var tags = settings.richText && text.IndexOf('<') >= 0 ? RichText.Parse(text) : null;
            int previousGlyph = -1;
            for (int i = 0; i < n; i++)
            {
                if (tags != null && tags.TryGetValue(i, out var tag))
                {
                    for (int k = i; k < i + tag.Length; k++) items[k].Kind = Kind.Tag;
                    switch (tag.Name)
                    {
                        case "b": boldDepth += tag.Closing ? -1 : 1; break;
                        case "i": italicDepth += tag.Closing ? -1 : 1; break;
                        case "color":
                            colors ??= new Stack<Color32>();
                            if (tag.Closing) { if (colors.Count > 0) color = colors.Pop(); }
                            else { colors.Push(color); color = tag.Color; }
                            break;
                        case "size":
                            sizes ??= new Stack<int>();
                            if (tag.Closing) { if (sizes.Count > 0) size = sizes.Pop(); }
                            else { sizes.Push(size); size = tag.Size; }
                            break;
                        case "quad":
                        {
                            int px = PixelSize(tag.Size > 0 ? tag.Size : size);
                            float aspect = tag.Uv.height > 0f ? tag.Uv.width / tag.Uv.height : 1f;
                            items[i] = new Item { Kind = Kind.Quad, Size = px, Color = color, QuadUv = tag.Uv, Advance = px * aspect };
                            previousGlyph = -1;
                            break;
                        }
                    }
                    i += tag.Length - 1;
                    continue;
                }
                char c = text[i];
                var style = (bold || boldDepth > 0, italic || italicDepth > 0) switch
                {
                    (true, true) => FontStyle.BoldAndItalic,
                    (true, false) => FontStyle.Bold,
                    (false, true) => FontStyle.Italic,
                    _ => FontStyle.Normal,
                };
                int pixelSize = PixelSize(size);
                ref var item = ref items[i];
                item.Size = pixelSize;
                item.Style = style;
                item.Color = color;
                if (c == '\n') { item.Kind = Kind.NewLine; previousGlyph = -1; continue; }
                if (c == '\r') { item.Kind = Kind.Hidden; continue; }
                if (c == '\t') { item.Kind = Kind.Tab; item.Whitespace = true; previousGlyph = -1; continue; }
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(c, text[i + 1]);
                    items[i + 1] = new Item { Kind = Kind.Hidden, Size = pixelSize, Style = style, Color = color };
                }
                item.Kind = Kind.Glyph;
                item.CodePoint = cp;
                item.Glyph = font.GetGlyph(cp, pixelSize, style);
                item.Advance = !item.Glyph.Exists ? 0f : gui && font.Dynamic ? item.Glyph.FineAdvance : item.Glyph.Advance;
                if (!font.Dynamic && item.Glyph.Exists) item.Advance += font.CharacterSpacing;
                item.Whitespace = c == ' ' || c == '​' || c == '　';
                item.BreakBefore = IsIdeograph(cp);
                if (previousGlyph >= 0)
                {
                    if (font.Dynamic) item.Kern = font.PixelKerning(items[previousGlyph].CodePoint, cp, pixelSize);
                    // Bitmap fonts carry imported kerning pairs (pixels at their size).
                    else if (font.Kerning.TryGetValue((items[previousGlyph].CodePoint, cp), out float k)) items[previousGlyph].Advance += k;
                }
                previousGlyph = i;
                if (cp >= 0x10000) i++;
            }
        }

        static bool IsIdeograph(int cp) =>
            (cp >= 0x2E80 && cp <= 0x9FFF) || (cp >= 0xAC00 && cp <= 0xD7AF) || (cp >= 0xF900 && cp <= 0xFAFF)
            || (cp >= 0xFF00 && cp <= 0xFFEF) || (cp >= 0x20000 && cp <= 0x2FFFF);

        float TabStop(float x, int size)
        {
            var space = font.GetGlyph(' ', size, FontStyle.Normal);
            float tab = tabSize * (space.Exists ? (gui && font.Dynamic ? space.FineAdvance : space.Advance) : size * 0.25f);
            return tab <= 0f ? x : (float)(Math.Floor(x / tab) + 1) * tab;
        }

        static bool Advances(Kind kind) => kind is Kind.Glyph or Kind.Quad or Kind.Tab;

        /// The pen's move for an item: its kerning (not at a line's first item) and advance.
        float Step(int k, int lineStart) => items[k].Advance + (k > lineStart && items[k].Kind == Kind.Glyph ? items[k].Kern : 0);

        void BreakLines(int basePixelSize)
        {
            lines.Clear();
            brokeWord = false;
            bool wrap = settings.horizontalOverflow == HorizontalWrapMode.Wrap && maxWidth > 0f;
            int start = 0;
            float x = 0f;
            int lastBreak = -1; // first item of the next line if broken at the last opportunity
            for (int i = 0; i < items.Length; i++)
            {
                ref var item = ref items[i];
                switch (item.Kind)
                {
                    case Kind.NewLine:
                        AddLine(start, i + 1, false, basePixelSize);
                        start = i + 1;
                        x = 0f;
                        lastBreak = -1;
                        continue;
                    case Kind.Tag:
                    case Kind.Hidden:
                        continue;
                    case Kind.Tab:
                        item.Advance = TabStop(x, item.Size) - x;
                        x += item.Advance;
                        lastBreak = i + 1;
                        continue;
                }
                if (item.Whitespace && !gui)
                {
                    // Whitespace never wraps; the line may break after it.
                    x += Step(i, start);
                    lastBreak = i + 1;
                    continue;
                }
                if (item.BreakBefore && i > start) lastBreak = i;
                while (wrap && x + Step(i, start) > maxWidth && HasContent(start, i))
                {
                    int breakAt = lastBreak > start ? lastBreak : i;
                    if (breakAt == i && lastBreak <= start) brokeWord = true;
                    AddLine(start, breakAt, true, basePixelSize);
                    start = breakAt;
                    lastBreak = -1;
                    x = 0f;
                    for (int k = start; k < i; k++) x += Advances(items[k].Kind) ? Step(k, start) : 0f;
                    // IMGUI: the item is checked again against the new line, whose word may
                    // itself be too wide ("X - Close Window" at 35: " " / "Wind" / "ow").
                    if (!gui) break;
                }
                x += Step(i, start);
                // IMGUI: a space wraps like a glyph; the line may break after it.
                if (item.Whitespace) lastBreak = i + 1;
            }
            AddLine(start, items.Length, false, basePixelSize);
        }

        bool HasContent(int start, int end)
        {
            for (int k = start; k < end; k++)
                if (Advances(items[k].Kind)) return true;
            return false;
        }

        void AddLine(int start, int end, bool wrapped, int basePixelSize)
        {
            var line = new Line { Start = start, End = end, Wrapped = wrapped };
            // Width by advances; the whitespace a line was wrapped at does not count.
            int last = end - 1;
            if (wrapped)
                while (last >= start && (items[last].Whitespace || items[last].Kind is Kind.Tag or Kind.Hidden)) last--;
            for (int k = start; k <= last; k++)
                if (Advances(items[k].Kind)) line.Width += Step(k, start);
            // Vertical metrics: the largest size on the line (the base size when empty).
            int size = 0;
            for (int k = start; k < end; k++)
                if (Advances(items[k].Kind)) size = Math.Max(size, items[k].Size);
            if (size == 0) size = basePixelSize;
            font.PixelLineMetrics(size, out line.Ascent, out int descent, out line.Pitch, out line.SpacingFloor);
            line.Block = line.Ascent + descent;
            lines.Add(line);
        }

        // Rounding of a line's start (half up) and of the block's top (half down, toward −y).
        static float RoundX(float v) => MathF.Floor(v + 0.5f);
        static float RoundY(float v) => MathF.Ceiling(v - 0.5f);

        readonly List<GlyphKey> keys = new();

        /// Places the kept lines, requests their glyphs in the font texture and writes the
        /// vertices, character infos and line infos. True when the font texture was rebuilt.
        public bool Emit(List<UIVertex> verts, List<UICharInfo> chars, List<UILineInfo> lineInfos, out Rect extents)
        {
            int count = keptLines;
            bool rebuilt = false;
            if (font.Dynamic)
            {
                keys.Clear();
                for (int l = 0; l < count; l++)
                    for (int k = lines[l].Start; k < lines[l].End; k++)
                        if (items[k].Kind == Kind.Glyph) keys.Add(new GlyphKey(items[k].CodePoint, items[k].Size, items[k].Style));
                rebuilt = font.RequestGlyphs(keys);
            }

            // Block height: the line pitches, the last line by its own block.
            float blockHeight = 0f;
            for (int l = 0; l < count; l++) blockHeight += l == count - 1 ? lines[l].Block : lines[l].Pitch * settings.lineSpacing;
            // Offset from the block top to the first line's top.
            float boxTopOffset = 0f;
            if (settings.alignByGeometry && count > 0)
            {
                // The block spans the glyphs' ink (first line's top to last line's bottom)
                // instead of the font's line boxes.
                GeometryExtent(0, out float firstTop, out _);
                GeometryExtent(count - 1, out _, out float lastBottom);
                float advance = 0f;
                for (int l = 0; l < count - 1; l++) advance += lines[l].Pitch * settings.lineSpacing;
                blockHeight = firstTop - (lastBottom - advance);
                boxTopOffset = -firstTop;
            }
            float widest = 0f;
            for (int l = 0; l < count; l++) widest = Math.Max(widest, LineWidth(l, out _));
            WidestLine = widest;

            // The rect: the extents in pixels plus 0.0001 on each axis. With updateBounds, its
            // width is the widest line's unless the text wraps, and its height the block's
            // unless lines are truncated (measured: the probe's updateBounds generations; the
            // truncated ones were all best fit). The pivot places it and the anchor places the
            // lines in it.
            bool bounds = settings.updateBounds;
            bool wraps = settings.horizontalOverflow == HorizontalWrapMode.Wrap && maxWidth > 0f;
            bool truncates = settings.verticalOverflow == VerticalWrapMode.Truncate;
            float w = bounds && !wraps ? widest : maxWidth + 0.0001f, h = bounds && !truncates ? blockHeight : maxHeight + 0.0001f;
            float xMin = -settings.pivot.x * w, yMax = (1f - settings.pivot.y) * h;
            int anchor = (int)settings.textAnchor;
            int vertical = anchor / 3, horizontal = anchor % 3;
            float blockTop = vertical switch
            {
                0 => RoundY(yMax),
                1 => RoundY(yMax - (h - blockHeight) / 2f),
                _ => RoundY(yMax - h + blockHeight),
            };
            float lineTop = blockTop + boxTopOffset;

            for (int l = 0; l < count; l++)
            {
                var line = lines[l];
                float width = LineWidth(l, out float inkOffset);
                float x = horizontal switch
                {
                    0 => RoundX(xMin),
                    1 => RoundX(xMin + w / 2f) - MathF.Ceiling(width / 2f),
                    _ => RoundX(xMin + w) - width,
                } - inkOffset;
                float baseline = lineTop - line.Ascent;
                float pitch = line.Pitch * settings.lineSpacing;
                lineInfos.Add(new UILineInfo
                {
                    startCharIdx = line.Start,
                    height = line.SpacingFloor,
                    topY = lineTop,
                    leading = pitch - line.Block,
                });
                for (int k = line.Start; k < line.End; k++)
                {
                    ref var item = ref items[k];
                    chars.Add(new UICharInfo { cursorPos = new Vector2(x, lineTop), charWidth = Advances(item.Kind) ? item.Advance : 0f });
                    if (!Advances(item.Kind)) continue;
                    if (item.Kind == Kind.Glyph && k > line.Start) x += item.Kern;
                    if (item.Kind == Kind.Glyph) EmitGlyph(verts, ref item, x, baseline);
                    else if (item.Kind == Kind.Quad) EmitQuad(verts, ref item, x, baseline);
                    x += item.Advance;
                }
                if (l == count - 1) chars.Add(new UICharInfo { cursorPos = new Vector2(x, lineTop), charWidth = 0f });
                lineTop -= pitch;
            }

            extents = bounds && count == 0 ? default : new Rect(0f, 0f, w, h);
            return rebuilt;
        }

        /// A line's width for alignment (by geometry: its ink's), and the offset of that width's
        /// left edge from the pen start.
        float LineWidth(int l, out float inkOffset)
        {
            inkOffset = 0f;
            if (settings.alignByGeometry && LineInk(l, out float inkLeft, out float inkRight))
            {
                inkOffset = inkLeft;
                return inkRight - inkLeft;
            }
            return lines[l].Width;
        }

        /// Ink extent (relative to the line top: top ≥ bottom) of a line's glyphs.
        void GeometryExtent(int l, out float top, out float bottom)
        {
            var line = lines[l];
            top = float.MinValue;
            bottom = float.MaxValue;
            for (int k = line.Start; k < line.End; k++)
            {
                if (items[k].Kind != Kind.Glyph || items[k].Glyph.Height == 0) continue;
                top = Math.Max(top, items[k].Glyph.Top - line.Ascent);
                bottom = Math.Min(bottom, items[k].Glyph.Top - items[k].Glyph.Height - line.Ascent);
            }
            if (top == float.MinValue) { top = 0f; bottom = -line.Block; }
        }

        /// Horizontal ink extent of a line relative to its pen start.
        bool LineInk(int l, out float left, out float right)
        {
            var line = lines[l];
            left = float.MaxValue;
            right = float.MinValue;
            float x = 0f;
            for (int k = line.Start; k < line.End; k++)
            {
                ref var item = ref items[k];
                if (!Advances(item.Kind)) continue;
                if (item.Kind == Kind.Glyph && k > line.Start) x += item.Kern;
                if (item.Kind == Kind.Glyph && item.Glyph.Width > 0)
                {
                    left = Math.Min(left, x + item.Glyph.Left);
                    right = Math.Max(right, x + item.Glyph.Left + item.Glyph.Width);
                }
                x += item.Advance;
            }
            return left != float.MaxValue;
        }

        void EmitGlyph(List<UIVertex> verts, ref Item item, float x, float baseline)
        {
            if (!item.Glyph.Exists) return;
            if (font.Dynamic)
            {
                // Every glyph but the space has a quad: its rendered box grown by the character
                // padding (an empty glyph's is 2 × 2).
                if (item.CodePoint == ' ') return;
                var placed = font.PlacedGlyph(new GlyphKey(item.CodePoint, item.Size, item.Style));
                if (!placed.InTexture) return;
                var uvRect = font.UvOf(placed);
                int pad = font.Padding;
                float left = x + placed.Left - pad, top = baseline + placed.Top + pad;
                AddQuad(verts, left, top - placed.Height - 2 * pad, left + placed.Width + 2 * pad, top,
                    new Vector2(uvRect.xMin, uvRect.yMax), new Vector2(uvRect.xMax, uvRect.yMax), new Vector2(uvRect.xMax, uvRect.yMin), new Vector2(uvRect.xMin, uvRect.yMin), item.Color);
                return;
            }
            var g = item.Glyph;
            if (g.Width == 0 || g.Height == 0) return;
            var uv = g.Uv;
            float x0 = x + g.Left, x1 = x0 + g.Width;
            float y1 = baseline + g.Top, y0 = y1 - g.Height;
            Vector2 tl, tr, br, bl;
            if (g.Flipped)
            {
                // Rotated bitmap-font character: uv x runs down the glyph, uv y across it.
                tl = new Vector2(uv.xMin, uv.yMax);
                tr = new Vector2(uv.xMin, uv.yMin);
                br = new Vector2(uv.xMax, uv.yMin);
                bl = new Vector2(uv.xMax, uv.yMax);
            }
            else
            {
                tl = new Vector2(uv.xMin, uv.yMax);
                tr = new Vector2(uv.xMax, uv.yMax);
                br = new Vector2(uv.xMax, uv.yMin);
                bl = new Vector2(uv.xMin, uv.yMin);
            }
            AddQuad(verts, x0, y0, x1, y1, tl, tr, br, bl, item.Color);
        }

        void EmitQuad(List<UIVertex> verts, ref Item item, float x, float baseline)
        {
            var uv = item.QuadUv;
            AddQuad(verts, x, baseline, x + item.Advance, baseline + item.Size,
                new Vector2(uv.xMin, uv.yMax), new Vector2(uv.xMax, uv.yMax), new Vector2(uv.xMax, uv.yMin), new Vector2(uv.xMin, uv.yMin), item.Color);
        }

        static void AddQuad(List<UIVertex> verts, float x0, float y0, float x1, float y1, Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl, Color32 color)
        {
            var v = UIVertex.simpleVert;
            v.color = color;
            v.position = new Vector3(x0, y1, 0f); v.uv0 = tl; verts.Add(v);
            v.position = new Vector3(x1, y1, 0f); v.uv0 = tr; verts.Add(v);
            v.position = new Vector3(x1, y0, 0f); v.uv0 = br; verts.Add(v);
            v.position = new Vector3(x0, y0, 0f); v.uv0 = bl; verts.Add(v);
        }
    }

    /// Unity's legacy rich text tags: <b>, <i>, <size=N>, <color=…>, <material=N> (each must
    /// be closed by its matching end tag, else it is plain text) and the self-contained
    /// <quad …>. Values may be quoted.
    internal static class RichText
    {
        internal struct Tag
        {
            public string Name;
            public bool Closing;
            public int Length;
            public Color32 Color;
            public int Size;
            public Rect Uv;
        }

        /// Recognized tags by start index.
        public static Dictionary<int, Tag> Parse(string text)
        {
            var found = new List<(int Start, Tag Tag)>();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '<') continue;
                int end = text.IndexOf('>', i + 1);
                if (end < 0) break;
                if (TryParse(text.AsSpan(i + 1, end - i - 1), out var tag))
                {
                    tag.Length = end - i + 1;
                    found.Add((i, tag));
                }
            }
            // Match opening and closing tags (stack per name); quads stand alone.
            var result = new Dictionary<int, Tag>();
            var open = new Dictionary<string, Stack<int>>();
            foreach (var (start, tag) in found)
            {
                if (tag.Name == "quad") { result[start] = tag; continue; }
                if (!tag.Closing)
                {
                    if (!open.TryGetValue(tag.Name, out var stack)) open[tag.Name] = stack = new Stack<int>();
                    stack.Push(start);
                }
                else if (open.TryGetValue(tag.Name, out var stack) && stack.Count > 0)
                {
                    int opening = stack.Pop();
                    result[opening] = found.Find(f => f.Start == opening).Tag;
                    result[start] = tag;
                }
            }
            return result;
        }

        static bool TryParse(ReadOnlySpan<char> body, out Tag tag)
        {
            tag = default;
            if (body.Length == 0) return false;
            if (body[0] == '/')
            {
                var name = body[1..].ToString();
                if (name is not ("b" or "i" or "size" or "color" or "material")) return false;
                tag.Name = name;
                tag.Closing = true;
                return true;
            }
            if (body.SequenceEqual("b") || body.SequenceEqual("i"))
            {
                tag.Name = body.ToString();
                return true;
            }
            int eq = body.IndexOf('=');
            int space = body.IndexOf(' ');
            var key = (eq >= 0 && (space < 0 || eq < space) ? body[..eq] : space >= 0 ? body[..space] : body).ToString();
            string value = eq >= 0 && (space < 0 || eq < space) ? Unquote(ValueAt(body, eq + 1)) : null;
            switch (key)
            {
                case "size":
                    if (value == null || !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float size)) return false;
                    tag.Name = "size";
                    tag.Size = (int)size;
                    return true;
                case "color":
                    if (value == null || !TryParseColor(value, out tag.Color)) return false;
                    tag.Name = "color";
                    return true;
                case "material":
                    if (value == null || !int.TryParse(value, out _)) return false;
                    tag.Name = "material";
                    return true;
                case "quad":
                {
                    tag.Name = "quad";
                    tag.Uv = new Rect(0f, 0f, 1f, 1f);
                    foreach (var (k, v) in Attributes(body[4..]))
                    {
                        float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f);
                        switch (k)
                        {
                            case "size": tag.Size = (int)f; break;
                            case "x": tag.Uv.x = f; break;
                            case "y": tag.Uv.y = f; break;
                            case "width": tag.Uv.width = f; break;
                            case "height": tag.Uv.height = f; break;
                        }
                    }
                    return true;
                }
            }
            return false;
        }

        static string ValueAt(ReadOnlySpan<char> body, int start)
        {
            var rest = body[start..];
            if (rest.Length > 0 && (rest[0] == '"' || rest[0] == '\''))
            {
                int close = rest[1..].IndexOf(rest[0]);
                return close < 0 ? rest.ToString() : rest[..(close + 2)].ToString();
            }
            int space = rest.IndexOf(' ');
            return (space < 0 ? rest : rest[..space]).ToString();
        }

        static string Unquote(string s) => s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0] ? s[1..^1] : s;

        static IEnumerable<(string Key, string Value)> Attributes(ReadOnlySpan<char> s)
        {
            var list = new List<(string, string)>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && s[i] == ' ') i++;
                int eq = s[i..].IndexOf('=');
                if (eq < 0) break;
                var key = s.Slice(i, eq).Trim().ToString();
                var value = ValueAt(s, i + eq + 1);
                list.Add((key, Unquote(value)));
                i += eq + 1 + value.Length;
            }
            return list;
        }

        // Unity's named rich-text colours.
        static readonly Dictionary<string, uint> Named = new(StringComparer.Ordinal)
        {
            ["aqua"] = 0x00ffffff, ["black"] = 0x000000ff, ["blue"] = 0x0000ffff, ["brown"] = 0xa52a2aff,
            ["cyan"] = 0x00ffffff, ["darkblue"] = 0x0000a0ff, ["fuchsia"] = 0xff00ffff, ["green"] = 0x008000ff,
            ["grey"] = 0x808080ff, ["lightblue"] = 0xadd8e6ff, ["lime"] = 0x00ff00ff, ["magenta"] = 0xff00ffff,
            ["maroon"] = 0x800000ff, ["navy"] = 0x000080ff, ["olive"] = 0x808000ff, ["orange"] = 0xffa500ff,
            ["purple"] = 0x800080ff, ["red"] = 0xff0000ff, ["silver"] = 0xc0c0c0ff, ["teal"] = 0x008080ff,
            ["white"] = 0xffffffff, ["yellow"] = 0xffff00ff,
        };

        /// #RGB, #RGBA, #RRGGBB, #RRGGBBAA or a colour name.
        static bool TryParseColor(string value, out Color32 color)
        {
            color = default;
            uint rgba;
            if (value.StartsWith('#'))
            {
                var hex = value[1..];
                if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return false;
                switch (hex.Length)
                {
                    case 3: rgba = Expand((v >> 8) & 0xF) << 24 | Expand((v >> 4) & 0xF) << 16 | Expand(v & 0xF) << 8 | 0xFF; break;
                    case 4: rgba = Expand((v >> 12) & 0xF) << 24 | Expand((v >> 8) & 0xF) << 16 | Expand((v >> 4) & 0xF) << 8 | Expand(v & 0xF); break;
                    case 6: rgba = v << 8 | 0xFF; break;
                    case 8: rgba = v; break;
                    default: return false;
                }
            }
            else if (!Named.TryGetValue(value, out rgba)) return false;
            color = new Color32((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
            return true;

            static uint Expand(uint nibble) => nibble * 17;
        }
    }
}
