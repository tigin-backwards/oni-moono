# Managed allocations while paused (2026-11-14)

Where the managed memory goes that each player hands out a frame while the colony is paused, why
the [re-profile](reprofile-2026-11-13.md#managed-allocations) showed Moono above the original
there (7.6 against 3.1 KB a frame on U60's mid-game colony), and what was changed. KB and MB are
powers of 1,000.

**In short:**
- **Where Moono's 7.3 KB a frame went:** 2.2 were the measurement's own (PerfProbe's section
  counters under .NET), 1.4 were two game tweaks', 1.6 the engine's, 2.1 the game's own code.
- **After the fixes** Moono hands out 2.8 KB a frame on that colony with the default HUD,
  against the original's 3.2, and less than the original with a management screen or an overlay
  open too.
- **In a player's terms it never mattered.** Before the fixes, a paused colony at 60 fps reached
  one gen0 collection about every quarter of an hour.

## Method

U60 (build 763023), Linux under WSL2, Xvfb at 1280×720, OpenGL (the software Vulkan device there
is not used), `scripts/alloc-compare.py run … --fps N --sites` with PerfProbe's `pausefirst.txt`
(the colony never runs) and `screens.txt`: the default HUD, then the vitals screen open, then the
oxygen overlay, each held for whole 600-frame windows. One launch each. Both players held the
cap (34 fps on the mid-game colony 2minnow, 20 on the late-game colony U57), so a frame and a
second compare alike.

- **Totals:** PerfProbe's `PERF alloc` counter over whole windows: .NET's process total for
  Moono, Boehm's for the original. Boehm counts a plain object as 32 B where .NET counts 24 and
  a `byte[100]` as 144 B against 128 (the probe's calibration line), so the original's figures
  run 10–30% high for the same objects.
- **Moono by call site:** `--trace`, dotnet-trace with the runtime's GC keyword at verbose level
  (`GCAllocationTick`), read by tools/AllocTrace. The runtime raises one event about every 100 KB
  a thread hands out, for the object that crossed the mark, with its stack and type: large
  objects are over-represented among single samples, and a site below about 0.05 KB a frame has
  too few samples to rank (762 samples in the HUD phase before the fixes, 9 B a frame each; about
  110 after, 33 B a frame each). Sites are named by the first frame outside the BCL; a property
  the JIT inlined shows as its caller.
- **The original:** only totals by update section (PerfProbe's `sites.txt` with Boehm's
  process-wide counter). Boehm hands a thread its small objects in batches, so a section's
  figure is good to a few hundred bytes a frame at best. There are no stacks: the original's
  split below is by section and by reading the game's code.

## Where it went (mid-game colony, default HUD, before the fixes)

Moono u60 a069cce1, 328 s of the HUD phase, 7.28 KB a frame by the counter and 7.29 sampled.

| Site | B a frame | Share | Whose |
|---|---|---|---|
| Method handle stubs at PerfProbe's section patches | 2,160 | 30% | the measurement (.NET only) |
| `fast-jobs`: a batch and its event for each `JobManager.Run` | 908 | 12% | tweak |
| `path-probe-repair`: an event, list and batch for each suit marker update | 495 | 7% | tweak |
| `Grid.UpdateSuitMarker`: a dictionary node a call | 325 | 4% | game, as in the original |
| `WorldInventory.Update`: a boxed set enumerator | 306 | 4% | game |
| IMGUI layout: two groups for each `OnGUI` | 277 | 4% | engine |
| Multi-target framebuffer: its key's string at each bind (GL thread) | 268 | 4% | engine (OpenGL) |
| `Game.UnsafeSim200ms`: a `SimActiveRegion` | 258 | 4% | game |
| `BloomEffect`: `Vector2[]` offsets for each blit | 287 | 4% | game |
| Draw recording's fork-join: a run object for each loop | 172 | 2% | engine (OpenGL) |
| Multi-target binding: an array and a target for each `SetRenderTarget` | 162 | 2% | engine (OpenGL) |
| Collider snapshot for the cursor's raycast | 153 | 2% | engine |
| Present: a closure and its delegate | 134 | 2% | engine (OpenGL) |
| Hover text card, selection, status item names, formatted temperatures and masses (strings) | 402 | 6% | game |
| `PinnedResourcesPanel.RefreshLine`: a closure a line | 105 | 1% | game |
| `LaunchPadTower` coroutine: a boxed int a step | 115 | 2% | game |
| Other engine sites (draw lists, visibility lists, command arrays) | 382 | 5% | engine |
| Other game sites | 267 | 4% | game |
| The probe's own log lines, the runtime | 95 | 1% | |

By owner: the measurement 2.2 KB a frame, the game 2.1, the engine 1.6, tweaks 1.4. Other threads
hand out 0.4 of it (the GL thread's key strings, the probe thread's batches).

**The original**, same colony and HUD: 3.18 KB a frame (108 KB a second). By section: `(other)`
1.86 (everything outside the probe's sections: Unity's behaviours, as `SuitMarker.Update`,
`WorldInventory.Update`, the hover card and the bloom effect), `Game.UnsafeSim200ms` 0.29,
`PinnedResourcesPanel` 0.27, `BrainScheduler.RenderEveryTick` 0.22, `Global.Update` 0.18,
`GlobalChoreProvider` 0.12, and 0.05–0.06 each for four more. Moono's game-code part, 2.1 KB in
.NET's sizes, is the same code: about 2.6 KB in Boehm's sizes against the original's 3.2 in all.

**By kind, as asked:**
- **(a) The game's own code, the same in both players:** 2.1 KB a frame. Candidates for exact
  tweaks, none taken: the suit marker's dictionary write every frame (0.3–0.5), the boxed
  enumerator in `WorldInventory.Update` (0.3), the bloom offsets (0.3), `SimActiveRegion` (0.3).
- **(b) Game code that hands out more under .NET than under Mono:** one pattern found, and not
  in the game: `ldtoken` of a method with `MethodBase.GetMethodFromHandle`, which Harmony emits
  for a patch that takes `__originalMethod`. .NET makes a 48 B stub at each call; Mono makes
  none. PerfProbe's section patches did that 24 times a frame (2.2 KB). A mod with such a patch
  on a per-frame method pays the same; no tweak of ours takes `__originalMethod` on one. No
  boxing, enumerator, closure or string site of the game stood out as .NET-only, but the
  original gives no stacks to compare site by site.
- **(c) The engine:** 1.6 KB a frame, 0.7 of it in the OpenGL device. Nothing from uGUI's layout
  or canvas rebuilds, TextMeshPro or input while the HUD is still.
- **(d) Tweaks:** 1.4 KB a frame, two sites.

## What is on screen

KB a frame, mid-game colony, one launch each (the original 2,400 HUD frames and 1,200 for each
of the others; Moono before 10,800 and 1,800; after 2,400 and 1,200, the build as committed,
u60 e7fe719e):

| | Original | Moono before | Moono after |
|---|---|---|---|
| Default HUD | 3.18 | 7.28 | 2.78 |
| Vitals screen open | 4.43 | 9.95 | 3.76 |
| Oxygen overlay | 2.88 | 7.17 | 2.55 |
| Default HUD, KB a second at 34 fps | 108 | 247 | 94 |

The vitals screen cost Moono 2.7 KB a frame more than the HUD and the original 1.25. Of Moono's,
1.9 KB was one engine site: the game's anim renderer on a canvas (each portrait) clears its
`CanvasRenderer` and sets `materialCount = 1` every frame, and each made a new one-material
array. The rest is the game's tooltip strings, in both players. The overlay changes little in
either.

## What was changed

| Fix | Before, B a frame | After | Kind |
|---|---|---|---|
| PerfProbe: each section patch has a prefix of its own, without `__originalMethod` | 2,160 | 0 | measurement |
| `fast-jobs`: a batch serves the next run once the caller and every worker it was handed to are done with it (`BatchHandOver`) | 908 | about 60 (a worker still held it) | tweak |
| `path-probe-repair`: a suit marker update that leaves the marker as it was is no change | 495 | 0 | tweak |
| `CanvasRenderer.Clear` keeps the emptied material array for the next `materialCount` of its length | 1,880 (vitals) | 0 | engine |
| Multi-target framebuffer found by the target that was bound last, checked against the names | 268 | 0 | engine (OpenGL) |
| Multi-target binding recorded again when the textures are the same | 162 | 0 | engine (OpenGL) |
| Fork-join run object used again once no helper holds it | 172 | 0 | engine (OpenGL) |
| Collider snapshot kept until a collider is added or destroyed | 153 | 0 | engine |
| Present recorded as a state command | 134 | 0 | engine (OpenGL) |
| IMGUI: an empty list of areas serves the next recording | 277 | about 100 | engine |

The suit marker events were more than garbage: with a checkpoint in the colony, every frame gave
the next path probe a change to repair from. Now a probe sees one only when the marker's counts,
flags or reservation sets differ after the call. The tweak's verify mode audits the same fields.

**After, by section** (mid-game colony, B a frame; HUD, vitals, oxygen overlay; the original's
HUD beside it):

| Section | HUD | Vitals | Overlay | Original, HUD |
|---|---|---|---|---|
| `(other)` | 1,893 | 2,774 | 1,768 | 1,855 |
| `Game.UnsafeSim200ms` | 240 | 240 | 240 | 288 |
| `PinnedResourcesPanel` | 135 | 133 | 137 | 274 |
| `PropertyTextures.LateUpdate` | 121 | 118 | 116 | 0 |
| `BrainScheduler.RenderEveryTick` | 93 | 88 | 94 | 222 |
| Other threads | 92 | 24 | 18 | (in the rows) |
| `GlobalChoreProvider` | 82 | 83 | 83 | 119 |
| `Global.Update` | 48 | 48 | 48 | 176 |
| `CanvasUpdateRegistry.PerformUpdate` | 32 | 32 | 32 | 4 |
| `KScreenManager.Update` | 4 | 190 | 4 | 5 |
| `Game.LateUpdate` | 19 | 2 | 2 | 21 |
| Total | 2,778 | 3,755 | 2,554 | 3,175 |

By owner, from an earlier build of the same fixes (about 110 samples, so ±0.3 KB): the game 2.5
KB a frame, the engine 0.7, tweaks 0.1.

**Under load** the figure is higher, before the fixes and after. Traced launches of the build
before (feb72799) and after, each beside 10 busy loops (11 cores busy beside the player, 34 fps
held): 5.40 KB a frame before, 3.78 after. Inside `Game.LateUpdate` 0.58 before and 1.04 after,
against 0.02 on a quiet machine. Before, that was `fast-jobs`' batches (0.37) and the conduit
flow meshes' channel arrays (0.24); after, the channel arrays alone (about 1.0, 22 samples):
`ConduitFlowMesh.End` sets its mesh every frame, and while the GL thread, behind, still holds
the mesh's spare arrays for an upload, the mesh makes new ones (`MeshNative.NewChannelAtLeast`).
How much depends on how far behind the GL thread is: beside a release QA run (31.8 fps of 34)
it came to 5.1 KB a frame and 8.0 in all. The site is not one of the fixes' and was left alone.

**Late-game colony (U57, 20 fps), after:** 6.65 KB a frame with the HUD (133 KB a second), 9.10
with the vitals screen, 5.36 with the oxygen overlay. The re-profile had Moono at 9.08 and the
original at 8.62 with the HUD. The game hands out 4.0 of it, the engine 2.1.

## What is left

- **The engine, late-game HUD (2.1 KB a frame):** a chart's canvas rebuild every frame:
  `TMPro.TextParser.Parse`'s state (0.7), the canvas arrays' pool (0.55), the nested canvas
  merge (0.3), `LayoutRebuilder` (0.2), IMGUI's root group (0.3).
- **The engine, mid-game HUD (0.7):** IMGUI's root group for each `OnGUI` (0.1), draw and
  command lists (0.3), the canvas arrays' pool (0.2).
- **The game (2.5 mid-game, 4.0 late-game):** as listed under (a), and late-game
  `KBatchedAnimUpdater.ProcessRegistrations` (0.9), the launch pad's coroutine (0.55) and
  `PinnedResourcesPanel` (0.5).
- **The engine, under load:** the conduit flow meshes' channel arrays (above), 1–5 KB a frame
  while the GL thread is behind.
- **Vulkan:** not measured. The device-side fixes are in the OpenGL device; the Vulkan device's
  own per-frame sites are unknown.

## Does it matter?

No. The player runs Server GC with a gen0 budget of up to 210 MB (`GCGen0MaxBudget`); in the
re-profile's speed-3 spans a gen0 collection came about every 300 MB. No collection of any
generation ran in any paused span measured (up to 9 minutes).

| Paused, default HUD | KB a frame | MB a second at 60 fps | at 144 fps | A gen0 collection every (60 fps) |
|---|---|---|---|---|
| Moono before, as measured | 7.3 | 0.44 | 1.05 | 8–11 min |
| Moono before, without the probe's part | 5.1 | 0.31 | 0.73 | 11–16 min |
| Moono after | 2.8 | 0.17 | 0.40 | 21–30 min |

The intervals are estimates from the budget and the allocation rates; the pause of such a
collection was not measured paused (the speed-3 spans' collections took 40–160 ms each, gen1 and
gen2 among them, on a heap in motion).

## Not tested

- Whether the conduit meshes' channel arrays under load differ between the builds beyond what
  two launches show (6 samples against 22), and a bound on them when the GL thread is far behind.
- The late-game figures are of an earlier form of PerfProbe's fix, whose section split was
  wrong; its totals stand.
- Frame rates on Windows and Vulkan (a smoke run of each passed: the colony at speed 3, the
  vitals screen, an overlay, an autosave), U59 paused figures (master has the same fixes).
- One launch for each figure; paused figures of two launches of the same build agreed within 4%
  (7.56 and 7.28).
