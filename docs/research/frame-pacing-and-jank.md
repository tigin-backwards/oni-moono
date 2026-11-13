# Frame pacing and jank on big colonies (2026-11-12)

The question: what makes interaction (UI scrolling above all) uneven on a big colony while the
simulation runs, and what could even it out without changing the game's results? UI logic runs on
the main thread with the simulation, so a scroll is as smooth as the main thread's frames.

**Verdict.**
- **Ours against the original.** On U60 at speed 3, Moono's frames are 10 times shorter than the original's on a late-game
  colony (Windows p50 7.5 ms against 80 ms, p99 26 against 133). The original runs a long, uniform frame
  that cannot keep speed 3 (2.3× game speed reached on Windows). Moono keeps speed 3 with a
  two-level frame: most frames 6–8 ms, and every frame that carries one of the game's 200 ms
  sim steps 13–16 ms.
- **Where our jank comes from.** That sim-step frame is 84–85% of Moono's spike time on the late-game colony
  (Windows and Linux). Inside it the main thread runs the game's own 200 ms batch, not the
  native simulation: the gas and liquid conduits, the conveyor rails, the plant absorbers, the
  power grid and the nav grid update. SimDLL's own call is under 1% of it.
- **Scrolling itself.** Scrolling a table screen adds about 1.5 ms a frame and no spikes of its own on
  Windows. While paused, a scroll is even: p99 7.3 ms, max 8.5 ms. The scroll's spikes at speed 3 are the
  sim-step frames.
- **The rest.**
  - On Linux (Mesa's Direct3D 12 layer under WSL), the main thread also waits on the GL thread
    while new rows are drawn.
  - GC pauses come once every one to three minutes: 35–92 ms on Windows, 100–135 ms on Linux.
  - The autosave freezes for 2.4–3.0 s, then three slow frames follow.
- **Recommended plan.**
  - Make the sim-step frame's batch cheaper with exact tweaks of the usual kind: conduits,
    conveyor rails, plant absorbers, power.
  - Leave sim pacing alone. Smoothing the game's delta was modelled at no gain: the
    sub-ticks are not the spike.
  - Don't build UI-only frames. They cannot be made invisible to game code, and they
    would not shorten the heavy frame.
  - Leave the vsync wait as it is: with vsync at 60 Hz about 1–2% of refreshes repeat a
    picture, and queue depths 1, 2 and 3 measure alike; see [present pacing](#e-present-pacing).
- **Nothing landed.**

## Method

- **Probe:** `tools/PerfProbe/Jank.cs` (local branch `jank-u60`, not committed: test tooling like
  the other PerfProbe scenarios). A `jank.txt` scenario drives speed, screens and scrolling, the
  same in both players. A recorder that runs first in each Update pass logs every frame's real
  time, GC collections by generation (and .NET's pause time in ours), and FrameTimingManager's
  main-thread time and wait after present. With `sections`, Harmony brackets (a prefix and a
  finalizer) time the main update entry points per frame: Game.Update, UnsafeSim200ms (and
  the steps with time), StepTheSim, UpdateCellInfo, UpdateNavGrids, the state machines' sim
  sub-ticks (and their count), render 200 ms and 1000 ms bucket groups (only when due),
  RenderEveryTick, BrainScheduler, AsyncPathProber, Game.LateUpdate, KBatchedAnimUpdater,
  KAnimBatchManager.Render, CanvasUpdateRegistry.PerformUpdate, KScreenManager, the scroll rects,
  SaveLoader.Save and a few more. The brackets cost Moono 5–8% of its frame rate; the
  distributions below come from launches without them unless marked.
- **Scenario:** load, 1,800 frames unrecorded (the post-load collection runs there), then
  recorded: paused 900 frames, speed 1, 2 (1,800 each), speed 3 (2,400), the vitals table open
  and its list scrolled top to bottom and back every 240 frames through ScrollRect's normalized
  position (1,440 at speed 3, 960 paused), and the game's own autosave with 900 frames after it.
  Autosaves otherwise off; vsync off (PerfProbe's `awake.txt`), so frame times are the frame's
  cost. A speed change is followed by 60 unrecorded frames.
- **Players:** Moono u60 a4f8d9cc (release, default tweaks) and the original U60 762230.
- **Colonies:**
  - a late-game Spaced Out colony with every DLC (cycle 17,077, 24 Duplicants, about 1,200
    critters);
  - a mid-game colony with 50 Duplicants (cycle 1,990).
- **Windows:**
  - the Ryzen 7 9800X3D / RTX 3090 machine, 1280×720 windowed on monitor 2;
  - `--no-focus`, High priority, sound prefs at 0;
  - a private stage folder and run home;
  - the perf queue and the Windows game lock held for each whole run.
- **Linux:**
  - Xvfb through `scripts/contain.sh`, on Mesa's Direct3D 12 layer under WSL2;
  - for CPU-side direction only, as in docs/PERFORMANCE.md. The original's GL there is far slower still.
- **Stacks:** one Moono Linux launch with a 150 s EventPipe trace (CPU samples at 1 kHz, GC
  events) over speed 3 and the scroll. The sampler stops threads at safe points, so leaf
  frames lean towards lock and GC-poll slow paths; the inclusive split under a known entry point
  is what is used here.
- **Spikes:**
  - A spike is a frame over 2× its phase's median.
  - Its excess (time over the median) is put down to the section whose time rose most above its own median in that frame.
  - Other possible causes: the GC pause in ours (a frame with a collection in the original), the wait after
    present, or what no section covers (the engine's render, input and other scripts).
  - The budget view counts frames over a 60 Hz refresh (16.7 ms) on Windows and over 33.3 ms on
    Linux.

## Frame times

### Late-game colony

| Run | Phase | fps | p50 | p95 | p99 | max | >2× p50 | >33 ms |
|---|---|---|---|---|---|---|---|---|
| Windows, ours | paused | 267 | 3.7 | 5.2 | 6.7 | 8 | 0.4% | 0.0% |
| | speed 1 | 162 | 5.8 | 9.6 | 13.1 | 24 | 1.8% | 0.0% |
| | speed 2 | 139 | 6.5 | 11.4 | 22.4 | 29 | 1.9% | 0.0% |
| | speed 3 | 116 | 7.5 | 14.9 | 25.5 | 45 | 5.0% | 0.0% |
| | speed 3, scrolling | 96 | 9.1 | 16.9 | 27.9 | 32 | 4.2% | 0.0% |
| | paused, scrolling | 221 | 4.5 | 6.0 | 7.3 | 8 | 0.0% | 0.0% |
| | autosave | 83 | 7.1 | 16.7 | 30.4 | 2,449 | 8.1% | 1.0% |
| Windows, original | paused | 58 | 16.9 | 22.4 | 24.2 | 80 | 0.1% | 0.1% |
| | speed 1 | 24 | 37.8 | 62.8 | 92.3 | 758 | 4.3% | 78% |
| | speed 2 | 14 | 65.7 | 102.2 | 112.0 | 131 | 0.0% | 100% |
| | speed 3 | 11 | 80.0 | 126.0 | 132.9 | 395 | 0.0% | 100% |
| | speed 3, scrolling | 11 | 82.2 | 128.3 | 133.4 | 151 | 0.0% | 100% |
| | paused, scrolling | 52 | 19.1 | 23.6 | 27.9 | 33 | 0.0% | 0.0% |
| | autosave | 7 | 78.0 | 122.7 | 133.3 | 25,116 | 0.7% | 100% |
| Linux, ours | paused | 110 | 8.9 | 10.6 | 12.6 | 18 | 0.0% | 0.0% |
| | speed 1 | 78 | 11.8 | 18.3 | 32.0 | 41 | 1.6% | 0.7% |
| | speed 2 | 64 | 13.9 | 25.4 | 37.5 | 72 | 3.9% | 3.2% |
| | speed 3 | 49 | 18.7 | 35.3 | 41.4 | 125 | 2.7% | 6.7% |
| | speed 3, scrolling | 42 | 22.2 | 38.7 | 46.5 | 59 | 1.2% | 10.3% |
| | paused, scrolling | 97 | 10.0 | 11.9 | 16.5 | 24 | 0.6% | 0.0% |
| | autosave | 40 | 17.9 | 35.7 | 45.0 | 3,329 | 4.9% | 7.4% |
| Linux, original (with the brackets) | paused | 30 | 32.6 | 40.2 | 44.9 | 321 | 0.1% | 44% |
| | speed 3 | 8 | 116.2 | 155.9 | 165.0 | 553 | 0.0% | 100% |
| | speed 3, scrolling | 8 | 121.8 | 162.1 | 171.7 | 207 | 0.0% | 100% |
| | autosave | 6 | 114.7 | 154.1 | 162.5 | 46,538 | 0.3% | 100% |

Game speed reached at speed 3 (game seconds a real second, from the frames' times and the game's
0.2 s cap a frame): ours 3.00 on both systems; the original 2.28 on Windows, 1.66 on Linux.

### Mid-game colony, 50 Duplicants

| Run | Phase | fps | p50 | p95 | p99 | max | >2× p50 | >33 ms |
|---|---|---|---|---|---|---|---|---|
| Linux, ours | paused | 79 | 12.4 | 14.3 | 18.6 | 20 | 0.0% | 0.0% |
| | speed 3 | 49 | 20.0 | 23.1 | 27.8 | 52 | 0.1% | 0.4% |
| | speed 3, scrolling | 42 | 22.0 | 40.0 | 43.2 | 66 | 0.6% | 9.2% |
| | paused, scrolling | 70 | 14.0 | 16.3 | 21.1 | 68 | 0.4% | 0.3% |
| | autosave | 45 | 20.2 | 25.3 | 36.1 | 1,053 | 0.3% | 1.8% |
| Linux, original (with the brackets) | paused | 42 | 23.7 | 27.2 | 28.6 | 30 | 0.0% | 0.0% |
| | speed 3 | 14 | 71.3 | 86.5 | 93.8 | 117 | 0.0% | 100% |
| | speed 3, scrolling | 12 | 80.2 | 95.3 | 103.2 | 206 | 0.1% | 100% |
| | autosave | 12 | 70.9 | 85.4 | 97.6 | 8,337 | 0.2% | 100% |
| Windows, ours | paused | 198 | 4.8 | 7.2 | 8.8 | 12 | 0.4% | 0.0% |
| | speed 1 | 137 | 6.9 | 10.5 | 13.7 | 18 | 0.9% | 0.0% |
| | speed 2 | 116 | 8.1 | 12.9 | 16.2 | 22 | 1.1% | 0.0% |
| | speed 3 | 86 | 10.6 | 19.7 | 26.0 | 105 | 3.9% | 0.2% |
| | speed 3, scrolling | 71 | 13.3 | 21.0 | 24.5 | 31 | 0.3% | 0.0% |
| | paused, scrolling | 192 | 5.1 | 6.1 | 7.0 | 9 | 0.0% | 0.0% |
| | autosave | 118 | 6.5 | 10.7 | 13.5 | 944 | 2.0% | 0.3% |
| Windows, original | paused | 105 | 9.3 | 12.0 | 13.0 | 15 | 0.0% | 0.0% |
| | speed 1 | 55 | 17.3 | 25.7 | 32.5 | 36 | 0.2% | 0.8% |
| | speed 2 | 36 | 26.4 | 37.7 | 43.4 | 54 | 0.1% | 15% |
| | speed 3 | 29 | 33.7 | 46.2 | 53.7 | 217 | 0.2% | 53% |
| | speed 3, scrolling | 22 | 44.8 | 60.8 | 67.5 | 86 | 0.0% | 99.5% |
| | paused, scrolling | 68 | 14.5 | 17.6 | 19.2 | 22 | 0.0% | 0.0% |
| | autosave | 24 | 34.2 | 47.5 | 54.6 | 4,880 | 0.3% | 57% |

The 105 ms frame in ours at speed 3 is a 92 ms gen0 collection. On Windows the original keeps
speed 3 on this colony (2.99×), at 29 fps; scrolling the table costs it 11 ms a frame against
Moono's 2.8 ms. A second Moono launch with the brackets ran at speed 3 with p50 8.5, p99 15.4 and max
44 ms (the runs differ by about 20% in their medians, as Windows runs on this machine do): its
frames over 2× the median add up to 255 ms in all phases together, a sim-step frame there costing 3.3 ms
more than another (2.1 ms in StepTheSim). Spikes on this colony are small on Windows: sim-step frames 35% of
that, canvas rebuilds 22%, a GC 14%.

### What the distributions say

- **The original's frame is long and flat.** Every frame at speed 2–3 carries a sim step and
  the full 12 sub-ticks the game allows a frame (0.2 game seconds), so frames hardly vary
  around their median (no frame over 2× it at speed 3). There is no jank in the
  relative sense, because there is no fast frame: everything is over 33 ms. The game
  falls short of speed 3 instead.
- **Moono's frame is short and two-level.** A sim step falls every 200 ms of game time: every
  67 ms of real time at speed 3, on every fifth to eighth frame at 110 fps. A fit over the
  late-game Windows frames: frame = 5.1 ms + 1.8 ms a sub-tick + 7.5 ms when it holds a sim step
  (scrolling: 5.9 + 2.0 + 7.6). The step frame is the spike, at a steady 15 Hz.
- **Scrolling costs little; the sim makes it uneven.** Paused, scrolling the vitals list adds
  0.8 ms a frame on Windows and stays as even as idle (p99 1.6× the median). At speed 3 it adds
  1.6 ms and inherits the sim's spikes.
- **On a 60 Hz display Moono's late-game Windows frames almost all fit.** 4.0% of frames
  at speed 3 and 8.1% while scrolling are over 16.7 ms, and none is over 33 ms. On a 144 Hz
  display (6.9 ms) every sim-step frame misses a refresh: a stutter at 15 Hz.

## Spike sources

Share of the spike excess (frames over 2× the median), paused to speed 3 and scrolling, autosave
phase left out:

| Source | Windows ours, late-game | Linux ours, late-game | Linux ours, mid-game | Original (Linux, both colonies) |
|---|---|---|---|---|
| Sim-step frame (the game's 200 ms batch, nav grids) | **85%** (195 spikes) | **84%** (141) | 0% | 25% / 0% |
| Sim sub-ticks (state machine buckets; a long frame's catch-up) | 13% | 5% | 0% | 45% / 48%: one frame each, with a full collection in it |
| GC pause | about 1% (one 36 ms gen0) | 3% (one 132 ms gen1) | 7% | 0% / 26% counted alone; Boehm's full collections are in the largest frames |
| Main thread waiting on the device thread | 0% | 2% | **66%** | 0% |
| Canvas rebuild | 0% | 0% | 4% | 30% (one frame) |
| Render-every-tick buckets, other | 1% | 5% | 23% | |

Frames over the budget, by the section that rose most:

| Run | Phase | Over budget | Sim step | Sub-ticks | Device wait | Other |
|---|---|---|---|---|---|---|
| Windows ours, late-game (16.7 ms) | speed 3 | 4.0% | 80 | 14 | 0 | 1 |
| | scrolling | 8.1% | 89 | 28 | 0 | 0 |
| Linux ours, late-game (33.3 ms) | speed 3 | 7.7% | 156 | 16 | 6 | 6 |
| | scrolling | 13.6% | 124 | 58 | 12 | 2 |
| Linux ours, mid-game (33.3 ms) | speed 3 | 2.0% | 2 | 2 | 43 | 0 |
| | scrolling | 11.9% | 4 | 3 | 157 | 7 |

### The sim-step frame

`Game.SimEveryTick` adds the frame's game time (`Time.deltaTime`, at most 0.2 s) to its
counter and runs one sub-tick per 1/60 s of it. Every 12th sub-tick first runs
`UnsafeSim200ms`: `SimMessages.NewGameFrame`, `StepTheSim`, `World.UpdateCellInfo` and
`Pathfinding.UpdateNavGrids`. The state machines' sim buckets (200 ms, 1000 ms, 4000 ms) are
spread over the sub-ticks by Klei. What runs inside `StepTheSim` is not spread: the game's
own 200 ms systems after the sim's data come back.

Moono, late-game, Linux trace, samples under `UnsafeSim200ms` (9.8% of the main thread):

| Part | Share |
|---|---|
| `StepTheSim` | 90% |
| · `ConduitFlow.Sim200ms` (gas and liquid pipes) | 26% of StepTheSim |
| · `SolidConduitFlow.Sim200ms` (conveyor rails; items stored and removed) | 22% |
| · `PlantElementAbsorbers.Sim200ms` | 14% |
| · `EnergySim.EnergySim200ms` | 13% |
| · `CircuitManager.Sim200msLast` | 8% |
| · `ElementConsumer.AddMass`, `Substance.SpawnResource`, `ConduitTemperatureManager.Sim200ms` | 4% each |
| · `Sim.HandleMessage` (SimDLL itself) | **0.6%** |
| `Pathfinding.UpdateNavGrids` | 9.5% |
| `World.UpdateCellInfo`, `NewGameFrame` | under 1% |

By the brackets on Windows, the step frame's extra 7.5 ms is `StepTheSim` 4.7 ms (mean; median
2.8), nav grids 0.6 ms, and a little more in everything else that frame (the frame after a long
one also gets more sub-ticks: 2.75 against 1.65 on average, +2.3 ms).

### The rest

- **Autosave:** 2.4–3.0 s on the main thread inside `SaveLoader.Save` on the late-game colony
  (background-save already moves compression and the write off it); 0.9–1.1 s on the mid-game one.
  The original: 25 s on Windows, 46 s on Linux, including its forced full collection. The
  three frames after it are 375–385 ms each on Windows: the frame after the freeze has the
  game's full 12 sub-ticks and a step, animation catch-up (`KBatchedAnimUpdater.LateUpdate`
  60–80 ms), and the next a 260 ms wait after present while the colony snapshot the autosave
  asks for is rendered and read back.
- **GC (ours):**
  - Late-game colony: one collection in 70–160 s of play; a gen0 of 34–36 ms on Windows, 100–135 ms on Linux.
  - Mid-game colony: a gen0 of 92 ms on Windows; gen1s of 42–56 ms on Linux.
  - The post-load collection (post-load-gc) ran in the unrecorded first 1,800 frames; no
    post-load hitch was seen.
- **GC (original):** Boehm's full collections, one every few minutes, at 190–760 ms frames.
- **Device wait (Linux):** on the mid-game colony while scrolling at speed 3, slow frames waited
  15 ms after present against 2 ms in the others: the GL thread draws the rows that come into
  view slowly under Mesa's Direct3D 12 layer, and the main thread waits for it to take the next frame.
  Windows shows no such wait. Linux only, and WSL's layer at that (docs/PERFORMANCE.md).

## Options

| Option | What | Expected effect, measured or modelled | Exact? | Risk | Verdict |
|---|---|---|---|---|---|
| (a) Spread the game's batches | Move the StepTheSim systems or render 1000 ms updaters onto other frames | The step batch is 85% of spike time | **No**: moves game updates relative to the sub-ticks and per-frame code | High | Not done |
| (a') Cheaper step batch | Exact tweaks of conduits, conveyor rails, plant absorbers, power | Each ms off StepTheSim's 4.7 ms comes off the spike | Yes, by construction and verify modes, as other tweaks | Per tweak | **Recommended** |
| (b) Sim pacing | Smooth the delta the game sees, carrying the rest | Modelled: sd 3.86 → 3.72 ms, p99 20.4 → 19.9 ms, spikes 6.2% → 5.6% (Windows, late-game) | No: a different delta sequence; game time stays exact over time | Low | Rejected: no gain |
| (c) UI-only frames | A frame of input, UI and drawing without game updates | Would not shorten the heavy frame | No: see below | High | Rejected |
| (d) GC tuning | Gen0 budget cap, Satori | 1–7% of spike time | Yes | Medium to high | Leave: both tried, no gain |
| (e) Present pacing | The vsync wait's rule, queue depth | With vsync about 1–2% of refreshes repeat; depth 1, 2 and 3 alike | Yes (no game-visible change) | Low | Measured: queue depth 1–3 within the runs' spread; no change |

### (a) Spreading batches

Klei already spreads the state machine sim buckets over the 12 sub-ticks of a 200 ms step (and
the 1000 ms and 4000 ms ones over 60 and 240). Two kinds of batch remain:

- **The step's systems** (`StepTheSim` and the nav grid update): they read the sim's results of that
  step and must run before the next sub-tick's buckets, which read what they write (pipe contents,
  stored items, power). Running any of them a sub-tick later changes the order of updates;
  running them on other threads at the same time needs each system's callbacks into game code
  (conduit updaters, storage events, status items) proved independent. Neither is exact in
  general. The fork-join route (as fast-anim-batches) is open inside one system where the
  work items touch disjoint state, which is a per-system study.
- **Render 200 ms and 1000 ms groups** (`StateMachineUpdater.Render`; `useSubticks` off, one
  bucket list): every updater of the group runs in one frame each 1000 ms of real time. On these
  colonies they cost 1.0–1.6 ms in that frame, below the spike threshold. On an open table screen,
  `TableColumn` is one of them (the table's once-a-second refresh), which the UI work on table
  screens covers.

### (b) Sim pacing

What the original does is the game's own code: `Time.deltaTime` is the real delta clamped to
`Time.maximumDeltaTime` (1/3 s in both players) times `timeScale` (3 at speed 3); the game clamps
that to 0.2 s and runs one sub-tick per 1/60 s. There is no catch-up: game time beyond 0.2 s a
frame is dropped, and the game slows (the original at 2.3× on Windows). Several sub-ticks run in
one frame whenever a frame is longer than 1/180 s at speed 3. Unity's engine adds nothing to this.

A per-frame sim budget, carrying the rest over, can only be built by handing the game a
smoothed `Time.deltaTime` (and `Time.time`). Every script then sees one consistent delta, and
game time adds up to the same total. But it is a different delta sequence from the one the
frames had, and the step lands on other frames relative to per-frame code. It was modelled
before building it (`sim.py`: the game's sub-tick loop replayed against the fitted frame cost
and the measured residuals, with an EWMA of the delta and a carry spread over the next frames):

| Late-game, Windows, speed 3 | p50 | p95 | p99 | >2× p50 | sd |
|---|---|---|---|---|---|
| Replay as the game paces | 8.1 | 16.7 | 20.4 | 6.2% | 3.86 |
| Smoothed delta (three settings) | 8.2 | 16.6 | 19.8–20.1 | 5.6–5.7% | 3.71–3.76 |

The spike is the step's fixed 7.5 ms; the sub-ticks a smoothed delta evens out are 1.8 ms each
and already vary by one or two. No gain, and not exact: not prototyped further.

### (c) UI-only frames

A frame that runs only input, UI layout and drawing while the game skips its updates:
- **It cannot shorten the heavy frame.** The main thread runs the game's frame to its end. A UI frame can come only between two game frames, so it delays the next game frame rather than splitting the current one. Running UI code inside the game frame, during the step, would have UI code read the game's state mid-step: the Grid arrays are repointed in `StepTheSim`, and conduit and storage state changes in the step's systems.
- **It cannot be hidden from game and mod code.** The scroll is game code: `KScrollRect`, and `TableScreen`'s scrollers and rows, which read Duplicant data. Its screens' `Update`s are game Updates. A UI frame has to choose:
  - Skip them, and the scroll does not move.
  - Run them, and the game's other Updates do not run in a frame that `Time.frameCount`, `Time.unscaledDeltaTime`, input edges (`GetKeyDown`, `GetMouseButtonDown`) and coroutines (`yield return null`) all count as a frame. Game code assumes every frame runs every Update: KScreenManager's input dispatch, the camera, the tools, the hover card.
- **A compositor scroll does not work either.** A browser-style scroll, moving the already drawn layer on the device thread, does not apply: RectMask2D culls rows outside the viewport out of the canvas geometry, so rows coming into view do not exist yet.
- **Conclusion.** Not feasible without changing what game code observes; not prototyped.

### (d) GC

Ours steady-state: one young collection in one to three minutes of play, 35–92 ms on Windows (two to
six missed refreshes at 60 Hz), 100–135 ms on Linux. gc-pauses.md measured the knobs:
- **Gen0 budget cap: rejected there.** A 64 MB a heap cap made steady play worse: a 105–118 ms pause, and more GC time a second.
- **Post-load collection: already shipped** (post-load-gc).
- **What is left:**
  - Satori, a low-pause collector: already tried, and it gave no gain here.
  - Fewer allocations: the allocation batches, which make collections rarer, not shorter.

GC is 1–7% of Moono's spike time here, so it comes after the step batch.

### (e) Present pacing

With vsync, what the player sees is the presents' cadence, not the frames' cost. Since
docs/research/input-latency.md the main thread waits before the next frame until frame n + 1 −
`QualitySettings.maxQueuedFrames` (2) has been presented, while Swappy's rule (`QueuedFrames`)
says the frame's stages fit a refresh; it turns the wait off only when more than 10% of the frames
over 2 s miss their refresh. On the late-game colony 4–8% of frames are over 16.7 ms with vsync
off, all sim-step frames, so the question was whether a deeper queue (or the wait off) would
absorb them.

Measured with vsync on (`vsync.txt`: the PerfProbe `queued N` command sets
`QualitySettings.maxQueuedFrames`), late-game colony, Windows, windowed on the 59.95 Hz monitor
(so composed by DWM), the main thread's frame intervals:

| Player, queue depth | Phase | fps | p50 | p95 | p99 | max | 2 refreshes | 3+ refreshes | interval changes |
|---|---|---|---|---|---|---|---|---|---|
| Ours, 2 (default) | speed 3 | 59.0 | 16.6 | 26.0 | 32.8 | 47 | 5.3% | 1.8% | 7.0% |
| | scrolling | 59.2 | 16.6 | 26.6 | 30.4 | 58 | 5.5% | 1.9% | 7.4% |
| Ours, 3 | speed 3 | 59.6 | 16.6 | 24.4 | 28.3 | 39 | 5.2% | 0.8% | 6.2% |
| | scrolling | 59.6 | 16.6 | 26.4 | 31.4 | 37 | 4.4% | 2.5% | 7.7% |
| Ours, 1 | speed 3 | 59.6 | 16.6 | 24.9 | 29.5 | 58 | 4.8% | 1.3% | 6.7% |
| Original, 2 | speed 3 | 9.7 | 94.8 | 146.8 | 236.6 | 456 | 0% | 100% | 55% |
| | scrolling | 10.0 | 93.8 | 135.8 | 176.7 | 214 | 0% | 100% | 53% |
| Original, 3 | speed 3 | 10.2 | 93.2 | 134.5 | 150.4 | 651 | 0% | 100% | 52% |
| Original, 1 | speed 3 | 7.9 | 117.0 | 166.9 | 183.7 | 667 | 0% | 100% | 70% |

("Interval changes": frames whose interval differs from the one before by more than 12 ms.)

- **Ours with vsync:** about 7% of frame intervals are two or more refreshes, and the depth makes
  no difference beyond the runs' spread (6.2–7.7% interval changes at every depth). The frame rate
  stays at 59.0–59.6 of 59.95, so about 1–2% of refreshes repeat a picture. The main thread then
  catches up on the next frame, which is why intervals of two refreshes show twice as often as the
  dropped refreshes.
- **A deeper queue does not absorb the sim-step frames**, because each one is followed by
  another full frame of work; there is no slack to fill the queue with at 60 Hz. A
  periodic-miss term for `QueuedFrames` would change latency, not cadence: not worth building.
- **The original with vsync** runs at 8–10 fps, every frame three or more refreshes, half of
  its intervals changing by more than 12 ms from one frame to the next; its depth 1 costs it 2 fps.
- Not measured: borderless fullscreen (where DXGI's frame statistics name each picture's
  vblank, as in frame-timing.md), 144 Hz, and present-side timing (a DXGI hook). The intervals
  above are the main thread's, which the wait ties to the presents.

## Recommended plan

1. **Cheaper sim-step frame (exact tweaks).** Profile `StepTheSim`'s systems on the late-game
   colony with the jank probe's brackets, from the top:
   - `ConduitFlow.Sim200ms`;
   - `SolidConduitFlow.Sim200ms`, where items stored and removed drive `Pickupable` tag refreshes;
   - `PlantElementAbsorbers.Sim200ms`, which builds a planner every step;
   - `EnergySim`, `CircuitManager.Sim200msLast`;
   - `UpdateNavGrids`.

   Each change is verified like the other tweaks (verify mode against the game's own path). The
   target is the step frame's 7.5 ms extra on Windows. By the fit above, halving
   it brings the step frame from about 13.7 ms to 10 ms (Windows, speed 3), under a 60 Hz
   refresh even while scrolling, though still over a 144 Hz one.
2. **Present pacing:** no change. With vsync at 60 Hz the queue depth makes no measurable
   difference; the sim-step frames have to get cheaper (1). A 144 Hz borderless run with a
   present-side hook is the check to make once (1) has landed.
3. **Autosave:** the 2.4–3.0 s serialization stays on the main thread (a save is one instant of
   the game's state). The 750 ms after it (the snapshot and its read-back, animation catch-up)
   is worth a look by the capture work.
4. **No sim-pacing or UI-only-frame changes**, for the reasons above.
5. **GC:** nothing; the gen0 cap and Satori were both tried without a gain.

## Runs

Local, not in the repository: the probe and scenarios on branch `jank-u60` (out/jank: `full.txt`,
`clean.txt`, `trace.txt`, `vsync.txt`; `linux-run.py`, `win-run.sh`, `win-job.sh` (a runq job); `analyze.py`, `sim.py`,
`table.py`, `shares.py`), the runs' logs and per-frame CSVs, and the trace with its breakdowns.
