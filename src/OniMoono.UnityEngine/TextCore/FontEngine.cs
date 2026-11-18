using System;
using System.Collections.Generic;
using System.IO;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Text;

namespace UnityEngine.TextCore.LowLevel
{
    // Unity's TextCore font engine: one current font face (FreeType) at a point size, glyph
    // queries, atlas packing and rendering into Texture2D atlases, and OpenType feature
    // records. Point sizes are pixels per EM (FreeType at 72 dpi). All entry points are
    // serialized by one lock (Unity's are callable from any thread).
    public partial class FontEngine
    {
        static readonly object s_Gate = new();
        static readonly Dictionary<(object Source, int FaceIndex), FontFace> s_Faces = new();
        static FontFace s_Face;
        static (object Source, int FaceIndex) s_FaceKey;
        static int s_PointSize;

        // Point size used when a face is loaded without one (TMP's default sampling size).
        const int DefaultPointSize = 90;

        internal FontEngine() { }

        // ----- Loading -----

        public static FontEngineError LoadFontFace(string filePath) => LoadFromPath(filePath, DefaultPointSize, 0);

        public static FontEngineError LoadFontFace(string filePath, int pointSize) => LoadFromPath(filePath, pointSize, 0);

        public static FontEngineError LoadFontFace(string filePath, float pointSize, int faceIndex)
            => LoadFromPath(filePath, (int)Math.Round(pointSize, MidpointRounding.AwayFromZero), faceIndex);

        public static FontEngineError LoadFontFace(byte[] sourceFontFile)
            => sourceFontFile.Length == 0 ? FontEngineError.Invalid_File : Load(sourceFontFile, () => sourceFontFile, DefaultPointSize, 0);

        public static FontEngineError LoadFontFace(byte[] sourceFontFile, int pointSize)
            => sourceFontFile.Length == 0 ? FontEngineError.Invalid_File : Load(sourceFontFile, () => sourceFontFile, pointSize, 0);

        public static FontEngineError LoadFontFace(byte[] sourceFontFile, float pointSize, int faceIndex)
            => sourceFontFile.Length == 0
                ? FontEngineError.Invalid_File
                : Load(sourceFontFile, () => sourceFontFile, (int)Math.Round(pointSize, MidpointRounding.AwayFromZero), faceIndex);

        public static FontEngineError LoadFontFace(Font font) => LoadFromFont(font, DefaultPointSize, 0);

        public static FontEngineError LoadFontFace(Font font, int pointSize) => LoadFromFont(font, pointSize, 0);

        public static FontEngineError LoadFontFace(Font font, float pointSize, int faceIndex)
            => LoadFromFont(font, (int)Math.Round(pointSize, MidpointRounding.AwayFromZero), faceIndex);

        public static FontEngineError LoadFontFace(string familyName, string styleName)
            => SystemFonts.TryFind(familyName, styleName, out var r) ? LoadFromPath(r.filePath, DefaultPointSize, r.faceIndex) : FontEngineError.Invalid_Face;

        public static FontEngineError LoadFontFace(string familyName, string styleName, float pointSize)
            => SystemFonts.TryFind(familyName, styleName, out var r)
                ? LoadFromPath(r.filePath, (int)Math.Round(pointSize, MidpointRounding.AwayFromZero), r.faceIndex)
                : FontEngineError.Invalid_Face;

        static FontEngineError LoadFromPath(string filePath, int pointSize, int faceIndex)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return FontEngineError.Invalid_File_Path;
            var full = Path.GetFullPath(filePath);
            return Load(full, () => File.ReadAllBytes(full), pointSize, faceIndex);
        }

        static FontEngineError LoadFromFont(Font font, int pointSize, int faceIndex)
        {
            if (!font) return FontEngineError.Invalid_File;
            var native = (FontNative)font.NativeOrThrow;
            var data = native.FaceData;
            if (data == null || data.Length == 0) return FontEngineError.Invalid_File;
            // Keyed by instance id (stable across unload and reload), like Unity's face cache,
            // which does not keep the Font loaded.
            return Load(native.InstanceId, () => data, pointSize, faceIndex);
        }

        /// Makes the face of `source` current at `pointSize`, opening it on first use; faces
        /// stay cached per source until unloaded.
        static FontEngineError Load(object source, Func<byte[]> read, int pointSize, int faceIndex)
        {
            lock (s_Gate)
            {
                var key = (source, faceIndex);
                if (!s_Faces.TryGetValue(key, out var face))
                {
                    var error = FontFace.TryOpen(read(), faceIndex, out face);
                    if (error != FontEngineError.Success) return error;
                    s_Faces[key] = face;
                }
                var sizeError = face.SetPixelSize(pointSize);
                if (sizeError != FontEngineError.Success) return sizeError;
                s_Face = face;
                s_FaceKey = key;
                s_PointSize = pointSize;
                return FontEngineError.Success;
            }
        }

        /// Unloads the current face (it is removed from the cache).
        public static FontEngineError UnloadFontFace()
        {
            lock (s_Gate)
            {
                if (s_Face == null) return FontEngineError.Success;
                s_Faces.Remove(s_FaceKey);
                s_Face.Dispose();
                s_Face = null;
                s_FaceKey = default;
                return FontEngineError.Success;
            }
        }

        public static FontEngineError SetFaceSize(int pointSize)
        {
            lock (s_Gate)
            {
                if (s_Face == null) return FontEngineError.Invalid_Face;
                var error = s_Face.SetPixelSize(pointSize);
                if (error == FontEngineError.Success) s_PointSize = pointSize;
                return error;
            }
        }

        // Observed in Unity's player (tools/UnityProbe, PROBE_SCENARIO=fontasset): an empty or null
        // style finds a face of the family even when none is called "Regular" ("DejaVu Sans" gives
        // its Book face); a family's Regular face when it has one. Which face it takes otherwise
        // differs by family there (DejaVu Sans Mono gives Bold); this takes the first.
        internal static bool TryGetSystemFontReference(string familyName, string styleName, out FontReference fontRef)
            => string.IsNullOrEmpty(styleName)
                ? SystemFonts.TryFindFamily(familyName, out fontRef)
                : SystemFonts.TryFind(familyName, styleName, out fontRef);

        // ----- Face information -----

        /// Metrics of the current face at its point size (default when no face is loaded).
        /// Vertical metrics are the face's design values scaled to the point size, unhinted.
        public static FaceInfo GetFaceInfo()
        {
            lock (s_Gate)
            {
                var face = s_Face;
                var info = default(FaceInfo);
                if (face == null) return info;
                float s = (float)s_PointSize / face.UnitsPerEm;
                info.faceIndex = face.FaceIndex;
                info.familyName = face.FamilyName;
                info.styleName = face.StyleName;
                info.pointSize = s_PointSize;
                info.scale = 1f;
                info.unitsPerEM = face.UnitsPerEm;
                info.lineHeight = face.Height * s;
                info.ascentLine = face.Ascender * s;
                info.descentLine = face.Descender * s;
                info.baseline = 0f;
                bool hasOs2 = face.Tables.TryGetOs2(out var os2);
                // Cap and x heights are the tops of the hinted 'H' and 'x' (whole pixels), as Unity's
                // player gives them (tools/UnityProbe's fontasset scenario: DejaVu Sans Book at 60
                // has 44 and 33; the game's LiberationSans SDF at 86 has 59 and 45). Without the
                // glyph, OS/2's heights (version 2+).
                info.capLine = GlyphTop(face, 'H') ?? (hasOs2 && os2.HasHeights ? os2.CapHeight * s : 0f);
                info.meanLine = GlyphTop(face, 'x') ?? (hasOs2 && os2.HasHeights ? os2.XHeight * s : 0f);
                if (hasOs2)
                {
                    info.superscriptOffset = os2.SuperscriptYOffset * s;
                    info.superscriptSize = (float)os2.SuperscriptYSize / face.UnitsPerEm;
                    info.subscriptOffset = -os2.SubscriptYOffset * s;
                    info.subscriptSize = (float)os2.SubscriptYSize / face.UnitsPerEm;
                    info.strikethroughOffset = os2.StrikeoutPosition * s;
                    info.strikethroughThickness = os2.StrikeoutSize * s;
                }
                info.underlineOffset = face.UnderlinePosition * s;
                info.underlineThickness = face.UnderlineThickness * s;
                // The hinted space's advance (19 for DejaVu Sans Book at 60, 24 for LiberationSans
                // at 86).
                info.tabWidth = GlyphAdvance(face, ' ');
                return info;
            }
        }

        static unsafe float? GlyphTop(FontFace face, char c)
        {
            uint index = face.GlyphIndex(c);
            if (index == 0 || !face.Load(index, FT.LOAD_DEFAULT | FT.LOAD_NO_BITMAP)) return null;
            return face.Slot->metrics.horiBearingY.Value / 64f;
        }

        static unsafe float GlyphAdvance(FontFace face, char c)
        {
            uint index = face.GlyphIndex(c);
            if (index == 0 || !face.Load(index, FT.LOAD_DEFAULT | FT.LOAD_NO_BITMAP)) return 0f;
            return face.Slot->metrics.horiAdvance.Value / 64f;
        }

        /// "Family - Style" for each face of the current font file; null when there is none.
        public static string[] GetFontFaces()
        {
            lock (s_Gate)
            {
                if (s_Face == null) return null;
                int count = s_Face.FaceCount;
                var names = new string[count];
                for (int i = 0; i < count; i++)
                {
                    if (i == s_Face.FaceIndex)
                    {
                        names[i] = s_Face.FamilyName + " - " + s_Face.StyleName;
                        continue;
                    }
                    if (FontFace.TryOpen(s_Face.Data, i, out var other) != FontEngineError.Success) { names[i] = ""; continue; }
                    names[i] = other.FamilyName + " - " + other.StyleName;
                    other.Dispose();
                }
                return names.Length == 0 ? null : names;
            }
        }

        internal static uint GetGlyphIndex(uint unicode)
        {
            lock (s_Gate) return s_Face?.GlyphIndex(unicode) ?? 0u;
        }

        internal static uint GetVariantGlyphIndex(uint unicode, uint variantSelectorUnicode)
        {
            lock (s_Gate) return s_Face?.VariantGlyphIndex(unicode, variantSelectorUnicode) ?? 0u;
        }

        public static bool TryGetGlyphIndex(uint unicode, out uint glyphIndex)
        {
            glyphIndex = GetGlyphIndex(unicode);
            return glyphIndex != 0;
        }

        // ----- Glyphs -----

        public static bool TryGetGlyphWithUnicodeValue(uint unicode, GlyphLoadFlags flags, out Glyph glyph)
        {
            lock (s_Gate)
            {
                glyph = null;
                if (s_Face == null) return false;
                uint index = s_Face.GlyphIndex(unicode);
                return index != 0 && TryLoadGlyph(index, flags, out glyph);
            }
        }

        public static bool TryGetGlyphWithIndexValue(uint glyphIndex, GlyphLoadFlags flags, out Glyph glyph)
        {
            lock (s_Gate)
            {
                glyph = null;
                return s_Face != null && TryLoadGlyph(glyphIndex, flags, out glyph);
            }
        }

        /// The glyph's FreeType metrics (pixels at the point size, or design units with
        /// LOAD_NO_SCALE); no atlas rect.
        static unsafe bool TryLoadGlyph(uint index, GlyphLoadFlags flags, out Glyph glyph)
        {
            glyph = null;
            if (!s_Face.Load(index, (int)flags)) return false;
            var m = s_Face.Slot->metrics;
            float k = (flags & GlyphLoadFlags.LOAD_NO_SCALE) != 0 ? 1f : 1f / 64f;
            glyph = new Glyph(index, new GlyphMetrics(m.width.Value * k, m.height.Value * k, m.horiBearingX.Value * k, m.horiBearingY.Value * k, m.horiAdvance.Value * k), GlyphRect.zero, 1f, 0)
            {
                classDefinitionType = (GlyphClassDefinitionType)s_Face.Tables.GlyphClass(index),
            };
            return true;
        }

        // ----- Atlas -----

        internal static bool TryAddGlyphToTexture(uint glyphIndex, int padding, GlyphPackingMode packingMode, List<GlyphRect> freeGlyphRects, List<GlyphRect> usedGlyphRects, GlyphRenderMode renderMode, Texture2D texture, out Glyph glyph)
        {
            lock (s_Gate)
            {
                glyph = null;
                var atlas = AtlasOf(texture);
                if (s_Face == null || atlas == null) return false;
                if (!TryAddGlyph(atlas, glyphIndex, padding, packingMode, freeGlyphRects, usedGlyphRects, renderMode, out glyph)) return false;
                atlas.ContentChanged();
                return true;
            }
        }

        /// Adds each glyph that fits (in list order); true when all of them were added. The
        /// returned array lists the added glyphs followed by a null terminator.
        internal static bool TryAddGlyphsToTexture(List<uint> glyphIndexes, int padding, GlyphPackingMode packingMode, List<GlyphRect> freeGlyphRects, List<GlyphRect> usedGlyphRects, GlyphRenderMode renderMode, Texture2D texture, out Glyph[] glyphs)
        {
            glyphs = null;
            if (glyphIndexes == null || glyphIndexes.Count == 0) return false;
            lock (s_Gate)
            {
                var added = new List<Glyph>(glyphIndexes.Count);
                var atlas = AtlasOf(texture);
                bool all = s_Face != null && atlas != null;
                if (all)
                {
                    foreach (uint index in glyphIndexes)
                    {
                        if (TryAddGlyph(atlas, index, padding, packingMode, freeGlyphRects, usedGlyphRects, renderMode, out var g)) added.Add(g);
                        else all = false;
                    }
                    atlas.ContentChanged();
                }
                glyphs = new Glyph[added.Count + 1];
                added.CopyTo(glyphs);
                return all;
            }
        }

        // Glyphs rendered ahead by RenderGlyphsAhead, by face, size, glyph, mode and padding.
        static readonly Dictionary<(FontFace Face, int Size, uint Glyph, GlyphRenderMode Mode, int Padding), GlyphBitmap> s_Rendered = new();

        /// Renders glyphs of the current face that are about to be added one by one (the
        /// distance fields in parallel), for TryAddGlyphToTexture to take instead of rendering
        /// them itself; ForgetGlyphsAhead drops what is left. A glyph renders the same either way.
        internal static void RenderGlyphsAhead(uint[] glyphIndexes, int padding, GlyphRenderMode renderMode)
        {
            lock (s_Gate)
            {
                if (s_Face == null || glyphIndexes == null || glyphIndexes.Length < 2) return;
                var bitmaps = GlyphRasterizer.RenderAll(s_Face, glyphIndexes, renderMode, padding);
                for (int i = 0; i < glyphIndexes.Length; i++)
                    if (bitmaps[i] != null) s_Rendered[(s_Face, s_PointSize, glyphIndexes[i], renderMode, padding)] = bitmaps[i];
            }
        }

        internal static void ForgetGlyphsAhead()
        {
            lock (s_Gate) s_Rendered.Clear();
        }

        /// Clears an atlas texture to zero and uploads it.
        internal static void ResetAtlasTexture(Texture2D texture)
        {
            lock (s_Gate)
            {
                var atlas = AtlasOf(texture);
                if (atlas == null) return;
                atlas.Memory.Span.Clear();
                atlas.ContentChanged();
            }
        }

        static Texture2DNative AtlasOf(Texture2D texture)
        {
            if (!texture) return null;
            var t = (Texture2DNative)texture.NativeOrThrow;
            if (!t.Readable || t.Width <= 0 || t.Height <= 0) return null;
            if (t.Memory == null) t.Allocate(t.SliceSize, 0);
            return t;
        }

        /// Renders one glyph and packs it: the packed rectangle is the glyph plus `padding`
        /// texels on every side; glyphRect is the glyph area inside it. Empty glyphs (spaces)
        /// get a zero rect and use no atlas space.
        static bool TryAddGlyph(Texture2DNative atlas, uint glyphIndex, int padding, GlyphPackingMode packingMode, List<GlyphRect> free, List<GlyphRect> used, GlyphRenderMode renderMode, out Glyph glyph)
        {
            glyph = null;
            if (!s_Rendered.Remove((s_Face, s_PointSize, glyphIndex, renderMode, padding), out var bitmap))
                bitmap = GlyphRasterizer.Render(s_Face, glyphIndex, renderMode, padding);
            if (bitmap == null) return false;
            bool sdf = GlyphRasterizer.IsSdf(renderMode);
            int w = sdf ? Math.Max(bitmap.Width - 2 * padding, 0) : bitmap.Width;
            int h = sdf ? Math.Max(bitmap.Height - 2 * padding, 0) : bitmap.Height;
            var classType = (GlyphClassDefinitionType)s_Face.Tables.GlyphClass(glyphIndex);
            if (w == 0 || h == 0)
            {
                glyph = new Glyph(glyphIndex, new GlyphMetrics(0f, 0f, bitmap.BearingX, bitmap.BearingY, bitmap.Advance), GlyphRect.zero, 1f, 0) { classDefinitionType = classType };
                return true;
            }
            if (!GlyphPacker.TryPack(w + 2 * padding, h + 2 * padding, packingMode, atlas.Width, atlas.Height, free, used, out var placed)) return false;
            if (sdf) Blit(atlas, bitmap, placed.x, placed.y);
            else Blit(atlas, bitmap, placed.x + padding, placed.y + padding);
            glyph = new Glyph(glyphIndex, new GlyphMetrics(w, h, bitmap.BearingX, bitmap.BearingY, bitmap.Advance), new GlyphRect(placed.x + padding, placed.y + padding, w, h), 1f, 0)
            {
                classDefinitionType = classType,
            };
            return true;
        }

        /// Writes a glyph bitmap (rows top-down) into level 0 with its bottom-left texel at
        /// (x, y); texture rows are bottom-up. Texels outside the texture are skipped.
        internal static void Blit(Texture2DNative atlas, GlyphBitmap bitmap, int x, int y)
        {
            var mip = atlas.Memory.Slice(atlas.MipOffset(0), atlas.MipSize(0));
            int tw = atlas.Width, th = atlas.Height;
            for (int row = 0; row < bitmap.Height; row++)
            {
                int ty = y + bitmap.Height - 1 - row;
                if (ty < 0 || ty >= th) continue;
                for (int col = 0; col < bitmap.Width; col++)
                {
                    int tx = x + col;
                    if (tx < 0 || tx >= tw) continue;
                    int src = row * bitmap.Width + col;
                    switch (atlas.Format)
                    {
                        case TextureFormat.Alpha8:
                            mip[ty * tw + tx] = bitmap.Rgba ? bitmap.Pixels[4 * src + 3] : bitmap.Pixels[src];
                            break;
                        case TextureFormat.RGBA32:
                        {
                            int o = 4 * (ty * tw + tx);
                            if (bitmap.Rgba)
                            {
                                for (int c = 0; c < 4; c++) mip[o + c] = bitmap.Pixels[4 * src + c];
                            }
                            else
                            {
                                mip[o] = mip[o + 1] = mip[o + 2] = 255;
                                mip[o + 3] = bitmap.Pixels[src];
                            }
                            break;
                        }
                        default:
                            if (!TextureFormats.CanEncode(atlas.Format)) return;
                            var color = bitmap.Rgba
                                ? (Color)new Color32(bitmap.Pixels[4 * src], bitmap.Pixels[4 * src + 1], bitmap.Pixels[4 * src + 2], bitmap.Pixels[4 * src + 3])
                                : new Color(1f, 1f, 1f, bitmap.Pixels[src] / 255f);
                            TextureFormats.Encode(atlas.Format, mip, tw, tx, ty, color);
                            break;
                    }
                }
            }
        }

        // ----- OpenType features -----

        static GlyphPairAdjustmentRecord PairRecord(uint first, SfntTables.ValueRecord v1, uint second, SfntTables.ValueRecord v2) =>
            new(new GlyphAdjustmentRecord(first, new GlyphValueRecord(v1.XPlacement, v1.YPlacement, v1.XAdvance, v1.YAdvance)),
                new GlyphAdjustmentRecord(second, new GlyphValueRecord(v2.XPlacement, v2.YPlacement, v2.XAdvance, v2.YAdvance)));

        /// Every pair adjustment of the GPOS 'kern' feature, in design units (TMP scales them).
        internal static GlyphPairAdjustmentRecord[] GetAllPairAdjustmentRecords()
        {
            lock (s_Gate)
            {
                if (s_Face == null) return null;
                var pairs = s_Face.Tables.PairAdjustments((_, _) => true);
                var records = new GlyphPairAdjustmentRecord[pairs.Count];
                for (int i = 0; i < pairs.Count; i++) records[i] = PairRecord(pairs[i].First, pairs[i].V1, pairs[i].Second, pairs[i].V2);
                return records;
            }
        }

        /// GPOS pair adjustments between glyphs of the list (both glyphs in it); null when there
        /// are none, else the records followed by an empty terminator record.
        internal static GlyphPairAdjustmentRecord[] GetPairAdjustmentRecords(List<uint> glyphIndexes)
        {
            lock (s_Gate)
            {
                if (s_Face == null || glyphIndexes == null) return null;
                var set = new HashSet<uint>(glyphIndexes);
                var pairs = s_Face.Tables.PairAdjustments((a, b) => set.Contains(a) && set.Contains(b));
                if (pairs.Count == 0) return null;
                var records = new GlyphPairAdjustmentRecord[pairs.Count + 1];
                for (int i = 0; i < pairs.Count; i++) records[i] = PairRecord(pairs[i].First, pairs[i].V1, pairs[i].Second, pairs[i].V2);
                return records;
            }
        }

        /// The records GetPairAdjustmentRecords(glyphIndexes) gives that have `glyphIndex` (one
        /// of glyphIndexes) as their first or second glyph, in its order and terminated alike,
        /// without expanding the font's other pairs: dynamic font assets ask this per glyph.
        internal static GlyphPairAdjustmentRecord[] GetPairAdjustmentRecordsOf(uint glyphIndex, IEnumerable<uint> glyphIndexes)
        {
            lock (s_Gate)
            {
                if (s_Face == null || glyphIndexes == null) return null;
                var set = new HashSet<uint>(glyphIndexes);
                if (!set.Contains(glyphIndex)) return null;
                var pairs = s_Face.Tables.PairAdjustmentsOf(glyphIndex, set);
                if (pairs.Count == 0) return null;
                var records = new GlyphPairAdjustmentRecord[pairs.Count + 1];
                for (int i = 0; i < pairs.Count; i++) records[i] = PairRecord(pairs[i].First, pairs[i].V1, pairs[i].Second, pairs[i].V2);
                return records;
            }
        }

        /// Pairs of the legacy 'kern' table between the given glyphs, as x-advance adjustments
        /// of the first glyph scaled to the current point size; null when there are none, else
        /// terminated by an empty record.
        internal static GlyphPairAdjustmentRecord[] GetGlyphPairAdjustmentTable(uint[] glyphIndexes)
        {
            lock (s_Gate)
            {
                if (s_Face == null || glyphIndexes == null) return null;
                var set = new HashSet<uint>(glyphIndexes);
                float s = (float)s_PointSize / s_Face.UnitsPerEm;
                var list = new List<GlyphPairAdjustmentRecord>();
                foreach (var ((left, right), value) in s_Face.Tables.KernPairs())
                {
                    if (value == 0 || !set.Contains(left) || !set.Contains(right)) continue;
                    list.Add(new GlyphPairAdjustmentRecord(new GlyphAdjustmentRecord(left, new GlyphValueRecord(0f, 0f, value * s, 0f)), new GlyphAdjustmentRecord(right, default)));
                }
                if (list.Count == 0) return null;
                list.Add(default);
                return list.ToArray();
            }
        }

        static MarkToBaseAdjustmentRecord MarkToBase((uint Base, (short X, short Y) BaseAnchor, uint Mark, (short X, short Y) MarkAnchor) r) => new()
        {
            baseGlyphID = r.Base,
            baseGlyphAnchorPoint = new GlyphAnchorPoint { xCoordinate = r.BaseAnchor.X, yCoordinate = r.BaseAnchor.Y },
            markGlyphID = r.Mark,
            markPositionAdjustment = new MarkPositionAdjustment(r.MarkAnchor.X, r.MarkAnchor.Y),
        };

        static MarkToMarkAdjustmentRecord MarkToMark((uint Base, (short X, short Y) BaseAnchor, uint Mark, (short X, short Y) MarkAnchor) r) => new()
        {
            baseMarkGlyphID = r.Base,
            baseMarkGlyphAnchorPoint = new GlyphAnchorPoint { xCoordinate = r.BaseAnchor.X, yCoordinate = r.BaseAnchor.Y },
            combiningMarkGlyphID = r.Mark,
            combiningMarkPositionAdjustment = new MarkPositionAdjustment(r.MarkAnchor.X, r.MarkAnchor.Y),
        };

        /// GPOS 'mark' attachments (base anchor and mark anchor, design units).
        internal static MarkToBaseAdjustmentRecord[] GetAllMarkToBaseAdjustmentRecords()
        {
            lock (s_Gate)
            {
                if (s_Face == null) return null;
                return s_Face.Tables.MarkAttachments(false, (_, _) => true).ConvertAll(MarkToBase).ToArray();
            }
        }

        /// Mark-to-base records whose base and mark are both in the list; null when none,
        /// else terminated by an empty record.
        internal static MarkToBaseAdjustmentRecord[] GetMarkToBaseAdjustmentRecords(List<uint> glyphIndexes)
        {
            lock (s_Gate)
            {
                if (s_Face == null || glyphIndexes == null) return null;
                var set = new HashSet<uint>(glyphIndexes);
                var list = s_Face.Tables.MarkAttachments(false, (b, m) => set.Contains(b) && set.Contains(m)).ConvertAll(MarkToBase);
                if (list.Count == 0) return null;
                list.Add(default);
                return list.ToArray();
            }
        }

        /// GPOS 'mkmk' attachments (base mark anchor and combining mark anchor, design units).
        internal static MarkToMarkAdjustmentRecord[] GetAllMarkToMarkAdjustmentRecords()
        {
            lock (s_Gate)
            {
                if (s_Face == null) return null;
                return s_Face.Tables.MarkAttachments(true, (_, _) => true).ConvertAll(MarkToMark).ToArray();
            }
        }

        /// Mark-to-mark records whose two marks are both in the list; null when none, else
        /// terminated by an empty record.
        internal static MarkToMarkAdjustmentRecord[] GetMarkToMarkAdjustmentRecords(List<uint> glyphIndexes)
        {
            lock (s_Gate)
            {
                if (s_Face == null || glyphIndexes == null) return null;
                var set = new HashSet<uint>(glyphIndexes);
                var list = s_Face.Tables.MarkAttachments(true, (b, m) => set.Contains(b) && set.Contains(m)).ConvertAll(MarkToMark);
                if (list.Count == 0) return null;
                list.Add(default);
                return list.ToArray();
            }
        }

        static LigatureSubstitutionRecord Ligature((uint[] Components, uint Ligature) l) => new() { componentGlyphIDs = l.Components, ligatureGlyphID = l.Ligature };

        /// GSUB 'liga' ligatures.
        internal static LigatureSubstitutionRecord[] GetAllLigatureSubstitutionRecords()
        {
            lock (s_Gate)
            {
                if (s_Face == null) return null;
                return s_Face.Tables.Ligatures(_ => true).ConvertAll(Ligature).ToArray();
            }
        }

        /// Ligatures having the glyph among their components; null when none, else terminated
        /// by an empty record.
        internal static LigatureSubstitutionRecord[] GetLigatureSubstitutionRecords(uint glyphIndex)
            => GetLigatureSubstitutionRecords(new List<uint> { glyphIndex });

        /// Ligatures having any of the glyphs among their components; null when none, else
        /// terminated by an empty record.
        internal static LigatureSubstitutionRecord[] GetLigatureSubstitutionRecords(List<uint> glyphIndexes)
        {
            lock (s_Gate)
            {
                if (s_Face == null || glyphIndexes == null) return null;
                var set = new HashSet<uint>(glyphIndexes);
                var list = s_Face.Tables.Ligatures(ids => Array.Exists(ids, set.Contains)).ConvertAll(Ligature);
                if (list.Count == 0) return null;
                list.Add(default);
                return list.ToArray();
            }
        }
    }
}
