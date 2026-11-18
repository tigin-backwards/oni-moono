using System;
using System.Collections.Generic;
using System.IO;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace OniMoono.Engine.Text
{
    /// A legacy (TextRendering) glyph at one pixel size and style: layout metrics in pixels
    /// (y up from the baseline) and, once requested, its place in the font texture.
    internal struct LegacyGlyph
    {
        public bool Exists;
        public float Advance;
        /// Dynamic fonts: the advance unrounded, the linear (unhinted) one, units × size / em
        /// (plus what synthesized bold adds). IMGUI lays text out with it.
        public float FineAdvance;
        /// Dynamic fonts: the box of the bitmap FreeType renders (bitmap_left, bitmap_top,
        /// width, rows), without the font's character padding. Bitmap fonts: the character rect.
        public int Left, Top, Width, Height;
        /// Texture placement when InTexture: the bottom-left texel of the glyph's rect, which
        /// for dynamic fonts includes the character padding on every side.
        public bool InTexture;
        public int X, Y;
        /// UV rectangle for non-dynamic (bitmap) fonts, from the character rects.
        public Rect Uv;
        public bool Flipped;
        public int LastUsedFrame;
    }

    internal readonly record struct GlyphKey(int CodePoint, int Size, FontStyle Style);

    /// Engine side of UnityEngine.Font (native class 128): the serialized font (TTF/OTF data,
    /// names, metrics at m_FontSize, character rects of bitmap fonts) and, for dynamic fonts,
    /// the glyph cache rendered into the font texture.
    internal sealed class FontNative : NativeObject
    {
        // Unity's m_ConvertCase values.
        public const int DynamicFont = -2, UnicodeSet = -1, DontConvertCase = 0, UpperCase = 1, LowerCase = 2;

        public float LineSpacing;
        public Material Material;
        public float FontSize;
        public Texture2D Texture;
        public int AsciiStartOffset;
        public float Tracking = 1f;
        public int CharacterSpacing;
        public int CharacterPadding = 1;
        public int ConvertCase = DontConvertCase;
        public Dictionary<int, LegacyGlyph> CharacterRects = new();
        public Dictionary<(int, int), float> Kerning = new();
        public float PixelScale = 0.1f;
        public byte[] Data = Array.Empty<byte>();
        public float Ascent, Descent;
        public uint DefaultStyle;
        public string[] FontNames = Array.Empty<string>();
        public Font[] Fallbacks = Array.Empty<Font>();
        public int FontRenderingMode;
        public bool ShouldRoundAdvanceValue = true;

        public bool Dynamic => ConvertCase == DynamicFont;

        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            LineSpacing = f.Float("m_LineSpacing");
            Material = resolver.Resolve(f.PPtr("m_DefaultMaterial")) as Material;
            FontSize = f.Float("m_FontSize");
            Texture = resolver.Resolve(f.PPtr("m_Texture")) as Texture2D;
            AsciiStartOffset = f.Int("m_AsciiStartOffset");
            Tracking = f.Float("m_Tracking");
            CharacterSpacing = f.Int("m_CharacterSpacing");
            CharacterPadding = f.Int("m_CharacterPadding");
            ConvertCase = f.Int("m_ConvertCase");
            foreach (Fields c in f.Array("m_CharacterRects"))
            {
                var uv = c.Struct("uv");
                var vert = c.Struct("vert");
                // vert is relative to the pen position with y up; a negative height extends
                // downwards from vert.y.
                float vx = vert.Float("x"), vy = vert.Float("y"), vw = vert.Float("width"), vh = vert.Float("height");
                float top = Math.Max(vy, vy + vh), bottom = Math.Min(vy, vy + vh);
                CharacterRects[(int)c.Get<uint>("index")] = new LegacyGlyph
                {
                    Exists = true,
                    Advance = c.Float("advance"),
                    Left = (int)Math.Floor(Math.Min(vx, vx + vw)),
                    Top = (int)Math.Ceiling(top),
                    Width = (int)Math.Round(Math.Abs(vw)),
                    Height = (int)Math.Round(top - bottom),
                    Uv = new Rect(uv.Float("x"), uv.Float("y"), uv.Float("width"), uv.Float("height")),
                    Flipped = c.Bool("flipped"),
                };
            }
            foreach (Fields pair in f.Array("m_KerningValues"))
            {
                var chars = pair.Struct("first");
                Kerning[(chars.Get<ushort>("first"), chars.Get<ushort>("second"))] = pair.Float("second");
            }
            PixelScale = f.Float("m_PixelScale");
            Data = f.Get<byte[]>("m_FontData");
            Ascent = f.Float("m_Ascent");
            Descent = f.Float("m_Descent");
            DefaultStyle = f.Get<uint>("m_DefaultStyle");
            var names = f.Array("m_FontNames");
            FontNames = new string[names.Length];
            for (int i = 0; i < names.Length; i++) FontNames[i] = (string)names[i];
            var fallbacks = new List<Font>();
            foreach (PPtr p in f.Array("m_FallbackFonts"))
                if (resolver.Resolve(p) is Font fallback) fallbacks.Add(fallback);
            Fallbacks = fallbacks.ToArray();
            FontRenderingMode = f.Int("m_FontRenderingMode");
            ShouldRoundAdvanceValue = f.Bool("m_ShouldRoundAdvanceValue");
        }

        // ----- Font data -----

        byte[] faceData;
        bool faceDataResolved;

        /// The TrueType/OpenType data the TextCore font engine loads for this font (TMP font
        /// assets made from it): the font's own data; for a font without data (built without
        /// "Include Font Data", or Unity's built-in LegacyRuntime), the installed OS font named
        /// in m_FontNames, else the built-in default face, Liberation Sans (LegacyRuntime always
        /// takes it here; unmeasured). The legacy text generator's glyphs come from the glyph
        /// chain below instead (measured).
        public byte[] FaceData
        {
            get
            {
                if (faceDataResolved) return faceData;
                if (Data.Length > 0) faceData = Data;
                else
                {
                    if (Source.File != "Resources/unity default resources")
                        foreach (var name in FontNames)
                            if (SystemFonts.TryFindFamily(name, out var r))
                            {
                                faceData = File.ReadAllBytes(r.filePath);
                                faceIndex = r.faceIndex;
                                break;
                            }
                    faceData ??= BuiltinFont.Data;
                }
                faceDataResolved = true;
                return faceData;
            }
        }

        int faceIndex;

        // ----- Glyphs (dynamic fonts) -----

        readonly object gate = new();
        List<GlyphChainLink> chain;
        readonly Dictionary<GlyphKey, LegacyGlyph> glyphs = new();
        readonly SkylinePacker packer = new();

        /// The empty texels around each glyph of a dynamic font (m_CharacterPadding): its glyph
        /// boxes and texture rects are the rendered bitmap grown by this on every side.
        public int Padding => Dynamic ? Math.Max(CharacterPadding, 0) : 0;

        /// The families a font without data falls back to, glyph by glyph, when none of its
        /// m_FontNames is installed (docs/cleanroom/ugui.md, "Legacy Text glyph placement").
        /// Measured on Linux only: FreeSans, then DejaVu Sans, then Liberation Sans; with none of
        /// them installed a glyph has no face. On Windows and macOS (unmeasured: Arial, the
        /// built-in font's m_FontNames, is installed there, so the names are found first) no
        /// default chain is known; the last resort there is the built-in Liberation Sans this
        /// engine used before, so that text stays visible.
        static readonly string[] LinuxDefaultChain = { "FreeSans", "DejaVu Sans", "Liberation Sans" };

        /// Where this font's glyphs come from, in order; each glyph from the first link whose
        /// face (in the glyph's style) has it:
        /// - a font with data: its data, then its m_FallbackFonts' faces;
        /// - a font without data: the first of m_FontNames installed (exact name, as
        ///   Font.GetOSInstalledFontNames lists them), then the platform's default chain (measured
        ///   on Linux when no name is installed; after an installed name it is unmeasured), then
        ///   its m_FallbackFonts' faces.
        List<GlyphChainLink> Chain
        {
            get
            {
                if (chain != null) return chain;
                chain = new List<GlyphChainLink>();
                if (Data.Length > 0) chain.Add(GlyphChainLink.FromData(this, Data, 0));
                else
                {
                    foreach (var (family, style) in OSFamilies(FontNames, OperatingSystem.IsLinux(), SystemFonts.TryResolveOSFontName, f => SystemFonts.TryFindStyled(f, 0, out _)))
                        chain.Add(GlyphChainLink.FromFamily(this, family, style));
                    if (!OperatingSystem.IsLinux() && BuiltinFont.Data.Length > 0) chain.Add(GlyphChainLink.FromData(this, BuiltinFont.Data, 0));
                }
                foreach (var fallback in Fallbacks)
                {
                    if (!fallback) continue;
                    var fn = (FontNative)fallback.NativeOrThrow;
                    if (fn != this && fn.FaceData is { Length: > 0 } data) chain.Add(GlyphChainLink.FromData(this, data, fn.faceIndex));
                }
                return chain;
            }
        }

        internal delegate bool OSFontNameResolver(string name, out string family, out int style);

        /// The installed families a font without data draws from, in order: the first of its
        /// names installed (with the style a styled name such as "FreeSans Bold" stands for),
        /// then on Linux the default chain's installed families, each once.
        internal static List<(string Family, int Style)> OSFamilies(string[] names, bool linux, OSFontNameResolver resolve, Func<string, bool> installed)
        {
            var families = new List<(string, int)>();
            string named = null;
            foreach (var name in names)
                if (resolve(name, out var family, out int style))
                {
                    families.Add((family, style));
                    named = family;
                    break;
                }
            if (linux)
                foreach (var family in LinuxDefaultChain)
                    if (family != named && installed(family)) families.Add((family, 0));
            return families;
        }

        /// Every open face with its font, so faces of fonts the garbage collector reclaimed
        /// (unloaded assets) are closed on the main thread.
        static readonly List<(WeakReference<FontNative> Owner, FontFace Face)> allFaces = new();

        internal void Register(FontFace face)
        {
            lock (allFaces) allFaces.Add((new WeakReference<FontNative>(this), face));
        }

        /// Closes this font's faces (Font destroyed); they reopen if the font is used again.
        public void ReleaseFaces()
        {
            lock (gate)
            {
                if (chain == null) return;
                var open = new List<FontFace>();
                foreach (var link in chain) link.CollectOpen(open);
                lock (allFaces) allFaces.RemoveAll(e => open.Contains(e.Face));
                foreach (var face in open) face.Dispose();
                chain = null;
            }
        }

        /// Closes the faces of fonts that no longer exist. Returns how many.
        public static int ReleaseOrphanFaces()
        {
            var orphans = new List<FontFace>();
            lock (allFaces)
                allFaces.RemoveAll(e =>
                {
                    if (e.Owner.TryGetTarget(out _)) return false;
                    orphans.Add(e.Face);
                    return true;
                });
            foreach (var face in orphans) face.Dispose();
            return orphans.Count;
        }

        /// Line metrics at a pixel size, from the serialized values at m_FontSize, unrounded.
        public void LineMetrics(int size, out float ascent, out float descent, out float lineHeight)
        {
            float k = FontSize > 0 ? size / FontSize : 1f;
            ascent = Ascent * k;
            descent = Descent * k;
            lineHeight = LineSpacing * k;
        }

        /// The generator's line metrics at a pixel size p (measured with LegacyRuntime,
        /// docs/cleanroom/ugui.md "Vertical metrics"): the serialized ascent, descent and line
        /// spacing scaled by p / m_FontSize and rounded to the nearest pixel; `spacingFloor` is
        /// the scaled line spacing rounded down (UILineInfo.height).
        public void PixelLineMetrics(int size, out int ascent, out int descent, out int pitch, out int spacingFloor)
        {
            float k = FontSize > 0 ? size / FontSize : 1f;
            ascent = (int)Math.Floor(Ascent * k + 0.5f);
            descent = (int)Math.Floor(-Descent * k + 0.5f);
            pitch = (int)Math.Floor(LineSpacing * k + 0.5f);
            spacingFloor = (int)Math.Floor(LineSpacing * k);
        }

        /// The kerning between two characters in pixels at a pixel size: a dynamic font's
        /// m_KerningValues (pixels at m_FontSize) scaled to the size and rounded to the nearest
        /// pixel (LegacyRuntime's 21 pairs, measured; the face's own kerning is not used).
        public int PixelKerning(int first, int second, int size)
        {
            if (Kerning.Count == 0 || first > 0xFFFF || second > 0xFFFF || !Kerning.TryGetValue((first, second), out float k)) return 0;
            return (int)Math.Floor(k * size / (FontSize > 0 ? FontSize : 1f) + 0.5f);
        }

        public int MapCase(int c) => ConvertCase switch
        {
            UpperCase => char.ToUpperInvariant((char)c),
            LowerCase => char.ToLowerInvariant((char)c),
            _ => c,
        };

        /// Bitmap fonts: whether the character has a rect. Dynamic fonts with data: whether a
        /// face of the chain has it. Fonts without data (OS fonts): always true (measured with
        /// LegacyRuntime for characters of FreeSans, DejaVu Sans, Liberation Sans and U+4E2D,
        /// which no face of its chain has).
        public bool HasCharacter(int c)
        {
            if (!Dynamic) return CharacterRects.ContainsKey(MapCase(c));
            if (Data.Length == 0) return true;
            lock (gate)
            {
                foreach (var link in Chain)
                    if (link.Face(0, out _) is { } face && face.GlyphIndex((uint)c) != 0) return true;
                return false;
            }
        }

        /// Layout metrics of a glyph. Dynamic fonts: rendered by FreeType at `size` pixels from
        /// the chain's first face that has it (an absent glyph has advance 0 and an empty box).
        /// Bitmap fonts: the character rect.
        public LegacyGlyph GetGlyph(int codePoint, int size, FontStyle style)
        {
            if (!Dynamic) return CharacterRects.GetValueOrDefault(MapCase(codePoint));
            lock (gate)
            {
                var key = new GlyphKey(codePoint, size, style);
                if (glyphs.TryGetValue(key, out var g)) return g;
                g = Measure(key);
                glyphs[key] = g;
                return g;
            }
        }

        int LoadFlags => FontRenderingMode switch
        {
            1 => FT.LOAD_DEFAULT | FT.LOAD_NO_BITMAP,
            2 => FT.LOAD_TARGET_MONO | FT.LOAD_NO_BITMAP,
            // Smooth and OS default: anti-aliased, unhinted.
            _ => FT.LOAD_NO_HINTING | FT.LOAD_NO_BITMAP,
        };

        static int StyleBits(FontStyle style) => style switch
        {
            FontStyle.Bold => SystemFonts.Bold,
            FontStyle.Italic => SystemFonts.Italic,
            FontStyle.BoldAndItalic => SystemFonts.Bold | SystemFonts.Italic,
            _ => 0,
        };

        /// Loads a glyph into its face's slot, with any style its face lacks synthesized; null
        /// when no face of the chain has it.
        unsafe FontFace LoadStyled(GlyphKey key) => LoadStyled(key, out _);

        unsafe FontFace LoadStyled(GlyphKey key, out bool emboldened)
        {
            emboldened = false;
            int style = StyleBits(key.Style);
            foreach (var link in Chain)
            {
                var face = link.Face(style, out int synthesize);
                if (face == null) continue;
                uint index = face.GlyphIndex((uint)key.CodePoint);
                if (index == 0) continue;
                if (face.SetPixelSize(Math.Max(key.Size, 1)) != FontEngineError.Success) return null;
                if (!face.Load(index, LoadFlags)) return null;
                var slot = face.Slot;
                if (slot->format == FT.GLYPH_FORMAT_OUTLINE)
                {
                    if ((synthesize & SystemFonts.Italic) != 0) FT.FT_GlyphSlot_Oblique(slot);
                    if ((synthesize & SystemFonts.Bold) != 0)
                    {
                        FT.FT_GlyphSlot_Embolden(slot);
                        emboldened = true;
                    }
                }
                return face;
            }
            return null;
        }

        int RenderMode => FontRenderingMode == 2 ? FT.RENDER_MODE_MONO : FT.RENDER_MODE_NORMAL;

        /// The advance (FreeType's, rounded to the nearest whole pixel unless the font keeps
        /// fractions) and the box of the bitmap FreeType renders.
        unsafe LegacyGlyph Measure(GlyphKey key)
        {
            var g = new LegacyGlyph { Exists = true };
            var face = LoadStyled(key, out bool emboldened);
            if (face == null) return g;
            var slot = face.Slot;
            long advance = (long)slot->advance.x.Value;
            g.Advance = ShouldRoundAdvanceValue ? (advance + 32) >> 6 : advance / 64f;
            long linear = (long)slot->linearHoriAdvance.Value;
            g.FineAdvance = linear / 65536f;
            // Synthesized bold widens the advance (26.6) but not the linear one (16.16).
            if (emboldened) g.FineAdvance += (advance - ((linear + 512) >> 10)) / 64f;
            if (slot->format == FT.GLYPH_FORMAT_OUTLINE && FT.FT_Render_Glyph(slot, RenderMode) != 0) return g;
            g.Left = slot->bitmap_left;
            g.Top = slot->bitmap_top;
            g.Width = (int)slot->bitmap.width;
            g.Height = (int)slot->bitmap.rows;
            return g;
        }

        // ----- Font texture (dynamic fonts) -----

        const int InitialTextureSize = 256, MaxTextureSize = 4096;

        Texture2DNative TextureData
        {
            get
            {
                if (!Texture)
                {
                    Texture = new Texture2D(0, 0, TextureFormat.Alpha8, false) { name = "Font Texture" };
                    if (Material) Material.mainTexture = Texture;
                }
                return (Texture2DNative)Texture.NativeOrThrow;
            }
        }

        /// Makes sure every requested glyph is in the font texture. When they do not all fit,
        /// the texture is rebuilt with only the glyphs used this frame plus the request,
        /// growing (width, then height, alternately) while they do not fit; true when the
        /// texture was rebuilt (the caller then raises Font.textureRebuilt, after which every
        /// text using this font must regenerate).
        public bool RequestGlyphs(IEnumerable<GlyphKey> keys)
        {
            bool rebuilt = RequestGlyphsCore(keys);
            if (rebuilt) AtlasVersion++;
            return rebuilt;
        }

        /// Incremented whenever the font texture is rebuilt (glyph UVs move).
        public int AtlasVersion;

        readonly List<GlyphKey> request = new();
        readonly HashSet<GlyphKey> requested = new();

        bool RequestGlyphsCore(IEnumerable<GlyphKey> keys)
        {
            lock (gate)
            {
                var t = TextureData;
                int frame = Time.frameCount;
                request.Clear();
                requested.Clear();
                bool missing = false;
                foreach (var key in keys)
                {
                    var g = GetGlyph(key.CodePoint, key.Size, key.Style);
                    if (g.LastUsedFrame != frame)
                    {
                        g.LastUsedFrame = frame;
                        glyphs[key] = g;
                    }
                    if (requested.Add(key))
                    {
                        request.Add(key);
                        missing |= !g.InTexture;
                    }
                }
                bool rebuilt = false;
                if (t.Width <= 0 || t.Height <= 0 || t.Format != TextureFormat.Alpha8 || t.Memory == null)
                {
                    // The texture's first allocation moves no glyph: no textureRebuilt (measured:
                    // fonts from CreateDynamicFontFromOSFont raise none on their first glyphs).
                    foreach (var g in glyphs.Values)
                        if (g.InTexture) { rebuilt = true; break; }
                    Reset(t, Math.Max(t.Width, InitialTextureSize), Math.Max(t.Height, InitialTextureSize));
                    missing = request.Count > 0;
                }
                if (!missing)
                {
                    if (rebuilt) t.ContentChanged();
                    return rebuilt;
                }
                if (!PackAll(t, request))
                {
                    var keep = new List<GlyphKey>();
                    foreach (var (key, g) in glyphs)
                        if (g.Exists && (g.LastUsedFrame == frame || requested.Contains(key))) keep.Add(key);
                    int w = t.Width, h = t.Height;
                    while (true)
                    {
                        Reset(t, w, h);
                        if (PackAll(t, keep) || (w >= MaxTextureSize && h >= MaxTextureSize)) break;
                        if (w <= h) w = Math.Min(w * 2, MaxTextureSize); else h = Math.Min(h * 2, MaxTextureSize);
                    }
                    rebuilt = true;
                }
                t.ContentChanged();
                return rebuilt;
            }
        }

        void Reset(Texture2DNative t, int width, int height)
        {
            t.Width = width;
            t.Height = height;
            t.Format = TextureFormat.Alpha8;
            t.MipCount = 1;
            t.Readable = true;
            t.Allocate(t.SliceSize, 0);
            packer.Reset(width, height);
            foreach (var key in new List<GlyphKey>(glyphs.Keys))
            {
                var g = glyphs[key];
                g.InTexture = false;
                glyphs[key] = g;
            }
        }

        /// Packs and renders glyphs, each in a rect of its bitmap plus the character padding on
        /// every side (empty glyphs too: a space's rect is 2 × 2); false when one does not fit
        /// (glyphs packed before it stay).
        bool PackAll(Texture2DNative t, List<GlyphKey> keys)
        {
            int pad = Padding;
            foreach (var key in keys)
            {
                var g = glyphs[key];
                if (g.InTexture) continue;
                int w = g.Width + 2 * pad, h = g.Height + 2 * pad;
                if (w <= 0 || h <= 0) g.X = g.Y = 0; // nothing to store
                else if (!packer.TryPack(w, h, out g.X, out g.Y)) return false;
                g.InTexture = true;
                if (g.Width > 0 && g.Height > 0) Render(t, key, g.X + pad, g.Y + pad, g.Width, g.Height);
                glyphs[key] = g;
            }
            return true;
        }

        unsafe void Render(Texture2DNative t, GlyphKey key, int x, int y, int width, int height)
        {
            var face = LoadStyled(key);
            if (face == null) return;
            var slot = face.Slot;
            if (slot->format == FT.GLYPH_FORMAT_OUTLINE && FT.FT_Render_Glyph(slot, RenderMode) != 0) return;
            var bitmap = GlyphRasterizer.CopyBitmap(&slot->bitmap);
            // The bitmap is the measured box; anything beyond it is clipped to the reserved rect.
            var clipped = new GlyphBitmap { Width = width, Height = height, Pixels = new byte[width * height] };
            for (int row = 0; row < Math.Min(bitmap.Height, height); row++)
                for (int col = 0; col < Math.Min(bitmap.Width, width); col++)
                    clipped.Pixels[row * width + col] = bitmap.Rgba ? bitmap.Pixels[4 * (row * bitmap.Width + col) + 3] : bitmap.Pixels[row * bitmap.Width + col];
            FontEngine.Blit(t, clipped, x, y);
        }

        /// UV rectangle (x, y, width, height) of a glyph placed in the texture: a dynamic
        /// font's covers the padded rect.
        public Rect UvOf(in LegacyGlyph g)
        {
            if (!Dynamic) return g.Uv;
            var t = TextureData;
            int pad = Padding;
            return new Rect((float)g.X / t.Width, (float)g.Y / t.Height, (float)(g.Width + 2 * pad) / t.Width, (float)(g.Height + 2 * pad) / t.Height);
        }

        public LegacyGlyph PlacedGlyph(GlyphKey key)
        {
            lock (gate) return glyphs.GetValueOrDefault(key);
        }

        // ----- Fonts made from OS fonts -----

        /// Font.CreateDynamicFontFromOSFont: a dynamic font without data whose glyphs come from
        /// the first of `names` installed, else the default chain (as LegacyRuntime's), with the
        /// line metrics of that chain's first face at `size` (measured: "Arial" here reads
        /// FreeSans's line height 14 and ascent 10; see SfntTables.TryGetLineMetrics).
        public void InitFromOSFonts(string[] names, int size)
        {
            InitDynamic(names, size);
        }

        /// A dynamic font made from a TrueType/OpenType file's data: its glyphs, and the faces the
        /// TextCore font engine loads for it (TMP font assets), come from that data, with its
        /// line metrics at `size`.
        public void InitFromData(byte[] data, string name, int size)
        {
            Data = data ?? Array.Empty<byte>();
            faceData = null;
            faceDataResolved = false;
            lock (gate) chain = null;
            InitDynamic(new[] { name }, size);
        }

        void InitDynamic(string[] names, int size)
        {
            FontNames = names;
            FontSize = size;
            ConvertCase = DynamicFont;
            CharacterPadding = 1;
            ShouldRoundAdvanceValue = true;
            lock (gate)
            {
                foreach (var link in Chain)
                {
                    var face = link.Face(0, out _);
                    if (face == null || !face.Tables.TryGetLineMetrics(out int ascender, out int descender, out int spacing)) continue;
                    float k = (float)size / face.UnitsPerEm;
                    Ascent = ascender * k;
                    Descent = descender * k;
                    LineSpacing = spacing * k;
                    break;
                }
            }
        }
    }

    /// One link of a dynamic font's glyph chain: its face per style, opened on first use, and
    /// the style FreeType synthesizes on it. A font's own data is one face for every style
    /// (bold and italic synthesized). An installed family uses its own bold, italic and bold
    /// italic faces (measured: FreeSansBold, FreeSansOblique and FreeSansBoldOblique, not
    /// synthesized); a style the family does not have is synthesized on its regular face
    /// (unmeasured).
    internal sealed class GlyphChainLink
    {
        readonly FontNative owner;
        readonly byte[] data;
        readonly int dataIndex;
        readonly string family;
        readonly int baseStyle;
        readonly FontFace[] faces = new FontFace[4];
        readonly int[] synthesized = new int[4];
        readonly bool[] resolved = new bool[4];
        readonly Dictionary<(string, int), FontFace> opened = new();

        GlyphChainLink(FontNative owner, byte[] data, int dataIndex, string family, int baseStyle)
        {
            this.owner = owner;
            this.data = data;
            this.dataIndex = dataIndex;
            this.family = family;
            this.baseStyle = baseStyle;
        }

        public static GlyphChainLink FromData(FontNative owner, byte[] data, int faceIndex) => new(owner, data, faceIndex, null, 0);

        public static GlyphChainLink FromFamily(FontNative owner, string family, int baseStyle) => new(owner, null, 0, family, baseStyle);

        /// The face for a style (SystemFonts' bits) and the bits FreeType must synthesize on it;
        /// null when it cannot be opened.
        public FontFace Face(int style, out int synthesize)
        {
            if (!resolved[style])
            {
                resolved[style] = true;
                if (data != null)
                {
                    faces[style] = Open(null, dataIndex);
                    synthesized[style] = style;
                }
                else
                {
                    int want = style | baseStyle;
                    if (SystemFonts.TryFindStyled(family, want, out var r))
                        faces[style] = Open(r.filePath, r.faceIndex);
                    else if (SystemFonts.TryFindStyled(family, baseStyle, out r) || SystemFonts.TryFindStyled(family, 0, out r))
                    {
                        faces[style] = Open(r.filePath, r.faceIndex);
                        synthesized[style] = want & ~baseStyle;
                    }
                }
            }
            synthesize = synthesized[style];
            return faces[style];
        }

        FontFace Open(string path, int index)
        {
            if (opened.TryGetValue((path, index), out var face)) return face;
            byte[] bytes = data;
            if (path != null)
            {
                try { bytes = File.ReadAllBytes(path); }
                catch (IOException) { bytes = null; }
                catch (UnauthorizedAccessException) { bytes = null; }
            }
            if (bytes == null || bytes.Length == 0 || FontFace.TryOpen(bytes, index, out face) != FontEngineError.Success) face = null;
            else owner.Register(face);
            opened[(path, index)] = face;
            return face;
        }

        public void CollectOpen(List<FontFace> into)
        {
            foreach (var face in opened.Values)
                if (face != null && !into.Contains(face)) into.Add(face);
        }
    }

    /// The built-in default face: Liberation Sans Regular (LegacyRuntime's serialized metrics
    /// are Liberation Sans's), taken from the game's own copy of it (a Font asset with that
    /// family in its m_FontNames and data included), else from the installed OS font. The
    /// TextCore font engine's face for fonts without data, and the last link of their glyph
    /// chain off Linux; on Linux the original draws LegacyRuntime from the OS fonts instead.
    internal static class BuiltinFont
    {
        static byte[] data;
        static readonly object gate = new();

        public static byte[] Data
        {
            get
            {
                lock (gate)
                {
                    if (data != null) return data;
                    data = FromGameData() ?? FromSystem() ?? Array.Empty<byte>();
                    if (data.Length == 0) Debug.LogError("The built-in font (Liberation Sans) was found neither in the game data nor among the installed fonts; text in fonts without data will not render.");
                    return data;
                }
            }
        }

        static byte[] FromGameData()
        {
            if (string.IsNullOrEmpty(AssetSystem.DataDir)) return null;
            var names = new List<string> { "resources.assets" };
            for (int i = 0; File.Exists(Path.Combine(AssetSystem.DataDir, $"sharedassets{i}.assets")); i++) names.Add($"sharedassets{i}.assets");
            foreach (var name in names)
            {
                if (!File.Exists(Path.Combine(AssetSystem.DataDir, name))) continue;
                var file = AssetSystem.Get(name);
                foreach (var info in file.Objects)
                {
                    if (file.TypeOf(info).ClassId != 128) continue;
                    var f = TypeTreeReader.ReadNative(128, file.OpenObject(info));
                    var bytes = f.Get<byte[]>("m_FontData");
                    if (bytes.Length == 0 || f.Get<uint>("m_DefaultStyle") != 0) continue;
                    foreach (var n in f.Array("m_FontNames"))
                        if ((string)n == "Liberation Sans") return bytes;
                }
            }
            return null;
        }

        static byte[] FromSystem() => SystemFonts.TryFind("Liberation Sans", "Regular", out var r) ? File.ReadAllBytes(r.filePath) : null;
    }
}
