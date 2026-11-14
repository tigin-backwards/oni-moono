# The slowest 1% of frames: what is in them and what would raise the 1% low (2026-11-14)

The question: the average frame rate overstates how the game feels, because many short frames
pull the mean up. What are Moono's slowest 1% of frames made of during steady play, and which
exact changes (the same results in the same order) would raise the "1% low"?

Earlier work this builds on and does not repeat:
[frame-pacing-and-jank.md](frame-pacing-and-jank.md) (the two-level frame, the sim-step frame as
84–85% of spike time), [sim-step-cost.md](sim-step-cost.md) (the five-step cycle),
[conduit-updates.md](conduit-updates.md) (pipes and rails),
[perceived-smoothness.md](perceived-smoothness.md) (hitches over 50 ms, the autosave, vsync) and [reprofile-2026-11-13.md](reprofile-2026-11-13.md)
(the launches whose frame logs Part 1 reads).

**Definitions.** Tools differ, so both are given throughout:
- **1% low** = 1,000 / the p99 frame time: the frame rate that 1% of frames are slower than.
  **0.1% low** = 1,000 / p99.9.
- **Mean of the slowest 1%** = 1,000 / the mean frame time of the slowest 1% of frames (the
  other common definition; always lower). Likewise for 0.1%.
- A launch of the re-profile keeps 3,600 frames (4,200 in a few), so its 1% is 36 frames and its
  0.1% is 4: read the 0.1% columns of Part 1 as rough.

## Verdict

- **The 1% low is 39–78 fps where the average is 85–195 fps** (U60, Windows, speed 3, default
  tweaks and every tweak, four colonies; Gluttony 17.5–28 against 29–53; measured, Part 1). The
  slowest 1% of frames take 1.9–3.5% of the time.
- **On the late-game colony the slowest 1% are one thing: the sim step that carries the liquid
  pipes and the conveyor rails**, once a game second, three times a real second at speed 3
  (measured, Part 2). 69% of the slowest 1% are that step's frame, 9% the frame right after it,
  18% another sim step's frame, 1% a GC pause. It is not a pile-up of periodic work: once-a-second
  render updates fall on 3% of them and collections on 1%.
- **It is the same batch every time, on a slower frame under it.** A median frame with that step
  takes 26.7 ms against 9.9 ms without a step: 11.2 ms is the step (pipes 4.4, rails 3.9, power,
  plants and the rest 2.9), and about 5.5 ms more comes with it in the same frame (1,000 ms sim
  buckets that always fall on that step's sub-tick, more animation and LateUpdate work). The
  frames that reach the slowest 1% are the fifth of those where everything else in the frame is
  slow too (2.5 sub-ticks against 1.5, Duplicant and critter brains 2.6 ms against 1.6).
- **Why the 1% low hardly moves when the average does:** that step is a fixed 3 frames a real
  second, 3–4% of frames at 80–100 fps, so the p99 frame always sits inside its frames (at
  their 81st percentile in the profiled run). Tweaks that shorten every frame move it by their
  saving only; the step's 11 ms stays.
- **No exact change found is worth more than about 1 fps of 1% low on its own** (estimates from a
  replay of the profiled run, Part 3). All of the exact candidates together come to about
  +1.5 fps. A millisecond off every frame is worth +1.3 fps of 1% low, a millisecond off that
  step +0.7.
- **Moving the coinciding work apart is possible but not exact.** The liquid pipes and the rails
  share a step because Klei starts both timers at 0.75 s; the 1,000 ms sim buckets that follow
  are tied to the same phase. Moving either changes the order of updates. Estimated for
  reference only: +1.6 and +1.2 fps of 1% low.
- **The 0.1% low** (24–35 fps) is the same step on the busiest frames, plus one event class of
  its own: a GC pause of 34–37 ms and the catch-up frames after it (8 of the 24 slowest frames
  in 295 s came from three collections).
- **With vsync at 60 Hz** the 1% low is 30–37 fps on every colony (a frame of two refreshes),
  and stays there until that step's frame fits one refresh; with every tweak its p99 is
  17–21 ms, still over.
- **Tracking:** `scripts/perf-spread.py` now reports the median frame, the p99 frame and the 1%
  low with B/A, never judged (Part 4). The nightly's summary table carries them.
- **Nothing else landed.** The probe's brackets are local (branch `onepct-u60`).

## Part 1: the numbers, from existing data (no new runs)

**Source.** The re-profile's per-frame logs (PerfProbe's `frametimes.csv`): released players
v2026.11.13.03 (U60 763023) and v2026.11.13.02 (U59 744825), Windows 11, Ryzen 7 9800X3D,
RTX 3090, 1280×720 on the second monitor, unfocused, High priority, vsync off, speed 3, no
autosave; a launch's first 3 windows of 600 frames left out (the original: 200-frame windows,
the first 2 left out). One or two launches a cell, as that page lists. "Every tweak" is
`--game-tweak all`.

### U60 (763023)

| Colony | Configuration | Launch | Frames | Average fps | Median fps | 1% low fps (1,000 / p99) | Mean of the slowest 1%, fps | 0.1% low fps (1,000 / p99.9) | Mean of the slowest 0.1%, fps | p50 / p99 / p99.9 / max ms | Slowest 1%: share of frame time |
|---|---|---|---|---|---|---|---|---|---|---|---|
| U57 | Original | 1 | 1,400 | 11.2 | 12.2 | 7.2 | 6.9 | 6.7 | 5.9 | 81.7 / 138.8 / 149.2 / 169 | 1.6% |
| U57 | Default tweaks | 1 | 3,600 | 94.3 | 105.5 | 39.0 | 33.5 | 28.8 | 25.4 | 9.5 / 25.6 / 34.7 / 45 | 2.8% |
| U57 | Default tweaks | 2 | 3,600 | 100.5 | 111.9 | 42.4 | 38.5 | 35.4 | 31.6 | 8.9 / 23.6 / 28.2 / 37 | 2.6% |
| U57 | Every tweak | 1 | 3,600 | 172.2 | 211.0 | 58.1 | 51.1 | 45.6 | 38.9 | 4.7 / 17.2 / 21.9 / 30 | 3.4% |
| U57 | Every tweak | 2 | 4,200 | 135.7 | 161.3 | 48.0 | 41.8 | 35.8 | 32.0 | 6.2 / 20.8 / 27.9 / 34 | 3.3% |
| 2minnow | Original | 1 | 1,400 | 24.6 | 25.8 | 14.2 | 13.5 | 12.5 | 12.5 | 38.8 / 70.3 / 80.2 / 80 | 1.8% |
| 2minnow | Default tweaks | 1 | 3,600 | 113.0 | 117.9 | 62.1 | 54.2 | 48.2 | 41.2 | 8.5 / 16.1 / 20.8 / 28 | 2.1% |
| 2minnow | Default tweaks | 2 | 3,600 | 107.8 | 114.8 | 54.0 | 46.6 | 39.0 | 37.2 | 8.7 / 18.5 / 25.6 / 29 | 2.3% |
| 2minnow | Every tweak | 1 | 3,600 | 85.4 | 88.7 | 43.5 | 39.6 | 35.3 | 33.7 | 11.3 / 23.0 / 28.3 / 33 | 2.2% |
| 2minnow | Every tweak | 2 | 3,600 | 102.3 | 106.8 | 56.0 | 49.5 | 42.8 | 39.5 | 9.4 / 17.9 / 23.4 / 27 | 2.1% |
| Gluttony | Original | 1 | 200 | 4.7 | 5.3 | 0.9 | 0.8 | 0.7 | 0.7 | 187.0 / 1153.7 / 1493.3 / 1493 | 6.2% |
| Gluttony | Default tweaks | 1 | 3,600 | 28.6 | 29.9 | 17.5 | 15.2 | 13.3 | 10.2 | 33.4 / 57.3 / 75.0 / 145 | 1.9% |
| Gluttony | Every tweak | 1 | 3,600 | 52.8 | 55.8 | 28.3 | 26.2 | 23.2 | 21.8 | 17.9 / 35.3 / 43.1 / 49 | 2.0% |
| c100 | Original | 1 | 1,400 | 18.2 | 18.5 | 12.7 | 12.0 | 11.1 | 10.2 | 54.0 / 78.7 / 89.7 / 98 | 1.5% |
| c100 | Default tweaks | 1 | 3,600 | 116.0 | 123.3 | 58.5 | 51.1 | 43.7 | 42.0 | 8.1 / 17.1 / 22.9 / 25 | 2.3% |
| c100 | Every tweak | 1 | 3,600 | 168.1 | 180.2 | 77.9 | 67.7 | 60.7 | 53.2 | 5.5 / 12.8 / 16.5 / 21 | 2.5% |
| DeCrew | Original | 1 | 1,400 | 25.4 | 27.1 | 14.1 | 13.2 | 11.7 | 10.9 | 36.9 / 70.9 / 85.2 / 92 | 1.9% |
| DeCrew | Default tweaks | 1 | 3,600 | 157.2 | 176.1 | 66.0 | 55.2 | 46.2 | 40.3 | 5.7 / 15.2 / 21.6 / 28 | 2.8% |
| DeCrew | Every tweak | 1 | 4,200 | 195.1 | 220.3 | 71.9 | 65.4 | 60.5 | 50.9 | 4.5 / 13.9 / 16.5 / 23 | 3.0% |

### U59 (744825)

| Colony | Configuration | Launch | Frames | Average fps | Median fps | 1% low fps (1,000 / p99) | Mean of the slowest 1%, fps | 0.1% low fps (1,000 / p99.9) | Mean of the slowest 0.1%, fps | p50 / p99 / p99.9 / max ms | Slowest 1%: share of frame time |
|---|---|---|---|---|---|---|---|---|---|---|---|
| U57 | Original | 1 | 2,000 | 11.5 | 13.1 | 7.7 | 1.9 | 0.5 | 0.5 | 76.5 / 129.2 / 2103.1 / 2124 | 6.0% |
| U57 | Original | 2 | 1,400 | 11.6 | 13.3 | 7.8 | 1.5 | 0.5 | 0.5 | 75.1 / 127.6 / 1979.6 / 2003 | 7.7% |
| U57 | Default tweaks | 1 | 3,600 | 111.6 | 123.9 | 46.6 | 41.8 | 35.3 | 34.5 | 8.1 / 21.5 / 28.3 / 30 | 2.7% |
| U57 | Default tweaks | 2 | 3,600 | 91.3 | 100.4 | 39.0 | 36.5 | 34.5 | 33.1 | 10.0 / 25.6 / 29.0 / 33 | 2.5% |
| U57 | Every tweak | 1 | 3,600 | 165.4 | 193.8 | 57.8 | 53.0 | 48.7 | 43.7 | 5.2 / 17.3 / 20.5 / 25 | 3.1% |
| U57 | Every tweak | 2 | 4,200 | 115.8 | 134.4 | 44.9 | 40.8 | 37.3 | 33.2 | 7.4 / 22.2 / 26.8 / 35 | 2.8% |
| 2minnow | Original | 1 | 2,000 | 27.6 | 30.6 | 17.8 | 3.6 | 1.6 | 1.6 | 32.7 / 56.1 / 629.4 / 649 | 7.7% |
| 2minnow | Original | 2 | 1,400 | 26.2 | 29.5 | 17.4 | 2.9 | 1.4 | 1.4 | 33.9 / 57.5 / 702.0 / 708 | 9.1% |
| 2minnow | Default tweaks | 1 | 3,600 | 85.8 | 98.9 | 33.7 | 29.9 | 24.6 | 23.6 | 10.1 / 29.7 / 40.7 / 44 | 2.9% |
| 2minnow | Default tweaks | 2 | 3,600 | 110.1 | 114.8 | 59.1 | 54.9 | 48.2 | 46.5 | 8.7 / 16.9 / 20.7 / 23 | 2.0% |
| 2minnow | Every tweak | 1 | 3,600 | 123.2 | 129.9 | 60.6 | 55.3 | 47.1 | 43.8 | 7.7 / 16.5 / 21.2 / 25 | 2.2% |
| 2minnow | Every tweak | 2 | 3,600 | 100.5 | 104.8 | 52.8 | 47.5 | 42.3 | 40.6 | 9.5 / 18.9 / 23.6 / 26 | 2.1% |
| Gluttony | Original | 1 | 600 | 6.1 | 6.6 | 3.7 | 3.6 | 3.3 | 3.3 | 151.0 / 267.6 / 299.3 / 299 | 1.7% |
| Gluttony | Default tweaks | 1 | 3,600 | 29.2 | 30.4 | 18.0 | 14.2 | 9.2 | 7.3 | 32.9 / 55.5 / 109.2 / 189 | 2.1% |
| Gluttony | Default tweaks | 2 | 3,600 | 27.9 | 29.4 | 16.4 | 12.9 | 11.8 | 6.6 | 34.1 / 60.9 / 84.9 / 224 | 2.2% |
| Gluttony | Every tweak | 1 | 3,600 | 46.9 | 49.1 | 25.5 | 18.9 | 11.4 | 8.3 | 20.4 / 39.2 / 87.8 / 173 | 2.5% |
| Gluttony | Every tweak | 2 | 3,600 | 49.3 | 52.3 | 25.8 | 19.4 | 13.2 | 9.8 | 19.1 / 38.8 / 75.9 / 130 | 2.5% |
| c100 | Original | 1 | 2,000 | 22.0 | 22.4 | 15.2 | 11.9 | 7.0 | 6.1 | 44.6 / 65.7 / 143.4 / 186 | 1.8% |
| c100 | Default tweaks | 1 | 4,200 | 125.9 | 130.5 | 70.6 | 56.4 | 42.3 | 28.8 | 7.7 / 14.2 / 23.6 / 49 | 2.2% |
| c100 | Every tweak | 1 | 3,600 | 187.0 | 196.5 | 93.5 | 83.4 | 76.6 | 64.9 | 5.1 / 10.7 / 13.1 / 18 | 2.2% |
| DeCrew | Original | 1 | 2,000 | 31.1 | 35.2 | 17.4 | 4.0 | 12.9 | 0.5 | 28.4 / 57.6 / 77.4 / 3863 | 7.9% |
| DeCrew | Default tweaks | 1 | 3,600 | 176.7 | 197.2 | 67.2 | 59.8 | 49.4 | 47.5 | 5.1 / 14.9 / 20.3 / 22 | 3.0% |
| DeCrew | Every tweak | 1 | 3,600 | 205.5 | 242.1 | 66.8 | 58.1 | 51.5 | 45.3 | 4.1 / 15.0 / 19.4 / 28 | 3.5% |

Reading them:
- **The 1% low is 37–58% of the median** with default tweaks on U60 (U57 and DeCrew 37%,
  c100 and 2minnow 47–53%, Gluttony 58%), and 28% on the fastest launch (U57 with every tweak,
  launch 1: median 211 fps, 1% low 58).
- **Default tweaks to every tweak** (U60): U57's average goes from 94–101 to 136–172 fps and its
  1% low from 39–42 to 48–58; c100's 1% low goes from 58 to 78, Gluttony's from 17.5 to 28.
  DeCrew's hardly moves (66 to 72).
- **The mean of the slowest 1% is 2–14 fps under the 1% low** in Moono's rows. In the original's
  U59 rows it collapses (U57: 7.7 against 1.9 fps), because a 2 s collection sits in that 1%.
- **The slowest 1% of frames take 1.9–3.5% of the time** in Moono; the last column.

### The histogram's shape

Share of frames by frame time, %, Moono only (bins in ms):

| Build | Colony | Configuration | Launch | 0–4 | 4–6 | 6–8 | 8–10 | 10–12 | 12–14 | 14–16 | 16–18 | 18–20 | 20–25 | 25–30 | 30–40 | 40–60 | 60+ |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| U60 | U57 | Default tweaks | 1 |  | 4.1 | 26.1 | 25.6 | 17.8 | 10.5 | 6.4 | 3.1 | 1.6 | 3.6 | 0.9 | 0.4 | 0.1 |  |
| U60 | U57 | Default tweaks | 2 |  | 6.8 | 30.1 | 25.3 | 15.5 | 10.5 | 4.8 | 2.1 | 1.5 | 2.8 | 0.6 | 0.1 |  |  |
| U60 | U57 | Every tweak | 1 | 28.7 | 39.9 | 13.7 | 9.3 | 3.9 | 1.9 | 0.9 | 1.1 | 0.5 | 0.2 | 0.1 |  |  |  |
| U60 | U57 | Every tweak | 2 | 8.4 | 39.2 | 21.5 | 14.4 | 6.0 | 3.3 | 2.6 | 2.1 | 1.2 | 1.0 | 0.3 | 0.0 |  |  |
| U60 | 2minnow | Default tweaks | 1 |  | 4.8 | 35.2 | 35.8 | 15.8 | 5.6 | 1.8 | 0.6 | 0.4 | 0.1 | 0.1 |  |  |  |
| U60 | 2minnow | Default tweaks | 2 |  | 4.6 | 32.1 | 31.6 | 18.2 | 7.8 | 3.1 | 1.4 | 0.5 | 0.6 | 0.1 |  |  |  |
| U60 | 2minnow | Every tweak | 1 |  | 0.5 | 7.1 | 23.6 | 28.5 | 21.9 | 10.2 | 4.4 | 1.8 | 1.7 | 0.4 | 0.0 |  |  |
| U60 | 2minnow | Every tweak | 2 | 0.1 | 5.4 | 23.8 | 29.3 | 22.1 | 11.4 | 5.2 | 1.8 | 0.5 | 0.4 | 0.1 |  |  |  |
| U60 | Gluttony | Default tweaks | 1 |  |  |  |  |  |  |  |  |  | 2.7 | 21.7 | 55.5 | 19.4 | 0.6 |
| U60 | Gluttony | Every tweak | 1 |  |  | 0.1 | 0.2 | 4.2 | 11.5 | 15.5 | 19.4 | 16.2 | 19.7 | 8.5 | 4.5 | 0.2 |  |
| U60 | c100 | Default tweaks | 1 |  | 15.0 | 33.0 | 26.4 | 14.7 | 6.9 | 2.4 | 0.9 | 0.3 | 0.3 | 0.0 |  |  |  |
| U60 | c100 | Every tweak | 1 | 15.8 | 43.0 | 26.7 | 9.9 | 3.1 | 0.9 | 0.5 | 0.1 |  | 0.1 |  |  |  |  |
| U60 | DeCrew | Default tweaks | 1 | 0.2 | 58.8 | 26.1 | 8.0 | 3.7 | 1.7 | 0.8 | 0.5 | 0.1 | 0.2 | 0.1 |  |  |  |
| U60 | DeCrew | Every tweak | 1 | 28.3 | 50.8 | 14.0 | 4.0 | 1.0 | 1.0 | 0.7 | 0.2 |  | 0.0 |  |  |  |  |
| U59 | U57 | Default tweaks | 1 |  | 15.6 | 33.5 | 22.6 | 13.3 | 7.0 | 2.9 | 1.8 | 1.5 | 1.5 | 0.3 |  |  |  |
| U59 | U57 | Default tweaks | 2 |  | 7.9 | 22.3 | 20.3 | 17.2 | 12.2 | 7.0 | 4.2 | 3.5 | 4.2 | 1.2 | 0.0 |  |  |
| U59 | U57 | Every tweak | 1 | 18.1 | 45.8 | 18.7 | 9.9 | 3.0 | 1.5 | 1.0 | 1.4 | 0.4 | 0.1 | 0.1 |  |  |  |
| U59 | U57 | Every tweak | 2 | 3.8 | 28.2 | 23.5 | 15.7 | 11.4 | 6.2 | 3.8 | 3.0 | 2.2 | 1.9 | 0.2 | 0.0 |  |  |
| U59 | 2minnow | Default tweaks | 1 |  | 2.2 | 17.5 | 28.7 | 21.4 | 11.1 | 5.4 | 2.7 | 2.1 | 4.7 | 3.4 | 0.7 | 0.1 |  |
| U59 | 2minnow | Default tweaks | 2 |  | 6.2 | 32.0 | 31.5 | 18.2 | 8.0 | 2.6 | 1.1 | 0.2 | 0.1 |  |  |  |  |
| U59 | 2minnow | Every tweak | 1 | 0.4 | 19.2 | 35.9 | 24.8 | 12.7 | 4.5 | 1.5 | 0.8 | 0.2 | 0.1 | 0.0 |  |  |  |
| U59 | 2minnow | Every tweak | 2 | 0.0 | 4.4 | 22.8 | 30.8 | 21.4 | 11.1 | 6.1 | 1.8 | 0.9 | 0.7 | 0.0 |  |  |  |
| U59 | Gluttony | Default tweaks | 1 |  |  |  |  |  |  |  |  | 0.0 | 4.8 | 23.7 | 53.8 | 17.2 | 0.5 |
| U59 | Gluttony | Default tweaks | 2 |  |  |  |  |  |  |  |  |  | 1.9 | 17.7 | 57.9 | 21.4 | 1.2 |
| U59 | Gluttony | Every tweak | 1 |  |  |  |  | 1.8 | 4.9 | 10.9 | 13.9 | 15.6 | 31.4 | 13.4 | 7.1 | 0.8 | 0.1 |
| U59 | Gluttony | Every tweak | 2 |  |  | 0.1 | 0.7 | 3.7 | 7.3 | 13.6 | 15.8 | 14.6 | 25.9 | 11.4 | 6.1 | 0.7 | 0.2 |
| U59 | c100 | Default tweaks | 1 | 0.0 | 20.5 | 35.3 | 28.1 | 11.9 | 3.1 | 0.7 | 0.2 | 0.1 | 0.0 | 0.0 | 0.0 | 0.0 |  |
| U59 | c100 | Every tweak | 1 | 22.2 | 48.3 | 21.4 | 6.4 | 1.4 | 0.2 |  | 0.1 |  |  |  |  |  |  |
| U59 | DeCrew | Default tweaks | 1 | 4.9 | 70.6 | 15.4 | 4.7 | 1.9 | 1.1 | 0.9 | 0.4 | 0.1 | 0.1 |  |  |  |  |
| U59 | DeCrew | Every tweak | 1 | 44.2 | 37.6 | 10.2 | 4.0 | 2.0 | 0.7 | 0.6 | 0.4 | 0.2 | 0.1 | 0.0 |  |  |  |

- **Two levels, not a tail.** The bulk sits in a 4 ms wide band (U57 with default tweaks: 6–10
  ms), and a second, thin band of 2–5% of frames sits 12–18 ms above it (20–30 ms). Hardly
  anything is beyond: no frame over 60 ms in any of these launches but Gluttony's.
- **Every tweak moves the bulk, not the second band's distance from it:** U57's bulk goes to
  0–6 ms and the second band to 16–25 ms.
- Gluttony is one wide band (its frame is 18–33 ms throughout); c100 and 2minnow have the
  narrowest shape.

### How the slowest 1% are spaced in time

Frames at or over the launch's p99, grouped into bursts (frames within 80 ms of each other),
against the game's clock (at speed 3 a sim step every 66.7 ms of real time, a game second every
333 ms):

| Build | Colony | Configuration | Launch | Slowest 1%: frames | Bursts | Bursts on the once-a-game-second grid (±33 ms of 333; chance 20%) | On the sim-step grid (±10 ms of 67; chance 30%) | Kept span s |
|---|---|---|---|---|---|---|---|---|
| U60 | U57 | Default tweaks | 1 | 36 | 23 | 87% | 96% | 38 |
| U60 | U57 | Default tweaks | 2 | 37 | 29 | 86% | 100% | 36 |
| U60 | U57 | Every tweak | 1 | 36 | 28 | 93% | 93% | 21 |
| U60 | U57 | Every tweak | 2 | 42 | 36 | 83% | 100% | 31 |
| U60 | 2minnow | Default tweaks | 1 | 36 | 20 | 50% | 45% | 32 |
| U60 | 2minnow | Default tweaks | 2 | 36 | 17 | 53% | 65% | 33 |
| U60 | 2minnow | Every tweak | 1 | 36 | 25 | 52% | 64% | 42 |
| U60 | 2minnow | Every tweak | 2 | 36 | 22 | 9% | 41% | 35 |
| U60 | Gluttony | Default tweaks | 1 | 37 | 29 | 55% | 41% | 126 |
| U60 | Gluttony | Every tweak | 1 | 37 | 30 | 53% | 80% | 68 |
| U60 | c100 | Default tweaks | 1 | 36 | 15 | 27% | 40% | 31 |
| U60 | c100 | Every tweak | 1 | 36 | 10 | 40% | 50% | 21 |
| U60 | DeCrew | Default tweaks | 1 | 36 | 30 | 80% | 100% | 23 |
| U60 | DeCrew | Every tweak | 1 | 42 | 39 | 97% | 95% | 22 |
| U59 | U57 | Default tweaks | 1 | 36 | 31 | 94% | 90% | 32 |
| U59 | U57 | Default tweaks | 2 | 36 | 28 | 82% | 93% | 39 |
| U59 | U57 | Every tweak | 1 | 36 | 32 | 100% | 88% | 22 |
| U59 | U57 | Every tweak | 2 | 42 | 29 | 79% | 93% | 36 |
| U59 | 2minnow | Default tweaks | 1 | 37 | 24 | 25% | 29% | 42 |
| U59 | 2minnow | Default tweaks | 2 | 36 | 21 | 19% | 57% | 33 |
| U59 | 2minnow | Every tweak | 1 | 36 | 22 | 59% | 73% | 29 |
| U59 | 2minnow | Every tweak | 2 | 37 | 21 | 14% | 71% | 36 |
| U59 | Gluttony | Default tweaks | 1 | 36 | 19 | 63% | 58% | 123 |
| U59 | Gluttony | Default tweaks | 2 | 36 | 22 | 50% | 45% | 129 |
| U59 | Gluttony | Every tweak | 1 | 36 | 25 | 48% | 52% | 77 |
| U59 | Gluttony | Every tweak | 2 | 36 | 22 | 55% | 82% | 73 |
| U59 | c100 | Default tweaks | 1 | 42 | 23 | 17% | 48% | 33 |
| U59 | c100 | Every tweak | 1 | 36 | 18 | 39% | 67% | 19 |
| U59 | DeCrew | Default tweaks | 1 | 36 | 32 | 97% | 97% | 20 |
| U59 | DeCrew | Every tweak | 1 | 36 | 36 | 92% | 94% | 18 |

- **U57 and DeCrew: periodic.** 80–100% of the bursts sit on one phase of the game second (20%
  would by chance), and the gaps between them are multiples of 333 ms (U57, U60, launch 2: 665,
  338, 328, 339, 332, 1,328, 1,997, 1,333, 334 ms …). It is the same step of the five-step cycle
  every time.
- **c100, 2minnow: bursts, not periodic.** The slow frames come 2–15 in a row (a slow frame
  hands the next one more sub-ticks), 1–5 s apart, on no fixed phase (9–59% on the grid). Their
  second band is small (p99 16–18 ms).
- **Gluttony: scattered** pairs of frames; about half on the grid.
- **Nothing shows at 1,000 ms or 1,333 ms of real time** (the render 1,000 ms groups; four game
  seconds) on U57 or DeCrew. On c100 and 2minnow a weak 1,333 ms rhythm shows in three launches
  of seven and not in the others; not followed up.

### With vsync (the smoothness study's runs)

From `out/feel` (borderless at 59.95 Hz, vsync on, U60 763023, steady play at speed 3 unless
marked; 2,401 frames a span):

| Run | Colony | Average fps | Median fps | 1% low | Mean of the slowest 1% | 0.1% low | p99 / p99.9 ms |
|---|---|---|---|---|---|---|---|
| Queued-frames wait as shipped, span a | U57 | 59.5 | 60.3 | 36.0 | 31.6 | 29.8 | 27.7 / 33.6 |
| Queued-frames wait as shipped, span b | U57 | 59.5 | 60.3 | 33.6 | 29.4 | 29.5 | 29.8 / 33.9 |
| Wait held on, span a | U57 | 59.5 | 60.3 | 35.6 | 30.9 | 29.7 | 28.1 / 33.7 |
| Wait held on, span b | U57 | 59.5 | 60.3 | 37.1 | 30.5 | 29.7 | 27.0 / 33.6 |
| Queued-frames wait as shipped, span a | c100 | 57.8 | 60.3 | 29.8 | 28.1 | 29.2 | 33.6 / 34.2 |
| Queued-frames wait as shipped, span b | c100 | 58.4 | 60.3 | 30.0 | 29.6 | 29.7 | 33.4 / 33.7 |
| Wait held on, span a | c100 | 59.5 | 60.3 | 49.0 | 31.6 | 29.7 | 20.4 / 33.7 |
| Wait held on, span b | c100 | 59.6 | 60.3 | 53.2 | 33.5 | 29.7 | 18.8 / 33.6 |
| Census (screens, overlays, an autosave), 23,564 frames | U57 | 54.8 | 60.2 | 28.7 | 15.7 | 12.3 | 34.9 / 81.3 |
| Census, 24,811 frames | c100 | 57.7 | 60.2 | 30.0 | 21.9 | 20.0 | 33.3 / 49.9 |
| Census, 25,178 frames | 2minnow | 58.6 | 60.3 | 29.8 | 26.7 | 29.5 | 33.5 / 33.9 |

With vsync the 1% low is a count of doubled refreshes: 30 fps as soon as more than 1% of
frames take two refreshes, which U57's once-a-second step alone does (3 a second in 60 is 5%).
The census rows hold scripted screen openings and an autosave, so their 0.1% columns are that
study's hitches, not steady play.

## Part 2: what the slowest 1% consist of (late-game colony, U60, default tweaks)

### The run

- **One Windows launch** (runq `win-perf` job j0000821, 2026-11-14 02:27–02:35 local, 484 s):
  the released player v2026.11.13.03 (u60 77a54e7b; the commits since touch saving only) on
  U60 763023, U57, default tweaks, 1280×720 on monitor 2, `--no-focus`, High priority, vsync off,
  no autosave, no warm-up launch. After 2,700 frames at speed 3: 6,001 frames recorded without
  brackets, then 24,001 with them (295 s, 886 game seconds, game speed 3.00).
- **One Linux launch before it** (job j0000820, 1,200 frames) only to check the probe; its
  numbers are not used. No Linux profile and no every-tweak run were made: the brackets gave
  the split, and Part 1's spacing shows the same step in the every-tweak launches.
- **Probe:** the jank study's `tools/PerfProbe/Jank.cs` with its phase marks and section
  brackets, plus brackets on the step's parts (`ConduitFlow.Sim200ms` for both pipe flows,
  `SolidConduitFlow.Sim200ms`, `PlantElementAbsorbers.Sim200ms`, `EnergySim.EnergySim200ms`,
  `CircuitManager.Sim200msFirst` and `Sim200msLast`, `ConduitTemperatureManager` and
  `ConduitDiseaseManager`, `ElementConsumer.AddMass`, `Substance.SpawnResource`,
  `Sim.HandleMessage`, `SimMessages.NewGameFrame`, `JobManager.Run`) and on the sim bucket
  groups by rate (`BucketGroup.AdvanceOneSubTick`). Local, branch `onepct-u60`; not landed.
- **This launch ran slower than the re-profile's** (median frame 10.9 ms with brackets, 11.7 ms
  in the span without, against 8.9–9.5 ms there), so its 1% low is 32 fps against 39–42. The
  brackets cost 5–8% (the jank study's figure); the rest is the launch and outside load.
- **Outside load, logged every 5 s:**
  - Windows side: another project's game (a background tool's, which runq does not hold back
    for) used 1.0–1.9 cores in bursts for 112 of the 295 s. Frames in those bursts were no
    slower: p50 10.98 against 10.88 ms, p99 30.1 against 31.6 ms, the liquid-and-rails step's
    frame 26.8 ms in both.
  - WSL side: a headless browser used 2–4 cores for the last 60 s and in three short bursts
    (93 s in all). Frames then were 10% slower (average 75.8 against 84.0 fps, p99 32.7 against
    30.1 ms). The span without brackets (80 s) had two such bursts.
  - So the launch was **mildly disturbed**: levels are about 10% low for a third of it; the
    make-up of the slow frames is the same inside and outside the disturbed spans (heavy-step
    frames are 3.6% and 4.0% of the frames, and the run's slowest 1% hold 0.8% of the quiet
    frames and 1.5% of the disturbed ones). It was not repeated: the make-up is what the run
    was for, and
    it agrees with the jank study's bracketed launch of 2026-11-12 (2,401 frames at 109 fps,
    U60 762230), where 23 of the slowest 24 frames were this step and one a collection.

### The launch's frame rates

| Span | Frames | Average fps | Median fps | 1% low | Mean of the slowest 1% | 0.1% low | Mean of the slowest 0.1% | p50 / p99 / p99.9 / max ms |
|---|---|---|---|---|---|---|---|---|
| Without brackets | 6,001 | 74.7 | 85.4 | 30.3 | 26.3 | 23.7 | 19.1 | 11.7 / 33.0 / 42.2 / 77 |
| With brackets | 24,001 | 81.3 | 92.2 | 32.4 | 28.2 | 24.1 | 20.1 | 10.9 / 30.8 / 41.6 / 74 |
| Jank study, with brackets (2026-11-12, for comparison) | 2,401 | 109.1 | 124.2 | 39.8 | 36.0 | 32.7 | 24.7 | 8.1 / 25.1 / 30.6 / 46 |

### Frames by what they carry

The five-step cycle of [sim-step-cost.md](sim-step-cost.md), per frame (24,001 frames; "heavy
step" is the step with the liquid pipes and the rails, "gas step" the one with the gas pipes):

| Frame | Frames | Share | p10 | p50 | p90 | p99 | max ms |
|---|---|---|---|---|---|---|---|
| No sim step | 19,570 | 81.5% | 6.7 | 9.9 | 15.8 | 24.1 | 74 |
| Plain step (3 of 5) | 2,659 | 11.1% | 11.7 | 16.1 | 23.0 | 31.5 | 53 |
| Gas step | 886 | 3.7% | 13.6 | 17.7 | 23.6 | 31.8 | 63 |
| **Heavy step** | 886 | 3.7% | 22.3 | **26.7** | 33.5 | 43.7 | 68 |
| The frame after a heavy step (4.9 sub-ticks) | 886 | 3.7% | 11.2 | 15.6 | 25.4 | 35.3 | 74 |

Every heavy-step frame is over 16.7 ms; 17% of all frames are.

### The slowest 1% and 0.1%

| | Slowest 1% (240 frames, over 30.8 ms) | Slowest 0.1% (24 frames, over 41.6 ms) |
|---|---|---|
| Heavy-step frame | 165 (69%) | 13 |
| Another step's frame | 42 (17.5%) | 6 |
| The frame after a heavy step | 22 (9%) | 2 |
| A collection in the frame | 3 (1%) | 3 |
| No step in it or before it | 8 (3%) | 0 |
| With a render 1,000 ms group due | 7 (3%) | 1 |
| With a render 200 ms group due | 25 (10%; 6% of all frames have one) | 3 |
| Sub-ticks, mean (all frames: 2.2) | 3.75 | 4.96 |
| Mean frame | 35.5 ms | 49.8 ms |

19% of the heavy-step frames are in the slowest 1%; the p99 frame (30.8 ms) is the 81st
percentile of the heavy-step frames.

The 24 slowest, one by one:
- **12 heavy-step frames** of 41.8–48.5 ms: the step 14.8–18.0 ms (pipes 5.1–6.6, rails
  4.1–7.3), 2–6 sub-ticks (6.2–11.3 ms), LateUpdate 7–12 ms.
- **6 frames of one event** (frames 27,119–27,127): a collection with a gen1 and a gen2 in
  it paused 33.9 ms (a 55.7 ms frame); five of the next eight frames then took 49–74 ms, four of
  them with 10–12 sub-ticks (12 is the game's limit), one waiting 15.6 ms for the native sim,
  one with 16.8 ms more of pause.
- **2 more collections:** a 34.8 ms pause in a frame without a step (49.8 ms), and a 37.4 ms
  pause inside the rails' update of a heavy step (68.4 ms).
- **2 frames with one long 1,000 ms sim bucket** (13.2 and 18.9 ms in `SIM_1000ms`; which
  updater was not recorded).
- **2 plain-step frames with a long LateUpdate** (15.2 and 18.9 ms).

### Against the median frames

Mean ms by part over each group's frames (wall time; a dotted row is inside the row above it,
the other rows add up to the frame).

| Part | No step | Any step | Heavy step | Slowest 1% | Slowest 0.1% |
|---|---|---|---|---|---|
| **The step (`StepTheSim`)** | 0 | 4.9 | **11.2** | 10.1 | 13.0 |
| · pipes (`ConduitFlow`, both) | 0 | 1.11 | 4.36 | 3.47 | 3.46 |
| · · of which job runs (`JobManager.Run`: 6 a heavy step) | 0 | 0.29 | 1.13 | 0.82 | 0.74 |
| · rails (`SolidConduitFlow`, with the items it spawns) | 0 | 0.78 | 3.87 | 3.42 | 4.94 |
| · power (`EnergySim`) | 0 | 0.79 | 0.83 | 0.90 | 1.14 |
| · plant absorbers | 0 | 0.72 | 0.57 | 0.68 | 0.87 |
| · circuits (`Sim200msLast`) | 0 | 0.39 | 0.42 | 0.44 | 0.54 |
| · mass consumed, conduit temperature and disease | 0 | 0.45 | 0.45 | 0.44 | 0.49 |
| · the native sim (`Sim.HandleMessage`) | 0 | 0.02 | 0.01 | 0.08 | 0.71 |
| · the rest of the step (the sim's data read back, callbacks) | 0 | 0.62 | 0.63 | 0.68 | 0.86 |
| Nav grids | 0 | 0.58 | 0.53 | 0.55 | 0.58 |
| Sim sub-ticks: 1,000 ms buckets | 0.53 | 0.65 | **1.58** | 2.55 | 3.49 |
| Sim sub-ticks: 200 ms buckets | 1.22 | 1.80 | 1.70 | 3.40 | 4.87 |
| Sim sub-ticks: every tick, 33 ms, 4,000 ms | 0.42 | 0.53 | 0.53 | 1.18 | 1.61 |
| Render 200 ms and 1,000 ms groups | 0.04 | 0.05 | 0.04 | 0.25 | 1.56 |
| Brains (`BrainScheduler`) | 1.62 | 1.97 | 2.06 | 2.81 | 4.21 |
| Rest of Update, coroutines | 0.21 | 0.26 | 0.52 | 1.38 | 2.04 |
| LateUpdate (`Game`, animation, `World`, rest) | 4.47 | 5.67 | 6.18 | 8.91 | 13.15 |
| Canvases and the rest after LateUpdate | 0.68 | 0.93 | 1.01 | 1.48 | 1.71 |
| Cameras (render) | 1.00 | 1.25 | 1.35 | 1.69 | 2.16 |
| GUI and end of frame | 0.44 | 0.58 | 0.62 | 0.81 | 1.03 |
| Present, message pump, next frame's start | 0.07 | 0.14 | 0.20 | 0.27 | 0.31 |
| GC pause | 0.00 | 0.01 | 0.04 | 0.51 | 5.12 |
| Sub-ticks in the frame | 2.21 | 2.25 | 1.99 | 3.75 | 4.96 |
| **Frame** | 10.7 | 19.3 | 27.5 | 35.5 | 49.8 |

- **The step is the same size in the slowest 1% as in any heavy-step frame** (10.1 against
  11.2 ms; lower because a quarter of them are other steps). What differs is everything around
  it: sub-ticks 7.1 against 3.8 ms, brains 2.8 against 2.1, LateUpdate 8.9 against 6.2.
- **Within the heavy-step frames**, the slowest third against the fastest third (32.7 against
  23.1 ms): LateUpdate +2.8 ms, sub-tick buckets +2.1 (2.5 sub-ticks against 1.5: the frame
  before was longer), rails +1.1, brains +1.0, pipes +0.6, cameras +0.4. A render 1,000 ms
  group is due in 2% of the slow third.
- **Render, present and the native sim are not in it.** Cameras add 0.35 ms to a heavy-step
  frame, the present 0.13 ms; the wait after present is 0.01 ms; the native sim's call is
  0.01 ms (it waited once in 295 s, after a collection).
- **The step's cost a game second** (all five steps): 24.4 ms, of which pipes 5.6, power 4.0,
  rails 3.9, absorbers 3.6, nav grids 2.9 beside it, circuits 2.0. That matches
  [conduit-updates.md](conduit-updates.md)'s 23.0 ms (without these brackets).

### What lands together, and why

- **The liquid pipes and the rails share a step by construction.** `Game` creates the gas flow
  with 0.25 s on its one-second timer and the liquid flow and the rail flow with 0.75 s each
  (`new ConduitFlow(…, 0.25f)`, `(…, 0.75f)`, `new SolidConduitFlow(…, 0.75f)`); the timers
  are not saved, so every load starts them there. Liquid and rails then fire on the same step
  for the whole session, gas two steps away.
- **1,000 ms sim buckets follow it at a fixed phase.** The `SIM_1000ms` group has 60 buckets,
  one a sub-tick, and the heavy step comes every 60 sub-ticks, so the same buckets always run
  on the step's own sub-tick and right after it. Measured on frames of one sub-tick: 1.28 ms of
  `SIM_1000ms` time on the heavy step's sub-tick against 0.05–0.5 ms on the others; and the frame
  after a heavy step spends 3.0 ms there in its 4.9 sub-ticks (1.2 ms would be average). A
  heavy-step frame has 1.58 ms of it on average, three times a frame's usual 0.53. Which
  updaters these are was not recorded
  (`ONI_BUCKET_PROFILE=1` with `--frame-stats` would name them).
- **The frame after gets the sub-ticks.** A 27 ms frame is 4.9 sub-ticks of game time at speed
  3, so the next frame runs them: 15.6 ms at the median, the second-slowest kind of frame.
- **Not coinciding:** render 1,000 ms groups (3% of the slowest 1%, as often as chance),
  collections (3 in 295 s), the present.

## Part 3: what would raise the 1% low

### What a millisecond is worth

A replay of the profiled run's 24,001 frames with a part made cheaper (and the next frame given
0.20 ms back for every ms saved, for its fewer sub-ticks: 1.13 ms a sub-tick, measured). All
figures here are **estimates** from that replay, on a launch whose 1% low was 32.4 fps; on a
launch at the re-profile's level (39–42 fps) the same ms are worth about 1.5 times as many fps.

| Change (replay) | p99 ms | 1% low fps | 0.1% low fps | Average fps |
|---|---|---|---|---|
| As measured | 30.83 | 32.4 | 24.1 | 81.3 |
| Every frame 1 ms shorter | 29.63 | +1.3 | +0.7 | 90.1 |
| Every frame 2 ms shorter | 28.43 | +2.7 | +1.5 | 101.0 |
| Every frame 3.5 ms shorter | 26.62 | +5.1 | +2.7 | 123.5 |
| Heavy step 1 ms shorter | 30.21 | +0.7 | +0.6 | 81.6 |
| Heavy step 2 ms shorter | 29.70 | +1.2 | +1.1 | 81.8 |
| Heavy step 4 ms shorter | 28.82 | +2.3 | +2.0 | 82.4 |

For scale, measured: default tweaks to every tweak takes U57's median frame from 9.2 to 5.5 ms
and its p99 from 24.6 to 19.0 ms on U60 (1% low 40.7 to 52.6 fps at the launches' medians).

### Exact options, ranked by estimated gain

| # | Option | Exact? | Estimated 1% low | 0.1% low | Evidence | Status |
|---|---|---|---|---|---|---|
| 1 | The step's remaining exact candidates of [sim-step-cost.md](sim-step-cost.md): a faster exact `EventSystem.Trigger`, power consumers' `Powered` flag without the dictionary, `IrrigationMonitor`'s attribute instance: 6–10% of every step | Yes, each with a verify mode | +0.6 to +0.9 fps | +0.5 to +0.9 | That study's perf shares; this run's step times | Parked there; two are designs |
| 2 | Fewer job hand-offs in the pipes' update (`fast-jobs` for the conduit frame jobs): on Windows the six `JobManager.Run` calls of a heavy step take 1.13 ms of the main thread's wall time (0.19 ms a run, the solve's own time included) | Yes (the items are independent per conduit) | at most +0.8 (all of it gone); +0.4 at half | +0.3 to +0.7 | **Measured** here: the Windows figure [conduit-updates.md](conduit-updates.md) candidate 5 waited for | Open; needs a prototype to see how much is waiting |
| 3 | Rails 10–25% cheaper again (hand-offs: tag events, `Pickupable.Take`) | Only where a cost has no effect, as the partitioner walk had | +0.3 to +0.8 | +0.3 to +0.7 | Replay; no candidate of that size is known after `fast-partitioner-events` (−22%) | No known candidate |
| 4 | 1,000 ms sim buckets cheaper (the ones on the heavy step's sub-ticks) | Per updater | +0.6 at 25% | +0.3 | Replay; the updaters are not identified yet | Next: name them with the bucket profile |
| 5 | A collection's pause | No exact option known: the gen0 budget cap was measured worse ([gc-pauses.md](gc-pauses.md)); fewer allocations make pauses rarer, not shorter | 0 | +0.9 if none | 3 collections in 295 s, 8 of the 24 slowest frames | The allocation batches (in flight) |
| 6 | Render 1,000 ms groups kept off sim-step frames | No (moves updaters between frames), and worthless | 0.0 | 0.0 | 7 of the 240 slowest frames have one due | Dropped |
| | **1, 2 at half and rails 10% together** | | **+1.5** | **+1.5** | Replay | |

Every frame's cost, which is worth more (1 ms: +1.3 fps):
- **Already shipped, exact, on by default:** `fast-save-roots`, `fast-partitioner-events`,
  `fast-conduit-sorts` (the step: −5% a game second, rails −22%; measured in
  [conduit-updates.md](conduit-updates.md)).
- **Shipped but not exact, off by default:** `brain-rate` is most of the gap between default
  and every tweak on U57 (the brains are 1.6 ms of a frame without a step and 2.8 ms of the
  slowest 1%); `off-world-fallers` is most of it on Gluttony. They are the measured way to a
  1% low of 48–58 fps on U57 today.
- **In flight elsewhere, not touched here:** the queued-frames wait, the autosave, overlays and
  table screens. None of them is in the steady-play slowest 1% (no present wait, no canvas
  time: 0.7–1.5 ms). The allocation batches make collections rarer, which reaches the 0.1% low
  only.

### Moving coinciding work off the sim-step frame

**Not possible without changing results**, for each thing that coincides:
- **Rails off the liquid pipes' step** (another start value for the rails' timer): estimated
  +1.6 fps of 1% low and +2.2 of 0.1% low. Not exact: rails would hand items over 0.2–0.4 game
  seconds earlier or later against every other system. The timers are not saved, so the phase
  is already an accident of when a colony was loaded; that makes the change small, not exact.
- **The 1,000 ms sim buckets off the heavy step's sub-ticks** (another bucket for those
  updaters, or the group started at another offset): estimated +1.2 and +1.9 fps. Not exact:
  it changes those updaters' order against the step and against each other.
- **The step's systems on other sub-ticks or threads:** ruled out in
  [frame-pacing-and-jank.md](frame-pacing-and-jank.md) (option a) and
  [sim-step-cost.md](sim-step-cost.md).
- **Render 1,000 ms groups:** see option 6; nothing to gain.
- **The sub-ticks after a long frame** (the frame after a heavy step, and the 10–12 sub-tick
  frames after a collection): that is the game's pacing; a smoothed delta was modelled at no
  gain and is not exact (the jank study's option b).

Not proposed, by standing decision: delaying temperature or building checks, a collection
after the save, Satori.

### What this means

- The 1% low on a rail- and pipe-heavy colony is the cost of one game system's batch, on top of
  the frame under it. Exact tweaks reach about 10–15% of that batch.
- The number that moves it is the frame under it. Each ms off the ordinary frame is worth about
  twice a ms off the step, because it comes off every heavy-step frame and the frame before it.
- A 1% low of 60 fps on U57 (a heavy-step frame under 16.7 ms at its 80th percentile) needs
  about 14 ms less than this launch had and about 8 ms less than the re-profile's launches;
  nothing exact on this page comes near. With every tweak the re-profile's best launch is at
  17.2 ms.

## Part 4: tracking

- **`scripts/perf-spread.py`** (the nightly's perf job and the by-hand A/B with `args` and
  `b_args`, which is how a tweak is compared): a launch now writes PerfProbe's
  `frametimes.txt`, keeps each window's frame times in `result.json`, and the summary's times
  table has three more rows with A, B and B/A: **the median frame, the p99 frame and the 1%
  low** (1,000 / the p99 frame), over every frame of a launch's measured running windows. Each
  launch's row lists its p99 frame and 1% low. No gate reads them.
- **The nightly's table** (`game-nightly.yml` calls `perf-spread.yml`) carries the rows with no
  change to the workflows. With its settings (three running windows of 300 frames, the first
  left out) the p99 frame is the sixth-slowest of 600 a launch, and on CI's software renderer
  (about 6 fps on early-base) most of a frame is the renderer's. Read B/A only, and not as a
  1% low a player would see.
- **On master** the probe did not write frame times; the two probe commits that add them came
  over from u60 with the script's change.
- **Not done, proposed:** the same three columns in the Windows A/B scripts used for tweak
  comparisons (they are local tools, not in the repository), and the mean of the slowest 1%
  beside the p99 in PerfProbe's own window line, which today prints a `worst1%` that mixes
  windows when the window is not 600 frames.

## Runs

| Run | Job | When (local) | What |
|---|---|---|---|
| Probe check, Linux | j0000820 | 2026-11-14 00:16–00:18 | 1,200 frames; the brackets patched (35 methods); not measured |
| Bracketed launch, Windows | j0000821 | 2026-11-14 02:27–02:35 | 6,001 frames without brackets, 24,001 with; mildly disturbed (above) |

Windows timing launches: 1 of the 2 allowed. Part 1 used no runs. Local, not in the
repository: the probe (branch `onepct-u60`, worktree `out/onepct-u60`), the run's CSV and load
logs, and the scripts (`s1.py`, `attr.py`, `whatif.py`, `bins.py`).
