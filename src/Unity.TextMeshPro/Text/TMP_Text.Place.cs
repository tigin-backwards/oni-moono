using System;
using UnityEngine;
using UnityEngine.TextCore;

namespace TMPro
{
    // Placement: the laid-out lines aligned in the rect, and the character, line, word, link and
    // page records of textInfo.
    public abstract partial class TMP_Text
    {
        // Where the visible part ends (the overflow modes and the visibility limits).
        int m_FirstHiddenLine = int.MaxValue;

        void Place(Rect rect)
        {
            var info = textInfo;
            var parsed = m_Parsed;
            var pl = s_Placements;
            var lines = s_Lines;
            int count = m_LayoutCharacterCount;
            int lineCount = s_LineCount;
            info.Clear();
            info.EnsureCharacters(count + 2);
            info.EnsureLines(lineCount + 1);
            info.characterCount = count;
            info.lineCount = lineCount;
            info.materialCount = parsed.Materials.Count;
            // An empty text keeps the last overflow; everything else is cleared.
            if (!m_SourceEmpty) m_firstOverflowCharacterIndex = m_LayoutOverflowIndex;
            m_IsTextTruncated = false;

            // Which lines are shown: the page on display, and none after an overflow that hides.
            int overflowLine = -1;
            if (m_LayoutOverflowIndex >= 0)
                for (int k = 0; k < lineCount; k++)
                    if (lines[k].First <= m_LayoutOverflowIndex && m_LayoutOverflowIndex <= lines[k].Last) { overflowLine = k; break; }
            int pages = lineCount == 0 ? 1 : lines[lineCount - 1].Page + 1;
            int shownPage = m_overflowMode == TextOverflowModes.Page ? Mathf.Clamp(m_pageToDisplay - 1, 0, pages - 1) : -1;
            m_FirstHiddenLine = lineCount;
            // Truncate and Ellipsis cut the text at the earlier of the overflow and the cut at the
            // width (the overflow's when they fall on the same character).
            int vertical = m_LayoutOverflowIndex, horizontal = m_LayoutHorizontalCut;
            if ((m_overflowMode == TextOverflowModes.Truncate || m_overflowMode == TextOverflowModes.Ellipsis) && (vertical >= 0 || horizontal >= 0))
            {
                bool atWidth = vertical < 0 || (horizontal >= 0 && horizontal < vertical);
                // What follows a cut at the width is never laid out: it does not overflow.
                if (atWidth) m_firstOverflowCharacterIndex = -1;
                Cut(atWidth ? horizontal : vertical, ref count, ref lineCount);
                info.EnsureCharacters(count + 1);
                info.EnsureLines(lineCount + 1);
                info.characterCount = count;
                info.lineCount = lineCount;
                m_FirstHiddenLine = lineCount;
                m_IsTextTruncated = true;
            }
            else if (m_overflowMode == TextOverflowModes.Linked && overflowLine >= 0)
            {
                // The first line is shown even when it alone overflows.
                m_FirstHiddenLine = Mathf.Max(overflowLine, 1);
                if (overflowLine == 0 && lineCount > 0 && pl[lines[0].First].Visible == false) m_FirstHiddenLine = 1;
            }
            if (m_MaxVisibleLines < m_FirstHiddenLine) m_FirstHiddenLine = Mathf.Max(0, m_MaxVisibleLines);

            int firstLine = 0, lastLine = Mathf.Min(lineCount, m_FirstHiddenLine) - 1;
            if (shownPage >= 0)
            {
                firstLine = -1;
                for (int k = 0; k < lineCount; k++)
                    if (lines[k].Page == shownPage) { if (firstLine < 0) firstLine = k; lastLine = k; }
                if (firstLine < 0) firstLine = 0;
            }

            // Vertical alignment of the shown lines.
            float top = rect.yMax - m_margin.y, bottom = rect.yMin + m_margin.w;
            float offsetY = 0f;
            if (lineCount > 0 && lastLine >= firstLine)
            {
                float textTop = lines[firstLine].Baseline + lines[firstLine].Ascender;
                float textBottom = lines[lastLine].Baseline + lines[lastLine].Descender;
                switch (m_VerticalAlignment)
                {
                    case VerticalAlignmentOptions.Top: offsetY = top - textTop; break;
                    case VerticalAlignmentOptions.Middle: offsetY = (top + bottom) * 0.5f - (textTop + textBottom) * 0.5f; break;
                    case VerticalAlignmentOptions.Bottom: offsetY = bottom - textBottom; break;
                    case VerticalAlignmentOptions.Baseline: offsetY = (top + bottom) * 0.5f - lines[firstLine].Baseline; break;
                    case VerticalAlignmentOptions.Geometry:
                    {
                        GlyphExtentsY(firstLine, lastLine, out float gTop, out float gBottom);
                        offsetY = (top + bottom) * 0.5f - (gTop + gBottom) * 0.5f;
                        break;
                    }
                    case VerticalAlignmentOptions.Capline:
                    {
                        float cap = CapLineOf(firstLine);
                        offsetY = (top + bottom) * 0.5f - (lines[firstLine].Baseline + cap * 0.5f);
                        break;
                    }
                    default: offsetY = top - textTop; break;
                }
            }


            // Right-to-left lines mirror their pen positions.
            if (m_isRightToLeft)
                for (int k = 0; k < lineCount; k++)
                {
                    ref var line = ref lines[k];
                    float end = line.StartX + line.Length;
                    for (int i = line.First; i <= line.Last; i++)
                    {
                        ref var p = ref pl[i];
                        p.X = line.StartX + end - (p.X + p.GlyphAdvance);
                    }
                }

            // Horizontal alignment per line.
            float left = rect.xMin + m_margin.x;
            for (int k = 0; k < lineCount; k++)
            {
                ref var line = ref lines[k];
                // Lines align in their width plus 0.0001 (the tolerance that lets a line fill its
                // width exactly).
                float available = line.Width + 0.0001f;
                // A tab counts to its end (measured: a centred "\t" spans -T/2 to T/2).
                float used = line.StartX + Mathf.Max(line.LastVisible >= 0 ? line.Length : 0f, line.TabEnd);
                var align = line.Align;
                bool lastOfParagraph = line.EndsWithLineFeed || k == lineCount - 1;
                float extra = available - used;
                line.OffsetX = 0f;
                switch (align)
                {
                    case HorizontalAlignmentOptions.Center: line.OffsetX = extra * 0.5f; break;
                    case HorizontalAlignmentOptions.Right: line.OffsetX = extra; break;
                    case HorizontalAlignmentOptions.Geometry:
                    {
                        GlyphExtentsX(k, out float gl, out float gr);
                        line.OffsetX = available * 0.5f - (gl + gr) * 0.5f;
                        break;
                    }
                    case HorizontalAlignmentOptions.Justified:
                    case HorizontalAlignmentOptions.Flush:
                        if ((align == HorizontalAlignmentOptions.Flush || !lastOfParagraph) && extra > 0f) Justify(ref line, extra);
                        break;
                }
            }

            // Records.
            var chars = info.characterInfo;
            m_TextExtents = Extents.uninitialized;
            int visibleSeen = 0;
            for (int k = 0; k < lineCount; k++)
            {
                ref var line = ref lines[k];
                // Positions are computed relative to the line (the pen from its start, baselines
                // from the first line's) and offset into the rect afterwards.
                float offsetX = left + (line.MarginLeft + line.OffsetX);
                float lineLeft = offsetX;
                float baseline = line.Baseline + offsetY;
                bool shown = k >= firstLine && k <= lastLine;
                var extents = Extents.uninitialized;
                int visibleOnLine = 0, spacesOnLine = 0;
                for (int i = line.First; i <= line.Last && i < count; i++)
                {
                    ref var p = ref pl[i];
                    ref var el = ref parsed.Elements[p.Element];
                    ref var ci = ref chars[i];
                    ci = default;
                    if (p.Blank)
                    {
                        // The record where truncation stopped: U+0003, at the source index of
                        // the character it stopped at and the pen position there, not drawn
                        // (measured on the shipped package; the input field's caret goes there).
                        // It reports the text's font asset.
                        float at = p.X + offsetX, baselineAt = line.Baseline + offsetY;
                        ci.character = (char)3;
                        ci.fontAsset = m_fontAsset;
                        ci.index = el.SourceIndex;
                        ci.lineNumber = k;
                        ci.pageNumber = line.Page;
                        ci.origin = ci.xAdvance = at;
                        ci.baseLine = baselineAt;
                        ci.ascender = baselineAt + line.Ascender;
                        ci.descender = baselineAt + line.Descender;
                        ci.bottomLeft = ci.topLeft = ci.topRight = ci.bottomRight = new Vector3(at, baselineAt, 0f);
                        ci.vertexIndex = -1;
                        continue;
                    }
                    ci.elementType = el.Kind == ElementKind.Sprite ? TMP_TextElementType.Sprite : TMP_TextElementType.Character;
                    // A soft hyphen drawn where its line breaks reports a hyphen-minus (measured on the
                    // shipped package).
                    ci.character = p.SoftHyphen > 0f && p.Visible ? '-' : (char)el.Unicode;
                    ci.index = el.SourceIndex;
                    ci.stringLength = el.SourceLength;
                    ci.textElement = el.Element;
                    ci.fontAsset = el.Asset as TMP_FontAsset ?? el.State.Font ?? m_fontAsset;
                    ci.material = parsed.Materials[el.MaterialIndex].Material;
                    ci.materialReferenceIndex = el.MaterialIndex;
                    ci.isUsingAlternateTypeface = el.AlternateTypeface;
                    ci.pointSize = p.PointSize;
                    ci.lineNumber = k;
                    ci.pageNumber = line.Page;
                    ci.scale = p.Scale;
                    ci.style = el.State.Style;
                    float pen = p.X + p.JustifyOffset;
                    float baselineRel = line.Baseline + p.BaselineShift;
                    p.Pen = pen;
                    p.BaselineRel = baselineRel;
                    p.OffsetX = offsetX;
                    p.OffsetY = offsetY;
                    float origin = pen + offsetX;
                    ci.origin = origin;
                    // The advance includes the spacing after the character (measured on the
                    // shipped package).
                    ci.xAdvance = (float)((double)pen + p.Advance) + offsetX;
                    ci.baseLine = baselineRel + offsetY;
                    // White space of its own font after a visible character reports the line's
                    // ascender and descender (measured on the shipped package).
                    bool lineMetrics = p.Excluded;
                    ci.ascender = (float)((double)line.Baseline + (lineMetrics ? line.Ascender : p.Ascender)) + offsetY;
                    ci.descender = (float)((double)line.Baseline + (lineMetrics ? line.Descender : p.Descender)) + offsetY;
                    float x0, x1, y0, y1;
                    // A soft hyphen that is not drawn has its corners at its origin.
                    if (p.SoftHyphen > 0f && !p.Visible)
                    {
                        x0 = x1 = origin;
                        y0 = y1 = ci.baseLine;
                    }
                    else if (el.Kind == ElementKind.Character && el.Element?.glyph != null)
                    {
                        // A character's corners are its quad's: the glyph's box with the
                        // material's padding (and synthesized bold's).
                        GlyphQuad(ref p, ref el, ci.fontAsset, out x0, out x1, out y0, out y1);
                    }
                    else
                    {
                        x0 = (float)((double)pen + p.MonoOffset + p.Left) + offsetX;
                        x1 = (float)((double)pen + p.MonoOffset + p.Right) + offsetX;
                        y1 = (float)((double)line.Baseline + p.Top + p.KernY) + offsetY;
                        y0 = (float)((double)line.Baseline + p.Bottom + p.KernY) + offsetY;
                    }
                    float shear = p.Italic && !el.AlternateTypeface ? ItalicShear(ci.fontAsset) : 0f;
                    float pivot = shear != 0f && ci.fontAsset != null ? ci.fontAsset.faceInfo.capLine * 0.5f * p.FontScale : 0f;
                    ci.bottomLeft = new Vector3(x0 + shear * (p.Bottom - p.BaselineShift - pivot), y0, 0f);
                    ci.topLeft = new Vector3(x0 + shear * (p.Top - p.BaselineShift - pivot), y1, 0f);
                    ci.topRight = new Vector3(x1 + shear * (p.Top - p.BaselineShift - pivot), y1, 0f);
                    ci.bottomRight = new Vector3(x1 + shear * (p.Bottom - p.BaselineShift - pivot), y0, 0f);
                    ci.aspectRatio = y1 - y0 != 0f ? (x1 - x0) / (y1 - y0) : 0f;
                    ci.color = CharacterColor(ref el);
                    ci.underlineColor = Opaque(el.State.UnderlineColor, ci.color);
                    ci.strikethroughColor = Opaque(el.State.StrikethroughColor, ci.color);
                    ci.highlightState = el.State.Highlight;
                    ci.highlightColor = el.State.Highlight.color;
                    ci.vertexIndex = -1;
                    bool visible = p.Visible && shown && IsShown(i, k, visibleSeen);
                    if (p.Visible) visibleSeen++;
                    ci.isVisible = visible;
                    if (p.Visible)
                    {
                        visibleOnLine++;
                        // A line's extents span its characters' quads and its ascender to
                        // descender.
                        extents.min.x = Mathf.Min(extents.min.x, x0);
                        extents.min.y = Mathf.Min(extents.min.y, ci.descender);
                        extents.max.x = Mathf.Max(extents.max.x, x1);
                        extents.max.y = Mathf.Max(extents.max.y, ci.ascender);
                    }
                    // Line feeds count as spaces.
                    if (p.Whitespace) spacesOnLine++;
                    // The text's space count leaves carriage returns out; every other
                    // white-space character counts one.
                    if (p.Whitespace && el.Unicode != 13) info.spaceCount++;
                    // The text's extents (textBounds, the rendered size) span the visible
                    // characters' advances and their ascenders to descenders.
                    if (visible)
                    {
                        m_TextExtents.min.x = Mathf.Min(m_TextExtents.min.x, ci.origin);
                        m_TextExtents.min.y = Mathf.Min(m_TextExtents.min.y, ci.descender);
                        m_TextExtents.max.x = Mathf.Max(m_TextExtents.max.x, ci.xAdvance);
                        m_TextExtents.max.y = Mathf.Max(m_TextExtents.max.y, ci.ascender);
                    }
                }
                ref var li = ref info.lineInfo[k];
                li = default;
                li.firstCharacterIndex = line.First;
                li.lastCharacterIndex = line.Last;
                li.characterCount = line.Last - line.First + 1;
                li.firstVisibleCharacterIndex = line.FirstVisible >= 0 ? line.FirstVisible : line.First;
                li.lastVisibleCharacterIndex = line.LastVisible >= 0 ? line.LastVisible : line.First;
                li.visibleCharacterCount = visibleOnLine;
                li.spaceCount = spacesOnLine;
                li.visibleSpaceCount = spacesOnLine;
                li.ascender = baseline + line.Ascender;
                li.baseline = baseline;
                li.descender = baseline + line.Descender;
                li.lineHeight = line.Height;
                // maxAdvance is where the last visible character's glyph advance ends (in the
                // rect, without its spacing); length is from the line's start to the right edge of
                // that character's glyph (synthesized bold widening it), without padding (both
                // measured on the shipped package).
                if (line.LastVisible >= 0)
                {
                    ref var last = ref pl[line.LastVisible];
                    float end = (float)((double)last.Pen + last.GlyphAdvance) + last.OffsetX;
                    li.maxAdvance = end;
                    ref var lastEl = ref parsed.Elements[last.Element];
                    float right;
                    if (lastEl.Kind == ElementKind.Character && lastEl.Element?.glyph != null)
                    {
                        var gm = lastEl.Element.glyph.metrics;
                        float bold = last.Bold ? BoldPadding(chars[line.LastVisible].fontAsset) : 0f;
                        right = (float)((double)last.Pen + last.MonoOffset + ((double)gm.horizontalBearingX + gm.width + bold) * last.Scale * last.XScale) + last.OffsetX;
                    }
                    else right = (float)((double)last.Pen + last.MonoOffset + last.Right) + last.OffsetX;
                    // From the line's left edge, its indent included (measured on the shipped
                    // package: "<indent=1em>•Polluted Dirt" in NotoSans 12 pt is 87.125 long,
                    // "•Polluted Dirt" 75.125).
                    li.length = right - lineLeft;
                }
                else
                {
                    li.maxAdvance = 0f;
                    li.length = 0f;
                }
                li.width = line.Width;
                li.marginLeft = line.MarginLeft;
                li.marginRight = line.MarginRight;
                li.alignment = line.Align;
                li.lineExtents = extents.max.x < extents.min.x ? Extents.zero : extents;
            }
            if (m_TextExtents.max.x < m_TextExtents.min.x) m_TextExtents = Extents.zero;

            if (m_FirstHiddenLine < lineCount) m_IsTextTruncated = true;

            RecordWords(count);
            RecordLinks(count);
            RecordPages(lineCount, pages);

            if (m_overflowMode == TextOverflowModes.Linked && m_linkedTextComponent != null)
            {
                int next = m_FirstHiddenLine < lineCount ? lines[m_FirstHiddenLine].First : count;
                var linked = m_linkedTextComponent;
                if (linked.text != m_text) linked.text = m_text;
                linked.firstVisibleCharacter = next;
            }
        }

        // Whether character i (on line k, the n-th visible one) is within the visibility limits.
        bool IsShown(int i, int line, int visibleIndex)
        {
            if (i < m_firstVisibleCharacter) return false;
            if (visibleIndex >= m_MaxVisibleCharacters) return false;
            if (line >= m_MaxVisibleLines) return false;
            return true;
        }

        static Color32 Opaque(Color32 tag, Color32 fallback) => tag.a == 0 && tag.r == 0 && tag.g == 0 && tag.b == 0 ? fallback : tag;

        // The vertex colour of a character: the text's colour or a colour tag, with <alpha>,
        // and for sprites white unless tinted.
        Color32 CharacterColor(ref ParsedElement el)
        {
            var state = el.State;
            Color32 c = m_overrideHtmlColors || !state.ColorFromTag ? m_fontColor32 : state.Color;
            if (!m_overrideHtmlColors && state.ColorFromTag)
            {
                // A tag's colour keeps the text's alpha unless the tag gives one.
                c = state.Color;
            }
            if (state.HasAlphaOverride) c.a = state.AlphaOverride;
            if (el.Kind == ElementKind.Sprite)
            {
                if (el.HasSpriteColor) return el.SpriteColor;
                if (!(m_tintAllSprites || el.SpriteTint)) return new Color32(255, 255, 255, c.a);
            }
            return c;
        }

        float ItalicShear(TMP_FontAsset font) => (font != null ? font.italicStyle : 35) * 0.01f;

        void Justify(ref LineData line, float extra)
        {
            var pl = s_Placements;
            int spaces = 0, glyphs = 0;
            for (int i = line.First; i <= line.LastVisible && i >= 0; i++)
            {
                if (pl[i].Whitespace && !pl[i].LineBreak) spaces++;
                else if (pl[i].Visible) glyphs++;
            }
            float ratio = m_wordWrappingRatios;
            float perSpace = 0f, perGlyph = 0f;
            if (spaces > 0)
            {
                perSpace = extra * (1f - ratio) / spaces;
                perGlyph = glyphs > 1 ? extra * ratio / (glyphs - 1) : 0f;
                if (glyphs <= 1) perSpace = extra / spaces;
            }
            else if (glyphs > 1) perGlyph = extra / (glyphs - 1);
            float acc = 0f;
            bool firstGlyph = true;
            for (int i = line.First; i <= line.Last; i++)
            {
                ref var p = ref pl[i];
                if (p.Visible)
                {
                    if (!firstGlyph) acc += perGlyph;
                    firstGlyph = false;
                }
                p.JustifyOffset = acc;
                if (p.Whitespace && !p.LineBreak && i < line.LastVisible) acc += perSpace;
            }
        }

        void GlyphExtentsY(int firstLine, int lastLine, out float top, out float bottom)
        {
            var pl = s_Placements;
            var lines = s_Lines;
            top = float.NegativeInfinity;
            bottom = float.PositiveInfinity;
            for (int k = firstLine; k <= lastLine; k++)
                for (int i = lines[k].First; i <= lines[k].Last; i++)
                {
                    if (!pl[i].Visible) continue;
                    top = Mathf.Max(top, lines[k].Baseline + pl[i].Top);
                    bottom = Mathf.Min(bottom, lines[k].Baseline + pl[i].Bottom);
                }
            if (float.IsInfinity(top))
            {
                top = lines[firstLine].Baseline + lines[firstLine].Ascender;
                bottom = lines[lastLine].Baseline + lines[lastLine].Descender;
            }
        }

        void GlyphExtentsX(int lineIndex, out float left, out float right)
        {
            var pl = s_Placements;
            ref var line = ref s_Lines[lineIndex];
            left = float.PositiveInfinity;
            right = float.NegativeInfinity;
            for (int i = line.First; i <= line.Last; i++)
            {
                if (!pl[i].Visible) continue;
                left = Mathf.Min(left, pl[i].X + pl[i].MonoOffset + pl[i].Left);
                right = Mathf.Max(right, pl[i].X + pl[i].MonoOffset + pl[i].Right);
            }
            if (float.IsInfinity(left)) left = right = 0f;
        }

        float CapLineOf(int lineIndex)
        {
            var pl = s_Placements;
            ref var line = ref s_Lines[lineIndex];
            float cap = 0f;
            for (int i = line.First; i <= line.Last; i++)
            {
                var font = m_Parsed.Elements[pl[i].Element].Asset as TMP_FontAsset ?? m_fontAsset;
                cap = Mathf.Max(cap, font.faceInfo.capLine * pl[i].FontScale);
            }
            return cap;
        }

        static int LineOf(int index, int lineCount)
        {
            var lines = s_Lines;
            for (int k = 0; k < lineCount; k++)
                if (lines[k].First <= index && index <= lines[k].Last) return k;
            return lineCount - 1;
        }

        // Truncate and Ellipsis at the cut c (the overflowing character, or the one cut at the
        // width). At the first character nothing is shown. Otherwise the characters before c stay
        // as laid out and c becomes the blank record, on c's line; when that line began by
        // wrapping (not after a line feed), the record stays at the end of the line before (the
        // characters of a word that wrapped onto it stay there too, as far as they fit, as the
        // shipped package lays out character by character), and the text has one line fewer.
        // Ellipsis puts "…" in place of the last character before the cut whose line fits the
        // rect's height and at whose pen "…" fits the width, followed by the blank record; when
        // no character qualifies, nothing is shown.
        void Cut(int c, ref int count, ref int lineCount)
        {
            if (c <= 0)
            {
                count = 0;
                lineCount = 0;
                return;
            }
            var lines = s_Lines;
            int k = LineOf(c, lineCount);
            int recordLine = k, stop = c;
            float recordX;
            if (k > 0 && c == lines[k].First && !lines[k - 1].EndsWithLineFeed)
            {
                ref var prev = ref lines[k - 1];
                ref var next = ref lines[k];
                var pl0 = s_Placements;
                // The line goes on: a soft hyphen drawn where it broke is hidden again
                // (measured on the shipped package).
                ref var end = ref pl0[prev.Last];
                if (end.SoftHyphen > 0f && end.Visible)
                {
                    end.Visible = false;
                    end.GlyphAdvance = end.Advance = 0f;
                }
                float x = pl0[prev.Last].X + pl0[prev.Last].Advance;
                for (; stop < count && stop <= next.Last; stop++)
                {
                    ref var p = ref pl0[stop];
                    if (p.LineBreak || (p.Visible && x + p.GlyphAdvance > prev.Width + 0.0001f)) break;
                    p.X = x;
                    x += p.Advance;
                }
                recordLine = k - 1;
                recordX = x;
                prev.Last = stop - 1;
                // The characters it goes on with are checked on that line as it stands.
                int none = -1;
                CheckSoFar(k - 1, lines[0].Ascender, m_LayoutHeight, m_LayoutBaseSize, ref none);
            }
            else
            {
                var pl0 = s_Placements;
                recordX = c > lines[k].First ? pl0[c - 1].X + pl0[c - 1].Advance : lines[k].StartX;
            }
            if (s_Placements.Length <= stop + 1) Array.Resize(ref s_Placements, stop + 2);
            var pl = s_Placements;
            if (m_overflowMode == TextOverflowModes.Ellipsis && m_Parsed.Ellipsis.Element != null)
            {
                var ellipsis = MeasureEllipsis(lines[recordLine].Width, out float fit);
                int at = stop - 1, atLine = recordLine;
                for (; at >= 0; at--)
                {
                    while (atLine > 0 && at < lines[atLine].First) atLine--;
                    ref var line = ref lines[atLine];
                    if (!pl[at].Below && pl[at].X + fit <= line.Width + 0.0001f) break;
                }
                if (at < 0)
                {
                    count = 0;
                    lineCount = 0;
                    return;
                }
                ellipsis.X = pl[at].X;
                ReplaceWithEllipsis(ref pl[at], ellipsis);
                pl[at + 1] = new Placement { Blank = true, Element = pl[at + 1].Element, X = pl[at].X + pl[at].Advance };
                ref var shown = ref lines[atLine];
                shown.Last = at + 1;
                shown.EndsWithLineFeed = false;
                FinishLine(ref shown, pl, m_LayoutBaseSize);
                Rebaseline(atLine);
                count = at + 2;
                lineCount = atLine + 1;
                return;
            }
            pl[stop] = new Placement { Blank = true, Element = pl[stop].Element, X = recordX };
            ref var last = ref lines[recordLine];
            last.Last = stop;
            last.EndsWithLineFeed = false;
            FinishLine(ref last, pl, m_LayoutBaseSize);
            Rebaseline(recordLine);
            count = stop + 1;
            lineCount = recordLine + 1;
        }

        // The line the cut ends is laid out with its characters before the cut only: its
        // baseline follows from its ascender as it stood there (measured on the shipped package:
        // a taller character after the cut does not lower it).
        void Rebaseline(int k)
        {
            ref var line = ref s_Lines[k];
            if (k > 0) line.Baseline = s_Lines[k - 1].Baseline - LineInterval(k, line.MaxAscent, m_LayoutBaseSize);
            line.LineHeight = line.Ascender - line.Descender;
        }

        // The ellipsis, as an element after the parsed ones. Its glyph (from whichever font has
        // it) is scaled by the text's own font (measured on the shipped package: a fallback's
        // ellipsis is drawn at the primary font's scale, unlike a typed one).
        // The ellipsis takes the place of a character, and its source position (measured: the
        // shipped package records the replaced character's index for it).
        void ReplaceWithEllipsis(ref Placement replaced, Placement ellipsis)
        {
            ref var source = ref m_Parsed.Elements[replaced.Element];
            ref var e = ref m_Parsed.Elements[ellipsis.Element];
            e.SourceIndex = source.SourceIndex;
            e.SourceLength = source.SourceLength;
            replaced = ellipsis;
        }

        // fit: the advance "…" needs to fit where it goes, that of its glyph at its own font's
        // scale (measured on the shipped package: GRAYSTROKE 12 pt's "…", from a fallback, needs
        // 7.078125, as a typed "…" is wide).
        Placement MeasureEllipsis(float width, out float fit)
        {
            var parsed = m_Parsed;
            int index = parsed.Count;
            if (parsed.Elements.Length <= index) Array.Resize(ref parsed.Elements, index + 1);
            parsed.Elements[index] = parsed.Ellipsis;
            var ellipsis = default(Placement);
            Measure(ref parsed.Elements[index], ref ellipsis, m_LayoutBaseSize, width);
            fit = ellipsis.GlyphAdvance;
            ellipsis = default;
            Measure(ref parsed.Elements[index], ref ellipsis, m_LayoutBaseSize, width, parsed.Ellipsis.State.Font ?? m_fontAsset);
            ellipsis.Element = index;
            return ellipsis;
        }

        // Letters (any L category) and decimal digits, and four hyphens: U+002D, the soft hyphen
        // U+00AD, U+2010 and U+2011 (measured: "Words and their separators").
        static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '-' || c == '\u00AD' || c == '\u2010' || c == '\u2011';

        static bool IsJoiner(char c) => c == '\'' || c == '\u2019';

        // Punctuation is any P category, except that § and ¶ count as symbols.
        static bool IsWordPunctuation(char c) => char.IsPunctuation(c) && c != '\u00A7' && c != '\u00B6';

        static int[] s_WordEnds = new int[8];

        void RecordWords(int count)
        {
            var info = textInfo;
            var chars = info.characterInfo;
            int words = 0;
            int start = -1;
            // Each word is counted on the line of the character that ended it.
            void Add(int first, int last, int endedBy)
            {
                info.EnsureWords(words + 1);
                if (s_WordEnds.Length < words + 1) Array.Resize(ref s_WordEnds, Mathf.NextPowerOfTwo(words + 1));
                info.wordInfo[words] = new TMP_WordInfo { textComponent = this, firstCharacterIndex = first, lastCharacterIndex = last, characterCount = last - first + 1 };
                s_WordEnds[words] = endedBy;
                words++;
            }
            bool LetterOrDigit(int i) => chars[i].elementType != TMP_TextElementType.Sprite && char.IsLetterOrDigit(chars[i].character);
            for (int i = 0; i < count; i++)
            {
                char c = chars[i].character;
                bool sprite = chars[i].elementType == TMP_TextElementType.Sprite;
                if (!sprite && IsWordCharacter(c))
                {
                    if (start < 0) start = i;
                    if (i == count - 1) Add(start, i, i);
                    continue;
                }
                // An apostrophe joins between letters or digits. At the end of the text it reads
                // the character slot past it, which holds what an earlier layout left there; when
                // that is a letter or digit the word never ends (measured on the shipped package).
                if (!sprite && IsJoiner(c) && i > 0 && LetterOrDigit(i - 1) && i + 1 < chars.Length && LetterOrDigit(i + 1))
                    continue;
                if (start >= 0)
                {
                    Add(start, i - 1, i);
                    start = -1;
                }
                // A text that starts with a separator that is not punctuation (white space, a
                // symbol, a sprite), or is a single character that is not a word character,
                // begins with an empty word (first 0, last -1).
                else if (i == 0 && (sprite || !IsWordPunctuation(c) || count == 1))
                    Add(0, -1, 0);
            }
            // A text without words is one word, with no record: the first keeps the previous
            // layout's.
            info.wordCount = words == 0 && count > 0 ? 1 : words;
            int lineCount = info.lineCount;
            for (int k = 0; k < lineCount; k++) info.lineInfo[k].wordCount = 0;
            for (int j = 0, k = 0; j < words; j++)
            {
                int at = s_WordEnds[j];
                while (k < lineCount - 1 && info.lineInfo[k].lastCharacterIndex < at) k++;
                if (k < lineCount) info.lineInfo[k].wordCount++;
            }
        }

        void RecordLinks(int count)
        {
            var info = textInfo;
            var links = m_Parsed.Links;
            info.linkCount = 0;
            if (links.Count == 0) return;
            info.EnsureLinks(links.Count);
            var chars = info.characterInfo;
            for (int l = 0; l < links.Count; l++)
            {
                int first = -1, last = -1;
                for (int i = 0; i < count; i++)
                {
                    if (m_Parsed.Elements[s_Placements[i].Element].State.Link != l) continue;
                    if (first < 0) first = i;
                    last = i;
                }
                ref var li = ref info.linkInfo[l];
                li = default;
                li.textComponent = this;
                li.hashCode = TextHash.Of(links[l].Id);
                li.linkIdFirstCharacterIndex = links[l].IdSourceIndex;
                li.linkIdLength = links[l].IdLength;
                li.linkTextfirstCharacterIndex = first < 0 ? 0 : first;
                li.linkTextLength = first < 0 ? 0 : last - first + 1;
            }
            info.linkCount = links.Count;
        }

        // The ID of the parsed link whose tag starts at that source index.
        internal string LinkId(int idSourceIndex, int hash)
        {
            string fallback = null;
            foreach (var link in m_Parsed.Links)
            {
                if (link.IdSourceIndex != idSourceIndex) continue;
                if (TextHash.Of(link.Id) == hash) return link.Id;
                fallback ??= link.Id;
            }
            return fallback ?? "";
        }

        void RecordPages(int lineCount, int pages)
        {
            var info = textInfo;
            var lines = s_Lines;
            info.EnsurePages(pages);
            info.pageCount = lineCount == 0 ? 0 : pages;
            for (int pg = 0; pg < pages && lineCount > 0; pg++)
            {
                int first = -1, last = -1;
                for (int k = 0; k < lineCount; k++)
                    if (lines[k].Page == pg) { if (first < 0) first = k; last = k; }
                if (first < 0) continue;
                info.pageInfo[pg] = new TMP_PageInfo
                {
                    firstCharacterIndex = lines[first].First,
                    lastCharacterIndex = lines[last].Last,
                    ascender = info.lineInfo[first].ascender,
                    baseLine = info.lineInfo[first].baseline,
                    descender = info.lineInfo[last].descender,
                };
            }
        }
    }
}
