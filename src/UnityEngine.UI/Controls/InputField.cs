using System;
using System.Collections;
using UnityEngine.EventSystems;

namespace UnityEngine.UI
{
    /// A legacy single- or multi-line text field: it edits `text` while focused (keys from
    /// Event.PopEvent), shows it in its Text with a caret or selection drawn on a child object,
    /// and validates input by its content type.
    public partial class InputField
    {
        protected enum EditState
        {
            Continue = 0,
            Finish = 1,
        }

        const float HScrollSpeed = 0.05f;
        const string EmailSpecials = "!#$%&'*+-/=?^_`{|}~";

        [NonSerialized] protected Mesh m_Mesh;
        [NonSerialized] protected int m_DrawStart;
        [NonSerialized] protected int m_DrawEnd;
        [NonSerialized] protected bool m_CaretVisible;
        [NonSerialized] protected int m_CaretPosition;
        [NonSerialized] protected int m_CaretSelectPosition;
        [NonSerialized] protected RectTransform caretRectTrans;
        [NonSerialized] protected UIVertex[] m_CursorVerts;
        [NonSerialized] protected CanvasRenderer m_CachedInputRenderer;

        TextGenerator inputGenerator;
        bool editing;
        bool activateNextUpdate;
        bool canceled;
        bool updatingLabel;
        string textBeforeEditing = "";
        float blinkStart;
        Coroutine blink;
        readonly Event pending = new();

        protected InputField()
        {
            m_ContentType = ContentType.Standard;
            m_InputType = InputType.Standard;
            m_AsteriskChar = '*';
            m_KeyboardType = TouchScreenKeyboardType.Default;
            m_LineType = LineType.SingleLine;
            m_CharacterValidation = CharacterValidation.None;
            m_OnSubmit = new SubmitEvent();
            m_OnDidEndEdit = new EndEditEvent();
            m_OnValueChanged = new OnChangeEvent();
            m_CaretColor = new Color(50f / 255f, 50f / 255f, 50f / 255f, 1f);
            m_SelectionColor = new Color(168f / 255f, 206f / 255f, 255f / 255f, 192f / 255f);
            m_Text = string.Empty;
            m_CaretBlinkRate = 0.85f;
            m_CaretWidth = 1;
            m_ShouldActivateOnSelect = true;
            EnforceTextHOverflow();
        }

        protected TextGenerator cachedInputTextGenerator => inputGenerator ??= new TextGenerator();

        // ----- Settings -----

        public bool shouldHideMobileInput { get => m_HideMobileInput; set => m_HideMobileInput = value; }

        public bool shouldActivateOnSelect { get => m_ShouldActivateOnSelect; set => m_ShouldActivateOnSelect = value; }

        public string text
        {
            get => m_Text;
            set => SetText(value);
        }

        public void SetTextWithoutNotify(string input) => SetText(input, false);

        /// Line breaks and tabs are dropped from single-line fields; validation and the character
        /// limit apply.
        void SetText(string value, bool sendCallback = true)
        {
            if (text == value) return;
            value ??= "";
            value = value.Replace("\0", string.Empty);
            if (m_LineType == LineType.SingleLine) value = value.Replace("\n", "").Replace("\t", "");
            if (onValidateInput != null || characterValidation != CharacterValidation.None)
            {
                m_Text = "";
                var validator = onValidateInput ?? Validate;
                m_CaretPosition = m_CaretSelectPosition = value.Length;
                int count = characterLimit > 0 ? Math.Min(characterLimit, value.Length) : value.Length;
                for (int i = 0; i < count; i++)
                {
                    char c = validator(m_Text, m_Text.Length, value[i]);
                    if (c != 0) m_Text += c;
                }
            }
            else m_Text = characterLimit > 0 && value.Length > characterLimit ? value.Substring(0, characterLimit) : value;
            if (m_CaretPosition > m_Text.Length) m_CaretPosition = m_CaretSelectPosition = m_Text.Length;
            else if (m_CaretSelectPosition > m_Text.Length) m_CaretSelectPosition = m_Text.Length;
            if (sendCallback) SendOnValueChanged();
            UpdateLabel();
        }

        public bool isFocused => editing;

        public float caretBlinkRate
        {
            get => m_CaretBlinkRate;
            set
            {
                if (m_CaretBlinkRate == value) return;
                m_CaretBlinkRate = value;
                if (editing) SetCaretActive();
            }
        }

        public int caretWidth { get => m_CaretWidth; set { if (m_CaretWidth == value) return; m_CaretWidth = value; MarkGeometryAsDirty(); } }

        public Text textComponent
        {
            get => m_TextComponent;
            set
            {
                if (m_TextComponent != null)
                {
                    m_TextComponent.UnregisterDirtyVerticesCallback(MarkGeometryAsDirty);
                    m_TextComponent.UnregisterDirtyVerticesCallback(UpdateLabel);
                    m_TextComponent.UnregisterDirtyMaterialCallback(UpdateCaretMaterial);
                }
                if (m_TextComponent == value) return;
                m_TextComponent = value;
                if (m_TextComponent != null)
                {
                    m_TextComponent.RegisterDirtyVerticesCallback(MarkGeometryAsDirty);
                    m_TextComponent.RegisterDirtyVerticesCallback(UpdateLabel);
                    m_TextComponent.RegisterDirtyMaterialCallback(UpdateCaretMaterial);
                }
                EnforceTextHOverflow();
            }
        }

        public Graphic placeholder { get => m_Placeholder; set => m_Placeholder = value; }

        public Color caretColor
        {
            get => customCaretColor ? m_CaretColor : textComponent.color;
            set { if (m_CaretColor == value) return; m_CaretColor = value; MarkGeometryAsDirty(); }
        }

        public bool customCaretColor { get => m_CustomCaretColor; set { if (m_CustomCaretColor == value) return; m_CustomCaretColor = value; MarkGeometryAsDirty(); } }

        public Color selectionColor { get => m_SelectionColor; set { if (m_SelectionColor == value) return; m_SelectionColor = value; MarkGeometryAsDirty(); } }

        public EndEditEvent onEndEdit { get => m_OnDidEndEdit; set => m_OnDidEndEdit = value; }
        public SubmitEvent onSubmit { get => m_OnSubmit; set => m_OnSubmit = value; }

        [Obsolete("onValueChange has been renamed to onValueChanged")]
        public OnChangeEvent onValueChange { get => onValueChanged; set => onValueChanged = value; }

        public OnChangeEvent onValueChanged { get => m_OnValueChanged; set => m_OnValueChanged = value; }

        public OnValidateInput onValidateInput { get => m_OnValidateInput; set => m_OnValidateInput = value; }

        public int characterLimit
        {
            get => m_CharacterLimit;
            set
            {
                if (m_CharacterLimit == Math.Max(0, value)) return;
                m_CharacterLimit = Math.Max(0, value);
                UpdateLabel();
            }
        }

        public ContentType contentType
        {
            get => m_ContentType;
            set
            {
                if (m_ContentType == value) return;
                m_ContentType = value;
                EnforceContentType();
            }
        }

        public LineType lineType
        {
            get => m_LineType;
            set
            {
                if (m_LineType == value) return;
                m_LineType = value;
                SetToCustomIfContentTypeIsNot(ContentType.Standard, ContentType.Autocorrected);
                EnforceTextHOverflow();
            }
        }

        public InputType inputType { get => m_InputType; set { if (m_InputType == value) return; m_InputType = value; SetToCustom(); } }

        public TouchScreenKeyboard touchScreenKeyboard => null;

        public TouchScreenKeyboardType keyboardType { get => m_KeyboardType; set { if (m_KeyboardType == value) return; m_KeyboardType = value; SetToCustom(); } }

        public CharacterValidation characterValidation { get => m_CharacterValidation; set { if (m_CharacterValidation == value) return; m_CharacterValidation = value; SetToCustom(); } }

        public bool readOnly { get => m_ReadOnly; set => m_ReadOnly = value; }

        public bool multiLine => m_LineType == LineType.MultiLineNewline || m_LineType == LineType.MultiLineSubmit;

        public char asteriskChar { get => m_AsteriskChar; set { if (m_AsteriskChar == value) return; m_AsteriskChar = value; UpdateLabel(); } }

        public bool wasCanceled => canceled;

        // ----- Caret and selection -----

        protected int caretPositionInternal { get => m_CaretPosition + Input.compositionString.Length; set { m_CaretPosition = value; ClampPos(ref m_CaretPosition); } }
        protected int caretSelectPositionInternal { get => m_CaretSelectPosition + Input.compositionString.Length; set { m_CaretSelectPosition = value; ClampPos(ref m_CaretSelectPosition); } }

        bool hasSelection => caretPositionInternal != caretSelectPositionInternal;

        public int caretPosition
        {
            get => m_CaretSelectPosition + Input.compositionString.Length;
            set
            {
                selectionAnchorPosition = value;
                selectionFocusPosition = value;
            }
        }

        public int selectionAnchorPosition
        {
            get => m_CaretPosition + Input.compositionString.Length;
            set
            {
                if (Input.compositionString.Length != 0) return;
                m_CaretPosition = value;
                ClampPos(ref m_CaretPosition);
            }
        }

        public int selectionFocusPosition
        {
            get => m_CaretSelectPosition + Input.compositionString.Length;
            set
            {
                if (Input.compositionString.Length != 0) return;
                m_CaretSelectPosition = value;
                ClampPos(ref m_CaretSelectPosition);
            }
        }

        protected void ClampPos(ref int pos)
        {
            if (pos < 0) pos = 0;
            else if (pos > text.Length) pos = text.Length;
        }

        // ----- Lifecycle -----

        protected override void OnEnable()
        {
            base.OnEnable();
            m_Text ??= "";
            m_DrawStart = 0;
            m_DrawEnd = m_Text.Length;
            if (m_CachedInputRenderer != null) m_CachedInputRenderer.SetMaterial(Graphic.defaultGraphicMaterial, Texture2D.whiteTexture);
            if (m_TextComponent != null)
            {
                m_TextComponent.RegisterDirtyVerticesCallback(MarkGeometryAsDirty);
                m_TextComponent.RegisterDirtyVerticesCallback(UpdateLabel);
                m_TextComponent.RegisterDirtyMaterialCallback(UpdateCaretMaterial);
                UpdateLabel();
            }
        }

        protected override void OnDisable()
        {
            blink = null;
            DeactivateInputField();
            if (m_TextComponent != null)
            {
                m_TextComponent.UnregisterDirtyVerticesCallback(MarkGeometryAsDirty);
                m_TextComponent.UnregisterDirtyVerticesCallback(UpdateLabel);
                m_TextComponent.UnregisterDirtyMaterialCallback(UpdateCaretMaterial);
            }
            CanvasUpdateRegistry.DisableCanvasElementForRebuild(this);
            if (m_CachedInputRenderer != null) m_CachedInputRenderer.Clear();
            if (m_Mesh != null) DestroyImmediate(m_Mesh);
            m_Mesh = null;
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            CanvasUpdateRegistry.UnRegisterCanvasElementForRebuild(this);
            base.OnDestroy();
        }

        /// The caret blinks at caretBlinkRate (shown half of each cycle); 0 keeps it shown.
        IEnumerator CaretBlink()
        {
            m_CaretVisible = true;
            yield return null;
            while (isFocused && m_CaretBlinkRate > 0f)
            {
                float period = 1f / m_CaretBlinkRate;
                bool shown = (Time.unscaledTime - blinkStart) % period < period / 2f;
                if (m_CaretVisible != shown)
                {
                    m_CaretVisible = shown;
                    if (!hasSelection) MarkGeometryAsDirty();
                }
                yield return null;
            }
            blink = null;
        }

        void SetCaretVisible()
        {
            if (!editing) return;
            m_CaretVisible = true;
            blinkStart = Time.unscaledTime;
            SetCaretActive();
        }

        void SetCaretActive()
        {
            if (!editing) return;
            if (m_CaretBlinkRate > 0f)
            {
                if (blink == null) blink = StartCoroutine(CaretBlink());
            }
            else m_CaretVisible = true;
        }

        protected void OnFocus() => SelectAll();

        protected void SelectAll()
        {
            caretPositionInternal = text.Length;
            caretSelectPositionInternal = 0;
        }

        public void MoveTextEnd(bool shift)
        {
            int position = text.Length;
            if (shift) caretSelectPositionInternal = position;
            else caretPositionInternal = caretSelectPositionInternal = position;
            UpdateLabel();
        }

        public void MoveTextStart(bool shift)
        {
            if (shift) caretSelectPositionInternal = 0;
            else caretPositionInternal = caretSelectPositionInternal = 0;
            UpdateLabel();
        }

        static string clipboard
        {
            get => GUIUtility.systemCopyBuffer;
            set => GUIUtility.systemCopyBuffer = value;
        }

        /// Activation waits for the next LateUpdate, so the click that focuses does not edit.
        protected virtual void LateUpdate()
        {
            if (activateNextUpdate)
            {
                activateNextUpdate = false;
                if (!isFocused)
                {
                    ActivateInputFieldInternal();
                    return;
                }
            }
            AssignPositioningIfNeeded();
        }

        public virtual void OnBeginDrag(PointerEventData eventData)
        {
            if (!MayDrag(eventData)) return;
            dragging = true;
        }

        [NonSerialized] bool dragging;

        /// Dragging extends the selection to the character under the pointer.
        public virtual void OnDrag(PointerEventData eventData)
        {
            if (!MayDrag(eventData)) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(textComponent.rectTransform, eventData.position, eventData.pressEventCamera, out var local);
            caretSelectPositionInternal = GetCharacterIndexFromPosition(local) + m_DrawStart;
            MarkGeometryAsDirty();
        }

        public virtual void OnEndDrag(PointerEventData eventData)
        {
            if (!MayDrag(eventData)) return;
            dragging = false;
        }

        bool MayDrag(PointerEventData eventData)
            => IsActive() && IsInteractable() && eventData.button == PointerEventData.InputButton.Left && m_TextComponent != null && !m_ReadOnly;

        /// A press in a focused field places the caret (a double click selects the word).
        public override void OnPointerDown(PointerEventData eventData)
        {
            if (!MayDrag(eventData)) return;
            EventSystem.current.SetSelectedGameObject(gameObject, eventData);
            bool hadFocus = editing;
            base.OnPointerDown(eventData);
            if (!hadFocus) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(textComponent.rectTransform, eventData.position, eventData.pressEventCamera, out var local);
            caretSelectPositionInternal = caretPositionInternal = GetCharacterIndexFromPosition(local) + m_DrawStart;
            UpdateLabel();
            eventData.Use();
        }

        public virtual void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            ActivateInputField();
        }

        /// The keys of the frame: editing keys, clipboard shortcuts (Ctrl+A, C, V, X), Return
        /// (finishes a single-line or submit-on-return field) and Escape (cancels).
        protected EditState KeyPressed(Event evt)
        {
            var mods = evt.modifiers;
            bool mac = Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;
            bool ctrl = mac ? (mods & EventModifiers.Command) != 0 : (mods & EventModifiers.Control) != 0;
            bool shift = (mods & EventModifiers.Shift) != 0;
            bool alt = (mods & EventModifiers.Alt) != 0;
            bool ctrlOnly = ctrl && !alt && !shift;
            switch (evt.keyCode)
            {
                case KeyCode.Backspace: Backspace(); return EditState.Continue;
                case KeyCode.Delete: ForwardSpace(); return EditState.Continue;
                case KeyCode.Home: MoveTextStart(shift); return EditState.Continue;
                case KeyCode.End: MoveTextEnd(shift); return EditState.Continue;
                case KeyCode.A when ctrlOnly: SelectAll(); return EditState.Continue;
                case KeyCode.C when ctrlOnly:
                    clipboard = inputType != InputType.Password ? SelectedText() : "";
                    return EditState.Continue;
                case KeyCode.V when ctrlOnly:
                    Append(clipboard);
                    return EditState.Continue;
                case KeyCode.X when ctrlOnly:
                    clipboard = inputType != InputType.Password ? SelectedText() : "";
                    Delete();
                    SendOnValueChanged();
                    return EditState.Continue;
                case KeyCode.LeftArrow: MoveLeft(shift, ctrl); return EditState.Continue;
                case KeyCode.RightArrow: MoveRight(shift, ctrl); return EditState.Continue;
                case KeyCode.UpArrow: MoveVertical(shift, up: true); return EditState.Continue;
                case KeyCode.DownArrow: MoveVertical(shift, up: false); return EditState.Continue;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (lineType != LineType.MultiLineNewline) return EditState.Finish;
                    break;
                case KeyCode.Escape:
                    canceled = true;
                    return EditState.Finish;
            }
            char c = evt.character;
            if (!multiLine && (c == '\t' || c == '\r' || c == '\n')) return EditState.Continue;
            if (c == '\r' || c == (char)3) c = '\n';
            if (IsValidChar(c)) Append(c);
            if (c == 0 && Input.compositionString.Length > 0) UpdateLabel();
            return EditState.Continue;
        }

        bool IsValidChar(char c)
        {
            if (c == 127) return false;
            if (c == '\t' || c == '\n') return true;
            return m_TextComponent.font.HasCharacter(c);
        }

        public void ProcessEvent(Event e) => KeyPressed(e);

        /// While focused, the frame's key events edit the text; Return or Escape ends editing.
        public virtual void OnUpdateSelected(BaseEventData eventData)
        {
            if (!isFocused) return;
            bool consumed = false;
            while (Event.PopEvent(pending))
            {
                if (pending.rawType == EventType.KeyDown)
                {
                    consumed = true;
                    if (KeyPressed(pending) == EditState.Finish)
                    {
                        if (!canceled) SendOnSubmit();
                        DeactivateInputField();
                        break;
                    }
                }
                if ((pending.type == EventType.ValidateCommand || pending.type == EventType.ExecuteCommand) && pending.commandName == "SelectAll")
                {
                    SelectAll();
                    consumed = true;
                }
            }
            if (consumed) UpdateLabel();
            eventData.Use();
        }

        string SelectedText()
        {
            if (!hasSelection) return "";
            int start = Math.Min(caretPositionInternal, caretSelectPositionInternal), end = Math.Max(caretPositionInternal, caretSelectPositionInternal);
            return text.Substring(start, end - start);
        }

        int WordStart(int from)
        {
            int i = from - 1;
            while (i > 0 && char.IsWhiteSpace(text[i - 1]) == char.IsWhiteSpace(text[from - 1]) && !char.IsWhiteSpace(text[i - 1])) i--;
            if (from > 0 && char.IsWhiteSpace(text[from - 1])) { while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--; while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--; }
            return Math.Max(0, i);
        }

        int WordEnd(int from)
        {
            int i = from;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i;
        }

        void MoveRight(bool shift, bool word)
        {
            if (hasSelection && !shift)
            {
                caretPositionInternal = caretSelectPositionInternal = Math.Max(caretPositionInternal, caretSelectPositionInternal);
                return;
            }
            int position = word ? WordEnd(caretSelectPositionInternal) : Math.Min(text.Length, caretSelectPositionInternal + 1);
            if (shift) caretSelectPositionInternal = position;
            else caretSelectPositionInternal = caretPositionInternal = position;
        }

        void MoveLeft(bool shift, bool word)
        {
            if (hasSelection && !shift)
            {
                caretPositionInternal = caretSelectPositionInternal = Math.Min(caretPositionInternal, caretSelectPositionInternal);
                return;
            }
            int position = word ? WordStart(caretSelectPositionInternal) : Math.Max(0, caretSelectPositionInternal - 1);
            if (shift) caretSelectPositionInternal = position;
            else caretSelectPositionInternal = caretPositionInternal = position;
        }

        /// Up and down move between lines of a multi-line field (to its ends on a single line).
        void MoveVertical(bool shift, bool up)
        {
            if (hasSelection && !shift)
            {
                caretPositionInternal = caretSelectPositionInternal = up ? Math.Min(caretPositionInternal, caretSelectPositionInternal) : Math.Max(caretPositionInternal, caretSelectPositionInternal);
                return;
            }
            int position = multiLine ? LineNeighbour(caretSelectPositionInternal, up) : (up ? 0 : text.Length);
            if (shift) caretSelectPositionInternal = position;
            else caretPositionInternal = caretSelectPositionInternal = position;
        }

        /// The character nearest the same x on the line above or below.
        int LineNeighbour(int position, bool up)
        {
            var g = cachedInputTextGenerator;
            if (g.characterCount == 0) return position;
            int visible = Mathf.Clamp(position - m_DrawStart, 0, g.characterCount - 1);
            int line = LineOf(visible, g);
            int target = up ? line - 1 : line + 1;
            if (target < 0) return 0;
            if (target >= g.lineCount) return text.Length;
            float x = g.characters[visible].cursorPos.x;
            int start = g.lines[target].startCharIdx, end = LineEnd(g, target);
            for (int i = start; i < end; i++)
            {
                var ch = g.characters[i];
                if (ch.cursorPos.x + ch.charWidth / 2f > x) return i + m_DrawStart;
            }
            return end + m_DrawStart;
        }

        static int LineOf(int character, TextGenerator g)
        {
            for (int i = 0; i < g.lineCount - 1; i++)
                if (g.lines[i + 1].startCharIdx > character) return i;
            return g.lineCount - 1;
        }

        static int LineEnd(TextGenerator g, int line)
            => line + 1 < g.lineCount ? Math.Max(g.lines[line].startCharIdx, g.lines[line + 1].startCharIdx - 1) : g.characterCountVisible;

        void Delete()
        {
            if (m_ReadOnly || caretPositionInternal == caretSelectPositionInternal) return;
            int start = Math.Min(caretPositionInternal, caretSelectPositionInternal), end = Math.Max(caretPositionInternal, caretSelectPositionInternal);
            m_Text = text.Substring(0, start) + text.Substring(end);
            caretPositionInternal = caretSelectPositionInternal = start;
        }

        void ForwardSpace()
        {
            if (m_ReadOnly) return;
            if (hasSelection)
            {
                Delete();
                SendOnValueChanged();
            }
            else if (caretPositionInternal < text.Length)
            {
                m_Text = text.Remove(caretPositionInternal, 1);
                SendOnValueChanged();
            }
        }

        void Backspace()
        {
            if (m_ReadOnly) return;
            if (hasSelection)
            {
                Delete();
                SendOnValueChanged();
            }
            else if (caretPositionInternal > 0)
            {
                m_Text = text.Remove(caretPositionInternal - 1, 1);
                caretSelectPositionInternal = caretPositionInternal = caretPositionInternal - 1;
                SendOnValueChanged();
            }
        }

        /// Replaces the selection by the character, within the character limit.
        void Insert(char c)
        {
            if (m_ReadOnly) return;
            Delete();
            if (characterLimit > 0 && text.Length >= characterLimit) return;
            m_Text = text.Insert(m_CaretPosition, c.ToString());
            caretSelectPositionInternal = caretPositionInternal += 1;
            SendOnValueChanged();
        }

        void SendOnValueChanged()
        {
            UISystemProfilerApi.AddMarker("InputField.value", this);
            onValueChanged?.Invoke(text);
        }

        protected void SendOnSubmit()
        {
            UISystemProfilerApi.AddMarker("InputField.onSubmit", this);
            onSubmit?.Invoke(m_Text);
        }

        protected void SendOnEndEdit()
        {
            UISystemProfilerApi.AddMarker("InputField.onEndEdit", this);
            onEndEdit?.Invoke(m_Text);
        }

        protected virtual void Append(string input)
        {
            if (m_ReadOnly || !InPlaceEditing()) return;
            foreach (char ch in input)
                if (ch >= ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '\u0003')
                    Append(ch);
        }

        /// A typed character, through the validator (onValidateInput or the content type's).
        protected virtual void Append(char input)
        {
            if (char.IsSurrogate(input) || m_ReadOnly || !InPlaceEditing()) return;
            int insertAt = Math.Min(selectionFocusPosition, selectionAnchorPosition);
            if (onValidateInput != null) input = onValidateInput(text, insertAt, input);
            else if (characterValidation != CharacterValidation.None) input = Validate(text, insertAt, input);
            if (input == 0) return;
            Insert(input);
        }

        static bool InPlaceEditing() => !TouchScreenKeyboard.isSupported || TouchScreenKeyboard.isInPlaceEditingAllowed;

        /// The character the content type accepts at the position, or 0.
        protected char Validate(string text, int pos, char ch)
        {
            if (characterValidation == CharacterValidation.None || !enabled) return ch;
            switch (characterValidation)
            {
                case CharacterValidation.Integer:
                case CharacterValidation.Decimal:
                {
                    bool signAllowed = pos == 0 && (text.Length == 0 || text[0] != '-');
                    bool selectionIncludesStart = caretPositionInternal == 0 || caretSelectPositionInternal == 0;
                    if (ch >= '0' && ch <= '9' && (pos != 0 || text.Length == 0 || text[0] != '-' || selectionIncludesStart)) return ch;
                    if (ch == '-' && (signAllowed || selectionIncludesStart)) return ch;
                    if (characterValidation == CharacterValidation.Decimal && (ch == '.' || ch == ',') && !text.Contains('.') && !text.Contains(',')) return ch;
                    return (char)0;
                }
                case CharacterValidation.Alphanumeric:
                    return ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' ? ch : (char)0;
                case CharacterValidation.Name:
                {
                    // Letters, with each word capitalized; single spaces and apostrophes between.
                    if (char.IsLetter(ch))
                    {
                        bool wordStart = pos == 0 || text[pos - 1] == ' ';
                        if (wordStart) return char.ToUpper(ch);
                        return char.IsLower(ch) || pos > 0 && text[pos - 1] == '\'' ? ch : char.ToLower(ch);
                    }
                    if (ch == '\'')
                        return !text.Contains('\'') && !(pos > 0 && (text[pos - 1] == ' ' || text[pos - 1] == '\'')) && !(pos < text.Length && (text[pos] == ' ' || text[pos] == '\'')) ? ch : (char)0;
                    if (ch == ' ' && pos != 0)
                        return !(pos > 0 && (text[pos - 1] == ' ' || text[pos - 1] == '\'')) && !(pos < text.Length && (text[pos] == ' ' || text[pos] == '\'')) ? ch : (char)0;
                    return (char)0;
                }
                case CharacterValidation.EmailAddress:
                {
                    if (ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9') return ch;
                    if (ch == '@' && text.IndexOf('@') == -1) return ch;
                    if (EmailSpecials.IndexOf(ch) != -1) return ch;
                    if (ch == '.')
                    {
                        char before = pos > 0 ? text[Math.Clamp(pos, 0, text.Length) - 1] : ' ';
                        char after = text.Length > 0 && pos < text.Length ? text[Math.Clamp(pos, 0, text.Length - 1)] : '\n';
                        if (before != '.' && after != '.') return ch;
                    }
                    return (char)0;
                }
            }
            return ch;
        }

        // ----- Activation -----

        public void ActivateInputField()
        {
            if (m_TextComponent == null || m_TextComponent.font == null || !IsActive() || !IsInteractable()) return;
            activateNextUpdate = true;
        }

        void ActivateInputFieldInternal()
        {
            if (EventSystem.current == null) return;
            if (EventSystem.current.currentSelectedGameObject != gameObject) EventSystem.current.SetSelectedGameObject(gameObject);
            Input.imeCompositionMode = IMECompositionMode.On;
            OnFocus();
            editing = true;
            textBeforeEditing = text;
            canceled = false;
            SetCaretVisible();
            UpdateLabel();
        }

        public override void OnSelect(BaseEventData eventData)
        {
            base.OnSelect(eventData);
            if (shouldActivateOnSelect) ActivateInputField();
        }

        /// Ends editing: a canceled edit restores the text from before, onEndEdit fires.
        public void DeactivateInputField()
        {
            if (!editing) return;
            editing = false;
            if (m_Placeholder != null) m_Placeholder.enabled = string.IsNullOrEmpty(m_Text);
            if (m_TextComponent != null && IsInteractable())
            {
                if (canceled) text = textBeforeEditing;
                SendOnEndEdit();
                m_CaretPosition = m_CaretSelectPosition = 0;
            }
            Input.imeCompositionMode = IMECompositionMode.Auto;
            MarkGeometryAsDirty();
            UpdateLabel();
        }

        public override void OnDeselect(BaseEventData eventData)
        {
            DeactivateInputField();
            base.OnDeselect(eventData);
        }

        public virtual void OnSubmit(BaseEventData eventData)
        {
            if (!IsActive() || !IsInteractable()) return;
            if (!isFocused) activateNextUpdate = true;
        }

        // ----- Display -----

        /// The Text shows the visible part of the text (asterisks for passwords); the placeholder
        /// shows while the text is empty.
        protected void UpdateLabel()
        {
            if (m_TextComponent == null || m_TextComponent.font == null || updatingLabel) return;
            updatingLabel = true;
            try
            {
                string full = EventSystem.current != null && gameObject == EventSystem.current.currentSelectedGameObject && Input.compositionString.Length > 0
                    ? text.Substring(0, m_CaretPosition) + Input.compositionString + text.Substring(m_CaretPosition)
                    : text;
                string shown = inputType == InputType.Password ? new string(asteriskChar, full.Length) : full;
                bool empty = string.IsNullOrEmpty(full);
                if (m_Placeholder != null) m_Placeholder.enabled = empty;
                if (!editing)
                {
                    m_DrawStart = 0;
                    m_DrawEnd = m_Text.Length;
                }
                if (!empty)
                {
                    var settings = m_TextComponent.GetGenerationSettings(m_TextComponent.rectTransform.rect.size);
                    settings.generateOutOfBounds = true;
                    cachedInputTextGenerator.PopulateWithErrors(shown, settings, gameObject);
                    SetDrawRangeToContainCaretPosition(caretSelectPositionInternal);
                    shown = shown.Substring(m_DrawStart, Mathf.Min(m_DrawEnd, shown.Length) - m_DrawStart);
                    SetCaretVisible();
                }
                m_TextComponent.text = shown;
                MarkGeometryAsDirty();
            }
            finally { updatingLabel = false; }
        }

        /// Scrolls the visible range (whole lines of a multi-line field, characters of a single
        /// line) so the caret stays in view.
        void SetDrawRangeToContainCaretPosition(int caretPos)
        {
            var g = cachedInputTextGenerator;
            if (g.lineCount <= 0) return;
            var extents = m_TextComponent.rectTransform.rect.size;
            float ppu = m_TextComponent.pixelsPerUnit;
            if (multiLine)
            {
                var lines = g.lines;
                int caretLine = LineOf(Math.Max(0, caretPos), g);
                if (caretPos > m_DrawEnd)
                {
                    m_DrawEnd = LineEnd(g, caretLine);
                    float bottom = lines[caretLine].topY - lines[caretLine].height;
                    int start = caretLine;
                    while (start > 0 && lines[start - 1].topY - bottom <= extents.y * ppu) start--;
                    m_DrawStart = lines[start].startCharIdx;
                }
                else
                {
                    if (caretPos < m_DrawStart) m_DrawStart = lines[caretLine].startCharIdx;
                    int first = LineOf(m_DrawStart, g), last = first;
                    float top = lines[first].topY;
                    while (last + 1 < g.lineCount && top - (lines[last + 1].topY - lines[last + 1].height) <= extents.y * ppu) last++;
                    m_DrawEnd = LineEnd(g, last);
                }
                return;
            }
            var chars = g.characters;
            if (m_DrawEnd > g.characterCountVisible) m_DrawEnd = g.characterCountVisible;
            float width = 0f;
            if (caretPos > m_DrawEnd || (caretPos == m_DrawEnd && m_DrawStart > 0))
            {
                m_DrawEnd = caretPos;
                for (m_DrawStart = m_DrawEnd - 1; m_DrawStart >= 0; m_DrawStart--)
                {
                    if (width + chars[m_DrawStart].charWidth > extents.x * ppu) break;
                    width += chars[m_DrawStart].charWidth;
                }
                m_DrawStart++;
            }
            else
            {
                if (caretPos < m_DrawStart) m_DrawStart = caretPos;
                m_DrawEnd = m_DrawStart;
            }
            for (; m_DrawEnd < g.characterCountVisible; m_DrawEnd++)
            {
                width += chars[m_DrawEnd].charWidth;
                if (width > extents.x * ppu) break;
            }
        }

        public void ForceLabelUpdate() => UpdateLabel();

        void MarkGeometryAsDirty() => CanvasUpdateRegistry.RegisterCanvasElementForGraphicRebuild(this);

        public virtual void Rebuild(CanvasUpdate update)
        {
            if (update == CanvasUpdate.LatePreRender) UpdateGeometry();
        }

        public virtual void LayoutComplete() { }
        public virtual void GraphicUpdateComplete() { }

        /// The caret or the selection, drawn on a child next to the text (made on first use).
        void UpdateGeometry()
        {
            if (m_CachedInputRenderer == null && m_TextComponent != null)
            {
                var go = new GameObject(transform.name + " Input Caret", typeof(RectTransform));
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(m_TextComponent.transform.parent);
                go.transform.SetAsFirstSibling();
                go.layer = gameObject.layer;
                caretRectTrans = go.GetComponent<RectTransform>();
                m_CachedInputRenderer = go.AddComponent<CanvasRenderer>();
                m_CachedInputRenderer.SetMaterial(Graphic.defaultGraphicMaterial, Texture2D.whiteTexture);
                // The caret area always matches the text area, whatever the layout.
                go.AddComponent<LayoutElement>().ignoreLayout = true;
                AssignPositioningIfNeeded();
            }
            if (m_CachedInputRenderer == null) return;
            if (m_Mesh == null) m_Mesh = new Mesh { name = "Input Field Caret", hideFlags = HideFlags.HideAndDontSave };
            using (var vh = new VertexHelper())
            {
                if (isFocused)
                {
                    var offset = m_TextComponent.PixelAdjustPoint(Vector2.zero);
                    if (!hasSelection) Caret(vh, offset);
                    else Highlight(vh, offset);
                }
                vh.FillMesh(m_Mesh);
            }
            m_CachedInputRenderer.SetMesh(m_Mesh);
        }

        /// The caret's rect matches the text's.
        void AssignPositioningIfNeeded()
        {
            if (m_TextComponent == null || caretRectTrans == null) return;
            var t = m_TextComponent.rectTransform;
            if (caretRectTrans.localPosition != t.localPosition || caretRectTrans.localRotation != t.localRotation || caretRectTrans.localScale != t.localScale
                || caretRectTrans.anchorMin != t.anchorMin || caretRectTrans.anchorMax != t.anchorMax || caretRectTrans.anchoredPosition != t.anchoredPosition
                || caretRectTrans.sizeDelta != t.sizeDelta || caretRectTrans.pivot != t.pivot)
            {
                caretRectTrans.localPosition = t.localPosition;
                caretRectTrans.localRotation = t.localRotation;
                caretRectTrans.localScale = t.localScale;
                caretRectTrans.anchorMin = t.anchorMin;
                caretRectTrans.anchorMax = t.anchorMax;
                caretRectTrans.anchoredPosition = t.anchoredPosition;
                caretRectTrans.sizeDelta = t.sizeDelta;
                caretRectTrans.pivot = t.pivot;
            }
        }

        void Caret(VertexHelper vh, Vector2 offset)
        {
            if (!m_CaretVisible) return;
            m_CursorVerts ??= new UIVertex[4];
            float width = m_CaretWidth;
            int at = Math.Max(0, caretPositionInternal - m_DrawStart);
            var g = m_TextComponent.cachedTextGenerator;
            if (g == null || g.lineCount == 0) return;
            float ppu = m_TextComponent.pixelsPerUnit;
            Vector2 start;
            if (at < g.characters.Count) start = new Vector2(g.characters[at].cursorPos.x / ppu, g.characters[at].cursorPos.y / ppu);
            else start = new Vector2(0f, 0f);
            int line = LineOf(at, g);
            float height = g.lines[line].height / ppu;
            start.x += offset.x;
            start.y += offset.y;
            Color32 c = caretColor;
            var quad = new UIVertex[4];
            for (int i = 0; i < 4; i++)
            {
                quad[i] = UIVertex.simpleVert;
                quad[i].color = c;
                quad[i].uv0 = Vector2.zero;
            }
            quad[0].position = new Vector3(start.x, start.y - height, 0f);
            quad[1].position = new Vector3(start.x + width, start.y - height, 0f);
            quad[2].position = new Vector3(start.x + width, start.y, 0f);
            quad[3].position = new Vector3(start.x, start.y, 0f);
            m_CursorVerts = quad;
            vh.AddUIVertexQuad(quad);
        }

        /// One quad per line the selection covers.
        void Highlight(VertexHelper vh, Vector2 offset)
        {
            int startChar = Math.Max(0, Math.Min(caretPositionInternal, caretSelectPositionInternal) - m_DrawStart);
            int endChar = Math.Max(0, Math.Max(caretPositionInternal, caretSelectPositionInternal) - m_DrawStart) - 1;
            var g = m_TextComponent.cachedTextGenerator;
            if (g.lineCount <= 0) return;
            float ppu = m_TextComponent.pixelsPerUnit;
            int line = LineOf(startChar, g), lineEnd = LineEnd(g, line);
            var quad = new UIVertex[4];
            for (int i = 0; i < 4; i++) { quad[i] = UIVertex.simpleVert; quad[i].color = (Color32)selectionColor; quad[i].uv0 = Vector2.zero; }
            int current = startChar;
            while (current <= endChar && current < g.characterCount)
            {
                if (current == lineEnd || current == endChar)
                {
                    var a = g.characters[startChar];
                    var b = g.characters[current];
                    var info = g.lines[line];
                    var min = new Vector2(a.cursorPos.x / ppu, info.topY / ppu - info.height / ppu);
                    var max = new Vector2((b.cursorPos.x + b.charWidth) / ppu, info.topY / ppu);
                    if (min.x > max.x) max.x = min.x;
                    min += offset;
                    max += offset;
                    quad[0].position = new Vector3(min.x, min.y);
                    quad[1].position = new Vector3(max.x, min.y);
                    quad[2].position = new Vector3(max.x, max.y);
                    quad[3].position = new Vector3(min.x, max.y);
                    vh.AddUIVertexQuad(quad);
                    startChar = current + 1;
                    line++;
                    if (line < g.lineCount) lineEnd = LineEnd(g, line);
                }
                current++;
            }
        }

        /// The visible character at a point in the text's rect.
        protected int GetCharacterIndexFromPosition(Vector2 pos)
        {
            var g = m_TextComponent.cachedTextGenerator;
            if (g.lineCount == 0) return 0;
            float ppu = m_TextComponent.pixelsPerUnit;
            int line = g.lineCount - 1;
            for (int i = 0; i < g.lineCount; i++)
                if (pos.y * ppu > g.lines[i].topY - g.lines[i].height)
                {
                    line = i;
                    break;
                }
            int start = g.lines[line].startCharIdx, end = LineEnd(g, line);
            for (int i = start; i < end; i++)
            {
                var ch = g.characters[i];
                if (pos.x * ppu < ch.cursorPos.x + ch.charWidth / 2f) return i;
            }
            return end;
        }

        void UpdateCaretMaterial()
        {
            if (m_TextComponent == null || m_CachedInputRenderer == null) return;
            m_CachedInputRenderer.SetMaterial(m_TextComponent.GetModifiedMaterial(Graphic.defaultGraphicMaterial), Texture2D.whiteTexture);
        }

        // ----- Content types -----

        /// The input, keyboard and validation settings of the content type.
        void EnforceContentType()
        {
            switch (contentType)
            {
                case ContentType.Standard: m_InputType = InputType.Standard; m_KeyboardType = TouchScreenKeyboardType.Default; m_CharacterValidation = CharacterValidation.None; break;
                case ContentType.Autocorrected: m_InputType = InputType.AutoCorrect; m_KeyboardType = TouchScreenKeyboardType.Default; m_CharacterValidation = CharacterValidation.None; break;
                case ContentType.IntegerNumber: m_LineType = LineType.SingleLine; m_InputType = InputType.Standard; m_KeyboardType = TouchScreenKeyboardType.NumberPad; m_CharacterValidation = CharacterValidation.Integer; break;
                case ContentType.DecimalNumber: m_LineType = LineType.SingleLine; m_InputType = InputType.Standard; m_KeyboardType = TouchScreenKeyboardType.NumbersAndPunctuation; m_CharacterValidation = CharacterValidation.Decimal; break;
                case ContentType.Alphanumeric: m_LineType = LineType.SingleLine; m_InputType = InputType.Standard; m_KeyboardType = TouchScreenKeyboardType.ASCIICapable; m_CharacterValidation = CharacterValidation.Alphanumeric; break;
                case ContentType.Name: m_LineType = LineType.SingleLine; m_InputType = InputType.Standard; m_KeyboardType = TouchScreenKeyboardType.NamePhonePad; m_CharacterValidation = CharacterValidation.Name; break;
                case ContentType.EmailAddress: m_LineType = LineType.SingleLine; m_InputType = InputType.Standard; m_KeyboardType = TouchScreenKeyboardType.EmailAddress; m_CharacterValidation = CharacterValidation.EmailAddress; break;
                case ContentType.Password: m_LineType = LineType.SingleLine; m_InputType = InputType.Password; m_KeyboardType = TouchScreenKeyboardType.Default; m_CharacterValidation = CharacterValidation.None; break;
                case ContentType.Pin: m_LineType = LineType.SingleLine; m_InputType = InputType.Password; m_KeyboardType = TouchScreenKeyboardType.NumberPad; m_CharacterValidation = CharacterValidation.Integer; break;
            }
            EnforceTextHOverflow();
        }

        /// A single-line field never wraps its text.
        void EnforceTextHOverflow()
        {
            if (m_TextComponent == null) return;
            m_TextComponent.horizontalOverflow = multiLine ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
        }

        void SetToCustomIfContentTypeIsNot(params ContentType[] allowed)
        {
            if (contentType == ContentType.Custom) return;
            foreach (var t in allowed)
                if (contentType == t) return;
            contentType = ContentType.Custom;
        }

        void SetToCustom()
        {
            if (contentType == ContentType.Custom) return;
            contentType = ContentType.Custom;
        }

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            // While editing the field stays in its selected look.
            if (editing) state = SelectionState.Selected;
            base.DoStateTransition(state, instant);
        }

        // ----- Layout: the text component's sizes -----

        public virtual void CalculateLayoutInputHorizontal() { }
        public virtual void CalculateLayoutInputVertical() { }
        public virtual float minWidth => 0f;

        public virtual float preferredWidth
        {
            get
            {
                if (textComponent == null) return 0f;
                var settings = textComponent.GetGenerationSettings(Vector2.zero);
                return textComponent.cachedTextGeneratorForLayout.GetPreferredWidth(m_Text, settings) / textComponent.pixelsPerUnit;
            }
        }

        public virtual float flexibleWidth => -1f;
        public virtual float minHeight => 0f;

        public virtual float preferredHeight
        {
            get
            {
                if (textComponent == null) return 0f;
                var settings = textComponent.GetGenerationSettings(new Vector2(textComponent.rectTransform.rect.size.x, 0f));
                return textComponent.cachedTextGeneratorForLayout.GetPreferredHeight(m_Text, settings) / textComponent.pixelsPerUnit;
            }
        }

        public virtual float flexibleHeight => -1f;
        public virtual int layoutPriority => 1;

        public partial class SubmitEvent { public SubmitEvent() { } }
        public partial class EndEditEvent { public EndEditEvent() { } }
        public partial class OnChangeEvent { public OnChangeEvent() { } }
    }
}
