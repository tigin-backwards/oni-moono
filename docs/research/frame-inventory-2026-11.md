# Frame inventory, every tweak on: U57 and 2minnow (2026-11-07)

Where Moono's frame goes with `--game-tweak all` on the current build, for a late-game colony (U57)
and a mid-game one (2minnow). It uses the same method as the pre-tweak inventory of 2026-10-19
(docs/research/u57-frame-inventory.md) and goes to the same level of detail.

**Build:** origin/master 740fb545 ("tweaks: instant-reachability hands back stray offsets and old
tables"), release, built in a worktree (out/inv-wt). CoreCLR 10.0.13, Server GC (concurrent).

**The short version.**

| | U57 (cycle 17,077) | 2minnow (cycle 1,990) |
|---|---|---|
| Frame (perf run, Linux/WSL2, speed 3) | **24.8 ms, 40.4 fps** | **27.2 ms, 36.7 fps** |
| What limits it | the main thread (busy 98%) | the GL thread under Mesa's D3D12 layer (the main thread waits 5.2 ms a frame for the device) |
| Main thread busy | 97.9% | 75.6% |
| Game code on the main thread | 14.4 ms (64% of sampled time) | 10.3 ms (56%) |
| Engine (Moono) on the main thread | 5.0 ms (22%) | 5.7 ms (31%) |
| Tweaks' own code | 1.8 ms (8%) | 1.1 ms (6%) |
| uGUI/TMP | 0.9 ms (4%) | 1.1 ms (6%) |
| GC on the main thread | 0.13 ms (sampled), plus a gen0 or gen1 pause of 30–450 ms every 30–60 s | 0.05 ms, plus pauses of 14–160 ms every 20–40 s |
| Oct 19 (pre-tweak, slow WSL) | 82 ms, 12.2 fps | — |

2minnow's numbers on Linux are GL-bound because of WSL's Mesa-over-D3D12 layer: a texture upload
costs about 75 µs there, against about 2.5 µs on Windows (docs/PERFORMANCE.md). On Windows the same
colony would be main-thread-bound, as U57 is here. Use the Linux numbers for CPU-side direction,
not frame rates (Windows: U57 about 144 fps, 2minnow about 160 fps with every tweak; PERFORMANCE.md).

## Where the main thread goes

Each colony's sampled main-thread CPU per frame (U57 22.5 ms, 2minnow 18.5 ms), split into
groups that don't overlap, from the perf tables below. Removing a share x raises the frame rate
by up to 1/(1 − x) where the main thread limits the frame, as on Windows for both colonies.

| Group | Where | U57 ms | U57 % | 2minnow ms | 2minnow % |
|---|---|---|---|---|---|
| Animation: updates, batch writes and uploads, events | Game | 2.97 | 13.2% | 2.64 | 14.3% |
| Per-object monitors and checks (building inputs, plant absorbers, sublimation, critter temperature, ranching, building heat, work ticks, amounts, gas exposure, suit lockers) | Game | 2.14 | 9.5% | 1.34 | 7.2% |
| Rendering (draw recording, culling, HUD batching) | Engine | 1.80 | 8.0% | 2.89 | 15.6% |
| Duplicant and critter brains (chore choice, sensors, the fetch scan's main-thread share) | Game | 1.50 | 6.7% | 1.05 | 5.7% |
| Canvas build and update | Engine | 1.30 | 5.8% | 1.31 | 7.1% |
| UI input (raycasts) | Engine | 1.29 | 5.7% | 1.08 | 5.8% |
| Conveyors (rail sim, items moving, transfer arms) | Game | 1.18 | 5.2% | 0.14 | 0.8% |
| Moving objects (cell-change handlers) | Game | 1.18 | 5.2% | 0.78 | 4.2% |
| Pipes and power | Game | 1.03 | 4.6% | 0.41 | 2.2% |
| World visuals (conduit flow meshes, property textures, wires, name labels, hover card) | Game | 0.97 | 4.3% | 0.59 | 3.2% |
| Navigators and state transitions (walking steps, polled conditions) | Game | 0.91 | 4.0% | 1.00 | 5.4% |
| Reachability (instant-reachability, including the Forget scan) | Tweak | 0.50 | 2.2% | 0.08 | 0.4% |
| Script dispatch and object lifecycle | Engine | 0.45 | 2.0% | 0.34 | 1.8% |
| Garbage collection | Runtime | 0.13 | 0.6% | 0.05 | 0.3% |
| Everything else: dozens of game systems under ~0.2 ms each (doors, chore drivers, colliders, sounds, schedulers, nav grid updates, partitioner queries, spawns), uGUI/TMP outside the canvas | mixed | 5.15 | 22.9% | 4.80 | 25.9% |
| **Total** | | **22.5** | **100%** | **18.5** | **100%** |

Off the main thread, not in these shares: the fetch scan's workers (3.8 ms CPU a frame in U57, 1.9 in
2minnow), path probes, draw recording, texture copies and the GL thread.

## Method

Each colony was run in Moono's Linux player under WSL2 (`scripts/run.sh` → `contain.sh`, Xvfb
1280×720, `--game-tweak all`). PerfProbe loaded the save from the menu and kept the fastest
speed, which is speed 3 (its `speed.txt` counts from 0, so speed 3 is 2). Every capture also
had:

- **NoVictoryPopup**, the test mod. It marks the victory achievements as reached when the colony
  spawns, so the "Better Together" alert (2minnow) and its U57 counterparts never open. Its log
  shows four achievements marked in each colony.
- **Autosave off** (`noautosave.txt`), so no save lands in a window.

Four runs, back to back, then two stopwatch runs (see "Wall times" below), all holding `~/.cache/oni-brutal-e2e/perf.lock` exclusively with
`perf.wanted` claimed:

| Run | What | Window |
|---|---|---|
| `u57-perf`, `mn-perf` | `perf record -e cpu-clock:u -F 499 -g` of every thread of the player, 90 s, starting 60 s after the first frame-rate window (about 2 min after the load). `DOTNET_PerfMapEnabled=1`; JIT code (`memfd:doublemapper`) resolved against the run's `perf-PID.map` (the last entry wins where code memory was reused), libcoreclr against the symbols of 10.0.13 (build-id matches). `/proc` per-thread schedstat before and after the window for each thread's busy time. | U57 f≈1,880–5,525, 3,645 frames. 2minnow f≈2,300–5,600, 3,312 frames |
| `u57-stats`, `mn-stats` | `--frame-stats` with `ONI_BUCKET_PROFILE=1`: 600-frame windows of frame phases, script times, `sim`, `game update`, waits and the 40 costliest state machine update kinds. PerfProbe's `counts.txt` (instances by component type at load) and a census (live counts of registered animations, conveyor items, conduits, brain groups, fetch groups; local-only PerfProbe addition, not committed). | 12 (U57) and 11 (2minnow) steady windows after 3 warm-up windows |

The frame-stats runs ran at the same frame rate as the perf runs: U57 38–44 fps, 2minnow 34–40.
Their per-system ms carry the timing patches' own cost (PERFORMANCE.md: up to about 10–15%). The
perf runs have no timing patches. Where the two disagree, the perf number is the CPU one, and the
frame-stats number is wall time, including waits that perf doesn't sample.

**Checks on the windows.**
- **Popups.** Screenshots at the start, middle and end of every window (the run folders'
  s0/s1/s2.png) show no dialog or alert. The only overlay is the hover card under the
  pointer, which sits in the middle of the window, as in the Oct 19 runs.
- **Other players.** Logged every 5 s; none ran during any of the four windows.
- **Cycle ends.** U57's cycle 17,077 → 17,078 ended inside both U57 windows, and 2minnow's
  1,990 → 1,991 inside its stats run's warm-up. Without the autosave, a cycle end costs only the
  daily report and the timelapse.

**Caveats.**
- perf runs with `perf_event_paranoid=2`, so it samples user mode only. Kernel time (syscalls,
  futex, page faults) is not in the samples; schedstat's busy time includes it.
- Blocked time is not sampled either. The main thread's waits for job workers, the device and GC
  pauses come from `--frame-stats`.

The capture and analysis scripts were one-off (one capture per run, a lock-and-queue driver, a perf
script reader for threads, owners, entries, inclusive time, callees and callers, and a frame-stats
reader for medians and bucket means); they are not kept in the repository.

## Object counts at load

PerfProbe's `counts.txt` counts `FindObjectsByType` instances, prefab templates included. The
census counts live game lists, at f724 (the census was repeated every 600 frames and stayed
within a few percent).

| | U57 | 2minnow | Oct 19 U57 |
|---|---|---|---|
| Duplicants (live) | 22 (25 MinionIdentity incl. stored/templates) | 50 (53) | 24 |
| Brains: Duplicant group / critter group | 25 / 1,069 | 50 / 615 | 1,186 critters |
| CreatureBrain instances | 1,186 | 737 | 1,186 |
| Navigators | 1,270 | 789 | |
| BuildingComplete (live list) | 61,967 | 13,971 | ~62,000 |
| Buildings under construction | 494 | 465 | |
| Workables | 441,191 | 112,184 | 441,253 |
| Pickupables (live): loose / stored | 27,570: 24,870 / 2,700 | 7,055: 4,881 / 2,174 | ~27,000 |
| Fetchables in FetchManager: prefab groups / items / biggest group | 210 / 20,972 / 2,351 | 153 / 4,449 / 1,359 | 211 groups |
| Fetch chores | 395 | 136 | |
| KBatchedAnimController | 89,466 | 23,687 | 89,794 |
| Controllers in the animation grid (chunk infos) | 81,983 | 18,189 | |
| Registered (playing) animations visited a frame | 3,569 (+180 always) | 1,685 (+55) | ~4,400 |
| Moving animated controllers | 480 | 146 | 1,400 moved/frame |
| AnimEventManager entries (playing animations) | 6,483 | 2,607 | ~6,450 |
| Conveyor items in transit / rail segments | 2,921 / 7,168 | 176 / 569 | ~950 |
| Liquid / gas conduits | 12,816 / 3,041 | 2,166 / 1,969 | |
| ConduitConsumer / Dispenser | 821 / 540 | 401 / 223 | 821 consumers |
| SolidConduitConsumer / Dispenser | 209 / 199 | 41 / 49 | 209 |
| Sublimates | 2,457 | 387 | 2,457 |
| RequireInputs | 1,918 | 713 | 1,918 |
| Generators / EnergyConsumers / Batteries | 214 / 1,176 / 97 (live) | 88 / 318 / 13 | 109 generators |
| Electrobanks | 2,710 | 1,419 | |
| Crops / Growing | 1,183 / 1,079 | 800 / 583 | |
| KCollider2D (box / circle) | 93,822 (74,628 / 19,194) | 24,596 | 93,840 |
| Radbolts | 3 | 0 | 4 |
| Doors | 779 | 168 | |
| LoopingSounds | 25,365 | 9,725 | |
| OccupyArea / KSelectable / Prioritizable | 89,112 / 96,853 / 92,164 | 21,553 / 26,848 / 23,357 | |
| StateMachineControllers | 93,319 | 24,472 | |
| KMonoBehaviour (all) | 2,118,811 | 587,403 | |
| Worlds | 2 | 14 | |

## The frame, thread by thread

Busy is schedstat run time over the 90 s window (user and kernel). ms/frame is busy time
divided by frames, so it is CPU per frame, not wall time on the frame's critical path.

| Thread | U57 busy | U57 ms/frame | 2minnow busy | 2minnow ms/frame | What runs |
|---|---|---|---|---|---|
| **Main** | **97.9%** | 24.2 | **75.6%** | 20.6 | the game, the engine's player loop, draw recording (part) |
| GL worker (`GL`) | 36.5% | 9.0 | **61.4%** | 16.7 | GL calls: draws, texture uploads, binds, presents. Mostly inside Mesa (gallium) and NVIDIA's D3D12 user-mode driver |
| Mesa driver thread (`gdrv0`) | 40.3% | 10.0 | 50.2% | 13.7 | Mesa's threaded-context worker: all native (gallium, nvwgf2umx, d3d12core) |
| Path probe worker | 19.4% | 4.8 | 21.3% | 5.8 | AsyncPathProber with `path-probe-repair` (ProbeRepair.Flood, PathProber.Run) |
| SimDLL (2 / 1 threads) | 14.2% | 3.5 | 13.5% | 3.7 | UpdatePressure, SimBase.UpdateData, BuildingHeatExchange, temperature tasks, property textures |
| FMOD (5 threads) | 11.5% | 2.8 | 17.2% | 4.7 | the mixer (libfmodstudio). 2minnow has more sound sources in view |
| KFastWorker ×15 (fast-jobs) | 57.9% total | 14.3 total | 40.8% total | 11.1 total | fetch pickups, conveyor arms, amounts, conduit networks, chore collection. Spinning included |
| Draw recording ×3 | 9.0% total | 2.2 | 12.0% total | 3.3 | GlDrawRecorder.RecordAhead/RecordChunk, uniform resolution |
| .NET thread pool | 3.6% (6 thr) | 0.9 | 12.6% (12 thr) | 3.4 | texture snapshot copies (memcpy), fast-anim-batches bodies, spin |
| Presenter | 4.4% | 1.1 | 4.3% | 1.2 | GlPresenter.PresentBuffer (glX swap through Mesa) |
| .NET Server GC / BGC | 1.3% | 0.3 | 0.7% / 2.2% | 0.2 / 0.6 | GC work (marking, background GC) |
| Tiered JIT | 1.1% | 0.3 | 1.5% | 0.4 | recompiles (Harmony-patched methods are re-JITed too) |

Process CPU (frame stats): U57 2.8 cores, 2minnow 3.2 cores.

**Main-thread waits** (frame stats, medians, ms/frame):

| | U57 | 2minnow |
|---|---|---|
| for the device to take the next frame | 0.40 | **5.19** |
| for job workers (all `JobManager.Run` calls, 11 a frame) | 0.29 | 0.14 |
| GC paused (median window) | 0.00 | 0.00 |
| GC collections in the steady windows | one every 30–60 s, pausing 32–449 ms (frame stats: gen0-only windows 32–127 ms, a gen1 window 381 ms) | one every 20–40 s, pausing 14–161 ms (gen0 14–54, gen1 40–161) |
| JIT, all threads | 0.25 | 0.31 |
| lock contentions | 5.6 a frame | 5.6 a frame |
| allocation | 17.8 MiB/s | 15.7 MiB/s |

## Main-thread time by owner (perf)

Of each frame's samples on the main thread (U57 22.5 ms sampled of 24.8; 2minnow 18.5 of 27.2;
the rest is blocked time, above).

The first two columns of each colony give the managed owner: the nearest managed caller,
so a native memcpy called from the engine counts as engine, and GC is counted when the stack is
in the collector. The "Leaf" column gives who runs the sampled instruction itself.

| Owner | U57 ms | U57 share | U57 leaf | 2minnow ms | 2minnow share | 2minnow leaf |
|---|---|---|---|---|---|---|
| Game (Assembly-CSharp, -firstpass, Harmony replacements of game methods) | 14.40 | 64.0% | 13.08 | 10.29 | 55.7% | 9.25 |
| Engine (OniMoono.UnityEngine, .Desktop, Brutal) | 4.99 | 22.2% | 4.35 | 5.71 | 30.9% | 4.77 |
| Tweaks (OniMoono.GameTweaks) | 1.82 | 8.1% | 1.46 | 1.11 | 6.0% | 0.78 |
| uGUI / TextMeshPro (ours) | 0.90 | 4.0% | 0.88 | 1.08 | 5.8% | 1.03 |
| GC (collector on the stack) | 0.13 | 0.6% | 0.03 | 0.05 | 0.3% | 0.03 |
| other (FMOD/SimDLL P/Invoke frames, PerfProbe, Steam) | 0.2 | 0.9% | | 0.2 | 1.1% | |
| *leaf only:* BCL (System.*) | | | 1.30 | | | 1.16 |
| *leaf only:* CoreCLR (write barriers, dispatch stubs, monitor fast paths, allocation) | | | 0.76 | | | 0.73 |
| *leaf only:* native (libc memcpy/free, libm, SimDLL, FMOD) | | | 0.60 | | | 0.69 |

The runtime's own share is small: write barriers 0.12 ms, virtual-dispatch stubs 0.07, monitor
fast paths 0.07, allocation helpers about 0.03. The game's code dominates in both colonies. The
engine's share is larger in 2minnow because more is drawn: 1,060 draws against 777, and 132
texture uploads a frame against 70.

### By player-loop entry (perf, main-thread CPU, ms/frame)

The % columns here and in "System by system" are shares of the main thread's sampled CPU per
frame (U57 22.5 ms, 2minnow 18.5 ms). Where the main thread limits the frame, as it does for
both colonies on Windows, removing a system's share x raises the frame rate by up to
1/(1 − x): 2% gives about +2%, 5% about +5%, 10% about +11%. Windows splits the frame
differently (rendering and uploads cost less there), so treat the shares as a guide.

| Entry | U57 | U57 % | 2minnow | 2minnow % | Oct 19 U57 |
|---|---|---|---|---|---|
| `Game.Update` (SimEveryTick: state machine sub-ticks, 200 ms sim step; cell-change monitor; …) | 9.78 | 43.5% | 6.13 | 33.1% | 41 |
| `Game.LateUpdate` (render-every-tick buckets with BrainScheduler, conduit visualizers) | 2.46 | 10.9% | 1.68 | 9.1% | 5 |
| `Global.LateUpdate` (KBatchedAnimUpdater) | 1.84 | 8.2% | 0.79 | 4.3% | 4.5 |
| Rendering (FrameRenderer.RenderFrame) | 1.73 | 7.7% | 2.73 | 14.8% | 6.3 |
| UI input (StandaloneInputModule: raycasts) | 1.29 | 5.7% | 1.08 | 5.8% | 0.7 |
| Canvas build (CanvasSystem.Build) | 0.72 | 3.2% | 0.55 | 3.0% | 2.4 |
| `World.LateUpdate` (animation batch writes, KAnimBatchManager.Render) | 0.68 | 3.0% | 1.48 | 8.0% | 2.2 |
| Canvas update (willRenderCanvases: TMP rescale, CanvasUpdateRegistry) | 0.53 | 2.4% | 0.66 | 3.6% | 0.5 |
| `Global.Update` (AnimEventManager) | 0.51 | 2.3% | 0.39 | 2.1% | 2.6 |
| engine phase overhead (dispatch, coroutines, starts) | 0.45 | 2.0% | 0.34 | 1.8% | |
| `PropertyTextures.LateUpdate` | 0.19 | 0.8% | 0.45 | 2.4% | |
| `InterfaceTool`/`SelectTool.LateUpdate` (hover) | 0.18 | 0.8% | 0.14 | 0.8% | |
| `LineLayer.Update` | 0.17 | 0.8% | 0.13 | 0.7% | |
| `KMonoBehaviour.OnDestroy` / `Spawn` (object churn) | 0.14 / 0.14 | 1.2% | 0.04 / 0.03 | 0.4% | |
| `Scheduler.Update` (GameScheduler) | 0.13 | 0.6% | 0.18 | 1.0% | |
| `KBatchedAnimTracker.LateUpdate` | 0.10 | 0.4% | 0.15 | 0.8% | |
| `WorldInventory.Update` | 0.11 | 0.5% | 0.05 | 0.3% | |
| `NameDisplayScreen.LateUpdate` | 0.10 | 0.4% | — |  | |
| `AnimEventHandlerManager.LateUpdate` | 0.06 | 0.3% | 0.14 | 0.8% | |
| `SuitMarker.Update` | 0.05 | 0.2% | 0.12 | 0.6% | |
| autosave (Oct: 9 ms averaged) | off |  | off |  | 9 |

### Frame stats: phases, scripts, sim and game update (medians of the steady windows)

| Frame phase (ms/frame) | U57 | 2minnow |
|---|---|---|
| early + fixed | 0.15 | 0.17 |
| update | 14.23 | 9.97 |
| late | 6.85 | 6.49 |
| post-late (canvas build) | 1.09 | 1.13 |
| render | 2.08 | 3.27 |
| present (on the main thread; includes the wait for the device) | 0.17 | 4.85 |
| GL thread busy (device) | 15.95 | 25.59 |
| draws / texture uploads / mesh uploads a frame | 777 / 70 / 0.6 | 1,060 / 132 / 1.3 |
| program / texture / VAO binds a frame | 257 / 3,322 / 1,099 | 326 / 5,282 / 1,652 |
| uniforms sent / unchanged | 1,777 / 19,386 | 3,489 / 25,746 |

| Script time (ms/frame) | U57 | 2minnow |
|---|---|---|
| Game.Update | 9.99 | 6.66 |
| Game.LateUpdate | 3.19 | 2.68 |
| Global.LateUpdate | 1.80 | 0.81 |
| EventSystem.Update (UI input) | 1.15 | 1.01 |
| World.LateUpdate | 0.68 | 1.57 |
| Global.Update | 0.53 | 0.50 |
| PropertyTextures.LateUpdate | 0.29 | 0.64 |
| SparkLayer.Update | 0.18 | 0.17 |
| SelectTool.LateUpdate | 0.17 | 0.14 |
| KBatchedAnimTracker.LateUpdate | 0.15 | 0.19 |
| GameScheduler.Update | 0.13 | 0.22 |
| DissolvingAlgae.Update | 0.13 | — |
| AnimEventHandlerManager.LateUpdate | 0.05 | 0.17 |

| `sim` (ms/frame) | U57 | 2minnow |
|---|---|---|
| Game.SimEveryTick (SimDLL part) | 8.42 (0.17) | 5.75 (0.07) |
| Game.UnsafeSim200ms (SimDLL) | 2.76 (0.14) | 1.43 (0.06) |
| · Game.StepTheSim (SimDLL) | 2.52 (0.14) | 1.13 (0.06) |
| SimDLL calls on the main thread | 0.17 ms in 2,758 calls | 0.08 ms in 986 calls |
| 200 ms sim steps a frame | 0.37 | 0.40 |
| game seconds a real second (asked 3.00) | 2.96–2.98 | 2.6–3.0 |

| `game update` (ms/frame, calls a frame) | U57 | 2minnow |
|---|---|---|
| state machines, render every tick (BrainScheduler inside) | 2.42 (1.00) | 1.76 (1.00) |
| · brains | 2.00 (1.00) | 1.46 (1.00) |
| render 200 ms / 1000 ms | 0.07 / 0.04 | 0.02 / 0.03 |
| sim every tick | 0.47 (4.5) | 0.73 (4.7) |
| sim 33 ms | 0.51 | 0.58 |
| sim 200 ms | 3.21 | 2.40 |
| sim 1000 ms | 1.29 | 0.57 |
| sim 4000 ms | 0.08 | 0.07 |
| nav grids | 0.23 (0.41) | 0.29 (0.39) |
| path probes (handing out work) | 0.03 | 0.04 |
| anim events | 0.51 | 0.47 |
| jobs (11 runs a frame) | 1.96 | 1.86 |
| · waiting for workers | 0.29 | 0.14 |

### Bucket profile: the costliest update kinds (mean over windows, timed)

% main is of the frame-stats runs' main-thread time per frame (U57 24.6 ms; 2minnow 21.0 ms,
leaving out its 4.85 ms wait for the device). These are timed, so they carry the timing patches'
own cost.

U57 (all buckets 7.5–10.6 ms a frame, 512–524 kinds):

| Update kind | rate | ms/frame | % main | entries a call | µs/entry |
|---|---|---|---|---|---|
| BrainScheduler | render every tick | 2.03 | 8.3% | 1 | 2,027 |
| RequireInputs | 200 ms | 0.43 | 1.8% | 1,746 | 0.66 |
| SolidTransferArm (batch, jobs) | 1000 ms | 0.41 | 1.7% | 278 | 19.6 |
| UpdateNavigator (Navigator.States) | every tick | 0.37 | 1.5% | 109 | 0.76 |
| Sublimates (fast-sublimation batch) | 200 ms | 0.34 | 1.4% | 207 (per tick slice) | 0.36 |
| CritterTemperatureMonitor.root | 200 ms | 0.24 | 1.0% | 1,064 | 0.61 |
| AmountInstance (batch, jobs) | 200 ms | 0.23 | 0.9% | 14,908 | 0.04 |
| RanchStation FindRanachable | 200 ms | 0.21 | 0.9% | 62 | 9.1 |
| Work(Pickup) (FetchAreaChore) | 33 ms | 0.21 | 0.9% | 8 | 5.4 |
| KComponentsInitializer (KComponents: structure temperatures, disease containers) | 200 ms | 0.17 | 0.7% | 1 | 463 |
| OvercrowdingMonitor | 1000 ms | 0.15 | 0.6% | 31 | 1.1 |
| EggProtectionMonitor find_egg (batch) | 200 ms | 0.15 | 0.6% | 66 | 6.0 |
| SolidTransferArm | render every tick | 0.10 | 0.4% | 278 | 0.35 |
| CreatureCalorieMonitor | 200 ms | 0.08 | 0.3% | 919 | 0.24 |
| ConvertMass (ElementConverter) | 1000 ms | 0.08 | 0.3% | 2 | 7.7 |
| LoopingSoundManager | render every tick | 0.08 | 0.3% | 1 | 75 |
| ChoreDriver haschore / nochore | 200 ms | 0.07 / 0.06 | 0.5% | 763 / 683 | 0.26 / 0.21 |
| Rottable | 1000 ms | 0.07 | 0.3% | 5 | 3.2 |
| TemperatureVulnerableUpdater | 200 ms | 0.07 | 0.3% | 1 | 188 |
| ArtifactModule | render every tick | 0.07 | 0.3% | 58 | 1.1 |
| GasLiquidExposureMonitor | 33 ms | 0.07 | 0.3% | 22 | 1.3 |
| Door.Controller RefreshIsBlocked | 200 ms | 0.06 | 0.2% | 768 | 0.22 |
| KBoxCollider2D / KCircleCollider2D | render every tick | 0.06 / 0.06 | 0.5% | 168 / 217 | 0.38 / 0.26 |
| DrowningMonitorUpdater | 200 ms | 0.06 | 0.2% | 1 | 157 |
| Electrobank | 200 ms | 0.06 | 0.2% | 2,702 | 0.06 |
| OxygenBreather | 200 ms | 0.05 | 0.2% | 22 | 6.4 |
| HighEnergyParticle ready | every tick | 0.05 | 0.2% | 2 | 5.5 |
| PressureVulnerableUpdater | 200 ms | 0.05 | 0.2% | 1 | 123 |
| ScenePartitioner | 1000 ms | 0.04 | 0.2% | 1 | 546 |
| ReachabilityMonitor (game polling) | 1000 ms | **not in the top 40** | 162.8% | | |

2minnow (all buckets about 6 ms):

| Update kind | rate | ms/frame | % main | entries a call | µs/entry |
|---|---|---|---|---|---|
| BrainScheduler | render every tick | 1.47 | 7.0% | 1 | 1,473 |
| UpdateNavigator | every tick | 0.72 | 3.4% | 90 | 1.66 |
| KComponentsInitializer | 200 ms | 0.27 | 1.3% | 1 | 680 |
| GasLiquidExposureMonitor | 33 ms | 0.20 | 1.0% | 50 | 1.66 |
| RequireInputs | 200 ms | 0.19 | 0.9% | 541 | 0.87 |
| AmountInstance (batch) | 200 ms | 0.17 | 0.8% | 8,971 | 0.05 |
| CritterTemperatureMonitor | 200 ms | 0.15 | 0.7% | 605 | 0.61 |
| Work(Pickup) | 33 ms | 0.13 | 0.6% | 3 | 8.7 |
| SuitLocker ChargeSuit | 200 ms | 0.13 | 0.6% | 2 | 157 |
| RanchStation FindRanachable | 200 ms | 0.11 | 0.5% | 13 | 21.8 |
| OxygenBreather | 200 ms | 0.10 | 0.5% | 50 | 5.0 |
| OvercrowdingMonitor | 1000 ms | 0.10 | 0.5% | 13 | 1.5 |
| Sublimates (batch) | 200 ms | 0.09 | 0.4% | 32 | 0.59 |
| LightSymbolTracker | render every tick | 0.09 | 0.4% | 205 | 0.45 |
| ChoreDriver haschore | 200 ms | 0.08 | 0.4% | 542 | 0.38 |
| BionicOxygenTankMonitor (critical) | 200 ms | 0.08 (in 3 of 11 windows: 2.2 ms a call) | 0.4% | 1 | 2,217 |
| KBoxCollider2D | render every tick | 0.07 | 0.3% | 126 | 0.51 |
| PressureVulnerableUpdater | 200 ms | 0.06 | 0.3% | 1 | 161 |
| SolidTransferArm (batch) | 1000 ms | 0.06 | 0.3% | 42 | 16.9 |

## System by system

ms are main-thread CPU per frame from perf (inclusive, no timing patches) unless marked
*(fs)*, which means frame-stats wall time. The Oct 19 column is from the pre-tweak inventory
(82 ms frame on the slow WSL). Mechanisms are from the decompiled game code (cache/decomp).
Status names the tweak that covers a system and what it saved; "real work" means nothing exact
is known to skip.

### U57

| System | ms | % main | Objects | Why it costs that | Status | Oct 19 |
|---|---|---|---|---|---|---|
| Brains: `BrainScheduler.RenderEveryTick` (via `BrainRate`) | 1.50 (fs 2.00) | 6.7% | 25 Duplicant brains, 1,069 critter brains; about 0.75 Duplicant and 3.75 critter updates a frame at 40 fps | `MinionBrain.UpdateBrain` 1.10: its sensors 0.78 (FetchManager.UpdatePickups 0.36 on the main thread, ClosestPickupableSensor 0.38, PickupableSensor 0.12), Brain.UpdateChores 0.46 (ChoreConsumer.FindNextChore 0.50 incl. chore collection); critter brains' sensors 0.18 | `brain-rate` caps updates at 30 Duplicant and 150 critter brains a second; `fast-offset-tables`; `path-probe-repair` off the main thread. Remaining: real work | 2.7 |
| `FetchManager.UpdatePickups` (inside the brain update) | 0.36 main CPU, wall about 0.9–1.1; worker CPU **3.79 ms a frame** | 1.6% | 20,972 fetchables in 210 prefab groups, 1 Duplicant update ≈ 0.75 a frame | for each Duplicant update: one job item per prefab group runs `GatherPickupablesWhichCanBePickedUp` (`CouldBePickedUpByMinion` on every fetchable: 1.46 ms) and `GatherReachablePickups` (`GetNavigationCost` per distinct cell: 1.48 ms), then sorts the group (0.81 ms) and dedups. The main thread runs its share and waits for the rest | `fast-offset-tables` (offset rechecks only for items that can go stale), `fast-jobs` (no waking of all workers). Remaining: a full scan of every fetchable per Duplicant update, which is polling (see "What's left") | 1.7 (stopwatch) |
| `GlobalChoreProvider.UpdateFetches` | 0.11 | 0.5% | 395 fetch chores on 2 worlds | per Duplicant update: a navigation cost per fetch chore's destination, sort, dedup by tags/priority | real work (small) | — |
| Animation updates: `KBatchedAnimUpdater` (`Global.LateUpdate`) | 1.84 (fs 1.80) | 8.2% | 3,569 registered + 180 always; 81,983 in the grid; 480 moving | `UpdateRegisteredAnims` 1.56: `UpdateAnim` per playing animation in visible chunks (advancing time, frame change, dirty marks; 0.59 self), `KAnimSynchronizer.SyncTime` 0.25, `UpdateFrame` 0.29; `ProcessMovingAnims` 0.13; `CleanUp` 0.10 | `fast-anim-cleanup` (cleanup 0.48 → 0.10); `off-screen-conveyors` (items off screen not moved). Remaining: real work, scales with what's in view | 3.2 |
| Cell-change monitor: `CellChangeMonitor.RenderEveryTick` | 1.18 | 5.2% | moved transforms (mostly 2,921 conveyor items in transit and 480 moving controllers) | per dirty transform: the handler dictionary, `SetCell`, and for a cell change `TriggerUpdate` (0.91): `Pickupable.OnCellChange` 0.77 (events, object layers: `ObjectLayerListItem.Refresh` 0.49, absorbing neighbours, which destroys items) | `off-screen-conveyors` places off-screen rail items only when they stop or come into view. Remaining: real work | 2.8 |
| Animation events: `AnimEventManager.Update` (`Global.Update`) | 0.49 (fs 0.51) | 2.2% | 6,483 playing animations | the per-frame loop over every playing animation's compact mirror (0.34 in `FastAnimEvents.Update`), firing frame events (sounds, `EventSystem.Trigger` 0.08) | `fast-anim-events`: 2.07 → 0.90 ms (Oct), now 0.49 | 2.5 |
| Animation batches: `KAnimBatchManager.UpdateDirty` (`World.LateUpdate`) | 0.46 + Render 0.12 + active area 0.06 | 2.6% | dirty batches of 81,983 controllers | `BatchSet.UpdateDirty` 0.23 (per dirty batch: instance and symbol data to the batch textures), `FastAnimBatches.Prefix` 0.21 (handing controllers to workers); the workers' share is on the thread pool (about 0.2 ms) | `fast-anim-batches`, `fast-symbol-overrides` (unchanged override blocks not re-sent). Remaining: real work | 1.7 |
| Conveyor rails: `SolidConduitFlow.Sim200ms` | 0.52 | 2.3% | 7,168 segments, 2,921 items, 209 consumers, 199 dispensers | bridges and consumers each 200 ms tick (`SolidConduitBridge.ConduitUpdate` 0.23, consumers 0.15); moving items between segments; removals (`RemovePickupable` 0.20, events) | real work | 1.5 |
| Conveyor items moving: `SolidConduitFlow.RenderEveryTick` | 0.26 (OffscreenConveyors 0.25) | 1.2% | items on rails | items on screen interpolated along their rails each frame (SetPosition) | `off-screen-conveyors` (changes positions while off screen) | 1.1 |
| Pipes: `ConduitFlow.Sim200ms` (liquid, gas) | 0.41 | 1.8% | 12,816 + 3,041 conduits, 821 consumers, 540 dispensers | network updates on the job workers (0.76 ms worker CPU), consumers, dispensers, bridges on the main thread | real work (`fast-jobs`) | 1.5 |
| Power: `EnergySim.EnergySim200ms` + `CircuitManager` | 0.38 + 0.24 | 2.8% | 214 generators, 1,176 consumers, 97 batteries, 2,710 electrobanks | each generator each 200 ms tick (`EnergyGenerator` 0.16, type checks, operational flags), circuits' Sim200msLast | real work | 1.6 |
| Conveyor arms: `SolidTransferArm` | 0.40 main; worker CPU 1.29 | 1.8% | 278 arms | `AsyncUpdate` on workers (scan pickupables in reach), `Sim` and finish on the main thread, `RenderEveryTick` 0.10 (arm animation) | real work (`fast-jobs`) | — |
| `RequireInputs.CheckRequirements` | 0.35 (bucket 0.43) | 1.6% | 1,746 a 200 ms tick | per building: power and conduit connection, status-item toggles | real work: inputs re-read each tick. Event-driven is possible but fussy (see below) | 1.4 |
| `PlantElementAbsorbers.Sim200ms` | 0.34 | 1.5% | absorbing plants | the batch is cheap; 0.22 of it is `EventSystem.Trigger` (irrigation monitors' handlers) | real work | 1.8 |
| Sublimation: `Sublimates` | 0.34 (bucket 0.34) | 1.5% | 2,457 items (207 a tick slice) | items not blocked emit; blocked ones are skipped in the batch | `fast-sublimation`: 2.4 → 1.0 (Oct) → 0.34 | 2.4 |
| State transitions: `TransitionDriver.UpdateTransition`, `TransitionUpdater` | 0.34 + 0.26 | 2.7% | moving navigators, polled transitions | navigator transitions (walking/climbing steps); polled transition conditions (radiation, fertility, incubation, suffocation) | real work | 1.2 |
| `UpdateNavigator` / `Navigator.AdvancePath` | 0.31 (bucket 0.37) | 1.4% | 109 moving navigators | per tick, each moving navigator's path step | real work | — |
| `CritterTemperatureMonitor` | 0.28 (bucket 0.24) | 1.2% | 1,064 critters | target state per critter from its cell temperatures; `GetTargetState` 0.14 self | real work (temperature checks are never delayed) | 1.1 |
| `RanchStation` FindRanachable | 0.24 (bucket 0.21) | 1.1% | 62 stations | each station scans the ranchable critters in its room (9 µs a station) | real work; polls critters that rarely change | 0.9 |
| `ScenePartitioner` (Sim1000ms cleanup and queries) | 0.32 | 1.4% | spatial index | `fast-partitioner-cleanup` makes the once-a-second cleanup look only at removed entries (0.04 in buckets); the rest is gathers by other systems | covered | — |
| NavGrid.UpdateGraph | 0.23 | 1.0% | dirty nav cells | rebuilding links of changed cells | real work (`compact-nav-links`) | ≤0.6 |
| KComponents Sim200ms (`StructureTemperatures` 0.12, `DiseaseContainers` 0.02) | 0.20 (bucket 0.17) | 0.9% | building heat | per building's heat exchange bookkeeping | real work | — |
| `StandardWorker.Work` | 0.20 | 0.9% | 22 Duplicants, remote worker docks | work ticks; element splitting on gunk removal and oil refill | real work | 1.6 |
| Amounts (`AmountInstance.BatchUpdate`) | 0.12 main; worker 0.77 | 0.5% | 14,908 amounts | batched on jobs | real work | — |
| Radbolts (`HighEnergyParticle`) | 0.07 | 0.3% | 3 | collider gathers skipped where no other radbolt is near | `fast-particle-collision`: 0.94 → 0.07 | 0.9 |
| **Reachability** (game polling, `ReachabilityMonitor`) | **0.015** | 0.1% | 27,711 workables answered by events, 0 polled | — | `instant-reachability`: replaced 2.5 ms of polling. Own cost below | 2.5 |
| **InstantReachability's own cost** | **0.48**, of it **`Forget` 0.42** | 2.1% | 27,711 covered entries | `Forget` removes the entry from `covered` (a `List<Entry>`) with `IndexOf`: a linear scan of 27,711 references for every unregistered workable (items absorbed in `Pickupable.OnCellChange` 0.25, items destroyed in `StepTheSim` 0.11, …). The event work proper (`Drain`, cell crossings) is about 0.05 | **a cheap exact fix**: keep each entry's index in `covered` and swap-remove (O(1)). About 0.4 ms a frame in U57, 0.05 in 2minnow | — |
| Game events (`EventSystem.Trigger`, all callers) | 1.31 total, 0.15 self | 5.8% | | handler lookup and type checks per handler; spread over the systems above (OnCellChange 0.28, plant absorbers 0.22, conveyor removals 0.20, anim events 0.08) | cross-cutting | — |
| Object churn: spawn / destroy | 0.39 / 0.18 | 2.5% | items split, absorbed, spawned by the sim | `KMonoBehaviour.Spawn`, awake/enable, `KDestroyGameObject` | real work | 0.6 |
| Conduit visualizers (solid, liquid/gas) | 0.18 + 0.14 | 1.4% | rails and pipes in view | rebuilding the flow meshes each frame | real work | — |
| `PropertyTextures.LateUpdate` | 0.19 (fs 0.29) | 0.8% | | uploading property textures (`LoadRawTextureData`) and a job | real work | — |
| `LineLayer.Update`, `SparkLayer` | 0.17, fs 0.18 | 0.8% | wires in view | | real work | — |
| Hover (`InterfaceTool`/`SelectTool.LateUpdate`) | 0.18 | 0.8% | the hover card | the pointer rests on the world, so the hover card is rebuilt | real work | — |
| `NameDisplayScreen.LateUpdate` | 0.11 | 0.5% | 22 name labels | label positions | real work | — |

### 2minnow

| System | ms | % main | Objects | Why | Status |
|---|---|---|---|---|---|
| Brains (BrainScheduler) | 1.05 (fs 1.46) | 5.7% | 50 Duplicant brains, 615 critter brains | `MinionBrain.UpdateBrain` 0.70: sensors 0.33 (UpdatePickups 0.18 main), UpdateChores 0.43 (FindNextChore 0.33) | `brain-rate`; real work |
| FetchManager.UpdatePickups | 0.18 main CPU, **wall 0.49** (0.6 a call); worker CPU **1.85** | 1.0% | 4,449 fetchables in 153 groups | as in U57: a full scan of every fetchable per Duplicant update | polling (see "What's left") |
| GlobalChoreProvider.UpdateFetches | 0.05 | 0.3% | 136 fetch chores, 14 worlds | | real work |
| UpdateNavigator + TransitionDriver | 0.39 + 0.61 (bucket 0.72) | 5.4% | 90 moving navigators (50 Duplicants on the move) | each tick, each moving Duplicant's path step and transition | real work: more Duplicants on the move than U57's 22 |
| Animation batches: `World.LateUpdate` | 1.48 (fs 1.57): UpdateDirty 1.11 (BatchSet 0.69, FastAnimBatches 0.40), Render 0.23 | 8.0% | 18,189 controllers; more of the colony is visible and animated per batch | per dirty batch: instance and symbol data; 2minnow's view is busier than U57's | `fast-anim-batches`, `fast-symbol-overrides`; real work |
| Animation updates: `Global.LateUpdate` | 0.79 (UpdateRegisteredAnims 0.73) | 4.3% | 1,204–1,685 registered | per playing animation in view | real work |
| Cell-change monitor | 0.78 | 4.2% | 146 moving controllers, 50 walking Duplicants, 176 conveyor items | handlers for cell changes (OnCellChange 0.43, object layers 0.23) | real work |
| Animation events | 0.37 (fs 0.47) | 2.0% | 2,607 | | `fast-anim-events` |
| PlantElementAbsorbers | 0.35 | 1.9% | | events | real work |
| `PropertyTextures.LateUpdate` | 0.45 (fs 0.64) | 2.4% | | `Texture2D.LoadRawTextureData` 0.30: the engine's upload path for the property textures, larger on Mesa | real work (engine upload) |
| KComponents Sim200ms | 0.27 (bucket) | 1.5% | | structure temperatures | real work |
| GasLiquidExposureMonitor | 0.20 (bucket) | 1.1% | 50 Duplicants | each Duplicant's exposure each 33 ms | real work |
| RequireInputs | 0.14 (bucket 0.19) | 0.8% | 541 a tick | | real work |
| ConduitFlow.Sim200ms | 0.17 | 0.9% | 2,166 + 1,969 conduits | | real work |
| EnergySim + CircuitManager | 0.14 + 0.10 | 1.3% | 88 generators | | real work |
| CritterTemperatureMonitor | 0.16 | 0.9% | 605 | | real work |
| SuitLocker ChargeSuit | 0.13 (bucket) | 0.7% | 2 a call, 157 µs each | | real work |
| Sublimates | 0.09 | 0.5% | 387 | | `fast-sublimation` |
| InstantReachability own cost | 0.08 (Forget 0.05) | 0.4% | 7,103 covered | as U57 | the same fix |
| Reachability polling | 0.002 | 0.0% | | | `instant-reachability` |
| SolidConduitFlow | 0.05 + 0.03 | 0.4% | 569 segments, 176 items | | real work |

## The engine's own costs

Main thread, perf, ms/frame:

| Entry | U57 | U57 % | 2minnow | 2minnow % | Oct 19 U57 | Notes |
|---|---|---|---|---|---|---|
| **Rendering** (FrameRenderer.RenderFrame) | **1.80** | 8.0% | **2.89** | 15.6% | 6.3 | |
| · world cameras: draw recording on the main thread (GlDrawRecorder.DrawQueue) | 0.73 | 3.2% | 1.31 | 7.1% | | the queues' recording goes to the 3 recorder threads (RecordAhead). This is what stays on the main thread: the hand-off, and draws outside the queues |
| · culling | 0.24 | 1.1% | 0.52 | 2.8% | | |
| · image effects (bloom, light buffer, FoW, colour correction) | 0.07 | 0.3% | 0.17 | 0.9% | | |
| · building the draw queues | 0.07 | 0.3% | 0.14 | 0.8% | | |
| · canvas overlays (HUD): batching 0.31 + recording 0.26 | 0.58 | 2.6% | 0.57 | 3.1% | | |
| **UI input** (StandaloneInputModule → GraphicRaycaster) | **1.29** | 5.7% | **1.08** | 5.8% | 0.7 | every frame: `CollectHits` over every registered graphic of the screen-space canvases (0.40 self), with `CanvasSystem.Unreachable` (0.76) asking whether each one sits under a closed screen. Answers are cached per object within one raycast, but reset at each raycast (`BeginRaycast`). The pointer did not move in these runs |
| **Canvas build** (CanvasSystem.Build → Collect) | 0.77 | 3.4% | 0.65 | 3.5% | 2.4 | the walk of visible UI; hidden screens are skipped (Oct: 2.1 → 0.8) |
| **Canvas update** (willRenderCanvases) | 0.53 | 2.4% | 0.66 | 3.6% | 0.47 | TMP `RescaleIfScaleChanged` 0.33 (every text, every frame; lossyScale cached since Oct), CanvasUpdateRegistry 0.15–0.28 (layout/graphic rebuilds) |
| Script dispatch, lifecycle (PhaseList.Run self, starts, awake/enable, destroy queue) | 0.45 | 2.0% | 0.34 | 1.8% | | 0.39 ms of U57's is awake/enable/destroy of spawned and removed objects |
| GetComponent and friends | under 0.1 | under 0.5% | under 0.1 | under 0.5% | 0.87 | component layouts (Oct) |
| Texture2D.LoadRawTextureData (property textures) | 0.13 | 0.6% | 0.30 | 1.6% | | |

Off the main thread (CPU, ms/frame):

| Thread | U57 | 2minnow | What |
|---|---|---|---|
| GL worker | 9.0 | 16.7 | U57: `GlDevice.RunDraw` 3.4, texture uploads 2.2 (`UploadChanged`: the animation batch textures and property textures the game re-applies), binds 0.9; 5.5 of it inside Mesa's gallium and 2.2 in NVIDIA's user-mode driver. 2minnow: uploads 5.7, draws 5.5, binds 1.6, mesh uploads 1.1 |
| Mesa's driver thread (gdrv0) | 10.0 | 13.7 | all native (gallium → nvwgf2umx → d3d12core): WSL's GL-over-D3D12 translation. Windows has none of this |
| Draw recording ×3 | 2.2 | 3.3 | RecordChunk/RecordAhead, uniform resolution (`GlUniforms.RecordAll` 0.61, `Resolve` 0.36), texture currency checks |
| Presenter | 1.1 | 1.2 | swap through GLX/Mesa |
| .NET thread pool | 0.9 | 3.4 | texture snapshot copies (memcpy: the animation textures copied on workers), fast-anim-batches bodies (0.29), spinning (1.2% of a core) |

GL thread busy per frame (frame stats): U57 15.95 ms, 2minnow 25.59 ms against a 27 ms frame.
That is why 2minnow waits 5.2 ms a frame for the device. The GPU itself ("gpu" phase) takes 11.1
and 19.4 ms.

## Off the main thread: the rest

| Work | U57 | 2minnow | Notes |
|---|---|---|---|
| **Path probe thread** | 19.4% of a core (4.8 ms/frame) | 21.3% (5.8) | `path-probe-repair`. U57: 20,501 probes, 15,673 repaired (41 µs each), 4,828 flooded (2,662 µs each; "moved or other abilities" 4,806). 2minnow: 25,227 probes, 12,185 repaired (49 µs), **13,042 flooded** (2,618 µs). Floods are 94% of the thread's time in 2minnow (Flood 5.15 of 5.46 ms), and most are after the Duplicant moved. The main thread hands out the work in 0.03–0.04 ms |
| **Job workers** (KFastWorker ×15) | 14.3 ms/frame CPU in all | 11.1 | U57: fetch pickups 3.79 (`CouldBePickedUpByMinion` 1.46, `GatherReachablePickups` 1.48, sort 0.81), conveyor arms 1.29, amounts 0.77, conduit networks 0.76, chore collection 0.42, spin-waiting 1.57. 2minnow: fetch 1.85, amounts 0.43, property textures 0.26, chore collection 0.14, spin 1.95 |
| **SimDLL** | 3.5 | 3.7 | UpdatePressure, SimBase.UpdateData, BuildingHeatExchange, temperature tasks; 2minnow also builds the liquid, flow and sunlight property textures |
| **FMOD** | 2.8 | 4.7 | the studio mixer threads |
| **GC** | Server GC 0.3 ms/frame of work; a gen0/gen1 pause of 32–449 ms every 30–60 s | BGC 0.6 + Server GC 0.2; pauses 14–161 ms | heap 8.7 GB (U57). The pauses are hitches, not average cost |
| **Tiered JIT** | 1.1% | 1.5% | still recompiling 3 minutes in (about 100–340 methods a 600-frame window) |

## Wall times of the methods the follow-up spikes need

perf samples CPU only, so a method that waits for job workers looks cheaper than it is. Two more
runs (`u57-wall`, `mn-wall`, the same setup, no frame stats) used PerfProbe's `walltime.txt`
stopwatches (Harmony prefix and postfix, main thread only, mean per 600-frame window). The table
gives the median over 7 steady windows, with the range in brackets.

**Caveats.**
- The U57 run was disturbed. Another agent's `ilspycmd` decompiles ran beside it. Two hot
  methods, patched for this run (`KBatchedAnimUpdater.LateUpdate`, `CellChangeMonitor.RenderEveryTick`),
  lose their profile-guided optimisation when patched (PerfProbe's WallTime notes this).
  Its windows ran 30–55 ms a frame against 24.8 in the perf run, so treat U57's wall times as
  1.2–1.5× high. Its cleanest window (29.8 ms) is given separately.
- The 2minnow run was clean: 25–28 ms a frame, the same as the perf run.
- Both attempts at each run overlapped: idle build-lock waits (no CPU), and in U57 also the
  decompiles. The tables use the second attempt. Another agent's player took the machine as soon
  as these runs released it, so there was no third attempt.
- `AnimEventManager.Update` counts 3 calls a frame because every overload named Update is
  patched and they nest, so its wall time is an upper bound.
- `JobManager.Run` reads near zero because `fast-jobs` replaces its body with a prefix. The jobs'
  wall time is in frame stats' `jobs` line instead.

| Method (main thread) | U57 wall, median (range) | U57, cleanest window | U57 perf CPU | 2minnow wall | 2minnow perf CPU |
|---|---|---|---|---|---|
| `FetchManager.UpdatePickups` (0.98 / 0.81 calls a frame) | 1.07 (0.86–1.47) | 0.86 | 0.36 | **0.49** (0.46–0.59) | 0.18 |
| · of which waiting for the workers (wall − CPU) | about 0.5–0.7 | 0.50 | | **about 0.3** | |
| · job CPU on the workers (perf) | | | 3.79 + 0.23 on main | | 1.85 + 0.10 on main |
| `GlobalChoreProvider.UpdateFetches` | 0.21 (0.16–0.22) | 0.16 | 0.11 | 0.08 (0.07–0.11) | 0.05 |
| `Sensors.UpdateSensors` (all of a Duplicant's sensors, fetch included) | 1.91 (1.57–2.42) | 1.57 | 0.78 | 0.71 (0.65–0.87) | 0.33 |
| `Brain.UpdateChores` (5.9 / 4.9 calls a frame) | 1.09 (0.99–1.34) | 0.99 | 0.46 | 0.60 (0.51–0.70) | 0.43 |
| `MinionBrain.UpdateBrain` (0.87 / 0.81 a frame) | 2.47 (2.15–3.07) | 2.15 | 1.10 | 1.18 (1.05–1.40) | 0.70 |
| `AnimEventManager.Update` (upper bound, nested overloads) | 0.80 (0.67–1.17) | 0.67 | 0.49 | 0.51 (0.44–0.61) | 0.37 |
| `KBatchedAnimUpdater.LateUpdate` (patched: PGO lost) | 2.57 (2.09–3.19) | 2.09 | 1.84 | 0.92 (0.81–1.03) | 0.79 |
| `KAnimBatchManager.UpdateDirty` (upload preparation; workers inside) | 0.61 (0.54–0.94) | 0.54 | 0.46 | **1.26** (1.09–1.41) | 1.11 |
| `KAnimBatchManager.Render` | 0.21 (0.18–0.26) | 0.18 | 0.12 | 0.29 (0.27–0.33) | 0.23 |
| `CellChangeMonitor.RenderEveryTick` (patched: PGO lost) | 2.04 (1.62–2.98) | 1.62 | 1.18 | 0.85 (0.72–0.97) | 0.78 |
| `Game.SimEveryTick` | 16.0 (11.3–26.1) | 11.3 | 8.25 | 5.78 (5.15–6.75) | 5.26 |
| `Game.UnsafeSim200ms` (0.56 / 0.40 a frame) | 5.11 | 3.71 | 2.60 | 1.39 | 1.12 |
| `Pathfinding.UpdateNavGrids` | 0.41 | 0.32 | 0.23 | 0.18 | 0.17 |
| `AsyncPathProber.Manager.TickFrame` | 0.05 | 0.04 | 0.03 | 0.04 | 0.04 |

**For the follow-up spikes:**
- **The fetch update costs more in wall time than perf shows.** In 2minnow it is about 0.6 ms
  per Duplicant update: 0.18 ms of main-thread CPU, the rest waiting for the workers, with
  1.9 ms of worker CPU behind it. In U57 it is about 0.9–1.1 ms per update, with 3.8 ms of
  worker CPU behind it. brain-rate makes it about one update a frame at these frame rates.
- **The rest of a Duplicant's update** (`MinionBrain.UpdateBrain` minus the fetch update:
  other sensors and chore choice) is about 1.3 ms (U57, cleanest window) and 0.7 ms (2minnow).
- **Animation uploads** (`KAnimBatchManager.UpdateDirty`) are the larger animation cost in
  2minnow (1.26 ms wall), and the updates are in U57.
- **Animation events** cost about 0.5 ms in both colonies.

## What's left

The biggest remaining costs, in order.

**U57** (main thread, 24.8 ms on Linux):
1. **State machine sim sub-ticks and the 200 ms step**: `Game.SimEveryTick` 8.25 ms. This is
   dozens of per-object systems, none over 0.6 ms: conveyors, pipes, power, requirement checks,
   critter monitors, navigators, transitions, ranching, sublimation, KComponents. It is real
   work per object. The data-layout spike of Oct 19 still describes the only way to go further:
   flat tables per system.
2. **Brains**: 1.5 ms (2.0 wall), plus 3.8 ms of fetch scans on the workers.
3. **Animation**: updates 1.84, batches 0.64, events 0.49. Real work per animation in view.
4. **Engine**: rendering 1.80, UI raycast 1.29, canvas build 0.77 and canvas update 0.53.
5. **Cell-change monitor**: 1.18 ms, driven by the 2,900 conveyor items and moving critters.

**2minnow** (27.2 ms on Linux): the GL thread is the limit (25.6 ms busy, mostly texture uploads
and draws through Mesa's D3D12 layer), so the main thread's 20.6 ms are not on the critical path
there. On the main thread, Game.SimEveryTick is 5.3 ms, rendering 2.9, animation batches 1.5,
the UI raycast 1.1 and brains 1.0.

**Polling patterns of the kind reachability had.** These are work redone on a timer or every
frame, whose answer changes only when something specific changes:

| Candidate | Cost | Pattern | Exactness |
|---|---|---|---|
| **`InstantReachability.Forget`'s list scan** | 0.42 ms (U57), 0.05 (2minnow) | not polling: an O(n) `List.IndexOf` over 27,711 entries per removal | trivially exact: keep the index in the entry. The cheapest find here |
| **Fetch pickups** (`FetchManager.UpdatePickups`) | main-thread wall about 0.9–1.1 ms a frame (U57) and 0.49 (2minnow), of it about 0.5 and 0.3 waiting; worker CPU 3.8 ms a frame (U57) and 1.9 (2minnow) | the closest match to reachability. Every Duplicant brain update re-tests every one of the 21,000 fetchables (`CouldBePickedUpByMinion`: reservations, tags, storage) and re-costs every reachable cell, though only about 200 tags and 25 pickupable cells change a frame (Oct 19 count) | an incremental per-Duplicant list (invalidated on tag, storage, reservation and cell changes and on the Duplicant's probe changing) gives the same set. The sort and dedup would need care to keep the game's order. Large. It mostly saves worker CPU, and saves main-thread time only where the job wait is on the critical path (machines with few cores) |
| **UI raycast** (engine) | 1.29 ms (U57), 1.08 (2minnow) | every frame, every registered graphic is rect-tested and its "under a closed screen" answer recomputed, while the pointer and the UI hold still | exact if the hit list is cached against the pointer position, the hierarchy's version (`Hierarchy.Touches`) and a version of the graphics' rects and raycast flags. Moving or animated UI must bump it. Engine-side, our own code |
| `RequireInputs.CheckRequirements` | 0.35–0.43 (U57) | every building each 200 ms re-reads its power and conduit connections, which change on (dis)connection events | event-driven is possible, but building checks are never delayed. An exact event version (update on connection change, keep status toggles in tick order) is a bigger job than its 0.4 ms |
| RanchStation FindRanachable | 0.21–0.24 (U57) | 62 stations re-scan their rooms' critters every 200 ms | could follow critter room changes; small |
| Door RefreshIsBlocked, Electrobank, ChoreDriver nochore/haschore | ≤0.07 each | per-object polls with rare changes | too small |
| Critter temperature, gas/liquid exposure, overcrowding | 0.1–0.3 each | inputs (cell temperature, gas) change continuously | not event-friendly; never delay temperature |
| Path probe floods (2minnow) | 5.2 ms a frame on the probe thread | 52% of probes flood after the Duplicant moved | off the main thread; repairing across a move would need the probe re-rooted. CPU only |

Not polling, and real work: animation updates, events and batches; the cell-change handlers;
conduit, power and conveyor flow; navigators and transitions; brains' chore choice.

## Comparison with Oct 19

The Oct 19 inventory was a different build (pre-tweak, before the engine changes since), on the
Linux of that time (WSL2, where PERFORMANCE.md notes the game's code ran about half as fast as on
Windows; now WSL 2.7.14, kernel 6.18.33.2), at 1024×768 rather than 1280×720. Same save and same
speed. Only the direction is comparable.

| | Oct 19 | Now | Why |
|---|---|---|---|
| U57 frame | 82 ms (12.2 fps) | 24.8 ms (40.4 fps) | the tweaks, the engine and runtime work since, and differences in the machine's state between the runs |
| Game code on the main thread | 58 ms (71%) | 14.4 ms (64%) | |
| Engine | 11 ms (13%) | 5.0 ms (22%) | the engine's share rose as the game's fell |
| Native (memcpy, maths, GC) | 6 ms | 0.6 ms leaf + 0.1 GC | texture snapshot copies moved to workers |
| Autosave in the window | 5–9 ms | off | `background-save` etc. (not measured here) |
| Game.Update | 41 | 9.8 | |
| Rendering | 6.3 | 1.8 | parallel draw recording, pooled records |
| Canvas build | 2.4 | 0.77 | hidden screens skipped |
| Animation updates | 3.2 | 1.84 | (and fewer registered: 3,569 vs ~4,400) |
| Cell-change monitor | 2.8 | 1.18 | |
| Brains | 2.7 | 1.5 (2.0 wall) | brain-rate, fast-offset-tables |
| Animation events | 2.5 | 0.49 | fast-anim-events |
| Reachability | 2.5 | 0.02 polling + 0.48 tweak (0.42 of it the list scan) | instant-reachability |
| Sublimation | 2.4 | 0.34 | fast-sublimation |
| Plant absorbers | 1.8 | 0.34 | |
| Animation batches | 1.7 | 0.46 + 0.12 | fast-anim-batches, fast-symbol-overrides |
| Power | 1.6 | 0.38 + 0.24 | |
| Conveyor sim | 1.5 | 0.52 | |
| Pipes | 1.5 | 0.41 | |
| RequireInputs | 1.4 | 0.35 | |
| Transitions | 1.2 | 0.60 | |
| Critter temperature | 1.1 | 0.28 | |
| Conveyor items moving | 1.1 | 0.26 | off-screen-conveyors |
| Radbolts | 0.9 | 0.07 | fast-particle-collision |
| Ranch stations | 0.9 | 0.24 | |
| UI input | 0.7 | 1.29 | no reduction: the raycast's cost is per graphic, and this run's pointer is over the world all the time (the closed-screen skip is in, but each graphic still has its rect test and closed-screen check) |
| GL thread | ~20% of a core (Oct) / 22 ms a frame busy (stopwatch run) | 36.5% (16 ms a frame) | more frames a second, so more upload and draw work a second |

Systems that fell more than the frame did (animation events, sublimation, reachability,
radbolts) are the ones a tweak targets. The rest fell with the build, the engine and the
machine's state, by roughly 3–5× each. So treat the per-system Oct figures as a ranking, not a
baseline to subtract from.

## Files

The captures (perf data, perf maps, schedstat snapshots, frame stats, screenshots) were kept
locally and not committed. The object counts came from a local PerfProbe census mode (live counts
of registered animations, conveyor items, conduits, brain groups and fetch groups), not committed.
