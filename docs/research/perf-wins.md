# Perf wins for Moono on U60: baseline and candidates (2026-11-02)

This is the first phase of the "Perf wins for Moono on U60" goal. It measures where our
player's main thread spends its frame on U60 (761211), on four saves, and ranks the changes that
would shorten it by estimated main-thread saving. The baseline is what every later change is
measured against.

Every number below is measured here (Windows, RTX 3090, driver 610.88), read from our code or
the game's, or an estimate that says how it was derived. Claims not checked say "unverified".

## Method

- **Player**: ours, the `u60` branch at `3ac5d19f`, plus PerfProbe's `busiest` camera mode
  (`a0c41eb4`, this branch). Built with `scripts/build-windows.sh release` and run on its
  defaults: Vulkan and the default game tweaks.
- **Game**: the frozen U60 761211 Windows install (`E:\oni-builds\oni-win-761211`), with the
  all-DLC Steam double.
- **Saves**, copied into each run home (the originals are untouched):
  - **DeCrew**: `DeCrew 527.sav`, the same file as `tests/e2e/fixtures/decrew.sav`.
  - **soggy lab 2**: the save from issue #48's reporter, with 35 of the reporter's 39 mods in
    their order. Four mods fail to load on U60 (the game's mod-error dialog then quits after
    3 frames), so they are left out: Fast Track 0.18.6, Automatic Geyser Calculation,
    Rocketry Expanded 2.15.0 and Better Info Cards. The other 35 passed `scripts/mod-gate.py`
    against 761211's onimaxxing data. One of the 35 still logs `MissingMethodException`
    (`Cmps<T>.GetEnumerator`) when habitat modules spawn; the colony runs.
  - **c100**: `c100.sav`, 200 Duplicants.
  - **U57**: `U57.sav`.
- **Run settings**:
  - PerfProbe at speed 3, with `awake.txt` and `camera.txt busiest`: the world with the most
    live Duplicants is made active, the camera stays where the game puts it, and a census of
    the save is logged.
  - 1280x720 windowed on monitor 2, `--no-focus`, High priority with power throttling off
    (`win-boost.ps1`).
  - Sound off: the run home's `Volume_*` prefs at 0, and `--audio-wav`.
  - `--frame-stats` on.
- **Taking turns**: each run waited until no other game ran on the machine and no other
  agent's Windows runner was waiting. Other agents' Linux players ran at the same time (load
  average 1.2-7.4), and the frame rate moved a lot between windows: DeCrew's ran 46-97 fps.
  The same save also measured differently in its two runs: U57 was 57.7 fps in its baseline
  run and 81.7 fps during its trace. Read the frame rates as roughly ±30%. The shares within
  one trace are consistent and are the basis of the ranking.
- **Measurement windows**: PerfProbe's 600-frame windows that start at least 150 s after the
  busiest world became active, for 120 s. In the first one to two minutes after a load the
  frame rate is a third to a half of the steady rate (JIT, first draws of each animation), so
  those windows are left out.
- **Traces**:
  - A separate run per save, traced 150 s after the busiest world became active, for 30 s.
  - `dotnet-trace` with `Microsoft-DotNETCore-SampleProfiler` and
    `Microsoft-Windows-DotNETRuntime:0x4019:5`: CPU samples, GC allocation ticks and
    contention events.
  - Samples are attributed to the main thread (the thread under `Game.Update`). A share is
    turned into milliseconds with that trace's own main-thread frame time (the sum of the
    frame phases in its windows): DeCrew 16.0, c100 28.4, U57 12.2, soggy 28.7 ms.
- **Sampling artifacts**:
  - As ARCHITECTURE section 7 says, the sampler stops threads at GC safe points. 35-54% of
    main-thread samples end in `Monitor.Enter_Slowpath`, the GC poll or `Singleton<T>.Instance`.
  - The analyzer charges those samples to the first real frame above them.
  - Methods that take an uncontended lock often look heavier than they are. These include
    `KBatchedAnimController.ValidateRegistration` (through `Singleton<T>.Instance`), at
    2.4-4.0% here, and FMOD's `StringHelper`. They are not used as evidence.
  - Real waits come from the contention events: on the main thread, 3.8 ms (DeCrew) to
    26.6 ms (c100) in 30 s, at most 0.03 ms a frame.

## Baseline (U60 761211, ours on Vulkan, speed 3)

Per frame, means over the windows. The main thread is the sum of the engine's frame phases
(`--frame-stats`). The device thread is the Vulkan thread's busy time. The scripts are the
engine's per-script times.

| | DeCrew | soggy lab 2 (35 mods) | c100 | U57 |
|---|---|---|---|---|
| Busiest world | 0, 15 Duplicants | 2 (Ickalin), 4 of 6 | 0 (Drenchazon), 200 of 204 | 0 (Phlegmoni), 21 of 22 |
| Frame rate | 64.6 fps | 32.5 | 44.5 | 57.7 |
| Windows (fps) | 46-97 | 28-36 | 40-51 | 46-80 |
| Slowest 1% of frames | 82.1 ms | 57.1 | 60.1 | 48.9 |
| Main thread | 15.4 ms | 30.7 | 22.3 | 17.2 |
| · update | 7.04 | 20.49 | 9.68 | 8.65 |
| · late update | 4.32 | 5.48 | 6.18 | 5.81 |
| · post-late | 0.72 | 1.10 | 1.07 | 0.94 |
| · render | 3.16 | 3.45 | 5.15 | 1.70 |
| GPU | 1.45 ms | 2.35 | 3.16 | 2.28 |
| Device thread busy | 4.87 ms | 5.16 | 7.90 | 2.37 |
| · draws | 3.39 | 2.67 | 2.13 | 1.48 |
| · texture uploads | 0.94 | 1.84 | 4.98 | 0.48 |
| Draws, pipeline binds | 2,399, 243 | 2,091, 317 | 1,577, 360 | 1,549, 306 |
| `Game.Update` | 4.60 ms | 18.60 | 7.55 | 6.29 |
| `Game.LateUpdate` | 1.96 | 1.89 | 1.81 | 2.95 |
| `World.LateUpdate` | 0.86 | 0.65 | 3.00 | 0.83 |
| `Global.LateUpdate` | 0.74 | 1.30 | 0.61 | 1.16 |
| `Global.Update` | 0.30 | 0.48 | 0.41 | 0.43 |
| `PropertyTextures.LateUpdate` | 0.27 | 1.19 | 0.45 | 0.18 |
| GC pauses | 3.4 ms/s | 0.3 | 1.3 | 1.9 |
| Allocation (trace) | 64 MiB/s | 26 | 36 | 85 |
| Managed heap (committed) | 10,619 (11,886) MiB | 7,950 (8,732) | 9,362 (10,072) | 11,048 (12,211) |
| Working set, private | 16,504, 18,325 MiB | 13,366, 16,582 | 11,004, 16,714 | 16,725, 18,662 |
| Video memory (local, non-local) | 3,939, 479 MiB | 4,813, 473 | 4,405, 472 | 4,144, 473 |

Census at the busiest world:

| Save | Game objects | Animation controllers | Pickupables | Buildings | Active mesh renderers |
|---|---|---|---|---|---|
| DeCrew | 153,670 | 97,871 | 7,941 | 103,115 | 7 |
| soggy lab 2 | 85,392 | 26,871 | 9,929 | 15,993 | 1,371 (1,364 `Mask`) |
| c100 | 78,177 | 21,456 | 7,163 | 13,059 | 165 (158 `Mask`) |
| U57 | 158,601 | 84,752 | 27,265 | 61,754 | 8 |

The main thread bounds the frame on every save. The device thread is busy at most 36% of the
main thread's frame (c100: 7.9 of 22.3 ms), and the GPU at most 3.2 ms.

## Where the main thread goes (traces)

Shares of the main thread's samples, with safe-point artifacts charged to the caller.

| | DeCrew | soggy | c100 | U57 |
|---|---|---|---|---|
| Game code | 50.7% | 45.0 | 57.1 | 54.8 |
| Our engine (`OniMoono.*`, facades) | 37.5 | 24.9 | 29.8 | 29.9 |
| Our game tweaks | 5.8 | 4.2 | 5.0 | 5.2 |
| Patched game methods | 0.8 | 2.3 | 0.6 | 1.7 |
| Native sim (`Game.StepTheSim` and other SimDLL calls) | 2.5 | 20.3 | 2.2 | 2.2 |
| Waiting (job joins, device sync) | 2.1 | 2.3 | 3.4 | 4.4 |
| Mods (own frames) | 0 | <0.1 | 0 | 0 |
| FMOD, Harmony, other | 0.6 | 0.8 | 1.9 | 1.8 |

By the script the engine calls (the first frame below our engine):

| Entry | DeCrew | soggy | c100 | U57 |
|---|---|---|---|---|
| `Game.Update` | 32.3% | 58.1 | 39.3 | 33.0 |
| Engine only (rendering, UI, frame loop) | 33.6 | 18.9 | 26.1 | 26.3 |
| `Game.LateUpdate` | 12.9 | 6.3 | 7.3 | 18.7 |
| `World.LateUpdate` | 5.7 | 1.9 | 14.3 | 5.2 |
| `Global.LateUpdate` | 4.8 | 4.0 | 3.5 | 6.3 |
| `Global.Update` | 2.2 | 1.6 | 2.6 | 2.9 |
| `PropertyTextures.LateUpdate` | 1.7 | 4.0 | 1.7 | 1.2 |

Inside `Game.Update`, `StateMachineUpdater.AdvanceOneSimSubTick` takes 16-31% of the main
thread and `Game.UnsafeSim200ms` 5-23%. On soggy lab 2, `Game.StepTheSim` alone is 19.2% of
the main thread (about 5.5 ms a frame): the native simulation step of its 15 worlds, run on
the main thread.

Engine pieces, as shares of the main thread (and ms a frame):

| Engine piece | DeCrew | soggy | c100 | U57 |
|---|---|---|---|---|
| Draw recording on the main thread (`GlDrawRecorder.DrawQueue`) | 12.4% (1.99) | 5.3 (1.52) | 10.4 (2.96) | 6.4 (0.78) |
| · `RecordAhead` | 7.3 | 3.0 | 3.1 | 2.8 |
| · `ForkJoin.For` (waiting for the helpers) | 1.2 | 0.6 | 2.0 | 0.8 |
| · `UploadBuffers.Rent` | 0.9 | 0.2 | 1.3 | 0.4 |
| `FrameRenderer.Cull`, its own frame | 3.5 (0.56) | 3.0 (0.86) | 2.1 (0.60) | 1.9 (0.23) |
| UI canvases (`CanvasSystem.RenderOverlays`) | 3.1 (0.50) | 2.5 (0.72) | 3.1 (0.88) | 2.7 (0.33) |
| `Texture2D.LoadRawTextureData` (property textures) | 0.7 (0.11) | 2.9 (0.83) | 1.1 (0.31) | 0.6 (0.07) |

Allocation hot spots, from the trace's allocation ticks:

| Allocated in | DeCrew | soggy | c100 | U57 |
|---|---|---|---|---|
| `SolidConduitFlowVisualizer+ConduitFlowMesh.End` | 26.8 MiB/s | 2.2 | 0.2 | 37.5 |
| `ConduitFlowVisualizer+ConduitFlowMesh.End` | 10.8 | 7.9 | 8.5 | 5.8 |
| `Mesh.Slice` (other callers) | 3.0 | 0.6 | 3.0 | 2.9 |
| `CanvasSystem.BuildMesh` | 0.7 | 0.6 | 5.7 | 3.4 |
| `PropertySheet..ctor` | 1.8 | 0.7 | 0.5 | 2.3 |
| `VkDevice.UploadChanged` | – | 4.3 | – | – |
| `RendererDraws.CollectMesh` | 0.2 | 0.1 | 0.1 | 0.3 |

`ConduitFlowMesh.End` (both visualizers, decompiled from 761211) only calls `mesh.Clear()`,
`SetVertices`, `SetUVs`, `SetColors` and `SetTriangles` on lists, then `Graphics.DrawMesh`. The
arrays it is charged with (`Vector4[]`, `Vector3[]`, `Int32[]`, `Color32[]`) are our mesh
setters' channel copies (`Mesh.Slice`), inlined into it.

## Candidates, ranked by estimated main-thread saving

Each estimate is an upper bound taken from the measured share. None of these has been
prototyped yet.

### 1. Symbol overrides re-resolved on every change (game-side). Up to about 1.7 ms on c100.

- **What**:
  - `SymbolOverrideController.ApplyOverrides` rebuilds its resolved override list from scratch
    each time a controller's batch is dirty. Each rebuild sorts, allocates two pooled
    dictionaries and a pooled list, and does atlas lookups and `SetSymbolOverrides` per
    symbol.
  - It then calls `FaceGraph.ApplyShape`.
  - It is called from `KAnimBatch.UpdateDirty` (a patched method), through
    `KBatchedAnimController.ApplySymbolOverrides`, in `World.LateUpdate`.
- **Evidence**:
  - c100 (200 Duplicants): 7.2% of the main thread (about 2.0 ms) inclusive, 6.1% in its own
    frame. `KAnimBatch.WriteSymbolOverrideInfoTex` adds 4.0% more (the main thread's share of
    `fast-anim-batches`' parallel writes).
  - U57: 2.5% (0.30 ms). DeCrew: 1.0% (0.16 ms). soggy: 0.3%.
  - It scales with Duplicants: faces and outfits are symbol overrides.
- **Exact?**: not yet known.
  - One approach: keep the resolved list while `symbolOverrides` is unchanged, and redo only
    the face shape and the atlas indices. That can be exact only if nothing else feeds the
    result.
  - That needs reading `SetSymbolOverrides`, `FaceGraph` and the batch's atlas list (not done).
- **Estimate**: at most the 6.1% own share on c100 (about 1.7 ms). How often the face is the
  only change is unmeasured; a stopwatch count of calls by cause comes first.
- **Verdict: landed** (`fast-symbol-overrides`, exact; see [Verdicts](#verdicts)).

### 2. Draw collection: the `Graphics.DrawMesh` queue in `Cull`. Up to about 0.5-0.9 ms.

- **What**:
  - `FrameRenderer.Cull` (`src/OniMoono.UnityEngine/Engine/Rendering/FrameRenderer.cs`)
    iterates the queued meshes with `foreach (var q in afterFrame ? frameQueued.Concat(queued)
    : queued)`.
  - The expression's type is `IEnumerable<QueuedMesh>`, so even the plain `queued` list goes
    through a boxed `List<T>.Enumerator`.
  - `QueuedMesh` is a struct of about 110 bytes with four references.
- **Evidence**:
  - `Cull`'s own frame is 1.9-3.5% of the main thread, almost all ending in
    `Buffer.BulkMoveWithWriteBarrier`, a copy of GC references. On DeCrew that is 3.5%,
    0.56 ms.
  - On DeCrew only 7 mesh renderers are active, so the renderer loop is not where this goes.
  - The animation batches draw through `Graphics.DrawMesh`: `Klei/BatchedAnimation` alone was
    about 580 of 1,500 draws a frame on U57 (vulkan-draw-binding.md).
  - That the copy comes from the enumerator is **unverified**: the stacks put
    `BulkMoveWithWriteBarrier` directly under `Cull`, with no enumerator frame between.
- **Exact**: yes. The fix iterates the two lists by index in the same order.
- **Estimate**: up to `Cull`'s own share: DeCrew 0.56, soggy 0.86 (part of that is soggy's
  1,371 `Mask` renderers), c100 0.60, U57 0.23 ms. Check first with a stopwatch around the
  queued loop.
- **Overlap**: the culling-registry branch (`rc-cull-u60`) edits the same method and keeps
  this loop unchanged. Land this after it, or on top of it.

### 3. Property-texture uploads: the copy in `LoadRawTextureData`. Up to 0.8 ms on soggy.

- **What**:
  - `PropertyTextures.LateUpdate` hands each sim property texture to
    `Texture2D.LoadRawTextureData`.
  - Our `LoadRaw` (`src/OniMoono.UnityEngine/Rendering/Texture.cs`) copies the whole slice
    into the texture's memory, and `Apply` then marks it for upload.
- **Evidence**: `LoadRawTextureData` takes soggy 2.9% (0.83 ms; 15 worlds), c100 1.1%
  (0.31), DeCrew 0.7% (0.11) and U57 0.6% (0.07) of the main thread.
  `PropertyTextures.LateUpdate` is 1.19 ms on soggy (script time).
- **Exact**: yes for either of these:
  - skip the copy and the upload when the bytes are unchanged (gluttony inventory item 7);
  - copy only the region the game's `TextureRegion` wrote.

  Both keep `LoadRawTextureData`'s copy-before-return contract
  (docs/research/parallel-main-thread.md).
- **Estimate**: at most the copy's share. A compare still reads both buffers, so less than
  that unless only the changed region is copied (estimate; the changed fraction is
  unmeasured).

### 4. UI canvas batching. Up to 0.3-0.9 ms.

- **What**: `CanvasSystem.RenderOverlays`: building and batching the screen-space canvases
  (`Build`, `Batched`, `BuildMesh`), then recording their draws.
- **Evidence**: 2.5-3.1% of the main thread (0.33-0.88 ms); `Batched`'s own frame is
  0.9-1.5%. On c100, `BuildMesh` allocates 5.7 MiB/s.
- **Exact**: no specific change has been identified; not examined yet.
- **Estimate**: unknown below the measured share. Listed so the next phase looks at it.

### 5. Upload staging on the main thread (`UploadBuffers.Rent`, `TexelCopies.Start`). Up to about 0.4 ms.

- **Evidence**: `UploadBuffers.Rent` takes 0.4-1.7% inclusive (c100 1.7%, about 0.48 ms).
  `TexelCopies.Start` takes 0.3-0.8%. Both are under draw recording and the property
  textures.
- **Exact**: likely (buffer reuse); not examined.
- **Estimate**: at most their shares. Partly covered by render pipelining if recording moves
  off the main thread.

### 6. Mesh channel copies (`Mesh.Slice`). Up to about 0.05-0.1 ms a frame, more in the slowest frames.

- **What**:
  - Every mesh setter allocates a new channel array (`Mesh.Slice`,
    `src/OniMoono.UnityEngine/Rendering/Mesh.cs`), because the device's uploads hold the
    arrays until they run on the render thread (`MeshNative`'s contract).
  - The conduit flow visualizers rebuild their meshes every frame.
- **Evidence**:
  - These copies are 40.6 MiB/s on DeCrew, 46.2 on U57, 11.7 on c100 and 10.7 on soggy
    (allocation table above).
  - They are the largest allocation source on every save. GC pauses were 3.4, 1.9, 1.3 and
    0.3 ms/s.
  - The heap agent's numbers put mesh UVs (`Vector4`) at about 42 MB of the heap on soggy lab
    2 (docs/research/heap-soggy-lab.md).
- **Exact**: yes, if a channel array is reused only after the upload that held it has run (a
  per-mesh pool handed back by the device).
- **Estimate**:
  - Bounded by the GC pauses: DeCrew 3.4 ms/s at 64.6 fps is about 0.05 ms a frame on
    average. Allocating and zeroing the arrays adds to that (unmeasured).
  - The gain is mostly in pause frequency and the slowest frames, not the mean.

### 7. `CollectMesh` and `CollectSprite` allocations. Under 0.05 ms.

- **What**:
  - `RendererDraws.CollectMesh` allocates 0.1-0.3 MiB/s.
  - `PropertySheet..ctor` allocates 0.5-2.3 MiB/s. Part of that is `CollectSprite`'s
    `new PropertySheet()` for each sprite drawn each frame (read from the code; the trace does
    not separate callers).
- **Exact**: yes (pooling per frame, as draw items are).
- **Estimate**: small. The allocation rates are 1-4% of the total, and so is their share of
  the GC pauses.

### 8. Vulkan per-draw trimming (vulkan-draw-binding.md step 1). 0 ms main thread today.

- **What**: identities before writes, a cheaper cache key for the large sets, a per-pass
  "ready" stamp in `PrepareTextures`, and pipeline keys hashed ahead
  (`src/OniMoono.Desktop/Vulkan/VkDevice.Execute.cs`). Measured there at -0.11 to -0.20 ms
  for identities first, and estimated at 0.4-0.7 ms in all, on the device thread.
- **Evidence**:
  - The device thread was busy 2.4-7.9 ms a frame against a main thread of 15-31 ms, so it
    does not bound any frame here.
  - Its draws took 1.5-3.4 ms. On c100, texture uploads (5.0 ms) are the larger part.
- **Exact**: yes (the same calls).
- **Estimate**: no main-thread saving until the device thread bounds the frame (slower CPUs,
  or once the main thread is shorter).

### Not candidates

- The native sim step (`Game.StepTheSim`): soggy 19.2%. It is SimDLL's work, called
  synchronously.
- `KBatchedAnimController.ValidateRegistration` and FMOD's `StringHelper`: lock artifacts (see
  Method).
- Main-thread lock waits: at most 0.03 ms a frame (contention events).
- Mods' own code on soggy lab 2: under 0.1% of the main thread.

### In progress elsewhere (not re-measured here)

- **Culling registry** (`rc-cull-u60`): cull only renderers that are enabled on active
  objects, and keep idle renderers free. It bears on soggy lab 2's 1,364 `Mask` renderers and
  on c100's 158.
- **Bindless residency**: in progress.
- **Render pipelining** (`render-pipeline-spike`): render-step timing, and renderer tests on
  worker threads in `Cull`. The main thread's share of draw recording is 0.8-3.0 ms a frame
  here (table above), the largest engine piece. It is the measure for that work.

### Load time and memory (heap agent, docs/research/heap-soggy-lab.md)

These are not per-frame, so they are outside the ranking above:

- Asset bundle bytes take 1.06 GB of the managed heap, at parity with Unity's LZMA
  decompression. A disk cache with `mmap` is possible.
- Each bundle is allocated twice at load (about 1.1 GB peak).
- About 86 blocking gen2 collections during mod load come from
  `MissingInterfaceMethods.Collect`'s `GC.Collect` loop (about 2.9 s).
- `ComponentLayout` match tables take 33.5 MB, `UploadedTexels` 37 MB, and mesh UVs as
  `Vector4` about 42 MB.

## Verdicts

| Candidate | Verdict | c100 | DeCrew | soggy lab 2 | U57 |
|---|---|---|---|---|---|
| 1. Symbol overrides | **Landed**, exact | 34.9, 40.7 → 62.4, 84.7 fps; main thread 28.4, 24.3 → 15.9, 11.7 ms | 107.2, 82.3 → 129.8, 107.8 fps | 31.3, 33.7 → 28.9, 33.4 fps | 62.3, 70.9 → 66.4, 70.6 fps |

Candidate 1 (`fast-symbol-overrides`, kept applications):
- **Cause**: Duplicants re-apply their overrides on every dirty mark
  (`applySymbolOverridesEveryFrame`, for the face). On c100 that is about 85-110 applications
  a frame (Linux, counted): about 11-17 after a real override change (speech, blinks), about
  0.4 after a batch change, the rest with unchanged inputs. The face drawn over the overrides
  changed each block twice, so the batch re-uploaded 40-67 whole override blocks a frame.
- **Change**: unchanged inputs replay the kept calls and redo only the writes covering
  entries written since; a block left byte-identical keeps its version. Uploads 40-67 → 14-18
  blocks a frame on c100; `ApplyOverrides` 2.4-3.3 → 1.0-1.7 ms a frame (Linux debug build,
  stopwatch).
- **Exactness**: ONI_TWEAK_VERIFY on c100: 0 mismatches in 120 million checks. A model test
  compares block and uploaded texture bytes with the game's full application over random
  histories. Paused-capture pixel comparisons are dominated by run-to-run noise (22k-96k
  pixels between two runs of one build) and show nothing either way.
- **Runs**: Windows, U60 761211, Vulkan, speed 3, busiest world, base and new interleaved
  (base, new, new, base), 120 s each from 60 s after the busiest world. Base is the same host
  with origin's `OniMoono.GameTweaks.dll`. The noise is large: the same build differs by up to
  25 fps between pairs on c100 and DeCrew. c100 gains in both pairs, every phase included
  (fewer texture uploads). soggy and U57 show no change beyond noise: their late phase, where
  the change runs, is 5.77, 5.20 → 5.69, 5.28 ms (soggy) and 5.62, 5.02 → 5.18, 4.89 ms (U57).

## Next

1. Put stopwatches on candidates 1-3: `ApplyOverrides` calls by cause, the queued loop in
   `Cull`, and `LoadRaw`'s copy with the changed fraction. Then prototype the ones whose
   measured share holds up.
2. Re-measure this baseline on a quiet machine (no Linux players) before comparing fps.
   Until then, compare main-thread phase times within paired runs.
