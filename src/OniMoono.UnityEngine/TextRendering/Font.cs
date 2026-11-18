using System;
using System.Collections.Generic;
using OniMoono.Engine.Text;

namespace UnityEngine
{
    public partial class Font
    {
        /// An empty font: no data, no characters, not dynamic.
        public Font()
        {
            Bind(new FontNative());
        }

        /// An empty font of that name, as Font() (Worldgen Designer passes a font file's path
        /// and falls back to an OS font when it has no 'A').
        public Font(string name) : this()
        {
            this.name = name;
        }

        FontNative Data => (FontNative)NativeOrThrow;

        internal override void EngineOnDestroy() => Data.ReleaseFaces();

        /// Raised after a dynamic font's texture was rebuilt (glyphs moved or were dropped);
        /// every text using the font must be regenerated.
        public static event Action<Font> textureRebuilt;

        internal static void InvokeTextureRebuilt_Internal(Font font) => textureRebuilt?.Invoke(font);

        public Material material
        {
            get => Data.Material;
            set => Data.Material = value;
        }

        public string[] fontNames
        {
            get => (string[])Data.FontNames.Clone();
            set => Data.FontNames = value == null ? Array.Empty<string>() : (string[])value.Clone();
        }

        /// Dynamic fonts render glyphs from font data on demand (m_ConvertCase == -2).
        public bool dynamic => Data.Dynamic;

        /// The size the font was imported at (its metrics and bitmap glyphs are at this size).
        public int fontSize => (int)Data.FontSize;

        /// The font's line spacing at its own size, in whole pixels (truncated: the built-in
        /// LegacyRuntime font's 14.95 reads 14 in the original player).
        public int lineHeight => (int)Data.LineSpacing;

        /// The font's ascent at its own size, in whole pixels (truncated: LegacyRuntime's 11.77
        /// reads 11).
        public int ascent => (int)Data.Ascent;

        public bool HasCharacter(char c) => Data.HasCharacter(c);

        /// Puts the characters, at `size` pixels (0: the font's size) and `style`, into the
        /// font texture; raises textureRebuilt when the texture had to be rebuilt. Bitmap fonts
        /// have all their characters in their texture already.
        public void RequestCharactersInTexture(string characters, int size, FontStyle style)
        {
            var data = Data;
            if (!data.Dynamic || string.IsNullOrEmpty(characters)) return;
            if (size == 0) size = (int)data.FontSize;
            var keys = new System.Collections.Generic.List<GlyphKey>(characters.Length);
            for (int i = 0; i < characters.Length; i++)
            {
                int c = characters[i];
                if (char.IsHighSurrogate(characters, i) && i + 1 < characters.Length && char.IsLowSurrogate(characters[i + 1]))
                    c = char.ConvertToUtf32(characters[i], characters[++i]);
                keys.Add(new GlyphKey(c, size, style));
            }
            if (data.RequestGlyphs(keys)) InvokeTextureRebuilt_Internal(this);
        }

        public void RequestCharactersInTexture(string characters, int size) => RequestCharactersInTexture(characters, size, FontStyle.Normal);

        public void RequestCharactersInTexture(string characters) => RequestCharactersInTexture(characters, 0, FontStyle.Normal);

        public static int GetMaxVertsForString(string str) => str.Length * 4 + 4;

        /// The glyph of a character at `size` pixels (0: the font's size) and `style`, when it is
        /// in the font texture (dynamic fonts: after RequestCharactersInTexture or a generation
        /// at that size; bitmap fonts: every character of the font). For a dynamic font the box
        /// is the rendered bitmap grown by the character padding, the advance a whole number of
        /// pixels; a character no face has is there with advance 0 and an empty box.
        public bool GetCharacterInfo(char ch, out CharacterInfo info, int size, FontStyle style)
        {
            info = default;
            var data = Data;
#pragma warning disable CS0618 // CharacterInfo's fields
            if (!data.Dynamic)
            {
                if (!data.CharacterRects.TryGetValue(data.MapCase(ch), out var r)) return false;
                info = new CharacterInfo
                {
                    index = ch, size = size, style = style, width = r.Advance, flipped = r.Flipped, uv = r.Uv,
                    vert = new Rect(r.Left, r.Top, r.Width, -r.Height),
                };
                return true;
            }
            var g = data.PlacedGlyph(new GlyphKey(ch, size == 0 ? (int)data.FontSize : size, style));
            if (!g.Exists || !g.InTexture) return false;
            int pad = data.Padding;
            info = new CharacterInfo
            {
                index = ch, size = size, style = style, width = g.Advance, uv = data.UvOf(g),
                vert = new Rect(g.Left - pad, g.Top + pad, g.Width + 2 * pad, -(g.Height + 2 * pad)),
            };
#pragma warning restore CS0618
            return true;
        }

        public bool GetCharacterInfo(char ch, out CharacterInfo info, int size) => GetCharacterInfo(ch, out info, size, FontStyle.Normal);

        public bool GetCharacterInfo(char ch, out CharacterInfo info) => GetCharacterInfo(ch, out info, 0, FontStyle.Normal);

        /// A dynamic font whose glyphs come from the installed OS font of that name (as the
        /// names Font.GetOSInstalledFontNames lists; when it is not installed, the default chain
        /// LegacyRuntime uses), at `size` pixels, with that face's line metrics.
        public static Font CreateDynamicFontFromOSFont(string fontname, int size) => CreateDynamicFontFromOSFont(new[] { fontname }, size);

        /// As CreateDynamicFontFromOSFont(string, int), with the first of the names installed;
        /// the font is named after the first name.
        public static Font CreateDynamicFontFromOSFont(string[] fontnames, int size)
        {
            var names = fontnames == null ? Array.Empty<string>() : (string[])fontnames.Clone();
            var font = new Font { name = names.Length > 0 ? names[0] ?? "" : "" };
            var data = font.Data;
            data.InitFromOSFonts(names, size);
            data.Material = FontMaterial();
            return font;
        }

        /// The player's internal entry point for a dynamic font made from a font file, which mods
        /// call by reflection (ONI Vietnamese Beautiful Font builds its TMP fonts from it): the
        /// font takes the file's data, named after the file, at 16 pixels.
        static void Internal_CreateFontFromPath(Font self, string fontPath)
        {
            var data = self.Data;
            data.InitFromData(System.IO.File.ReadAllBytes(fontPath), System.IO.Path.GetFileNameWithoutExtension(fontPath), 16);
            data.Material = FontMaterial();
        }

        /// A new material for a font's texture, with the built-in font's shader.
        static Material FontMaterial()
        {
            Shader shader = null;
            try
            {
                shader = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")?.material?.shader;
            }
            catch (System.IO.IOException)
            {
                // No player data (no built-in resources file).
            }
            shader ??= Shader.Find("GUI/Text Shader");
            return !shader ? null : new Material(shader) { name = "Font Material" };
        }

        /// Paths of the font files installed on the system.
        public static string[] GetPathsToOSFonts() => (string[])SystemFonts.Paths.Clone();

        /// The fonts installed on the system, each name once: the family, then " Bold" and
        /// " Italic" by the face's style (measured on Linux, 2026-10-24).
        public static string[] GetOSInstalledFontNames() => (string[])SystemFonts.OSFontNames.Clone();
    }
}
