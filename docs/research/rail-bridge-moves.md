# Rail bridges: what the store round trip does, and moving an item without it (2026-11-14)

The question: a conveyor bridge that moves a whole item un-stores it and stores it again
([conduit-updates.md](conduit-updates.md): 2.84 ms a game second on the late-game colony, about
50 µs a move). Does that round trip leave anything behind that a plain move of the item from the
input rail tile to the output rail tile would not? If not, can a game tweak make the plain move?

**Verdict.** The round trip ends where it began, apart from eight things it leaves behind, all
cheap to make by hand, and four pieces of bookkeeping that no gameplay reads. `direct-rail-bridges`
(on by default since 2026-11-14, outside `exact`) makes the move directly for the items the audit covers and leaves the rest to the
game. Sections: the audit, what the tweak does, verification, cost.

## Method

Code reading of the game's decompiled code, U60 763023 (the build's own decompile, compared with
762107's for the audited classes) and U59 744825. No player runs for the audit. The verify mode's
counts and the cost are measured (below).

The game's code, `SolidConduitBridge.ConduitUpdate`'s whole-item branch:

```
Pickupable p = flowManager.RemovePickupable(inputCell);
if (p) { flowManager.AddPickupable(outputCell, p); dispensing = true; ...OnMassTransfer(...); }
```

- `RemovePickupable`: empties the cell, clears the item's conveyor-overlay look, triggers
  `OnStore` (856640610) with `false`, queues the handle to be freed at the next step.
- `AddPickupable`: allocates a handle, applies the overlay look when the Conveyor overlay is
  open, triggers `OnStore` with `true`, then `SetContents` (parent, position, scene layer).

## The audit

What runs in the round trip, for an item as the rails keep it (`Stored` and `StoredPrivate`
tags, no storage, stored-items partitioner entry, not selectable, not in the save list). "Same"
means the state after the re-store equals the state before the un-store.

| What runs | What it changes | After = before? | Skipping it is |
|---|---|---|---|
| `Pickupable.OnStore(false)` → `RemovedFromStorage` → `UpdateCachedCell(PosToCell(item))` | `cachedCell`, the stored-items partitioner entry's cell, the offset tracker's cell | **No**: both become the cell the item is drawn at (rail flow does not update them) | gameplay (reachability, sweepers and world inventory read `cachedCell`). **Kept**: the tweak calls `UpdateCachedCell` |
| `RemovedFromStorage`: `transform.rotation = identity`, anim controller enabled | rotation, `enabled` | Same for a rail item (set when it left its storage) | invisible. **Kept** (rotation set; an item with a disabled controller goes the game's way) |
| `KPrefabID.RemoveTag(Stored)`, `RemoveTag(StoredPrivate)`, then `AddTag` of both | tags; four `TagsChanged` events | Same | invisible, but for the listeners below |
| `TagsChanged` → `Pickupable.OnTagsChanged` → `UpdateListeners(true)` then `(false)` | item list of the cell (`ObjectLayerListItem`, a `Grid.Objects` write and its partitioner event), solid-changed and pickupables partitioner entries, the cell-change subscription; all removed again | Same; new partitioner handles | bookkeeping (handles). No callback sits on the Pickupables object layer (conduit-updates.md) |
| `UpdateListeners(true)`: `CellChangeMonitor.MarkDirty`, `ClearLastKnownCell` | the item's next cell is reported even if it equals the last known one | **No** (deferred to the next frame) | gameplay in principle: `SimTemperatureTransfer` moves the item's sim chunk on that report. **Kept**: both calls are made. (The output tile is never the last known cell, so the report comes either way) |
| `OnTagsChanged` → `AddFaller`, then `RemoveFaller` | a faller and, in mid-air, gravity; both removed in the same call, freed at the frame's clean-up | Same | bookkeeping (handles). A faller removed in the call is never stepped (`GravityComponents.FixedUpdate` skips it) |
| `SaveLoadRoot.SetRegistered(true)` then `(false)` | appended to and removed from the save list; overlays' register/unregister callbacks | Same (rail items are saved by `SolidConduitSerializer` in rail order, not from that list) | invisible. Save order: unaffected |
| `KSelectable.IsSelectable = true` then `false` | a flag | Same | invisible |
| `TagsChanged` → `FetchableMonitor`: a reachable item becomes fetchable, then unfetchable | `FetchManager.Add`/`Remove`; `Game` events `AddedFetchable`/`RemovedFetchable` → `WorldInventory` | Fetch manager: same (handles). World inventory: the item is added to and removed from its sets, but **sets are made** for tags that had none, and **a kind not in the world's inventory yet is discovered** (`DiscoveredResources.Discover`) | Discovery: **gameplay**. **Kept**: a kind that is not discovered (or not in its category) goes the game's way. Empty sets: bookkeeping (amounts read the same; the inventory refreshes one tag a frame, so more tags is a frame's lag for the others) |
| `Pickupable.OnStore(true)` → `NotifyChanged(cachedCell)` | pickupables-changed partitioner event: weight plates recount their cell; Duplicant sensors, ladder beds, flytraps, the critter manipulator look | **No** (an event) | gameplay (a weight plate's count is refreshed only by this event). **Kept**: the event fires |
| `OnStore(true)` on U60 762230 and later: an entombed item is released | entombed state | Same for a rail item (never entombed) | invisible; an entombed item goes the game's way |
| `Clearable.OnStore` → `CancelClearing` | sweep mark | Same (a stored item has none) | invisible; a marked item goes the game's way |
| `IncubationMonitor.Instance.OnStore` (eggs) | `isSuppressed` false then true: the egg passes through `incubating` (effects swapped, idle animation restarted, the hatch check made on entry) | **No**: the animation restarts, and an egg already at 100% starts hatching | gameplay. **Kept**: the tweak calls the monitor's handler with `false` and `true` |
| `Compostable.OnStore` | "marked for compost" status item removed and added | Same; the item's status list order | bookkeeping |
| `BionicUpgradeComponent.RefreshStatusItem` | "unassigned booster" status item and icon added, then removed | Same | bookkeeping (no frame is drawn in between) |
| `Electrobank.ClearHealthBar` | a damage bar | Same (a stored one has none) | invisible |
| `Light2D.OnStore` (carved lumen quartz) | `enabled = true` both times | Same | invisible |
| `Storable.OnStore` | the tags again | Same | invisible |
| `Baggable`, `Trappable`, `Navigator`, `IncubatorMonitor`, `DeathMonitor`, `DecompositionMonitor`, `BaggedStates`, `RailGunPayload`, `CargoLander`, `FallMonitor`, `RobotAi` | a critter is un-wrangled, a payload goes to its idle state, … | **No** | gameplay. Not on what a loader accepts; items with any of them go the game's way |
| `RemovePickupable`: `ClearOverlayVisualization`; `AddPickupable`: `ApplyOverlayVisualization` with the overlay open | layer and tint: a tinted item (a mutant seed) turns white at its first bridge; with the overlay open the item is taken out of and put back into its animation batch | **No** for a tinted item | visual. **Kept**: both calls are made as the game makes them |
| `freedHandles.Add`, `conveyorPickupables.Allocate` | the item's rail handle | **No**: a new handle | bookkeeping. The handle list's order is the order `OnOverlayChanged` recolours rail items in, which sets their order in the animation batches when the Conveyor overlay opens or closes: two overlapping rail items may draw in the other order |
| `SetContents` | parent, position, scene layer | (the move itself) | **Kept**: the game's method |

Checked and not involved: `SimTemperatureTransfer`, `PrimaryElement` (a rail item keeps its
own sim chunk, enabled, and exchanges heat with the cell the chunk is in; nothing in the round
trip registers, unregisters, disables or resets it); `Rottable` (its states follow the
`Preserved` and `Entombed` tags and read the cell and `pickupable.storage`, null on a rail);
`Sublimates`, `EntitySplitter`, `Durability`, `DecorProvider`, `Prioritizable`, disease (no
listener); `Movable` (only on items marked for relocation). No scheduler callback is queued.

U59 744825: the same code but for the cell-change handler's registration (the monitor's own
list instead of the object's events) and `OnStore(true)`'s entombed release, which it lacks.
The bridge's partial branch differs (U60 checks `Take`'s result); the tweak does not touch it.

## What the tweak does

`SolidConduitBridge.ConduitUpdate` is transpiled: the whole-item branch's `RemovePickupable`
and `AddPickupable` calls become the tweak's. The rest of the method, the partial move,
`desiredMassTransfer`, `OnMassTransfer` and `dispensing`, is the game's.

- **Remove**: empties the input cell and clears the overlay look, as the game does; the item is
  not told anything and keeps its handle.
- **Add**: `UpdateCachedCell`, the cell-change monitor's two calls, the rotation, the egg's
  handler (`false`), the overlay look when the overlay is open, `NotifyChanged`, the egg's
  handler (`true`), `SetContents` with the same handle.
- **The game's way instead** for: a kind not yet discovered; an item with a component or state
  machine from another assembly than the game's (a mod's); critters, Duplicants, items with
  `Storage`, `Health`, a navigator, payloads and landers; an item not in the rails' usual state
  (tags, storage, partitioner entries, entombed, marked for sweeping, animation off); an output
  cell that lost its rail or got an item (then the item is first un-stored, as the game's
  removal would have, and the game's add drops it).
- **Stands down** while another Harmony owner patches the bridge's update, the rails' add or
  remove, `Pickupable.OnStore` and the methods it reaches for a rail item, the egg's handler,
  the world inventory's or the fetch monitor's hooks.
- A game update that changes any of the ten method bodies the audit read turns the tweak off
  until they are reviewed (fingerprints).

## What differs with the tweak

1. An item keeps its rail handle. Seen only as the order two overlapping rail items are drawn
   in after the Conveyor overlay is opened or closed.
2. A reachable item is not listed as fetchable for the instant of the crossing: the world
   inventory does not make empty sets for tags that have no fetchable item yet.
3. Fetch-manager, partitioner, faller and status-item handles and list places differ.

Nothing else: what the rails carry and when, masses, temperatures and heat exchange, germs,
freshness, eggs, the meter valve's counts and the save file are the same.

## Mods

Scanned as in [tweak-step-aside-audit.md](tweak-step-aside-audit.md) (ModTriage's patch targets,
a raw name scan of every mod DLL, each hit read in the decompiled mod; 2,898 Workshop folders
and Fast Track).

| Method | Mods that patch it | The tweak |
|---|---|---|
| `SolidConduitBridge.ConduitUpdate` | Ronivan's Legacy (transpiler, with its rails option), Utility Layers (skipping prefix, postfix, finalizer) | stands down |
| `SolidConduitFlow.RemovePickupable`, `AddPickupable`, `SetContents` | Ronivan's Legacy, Utility Layers, Insulated Solid Conduit | stands down |
| `SolidConduitFlow.Sim200ms` | Utility Layers, Oxygen Multiplayer (transpilers), Multi Channel Utilities (postfix: its extra channels run the same bridges), Vision Not Included (prefix) | stands down |
| `WorldInventory.OnAddedFetchable` | Fast Track (skipping prefix, on by default), Oxygen Multiplayer | stands down |
| `DiscoveredResources.Discover` | One Giant Leap, ONI Bingo, Applied Logistics Network, ArchipelagoNotIncluded, Oxygen Multiplayer | stands down |
| `Pickupable.OnStore` and the four methods it reaches, `IncubationMonitor.Instance.OnStore`, `FetchableMonitor.Instance.RegisterFetchable` | none found | guarded all the same |
| `OnStore` event listeners on items in general | Forbid Items (a component on every ore and loose item), No Manual Delivery (a handler on every fetchable's state machine) | off while either is active (by static ID; Forbid Items' component would send every item the game's way anyway) |
| `OnStore` listeners on their own objects | Pickup Flopping Pacu, CorpseOnPedestal, Retro Edition, PotatoElectrobanks, SuperRover, FusionEnergyProject | their items carry a mod's component or state machine, so they cross the game's way |

`LimitValve.OnMassTransfer` (Smart Limit Valve, Transfer Statistics) and buildings that reuse
`SolidConduitBridge` (Longer Bridges, Extended Bridges, Dupes Logistics, Debris Thermostat) are
not affected: the bridge's own code calls them as before. Egg mods (Egg Cryopreservation,
Shinebug Reactor) patch `IncubationMonitor.UpdateIncubationState`, which the egg's handler
still calls.

## Verification (measured)

- **Verify mode, late-game colony** (U60 763023, Linux, speed 3, 150 s): 12,928 direct moves
  and 12,928 of the game's own round trips, each compared with what the audit says the round
  trip leaves. 0 wrong. No item was of a kind not eligible, undiscovered or in an unusual state.
  The round trips checked against the audit are the evidence that the audit reads the game
  right; the direct moves checked the same way, that the tweak does what the audit asks.
- **A conveyor loop, tweak off and on** (`tests/e2e/direct-rail-bridges.yaml`,
  `tools/RailLoopTestMod`): ten items (ores, a ration, meal lice, a hatch egg) round a closed
  loop through two bridges and a meter valve with a limit of 600. Both runs stall at 170 game
  seconds with the same 11 items (one ore split by the valve) on the same tiles with the same
  masses (54 kg), and the valve at 600. The items' temperatures differ run to run in the
  fourth decimal (below). With the tweak and verify: 173 direct moves, 172 round trips, 0 wrong.
- **Unit tests** (`RailHandOffTests`): the hand-off on a fake rail.

## Cost (measured)

Late-game colony, U60 763023, Linux under Xvfb, speed 3, default tweaks, one launch each,
stopwatches around `SolidConduitFlow.Sim200ms` and `SolidConduitBridge.ConduitUpdate` (about
0.5 µs a bracket, 422 bridge calls a step), 480 heavy steps after a warm-up:

| ms a game second | off | on | change |
|---|---|---|---|
| Rail bridges (422 calls, 55 moves) | 2.56 | 1.46 | −43% |
| The rails' step | 5.72 | 4.35 | −24% |
| Bridge time per moved item | 46.6 µs | 26.7 µs | |

The bridge time left includes the 367 idle calls and their brackets. No Windows run was made,
and no frame-rate claim: this is about 1.4 ms of each game second's heavy step.

## Where the remaining bridge time goes (measured)

One more launch with the tweak on, the same colony and brackets, plus stopwatches inside the
tweak (local, not landed) and around what the bridges call. A stopwatch pair costs 0.09 µs. Per
game second, 422 bridge calls:

| Part | Calls a game second | µs each | ms a game second |
|---|---|---|---|
| Idle bridge calls (the game's checks; includes the bracket) | 367 | 0.62 | 0.23 |
| Direct moves | 49 | 15.3 | 0.75 |
| Partial moves, the game's path: `Pickupable.Take` splits an item (a new object) | 5.9 | 91 | 0.54 |
| ... and their `AddPickupable` | 5.9 | 5.3 | 0.03 |
| `LimitValve.OnMassTransfer` (meter valves' counts and meters) | 27 | 7.5 | 0.20 |
| Moves left to the game's round trip (not eligible, undiscovered, unusual state) | 0 | | 0 |

A direct move's 15.3 µs:

| Step | µs |
|---|---|
| `Pickupable.UpdateCachedCell`: the stored-items partitioner entry, the offset table's two partitioner entries and its offsets for the new cell, instant-reachability's index | 8.8 |
| `SetContents` (parent, position, scene layer) | 1.9 |
| The egg's two notices (averaged over all moves; none for other items) | 1.5 |
| `NotifyChanged` (the pickupables-changed event) | 0.9 |
| Whether the item is in the rails' usual state | 0.7 |
| Cell-change monitor's two calls | 0.4 |
| Emptying the cell, the overlay look | 0.4 |
| Kind eligible and discovered (one dictionary lookup by prefab) | 0.3 |
| Rotation | 0.1 |

Nothing here was cut. Eligibility is already one lookup per prefab, the egg's handler runs only
for eggs, and the overlay look is applied only with the overlay open. What is left is the
filed cell:
- Plain rail flow never updates it. Between hand-offs a rail item's `cachedCell`, its
  partitioner entry and its offset table stay where the last hand-off left them, and only its
  transform (and through the cell-change monitor its sim chunk) follows the rail.
- A hand-off is different because the game's round trip files the item anew, and that state
  stays: sweepers scan the stored-items partitioner by it, the reachability check and
  instant-reachability's index read it.
- Leaving out only the offset table's part would not save its work: the next reachability
  check of the item computes the same table for the new cell.

So the floor of a direct move is about 15 µs, against a liquid packet's array write: a rail item
is a game object the rest of the game files by cell. The other 1 ms of the bridges' update is
the game's own: idle checks, meter valves' counting, and partial moves that create an object.

## The loop's temperatures, run to run

The loop spec's sum of the eleven items' temperatures at the stall, U60 763023:

| Run | Sum, K |
|---|---|
| off | 3,242.42 |
| off again | 3,242.42 |
| on (verify) | 3,242.36 |
| on again (verify) | 3,242.42 |
| on, U59 | 3,242.42 |

Per item, for "off again" and "on again" (the test mod logs them since then): nine of the
eleven are equal to the last digit; the 4 kg sandstone reads 294.86008 K off and 294.85992 K
on, the 6 kg sandstone 294.75922 K and 294.75925 K. The two on runs differ from each other by
more (0.06 K in the sum) than on differs from off, so the differences are run to run; the first
off run's per-item values were not logged, so whether two off runs agree to the last digit is
not known. An item's sim chunk follows its transform's cell at the next rendered frame, in
both paths, so the heat exchanged depends on frame timing.

## Partial moves (measured and code reading, 2026-11-14)

A partial move is a meter valve passing less than the stack in its input tile: the bridge calls
`Pickupable.Take(amount)`, which makes a new item of that amount, and adds the new item to the
output tile; the rest stays in the input tile. Only `LimitValve` sets a bridge's
`desiredMassTransfer` in the game (the conveyor meter; the gas and liquid meters use the pipes'
bridges).

**On the late-game colony** (one launch, 540 game seconds, per-valve counts): 13 conveyor meters
are asked, 10 of them make partial moves, 5.9 a game second in all. They are dosing valves: a
limit far below the stack, reset by automation about every second.

| Valve (limit) | Partial moves of 540 game seconds | Asked, kg | Of a stack of, kg | Item |
|---|---|---|---|---|
| 0.04 | 539 | 0.040 | 10.3 | diamond |
| 0.06 | 538 | 0.060 | 11.1 | diamond |
| 2 | 471 | 1.97 | 11.2 | igneous rock |
| 1 | 356 | 0.99 | 10.6 | aluminium |
| 4 | 323 | 4.00 | 14.0 | sulfur |
| 1 | 279 | 0.97 | 10.7 | tungsten |
| 1 | 268 | 0.98 | 10.7 | tungsten |
| 1 | 231 | 0.99 | 10.0 | tungsten |
| 1 | 171 | 0.97 | 9.7 | iron |
| 0.5 | 26 | 0.49 | 1.7 | omelette |

**Where the time goes.** `Take` is 72 µs a call here (91 µs in the earlier launch), and the
piece's `AddPickupable` 3.9 µs. All but 26 of the 3,202 splits are ore: `ElementSplitterComponents.OnTake`
→ `Substance.SpawnResource` → `GameUtil.KInstantiate` of the element's prefab, its components'
`Awake`/`OnPrefabInit` on activation, mass and temperature, the split event, the anim's overlay
colour. The 26 food splits (`EntitySplitter.Split`, bracketed by step) cost 173 µs each: the
instantiate 38 µs, `GetComponent` and `SetActive` (every component's `Awake` and `OnPrefabInit`)
125 µs, three `TotalAmount` sets 1.2 µs, the temperature 0.1 µs, the rest (the split event,
`GetComponents<IExtendSplitting>`) 9 µs. The piece's `Pickupable.OnSpawn` runs in the next
frame, outside the bridge's time: 10.8 µs. The ore path's steps were not bracketed one by one;
by its total it is the same shape with fewer components.

**No round trip to remove.** The piece is stored on the rail in the call that makes it, before
it has spawned: its listeners for the Stored tags are not subscribed yet and its save root is
not registered, so `AddPickupable`'s store is a tag write, and at its spawn it registers
directly as a stored item (the path a save's rail items take when loading). Nothing is set up
as a loose item and taken down again.

**What could be cheaper.**

| Option | Saves (estimate) | Changes |
|---|---|---|
| The engine's `Instantiate` and component activation, if a profile finds slack there | part of ~65 µs a split: up to ~0.3 ms a game second here, and every other object the game creates | nothing (exact) |
| A pool of pieces, or reusing objects | most of a split | object identity: `KPrefabID.InstanceID`, the components' and state machines' fresh state, anything a mod attached; not provably invisible |
| Passing the stack and leaving the new object behind | nothing: one new object either way | which of the two is new |

No game-side change is proposed. The lead is the engine's cost of creating an object, which
needs its own profile (instantiate against component `Awake`, ours against the game's code).
