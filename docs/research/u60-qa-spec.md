# U60 QA specification: the U59 to U60 changes

What to test so that each change between U59 (744825) and the U60 public beta (761015) behaves in
Moono as in Unity's player. The question for each item is not whether Klei's feature works; it is
whether ours matches the original when the feature runs. Every item names the part of our engine
it exercises. Items with no new engine surface are kept, marked low risk, and grouped.

Nothing here has been run. Status of the port itself: [u60-inventory.md](u60-inventory.md).

## Method

- **Changed methods.** The authoritative list is the IL fingerprint diff of the U59 release
  against the U60 beta, as oni-versioning's README ("Beta against release") prescribes: `ilspycmd
  -il` (11.1.0.9782) on `Assembly-CSharp.dll` and `Assembly-CSharp-firstpass.dll` from
  `~/projects/oni-linux` (744825) and `~/projects/oni-linux-761015`, then
  `scripts/il-fingerprint-diff.py a.il b.il --methods`. Assembly-CSharp: 1,412 types changed.
  Firstpass: 2,148, of which 2,041 are the Epic Online Services binding (`Epic.*`) and 24
  FuzzySharp; the other 83 were read one by one.
- **Citations.** Read in the C# diff `git diff U59-744825 U60-761015`. `u60:File.cs:N` is a line
  of `OxygenNotIncluded/File.cs` at tag U60-761015, `u59:` the same at U59-744825. Firstpass
  files are not in oni-versioning: `fp60:`/`fp59:` are lines in
  `~/projects/onimaxxing/decomp/u60-csharp-firstpass` and `u59-csharp-firstpass` (checked equal to
  the tags for the files spot-checked). "new" means the U59 release has no counterpart.
- **Not used:** the U59-736385 → U60 beta-to-beta diff. U59 took hotfixes after its last beta, so
  that diff credits U60 with them. Every item of the baseline report found that way was checked
  against the method list above; all of them are in it.
- **Engine members.** Every external member reference (`[Assembly]Type::Member`) in the U60 IL of
  both assemblies, minus those in U59's: 117 new, 17 gone. The ones that matter are in the items
  below (AsyncGPUReadback, `ImageConversion.EncodeArrayToPNG`, `TMP_Text.SetCharArray`,
  `Resources.UnloadAsset`, `Time.unscaledTimeAsDouble`, `File.Replace`, culture `CompareInfo`,
  `Int32.TryFormat`, Steam `UGCDownloadToLocation`). All exist in our engine and Steamworks.NET
  port today; what is untested is the behaviour.
- **Managed DLLs.** No assembly was added or removed (the beta adds `.pdb` files only). Against
  the U59 beta (same compiler settings) the DLLs whose bytes changed are the game's two, VYaml
  (1.2 → 1.4), the Unity packages and modules, FMODUnity, ImGui.NET, LibNoiseDotNet and
  Steamworks.NET (still 20.1.0; the bytes differ, the version does not). Unity's modules and UI
  packages are ours (ARCHITECTURE §1, load context), so their changes reach us only through the
  game's calls, covered by ApiCheck (inventory, "Engine API").
- **StreamingAssets.** Diffed on the Windows copies (`DataNotIncluded-U59-744825`,
  `DataNotIncluded-U60-761015`) and the Linux installs. Changed: two base templates, four DLC base
  templates, the frozen breakroom POI, MetalRich/MetalPoor, codex entries, the three `.po` files
  and the template `.pot`, FMOD banks, asset bundles. New: `dlc5/templates/poi/reef/labPOI1.yaml`,
  `templates/poi_critter_enclosure.yaml`, `templates/rough unused poi/poi_critterenclosure.yaml`,
  three codex entries. No worlds, subworlds, clusters or element yaml changed.

## Harnesses

- **e2e:** a spec in `tests/e2e/` run by `scripts/e2e.py --player both` (README there). Evidence
  is `ui_dump`, `shot`, `world_shot`, `expect_state ... == original` and log signatures. Fixtures:
  `early-base`, `landing`, `decrew`, `prehistoric1`, `rockets-so`, `vanilla-base`, `main-menu`.
- **qa:** `scripts/qa.sh` stages (unit, dogfood scenarios in `scripts/qa-scenarios/`, verify,
  perf, memory).
- **unit:** `tests/OniMoono.UnityEngine.Tests` and `tests/OniMoono.Assets.Tests`.
- **new tooling:** named in the item where the harness lacks a step.

Risk: **high** when the change puts new or changed weight on an engine path whose result a player
sees and which nothing has run yet; **medium** when the path is exercised but the new use differs,
or the item is partly covered; **low** when it is game logic only, or already covered.

60 items: 11 high, 23 medium, 26 low (the G table's 13 rows and V2, V3 among the low).

## Rendering

### R1 Pipe Flow sub-overlay, vanilla

- **Change:** "Added a Pipe Flow sub-overlay to Plumbing and Ventilation Overlays."
- **Provenance:** a ninth batch material type, `MaterialType.Conduit` → shader
  `Klei/BatchedAnimationConduit` (fp60:KAnimBatchGroup.cs:179, 237, 291 `materials = new
  Material[9]`; fp59:KAnimBatchGroup.cs:283 `new Material[8]`). `ConduitFlowKAnimVisualizer.cs`
  (new, u60:ConduitFlowKAnimVisualizer.cs:4 on). `OverlayModes.ConduitMode` flow toggle
  (u60:OverlayModes.cs:78-240). Strings `STRINGS/UI.cs` CONDUITFLOW.
- **Engine surface:** a shader new to the game, run from Unity's shipped Vulkan programs and our
  GLSL path; the instance-data texture carrying a uint read back with `floatBitsToUint`; animated
  by unscaled time. **High:** the existing spec runs it under a mod's icons only.
- **Test:** `decrew`, Spaced Out. F7, shot with the sub-overlay off, toggle Pipe Flow, shot; F6,
  same; then a blocked pipe (a liquid pipe with its output building disabled) close up. Repeat
  under `--renderer opengl` and Vulkan. Pass: world shots within the spec's threshold (the arrows'
  phase differs, so mask nothing but allow 4 % as `pipe-flow.yaml` does), `IsViewConduitFlowEnabled`
  true in both, no new log signatures.
- **Harness:** e2e. Copy `pipe-flow.yaml` without `mods:` as `pipe-flow-vanilla.yaml`.

### R2 Animation batch pooling

- **Change:** not in notes.
- **Provenance:** `KAnimBatchGroup.GetOrCreateBatch`/`ReleaseBatch` and a batch pool
  (fp60:KAnimBatchGroup.cs:453-470), `KAnimBatch.Reuse` and `AtlasList.Reset` (fp60:KAnimBatch.cs),
  `KAnimBatchManager.IsCorrectlyRegistered`; material, mesh and texture names dropped. No U59
  counterpart: batches were always new.
- **Engine surface:** a reused batch keeps its Material, Mesh and instance texture: our material
  property state, mesh reuse and texture rebinding must not leak the previous batch's atlas.
  fast-anim-batches writes into these batches. **Medium.**
- **Test:** `early-base`, sandbox on. Build and deconstruct 40 mixed buildings over several
  `run` steps so batches empty and are reused, then build a different set in the same area.
  World shot of the area after each round. Then QA verify (fast-anim-batches verify mode).
  Pass: shots match, verify reports 0 wrong.
- **Harness:** e2e (new spec) and qa verify.

### R3 Timelapse and colony preview capture

- **Change:** "Improved timelapse capture performance." The mechanism is not in the notes.
- **Provenance:** `Timelapser.CapturePng`, `ReadPixelsSync`, `QueueEncodeAndWrite`,
  `FlushPendingScreenshots` (u60:Timelapser.cs:314-430): `AsyncGPUReadback.Request(rt, 0,
  TextureFormat.RGBA32, cb)` when `SystemInfo.supportsAsyncGPUReadback`, else `ReadPixels`; PNG
  encoding by `ImageConversion.EncodeArrayToPNG` in `Task.Run`; `AsyncGPUReadback.WaitAllRequests`
  and `Task.WaitAll` on cleanup. U59: an ARGB32 `Texture2D.ReadPixels` and `EncodeToPNG` on the
  main thread (u59:Timelapser.cs:290-301). `GroundRenderer.RenderAll` is gone
  (u59:GroundRenderer.cs:511). `SaveColonyPreview(filename, isInitialSave)` also writes the first
  timelapse frame (u60:SaveLoader.cs:930).
- **Engine surface:** AsyncGPUReadback on both renderers (row order, RGBA byte order, when the
  callback runs), `NativeArray<byte>.ToArray`, `EncodeArrayToPNG` called from a worker thread,
  `Debug.LogWarning` from that thread. **High:** every save writes a preview through it.
- **Test:** `early-base`. Save from the pause menu as a new file; then `run` to the next cycle so
  the timelapse writes a frame. Read the colony's `.png` preview and the timelapse frame from the
  run's profile. Run with OpenGL and Vulkan. Pass: same width and height as the original's, not
  flipped, channels in order, pixel difference within the `world_shot` threshold; no
  "GPU readback failed" or "failed to write screenshot" line.
- **Harness:** e2e, with new tooling: a step that copies a profile file into the evidence and
  compares it as an image (`qa-tools.py image` already compares two PNGs).

### R4 Colony previews on the Load and Colony Summary screens

- **Change:** "Fixed memory leaks loading colony images on the Load Game screen", "Improved memory
  usage on Colony Summary screen".
- **Provenance:** `RetireColonyUtility.LoadPreviewTexture`, `DownscaleToFit`, `CreatePreviewSprite`
  (u60:RetireColonyUtility.cs:474-508): images over 1024 px are drawn by `Graphics.Blit` (bilinear)
  into a temporary ARGB32 render texture, read back with `ReadPixels` into RGBA32, `Apply(false,
  true)`, and the source destroyed; `Sprite.Create(..., SpriteMeshType.FullRect)`. U59 used the
  loaded texture as is (u59:RetireColonyUtility.cs:452-520). `LoadScreen.ReleasePreviewSprite`,
  `DestroyPreviewSprite` (u60:LoadScreen.cs:1238-1276); `Slideshow.DestroyCurrentSlideImage`,
  `RetiredColonyInfoScreen.OnForcedCleanUp`.
- **Engine surface:** `Graphics.Blit` scaling with bilinear filtering, `RenderTexture.GetTemporary`,
  `ReadPixels` from it, `makeNoLongerReadable`, Sprite FullRect meshes in uGUI Image, texture
  destruction while sprites are shown. **High:** every Load screen shows one.
- **Test:** `main-menu` with two saves whose previews are wider than 1024 (the default timelapse
  resolution is). Load Game, select each colony, shot the preview; close and reopen five times.
  Then a retired colony's summary slideshow. Pass: shots match within 2 %; texture memory
  (`ONI_TEXTURE_MEMORY_LOG`) does not grow across the reopenings.
- **Harness:** e2e (new spec, `from: main-menu`, the saves placed by setup).

### R5 Render targets of the world camera

- **Change:** not in notes.
- **Provenance:** `MultipleRenderTargetProxy.RecreateCopyRT`, `DestroyRT`, `OnDestroy`
  (u60:MultipleRenderTargetProxy.cs:39-120): copies are reused when size, format and depth match;
  the third target and its copy are destroyed when the coloured overlay buffer is off; all are
  destroyed with the camera. U59 made new copies on every resize and never freed them
  (u59:MultipleRenderTargetProxy.cs:57).
- **Engine surface:** `Camera.SetTargetBuffers` with two, then three colour buffers; render
  texture destruction while bound; resize. **Medium.**
- **Test:** the `resize` and `overlays` QA scenarios as they are; plus an e2e that toggles an
  overlay using the coloured buffer on and off ten times, resizes, and loads a second save.
  Pass: captures match references; the perf stage's "multi-target framebuffers not piling up"
  check holds.
- **Harness:** qa dogfood and perf; e2e for the toggle loop.

### R6 World visual fixes

- **Change:** "Fixed tops of tiles not rendering when there are wall tiles on either side";
  "Carpet Tiles should no longer have a visual gap"; "Metal Tiles should now show accurate preview
  art when under construction"; "Fixed Buried Object cracks sometimes being left behind";
  "under-construction buildings looked pixelated on some overlays"; "gas and liquid vents would
  sometimes appear open when disabled by automation"; "Rock Crusher not displaying the output item";
  "Metal Refinery now has an animation when outputting a product".
- **Provenance:** `TileVisualizer` refreshes the four diagonal cells (u60:TileVisualizer.cs:32;
  absent in U59). `AnimTilableSingleController` gains `partitionerUpdateRange`, a preview `Update`
  that re-tiles on cell change and `requireNeightbourCellToBeCellOfInterest`.
  `VentController.IsClosed` and `off.EnterTransition(closed, ...)` (u60:VentController.cs:66-84).
  `MetalRefineryConfig.DoPostConfigurePreview`, `RockCrusherConfig`. The overlay pixelation and the
  carpet gap have no code change found: data (materials or anims).
- **Engine surface:** block tile meshes, kanim symbol visibility, overlay material sampling.
  **Medium:** visible, but driven by game code we run unchanged.
- **Test:** `early-base`, sandbox. One `world_shot` per case: a tile row with drywall each side;
  three carpet tiles; a metal tile blueprint; a buried object dug out; a building under
  construction in the Power and Decor overlays; a gas vent with automation red; Rock Crusher and
  Metal Refinery mid-output. Pass: each shot matches.
- **Harness:** e2e (new spec).

### R7 New and changed animations and sprites

- **Change:** "Animations" list (grow-up anims, grooming calls, Pipsqueak sleep, Drecko egg, Seaquine,
  salt shaker layering, belt symbol, Beach Chair pop, Pacu on the Aquatic Grooming Station,
  making a mess, cheering, swim waving); "Added artwork for Frozen Ovolene"; "Updated the icon ...
  Neutronium"; Vulcanizer power-cut anims; Slogo and Gildgo anims and icons; Jawbo tame tag;
  Pinpoki layering; Glo Squid face spots; Beakon Fry breathing; Bionic stunner swim anims; Sweep
  Only light; the new buildings' and Merpip's anims.
- **Provenance:** asset bundles (`dlc2..dlc5_bundle`, `expansion1_bundle`, `hires_*`) and
  `sharedassets`; code only where layering moved (e.g. Pinpoki `SceneLayer`). `Assets.LoadAnims`
  changed (u60:Assets.cs:237-266).
- **Engine surface:** our bundle reader and kanim build/anim texture upload for files new to the
  data; symbol overrides; z-ordering of scene layers. **Medium.**
- **Test:** sandbox spawner on `landing` with all DLCs: spawn each new or changed critter and
  building, play the named anim where a state can be forced (`setup.call` may prepare, not
  perform), `world_shot` each. Pass: shots match; no "anim not found" or symbol warnings in ours
  only.
- **Harness:** e2e (new spec, several shots per run).

### R8 Temperature overlay icons and colour modes

- **Change:** heat source/sink icons for geysers, Thermo Regulator, Aquatuner, Liquid Tepidizer,
  Research Reactor; "Added Tritanopia colorblind support"; "Fixed Absolute Zero broken string on
  Temperature overlay when a colorblind mode is enabled".
- **Provenance:** `GeyserGenericConfig.cs:196,204`, `AirConditionerConfig.cs:48`,
  `LiquidHeaterConfig.cs:54`, `NuclearReactorConfig.cs:138` (U60 lines, from the baseline and
  confirmed in the method list). `GraphicsOptionsScreen.GetColorModeIdFromPrefs`,
  `ToLegacyColorModeId`, `ColorModeKeyV2` (u60:GraphicsOptionsScreen.cs:280-318); the colour set
  count comes from `GlobalAssets.colorSetOptions`, an asset. `SimDebugView.PrepareDiseaseColours`,
  `get_TemperatureColors` (u60:SimDebugView.cs:466-848). "Tritanopia" itself was already in U59
  (u59:STRINGS/UI.cs:4250).
- **Engine surface:** overlay icon sprites, KPlayerPrefs, the colour-set asset read by our
  serializer. **Medium.**
- **Test:** `decrew`. Options > Graphics: `ui_dump` the colour mode dropdown (its entries); pick
  each mode, F3, `world_shot` over a geyser and an Aquatuner; check `KPlayerPrefs` keys `ColorModeKeyV2`
  and the legacy one hold the same values as the original's. Pass: dumps and shots match,
  `== original` on both prefs.
- **Harness:** e2e (new spec).

### R9 Codex videos

- **Change:** not in notes.
- **Provenance:** `VideoWidget.ReleaseRenderTexture` (u60:VideoWidget.cs:56-79), `VideoScreen`
  changes: the render texture is destroyed when the widget goes.
- **Engine surface:** our VideoPlayer, a RawImage showing a destroyed render texture. **Medium.**
- **Test:** `early-base`. Open a codex entry with a video, wait two seconds, navigate to another
  entry and back three times. Shot each time. Pass: shots match, no error lines.
- **Harness:** e2e.

## Input and the event system

### I1 Mouse buttons 3 to 6 as bindings

- **Change:** "Allowed key binding of mouse buttons 3-6."
- **Provenance:** `InputBindingsScreen.validKeys` 111 → 115 entries, `KeyCode.Mouse3`–`Mouse6`
  (u60:InputBindingsScreen.cs:52, 165-168; u59:InputBindingsScreen.cs:52); the screen polls
  `Input.GetKey`/`GetKeyDown` for them while rebinding. `KInputController` actions 280 → 281
  (fp60:KInputController.cs:41, 103).
- **Engine surface:** our input layer. GLFW reports X11 buttons 8–11 as its buttons 3–6, which
  `InputStateFeed` maps to `KeyCode.Mouse3`–`Mouse6` (`GlfwPlatform.cs`); whether Unity's X11
  and Windows players number the side buttons the same way is unmeasured. Also whether Unity
  sends IMGUI mouse events for them. **High.**
- **Test:** `early-base`. Options > Controls, rebind an action (e.g. Dig) to the back side button
  (X11 button 8), then to button 9; press it in the world. A KeyLog run records `Input.GetKey` per
  button 8–11. Pass: the binding label reads the same as the original's (`ui_dump`), the tool
  activates, and the KeyLog matches per button.
- **Harness:** e2e with new tooling: `click` and `key` accept X buttons 8–11 (XTEST sends any
  button number). Windows needs a separate manual check with a five-button mouse.

### I2 Mouse wheel on sliders

- **Change:** "The mouse wheel can now be used to adjust sliders."
- **Provenance:** `KSlider : IScrollHandler`, `OnScroll` with a 16-step table and
  `MoveSource.ScrollWheel` (fp60:KSlider.cs:46, 125-175; fp59:KSlider.cs has neither).
  `Slider.wholeNumbers` is read for the first time by game code.
- **Engine surface:** our uGUI event system: `ExecuteEvents.GetEventHandler<IScrollHandler>`
  picks the slider before an enclosing ScrollRect; `PointerEventData.scrollDelta` sign and size
  per notch; `IsActive`, `IsInteractable`; the slider's move sound. **High.**
- **Test:** `early-base`, sandbox. A Smart Storage Bin (its new threshold sliders), a Temperature
  Sensor side screen slider inside the details screen's scroll view, and a whole-number slider
  (Timer Sensor). Scroll +1, +3, −2 notches over each handle. Pass: slider values `== original`
  after each step, the side screen's scroll position unchanged, `KSlider:PlayMoveSound` call
  count equal (`driver: count`).
- **Harness:** e2e (new spec).

### I3 Keyboard panning at any frame rate

- **Change:** "Using the keyboard to move in the Research Screen and the Skills Screen should now be
  more consistent across different frame rates"; the same for the Spaced Out Starmap.
- **Provenance:** `KScrollRect` keyboard scroll scaled by `60f * Time.unscaledDeltaTime`
  (fp60:KScrollRect.cs:188-208; fp59:KScrollRect.cs:186-206 per frame); `ResearchScreen` pan delta
  (u60:ResearchScreen.cs:226; u59:ResearchScreen.cs:223 without the factor). ClusterMapScreen has
  only compiler-shape changes; its fix is elsewhere or in KScrollRect.
- **Engine surface:** `Time.unscaledDeltaTime`. Our frame rate differs from the original's, so the
  per-frame distance does too; the per-second distance should not. **Medium.**
- **Test:** `early-base`. Research screen: hold the pan-right key 2 s of real time, read the
  content's anchored position; the same on the Skills screen and the starmap (`landing`). Run
  each player twice, once with a frame cap at the original's measured rate. Pass: with the cap,
  displacement `== original` within 5 %; without it, within 15 % (the delta is applied twice, so
  it is not fully rate-independent).
- **Harness:** e2e. The cap is `Application.targetFrameRate` set in `setup:` (it prepares the
  run, so `setup.call` may do it); check it holds in both players before relying on it.

### I4 Move To hotkey

- **Change:** "Added keyboard shortcut for “Move To” and “Relocate To”."
- **Provenance:** `UserMenuScreen.OnKeyDown` consumes `Action.BuildMenuKeyQ` for a selected
  `Movable` or a Duplicant's `MoveToLocationMonitor` (u60:UserMenuScreen.cs:182-201, new).
- **Engine surface:** key events through KInputController. **Low.**
- **Test:** `early-base`. Select debris, press Q, click a cell; select a Duplicant, press Q.
  Pass: `MoveToLocationTool` active in both (`expect_state`), the debris gets a move chore.
- **Harness:** e2e.

### I5 Renaming with Enter

- **Change:** "Fixed pressing Enter to confirm a building rename also toggling Enable Building";
  "pressing Enter after deleting the text preventing the building from being renamed again";
  "link tags should no longer be editable"; "the 'View full entry in Database' button should
  still work".
- **Provenance:** `EditableTitleBar.OnEndEdit` strips link formatting and re-enables the edit
  button after ten frames; the input field gets the stripped title
  (u60:EditableTitleBar.cs:58-81, 154; u59:EditableTitleBar.cs:58-80). `BuildingEnabledButton`
  moved to the new subscribe API. The Enter double handling has no other change found; it depends
  on which handler consumes Return first.
- **Engine surface:** our `TMP_InputField` (submit, end edit, deselect order), the frame Return
  reaches KInputController after the field ends editing, coroutines with `WaitForEndOfFrame`.
  **High:** key-event order is ours.
- **Test:** `early-base`. Select the Ration Box, rename to "e2e" with Enter; check the building
  is still enabled. Rename again, clear the text, Enter; rename a third time. Open a building whose
  name has a link, edit. Click "View full entry in Database" after renaming. Pass: names, enabled
  state and the codex article `== original`; `ui_dump` of the title bar matches.
- **Harness:** e2e (new spec).

### I6 Clicks on priority toggles and research icons

- **Change:** "Priority setting toggles now remain visually enabled when double-clicked"; "Hovering
  over an item on the Research screen now displays an icon which can be clicked to open the item's
  database entry."
- **Provenance:** `PriorityButton` sets `toggle.isOn = true` (u60:PriorityButton.cs:58, new line).
  `ResearchEntryIcon` (new class, u60:ResearchEntryIcon.cs:16-42: `OnPointerEnter`,
  `OnPointerExit`, `OnInfoButtonClick`).
- **Engine surface:** double-click counting and toggle groups in our event system; pointer enter
  and exit on small nested targets. **Medium.**
- **Test:** `early-base`. Double-click priority 7 in the priority screen and in a building's side
  screen, `ui_dump` the group. Research screen: hover an unlock icon, shot, click its info icon.
  Pass: dumps match, codex opens on the same article.
- **Harness:** e2e.

### I7 The pointer in the world

- **Change:** "Potential fix for objects that become unselectable after they fall"; "Buildings that
  have solid tiles ... should no longer display an element tooltip"; "Fixed a crash that could occur
  using drag tools such as the Dig tool"; "Added Square shape option to most sandbox tools".
- **Provenance:** `InterfaceTool.GetObjectUnderCursor`, `UpdateHoverElements` (sorting removed),
  `KCollider2D` (4 methods), `SelectToolHoverTextCard`, `DigTool.PlaceDig`, `BrushTool.VisitBrushOffsets`,
  `SupportsNonCircleBrushShapes` (u60:BrushTool.cs:81-126).
- **Engine surface:** hover and selection under the cursor, drag input across cells. **Medium.**
- **Test:** `early-base`, sandbox. Drop debris down a shaft and select it; hover the solid tile
  of a Solar Panel; drag-dig a large rectangle then cancel mid-drag with Escape; square sandbox
  brush. Pass: `ui_dump` of the hover card matches; selections and dig orders `== original`.
- **Harness:** e2e.

## UI, text and layout

### U1 The text pipeline

- **Change:** "Added KleiStringBuilder to reduce string allocations" (Modding).
- **Provenance:** `LocText.SetContent`, `ParseText`, `ManifestStyledLinks`, `NeedsLinkStyling`
  (u60:LocText.cs:211-228, new) call `TMP_Text.SetCharArray(ReadOnlySpan<char>)`.
  `KleiStringBuilder` (fp60:KleiStringBuilder.cs, new) formats with `Int32/Int64.TryFormat` into
  spans (fp60:KleiStringBuilder.cs:344-378). `HoverTextDrawer` reuses widgets.
- **Engine surface:** our TMP (SetCharArray, link parsing), number formatting in the current
  culture. **Medium:** implemented and run by every QA scenario; one open check (below).
- **Test:** the open check from the inventory: whether Unity's TMP skips an unchanged
  `SetCharArray` (layout rebuild, `OnPreRenderText` events). Measure in the original with
  UnityProbe; then an e2e that sets a hover card's text to the same value on consecutive frames
  and counts layout rebuilds. Also run the QA dogfood with `LANG=ru_RU.UTF-8` and `de_DE.UTF-8`.
  Pass: rebuild counts equal; dogfood captures match the English references' layout.
- **Harness:** UnityProbe, e2e, qa dogfood.

### U2 Hidden table screens disable their masks and scroll views

- **Change:** "Significantly improved scrolling performance on the Consumables and Priorities
  screens"; "Fixed performance overhead incurred from opening the Priorities, Vitals, or Consumables
  screen for the first time."
- **Provenance:** `ShowOptimizedKScreen.SetHiddenBehavioursEnabled`, `DisableEnabledInChildren<T>`
  for `RectMask2D`, `ScrollRect`, `KBatchedAnimCanvasRenderer` (fp60:ShowOptimizedKScreen.cs:35-76,
  new), used by `TableScreen`, `AllResourcesScreen`, `AllDiagnosticsScreen`.
- **Engine surface:** `Behaviour.enabled` toggles on masks and scroll views in inactive trees;
  RectMask2D unregistering and re-registering its clippables; our canvas mask skips and the UI
  walk that skips hidden screens (1eb657c9). **High.**
- **Test:** `early-base` with the foods discovered as in `consumables-scroll.yaml`. Open
  Consumables, scroll, close; open Vitals, Priorities, back to Consumables; then All Resources and
  Diagnostics. `ui_dump` and shot after each open. Pass: dumps match (rect masks, scroll
  positions, row clipping), portraits drawn, no rows outside the viewport.
- **Harness:** e2e (extend `consumables-scroll.yaml` or a new spec).

### U3 Build menu and material selection

- **Change:** "Added the Farming build menu category"; "Stations ... renamed to Operations";
  "Building material selection now shows missing resources, including undiscovered resources, and
  hints at which research needs to be completed"; "Buildings which must be built against a backwall
  now mention this"; "Buildings that have special placement requirements can be queued up";
  "Support" subcategory.
- **Provenance:** `PlanScreen` (44 methods; `TryGetSubCategoryTitle`, `GetSubCategoryGrid`,
  `String.CompareOrdinal` at u60:PlanScreen.cs:1328), `MaterialSelectionPanel` (18),
  `MaterialSelector` (32), `ProductInfoScreen` (11), `BuildingDef` (18), `TUNING/BUILDINGS.cs`
  plan categories.
- **Engine surface:** GridLayoutGroup and toggle layout, TMP wrapping of the new hint lines.
  **Medium.**
- **Test:** `early-base`. Open each category, `ui_dump` the plan screen; open a building whose
  material is undiscovered and one needing a backwall, `ui_dump` the product info and material
  panel; queue a building on an under-construction foundation. Pass: dumps match; the queued
  blueprint exists in both.
- **Harness:** e2e (extend `build-menu-search.yaml`'s pattern).

### U4 Blueprints, Supply Closet and fuzzy search

- **Change:** blueprint search bars (building info screen, Duplicant tab), outfit blueprints near
  the top, Copy Blueprint tool, drywall Rotate button, Supply Closet layouts, "See more Blueprints"
  deep link, Electrical → Power, the Regal pump rename, Manual Generator look.
- **Provenance:** `Database.BlueprintSearch` (new, u60:Database/BlueprintSearch.cs:6),
  `FuzzySearch` (u60:FuzzySearch.cs:75-193; u59:FuzzySearch.cs:55-142), FuzzySharp rewritten on
  spans with `CultureInfo.CurrentCulture.CompareInfo` (fp60:FuzzySharp/TokenizedString.cs:22,
  FuzzySharp.SimilarityRatio.Scorer.StrategySensitive/TokenSortScorerBase.cs:12), `CopyCosmeticsTool`
  (new), `CosmeticsPanel` (13), `FacadeSelectionPanel` (16), `KleiInventoryScreen` (15),
  `OutfitBrowserScreen_CategoriesAndSearchBar`, `KleiPermitDioramaVis_*`.
- **Engine surface:** culture-sensitive string comparison (our CoreCLR uses ICU on Linux; the
  original uses Mono's collation), TMP input fields, scroll views and grids of the Supply Closet.
  **Medium.**
- **Test:** `early-base`. Search "pump", "wtr pmp", "regal" and a two-token query in the Duplicant
  blueprint tab and the building info screen; `ui_dump` the result list order. Copy a Duplicant's
  outfit to another with the tool. Rotate a drywall blueprint. Open the Supply Closet from "See
  more" and from the main menu, at 1280×720 and 1280×800. Pass: result order and dumps match,
  outfits `== original`.
- **Harness:** e2e (new spec).

### U5 Details screen and codex layouts

- **Change:** critter and plant subtitles; recreation effect durations; large diets; light range
  as whole numbers; recipe side screen height; Orbital Cargo side screen on Steam Deck; Suit
  Wearing category in skills; Effects indentation; codex Critters/Plants by Type pages and layout;
  collapsible headers after navigating; drops formatting; Food Quality hidden for ingredients;
  Applications sections; Medical Pack link; Power Banks heading; booster recipes; Refashionator;
  Flue Coral checkbox and "Peak Conditions"; Breathing Station charges; Decor plants' Throughput
  label; Neutronium tooltip.
- **Provenance:** `SimpleInfoScreen` (28), `AdditionalDetailsPanel` (8), `DescriptorPanel`,
  `CollapsibleDetailContentPanel`, `CodexEntryGenerator*` (31, 26, 7, 7),
  `CodexCollapsibleHeader.ResolveContentsGameObject` replacing a cached `ContentsGameObject`
  (u60:CodexCollapsibleHeader.cs:27-40; u59:CodexCollapsibleHeader.cs:12-52), `Descriptor`
  indent API (fp60:Descriptor.cs), `SelectedRecipeQueueScreen` (6), codex yaml
  (`codex/TechComponents/DynamicCodexEntry_Electrobank.yaml` new).
- **Engine surface:** layout groups, ContentSizeFitter, TMP; Unity's destroyed-object null for
  the cached header (`== null` on a destroyed GameObject). **Medium.**
- **Test:** `landing` with all DLCs. Select a Hatch, a Pip, a Bristle Blossom, a Soda Fountain, a
  lamp; `ui_dump` the details panels. Codex: Critters by Type, Plants by Type, Hatch, Muckroot,
  Balm Lily, Power Banks; collapse a header, open another article, come back, collapse again.
  Skills screen. A fabricator's recipe screen at 1280×800. Pass: dumps match; the header toggles in
  both.
- **Harness:** e2e (two or three specs by screen).

### U6 Tutorial hiding

- **Change:** "Simplified tutorial hiding ... This can be reset in the Options screen."
- **Provenance:** `Tutorial` (u60:Tutorial.cs:71-99), `GameOptionsScreen.OnShowTutorialToggle`,
  `RefreshShowTutorialToggle` (new), `OnTutorialReset` removed; `MessageDialogFrame` (9).
- **Engine surface:** KPlayerPrefs, a toggle. **Low.**
- **Test:** `landing`. Tick "don't show again" on the first tutorial; check none shows for a
  cycle; Options > Game, re-enable. Pass: prefs and shown messages `== original`.
- **Harness:** e2e.

### U7 Translation validation

- **Change:** not in notes.
- **Provenance:** `Localization.ValidateTranslatedString` (u60:Localization.cs:886-930, new),
  called from `OverloadStrings` (u60:Localization.cs:567): a translated string with mismatched
  parameters, more links than English or unbalanced link tags is rejected. The ko, ru and zh `.po`
  files changed.
- **Engine surface:** string scanning with `String.Split` and the culture-sensitive
  `String.IndexOf(string, int)` (ICU in ours, Mono's collation in the original). **Low.**
- **Test:** the existing translation specs (`mods/929139073-german-translation.yaml` and others),
  plus `language: zh_klei`, `ru_klei`. Count the rejection log lines. Pass: the same count and
  keys as the original's.
- **Harness:** e2e (existing specs; add a `require_log` line).

### U8 Front-end screens

- **Change:** "Adjusted arrow position on asteroid selection screen"; "Load Game screen has more room
  for displaying save name and cycle count"; "Fixed white lines surrounding DLC logos on load screen";
  "Show fewer details in the mod restart dialog".
- **Provenance:** `LoadScreen` (17), `ColonyDestinationSelectScreen`, `KMod.Manager.DevRestartDialog`
  (u60:KMod/Manager.cs:954), `MainMenu.RefreshResumeButton`; DLC logo sprites in the data.
- **Engine surface:** sprite sampling at the logos' edges (the white lines were a texture-border
  artefact), TMP truncation. **Medium.**
- **Test:** `main-menu`. Shot the main menu's DLC logos, the Load screen with a long colony name,
  the asteroid carousel; enable a mod to raise the restart dialog. Pass: shots within 2 %, dumps
  match.
- **Harness:** e2e (the mod dialog already appears in QA; qa dismisses it, e477137a).

### U9 Sandbox tool menu

- **Change:** "Added Square shape option"; "Added Foreground/Background options"; "Added Size control
  to the Critter Removal sandbox tool"; "Pacu Treat is now available in the Spawner"; "The Destroy
  sandbox tool also deletes falling elements".
- **Provenance:** `SandboxToolParameterMenu_ShapeLayerControl` (new, u60:...:28-80),
  `SandboxToolParameterMenu` (16), `UnstableGroundManager` (u60:UnstableGroundManager.cs:167).
- **Engine surface:** toggles and sliders in the tool menu. **Low.**
- **Test:** `early-base`, sandbox. Each tool's parameter menu `ui_dump`; a square brush stroke; a
  destroy over falling sand. Pass: dumps and the cells' mass `== original`.
- **Harness:** e2e.

## Runtime and BCL

### L1 Cell change tracking and KObject

- **Change:** "Moved CellChange events to the EventSystem ... added cachedCell to KObject's",
  "Fixed memory leaks related to pathfinding and loading another save".
- **Provenance:** `CellChangeMonitor.RegisterKObject`, `UnregisterKObjectHierarchy`,
  `MarkDirtyRecursive` keyed by `Transform.GetInstanceID`, walking `childCount`/`GetChild`
  (fp60:CellChangeMonitor.cs:164-268); `KObject` gains `gameObject`, `Trigger`,
  `DetachFromCellChangeMonitor` (fp60:KObject.cs); `KMonoBehaviour.KObj`; `Grid.PosToCell`.
- **Engine surface:** instance ids, child order, destruction order (the cellchangecrash
  Destroy-from-OnDestroy case, already fixed: faba323a). **Medium.**
- **Test:** QA verify on U57 (moving critters, falling debris, conveyors); an e2e on `rockets-so`
  launching and landing a rocket; load a second save from the pause menu, then a third. Pass: no
  new log signatures; positions of a tracked critter and a rocket `== original` after the runs;
  managed heap after the third load within 5 % of after the first.
- **Harness:** qa verify, e2e (`rocket-launch.yaml` extended), `--frame-stats`.

### L2 New BCL calls

- **Change:** not in notes.
- **Provenance:** `File.Replace(tmp, final, bak, true)` and `File.Move` in
  `SteamUGCService.SwapDownloadedFile` (u60:SteamUGCService.cs:199-211); `File.Replace` in
  `SaveLoader.MoveCorruptFile` (u60:SaveLoader.cs:246, inside a `catch` after an empty `try`, so
  unreachable as decompiled); `CompareInfo` from `CultureInfo.CurrentCulture` in FuzzySharp
  (U4); `Int32/Int64.TryFormat` (U1); `Task.Run`, `Task.WaitAll`, `Interlocked.CompareExchange`
  (R3); `HashCode`, `MemoryExtensions`, `ArraySegment<byte>` in VYaml-generated formatters.
- **Engine surface:** CoreCLR's BCL in place of Mono's. **Medium.**
- **Test:** unit tests in `OniMoono.UnityEngine.Tests` that pin Mono's results where they may
  differ: `File.Replace` over an existing and a missing destination and a read-only backup;
  `CompareInfo.Compare` ordering of the blueprint names under `en-US`, `de-DE`, `ru-RU` and the
  invariant culture, compared with values recorded from the original (UnityProbe). Pass: equal.
- **Harness:** unit, UnityProbe.

### L3 Development-build calls and new engine members

- **Change:** "Minor unity version update 6000.3.5f2 -> 6000.3.17f1."
- **Provenance:** the new external members listed under Method. `UIScheduler.GetTimeAsDouble`
  uses `Time.unscaledTimeAsDouble` (u60:UIScheduler.cs:16, new).
- **Engine surface:** ApiCheck and the no-op assertion and profiler members. **Low:** covered
  (67946a05, 1976a914; inventory "Engine API").
- **Test:** `scripts/check.sh` (ApiCheck) on the U60 data. Pass: only the 13 known UI Toolkit
  members missing.
- **Harness:** qa unit.

## Assets and data

### A1 Templates through VYaml 1.4

- **Change:** "Upgraded VYaml from 1.2.0 to 1.4.0"; "POI templates are now loaded using VYaml";
  "Faster worldgen when starting a new game."
- **Provenance:** `TemplateCache.LoadTemplateYaml` → `KYaml.LoadFile<TemplateContainer>` then
  `ApplyVYamlDefaults` (u60:TemplateCache.cs:40-63; u59:TemplateCache.cs:48, YamlDotNet);
  VYaml attributes on `TemplateContainer` (29), `TemplateClasses.Prefab` (30), `Cell`,
  `StorageItem`, `Rottable`, `TemplateAmountValue`; `Info` (17). All templates, not only POIs.
- **Engine surface:** VYaml runs from `Managed/` on our BCL (UTF-8 decoding, number parsing),
  source-generated formatters, reflection over attributes. **Medium:** a new game generates
  without errors and matches QA's references (inventory, 2026-10-31); a value read differently
  would not log.
- **Test:** new tooling: an E2eDriver command (or a PerfProbe mode) that loads every `.yaml` under
  `templates/` and the DLC template folders through `TemplateCache.LoadTemplateYaml` and writes one
  line per file: cell count, prefab count, the sums of mass and temperature, prefab ids, other
  values' hash. Run in both players on the same data. Then a new game for each start
  (`SNDST-A`, forest, swamp, the DLC starts), checking spawnables and the start base cells
  functionally. Pass: every line `== original`.
- **Harness:** new tooling; e2e worldgen pattern.

### A2 Asset loading at the main menu

- **Change:** "Improve loading time of assets and POIs when entering the main menu."
- **Provenance:** `Assets.LoadAnims`, `CreatePrefabs`, `SortPrefabsBySignificantDLC`,
  `AddCountableBuildMaterialCategories` (u60:Assets.cs:212-338), `GlobalAssets`, `Db`.
- **Engine surface:** our bundle reader and the order prefabs are made in. **Medium.**
- **Test:** second launch (start-up rule) to the main menu, ours and original. Count prefabs
  (`Assets.Prefabs.Count`), anims (`Assets.Anims.Count`), and the first 50 prefab ids in order.
  Pass: equal; ours' time to menu no worse than on U59 by more than 10 %.
- **Harness:** e2e (`from: main-menu`), the start-up timing script.

### A3 Starting-biome vistas

- **Change:** the Sandstone, Forest, Swamp, Ice Cave vistas; "Improved the way vista textures are
  loaded in memory".
- **Provenance:** `VistaConfigs` (u60:VistaConfigs.cs:27-155), `Vista` (16), `VistaLayers` with
  `Resources.Load`/`UnloadAsset` (u60:VistaLayers.cs:77-123), `VistaSelectionPanel` (new). Base
  templates spawn the vista entity (`templates/bases/forestBase.yaml`, `sandstoneBase.yaml`,
  `dlc/expansion1/.../swampBase.yaml`, `dlc/dlc2/.../ceresBase.yaml`; DLC5 `beachBaseVista.yaml`
  changed); Garden is commented out in `dlc/dlc4/templates/bases/gardenBase.yaml`.
- **Engine surface:** `Resources.UnloadAsset` and reload from the asset file (0e763810). **Medium:**
  the toggle is covered; the spawn at worldgen is not.
- **Test:** `vista-toggle.yaml` as is; plus a new game per start (Terra, Forest, Swamp in Spaced
  Out, Ceres, Beach) with a `world_shot` of the pod. Pass: shots match.
- **Harness:** e2e (existing and new).

### A4 Data files

- **Change:** MetalRich/MetalPoor and Zinc Ore, the frozen breakroom POI, codex entries, strings.
- **Provenance:** `worldgen/traits/MetalRich.yaml:35-37`, `MetalPoor.yaml:35-37`;
  `dlc/expansion1/templates/poi/frozen/frag_breakroom.yaml` (every `preventFoWReveal` removed);
  three new codex yaml; FMOD banks (base, DLC2, DLC4, DLC5, expansion1); `COSMETIC2_ID` (Steam
  app 5315880) maps to `hires_cosmetic2_bundle`, which the data does not ship
  (fp60:StreamedTextures.cs, fp60:SteamDistributionPlatform.cs:54-55).
- **Engine surface:** none new, except a DLC the Steam double may report as owned whose bundle is
  missing. **Low.**
- **Test:** check what `tools/SteamStub` answers for app 5315880 and run the main menu with it
  owned and not owned: both players must log the same and start. Worldgen with MetalRich on a
  zinc world: count Zinc Ore cells. Pass: `== original`.
- **Harness:** e2e.

## Saves

### S1 Save format 7.39 and U59 saves

- **Change:** "Updated EntombedItemManager and BubbleManager to serialize disease using the id hash";
  "Duplicants in existing saves automatically gain these traits if they have been skill scrubbed";
  "Fixed critters being unable to use a critter condo for one cycle after load"; "Doors left in an
  open state will now close correctly when loaded".
- **Provenance:** `SAVE_MINOR_VERSION_DISEASE_HASHES`, minor 38 → 39 (u60:SaveManager.cs:84-86;
  u59:SaveManager.cs:84); `EntombedItemManager` (u60:EntombedItemManager.cs:20-80);
  `MinionResume.GrantedSkillIDs` (u60:MinionResume.cs:51, 118); `Door.OnSpawn`
  (u60:Door.cs:180-195).
- **Engine surface:** KSerialization with our save tweaks (fast-serialization, fast-save-compression,
  background-save). **Medium:** the tweaks were reviewed, not run on a 7.39 round trip with these
  members.
- **Test:** load a U59 save (DeCrew, U57) in U60, save, load the result; in a new colony entomb a
  germy item and make a germy bubble, save, load. Every tweak on, then none. Pass: entombed items'
  disease and count, Duplicants' traits, door states `== original` after each load.
- **Harness:** e2e (`expect_state` over `all:` lists), qa saveload.

### S2 Saving while screenshots are written

- **Change:** "Improved stutter from initial save after closing the message."
- **Provenance:** `SaveScreen.DoSaveWhenSafe` waits for `Game.CanSave` (u60:SaveScreen.cs:95-110,
  new), `SaveLoader.SaveAndQuitRoutine` (u60:SaveLoader.cs:809), the initial save writes the first
  timelapse frame (u60:SaveLoader.cs:930); `Timelapser.HasPendingScreenshotWrites` and the flush on
  cleanup (R3); `Global.OnExitRequest` (u60:Global.cs:360-407).
- **Engine surface:** background-save moves the save off the frame; U60 starts a GPU readback and
  a worker-thread PNG write inside it. Both must finish, in order, before quit or load. **High.**
- **Test:** `early-base`: save with background-save on, then immediately quit to the main menu;
  save and quit to desktop (`SaveAndQuitRoutine`); autosave at a new day with the timelapse on.
  Pass: the save loads, its preview PNG is complete (R3's comparison), no exception from the
  writer task.
- **Harness:** e2e, `E2E_GAME_TWEAKS=background-save` and `none`.

## Mods

### M1 Loose-file Workshop mods

- **Change:** "Update our mod-loading code to accommodate the new SteamUGC format, namely loose files
  in a folder, as well as continuing to support the legacy zip format."
- **Provenance:** `KMod.Steam.MakeMod` tries `new Directory(pchFolder)` before `new ZipFile`
  (u60:KMod/Steam.cs:21-131; u59:KMod/Steam.cs:12-84).
- **Engine surface:** our mod loading hooks (ModAccess's transpiler on `DLLLoader.LoadDLLs`,
  ModVersionStamp on `KMod.Local.Subscribe`), assembly loading from a plain folder. **High:** not
  run (inventory, "Steam's loose-file mod format").
- **Test:** a Workshop mod laid out as a folder in `steamapps/workshop/content/457140/ID/` (copy a
  covered mod's zip contents there), with the Steam double reporting it installed at that folder.
  Start; open Mods. Pass: the mod loads in both players with the same log lines and its feature
  works (its existing spec passes).
- **Harness:** e2e, with new tooling: SteamStub serving `GetItemInstallInfo` for a folder.

### M2 Corrupt Workshop items are downloaded again

- **Change:** not in notes.
- **Provenance:** `KMod.Steam.MakeCorruptMod`, `MakeModAndReportOutcome`
  (u60:KMod/Steam.cs:21-131); `SteamUGCService.ForceDownloadContent` (`UGCDownloadToLocation`,
  `CallResult<RemoteStorageDownloadUGCResult_t>`), `SwapDownloadedFile` (`File.Replace`)
  (u60:SteamUGCService.cs:166-230); `KMod.Manager.SubscribeCorruptMod`, `OnCorruptModReacquired`.
- **Engine surface:** our Steamworks.NET port (call results, struct layout of
  `RemoteStorageDownloadUGCResult_t`), `File.Replace` on Linux and Windows. **High.**
- **Test:** truncate a subscribed legacy-zip mod's file; start. Observe "Corrupt mod file(s)",
  "ForceDownloadContent completed ... succeeded=True", "SwapDownloadedFile: swapped". Pass: the
  same lines in both players and the mod usable after the restart the game asks for.
- **Harness:** e2e, with SteamStub implementing `UGCDownloadToLocation` and the call result; or a
  manual run on Windows with real Steam.

### M3 Mod API changes

- **Change:** "ModUtil.CreateSubstance now initializes the anims array"; "Fixed asteroid and biome
  remixing mods crashing"; "Added requiredDlcIds to WorldMixingSettings and SubworldMixing Settings".
- **Provenance:** `ModUtil.CreateSubstance` (u60:ModUtil.cs:106), `Substance.Anims`
  (u60:Substance.cs:118); `requiredDlcIds` (fp60:ProcGen/WorldMixingSettings.cs:24,
  fp60:ProcGen/SubworldMixingSettings.cs:28); `ProcGen.SettingsCache.GetRandomTraits`.
- **Engine surface:** none new. **Low.**
- **Test:** the burndown's element-adding and remix mods on U60 (`tests/e2e/results-u60`). Pass:
  played.
- **Harness:** e2e (mod burndown).

### M4 Obsolete game APIs

- **Change:** "EventSystem’s IntraObjectHandler is obsolete"; "IGameObjectEffectDescriptor.GetDescriptors
  is obsolete"; "EquipmentTemplates.CreateEquipmentDef has a new overload"; "Marked Unsubscribe(int,
  Action) obsolete"; "GlobalStringBuilderPool is now obsolete"; harvest tunings.
- **Provenance:** `EventSystem.Subscribe_obsolete`, `Unsubscribe_obsolete` (fp60:EventSystem.cs),
  `GetDescriptors` → `CollectDescriptors` across about 200 types; `TUNING/CROPS.cs`.
- **Engine surface:** Harmony patches by mods on moved methods; our Harmony behaves as Mono's
  (no IL-shape promise for transpilers). **Low:** the burndown covers it per mod.
- **Test:** none beyond the burndown.

## Platform and host

### P1 Crash reporter messages

- **Change:** "Display a message on the crash reporter screen when it detects the operating system
  has blocked loading the sim"; "Display a message suggesting the user verify files or reinstall the
  game if it detects certain crashes".
- **Provenance:** `KCrashReporter.IsOperatingSystemBlockedError` matches
  `"DllNotFoundException: SimDLL"` in the error or the stack trace (u60:KCrashReporter.cs:261-263,
  453-470, new); `CorruptFileErrorPrefixes` "KAnimBatchManager is not ready when loading anim",
  "First anim file needs to be non-null." (u60:KCrashReporter.cs:261); `WorldGen.ReportWorldGenError`
  queues the sim error (u60:ProcGenGame/WorldGen.cs:544-552); `QueueSimDLLLoadError`.
- **Engine surface:** the text of our `DllNotFoundException` and of the log line our player writes
  for it. Mono reports "SimDLL assembly:<unknown assembly> type:<unknown type> member:(null)", so
  `e.ToString()` holds "DllNotFoundException: SimDLL"; CoreCLR reports "Unable to load shared library
  'SimDLL' or one of its dependencies", which does not match. **High:** likely to fail as is.
- **Test:** a test install (never the live one) with `libSimDLL.so` made unreadable; New Game to
  worldgen; and on Windows with `SimDLL.dll` blocked. Pass: the "operating system blocked" popup
  appears in both players, with the same title and the same report queued.
- **Harness:** e2e on a copied install (`out/local-tools/u60orig` pattern); manual on Windows.

### P2 Vulkan as the Linux default

- **Change:** "Vulkan is now the default renderer on Linux. OpenGL can be used using launch options."
- **Provenance:** player settings in the data; no game code. Our selection: ARCHITECTURE §3.5, D54.
- **Engine surface:** renderer selection and fallback. **Medium:** covered on llvmpipe and Mesa
  d3d12; real Linux hardware is not available here.
- **Test:** the launch options Unity's player takes (`-force-opengl`, `-force-vulkan`) on ours:
  each picks that renderer; no option gives Vulkan. QA dogfood once per renderer. Pass: the log
  names the renderer as the original's does; captures match.
- **Harness:** qa dogfood; manual on a real GPU.

### P3 Linux restarter

- **Change:** "Fixed Linux restarter not working."
- **Provenance:** `App.Restart` unchanged; the fix is in Klei's `Restarter` binary (12.5 MB → 2.3
  MB). Ours ships `src/OniMoono.Restarter`.
- **Engine surface:** our restarter, argument passing. **Medium.**
- **Test:** `main-menu`: enable a mod and accept the restart; switch language (translation spec's
  restart). Pass: the game comes back with the same arguments and the change applied, in both.
- **Harness:** e2e (`translation-screen.yaml` stops at the dialog; extend it past the restart).

### P4 Unity 6000.3.17f1 data

- **Change:** "Minor unity version update."
- **Provenance:** six native layouts (inventory, "Native data layouts"); `boot.config` changes.
- **Engine surface:** type trees. **Low:** covered (09a00671, `--check-*` modes on Linux and
  Windows U60 data).
- **Test:** `scripts/check.sh`. Pass: asset, bind and shader checks clean.

### P5 IMGUI developer tools

- **Change:** "Fixed IMGUI developer tools crashing on Linux and enabled them for Mac."
- **Provenance:** a new `cimgui.so` exporting `SetKAssertCB`; `DevToolManager`, `DevToolSearchAndFocus`
  (new), `DevToolFuzzy`.
- **Engine surface:** Mesh, CommandBuffer scissors, RenderTexture in a RawImage. **Low:** checked
  pixel for pixel (63c86671, c21392e8).
- **Test:** an e2e of the 2026-11-01 manual check (toggle, menu bar, StringsTable, a scroll,
  typing into its filter) so it stays checked. Pass: shots match.

## Audio

### AU1 UI sounds and stingers

- **Change:** "Fixed 'click' sound selecting storages for the first time"; "Fixed a doubled click
  sound when opening the Research or Skills screens from the Printing Pod sidescreen"; "Added music
  stinger ... Minnow POI"; "Fixed audio missing reverb on new critters"; slider sounds on wheel (I2).
- **Provenance:** `KFMOD` caches event paths by GUID (`eventPathsByGuid`, `PathToEventReference`;
  fp60:KFMOD.cs:59-91, 210-322; fp59:KFMOD.cs:178-228), `GetSoundEventDescription` returns null
  instead of throwing; `GlobalAssets.GetSound`; banks changed.
- **Engine surface:** FMODUnity (shipped) and the native library; ours only passes 3D attributes
  and the listener. **Low.**
- **Test:** `early-base`, `driver: {count: {methods: ["KFMOD:PlayUISound", "KFMOD:BeginOneShot"]}}`
  around: select a storage bin twice; open Research from the Printing Pod side screen. Pass:
  counts `== original`.
- **Harness:** e2e.

## Game logic (lower risk)

Each item is game code with no new engine surface. One spec per item, functional checks only
(`expect_state ... == original`), sandbox setups through `setup.call` where they only prepare.

| Id | Changes | Provenance | Check |
|---|---|---|---|
| G1 | Swimming and Swimming2 in all versions; Aquatic Grooming Station everywhere; Swim Hack booster un-gated | `Database/Skills.cs` u60:335,342 vs u59:340,345 (`DLC5_ID`); `BionicUpgradeComponentConfig` | base-game colony (`vanilla-base`): skill list has Swimming; a Duplicant swims a pool |
| G2 | Study, Workshop, Cooking ("Cuisine" in the notes), Forge Tools; the Support subcategory | `Database/SupportBuildingBoosts.cs:3-16` (new), `SupportBuilding` (42), `SupportBuildingReceptor` (22), `SupportBuildingBoost_*`, `STRINGS/BUILDINGS.cs:42-44` | recipe time with and without a tool in range; Study Tools stacking |
| G3 | Space Racer, Trampoline, Sandpit; recreation durations; recreation preference | `AstronautTrainingCenterConfig.cs` (u60:17-27; u59:19 `Deprecated`), `TrampolineConfig.cs` (new), `SandboxBuildingConfig.cs` (new), `SodaFountainWorkable.cs:30`, `HotTubWorkable.cs:24`, `RecreationWorkable` (13) | effect durations and which building a Duplicant picks |
| G4 | Merpip, Pips eating Caviar, Snow Globe keepsake, Merpip and other Better Together rewards, Tublia care package | `SquirrelAquaticConfig.cs` (new, 15-69), `BaseSquirrelConfig.cs:99-114`, `MinnowImperativePOIStates` (10) | diet and excretion; reward spawns |
| G5 | Smart Storage Bin no power and sliders; Copy Settings on suit docks; Oxylite 3×3; Window Tile and Wicker Door decor; fridge cooling only; drywall and Tempshift Plate behind tiles; Bottle Drainer; Lead Suit from any Glassy; insulated rocket storage; Party Line Phone light; Breathing Station charges (tank 25 → 30 kg); Coquina → Pearl; Deep Fryer without a Kitchen (requirement removed, not only the status) | `StorageLockerSmart.cs:80-82`, `SuitLockerConfig.cs:40`, `OxyliteRefineryConfig.cs:26-29`, `GlassTileConfig.cs:14`, `WoodenDoorConfig.cs:15`, `RefrigeratorController.cs:13-39`, `ExteriorWallConfig.cs:12`, `ThermalBlockConfig.cs:19`, `BottleEmptierConduitLiquidConfig.cs:38`, `SuitFabricatorConfig.cs:122,148`, `RocketInteriorLiquidInputConfig.cs:49`, `TelephoneCallerWorkable.cs:29`, `UnderwaterBreathingStationConfig.cs:10-12`, `Deepfryer` | building defs and behaviour values |
| G6 | Shine Bug temperature; Beetinies; Rhex/Jawbo culling; Pips not planting Starnacle/Pinpoki; Slogo/Gildgo eat and poop, Mucin temperature; condos must be operational; large flyers; Fish Trap crash; Orehull/Jawbo feeders; harvest stops on a bite | `BaseLightBugConfig.cs:13`, `BaseBeeConfig`, `CritterCondoStates.cs:50`, `Growing.cs:121`, `CreatureCalorieMonitor` (17), `CreatureFeeder` (4), `OvercrowdingMonitor` (2) | critter states over a few cycles |
| G7 | Suit checkpoints idling; flee crash; transit tube destroyed; rescued during a save; clapping; double cheer; socialising; Bionic bed rest; Advanced Construction booster (+5 Construction, Heavi-Watt and Steam Turbine perk); rocket lander breath; full-bladder pilots; bionic dance at the edge; starving tooltip order | `FleeChore.cs:85`, `IdleSuitMarkerCellQuery`, `TravelTube` (6), `Database/SkillPerks.cs:311`, `SteamTurbineConfig2.cs:53`, `WireRefinedHighWattageConfig.cs:25`, `MinionResume.cs:51,118`, `NotificationScreen` | chores and states after each setup |
| G8 | Status and string renames: Just Ate → Growing Antlers/Fur/Regal Crest/Feathers, Replenishing Ink, Growing Iron Ore; Well Fed → Replenishing Biodiesel; Wood → Lumber; Stations → Operations; Electrical → Power; Regal Neutronium Pitcher Pump; Engineering and Advanced Construction Booster; Jungle, Rhex, Bammoth, Seaquine descriptions; Flue Coral "Peak Conditions", "plant"; Aquatic Critter Fountain text; checkpoint, Steam Turbine, wire, Fine Dining tooltips; gaskets in units | `STRINGS/CREATURES.cs:4656-4698`, `STRINGS/ITEMS.cs:213,220`, `STRINGS/UI.cs`, `.pot` | one `ui_dump` per string in English and zh, `== original` |
| G9 | Unit-mass items never split; fabricator reserved amounts; Egg Cracker; storage thresholds equality; filter and memorial keep settings on Change Material; Outhouse uses; pressure damage destroys Farm Tile and others; decor of solid-tile buildings; wire max wattage; pipe damage status and "requires building intake"; Lavatory tooltip; filters with undiscovered options; bubbles in Airflow Tiles; Printerceptor; agriculture speed; neutronium under construction; Gleaner without DLC5 | `FetchManager` (4), `ComplexFabricator` (20), `EggCrackerConfig`, `ConduitThresholdSensor`, `Filterable.cs:7,95-100`, `Grave.cs:124-133`, `Toilet` (10), `BuildingHP`, `DecorProvider` (13), `ElectricalUtilityNetwork.GetMaxSafeWattage`, `ConduitDamageFeedback` (new), `BubbleManager` (19), `HijackedHeadquarters.cs:176-181`, `Tinkerable.cs:135`, `MilkFatSeparatorConfig.cs:85` | masses, counts, status items |
| G10 | Start bases (forest lost its Hatch, 18 → 17 wide, cells to Vacuum); Zinc Ore in metal traits; frozen breakroom revealable; mixing shows the right traits; Demolior setting availability; unplaced `labPOI1.yaml`, two critter-enclosure templates | `templates/bases/forestBase.yaml`, `sandstoneBase.yaml`, `worldgen/traits/MetalRich.yaml:35-37`, `frag_breakroom.yaml`, `CustomGameSettingConfigs.cs:104` | new games, functional (never cell by cell) |
| G11 | Sandbox: Pacu Treat in the Spawner, destroy clears falling sand | U9 | covered by U9 |
| G12 | Codex content: critter drops, Food Quality, Applications, Medical Pack link, Power Banks, booster recipes, Refashionator | `CodexEntryGenerator*` | covered by U5's dumps |
| G13 | Spaced Out: logic ports on take-off and landing; Material Study Terminal 100/100; rocket modules rebuilt while paused; equippables same world only; Robo-Pilot signal | `LogicPorts` (9), `HEPFuelTank.OnRocketLaunched/Landed`, `PassengerRocketModule` (11), `Equippable` (8), `RoboPilotModule` | `rockets-so` launches and lands |

## The 761015 → 761211 delta

Plain C# diff of the two betas (`git diff U60-761015 U60-761211`): 13 files; `u761211:` lines are
at tag U60-761211. Of the 136 Managed
DLLs only `Assembly-CSharp.dll` changed (byte comparison of `E:\Development\U60-761015\Managed` and
`U60-761211\Managed`). 761211's StreamingAssets are not on this machine.

- **Build number** 761015 → 761211 in `KleiVersion.ChangeList`, `BuildWatermark`, `Game`
  (changelists played on), `Global`, `KCrashReporter`, `KMod/Mod.cs` (`minimumSupportedBuild <=
  761211`), `LoadScreen` and `MainMenu` (a save from a newer build is refused), `SaveGame` header,
  `PerformanceCaptureMonitor`. Save minor version stays 39.
- **Rail gun and orbital cargo transfers:** the loop that moves mass into a payload now stops when
  `Storage.Transfer` moves nothing (u761211:RailGun.cs:425-440,
  u761211:OrbitalDeployCargoModule.cs:149-163). Before, a storage holding mass it could not transfer
  spun forever on the main thread.
- **CreatureDeliveryPoint:** `deliveryOffsets` from `OffsetGroups.Use` to a single (0, 0) offset
  (u761211:CreatureDeliveryPoint.cs:175).

Tests if we retarget:

- **V1** (medium) build-number dependent paths: a 761211 save refused by 761015 and accepted by
  761211 in both players; a mod with `minimumSupportedBuild: 761211` picked in both; our
  `GameVersionCheck` (Unity version, unchanged if 761211 is still 6000.3.17f1) still lets it start.
  e2e from `main-menu` with fixture saves stamped each way.
- **V2** (low) rail gun with a payload storage of a mass it cannot transfer, and an orbital cargo
  module deploying: no hang (the watchdog), the payload mass `== original`. `rockets-so`.
- **V3** (low) critter drop-off delivers to its own cell: a Critter Drop-Off with a reachable
  neighbour only; `expect_state` on the delivered critter count `== original`.

## Coverage of the patch notes

One row per line of the notes (Features, Changes, Fixes, Modding, Performance). "n/t" = not
testable here, with why.

| Notes line | Test |
|---|---|
| Swimming in all versions; Aquatic Grooming Station in all versions | G1 |
| Pipe Flow sub-overlay | R1 |
| Support buildings: Study, Workshop, Cuisine, Forge Tools | G2 |
| Space Racer | G3, R7 |
| Sandstone, Forest vistas; Swamp (SO); Ice Cave (Frosty) | A3 |
| Garden vista (coming soon) | n/t: commented out in `gardenBase.yaml`, not in `VistaConfigs.VISTAS` |
| Critter and plant subtitles | U5 |
| Tritanopia | R8 |
| Trampoline; Sandpit | G3, R7 |
| Simplified tutorial hiding | U6 |
| Skill Scrubber and trait skills, existing saves | S1, G7 |
| Move To / Relocate To shortcut | I4 |
| Frozen Ovolene art; Neutronium dig icon | R7 |
| Verify files / reinstall message | P1 |
| Shine Bug temperature | G6 |
| Mouse buttons 3-6 | I1 |
| Mouse wheel on sliders | I2 |
| Farming category; Operations rename | U3, G8 |
| Material selection shows missing resources | U3 |
| Blueprint search bar (build menu info screen) | U4 |
| Drywall Rotate button | U4 |
| Duplicant blueprint tab: search, outfit list, Copy Blueprint tool | U4 |
| Smart Storage Bin: no power; sliders | G5, I2 |
| Copy Settings on Atmo, Oxygen Mask, Jet Suit docks; Lead Suit Dock | G5 |
| Automatic Dispenser Sweep Only light | R7 |
| Recreation preference; reduced usage times (5 buildings) | G3 |
| Drywall behind base tiles | G5 |
| Bottle Drainer off-gassing | G5 |
| Oxylite Refinery 3×3 | G5 |
| Window Tile decor; Wicker Door decor | G5 |
| Refrigerators no heating | G5 |
| Metal Refinery output animation | R6 |
| Heat source/sink icons: geysers, Regulator, Aquatuner, Tepidizer, Research Reactor | R8 |
| Pipe damage status item; "Requires Building Intake"; Lavatory tooltip | G9 |
| Backwall requirement text; queueing on under-construction supports | U3 |
| ARM Macs | n/t: no macOS player of ours; ours targets x64 |
| Vulkan default on Linux | P2 |
| Supply Closet screen sizes; deep link; organisation; Power rename; Regal Pitcher Pump | U4, G8 |
| Sandbox: Square shape; Foreground/Background; Critter Removal size; Pacu Treat; Destroy falling | U9, I7 |
| Recreation effect durations shown | U5 |
| Critters/Plants by Type pages; database layouts; large diets; light range | U5 |
| Wood → Lumber; Husky Moo; Jungle description | G8 |
| Asteroid selection arrow; Load Game room | U8 |
| Crash reporter: sim blocked | P1 |
| Recipe side screen height | U5 |
| Starving tooltip order | G7 |
| Research screen info icon | I6 |
| Filters with undiscovered options | G9 |
| Refashionator in the database | U5 |
| Rocket storage insulated; Lead Suits from Glassy; Party Line Phone | G5 |
| Frosty, Prehistoric, Aquatic status and description renames | G8 |
| Bionic Bed Rest; booster changes and renames; Swim Hack booster | G7, G8, G1 |
| Booster and Power Bank database entries | U5 |
| Bionic Stunner swim animations | R7 |
| Rhex and Jawbo culling | G6 |
| Breathing Station charges | G5 |
| Snow Globe keepsake; Merpip reward; Tublia care package | G4 |
| Flue Coral checkbox and "Peak Conditions" | U5, G8 |
| Vulcanizer power-cut animations; Slogo/Gildgo animations | R7 |
| Coquina → Pearl | G5 |
| Minnow music stinger | AU1 |
| Fixes, all versions: unit splitting, cracks, unselectable after fall, Airflow bubbles, agriculture, harvest bite, Linux restarter, pre-SSE3, Printerceptor, drag tool crash | G9, R6, I7, G9, G9, G6, P3, n/t (pre-SSE3: no such machine here; the fix is in Klei's native code), G9, I7 |
| Fixes, Duplicants (9 lines) | G7 |
| Fixes, Critters (3 lines) | G6 |
| Fixes, Buildings: Outhouse, doors on load, Egg Cracker, solid-tile decor, solid-tile tooltip, memorials, grooming freeze, pressure damage, condos, storage thresholds, Rock Crusher output, filter Change Material, tile tops, Carpet gap, Metal Tile preview, Gleaner | G9, S1, G9, G9, I7, G9, R7, G9, G6, G9, R6, G9, R6, R6, R6, G9 |
| Fixes, UI: Enter on rename (2), link tags, database button | I5 |
| Fixes, UI: reserved materials, Throughput label, Suit Wearing category, indentation, Neutronium tooltip, under-construction over Neutronium | G9, U5, U5, U5, U5, G9 |
| Priority toggles double-click | I6 |
| White lines round DLC logos | U8 |
| Click sound on storages; doubled click | AU1 |
| Pixelated under-construction buildings on overlays | R6 |
| Absolute Zero string with colour modes | R8 |
| Tooltips: checkpoints, Steam Turbine, wires, gaskets, pump status, wire type, Fine Dining | G8, G9 |
| Manual Generators in the Supply Closet | U4 |
| Keyboard movement in Research and Skills | I3 |
| Database fixes (7 lines) | U5, G12 |
| Animations (11 lines) | R7 |
| Base game: pilots' bladder | G7 |
| Spaced Out fixes: logic ports, 100/100, Beetinies, breakroom POI, mixing traits, Steam Deck side screen, starmap keyboard, rocket modules while paused, equippables | G13, G13, G6, G10, G10, U5, I3, G13, G13 |
| Frosty: Deep Fryer | G5 |
| Bionic: Robo-Pilot; dance at the edge | G13, G7 |
| Prehistoric: Demolior setting; Jawbo tame tag | G10, R7 |
| Aquatic fixes (16 lines) | G6, R7, AU1 (reverb), G8, G10 (Zinc), G5, G7 (lander breath) |
| Unity 6000.3.5f2 → 6000.3.17f1 | P4, L3 |
| VYaml 1.2 → 1.4; POI templates through VYaml | A1 |
| IMGUI dev tools on Linux; Mac | P5; Mac n/t |
| Mod Uploader 1000-file limit | n/t: the uploader is a separate tool, not the game |
| SteamUGC loose files | M1 (and M2, not in notes) |
| Fewer details in the mod restart dialog | U8 |
| ModUtil.CreateSubstance; remix mods crash; requiredDlcIds | M3 |
| Code changes (KleiStringBuilder, harvest tunings, disease index check and hashes, obsolete APIs, CellChange events) | U1, M4, S1, M4, L1 |
| Memory allocation optimisations | n/t as function; perf stage |
| Incremental GC | n/t: a Unity player setting CoreCLR ignores (inventory, "Incremental GC") |
| Optimisations offsetting incremental GC | perf stage on Windows |
| Asset and POI loading at the main menu | A2 |
| Faster worldgen | A1 |
| Timelapse capture performance | R3 |
| Colony Summary memory | R4 |
| Vista textures on demand | A3 |
| Sweep chores in large bases | perf stage (U57) |
| Power, Temperature, Materials overlay performance | qa overlays, perf |
| Geyser eruption state work | G-level, perf stage |
| Initial save stutter | S2 |
| First open of Priorities, Vitals, Consumables; their scrolling | U2 |
| Memory leaks: pathfinding and loading another save; time lapser; switching OS users; colony images on Load | L1, R3, n/t (OS user switch), R4 |

## Changes the notes do not mention

- The timelapse capture path: AsyncGPUReadback, `EncodeArrayToPNG` on a worker thread, the first
  timelapse frame written by the initial save (R3, S2).
- Colony previews downscaled to 1024 px through `Graphics.Blit` (R4).
- Hidden table screens disabling their RectMask2D, ScrollRect and canvas anim renderers (U2).
- Animation batch pooling (R2); world camera render-target reuse and freeing (R5); codex video
  render textures freed (R9).
- Corrupt Workshop items downloaded again through `UGCDownloadToLocation` and swapped in with
  `File.Replace` (M2).
- Translation strings validated and rejected on mismatched parameters or links (U7).
- FuzzySharp rewritten on spans with culture-sensitive comparison (U4, L2).
- KFMOD's GUID path cache; a missing sound description returns null instead of throwing (AU1).
- `SaveScreen` waits for `ClusterManager.CanSave` before saving (S2).
- The Epic Online Services binding regenerated (2,041 types in firstpass). Not loaded by us on
  Linux (`--check-natives` expects U60's Linux EOS library absent, 3a83f0f9).
- A scripted profiling harness: `ProfilingScript`, `CameraPan`, `GenericGameSettings.ScriptedProfile`,
  `KProfilerBegin.IScriptedProfile`, `PerformanceCaptureMonitor` reading `ProfilerRecorder` (Render
  category) values that report 0 in ours.
- Behind-tiles placement also for Tempshift Plates and decorative backwalls; Deep Fryer's Kitchen
  requirement removed; Breathing Station tank 25 → 30 kg; Lead Suit repair still needs Glass.
- `COSMETIC2_ID` (Steam app 5315880, "Classic Critter Cosmetics Pack") with its bundle mapping, but
  no bundle in the data (A4); 12 preset outfits; build-menu hotkey strings PLAN5–16.
- `Grid.PosToXY`, `Grid.CollectCellsInLine<T>`; `GroundRenderer.RenderAll` removed.
- 761211: the rail gun and orbital cargo transfer-loop fix and the Critter Drop-Off offset.

## Already covered by U60 work

- Native layouts and the version check: 09a00671, 2e862b95 (inventory, "Native data layouts",
  "Version check").
- Development-build assertions and profiler calls as no-ops: 1976a914, 67946a05.
- `TMP_Text.SetCharArray(ReadOnlySpan<char>)`: 3769d2bc, 3336373c (`docs/cleanroom/textmeshpro.md`).
  Open: Unity's handling of unchanged text (U1).
- `Resources.UnloadAsset` and vista reloads: 0e763810, `vista-toggle.yaml` (525299eb).
- Pipe Flow with Pipe Flow Overlay's icons: `pipe-flow.yaml` (0ada81d3).
- Vulkan by default, shipped programs, fallback: ARCHITECTURE §3.5, D54; `--check-shaders` on both
  platforms' U60 data.
- IMGUI dev tools on Linux, pixel for pixel: 63c86671, c21392e8.
- Linux plugin layout (`Plugins/AnyCPU` for `libsteam_api.so`): inventory, "Plugin layout".
- Destroy from OnDestroy, found through the cell change rework: faba323a, ade583bd; Destroy from an
  Awake: a79fbb3e.
- U60's Disease overlay crash: an opt-in fix, 0c07c551.
- Game tweaks reviewed against U60 (fast-anim-batches for `GetCustomShaderParams`, overlay tweaks
  retired, save tweaks hardened): cbc39d1b, c967677e, 83087731, 17e37db1, 1ab2492d; QA verify 0
  wrong.
- `[Debuggable]` ignored as Mono does: 12f05e56 (D56).
- The taller mod-differences dialog in QA: e477137a. IMGUI wheel deltas: cdbafaf3, 2d1ac5b2.
- VYaml at runtime: QA newgame on U60 matched its references (inventory, 2026-10-31); A1 adds the
  value comparison.
- Mods: the U60 burndown in `tests/e2e/results-u60` covers M3 and M4 per mod.

## Order of implementation

1. **P1** crash reporter sim-blocked text: cheap, and likely a real difference.
2. **R3, S2** timelapse and preview capture, with the save path under background-save: every save
   goes through it. Needs the file-to-evidence step.
3. **I2, I5, U2** wheel on sliders, rename with Enter, hidden table screens: event system, TMP
   input and masks, all reachable with the current e2e steps.
4. **R4** Load screen previews.
5. **I1** mouse buttons 3–6: needs XTEST buttons 8–11 in e2e.
6. **M2, M1** Workshop re-download and loose-file mods: need SteamStub UGC support.
7. **R1** vanilla Pipe Flow, **A1** template value dump, **S1** save round trips.
8. The medium items by area (R2, R5–R9, I3, I6, I7, U1, U3–U5, U8, L1, L2, A2, A3, P2, P3), then
   the low ones and the G table.
9. **V1–V3** only if we retarget to 761211.
