# Brains and path probes (2026-10-25)

Is the Duplicant and critter brains' main-thread cost only a matter of how many there are, with
path probes on worker threads, so that a smaller reachable area does not change the frame time?
Mostly, but not only:

- The path probes never hold up the main thread.
- Part of the brains' own work shrinks when less is reachable.
- With cores to spare that change is within the runs' noise on the frame time.
- On 2 cores the probe thread takes CPU the rest of the frame needs, and a smaller area
  shortened U57's frame by a fifth.

## How the game runs them

From the game's code (build 744825):
- **`BrainScheduler.RenderEveryTick`** runs its two groups each rendered frame. The Duplicant
  group updates 1 brain a frame (alternating between its priority queue and its round robin),
  the critter group 5 (`InitialProbeCount`). The groups' probe sizes (`InitialProbeSize`,
  `IdealProbeSize`, `MinProbeSize`, `ProbeSizeStep`) are declared but nothing reads them. A
  group's loop skips brains that aren't running, so the count is per frame whatever the colony's
  size: more brains means each gets its turn less often, not more work a frame.
- **A brain update** (`Brain.UpdateBrain`) runs `onPreUpdate`, then a chore search
  (`ChoreConsumer.FindNextChore`, every chore of its providers checked against their
  preconditions). For a Duplicant, `onPreUpdate` is its sensors (`Sensors.UpdateSensors`): the path
  prober sensor, safe, idle and mingle cells, the closest edible, assignable reachability and
  `PickupableSensor`, which runs `GlobalChoreProvider.UpdateFetches` and
  `FetchManager.UpdatePickups`. That last one runs two jobs on the game's job threads (offset
  tables, then each fetchable's path cost from this Duplicant), and the main thread works on them
  and waits until they're done. For a critter, `onPreUpdate` is `Navigator.UpdateProbe`.
- **Duplicants' path probes** (and rovers' and fetch drones': `executePathProbeTaskAsync`) never
  run in the brain update: `PathProberSensor` calls `Navigator.UpdateProbe`, which returns at once
  for them. `AsyncPathProber` has one thread of its own (`CreateInstance(1)`). Once a frame, at
  the end of `Game.Update`, `TickFrame` hands out the results finished since the last frame
  (`Navigator.TakeResult`: the new path grid, and the reachable cells gained and lost for
  `MinionGroupProber`), then queues up to 4 new probes (`kMaxProbersPerFrame`), those waiting
  longest first. Orders the thread hasn't started by the next `TickFrame` are dropped and queued
  again. Nothing waits for a probe: a slow thread means older path grids, not a longer frame.
- **Critters' probes** run on the main thread, inside their brain update, in a window of 65×65
  cells around them (`max_probing_radius` 32 by default).

## Method

- Linux under WSL2, the release player at 1280×720, `--game-tweak exact` (so `brain-rate` off),
  speed 3, U57 and Gluttony (`presync.sav`), each run holding the perf lock once the machine was
  quiet (under 2 cores busy).
- A temporary PerfProbe mode (not kept): stopwatches as Harmony prefixes and postfixes on the
  methods above, split by Duplicant and critter, and on `AsyncPathProber.WorkOrder.Execute` on the
  probe thread, with each probe's reachable cells. Medians of 600-frame windows. Patched before the
  colony's first brain update: patched later, the JIT has already devirtualized and inlined
  `Brain.UpdateBrain` into the critter group's loop, and the patch misses those calls.
- Fewer cores: the whole player under `taskset -c 0-3` or `0-1`; the game sizes its job threads
  from the cores it may use (16, 4 or 2).
- Smaller reachable area: every door locked through the game's own `Door.QueueStateChange`
  (with the debug instant-build flag, so no Duplicant has to walk to each door) after window 5 of
  12; windows 1–5 against 7–11. Runs without the lock, measured the same way, didn't drift between
  the two halves by more than 0.12 ms of brain time.

## Where the time goes

Milliseconds a frame on the main thread unless noted.

| | U57 | Gluttony |
|---|---|---|
| Duplicants, critters with brains | 22, about 1,070 | 67, about 895 |
| Frame | 29.1 | 72.4 |
| `BrainScheduler.RenderEveryTick` | 2.85 | 5.92 |
| · Duplicant brains (updates a frame) | 2.32 (0.89) | 5.04 (1.00) |
| · · sensors | 1.74 | 4.37 |
| · · · `UpdatePickups` | 1.14 | 4.19 |
| · · job runs inside brain updates (running and waiting on the job threads) | 1.45 | 4.39 |
| · · chore searches (a frame) | 0.52 (0.85) | 0.60 (0.94) |
| · · the async path probe | 0.000 | 0.000 |
| · critter brains (updates a frame) | 0.56 (5.1) | 0.88 (5.0) |
| · · their own path probes, on the main thread | 0.02 | 0.02 |
| · · chore searches | 0.15 | 0.29 |
| `AsyncPathProber.TickFrame` (outside the scheduler, in `Game.Update`) | 0.027 | 0.040 |
| Probe thread: probes a frame | 4.0 | 4.0 |
| Probe thread: ms a probe, ms a frame | 3.30, 13.2 | 1.24, 5.0 |
| Reachable cells a Duplicant | 9,760 | 2,850 |

So the main thread waits for no path probe; the hand-off costs 0.03–0.04 ms. What it waits for
is the fetch job: about half of the scheduler's time on U57 and three quarters on Gluttony, where
`UpdatePickups` (one Duplicant's update) costs about 4 times as much as on U57. The probe thread
is busy about 45% of the frame on U57 and 7% on Gluttony, and finishes its 4 probes every frame.

## A smaller reachable area

| | U57 before → after | Gluttony before → after |
|---|---|---|
| Doors locked | 712 | 367 |
| Reachable cells a Duplicant | 9,760 → 258 | 2,856 → 212 |
| Probe thread, ms a probe | 3.30 → 0.078 | 1.27 → 0.107 |
| Probe thread, ms a frame | 13.2 → 0.31 | 5.06 → 0.43 |
| `TickFrame` | 0.027 → 0.031 | 0.041 → 0.044 |
| `BrainScheduler.RenderEveryTick` | 2.85 → 2.49 | 6.18 → 4.85 |
| · Duplicant brains | 2.32 → 1.96 | 5.24 → 3.92 |
| · · `UpdatePickups` | 1.14 → 0.84 | 4.33 → 3.28 |
| · · chore searches | 0.52 → 0.45 | 0.61 → 0.39 |
| · critter brains | 0.56 → 0.52 | 0.97 → 0.91 |
| Frame | 29.1 → 27.5 | 75.0 → 74.3 (the run without the lock: 72.4 → 74.7) |

The probes' cost follows the reachable area (40 and 12 times less work for 38 and 13 times fewer
cells) but none of it was on the main thread. What did come off the main thread is the
Duplicants' own work: the fetch job finds fewer reachable items to cost and sort, and chore
searches fail sooner. That's 0.36 ms on U57 (13% of the scheduler) and 1.3 ms on Gluttony (22%),
1–2% of the frame, inside what runs vary by. Critters don't change: their probe window is
already capped.

## Fewer cores

| | U57, 16 → 4 → 2 cores | Gluttony, 16 → 4 cores |
|---|---|---|
| Frame | 29.1 → 32.4 → 51.3 | 72.4 → 73.4 |
| `BrainScheduler.RenderEveryTick` | 2.85 → 3.55 → 6.00 | 5.92 → 5.99 |
| · job runs inside brain updates | 1.45 → 2.03 → 4.43 | 4.39 → 4.40 |
| `TickFrame` | 0.027 → 0.028 → 0.061 | 0.040 → 0.040 |
| Probe thread: probes a frame, ms a frame | 4.0, 13.2 → 4.0, 13.6 → 4.0, 18.6 | 4.0, 5.0 → 4.0, 5.2 |

The path probes use one core at most, whatever the machine has, and even on 2 cores the thread
finished its 4 probes a frame; the main thread still never waits for it. What fewer cores slow is
the fetch job, which the main thread does wait for: on U57 it goes from 1.45 to 4.4 ms on 2 cores
(on Gluttony it's no faster with 16 job threads than with 4). On 2 cores the probe thread also
takes about a third of a core the main thread might have had.

That third of a core shows once the area shrinks. U57 on 2 cores, 708 doors locked after
window 5 (the run without the lock: frame 52.4 → 50.2, scheduler 6.00 → 6.00):

| | U57 on 2 cores, before → after |
|---|---|
| Reachable cells a Duplicant | 9,760 → 264 |
| Probe thread, ms a frame | 18.8 → 0.33 |
| `BrainScheduler.RenderEveryTick` | 6.10 → 3.41 |
| · job runs inside brain updates | 4.50 → 1.92 |
| · chore searches | 0.50 → 0.29 |
| Frame | 51.5 → 40.5 (19.4 → 24.7 fps) |

The frame lost 11 ms, the brains 2.7 of them: with the probe thread nearly idle, the job threads
and the rest of the frame got its core back.

## Answer

- The path probes don't cost the main thread anything that depends on the reachable area: the
  Duplicants' run on a thread of their own that nothing waits for, and the critters' run on the
  main thread in a fixed 65×65 window (0.02 ms a frame).
- The brains' main-thread cost is not the number of Duplicants and critters either: the game
  updates 1 Duplicant and 5 critters a frame whatever their number. It's the cost of each update,
  which grows with what each Duplicant has to consider (fetchable items, errands) and, in part,
  with how much of that is reachable.
- Shrinking the reachable area 13–38 times cut the brains' main-thread time by 13–22% (0.4–1.3 ms),
  and the frame time by no more than the runs' noise, with cores to spare (4 and 16).
- With few cores it does change the frame time: on 2 cores the probe thread's work (about 37% of
  the frame on U57) competes with the main thread's job threads, and the smaller area made U57's
  frame 21% shorter.
