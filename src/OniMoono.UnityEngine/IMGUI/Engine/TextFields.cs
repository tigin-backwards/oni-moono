using UnityEngine;

namespace OniMoono.Engine.IMGUI
{
    /// GUI.TextField and GUI.TextArea: a string edited through the control's TextEditor state
    /// object while the control has keyboard focus.
    internal static class TextFields
    {
        public static string Edit(Rect position, string text, int maxLength, GUIStyle style, bool multiline)
        {
            GUIStateData.RequireGUI();
            style ??= GUIStyle.none;
            text ??= "";
            int id = GUIUtility.GetControlID(ControlHints.TextField, FocusType.Keyboard, position);
            var editor = (TextEditor)GUIStateData.GetStateObject(typeof(TextEditor), id);
            editor.text = text;
            editor.position = position;
            editor.style = style;
            editor.controlID = id;
            editor.multiline = multiline;
            bool focused = GUIStateData.KeyboardControl == id;
            var e = Event.current;
            var settings = GUIStateData.CurrentSkin.settings;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (!position.Contains(e.mousePosition)) break;
                    GUIStateData.HotControl = id;
                    if (!focused)
                    {
                        // Focusing by a click selects the whole text.
                        GUIStateData.KeyboardControl = id;
                        editor.OnFocus();
                    }
                    else if (e.clickCount == 2 && settings.doubleClickSelectsWord)
                    {
                        editor.MoveCursorToPosition(e.mousePosition);
                        editor.SelectCurrentWord();
                    }
                    else if (e.clickCount == 3 && settings.tripleClickSelectsLine)
                    {
                        editor.MoveCursorToPosition(e.mousePosition);
                        editor.SelectCurrentParagraph();
                    }
                    else if (e.shift)
                    {
                        editor.SelectToPosition(e.mousePosition);
                    }
                    else
                    {
                        editor.MoveCursorToPosition(e.mousePosition);
                    }
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIStateData.HotControl != id) break;
                    editor.SelectToPosition(e.mousePosition);
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIStateData.HotControl != id) break;
                    GUIStateData.HotControl = 0;
                    e.Use();
                    break;
                case EventType.KeyDown:
                    if (!focused) break;
                    if (editor.HandleKeyEvent(e))
                    {
                        e.Use();
                        break;
                    }
                    if (e.keyCode != KeyCode.None) break;
                    char c = e.character;
                    if (c == '\r') c = '\n';
                    // Tab moves focus; a new line only goes into multi-line fields.
                    if (c == '\t' || c == '\0' || (c == '\n' && !multiline)) break;
                    if (c != '\n' && char.IsControl(c)) break;
                    var font = style.ResolvedFont;
                    if (c != '\n' && font && !font.HasCharacter(c)) break;
                    editor.SaveBackup();
                    editor.Insert(c);
                    e.Use();
                    break;
                case EventType.Repaint:
                    if (focused) editor.DrawCursor(editor.text);
                    else style.Draw(position, GUIContent.Scratch(editor.text), id);
                    break;
            }
            var result = editor.text;
            if (maxLength >= 0 && result.Length > maxLength)
            {
                result = result.Substring(0, maxLength);
                editor.text = result;
            }
            if (result != text) GUIStateData.Changed = true;
            return result;
        }
    }
}
