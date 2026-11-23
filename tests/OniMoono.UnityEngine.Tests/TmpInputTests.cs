using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Xunit;

namespace OniMoono.Tests
{
    // The input field and dropdown, driven through the player loop and the event queue.
    public class TmpInputTests : EngineTest
    {
        // An input module that only sends the selected object its update event each frame (as
        // the game's input module does).
        internal sealed class SelectedUpdateModule : BaseInputModule
        {
            public override void Process()
            {
                var selected = eventSystem.currentSelectedGameObject;
                if (selected != null) ExecuteEvents.Execute(selected, GetBaseEventData(), ExecuteEvents.updateSelectedHandler);
            }
        }

        sealed class UpperCaseOnly : TMP_InputValidator
        {
            // Inserts upper-case letters only (as the game's validators insert what they accept).
            public override char Validate(ref string text, ref int pos, char ch)
            {
                if (ch < 'A' || ch > 'Z') return (char)0;
                text = text.Insert(pos, ch.ToString());
                pos++;
                return ch;
            }
        }

        // A field built inactive and then enabled; with assignLater, its text component and
        // placeholder are assigned only after it is enabled.
        internal static TMP_InputField NewField(float width = 200f, bool assignLater = false)
        {
            TmpTestSupport.LoadGameData();
            if (EventSystem.current == null) new GameObject("EventSystem", typeof(EventSystem), typeof(SelectedUpdateModule));
            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var fieldGo = new GameObject("Field", typeof(RectTransform));
            fieldGo.transform.SetParent(canvasGo.transform, false);
            ((RectTransform)fieldGo.transform).sizeDelta = new Vector2(width, 30f);
            fieldGo.SetActive(false);
            var field = fieldGo.AddComponent<TMP_InputField>();
            var area = new GameObject("Text Area", typeof(RectTransform));
            area.transform.SetParent(fieldGo.transform, false);
            var areaRect = (RectTransform)area.transform;
            areaRect.anchorMin = Vector2.zero;
            areaRect.anchorMax = Vector2.one;
            areaRect.sizeDelta = Vector2.zero;
            area.AddComponent<RectMask2D>();
            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(area.transform, false);
            var textRect = (RectTransform)textGo.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.font = TmpTestSupport.Graystroke;
            text.fontSize = 14f;
            var placeholderGo = new GameObject("Placeholder", typeof(RectTransform));
            placeholderGo.transform.SetParent(area.transform, false);
            var placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.font = TmpTestSupport.Graystroke;
            field.textViewport = areaRect;
            if (assignLater) fieldGo.SetActive(true);
            field.textComponent = text;
            field.placeholder = placeholder;
            fieldGo.SetActive(true);
            return field;
        }

        static void Type(params Event[] events)
        {
            foreach (var e in events) Event.Enqueue(e);
            Frame();
        }

        static Event Key(KeyCode key, char character = '\0', EventModifiers modifiers = 0) =>
            new() { type = EventType.KeyDown, keyCode = key, character = character, modifiers = modifiers };

        static Event Char(char c) => Key(KeyCode.None, c);

        [Fact]
        public void FocusTurnsTheImeOnAndDeactivationSetsItToAuto()
        {
            var saved = Input.imeCompositionMode;
            try
            {
                var field = NewField();
                Input.imeCompositionMode = IMECompositionMode.Off;
                field.ActivateInputField();
                Assert.Equal(IMECompositionMode.Off, Input.imeCompositionMode);
                Frame();
                Assert.True(field.isFocused);
                Assert.Equal(IMECompositionMode.On, Input.imeCompositionMode);
                // While focused the field leaves a mode set by others alone.
                Input.imeCompositionMode = IMECompositionMode.Off;
                Frame();
                Assert.Equal(IMECompositionMode.Off, Input.imeCompositionMode);
                field.DeactivateInputField();
                Assert.Equal(IMECompositionMode.Auto, Input.imeCompositionMode);
                // Deactivating an unfocused field changes nothing.
                Input.imeCompositionMode = IMECompositionMode.Off;
                field.DeactivateInputField();
                Assert.Equal(IMECompositionMode.Off, Input.imeCompositionMode);
                // Activating again turns it on again.
                field.ActivateInputField();
                Frame();
                Assert.Equal(IMECompositionMode.On, Input.imeCompositionMode);
                field.DeactivateInputField();
                Assert.Equal(IMECompositionMode.Auto, Input.imeCompositionMode);
            }
            finally { Input.imeCompositionMode = saved; }
        }

        [Fact]
        public void TheImeCompositionShowsAtTheCaretUntilItIsTyped()
        {
            var savedCursor = Input.compositionCursorPos;
            // The caret, which places the IME, is drawn in the canvas update (as in UGuiTest).
            OniMoono.Engine.UI.CanvasSystem.Install();
            try
            {
                var field = NewField();
                field.text = "ab";
                field.onFocusSelectAll = true;
                field.ActivateInputField();
                Frame();
                Assert.True(field.isFocused);
                // Focus selected all: the composition replaces the selection, without an event.
                int changes = 0;
                field.onValueChanged.AddListener(_ => changes++);
                OniMoono.Engine.Platform.ImeInput.Composition = "ni";
                Frame();
                Assert.Equal("", field.text);
                Assert.Equal("<u>ni</u>​", field.textComponent.text);
                Assert.Equal(0, changes);
                // The IME's windows go to the caret, after the composition.
                Input.compositionCursorPos = new Vector2(-1f, -1f);
                OniMoono.Engine.Platform.ImeInput.Composition = "ni hao";
                Frame();
                Assert.Equal("<u>ni hao</u>​", field.textComponent.text);
                // Composed: the composition goes, and the result is typed.
                OniMoono.Engine.Platform.ImeInput.Composition = "";
                Type(Char('你'), Char('好'));
                Assert.Equal("你好", field.text);
                Assert.Equal("你好​", field.textComponent.text);
                Assert.NotEqual(new Vector2(-1f, -1f), Input.compositionCursorPos);
                // A field that is not focused shows no composition.
                field.DeactivateInputField();
                OniMoono.Engine.Platform.ImeInput.Composition = "x";
                Frame();
                Assert.Equal("你好​", field.textComponent.text);
            }
            finally
            {
                OniMoono.Engine.Platform.ImeInput.Composition = "";
                Input.compositionCursorPos = savedCursor;
            }
        }

        [Fact]
        public void ActivationStartsEditingInTheNextLateUpdate()
        {
            var field = NewField();
            bool focused = false;
            field.onFocus += () => focused = true;
            field.ActivateInputField();
            Assert.False(field.isFocused);
            Frame();
            Assert.True(field.isFocused);
            Assert.True(focused);
            Assert.Same(field.gameObject, EventSystem.current.currentSelectedGameObject);
            // The shown text carries the end marker for the caret.
            Assert.Equal("​", field.textComponent.text);
            // The caret is drawn by a child of the viewport, its first.
            Assert.Equal("Caret", field.textViewport.GetChild(0).name);
        }

        [Fact]
        public void TypingEditsTheTextAndRaisesTheEvents()
        {
            var field = NewField();
            var changes = new List<string>();
            field.onValueChanged.AddListener(changes.Add);
            field.ActivateInputField();
            Frame();
            Type(Char('a'), Char('b'), Char('c'));
            Assert.Equal("abc", field.text);
            Assert.Equal(new[] { "a", "ab", "abc" }, changes);
            Assert.Equal(3, field.caretPosition);
            Type(Key(KeyCode.LeftArrow), Key(KeyCode.Backspace));
            Assert.Equal("ac", field.text);
            Assert.Equal(1, field.caretPosition);
            Type(Key(KeyCode.Home), Key(KeyCode.Delete));
            Assert.Equal("c", field.text);
            Type(Key(KeyCode.End), Char('d'));
            Assert.Equal("cd", field.text);
            Assert.Equal("cd​", field.textComponent.text);
            // Select all and type over it.
            Type(Key(KeyCode.A, 'a', EventModifiers.Control), Char('z'));
            Assert.Equal("z", field.text);
        }

        [Fact]
        public void EnterSubmitsAndEndsEditingAndEscapeRestores()
        {
            var field = NewField();
            string submitted = null, ended = null;
            field.onSubmit.AddListener(s => submitted = s);
            field.onEndEdit.AddListener(s => ended = s);
            field.ActivateInputField();
            Frame();
            Type(Char('h'), Char('i'), Key(KeyCode.Return, '\n'));
            Assert.Equal("hi", submitted);
            Assert.Equal("hi", ended);
            Assert.False(field.isFocused);

            field.ActivateInputField();
            Frame();
            Type(Char('!'), Key(KeyCode.Escape));
            Assert.False(field.isFocused);
            Assert.Equal("hi", field.text);
        }

        [Fact]
        public void CharactersAreValidated()
        {
            var field = NewField();
            field.characterValidation = TMP_InputField.CharacterValidation.Integer;
            field.characterLimit = 4;
            field.ActivateInputField();
            Frame();
            Type(Char('x'), Char('-'), Char('1'), Char('-'), Char('2'), Char('3'), Char('4'), Char('5'));
            Assert.Equal("-123", field.text);

            var custom = NewField();
            custom.inputValidator = ScriptableObject.CreateInstance<UpperCaseOnly>();
            custom.characterValidation = TMP_InputField.CharacterValidation.CustomValidator;
            custom.ActivateInputField();
            Frame();
            Type(Char('a'), Char('B'), Char('1'), Char('C'));
            Assert.Equal("BC", custom.text);
        }

        // onValidateInput comes first whatever the character validation: with CustomValidator
        // and no inputValidator, the callback's result is inserted (issue #121: the Storage
        // Quotas mod's fields), and a callback overrides an inputValidator too.
        [Fact]
        public void OnValidateInputComesBeforeACustomValidator()
        {
            var field = NewField();
            field.contentType = TMP_InputField.ContentType.Custom;
            field.characterValidation = TMP_InputField.CharacterValidation.CustomValidator;
            field.onValidateInput = (t, i, c) => c >= '0' && c <= '9' ? c : (char)0;
            int changes = 0;
            field.onValueChanged.AddListener(_ => changes++);
            field.ActivateInputField();
            Frame();
            Type(Char('5'), Char('x'), Char('0'));
            Assert.Equal("50", field.text);
            Assert.Equal(2, field.caretPosition);
            Assert.Equal(2, changes);
            // A paste goes through the callback character by character.
            GUIUtility.systemCopyBuffer = "1a2";
            Type(Key(KeyCode.V, 'v', EventModifiers.Control));
            Assert.Equal("5012", field.text);

            var both = NewField();
            both.inputValidator = ScriptableObject.CreateInstance<UpperCaseOnly>();
            both.characterValidation = TMP_InputField.CharacterValidation.CustomValidator;
            both.onValidateInput = (t, i, c) => char.ToLowerInvariant(c);
            both.ActivateInputField();
            Frame();
            Type(Char('A'), Char('b'), Char('1'));
            Assert.Equal("ab1", both.text);

            // Without a callback the inputValidator still validates.
            both.onValidateInput = null;
            Type(Char('c'), Char('D'));
            Assert.Equal("ab1D", both.text);

            // None and the content types' validations are unchanged.
            var none = NewField();
            none.characterValidation = TMP_InputField.CharacterValidation.None;
            none.ActivateInputField();
            Frame();
            Type(Char('a'), Char('-'), Char('1'));
            Assert.Equal("a-1", none.text);
            var digits = NewField();
            digits.characterValidation = TMP_InputField.CharacterValidation.Digit;
            digits.ActivateInputField();
            Frame();
            Type(Char('a'), Char('7'), Char('-'));
            Assert.Equal("7", digits.text);
        }

        [Fact]
        public void ARejectedCharacterLeavesTheSelection()
        {
            var field = NewField();
            field.characterValidation = TMP_InputField.CharacterValidation.Integer;
            field.text = "123";
            int changes = 0;
            field.onValueChanged.AddListener(_ => changes++);
            field.ActivateInputField();
            Frame();
            // Select all: the anchor at the end, the caret at the start.
            Type(Key(KeyCode.A, 'a', EventModifiers.Control));
            Assert.Equal(3, field.selectionStringAnchorPosition);
            Assert.Equal(0, field.stringPosition);
            // The character positions follow a frame later.
            Frame();
            Assert.Equal(3, field.selectionAnchorPosition);
            Assert.Equal(0, field.caretPosition);
            Type(Char('x'));
            Assert.Equal("123", field.text);
            Assert.Equal(3, field.selectionAnchorPosition);
            Assert.Equal(0, field.selectionFocusPosition);
            Assert.Equal(0, changes);
            // A part of the text, selected backwards.
            Type(Key(KeyCode.End), Key(KeyCode.LeftArrow, '\0', EventModifiers.Shift), Key(KeyCode.LeftArrow, '\0', EventModifiers.Shift), Char('x'));
            Assert.Equal("123", field.text);
            Assert.Equal(3, field.selectionAnchorPosition);
            Assert.Equal(1, field.selectionFocusPosition);
            Assert.Equal(0, changes);
            // Validation sees the text with the selection removed: "-" typed over a selected "-"
            // is at the start of "12".
            field.text = "-12";
            Type(Key(KeyCode.Home), Key(KeyCode.RightArrow, '\0', EventModifiers.Shift), Char('-'));
            Assert.Equal("-12", field.text);
            Assert.Equal(1, field.caretPosition);

            // onValidateInput is given that text and the selection's start.
            var callback = NewField();
            callback.text = "abc";
            string seen = null;
            int seenIndex = -1;
            callback.onValidateInput = (t, i, c) => { seen = t; seenIndex = i; return (char)0; };
            callback.ActivateInputField();
            Frame();
            Type(Key(KeyCode.End), Key(KeyCode.LeftArrow, '\0', EventModifiers.Shift), Key(KeyCode.LeftArrow, '\0', EventModifiers.Shift), Char('x'));
            Assert.Equal("a", seen);
            Assert.Equal(1, seenIndex);
            Assert.Equal("abc", callback.text);
            Assert.Equal(3, callback.selectionAnchorPosition);
            Assert.Equal(1, callback.selectionFocusPosition);

            // A custom validator is handed the text after the selection is removed, which stays
            // removed when it rejects the character.
            var custom = NewField();
            custom.inputValidator = ScriptableObject.CreateInstance<UpperCaseOnly>();
            custom.characterValidation = TMP_InputField.CharacterValidation.CustomValidator;
            custom.text = "ABC";
            changes = 0;
            custom.onValueChanged.AddListener(_ => changes++);
            custom.ActivateInputField();
            Frame();
            Type(Key(KeyCode.End), Key(KeyCode.LeftArrow, '\0', EventModifiers.Shift), Key(KeyCode.LeftArrow, '\0', EventModifiers.Shift), Char('x'));
            Assert.Equal("A", custom.text);
            Assert.Equal(1, custom.caretPosition);
            Assert.Equal(0, changes);
        }

        static TMP_InputField Typed(string s, bool multiLine = false)
        {
            var field = NewField();
            if (multiLine)
            {
                ((RectTransform)field.transform).sizeDelta = new Vector2(200f, 100f);
                field.lineType = TMP_InputField.LineType.MultiLineNewline;
            }
            field.ActivateInputField();
            Frame();
            foreach (char c in s) Type(c == '\n' ? Key(KeyCode.Return, '\n') : Char(c));
            return field;
        }

        // The caret graphic's quads (left, bottom, right, top), in its space (the text's here).
        static Rect[] CaretQuads(TMP_InputField field)
        {
            Canvas.ForceUpdateCanvases();
            var renderer = field.textViewport.GetChild(0).GetComponent<CanvasRenderer>();
            var p = ((OniMoono.Engine.UI.CanvasRendererNative)renderer.EngineStateOrThrow).Geometry?.Positions ?? new Vector3[0];
            var quads = new Rect[p.Length / 4];
            for (int q = 0; q < quads.Length; q++)
                quads[q] = Rect.MinMaxRect(Mathf.Min(p[4 * q].x, p[4 * q + 2].x), Mathf.Min(p[4 * q].y, p[4 * q + 2].y), Mathf.Max(p[4 * q].x, p[4 * q + 2].x), Mathf.Max(p[4 * q].y, p[4 * q + 2].y));
            return quads;
        }

        static void AssertCaret(TMP_InputField field, int caret, int position)
        {
            Assert.Equal(caret, field.caretPosition);
            Assert.Equal(position, field.stringPosition);
        }

        [Fact]
        public void TheCaretIsAtTheLaidOutCharacterOfItsStringPosition()
        {
            // "a<b>c" lays out as a, c and the end marker (source indices 0, 4 and 5).
            var field = Typed("a<b>c");
            Assert.Equal("a<b>c", field.text);
            AssertCaret(field, 2, 5);
            var info = field.textComponent.textInfo;
            var caret = CaretQuads(field)[0];
            Assert.Equal(info.characterInfo[2].origin, caret.xMin, 3);
            // Left moves to the character before, where its source begins (over the tag).
            Type(Key(KeyCode.LeftArrow));
            AssertCaret(field, 1, 4);
            Assert.Equal(info.characterInfo[1].origin, CaretQuads(field)[0].xMin, 3);
            Type(Key(KeyCode.LeftArrow));
            AssertCaret(field, 0, 0);
            // Right moves past the character's source (before the tag that follows).
            Type(Key(KeyCode.RightArrow));
            AssertCaret(field, 1, 1);
            Type(Key(KeyCode.RightArrow));
            AssertCaret(field, 2, 5);
            // The selection's ends are kept both ways; it is drawn over the selected characters.
            Type(Key(KeyCode.LeftArrow, '\0', EventModifiers.Shift));
            Assert.Equal(2, field.selectionAnchorPosition);
            Assert.Equal(1, field.selectionFocusPosition);
            Assert.Equal(5, field.selectionStringAnchorPosition);
            Assert.Equal(4, field.selectionStringFocusPosition);
            var selection = CaretQuads(field);
            Assert.Single(selection);
            Assert.Equal(info.characterInfo[1].origin, selection[0].xMin, 3);
            Assert.Equal(info.characterInfo[2].origin, selection[0].xMax, 3);
            // Setting the caret position (a character index) sets the string position.
            field.caretPosition = 1;
            AssertCaret(field, 1, 4);

            // A surrogate pair (drawn as the missing glyph) is one character of two positions;
            // the caret is as tall as the character before it.
            field = Typed("a😀b");
            AssertCaret(field, 3, 4);
            Type(Key(KeyCode.LeftArrow));
            AssertCaret(field, 2, 3);
            info = field.textComponent.textInfo;
            caret = CaretQuads(field)[0];
            Assert.Equal(info.characterInfo[1].ascender, caret.yMax, 3);
            Assert.Equal(info.characterInfo[1].descender, caret.yMin, 3);
            Type(Key(KeyCode.LeftArrow));
            AssertCaret(field, 1, 1);
            caret = CaretQuads(field)[0];
            Assert.Equal(info.characterInfo[0].ascender, caret.yMax, 3);

            // An escape ("\n" typed as two characters) is one character.
            field = Typed("a\\nb");
            AssertCaret(field, 3, 4);
            Assert.Equal(2, field.textComponent.textInfo.lineCount);
            Type(Key(KeyCode.LeftArrow));
            AssertCaret(field, 2, 3);
            Type(Key(KeyCode.LeftArrow));
            AssertCaret(field, 1, 1);

            // Up and down start from the caret's character.
            field = Typed("ab<b>cd\nef", multiLine: true);
            AssertCaret(field, 7, 10);
            Type(Key(KeyCode.UpArrow));
            AssertCaret(field, 2, 5);
            Type(Key(KeyCode.DownArrow));
            AssertCaret(field, 7, 10);
        }

        [Fact]
        public void EachChangeLaysTheTextOutOnceAndAPasteOnce()
        {
            var field = NewField();
            field.ActivateInputField();
            Frame();
            var shown = field.textComponent;
            int layouts = 0, changes = 0;
            System.Action<Object> count = o => { if (o == shown) layouts++; };
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(count);
            try
            {
                field.onValueChanged.AddListener(_ => changes++);
                // One layout per typed character, none again in the canvas rebuild.
                Type(Char('a'), Char('b'), Char('c'));
                Canvas.ForceUpdateCanvases();
                Assert.Equal(3, layouts);
                Assert.Equal(4, shown.textInfo.characterCount);
                // A paste raises onValueChanged per character and lays the text out once.
                layouts = changes = 0;
                GUIUtility.systemCopyBuffer = new string('q', 3000);
                Type(Key(KeyCode.V, 'v', EventModifiers.Control));
                Canvas.ForceUpdateCanvases();
                Assert.Equal(1, layouts);
                Assert.Equal(3000, changes);
                Assert.Equal(3003, field.text.Length);
                Assert.Equal(3003, field.caretPosition);
                Assert.Equal(3004, shown.textInfo.characterCount);
                // Setting the text lays it out once.
                layouts = 0;
                field.text = "hello";
                Canvas.ForceUpdateCanvases();
                Assert.Equal(1, layouts);
            }
            finally { TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(count); }
        }

        [Fact]
        public void InATruncatedTextTheCaretEndsWhereTheTextStops()
        {
            // A 100 wide field whose text truncates: "QUIT GAME NOW" stops before the "W".
            foreach (var mode in new[] { TextOverflowModes.Truncate, TextOverflowModes.Ellipsis })
            {
                var field = NewField(100f);
                field.textComponent.overflowMode = mode;
                field.ActivateInputField();
                Frame();
                foreach (char c in "QUIT GAME NOW") Type(Char(c));
                var info = field.textComponent.textInfo;
                Assert.Equal(13, info.characterCount);
                // The record where it stopped is at the "W"'s source index and the pen there; the
                // caret at the end is drawn at it.
                var stopped = info.characterInfo[12];
                Assert.Equal('\u0003', stopped.character);
                Assert.Equal(12, stopped.index);
                Assert.Equal(info.characterInfo[11].xAdvance, stopped.origin, 3);
                Assert.Equal(12, field.caretPosition);
                Assert.Equal(stopped.origin, CaretQuads(field)[0].xMin, 3);
                // The ellipsis has the index of the character it replaced; Left goes there, End
                // back to where the text stopped.
                Assert.Equal(11, info.characterInfo[11].index);
                Type(Key(KeyCode.LeftArrow));
                AssertCaret(field, 11, 11);
                Type(Key(KeyCode.End));
                AssertCaret(field, 12, 12);
            }
        }

        [Fact]
        public void AFieldGivenItsTextAfterEnablingHasNoCaretUntilEnabledAgain()
        {
            // Built in code as a mod builds its chat field: the field is enabled before it has
            // a viewport and a text.
            TmpTestSupport.LoadGameData();
            if (EventSystem.current == null) new GameObject("EventSystem", typeof(EventSystem), typeof(SelectedUpdateModule));
            var logs = new List<(string message, LogType type)>();
            Application.LogCallback record = (message, _, type) => logs.Add((message, type));
            Application.logMessageReceived += record;
            try
            {
                var canvasGo = new GameObject("Canvas", typeof(RectTransform));
                canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var fieldGo = new GameObject("Field", typeof(RectTransform));
                fieldGo.transform.SetParent(canvasGo.transform, false);
                ((RectTransform)fieldGo.transform).sizeDelta = new Vector2(200f, 30f);
                fieldGo.AddComponent<Image>();
                var field = fieldGo.AddComponent<TMP_InputField>();
                var viewportGo = new GameObject("TextViewport", typeof(RectTransform));
                viewportGo.transform.SetParent(fieldGo.transform, false);
                var viewport = (RectTransform)viewportGo.transform;
                viewport.anchorMin = Vector2.zero;
                viewport.anchorMax = Vector2.one;
                viewport.sizeDelta = Vector2.zero;
                viewportGo.AddComponent<RectMask2D>();
                viewportGo.AddComponent<Image>();
                var textGo = new GameObject("Text", typeof(RectTransform));
                textGo.transform.SetParent(viewportGo.transform, false);
                var text = textGo.AddComponent<TextMeshProUGUI>();
                text.font = TmpTestSupport.Graystroke;
                field.textViewport = viewport;
                field.textComponent = text;
                field.text = "hello";

                string[] Children()
                {
                    var names = new string[viewport.childCount];
                    for (int i = 0; i < names.Length; i++) names[i] = viewport.GetChild(i).name;
                    return names;
                }

                // Canvas updates and frames don't create the caret.
                Canvas.ForceUpdateCanvases();
                Frame();
                Frame();
                Assert.Equal(new[] { "Text" }, Children());

                // Enabled again with its text, the field has its caret.
                fieldGo.SetActive(false);
                fieldGo.SetActive(true);
                Canvas.ForceUpdateCanvases();
                Frame();
                Assert.Equal(new[] { "Caret", "Text" }, Children());

                Assert.Empty(logs);
            }
            finally
            {
                Application.logMessageReceived -= record;
            }
        }

        // The start of editing, which mods patch by name: a private instance method taking nothing.
        [Fact]
        public void ActivationStartsInAPrivateMethodModsCanPatch()
        {
            var method = typeof(TMP_InputField).GetMethod("ActivateInputFieldInternal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, System.Type.EmptyTypes, null);
            Assert.NotNull(method);
            Assert.True(method.IsPrivate);
            Assert.Equal(typeof(void), method.ReturnType);
            var clear = typeof(TMP_InputField).GetMethod("DeactivateInputField", [typeof(bool)]);
            Assert.NotNull(clear);
        }

        // onFocus is invoked by ActivateInputFieldInternal, once per run.
        [Fact]
        public void ActivationRunsOnceAndNotAgainWhileFocused()
        {
            var field = NewField();
            int runs = 0;
            field.onFocus += () => runs++;
            field.ActivateInputField();
            Assert.Equal(0, runs);
            Frame();
            Assert.Equal(1, runs);
            Assert.True(field.isFocused);
            field.ActivateInputField();
            Frame();
            Frame();
            Assert.Equal(1, runs);
            Assert.True(field.isFocused);
        }

        [Fact]
        public void ActivationOfAnInactiveFieldDoesNothing()
        {
            var field = NewField();
            int runs = 0;
            field.onFocus += () => runs++;
            field.gameObject.SetActive(false);
            field.ActivateInputField();
            Frame();
            field.gameObject.SetActive(true);
            Frame();
            Frame();
            Assert.Equal(0, runs);
            Assert.False(field.isFocused);
        }

        [Fact]
        public void SelectingActivatesOnlyWithShouldActivateOnSelect()
        {
            var on = NewField();
            int onRuns = 0;
            on.onFocus += () => onRuns++;
            EventSystem.current.SetSelectedGameObject(on.gameObject);
            Assert.False(on.isFocused);
            Frame();
            Assert.Equal(1, onRuns);
            Assert.True(on.isFocused);

            var off = NewField();
            off.shouldActivateOnSelect = false;
            int offRuns = 0;
            off.onFocus += () => offRuns++;
            EventSystem.current.SetSelectedGameObject(off.gameObject);
            Frame();
            Frame();
            Assert.Equal(0, offRuns);
            Assert.False(off.isFocused);
            // Selecting another object deselected the first, which ended its editing.
            Assert.False(on.isFocused);
        }

        [Fact]
        public void DeactivatingASelectedFieldDeselectsIt()
        {
            var field = NewField();
            int runs = 0, deselects = 0;
            field.onFocus += () => runs++;
            field.onDeselect.AddListener(_ => deselects++);
            field.ActivateInputField();
            Frame();
            Assert.Same(field.gameObject, EventSystem.current.currentSelectedGameObject);
            field.DeactivateInputField();
            Assert.False(field.isFocused);
            Assert.Null(EventSystem.current.currentSelectedGameObject);
            Assert.Equal(1, deselects);
            // Selecting it again activates it, once.
            EventSystem.current.SetSelectedGameObject(field.gameObject);
            Assert.False(field.isFocused);
            Frame();
            Assert.Equal(2, runs);
            Assert.True(field.isFocused);

            // An unfocused, unselected field changes no selection.
            var other = NewField();
            other.DeactivateInputField(true);
            Assert.Same(field.gameObject, EventSystem.current.currentSelectedGameObject);
            Assert.True(field.isFocused);
        }

        [Fact]
        public void DisablingEndsEditing()
        {
            var field = NewField();
            string ended = null;
            field.onEndEdit.AddListener(s => ended = s);
            field.ActivateInputField();
            Frame();
            Type(Char('w'));
            field.gameObject.SetActive(false);
            Assert.False(field.isFocused);
            Assert.Equal("w", ended);
        }

        [Fact]
        public void DeselectingEndsEditing()
        {
            var field = NewField();
            string ended = null;
            field.onEndEdit.AddListener(s => ended = s);
            field.ActivateInputField();
            Frame();
            Type(Char('q'));
            field.OnDeselect(new BaseEventData(EventSystem.current));
            Assert.False(field.isFocused);
            Assert.Equal("q", ended);
            // The placeholder shows when the text is empty.
            var placeholder = (TextMeshProUGUI)field.placeholder;
            Assert.False(placeholder.enabled);
            field.text = "";
            Assert.True(placeholder.enabled);
        }

        [Fact]
        public void DropdownShowsItsOptionsAndSetsTheValue()
        {
            TmpTestSupport.LoadGameData();
            if (EventSystem.current == null) new GameObject("EventSystem", typeof(EventSystem), typeof(SelectedUpdateModule));
            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<GraphicRaycaster>();
            var root = new GameObject("Dropdown", typeof(RectTransform));
            root.transform.SetParent(canvasGo.transform, false);
            ((RectTransform)root.transform).sizeDelta = new Vector2(160f, 30f);
            root.SetActive(false);
            var dropdown = root.AddComponent<TMP_Dropdown>();
            var caption = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            caption.transform.SetParent(root.transform, false);
            caption.font = TmpTestSupport.Graystroke;
            // The template: a list with a content holding one toggle item with a text.
            var template = new GameObject("Template", typeof(RectTransform));
            template.transform.SetParent(root.transform, false);
            template.AddComponent<CanvasGroup>();
            var templateRect = (RectTransform)template.transform;
            templateRect.sizeDelta = new Vector2(160f, 150f);
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(template.transform, false);
            var contentRect = (RectTransform)content.transform;
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 28f);
            var item = new GameObject("Item", typeof(RectTransform));
            item.transform.SetParent(content.transform, false);
            var itemRect = (RectTransform)item.transform;
            itemRect.anchorMin = new Vector2(0f, 0.5f);
            itemRect.anchorMax = new Vector2(1f, 0.5f);
            itemRect.sizeDelta = new Vector2(0f, 20f);
            var toggle = item.AddComponent<Toggle>();
            var itemLabel = new GameObject("Item Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            itemLabel.transform.SetParent(item.transform, false);
            itemLabel.font = TmpTestSupport.Graystroke;
            dropdown.template = templateRect;
            dropdown.captionText = caption;
            dropdown.itemText = itemLabel;
            template.SetActive(false);
            root.SetActive(true);

            int changed = -1;
            dropdown.onValueChanged.AddListener(v => changed = v);
            dropdown.options = new List<TMP_Dropdown.OptionData> { new("Low"), new("Medium"), new("High") };
            Assert.Equal("Low", caption.text);
            dropdown.value = 2;
            Assert.Equal(2, changed);
            Assert.Equal("High", caption.text);

            Frame();
            dropdown.Show();
            Assert.True(dropdown.IsExpanded);
            var list = root.transform.Find("Dropdown List");
            Assert.NotNull(list);
            Assert.NotNull(canvasGo.transform.Find("Blocker"));
            var items = list.GetComponentsInChildren<Toggle>();
            Assert.Equal(3, items.Length);
            Assert.Equal("Item 1: Medium", items[1].name);
            Assert.True(items[2].isOn);
            items[1].isOn = true;
            Assert.Equal(1, dropdown.value);
            Assert.Equal("Medium", caption.text);
            Frame();
            Assert.Null(canvasGo.transform.Find("Blocker"));
        }
    }
}
