using System.Collections.Generic;
using OniMoono.Engine.IMGUI;

namespace UnityEngine
{
    /// How an IMGUI control looks: backgrounds and text colours per state, the nine-slice
    /// border, margins, padding, overflow, font and text settings, and fixed sizes.
    public sealed partial class GUIStyle
    {
        // The fields carry the names of Unity's serialized GUIStyle; the serializer reads and
        // writes them directly (ScriptData.ReadGUIStyle, JsonWriter.WriteGUIStyle).
        internal string m_Name = "";
        internal GUIStyleState m_Normal = new(), m_Hover = new(), m_Active = new(), m_Focused = new();
        internal GUIStyleState m_OnNormal = new(), m_OnHover = new(), m_OnActive = new(), m_OnFocused = new();
        internal RectOffset m_Border = new(), m_Margin = new(), m_Padding = new(), m_Overflow = new();
        internal Font m_Font;
        internal int m_FontSize;
        internal FontStyle m_FontStyle;
        internal TextAnchor m_Alignment;
        internal bool m_WordWrap;
        internal bool m_RichText = true;
        internal TextClipping m_TextClipping;
        internal ImagePosition m_ImagePosition;
        internal Vector2 m_ContentOffset;
        internal float m_FixedWidth;
        internal float m_FixedHeight;
        internal bool m_StretchWidth = true;
        internal bool m_StretchHeight;

        static GUIStyle noneStyle;

        public GUIStyle() { }

        /// A deep copy of `other`; a null `other` copies the current skin's error style.
        public GUIStyle(GUIStyle other)
        {
            if (other == null)
            {
                Debug.LogError("Copied a null GUIStyle; the skin's error style is used instead.");
                other = GUISkin.ErrorStyle;
            }
            CopyFrom(other);
        }

        void CopyFrom(GUIStyle o)
        {
            m_Name = o.m_Name;
            m_Normal.CopyFrom(o.m_Normal);
            m_Hover.CopyFrom(o.m_Hover);
            m_Active.CopyFrom(o.m_Active);
            m_Focused.CopyFrom(o.m_Focused);
            m_OnNormal.CopyFrom(o.m_OnNormal);
            m_OnHover.CopyFrom(o.m_OnHover);
            m_OnActive.CopyFrom(o.m_OnActive);
            m_OnFocused.CopyFrom(o.m_OnFocused);
            Copy(m_Border, o.m_Border);
            Copy(m_Margin, o.m_Margin);
            Copy(m_Padding, o.m_Padding);
            Copy(m_Overflow, o.m_Overflow);
            m_Font = o.m_Font;
            m_FontSize = o.m_FontSize;
            m_FontStyle = o.m_FontStyle;
            m_Alignment = o.m_Alignment;
            m_WordWrap = o.m_WordWrap;
            m_RichText = o.m_RichText;
            m_TextClipping = o.m_TextClipping;
            m_ImagePosition = o.m_ImagePosition;
            m_ContentOffset = o.m_ContentOffset;
            m_FixedWidth = o.m_FixedWidth;
            m_FixedHeight = o.m_FixedHeight;
            m_StretchWidth = o.m_StretchWidth;
            m_StretchHeight = o.m_StretchHeight;
        }

        static void Copy(RectOffset to, RectOffset from)
        {
            from ??= new RectOffset();
            to.left = from.left;
            to.right = from.right;
            to.top = from.top;
            to.bottom = from.bottom;
        }

        /// An empty style: no backgrounds, no padding, stretching horizontally.
        public static GUIStyle none => noneStyle ??= new GUIStyle();

        /// The current skin's style of that name.
        public static implicit operator GUIStyle(string str) => GUIStateData.CurrentSkin.GetStyle(str);

        public string name
        {
            get => m_Name ?? "";
            set => m_Name = value;
        }

        // The states and offsets are the style's own objects: assigning copies the values in,
        // so that changing what the getter returned changes the style.
        public GUIStyleState normal { get => m_Normal; set => AssignState(m_Normal, value); }
        public GUIStyleState hover { get => m_Hover; set => AssignState(m_Hover, value); }
        public GUIStyleState active { get => m_Active; set => AssignState(m_Active, value); }
        public GUIStyleState focused { get => m_Focused; set => AssignState(m_Focused, value); }
        public GUIStyleState onNormal { get => m_OnNormal; set => AssignState(m_OnNormal, value); }
        public GUIStyleState onHover { get => m_OnHover; set => AssignState(m_OnHover, value); }
        public GUIStyleState onActive { get => m_OnActive; set => AssignState(m_OnActive, value); }
        public GUIStyleState onFocused { get => m_OnFocused; set => AssignState(m_OnFocused, value); }

        static void AssignState(GUIStyleState own, GUIStyleState value)
        {
            if (value == null) return;
            if (!ReferenceEquals(own, value)) own.CopyFrom(value);
        }

        public RectOffset border { get => m_Border; set => Copy(m_Border, value); }
        public RectOffset margin { get => m_Margin; set => Copy(m_Margin, value); }
        public RectOffset padding { get => m_Padding; set => Copy(m_Padding, value); }
        public RectOffset overflow { get => m_Overflow; set => Copy(m_Overflow, value); }

        public Font font { get => m_Font; set => m_Font = value; }
        /// 0: the font's own size.
        public int fontSize { get => m_FontSize; set => m_FontSize = value; }
        public FontStyle fontStyle { get => m_FontStyle; set => m_FontStyle = value; }
        public TextAnchor alignment { get => m_Alignment; set => m_Alignment = value; }
        public bool wordWrap { get => m_WordWrap; set => m_WordWrap = value; }
        public bool richText { get => m_RichText; set => m_RichText = value; }
        public TextClipping clipping { get => m_TextClipping; set => m_TextClipping = value; }
        public ImagePosition imagePosition { get => m_ImagePosition; set => m_ImagePosition = value; }
        public Vector2 contentOffset { get => m_ContentOffset; set => m_ContentOffset = value; }
        /// 0: no fixed width.
        public float fixedWidth { get => m_FixedWidth; set => m_FixedWidth = value; }
        /// 0: no fixed height.
        public float fixedHeight { get => m_FixedHeight; set => m_FixedHeight = value; }
        public bool stretchWidth { get => m_StretchWidth; set => m_StretchWidth = value; }
        public bool stretchHeight { get => m_StretchHeight; set => m_StretchHeight = value; }

        /// The height of one line of text in the style's font.
        public float lineHeight => GUIText.LineHeight(ResolvedFont, m_FontSize);

        /// A word-wrapping style's height depends on the width it is given.
        public bool isHeightDependantOnWidth => m_FixedHeight == 0f && m_WordWrap && m_ImagePosition != ImagePosition.ImageOnly;

        /// The style's font, else the current skin's, else the built-in LegacyRuntime font.
        internal Font ResolvedFont
        {
            get
            {
                if (m_Font) return m_Font;
                var skinFont = GUIStateData.CurrentSkin?.font;
                return skinFont ? skinFont : GUIText.BuiltinFont;
            }
        }

        public override string ToString() => $"GUIStyle '{name}'";

        // ----- Measurement -----

        bool ShowsText(GUIContent content) => m_ImagePosition != ImagePosition.ImageOnly && !string.IsNullOrEmpty(content.text);

        bool ShowsImage(GUIContent content) => m_ImagePosition != ImagePosition.TextOnly && content.image;

        /// The content's size without padding: text unwrapped (wrapWidth ≤ 0) or wrapped.
        Vector2 ContentSize(GUIContent content, float wrapWidth)
        {
            bool text = ShowsText(content), image = ShowsImage(content);
            var textSize = text || !image ? GUIText.Size(text ? content.text : "", ResolvedFont, m_FontSize, m_FontStyle, m_RichText, wrapWidth) : Vector2.zero;
            if (!image) return textSize;
            var imageSize = new Vector2(content.image.width, content.image.height);
            if (!text) return imageSize;
            return m_ImagePosition == ImagePosition.ImageAbove
                ? new Vector2(Mathf.Max(imageSize.x, textSize.x), imageSize.y + textSize.y)
                : new Vector2(imageSize.x + textSize.x, Mathf.Max(imageSize.y, textSize.y));
        }

        /// The size the content takes with this style: the content plus the padding. A fixed
        /// width or height replaces that axis. Empty content measures one empty line.
        public Vector2 CalcSize(GUIContent content)
        {
            content ??= GUIContent.none;
            var size = ContentSize(content, 0f);
            size.x += m_Padding.horizontal;
            size.y += m_Padding.vertical;
            if (m_FixedWidth != 0f) size.x = m_FixedWidth;
            if (m_FixedHeight != 0f) size.y = m_FixedHeight;
            return size;
        }

        /// The height of the content at `width`, wrapped when the style word-wraps.
        public float CalcHeight(GUIContent content, float width)
        {
            if (m_FixedHeight != 0f) return m_FixedHeight;
            content ??= GUIContent.none;
            float wrap = 0f;
            if (m_WordWrap)
            {
                wrap = width - m_Padding.horizontal;
                if (ShowsImage(content) && ShowsText(content) && m_ImagePosition == ImagePosition.ImageLeft) wrap -= content.image.width;
                wrap = Mathf.Max(1f, wrap);
            }
            return ContentSize(content, wrap).y + m_Padding.vertical;
        }

        const float WrappedMinWidth = 32f;

        /// The width range of the content: `maxWidth` its unwrapped width (CalcSize), and
        /// `minWidth` the same, or when the style word-wraps 32 or `maxWidth` if that is smaller,
        /// whatever the words ("GUILayout, measured", part 5, rule 1).
        public void CalcMinMaxWidth(GUIContent content, out float minWidth, out float maxWidth)
        {
            content ??= GUIContent.none;
            if (m_FixedWidth != 0f)
            {
                minWidth = maxWidth = m_FixedWidth;
                return;
            }
            maxWidth = CalcSize(content).x;
            if (!m_WordWrap || !ShowsText(content))
            {
                minWidth = maxWidth;
                return;
            }
            minWidth = Mathf.Min(WrappedMinWidth, maxWidth);
        }

        /// The rect the content goes in: `position` shrunk by the padding and moved by the
        /// content offset.
        internal Rect ContentRect(Rect position)
        {
            var r = m_Padding.Remove(position);
            r.position += m_ContentOffset;
            return r;
        }

        // ----- Cursor geometry -----

        /// Where the cursor before string index `cursorStringIndex` is drawn (its top left).
        public Vector2 GetCursorPixelPosition(Rect position, GUIContent content, int cursorStringIndex)
        {
            var carets = CaretsIn(position, content?.text, out _);
            int i = Mathf.Clamp(cursorStringIndex, 0, carets.Count - 1);
            return ContentRect(position).position + carets[i].Position;
        }

        /// The string index whose cursor position is nearest to `cursorPixelPosition`: on the
        /// line under it (the first or last line above or below the text), the nearest caret.
        public int GetCursorStringIndex(Rect position, GUIContent content, Vector2 cursorPixelPosition)
        {
            var carets = CaretsIn(position, content?.text, out _);
            var local = cursorPixelPosition - ContentRect(position).position;
            int lastLine = carets[^1].Line;
            int line = 0;
            for (int i = 0; i < carets.Count; i++)
            {
                if (carets[i].Position.y <= local.y) line = carets[i].Line;
                else break;
            }
            line = Mathf.Clamp(line, 0, lastLine);
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < carets.Count; i++)
            {
                if (carets[i].Line != line) continue;
                float d = Mathf.Abs(carets[i].Position.x - local.x);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            return best;
        }

        /// Caret positions of the content laid out in `position`, relative to `position`'s
        /// content rect, and the line height (text editing).
        internal List<(Vector2 Position, int Line)> CaretsIn(Rect position, string text, out float height)
        {
            var r = ContentRect(position);
            return GUIText.Carets(text ?? "", ResolvedFont, m_FontSize, m_FontStyle, m_Alignment, m_WordWrap, r.size, out height, out _);
        }
    }
}
