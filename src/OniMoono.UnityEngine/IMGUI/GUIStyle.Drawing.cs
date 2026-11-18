using OniMoono.Engine.IMGUI;

namespace UnityEngine
{
    public sealed partial class GUIStyle
    {
        // ----- Draw -----

        public void Draw(Rect position, bool isHover, bool isActive, bool on, bool hasKeyboardFocus)
            => Draw(position, GUIContent.none, isHover, isActive, on, hasKeyboardFocus);

        public void Draw(Rect position, string text, bool isHover, bool isActive, bool on, bool hasKeyboardFocus)
            => Draw(position, GUIContent.Scratch(text), isHover, isActive, on, hasKeyboardFocus);

        public void Draw(Rect position, Texture image, bool isHover, bool isActive, bool on, bool hasKeyboardFocus)
            => Draw(position, GUIContent.Scratch(image), isHover, isActive, on, hasKeyboardFocus);

        /// Draws the background of the state the flags select, then the content.
        public void Draw(Rect position, GUIContent content, bool isHover, bool isActive, bool on, bool hasKeyboardFocus)
        {
            if (!IsRepaint()) return;
            var (background, textColor) = Look(isHover, isActive, on, hasKeyboardFocus);
            DrawBackground(position, background);
            DrawContent(position, content ?? GUIContent.none, textColor);
        }

        public void Draw(Rect position, GUIContent content, int controlID) => Draw(position, content, controlID, false, false);

        public void Draw(Rect position, GUIContent content, int controlID, bool on) => Draw(position, content, controlID, on, false);

        /// Draws for the control `controlID`: active while it holds the mouse, focused while it
        /// has keyboard focus, hovered while the mouse is over it (and no other control holds
        /// the mouse) or when `hover` says so.
        public void Draw(Rect position, GUIContent content, int controlID, bool on, bool hover)
        {
            if (!IsRepaint()) return;
            bool isActive = controlID != 0 && GUIStateData.HotControl == controlID;
            bool isHover = hover || IsHovered(position, controlID);
            bool focus = controlID != 0 && GUIStateData.KeyboardControl == controlID;
            Draw(position, content, isHover, isActive, on, focus);
        }

        internal static bool IsHovered(Rect position, int controlID)
        {
            var e = Event.current;
            if (e == null || !GUIStateData.Enabled) return false;
            int hot = GUIStateData.HotControl;
            return (hot == 0 || hot == controlID) && position.Contains(e.mousePosition);
        }

        static bool IsRepaint()
        {
            if (Event.current is { rawType: EventType.Repaint }) return true;
            Debug.LogError("Style.Draw may only be called during a Repaint event.");
            return false;
        }

        /// The background and text colour of a state: a pressed control is active; otherwise a
        /// focused one focused, a hovered one hover, else normal (the on… states when `on`). A
        /// state without a background or text colour takes normal's (onNormal's).
        (Texture2D Background, Color Text) Look(bool isHover, bool isActive, bool on, bool focus)
        {
            var baseState = on ? m_OnNormal : m_Normal;
            GUIStyleState state;
            if (isActive) state = on ? m_OnActive : m_Active;
            else if (focus) state = on ? m_OnFocused : m_Focused;
            else if (isHover) state = on ? m_OnHover : m_Hover;
            else state = baseState;
            var background = state.m_Background ? state.m_Background : baseState.m_Background;
            var text = state.m_TextColor != default ? state.m_TextColor : baseState.m_TextColor;
            return (background, text);
        }

        /// GUI.enabled false draws controls semi-transparent.
        static Color Dim(Color c)
        {
            if (!GUIStateData.Enabled) c.a *= 0.5f;
            return c;
        }

        static Color Multiply(Color a, Color b) => new(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a);

        /// The background nine-sliced by the border over `position` grown by the overflow,
        /// tinted by GUI.color × GUI.backgroundColor.
        void DrawBackground(Rect position, Texture2D background)
        {
            if (!background) return;
            var tint = Dim(Multiply(GUIStateData.Color, GUIStateData.BackgroundColor));
            GUIRenderer.DrawTexture(m_Overflow.Add(position), background, new Rect(0f, 0f, 1f, 1f), tint, m_Border);
        }

        /// The image and text in the content rect, placed by imagePosition; the text aligned,
        /// wrapped and clipped as the style says, coloured by the state × GUI.color ×
        /// GUI.contentColor.
        void DrawContent(Rect position, GUIContent content, Color stateColor)
        {
            var rect = ContentRect(position);
            bool text = ShowsText(content), image = ShowsImage(content);
            var tint = Dim(Multiply(GUIStateData.Color, GUIStateData.ContentColor));
            var textRect = rect;
            if (image)
            {
                var texture = content.image;
                var size = new Vector2(texture.width, texture.height);
                var room = rect.size;
                if (text && m_ImagePosition == ImagePosition.ImageAbove) room.y = Mathf.Max(0f, rect.height - TextHeight(content, rect.width));
                float scale = Mathf.Min(1f, size.x > 0f ? room.x / size.x : 1f, size.y > 0f ? room.y / size.y : 1f);
                size *= Mathf.Max(0f, scale);
                Rect imageRect;
                if (!text)
                {
                    imageRect = Align(rect, size, m_Alignment);
                }
                else if (m_ImagePosition == ImagePosition.ImageAbove)
                {
                    var top = new Rect(rect.x, rect.y, rect.width, size.y);
                    imageRect = Align(top, size, m_Alignment);
                    textRect = new Rect(rect.x, rect.y + size.y, rect.width, Mathf.Max(0f, rect.height - size.y));
                }
                else
                {
                    var left = new Rect(rect.x, rect.y, size.x, rect.height);
                    imageRect = Align(left, size, m_Alignment);
                    textRect = new Rect(rect.x + size.x, rect.y, Mathf.Max(0f, rect.width - size.x), rect.height);
                }
                GUIRenderer.DrawTexture(imageRect, texture, new Rect(0f, 0f, 1f, 1f), tint, null);
            }
            if (text)
            {
                var color = Dim(Multiply(stateColor, Multiply(GUIStateData.Color, GUIStateData.ContentColor)));
                GUIRenderer.DrawText(textRect, content.text, ResolvedFont, m_FontSize, m_FontStyle, m_Alignment, m_WordWrap, m_RichText, color,
                    m_TextClipping == TextClipping.Clip ? rect : null);
            }
        }

        float TextHeight(GUIContent content, float width)
            => GUIText.Size(content.text, ResolvedFont, m_FontSize, m_FontStyle, m_RichText, m_WordWrap ? width : 0f).y;

        static Rect Align(Rect area, Vector2 size, TextAnchor anchor)
        {
            int a = (int)anchor;
            float fx = (a % 3) * 0.5f, fy = (a / 3) * 0.5f;
            return new Rect(area.x + (area.width - size.x) * fx, area.y + (area.height - size.y) * fy, size.x, size.y);
        }

        // ----- Text editing -----

        /// Draws a text field's text with the cursor before `position` index `character`.
        public void DrawCursor(Rect position, GUIContent content, int controlID, int character)
            => DrawEditing(position, content, controlID, character, character, Vector2.zero, true);

        /// Draws a text field's text with the characters between the two indices selected.
        public void DrawWithTextSelection(Rect position, GUIContent content, int controlID, int firstSelectedCharacter, int lastSelectedCharacter)
            => DrawEditing(position, content, controlID, lastSelectedCharacter, firstSelectedCharacter, Vector2.zero, firstSelectedCharacter == lastSelectedCharacter);

        /// A text being edited: the background of its state, the selection in the skin's
        /// selection colour, the text (rich text is not interpreted), the cursor in the skin's
        /// cursor colour, all moved by `scroll` and clipped to the content rect.
        internal void DrawEditing(Rect position, GUIContent content, int controlID, int cursor, int select, Vector2 scroll, bool drawCursor)
        {
            if (!IsRepaint()) return;
            content ??= GUIContent.none;
            bool focus = controlID != 0 && GUIStateData.KeyboardControl == controlID;
            bool isActive = controlID != 0 && GUIStateData.HotControl == controlID;
            var (background, textColor) = Look(IsHovered(position, controlID), isActive, false, focus);
            DrawBackground(position, background);

            var clip = ContentRect(position);
            var origin = clip.position - scroll;
            var text = content.text ?? "";
            var carets = CaretsIn(position, text, out float lineHeight);
            var settings = GUIStateData.CurrentSkin.settings;
            if (cursor != select)
            {
                int from = Mathf.Clamp(Mathf.Min(cursor, select), 0, carets.Count - 1);
                int to = Mathf.Clamp(Mathf.Max(cursor, select), 0, carets.Count - 1);
                var selection = Dim(settings.selectionColor);
                int lineStart = from;
                for (int i = from + 1; i <= to + 1; i++)
                {
                    if (i <= to && carets[i].Line == carets[lineStart].Line) continue;
                    int lineEnd = i - 1;
                    var a = carets[lineStart].Position;
                    var b = carets[lineEnd].Position;
                    FillClipped(Rect.MinMaxRect(origin.x + a.x, origin.y + a.y, origin.x + b.x, origin.y + a.y + lineHeight), clip, selection);
                    lineStart = i;
                }
            }
            var color = Dim(Multiply(textColor, Multiply(GUIStateData.Color, GUIStateData.ContentColor)));
            GUIRenderer.DrawText(new Rect(origin, clip.size), text, ResolvedFont, m_FontSize, m_FontStyle, m_Alignment, m_WordWrap, false, color, clip);
            if (drawCursor && focus && CursorVisible(settings))
            {
                var p = carets[Mathf.Clamp(cursor, 0, carets.Count - 1)].Position;
                FillClipped(new Rect(origin.x + p.x, origin.y + p.y, 1f, lineHeight), clip, Dim(settings.cursorColor));
            }
        }

        /// The cursor blinks at the skin's flash speed (flashes per second; 0: steady).
        static bool CursorVisible(GUISettings settings)
        {
            float speed = settings.cursorFlashSpeed;
            return speed <= 0f || Time.realtimeSinceStartup * speed % 1f < 0.5f;
        }

        static void FillClipped(Rect r, Rect clip, Color color)
        {
            var c = Rect.MinMaxRect(Mathf.Max(r.xMin, clip.xMin), Mathf.Max(r.yMin, clip.yMin), Mathf.Min(r.xMax, clip.xMax), Mathf.Min(r.yMax, clip.yMax));
            if (c.width <= 0f || c.height <= 0f) return;
            GUIRenderer.DrawTexture(c, Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f), color, null);
        }
    }
}
