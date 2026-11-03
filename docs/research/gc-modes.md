# GC modes: fast, low memory and a capped heap count (2026-11-03)

The question: what do the garbage collection option's two modes cost and save in play, does a
capped Server GC heap count do better, and is a device heuristic (by RAM or cores) justified?
The option is in Moono Tweaks, General ([TWEAKS.md](../TWEAKS.md#garbage-collection)).

**Verdict: no heuristic; Fast stays the default for everyone.** Low memory saves 0.8-0.9 GB of
settled working set on both colonies (about 8%), but only 0.3-0.4 GB at the peak, which comes
during the load and decides whether a PC short on RAM pages. It costs 4-8 s more GC pause while
loading (the load takes 8-13 s longer) and about ten times as many collections in play, though those
stay below 1% of the time and its longest pauses were no longer than Fast's. Six cores and
twelve threads change nothing: DATAS sizes the Server GC for them, and the numbers match those
on all sixteen threads. A capped heap count (4) saves nothing and pauses up to five times longer.

## Facts checked first

- `src/OniMoono.Host/OniMoono.Host.csproj` sets `ServerGarbageCollection`,
  `ConcurrentGarbageCollection` and `TieredPGO` to true.
- We start the runtime ourselves on Windows: OniMoono.exe (src/OniMoono.NativeHost/host.c)
  calls `hostfxr_initialize_for_dotnet_command_line` with this process's arguments, so the
  runtimeconfig's properties apply and the host can set more with
  `hostfxr_set_runtime_property_value` before `hostfxr_run_app`. Linux and macOS start through
  the SDK's apphost, which reads only `OniMoono.runtimeconfig.json`; there the option execs the
  player again with `DOTNET_gcServer` (TWEAKS.md).
- DATAS is on in our player on .NET 10 (10.0.9 on Windows): the log's `gc config:` line has
  `ServerGC=True`, `GCDynamicAdaptationMode=1` on Windows and Linux. `HeapCount` reads 16
  there, or 12 under a 12-thread affinity: under DATAS it is the most heaps the GC may use, not
  the heaps in use. `DOTNET_GCHeapCount=4` turns DATAS off (`GCDynamicAdaptationMode=0`, 4
  heaps). Workstation GC reports `HeapCount=1`.

## Method

- **Player**: the option on u60 at `9ef12422`, `scripts/build-windows.sh release`,
  default tweaks and renderer (Vulkan).
- **Game**: the frozen U60 761211 Windows install, with the all-DLC Steam double.
- **Saves**: c100 (200 Duplicants; managed heap about 9 GB) and soggy lab 2, vanilla on U60
  (managed heap about 5.5 GB; with compact-nav-links on, as by default).
- **Modes**: fast (as shipped: Server GC with DATAS, concurrent), cap4 (fast with
  `DOTNET_GCHeapCount=4`), low (`--gc low-memory`: Workstation concurrent GC, set by OniMoono.exe).
- **CPUs**: all (Ryzen 7 9800X3D, 8 cores, 16 threads, 94 GB) and 6c: affinity 0xFFF (6 cores,
  12 threads) from process creation (`start /affinity`), so the runtime sizes itself for 12, to
  stand in for a Ryzen 5 5600H laptop.
- **Runs**: PerfProbe at speed 3 with the busiest world active, `--frame-stats`, 1280x720 on
  monitor 2, `--no-focus`, High priority, sound off, 15,000 frames. The modes of one save and
  CPU set ran back to back; fast and low on all CPUs got a second launch each. Each run held the
  shared Windows lock from launch to kill; other agents' Linux players loaded the machine (load
  average 5-15) and its free RAM moved between 4 and 22 GB.
- **Windows** (from 120 s after the busiest world became active): frame rate (PerfProbe),
  main thread (the sum of the frame phases), working set settled (mean of the 600-frame memory
  lines) and the process's peak, managed heap, GC pause (ms per second, from
  `GC.GetTotalPauseDuration`), the longest pause (`GetGCMemoryInfo` at each frame's end) and
  collections a minute. The load's GC pause is PerfProbe's at the colony's first frame.
- **Linux** for direction (U60 761211, release build, Xvfb lanes through `scripts/e2e.py`
  in the shared queue, `E2E_MAX_PLAYERS=2`, 900 game seconds at speed 3; 6c with `taskset -c
  0-11`).

Frame rates moved with the machine far more than with the GC: soggy lab 2 on low memory ran
25.7 fps in one launch (main thread on its CPU 66% of the time) and 64.5 in the other; c100 on
fast 36.1 and 53.3. Read them as ±40%. The memory and GC numbers repeated within a few percent.

## Results (Windows)

| Save, CPUs | Mode | fps | Main ms | Working set settled / peak (MiB) | Heap (MiB) | GC ms/s | Longest pause ms | Gen0/1/2 a minute | Load s (its GC) |
|---|---|---|---|---|---|---|---|---|---|
| c100, all | fast | 36.1, 53.3 | 28.2, 19.1 | 10,580 / 11,565 | 9,005 | 1.2, 1.7 | 93, 105 | 0.8 / 0.5 / 0.26 | 31, 33 (1.7, 1.9 s) |
| c100, all | cap4 | 35.9 | 28.0 | 10,744 / 11,425 | 9,009 | 1.9 | 409 | 0.6 / 0.2 / 0.20 | 36 (1.8 s) |
| c100, all | low | 33.2, 59.6 | 30.4, 17.4 | 9,798 / 11,126 | 8,910 | 8.6, 9.4 | 82, 67 | 8.2 / 7.6 / 0 | 42, 41 (7.4, 6.4 s) |
| c100, 6c | fast | 40.6 | 26.3 | 10,574 / 11,531 | 8,930 | 0.8 | 74 | 0.5 / 0.5 / 0 | 32 (1.8 s) |
| c100, 6c | cap4 | 52.7 | 19.3 | 10,723 / 11,466 | 8,932 | 0.7 | 358 | 1.0 / 0 / 0.32 | 34 (1.9 s) |
| c100, 6c | low | 41.7 | 26.2 | 9,849 / 11,124 | 8,926 | 8.7 | 86 | 6.8 / 7.2 / 0 | 44 (7.6 s) |
| soggy, all | fast | 48.9, 55.4 | 20.5, 18.1 | 10,277 / 12,613 | 5,526 | 0.2, 0.9 | 256, 84 | 0.8 / 0 / 0.15 | 43, 32 (2.6, 1.3 s) |
| soggy, all | cap4 | 48.1 | 21.0 | 10,194 / 12,557 | 5,674 | 2.7 | 488 | 0.6 / 0.3 / 0 | 44 (2.4 s) |
| soggy, all | low | 25.7, 64.5 | 54.4, 15.8 | 9,366 / 12,327 | 5,535 | 2.8, 3.0 | 144, 35 | 6.6 / 1.2 / 0 | 56, 40 (10.8, 6.7 s) |
| soggy, 6c | fast | 39.4 | 34.1 | 10,082 / 12,676 | 5,590 | 0.7 | 110 | 0.3 / 0.2 / 0.16 | 42 (2.2 s) |
| soggy, 6c | cap4 | 55.0 | 18.0 | 10,201 / 12,738 | 5,663 | 3.3 | 419 | 0.9 / 0.4 / 0 | 35 (1.9 s) |
| soggy, 6c | low | 47.4 | 22.4 | 9,327 / 12,334 | 5,543 | 3.1 | 38 | 7.4 / 1.4 / 0 | 46 (7.8 s) |

Two values in a cell are the two launches; memory with two launches is their mean.

- **Memory.** Low memory's saving is the same on every CPU set: settled working set −0.73 to
  −0.91 GB, peak −0.29 to −0.44 GB. The managed heap is the same in all three modes (the colony's
  live data); what Workstation GC gives back is Server GC's spare room per heap. The peak is
  reached during the load in every mode, 1-2.4 GB above the settled working set.
- **GC in play.** Fast collects about once a minute (gen0, gen1, an occasional background gen2);
  low memory 8-16 times a minute, gen0 and gen1, and no gen2 in the windows. Its pause total is
  3-9 ms a second, below 1% of the time and 0.05-0.26 ms a frame. Its longest pauses were 35-144
  ms against Fast's 74-256: no longer.
- **Loading.** Low memory pauses 6.4-10.8 s for GC while a colony loads, Fast 1.3-2.6 s; the
  load takes 8-13 s longer (c100 31-33 → 41-44 s, soggy 32-43 → 40-56 s).
- **cap4.** No memory saved (settled and peak within 0.2 GB of Fast), and the longest pause
  358-488 ms, two to six times Fast's.
- **6 cores.** DATAS reports 12 heaps at most; memory, GC time and pauses match all 16 threads
  in each mode. The frame rate did not drop with fewer cores beyond the runs' spread.

## Linux (direction)

| Save, CPUs | Mode | Working set settled / peak (MiB) | GC ms/s | Longest pause ms | Gen0/1/2 a minute |
|---|---|---|---|---|---|
| c100, all | fast / cap4 / low | 11,220 / 12,109; 11,211 / 12,052; 10,661 / 11,629 | 2.4; 2.3; 8.8 | 245; 654; 91 | 0.8/0.5/0.17; 0.6/0.3/0.15; 7.0/8.1/0.18 |
| c100, 6c | fast / cap4 / low | 10,932 / 11,547; 11,062 / 11,952; 10,679 / 11,593 | 2.0; 3.0; 9.9 | 356; 487; 101 | 0.7/0.3/0.17; 0.5/0.5/0.16; 6.2/9.0/0.16 |
| soggy, all | fast / cap4 / low | 10,159 / 10,340; 10,213 / 10,576; 9,494 / 9,505 | 1.7; 1.3; 3.1 | 294; 354; 105 | 0.2/0.2/0.23; 0/0.2/0; 2.0/1.2/0 |
| soggy, 6c | fast / cap4 / low | 10,358 / 10,390; 10,041 / 10,093; 9,353 / 9,364 | 1.5; 1.6; 2.3 | 416; 424; 93 | 0/0.2/0; 0/0.2/0; 2.0/0.7/0 |

The same direction: low memory 0.25-1.0 GB below Fast, several times the collections, shorter
longest pauses; cap4 saves nothing and pauses longest. Two soggy players (cap4 on all CPUs, low
on 6c) ended without a trace in the log at about 10 GB, well inside the test slice's cap, in
different modes; both reran to the end and their second runs are the ones above.

## Recommendation

A device heuristic is not justified by these numbers:

- **Cores**: nothing to act on. With DATAS the Server GC already sizes itself to the CPUs it
  gets; on 6 cores and 12 threads memory and pauses are the same as on 16 threads.
- **RAM**: switching a small-RAM PC to low memory by itself would buy 0.3-0.5 GB at the load's
  peak and about 0.8 GB in play, of 11-13 GB, for loads 8-13 s longer, every time. That is the
  trade the option offers a player who sees paging; it is not one to make for them.
- **Capped heap count**: worse than both modes; not worth an option.

The project comment's "about 2 GB more memory" for Server GC (U57, before DATAS) is now about
0.8 GB in play on these colonies. If memory is to come down further, the levers are the live
data (as compact-nav-links did: 2.1 GiB on soggy lab 2) and the load's peak, not the GC mode.
To revisit: a real 16 GB laptop that pages on a large colony, measured with both modes.
