# Managed allocations: the original and Moono (2026-11-10)

How much managed memory each player hands out while a colony runs, and where. KB, MB and GB are
powers of 1,000. "A game second" is one second of game time; "a real second" one of wall-clock
time. Method and fixtures in [Method](#method).

The [U60 runs after allocation batch 4](#u60-after-allocation-batch-4-2026-11-11) are the latest
figures. The rest of this page measured an earlier u60 build (cf2d0849) and U59.

## U60 after allocation batch 4 (2026-11-11)

Moono u60 4ff1e540 (allocation batches 1 to 4) against the original and against the u60 build
measured in the rest of this page (cf2d0849, batches 1 to 3), all three held at the same frame
cap: 20 fps on the late-game colony, 34 fps on the mid-game one. The original can't reach either
cap, so it ran at its own rate. Each row is one 600 s window at speed 3, Linux. Other load was
0.5–0.9 cores.

**Late-game colony, 20 fps cap**

| Run | fps | Game s a real s | KB a frame | MB a game s | MB a real s | Collections in 600 s | GC pause |
|---|---|---|---|---|---|---|---|
| Original | 6.8 | 1.37 | 294 | 1.47 | 2.01 | 1 | not measured (incremental) |
| Moono, earlier build | 19.6 | 2.99 | 283 | 1.86 | 5.55 | 5 (2 gen1, 1 gen2) | 361 ms in all |
| Moono, batch 4 | 19.6 | 2.99 | 217 | 1.42 | 4.25 | 3 (1 gen1) | 526 ms in all |

**Mid-game colony, 34 fps cap**

| Run | fps | Game s a real s | KB a frame | MB a game s | MB a real s | Collections in 600 s | GC pause |
|---|---|---|---|---|---|---|---|
| Original | 12.5 | 2.48 | 60 | 0.30 | 0.75 | 1 | not measured (incremental) |
| Moono, earlier build | 33.7 | 2.99 | 105 | 1.18 | 3.53 | 3 (1 gen1) | 151 ms in all |
| Moono, batch 4 | 34.0 | 2.99 | 58 | 0.66 | 1.98 | 2 (1 gen1) | 142 ms in all |

- **A frame, Moono now allocates less than the original on both colonies:** 217 against 294 KB
  on the late-game colony, 58 against 60 on the mid-game one.
- **A game second, Moono is level with the original on the late-game colony** (1.42 against
  1.47 MB, 0.97×), though it draws 6.5 frames to a game second against the original's 5.0. On the
  mid-game colony it is 2.2× (0.66 against 0.30 MB), down from 3.5–4.2× before, at 11.4 frames
  to a game second against 5.0.
- **Batch 4 cut** 23% a frame on the late-game colony (283 → 217 KB) and 44% on the mid-game one
  (105 → 58 KB), with two collections fewer in 600 s on the late-game colony and one fewer on the
  mid-game one.
- A first late-game pair, run with the site probe loaded, gave 293 KB a frame for the original and
  223 for Moono: the same within 3%.

### Where it goes after batch 4

PerfProbe's `sites.txt` and `scripts/alloc-compare.py` (method in
[PERFORMANCE.md](../PERFORMANCE.md#managed-allocations-against-the-original)), one 600 s run each at
the same caps as above, Moono u60 4ff1e540. The totals came within 7% of the runs above, either way:
the runs vary that much, and the site probe's brackets cost little. These tables replace the earlier build's U60 tables under
[Where it goes](#where-it-goes-u60-cf2d0849-and-u59). As there, the original's counter is
process-wide and Moono's the main thread's, so `BrainScheduler`, `(other)` and "(other threads)"
compare only together.

**Late-game colony, 20 fps cap** (the original at 6.8 fps, 5.5 frames to a game second; Moono at
18.9 fps, 6.4)

| Section | Original KB a game s | Moono KB a game s | Original KB a frame | Moono KB a frame |
|---|---|---|---|---|
| BrainScheduler.RenderEveryTick | 385 | 33 | 69.6 | 5.2 |
| (other threads) | (in the rows) | 373 | | 58.5 |
| Game.UnsafeSim200ms | 326 | 330 | 59.0 | 51.8 |
| (other) | 199 | 223 | 36.0 | 34.9 |
| CanvasUpdateRegistry.PerformUpdate | 145 | 119 | 26.2 | 18.6 |
| Game.Update | 84 | 85 | 15.2 | 13.3 |
| Work(Pickup) | 64 | 59 | 11.5 | 9.3 |
| SolidTransferArm SIM_1000ms | 41 | 25 | 7.5 | 3.9 |
| KBatchedAnimUpdater.LateUpdate | 38 | 25 | 6.9 | 3.9 |
| HighEnergyParticle states | 29 | 29 | 5.3 | 4.6 |
| Global.Update | 28 | 15 | 5.1 | 2.4 |
| World.LateUpdate | 8 | 27 | 1.5 | 4.3 |
| SapTree states | 24 | 15 | 4.4 | 2.4 |
| Game.LateUpdate | 11 | 5 | 1.9 | 0.8 |
| Total | 1,508 | 1,466 | 273 | 230 |

**Mid-game colony, 34 fps cap** (the original at 11.6 fps, 5.0 frames to a game second; Moono at
33.6 fps, 11.2)

| Section | Original KB a game s | Moono KB a game s | Original KB a frame | Moono KB a frame |
|---|---|---|---|---|
| (other) | 57 | 247 | 11.4 | 22.0 |
| World.LateUpdate | 2 | 75 | 0.4 | 6.7 |
| (other threads) | (in the rows) | 62 | | 5.5 |
| CanvasUpdateRegistry.PerformUpdate | 18 | 38 | 3.6 | 3.3 |
| Global.Update | 29 | 31 | 5.7 | 2.8 |
| BrainScheduler.RenderEveryTick | 24 | 27 | 4.8 | 2.4 |
| Game.Update | 21 | 26 | 4.2 | 2.4 |
| Work(Pickup) | 23 | 25 | 4.6 | 2.3 |
| KBatchedAnimUpdater.LateUpdate | 25 | 16 | 5.0 | 1.5 |
| Game.UnsafeSim200ms | 17 | 21 | 3.3 | 1.8 |
| UpdateNavigator | 10 | 13 | 2.0 | 1.2 |
| Pathfinding.UpdateNavGrids | 1 | 11 | 0.2 | 1.0 |
| Game.LateUpdate | 1 | 9 | 0.1 | 0.8 |
| Total | 302 | 680 | 60 | 60 |

Against the earlier build's U60 tables, a frame:
- **The canvas rebuilds** fell from 44.2 to 18.6 KB on the late-game colony, now below the
  original's 26.2, and from 16.6 to 3.3 on the mid-game one. Most of what is left is a chart's
  `UILineRenderer` (game code, as in the original): 15.6 KB of the late-game colony's 19.4 in the
  trace below.
- **`SpriteSheetAnimManager`** fell from 2.9 KB to 0.01, `PropertyTextures.LateUpdate` from 1.0
  to 0.5, and `Game.LateUpdate` from 1.9–2.0 to 0.8.
- **`(other)`**, the engine's own frame with the scripts it runs, is 35 KB on the late-game colony
  (the original 36, counting other threads too) and 22 on the mid-game one (the original 11).
- **`World.LateUpdate`** is unchanged: 4.3 and 6.7 KB against the original's 1.5 and 0.4. It is
  now the largest engine-facing section on the mid-game colony after `(other)`.
- The game's own sections (the sim, `Game.Update`, the workables and state machines) still match
  the original a game second, a little lower in Moono.

**By thread and call site.** A 180 s allocation trace of each colony (`--trace`, same caps; the
counter 228 and 68 KB a frame):
- The late-game colony's other threads are the chore collection: `RemoteChore.CollectChores` on
  the game's job threads hands out 46 KB a frame of its 52, which the original books under
  `BrainScheduler.RenderEveryTick` (that section and the other threads come to 406 KB a game
  second in Moono against the original's 385). On the mid-game colony the other threads hand out
  9 KB a frame, mostly the GL thread's texture uploads (2.6) and `Parallel.For`'s own state.
- `(other)` splits into game, uGUI and mod code (25 KB a frame on the late-game colony, 12 on the
  mid-game one) and the engine's own (10 and 14). Batch 4's engine sites come to 5.3 and 2.6 KB a
  frame in all (canvas copies 2.5 and 0.5).
- The largest single sites left in `(other)`: `TerrainBG.LateUpdate`'s `Vector4[]` (4–5 KB a
  frame on both colonies), the texel copies' tasks (1–3.4) and, on the mid-game colony,
  `ReadPixels` and `AsyncGPUReadback` read-backs (3.6).

### After allocation batch 5

The two largest sites where Moono allocated more than the original:
- **`TerrainBG.LateUpdate`'s `Vector4[]`**: the engine's own. TerrainBG sets two array properties
  (255 and 28 vectors) on its back-wall block before each frame's draw, and the engine made a new
  array at each assignment. Unity copies the values into the property's storage; so does Moono
  now, into the array made at the first assignment.
- **`World.LateUpdate`**: Moono's own tweaks, not the game or the engine. fast-anim-batches' two
  `Parallel.For` calls a frame (its tasks, replicas, ranges and closures), an invocation list for
  each controller with a position delegate, and fast-symbol-overrides' sort of many writes with a
  comparer (a delegate a call). The tweaks now run the same items with a loop of their own on the
  thread pool, read a single-target delegate's method directly, and sort without a comparer.
  Their verify mode reported no mismatches on either colony.

u60 b928b77a against the same build with batch 5, one 600 s run each at the same caps as above,
`sites.txt` on. The machine was busier than for the runs above, so neither player reached the
mid-game cap, and the late-game pair had uneven background load (2.2 cores beside the first
run, 0.6 beside the second), so its frame rates differ.

| KB a frame | Mid-game, before | Mid-game, after | Late-game, before | Late-game, after |
|---|---|---|---|---|
| In all | 64.9 | 54.7 | 234 | 214 |
| `World.LateUpdate` | 6.7 | 2.0 | 4.6 | 2.9 |
| `(other)` | 23.6 | 20.1 | 35.1 | 28.0 |
| Other threads | 5.3 | 2.3 | 58.0 | 56.9 |
| fps | 30.0 | 29.0 | 18.7 | 20.0 |
| Collections in 600 s | 2 (171 ms) | 1 (79 ms) | 3 (302 ms) | 3 (235 ms) |

A 180 s allocation trace of the mid-game colony after batch 5 has no `TerrainBG` allocations
left. `World.LateUpdate` is 2.0–2.9 KB a frame against the original's 0.4–1.5. In the trace
(2.7 KB a frame) the largest parts left are the thread pool's record of each helper the tweaks'
loop queues (0.9 KB) and the game's own override blocks (0.5 KB, as in the original).

## Summary (u60 cf2d0849 and U59, 2026-11-10)

Each row is one 600 s window at speed 3, Linux, Moono with its default tweaks. "Capped" is Moono
held at 7 fps, about the original's frame rate on the late-game colony. "Other load" is the CPU
the rest of the machine used meanwhile, in cores.

**U60, late-game colony**

| Run | fps | Game s a real s | KB a frame | MB a game s | MB a real s | Collections in 600 s | GC pause | Other load |
|---|---|---|---|---|---|---|---|---|
| Original | 6.8 | 1.37 | 280 | 1.40 | 1.91 | 1 | not measured (incremental) | 4.2 |
| Original | 6.2 | 1.20 | 275 | 1.43 | 1.71 | 1 | not measured (incremental) | 5.8 |
| Moono | 28.2 | 2.97 | 238 | 2.25 | 6.70 | 4 (1 gen1) | 578 ms in all | 3.9 |
| Moono | 35.9 | 2.97 | 222 | 2.69 | 7.99 | 6 (2 gen1, 1 gen2) | 467 ms in all | 2.9 |
| Moono capped | 7.0 | 1.40 | 363 | 1.82 | 2.55 | 2 | 227 ms in all | 5.2 |
| Moono capped | 7.0 | 1.38 | 365 | 1.85 | 2.55 | 2 | 195 ms in all | 3.1 |

**U60, mid-game colony**

| Run | fps | Game s a real s | KB a frame | MB a game s | MB a real s | Collections in 600 s | GC pause | Other load |
|---|---|---|---|---|---|---|---|---|
| Original | 10.2 | 2.03 | 61 | 0.30 | 0.62 | 1 | not measured (incremental) | 5.2 |
| Original | 11.4 | 2.27 | 64 | 0.32 | 0.73 | 1 | not measured (incremental) | 3.6 |
| Moono | 29.9 | 2.95 | 111 | 1.13 | 3.32 | 3 (1 gen1) | 222 ms in all | 4.4 |
| Moono | 35.4 | 2.99 | 106 | 1.25 | 3.73 | 4 (2 gen1) | 247 ms in all | 2.8 |

**U59, late-game colony**

| Run | fps | Game s a real s | KB a frame | MB a game s | MB a real s | Collections in 600 s | GC pause | Other load |
|---|---|---|---|---|---|---|---|---|
| Original | 6.7 | 1.29 | 452 | 2.34 | 3.02 | 3 full | 11.9 s in all, about 4.0 s each | 4.3 |
| Original | 4.9 | 0.93 | 437 | 2.30 | 2.13 | 2 full | 7.4 s in all, about 3.7 s each | 10.5 |
| Moono | 28.2 | 3.00 | 301 | 2.83 | 8.47 | 5 (1 gen1) | 410 ms in all | 3.5 |
| Moono | 15.8 | 2.70 | 425 | 2.49 | 6.73 | 5 (1 gen1) | 644 ms in all | 8.3 |
| Moono capped | 7.0 | 1.40 | 495 | 2.48 | 3.47 | 3 (1 gen1) | 302 ms in all | 3.0 |
| Moono capped | 7.0 | 1.39 | 707 | 3.55 | 4.94 | 4 (1 gen1) | 291 ms in all | 3.4 |

**U59, mid-game colony**

| Run | fps | Game s a real s | KB a frame | MB a game s | MB a real s | Collections in 600 s | GC pause | Other load |
|---|---|---|---|---|---|---|---|---|
| Original | 11.4 | 2.28 | 179 | 0.90 | 2.04 | 5 full | 4.8 s in all, about 1.0 s each | 3.8 |
| Original | 8.4 | 1.68 | 178 | 0.89 | 1.49 | 5 full | 6.1 s in all, about 1.2 s each | 8.6 |
| Moono | 25.1 | 2.91 | 182 | 1.57 | 4.58 | 4 (2 gen1) | 893 ms in all | 7.3 |
| Moono | 21.6 | 2.89 | 199 | 1.49 | 4.30 | 5 (2 gen1) | 545 ms in all | 7.2 |

Moono against the original, a game second:

| | Uncapped | At the original's frame rate |
|---|---|---|
| U60, late-game | 1.6–1.9× | 1.3× |
| U59, late-game | 1.1–1.2× | 1.1× (one run 1.5×, below) |
| U60, mid-game | 3.5–4.2× | not run (about 2× estimated, below) |
| U59, mid-game | 1.7–1.8× | not run |

A real second, Moono hands out 2–5× more on the late-game colony and 2–6× more on the mid-game
one, as it runs the game 1.3–3× faster and draws 2–6× more frames.

## The capped runs

The original can't keep speed 3 on either colony: it gets 0.9–2.3 game seconds a real second,
about 5 frames to a game second. Uncapped, Moono gets about 3 game seconds a real second at 6–12
frames to a game second. Whatever runs once a frame runs up to 2.4× as often a game second in
Moono, so its uncapped figures a game second overstate the gap.

Held at 7 fps (5.0 frames to a game second, as in the original), Moono on U60's late-game colony
hands out 1.82–1.85 MB a game second against the original's 1.40–1.43: 1.3×, about 85 KB more a
frame. Its three U60 late-game runs (5.0, 9.5 and 12.1 frames to a game second) fit a line: about
95–120 KB a frame repeats with every frame, and about 1.2–1.4 MB a game second follows game time.
The sections that run once a frame (below: `(other)`, the canvas, `World.LateUpdate`,
`Game.Update` and `LateUpdate`, `Global.Update`, the animation updaters and `PropertyTextures`)
add up to about 114 KB a frame, which agrees.

On U59's late-game colony the first capped run hands out 2.48 MB a game second against the
original's 2.30–2.34 (1.06×), as does the uncapped run on a busy machine that drew only 5.9
frames to a game second (2.49). The second capped run is 3.55, but its rate rose for about half of its
eight windows (to 3.4–4.9 MB a game second, from 2.2–2.9) and fell back: the colony was doing
something then that the other runs' windows didn't catch, so it is left out of the ratio.

No mid-game run was capped. If Moono's once-a-frame sections on U60's mid-game colony (about 85 KB
a frame, below) all scale with frames, capping it at 5 frames to a game second would give about
0.7 MB a game second, about 2× the original's 0.3. That is an estimate.

## Where it goes (u60 cf2d0849 and U59)

The U60 tables here are superseded by [Where it goes after batch 4](#where-it-goes-after-batch-4);
the U59 tables stand.

Sections are the game's main update methods and every state-machine bucket, each counted without
the sections nested in it. "(other)" is what ran on the main thread outside them, in Moono mostly
the engine's own frame (rendering, canvas batching, the UI walk). State-machine buckets are grouped
by type ("SapTree states" is every `SapTree.root.*` bucket).

The two counters differ here. The original's is process-wide, so a section also counts what other
threads allocated while it ran: `BrainScheduler.RenderEveryTick` includes the fetch jobs it waits
on, and `(other)` part of the path prober thread's work. Moono's is the main thread's own, and the
rest is the "(other threads)" row. `BrainScheduler`, `(other)` and "(other threads)" are therefore
not comparable one to one; the other rows are.

**U60, late-game colony** (the original at 5.2 frames to a game second, Moono at 12.1)

| Section | Original KB a game s | Moono KB a game s | Original KB a frame | Moono KB a frame |
|---|---|---|---|---|
| (other threads) | (in the rows) | 680 | | 56.1 |
| (other) | 185 | 576 | 35.7 | 47.6 |
| CanvasUpdateRegistry.PerformUpdate | 142 | 535 | 27.3 | 44.2 |
| BrainScheduler.RenderEveryTick | 365 | 43 | 70.4 | 3.5 |
| Game.UnsafeSim200ms | 291 | 357 | 56.1 | 29.5 |
| Game.Update | 87 | 95 | 16.7 | 7.9 |
| Work(Pickup) | 64 | 59 | 12.4 | 4.9 |
| World.LateUpdate | 4 | 52 | 0.7 | 4.3 |
| SolidTransferArm SIM_1000ms | 41 | 26 | 7.8 | 2.2 |
| KBatchedAnimUpdater.LateUpdate | 38 | 27 | 7.3 | 2.3 |
| SpriteSheetAnimManager RENDER_EVERY_TICK | 0 | 35 | 0 | 2.9 |
| HighEnergyParticle states | 28 | 28 | 5.3 | 2.3 |
| Global.Update | 27 | 20 | 5.1 | 1.7 |
| Game.LateUpdate | 8 | 23 | 1.6 | 1.9 |
| SapTree states | 23 | 16 | 4.5 | 1.3 |
| Total | 1,426 | 2,694 | 275 | 222 |

**U60, mid-game colony** (5.0 and 11.8 frames to a game second)

| Section | Original KB a game s | Moono KB a game s | Original KB a frame | Moono KB a frame |
|---|---|---|---|---|
| (other) | 64 | 562 | 12.9 | 47.5 |
| CanvasUpdateRegistry.PerformUpdate | 18 | 196 | 3.7 | 16.6 |
| World.LateUpdate | 3 | 94 | 0.6 | 8.0 |
| (other threads) | (in the rows) | 76 | | 6.4 |
| SpriteSheetAnimManager RENDER_EVERY_TICK | 0 | 34 | 0 | 2.9 |
| Global.Update | 31 | 34 | 6.2 | 2.8 |
| Game.Update | 25 | 32 | 4.9 | 2.7 |
| BrainScheduler.RenderEveryTick | 24 | 27 | 4.8 | 2.3 |
| Work(Pickup) | 23 | 25 | 4.7 | 2.1 |
| Game.LateUpdate | 3 | 24 | 0.6 | 2.0 |
| KBatchedAnimUpdater.LateUpdate | 24 | 13 | 4.8 | 1.1 |
| Game.UnsafeSim200ms | 18 | 22 | 3.7 | 1.8 |
| PropertyTextures.LateUpdate | 0 | 12 | 0 | 1.0 |
| Total | 321 | 1,250 | 64 | 106 |

**U59, late-game colony** (5.3 and 5.9 frames to a game second: Moono's run was on a busy machine)

| Section | Original KB a game s | Moono KB a game s | Original KB a frame | Moono KB a frame |
|---|---|---|---|---|
| (other) | 370 | 504 | 70.3 | 86.2 |
| Game.UnsafeSim200ms | 448 | 445 | 85.2 | 76.0 |
| CanvasUpdateRegistry.PerformUpdate | 159 | 301 | 30.2 | 51.4 |
| BrainScheduler.RenderEveryTick | 233 | 58 | 44.2 | 9.8 |
| (other threads) | (in the rows) | 204 | | 34.8 |
| Work(Pickup) | 138 | 133 | 26.2 | 22.7 |
| Game.Update | 137 | 137 | 26.0 | 23.4 |
| Global.Update | 86 | 53 | 16.4 | 9.1 |
| KBatchedAnimUpdater.LateUpdate | 81 | 55 | 15.4 | 9.4 |
| LaunchableRocketCluster states | 55 | 78 | 10.5 | 13.3 |
| HighEnergyParticle states | 50 | 39 | 9.5 | 6.6 |
| GlobalChoreProvider RENDER_200ms | 49 | 16 | 9.2 | 2.8 |
| SolidTransferArm SIM_1000ms | 45 | 27 | 8.5 | 4.7 |
| Work(WorkerOilRefiller) | 38 | 42 | 7.2 | 7.1 |
| Total | 2,297 | 2,488 | 437 | 425 |

**U59, mid-game colony** (5.0 and 7.5 frames to a game second)

| Section | Original KB a game s | Moono KB a game s | Original KB a frame | Moono KB a frame |
|---|---|---|---|---|
| (other) | 126 | 466 | 25.2 | 62.4 |
| ChargeSuit | 163 | 165 | 32.7 | 22.1 |
| CanvasUpdateRegistry.PerformUpdate | 22 | 163 | 4.4 | 21.8 |
| Global.Update | 119 | 87 | 23.9 | 11.6 |
| World.LateUpdate | 1 | 82 | 0.2 | 11.0 |
| Game.Update | 52 | 65 | 10.5 | 8.7 |
| (other threads) | (in the rows) | 56 | | 7.5 |
| Work(Pickup) | 52 | 55 | 10.5 | 7.4 |
| BrainScheduler.RenderEveryTick | 52 | 32 | 10.4 | 4.3 |
| KBatchedAnimUpdater.LateUpdate | 49 | 43 | 9.8 | 5.8 |
| Game.UnsafeSim200ms | 27 | 29 | 5.4 | 3.9 |
| SpriteSheetAnimManager RENDER_EVERY_TICK | 0 | 22 | 0 | 2.9 |
| Total | 890 | 1,488 | 178 | 199 |

Two patterns hold on all four:
- **Game logic matches.** The sim (`Game.UnsafeSim200ms`), work (`Work(Pickup)` and the other
  workables), `Game.Update` and the state machines hand out about the same a game second in both,
  often a little less in Moono. These run the same game code; the tweaks and .NET's stack
  allocation may account for the small savings.
- **Engine-facing sections cost more.** `CanvasUpdateRegistry.PerformUpdate` (UGUI's rebuilds
  into our canvas renderer) takes 1.6–5× the original's a frame, `World.LateUpdate` (the world's
  draws) 6–55×, `SpriteSheetAnimManager` and `PropertyTextures.LateUpdate` go from nothing to 1–3
  KB a frame, and `(other)`, the engine's own frame, is 1.2–4× (the original's counts other
  threads too, so Moono's real share is larger). In the original these are native Unity, which
  hands out no managed memory.

An earlier allocation trace of the late-game colony (EventPipe sampling, 180 s) gives the same
split by owner: about 58% of Moono's bytes were first allocated by game code and about 37% by
the engine. The engine's largest were `Instantiate`'s structure cloning (11%), canvas geometry
(5%), mesh channel arrays (4%) and `Canvas.willRenderCanvases` removals (2%).

## Method

- Players: the original (U59 744825 release, U60 762230 preview, a development build) and Moono (master
  8392ed3b for U59, u60 cf2d0849 for U60, and u60 4ff1e540 for the runs after batch 4), both with PerfProbe's `alloc.txt`, on Linux under
  WSL2 at 1280×720 on Xvfb. Moono ran a warm-up launch first, then default tweaks.
- Each run loaded the colony at speed 3 with no autosaves, waited 90 s after the first frame-rate
  window, then measured about 600 s between two `PERF alloc` lines. The capped runs set
  `fps.txt` to 7; the runs after batch 4 set it to 20 (late-game) and 34 (mid-game) for every
  player.
- The original's counter is Boehm's own total, `GC_get_total_bytes`, read from libmonobdwgc every
  frame, with `GC_get_gc_no` and `GC_get_full_gc_total_time`. Moono's is .NET's
  `GC.GetTotalAllocatedBytes(true)`, with the runtime's pause total and collection counts.
- The site breakdown is PerfProbe's `sites.txt` (AllocSites; committed 2026-11-11, rebuilt from
  this description after the first copy was lost): Harmony prefixes and finalizers
  on the main update methods and a replacement of the state-machine bucket loop that brackets
  each bucket, crediting the counter's rise to the innermost open section. It used Boehm's total
  in the original and `GC.GetAllocatedBytesForCurrentThread` (main thread) in Moono. Usable runs:
  one per player, colony and version (the first site runs of the original's U60 failed to reach
  the colony, and one Moono U60 late-game run's game clock didn't advance).
- Fixtures: the late-game colony is U57 (Spaced Out, every DLC, cycle 17,077, 24 Duplicants,
  1,186 critters); the mid-game colony is 2minnow (cycle 1,990, 50 Duplicants).

**Validation**

- Calibration at start-up, with known allocations kept in a static field: the original measures a
  plain object at 32 B, a `byte[100]` at 144 B and a `byte[100000]` at 100,048 B; Moono 24 B,
  128 B and 100,024 B. The counters are right, and Boehm's sizes run larger.
- On U60's development build, Unity's "GC Allocated In Frame" counter (object sizes, main thread
  only) came to 0.857–0.868 of Boehm's total in every run, with objects of 84–91 B on average and
  600–2,660 allocations a frame.
- Heap growth (`Profiler.GetMonoUsedSizeLong` over frames without a collection) agrees with .NET's
  counter within 0.3% in every Moono run. In the original it catches only 0.31–0.70 of Boehm's
  total, since Boehm's lazy sweep reuses freed slots without "used" rising, so it isn't used there.
- The site script was checked by hand against the raw `PERF sites` lines of the original's U60
  mid-game run: the total (321 KB a game second), `(other)` (64.4), `Global.Update` (31.1) and
  `Work(Pickup)`'s 13.5 calls a frame all match.

## Caveats

- **Frame rate.** The original ran at 5–11 fps, Moono at 16–36. Per-frame work is a larger
  share of Moono's game second; only the late-game colony has capped runs to remove that.
- **Machine load.** Other work used 3–10.5 cores during the runs (table column). The busiest
  runs drew fewer frames, which shifts their rates a game second but not their rates a frame much.
- **Sizes.** Boehm rounds sizes up to its classes and adds a byte: a plain object is 32 B against
  .NET's 24 B, and Unity's own counter (main thread, object sizes) comes to 15–17% less than Boehm's total.
  The original's figures are therefore a little high, and the ratios above a little low for
  Moono. .NET 10 also stack-allocates objects that never escape, which Mono doesn't.
- **Variance.** Colony events move the rate: one capped U59 run rose by up to 70% for a few windows.
  Two runs per cell give ranges, not confidence intervals.
- **Pauses.** U59's original uses a stop-the-world collector; Boehm's full-collection timer gives
  its pauses. U60's original uses incremental collection, which that timer doesn't count, so its
  pauses weren't measured. Moono's figure is its total pause time; the longest pause wasn't logged.

## What this means

- **After allocation batch 4, Moono allocates less than the original a frame on U60** and the
  same a game second on the late-game colony; the mid-game colony is still 2.2× a game second at
  Moono's higher frame rate ([above](#u60-after-allocation-batch-4-2026-11-11)). The rest of this
  section is about the earlier builds.
- **Moono allocates more than the original, almost all of it in the engine.** At the original's
  frame rate the gap on the late-game colony is 1.1× (U59) to 1.3× (U60) a game second. Uncapped,
  it is up to 1.9× on the late-game colony and 4× on U60's mid-game one, where the game itself
  allocates little and the engine's per-frame cost dominates. The game's own code allocates about
  the same in both.
- **Next candidates**, by size:
  1. The engine's own frame (`(other)`, 48–86 KB a frame) and the canvas rebuilds
     (`CanvasUpdateRegistry`, 17–51 KB a frame): canvas geometry, mesh channels, the
     `willRenderCanvases` removals, and `Instantiate`'s cloning.
  2. Other threads in Moono (56 KB a frame on U60's late-game colony, 6–35 elsewhere): not yet
     split by thread; a trace with thread IDs would say whether it is the path prober, the job
     threads or the engine's upload workers.
  3. `World.LateUpdate` (4–11 KB a frame against under 1): the world's draws through the engine.
  4. `SpriteSheetAnimManager` (3 KB a frame), `PropertyTextures.LateUpdate` (1 KB a frame) and
     `Game.LateUpdate` (2 KB against under 1 on the mid-game colony): engine API calls that the
     original serves without managed allocations.
- **GC pauses favour Moono despite the volume.** U59's original stops for about 4 s on the
  late-game colony and about 1 s on the mid-game one, every 2–5 minutes. Moono runs 2–6
  collections in 10 minutes for 0.2–0.9 s in all, 60–220 ms a collection on average, and it does
  so while handing out 2–5× more a real second.

## The engine's frame and the canvas rebuilds, by site (allocation batch 4)

An allocation trace with thread IDs (EventPipe sampling, 180 s at speed 3, Linux, U59, master
dce09838, the section probe on) split the first two candidates by call site. KB a frame on the
main thread; "the engine's own" is what an engine frame allocated itself, the rest is game or uGUI
code (the late-game colony at 41 fps, the mid-game one at 39).

| `(other)` | Late-game | Mid-game |
|---|---|---|
| In all | 54.5 | 55.1 |
| Game and uGUI code (scripts' `Update`, `LateUpdate` and `Start`, run by the engine's loop) | 26.2 | 14.8 |
| The engine's own | 28.3 | 40.3 |
| of which: per-frame list and array copies (cameras, canvases, particle systems, pending Starts, phase invocation lists, the update phases' sorted lists, coroutine waiters, image-effect and batcher lists) | 6.8 | 8.7 |
| render targets (`RenderTargetBinding.Of`, the GL device's target records) | 6.1 | 6.4 |
| `Canvas.willRenderCanvases` removals (TextMeshPro's texts leaving it when disabled) | 3.8 | 5.5 |
| canvas element copies set outside the rebuilds, rebakes, nested canvas merges | 5.3 | 3.5 |
| `GetTemporary`'s lookup description, immediate draws' closures | 2.2 | 2.3 |
| texel copies' tasks, particle geometry, other | 4.1 | 14.0 |

| `CanvasUpdateRegistry.PerformUpdate` | Late-game | Mid-game |
|---|---|---|
| In all | 41.1 | 16.5 |
| Element copies (`CanvasRenderer.SetMesh`: positions, colours, four UV sets, normals, tangents, indices) | 18.6 | 9.8 |
| uGUI's worker mesh channels (`VertexHelper.FillMesh`) | 5.9 | 0.9 |
| `Mesh.Clear`'s new submesh list | 2.3 | 2.6 |
| A chart's `UILineRenderer` (game code, as in the original) | 13.8 | 2.2 |
| TextMeshPro, layout | under 0.5 | under 0.5 |

The copies missed their arrays because each renderer kept its own spares for 4 frames at one
length: most elements rebuild less often, or at another length. Other threads (late-game 24.6 KB
a frame, mid-game 9.5) were mostly game code: the chore collection's contexts on the game's job
threads (`RemoteChore.CollectChores`, 20.7 on the late-game colony), then the GL thread's
texture uploads and screenshot read-backs (1.5 and 5.5) and `Parallel.For`'s own state on the
thread pool (about 2).

After batch 4 (same method and base), the engine's own sites above come to 0.9 KB a frame
in `(other)` on both colonies (from 18.8 and 22.8) and 5.6 and 3.1 in the canvas sites (from 32.6
and 17.8). 600 s meter runs on the mid-game colony, at 34 fps both: 159 → 109 KB a frame, 1.81 →
1.26 MB a game second, the canvas rebuilds 17.7 → 3.5 KB a frame and `(other)` 53.9 → 26.4; 5 → 4
collections in 600 s. On the late-game colony held at 20 fps (uncapped runs drew 28–40 fps on a
busy machine, too far apart to compare): 381 → 309 KB a frame, 2.46 → 2.06 MB a game second, the
canvas rebuilds 48.8 → 18.2 KB a frame (most of the rest is a chart's `UILineRenderer`, game code)
and `(other)` 78 → 50; 5 → 4 collections.
