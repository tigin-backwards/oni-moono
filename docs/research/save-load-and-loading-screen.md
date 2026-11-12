# Save loads and the loading screen (2026-11-11)

When a save is opened from the Load screen, the loading screen is drawn once and then stays still
until the colony appears. This note measures where a load's time goes, which frames reach the
screen while it runs, what keeps the screen from moving, and what each way of animating it or
shortening the load could gain. Measured only: nothing here is built.

## Method

- **Builds:** U59 (744825) with every DLC; our player at master 95b945fe with the default game
  tweaks (`exact`); the original Unity player on the same install. Linux, Xvfb at 1280x720, the
  GPU through Mesa's d3d12 driver, each run in its own run home (so Mesa's shader cache starts
  empty). Windows not measured: the perf queue and the Windows lock were held by another job,
  and game processes were running on Windows, the whole time this study ran.
- **Saves:** a mid-game Spaced Out colony (30 MB, about 50 Duplicants) and a late-game Spaced Out
  colony (150 MB, cycle 17,000+, about 62,000 buildings).
- **Probe:** a test-only game mod (LoadProbe, not committed), loaded in both players. 300 frames
  after the main menu is up it does what the Load button does (`LoadingOverlay.Load`, then
  `LoadScreen.DoLoad`). It writes a timeline: every frame's start (`Update`) and end
  (`WaitForEndOfFrame`), the entry and exit of about 50 load steps through Harmony, and in a
  second mode the calls and outermost time per frame of the many-call steps (instantiation,
  `OnPrefabInit`, `OnSpawn`, per-object deserialization). A background thread that wakes every
  5 ms logs each wake-up more than 30 ms late: the stalls a presenter thread would see.
- **Screen:** the Xvfb display recorded at 20 fps from the click (ffmpeg x11grab); a picture
  counts as changed when its mean difference from the one before passes 0.5 of 255.
- **Ours only:** `ONI_PRESENT_TRACE=1` (swaps and gaps between them), `ONI_STARTUP_STATS=1`
  (each slow frame's GL-thread work), `perf record` at 499 Hz with call graphs (JIT symbols did
  not resolve, so only the per-thread split is used).
- **Totals:** from the click to "interactive", the start of the first frame after which no frame
  takes 100 ms or more. Two runs per player and save where time allowed; release QA ran two
  large players beside the second runs, so those are slower.
- **Charts and stills:** `C:\Users\Glyn\Downloads\save-load-study\` (`save-load-timeline.svg`,
  `save-load-timeline-moono.svg`, the loading screen in both players, our first colony frames).

## Totals

| Player | Mid-game colony | Late-game colony |
|---|---|---|
| Ours | 20.8 s, 21.0 s | 37.2 s, 41.9 s |
| Original | 84.1 s, 92.2 s | 410.9 s |

The original's late-game load was run once (about seven minutes).

## Where a load goes

First runs, ms from the click (% of the total):

| Phase | Ours, mid | Ours, late | Original, mid | Original, late |
|---|---|---|---|---|
| Loading overlay up, two frames, `DoLoad` | 10 | 13 | 13 | 22 |
| Teardown: `Resources.UnloadUnusedAssets`, `GC.Collect` | 167 | 158 | 59 | 89 |
| Scene switch: front end destroyed, backend scene loaded | 270 | 266 | 733 | 1,510 |
| Backend Awake, Sim DLL initialised | 15 | 19 | 201 | 353 |
| File read (`File.ReadAllBytes`) | 15 | 90 | 101 | 250 |
| Header and KSerialization type directory | 62 | 72 | 866 | 1,221 |
| Decompression (zlib) | 445 (2.1%) | 2,539 (6.8%) | 2,058 | 18,571 |
| Root deserialization, mod footprint, settings | 15 | 12 | 8,569 | 25 |
| Sim DLL: cells, `LoadWorld`, `Sim.Start` | 61 | 22 | 75 | 48 |
| `Game.OnPrefabInit` up to the shader warm-up | 73 | 56 | 37 | 45 |
| `Shader.WarmupAllShaders` | 1,802 (8.7%) | 1,780 (4.8%) | 0 | 0 |
| Rest of `PostLoadPrefabs` (game screens, managers) | 802 | 432 | 989 | 263 |
| `SaveManager.Load`: every object instantiated and deserialized | 5,764 (27.7%) | 16,408 (44.1%) | 31,711 (37.7%) | 309,570 (75.3%) |
| `Game.Load`, camera, world grid | 87 | 57 | 542 | 2,164 |
| `SaveLoader.Load` tail (`Stats.Print`, `GC.Collect`) | 118 | 378 | 583 | 1,884 |
| Other scene objects' `OnSpawn` | 71 | 165 | 124 | 1,318 |
| `Game.OnSpawn` | 152 | 332 | 280 | 1,293 |
| `Game.OnSpawn`: `Resources.UnloadUnusedAssets` | 299 | 1,156 | 0 | 0 |
| Rest of `Game.OnSpawn` and of its frame | 930 | 1,285 | 1,968 | 3,786 |
| First colony frames: every object's `OnSpawn`, first draws | 9,644 (46.4%) | 11,991 (32.2%) | 35,197 (41.8%) | 68,460 (16.7%) |
| **Total** | **20,799** | **37,231** | **84,106** | **410,873** |

Notes:

- The original's root-deserialization row is `WorldGen.LoadSettings` (8.5 s on the mid-game
  colony, 13 s in its second run); ours takes no measurable time there. Why was not looked into.
- Unity's `Resources.UnloadUnusedAssets` and `Shader.WarmupAllShaders` take no Harmony patch,
  or return at once: their time in the original falls into the neighbouring rows.
- **The many-call steps** (ours, the per-frame totals; the patches add 10–20%):
  - mid-game: 23,506 instantiations (3.8 s with their `OnPrefabInit`s) and 22,114 objects
    deserialized (3.3 s) inside `SaveManager.Load`; the first colony frame then runs 837,772
    `Spawn` calls (6.2 s);
  - late-game: 93,395 instantiations (7.2 s), 3.06 million `OnPrefabInit`s (8.4 s), 90,998
    objects deserialized (11.8 s), then 3.24 million `Spawn` calls (9.2 s) in the first colony
    frame.
- **Shader warm-up** in ours is `GlDevice.WarmupShaders`: a synchronous call (`worker.Call`)
  that keeps the main thread waiting while the GL thread builds 360 programs. The 1.8 s is with an
  empty Mesa shader cache; a later load in the same process finds the programs built.
- **Threads** (ours, late-game, perf): during `SaveManager.Load` (21.9 s with perf on) the main
  thread was on the CPU for 15.5 s; the server GC threads used 10 s and the background GC 4 s.
  The GL thread used 1.9 s during the shader warm-up and 1 s in the whole of `SaveManager.Load`.
  Apart from the GC, the other threads are almost idle while the main thread loads.
- **GC** (ours): the runtime's pause total grows by 1.0 s (mid-game) and 2.9 s (late-game) from the
  click to the colony frame, and 0.4 s and 0.9 s more before the colony settles.

## Frames during a load

Every gap of more than 100 ms between frame ends, first runs (ms from the click):

| Run | Gaps |
|---|---|
| Ours, mid | 6–178 (173); **178–18,186 (18,007)**; 18,186–18,813 (627); 18,813–20,148 (1,336); 20,148–20,420 (272); 20,465–20,797 (333) |
| Ours, late | 6–171 (165); **171–33,506 (33,335)**; 33,506–35,234 (1,729); 35,234–36,749 (1,515); 36,749–37,227 (478) |
| Original, mid | **73–79,676 (79,603)**; 79,676–83,303 (3,627); 83,303–83,899 (596); 83,899–84,102 (203) |
| Original, late | **113–379,058 (378,945)**; 379,058–390,445 (11,387); 390,445–393,258 (2,813); then eight frames of 100–190 ms |

What runs in each:

- **The first, short gap** (ours only) is the frame that calls `DoLoad`: `App.LateUpdate` runs
  `KObjectManager.Cleanup`, `Resources.UnloadUnusedAssets` and `GC.Collect`, then asks for the
  backend scene. That frame's picture (the loading screen) is presented about 0.18 s in.
- **The long gap is one engine frame.** It loads the backend scene and runs `SaveLoader.OnSpawn`
  (the whole read, decompression, deserialization, Sim DLL load, `PostLoadPrefabs` with the shader
  warm-up and screens, and `SaveManager.Load`), then `Game.OnSpawn`. Then the same frame's
  `Update` runs and the `OnSpawn` of every loaded object (6–9 s). The main thread does not return
  to the player loop between the loading screen's picture and the first picture of the colony.
- **The next gaps** are the first colony frames: their first draws (the GL thread busy 0.7–1 s on
  the textures and meshes made ahead of their first draw), the mod-differences dialog, the first
  sim ticks.
- **Our present trace agrees:** one swap 18,984 ms (mid) and 34,116 ms (late) after the one before.
- **The recordings** show the same still picture (a Duplicant's face over "LOADING...") from the
  first 0.1 s until the colony appears at 19.2 s and 34.3 s in ours, 80.6 s in the original.
  There is no frame in between whose content could have changed.

## What stops the loading screen from moving

1. **No frames are presented (the main cause, in both players).** The load runs inside game code
   on the main thread for the whole long gap: one engine frame of 18–33 s in ours, 80–380 s in
   the original. `SceneManager.LoadScene` is synchronous, and the game builds the world in
   `OnSpawn` and `Start` of the same frame, so the player loop never runs a frame in between. The
   original behaves the same way: Unity draws nothing between the two scenes here either.
2. **The content would not move if frames were drawn.** `LoadingOverlay` is a static Image and a
   LocText; nothing in it animates. The frames that do run during the load (the two after the
   click, the one at 0.18 s) show the same picture.
3. **Waits in our engine are a small part.** The only one inside the gap is the shader warm-up
   (the main thread waits for the GL thread's program builds, 1.8 s with an empty driver cache);
   in the first colony frame the GL thread's work is about 1 s. Nothing in the long gap waits on
   a fence or on the presenter.
4. **Garbage collections stop every managed thread, the presenter included.** The 5 ms ticker
   thread saw no gap over 30 ms in our mid-game load on a quiet machine; with release QA running
   beside it, its longest gaps were 0.24 s (mid-game) and 0.5 s (late-game), during
   `SaveManager.Load` and the first colony frame, where the GC pause total rises. In the original,
   Boehm's collections stopped it for up to 1.4 s (mid-game) and 4.6 s (late-game), 83 and 277
   times over 30 ms.

## Loading Pip

[Loading Pip - Customizable Loading Screen](https://steamcommunity.com/sharedfiles/filedetails/?id=3408518726)
(row 894 of the mod table) passes the mod gate on its current upload (version 1.4.0, reviewed by
onimaxxing); so does the companion mod it looks for,
[Pip Licks Butt](https://steamcommunity.com/sharedfiles/filedetails/?id=3802090892) (1.0.0.0).

- **What it draws:** a postfix on `LoadingOverlay.Load` replaces the overlay's Image sprite with
  the UI icon of a chosen entity (Pip by default), a random critter, a custom PNG from its
  assets folder, or anything. It is a still picture.
- **What it animates:** only with Pip Licks Butt enabled and Loading Pip's default options. It
  then hides the Image, puts a `KBatchedAnimController` with `KBatchedAnimCanvasRenderer` on it
  (Pip's `lick_butt_loop`), and steps the animation every 33 ms from a coroutine on
  `Global.Instance` (`WaitForSecondsRealtime`).
- **Played with both mods on the mid-game colony,** in each player: the loading screen changes once
  in the first 0.15 s (the frames before the load), then stays still for 19.2 s in ours and
  69.9 s in the original. The coroutine and the drawing both need main-thread frames, so the
  animation freezes for the whole load, in the original as in ours. Totals were unchanged
  (20.6 s and 85.8 s).

A presenter-thread animation would not move this mod's content: its kanim is game UI drawn by the
main and GL threads. Only frames from the main thread would.

## The start-up presenter

At start-up the window keeps moving while the main and GL threads load (`GlSplash`, ARCHITECTURE
§3.5):

- `GlPresenter` runs on a thread of its own, current on a GL context shared with the GL thread's.
  The GL thread draws each frame into one of three offscreen back buffers; the presenter waits
  on that frame's fence, blits it to the window (or the DXGI swap chain's back buffer on Windows)
  and presents.
- While `GlSplash.Active`, its wait for the next frame times out about 60 times a second, and
  `Redraw()` blits the last shown frame again and draws the splash over it: Gassy Moo comets (a
  kanim's quads from `sharedassets0.assets`, its own small program and texture) and a progress
  bar (`StartupProgress`). Game frames are not changed: the splash is drawn into the window after
  the copy.
- Once 30 frames arrive within 100 ms of each other (the main menu) the rain fades out, and
  `Active` stays false for the rest of the run. On Windows with DXGI, `StallRepeats` then
  presents the last frame again during stalls, so a variable-refresh display keeps refreshing,
  but the picture does not change. With OpenGL's SwapBuffers there is no repeat.

**What it would take during a save load:**

- **Re-arming:** a call that resets the splash's fade state (`fadeStart`, `steady`) and the
  `MaxSeconds` limit, made by the engine when the next frame is late after a scene load starts:
  `SceneLoader`, or a frame not handed over for about 250 ms after `SceneManager.LoadScene`.
  Nothing in the game or in mods has to change. The existing 30-steady-frames rule ends it at
  the colony.
- **What it could show:**
  - **The last frame, repeated:** DXGI's repeats already do this on Windows; it changes nothing on
    screen.
  - **A cycle of frames:** pointless here. The overlay is static, so captured frames would be
    identical, and Loading Pip's kanim does not advance without main-thread frames.
  - **Drawing with time:** the comets, a spinner, a shimmer on the last frame, or a progress bar,
    all drawn by the presenter over the loading screen's picture. For a progress bar the engine
    can count instantiations against the last load's count of the same save, or keep the last
    load's checkpoint times, as `StartupProgress` does for start-up.
- **Limits:**
  - It cannot animate the game's own UI or a mod's loading-screen content.
  - It hitches while a garbage collection stops the presenter thread: up to 0.5 s seen in the
    late-game load on a busy machine.
  - The presenter's GL context shares the driver with the GL thread's program builds and uploads
    (as at start-up, where this works).

## Off-thread potential

| Work | Ours, mid | Ours, late | Where it can run |
|---|---|---|---|
| File read | 15 ms | 90–257 ms | any thread, from the moment the save is picked |
| Header and type directory | 62 ms | 72–141 ms | any thread (KSerialization's type table) |
| Decompression | 445 ms | 2.5–2.8 s | any thread |
| Shader warm-up | 1.8 s | 1.8 s | GL thread, without the main thread waiting |
| Root deserialization, settings, Sim DLL load | 0.1 s | 0.03 s | main thread (game state, Sim DLL) |
| Object instantiation and deserialization | 5.8 s | 16.4–18.0 s | main thread (Unity objects, components' callbacks) |
| `OnSpawn` of every object, first frames | 9.6 s | 12.0–14.3 s | main thread |

The pure data work (read, header, decompression) is about 0.5 s of the mid-game load (2.5%) and
2.7–3.2 s of the late-game load (7%). The rest is world construction that has to stay on the main
thread.

Estimated saving per change:

- **Read and decompress from the save's selection:** a player picks a save, then clicks Load. A
  worker thread can read and decompress the file meanwhile, and `SaveLoader.Load` takes the
  result when it is for the same file and file time. Up to 0.5 s (mid) and 2.7–3 s (late) when
  the click comes after it is done; less after a quick click.
- **Overlapping only with teardown and the scene switch:** started at the click, about 0.45 s of
  it is hidden behind those (0.4–0.45 s in both saves).
- **Shader warm-up off the main thread:** `WarmupShaders` queued to the GL thread without
  `worker.Call`'s wait; the first draws wait for the programs they need, which are long built by
  then. 1.8 s on Linux with an empty driver cache, less with a warm one.
- **Parsing sections in parallel:** `SaveManager.Load`'s prefab groups carry their byte
  lengths, so their boundaries can be found ahead. But the parsing writes into components that
  `KInstantiate` has just made on the main thread, and components' deserialization callbacks run
  game code. A parallel parse into an intermediate form, applied on the main thread, could take
  part of the 3.3 s (mid) and 11.8 s (late) of deserialization at most. That means a second
  implementation of KSerialization's reading, which is a large exactness risk and touches every
  mod that patches deserialization.

## Options

**A. Presenter-thread animation during save loads.** Re-arm `GlSplash` (or a load-specific
overlay) when a scene load starts and the next frame is late; draw the last frame with
moving art and a progress estimate; fade out with the existing steady-frames rule.

- **Gain:** a moving window for the whole load: 18–33 s in ours where nothing moves now. No
  change to load time.
- **Exactness:** none affected. It is engine-only and draws after the game's frame is copied.
- **Mods:** unaffected. Loading Pip still shows its still sprite, and its Pip animation still
  freezes.
- **Risks:** GC pauses still stop it (0.2–0.5 s seen). It has to stay out of the way of a frame
  that comes. Linux has no DXGI-style discard of a superseded picture, so a redraw can delay the
  first colony picture by up to a refresh, as at start-up.

**B. Read and decompress at save selection, and warm shaders without waiting.** A worker thread
reads and decompresses the selected save. `SaveLoader.Load` (through a prefix on
`DecompressContents` and the read) takes the bytes when the file is unchanged. `WarmupShaders`
no longer waits for the GL thread.

- **Gain:** about 2.3 s (mid, 11%) and 4.5 s (late, 12%) of ours' load.
- **Exactness:** unaffected: the same bytes, decompressed by the same code
  (`fast-save-compression`'s, or the game's Ionic.Zlib when that tweak is off).
- **Mods:** the patch has to stand back for mods that take `DecompressContents` or
  `SaveLoader.Load` (Better Save does). It holds the decompressed save in memory between the pick
  and the click (its size was not measured here), dropped when another save is
  picked or the screen closes.

**C. Hand frames back to the player loop during the load.** Split `SaveManager.Load` and the
`OnSpawn` pass into chunks across frames, so the game's own loading screen (and Loading Pip's
animation) runs.

- **Gain:** real game frames, so any loading-screen content moves.
- **Not exact:** objects would exist and update across frames before the world is complete, and
  `Start`/`OnSpawn` order and timing would change. Every mod that assumes a load finishes in one
  frame is at risk.
- Not recommended.

A first and B second fits the goals: A answers "the screen does not move" with no exactness or
mod risk, and B shortens what A has to cover.

## Built: option B (2026-11-11)

- **The shader warm-up** no longer waits (`GlWarmup`, ARCHITECTURE.md): the GL thread builds the
  programs while it has no frame to execute. `ONI_WARMUP_AHEAD=0` restores the wait. The Vulkan
  device's warm-up (U60) is unchanged. Kept.
- **Reading and decompressing the save ahead** was built as a game tweak (`save-read-ahead`),
  measured, and dropped before any release (see below).

Measured as above (Linux U59, Xvfb, each run in its own run home, so Mesa's cache starts empty),
three runs per configuration, interleaved, under the perf queue; other agents' players ran beside
some of them. *Before*: both off. *Click*: both on, the load started from the click. *Selected*:
the save selected about 2 s before the click.

Click to interactive, s (each run; the median in bold):

| | Before | Click | Selected |
|---|---|---|---|
| Mid-game colony | 16.0, **16.3**, 18.7 | 14.7, **14.7**, 18.0 | 15.1, **15.1**, 41.8 |
| Late-game colony | 26.8, **30.9**, 31.2 | 25.6, **27.9**, 30.4 | 25.4, **26.0**, 30.6 |

The phases the two changes take over, ms (means of the three runs):

| Phase | Mid: before | Click | Selected | Late: before | Click | Selected |
|---|---|---|---|---|---|---|
| File read | 12 | 1 | 1 | 70 | 1 | 1 |
| Decompression | 297 | 51 | 13 | 1,550 | 1,243 | 215 |
| `Shader.WarmupAllShaders` | 1,403 | 17 | 17 | 1,449 | 14 | 16 |

- **The warm-up** saves about 1.4 s of every load with an empty driver cache. The 360 programs
  were built on the GL thread in 1.4–1.6 s, done well before the end of `SaveManager.Load`; no
  draw waited for one. Every other phase is the same within the runs' spread (the first colony
  frames vary by seconds between runs; one selected mid-game run's took 34 s, with another player
  loading beside it).
- **The read-ahead** gave the same bytes as the game's own read and decompression
  (`ONI_TWEAK_VERIFY=1`, 0 mismatches on both colonies, with `fast-save-compression` on and off),
  but was dropped:
  - From the click it saves only about 0.3–0.4 s: the late-game colony's decompression takes
    about 1.5 s on the thread and is not done when the load reaches it.
  - It holds the save read and decompressed (150 MB and 690 MB for the late-game colony) through
    the load; during a load from the pause menu that is on top of the old colony, raising the
    load's peak memory.
  - From the selection it saves more (1.3 s of decompression on the late-game colony), but players
    click through many saves on the Load screen, and each selection would cost about 1.5 s of CPU
    and 840 MB.
