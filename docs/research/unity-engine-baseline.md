# Unity's engine as a baseline: the original's frame against Moono's (2026-11-14)

How a frame of the original game (Unity's player) divides between Unity's engine, the game's
managed code and the rest, beside Moono's split for the same scene. It answers whether the
5 ms a frame that Moono's engine takes ([frame-inventory-2026-11.md](frame-inventory-2026-11.md))
is good.

Clean-room rule: what this page says about Unity comes from profiling the shipped player and from
public documentation. `UnityPlayer.so` is stripped, so its functions appear as addresses; they are
labelled by what was measured under them (which script callbacks they call), never by reading
Unity's code.

## The short version

Late-game colony (U57), Linux, speed 3 asked, main thread, ms a frame:

| | Original | Moono, no tweaks | Moono, default tweaks |
|---|---|---|---|
| Frame | 130.9 ms (7.6 fps) | 34.1 ms (29.3 fps) | 31.4 ms (31.9 fps) |
| Game seconds a real second | 1.58 | 2.96 | 2.97 |
| Game code (with the BCL and runtime under it) | 62.2 | 22.4 | 20.5 (+1.5 tweaks) |
| Engine calls the game code made | 34.9 | 1.2 | 1.4 |
| Engine work nobody asked for (rendering, canvas, script dispatch) | 7.0 | 4.0 | 4.1 |
| uGUI/TextMeshPro with the engine calls it made | 5.2 | 0.8 | 1.1 |
| GC on the stack | 0.4 | 0.0 | 0.0 |
| **Engine-owned, all of it** | **44.8 (41%)** | **5.4 (19%)** | **5.7 (20%)** |

- **Unity's engine takes 44.8 ms of the original's frame; Moono's takes 5.4–5.7 ms.** 35 of
  Unity's 45 ms are small calls from game code that cross into native code: component lookups
  20.3 ms, null checks 5.4 ms, transform reads 3.7 ms. Moono inlines most of them into the game
  code, where they nearly vanish.
- **On equal work, the engine's own per-frame work plus uGUI is 12.1 ms in Unity and 4.8–5.1 ms
  in Moono.** Moono's 5 ms is good: 2.4× less than Unity for the same scene.
- **One part is dearer in Moono: rendering on the main thread** (2.6 ms against 1.9 ms on U57,
  4.2 against 2.1 on 2minnow). Unity gives more of it to job workers.
- **The original is slow mostly because of game code on Mono and the calls it makes**, not
  because of Unity's own per-frame work: 56% game code, 32% engine calls from game code, 6%
  engine's own work, 5% uGUI, under 1% GC on this colony.

## Method

**Players and build.** Game build 744825 (U59), every DLC.
- Original: the Linux player (`out/local-tools/u57orig`, Unity 6000.3.5f2, Mono with the
  Boehm collector), started as `scripts/alloc-compare.py` starts it.
- Moono: origin/master c4cca30f, release build, CoreCLR 10.0.13, through `scripts/run.sh`.
  "No tweaks" is `--game-tweak none`: the same game code as the original. "Default tweaks" is a
  user's launch (`ONI_TWEAK_SETTINGS=1`).

**Driver path.** Both players ran under Xvfb at 1280×720 with `GALLIUM_DRIVER=d3d12`: OpenGL
4.6 from Mesa 25.2.8 over D3D12 on the NVIDIA GPU (both logs name it). This is the path Moono's
Linux runs use. No run used llvmpipe. Unity ran with a render thread and graphics jobs
(`kGfxThreadingModeClientWorkerJobs`).

**Scene.** PerfProbe loaded the save from the menu and held speed 3, with `novictory.txt` and
`noautosave.txt`, the camera where the save puts it and the pointer in the middle of the window,
as in the frame inventory. Screenshots at the start, middle and end of each window show no
dialog.

**Launches.** Six, all through runq as `linux-perf` jobs on a quiet machine:

| Launch | Load to colony | Warm-up | Recorded | Frames | Game seconds |
|---|---|---|---|---|---|
| Original, 2minnow (smoke, 20 s; not used in tables) | 88 s | 20 s | 20 s | 276 | 54 |
| Original, U57 | 272 s | 45 s | 75.9 s | 579 | 120 |
| Original, 2minnow | 88 s | 60 s | 75.2 s | 959 | 191 |
| Moono no tweaks, U57 | 74 s | 60 s | 75.2 s | 2,205 | 223 |
| Moono no tweaks, 2minnow | 56 s | 60 s | 75.3 s | 2,645 | 222 |
| Moono default tweaks, U57 | 76 s | 60 s | 75.2 s | 2,398 | 224 |

One launch each: no spread is known. One more job failed before it started a player.

**Sampling.** `perf record -e cpu-clock:u -F 499`, user mode only (`perf_event_paranoid=2`).
- Moono: `-g` (frame pointers) for every thread, JIT frames named from .NET's perf map
  (`DOTNET_PerfMapEnabled=1`). 99% of main-thread stacks reach the process's first frame.
- Original: frame pointers don't work (UnityPlayer.so is built without them) and perf's DWARF
  unwinder stops at Mono's JIT code. So the main thread was recorded with a copy of its stack
  (`--call-graph dwarf,65528`) and unwound by a local tool: `.eh_frame` for code in a module,
  the frame-pointer chain for JIT code. Mono ran with `MONO_DEBUG=disable_omit_fp` so its JIT
  code keeps frame pointers. A second `perf record -g` took every thread for the leaf owner.
- Stack completeness, original: 98.1% (U57) and 96.7% (2minnow) of main-thread samples unwind
  to the thread's first frame. The rest end when the 64 KB copy runs out. Every sample under a
  render callback (`OnRenderImage`, `OnPreCull`) is among them, and none of their frames
  appears in a complete stack: they are Unity's camera rendering, which uses a deep stack. They
  keep their owner and are counted as "camera rendering". perf lost 54 and 132 samples of
  about 32,000.
- Mono's JIT map: Unity's Mono takes neither `--jitmap` nor `MONO_ENV_OPTIONS`. A local
  PerfProbe addition asks Mono's exported `mono_jit_info_table_find` about every address of the
  process's anonymous executable memory after the window and writes start, size, image and
  full name: 58,000 methods, 18 MB of code, in 0.7 s. The image tells Assembly-CSharp from
  UnityEngine's managed wrappers and mscorlib.

**Owners.** As in the frame inventory:
- **Leaf:** who runs the sampled instruction.
- **Inclusive (owner):** the nearest frame from the leaf that belongs to game code, the engine,
  uGUI/TMP, tweaks, SimDLL or FMOD. A `memcpy` or a Mono runtime helper counts for whoever
  called it. A sample with the collector on its stack counts as GC.
- Game: Assembly-CSharp, -firstpass and the game's other managed libraries. Engine: for the
  original `UnityPlayer.so` and the `UnityEngine.*` managed assemblies; for Moono
  `OniMoono.*`. uGUI/TMP: `UnityEngine.UI`, `Unity.TextMeshPro` (managed code in both players).

**The comparable split.** Engine-owned samples divide by who asked:
- **(c)** a game frame sits above the engine frames: a call the game made.
- **uGUI's calls:** the same for uGUI/TMP.
- **(d)** no game or uGUI frame above: the engine's own per-frame work.

In Moono a cheap engine call is inlined into the game method and counts as game code. So (a)
and (b) alone don't compare; "game + (c)" and "(d) + uGUI" do.

**ms a frame** is samples × 1/499 s ÷ frames. **ms a game second** is that × frames a game
second. Busy time is `/proc` schedstat (user and kernel).

**Measured against estimated.** Measured: everything in the tables unless marked. Estimated:
call counts (from per-call costs measured elsewhere), Moono's draws a frame (from the Nov 7
inventory's frame stats: this study ran without `--frame-stats`), and the labels of Unity's
phases (from the callbacks seen under each).

## The frame

| | Original U57 | Moono none U57 | Moono default U57 | Original 2minnow | Moono none 2minnow |
|---|---|---|---|---|---|
| fps | 7.6 | 29.3 | 31.9 | 12.8 | 35.2 |
| Frame ms | 130.9 | 34.1 | 31.4 | 78.4 | 28.4 |
| Game s a real s (3.00 asked) | 1.58 | 2.96 | 2.97 | 2.54 | 2.95 |
| Frames a game s | 4.83 | 9.91 | 10.73 | 5.02 | 11.92 |
| Main thread busy | 97.6% | 95.9% | 97.9% | 97.8% | 86.7% |
| Main busy ms/frame (user + kernel) | 127.8 | 32.7 | 30.7 | 76.6 | 24.6 |
| Main sampled ms/frame (user) | 110.2 | 28.6 | 28.8 | 66.7 | 20.9 |
| Collections in the window | 0 | 1 | 1 | 4 | 0 |
| Managed heap GB | 6.7 | 7.9 | 6.5 | 4.0 | 5.6 |
| Draw calls a frame | 1,094 (Unity's counter) | 777 (Nov 7) | 777 (Nov 7) | 1,650 | 1,060 (Nov 7) |
| Playing animations visited a frame | 4,493 | 4,420 | 4,441 | 1,731 | 1,622 |

The main thread limits every run but Moono's 2minnow, where the GL thread does (as in the
inventory). The original can't hold speed 3 on either colony.

## Main thread, per frame

Inclusive by owner (ms a frame, share of sampled time):

| Owner | Original U57 | Moono none U57 | Moono default U57 | Original 2minnow | Moono none 2minnow |
|---|---|---|---|---|---|
| Game | 62.16 (56.4%) | 22.37 (78.2%) | 20.47 (71.2%) | 35.33 (53.0%) | 12.97 (62.1%) |
| Engine | 44.81 (40.7%) | 5.42 (18.9%) | 5.68 (19.7%) | 26.62 (39.9%) | 6.90 (33.1%) |
| uGUI/TMP | 2.27 (2.1%) | 0.57 (2.0%) | 0.82 (2.9%) | 1.66 (2.5%) | 0.80 (3.8%) |
| Tweaks | — | — | 1.54 (5.3%) | — | — |
| GC | 0.42 (0.4%) | 0.00 | 0.00 | 2.75 (4.1%) | 0.00 |
| SimDLL | 0.32 | 0.13 | 0.14 | 0.11 | 0.05 |
| FMOD | 0.19 | 0.10 | 0.11 | 0.23 | 0.14 |
| **Total** | **110.18** | **28.59** | **28.77** | **66.72** | **20.87** |

By leaf (ms a frame):

| Leaf | Original U57 | Moono none U57 | Moono default U57 | Original 2minnow | Moono none 2minnow |
|---|---|---|---|---|---|
| Game code | 38.55 | 20.19 | 18.23 | 21.56 | 11.19 |
| Engine, native (UnityPlayer.so) | 33.03 | — | — | 19.25 | — |
| Engine, managed (UnityEngine wrappers; OniMoono) | 6.94 | 4.56 | 4.79 | 3.38 | 5.87 |
| uGUI/TMP | 2.06 | 0.53 | 0.79 | 1.43 | 0.75 |
| Tweaks | — | — | 1.12 | — | — |
| BCL (mscorlib; System.*) | 17.33 | 1.47 | 1.87 | 9.49 | 1.20 |
| Runtime (libmonobdwgc, trampolines; libcoreclr) | 9.62 | 0.91 | 1.02 | 6.44 | 0.76 |
| GC (Boehm `GC_*`) | 0.41 | 0.00 | 0.00 | 2.66 | 0.00 |
| libc | 1.74 | 0.71 | 0.41 | 2.20 | 0.92 |
| GL driver | 0.01 | 0.01 | 0.01 | 0.01 | 0.01 |
| SimDLL / FMOD | 0.30 / 0.18 | 0.12 / 0.09 | 0.14 / 0.09 | 0.09 / 0.21 | 0.04 / 0.13 |

Neither player's main thread calls the GL driver: both hand draws to a render thread.

In the original, game-owned time is 38.6 ms of its own code, 17.1 ms of BCL (collections that
Mono doesn't inline: `HashSet<Tag>.Contains` 2.4 ms, `List<T>.get_Item` 3.3 ms, `Dictionary`
lookups) and 5.9 ms of Mono's runtime (type checks, trampolines, hash lookups).

### The comparable split (ms a frame)

| | Original U57 | Moono none U57 | Moono default U57 | Original 2minnow | Moono none 2minnow |
|---|---|---|---|---|---|
| (a) Engine code as leaf | 39.97 | 4.56 | 4.79 | 22.63 | 5.87 |
| (b) Game code as leaf | 38.55 | 20.19 | 18.23 | 21.56 | 11.19 |
| Game-owned, inclusive | 62.16 | 22.37 | 20.47 | 35.33 | 12.97 |
| (c) Engine calls made by game code, inclusive | 34.94 | 1.16 | 1.38 | 18.19 | 1.25 |
| **Game + (c): all that game code asked for** (Moono default: with tweaks) | **97.10** | **23.52** | **23.39** | **53.52** | **14.21** |
| (d) Engine work no game code asked for | 6.97 | 4.01 | 4.05 | 5.23 | 5.40 |
| uGUI/TMP with the engine calls it made | 5.16 | 0.81 | 1.06 | 4.86 | 1.05 |
| **(d) + uGUI: the engine side of the frame** | **12.14** | **4.82** | **5.11** | **10.09** | **6.45** |

(c) by API group (ms a frame). Moono's column holds only the calls that were not inlined:

| Group | Original U57 | Moono none U57 | Original 2minnow | Moono none 2minnow |
|---|---|---|---|---|
| Component lookup (`GetComponent`, `component.transform`, `.gameObject`) | 20.32 | 0.18 | 8.70 | 0.13 |
| Object checks (`== null`, `!= null`, `Equals`) | 5.36 | 0.07 | 1.97 | 0.02 |
| Transform (positions, children, `hasChanged`) | 3.69 | 0.13 | 1.58 | 0.13 |
| Texture (`Apply`, raw data, `NativeArray.Copy`) | 1.45 | 0.16 | 2.30 | 0.34 |
| Instantiate / Destroy / AddComponent | 1.40 | 0.18 | 0.62 | 0.08 |
| Mesh, `Graphics.DrawMesh`, `Blit`, camera | 1.23 | 0.20 | 1.62 | 0.25 |
| Activity flags (`SetActive`, `enabled`) | 0.48 | 0.05 | 0.33 | 0.05 |
| Material and property blocks | 0.31 | 0.06 | 0.55 | 0.10 |
| Other | 0.70 | 0.13 | 0.52 | 0.15 |

The original's largest calls on U57, with a rough count from the per-call costs in
[unity-vs-moono.md](unity-vs-moono.md) (estimated; those costs were measured at the main menu):

| Call | ms a frame | Estimated calls a frame |
|---|---|---|
| `Component.transform` (+ `GameObject.transform` 0.71) | 7.52 | 400,000–650,000 |
| `GetComponent<T>` on a component or GameObject, `TryGetComponent` | 7.38 | 130,000–165,000 |
| `==` / `!=` on objects | 5.03 | about 1.2 million |
| `Component.gameObject` | 2.80 | |
| `Transform.position` (get) | 2.66 | 170,000–250,000 |
| `GetComponents<T>` | 1.85 | |
| `Object.Instantiate` | 1.22 | |
| `Graphics.DrawMesh` | 0.88 | |

(d) by phase (ms a frame):

| Phase | Original U57 | Moono none U57 | Moono default U57 | Original 2minnow | Moono none 2minnow |
|---|---|---|---|---|---|
| Camera rendering (culling, draw recording, UI overlay batches) | 1.89 | 2.56 | 2.16 | 2.07 | 4.18 |
| Canvas phase | 1.23 | 0.75 | 1.00 | 0.79 | 0.73 |
| Script dispatch (calling `Update`, `LateUpdate`, coroutines) | 2.11 | 0.56 | 0.70 | 1.17 | 0.37 |
| Other phases | 1.75 | 0.15 | 0.18 | 1.20 | 0.12 |

Unity's phases are the functions directly under its player loop: the two that call the `Update`
and `LateUpdate` scripts are "script dispatch", the one that calls
`Canvas.SendWillRenderCanvases` is "canvas", the deep-stack samples are "camera rendering",
and the rest (input, transform dispatch, time, audio update: not told apart without symbols)
are "other phases". Moono's rendering includes its UI overlay batching (0.86 ms on U57), which
Unity does partly in its canvas phase, so compare rendering + canvas: 3.12 against 3.31 ms
(U57) and 2.86 against 4.91 ms (2minnow).

## Main thread, per game second

The original runs about 5 frames a game second, Moono 10–12, so per-frame work (rendering,
canvas, render-every-tick game code) counts twice as often in Moono's game second.

| ms of main thread a game second | Original U57 | Moono none U57 | Moono default U57 | Original 2minnow | Moono none 2minnow |
|---|---|---|---|---|---|
| Total sampled | 532 | 283 | 309 | 335 | 249 |
| Game-owned, inclusive | 300 | 222 | 220 | 177 | 155 |
| Tweaks | — | — | 17 | — | — |
| (c) Engine calls made by game code | 169 | 11 | 15 | 91 | 15 |
| Game + (c) (+ tweaks) | 469 | 233 | 251 | 268 | 169 |
| (d) Engine's own work | 34 | 40 | 43 | 26 | 64 |
| uGUI/TMP with its engine calls | 25 | 8 | 11 | 24 | 12 |
| (d) + uGUI | 59 | 48 | 55 | 51 | 77 |
| Engine-owned, all of it | 216 | 54 | 61 | 134 | 82 |
| GC | 2.0 | 0 | 0 | 13.8 | 0 |
| Game code as leaf | 186 | 200 | 196 | 108 | 133 |
| BCL as leaf | 84 | 15 | 20 | 48 | 14 |
| Runtime as leaf | 46 | 9 | 11 | 32 | 9 |

Per game second Moono's engine side costs about the same as Unity's on U57 (48–55 against
59 ms) and more on 2minnow (77 against 51 ms), because Moono draws 2.1–2.4 times the frames.

## Per unit of work

| Unit | Original | Moono | |
|---|---|---|---|
| Engine side of one frame, (d) + uGUI: U57 | 12.1 ms | 4.8 ms (none), 5.1 (default) | Moono 2.4× cheaper |
| Engine side of one frame: 2minnow | 10.1 ms | 6.5 ms | Moono 1.6× cheaper |
| Script dispatch a frame: U57 / 2minnow | 2.11 / 1.17 ms | 0.56 / 0.37 ms | Moono 3–4× cheaper |
| Canvas: phase + uGUI/TMP rebuilds (`CanvasUpdateRegistry`, TMP) a frame, U57 | 1.23 + 1.99 ms | 0.75 + 0.42 ms | Moono 2.8× cheaper |
| UI input (`EventSystem.Update`, raycasts) a frame, U57 | 2.59 ms | 0.33 ms | Moono 8× cheaper |
| Animation update (`Global.LateUpdate`, game code and its engine calls) per 1,000 playing animations: U57 / 2minnow | 1.83 / 2.10 ms | 0.53 / 0.51 ms | game code, not engine: Moono 3.5–4× cheaper |
| **Main-thread rendering per 1,000 draws: U57 / 2minnow** | **1.7 / 1.3 ms** | **3.3 / 3.9 ms** | **Moono 2–3× dearer** (estimated: draw counts differ in kind) |
| GL driver, all threads, per 1,000 draws: U57 / 2minnow | 19.9 / 14.9 core-ms | 18.7 / 21.1 core-ms | about equal |

Canvases rebuilt a frame could not be counted in the original, so canvas work is per frame.
Unity's draw calls are its `Draw Calls Count` counter; Moono's are the draws its frame stats
counted on Nov 7 with every tweak. The scene and camera are the same, the counters are not.

## Other threads (core-ms a frame, busy)

| Threads | Original U57 | Moono none U57 | Moono default U57 | Original 2minnow | Moono none 2minnow |
|---|---|---|---|---|---|
| Main | 127.8 | 32.7 | 30.7 | 76.6 | 24.6 |
| Render thread (`UnityGfxDeviceW`; Moono `GL`) | 15.3 | 10.1 | 9.3 | 19.2 | 18.4 |
| · of it engine code + libc (sampled) | 5.8 | 3.2 | 1.9 | 7.8 | 6.1 |
| Mesa driver thread | 15.4 | 8.5 | 8.3 | 15.5 | 11.8 |
| Unity job workers (15) | 19.4 (7.4 sampled in engine code) | — | — | 20.9 (8.7) | — |
| Moono draw recording (3) | — | 2.4 | 2.1 | — | 3.3 |
| .NET thread pool: texture copies (`TexelCopies`, memcpy) | — | 13.0 | 1.3 | — | 15.7 |
| Presenter | — | 1.1 | 1.2 | — | 1.3 |
| Game job workers (`KWorker`, 15) | 37.7 | 14.3 | 18.8 | 17.8 | 7.0 |
| Path probe thread | 26.9 | 14.4 | 5.6 (repair tweak) | 24.7 | 13.6 |
| SimDLL | 9.2 | 4.5 | 4.7 | 8.5 | 3.9 |
| FMOD | 10.2 | 3.6 | 4.0 | 11.4 | 5.6 |

Unity's job workers' busy time includes waiting in user mode; the sampled figure is time in
engine code. What the jobs do (culling, transforms, draw preparation) can't be told apart
without symbols.

## Answers

**How much main-thread time does Unity's engine take against Moono's?**
- Per frame, U57: 44.8 ms against 5.4 ms (no tweaks) and 5.7 ms (default). 2minnow: 26.6
  against 6.9 ms.
- Per game second, U57: 216 ms against 54–61 ms. 2minnow: 134 against 82 ms.
- Without the calls game code made (which Moono inlines), per frame: 12.1 against 4.8–5.1 ms
  (U57), 10.1 against 6.5 ms (2minnow). Per game second: 59 against 48–55 ms (U57), 51 against
  77 ms (2minnow).

**Which subsystems are cheaper or dearer in Moono per unit of work?**
- Cheaper: calls from game code (component lookups 20.3 → 0.2 ms a frame, null checks 5.4 →
  0.1, transforms 3.7 → 0.1, instantiate and destroy 1.4 → 0.2), script dispatch (3–4×), canvas
  and uGUI (2.8×), UI input (8×), the render thread's own code.
- About equal: GL driver work per draw.
- Dearer: main-thread rendering per frame and per draw, and texture copies without tweaks
  (below).

**How much of the original's slowness is engine, how much Mono running game code, how much GC?**
Of the 110 ms sampled a frame on U57 (131 ms frame, main thread 98% busy):
- Game code on Mono: 62.2 ms (56%). Its own code 38.6, BCL 17.1, Mono's runtime 5.9.
- Engine calls made by game code: 34.9 ms (32%).
- Engine's own per-frame work: 7.0 ms (6%). uGUI/TMP with its calls: 5.2 ms (5%).
- GC: 0.4 ms (0.4%), allocation paths only; no collection fell in the window. On 2minnow, with
  4 collections in 75 s: 2.75 ms (4.1%).
- About 18 ms a frame is busy but not sampled (kernel time, 9% of the thread's CPU, and
  samples that landed in the kernel).

Per game second Moono needs 233 ms for what game code asks (no tweaks) against the original's
469 ms: 158 ms of the 236 ms saved are engine calls, 78 ms is game code running faster on
CoreCLR (BCL 84 → 15, runtime 46 → 9, while game code as leaf goes 186 → 200 with the inlined
calls in it).

**Is Moono's 5 ms good?** Yes. For the same scene Unity's engine-owned time is 45 ms, and its
own per-frame work plus uGUI is 12 ms against Moono's 5. The engine is no longer where the
frame goes: game code is 71–78% of Moono's main thread.

**The three places where Moono's engine costs more than Unity's:**
1. **Rendering on the main thread.** 2.6 against 1.9 ms a frame on U57, 4.2 against 2.1 on
   2minnow; with canvas 3.3 against 3.1 and 4.9 against 2.9. About 3.3–3.9 ms per 1,000 draws
   against Unity's 1.3–1.7. In Moono it is `GlDrawRecorder.RecordAhead`/`RecordChunk` on the
   main thread's share of the fork-join (0.6–0.8 ms), uniform resolution, `FrameRenderer.Cull`
   with `CanvasSystem.CollectCameraDraws` (0.4–0.7 ms) and `CanvasSystem.RenderOverlays`
   batching every frame (0.6–0.9 ms). Unity spends more in total (job workers 7–9 ms a frame
   in engine code) but less on the thread that limits the frame.
2. **Texture copies without tweaks.** With `--game-tweak none`, `TexelCopies.Copy.TryMake`
   copies 8.6 (U57) to 13.2 (2minnow) core-ms a frame on pool threads and
   `GlTextures.UploadChanged` takes 3.7–7.6 ms of the GL thread. Unity's cost for the same
   game writes is 1.5–2.3 ms on the main thread plus part of its render thread's 3.3–4.7 ms of
   libc. With default tweaks the copies drop to 0.1 ms and the upload to 2.4 ms, so this
   matters for the no-tweak configuration and for 2minnow, where the GL thread limits the frame.
3. **Per game second, per-frame work.** At 2.1–2.4 times the original's frames a game second,
   Moono's (d) is 40–64 ms a game second against Unity's 26–34 ms. This is the price of the
   frame rate, not of slower code, except for item 1.

## Limits

- **Linux and OpenGL.** The original's Windows build uses D3D11, and Windows frame rates are
  higher for both players (U57: original 11.6 fps, Moono 103 with default tweaks). The
  Mesa-over-D3D12 layer makes GL work dearer for both. Use this page for CPU-side direction.
- **One launch each**, 75 s windows; the original's windows hold 579 and 959 frames.
- **Sampling.** User mode only; 499 Hz; blocked time not seen. The stack copy (up to 64 KB a
  sample) costs the original's main thread a little; `MONO_DEBUG=disable_omit_fp` gives Mono's
  JIT one register less. Neither was measured apart: the original ran 7.6 fps here against
  11.6 on Windows, mostly the platform.
- **Stacks.** 2–3% of the original's main-thread samples end before the root (camera
  rendering). A sample in a JIT method's first or last instructions can miss its caller.
- **No symbols for UnityPlayer.so.** Unity's phases other than script dispatch, canvas and
  camera rendering are one group; the job workers' work is not split.
- **Inlining.** Moono's (c) holds only calls that were not inlined; the rest is inside game
  time. That is why "game + (c)" is the comparable sum.
- **Draw counts** come from different counters and, for Moono, from another day's runs.
- **GC.** Boehm's collections are seen only as samples with `GC_*` on the stack; U57's window
  had none.

## Tooling

Local, not in the repository (out/unitybase in the study's worktree): `ubrun.py` (one launch),
`ublib.py` (a perf.data reader, an `.eh_frame` unwinder that crosses Mono's JIT frames by
frame pointer, symbol and JIT-map naming), `ubextract.py`, `ubanalyze.py`, `ubcompare.py`, and
PerfProbe's `UnityBase.cs` (`unitybase.txt`: a per-second line with frame, game time and
Unity's render counters, and the Mono JIT map). The unwinder and the JIT map are reusable for
any profile of the original.
