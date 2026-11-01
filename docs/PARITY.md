# Parity checklist

The M6 exit criterion (PLAN §M6): every item ticked, or listed with a documented deviation.
Items come from the game's own screens, tools and features (Assembly-CSharp: `KScreen`
subclasses, `OverlayModes`, `InterfaceTool` subclasses, options screens). An item is ticked when
a run on this engine exercised it and the result matched the original player (screenshots
compared with the original under the same conditions, ARCHITECTURE §9), with no exceptions in
the log.

Legend: `[x]` verified · `[ ]` not yet verified · `[~]` deviation (see the note).

## Start-up and front end

- [x] Launch scene, Klei/Unity splash, `LaunchInitializer`, Steam initialisation (offline test double)
- [x] Main menu: backdrop, buttons, MOTD/news panels (offline), build watermark
- [x] Main menu: Resume Game / Load Game (`LoadScreen`, colony and save lists)
- [x] New game flow: `ModeSelectScreen` (Survival / No Sweat), `ClusterCategorySelectionScreen`
- [x] `ColonyDestinationSelectScreen` (asteroid, seed, settings panels)
- [x] Story Traits list, Game Settings panel (seed, difficulty steppers, checkboxes respond)
- [x] `WorldGenScreen` (threaded worldgen with SimDLL settle, progress)
- [x] Worldgen parity: the same coordinates (typed into the destination screen) generate the same terrain as the original player
- [x] `CharacterSelectionController` / `CharacterContainer` (Duplicant cards, reroll, embark)
- [x] `ModsScreen` (empty list, Steam Workshop / Disable All / Close; no Workshop items with the test double) as the original
- [x] `ScenariosMenu`: not reachable in this build (no code opens it)
- [x] `LockerMenuScreen` (Supply Closet, offline) and its Duplicants outfit screen
- [x] Supply Closet "All" (`KleiInventoryScreen`: blueprint categories, building diorama with the animate-in, details) and Wardrobe (`OutfitBrowserScreen`; its initial outfit is random) as the original
- [x] `PatchNotesScreen`: unreachable in this build (`ShouldShowScreen` returns false); `KleiItemDropScreen`: needs claimable items from a Klei account (the offline test double has no valid ticket), unreachable in both players
- [x] `RetiredColonyInfoScreen`: colony list, achievements, colony detail (Duplicants with portraits, buildings, oxygen graph); colony cards show the timelapse thumbnail
- [x] Tutorial videos (`VideoScreen` from a message's video widget, render-texture output) play as the original, with libvpx available (verified with Ubuntu's libvpx9 unpacked locally)
- [x] Intro short on first launch: this build sets no intro short (`MainMenu.IntroShortName`), so neither player plays one (no `PlayShortOnLaunch` pref written by either)
- [x] Victory videos: the Thriving sequence's short and victory loop with its message panel (`VideoScreen.PlayShortWithVictoryLoop`), triggered with tools/UnityProbe in both players and captured at the same points

## Options

- [x] `OptionsMenuScreen` and its Graphics, Audio, Game, Data Communication, Feedback and Credits screens (compared with the original)
- [x] `GraphicsOptionsScreen`: fullscreen switching at runtime (window resized, UI re-laid out, "Accept Changes?"), resolution label as the original ("@ 0Hz" under Xvfb in both)
- [x] `GraphicsOptionsScreen`: resolution dropdown (placement and the same mode list as the original on a single-mode display), choosing a resolution resizes the window with "Accept Changes?"
- [x] `GraphicsOptionsScreen`: UI scale slider (whole UI re-scales), colour mode dropdown (palette swatches change) as the original
- [x] `AudioOptionsScreen`: volume sliders and settings checkboxes respond as the original (sound output itself: see Audio below)
- [x] `GameOptionsScreen`: camera pan speed slider, cloud-save checkbox, temperature unit. Slider clicks and drags started off the handle land on the same value as the original; drags started on the handle follow uGUI's handle-offset rule exactly here, while the original's result varies with the press point under XTEST (one step either way)
- [x] `InputBindingsScreen`: rebinding (conflict notice when a key is taken), persistence across restarts (`keybindings.json`)
- [x] `LanguageOptionsScreen`: built-in languages listed; choosing Chinese prompts in Chinese, restarts through `Restarter`, and the menus come back in Chinese (CJK font)
- [x] Settings persistence across restarts (`kplayerprefs.yaml`: temperature unit)

## Colony HUD and screens

- [x] World composite: terrain, tiles, backwall, gas, liquids, light buffer, property textures, fog of war
- [x] Duplicants and buildings (KAnim batches), printing pod, status items, name displays
- [x] Thought and conversation bubbles: pop-in by the legacy `Animation` component (the game's only legacy clips), icons inside the bubble at the clip's final scale
- [x] `TopLeftControlScreen`, `DateTime` (cycle clock), `MeterScreen` (Duplicants, stress, rations)
- [x] `SpeedControlScreen` (pause, speeds 1–3)
- [x] `ColonyDiagnosticScreen` panel, `PinnedResourcesPanel` / resources panel
- [x] Notifications (`NotificationScreen`), tutorial messages
- [x] `DetailsScreen` (selection: status, properties tabs with nested canvases and RectMask2D)
- [x] `HoverTextScreen` (element hover cards)
- [x] `SideDetailsScreen`: Ration Box element filter (unticking → "Filters Not Designated"), capacity slider (drag → new capacity), Material tab
- [x] Liquid Valve flow control side screen: a slider click sets 4673 g/s in both players
- [x] Pneumatic Door access control (Duplicant rows with portraits, Open/Auto/Lock) and Thermo Sensor threshold side screens render and respond (not yet compared with the original)
- [x] Side screens of 18 buildings built and selected by tools/UnityProbe in both players (door access, thermo/atmo sensors, signal switch, storage bin, liquid valve, cot and outhouse assignment, manual generator and smart battery thresholds, research station, microbe musher and electric grill orders, deodorizer, electrolyzer, flower pot and planter box seeds, refrigerator) match the original
- [x] `BuildMenu` / `BuildingGroupScreen` / `ProductInfoScreen` / `MaterialSelectionPanel`: category menus, building info, material choice and placement (Storage Bin, Ladder, Dev Life Support, wire tool)
- [x] Management screens opened by hotkey and compared with the original: Vitals, Consumables, Priorities (jobs), Schedule (`ScheduleScreen`), Skills (`SkillsScreen`, Duplicant portrait in a stencil mask), Database (`CodexScreen`), Reports (`ReportScreen`)
- [x] Research and Starmap buttons before a Research Station / Telescope exists: locked, with the original's tooltips
- [x] Research tree interaction: hover, tooltips, selecting a tech (instant completion in debug mode) as the original
- [x] Research progress: Basic Farming at 7.5/15 points (set through `Research`) shows the same progress bar and search results in the research screen as the original
- [x] Research by a Duplicant at a powered Research Station (tools/UnityProbe: station, Dev Generator and wire built beside the start room, 500 kg dirt, Basic Farming active, 150 s at speed 3): 60% of Basic Farming and the dirt used up in both players
- [x] `ClusterMapScreen`, `WorldSelector`: Spaced Out content is not installed in this game copy (no `StreamingAssets/dlc`), unreachable in both players
- [x] `PauseScreen`: open, save with overwrite confirmation (41 saves in one run)
- [x] `PauseScreen` → Main Menu ("Save and quit" prompt) → front end with Resume Game; the front end comes back at the smaller UI scale as in the original (once, ours returned to the normal scale a few seconds later; not reproduced since)
- [x] `PauseScreen`: Save As (save slots, New Save, typed name in `FileNameDialog`), Load (colony list, colony view, Load Game → the save loads in game)
- [x] `PauseScreen`: Options (the same options screens as the main menu)
- [x] Printing pod recharge (cycle 3–4): "New Printables are available", pod side screen "Choose a Blueprint" → selection screen with Duplicants and a care package → Print → the new Duplicant arrives
- [x] `GameOverScreen` (colony lost)
- [x] `StoryMessageScreen` ("IMPERATIVE ACHIEVED" alert, Proceed → camera sequence) as the original, triggered with tools/UnityProbe; `VictoryScreen` is never opened in this build (prefab field only)
- [x] `EventInfoScreen.ShowPopup` (the Somnium Synthesizer story-trait popup) as the original, triggered with tools/UnityProbe
- [x] Timelapse capture and save preview (`Timelapser`: base and overlay cameras rendered from an end-of-frame coroutine into a render texture, `ReadPixels` + `EncodeToPNG`): the world around the printing pod as in the original's images
- [x] Camera: wheel zoom out/in, keyboard pan, Home (camera return)
- [x] Camera: right- and middle-button drag pan move the view by the same pixel offsets as the original (−200,−100 and −250,+200, the latter clamped at the world edge, measured by image registration); Ctrl+1 / Shift+1 bookmark returns to the exact view in both. ONI has no edge pan (the edge padding only clamps the camera)
- [x] Camera follow: stress meter click selects and focuses a Duplicant, Follow Cam zooms in and keeps the Duplicant centred while it walks

## Tools

- [x] Dig (`DigTool`)
- [x] Activation with tool label, filter panel and overlay: Cancel, Deconstruct, Prioritize (`PriorityScreen`), Disinfect (germ overlay), Sweep (`ClearTool`), Mop, Attack, Wrangle (`CaptureTool`), Harvest, Empty Pipe, Disconnect (compared with the original where its scripted run activated the same tool)
- [x] Move To (`MoveToLocationTool`): the target sets the "Move to" errand and the Duplicant walks there; an unreachable target keeps the tool active with "UNREACHABLE" (seen in the original)
- [x] Deconstruct (instant-build mode: the Storage Bin goes, its material drops as debris), Sweep (marker on the debris), Cancel (removes the sweep order; the filter gains Sweep & Mop / Dig Orders)
- [x] Copy Settings: Storage Bin capacity set by a slider click (9911 kg in both players) copied onto a second bin as in the original; the element filter lists only Critter Egg at that point in both, so All Standard changes nothing in either
- [x] Mop order carried out (tools/UnityProbe: a 30 kg puddle beside the printing pod, Mop tool, 40 s at speed 3): mopped and the order removed in both players
- [x] Disinfect order carried out (Storage Bin with 100000 food-poisoning germs → 0 within 120 s at speed 3) in both players
- [x] Harvest (a fully grown Mealwood beside the printing pod, marked Harvest When Ready: harvested, regrowing at 5%) and Attack (a Hatch beside the pod: killed) carried out within 120 s at speed 3, identically in both players (tools/UnityProbe)
- [x] Wrangle carried out in both players (`tools/UnityProbe`): with Critter Ranching learned, a marked Hatch is bagged within 20 s, stays bagged about 100 s on the floor and then wriggles free, as in the original
- [x] Build placement with materials: Storage Bin and a dragged Ladder from the Base menu; placement refused with "Missing resources" until the colony has the materials (as the original); Duplicants deliver and build
- [~] `BaseUtilityBuildTool`: the wire tool (Power → Wire, material list, power overlay) opens and responds to a drag as in the original; a placed wire has not been observed in either player's scripted run yet
- [x] Liquid pipe drag (plumbing overlay, pipe preview, built in instant-build mode); building rotation (O turns the Liquid Valve preview from vertical to horizontal and it is built rotated); build menu search and the grid layout with everything researched

## Overlays

- [x] F1–F11 compared with the original (same keys, same frame of play): Oxygen, Power, Temperature, Materials (`TileMode`), Light, Plumbing (`LiquidConduit`), Ventilation (`GasConduit`), Decor, Germs (`Disease`), Farming (`Crop`), Rooms (with room highlight under the cursor)
- [x] Research-gated overlays with everything unlocked (debug instant-build mode, compared with the original in the same mode): Exosuits, Automation, Shipping, and the remaining Shift+F overlays
- [x] Build menu categories that become available later (Utilities, Automation, Shipping, Rocketry) appear and lay out as in the original
- [x] `ResearchScreen` tree and `StarmapScreen` open as in the original
- [x] Overlay legends (`OverlayLegend`) shown with every overlay compared above
- [x] Overlay filter toggles: Temperature overlay modes (Absolute, Relative with its range slider, Thermal Tolerance, State Change) switch the view and legend as the original

## Simulation and persistence

- [x] Ten cycles at speed 3 with digging and building, zero exceptions (M5 exit)
- [x] Autosave each cycle; save → quit to menu → load → continue (M5 exit)
- [x] Hi-res texture bundle streaming (`StreamedTextures`): bundles load while the Supply Closet is open; the Duplicant outfit screen renders from them
- [x] A loaded colony keeps its name and its saves' place: autosaves go next to the loaded save (`cloud_save_files/<id>/Regrettable/auto_save/Regrettable Cycle 162.sav`), the same path and active-save pointer as the original. Before, this player saved loaded colonies under a new random name in `save_files`: the new-colony screen that Game's Awake creates started before Game, and its colony-name field renamed the colony and moved the save pointer; in the original Game starts first and destroys that screen. Start is now queued when activation begins, before Awake, as measured in the original (an object an Awake creates starts after its creator)
- [x] Videos decode with the engine's own VP8 decoder (a port of RFC 6386's reference decoder): every frame of the game's 18 clips matches libvpx plane for plane, on Linux and Windows alike without a codec library

## Platform

- [x] Windows (the Windows build from Steam, OpenGL on NVIDIA's driver): type-load, asset, bind and shader checks; the menu as on Linux; DeCrew and Regrettable Dimension played (28k frames at speed 3: no exceptions, no white-out, autosaves); test runs isolated from the user's Documents, LocalLow, Steam account and speakers. Frame rate on DeCrew ~42 fps with bindless textures (26 before: NVIDIA serializes draws whose vertex-stage textures change)
- [x] Windows, a new colony: New Game → Survival → Standard → Terra, world generation through the Windows `SimDLL.dll` (8.6 s), Duplicant selection, embark, intro, then cycles 1–3 at fast speed with the initial save, autosaves in the colony's own cloud folder and the timelapse snapshots: no exceptions. A tutorial video ("Digging for Resources") plays in the game with the engine's VP8 decoder
- [x] Saves across platforms: an autosave written by this player on Windows loads and plays in the original player on Linux (no exceptions)
- [x] Windows, Spaced Out with every DLC (U57: cycle 17078, 22 Duplicants, sandbox, the Steam double owning all DLC): the DLC intro video, the save loads, plays at speed 3 from cycle 17078 to 17086 with an autosave each cycle (30k frames): no exceptions

- [x] Window, input (mouse, keyboard, text), cursor images
- [x] Windows' pointer size (Accessibility > Mouse pointer > Size) scales the game's cursors as in the original: the shape Windows displays (desktop duplication) is 32x32 at size 32, 48x48 at 48 and 64x64 at 64, pixel for pixel the original's, and follows size changes while the game runs; hiding and showing it (`Cursor.visible`) and tool changes take effect without a mouse move, as in the original (`Win32Cursor`)
- [x] Clipboard: select all, copy and paste in the destination coordinates field (as the original)
- [x] Frame rate at parity with the original or above (PLAN §Status, Performance). On Windows (native NVIDIA driver, both players isolated the same way, speed 3, tools/PerfProbe): DeCrew 48–56 fps against the original's 17–20 (a game cycle every 210 s against 229–232 s; 200 s is speed 3's limit); U57 (every DLC) 17–18.3 fps against 8.3–9.5, a cycle every 219–222 s against 363–368 s
- [x] Memory at parity with the original: DeCrew (cycle 17630, base game) holds 11.9–12.4 GB resident over five cycles at speed 3, the original 12.9–14.3 GB on the same save and machine. Both grow with the colony (heap dumps a few cycles apart: new debris items and their components; the reports' notes are pruned), with no engine-side growth
- [x] Windowed → fullscreen (borderless) switching at runtime
- [x] Resolution changes at runtime (window resized)
- [x] Exclusive fullscreen (`-window-mode exclusive`, or a script request): as in Unity's Linux player it gives a fullscreen window, and the game reports FullScreenWindow like the original
- [x] Focus loss and regain: switching X focus to another window and back delivers OnApplicationFocus(false/true) (held keys released), no exceptions
- [x] Audio output: FMOD initialises and mixes through ALSA (verified with a private ALSA configuration whose default device writes PCM to a file through a null sink: the main-menu music is present). Without libpulse or an ALSA device, FMOD runs without output in both players
- [x] Audio content compared with the original, recorded in real time through a private paced ALSA sink (`scripts/audio-sink/record.sh`): main-menu music matches the original's (10 ms RMS envelopes correlate 0.97, aligned waveforms 0.90 over 30 s, level ratio 0.9997); colony ambience on the same seed has the original's level (RMS 0.035–0.040 against 0.036–0.043), stereo balance and spectrum (band energy shares within a few percent)

## Engine semantics

- [x] Lifecycle and timing measured in both players with `tools/UnityProbe` (a mod): Destroy timing (objects and direct children disabled at once, deeper ones at destruction, deepest first), objects awaiting destruction not re-enabled, request order of destruction, component Destroy, `Time.frameCount`/`renderedFrameCount`, startup frames (mods load at frame 2, frontend from frame 3), `yield return null` resuming the next frame, Start and LateUpdate in the same frame for behaviours created by coroutines: output identical apart from a time-based wait landing on another frame at another frame rate
- [x] Values (tools/UnityProbe): vector/colour/matrix formatting, maths, JsonUtility (see ARCHITECTURE D10), hierarchy queries, SetParent, Instantiate callback order, SendMessage, root sibling indices — identical
- [x] Main-menu UI layout (tools/UnityProbe UiDump): every RectTransform's rect and world corners and every text's content, size and preferred size across all canvases, compared with the original at 1024×768 — identical apart from TextMeshPro auto-size and one preferred width differing by 0.1 (float rounding)
- [x] In-game HUD layout (UiDump once the game clock has run 120 frames after embarking): 3415 RectTransforms of the overlay canvases (HUD, management screens, research tree) compared with the original — differences only 0.1-pixel rounding, the hover card's state at the pointer and game-state texts
- [x] Local mods load (KMod, `Assembly.LoadFrom`, Harmony patches applied on CoreCLR) and stay enabled across launches, with the version stamp the original writes (ARCHITECTURE D3; OniMcp checked over three consecutive launches)
- [x] Crash screen (`KCrashReporter`, opened by a mod's `Debug.LogError` at the main menu): identical to the original at 1024×768, including the text and buttons running past the right edge
- [x] Large save (DeCrew, base game, cycle 17628, 138 MB): loads without exceptions to the same screen as the original (about 1 min here, 3 min 50 s in the original); frame rate there 26.7 fps against 25.8
- [x] Large save played on: DeCrew at speed 3 for five cycles in both players: no exceptions, all 15 Duplicants alive and eating, the same notifications as the original at cycle 17633 (needed the D1 fix: before it the state-machine parameters of the save loaded at defaults and the colony starved); the autosave this player then wrote (cycle 17634) loads in the original player without exceptions, its Duplicants finding food and eating
- [x] Liquids (WaterCubes, drawn from a command buffer after copying the render targets): a water-filled room of Cool Abyss renders as in the original (the buffer's blits used to leave their matrices for the liquid draw, which then fell outside the view: no liquid was drawn at all)
- [x] A colony from an old build (Cool Abyss, build 468841, cycle 596, 20 Duplicants): played at speed 3 in both players, no exceptions, no deaths, at cycle 598 food 8.72 M against 8.71 M kcal, crops 39/65 against 40/65, the same notifications
- [x] Regrettable Dimension (cycle 161, 14 Duplicants, a save posted as a food bug): plays in both players without exceptions, with starvation in both. Which Duplicants starve is not reproducible in the original itself: from its own cycle-166 autosave, two runs lost Otto, Harold, Gossmann and Ada, then Frankie, Ari and Stinky; this player lost Gossmann, Bubbles, Frankie, Nisbet and Meep, with every Duplicant's calories identical to the original's until the first meal choices. A white-out of the background found here came from the vertex-array change: constant attribute values (the background planes' colour) were cached across draws although a draw leaves them undefined; set again after each draw, five cycles show none (it appeared in every earlier run)
- [x] An old-build save (The Swanky Planet, build 466292, cycle 7385, 12 Duplicants): the three update-notes dialogs as in the original, then played at speed 3 in both players: no exceptions, no deaths, at cycle 7387 food 11.49 M against 11.50 M kcal, crops 73/133 against 74/133, the same notifications
- [x] A third-party mod built on PLib (OniMcp 0.2.5, an in-game MCP server: Harmony patches, PLib options, `HttpListener`, `ScreenCapture`): its patches verify healthy, its server answers, it loads a save, reads colony and Duplicant state, runs the game and takes screenshots, with no exceptions (PLib needed `RectTransform.SetInsetAndSizeFromParentEdge`)

## Debug mode

- [x] Dev tools (debug mode, Ctrl+\`): `ImGuiRenderer` creates its target (`RenderTextureDescriptor`, `GetTemporary`) as in the original; U60's Linux `cimgui.so` (now in `Plugins/x86_64`) exports `SetKAssertCB`, which U59's lacked (both players then crashed in `igRender`), so the dev tools work on Linux in both players. ImGui draws through the command-buffer path (`DrawMesh`, scissor rectangles, view/projection matrices, `Graphics.ExecuteCommandBuffer`); ours matches the original pixel for pixel on OpenGL and Vulkan (menu bar, warning, StringsTable, scrolling, typing; u60-inventory.md)
- [x] Instant build (Ctrl+F4), Dev Life Support, Dev Generator in the build menu

## M9: this project's UI packages against the shipped ones

These results come from differential runs, since retired, that ran each scenario with Unity's
shipped packages and with this project's (`src/UnityEngine.UI`, `src/Unity.TextMeshPro`,
`src/Unity.Timeline`, written from `docs/cleanroom/`). The host now loads only this project's;
new comparisons are against the original Unity player (`scripts/e2e.py --player both`). The
uGUI results below were taken with the shipped TextMeshPro on both sides; the TextMeshPro
results with all three of ours. A difference was accepted when a run of the same scenario with
the shipped package on both sides showed it too.

- [x] `menu`, `options` (both legacy dropdowns opened and closed, the UI scale slider, the audio
  sliders and a checkbox), `management` (Priorities with the wheel, Vitals, Consumables): no
  differences except the main menu's animated DLC glow (`BGGlow`), which differs between two
  shipped runs as well.
- [x] `management2` (Skills, Research, Schedule): the research tree's pan and zoom position, which
  the game animates with real time; two shipped runs differ there too.
- [x] `colony` (DeCrew, Database search): the search field's TextMeshPro caret, which blinks with
  real time (and shifts the draw order of everything after it), and notifications whose arrival
  depends on the simulation's real-time pace.
- [x] `newgame`: what the game randomizes (the initial coordinates before they are typed, the
  story shown first, the Duplicants, their name labels) and the world generation's progress at a
  given frame; every difference also appears between two shipped runs.
- [x] TextMeshPro and Timeline, all three packages ours: `menu`, `options-dropdown`,
  `options-audio`: the DLC glow only. `naming`: the glow and the notification timing seen with
  uGUI. `codex` and `colony` (frames 1550–2200): two research labels (Farm Station, Liquid and
  Gas Cargo) whose preferred width is 0.01 off, float rounding; and in `colony` the Database
  search caret's blink.
- [x] Frame rate with all three of ours (Windows, PerfProbe, runs alternated): DeCrew
  35.3–36.3 fps against 32.6–32.9 with the shipped packages; U57 11.7–12.6 against 12.1–12.3.
- [~] 2D colliders never block a `GraphicRaycaster` (blocking objects TwoD/All): the engine runs no
  2D physics queries. The shipped package cannot do it on this engine either.

## Deviations

- `SystemInfo.supportsComputeShaders` is false (the engine implements no compute shaders; the game reads it only for its hardware metrics and an unused image-effect check). `SystemInfo.graphicsMemorySize` is the dedicated video memory (the original printed a slightly lower figure here, likely the memory available at the time).

- Floating-point results are not bit-exact (out of scope, ARCHITECTURE §5).
- Runtime deviations inherent to CoreCLR (ARCHITECTURE §2.1).
- `ScreenCapture.CaptureScreenshot` with superSize > 1 (debug screenshot keys only): open item, PLAN.
