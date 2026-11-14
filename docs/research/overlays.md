# Overlays (2026-10-24)

What each overlay costs, where that goes, what Colony Performance Fixes (Workshop 3823893994)
changes for overlays, and the `fast-overlays` game tweak.

## Method

- **Runs:** Linux under WSL2, the release player at 1024×768, `--game-tweak exact` and
  `--frame-stats`, the game paused, so the simulation's own variation stays out of the
  overlays' costs (the overlays update every frame either way; at speed 3 the sim's work a
  frame grows with the frame time, which hid the overlays' own costs in a first, unpaused
  sweep). PerfProbe's `overlays.txt` mode opens each overlay in turn and holds it 1,200 frames,
  of which the second 600 are measured; the sweep opens and closes with no overlay, and the base
  is the mean of the two.
- **Views:** U57 at the save's position, orthographic size 16 (44×33 cells of its busiest
  planetoid); 2minnow at the save's own view, size 20.
- **Times:** the engine's phase times (main thread: every phase but the wait in `present`),
  its GL-thread busy time, its per-script times, and PerfProbe's stopwatch (`walltime.txt`,
  extended with a target list of its own: the modes' updates, `SimDebugView`, `Infrared`, the
  conduit flow visualizers, the controller colour setters, `CircuitManager`…).
- **Profiles:** `perf` (cpu-clock, 499 Hz, call graphs, JIT perf maps) of one overlay held.
- **Timing runs** hold the machine-wide perf lock exclusively, once no game runs on Windows and
  the machine is quiet. Holds still vary by about ±1 ms of main-thread time run to run.

## The overlays

Before the tweak (`exact` without `fast-overlays`), milliseconds a frame. The frame is GL-bound
while paused (the GL thread is busy about 16 ms a frame), so the main thread's time is the
column that shows an overlay's own cost; at speed 3 the main thread is the limit (about 30 ms on
U57), and every millisecond of it shows. "Most of it" names the stopwatch's largest methods
(their own ms a frame) and the UI input module's growth against no overlay.

**U57**

| Overlay | Frame | Main thread (vs none) | GL thread | Most of it |
|---|---|---|---|---|
| None | 16.7 | 10.6 | 16.5 | |
| Oxygen | 15.5 | 10.1 (−0.5) | 15.3 | |
| Power | 15.6 | 13.2 (+2.6) | 15.4 | `Power.Update` 1.28 (`GetWattsNeededWhenActive` 0.52 of it), UI input +0.78 |
| Temperature | 23.6 | 23.3 (+12.7) | 17.6 | `Infrared.Update` 10.44 (the controllers' colour setter 1.38 of it), UI input +0.93 |
| Temperature, heat flow | 17.3 | 14.2 (+3.6) | 16.9 | UI input +1.07 |
| Temperature, state change | 16.4 | 12.5 (+1.9) | 16.3 | UI input +0.74 |
| Materials | 16.9 | 13.9 (+3.3) | 16.5 | `TileMode.Update` 4.05 |
| Light | 14.8 | 9.9 (−0.7) | 14.6 | |
| Decor | 16.9 | 16.0 (+5.4) | 15.1 | `Decor.Update` 4.32 |
| Germs | 15.4 | 10.4 (−0.2) | 15.2 | |
| Farming | 17.0 | 12.5 (+1.9) | 16.7 | `Crop.Update` 0.68 |
| Harvest | 14.7 | 12.7 (+2.1) | 14.0 | UI input +0.86, `Harvest.Update` 0.14 |
| Rooms | 14.8 | 9.9 (−0.7) | 14.9 | |
| Liquid pipes | 25.0 | 20.3 (+9.7) | 24.6 | rendering +3.0, UI input +2.21, `ConduitMode.Update` 1.56 |
| Gas pipes | 16.8 | 12.8 (+2.2) | 16.6 | UI input +0.59, `ConduitMode.Update` 0.31 |
| Conveyor rails | 18.4 | 15.1 (+4.5) | 17.9 | UI input +0.95, `SolidConduitFlowVisualizer.Render` 0.89, `SolidConveyor.Update` 0.88 |
| Automation | 14.4 | 10.1 (−0.5) | 14.1 | `Logic.Update` 0.22 |
| Exosuits | 14.9 | 10.4 (−0.2) | 14.6 | |
| Radiation | 14.7 | 10.1 (−0.5) | 14.4 | |
| Priorities | 14.6 | 11.8 (+1.2) | 14.2 | |
| Thermal conductivity | 14.5 | 9.8 (−0.8) | 14.3 | |

**2minnow**

| Overlay | Frame | Main thread (vs none) | GL thread | Most of it |
|---|---|---|---|---|
| None | 15.8 | 9.0 | 15.5 | |
| Oxygen | 15.0 | 8.4 (−0.6) | 15.0 | |
| Power | 15.2 | 11.1 (+2.1) | 15.2 | `Power.Update` 1.47 (`GetWattsNeededWhenActive` 0.58 of it) |
| Temperature | 21.5 | 18.6 (+9.6) | 20.9 | `Infrared.Update` 6.53 (colour setter 1.68 of it), UI input +0.58 |
| Temperature, heat flow | 19.7 | 11.4 (+2.4) | 19.4 | GL thread +3.9 |
| Temperature, state change | 19.8 | 11.6 (+2.6) | 19.8 | GL thread +4.3 |
| Materials | 19.3 | 15.8 (+6.8) | 18.9 | `TileMode.Update` 4.79 |
| Light | 15.4 | 9.3 (+0.3) | 15.3 | |
| Decor | 14.3 | 10.4 (+1.4) | 14.1 | `Decor.Update` 2.17 |
| Germs | 15.2 | 9.6 (+0.6) | 15.0 | `DiseaseContainers.UpdateOverlayColours` 0.80 |
| Farming | 15.1 | 8.7 (−0.3) | 14.8 | `Crop.Update` 0.25 |
| Harvest | 14.4 | 9.3 (+0.3) | 14.2 | UI input +0.33 |
| Rooms | 15.1 | 9.3 (+0.3) | 14.9 | |
| Liquid pipes | 17.6 | 12.6 (+3.7) | 17.3 | `ConduitMode.Update` 0.87, UI input +0.61 |
| Gas pipes | 17.2 | 12.2 (+3.2) | 17.0 | `ConduitMode.Update` 0.85, UI input +0.50 |
| Conveyor rails | 15.8 | 10.5 (+1.5) | 15.5 | UI input +0.31, `SolidConveyor.Update` 0.22 |
| Automation | 15.9 | 10.1 (+1.1) | 15.7 | `Logic.Update` 0.19 |
| Exosuits | 15.6 | 9.2 (+0.2) | 15.3 | |
| Radiation | 14.6 | 8.5 (−0.5) | 14.4 | |
| Priorities | 14.5 | 9.8 (+0.8) | 14.2 | |
| Thermal conductivity | 14.4 | 8.4 (−0.6) | 14.2 | |

The overlays drawn from the simulation's cells (oxygen, light, rooms, radiation, thermal
conductivity, and the cell colours of every other one) cost almost nothing on the main thread:
`SimDebugView` colours the cells in view on the game's job threads and uploads its texture, about
0.1–0.2 ms in all. The cost is in the overlays that visit objects:

- **Temperature:** `Infrared.Update` triggers an event on `Game` that reaches every object with a
  temperature visualizer, in every world (U57: about 91,000 a frame, two thirds of them
  buildings), and each reads its transform to see whether it is in view. That walk, a cache miss
  or more per object, is most of it; colouring the 4,600 in view is the rest.
- **Materials and decor:** every frame the mode drops targets that left the view, gathers the
  objects in view from a partition (materials: the scene partitioner's pickupables and buildings,
  each asked for its `PrimaryElement` and its element tested against every filter) and
  highlights each target (U57: 3,366 and 2,575): positions, components, a condition, the
  controller's layer and highlight colour.
- **Power:** for each of the 378 power buildings in view, `CircuitManager.GetWattsNeededWhenActive`
  walks the circuits reachable through transformers, allocating five collections, and sums every
  consumer on them: the same answer for every building of a circuit.
- **Pipes and conveyor rails:** the same target walk, with several component lookups per target
  each frame (`IBridgedNetworkItem` twice, `Building`, the controller, and `Conduit` for each object
  gathered). The engine draws more too: on U57 about 6,000 draws a frame in liquid pipes,
  2,800 in gas pipes and 3,400 in conveyor rails, against 1,800 with no overlay (liquid pipes:
  +3 ms recording them, +8 ms on the GL thread).
- **UI input:** the input module raycasts the pointer against every raycastable graphic of the
  overlay's labels, legends and port icons each frame, 0.2–2.2 ms more than with no overlay.

## Colony Performance Fixes

Decompiled from its DLL (`out/local-tools/moddecomp/3823893994`). For overlays:

| Change | What it does | Same result? |
|---|---|---|
| `OverlayThrottle` | Skips the `Update` of the materials, decor, power and pipe overlays unless 0.25 s have passed, the view has moved or the hovered object changed; while skipped, re-pulses the tints of a hovered network from tints captured at the last update | no: everything the update shows (new targets, tints, labels, circuit loads) can be 0.25 s old, and the pulse reuses captured tints |
| `TemperatureOverlay` | Takes the temperature visualizers off the game's event, keeps buildings in 16×16-cell chunks and the rest in a list; each frame colours a quarter of the chunks in view and a quarter of the rest, and only objects whose temperature moved by 0.05 K or more | no: colours up to four frames late, changes under 0.05 K not shown, and the order controllers are coloured in (which orders their updates) differs |
| `PowerCircuits` | Caches `CircuitManager.GetWattsNeededWhenActive` per circuit for the frame, cleared by `CircuitManager.Refresh` | almost: a circuit's consumers can change between two calls in a frame, and it applies to every caller, not only the overlay |

Its other changes are not about overlays: batched animation updates and a cached UI hover
raycast (both change timing), Unity's incremental GC, and running at full speed unfocused.

## `fast-overlays`

Exact by construction, each part with a verify mode (`ONI_TWEAK_VERIFY=1`):

- **Temperature:** the event still runs, with the same handlers in the same order, but the
  visualizers' handler (the static delegate they subscribe with) is the tweak's. By its place
  in the handler order it keeps the position of an object that cannot move (a spawned building
  that is not a rocket module) and passes such a place over without touching the object when it
  is out of view; any other object reads its position through its kept transform, as the game
  does. A place held by another visualizer (one was added or removed) is read again. Objects in
  view are coloured by the game's own code. Verify: every kept position and transform against
  the object's own.
- **Power:** within one `Power.Update`, the first answer for a circuit is kept for its other
  buildings (nothing changes a circuit during the update). Verify: every kept answer is
  computed again, bit for bit.
- **Targets** (every mode with targets): the modes' calls to `AddTargetIfVisible`,
  `RemoveOffscreenTargets` and `UpdateHighlightTypeOverlay` go to the tweak's versions, the same
  loops with the same effects in the same order. What differs:
  - An object already among the targets is passed at once (the game's method does nothing more
    for it, after reading its bounds).
  - Materials skips its element's filter tests for such an object.
  - Each target's transform, controller and (if it cannot move) position are kept by place in
    the target set's order.
  - An unused `KPrefabID` lookup is left out.

  Verify: every kept position, transform and controller against the object's own, and an object
  already among the targets runs the game's method, which must leave the targets as they were.

Each part steps aside for the frame when another Harmony owner patches what it replaces (the
patched method then runs).

Two sweeps with the tweak (the second on the final code); where they differ, both are given.

| Stopwatch, ms a frame | U57 before | U57 after | 2minnow before | 2minnow after |
|---|---|---|---|---|
| `Infrared.Update` (temperature) | 10.44 | 6.03, 5.98 | 6.53 | 3.81, 3.97 |
| `Power.Update` | 1.28 | 0.77, 0.95 | 1.47 | 0.92, 0.93 |
| · of which `GetWattsNeededWhenActive` | 0.52 | 0.07 | 0.58 | 0.07 |
| `TileMode.Update` (materials) | 4.05 | 3.77, 3.81 | 4.79 | 3.48, 3.71 |
| `Decor.Update` | 4.32 | 3.60, 4.03 | 2.17 | 1.90, 1.93 |
| `ConduitMode.Update` (liquid pipes) | 1.56 | 1.64, 2.00 | 0.87 | 0.72, 0.91 |

Main-thread time with the temperature overlay open, against no overlay: U57 +12.7 → +8.3 and +9.0 ms,
2minnow +9.6 → +4.3 and +5.4 ms. The other holds' main-thread times moved by up to
±3 ms between the sweeps, overlays the tweak does not touch included, so only the stopwatch
numbers above and the temperature overlay's gain stand out from the noise. The pipe and conveyor modes' own updates did not change measurably:
their cost is the component lookups above, which the tweak does not keep.

Verified on U57 (every overlay in turn, 300 frames each, unpaused): 27 million kept positions,
2.1 million kept rows, 1.3 million targets passed and 33,000 power answers checked, 0 wrong;
2minnow the same way: 6.6 million, 5.9 million, 3.6 million and 157,800, 0 wrong. QA's verify
stage now opens every overlay in turn and requires these reports.

## What's left

- **Temperature:** the 30,000 objects a frame on U57 that can move (debris, stored items,
  critters) still read their transforms, and every visualizer still receives the event; colouring
  the ones in view stays the game's. Making the event reach only visualizers in view would mean
  reimplementing the game's event dispatch for it.
- **Materials and decor:** the gather (the scene partitioner's walk, a component lookup per
  object) and the controllers' layer and colour setters remain; most of it is a cache miss per
  object.
- **Pipes and conveyor rails:** the component lookups each frame (`IBridgedNetworkItem`, an
  interface, twice per target) could be kept like the targets' controllers; about half of
  `ConduitMode.Update` in a profile.
- **Engine:** UI input raycasting against the overlays' graphics (up to 2.2 ms), and the pipe
  overlays' extra draws (liquid pipes on U57: 3 ms recording on the main thread, 8 ms on the GL
  thread); the heat flow and state change views cost the GL thread about 4 ms more on 2minnow.
