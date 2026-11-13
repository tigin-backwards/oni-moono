# Re-profile against the original, 2026-11-13 (interim)

**Interim: first launches only.** The second launches, the U60 colonies still queued, the
allocation tables and the write-up follow in this file; [PERFORMANCE.md](../PERFORMANCE.md) is
refreshed once they are in.

- **Builds:** Moono v2026.11.13.02 (master 01c5dc35) on the game's U59 build 744825, and
  v2026.11.13.03 (u60 77a54e7b) on U60 build 763023: the released Windows archives, not local
  builds. The original is the isolated copy of the same game build
  (`E:\oni-builds\BrutalNotIncluded-744825` and `-763023`, `scripts/original-windows.sh`). Every DLC.
- **Machine:** native Windows 11, Ryzen 7 9800X3D, RTX 3090, as in PERFORMANCE.md.
- **Method:** each launch is a runq `win-perf` job: a fresh run home, a warm-up launch to the
  main menu, then the save resumed at 1280×720 on the second monitor, unfocused (`--no-focus`),
  High priority, vsync off, no mods but PerfProbe holding speed 3 (`awake.txt`,
  `noautosave.txt`, `novictory.txt`, `frametimes.txt`). Moono: 9 windows of 600 frames, the first
  3 left out. The original: windows of 200 frames for at most 240 s of play (170 s on U60), the
  first 2 left out. A launch's frame rate is the mean of its kept windows; p50 and p99 are over
  every frame of the kept windows. "default" is no launch option; "all" is `--game-tweak all`,
  which adds `fetch-by-cell`, `off-screen-conveyors`, `sim-step-pace`, `brain-rate` and
  `off-world-fallers` to the defaults. Load is the log's first line to the colony. Memory is the
  process's, sampled every second.
- **Not as on 2026-11-06:** `novictory.txt` keeps 2minnow's victory sequence from starting, and
  autosaves are off. With the sequence running (as on 2026-11-06) the same build measures
  2minnow with default tweaks at 136.5 fps (one launch), against 88.0 without it: the two
  dates' 2minnow rows are not comparable. Controls with the 2026-11-06 release measured the
  same way are below.
- **Saves:** U57 (late game), 2minnow (mid game), Gluttony (the uncleaned save, with its falling
  objects), c100 (200 Duplicants), DeCrew (base game, cycle 17,628).

## U59 (master 01c5dc35, game build 744825)

| Save | Configuration | fps (launches) | mean | p50 ms | p99 ms | load s | peak WS GB | private end GB | peak private GB |
|---|---|---|---|---|---|---|---|---|---|
| U57 | orig | 11.6 | 11.6 | 76.5 | 129.2 | 131.5 | 10.8 | 15.4 | 15.5 |
| U57 | default | 112.2 | 112.2 | 8.1 | 21.5 | 23.7 | 13.6 | 13.9 | 14.2 |
| U57 | all | 165.8 | 165.8 | 5.2 | 17.3 | 24.4 | 14.2 | 14.6 | 14.6 |
| U57 | s1off | 159.2 | 159.2 | 5.8 | 12.7 | 23.6 | 13.7 | 13.7 | 14.4 |
| U57 | s1on | 169.5 | 169.5 | 5.3 | 14.5 | 23.6 | 13.7 | 14.1 | 14.3 |
| 2minnow | orig | 27.6 | 27.6 | 32.7 | 56.1 | 44.0 | 6.8 | 11.2 | 11.3 |
| 2minnow | default | 88.0 | 88.0 | 10.1 | 29.7 | 19.8 | 10.4 | 10.6 | 10.6 |
| 2minnow | all | 123.4 | 123.4 | 7.7 | 16.5 | 18.3 | 10.4 | 10.7 | 10.7 |
| Gluttony | orig | 6.1 | 6.1 | 151.0 | 267.6 | 214.8 | 16.1 | 21.0 | 21.0 |
| Gluttony | default | 29.2 | 29.2 | 32.9 | 55.5 | 32.0 | 16.9 | 17.5 | 17.6 |
| Gluttony | all | 47.4 | 47.4 | 20.4 | 39.2 | 31.8 | 17.8 | 18.4 | 18.4 |
| c100 | orig | 22.0 | 22.0 | 44.6 | 65.7 | 31.1 | 11.0 | 15.7 | 15.7 |
| c100 | default | 126.4 | 126.4 | 7.7 | 14.2 | 15.3 | 11.3 | 11.5 | 11.5 |
| c100 | all | 188.7 | 188.7 | 5.1 | 10.7 | 15.4 | 11.1 | 11.2 | 11.3 |
| DeCrew | orig | 31.9 | 31.9 | 28.4 | 57.6 | 144.8 | 12.5 | 16.3 | 16.9 |
| DeCrew | default | 177.1 | 177.1 | 5.1 | 14.9 | 22.6 | 13.7 | 14.2 | 14.3 |
| DeCrew | all | 206.1 | 206.1 | 4.1 | 15.0 | 22.0 | 14.0 | 14.4 | 14.4 |

`s1off` and `s1on` are the speed-1 pair: default tweaks, and default tweaks with `sim-step-pace`.

## U60 (u60 77a54e7b, game build 763023)

| Save | Configuration | fps (launches) | mean | p50 ms | p99 ms | load s | peak WS GB | private end GB | peak private GB |
|---|---|---|---|---|---|---|---|---|---|
| U57 | orig | 11.2 | 11.2 | 81.7 | 138.8 | 81.3 | 14.6 | 19.3 | 19.4 |
| U57 | default | 94.8 | 94.8 | 9.5 | 25.6 | 25.4 | 11.6 | 12.7 | 12.8 |
| U57 | all | 172.5 | 172.5 | 4.7 | 17.2 | 25.0 | 12.5 | 13.8 | 13.8 |
| 2minnow | default | 113.7 | 113.7 | 8.5 | 16.1 | 18.7 | 8.4 | 9.6 | 9.8 |
| 2minnow | all | 85.9 | 85.9 | 11.3 | 23.0 | 18.3 | 8.5 | 9.6 | 9.8 |

## Controls: the 2026-11-06 release (4f383caa) measured the same way today

One launch each, U59, `novictory.txt` on unless said.

| Launch | fps | p50 ms | p99 ms |
|---|---|---|---|
| c-new-2minnow-default-vict | 136.53 | 6.89 | 14.15 |
| c-old-2minnow-all | 156.08 | 5.94 | 12.32 |
| c-old-2minnow-default | 115.66 | 8.30 | 15.14 |

`c-new` is today's release, `c-old` the 2026-11-06 one; `vict` leaves the victory sequence on.

## Memory census, U59 (first run)

U57, no mods but PerfProbe and the census mod, default tweaks, 1280×720, speed 3: a census when
the colony is up, 1,800 frames later (settled), after a save and 30 s, 1,200 frames later (end).

```
m-census-ours
CENSUS up process: private 12729 MB (peak 14063), working set 12517 MB (peak 13379), pagefile 12729 MB
CENSUS settled process: private 13991 MB (peak 14063), working set 13590 MB (peak 13605), pagefile 13991 MB
CENSUS settled private committed by owner: unattributed 5535 MB (3233 reservations); managed GC 7350 MB (1 reservations); NT heaps 1003 MB (185 reservations); thread stacks 6 MB (148 reservations); executable (JIT code, stubs) 0 MB (2 reservations); census too
CENSUS settled managed: GC.GetTotalMemory 6,344 MB
CENSUS save window: private 13,975 MB -> max 15,663 MB (+1,687 MB)
CENSUS after-save process: private 15425 MB (peak 15698), working set 13311 MB (peak 15040), pagefile 15425 MB
CENSUS end process: private 15446 MB (peak 15698), working set 13325 MB (peak 15040), pagefile 15446 MB
CENSUS end private committed by owner: unattributed 5796 MB (3210 reservations); managed GC 8385 MB (1 reservations); NT heaps 1159 MB (225 reservations); thread stacks 6 MB (143 reservations); executable (JIT code, stubs) 0 MB (2 reservations); census tool it
CENSUS end managed: GC.GetTotalMemory 6,043 MB
CENSUS end-after-full-gc process: private 15343 MB (peak 15698), working set 13224 MB (peak 15040), pagefile 15343 MB
m-census-orig
CENSUS up process: private 14504 MB (peak 14504), working set 10138 MB (peak 10138), pagefile 14504 MB
CENSUS settled process: private 15415 MB (peak 15524), working set 10607 MB (peak 10809), pagefile 15415 MB
CENSUS settled mono: heap size 6432 MB, used 6361 MB
CENSUS settled private committed by owner: unattributed 14300 MB (3765 reservations); NT heaps 986 MB (106 reservations); thread stacks 14 MB (161 reservations); executable (JIT code, stubs) 28 MB (412 reservations); census tool itself 14 MB (1 reservations);
CENSUS settled managed: GC.GetTotalMemory 6,361 MB
CENSUS save window: private 15,400 MB -> max 17,774 MB (+2,373 MB)
CENSUS after-save process: private 17593 MB (peak 17774), working set 12577 MB (peak 12759), pagefile 17593 MB
CENSUS end process: private 17627 MB (peak 17774), working set 12624 MB (peak 12759), pagefile 17627 MB
CENSUS end mono: heap size 8085 MB, used 7580 MB
CENSUS end private committed by owner: unattributed 16481 MB (3927 reservations); NT heaps 1012 MB (109 reservations); thread stacks 14 MB (160 reservations); executable (JIT code, stubs) 29 MB (421 reservations); census tool itself 14 MB (1 reservations);
CENSUS end managed: GC.GetTotalMemory 7,579 MB
CENSUS end-after-full-gc process: private 17627 MB (peak 17774), working set 12624 MB (peak 12759), pagefile 17627 MB
```
