# U60: what changed for Moono

U60 is the game's November 2026 quality-of-life update. It is in public testing as build 761015 on
Steam's `public_testing` branch, and is built with Unity 6000.3.17f1 (U59: 744825, Unity
6000.3.5f2). This branch (`u60`) supports U60 only. Master stays on U59 until U60 ships, then
`u60` merges into it.

Sources:
- Klei's decompiled game code for both builds in oni-versioning (`aa9baa00` U59, `4a63c523` U60).
- The public test's patch notes.
- `tools/AssemblyDiff`, `tools/ApiCheck` and the player's `--check-*` modes run against the Windows
  U60 install.

## Status

| Area | State |
|---|---|
| Native data layouts | Done: 6000.3.17f1 type trees. Asset, bind and shader checks pass on Windows U60. |
| Version check | Done on `u60` and master: a Moono build names the game update it needs. |
| Engine API | Done: ApiCheck back to the 13 known UI Toolkit members. |
| UI packages | Done: `TMP_Text.SetCharArray(ReadOnlySpan<char>)` from the spec; package ApiCheck 0 missing. |
| Game tweaks | Static review done (2 fixed, overlays left out where U60 does their work); runtime checks need a U60 player. |
| Linux build | Done: frozen at `~/projects/oni-linux-761015`, DLC content copied (not linked) from the frozen Windows copy, test install `out/local-tools/u60orig`. |
| Main menu and a save on U60 | Both: Linux (QA dogfood: menu, DeCrew and new game, all 13 scenarios clean) and Windows (frozen copy `E:\oni-builds\oni-win-761015`, RTX 3090, OpenGL 4.5: main menu, DeCrew loaded and dismissed its mod dialog, no errors). |
| Performance | Windows, focused (see Performance): ours 64.9 fps against the original's 11.9 on U60 (about 5.5×). Linux, 2026-10-31: the save tweaks, fast-jobs, fast-anim-events and fast-conveyor-paths still pay; the other exact tweaks' gains are below what this machine resolves now. Frame rates do not compare: the machine was GPU-bound at about 6 fps in every configuration, U59 included. | Peak memory: see Memory.
| QA and e2e on U60 | QA: unit and all 13 dogfood scenarios pass (references blessed from a reviewed U60 run); verify (U57, every tweak's verify mode) ran 840 game seconds with no mismatches and no errors logged. e2e: not yet run. |

## The build

- **Development build:**
  - The public test is a Unity development build. Every managed assembly ships with a `.pdb`, and
    `boot.config` gains `player-connection-*` lines.
  - Unity compiles its assertion and profiler calls into such builds only. The game, uGUI and
    Timeline therefore call `Debug.Assert`, `Assertions.Assert` and `Profiling.Profiler`. A release
    build of U60 may not.
  - Moono treats these development-only calls as a release build does: assertions, profiler
    samples and `Debug.isDebugBuild` (false) do nothing. The public test is a release candidate,
    and emulating its debug behaviour would only matter until U60 ships as a release build.
- **`boot.config`:** the graphics threading mode goes from 4 to 6, the native graphics-jobs line is
  gone, and `gc-max-time-slice=3` is new (Unity's incremental GC). None of these is read by Moono:
  CoreCLR's background GC replaces Unity's.
- **Unoptimised game code:** the public test's `Assembly-CSharp` is compiled without
  optimisation. It is full of `nop`s and keeps comparisons in locals; `SaveLoader.Save` alone
  has 75 `nop`s, and U59 had none. Most of the decomp diff in untouched methods is this compiler
  shape. Transpilers must be checked against U60's IL, not the C#. Frame rates measured on the
  public test understate a release build of U60, for vanilla and Moono alike, since both run the
  same game code.
- **`[Debuggable(DisableOptimizations)]`:** the game's assemblies and the managed plugins also
  carry DebuggingModes 0x107 (U59: 0x2). CoreCLR honoured it and compiled the whole game without
  optimisation (U57's main thread 85 ms a frame against 54); Mono ignores that form of the
  attribute. Moono now ignores it too (ARCHITECTURE D56, `MonoJitOptimizer`).
- **New Unity modules:** `UnityEngine.AMDModule`, `UnityEngine.NVIDIAModule` (FSR2 and DLSS) and
  `UnityEngine.InsightsModule`. Nothing in the game references them.

## Native data layouts

Six native classes changed layout between 6000.3.5f2 and 6000.3.17f1. Two of them are read at start-up:

- **`PlayerSettings`:** gained `metalUseMetalDisplayLink`.
- **`PhysicsManager`:** gained `m_IncrementalStaticBroadphase` (version 22 to 23).

The others (`Terrain`, `GraphicsStateCollection`, `ArticulationBody`, `VisualEffectAsset`) are not
in the game's data. With the 6000.3.5f2 trees the player stopped in `ProjectSettings.Load` on an
array count read out of step. The branch now carries the 6000.3.17f1 trees from AssetRipper's
TypeTreeDumps, compacted by `tools/TypeTrees/compact.py` (it reproduces the old file byte for byte).

## Version check

`GameVersionCheck` (host) compares the Unity version in the `globalgamemanagers` header with the
type trees the build carries, before anything loads. On a mismatch it names both game updates and
the build to get. The message goes to the log and stderr, and on Windows to a message box, since a
start from Steam has no console. Unity version stands for game update because a different Unity
version is exactly what breaks the asset reader.

## Engine API

Members U60 needs that U59 did not:

- `Debug.Assert` (three overloads), `Debug.AssertFormat` and `Assertions.Assert.IsTrue`: no-ops,
  as in a release build.
- `Profiling.Profiler.BeginSample(string)` and `EndSample()`: no-ops.
- `ProfilerRecorder.LastValue`: `PerformanceCaptureMonitor` reads it for "Gfx Used Memory", "Draw
  Calls Count" and "SetPass Calls Count", which report 0 here as before.
- `Resources.UnloadAsset`: not a development-build call. U60's `VistaLayers` loads the new
  starting-biome vista textures on demand and unloads those it stops showing (the patch notes'
  "vista textures ... only load them on demand"). Moono unloads as Unity does: the texture's
  texels and device copy are freed, the object stays valid with its instance id, and its next
  use (a draw, a texel read or write, Resources.Load handing it out again) reads it from its
  asset file. Run-time textures and asset-bundle textures keep their data, as do the sprites
  (little memory). The vistas are entities a new U60 colony gets: the U57 save has none and
  makes no UnloadAsset call, so the QA perf peak does not change with it (see Memory).

Other CoreModule additions (`Random.onUnitCircle`, `JobHandle` equality, `SpriteRenderer` shader
user values, `SystemInfo.SupportsTypedUAVShaderLoadStoreOnGraphicsFormat`) are not referenced by the
game.

## Memory

Peak resident memory on the U57 save, OpenGL on Mesa's d3d12 driver, 1024x768, measured on
2026-10-30. Every run goes to frame 3724 (PERF line); the peak comes with the first autosave, a
new day about 2,500 frames after the colony is up, and nothing after it raised a run by more
than 50 MB (full 4,800-frame runs: ours U59 16,395 MB, U60 16,794 MB). Two ways of loading: the
save loaded by PerfProbe's load.txt (colony up at frame 124), and the QA perf harness's menu clicks
(colony up at frame 905, autosave near 3,360).

| Player | load.txt U59 | load.txt U60 | QA harness U59 | QA harness U60 |
|---|---|---|---|---|
| Unity's | 13,065 MB | 15,093 MB | | |
| Moono | 16,344 MB | 16,794 MB (16,781 with the unload) | 15,853 MB | 16,187 MB (with the unload) |

- Unity's own player grows by 2.0 GB on U60, most of it there right after the load (13.5 GB
  against 11.1). Moono grows by 0.45 GB: its GC heap is about 170 MB larger and commits 445 MB
  more; its textures take less (3.27 GB on the device at frame 1200, against 3.42 on U59).
- Moono's file-backed resident memory is 2.6–2.8 GB (the asset files it maps); Unity's is under
  200 MB.
- QA's 14.0–14.6 GB on master are runs of 2026-10-22 to 10-24 (13.9–15.2 GB). The same harness,
  cut at frame 3724, gives 15.85 GB on master today (8ac69537): master has grown since, and the
  U60 branch adds 0.3–0.45 GB on top of it, not 2.3 GB.

## UI packages

- **uGUI:** no change.
- **TextMeshPro:**
  - `TMP_Text.SetCharArray(ReadOnlySpan<char>)` is new, and U60's `LocText.SetContent` uses it for
    most UI text (spec: `docs/cleanroom/textmeshpro.md`, U60 section).
  - The shipped `TMP_InputField` now calls `GetFocus`, `GetWindowRect` and `ClientToScreen`,
    probably to place the IME candidate window. To be measured on Windows before anything is
    specified (relates to issue #30).

## Host and platform items from the patch notes

- **Steam's loose-file mod format:**
  - `KMod.Steam` now installs a Workshop item from a folder (`Directory`) when it has one, else
    from the legacy zip. It re-downloads corrupt items.
  - Our hooks still match: `KMod.DLLLoader.LoadDLLs` has one `Assembly.LoadFrom` (ModAccess's
    transpiler), and `KMod.Local.Subscribe` is unchanged (ModVersionStamp).
  - A loose-file Workshop mod still needs a runtime check.
- **VYaml 1.2 to 1.4:** loaded from the game's `Managed` folder with no special handling. POI
  templates now go through it, so new-game worldgen covers it at runtime.
- **Incremental GC:** a Unity player setting plus `gc-max-time-slice`. The game's code adds no
  `GarbageCollector` calls. Not applicable to CoreCLR.
- **IMGUI developer tools on Linux:** to be checked with the clean-room IMGUI once the Linux build runs.
- **Vulkan as the Linux default:** U60's data carries Unity's Vulkan programs as well as the
  OpenGL ones. Moono's `u60` renders with Vulkan by default on Linux and Windows, running those
  shipped programs, and falls back to OpenGL where Vulkan does not start (ARCHITECTURE §3.5,
  D54). `--check-shaders` compiles all 120 shaders' OpenGL programs with no errors on the Linux
  and Windows U60 data.
- **Linux restarter fix:** `App.Restart` still starts `Restarter` beside the game with the same
  arguments (only the compiler shape changed), so Klei's fix is inside their Restarter binary
  (12.5 MB in U59, 2.3 MB in U60). Moono's own `src/OniMoono.Restarter` replaces it; no change.
- **Plugin layout (Linux):**
  - U60's Linux build moves its plugins into `Plugins/x86_64/` and keeps `libsteam_api.so` in
    `Plugins/AnyCPU/`. Moono looked in `x86_64` only, so on a real install SteamAPI.Init would
    have failed and the game quit. AnyCPU is now searched too (`GameLayout`).
  - FMOD is the logging build, `libfmodstudioL.so` (`fmodstudioL.dll` on Windows); all 571 of
    its entry points resolve.
  - The Windows build adds `Plugins/ARM64/` (Windows on ARM).

## Game tweaks

Each area's review is in `u60-tweaks-<area>.md` (anim, spatial, overlays, sim, save). Each table
lists the game members a tweak touches, what U60 changed (with decomp line numbers), the verdict,
and the runtime check that confirms it once a U60 player runs.

| Area | Tweaks | Verdict |
|---|---|---|
| Animation | fast-anim-batches, fast-anim-cleanup, fast-anim-events, fast-symbol-overrides | fast-anim-batches **updated**. U60 renamed `GetPostProcessingParams` to `GetCustomShaderParams`, so the tweak had switched itself off; a missing write-path method now stops the install. The other three are unchanged. |
| Spatial | fast-partitioner-cleanup, fast-offset-tables, off-world-fallers, fast-particle-collision | Unchanged. Every transpiler anchor still matches U60's unoptimised IL. `OffsetTracker` now keeps a validity flag, cleared only from paths the tweak already patches. |
| Overlays | fast-overlays (power, temperature, targets), fast-overlay-textures | **Retired** on this branch (removed). Evidence: U60 caches circuit answers in `Power.Update`, colours only visible infrared visualizers, moved the target helpers, and `MultipleRenderTargetProxy` manages its copies. On U60 fast-overlays would have stopped start-up (ambiguous `AddTargetIfVisible`, a missing handler). Removed on the user's call rather than after a performance comparison. |
| Simulation | fast-sublimation, fast-conveyor-paths, offscreen-conveyors, brain-rate, brain-rate-allowance, slow-reachability, fast-jobs | Unchanged. The differences are compiler shape or the event-system move, which none of them depends on. |
| Save | fast-save-type-checks, background-save, fast-save-compression, fast-serialization, no-save-gc | Unchanged for U60. Two hardening fixes: fast-serialization checks its KSerialization members at install, and background-save now waits for a save in progress before the load screen's cloud/local migration (a gap that U59 had too). |

Engine optimizations Klei's U60 work touches:
- **Canvas mask skips:** our canvas skips graphics clipped out of sight by rect masks. U59's
  Consumables screen gave it 100,000 such graphics as soon as it opened. U60's Consumables,
  Vitals and Jobs screens don't reach that in the same QA run, which fits the patch notes'
  "significantly improved scrolling performance on the Consumables and Priorities screens".
  The research tree still does. The skip is still correct (0 wrong); it simply has less to skip
  on those screens.

Done since: each review's runtime checks (QA verify on U57, every tweak's verify mode, 0
mismatches; the counts are in Performance below) and the overlay frame times (Performance).

Open checks:
- `TMP_Text.SetCharArray` with unchanged text: ours now skips the update when the characters
  equal the current text. Whether Unity's package does the same (layout and events) is to be
  measured in the original.

## Performance

**Windows, the game window focused (2026-10-31).** Native Windows 11, Ryzen 7 9800X3D, RTX 3090
(driver 610.88), U57 save at speed 3, 1280×720 windowed on the second monitor, the game window
focused at High priority with power throttling off, as PERFORMANCE.md's numbers were taken. Short
runs (3,600 frames; the 600-frame windows after the first three), so treat the figures as ±10%.

| | Frame rate | Against the original |
|---|---|---|
| Original U60 (761015) | 11.9 fps | |
| Ours, U60 release build (8ce5a5f7: Vulkan, DXGI, default tweaks) | 64.9 fps | about 5.5× |
| Ours, master on U59 (80e375f1, OpenGL, DXGI, default tweaks) | 73.5 fps | about 6.1× (original U59: 12.1) |

- Runs with the window unfocused under `--background` (pointer pinned at (0,0), input ignored)
  came out 13–40% slower for ours (EventSystem.Update 0.2–0.8 ms against 0.02): frame rates are
  taken focused.
- The UI canvas phase (post-late) is 2.3–2.6 ms a frame against 0.42 ms on 2026-10-22 (U59) in
  both builds; when it grew is still to be bisected.
- U60 is about 12% below U59 for ours in these short runs while the original is level; U60's
  game code against Vulkan + DXGI is not yet separated.

The Linux numbers below are CPU-side direction only.

Measured on Linux (WSL2, Mesa's d3d12 driver on the RTX 3090), 2026-10-31, `u60-perf` (u60
with `MonoJitOptimizer`, D56): U57 resumed through PerfProbe's `load.txt`, 1024×768 on Xvfb,
speed 3, vsync off (`awake.txt`), each run under `perf.lock` on a quiet machine (under 2 cores
busy when it started; the load averages are given). Frame rates are the 600-frame windows after
the first three, leaving out those holding an autosave; percentiles are over those windows'
frames (`frametimes.txt`). Two runs of each configuration, interleaved. The vanilla player is
the frozen U60 Linux build (`out/local-tools/u60orig`) on llvmpipe, Mesa's software
rasteriser, which runs it 3–4 times faster than the d3d12 driver here (tests/e2e/README.md,
"Run time"). U60's development build understates a release build's speed for both players.

**The machine was GPU-bound, U59 included.** The GPU stayed in its idle state while the players
ran (`nvidia-smi`: P8, 210 MHz graphics, 405 MHz memory, throttle reason "idle", 15–25%
busy). Ours spent 155–165 ms a frame on the GL thread, nearly all in texture uploads through the
driver: the four 607×140 world textures `PropertyTextures` re-uploads every frame (Flow, Liquid
and others, filled by the sim) cost 20–50 ms each. Master's player on U59 measured the same way
was no different (GL thread 153 ms, the same uploads, `Game.Update` 21.5 ms), against 37–43 fps
in QA's perf runs on 2026-10-23/24. So every configuration below ran at about 6 fps and the
frame rates say nothing about the tweaks or the players; the main thread's own time (the
engine's `--frame-stats`, at the same frame rate in every run), PerfProbe's stopwatches, the
autosave freeze and the load are CPU-bound and do compare. Frame rates need a rerun on a
machine whose GPU clocks up (Windows, or this one once it does).

| U57, Linux | Frame rate | Frame time p50 / p95 / p99 | Load average (start; mean) |
|---|---|---|---|
| Vanilla U60 (llvmpipe) | 6.18–6.21 fps | 153–154 / 209–211 / 222–224 ms | 2.5–4.6; 7.7–8.8 (llvmpipe's threads) |
| Ours, default (`exact`) | 6.24–6.44 fps | 151–157 / 193–195 / 308–320 ms | 1.9–2.3; 1.7–3.8 |
| Ours, every tweak | 6.41–6.43 fps | 152 / 196–198 / 297–304 ms | 1.5–6.3; 1.8–2.3 |
| Ours, no tweaks | 6.48–7.07 fps | 147–152 / 182–191 / 312–324 ms | 1.4; 1.5–1.8 |
| Ours, default, Debuggable honoured | 5.98–6.71 fps | 146–164 / 197–220 / 277–318 ms | 1.6–2.2; 2.1–2.6 |

The main thread at that frame rate (ms a frame; the total is the frame phases' sum, the scripts
are the engine's script times; two runs, the total as a range, the scripts as means):

| Configuration | Main thread | `Game.Update` | `Game.LateUpdate` | `Global.LateUpdate` | `Global.Update` | `PropertyTextures.LateUpdate` | `World.LateUpdate` |
|---|---|---|---|---|---|---|---|
| No tweaks | 57.5–58.3 | 26.6 | 6.05 | 2.47 | 2.11 | 1.83 | 0.88 |
| Default (`exact`) | 53.4–53.8 | 26.8 | 6.03 | 2.48 | 1.39 | 0.56 | 1.02 |
| Every tweak | 48.8–50.7 | 24.3 | 5.61 | 1.93 | 1.45 | 0.55 | 1.15 |
| Default without fast-jobs | 53.7–59.7 | 27.5 | 6.40 | 2.46 | 1.42 | 1.86 | 0.93 |
| Default without the anim tweaks | 51.7–54.1 | 24.9 | 5.99 | 2.51 | 2.03 | 0.55 | 0.89 |
| Default without the spatial tweaks | 53.1–56.0 | 27.0 | 6.22 | 2.36 | 1.42 | 0.56 | 0.83 |
| Default without fast-sublimation | 52.7–54.0 | 26.8 | 6.00 | 2.56 | 1.43 | 0.57 | 1.15 |
| Default, Debuggable honoured | 84.6–85.9 | 48.2 | 11.34 | 4.08 | 1.96 | 0.82 | 1.44 |

The script times move by about 0.3 ms between runs of one configuration (`Game.Update` by up to
2 ms), so gains under that need the stopwatches. PerfProbe's (`walltime.txt`, one run each,
eight windows), the default against the default without the spatial tweaks and
fast-sublimation:

| Method (ms a frame) | Default | Without | Tweak |
|---|---|---|---|
| `Sublimates.Sim200ms` | 0.52 (602 calls) | 1.64 (2,452 calls) | fast-sublimation |
| `ScenePartitioner.Sim1000ms` | 0.13 | 0.61 | fast-partitioner-cleanup |
| `HighEnergyParticle.CheckCollision` | 0.12 | 0.41 | fast-particle-collision |
| `FetchManager.UpdatePickups` | 1.40 | 1.54 | fast-offset-tables |
| `Game.SimEveryTick` (all of them inside) | 22.3 | 24.7 | |

| Autosave and load | Autosave freeze (longest frame) | Load |
|---|---|---|
| Vanilla U60 (llvmpipe) | 47.3–48.0 s (one run) | 141–160 s |
| Ours, no tweaks | 8.5–8.8 s | 27–30 s |
| Ours, default | 2.6–2.9 s | 26–27 s |
| Ours, default without the save tweaks | 8.1–8.8 s | 27–29 s |
| Ours, default, Debuggable honoured | 3.6–3.9 s | 31–36 s |

The freeze is the second and third of PerfProbe's three autosaves (`autosave.txt`) in each run.
The first one takes 31–33 s in ours and 50 s in vanilla in every configuration: it also draws the
colony preview for the first time, which this GPU state makes slow. Vanilla's freeze is on
llvmpipe and on this machine (its U59 freeze was 26 s on Windows, PERFORMANCE.md).

fast-conveyor-paths (PerfProbe's `rebuild.txt`, one run each): the load's conveyor rebuild 890 ms
without it, 15 ms with it; five rebuilds after the load 113–127 ms without, 2–14 ms with.

Overlays, paused, 1200 frames each, the second 600-frame window (ms a frame; one run each):

| | None | Power | Temperature | Oxygen |
|---|---|---|---|---|
| Ours (default) | 27.2 | 31.9 | 29.8 | 26.5 |
| Vanilla U60 (llvmpipe) | 127.2–127.4 | 130.9 | 142.4 | 127.3 |

Ours' None is the sweep's last window; its first, before the others, was still warming up
(58 ms). Power costs ours 4.7 ms over None and vanilla 3.7 ms; Temperature 2.6 ms against
vanilla's 15 ms, much of which is llvmpipe drawing the overlay. Nothing here suggests the
retired overlay tweaks left a cost Klei's U60 work does not cover; a Windows comparison would
settle it with the GPU out of the way.

QA's verify run on U60 (2026-10-30, every tweak's verify mode, 829 game seconds) shows each
exact tweak still has its work to skip: fast-anim-events 30,000,000 entries checked, fast-anim-cleanup
73,717 chunks skipped against 6,283 swept, fast-anim-batches 1,733,521 controllers written ahead,
fast-sublimation 9,000,000 updates skipped against 2,953,997 run, fast-particle-collision 99,997
gathers skipped against 3 made, fast-partitioner-cleanup 2,029,281 entries dropped,
fast-offset-tables 9,430 items recomputed in 4,000 updates, fast-conveyor-paths 735 paths, all with
0 wrong.

Per tweak:

| Tweak | U60 evidence | Verdict |
|---|---|---|
| Save tweaks (fast-serialization, fast-save-type-checks, fast-save-compression, no-save-gc, background-save) | autosave freeze 8.1–8.8 → 2.6–2.9 s | Keep |
| fast-jobs | `PropertyTextures.LateUpdate` 1.86 → 0.56 ms | Keep |
| fast-anim-events | `Global.Update` 2.03 → 1.39 ms | Keep |
| fast-conveyor-paths | a rebuild 113–127 → 2–14 ms, the load's 890 → 15 ms | Keep |
| fast-sublimation | `Sublimates.Sim200ms` 1.64 → 0.52 ms | Keep |
| fast-partitioner-cleanup | `ScenePartitioner.Sim1000ms` 0.61 → 0.13 ms | Keep |
| fast-particle-collision | `CheckCollision` 0.41 → 0.12 ms | Keep |
| fast-offset-tables | `UpdatePickups` 1.54 → 1.40 ms (U59 on Linux: 1.74 → 1.33) | Keep; smaller gain than on U59 |
| fast-anim-batches, fast-symbol-overrides | `World.LateUpdate` 0.88–0.89 ms without the anim tweaks, 1.02 with them | Smaller gain; no gain measured here. Re-measure CPU-bound (Gluttony, where fast-anim-batches was measured, or Windows) before retiring; a candidate to retire if that agrees |
| fast-anim-cleanup | `Global.LateUpdate` 2.51 without the anim tweaks, 2.48 with (its sweep was 0.12 ms on U59) | Smaller gain, under the noise; keep (verify shows 92% of chunks skipped) |
| brain-rate, slow-reachability, off-screen-conveyors (every tweak against the default) | main thread 53.6 → 49.8 ms; `Game.Update` 26.8 → 24.3, `Global.LateUpdate` 2.48 → 1.93 | Keep as opt-in |

The fast-jobs gain under-reads here: it saves waking worker threads for small jobs, which costs
most when the frame is short, and this GPU state made it long.

**Memory.** `qa.sh --memory` on ours U60 (every tweak, stopped once the first autosave has
passed and memory has settled) peaked at 16,109 and 16,654 MB (RssAnon 13.3–13.9 GB, RssFile
2.6 GB); the perf stage's run on 2026-10-30 reached 16,802 MB. The bench runs above, which go on
longer (three autosaves, or 6,600 frames), peaked at 16.0–18.2 GB with the default or every tweak.
Vanilla U60 peaks about 15.1 GB against 13.1 GB for vanilla U59: the update itself adds about
2 GB. (Vanilla on llvmpipe peaked at 19.6–21.1 GB here, which counts the software rasteriser's
copies of every texture, so it does not compare.) One of the two memory runs failed U59's 16 GB
limit, so u60's `qa.sh` now allows 18 GB (`mem_limit_mb`, `QA_MEMORY_LIMIT_MB` to change it):
U59's limit plus what U60 adds in vanilla, about 1.2 GB above the highest of the stages' peaks
on U60.
