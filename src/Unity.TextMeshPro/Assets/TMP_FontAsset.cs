using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace TMPro
{
    // A font for text: the face metrics, the glyphs and characters of its atlas textures, the
    // pair adjustments used for kerning, fallbacks and the style weights. Lookups are built from
    // the serialized tables on first use. A dynamic asset renders missing characters into its
    // atlas through the engine's FontEngine.
    public partial class TMP_FontAsset
    {
        Dictionary<uint, Glyph> m_GlyphLookup;
        Dictionary<uint, TMP_Character> m_CharacterLookup;
        Dictionary<ulong, GlyphPairAdjustmentRecord> m_PairLookup;
        HashSet<uint> m_MissingUnicodes;
        bool m_FaceLoadFailed;

        // Defaults of a new asset, as the font asset documentation lists them.
        const string CurrentVersion = "1.1.0";

        public TMP_FontAsset()
        {
            normalStyle = 0f;
            normalSpacingOffset = 0f;
            boldStyle = 0.75f;
            boldSpacing = 7f;
            italicStyle = 35;
            tabSize = 10;
        }

        // ----- Contents -----

        public Font sourceFontFile { get => m_SourceFontFile; internal set => m_SourceFontFile = value; }

        public AtlasPopulationMode atlasPopulationMode
        {
            get => m_AtlasPopulationMode;
            set => m_AtlasPopulationMode = value;
        }

        public FontAssetCreationSettings creationSettings { get => m_CreationSettings; set => m_CreationSettings = value; }

        public List<Glyph> glyphTable { get => m_GlyphTable; internal set => m_GlyphTable = value; }

        public Dictionary<uint, Glyph> glyphLookupTable
        {
            get
            {
                if (m_GlyphLookup == null) ReadFontAssetDefinition();
                return m_GlyphLookup;
            }
        }

        public List<TMP_Character> characterTable { get => m_CharacterTable; internal set => m_CharacterTable = value; }

        public Dictionary<uint, TMP_Character> characterLookupTable
        {
            get
            {
                if (m_CharacterLookup == null) ReadFontAssetDefinition();
                return m_CharacterLookup;
            }
        }

        public Texture2D atlasTexture => m_AtlasTextures != null && m_AtlasTextures.Length > 0 ? m_AtlasTextures[0] : null;

        public Texture2D[] atlasTextures { get => m_AtlasTextures; set => m_AtlasTextures = value; }

        public int atlasTextureCount => m_AtlasTextureIndex + 1;

        public bool isMultiAtlasTexturesEnabled { get => m_IsMultiAtlasTexturesEnabled; set => m_IsMultiAtlasTexturesEnabled = value; }

        public int atlasWidth { get => m_AtlasWidth; internal set => m_AtlasWidth = value; }

        public int atlasHeight { get => m_AtlasHeight; internal set => m_AtlasHeight = value; }

        public int atlasPadding { get => m_AtlasPadding; internal set => m_AtlasPadding = value; }

        public GlyphRenderMode atlasRenderMode { get => m_AtlasRenderMode; internal set => m_AtlasRenderMode = value; }

        public TMP_FontFeatureTable fontFeatureTable { get => m_FontFeatureTable; internal set => m_FontFeatureTable = value; }

        public List<TMP_FontAsset> fallbackFontAssetTable { get => m_FallbackFontAssetTable; set => m_FallbackFontAssetTable = value; }

        public TMP_FontWeightPair[] fontWeightTable { get => m_FontWeightTable; internal set => m_FontWeightTable = value; }

        // ----- Creation -----

        public static TMP_FontAsset CreateFontAsset(Font font)
            => CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);

        public static TMP_FontAsset CreateFontAsset(Font font, int samplingPointSize, int atlasPadding, GlyphRenderMode renderMode, int atlasWidth, int atlasHeight, AtlasPopulationMode atlasPopulationMode = AtlasPopulationMode.Dynamic, bool enableMultiAtlasSupport = true)
        {
            if (font == null) return null;
            if (FontEngine.LoadFontFace(font, samplingPointSize) != FontEngineError.Success)
            {
                Debug.LogWarning($"Unable to load font face for [{font.name}]. Make sure \"Include Font Data\" is enabled in the Font Import Settings.", font);
                return null;
            }
            var asset = ScriptableObject.CreateInstance<TMP_FontAsset>();
            asset.m_Version = CurrentVersion;
            asset.name = font.name + " SDF";
            // Measured: GetSimpleHashCode(name + " Atlas Material") of the name at creation.
            asset.materialHashCode = TMP_TextUtilities.GetSimpleHashCode(asset.name + " Atlas Material");
            asset.m_FaceInfo = FontEngine.GetFaceInfo();
            asset.m_SourceFontFile = font;
            asset.m_AtlasPopulationMode = atlasPopulationMode;
            asset.m_AtlasWidth = atlasWidth;
            asset.m_AtlasHeight = atlasHeight;
            asset.m_AtlasPadding = atlasPadding;
            asset.m_AtlasRenderMode = renderMode;
            asset.m_IsMultiAtlasTexturesEnabled = enableMultiAtlasSupport;
            asset.m_GlyphTable = new List<Glyph>();
            asset.m_CharacterTable = new List<TMP_Character>();
            asset.m_FontFeatureTable = new TMP_FontFeatureTable();
            asset.m_FallbackFontAssetTable = new List<TMP_FontAsset>();
            asset.m_FontWeightTable = new TMP_FontWeightPair[10];
            asset.m_UsedGlyphRects = new List<GlyphRect>();
            asset.m_FreeGlyphRects = new List<GlyphRect> { new GlyphRect(0, 0, atlasWidth - 1, atlasHeight - 1) };
            asset.m_AtlasTextureIndex = 0;

            var texture = NewAtlasTexture(asset, 0);
            asset.m_AtlasTextures = new[] { texture };

            bool sdf = IsSdf(renderMode);
            var shader = Shader.Find(sdf ? "TextMeshPro/Mobile/Distance Field" : "TextMeshPro/Bitmap");
            var material = new Material(shader) { name = texture.name + " Material" };
            material.SetTexture(ShaderUtilities.ID_MainTex, texture);
            material.SetFloat(ShaderUtilities.ID_TextureWidth, atlasWidth);
            material.SetFloat(ShaderUtilities.ID_TextureHeight, atlasHeight);
            if (sdf)
            {
                material.SetFloat(ShaderUtilities.ID_GradientScale, atlasPadding + 1);
                material.SetFloat(ShaderUtilities.ID_WeightNormal, asset.normalStyle);
                material.SetFloat(ShaderUtilities.ID_WeightBold, asset.boldStyle);
            }
            asset.m_Material = material;

            var settings = asset.m_CreationSettings;
            settings.sourceFontFileName = font.name;
            settings.pointSizeSamplingMode = 1;
            settings.pointSize = samplingPointSize;
            settings.padding = atlasPadding;
            settings.packingMode = 0;
            settings.atlasWidth = atlasWidth;
            settings.atlasHeight = atlasHeight;
            settings.renderMode = (int)renderMode;
            settings.includeFontFeatures = true;
            asset.m_CreationSettings = settings;
            asset.ReadFontAssetDefinition();
            return asset;
        }

        // From an installed OS font, by family and style name (matched without regard to case).
        // A family or style that is not installed returns null and logs one line; a point size
        // of 0 or below samples the face at its units per EM.
        public static TMP_FontAsset CreateFontAsset(string familyName, string styleName, int pointSize = 90)
        {
            if (!FontEngine.TryGetSystemFontReference(familyName, styleName, out var fontRef))
            {
                Debug.Log($"Unable to find a font file with the specified Family Name [{familyName}] and Style [{styleName}].");
                return null;
            }
            if (!LoadFaceFromPath(fontRef.filePath, fontRef.faceIndex, pointSize)) return null;
            return NewFileAsset(fontRef.filePath, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.DynamicOS);
        }

        // From the face at faceIndex of a font file. A file that cannot be loaded as a font (or
        // has no such face) returns null and logs one line.
        public static TMP_FontAsset CreateFontAsset(string fontFilePath, int faceIndex, int samplingPointSize, int atlasPadding, GlyphRenderMode renderMode, int atlasWidth, int atlasHeight)
        {
            if (!LoadFaceFromPath(fontFilePath, faceIndex, samplingPointSize))
            {
                Debug.Log($"Unable to load font face from [{fontFilePath}].");
                return null;
            }
            return NewFileAsset(fontFilePath, atlasPadding, renderMode, atlasWidth, atlasHeight, AtlasPopulationMode.Dynamic);
        }

        // Makes the face current at pointSize; at its units per EM when pointSize is 0 or below.
        static bool LoadFaceFromPath(string path, int faceIndex, int pointSize)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (pointSize > 0) return FontEngine.LoadFontFace(path, pointSize, faceIndex) == FontEngineError.Success;
            if (FontEngine.LoadFontFace(path, DefaultSamplingPointSize, faceIndex) != FontEngineError.Success) return false;
            int unitsPerEm = FontEngine.GetFaceInfo().unitsPerEM;
            return unitsPerEm > 0 && FontEngine.LoadFontFace(path, unitsPerEm, faceIndex) == FontEngineError.Success;
        }

        const int DefaultSamplingPointSize = 90;

        // A dynamic asset of the current face, read from a file: unnamed, no source Font, default
        // creation settings, no fallback list, and a 1 x 1 atlas texture that takes the atlas size
        // when its first glyph is added.
        static TMP_FontAsset NewFileAsset(string path, int atlasPadding, GlyphRenderMode renderMode, int atlasWidth, int atlasHeight, AtlasPopulationMode populationMode)
        {
            bool sdf = IsSdf(renderMode);
            var shader = Shader.Find(sdf ? "TextMeshPro/Mobile/Distance Field" : "TextMeshPro/Bitmap");
            // The game ships no bitmap shader: no asset for a bitmap render mode.
            if (shader == null) throw new System.ArgumentNullException();

            var asset = ScriptableObject.CreateInstance<TMP_FontAsset>();
            asset.m_Version = CurrentVersion;
            asset.name = "";
            // Measured: GetSimpleHashCode(name + " Atlas Material") of the name at creation.
            asset.materialHashCode = TMP_TextUtilities.GetSimpleHashCode(asset.name + " Atlas Material");
            asset.m_FaceInfo = FontEngine.GetFaceInfo();
            asset.m_SourceFontFilePath = path;
            asset.m_AtlasPopulationMode = populationMode;
            asset.m_AtlasWidth = atlasWidth;
            asset.m_AtlasHeight = atlasHeight;
            asset.m_AtlasPadding = atlasPadding;
            asset.m_AtlasRenderMode = renderMode;
            asset.m_IsMultiAtlasTexturesEnabled = true;
            asset.m_GlyphTable = new List<Glyph>();
            asset.m_CharacterTable = new List<TMP_Character>();
            asset.m_FontFeatureTable = new TMP_FontFeatureTable();
            asset.m_FallbackFontAssetTable = null;
            asset.m_FontWeightTable = new TMP_FontWeightPair[10];
            asset.m_UsedGlyphRects = new List<GlyphRect>();
            asset.m_FreeGlyphRects = new List<GlyphRect> { new GlyphRect(0, 0, atlasWidth - 1, atlasHeight - 1) };
            asset.m_AtlasTextureIndex = 0;

            var format = IsColor(renderMode) ? TextureFormat.RGBA32 : TextureFormat.Alpha8;
            var texture = new Texture2D(1, 1, format, false) { name = "" };
            FontEngine.ResetAtlasTexture(texture);
            asset.m_AtlasTextures = new[] { texture };

            var material = new Material(shader) { name = shader.name };
            material.SetTexture(ShaderUtilities.ID_MainTex, texture);
            material.SetFloat(ShaderUtilities.ID_TextureWidth, atlasWidth);
            material.SetFloat(ShaderUtilities.ID_TextureHeight, atlasHeight);
            if (sdf)
            {
                material.SetFloat(ShaderUtilities.ID_GradientScale, atlasPadding + 1);
                material.SetFloat(ShaderUtilities.ID_WeightNormal, asset.normalStyle);
                material.SetFloat(ShaderUtilities.ID_WeightBold, asset.boldStyle);
            }
            asset.m_Material = material;
            asset.ReadFontAssetDefinition();
            return asset;
        }

        // GlyphRasterModes bits of the render mode: SDF (0x20) and SDFAA (0x40) fields, colour (0x10000).
        internal static bool IsSdf(GlyphRenderMode mode) => ((int)mode & 0x60) != 0;

        static bool IsColor(GlyphRenderMode mode) => ((int)mode & 0x10000) != 0;

        static Texture2D NewAtlasTexture(TMP_FontAsset asset, int index)
        {
            var format = IsColor(asset.m_AtlasRenderMode) ? TextureFormat.RGBA32 : TextureFormat.Alpha8;
            var texture = new Texture2D(asset.m_AtlasWidth, asset.m_AtlasHeight, format, false)
            {
                name = asset.name + " Atlas" + (index > 0 ? " " + index : ""),
            };
            FontEngine.ResetAtlasTexture(texture);
            return texture;
        }

        // ----- Lookups -----

        // Builds the lookups from the serialized tables: glyphs by index, characters by unicode
        // (each linked to its glyph), pair adjustments by glyph pair.
        public void ReadFontAssetDefinition()
        {
            m_GlyphTable ??= new List<Glyph>();
            m_CharacterTable ??= new List<TMP_Character>();
            m_GlyphLookup = new Dictionary<uint, Glyph>(m_GlyphTable.Count);
            foreach (var g in m_GlyphTable)
                if (g != null) m_GlyphLookup.TryAdd(g.index, g);
            m_CharacterLookup = new Dictionary<uint, TMP_Character>(m_CharacterTable.Count);
            foreach (var c in m_CharacterTable)
            {
                if (c == null) continue;
                if (!m_GlyphLookup.TryGetValue(c.glyphIndex, out var glyph)) continue;
                c.glyph = glyph;
                c.textAsset = this;
                m_CharacterLookup.TryAdd(c.unicode, c);
            }
            m_PairLookup = new Dictionary<ulong, GlyphPairAdjustmentRecord>();
            var records = m_FontFeatureTable?.m_GlyphPairAdjustmentRecords;
            if (records != null)
                foreach (var r in records)
                    m_PairLookup.TryAdd(PairKey(r.firstAdjustmentRecord.glyphIndex, r.secondAdjustmentRecord.glyphIndex), r);
            m_MissingUnicodes = null;
            if (m_FaceInfo.scale == 0f)
            {
                var info = m_FaceInfo;
                info.scale = 1f;
                m_FaceInfo = info;
            }
        }

        static ulong PairKey(uint first, uint second) => (ulong)first << 32 | second;

        internal bool TryGetPairAdjustment(uint firstGlyph, uint secondGlyph, out GlyphPairAdjustmentRecord record)
        {
            if (m_PairLookup == null) ReadFontAssetDefinition();
            return m_PairLookup.TryGetValue(PairKey(firstGlyph, secondGlyph), out record);
        }

        internal bool HasPairAdjustments
        {
            get
            {
                if (m_PairLookup == null) ReadFontAssetDefinition();
                return m_PairLookup.Count > 0;
            }
        }

        // The character in this asset only (no fallbacks); a dynamic asset adds it when its font
        // has the glyph.
        internal bool TryGetLocalCharacter(uint unicode, out TMP_Character character)
        {
            if (characterLookupTable.TryGetValue(unicode, out character)) return true;
            if (m_AtlasPopulationMode == AtlasPopulationMode.Static) return false;
            return TryAddCharacter(unicode, out character);
        }

        public bool HasCharacter(int character) => HasCharacter((uint)character, false, false);

        public bool HasCharacter(char character, bool searchFallbacks = false, bool tryAddCharacter = false)
            => HasCharacter((uint)character, searchFallbacks, tryAddCharacter);

        // With searchFallbacks, the same search as HasCharacters' (FindCharacter): this asset's
        // fallbacks, TMP_Settings' fallbacks and the default font asset.
        public bool HasCharacter(uint character, bool searchFallbacks = false, bool tryAddCharacter = false)
            => FindCharacter(character, searchFallbacks, tryAddCharacter);

        // The fallbacks of a list, depth-first: each asset's table (a dynamic one adding the
        // character with tryAdd), then its own fallbacks. A null entry ends that list's search,
        // the lists around it going on (measured on the shipped package: fallbacks [null,
        // NotoSans] do not find "a"; [M, NotoSans], M's own fallbacks being [null], do).
        static bool FallbackHasCharacter(List<TMP_FontAsset> fallbacks, uint character, bool tryAdd, HashSet<int> visited)
        {
            if (fallbacks == null) return false;
            foreach (var f in fallbacks)
            {
                if (f == null) return false;
                if (!visited.Add(f.GetInstanceID())) continue;
                if (HasOrAdds(f, character, tryAdd)) return true;
                if (FallbackHasCharacter(f.m_FallbackFontAssetTable, character, tryAdd, visited)) return true;
            }
            return false;
        }

        public bool HasCharacters(string text)
        {
            if (text == null) return false;
            for (int i = 0; i < text.Length; i++)
                if (!characterLookupTable.ContainsKey(text[i])) return false;
            return true;
        }

        public bool HasCharacters(string text, out List<char> missingCharacters)
        {
            missingCharacters = new List<char>();
            if (text == null) return false;
            for (int i = 0; i < text.Length; i++)
                if (!characterLookupTable.ContainsKey(text[i])) missingCharacters.Add(text[i]);
            return missingCharacters.Count == 0;
        }

        // Whether every code point of the text is found (a surrogate pair is one code point, a
        // lone surrogate itself; control characters below U+0020 always are). missingCharacters
        // is null when all are, else the missing ones in text order, repeats kept. Without
        // searchFallbacks only this asset is looked in; with it, then its fallbacks (and theirs),
        // TMP_Settings' fallbacks and its default font asset. tryAddCharacter adds a missing
        // character to the first dynamic asset in that order whose face has it.
        public bool HasCharacters(string text, out uint[] missingCharacters, bool searchFallbacks = false, bool tryAddCharacter = false)
        {
            missingCharacters = null;
            List<uint> missing = null;
            int length = text.Length;
            for (int i = 0; i < length; i++)
            {
                uint unicode = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < length && char.IsLowSurrogate(text[i + 1]))
                {
                    unicode = (uint)char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                if (unicode < 0x20) continue;
                if (FindCharacter(unicode, searchFallbacks, tryAddCharacter)) continue;
                (missing ??= new List<uint>()).Add(unicode);
            }
            if (missing == null) return true;
            missingCharacters = missing.ToArray();
            return false;
        }

        // This asset (adding the character with tryAdd when dynamic), then with searchFallbacks
        // its fallbacks, TMP_Settings' fallbacks and the default font asset, each with its own
        // fallbacks, depth-first; tryAdd adds to the first dynamic asset in that order whose face
        // has the character (measured on the shipped package, UnityProbe's fontfallback).
        bool FindCharacter(uint unicode, bool searchFallbacks, bool tryAdd)
        {
            if (HasOrAdds(this, unicode, tryAdd)) return true;
            if (!searchFallbacks) return false;
            var visited = new HashSet<int> { GetInstanceID() };
            if (FallbackHasCharacter(m_FallbackFontAssetTable, unicode, tryAdd, visited)) return true;
            if (FallbackHasCharacter(TMP_Settings.fallbackFontAssets, unicode, tryAdd, visited)) return true;
            var def = TMP_Settings.defaultFontAsset;
            if (def == null || !visited.Add(def.GetInstanceID())) return false;
            return HasOrAdds(def, unicode, tryAdd) || FallbackHasCharacter(def.m_FallbackFontAssetTable, unicode, tryAdd, visited);
        }

        static bool HasOrAdds(TMP_FontAsset asset, uint unicode, bool tryAdd)
            => asset.characterLookupTable.ContainsKey(unicode)
                || (tryAdd && asset.m_AtlasPopulationMode != AtlasPopulationMode.Static && asset.TryAddCharacter(unicode, out _));

        // ----- Dynamic population -----

        public bool TryAddCharacters(uint[] unicodes, bool includeFontFeatures = false) => TryAddCharacters(unicodes, out _, includeFontFeatures);

        public bool TryAddCharacters(uint[] unicodes, out uint[] missingUnicodes, bool includeFontFeatures = false)
        {
            var missing = new List<uint>();
            if (unicodes != null && m_AtlasPopulationMode != AtlasPopulationMode.Static)
            {
                RenderGlyphsAhead(unicodes);
                try
                {
                    foreach (uint u in unicodes)
                        if (!characterLookupTable.ContainsKey(u) && !TryAddCharacter(u, out _)) missing.Add(u);
                }
                finally { FontEngine.ForgetGlyphsAhead(); }
            }
            else if (unicodes != null)
            {
                foreach (uint u in unicodes)
                    if (!characterLookupTable.ContainsKey(u)) missing.Add(u);
            }
            missingUnicodes = missing.ToArray();
            return missing.Count == 0;
        }

        public bool TryAddCharacters(string characters, bool includeFontFeatures = false) => TryAddCharacters(characters, out _, includeFontFeatures);

        public bool TryAddCharacters(string characters, out string missingCharacters, bool includeFontFeatures = false)
        {
            var unicodes = new List<uint>();
            if (characters != null)
                for (int i = 0; i < characters.Length; i++)
                {
                    if (char.IsHighSurrogate(characters[i]) && i + 1 < characters.Length && char.IsLowSurrogate(characters[i + 1]))
                    {
                        unicodes.Add((uint)char.ConvertToUtf32(characters[i], characters[i + 1]));
                        i++;
                    }
                    else unicodes.Add(characters[i]);
                }
            bool all = TryAddCharacters(unicodes.ToArray(), out uint[] missing, includeFontFeatures);
            var text = new System.Text.StringBuilder();
            foreach (uint u in missing) text.Append(char.ConvertFromUtf32((int)u));
            missingCharacters = text.ToString();
            return all;
        }

        // Has the engine render the new glyphs of these characters together (in parallel)
        // before TryAddCharacter adds them one by one.
        void RenderGlyphsAhead(uint[] unicodes)
        {
            if ((m_SourceFontFile == null && string.IsNullOrEmpty(m_SourceFontFilePath)) || m_FaceLoadFailed || unicodes.Length < 2) return;
            if (!LoadFace()) return;
            var glyphs = new List<uint>();
            var seen = new HashSet<uint>();
            foreach (uint u in unicodes)
            {
                if (characterLookupTable.ContainsKey(u) || (m_MissingUnicodes != null && m_MissingUnicodes.Contains(u))) continue;
                if (FontEngine.TryGetGlyphIndex(u, out uint glyphIndex) && !glyphLookupTable.ContainsKey(glyphIndex) && seen.Add(glyphIndex)) glyphs.Add(glyphIndex);
            }
            FontEngine.RenderGlyphsAhead(glyphs.ToArray(), m_AtlasPadding, m_AtlasRenderMode);
        }

        // Renders the character's glyph into the current atlas texture (a new one when the
        // current is full and multi-atlas is enabled) and records it in the tables.
        internal bool TryAddCharacter(uint unicode, out TMP_Character character)
        {
            character = null;
            if ((m_SourceFontFile == null && string.IsNullOrEmpty(m_SourceFontFilePath)) || m_FaceLoadFailed) return false;
            if (m_MissingUnicodes != null && m_MissingUnicodes.Contains(unicode)) return false;
            if (!LoadFace())
            {
                m_FaceLoadFailed = true;
                return false;
            }
            if (!FontEngine.TryGetGlyphIndex(unicode, out uint glyphIndex))
            {
                (m_MissingUnicodes ??= new HashSet<uint>()).Add(unicode);
                return false;
            }
            // Another character may already use the glyph.
            if (glyphLookupTable.TryGetValue(glyphIndex, out var existing))
            {
                character = new TMP_Character(unicode, this, existing);
                m_CharacterTable.Add(character);
                m_CharacterLookup[unicode] = character;
                return true;
            }
            m_UsedGlyphRects ??= new List<GlyphRect>();
            m_FreeGlyphRects ??= new List<GlyphRect> { new GlyphRect(0, 0, m_AtlasWidth - 1, m_AtlasHeight - 1) };
            var texture = m_AtlasTextures[m_AtlasTextureIndex];
            // An asset made from a font file starts with a 1 x 1 texture.
            if (texture != null && texture.width == 1 && texture.height == 1 && (m_AtlasWidth > 1 || m_AtlasHeight > 1))
            {
                texture.Reinitialize(m_AtlasWidth, m_AtlasHeight);
                FontEngine.ResetAtlasTexture(texture);
            }
            if (!FontEngine.TryAddGlyphToTexture(glyphIndex, m_AtlasPadding, GlyphPackingMode.BestShortSideFit, m_FreeGlyphRects, m_UsedGlyphRects, m_AtlasRenderMode, texture, out var glyph))
            {
                if (!m_IsMultiAtlasTexturesEnabled) return false;
                // The current atlas is full: continue in a new one.
                m_AtlasTextureIndex++;
                var next = NewAtlasTexture(this, m_AtlasTextureIndex);
                var list = new List<Texture2D>(m_AtlasTextures) { next };
                m_AtlasTextures = list.ToArray();
                m_FreeGlyphRects = new List<GlyphRect> { new GlyphRect(0, 0, m_AtlasWidth - 1, m_AtlasHeight - 1) };
                m_UsedGlyphRects = new List<GlyphRect>();
                if (!FontEngine.TryAddGlyphToTexture(glyphIndex, m_AtlasPadding, GlyphPackingMode.BestShortSideFit, m_FreeGlyphRects, m_UsedGlyphRects, m_AtlasRenderMode, next, out glyph))
                    return false;
            }
            glyph.atlasIndex = m_AtlasTextureIndex;
            m_GlyphTable.Add(glyph);
            m_GlyphLookup[glyphIndex] = glyph;
            character = new TMP_Character(unicode, this, glyph);
            m_CharacterTable.Add(character);
            m_CharacterLookup[unicode] = character;
            if (m_FontFeatureTable != null) AddPairAdjustments(glyphIndex);
            return true;
        }

        // Kerning pairs between the new glyph and the glyphs already in the asset.
        void AddPairAdjustments(uint glyphIndex)
        {
            var records = FontEngine.GetPairAdjustmentRecordsOf(glyphIndex, m_GlyphLookup.Keys);
            if (records == null) return;
            m_FontFeatureTable.m_GlyphPairAdjustmentRecords ??= new List<GlyphPairAdjustmentRecord>();
            foreach (var r in records)
            {
                uint a = r.firstAdjustmentRecord.glyphIndex, b = r.secondAdjustmentRecord.glyphIndex;
                if (a == 0 && b == 0) break;
                if (a != glyphIndex && b != glyphIndex) continue;
                // Records are in design units; the asset stores them at its sampling size.
                float s = m_FaceInfo.pointSize / Mathf.Max(1, m_FaceInfo.unitsPerEM);
                var scaled = new GlyphPairAdjustmentRecord(
                    new GlyphAdjustmentRecord(a, r.firstAdjustmentRecord.glyphValueRecord * s),
                    new GlyphAdjustmentRecord(b, r.secondAdjustmentRecord.glyphValueRecord * s));
                if (m_PairLookup.TryAdd(PairKey(a, b), scaled)) m_FontFeatureTable.m_GlyphPairAdjustmentRecords.Add(scaled);
            }
        }

        bool LoadFace()
        {
            int pointSize = m_CreationSettings.pointSize > 0 ? m_CreationSettings.pointSize : (int)m_FaceInfo.pointSize;
            if (m_SourceFontFile == null)
                return FontEngine.LoadFontFace(m_SourceFontFilePath, pointSize, m_FaceInfo.faceIndex) == FontEngineError.Success;
            return FontEngine.LoadFontFace(m_SourceFontFile, pointSize, m_FaceInfo.faceIndex) == FontEngineError.Success;
        }

        // Removes the dynamically added glyphs and characters and clears the atlas.
        public void ClearFontAssetData(bool setAtlasSizeToZero = false)
        {
            if (m_AtlasPopulationMode == AtlasPopulationMode.Static) return;
            m_GlyphTable?.Clear();
            m_CharacterTable?.Clear();
            m_UsedGlyphRects = new List<GlyphRect>();
            m_FreeGlyphRects = new List<GlyphRect> { new GlyphRect(0, 0, m_AtlasWidth - 1, m_AtlasHeight - 1) };
            m_FontFeatureTable?.m_GlyphPairAdjustmentRecords?.Clear();
            if (m_AtlasTextures != null)
            {
                for (int i = 1; i < m_AtlasTextures.Length; i++) Object.Destroy(m_AtlasTextures[i]);
                if (m_AtlasTextures.Length > 0)
                {
                    FontEngine.ResetAtlasTexture(m_AtlasTextures[0]);
                    m_AtlasTextures = new[] { m_AtlasTextures[0] };
                }
            }
            m_AtlasTextureIndex = 0;
            ReadFontAssetDefinition();
        }
    }

    public partial class TMP_FontFeatureTable
    {
        public TMP_FontFeatureTable()
        {
            m_GlyphPairAdjustmentRecords = new List<GlyphPairAdjustmentRecord>();
            m_LigatureSubstitutionRecords = new List<LigatureSubstitutionRecord>();
            m_MultipleSubstitutionRecords = new List<MultipleSubstitutionRecord>();
            m_MarkToBaseAdjustmentRecords = new List<MarkToBaseAdjustmentRecord>();
            m_MarkToMarkAdjustmentRecords = new List<MarkToMarkAdjustmentRecord>();
        }

        public List<GlyphPairAdjustmentRecord> glyphPairAdjustmentRecords
        {
            get => m_GlyphPairAdjustmentRecords;
            set => m_GlyphPairAdjustmentRecords = value;
        }
    }
}
