# Perceived smoothness: hitches, the autosave, camera motion and latency (2026-11-13)

The question: beyond the average frame rate, what makes Moono feel uneven in play, and what
would help? Four parts: a census of hitches (frames over 50 ms), the autosave freeze, camera
motion against the display, and input latency. This is a measurement and design study.
**Nothing landed.** The probes and prototypes are on the local branch `feel-u60`.

**Status: preliminary (2026-11-13, 22:40 UTC).** Three runs are still queued: the notes left
to the save's worker (a measurement of the bound), a split of `ReportManager`'s time, and a
Linux perf profile of the save. The places they will fill are marked "queued". Every figure
below is measured unless it says estimate.

Earlier work this builds on: [frame-pacing-and-jank.md](frame-pacing-and-jank.md) (the sim-step
frame; time-step smoothing, UI-only frames and queue depth rejected),
[gc-pauses.md](gc-pauses.md) (no collection after a save), [frame-timing.md](frame-timing.md)
(deltas rounded to whole refreshes), [input-latency.md](input-latency.md) (the queued-frames
wait), [sim-step-cost.md](sim-step-cost.md).

## Verdict

- **Hitches.** With vsync at 60 Hz a clean run has 37–74 frames over 50 ms per 10 minutes on the
  late-game and 200-Duplicant colonies and 11–32 on the mid-game one (the original there: 109–258). Almost all of them
  come from six sources, in order of time lost: the autosave, management screens opening (first
  time and every time after), the Decor overlay's sim step on the late-game colony, the frames
  after an autosave, GC pauses, and overlay switches. The sim-step frame of the earlier study is
  not a hitch source at 60 Hz: 6 frames over 50 ms in 46 minutes.
- **Autosave.** On the late-game colony 1.0–1.2 s of the 2.0–2.4 s is one component,
  `ReportManager`: 17,077 days of reports, 496 MB of the 707 MB the save writes. Writing its
  notes packed (the same bytes) took 0.25 s off in a prototype; the rest of it needs the
  profile that is still queued. Serialising on a worker thread is not feasible as the game
  stands. The presenter could pan the last picture during the freeze, but it is the weakest
  of the three options.
- **Camera motion.** With vsync, 1–3% of pictures are shown for a time other than the delta the
  camera moved by. A camera delta predicted from the display's grid recovers a third to a half
  of the little that is left on one colony and nothing on the others. Not worth building.
- **Frame cap.** No cap without vsync pans as evenly as vsync does (per-refresh step spread
  40–48% against 16–20%). The cap itself runs slow: a cap of 60 gives 57.0 fps.
- **Latency.** A frame's start reaches the screen in 48–49 ms when the queued-frames wait is on
  and 79–83 ms when it is off, on Vulkan and on OpenGL alike. On the late-game colony at speed 3
  the wait is always off; on lighter colonies it flips every 10–40 s, and each turn-on repeats
  two pictures. With the wait held on by a test switch both colonies keep 59.5 fps: the
  200-Duplicant colony pans more evenly than under the rule's flipping, and the late-game
  colony presents a refresh earlier for three times the repeated refreshes.

## Method

- **Players.** Moono u60 77a54e7b (release, default tweaks, Vulkan through DXGI) on U60 763023
  with every DLC; the original U60 763023 through `scripts/original-windows.sh`.
- **Machine.** Windows 11, Ryzen 7 9800X3D, RTX 3090; monitor 2 at 59.95 Hz, borderless
  fullscreen, vsync as the game sets it (on), `--no-focus`, High priority, muted. Every run a
  runq `win-perf` job.
- **Colonies.** A late-game Spaced Out colony (cycle 17,077, 24 Duplicants, about 91,000 saved
  objects); a 200-Duplicant colony; a mid-game colony with 50 Duplicants (cycle 1,990).
- **Probe.** `tools/PerfProbe/Jank.cs` from the frame-pacing study, extended (local, not
  committed):
  - a continuous recording with an event log: each scripted action with its frame and the time
    its call took;
  - seven phase marks a frame, the same in both players (scripts at the ends of the execution
    order, the cameras' callbacks, `WaitForEndOfFrame`): Update, between Update and LateUpdate
    (coroutines), LateUpdate, post-late (canvases), cameras, GUI and end of frame, and the rest
    (present, waits, input poll and early update of the next frame);
  - the section brackets of the earlier study (sim step, sub-ticks, animation, canvas, save);
  - GC counts and pause time per frame;
  - `SaveCost.cs`: brackets inside a save, by stage, prefab tag, component type and callback;
  - `DxgiHook.cs` and a marker square from the latency study, `Motion.cs` (the camera prototype).
- **Census script** (`out/feel/census.txt`), run once at speed 1 and once at speed 3, 7.6
  minutes each: every management screen opened twice (Vitals, Consumables, Priorities,
  Schedule, Skills, Research, Reports, Database, the starmap), 13 overlays switched (five of them
  twice), keyboard pans and wheel zooms through `CameraController`'s own state, a Duplicant and
  a building selected twice, three build-menu categories, 24 dig orders and 12 tiles planned,
  four camera jumps, pause and the three speeds, and the game's own autosave
  (`GameClock.DoAutoSave`), with 20–120 s of plain play between groups.
- **A probe error in every census run.** The script opens the base game's Starmap button and
  the Spaced Out one. All three colonies are Spaced Out colonies, where the base game's
  button has no screen: `ManagementMenu.OnButtonClick` threw `KeyNotFoundException: The given
  key 'ManagementMenu+ManagementMenuToggleInfo' was not present in the dictionary` four times
  a run (twice a speed), in both players; the probe caught it and went on. So the base-game
  Starmap was skipped, nine screens were opened, and "Starmap" below is Spaced Out's
  (`clusterMap`).
- **Attribution.** A frame over 50 ms goes to, in order: the save; the three frames after it; a
  GC pause covering 40% of its excess; the scripted action that ran in it or within three
  frames before; else the section or phase that rose most above its median.
- **What disturbed runs.** Another project's build on the WSL side (8–13 cores busy, outside
  runq) slowed every part of the frame by 2–3 times for minutes in two census runs; at speed 3
  the late-game colony then sat at the game's limit of 0.2 game seconds a frame (72 ms frames,
  14 fps). Both runs were repeated, and outside load was logged every 5 s from then on. The
  first late-game run is used only where it agrees with the repeat. A probe artefact was found
  and removed: PerfProbe's dialog search (170–400 ms) ran whenever a screen had paused the game.

## Part 1: hitch census

### The metric

Frames over 50 ms and over 100 ms per 10 minutes, vsync on at 60 Hz, clean runs:

| Colony | Speed | Frames | fps | p99, ms | >33 ms per 10 min | >50 ms per 10 min | >100 ms per 10 min |
|---|---|---|---|---|---|---|---|
| Late-game | 1 | 26,311 | 57.5 | 33.7 | 685 | 58 | 17 |
| Late-game | 3 | 25,164 | 55.0 | 34.7 | 608 | 74 | 30 |
| 200 Duplicants | 1 | 26,335 | 57.5 | 33.5 | 434 | 51 | 29 |
| 200 Duplicants | 3 | 26,389 | 57.7 | 33.3 | 320 | 37 | 20 |
| Mid-game | 1 | 26,886 | 58.8 | 33.3 | 331 | 32 | 9 |
| Mid-game | 3 | 26,763 | 58.5 | 33.5 | 440 | 11 | 1 |
| Mid-game, original | 1 | 22,266 | 48.6 | 40.3 | 1,361 | 109 | 41 |
| Mid-game, original | 3 | 17,917 | 39.1 | 50.6 | 2,926 | 258 | 22 |

The late-game repeat ran with `--frame-stats`. The script packs about an hour's worth of
screen and overlay use into 7.6 minutes and has one autosave per 7.6 minutes (the game saves
once per 10 minutes at speed 1 and per 3.3 minutes at speed 3), so the figures compare runs
with each other; they are not what a session of ordinary play would count.

### Every census run

All seven, with the two that were disturbed. Times are UTC on 2026-11-13; "outside load" is
the time the WSL side had more than 2 cores busy outside runq during the run (logged from
18:31).

| Run (runq job) | Time | Speed | Frames | fps | p50 / p99 / max, ms | >33 ms | >50 ms | >100 ms | >50 / >100 per 10 min | Note |
|---|---|---|---|---|---|---|---|---|---|---|
| Late-game, first (j0000470) | 17:31–17:48 | 1 | 25,671 | 56.0 | 16.6 / 39.5 / 2,849 | 589 | 109 | 14 | 143 / 18 | **disturbed** from 7 min in; 8 probe-artefact frames left out |
| | | 3 | 20,006 | 43.6 | 17.1 / 86.8 / 2,182 | 2,259 | 910 | 81 | 1,191 / 106 | **disturbed**: 2.5 min at 14 fps; not used |
| Late-game, repeat (j0000493), `--frame-stats` | 19:43–19:59 | 1 | 26,311 | 57.5 | 16.6 / 33.7 / 2,617 | 522 | 44 | 13 | 58 / 17 | 30 s of outside load |
| | | 3 | 25,164 | 55.0 | 16.6 / 34.7 / 2,092 | 463 | 56 | 23 | 74 / 30 | used |
| 200 Duplicants (j0000471) | 18:08–18:25 | 1 | 26,335 | 57.5 | 16.6 / 33.5 / 1,391 | 331 | 39 | 22 | 51 / 29 | used; load not logged yet, no slow stretch |
| | | 3 | 26,389 | 57.7 | 16.6 / 33.3 / 813 | 244 | 28 | 15 | 37 / 20 | used |
| Mid-game, first (j0000472) | 18:28–18:45 | 1 | 24,454 | 53.4 | 16.9 / 33.6 / 1,070 | 298 | 52 | 23 | 68 / 30 | **disturbed**: 4.4 min at 8–13 cores; not used |
| | | 3 | 26,995 | 59.0 | 16.6 / 33.1 / 759 | 199 | 7 | 2 | 9 / 3 | clean, agrees with the repeat |
| Mid-game, repeat (j0000520) | 21:28–21:45 | 1 | 26,886 | 58.8 | 16.6 / 33.3 / 1,022 | 252 | 24 | 7 | 32 / 9 | used |
| | | 3 | 26,763 | 58.5 | 16.6 / 33.5 / 755 | 335 | 8 | 1 | 11 / 1 | used |
| Mid-game, original (j0000519) | 21:11–21:28 | 1 | 22,266 | 48.6 | 17.1 / 40.3 / 4,845 | 1,040 | 83 | 31 | 109 / 41 | no outside load |
| | | 3 | 17,917 | 39.1 | 24.0 / 50.6 / 4,801 | 2,235 | 197 | 17 | 258 / 22 | |

Each speed's span is 457–459 s. The first late-game run and the first mid-game run used the
probe before its dialog search was taken out of census runs; the 200-Duplicant run and all
later ones used it after.

### Ranked sources

Over the clean runs of ours (45.7 minutes: the three colonies at both speeds). "Time lost" is
the frames' time over one refresh.

| Source | Frames >50 ms | of them >100 ms | Per 10 min | Time lost, ms per 10 min | Worst, ms | Seen on |
|---|---|---|---|---|---|---|
| Autosave: the save (one frame) | 6 | 6 | 1.3 | 1,726 | 2,617 | all |
| Management screen opened, first time | 34 | 15 | 7.4 | 1,099 | 1,391 | all |
| Management screen opened again | 36 | 13 | 7.9 | 669 | 301 | all |
| A stall before the frame (input poll or present), over 120 ms | 10 | 10 | 2.2 | 465 | 600 | all |
| Autosave: the frames after it | 13 | 9 | 2.8 | 445 | 391 | all |
| Decor overlay open: the sim step once a game second | 22 | 11 | 4.8 | 432 | 179 | late-game |
| Overlay switched, first time | 16 | 4 | 3.5 | 248 | 210 | all |
| GC pause | 13 | 4 | 2.8 | 245 | 249 | all |
| Present or device wait, under 120 ms | 16 | 0 | 3.5 | 129 | 87 | all |
| Management screen closed | 5 | 4 | 1.1 | 127 | 163 | 200 Duplicants |
| Overlay switched again | 9 | 1 | 2.0 | 106 | 152 | late-game |
| Sim step or sub-tick frame | 6 | 1 | 1.3 | 61 | 118 | late-game |
| Selecting a Duplicant or building | 4 | 0 | 0.9 | 37 | 68 | all |
| Other (LateUpdate, cameras, end of frame) | 9 | 3 | 2.0 | 142 | 157 | all |

### What each one is

- **Autosave.** One frame: 2.1–2.6 s late-game, 0.69–0.81 s on the 200-Duplicant colony,
  0.76–1.07 s mid-game. Part 2.
- **Management screens.** The cost follows the number of Duplicants, and re-opening is not
  free. Time lost in the four frames from the click, ms, for the four openings of a run (the
  last two at speed 3):

  | Screen | 200 Duplicants | Late-game (24) | Mid-game (50) | Mid-game, original |
  |---|---|---|---|---|
  | Consumables | 1,725 / 506 / 505 / 386 | 109 / 29 / 50 / 53 | 232 / 63 / 66 / 53 | 800 / 360 / 354 / 377 |
  | Skills | 687 / 183 / 184 / 216 | 68 / 39 / 22 / 112 | 133 / 41 / 34 / 33 | 374 / 310 / 149 / 142 |
  | Priorities | 538 / 224 / 159 / 131 | 40 / 0 / 16 / 17 | 105 / 13 / 16 / 11 | 324 / 229 / 232 / 223 |
  | Vitals | 434 / 151 / 83 / 93 | 35 / 17 / 16 / 10 | 42 / 3 / 1 / 0 | 247 / 142 / 144 / 121 |
  | Schedule | 343 / 216 / 116 / 100 | 267 / 54 / 58 / 61 | 194 / 67 / 57 / 57 | 637 / 346 / 395 / 393 |
  | Reports | 118 / 33 / 58 / 46 | 223 / 28 / 11 / 8 | under 25 | 202 / 88 / 111 / 104 |
  | Starmap (Spaced Out) | 20 / 26 / 16 / 34 | 140 / 83 / 96 / 86 | 30 / 15 / 33 / 17 | 190 / 123 / 112 / 116 |

  The first opening is in `KScreenManager` and the screen's own `OnShow` (Update phase), then
  canvas rebuilds in the next frame or two. `lazy-table-refresh` (on by default) is in these
  numbers. Closing Consumables costs 100–150 ms on the 200-Duplicant colony each time.
- **Decor overlay, late-game.** While the Decor overlay is open, `StepTheSim` takes 115–155 ms
  once a game second: every 46th frame at speed 1 and every 9th at speed 3, where the game
  drops to about 30 fps with a 150 ms frame three times a second. Both late-game runs show it;
  the other colonies do not. Not profiled yet: it is the first thing to look at after the
  autosave.
- **Overlay switches.** Late-game, time lost per switch, first / second: Decor 288 / 243,
  liquid pipes 236 / 87, Temperature 141 / 78 (and 86, 111 at speed 3), conveyor 113 / 41,
  Germs 99 / 55, Power 81 / 36. The time is in `Game.LateUpdate` (the overlay mode's enable)
  and canvas rebuilds. Under 70 ms on the 200-Duplicant colony.
- **The frames after an autosave.** Part 2.
- **GC pauses.** 39–135 ms, 13 in 46 minutes; several fell on a screen opening (allocation
  bursts). As gc-pauses.md: young collections, nothing new.
- **Stalls before the frame.** One burst on the late-game colony: seven frames in a row with
  126–560 ms in the early-update phase (the window's message pump), 1.8 s in all, with no
  device wait and no GC. It did not repeat; most likely desktop activity on the test machine.
  Singles of 32–44 ms in the present phase come about once a minute.
- **The queued-frames wait turning on** repeats two pictures (two 33 ms frames) each time:
  6 times in 15 minutes late-game, 25 on the 200-Duplicant colony, 14 mid-game. Under the 50 ms
  line, so not in the table; see Part 4.
- **Sim-step frames.** Six frames over 50 ms in 46 minutes. At 60 Hz the two-level frame of
  the earlier study fits the refresh.
- **Dig and build orders, camera jumps, speed changes, the build menu:** no frame over 50 ms
  that is theirs. Pause takes effect in the frame after the key (Part 4).

### The original, for reference

The original U60 763023 on the mid-game colony, the same script, borderless at 60 Hz, no
outside load during the run:

- **Count.** 109 frames over 50 ms per 10 minutes at speed 1 and 258 at speed 3, against
  Moono's 32 and 11. Its frame rate is 48.6 and 39.1 fps (Moono 58.8 and 58.5).
- **Autosave.** 4.8 s both times (Moono 0.76–1.02 s), then 183 and 105 ms frames.
- **Screens.** Every opening costs it 120–400 ms, the fourth as much as the second
  (Consumables 354–377 ms, Schedule 346–395 ms; Moono 53–66 and 57–67), and every closing
  40–250 ms.
- **Overlays.** 80–225 ms per switch, first time or not.
- **At speed 3** its ordinary frame is 24 ms with a p99 of 51 ms, so sub-tick and canvas frames
  cross the 50 ms line by themselves: 109 of its 197 hitches there.

## Part 2: the autosave freeze

### Where the main-thread time goes

Late-game colony, Windows, default tweaks, the game's own autosave, timed by brackets
(`SaveCost.cs`). The save writes 706.7 MB before compression. Three saves, each with one more
level of brackets; the first (stages and prefab tags only, 550 brackets hit 85,000 times) took
2,032 ms, the others 2,330–2,430 ms with 1.75 million bracket calls, so their per-type times
are upper bounds.

| Part | ms | Bytes | Calls | Notes |
|---|---|---|---|---|
| `SaveLoader.Save` in all | 2,032 | 706.7 MB | | 2.08–2.60 s in the census runs |
| **`ReportManager`: 17,077 days of reports** | **965–1,182** | **495.6 MB** | 1 | 70% of the bytes: each day's report entries through the serializer's templates, and their notes through `NoteStorage.Serialize` (three writes a note); the split is queued |
| The other 85,560 saved objects | about 840 | 211 MB | | |
| · `StateMachineController.Serialize` | 182–191 | 152.8 MB | 90,800 | type, suffix and state names as strings for every running state machine, 1.7 KB an object |
| · the loop per object (`GetComponents`, type checks, type names, lengths) | 356–408 with brackets | | 91,000 | an upper bound: it carries the brackets' own cost for 19 calls an object |
| · component data through the templates (752 types) | about 500 with brackets | 18 MB | 1.75 million | `fast-serialization`'s path |
| · `[OnSerializing]` and `[OnSerialized]` callbacks, all 26 that ran | 38 | | 251,316 | `PrimaryElement` 25, `Repairable` 8.5, `Operational` 2.5 |
| · `Modifiers`, `Storage`, conveyor contents | 26 + 20 + 28 | 3.4 MB | | |
| `RetireColonyUtility.SaveColonySummaryData` | 100–126 | | 1 | the colony summary file, rewritten every save |
| `Game.Save`, `Sim.Save`, old autosaves' headers, the rest | 8 + 3 + 6 + 4 | | | SimDLL's part is 3 ms |
| Compression and the file | 0 | | | on a worker (`background-save`) |

Callbacks against plain data writing: callbacks are 38 ms of 2,032 (2%). The save is data
writing, and half of it is the report history, which no longer changes once its day has
ended.

The buffer matters too: `SaveLoader.Save` makes a new `MemoryStream` of the last save's size
plus a tenth (777 MB here) for every save, so each save writes into memory the system has
not handed out yet; the first save of a session starts from nothing and grows by doubling
(2.56 s against 1.98–2.13 s for the next two).

Every save measured, `SaveLoader.Save` in ms by stage (stage and tag brackets only; three
autosaves a launch, 25 s apart; the first of a launch is slower):

| Launch (runq job) | Save | Total | `ReportManager`'s object (`SaveGame`) | The other objects | Colony summary | Compression on the main thread | `Save`'s own rest (the collection) |
|---|---|---|---|---|---|---|---|
| Default tweaks (j0000475) | 1st with brackets (2nd of the launch) | 2,032 | 1,030 | 842 | 126 | 0 | 3 |
| `--game-tweak exact` (j0000523) | 1 / 2 / 3 | 2,560 / 1,982 / 2,126 | 1,337 / 1,026 / 1,157 | 956 / 811 / 829 | 210 / 114 / 112 | 0 | 8 / 3 / 2 |
| exact, packed notes, `ONI_FAST_NOTES=1` (j0000522) | 1 / 2 / 3 | 2,233 / 1,707 / 1,896 | 989 / 755 / 924 | 986 / 811 / 822 | 202 / 111 / 117 | 0 | 8 / 3 / 2 |
| exact without `fast-serialization` (j0000524) | 1 / 2 / 3 | 2,984 / 2,447 / 2,447 | 1,878 / 1,544 / 1,554 | 863 / 758 / 759 | 197 / 121 / 108 | 0 | 5 / 1 / 1 |
| exact without `fast-save-type-checks` (j0000525) | 1 / 2 / 3 | 2,898 / 2,356 / 2,500 | 1,303 / 1,004 / 1,153 | 1,346 / 1,199 / 1,194 | 194 / 121 / 122 | 0 | 8 / 3 / 2 |
| exact without `background-save` (j0000526) | 1 / 2 / 3 | 3,605 / 3,127 / 3,226 | 1,285 / 1,014 / 1,176 | 941 / 833 / 804 | 196 / 128 / 116 | 1,126 / 1,121 / 1,099 | 8 / 3 / 2 |
| exact without `no-save-gc` (j0000527) | 1 / 2 / 3 | 3,555 / 2,185 / 2,170 | 1,262 / 977 / 964 | 927 / 820 / 816 | 198 / 101 / 104 | 0 | 1,121 / 260 / 259 |
| No tweaks (j0000477) | 1 / 2 / 3 | 8,627 / 7,535 / 7,388 | 1,808 / 1,571 / 1,480 | 1,253 / 1,149 / 1,069 | | 4,284 / 4,176 / 4,197 | 1,043 / 503 / 513 |
| No tweaks (j0000478) | 1 / 2 / 3 | 9,006 / 7,462 / 7,513 | 1,959 / 1,590 / 1,474 | 1,280 / 1,096 / 1,081 | | 4,493 / 4,195 / 4,137 | 1,027 / 450 / 693 |

The two "no tweaks" launches were meant to leave one tweak out each; `--game-tweak -NAME`
alone means no tweaks at all, so they are two launches of the same thing. A launch meant to
leave all five out (j0000476) failed in its script and measured nothing. The saved body was
498.63–498.66 MB for the `SaveGame` object in every launch.

The profile of the save (Linux perf, queued) goes here.

### What the save tweaks already cover

The same three autosaves a launch with stage brackets, `--game-tweak exact` with one tweak
left out; the mean of the second and third saves, ms:

| Tweaks | Save | Difference | Where |
|---|---|---|---|
| All (exact set) | 2,054 | | |
| Without `fast-serialization` | 2,447 | +393 | in `ReportManager`'s object (1,549 against 1,092): the report entries' fields |
| Without `fast-save-type-checks` | 2,428 | +374 | the other objects (1,197 against 820) |
| Without `background-save` | 3,177 | +1,123 | compression on the main thread, 1,110 with .NET's zlib |
| Without `no-save-gc` | 2,178 | +124 | the forced collection, 260 ms (1,121 ms after the first save) |
| None of the tweaks (two launches) | 7,388–7,535 | +5,400 | compression with the game's own zlib 4,140–4,200; the collection 450–690; `ReportManager`'s object 1,470–1,590; the other objects 1,070–1,150 |

`fast-save-compression` is the difference between the two compression figures, 3.1 s, when
compression is on the main thread; with `background-save` it shortens the worker's part.
`compact-report-history` and `fast-save-roots` are not save tweaks but sit on the path: the
first writes the notes, the second is not involved in a save.

### The slow frames afterwards

The same on all three colonies (late-game figures; 200 Duplicants and mid-game in brackets):

| Frame | ms | What |
|---|---|---|
| The save's frame, at its end | 54–88 | `Timelapser.Render` at `WaitForEndOfFrame`: `ReadPixels` of the whole screen into the freeze texture (a synchronous read-back), then the camera goes to the whole-colony view for the preview |
| +1 | 156–210 (165–167; 67–73) | animation updates for everything the preview's view shows, `KBatchedAnimUpdater.LateUpdate` 70–73 ms; `World.LateUpdate` 19–29; the cameras 25–50; 12 sub-ticks; then `RenderForTimelapser` draws the colony again into the preview target (15 ms at the end of frame) |
| +2 | 115–381 (75–391; 36–107) | the animation updates again, 71–77 ms; the main thread waits 0–343 ms for the device thread to draw the two whole-colony frames; 12 sub-ticks |
| +3, +4 | 33–164 | 12 sub-ticks each (the game's catch-up after a long frame), a device wait of 50–90 ms in some runs |

About 400–750 ms after the freeze. The preview's read-back is asynchronous in U60
(`AsyncGPUReadback`) and its PNG is encoded on a task; what is left is the freeze frame's
synchronous `ReadPixels`, a whole-colony view rendered twice (once by the game's own cameras
behind the freeze picture, once by `RenderForTimelapser`), and every animation in that view
updated in two frames.

### Option (a): the presenter pans the last picture during the freeze

- **What exists.** The presenter thread already shows the last picture again while no frame
  comes (`StallRepeats`, DXGI, for variable-refresh displays) and draws over it while a save
  loads (`loading-animation`). Redrawing the last picture with an offset is the same machinery.
- **What it needs.**
  - *The world without the UI.* The presented picture has the UI in it. Panning needs the
    world layer and the UI layer apart: one frame before the save rendered into two targets
    (the autosave announces itself about 1.5 s ahead through `Game.StartDelayedSave`), and a
    composite on the presenter. A full-screen target more, for the freeze only.
  - *Input on the presenter thread.* GLFW delivers input on the main thread, which is busy.
    Windows can read key and pointer state from any thread (reading, not moving); X11 can
    with a second connection; Wayland cannot.
  - *Edges.* A shifted picture shows nothing where the old view ended. Rendering the pre-save
    frame with a margin covers a small pan; a keyboard pan crosses a screen in about a second,
    so a 2 s freeze still runs into the edge. Zoom only scales pixels.
  - *The game's camera afterwards.* The game must end up where the presenter moved the view,
    so the pan is fed to `CameraController` after the save: a behaviour change, camera only.
- **What it gives.** The picture moves, but nothing in it does: Duplicants, fluids and the UI
  stay frozen, clicks still wait. Works on Vulkan and OpenGL (both present through the same
  DXGI path on Windows); Linux has neither the repeats nor the input.
- **Verdict.** Feasible, about two weeks with the Linux gap left open, and it hides the
  freeze only for a player who happens to be panning. Do (c) first; revisit if a 1 s freeze
  is still a complaint.

### Option (b): serialising on a worker with the game read-only

Static review of the U60 763023 code and of 2,275 decompiled Workshop mods. What stands in the way:

- **Unity calls per object.** `SaveLoadRoot.Save` reads the transform's position, rotation and
  scale and calls `GetComponents<KMonoBehaviour>()`; `SaveManager.Save` and `Write` use Unity
  null checks and `GetComponent<SimCellOccupier>()`; `Storage.Serialize`,
  `SolidConduitSerializer` and `StateMachineSerializer` call `GetComponent` per item. Our
  engine could make these readable off the main thread, but mods' patches on them expect the
  main thread.
- **Callbacks that change the game.** Of the 26 `[OnSerializing]` methods:
  - `PrimaryElement` (every object) reads its temperature through a callback into SimDLL's
    chunk memory and writes a hook's `deltaKJ`; its sanitizing can set the mass, and setting
    the mass can destroy the object;
  - `Operational` adds to `activeTime` and resets its start times from the game clock;
  - `OccupyArea` destroys an object standing on an invalid cell;
  - `Ref<T>` resolves and caches its target (`GetComponent<KPrefabID>`);
  - `StoredMinionIdentity`, `GeothermalVent`, `Repairable`, `LogicPorts` rewrite fields the
    game reads.

  These run on the main thread today, in the save's order. On a worker they race the frame.
- **The paused game is not still.** With the game paused `Game.Update` still calls
  `UnsafeSim200ms(0)`: SimDLL's frame, its callbacks (spawned ore, melts), the conduit, power
  and pathfinding per-frame updates; `Game.LateUpdate` runs the render-rate state machine
  updaters. `Sim.Save` (`SIM_BeginSave` returns a pointer into SimDLL's buffer) needs the sim
  to itself. Tools, side screens, priorities and schedules write saved fields, and a dig or
  build order registers a new saved object in the lists the save is walking (`foreach`: it
  throws).
- **KSerialization is single-threaded by design.** Its template and type-name caches are plain
  static dictionaries and `WriteKleiString` uses shared static buffers. The length fields are
  patched by seeking back in the stream.
- **Mods.** 48 of 2,248 mods with patches touch the save path. Among them:
  - IntegratedPumps and Fix Steam Turbine call `Game.UnsafeSim200ms(0)` from a prefix on
    `SaveLoader.Save(BinaryWriter)`;
  - ReplaceWireBridges swaps `SaveManager.sceneObjects` for the save and puts it back;
  - Decor Pack I, Spooky Pumpkin, Customize your paints, Outfits Included and Festive
    Halloween Spices rewrite live components before the save and restore them after;
  - Hide or Delete Asteroid destroys objects in a prefix; Colony Cuisine asserts the main
    thread;
  - 32 mods have `[OnSerializing]` or `[OnSerialized]` methods of their own, 4 of 13 sampled
    calling Unity or other objects; 501 mods save components of their own.

  Fast Save and BetterSave, the two mods built for this, both keep serialisation on the main
  thread and move only compression and the file write, as `background-save` does.
- **What would be thread-safe.** The report notes (a private structure nothing else writes
  while paused) and the compression. Everything that walks game objects is not.
- **Verdict.** Not feasible without changing what game and mod code observes. The one large
  piece that could leave the main thread, the notes, is better made cheap (c).

### Option (c): a shorter main-thread part

| # | Change | Exact? | Saving on the late-game colony | Evidence |
|---|---|---|---|---|
| 1 | The report notes written packed: `compact-report-history`'s arrays into a buffer, 9 bytes a note, in blocks | Yes: the same bytes; the tweak's verify mode compares them with the game's | **250 ms, 12% of the save**: `ReportManager`'s object 755–924 ms against 1,026–1,157 (second and third saves); the first save 989 against 1,337 | **Measured**, prototype behind `ONI_FAST_NOTES=1`, one launch of three saves each |
| 2 | The notes not written on the main thread at all: the save leaves their bytes out of the buffer (it only moves the position), and `background-save`'s worker fills them in from the store before it compresses; when a mod takes the body or `background-save` is off, they are filled in before `CompressContents` | Yes if the worker reads a snapshot of the store's records (its note arrays only grow) | Queued: a launch that skips the notes on the main thread (`ONI_FAST_NOTES=2`, its saves not valid) to measure the bound | Design; estimate until that run |
| 3 | The report entries of past days (the rest of `ReportManager`'s 0.75–0.9 s) | To be designed: they are game objects (`DailyReport`, `ReportEntry`) written through templates; a day that has ended does not change | Not known | Needs the profile; `fast-serialization` already takes 0.4 s off them |
| 4 | The first save's buffer sized from the loaded file (the body's length is known at load) instead of grown by doubling | Yes | Part of the first save's extra 430–580 ms | Estimate from the first save against the later ones |
| 5 | State machine entries: the type, suffix and state names' encoded bytes kept per name instead of encoded per write | Yes: the same bytes | Part of 182–191 ms | Estimate; needs the profile |
| 6 | The colony summary (`SaveColonySummaryData`) written on the save's worker, its data gathered on the main thread | Yes if the file ends the same | Up to 100–126 ms | Bracket; not prototyped |
| 7 | The freeze frame without a synchronous read-back (a GPU copy of the screen instead of `ReadPixels`) | Yes: the same picture shown | 54–88 ms of the save's frame | Phase marks; not prototyped |
| 8 | The preview's view rendered once: the game's own cameras skipped for the frame the freeze picture covers | The picture on screen is the same; the animation updates the preview relies on must still run | Part of 25–50 ms and of the device wait after | Estimate |

Measured so far: 1. With 1, 6 and 7 the late-game save would be about 1.6–1.7 s; getting to
about 1 s needs 2 and 3. Not proposed: a collection after the save (tried and rejected,
gc-pauses.md), and keeping the 777 MB buffer between saves (it would stay resident).

## Part 3: motion that matches the display

### The mismatch

`FramePacing` already rounds a vsynced frame's delta to whole refreshes. What is left is the
frame that is shown for longer than its delta said: a late frame moved by one refresh and
stays up for two, and the next one moves by two and stays up for one.

Measured with the DXGI hook: every Present with `GetFrameStatistics` (borderless, so each
sample names a picture's vertical blank), each frame matched to its Present, a marker square
checking the match (every one of 80–240 marker pictures a span showed its own frame's marker).
Keyboard pan at orthographic size 12, direction swapped every 180 frames; "held" frames are 60
or more into a direction.

| Run (speed 3 unless said) | fps | Pictures shown 2+ refreshes | Delta off its displayed interval | Picture speed >10% / >25% off | Per-refresh step spread | Repeated refreshes | Tracking error |
|---|---|---|---|---|---|---|---|
| Late-game | 59.4 | 0.8% | 1.8% | 3.6% / 2.7% | 10.9% | 0.8% | 4.2 px |
| Late-game, speed 1 | 58.8 | 1.1% | 2.4% | 5.2% / 3.9% | 19.3% | 3.0% | 6.3 px |
| Late-game, paused | 59.3 | 0.9% | 1.4% | 3.0% / 2.2% | 8.8% | 0.2% | 2.3 px |
| 200 Duplicants | 58.6 | 1.3% | 2.3% | 5.1% / 3.6% | 7.8% | 0.4% | 3.6 px |
| 200 Duplicants, speed 1 | 58.9 | 1.7% | 3.1% | 6.2% / 4.5% | 14.8% | 1.6% | 4.9 px |
| Mid-game | 58.1 | 0.7% | 1.6% | 3.9% / 2.6% | 8.6% | 0.2% | 3.8 px |

- **Picture speed:** the camera's step divided by the time its picture stayed up, against the
  median (as frame-timing.md).
- **Per-refresh step spread** (the judder metric): at every vertical blank, how far the picture
  on screen has moved since the one before (zero when a picture repeats); the standard
  deviation over the mean. Only stretches where every picture's display time is known.
- **Tracking error:** the shown position against a straight line through the four refreshes
  before and after, RMS, in pixels at 1440p (60 px a cell at this zoom): what an eye following
  the pan sees as shake.
- Frame statistics covered 85–95% of the pictures. Zoom was recorded but not scored: the wheel
  zoom is a smoothed lerp of the size, and its frames were as even as the pan's.

### The prototype: only the camera moves by the predicted display time

`Motion.cs` points the three reads of `Time.unscaledDeltaTime` in
`CameraController.NormalCamUpdate` at its own delta; nothing else in the game sees another
time. The delta is the number of refreshes between the vertical blank the last frame's picture
was predicted for and this frame's, taken at the camera's update. That update runs after
`Game.Update` (4.4 ms into a 11.7 ms frame late-game: input 0.15 ms, `Game.Update`
0.5–4.1 ms, the camera 4.5 ms), so the frame's sim work is already known when it predicts.

Replayed first on the recorded runs (CameraController's pan filter reproduces the recorded
steps exactly, so only the delta changes):

| Candidate delta | Late-game: speed off >10% / >25%, step spread, tracking | 200 Duplicants | Mid-game |
|---|---|---|---|
| As it is (rounded wall clock) | 3.6 / 2.7, 10.9%, 4.18 px | 5.1 / 3.6, 7.8%, 3.62 px | 3.9 / 2.6, 6.7%, 3.48 px |
| Predicted from the camera's update | 2.3 / 1.8, 9.7%, 3.90 px | 4.8 / 3.6, 8.1%, 3.79 px | 3.5 / 2.6, 6.1%, 3.41 px |
| The frame's own Present call on the grid (not knowable in time) | 1.9 / 1.5, 9.3%, 3.77 px | 4.7 / 3.5, 8.1%, 3.43 px | 3.1 / 2.3, 5.5%, 3.07 px |
| Oracle: its own displayed interval | 1.4 / 0.4, 9.4%, 3.63 px | 2.7 / 0.8, 7.6%, 3.33 px | 1.8 / 0.6, 5.4%, 2.84 px |

Even the oracle moves the judder metric by a tenth: a repeated refresh is a repeated refresh
whatever the camera does, and repeats are 0.2–3% of refreshes. The causal prediction gets a
third to a half of the oracle's gain on the late-game colony and none on the others, where
late frames are late on the device or presenter side, after the camera has moved.

Measured, the prototype against the game's delta, alternating spans in one launch:
| | Late-game: step spread, repeats, tracking | 200 Duplicants |
|---|---|---|
| The game's delta, three spans | 4.1%, 0.1%, 1.5 px; 15.3%, 1.2%, 4.3 px; 14.7%, 1.1%, 4.0 px | 27.1%, 4.4%, 8.0 px; 12.6%, 1.0%, 3.7 px; 11.7%, 0.7%, 2.6 px |
| Predicted delta, three spans | 15.4%, 1.3%, 4.4 px; 16.7%, 2.0%, 3.7 px; 17.4%, 1.8%, 3.9 px | 28.8%, 4.2%, 8.3 px; 17.9%, 1.9%, 4.1 px; 15.9%, 1.8%, 3.7 px |

No gain: the spans differ by how many refreshes repeated (0.1–4.4%), and the predicted delta
is within that spread on both colonies, if anything behind. With the replay this settles it:
**driving the camera from the predicted display time is not worth building** at 60 Hz. What
moves the judder metric is the number of repeated refreshes, which is frame cost and the
present queue (Part 4), not the delta.

### A frame cap

Late-game colony, speed 3, the same pan; without vsync a picture counts as shown from the
vertical blank after its Present call:

| Setting | fps | Frame interval sd | Per-refresh step spread | Repeated refreshes | Tracking error | Update to Present call | Update to display |
|---|---|---|---|---|---|---|---|
| vsync on | 59.2–59.5 | 2.8–3.3 ms | 16–20% | 1.5–1.8% | 4.1–5.4 px | 33 ms | 83 ms |
| vsync off, no cap | 102 | 16.6 ms (one hitch) | 116% | 5.7% | 28.6 px | 14.1 ms | about 41 ms |
| vsync off, cap 60 | 57.0 | 2.2 ms | 48% | 12.4% | 12.8 px | 12.5 ms | about 42 ms |
| vsync off, cap 120 | 93.8 | 4.2 ms | 45% | 5.0% | 14.7 px | 9.6 ms | about 40 ms |
| vsync off, cap 180 | 120.9 | 3.6 ms | 40% | 2.0% | 13.2 px | 8.1 ms | about 32 ms |

- **Evenness.** Every setting without vsync pans two to three times less evenly than vsync.
  The camera moves by the last frame's wall-clock time while frames alternate between 6–8 ms
  and 13–16 ms (the sim-step frame), and which frame a refresh catches varies.
- **A cap at the refresh rate is the worst case.** 57.0 fps against 59.95 Hz: a repeated
  refresh every eighth. The cap sleeps for what is left of each frame's own period, so every
  overrun (a sim-step frame, the timer's half millisecond) is lost and never caught up.
  Pacing against a running deadline would hold 60.0; even then a cap that is not locked to the
  vertical blank drifts through it.
- **A multiple of the refresh rate** needs frames that fit it: this colony reaches 94 of 120
  and 121 of 180.
- **Latency.** Without vsync a frame is presented 8–14 ms after its Update, against 33 ms.
  "Update to display" without vsync comes from the 9–37% of pictures the frame statistics
  named, so it is rough.
- **Verdict.** No cap replaces vsync for evenness. Two small things are worth doing whatever
  else happens: pace the cap against a running deadline (57 becomes 60), and say in the
  setting's text that a cap under the refresh rate with vsync on is the low-latency choice for
  variable-refresh displays. A cap locked to the vertical blank (presenting without vsync just
  before it) was not built; it is the only "smart cap" that could match vsync's evenness with
  less latency, and it needs the display's phase, which only DXGI gives us.

## Part 4: responsiveness

### Where input is sampled

Input is polled at the start of the frame (`PlatformLoop.Poll` in early update), after the
previous frame's present and after the queued-frames wait, so the wait does not age it. The
game dispatches it in `Global.Update`, 0.02–0.15 ms into the frame, before `Game.Update`; the
camera reads the pointer 0.7–4.5 ms in. The probe's marker changes in its own Update, the
first in the frame, so "Update to display" below is input to photon less the time from the
physical event to the next poll (half a frame on average).

### Update to display

Median over a pan of 1,500–3,600 frames, 60 Hz, borderless:

| Colony, state | Wait | Update to Present call | Update to display |
|---|---|---|---|
| Any colony, paused | on | 16.8 ms | 49 ms |
| Mid-game, speed 3, Vulkan | on (flips) | 16.8–17.0 | 48–50 (p90 72–76) |
| Mid-game, speed 3, OpenGL | on (flips) | 17.0–17.2 | 48–49 (p90 70–76) |
| 200 Duplicants, speed 1 | on | 16.8 | 48 |
| 200 Duplicants, speed 3 | flips | 33.2 | 61 (p10 46, p90 84) |
| Late-game, speed 1 | flips | 33.1 | 71 (p10 45, p90 90) |
| Late-game, speed 3, Vulkan | off | 33.2–33.3 | 79–81 |
| Late-game, speed 3, OpenGL | off | 33.3–33.6 | 81–83 |
| Mid-game, `maxQueuedFrames` 1, Vulkan / OpenGL | | 33.3 / 33.5 | 66 / 64, at 47 / 43 fps |
| Late-game, `maxQueuedFrames` 3 | off | 49.8 | 95 |

- **Two states, two refreshes apart.** With the wait on, a frame is presented one refresh
  after its Update and shown two refreshes later (48–49 ms, the original's figure). With it
  off, the main thread runs a frame further ahead and DXGI holds one more picture: 79–83 ms.
- **Vulkan and OpenGL with DXGI measure alike** in both states.
- **The late-game colony never gets the wait at speed 3.** Its stages sum to 11–12 ms, and the
  rule turns the wait on only under (sum + 1 ms) × 1.5 of a refresh, 10.1 ms.
- **Lighter colonies flip.** 13 turn-ons in 2.5 minutes on the mid-game colony, 25 in 15
  minutes on the 200-Duplicant one: latency moves between 48 and 80 ms, and each turn-on
  repeats two pictures to shed DXGI's extra one.
- **`maxQueuedFrames` 1** costs a fifth of the frame rate and is slower to the screen than 2.

### Options

**The wait held on, or its rule changed** (test switches in `QueuedFrames`, measurement
only: `ONI_QUEUED_WAIT=on`, `ONI_QUEUED_DROP=N`). Speed 3, two spans of 2,400 frames a launch.
The display's frame statistics did not update during these five launches (the window was
composed, not flipped), so they are compared at the Present call and by the pictures'
Present times on the refresh grid:

| Colony | Wait | Changes | fps | Update to Present call | Per-refresh step spread | Repeated refreshes | Tracking error |
|---|---|---|---|---|---|---|---|
| Late-game | the rule (off) | 4 | 59.5 | 33.3–33.4 ms | 16.5–18.0% | 1.3–1.8% | 4.4–5.1 px |
| Late-game | held on | 1 | 59.5 | 16.7 ms | 29.7–31.4% | 4.3–4.7% | 7.9–8.0 px |
| Late-game | the rule, off above 30% missed | 1 | 59.6–59.7 | 33.4 ms | 12.0–19.5% | 0.8–2.0% | 3.4–5.1 px |
| 200 Duplicants | the rule (flipping) | 19 | 57.8–58.4 | 17.2–17.4 ms | 28.0–32.6% | 4.7–5.4% | 7.9–9.0 px |
| 200 Duplicants | held on | 1 | 59.5–59.6 | 16.8 ms | 18.7–19.6% | 1.8% | 5.3–5.4 px |

- **Flipping is the worst state.** On the 200-Duplicant colony the rule changed its mind 19
  times in one launch (about 2.5 minutes of play). Held on, the same colony has a third of the repeated refreshes, 1.5 fps
  more and the same latency. The rule needs hysteresis: after it turns the wait off for
  misses, the next try should come later each time (2 s, 4 s, 8 s, up to a minute or so),
  and it should not turn on while the stages' sum is within a margin of the last sum it
  failed at. Exact (no game-visible change), small, and testable in `QueuedFramesTests`.
- **On the late-game colony the wait costs evenness, not frame rate.** Held on, the Present
  comes a refresh earlier (and by the two states measured above, the picture two refreshes
  earlier, 33 ms), at 59.5 fps either way, but three times as many refreshes repeat (4.5%
  against 1.5%): each sim-step frame that does not fit is a repeat instead of being absorbed
  by the queue. Which is better is a matter of taste; it could be a setting ("Lower input
  delay"), off by default.
- **The miss threshold is not the gate.** Raising it to 30% changed nothing on the late-game
  colony: the wait never turns on there because the stages' sum fails the turn-on test.
- **A shorter queue only when there is headroom** is what the rule already tries to be. The
  finer version (wait before ordinary frames, run ahead only before a sim-step frame, which
  the game's time accumulator predicts) would give the late-game colony most of the latency
  without the repeats. Not prototyped: an estimate, and the next thing to try after the
  hysteresis.

- **Sampling input later in the frame.** Input is already the first thing in the frame, and
  the frame's work sits between it and the present: 5–12 ms of main thread, then the device
  thread and the presenter. Polling again just before `CameraController.Update` would win
  0.7–4.5 ms of 48–81 and needs the game's input dispatch run twice; not worth it. The
  refreshes in the queue are where the time is.
- **Pause, speed and menus in the same frame.** They already are, as far as the game's own
  code allows: the key is dispatched before `Game.Update` in the frame that polls it, the
  time scale changes there, and the sim runs its last sub-ticks with the delta the frame
  started with, so the pause shows one frame later (as in the original; Unity fixes a frame's
  delta at its start). Changing that changes game-visible timing. No frame over 50 ms belongs
  to a pause or speed change in the census. What makes a menu feel late is the cost of
  opening it (Part 1) and the queue (above).

## Recommended order of work

| # | Work | Kind | Expected effect | Basis |
|---|---|---|---|---|
| 1 | The queued-frames wait stops flipping (hysteresis in `QueuedFrames`) | Exact, small | On colonies near the line: repeated refreshes 4.7–5.4% to about 1.8%, 1.5 fps more, latency steady at 48 ms instead of moving between 48 and 80 | Measured with the wait held on |
| 2 | Autosave, main thread: packed notes; then the notes filled in by the save's worker; then the report entries after a profile; the colony summary on the worker; the first save's buffer | Exact tweaks | Late-game save 2.05 s to 1.8 s with the first step (measured), about 1.0–1.3 s with all of them (estimated) | Brackets; one prototype measured |
| 3 | The frames after an autosave: the freeze frame as a GPU copy, the preview's view rendered once | Exact | 400–750 ms of slow frames to perhaps 250–450 ms (estimated) | Phase marks |
| 4 | The Decor overlay's sim step on the late-game colony: profile it first | Unknown | A 115–155 ms frame once a game second while the overlay is open | Measured, cause not known |
| 5 | Management screens on colonies with many Duplicants (the table screens' work goes on): Consumables 1.7 s on first opening and 0.4–0.5 s every time after with 200 Duplicants | Tweaks, as `lazy-table-refresh` | The largest interaction hitch after the autosave | Measured |
| 6 | Overlay switches that cost every time (Temperature 78–111 ms, liquid pipes 87–236 ms, Decor 243–288 ms late-game) | Tweaks | 2–5 frames over 50 ms per 10 minutes of overlay use | Measured |
| 7 | The frame cap paced against a running deadline | Exact, small | A cap of 60 gives 60.0 instead of 57.0 fps | Measured (the shortfall), the fix estimated |
| 8 | A "lower input delay" setting (the wait held on), or the wait run ahead only before sim-step frames | Behaviour of presentation only | Late-game at speed 3: about 33 ms less latency; with the plain setting three times the repeated refreshes | Measured for the plain setting, estimated for the predictive one |

Not worth doing: a camera delta from the predicted display time (measured, no gain); polling
input later in the frame (0.7–4.5 ms of 48–81); serialising on a worker (not feasible);
`maxQueuedFrames` 1 (slower and later). Parked: the presenter panning the last picture during
the freeze, until the save is as short as (2) can make it.

## Builds, commits and runs

- **Date:** 2026-11-13, 17:28–22:36 UTC, on the test machine (Windows 11, Ryzen 7 9800X3D,
  RTX 3090; monitor 2 at 59.951 Hz, refresh measured 16.681 ms).
- **Moono:** u60 at 77a54e7b, release, built in the worktree `out/feel-u60` (branch
  `feel-u60`: 77a54e7b plus the jank probe's commit and uncommitted probe and prototype
  changes). The engine and host are 77a54e7b's until 18:58 UTC; from then the host also holds
  the packed-notes switch (off unless `ONI_FAST_NOTES` is set), and from 22:18 the
  `QueuedFrames` test switches (off unless set). Neither changes a run that does not set them.
- **Game:** U60 763023 with every DLC (`E:\oni-builds\oni-win-763023`); the original from
  `E:\oni-builds\BrutalNotIncluded-763023`.
- **Probe:** `tools/PerfProbe` built against 763023: `Jank.cs` (census commands, phases,
  events), `SaveCost.cs`, `Motion.cs`, `DxgiHook.cs` and `Clock.cs` (from the `latency-probe`
  branch). Two builds: `release` (census and saves) and `motion`.
- **Player arguments:** `--splash none --log-paths full --no-focus`, the offline Steam double;
  census, motion, cap and latency runs borderless on monitor 2
  (`-screen-fullscreen 1 -window-mode borderless -monitor 2`); save runs windowed 1280×720.
  The game's own vsync setting (`vSyncCount 1`), its sleep while unfocused turned off by the
  probe (`nosleep.txt`), its autosave off except the ones the script asks for.

| Job | UTC | What | Outside load |
|---|---|---|---|
| j0000469 | 17:28–17:31 | smoke test of the census probe (mid-game) | not logged |
| j0000470 | 17:31–17:48 | census, late-game, first | not logged; disturbed |
| j0000471 | 18:08–18:25 | census, 200 Duplicants | not logged |
| j0000472 | 18:28–18:45 | census, mid-game, first | 265 s; disturbed |
| j0000475 | 18:45–18:49 | save brackets by level, default tweaks | 0 |
| j0000476 | 18:49–18:52 | save, five tweaks off: failed (script) | |
| j0000477, j0000478 | 18:52–18:58 | save, no tweaks, twice | 0 |
| j0000482, j0000483, j0000484 | 19:11–19:23 | camera motion: late-game, 200 Duplicants, mid-game | 0, 5 s, 35 s |
| j0000485 | 19:23–19:28 | frame caps, late-game | 0 |
| j0000486, j0000487 | 19:28–19:35 | latency and queue depth, Vulkan: late-game, mid-game | 0 |
| j0000488, j0000489 | 19:35–19:43 | the same, `--renderer opengl` | 0 |
| j0000493 | 19:43–19:59 | census, late-game, repeat, `--frame-stats` | 30 s |
| j0000519 | 21:11–21:28 | census, mid-game, the original | 0 |
| j0000520 | 21:28–21:45 | census, mid-game, repeat | 0 |
| j0000522 | 21:45–21:48 | save, exact, packed notes | 0 |
| j0000523–j0000527 | 21:48–22:03 | save, exact; without fast-serialization; without fast-save-type-checks; without background-save; without no-save-gc | 0 |
| j0000529, j0000530 | 22:09–22:18 | camera prototype A/B: late-game, 200 Duplicants | 0 |
| j0000532–j0000536 | 22:19–22:36 | the wait held on, drop threshold 30, the rule: late-game; held on, the rule: 200 Duplicants | 0 |

runq keeps a job's folder for 14 days; the per-frame CSVs and logs are copied to
`out/feel/win` in the worktree.

## Runs

Local, not in the repository: branch `feel-u60` (worktree `out/feel-u60`), `out/feel`
(scenarios `census.txt`, `savecost.txt`, `savestages.txt`, `saveperf.txt`, `motion-base.txt`,
`motion-ab.txt`, `motion-caps.txt`, `latency.txt`, `latency1.txt`; `win-job.sh`, a runq job;
`linux-run.py`; `census.py`, `rank.py`, `events.py`, `motion.py`, `saveperf.py`), the runs'
logs and per-frame CSVs under `out/feel/win`, and the outside-load log `cpuload.log`.
