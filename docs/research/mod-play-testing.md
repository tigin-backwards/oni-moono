# Play-testing Workshop mods: a strategy for the "Played" column

Status: proposal, not implemented. Written 2026-10-22 without running either player (a
performance measurement was running). Numbers marked *estimate* are to be measured in the pilot.

## 1. Summary (the 10-minute part)

**Problem.** `docs/MOD_COMPATIBILITY.md` has 505 rows. 368 are Loads ✅ / Colony ✅ / Played ⬜,
13 are Played ✅, 19 are ❌ upstream, 105 are untested (translations, onimaxxing flags). In the top
200 there are 141 mods at ✅ ✅ ⬜. Loads and Colony only show that nothing new lands in the log
during 90 s of U57. Today's issues show what that misses:

- #6, Blueprints Expanded's UI renders wrong. The mod is marked Played, but that verdict came from
  PerfProbe's `blueprints.txt` mode, which calls `CreateBlueprint`, `LoadBlueprint` and
  `UseBlueprint` by reflection. That path never opens the mod's screens.
- #5, Duplicant Stat Selector's buttons do nothing. This is an interaction bug, and the log is
  clean.
- #4, the shader's reveal and camera-distance falloff draw wrong. This is a rendering bug, and the
  log is clean too.

**Recommendation.** Grow `scripts/e2e.py` into the mod play-tester. One YAML spec per mod starts
from a small fixture save with the mod installed and runs in both players: the original first, to
prove the spec works, then ours. The spec drives the mod the way a player does, and the verdict
has three parts:

1. **Outcome checks** on game state, through OniMcp and a new state probe.
2. **Interaction checks.** The runner clicks the mod's buttons, toggles and dropdowns with real X11
   input (XTEST) on each player's Xvfb display. It then asserts both the UI's response and the
   effect on game state. Calling a mod's methods by reflection is allowed only for setup, never
   for the thing under test. That is the lesson of #6.
3. **Differential checks** against the original player:
   - log signatures (as today);
   - a structural dump of the mod's screen, diffed with `ui-diff.py`;
   - a cropped, masked screenshot of that screen, compared pixel by pixel.

   Both players reach the same state because they load the same fixture, pause at once, and take
   every UI shot before any simulation time passes.

**New pieces:**
- **`tools/ModTriage`**: a metadata scanner, a sibling of ApiCheck. It reads a mod's DLL and
  assigns its classes and a `touches_ui` flag.
- **`tools/E2eDriver`**: our own small test mod, loaded beside OniMcp in both players. It finds UI
  elements and reports their screen rectangles, checks raycast hits, dumps UI subtrees, and reads
  game and mod state by reflection.
- **XTEST input in `e2e.py`**, through ctypes on the installed `libXtst.so.6`. No new package is
  needed.
- **Per-lane ports and mod installation in `e2e.py`**, so several specs can run in parallel.
- **Mask and tolerance options** in `ui-diff.py` and in `qa-tools.py image`.

OniMcp stays unmodified and does the gameplay work: digging, building, running the game, saving,
user-menu buttons, side screens, management screens, overlays and screenshots. It deliberately
takes no coordinate input, and it only allows a fixed list of UI actions. So it cannot press a
mod's own buttons, which is why E2eDriver and XTEST are needed.

**Pilot (7 mods, Section 8):**

| Mod | Classes | Why it is in the pilot |
|---|---|---|
| Blueprints Expanded | S, T, K, O | issue #6 |
| Duplicant Stat Selector | M, S | issue #5 |
| Research Queue | K on a vanilla screen | a keybind on a game screen |
| Sweep By Type | T, S, O | tool, PLib screen, PLib options |
| Airlock Door | B | a mod building |
| Bigger Camera Zoom Out | V | camera and render, close to #4 |
| Full Miner Yield | P | passive tuning |

**Effort (estimates):**
- **Infrastructure:** about 8–10 working days:
  - ModTriage: 1 day
  - e2e.py extensions: 2–3 days
  - E2eDriver: 2–3 days
  - diff tooling: 0.5 day
  - fixture ladder: 1 day
  - calibration of thresholds on vanilla screens: 1 day
- **Pilot:** 3–5 days, mostly spent on #5 and #6 themselves.
- **Rollout to the top ~150 testable mods:** about 2–3 weeks part time.
  - Authoring (agent drafts, a person reviews): passive or option-only mods about 15 min each;
    buildings and tools 30–60 min; mods with their own screens 1–3 h. Total about 80 agent-hours
    plus about 20 h of review.
  - Machine time: about 20 min of lane time per mod (one original run plus two of ours). With 4
    lanes the whole set is about 2–3 nights.

**Decisions needed from you (details in Section 10):**
1. Should rows marked Played without driving the UI be re-checked or demoted? This starts with
   Blueprints Expanded, while #6 is open.
2. Should "Played" require UI checks for every UI-touching mod, as proposed? Should there be a
   "Played with known differences" state (⚠️) for small differences linked to an issue?
3. E2eDriver as a new test mod (proposed), or more modes in PerfProbe?
4. XTEST via ctypes (proposed, nothing to install), or install `xdotool`?
5. Should `ws-mods.py` and `modtable.py` move from `out/local-tools` into `scripts/`, so the
   committed runner can install mods and regenerate the table?
6. What evidence gets committed? Proposed: specs and a `results.json`, but no images.
7. Parallelism budget: 4 lanes proposed. Mod specs never run while a perf measurement runs.

## 2. What "Played" should mean

Proposed definition, to replace line 11 of MOD_COMPATIBILITY.md:

> **Played:** a spec drove the mod's features on a fixture save in the original player and in
> ours. Its outcomes held in both, ours logged nothing the original did not, and for a mod with
> its own UI, its screens match the original's in structure and in a masked screenshot, and its
> buttons do the same thing.

A mod is Played ✅ when its spec passes:
- once in the original player, which validates the spec and blesses the references;
- twice in ours (`--repeat 2`), against the original's references.

## 3. Triage: classes and flags

### 3.1 Classes

A mod can have several classes. The classes decide which spec template applies and how much the
spec costs to write.

| Class | Meaning | Typical surfaces | Template cost |
|---|---|---|---|
| **P** passive | tuning, AI, chore or sim behaviour; no new UI or input | Harmony patches on sim or chore code | low |
| **B** building | adds buildings (`IBuildingConfig`, kanims in `anim/`) | build menu entry, placement, side screen, function | low–medium |
| **T** tool | adds or changes a drag or interface tool (`DragTool`, `InterfaceTool`, `ToolMenu` patches) | toolbar entry, drag, filter menu, hover card | medium |
| **S** screen | its own screens, dialogs or side screens (`KScreen`, `KModalScreen`, `SideScreenContent`, PLib `PDialog`, Sgt_Imalas `FScreen` from asset bundles, IMGUI `OnGUI`) | opening, layout, buttons, text input | high |
| **K** input | keybinds or modifier-clicks (`PAction`, `KInputController` modifiers, `GetKeyDown`, `BindingEntry`) | key presses, shift-click on vanilla UI | medium |
| **O** options | settings only (PLib `POptions`, `[Option]` attributes) or settings beside other features | Mods screen → Options dialog, `config.json` | low (one shared template) |
| **V** visual | rendering, camera, overlays, shaders, textures (`CameraController`, `OverlayModes`, `SimDebugView`, materials and shaders, texture packs) | world view, overlay legend | medium (needs paused-frame shots) |
| **M** menu / new game | acts before a colony exists: new-game flow, Mod Manager, world or duplicant generation screens | main menu, `MinionSelectScreen`, Mods screen | high |
| **X** no code | translations (a strings.po; played through the language prefs, see "Translations" in section 10), multiplayer sessions needing two Steam accounts (not testable here) | main menu, options, a colony's screens (translations) | low |

**Flags** (independent of class):
- `touches_ui`: the mod's own code references UGUI (`UnityEngine.UI.*`), `TMPro.*` or `LocText`,
  subclasses `KScreen` or `SideScreenContent`, defines `OnGUI`, or loads UI prefabs from an
  `AssetBundle`. If it is set, UI verification (Section 5.3) is mandatory.
- `asset_bundle_ui`: the mod loads screens from a bundle in `assets/` (Sgt_Imalas' UtilLibs mods:
  Blueprints Expanded, DSS and others). These are the riskiest for us: deserialised prefabs, and
  layout we did not write.
- `plib_options`: the mod has a PLib options dialog, which the shared O template covers.
- `user_menu`: the mod adds buttons to the selected object's menu (`OnRefreshUserMenu`). OniMcp
  can press these already.
- `needs_late_game`: the feature needs content that only a late save has (rockets, space). Such a
  spec runs serially, on its own (Section 6.3).

### 3.2 Assigning classes cheaply: `tools/ModTriage`

Implemented. Its table is `docs/research/mod-triage.md`, which also says where the tool departs
from this section (shared helper code kept apart, two refinements to the target mapping).

This is a new console tool beside `tools/ApiCheck`, using `System.Reflection.Metadata` in the same
way: it reads metadata only and never loads the DLL. For each mod (`out/ws/ID/*.dll`, or the
newest `archived_versions/*` that matches the game) it emits one JSON record:

- **Own types.** It excludes merged libraries: PLib, which is merged into 75 of the 148 loading
  top-200 mods, sometimes renamed into the mod's namespace, so it is recognised by type-name
  fingerprints such as `PDetours`, `PPanel`, `POptions`, `PUIElements` and `PRegistry`. It also
  excludes Harmony and Newtonsoft. UtilLibs stays in, because it *is* those mods' UI. Without this
  step every PLib mod looks like an S/T/K/V mod: a string scan run while writing this flagged 114
  of the 148 loading top-200 mods as UI-touching.
- **Base types of own types:** `KScreen`, `KModalScreen`, `SideScreenContent`, `DragTool`,
  `InterfaceTool`, `IBuildingConfig`, `UserMod2`, `FScreen` and so on.
- **Type and member references from own method bodies**, grouped by namespace: `UnityEngine.UI`,
  `TMPro`, `UnityEngine.GUI*`, `AssetBundle`, `Shader`, `Material` and `Camera`.
- **Harmony patch targets.** These come from `[HarmonyPatch(typeof(X), "M")]` attribute blobs, and
  from `ldstr` and `ldtoken` next to `Harmony.Patch` and PLib `Patch` calls. Each target is
  mapped to a class by its declaring type: `*Screen`, `*SideScreen` and `DetailsScreen` → S;
  `ToolMenu`, `*Tool` and `PlayerController` → T; `CameraController`, `OverlayScreen`,
  `SimDebugView` and `*Renderer` → V; `KInputController`, `GameInputMapping` and `KInputHandler`
  → K; `MinionSelectScreen`, `NewGameFlow*`, `ModsScreen` and `MainMenu` → M; everything else →
  P.
- **`mod.yaml` / `mod_info.yaml`:** `supportedContent`, `minimumSupportedBuild` and `APIVersion`.
  Also `anim/` (kanims, which suggest B), `assets/` (bundles), and translations-only mods with no
  DLL (X).

**Output:** `out/modtriage/ID.json`, plus a table with one line per mod (ID, classes, flags, patch
targets by class). An agent then confirms the classes from the decompiled mod (`ilspycmd`, which
is installed and needs `DOTNET_ROOT=$HOME/.dotnet`, into `out/local-tools/moddecomp/ID/`, never
committed). It writes a short **mod card** with:
- what the mod does;
- how a player reaches each feature (button, hotkey, tool, side screen);
- what changes when the feature is used.

The card becomes the spec's header comment and the input for the spec template. Effort: about 1
day for the tool; then seconds per mod, plus 5–10 minutes of agent reading per mod for the card.

### 3.3 Rough expected distribution in the top 200

These are estimates from a string scan plus the titles. ModTriage will replace them.

| Group | Count |
|---|---|
| X (translations, flagged by onimaxxing) | about 45 |
| ❌ upstream | 9 |
| Already Played | 7 |
| P only (Full Miner Yield, No 'Long Commutes', Double Sweeper Range, …) | about 25–30 |
| B, possibly with O (Airlock Door, Insulated Door, Tiny Door, Steel Ladder, …) | about 35 |
| T or K (Sweep By Type, Smart Drag Tool, Mass Move Tool, Research Queue, Pipette, …) | about 20 |
| S or M (DSS, DGSM, Blueprints, Mod Manager, Customize Geyser, Critter Inventory, …) | about 30 |
| V (Bigger Camera Zoom Out, GasOverlay, Show Building Ranges, MaterialColor, …) | about 15 |

## 4. Harness: what drives the game

### 4.1 What OniMcp already does (unmodified; its licence forbids vendoring or changes)

From its tool surface (out/local-tools/onimcp, `server_control domain=catalog action=manifest`):

| Need | OniMcp |
|---|---|
| load a fixture, pause, speed, step game time | `game_control domain=launch/save/speed` (used by e2e.py) |
| dig, sweep, mop, deconstruct, priorities | `orders_control` |
| place buildings, mod buildings included | viewport-map tokens via `world_editor edit` (e2e.py `build`); mod prefab IDs are expected to work, to be verified in the pilot |
| building settings: sliders, thresholds, doors, logic, access, receptacles, dispensers, suit lockers, telepad | `building_control domain=config` / `domain=side_surface` (vanilla side screens) |
| user-menu buttons, mods' included | `building_control domain=side_surface kind=button action=list/press` |
| management screens, find/help, pause menu | `game_control domain=ui uiDomain=action action=open_management/trigger` (whitelist) |
| overlays, camera, screenshots | `navigation_control action=switch_view/set_view/focus_cell/screenshot` |
| colony facts | `oni://colony/status`, `oni://colony/summary`, rooms, inventory, `read_control` |
| new-game flow | `game_control domain=launch` new-game stages |

**What it cannot do:**
- click arbitrary UI;
- press keys or modifier-clicks;
- open a mod's own screen;
- read a mod's UI state or mod state;
- set the camera beyond what the game itself allows.

These are exactly what classes S, T, K, M and V need.

### 4.2 New: `tools/E2eDriver` (our test mod)

**Why a separate mod.** PerfProbe is a perf instrument whose behaviour is switched by trigger
files and whose output is scraped from the log. A driver needs request and response, so it is
cleaner on its own. It still borrows PerfProbe's patterns: `FindType`, Traverse, the canvas walk
of its measure mode, and hiding canvases for world shots.

**Transport.** A JSON-lines TCP server on `127.0.0.1:PORT`, where PORT is 8790 plus the lane.
Requests are handled on the main thread in `Update`, one per frame.

**Commands:**
- `ui.find {path?, text?, type?, under?}`: matching active elements, each with its hierarchy path,
  screen rect in pixels (from `RectTransform.GetWorldCorners` and the canvas camera, origin
  top-left to match X11), active state, `Selectable.interactable`, `Toggle.isOn`, text, and the
  **top raycast hit** at its centre (`EventSystem.RaycastAll`). The last one tells "this button is
  covered by another graphic" apart from "the button does nothing".
- `ui.dump {root}`: the subtree in the `--dump-ui` line format. It records only what can be read
  through public members in both players: path, rect, corners, Graphic colour, raycastTarget,
  text, isOn, interactable, slider and scroll values, LayoutElement sizes. CanvasRenderer geometry
  hashes are dropped. `ui-diff.py` then compares the original's dump with ours.
- `ui.wait {path, cond, frames}`: waits until an element exists or reaches a state.
- `world.to_screen {cell}` and `screen.to_cell {x, y}`: used for world clicks and drags.
- `state.get {expr}`: a small read-only path language over static members and the
  `Component`s of a selected object, for example `Research.Instance.queuedTech.Count`,
  `type:SetStartDupes.… .Instance.field`, or `selected:MinionIdentity.GetComponent<Traits>…`.
  Values come back as JSON.
- `camera.get/set {pos, ortho}` and `canvas.hide/show`, for V-class shots.
- `setup.call {type, method, args}`: reflective calls, **only allowed in a spec's `setup:`
  block** (for example to seed a blueprint file). The runner rejects it in `steps:`.
- `seed {n}`: sets `UnityEngine.Random.InitState` and the game's KRandom seeds before a random
  screen opens (Section 5.4).

**Same code in both players.** It targets net471 like PerfProbe, and loads in the original as a
local mod.

### 4.3 New: real input through XTEST

`e2e.py` gains an `Input` class: ctypes on `libXtst.so.6` and `libX11.so.6`, both present, with
`XTestFakeMotionEvent`, `XTestFakeButtonEvent`, `XTestFakeKeyEvent` and `XFlush` on the lane's
`DISPLAY`.

- **Why not our host's `--input FRAME:click:x,y`:** it exists only in our player and is scheduled
  by frame, not by state.
- **Why X11:** both players read X11. The original's UnityPlayer does, and ours does through GLFW
  (ARCHITECTURE §3.6). So the whole path is tested the same way in both, including the event
  system's raycasting and the mod's pointer handlers. DSS's `FButton` is a custom pointer handler,
  the likely seat of #5.
- **Keys:** a `KKeyCode` → X keysym table (letters, digits, F-keys, Shift, Ctrl, Alt, Escape,
  Return, arrows). Modifier-clicks are key-down, click, key-up with a frame or two between.
- **Clicks:** each one first does `ui.find` and checks that the top raycast hit is the element or
  one of its children. If not, the step fails with "occluded by PATH", and the spec does not click
  blind. Then it moves the pointer, waits 2 frames (hover), presses, waits 2 frames, and releases.
  Drags are a series of motion events between `world.to_screen` points.

### 4.4 e2e.py changes

**Parallel lanes.**
- The port is fixed at 8788 today, and it clashes with the worldmaxxing-local server. Each lane
  writes its own `OniMcpConfig.json` Port (8800 + 10 × lane) and its own E2eDriver port.
  `xvfb-run -a` already gives each lane its own display.
- `--lanes N` runs specs from a queue.
- **Machine lock:** before starting, the runner waits while `ps` shows a perf job, the way the
  sweep scripts do with `pgrep -x`. It also takes `flock out/e2e/.lock` per lane.

**Installing mods.**
- A spec's `mods: [3468585385, …]` installs Workshop builds as Local mods into the run home,
  using `scripts/ws-mods.py profile`'s logic (decision 5), with OniMcp and E2eDriver added.
- `options: {staticID: {Field: value}}` writes a mod's PLib `config.json` before launch.

**New step kinds** (the existing ones stay):
```yaml
setup:                                   # before steps; reflection allowed here only
  - call: {tool: ..., args: ...}         # OniMcp
  - driver: {setup.call: {...}}
steps:
  - open: {management: research}         # OniMcp whitelist; or {key: R}; or {user_menu: "Label"}
  - click: {ui: "*/ResearchScreen/**/Tech_FarmingTech/Button", mods: [Shift]}
  - click: {text: "Quick Learner", under: "UnityDuplicitySelectionScreen"}
  - toggle: {ui: "...", to: false}
  - dropdown: {ui: "...", option: "Sandstone"}
  - type: {ui: ".../Input", text: "e2e-blueprint", enter: true}
  - key: {code: "Shift+B"}
  - drag: {from: [110, 207], to: [118, 209]}       # world cells
  - tool: {toolbar: "SweepByType"}                 # a ToolMenu entry by its tool name
  - expect_ui: {"**/ToReplace/CurrentlyActive/Label": {text: "Quick Learner", active: true}}
  - expect_state: {"Research.Instance.queuedTech.Count": ">=3"}
  - ui_dump: {name: selector, root: "**/BlueprintSelector"}
  - shot: {name: selector, crop: "**/BlueprintSelector", mask: ["**/Portrait", "**/DateLabel"]}
  - world_shot: {name: zoomed, ortho: 45, at: [128, 210], hide_ui: true}
```
`shot`, `ui_dump` and `world_shot` record artifacts. They are compared with the original's
references after the run (Section 5).

### 4.5 Fixtures: a ladder of small saves

Specs need content: research done, materials, power. A late save is too big to load in parallel
(U57's player uses over 10 GB). The plan is to build a ladder with `e2e.py --bless` in the
original player:

1. `landing` (exists, 1.3 MB);
2. `early-base` (spec exists: dug out, outhouse, research station, generator, cot);
3. `mid-base`: research through Tier 3, power, pipes, refined metal and steel in storage, debris
   on the floor, a telepad ready to print, a few rooms;
4. `spaced-out-mid`: `2minnow.sav` (30 MB, the Colony sweeps' save) for rockets and cluster
   features.

`mid-base` is grown by specs from `early-base`, partly with `setup:` sandbox calls through
E2eDriver, for example to grant research and materials. Every fixture is blessed in the original,
and so is identical for both players. U57 is used only for `needs_late_game` specs.

## 5. Oracle: deciding pass or fail

### 5.1 Principles

- **Outcomes and invariants, not trajectories.** For example "N construction sites exist", "the
  trait is on the printed duplicant" or "only sandstone was swept". Never "the duplicant walked
  this path".
- **The original runs first.** A spec that fails in the original is a broken spec, or a mod
  broken upstream (❌), never a verdict on us.
- **Every UI step takes place in paused time.** Time advances only in explicit `run` steps. All UI
  evidence is captured before the first `run`, or after an `expect` that pins the state.

### 5.2 Per-player checks (in each player on its own)

- Every step succeeds: no SpecFailure, no click on an occluded element, no `ui.wait` timeout.
- Every `expect`, `expect_ui` and `expect_state` holds.
- The player is alive at the end, with no `[ERROR]` lines from E2eDriver itself.

### 5.3 Differential checks (ours against the original's references)

| Evidence | How it is compared | Fails when | Ignored |
|---|---|---|---|
| Log signatures | `qa-tools.py signatures`; ours minus the union of the original's runs (as e2e.py does now) | any new signature | signatures in both players |
| Mod log lines | lines the spec names in `require_log:` (for example PLib's options-saved line, the mod's own "blueprint saved") | missing in ours but present in the original | anything else |
| UI structure (`ui_dump`) | `ui-diff.py ORIG OURS --tolerance 2 --mask-text REGEX…` (new options: rect fields compared within ±2 px; text fields matched after masking numbers or dates the spec names) | an element only in one player; `active`, `interactable` or `isOn` differs; text differs after masks; an element off-screen or 0-sized in ours only; rect off by more than 2 px or size off by more than 2 % | sibling order of dictionary-built lists (sorted by path), masked text |
| UI pixels (`shot`) | crop both players' screenshots to the element's rect from `ui.find` (same 1280×720, same UI scale); fill mask rects; `qa-tools.py image` (fraction of pixels whose channel difference exceeds 40) | more than **2 %** of pixels differ for static screens; more than **4 %** where text is dense, set per spec after calibration | masked regions |
| World pixels (`world_shot`, class V) | the same, on a paused frame right after the fixture loads, camera set by E2eDriver, UI canvases hidden | more than **5 %** (sim noise is low because the frame is paused immediately; the threshold is calibrated, see below) | masked: Duplicants, critters, animated buildings (kanim phase) |
| Interaction effect | the same `expect_ui` and `expect_state` must hold in both players | holds in the original but not in ours (#5's case) | – |

**Calibration first.** Before any mod spec, measure the noise floor: run the pilot's screens with
**no mod**, on vanilla screens such as the research screen, the immigrant screen, the build menu
and the options dialog. Do it twice in each player and take the 95th-percentile difference of
original against original, and original against ours. A threshold is set at about twice the
original-against-ours floor of a comparable vanilla screen. Known structural differences that are
not mod bugs (font hinting, TMP SDF edges) are recorded once as global masks or ignores.

### 5.4 Getting both players into the same state

- **Same fixture, same resolution** (1280×720 windowed), same `kplayerprefs.yaml`, same mod
  versions, same options files.
- **Paused right after the load** (e2e.py already pauses). UI steps never unpause.
- **Same camera:** E2eDriver `camera.set` before any shot.
- **Random content.**
  - DSS's immigrant cards, the care packages and the names come from the game's random numbers.
    E2eDriver `seed` sets the same seeds in both players before the screen opens. Where a value
    still differs between the two runtimes (System.Random on Mono and on CoreCLR are both
    deterministic for a given seed, but the game may mix in time), the spec masks it in pixels
    and text, and asserts relations instead of values, for example "the label now names the trait
    that was clicked".
  - Portraits are kanims, so they are always masked.
- **Hover and tooltips:** the pointer is moved off the UI (a fixed corner) before every `shot`,
  unless the shot is of a tooltip. Then the pointer is placed on the element's centre and the shot
  waits 10 frames.
- **Animation:** screens that open with a tween are shot after `ui.wait` reports the rect stable
  for 5 frames.

### 5.5 What a difference means

- **Fail:** a missing or extra element, a wrong active, interactable or toggle state, an
  interaction that has an effect in the original but not in ours, a new log signature, or pixels
  over the threshold on an unmasked region.
- **Investigate, then ⚠️ or fail:** pixel differences above the noise floor but under the
  threshold, concentrated in one element (for example a mis-sized icon). The runner writes the
  diff mask PNG and the worst element by overlap, so a person can judge in under a minute.
- **Ignore:** differences in masked regions, or ones explained by a documented engine difference
  (MOD_COMPATIBILITY's "Known differences", for example rounded LineRenderer caps). Each ignore
  names that entry.

## 6. Scale

### 6.1 Authoring

**Spec templates per class** (`tests/e2e/mods/_templates/`):

| Template | Steps |
|---|---|
| **P** | apply the trigger (dig, build or run) and assert the tuned quantity against the original's value with a tolerance. Often a single number, for example debris mass from a dig. |
| **B** | `build` the prefab → `run until` built → select it → `shot` its side screen → exercise its function with `expect` |
| **T** | `tool` from the toolbar → `shot` and `ui_dump` its filter menu → choose an option → `drag` → `expect` orders only on the matching targets |
| **K** | `key` or modifier-`click` → `expect_ui` and `expect_state` |
| **O** (shared) | open the Mods screen → the mod's Options button → `ui_dump` and `shot` of the dialog → toggle the first bool and set the first number → OK → `config.json` changed (the runner reads the file) → reopen → values shown |
| **S/M** | open → `ui_dump` and `shot` → walk every button, toggle and dropdown in the mod card's list, asserting each one's response → close |
| **V** | `world_shot` at two zoom levels and with the mod's overlay on |

**Agent-generated drafts.** Per mod, an agent is given:
- the ModTriage JSON;
- the decompiled source;
- the class templates;
- the E2eDriver `ui.find` output from a menu-only run, so it uses real hierarchy paths.

It writes the mod card and a draft spec, then runs the draft **in the original only** when a lane
is free, and iterates until the draft passes there. A person reviews the card and the spec
(5–15 min). The review checks that the spec exercises what players use, and not trivia.

**Cost per mod** (agent time; human review on top):

| Class | Agent | Review |
|---|---|---|
| P | 10–20 min | 5 min |
| O only | 10 min | 2 min |
| B | 20–40 min | 5–10 min |
| T, K | 30–60 min | 10 min |
| V | 30–60 min | 10 min (thresholds) |
| S, M | 1–3 h | 15–30 min |

Investigating failures is not included. Each real bug, such as #5 or #6, is its own engine task.

### 6.2 Runtime

Per player run *(estimate, measured in the pilot)*:

| Stage | Time |
|---|---|
| start to the menu with 3 mods | 60–90 s |
| load a small fixture | 20–60 s |
| UI steps | 1–3 min |
| `run` steps | 1–5 min at speed 3 |
| total | 5–10 min |

Per mod that is one original run plus two of ours, about 20–25 min of lane time.

**Memory.** A small-fixture instance is about 3–5 GB *(estimate)*.
- **4 lanes:** at most about 20 GB, well inside 62 GB with room for the build and the OS. 6 lanes
  is the hard maximum. **Never** U57 in parallel: `needs_late_game` specs take all lanes (one
  instance, which uses over 10 GB).
- **CPU:** 16 cores; each instance uses 2–4 busy threads, so 4 lanes leave headroom.
- **GPU:** the shared Mesa d3d12 adapter is a possible bottleneck; the pilot measures frame time
  with 1, 2 and 4 lanes.

**Batching.** P and O mods that do not conflict are batched about 10 per session: one spec file
with one section per mod, and an automatic bisection on failure. This divides their start-up cost
by 10. S, M, T, K and V mods run one per session, because screens and input interfere.

**Throughput.** About 110 non-batched mods × 22 min ÷ 4 lanes ≈ 10 h, plus about 40 batched mods
≈ 1 h. A full first pass is **one or two nights**. A re-run of ours only, against stored
references, takes about two-thirds of that.

### 6.3 Ordering

Work down by rank. Within a rank band of 25, do the classes that unlock templates first: O, B
and P, then T and K, then S and M. Mods with open GitHub issues come first, whatever their rank.

## 7. Recording results

**Committed** (decision 6):
- `tests/e2e/mods/ID-slug.yaml`: the spec with the mod card as its header.
- `tests/e2e/mods/results.json`, one record per mod: Workshop ID, the mod's `time_updated` and DLL
  SHA-256, the game build, our commit, date, verdict (`played`, `played-with-differences`,
  `fails`, `upstream`), new signatures, worst pixel fraction per shot, ui-diff difference count,
  and a one-line note.

*Implemented* (tests/e2e/README.md, "The results record"): one file per spec,
`tests/e2e/results/SPEC.json`, rather than one `results.json`, so batches in parallel worktrees
never conflict. A record holds the mod IDs, the verdict (`played`, `warning`, `failed`, or
`pending` while runs are missing), the known findings a ⚠️ cites (`tests/e2e/known-findings.yaml`),
a note, the commit, the mods' DLL hashes and each run's summary. `upstream` stays in
`modtable.py`'s `broken` set.

**Kept locally** (not committed):
- `out/e2e/SPEC/RUN_ID/PLAYER-N/`: logs, screenshots, dumps, diff masks.
- `out/e2e-refs/ID/BUILD/`: the original's blessed references (dumps, cropped shots, signatures,
  expect values). These are what re-runs of ours compare against. They are reproducible from the
  committed spec plus the original player.

**The table.** `scripts/modtable.py` reads `results.json` instead of its hand-written `played`
dict.
- Played ✅ plus a note naming what was exercised, for example "trait and attribute buttons, care
  package editor; screens within 1.1 %".
- ⚠️ plus a note and an issue link, for `played-with-differences`, if decision 2 adopts it.
- ⬜ with "spec fails: …" while we are broken.

**Re-runs.**
- **Our player changes:** a weekly job (or on demand before a release) runs all Played specs in
  ours only, against the stored references. A regression flips that mod's result and opens an
  issue.
- **A mod updates:** `scripts/ws-mods.py download` weekly compares `time_updated` and the DLL hash. On a
  change the job re-runs the spec in the original, re-blesses, and then runs ours. If the original
  now fails, the spec needs maintenance (agent task), and the row shows "re-check pending".
- **The game updates (Klei build):** a full re-bless in the original, then ours.
- **Staleness:** a result older than the mod's `time_updated` makes the row read "re-check
  pending" automatically.

## 8. Pilot: seven mods and their spec sketches

These are sketches. Element paths are illustrative until `ui.find` gives the real ones.

### 8.1 Blueprints Expanded (rank 65, 3468585385): S, T, K, O; `asset_bundle_ui`; issue #6

**Today's Played is not valid.** PerfProbe's `blueprints.txt` mode invoked the mod's methods by
reflection and never showed its UI. The bar for re-earning it:
- the selector screen and the use-blueprint options panel match the original;
- the dialogs work;
- the round trip works through the UI.

```yaml
mods: [3468585385]
from: early-base
steps:
  - tool: {toolbar: CreateBlueprintTool}          # its toolbar entry; also try its hotkey via key:
  - drag: {from: [108, 212], to: [136, 205]}
  - ui_dump: {name: name-dialog, root: "**/FileNameDialog"}
  - shot: {name: name-dialog, crop: "**/FileNameDialog"}
  - type: {ui: "**/FileNameDialog/**/Input", text: "e2e", enter: true}
  - expect_state: {"file:blueprints/e2e.blueprint": exists}
  - tool: {toolbar: UseBlueprintTool}
  - ui_wait: {path: "**/BlueprintSelector", cond: stable}
  - ui_dump: {name: selector, root: "**/BlueprintSelector"}
  - shot: {name: selector, crop: "**/BlueprintSelector", mask: ["**/DateLabel"]}
  - click: {text: "e2e", under: "**/BlueprintSelector"}
  - shot: {name: use-panel, crop: "**/UseBlueprintStateContainer"}
  - click: {ui: "**/RotateActions/RotateR"}       # UI response: the preview rotates
  - expect_state: {"type:BlueprintsV2.…BlueprintState.rotation": 1}
  - click_world: {cell: [150, 212]}
  - expect: {pending_construction: ">=1"}
  - expect_state: {"count:Constructable": "== original"}   # the same site count as the original run
```

**Effort:** 1 day, plus the #6 fix.

### 8.2 Duplicant Stat Selector (rank 11, 2856555858): M, S; `asset_bundle_ui`; issue #5

**Surfaces:**
- trait, interest and attribute buttons added to each immigrant card;
- the `UnityDuplicitySelectionScreen` list window;
- the care package editor;
- the same at the new-game duplicant selection.

```yaml
mods: [2856555858]
from: mid-base                                    # telepad ready to print
setup:
  - driver: {seed: 12345}
steps:
  - call: {tool: building_control, args: {domain: side_surface, surface: facility, kind: telepad, action: open_immigrants, confirm: true}}
  - ui_wait: {path: "**/ImmigrantScreen", cond: stable}
  - ui_dump: {name: cards, root: "**/ImmigrantScreen"}
  - shot: {name: cards, crop: "**/ImmigrantScreen", mask: ["**/Portrait*", "**/NameLabel"]}
  - click: {ui: "**/CharacterContainer[0]/**/TraitButton[0]"}    # the #5 button
  - expect_ui: {"**/UnityDuplicitySelectionScreen": {active: true}}
  - shot: {name: trait-list, crop: "**/UnityDuplicitySelectionScreen"}
  - type: {ui: "**/SearchBar/Input", text: "Quick"}
  - click: {text: "Quick Learner", under: "**/UnityDuplicitySelectionScreen"}
  - expect_ui: {"**/CharacterContainer[0]/**/Traits": {text: "~Quick Learner"}}
  - click: {ui: "**/CharacterContainer[0]/**/Athletics/Plus"}
  - expect_ui: {"**/CharacterContainer[0]/**/Athletics/Value": {text: "+1 from before"}}
  - click: {ui: "**/CharacterContainer[0]/SelectButton"}
  - click: {ui: "**/ImmigrantScreen/**/ProceedButton"}
  - run: {seconds: 30}
  - expect: {duplicants: "+1"}
  - expect_state: {"newest:MinionIdentity/Traits": {contains: QuickLearner}}
```

A second spec does the same at the new-game duplicant selection, through OniMcp's new-game
stages, with the main menu as the start.

**Effort:** 1 day, plus the #5 fix. Here the occlusion check (Section 4.3) tells a covered button
apart from a handler that never fires.

### 8.3 Research Queue (rank 6, 1893887916): K on a vanilla screen

It patches `ResearchEntry.OnResearchClicked`, `Research.AddTechToQueue` and `SetActiveResearch`.
Shift-click queues a tech.

```yaml
mods: [1893887916]
from: early-base
steps:
  - open: {management: research}
  - click: {ui: "**/ResearchScreen/**/Tech_AdvancedResearch/Button", mods: [Shift]}
  - click: {ui: "**/ResearchScreen/**/Tech_FineDining/Button", mods: [Shift]}
  - expect_state: {"Research.Instance.queuedTech.Count": ">=4"}   # targets plus prerequisites, compared with the original's count
  - expect_ui: {"**/Tech_FineDining/**/QueueNumber": {active: true}}
  - shot: {name: queue, crop: "**/ResearchScreen", mask: ["**/ProgressBar*"]}
  - click: {ui: "**/Tech_AdvancedResearch/Button"}                 # a plain click replaces the queue
  - expect_state: {"Research.Instance.queuedTech.Count": "== original"}
```

**Effort:** 2–3 h.

### 8.4 Sweep By Type (rank 13, 1863428350): T, S (KScreen), O

It adds `FilteredClearTool` (a DragTool), `TypeSelectScreen` and a PLib option ("Disable Material
Icons").

```yaml
mods: [1863428350]
from: early-base-debris                          # early-base after digging: sandstone and cuprite debris
steps:
  - tool: {toolbar: SweepByType}
  - ui_dump: {name: types, root: "**/TypeSelectScreen"}
  - shot: {name: types, crop: "**/TypeSelectScreen"}
  - click: {text: "All", under: "**/TypeSelectScreen"}            # clear all
  - toggle: {ui: "**/TypeSelectScreen/**/Sandstone", to: true}
  - drag: {from: [108, 210], to: [137, 206]}
  - expect_state: {"count:Clearable[marked]": "== count:Pickupable[Sandstone] in area"}
  - run: {seconds: 600, until: {"count:Clearable[marked]": 0}}
  - expect_state: {"count:Pickupable[Cuprite] in area": "== before"}
  # the shared O template:
  - include: _templates/plib-options.yaml
    with: {mod: "SweepByType", toggle: "Disable Material Icons"}
  - tool: {toolbar: SweepByType}
  - expect_ui: {"**/TypeSelectScreen/**/Sandstone/Icon": {active: false}}
```

**Effort:** 3–4 h, and it produces the PLib-options template that covers about 75 mods.

### 8.5 Airlock Door (rank 17, 2094698134): B

`AirlockDoorConfig` and `AirlockDoorInsulatedConfig` take power, and they patch door transitions
and falling.

```yaml
mods: [2094698134]
from: mid-base                                   # power and steel
steps:
  - build: {prefab: AirlockDoor, at: [140, 207]}   # in the wall between the base and a CO2 pocket
  - run: {seconds: 900, until: {buildings: {AirlockDoor: 1}}}
  - call: {tool: navigation_control, args: {action: focus_cell, x: 140, y: 207}}
  - click_world: {cell: [140, 208]}
  - shot: {name: side-screen, crop: "**/DetailsScreen"}
  - ui_dump: {name: side-screen, root: "**/DetailsScreen/**/SideScreens"}
  - run: {seconds: 300}
  - expect_state: {"gasmass:left-region CO2": "< 1 kg"}   # nothing leaked through while Duplicants passed
  - expect_state: {"door:140,207 transitions": ">0"}
```

**Effort:** 2–3 h. It is also the first test that OniMcp's viewport tokens accept mod prefabs.

### 8.6 Bigger Camera Zoom Out (rank 1, 1717463209): V

It patches `CameraController.OnPrefabInit` (maxOrthographicSize), `SetMaxOrthographicSize`,
`ConstrainToWorld` and `ClusterMapScreen.OnKeyDown`. Rendering at large orthographic sizes is
where the falloff of #4 shows.

```yaml
mods: [1717463209]
from: mid-base
steps:
  - scroll: {at: center, notches: -40}           # XTEST wheel: a player's zoom out
  - expect_state: {"Camera.main.orthographicSize": "> 20"}   # beyond vanilla's 20
  - world_shot: {name: far, hide_ui: true, mask: ["dupes", "critters"]}
  - world_shot: {name: mid, ortho: 30, at: [128, 210], hide_ui: true}
  - key: {code: Z}                               # the starmap
  - scroll: {at: center, notches: -20}
  - shot: {name: starmap}
```

**Effort:** 2 h, plus threshold calibration on vanilla world shots, which V specs share.

### 8.7 Full Miner Yield (rank 71, 2527442674): P

It patches `WorldDamage.OnDigComplete`.

```yaml
mods: [2527442674]
from: landing
steps:
  - expect_state: {"mass:cells 110,207..118,209": {save_as: before}}
  - dig: [110, 207, 118, 209]
  - run: {seconds: 600, until: {pending_digs: 0}}
  - expect_state: {"mass:debris in 105,203..123,212": ">= 0.95 * before"}   # vanilla gives about 50 %
```

**Effort:** 1 h; no UI evidence (the `touches_ui` flag is off).

## 9. Implementation order

1. **ModTriage** (1 day). Run it over `out/ws` and produce the class table. Check it on the 7 pilot
   mods by hand.
2. **E2eDriver**: `ui.find`, `ui.dump`, `state.get`, `camera`, `seed`, `setup.call`
   (2–3 days). Test it at the main menu in both players: dump the vanilla options screen in both
   and diff it, which also calibrates tolerances.
3. **e2e.py**: lanes and ports, `mods:` and `options:`, XTEST input, the new steps, references
   under `out/e2e-refs`, and the comparisons (2–3 days).
4. **ui-diff.py** `--tolerance` and `--mask-text`, and **qa-tools.py image** crop and mask
   (half a day).
5. **Calibration** on vanilla screens and world shots (1 day).
6. **Fixture ladder**: `early-base` (spec exists), `early-base-debris`, `mid-base` (1 day).
7. **Pilot**: 7 specs (3–5 days, most of it #5 and #6).
8. **Templates and agent pipeline**, then rollout by rank (Section 6.3).

## 10. Risks and open questions

**Decided (2026-10-22):**
1. Blueprints Expanded goes to ⬜ now; the other existing Played rows stay ✅ until their spec runs.
2. A ⚠️ state is added: played, small known difference, issue linked.
3. E2eDriver is a separate mod.
4. XTEST through ctypes (nothing to install).
5. `ws-mods.py` and `modtable.py` move into `scripts/`; e2e.py is committed and depends on no
   uncommitted tool. Done: `scripts/ws-mods.py` and `scripts/modtable.py`.
6. Specs and results.json go in git; screenshots stay local. Fixture saves stay out of git too
   (`tests/e2e/fixtures/*.sav`).
7. 4 lanes, and no mod spec starts while a perf run holds the lock.

**The decisions as they were put:**
1. **Existing Played rows.** Of the 13, several were checked by log or by reflection, not through
   the UI: Blueprints Expanded, the three "issue #2's set" rows, ONI Together. Proposal: demote
   Blueprints Expanded to ⬜ now (#6 is open). Re-check the rest in the rollout, keeping them ✅
   until their spec runs.
2. **A ⚠️ state** for "played, small known difference, issue linked". Without it, a 1-pixel icon
   offset either blocks ✅ or hides in a note.
3. **E2eDriver as a separate mod** (proposed) or a PerfProbe mode.
4. **XTEST through ctypes** (nothing to install) or `xdotool` (apt).
5. **Moving `ws-mods.py` and `modtable.py` into `scripts/`.** The committed e2e.py should not
   depend on uncommitted tools.
6. **Evidence in git:** specs and results.json only (proposed), or also small cropped PNGs (about
   50 KB each, about 20 MB over 150 mods).
7. **Lanes:** 4 (proposed). A rule that mod specs never start while a perf run is active, enforced
   by the lock.

**Technical risks:**
- **XTEST in the original Unity player under Xvfb.** It is expected to work, since Unity reads
  core X input, but it is untested. If the original ignores synthetic events without window focus,
  the runner must focus the window first (one window per display makes this easy).
- **OniMcp is a third party at version 0.x.** Its tool names may change, so the runner pins one
  build (`E2E_ONIMCP`). It also needs the ValueTuple forwarder in the original. Its own patches
  could interact with some mods. Mitigation: the spec passes in the original with OniMcp too, so
  an interaction shows as a broken spec, not as our bug.
- **Random UI content** (DSS, care packages, names) may not be the same across players even with
  the seed set. Then masks and relational asserts carry the check (Section 5.4), and pixel
  evidence is weaker for those screens.
- **Pixel noise between players** (font hinting, SDF edges, filtering) could be larger than the
  differences that matter. Calibration on vanilla screens decides whether 2 % is right. If it is
  not, the structural dump becomes the primary UI oracle and pixels a secondary check.
- **Agent-drafted specs** may test trivia, or encode a mod's own bug as the expectation. The
  original-first rule catches the second. Review catches the first.
- **Mod settings written outside the run home** (a few mods write next to their DLL or into
  `~/.config`). Each run copies the mod folders fresh into its own home, and the triage flags
  absolute-path writes.
- **Spaced Out only:** the fixtures are Spaced Out saves. Base-game-only mods (`supportedContent:
  VANILLA_ID`) need a base-game fixture. Should we add one, or record such mods as "Spaced Out
  not supported"?
- **Not covered:** multiplayer sessions (two Steam accounts). These rows stay ⬜ with their
  current notes.
- **Translations** were first left out as applied only from a Steam subscription. They are not:
  - The Translations screen (`LanguageOptionsScreen`) lists every installed mod with a `.po`
    file, whatever its distribution platform, Local ones too, enabled or not.
  - Picking one saves `SelectedLanguageType: UGC` and `InstalledLanguage: ID` (the mod's label
    id, its Workshop ID as a Local mod) and restarts. `Localization.Initialize` loads that
    mod's `strings.po` at the next start.
  - Only the Workshop button, the unsubscribe and update notices and
    `VerifyTranslationModSubscription` need Steam.
  - A translation must stay disabled, as a subscription leaves it. KMod loads no content of an
    enabled one (`LoadTranslations` returns false), marks it failed and opens a load error
    dialog.

  So a spec installs it as e2e installs every mod, and `translation: ID` writes the two prefs
  (tests/e2e/README.md). `tests/e2e/translation-screen.yaml` drives the screen to its restart
  dialog, and clicking restart by hand wrote the same two prefs in both players before each
  quit. Each translation's spec checks a colony's research and priorities screens and pause
  menu, then the main menu, the options menu and its Game options screen, dumped and shot (a
  main menu shot reads the lane's display).

  Of the 51 translations in the top 750, 49 play as in the original, and 2 name their file
  Strings.po, which neither player finds on Linux. The Japanese, Korean and some Chinese ones
  play as in the original since text heights follow the shipped package's hundredth rule
  (docs/cleanroom/textmeshpro.md). Their Game options headers' layout was 0.01 taller before.
- **Worldgen mods** are classed by their code like any other and get specs, with functional
  expectations: the mod's worlds or clusters are offered and generate, its biomes, traits,
  templates and POIs appear, and generation ends without errors. The generated layout need not
  match the original's cell for cell (our worldgen APIs, such as `Mathf.PerlinNoise`, are only
  functionally similar), so world screenshots and element-by-cell comparisons are not expected.
- **Machine contention** with perf measurements. The lock and the night-time schedule handle it,
  but a perf run blocks the mod queue.
