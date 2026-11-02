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

## Index

One row per item. G and V items are rows of their sections, not headings.

| Id | Title | Risk | Harness |
|---|---|---|---|
| [R1](#r1-pipe-flow-sub-overlay-vanilla) | Pipe Flow sub-overlay, vanilla | high | e2e |
| [R2](#r2-animation-batch-pooling) | Animation batch pooling | medium | e2e, qa verify |
| [R3](#r3-timelapse-and-colony-preview-capture) | Timelapse and colony preview capture | high | e2e, new tooling |
| [R4](#r4-colony-previews-on-the-load-and-colony-summary-screens) | Colony previews on the Load and Colony Summary screens | high | e2e |
| [R5](#r5-render-targets-of-the-world-camera) | Render targets of the world camera | medium | qa dogfood, perf; e2e |
| [R6](#r6-world-visual-fixes) | World visual fixes | medium | e2e |
| [R7](#r7-new-and-changed-animations-and-sprites) | New and changed animations and sprites | medium | e2e |
| [R8](#r8-temperature-overlay-icons-and-colour-modes) | Temperature overlay icons and colour modes | medium | e2e |
| [R9](#r9-codex-videos) | Codex videos | medium | e2e |
| [I1](#i1-mouse-buttons-3-to-6-as-bindings) | Mouse buttons 3 to 6 as bindings | high | e2e, new tooling; manual on Windows |
| [I2](#i2-mouse-wheel-on-sliders) | Mouse wheel on sliders | high | e2e |
| [I3](#i3-keyboard-panning-at-any-frame-rate) | Keyboard panning at any frame rate | medium | e2e |
| [I4](#i4-move-to-hotkey) | Move To hotkey | low | e2e |
| [I5](#i5-renaming-with-enter) | Renaming with Enter | high | e2e |
| [I6](#i6-clicks-on-priority-toggles-and-research-icons) | Clicks on priority toggles and research icons | medium | e2e |
| [I7](#i7-the-pointer-in-the-world) | The pointer in the world | medium | e2e |
| [U1](#u1-the-text-pipeline) | The text pipeline | medium | UnityProbe, e2e, qa dogfood |
| [U2](#u2-hidden-table-screens-disable-their-masks-and-scroll-views) | Hidden table screens disable their masks and scroll views | high | e2e |
| [U3](#u3-build-menu-and-material-selection) | Build menu and material selection | medium | e2e |
| [U4](#u4-blueprints-supply-closet-and-fuzzy-search) | Blueprints, Supply Closet and fuzzy search | medium | e2e |
| [U5](#u5-details-screen-and-codex-layouts) | Details screen and codex layouts | medium | e2e |
| [U6](#u6-tutorial-hiding) | Tutorial hiding | low | e2e |
| [U7](#u7-translation-validation) | Translation validation | low | e2e |
| [U8](#u8-front-end-screens) | Front-end screens | medium | e2e |
| [U9](#u9-sandbox-tool-menu) | Sandbox tool menu | low | e2e |
| [L1](#l1-cell-change-tracking-and-kobject) | Cell change tracking and KObject | medium | qa verify, e2e |
| [L2](#l2-new-bcl-calls) | New BCL calls | medium | unit, UnityProbe |
| [L3](#l3-development-build-calls-and-new-engine-members) | Development-build calls and new engine members | low | qa unit |
| [A1](#a1-templates-through-vyaml-14) | Templates through VYaml 1.4 | medium | new tooling; e2e |
| [A2](#a2-asset-loading-at-the-main-menu) | Asset loading at the main menu | medium | e2e, start-up timing |
| [A3](#a3-starting-biome-vistas) | Starting-biome vistas | medium | e2e |
| [A4](#a4-data-files) | Data files | low | e2e |
| [S1](#s1-save-format-739-and-u59-saves) | Save format 7.39 and U59 saves | medium | e2e, qa saveload |
| [S2](#s2-saving-while-screenshots-are-written) | Saving while screenshots are written | high | e2e |
| [M1](#m1-loose-file-workshop-mods) | Loose-file Workshop mods | high | e2e, new tooling |
| [M2](#m2-corrupt-workshop-items-are-downloaded-again) | Corrupt Workshop items are downloaded again | high | e2e, new tooling; or manual on Windows |
| [M3](#m3-mod-api-changes) | Mod API changes | low | e2e (mod burndown) |
| [M4](#m4-obsolete-game-apis) | Obsolete game APIs | low | none beyond the burndown |
| [P1](#p1-crash-reporter-messages) | Crash reporter messages | high | e2e on a copied install; manual on Windows |
| [P2](#p2-vulkan-as-the-linux-default) | Vulkan as the Linux default | medium | qa dogfood; manual on a real GPU |
| [P3](#p3-linux-restarter) | Linux restarter | medium | e2e |
| [P4](#p4-unity-6000317f1-data) | Unity 6000.3.17f1 data | low | check.sh |
| [P5](#p5-imgui-developer-tools) | IMGUI developer tools | low | e2e |
| [AU1](#au1-ui-sounds-and-stingers) | UI sounds and stingers | low | e2e |
| [G1](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G2](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G3](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G4](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G5](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G6](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G7](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G8](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G9](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G10](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G11](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G12](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [G13](#game-logic-lower-risk) | game logic (table) | low | e2e |
| [V1](#the-761015--761211-delta) | 761211: build-number dependent paths | medium | e2e |
| [V2](#the-761015--761211-delta) | 761211: rail gun and orbital cargo transfers | low | e2e |
| [V3](#the-761015--761211-delta) | 761211: critter drop-off offset | low | e2e |

## Rendering

### R1 Pipe Flow sub-overlay, vanilla

**Status:** passes (7e8980e0, Linux OpenGL; Windows Vulkan: pending).
`tests/e2e/u60/r1-pipe-flow-vanilla.yaml`: Ventilation and Plumbing with the sub-overlay off
(0.1 % to 0.3 % of pixels apart) and on; `IsViewConduitFlowEnabled` true in both, no new log
signatures. The flow shots differ by the arrows' phase only (the diff images show the arrows
alone), 0.4 % to 6.5 %, so they allow 8 %, not the 4 % this item named. The blocked-pipe close-up
(step 4) is not in the spec: DeCrew has no disabled pipe output found yet.

**Risk: high.** The existing spec runs it under a mod's icons only.

**Patch note:** "Added a Pipe Flow sub-overlay to Plumbing and Ventilation Overlays."

**What changed:**

| | U59 | U60 |
|---|---|---|
| Batch material types | eight (`new Material[8]`) | a ninth, `MaterialType.Conduit` → shader `Klei/BatchedAnimationConduit` (`materials = new Material[9]`) |

Also changed: `ConduitFlowKAnimVisualizer.cs` (new); the `OverlayModes.ConduitMode` flow toggle;
strings `STRINGS/UI.cs` CONDUITFLOW.

**Where:**
- `fp60:KAnimBatchGroup.cs:179, 237, 291`; U59: `fp59:KAnimBatchGroup.cs:283`
- `u60:ConduitFlowKAnimVisualizer.cs:4` on (new)
- `u60:OverlayModes.cs:78-240`

**Engine surface:**
- a shader new to the game, run from Unity's shipped Vulkan programs and our GLSL path
- the instance-data texture carrying a uint read back with `floatBitsToUint`
- animated by unscaled time

**Test:**
- Setup: `decrew`, Spaced Out; under `--renderer opengl` and again under Vulkan.
- Steps:
  1. F7, shot with the sub-overlay off.
  2. Toggle Pipe Flow, shot.
  3. F6, the same.
  4. A blocked pipe (a liquid pipe with its output building disabled) close up.
- Pass:
  - world shots within the spec's threshold (the arrows' phase differs, so mask nothing but
    allow 4 % as `pipe-flow.yaml` does);
  - `IsViewConduitFlowEnabled` true in both;
  - no new log signatures.

**Harness:** e2e. Copy `pipe-flow.yaml` without `mods:` as `pipe-flow-vanilla.yaml`.

### R2 Animation batch pooling

**Risk: medium.**

**Patch note:** not in the notes.

**What changed:**

| | U59 | U60 |
|---|---|---|
| Batches | always new | `KAnimBatchGroup.GetOrCreateBatch`/`ReleaseBatch` and a batch pool |
| Reuse | — | `KAnimBatch.Reuse` and `AtlasList.Reset` |
| Registration | — | `KAnimBatchManager.IsCorrectlyRegistered` |
| Names | material, mesh and texture names | dropped |

**Where:**
- `fp60:KAnimBatchGroup.cs:453-470` (the pool)
- `fp60:KAnimBatch.cs`
- U59: no counterpart

**Engine surface:**
- a reused batch keeps its Material, Mesh and instance texture: our material property state,
  mesh reuse and texture rebinding must not leak the previous batch's atlas
- fast-anim-batches writes into these batches

**Test:**
- Setup: `early-base`, sandbox on.
- Steps:
  1. Build and deconstruct 40 mixed buildings over several `run` steps, so batches empty and are
     reused.
  2. Build a different set in the same area.
  3. World shot of the area after each round.
  4. QA verify (fast-anim-batches verify mode).
- Pass:
  - shots match;
  - verify reports 0 wrong.

**Harness:** e2e (new spec) and qa verify.

### R3 Timelapse and colony preview capture

**Status:** passes (a59fc749, Linux OpenGL; Windows Vulkan: pending).
`tests/e2e/u60/r3-timelapse-capture.yaml` (with the new `file_shot` step): a Save As preview,
1272x808 in both, 0.6 % of pixels differ. The autosave preview at a new cycle is S2's. The
timelapse frame proper was not written in the original within 330 s of a new cycle on this
harness, so it is not compared.

**Risk: high.** Every save writes a preview through this path.

**Patch note:** "Improved timelapse capture performance." The notes don't say how.

**What changed:**

| | U59 | U60 |
|---|---|---|
| Read-back | ARGB32 `Texture2D.ReadPixels` on the main thread | `AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, cb)` when `SystemInfo.supportsAsyncGPUReadback`, else `ReadPixels` |
| PNG encoding | `EncodeToPNG` on the main thread | `ImageConversion.EncodeArrayToPNG` in `Task.Run` |
| Clean-up | nothing to wait for | `AsyncGPUReadback.WaitAllRequests`, then `Task.WaitAll` |
| Colony preview | separate | `SaveColonyPreview(filename, isInitialSave)` also writes the first timelapse frame |
| Ground | `GroundRenderer.RenderAll` | removed |

**Where:**
- `u60:Timelapser.cs:314-430` (`Timelapser.CapturePng`, `ReadPixelsSync`, `QueueEncodeAndWrite`,
  `FlushPendingScreenshots`); U59: `u59:Timelapser.cs:290-301`
- `u60:SaveLoader.cs:930`
- `u59:GroundRenderer.cs:511`

**Engine surface:**
- AsyncGPUReadback on both renderers: row order, RGBA byte order, and when the callback runs
- `NativeArray<byte>.ToArray`
- `EncodeArrayToPNG` called from a worker thread
- `Debug.LogWarning` from that thread

**Test:**
- Setup: `early-base`, once with OpenGL and once with Vulkan.
- Steps:
  1. Save from the pause menu as a new file.
  2. `run` to the next cycle, so the timelapse writes a frame.
  3. Read the colony's `.png` preview and the timelapse frame from the run's profile.
- Pass:
  - same width and height as the original's, not flipped, channels in order;
  - pixel difference within the `world_shot` threshold;
  - no "GPU readback failed" or "failed to write screenshot" line in the log.

**Harness:** e2e, with new tooling: a step that copies a profile file into the evidence and
compares it as an image (`qa-tools.py image` already compares two PNGs).

### R4 Colony previews on the Load and Colony Summary screens

**Status:** fails in ours: repro `tests/e2e/u60/r4-load-previews.yaml` (Colony Summaries dump), not
fixed because the one difference left is a KScrollRect's horizontal normalizedPosition, 1 in ours
and 0 in the original, its content's left edge on the viewport's (the sub-ulp case of
known-findings' `scroll-normalized-position`, whose inputs are not traced here). The item's own
criterion passes (816dde15, Linux OpenGL; Windows Vulkan: pending): the 2176x1536 previews of two
saves on the Load screen, before and after closing it five times, within 0.2 %. The spec also
found an achievement's `<sup>` text 6 px taller in ours, fixed in 816dde15. Texture memory over
the reopenings was not logged (ONI_TEXTURE_MEMORY_LOG is ours only).

**Risk: high.** Every Load screen shows one.

**Patch notes:**
- "Fixed memory leaks loading colony images on the Load Game screen"
- "Improved memory usage on Colony Summary screen"

**What changed:**

| | U59 | U60 |
|---|---|---|
| Previews over 1024 px | the loaded texture used as is | drawn by `Graphics.Blit` (bilinear) into a temporary ARGB32 render texture |
| Read-back | — | `ReadPixels` into RGBA32, `Apply(false, true)`, the source destroyed |

Also changed:
- `Sprite.Create(..., SpriteMeshType.FullRect)`
- `LoadScreen.ReleasePreviewSprite`, `DestroyPreviewSprite`
- `Slideshow.DestroyCurrentSlideImage`, `RetiredColonyInfoScreen.OnForcedCleanUp`

**Where:**
- `u60:RetireColonyUtility.cs:474-508` (`RetireColonyUtility.LoadPreviewTexture`, `DownscaleToFit`,
  `CreatePreviewSprite`); U59: `u59:RetireColonyUtility.cs:452-520`
- `u60:LoadScreen.cs:1238-1276`

**Engine surface:**
- `Graphics.Blit` scaling with bilinear filtering
- `RenderTexture.GetTemporary`, and `ReadPixels` from it
- `makeNoLongerReadable`
- Sprite FullRect meshes in uGUI Image
- texture destruction while sprites are shown

**Test:**
- Setup: `main-menu` with two saves whose previews are wider than 1024 (the default timelapse
  resolution is).
- Steps:
  1. Load Game, select each colony, shot the preview.
  2. Close and reopen five times.
  3. Open a retired colony's summary slideshow.
- Pass:
  - shots match within 2 %;
  - texture memory (`ONI_TEXTURE_MEMORY_LOG`) does not grow across the reopenings.

**Harness:** e2e (new spec, `from: main-menu`, the saves placed by setup).

### R5 Render targets of the world camera

**Status:** passes (5b239dee, Linux OpenGL; Windows Vulkan: pending), the e2e part:
`tests/e2e/u60/r5-overlay-targets.yaml` toggles the Temperature overlay ten times, shoots it on and
off, loads the colony again and shoots it on (0.1 % to 2.6 % of pixels apart, no new log
signatures). Window resize stays with the qa `resize` scenario.

**Risk: medium.**

**Patch note:** not in the notes.

**What changed:**

| | U59 | U60 |
|---|---|---|
| Copies on resize | new copies on every resize | reused when size, format and depth match |
| Freeing | never freed | the third target and its copy destroyed when the coloured overlay buffer is off; all destroyed with the camera |

**Where:**
- `u60:MultipleRenderTargetProxy.cs:39-120` (`MultipleRenderTargetProxy.RecreateCopyRT`,
  `DestroyRT`, `OnDestroy`); U59: `u59:MultipleRenderTargetProxy.cs:57`

**Engine surface:**
- `Camera.SetTargetBuffers` with two, then three colour buffers
- render texture destruction while bound
- resize

**Test:**
- Setup: the `resize` and `overlays` QA scenarios as they are, plus an e2e.
- Steps (the e2e):
  1. Toggle an overlay using the coloured buffer on and off ten times.
  2. Resize.
  3. Load a second save.
- Pass:
  - captures match references;
  - the perf stage's "multi-target framebuffers not piling up" check holds.

**Harness:** qa dogfood and perf; e2e for the toggle loop.

### R6 World visual fixes

**Risk: medium.** Visible, but driven by game code we run unchanged.

**Patch notes:**
- "Fixed tops of tiles not rendering when there are wall tiles on either side"
- "Carpet Tiles should no longer have a visual gap"
- "Metal Tiles should now show accurate preview art when under construction"
- "Fixed Buried Object cracks sometimes being left behind"
- "under-construction buildings looked pixelated on some overlays"
- "gas and liquid vents would sometimes appear open when disabled by automation"
- "Rock Crusher not displaying the output item"
- "Metal Refinery now has an animation when outputting a product"

**What changed:**
- `TileVisualizer` refreshes the four diagonal cells (absent in U59).
- `AnimTilableSingleController` gains `partitionerUpdateRange`, a preview `Update` that re-tiles on
  cell change, and `requireNeightbourCellToBeCellOfInterest`.
- `VentController.IsClosed` and `off.EnterTransition(closed, ...)`.
- `MetalRefineryConfig.DoPostConfigurePreview`, `RockCrusherConfig`.
- The overlay pixelation and the carpet gap have no code change found: data (materials or anims).

**Where:**
- `u60:TileVisualizer.cs:32`
- `u60:VentController.cs:66-84`

**Engine surface:**
- block tile meshes
- kanim symbol visibility
- overlay material sampling

**Test:**
- Setup: `early-base`, sandbox.
- Steps: one `world_shot` per case:
  1. a tile row with drywall each side;
  2. three carpet tiles;
  3. a metal tile blueprint;
  4. a buried object dug out;
  5. a building under construction in the Power and Decor overlays;
  6. a gas vent with automation red;
  7. Rock Crusher and Metal Refinery mid-output.
- Pass: each shot matches.

**Harness:** e2e (new spec).

### R7 New and changed animations and sprites

**Risk: medium.**

**Patch notes:** the "Animations" list (grow-up anims, grooming calls, Pipsqueak sleep, Drecko egg,
Seaquine, salt shaker layering, belt symbol, Beach Chair pop, Pacu on the Aquatic Grooming Station,
making a mess, cheering, swim waving); "Added artwork for Frozen Ovolene"; "Updated the icon ...
Neutronium"; Vulcanizer power-cut anims; Slogo and Gildgo anims and icons; Jawbo tame tag; Pinpoki
layering; Glo Squid face spots; Beakon Fry breathing; Bionic stunner swim anims; Sweep Only light;
the new buildings' and Merpip's anims.

**What changed:**
- asset bundles (`dlc2..dlc5_bundle`, `expansion1_bundle`, `hires_*`) and `sharedassets`
- code only where layering moved (e.g. Pinpoki `SceneLayer`)
- `Assets.LoadAnims` changed

**Where:**
- `u60:Assets.cs:237-266`

**Engine surface:**
- our bundle reader, and kanim build/anim texture upload for files new to the data
- symbol overrides
- z-ordering of scene layers

**Test:**
- Setup: sandbox spawner on `landing` with all DLCs.
- Steps:
  1. Spawn each new or changed critter and building.
  2. Play the named anim where a state can be forced (`setup.call` may prepare, not perform).
  3. `world_shot` each.
- Pass:
  - shots match;
  - no "anim not found" or symbol warnings in ours only.

**Harness:** e2e (new spec, several shots per run).

### R8 Temperature overlay icons and colour modes

**Risk: medium.**

**Patch notes:**
- heat source/sink icons for geysers, Thermo Regulator, Aquatuner, Liquid Tepidizer, Research
  Reactor
- "Added Tritanopia colorblind support"
- "Fixed Absolute Zero broken string on Temperature overlay when a colorblind mode is enabled"

**What changed:**
- The heat icons, in the configs below.
- `GraphicsOptionsScreen.GetColorModeIdFromPrefs`, `ToLegacyColorModeId`, `ColorModeKeyV2`. The
  colour set count comes from `GlobalAssets.colorSetOptions`, an asset.
- `SimDebugView.PrepareDiseaseColours`, `get_TemperatureColors`.
- "Tritanopia" itself was already in U59.

**Where:**
- `GeyserGenericConfig.cs:196,204`, `AirConditionerConfig.cs:48`, `LiquidHeaterConfig.cs:54`,
  `NuclearReactorConfig.cs:138` (U60 lines, from the baseline and confirmed in the method list)
- `u60:GraphicsOptionsScreen.cs:280-318`
- `u60:SimDebugView.cs:466-848`
- `u59:STRINGS/UI.cs:4250` (Tritanopia)

**Engine surface:**
- overlay icon sprites
- KPlayerPrefs
- the colour-set asset read by our serializer

**Test:**
- Setup: `decrew`.
- Steps:
  1. Options > Graphics: `ui_dump` the colour mode dropdown (its entries).
  2. Pick each mode, F3, `world_shot` over a geyser and an Aquatuner.
  3. Check that `KPlayerPrefs` keys `ColorModeKeyV2` and the legacy one hold the same values as
     the original's.
- Pass:
  - dumps and shots match;
  - `== original` on both prefs.

**Harness:** e2e (new spec).

### R9 Codex videos

**Risk: medium.**

**Patch note:** not in the notes.

**What changed:**
- `VideoWidget.ReleaseRenderTexture` and `VideoScreen` changes: the render texture is destroyed
  when the widget goes.

**Where:**
- `u60:VideoWidget.cs:56-79`

**Engine surface:**
- our VideoPlayer
- a RawImage showing a destroyed render texture

**Test:**
- Setup: `early-base`.
- Steps:
  1. Open a codex entry with a video, wait two seconds.
  2. Navigate to another entry and back, three times. Shot each time.
- Pass:
  - shots match;
  - no error lines.

**Harness:** e2e.

## Input and the event system

### I1 Mouse buttons 3 to 6 as bindings

**Risk: high.**

**Patch note:** "Allowed key binding of mouse buttons 3-6."

**What changed:**

| | U59 | U60 |
|---|---|---|
| `InputBindingsScreen.validKeys` | 111 entries | 115: adds `KeyCode.Mouse3`–`Mouse6` |
| `KInputController` actions | 280 | 281 |

While rebinding, the screen polls `Input.GetKey`/`GetKeyDown` for the new buttons.

**Where:**
- `u60:InputBindingsScreen.cs:52, 165-168`; U59: `u59:InputBindingsScreen.cs:52`
- `fp60:KInputController.cs:41, 103`

**Engine surface:**
- Our input layer: GLFW reports X11 buttons 8–11 as its buttons 3–6, which `InputStateFeed` maps
  to `KeyCode.Mouse3`–`Mouse6` (`GlfwPlatform.cs`).
- Unmeasured: whether Unity's X11 and Windows players number the side buttons the same way.
- Also whether Unity sends IMGUI mouse events for them.

**Test:**
- Setup: `early-base`.
- Steps:
  1. Options > Controls: rebind an action (e.g. Dig) to the back side button (X11 button 8),
     then to button 9.
  2. Press it in the world.
  3. A KeyLog run records `Input.GetKey` per button 8–11.
- Pass:
  - the binding label reads the same as the original's (`ui_dump`);
  - the tool activates;
  - the KeyLog matches per button.

**Harness:** e2e with new tooling: `click` and `key` accept X buttons 8–11 (XTEST sends any
button number). Windows needs a separate manual check with a five-button mouse.

### I2 Mouse wheel on sliders

**Status:** passes (436e1fce, Linux OpenGL; Windows: pending). `tests/e2e/u60/i2-slider-wheel.yaml`:
all 15 values, the side screen's scroll position and the 21 move sounds equal the original's.

**Risk: high.**

**Patch note:** "The mouse wheel can now be used to adjust sliders."

**What changed:**

| | U59 | U60 |
|---|---|---|
| Scroll handling | none | `KSlider : IScrollHandler`, `OnScroll` with a 16-step table and `MoveSource.ScrollWheel` |

`Slider.wholeNumbers` is read for the first time by game code.

**Where:**
- `fp60:KSlider.cs:46, 125-175`; U59: `fp59:KSlider.cs` has neither

**Engine surface:**
- our uGUI event system: `ExecuteEvents.GetEventHandler<IScrollHandler>` picks the slider before an
  enclosing ScrollRect
- `PointerEventData.scrollDelta` sign and size per notch
- `IsActive`, `IsInteractable`
- the slider's move sound

**Test:**
- Setup: `early-base`, sandbox, with three sliders: a Smart Storage Bin (its new threshold
  sliders), a Temperature Sensor side screen slider inside the details screen's scroll view, and a
  whole-number slider (Timer Sensor).
- Steps:
  1. Scroll +1, +3, −2 notches over each handle.
- Pass:
  - slider values `== original` after each step;
  - the side screen's scroll position unchanged;
  - `KSlider:PlayMoveSound` call count equal (`driver: count`).

**Harness:** e2e (new spec).

### I3 Keyboard panning at any frame rate

**Risk: medium.**

**Patch notes:**
- "Using the keyboard to move in the Research Screen and the Skills Screen should now be more
  consistent across different frame rates"
- the same for the Spaced Out Starmap

**What changed:**

| | U59 | U60 |
|---|---|---|
| `KScrollRect` keyboard scroll | per frame | scaled by `60f * Time.unscaledDeltaTime` |
| `ResearchScreen` pan delta | without the factor | with it |

ClusterMapScreen has only compiler-shape changes; its fix is elsewhere or in KScrollRect.

**Where:**
- `fp60:KScrollRect.cs:188-208`; U59: `fp59:KScrollRect.cs:186-206`
- `u60:ResearchScreen.cs:226`; U59: `u59:ResearchScreen.cs:223`

**Engine surface:**
- `Time.unscaledDeltaTime`
- our frame rate differs from the original's, so the per-frame distance does too; the per-second
  distance should not

**Test:**
- Setup: `early-base`; the starmap on `landing`. Run each player twice, once with a frame cap at
  the original's measured rate.
- Steps:
  1. Research screen: hold the pan-right key 2 s of real time, read the content's anchored
     position.
  2. The same on the Skills screen and the starmap.
- Pass:
  - with the cap, displacement `== original` within 5 %;
  - without it, within 15 % (the delta is applied twice, so it is not fully rate-independent).

**Harness:** e2e. The cap is `Application.targetFrameRate` set in `setup:` (it prepares the run,
so `setup.call` may do it); check it holds in both players before relying on it.

### I4 Move To hotkey

**Risk: low.**

**Patch note:** "Added keyboard shortcut for “Move To” and “Relocate To”."

**What changed:**
- `UserMenuScreen.OnKeyDown` consumes `Action.BuildMenuKeyQ` for a selected `Movable` or a
  Duplicant's `MoveToLocationMonitor` (new).

**Where:**
- `u60:UserMenuScreen.cs:182-201`

**Engine surface:**
- key events through KInputController

**Test:**
- Setup: `early-base`.
- Steps:
  1. Select debris, press Q, click a cell.
  2. Select a Duplicant, press Q.
- Pass:
  - `MoveToLocationTool` active in both (`expect_state`);
  - the debris gets a move chore.

**Harness:** e2e.

### I5 Renaming with Enter

**Status:** passes (4b7ee572, Linux OpenGL; Windows: pending). `tests/e2e/u60/i5-rename-enter.yaml`:
three Enter renames of the Ration Box (one emptied) keep it enabled, names, title bar dumps and
the codex article equal the original's.

**Risk: high.** Key-event order is ours.

**Patch notes:**
- "Fixed pressing Enter to confirm a building rename also toggling Enable Building"
- "pressing Enter after deleting the text preventing the building from being renamed again"
- "link tags should no longer be editable"
- "the 'View full entry in Database' button should still work"

**What changed:**
- `EditableTitleBar.OnEndEdit` strips link formatting and re-enables the edit button after ten
  frames. The input field gets the stripped title.
- `BuildingEnabledButton` moved to the new subscribe API.
- The Enter double handling has no other change found. It depends on which handler consumes
  Return first.

**Where:**
- `u60:EditableTitleBar.cs:58-81, 154`; U59: `u59:EditableTitleBar.cs:58-80`

**Engine surface:**
- our `TMP_InputField` (submit, end edit, deselect order)
- the frame Return reaches KInputController after the field ends editing
- coroutines with `WaitForEndOfFrame`

**Test:**
- Setup: `early-base`.
- Steps:
  1. Select the Ration Box, rename to "e2e" with Enter; check the building is still enabled.
  2. Rename again, clear the text, Enter.
  3. Rename a third time.
  4. Open a building whose name has a link, edit.
  5. Click "View full entry in Database" after renaming.
- Pass:
  - names, enabled state and the codex article `== original`;
  - `ui_dump` of the title bar matches.

**Harness:** e2e (new spec).

### I6 Clicks on priority toggles and research icons

**Risk: medium.**

**Patch notes:**
- "Priority setting toggles now remain visually enabled when double-clicked"
- "Hovering over an item on the Research screen now displays an icon which can be clicked to open
  the item's database entry."

**What changed:**
- `PriorityButton` sets `toggle.isOn = true` (a new line).
- `ResearchEntryIcon`, a new class: `OnPointerEnter`, `OnPointerExit`, `OnInfoButtonClick`.

**Where:**
- `u60:PriorityButton.cs:58`
- `u60:ResearchEntryIcon.cs:16-42`

**Engine surface:**
- double-click counting and toggle groups in our event system
- pointer enter and exit on small nested targets

**Test:**
- Setup: `early-base`.
- Steps:
  1. Double-click priority 7 in the priority screen and in a building's side screen; `ui_dump`
     the group.
  2. Research screen: hover an unlock icon, shot.
  3. Click its info icon.
- Pass:
  - dumps match;
  - the codex opens on the same article.

**Harness:** e2e.

### I7 The pointer in the world

**Risk: medium.**

**Patch notes:**
- "Potential fix for objects that become unselectable after they fall"
- "Buildings that have solid tiles ... should no longer display an element tooltip"
- "Fixed a crash that could occur using drag tools such as the Dig tool"
- "Added Square shape option to most sandbox tools"

**What changed:**
- `InterfaceTool.GetObjectUnderCursor`, `UpdateHoverElements` (sorting removed)
- `KCollider2D` (4 methods), `SelectToolHoverTextCard`, `DigTool.PlaceDig`
- `BrushTool.VisitBrushOffsets`, `SupportsNonCircleBrushShapes`

**Where:**
- `u60:BrushTool.cs:81-126`

**Engine surface:**
- hover and selection under the cursor
- drag input across cells

**Test:**
- Setup: `early-base`, sandbox.
- Steps:
  1. Drop debris down a shaft and select it.
  2. Hover the solid tile of a Solar Panel.
  3. Drag-dig a large rectangle, then cancel mid-drag with Escape.
  4. Use the square sandbox brush.
- Pass:
  - `ui_dump` of the hover card matches;
  - selections and dig orders `== original`.

**Harness:** e2e.

## UI, text and layout

### U1 The text pipeline

**Risk: medium.** Implemented and run by every QA scenario; one open check (below).

**Patch note:** "Added KleiStringBuilder to reduce string allocations" (Modding).

**What changed:**
- `LocText.SetContent`, `ParseText`, `ManifestStyledLinks`, `NeedsLinkStyling` (new) call
  `TMP_Text.SetCharArray(ReadOnlySpan<char>)`.
- `KleiStringBuilder` (new) formats with `Int32/Int64.TryFormat` into spans.
- `HoverTextDrawer` reuses widgets.

**Where:**
- `u60:LocText.cs:211-228`
- `fp60:KleiStringBuilder.cs` (new); the formatting at `fp60:KleiStringBuilder.cs:344-378`

**Engine surface:**
- our TMP (SetCharArray, link parsing)
- number formatting in the current culture

**Test:**
- Setup: the open check from the inventory: whether Unity's TMP skips an unchanged
  `SetCharArray` (layout rebuild, `OnPreRenderText` events).
- Steps:
  1. Measure it in the original with UnityProbe.
  2. An e2e sets a hover card's text to the same value on consecutive frames and counts layout
     rebuilds.
  3. Run the QA dogfood with `LANG=ru_RU.UTF-8` and `de_DE.UTF-8`.
- Pass:
  - rebuild counts equal;
  - dogfood captures match the English references' layout.

**Harness:** UnityProbe, e2e, qa dogfood.

### U2 Hidden table screens disable their masks and scroll views

**Status:** fixed in 90f04f7c (Linux OpenGL; Windows: pending). `tests/e2e/u60/u2-hidden-table-screens.yaml`
failed in ours on All Diagnostics: its value labels (wrapped, auto-sized 8 to 14) took 8 pt and one
line where the original took 14. Repro `u2-diagnostics-autosize.yaml`; measured with UnityProbe
`autosizepref`: a wrapped auto-sized text's preferred height ignores the rect's height. Both specs
now play; the hidden screens' scroll views go off and on as in the original, dumps match, shots
within 1.5 %. The table screens are hidden, not deactivated (alpha 0), and the original scrolls
Consumables back to the left when it shows again.

**Risk: high.**

**Patch notes:**
- "Significantly improved scrolling performance on the Consumables and Priorities screens"
- "Fixed performance overhead incurred from opening the Priorities, Vitals, or Consumables screen
  for the first time."

**What changed:**
- `ShowOptimizedKScreen.SetHiddenBehavioursEnabled` and `DisableEnabledInChildren<T>` for
  `RectMask2D`, `ScrollRect`, `KBatchedAnimCanvasRenderer` (new).
- Used by `TableScreen`, `AllResourcesScreen`, `AllDiagnosticsScreen`.

**Where:**
- `fp60:ShowOptimizedKScreen.cs:35-76`

**Engine surface:**
- `Behaviour.enabled` toggles on masks and scroll views in inactive trees
- RectMask2D unregistering and re-registering its clippables
- our canvas mask skips, and the UI walk that skips hidden screens (1eb657c9)

**Test:**
- Setup: `early-base` with the foods discovered as in `consumables-scroll.yaml`.
- Steps (`ui_dump` and shot after each open):
  1. Open Consumables, scroll, close.
  2. Open Vitals, Priorities, back to Consumables.
  3. Open All Resources and Diagnostics.
- Pass:
  - dumps match (rect masks, scroll positions, row clipping);
  - portraits drawn;
  - no rows outside the viewport.

**Harness:** e2e (extend `consumables-scroll.yaml` or a new spec).

### U3 Build menu and material selection

**Risk: medium.**

**Patch notes:**
- "Added the Farming build menu category"
- "Stations ... renamed to Operations"
- "Building material selection now shows missing resources, including undiscovered resources, and
  hints at which research needs to be completed"
- "Buildings which must be built against a backwall now mention this"
- "Buildings that have special placement requirements can be queued up"
- "Support" subcategory

**What changed:**
- `PlanScreen` (44 methods; `TryGetSubCategoryTitle`, `GetSubCategoryGrid`, `String.CompareOrdinal`)
- `MaterialSelectionPanel` (18), `MaterialSelector` (32), `ProductInfoScreen` (11),
  `BuildingDef` (18)
- `TUNING/BUILDINGS.cs` plan categories

**Where:**
- `u60:PlanScreen.cs:1328` (`String.CompareOrdinal`)

**Engine surface:**
- GridLayoutGroup and toggle layout
- TMP wrapping of the new hint lines

**Test:**
- Setup: `early-base`.
- Steps:
  1. Open each category, `ui_dump` the plan screen.
  2. Open a building whose material is undiscovered and one needing a backwall; `ui_dump` the
     product info and material panel.
  3. Queue a building on an under-construction foundation.
- Pass:
  - dumps match;
  - the queued blueprint exists in both.

**Harness:** e2e (extend `build-menu-search.yaml`'s pattern).

### U4 Blueprints, Supply Closet and fuzzy search

**Risk: medium.**

**Patch notes:** blueprint search bars (building info screen, Duplicant tab), outfit blueprints
near the top, Copy Blueprint tool, drywall Rotate button, Supply Closet layouts, "See more
Blueprints" deep link, Electrical → Power, the Regal pump rename, Manual Generator look.

**What changed:**
- `Database.BlueprintSearch` (new)
- `FuzzySearch`
- FuzzySharp rewritten on spans with `CultureInfo.CurrentCulture.CompareInfo`
- `CopyCosmeticsTool` (new), `CosmeticsPanel` (13), `FacadeSelectionPanel` (16),
  `KleiInventoryScreen` (15), `OutfitBrowserScreen_CategoriesAndSearchBar`, `KleiPermitDioramaVis_*`

**Where:**
- `u60:Database/BlueprintSearch.cs:6`
- `u60:FuzzySearch.cs:75-193`; U59: `u59:FuzzySearch.cs:55-142`
- `fp60:FuzzySharp/TokenizedString.cs:22`,
  `FuzzySharp.SimilarityRatio.Scorer.StrategySensitive/TokenSortScorerBase.cs:12`

**Engine surface:**
- culture-sensitive string comparison (our CoreCLR uses ICU on Linux; the original uses Mono's
  collation)
- TMP input fields, scroll views and grids of the Supply Closet

**Test:**
- Setup: `early-base`.
- Steps:
  1. Search "pump", "wtr pmp", "regal" and a two-token query in the Duplicant blueprint tab and
     the building info screen; `ui_dump` the result list order.
  2. Copy a Duplicant's outfit to another with the tool.
  3. Rotate a drywall blueprint.
  4. Open the Supply Closet from "See more" and from the main menu, at 1280×720 and 1280×800.
- Pass:
  - result order and dumps match;
  - outfits `== original`.

**Harness:** e2e (new spec).

### U5 Details screen and codex layouts

**Risk: medium.**

**Patch notes:** critter and plant subtitles; recreation effect durations; large diets; light
range as whole numbers; recipe side screen height; Orbital Cargo side screen on Steam Deck; Suit
Wearing category in skills; Effects indentation; codex Critters/Plants by Type pages and layout;
collapsible headers after navigating; drops formatting; Food Quality hidden for ingredients;
Applications sections; Medical Pack link; Power Banks heading; booster recipes; Refashionator;
Flue Coral checkbox and "Peak Conditions"; Breathing Station charges; Decor plants' Throughput
label; Neutronium tooltip.

**What changed:**
- `SimpleInfoScreen` (28), `AdditionalDetailsPanel` (8), `DescriptorPanel`,
  `CollapsibleDetailContentPanel`, `CodexEntryGenerator*` (31, 26, 7, 7)
- `CodexCollapsibleHeader.ResolveContentsGameObject` replacing a cached `ContentsGameObject`
- `Descriptor` indent API
- `SelectedRecipeQueueScreen` (6)
- codex yaml (`codex/TechComponents/DynamicCodexEntry_Electrobank.yaml` new)

**Where:**
- `u60:CodexCollapsibleHeader.cs:27-40`; U59: `u59:CodexCollapsibleHeader.cs:12-52`
- `fp60:Descriptor.cs`

**Engine surface:**
- layout groups, ContentSizeFitter, TMP
- Unity's destroyed-object null for the cached header (`== null` on a destroyed GameObject)

**Test:**
- Setup: `landing` with all DLCs.
- Steps:
  1. Select a Hatch, a Pip, a Bristle Blossom, a Soda Fountain, a lamp; `ui_dump` the details
     panels.
  2. Codex: Critters by Type, Plants by Type, Hatch, Muckroot, Balm Lily, Power Banks.
  3. Collapse a header, open another article, come back, collapse again.
  4. Skills screen.
  5. A fabricator's recipe screen at 1280×800.
- Pass:
  - dumps match;
  - the header toggles in both.

**Harness:** e2e (two or three specs by screen).

### U6 Tutorial hiding

**Risk: low.**

**Patch note:** "Simplified tutorial hiding ... This can be reset in the Options screen."

**What changed:**
- `Tutorial`
- `GameOptionsScreen.OnShowTutorialToggle`, `RefreshShowTutorialToggle` (new); `OnTutorialReset`
  removed
- `MessageDialogFrame` (9)

**Where:**
- `u60:Tutorial.cs:71-99`

**Engine surface:**
- KPlayerPrefs, a toggle

**Test:**
- Setup: `landing`.
- Steps:
  1. Tick "don't show again" on the first tutorial.
  2. Check none shows for a cycle.
  3. Options > Game, re-enable.
- Pass: prefs and shown messages `== original`.

**Harness:** e2e.

### U7 Translation validation

**Status:** passes (d6043974): `tests/e2e/u60-u7-zh.yaml`, `-ru`, `-ko` (results in
`tests/e2e/results-u60`) require each "TRANSLATION ERROR!" block the original logs, whole, and no
other: zh and ko reject `STRINGS.UI.BUILDINGEFFECTS.DIET_HEADER` (parameters), ru that and
`STRINGS.UI.ROLES_SCREEN.PERKS.EXOSUIT_EXPERTISE.DESCRIPTION` (link count). Ours logs the same
keys in the same order. The culture-sensitive `IndexOf` differs from Mono's around soft hyphens,
zero-width and combining characters (L2's probe, `IX` lines), but no translated string reaches
that difference.

**Risk: low.**

**Patch note:** not in the notes.

**What changed:**
- `Localization.ValidateTranslatedString` (new), called from `OverloadStrings`. A translated string
  with mismatched parameters, more links than English or unbalanced link tags is rejected.
- The ko, ru and zh `.po` files changed.

**Where:**
- `u60:Localization.cs:886-930` (new)
- `u60:Localization.cs:567` (the call)

**Engine surface:**
- string scanning with `String.Split` and the culture-sensitive `String.IndexOf(string, int)` (ICU
  in ours, Mono's collation in the original)

**Test:**
- Setup: the existing translation specs (`mods/929139073-german-translation.yaml` and others),
  plus `language: zh_klei`, `ru_klei`.
- Steps:
  1. Count the rejection log lines.
- Pass: the same count and keys as the original's.

**Harness:** e2e (existing specs; add a `require_log` line).

### U8 Front-end screens

**Risk: medium.**

**Patch notes:**
- "Adjusted arrow position on asteroid selection screen"
- "Load Game screen has more room for displaying save name and cycle count"
- "Fixed white lines surrounding DLC logos on load screen"
- "Show fewer details in the mod restart dialog"

**What changed:**
- `LoadScreen` (17), `ColonyDestinationSelectScreen`, `KMod.Manager.DevRestartDialog`,
  `MainMenu.RefreshResumeButton`
- DLC logo sprites in the data

**Where:**
- `u60:KMod/Manager.cs:954` (the restart dialog)

**Engine surface:**
- sprite sampling at the logos' edges (the white lines were a texture-border artefact)
- TMP truncation

**Test:**
- Setup: `main-menu`.
- Steps:
  1. Shot the main menu's DLC logos, the Load screen with a long colony name, and the asteroid
     carousel.
  2. Enable a mod to raise the restart dialog.
- Pass:
  - shots within 2 %;
  - dumps match.

**Harness:** e2e (the mod dialog already appears in QA; qa dismisses it, e477137a).

### U9 Sandbox tool menu

**Risk: low.**

**Patch notes:**
- "Added Square shape option"
- "Added Foreground/Background options"
- "Added Size control to the Critter Removal sandbox tool"
- "Pacu Treat is now available in the Spawner"
- "The Destroy sandbox tool also deletes falling elements"

**What changed:**
- `SandboxToolParameterMenu_ShapeLayerControl` (new)
- `SandboxToolParameterMenu` (16)
- `UnstableGroundManager`

**Where:**
- `u60:...:28-80` (the shape and layer control)
- `u60:UnstableGroundManager.cs:167`

**Engine surface:**
- toggles and sliders in the tool menu

**Test:**
- Setup: `early-base`, sandbox.
- Steps:
  1. `ui_dump` each tool's parameter menu.
  2. A square brush stroke.
  3. A destroy over falling sand.
- Pass: dumps and the cells' mass `== original`.

**Harness:** e2e.

## Runtime and BCL

### L1 Cell change tracking and KObject

**Risk: medium.**

**Patch notes:**
- "Moved CellChange events to the EventSystem ... added cachedCell to KObject's"
- "Fixed memory leaks related to pathfinding and loading another save"

**What changed:**
- `CellChangeMonitor.RegisterKObject`, `UnregisterKObjectHierarchy`, `MarkDirtyRecursive` keyed by
  `Transform.GetInstanceID`, walking `childCount`/`GetChild`
- `KObject` gains `gameObject`, `Trigger`, `DetachFromCellChangeMonitor`
- `KMonoBehaviour.KObj`; `Grid.PosToCell`

**Where:**
- `fp60:CellChangeMonitor.cs:164-268`
- `fp60:KObject.cs`

**Engine surface:**
- instance ids, child order
- destruction order (the cellchangecrash Destroy-from-OnDestroy case, already fixed: faba323a)

**Test:**
- Setup: QA verify on U57; `rockets-so` for the e2e.
- Steps:
  1. QA verify on U57 (moving critters, falling debris, conveyors).
  2. An e2e on `rockets-so` launching and landing a rocket.
  3. Load a second save from the pause menu, then a third.
- Pass:
  - no new log signatures;
  - positions of a tracked critter and a rocket `== original` after the runs;
  - managed heap after the third load within 5 % of after the first.

**Harness:** qa verify, e2e (`rocket-launch.yaml` extended), `--frame-stats`.

### L2 New BCL calls

**Status:** fails in ours (Linux; ours on Vulkan, the original on OpenGL; no rendering involved):
culture-sensitive `CompareInfo` and `IndexOf` use ICU in ours and Mono's collation in the
original, and the orders differ; repro `tests/e2e/u60-bcl-probe.yaml` (UnityProbe `u60`, lines
`U60 CI`, `U60 IX`; 77186275). Not fixed because the game's own threads run invariant in both
players (also under `LANG=de_DE.UTF-8` and `ru_RU.UTF-8`) and FuzzySharp's
`TokenizedString.SortedTokens` of all 469 building names are identical in English, Russian and
Simplified Chinese; only mod code doing culture-sensitive comparisons could see it. In detail:
- `File.Replace` over an existing and a missing destination, with a new, existing, missing and
  read-only backup, a read-only destination or source, and `File.Move`: the same outcomes and
  files (pinned by `MonoFileReplaceTests`). Two cases the game never reaches differ: a source that
  is its destination (Mono backs it up and succeeds, ours throws IOException), and a missing
  source (both throw FileNotFoundException, Mono after making the backup).
- `CompareInfo.Compare` orders differently under every culture: apostrophes and hyphens (Mono
  ignores them, "'s" sorts with "s"), "ae"/"æ" and "ß"/"ss" (equal in Mono), and under ru-RU, zh-CN
  and ko-KR the scripts (Cyrillic and CJK before Latin in ICU).
- Culture-sensitive `IndexOf` differs around a soft hyphen, a zero-width space and a combining
  accent (U7's strings have none of them).
- `Int32`/`Int64.TryFormat` give the same text for every format and culture tried; `Single`'s
  differs as ARCHITECTURE D2 describes (shortest round trip, "-0"), and under de-DE, ru-RU, zh-CN
  and ko-KR infinity is "∞" in ours ("+unendlich", "бесконечность", "Infinity" in Mono).

**Risk: medium.**

**Patch note:** not in the notes.

**What changed:**
- `File.Replace(tmp, final, bak, true)` and `File.Move` in `SteamUGCService.SwapDownloadedFile`
- `File.Replace` in `SaveLoader.MoveCorruptFile`: inside a `catch` after an empty `try`, so
  unreachable as decompiled
- `CompareInfo` from `CultureInfo.CurrentCulture` in FuzzySharp (U4)
- `Int32/Int64.TryFormat` (U1)
- `Task.Run`, `Task.WaitAll`, `Interlocked.CompareExchange` (R3)
- `HashCode`, `MemoryExtensions`, `ArraySegment<byte>` in VYaml-generated formatters

**Where:**
- `u60:SteamUGCService.cs:199-211`
- `u60:SaveLoader.cs:246`

**Engine surface:**
- CoreCLR's BCL in place of Mono's

**Test:**
- Setup: unit tests in `OniMoono.UnityEngine.Tests` that pin Mono's results where they may
  differ.
- Steps:
  1. `File.Replace` over an existing and a missing destination and a read-only backup.
  2. `CompareInfo.Compare` ordering of the blueprint names under `en-US`, `de-DE`, `ru-RU` and the
     invariant culture, compared with values recorded from the original (UnityProbe).
- Pass: equal.

**Harness:** unit, UnityProbe.

### L3 Development-build calls and new engine members

**Status:** passes (e58e7da0): `scripts/check.sh` on the 761211 Linux data: ApiCheck finds 787 type and
3,225 member refs, 0 types and 13 members missing, all 13 in `UnityEngine.UIElementsModule`; the
unit suites pass.

**Risk: low.** Covered (67946a05, 1976a914; inventory "Engine API").

**Patch note:** "Minor unity version update 6000.3.5f2 -> 6000.3.17f1."

**What changed:**
- the new external members listed under Method
- `UIScheduler.GetTimeAsDouble` uses `Time.unscaledTimeAsDouble` (new)

**Where:**
- `u60:UIScheduler.cs:16`

**Engine surface:**
- ApiCheck, and the no-op assertion and profiler members

**Test:**
- Steps:
  1. `scripts/check.sh` (ApiCheck) on the U60 data.
- Pass: only the 13 known UI Toolkit members missing.

**Harness:** qa unit.

## Assets and data

### A1 Templates through VYaml 1.4

**Risk: medium.** A new game generates without errors and matches QA's references (inventory,
2026-10-31), but a value read differently would not log.

**Patch notes:**
- "Upgraded VYaml from 1.2.0 to 1.4.0"
- "POI templates are now loaded using VYaml"
- "Faster worldgen when starting a new game."

**What changed:**

| | U59 | U60 |
|---|---|---|
| `TemplateCache.LoadTemplateYaml` | YamlDotNet | `KYaml.LoadFile<TemplateContainer>`, then `ApplyVYamlDefaults` |

All templates, not only POIs. VYaml attributes on `TemplateContainer` (29),
`TemplateClasses.Prefab` (30), `Cell`, `StorageItem`, `Rottable`, `TemplateAmountValue`; `Info` (17).

**Where:**
- `u60:TemplateCache.cs:40-63`; U59: `u59:TemplateCache.cs:48`

**Engine surface:**
- VYaml runs from `Managed/` on our BCL (UTF-8 decoding, number parsing)
- source-generated formatters
- reflection over attributes

**Test:**
- Setup: new tooling: an E2eDriver command (or a PerfProbe mode) that loads every `.yaml` under
  `templates/` and the DLC template folders through `TemplateCache.LoadTemplateYaml`. It writes
  one line per file: cell count, prefab count, the sums of mass and temperature, prefab ids, other
  values' hash.
- Steps:
  1. Run it in both players on the same data.
  2. A new game for each start (`SNDST-A`, forest, swamp, the DLC starts), checking spawnables and
     the start base cells functionally.
- Pass: every line `== original`.

**Harness:** new tooling; e2e worldgen pattern.

### A2 Asset loading at the main menu

**Status:** passes (89f503d6, Linux OpenGL; Windows: pending). `tests/e2e/u60/a2-menu-assets.yaml`:
2585 prefabs, 3325 anims, 469 building defs and the first 50 prefab ids in order equal the
original's. The time to the menu is not part of the spec (the start-up timing script's).

**Risk: medium.**

**Patch note:** "Improve loading time of assets and POIs when entering the main menu."

**What changed:**
- `Assets.LoadAnims`, `CreatePrefabs`, `SortPrefabsBySignificantDLC`,
  `AddCountableBuildMaterialCategories`
- `GlobalAssets`, `Db`

**Where:**
- `u60:Assets.cs:212-338`

**Engine surface:**
- our bundle reader and the order prefabs are made in

**Test:**
- Setup: second launch (start-up rule) to the main menu, ours and original.
- Steps:
  1. Count prefabs (`Assets.Prefabs.Count`), anims (`Assets.Anims.Count`), and the first 50
     prefab ids in order.
- Pass:
  - equal;
  - ours' time to menu no worse than on U59 by more than 10 %.

**Harness:** e2e (`from: main-menu`), the start-up timing script.

### A3 Starting-biome vistas

**Risk: medium.** The toggle is covered; the spawn at worldgen is not.

**Patch notes:** the Sandstone, Forest, Swamp, Ice Cave vistas; "Improved the way vista textures
are loaded in memory".

**What changed:**
- `VistaConfigs`, `Vista` (16), `VistaLayers` with `Resources.Load`/`UnloadAsset`,
  `VistaSelectionPanel` (new)
- Base templates spawn the vista entity (`templates/bases/forestBase.yaml`, `sandstoneBase.yaml`,
  `dlc/expansion1/.../swampBase.yaml`, `dlc/dlc2/.../ceresBase.yaml`); DLC5 `beachBaseVista.yaml`
  changed.
- Garden is commented out in `dlc/dlc4/templates/bases/gardenBase.yaml`.

**Where:**
- `u60:VistaConfigs.cs:27-155`
- `u60:VistaLayers.cs:77-123`

**Engine surface:**
- `Resources.UnloadAsset` and reload from the asset file (0e763810)

**Test:**
- Steps:
  1. `vista-toggle.yaml` as is.
  2. A new game per start (Terra, Forest, Swamp in Spaced Out, Ceres, Beach) with a `world_shot`
     of the pod.
- Pass: shots match.

**Harness:** e2e (existing and new).

### A4 Data files

**Risk: low.**

**Patch notes:** MetalRich/MetalPoor and Zinc Ore, the frozen breakroom POI, codex entries,
strings.

**What changed:**
- Zinc Ore in the MetalRich and MetalPoor traits
- the frozen breakroom POI: every `preventFoWReveal` removed
- three new codex yaml
- FMOD banks (base, DLC2, DLC4, DLC5, expansion1)
- `COSMETIC2_ID` (Steam app 5315880) maps to `hires_cosmetic2_bundle`, which the data does not ship

**Where:**
- `worldgen/traits/MetalRich.yaml:35-37`, `MetalPoor.yaml:35-37`
- `dlc/expansion1/templates/poi/frozen/frag_breakroom.yaml`
- `fp60:StreamedTextures.cs`, `fp60:SteamDistributionPlatform.cs:54-55`

**Engine surface:**
- none new, except a DLC the Steam double may report as owned whose bundle is missing

**Test:**
- Steps:
  1. Check what `tools/SteamStub` answers for app 5315880.
  2. Run the main menu with it owned and not owned: both players must log the same and start.
  3. Worldgen with MetalRich on a zinc world: count Zinc Ore cells.
- Pass: `== original`.

**Harness:** e2e.

## Saves

### S1 Save format 7.39 and U59 saves

**Risk: medium.** The tweaks were reviewed, not run on a 7.39 round trip with these members.

**Patch notes:**
- "Updated EntombedItemManager and BubbleManager to serialize disease using the id hash"
- "Duplicants in existing saves automatically gain these traits if they have been skill scrubbed"
- "Fixed critters being unable to use a critter condo for one cycle after load"
- "Doors left in an open state will now close correctly when loaded"

**What changed:**

| | U59 | U60 |
|---|---|---|
| Save minor version | 38 | 39 (`SAVE_MINOR_VERSION_DISEASE_HASHES`) |

Also changed: `EntombedItemManager`, `MinionResume.GrantedSkillIDs`, `Door.OnSpawn`.

**Where:**
- `u60:SaveManager.cs:84-86`; U59: `u59:SaveManager.cs:84`
- `u60:EntombedItemManager.cs:20-80`
- `u60:MinionResume.cs:51, 118`
- `u60:Door.cs:180-195`

**Engine surface:**
- KSerialization with our save tweaks (fast-serialization, fast-save-compression, background-save)

**Test:**
- Setup: every tweak on, then none.
- Steps:
  1. Load a U59 save (DeCrew, U57) in U60, save, load the result.
  2. In a new colony, entomb a germy item and make a germy bubble; save, load.
- Pass: entombed items' disease and count, Duplicants' traits and door states `== original` after
  each load.

**Harness:** e2e (`expect_state` over `all:` lists), qa saveload.

### S2 Saving while screenshots are written

**Status:** passes (71236ffc, Linux OpenGL, background-save; Windows: pending).
`tests/e2e/u60/s2-save-and-quit.yaml` (with the new `expect_exit` step and `quits` clicks): Save
then Main Menu at once, the save loaded back, the autosave at a new cycle, Save and Quit to the
desktop (exit 0); the three previews within 0.6 %, 3.2 % for the autosave's (the colony moved).
The run with no save tweaks is still to be recorded.

**Risk: high.**

**Patch note:** "Improved stutter from initial save after closing the message."

**What changed:**
- `SaveScreen.DoSaveWhenSafe` waits for `Game.CanSave` (new).
- `SaveLoader.SaveAndQuitRoutine`.
- The initial save writes the first timelapse frame.
- `Timelapser.HasPendingScreenshotWrites` and the flush on cleanup (R3).
- `Global.OnExitRequest`.

**Where:**
- `u60:SaveScreen.cs:95-110`
- `u60:SaveLoader.cs:809`
- `u60:SaveLoader.cs:930` (the first timelapse frame)
- `u60:Global.cs:360-407`

**Engine surface:**
- background-save moves the save off the frame; U60 starts a GPU readback and a worker-thread PNG
  write inside it
- both must finish, in order, before quit or load

**Test:**
- Setup: `early-base`.
- Steps:
  1. Save with background-save on, then immediately quit to the main menu.
  2. Save and quit to desktop (`SaveAndQuitRoutine`).
  3. Autosave at a new day with the timelapse on.
- Pass:
  - the save loads;
  - its preview PNG is complete (R3's comparison);
  - no exception from the writer task.

**Harness:** e2e, `E2E_GAME_TWEAKS=background-save` and `none`.

## Mods

### M1 Loose-file Workshop mods

**Risk: high.** Not run (inventory, "Steam's loose-file mod format").

**Patch note:** "Update our mod-loading code to accommodate the new SteamUGC format, namely loose
files in a folder, as well as continuing to support the legacy zip format."

**What changed:**
- `KMod.Steam.MakeMod` tries `new Directory(pchFolder)` before `new ZipFile`.

**Where:**
- `u60:KMod/Steam.cs:21-131`; U59: `u59:KMod/Steam.cs:12-84`

**Engine surface:**
- our mod loading hooks (ModAccess's transpiler on `DLLLoader.LoadDLLs`, ModVersionStamp on
  `KMod.Local.Subscribe`)
- assembly loading from a plain folder

**Test:**
- Setup: a Workshop mod laid out as a folder in `steamapps/workshop/content/457140/ID/` (copy a
  covered mod's zip contents there), with the Steam double reporting it installed at that folder.
- Steps:
  1. Start.
  2. Open Mods.
- Pass:
  - the mod loads in both players with the same log lines;
  - its feature works (its existing spec passes).

**Harness:** e2e, with new tooling: SteamStub serving `GetItemInstallInfo` for a folder.

### M2 Corrupt Workshop items are downloaded again

**Risk: high.**

**Patch note:** not in the notes.

**What changed:**
- `KMod.Steam.MakeCorruptMod`, `MakeModAndReportOutcome`
- `SteamUGCService.ForceDownloadContent` (`UGCDownloadToLocation`,
  `CallResult<RemoteStorageDownloadUGCResult_t>`), `SwapDownloadedFile` (`File.Replace`)
- `KMod.Manager.SubscribeCorruptMod`, `OnCorruptModReacquired`

**Where:**
- `u60:KMod/Steam.cs:21-131`
- `u60:SteamUGCService.cs:166-230`

**Engine surface:**
- our Steamworks.NET port (call results, struct layout of `RemoteStorageDownloadUGCResult_t`)
- `File.Replace` on Linux and Windows

**Test:**
- Steps:
  1. Truncate a subscribed legacy-zip mod's file.
  2. Start.
  3. Observe "Corrupt mod file(s)", "ForceDownloadContent completed ... succeeded=True",
     "SwapDownloadedFile: swapped".
- Pass:
  - the same lines in both players;
  - the mod usable after the restart the game asks for.

**Harness:** e2e, with SteamStub implementing `UGCDownloadToLocation` and the call result; or a
manual run on Windows with real Steam.

### M3 Mod API changes

**Risk: low.**

**Patch notes:**
- "ModUtil.CreateSubstance now initializes the anims array"
- "Fixed asteroid and biome remixing mods crashing"
- "Added requiredDlcIds to WorldMixingSettings and SubworldMixing Settings"

**What changed:**
- `ModUtil.CreateSubstance`, `Substance.Anims`
- `requiredDlcIds`
- `ProcGen.SettingsCache.GetRandomTraits`

**Where:**
- `u60:ModUtil.cs:106`
- `u60:Substance.cs:118`
- `fp60:ProcGen/WorldMixingSettings.cs:24`, `fp60:ProcGen/SubworldMixingSettings.cs:28`

**Engine surface:**
- none new

**Test:**
- Steps:
  1. The burndown's element-adding and remix mods on U60 (`tests/e2e/results-u60`).
- Pass: played.

**Harness:** e2e (mod burndown).

### M4 Obsolete game APIs

**Status:** passes (e58e7da0): the U60 burndown (`tests/e2e/results-u60`, 595 records, 761015 and
761211) played 572 mods; 210 of them carry `IntraObjectHandler` subscriptions, 16 name
`GetDescriptors`, 14 `GlobalStringBuilderPool` and 172 `CreateEquipmentDef` in their assemblies.
None of the 23 not played fails on these APIs: 18 fail in the original too (pending), 4 played
with known findings (warning), and CycleTrim fails in ours for a structural reason (it checks for
Unity's Mono).

**Risk: low.** The burndown covers it per mod.

**Patch notes:**
- "EventSystem’s IntraObjectHandler is obsolete"
- "IGameObjectEffectDescriptor.GetDescriptors is obsolete"
- "EquipmentTemplates.CreateEquipmentDef has a new overload"
- "Marked Unsubscribe(int, Action) obsolete"
- "GlobalStringBuilderPool is now obsolete"
- harvest tunings

**What changed:**
- `EventSystem.Subscribe_obsolete`, `Unsubscribe_obsolete`
- `GetDescriptors` → `CollectDescriptors` across about 200 types
- `TUNING/CROPS.cs`

**Where:**
- `fp60:EventSystem.cs`

**Engine surface:**
- Harmony patches by mods on moved methods
- our Harmony behaves as Mono's (no IL-shape promise for transpilers)

**Test:** none beyond the burndown.

## Platform and host

### P1 Crash reporter messages

**Status:** fixed in 1b876641 (Linux OpenGL; Windows: pending). Specs `tests/e2e/u60/p1-sim-blocked.yaml`
(libSimDLL.so absent: the popup) and `p1-sim-unreadable.yaml` (unreadable, as worded: Unity's
Mono names the file and dlerror's text, so the original shows no popup either); both played.

**Risk: high.** Likely to fail as is.

**Patch notes:**
- "Display a message on the crash reporter screen when it detects the operating system has blocked
  loading the sim"
- "Display a message suggesting the user verify files or reinstall the game if it detects certain
  crashes"

**What changed:**
- `KCrashReporter.IsOperatingSystemBlockedError` (new) matches `"DllNotFoundException: SimDLL"` in
  the error or the stack trace.
- `CorruptFileErrorPrefixes`: "KAnimBatchManager is not ready when loading anim", "First anim file
  needs to be non-null."
- `WorldGen.ReportWorldGenError` queues the sim error.
- `QueueSimDLLLoadError`.

**Where:**
- `u60:KCrashReporter.cs:261-263, 453-470` (new)
- `u60:KCrashReporter.cs:261` (the prefixes)
- `u60:ProcGenGame/WorldGen.cs:544-552`

**Engine surface:** the text of our `DllNotFoundException` and of the log line our player writes
for it.

| | Message | Matches |
|---|---|---|
| Mono | "SimDLL assembly:<unknown assembly> type:<unknown type> member:(null)", so `e.ToString()` holds "DllNotFoundException: SimDLL" | yes |
| CoreCLR | "Unable to load shared library 'SimDLL' or one of its dependencies" | no |

**Test:**
- Setup: a test install (never the live one).
- Steps:
  1. Make `libSimDLL.so` unreadable; New Game to worldgen.
  2. On Windows, the same with `SimDLL.dll` blocked.
- Pass: the "operating system blocked" popup appears in both players, with the same title and the
  same report queued.

**Harness:** e2e on a copied install (`out/local-tools/u60orig` pattern); manual on Windows.

### P2 Vulkan as the Linux default

**Risk: medium.** Covered on llvmpipe and Mesa d3d12; real Linux hardware is not available here.

**Patch note:** "Vulkan is now the default renderer on Linux. OpenGL can be used using launch
options."

**What changed:**
- player settings in the data; no game code

**Where:**
- our selection: ARCHITECTURE §3.5, D54

**Engine surface:**
- renderer selection and fallback

**Test:**
- Steps:
  1. The launch options Unity's player takes (`-force-opengl`, `-force-vulkan`) on ours: each
     picks that renderer; no option gives Vulkan.
  2. QA dogfood once per renderer.
- Pass:
  - the log names the renderer as the original's does;
  - captures match.

**Harness:** qa dogfood; manual on a real GPU.

### P3 Linux restarter

**Risk: medium.**

**Patch note:** "Fixed Linux restarter not working."

**What changed:**

| | U59 | U60 |
|---|---|---|
| Klei's `Restarter` binary | 12.5 MB | 2.3 MB (the fix) |

`App.Restart` unchanged.

**Where:**
- ours ships `src/OniMoono.Restarter`

**Engine surface:**
- our restarter, argument passing

**Test:**
- Setup: `main-menu`.
- Steps:
  1. Enable a mod and accept the restart.
  2. Switch language (translation spec's restart).
- Pass: the game comes back with the same arguments and the change applied, in both.

**Harness:** e2e (`translation-screen.yaml` stops at the dialog; extend it past the restart).

### P4 Unity 6000.3.17f1 data

**Status:** passes (e58e7da0): `scripts/check.sh` on the 761211 Linux data (check-assets: 0 failures, 0
unresolved scripts; check-bind: 0 failures, 584 shader variants); `--check-assets`,
`--check-bind` and `--check-shaders` are also clean on the frozen Windows 761211 data, read from
Linux (587 variants; 120 shaders, 0 compile or link errors, as on the Linux data).

**Risk: low.** Covered (09a00671, `--check-*` modes on Linux and Windows U60 data).

**Patch note:** "Minor unity version update."

**What changed:**
- six native layouts (inventory, "Native data layouts")
- `boot.config` changes

**Engine surface:**
- type trees

**Test:**
- Steps:
  1. `scripts/check.sh`.
- Pass: asset, bind and shader checks clean.

### P5 IMGUI developer tools

**Risk: low.** Checked pixel for pixel (63c86671, c21392e8).

**Patch note:** "Fixed IMGUI developer tools crashing on Linux and enabled them for Mac."

**What changed:**
- a new `cimgui.so` exporting `SetKAssertCB`
- `DevToolManager`, `DevToolSearchAndFocus` (new), `DevToolFuzzy`

**Engine surface:**
- Mesh, CommandBuffer scissors, RenderTexture in a RawImage

**Test:**
- Steps: an e2e of the 2026-11-01 manual check, so it stays checked:
  1. toggle;
  2. menu bar;
  3. StringsTable;
  4. a scroll;
  5. typing into its filter.
- Pass: shots match.

## Audio

### AU1 UI sounds and stingers

**Risk: low.**

**Patch notes:**
- "Fixed 'click' sound selecting storages for the first time"
- "Fixed a doubled click sound when opening the Research or Skills screens from the Printing Pod
  sidescreen"
- "Added music stinger ... Minnow POI"
- "Fixed audio missing reverb on new critters"
- slider sounds on wheel (I2)

**What changed:**

| | U59 | U60 |
|---|---|---|
| `GetSoundEventDescription` on a missing event | throws | returns null |

Also: `KFMOD` caches event paths by GUID (`eventPathsByGuid`, `PathToEventReference`);
`GlobalAssets.GetSound`; banks changed.

**Where:**
- `fp60:KFMOD.cs:59-91, 210-322`; U59: `fp59:KFMOD.cs:178-228`

**Engine surface:**
- FMODUnity (shipped) and the native library
- ours only passes 3D attributes and the listener

**Test:**
- Setup: `early-base`, with
  `driver: {count: {methods: ["KFMOD:PlayUISound", "KFMOD:BeginOneShot"]}}` around the steps.
- Steps:
  1. Select a storage bin twice.
  2. Open Research from the Printing Pod side screen.
- Pass: counts `== original`.

**Harness:** e2e.

## Game logic (lower risk)

Each item is game code with no new engine surface. One spec per item, functional checks only
(`expect_state ... == original`), sandbox setups through `setup.call` where they only prepare.

| Id | Changes | Where | Check |
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

Plain C# diff of the two betas (`git diff U60-761015 U60-761211`): 13 files. `u761211:` lines are
at tag U60-761211. Of the 136 Managed DLLs only `Assembly-CSharp.dll` changed (byte comparison of
`E:\Development\U60-761015\Managed` and `U60-761211\Managed`). 761211's StreamingAssets are not on
this machine.

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
