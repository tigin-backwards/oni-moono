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
- **Which option**:
  - Copying only the region `TextureRegion` wrote does not apply. The game's five
    `LoadRawTextureData` calls (`Flow`, `Liquid`, `LiquidData`, `MaterialData`,
    `ExposedToSunlight`) hand over the sim's whole-grid buffers, with no region.
    `TextureRegion` writes the other property textures through `GetRawTextureData<T>` into a
    page the size of the visible cells, which `Apply` uploads whole.
  - Skipping when the bytes are unchanged is what landed (`RawTextureLoads`). The copy skips
    the equal leading bytes and says whether any differed. While none did, `Apply` asks the
    device for no upload: no worker copy (`TexelCopies`), no staging and no device-side
    compare. The bytes are in the texture before the call returns. A texture whose texels
    are reached any other way (`Memory`, a `GetRawTextureData<T>` or `GetPixelData<T>`
    alias, new memory) uploads as before.
  - The compare costs about what the copy did, as the 2026-11-02 prototype found (a
    benchmark on this CPU: a 7.3 MB compare is 1.0-1.2 times a copy). The gain is the work
    after the copy.
- **Correctness**:
  - `ONI_VERIFY_RAW_LOADS=1` checks every load against a full copy, and every skipped
    upload against a snapshot of the texels last uploaded. Windows: c100 75,000 checks,
    soggy lab 2 (35 mods) 18,000 and 33,000; 0 wrong.
  - `ONI_CAPTURE_TWICE=full-uploads` renders each 2x capture again after every raw-loaded
    texture is uploaded in full. c100 frames 2500-5500 (4 captures) and soggy lab 2 frames
    2500-6000 (5 captures): byte-identical.
- **Measured (2026-11-04)**:
  - **Setup**: Windows, the U60 761654 frozen install. Base is `u60` at `23e4a2f4`; the
    change is the same plus this one. Vulkan defaults, speed 3, busiest world, 1280x720 on
    monitor 2, `--no-focus`, High priority, sound off, `--frame-stats`. soggy lab 2 has its
    35 gated mods.
  - **Runs**: one unmeasured launch of each build, then base, change, base, change (about
    130 s after the busiest world, means from 30 s on). Then two launches of the change with
    `ONI_RAW_LOADS_ALT=1`, which runs every other 60-frame window as before. Later the same
    day, with the same staged build and setup: one unmeasured launch and two ALT launches on
    U57, and two more ALT launches on DeCrew (its third and fourth).
  - **Machine**: every run started after a minute with no other player and a WSL load
    under 2. The highest load while measuring was 0.05-2.48.
  - **Paired windows**: change minus before, median (IQR), one value per launch.
    "Unchanged" is the change's unchanged loads per frame (of 5).

    | Save | Unchanged | Main thread (ms) | `LoadRawTextureData` (ms) | Device thread busy (ms) | Its texture uploads (ms) |
    |---|---|---|---|---|---|
    | c100 | 4.40, 4.34 | -0.93 (-1.41..-0.23), -0.89 (-1.72..-0.03) | +0.002, +0.003 (of 0.21) | -0.39 (-0.63..-0.17), -0.47 (-0.70..-0.08) | -0.28, -0.32 |
    | DeCrew | 4.25, 4.31, 4.26, 4.27 | -0.17 (-0.40..+0.09), -0.16 (-0.46..+0.10), -0.20 (-0.52..+0.05), -0.14 (-0.44..+0.09) | +0.007, +0.008, +0.009, +0.010 (of 0.05) | -0.09 (-0.16..-0.03), -0.11 (-0.17..-0.02), -0.11 (-0.20..-0.03), -0.09 (-0.18..-0.02) | -0.04, -0.04, -0.05, -0.04 |
    | U57 | 4.20, 4.16 | -0.39 (-1.10..+0.21), -0.29 (-0.98..+0.30) | +0.005, +0.008 (of 0.06) | -0.07 (-0.14..-0.03), -0.08 (-0.13..-0.02) | -0.05, -0.05 |
    | soggy lab 2 | 0.50, 0.46 | -0.05 (-0.30..+0.19), -0.01 (-0.37..+0.15) | +0.021 (+0.011..+0.032), +0.021 (of 0.80) | -0.13 (-0.20..-0.05), -0.08 (-0.18..-0.02) | -0.11, -0.10 |

  - **Pooled pairs** (all of a save's ALT launches; fps is 1000 / main-thread ms of each
    60-frame window, which tracks PerfProbe's fps to within 1%):

    | Save | Launches | Main thread (ms) | Device thread busy (ms) | fps |
    |---|---|---|---|---|
    | c100 | 2 | -0.91 (-1.57..-0.12) | -0.43 (-0.65..-0.15) | +12.0 (+1.6..+21.1) |
    | DeCrew | 4 | -0.16 (-0.44..+0.08) | -0.10 (-0.18..-0.02) | +4.6 (-2.3..+12.0) |
    | U57 | 2 | -0.35 (-1.02..+0.24) | -0.07 (-0.14..-0.02) | +2.8 (-2.0..+8.7) |

  - **Launches**: base, base → change, change.

    | Save | fps | Slowest 1% (ms) | Main thread (ms) | Device busy (ms) | `PropertyTextures.LateUpdate` (ms) |
    |---|---|---|---|---|---|
    | c100 | 100.1, 96.9 → 109.3, 122.6 | 21.6, 22.1 → 21.1, 19.1 | 10.03, 10.28 → 9.13, 8.18 | 3.23, 3.46 → 2.95, 2.71 | 0.34, 0.34 → 0.33, 0.32 |
    | DeCrew | 157.1, 163.1 → 168.4, 163.5 | 20.0, 19.0 → 18.7, 20.1 | 6.31, 6.10 → 5.89, 6.09 | 1.90, 1.86 → 1.73, 1.67 | 0.15, 0.14 → 0.16, 0.16 |
    | soggy lab 2 | 35.5, 35.4 → 34.8, 35.2 | 52.2, 54.2 → 52.0, 54.0 | 28.08, 28.19 → 28.65, 28.32 | 3.93, 4.32 → 3.91, 3.85 | 1.05, 1.07 → 1.09, 1.08 |

  - **What it shows**:
    - Where frames are fast (c100, DeCrew), most loads repeat the last frame's bytes, and
      the skipped uploads save main-thread time.
    - On c100 that is 0.9 ms, spread over update (-0.4), late (-0.2) and render (-0.15).
      That fits the worker copies and the texel-copy fence at the frame's start, though
      this was not traced further.
    - On DeCrew (four launches) the main thread gains about 0.16 ms and on U57 (two) about
      0.35 ms, but on both the IQR crosses zero. Four launches did not narrow DeCrew's:
      each launch gives the same spread. The device thread is 0.07-0.11 ms less busy on
      both, with IQRs that do not cross zero.
    - soggy lab 2 runs at 35 fps, and nearly every load changes. Its main thread is
      neutral; the compare adds 0.02 ms and the device saves 0.1 ms.
    - Launch fps moves with the windows, inside the noise between two launches of one
      build (c100's base launches differ by 3 fps, its change launches by 13).
- **Verdict: kept, main-thread gain clear on one save only.** It is exact and costs at most
  0.02 ms (the compare), so it stays in. Its main-thread gain is clear only on c100 (-0.9
  ms). DeCrew and U57 lean the same way and soggy lab 2 is neutral, so it does not meet the
  two-save rule for a measured main-thread gain. The device-thread saving is clear on all
  four saves, at 0.07-0.47 ms.

### 4. UI canvas batching. Up to 0.3-0.9 ms.

- **What**: `CanvasSystem.RenderOverlays`: building and batching the screen-space canvases
  (`Build`, `Batched`, `BuildMesh`), then recording their draws.
- **Evidence**: 2.5-3.1% of the main thread (0.33-0.88 ms); `Batched`'s own frame is
  0.9-1.5%. On c100, `BuildMesh` allocates 5.7 MiB/s.
- **Exact**: no specific change has been identified; not examined yet.
- **Estimate**: unknown below the measured share. Listed so the next phase looks at it.
- **Verdict: allocations landed, time rejected** (see [Verdicts](#verdicts)).

### 5. Upload staging on the main thread (`UploadBuffers.Rent`, `TexelCopies.Start`). Up to about 0.4 ms.

- **Evidence**: `UploadBuffers.Rent` takes 0.4-1.7% inclusive (c100 1.7%, about 0.48 ms).
  `TexelCopies.Start` takes 0.3-0.8%. Both are under draw recording and the property
  textures.
- **Exact**: likely (buffer reuse); not examined.
- **Estimate**: at most their shares. Partly covered by render pipelining if recording moves
  off the main thread.
- **Verdict: rejected** (the shares are the sampler's lock artifact; see [Verdicts](#verdicts)).

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
- **Verdict: landed (allocations)**, exact (see [Verdicts](#verdicts)).

### 7. `CollectMesh` and `CollectSprite` allocations. Under 0.05 ms.

- **What**:
  - `RendererDraws.CollectMesh` allocates 0.1-0.3 MiB/s.
  - `PropertySheet..ctor` allocates 0.5-2.3 MiB/s. Part of that is `CollectSprite`'s
    `new PropertySheet()` for each sprite drawn each frame (read from the code; the trace does
    not separate callers).
- **Exact**: yes (pooling per frame, as draw items are).
- **Estimate**: small. The allocation rates are 1-4% of the total, and so is their share of
  the GC pauses.
- **Verdict: rejected** (no sprites are drawn in these saves; see [Verdicts](#verdicts)).

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
- **Verdict: three parts landed, two dropped** (8a-8e in [Verdicts](#verdicts); checks, Windows
  and Linux numbers in vulkan-draw-binding.md §7). On the RTX 3090, identities first and the ready
  stamp take 0.12-0.36 ms a frame off the device thread's draws; push templates are neutral on
  NVIDIA and AMD; pipeline-key hashes made once were within noise and dropped.

### Not candidates

- The native sim step (`Game.StepTheSim`): soggy 19.2%. It is SimDLL's work, called
  synchronously.
- `KBatchedAnimController.ValidateRegistration` and FMOD's `StringHelper`: lock artifacts (see
  Method).
- Main-thread lock waits: at most 0.03 ms a frame (contention events).
- Mods' own code on soggy lab 2: under 0.1% of the main thread.

### Landed

| Change | Status | Measured (U60 761211, speed 3) |
|---|---|---|
| Incremental repair of Duplicants' async path probes (`path-probe-repair`, src/OniMoono.GameTweaks/ProbeRepair.cs; docs/TWEAKS.md) | Landed as a tweak, on by default (user's decision; not in `exact`). Differs from the original only in ties: where two routes cost the same, the repair may keep the other one, so a Duplicant may take another route of the same cost | Windows, one run a save, repairs switched on and off every 20 s (3 windows each, `ONI_PROBE_REPAIR_SWITCH`): probe thread **c100 92.0 → 33.4 ms/game s** (2,603 → 822 us a probe), **DeCrew 308.9 → 71.4** (7,580 → 1,399 us; off, the thread was 93% busy and the game's 4-a-frame queue kept it to 40.7 probes/game s, on 51.0). Process CPU: DeCrew 3.11 → 2.48 cores (1,036 → 825 ms/game s); c100 3.77 vs 3.96 cores, within the windows' frame-rate spread (26.5 vs 30.4 fps). Linux (direction only, separate runs): probe thread c100 89.6/84.0 → 34.5/28.2, DeCrew 196.8/230.2 → 70.4/73.9 ms/game s. A repair costs 0.1-0.3 ms; about 60% of probes are repairs, the rest Duplicants that moved. Verify mode (`ONI_TWEAK_VERIFY=1`): 0 of 10,704 repairs differed from a flood, 0 of 181 of our floods from `PathProber.Run`. Memory: four bits a state a Duplicant (0.9 MB on c100's 636×404 world; a byte, 1.8 MB, before the optimisation below) and about 45 MB of probe-thread scratch; not separable from run-to-run working-set spread here. Optimised after landing (ProbeBench, `ONI_PROBE_BENCH=1`, replays both captures with every Duplicant and digests every state; floods and repairs bit-identical to the landed engine): floods list their reachable cells from a bitmap instead of sorting them twice, skip links TraversePath always refuses (a hover without a jet pack, a swim without the skill: 38-46% of its calls) and do less per link; bench floods c100 1,631 → 1,187 ms, DeCrew 4,461 → 3,358 ms. A repair that would redo half its result floods instead ("repair gave up" in the report): on DeCrew 40 of 1,185 repairs (a door by a Duplicant's spot) took 6 ms each, 2.6 floods, with a 12.7k-state delta for the main thread; repairs 226 → 85 ms for 40 more floods, the main thread's delta 424 → 5 states a repair. In game (Linux, DeCrew, verify mode, separate runs, direction only): our flood 0.85 → 0.73 of the game's on the same orders, flood 5,603 → 4,873 us, repair 162 → 109 us. Fixed after the U60 release QA (51aa950f): a change the game made earlier in the frame (an access door taken down) reached the probe thread before the repair's batch, so a repair could miss the cells behind it (126 cells in one QA run, in 8 of 10 plays); the probe thread now takes everything recorded before it starts. Verify mode now also checks each differing repair against what a flood can leave (ties accepted, counted apart) and audits the change feed each frame: two QA verify runs, 9,613 and 10,672 repairs, 0 wrong, 0 ties, 0 missed changes. |
| The Load screen with thousands of saves (`fast-load-screen`, src/OniMoono.GameTweaks/FastLoadScreen.cs, src/OniMoono.Host/GameTweaks/LoadScreenPreviews.cs; LoadImage's own PNG decoder, src/OniMoono.UnityEngine/Core/PngDecode.cs; `fix-load-screen-clicks`; docs/research/load-screen-many-saves.md) | `fast-load-screen` landed in `exact` (on by default): each save's header read once a refresh instead of four times, read ahead on 8 threads and applied in the game's order; previews of pages already shown kept for the screen's opening (decode memo, and U60's GPU read-back of downscaled previews). The PNG decoder is engine-side, always on, exact (byte-identical to stb on 10,741 PNGs). `fix-load-screen-clicks` (the game's stacked colony click handlers) landed on by default (the user's decision), out of `exact` | Linux lanes, 4,637 saves (synthetic tree of a user's folder), one to two runs: opening **1,285-1,457 → 542-720 ms** (original 2,280-2,455); both header passes 806-964 → 174-229; Load more to 5 pages **1,213-1,664 → 289-293 ms** (its 100 previews 820-1,129 → 80-84; original 2,098-2,871). WSL 9P, the real folder's 4,637 headers: 21.8-51.8 s one after another, 2.2-2.5 s read ahead. Decoder, a 2096x648 preview: stb 13.5-15.4 ms, ours 6.5-6.9. Verify mode: 0 of 92,720 headers and 0 of 64 previews differed. A 715-save colony after four Load more clicks: 5 builds in 2.9-4.1 s, with the fix 1 in 0.41-0.43 s. Windows not measured. |
| Anim lists trimmed (`trim-anim-lists`, src/OniMoono.GameTweaks/TrimAnimLists.cs; docs/research/heap-reduction-designs.md §2) | Landed in `exact` (on by default): after Assets.OnPrefabInit each batch group's `frameElements`, `animFrames` and `symbolFrameInstances` Capacity is set to its Count | Linux, U60 with every DLC (any save): 5,923 lists, **66.5 MB** of unused slots freed; c100's live managed heap after a full GC at the load 8,251.1 → 8,185.9 MB. Verify mode: 0 lists differed. Windows not measured. |

### Fixes in progress when the goal started (kept their own measurements)

- **Culling registry: landed** on u60 (3e396fbe, 04026a41, 6b148c03; master 0741df06,
  296fd060, b85b1fd5). Cull walks only renderers enabled on active objects, kept in one list
  per layer, and each camera walks only its mask's layers; tied renderers keep Unity's order
  (UnityProbe renderercull). On soggy lab 2 with Fast Track, 15 cameras had each walked about
  12,000 enabled renderers for about 900 in their mask: 6.5-8.8 ms of a 9-12 ms render phase.
  `FrameRenderer.CullVisits` and its test keep the idle renderers free.
- **Bindless residency: landed** (hardening for #50; u60 10fa85a4 and the commits before it,
  master cc576f14): make-resident is checked with a fallback to a bound unit, textures leave
  residency only once the GPU is done with them, a cap of 8,192 resident handles, idle eviction
  opt-in. research/bindless-residency.md has the driver test and the colony counts. Not a
  frame-time change.
- **Render pipelining: done, not taken** (`render-pipeline-spike`, local, 2026-11-02).
  Overlapping frame N's render work with frame N+1's update is not feasible at a reasonable
  cost: no cheap snapshot of render state (taking one costs about what culling does), game code
  runs during rendering (OnRenderImage effects such as bloom and fog of war, OnPostRender
  visualizers, OnBecameVisible health bars, the Timelapser; one of soggy lab 2's mods hooks
  OnPostRender), and materials and globals are rewritten in place each frame, so an overlapped
  frame could sample the next frame's textures. Splitting Cull across workers while the main
  thread waits was feasible: soggy lab 2's render phase 11.8 → 7.7 ms, with byte-identical
  captures (`ONI_CAPTURE_TWICE=serialcull`). Its cause, 30,600 registered renderers x 15
  cameras for about 1,300 visible, was fixed serially instead by the culling registry above.
  What parallel culling would still add after it was estimated at 0.5-1.5 ms (estimate from
  the spike's per-camera walk times) and is not pursued.

### Load time and memory (heap agent, docs/research/heap-soggy-lab.md)

These are not per-frame, so they are outside the ranking above:

- Asset bundle bytes took 1.06 GB of the managed heap. On U60 (LZ4 blocks) they are now read
  block by block (Verdicts, "Bundles read block by block"); U59's LZMA bundles are decompressed
  whole, at parity with Unity. A mapped cache file was built and reverted (its disk writes and
  cold reads).
- Each bundle is allocated twice at load (about 1.1 GB peak); on U60 no longer.
- About 86 blocking gen2 collections during mod load come from
  `MissingInterfaceMethods.Collect`'s `GC.Collect` loop (about 2.9 s).
- `ComponentLayout` match tables take 33.5 MB, `UploadedTexels` 37 MB, and mesh UVs as
  `Vector4` about 42 MB.

## Verdicts

| Candidate | Verdict | c100 | DeCrew | soggy lab 2 | U57 |
|---|---|---|---|---|---|
| 1. Symbol overrides | **Landed**, exact | 34.9, 40.7 → 62.4, 84.7 fps; main thread 28.4, 24.3 → 15.9, 11.7 ms | 107.2, 82.3 → 129.8, 107.8 fps | 31.3, 33.7 → 28.9, 33.4 fps | 62.3, 70.9 → 66.4, 70.6 fps |
| 2. `Cull`'s queued loop | **Landed**, exact | 24.0, 23.4 → 19.4, 20.1 µs per Cull call (about 960 meshes) | 30.1, 25.3 → 22.3, 22.9 µs per call (about 2,000 meshes) | – | – |
| Closures and delegates (sweep) | **Landed (allocations)**, exact | closures and delegates 83-102 → 0.7-2.2 KiB/frame; ours 354-417 → 311-348; total 664-785 → 642-702 | 64-80 → 0; ours 312-374 → 260-261; total 876-997 → 858-886 | – | – |
| 6. Mesh channel copies (with 4's baked meshes) | **Landed (allocations)**, exact | reuse 26.9, 25.0 → 15.0, 15.4 MiB/s; all of it 468-575 → 258-373 MiB per 600 frames | reuse 34.2, 36.9, 59.0 → 16.5, 17.4, 20.9 MiB/s; all 546-609 → 181-241 MiB | reuse 20.4 → 10.2 MiB/s (without its mods) | reuse 24.7, 30.0 → 15.6, 12.2 MiB/s; all 744-770 → 350-407 MiB |
| 4. UI canvas batching | **Allocations landed** (in 6's numbers); **time rejected** | `BuildMesh` 5.7 MiB/s, its colours (2.8) kept when only moved | – | – | – |
| 5. Upload staging | **Rejected** | `TexelUpload.Take` 0.30-0.41 ms a frame: rent 0.02-0.04, small copies 0.17-0.23, task starts 0.07-0.09 | – | – | – |
| 3. Property-texture uploads (`RawTextureLoads`) | **Kept**, exact (verify 0 wrong, captures byte-identical); main-thread gain clear on one save only (c100); device thread less busy on all four | paired windows: main thread -0.93, -0.89 ms; device -0.39, -0.47 ms; fps 100.1, 96.9 → 109.3, 122.6 | four launches: main thread -0.17, -0.16, -0.20, -0.14 ms (pooled -0.16, IQR -0.44..+0.08: crosses zero); device -0.09 to -0.11 ms | main thread -0.05, -0.01 ms (neutral); `LoadRawTextureData` +0.02 ms; device -0.13, -0.08 ms | two launches: main thread -0.39, -0.29 ms (pooled -0.35, IQR -1.02..+0.24: crosses zero); device -0.07, -0.08 ms |
| 7. `CollectMesh`/`CollectSprite` | **Rejected** | no sprites drawn; sheets come from blits and blocks | – | – | – |
| NavGrid links in compact stores (`compact-nav-links`, navgrid-links-store.md) | **Landed**, exact, on by default | managed heap 9,200, 9,240 → 8,628, 8,592 MiB; load 43.3, 39.1 → 35.3, 32.8 s; fps 40.8, 47.4 → 46.4, 38.7 (noise) | links 256.4 → 26.2 MB (Linux) | managed heap 7,736, 7,696 → 5,602, 5,594 MiB; working set 12,094, 12,030 → 9,835, 9,772 MiB; fps 70.2, 41.8 → 54.5, 44.7 (noise) | – |
| 8a. Identities before set 0's writes (c7868f47) | **Landed**; correct (verify 0 wrong, captures byte-identical) | Windows: device-thread draws -0.19 ms (IQR -0.25..-0.13) | Windows: draws -0.36 ms (IQR -0.46..-0.25); Linux (llvmpipe, CPU direction): -0.49 ms (IQR -0.66..-0.09) | – | – |
| 8b. Cheaper key for the large sets | **Dropped**: a CPU benchmark found the full 78-word key's lookup and one of the 8 changing words equally fast (about 60 ns) | – | – | – | – |
| 8c. Per-pass "ready" stamp (22baa5e5) | **Landed**; correct (verify 0 wrong, captures byte-identical) | Windows: draws -0.12 ms (IQR -0.19..-0.06) | Windows: draws -0.24 ms (IQR -0.39..-0.05); Linux: -0.61 ms (IQR -0.76..+0.03) | – | – |
| 8d. Pipeline-key hashes made once | **Dropped**: within noise on Windows; CPU benchmark 36-48 → 28 ns a draw (0.01-0.03 ms a frame) | Windows: draws -0.04 ms (IQR -0.11..+0.03) | Windows: draws -0.06 ms (IQR -0.18..+0.03); Linux: -0.14 ms (IQR -0.32..+0.30) | – | – |
| 8e. Push descriptor templates | **Landed**, neutral on NVIDIA and AMD; correct (verify 0 wrong) | Windows: draws -0.005 ms (IQR -0.05..+0.05); AMD iGPU -0.005 ms (IQR -0.08..+0.12) | Windows: draws -0.01 ms (IQR -0.09..+0.10); Linux: -0.25 ms (IQR -0.31..-0.17) | – | – |
| Path grids in pages (`paged-path-grids`, heap-reduction-designs.md §3.3) | **Landed**, exact, on by default | Windows: managed heap 8,729, 8,676 → 4,308, 4,339 MiB; private bytes 15,441, 15,291 → 11,199, 11,104 MiB; working set 10,212, 10,083 → 9,579, 9,618 MiB; fps 46.2, 54.2 → 46.3, 56.1, main thread 25.7, 16.1 → 17.8, 19.6 ms (noise); probe thread 0.66, 0.47 → 0.59, 0.60 ms a probe | tables 265.5 → 177.7 MB (Linux) | Linux, without mods: heap after a full GC 4,267 → 3,276 MB; GC commit 5,554 → 4,189 MB | – |
| Bundles read block by block (U60; src/OniMoono.Assets/Files/ArchiveBlocks.cs, heap-reduction-designs.md §1.3) | **Landed**, engine-side, always on (`ONI_BUNDLE_BLOCKS=0` off), exact: verify mode (`ONI_BUNDLE_BLOCKS_VERIFY=1`) 0 mismatches; replaces the mapped bundle cache, reverted. At the menu (Linux, every DLC, any save): live heap 1,717 → 637 MB, committed 2,717-2,754 → 1,169-1,403 MB, time to the menu 16.6-16.7 → 16.0-16.7 s. Windows (Vulkan, interleaved, second launch, 5 runs each): private bytes 6,675-6,911 → 5,462-5,697 MiB, managed heap 1,966-2,239 → 862-1,032 MiB; time to the menu 14.4-17.8 (mean 16.4) → 15.6-17.9 (mean 17.0) s, inside the spread; the main thread's reads 265-300 ms | Linux: live heap at the load 3,449, 3,450 → 2,336, 2,337 MB; 300 s in 4,685, 4,697 → 3,577, 3,579 MB; load 16.1, 16.6 → 17.6, 17.0 s (no block read during it: spread); frame interval 26.4, 26.0 → 25.1, 27.3 ms (spread); main thread's bundle reads 0 → 0.14-0.20 s before the menu. Windows: private bytes 10,781, 11,117 → 9,926, 9,898 MiB, managed heap 4,357, 4,482 → 3,420, 3,414 MiB (110 s in); load 18.4, 15.1 → 13.8, 15.8 s; fps 37.6, 42.9 → 47.4, 42.9, worst 1% 50.5, 47.9 → 44.6, 49.2 ms (noise); the same slow frames, none from uploads | Windows: private bytes 17,452, 16,979 → 15,657, 15,799 MiB, managed heap 9,669, 9,384 → 7,812, 7,998 MiB (110 s in); load 26.2, 26.4 → 24.8, 24.4 s; fps 46.4, 51.9 → 51.6, 54.7, worst 1% 59.2, 51.4 → 49.9, 48.8 ms (noise); the same slow frames | – | – |

Resident vs committed (`paged-path-grids` and bundle block reads, the two rows above), on the
200-Duplicant colony: Windows, U60 761654, Vulkan, speed 3, busiest world, one run each after
a warm-up, means of 10 samples (every 10 s, 30-130 s after the camera reached the world). Page
faults count soft and hard together.

| Configuration | Working set | Private working set | Private bytes | Managed heap | Page faults a second |
| --- | --- | --- | --- | --- | --- |
| Defaults | 8,654 MiB | 5,807 MiB | 9,910 MiB | 3,462 MiB | 8,200 |
| `paged-path-grids` off | 8,936 MiB | 6,089 MiB | 13,909 MiB | 7,488 MiB | 7,900 |
| `ONI_BUNDLE_BLOCKS=0` | 9,639 MiB | 6,968 MiB | 11,176 MiB | 4,548 MiB | 1,050 |
| Both off | 9,938 MiB | 7,265 MiB | 15,204 MiB | 8,604 MiB | 320 |

- Paged path grids save 3,999 MiB of private bytes but only 282 MiB of working set: the
  Duplicants' flat path arrays were committed but mostly never touched, so they never took
  RAM.
- Block reads save 1,267 MiB of private bytes and 986 MiB of working set (1,161 MiB private
  working set): the whole-bundle copies were unpacked and touched, so this saving is in RAM
  too. They come with more page faults: 8,200 a second against 1,050 without them.
- Both together save 5,294 MiB of private bytes and 1,284 MiB of working set (1,458 MiB
  private working set).

Resident vs committed (`compact-nav-links`), measured the same way on the 200-Duplicant
colony: defaults and the tweak off by the settings file, two runs each, alternated (defaults,
off, defaults, off), after a warm-up; means of 9-10 samples.

| Configuration | Working set | Private working set | Private bytes | Managed heap | Page faults a second |
| --- | --- | --- | --- | --- | --- |
| Defaults | 8,525, 8,573 MiB | 5,678, 5,726 MiB | 9,784, 9,822 MiB | 3,340, 3,352 MiB | 7,780, 470 |
| `compact-nav-links` off | 9,120, 9,247 MiB | 6,275, 6,404 MiB | 10,529, 10,530 MiB | 3,950, 3,976 MiB | 400, 360 |

- The compact stores save 726 MiB of private bytes and 634 MiB of working set (637 MiB
  private working set), so most of the saving is in RAM. The game's flat `Link[]` arrays
  were all touched: NavGrid's constructor builds the links of every cell
  (`NavGridUpdater.CreateLinks`), and each cell's row ends with a written end marker, even
  when the cell has no links. A row is 136-448 bytes (8-byte links, 17-56 slots), so every
  4 KiB page of the array holds a written marker.
- Page faults do not separate the two: one run with defaults faulted 7,780 times a second,
  the other 470.

Path grids in pages (`paged-path-grids`):
- **Change**: each PathGrid's `Cells` and `ProberCells` (whole-cluster arrays per Duplicant,
  Rover and Flydo) become page tables of 256 elements made on first write, an unwritten page
  reading as the zeros the array holds; the load-time rewrite sends PathGrid's 12 accesses
  through them, path-probe-repair reads them through views and pages its own arrays too
  (heap-reduction-designs.md §3.3). The arrays the game would hold → what the tables hold, in
  play: c100 5,025 → 830-880 MB (713 grids, critters' included), soggy lab 2 without mods
  1,044-1,129 → 46-52 MB, DeCrew 265.5 → 177.7 MB.
- **Exactness**: ONI_TWEAK_VERIFY, every element against a flat array every 30 s: 0 differed on
  c100 (439.6 M elements), soggy lab 2 (105.8 M) and DeCrew (23.4 M), path-probe-repair's own
  checks 0 wrong. e2e on U60, both players: early-base, u60-v3-delivery, klei-unreachable pass;
  path-probe-repair.yaml (ours) passes; issue-48-fast-track does not run here (its Fast Track
  build is not installed), Fast Track's three `lock (grid.Cells)` are kept (unit test on its
  DLL).
- **Runs**: see the table's row and §3.3.

NavGrid links in compact stores (`compact-nav-links`):
- **Change**: `NavGrid.Links` (maxLinksPerCell slots for every cell of the map, under 6 % used)
  becomes a one-element handle naming a store of compressed rows; the load-time rewrite sends
  every `Link[]` access of the game and mods through its helpers, the three hot walks through a
  cursor (navgrid-links-store.md §6). The grids' links: soggy lab 2 2,387.5 → 118.2 MB, c100
  670.1 → 55.3 MB, DeCrew 256.4 → 26.2 MB (the store's own count after the load).
- **Exactness**: ONI_TWEAK_VERIFY keeps the game's array beside each store, written by the same
  calls, and compares every cell after the load and every 30 s of play: 0 cells differed on
  c100 (14 comparisons, 3.84 M links), DeCrew (27, 1.80 M), soggy lab 2 without mods (13,
  5.35 M), soggy lab 2 with its 35 U60-loading mods (U60, and U59 on master) and with
  path-probe-repair on (c100, DeCrew; its repairs verified against floods, 0 differed). An
  independent check, the grids dumped after the load's first flush in two runs of the same
  save, one with the store and one with the game's arrays (`ONI_NAVLINKS_DUMP`): 0 cells
  differ, per grid, per nav type and in order, on c100 (3.84 M links), DeCrew (1.81 M), soggy
  lab 2 without mods (5.34 M) and with its 35 mods on U60 (5.34 M) and on U59 (5.33 M). The
  unit tests replay NavProbe's dumps of the three saves through CreateLinks' writes from
  several threads and race readers against writers.
- **Mods**: of the 1,340 Workshop DLLs naming NavGrid, MoreTek, Direct Duplicant Control and
  Travel Tubes Flydo are rewritten, none has an access the store cannot follow. The four
  reviewed transpilers apply to the rewritten methods (`--check-types`, U59); on U60 Hazard
  Pathing throws with the tweak on and off alike, its pattern being U59's IL. e2e in both
  players on U60: early-base, Transit Tubes Expanded, Flydo Transit Tubes and ONI Lag Fix
  pass; Transit Tube Overlay fails at its tint check in the original, and in ours with the
  tweak on and off alike. On U59 all five pass. issue-48-fast-track did not run: the mod gate
  skips the Fast Track build it needs (no Workshop upload).
- **Runs** (Windows, U60 761211, Vulkan defaults, speed 3, busiest world, `--no-focus`, High
  priority, sound off, monitor 2; base is the same build with `--game-tweak -compact-nav-links`,
  which leaves the game's IL as it is; base, new, base, new, one launch each; 150-270 s after the
  busiest world; other agents' Linux players loaded the machine, load average 8-21): the managed
  heap falls by the store's saving, 570-650 MiB on c100 and 2,100 MiB on soggy lab 2, the
  working set by 600-700 and 2,250 MiB. c100's load was 7 s shorter in both pairs (43.3,
  39.1 → 35.3, 32.8 s: the 670 MB zeroed arrays are gone); soggy's within noise (33.8, 40.0 →
  32.8, 41.6 s). Frame rate and main-thread time moved within the runs' spread (c100 24.3, 21.4
  → 21.9, 25.9 ms; soggy 14.3, 23.9 → 19.4, 22.7 ms), as did process CPU (c100 3.08, 3.10 →
  3.14, 2.98 cores; soggy 3.19, 3.16 → 3.18, 3.20). Frame stats' `path probes` stayed 0.10-0.11
  ms on c100 and 0.02-0.03 on soggy; `nav grids` (the grids' updates, through the staged
  writes) rose 0.16 → 0.18-0.19 ms on c100 and 0.06-0.08 → 0.08-0.09 on soggy. The probe
  thread itself is not timed by frame stats; a link-walk micro-benchmark over the real links
  (Linux, loaded machine) put the cursor walk level with the game's array on the Duplicant grids
  and about 1.4× slower on DeCrew's flyer grid, where most cells hold links.

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

Candidate 2 and the closure sweep (Linux, U60 761211, OpenGL on Xvfb, speed 3, busiest world,
30 s traces with GC allocation ticks from 90 s after the busiest world; Windows not measured):
- **Cull's queued loop**: the boxed enumerators had already gone (6d296cca); the `foreach`
  still copied each `QueuedMesh` out of its list. The loop reads each in place by index, in
  the same order. A stopwatch around it gives the numbers above, two runs per build. That is
  15-25% of the loop, a few microseconds per camera: well short of the estimate, which was
  `Cull`'s whole own frame.
- **Closures and delegates**: per-change and per-draw closures and delegates in our code. GL
  texture and mesh uploads and sampler changes, render targets, viewports, scissors and clears
  (GL and Vulkan) are now commands reused from the frame's arena. Closures C# made on entry to
  `GlDrawRecorder.Program`, `GlTextures.Upload2D` and `TexelCopies.Start` are made only on
  their rare paths. Image effects, nested canvases, video planes, immediate draws' variant
  selection and the symbol-override overlap sort allocate nothing per call. "Ours" counts
  allocations whose innermost non-BCL frame is in our code: −16 to −23%. Total allocation is
  about −8%, most of the rest being the game's conduit meshes. The main thread's time could
  not be measured at this size (30-80 ms a frame with the machine's load), and neither could
  fps.
- **Start-up** (35 mods, to the main menu): mod access checks and missing-type scans no longer
  look up every loaded assembly's name or make a delegate per type checked. Start-up allocations
  from our code fell from 3,353 to 3,011 MiB. The time to the main menu (22-26 s) did not
  change beyond noise.
- **Windows**: a Vulkan smoke run on c100 (761211, speed 3, busiest world) loaded and played
  for two minutes with no errors in the log. The GC ran one gen0 and one gen1 collection in a
  60 s trace: with Server GC's budgets, collections are already too few for these allocations
  to change their count measurably.

Candidates 4-7 (`perf-ui-mesh`):
- **6, the change**: each mesh setter copies a list once, straight into the channel array
  (SetUVs had copied the whole list and then sliced it; SetTriangles and SetIndices with a list
  copied it twice). An array a setter made is kept when the mesh lets go of it, and reused for a later
  channel of the same length once the device has run every upload that could still read it:
  each upload carries its place in the mesh's uploads and reports when it ran, on OpenGL and
  Vulkan, failed or not. Only setter-made arrays are reused, since getters hand out copies;
  spares not taken within four frames are dropped. `ONI_MESH_SPARES=off` turns the reuse off.
- **4, the change**: a canvas element whose geometry and colour are unchanged (world-space
  canvases follow their objects every frame) keeps its colour array, takes its positions from
  the mesh's reused arrays and keeps its one submesh. The same switch turns this off.
- **Linux** (U60 761211, OpenGL on Xvfb, speed 3, busiest world, release builds, base `a90b12ae`
  against the change, one run each): allocation per 600-frame window as in the table, −35 to
  −70%. Reuse took 83% of the arrays wanted on DeCrew (about 67 a frame).
- **Windows** (U60 761211, Vulkan, speed 3, busiest world, `--no-focus`, High priority, sound
  off, monitor 2): one launch per save, PerfProbe's `ab.txt` flipping `MeshNative.ReuseOff` in
  18 s segments, six alternating after 30 s or eight in A B B A order after 60 or 150 s. The
  switch covers the reuse and the kept colours; the single copies stay on in both halves, so
  the table's "reuse" figures understate the change. Allocation falls 37-65% in every run. The
  frame rate drifted by up to 2× within a run (warm-up, and other agents' players on the
  machine), far more than any difference between the halves: main-thread time with reuse on
  was 13.4-57.1 ms against 16.1-55.8 off: lower by 1.8-2.6 ms in all three DeCrew runs (the
  conduit-heavy save) and by 1.0 ms on soggy, either way on c100 and U57; within the drift,
  not a measured saving. GC pauses were too few to compare (0-7 ms/s, either way round in 18 s
  segments): with Server GC's budgets the collections came 10-60 s apart. soggy lab 2 ran
  without the reporter's mods (their copies were lost with the scratch space).
- **Exactness**: the arrays hold the same values (every element is written before use), and
  an array is reused only after the upload that read it ran (tests: an upload not yet run keeps
  its arrays; arrays a setter did not make are never reused). Paused captures (DeCrew, frames
  2400 and 3000, reuse off and on, two runs each) differ by 47-257k pixels between runs either
  way, the same as two runs with the same setting (207-230k): run-to-run noise, nothing either
  way. e2e in both players: issue-48-pipes, early-base, build-menu-search and u60-p2-renderer
  pass; consumables-scroll gives its known finding; pipe-flow fails in the original player
  (its start timed out once; then its overlay checkbox did not settle within 300 frames), so
  ours was not compared.
- **4, the time**: with the batching helpers kept from inlining (Linux c100 trace), `Batched`
  is 1.4% of the main thread: `Item` 0.6% own, `SheetFor` 0.3%, `BuildMesh` 0.2%,
  `CollectCameraDraws` 0.5%. No one piece holds the cost; nothing exact was found to remove.
- **5**: `UploadBuffers.Rent`'s 1.4% own share on c100 is the sampler stopping at its lock's
  safe point (the Method's lock artifact): a stopwatch puts `Rent` at 0.02-0.04 ms a frame,
  with no pool misses after warm-up and no contention events. All of `TexelUpload.Take` is
  0.30-0.41 ms a frame on Linux c100 (260-290 uploads, 22-28 MiB): the copies under 64 KiB made
  on the main thread (256-266 a frame) take 0.17-0.23 ms, starting the 20-26 larger ones on the
  pool 0.07-0.09. Moving the small copies off the main thread would be the only lever; it is
  `TexelCopies`' design (the parked texelcopies-hang work and the closure sweep touch it) and
  would make the device wait on a batch, so it is not taken here.
- **7**: no traced save draws a sprite (`CollectSprite`: no samples, no allocations).
  `PropertySheet` allocations come from `Blits.Blit` (0.1-0.9 MiB/s) and
  `MaterialPropertyBlock`'s copy on write (0.3-0.7 MiB/s), which keeps queued draws' values as
  Unity's copy at `DrawMesh` does. `CollectMesh`'s allocations were the sorting-layer lambda,
  a loop on u60 since.

## State machine updates by kind (2026-11-03)

`StateMachineUpdater.AdvanceOneSimSubTick` was the largest game-side entry left in
`Game.Update`. This phase splits it by update kind and looks for exact savings.

### Method

- **Per-kind stopwatches**: `ONI_BUCKET_PROFILE=1` (with `--frame-stats`) runs the buckets'
  loop with each bucket's `Update` timed, the same calls in the same order, and logs the 40
  costliest kinds a window (docs/PERFORMANCE.md, "Frame stats in a player's log"). A kind is a
  bucket's name, rate, data type and the callback it runs. The stopwatches add about 2-4% to
  the buckets' time; both halves of an A/B pay it.
- **A/B in one run**: `ONI_FRAME_STATS_AB` flips a tweak's static `Bypass` switch after every
  600-frame window. "Paired" differences take each window against the mean of its two
  neighbours of the other value, which cancels a steady drift; they are given as mean ± sd
  over n windows. The first two windows (the load) are left out.
- **Linux**: e2e lanes (OpenGL on Xvfb, speed 3, the save's active world), `--game-tweak exact
  path-probe-repair`, other agents' players on the machine. Direction only, 2-4 windows a value.
- **Windows**: U60 761211, Vulkan defaults, speed 3, PerfProbe's busiest world, 1280x720 on
  monitor 2, `--no-focus`, High priority, sound off, one launch a save, 12 windows a value.
  Other agents' Linux players ran (load average 6-9).
- **Samples**: Linux `perf record -e cpu-clock:u -g` at 499 Hz for 45-60 s on c100 and DeCrew
  (`DOTNET_PerfMapEnabled=1`), resolved against the perf map: self time by function, with
  callers and callees, for the hot kinds.
- soggy lab 2 would run without mods. On Linux its load stops at the missing-mods dialog,
  whose OK button is off the screen even at 1280x1200.

### Ranking

ms a frame in the tweak-off windows of the A/B runs (all other default tweaks on). c100 and
DeCrew on Windows; U57 on Linux, where the game's code runs about 2.5× slower (c100's buckets:
11.6 ms on Windows, 25.7 on Linux). "–" is under 0.05 ms (Windows) or 0.1 ms (Linux).

| Kind (rate) | c100 (Windows) | DeCrew (Windows) | U57 (Linux) |
|---|---|---|---|
| All buckets | 11.62 | 5.14 | 33.65 |
| `BrainScheduler` (render every tick; brains, timed apart as `brains`) | 2.01 | 1.14 | 4.91 |
| `Navigator` `UpdateNavigator` (sim every tick; moving along paths) | 1.69 | 0.42 | 1.44 |
| `ReachabilityMonitor` (1000 ms) | 0.36 | 0.25 | 3.09 |
| `RequireInputs` (200 ms) | 0.17 | 0.41 | 1.90 |
| `SolidTransferArm` batch (1000 ms) | – | 0.15 | 1.56 |
| `Sublimates` (200 ms, `fast-sublimation`'s batch) | 0.23 | 0.06 | 1.44 |
| `CritterTemperatureMonitor` (200 ms) | 0.19 | 0.08 | 1.25 |
| `RanchStation` `FindRanachable` (200 ms) | – | – | 1.07 |
| `AmountInstance` batch (200 ms) | 0.20 | 0.09 | 0.96 |
| `KComponentsInitializer` (200 ms; `DiseaseContainers`, `StructureTemperatureComponents`) | 0.52 | 0.16 | 0.66 |
| `SleepChore` `Work(Sleep)` (33 ms) | 0.51 | – | – |
| `FallMonitor` `CheckLanded` (33 ms) | 0.40 | – | – |
| `GasLiquidExposureMonitor` (33 ms) | 0.39 | – | 0.34 |
| `ChoreDriver` (200 ms, every brain) | 0.20 | – | 0.69 |
| `ConversationManager` (200 ms) | 0.20 | – | – |

soggy lab 2's Windows runs waited behind another agent's soak run for the shared lock and are
not in the table; its Linux load stops at the missing-mods dialog (see Method).

Linux shows the same order at about 2.5× the time (c100: all buckets 25.7, Navigator 3.5,
brains 3.3, sleep 1.6, KComponents 1.1, reachability 1.0 ms; U57: brains 4.9, reachability
3.1, `RequireInputs` 1.9, transfer arms 1.6, sublimation 1.4, Navigator 1.4, critter
temperature 1.3, ranch stations 1.1 ms).

Beyond the first ten, about 350 kinds each take under 0.15 ms: on c100 the sim 200 ms group
is 6.8-9.7 ms on Linux, of which the kinds above are under half. Gluttony's inventory found the
same spread (docs/research/gluttony-frame-inventory.md).

What the samples show inside the kinds (Linux, shares of the main thread):
- Most of the time is in the kinds' own code: reads of scattered components, one or two
  dictionary lookups, a status item toggled. `RequireInputs.CheckRequirements` (DeCrew 2.3%
  self), `ReachabilityMonitor`'s update (1.2%), `StructureTemperatureComponents.Sim200ms`
  (1.0%, a walk of every building's header), `DiseaseContainers.Sim200ms` (c100 0.8%).
- Two costs repeat across kinds. `StateMachineController.GetSMI(Type)`, which every
  `GetSMI<T>()` reaches, walks the object's state machines with `IsAssignableFrom`:
  `Object.GetType` alone is 1.2% of c100's main thread, 1.0% of it under sleeping Duplicants'
  `Sleepable.OnWorkTick` (`GetSMI<StaminaMonitor.Instance>`), and ranch stations ask each
  critter for its `RanchableMonitor`. `Effects.Remove` walks both effect lists for ids that are
  not there: `GasLiquidExposureMonitor` removes both eye irritations from every Duplicant every
  33 ms (0.44% of c100's main thread in the walk).
- The buckets' dispatch (`UpdateBucketWithUpdater.Update`, the scheduler's updaters) is
  1-2% of the main thread, mostly the first touch of each entry's object.

### Candidates and verdicts

| Candidate | Verdict | Windows (paired, tweak on minus off) | Linux |
|---|---|---|---|
| `GetSMI(Type)` answers kept per controller (`fast-smi-lookups`) | **Landed**, exact, on by default | c100: sleep −0.202 ± 0.165 ms (n 22; 0.51 → 0.26), all state machines −1.08 ± 1.28; DeCrew: ranch stations −0.011 ± 0.004, sweep bots −0.023 ± 0.017, sleep −0.018 ± 0.023, all state machines +0.00 ± 0.38 | c100: sleep −1.09 ± 0.18 (n 4), all −4.3 ± 1.9; U57: ranch stations −0.30 ± 0.07 (n 2), all +2.1 ± 0.9 (n 2; see below) |
| Removals of absent effects skipped (`fast-effect-removal`, prototype) | **Rejected**: one save | c100: `GasLiquidExposureMonitor` −0.073 ± 0.034 (n 22; 0.28 → 0.21), all state machines +0.09 ± 0.73; DeCrew: −0.002 ± 0.008, all −0.03 ± 0.65 (n 22); U57 (stopped after 9 windows): −0.005 ± 0.015, all −0.39 ± 1.30 (n 7) | c100: `GasLiquidExposureMonitor` −0.21 ± 0.07 (n 5); DeCrew −0.03 ± 0.10 (n 5); U57 −0.00 ± 0.02 (n 3), all +2.1 ± 0.5 |
| `StructureTemperatureComponents.Sim200ms`: visit only dirty or active buildings | Not taken | DeCrew 0.16 ms all of `KComponentsInitializer` | DeCrew 0.7 ms (103k buildings) |
| `RequireInputs`, `ReachabilityMonitor`, `FallMonitor`, critter monitors: skip unchanged updates | Rejected | – | – |
| The buckets' dispatch | Rejected | – | – |
| `Navigator`'s movement | Rejected (not ours to skip) | – | – |
| Brains | Not ours here | – | – |

- **`fast-smi-lookups`** (src/OniMoono.GameTweaks/FastSmiLookups.cs): a lookup answered since
  the controller's `stateMachines` list last changed (its `List<T>._version`, which every
  change moves) returns that answer; the walk is the game's, and an answer is kept only once it
  has returned. Instances never change type, so the answer is the walk's. Verify mode
  (`ONI_TWEAK_VERIFY=1`) walks for every kept answer: c100 786k lookups, 724k from the cache,
  0 wrong; DeCrew 524k, 482k, 0 wrong (Linux). No Workshop mod patches `GetSMI(Type)` (raw
  scan of `out/ws`, the one hit decompiled); with one, the game's method runs.
- **`fast-effect-removal`** (prototype, not committed): a removal that changed neither of the
  `Effects`' lists found nothing and did nothing, so its id was kept as absent until either
  list's version moved, and a later removal of a kept id skipped. Exact (verify mode: c100
  5.24 M removals, 5.19 M skipped, 0 wrong; DeCrew 786k, 782k, 0 wrong), but the saving is in
  one monitor and grows with Duplicants: on c100 only. DeCrew's 15 and U57's 22 Duplicants
  show nothing, and every removal pays the lookup of its kept ids. ONI Together and Colony
  Cuisine V2 patch `Effects.Remove(HashedString)`.
- **U57 on Linux**: with either tweak on, the windows' state machine total read about 2 ms
  higher (n 2-3 a value), in every kind alike, also those that never call the tweaks' methods
  (critter temperature +0.055, rot +0.025 ms with `fast-effect-removal`). The machine's load
  moved the window totals by 10-20% between neighbours (other agents' players), so this is
  read as noise. On Windows, U57 with `fast-effect-removal` (9 windows, stopped early) shows no
  such rise (all state machines −0.39 ± 1.30, critter temperature −0.02 ± 0.06); the U57 Windows
  run of `fast-smi-lookups` waited behind another agent's soak run for the shared lock and did
  not run.
- **Frame rate**: `fast-smi-lookups` does not move the frame rate beyond its noise (Windows
  main thread, on vs off: c100 26.3 ± 3.0 vs 29.1 ± 5.5 ms; DeCrew 20.2 ± 4.4 vs 19.1 ± 4.1).
  Its gains are in the kinds it touches, 0.01-0.2 ms a frame on Windows, larger with more
  Duplicants and ranch stations.
- **`StructureTemperatureComponents`**: the walk over every building's header is cheap per
  building and matters only on DeCrew (103k buildings, 0.16 ms on Windows). An index of dirty
  and active headers would have to follow every `SetHeader` and the compacted vector's moves
  for 0.1 ms; not taken.
- **The kinds' own work** (`RequireInputs`, reachability, falls, critter temperature, ranch
  stations' eligibility): each reads the world through other components every update, and
  telling that nothing changed would read the same components. No exact skip found.
- **The buckets' dispatch**: every entry of a bucket shares one updater, but the per-entry cost
  is the first touch of the entry's object, which a specialised loop would not remove.
- **`Navigator` movement** (`TransitionDriver.UpdateTransition`, `AdvancePath`): moves
  transforms and checks the path every tick for moving Duplicants and critters; real work.
- **Brains** (`BrainScheduler`, the largest kind): fetch and chore searches, already the subject
  of `fast-offset-tables` and the gameplay tweak `brain-rate`; left out of this phase.

## `Game.StepTheSim` on soggy lab 2 with the reporter's mods (2026-11-03)

Issue #48's reporter (Ryzen 5 5600H laptop, GTX 1650) logged `Game.StepTheSim` at about 44 ms a
frame with Speed Control's fast speed, one 200 ms sim step a frame. The question: is that our
CPU being faster, or something specific?

### Method

- **Player**: `origin/master` at `30248850` (U59), `scripts/build-windows.sh release`, default
  tweaks, OpenGL, with PerfProbe from `u60` (`camera.txt busiest`, `walltime.txt`, `awake.txt`).
- **Game**: the frozen U59 744825 Windows install (`E:\oni-builds\oni-win-744825`).
- **Save and mods**: `soggy lab 2.sav` (cycle 806) with all 39 of the reporter's mods in their
  order (`out/slowsave/mods-template`): the 35 gated ones, Fast Track 0.18.6, Rocketry Expanded
  2.15.0, Automatic Geyser Calculation and Better Info Cards. One of them (3824495381) has no
  `mod_info.yaml` and does not load, as in the reporter's log.
- **Run**: one launch, 9800X3D, speed 3 (Speed Control asks for 10 game seconds a second; every
  frame hit the game's limit of one 200 ms step), busiest world 2 active, 1280x720 on monitor 2,
  `--no-focus`, High priority, sound off, `--frame-stats` with `ONI_BUCKET_PROFILE=1`. Twelve
  600-frame windows after the load.
- **Trace**: `dotnet-trace` (`Microsoft-DotNETCore-SampleProfiler`), 40 s from 130 s after the
  load. Native code shows as `UNMANAGED_CODE_TIME` under the P/Invoke's managed caller.
- **Machine**: other agents' Linux players ran (WSL load 5-9; 120-130 GiB committed of 154). In
  half the windows the main thread was on the CPU only 63-70% of the time, against 80-82% in the
  others, and SimDLL's time tripled in those windows.
- **The reporter's numbers** are PerfProbe walltime lines from their log (`Player.txt`). They
  loaded a later save, `soggy lab 2 after delete planet.sav` (cycle 906), which we do not have.
  Their build logs no SimDLL split.

### Results

ms a frame:

| | Reporter (5600H) | Ours, quiet windows (6) | Ours, loaded windows (6) |
|---|---|---|---|
| Frame (PerfProbe walltime) | 77-85 | 39-45 (fps 22-26) | 49-65 (fps 13-20) |
| `Game.SimEveryTick` | 51.4-55.6 | 17.9-21.4 | 29.2-38.4 |
| `Game.StepTheSim` | 43.7-47.1 | 7.0-8.6 | 16.0-21.5 |
| · in SimDLL | (not logged) | 5.6-7.1 | 14.1-19.6 |
| · managed | (not logged) | 1.4-1.7 | 1.8-2.0 |
| `SimEveryTick` without `UnsafeSim200ms` (state machines and the rest) | 7.3-10.1 | 10.3-12.5 | 12.3-16.5 |

Inside `Game.StepTheSim` (trace, taken in loaded windows; 20.7% of the main thread):

| Callee | Share of `StepTheSim` |
|---|---|
| `Sim.HandleMessage` (the sim step), native | 85% |
| `ConduitFlow.Sim200ms` | 3.5% |
| `EnergySim.EnergySim200ms` (Rocketry Expanded's `RTB_ModuleGenerator` 0.5%) | 3.4% |
| `CircuitManager.Sim200msLast` (Fast Track's `FastElectricalNetworkCalculator`) | 1.6% |
| `ElementConsumer.AddMassInternal` | 1.5% |
| `PlantElementAbsorbers.Sim200ms` | 1.0% |
| `SolidConduitFlow.Sim200ms` | 0.9% |
| `ConduitTemperatureManager.Sim200ms` | 0.7% |
| `StepTheSim`'s own code | 0.7% |
| GC polls and lock entries (sampling artifacts) | 7.8% |

- The game's handling of the step's results (substance and solid changes, callbacks, falling
  liquid, dug ore, FX, unstable ground, world damage, mass and temperature bookkeeping) is
  inlined into `StepTheSim`: all of it is the 0.7% of its own code, about 0.1 ms a frame.
- Mods inside `StepTheSim`: about 2% of it (0.2-0.4 ms). Fast Track's electrical network is
  1.6% and Rocketry Expanded's generators 0.5%. The other `_Patch` frames there are our
  frame-stats timers.
- The state machine buckets were 10.9-17.6 ms a frame (468-532 kinds). Navigator, the room
  prober, sublimation, critter temperature and brains led, each 0.7-1.2 ms.

### Verdict

CPU speed alone does not explain it:
- **Managed work takes about as long on both machines.** Their state machines and the rest of
  `SimEveryTick` take 7-10 ms against our 10-12. At PassMark's single-thread ratio (4,420 against
  2,867, 1.54× ([PassMark](https://www.cpubenchmark.net/compare/4274vs6344/AMD-Ryzen-5-5600H-vs-AMD-Ryzen-7-9800X3D)),
  an estimate), their save does less managed work than ours, or their CPU is faster than the
  rating says. Their save is a later one with one planet removed, so the work differs.
- **The native sim step is 5-7× slower there**: about 42 ms (44 less about 2 ms managed) against
  our 5.6-7.1 ms in quiet windows. Against our loaded windows (14-20 ms) the gap is 2-3×, which
  also beats 1.54×.
- **SimDLL is sensitive to the machine.** Its time tripled in our loaded windows while managed
  code grew about 1.4×. SimDLL imports `CreateThread` and condition-variable waits, so it likely
  runs part of the step on its own threads, and the main thread waits for them. A 6-core laptop
  with less cache (16 MB L3 against 96 MB) would suffer the same way, more so with the game's
  other threads busy. This is a likely explanation, not a measured one.
- Nothing in the managed part of `StepTheSim`, ours or the mods', is large enough to matter
  (about 1.5-2 ms of 7-21).

To settle it, the reporter could send `soggy lab 2 after delete planet.sav` and a log from a
current build. Its `sim` line splits `StepTheSim` into SimDLL and managed time on their machine.
Our run's data: `out/soggy/runs/soggy1.*` in worktree `agent-a6e1bc274ec67af1a` (log,
nettrace, speedscope, `stepthesim.txt`).

## Final comparison (2026-11-04)

The last U60 release against the current `u60`, with candidate 3, on a quiet machine.

### Method

- **Builds**: base is release v2026.11.03.03 (`529b58c1`), change is `u60` at `ff1571c9`. Both
  built with `scripts/build-windows.sh release` and run on their defaults (Vulkan, default
  tweaks), with the same PerfProbe.
- **Game**: the frozen U60 761211 Windows install, with the all-DLC Steam double.
- **Saves**: as in the baseline. soggy lab 2 has its 35 U60-loading mods, which load without
  the mod-differences dialog. Fast Track 0.18.6 and Rocketry Expanded 2.15.0 still fail on
  761211: Fast Track's patch of `LocText.ParseText` and Rocketry Expanded's
  `FixesVanillaCrashOnPlanetSelection` throw in Harmony, and the game's mod-error dialog quits
  after 3 frames (found in the 2026-11-03 run).
- **Runs**: one launch per run, base, change, base, change for each save, after one unmeasured
  launch of each build. Speed 3, busiest world, 1280x720 windowed on monitor 2, `--no-focus`,
  High priority, sound off, `--frame-stats`. Each run lasts 130 s after the busiest world is
  made active. The numbers are means over the 100 s from 30 s after that: PerfProbe's
  600-frame windows for fps and the slowest 1%, frame stats' 60-frame windows for the main
  thread (the sum of its phases) and the device thread. Memory is the last memory line, about
  130 s in. Load is from `Loading save file` to the colony's first frame.
- **Quiet machine**: each run started only after a minute with no other e2e player, QA lane or
  Linux player and a WSL load under 2. A run during which one started, or the load reached 3,
  would have been run again; none was. The highest WSL load while measuring was 0.14-2.02.
  Frame stats' machine lines: CPU clock 4.70 GHz throughout, Balanced power plan, the
  player's process on 2.3-4.6 cores, its main thread on the CPU 90-99% of the time (soggy lab
  2 67-72%, where it waits on the sim's threads), 35-55 GiB of RAM free.

### Results

Per run, base → change, the two pairs in launch order:

| | DeCrew | soggy lab 2 (35 mods) | c100 | U57 |
|---|---|---|---|---|
| Busiest world | 0 | 2 | 0 | 0 |
| Frame rate (fps) | 162.0, 152.8 → 158.3, 165.4 | 34.9, 35.4 → 34.4, 35.4 | 108.9, 106.4 → 118.8, 120.5 | 89.4, 88.4 → 93.1, 96.9 |
| Slowest 1% of frames (ms) | 19.0, 19.9 → 20.6, 19.1 | 61.0, 55.3 → 56.1, 54.1 | 19.3, 19.3 → 19.5, 19.2 | 31.9, 33.4 → 28.8, 27.7 |
| Main thread (ms) | 6.14, 6.49 → 6.32, 6.00 | 28.57, 28.22 → 28.99, 28.09 | 9.17, 9.48 → 8.46, 8.31 | 11.08, 11.24 → 10.70, 10.32 |
| Device thread busy (ms) | 1.76, 2.00 → 1.82, 1.81 | 4.12, 4.00 → 4.20, 3.85 | 3.17, 3.15 → 2.69, 2.68 | 1.44, 1.46 → 1.47, 1.33 |
| Private bytes (MiB) | 17,120, 18,113 → 17,110, 16,761 | 14,564, 14,257 → 12,678, 12,034 | 15,316, 15,240 → 9,912, 9,898 | 16,398, 15,949 → 14,638, 13,705 |
| Managed heap (MiB) | 8,796, 9,440 → 8,399, 8,068 | 5,981, 5,853 → 4,317, 3,859 | 8,789, 8,721 → 3,333, 3,320 | 9,535, 9,097 → 7,324, 7,162 |
| Load (s) | 17.1, 16.2 → 16.6, 16.4 | 14.3, 14.0 → 13.7, 14.2 | 9.3, 9.6 → 9.3, 9.7 | 18.3, 18.3 → 17.9, 17.7 |

- **Memory**: the managed heap is 5.3 GiB smaller on c100 (means of the two runs), 1.8 GiB on
  soggy lab 2, 2.0 GiB on U57 and 0.9 GiB on DeCrew. Private bytes are 5.2, 2.0, 2.0 and 0.7
  GiB smaller. The heap's size at a given moment also depends on when the last collection ran
  (DeCrew's two base runs differ by 0.6 GiB).
- **c100 is faster in both pairs**: 107.6 → 119.7 fps (means), the main thread 9.33 → 8.39
  ms and the device thread 3.16 → 2.69 ms. The two runs of each build are within 2.5 fps of
  each other. The main and device thread drops are the size candidate 3 measured on its own
  (-0.9 and -0.4 to -0.5 ms, above), but this comparison does not separate it from the other
  changes.
- **U57 is a little faster in both pairs**: 88.9 → 95.0 fps, the main thread 11.16 → 10.51 ms
  and the slowest 1% 32.7 → 28.3 ms. Its runs of one build differ by 1.0 fps (base) and 3.9
  fps (change). With two runs each, that is a small gain, not a measured size.
- **DeCrew and soggy lab 2 are within the noise** on frame rate, the slowest 1%, the main
  thread and the device thread. Load time moved by at most 0.5 s (U57), too little to call
  from two runs each.
- **Noise**: between two runs of the same build, DeCrew differed by 9.2 fps (base) and 7.1
  (change), c100 by 2.5 and 1.7, U57 by 1.0 and 3.9, soggy lab 2 by 0.5 and 1.0. Within a run,
  the 600-frame windows spread over 134-178 fps on DeCrew, 83-142 on c100, 82-103 on U57 and
  33-36 on soggy lab 2. The 2026-11-03 run saw a 17 fps spread between two c100 runs of one
  build, so two runs each can still mislead.
- **Earlier run**: the 2026-11-03 run, with `u60` at `dc48d2d2` (before candidate 3), found
  the same memory drop (heap 1.4-1.9 GiB smaller on DeCrew, soggy lab 2 and U57, 5.3 GiB on
  c100) and frame rate within the noise on every save (c100 92.7, 109.9 → 94.5, 104.9 fps;
  DeCrew 152.6, 136.4 → 149.7, 150.5).

### What differs between the two builds

From `git log v2026.11.03.03..ff1571c9`. The release is `a51a116a` with fixes cherry-picked
from `u60` (path-probe-repair's change feed, the restarter's arguments, the tweaks search), so
those are in both builds. What only the change build has:

- **Property-texture upload skip** (candidate 3, `RawTextureLoads`): `fd149794`, `ff1571c9`.
- **Paged path grids** (`paged-path-grids`, on by default): `c2bfa28c`, `643cd13e`,
  `0b958e8d`. Its verify mode waits for writes to end (`23e4a2f4`); the write counts it adds
  are kept only in verify mode.
- **Bundles read block by block** (`1fb784f9`), after the mapped bundle cache (`38f9f5e1`) was
  reverted (`daedb53f`).
- **Push descriptor templates** on Vulkan (`7ef39374`, 8e above). Identities first and the
  ready stamp (8a, 8c) are in both builds.
- **Garbage collection option** (`38ce49d4`): its default, fast, is the Server GC both builds
  use, so it changes nothing here.
- **VP8 decoder compiled optimized** from its first call (`1f5aafb9`): the intro video, which
  these runs do not play.
- Not in play at defaults: the Vulkan draw verify mode and `ONI_CAPTURE_TWICE=full-draws`
  (`6ee47554`, `70fd25be`), `ONI_VERIFY_RAW_LOADS` and `ONI_CAPTURE_TWICE=full-uploads`, and
  frame stats naming the longest GC pause and peak memory (`36c08d03`).

The memory changes are not measured apart here. Their own measurements (Verdicts) are of the
same size: block reads took 1.0-1.6 GiB off the heap on c100 and DeCrew, and paged path grids
4.3 GiB on c100; the grids' arrays were 1.0-1.1 GB → about 50 MB on soggy lab 2 without mods
and 266 → 178 MB on DeCrew. Nor are the frame-rate gains on c100 and U57 measured apart from
the rest.

## Next

1. Put stopwatches on candidates 1-3: `ApplyOverrides` calls by cause, the queued loop in
   `Cull`, and `LoadRaw`'s copy with the changed fraction. Then prototype the ones whose
   measured share holds up.
2. Re-measure this baseline on a quiet machine (no Linux players) before comparing fps.
   Until then, compare main-thread phase times within paired runs.
