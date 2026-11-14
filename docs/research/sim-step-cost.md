# The 200 ms sim step: how far exact tweaks go (2026-11-12)

The question: the frame that holds one of the game's 200 ms sim steps is Moono's jank on big
colonies ([frame-pacing-and-jank.md](frame-pacing-and-jank.md)). How much of that step could
exact game-side tweaks remove? Exact here means the same results, the same order of updates,
events and RNG use, and no delayed temperature or building checks. The working guess was 20–35% of
the step, about 2–4% fps.

**Verdict.**
- **The guess does not hold.** About 6–10% of the step looks reachable with exact tweaks, which
  is roughly 0.5–0.8% fps on Windows. That is an estimate from Linux measurements; Windows fps
  has not been measured yet.
- **One candidate is measured and exact:** a reference scan in `SaveManager.Unregister` cuts the
  conveyor rails' update by 8%, about 1.7% of the step. Every other candidate is an estimate.
- **The step is per-object game work.** Rail hand-offs, pipe consumers and dispensers,
  generators, power consumers and plant monitors each do real, game-visible work. No one cost
  is removable.
- **Two leads from the earlier inventories no longer apply.**
  - *Event handler type checks:* in U60, Klei moved events to `Action<object, object>` entries,
    and the intra-object handlers are gone. Dispatch itself is 1.2% of the main thread
    game-wide.
  - *Lock and pool costs:* the dotnet-trace sampler inflates them. perf puts
    `Singleton<T>.Instance` at 0.27% of the main thread.
- **The fps budget is smaller than the guess assumed.** The step plus the nav grid update is
  about 8% of main-thread time on Windows at speed 3. So 20–35% off the step would have given
  about 1.5–2.5% fps, not 2–4%.
- **Landed:** candidate 1, as the `fast-save-roots` tweak (on by default, both branches).
  Measured with the tweak itself: hand-offs −19%, rails −9%, the step −3%. Verify: 53,017
  removals, 0 wrong. Candidates 2–4 are parked.
- **Closed:** item 8, our nav link store's commit. Its time is waiting for cold memory, which
  a changed commit only moves ([navgrid-links-store.md](navgrid-links-store.md) §7).

## The step comes in a five-step cycle

`ConduitFlow.Sim200ms` (gas and liquid) and `SolidConduitFlow.Sim200ms` do work only once a game
second (`elapsedTime >= 1`), so one step in five carries the conveyor rails and one pipe flow,
and another carries the other pipe flow. From the jank study's section CSVs (late-game colony,
U60, speed 3, `StepTheSim` per step, ms):

| | heavy step (rails, one pipe flow) | 2nd | 3rd (other pipe flow) | 4th | 5th | cycle (one game second) |
|---|---|---|---|---|---|---|
| Windows | 11.6 | 2.6 | 3.7 | 2.8 | 2.6 | 23.3 (+ nav grids 2.9) |
| Linux | 15.6 | 3.4 | 5.4 | 3.5 | 3.5 | 31.3 (+ nav grids 3.2) |

- **What the player sees:** the heavy step is the 15 Hz stutter's worst case, three times a real second at speed 3.
- **Cost a second:** the step costs 70 ms a real second on Windows (about 7% of the main thread at 116 fps), and the nav grid update 9 ms more.

## Method

- **Players:** Moono u60 804f3ced, release, default tweaks. Late-game colony (24 Duplicants, about 1,200 critters). Linux players under `scripts/contain.sh`, Xvfb, perf queue.
- **Machine:** other agents' builds and git work ran beside the runs (load average 6–9). The
  colony ran at 15–50 fps instead of the jank study's 49. Absolute Linux times are high. Shares
  and same-run A/B ratios are what is used.
- **Brackets:** `tools/PerfProbe/StepCost.cs` (local, uncommitted test tooling) records a row
  per sim step, 1,500 steps:
  - every system `StepTheSim` runs, with gas and liquid pipes split;
  - their sub-parts;
  - call counts;
  - and, in a second phase, `EventSystem.Trigger` calls.

  Brackets on methods called about 1,200 times a step inflate those sections, so their ms are
  read only as shares.
- **perf:** `perf record -e cpu-clock:u -F 999 -g` of the player for 90 s without brackets,
  `DOTNET_PerfMapEnabled=1`. JIT frames are resolved against the run's perf map. This is the
  source of every self-time split below.
- **Why perf and not the trace:** the jank study's EventPipe trace puts `Monitor.Enter_Slowpath`
  as the leaf of 29% of main-thread samples. perf finds `Singleton<T>.Instance` (a `lock` on every
  read) at 0.27% inclusive. Only the main thread takes these locks, and a benchmark on .NET
  10.0.13 gives 10–14 ns a call (thin, inflated or hashed). The fast path hands over to the
  slow QCall when a suspension is pending, and the sampler suspends threads every millisecond,
  so the trace pins samples there (as ARCHITECTURE.md notes for dotnet-trace).
- **Prototypes:** `tools/PerfProbe/Proto.cs` (local), switched at run time, so one run alternates
  four off/on blocks of 150 steps. The first block runs each prototype beside the game's code and
  counts differences.

## Per system

- **Share:** of `UnsafeSim200ms` (step plus nav grids) in the perf run.
- **Windows ms:** that share applied to the measured Windows cycle (26.2 ms: step plus nav grids),
  so it is an estimate.
- **Objects:** from the probe's census and call counts.

| System | Share | Windows ms a cycle (est.) | Objects | What dominates (perf self time) | Status |
|---|---|---|---|---|---|
| Conveyor rails (`SolidConduitFlow.Sim200ms`) | 21.6% | 5.7 | 1,442 updaters; 84 bridge and 41 consumer hand-offs a cycle | Hand-offs: each item leaves a rail as a world item (partitioner entries, object layer, faller, `Stored` tag events) and goes straight back into storage. `ObjectLayerListItem.Refresh` 15% (each `Grid.Objects` write fires a partitioner event), `SaveManager.Unregister` 8%, partitioner lookups | Real work, except the `Unregister` scan (candidate 1) |
| Pipes (`ConduitFlow.Sim200ms`, both) | 16.8% | 4.4 | 2,857 liquid and 915 gas updaters, 1,058 and 295 networks | Its own loop and the main thread's share of the network jobs 13%, dispensers 9%, `UpdateConduit` 8%, consumers' `stored_mass` 7% and `Consume` 6%. `AddElement`/`RemoveElement` P/Invoke into SimDLL for every change | Real work |
| Power (`EnergySim.EnergySim200ms`) | 14.3% | 3.7 | 214 generators (103 fuel generators, 96 steam turbines), 97 batteries, 1,169 consumers | Its loop over 1,169 consumers (`GetCircuitID`, flags) 21%, fuel generators 13%, steam turbines 10%, `Generator.CheckConnectionStatus` 8%, `GetProperName` for reports 7%, storage events 6% | Real work. Consumer flag reads: candidate 3 |
| Plant absorbers (`PlantElementAbsorbers.Sim200ms`) | 13.0% | 3.4 | 315 absorbers; 570 consume plans a step (absorbers and the monitors' storage-change handlers) | Its loop with the plans inlined 37%, `IrrigationMonitor.UpdateIrrigation` 24% (attribute lookup by id string, tag scans of the planter's items), event dispatch 14% | Real work. Planner reuse measured at about 4%: dropped |
| Nav grids (`Pathfinding.UpdateNavGrids`) | 9.7% | 2.5 | dirty cells | `NavLinkStore.Commit` (our compact-nav-links) 20%, `Transition.IsValid` 19%, link creation, flying validator with `HasTag` | Real work. The store's commits wait for cold memory; they copy no tables (item 8, closed) |
| Circuits (`CircuitManager.Sim200msLast`) | 7.9% | 2.1 | 1,166 `SetConnectionStatus` calls a step | `SetConnectionStatus` 51%: an `Operational` flag dictionary read per consumer. The circuit loop and its sorts 34% | Real work. Candidate 3 |
| Spawned items (`Substance.SpawnResource`) | 4.4% | 1.2 | 4.2 a step | Object instantiation (engine clone, awake) | Real work |
| `ElementConsumer.AddMass` | 3.3% | 0.9 | 135 a step | `Storage.FindPrimaryElement` (a linear scan of storage items) 34%, storage events 26% | Real work, small |
| Other: callbacks, ore melt, disease, path-probe repair hook | 8.9% | 2.3 | | Callback releases, `DoOreMeltTransition`, `ConduitDiseaseManager`, `ProbeRepair.StepTheSimPost` (ours, 1.7%) | |
| Conduit temperature (native) | 0.2% | 0.05 | | SimDLL | Native |

**Events:**
- **Volume:** 1,321 `EventSystem.Trigger` calls a step.
- **Game-wide:** `Trigger` is 9.2% of the main thread inclusive, and 1.2% is dispatch itself.
  The rest is handlers: cell changes 39% of it, the plant absorbers 10%, tag removals 9.5%.
- **Inside the step:** dispatch is 5.6% of the step.

## Candidates, ranked

| # | Tweak | Exact? | Saving | Evidence | Risk | Branches |
|---|---|---|---|---|---|---|
| 1 | `SaveManager.Unregister`: remove a live root by reference scan; a destroyed root goes to the game's `List.Remove` | Yes. For a live root, `List.Remove`'s `Equals` is reference equality. It also reads every other root's liveness (cache misses), and a rail hand-off scans the whole list because `Register` has just appended the root at its end | Rails −8%, `Pickupable.OnStore` −19%: about 1.7% of the step, 0.4 ms a cycle on Windows (est.), and most of `Unregister`'s 0.26% of the main thread game-wide | **Measured**: four same-run off/on pairs, normalized by untouched systems: `OnStore` 0.78–0.85, rails 0.88–0.94. Verify: 3,462 calls, 0 differences | Low: one method; must keep `onRegister`'s null check before `onUnregister` (Klei's own quirk) | Both (U57 has the same code) |
| 2 | A faster exact `EventSystem.Trigger`: fewer copies of the `SBOVector4<Entry>` value and its entries while keeping the game's re-read when a handler unsubscribes | Yes, with care: handlers may subscribe and unsubscribe during dispatch, and the deferred removals must match | Estimated 30–50% of dispatch: 2–3% of the step, 0.4–0.6% of the main thread game-wide | perf: dispatch self 1.2% game-wide, 5.6% of the step | Medium: many mods patch `Trigger`, so it must stand down for foreign patches as other tweaks do | U60 (the event store changed in U60) |
| 3 | Power consumers: read the `Powered` flag without the `Operational` dictionary (a kept value moved by every write of that flag) | Possible: every write of the flag goes through `Operational.SetFlag`, but its dictionary is public, so a verify mode is needed | Up to half of `SetConnectionStatus` and the consumers' share of `EnergySim`: 3–4% of the step (estimated) | perf: `SetConnectionStatus` 51% of the circuits, consumers 21% of power | Medium: a design job, not a quick patch | Both |
| 4 | `IrrigationMonitor.UpdateIrrigation`: keep the `FertilizerUsageMod` attribute instance while the attribute table is unchanged | Yes (same instance) | At most 1–1.5% of the step (estimated) | perf: `UpdateIrrigation` 4.3% of the step | Low | Both |
| 5 | `Storage.FindPrimaryElement` | Same answer needed | Under 1% | perf 2.6% of the step, mostly item reads | Not worth it: P2 showed component lookups are not the cost | — |
| 6 | Plant absorbers' `Planner` reused per storage version | Yes (verify: 85,475 plans, 0 differences) | **Measured** about 4% of the absorbers, 0.4% of the step | Same A/B as 1 | Dropped: too small | — |
| 7 | `Singleton<T>.Instance` without its lock (load-time IL rewrite) | Yes | Under 0.27% of the main thread | perf | Dropped: too small | — |
| 8 | compact-nav-links' `NavLinkStore.Commit` | Ours | None found. A commit waits for the cell's head and its block, cold memory it has to touch once a cell; no table is copied | **Measured** (2026-11-14): with the waits moved out of `Commit`, it fell from 17.3% to 1.4% of `UpdateNavGrids`, and the store as a whole stayed at 21.8% and 21.1% (0.75 and 0.78 ms a game second on Linux) | Closed: nothing landed in the commit ([navgrid-links-store.md](navgrid-links-store.md) §7) | — |

Together, 1–4 come to roughly 6–10% of the step. Two of them are designs, not patches. On
Windows that is about 1.5–2.5 ms a cycle, or 0.5–0.8% fps (estimated).

For the 15 Hz stutter, candidate 1 shortens the heavy step by about 0.4 ms of its 11.6 ms. The
heavy step is rail and pipe work, which no exact tweak here removes.

## What was ruled out

- **Spreading the step's systems over sub-ticks, or running them alongside each other.** Not
  exact (frame-pacing-and-jank.md, option a).
- **Skipping the hand-off's round trip through the world.** When a bridge, consumer or
  dispenser moves an item, it fires tag, storage and partitioner events. It also adds and
  removes the item's faller and partitioner entries, whose handles and free lists other systems
  see later.
- **Skipping unchanged meter tints** (`SteamTurbine`'s `SetSymbolTint` every step): it also calls
  `SuspendUpdates(false)`, which changes the animation's update registration.
- **Status items.** `SetStatusItem` already returns early when the item is set.

## Next

1. **Windows measurements** (held until the machine is free): the same A/B with
   candidate 1 for Windows ms, then fps against the default build at speed 3.
2. **Build candidate 1 as a tweak**, with a verify mode and a foreign-patch stand-down, on both
   branches.
3. **Prototype candidates 2 and 3** with the same in-run A/B before deciding on them.

## Runs

Local, not in the repository: out/stepcost in the stepcost-u60 worktree (scenarios `sections.txt`,
`perf.txt`, `ab.txt`; runners `linux-run.py`, `win-run.sh`), the runs' CSVs, perf data and map.
