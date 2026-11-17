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
  skipped visits all end in the mask test), about 0.05 ms. Lists by layer would save more, with
  sequence numbers taken from the queue position so that ties keep their order.
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

## What landed

- **`render: split the main thread's rendering under ONI_RENDER_TIMING`** (u60 56e7855b, cherry-picked
  to master): with `ONI_RENDER_TIMING=1`, every 600 frames a `render split` line follows the
  `render timing` line, with the parts above, their counts, each camera's inclusive time, the
  canvas update's parts and each `willRenderCanvases` subscriber's time and calls. Off, it costs
  a static bool test per part change and a few counter increments.
