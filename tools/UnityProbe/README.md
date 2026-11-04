# UnityProbe

A mod for the original game, built against the local install's assemblies, that measures how
Unity's player behaves where documentation is silent (lifecycle timing around `Destroy`,
reactivation of objects awaiting destruction, `OnApplicationQuit` order). The engine's behaviour
in those areas follows its output; `tests/OniMoono.UnityEngine.Tests/LifecycleTests.cs` encodes
the results.

Use it only with a scratch copy of the game and a scratch profile (`XDG_CONFIG_HOME`), never the
player's own mods folder:

1. `dotnet build -c Release` (set `OniManaged` if the game is not at `~/projects/oni-linux`).
2. Copy `SemanticsProbe.dll` with a `mod.yaml` (`staticID: SemanticsProbe`) and a `mod_info.yaml`
   (`supportedContent: ALL`, `APIVersion: 2`) into the scratch profile's
   `Klei/Oxygen Not Included/mods/Local/SemanticsProbe/`.
3. Start the original once so it lists the mod in `mods/mods.json`, set the entry's `enabled` to
   `true` and `enabledForDlc` to `[""]`, and start it again.
4. Read the `PROBE` lines in `Player.log`.

What it records (each line prefixed `PROBE f<frame>`): lifecycle scenarios (Destroy, activation,
coroutines, start/late-update timing; Start order, scenarios 12 to 19; Destroy of objects whose
Start is pending, scenario 20, alone with `PROBE_SCENARIO=startdestroy`; Destroy from an Awake
while the object activates, alone with `PROBE_SCENARIO=awakedestroy`, lines `AD …`, ending with
an `AwakeDestroyProbeDone` canvas), the Duplicants' spawn
order after a save loads (`MINION`, `KSTART`: build against the original's own `Managed` with
`-p:OniManaged=…`, start the colony after `=== values: screen`), engine values (`VAL …`: formatting, maths, JSON,
hierarchy queries, Instantiate order, Instantiate of parts awaiting Destroy and the points in
the frame where Destroy is carried out, SendMessage, screen, AddComponent's refusals), and UI layout dumps (`UI …`) of the
front end 8 s after it loads and of the in-game overlay canvases once the clock has run for 120
frames. Compare the two players' outputs with frame numbers taken relative to each scenario
header.

With `PROBE_SCENARIO=canvas` only the canvas probe runs (`CanvasProbe.cs`, lines `CANVAS …`, at the
main menu): which disabled Behaviours the FindObjects family returns, and whether nested canvases
under a disabled root canvas (or an inactive one) draw, take raycasts and keep their root. Set
`PROBE_CAPTURE_DIR` for its screenshots, read back by pixel; `CanvasTests` and `SceneRootsTests`
encode the results.

With `PROBE_SCENARIO=canvasorder` only the canvas order probe runs (`CanvasOrder.cs`, lines `CO …`,
in a colony, 120 frames after it loads): the draw order of root overlay canvases that tie on
sorting order, after one change at a time (enabling, activation, sortingOrder set, reparenting,
nested canvases, instantiation, sibling order). Each step is a screenshot in `PROBE_CAPTURE_DIR`;
`co-check.py DIR [DIR2]` prints the canvases bottom to top for each. `CanvasTests`
(`CanvasesTiedOnSortingOrderDrawInListOrder`) encodes the results. It and the draw order probe
run in a colony through `scripts/e2e.py`: copy OniMcp's mod folder (E2E_ONIMCP) with
`SemanticsProbe.dll` added, point E2E_ONIMCP at the copy, set `PROBE_SCENARIO` and
`PROBE_CAPTURE_DIR`, and run a spec that waits for `**/CanvasOrderProbeDone` (or
`**/DrawOrderProbeDone`, with the camera at [109, 209], orthographic size 4).

With `PROBE_SCENARIO=draworder` only the draw order probe runs (`DrawOrder.cs`, lines `DO …`): pairs
of transparent Graphics.DrawMesh quads at z -23.5 (as KAnimBatchManager draws), at the same
distance, a float step apart and 1e-4 apart, submitted in both orders, with their materials
created in both orders and with unit or float.MaxValue bounds. `do-check.py LOG DIR` prints which
quad is on top; `DrawMeshTests` (`TransparentDrawsAtTheSameDistanceGoByMaterial`) encodes it.

With `PROBE_SCENARIO=renderercull` only the renderer culling probe runs (`RendererCull.cs`, lines
`RC …`, 10 s into the main menu): the time of `Camera.Render()` with 30,000 more MeshRenderers,
disabled, on inactive objects, under inactive parents or enabled out of view; when enabling,
disabling, activation and creation from `OnPreCull`, `OnPreRender` and `OnBecameVisible` take
effect; and which of two renderers tied on depth draws last after each change. It runs through
`scripts/e2e.py` with OniMcp's mod folder as above and `tests/e2e/renderer-cull-probe.yaml`;
compare the players' `RC when` and `RC tie` lines. `RendererCullTests` encodes it.

With `PROBE_SCENARIO=drawmeshnow` only the DrawMeshNow probe runs (`DrawMeshNow.cs`, lines `DMN
…`, 10 s into the main menu, a few seconds): meshes drawn with Graphics.DrawMeshNow into a 16 × 16
render texture and read back as a character map, outside rendering and from a camera's
`onPostRender` and `OnRenderImage`, under the camera's matrices, GL.modelview, the pixel matrix,
LoadOrtho and loaded projections. It runs through `scripts/e2e.py` with OniMcp's mod folder as
above and a spec `from: main-menu` that waits for `**/DrawMeshNowProbeDone`; diff the players'
`DMN` lines. The file's header lists the cases and what they measured; `DrawMeshTests`
(`DrawMeshNowDrawsWithThePassSetInTheCameraView`) encodes it.

Both players write the same version stamp for the mod (ARCHITECTURE D3), so one `mods.json`
serves both. In U59 that stamp is `692750779`, with `enabledForDlc: ["", "EXPANSION1_ID"]`;
with another stamp the game reinstalls the mod and disables it.

With `PROBE_SCENARIO=textwidth` only the text-width probe runs (`TextWidth.cs`, lines `TW …`,
10 s into the main menu, about two seconds of work): TMP's preferred width and height of short
labels in every NotoSans and GRAYSTROKE font asset at 12, 14 and 16 pt, regular and bold, set up
as `SetTextStyleSetting` does, with each character's origin and advance; in rects at three
pivots, under a `HorizontalLayoutGroup` and in a `LayoutElement`. Values are logged in "R"
format with their float bits and E2eDriver's two-decimal dump form, after a check of that
formatting itself. Compare the players with `tools/UnityProbe/tw-check.py diff ORIG OURS`, and
check the rounding rule with `tw-check.py rule LOG`; `docs/cleanroom/textmeshpro.md`
("Preferred sizes to the hundredth") records what it measured.

With `PROBE_SCENARIO=suptext` only the script probe runs (`SupText.cs`, lines `SU …`, 10 s into
the main menu): NotoSans 14 pt texts with `<sup>` and `<sub>`, wrapped in a 338.22-wide rect and
unwrapped: the preferred height, each line's ascender, descender, baseline and height, and each
character's, then a `SupTextProbeDone` canvas. `TmpScriptOffsetTests` encodes it.

With `PROBE_SCENARIO=autosizepref` only the auto-size probe runs (`AutoSizePreferred.cs`, lines
`AP …`, 10 s into the main menu): wrapped, auto-sized NotoSans texts (8 to 14 pt) 72 wide in rects
0 to 100 high, then in a HorizontalLayoutGroup row that controls their height: the size chosen,
the preferred width and height and GetPreferredValues, then an `AutoSizePreferredProbeDone`
canvas. `TmpEmptyTextTests` (`AWrappedAutoSizedTextsPreferredHeightIgnoresTheRectsHeight`)
encodes it.

With `PROBE_SCENARIO=textheight` only the text-height probe runs (`TextHeight.cs`, lines `TH …`,
10 s into the main menu): TMP's preferred height and width of 21 labels in the scripts the
Workshop's translations use (Japanese, Chinese, Korean, Thai, Cyrillic, Vietnamese, Latin) in
NotoSansCJKjp, NotoSansCJKsc, NotoSansCJKkr, NotoSansThai, RobotoCondensed and NotoSans, at 12
to 28 pt, set up as `SetTextStyleSetting` does, top left in a 400 × 60 rect, unwrapped and
wrapped, with the rect's top at 30, 0 and 22.2. Each logs the first line's ascender, descender
and baseline and each character's ascender, descender, scale and font asset (fallbacks show
there), then a `TextHeightProbeDone` canvas appears. Compare the players with
`tools/UnityProbe/th-check.py diff ORIG OURS`, and check the rule with `th-check.py rule LOG`;
`docs/cleanroom/textmeshpro.md` ("Heights in the translations' fonts") records what it
measured. It runs through `scripts/e2e.py` with a spec `from: main-menu` that waits for that
canvas, as the canvas order probe does.

With `PROBE_SCENARIO=textoverflow` only the overflow probe runs (`TextOverflow.cs`, lines `OV …`,
10 s into the main menu): `isTextOverflowing`, `firstOverflowCharacterIndex`, `isTextTruncated`,
the `textInfo` counts and each character's code, visibility and line, for texts of white space
alone and with white space around a visible character, in NotoSans-Regular 14 pt and GRAYSTROKE
REGULAR SDF 12 pt. The rects run from 0 × 0 through the text's preferred size (a hundredth and a
thousandth under and over it) and one line of the font to 400 × 100, with Overflow, Truncate,
Ellipsis and Masking, wrapping on and off. Each text's layout in the 400 × 100 rect is logged
too (`metrics`: origins, advances, line ascenders and descenders). Compare the players with
`tools/UnityProbe/ov-check.py diff ORIG OURS`, and check the rule against a log with
`ov-check.py rule LOG`; `textmeshpro.md` ("Overflow at white space") records what it measured.
Before them it logs `CH` lines: further white space and control characters (CR, vertical tab,
form feed, U+0003, U+2000–U+200B, U+2028, U+2029, U+3000, U+FEFF, U+180E) alone and around
"A" in low rects. It also logs `TAB` lines: the fonts' tab settings and tabs after other
characters at two sizes, in rects 50, 400 and 1000 wide. `PROBE_OV_PART=chars` runs only
these.

With `PROBE_SCENARIO=inputlayout` only the input-field layout probe runs (`InputLayout.cs`, lines
`IL …`, 10 s into the main menu). It logs `TMP_InputField`'s layout element values (min,
preferred, flexible, priority) beside its text component's and placeholder's preferred sizes,
and `LayoutUtility`'s values and the rects. The fields are built in code as Klei's prefabs are,
and the cases vary the field's rect, the text rect, the viewport's anchors, offsets and mask
padding, the text's margins and text, the placeholder, the line type and the character limit.
Then Geyser Control's slider row and other layouts (a `HorizontalLayoutGroup` with height
control on and off, `ContentSizeFitter`s) are logged through four forced layout passes and three
frames. Check the rule with `tools/UnityProbe/il-check.py rule LOG`, and compare the players
with `il-check.py diff ORIG OURS`. `textmeshpro.md` ("The input field's layout values")
records what it measured.

With `PROBE_SCENARIO=inputtext` only the input-text probe runs (`InputText.cs`, lines `IT …`, 10 s
into the main menu, about a minute). It covers `KInputTextField`s built as the slider row's
`NumberInputField`, with text set by `text =`, `SetTextWithoutNotify` or the text component's
`text`, on inactive, active and focused fields, across `characterLimit`, `lineType` and
`contentType`, with one of them set after the text in some cases. Each field is followed
through a frame, enabling, activation, an edit (End, a typed "9", Backspace, through
`ProcessEvent`) and deselection. Each step logs the text, the label, the caret and the
`onValueChanged` / `onEndEdit` / `onSubmit` reports. Check the rule with
`tools/UnityProbe/it-check.py rule LOG`, and compare the players with `it-check.py diff ORIG
OURS`. `textmeshpro.md` ("Text set from code") records what it measured.

With `PROBE_SCENARIO=inputcaret` only the input-caret probe runs (`InputCaret.cs`, lines `IC …`,
10 s into the main menu, about a minute). `IC settings` lines list the loaded game fields'
`onFocusSelectAll`, `resetOnDeActivation` and line type. `IC end` lines follow
`KInputTextField`s through an edit and its end. The text is typed, typed after Ctrl+A and
Backspace, typed and then selected with Shift+Home, or set from code. The end is Return
through `ProcessEvent`, `OnSubmit`, `DeactivateInputField()` or `(true)`, deselection or
disabling, with `resetOnDeActivation` and `onFocusSelectAll` on and off, and with listeners that
do what Klei's `KInputField` does on `onEndEdit`. They log the caret, the selection and every
event with the caret it was sent with. `IC end x` lines do the same with real key presses, sent
to the player's window through XTest (libX11 and libXtst, as `scripts/e2e.py` types), ended by
Return, Escape or deselection. `IC cull` lines log the caret graphic's
`CanvasRenderer.cull` beside an `Image` with its rect. The caret is at its defaults
(`AssignPositioningIfNeeded` skipped by a Harmony prefix) or placed, under the field's own
`RectMask2D`, inside an outer one or outside it, never focused or focused. `PROBE_IC_PART=end`,
`x` or `cull` runs one part. Check the rules with `tools/UnityProbe/ic-check.py rule LOG`, and
compare the players with `ic-check.py diff ORIG OURS [end|x|cull]`. `textmeshpro.md` ("The
caret after an edit ends", "The caret graphic and RectMask2D") records what it measured.

With `PROBE_SCENARIO=tmpgaps` only the TMP gaps probe runs (`TmpGaps.cs`, lines `TG …`, 10 s
into the main menu, about half a minute): package members that Workshop mods reach and this
implementation lacked. `TG meta` lines give their metadata by reflection. `TG assign` lines log
each call of `TMP_InputField.AssignPositioningIfNeeded` (a Harmony prefix and postfix, with the
calling methods' names) and whether the caret's rect matches the text's, through a field's life.
`TG mesh` lines follow the protected `m_Mesh`, `TG dropdown` lines the private
`TMP_Dropdown.m_Dropdown` through Show and Hide. `TG chars` covers `TMP_FontAsset.HasCharacters(
string, out uint[], bool, bool)`, `TG hash` `TMP_TextUtilities.GetHashCode`, `TG renderer`
`TextMeshProUGUI.canvasRenderer`, and `TG sprite` and `TG font` the `MaterialReferenceManager`
registrations and `TMP_Text.OnSpriteAssetRequest`. `TG res` lines follow the game's own sprite
and font tags, which name assets under Resources paths. `PROBE_TG_PART=meta`, `assign`, `mesh`,
`dropdown`, `chars`, `hash`, `renderer`, `sprite`, `font` or `resources` runs one part. It ends
by creating
`TmpGapsProbeDone`, for a spec's `ui_wait`. `textmeshpro.md` ("Package members mods reach")
records what it measured.

With `PROBE_SCENARIO=fontasset` only the font-asset probe runs (`FontAsset.cs`, lines `FA …`, 10 s
into the main menu, a few seconds). It calls `TMP_FontAsset.CreateFontAsset` by family and style
name and by file path, with installed and missing families, other cases, empty and null names,
point sizes of 0 and below, and files that are not fonts. It logs what each call returns or
throws, and what it logs. For each asset it logs the face, atlas, material, creation settings
and tables. Then it uses an asset in a `TextMeshProUGUI` (a character to add, one the face
lacks) and logs the asset's character table. It also calls the engine's internal
`FontEngine.TryGetSystemFontReference` by reflection. `textmeshpro.md` ("Font assets from OS
fonts") records what it measured.

With `PROBE_SCENARIO=pointermove` only the pointer-move probe runs (`PointerMove.cs`, lines `PM
…`, 10 s into the main menu). It sets a `BaseInput` of its own as the current input module's
`inputOverride`, which moves the pointer and presses the left button frame by frame over a small
hierarchy of handlers. It logs every `OnPointerMove`, `OnPointerEnter` and `OnPointerExit` with
its event data. `ugui.md` ("Pointer move events") records what it measured.

With `PROBE_SCENARIO=scrollbar` only the scrollbar probe runs (`ScrollbarPress.cs`, lines `SB
…`, 10 s into the main menu, a few seconds). It builds a fresh `Scrollbar` per case (each
direction, several sizes, values and step counts) and presses its track or handle through the
current module's `inputOverride`, held for 1 to 60 frames, some moving while held. Each frame
logs the value and the handle's anchors, and each `onValueChanged` its argument. Then a
`Scrollbar` subclass runs both protected `ClickRepeat` overloads by hand and as coroutines,
idle and with a press held, and overrides `Update`; `PROBE_SB_PART=press` runs only the press
cases, `clickrepeat` only the subclass cases, and `clickrepeat2` only the held-press cases (a
value set while held, the event's position fields, a drag, a released press). `ugui.md`
("Scrollbar presses on the track") records what it measured.

With `PROBE_SCENARIO=scrollvis` only the scrollbar visibility probe runs (`ScrollVis.cs`, lines
`SVIS …`, 2 s into the main menu, under a second). It builds scroll rects with each scrollbar
visibility, content that fits or overflows, the axis on or off, and the scrollbar a child of the
scroll rect, beside it or under its viewport, then deactivates the scrollbars, moves, resizes
and rebuilds from an Update, logging each scrollbar's activity at every frame's end. It ends
with a `ScrollVisProbeDone` canvas. `ugui.md` ("ScrollRect: scrollbar visibility, measured")
records what it measured.

With `PROBE_SCENARIO=layoutcull` only the layout-and-cull probe runs (`LayoutCull.cs`, lines `LC
…`, 10 s into the main menu, about a minute). `LC lay` lines follow a `VerticalLayoutGroup`
(a subclass counting its layout calls) with three children through one change at a time:
- a child's position, local position, scale, size, pivot or anchors, the group's position or
  size, or a drag that sets a child's position every frame;
- then a re-layout from elsewhere;
- across fitters, child size control and `ignoreLayout`.

Then come other ways to re-lay out, nested groups, and when `OnRectTransformDimensionsChange`
arrives for rects changed in `Canvas.willRenderCanvases`. `LC cull` lines give the offsets
across a `RectMask2D`'s edge at which `Image`s and `TextMeshProUGUI` texts of several kinds are
culled. These are at canvas scale 1 and 1.032, with padding and softness, each with its rect and
mesh bounds. Then come when the cull is re-evaluated, and a copy of Modify Difficulty Settings'
toggle row. Compare the players with `tools/UnityProbe/lc-check.py diff ORIG OURS
[--positions]`. `ugui.md` ("Moved children of layout groups", "RectMask2D culling") records what
it measured.

With `PROBE_SCENARIO=deactorder` only the deactivation-order probe runs (`DeactivateOrder.cs`,
lines `DA …`, at mod load): a small hierarchy deactivated, activated and destroyed, with each
`OnEnable` and `OnDisable` logging every object's `activeSelf`, `activeInHierarchy` and
`isActiveAndEnabled`, and cases where an `OnDisable` deactivates a sibling or disables a
component. Diff the players' `DA` lines; `LifecycleTests` (`DeactivationTurnsEachObjectInactive…`)
encodes it.

With `PROBE_SCENARIO=execorder` only the execution order probe runs (`ExecOrder.cs`, lines `EO
…`, 2 s into the main menu): the probe's own types marked `[DefaultExecutionOrder(-1000)]` and
`(1000)`, a subclass of the first and an unmarked type, as separate objects made in one frame
and as components of one object, with each Awake, OnEnable, Start, FixedUpdate, Update and
LateUpdate in the order it ran. It ends with an `ExecOrderProbeDone` canvas. Measured in U60 and
U59, the same: the player orders a runtime-loaded assembly's types by the attribute, inherited, in
the update passes and in an object's activation, but not the Starts of separate objects.
`DefaultExecutionOrderTests` encodes it.

With `PROBE_SCENARIO=frametiming` only the frame timing probe runs (`FrameTimings.cs`, lines `FT
…`, 5 s into the main menu): `FrameTimingManager`'s values and the latest timings over five
frames, the Profiler's memory counters (their sign) and `SceneManager.GetSceneAt` in and out of
range. It ends with a `FrameTimingProbeDone` canvas. In U60's Linux player, `GetLatestTimings`
with more frames than the array holds writes past it, and with a null array it crashes the
player, so the probe asks neither. U59's Linux player, a release build, has the feature off:
`IsFeatureEnabled` is false and `GetLatestTimings` returns 0 and writes nothing, and
`Profiler.supported` is false; the memory counters are positive in both. `FrameTimingTests`
encodes U59's.

Both run through `scripts/e2e.py` with OniMcp's mod folder as above and a spec `from: main-menu`
that waits for the canvas (`ui_wait`, `cond: exists`).

With `PROBE_SCENARIO=focusdelivery` only the focus probe runs (`FocusDelivery.cs`, lines `AF …`,
from mod load): which MonoBehaviours `OnApplicationFocus` reaches. Move the X input focus away
from the player's window and back (XSetInputFocus on the lane's display) while the main menu is
up. Measured in U59: both players call it on enabled and disabled behaviours of active objects,
and on an object with a timed Destroy pending, never on inactive objects.

To run one of them in a player without the e2e runner's lanes, give it a scratch profile
(`XDG_CONFIG_HOME`) whose `mods/Local/SemanticsProbe/` holds the DLL and the two yaml files, with
the `mods.json` entry described above. Run the original player through `scripts/e2e.py`'s
`original_player` copy (Unity's player locks its folder), under `xvfb-run -a`, with
`PROBE_SCENARIO` set.
With `PROBE_SCENARIO=submesh` only the sub-mesh probe runs (`SubMesh.cs`, lines `SM …`, 10 s
into the main menu, about a minute). It covers the `TMP_SubMeshUI` children that texts in GRAYSTROKE
REGULAR SDF 12 pt and NotoSans-Regular 14 pt make for CJK characters and "▼" taken from
fallback fonts. Each step logs every sub-mesh's `raycastTarget`, `maskable`, activity, components, rect, mesh and depth.
It also logs whether `EventSystem.RaycastAll` reaches it. The steps cover the parent's
`raycastTarget` set before and after the sub-mesh exists, the sub-mesh's own `raycastTarget`
set, texts without fallback glyphs, enable and activation cycles of either object, relayouts,
pivot, anchors, `maskable` and a clone. `reg` lines follow one step each on a fresh sub-mesh,
`img` lines run the same steps on an `Image` as a control, and `hand` lines cover a
`TMP_SubMeshUI` added by hand. Then every loaded
`TMP_SubMeshUI` (the prefabs' saved ones) is listed, and copies of three prefab labels and of
the whole `LargeImpactorSequenceReticle` are checked. `textmeshpro.md` ("Sub-meshes and
raycasts") records what it measured.

With `PROBE_SCENARIO=wordcount` only the word probe runs (`WordCount.cs`, lines `WC …`, 10 s
into the main menu, about a minute). It logs `textInfo.wordCount`, each word's first and last
character index and count, each line's word count and the characters, in NotoSans-Regular 14 pt
and GRAYSTROKE REGULAR SDF 12 pt. The texts are hyphens, dashes, non-breaking spaces,
apostrophes, digits, CJK, tags, texts without words and carriage returns (with `spaceCount`),
then a sweep of single characters in "abXcd", "Xab", "abX", "12X34" and alone. Wrapped texts follow, each distinct break from 1 wide to
past the preferred width. Check the rule with `tools/UnityProbe/wc-check.py rule LOG`, and
compare the players with `wc-check.py diff ORIG OURS`. `textmeshpro.md` ("Words and their
separators") records what it measured.

With `PROBE_SCENARIO=richtext` only the rich-text probe runs (`RichText.cs`, lines `RT …`, 10 s
into the main menu, a few frames): `<b>` in NotoSans-Regular (14 and 18 pt) and GRAYSTROKE, and
`<indent>` opening part-way along a line (bullets with hanging indents, nested, wrapped; an indent
left of the pen; `</indent>` mid-line), each case at a fixed place on a black panel, its
characters logged and the screen captured to `PROBE_CAPTURE_DIR/richtext.png` (the directory must
exist). Run it with `tests/e2e/probe-richtext.yaml` as the canvas probes run (OniMcp's folder
with `SemanticsProbe.dll`). Measured 2026-11-02: bold draws with the same ink in both players
(within 0.4% per word); an opening `<indent>` moves the pen to the indent, back as well as
forward, and the line's preferred width is as far as it reached; `TmpRichTextTests`
(`AnIndentPartWayAlongALineMovesThePen`) encodes it.

With `PROBE_SCENARIO=tallline` only the tall-line probe runs (`TallLine.cs`, lines `TL …`, 10 s
into the main menu, about ten seconds). It lays out texts whose line grows taller part-way: the
geyser names' U+2011 from a fallback font, `<size>`, `<voffset>` and spaces in `<size>`, on the
first line and on later ones, in GRAYSTROKE REGULAR SDF 14 and 12 pt and NotoSans-Regular
14 pt, with Overflow, Truncate and Ellipsis, wrapping on and off. It sweeps the rect's height
from 0 to 70 and logs each distinct result (`isTextOverflowing`, `firstOverflowCharacterIndex`,
`isTextTruncated`, the counts, each record and each line's metrics), each text's layout in a
tall rect (`metrics`), and the details screen's title in its own rect. Check the rule with
`tools/UnityProbe/tl-check.py rule LOG`, and compare the players with `tl-check.py diff ORIG
OURS`. `textmeshpro.md` ("Lines that grow part-way") records what it measured.

With `PROBE_SCENARIO=fontfallback` only the font fallback probe runs (`FontFallback.cs`, lines
`FF …`, 10 s into the main menu, a few seconds): `TMP_Settings`' global fallbacks and default
font asset, each loaded font's fallback list, `HasCharacter` with and without `searchFallbacks`
(and `tryAddCharacter`) for each font and a set of characters, the chain the search walks
(empty assets with the global list or the default emptied, nested fallbacks, null entries,
dynamic assets that add), and which font asset each character of five strings (no-break spaces,
CJK, Cyrillic, symbols, other scripts) is drawn from in every loaded font, with the menu's own
texts. `tests/e2e/probe-font-fallback.yaml` (and `-zh`, `-ru`) run it; diff the players' `FF`
lines. `textmeshpro.md` (`TMP_FontAsset.HasCharacter`) records what it measured.

With `PROBE_SCENARIO=emptytext` only the empty-text probe runs (`EmptyText.cs`, lines `ET …`, 10 s
into the main menu, about a minute). It lays out texts with nothing visible: "", a space, texts
of rich-text tags alone ("<b></b>", "<b> </b>", "<br>") and U+200B, in NotoSans-Regular 16 pt
and GRAYSTROKE REGULAR SDF 12 pt, in rects 95.7 wide and 0 to 100 high (one line of the font
and a hundredth under and over it among them), in every overflow mode but Linked, wrapping on
and off, auto-size off and on (`fresh` lines, a new text per case). Then "kg" and "AB\nCD" are
laid out in a rect they overflow or fit, the text set to "", a space, "<b></b>" or null and laid
out by `ForceMeshUpdate` or by the frame's rebuild, and the rect resized (`after` lines); and
auto-size with `fontSizeMax` above and below `fontSize` (`automax` lines). Each logs
`isTextOverflowing`, `firstOverflowCharacterIndex`, `isTextTruncated`, the `textInfo` counts,
the font size and the preferred size. Check the rule with `tools/UnityProbe/et-check.py rule
LOG`, and compare the players with `et-check.py diff ORIG OURS`. `textmeshpro.md` ("Texts with
nothing to lay out") records what it measured.

With `PROBE_SCENARIO=indent` only the indent probe runs (`IndentWidth.cs`, lines `IN …`, 10 s into
the main menu, a few seconds). It lays out 28 texts whose `<indent>` changes part-way along a
line, as the details screen's bulleted descriptors do (`<indent=1em>•<indent=1.75em>…`): nested
indents in em, px and %, an inner indent behind the pen, `</indent>` part-way, `<link>`,
`<pos>` and `<size>` beside them, several lines and wrapping, in NotoSans-Regular and GRAYSTROKE
REGULAR SDF at 12, 14, 16 and 20 pt. Each logs the preferred sizes, each character's origin,
advance and vertices, each line's metrics and the links, then an `IndentProbeDone` canvas
appears, for a spec's `ui_wait` as the text-height probe has.
`textmeshpro.md` ("Indents opened part-way along a line") records what it measured, and
`TmpIndentTests` encodes it.

With `PROBE_SCENARIO=childactive` only the child-activation probe runs (`ChildActivation.cs`, lines
`CA …`, 10 s into the main menu, a few seconds). A `GridLayoutGroup` (a subclass counting its
layout calls and messages) holds nine children; some start inactive and have never been laid
out. The children are bare rects with an `Image` child, bare rects alone, `Image`s or
`LayoutElement`s, and the grid is either a root group or nested in two `VerticalLayoutGroup`s
with a `ContentSizeFitter`, as the build menu nests it. One step at a time: a child appears,
one disappears, all swap after a reorder, a child's own child is toggled, the grid component is
disabled. Each logs the messages the grid and the children receive, every
`RectTransform.reapplyDrivenProperties`, the layout rebuild queue (read from the registry by
reflection) and the children's positions, through two frames. Measured 2026-10-23: `SetActive`
raises `reapplyDrivenProperties` for each RectTransform whose `activeInHierarchy` changed, the
child before its parent, before `OnEnable`/`OnDisable`, with no
`OnRectTransformDimensionsChange`; the group is queued at once and re-lays out in that frame.
`ActivatedLayoutChildTests` encodes it.

With `PROBE_SCENARIO=childdims` only the child-dimensions probe runs (`ChildDimensions.cs`,
lines `CD …`, 10 s into the main menu, about a minute). A rect with a child, a grandchild and a
stretched child, each with a spy, is moved, resized, scaled, rotated or re-pivoted: in a
coroutine, in `Canvas.willRenderCanvases` or during the layout rebuild; with and without
graphics on it; on canvases at scale 1 and 0.86, pixel perfect or not; then as a child of a
`HorizontalLayoutGroup`, and a copy of the main menu's news row. Each message is logged with the
phase it came after. `PROBE_CD_PART=extra` runs the pixel-perfect details instead: which objects
are told (a `CanvasRenderer` alone, disabled, transparent or culled graphics, a layout element),
several moves in a frame, a drag, values set to themselves, new, cloned, activated and
reparented objects, a move at the end of the frame, and nested canvases that override
`pixelPerfect`. `PROBE_CD_PART=born` runs objects made and moved in the same frame (moved
directly, through a parent, by a layout group, cloned, moved while inactive, moved before their
`Image` exists). `ugui.md` ("Moves on pixel-perfect canvases") records what it measured, and
`PixelPerfectMoveTests` encodes it.

With `PROBE_SCENARIO=floatprec` only the float-precision probe runs (`FloatPrecision.cs`, lines
`VAL fp …`, at mod load; the full run includes it in its values): float expressions whose
float32 and double results differ, each line saying which one the player gave. Its header
records what it measured: the original rounds float arithmetic to float32 only on stores.

With `PROBE_SCENARIO=convert` only the conversion probe runs (`Conversions.cs`, lines `CV …`, at
mod load, a second): NaN, the infinities and values at and beyond the edges of every integer
type, as float32 and float64, through each unchecked IL conversion (conv.i1 to conv.u8, conv.i,
conv.u), each checked one (conv.ovf.*), Convert.ToXxx, Math.Round and Math.Floor then (int), and
Unity's Mathf.RoundToInt/FloorToInt/CeilToInt, Vector2Int/Vector3Int's and Color32 from Color.
Check the rule with `tools/UnityProbe/cv-check.py rule LOG`, and compare the players with
`cv-check.py diff ORIG OURS`. ARCHITECTURE D29 records what it measured.

With `PROBE_SCENARIO=jpg` only the image-encoder probe runs (`JpgEncode.cs`, lines `JPG …`, at
mod load): `EncodeToJPG`, `EncodeToTGA` and `EncodeToPNG` of gradient textures in each
uncompressed, HDR and block-compressed format, an unreadable one and JPEG qualities from -5 to
1000, with each JPEG's frame header (components, sampling, first quantisation value). The files
go to `jpg-mono` or `jpg-coreclr` in `PROBE_OUT`; `tools/UnityProbe/jpg-check.py ORIG OURS`
decodes both players' files and prints each JPEG's error against the original's PNG and whether
the TGAs and PNGs are equal. `ImageEncodeTests` encodes what it measured.

With `PROBE_SCENARIO=dirorder` only the directory-listing probe runs (`DirectoryOrder.cs`, lines
`DL …`, at mod load): a tree of files and folders whose names tell UTF-8 byte order, UTF-16
code unit order and case-insensitive orders apart, made in another order, listed through every
`Directory` and `DirectoryInfo` `Get*`/`Enumerate*` overload (patterns, a pattern with a folder
part, `AllDirectories`, `EnumerationOptions`), and an enumeration with files added before and
after its first step. The tree stays in the temporary folder (its path is the first line), so
`ls -U` shows the folder's own order. Compare the players' lines directly (`diff`).
ARCHITECTURE D30 records what it measured, and `MonoDirectoryOrderTests` encodes it.

With `PROBE_SCENARIO=memberorder` only the member-order probe runs (`MemberOrder.cs`, lines `MO …`,
at mod load): the order of `GetFields`, `GetMethods`, `GetProperties`, `GetEvents`,
`GetNestedTypes`, `GetConstructors`, `GetInterfaces`, `GetMembers` and `GetCustomAttributes` for
the probe's types (fields of every kind interleaved, a generic base class, overrides, hidden
properties) and for `CritterCondoInteractMontior`, and the parameters `CreateStates` numbers. At
the main menu it writes the same lists for every type of Assembly-CSharp and firstpass, with the
fields `MyAttributeManager` injects, to `memberorder-mono.tsv` or `memberorder-coreclr.tsv` in
`PROBE_OUT` (its path is logged), then a `MemberOrderProbeDone` canvas appears; compare two files
with `tools/UnityProbe/mo-check.py ORIG OURS [CALL [N]]`. With `PROBE_SCENARIO=smorder` it logs,
in a colony, each state machine the game makes (`MO SM …`: its parameters as numbered and its
parameter fields as listed then). Both run through `scripts/e2e.py` with OniMcp's mod folder
holding `SemanticsProbe.dll` (as the canvas order probe does). ARCHITECTURE D39 records what
they measured, and `MonoMemberOrderTests` encodes it.

With `PROBE_SCENARIO=findorder` only the find-order probe runs (`FindOrder.cs`, lines `FO …`, 10 s
into the main menu): the order of `Resources.FindObjectsOfTypeAll`. It logs every `LocText` the
call returns (instance id, scene, activity, pivot, path) and whether the ids ascend, then
components and ScriptableObjects made in a known order, some destroyed and others made after.
The original returns ascending instance ids: the newest run-time object first, the objects
loaded from files last. `FindObjectsOrderTests` encodes the result.

With `PROBE_SCENARIO=newobject` only the `new` probe runs (`NewObject.cs`, lines `NEW …`, at the
main menu): what `new` gives for a ScriptableObject subclass, a bare ScriptableObject and a
MonoBehaviour (whether it is alive, its instance id, name, hide flags and `ToString`, which of
Awake and OnEnable run and when, what reaches the log), then Instantiate, DestroyImmediate and
Destroy on it. `ScriptableObjectNewTests` encodes the results.

With `PROBE_SCENARIO=emptycomposite` only the composite-field probe runs (`EmptyComposite.cs`,
lines `EC …`, at the main menu; an `EmptyCompositeProbeDone` canvas appears at the end): what
Instantiate copies of a mod MonoBehaviour's fields of the mod's own `[Serializable]` classes (with
and without serialized fields or a parameterless constructor, null and not), struct and list of
them, beside an int, a string, a `List<int>` and the game's `AttackEffect`. It runs through
`scripts/e2e.py` with OniMcp's mod folder as above and a spec `from: main-menu` that waits for
`**/EmptyCompositeProbeDone`. `InstantiateFieldTests` encodes the results (ARCHITECTURE D52).

With `PROBE_SCENARIO=missingtypes` only the missing-types probe runs (`MissingTypes.cs`, lines
`MT …`, at the main menu; a `MissingTypesProbeDone` canvas appears at the end). It is built
against `Gone/v1`'s ProbeGone; put `Gone/v2`'s build (`dotnet build Gone/v2 -c Release`) beside
SemanticsProbe.dll instead, which lacks the type Gone and the method Keeper.Removed. Each case is
a method naming Gone one way (a call by its return or parameter type, a field of it, isinst,
castclass, newobj, newarr, ldtoken, a generic argument, a local, a catch clause, a call to the
probe's own method with Gone in its signature, two of them in either order), called twice: the
exception's type, message and first frame, and whether any of the method ran. Then components
and a ScriptableObject without a parameterless constructor: what AddComponent, CreateInstance and
Instantiate make, which messages run and what is logged. ARCHITECTURE D40 and §3.2 record what
it measured; `NoParameterlessConstructorTests` and `--check-types` encode it.

With `PROBE_SCENARIO=missingmembers` only the missing-members probe runs (`MissingMembers.cs`,
lines `MM …`, at the main menu; a `MissingMembersProbeDone` canvas appears at the end), with
`Gone/v2`'s build in the mod folder as for missingtypes: v2 lacks or retypes the members v1's
Keeper, Box<T>, Pair and GlobalKeeper (no namespace) have. Each case is a method naming one of
them (static, instance and virtual methods, a constructor, a property, a changed parameter type,
generic methods and types, nested types, by-ref and array parameters, fields, and orders against
a missing type), called twice: the exception's type, message, first frame and `ToString()`, and
whether any of the method ran. Then how such an exception reaches the log through the game's
`KMonoBehaviour.Spawn` pattern (`DebugUtil.LogExceptionCallstack`), `Debug.LogException` and
`Debug.LogError(e.ToString())`. Run it through `scripts/e2e.py` as the canvas probes are, with a
spec that loads any colony. ARCHITECTURE D42 records what it measured; `--check-types` encodes it.

With `PROBE_SCENARIO=kindchange` only the kind-change probe runs (`KindChange.cs`, lines `KC …`,
at the main menu; a `KindChangeProbeDone` canvas appears at the end). Its cases are
`Kind/Cases`' ProbeKindCases, built without unsafe code (an unsafe build is SkipVerification,
which turns off the checks measured), against `Kind/v1`'s ProbeKind, where Flip is a struct and
Flop a class; put `Kind/v2`'s build (`dotnet build Kind/v2 -c Release`) and ProbeKindCases.dll
beside SemanticsProbe.dll, where Flip is a class and Flop a struct, as a mod built before U57
meets LogicValueChanged. Each case is a method using Flip one way (unbox.any into a local, a
call's result, a static field and an array element stored or reached through their address,
`new`, a default local, a box, `List<Flip>` as a field, a new object, a parameter and with Add,
the mod's own field and method, `out`, `ref`, a handler unboxing its event, Flop returned and
passed), called twice: what it returned, or the exception's type, message and first frame.
`PROBE_KC_CRASH=1` adds, last, the two cases that crash the player. ARCHITECTURE D56 records what
it measured; `--check-types` encodes it.

With `PROBE_SCENARIO=atcall` only the at-call probe runs (`AtCall.cs`, lines `AC …`, at the main
menu; an `AtCallProbeDone` canvas appears at the end). Its cases are `AtCall/Cases`'
ProbeAtCallCases, built against the original's own mscorlib and System.Drawing (`Managed/`),
against `AtCall/Absent`'s ProbeAbsent and against `Gone/v1`; put ProbeAtCallCases.dll and
`Gone/v2`'s build beside SemanticsProbe.dll, and not ProbeAbsent.dll. Each case is a method
naming the absent assembly one way (a call, newobj, a static field, a local, a struct local,
isinst, behind a branch, in a method its caller catches around), a member Gone/v2 lacks, or
System.Drawing's Bitmap and Rectangle, mostly inside a try of its own, called twice: what it
returned (its catch's exception included) or the exception's type, message, inner exceptions
and first frame, and whether any of it ran. Then JobsUtility's static properties, fields and
methods, what setting `JobWorkerCount` does, and `System.Number`'s Format methods, with
whether a Harmony postfix on `FormatSingle(float, string, NumberFormatInfo)` runs for
`float.ToString` (ZEROs patches it). ARCHITECTURE D57 records what it measured;
`JobsUtilityTests` and `--check-types` encode it.

With `PROBE_SCENARIO=quitevents` only the quit-events probe runs (`QuitEvents.cs`, lines `QE …`,
at the main menu): Application.wantsToQuit, quitting and unloading handlers and OnApplicationQuit,
around three Application.Quit() calls three frames apart (a handler refuses the first and throws
at the second). The player quits at the second. `QuitEventsTests` encodes the results.

With `PROBE_SCENARIO=particleemit` only the particle probe runs (`ParticleEmit.cs`, lines `PE …`,
10 s into the main menu, about a second): particle systems built by script as ManualMiner
(3638972299) builds its laser's sparks. It logs the modules' defaults on a new component and what
setters keep, the particles `Emit(EmitParams, count)` makes with each parameter set or left out,
Emit on a playing, a stopped and a never-played system, a velocity limit's speeds over steps of
`Simulate`, and rotation and size over lifetime. It runs through `scripts/e2e.py` with OniMcp's
mod folder as above and a spec `from: main-menu` that waits for `**/ParticleEmitProbeDone`.
`ParticleTests` (`EmitParamsOverrideTheModules`, `LimitVelocityDampensTheExcessSpeed` and
others) encodes the results.

With `PROBE_SCENARIO=destroyflags` only the destroy-flags probe runs (`DestroyFlags.cs`, lines
`DF …`, at the main menu): the active flags each component sees in its OnDisable and OnDestroy
when a GameObject is destroyed (with Destroy and DestroyImmediate; active, inactive, under an
inactive parent; its child and grandchild) and when a component is destroyed alone, then
whether `KObjectManager` released the event system of a destroyed `KMonoBehaviour`. A
`DestroyFlagsProbeDone` canvas appears at the end. `LifecycleTests`
(`DestroyedObjectIsInactiveInItsOwnOnDestroy` and the three after it) encodes the results.

With `PROBE_SCENARIO=nativerange` only the NativeArray range probe runs (`NativeRanges.cs`, lines
`NR …`, at mod load, in the original player only): the indexer, `Copy`, `CopyFrom`, `CopyTo`
and `GetSubArray` with indices and ranges before or past an array, through views of a larger
backing array so that what an unchecked access touches is logged. It runs through
`scripts/e2e.py` with OniMcp's mod folder as above and a spec `from: main-menu`. Measured
2026-10-27: none of them throws (the file's header has the details). ARCHITECTURE D46 records
it; `NativeArrayRangeTests` encodes it.

With `PROBE_SCENARIO=statics` only the statics probe runs (`StaticValues.cs`, lines `SV …`, at mod
load): every public static property and field of `Color` and `Color32`, found by reflection (so
each player lists what it has), with the components' float bits; `GarbageCollector.isIncremental`;
and a new `TextMesh`'s `alignment` and what reads back after each value is set. Compare the
players with `tools/UnityProbe/sv-check.py diff ORIG OURS` (bits, not the "R" text), and print
the engine's colour table from the original's log with `sv-check.py cs LOG`. Measured
2026-10-23: 157 named colours, none on `Color32`; `isIncremental` False; alignment Left by
default and any value read back as set. `ModApiTests` encodes them.

With `PROBE_SCENARIO=delegatejson` only the delegate probe runs (`DelegateJson.cs`, lines `DJ …`,
at mod load): objects holding a static, an instance, a multicast, a generic method's, a closure's
and a lambda's delegate through the game's Newtonsoft.Json (default settings, Indented,
ReferenceLoopHandling.Ignore, TypeNameHandling.All), the delegate alone, reading the JSON back,
a BinaryFormatter round trip, the entries of `GetObjectData` (and of each DelegateEntry), and
MethodInfos' own `GetObjectData` and JSON. It needs the game's `Newtonsoft.Json.dll` (referenced
from `Managed/`). It runs through `scripts/e2e.py` with OniMcp's mod folder as above and a spec
`from: main-menu`. ARCHITECTURE D48 records what it measured; `MonoDelegateSerializationTests`
encodes it.

With `PROBE_SCENARIO=callingasm` only the calling-assembly probe runs (`CallingAssembly.cs`,
lines `CA …`, at mod load): what `Assembly.GetCallingAssembly()`, `GetExecutingAssembly()` and
`new StackTrace()` report in Harmony prefixes and postfixes of the probe's constructors and
methods called from an emitted assembly, `CallerAsm` (directly, from another patched
constructor, through `MethodInfo.Invoke` and `Activator`), in a patched method's own body, and
in small and non-inlined methods nothing patches. It runs through `scripts/e2e.py` with a spec
`from: main-menu`, as the canvas order probe does. ARCHITECTURE D41 records what it measured;
`--check-types` ("GetCallingAssembly in a mod's patches …") encodes it.

With `PROBE_SCENARIO=stackdepth` only the stack-depth probe runs (`StackDepth.cs`, lines `SD …`,
at mod load, a summary at the main menu): a postfix of the `ElementConverter.OutputElement`
constructor lists `new StackTrace()`'s frames for the first calls from a building config's
`ConfigureBuildingTemplate`, the frames No Minimum Temperature reads. Run it through
`scripts/e2e.py` with OniMcp's mod folder as above and any spec. ARCHITECTURE D19 records what it
measured; `--check-types` ("Frames a mod's postfix of a struct's constructor sees") encodes it.

With `PROBE_SCENARIO=asmbind` only the assembly-binding probe runs (`AssemblyBinding.cs`, lines
`AB …`, at mod load). Write its assemblies into the mod folder first: `dotnet run --project
tools/UnityProbe/Binding -- MODFOLDER/binding` (libraries in several versions, with and without
a strong name, each in a folder of its own, and users built against them). It loads each folder
as KMod loads a mod's, or a user alone with `Assembly.LoadFrom`, and calls the users: each
answer names the copy a reference bound to (another version loaded first, several loaded, a
reference in other case, a copy beside the user, another key, none at all). It also logs
`Assembly.LoadFrom`, `Load(byte[])` and `Load(name)` of loaded names, the AssemblyResolve
handlers registered before its own and each event it sees with its frames. It runs through
`scripts/e2e.py` with a spec `from: main-menu`, as the canvas order probe does. ARCHITECTURE D45
records what it measured; `--check-types` ("Strong-named copies, …") encodes it.

With `PROBE_SCENARIO=logthreads` only the log-threads probe runs (`LogThreads.cs`, lines `LT …`,
10 s into the main menu): a managed `Thread`, a thread-pool item, a `Task` and the main thread
log each type through `UnityEngine.Debug`, `Debug.unityLogger` and Klei's `Debug`, and handlers
on `Application.logMessageReceived` and `logMessageReceivedThreaded` record the thread and
frame they run on; then handlers on one event only, the main thread blocked in `Join` while a
thread logs, an exception left uncaught on a thread, throwing handlers, handlers that log, and
whether KCrashReporter opened its dialog. Measured 2026-10-24 (U57): another thread's messages
reach `logMessageReceivedThreaded` alone, on that thread, at once, and never
`logMessageReceived` (nor KCrashReporter's dialog); the main thread's reach `logMessageReceived`
and then the threaded event at once. The callbacks run before the text is written. A handler's
exception is written as an exception's text before the message, reaches no callback and ends
the call; a message logged in a handler is written and reaches none. `LogCallbackTests`
encodes it.

With `PROBE_SCENARIO=logging` only the logging probe runs (`Logging.cs`, lines `LOG …`, 10 s into
the main menu): each log type through `UnityEngine.Debug`, `Debug.unityLogger` and Klei's `Debug`,
between `LOG case NAME begin` and `end` lines so that the text the player writes for it lies in
between, with what `Application.logMessageReceived` got (condition and stack trace); then
`Application.GetStackTraceLogType` per type, the same calls with every type set to None and to
Full, and exceptions left uncaught in an Update and a coroutine. Measured 2026-10-24 (U57): every
type reports ScriptOnly, yet messages (Log, Warning, Error, an Exception-typed message) get no
stack trace, in the log or the callbacks. An exception's text is its innermost "Type: message",
the frames of each exception in Mono's format (`  at A.B () [0x00000] in <mvid>:0 `, then
"Rethrow as Outer: message" and the outer one's), the managed frames of the logging call in
Unity's (`UnityEngine.Debug:LogException(Exception)`, `A:B(String, Action)`, a nested type named
`Namespace.<>c`), and an empty line; the callbacks get the frames as `A.B () (at <mvid>:0)` and the
call from `UnityEngine.Debug`'s frame on. An exception from Update has its own frames alone. None
gives no trace for exceptions either; Full gives native frames. `LogTracesTests` encodes it.

With `PROBE_SCENARIO=alignment` only the alignment probe runs (`Alignment.cs`, lines `AL …`, 10 s
into the main menu): copies of loaded `LocText`s, one per value of their stored
`m_textAlignment`, and new `TextMeshProUGUI`s, given an alignment before and after they first
wake (copied under an inactive parent, as Chain Tool's menu does), each step logging the
alignment, its halves and the stored value. `textmeshpro.md` ("Alignment set before a text
first wakes") records what it measured.

With `PROBE_SCENARIO=tooltip` only the tooltip probe runs (`Tooltip.cs`, lines `TT …`, 10 s
into the main menu, about a minute and a half). One behaviour draws IMGUI controls with and
without `GUIContent` tooltips (GUI and GUILayout controls, a group, an area, scroll views) and
opens three windows (`GUI.Window`, a `GUILayout.Window` laid out as Customize Duplicant traits'
editor, one over another); a second behaviour only reads. The real mouse is moved over each
control through XTest, and every OnGUI call and window function logs `GUI.tooltip` at its
start, after each control and at its end, per event, with `tooltipRect`; Update, LateUpdate
and the end of the frame read it too. Then the same with every call resetting the value first,
values set from code, keyboard focus, and presses held and dragged off. The XTest window must be
the player's (it focuses the largest mapped window). Check the rules with
`tools/UnityProbe/tt-check.py rule LOG`, and compare the players with `tt-check.py diff ORIG
OURS`. `docs/cleanroom/imgui.md` ("GUI.tooltip") records what it measured.

With `PROBE_SCENARIO=researchpan` only the research-pan probe runs (`ResearchPan.cs`, lines `RP
…`, in a colony): Harmony patches on `ResearchScreen.Show` and `Update` log the content's pan,
zoom, rect and world position, the pointer and the frame time for the first 120 Updates after
the screen opens. Run it with `SemanticsProbe.dll` in a copy of OniMcp's mod folder (as the
canvas order probe) and a spec that opens the research screen. `PROBE_RP_SLEEP=MS` makes the
frames around the opening that long, as the original's are in the e2e runner (about 450 ms).
Found with Instant Research Fixed (3034422919), whose screen opened at another pan in ours: the
pan follows the frame times (Klei's Update steps by them), not the engine.

With `PROBE_SCENARIO=cardcollapse` only the card-collapse probe runs (`CardCollapse.cs`, lines
`CC …`, in a colony): each change of `Input.mousePosition`, KScreen pointer enters and exits,
`ProductInfoScreen.ToggleExpandedInfo` and OniMcp's screenshots, by frame. Run it as the
research-pan probe, with a spec that opens a building's card and shoots it. Found with Christmas
Tree (2906165124): both players collapse the card in the frame the pointer leaves the build
menu; whether the e2e runner's shot after that move caught it depended on timing.

With `PROBE_SCENARIO=meshshrink` only the mesh probe runs (`MeshShrink.cs`, lines `MS …`, 10 s
into the main menu, a few frames). A mesh with the triangles of two quads gets fewer vertices
(the setter, every `SetVertices` overload, after `Clear`), channels of the wrong length and
triangles past its vertices; each step logs what the player logged and the mesh after it. Then
TextMeshPro's `TMP_MeshInfo` constructor and `ResizeMeshInfo` on meshes that hold more quads
than the new buffer. Diff the two players' `MS` lines; `textmeshpro.md` ("Mesh buffers made on a
mesh that holds more") records what it measured.

With `PROBE_SCENARIO=guilayout` only the GUILayout probe runs (`GuiLayout.cs`, lines `GL …`, 10 s
into the main menu, about 80 s; `PROBE_GL_PART=size`, `wsize`, `order` or `mm` runs one part).
`size` logs the default skin's styles and the rects GUILayout gives: every control kind with
one to three lines, other font sizes, paddings and margins, sequences of entries, groups and
areas with and without a style, scroll views and window content. `wsize` logs
`GUILayout.Window`'s returned rect against the rect passed in, with options and content or size
changing between frames. `order` logs every OnGUI call and window function per event, for two
behaviours with windows added, dropped, brought to the front or back, focused and clicked
(through XTest, as the tooltip probe; the EventSystem is disabled meanwhile). `mm` changes the
GUILayout calls of one event against the Layout before it (entries added, removed, regrouped;
in OnGUI, scroll views and windows; during Repaint, MouseDown, MouseUp and `Used`, and through a
button's handler as Customize Duplicant traits' SAVE and DEL), and logs each call's rect or
exception and what the player logs. Check the rules with `tools/UnityProbe/gl-check.py rule
LOG`, and compare the players with `gl-check.py diff ORIG OURS`. `docs/cleanroom/imgui.md`
("GUILayout, measured") records what it measured. `PROBE_GL_PART=wrap` runs a fifth part,
which a full run leaves out (`GuiLayoutWrap.cs`, lines `GL wrap …`): word-wrapping labels'
`CalcMinMaxWidth`, heights and line breaks at 15 widths, horizontal groups narrower than their
entries, wrapping labels in scroll views of a fixed width, and ModTools' Runtime Console window
as the mod lays it out; diff the players' lines (part 5 of "GUILayout, measured").

With `PROBE_SCENARIO=legacytext` only the legacy-text probe runs (`LegacyText.cs`, lines `LX …`,
10 s into the main menu, about a minute; `PROBE_LX_PART` takes a comma list of `raster`, `ci`,
`gen`, `anchors`, `align`, `fonts` and `text`). It uses the built-in font
(`GetBuiltinResource<Font>("Arial.ttf")`, as Geyser Swapper's panel does).
- `raster`: which pixel size and style a glyph is rendered at after one generation, at font
  sizes × scale factors around 9 pixels.
- `ci`: `Font.GetCharacterInfo` for printable ASCII at 5 to 26 pixels in every style.
- `gen`: `TextGenerator` at every font size × scale factor from 5 to 25 pixels, for the panel's
  labels and strings with kerning pairs: cursors, widths, quads, lines and preferred sizes.
- `anchors` and `align`: the anchors, `updateBounds` and `generateOutOfBounds`, and extents in
  eighths of a pixel at four pivots.
- `fonts`: `Font.CreateDynamicFontFromOSFont` names and characters the default face lacks.
- `text`: `Text` components on canvases at 0.86, 1, 0.5 and 1.5, pixel perfect or not, with
  the mesh a `BaseMeshEffect` sees.

Check the rules with `tools/UnityProbe/lx-check.py rule LOG`, check the glyph table against a
font file with `lx-check.py face LOG FONT STYLE`, and compare the players with `lx-check.py
diff ORIG OURS` (`diff --no-uv` leaves out the texture coordinates, which depend on where each
player packs a glyph; with it ours matched every line, 2026-10-24). To see which face the original falls back to, hide a font directory from it:
run the player under `unshare --user --map-root-user --mount` after bind-mounting an empty
folder over the directory. `docs/cleanroom/ugui.md` ("Legacy Text glyph placement") records
what it measured.

With `PROBE_SCENARIO=modtext` only the mod-text probe runs (`ModText.cs`, lines `MX …`, 10 s into
the main menu, a few seconds; `PROBE_MX_PART` takes a comma list of `font`, `ellipsis`, `bold`,
`lines` and `pref`). It builds texts as three Workshop mods do. `font`: `font = null` on texts
before and after they wake, and PLib's `LocText` with a `TextStyleSetting` made with `new`
(Smart Coolers). `ellipsis` and `bold`: short Bold and Italic labels in each overflow mode, the
fonts and materials of their characters and ellipsis and their sub-meshes, and the fonts' weight
tables (Rookie Quest Guide). `lines`: Social Life's summary and Rookie Quest Guide's disclaimer,
multi-line texts with glyphs from taller fallback fonts, with their preferred heights and line
metrics. `pref`: two- to four-line texts that tell the line-interval and preferred-height rules
apart (`PROBE_MX_PREF=2` for the set with each character's metrics). A `face` line gives each
font's face values. It runs through `scripts/e2e.py` with a spec `from: main-menu` that waits
(and times out) on `**/MxProbeDone`, as the text-height probe does. Compare the players with
`tools/UnityProbe/mx-check.py diff ORIG OURS [PART]` and check the rules with `mx-check.py rule
LOG`. `textmeshpro.md` ("A font set to null", "The ellipsis glyph in Bold and Italic texts",
"Line gaps and preferred heights with fallback glyphs") records what it measured.

## UI semantics (`UiSemantics.cs`, `UIS` lines)

Ten seconds into the front end, after the front-end dump, it builds small hierarchies on an
overlay canvas of its own (scale 1) and logs frame by frame. Each line is `PROBE f<frame> UIS
<section> <label> <phase> <values>`. A phase is `now` (right after the step), then per frame
`U` (an Update), `co` (a coroutine's `yield return null`), `L` (a LateUpdate), `R`
(`Canvas.willRenderCanvases`, after the canvas rebuild) and `E` (the end of the frame).

- `scroll`: ScrollRect's `normalizedPosition` and its scrollbars' `value`/`size`. Content
  smaller than, equal to and larger than the viewport, by pivot and offset; the movement
  types; one axis only; scaled content; the content resized at run time; hairline offsets
  and random exactly-fitting layouts. `scroll raw` lines give the inputs at full precision
  (the view's rect, the content's world corners and those corners in the view's space).
  `zoom anchoring` reproduces Blueprints Expanded's pointer-anchored preview zoom.
- `input`: when `TMP_InputField` fills its text component and makes and positions its caret.
  Fields are built inactive, active, with the text component assigned later, outside any
  canvas, cloned, and with PLib's replacement of `AssignPositioningIfNeeded` emulated.
  `UiSemantics.Install` hooks that private method with Harmony when it exists (`UIS hooks`
  says whether it does) and logs each call (`APIN`).
- `depth`: `CanvasRenderer.absoluteDepth`/`relativeDepth` and `Graphic.depth` for renderers
  of every kind on one canvas. Then one change at a time, to see which changes renumber it.

Run it in both players with the same DLL. Then compare:

```
tools/UnityProbe/uis-check.py diff ORIG/Player.log OURS/stdout.txt [scroll|input|depth]
tools/UnityProbe/uis-check.py fit LOG          # the ScrollRect rule against the raw lines
tools/UnityProbe/uis-check.py inputs ORIG/Player.log OURS/stdout.txt
```

`docs/cleanroom/ugui.md` (ScrollRect) and `textmeshpro.md` (TMP_InputField) record what it
measured. Elastic lines differ by frame timing, and so do the `time` lines.

Five scenarios found with Resize Dup UI (Workshop 2549295369), whose Vitals scrollbar kept a stale
size in ours (`docs/cleanroom/ugui.md`, "ScrollRect: bounds to the bit"); each file's header says
what it logs:

- `zeroscale` (`ZeroScale.cs`, `ZS`, 10 s into the main menu): a scroll rect's first frames under
  an ancestor at scale 0, as `TableScreen.OnActivate` leaves the screen, with a fast and a 250 ms
  frame before it, activated from a coroutine and from an `Update`.
- `vitalsopen` (`VitalsOpen.cs`, `VO`): in a colony, what runs around a table screen's
  activation, and the inputs of its scroll rect's bounds to the bit (`geo`). It needs a colony
  and a table screen opened: install the probe as a Local mod in an e2e run of the Resize Dup UI
  spec (a scratch `ONI_WS` holding it as an item, the spec's `mods` naming that item, and
  `PROBE_SCENARIO` in the runner's environment), or with `SemanticsProbe.dll` in a copy of
  OniMcp's mod folder. `PROBE_VO_SCREEN=VitalsTableScreen` limits the bit-for-bit lines to that
  screen. Each update also logs the bounds the scroll rect compares with (`prev=`).
- `updorder` (`UpdateOrder.cs`, `UO`): the order Update and LateUpdate reach behaviours of one
  execution order made at different times, KScreenManager and an object made in OnLoad among
  them.
- `matmul` (`MatrixMath.cs`, `MM` and `MMS`, at mod load): Matrix4x4's products and inverse to
  the bit, and the inverse of singular and near-singular matrices;
  `mm-check.py rule LOG` tests candidate formulas, `mm-check.py diff ORIG OURS` compares players.
- `projection` (`ProjectionMath.cs`, `PJ`, at mod load): `Matrix4x4.Perspective`, `Ortho`,
  `Frustum`, `LookAt`, `Inverse3DAffine`, `Rotate`, `TRS` and `Quaternion.AngleAxis` to the bit,
  degenerate inputs and the look-at and axis fallbacks' boundaries included, and cameras'
  matrices and conversions (`WorldToScreenPoint` and the rest); then the camera rect rules
  (`PK`: rects and pixel rects out of range, NaN and at half pixels, a target texture, a set
  aspect kept or reset, worldToCameraMatrix's signs of zero) and where `Inverse3DAffine` and
  `ScreenToWorldPoint` give up, by bisection (`PJ PT affine`, `PJ PQ bound`), with simple
  cameras' rays (`PJ PQ ray`); `pj-check.py rule LOG` tests Mesa's, GLU's and the measured
  formulas, `pj-check.py diff ORIG OURS` compares players.
- `transform` (`TransformMath.cs`, `TM`, `TN`, `TR` and others, at mod load): transform
  matrices, rotations, scales, points and RectTransform corners of logged hierarchies to the bit;
  `tm-check.py rule LOG` and `tm-check.py diff ORIG OURS` read the `TM` lines; the file's header
  lists the rest, and `docs/cleanroom/ugui.md` ("Transform hierarchy to the bit") what they
  measured.
- `invscale` (`InverseScale.cs`, `IS1` to `IS4`, at mod load): `worldToLocalMatrix` of single
  transforms over scales of every exponent, a dense sweep of mantissas, and 3,000 chains (UI-like
  and general). `PROBE_IS_PART=full` logs instead each of the 2^23 mantissas' inverse scale as
  its difference from 1/s (`IS5`, a minute), `PROBE_IS_PART=edge` the edges (`IS6`: special
  values, every exponent, the smallest scale with an inverse, by bisection). `is-check.py diff
  ORIG OURS` compares players; `is-check.py table LOG OUT` rebuilds the engine's
  `Math/InverseScale.bin` from a `full` run.
- `scrollgeo` (`ScrollGeo.cs`, `SG`): in any screen, after each `KScrollRect.LateUpdate` of a
  scroll rect whose path contains `PROBE_SG_NAME` (default `SideScreen`), when anything changed:
  its normalized position, the content's world corners, the view's matrices and every level of
  the content and the view to the root, as float bits. Install it in a spec that opens the
  screen, as `vitalsopen`, and diff the two players' last lines to find the first input that
  differs.
