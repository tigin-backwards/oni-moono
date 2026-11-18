using UnityEngine;

namespace OniMoono.Engine.IMGUI
{
    /// The control-ID hints of the built-in controls: arbitrary, distinct per kind, so that a
    /// control skipped in one pass does not shift the IDs of the controls of other kinds.
    internal static class ControlHints
    {
        public const int Button = 0x4C1A_0001;
        public const int RepeatButton = 0x4C1A_0002;
        public const int Toggle = 0x4C1A_0003;
        public const int TextField = 0x4C1A_0004;
        public const int Slider = 0x4C1A_0005;
        public const int Scrollbar = 0x4C1A_0006;
        public const int ScrollbarStep = 0x4C1A_0007;
        public const int ScrollView = 0x4C1A_0008;
        public const int DragWindow = 0x4C1A_0009;
        public const int Window = 0x4C1A_000A;
        public const int Group = 0x4C1A_000B;
        public const int Toolbar = 0x4C1A_000C;
    }

    /// The input handling and drawing of the built-in controls. Every control draws only on
    /// Repaint and reacts only to events over its rect (or while it holds the mouse).
    internal static class Controls
    {
        static Event Current => Event.current;

        static bool Over(Rect r) => r.Contains(Current.mousePosition);

        /// GUI.Label (`label`) and GUI.Box: draw on Repaint, where a hovered one sets the tooltip.
        public static void Label(Rect position, GUIContent content, GUIStyle style, bool label)
        {
            GUIStateData.RequireGUI();
            if (Current.type != EventType.Repaint) return;
            (style ?? GUIStyle.none).Draw(position, content, false, false, false, false);
            Tooltip(position, content, 0, label);
        }

        /// On Repaint, a control whose content has a tooltip makes it GUI.tooltip when the mouse
        /// is inside its rect and inside the visible area of the clip it is drawn in, or when it
        /// holds the mouse (`id` is hotControl). A label also moves GUI.tooltipRect to the mouse.
        /// Windows do not hide the controls under them, so the last one drawn wins.
        static void Tooltip(Rect position, GUIContent content, int id, bool label)
        {
            string tip = content?.tooltip;
            if (string.IsNullOrEmpty(tip)) return;
            var mouse = Current.mousePosition;
            bool hovered = position.Contains(mouse) && ClipStack.AbsoluteVisible.Contains(ClipStack.Unclip(mouse));
            if (!hovered && (id == 0 || GUIStateData.HotControl != id)) return;
            GUIStateData.Tooltip = tip;
            if (label && hovered) GUIStateData.TooltipRect = new Rect(mouse, Vector2.zero);
        }

        /// True on the release of a press that started inside, when the release is inside too;
        /// also on Space or Return while the button has keyboard focus.
        public static bool Button(Rect position, GUIContent content, GUIStyle style)
        {
            GUIStateData.RequireGUI();
            int id = GUIUtility.GetControlID(ControlHints.Button, FocusType.Passive, position);
            return Pressable(position, id, content, style ?? GUIStyle.none, false, true);
        }

        /// A button's input and drawing; with `tooltip` it sets GUI.tooltip as it draws.
        static bool Pressable(Rect position, int id, GUIContent content, GUIStyle style, bool on, bool tooltip = false)
        {
            var e = Current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (Over(position))
                    {
                        GUIStateData.HotControl = id;
                        e.Use();
                    }
                    return false;
                case EventType.MouseDrag:
                    if (GUIStateData.HotControl == id) e.Use();
                    return false;
                case EventType.MouseUp:
                    if (GUIStateData.HotControl != id) return false;
                    GUIStateData.HotControl = 0;
                    e.Use();
                    if (!Over(position)) return false;
                    GUIStateData.Changed = true;
                    return true;
                case EventType.KeyDown:
                    if (GUIStateData.KeyboardControl == id && e.keyCode is KeyCode.Space or KeyCode.Return or KeyCode.KeypadEnter)
                    {
                        e.Use();
                        GUIStateData.Changed = true;
                        return true;
                    }
                    return false;
                case EventType.Repaint:
                    style.Draw(position, content, id, on);
                    if (tooltip) Tooltip(position, content, id, false);
                    return false;
            }
            return false;
        }

        /// True on every Repaint while held with the mouse over it, and on the release inside.
        public static bool RepeatButton(Rect position, GUIContent content, GUIStyle style)
        {
            GUIStateData.RequireGUI();
            style ??= GUIStyle.none;
            int id = GUIUtility.GetControlID(ControlHints.RepeatButton, FocusType.Passive, position);
            var e = Current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (Over(position))
                    {
                        GUIStateData.HotControl = id;
                        e.Use();
                    }
                    return false;
                case EventType.MouseUp:
                    if (GUIStateData.HotControl != id) return false;
                    GUIStateData.HotControl = 0;
                    e.Use();
                    return Over(position);
                case EventType.Repaint:
                    style.Draw(position, content, id);
                    return GUIStateData.HotControl == id && Over(position);
            }
            return false;
        }

        /// A button that returns the flipped value when clicked.
        public static bool Toggle(Rect position, bool value, GUIContent content, GUIStyle style)
        {
            GUIStateData.RequireGUI();
            int id = GUIUtility.GetControlID(ControlHints.Toggle, FocusType.Passive, position);
            return Pressable(position, id, content, style ?? GUIStyle.none, value, true) ? !value : value;
        }

        /// A row of buttons splitting `position`'s width equally, the one at `selected` drawn
        /// "on". Returns the index of a clicked button, otherwise `selected`; GUI.changed is
        /// set only when the click picks another button.
        public static int Toolbar(Rect position, int selected, GUIContent[] contents, GUIStyle style)
        {
            GUIStateData.RequireGUI();
            style ??= GUIStyle.none;
            int count = contents?.Length ?? 0;
            if (count == 0) return selected;
            float width = position.width / count;
            int result = selected;
            bool changedBefore = GUIStateData.Changed;
            for (int i = 0; i < count; i++)
            {
                var r = new Rect(position.x + i * width, position.y, width, position.height);
                int id = GUIUtility.GetControlID(ControlHints.Toolbar, FocusType.Passive, r);
                if (Pressable(r, id, contents[i] ?? GUIContent.none, style, i == selected)) result = i;
            }
            if (result == selected) GUIStateData.Changed = changedBefore;
            return result;
        }

        /// The contents of a string or texture toolbar.
        public static GUIContent[] Contents(string[] texts)
        {
            if (texts == null) return null;
            var contents = new GUIContent[texts.Length];
            for (int i = 0; i < texts.Length; i++) contents[i] = new GUIContent(texts[i]);
            return contents;
        }

        public static GUIContent[] Contents(Texture[] images)
        {
            if (images == null) return null;
            var contents = new GUIContent[images.Length];
            for (int i = 0; i < images.Length; i++) contents[i] = new GUIContent(images[i]);
            return contents;
        }

        // ----- Sliders and scrollbars -----

        /// The thumb's extent along the slider: its style's fixed size, else its padding.
        static float ThumbLength(GUIStyle thumb, bool horizontal)
        {
            float fixedSize = horizontal ? thumb.fixedWidth : thumb.fixedHeight;
            return fixedSize != 0f ? fixedSize : horizontal ? thumb.padding.horizontal : thumb.padding.vertical;
        }

        /// A value dragged with a thumb between `start` and `end` (either order). Clicking the
        /// track jumps the thumb's centre to the mouse. `size` is the part of the range the
        /// thumb covers (0 for a slider).
        public static float Slider(Rect position, float value, float size, float start, float end, GUIStyle slider, GUIStyle thumb, bool horizontal, int id)
        {
            slider ??= GUIStyle.none;
            thumb ??= GUIStyle.none;
            var e = Current;
            var track = slider.padding.Remove(position);
            float min = Mathf.Min(start, end), max = Mathf.Max(start, end);
            float span = max - min;
            size = Mathf.Clamp(size, 0f, span);
            // The value runs from `start` to `last`: the thumb's start covers the range less its size.
            float last = start <= end ? end - size : end + size;
            float trackLength = horizontal ? track.width : track.height;
            float thumbLength = ThumbLength(thumb, horizontal);
            if (size > 0f && span > 0f) thumbLength = Mathf.Max(thumbLength, trackLength * size / span);
            thumbLength = Mathf.Min(thumbLength, trackLength);
            float travel = Mathf.Max(0f, trackLength - thumbLength);
            float Fraction(float v) => last != start ? Mathf.Clamp01((v - start) / (last - start)) : 0f;
            Rect ThumbRect(float v)
            {
                float offset = travel * Fraction(v);
                return horizontal
                    ? new Rect(track.x + offset, track.y, thumbLength, track.height)
                    : new Rect(track.x, track.y + offset, track.width, thumbLength);
            }
            float ValueAt(float thumbStart)
            {
                float f = travel > 0f ? Mathf.Clamp01((thumbStart - (horizontal ? track.x : track.y)) / travel) : 0f;
                return start + (last - start) * f;
            }
            float Along(Vector2 p) => horizontal ? p.x : p.y;
            var state = (SliderGrip)GUIStateData.GetStateObject(typeof(SliderGrip), id);
            float result = value;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (!position.Contains(e.mousePosition)) break;
                    GUIStateData.HotControl = id;
                    var current = ThumbRect(value);
                    if (current.Contains(e.mousePosition))
                    {
                        state.Grip = Along(e.mousePosition) - (horizontal ? current.x : current.y);
                    }
                    else
                    {
                        state.Grip = thumbLength * 0.5f;
                        result = ValueAt(Along(e.mousePosition) - state.Grip);
                    }
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIStateData.HotControl != id) break;
                    result = ValueAt(Along(e.mousePosition) - state.Grip);
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIStateData.HotControl != id) break;
                    GUIStateData.HotControl = 0;
                    e.Use();
                    break;
                case EventType.Repaint:
                    slider.Draw(position, GUIContent.none, id);
                    thumb.Draw(ThumbRect(value), GUIContent.none, id);
                    break;
            }
            result = Mathf.Clamp(result, Mathf.Min(start, last), Mathf.Max(start, last));
            if (result != value) GUIStateData.Changed = true;
            return result;
        }

        /// A slider whose thumb covers `size` of the range, with step buttons at the ends.
        public static float Scrollbar(Rect position, float value, float size, float start, float end, GUIStyle style, bool horizontal)
        {
            GUIStateData.RequireGUI();
            style ??= GUIStyle.none;
            var skin = GUIStateData.CurrentSkin;
            var thumb = Partner(style, "thumb", horizontal ? skin.horizontalScrollbarThumb : skin.verticalScrollbarThumb);
            var less = Partner(style, horizontal ? "leftbutton" : "upbutton", horizontal ? skin.horizontalScrollbarLeftButton : skin.verticalScrollbarUpButton);
            var more = Partner(style, horizontal ? "rightbutton" : "downbutton", horizontal ? skin.horizontalScrollbarRightButton : skin.verticalScrollbarDownButton);
            float lessLength = ThumbLength(less, horizontal), moreLength = ThumbLength(more, horizontal);
            Rect lessRect, moreRect, track;
            if (horizontal)
            {
                lessRect = new Rect(position.x, position.y, lessLength, position.height);
                moreRect = new Rect(position.xMax - moreLength, position.y, moreLength, position.height);
                track = new Rect(position.x + lessLength, position.y, Mathf.Max(0f, position.width - lessLength - moreLength), position.height);
            }
            else
            {
                lessRect = new Rect(position.x, position.y, position.width, lessLength);
                moreRect = new Rect(position.x, position.yMax - moreLength, position.width, moreLength);
                track = new Rect(position.x, position.y + lessLength, position.width, Mathf.Max(0f, position.height - lessLength - moreLength));
            }
            int id = GUIUtility.GetControlID(ControlHints.Scrollbar, FocusType.Passive, position);
            float result = Slider(track, value, size, start, end, style, thumb, horizontal, id);
            // A step is a tenth of the visible part (of the range when nothing is visible).
            float step = (size > 0f ? size : Mathf.Abs(end - start)) * 0.1f * Mathf.Sign(end - start == 0f ? 1f : end - start);
            if (StepButton(lessRect, less)) result -= step;
            if (StepButton(moreRect, more)) result += step;
            float low = Mathf.Min(start, end), high = Mathf.Max(start, end);
            size = Mathf.Clamp(size, 0f, high - low);
            result = start <= end ? Mathf.Clamp(result, start, end - size) : Mathf.Clamp(result, end + size, start);
            if (result != value) GUIStateData.Changed = true;
            return result;
        }

        /// A scrollbar style's part: the skin's style named after it ("horizontalscrollbar" +
        /// "thumb"), else the skin's standard part.
        static GUIStyle Partner(GUIStyle style, string part, GUIStyle fallback)
        {
            var named = GUIStateData.CurrentSkin.FindStyle(style.name + part);
            return named ?? fallback ?? GUIStyle.none;
        }

        /// True on the press of a step button.
        static bool StepButton(Rect position, GUIStyle style)
        {
            int id = GUIUtility.GetControlID(ControlHints.ScrollbarStep, FocusType.Passive, position);
            var e = Current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (!position.Contains(e.mousePosition)) return false;
                    GUIStateData.HotControl = id;
                    e.Use();
                    return true;
                case EventType.MouseUp:
                    if (GUIStateData.HotControl != id) return false;
                    GUIStateData.HotControl = 0;
                    e.Use();
                    return false;
                case EventType.Repaint:
                    style.Draw(position, GUIContent.none, id);
                    return false;
            }
            return false;
        }

        // ----- Textures -----

        /// GUI.DrawTexture: places the texture by `scaleMode` (the image aspect is the
        /// texture's when `imageAspect` is 0), tinted by GUI.color, on Repaint.
        public static void DrawTexture(Rect position, Texture image, ScaleMode scaleMode, bool alphaBlend, float imageAspect, Color tint)
        {
            GUIStateData.RequireGUI();
            if (!image || Current.type != EventType.Repaint) return;
            if (imageAspect == 0f) imageAspect = image.height > 0 ? (float)image.width / image.height : 1f;
            var uv = new Rect(0f, 0f, 1f, 1f);
            var rect = position;
            float rectAspect = position.height != 0f ? position.width / position.height : 1f;
            switch (scaleMode)
            {
                case ScaleMode.ScaleToFit:
                    if (imageAspect > rectAspect)
                    {
                        float h = position.width / imageAspect;
                        rect = new Rect(position.x, position.y + (position.height - h) * 0.5f, position.width, h);
                    }
                    else
                    {
                        float w = position.height * imageAspect;
                        rect = new Rect(position.x + (position.width - w) * 0.5f, position.y, w, position.height);
                    }
                    break;
                case ScaleMode.ScaleAndCrop:
                    if (imageAspect > rectAspect)
                    {
                        float visible = rectAspect / imageAspect;
                        uv = new Rect((1f - visible) * 0.5f, 0f, visible, 1f);
                    }
                    else
                    {
                        float visible = imageAspect / rectAspect;
                        uv = new Rect(0f, (1f - visible) * 0.5f, 1f, visible);
                    }
                    break;
            }
            DrawTextureWithTexCoords(rect, image, uv, alphaBlend, tint);
        }

        public static void DrawTextureWithTexCoords(Rect position, Texture image, Rect texCoords, bool alphaBlend, Color tint)
        {
            GUIStateData.RequireGUI();
            if (!image || Current.type != EventType.Repaint) return;
            if (!GUIStateData.Enabled) tint.a *= 0.5f;
            GUIRenderer.DrawTexture(position, image, texCoords, tint, null, alphaBlend ? null : GUIRenderer.BlitMaterial);
        }
    }

    /// A slider's grip: where on the thumb the mouse took it.
    internal sealed class SliderGrip
    {
        public float Grip;
    }
}
