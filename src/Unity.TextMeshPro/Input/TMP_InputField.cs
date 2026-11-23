using System;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TMPro
{
    // An editable text: a TMP text in a viewport, a placeholder, and a caret and selection drawn
    // by a child graphic. ActivateInputField starts editing in the next LateUpdate; while focused,
    // the event system's update of the selected object drains the keyboard events
    // (Event.PopEvent) and applies the editing keys and typed characters (validated), as the
    // shipped package does; LateUpdate scrolls the text to keep the caret visible.
    public partial class TMP_InputField
    {
        protected enum EditState
        {
            Continue,
            Finish,
        }

        // The zero-width space appended to the shown text, so that the caret has a character to
        // stand before at the end of the text.
        const char EndMarker = '​';

        bool m_AllowInput;
        bool m_ShouldActivateNextUpdate;
        bool m_WasCanceled;
        bool m_TextChangedSinceActivation;
        string m_OriginalText = "";
        int m_StringPosition, m_StringSelectPosition;
        float m_BlinkStart;
        bool m_CaretVisible;
        TMP_SelectionCaret m_Caret;
        // The caret's RectTransform, from its creation. Interface: PLib's TextMeshProPatcher
        // reads it by name (`___caretRectTrans`) in its prefix on AssignPositioningIfNeeded.
        RectTransform caretRectTrans;
        readonly Event m_ProcessingEvent = new();
        bool m_PreventCallback;
        bool m_IsSelectAll;
        bool m_Dragging;
        // The caret graphic's geometry: made in the first caret rebuild after enable, refilled
        // with the caret quad, the selection quads or nothing, and dropped on disable.
        // Interface: Applied Logistics Network's subclass reads m_Mesh.
        protected Mesh m_Mesh;
        Scrollbar m_ListenedScrollbar;
        // The caret position and label the text was last scrolled for.
        int m_ScrolledForPosition = -1;
        string m_ScrolledForLabel;
        // The IME composition the label shows at the caret (issue #30).
        string m_LabelComposition = "";
        // Input.compositionCursorPos is set from the next caret drawn (after a label without a
        // composition), or when the caret moves to another line: as shipped.
        bool m_PlaceIme = true;
        int m_ImeLine = -1;

        protected Mesh mesh => m_Mesh ??= new Mesh();

        protected TMP_InputField()
        {
            m_Text = "";
            m_ContentType = ContentType.Standard;
            m_InputType = InputType.Standard;
            m_AsteriskChar = '*';
            m_KeyboardType = TouchScreenKeyboardType.Default;
            m_LineType = LineType.SingleLine;
            m_CharacterValidation = CharacterValidation.None;
            m_RegexValue = "";
            m_GlobalPointSize = 14f;
            m_CharacterLimit = 0;
            m_OnEndEdit = new SubmitEvent();
            m_OnSubmit = new SubmitEvent();
            m_OnSelect = new SelectionEvent();
            m_OnDeselect = new SelectionEvent();
            m_OnTextSelection = new TextSelectionEvent();
            m_OnEndTextSelection = new TextSelectionEvent();
            m_OnValueChanged = new OnChangeEvent();
            m_OnTouchScreenKeyboardStatusChanged = new TouchScreenKeyboardEvent();
            m_CaretColor = new Color(50f / 255f, 50f / 255f, 50f / 255f, 1f);
            m_SelectionColor = new Color(168f / 255f, 206f / 255f, 255f / 255f, 192f / 255f);
            m_CaretBlinkRate = 0.85f;
            m_CaretWidth = 1;
            m_RichText = true;
            m_ScrollSensitivity = 1f;
            m_LineLimit = 0;
            m_ShouldActivateOnSelect = true;
            m_RestoreOriginalTextOnEscape = true;
            // On in a new field (measured), as in the game's prefabs.
            m_ResetOnDeActivation = true;
        }

        // ----- Settings -----

        public string text
        {
            get => m_Text;
            set => SetText(value, true);
        }

        public void SetTextWithoutNotify(string input) => SetText(input, false);

        // Text set from code is kept whole: neither the character limit, nor the single-line
        // rule, nor the content type applies to it, in any state, then or later (measured:
        // "507.2" at limit 4 stays "507.2"). Only typed characters are limited and validated.
        void SetText(string value, bool sendCallback)
        {
            if (value == null) value = "";
            value = value.Replace("\0", "");
            if (m_Text == value) return;
            m_Text = value;
            if (m_StringPosition > m_Text.Length) m_StringPosition = m_StringSelectPosition = m_Text.Length;
            else if (m_StringSelectPosition > m_Text.Length) m_StringSelectPosition = m_Text.Length;
            if (sendCallback) SendOnValueChanged();
            UpdateLabel();
        }

        public bool isFocused => m_AllowInput;

        public float caretBlinkRate
        {
            get => m_CaretBlinkRate;
            set
            {
                m_CaretBlinkRate = value;
                if (m_AllowInput) m_BlinkStart = Time.unscaledTime;
            }
        }

        public int caretWidth
        {
            get => m_CaretWidth;
            set
            {
                if (m_CaretWidth == value) return;
                m_CaretWidth = value;
                MarkGeometryChanged();
            }
        }

        public RectTransform textViewport { get => m_TextViewport; set => m_TextViewport = value; }

        public TMP_Text textComponent
        {
            get => m_TextComponent;
            // Leaves the text component's text alone (measured: an assigned "" stays "").
            set => m_TextComponent = value;
        }

        public Graphic placeholder
        {
            get => m_Placeholder;
            set => m_Placeholder = value;
        }

        public Scrollbar verticalScrollbar { get => m_VerticalScrollbar; set => m_VerticalScrollbar = value; }

        public float scrollSensitivity { get => m_ScrollSensitivity; set => m_ScrollSensitivity = value; }

        public Color caretColor
        {
            get => m_CustomCaretColor ? m_CaretColor : (m_TextComponent != null ? m_TextComponent.color : m_CaretColor);
            set
            {
                m_CaretColor = value;
                MarkGeometryChanged();
            }
        }

        public bool customCaretColor { get => m_CustomCaretColor; set => m_CustomCaretColor = value; }

        public Color selectionColor
        {
            get => m_SelectionColor;
            set
            {
                m_SelectionColor = value;
                MarkGeometryChanged();
            }
        }

        public SubmitEvent onEndEdit { get => m_OnEndEdit; set => m_OnEndEdit = value; }
        public SubmitEvent onSubmit { get => m_OnSubmit; set => m_OnSubmit = value; }
        public SelectionEvent onSelect { get => m_OnSelect; set => m_OnSelect = value; }
        public SelectionEvent onDeselect { get => m_OnDeselect; set => m_OnDeselect = value; }
        public TextSelectionEvent onTextSelection { get => m_OnTextSelection; set => m_OnTextSelection = value; }
        public TextSelectionEvent onEndTextSelection { get => m_OnEndTextSelection; set => m_OnEndTextSelection = value; }
        public OnChangeEvent onValueChanged { get => m_OnValueChanged; set => m_OnValueChanged = value; }
        public TouchScreenKeyboardEvent onTouchScreenKeyboardStatusChanged { get => m_OnTouchScreenKeyboardStatusChanged; set => m_OnTouchScreenKeyboardStatusChanged = value; }
        public OnValidateInput onValidateInput { get => m_OnValidateInput; set => m_OnValidateInput = value; }

        public int characterLimit
        {
            get => m_CharacterLimit;
            set
            {
                // A text longer than the new limit is kept, without an event (measured): the
                // limit applies to typed characters only. The label is written again, also on
                // an inactive field (measured: a text component's own "507.2" becomes U+200B).
                if (m_CharacterLimit == value) return;
                m_CharacterLimit = Math.Max(0, value);
                UpdateLabel();
            }
        }

        public float pointSize
        {
            get => m_GlobalPointSize;
            set
            {
                m_GlobalPointSize = Math.Max(0f, value);
                SetGlobalPointSize(m_GlobalPointSize);
            }
        }

        public TMP_FontAsset fontAsset
        {
            get => m_GlobalFontAsset;
            set
            {
                m_GlobalFontAsset = value;
                SetGlobalFontAsset(value);
            }
        }

        public bool onFocusSelectAll { get => m_OnFocusSelectAll; set => m_OnFocusSelectAll = value; }

        public bool resetOnDeActivation { get => m_ResetOnDeActivation; set => m_ResetOnDeActivation = value; }

        public bool keepTextSelectionVisible { get => m_KeepTextSelectionVisible; set => m_KeepTextSelectionVisible = value; }

        public bool restoreOriginalTextOnEscape { get => m_RestoreOriginalTextOnEscape; set => m_RestoreOriginalTextOnEscape = value; }

        public bool isRichTextEditingAllowed { get => m_isRichTextEditingAllowed; set => m_isRichTextEditingAllowed = value; }

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
                SetTextComponentWrapMode();
                SetToCustomIfContentTypeIsNot(ContentType.Standard, ContentType.Autocorrected);
            }
        }

        public int lineLimit
        {
            get => m_LineLimit;
            set => m_LineLimit = m_LineType == LineType.SingleLine ? 1 : Math.Max(0, value);
        }

        public InputType inputType
        {
            get => m_InputType;
            set
            {
                if (m_InputType == value) return;
                m_InputType = value;
                SetToCustom();
            }
        }

        public TouchScreenKeyboardType keyboardType
        {
            get => m_KeyboardType;
            set
            {
                if (m_KeyboardType == value) return;
                m_KeyboardType = value;
                SetToCustom();
            }
        }

        public CharacterValidation characterValidation
        {
            get => m_CharacterValidation;
            set
            {
                if (m_CharacterValidation == value) return;
                m_CharacterValidation = value;
                SetToCustom();
            }
        }

        public TMP_InputValidator inputValidator
        {
            get => m_InputValidator;
            set
            {
                // The validator applies when characterValidation is CustomValidator (setting it
                // does not change that, as in the shipped package).
                m_InputValidator = value;
            }
        }

        public bool readOnly { get => m_ReadOnly; set => m_ReadOnly = value; }

        public bool richText
        {
            get => m_RichText;
            set
            {
                m_RichText = value;
                SetTextComponentRichTextMode();
            }
        }

        public bool multiLine => m_LineType == LineType.MultiLineNewline || m_LineType == LineType.MultiLineSubmit;

        public char asteriskChar
        {
            get => m_AsteriskChar;
            set
            {
                m_AsteriskChar = value;
                UpdateLabel();
            }
        }

        public bool wasCanceled => m_WasCanceled;

        public bool shouldHideMobileInput { get => m_HideMobileInput; set => m_HideMobileInput = value; }

        public bool shouldHideSoftKeyboard { get => m_HideSoftKeyboard; set => m_HideSoftKeyboard = value; }

        public virtual bool shouldActivateOnSelect { get => m_ShouldActivateOnSelect; set => m_ShouldActivateOnSelect = value; }

        // ----- Caret and selection -----

        // The caret and the selection are kept as string positions (in text). The caret
        // position and the selection's anchor and focus positions are character indices in the
        // laid-out text, which tags, escapes and surrogate pairs make differ from them
        // (measured on the shipped package).
        public int caretPosition
        {
            get => SelectAllPending ? m_LagFocus : CharacterIndexOf(m_StringSelectPosition);
            set
            {
                selectionAnchorPosition = value;
                selectionFocusPosition = value;
            }
        }

        public int selectionAnchorPosition
        {
            get => SelectAllPending ? m_LagAnchor : CharacterIndexOf(m_StringPosition);
            set => selectionStringAnchorPosition = StringIndexOf(value);
        }

        public int selectionFocusPosition
        {
            get => SelectAllPending ? m_LagFocus : CharacterIndexOf(m_StringSelectPosition);
            set => selectionStringFocusPosition = StringIndexOf(value);
        }

        public int stringPosition
        {
            get => m_StringSelectPosition;
            set
            {
                selectionStringAnchorPosition = value;
                selectionStringFocusPosition = value;
            }
        }

        // The caret's string index (Text Input Hotkeys reads it).
        internal int stringPositionInternal => m_StringSelectPosition;

        public int selectionStringAnchorPosition
        {
            get => m_StringPosition;
            set
            {
                m_StringPosition = Mathf.Clamp(value, 0, m_Text.Length);
                MarkGeometryChanged();
            }
        }

        public int selectionStringFocusPosition
        {
            get => m_StringSelectPosition;
            set
            {
                m_StringSelectPosition = Mathf.Clamp(value, 0, m_Text.Length);
                MarkGeometryChanged();
            }
        }

        // The laid-out character a string position is at: the first whose source index is not
        // before it (the end marker's at the end). Without a layout, the string position. While
        // an edit's events run, before its text is laid out, a position past the old layout's
        // end lies that far past its last character: a typed character's onValueChanged reads
        // the caret after it (measured: typing "500" reports 1, 2 and 3).
        int CharacterIndexOf(int position)
        {
            var info = m_TextComponent != null ? m_TextComponent.textInfo : null;
            if (info == null || info.characterCount == 0) return position;
            for (int k = 0; k < info.characterCount; k++)
                if (info.characterInfo[k].index >= position) return k;
            int last = info.characterCount - 1;
            if (m_LabelText != null && !string.Equals(m_LabelText, m_Text, StringComparison.Ordinal))
                return last + Math.Max(0, position - info.characterInfo[last].index);
            return last;
        }

        // Ctrl+A moves the string positions at once and the character positions a frame later
        // (measured: right after it, selectionStringAnchorPosition is 4 in "1000" while
        // selectionAnchorPosition still reads 0). Until the frame ends, or the positions move
        // again, the character positions read as Ctrl+A found them.
        int m_LagFrame = -1, m_LagAnchor, m_LagFocus, m_LagStringAnchor, m_LagStringFocus;

        bool SelectAllPending => m_LagFrame == Time.frameCount && m_LagStringAnchor == m_StringPosition && m_LagStringFocus == m_StringSelectPosition;

        // The string position of a laid-out character: where its source begins.
        int StringIndexOf(int character)
        {
            var info = m_TextComponent != null ? m_TextComponent.textInfo : null;
            if (info == null || info.characterCount == 0) return character;
            if (character < 0) return 0;
            if (character >= info.characterCount) return m_Text.Length;
            return Mathf.Min(info.characterInfo[character].index, m_Text.Length);
        }

        // The text's layout for the current text, laid out now when it is out of date (the
        // keys handled in one update move over what the previous ones typed).
        TMP_TextInfo CurrentTextInfo()
        {
            if (m_TextComponent == null) return null;
            if (m_TextComponent.havePropertiesChanged && m_TextComponent.isActiveAndEnabled) m_TextComponent.ForceMeshUpdate();
            return m_TextComponent.textInfo;
        }

        bool HasSelection => m_StringPosition != m_StringSelectPosition;

        int SelectionStart => Math.Min(m_StringPosition, m_StringSelectPosition);

        int SelectionEnd => Math.Max(m_StringPosition, m_StringSelectPosition);

        // ----- Lifecycle -----

        protected override void Awake()
        {
            base.Awake();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (m_Text == null) m_Text = "";
            if (m_TextComponent != null)
            {
                SetTextComponentWrapMode();
                SetTextComponentRichTextMode();
                m_TextComponent.RegisterDirtyVerticesCallback(OnTextComponentDirty);
            }
            if (EnsureCaret()) AssignPositioningIfNeeded();
            if (m_VerticalScrollbar != null)
            {
                m_ListenedScrollbar = m_VerticalScrollbar;
                m_ListenedScrollbar.onValueChanged.AddListener(OnScrollbarValueChange);
            }
            // The caret draws with the default UI material and a white texture from the start
            // (measured: the shipped package's caret renderer has them before it draws).
            if (m_Caret != null) m_Caret.canvasRenderer.SetMaterial(Graphic.defaultGraphicMaterial, Texture2D.whiteTexture);
            // The label is written now but laid out in the canvas rebuild, like any changed
            // text (measured: characterCount stays 0 through this frame's LateUpdate).
            UpdateLabel(false);
        }

        protected override void OnDisable()
        {
            m_ShouldActivateNextUpdate = false;
            DeactivateInputField();
            if (m_TextComponent != null) m_TextComponent.UnregisterDirtyVerticesCallback(OnTextComponentDirty);
            CanvasUpdateRegistry.UnRegisterCanvasElementForRebuild(this);
            if (m_ListenedScrollbar != null) m_ListenedScrollbar.onValueChanged.RemoveListener(OnScrollbarValueChange);
            m_ListenedScrollbar = null;
            DropMesh();
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            CanvasUpdateRegistry.UnRegisterCanvasElementForRebuild(this);
            DropMesh();
            base.OnDestroy();
        }

        void DropMesh()
        {
            if (m_Mesh != null) Destroy(m_Mesh);
            m_Mesh = null;
        }

        // The caret graphic: a child of the viewport (its first), drawn under the text. It is
        // created only here, when the field is enabled with a text component: a field given its
        // text component later has none until it is enabled again, and nothing else creates it
        // (a graphic created in a canvas rebuild would be logged). It is made at the world
        // origin with the RectTransform defaults and keeps that world position under the
        // viewport; AssignPositioningIfNeeded then places it. True when one was made.
        bool EnsureCaret()
        {
            if (m_Caret != null || m_TextComponent == null) return false;
            var parent = m_TextViewport != null ? m_TextViewport : m_TextComponent.rectTransform.parent as RectTransform;
            if (parent == null) return false;
            var go = new GameObject("Caret", typeof(RectTransform));
            go.hideFlags = HideFlags.DontSave;
            go.layer = gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, true);
            rt.SetAsFirstSibling();
            caretRectTrans = rt;
            go.AddComponent<CanvasRenderer>();
            m_Caret = go.AddComponent<TMP_SelectionCaret>();
            m_Caret.Owner = this;
            var element = go.AddComponent<LayoutElement>();
            element.ignoreLayout = true;
            return true;
        }

        // Places the caret's rect where the text component's is: its local transform and its
        // rect values. Called where the shipped package calls it: in OnEnable once per caret
        // made, in the caret rebuild after the text was moved to keep the caret in view, and
        // after the vertical scrollbar's value change moved the text. Interface: PLib's
        // TextMeshProPatcher finds it by name and may skip it with a Harmony prefix, copying the
        // values itself at the end of the frame (until then the caret is drawn where its rect
        // was).
        void AssignPositioningIfNeeded()
        {
            if (m_TextComponent == null || caretRectTrans == null) return;
            var text = m_TextComponent.rectTransform;
            var caret = caretRectTrans;
            caret.localPosition = text.localPosition;
            caret.localRotation = text.localRotation;
            caret.localScale = text.localScale;
            caret.anchorMin = text.anchorMin;
            caret.anchorMax = text.anchorMax;
            caret.anchoredPosition = text.anchoredPosition;
            caret.sizeDelta = text.sizeDelta;
            caret.pivot = text.pivot;
        }

        // ----- Activation -----

        public void ActivateInputField()
        {
            if (m_TextComponent == null || m_TextComponent.font == null || !IsActive() || !IsInteractable()) return;
            if (isFocused) return;
            m_ShouldActivateNextUpdate = true;
        }

        // Where editing starts, in the LateUpdate after a request; mods patch it by name (with
        // isFocused still false on entry). It runs once per activation: LateUpdate skips a field
        // already focused, and a request on an inactive field is never made.
        void ActivateInputFieldInternal()
        {
            if (EventSystem.current == null) return;
            if (EventSystem.current.currentSelectedGameObject != gameObject) EventSystem.current.SetSelectedGameObject(gameObject);
            m_AllowInput = true;
            // The IME is turned on for the edit, once: a mode set while the field is focused
            // stays until it is deactivated (measured, issue #30).
            Input.imeCompositionMode = IMECompositionMode.On;
            m_OriginalText = m_Text;
            m_WasCanceled = false;
            m_TextChangedSinceActivation = false;
            // Without onFocusSelectAll the caret stays where it was (measured: at 0 for a field
            // whose text was set from code, not at the end).
            // With it, the caret goes to the start and the anchor to the end, as Ctrl+A puts them
            // (measured: caretPosition 0, selectionAnchorPosition 3 in "500").
            if (m_OnFocusSelectAll)
            {
                m_StringPosition = m_Text.Length;
                m_StringSelectPosition = 0;
                m_IsSelectAll = true;
            }
            m_BlinkStart = Time.unscaledTime;
            m_CaretVisible = true;
            m_ScrolledForPosition = -1;
            onFocus?.Invoke();
            UpdateLabel();
        }

        public void DeactivateInputField() => DeactivateInputField(false);

        public void DeactivateInputField(bool clearSelection) => EndEdit(clearSelection, false);

        // Ends the edit. The caret and the selection stay where they were, whatever
        // resetOnDeActivation and clearSelection say; only a text shorter than them (Escape's
        // restore, then a listener's) cuts them, as any text set does (measured). onEndEdit is
        // sent, unfocused and with the caret as it was, on Enter and Escape (`keyed`), and
        // otherwise only with resetOnDeActivation or clearSelection: without them,
        // DeactivateInputField() and deselection send onDeselect alone, and disabling nothing.
        void EndEdit(bool clearSelection, bool keyed)
        {
            m_ShouldActivateNextUpdate = false;
            if (!m_AllowInput) return;
            m_AllowInput = false;
            // Back to Auto, whatever the mode was set to while focused (measured).
            Input.imeCompositionMode = IMECompositionMode.Auto;
            if (m_WasCanceled && m_RestoreOriginalTextOnEscape) text = m_OriginalText;
            m_IsSelectAll = false;
            if (keyed || m_ResetOnDeActivation || clearSelection) SendOnEndEdit();
            UpdateLabel();
            MarkGeometryChanged();
            // A focused field ends selected no more, whatever clearSelection says, so selecting it
            // again activates it. Its OnDeselect calls DeactivateInputField again, which finds it
            // unfocused. Not while the event system is already changing the selection.
            var eventSystem = EventSystem.current;
            if (eventSystem != null && !eventSystem.alreadySelecting && eventSystem.currentSelectedGameObject == gameObject)
                eventSystem.SetSelectedGameObject(null);
        }

        public override void OnSelect(BaseEventData eventData)
        {
            base.OnSelect(eventData);
            m_OnSelect?.Invoke(m_Text);
            if (m_ShouldActivateOnSelect) ActivateInputField();
        }

        public override void OnDeselect(BaseEventData eventData)
        {
            DeactivateInputField();
            base.OnDeselect(eventData);
            m_OnDeselect?.Invoke(m_Text);
        }

        public virtual void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            ActivateInputField();
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            bool hadFocus = isFocused;
            base.OnPointerDown(eventData);
            if (!hadFocus) return;
            // A click in a focused field moves the caret to the clicked character.
            int index = PositionFromScreen(eventData.position, eventData.pressEventCamera);
            if (index < 0) return;
            m_StringSelectPosition = index;
            if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) m_StringPosition = index;
            MarkGeometryChanged();
            eventData.Use();
        }

        public virtual void OnBeginDrag(PointerEventData eventData)
        {
            if (!isFocused || eventData.button != PointerEventData.InputButton.Left) return;
            m_Dragging = true;
        }

        public virtual void OnDrag(PointerEventData eventData)
        {
            if (!m_Dragging || !isFocused) return;
            int index = PositionFromScreen(eventData.position, eventData.pressEventCamera);
            if (index < 0) return;
            m_StringSelectPosition = index;
            MarkGeometryChanged();
            eventData.Use();
        }

        public virtual void OnEndDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            m_Dragging = false;
            if (HasSelection) m_OnEndTextSelection?.Invoke(m_Text, SelectionStart, SelectionEnd);
        }

        // On a focused field: onSubmit, then the edit ends as DeactivateInputField() ends it and
        // the field is deselected (measured). One that is not focused is activated.
        public virtual void OnSubmit(BaseEventData eventData)
        {
            if (!IsActive() || !IsInteractable()) return;
            if (!isFocused)
            {
                m_ShouldActivateNextUpdate = true;
                return;
            }
            SendOnSubmit();
            DeactivateInputField();
        }

        public virtual void OnCancel(BaseEventData eventData)
        {
            if (!IsActive() || !IsInteractable()) return;
            if (isFocused)
            {
                m_WasCanceled = true;
                DeactivateInputField();
            }
        }

        public virtual void OnScroll(PointerEventData eventData)
        {
            if (m_TextComponent == null || !multiLine) return;
            float delta = eventData.scrollDelta.y * m_ScrollSensitivity;
            var textRect = m_TextComponent.rectTransform;
            var p = textRect.anchoredPosition;
            float y = Mathf.Max(0f, p.y - delta);
            if (y == p.y) return;
            textRect.anchoredPosition = new Vector2(p.x, y);
            // Not measured on the shipped package: the caret follows, as after the scrollbar.
            AssignPositioningIfNeeded();
            UpdateScrollbar();
            MarkGeometryChanged();
        }

        public virtual void OnUpdateSelected(BaseEventData eventData)
        {
            if (!isFocused) return;
            bool changed = false;
            while (Event.PopEvent(m_ProcessingEvent))
            {
                var type = m_ProcessingEvent.rawType;
                if (type != EventType.KeyDown) continue;
                string before = m_Text;
                var state = KeyPressed(m_ProcessingEvent);
                if (m_Text != before) changed = true;
                if (state == EditState.Finish)
                {
                    // Enter: onSubmit, onEndEdit, onDeselect. Escape: the restored text's
                    // onValueChanged, onEndEdit, onDeselect (measured).
                    if (!m_WasCanceled) SendOnSubmit();
                    EndEdit(false, true);
                    break;
                }
            }
            // Every edit shows its text as it is made; this catches one that did not. The IME's
            // composition changes without key events: the keys it takes never reach the field.
            if ((changed && !ReferenceEquals(m_Text, m_LabelText)) || !string.Equals(Composition, m_LabelComposition, StringComparison.Ordinal)) UpdateLabel();
            eventData.Use();
        }

        // ----- Editing -----

        protected virtual void LateUpdate()
        {
            if (m_ShouldActivateNextUpdate)
            {
                m_ShouldActivateNextUpdate = false;
                if (!isFocused)
                {
                    ActivateInputFieldInternal();
                    return;
                }
            }
            if (!isFocused) return;
            UpdateBlink();
        }

        public void ProcessEvent(Event e) => KeyPressed(e);

        // Interface: protected, not virtual, the parameter named evt, as shipped (mods patch it
        // by name and a subclass may call it).
        protected EditState KeyPressed(Event evt)
        {
            var modifiers = evt.modifiers;
            bool ctrl = SystemInfo.operatingSystemFamily == OperatingSystemFamily.MacOSX ? (modifiers & EventModifiers.Command) != 0 : (modifiers & EventModifiers.Control) != 0;
            bool shift = (modifiers & EventModifiers.Shift) != 0;
            bool alt = (modifiers & EventModifiers.Alt) != 0;
            bool ctrlOnly = ctrl && !alt && !shift;
            switch (evt.keyCode)
            {
                case KeyCode.Backspace:
                    // One character whatever the modifiers (measured).
                    Backspace();
                    return EditState.Continue;
                case KeyCode.Delete: ForwardDelete(ctrl); return EditState.Continue;
                case KeyCode.Home: MoveTextStart(shift); return EditState.Continue;
                // Ctrl+End goes to the text's end (measured: in a five-line field it scrolls to
                // the last line).
                case KeyCode.End when ctrl: MoveTextEnd(shift); return EditState.Continue;
                case KeyCode.End: MoveToEndOfLaidOutLine(shift); return EditState.Continue;
                case KeyCode.A when ctrlOnly: SelectAll(); return EditState.Continue;
                case KeyCode.C when ctrlOnly:
                    if (m_InputType != InputType.Password && HasSelection) GUIUtility.systemCopyBuffer = SelectedText();
                    return EditState.Continue;
                case KeyCode.V when ctrlOnly:
                    Append(GUIUtility.systemCopyBuffer ?? "");
                    return EditState.Continue;
                case KeyCode.X when ctrlOnly:
                    if (m_InputType != InputType.Password && HasSelection) GUIUtility.systemCopyBuffer = SelectedText();
                    DeleteSelection();
                    return EditState.Continue;
                case KeyCode.LeftArrow: MoveLeft(shift, ctrl); return EditState.Continue;
                case KeyCode.RightArrow: MoveRight(shift, ctrl); return EditState.Continue;
                case KeyCode.UpArrow: MoveUp(shift); return EditState.Continue;
                case KeyCode.DownArrow: MoveDown(shift); return EditState.Continue;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (m_LineType != LineType.MultiLineNewline) return EditState.Finish;
                    break;
                case KeyCode.Escape:
                    m_WasCanceled = true;
                    return EditState.Finish;
            }
            char c = evt.character;
            if (!multiLine && (c == '\t' || c == '\r' || c == '\n')) return EditState.Continue;
            if (c == '\r' || c == 3) c = '\n';
            if (IsValidChar(c)) Append(c);
            return EditState.Continue;
        }

        bool IsValidChar(char c)
        {
            if (c == 0) return false;
            if (c == 127) return false;
            if (c == '\t' || c == '\n') return multiLine || c == '\t';
            return !char.IsControl(c);
        }

        string SelectedText() => HasSelection ? m_Text.Substring(SelectionStart, SelectionEnd - SelectionStart) : "";

        // A paste inserts its characters one by one (each validated, each raising
        // onValueChanged) and shows the result once: the text is laid out once per paste
        // (measured on the shipped package).
        protected virtual void Append(string input)
        {
            if (m_ReadOnly || input == null) return;
            m_DeferLabel = true;
            try
            {
                foreach (char c in input)
                {
                    if (c >= ' ' || c == '\t' || c == '\r' || c == '\n') Append(c == '\r' ? '\n' : c);
                }
            }
            finally { m_DeferLabel = false; }
            UpdateLabel();
        }

        // Set while a paste inserts its characters.
        bool m_DeferLabel;

        // The text the label last showed.
        string m_LabelText;

        // What the label last wrote into the text component (with the end marker).
        string m_LabelShown;

        // Inserts a typed character at the caret (replacing the selection) once it passes the
        // validation: the onValidateInput callback's when there is one (whatever the character
        // validation, CustomValidator included), else a custom validator's (which inserts it
        // itself) or the content type's, as in the shipped package. Each sees the text with the
        // selection removed and the position where it began (measured on the shipped package).
        // A character the content type or the callback rejects changes nothing: the selection
        // stays. A custom validator is handed the text after the selection is removed, which
        // stays removed whatever it returns (without onValueChanged when it rejects).
        protected virtual void Append(char input)
        {
            if (m_ReadOnly) return;
            if (m_OnValidateInput == null && m_CharacterValidation == CharacterValidation.CustomValidator)
            {
                if (HasSelection)
                {
                    DeleteSelectionInternal();
                    // Shown even if the character is then refused.
                    UpdateLabel();
                }
                if (m_CharacterLimit > 0 && m_Text.Length >= m_CharacterLimit) return;
                if (m_InputValidator == null) return;
                string t = m_Text;
                int pos = m_StringSelectPosition;
                m_InputValidator.Validate(ref t, ref pos, input);
                if (t == m_Text) return;
                if (m_CharacterLimit > 0 && t.Length > m_CharacterLimit) return;
                m_Text = t;
                m_StringPosition = m_StringSelectPosition = Mathf.Clamp(pos, 0, m_Text.Length);
                SendOnValueChanged();
                UpdateLabel();
                return;
            }
            string remaining = HasSelection ? m_Text.Remove(SelectionStart, SelectionEnd - SelectionStart) : m_Text;
            int at = HasSelection ? SelectionStart : m_StringSelectPosition;
            if (m_CharacterLimit > 0 && remaining.Length >= m_CharacterLimit) return;
            if (m_OnValidateInput != null) input = m_OnValidateInput(remaining, at, input);
            else if (m_CharacterValidation != CharacterValidation.None) input = Validate(remaining, at, input);
            if (input == 0) return;
            if (HasSelection) DeleteSelectionInternal();
            Insert(input);
        }

        void Insert(char c)
        {
            if (m_ReadOnly) return;
            m_Text = m_Text.Insert(m_StringSelectPosition, c.ToString());
            m_StringSelectPosition++;
            m_StringPosition = m_StringSelectPosition;
            m_IsSelectAll = false;
            SendOnValueChanged();
            UpdateLabel();
        }

        // The documented validations of the content types.
        protected char Validate(string text, int pos, char ch)
        {
            switch (m_CharacterValidation)
            {
                case CharacterValidation.None:
                    return ch;
                case CharacterValidation.Digit:
                    return ch >= '0' && ch <= '9' ? ch : (char)0;
                case CharacterValidation.Integer:
                case CharacterValidation.Decimal:
                {
                    bool sign = text.Length > 0 && text[0] == '-';
                    bool cursorBeforeSign = sign && pos == 0;
                    if (cursorBeforeSign) return (char)0;
                    if (ch >= '0' && ch <= '9') return ch;
                    if (ch == '-' && pos == 0 && !sign) return ch;
                    if (m_CharacterValidation == CharacterValidation.Decimal && (ch == '.' || ch == ',') && text.IndexOf('.') < 0 && text.IndexOf(',') < 0) return ch;
                    return (char)0;
                }
                case CharacterValidation.Alphanumeric:
                    return (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') ? ch : (char)0;
                case CharacterValidation.Name:
                {
                    char prev = pos > 0 && pos <= text.Length ? text[pos - 1] : ' ';
                    char next = pos < text.Length ? text[pos] : '\n';
                    if (char.IsLetter(ch))
                    {
                        // Each word starts with a capital, the rest is lower case.
                        if (prev == ' ' || pos == 0) return char.ToUpper(ch);
                        return char.ToLower(ch);
                    }
                    if (ch == '\'')
                        return prev != ' ' && prev != '\'' && next != '\'' && !text.Contains("'") ? ch : (char)0;
                    if (ch == ' ')
                        return prev != ' ' && prev != '\'' && next != ' ' && next != '\'' && pos != 0 ? ch : (char)0;
                    return (char)0;
                }
                case CharacterValidation.EmailAddress:
                {
                    if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) return ch;
                    if (ch == '@' && text.IndexOf('@') == -1) return ch;
                    const string allowed = "!#$%&'*+-/=?^_`{|}~";
                    if (allowed.IndexOf(ch) != -1) return ch;
                    if (ch == '.')
                    {
                        char prev = pos > 0 && pos <= text.Length ? text[pos - 1] : ' ';
                        char next = pos < text.Length ? text[pos] : '\n';
                        return prev != '.' && next != '.' ? ch : (char)0;
                    }
                    return (char)0;
                }
                case CharacterValidation.Regex:
                    return !string.IsNullOrEmpty(m_RegexValue) && Regex.IsMatch(ch.ToString(), m_RegexValue) ? ch : (char)0;
            }
            return ch;
        }

        // Removes the selection, or the character before the caret (a surrogate pair whole),
        // whatever the modifiers (measured).
        void Backspace()
        {
            if (m_ReadOnly) return;
            if (HasSelection)
            {
                DeleteSelection();
                return;
            }
            if (m_StringSelectPosition <= 0)
            {
                // An empty field still reports its text, though nothing changed (measured).
                if (m_Text.Length == 0) SendOnValueChanged();
                return;
            }
            int start = m_StringSelectPosition - 1;
            if (start > 0 && char.IsLowSurrogate(m_Text[start]) && char.IsHighSurrogate(m_Text[start - 1])) start--;
            m_Text = m_Text.Remove(start, m_StringSelectPosition - start);
            m_StringPosition = m_StringSelectPosition = start;
            SendOnValueChanged();
            UpdateLabel();
        }

        void ForwardDelete(bool word)
        {
            if (m_ReadOnly) return;
            if (HasSelection)
            {
                DeleteSelection();
                return;
            }
            if (m_StringSelectPosition >= m_Text.Length) return;
            int end = word ? NextWordEnd(m_StringSelectPosition) : m_StringSelectPosition + 1;
            if (!word && end < m_Text.Length && char.IsLowSurrogate(m_Text[end])) end++;
            m_Text = m_Text.Remove(m_StringSelectPosition, end - m_StringSelectPosition);
            SendOnValueChanged();
            UpdateLabel();
        }

        void DeleteSelection()
        {
            if (m_ReadOnly || !HasSelection) return;
            DeleteSelectionInternal();
            SendOnValueChanged();
            UpdateLabel();
        }

        void DeleteSelectionInternal()
        {
            int start = SelectionStart, end = SelectionEnd;
            m_Text = m_Text.Remove(start, end - start);
            m_StringPosition = m_StringSelectPosition = start;
            m_IsSelectAll = false;
        }

        // The selection runs from the end (its anchor) to the caret at the start (measured on the
        // shipped package).
        protected void SelectAll()
        {
            m_LagAnchor = selectionAnchorPosition;
            m_LagFocus = caretPosition;
            m_IsSelectAll = true;
            m_StringPosition = m_Text.Length;
            m_StringSelectPosition = 0;
            m_LagFrame = Time.frameCount;
            m_LagStringAnchor = m_StringPosition;
            m_LagStringFocus = m_StringSelectPosition;
            MarkGeometryChanged();
        }

        public void MoveTextStart(bool shift)
        {
            m_StringSelectPosition = 0;
            if (!shift) m_StringPosition = 0;
            MarkGeometryChanged();
        }

        // To the last laid-out character: the end marker, or where a truncated text stopped
        // (measured: the shipped package's End there gives the stopped-at character's index).
        public void MoveTextEnd(bool shift)
        {
            var info = CurrentTextInfo();
            m_StringSelectPosition = info != null && info.characterCount > 0
                ? Mathf.Clamp(info.characterInfo[info.characterCount - 1].index, 0, m_Text.Length)
                : m_Text.Length;
            if (!shift) m_StringPosition = m_StringSelectPosition;
            MarkGeometryChanged();
        }

        // The End key: to the end of the caret's line as laid out, before its last character
        // (the line feed, or the white space a wrapped line ends in). Measured: in "ab\ncd" End
        // from 0 stops after "ab", also in a single-line field; in a 64-wide multi-line field
        // "a\tb" wraps after the tab, and End from 0 stops after "a". On the last line it is
        // MoveTextEnd.
        void MoveToEndOfLaidOutLine(bool shift)
        {
            var info = CurrentTextInfo();
            if (info == null || info.characterCount == 0 || info.lineInfo == null)
            {
                MoveTextEnd(shift);
                return;
            }
            int ci = Mathf.Clamp(CharacterIndexOf(m_StringSelectPosition), 0, info.characterCount - 1);
            int line = info.characterInfo[ci].lineNumber;
            if (line < 0 || line >= info.lineCount - 1 || line >= info.lineInfo.Length)
            {
                MoveTextEnd(shift);
                return;
            }
            int last = Mathf.Clamp(info.lineInfo[line].lastCharacterIndex, ci, info.characterCount - 1);
            m_StringSelectPosition = Mathf.Clamp(info.characterInfo[last].index, 0, m_Text.Length);
            if (!shift) m_StringPosition = m_StringSelectPosition;
            MarkGeometryChanged();
        }

        public void MoveToStartOfLine(bool shift, bool ctrl)
        {
            if (ctrl)
            {
                MoveTextStart(shift);
                return;
            }
            int line = m_Text.LastIndexOf('\n', Math.Max(0, m_StringSelectPosition - 1));
            m_StringSelectPosition = line < 0 || m_StringSelectPosition == 0 ? 0 : line + 1;
            if (!shift) m_StringPosition = m_StringSelectPosition;
            MarkGeometryChanged();
        }

        public void MoveToEndOfLine(bool shift, bool ctrl)
        {
            if (ctrl)
            {
                MoveTextEnd(shift);
                return;
            }
            int line = m_Text.IndexOf('\n', m_StringSelectPosition);
            m_StringSelectPosition = line < 0 ? m_Text.Length : line;
            if (!shift) m_StringPosition = m_StringSelectPosition;
            MarkGeometryChanged();
        }

        void MoveLeft(bool shift, bool ctrl)
        {
            if (HasSelection && !shift)
            {
                m_StringPosition = m_StringSelectPosition = SelectionStart;
                MarkGeometryChanged();
                return;
            }
            // A step moves to the laid-out character before the caret's, where its source begins
            // (over the tags between them).
            int p;
            if (ctrl) p = FindPrevWordBegin();
            else if (CurrentTextInfo() is { characterCount: > 0 })
            {
                int k = CharacterIndexOf(m_StringSelectPosition);
                p = k > 0 ? StringIndexOf(k - 1) : 0;
            }
            else
            {
                p = m_StringSelectPosition - 1;
                if (p > 0 && p < m_Text.Length && char.IsLowSurrogate(m_Text[p])) p--;
            }
            m_StringSelectPosition = Mathf.Clamp(p, 0, m_StringSelectPosition);
            if (!shift) m_StringPosition = m_StringSelectPosition;
            MarkGeometryChanged();
        }

        void MoveRight(bool shift, bool ctrl)
        {
            if (HasSelection && !shift)
            {
                m_StringPosition = m_StringSelectPosition = SelectionEnd;
                MarkGeometryChanged();
                return;
            }
            // A step moves past the source of the caret's laid-out character (before the tags
            // that follow it).
            int p;
            if (ctrl) p = NextWordEnd(m_StringSelectPosition);
            else if (CurrentTextInfo() is { characterCount: > 0 } info)
            {
                int k = CharacterIndexOf(m_StringSelectPosition);
                ref var c = ref info.characterInfo[k];
                p = k < info.characterCount - 1 ? c.index + Math.Max(1, c.stringLength) : m_Text.Length;
            }
            else
            {
                p = m_StringSelectPosition + 1;
                if (p < m_Text.Length && char.IsLowSurrogate(m_Text[p])) p++;
            }
            m_StringSelectPosition = Mathf.Clamp(p, m_StringSelectPosition, m_Text.Length);
            if (!shift) m_StringPosition = m_StringSelectPosition;
            MarkGeometryChanged();
        }

        void MoveUp(bool shift) => MoveVertical(shift, -1);

        void MoveDown(bool shift) => MoveVertical(shift, 1);

        // Moves the caret to the nearest character of the line above or below.
        void MoveVertical(bool shift, int direction)
        {
            if (!multiLine || m_TextComponent == null)
            {
                if (direction < 0) MoveTextStart(shift);
                else MoveTextEnd(shift);
                return;
            }
            var info = CurrentTextInfo();
            if (info.characterCount == 0) return;
            int ci = Mathf.Clamp(CharacterIndexOf(m_StringSelectPosition), 0, info.characterCount - 1);
            int line = info.characterInfo[ci].lineNumber + direction;
            if (line < 0) { MoveTextStart(shift); return; }
            if (line >= info.lineCount) { MoveTextEnd(shift); return; }
            float x = info.characterInfo[ci].origin;
            int target = TMP_TextUtilities.FindNearestCharacterOnLine(m_TextComponent, new Vector3(x, 0f, 0f), line);
            if (target < 0) return;
            m_StringSelectPosition = Mathf.Clamp(info.characterInfo[target].index, 0, m_Text.Length);
            if (!shift) m_StringPosition = m_StringSelectPosition;
            MarkGeometryChanged();
        }

        // The string index where the word before the caret begins (Text Input Hotkeys deletes
        // back to it on Ctrl+Backspace).
        int FindPrevWordBegin()
        {
            int p = m_StringSelectPosition - 1;
            while (p > 0 && !char.IsLetterOrDigit(m_Text[p])) p--;
            while (p > 0 && char.IsLetterOrDigit(m_Text[p - 1])) p--;
            return Math.Max(0, p);
        }

        int NextWordEnd(int pos)
        {
            int p = pos;
            while (p < m_Text.Length && !char.IsLetterOrDigit(m_Text[p])) p++;
            while (p < m_Text.Length && char.IsLetterOrDigit(m_Text[p])) p++;
            return p;
        }

        // ----- Events -----

        // The text component's vertices are dirty. A text written into it from outside, on an
        // enabled field, is put back at once to the field's label, without an event (measured:
        // textComponent.text = "507.2" reads back as the field's text and U+200B).
        void OnTextComponentDirty()
        {
            MarkGeometryChanged();
            if (m_PreventCallback || m_DeferLabel || m_LabelShown == null || m_TextComponent == null) return;
            if (!string.Equals(m_TextComponent.text, m_LabelShown, StringComparison.Ordinal)) UpdateLabel(false);
        }

        void SendOnValueChanged()
        {
            m_TextChangedSinceActivation = true;
            if (m_PreventCallback) return;
            m_OnValueChanged?.Invoke(m_Text);
        }

        protected void SendOnEndEdit() => m_OnEndEdit?.Invoke(m_Text);

        protected void SendOnSubmit() => m_OnSubmit?.Invoke(m_Text);

        // ----- Display -----

        // Shows the text (as asterisks for passwords; tags shown literally unless rich-text
        // editing is allowed) with the end marker, and the placeholder when it is empty. The
        // IME's composition shows at the caret, underlined with rich text, after the selection
        // it replaces is deleted (without an event), as shipped.
        protected void UpdateLabel() => UpdateLabel(true);

        // The text the IME is composing for this field: none unless it is focused and editable.
        string Composition
        {
            get
            {
                if (m_ReadOnly || !m_AllowInput) return "";
                var module = EventSystem.current != null ? EventSystem.current.currentInputModule : null;
                return (module != null && module.input != null ? module.input.compositionString : Input.compositionString) ?? "";
            }
        }

        // layout false leaves the laying out to the canvas rebuild.
        void UpdateLabel(bool layout)
        {
            if (m_TextComponent == null || m_TextComponent.font == null || m_PreventCallback || m_DeferLabel) return;
            m_PreventCallback = true;
            string composing = Composition;
            if (composing.Length > 0 && HasSelection) DeleteSelectionInternal();
            m_LabelText = m_Text;
            m_LabelComposition = composing;
            string full = m_Text;
            if (composing.Length > 0)
            {
                int at = Mathf.Clamp(m_StringPosition, 0, m_Text.Length);
                full = m_Text.Substring(0, at) + (m_RichText ? "<u>" + composing + "</u>" : composing) + m_Text.Substring(at);
            }
            else m_PlaceIme = true;
            string shown;
            if (m_InputType == InputType.Password) shown = new string(m_AsteriskChar, full.Length);
            else shown = full;
            bool empty = string.IsNullOrEmpty(full);
            if (m_Placeholder != null) m_Placeholder.enabled = empty;
            m_LabelShown = shown + EndMarker;
            m_TextComponent.text = m_LabelShown;
            // The text is laid out at once, so that the caret's character indices follow the
            // change (measured: the shipped package lays it out once per typed character, and
            // not again in the canvas rebuild).
            if (layout) m_TextComponent.ForceMeshUpdate();
            m_PreventCallback = false;
            MarkGeometryChanged();
        }

        public void ForceLabelUpdate() => UpdateLabel();

        void SetTextComponentWrapMode()
        {
            if (m_TextComponent == null) return;
            m_TextComponent.textWrappingMode = multiLine ? TextWrappingModes.Normal : TextWrappingModes.PreserveWhitespaceNoWrap;
        }

        void SetTextComponentRichTextMode()
        {
            if (m_TextComponent == null) return;
            m_TextComponent.richText = m_RichText;
        }

        public void SetGlobalPointSize(float pointSize)
        {
            if (m_Placeholder is TMP_Text placeholderText) placeholderText.fontSize = pointSize;
            if (m_TextComponent != null) m_TextComponent.fontSize = pointSize;
        }

        public void SetGlobalFontAsset(TMP_FontAsset fontAsset)
        {
            if (fontAsset == null) return;
            if (m_Placeholder is TMP_Text placeholderText) placeholderText.font = fontAsset;
            if (m_TextComponent != null) m_TextComponent.font = fontAsset;
        }

        void EnforceContentType()
        {
            switch (m_ContentType)
            {
                case ContentType.Standard:
                    m_InputType = InputType.Standard;
                    m_KeyboardType = TouchScreenKeyboardType.Default;
                    m_CharacterValidation = CharacterValidation.None;
                    break;
                case ContentType.Autocorrected:
                    m_InputType = InputType.AutoCorrect;
                    m_KeyboardType = TouchScreenKeyboardType.Default;
                    m_CharacterValidation = CharacterValidation.None;
                    break;
                case ContentType.IntegerNumber:
                    m_LineType = LineType.SingleLine;
                    m_InputType = InputType.Standard;
                    m_KeyboardType = TouchScreenKeyboardType.NumberPad;
                    m_CharacterValidation = CharacterValidation.Integer;
                    break;
                case ContentType.DecimalNumber:
                    m_LineType = LineType.SingleLine;
                    m_InputType = InputType.Standard;
                    m_KeyboardType = TouchScreenKeyboardType.NumbersAndPunctuation;
                    m_CharacterValidation = CharacterValidation.Decimal;
                    break;
                case ContentType.Alphanumeric:
                    m_LineType = LineType.SingleLine;
                    m_InputType = InputType.Standard;
                    m_KeyboardType = TouchScreenKeyboardType.ASCIICapable;
                    m_CharacterValidation = CharacterValidation.Alphanumeric;
                    break;
                case ContentType.Name:
                    m_LineType = LineType.SingleLine;
                    m_InputType = InputType.Standard;
                    m_KeyboardType = TouchScreenKeyboardType.Default;
                    m_CharacterValidation = CharacterValidation.Name;
                    break;
                case ContentType.EmailAddress:
                    m_LineType = LineType.SingleLine;
                    m_InputType = InputType.Standard;
                    m_KeyboardType = TouchScreenKeyboardType.EmailAddress;
                    m_CharacterValidation = CharacterValidation.EmailAddress;
                    break;
                case ContentType.Password:
                    m_LineType = LineType.SingleLine;
                    m_InputType = InputType.Password;
                    m_KeyboardType = TouchScreenKeyboardType.Default;
                    m_CharacterValidation = CharacterValidation.None;
                    break;
                case ContentType.Pin:
                    m_LineType = LineType.SingleLine;
                    m_InputType = InputType.Password;
                    m_KeyboardType = TouchScreenKeyboardType.NumberPad;
                    m_CharacterValidation = CharacterValidation.Digit;
                    break;
            }
            SetTextComponentWrapMode();
        }

        void SetToCustomIfContentTypeIsNot(params ContentType[] allowed)
        {
            if (m_ContentType == ContentType.Custom) return;
            foreach (var t in allowed)
                if (m_ContentType == t) return;
            m_ContentType = ContentType.Custom;
        }

        void SetToCustom()
        {
            if (m_ContentType == ContentType.Custom) return;
            m_ContentType = ContentType.Custom;
        }

        void SetToCustom(CharacterValidation validation)
        {
            if (m_ContentType == ContentType.Custom)
            {
                m_CharacterValidation = validation;
                return;
            }
            m_ContentType = ContentType.Custom;
            m_CharacterValidation = validation;
        }

        // ----- Caret geometry -----

        void UpdateBlink()
        {
            if (m_CaretBlinkRate <= 0f)
            {
                if (!m_CaretVisible) { m_CaretVisible = true; MarkGeometryChanged(); }
                return;
            }
            float period = 1f / m_CaretBlinkRate;
            bool visible = (Time.unscaledTime - m_BlinkStart) % period < period / 2f;
            if (visible == m_CaretVisible) return;
            m_CaretVisible = visible;
            if (!HasSelection) MarkGeometryChanged();
        }

        // The caret is redrawn in the next graphic rebuild (a change made during one waits
        // for the next).
        void MarkGeometryChanged()
        {
            if (!IsActive()) return;
            CanvasUpdateRegistry.TryRegisterCanvasElementForGraphicRebuild(this);
        }

        public virtual void Rebuild(CanvasUpdate update)
        {
            if (update == CanvasUpdate.LatePreRender) UpdateCaretGeometry();
        }

        public virtual void LayoutComplete() { }

        public virtual void GraphicUpdateComplete() { }

        // The caret (a quad caretWidth wide over the line at the caret) or the selection (a quad
        // per line it covers), written to the caret graphic's canvas renderer. With no caret
        // graphic, nothing is drawn.
        void UpdateCaretGeometry()
        {
            if (m_Caret == null) return;
            var vh = new VertexHelper();
            if (isFocused && m_TextComponent != null && m_TextComponent.textInfo != null)
            {
                // The text laid out for the current content places the caret: after the caret or
                // the text changed, the text moves to keep the caret in view, and the caret's rect
                // follows it. A redraw for the blink alone leaves a scrolled text where it is.
                if (!m_TextComponent.havePropertiesChanged && (m_ScrolledForPosition != m_StringSelectPosition || !ReferenceEquals(m_ScrolledForLabel, m_LabelShown)))
                {
                    m_ScrolledForPosition = m_StringSelectPosition;
                    m_ScrolledForLabel = m_LabelShown;
                    if (KeepCaretVisible())
                    {
                        AssignPositioningIfNeeded();
                        UpdateScrollbar();
                    }
                }
                if (HasSelection) GenerateSelection(vh);
                else if (m_CaretVisible) GenerateCaret(vh);
            }
            var target = mesh;
            vh.FillMesh(target);
            vh.Dispose();
            m_Caret.canvasRenderer.SetMesh(target);
        }

        // Local position of the caret at string position `pos` (in the text's space): at the
        // origin of its laid-out character, as tall as the character before it on the same line
        // (or its own at a line's start), from that character's ascender to its descender
        // (measured on the shipped package).
        void CaretGeometry(int pos, out float x, out float top, out float bottom)
        {
            var info = m_TextComponent.textInfo;
            x = 0f;
            top = bottom = 0f;
            if (info.characterCount == 0)
            {
                var rect = m_TextComponent.rectTransform.rect;
                x = rect.xMin;
                top = rect.yMax;
                bottom = rect.yMin;
                return;
            }
            int index = Mathf.Clamp(CharacterIndexOf(pos), 0, info.characterCount - 1);
            ref var c = ref info.characterInfo[index];
            x = c.origin;
            ref var height = ref c;
            if (index > 0 && info.characterInfo[index - 1].lineNumber == c.lineNumber) height = ref info.characterInfo[index - 1];
            top = height.ascender;
            bottom = height.descender;
        }

        void GenerateCaret(VertexHelper vh)
        {
            CaretGeometry(CaretLabelPosition, out float x, out float top, out float bottom);
            float width = Mathf.Max(1, m_CaretWidth);
            Color32 color = caretColor;
            AddQuad(vh, x, bottom, x + width, top, color);
            PlaceIme(x, bottom);
        }

        // The caret's position in the label: after the composition while there is one.
        int CaretLabelPosition => m_LabelComposition.Length == 0
            ? m_StringSelectPosition
            : Mathf.Clamp(m_StringPosition, 0, m_Text.Length) + (m_RichText ? 3 : 0) + m_LabelComposition.Length;

        // Input.compositionCursorPos at the caret's bottom left, in screen pixels from the top
        // (shipped: the caret's first vertex, through its renderer's transform and the canvas's
        // camera, y flipped), where the IME places its windows; set through the input module's
        // BaseInput when there is one, as shipped.
        void PlaceIme(float x, float bottom)
        {
            var info = m_TextComponent.textInfo;
            int line = info.characterCount > 0 ? info.characterInfo[Mathf.Clamp(CharacterIndexOf(CaretLabelPosition), 0, info.characterCount - 1)].lineNumber : 0;
            if (!m_PlaceIme && line == m_ImeLine) return;
            m_PlaceIme = false;
            m_ImeLine = line;
            var canvas = m_TextComponent.canvas;
            if (canvas == null) return;
            Camera camera = null;
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                camera = canvas.worldCamera;
                if (camera == null) camera = Camera.current;
            }
            Transform space = caretRectTrans != null ? caretRectTrans : m_TextComponent.rectTransform;
            Vector2 at = RectTransformUtility.WorldToScreenPoint(camera, space.TransformPoint(new Vector3(x, bottom, 0f)));
            at.y = Screen.height - at.y;
            var module = EventSystem.current != null ? EventSystem.current.currentInputModule : null;
            if (module != null && module.input != null) module.input.compositionCursorPos = at;
        }

        void GenerateSelection(VertexHelper vh)
        {
            var info = m_TextComponent.textInfo;
            int start = Mathf.Clamp(CharacterIndexOf(SelectionStart), 0, Math.Max(0, info.characterCount - 1));
            int end = Mathf.Clamp(CharacterIndexOf(SelectionEnd), 0, Math.Max(0, info.characterCount - 1));
            if (info.characterCount == 0) return;
            Color32 color = m_SelectionColor;
            int i = start;
            while (i < end)
            {
                int line = info.characterInfo[i].lineNumber;
                float x0 = info.characterInfo[i].origin;
                int j = i;
                while (j + 1 < end && info.characterInfo[j + 1].lineNumber == line) j++;
                float x1 = info.characterInfo[j].xAdvance;
                ref var li = ref info.lineInfo[line];
                AddQuad(vh, x0, li.descender, x1, li.ascender, color, true);
                i = j + 1;
            }
        }

        // A caret quad's corners go bottom left, top left, top right, bottom right; a selection
        // quad's bottom left, bottom right, top right, top left (measured in m_Mesh).
        static void AddQuad(VertexHelper vh, float x0, float y0, float x1, float y1, Color32 color, bool selection = false)
        {
            int v = vh.currentVertCount;
            vh.AddVert(new Vector3(x0, y0, 0f), color, Vector2.zero);
            vh.AddVert(selection ? new Vector3(x1, y0, 0f) : new Vector3(x0, y1, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(x1, y1, 0f), color, Vector2.zero);
            vh.AddVert(selection ? new Vector3(x0, y1, 0f) : new Vector3(x1, y0, 0f), color, Vector2.zero);
            vh.AddTriangle(v, v + 1, v + 2);
            vh.AddTriangle(v + 2, v + 3, v);
        }

        // Moves the text (horizontally for a single line, vertically for multi-line) so that the
        // caret is inside the viewport. True when it moved. The caret's geometry is in the
        // text's local space, drawn in the caret's rect, which follows the text's.
        bool KeepCaretVisible()
        {
            var viewport = m_TextViewport;
            if (viewport == null || m_TextComponent == null) return false;
            var info = m_TextComponent.textInfo;
            if (info.characterCount == 0) return false;
            CaretGeometry(CaretLabelPosition, out float x, out float top, out float bottom);
            var textRect = m_TextComponent.rectTransform;
            var view = viewport.rect;
            if (!multiLine)
            {
                Vector3 caretInView = viewport.InverseTransformPoint(textRect.TransformPoint(new Vector3(x, 0f, 0f)));
                float shift = 0f;
                if (caretInView.x > view.xMax) shift = view.xMax - caretInView.x - m_CaretWidth;
                else if (caretInView.x < view.xMin) shift = view.xMin - caretInView.x;
                if (shift == 0f) return false;
                textRect.anchoredPosition += new Vector2(shift, 0f);
                return true;
            }
            float topInView = viewport.InverseTransformPoint(textRect.TransformPoint(new Vector3(x, top, 0f))).y;
            float bottomInView = viewport.InverseTransformPoint(textRect.TransformPoint(new Vector3(x, bottom, 0f))).y;
            float rise = 0f;
            if (bottomInView < view.yMin) rise = view.yMin - bottomInView;
            else if (topInView > view.yMax) rise = view.yMax - topInView;
            if (rise == 0f) return false;
            textRect.anchoredPosition += new Vector2(0f, rise);
            return true;
        }

        // The multi-line text's height beyond the viewport's: how far it can scroll.
        float ScrollableHeight()
        {
            if (m_TextComponent == null || m_TextViewport == null) return 0f;
            var info = m_TextComponent.textInfo;
            if (info == null || info.lineCount == 0) return 0f;
            float height = info.lineInfo[0].ascender - info.lineInfo[info.lineCount - 1].descender;
            return Mathf.Max(0f, height - m_TextViewport.rect.height);
        }

        // The vertical scrollbar's value moved: the text goes to that scroll (0 its start, 1 its
        // end) and the caret's rect follows (measured: in the change's handler).
        void OnScrollbarValueChange(float value)
        {
            if (m_TextComponent == null || !multiLine) return;
            var textRect = m_TextComponent.rectTransform;
            textRect.anchoredPosition = new Vector2(textRect.anchoredPosition.x, value * ScrollableHeight());
            AssignPositioningIfNeeded();
            MarkGeometryChanged();
        }

        // Shows the text's scroll on the vertical scrollbar, without sending its change.
        void UpdateScrollbar()
        {
            if (m_VerticalScrollbar == null || m_TextComponent == null || !multiLine) return;
            float scrollable = ScrollableHeight();
            float view = m_TextViewport != null ? m_TextViewport.rect.height : 0f;
            m_VerticalScrollbar.size = scrollable > 0f ? view / (view + scrollable) : 1f;
            m_VerticalScrollbar.SetValueWithoutNotify(scrollable > 0f ? Mathf.Clamp01(m_TextComponent.rectTransform.anchoredPosition.y / scrollable) : 0f);
        }

        // The string position of the character under a screen point.
        int PositionFromScreen(Vector2 screen, Camera camera)
        {
            if (m_TextComponent == null) return -1;
            if (!TMP_TextUtilities.ScreenToLocal(m_TextComponent, screen, camera, out var local)) return -1;
            var info = m_TextComponent.textInfo;
            if (info.characterCount == 0) return 0;
            int line = 0;
            for (int k = 0; k < info.lineCount; k++)
                if (local.y <= info.lineInfo[k].ascender) line = k;
            int nearest = TMP_TextUtilities.FindNearestCharacterOnLine(m_TextComponent, local, line);
            if (nearest < 0) return m_Text.Length;
            ref var c = ref info.characterInfo[nearest];
            int index = c.index;
            if (local.x > (c.origin + c.xAdvance) * 0.5f) index += Math.Max(1, c.stringLength);
            return Mathf.Clamp(index, 0, m_Text.Length);
        }

        // ----- Layout element -----

        public virtual void CalculateLayoutInputHorizontal() { }

        public virtual void CalculateLayoutInputVertical() { }

        public virtual float minWidth => 0f;

        public virtual float preferredWidth
        {
            get
            {
                if (m_TextComponent == null) return 0f;
                return m_TextComponent.preferredWidth + Padding().x;
            }
        }

        public virtual float flexibleWidth => -1f;

        public virtual float minHeight => 0f;

        public virtual float preferredHeight
        {
            get
            {
                if (m_TextComponent == null) return 0f;
                return m_TextComponent.preferredHeight + Padding().y;
            }
        }

        public virtual float flexibleHeight => -1f;

        public virtual int layoutPriority => 1;

        // The viewport's own offsets from its anchors (offsetMin - offsetMax); 0 without a
        // viewport. Neither the field's rect nor the text's enters the layout values, so laying
        // the field out cannot change its own preferred size.
        Vector2 Padding()
        {
            if (m_TextViewport == null) return Vector2.zero;
            return m_TextViewport.offsetMin - m_TextViewport.offsetMax;
        }
    }
}
