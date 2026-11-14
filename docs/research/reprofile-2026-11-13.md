# Re-profile against the original, 2026-11-13

Moono's two releases of 2026-11-13 against the original game of the same build: frame rates and
frame times, memory and managed allocations. Every measurement of the re-profile is on this
page; [PERFORMANCE.md](../PERFORMANCE.md) carries the headline tables.

- **Builds:** Moono v2026.11.13.02 (master 01c5dc35) on the game's U59 build 744825, and
  v2026.11.13.03 (u60 77a54e7b) on U60 build 763023, every DLC. The players are the released
  archives (Windows zip, Linux tar.gz), not local builds. The original is the isolated copy of
  the same game build (`E:\oni-builds\BrutalNotIncluded-744825` and `-763023`, through
  `scripts/original-windows.sh`; on Linux the frozen installs). No comparison crosses a build.
- **Machine:** native Windows 11, a Ryzen 7 9800X3D and an RTX 3090, as in PERFORMANCE.md;
  the allocation runs on the same machine's WSL2.
- **Colonies:** U57 (late game, cycle 17,077), 2minnow (mid game, cycle 1,990), Gluttony (cycle
  15,304, 67 Duplicants; the uncleaned save with its falling objects), c100 (204 Duplicants,
  cycle 99) and DeCrew (base game, cycle 17,628). All five load on U60.
- **Every run** went through runq as a `win-perf` or `linux-perf` job, with nothing else of the
  test machine's running beside it. The Windows desktop itself was in use during the day, which
  runq doesn't see; see [How far the launches agree](#how-far-the-launches-agree).

## Frame rates and frame times

### Method

Each launch is one job: a fresh run home, a warm-up launch to the main menu, then the save
resumed at 1280×720 on the second monitor, unfocused (`--no-focus`), at High priority, vsync
off, no mods but PerfProbe, which holds speed 3 (`awake.txt`, `noautosave.txt`, `novictory.txt`,
`frametimes.txt`). Presentation is the default (DXGI). Nothing records the screen.

- **Moono:** 9 windows of 600 frames, the first 3 left out.
- **The original:** windows of 200 frames for at most 240 s of play (170 s for the later
  launches: U60's and the second ones), the first 2 left out. At 5–6 fps (Gluttony) that keeps
  3 windows.
- **A launch's frame rate** is the mean of its kept windows' rates. **p50 and p99** are over
  every frame of the kept windows (PerfProbe's `frametimes.txt`; master's probe got u60's
  frame-time logging as a local patch, not committed).
- **Configurations:** the original; Moono with no launch option ("default tweaks"); Moono with
  `--game-tweak all` ("every tweak"), which adds the five tweaks that are off by default:
  `fetch-by-cell`, `off-screen-conveyors`, `sim-step-pace`, `brain-rate` and `off-world-fallers`.
- **Launches:** the headline tables give the median of a configuration's launches and, in
  brackets, the lowest and highest. Two launches were planned for each; to keep the Windows
  slot's time down, these ran once: c100 and DeCrew on both builds, U60's Gluttony, and the
  original everywhere but U57 and 2minnow on U59. A cell with a range is two launches, a cell
  without is one. Each table below lists every launch. A third round for U59's U57 and
  2minnow, whose two launches were far apart, was dropped: the runs cost more than the
  confidence they would have bought.
- **Load** is the log's first line to the colony. **Memory** is the process's, sampled every
  second: the working set's peak, private bytes near the end of the launch, and their peak.

Differences from the 2026-11-06 tables' method: `novictory.txt` and no autosaves (then: the
victory sequence left on, three autosaves at the end), 6 kept windows for 12, two or three
launches for three, and the original measured today instead of on 2026-10-22. `novictory.txt`
matters on 2minnow, which reaches a victory on load: with the sequence running, today's release
measured 136.5 fps with default tweaks (one launch), against 88.0 and 110.7 without it. Today's
2minnow rows are therefore not comparable with 2026-11-06's (133 fps); the controls below
measure the 2026-11-06 release today's way.

### U59: v2026.11.13.02 against the original (build 744825)

| Colony | Original fps | Default tweaks fps | | Every tweak fps | | p50 / p99 ms: original | default | every tweak |
|---|---|---|---|---|---|---|---|---|
| U57 | 11.6 (11.6–11.7) | 103.2 (94.1–112.2) | 8.9× | 140.9 (116.0–165.8) | 12.1× | 75.8 / 128.4 | 9.0 / 23.6 | 6.3 / 19.8 |
| 2minnow | 27.0 (26.4–27.6) | 99.4 (88.0–110.7) | 3.7× | 112.1 (100.8–123.4) | 4.2× | 33.3 / 56.8 | 9.4 / 23.3 | 8.6 / 17.7 |
| Gluttony | 6.1 | 28.6 (27.9–29.2) | 4.7× | 48.5 (47.4–49.7) | 8.0× | 151.0 / 267.6 | 33.5 / 58.2 | 19.8 / 39.0 |
| c100 | 22.0 | 126.4 | 5.7× | 188.7 | 8.6× | 44.6 / 65.7 | 7.7 / 14.2 | 5.1 / 10.7 |
| DeCrew | 31.9 | 177.1 | 5.6× | 206.1 | 6.5× | 28.4 / 57.6 | 5.1 / 14.9 | 4.1 / 15.0 |

Every launch:

| Colony | Player | Launch | fps | Windows kept (lowest–highest fps) | p50 ms | p99 ms | Longest frame ms | Load s | Peak working set GB | Private at the end GB | Peak private GB |
|---|---|---|---|---|---|---|---|---|---|---|---|
| U57 | Original | 1 | 11.6 | 10 (9.7–12.6) | 76.5 | 129.2 | 2124 | 132 | 10.8 | 15.4 | 15.5 |
| U57 | Original | 2 | 11.7 | 7 (10.6–12.4) | 75.1 | 127.6 | 2003 | 157 | 10.7 | 15.3 | 15.4 |
| U57 | Moono, default tweaks | 1 | 112.2 | 6 (94.8–119.2) | 8.1 | 21.5 | 30 | 24 | 13.6 | 13.9 | 14.2 |
| U57 | Moono, default tweaks | 2 | 94.1 | 6 (75.5–112.2) | 10.0 | 25.6 | 33 | 24 | 13.7 | 14.1 | 14.3 |
| U57 | Moono, every tweak | 1 | 165.8 | 6 (152.9–178.8) | 5.2 | 17.3 | 25 | 24 | 14.2 | 14.6 | 14.6 |
| U57 | Moono, every tweak | 2 | 116.0 | 7 (108.6–126.3) | 7.4 | 22.2 | 35 | 31 | 14.4 | 14.7 | 14.8 |
| 2minnow | Original | 1 | 27.6 | 10 (26.5–29.6) | 32.7 | 56.1 | 649 | 44 | 6.8 | 11.2 | 11.3 |
| 2minnow | Original | 2 | 26.4 | 7 (23.6–29.6) | 33.9 | 57.5 | 708 | 44 | 6.8 | 11.2 | 11.3 |
| 2minnow | Moono, default tweaks | 1 | 88.0 | 6 (70.9–110.2) | 10.1 | 29.7 | 44 | 20 | 10.4 | 10.6 | 10.6 |
| 2minnow | Moono, default tweaks | 2 | 110.7 | 6 (96.8–121.9) | 8.7 | 16.9 | 23 | 18 | 10.4 | 10.7 | 10.7 |
| 2minnow | Moono, every tweak | 1 | 123.4 | 6 (115.8–128.5) | 7.7 | 16.5 | 25 | 18 | 10.4 | 10.7 | 10.7 |
| 2minnow | Moono, every tweak | 2 | 100.8 | 6 (90.0–107.1) | 9.5 | 18.9 | 26 | 18 | 10.6 | 10.8 | 10.8 |
| Gluttony | Original | 1 | 6.1 | 3 (6.0–6.2) | 151.0 | 267.6 | 299 | 215 | 16.1 | 21.0 | 21.0 |
| Gluttony | Moono, default tweaks | 1 | 29.2 | 6 (27.6–30.0) | 32.9 | 55.5 | 189 | 32 | 16.9 | 17.5 | 17.6 |
| Gluttony | Moono, default tweaks | 2 | 27.9 | 6 (27.0–28.8) | 34.1 | 60.9 | 224 | 32 | 17.6 | 18.2 | 18.3 |
| Gluttony | Moono, every tweak | 1 | 47.4 | 6 (40.1–52.2) | 20.4 | 39.2 | 173 | 32 | 17.8 | 18.4 | 18.4 |
| Gluttony | Moono, every tweak | 2 | 49.7 | 6 (44.6–56.1) | 19.1 | 38.8 | 130 | 32 | 16.7 | 17.4 | 17.5 |
| c100 | Original | 1 | 22.0 | 10 (20.5–23.1) | 44.6 | 65.7 | 186 | 31 | 11.0 | 15.7 | 15.7 |
| c100 | Moono, default tweaks | 1 | 126.4 | 7 (109.4–133.7) | 7.7 | 14.2 | 49 | 15 | 11.3 | 11.5 | 11.5 |
| c100 | Moono, every tweak | 1 | 188.7 | 6 (153.6–201.2) | 5.1 | 10.7 | 18 | 15 | 11.1 | 11.2 | 11.3 |
| DeCrew | Original | 1 | 31.9 | 10 (20.1–35.7) | 28.4 | 57.6 | 3863 | 145 | 12.5 | 16.3 | 16.9 |
| DeCrew | Moono, default tweaks | 1 | 177.1 | 6 (164.2–185.7) | 5.1 | 14.9 | 22 | 23 | 13.7 | 14.2 | 14.3 |
| DeCrew | Moono, every tweak | 1 | 206.1 | 6 (192.4–220.1) | 4.1 | 15.0 | 28 | 22 | 14.0 | 14.4 | 14.4 |

### U60: v2026.11.13.03 against the original (build 763023)

| Colony | Original fps | Default tweaks fps | | Every tweak fps | | p50 / p99 ms: original | default | every tweak |
|---|---|---|---|---|---|---|---|---|
| U57 | 11.2 | 97.7 (94.8–100.6) | 8.7× | 154.3 (136.1–172.5) | 13.7× | 81.7 / 138.8 | 9.2 / 24.6 | 5.5 / 19.0 |
| 2minnow | 24.7 | 111.0 (108.3–113.7) | 4.5× | 94.6 (85.9–103.3) | 3.8× | 38.8 / 70.3 | 8.6 / 17.3 | 10.3 / 20.4 |
| Gluttony | 4.7 | 28.7 | 6.2× | 52.9 | 11.3× | 187.0 / 1153.7 | 33.4 / 57.3 | 17.9 / 35.3 |
| c100 | 18.2 | 117.4 | 6.5× | 172.6 | 9.5× | 54.0 / 78.7 | 8.1 / 17.1 | 5.5 / 12.8 |
| DeCrew | 25.5 | 157.7 | 6.2× | 195.2 | 7.6× | 36.9 / 70.9 | 5.7 / 15.2 | 4.5 / 13.9 |

Every launch:

| Colony | Player | Launch | fps | Windows kept (lowest–highest fps) | p50 ms | p99 ms | Longest frame ms | Load s | Peak working set GB | Private at the end GB | Peak private GB |
|---|---|---|---|---|---|---|---|---|---|---|---|
| U57 | Original | 1 | 11.2 | 7 (10.9–11.6) | 81.7 | 138.8 | 169 | 81 | 14.6 | 19.3 | 19.4 |
| U57 | Moono, default tweaks | 1 | 94.8 | 6 (85.0–106.4) | 9.5 | 25.6 | 45 | 25 | 11.6 | 12.7 | 12.8 |
| U57 | Moono, default tweaks | 2 | 100.6 | 6 (95.0–103.6) | 8.9 | 23.6 | 37 | 25 | 11.5 | 12.6 | 12.8 |
| U57 | Moono, every tweak | 1 | 172.5 | 6 (160.2–180.4) | 4.7 | 17.2 | 30 | 25 | 12.5 | 13.8 | 13.8 |
| U57 | Moono, every tweak | 2 | 136.1 | 7 (126.2–150.2) | 6.2 | 20.8 | 34 | 24 | 11.4 | 12.5 | 12.7 |
| 2minnow | Original | 1 | 24.7 | 7 (20.1–26.0) | 38.8 | 70.3 | 80 | 34 | 7.7 | 12.1 | 12.2 |
| 2minnow | Moono, default tweaks | 1 | 113.7 | 6 (100.7–124.8) | 8.5 | 16.1 | 28 | 19 | 8.4 | 9.6 | 9.8 |
| 2minnow | Moono, default tweaks | 2 | 108.3 | 6 (101.4–124.6) | 8.7 | 18.5 | 29 | 20 | 8.5 | 9.5 | 9.7 |
| 2minnow | Moono, every tweak | 1 | 85.9 | 6 (79.7–95.3) | 11.3 | 23.0 | 33 | 18 | 8.5 | 9.6 | 9.8 |
| 2minnow | Moono, every tweak | 2 | 103.3 | 6 (91.4–122.0) | 9.4 | 17.9 | 27 | 19 | 8.3 | 9.4 | 9.6 |
| Gluttony | Original | 1 | 4.7 | 1 (4.7–4.7) | 187.0 | 1153.7 | 1493 | 133 | 19.7 | 24.9 | 24.9 |
| Gluttony | Moono, default tweaks | 1 | 28.7 | 6 (27.2–29.8) | 33.4 | 57.3 | 145 | 34 | 16.5 | 17.8 | 17.8 |
| Gluttony | Moono, every tweak | 1 | 52.9 | 6 (50.6–55.4) | 17.9 | 35.3 | 49 | 34 | 15.6 | 16.8 | 16.9 |
| c100 | Original | 1 | 18.2 | 7 (17.2–19.1) | 54.0 | 78.7 | 98 | 26 | 11.4 | 15.9 | 15.9 |
| c100 | Moono, default tweaks | 1 | 117.4 | 6 (93.5–127.8) | 8.1 | 17.1 | 25 | 18 | 8.7 | 10.0 | 10.0 |
| c100 | Moono, every tweak | 1 | 172.6 | 6 (131.9–200.7) | 5.5 | 12.8 | 21 | 18 | 8.7 | 10.0 | 10.1 |
| DeCrew | Original | 1 | 25.5 | 7 (22.9–28.0) | 36.9 | 70.9 | 92 | 66 | 15.1 | 19.0 | 19.5 |
| DeCrew | Moono, default tweaks | 1 | 157.7 | 6 (145.6–168.3) | 5.7 | 15.2 | 28 | 24 | 12.3 | 13.1 | 13.2 |
| DeCrew | Moono, every tweak | 1 | 195.2 | 7 (188.0–203.0) | 4.5 | 13.9 | 23 | 23 | 11.9 | 12.8 | 12.8 |

### Speed 1 with `sim-step-pace` off and on

U57 on U59 at speed 1 (`speed.txt` 0), one launch each: default tweaks, and default tweaks with
`sim-step-pace` (`--game-tweak all` less the other four opt-in tweaks). The tweak acts only at
speed 1 above 60 fps.

| `sim-step-pace` | fps | p50 ms | p99 ms | Longest frame ms |
|---|---|---|---|---|
| off (default tweaks) | 159.2 (149.2–165.6) | 5.8 | 12.7 | 26 |
| on (default tweaks and `sim-step-pace`) | 169.5 (149.1–179.3) | 5.3 | 14.5 | 31 |

The frame rate rose by 6.5% and the median frame fell by 0.5 ms; p99 did not improve. One pair on a
day when two launches of one configuration differed by more than that
([below](#how-far-the-launches-agree)), so it shows the direction at most. The large gain in
[TWEAKS.md](../TWEAKS.md#simulation-at-its-own-pace) (112 → 220 fps) is from a large modded
colony whose frames waited a median 25 ms for the simulation; U57 without mods waits far less.

### How far the launches agree

Launches of one configuration differed by up to 43% today (U57 with every tweak on U59:
116.0 and 165.8 fps), far more than on 2026-11-06 (143.7–151.4). The original's launches
agree within 5%. The slower a frame, the steadier: Gluttony's launches are within 5%, the
fastest configurations vary most. The test machine's Linux side was quiet for every job (runq
flagged none as noisy); what else ran on the Windows side is not recorded. Read differences
under about 30% between two of today's Moono rows as unresolved.

### Controls: the 2026-11-06 release measured today's way, and the opt-in tweaks one at a time

One launch each, U59, on the same harness.

| Colony | Release | Tweaks | Victory sequence | fps (windows, lowest–highest) | p50 ms | p99 ms |
|---|---|---|---|---|---|---|
| 2minnow | v2026.11.13.02 | default | on, as on 2026-11-06 | 136.5 (126.9–148.1) | 6.9 | 14.2 |
| 2minnow | v2026.11.13.02 | none | off | 62.7 (55.3–73.3) | 15.8 | 26.6 |
| 2minnow | v2026.11.06.01 | every tweak | off | 156.1 (150.5–165.6) | 5.9 | 12.3 |
| 2minnow | v2026.11.06.01 | default | off | 115.7 (106.3–122.3) | 8.3 | 15.1 |
| 2minnow | v2026.11.06.01 | none | off | 74.2 (69.1–77.4) | 13.1 | 22.3 |
| U57 | v2026.11.06.01 | every tweak | off | 123.6 (100.8–140.6) | 6.7 | 24.0 |
| U57 | v2026.11.06.01 | default | off | 104.9 (101.7–107.0) | 8.4 | 23.4 |
| U57 | v2026.11.13.02 | default and `brain-rate` | off | 142.1 (130.8–150.4) | 6.0 | 19.1 |
| U57 | v2026.11.13.02 | default and `off-screen-conveyors` | off | 112.4 (91.8–123.1) | 8.0 | 22.4 |
| U57 | v2026.11.13.02 | default and `fetch-by-cell` | off | 80.9 (69.7–90.6) | 11.4 | 31.4 |

**U57 with default tweaks against the 2026-11-06 table.** The 2026-11-06 table has U57 at about
90 fps with default tweaks (88.9–90.2) and about 144 with every tweak (143.7–151.4); 144 is the
every-tweak row, not the default one. Today's release measures 94.1 and 112.2 fps with default
tweaks and 116.0 and 165.8 with every tweak, and the 2026-11-06 release, launched once today on
the same harness, 104.9 and 123.6. So with default tweaks today's release is at or above the
old table and the old release; with every tweak its launches straddle both. No regression
shows, and none can be excluded below the spread of today's launches.

**2minnow.** With `novictory.txt`, today's release measured 88.0 and 110.7 fps with default tweaks
and 100.8 and 123.4 with every tweak; the 2026-11-06 release 115.7 and 156.1 (one launch each), and
with no tweaks 74.2 against today's 62.7. Each of those single launches of the old release is
above today's median (two launches; one for no tweaks), by 16–39%. That is within what two
launches of one configuration differed by today, so it is not shown to be a regression, but it
is the one place where the old release came out ahead in every pairing, and it deserves an A/B
in one session (alternating launches, at least three each). With the victory sequence running, as on
2026-11-06, today's release measures 136.5 fps against that table's 133.

**Which opt-in tweaks make the gap between default and every tweak on U57.** Default tweaks
with one opt-in tweak added, one launch each (the rows above):
- `brain-rate`: 142.1 fps against the default's 94.1 and 112.2. It accounts for most of the
  gap, as on 2026-11-06 (leave-one-out then: 89.6–112.7 without it against 143.7–151.4).
- `off-screen-conveyors`: 112.4 fps, inside the default's range. 2026-11-06 measured it at
  about 0.8 ms a frame with every other tweak on, which a single launch can't resolve today.
- `fetch-by-cell`: 80.9 fps, below every default launch. On 2026-11-06 it left U57's frame
  unchanged on Linux. One launch, so not established: it may be the day's spread, or the
  tweak may cost frame time on U57 while `brain-rate` is off. It needs its own A/B.
- `sim-step-pace` does nothing at speed 3 (above), and `off-world-fallers` lands 3 objects on
  U57. On Gluttony `off-world-fallers` is most of the gap (default 27.9–29.2, every tweak
  47.4–49.7 fps on U59).

## Memory

U57 with default tweaks, no mods but PerfProbe (speed 3) and the census mod, 1280×720 on the
second monitor, unfocused, one launch each without a warm-up launch. The census mod reads the
process's counters when the colony is up, 1,800 frames later ("settled"), after a save it asks
for and the 30 s after it, 1,200 frames later ("the end"), and after a forced full collection.
The managed heap is .NET's committed GC memory and `GC.GetTotalMemory` in Moono, Mono's heap
size and used size in the original. The run lengths are the last row: the original draws the
same number of frames in about four times the time.

**U59** (v2026.11.13.02, build 744825)

| GB | Original | Moono |
|---|---|---|
| Private bytes, colony up | 14.50 | 12.73 |
| Private bytes, settled (1,800 frames on) | 15.41 | 13.99 |
| Private bytes at the end | 17.63 | 15.45 |
| Private bytes, peak | 17.77 | 15.70 |
| Private bytes at the end, after a full collection | 17.63 | 15.34 |
| Working set, settled | 10.61 | 13.59 |
| Working set, peak | 12.76 | 15.04 |
| Managed heap committed, settled | 6.43 | 7.35 |
| Managed heap in use, settled | 6.36 | 6.34 |
| Managed heap committed, at the end | 8.09 | 8.38 |
| Managed heap in use, at the end | 7.58 | 6.04 |
| A save's rise in private bytes (highest in the 30 s after it) | 2.37 | 1.69 |
| The run, colony up to the last census | 313 s | 77 s |

**U60** (v2026.11.13.03, build 763023)

| GB | Original | Moono |
|---|---|---|
| Private bytes, colony up | 16.98 | 11.84 |
| Private bytes, settled (1,800 frames on) | 18.11 | 13.15 |
| Private bytes at the end | 19.70 | 15.13 |
| Private bytes, peak | 19.94 | 15.27 |
| Private bytes at the end, after a full collection | 19.70 | 15.07 |
| Working set, settled | 13.32 | 11.86 |
| Working set, peak | 15.00 | 13.44 |
| Managed heap committed, settled | 8.88 | 7.37 |
| Managed heap in use, settled | 8.88 | 5.98 |
| Managed heap committed, at the end | 9.93 | 8.52 |
| Managed heap in use, at the end | 7.92 | 5.20 |
| A save's rise in private bytes (highest in the 30 s after it) | 1.85 | 2.13 |
| The run, colony up to the last census | 337 s | 79 s |

- Moono's private bytes are below the original's at every point on both builds: by 1.4–2.2 GB
  on U59 and 4.6–5.0 GB on U60.
- Its working set is above the original's on U59 and below it on U60. The original keeps
  several GB committed but paged out or never touched (private 15.4 GB against a working set of
  10.6 GB, settled, on U59); Moono touches nearly all it commits.
- The fps launches above agree: U57's peak working set is 13.6–14.2 GB in Moono on U59
  (2026-11-06: 16.2–16.8 GB) against the original's 10.8 GB (2026-11-06: 12.5 GB, with
  autosaves).

Against the earlier census: the census runs of 2026-11-12 (not in a doc) were of another
colony, a 2,560×1,440 run of a player's late-game save with `--frame-stats`, for 3,600 and
3,000 frames; they measured 11.1 GB private settled for Moono and 12.1 GB for the original on
U59, 10.2 and 12.7 GB on U60. Today's are of U57 at 1280×720 for 1,800 and 1,200 frames, so
the two sets differ by colony, resolution and length and neither replaces the other. A pair of
figures of 12.4 and 13.3 GB could not be traced to a run of this re-profile or to those logs;
the closest here are Moono's 12.7 GB with the colony up and 14.0 GB settled on U59, which
differ only by the 1,800 frames between them.

## Managed allocations

One launch for each player, build and colony (Linux under WSL2, Xvfb at 1280×720,
`scripts/alloc-compare.py` with PerfProbe's `alloc.txt` and `sites.txt`, Moono with default
tweaks after a warm-up launch, no autosaves, `novictory.txt`): the colony held paused for its
first 2,400 frames (`pausefirst.txt`), then run at speed 3. The frame cap is the published one,
20 fps on the late-game colony (U57) and 34 on the mid-game one (2minnow), for both players; the
original runs below it at speed 3. The paused span is the 1,200 frames after the first 600-frame
window; the running span starts 90 s after the colony starts running and lasts about 600 s for
Moono and about 480 s for the original. The original's counter is Boehm's process-wide total
(objects sized about a third larger for a plain object); Moono's is .NET's total, its sections the
main thread's. Collections are Boehm's for the original (full collections' time where it reports
it) and gen0/gen1/gen2 with the runtime's pause total for Moono.

**U59 (v2026.11.13.02, build 744825), late-game colony, 20 fps cap**

| | Original | Moono |
|---|---|---|
| Paused: KB a frame | 32.5 | 23.4 |
| Paused: fps | 20.0 | 20.2 |
| Paused: collections in the span | 0 | 0 |
| Speed 3: fps | 8.7 | 20.1 |
| Speed 3: frames a game second | 5.0 | 6.7 |
| Speed 3: KB a frame | 428.5 | 308.9 |
| Speed 3: MB a game second | 2.14 | 2.08 |
| Speed 3: MB a real second | 3.74 | 6.22 |
| Speed 3: the span | 482 s, 4200 frames | 626 s, 12600 frames |
| Speed 3: collections in the span | 2, 5.4 s of full collections | gen0/gen1/gen2 5/1/0, 405 ms paused in all |

By update section, paused, KB a frame:

| Section | Original KB a frame | Moono KB a frame |
|---|---|---|
| (other) | 14.0 | 8.2 |
| KBatchedAnimUpdater.LateUpdate | 6.0 | 3.2 |
| ArtifactModule RENDER_EVERY_TICK | 4.5 | 5.6 |
| GlobalChoreProvider RENDER_200ms | 2.6 | 1.5 |
| BrainScheduler.RenderEveryTick | 1.3 | 0.6 |
| PinnedResourcesPanel RENDER_1000ms | 1.1 | 0.3 |
| PropertyTextures.LateUpdate | 0.7 | 0.8 |
| LogicRibbonReader RENDER_200ms | 0.6 | 0.2 |
| Game.Update | 0.0 | 0.5 |
| MeterScreen RENDER_1000ms | 0.5 | 0.4 |
| Game.LateUpdate | 0.0 | 0.5 |
| KScreenManager.Update | 0.5 | 0.4 |
| (other threads) |  | 0.4 |
| Game.UnsafeSim200ms | 0.1 | 0.3 |
| LogicRibbonWriter RENDER_200ms | 0.2 | 0.1 |
| Total | 32 | 23 |

By update section at speed 3 (the largest; the original's counter is process-wide, so its other threads are inside its rows):

| Section | Original KB a game s | Moono KB a game s | Original KB a frame | Moono KB a frame |
|---|---|---|---|---|
| Game.UnsafeSim200ms | 411 | 405 | 82.1 | 60.1 |
| (other) | 348 | 324 | 69.7 | 48.0 |
| BrainScheduler.RenderEveryTick | 251 | 39 | 50.3 | 5.7 |
| (other threads) | (in the rows) | 211 |  | 31.2 |
| CanvasUpdateRegistry.PerformUpdate | 129 | 159 | 25.7 | 23.6 |
| Game.Update | 133 | 144 | 26.6 | 21.4 |
| Work(Pickup) | 136 | 131 | 27.3 | 19.5 |
| KBatchedAnimUpdater.LateUpdate | 83 | 51 | 16.6 | 7.6 |
| Global.Update | 76 | 56 | 15.2 | 8.3 |
| LaunchableRocketCluster states | 32 | 68 | 6.5 | 10.1 |
| HighEnergyParticle states | 48 | 41 | 9.7 | 6.1 |
| SolidTransferArm SIM_1000ms | 44 | 26 | 8.8 | 3.9 |
| Work(WorkerOilRefiller) | 38 | 42 | 7.7 | 6.2 |
| ArtifactModule RENDER_EVERY_TICK | 23 | 34 | 4.5 | 5.0 |
| RocketProcessConditionDisplayTarget SIM_1000ms | 31 | 26 | 6.2 | 3.8 |
| Total | 2,143 | 2,083 | 429 | 309 |

**U59 (v2026.11.13.02, build 744825), mid-game colony, 34 fps cap:** not measured (the launches did not finish).

**U60 (v2026.11.13.03, build 763023), late-game colony, 20 fps cap:** not measured (the launches did not finish).

**U60 (v2026.11.13.03, build 763023), mid-game colony, 34 fps cap:** not measured (the launches did not finish).

## What changed since 2026-11-06

See [PERFORMANCE.md](../PERFORMANCE.md#what-changed-since-2026-11-06).
