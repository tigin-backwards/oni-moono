# E2eDriver

The play-tester's driver (`docs/research/mod-play-testing.md` §4.2): a game mod that loads in
both players, our player and the original Unity player. It answers requests over a local
socket. Through those requests it finds UI elements and reports their screen rectangles and the
raycast hit at their centre. It also dumps UI subtrees, reads game and mod state by reflection,
moves the camera, hides canvases, makes setup calls and seeds the random generators. It uses
only public Unity and uGUI API, the same in both players.

`scripts/e2e.py` talks to it. Input (clicks, keys, drags) is not the driver's job: the runner
sends real X11 events through XTEST, using the rectangles the driver reports. Runs that must
not use the desktop's pointer or keyboard (Windows test runs) use its synthetic input instead
(`input.*`, below).

## Building and installing

```
dotnet build tools/E2eDriver -c Release     # -> out/bin/E2eDriver/release/
```

Copy `E2eDriver.dll`, `mod.yaml` and `mod_info.yaml` into the run profile's
`mods/Local/E2eDriver/`, and enable it in `mods/mods.json`. Use the label version
`1399985550`, the Mono string hash of the folder name `E2eDriver` (ARCHITECTURE D3), so that
one `mods.json` serves both players:

```json
{"label":{"distribution_platform":0,"id":"E2eDriver","title":"E2eDriver","version":1399985550},
 "status":1,"enabled":true,"enabledForDlc":["","EXPANSION1_ID"],"crash_count":0,
 "reinstall_path":null,"staticID":"E2eDriver"}
```

The game log shows `[E2eDriver] listening on 127.0.0.1:PORT` once the mod has loaded, early
in start-up and before the main menu.

## Transport

- TCP on `127.0.0.1`. The port comes from the environment variable `E2E_DRIVER_PORT` and
  defaults to `8790`. Give each lane its own port.
- **JSON lines.** Each request is one JSON object on one line, and so is each response. A client
  may keep one connection open for many requests. Several clients may connect at once.
- **Main thread, one per frame.** Requests are queued and handled on the game's main thread, in
  `Update`, one per frame. A command that spans frames (`ui.wait`, `buildmenu.settled`,
  `camera.set`, `canvas.hide`, `canvas.show`) holds the queue until it answers. So a client gets its
  answers in the order it sent the requests.

### Request

```json
{"id": 7, "cmd": "ui.find", "args": {"path": "**/OKButton"}}
```

- `id`: any JSON value, echoed back in the response. Optional, but needed to match responses
  when requests are pipelined.
- `cmd`: the command name.
- `args`: an object. When it is left out, the request object itself is read as the arguments,
  so `{"id":7,"cmd":"ui.find","path":"**/OKButton"}` also works.

### Response

```json
{"id": 7, "ok": true,  "frame": 8861, "result": {...}}
{"id": 7, "ok": false, "frame": 8861, "error": "no element matches **/Nope"}
```

- `frame`: `Time.frameCount` when the answer was sent.
- A malformed line is answered with `{"id": null, "ok": false, "error": "malformed request: …"}`.
- A `ui.wait` timeout is `ok: false`, and its `result` still carries the last state it saw.
- An unexpected exception inside a command is `ok: false`, with the error
  `"ExceptionType: message"`. It is also logged as an `[E2eDriver] … failed` warning.

## Conventions

- **Screen coordinates** are pixels with the origin at the **top left**, x to the right and y
  down, as in X11. So a rectangle from the driver can be clicked with XTEST as it is. Values
  are rounded to 0.01.
- **Rect** objects are `{"x", "y", "w", "h"}`. `x, y` is the top-left corner.
- **Hierarchy paths** are the GameObject names from the scene root, joined with `/`, for
  example `Canvas/ConfirmDialog/GameObject/Panel/OKButton`.
- **Keys** name each level with its sibling index, in the `--dump-ui` style:
  `/Canvas#0/ConfirmDialog#0/GameObject#1/Panel#3/OKButton#0`.
- **Numbers** are written the same way in both runtimes:
  - floats as the shortest of G6 to G9 that reads back as the same value;
  - doubles to 15 significant digits;
  - NaN and the infinities as the strings `NaN`, `Infinity` and `-Infinity`.
- **Type names** are full names, with generic arguments written as `List<System.String>`. They
  carry no assembly names, so they compare equal across runtimes.

### Path patterns

These patterns are used by `path`, `under` and `root`, and in `ui:` expressions.

- A pattern is matched against the **whole** path from the scene root. Segments are separated
  by `/`.
- In a segment, `*` matches any run of characters and `?` matches one character.
- `**` matches any number of segments, including none.
- `Name[n]` matches the n-th sibling, counting from 0, among the siblings whose name matches
  `Name`. Queries for active elements count only active siblings; with `inactive: true` all
  siblings count.
- A pattern without any `/` matches that name anywhere: `OKButton` is the same as
  `**/OKButton`. A leading `/` is ignored.

Examples: `**/ConfirmDialog/**/OKButton`, `**/MainMenuButtons/ButtonTemplate[1]`,
`*/ResearchScreen/**/Tech_*`.

### Element description

`ui.find` and `ui.wait` describe each element like this:

```json
{"path": "Canvas/ConfirmDialog/GameObject/Panel/OKButton",
 "key": "/Canvas#0/ConfirmDialog#0/GameObject#1/Panel#3/OKButton#0",
 "name": "OKButton", "active": true, "activeSelf": true,
 "rect": {"x": 452.5, "y": 404.65, "w": 375.0, "h": 48.0},
 "center": {"x": 640.0, "y": 428.65}, "onscreen": true,
 "canvas": "Canvas", "renderMode": "ScreenSpaceOverlay",
 "components": ["CanvasRenderer", "KImage", "KButton", "..."],
 "interactable": true, "isOn": false, "state": 0,
 "groupInteractable": true, "blocksRaycasts": true, "alpha": 1.0,
 "raycastTarget": true, "text": "OK",
 "hit": {"path": "Canvas/ConfirmDialog/GameObject/Panel/OKButton/Text", "relation": "child",
         "raycaster": "GraphicRaycaster", "sortingLayer": 0, "sortingOrder": 0,
         "depth": 12, "distance": 0.0, "count": 4}}
```

- `rect`: the screen-space bounding box of the RectTransform's world corners, through the root
  canvas's camera. An overlay canvas has no camera. A world-space canvas uses its own camera,
  or else `Camera.main`.
- `onscreen`: the centre is inside the screen and the rect is not empty.
- `interactable`: a `Selectable`'s `IsInteractable()`, or a game component's public
  `isInteractable` (KButton). It is left out when there is neither.
- `isOn`: a `Toggle`'s `isOn`, or a game component's public `isOn` (KToggle).
- `state`: a `MultiToggle`'s `CurrentState`.
- `groupInteractable`, `blocksRaycasts`, `alpha`: the enabled `CanvasGroup`s from the element
  up to the first one with `ignoreParentGroups`, combined.
- `text`: the element's own `TMP_Text` (LocText included) or `UnityEngine.UI.Text`.
- `hit`: the topmost result of `EventSystem.RaycastAll` at the centre. It is given only for
  active elements that are on screen, and only when `hit` is not `false`. Its `relation` to the
  element is one of these:
  - `self`: the element itself;
  - `child`: inside the element;
  - `ancestor`: a parent of the element;
  - `other`: something else covers the centre;
  - `none`: nothing was hit;
  - `no-event-system`: there is no event system.

  A click is safe only for `self` and `child`. `other` means the element is covered (an
  "occluded by PATH" failure). With `hits: N`, `hit.all` lists the top N results, which helps
  to diagnose an occlusion.

## Commands

### ping

Who is answering, and where.

```
>> {"id":1,"cmd":"ping","args":{}}
<< {"id":1,"ok":true,"frame":2628,"result":{"driver":"1","player":"ours","unity":"6000.3.5f2",
    "scene":"frontend","screen":{"w":1280,"h":720},"port":8811,"paused":null,"world":false}}
```

- `player`: `ours` when an `OniMoono*` (or older `OniBrutal*`) assembly is loaded, else `original`.
- `paused`: `SpeedControlScreen.IsPaused`, or `null` before a colony is loaded.
- `world`: whether a colony's grid is loaded.

### ui.at `{x, y, hits?}`

The raycast at a screen point (top-left pixels): the topmost hit and, in `all`, the top `hits`
results (default 8), each with its path, raycaster, sorting and depth. For "what is under the
cursor", for example when a world click seems to be swallowed by the UI.

### ui.find `{path?, text?, type?, under?, inactive?, limit?, hit?, hits?}`

The elements matching every filter given, in hierarchy order, each one described.

- `path`: a path pattern.
- `text`: exact text, compared trimmed, with or without rich-text tags. `~sub` matches a
  case-insensitive substring instead.
- `type`: a component type, by short or full name. Base types count, so `Selectable` matches a
  `Button`.
- `under`: a path pattern that the element or one of its ancestors must match.
- `inactive`: also return inactive elements. Default `false`.
- `limit`: the most elements described. Default 50. `count` is always the total.
- `hit`: compute `hit`. Default `true`.
- `hits`: list the top N raycast results in `hit.all`. Default 0.

```
>> {"id":2,"cmd":"ui.find","args":{"text":"MOD DIFFERENCES","under":"Canvas/ConfirmDialog"}}
<< {"id":2,"ok":true,"frame":8862,"result":{"count":1,"elements":[{"path":"Canvas/ConfirmDialog/GameObject/Header/Label",
    "rect":{"x":420.5,"y":251.35,"w":439.0,"h":24.0},"center":{"x":640.0,"y":263.35},"text":"MOD DIFFERENCES", ...}]}}
```

### ui.dump `{root?, inactive?, file?, settle?}`

The subtree of every element matching `root`, one line per element, in the `--dump-ui` line
format that `scripts/ui-diff.py` compares. Without `root`, all canvases are dumped. A match
inside another match's subtree is dumped only once.

Each line has these parts:
- the element's key, then a tab;
- `rect x,y,w,h` (`RectTransform.rect`), `world x0,y0,x2,y2` (the world corners) and
  `screen x,y,w,h` (top-left pixels), with `inactive` added for inactive elements;
- then, for each component, ` | Type` (`Type(off)` when it is disabled) followed by the
  component's public members as `name=value`:
  - Canvas: render mode, scale, sorting;
  - CanvasGroup;
  - Graphic: colour, raycastTarget, depth;
  - Image;
  - Text / TMP_Text: text, size, alignment, overflow, textInfo counts;
  - Selectable: interactable;
  - Toggle: isOn;
  - Slider and Scrollbar values;
  - ScrollRect: normalizedPosition;
  - InputField and Dropdown;
  - LayoutElement and LayoutGroup sizes;
  - CanvasScaler;
  - CanvasRenderer: `col=` and `cull`.

CanvasRenderer geometry hashes are not recorded: they cannot be read through public API.

- With `file`, the lines are written to that file, and the result is
  `{roots, count, file}`.
- Without `file`, the result is `{roots, count, lines: [...]}`.
- With `settle: N`, it first waits up to N frames for the build menu to settle, as
  `buildmenu.settled` does, and dumps in the frame the menu is found settled: at once when it
  already is, so the dump sees the same frame as without the wait. The result then also holds
  the last check as `settle`, and a menu that does not settle fails as `buildmenu.settled` does.

```
>> {"id":4,"cmd":"ui.dump","args":{"root":"**/ConfirmDialog/GameObject/Panel"}}
<< {"id":4,"ok":true,"frame":8863,"result":{"roots":["Canvas/ConfirmDialog/GameObject/Panel"],"count":3,"lines":[
    "/Canvas#0/ConfirmDialog#0/GameObject#1/Panel#3\trect -187.5,0,375,56 world 452.5,267.35,827.5,323.35 screen 452.5,396.65,375,56 | CanvasRenderer col=255,255,255,255 | VerticalLayoutGroup preferredWidth=375 preferredHeight=56 minWidth=375 minHeight=56", ...]}}
```

### ui.wait `{path?/text?/type?/under?, cond?, value?, frames?, stable?}`

Checks the first element matching the query once per frame, until `cond` holds or `frames`
have passed (default 300).

| cond | holds when |
|---|---|
| `exists` (default) | an active element matches |
| `gone` | no active element matches |
| `active` / `inactive` | the first match, inactive ones included, is active / inactive (or none exists, for `inactive`) |
| `interactable` / `not_interactable` | its `interactable` and `groupInteractable` |
| `on` / `off` | its `isOn` |
| `text` | its text matches `value` (exact, or `~sub`) |
| `stable` | its screen rect is unchanged for `stable` consecutive frames (default 5): a tween has ended |

- **Result:** `{frames, count, element}`.
- **On timeout:** `ok: false` with the error `timeout: COND not met within N frames`, and
  `result` holding the last `{frames, count, element}`.

```
>> {"id":7,"cmd":"ui.wait","args":{"path":"**/MainMenuButtons","cond":"stable"}}
<< {"id":7,"ok":true,"frame":3029,"result":{"frames":6,"count":1,"element":{...}}}
>> {"id":8,"cmd":"ui.wait","args":{"path":"**/NoSuchElement","frames":10}}
<< {"id":8,"ok":false,"frame":3040,"error":"timeout: exists not met within 10 frames","result":{"frames":10,"count":0,"element":null}}
```

### world.to_screen `{cell: N | [x, y]}` or `{pos: [x, y, z?]}`

Where a cell's centre, or a world position, appears on screen. It goes through
`Grid.CellToPosCCC` and `Camera.main.WorldToScreenPoint`, and needs a loaded colony for `cell`.

- **Result:** `{x, y, onscreen, world: {x, y, z}}`.

```
>> {"cmd":"world.to_screen","args":{"cell":[136,229]}}
<< {"ok":true,"result":{"x":640.0,"y":360.0,"onscreen":true,"world":{"x":136.5,"y":229.5,"z":-25.5}}}
```

### screen.to_cell `{x, y}`

The cell under a screen point, given in top-left pixels.

- **Result:** `{cell, valid, world: {x, y}, xy: [x, y], worldId}`. `xy` and `worldId` are
  given only when the cell is valid.

```
>> {"cmd":"screen.to_cell","args":{"x":100,"y":100}}
<< {"ok":true,"result":{"cell":151486,"valid":true,"world":{"x":118.5,"y":238.16667},"xy":[118,238],"worldId":0}}
```

### state.get `{expr, depth?, limit?}`

A small read-only path language. The result is `{value, type}`.

**Heads:**
- `A.B.C`: the longest leading run of segments that names a type, then its static members. For
  example `Grid.WidthInCells`, `Camera.main.orthographicSize` or
  `Research.Instance.queuedTech.Count`. Short type names prefer the game's assemblies.
- `type:FULL.NAME::rest`: an explicit type, for names that are ambiguous or nested (`A+B`). A
  mod's types are found too.
- `selected:rest`: the GameObject the player has selected (`SelectTool.Instance.selected`).
- `ui:PATTERN::rest`: the GameObject of the first active element matching PATTERN.
- `all:TYPE::rest`: every loaded object of a Unity type (`FindObjectsByType`), as an array.
- `last:rest`: the latest non-null value a `setup.call` returned, such as an object it
  instantiated: `{target: "last:", method: SetActive, args: [true]}`.

**Segments**, separated by `.`:
- `name`: a field or property, public or not, instance or static, base types included.
- `name(args)`: a method call. The arguments are JSON values; zero arguments are allowed.
  Meant for getters, such as `GetCycle()` and `GetTraitIds()`.
- `GetComponent<T>`, `GetComponents<T>`, `GetComponentInChildren<T>`,
  `GetComponentsInChildren<T>`, `GetComponentInParent<T>`: on a GameObject or a Component.
- On a GameObject or Component, a bare component type name works like `GetComponent<T>`, for
  example `selected:MinionIdentity.name`.
- `[n]` indexes a list or array (negative counts from the end). `["key"]` or `[n]` indexes a
  dictionary. Its key is converted to the key type: Tag, enum or string constructors are
  accepted.

**Values:**
- Scalars are written as themselves, and enums by name.
- Vectors, colours, rects and quaternions are objects.
- A Unity object is `{$type, name, id, path, active?}`. `id` is the instance ID, which differs
  between runs.
- Lists are arrays and dictionaries are objects, at most `limit` entries (default 200).
- Other objects are written as their public fields and properties while `depth` lasts
  (default 2), then as `{$type, $str}`.
- A getter that throws is written as `"!ExceptionType"`.

```
>> {"cmd":"state.get","args":{"expr":"Components.LiveMinionIdentities.Items[0].GetComponent<Traits>.GetTraitIds()"}}
<< {"ok":true,"result":{"value":["Snorer","FrostProof","InteriorDecorator","StressVomiter","SuperProductive"],"type":"System.Collections.Generic.List<System.String>"}}
>> {"cmd":"state.get","args":{"expr":"KPlayerPrefs.GetInt(\"ResolutionWidth\")"}}
<< {"ok":true,"result":{"value":1280,"type":"System.Int32"}}
```

### state.each `{expr, members?, depth?, limit?}`

The items of `expr` (an array, list or other enumerable, as `state.get` reads it), and from
each item the values of `members`: paths in the same language, applied to the item (`".a.b"`,
`".GetComponent<KPrefabID>.PrefabTag.Name"`, `""` for the item itself; default `[""]`). All in
one frame, for lists too long to read an item per request.

- **Result:** `{count, items}`, `items` holding one array per item with its members' values,
  written as `state.get` writes values.
- A member that cannot be read fails the command: `item N.member: error`.

```
>> {"cmd":"state.each","args":{"expr":"SaveGame.Instance.worldGenSpawner.spawnables","members":[".spawnInfo.id",".isSpawned"]}}
<< {"ok":true,"result":{"count":5492,"items":[["Tile",false],["GeyserGeneric_steam",false], ...]}}
```

### camera.get

Reads `Camera.main`, or on the main menu, which has no camera tagged MainCamera, the enabled
camera with the lowest depth.

- **Result:** `{camera, main, pos, ortho, orthographic, pixels: {w, h}, controller, cell?}`.
  `controller` is whether the game's CameraController exists. `cell` is the cell under the
  camera.

### camera.set `{pos: [x, y] | cell: N | [x, y], ortho?, max?, raw?}`

- **In a colony:** goes through `CameraController.SnapTo(pos, ortho)`, which moves all the
  game's cameras. `max` first raises the controller's maximum orthographic size.
- **Without a controller, or with `raw: true`:** sets `Camera.main`'s position and
  orthographic size directly.

It answers 2 frames later with `camera.get`'s result, which shows where the camera really
ended up. The controller may constrain it.

```
>> {"cmd":"camera.set","args":{"cell":[136,229],"ortho":12}}
<< {"ok":true,"result":{"camera":"overlayNoDepth","main":true,"pos":{"x":136.5,"y":229.5,"z":-100.0},"ortho":12.0,"orthographic":true,"pixels":{"w":1280,"h":720},"controller":true,"cell":[136,229]}}
```

### canvas.hide `{world?}` / canvas.show

These are for V-class world shots.

- **canvas.hide** disables every enabled root canvas. World-space canvases are disabled only
  with `world: true`. It remembers them for `canvas.show`, and answers 2 frames later, so a
  frame without the UI has been drawn. Result: `{hidden, canvases: [paths, sorted], total}`.
- **canvas.show** re-enables what was hidden, and answers 2 frames later. Result: `{shown}`.

### setup.call `{type, method, args?}` or `{target, method, args?}`

A reflective call. **The runner allows it only in a spec's `setup:` block**, never in
`steps:`. It may set up a test, but the thing under test must be driven through the UI.

- `type`: a static method on that type.
- `target`: a `state.get` expression for the instance, or for the type, to call `method` on.
- `args`: JSON values, converted to the parameter types. Accepted conversions:
  - numbers to any numeric type;
  - strings to enums, Tags or HashedStrings;
  - `[x, y(, z)]` to Vector2, Vector3 or Vector2I;
  - arrays to arrays, `List<T>` or a list interface (`IList<T>`, `IEnumerable<T>`, …);
  - a type name to `Type`;
  - `{"$": "EXPR"}` to the value of the `state.get` expression EXPR, when it is of the
    parameter's type, or to its component of that type: for arguments JSON cannot spell, such
    as a `Tech` or a `GameObject`. `SelectTool.Instance.Select` with
    `[{"$": "all:ResearchCenter::[0]"}]` selects the research station.

  Missing trailing arguments take their defaults. An `out` or `ref` parameter takes a value of
  its element type, and a trailing `out` one may be left out; the result is the return value
  only. Among the overloads, exact kinds win: a JSON integer picks `int` over `float`.

- **Result:** `{value, type}` of the return value, as in `state.get`.

```
>> {"cmd":"setup.call","args":{"type":"UnityEngine.Random","method":"Range","args":[0,1000]}}
<< {"ok":true,"result":{"value":683,"type":"System.Int32"}}
```

### seed `{n, krandom?}`

The same random numbers in both players before a random screen opens (§5.4). It does three
things:
- `UnityEngine.Random.InitState(n)`;
- the game's shared `Util.random` becomes `new KRandom(n)`;
- unless `krandom: false`, every later `KRandom` that seeds itself from
  `Environment.TickCount` gets n+1, n+2, … instead.

The third works through a Harmony prefix on `KRandom(int)`, installed at the first `seed`. It
replaces a seed that equals the current tick count within ±2. The parameterless constructor
itself is not patched, because a JIT may inline it into its callers.

- **Result:** `{unity, util, krandom, consumedBefore}`. `consumedBefore` is how many
  self-seeded KRandoms took a sequence seed since the previous `seed`.

Values may still differ between the runtimes: `System.Random` seeded from time, or the game
mixing in the clock. Specs then mask such values, or assert relations between them.

```
>> {"cmd":"seed","args":{"n":42}}
<< {"ok":true,"result":{"unity":42,"util":true,"krandom":true,"consumedBefore":0}}
```

### ui.action `{action}`

Delivers one of the management screens' game Actions (`ManageVitals`, `ManageConsumables`,
`ManagePriorities`, `ManageSchedule`, `ManageSkills`, `ManageResearch`, `ManageStarmap`,
`ManageReport`, `ManageDatabase`) to `KScreenManager` as a key-down and key-up, as the screen's
hotkey does: e2e.py's `open: {management: …}`. The same in U59 and U60.

### Synthetic input: input.move, input.button, input.key, input.scroll, input.text, input.submit

Input made inside the game, for a window that never has the desktop's focus or pointer
(`SyntheticInput.cs`). The first request tells the game and the event system they have the
focus (both drop input while unfocused: `KInputManager.isFocused`, `EventSystem.isFocused`) and
puts a `BaseInput` override on every input module; from then on the event system reads the
pointer from it, and the world tools from `KInputManager`'s locked mouse position, set each
frame. Keys and mouse buttons reach the game as `KInputController` queues a real key's KeyDef
(`QueueButtonEvent`). The platform's own input (window messages, X11 events) is not involved,
so this checks what the game and the UI do with input, not how the player reads it.

- `input.move {x, y}`: the pointer, in screen pixels from the top left.
- `input.button {button: left|right|middle, down}`: a press or release; the event system sees
  it in the next frame it reads the button, the release a frame after the press at the earliest.
  Result: `queued`, whether a binding uses the button.
- `input.key {code, mods?, down}`: a KKeyCode name (`F7`, `Return`, `Alpha1`) and modifiers
  (`Shift`, `Ctrl`, `Alt`). `queued` is false when no binding uses the key with those modifiers.
  Text fields do not hear it: they read IMGUI events.
- `input.scroll {notches}`: wheel notches (positive up) for the event system and as the
  game's scroll keys.
- `input.text {text}`: the selected TMP input field's text, set whole (its `onValueChanged`
  runs as for typing).
- `input.submit`: Enter in the selected TMP input field: its `onSubmit`, then the field
  deactivated, which sends `onEndEdit`.
- `input.focus`: the focus only. Result `{focused}`.

### screen.capture `{name}`

The frame as a PNG through `ScreenCapture.CaptureScreenshot`, at
`Path.GetTempPath()/e2e-shots/PORT-NAME.png`, in any scene (OniMcp shoots in a colony only).
Answers once the file is complete. Result: `{path, w, h}`.

### count `{methods}`

Counts the calls of game methods from now on, through a Harmony prefix on each, added the
first time the method is named. `methods` names each as `Type:Method` (Harmony's
`AccessTools.Method` form, `+` for a nested type), the first overload by that name, or
`Type:Method(ParamType,...)` for one overload of several (`KFMOD:PlayUISound(System.String)`). Each
`count` restarts every count it names. The counts are static state:
`type:E2eDriver.Counting::Of("Brain:UpdateBrain")` reads one, so `expect_state` can relate them,
as `late-patch.yaml` relates a method's calls to its caller's.

A prefix added while the game runs is the test itself: whether a patch applies to code that
was compiled before it (ARCHITECTURE D34).

- **Result:** `{"Type:Method": 0, …}`, the counts before the restart.

```
>> {"cmd":"count","args":{"methods":["Brain:UpdateBrain"]}}
<< {"ok":true,"result":{"Brain:UpdateBrain":0}}
```

### buildmenu.settled `{frames?}`

Whether the build menu (PlanScreen) shows what the game would compute for it now. The game
catches up over frames: each WorldInventory recounts one tag a frame, and PlanScreen re-checks
ten buildings' requirements, repaints ten building buttons and one category button a frame. The
menu is settled when all of these hold at once:

- every world's WorldInventory holds, for each tag, the amount its `Update` would count now
  (the amounts of the tag's pickupables in that world, not privately stored);
- every building button is shown or hidden as the game's filter decides now: the open
  category's buttons whose research is done (`PlanScreen.TechRequirementsMet`), or the search's
  passing scores;
- for every shown button, PlanScreen's cached state equals `GetBuildableStateForDef` now, and
  the button shows that state: its MultiToggle state, its icon's material (in colour or
  desaturated) and its research overlay, as `PlanBuildingToggle.RefreshDisplay` paints them;
- no shown button was repainted earlier in the same frame: a repaint sets the button's label
  font size, which TextMeshPro's auto-sizing replaces only at the end of the frame, so a dump
  in that frame would read a size that depends on where the round-robin repaint is;
- every category button's images, visibility and research icon show what its buildings'
  states give now (`SetCategoryButtonState`);
- no panel resize is pending, and the panel has finished expanding or collapsing.

Without a colony or a PlanScreen it is settled. A closed menu checks only the inventories and
the category buttons.

- `frames`: with 0 (the default), one check now. Otherwise it checks once a frame until the
  menu is settled, or fails after that many frames with `timeout: the build menu did not settle
  within N frames`, its `result` holding the last check.
- **Result:** `{settled, menu, open, visible, stale, reasons, ms, frames, maxMs}`. `menu` is
  whether a PlanScreen is up, `open` whether a category (or search) panel is open, `visible`
  how many building buttons are shown, `stale` how many things are stale, and `reasons` the
  first twelve of them. `ms` is the last check's time and `maxMs` the slowest check's.

```
>> {"cmd":"buildmenu.settled","args":{"frames":600}}
<< {"ok":true,"result":{"settled":true,"menu":true,"open":true,"visible":12,"stale":0,"reasons":[],"ms":0.41,"frames":37,"maxMs":0.62}}
```

### keys.log `{on?}`

Starts the key log, or clears it when it runs (`on: false` stops it). While it runs, the
driver records the key codes `Input.GetKeyDown` is true for in each frame (all key codes below
`Mouse0`), the key code of each IMGUI `KeyDown` event that names one, and the characters
`KeyDown` events type. Read them as state: `type:E2eDriver.KeyLog::Down` (one entry a frame,
codes of one frame joined by `+`, entries by spaces), `::Events` (space-separated) and
`::Chars`. It also records each frame's `Input.mouseScrollDelta` and
`Input.GetAxis("Mouse ScrollWheel")` where either is not zero (`::Wheel`, `x,y/axis` entries
separated by spaces) and the delta of each IMGUI `ScrollWheel` event (`::WheelEvents`, `x,y`).
Mouse buttons go apart: the codes `Mouse0` to `Mouse6` `Input.GetKeyDown` is true for
(`::MouseDown`, as `::Down`), those `Input.GetKey` holds each frame any is held
(`::MouseHeld`), and the button of each IMGUI `MouseDown` event (`::MouseEvents`).
Answers `{"recording": true}`.

### imgui.scrollview `{on?}`

Shows an IMGUI scroll view 300 by 300 pixels centred on the screen, over content 3000 high, and
resets its scroll position (`on: false` removes it). The wheel over it scrolls it; read the
position as state: `type:E2eDriver.ImguiProbe::ScrollY`. Answers `{"on": true}`.

### templates.load `{}`

Loads every `.yaml` under StreamingAssets' `templates/` and each `dlc/*/templates/` through the
game's `TemplateCache.LoadTemplateYaml` (VYaml since U60), in ordinal order of the path, and
reduces each to one line: `PATH cells=N prefabs=N mass=BITS temp=BITS ids=… hash=HEX`. `mass`
and `temp` are the sums of the cells' values as a double's bits in hex; `ids` the prefab ids of
buildings, pickupables, elementalOres, otherEntities and backwallEntities; `hash` an FNV-1a 64
of every public field and property, floats by their bits. A file the loader rejects reads
`PATH null`. Each line is also logged (`[E2eDriver] template …`). Read them as state:
`type:E2eDriver.Templates::Lines`, `::Count`, `::Failed` and `::Digest` (the hash of all
lines). Answers `{"count", "failed", "digest"}`.

### Census reads

State reads that sum up a colony's world in one value, for comparing two players:
- `type:E2eDriver.Census::Pickupables`: every live Pickupable's prefab id with its count,
  `ID=N` in ordinal order, joined by spaces;
- `type:E2eDriver.Census::Chunks`: every element chunk as `ID@CELL`, sorted;
- `type:E2eDriver.Census::StartGrid`: the start world's cells as `elements=HEX solid=N
  mass=HEX` (FNV-1a 64 over each cell's element index and mass bits).

## Errors

| error | when |
|---|---|
| `unknown command X` | `cmd` is not one of the above |
| `the request has no cmd` | |
| `no element matches P` | `ui.dump` with a `root` that matches nothing |
| `no world is loaded` | `world.to_screen` with a cell, or `screen.to_cell`, outside a colony |
| `no type X`, `no field or property X on T`, `no method X on T`, `no overload of T.M takes (…)` | state.get and setup.call |
| `X: the value before it is null` | a path stepped through null |
| `timeout: …` | ui.wait, buildmenu.settled |
