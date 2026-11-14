# The pile-heavy colony at default settings (2026-11-14)

Gluttony (the uncleaned save of [case study 3](../PERFORMANCE_CASE_STUDY_3.md)) runs at about
29 fps with default settings and about 53 with every tweak, where the other colonies run at
100–180. The questions:

- Where does a default frame go?
- What does each opt-in tweak buy alone?
- Could the big ones be on by default?
- What is left after every tweak?
- Is the colony like anything players hit?

A study: nothing was built beyond probes.

**Verdict.**
- **Two opt-in tweaks make the gap, and they add up.** In one launch, alternating blocks
  (Windows, speed 3):
  - Default: 28.6 fps (35.0 ms a frame).
  - `off-world-fallers` alone: 37.7 fps (−8.4 ms).
  - `fetch-by-cell` alone: 34.4 fps (−5.5 ms).
  - Both: 49.2 fps (−14.6 ms).
  - Every tweak: 53.8 fps (18.6 ms).

  The re-profile's guess that `off-world-fallers` is most of the gap is half right. It is 8.4
  of the 16.4 ms, `fetch-by-cell` 5.5–5.9, `brain-rate` 1.4 (only once the colony is above
  30 fps), `off-screen-conveyors` nothing measurable.
- **Each saving counts about twice at speed 3.** Sim work is paid per game second, so a shorter
  frame also carries less of it. The gravity step is 4.4 ms of a default frame, and landing the
  objects takes 8.4 ms off it.
- **`off-world-fallers` is a fix of a game bug with no visible effect and no effect on the
  save.** Its one cost as it is written: it transpiles `GravityComponents.FixedUpdate`, and a
  Harmony-patched copy of that loop runs about 50% slower for every falling object. Landing
  the objects from a pass of its own would leave the game's loop untouched.
- **`fetch-by-cell` differs from the game only by ties, and often.** In verify mode on this
  colony, of 715,889 results for a kind, 40,649 (5.7%) named another of several tied items and
  none differed otherwise. 96% of the fetch updates held at least one such tie. An exact form
  would run the game's own pass for a kind whose best items tie. It would keep the whole gain
  for the 86% of results with no tie and lose it where the 20,545 missiles tie: half the gain
  or more (estimated).
- **`brain-rate` has no exact form.** The game ties brain updates to rendered frames; the tweak
  holds them to what the game does at 30 fps. On this colony Moono's default already updates
  brains five times as often as the original player does.
- **After every tweak nothing large is left.** 36% of the main thread is sim work at 120 ms a
  game second over several hundred kinds of update, the largest 0.7 ms a frame. The rest is
  12 ms a frame of rendering, animation, brains and UI. It is the frame the 2026-10-22
  inventory describes.
- **What is extreme about the colony** is 40,500 loose items in eight cells: 17,539 objects
  hanging below two rocket interiors (a chute dropping through the floor, the game's bug) and
  20,578 missiles on the ground in six cells (ordinary play: a fabricator left on forever).

## The colony in numbers

Windows, 25 s after the load, default settings (PerfProbe's `piles.txt`, a local probe).
Component counts are from the Linux launch, active or not.

| | |
|---|---|
| Duplicants, critters | 67, 900 |
| Worlds | 26, of them 15 rocket interiors |
| Pickupables | 64,312: 58,596 loose, 4,749 stored |
| Loose items by cell | 4,526 cells: 2,505 hold one item, 1,449 hold 2–4, 408 hold 5–9, 111 hold 10–49, 24 hold 50–99, 21 hold 100–999 (5,758 items), 8 hold 1,000 or more (40,516 items) |
| The eight largest cells | 12,780 and 4,759 hanging below rocket interiors (10,322 diamonds, 7,180 egg shells, 34 eggs); 5,128, 5,105, 4,406, 3,753 and 1,328 missiles side by side on one floor; 3,257 regolith chunks |
| Loose items by kind | missiles 20,578, diamonds 10,379, egg shells 7,335, regolith 4,212, sulfur 1,451, igneous rock 1,123 |
| Falling objects (gravity components) | 17,566, of them 17,539 hanging below a world's bottom row; 28 once those are landed |
| Fetchables | 47,900 in 204 kinds (missiles 20,545); 3,982 piles (one kind in one cell) |
| Fetchables in cells a Duplicant has no path to | 32,948 on average (69%); at least 19,532 (41%) for any Duplicant |
| Loose items in chunks in view | 19,591 (the view is the rocket interior with the 12,780 hanging below it: in a chunk in view, below the screen's edge) |
| Errands | 440 with the global provider, 122 fetches |
| Rails | 4,974 cells, 863 carrying an item, 200 items moving, 61 of them in view |
| Animation controllers, colliders, workables | 139,122, 141,323, 609,592 |
| Buildings, storages, navigators | 74,032, 6,144, 1,091 |

The hanging objects never merge. A falling item joins a pile of its kind when it enters a cell
with solid ground below (`Pickupable.OnCellChange`); these have no cell below, so every chunk
the chute dropped is still an object of its own. The missiles are discrete items, which never
merge.

## Method

- **Players:** u60 a069cce1 with local probe code, release builds, game U60 763023, every DLC.
  Windows: 1280×720 on the second monitor, unfocused (`--no-focus`), High priority, vsync off,
  a warm-up launch to the menu first, PerfProbe holding speed 3 with no autosaves and no victory
  sequence, as the re-profile ran it. Every run was a runq job (`win-perf`, `linux-perf`).
- **In-launch A/B.** Two launches of one configuration differed by up to 43% on 2026-11-13, so
  the tweaks are compared in one launch. Local switches (`ProbeOn` in `BrainRate`,
  `FetchByCell`, `OffscreenConveyors`, uncommitted) turn a tweak's patches into pass-throughs,
  and PerfProbe's `matrix.txt` (local) runs configurations in blocks: 4 s left out after each
  switch, then 10 s measured.
- **`off-world-fallers` cannot be switched that way,** and the first attempt showed why. A
  Harmony-patched method runs as a rebuilt copy of the game's, and a hot loop in one is slower
  (PerfProbe's `WallTime` notes the same). With the tweak installed and its landing switched
  off, the gravity step cost 1.9–2.5 ms a call against 1.30 for the game's own code, and the
  colony ran at 18–24 fps. So the tweak was left out of the launch and installed at run time,
  once. The default blocks run the game's unpatched step, and every block after the install
  has the tweak on. That half is one-way. The four default blocks before it (33.6–36.0 ms)
  and the four every-tweak blocks after it (18.2–18.8 ms) show how steady the launch was.
- **Exact brackets** are the engine's `--frame-stats` with `ONI_BUCKET_PROFILE=1`, in launches
  of their own (one at default, one with every tweak). Frame stats cost about 8% here (26.5
  against 28.6 fps at default), so those launches give the split, not the frame rate.
- **The split below the brackets** is `perf record -e cpu-clock:u -F 499 -g` on Linux, 60 s from
  100 s after the load, one launch at default and one with every tweak, JIT frames resolved from
  the perf map. On Linux the default colony runs at 12 fps, where the game caps a frame at
  200 ms of game time, so a Linux frame holds twice the sim work of a Windows one: its shares
  are read within the sim and within the rest, not across them.
- **Every tweak** here is the re-profile's set: the defaults plus `fetch-by-cell`,
  `off-screen-conveyors`, `sim-step-pace`, `brain-rate` and `off-world-fallers`.
  `direct-rail-bridges` was left out: it came after the re-profile, opt-in at first and on by
  default since 2026-11-14.
- **Launches:** two on Linux and five on Windows, each once.
- **Outside load** was sampled every 2 s (the machine's CPU less the game's). The first matrix
  launch was disturbed: 23–51% of the machine's cores were busy outside the game for its first
  three and a half minutes (another tool's game was running on the desktop; runq held the later
  measurements while one ran). Its second half, at 6–21%, is used only for bracket times. The
  other launches saw 6–16%, but for the end of the every-tweak frame-stats launch (below).

## Where a default frame goes

Windows, default settings, frame stats on: 37.7 ms a frame (26.5 fps), six windows of 600
frames. Indented rows are part of the row above.

| Part | ms a frame |
|---|---|
| Update phase | 23.25 |
| · `Game.Update` | 19.25 |
| · · `Game.SimEveryTick` | 17.69 |
| · · · state machine and component sub-ticks (3.4 a frame) | 13.80 |
| · · · · the gravity step (`KComponents`' 33 ms tick: 17,566 falling objects) | 4.43 |
| · · · · solid transfer arms (324, once a game second: 11.7 ms a run) | 1.29 |
| · · · · `RequireInputs` (2,228 buildings) | 0.75 |
| · · · · navigators moving (88) | 0.65 |
| · · · · radbolts (48) | 0.44 |
| · · · · rot, exposure, sublimation, critter temperature, ranching, amounts, overcrowding, dreams | 0.2–0.3 each |
| · · · · some 490 other kinds | about 4.2 |
| · · · `Game.UnsafeSim200ms` (0.57 a frame) | 3.89 |
| · · · · `StepTheSim` (SimDLL 0.2) | 2.91 |
| · · · · nav grids | 0.83 |
| · `Global.Update` (animation events) | 1.06 |
| · `GameScheduler`, `WorldInventory`, smaller updates | about 1.3 |
| LateUpdate phase | 9.38 |
| · `Game.LateUpdate` | 4.99 |
| · · brains: 1 Duplicant and 5 critters a frame | 4.15 |
| · · · the Duplicant's fetch update (`FetchManager.UpdatePickups`), mostly waiting for the job workers | 3.0–3.5 |
| · `Global.LateUpdate` (animation updates) | 1.76 |
| · `World.LateUpdate` (animation batch uploads) | 1.57 |
| · `PropertyTextures`, trackers, smaller updates | about 1.0 |
| Engine after LateUpdate (canvases) | 1.40 |
| Rendering (engine) | 3.48 |

The GPU is busy 4.6 ms a frame. The job system runs 11.9 times a frame for 4.4 ms of wall time,
3.2 of it waiting for workers. The fetch update's time is from the first matrix launch's bracket.

**The items' share.**
- **Gravity, 4.4 ms.** 17,539 objects are stepped 30 times a game second, 3.4 steps a frame:
  74 ns an object. On Linux a quarter of the step is the lookup of each object in the gravity
  manager's clean-up set (`HashSet<object>.Contains`, a virtual `GetHashCode` on the
  GameObject) and the rest is the loop's own reads of the object's transform and the grid.
- **The fetch update, 3.0–3.5 ms.** Each frame one Duplicant's brain rechecks all 47,900
  fetchables: `CouldBePickedUpByMinion` for each, then a path cost per cell and a sort. The work
  is one job per kind, so its wall time is the largest kind's: the 20,545 missiles.
- **Everything else about them is small.** On Linux, of the main thread's time: the scene
  partitioner 3.1%, the cell-change monitor 2.7%, `Pickupable`'s own methods 2.4%,
  `instant-reachability` 0.2%, `WorldInventory.Update` 0.6%. Animation updates run for
  playing and moving controllers, not for items at rest; what drawing the 19,591 in view costs
  was not separated from the rest of rendering (3.5 ms).

Linux, the main thread's CPU time (90% busy in both launches):

| | Default (12.7 fps) | Every tweak (23.3 fps) |
|---|---|---|
| `Game.SimEveryTick` | 64.0% | 45.5% |
| · sub-ticks | 51.8% | 31.5% |
| · · the gravity step | 22.3% | 0.2% |
| · `Game.UnsafeSim200ms` | 12.1% | 13.9% |
| Brains | 4.4% | 5.5% |
| · `FetchManager.UpdatePickups` (the main thread's part) | 1.6% | 0.8% |
| Animation updates and batch uploads | 6.8% | 10.7% |
| Animation events (`Global.Update`) | 1.4% | 1.9% |
| Cell-change monitor | 2.7% | 4.0% |
| Rendering | 5.6% | 9.7% |
| Canvases | 2.7% | 4.6% |

The fetch update's groups used 0.14 of a core over all threads at default and 0.08 with every
tweak.

## Why a saving counts twice

At speed 3 a real second carries three game seconds, and the sim's work is paid per game
second: 90 sub-ticks and 15 sim steps, whatever the frame rate. On Windows that is about 117 ms
a game second without the gravity step and 39 ms for it. So at default 47% of the main thread
goes to work a shorter frame does not shrink, and the per-frame rest is about 19 ms. A frame is
then the per-frame work over the share the sim leaves:

- default: 18.6 / (1 − 0.47) = 35 ms;
- gravity gone: 18.6 / (1 − 0.35) = 28.6 ms (measured 26.6);
- the fetch update 2.9 ms shorter: 15.7 / (1 − 0.47) = 29.5 ms (measured 29.1).

With every tweak the sim is still 36% of the main thread, and the frame is 12 ms of per-frame
work over the other 64%.

## What each opt-in tweak buys here

Windows, speed 3, one launch (`j0000976`), blocks of 10 s in this order: D, C, D, C, D, O, D,
then `off-world-fallers` installed, then F, F+C, F, F+C, A, A−B, A, A−O, A, A−B, A, A−O. D is
the default set; F `off-world-fallers`, C `fetch-by-cell`, B `brain-rate`,
O `off-screen-conveyors`; A all four.

| Configuration | Blocks, ms a frame | Mean | fps | p50 / p99 ms | Against |
|---|---|---|---|---|---|
| Default | 36.0, 33.6, 34.7, 35.4 | 35.0 | 28.6 | 33.7 / 54.6 | |
| + `off-screen-conveyors` | 33.2 | 33.2 | 30.2 | 31.9 / 51.0 | −1.9 ms against its neighbours (35.1): one block, inside the default blocks' spread |
| + `fetch-by-cell` | 29.7, 28.4 | 29.1 | 34.4 | 28.0 / 50.1 | −5.5 ms against its neighbours, +20% |
| + `off-world-fallers` | 26.3, 26.8 | 26.6 | 37.7 | 25.4 / 45.4 | −8.4 ms, +32% |
| + both | 20.3, 20.4 | 20.3 | 49.2 | 19.3 / 35.9 | −14.6 ms, +72% |
| Every tweak | 18.7, 18.2, 18.6, 18.8 | 18.6 | 53.8 | 17.6 / 34.3 | −16.4 ms, +88% |
| Every tweak without `brain-rate` | 19.5, 20.5 | 20.0 | 50.0 | 18.9 / 35.3 | +1.4 ms against its neighbours |
| Every tweak without `off-screen-conveyors` | 18.2, 18.9 | 18.5 | 54.0 | 17.7 / 34.4 | −0.1 ms against its neighbours |

- **`off-world-fallers`:** 17,539 objects landed; the gravity step went from 17,566 objects to
  about 30.
- **`fetch-by-cell`:** the fetch update went from 3.0–3.5 to 0.31–0.34 ms a call (the first
  matrix launch's bracket).
- **`brain-rate`** does nothing at default (below 30 fps the game's own count is under the
  cap). With the others on, the colony is at 50 fps and the cap takes brain updates from 50 to
  30 a second: brains 1.4 → 1.0 ms a frame.
- **`off-screen-conveyors`:** 139 of 200 moving rail items are out of view. The rails' frame
  update is 0.15–0.2 ms in all.
- **`sim-step-pace`** was not measured: it acts above 60 fps at speed 1.
- **Not measured alone:** `brain-rate` and `off-screen-conveyors` on top of the default set
  only (the first does nothing there by construction), and any pair but the one above.

## Could they be on by default?

### `off-world-fallers`

- **Why it is opt-in.** It is in the Bug fixes group and out of `exact`, because the objects'
  state changes: they stop counting as falling.
- **The bug.** `GravityComponents.FixedUpdate` leaves an object where it is when its next
  position is outside its world, and keeps it falling. An item dropped through the bottom of a
  rocket interior is stepped forever, half a cell below the grid.
- **What the tweak changes.** It lands such an item where it hangs, through the game's own item
  landing (`FallerComponents.OnLanded`), once no later step could move it. Then:
  - the item is no longer in `GameComps.Gravities`. The game reads that for critters, robots,
    comets and power banks, and in `Pickupable.OnOreSizeChanged`, which only carries a falling
    item's speed over;
  - no Landed event is sent. None would be sent in the game: the item never lands;
  - if the rocket is taken apart and its items are moved to the world below, the item falls
    from rest instead of at the speed it had;
  - the position does not change, and gravity is not saved, so the save is the same with the
    tweak on or off. After a load the items fall again until their first step.
- **What a player would notice.** Nothing on screen: the items hang below the interior's view
  and stay there. The frame rate on a colony that has them: 28.6 → 37.7 fps here. On a colony
  without them it does nothing (U57: 3 objects landed).
- **An exact variant.** Possible in principle: keep the objects falling and skip the step for
  one whose step provably repeats (its transform, its speed and the cells the step reads are
  unchanged), adding only the step's time to its `elapsedTime`. It would need a mirror of the
  game's step and invalidation from our transform and the grid, for the same gain as the
  landing. Not designed.
- **A cost of the current form.** The tweak transpiles the step, and so does any patch of it.
  The game's method then runs as Harmony's copy, measured here at 1.9–2.5 ms a call for 17,566
  objects against 1.30 ms unpatched. With the objects landed the loop has 30 objects and the
  cost is nil. On a colony with many objects falling for real it is 35–70 ns an object a
  step. A landing pass of its own (once a second over `GameComps.Gravities`, the same test)
  would leave the game's loop unpatched. It would also take the tweak off the list of patches
  other mods' transpilers of that method have to live with
  ([tweak-step-aside-audit.md](tweak-step-aside-audit.md), rows 14 and 18).
- **Landed 2026-11-14: the tweak no longer patches the step.** It runs around it, from a
  prefix and a postfix on `KComponentSpawn.Sim33ms` (the scheduler's call that holds the
  step): before the step it notes the falling items within 16 cells of the grid's bottom row,
  after it it lands the noted ones the step left in place for good
  ([TWEAKS.md](../TWEAKS.md#items-lost-below-a-world)). It now stands down for another mod's
  prefix, transpiler or finalizer on the step. Measured in launches started without the tweak,
  patches installed at run time, 10 s blocks after 4 s left out (Windows, speed 3, U60 763023;
  the step timed in a copy of the scheduler's bucket loop, with no patch on the step or its
  caller):

  | Pile-heavy colony | Step, ms a call | Step, ms a frame | Frame, ms |
  |---|---|---|---|
  | Default, 17,566 falling (3 blocks) | 1.45–1.50 | 4.6–4.9 | 35.0, 35.6, 36.6 |
  | New hook installed, idle (2) | 1.48, 1.51 | 4.9–5.0 | 36.1, 37.5 |
  | New tweak on, 17,539 landed (3) | 0.021–0.024 | 0.05–0.06 | 28.0, 26.5, 25.7 |
  | Old transpiler installed, idle, the items falling again (2) | 1.79, 1.79 | 5.9 | 36.5, 36.5 |
  | Old tweak on, landed (3) | 0.015–0.016 | 0.03–0.04 | 24.9, 24.3, 23.9 |

  - Patching the step's caller does not slow the step (1.50 against 1.47 ms a call); the
    transpiled step ran 22% slower here (the study saw 1.9–2.5 against 1.30).
  - The gain is kept: 35.7 → 26.7 ms, +34% (the study's transpiler: 35.0 → 26.6). With the
    items landed the passes cost about 6 µs a step over the transpiler's (0.022 against
    0.016 ms a call for 29 objects), 0.02 ms a frame.
  - The old blocks came after the new ones and the frame fell block by block throughout, so
    the 2 ms between them is the launch's drift. A second launch in the order old, new, old
    (disturbed: outside load median 17%, and its frames rose instead) gave old 31.0 and 33.4,
    new 30.0 and 31.5, old 32.7 and 33.4 ms. Neither shows the new form slower.
  - A mid-game colony with 3 such items, same method: step 6.4–6.6 µs a call at default,
    5.3–6.5 with the hook idle, 7.1–7.3 with the tweak on, 6.3–7.0 with the transpiler; frame
    9.1–9.5 ms in every block. The loop holds 2 to 16 objects there, so this shows the passes
    cost under 1 µs a step, not the transpiler's cost for each falling object.
  - Verify mode (the transpiler installed too, only noting what it would land), Linux, 8,192
    steps each on U60 763023 and U59 744825: 17,549 items landed, each step's landings the
    transpiler's in the same order, 0 differences.
- **For on by default:**
  - It fixes a bug in the game's own code, as the `fix-*` tweaks that are on by default do.
  - Nothing on screen and nothing in the save changes.
  - It is the largest single gain on a colony that has the objects.
- **Against:** it is the one bug fix that changes an object's simulation state rather than a
  screen or a sound, and its gain is nil without the bug.

### `fetch-by-cell`

- **Why it is opt-in.** Where two items of one kind have the same tags, priority, path cost,
  food quality and freshness, the game's sort (unstable) picks one of them and the tweak may
  pick the other. Such items can still differ in mass, temperature and germs, and two piles at
  the same path cost are two places.
- **What a player would notice.** A Duplicant may take another of several identical items, or
  walk to another pile at the same distance. Nothing else: the path costs, the kinds and the
  order of the merged list are the game's.
- **A narrower exact part.** Two parts of it are exact as they stand:
  - *piles with no path.* The game drops every item whose cell has no path cost before its
    sort, so leaving such a pile out without checking its items changes nothing. On this colony
    that is 69% of the fetchables for the average Duplicant and never under 41%;
  - *winners that are alone.* The game's result for a kind and priority is the least item by
    path cost, quality and freshness. When one item alone is least, the sort's order cannot
    matter.

  What is left is a kind whose least items tie. An exact tweak would run the game's own pass
  for that kind in that update.
- **How often that would be, measured** (verify mode and a local counter, one launch, 3,549
  fetch updates, 715,889 results for a kind):
  - 36% of the results were empty (nothing of the kind within reach), 50% were decided
    without a tie, and 14% had a winner that shares its key with another item: 13.4% of all
    winners, 2.1% with an item of another pile at the same path cost, the rest within one pile;
  - the tweak's choice was another item than the game's in 5.7% of the results, and 96% of the
    updates had at least one such result among their 200 or so. Nothing differed but by a tie,
    and no item in a left-out cell had a path (72 million checked);
  - the kinds with a possible tie held 10,083 of the 46,688 items an update on average
    (22%). The missiles are 20,545 of them, so they tie in at most half the updates: the ones
    of Duplicants that can reach their piles.
- **What an exact form would keep (estimated).** The by-pile pass for every kind, then the
  game's pass for the kinds with a possible tie: 22% of the items' checks, and the update's
  wall time whenever the largest kind is among them. That is the whole gain in the updates
  without the missiles and almost none in those with them: half the 5.5 ms or more. It is not
  built, and the tie test would need its own verify mode.
- **Against on by default as it is:** the ties. **For:** it is the second largest gain here
  and the largest on the other colonies with big piles ([PERFORMANCE.md](../PERFORMANCE.md)).

### `brain-rate`

- **Why it is opt-in.** It changes timing: above 30 fps a Duplicant re-evaluates its errands,
  and a critter its behaviours, less often per real second than without it.
- **No exact variant exists.** The game updates 1 Duplicant and 5 critter brains per rendered
  frame, so the rate is the frame rate's, and there is no rate that is the game's own but the
  one its constants state: 30 fps. The tweak holds the updates to that.
- **On this colony:** the original player runs it at 4.7 fps, so about 5 Duplicant brain
  updates a second. Moono's default runs 29, with every tweak but this one 50, with it 30.
- **What a player would notice.** Above 30 fps an idle Duplicant can wait a little longer for
  its next errand than without the tweak: with 67 Duplicants a full round takes about 2.2 s at
  30 updates a second and 1.3 s at 50. One that just finished an errand or is under threat still
  gets the next update.
- **Gain here:** 1.4 ms of 20.0 with the other tweaks on (+7%), nothing at default.

### `off-screen-conveyors`

- **Why it is opt-in.** While off screen, a travelling item's transform lags behind its place
  in the rails' flow, and with it the cell the game files the item under for partitioner
  lookups, decor and radiation.
- **No gain measured here** (−0.1 ms with the others on; −1.9 ms in one block at default,
  inside the spread). The rails' frame update is 0.2 ms on this colony.

## What is left after every tweak

Windows, every tweak, frame stats on: 22.1 ms a frame (45.2 fps), the launch's three
undisturbed windows (outside load rose to 20–23% of the machine's cores in the later ones, and
the frame rate fell to 36; they are left out).

| Part | ms a frame |
|---|---|
| Update phase | 12.19 |
| · `Game.Update` | 8.98 |
| · · `Game.SimEveryTick` | 7.97 |
| · · · sub-ticks (2 a frame) | 5.66 |
| · · · · solid transfer arms (324, once a game second: about 10 ms a run) | 0.69 |
| · · · · `RequireInputs` | 0.48 |
| · · · · navigators moving | 0.38 |
| · · · · radbolts | 0.24 |
| · · · · rot, sublimation, exposure, critter temperature, ranching, amounts, overcrowding | 0.14–0.20 each |
| · · · · the other kinds | about 2.7 |
| · · · `Game.UnsafeSim200ms` (`StepTheSim` 1.73, nav grids 0.48) | 2.30 |
| · the cell-change monitor and the rest of `Game.Update` | 1.0 |
| · `Global.Update` (animation events) | 0.80 |
| · `WorldInventory`, `GameScheduler`, smaller updates | about 1.2 |
| LateUpdate phase | 5.37 |
| · `Game.LateUpdate` (brains 1.01) | 1.70 |
| · `World.LateUpdate` (animation batch uploads) | 1.43 |
| · `Global.LateUpdate` (animation updates) | 1.33 |
| · `PropertyTextures`, trackers, smaller updates | about 0.9 |
| Engine after LateUpdate (canvases) | 1.11 |
| Rendering (engine) | 3.23 |

The job system runs 10 times a frame for 1.0 ms, 0.45 of it waiting. The gravity step and the
fetch update are gone from the table: 0.03 and 0.2 ms.

Ranked, with what the earlier inventories say of each:

| # | Cost | ms | Share | Exact candidates |
|---|---|---|---|---|
| 1 | Sim sub-ticks | 5.7 | 26% | Several hundred kinds, the largest 0.7 ms. `RequireInputs`, the radbolts, the dream bubbles and the state machine lookups were taken or ruled out in [gluttony-frame-inventory.md](gluttony-frame-inventory.md) and since. **New:** the transfer arms, the largest single kind here and a 10 ms frame once a game second; not studied, exactness unknown; at most 0.7 ms. The airtight-suit check (0.15 ms on Linux) was never tried |
| 2 | Animation: updates, batch uploads, events | 3.6 | 16% | The `fast-anim-*` tweaks are in; what is left is per playing animation |
| 3 | Rendering | 3.2 | 15% | Draw recording's uniform lookups (0.3–0.5 ms on Linux, inventory candidate 2) were never tried; the similar per-block cache gave nothing |
| 4 | The 200 ms sim step and nav grids | 2.3 | 10% | [sim-step-cost.md](sim-step-cost.md): candidates 2–4 are parked, 6–10% of the step, so 0.1–0.2 ms here |
| 5 | Small per-frame updates | 2.1 | 9% | `WorldInventory.Update` (0.27) and `PropertyTextures` (0.35) were looked at and dropped |
| 6 | Brains and the rest of `Game.LateUpdate` | 1.7 | 8% | Chore searches; `brain-rate` already caps them |
| 7 | Canvases | 1.1 | 5% | Time rejected in the U60 perf-wins study (`perf-wins.md` on the u60 branch, candidate 4) |
| 8 | The cell-change monitor and the rest of `Game.Update` | 1.0 | 5% | Switched-off effects left out of it: measured and dropped ([PERFORMANCE.md](../PERFORMANCE.md#tried-and-dropped)) |

No exact candidate above 1 ms is left. Everything in the last column together is about 1–1.5 ms
of 18.6 (estimated), which would take 54 fps to 57 or 58. The frame is what
[gluttony-frame-inventory.md](gluttony-frame-inventory.md) found on 2026-10-22: ordinary game
logic spread thin. The lever that remains is the sim's 120 ms a game second, 36% of the main
thread at speed 3 and 12% at speed 1.

## Is the colony like anything players hit?

- **The hanging objects are rare.** They need a chute (or any drop) through the bottom row of
  a rocket interior, kept running: here two rockets, for thousands of cycles. U57 has 3 such
  objects. One web search found no report of it. Where it happens it grows without bound, since
  the objects never merge and never go away.
- **The big piles are ordinary late-game play.** 20,578 missiles in six cells, 422 dream
  journals, 746 food packages: a fabricator on "forever", or a ranch's by-products, with nobody
  using the output. Discrete items never merge, so such piles grow for as long as the colony
  runs. The other late-game colonies measured for `fetch-by-cell` have 21,000 to 33,600
  fetchables in 3,600 to 3,900 piles; this one has 47,900 in 3,982, and 43% of them are one
  kind.
- **What default settings do for each:** nothing for the hanging objects (8.4 ms a frame here)
  and nothing for the piles' fetch checks (5.5 ms here). The piles' other costs (animation,
  partitioner, reachability) are already small at default.
- **The view.** The save opens on a rocket interior with 19,591 loose items in chunks in view.
  Other views were not measured.

## U59

Code reading only. `FetchManager`'s update is the same in U59 (the two builds differ in
`IsFetchablePickup`, six lines), and `GravityComponents.FixedUpdate` differs only in where the
fake-floor test lives. U60 moved the fallers' cell-change handler to the object's event system
and added `UpdateOffsetTables`; neither is in these paths. The re-profile's U59 figures (28.6 and
48.5 fps) agree.

## Measured, estimated, not tested

- **Measured on Windows:** the default frame's brackets (one launch); each configuration in
  the table above (one launch, alternating blocks); the every-tweak frame's brackets (one
  launch); the ties of `fetch-by-cell` (one launch in verify mode).
- **Measured on Linux:** the two profiles' shares and the counts.
- **Estimated:** the two-fold effect's arithmetic; the gains of the variants named above, none
  of which was built.
- **Not tested:** U59; speed 1 and 2; other views of the colony; the cleaned save;
  `direct-rail-bridges`; `sim-step-pace`; any change to the tweaks themselves.
- **One launch each.** The in-launch blocks are the evidence; no number here is a comparison
  between launches.

## Runs

Local, not in the repository: the worktree `out/piles-u60` (probe code in
`tools/PerfProbe/Matrix.cs` and `Piles.cs`, switches in four tweaks), and the session's
scratchpad folder `piles` (runners `linprof.sh`, `winjob.sh`; `an.py`, `matrix.py`,
`fstats.py`; logs, perf data and maps). runq jobs: Linux `j0000960` (default), `j0000964`
(every tweak); Windows `j0000962` (default, frame stats), `j0000965` (first matrix, disturbed),
`j0000976` (matrix), `j0000978` (every tweak, frame stats), `j0000980` (fetch ties).
