# Gluttony frame inventory (2026-10-22)

What Gluttony's frame is made of, system by system, why Moono gains about 3× on it where
U57 gains 7×, and what could still be made faster. Gluttony: Spaced Out, cycle 15,304, 1160×194
cluster; the cleaned save (the 17,552 objects stuck falling below two rocket interiors
destroyed, see [PERFORMANCE_CASE_STUDY_3.md](../PERFORMANCE_CASE_STUDY_3.md)), every tweak.

## Method

As for [u57-frame-inventory.md](u57-frame-inventory.md): `perf` on Linux (cpu-clock, 499 Hz,
every thread, JIT symbols through perf maps), 60 s of steady play at speed 3 from the third
600-frame window, every tweak including the radbolt collider cache (commit b76eaaa). The run
held 24.4 fps (40.9 ms a frame); the game's code runs slower on Linux than on Windows (about
32 ms a frame there) but keeps its proportions. All times below are Linux main-thread ms a
frame. Instance counts are PerfProbe's `counts.txt` at colony load (all objects, active or
not). A second, 90 s profile ran while other players were busy on the machine (11–17 fps) and
gave only the counts.

Scripts (not in the repo): `out/glut-inv/` (`load.py`, `kids.py`: frames under a node, `sys.py`:
the first specific system under a dispatcher, generic state-machine plumbing skipped).

## The colony in numbers

| | Gluttony | U57 |
|---|---|---|
| Duplicants | 70 | 24 |
| Critter brains | 1,021 | 1,186 |
| Navigators | 1,087 | |
| Buildings (complete) | 74,026 | ~62,000 |
| Workables | 558,185 | 441,253 |
| Pickupables | 47,337 | |
| Animation controllers | 122,293 | ~90,000 |
| Colliders | 124,173 | 93,840 |
| `RequireInputs` buildings | 2,402 | 1,918 |
| Workers (`StandardWorker`, Duplicants and remote workers) | 397 | |
| Radbolts in flight | 74–94 | 4 |
| Sublimating items | 1,766 | 2,457 |
| Solid transfer arms | 325 | |
| Storages | 6,128 | |
| Wires, pipes, rails | 8,779, 15,877, 4,975 | |

## The frame

40.9 ms. The main thread is the limit: busy 88% of the time sampled. The native sim (SimDLL) is
busy 41%, the GL worker 41%, the job workers a few percent each.

| Owner of main-thread time | Share | ms/frame |
|---|---|---|
| Game (`Assembly-CSharp`, `-firstpass`) | 62% | 25.3 |
| This project's engine (`OniMoono.UnityEngine`, `.Desktop`) | 19% | 7.6 |
| CoreCLR's library (`System.Private.CoreLib`: collections, casts, copies) | 9% | 3.5 |
| Game tweaks, Harmony stubs | 5% | 2.0 |
| uGUI, TextMeshPro (this project's) | 3% | 1.1 |
| CoreCLR stubs and native | 2% | 1.0 |

(Native leaves are counted with the managed code that called them.)

By player-loop entry: `Game.Update` 19.7 ms, rendering 5.7 ms, `Game.LateUpdate` 3.4 ms,
`World.LateUpdate` 2.6 ms, `Global.LateUpdate` 1.9 ms, canvas update and build 1.6 ms,
`Global.Update` 0.7 ms, input module 0.5 ms, `WorldInventory.Update` 0.5 ms,
`PropertyTextures.LateUpdate` 0.5 ms.

`Game.Update`'s 19.7 ms: state-machine sim sub-ticks 13.0, `Game.UnsafeSim200ms` 4.8 (of which
3.6 is the managed Sim200ms work run from `StepTheSim`, not waiting), `CellChangeMonitor` 1.4.

## Why the gain is smaller than U57's

U57's frame had a handful of large systems the tweaks could cut exactly or by timing:
reachability 2.5 ms, sublimation 2.4, animation events 2.5, conveyor items 2.9, radbolt
collisions 0.9. Each had tens of thousands of objects doing the same cheap thing.

Gluttony's sim sub-ticks are 13 ms spread over 386 distinct systems (update callbacks of state
machines and components):

- The largest is 1.3 ms.
- Only 13 are above 0.2 ms, and together those 13 make 6 ms.
- The other 7 ms is 370 systems below 0.2 ms each: critter and Duplicant monitors, machines'
  state checks, ranching, rot, breathing, exposure.

That is ordinary game logic, which CoreCLR runs faster than Mono but which offers no single
place to skip work.

## System by system

Columns as in the U57 inventory. Safety classes:
- **A:** the same results by construction;
- **B:** the same results, different timing or order of work;
- **C:** changes behaviour.

### Simulation sub-ticks (13.0 ms)

| System | ms | Count | Mechanism | Candidate | Class |
|---|---|---|---|---|---|
| `HighEnergyParticle.CheckCollision` | 1.27 | 74–94 radbolts | `fast-particle-collision` already skips 43% of the 3×3 collider gathers; the rest find another radbolt registered nearby (radbolts travel in streams), so the full gather runs (0.52, every collider of the stored items in 9 cells), then `ParticleOf` scans the live radbolts for each gathered collider (0.21) | see candidate 1 | A |
| `RequireInputs.CheckRequirements` | 0.83 | 2,402 buildings | per building per 200 ms: power and conduit connection reads (`ConduitConsumer.IsConnected` 0.18: a `Grid.Objects` read per call), six status-item toggles. 70% is the method's own reads of scattered components | an exact skip still reads the inputs (the sublimation lesson) | — |
| `ReachabilityMonitor` | 0.62 | workables | already `slow-reachability`; own reads | — | — |
| Solid transfer arms | 0.52 | 325 | batch update and finish | — | — |
| `FastSublimation.Batch` | 0.46 | 1,766 items | already the tweak's batched skip | — | — |
| `RanchStation` find-ranchable update | 0.42 | ranch stations × critters in the room | per station per tick, every critter in its cavity: `GetSMI<RanchableMonitor.Instance>()`, which walks the critter's ~20 state machines calling `GetType()`/`IsAssignableFrom` (0.20), then eligibility checks | see candidate 4 | A |
| Workers' work ticks (`StandardWorker.Work`) | 0.40 | 397 workers | work completion (0.15), experience, stamina checks | — | — |
| `CritterTemperatureMonitor` | 0.38 | 1,021 critters | target state per critter per tick; `IsEntirelyInVaccum` loops occupied cells | — | — |
| `ScenePartitioner.Sim1000ms` | 0.36 | dirty partitioner nodes | each node that lost an entry since the last second has every entry re-checked with `Lookup` to drop freed handles: the cost is the node's entry count, not the number freed | see candidate 6 | A |
| `Navigator.AdvancePath` (transition driver) | 0.33 | 1,087 navigators | moving along paths | — | — |
| `Rottable` | 0.30 | 1,044 edibles | `RefreshModifiers` per item | — | — |
| Amounts (`AmountInstance.BatchUpdate`) | 0.30 | Duplicants, critters | stress, calories, breath deltas | — | — |
| `GasLiquidExposureMonitor` | 0.26 | 70 Duplicants | `SuitEquipper.IsWearingAirtightSuit` (0.20): `GetComponent<MinionIdentity>`, every equipment slot, `GetComponent<KPrefabID>().HasTag` per equipped item, per Duplicant per tick | see candidate 5 | A |
| `StructureTemperatureComponents.Sim200ms` | 0.24 | buildings | real work | — | — |
| `Dreamer.UpdateDream` | 0.21 | dreaming Duplicants | every sim tick: `NameDisplayScreen.GetEntry`, a linear search of every name-display entry (one per Duplicant and critter) comparing GameObjects | see candidate 3 | A |
| 370 other systems | ~7 | | none above 0.2 ms | — | — |

### Sim200ms run from `StepTheSim` (3.6 ms) and the rest of `UnsafeSim200ms`

| System | ms | Mechanism |
|---|---|---|
| `ConduitFlow.Sim200ms` (liquid, gas) | 0.82 | consumers, dispensers, bridges |
| `NavGrid.UpdateGraph` | 0.87 | links and valid cells rebuilt for changed cells |
| `PlantElementAbsorbers.Sim200ms` | 0.62 | mostly irrigation events through `EventSystem.Trigger` |
| `EnergySim.EnergySim200ms` | 0.57 | generators, batteries |
| `SolidConduitFlow.Sim200ms` | 0.48 | rail items, bridges |
| `CircuitManager.Sim200msLast` | 0.31 | circuits |
| Element chunks spawned and split | ~0.3 | `Substance.SpawnResource`, `ElementSplitterComponents.OnTake` (suit lockers' oxygen, deliveries): each one instantiates a GameObject |

### Everything else in the game's frame

| System | ms | Count | Mechanism | Candidate |
|---|---|---|---|---|
| Brains (`BrainScheduler.RenderEveryTick`) | 2.40 | 70 Duplicants, 1,021 critters | Duplicants: fetch updates 1.12 (the job system, `fast-offset-tables` on), chore search 0.63, state changes 0.29 | — |
| `KAnimBatch.UpdateDirty` | 1.90 | dirty animation batches | per-instance data written into batch textures | — |
| `KBatchedAnimController.UpdateAnim` | 1.59 | 122,293 controllers (those playing) | per playing animation per frame | — |
| `CellChangeMonitor.RenderEveryTick` | 1.42 | moving objects | cell-change handlers (1.00: object layers, partitioner) | — |
| `Global.Update` (animation events) | 0.72 | | `fast-anim-events` on | — |
| `WorldInventory.Update` | 0.53 | one per world | every frame, one tag's whole pickupable set summed, reading each item's world from its transform | per frame, not per game time: C only |
| `PropertyTextures.LateUpdate` | 0.50 | 1160×194 textures | `LoadRawTextureData` copies (0.30) | engine: avoid the copy when unchanged |
| `FallingWater` | 0.39 | | particles and mesh | — |
| `InterfaceTool`, `Scheduler`, `LineLayer`, trackers | ~0.9 | | | — |

### What the engine costs (main thread)

| Entry | ms |
|---|---|
| Rendering (`FrameRenderer.RenderFrame`) | 5.73 |
| · draw recording (`GlDevice.Record`, ~1,800 draws a frame) | 3.65 |
| · · uniform values (`GlUniforms.RecordAll`, `ValuesOf`; `Dictionary<int, Vector4>` lookups 0.39) | 1.37 |
| · · texture preparation | 0.68 |
| · canvas overlays | 1.02 |
| Canvas update and build | 1.56 |
| Input module (UI raycasts every frame) | 0.54 |
| Instantiate, activation, destroy (the game's element chunks, FX) | ~1.0 |
| Behaviour dispatch (`PhaseList.Run`, `Integrate`) | ~0.5 |

## Candidates, ranked

Estimated gains are Linux main-thread ms of a 40.9 ms frame; on Windows each is roughly 0.8× as
much.

1. **Radbolt gathers decided from the live radbolts (A, ~0.7 ms).** `CheckCollision` merges with
   the first gathered radbolt that is collideable and that it and the other intersect both ways.
   `AnotherParticleIn` already knows which other radbolts are registered in the 3×3 area. Test
   those directly:
   - If none passes the merge test, the gather cannot change anything and is skipped (now it
     runs whenever one is merely nearby).
   - If exactly one passes, the outcome is that one, whatever the gather's order.
   - Only with two or more does the real gather decide the order.

   Also make `ParticleOf` a dictionary lookup by GameObject instead of a scan of the live list.
   Removes most of the gather (0.52) and the scans (0.21).
2. **Draw recording's uniform lookups (A, engine, ~0.3–0.5 ms).** `GlUniforms.Sets` and
   `ResolveProperty` look each uniform up in the property sheet's `Dictionary<int, Vector4>` and
   `Dictionary<int, float>` per draw (0.48 together). An array slot per property id, assigned
   when a program's uniforms are bound, would make them indexed reads. The U57 per-block cache
   gave nothing, so measure before keeping it.
3. **Dream bubbles' entry search (A, ~0.2 ms).** `Dreamer.UpdateDream` calls
   `NameDisplayScreen.DreamTick` every sim tick for every dreaming Duplicant, and `GetEntry` scans
   every name-display entry. An index from GameObject to the first matching entry, kept as
   entries are added and removed, gives the same entry.
4. **`GetSMI<T>` by type (A, ~0.2 ms).** `StateMachineController.GetSMI(Type)` walks the
   object's state machines with `IsAssignableFrom(GetType())`; ranch stations call it for every
   critter in their room each tick. A per-controller cache of type to instance, cleared when its
   state machine list changes, returns the same instance.
5. **Airtight-suit check (A, ~0.15 ms).** `IsWearingAirtightSuit` walks equipment and component
   lookups per Duplicant per tick; cache the result per Duplicant, invalidated on equip and
   unequip and on the equipped item's tag changes. More bookkeeping than gain; low priority.
6. **Partitioner node cleanup by freed handles (A, ~0.3 ms here, more on the original save).**
   `ScenePartitioner.Sim1000ms` re-checks every entry of every node that lost one. Recording
   which handles were freed per node lets it remove exactly those (the same entries, in the
   same descending order) without the scan. On the original save, with 10,000 items in two
   nodes, this was 1.6 ms a frame on Windows.
7. **Property texture uploads (A, engine, ~0.2 ms).** `PropertyTextures` reloads its
   1160×194 textures through `LoadRawTextureData`; skip the copy (and the upload) when the
   bytes are unchanged, as the animation batch textures already do with partial uploads.
8. **`RequireInputs` (A, ≤0.2 ms).** The status-item toggles and `Db.Get()` reads are cheap;
   the cost is reading 2,402 buildings' scattered components. An exact skip still has to read
   them; probably not worth it.

Not candidates: brains, animation updates and batch writes, cell-change handling, conduit and
energy sims, navigation: real work per object, as in U57.

## The original save's extra costs

With the 17,552 stuck objects still in the save and `off-world-fallers` landing them, the
frame is about 9 ms longer (Windows). Per wall second on Linux, against the cleaned save:
`ScenePartitioner.Sim1000ms` +19 ms (nodes of 10,000 entries rescanned whenever one changes),
`ReachabilityMonitor` +15 ms (the items' reachability), object-layer list refreshes and
`ObjectLayerIndexer.set_Item` +15 ms (10,000 items in one cell's list), fetch and pickup
bookkeeping +10 ms. Candidate 6 addresses the first.

## Outcome (2026-10-22)

- 1, radbolt gathers: done. `fast-particle-collision` gathers only when a nearby radbolt passes
  the merge test; 56% of checks gathered before, 0.09% after. Radbolt checks 1.5 → 0.9 ms.
  `ParticleOf` was left as a scan: it runs only on the rare gathers now.
- 6, partitioner cleanup: done as `fast-partitioner-cleanup` (exact). 0.38 → 0.16 ms.
- 3, dream bubbles: dropped. `NameDisplayScreen.entries` is a public list the game changes in
  several places (adds, `RemoveAll`, an in-place compaction), and `GetEntry` compares with
  Unity's destroyed-object equality; an index kept exact through all of that is too fragile for
  0.2 ms.
- 4, `GetSMI`: dropped; 0.07 ms on the main thread in the current profile.
- 2, uniform lookups: not tried; the similar per-block cache gave nothing on U57, and the cost is
  spread over every draw's uniforms.
- 7, property textures: dropped; the cost is `LoadRawTextureData`'s copy, and telling whether
  the bytes changed costs a comparison of the same size.
- 5 and 8: not tried (bookkeeping heavier than the gain).
