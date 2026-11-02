# TextMeshPro (Unity.TextMeshPro): functional specification

This project's replacement of TextMeshPro, the assembly `Unity.TextMeshPro`
(`src/Unity.TextMeshPro`, namespace `TMPro`). Every text in the game is a TextMeshPro text:
Klei's `LocText` derives from `TextMeshProUGUI`. The shipped package is a build modified by
Klei: the game calls a Klei addition, `TextMeshProUGUI.KForceUpdateDirty()`. The rules are in
`docs/cleanroom/README.md`, and PLAN M9 describes the stage.

## Interface

**Generated shapes:** `src/Unity.TextMeshPro/Generated/` holds the type declarations, enums,
interfaces, delegates and serialized fields. `scripts/regen-packages.sh` regenerates them from
the shipped package's metadata. Do not edit them; write the rest in partial declarations.

**What the data contains:** the serialized fields are how the game's data stores its assets.
`out/data-scripts.tsv` lists them: 29 `TMP_FontAsset`, 13 `TMP_SpriteAsset`, 4
`TMP_ColorGradient`, one `TMP_Settings`, one `TMP_StyleSheet`, 45 `TextMeshProUGUI`, 16
`TMP_SubMeshUI`, 5 `TMP_InputField` and 2 `TMP_Dropdown`. Thousands more `LocText`
components carry `TextMeshProUGUI`'s fields.

**Required members:** `out/surface/package-members.tsv`, the `Unity.TextMeshPro` rows, lists
what the game calls and overrides.

`LocText`:
- hides `Awake`, `Start` and `OnDestroy` with its own methods that call the base ones;
- overrides `text` (get and set), `SetText(string)`, `OnEnable` and the protected
  `GenerateTextMesh()`;
- calls `KForceUpdateDirty()`.

`KInputTextField` derives from `TMP_InputField` and overrides `LateUpdate`. Two classes derive
from `TMP_InputValidator`.

The game also:
- reads `textInfo`: characters, lines, words, links and mesh info;
- uses `TMP_TextUtilities.FindIntersectingLink/Character/Word/Line` and
  `IsIntersectingRectTransform` for clickable links;
- edits `meshInfo` vertex data and calls `UpdateVertexData` and `UpdateGeometry`;
- adds styles at run time (`TMP_StyleSheet.instance.AddStyle(new TMP_Style(name, open, close))`
  then `RefreshStyles()`);
- swaps font assets for localization;
- creates a dynamic font asset (`TMP_FontAsset.CreateFontAsset(Font, …)`).

`scripts/build-packages.sh` runs ApiCheck for the game's references; it must report nothing
missing.

**The engine:** it is this project's own code (`src/OniMoono.UnityEngine`) and gives
`Unity.TextMeshPro` access to its internals. The parts used here:
- **`UnityEngine.TextCore`:** `FaceInfo`, `Glyph`, `GlyphRect`, `GlyphMetrics`, and
  `TextCore.LowLevel.FontEngine`, a FreeType-backed font engine that loads font files,
  renders glyphs to atlases in SDF and bitmap modes, and returns the OpenType adjustment
  tables;
- **meshes, materials and shader property IDs;**
- **`CanvasRenderer`.**

The shaders, such as `TextMeshPro/Distance Field` and `TextMeshPro/Sprite`, are the game's own
assets. They define what the vertex channels and material properties mean.

**uGUI:** this package builds on uGUI (`MaskableGraphic`, `CanvasUpdateRegistry`,
`LayoutUtility`, `ILayoutElement`, `Selectable`, the event system). Until this project's uGUI
is finished, compile against the shipped `UnityEngine.UI.dll` as a metadata reference only.
Use only uGUI's public and protected API.

## Behaviour

These follow the TextMeshPro documentation: the user guide, the rich-text tags reference and
the scripting API. What the game relies on is spelled out below.

### Assets

**`TMP_Asset`:** `hashCode`, `material`, `materialHashCode` and `version`.

**`TMP_FontAsset`:**
- **Contents:**
  - `faceInfo`;
  - `glyphTable` and `characterTable` (`TMP_Character`: unicode, glyph index, scale);
  - `atlasTextures`, `atlasWidth`, `atlasHeight`, `atlasPadding`, `atlasRenderMode`;
  - `material`, `fallbackFontAssetTable`, `fontFeatureTable` (the glyph-pair adjustment
    records used for kerning), `atlasPopulationMode` (Static or Dynamic) and
    `creationSettings`;
  - the style weights: `normalStyle`, `normalSpacingOffset`, `boldStyle`,
    `boldSpacing`, `italicStyle`, `tabSize`;
  - the font weight table.
- **Lookup tables:** loaded assets build their lookups on first use, from `characterTable`
  by unicode to glyph.
- **Fallbacks:** a character missing from an asset is looked up in its fallback list
  (depth-first, each asset once). Then the global fallbacks (`TMP_Settings.fallbackFontAssets`)
  are tried, then the default font asset. A dynamic asset adds missing characters by rendering
  them into its atlas through the engine's `FontEngine` before it falls back. A character that
  cannot be found anywhere becomes the missing-glyph character (`TMP_Settings.missingGlyphCharacter`,
  `□` by default) of the primary asset, with a warning when `TMP_Settings.warningsDisabled` is
  false.
- **`CreateFontAsset(Font, samplingPointSize, atlasPadding, renderMode, atlasWidth,
  atlasHeight[, populationMode, enableMultiAtlas])`:**
  - It creates a dynamic asset over the font file, with an empty atlas texture (Alpha8) and a
    material on `TextMeshPro/Distance Field` (SDF modes) or the bitmap shader.
  - Characters are added on demand.
  - `TryAddCharacters` and `HasCharacter` are public.

**`TMP_SpriteAsset`:** a sprite sheet with `spriteCharacterTable` and `spriteGlyphTable`,
`spriteSheet` and `material`. Sprites are looked up by name, by index, or by the unicode of a
sprite character. Fallback sprite assets apply.

**`TMP_StyleSheet` and `TMP_Style`:**
- Named styles with an opening and a closing definition, which are rich text inserted for
  `<style="name">` … `</style>`.
- `instance` is the default sheet from `TMP_Settings`.
- `GetStyle(hash or name)`, `AddStyle`, `RefreshStyles()` (rebuilds the lookup).
- `TMP_Style(name, opening, closing)`.

**`TMP_Settings`:**
- The project asset, found through `Resources.Load` at `TMP Settings`.
- **Contents:** `defaultFontAsset`, `fallbackFontAssets`, `defaultSpriteAsset`,
  `defaultStyleSheet`, `defaultFontSize`, `enableWordWrapping`, `enableKerning`,
  `enableExtraPadding`, `enableTintAllSprites`, `enableParseEscapeCharacters`,
  `missingGlyphCharacter`, `warningsDisabled` and `useModernHangulLineBreakingRules`.
- **Line breaking rules:** the leading and following characters, from text assets.
- Read the asset's serialized fields; the game's data holds the one used.

**`TMP_ColorGradient`:** four corner colours and a mode (Single, HorizontalGradient,
VerticalGradient, FourCornersGradient).

**`ShaderUtilities`:**
- The shader property IDs as public static ints, among them `ID_GlowPower`, which the game
  reads, `ID_FaceColor`, `ID_OutlineColor`, `ID_OutlineWidth`, `ID_GradientScale`,
  `ID_ScaleRatio_A`/`B`/`C`, `ID_WeightNormal`, `ID_WeightBold`, `ID_ClipRect`,
  `ID_StencilID` and `ID_StencilComp`.
- `GetPadding(material, enableExtraPadding, isBold)`: the extra pixels the material's outline,
  dilate, glow and underlay need around a glyph, in atlas units.
- `UpdateShaderRatios(material)`, `IsMaskingEnabled` and `GetFontStyle`.

### Text layout (`TMP_Text`)

`TMP_Text` (a `MaskableGraphic`) holds the text and its settings, and lays the text out into
`textInfo` and a mesh. `TextMeshProUGUI` draws through its `CanvasRenderer`, with sub-meshes
(`TMP_SubMeshUI` children) for characters from other materials: fallback fonts and sprites.
`TextMeshPro` draws through a `MeshRenderer` in the world; the game references only its
transform.

**Settings:**
- **Content and fonts:**
  - `text`, `SetText(string)`, `SetText(format, args…)`, which replaces `{0}`, `{1}`,
    `{0:2}`-style placeholders with float arguments, and `SetCharArray`;
  - `font` (a `TMP_FontAsset`), `fontSharedMaterial`, `fontMaterial` (an instance) and
    `fontSize`.
- **Auto-size:** `enableAutoSizing`, `fontSizeMin`, `fontSizeMax`, `characterWidthAdjustment`
  and `lineSpacingAdjustment`.
- **Style and colour:**
  - `fontStyle` (FontStyles flags: Bold, Italic, Underline, LowerCase, UpperCase, SmallCaps,
    Strikethrough, Superscript, Subscript, Highlight) and `fontWeight`;
  - `color`, `alpha`, `enableVertexGradient`, `colorGradient` and `overrideColorTags`.
- **Spacing and placement:**
  - `characterSpacing`, `wordSpacing`, `lineSpacing` and `paragraphSpacing`;
  - `alignment` (TextAlignmentOptions);
  - `textWrappingMode` (and the older `enableWordWrapping`) with `wordWrappingRatios`;
  - `overflowMode`: Overflow, Ellipsis, Masking, Truncate, ScrollRect, Page or Linked;
  - `margin`.
- **Rendering options:** `enableKerning`, `extraPadding`, `richText`, `isRightToLeftText`,
  `isOrthographic`, `isOverlay` and `isTextObjectScaleStatic`.
- **Visibility:** `maxVisibleCharacters`, `maxVisibleWords`, `maxVisibleLines`,
  `firstVisibleCharacter` and `pageToDisplay`.
- `fontFeatures`, the OTL feature tags; `kern` is enabled with kerning.
- `autoSizeTextContainer`, and `TextContainer` (obsolete, but referenced).

**When it lays out:**
- A property change sets `havePropertiesChanged` and dirties the vertices and the layout.
- During the canvas graphic rebuild (`Rebuild(PreRender)`), a text with properties changed is
  laid out and its mesh regenerated by the protected virtual `GenerateTextMesh()`. LocText
  overrides that method.
- `ForceMeshUpdate(ignoreActiveState, forceTextReparsing)` does it immediately.
- The game's `KForceUpdateDirty()` marks the text as changed, so it regenerates in the next
  rebuild: `havePropertiesChanged` is set, and the vertices and layout are dirtied.
- After a layout, `havePropertiesChanged` is false, and `TMPro_EventManager.TEXT_CHANGED_EVENT`
  (a `FastAction<Object>`: `Add`, `Remove`, `Call`) is called with the text object.

**Parsing:**
- `richText` enables the documented tags:
  - weight and style: `<b>`, `<i>`, `<u>`, `<s>`, `<sup>`, `<sub>`, `<mark=…>`,
    `<lowercase>`, `<uppercase>`, `<smallcaps>`, `<font-weight>`;
  - fonts and colour: `<color=…>` (names and #RRGGBB[AA]), `<alpha>`, `<font="…">`,
    `<material>`, `<gradient>`;
  - size and spacing: `<size=…>` (px, %, em, ±), `<cspace>`, `<mspace>`, `<space>`,
    `<line-height>`, `<line-indent>`, `<indent>`, `<margin>`, `<pos>`, `<width>`;
  - placement: `<align>`, `<voffset>`, `<rotate>`;
  - other content: `<sprite …>`, `<link="…">`, `<nobr>`, `<noparse>`, `<style>`, `<br>`,
    `<page>`, `<zwsp>`.
- Closing tags pop the stacks. Unknown tags are drawn as text.
- Escape sequences (`\n`, `\t`, `\uXXXX`) are parsed when `TMP_Settings.enableParseEscapeCharacters`
  is set.
- UTF-16 surrogate pairs are one character.
- Measure what the game's texts use: the localization strings use links, colours, sizes, styles
  and sprites.

**Placing characters:**
- **Glyph scale:** the scale is `fontSize / faceInfo.pointSize × faceInfo.scale`, times the
  character's scale, with sub and superscript adjustments.
- **Advance:** each character advances by its glyph's horizontal advance, plus:
  - `characterSpacing` (in em/100), and `wordSpacing` for spaces;
  - the font's normal or bold spacing offset;
  - the kerning pair adjustment from `fontFeatureTable` when kerning is on.
- **Monospacing:** `<mspace>` centres each glyph in the fixed width instead.
- **Tabs:** they advance to the next multiple of `tabSize` × the face's `tabWidth`, scaled
  to the size, from the line's start (measured: "Further characters" under "Overflow at
  white space" below).
- **Lines:**
  - Lines break at `\n` and when a word overflows the width with wrapping on.
  - Words are separated by whitespace and the break opportunities of CJK characters. The
    line-breaking rules decide which characters cannot start or end a line. U+00A0 gives no
    break; U+2007 and U+202F do (measured: "Words and their separators" below, which also
    gives `textInfo`'s words).
  - A word wider than the line is broken at characters.
  - `<nobr>` prevents breaks.
- **Line height:** the line height is the font's line height, scaled to the size, plus
  `lineSpacing` (em/100). `paragraphSpacing` is added after `\n`.
- **Vertical alignment:** Top, Middle, Bottom, Baseline, Midline and Capline use the font's
  ascender, descender and cap height, as documented. Geometry alignment uses the mesh bounds.
- **Horizontal alignment:**
  - Left, Center, Right.
  - Justified: the spaces stretch, except on the last line.
  - Flush: every line stretches.
  - Each line's offset is computed from its width, or its visible width, as documented.
- **Overflow:**
  - Ellipsis: text that does not fit ends in "…" (U+2026) where it is cut.
  - Truncate: the rest is hidden.
  - Page: pages split at the height, and `pageToDisplay` picks one.
  - Masking: the material clips at the rect.
  - Linked: the overflow continues in `linkedTextComponent`.
  - `isTextOverflowing` and `firstOverflowCharacterIndex` report it.
- **Auto-size:** with `enableAutoSizing`, the size is searched between `fontSizeMin` and
  `fontSizeMax` for the largest one at which the text fits. Character width and line spacing
  are adjusted within their limits first, as documented. The size used is reported by
  `fontSize`.
- **Preferred sizes:**
  - `preferredWidth` and `preferredHeight` (and `GetPreferredValues(…)`) are the size of the
    laid-out text: without wrapping for the width, wrapped at the given width for the height.
    Margins are included. Both are taken to the next hundredth above, measured from the
    line's start rather than the rect (see "Preferred sizes to the hundredth" below).
  - They are the `ILayoutElement` values: minimum 0, flexible -1.
  - `renderedWidth` and `renderedHeight` are those of the visible characters.
  - `bounds` and `textBounds` are the mesh's bounds and the text's.

**The mesh**, per character quad (four vertices: bottom-left, top-left, top-right,
bottom-right):
- **Position:** the glyph's rect, with padding for the material (`ShaderUtilities.GetPadding`)
  and an italic shear of `italicStyle` (×0.01) per unit height.
- **uv0:**
  - xy: the glyph's atlas UVs, including the padding;
  - w: the SDF scale the shader expects, from `fontSize`, the canvas's lossy scale and the
    gradient scale. It is negative for bold, which the shader reads as a weight switch.
- **uv1** (`uvs2`): the face and outline texture coordinates, mapped over the line or the
  character as `horizontalMapping` and `verticalMapping` say. By default they are 0 to 1 over
  the character.
- **Colour:** the vertex colour, from `color`, colour tags, the vertex gradient and `alpha`,
  as Color32.
- **Normal and tangent:** (0, 0, −1) and (−1, 0, 0, 1).
- **Decorations:** underline and strikethrough are drawn with the font's underline glyph, as
  three quads per run. Highlight (`<mark>`) is quads behind the text.
- **Sprites** use the sprite asset's material, in a sub-mesh, tinted only with
  `tintAllSprites` or the tag's `tint=1`.

Use `--dump-ui-vertices` to measure exactly what the shipped package generates for a given
text, channel by channel, and compare it with yours. The shaders are the shipped ones, so the
channel meanings must match exactly.

**`textInfo` (`TMP_TextInfo`):**
- **Counts:** `characterCount`, `spriteCount`, `spaceCount`, `wordCount`, `linkCount`,
  `lineCount`, `pageCount` and `materialCount`.
- **Arrays:**
  - `characterInfo` (`TMP_CharacterInfo`): character, index, element type, material
    reference index, vertex index, the corners (bottomLeft, topLeft, topRight, bottomRight),
    origin, ascender, baseline, descender, xAdvance, aspect ratio, scale, colour, style,
    visibility, page and line numbers;
  - `wordInfo` (first and last character index, character count, `GetWord()`);
  - `lineInfo` (character counts, first and last character indices including the visible
    ones, the extents, ascender, baseline, descender, width, line height, alignment);
  - `linkInfo` (`GetLinkID()`, `GetLinkText()`, the character range);
  - `pageInfo`;
  - `meshInfo` (`TMP_MeshInfo` per material: `mesh`, `vertices`, `uvs0`, `uvs2`, `colors32`,
    `normals`, `tangents`, `triangles`, `vertexCount`, `SwapVertexData`, `SortGeometry`).
- **Copies:** `CopyMeshInfoVertexData()` returns copies.
- **Arrays grow** in the documented way. Entries beyond the counts are stale data, which
  callers must not read.

**After a layout:**
- **`UpdateVertexData([flags])`** pushes the `meshInfo` arrays back to the meshes and renderers.
  The game changes vertex colours and positions this way.
- **`UpdateGeometry(mesh, index)`** sets a mesh on the renderer of that index.

**Materials and masking (UGUI):**
- `fontSharedMaterial` is the material for rendering, with `GetModifiedMaterial` applying the
  stencil for `Mask` (stencil materials) as for any `MaskableGraphic`.
- Under a `RectMask2D`, the clip goes through `canvasRenderer.EnableRectClipping` and the
  sub-meshes follow. The `_ClipRect` and softness come from the canvas renderer.
- `TMP_SubMeshUI` children are created on demand, named after their material, hidden in the
  hierarchy as Unity does (`HideFlags`). They are reused, and disabled when unused.

**`TMP_TextUtilities`:**
- **`FindIntersectingCharacter(text, screenPos, camera, visibleOnly)`:** the index of the
  character whose quad contains the point, or -1.
- **`FindIntersectingWord`, `FindIntersectingLine`, `FindIntersectingLink`:** likewise for
  words, lines and links. The link test uses the link's character quads.
- **`IsIntersectingRectTransform(rect, position, camera)`.**
- The conversions use `RectTransformUtility` with the canvas's camera; null for overlay
  canvases.

### Input field and dropdown

**`TMP_InputField`:**
- **Settings:**
  - `textViewport`, `textComponent` (a TMP text), `placeholder` (a Graphic) and `text`;
  - `characterLimit`, `contentType`, `lineType`, `inputType`, `keyboardType`,
    `characterValidation` and `inputValidator` (a `TMP_InputValidator`, whose
    `Validate(ref text, ref pos, ch)` the game overrides);
  - `caretBlinkRate`, `caretWidth`, `selectionColor`, `readOnly`, `richText`,
    `onFocusSelectAll`, `resetOnDeActivation`, `restoreOriginalTextOnEscape`,
    `isRichTextEditingAllowed` and `pointSize`.
- **Events:** `onValueChanged`, `onEndEdit`, `onSubmit`, `onSelect`, `onDeselect`,
  `onTextSelection`, `onEndTextSelection`, `onTouchScreenKeyboardStatusChanged`, and the
  public `Action onFocus`, which the game assigns.
- **Activation:**
  - `ActivateInputField()` starts editing in the next `LateUpdate`, with focus and caret.
  - That start is a private instance method, `void ActivateInputFieldInternal()`, which mods
    patch by name (Favorite Buildings, to know when the player is typing). Measured in the
    original player:
    - After `ActivateInputField()`, it runs once in the same frame's `LateUpdate`, with
      `isFocused` still false; `isFocused` is true from the next frame.
    - Selecting the field (`EventSystem.SetSelectedGameObject`) with `shouldActivateOnSelect`
      on runs it the same way; with it off, selecting neither runs it nor focuses the field.
    - `ActivateInputField()` on a field already focused does not run it again.
    - `ActivateInputField()` while the GameObject is inactive does nothing: it does not run,
      not even once the object is active again, and the field stays unfocused.
  - `DeactivateInputField()` ends it. It calls `DeactivateInputField(bool clearSelection)` with
    false, so a patch of either overload sees every deactivation. Deselecting the field and
    disabling its GameObject both call `DeactivateInputField()`.
  - Deactivating a focused field that the event system has selected also deselects it, even
    with `clearSelection` false. Measured order: `DeactivateInputField()` →
    `DeactivateInputField(false)`, and within that the deselection runs `OnDeselect`, which calls
    `DeactivateInputField()` → `DeactivateInputField(false)` again before the outer call
    returns. Afterwards `currentSelectedGameObject` is no longer the field, so selecting it
    again activates it as above. On a field that is not focused (and not selected),
    `DeactivateInputField(true)` changes no selection and makes no nested call.
  - Clicking the field selects and activates it.
  - Deselecting it (`OnDeselect`) deactivates it.
  - **The IME mode** (measured 2026-10-29 in the original Windows player, issue #30: the
    duplicant rename field, `EditableTitleBar`, read with E2eDriver a frame at a time):
    activation sets `Input.imeCompositionMode` to `On`, from `Auto` or `Off`, by the first
    frame the field is focused; deactivating a focused field sets it to `Auto`, from `On` or
    `Off`. In between the field leaves it alone: set to `Auto` or `Off` while the field is
    focused, it stays so. Selecting a field that does not become focused (on a screen that
    is not shown) leaves it as it was.
  - While an IME composes (`Input.compositionString` not empty), the label shows the text
    with the composition inserted at the caret in `<u>…</u>` (measured: `<u>ni</u>​` for
    "ni" with the text selected), and `Input.compositionCursorPos` is set to a screen point at
    the caret. Our input reports no composition string, so this part is not implemented.
- **Editing:**
  - While focused, the field drains the keyboard events with `Event.PopEvent` in
    `OnUpdateSelected` (measured: not in `LateUpdate`), which the event system's input
    module sends to the selected object every frame.
  - It handles the documented editing keys: arrows, Home and End, Backspace and Delete,
    Ctrl+A/C/V/X, Enter per line type, and Escape.
  - Typed characters are validated: by `characterValidation`, or by `inputValidator`, which
    the game uses.
  - `onValueChanged` fires on every change. Enter in a single-line field fires `onSubmit`
    and `onEndEdit`, then deactivates.
- **Drawing:**
  - The caret and selection are drawn by a child graphic in the viewport.
  - The text is scrolled to keep the caret visible.
  - The placeholder shows when the text is empty.
- **Layout element:** the field's preferred size is its text component's plus its viewport's
  offsets. The rects do not enter it. See "The input field's layout values" below.

KInputTextField overrides `LateUpdate` and calls the base implementation, so the base's
`LateUpdate` must do its share of the work above (starting a requested activation, caret
blinking, scrolling the text).

**`TMP_InputField`: when the label and the caret are set up, measured** (2026-10-22,
`tools/UnityProbe`, the `UIS input` lines; fields built in code on an overlay canvas, phase by
phase: right after each step, then Update, a coroutine's `yield return null`, LateUpdate,
`Canvas.willRenderCanvases` and the end of the frame). "The label" is the text component's
`text`.
- **On enable, with a text component:**
  - The label becomes the field's text followed by U+200B (zero-width space) at once, in
    `OnEnable`: `""` gives `"​"`, a text set while inactive (`"hello"`) gives
    `"hello​"`.
  - It is not laid out then. `textInfo.characterCount` stays 0 and `isTextOverflowing` false
    through that frame's LateUpdate. They become 1 and true for the empty field in the canvas
    rebuild before rendering, as for any changed text. (This implementation lays it out at
    once: `characterCount` 1 right after `SetActive(true)`. Not matched.)
- **The caret is made in that same `OnEnable`,** when the field has no caret yet:
  - It is a new GameObject `Caret` (hideFlags `DontSave`) with `RectTransform`,
    `CanvasRenderer`, `TMP_SelectionCaret` and a `LayoutElement` that ignores layout, made the
    first child of `textViewport`.
  - It is made at the world origin and keeps that world position as the viewport's child, with
    the RectTransform defaults: anchors (0.5, 0.5), size 100 × 100, pivot (0.5, 0.5). Under a
    viewport centred at world (640, 459.5) its anchoredPosition is (−640, −459.5).
  - Still within that `OnEnable`, the field calls the private instance method
    `void AssignPositioningIfNeeded()` once. It copies the text component's RectTransform onto
    the caret's: `localPosition`, `localRotation`, `localScale`, `anchorMin`, `anchorMax`,
    `anchoredPosition`, `sizeDelta` and `pivot`. Measured with a Harmony hook: on entry the
    caret's anchors are (0.5, 0.5)–(0.5, 0.5) with size 100 × 100; on return they are the text's,
    (0, 0)–(1, 1) with size 0 × 0.
  - The caret's RectTransform is held, from its creation, in the private instance field
    `RectTransform caretRectTrans`. It is already set when `AssignPositioningIfNeeded` runs.
  - `AssignPositioningIfNeeded` runs nowhere else. It does not run on a later enable (the caret
    exists), on select, focus or `ActivateInputField`, on typing, on setting `text`, or on
    deselect. It is called once per caret made: for the template and for a clone, which both
    make one.
  - **Why the names are interface:** PLib's `TextMeshProPatcher`, in 36 of the 74 decompiled
    mods in `out/local-tools/moddecomp`, finds `TMP_InputField.AssignPositioningIfNeeded` by
    name. It puts a Harmony prefix on it that takes `___caretRectTrans` and
    `___m_TextComponent`, and a postfix on `OnEnable` that takes `___m_VerticalScrollbar` and
    `___m_TextComponent`. When the
    caret's eight values differ from the text's, the prefix skips the method. It starts a
    coroutine on the field instead, which copies them at the end of the frame. Without the
    method, the patch is not applied at all, and the caret is positioned at once. With the
    method but without the field, Harmony fails to patch, and PLib logs "Unable to patch
    TextMeshPro bug" (inferred from Harmony and PLib's code, not measured). So both names must
    exist, with these types.
  - **The result in the original with PLib:** measured by reproducing that prefix in the probe.
    A field hidden again in the frame it was shown loses the coroutine. Its caret then keeps
    the defaults (100 × 100, centred, at the world origin) for good, through being shown again,
    focus and typing. Sweep By Type's filter field is in this state in the original
    (`Caret` rect −50, −50, 100, 100); in ours it fills the text area.
  - A field enabled without a text component makes no caret. It makes none when a text
    component is assigned later, nor on activation, but makes one on its next enable.
- **Assigning `textComponent`** (and `placeholder`) to an enabled field leaves the text
  component alone: its `""` stays `""`. (This implementation writes `"​"` into it then.
  Not matched; this is Duplicant Stat Selector's search field, whose component is assigned at
  run time.)
- **Setting `text`:**
  - to the value it already holds changes nothing: that field's label stays `""`;
  - to another value writes the label (`"ab​"`) and lays it out at once
    (`characterCount` 3);
  - back to `""` gives `"​"`.
- **Activation** of a field whose label was never written (selecting it, with
  `shouldActivateOnSelect`, or `ActivateInputField()`) writes `"​"` in the LateUpdate that
  activates it. It is seen in that frame's `willRenderCanvases`, with `isFocused` true.
- **Not measured:** `ForceLabelUpdate()` on a field whose label was never written (measured only
  after it had been written: no change); the caret's layer; a text component on an inactive
  GameObject at enable.
- **Two text behaviours the same probe measured** (they apply to every `TextMeshProUGUI`, not only
  input fields; probe output `uis-check.py diff … input`):
  - An empty text in a rect lower than its font's line reports `isTextOverflowing` false. For
    auto-size it still counts as not fitting.
  - A text with no canvas above it is not laid out. It stays dirty and is laid out in the first
    canvas rebuild after it is parented under a canvas (a field built in code, then parented).

**`TMP_Dropdown`:** like uGUI's `Dropdown`, with TMP texts:
- **Contents:** `options` (`OptionData`: text and image), `value` and `onValueChanged`.
- **Showing the list:** `Show()` builds the list from `template` under a new canvas with a
  blocker. Clicking an option sets `value` and hides the list.
- **`RefreshShownValue`** updates the caption.
- `set_options` replaces the options and refreshes the caption.

### Miscellaneous

- **`TMPro_ExtensionMethods`:** `Compare(Vector3, Vector3, accuracy)` and
  `Compare(Quaternion, …)` compare components rounded to `accuracy` decimal places;
  `Tint(Color32, float)` multiplies RGB. Colour helpers mods call (Paint Your Pipes multiplies
  pipe colours), all public static extension methods, measured in the original player:
  - `Color32 Multiply(this Color32 c1, Color32 c2)`: each channel, alpha included, is
    ⌊c1·c2 / 255⌋ (integer division; 1×255 = 1, 1×128 = 0, 128×128 = 64, 255×255 = 255).
  - `Color32 Tint(this Color32 c1, Color32 c2)`: the same as `Multiply`, alpha included.
  - `Color MinAlpha(this Color c1, Color c2)`: c1's RGB with the smaller of the two alphas.
  - `bool Compare(this Color32 a, Color32 b)` and `bool Compare(this Color a, Color b)`: every
    channel exactly equal (a difference of 1e-7 in one float channel is unequal).
    `CompareRGB` (both overloads) is the same without alpha.
- **`FastAction<T>`:** an ordered set of delegates, called in insertion order.
- **`TMP_Text` exposes** `rectTransform`, `transform` (cached), `mesh` and the `isVolumetricText`
  family as documented.
- **`TMP_Text.ignoreRectMaskCulling`** (get and set, a `bool`, false by default) keeps the value it
  is given and changes nothing else. The shipped package marks it
  `[Obsolete("This does nothing. It was added to prevent PLib from breaking after upgrading to Unity 6.3")]`
  (a warning, not an error); keep the attribute and its message. Old PLib, embedded in many mods,
  sets it on input fields' text.

### Measured on the shipped package

Found by running the implementation's tests and the differential runs against the shipped
package (M9). They refine the sections above.
- **Sub-meshes:** `TMP_SubMeshUI` children are named "New Game Object". A sub-mesh that
  already holds a material the layout uses keeps that material's number; unused ones stay.
- **Characters:** character corners include the padding. Small caps keep the text's size and
  line metrics. Sprites don't raise the line's ascender, and `spriteCount` stays 0.
- **Zero-width space:** draws nothing, except where white space is preserved (input fields):
  there its quad goes in the spare slot without being counted. If the font lacks the
  character, it uses an empty glyph of the text's own font, not a fallback's.
- **Buffers:** grow to the next power of two of (quads + 1). Grown slots have colour zero,
  new buffers are white.
- **SDF scale refresh:** checked in `Canvas.willRenderCanvases` against the lossy scale's y
  for which the SDF scale (uv0.w) was last set. Only a change beyond a factor of 1.25 either
  way acts (0.8 and 1.25 do not); it multiplies uv0.w by the ratio and uploads, without a new
  layout. A scale to or from 0 regenerates the text. Texts with `isTextObjectScaleStatic` are
  skipped. Only screen-space-overlay canvases divide the SDF scale by `scaleFactor`.
- **Spacing and advances:** character, word and line spacing are in hundredths of the text's
  font size, whatever `<size>` says. `xAdvance` includes the spacing after the character;
  `lineInfo.maxAdvance` and `length` follow it.
- **Bold and italic:** with extra padding, a bold quad is as wide as a regular one. Italic
  glyphs lean around half the cap height, not the baseline.
- **Words:** a text starting with white space or a symbol records an empty word (0, -1) first.
- **Unwrapped measurement:** a rect without width doesn't wrap, and
  `GetPreferredValues(w, h)` returns the unwrapped width. A text added to a default 100 × 100
  rect makes it 200 × 50. A text never laid out reports one material.
- **Caret:** its material is set when the field is enabled; its CanvasRenderer is added before
  the caret graphic. The caret graphic is created only when the field is enabled with a text
  component. A field given its text component after it was enabled has no caret until it is
  enabled again (measured: a mod builds its field in code, then assigns the viewport and text).
  Canvas updates and frames don't create the caret, and nothing is logged; a rebuild with no
  caret draws none.
- **Stored alignment:** values 0–15 in the game's data use the oldest alignment table; a value
  set at run time is split into its horizontal and vertical bytes.
- **Truncation:** Truncate and Ellipsis stop where the first line that doesn't fit begins,
  leaving a blank record. Truncation keeps the part of a wrapped word that fits.
- **Auto-size** doesn't choose a size that would break a word. A wrapped text's
  `preferredHeight` uses the size auto-size chooses for the width alone, the height unbounded,
  whatever the rect's height (UnityProbe `autosizepref`, U60's All Diagnostics labels); an
  unwrapped text's is at `fontSizeMax` (measured in "Texts with nothing to lay out"). A margin change keeps the preferred sizes until the text
  changes.
- **`textBounds`** spans the characters' advances.
- **Input field:** `inputValidator` doesn't change `characterValidation`.
- **Dynamic font assets** use the "TextMeshPro/Mobile/Distance Field" shader.
- **A saved material of another atlas:** when a text is loaded, a saved `m_sharedMaterial`
  whose main texture is not its font's atlas texture is replaced by the font's material.
  A saved material that does draw the font's atlas is kept, whichever material it is, and so
  is a material set at run time, whatever its atlas. Re-enabling a text does not check
  again. (Found in QA: the Duplicant cards' expectation lines are saved with a font other than
  their material's, RobotoCondensed-Regular Material; kept, the glyphs sampled the wrong
  atlas. Test `ASavedMaterialOfAnotherAtlasGivesWayToTheFontsMaterial`.)
- **Timeline:** `TimelineClip` has no public constructor.

### Corrections from the defect review (2026-10-20)

A review of this implementation found these behaviours missing or wrong. Each must first be
measured on the shipped package as a black box (a test, or `--dump-ui` / `--dump-ui-vertices`
compared with the same screen in the original Unity player), then matched. Where the measurement differs
from what is described here, the measurement wins and this section is corrected.

1. **Horizontal overflow with wrapping off.** With word wrapping off and `Ellipsis` or
   `Truncate`, a line wider than the rect is cut at the rect's width: the characters that fit
   are kept, and for `Ellipsis` the last visible characters give way to "…" so it fits. Lines
   above it are not affected, and a single line is never removed entirely. Measure a one-line
   label slightly wider than its rect, and a two-line text whose second line is too wide.
   *Measured* (14 pt GRAYSTROKE, "QUIT GAME", 68.05 wide, in a 65.05 rect; test
   `WithoutWrappingTruncateAndEllipsisCutALineAtTheRectsWidth`):
   - The cut is at the first visible character whose glyph advance passes the width ("E"):
     Truncate keeps "QUIT GAM" and ends with the blank record (9 records, one line).
   - Ellipsis replaces the last character that fit ("M") with "…": "QUIT GA…" and the blank
     record. The ellipsis is drawn at the text font's scale even when its glyph comes from a
     fallback (NotoNaskhArabic, 32 pt: scale 14/124, 2.131 wide, where a typed "…" from the same
     fallback is 8.26). This holds for the vertical Ellipsis too.
   - Lines above the cut are not affected; the cut line ends the text: in "QUIT GAME
     LONGER\nAB" the second line is not shown.
   - The correction above is wrong on one point: when even a line's first character does not
     fit, the line is not kept. On the first line nothing is shown (0 characters, 0 lines) in
     both modes; on a later line Truncate leaves the blank record alone on it, and Ellipsis
     shows nothing at all.
   - A cut at the width, and any line wider than the rect without wrapping (Overflow mode
     too), is not an overflow: `isTextOverflowing` is false and `firstOverflowCharacterIndex`
     -1; `isTextTruncated` is true.
   - Not matched: on the cut line the shipped package counts the replaced character in
     `visibleCharacterCount` besides the ellipsis, and its `length` includes the padding.
2. **Several atlas textures.** A glyph's `atlasIndex` selects the texture it is drawn from. A
   font asset whose glyphs span several atlases (a dynamic font with multi-atlas support after
   its first atlas fills, or a shipped asset with `atlasIndex > 0` glyphs) draws each glyph from
   its own atlas: one sub-mesh per atlas, each with a material whose main texture is that atlas.
   Measure with a dynamic font asset small enough to fill its first atlas.
   *Measured* (LiberationSans at 48 pt, padding 5, 64 × 64 atlases, multi-atlas on, "ABCDEF" at
   20 pt; test `GlyphsInLaterAtlasesDrawFromTheirOwnAtlas`): each atlas holds one letter, so
   the six letters open atlases 1–6. Every character whose glyph is in atlas k > 0 is drawn in
   a sub-mesh whose material is a copy of the text's material with atlas k as its main
   texture (`characterInfo.material` and `meshInfo[n].material` too): `materialCount` 7, the
   text's own mesh empty, six sub-meshes of one quad. Glyphs in atlas 0 stay in the text's
   own mesh. Also seen, not matched: the shipped package adds "…" (U+2026) and "_" (U+005F) to
   a dynamic asset at its first layout, whatever the overflow mode, so they take the first
   atlas (without multi-atlas, the letters then come from a fallback); its `atlasTextures`
   array grows by doubling (8 entries for 7 atlases, the last null); and the asset's material
   and textures are unnamed.
3. **Rejected characters over a selection.** Typing a character that validation rejects
   (`characterValidation`, `contentType`, `onValidateInput`, a custom `inputValidator`)
   changes nothing: the selection stays selected and the text unchanged. Validation sees the
   text as it is before the selection is replaced. Measure in a field with Integer validation
   and its text selected, typing a letter.
   *Measured* (test `ARejectedCharacterLeavesTheSelection`):
   - Integer validation, "123" all selected, "x" typed: the text, the selection and
     `onValueChanged` are untouched; likewise with "23" selected, with `contentType`
     IntegerNumber, and when `onValidateInput` returns 0.
   - The last sentence of the correction is wrong: validation sees the text **with the
     selection removed**, at the position where the selection began. "-" typed over the
     selected "-" of "-12" is accepted (it is at the start of "12"), giving "-12"; ".2"
     selected in "1.2" with Decimal and "." typed gives "1."; `onValidateInput` is called with
     ("a", 1) for "bc" selected in "abc", and with ("", 0) for all of it.
   - A custom validator (`CustomValidator` with `inputValidator`) is the exception: the
     selection is removed before it is called and stays removed when it rejects the
     character ("BC" selected in "ABC", "x" rejected: "A", caret 1, without
     `onValueChanged`).
   - With validation None, `inputValidator` is not used; `characterLimit` counts the text
     with the selection removed ("1234" all selected with a limit of 4 accepts "5").
   - Ctrl+A puts the selection's anchor at the end and the caret at the start
     (`selectionAnchorPosition` 3, `caretPosition` 0 for "123").
4. **Line breaks after hyphens.** Measure whether a line may break after `-` (U+002D) and after
   a soft hyphen (U+00AD), whether the soft hyphen is drawn at a break and hidden elsewhere,
   and whether a non-breaking hyphen (U+2011) prevents a break; implement what is measured.
   *Measured* (14 pt GRAYSTROKE, "aaaa" 30.23 wide, "aaaa-" 37.79; test
   `LinesMayBreakAfterHyphensAndSoftHyphens`):
   - A line may break after "-": "aa-bb-cc-dd" in 38.79 gives "aa-" / "bb-" / "cc-dd" (where
     "aa-bb" would fit), and "aaaa-bbbb" in 30.73 gives "aaaa" / "-" / "bbbb".
   - Not after the hyphen U+2010 or the non-breaking hyphen U+2011 (both from the fallback
     here): "aaaa‑bbbb" in 30.73 breaks between characters, "aaaa" / "‑bbb" / "b".
   - The soft hyphen U+00AD takes no room (the preferred width of "aaaa", U+00AD, "bbbb" is
     that of "aaaabbbb", 60.46) and is not drawn, its corners at its origin. A line may break
     after it; there it is drawn with its glyph's advance (3.472) when that fits the line
     (38.79), and stays hidden when it does not (30.73), the break the same.
   - Nor after the en and em dashes (U+2013, U+2014) or "/": those words break between
     characters.
   - Not matched: a line that ends where the text wraps (or is cut) has a `length` that
     includes the material padding of its last character (0.141 here); a text's last line
     does not.
5. **Caret and selection with rich text.** The input field keeps string positions (in its
   text) and character indices (in the laid-out text) apart. Typed tags, escapes and surrogate
   pairs make them differ; the caret and the selection are drawn at the laid-out character for
   their string position, and moving up and down starts from that character. Measure a field
   with rich text on, after typing `a<b>c`, an escape, and an emoji.
   *Measured* (test `TheCaretIsAtTheLaidOutCharacterOfItsStringPosition`):
   - `caretPosition`, `selectionAnchorPosition` and `selectionFocusPosition` are character
     indices; `stringPosition`, `selectionStringAnchorPosition` and
     `selectionStringFocusPosition` string positions. "a<b>c" lays out as "a", "c" and the end
     marker (source indices 0, 4, 5): at the end the caret is 2 and the string position 5.
     A string position is at the first character whose source index is not before it.
     Setting `caretPosition` to 1 sets the string position to 4 (that character's index).
   - Left moves to the character before, at its source index: 5 → 4 → 0 (over the tag).
     Right moves past the source of the caret's character: 0 → 1 (before the tag) → 5.
     Shift+Left from the end: anchor 2 / 5, focus 1 / 4.
   - A surrogate pair is one character of two positions: "a😀b" (the emoji drawn as the
     missing glyph □) ends at caret 3, string position 4; Left gives 2 / 3, then 1 / 1. An
     escape typed as "\" and "n" is parsed into a line feed, one character of two positions:
     "a\nb" ends at 3 / 4 on the second line; Left gives 2 / 3, then 1 / 1.
   - Up and down start from the caret's character: in the multi-line "ab<b>cd" + Enter +
     "ef", Up from the end (7 / 10) goes to "c" (2 / 5), the nearest character above, and
     Down back. Up on the first line goes to the start.
   - The caret quad is `caretWidth` wide at its character's origin, from the ascender to the
     descender of the character before it on the same line (its own at a line's start): after
     "a" in "a😀b" it is as tall as "a", after "□" as the fallback's □. The selection runs
     from its first character's origin to its last's advance.
   - The text component is laid out when a key changes the text, so `textInfo` and the
     character indices are current right after it. Counted with `TEXT_CHANGED_EVENT` (test
     `EachChangeLaysTheTextOutOnceAndAPasteOnce`): three characters typed in one frame lay it
     out three times; a paste of 3,000 characters raises `onValueChanged` 3,000 times and lays
     it out once; setting `text` lays it out once. The canvas rebuild after any of these does
     not lay it out again (a text laid out by `ForceMeshUpdate` is not regenerated in the
     next rebuild unless it changes again).
   - Not matched: setting `caretPosition` in code does not redraw the shipped package's
     caret until the next change; this implementation redraws it at the next rebuild.

**Measured after the review of these corrections:**
- **The record where truncation stops** is U+0003, not drawn, with the source index of the
  character it stopped at, at the pen position there (origin and advance), on its line. An
  ellipsis has the source index of the character it replaced. In a 100-wide input field whose
  text truncates (Truncate or Ellipsis), "QUIT GAME NOW" stops before "W": the text is not
  scrolled, the caret at the end is character 12 (the record, index 12) and is drawn at its
  origin; Left goes to 11 / 11 (the ellipsis, or "O"); End goes to 12 / 12, the index of the
  last laid-out character rather than the text's length. Test
  `InATruncatedTextTheCaretEndsWhereTheTextStops`.
- **Soft hyphens and the line's height:** a hidden soft hyphen takes no part in its line's
  ascender, descender or line height ("aaaa", U+00AD, "bbbb" is 14.70 high, as "aaaabbbb",
  though its soft hyphen comes from a taller fallback). One drawn where the line breaks does
  (the line is 23.8 high) but is not counted when the line is checked against the rect's
  height. When Truncate or Ellipsis continue that line (38.79 wide, 20 high), the soft
  hyphen is hidden again: "aaaa", "b", then the blank record (Ellipsis: "aaaa…"). Test
  `AHiddenSoftHyphenTakesNoPartInItsLinesHeight`. A drawn soft hyphen's `character` is "-"
  (U+002D), every time it is drawn, as in the shipped package (measured: "Words and their
  separators" below; test `ADrawnSoftHyphenReportsAHyphenMinus`). Not matched: the shipped
  package puts its line 0.035 lower.
- **Password fields** show one asterisk per UTF-16 unit, and the shipped package's caret
  steps per unit, between the halves of a surrogate pair (a, 😀, b: Left from 4 gives 3, 2,
  1); a character typed there goes between them, and Backspace removes one unit, leaving a
  lone high surrogate. This implementation steps the same way but removes a whole pair with
  Backspace and Delete (deliberately not matched: it would leave a lone surrogate). In a
  plain field both packages step and delete by pairs.
- **Not measured** (multi-atlas fonts only, which the game does not use): which material a
  decoration (underline, strikethrough, highlight) takes when its run starts in atlas k; and
  the per-atlas material copies are made once per source material and texture, not
  refreshed when the source material changes later.

### Preferred sizes to the hundredth (measured 2026-10-23)

Found in mod play-tests: overlay legend rows (Gas Overlay, Rooms Expanded, Pipe Flow Overlay),
Bigger Building Menu's search bar and a Butcher Station label were 0.01 narrower in this
implementation (67.25 for 67.26, 100 for 100.01, 13.75 for 13.76), and a label of one space
was 0 × 19.04 where the shipped package's is 0.01 × 0.01. The UI dump's formatting is not
the cause: `Math.Round(v, 2)` and `"0.##"` give the same strings in both players for every
float tested, and the layout groups add the same child values to the same sums. The first
value that differs is the text's own `preferredWidth`.

Measured with `PROBE_SCENARIO=textwidth` (tools/UnityProbe, `TextWidth.cs`): 19 labels in
every NotoSans and GRAYSTROKE font asset, at 12, 14 and 16 pt, regular and bold, set up as
`SetTextStyleSetting` does (kerning, extra padding, no wrapping), in a 400 × 40 rect at
three pivots, under a `HorizontalLayoutGroup` beside a 24-wide icon, and copied into a
`LayoutElement`. `tools/UnityProbe/tw-check.py` compares two logs and checks the rule below.
- **The rule.** Let x be the widest line's width, as a float: the pen at the end of its last
  visible character (that character's origin plus its glyph's advance, without the
  character or style spacing after it, rounded to float once), measured from the line's left
  edge (where an indented line's pen starts is not the origin; see "Indents opened part-way
  along a line").
  `preferredWidth` is (⌊x × 100⌋ + 1) / 100, with x × 100 taken exactly (in double, not
  rounded to a float first), stored as the nearest float. So a width already on a
  hundredth goes up by 0.01 (67.25 gives 67.26, 139 gives 139.01), and any other goes to
  the next hundredth (85.09375 gives 85.1). The rule reproduces all 648 regular-style
  texts at pivot 0, including white space, against the shipped package; ceiling to the
  hundredth, as this implementation did before, misses 21 of the 576 that are not white
  space, and the 72 that are.
- **Implemented.** This implementation follows the rule: in the same probe it reproduces it
  on all 648 regular-style texts, at every pivot and under the layout group. With the pens
  summed as "Pens to the bit" (below) says, every text of the probe has the shipped
  package's width and every pen its bits.
- **Not tied to the rect.** The value does not depend on where the rect is: the same text at
  pivot 0.5, 0 and 0.37, and as a child of the layout group, has the same bits in all 1,368
  cases. This implementation used to sum the pens in the rect's coordinates, so its value
  moved with the rect: 68 texts changed between the pivots and 61 under the layout group
  (GRAYSTROKE REGULAR 16 pt "Hydrogen" was 67.51 alone and 67.5 in the row), and "i" in
  NotoSansCJKjp 12 pt was 3.31 where the shipped package's pen is the float 3.3
  (3.2999999523) and gives 3.3. It now measures from the line's own pen start and gives the
  same bits at every pivot and in the row.
- **White space.** A text of only white space (" ", "  ") has no visible character, so x is
  0: its preferred width is 0.01, and its preferred height is 0.01 too (the height is that of
  the visible characters, none here, by the same rule). This implementation gave 0 and one
  line's height (12.6 for GRAYSTROKE 12 pt, 19.04 for NotoSans 14 pt); it now gives
  0.01 × 0.01. An empty text ("") is 0 × 0 in both.
- **Heights** follow the same rule (first line's ascender to the last line's descender):
  NotoSans-Regular at 16 pt is 21.75 high and gives 21.76; at 12 pt 16.3125 gives 16.32.
  No other height measured falls on a hundredth, so ceiling and the rule agree on the rest.
- **Bold:** x ends at the last character's regular glyph advance, as above; the bold spacing
  after it is not counted. NotoSans-Regular 12 pt bold "Ethanol Gas" ends its last advance at
  71.21, its "s" begins at 65.1 and is 5.75 wide: x is 70.849998 and the width 70.85 (this
  implementation gave 70.86 for the centred rect, and now gives 70.85). The rule reproduces 574 of 576 bold texts
  from the logged values; the two misses are within one float step of a hundredth, where
  this reconstruction from logged values is not exact.
- **Layout groups and layout elements** take the value as it is: a `HorizontalLayoutGroup`
  with 4 + 4 of padding, 12 of spacing and a 24-wide child reports 44 + the text's width in
  float (148.26001 for 104.26; 44.01 for " "), and a `LayoutElement` given it keeps the bits.

| Font, size, text (regular) | x | shipped | this implementation before | now |
|---|---|---|---|---|
| NotoSans-Regular 12, "Ethanol Gas" | 67.25 | 67.26 | 67.25 | 67.26 |
| NotoSans-Regular 12, "Super Coolant Gas" | 104.25 | 104.26 | 104.25 | 104.26 |
| NotoSans-Regular 12, "Nuclear Fallout" | 85.09375 | 85.1 | 85.1 | 85.1 |
| NotoSans-Regular 12, "Room" | 33.1875 | 33.19 | 33.19 | 33.19 |
| NotoSans-Regular 16, "Super Coolant Gas" | 139 | 139.01 | 139 | 139.01 |
| NotoSans-Regular 16, "Room" | 44.25 | 44.26 | 44.25 | 44.26 |
| GRAYSTROKE REGULAR SDF 16, "Hydrogen" | 67.5 | 67.51 | 67.5 at pivot 0 | 67.51 |
| GRAYSTROKE OUTLINE SDF 14, "A" | 8.5 | 8.51 | 8.5 | 8.51 |
| NotoSansCJKjp-Regular 12, "Oxygen" | 41.850002 | 41.86 | 41.86 | 41.86 |
| NotoSansCJKjp-Regular 12, "i" | 3.2999999523 | 3.3 | 3.31 | 3.3 |
| NotoSans-Regular 14, " " | 0 | 0.01 × 0.01 | 0 × 19.04 | 0.01 × 0.01 |
| any, "" | — | 0 × 0 | 0 × 0 | 0 × 0 |

Not measured: margins (they are expected to be added to x before the rounding, as the
section above says they are included), several lines, wrapping, auto-size and rich-text
spacing tags. Measure them the same way before relying on them.

### Pens to the bit (measured 2026-11-01)

Found in a mod play-test: Radiation and Light Transport's Light Collector title (GRAYSTROKE
REGULAR SDF 16 pt) was 102.41 wide in the original and 102.4 here. The character pens
(`xAdvance` at pivot 0) differed by a float step or two in 1,388 of the probe's 4,320 texts
(19 labels and "Light Collector", every NotoSans and GRAYSTROKE asset, 12, 14 and 16 pt,
regular and bold, three pivots), all at scales that are not powers of two (16/124, 14/24,
...): NotoSans 14 pt "Oxygen" has its "g" end at 34.08854294 shipped, 34.08853912 here. The
preferred width moved by 0.01 where the pen ends within a float step of a hundredth: 42 texts
("Light Collector" ends at 102.400009 shipped, 102.399994 here).

- **The rule.** Each character's pen advance is its glyph's advance × the scale (and the
  width adjustment), plus its kerning pair adjustments × the same, plus the spacing after it
  ((character spacing + the style's spacing) × the size / 100, word spacing for a space), all
  in double; the pen position is the previous pen plus that, rounded to float once. A
  character's `xAdvance` is its pen plus its advance, rounded once. This is what the shipped
  package's float code computes when the runtime keeps float intermediates in double
  (ARCHITECTURE D25), as Unity's Mono does. Rounding the glyph advance to float first, then
  the spacing, then the sum, gave the differences above.
- **Implemented.** In the same probe every pen of all 4,320 texts has the shipped package's
  bits, and every preferred width its value. Rounding the spacing to float alone still left
  67 bold texts a step off (the bold spacing × size / 100 is not a float: 7 × 0.16).

### Overflow at white space (measured 2026-10-23)

Found in a mod play-test: Butcher & Fishing Stations has a label of one space (" ", 14 pt,
centred) in a rect its `LayoutElement` sizes to the text's preferred size, 0.01 × 0.01 (see
"Preferred sizes to the hundredth" above). The shipped package reports `isTextOverflowing`
false and `firstOverflowCharacterIndex` -1; this implementation reports true and 0. The
`textInfo` counts are the same in both.

Measured with `PROBE_SCENARIO=textoverflow` (tools/UnityProbe, `TextOverflow.cs`): 22 texts
(" ", "  ", "\t", " \n ", "\n", "", U+00A0, "A", " A", "A ", " A ", "  A  ", "A\n", "\nA",
"A\n ", " \nA", "A B", "AB", "A\nB", " \n\nA", "A \n ", "…") in NotoSans-Regular 14 pt and
GRAYSTROKE REGULAR SDF 12 pt, set up as `SetTextStyleSetting` does. Rect widths 0, 0.005, 0.01,
the preferred width and 0.01 and 0.001 under and over it, and 400; heights 0, 0.005, 0.01, the
preferred height and the same steps around it, one line of the font (ascender to descender)
and 0.01 under and over it, and 100. Overflow, Truncate, Ellipsis and Masking, wrapping on and
off, centred (and top-left for Overflow without wrapping): 37,692 cases, each with
`isTextOverflowing`, `firstOverflowCharacterIndex`, `isTextTruncated`, the `textInfo` counts
and every character's code, `isVisible` and `lineNumber`. `tools/UnityProbe/ov-check.py rule
LOG` predicts each case from the rule below and reproduces all 37,692; this implementation
(with the preferred sizes above) reproduces 25,947 of its 38,124.
- **Checked characters.** Every character except white space is checked against the rect's
  height. The white space here is space, no-break space and line feed; "Further characters"
  below lists the rest. The tab is checked too, though it is not visible (`isVisible` false,
  character 9).
- **Vertical overflow.** The text overflows at the first checked character whose line's
  bottom is below the rect: the distance from the first line's ascender down to that line's
  descender is greater than the rect's height. Exactly at the height fits: NotoSans 14 pt's
  first line ends at 19.03125, so 19.03125 fits and 19.03 overflows; its second line ends at
  38.098953. `firstOverflowCharacterIndex` is that character's index, and
  `isTextOverflowing` is true when there is one.
  - So a text of white space alone never overflows, in any rect (0 × 0 too), in any mode,
    with or without wrapping: " ", "  ", " \n ", "\n" and U+00A0 report false and -1, and
    keep their characters.
  - White space before the first checked character, after the last one, and lines of white
    space alone never overflow either: " A" in a rect of height 0 overflows at 1, "\nA" at 1,
    " \n\nA" at 3; "A\n " in a 400 × 19.03125 rect does not overflow.
  - Lines of white space still take their height: in " \nA" the "A" overflows below 38.098953,
    in " \n\nA" below 57.16666.
  - Alignment makes no difference, and Masking reports the same as Overflow.
- **Cut at the width** (Truncate and Ellipsis, wrapping on or off). The first visible character
  (not white space, not a tab) whose pen after it passes the rect's width cuts the text there.
  The pen is measured from the line's start, as laid out. With wrapping on, such a character
  is one that is still too wide on the line it wraps to. "A" (8.93 wide) in a rect 0 wide, wrapped,
  Truncate, shows nothing and reports `isTextTruncated` true; in Overflow it shows "A". This
  cut is not an overflow: `firstOverflowCharacterIndex` stays -1, as in "Horizontal overflow
  with wrapping off" above. A tab is never cut.
- **Which cut.** The text is cut at the earlier of the two. When the cut at the width comes
  first, the index is -1. When both fall on the same character, it is the overflow's.
- **Truncate** at the cut c:
  - At c = 0, nothing is shown: `characterCount` 0, `lineCount` 0.
  - Otherwise the characters before c stay as laid out and c becomes the blank record
    (U+0003, not visible): `characterCount` is c + 1. White space before the cut is kept:
    " A" in a rect of height 0 keeps " " and the record on one line.
  - The record is on c's line. When that line began by wrapping (not after a line feed), the
    record stays at the end of the line before, and the text has one line fewer: "A B" wrapped
    at 21.69 × 19.04 gives "A", " " and the record on line 0; "\nA" at height 0 gives "\n" and
    the record on line 1.
- **Ellipsis** at the cut c: "…" takes the place of the last character before c whose line
  fits the rect's height and at whose pen "…" fits the width (its pen plus the ellipsis's
  advance, 11.083328 for NotoSans 14 pt, 7.078125 for GRAYSTROKE 12 pt from its fallback). The
  blank record follows it on the same line, and the text ends there (`lineCount` is that line
  + 1). When no character qualifies, nothing is shown (`characterCount` 0, `lineCount` 0).
  - " A" at 12.57 × 100 gives "…" and the record; "  A  " at 16.22 gives " …".
  - " A" 0 wide gives nothing: "…" does not fit at the space's pen 0.
  - " A" at height 0 gives nothing: line 0 does not fit.
  - " \n\nA" at 400 × 19.03125 gives " …" on line 0, in place of the first line feed: the
    second line does not fit.
  - GRAYSTROKE 12 pt " \nA" at 7.28 × 12.59 gives "…" in place of the space: at the line
    feed's pen (2.28) it does not fit.
  - This refines "Ellipsis shows nothing at all" in "Horizontal overflow with wrapping off"
    above: that holds where "…" is wider than the room before the cut.
- **Preferred height** is measured down to the last line with a checked character. "A\n " and
  "A \n " are 8.94 × 19.04 (this implementation: 38.1 high), " \nA" 38.1 and " \n\nA" 57.17
  high. "\t" is 0.01 × 19.04 (its width is that of no visible character, its height that of a
  checked one).

- **Implemented.** This implementation follows the rule: in the same probe `ov-check.py rule`
  reproduces all 37,692 cases from its log, and `ov-check.py diff` finds none that differ from
  the shipped package, preferred sizes included. "…" fits where its glyph's advance at its own
  font's scale fits (7.078125 for GRAYSTROKE 12 pt, from its fallback); it is still drawn at
  the text font's scale, as "Horizontal overflow with wrapping off" above measured. The further
  characters below are implemented as listed there, and auto-size still counts a line of white space
  below the rect as not fitting.

Where this implementation deviated before (`TMP_Text.Layout.cs` and `TMP_Text.Place.cs`):
- It checks each line, at the line's first character, whatever that is. So white space
  overflows (" " at 0.01 × 0.01 reports 0), and the index is the line's start (" A" and "\nA"
  at height 0 report 0, "A\n " at 400 × 19.03125 reports 2).
- Truncate and Ellipsis then remove the whole overflowing line: " A" at height 0 shows
  nothing where the shipped package keeps " " and the record. A text of white space alone in
  a low rect is cut to nothing where the shipped package keeps it.
- Ellipsis places "…" at the character before the cut without checking that it fits: " A" 0
  wide shows "…", and GRAYSTROKE 12 pt " \nA" at 7.28 × 12.59 shows " …".
- With wrapping on, Truncate and Ellipsis keep a character too wide for any line ("A" 0 wide
  stays "A"; " A" 0 wide gives " " and "A" on two lines).
- A tab is drawn as a visible U+25A1 and is 14.01 × 20.74 (NotoSans 14 pt), and lines of
  white space after the last checked character count towards the preferred height.

| Text (NotoSans-Regular 14 pt, centred), rect, mode | shipped: overflowing, index; characters | this implementation before |
|---|---|---|
| " ", 0.01 × 0.01, Overflow (Butcher & Fishing Stations) | false, -1; " " | true, 0; " " |
| " ", 0 × 0, Truncate | false, -1; " " | true, 0; nothing |
| U+00A0, 0 × 0, Ellipsis | false, -1; U+00A0 | true, 0; nothing |
| " \n ", 0 × 0, Overflow, wrapped | false, -1; 3 characters, 2 lines | true, 0 |
| "A", 400 × 19.03, Overflow | true, 0 | true, 0 |
| "A", 400 × 19.03125, Overflow | false, -1 | false, -1 |
| "\t", 400 × 19.03125, Overflow | false, -1; "\t" not visible | true, 0; U+25A1 visible |
| " A", 400 × 0, Overflow | true, 1 | true, 0 |
| " A", 400 × 0, Truncate | true, 1; " ", record | true, 0; nothing |
| " A", 400 × 0, Ellipsis | true, 1; nothing | true, 0; nothing |
| " A", 0 × 100, Ellipsis | false, -1, truncated; nothing | false, -1; "…", record |
| " A", 12.57 × 100, Ellipsis | false, -1; "…", record | the same |
| "\nA", 400 × 0, Overflow | true, 1 | true, 0 |
| "\nA", 400 × 0, Truncate | true, 1; "\n", record on line 1 | true, 0; nothing |
| "\nA", 400 × 38.09, Ellipsis | true, 1; "…", record on line 0 | the same |
| "A\n ", 400 × 19.03125, Overflow | false, -1 | true, 2 |
| " \n\nA", 400 × 19.03125, Ellipsis | true, 3; " ", "…", record | true, 2; the same characters |
| "A B", 21.69 × 19.04, Truncate, wrapped | true, 2; "A", " ", record, 1 line | the same |
| "A B", 21.69 × 19.04, Ellipsis, wrapped | true, 2; "A", "…", record | the same |
| "A", 0 × 100, Truncate, wrapped | false, -1, truncated; nothing | false, -1; "A" |
| " A", 0 × 100, Truncate, wrapped | false, -1; " ", record, 1 line | " ", "A", 2 lines |
| GRAYSTROKE 12 pt " \nA", 7.28 × 12.59, Ellipsis | true, 2; "…", record | true, 2; " ", "…", record |
| GRAYSTROKE 12 pt U+00A0 (fallback, 20.41 high), 400 × 12.59, Overflow | false, -1 | true, 0 |
| preferred size, "A\n " | 8.94 × 19.04 | 8.94 × 38.1 |
| preferred size, "\t" | 0.01 × 19.04 | 14.01 × 20.74 |

**Further characters** (measured 2026-10-23, the same probe with `PROBE_OV_PART=chars`, `CH`
lines). Each character x below was measured alone, doubled, and in "xA", "Ax", "A\nx", "A\nxx"
and "x\nA", in NotoSans-Regular 14 pt and GRAYSTROKE REGULAR SDF 12 pt. The rects were 0 × 0,
0.01 × 0.01, 400 × 0, 400 × one line (and 0.01 under it) and 400 × 100, in Overflow and
Truncate. Both fonts agree on everything except advances and heights.
- **Not checked** (never overflow; a text of only them reports false and -1 in 0 × 0 and
  0.01 × 0.01 and keeps its characters under Truncate; "xA" in 400 × 0 overflows at 1; "A\nx" in a
  rect one line high does not overflow; "A\nx" is as high as "A"): space, U+00A0, line feed,
  CR (U+000D), vertical tab (U+000B), U+2000–U+200A, the zero-width space U+200B, U+2028,
  U+2029 and U+3000. None of them is visible.
  - Line feed, vertical tab, U+2028 and U+2029 break the line ("x\nA" has three lines). CR
    does not: it is an ordinary character on its line, 7.146 wide in NotoSans 14 pt and 0 in
    GRAYSTROKE 12 pt ("x\nA" has two lines).
  - Not checked does not mean no height. A white-space character that comes from a fallback
    font with a deeper descender still deepens its line. In NotoSans 14 pt, U+3000 (descender
    5.79 below the baseline against 4.08) makes "x\nA" 39.81 high, not 38.1. The same goes
    for U+00A0 and U+2000–U+200A in GRAYSTROKE 12 pt: "x\nA" is 33.01, not 25.18. On the last
    line it still raises the preferred height, though by an amount not explained here: "A\nx"
    is 20.33 (U+3000, NotoSans) and 14.03 (U+00A0 and U+2000–U+200A, GRAYSTROKE, also U+3000
    15.13), where one line is 19.04 and 12.6. Its overflow index is not affected.
- **Checked** (as a visible character: a text of only it overflows at 0 in 0 × 0 and
  0.01 × 0.01, Truncate there leaves nothing, "xA" in 400 × 0 overflows at 0, and "A\nx" in a rect
  one line high overflows at 2):
  - the tab (not visible, see below);
  - form feed (U+000C) and U+180E: neither font has them, so each is drawn as the missing
    glyph U+25A1, visible: 14.01 × 20.74 (NotoSans 14 pt), 12.01 × 17.78 (GRAYSTROKE 12 pt);
  - U+FEFF: visible (`isVisible` true), 0 wide, from a fallback: 0.01 × 23.82 (NotoSans),
    0.01 × 20.42 (GRAYSTROKE).
- **U+0003 ends the text.** Nothing after it is laid out: "x" and "xA" have no characters and
  no lines and are 0.01 × 0.01; "x\nA" has none either. "Ax" has two characters, the second
  U+0003 (not visible, of no height), and is 8.94 × 19.04. "A\nx" has three characters on two
  lines, but is 8.94 × 4.09 (NotoSans) and 7.29 × 1.21 (GRAYSTROKE), less than the "A" alone:
  an anomaly measured, not explained. None of them overflows.
- **The tab's advance.** A tab moves the pen to the next tab stop. Stops are multiples of
  T = `tabSize` × `faceInfo.tabWidth` × (font size / `faceInfo.pointSize`) × `faceInfo.scale`,
  counted from the line's start. A pen already on a stop moves to the next one.
  - NotoSans-Regular has `tabSize` 10 and `tabWidth` 62.5 at point size 24: T is 364.58333 at
    14 pt and 729.16667 at 28 pt. GRAYSTROKE REGULAR SDF has 10 and 310 at 124: T is 300 at
    12 pt, 600 at 24 pt.
  - "\t" ends at T, "\t\t" at 2T, "A\t" and " \t" at T, "\tA" has its "A" at T.
  - The stops do not depend on the rect: the same in rects 50, 400 and 1000 wide. Centred
    text lays the line out the same and then centres it ("\t" from -T/2 to T/2).
  - The space width plays no part: 10 × the space is 36.458 and 22.778, which this
    implementation uses. It agrees with T only where `tabWidth` is 10 spaces (NotoSans: 62.5
    is 10 × 6.25), and even there it leaves out the `tabSize` factor.
  - The preferred width counts a tab at its start, not its end. "\t" is 0.01 and "\t\t"
    364.59 (the second tab starts at T), "A\t" 8.94, " \t" 3.65, and "\tA" 373.52, which is
    T + the "A". It is the next hundredth above the larger of the last visible character's
    end and the last tab's start.

| Character (NotoSans-Regular 14 pt) | checked | alone in 0 × 0 | "xA", 400 × 0 | "A\nx", 400 × 19.03125 | preferred size of x; of "A\nx" |
|---|---|---|---|---|---|
| U+000D (CR) | no | false, -1 | true, 1 | false, -1 | 0.01 × 0.01; 8.94 × 19.04 |
| U+000B (VT), U+2028, U+2029 | no, break the line | false, -1 | true, 1 | false, -1 | 0.01 × 0.01; 8.94 × 19.04 |
| U+2000–U+200A, U+200B | no | false, -1 | true, 1 | false, -1 | 0.01 × 0.01; 8.94 × 19.04 |
| U+3000 (from a fallback) | no | false, -1 | true, 1 | false, -1 | 0.01 × 0.01; 8.94 × 20.33 |
| U+0009 (tab) | yes, not visible | true, 0 | true, 0 | true, 2 | 0.01 × 19.04; 8.94 × 38.1 |
| U+000C (FF), U+180E | yes, drawn as U+25A1 | true, 0 | true, 0 | true, 2 | 14.01 × 20.74; 14.01 × 39.81 |
| U+FEFF | yes, visible, 0 wide | true, 0 | true, 0 | true, 2 | 0.01 × 23.82; 8.94 × 42.89 |
| U+0003 | ends the text | false, -1, no characters | false, -1, none | false, -1 | 0.01 × 0.01; 8.94 × 4.09 |

| Tab, rect 400 wide, left-aligned | shipped: tab ends at (from the line's start) | this implementation before |
|---|---|---|
| NotoSans-Regular 14 pt "\t" | 364.58333 | 36.458 |
| GRAYSTROKE REGULAR SDF 12 pt "\t" | 300 | 22.778 |

- **Implemented.** This implementation follows the block. In the probe's `CH` and `TAB`
  lines it matches the shipped package's overflow, index, truncation, counts and characters,
  and the tab's stops and preferred widths, except where the block leaves the heights of
  white space from a fallback and "A\n\u0003" unexplained: there "x\nA" is 39.74 where the
  shipped package gives 39.81 (NotoSans, U+3000, U+000C, U+180E), "A\nx" stays one line high,
  and "Ax" in a rect one line high overflows at the "A" (U+3000, and U+00A0 and U+2000–U+200A
  in GRAYSTROKE, which the shipped package lays out at the text font's height after a
  visible character). It also gives index 0, not 1, for "Ax" one line high where x is a
  taller checked character (U+000C, U+180E, U+FEFF), which the block does not cover. CR's
  record spans its glyph's advance but leaves the pen where it is ("\rA" has its "A" at the
  line's start, as the probe logs it). The zero-width space is unchecked only where it is not
  drawn: where white space is preserved it is visible (see "Text layout"), and this
  implementation checks it there like any visible character. An empty `TMP_InputField`'s text
  is a lone U+200B with white space preserved, and in the shipped package the field is one
  line high (Butcher & Fishing Stations: 21.04 with its padding); without this it was 0.01
  high (2.01 with padding).

Not measured: Page, Linked
and ScrollRect, auto-size (whether a text of white space alone fits), margins, rich text,
several fonts on one line, and whether "…" backs up past a visible character it cannot follow.
Measure them the same way before relying on them.

### The input field's layout values (measured 2026-10-23)

Found in a mod play-test: Geyser Control opens a `MultiSliderSideScreen`. Each slider row's
`TitleRow` (a `HorizontalLayoutGroup` controlling its children's width and height, not
expanding them) holds a `NumberInputField`, a `KInputTextField` (a `TMP_InputField`). In the
shipped package the row is 24 high and the field's rect is -32, -12, 64, 24. In this
implementation the row's preferred height was 33,329.72 and the screen 166,846 px tall, drawn
as a white column. The first value that differs is the field's own `preferredHeight`: 19.04
in the shipped package, which equals its text component's, and 44.04 in this implementation
at the field's first layout. That value then grows by 20.04 with every layout pass.

The prefab, read in the original player through E2eDriver:
- **The field:** a 64 × 24 rect (anchors top-left of the row), with a `LayoutElement` (min
  64 × 24, preferred width 64, preferred height -1, flexible width 0, priority 1), a sliced
  `Image` (priority 0), `KNumberInputField` and `KInputTextField`. Its settings: `SingleLine`,
  `characterLimit` 4 (set by `SliderSet.SetTarget`), `DecimalNumber`, no placeholder.
- **`textViewport`:** "Text Area", with a `RectMask2D` (padding 0), stretched over the field
  with offsets 0.
- **`textComponent`:** "Text", a `LocText` at 14 pt, no wrapping. It is anchored across the
  viewport's top edge: anchors (0, 1)–(1, 1), size delta (-3, -1), position (1.5, -0.5). Its
  rect is 61 wide and -1 high, whatever the field's height.
- **The values:** the field's preferred size is 32.09 × 19.04, its text's 32.09 × 19.04.
  `LayoutUtility` gives the field 64 × 24: the `LayoutElement`'s preferred width and minimum
  height are the larger values. Forcing the
  row's layout again (`LayoutRebuilder.ForceRebuildLayoutImmediate`) changes nothing.

Measured with `PROBE_SCENARIO=inputlayout` (tools/UnityProbe, `InputLayout.cs`): fields built in
code as Klei's prefabs are, a field with an `Image` and a `TMP_InputField`, its child
"Text Area" (`RectMask2D`) as `textViewport`, and its grandchild "Text" (NotoSans-Regular
14 pt, no wrapping) as `textComponent`. The cases vary:
- the field's rect (64 × 24, 200 × 100, 10 × 5);
- the text rect (stretched with offsets 0, inset by 10 and 6, moved, Geyser Control's
  top-anchored one, a fixed 50 × 10 at the centre);
- the viewport (stretched with offsets, fixed 40 × 10 at the centre, moved, anchored to part
  of the field, larger than the field, with `RectMask2D` padding, not assigned);
- the text's margins, and the text: "1915", "" and longer ones;
- with and without a placeholder (20 pt, wider and taller than the text);
- each line type, `characterLimit` 0 and 2, and no text component.

Each value is read at once, after a forced rebuild and a frame later. The same fields are then
laid out through four forced passes and three frames: in Geyser Control's row, under a
`HorizontalLayoutGroup` with `childControlHeight` on, off, and on with
`childForceExpandHeight`, with a `ContentSizeFitter` on the field, and in a row whose own
`ContentSizeFitter` fits its height. `tools/UnityProbe/il-check.py rule LOG` checks the rule
below against a log, and `il-check.py diff ORIG OURS` compares two. The rule reproduces all 888
lines of the shipped package's log.
- **The rule.** With a text component:
  - `preferredWidth` is the text component's `preferredWidth` + (viewport `offsetMin.x` −
    viewport `offsetMax.x`).
  - `preferredHeight` is the text component's `preferredHeight` + (viewport `offsetMin.y` −
    viewport `offsetMax.y`).
  - The offsets are the viewport's own, as set relative to its anchors. With no viewport they
    count as 0.
  - `minWidth` and `minHeight` are 0, `flexibleWidth` and `flexibleHeight` -1, and
    `layoutPriority` 1. `CalculateLayoutInputHorizontal` and `CalculateLayoutInputVertical`
    change none of them.
  - Without a text component, both preferred values are 0.
- **Not the rects.** Neither the field's rect nor the text component's enters the value. The
  same text gives the same value at 64 × 24, 200 × 100 and 10 × 5, and in every text rect,
  including a negative one (Geyser Control's -1 high, or -10 × -7 in a 10 × 5 field). So
  laying the field out cannot change its own preferred size, and repeated passes give the
  same value: every pass in every layout above reports the same preferred size as the first.
- **The viewport's offsets, not its rect.** A viewport fixed at 40 × 10 in the field's centre
  has offsets (-20, -5) and (20, 5), so the field is 32.09 − 40 = -7.91 wide and
  19.04 − 10 = 9.04 high. That holds in a 64 × 24 field and in a 200 × 100 one, and when the
  viewport is moved by (6, 2), since its offsets differ by the same amounts. The value is not
  clamped: -7.91 is reported, and `LayoutUtility.GetPreferredWidth` makes it 0 (as for any
  value below the minimum). A viewport larger than the field (offsets (-5, -3) and (7, 4)) gives
  32.09 − 12 = 20.09 by 19.04 − 7 = 12.04. A viewport anchored to the middle half of the field
  with offsets 0 adds nothing. The `RectMask2D`'s padding adds nothing either.
- **The text's own preferred size,** as "Text layout" and the sections above give it. Its
  margins are in it (margins 4, 2, 6, 3 give 42.09 × 24.04). An empty field's text is the lone
  U+200B: 0.01 × 19.04. The line type enters only through the text: a multi-line type turns
  wrapping on, so "abc def ghi jkl mno" in a 64-wide text is 124.8 × 57.17 (three lines), as
  its text component reports.
- **The placeholder does not count,** shown or not: an empty field with a placeholder 293.81 ×
  27.19 is 0.01 × 19.04, and so is the same field without it.
- **`characterLimit` does not count** beyond what it does to the text.
- **In layouts,** the field is laid out like any element of priority 1 with these values:
  - Under a `HorizontalLayoutGroup` controlling height, the field "1915" becomes
    32.09 × 19.04.
  - With height control off, it is 32.09 wide and keeps its 24.
  - With a `ContentSizeFitter` on the field, it is 32.09 × 19.04 from the next canvas update.
  - Beside Geyser Control's `LayoutElement`, the larger value wins: 64 × 24, the row 24.

Where this implementation deviated before (`TMP_InputField.cs`, the layout element at the end):
- It added to the text's preferred size the difference between the field's rect and the text
  component's rect, at least 0, not the viewport's offsets. That difference changes with the
  field's own size wherever the text rect does not stretch with the field.
- Geyser Control's text is anchored to the top and is -1 high in every field. So the field's
  preferred height was 19.04 + (h + 1), where h is the field's current height: 44.04 at 24. A
  parent that sizes the row to its preferred height makes the field 44.04 high, the next pass
  reads 64.08, and so on. In the probe's fitted row, the passes give 44.04, 64.08, 84.12,
  104.16, 124.2 and then 20.04 more each frame. In the game it kept growing while the side
  screen is open: the UI dump read the row at 33,329.72, and the field's `preferredHeight` was
  82,485.54 when read later.
- Where the text rect does stretch, the value was wrong without growing:
  - a text inset by (-20, -12) gives 52.09 × 31.04 for 32.09 × 19.04;
  - a viewport fixed at 40 × 10 gives 56.09 × 33.04 for -7.91 × 9.04;
  - a viewport larger than the field is clamped to 32.09 × 19.04 for 20.09 × 12.04;
  - a field without a viewport counts the text's offsets (44.09 × 26.04 for 32.09 × 19.04).
- It matched where the viewport and the text both stretch over the field: offsets 0 (32.09 ×
  19.04), or offsets on the viewport with the text at 0 (44.09 × 26.04). It also matched the
  margins, the empty text, the placeholder, the line types and the missing text component.
  In the probe 540 of the 888 lines differ, every one in `preferredWidth` or `preferredHeight`
  first.

| Field (text "1915", NotoSans 14 pt, 32.09 × 19.04) | shipped | this implementation before |
|---|---|---|
| Geyser Control: 64 × 24, text anchored to the top, size delta (-3, -1) | 32.09 × 19.04 | 35.09 × 44.04 |
| the same, the field 200 × 100 | 32.09 × 19.04 | 35.09 × 120.04 |
| the same in a row fitted to its height, passes 1 to 4 | 19.04 every pass | 64.08, 84.12, 104.16, 124.2 |
| the same in Geyser Control's row (`LayoutElement` min 64 × 24) | row 24 | row 44.04; in the game 33,329.72 |
| text and viewport stretched, offsets 0, any field size | 32.09 × 19.04 | 32.09 × 19.04 |
| text inset (size delta (-20, -12)) | 32.09 × 19.04 | 52.09 × 31.04 |
| viewport offsets (5, 3) and (-7, -4) | 44.09 × 26.04 | 44.09 × 26.04 |
| viewport anchored (0.25, 0.5)–(0.75, 1), offsets (2, 1) and (-3, -4) | 37.09 × 24.04 | 69.09 × 36.04 |
| viewport fixed 40 × 10 at the centre | -7.91 × 9.04 | 56.09 × 33.04 |
| viewport offsets (-5, -3) and (7, 4), larger than the field | 20.09 × 12.04 | 32.09 × 19.04 |
| no viewport, text inset by (5, 3) and (7, 4) | 32.09 × 19.04 | 44.09 × 26.04 |
| `RectMask2D` padding (4, 3, 2, 1) | 32.09 × 19.04 | 32.09 × 19.04 |
| text margins (4, 2, 6, 3) | 42.09 × 24.04 | 42.09 × 24.04 |
| empty text, Geyser Control's text rect | 0.01 × 19.04 | 3.01 × 44.04 |
| empty text, a placeholder 293.81 × 27.19 shown | 0.01 × 19.04 | 0.01 × 19.04 |
| `MultiLineNewline`, "abc def ghi jkl mno" in 64 wide | 124.8 × 57.17 | 124.8 × 57.17 |
| no text component | 0 × 0 | 0 × 0 |
| min, flexible, priority (every case) | 0, -1, 1 | 0, -1, 1 |

- **Implemented.** This implementation follows the rule: the text's preferred size plus the
  viewport's `offsetMin` − `offsetMax`, 0 without a viewport, no rect read and no clamp. In the
  probe `il-check.py rule` holds for all 888 lines of its log. Against the shipped package's
  log the only values that differ are the inactive fields' `characterLimit` and `SingleLine`
  cases below (60 lines), which are outside the rule.

Also seen, outside the rule: `text` set from code on a field that is still inactive (as the probe
builds them) keeps what `characterLimit` and `SingleLine` would refuse from typing. "1915"
with a limit of 2 stays "1915", and "ab\ncd" in a `SingleLine` field keeps its line feed and is
two lines (16.45 × 38.1). This implementation cuts them to "19" and "abcd". "Text set from
code" below measures this in every state: the shipped package keeps the value whole on active
and focused fields too.

Not measured: a scaled or rotated viewport, a viewport that is not the field's child,
`ForceLabelUpdate` between passes, the vertical scrollbar, and auto-size text.

### Text set from code (measured 2026-10-23)

Found in a mod play-test: with its layout fixed, Geyser Control's slider fields still showed
cut values. `SliderSet.SetTarget` gives each field a `characterLimit`
(⌊1 + log10(max + decimal places)⌋, 4 for mass per cycle), and then `KNumberInputField.SetDisplayValue`
sets `field.text` to the value. The shipped package keeps "507.2", "63.6", "65208.3" and
"61.3", in the field's `text` and in its label. This implementation cut them to "507.",
"63", "65208." and "61".

Measured with `PROBE_SCENARIO=inputtext` (tools/UnityProbe, `InputText.cs`, lines `IT …`): 438
cases. Each field is a `KInputTextField` built as the slider row's `NumberInputField` is
(an `Image`, "Text Area" with a `RectMask2D`, "Text" anchored to its top), with
`onFocusSelectAll` off. Its line type, content type and limit are set first, as `SetTarget`
sets them, or one of them after the text. Then the text is set, in one of three ways:
`text =`, `SetTextWithoutNotify`, or the text component's own `text`. The field is in one of
three states then: inactive, active but not focused, or focused (selected and activated two
frames before).

The cases vary:
- Geyser Control's values with limit 4 and `DecimalNumber`;
- the limits 0, 1 and 4;
- each line type with a line feed, a tab and a CR;
- each content type (Standard, Autocorrected, IntegerNumber, DecimalNumber, Alphanumeric,
  Name, EmailAddress, Password, Pin) with "-12.5e3", "ab c!1" and "x\ny";
- a limit with `MultiLineNewline`.

Each field is then followed step by step: a frame on, being enabled (when inactive), activation
(selected and `ActivateInputField`, two frames), an edit (End, then a typed "9", then
Backspace, through `ProcessEvent`) and deselection. Every step logs the field's `text`, its
label, `caretPosition` and the `onValueChanged`, `onEndEdit` and `onSubmit` reports.
`tools/UnityProbe/it-check.py rule LOG` checks the rule below, and `it-check.py diff ORIG OURS`
compares two players' logs. The rule holds for all 438 cases in the shipped package's log.
- **`text =` and `SetTextWithoutNotify` keep the value whole.**
  - `characterLimit` does not cut it: "507.2" with limit 4 stays "507.2", and "abcdefg" with
    limit 1 stays "abcdefg".
  - `SingleLine` keeps line feeds and tabs: "ab\ncd" stays two lines, "a\tb" keeps its tab.
  - No content type validates or changes it: IntegerNumber keeps "ab c!1", Name keeps
    "-12.5e3".
  - This holds in every state: inactive, active, and focused while being edited.
  - The label becomes the value followed by U+200B ("507.2​"). With `Password` and `Pin` the
    label is one "*" per character, a line feed included ("x\ny" shows "***​").
- **Nothing applies them later.** The text stays whole a frame on, when the field is enabled,
  on activation and focus, and on deselection.
  - Setting `characterLimit`, `contentType` or `lineType` after the text changes nothing either:
    "507.2" then limit 4 stays "507.2", and no `onValueChanged` is sent.
- **Only an edit is limited and validated, one typed character at a time.**
  - A typed character is refused while the text is as long as the limit or longer: "507.2" with
    limit 4 stays "507.2" when "9" is typed, with no `onValueChanged`. "12" becomes "129".
  - Deleting is not limited: Backspace on "507.2" gives "507.", and the rest is not cut.
  - The content type checks only the typed character against the text around it. IntegerNumber
    takes "9" at the end of "-12.5e3", giving "-12.5e39"; Name refuses it.
  - The rest of the text is never checked again: "x\ny" keeps its line feed in a `SingleLine`
    field through every edit.
- **`onValueChanged`:**
  - `text =` reports the whole value once, at once, in every state, including inactive fields.
  - `SetTextWithoutNotify` reports nothing.
  - Edits report the edited text.
  - Backspace on an empty field still reports `""` once (Name, after its refused "9"), though
    nothing changed.
  - Deselecting reports `onEndEdit` with the text as it is ("507." after the Backspace).
- **The text component's own `text`:**
  - On an enabled field (active or focused), it is put back at once: right after
    `textComponent.text = "507.2"` it reads back as the field's text and U+200B ("​" for an
    empty field). The field's `text` stays "". No event is sent.
  - On an inactive field it keeps the value until the field is enabled, which writes the
    field's text and U+200B, as "when the label and the caret are set up" above describes.
  - Edits work on the field's `text`, not on the label: "9" typed afterwards gives "9".
- **Activation** with `onFocusSelectAll` off leaves the caret where it was, at 0
  (`caretPosition` 0, `selectionAnchorPosition` 0), not at the end. End then moves it to the
  end of the caret's line as laid out, not of the text. In a `SingleLine` field "ab\ncd" takes
  "9" after "ab". In a 64-wide multi-line field "a\tb" wraps (the tab reaches 364.58), and "9"
  goes in after "a".

Where this implementation deviated before (`TMP_InputField.cs`: `SetText` and the
`characterLimit` setter):
- **`SetText` cut.** `text =` and `SetTextWithoutNotify` removed line feeds and tabs in a
  `SingleLine` field (so "x\ny" became "xy" under every single-line content type), and cut
  the value to `characterLimit`. `onValueChanged` and the label carried
  the cut value. That is Geyser Control's "507." and "65208." (the game's limits differ by row).
  CR was kept, as in the shipped package.
- **The `characterLimit` setter cut** the text when the limit fell below its length, and
  sent `onValueChanged` with the cut text.
- **The text component's own text** stayed as set on an enabled field until activation wrote
  the label, where the shipped package puts it back at once.
- **Activation** put the caret at the end of the text (7 in "abcdefg"), not at 0, in all 132
  activations of a field with text.
- **Backspace** on an empty field sent nothing.
- **End** in the wrapped multi-line "a\tb" went to the end of the text ("a\tb9").
- It matched elsewhere: `SetTextWithoutNotify` sent nothing, a typed character was refused at
  the limit, CR was kept, setting the text on a focused field left the caret at 0, the label
  of an inactive field was kept until enable, and `onEndEdit` came on deselection.
- In the probe, 1,628 of 4,428 steps differed. 338 of 438 cases followed the rule, and the 100
  that did not were the limit and line-feed cases above.

| Field, text set from code | shipped: `text`; `onValueChanged` | this implementation before |
|---|---|---|
| limit 4, `DecimalNumber`, `text = "507.2"` (inactive, active or focused) | "507.2"; "507.2" | "507."; "507." |
| the same, "65208.3" | "65208.3"; "65208.3" | "6520"; "6520" |
| the same, "12", then "9" typed | "12", then "129" | the same |
| the same, "507.2", then "9" typed | "507.2", "9" refused, no event | "507.", "9" refused |
| the same, "507.2", then Backspace and deselect | "507."; `onEndEdit` "507." | "507"; `onEndEdit` "507" |
| limit 4, `SetTextWithoutNotify("507.2")` | "507.2"; nothing | "507."; nothing |
| `text = "507.2"`, then `characterLimit = 4` | "507.2"; nothing more | "507."; "507." |
| limit 1, `text = "abcdefg"` | "abcdefg" | "a" |
| limit 3, `MultiLineNewline`, "a\nbcd" | "a\nbcd" | "a\nb" |
| `SingleLine`, "ab\ncd" | "ab\ncd" (two lines) | "abcd" |
| `SingleLine`, "a\tb" | "a\tb" | "ab" |
| `SingleLine`, "ab\rcd" | "ab\rcd" | "ab\rcd" |
| `IntegerNumber`, "ab c!1" | "ab c!1" | "ab c!1" |
| `Password`, "x\ny" | "x\ny", label "***​" | "xy", label "**​" |
| `textComponent.text = "507.2"`, active field with text "" | label "​" at once, `text` "" | label "507.2" until activation |
| the same, inactive field | label "507.2" until enable, then "​" | the same |
| activation, "abcdefg", `onFocusSelectAll` off | caret 0 | caret 7 |
| Backspace in an empty field | `onValueChanged` "" | nothing |

- **Implemented.** `text =` and `SetTextWithoutNotify` keep the value whole (only `\0` is
  removed, as before). The `characterLimit`, `contentType` and `lineType` setters leave the
  text alone and send nothing. Typed characters are limited and validated as before.
  - A text written into an enabled field's text component is put back at once, from the text
    component's dirty-vertices callback.
  - Activation with `onFocusSelectAll` off leaves the caret where it was.
  - The End key goes to the end of the caret's laid-out line, before its last character (the
    line feed, or the white space a wrapped line ends in). On the last line it goes to the
    last laid-out character, as before. `MoveTextEnd` itself is unchanged.
  - Backspace in an empty field reports `""`.
  - In the probe, `it-check.py rule` holds for all 438 cases of our log. `it-check.py diff`
    against the shipped package's log finds none of the 4,428 steps different. Tests are in
    `TmpInputTextTests`.
- **Also measured by that diff:** setting `characterLimit` writes the label again, also on an
  inactive field. Take an inactive field whose text component has its own "507.2": setting
  `characterLimit = 4` makes the label "​" (the field's text "" and U+200B) at once, before
  the field is enabled. This implementation does the same.

Not measured: `onValidateInput` and `inputValidator` (the probe's fields have neither; some of
the game's fields do), `characterValidation` set without a content type, pasting, `text =` with the value the field already holds, rich text, and
`readOnly`. Measure them the same way before relying on them.

### Font assets from OS fonts (measured 2026-10-23)

Found in a mod play-test (issue #2): Geyser Tab (Workshop 3746027329) opens no window. Building
the window, it gives its Refresh button's "↻" label an OS font. It calls
`TMP_FontAsset.CreateFontAsset("Segoe UI Symbol", "Regular", 60)`, then "Arial", "Malgun
Gothic" and "Tahoma", inside a try/catch. This implementation lacks that overload. So the
method that calls it fails to compile (`MissingMethodException`), the catch never runs, and
the whole window's build is abandoned. In the original on this Linux machine, none of the four
is installed. Each call returns null, and the label keeps the game's font.

**Interface, from the package's metadata:** two public overloads that this implementation
lacks:
- `static TMP_FontAsset CreateFontAsset(string familyName, string styleName, int pointSize =
  90)`;
- `static TMP_FontAsset CreateFontAsset(string fontFilePath, int faceIndex, int
  samplingPointSize, int atlasPadding, GlyphRenderMode renderMode, int atlasWidth, int
  atlasHeight)`.

Measured with `PROBE_SCENARIO=fontasset` (tools/UnityProbe, `FontAsset.cs`, lines `FA …`, 10 s
into the main menu), on this machine's fonts (DejaVu, FreeFont, TLWG, Unifont, IPA Gothic, …).
`Application.logMessageReceived` caught what was logged.

**By family and style name:**
- **Lookup:** the face comes from the engine's `FontEngine.TryGetSystemFontReference(familyName,
  styleName, out FontReference)`, which is internal in Unity's engine too. Called by
  reflection, it gave the same faces. The engine provides it here; it is in this project's
  engine code.
  - Family and style match without regard to case ("dejavu sans", "BOOK"). Names must be
    whole: "DejaVuSans" finds nothing. A localized family name finds nothing ("IPAゴシック"
    for IPAGothic).
  - A style the family lacks finds nothing: "DejaVu Sans" has no "Regular" (its faces are Book
    and Bold).
  - An empty or null style finds the family's "Regular" face when it has one (FreeSans, Loma,
    IPAGothic, Unifont). Otherwise it finds some face of the family: Book for DejaVu Sans,
    Bold for DejaVu Sans Mono. The engine gives the family's first face then.
- **Not found** (also with an empty or null family): it returns null and logs one line through
  `Debug.Log` (a Log, not a warning): `Unable to find a font file with the specified Family
  Name [Arial] and Style [Regular].` A null family prints as `[]`, and so does an empty style.
  Nothing is thrown.
- **Found:** a new asset on each call. Two calls for the same face give two assets with two
  atlas textures.
  - `name` "", `hideFlags` None, `version` "1.1.0", `sourceFontFile` null.
  - `faceInfo` is the face's at `pointSize`, with its own family and style names (for "dejavu
    sans"/"book": "DejaVu Sans"/"Book") and scale 1. These are the engine's
    `FontEngine.GetFaceInfo()` values. DejaVu Sans Book at 60: line height 69.84375, ascent
    55.6933556, descent −14.15039, cap line 44, mean line 33, tab width 19.
  - A `pointSize` of 0 or below loads the face at its units per em: DejaVu Sans gives
    `pointSize` 2048, line height 2384, ascent 1901.
  - Atlas: 1024 × 1024, padding 9, `atlasRenderMode` SDFAA, `atlasPopulationMode` DynamicOS,
    `isMultiAtlasTexturesEnabled` true. `atlasTextures` holds one texture: 1 × 1, Alpha8, named
    "", which is also `atlasTexture`. It is resized to the atlas size when the first glyph is
    added.
  - `material`: a new material on "TextMeshPro/Mobile/Distance Field", named as its shader.
    `_MainTex` is the atlas texture. `_TextureWidth` and `_TextureHeight` are 1024,
    `_GradientScale` is the padding + 1 (10), `_WeightNormal` 0 and `_WeightBold` 0.75.
  - `creationSettings` is the default (null GUID, zeros, `includeFontFeatures` false).
  - `characterTable` and `glyphTable` are empty, and `fallbackFontAssetTable` is null.
  - `normalStyle` 0, `normalSpacingOffset` 0, `boldStyle` 0.75, `boldSpacing` 7,
    `italicStyle` 35, `tabSize` 10. `fontWeightTable` has 10 entries.

**By file path:**
- The face at `faceIndex` of the file, at `samplingPointSize`, with the given padding, render
  mode and atlas size. `atlasPopulationMode` is Dynamic here, not DynamicOS. `_GradientScale`
  is the padding + 1 (7 for 6), and `_TextureWidth`/`_TextureHeight` are the atlas size. The
  rest is as above (DejaVuSans.ttf, 0, 60, 6, SDFAA, 512 × 512; and 0, 90, 9, SDFAA, 1024 ×
  1024).
- A missing file, a file that is not a font (/etc/hostname), an empty path or a `faceIndex`
  past the file's faces: it returns null and logs `Unable to load font face from [PATH].`
  (`Debug.Log`).
- `RASTER_HINTED` throws `ArgumentNullException` ("Value cannot be null.") and returns no
  asset. The game ships no bitmap TMP shader.

**In use** (a `TextMeshProUGUI` at 16 pt, no wrapping, on DejaVu Sans Book at 60):
- The first text adds U+2026 (…) and U+005F (_) to the asset, then the text's characters.
  After "↻", `characterTable` is [U+2026, U+005F, U+21BB], and the atlas is 1024 × 1024. A
  following "A" adds U+0041.
- "↻" is drawn from the asset: preferred width 13.41, height 18.63, advance 13.4083328. "A" is
  10.95 wide.
- A character the face lacks (U+AC00) is not added. It comes from the global fallbacks
  (NotoSansCJKkr-Regular), and the table stays as it was.
- `HasCharacter('↻')` is then true, `TryAddCharacters("↻Z")` returns true, and
  `TryAddCharacters("가")` returns false with `missing` "가".

Not measured: Windows and macOS lookups (the families Geyser Tab tries exist there), and
another call's effect on an existing asset of the same face.

### Sub-meshes and raycasts (measured 2026-10-23)

Found in a user's video: the tool cursor's icon and hover card vanished over empty world while
a Demolior impactor reveal ran, because the event system reported the pointer over UI. In this
implementation the topmost raycast hit at the screen's centre was
`LargeImpactorSequenceReticle/CalculatingImpactSection/label/TMP SubMeshUI [GRAYSTROKE REGULAR
SDF Material + NotoSansCJKsc-Regular Atlas]`. That is a sub-mesh saved in the reticle prefab:
its label shows no fallback glyph, and the sub-mesh's empty rect covers the label. A mod
play-test found the same independently (Duplicant Status Bar): the "New Game Object" sub-meshes
of its header labels had `raycastTarget` true here and false in the shipped package. Their
parent texts have it false.

Measured with `PROBE_SCENARIO=submesh` (tools/UnityProbe, `SubMesh.cs`, lines `SM …`), at the
main menu, on an overlay canvas of its own with a `GraphicRaycaster`. "Hit" below means that
`EventSystem.RaycastAll` at the graphic's rect centre returns it. The cases:
- **Texts:** GRAYSTROKE REGULAR SDF 12 pt and NotoSans-Regular 14 pt, 400 × 100, with
  "AB中文", "中文" and "AB▼". The CJK glyphs come from NotoSansCJKsc-Regular and the "▼" from
  another fallback, through the global fallback list. Each text makes one sub-mesh.
- **Steps (`reg` lines):** 21 steps, each on a fresh text whose `raycastTarget` is on or off.
  They cycle the enabled state and the activity of the sub-mesh and of the text, in the same
  frame and a frame apart. They cycle the sub-mesh's own `raycastTarget` (false, then true), and
  combine that cycle with the other ones. They relayout with the sub-mesh's or the text's
  `raycastTarget` set off first. They take the fallback glyphs away and bring them back, change
  to another fallback and reparent the text.
- **Controls (`img` lines):** an `Image` made the same ways.
- **By hand (`hand` lines):** a `TMP_SubMeshUI` added by hand, before and after parenting.
- **Saved sub-meshes:** the ten loaded prefab sub-meshes, copies of three prefab labels, and
  a copy of the whole `LargeImpactorSequenceReticle`.

The shipped package:
- **A sub-mesh is not a raycast target until its `raycastTarget` changes to true.**
  - Enabling does not list it with the canvas's raycast targets, unlike every other
    `Graphic`. Take a sub-mesh made by a layout with `raycastTarget` true, drawn (depth 1),
    covering the text's rect: it is never hit, through every later frame. The same holds for
    the enabled and activity cycles of the sub-mesh or the text, a reparent, a relayout, and
    fallback glyphs taken away and brought back.
  - A `TMP_SubMeshUI` added by hand (`raycastTarget` true, its own default) is not hit
    either, whether it was added before or after parenting.
  - The `Image` controls are hit from the next frame in every one of those cases, so the rule
    belongs to the sub-mesh, not to the raycaster.
  - Setting `raycastTarget` false and then true lists it: from then on it is hit, with or
    without glyphs (an empty sub-mesh is hit over its whole rect).
  - Setting it to the value it already has does nothing: true on a fresh sub-mesh leaves it
    unlisted.
- **After it is listed:**
  - Cycling the sub-mesh's own enabled state, or the text's object's activity, drops it from
    the list, and it is not hit again until the next change to true.
  - Cycling the text component's enabled state keeps it listed.
  - Setting `raycastTarget` false drops it.
  - A listed sub-mesh destroyed with its text stays in the canvas's list: every later raycast
    on that canvas throws a `NullReferenceException` from `CanvasRenderer.cull`, and the input
    module's do too. Do not reproduce this. Drop a destroyed sub-mesh from the list.
- **The text copies its `raycastTarget` into the sub-meshes it fills,** through the property,
  so a change lists or drops the sub-mesh as above:
  - **At creation:** a text with `raycastTarget` false makes its sub-mesh with false; one with
    true makes it with true (unlisted, as above). This is Duplicant Status Bar's false.
  - **At every mesh generation** (`ForceMeshUpdate`, or the canvas rebuild after a change of
    text, of the rect or of enabling) that puts glyphs in that sub-mesh:
    - A sub-mesh set false by hand becomes true again when its text is true. Then it is
      listed and hit.
    - When the text is set false after the sub-mesh exists, the sub-mesh becomes false at
      the next generation, not before.
  - Setting the text's `raycastTarget` alone changes no sub-mesh until that generation.
  - Sub-meshes a generation leaves unused keep their value.
- **Saved sub-meshes** keep the value saved with them until a generation fills them, and they
  are not listed. All ten loaded at the main menu are saved with `raycastTarget` true,
  including the reticle's two labels, whose texts are false. Copies of these labels, and of
  the whole reticle, are never hit. They only become hit after a false-and-true cycle of the
  sub-mesh's own property.
- **Rect:**
  - A sub-mesh made by a layout is stretched over its text: anchors (0, 0)–(1, 1), size delta
    0, position 0. Its rect is the text's.
  - Its pivot is the text's, and it follows changes to the text's pivot at once, so its
    local origin stays the text's. With the text's pivot set to (0, 1), the sub-mesh's pivot
    reads (0, 1) right after.
  - Resizing the text, changing its anchors or margins resizes the sub-mesh with it.
  - Saved sub-meshes carry the same anchors and their text's pivot.
- **Without fallback glyphs:** when the text changes to "AB" or "", the sub-mesh stays active
  and enabled, with its rect, material and depth. Its `mesh` keeps its vertex count and bounds
  from the last fill. When fallback glyphs return, the same sub-mesh (same instance) is
  filled again.
- **Other Graphic values:**
  - `maskable` is copied from the text at creation: false when the text was unmaskable
    before the layout. Setting the text's `maskable` later did not change the sub-mesh within
    the next frame.
  - The colour is white.
  - The layer is 0 (the text's) for those made at run time.
- **Components:** a sub-mesh made at run time has, in order, `RectTransform`, `LayoutElement`
  (`ignoreLayout` true), `CanvasRenderer` and `TMP_SubMeshUI`. It is named "New Game Object",
  with hide flags `DontSave`. Saved ones have no `LayoutElement`. `TMP_SubMeshUI` is not an
  `ILayoutElement`, and `LayoutUtility` gives it preferred sizes 0.
- **The text disabled:** its sub-meshes are disabled with it (`enabled` false), and enabled
  again with it. Deactivating the text's object leaves the sub-mesh's `enabled` false until the
  object is active again.
- **A clone:** `Instantiate` of a text with a sub-mesh gives a copy whose cloned sub-mesh stays
  empty (vertex count 0). The copy's first layout makes a second sub-mesh for the glyphs.

Where this implementation deviated before (`TMP_SubMeshUI.cs`, `TextMeshProUGUI.UploadGeometry`,
and `Graphic.OnEnable` in this project's uGUI):
- **Every enabled sub-mesh was a raycast target.** `TMP_SubMeshUI` listed itself on enable, as
  `Graphic` does. Fresh sub-meshes, hand-added ones and saved ones were all hit from their
  first drawn frame: the reticle's two sub-meshes, and the sub-mesh of every text with a
  fallback glyph. The sub-mesh was the topmost hit over its text, ahead of the text itself.
  This was the bug. The `Image` controls match the shipped package.
- **The text's `raycastTarget` was never copied.** A sub-mesh made under a text with false was
  true (Duplicant Status Bar's dump). One set false by hand stayed false through relayouts, and
  the text set false left its sub-meshes true.
- **The pivot was set only at creation:** after the text's pivot changed, the sub-mesh kept
  (0.5, 0.5), and its local position moved (150, -30 for a 300 × 60 text pivoted to (0, 1)),
  so its local origin was no longer the text's.
- **`maskable`** was not copied: true under an unmaskable text.
- **Hide flags** were `None` (`TMP_Settings.hideSubTextObjects` is false), not `DontSave`.
- **The text disabled** left its sub-meshes enabled (only their renderers were cleared).
- **Without fallback glyphs** the sub-mesh's mesh was cleared (vertex count 0). Nothing the
  probe measures depends on this. Whether the shipped package still draws the stale
  vertices was not measured; it should draw nothing.
- **A clone** adopted the cloned sub-mesh and filled it, so it had one sub-mesh, not two.
- It matched elsewhere: the anchors and the rect, the components and their order, the
  `LayoutElement` with `ignoreLayout`, the name, reuse of the same sub-mesh when glyphs
  return, and the saved sub-meshes' serialized values.

| Case (GRAYSTROKE 12 pt, "AB中") | shipped: sub-mesh `raycastTarget`, hit | this implementation before | now |
|---|---|---|---|
| text true, sub-mesh made by the layout | true, not hit | true, hit | true, not hit |
| text false, sub-mesh made by the layout | false, not hit | true, hit | false, not hit |
| text true; the sub-mesh's or the text's enabled or active cycle | true, not hit | true, hit | true, not hit |
| text true; sub-mesh set false, then true | true, hit | true, hit | true, hit |
| the same, then the sub-mesh's enabled cycled | true, not hit | true, hit | true, not hit |
| the same, then the text's object's activity cycled | true, not hit | true, hit | true, not hit |
| the same, then the text component's enabled cycled | true, hit | true, hit | true, not hit (dropped on disable) |
| text true; sub-mesh set false, then a relayout | true, hit | false, not hit | true, hit |
| text set false after the layout, then a relayout | false, not hit | true, hit | false, not hit |
| text set false after the layout, no relayout | true, not hit | true, hit | true, not hit |
| text true; fallback glyphs removed and returned | true, not hit | true, hit | true, not hit |
| text true; sub-mesh false, glyphs removed and returned | true, hit | false, not hit | true, hit |
| `TMP_SubMeshUI` added by hand | true, not hit | true, hit | true, not hit |
| `Image` made the same ways (control) | true, hit | true, hit | true, hit |
| reticle prefab: CalculatingImpactSection label (text false, saved true, empty) | true, not hit | true, hit | true, not hit |
| language menu "Uninstall" label (text true, saved true, empty) | true, not hit | true, hit | true, not hit |
| either saved one after its own false-and-true cycle | true, hit | true, hit | true, hit |
| text pivot changed to (0, 1) | pivot (0, 1), position 0 | pivot (0.5, 0.5), position moved | pivot (0, 1), position 0 |
| text unmaskable before the layout | `maskable` false | true | false |
| text component disabled | sub-mesh disabled | sub-mesh enabled | sub-mesh disabled |

The "now" column is the submesh probe in this player (2026-10-23), and `TmpSubMeshTests`.

- **Implemented** (`TMP_SubMeshUI.cs`, `TextMeshProUGUI.cs`; `Graphic` is unchanged):
  - `TMP_SubMeshUI` does not list itself as a raycast target when enabled. The
    `raycastTarget` property lists it when it changes to true while active and enabled, and
    drops it when it changes to false.
  - It is dropped on disable and on destroy as well. This differs from the shipped package only
    in its failure cases: the stale entry, and the text component's enabled cycle keeping it.
  - A text sets each sub-mesh it creates or fills to its own `raycastTarget` through that
    property, and sets `maskable` from its own at creation.
  - The sub-mesh's pivot follows its text's.
  - Sub-meshes made at run time are `DontSave`. A text adopts only the sub-meshes saved with it
    (without `DontSave`), so a clone's copy of a run-time sub-mesh stays empty and the clone
    makes its own.
  - The text's disable disables the sub-meshes it holds, and its enable enables those again.
  - An unused sub-mesh keeps its mesh; its renderer is given an empty mesh, so it draws
    nothing.
  - The expected effect in the game: a sub-mesh is hit only after code sets its
    `raycastTarget` from false to true, directly or through its text's copy. With these rules
    the reticle's sub-meshes are not hit, and Duplicant Status Bar's are false, as in the
    shipped package. Whether any game or mod code does that was not surveyed.
    `tests/e2e/demolior-pointer.yaml` checks the reticle.

Not measured:
- world-space `TMP_SubMeshUI` (`TextMeshPro`, not UGUI);
- sprite sub-meshes from a layout (only the saved "TextMeshPro/Sprite" one is listed);
- the text's `maskable` changed after the sub-mesh exists and followed by a relayout;
- whether the stale vertices of an unused sub-mesh are drawn.

### Words and their separators (measured 2026-10-23)

Found by a mod's play-test (Geyser Tab, `tests/e2e/mods/3746027329-geyser-tab.yaml`): its geyser
list shows a `TextMeshProUGUI` (NotoSans-Regular 14 pt) with "Liquid Sulfur Geyser NJ24‑7",
the character before the 7 being U+2011, the non-breaking hyphen. `textInfo.wordCount` is 4 in
the shipped package and 5 here: this implementation ended a word at the U+2011.

Measured with `PROBE_SCENARIO=wordcount` (tools/UnityProbe, `WordCount.cs`, lines `WC …`), at
the main menu. It used one `TextMeshProUGUI` per font, set up as `SetTextStyleSetting` does,
in NotoSans-Regular 14 pt and GRAYSTROKE REGULAR SDF 12 pt. Each case logs `wordCount`, each
word's first and last character index and character count, each line's `wordCount`, and the
characters' codes and visibility. The cases:
- **Texts** (unwrapped, 400 × 100): the hyphens and dashes U+002D, U+00AD, U+2010–U+2015 and
  U+2212; the spaces U+00A0, U+202F and U+2007; apostrophes and quotes; "NJ24", "24-7", digits
  mixed with letters, decimals, underscores, CJK and Hangul, rich-text tags, sprites, emoji,
  combining marks; texts without words; texts ending in an apostrophe.
- **A sweep** of every character in U+0021–U+007E, U+00A0–U+017F, U+2000–U+206F and
  U+3000–U+303F, and some others (U+0301, U+02BC, U+2E3A, U+30FB, U+FE63, U+FF07, U+FF0D,
  U+FF3F, …). Each X was laid out as "abXcd", "Xab", "abX", "12X34" and X alone, plus a few
  texts with rich text off.
- **Wrapped texts:** "aaaa", X, "bbbb" for the separators above, "aa-bb-cc-dd", the same with
  U+2011, "NJ24‑7 NJ24-7", and the geyser's name with either hyphen. Each was laid out in rects
  from 1 to 2 past its preferred width, in steps of 0.25, logging each distinct set of breaks.

Two runs of the original logged the same lines. `tools/UnityProbe/wc-check.py rule LOG`
replays a log against the rule below: it reproduces all 6093 word lists and all 6035 checked
line word counts of the second run, and all 5985 and 5929 of the first. `wc-check.py diff ORIG
OURS` compares two players.

**The rule.** The laid-out characters are taken one by one: tags are not characters, a sprite
is one, and a character the fonts lack is its replacement, U+25A1 (an emoji too). The rule
uses each character's category in the Unicode tables:
- **Word characters:**
  - Letters (any L category, so ideographs, kana and Hangul too) and decimal digits (Nd).
  - Four hyphens: U+002D, the soft hyphen U+00AD, U+2010 and U+2011. They count anywhere in a
    word, as letters do: "a-b", "a‑b", "-a", "‑a", "a‑" and "--" are one word each, from the
    first to the last character, and "-" alone is the word (0, 0).
  - Nothing else: not the other dashes (U+2012–U+2015, U+2212, U+FE63, U+FF0D), "_", the
    non-breaking spaces, superscripts and fractions (No), letter numbers (Nl: U+3007, the
    Hangzhou numerals), or combining marks ("e", U+0301, "a" is two words).
  - So "NJ24‑7" and "24-7" are one word, "ab中cd" is one word, "中。文" is two, and
    "1,000" is "1" and "000".
- **Apostrophes** (U+0027 and U+2019, not U+2018, `"`, "`" or U+FF07) join when the character
  before and the character after are both letters or digits. Hyphens don't count here:
  - "don't", "a'1" and "1'a" are one word;
  - "a''b" is two words, "ab'-" is "ab" and "-", and "ab-'" is "ab-";
  - an apostrophe at the start doesn't join: "'tis" is 1–3.
- **An apostrophe at the end** after a letter or digit:
  - The character after it is read from the character slot past the text. That slot holds
    whatever an earlier layout of the same component left there.
  - If it holds a letter or digit, the apostrophe joins and the word never ends: it is not
    counted, not recorded and not counted on its line. "ab cd'" has one word, "ab".
  - Otherwise the apostrophe ends the word as any separator does: "a'" is (0, 0) when the
    slot held "'".
  - In the probe, "ab'", laid out after a text with "a" at index 3, has word count 1 and line
    word count 0, and its record is the previous text's (rule for texts without words below).
- **A word ends** at the first separator after it, or at the text's last character. Its record
  is (first, last, last − first + 1). It is counted on the line of the character that ended
  it: the separator, or the last character. A word broken across lines is one word, counted on
  the line where it ends:
  - "aaaa-bbbb" broken "aaaa-|bbbb" counts 0, 1;
  - "aaaa/bbbb" broken "aa|aa|/b|bb|b" (GRAYSTROKE, 13 wide) counts 0, 0, 1, 0, 1;
  - "aaaa", U+00A0, "bbbb" broken after the U+00A0 counts 1, 1.
- **The empty word** (0, −1, count 0), counted on line 0, comes first in two cases:
  - When the first character is a separator that is not punctuation: white space, a symbol, a
    sprite, a combining mark, U+25A1. So " ab" and "+ a" have two words, and "(a)", "%a", "#1"
    and "'tis" have one.
  - When the text is a single character that is not a word character, punctuation included:
    "(", "." and U+00A0 alone are (0, −1).
- **Punctuation** is any P category, except § (U+00A7) and ¶ (U+00B6), which count as symbols:
  "§a" is (0, −1) and (1, 1).
- **A text without words** (two or more characters, starting with punctuation, none of them a
  word character, or a text whose only word never ended) has word count 1. It writes no
  record: `wordInfo[0]` keeps the previous layout's, and no line counts a word. ".." laid out
  after "#1" reports (1, 1); "()" after "(" reports (0, −1). An empty text has word count 0.

**Line breaking**, measured with the same probe:
- **U+2010 and U+2011** give no break opportunity; a line that must break there fills by
  characters. "aa‑bb‑cc‑dd" in 28.5 (NotoSans) is "aa‑|bb‑c|c‑dd", where "aa-bb-cc-dd" in
  35.25 is "aa-|bb-|cc-dd". This implementation matches.
- **U+2007 and U+202F** allow a break after them, as a space does; U+00A0 does not. "aaaa",
  X, "bbbb" in 19.5 (GRAYSTROKE) is "aaa|aX|bbb|b" for U+2007 and U+202F, and "aaa|aXb|bbb"
  for U+00A0.
- **A drawn soft hyphen** reports `character` U+002D wherever it is drawn at a break, and a
  hidden one U+00AD. (This settles the "sometimes" under "Soft hyphens and the line's height"
  above: it is drawn exactly when its glyph fits the line.)

**Implemented** (`RecordWords` in `TMP_Text.Place.cs`, the break opportunities in
`TMP_Text.Layout.cs` and a drawn soft hyphen's `character` in `TMP_Text.Place.cs`; tests in
`TmpWordTests`). This implementation follows the rule: in the same probe `wc-check.py rule`
reproduces all 6093 word lists and all 6035 checked line word counts from its log, and
`wc-check.py diff` against the shipped package's second run finds none of the 6093 common
lines different, and no line in one log only. Letters and digits are .NET's
`char.IsLetterOrDigit`, which agrees on every character swept; punctuation is
`char.IsPunctuation` without § and ¶. The slot past the text is the `characterInfo` entry
after the last character, which keeps what an earlier layout wrote there (entries beyond the
count are not cleared), or none (no letter) past the array's end.

Before (2026-10-23) this implementation deviated: U+00AD, U+2010 and U+2011 were separators;
an apostrophe joined any word character after it, with no check of the one before it, and one
at the end always ended its word; a line counted the words that began on it; a single
punctuation character and a text without words recorded (0, 0) and counted it on line 0; §
and ¶ were punctuation; U+2007 and U+202F gave no break opportunity; a drawn soft hyphen kept
`character` U+00AD. It matched elsewhere: the letters and digits, the other dashes, the
non-breaking spaces' word counts, the empty first word, tags, sprites, CJK, and the breaks
around U+2010 and U+2011.

| Case | shipped (and this implementation now) | this implementation before (2026-10-23) |
|---|---|---|
| "Liquid Sulfur Geyser NJ24‑7" (U+2011), NotoSans 14 pt | 4 words; the last 21–26 | 5 words; 21–24, 26–26 |
| "a‑b", "a‐b" (U+2010), "a", U+00AD, "b" | 1 word, 0–2 | 2 words, 0–0 and 2–2 |
| "‑a" | 0–1 | 1–1 |
| "a ‑ b" | 3 words, 0, 2, 4 | 2 words, 0 and 4 |
| "a–b" (U+2013), "a−b" (U+2212), "a_b", "a", U+00A0, "b" | 2 words | 2 words |
| "ab'-" | "ab" 0–1, "-" 3–3 | 0–3 |
| "a'1", "don't" | 1 word | 1 word |
| "ab cd'", slot after the text held "c" | 1 word, 0–1; line count 1 | 2 words; line count 2 |
| "ab'", slot held "a" | 1 word, previous record (0–0); line count 0 | 0–1; line count 1 |
| "(" | (0, −1) | (0, 0) |
| "..", laid out after "#1" | 1 word, record 1–1 kept; line count 0 | (0, 0); line count 1 |
| "+", " ab" | (0, −1) first | (0, −1) first |
| "§a" | (0, −1), (1, 1) | (1, 1) |
| "ab中cd"; "中。文"; "ab<b>cd</b>"; "e", U+0301, "a" | 1; 2; 1; 2 words | 1; 2; 1; 2 words |
| geyser's name (U+2011), NotoSans, 40 wide: "Liqui\|d \|Sulfur \|Geyse\|r \|NJ24‑\|7" | line counts 0,1,1,0,1,0,1 | 1,0,1,1,0,1,1 (and 5 words) |
| "aa-bb-cc-dd", 21.75 wide: "aa-\|bb-\|cc-\|dd" | line counts 0,0,0,1 | 1,0,0,0 |
| "aaaa", U+2007 or U+202F, "bbbb", GRAYSTROKE, 19.5 wide | "aaa\|aX\|bbb\|b" | "aaa\|aXb\|bbb" |
| "aaaa", U+00A0, "bbbb", GRAYSTROKE, 19.5 wide | "aaa\|aXb\|bbb" | "aaa\|aXb\|bbb" |
| "aaaa", U+00AD, "bbbb", NotoSans, 36 wide: "aaaa-\|bbbb" | the soft hyphen drawn, `character` U+002D | drawn, U+00AD |
| "aa‑bb‑cc‑dd", NotoSans, 28.5 wide | "aa‑\|bb‑c\|c‑dd" | the same |

Whole runs before: `wc-check.py rule` on this implementation's log matched 5581 of 6109 word
lists and 5464 of 6051 line word counts; `wc-check.py diff` found 811 of 6093 common lines
differing (82 texts, 236 sweep lines, 493 wrapped layouts).

Not measured:
- the slot past the text, and `wordInfo[0]`, in a component that has laid out no longer text
  (presumably empty: the slot no letter, the record zeros);
- `maxVisibleWords`, `TMP_TextUtilities.FindIntersectingWord` and the input field's
  word-wise caret moves, which use words;
- characters outside the swept ranges whose Unicode category differs between the original's
  tables and .NET's (only § and ¶ were found in them);
- world-space `TextMeshPro`.

### Carriage returns are not counted as spaces (measured 2026-10-24)

Found by a mod's play-test (One Hundred K Challenge, `tests/e2e/mods/2228381429-100k-challenge.yaml`):
the destination screen's description of its world, a `LocText` in NotoSans-Regular 14 pt, ends
its paragraphs with "\r\n" (three of them). The shipped package reports `textInfo.spaceCount`
50 for it, this implementation 53; `characterCount` (283), `wordCount` (49) and the lines agree.

Measured with `PROBE_SCENARIO=wordcount` (see "Words and their separators"; the probe's text
list gained the cases below), unwrapped in 400 × 100, in NotoSans-Regular 14 pt and GRAYSTROKE
REGULAR SDF 12 pt, which agree. Run through `scripts/e2e.py` (a main-menu spec waiting for the
probe's `WcCanvas` to go, with `E2E_ONIMCP` pointing at a copy of OniMcp's folder holding
`SemanticsProbe.dll`); `wc-check.py diff` finds 22 of 6119 common lines differing, these
cases in both fonts and nothing else.

| Text | shipped: `spaceCount` | here before |
|---|---|---|
| "\r", "ab\r", "\rab", "ab\rcd" | 0 | 1 |
| "ab\r\rcd" | 0 | 2 |
| "ab\r\ncd", "ab\r\n", "ab\n\rcd" | 1 | 2 |
| "ab \r\ncd" | 2 | 3 |
| "ab\r\n\r\ncd" | 2 | 4 |
| `"ab.\r\n\r\n<smallcaps>cd ef</smallcaps>\r\n"` | 4 | 7 |
| "ab\u000Bcd", "ab cd" (controls) | 1 | 1 |

**The rule.** CR (U+000D) adds nothing to `spaceCount`: every other white-space character
counted before (space, line feed, tab, the vertical tab, U+2028, …) still counts one. Nothing
else about CR changes: it is still a character of its line (`characterCount` includes it), it
still ends a word ("ab\rcd" has the words 0–1 and 3–4 in both), and a CR alone is still one
empty word record (0, −1), as before.

**Implemented** (the text's `spaceCount` in `TMP_Text.Place.cs`; tests in `TmpWordTests`,
`CarriageReturnsAreNotCountedAsSpaces`). In the same probe `wc-check.py diff` against the
shipped package's run finds none of the 6119 common lines different, and no line in one log
only; `wc-check.py rule` matches all 6119 word lists and 6047 line word counts.

Not measured: CR in wrapped text, in `TMP_InputField`, and in world-space `TextMeshPro`.

### Culling under a RectMask2D (measured 2026-10-23)

`TextMeshProUGUI` culls under a `RectMask2D` with the bounds of its mesh, not with its rect. The
bounds are the box of all the mesh's vertices, the unused ones at the local origin included, so
the pivot is always inside. The cull is re-evaluated when that mesh changes. `ugui.md` ("RectMask2D
culling (measured)") records the measurement, the rule and where this implementation deviates.
The probe also found that the original's `TMP_Text.bounds` equals the mesh's bounds, and that
an empty text keeps 4 vertices.

### Lines that grow part-way (measured 2026-10-24)

Found by issue #12: a geyser's details screen shows no name. The title is a `LocText`
(GRAYSTROKE REGULAR SDF 14 pt, Truncate, no wrapping, top left, extra padding and kerning as
`SetTextStyleSetting` sets them) in a rect 175.13 × 22, showing "Liquid Sulfur Geyser NJ24‑7".
The U+2011 before the 7 comes from a fallback font, and is taller: the line is 14.69 high
without it (GRAYSTROKE's ascender 13.29 and descender −1.40) and 23.82 with it. The shipped
package shows "Liquid Sulfur Geyser NJ24", with `isTextOverflowing` true,
`firstOverflowCharacterIndex` 25, 26 records (the blank record last) and one line. This
implementation reports index 0 and shows nothing: 0 records, 0 lines, no sub-mesh. Every geyser
and vent has such a name, so every one has an empty title.

Measured with `PROBE_SCENARIO=tallline` (tools/UnityProbe, `TallLine.cs`, lines `TL …`), at the
main menu. The texts were 19 lines that grow taller part-way:
- the U+2011 of geyser names ("Liquid Sulfur Geyser NJ24‑7", "Chlorine Gas Vent AB12‑3",
  "NJ24‑7", "‑7", "AAAA‑", "‑AAAA");
- `<size=150%>` before, between and after ordinary letters, and two sizes in one line;
- `<voffset=±0.4em>`, a smaller `<size=60%>`, and spaces in `<size=150%>`;
- the same on a second line after a line feed, and on a line begun by wrapping (60 wide).

Each was set in GRAYSTROKE REGULAR SDF 14 pt, NotoSans-Regular 14 pt and GRAYSTROKE 12 pt,
with Overflow, Truncate and Ellipsis, wrapping on and off. The rect was 400 wide (60 for the
wrapped text), its height swept from 0 to 70 in steps of 0.25, and each distinct result was
logged: `isTextOverflowing`, `firstOverflowCharacterIndex`, `isTextTruncated`, `characterCount`,
`lineCount`, and each record's code, visibility, line, and each line's ascender, descender,
height and baseline. Each text's layout in a 400 × 1000 rect was logged too (`metrics`), and
the title in its own rect. `tools/UnityProbe/tl-check.py rule LOG` predicts all 79,257
400-wide sweep results of the shipped package from the rule below. `tl-check.py diff ORIG OURS`
finds 258 of 348 cases differing in this implementation, all from the index of the first
overflowing character.

- **Each character is checked against the line as it stands when it is placed.** "Overflow at
  white space" above says which characters are checked, and that a character overflows when
  its line ends below the rect: the distance from the first line's top down to the line's
  descender is greater than the rect's height. The line's ascender and descender there are
  those of its characters so far, not of the whole line. A taller character later in the line
  does not make the characters before it overflow. It overflows itself, at its own index.
  - GRAYSTROKE 14 pt, the geyser's name, unwrapped: index 0 below a height of 14.69 (the
    first letter's line), index 25 (the U+2011) from 14.75 to 23.81, none from 23.82.
  - "AAAA<size=150%>B</size>AA" (GRAYSTROKE 14 pt, 22.04 high): 4 from 14.69 to 22.03.
    "<size=150%>B</size>AAAA": 0 below 22.04. "AAAA<voffset=0.4em>B</voffset>AA" and the same
    with −0.4em (20.3 high): 4 from 14.69. "AA<size=130%>B</size>AA<size=160%>C</size>AA":
    2 from 14.75, 5 from 19.25, none from 23.75 (GRAYSTROKE 14 pt; 19.25, 24.75 and 30.5 in
    NotoSans 14 pt).
  - On the first line, its top is the highest ascender so far as well: a taller character
    raises the first line's top, and the check at it measures from there.
  - On a later line, the line's baseline sits below the line before by the usual interval
    from the line's ascender so far. A taller character lowers the baseline by the rise in the
    line's ascender, and the check at it measures its line's descender from there. The lines
    before keep the first line's top.
  - "AA\nAAAA<size=150%>B</size>AA" (GRAYSTROKE 14 pt): 0 below 14.69, 3 (the second line's
    first letter) up to 29.38 (two lines of letters), 7 (the B) from 29.5 to 36.72, none from
    36.73. The same with the U+2011 in place of the B: 7 from 29.5 to 38.5.
    "AA\n<size=150%>B</size>AAAA": 3 from 14.69 to 36.72.
  - The same with wrapping on, and in every mode. With wrapping, a line begun by wrapping is
    checked the same way: "AAAA AAAA<size=150%>B</size>AA" 60 wide breaks after "AAAA " and
    before the last "A". It overflows at 5 (the second line's first letter) from 14.75, at 9
    (the B) from 29.5, at 11 (the third line) from 36.75, and not at all from 51.5
    (GRAYSTROKE 14 pt).
- **Truncate and Ellipsis** cut at that index as "Overflow at white space" above describes. The
  line the cut ends is laid out with its characters before the cut only: its ascender,
  descender, height and baseline are those of the line as it stood before the taller
  character.
  - The title, Truncate: "Liquid Sulfur Geyser NJ24" and the blank record, one line 14.68 high,
    ascender 11 (the rect's top), descender −3.69, baseline −2.29, in the rect's centred
    coordinates. Ellipsis gives "Liquid Sulfur Geyser NJ2…" and the record.
  - "AA\nAAAA<size=150%>B</size>AA" at 400 × 30, Truncate: "AA", line feed, "AAAA" and the
    record on line 1, whose baseline is −12.97, as without the B (Overflow: −19.62).
- **White space in a `<size>` takes no part in the line's height.** A space inside
  `<size=150%>` reports the ascender and descender of the text's own size in `characterInfo`.
  It does not make its line taller, and is not counted in the preferred height.
  - "AAAA <size=150%> </size>AA": GRAYSTROKE 12 pt 12.6 high, as "AAAA AA", and it never
    overflows a rect 12.75 high. This implementation: 18.89 high, and index 0 below that.
  - This differs from white space taken from a fallback font, which does deepen its line
    ("Further characters" above). Not measured: white space in `<voffset>`, and in a
    `<size>` smaller than the text.

- **Implemented.** This implementation follows the block: in the same probe `tl-check.py rule`
  passes all 79,257 sweep results from its log, and `tl-check.py diff` finds none of the 348
  cases differing from the shipped package. Where Truncate goes on with a line begun by
  wrapping, the characters that move onto the line before are checked on that line as it
  stands. The white space a `<size>` leaves out of the line's height is space-like white space
  and line breaks, not the tab, which is checked.

Where this implementation deviated before (`TMP_Text.Layout.cs`): it lays out each line whole, and
then checks the line's whole height at its first checked character. So a line that grows
part-way overflows at its start. Truncate and Ellipsis then cut the whole line. On the first
line nothing is shown, and that is the geyser title: index 0, 0 records. In
"AA\nAAAA<size=150%>B</size>AA" it reports 3 where the shipped package reports 7.

| Case (GRAYSTROKE REGULAR SDF 14 pt) | shipped: index; records | this implementation before |
|---|---|---|
| the title, 175.13 × 22, Truncate | 25; "Liquid Sulfur Geyser NJ24", record | 0; nothing |
| the title, 175.13 × 22, Ellipsis | 25; "Liquid Sulfur Geyser NJ2…", record | 0; nothing |
| the title, 175.13 × 22, Overflow | 25; all 27 | 0; all 27 |
| "AAAA<size=150%>B</size>AA", 400 × 20, Truncate | 4; "AAAA", record | 0; nothing |
| "AA\nAAAA<size=150%>B</size>AA", 400 × 30, Truncate | 7; "AA", LF, "AAAA", record | 3; "AA", LF, record |
| "AAAA <size=150%> </size>AA", 400 × 20, Truncate | none; all 8 | 0; nothing |

Not measured: `<line-height>`, `<line-indent>`, sprites taller than the text, auto-size with
a line that grows part-way (whether it shrinks to fit the whole line or the part before the
cut), Page mode and `maxVisibleLines`, and world-space `TextMeshPro`.

### The caret after an edit ends (measured 2026-10-24)

Found by two mod specs. A value typed into a capacity side screen's field and submitted with
Enter leaves the original's `KInputTextField` with `caretPosition` at the end of the text: 3
for "500" (Adjustable Transformers, `tests/e2e/mods/1885249412-adjustable-transformers.yaml`,
ui_dump `capacity-set`, `…/CapacityControlSideScreen#1/ValidContent#1/Max#1/VerticalLayout#1/InputField#1`)
and 5 for "50000" (Measuring Cup, `3336359859-measuring-cup.yaml`). This implementation reports
0. The spec clicks the field, presses Ctrl+A and Backspace, types the value and presses
Return. Klei's `KInputField` (the side screen's `KNumberInputField`) listens to `onEndEdit`: it
sets `text` to the parsed value again and calls `DeactivateInputField()` at the end of the
frame. The game's field prefabs (all 28 `TMP_InputField`s loaded at the main menu but one)
have `resetOnDeActivation` and `onFocusSelectAll` on. A new `TMP_InputField` has
`resetOnDeActivation` on as well.

**Measured** with `PROBE_SCENARIO=inputcaret` (tools/UnityProbe, `InputCaret.cs`, lines `IC
end …`). Each field is a `KInputTextField` built as `InputText.cs` builds the slider row's
field, but 120 wide. Its text comes from one of five sources:
- typing "500" or "50000" (`ProcessEvent`, one character at a time);
- Ctrl+A, Backspace and then typing, over a text "1000" set from code;
- typing and then Shift+Home (a selection: caret 0, anchor at the end);
- `text =` while the field is focused;
- `text =` before the field is focused.

The edit then ends in one of six ways: `ProcessEvent` of Return, `OnSubmit(BaseEventData)`,
`DeactivateInputField()`, `DeactivateInputField(true)`, deselection
(`EventSystem.SetSelectedGameObject(null)`), or disabling the GameObject. Each case runs with
`resetOnDeActivation` and `onFocusSelectAll` off and on. Some runs add the listeners Klei's
`KInputField` adds; their `onEndEdit` sets the same text, a shorter one ("5"), a longer one
("500123") or the parsed number. In the `IC end x …` cases the keys are real: the probe sends
them to the player's window through XTest, as `scripts/e2e.py` types, so they reach the field
through the event system and `OnUpdateSelected`. Those cases end with Return, Escape or
deselection. Each step logs `text`, `caretPosition`, `selectionAnchorPosition`,
`selectionFocusPosition`, `stringPosition`, `selectionStringAnchorPosition`, `isFocused` and
whether the event system has the field selected. Each event logs the caret it was sent
with. After the end the field is activated again (`onFocusSelectAll` as set) and "9" is typed,
which shows where the string position really was. That is 246 `ProcessEvent` cases and 44
cases with real keys. `tools/UnityProbe/ic-check.py rule LOG` checks the rules below, and they
hold for all 290 in the shipped package's log. `ic-check.py diff ORIG OURS end|x` compares two
players' logs.

1. **Ending an edit leaves the caret and the selection where they were.** That holds for every
   way of ending above, with real keys and through `ProcessEvent`. `resetOnDeActivation` makes
   no difference, and neither does `DeactivateInputField(true)`'s `clearSelection`.
   - Typed "500" and Return: `caretPosition`, `selectionAnchorPosition` and
     `selectionFocusPosition` stay 3, and so do `stringPosition` and
     `selectionStringAnchorPosition`. Typed "50000": 5.
   - A selection stays: after Shift+Home, the caret stays 0 and the anchor 3, through Return,
     deselection and `DeactivateInputField(true)`.
   - A text set from code keeps the caret where it was: at 0 after `text =` on a focused field.
   - Activating the field again starts from there: "9" typed after it goes in at the end
     ("5009", caret 4), with `onFocusSelectAll` off.
   - **A shorter text cuts them.** When a listener, or Escape's restore of the original text,
     sets a text shorter than the caret's position, the caret and the anchor become that
     text's length. They do not move back when the text grows again. `onEndEdit` setting "5"
     after "500" leaves 1/1. Escape after typing "500" in an empty field restores "" (0/0),
     and a listener's "0" after that stays at 0/0. A longer text ("500123") keeps 3/3.
   - Klei's flow (`onEndEdit` sets the parsed value, then `DeactivateInputField()` at the end
     of the frame) keeps 3 for "500" and 5 for "50000". This is the value the two specs read.
2. **`onEndEdit`** is sent:
   - on Enter (real keys, single-line): `onSubmit`, then `onEndEdit`, then the field is
     deselected (`onDeselect`). That order holds whatever `resetOnDeActivation` says.
   - on Escape (real keys): after the restored text's `onValueChanged`, before `onDeselect`.
   - by `DeactivateInputField()`, deselection and disabling only when `resetOnDeActivation`
     is on. With it off, deselection and `DeactivateInputField()` send `onDeselect` alone, and
     disabling sends nothing.
   - by `DeactivateInputField(true)` always.
   - Each is sent with the caret where it was (`endEdit:"500"@3/3/3/3`).
  - Each `onValueChanged` of a typed character is sent with the caret after it: typing "500"
    reports `"5"@1/1/1/1`, `"50"@2/2/2/2` and `"500"@3/3/3/3` (caret, anchor, focus, string
    position).
3. **`OnSubmit(BaseEventData)` on a focused field** sends `onSubmit`, then ends the edit as
   `DeactivateInputField()` does (rule 2), and deselects the field. Not measured: `OnSubmit` on
   a field that is not focused.
4. **`ProcessEvent` of Return** (KeyCode Return, character '\n', single-line) does nothing: the
   field stays focused, its text unchanged, and no event is sent. Only Enter through the event
   system ends the edit.
5. **Select-all on focus** (`onFocusSelectAll` on, measured with a text set before focus) puts
   the caret at 0 and the anchor at the end: `caretPosition` 0, `selectionAnchorPosition` 3,
   `stringPosition` 0, `selectionStringAnchorPosition` 3. This is the order Ctrl+A gives
   (described above).
6. **The character positions follow the string positions a frame late after Ctrl+A in
   `ProcessEvent`.** Right after it, `selectionStringAnchorPosition` is 4 in "1000" while
   `selectionAnchorPosition` still reads 0. With real keys, two frames later, both read 4.
   Nothing in the game reads them in between.

- **Implemented.** This implementation follows the rules above: in the same probe `ic-check.py
  rule` holds for all 326 cases of its log (end, x and cull), and `ic-check.py diff` finds none
  of the 2,055 `ProcessEvent` steps, the 324 steps with real keys or the 540 cull steps
  different from the shipped package. The character positions read as Ctrl+A found them until
  the frame ends or the string positions move again. While an edit's events run before its
  text is laid out, a string position past the old layout's end reads that far past its last
  character.

**Where this implementation deviated before** (`TMP_InputField.cs`):
- `DeactivateInputField(bool)` puts both string positions to 0 when `resetOnDeActivation` or
  `clearSelection` is set. The game's fields have `resetOnDeActivation` on, so every ended edit
  leaves the caret at 0. That is the specs' `caretPosition=0`, and the field typed into again
  starts at 0 ("9500" where the original gives "5009"). Measured in 68 of the 290 cases. Keep
  the positions. Cut them to the length of each new text, which `SetText` already does.
- It sends `onEndEdit` on every deactivation. The original sends it only with
  `resetOnDeActivation` or `clearSelection`, or on Enter and Escape (rule 2). That differs for
  fields with `resetOnDeActivation` off, which a Klei listener then never hears.
- `OnSubmit(BaseEventData)` on a focused field does nothing. The original submits and ends the
  edit (rule 3). With real keys, Enter sends `onSubmit` once in both players.
- Select-all on focus puts the anchor at 0 and the caret at the end, the reverse of rule 5.
- The character positions follow the string positions at once (rule 6). Not worth matching.
- Inside the events the character positions lag instead. While a typed character's
  `onValueChanged` runs, `caretPosition` still reads the position before it. Typing "500"
  reports `changed:"5"@0/0/0/1`, `"50"@1/1/1/2` and `"500"@2/2/2/3`, where the original reports
  `@1/1/1/1`, `@2/2/2/2` and `@3/3/3/3`. `stringPosition` (the last value) is right in both. A
  listener that reads `caretPosition` in `onValueChanged` gets the new caret in the original.
- `onEndEdit` is sent after the positions are reset, so with `resetOnDeActivation` on it
  reports `@0/0/0/0` where the original reports the caret as it was.
- It matches the original on rule 4, on Enter's event order, and on the caret with
  `resetOnDeActivation` off.
- `ic-check.py rule` holds for 94 of 326 cases of our log (end, x and cull). `ic-check.py
  diff` finds 910 of the 2,055 `ProcessEvent` steps different, and 149 of the 324 steps with
  real keys.

| Case | shipped: caret/anchor after; events | this implementation before |
|---|---|---|
| Ctrl+A, Backspace, "500", Return (real keys), `resetOnDeActivation` on: the specs | 3/3; submit, endEdit, deselect | 0/0; the same events |
| the same with Klei's `onEndEdit` (text set again, deactivate at end of frame) | 3/3 | 0/0 |
| the same, "50000" | 5/5 | 0/0 |
| the same, `resetOnDeActivation` off | 3/3; submit, endEdit, deselect | the same |
| "500" typed, deselected, `resetOnDeActivation` on | 3/3; endEdit, deselect | 0/0; endEdit, deselect |
| the same, `resetOnDeActivation` off | 3/3; deselect | 3/3; endEdit, deselect |
| "500" typed, `DeactivateInputField(true)`, `resetOnDeActivation` off | 3/3; endEdit, deselect | 0/0; endEdit, deselect |
| "500" typed, Shift+Home, Return (real keys), `resetOnDeActivation` on | 0/3 | 0/0 |
| "500" typed, the GameObject disabled, `resetOnDeActivation` off | 3/3; nothing | 3/3; endEdit |
| "500" typed, `onEndEdit` sets "5", `resetOnDeActivation` on | 1/1 | 0/0 |
| "500" typed into "" and Escape (real keys) | 0/0, text ""; changed, endEdit, deselect | the same |
| "500" typed over "1000" and Escape, `resetOnDeActivation` on | 3/3, text "1000" | 0/0 |
| "500" typed, `OnSubmit(BaseEventData)`, `resetOnDeActivation` on | 3/3; submit, endEdit, deselect, unfocused | 3/3; nothing, still focused |
| "500" typed, `ProcessEvent(Return)` | 3/3, still focused; nothing | the same |
| text "500" before focus, `onFocusSelectAll` on | caret 0, anchor 3 | caret 3, anchor 0 |

Not measured: multi-line fields (Enter in `MultiLineSubmit` and `MultiLineNewline`), Tab,
`readOnly`, fields on a touch keyboard, and what `resetOnDeActivation` changes if anything (it
moved neither the caret, the selection nor the text in any case here).

### The caret graphic and RectMask2D (measured 2026-10-24)

Found by a mod spec: Mods Presets Organizer (`tests/e2e/mods/3284635963-mods-presets-organizer.yaml`,
ui_dump `presets`, `…/preset_add#6/input_field_holder#0/Caret#0`). The field has never been
focused. Its caret (`TMP_SelectionCaret`) is a child of `input_field_holder`, which has the
`RectMask2D`. The caret was left at its defaults: 100 × 100, centred, at world (−213.94,
272.28)–(−127.94, 358.28). That happens when PLib's patch of `AssignPositioningIfNeeded` loses
its coroutine, as described under "when the label and the caret are set up". The holder's
rect runs from (302.11, 616.28) to (458.52, 633.48), so the caret's rect lies wholly outside
the clip. Under "RectMask2D culling" in `ugui.md` an `Image` there would be culled. The
original does not cull the caret's `CanvasRenderer`; ours does.

**Measured** with `PROBE_SCENARIO=inputcaret` (lines `IC cull …`). The fields are built as
above, active or enabled after a frame, on a canvas of scale 1. The caret is either at its
defaults (the probe's Harmony prefix skips `AssignPositioningIfNeeded`, so the caret stays 100 ×
100 at the world origin, outside the viewport's clip) or placed over the text. The field sits
in one of three places:
- `own`: only its viewport's `RectMask2D`;
- `outer-in`: also inside an outer 300 × 100 `RectMask2D`;
- `outer-out`: moved 200 up, out of the outer mask, so the two clips do not intersect.

Each field is never focused, focused with "12" typed, or focused and then deselected. An
`Image` with the caret's rect, the caret's sibling, is the control. Each step logs the caret's,
the text's and the control's `CanvasRenderer.cull`, `hasRectClipping`, rect in canvas space
and mesh (`CanvasRenderer.GetMesh`). The steps are: two frames, focus, the mask moved by 1 and
back, the field moved by 1, the caret's GameObject and graphic toggled, the caret moved by 1,
the field disabled and enabled. Then come direct calls of the caret's and the control's
`Cull(clipRect, validRect)`: a rect far off with `true`, a rect over everything with `true`,
and the far rect with `false`. 36 cases. `ic-check.py rule` checks the rule and it holds for all
36 in the shipped package's log. `ic-check.py diff ORIG OURS cull` compares the players.

- **The caret graphic is culled only when its clip rect is invalid.** Its
  `Cull(clipRect, validRect)` sets `canvasRenderer.cull` to `!validRect`, whatever its rect,
  its mesh and the clip rect are.
  - With a valid clip, it is never culled: at its defaults outside the clip (`own`,
    `outer-in`), never focused or focused, in every step above.
  - Its mesh makes no difference: with no vertices (never focused, or after an edit ended) and
    with its 4-vertex quad lying outside the clip (focused at its defaults), it is not culled.
  - In the same steps the control `Image` with the same rect is culled (`MaskableGraphic`'s
    rule in `ugui.md`).
  - Called directly: `Cull(far, true)` leaves it not culled (or un-culls it), `Cull(far,
    false)` culls it.
  - When the clip rect is invalid (`outer-out`, the masks do not overlap), the caret is culled
    with everything else under the mask, and `hasRectClipping` is false.
- **It is registered with the masks like any `MaskableGraphic`.** `maskable` is true, and
  `hasRectClipping` is true under a valid clip from the first canvas update after the field is
  enabled. So the clip still cuts its pixels; only the cull is skipped.
- **Measured on the way:** after a direct `Cull` call un-culled an `Image` that the mask
  culls, the original culled it again a frame later with nothing moved. Ours keeps the value
  of the call until something moves. Only code that calls `Cull` itself would see it.

- **Implemented.** `TMP_SelectionCaret.Cull` sets the cull to `!validRect` and raises the
  change as the base does. A `RectMask2D` culls again, in its next pass, a graphic whose cull
  changed since its last pass set it (the re-cull above); `ugui.md` records it. `ic-check.py
  diff … cull` finds none of the 540 steps different.

**Where this implementation deviated before.** `TMP_SelectionCaret` (`src/Unity.TextMeshPro/Input/TMP_SelectionCaret.cs`)
had no `Cull` of its own, so it culls with its rect as `MaskableGraphic` does:
- The caret at its defaults outside the clip is culled in every step with a valid clip (12
  cases). That is the presets organizer's `Caret#0`.
- A caret placed over the text is culled after it moves, because the text's rect has a height
  of −1 and so overlaps nothing (12 cases).
- `Cull(far, true)` culls it.
- `ic-check.py diff … cull` finds 210 of the 540 steps different: 198 in the caret's cull, and
  24 in the control's a frame after the direct calls (the re-cull noted above).

Give it a `Cull(Rect clipRect, bool validRect)` that sets `canvasRenderer.cull = !validRect`
(and raises the cull-state change as the base does when the value changes).

| Case (`cull …`) | shipped: caret culled | this implementation |
|---|---|---|
| defaults, `own`, never focused (the presets organizer) | no | yes |
| defaults, `own`, focused (quad outside the clip) | no | yes |
| defaults, `outer-in`, any | no | yes |
| placed, `own`, after the caret moved by 1 | no | yes |
| any, `outer-out` (clip rect invalid) | yes | yes |
| `Cull(far, true)` called directly | no | yes |
| `Cull(far, false)` called directly | yes | yes |
| the control `Image`, defaults' rect, `own` | yes | yes |

### Alignment set before a text first wakes (measured 2026-10-24)

Found by Chain Tool (3303494244): its tool menu clones toggle rows under a hidden menu and sets
each label's `alignment` to MidlineLeft (4097) before the labels have ever been active. The
shipped package then shows them TopLeft, the prefab's alignment; this implementation keeps
MidlineLeft (ui_dump `menu`, `…/CreatePanel/toggleEntry#{0,1}/Label`). The engine's lifecycle is
not the cause: in both players the copies wake (Awake, OnEnable) when the menu is first shown,
after the mod's code.

Measured with `PROBE_SCENARIO=alignment` (tools/UnityProbe, `Alignment.cs`, lines `AL …`), 10 s
into the main menu, in both players. Among the loaded `LocText`s, the serialized
`m_textAlignment` holds the values 0, 1, 4, 5, 7, 9 (the oldest table), 257, 258, 260, 513, 514,
516, 1025, 1026, 1028 and 8200, and 65535 (already converted) in 10 of them. For one text of each
value, copies were made with `Instantiate` under an inactive parent, set active themselves (as
`Util.KInstantiateUI(…, force_active: true)` does), given an alignment and then shown by
activating the parent; others were copied under an active parent, or set after waking. Each step
logged `alignment`, `horizontalAlignment`, `verticalAlignment` and the stored value (read by
reflection).

- **A setter changes the two halves only.** `alignment`, `horizontalAlignment` and
  `verticalAlignment` change what the getters return at once, before the text has woken too
  (MidlineLeft reads back as h Left, v Geometry, 4097), and leave the stored `m_textAlignment`
  as it was.
- **The stored value is applied when the text first wakes.** If it is not 65535, it replaces
  both halves then (by the rules of "Stored alignment" above: 0–15 by the oldest table, other
  values split into their bytes) and becomes 65535. So whatever was set before the first wake
  is lost when the data stored an alignment: a copy of a text with 257 set to MidlineLeft while
  hidden shows TopLeft once shown; with 5 or 514, Center; with 513, Left; with 516, Right. A
  half set alone (h Right, or v Bottom) is lost the same way.
- **Before that, the getters report the serialized halves, not the stored value.** The game's
  texts that store an old value hold the default halves (Left, Top), so a text not yet woken
  reads TopLeft whatever it will show (the stored 5 reads TopLeft until it wakes, then Center).
- **Once applied, it is not applied again.** A value set after the first wake stays through
  deactivation and reactivation and later frames. A text whose stored value is 65535 keeps a
  value set before it wakes (MidlineLeft on such a copy is still MidlineLeft when shown).
- **New components** (`AddComponent<TextMeshProUGUI>()`) store 65535 and default to TopLeft; a
  value set before or after they wake stays.
- **Copies carry the stored value.** A copy of a not-yet-woken text that was given MidlineLeft
  stores the source's value and shows the source's alignment when it wakes (a copy made of a
  stored-257 text shows TopLeft). So does a not-yet-woken copy moved under an active parent: it
  wakes there and loses what was set.
- This implementation stored the set value before (the setters wrote the stored field), so the
  first wake applied what was set; every "hidden" case above kept MidlineLeft in it, and the
  stored value read 4097 after a set.
- **Implemented.** The setters now change the two halves only. In the same probe 537 of the
  551 `AL` lines match the original's. The 14 that differ come from the players picking other
  loaded texts as sources (their loaded-text lists come in different orders): the 65535
  group's source is centred in ours, top in the original, and its copies follow it; the
  fontStyle of three groups and which copy logs its Awake untagged follow their sources too.

### Texts with nothing to lay out (measured 2026-10-24)

Found in a mod play-test: Adjustable Auto Sweeper (2971184827) shows the game's
`IntSliderSideScreen`, whose `SliderSet.SetTarget` sets `unitsLabel.text` to the target's
`SliderUnits`, here "". The label (`…/IntSliderSideScreen/Contents/UnitsLabel`, NotoSans 16 pt,
95.7 × 15, top left) then reports `isTextOverflowing` true and `firstOverflowCharacterIndex` 0
in the shipped package, and false and -1 in this implementation. The `textInfo` counts are the
same (none). One line of NotoSans 16 pt is 21.75 high, so any visible text overflows that rect
at 0; the label had such a text before it was set to "".

Measured with `PROBE_SCENARIO=emptytext` (tools/UnityProbe, `EmptyText.cs`, lines `ET …`), at
the main menu. The texts were "", null, a space, "<b></b>", "<color=#FF0000></color>",
"<size=150%></size>", "<i></i><u></u>", "<b> </b>", "<br>" and U+200B, in NotoSans-Regular
16 pt and GRAYSTROKE REGULAR SDF 12 pt, set up as `SetTextStyleSetting` does, top left, 95.7
wide. The heights were 0, 5, 15, one line of the font and a hundredth under and over it, and
100. Each was tried in Overflow, Truncate, Ellipsis, Masking, ScrollRect and Page, wrapping on
and off, auto-size off and on (`fontSizeMin` 4, `fontSizeMax` the size), in a new text
(`fresh`). Then "kg" and "AB\nCD" were laid out first, in a rect 15 high, one line + 0.01 high
(where "AB\nCD" overflows at 3) or 100 high. Then the text was set to "", null, a space or
"<b></b>", laid out by `ForceMeshUpdate` or by the frame's own rebuild, and the rect resized to
100, 0 and 15 (`after`). Last, auto-size was tried with `fontSizeMax` 8 above and 4 below the
size set (`automax`). Each case logged `isTextOverflowing`, `firstOverflowCharacterIndex`,
`isTextTruncated`, `characterCount`, `lineCount`, `spaceCount`, `wordCount`, `pageCount`,
`fontSize`, and then `preferredWidth` and `preferredHeight`. The flags were read before the
preferred sizes, as E2eDriver's dump reads them, and again after. Two runs of the shipped package
gave the same 5,904 results. `tools/UnityProbe/et-check.py rule LOG` predicts all 5,376 of them
that the rule below covers. Before the fix this implementation followed it in 2,545 and
`et-check.py diff ORIG OURS` found 2,993 of 6,000 results differing; now it follows all 5,376,
and the diff finds 24 of 5,904: the `spaceCount` of "AB\nCD" cut by Ellipsis, noted at the end.

- **An empty text keeps the last overflow.** When the text is "" or null, the layout reports
  nothing and changes neither `isTextOverflowing` nor `firstOverflowCharacterIndex`. Both stay
  as the last layout of a text with something in it left them.
  - "kg" in 95.7 × 15 overflows at 0, and after `text = ""` it still reports true and 0.
    "AB\nCD" in 95.7 × 21.76 overflows at 3, and "" then reports true and 3. Where the earlier
    text fitted (95.7 × 100, or "kg" one line + 0.01 high), "" reports false and -1.
  - The same in every overflow mode, by `ForceMeshUpdate` or by the frame's rebuild, and after
    the rect is resized (to 100, to 0, to 15): resizing an empty text changes nothing.
  - A text that was never anything but empty reports false and -1 in every rect, 0 high too.
  - The rest is cleared: `isTextTruncated` false (a Truncate that cut "kg" leaves true, 0 and
    not truncated), `characterCount`, `lineCount`, `spaceCount`, `wordCount` and `pageCount` 0,
    and the preferred size 0 × 0.
  - Under auto-size, the earlier text's overflow is kept in the same way. Each earlier text here
    fitted at some size, so each gave false and -1. `fontSize` becomes the size set (the value
    code last gave `fontSize`), but no more than `fontSizeMax`. It does not stay at the earlier
    text's auto-sized size: "kg" shrunk to 11 in 95.7 × 15, then "", reports 16 (`fontSizeMax`
    16 or 24) and 12 with `fontSizeMax` 12. GRAYSTROKE 12 pt with `fontSizeMax` 8 reports 8.
  - null behaves as "" throughout.
- **A text of tags alone is laid out** and is not empty in this sense. "<b></b>",
  "<color=#FF0000></color>", "<size=150%></size>" and "<i></i><u></u>" have no characters, no
  lines and no pages. They report false and -1 in every rect, mode and wrapping, whatever came
  before: "kg" overflowing at 0, then "<b></b>", reports false and -1. Their preferred size is
  0.01 × 0.01, not the empty text's 0 × 0.
- **White space alone** (a space, "<b> </b>", "<br>", U+200B) is laid out as "Overflow at white
  space" above says: false and -1 in every rect, mode and wrapping, after an overflowing text
  too. It has 1 character, 1 line and 1 page, `spaceCount` 1 (0 for U+200B) and `wordCount` 1,
  and Truncate and Ellipsis keep the character. Its preferred size is 0.01 × 0.01.
- **Auto-size with nothing visible.** A text of white space or tags alone takes `fontSizeMax`
  in every rect, 0 high too: 24 with the size set at 16 and `fontSizeMax` 24, and 12 with
  `fontSizeMax` 12. An empty text takes the size set, as above.
- **Preferred size under auto-size** (noted in passing, one unwrapped line): `preferredWidth`
  and `preferredHeight` are those of the text at `fontSizeMax`, not at the size auto-size chose.
  "kg" shrunk to 11 in 95.7 × 15 is 18.38 × 21.76 with `fontSizeMax` 16, 27.57 × 32.63 with 24,
  and 13.79 × 16.32 with 12. That narrows "preferredHeight uses the auto-sized size" under
  "Measured on the shipped package" above, which no probe measured: this probe tried no
  wrapped text, so a wrapped text keeps that line (its preferred height at the size auto-size
  chose) and an unwrapped one is measured at `fontSizeMax`.

Before the fix this implementation (`TMP_Text.Place.cs` set `m_firstOverflowCharacterIndex`
from each layout, an empty one too) differed in each row of the table: an empty text reset the
overflow to false and -1, a text of tags alone was 0 × 0, auto-size shrank white space alone
as if it had a line to fit (a space in 95.7 × 15 got 11, in 95.7 × 0 `fontSizeMin`) and left
tags alone and empty texts at the size set or the earlier auto-sized size, and the unwrapped
preferred height under auto-size was taken at the chosen size (14.96 below). It now follows
the table (`TmpEmptyTextTests`).

| Case (NotoSans-Regular 16 pt, rect 95.7 wide) | shipped: overflowing, index; other | this implementation, before the fix |
|---|---|---|
| "kg" at height 15, then "" (Adjustable Auto Sweeper's UnitsLabel) | true, 0; 0 characters, 0 × 0 | false, -1 |
| the same, then the rect resized to 100 | true, 0 | false, -1 |
| "kg" at 15, Truncate, then "" | true, 0; not truncated | false, -1; not truncated |
| "AB\nCD" at 21.76, then "" (Overflow, Truncate, Ellipsis) | true, 3 | false, -1 |
| "kg" at 100, then "" | false, -1 | false, -1 |
| "kg" at 15, then null | true, 0 | false, -1 |
| "" in a new text, any height, mode, auto-size | false, -1; 0 × 0 | the same |
| "kg" at 15, then "<b></b>" | false, -1; 0.01 × 0.01 | false, -1; 0 × 0 |
| "<color=#FF0000></color>" in a new text | false, -1; 0 characters, 0.01 × 0.01 | false, -1; 0 × 0 |
| " " at 0, Truncate | false, -1; 1 character | the same |
| " " at 15, auto-size, size 16, `fontSizeMax` 24 | `fontSize` 24 | 11 |
| "<b></b>" at 15, auto-size, size 16, `fontSizeMax` 24 | `fontSize` 24 | 16 |
| "kg" at 15, auto-size (11), then "", `fontSizeMax` 24 | `fontSize` 16 | 11 |
| "" at 15, auto-size, size 16, `fontSizeMax` 12 | `fontSize` 12 | 16 |
| "kg" at 15, auto-size (11), `fontSizeMax` 24: preferred size | 27.57 × 32.63 | 27.57 × 14.96 |

Not measured: `fontSizeMin` above the size set, Linked mode, `SetText` and `SetCharArray`, an
empty text in a `TMP_InputField` (its text is U+200B, see "Further characters" above), margins,
and whether anything other than a layout of a non-empty text, such as a font or size change,
clears the kept overflow. In passing, "AB\nCD" cut by Ellipsis at 21.76 reports `spaceCount` 1
in the shipped package and 0 here.

### Heights in the translations' fonts (measured 2026-10-25)

Found in the translations' play-tests (`tests/e2e/mods/*`, a translation picked through the
language prefs): with a Japanese, Chinese or Korean translation, the Game options screen's
headers (LocText, 24 pt, NotoSansCJKjp/sc/kr through the translation's font) give their layout
groups a preferred height 0.01 higher in this implementation: `GameOptionsScreen`'s
`VerticalLayoutGroup` reports 459.86 where the shipped package's reports 459.84, its rows 191.56
for 191.55 and its save options 152.3 for 152.29. Nothing differs on screen. The headers' own
rects are 35.56 high here and 35.55 in the shipped package.

Measured with `PROBE_SCENARIO=textheight` (tools/UnityProbe, `TextHeight.cs`, lines `TH …`),
at the main menu: 21 labels in Japanese, Chinese, Korean, Thai, Cyrillic, Vietnamese and Latin
(the Game options screen's headers among them) in NotoSansCJKjp, NotoSansCJKsc, NotoSansCJKkr,
NotoSansThai, RobotoCondensed and NotoSans, regular, at 12, 14, 16, 18, 20, 24 and 28 pt, set up
as `SetTextStyleSetting` does (kerning, extra padding), top left in a 400 × 60 rect, unwrapped
and wrapped, with the rect's top at 30, 0 and 22.2 (pivot 0.5, 1 and 0.63). Each logs the
preferred height and width with their float bits, `GetPreferredValues`, the first line's
ascender, descender, baseline and line height, and each character's ascender, descender, scale
and font asset. `tools/UnityProbe/th-check.py diff ORIG OURS` lists the texts that differ, and
`th-check.py rule LOG` recomputes each one-line text's preferred height from its line.
3,528 texts: 368 differ.

- **Heights follow the rule of the widths.** Let h be the line's height as a float: its
  ascender less its descender. `preferredHeight` is (⌊h × 100⌋ + 1) / 100, with h × 100 taken
  exactly (in double), as "Preferred sizes to the hundredth" above says for widths. The rule
  reproduces all 3,528 texts of the shipped package from their logged lines, but the 70 whose
  rect top is at 22.2: there the logged line is the rect's, and its float rounding makes the
  reconstruction, not the package, miss; the package gives the same bits at every rect.
  - At 24 pt every font's line is 35.55 high (NotoSansCJKjp: ascender 34.8125 and descender
    -9.625 at point size 30, scaled by 0.8; the others reach it through glyphs of the CJK
    fonts), which as a float is 35.5499992. The shipped package gives 35.55 (3554.99992 floors to
    3554). This implementation gives 35.56 from the same line (its ascender and descender, the
    characters' and the rendered height 35.55 have the same bits in both), at every rect top: as
    if h × 100 were rounded to a float (3555) before the floor. 280 of the 368 differences are
    this one, in every font at 24 pt, wrapped or not, at every rect top.
  - No other size measured has a line within a float step under a hundredth, so both agree on
    the rest.
- **A line's height comes from its visible characters.** A text set in a font that lacks some of
  its glyphs takes them from a fallback (here NotoSans-Regular, for Cyrillic in the CJK and Thai
  fonts). In the shipped package the line's ascender and descender, and so its baseline and
  height, are those of the fonts of the line's visible characters; the space between the words,
  which the primary font holds, adds nothing, and its own character info reports the line's
  ascender and descender, not its font's. In this implementation the space's font counts too:
  - "Единицы температуры" in NotoSansCJKsc 24 pt (its letters from NotoSans-Regular, its space
    from NotoSansCJKsc): shipped, line ascender 30, descender -2.625, baseline 4.375 (rect top
    at 30), height 32.625, preferred 32.63, the letters' ascender 30. Here: descender -5.53125,
    baseline 2.15625, height 35.53125, preferred 35.54, the letters' ascender 27.78125: the
    letters sit 2.22 lower.
  - The same text at 12 pt in NotoSansCJKkr: shipped 16.32 (NotoSans's 12.8125 + 3.5), here
    17.78 (NotoSansCJKkr's 13.925 + 3.85); in NotoSansThai 12 pt 16.32 and 16.89. It differs
    at every size and rect in NotoSansCJKsc, NotoSansCJKkr and NotoSansThai (NotoSansCJKjp
    holds Cyrillic): 84 of the 368 differences.
  - Not measured: a line of white space alone in such a text, a line whose only visible
    characters are the primary font's, and fallbacks that are larger than the primary font.
- **Implemented.** This implementation follows both rules: it takes the line's height as a
  float before the rounding, and leaves white space of the text's own font after a visible
  character out of its line's ascender and descender (white space before the line's first
  visible character still counts: " A" in a rect 0 high does not fit at the space, as measured
  under "Overflow at white space"). In the same probe `th-check.py diff` finds 12 of the 3,528
  texts differing, the widths below; every height, the first line's ascender, descender and
  baseline, and each character's ascender and descender match. `lineInfo.lineHeight` still
  differs in 1,864 texts (17.765625 here for 17.75 in NotoSansCJKjp 12 pt "常规"), which this
  section does not cover.
- **Also seen** (as in "Preferred sizes to the hundredth"): widths a hundredth apart where a
  pen falls within a float step of a hundredth, NotoSansCJKkr 18 pt "온도 단위" 70.35 shipped
  and 70.36 here, NotoSansCJKjp 24 pt "Ag" 28.06 and 28.05: 12 texts, 4 of them with the
  same height.

| Font, size, text | line (ascender, descender; top at 30) | shipped preferred height | this implementation |
|---|---|---|---|
| NotoSansCJKjp 24, "全般" (Game options' header) | 30, -5.55 | 35.55 | 35.56 |
| NotoSansCJKjp 24, "全般", rect top at 0 | 0, -35.55 | 35.55 | 35.56 |
| NotoSans 24, "General" | 30, -2.625 | 32.63 | 32.63 |
| NotoSansCJKsc 24, "Единицы температуры" | 30, -2.625 (here -5.53125) | 32.63 | 35.54 |
| NotoSansCJKkr 12, "Единицы температуры" | 30, 13.6875 (here 12.225) | 16.32 | 17.78 |
| NotoSansThai 12, "Единицы температуры" | 30, 13.6875 (here 13.113636) | 16.32 | 16.89 |

### Backspace with modifiers, and the members Text Input Hotkeys patches (measured 2026-10-25)

Found in the load sweep `s2000`: Text Input Hotkeys (Workshop 3165114076, `Aze.DeleteFullWord`)
loads in the original and fails to load here. Harmony throws `InvalidProgramException` while it
patches `TMP_InputField.KeyPressed(Event)`, KMod disables the mod and the player quits while it
loads.

**Measured in the original,** with the build menu's search field (a `KInputTextField`), real
keys through XTEST (`scripts/e2e.py`, scratch specs typing "liquid pump", then a modifier with
Backspace, then "gas", then Ctrl+Shift+Backspace):

| Keys | Shipped package, no mod | Shipped package with the mod | This implementation, no mod |
|---|---|---|---|
| Backspace after "liquid pump" | "liquid pum" | "liquid pum" | "liquid pum" |
| Ctrl+Backspace after "liquid pump" | "liquid pum", caret 10 | "liquid ", caret 7 | "liquid ", caret 7 |
| Ctrl+Shift+Backspace after "…gas" | one character removed | everything before the caret removed (""), caret 0 | the word "gas" removed |

- **The shipped package ignores Ctrl and Shift on Backspace:** it removes the one character
  before the caret, or the selection, whatever the modifiers. This implementation removes the
  word before the caret on Ctrl+Backspace, which differs. Backspace must remove one character
  with any modifiers. Deleting by word is what the mod adds.

**Why the names are interface:** the mod's code (read from its assembly) needs these members of
`TMP_InputField`, under these names and signatures. It was compiled against a copy of the
package with every member public, and ARCHITECTURE D11 lets it reach them:
- `void Backspace()`: private, instance, no parameters. It removes the character before the
  caret (or the selection, with any modifiers, as above). The mod finds it with
  `AccessTools.Method(typeof(TMP_InputField), "Backspace")`, by name alone. An overload with
  other parameters breaks the mod, so there must be exactly one method of that name.
- `EditState KeyPressed(Event evt)`: protected, instance, not virtual (corrected: "`KeyPressed`
  is protected" below). The Backspace key reaches `Backspace()` through a direct call in this
  method, with only the field itself as the call's argument.
  The mod's transpiler swaps that call for its own static `Wrapper(TMP_InputField, Event)`,
  which takes the event (`evt`, the method's first parameter) as well. With any other
  argument on the call, the method that results is invalid (what happens here today:
  `Backspace(bool)` is called with the Ctrl state).
- `int stringPositionInternal { get; }`: an internal property, the caret's position in the
  text (the string index, not the character index). The mod reads it on Ctrl+Shift+Backspace
  as the count of characters before the caret.
- `int FindPrevWordBegin()`: private, instance. The string index where the word before the
  caret begins. The mod deletes `stringPositionInternal - FindPrevWordBegin()` characters on
  Ctrl+Backspace. Measured through the mod: after "liquid pump" (caret 11) it is 7.
- **`Backspace()`'s body is copied by the mod** (a Harmony reverse patch, `MultiBackspace(
  TMP_InputField instance, int charsToRemove)`). Its transpiler finds each call to
  `string.Remove(int, int)` in the body. It replaces the two nearest `ldc.i4.1` before that
  call and the nearest one after it with the count, and leaves all else as it is. So when
  `Backspace()` removes one character, the constant 1 must appear in its IL as `ldc.i4.1`
  exactly three times around that removal. Twice before the `Remove` call: the removal's start
  (caret − 1) and its length (1). Once after it: the caret moving back by 1. In that order, and
  with no other `ldc.i4.1` in between. Measured through the mod: Ctrl+Shift+Backspace
  after "liquid gas" (caret 10) leaves "" and caret 0, so all three take the count. Any
  other `Remove(int, int)` call in the body is rewritten the same way.
  **Out of scope by policy:** Moono supports ordinary Harmony patches on the packages' internals
  (prefixes, postfixes, reflection on members, private ones included), so the members above
  exist with these signatures and are called at the same points. It does not match the shipped
  IL for transpilers and reverse patches, which is too fragile to keep: `Backspace()`'s IL is
  not shaped for this rewrite, and the mod's Ctrl shortcuts are not expected to work.
- Nothing else in the 2119 Workshop builds in `out/ws` names `FindPrevWordBegin` or
  `stringPositionInternal`, apart from copies of the shipped package that mods carry.

### Package members mods reach (measured 2026-10-25)

Found by a survey of the Workshop mods' patches and reflection on the UI packages: members that
mods patch, read by reflection, read from a subclass or call, which this implementation lacks or
calls at other points. ARCHITECTURE D11 supports ordinary Harmony patches (prefixes, postfixes,
finalizers) and reflection on the package's internals, not transpilers. So for each member the
interface is its name, its signature and visibility, and the points it is called at, never its
IL.

**Measured** with `PROBE_SCENARIO=tmpgaps` (tools/UnityProbe, `TmpGaps.cs`, lines `TG …`, 10 s
into the main menu, through `scripts/e2e.py --player original` with OniMcp's mod folder). The
metadata (visibility, `static`, `virtual`, parameter names and defaults, field types) is read
by reflection in the player (`TG meta`). Behaviour is measured on objects built in code under an
overlay canvas: `KInputTextField`s and plain `TMP_InputField`s as `InputText.cs` builds them (160
× 24, NotoSans-Regular 14), a `TMP_Dropdown` with a template in the layout uGUI's documentation
describes, dynamic font assets from `TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book")`, and
8 × 8 sprite assets built as Storage Info Plus builds its own. `PROBE_TG_PART` runs one part.
OniMcp carries PLib, whose `TextMeshProPatcher` prefixes `AssignPositioningIfNeeded`. The
`assign` part runs once with that prefix in place and once after the probe removes it.

#### Where `TMP_InputField.AssignPositioningIfNeeded` is called

**Why:** PLib (`PeterHan.PLib.UI.TextMeshProPatcher`, embedded in 845 catalog mods, applied once
by the newest PLib loaded) puts a Harmony prefix on this method. The prefix takes
`___caretRectTrans` and `___m_TextComponent`. When the caret's eight rect values (below) differ
from the text component's, it starts a coroutine on the field that copies them at the end of the
frame, and skips the method. StorageNetwork (3732422991) has the same prefix. So the method must
be called at the shipped package's points: if this implementation calls it less often, a caret
that drifts from its text is never realigned, with or without PLib.

**Interface (metadata):** `private void AssignPositioningIfNeeded()`, instance, no parameters,
not virtual. The field it reads is `private RectTransform caretRectTrans` ("The input field's
layout values" and "Text set from code" already require both; the caret is made in `OnEnable`).

**What it does** (with PLib's prefix removed): it copies the text component's RectTransform onto
the caret's: `localPosition`, `localRotation`, `localScale`, `anchorMin`, `anchorMax`,
`anchoredPosition`, `sizeDelta` and `pivot`. Measured with a Harmony prefix and postfix: on entry
the caret differs (a new caret: anchoredPosition (−640, −360), 100 × 100; later, the text moved
by a scroll), on return it matches, in every call below, including a direct call through
reflection.

**The points it is called at.** The probe's prefix counts the calls per field and records the
calling methods (`System.Diagnostics.StackTrace`). The points were the same for
`KInputTextField` and `TMP_InputField`, and with PLib's prefix in place or removed:
1. **`OnEnable`, once, when it makes the caret** (enabling a field with a text component and no
   caret yet: `SetActive(true)`, and `Instantiate` of an active field, whose clone makes its own
   caret). A later enable, with the caret already made, does not call it, nor does disabling and
   enabling the component.
2. **While the caret's geometry is generated, when the text is moved to keep the caret in
   view.** That is in the canvas's graphics rebuild (`UpdateGeometry` → `OnFillVBO` →
   `GenerateCaret` → a method that moves the text relative to the viewport → this method),
   after the text component's RectTransform has been moved. Measured:
   - Single line, focused: setting a text longer than the field ("a much longer text that
     scrolls out of the field to the right") and End. The next frame's rebuild moves the text
     to anchoredPosition x −183.82, then calls the method once, and the caret matches the text
     again.
   - Multi-line (`MultiLineNewline`, wrapping on, 160 × 40): five lines and Ctrl+End. The next
     frame's rebuild moves the text up (y 56.3) and calls it once.
   - It is not called in rebuilds where the text stays where it is: typing "d" into "abc", Ctrl+A,
     the caret's blink, or frames with the caret in view.
3. **When the vertical scrollbar's value changes** (a multi-line field with `verticalScrollbar`
   set): the scrollbar's `onValueChanged` reaches the field's handler, which moves the text to
   the scrolled position and calls the method. It was called twice for each change: twice in
   the frame the field was first focused (the scrollbar was set up then), and twice when the
   probe set `Scrollbar.value = 0.2`.

**Where it is not called** (each step logged, then one to three frames): `text =` (focused or
not), typing, Ctrl+A, the text component's RectTransform moved from code (focused or not), the
field resized, `LayoutRebuilder.ForceRebuildLayoutImmediate`, `Canvas.ForceUpdateCanvases`,
`ForceLabelUpdate`, select, `ActivateInputField`, the frames of focus, `DeactivateInputField`,
deselection, disabling and enabling the component, `SetActive(false)` and `SetActive(true)` of
a field that has its caret, reparenting, and assigning `textComponent` or `textViewport`.
It is not called once a frame. (This refines "Input field and dropdown", which says it runs
nowhere but `OnEnable`: that was measured on fields that never scrolled.)

**So a caret drifts in the shipped package too.** After the text component's rect is moved from
code (anchoredPosition (5, 3), sizeDelta (−20, −4)), the caret keeps its old values through
frames, focus and typing, until one of the points above copies them. The same holds after the
text is reassigned to another component.

**With PLib's prefix**, the same calls arrive. The prefix finds the caret different and defers
the copy to the end of the frame: the caret still differs in the frame's `LateUpdate` and
`willRenderCanvases`, and matches in a coroutine waiting for `WaitForEndOfFrame` started after
it.

**Requirement:**
- Keep the method and the field with these names and signatures.
- Call it at the three points: in `OnEnable` right after the caret is made; after the field moves
  its text component's RectTransform to keep the caret in the viewport (horizontally for a single
  line, vertically for multi-line), in the same rebuild; and after the vertical scrollbar's value
  change moves the text (at least once per change; the original calls it twice). Not elsewhere,
  and not once a frame.
- Since the caret's rect follows the text's at those points, the caret geometry must be drawn in
  a rect equal to the text's after them, wherever the text has moved.

**Where this implementation deviated before** (`Input/TMP_InputField.cs`): it calls the method
only from `OnEnable`. It scrolls by moving the text component's RectTransform
(`KeepCaretVisible`, `ApplyScroll`) and draws the caret through an offset between the text's
and the caret's rects, so the caret's rect is never realigned after a scroll and the prefixes
above never run then.

#### `TMP_Dropdown.m_Dropdown`, the shown list

**Why:** Auto Kitchen (自动厨房, 3815900814) arranges its dropdown's list in a `LateUpdate`. It
finds `AccessTools.Field(typeof(TMP_Dropdown), "m_Dropdown")`, and while `IsExpanded` is true it
reads the field as a `GameObject`, takes its `ScrollRect` and `RectTransform`, and lays out the
`Toggle`s under `ScrollRect.content`. It checks for null, so without the field its arranging
turns off silently.

**Interface (metadata):** `private GameObject m_Dropdown` (instance, not serialized). Beside it
the shipped type has `private GameObject m_Blocker`. `IsExpanded` is a public get-only property.

**Measured** (a dropdown with three options, its template `Template` (Image, ScrollRect) →
`Viewport` (Image, Mask) → `Content` → `Item` (Toggle) with background, checkmark and
`TextMeshProUGUI` label):
- **Before the first `Show()`**, and on the game's two `NumericDropdown`s at the main menu: null.
- **`Show()`** sets it at once to the new list: a GameObject named `Dropdown List`, a child of
  the dropdown itself (path `…/tgdd/Dropdown List`), active, with the components
  `RectTransform, CanvasRenderer, Image, ScrollRect, Canvas, GraphicRaycaster, CanvasGroup` in
  that order. That is the template's clone with a `Canvas`, `GraphicRaycaster` and `CanvasGroup`
  added. Its `ScrollRect.content` holds the template's item (inactive) and one item per option:
  4 `Toggle`s with inactive ones, named `Item 0: Alpha`, `Item 1: Beta`, `Item 2: Gamma`.
  The `CanvasGroup` fades in from alpha near 0.
- **A second `Show()` while shown** keeps the same list.
- **`Hide()`** does not clear it. The list fades out (alpha down to 0 over about 0.15 s of
  unscaled time). The field keeps the list, and `IsExpanded` stays true, through the fade. In
  the frame the fade ends, the field becomes null and `IsExpanded` false, and the list
  GameObject is gone the frame after (it is destroyed at the end of the fade). The blocker is
  gone the frame after `Hide()`.
- **Choosing an item** (a toggle's `isOn` set, or a click on another item): `onValueChanged`
  runs with the field still set and `IsExpanded` true. Then it is hidden as by `Hide()`, and the
  field clears when the fade ends.
- **`value` set from code while shown** sends `onValueChanged` with the field set, and leaves
  the list shown.
- **`Show()` and `Hide()` in one frame:** the field is set, and clears when the fade ends.
- **`Hide()` then `Show()` the next frame:** `Show()` makes a new list and puts it in the field,
  while the old one finishes its fade and is destroyed. The field keeps the new one.
- **The dropdown's GameObject deactivated while shown:** the field is null at once and
  `IsExpanded` false. The old list is inactive (its parent is) and gone the next frame.
- **The component disabled while shown:** the field is null at once, `IsExpanded` false, and
  the list is still alive in that frame. It was gone 0.6 s later; not measured frame by frame.
- **The dropdown destroyed while shown:** the list goes with it (it is a child).
- In every step, `IsExpanded` was true exactly when the field was set.

**Requirement:** a private instance field `GameObject m_Dropdown` that holds the shown list from
`Show()` until the hiding fade ends (or the dropdown is disabled), and is null otherwise. The list
GameObject carries the template's `ScrollRect` (with `content` holding one `Toggle` per option)
and its `RectTransform`. `IsExpanded` is true exactly while the field is set.

**Where this implementation deviated before** (`Input/TMP_Dropdown.cs`): the list is held in a
field named `m_List`, so the lookup finds nothing.

#### `TMP_InputField.m_Mesh`, the caret's mesh

**Why:** Applied Logistics Network (3753045682) derives `ALN_InputTextField` from
`TMP_InputField` and reads `base.m_Mesh` in its diagnostics (`CaretStateSnapshot`):
`m_Mesh != null ? m_Mesh.vertexCount : -1`. The field is compiled into its assembly as a field
reference, so without it the method throws `MissingFieldException`.

**Interface (metadata):** `protected Mesh m_Mesh` (instance, not serialized). The shipped type
also has a get-only `protected Mesh mesh` property.

**Measured** (fields with `caretColor` (10, 20, 30, 255), `customCaretColor` on and
`selectionColor` (200, 100, 50, 128); the same for `KInputTextField` and `TMP_InputField`):
- **Null** before the field is first enabled, and still null right after `SetActive(true)`.
- **A new `Mesh` (name "", hideFlags None) from the first canvas rebuild after enabling.** It
  is the caret graphic's geometry, in the caret's local space:
  - not focused: 0 vertices;
  - focused, caret shown: one quad, 4 vertices and 2 triangles, the caret's width (1) by the
    line's height: (−77, −8.03) to (−76, 11) at the start of the text, (−51.95, −8.03) to (−50.95,
    11) after "dabc", every vertex in the caret colour;
  - during the blink's hidden half: 0 vertices;
  - with a selection: one quad per selected line, in `selectionColor`: "ab" of "dabc" selected
    gives 4 vertices from (−68.4, −8.03) to (−51.95, 11) in (200, 100, 50, 128);
  - after `DeactivateInputField`: 0 vertices from the next rebuild.
- **The same `Mesh` object** through all of that: it is refilled, not replaced.
  (`CanvasRenderer.GetMesh()` on the caret returns another copy each upload, with the same vertex
  count.)
- **`SetActive(false)` (OnDisable) sets it to null.** The next enable leaves it null until the
  first rebuild, which makes a new one.
- When it is refilled: in the canvas rebuilds that redraw the caret (focus, each change of the
  blink, a selection made with Shift+Left and `ForceLabelUpdate`: the next frame). After "d" was
  typed through `ProcessEvent` it kept the old caret position for the two frames logged, until
  the blink redrew it.

**Requirement:** a protected instance field `Mesh m_Mesh` (and the protected get-only `mesh`)
that holds the caret graphic's current geometry as above: made on the first caret rebuild after
enable, refilled with the caret quad, the selection quads or nothing, and set to null on disable.
Mods only read `vertexCount`; the vertex values above are what the original holds.

**Where this implementation deviated before:** the caret graphic (`TMP_SelectionCaret`) owns
the work mesh; `TMP_InputField` has no `m_Mesh` or `mesh`.

#### `TMP_InputField.KeyPressed` is protected

**Interface (metadata):** `protected EditState KeyPressed(Event evt)`: instance, not virtual,
parameter named `evt`. (The section "Backspace with modifiers" gives what is called inside it.)

**Why:** Vision Not Included (3683507975), Storage Quotas (3781549526) and ONI Multiplayer
(3807965885) find it by name for Harmony prefixes that inject `evt` or check that it exists;
those work at either visibility. A subclass of `TMP_InputField` in a mod can call it only if it
is protected; no mod in the catalog does so today.

**Requirement:** declare it `protected`, with this signature and parameter name. No change of
behaviour. This implementation declares it private.

#### `TMP_FontAsset.HasCharacters(string, out uint[], bool, bool)`

**Why:** Expeditions: The Shattered Planet (3803506841) checks a candidate font's coverage with
`HasCharacters(characters, out missing, true, false)` and counts `missing?.Length`. ONI
Multiplayer (3807965885) checks one code point at a time with `(text, out missing, true, true)`.
Without the overload both throw `MissingMethodException`.

**Interface (metadata):** `public bool HasCharacters(string text, out uint[] missingCharacters,
bool searchFallbacks = false, bool tryAddCharacter = false)`. Beside it: `HasCharacters(string)`,
`HasCharacters(string text, out List<char> missingCharacters)`, `HasCharacter(int)` and
`HasCharacter(char character, bool searchFallbacks = false, bool tryAddCharacter = false)`.

**Measured** on NotoSans-Regular (static, 1,977 characters, no fallbacks of its own), on dynamic
DejaVu Sans assets (empty tables), with `TMP_Settings.fallbackFontAssets` (11 fonts, the Noto
CJK, Arabic, Hebrew and Thai fonts among them) and `TMP_Settings.defaultFontAsset`
(LiberationSans SDF) as loaded, and with them cleared by the probe:
- **Return:** true when every character is found, else false.
- **`missingCharacters`:** null when the result is true (also for ""), else the missing code
  points in the order of the text, with repeats: "a一一b丁" gives [4E00, 4E00, 4E01].
- **Code points:** a surrogate pair is one code point ("x😀y" on NotoSans: [1F600]); a lone
  surrogate is reported as itself ([D83D]).
- **Control characters are never missing:** U+0003, tab, LF and CR count as found in an empty
  dynamic asset. Space, U+00A0, U+00AD, U+200B and U+2060 do not.
- **`""`:** true, null. **`null`:** throws `NullReferenceException`.
- **`searchFallbacks` false:** only the asset's own character table (and, with
  `tryAddCharacter`, its own atlas).
- **`searchFallbacks` true:** a character not in the asset is looked for, in turn, in its
  `fallbackFontAssetTable` (and their fallbacks, recursively), then in
  `TMP_Settings.fallbackFontAssets`, then in `TMP_Settings.defaultFontAsset`:
  - "一" and "☃" on NotoSans: found (in the global fallbacks); with the global list cleared,
    missing.
  - "abc" on an empty dynamic asset, with the global list cleared: found (in the default font
    asset); with the default font asset cleared too, [61] for "a".
  - A fallback's own fallback is searched: "─" found in a fallback two levels down.
- **`tryAddCharacter` true:** a character the asset lacks is added to it when its
  `atlasPopulationMode` is dynamic and its face has the glyph; `characterTable` grows by one per
  character. With `searchFallbacks`, the asset itself is tried (and added to) first: "eé" on an
  empty dynamic asset adds both to it. Then each fallback in turn: "a☃" on NotoSans with a dynamic
  fallback adds "☃" to that fallback (its table 0 → 1), and with nesting, to the nested one. An
  asset in `Static` mode adds nothing, even with a face that has the glyph. Characters the face
  lacks stay missing ("一" in DejaVu Sans) and are not added. Already present characters are not
  added again.
- With `tryAddCharacter` false, nothing is added anywhere.

**Requirement:** the overload with this signature, parameter names and defaults, behaving as
above.

#### `TextMeshProUGUI.canvasRenderer`

**Why:** Duplicant Status Bar (3682732647) and Schedule & Priority Screen Expander (3774780988)
read `label.canvasRenderer` on a `TextMeshProUGUI`/`LocText`. Their assemblies were compiled
against the shipped type, which declares its own getter, so the call names
`TextMeshProUGUI.get_canvasRenderer`. Without it the call throws `MissingMethodException`.

**Interface (metadata):** `public CanvasRenderer canvasRenderer { get; }` declared on
`TextMeshProUGUI`, not virtual (it hides `Graphic.canvasRenderer`, which is also a non-virtual
getter), no setter. The shipped type keeps it in a private field `CanvasRenderer
m_canvasRenderer`; `TMP_SubMeshUI` declares no such property.

**Measured:** it returns the GameObject's `CanvasRenderer` component (the one
`GetComponent<CanvasRenderer>()` returns, also what `Graphic.canvasRenderer` returns) in every
case: on an inactive GameObject before `Awake`, whether it or `Graphic`'s getter is read first;
after activation; on a text made active at once; after a layout; on a `LocText` in the scene;
and called through reflection on the declared getter.

**Requirement:** declare the getter on `TextMeshProUGUI`, returning the same component as
`Graphic.canvasRenderer`, at every point of the object's life.

#### `TMP_TextUtilities.GetHashCode(string)`

**Why:** Storage Info Plus (3777521625) sets its sprite asset's `hashCode` to
`TMP_TextUtilities.GetHashCode("sip_divider")` and registers it under that key (next section).

**Interface (metadata):** `public static int GetHashCode(string s)`. The shipped type also has
`public static int GetSimpleHashCode(string s)`, `public static uint
GetSimpleHashCodeLowercase(string s)` and `public static uint GetHashCodeCaseInSensitive(string
s)`.

**Measured** (27 strings; the formulas below give every value): the hash runs over the string's
UTF-16 code units, starting at 0, with `hash = ((hash << 5) + hash) ^ c` in 32-bit wrapping
arithmetic, where `c` is the code unit with ASCII `a`–`z` changed to `A`–`Z`. Nothing else is
changed: "é" (233), "ı" (305) and "ß" (223) hash as themselves. So:
- "" → 0, "a" and "A" → 65, "ab" → 2083, "abc", "ABC" and "aBc" → 68800, "a b" → 69667,
  "sip_divider" and "SIP_DIVIDER" → −197458124, "NotoSans-Regular" → 382882690, "😀" (two code
  units) → 1769949, "abcdefghijklmnopqrstuvwxyz0123456789" → 1902480570.
- **null → 0** (no exception).
- The others: `GetSimpleHashCode` is the same without the case change ("a" 97, "sip_divider"
  1165012724) and throws `NullReferenceException` on null. `GetHashCodeCaseInSensitive` gives
  `GetHashCode`'s bits as `uint` (4097509172 for "sip_divider"). `GetSimpleHashCodeLowercase`
  starts at 5381 and changes `A`–`Z` to `a`–`z` ("" 5381, "a" and "A" 177604).
- **The assets' `hashCode`:** every game font and sprite asset reports `GetHashCode(name)`.
  `TMP_Asset.hashCode` (public get and set) returns the stored value; when that is 0, the getter
  computes `GetHashCode(name)`, stores it and returns it. A new sprite asset reads 0 while its
  name is "" (whose hash is 0), then 85746282 after it is named "fresh"; renamed "renamed", it
  keeps 85746282. A value set (12345) is kept through a rename.
- **The assets' `materialHashCode`** (public get and set on `TMP_Asset`) is stored data: the game
  fonts' values are not the hash of their material's name (some are `GetSimpleHashCode` of it).
  An asset from `CreateFontAsset` has `GetSimpleHashCode(name + " Atlas Material")` of its name
  at creation, "", so every such asset has −2006316128, whatever it is named later.

**Requirement:** `GetHashCode(string)` as above (the other three are public in the shipped
package too). `TMP_Asset.hashCode`'s getter fills itself from the name when 0.

#### `MaterialReferenceManager` and `TMP_Text.OnSpriteAssetRequest`

**Why:**
- Storage Info Plus (3777521625) builds a sprite asset "sip_divider" at run time, registers it
  with `MaterialReferenceManager.AddSpriteAsset(asset.hashCode, asset)`, adds a handler to the
  static event `TMP_Text.OnSpriteAssetRequest` that returns it for the name "sip_divider", and
  writes `<sprite="sip_divider" name="sip_divider_line">` into its texts.
- Project Expedition (3803418607) calls `MaterialReferenceManager.AddFontAsset(font)` with a game
  font before it builds texts with it.
- This implementation has no `MaterialReferenceManager` type and no such event, so the methods
  of both mods that name them fail when they are compiled.

**Interface (metadata):** `public class MaterialReferenceManager` (the shipped type has a
constructor) with:
- `public static MaterialReferenceManager instance { get; }`;
- `public static void AddFontAsset(TMP_FontAsset fontAsset)`;
- `public static void AddSpriteAsset(TMP_SpriteAsset spriteAsset)` and `public static void
  AddSpriteAsset(int hashCode, TMP_SpriteAsset spriteAsset)`;
- `public static void AddFontMaterial(int hashCode, Material material)`;
- `public static void AddColorGradientPreset(int hashCode, TMP_ColorGradient spriteAsset)`;
- `public bool Contains(TMP_FontAsset font)` and `public bool Contains(TMP_SpriteAsset sprite)`;
- `public static bool TryGetFontAsset(int hashCode, out TMP_FontAsset fontAsset)`,
  `TryGetSpriteAsset(int hashCode, out TMP_SpriteAsset spriteAsset)`, `TryGetMaterial(int
  hashCode, out Material material)` and `TryGetColorGradientPreset(int hashCode, out
  TMP_ColorGradient gradientPreset)`, all static.

On `TMP_Text`: `public static event Func<int, string, TMP_SpriteAsset> OnSpriteAssetRequest`
and, beside it, `public static event Func<int, string, TMP_FontAsset> OnFontAssetRequest`.

**Measured, the registry:**
- **Initially** no game font is registered (NotoSans-Regular, NotoSans-Bold, GRAYSTROKE REGULAR
  SDF, RobotoCondensed-Regular), not even the font every main-menu text uses, nor the default
  sprite asset (XB360Sheet, `TMP_Settings.defaultSpriteAsset`).
- **`AddSpriteAsset(int, asset)`** registers the asset under that key. A second add under the same
  key keeps the first asset and throws nothing. `AddSpriteAsset(asset)` registers it under
  `asset.hashCode`.
- **`AddFontAsset(font)`** registers the font under `font.hashCode` and its material under
  `font.materialHashCode` (`TryGetMaterial(materialHashCode)` returns `font.material`). Adding the
  same font again does nothing and throws nothing, even when its material key is taken. Another
  font under a font key already taken is ignored (the first
  stays). Another font whose `materialHashCode` is taken (two `CreateFontAsset` assets, which
  share one value) is registered as a font, and then the material's add throws
  `ArgumentException` ("An item with the same key has already been added. Key: −2006316128") out
  of `AddFontAsset`. Two game fonts that share one material object (GRAYSTROKE NAME SDF and
  GRAYSTROKE OUTLINE SDF) have different `materialHashCode`s and both add. `AddFontAsset(null)`
  throws `NullReferenceException`.
- A font renamed after it was added stays under its old key (its `hashCode` does not change).

**Measured, sprite tags** (`<sprite="NAME" index=N>` or `name="…"`, laid out by `ForceMeshUpdate`
and by the frame's rebuild):
- **The asset is looked up by `GetHashCode(NAME)` in the registry**, so case does not matter
  (`"PROBE_REG"` finds `probe_reg`), and an asset registered under another key is found only by
  that key (an asset named `probe_x` registered under the hash of "probe_alias" is found by
  `<sprite="probe_alias" …>`, not by `<sprite="probe_x" …>`). The text's own `spriteAsset`, even
  when it is the asset named in the tag, is not found by name, and neither is a live sprite
  asset that is not registered.
- **When it is not registered, `OnSpriteAssetRequest` is raised** with `(GetHashCode(NAME),
  NAME)`:
  - **The handler returns an asset:** the sprite is drawn from it, and the asset is registered
    under that key: `TryGetSpriteAsset` finds it afterwards, and later layouts of that name do
    not raise the event again, even after the handler stops returning it.
  - **The handler returns null (or there is no handler):** the tag stays in the text as
    literal characters (`a<sprite="probe_unreg" index=0>b`, 32 characters), and the event is
    raised again in every later layout: twice per `ForceMeshUpdate` and per frame rebuild that
    lays the text out, three times for `GetPreferredValues`. Setting `text` alone does not
    raise it; the layout does.
  - **Several handlers:** the event is invoked as one multicast delegate; all handlers run, and
    only the last one's return value counts (a non-null asset from the first, then null from the
    second, leaves the tag as text).
  - **Unquoted:** `<sprite=probe_unreg index=0>` raises it with the name `probe_unreg index=0`
    (the rest of the tag) and its hash.
  - It is not raised for `<sprite=N>` (the default sprite asset: XB360Sheet's sprite N), for
    `<sprite name="…">` without an asset name (a name the default sprite asset lacks leaves the
    tag as text), or with `richText` off. After the handler is removed it is not raised.
- **In a found asset:** `index=0` gives sprite 0; an index past the end leaves the tag as text;
  `name="reg_line"` gives that sprite, and a name the asset lacks gave sprite 0.
- The sprite adds a material: `textInfo.materialCount` 2 for a text with one sprite.

**Measured, font tags** (`<font="NAME">…</font>`):
- **Looked up by `GetHashCode(NAME)` in the registry**, case-insensitive and quoted or not.
  `<font="default">` is the text's own font.
- **Not registered:** the tag stays as literal text: game fonts by name ("Economica-Bold-OTF SDF",
  "GRAYSTROKE NAME SDF"), even the text's own font ("NotoSans-Regular"), and an unknown name.
  The text keeps its font for what follows.
- **Registered by `AddFontAsset`:** the characters inside use it (`[ProbeFontA]b`, a second
  material). A font registered with `hashCode` set to the hash of "AliasC" is found by
  `<font="AliasC">`, not by its name. A font renamed after registration is found by its old
  name only.

**Requirement:** the `MaterialReferenceManager` type with these static members, keyed by the
hashes above; sprite and font tags that look their assets up in it by `GetHashCode(NAME)`; and
`OnSpriteAssetRequest`, raised (with the hash and the name) for each sprite tag whose asset is not
registered, whose last handler's non-null result is drawn and registered. What this
implementation finds today by searching loaded objects by name (`TextParser.FindFont`,
`FindSpriteAsset`) differs: the original finds no unregistered asset by name. Not measured:
assets under `Resources` paths (`TMP_Settings.defaultFontAssetPath`, "Sprite Assets/"), which
the game has none of for these names, and `OnFontAssetRequest`.

#### Tags naming assets under Resources (measured 2026-10-26)

**Why:** the game's own sprite tags name `oni_sprite_assets` (`<sprite="oni_sprite_assets"
name="oni_sprite_assets_germs">` and the other freshness, battery and syrup tree icons), which
no code registers; it is at `Sprite Assets/oni_sprite_assets` in Resources. The only font tag in
the game's code, in TextMeshPro's example scripts, names `Bangers SDF` with the material `Bangers
SDF - Drop Shadow`, both at `Fonts & Materials/`. The paths are `TMP_Settings.defaultFontAssetPath`
("Fonts & Materials/") and `defaultSpriteAssetPath` ("Sprite Assets/").

**Measured** with `PROBE_TG_PART=resources` (lines `TG res …`), on a NotoSans text, with a handler
on each request event that returns null:
- `<sprite="oni_sprite_assets" …>`: `OnSpriteAssetRequest` is raised once with
  `(GetHashCode(NAME), NAME)`; then the sprite is drawn from the asset under Resources, and the
  asset is registered under that hash (`TryGetSpriteAsset` true afterwards; the next tags of
  that name, in any case, raise nothing). The same for `EmojiOne`.
- `<font="Bangers SDF" material="Bangers SDF - Drop Shadow">BANG</font>`: `OnFontAssetRequest` is
  raised once, then the font under Resources is used and registered under the tag's hash, and
  the material under Resources is used and registered under `GetHashCode(material name)`. Any
  case finds it afterwards. `<font="LiberationSans SDF">` likewise.
- An unknown font: `OnFontAssetRequest` is raised twice per `ForceMeshUpdate`, and the opening
  tag stays as text. Its closing tag `</font>` is taken as a tag all the same
  (`a<font="ProbeNoSuchFont">bc`), as is a closing tag with no font open (`ab</font>c` shows
  "abc").
- `<material="LiberationSans SDF - Outline">` finds the material under Resources.

**The assets' `materialHashCode` read from the game's fonts** (`TG hash font-material`): for most
it is `GetSimpleHashCode(name + " Atlas Material")` of the font's name (GRAYSTROKE REGULAR,
OUTLINE and ITEM DROP SDF, LiberationSans SDF and its fallback, the Noto fonts), the value an
asset made by `CreateFontAsset` has too. Two are `GetSimpleHashCode` of their material's name
(Economica-Bold-OTF SDF, GRAYSTROKE NAME SDF). No serialized field holds it.

**Requirement:** a tag's asset not in the registry is asked for with the request event first,
then loaded from the Resources path for its kind; what either gives is registered under the
tag's hash. A `materialHashCode` not set is `GetSimpleHashCode(name + " Atlas Material")`, so
fonts that share one material keep keys of their own (both GRAYSTROKE fonts above add).

### A font set to null (measured 2026-10-26)

**Why:** Smart Coolers (2990501709) builds its side screen with PLib. PLib's checkboxes give
their `LocText` labels a `TextStyleSetting` made with `new` and no `sdfFont`, so the game's style
code sets the text's `font` to null when the label wakes. In the shipped package the labels lay
out in the default font ("Cool Contents", Bold 14 pt: 100.65 wide, so the checkbox is 123.65
wide). In this implementation the font stays null and nothing is laid out: `textInfo` counts 0
characters, the text's preferred width is 0, the checkbox is 23 wide and "Heat Contents" has
nothing to click.

**Measured** with `PROBE_SCENARIO=modtext PROBE_MX_PART=font` (tools/UnityProbe, `ModText.cs`,
lines `MX font …`), at the main menu. The default font asset (`TMP_Settings.defaultFontAsset`)
is LiberationSans SDF.
- On a `TextMeshProUGUI` that has woken, `font = null` reads back the default font asset at
  once, and `fontSharedMaterial` becomes that font's material. "Cool Contents" then lays out in
  it (36 pt: 13 characters, preferred width 228.56), after `ForceMeshUpdate` and after the
  frame's rebuild alike.
- The same on a text that was already laid out in NotoSans-Regular (preferred width 237.57):
  right after `font = null` the getter returns the default font, the material is the default
  font's and the preferred width is 228.56, before any `ForceMeshUpdate`.
- On a text that has not woken (added to an inactive object), `font = null` reads back null
  until the object is activated; from then on it reads the default font, and the text lays out
  in it.
- PLib's sequence (an inactive object, `AddComponent<LocText>`, its `key` set to "" and its
  `textStyleSetting` to `new TextStyleSetting { fontSize = 14, textColor = black, style = Bold,
  enableWordWrapping = false }`, then activation, `alignment` Center and the text): the font
  reads LiberationSans SDF after activation, and "Cool Contents" lays out with 13 characters,
  preferred width 100.65 and height 15.65. With NotoSans-Regular as the style's `sdfFont` the
  text uses NotoSans (97.43 × 19.04).
- A `LocText` with the game's default style given `font = null` after waking: the default font,
  laid out.

**This implementation:** the getter returns null in every case above after `font = null`, the
material stays the previous one, and the text lays out nothing (0 characters, preferred size
0 × 0).

**Requirement:** setting `font` to null on a text that has woken makes it use the default font
asset, as if that asset had been assigned: the getter returns it, the shared material becomes
its material, and the layout follows. A text that has not woken keeps null until it wakes, and
takes the default font asset then (as a text without a font does).

### The ellipsis glyph in Bold and Italic texts (measured 2026-10-26)

**Why:** Rookie Quest Guide (3738243630) makes its sidebar's material headers ("Pitcher Pump",
"Wash Basin", "Outhouse") as `TextMeshProUGUI`s in the default font, 12 pt, Bold, with
`overflowMode` Ellipsis, in rects they fit (238 × 22). The shipped package gives each a second
material and a `TMP_SubMeshUI` child although no ellipsis is shown (`textInfo.materialCount` 2);
this implementation has one material and no sub-mesh.

**Measured** with `PROBE_SCENARIO=modtext PROBE_MX_PART=ellipsis,bold` (lines `MX ellipsis …`,
`MX bold …`), labels built as the mod builds them (text, size, alignment MidlineLeft, style,
then the mode), at widths 238 and 30 or 40. LiberationSans SDF has no typefaces in its font
weight table. The global fallbacks end with Roboto-Regular SDF, whose table has Roboto-Italic SDF
as the italic of weight 400 (entry 4), and Roboto-Bold SDF and Roboto-BoldItalic SDF as the
regular and italic of weight 700 (entry 7). LiberationSans SDF holds U+2026 itself.
- **Ellipsis mode with a style the primary font has no typeface for** takes its ellipsis from
  the first typeface for that style found in the fallback search: Bold from Roboto-Bold SDF,
  Italic from Roboto-Italic SDF, Bold and Italic from Roboto-BoldItalic SDF. That font's material
  is part of the text from the start. Each layout counts it as a material even when the text
  fits and nothing is cut. It is material index 1, ahead of fallbacks the text's own characters
  need (a "▪" from LiberationSans SDF - Fallback comes second, index 2, and `materialCount` is
  3). Its sub-mesh exists with an empty quad (4 vertices). An empty text in this mode has the
  material count 2 as well.
- When the text is cut, the ellipsis is drawn from that font and material: "Pitcher Pump", Bold,
  30 wide: "Pit", "…" from Roboto-Bold SDF (material 1, advance 4.965 at 12 pt), then the blank
  record; 40 wide: "Pitch", the ellipsis and the record (7 records). Italic, 30 wide: "Pitc", "…" from
  Roboto-Italic SDF (advance 3.645).
- A "…" typed in the text is an ordinary character: from LiberationSans SDF in every style (Bold:
  advance 12.84, the font's own glyph widened as Bold is), material 0, and no extra material in
  Overflow mode.
- Normal style in Ellipsis mode: the ellipsis is LiberationSans SDF's own, and no extra material
  or sub-mesh appears (material count 1).
- Not measured: a primary font whose own weight table has the style's typeface, and Underline
  or other styles.

**This implementation** takes the ellipsis from the primary font in every style, so a Bold or
Italic Ellipsis text has no extra material or sub-mesh. Where it cuts, its ellipsis is the
primary's glyph, widened for Bold (12.84), so fewer letters fit: "Pitcher Pump" Bold at 40
wide shows "Pitc" and the ellipsis where the shipped package shows "Pitch".

**Requirement:** in Ellipsis mode, resolve the ellipsis character for the text's style before
the layout. If the primary font has no typeface for the style, search the fallbacks, as for a
missing character, for a font whose weight table has a typeface for the style, and take the
ellipsis from that typeface. Count its material among the text's materials, ahead of those of
the characters, with its sub-mesh, whether or not the ellipsis is drawn. With no such typeface,
use the primary font's ellipsis as now.

**Also seen** (not needed by the mod): `HasCharacter('…', searchFallbacks: true)` is true on
LiberationSans SDF - Fallback and NotoSansThai-Regular in the shipped package (the character is
in a global fallback), false here; the blank record that ends a cut text reports LiberationSans
SDF as its font asset there and null here.

### Line gaps and preferred heights with fallback glyphs (measured 2026-10-26)

**Why:** Social Life (3775109476) shows a summary of five lines in GRAYSTROKE REGULAR SDF 13 pt
(`LocText`, wrapping, top left, 740 wide), whose first and last lines have a "·" (U+00B7) that
GRAYSTROKE lacks; it comes from NotoNaskhArabic-Regular. The text's preferred height is 85.14 in
the shipped package and 85.17 here. Rookie Quest Guide's disclaimer (default font 9 pt, wrapping,
1212 wide, two lines of CJK characters from NotoSansCJKjp and NotoSansCJKsc) is 26.96 high there
and 26.66 here.

**Measured** with `PROBE_SCENARIO=modtext PROBE_MX_PART=lines` (lines `MX lines …`: the mods'
texts and variations) and `PROBE_MX_PART=pref PROBE_MX_PREF=2` (lines `MX pref …`: two- to
four-line texts in GRAYSTROKE 13 pt with "·" from NotoNaskhArabic and in LiberationSans SDF 9 pt
with "免" from NotoSansCJKjp, placed first, last or between, with white space, `<size>` and
`<voffset>`, wrapping on and off). Each logs the preferred height in a rect 0 high, each line's
ascender, descender, baseline and line height and each character's ascender, descender and
baseline in a rect 1000 high. `tools/UnityProbe/mx-check.py rule LOG` checks both rules below
against a log: the shipped package matches all 136 line intervals and all 100 preferred
heights; this implementation 48 and 52.

The faces involved (pointSize, lineHeight, ascentLine, descentLine; scale 1): GRAYSTROKE 124,
130.0625, 117.6875, −12.4375; NotoNaskhArabic 32, 54.5, 34.1875, −20.25; LiberationSans 86,
98.8916, 77.8535156, −18.22461; NotoSansCJKjp 30, 44.375, 34.8125, −9.625; NotoSansCJKsc 32,
47.375, 37.125, −10.25. Below, a character's ascender and descender are those `characterInfo`
reports, relative to its baseline, and the primary font's line gap G is its lineHeight less its
ascent and its descent's depth, scaled to the text's size: GRAYSTROKE 13 pt −0.006552,
LiberationSans 9 pt 0.294432.
- **A line's height** (`lineInfo.lineHeight`) is the largest (ascender − descender) among its
  characters, plus G. A character from a fallback font contributes its own ascender and
  descender, not its font's lineHeight: the first summary line is 22.115234 − 0.006552 =
  22.1086826 high (NotoNaskhArabic's lineHeight would give 22.140625, as this implementation
  reports), and the disclaimer's lines 13.33125 + 0.294432 = 13.6256838 (NotoSansCJKjp's
  height; NotoSansCJKsc's is smaller). G is the text's size even in a `<size>` (measured with
  `<size=150%>` lines); a `<voffset>` moves ascender and descender together and leaves the
  height.
- **The interval** from line k−1's baseline down to line k's is line k−1's height less line
  k−1's ascender, plus line k's ascender (its final one, after it grows part-way, "Lines that
  grow part-way" above). The height and the ascender are each the line's largest; they need not
  come from one character. "免责声明\nAB" (9 pt): 13.6256838 − 10.44375 + 8.147463 = 11.32939.
  This implementation takes the largest of the characters' own (lineHeight − ascent) instead,
  with each fallback font's lineHeight: 11.03027 there, and 20.59015 for 20.558228 in the
  summary.
- **The preferred height** is the first line's top less the lowest of these bottoms:
  - the last line's descender (as the laid-out lines give it);
  - on each line after the first that has a visible character after its first character: the
    first character's descender below a baseline lowered by L, where L is the line before's
    ascender less that first character's ascender, when L is positive.
  The second term is how the shipped package's preferred height exceeds the extent of the laid
  out lines. Only a last line could show it here; on earlier lines it lies above later lines'
  bottoms. Then the rounding of "Preferred sizes to the hundredth" applies.
  - "·\nB" 35.76 (the lines' extent, 35.7508); "·\nBB", "·\n B" and "·\nB B" 37.31 (L = 13.888672
    − 12.338227 = 1.550445); "·\nB " 35.76 (the space after "B" is not visible); "·\nB\nBB" 49.39
    (the line before is "B": L = 0); "B\n·\nBB" 50.94. "·\nBB·" 44.23: the second term lies
    above the "·"'s own descender.
  - "免\nAB" 25.98 for an extent of 23.6804 (L = 10.44375 − 8.147463); "免\nB免B" 28.28: L less
    the difference of the two descenders, 2.2963 − 0.9803. "<size=150%>B</size>\nBB" (13 pt)
    40.27 for 34.10; "·\nB<size=150%>B</size>" 43.48: the first B's descender, though the
    larger B's is lower in the laid-out line.
- The mods' texts follow: the summary 85.14 (its last line has a "·"), the disclaimer 26.96
  (from the line height alone), Social Life's "Couples: 0 · Cold wars: 0\nColony: Neutral (0)"
  37.31 (here 35.79). Texts whose glyphs are all the primary font's, or whose fallbacks are no
  taller than it (NotoSans-Regular's "·"), are unchanged.

**Requirement:** take each character's part in its line's height as its ascender less its
descender plus the primary font's line gap at the text's size, take the line's height and
ascender as separate maximums, and space the lines by the interval above. Compute the preferred
height with the extra bottom candidate above.

### Mesh buffers made on a mesh that holds more (measured 2026-10-29)

**Why:** CalYu's Building Pack (2995808112, `tests/e2e/mods/2995808112-calyus-building-pack.yaml`)
names its buildings in Chinese, so the build menu's and the product card's `LocText`s draw
characters from fallback fonts in sub-meshes. When a text is rebuilt with a new buffer
(`new TMP_MeshInfo(mesh, size)`) on a mesh that already holds the triangles of more quads than
the new buffer, this implementation logs the engine's error `Mesh.vertices is too small. The
supplied vertex array has less vertices than are referenced by the triangles array.` (from
`TMP_MeshInfo..ctor` under `TMP_Text.BuildGeometry`, `TextMeshProUGUI.Rebuild`,
`CanvasUpdateRegistry`), followed by `Mesh.normals is out of bounds …` and the same for
tangents, uv and colors, and the game opens its crash dialog. The shipped package logs nothing.

**Measured** with `PROBE_SCENARIO=meshshrink` (`tools/UnityProbe/MeshShrink.cs`, lines `MS …`,
10 s into the main menu). The engine's `Mesh` is not the difference: in both players a mesh
with 8 vertices and the triangles of two quads (indices up to 7) refuses 4 vertices through the
`vertices` setter and every `SetVertices` overload (`DontValidateIndices` too) with that error,
and keeps its 8 vertices. After `Clear()` (or `triangles` set first to one quad) the same 4
vertices are taken without a log. Then, on the shipped package:
- `new TMP_MeshInfo(mesh, 1)` on that two-quad mesh logs nothing. The mesh afterwards has 4
  vertices, 4 normals of (0, 0, -1), 4 tangents and 6 triangle indices (one quad, up to index
  3), and bounds of zero centre and size. The buffer's `mesh` is the mesh passed in.
- On a mesh that had no uv or colour channel (5 quads of positions and triangles, or a new
  empty mesh), the new buffer's mesh has positions, normals, tangents and triangles but no uv
  and no colours (`uv` and `colors32` read empty). On the two-quad mesh above, which had uv
  and colours, they read 4 entries of leftover memory after the constructor (uv
  (-431602080, -431602080), colours (205, 205, 205, 205)): kept by the clear, not set. With 2
  quads, the size the mesh already had, they keep their old values.
- Constructors in a row on one mesh (1, 4, then 1 quads) and `ResizeMeshInfo` from 4 to 1 and
  from 1 to 8 quads log nothing either; the mesh holds 4, 16, 4, 4 and 32 vertices, with
  normals and tangents, and no uv or colours when the first constructor's mesh had none.

So the shipped constructor empties the mesh, keeping its vertex layout, before it sets the new
buffer's positions, normals, tangents and triangles, and does not hand the mesh its uv or
colours. The probe's `clear*` lines show the engine's side of that: `Clear()` and `Clear(true)`
keep the channels the mesh had, which the next vertices give the new vertex count, and
`Clear(false)` drops them (this engine follows it since 2026-10-29).

**This implementation** sets the positions on the mesh without emptying it first, so a mesh that
held more quads than the new buffer refuses them and the error is logged; it also sets uv, uv2
and colours in the constructor.

**Requirement:** a new `TMP_MeshInfo(mesh, size)` given a mesh must leave that mesh holding the
new buffer's quads whatever the mesh held before, without any log: positions, normals, tangents
and the quads' triangles, bounds zero, and uv and colours not set by the constructor. Check it
with the probe's `tmp.*` lines (`diff` of the two players' `MS` lines shows no difference in
them) and with the spec above, which must reach its last step without the crash dialog.

### U60's package (Unity 6000.3.17f1, game build 761015)

U60 ships a newer TextMeshPro. Against our package, the game's assemblies need one more member:

- `TMP_Text.SetCharArray(ReadOnlySpan<char> sourceText)`, public. It behaves exactly as
  `SetCharArray(char[] sourceText)` given an array holding the span's characters in order (an
  empty span as an empty array). The game's `LocText.SetContent` calls it for most UI text, so it
  must not allocate a string per call; the text property, layout, preferred sizes and events are
  those of the `char[]` overload.

The shipped `TMP_InputField` also calls three Windows functions of its own (`GetFocus`,
`GetWindowRect`, `ClientToScreen`), private members whose purpose is not yet measured; the
probable one is placing the IME candidate window near the caret. To be measured in the original
U60 player on Windows with an IME before anything is specified for it.

### Indents opened part-way along a line (measured 2026-11-01)

**Why:** the details screen and the product card write a nested descriptor as a bullet between
two indents: `<indent=1em>•<indent=1.75em><b><style="KLink"><link="TOXICSAND">Polluted
Dirt</style></b></link>: 110 g/s</indent>`. Shangri-La (2891557203), Chlorine Sublimation
Station (3786962460) and Cockpit Commode (3814966327) show such lines. In NotoSans-Regular
12 pt that line's preferred width is 144.25 in the shipped package and was 139.75 here: this
implementation took an indent only where a line starts, so the text after the bullet followed it
at 16.5 (the bullet is 4.5 wide) instead of starting at the inner indent, 21.

**Measured** with `PROBE_SCENARIO=indent` (`tools/UnityProbe/IndentWidth.cs`, lines `IN …`, 10 s
into the main menu): 28 texts in NotoSans-Regular and GRAYSTROKE REGULAR SDF at 12, 14, 16 and
20 pt, set up as `SetTextStyleSetting` does, top left at pivot 0 in a 400 × 100 rect, unwrapped,
and the texts with several lines or a long line also wrapped at 60. Each logs the preferred
sizes (also from `GetPreferredValues`), each character's origin, advance and vertices, each
line's metrics and the links. `TmpIndentTests` encodes the values. In the shipped package:
- **An indent opened part-way along a line moves the pen to it**, measured from the line's left
  edge, ahead or back. In NotoSans-Regular 12 pt, `<indent=1em>•<indent=1.75em>Polluted Dirt`
  has the bullet at 12 and the "P" at 21 (at 20 pt: 20 and 35); `<indent=1em>•<indent=1.2em>…`
  has the "P" at 14.4000006, over the bullet; `<indent=2em>ABCD<indent=1em>Polluted Dirt` has
  "ABCD" from 24 and the "P" back at 12; `<indent=1em>•<indent=1.75em>X<indent=4em>Y` has the
  "Y" at 48. A third indent goes to its own value (`<indent=2.5em>` after two: 30).
- **Closing an indent leaves the pen where it is**: `<indent=1em>•</indent>Polluted Dirt` has
  the "P" right after the bullet, at 16.5, as `<indent=1em>•Polluted Dirt` does.
- **Later lines start at the indent in force**: after a line feed or a wrap the next line starts
  at 1.75 em (21), and a line that opens its own indents starts at the first of them
  (`…\n<indent=1em>•<indent=1.75em>…` has its second bullet at 12 and text at 21 again). An
  indent opened where a line starts is where that line starts (`<indent=1em><indent=1.75em>A`:
  the "A" at 21).
- **`<pos>` is measured from the line's left edge too**: `<indent=1em>•<pos=1.75em>Polluted
  Dirt` has the "P" at 21, as with the inner indent (this implementation put it at 33,
  the line's indent plus the position).
- **Percent indents are of the rect's width**, for the preferred width as well as the layout:
  `<indent=10%>•<indent=20%>Polluted Dirt` in the 400-wide rect has the bullet at 40 and the
  "P" at 80 (exactly; 10 × 0.01 × 400 in floats is 39.999996), and its preferred width is
  150.88, from `preferredWidth` and `GetPreferredValues` with or without a width (this
  implementation gave NaN: it took the percentage of the unbounded width it measures with).
- **The preferred width** follows from the pens with the rule of "Preferred sizes to the
  hundredth": the widest line's pen at the end of its last visible character, measured from
  the left edge, so the jump to the inner indent counts in full. The details screen's line is
  144.25 (its last character, "s" of "g/s", ends at 144.242508); without the link and bold,
  139.57. That end is the pen plus the glyph's advance rounded to float once from the left
  edge, not from the line's indent: `<indent=1em>•<indent=3em>Polluted Dirt` in NotoSans 16 pt
  ends at 142.499995, the float 142.5, and is 142.51 wide; measured from the line's indent (16)
  the end is the float 126.49999, which gives 142.5. `<indent=2em>ABCD<indent=1em>Polluted
  Dirt` ends at the float 110.49999 and is 110.5 wide.
- **Links and styles** take no part: `<link>` around the text after the inner indent leaves the
  pens as they are, and the link's first character is the "P".
- **`lineInfo.length` counts from the line's left edge**, its indent included: "•Polluted Dirt"
  is 75.125 long, `<indent=1em>•Polluted Dirt` 87.125, the details screen's line 143.680008
  (this implementation left the line's starting indent out: 75.125, 131.68).

Also seen, not part of these rules (they hold without indents too, as far as the probe shows):
`lineInfo.width` is the rect's width plus 0.0001 (400.0001; here 400), and for the wrapped
texts `GetPreferredValues(text)` gives the unwrapped height (32.66 for two lines in NotoSans 12
pt) and `GetPreferredValues(text, 60, 100)` the wrapped width (59.44), where this
implementation gives the values of `preferredWidth` and `preferredHeight` for both. Measure
them without indents before relying on them.

Not measured: `<line-indent>` together with an indent opened part-way along a line, `<pos>` at
the start of an indented line, percent indents with margins, and em indents inside `<size>`.

**This implementation** now follows these rules. Against the shipped package all 248 preferred
sizes match, and every pen of the 84 texts to the bit, with the pens summed as "Pens to the
bit" says.

## Verification

1. **Build:**
   - `scripts/build-packages.sh` builds the package, and ApiCheck reports nothing missing for
     the game.
   - The mixed check covers Unity's shipped uGUI and Timeline: while they are Unity's, your
     TextMeshPro calls into the shipped uGUI, so use only its public and protected API.
2. **Unit tests** (`tests/OniMoono.UnityEngine.Tests`, with a reference to the package
   project):
   - rich-text parsing;
   - wrapping and the line-breaking rules;
   - each alignment;
   - each overflow mode;
   - auto-size;
   - kerning;
   - `textInfo` contents;
   - the `TMP_TextUtilities` hit tests;
   - the input field's editing and validation;
   - dynamic font assets, through the engine's FontEngine;
   - the style sheet.
3. **Differential runs** against the original Unity player (`scripts/e2e.py --player both`):
   - The `menu` and `colony` scenarios, plus scenarios you add for long texts with wrapping and
     auto-size, localization fonts, links, the input fields and the dropdowns.
   - No differences beyond the tolerance in the geometry hashes, `textInfo` counts and font
     sizes, or each one explained.
4. **Performance:** DeCrew's frame rate is not worse than with the shipped package.
