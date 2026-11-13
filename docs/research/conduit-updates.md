# Conduit updates: where the once-a-second work goes (2026-11-12)

The question: the heavy 200 ms sim step ([sim-step-cost.md](sim-step-cost.md)) carries the gas
and liquid `ConduitFlow.Sim200ms` and the conveyor rails' `SolidConduitFlow.Sim200ms`, which do
their work once a game second. Which game code costs what there, how much of it runs when nothing
changed, and which exact speed-ups are within reach?

**Verdict.**
- **The endpoints do real work.** Consumers, dispensers, bridges, valves and rail hand-offs move
  mass and items and fire game-visible events. Few of them are idle, and none is cheap to skip.
  Full consumers with unchanged storage and empty dispensers cost about 1–2 µs a call.
- **The best candidate is outside the endpoints.** Every `Grid.Objects` write runs a
  scene-partitioner event, which walks every entry in the cell's 16×16 node on that object layer
  and looks each one up. On the Pickupables layer no entry has a callback, so the walk does
  nothing.
  - **Late-game colony:** the walk is 2.3% of the main thread game-wide, and 22% of the rails'
    update (1.35 ms a game second on Linux).
  - **Prototype:** skip the walk where it has no effect, dropping freed handles the way the
    game's walk would. Verified exact: 0 differences on all three saves. It saves 86% of the
    walks' time on the late-game colony, about 2% of the main thread and about 1.2 ms a game
    second of the rails on Linux. On the other two saves it saves 82% and 56% of a cost that is
    only 0.1% of the main thread there.
- **Small colonies pay mostly for job dispatch.** On the 200-Duplicant colony and the
  reporter's colony, the pipe flows cost about 4.3 ms a game second on Linux. Only about 1 ms of
  that is endpoints. The rest is six or more job runs a flow, about 0.3 ms each, mostly waiting
  for workers to wake. This needs Windows numbers before anything is built.
- **Two exact leads are measured and small.**
  - *The updaters are re-sorted every heavy step* (a Klei bug: `dirtyConduitUpdaters` is never
    cleared). The sort costs 35 µs a flow on Linux.
  - *A consumer's free space is computed with two storage walks.* Fusing them saves 5% of
    the getter.

  Both are verified exact. Both are too small to build on their own.

## Method

- **Players:**
  - U60 (762230): Moono u60 at 0bf0c5b6 (origin/u60), release, default tweaks. Linux players
    under runq (`linux-perf` for the measured runs, `linux-player` for prototype checks),
    Xvfb, speed 3.
  - U59: the reporter's colony on Moono master at f4a3a222 with its 39 mods (Speed Control
    off, as in the low-fps study).
  - The 763023 update changes `AttributeInstance.GetTotalValue` (a NaN clamp). Its share in
    storage-event handlers such as `IrrigationMonitor` may be a little higher there. Every
    comparison here stays within one run, so within one build.
- **Saves:**
  - the late-game colony (24 Duplicants, about 1,200 critters): 2,857 liquid and 915 gas
    updaters, 1,442 rail updaters;
  - the 200-Duplicant colony (U60): 522 liquid, 137 gas, 15 rail updaters;
  - the reporter's colony (U59, mods): 616 liquid, 146 gas, 158 rail updaters.
- **Probe:** `tools/PerfProbe/ConduitCost.cs`, `ConduitProto.cs` and `PartProto.cs` (local,
  uncommitted test tooling, beside StepCost). One launch per save runs:
  1. a 120 s `perf record -e cpu-clock:u -F 1999 -g` of the whole process, with nothing
     patched;
  2. a flows-only bracket phase (the flows and their phases);
  3. verify passes and in-run off/on blocks for the prototypes;
  4. brackets on every distinct updater method, keyed by flow and owner prefab;
  5. brackets on about 60 of the methods the updaters call, plus `EventSystem.Trigger` by
     event hash;
  6. an outcome census.
- **Units:**
  - **ms a game second:** one heavy step of each flow, so all three flows once. At speed 3 a
    real second holds about three of them.
  - **perf time:** main-thread CPU (samples / 1,999 Hz / heavy steps in the window).
  - **bracket time:** wall time, and includes the time the main thread waits for workers.
  - **bracket overhead:** about 0.5 µs a bracket. Sub-µs updaters such as `RequireOutputs`
    and bridges show mostly overhead, so their real costs come from perf.
- **Linux only.** The machine was shared, and no Windows run was made. Windows ms are not
  given. Use shares, and the earlier study's Windows/Linux step ratio (about 0.75), for
  direction only.

## The three flows on each save

Bracket wall time a game second, Linux. The flows-only phase:

| | gas | liquid | rails | rails' partitioner walk (perf) |
|---|---|---|---|---|
| Late-game colony | 2.27 | 6.15 | 7.63 | 1.35 |
| 200-Duplicant colony | 1.44 | 2.87 | 0.19 | ~0 |
| Reporter's colony | 1.40 | 2.70 | 0.74 | ~0 |

On the late-game colony the three flows are about half of the five-step cycle (31 ms on Linux,
which matches the earlier study).

## Call tree, late-game colony

Main-thread CPU (perf), ms a game second. Calls and µs per call are from the bracket phases.

### Pipes: gas and liquid, 5.34 ms CPU (8.42 ms wall)

- **`ConduitConsumer.ConduitUpdate`, 1.86 ms.** 749 calls: 574 liquid, 175 gas; about 2.8 µs
  per liquid call when bracketed.
  - **`Consume`, 1.72 ms:**
    - **`Storage.AddLiquid`, 0.76 ms** (234 a game second, 4.5 µs). Mostly the
      `OnStorageChange` event, 0.50 ms; its largest handler is
      `IrrigationMonitor.UpdateIrrigation`, 0.20 ms. `LiquidSourceManager.CreateChunk`
      takes 0.12 ms (about 1 a game second at 137 µs: object creation).
    - **`space_remaining_kg`, 0.42 ms:** the `stored_mass` walk 0.37 ms
      (`GetMassAvailable(tag)` 1.2 µs a call). The second walk, `RemainingCapacity`, is cheap
      (0.3–0.5 µs).
    - **`AddGasChunk`, 0.09 ms;** the pipe lookup (`IsConnected`, `GetContents`) under
      0.1 ms; `RemoveElement` 0.1 ms.
  - **One outlier:** a single `NuclearReactor` consumer costs 163 µs a call.
- **`ConduitDispenser.ConduitUpdate`, 0.87 ms** (496 calls, about 1.9 µs).
  - **Its own code, 0.5 ms:** `IsConnected` (a `Grid.Objects` dictionary read and
    `GetComponent<BuildingComplete>`) and `Operational.SetFlag` (a dictionary read).
  - **`Dispense`, 0.35 ms:** `FindSuitableElement` 0.13 ms; the `OnStorageChange` and
    `OnConduitObjectDispensed` events 0.13 ms.
- **Network solve, 0.69 ms on the main thread.** `UpdateConduit` jobs: about 8.0 ms of worker
  CPU a game second, and 0.58 ms of the solve itself on the main thread.
  - **Wall time inside `GlobalJobManager.Run`:** liquid 0.82 ms for the solve (four
    iterations), gas 0.03 ms. `BeginFrame` and `EndFrame` jobs are about 0.3 ms each per flow,
    whatever the flow's size.
  - **Workers:** 2.0 ms of CPU a game second on BeginFrame and EndFrame
    (`InitializeContents`, `InvalidateLastFlow`, `PublishDiseaseToGame`).
- **Conduction panels** (`ContactConductivePipeBridge.Flow`), 0.32 ms: the temperature
  exchange.
- **Bridges** (`ConduitBridge`), 0.25 ms (820 calls); **`RequireOutputs.UpdatePipeState`**
  0.1–0.2 ms (1,363 calls).
- **`EndFrame` on the main thread, 0.22 ms:** `PublishTemperatureToGame`, a SimDLL call per
  conduit, 0.14 ms.
- **`Operational.UpdateOperational`, 0.15 ms.**
  - Its events (0.11 ms) are real changes. `RequireOutputs` turns about 25 liquid and 13 rail
    buildings' operational flag over every game second (`OperationalChanged`,
    `BuildingStateChanged`, state-machine transitions).
  - Dispensers run `UpdateOperational` without a change about 100 times a game second, for
    buildings that are connected but not operational.
- **The updaters' sort, 0.105 ms** (see candidate 2). `Sim200ms`'s own code is 0.64 ms: the
  updater loop and the networks' `Finish` on the main thread (1,058 calls, under 0.27 ms
  bracketed).

### Rails: 6.15 ms CPU (7.63 ms wall)

- **`SolidConduitBridge.ConduitUpdate`, 2.84 ms** (422 calls; 55 move an item).
  - `RemovePickupable` 1.65 ms and `AddPickupable` 0.53 ms: the store round trip.
  - `Pickupable.Take` 0.37 ms (6 a game second, 65 µs each: a chunk split).
  - `LimitValve.OnMassTransfer` 0.21 ms.
- **`SolidConduitConsumer.ConduitUpdate`, 1.74 ms** (204 calls; 13 take an item).
  - `RemovePickupable` 0.98 ms and `Storage.Store` 0.49 ms.
  - `GetConnectedNetworkID` 0.06 ms.
  - A `SolidVent` call costs 20 µs.
- **Dispensers:** `Pickupable.Take` and `EntitySplitter`, 0.47 ms. 8 of 190 calls send an item.
- **Fixed costs:** the path walk 0.22 ms, `UpdateFlowDirection` 0.14 ms, `RequireOutputs`
  0.14 ms, the sort 0.03 ms.
- **Inside the round trips:**
  - `Pickupable.OnStore` removes and adds the `Stored` tag. `KPrefabID.RemoveTag` is 1.88 ms,
    through its `TagsChanged` handlers (state-machine transitions, `Pickupable.UpdateListeners`).
  - `ObjectLayerListItem.Refresh`'s `Grid.Objects` writes are 1.35 ms. Of that, 85% is the
    partitioner walk (candidate 1).
  - `SaveManager.Unregister` is now 0.02 ms (`fast-save-roots`).

### Other saves

- **200-Duplicant colony:**
  - Endpoints, bracketed: liquid consumers 0.65 ms (167 calls, `HydroponicFarm` 5.5 µs),
    dispensers 0.23 ms, `RequireOutputs` 0.15 ms.
  - Rails 0.19 ms.
  - Pipes are 4.3 ms of wall time, so about 3 ms a game second is the job runs.
- **Reporter's colony:**
  - Endpoints: liquid consumers 0.87 ms (199 calls), rail bridges 0.21 ms (12 calls, 17 µs).
  - Pipes: 4.1 ms of wall time, 0.7% of the main thread in perf. The rest is the job
    runs.

## What scales with what, and what happens when nothing changed

- **Pipes' fixed cost:** six or more `GlobalJobManager.Run` calls a flow (BeginFrame, up to four
  solve iterations, EndFrame) at about 0.3 ms of wall time each on Linux, whatever the size. On
  the small saves it is most of the pipes' time.
- **Per conduit:** the solve (on workers) and the frame jobs; on the main thread, the
  temperature publish (a SimDLL call per conduit) and the network `Finish`.
- **Per endpoint:** 1–5 µs a call, mostly cache misses on the building, its storage and its
  items (Moono's component layouts already make `GetComponent` cheap).
- **Per moved item or mass:** storage events and their handlers, chunk creation, and on rails
  the store round trip (20–65 µs a hand-off).

Outcome census, late-game colony, per game second:

| Updater | Total | Idle or unchanged | Doing work |
|---|---|---|---|
| Liquid consumers | 574 | 160 full (154 with storage as at the last call), 93 pipe empty, 56 not operational, 19 unconnected or off | 246 consume |
| Gas consumers | 175 | 50 full (49 unchanged), 16 pipe empty, 25 not operational | 83 consume |
| Liquid dispensers | 325 | 162 empty, 47 blocked, 16 off or not operational | 100 dispense |
| Gas dispensers | 171 | 57 empty, 13 blocked | 101 dispense |
| Liquid bridges | 620 | 229 input empty, 143 blocked, 11 unconnected | 237 move |
| Rail bridges | 412 | 357 idle | 55 move |
| Rail consumers | 199 | 186 idle | 13 take |
| Rail dispensers | 190 | 182 idle | 8 send |

The other two saves are similar in proportion. On the 200-Duplicant colony, 45 of 156 liquid
consumers are full with unchanged storage. On the reporter's colony it is 52 of 190.

Idle endpoints are cheap: a full consumer is the pipe read, the operational check and the
storage walk, about 1–2 µs. An idle rail endpoint is the connection and network-ID reads, under
1 µs. Exact skipping of idle endpoints therefore buys little; see candidate 3.

## Candidates, ranked by measured cost removed

| # | Speed-up | Exact? | Cost it removes (Linux) | Evidence | Mod risk | Branch |
|---|---|---|---|---|---|---|
| 1 | **Partitioner event walks on callback-free layers.** In `ScenePartitioner.TriggerEventInternal`, skip a node's walk when no entry on its layer has ever had a callback, and drop the node's freed handles as the walk would | Yes. The walk's only effects are the callbacks (none on such a layer) and dropping freed handles. `ScenePartitioner.Remove` marks a freed handle's nodes dirty; `Sim1000ms` drops the handles and clears the flag. So a clean node holds none, and a dirty node's freed handles are those removed since the last `Sim1000ms`. Dropping them in descending list order leaves the list as the game's descending swap-remove walk does (the reasoning `fast-partitioner-cleanup` uses). `eventCallback` is set only by `ScenePartitionerEntry.Init` | Late-game colony: 2.3% of the main thread game-wide (1.35 ms a game second in the rails, 5.45 ms outside the step); the prototype saves 86% of the walks' time, so about 2.0% of the main thread and about 1.2 ms a game second of the rails. 200-Duplicant colony: 82% of the walks' time; reporter's colony: 56%. There the walks are 0.1% of the main thread | **Measured**, in-run A/B (four off/on pairs) and verify: on the late-game colony, 97,572 skips with 2,394 dirty nodes cleaned (2,401 handles), 0 wrong; 200-Duplicant colony, 551 dirty nodes cleaned (674 handles), 0 wrong; reporter's colony 127 (128), 0 wrong. The verify replays the game's walk on a copy and compares the lists | Medium. Fast Track (local build), MoreTek and Colony Performance Fixes patch `ScenePartitioner.TriggerEvent`, `Insert` or `Sim1000ms`; Multiplayer Mod (Alpha) names `ObjectLayerIndexer`. Stand down for foreign patches on `TriggerEventInternal`, `TriggerEvent`, `Insert`, `Remove` and `Sim1000ms`. Share the freed-handle record with `fast-partitioner-cleanup` | Both (identical code in U59 and U60; only the callback's signature differs) |
| 2 | **Updater sort permutation.** `ConduitFlow` and `SolidConduitFlow` re-sort `conduitUpdaters` every heavy step, because `dirtyConduitUpdaters` is never cleared. Keep the permutation per list while the priorities in list order are unchanged, and apply it | Yes. The introsort's permutation depends only on the comparison results, so on the priority sequence; `Array.Sort` of indices runs the same generic algorithm. The permutation is the identity for only 1 of the 9 lists across the three saves: the game reorders equal-priority updaters every game second | 0.105 ms (pipes) and 0.03 ms (rails) a game second (perf); 35 µs a sort. The unoptimized prototype took 25 µs (it copies the list); in place, about 5–10 µs | **Measured**: verify, 327 cached sorts on each of three saves, 0 wrong | Low. Transpilers on `Sim200ms` (15 mods name it, among them Underground Conduit, Industrial Revolution, ONI Together, Stacked Pipes & Rails, Utility Layers, Colony Performance Fixes): stand down for foreign transpilers | Both |
| 3 | **Full consumers, by a storage epoch.** Skip `space_remaining_kg`'s walk when the storage is unchanged since the last call | Possible, but needs an exact epoch: `Storage.items` is a public list, and item mass changes in `PrimaryElement`'s setters | At most about 0.25 ms a game second: about 200 full, unchanged consumers at 1–1.5 µs | Census only | Medium (Partitioned Storage, Applied Logistics Network, StorageNetwork, Storage Quotas, Rocketry Expanded touch `MassStored`, `RemainingCapacity` or `space_remaining_kg`) | Both. Not worth the design on these numbers |
| 4 | One storage walk for `space_remaining_kg` | Yes. Two sums, each in the game's order | 5% of the getter: about 0.02 ms | **Measured**: 59,163 calls, 0 wrong | Low | Dropped |
| 5 | **Small job runs on the caller** (`fast-jobs`). For the conduit frame jobs, whose work is small, avoid waking workers | Yes (the work items are independent per conduit) | Linux: about 0.3 ms of wall time a run. Six or more runs a flow come to about 1.2 ms (gas) and 1.4–2 ms (liquid) a game second on every save. The workers' CPU for that work: 2.0 ms (late-game) and 0.6 ms (reporter's) a game second, so running it all on the caller could cost more | Brackets and perf, not prototyped | Low | Both. Needs Windows wake latencies first: on Linux this is mostly WSL thread wake-ups |

## What was ruled out

- **Skipping idle endpoints in general.** They are already cheap (above). The idle consumers'
  `Operational` reads and the dispensers' `SetFlag` are dictionary reads; `UpdateOperational`
  without a change fires nothing.
- **`RequireOutputs`' operational changes.** They are real. About 38 buildings a game second
  change their operational flag and fire `OperationalChanged`, `BuildingStateChanged` and their
  handlers.
- **The rails' store round trip** (`OnStore`, tags, faller, partitioner, save list). It is
  game-visible, as the earlier study found. Only the partitioner walk inside it has no effect
  (candidate 1).
- **Running `UpdateNetworkTask.Finish` on workers.** It is exact (the networks are disjoint),
  but under 0.27 ms of wall time on the largest save, bracket overhead included.
- **Conduction panels, valves, filters, sensors and meters.** Their costs are in the tree above.
  Each does real temperature, mass or animation work: `ValveBase.UpdateAnim`, about 1 µs;
  `ElementFilter` 22 µs, one call. Only one of them is above 0.05 ms: the conduction panels,
  0.32 ms.

## Landed (2026-11-13)

Candidates 1 and 2 are game tweaks, on by default on both branches: `fast-partitioner-events`
(with the freed-handle record now shared with `fast-partitioner-cleanup`) and
`fast-conduit-sorts`. Both stand down for foreign patches on what they replace and while Fast
Track, MoreTek, Colony Performance Fixes or Multiplayer Mod is active.

- **Verify:** QA's verify stage on the late-game colony (U60 763023, Linux), 840 game seconds,
  0 failures. `fast-partitioner-events`: 2,667,730 walks skipped, 56,150 freed handles
  dropped, 0 wrong. `fast-conduit-sorts`: 2,817 sorts, 0 wrong. `fast-partitioner-cleanup`, on
  the shared record: 500,000 nodes cleaned, 0 wrong.
- **Windows** (U60 763023, the late-game colony at speed 3, High priority, unfocused; both tweaks
  switched by their bypass in four off/on pairs of 100 steps in one run):

  | ms a game second | off | on | change |
  |---|---|---|---|
  | Conveyor rails (`SolidConduitFlow.Sim200ms`) | 5.06 | 3.95 | −22% (pairs −19 to −27%) |
  | The 200 ms step, all five | 24.2 | 23.0 | −5% (pairs −4.4 to −5.7%) |
  | Gas pipes | 1.15 | 1.11 | −3% |
  | Liquid pipes | 4.41 | 4.42 | unchanged |
  | Frame rate | 104.5 fps | 111.8 fps | +2 to +5% in pairs 2–4 (pair 1, +20%, was still warming up) |

  Most of the gain is outside the rails' step too: items moving cell every frame no longer walk
  their node.

## What I would build first

Candidate 1, as a game tweak on both branches:
- the walk skip on layers that have never had a callback entry;
- a per-node record of freed handles (shared with `fast-partitioner-cleanup`, which needs the
  same record);
- a verify mode that replays the game's walk;
- a stand-down for foreign patches on the methods above.

It removes about 2% of the main thread on rail- and item-heavy colonies, about 1 ms a game
second of the heavy step's rails (Linux). Its effect outside the step, items moving cell every
frame, is larger. Before landing it needs Windows ms and fps, measured with the tweak itself.

Candidate 2 is a few lines and could ride along, but it is under 0.15 ms a game second.

Candidate 5 needs a Windows measurement of the frame-job wake latency before any design.

## Runs

Local, not in the repository: out/cc in the conduits-u60 worktree (scenario `study.txt`,
prototype scenarios `part*.txt`, runner `linux-run.py`, `perftree.py`, `summ.py`, `partab.py`)
and the master worktree's out/cc for the reporter's colony. The runs' TSVs, perf data and maps are
in their `runs/` folders.
