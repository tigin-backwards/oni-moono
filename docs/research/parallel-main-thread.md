# Main-thread work on other threads (2026-10-23)

Which of the main thread's work can be split across threads, or moved off the main thread,
without changing the game's results, judged against the per-system costs in
[gluttony-frame-inventory.md](gluttony-frame-inventory.md) and
[u57-frame-inventory.md](u57-frame-inventory.md). Those inventories call animation updates and
batch writes "not candidates" in the sense of skipping work; this note asks whether the same
work can run in parallel. The rules are the tweaks' rules: the same results by construction,
proved by a verify mode.

Two shapes are possible here:

- **Fork-join.** Klei's `GlobalJobManager.Run`: the main thread hands out items and waits, so
  only the time the items take together comes back.
- **Asynchronous.** `AsyncPathProber` runs on threads of its own and the main thread never waits
  for it.

Everything below is fork-join. Nothing found could run truly asynchronously, because the frame
needs each result before it goes on.

## Method

- **Mechanisms:** read from Klei's decompiled code (`KAnimBatch`, `KAnimBatchManager`,
  `BatchSet`, `KBatchedAnimController`, `KBatchedAnimInstanceData`, `SymbolInstanceGpuData`,
  `SymbolOverrideController`, `FaceGraph`, `NavGridUpdater`, `CellChangeMonitor`,
  `WorldInventory`) and from this engine's code for the engine calls they make.
- **Profiles:** `perf` on Linux as in the inventories (cpu-clock, 499 Hz, JIT symbols through
  perf maps), on Gluttony's cleaned save (`presync-clean.sav`) with every tweak: the inventory's
  own profile, and one with the prototype.
- **Timings:** each prototype's own stopwatch (`ONI_TWEAK_TIMING=1`): main-thread wall time of
  the patched entry point, averaged over 600-frame windows, the same build run serially
  (`ONI_FAST_ANIM_BATCHES_SERIAL=1`) and in parallel, back to back under the machine-wide perf
  lock. Other agents' players were running on the machine (Gluttony at 15–20 fps, against 24
  in the inventory), so only the steady windows (the third onwards) are compared.

## `KAnimBatch.UpdateDirty` (Gluttony 1.9 ms, U57 1.7 ms)

`KAnimBatchManager.UpdateDirty` walks the visible batch sets in list order; a dirty set walks
its batches; a batch with `needsWrite` runs `KAnimBatch.UpdateDirty`, which for each index in
its `dirtySet`, in order:
1. skips a null or destroyed controller;
2. **W**, the instance data: `KBatchedAnimInstanceData.WriteToTexture` reads the controller's
   frame index, frame count, visibility, post-processing parameters, first frame, position
   (`GetPositionData`, or a `KBatchedAnimTracker` delegate reading its parent's pivot symbol)
   and transform matrix (`GetTransformMatrix`: transform position, rotation, scale), stores
   them in the controller's own 112-byte record and copies it into the batch's data texture at
   the controller's index;
3. **S**, the symbol instance block: when the batch's slot for that index holds another block
   or version, copies the controller's `SymbolInstanceGpuData` into the batch's symbol texture
   and updates the slot;
4. **A**, `ApplySymbolOverrides`: re-applies the batch's atlases to its property block, and
   for a controller with a `SymbolOverrideController` re-applies its overrides when their
   version changed or every frame if asked (sorting, pooled dictionaries,
   `KAnimBatchManager.GetBatchGroupData`, which can insert, `FaceGraph.ApplyShape`,
   `SpeechMonitor.DrawMouth`, which adds an override and marks the controller dirty), then its
   own atlases; creates the batch's override texture on first need;
5. **O**, the override block: as S, for `SymbolOverrideInfoGpuData` into the override
   texture.

Then the batch's dirty set is cleared and its textures' `Apply` called.

### What it touches

| State | Who writes it | Shared between batches? |
|---|---|---|
| The batch's data, symbol and override textures, its slots, dirty set, `needsWrite` | its own update | no: one batch's |
| A controller's instance record and transform matrix | W of that controller | no: a controller is registered with one batch |
| `SymbolInstanceGpuData`, `SymbolOverrideInfoGpuData` | the controller's setters (A for overrides) | no: per controller |
| The batch's `MaterialPropertyBlock`, `AtlasList` | A | no, but A is game code |
| `KAnimBatchTextureCache` (static), `KAnimBatchManager.batchGroupData` | `Init`, override texture creation, A | **yes** |
| `DictionaryPool`/`ListPool` of `SymbolOverrideController` (static) | A | **yes** |
| Transforms' cached world matrices and rotations | read lazily by W (a tracker reads its parent's) | **yes**: a parent can be read from two batches |
| `BatchSet.dirty` | A (through `SetDirty`) and the set's walk | per set |

No static scratch buffer is used by W, S or O; `KBatchGroupData` (frame elements, symbol
counts) is only read by them.

### Is the order observable?

Not between batches for W, S and O: each writes its own batch's memory, reading state that no
other batch's update changes. A is the exception: it is game code with shared pools and
dictionaries, and the order of its calls is the order of the events it raises. So W and S can
run for every batch before the loop, and O after it, if A of one controller never changes what
W, S or O of another reads. It does not: A writes only its own controller's override state,
its batch's atlases and property block, the shared pools (not read by W, S, O), and the
controller's dirty flag (already set). Within one controller, W and S come before A in the game,
and stay before it; O comes after A, and stays after it.

### The engine calls

- `Texture2D.GetRawTextureData<T>`: a view of the texture's memory. It first waits for the GL
  device's snapshot of that texture, if one is still copying.
- `NativeArray.Copy`: a span copy.
- `Texture2D.Apply`: bumps the content version. There are no mip levels to rebuild here, and
  nothing reads the memory until the frame is drawn.
- `Transform.position`/`rotation`/`localScale`.
- `Component.transform`: a cached reference.
- Unity's null test: a volatile read.

None asserts the main thread. All are per-object except the transform caches: a world matrix or
rotation is computed on first read after a change and kept with a dirty flag. Two threads
reading the same transform both compute the same value. The flag is now written with release
and read with acquire semantics (plain moves on x64), so a reader that finds it clear also sees
the stored value (`TransformConcurrentReadTests`).

### Prototype: `fast-anim-batches`

- A prefix on `KAnimBatchManager.UpdateDirty` (last of the prefixes, only if the original runs)
  collects the dirty batches the walk will reach and, when they hold at least 128 dirty
  controllers, runs W and S for each on `Parallel.For`, one batch per work item, recording per
  index that it was written.
- A transpiler on `KAnimBatch.UpdateDirty` routes its W, S and O calls through the tweak: W and
  S return at once for an index written ahead (S with the game's answer), O makes the game's
  slot test and, when the block is stale, returns "written" and queues the copy.
- A postfix makes the queued O copies on workers, one batch per item, before anything else runs.
- Left to the game's loop: UI batches (`GetTransformMatrix` reads canvas layout and writes
  fields), batches being set up, controllers not exactly `KBatchedAnimController`, position
  delegates other than the tracker's, and every frame when another Harmony owner patches a
  method on the worker path.
- Verify mode (`ONI_TWEAK_VERIFY=1`): at each skipped call's original place, W and S are redone
  into scratch memory and compared byte for byte with the worker's; S's slot must match the
  controller's version; a controller written ahead that the loop never reached is a mismatch;
  each deferred O block must have kept the version it had when the loop reached it.

| Gluttony, Linux, stopwatch | Serial | Parallel |
|---|---|---|
| `KAnimBatchManager.UpdateDirty`, steady windows (ms a frame) | 1.92–2.01 | 1.49–1.65 |
| of which the workers' phases (wall) | | 0.55–0.61 |
| Frames run in parallel | | 94% |

Verified: Gluttony, 3.2 million writes ahead and 55,000 deferred override blocks, 6.4 million
checks, 0 mismatches; U57, 5,400 frames (96% in parallel): 4.3 million writes ahead, 16,000
deferred override blocks, 8.6 million checks, 0 mismatches.

Why it saves only about a fifth: in the profile with the tweak, what stays on the main thread
is A, 1.0 ms (`SymbolOverrideController.ApplyOverrides` 0.79, of which
`fast-symbol-overrides`' value comparison 0.42, `FaceGraph.ApplyShape` 0.13), the atlas
re-applies 0.24, and the loop's own visits to each controller. The workers' 0.6 ms wall time
did what took the main thread about 1 ms; the rest of the frame-time win is lost to the fork
and join and to the main thread then reading the same controllers again, from the shared cache
instead of memory. A is game logic with shared pools and events, so it stays.

## Queued draw recording (engine; Gluttony 4.1 ms, U57 2.4 ms)

`FrameRenderer` sorts a camera's opaque and transparent draws into a queue and hands each
queue to the device; overlay canvases hand each batch list the same way. The GL calls already
run on the device's own thread; what the main thread did for each draw, in order, was record
it (`GlDrawRecorder`, formerly inside `GlDevice.Draw`): prepare the mesh (an upload posted
when it changed), pick the material's passes, select the variant (cached per material, pass and
keyword list), find the program (compiled through `worker.Call` on first use), parse the
pass's state, resolve the material's and globals' values for the program (cached per material
and program, again whenever either version changes), copy them and resolve the draw's own
values over them, resolve the pass state, prepare each sampler's texture (an upload or sampler
change posted when it changed) and post the draw.

### When the inputs can change

| Input | Changed by | Between a queue's first draw and its last? |
|---|---|---|
| Materials (values, keywords, shader), property blocks | game code | no: no game code runs inside a queue |
| Shader globals and global keywords | game code, command buffers (`SetGlobalTexture`…) | no: command buffers run at camera events, between queues |
| Transforms | game code | no: the draw items carry matrices taken at culling |
| Meshes and textures (content, sampler settings, render texture shape) | game code, `CanvasSystem` batching (before the overlay queue is handed over) | no |
| The camera constants | `SetCamera`, between queues | no |
| The device's caches | the recording itself | yes: this is what must be made safe |

The sync points are the places game code or the device's other users run: camera callbacks
and image effects (around the queues), command buffers at camera events (between the opaque
and transparent queues), `Graphics.Draw*`/`GL` immediate draws (outside queues: recorded
serially as before), and captures (which render the screen again, queue by queue). A
queue is recorded completely, both phases below, before `DrawQueue` returns, so every sync
point sees the serial recording's state.

The earlier ruling against parallel recording ([u57-frame-inventory.md](u57-frame-inventory.md))
was about the game changing materials, property blocks and globals between draws. That holds
for immediate draws and for command buffers, which stay serial; between the draws of a queue
nothing changes them, so no per-draw copy of the inputs is needed.

### What moved, and why it is exact

1. **Workers** (the main thread and up to three helper threads of the recorder's own,
   `ForkJoin`; chunks of at least 24 draws, queues of at least 96) record each
   draw into the frame's arena, a child arena per chunk (`FrameArena.Child`, reset with the
   frame): everything but what would add a cache entry or compile. The material's values,
   found out of date (the game sets globals every frame, so almost every entry is), are
   resolved again by the first thread to need them, under the entry's lock, and published
   with release semantics (`GlUniforms.SharedValuesOf`); the entry is a pure function of the
   material, the globals and the program, all fixed during the queue, and the draw's own values
   are resolved over every slot that depends on the draw. Cached pass states are immutable
   objects replaced whole (`ResolvedFor`). `GlUniforms`' scratch is per thread. A mesh or
   texture whose device side is out of date is left pending.
2. **The main thread walks the queue in order:** a draw left out entirely (no values entry
   yet for its material and program, no variant cached, no program, a UsePass, an exception) is
   recorded by the serial code; a draw recorded ahead has its pending mesh and textures
   prepared exactly as the serial code prepares them (mesh first, then each pass's samplers in
   order, each pass posted after its own), so uploads land between the same draws; then its
   passes are posted. A program compiled during the walk hands the frame over; the draws after
   it are then recorded serially into the next frame (their data lives in the frame handed
   over).

So the commands, their order and their data are the serial loop's: every cache a worker reads
was current, or made current from inputs nothing changes; everything that posts work happens
on the main thread at the serial place. About 95% of queued draws are recorded ahead (the rest
are in queues under 96 draws); in the steady state 0–2 draws a frame are left out.

### Verify mode and captures

`ONI_VERIFY_PARALLEL_DRAWS=1`: after each draw recorded ahead is posted, the serial code
records it again into scratch, without posting; the program, state, every value and block
byte (bitwise), every texture, the mesh and its layout must be equal, and the serial recording
must post nothing. `scripts/qa.sh`'s verify stage runs it. DeCrew's overlays scenario (2,420
frames): 3.3 million draws verified, 0 wrong. The unit tests compare whole command streams of
a serial and a parallel recorder from a partly prepared state (changed textures, mesh,
material, keywords and globals), and fail when the pending preparation, the mesh preparation or
the values refresh is left out.

Captures: run to run, the colony's frames are not reproducible (two serial runs of the overlays
scenario differ in 70,000–365,000 of 786,000 pixels), so captures are compared within a run:
`ONI_CAPTURE_TWICE=serial` renders each super-sized capture's frame again with every queue
recorded serially. The overlays scenario's 14 captures (2048×1536): all identical; the control
(`ONI_CAPTURE_TWICE=parallel`) is too.

### Timings

Stopwatch counters (`ONI_RENDER_TIMING=1`: main-thread wall time of `FrameRenderer.RenderFrame`
and of its draw queues, 600-frame windows), every tweak, Linux, 1280×720, under the exclusive
machine lock. `ONI_PARALLEL_DRAWS=alternate` switches between serial and parallel recording
every window, so both are measured in one run; the first window (the load) is left out. (This
first version ran the workers on `Parallel.For`; see below for why that changed, and for the
whole-run numbers of the version kept.)

| Main-thread ms a frame | Gluttony serial | Gluttony parallel | U57 serial | U57 parallel |
|---|---|---|---|---|
| Draw queues | 3.86–4.24 (4.07) | 1.58–1.77 (1.72) | 2.38–2.63 (2.44) | 1.10–1.16 (1.13) |
| · of which the parallel part (wall) | | 1.09 | | 0.83 |
| · of which the walk | | 0.41 | | 0.18 |
| Rendering in all | 5.21–5.77 (5.49) | 3.25–3.86 (3.65) | 3.20–3.49 (3.29) | 1.88–2.01 (1.94) |
| Frame rate, same windows | 20.1–21.9 (21.0) | 21.0–23.6 (21.9) | 22.2–38.2 (34.4; 36.8 without one slow window) | 35.8–42.7 (39.7) |

Gluttony (`presync-clean.sav`, 1,830 draws a frame): 1.8 ms less rendering a frame; U57
(1,520 draws): 1.35 ms. More than the audit's estimate, because the values refresh moved too:
it runs for nearly every draw every frame.

### Not the thread pool: a busy machine

QA's perf stage then found it slower over whole runs: U57, 4,800 frames, 37.6 fps off and 34.6
on, windows falling from 37.6 to 25.1 as the run went on. Alternating windows hide what
happens across a run, so it was reproduced with whole runs, with thread-pool, thread, GC and
CPU counters added to the timing lines. On an idle machine those stayed flat and the
recording kept its gain. With other work on 12 of the 16 cores (busy loops, standing in for
the other players QA's machine runs between its locked stages) it lost: 14.3 fps on against
17.5 off, though its draw queues were only about 0.5 ms slower. The loss was elsewhere in the
frame: after each `Parallel.For` (several a frame) the pool's workers, up to one a core, spin
looking for more work, and on a busy machine that spinning takes the cores of the GL thread
and of the game's job threads. Pool threads, GC counts and the heap were the same either
way, so neither pool growth nor the child arenas were the cause.

The recording now runs on `ForkJoin`: the main thread and a quarter of the cores' worth of
helper threads (at most three) of its own, which sleep in the kernel between queues and never
spin. The main thread claims chunks too, so a helper that wakes late finds nothing left; the
main thread waits only for chunks a helper has claimed (0.2 ms a frame, U57). Results:

| U57, `scripts/qa.sh perf` (4,800 frames, every tweak) | Off | On |
|---|---|---|
| Idle machine, back to back (off, on, on, off) | 38.89, 38.40 fps | 41.85, 43.39 fps |
| · steady windows, render timing | 37.0–40.6 fps, rendering 3.0–3.2 ms | 39.6–44.9 fps, rendering 1.9–2.0 ms |
| 12 of 16 cores busy | 17.53 fps (draw queues 3.3–3.5 ms) | 19.21 fps (2.0–2.2 ms); with `Parallel.For` 14.32 |

No window falls as the run goes on. `fast-anim-batches` still runs on `Parallel.For` and may
show the same effect on a busy machine.

## Other candidates

Costs are the inventories' Linux main-thread ms a frame (Gluttony unless marked).

| Work | ms | What blocks it | Verdict |
|---|---|---|---|
| Animation batch writes (above) | 1.9 | A is game code | **done**: `fast-anim-batches`, about 0.4 ms |
| `NavGrid.UpdateGraph`, the links phase | 0.5 of 0.87 | nothing: per-cell, disjoint writes, reads only the nav table already updated; the game runs the same code on its job threads for full rebuilds | **tried, dropped**: prototyped exactly (links computed on workers, written in the game's order with its overflow behaviour; 20 million cells verified identical). About 19 updates a frame of about 40 µs each; the fork and join cost more than they saved (0.55–0.63 serial, 0.54–0.61 with only updates of 600 cells or more in parallel, worse below that) |
| Queued draw recording (above) | 4.1 (U57 2.4) | the recorder's caches written on first use, uploads posted by preparation, the per-frame arena | **done, engine:** draws recorded on workers, preparation and posting in order; Gluttony 1.8 ms, U57 1.35 ms. Overlay canvases' batches go through the same path |
| `WorldInventory.Update` | 0.53 | none: per item `GetMyWorldId`, a tag test and `TotalAmount` are reads | **candidate (a):** read each item's amount on workers, sum on the main thread in the set's order (float sums keep their order). ~0.3 ms; not prototyped |
| Reachability checks (`ReachabilityMonitor`) | 0.62 | each changed result triggers its state machine | **candidate (a):** compute `IsAllReachable` on workers, apply changes in order (the prober's grid is already read while its own threads write it). ~0.4 ms; not prototyped |
| `fast-sublimation`'s batch | 0.46 (U57 ~1.0) | full updates of earlier items in the batch | **to study:** the skip decisions read the item, its cell's mass from the sim's last step and its chunk temperature, which earlier full updates do not change within a batch; decisions on workers, updates in order |
| `KBatchedAnimController.UpdateAnim` | 1.59 | almost every branch has effects: batch dirty lists shared by a batch's controllers, re-registration on chunk change, `SuspendUpdates` editing the updater's lists mid-walk, synchronizers driving other controllers, events | no |
| `CellChangeMonitor.RenderEveryTick` | 1.42 | handlers (1.0 of it) run in order and move other transforms and add handlers before their entries are reached; only the cell computation (0.4) is pure, and revalidating it reads the same transforms | no |
| Animation events (`fast-anim-events` loop) | 0.72 | events change later entries and the entry list | no |
| Brains: chore search, sensors | 2.4 | preconditions with caches and reservations, order-dependent | no (fetch updates already run on the job threads) |
| `RequireInputs.CheckRequirements` | 0.83 | status-item toggles and events per building; the check would need reimplementing to split reads from writes | not worth it (≤0.4) |
| Critter temperature | 0.38 | transitions in order | small (≤0.2) |
| Canvas update and build | 1.56 | layout and graphic rebuild callbacks in order; TextMeshPro adds glyphs to shared font atlases | no |
| Input module | 0.54 | raycast filters are game code; sorting needs the same input order | small |
| `PropertyTextures.LateUpdate` copies | 0.3 | `LoadRawTextureData` must copy before returning (the caller may reuse its buffer); only splitting the copy across threads, which is memory-bound | not worth it |
| Sim sub-ticks (386 systems) | 13 | shared game state, order of callbacks observable | no |
| Instantiate, destroy, behaviour dispatch | ~1.5 | order of object creation and callbacks | no |

## What's left

Queued draw recording, the one large structural candidate, is done (1.35–1.8 ms). Of the
game-side ones, the reads in `WorldInventory` and
reachability can be split from their effects exactly, for a few tenths of a millisecond each.
The rest of the main thread is game logic whose order is part of its results.
