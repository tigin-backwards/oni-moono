using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.UI;

namespace TMPro
{
    // A text: its content and settings, laid out into textInfo and a mesh. A property change
    // marks the text changed (havePropertiesChanged) and dirties its vertices and layout; the
    // canvas rebuild (or ForceMeshUpdate) lays it out again.
    public abstract partial class TMP_Text
    {
        // ----- Non-serialized state -----

        readonly ParsedText m_Parsed = new();
        private protected bool m_ParseRequired = true;
        bool m_LayoutCacheValid;
        float m_CachedPreferredWidth = -1f, m_CachedPreferredHeight = -1f;
        float m_CachedPreferredHeightWidth = float.NaN;
        int m_MaxVisibleCharacters = 99999, m_MaxVisibleWords = 99999, m_MaxVisibleLines = 99999;
        bool m_IsTextOverflowing;
        bool m_IsTextTruncated;
        float m_LineSpacingAdjustment;
        float m_CharWidthAdjustment;
        float m_AutoSizedFontSize = -1f;
        // The value code last gave fontSize (NaN: none; the serialized base size then).
        float m_FontSizeSet = float.NaN;
        Extents m_TextExtents;
        float m_Padding;
        bool m_IsUsingBold;
        TMP_Style m_TextStyle;
        int m_TextStyleResolvedHash;
        RectTransform m_RectTransformCache;
        Transform m_TransformCache;
        Mesh m_MeshCache;
        // The text as last parsed (SetCharArray builds it from a buffer).
        string m_SourceCache;
        // The text was last set from characters (SetText(StringBuilder), SetCharArray): U59's
        // text property then counts the same text as a change, once (measured).
        bool m_TextFromCharacters;
        private protected bool m_IgnoreActiveState;

        protected static readonly Color32 s_colorWhite = new(255, 255, 255, 255);

        // Called with the text info after each layout, before the mesh is uploaded.
        public virtual event Action<TMP_TextInfo> OnPreRenderText;

        protected TMP_Text()
        {
            m_text = "";
            m_fontColor32 = s_colorWhite;
            m_fontColor = Color.white;
            m_fontColorGradient = new VertexGradient(Color.white);
            m_faceColor = s_colorWhite;
            m_outlineColor = new Color32(0, 0, 0, 255);
            m_fontSize = 36f;
            m_fontSizeBase = 36f;
            m_fontWeight = FontWeight.Regular;
            m_fontSizeMin = 18f;
            m_fontSizeMax = 72f;
            m_HorizontalAlignment = HorizontalAlignmentOptions.Left;
            m_VerticalAlignment = VerticalAlignmentOptions.Top;
            m_textAlignment = TextAlignmentOptions.Converted;
            m_characterHorizontalScale = 1f;
            m_wordWrappingRatios = 0.4f;
            m_TextWrappingMode = TextWrappingModes.Normal;
            m_overflowMode = TextOverflowModes.Overflow;
            m_firstOverflowCharacterIndex = -1;
            m_enableKerning = true;
            m_isRichText = true;
            m_EmojiFallbackSupport = true;
            m_parseCtrlCharacters = true;
            m_isOrthographic = false;
            m_horizontalMapping = TextureMappingOptions.Character;
            m_verticalMapping = TextureMappingOptions.Character;
            m_geometrySortingOrder = VertexSortingOrder.Normal;
            m_VertexBufferAutoSizeReduction = false;
            m_pageToDisplay = 1;
            m_charWidthMaxAdj = 0f;
            m_lineSpacingMax = 0f;
        }

        // ----- Content -----

        public virtual string text
        {
            get => m_text;
            set
            {
                if (m_text == value && !m_TextFromCharacters) return;
                m_TextFromCharacters = false;
                m_text = value ?? "";
                m_SourceCache = null;
                SourceChanged();
            }
        }

        // Lines run from right to left: each line's characters are placed from its right end.
        public bool isRightToLeftText
        {
            get => m_isRightToLeft;
            set
            {
                if (m_isRightToLeft == value) return;
                m_isRightToLeft = value;
                Changed();
            }
        }

        public ITextPreprocessor textPreprocessor
        {
            get => m_TextPreprocessor;
            set => m_TextPreprocessor = value;
        }

        public virtual void SetText(string sourceText) => SetText(sourceText, true);

        // Unlike the text property, the same text again counts as a change (measured: the text
        // is regenerated and laid out again, its events sent).
        public void SetText(string sourceText, bool syncTextInputBox)
        {
            m_TextFromCharacters = false;
            m_text = sourceText ?? "";
            m_SourceCache = null;
            SourceChanged();
        }

        public void SetText(string sourceText, float arg0) => SetFormatted(sourceText, 1, arg0, 0, 0, 0, 0, 0, 0, 0);
        public void SetText(string sourceText, float arg0, float arg1) => SetFormatted(sourceText, 2, arg0, arg1, 0, 0, 0, 0, 0, 0);
        public void SetText(string sourceText, float arg0, float arg1, float arg2) => SetFormatted(sourceText, 3, arg0, arg1, arg2, 0, 0, 0, 0, 0);
        public void SetText(string sourceText, float arg0, float arg1, float arg2, float arg3) => SetFormatted(sourceText, 4, arg0, arg1, arg2, arg3, 0, 0, 0, 0);
        public void SetText(string sourceText, float arg0, float arg1, float arg2, float arg3, float arg4) => SetFormatted(sourceText, 5, arg0, arg1, arg2, arg3, arg4, 0, 0, 0);
        public void SetText(string sourceText, float arg0, float arg1, float arg2, float arg3, float arg4, float arg5) => SetFormatted(sourceText, 6, arg0, arg1, arg2, arg3, arg4, arg5, 0, 0);
        public void SetText(string sourceText, float arg0, float arg1, float arg2, float arg3, float arg4, float arg5, float arg6) => SetFormatted(sourceText, 7, arg0, arg1, arg2, arg3, arg4, arg5, arg6, 0);
        public void SetText(string sourceText, float arg0, float arg1, float arg2, float arg3, float arg4, float arg5, float arg6, float arg7) => SetFormatted(sourceText, 8, arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7);

        public void SetText(StringBuilder sourceText)
        {
            SetText(sourceText?.ToString(), true);
            m_TextFromCharacters = true;
        }

        public void SetText(char[] sourceText) => SetCharArray(sourceText);

        public void SetText(char[] sourceText, int start, int length) => SetCharArray(sourceText, start, length);

        public void SetCharArray(char[] sourceText) => SetCharArray(sourceText, 0, sourceText?.Length ?? 0);

        // The same characters again count as a change, as for SetText.
        public void SetCharArray(char[] sourceText, int start, int length)
        {
            string s = sourceText == null ? "" : new string(sourceText, start, length);
            m_text = s;
            m_TextFromCharacters = true;
            m_SourceCache = null;
            SourceChanged();
        }

        // {N} is replaced by argument N; {N:D} by argument N with D decimal places.
        void SetFormatted(string format, int count, float a0, float a1, float a2, float a3, float a4, float a5, float a6, float a7)
        {
            if (format == null)
            {
                SetText((string)null, true);
                return;
            }
            Span<float> args = stackalloc float[] { a0, a1, a2, a3, a4, a5, a6, a7 };
            var sb = new StringBuilder(format.Length + 16);
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                if (c == '{')
                {
                    int close = format.IndexOf('}', i + 1);
                    if (close > i + 1)
                    {
                        var spec = format.AsSpan(i + 1, close - i - 1);
                        int colon = spec.IndexOf(':');
                        var indexPart = colon < 0 ? spec : spec[..colon];
                        if (int.TryParse(indexPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) && index < count)
                        {
                            int decimals = 0;
                            if (colon >= 0 && !int.TryParse(spec[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out decimals)) decimals = 0;
                            AppendNumber(sb, args[index], decimals);
                            i = close;
                            continue;
                        }
                    }
                }
                sb.Append(c);
            }
            SetText(sb.ToString(), true);
        }

        static void AppendNumber(StringBuilder sb, float value, int decimals)
        {
            sb.Append(Math.Round((double)value, Math.Min(decimals, 15), MidpointRounding.AwayFromZero).ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture));
        }

        void SourceChanged()
        {
            m_ParseRequired = true;
            InvalidateLayoutCache();
            m_havePropertiesChanged = true;
            SetVerticesDirty();
            SetLayoutDirty();
        }

        // The text as parsed, without the rich-text tags.
        public string GetParsedText()
        {
            if (m_textInfo == null) return "";
            var sb = new StringBuilder(m_textInfo.characterCount);
            for (int i = 0; i < m_textInfo.characterCount; i++) sb.Append(m_textInfo.characterInfo[i].character);
            return sb.ToString();
        }

        // ----- Fonts and materials -----

        // Set to null on a text that has woken, the font is the default font asset, as if that
        // asset had been assigned (measured on the shipped package: "A font set to null"). A
        // text that has not woken keeps null, and takes the default when it wakes.
        public TMP_FontAsset font
        {
            get => m_fontAsset;
            set
            {
                if (value == null && m_Woken) value = TMP_Settings.defaultFontAsset;
                if (m_fontAsset == value) return;
                m_fontAsset = value;
                LoadFontAsset();
                FontChanged();
            }
        }

        // Whether the text has woken (Awake has run).
        private protected bool m_Woken;

        // Called when the font asset changes: the shared material follows the font's.
        private protected virtual void LoadFontAsset()
        {
            if (m_fontAsset == null) return;
            m_sharedMaterial = m_fontAsset.material;
            m_fontSharedMaterials = null;
            m_fontMaterial = null;
            m_Padding = 0f;
        }

        void FontChanged()
        {
            m_ParseRequired = true;
            InvalidateLayoutCache();
            m_havePropertiesChanged = true;
            SetVerticesDirty();
            SetLayoutDirty();
            SetMaterialDirty();
        }

        public virtual Material fontSharedMaterial
        {
            get => m_sharedMaterial;
            set
            {
                if (m_sharedMaterial == value) return;
                m_sharedMaterial = value;
                m_Padding = 0f;
                m_ParseRequired = true;
                m_havePropertiesChanged = true;
                InvalidateLayoutCache();
                SetVerticesDirty();
                SetMaterialDirty();
            }
        }

        public virtual Material[] fontSharedMaterials
        {
            get
            {
                var materials = new Material[m_Parsed.Materials.Count == 0 ? 1 : m_Parsed.Materials.Count];
                materials[0] = m_sharedMaterial;
                for (int i = 1; i < materials.Length; i++) materials[i] = m_Parsed.Materials[i].Material;
                return materials;
            }
            set
            {
                if (value == null || value.Length == 0) return;
                fontSharedMaterial = value[0];
            }
        }

        // An instance of the shared material owned by this text.
        public Material fontMaterial
        {
            get
            {
                if (m_fontMaterial == null && m_sharedMaterial != null)
                {
                    m_fontMaterial = new Material(m_sharedMaterial) { name = m_sharedMaterial.name + " (Instance)" };
                    m_sharedMaterial = m_fontMaterial;
                    m_Padding = 0f;
                    m_havePropertiesChanged = true;
                    SetVerticesDirty();
                    SetMaterialDirty();
                }
                return m_fontMaterial;
            }
            set
            {
                if (value == null || m_sharedMaterial == value) return;
                m_fontMaterial = value;
                m_sharedMaterial = value;
                m_Padding = 0f;
                m_havePropertiesChanged = true;
                InvalidateLayoutCache();
                SetVerticesDirty();
                SetMaterialDirty();
            }
        }

        public Material[] fontMaterials
        {
            get
            {
                var shared = fontSharedMaterials;
                shared[0] = fontMaterial;
                return shared;
            }
            set
            {
                if (value == null || value.Length == 0) return;
                fontMaterial = value[0];
            }
        }

        // ----- Colour -----

        public override Color color
        {
            get => m_fontColor;
            set
            {
                if (m_fontColor == value) return;
                m_havePropertiesChanged = true;
                m_fontColor = value;
                m_fontColor32 = value;
                m_ParseRequired = true;
                SetVerticesDirty();
            }
        }

        public float alpha
        {
            get => m_fontColor.a;
            set
            {
                if (m_fontColor.a == value) return;
                m_fontColor.a = value;
                m_fontColor32 = m_fontColor;
                m_havePropertiesChanged = true;
                m_ParseRequired = true;
                SetVerticesDirty();
            }
        }

        public bool enableVertexGradient
        {
            get => m_enableVertexGradient;
            set
            {
                if (m_enableVertexGradient == value) return;
                m_havePropertiesChanged = true;
                m_enableVertexGradient = value;
                SetVerticesDirty();
            }
        }

        public VertexGradient colorGradient
        {
            get => m_fontColorGradient;
            set
            {
                m_havePropertiesChanged = true;
                m_fontColorGradient = value;
                SetVerticesDirty();
            }
        }

        public TMP_ColorGradient colorGradientPreset
        {
            get => m_fontColorGradientPreset;
            set
            {
                m_havePropertiesChanged = true;
                m_fontColorGradientPreset = value;
                SetVerticesDirty();
            }
        }

        public TMP_SpriteAsset spriteAsset
        {
            get => m_spriteAsset;
            set
            {
                m_spriteAsset = value;
                Changed(parse: true);
            }
        }

        public bool tintAllSprites
        {
            get => m_tintAllSprites;
            set
            {
                if (m_tintAllSprites == value) return;
                m_tintAllSprites = value;
                m_havePropertiesChanged = true;
                SetVerticesDirty();
            }
        }

        public TMP_StyleSheet styleSheet
        {
            get => m_StyleSheet;
            set
            {
                m_StyleSheet = value;
                m_TextStyle = null;
                Changed(parse: true);
            }
        }

        // The default style of the text (applied around its source), from its style sheet or
        // the default one.
        public TMP_Style textStyle
        {
            get
            {
                if (m_TextStyle == null || m_TextStyleResolvedHash != m_TextStyleHashCode)
                {
                    m_TextStyleResolvedHash = m_TextStyleHashCode;
                    m_TextStyle = m_TextStyleHashCode == 0 ? null : (m_StyleSheet != null ? m_StyleSheet.GetStyle(m_TextStyleHashCode) : null) ?? TMP_StyleSheet.instance?.GetStyle(m_TextStyleHashCode);
                }
                return m_TextStyle;
            }
            set
            {
                m_TextStyle = value;
                m_TextStyleHashCode = value?.hashCode ?? 0;
                m_TextStyleResolvedHash = m_TextStyleHashCode;
                Changed(parse: true);
            }
        }

        public bool overrideColorTags
        {
            get => m_overrideHtmlColors;
            set
            {
                if (m_overrideHtmlColors == value) return;
                m_havePropertiesChanged = true;
                m_overrideHtmlColors = value;
                SetVerticesDirty();
            }
        }

        public Color32 faceColor
        {
            get
            {
                if (m_sharedMaterial == null) return m_faceColor;
                m_faceColor = m_sharedMaterial.GetColor(ShaderUtilities.ID_FaceColor);
                return m_faceColor;
            }
            set
            {
                if (m_faceColor.r == value.r && m_faceColor.g == value.g && m_faceColor.b == value.b && m_faceColor.a == value.a) return;
                SetFaceColor(value);
                m_havePropertiesChanged = true;
                m_faceColor = value;
                SetVerticesDirty();
                SetMaterialDirty();
            }
        }

        public Color32 outlineColor
        {
            get
            {
                if (m_sharedMaterial == null) return m_outlineColor;
                m_outlineColor = m_sharedMaterial.GetColor(ShaderUtilities.ID_OutlineColor);
                return m_outlineColor;
            }
            set
            {
                if (m_outlineColor.r == value.r && m_outlineColor.g == value.g && m_outlineColor.b == value.b && m_outlineColor.a == value.a) return;
                SetOutlineColor(value);
                m_havePropertiesChanged = true;
                m_outlineColor = value;
                SetVerticesDirty();
            }
        }

        public float outlineWidth
        {
            get => m_sharedMaterial == null ? 0f : m_sharedMaterial.GetFloat(ShaderUtilities.ID_OutlineWidth);
            set
            {
                SetOutlineThickness(value);
                m_havePropertiesChanged = true;
                m_Padding = 0f;
                SetVerticesDirty();
            }
        }

        // The colours and thickness are set on an instance of the material.
        private protected void SetFaceColor(Color32 color)
        {
            var material = fontMaterial;
            if (material != null) material.SetColor(ShaderUtilities.ID_FaceColor, color);
        }

        private protected void SetOutlineColor(Color32 color)
        {
            var material = fontMaterial;
            if (material != null) material.SetColor(ShaderUtilities.ID_OutlineColor, color);
        }

        private protected void SetOutlineThickness(float thickness)
        {
            var material = fontMaterial;
            if (material == null) return;
            material.SetFloat(ShaderUtilities.ID_OutlineWidth, Mathf.Clamp01(thickness));
            ShaderUtilities.UpdateShaderRatios(material);
        }

        // ----- Size and style -----

        // The size used by the last layout (the auto-sized one when auto-sizing).
        public float fontSize
        {
            get => m_fontSize;
            set
            {
                m_FontSizeSet = value;
                if (m_fontSize == value) return;
                m_havePropertiesChanged = true;
                m_fontSize = value;
                if (!m_enableAutoSizing) m_fontSizeBase = m_fontSize;
                Changed();
            }
        }

        public FontWeight fontWeight
        {
            get => m_fontWeight;
            set
            {
                if (m_fontWeight == value) return;
                m_fontWeight = value;
                Changed(parse: true);
            }
        }

        public float pixelsPerUnit
        {
            get
            {
                var canvas = this is TextMeshProUGUI ui ? ui.canvas : null;
                if (canvas == null) return 1f;
                if (m_fontAsset == null) return canvas.scaleFactor;
                if (m_fontAsset.faceInfo.pointSize <= 0f || m_fontSize <= 0f) return 1f;
                return m_fontSize / m_fontAsset.faceInfo.pointSize;
            }
        }

        public bool enableAutoSizing
        {
            get => m_enableAutoSizing;
            set
            {
                if (m_enableAutoSizing == value) return;
                m_enableAutoSizing = value;
                Changed();
            }
        }

        public float fontSizeMin
        {
            get => m_fontSizeMin;
            set
            {
                if (m_fontSizeMin == value) return;
                m_fontSizeMin = value;
                Changed();
            }
        }

        public float fontSizeMax
        {
            get => m_fontSizeMax;
            set
            {
                if (m_fontSizeMax == value) return;
                m_fontSizeMax = value;
                Changed();
            }
        }

        public FontStyles fontStyle
        {
            get => m_fontStyle;
            set
            {
                if (m_fontStyle == value) return;
                m_fontStyle = value;
                Changed(parse: true);
            }
        }

        public bool isUsingBold => m_IsUsingBold;

        // ----- Placement -----

        public HorizontalAlignmentOptions horizontalAlignment
        {
            get => m_HorizontalAlignment;
            set
            {
                if (m_HorizontalAlignment == value) return;
                m_HorizontalAlignment = value;
                Changed(layout: false);
            }
        }

        public VerticalAlignmentOptions verticalAlignment
        {
            get => m_VerticalAlignment;
            set
            {
                if (m_VerticalAlignment == value) return;
                m_VerticalAlignment = value;
                Changed(layout: false);
            }
        }

        public TextAlignmentOptions alignment
        {
            get => (TextAlignmentOptions)((int)m_HorizontalAlignment | (int)m_VerticalAlignment);
            set
            {
                var h = (HorizontalAlignmentOptions)((int)value & 0xFF);
                var v = (VerticalAlignmentOptions)((int)value & 0xFF00);
                if (m_HorizontalAlignment == h && m_VerticalAlignment == v) return;
                m_HorizontalAlignment = h;
                m_VerticalAlignment = v;
                Changed(layout: false);
            }
        }

        // The setters change the two halves only, never the stored m_textAlignment (measured):
        // a text whose data stores an alignment loses what was set before it first wakes.
        // An alignment stored by older data (m_textAlignment) replaces the two alignments when
        // the text first wakes, and is then marked converted.
        // The oldest format numbered the alignments 0-15: rows Top, Middle, Bottom and Baseline
        // of Left, Center, Right and Justified (measured: the game's data has texts with the
        // values 0 and 5 that the shipped package shows top left and centred). Other values
        // split into their bytes.
        private protected void ConvertAlignment()
        {
            if (m_textAlignment == TextAlignmentOptions.Converted) return;
            int a = (int)m_textAlignment;
            if (a >= 0 && a < 16)
            {
                m_HorizontalAlignment = (HorizontalAlignmentOptions)(1 << (a & 3));
                m_VerticalAlignment = (a >> 2) switch
                {
                    0 => VerticalAlignmentOptions.Top,
                    1 => VerticalAlignmentOptions.Middle,
                    2 => VerticalAlignmentOptions.Bottom,
                    _ => VerticalAlignmentOptions.Baseline,
                };
            }
            else
            {
                m_HorizontalAlignment = (HorizontalAlignmentOptions)(a & 0xFF);
                m_VerticalAlignment = (VerticalAlignmentOptions)(a & 0xFF00);
            }
            m_textAlignment = TextAlignmentOptions.Converted;
        }

        public float characterSpacing
        {
            get => m_characterSpacing;
            set
            {
                if (m_characterSpacing == value) return;
                m_characterSpacing = value;
                Changed();
            }
        }

        public float characterHorizontalScale
        {
            get => m_characterHorizontalScale;
            set
            {
                if (m_characterHorizontalScale == value) return;
                m_characterHorizontalScale = value;
                Changed();
            }
        }

        public float wordSpacing
        {
            get => m_wordSpacing;
            set
            {
                if (m_wordSpacing == value) return;
                m_wordSpacing = value;
                Changed();
            }
        }

        public float lineSpacing
        {
            get => m_lineSpacing;
            set
            {
                if (m_lineSpacing == value) return;
                m_lineSpacing = value;
                Changed();
            }
        }

        public float lineSpacingAdjustment
        {
            get => m_lineSpacingMax;
            set
            {
                if (m_lineSpacingMax == value) return;
                m_lineSpacingMax = value;
                Changed();
            }
        }

        public float paragraphSpacing
        {
            get => m_paragraphSpacing;
            set
            {
                if (m_paragraphSpacing == value) return;
                m_paragraphSpacing = value;
                Changed();
            }
        }

        public float characterWidthAdjustment
        {
            get => m_charWidthMaxAdj;
            set
            {
                if (m_charWidthMaxAdj == value) return;
                m_charWidthMaxAdj = value;
                Changed();
            }
        }

        public TextWrappingModes textWrappingMode
        {
            get => m_TextWrappingMode;
            set
            {
                if (m_TextWrappingMode == value) return;
                m_TextWrappingMode = value;
                Changed();
            }
        }

        [Obsolete("The enableWordWrapping property is now obsolete. Please use the textWrappingMode property instead.")]
        public bool enableWordWrapping
        {
            get => m_TextWrappingMode == TextWrappingModes.Normal || m_TextWrappingMode == TextWrappingModes.PreserveWhitespace;
            set
            {
                var mode = value ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
                if (m_TextWrappingMode == mode) return;
                m_TextWrappingMode = mode;
                Changed();
            }
        }

        public float wordWrappingRatios
        {
            get => m_wordWrappingRatios;
            set
            {
                if (m_wordWrappingRatios == value) return;
                m_wordWrappingRatios = value;
                Changed();
            }
        }

        public TextOverflowModes overflowMode
        {
            get => m_overflowMode;
            set
            {
                if (m_overflowMode == value) return;
                m_overflowMode = value;
                // Ellipsis resolves its character when the text is parsed.
                Changed(parse: true);
            }
        }

        public bool isTextOverflowing => m_firstOverflowCharacterIndex != -1;

        public int firstOverflowCharacterIndex => m_firstOverflowCharacterIndex;

        public TMP_Text linkedTextComponent
        {
            get => m_linkedTextComponent;
            set
            {
                if (value == this) return;
                if (m_linkedTextComponent != value)
                {
                    if (m_linkedTextComponent != null) m_linkedTextComponent.parentLinkedComponent = null;
                    m_linkedTextComponent = value;
                    if (value != null) value.parentLinkedComponent = this;
                }
                Changed();
            }
        }

        public bool isTextTruncated => m_IsTextTruncated;

        public bool enableKerning
        {
            get => m_enableKerning;
            set
            {
                if (m_enableKerning == value) return;
                m_enableKerning = value;
                Changed();
            }
        }

        public List<OTL_FeatureTag> fontFeatures
        {
            get => m_ActiveFontFeatures ??= new List<OTL_FeatureTag> { OTL_FeatureTag.kern };
            set
            {
                m_ActiveFontFeatures = value;
                Changed();
            }
        }

        public bool extraPadding
        {
            get => m_enableExtraPadding;
            set
            {
                if (m_enableExtraPadding == value) return;
                m_enableExtraPadding = value;
                m_Padding = 0f;
                Changed();
            }
        }

        public bool richText
        {
            get => m_isRichText;
            set
            {
                if (m_isRichText == value) return;
                m_isRichText = value;
                Changed(parse: true);
            }
        }

        public bool emojiFallbackSupport
        {
            get => m_EmojiFallbackSupport;
            set
            {
                if (m_EmojiFallbackSupport == value) return;
                m_EmojiFallbackSupport = value;
                Changed(parse: true);
            }
        }

        public bool parseCtrlCharacters
        {
            get => m_parseCtrlCharacters;
            set
            {
                if (m_parseCtrlCharacters == value) return;
                m_parseCtrlCharacters = value;
                Changed(parse: true);
            }
        }

        public bool isOverlay
        {
            get => m_isOverlay;
            set
            {
                if (m_isOverlay == value) return;
                m_isOverlay = value;
                SetShaderDepth();
                m_havePropertiesChanged = true;
                SetVerticesDirty();
            }
        }

        bool m_isOverlay;

        // The overlay setting draws the text on top regardless of depth; UI materials ignore it.
        private protected virtual void SetShaderDepth() { }

        public bool isOrthographic
        {
            get => m_isOrthographic;
            set
            {
                if (m_isOrthographic == value) return;
                m_isOrthographic = value;
                m_havePropertiesChanged = true;
                SetVerticesDirty();
            }
        }

        public bool enableCulling
        {
            get => m_isCullingEnabled;
            set
            {
                if (m_isCullingEnabled == value) return;
                m_isCullingEnabled = value;
                SetCulling();
                m_havePropertiesChanged = true;
            }
        }

        private protected virtual void SetCulling() { }

        public bool ignoreVisibility
        {
            get => m_ignoreCulling;
            set
            {
                if (m_ignoreCulling == value) return;
                m_havePropertiesChanged = true;
                m_ignoreCulling = value;
            }
        }

        public TextureMappingOptions horizontalMapping
        {
            get => m_horizontalMapping;
            set
            {
                if (m_horizontalMapping == value) return;
                m_havePropertiesChanged = true;
                m_horizontalMapping = value;
                SetVerticesDirty();
            }
        }

        public TextureMappingOptions verticalMapping
        {
            get => m_verticalMapping;
            set
            {
                if (m_verticalMapping == value) return;
                m_havePropertiesChanged = true;
                m_verticalMapping = value;
                SetVerticesDirty();
            }
        }

        public float mappingUvLineOffset
        {
            get => m_uvLineOffset;
            set
            {
                if (m_uvLineOffset == value) return;
                m_havePropertiesChanged = true;
                m_uvLineOffset = value;
                SetVerticesDirty();
            }
        }

        public VertexSortingOrder geometrySortingOrder
        {
            get => m_geometrySortingOrder;
            set
            {
                m_geometrySortingOrder = value;
                m_havePropertiesChanged = true;
                SetVerticesDirty();
            }
        }

        public bool isTextObjectScaleStatic
        {
            get => m_IsTextObjectScaleStatic;
            set => m_IsTextObjectScaleStatic = value;
        }

        public bool vertexBufferAutoSizeReduction
        {
            get => m_VertexBufferAutoSizeReduction;
            set
            {
                m_VertexBufferAutoSizeReduction = value;
                m_havePropertiesChanged = true;
                SetVerticesDirty();
            }
        }

        // ----- Visibility -----

        public int firstVisibleCharacter
        {
            get => m_firstVisibleCharacter;
            set
            {
                if (m_firstVisibleCharacter == value) return;
                m_havePropertiesChanged = true;
                m_firstVisibleCharacter = value;
                SetVerticesDirty();
            }
        }

        public int maxVisibleCharacters
        {
            get => m_MaxVisibleCharacters;
            set
            {
                if (m_MaxVisibleCharacters == value) return;
                m_havePropertiesChanged = true;
                m_MaxVisibleCharacters = value;
                SetVerticesDirty();
            }
        }

        public int maxVisibleWords
        {
            get => m_MaxVisibleWords;
            set
            {
                if (m_MaxVisibleWords == value) return;
                m_havePropertiesChanged = true;
                m_MaxVisibleWords = value;
                SetVerticesDirty();
            }
        }

        public int maxVisibleLines
        {
            get => m_MaxVisibleLines;
            set
            {
                if (m_MaxVisibleLines == value) return;
                m_havePropertiesChanged = true;
                m_MaxVisibleLines = value;
                SetVerticesDirty();
            }
        }

        public bool useMaxVisibleDescender
        {
            get => m_useMaxVisibleDescender;
            set
            {
                if (m_useMaxVisibleDescender == value) return;
                m_havePropertiesChanged = true;
                m_useMaxVisibleDescender = value;
                SetVerticesDirty();
            }
        }

        public int pageToDisplay
        {
            get => m_pageToDisplay;
            set
            {
                if (m_pageToDisplay == value) return;
                m_havePropertiesChanged = true;
                m_pageToDisplay = value;
                SetVerticesDirty();
            }
        }

        public virtual Vector4 margin
        {
            get => m_margin;
            set
            {
                if (m_margin == value) return;
                m_margin = value;
                // The preferred sizes keep their margins until the text or its formatting
                // changes (as the shipped package's do).
                float width = m_CachedPreferredWidth, height = m_CachedPreferredHeight, heightWidth = m_CachedPreferredHeightWidth;
                Changed();
                m_CachedPreferredWidth = width;
                m_CachedPreferredHeight = height;
                m_CachedPreferredHeightWidth = heightWidth;
            }
        }

        // ----- Results -----

        public TMP_TextInfo textInfo
        {
            get
            {
                if (m_textInfo == null) m_textInfo = new TMP_TextInfo(this);
                return m_textInfo;
            }
        }

        public bool havePropertiesChanged
        {
            get => m_havePropertiesChanged;
            set
            {
                if (m_havePropertiesChanged == value) return;
                m_havePropertiesChanged = value;
                if (!value) return;
                m_ParseRequired = true;
                InvalidateLayoutCache();
                SetAllDirty();
            }
        }

        public bool isUsingLegacyAnimationComponent
        {
            get => m_isUsingLegacyAnimationComponent;
            set => m_isUsingLegacyAnimationComponent = value;
        }

        public new Transform transform
        {
            get
            {
                if (m_TransformCache == null) m_TransformCache = base.transform;
                return m_TransformCache;
            }
        }

        public new RectTransform rectTransform
        {
            get
            {
                if (m_RectTransformCache == null) m_RectTransformCache = GetComponent<RectTransform>();
                return m_RectTransformCache;
            }
        }

        public virtual bool autoSizeTextContainer { get; set; }

        public virtual Mesh mesh => m_MeshCache;

        public bool isVolumetricText
        {
            get => m_isVolumetricText;
            set
            {
                if (m_isVolumetricText == value) return;
                m_isVolumetricText = value;
                m_havePropertiesChanged = true;
                SetVerticesDirty();
            }
        }

        // Kept for old PLib, which sets it on input fields' text; it changes nothing.
        [Obsolete("This does nothing. It was added to prevent PLib from breaking after upgrading to Unity 6.3")]
        public bool ignoreRectMaskCulling { get; set; }

        // The bounds of the mesh: every vertex, the unused ones at the local origin included
        // (measured: the shipped package's equal its mesh's bounds, pivot included).
        public Bounds bounds => m_MeshCache != null ? m_MeshCache.bounds : default;

        // The bounds of the text's characters.
        public Bounds textBounds
        {
            get
            {
                if (m_textInfo == null) return default;
                var e = m_TextExtents;
                if (e.max.x < e.min.x) return default;
                var center = new Vector3((e.min.x + e.max.x) * 0.5f, (e.min.y + e.max.y) * 0.5f, 0f);
                return new Bounds(center, new Vector3(e.max.x - e.min.x, e.max.y - e.min.y, 0f));
            }
        }

        // ----- Helpers -----

        // A property affecting layout changed: re-layout (and re-parse when the parsed
        // elements depend on it).
        private protected void Changed(bool parse = false, bool layout = true)
        {
            if (parse) m_ParseRequired = true;
            InvalidateLayoutCache();
            m_havePropertiesChanged = true;
            SetVerticesDirty();
            if (layout) SetLayoutDirty();
        }

        private protected void InvalidateLayoutCache()
        {
            m_LayoutCacheValid = false;
            m_CachedPreferredWidth = -1f;
            m_CachedPreferredHeight = -1f;
            m_CachedPreferredHeightWidth = float.NaN;
        }

        private protected Mesh MeshCache
        {
            get => m_MeshCache;
            set => m_MeshCache = value;
        }
    }
}
