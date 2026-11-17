# Main-thread rendering: where it goes and what would cut it (2026-11-17)

[unity-engine-baseline.md](unity-engine-baseline.md) found one part of the frame dearer in Moono
than in Unity: the main thread's rendering, 2.6 ms a frame against Unity's 1.9 ms on the
late-game colony and 4.2 against 2.1 on the mid-game Spaced Out colony (Linux, sampled). This
page splits that time into its parts, explains why the mid-game colony costs more, lines the parts
up against Unity's and ranks fixes.

## The short version

- **Recording draws for the device is half of it.** `GlDrawRecorder.DrawQueue` (shared by the
  OpenGL and Vulkan devices) takes 52–54% of the render phase on both colonies, on Linux and on
  Windows: about 1.1 µs of main-thread time a draw. Overlay canvas batching is the second part
  (16–23%), culling with sorting the third (15–21%).
- **The mid-game colony has 55% more draws** (1,650 a frame against 1,060): more animation
  batches in view (1,320 against 790) and a busier HUD camera. Recording, queued-mesh culling,
  camera-canvas batching and sorting grow with the draws; overlay batching does not.
- **On Windows the shares hold** (Vulkan): 1.43 ms (late) and 2.05 ms (mid) a frame, 12% and
  22% of the frame at 81 and 106 fps, recording 52–54%.
- **Unity does most of this per-draw work off the main thread** (render thread and graphics
  jobs). Its main thread's camera rendering grows only 1.89 → 2.07 ms between the colonies, ours
  2.19 → 3.19 ms.
- **The parts are mostly real work spread thin.** No part has a small, exact fix worth more than
  the runs' spread; the three fixes below are designs. What landed is the measurement itself
  (`ONI_RENDER_TIMING=1` now logs the split).
- **Built since** ([below](#built-on-2026-11-17)): TextMesh Pro's scale check now runs only for
  texts whose scale may have changed (1.84 → 0.06 ms a frame on the mid-game colony, landed).
  Cameras whose culling mask has none of the queued meshes' layers skip them (exact, landed
  untimed: 60% of the queued-mesh visits on the late-game colony). Reusing last frame's draw
  values is exact but hits too few draws to measure; it stays on a local branch.

## Method

- **Build.** u60 with the timing of 56e7855b (U60 build 763325, every DLC), release, default tweaks (a player's
  launch, `ONI_TWEAK_SETTINGS=1`), `--frame-stats`, `ONI_RENDER_TIMING=1`.
- **Linux.** One `linux-perf` runq job per colony: Xvfb at 1280×720, OpenGL through Mesa's d3d12
  driver, PerfProbe holding speed 3 with no autosave and no victory sequence; 60 s warm-up, then
  120 s, with a 60 s `perf record -g` of every thread in the middle (499 Hz, user mode).
- **Windows.** One `win-perf` job per colony: Vulkan (u60's default) with DXGI presentation, the
  frozen 763325 install, 1280×720 on the second monitor, unfocused, High priority, muted, a
  warm-up launch to the main menu first; 12,000 frames, the first 4,800 left out.
- **Timing.** The render phase is split by a stack of parts, each timed exclusive of those inside
  it (`OniMoono.Engine.Rendering.RenderSplit`): a Stopwatch read at each change of part, a few
  dozen a frame. Counts come from counters in the same places. Each figure is the mean of the
  600-frame windows after the warm-up (Linux 7 and 9 windows, Windows 12 and 12).
- **Cross-check.** The sampled time under `FrameRenderer.RenderFrame` is 1.80 ms (late game) and
  2.68 ms (mid game) against 2.19 and 3.19 ms timed: sampling sees user mode only, so the
  futex wakes of the recording threads and other kernel time fall out. The parts' order agrees.
- **What was not measured.** One launch per case: no spread is known. OpenGL on Windows was not
  run: Vulkan's split matched Linux's OpenGL split within a point or two of share, and both
  devices use the same recorder. Unity's own split by profiler marker isn't available (its
  player has no symbols), so the Unity column is the baseline page's phase totals.

## The breakdown

Render phase, main thread, ms a frame (share of the phase):

| Part | Late game, Linux | Mid game, Linux | Late game, Windows | Mid game, Windows |
|---|---|---|---|---|
| Recording draws (`GlDrawRecorder.DrawQueue`) | 1.169 (53%) | 1.698 (53%) | 0.738 (52%) | 1.108 (54%) |
| · of it: the main thread's share of the parallel recording | 0.923 | 1.239 | 0.573 | 0.828 |
| · · waiting for the helpers | 0.145 | 0.153 | 0.089 | 0.082 |
| · of it: the in-order walk (uploads, posting) | 0.166 | 0.303 | 0.085 | 0.162 |
| Overlay canvas batching (`CanvasSystem.Batched` for the HUD) | 0.514 (23%) | 0.512 (16%) | 0.328 (23%) | 0.311 (15%) |
| Culling: queued meshes (`Graphics.DrawMesh`: animation batches) | 0.086 (4%) | 0.194 (6%) | 0.054 (4%) | 0.123 (6%) |
| Culling: camera and world-space canvases batched | 0.088 (4%) | 0.211 (7%) | 0.054 (4%) | 0.093 (5%) |
| Culling: renderers | 0.070 (3%) | 0.106 (3%) | 0.047 (3%) | 0.067 (3%) |
| Culling: distances | 0.014 | 0.022 | 0.003 | 0.005 |
| Sorting each camera's draws | 0.075 (3%) | 0.144 (5%) | 0.069 (5%) | 0.114 (6%) |
| Image effects (`OnRenderImage`: game code and its blits) | 0.067 (3%) | 0.147 (5%) | 0.062 (4%) | 0.109 (5%) |
| Camera command buffers | 0.029 | 0.046 | 0.022 | 0.047 |
| Camera callbacks (`OnPreCull`, `OnPreRender`, `OnPostRender`) | 0.016 | 0.026 | 0.010 | 0.016 |
| Visibility messages, undrawn meshes, uploads, the rest | 0.063 | 0.085 | 0.041 | 0.061 |
| **Render phase** | **2.192** | **3.191** | **1.429** | **2.052** |
| Frame rate (fps, with the timing on) | 39.0 | 44.8 | 81.1 | 106.5 |

The counts behind it, a frame:

| Count | Late game, Linux | Mid game, Linux | Late game, Windows | Mid game, Windows |
|---|---|---|---|---|
| Cameras rendered | 15 | 15 | 15 | 15 |
| Draws recorded | 1,062 | 1,651 | 1,028 | 1,466 |
| · of them the world camera's (`baseCamera MRT`) / the HUD camera's (`uiCamera`) | 761 / 117 | 1,228 / 206 | 734 / 112 | — |
| Draws recorded ahead on the helper threads | 96% | 94% | 96% | — |
| Renderers culled (visits) | 134 | 105 | 129 | — |
| Queued meshes looked at (all cameras) / in view | 12,189 / 791 | 21,228 / 1,324 | 11,594 / 756 | 18,802 / 1,134 |
| Draws sorted | 912 | 1,515 | 880 | 1,307 |
| Canvas batches: overlay / camera | 3 / 6 | 3 / 6 | 3 / 6 | 2 / 6 |
| Overlay canvas draws gathered → items after merging | 727 → 245 | 760 → 293 | 735 → 248 | 710 → 281 |
| Merged runs (rebuilt) | 72 (1.7) | 78 (2.0) | 73 (1.1) | 74 (1.1) |
| Elements baked again (vertices) | 43 (1,043) | 82 (2,151) | 34 (694) | 62 (1,590) |
| UI meshes set by uGUI (changed) / text meshes changed | 16 (15) / 0 | 15 (14) / 0 | 14 (13) / 0 | 10 (9) / 0 |
| Device draws (GL thread, frame stats) | 1,070 | 1,709 | — | — |
| Mesh uploads (GL thread time, ms) | 0.45 | 0.91 | — | — |

Per camera, inclusive ms a frame (Linux): the world camera 0.93 / 1.51, the HUD camera 0.25 /
0.48, `Overlay` 0.06 / 0.13; the other twelve (the light buffer, the timelapse camera, six
texture lerpers, …) 0.2 ms together.

### The canvas update beside it

Not in the render phase but the same kind of work (post-late update, before rendering):

| Part (ms a frame) | Late game, Linux | Mid game, Linux | Late game, Windows | Mid game, Windows |
|---|---|---|---|---|
| TextMesh Pro's scale check (`RescaleIfScaleChanged`, every enabled text) | 1.017 | 0.886 (2,026 texts) | 0.531 | 0.623 |
| Canvas build (`CanvasSystem.Build`: the walk of visible UI) | 0.609 | 0.456 | 0.354 | 0.320 |
| Layout and graphic rebuilds (`CanvasUpdateRegistry`) | 0.119 | 0.195 | 0.061 | 0.089 |
| Root transforms, pixel-perfect moves | 0.027 | 0.027 | 0.014 | 0.016 |

The scale check's figure carries the timer around each of 2,000 calls (about 50 ns each);
sampled, it is 0.69 ms on the late-game colony. No text mesh changed in the steady state.

## Why the mid-game colony costs more

- **More draws, from more animation batches in view.** The camera of the mid-game save shows a
  busier part of the base: 1,320 queued animation batches in view against 790, and the world
  camera records 1,228 draws against 761. The HUD camera draws 206 against 117, the light buffer
  51 against 12.
- **What grows with them** (late → mid, Linux): recording +0.53 ms, queued-mesh culling +0.11
  (each of 15 cameras walks the 1,400 queued meshes), camera-canvas batching +0.12, sorting
  +0.07, image effects +0.08. Overlay batching stays at 0.51 ms: the HUD is the same size.
- **Per draw the main thread spends about 1.1 µs recording** (1.17 ms / 1,062 draws; 1.70 ms /
  1,651), and about 2 µs in the whole render phase. Unity's main thread spends 1.7 µs (late
  game) and 1.25 µs (mid game) a draw on its camera rendering, with about the same draw counts
  (1,094 and 1,650 by its counter).
- **Against the baseline's 4.2 ms:** that figure was U59 with no tweaks, sampled; this page
  measured u60 with the default tweaks, timed (3.19 ms; 2.68 sampled). The difference between the
  two configurations was not measured here.

## Against Unity

Unity's figures are the baseline page's (Linux, sampled, main thread, ms a frame); its phases are
told apart by the script callbacks under them, not by marker.

| Work | Unity late / mid | Moono late / mid (Linux, timed) | Where Unity does it |
|---|---|---|---|
| Camera rendering on the main thread | 1.89 / 2.07 | 2.19 / 3.19 | culling and command building largely on 15 job workers (7–9 ms a frame of engine code) and its render thread |
| · per-draw command building (programs, uniforms, textures) | not on the main thread (inferred) | 1.17 / 1.70 (plus 1.45 core-ms on 3 helper threads, late game) | graphics jobs and the render thread |
| · culling | jobs (inferred) | 0.26 / 0.53 | culling jobs |
| · UI overlay batching | in the canvas phase, partly on jobs | 0.51 / 0.51 | native canvas batching |
| Canvas phase (+ uGUI/TMP rebuilds) | 1.23 + 1.99 (late) | 1.77 (late: build, scale checks, rebuilds) | managed TMP and uGUI, native canvas |

- **Work Moono does that Unity skips:** none found in the render phase; the parallel recording
  is verified to give the serial loop's commands, and every part maps to something Unity also
  does. The scale check of every text is in Unity too (TextMesh Pro's update manager reads each
  text's `lossyScale` every frame), there through a native call.
- **Work Unity does natively or off-thread:** the per-draw command building (recording),
  culling, and canvas batching. That is where the 0.3 ms (late) and 1.1 ms (mid) the main thread
  pays more come from, and why Unity's main thread barely grows with the draws.
- **Rendering plus canvas** (the baseline's fairer sum): Unity 3.12 / 2.86 ms, Moono 3.96 / 4.75
  ms timed here with the canvas update counted in full (the baseline counted 3.31 / 4.91).

## Fixes, ranked by expected saving

Savings are main-thread ms a frame, Linux (Windows about 0.6× as much). None was built: none is
small, contained and exact with a saving a 3+3 series on Windows could tell from its spread
(launches there differ by up to 43%).

### 1. Record less per draw: reuse last frame's recording (0.4–0.8 ms; Windows 0.3–0.4)

- **What it changes.** Most draws are the same as last frame's: the same animation batch with
  the same program, material version, property-block version, matrix and camera. The recorder
  would key each recorded draw by those inputs (program, `MaterialValues` versions, the block's
  `Version`, `ObjectToWorld`, the camera constants, the textures' device versions) and copy last
  frame's values and samplers when the key matches, skipping `GlUniforms.RecordFrom`,
  `RecordAll`'s per-uniform `Sets` lookups and the sampler loop. Programs that read time
  (`_Time`, `_SinTime`, `unity_DeltaTime`) never reuse.
- **Where the time is now** (late game, all recording threads, 1.95 core-ms a frame):
  `RecordAhead` itself 35%, `GlUniforms.Resolve` 14%, `GlUniforms.Sets` (dictionary lookups per
  uniform per draw) 11%, `GlTextures.IsCurrent` 7%.
- **Exactness.** Pixel-identical as long as the key covers every input a recording reads; a miss
  in the key would draw a stale value. `ONI_VERIFY_PARALLEL_DRAWS` already records each draw again
  and compares (`ProgramInputs.SameAs`); a reused draw would be checked the same way.
- **A smaller, exact step:** cache, per program and per property block's set of keys, which
  uniforms the block sets (needs a key-set version on `PropertySheet`, bumped on add, remove and
  clear only). That removes the `Sets` lookups: about 0.3 core-ms, 0.07 ms of the main thread.
- **Not chosen: more helper threads.** Three helpers (a quarter of the cores, at most three)
  leave the main thread a quarter of the recording; five would leave a sixth (about −0.15 ms
  late, −0.2 mid) but cost more futex wakes per queue and compete with the game's 15 job
  workers, which is why the cap exists.

### 2. Reuse an unchanged overlay canvas's batches (0.3–0.4 ms; Windows about 0.2)

- **What it changes.** Each frame `CanvasSystem.Batched` turns the HUD's 730–760 element draws
  into 245–293 items: an item per element (baked-mesh check, draw state, property sheet), the
  layered order's check against last frame's (`LayeredOrder.Matches`, every draw), then the
  merged runs' checks. Nearly nothing changes from frame to frame (1–2 merged runs rebuilt, 34–82
  elements baked again). A batch whose draw list, elements' versions (geometry, colour, cull,
  clip rect, textures, materials' versions) and canvas transform are unchanged would hand out
  last frame's items again, copied into this frame's draw items.
- **Exactness.** Needs one version per canvas renderer covering every input `Item` reads, bumped
  wherever any of them changes; a missed bump shows a stale HUD element. A verify mode would
  build the items as now every Nth frame and compare. The same cache serves the six camera and
  world-space canvas batches culled per camera (0.09–0.21 ms, part of fix 3's rows).

### 3. Cull and sort with less per-draw work (0.1–0.3 ms; grows with draws)

- **Queued meshes:** each of the 15 cameras walks every `Graphics.DrawMesh` mesh (12,000–21,000
  visits a frame for 790–1,320 in view). A layer mask of the frame's queued meshes, kept as they
  are queued, lets a camera whose culling mask misses all of them skip the loop: exact (the
  skipped visits all end in the mask test), about 0.05 ms ([landed](#smaller-ones)). Lists by
  layer would save more, with sequence numbers taken from the queue position so that ties keep
  their order.
- **Sorting:** the opaque and transparent sorts compare up to five fields through a delegate. A
  packed key (queue, sorting layer and order, distance's bits, sequence) sorted as one `long`
  gives the same order, since the sequence makes every key distinct: about half of 0.075–0.144 ms.
- **Exactness:** both keep every draw and its order; the sort must keep `-0`/NaN distances
  ordered as `float.CompareTo` does.

### Beside the render phase: TextMesh Pro's scale check (0.5–0.8 ms; Windows about 0.4)

Every enabled text (2,026 on the mid-game colony) is called every frame to compare its lossy scale
with the one its mesh was made for. The scale itself is cached on the transform, so the time is
the call and the cache misses of 2,000 objects. Exact design: texts register with their
transform, and `TransformNative.MarkDirty` (where the cached scale is invalidated) queues the
texts under the moved subtree; the check visits only those, in the order the subscribers would
have run. Risk: the transform's dirty path is hot, and any scale change that bypasses
`MarkDirty` would leave a text unscaled. Unity pays the same per-text check.

## Built on 2026-11-17

### TextMesh Pro's scale check, for moved texts only (landed)

- **What it does.** Each text's transform gets a slot (`TextScaleWatch`). Every invalidation of
  the transform's cached world data (`TransformNative.WorldChanged`: its own change or an
  ancestor's) arms the slot. The text's `willRenderCanvases` entry carries the slot and keeps
  its place among the subscribers; the call skips it while the slot is disarmed. A check that
  finds the scale settled disarms it; a rescale, a regeneration and every new SDF scale arm it.
- **Why exact.** The check reads the transform's cached lossy scale, which changes only through
  an invalidation; a disarmed text would read the scale it settled with. The early returns
  (static scale, nothing generated) leave the slot armed.
- **Engine, not a tweak.** Like the engine's other exact fast paths (canvas skips, the cull
  cache, mesh reuse): always on, with a verify mode, `ONI_VERIFY_TEXT_SCALES=1`, which calls
  every skipped check and logs each that would have acted. `qa.sh verify` and CI's headless
  verify runs set it.
- **Cost on the transform path.** One field test per transform marked dirty: 8.43 ns a
  transform before, 8.44 after (median of five runs each of a 4,681-transform hierarchy moved
  400 times, release build).

| Linux, one launch each | Late game, before | after | Mid game, before | after |
|---|---|---|---|---|
| Scale check (ms a frame) | 0.829 | 0.007 | 1.837 | 0.057 |
| · checks called a frame | 2,294 | 10 | 2,025 | 37 |
| Post-late phase (frame stats, ms) | 1.75 | 0.68 | 4.48 | 2.56 |
| Frame rate (PerfProbe, timing on) | 35.2 | 45.8 | 18.9 | 19.2 |

- **The machine was slow most of the afternoon** (the mid-game pair ran at 19 fps, against 45 in
  this page's first runs), so frame rates compare only within a pair, and even there they move
  with the machine. The post-late phase fell by the scale check and more; the late pair's
  launches came 3 hours apart (a first after-launch on a machine at half speed was run again).
- **Verify runs:** 0 wrong on both colonies (Linux, OpenGL) and on Windows (Vulkan, late game):
  5.4, 5.1 and 13.7 million skipped checks run.
- **Windows, alternating 3+3 launches, Vulkan, 7,200 frames each after warm-up** (median
  [range]):

| Colony | Before fps | After fps | Before 1% low | After 1% low |
|---|---|---|---|---|
| Late game | 68.0 [26.2–68.5] | 28.5 [26.2–80.2] | 17.9 [11.5–22.3] | 11.7 [8.9–25.5] |
| Mid game | 44.2 [37.1–48.3] | 43.2 [38.9–45.0] | 18.9 [16.5–19.3] | 18.5 [15.9–18.8] |

  Not resolved: the machine slowed partway through the late-game series (both launches of the
  third pair ran at 26 fps), and the mid-game series ran slowed throughout. The expected
  saving, about 0.5 ms of a 12–15 ms frame, is far inside these ranges.

### Reusing last frame's draw values (built, not landed)

- **What it does.** Each queued draw item keeps, per pass, the values it recorded and every
  input `GlUniforms.RecordFrom` reads: the program, the material's values with a new content
  stamp (bumped only when a resolve changes their bits), the block and its version, the object
  matrix, the camera constants, the conventions and the render-texture flag, all bit for bit.
  Programs reading the time or a texture's size never reuse.
- **Exact:** `ONI_VERIFY_DRAW_REUSE=1` made every reused draw's values again: 0 wrong in 0.94
  million (late), 0.95 million (mid) on OpenGL and 2.2 million on Vulkan.
- **Why not landed.** It reuses only 25–41% of the passes it could: draw items are pooled by
  position, so one moved draw shifts the rest (material and block misses, 290–630 passes a
  frame), the material values' content changes every frame for some programs (stamp, about
  280), and blocks are set again each frame (block version, 95–115). Its saving could not be
  told from the afternoon's spread, and every miss pays an extra copy. Keying the record on the
  draw's own objects (the renderer, or the queued mesh's block) instead of the pooled item would
  be the next step.

### Smaller ones

- **Skipping a camera whose culling mask misses every queued mesh's layer** (landed). The
  engine keeps the layers of each queued-mesh list, one bit per layer (`1 << Layer`, the bit
  the mask test reads), updated wherever a list gains meshes or is cleared. A camera whose
  culling mask has none of a list's bits skips the list.
  - **Exact.** Every skipped mesh would fail the mask test. `Graphics.DrawMesh` is the only
    way into the lists, and every overload passes its layer through; a mesh queued for one
    camera still needs that camera's mask to have its layer, so the camera argument only
    narrows further. Command buffers keep their own list. A unit test draws two layers for two
    cameras.
  - **Work skipped.** On the late-game colony (Linux, one launch, a counting build), 9 of the
    15 cameras a frame skip the list: about 7,300 of 12,160 visits a frame, with the meshes in
    view unchanged (790).
  - **Not timed.** The queued-mesh cull row is 0.09–0.47 ms. The change only removes work, so
    it landed without frame-rate runs while the machines were under heavy load.
- **Sorting on one packed key:** not built. The transparent order is not a key: two
  `Graphics.DrawMesh` draws tied on everything else go by material, a renderer's draw between
  them by sequence, so three such draws can order in a cycle and the result depends on which
  pairs the sort compares. A packed key would order them differently. The opaque order is a
  plain key, but sorting is 0.08–0.2 ms in all.

## What landed

- **`render: split the main thread's rendering under ONI_RENDER_TIMING`** (u60 56e7855b, cherry-picked
  to master): with `ONI_RENDER_TIMING=1`, every 600 frames a `render split` line follows the
  `render timing` line, with the parts above, their counts, each camera's inclusive time, the
  canvas update's parts and each `willRenderCanvases` subscriber's time and calls. Off, it costs
  a static bool test per part change and a few counter increments.
- **`tmp: check a text's scale only after its transform changed`** (u60 648b50d3, master
  d0cac00b): above.
- **`render: skip queued meshes a camera's culling mask has no layer of`** (u60 5ebe7b7c,
  master 5a777ec9): above.
