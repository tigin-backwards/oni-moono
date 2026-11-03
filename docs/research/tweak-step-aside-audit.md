# Tweak step-aside audit (2026-11-02)

Which game methods each game tweak replaces, calls less often, copies or wraps, whether it
steps aside when another Harmony owner patches them, and which real mods do. An audit only: no
tweak changed. Branches: origin/master (U59, 5f945509) and origin/u60 (U60, 2c2c6a7e).

## Sources and limits

- **Tweaks:** `src/OniMoono.GameTweaks/*.cs` and `src/OniMoono.Host/GameTweaks/*.cs` on both
  branches. They are the same on both but for: `fast-overlays`, `fast-overlay-textures` and
  `CallSiteReroute` (master only); `fix-disease-overlay-crash` (`DestroyedCellChangeObjects`,
  u60 only); `fast-anim-batches`' write path (U60's `GetCustomShaderParams` for
  `GetPostProcessingParams`, and missing methods fail the install); `fast-serialization`'s
  member checks at install (u60).
- **Game code:** onimaxxing's decompiles `decomp/u59-csharp*` and `decomp/u60-csharp*`. The
  methods the tweaks touch differ between U59 and U60 only in decompiler noise, except where
  noted.
- **Mods:** the 2,897 Workshop folders in `out/ws` (2,717 with code). `tools/ModTriage` was run
  over all of them (not only the ranked 750) and its per-mod patch targets indexed by
  `Type::Method`; then a raw scan of every DLL for the methods' names (UTF-8 and UTF-16) found
  patches made through lookups ModTriage cannot follow (1,556 `Patch` calls in the corpus have
  no target it can read). Every hit below was read in the decompiled mod (ilspycmd). Fast Track
  (GitHub, not in `out/ws`): 0.18.5 (`out/modsrc/fasttrack`) and the approved 0.18.6
  (`out/modsrc/fasttrack-0.18.6`), decompiled; both have the same `[HarmonyPatch]` set.
- **Ranks** are MOD_COMPATIBILITY.md's. "None found" means none in the 2,717 mods with code and
  Fast Track. Seven small mods (MoreTek, MaxLevelUp, Fast climb fire pole, Water Sieve cleans
  germs, Slim Transit Tube Access, 放电器增强, Atomic Bank Tweaks) contain every game member name
  scanned for (an embedded copy of the game's assembly or its metadata); none patches these
  methods, and they are left out below.
- Runtime-only patches (targets built from strings at run time and not found by either scan)
  can still exist; the raw scan makes that unlikely for the distinctive names.

## How Harmony runs a tweak next to a mod

Three facts decide most of what follows (the game's 0Harmony 2.4.2, `MethodCreator.AddPrefixes`,
`PatchSorter`):

1. **Order.** Patches of one kind run by priority, highest first, then in the order they were
   added. The tweaks install before any mod loads, so at the default priority (400) a tweak's
   prefix runs before every mod's, and a tweak's transpiler sees the game's IL first.
2. **A prefix that returns false skips more than the body.** It skips the original, every
   transpiler's changes, and every later prefix that returns `bool` or writes a `ref` argument
   or `__result` (Harmony's `AffectsOriginal`). Later prefixes that return nothing and only read
   still run, as do all postfixes and finalizers.
3. **So a tweak prefix at the default priority that returns false hides later mods' gating
   prefixes,** and a mod's read-only prefix runs only after the tweak has done the work. The
   tweaks that already avoid this put their prefix at `Priority.Last` and check
   `__runOriginal` (`fast-conveyor-paths`, `fast-symbol-overrides`' application, the save
   compression prefixes, `fast-overlays`' power and tile prefixes, `fast-anim-batches`).

Inlining is not a separate risk for tweaks built against the game's types: the host's
`PatchDependents`/`PatchInliners` (ARCHITECTURE D21, D34) rebuild a tweak's Harmony replacement
when a method it calls or inlined is patched later. The host's own assembly is excluded from
those rebuilds, so the host-side tweaks (`fast-serialization`, `fast-particle-collision`,
`fast-save-compression`, `background-save`, `no-save-gc`) would keep an inlined copy of a small
game method patched after their first call. None of their callees is patched late by any mod
found.

## Ranked: needs a guard

Real mod conflicts first, then default-on tweaks without a known patcher, then opt-in tweaks.
"Default on" is every tweak outside the Changes gameplay group.

### Real conflicts

| # | Tweak (default) | What goes wrong | Mods | Guard to add |
|---|---|---|---|---|
| 1 | `fast-serialization` (on) | Its `SerializationTemplate.SerializeData` prefix (priority 400, returns false) writes the object before the mod's read-only prefix runs. Festive Halloween Spices hides its modded spices from an `Edible` in that prefix and restores them in a postfix, so a save stays loadable without the mod; with the tweak the modded spices are written into the save. Better Save's own bool prefix on the same method (its compiled-delegate serializer and first-save cross-check) never runs. | Festive Halloween Spices (2877478087, rank 1321); 更好的存档 BetterSave (3805601277, rank 1127). Fast Save's `Helper.WriteValue` prefix (1867707267, rank 37, option *Use Delegates*, off by default) comes with its own `Serializer` prefixes, which bypass `SerializeData` altogether. | `Priority.Last` and `__runOriginal`: fixes both known cases (the spices are hidden before the write; Better Save's false return skips the tweak). Also step aside, rechecked at each save, when another owner transpiles `SerializeData` or patches `Helper.WriteValue` (the plain-field path skips it). |
| 2 | `fast-anim-events` (on) | It copies `AnimEventManager.PlayEvents` and `StopEvents` into its loop. Fast Track's *Disable sound* replaces both with `return false`; with the tweak every animation event's `Play`/`Stop` runs again (the sound itself stays off through Fast Track's `SoundEvent.PlaySound` patch, but the mouth-flap, voice and counted events' other work and their cost come back). | Fast Track 0.18.5/0.18.6 `TurnOffSoundsPatch` (option off by default). | Per path, rechecked: while another owner patches `PlayEvents` or `StopEvents`, call the game's methods (delegates) instead of the inline loops; while one patches the three-argument `Update`, `GetFrameIdx` or `GetPlaySpeed`, let the game's `Update` run. |
| 3 | `fast-sublimation` (on) | Items it skips never reach `Sublimates.Sim200ms`, so mods' prefixes on it run for fewer items than in the original. All four known patches are gating prefixes (return false to stop sublimation), and a skipped item would not have changed either way, so no effect was found beyond the skip's own heat-delta copy, which the mod's false return would have prevented. | Stop Sublimation While In Storage (2686344355, rank 501); No sublimation (3518862481, rank 858); Controlled Sublimation (3636578165, rank 1953, scales `dt` by `ref`); Insulated Solid Conduit (3797381549, rank 2768). | Per path, rechecked every few hundred batches: while another owner patches `Sim200ms` (or `RefreshStatusItem`, `SimTemperatureTransfer.OnGetTemperature`, `PrimaryElement.get_Temperature`, whose work the skip stands in for), the batch function runs every item's `Sim200ms`. |
| 4 | `fast-jobs` (on) | It replaces `JobManager.Run` and runs the items on threads of its own, a copy of `DoNextWorkItem` and the worker loop. Debug Console's void prefix on `Run` still runs, but the `WorkerThread.Run` replacement it installs from there (worker exceptions printed to its console) serves only the first job; 系统性能观察器's transpilers on `Run` and `DoNextWorkItem` (its job timings) never run. | Debug Console (2041219184, rank 283); 系统性能观察器 OniPerformanceObserver (3800923048, rank 2568). | Global, rechecked (Debug Console patches at the first `Run`, after the tweak's first check could run): while another owner transpiles `Run` or patches `DoNextWorkItem` or `WorkerThread.Run`, return true. A read-only prefix or postfix on `Run` alone needs no step-aside. |
| 5 | `fast-overlays` (on, master) | The power path asks `CircuitManager.GetWattsNeededWhenActive` once per circuit per update. That method is guarded, but the consumers' `WattsNeededWhenActive` getters it sums are not: their patches run once per circuit instead of once per building. The known ones return the same value within an update, so the tints are the same. | Smarter Conditioner (3446454257, rank 688, tested); Wireless Power (2356326908, rank 272, broken upstream); Conduit Teleport (2253227918, rank 711, fails to load); Project Expedition (3803418607, rank 1936); 调谐仪 (3519403656, rank 1216). | Add `EnergyConsumer.get_WattsNeededWhenActive` and the other `IEnergyConsumer` implementations' getters to `powerPatched` (per path: only the power cache is lost). |

The guarded tweaks do step aside for the mods that patch their methods, as designed:
`fast-anim-batches` for Fast Track's *animation* optimisations (on by default: its prefix
replaces `KAnimBatch.UpdateDirty`), `fast-overlays`' power cache for Colony Performance Fixes
(3823893994), Controlled Power (3673526385), Utility Layers (3789151799) and ONI Multiplayer
(3807965885), `fast-overlay-textures` for Stock Bug Fix (1967921388, rank 369, which patches
`CreateRenderTarget` itself).

### Default on, no patcher found

| # | Tweak | Gap | Guard to add |
|---|---|---|---|
| 6 | `fast-symbol-overrides` | Part 1 (the setters skipped when they would store what is there) is unguarded: the `KeptCalls` check gates only parts 2 and 3. Both `SetSymbolOverrideInfo` prefixes run at priority 400 and return false, so a foreign transpiler of a setter, or a later gating prefix, is skipped on every unchanged write. | `Priority.Last` and `__runOriginal` on both, and per method, rechecked: while another owner patches a setter, it runs. |
| 7 | `fast-overlays` (master) | `TileMode.TryAddObject`'s prefix (it returns false for a tile already shown) is checked only against patches of `AddTargetIfVisible`, not of `TryAddObject`. The infrared check covers `OnTemperatureOverlayInfraredUpdate` but not `Infrared.Update`, which raises the event the tweak's handler counts places in (Colony Performance Fixes replaces `Infrared.Update`; the places are compared by reference, so it is safe, only wasted). | Add `TryAddObject` to the targets' list, and `Infrared.Update` to `infraredPatched`. |
| 8 | `fast-anim-cleanup` | Replaces `KBatchedAnimUpdater.CleanUp` (copying `DoGridProcessing`) with no check; its record of destroyed controllers comes only from `KBatchedAnimController.OnDestroy`. | Global, rechecked: while another owner patches `CleanUp` or `DoGridProcessing`, return true. |
| 9 | `fast-offset-tables` | `Workable.GetOffsets`/`ValidateOffsets` and `OffsetTracker.GetOffsets` run only for marked items, and `FetchablesByPrefabId.UpdateOffsetTables` (the main thread's share of the job) never runs. Fast Track's `PickupOpts` (on by default) patches `OffsetTracker.GetOffsets` but runs its own offset pass over every item, so its patch sees every item anyway. | Per path, rechecked: while another owner patches any of the four (or `OffsetTableTracker.UpdateOffsets`), `FixOffsets` runs the game's job. |
| 10 | `fast-partitioner-cleanup` | `GameScenePartitioner.Lookup` runs only for removed handles; it relies on every handle being freed through `ScenePartitioner.Remove`. | Global, rechecked: `Sim1000ms` (other transpilers), `Remove` (a gating prefix makes the record wrong), `GameScenePartitioner.Free`. |
| 11 | `fast-save-compression` | `DecompressContents`' prefix is at priority 400 (the compress one is at `Last`); a later gating prefix would be skipped. | `Priority.Last` and `__runOriginal`, as `Compress`. |
| 12 | `background-save` | Not a patch conflict but stale state: until the background write ends the save on disk is only its header. Mods that read a save themselves are not waited for: Mod Profile Manager (2854869130, rank 156) decompresses a save's body to read its mod list. Fast Save (1867707267, rank 37, *Background save* on by default) compresses its autosave on its own thread through `CompressContents`, so its autosave is handed on to a second background write and reports done before the file is. | Untested rather than unguarded: an e2e autosave with Fast Save, and with Mod Profile Manager reading the list right after a save. |
| 13 | `no-save-gc` | Removes `GC.Collect()` from `SaveLoader.Save`; Better Save's `GC.Collect` prefix and its *collect after save* option never see that call. | While another owner patches `GC.Collect`, leave the call in. |
| 14 | `fast-particle-collision`, `fast-anim-batches`, `fast-offset-tables`, `fast-partitioner-cleanup`, `off-world-fallers` | Call-site transpilers at the default priority: a mod's transpiler looking for the same call would find it gone (issue #41's cause). None known today: the transpilers on the same methods (RadBolt Fission and Rephysicalized on `CheckCollision`, Fast Track on `GravityComponents.FixedUpdate`) look for other instructions. | `CallSiteReroute.TranspilerPriority` (move `CallSiteReroute` to u60 too). |

### Opt-in (changes gameplay)

| # | Tweak | What goes wrong | Mods | Guard to add |
|---|---|---|---|---|
| 15 | `brain-rate` | Its `BrainScheduler.RenderEveryTick` prefix (400, returns false) skips the later gating prefixes: Fast Track's (priority 200, `PickupOpts`, on by default), which runs the brain groups through its asynchronous updater, never runs; neither does Cooperative Colonies' lockstep gate. | Fast Track 0.18.5/0.18.6; Cooperative Colonies (3824584976, rank 2591); CycleTrim (3807166689, rank 2716) caps creature brains in `BrainGroup.RenderEveryTick` too. | Global, rechecked: while another owner patches `RenderEveryTick`, return true. Fewer `Brain.UpdateBrain` calls (Multiplayer Mod 3728274068, Cooperative Colonies) is what the tweak is for. |
| 16 | `off-screen-conveyors` | Same shape on `SolidConduitFlow.RenderEveryTick`: Cooperative Colonies' lockstep gate is skipped. Postfixes (Controlled Visuals 3672588272, Multi Channel Utilities 3821447025) still run. | Cooperative Colonies. | Global, rechecked: a foreign prefix that returns bool, or a transpiler, on `RenderEveryTick`: return true. |
| 17 | `slow-reachability` | Fast Track's *FastReachability* (on by default) replaces `ReachabilityMonitor.InitializeStates`, so the tweak's transpiler never runs: no effect, no harm. | Fast Track. | Log that it has no effect. |
| 18 | `off-world-fallers` | Compatible with the known `GravityComponents.FixedUpdate` patches: Bubbles Bathtub (3286559448, rank 447) and Floating[fix] (3501501795) read-only prefixes, Floatation (1751123054, broken upstream), Cooperative Colonies (a gating prefix and a transpiler on `GetInstanceID` calls), Fast Track's *NoSplash* transpiler (on `IsVisiblyInLiquid` and the splash sound). | – | Only #14. |

## Per tweak

Kinds: **R** replaces (prefix returning false, transpiler, call-site reroute: a foreign patch is
skipped or conflicts); **F** calls fewer times than the original; **C** copies a game method's
logic instead of calling it; **W** wraps (prefix/postfix, the original runs); **S** state kept
that a mod could change another way.

"Rechecked" means the owners are read again while the game runs, so a late patch is seen;
"permanent" means once off the tweak stays off.

### fast-anim-batches (Graphics, on; both branches)

Guard: `CheckPatches` over `WritePath()`, global and permanent, at the first frame then every
3,000 passes. Fast Track's animation optimisations (on by default) patch `KAnimBatch.UpdateDirty`,
so with Fast Track the tweak is off.

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `KAnimBatch.UpdateDirty` | R (transpiler: its three write calls rerouted) | yes | yes | its loop is the tweak's | Fast Track (prefix, replaces it) |
| `KAnimBatchManager.UpdateDirty` | W (prefix `Last`, postfix `First`) | no | no | runs as before | none |
| `KAnimBatch.WriteBatchedAnimInstanceData`, `WriteSymbolInstanceData`, `WriteSymbolOverrideInfoTex` | F/moved (run on workers, the override write after the loop) | yes | yes | – | none |
| `KBatchedAnimInstanceData.WriteToTexture`, `SymbolInstanceGpuData.WriteToTexture`, `SymbolOverrideInfoGpuData.WriteToTexture` | moved to workers | yes | yes | a patch would run off the main thread | none |
| `KBatchedAnimController`: `GetCurrentFrameIndex`, `IsVisible`, `GetCurrentNumFrames`, `GetPostProcessingParams` (U60 `GetCustomShaderParams`), `GetFirstFrameIndex`, `GetPositionData`, `GetBatchInstanceData`, `GetSymbolTransform`, `GetSymbolLocalTransform`, `GetTransformMatrix()`, `symbolInstanceGpuData`; `KAnimControllerBase.PositionIncludingOffset`, `Offset`, `GetPivotSymbolPosition`, `symbolOverrideInfoGpuData`; `KBatchedAnimTracker.MyAnimGetPosition`; `TransformExtensions.GetPosition` | moved to workers, earlier | yes | yes | – | none |
| `KBatchedAnimController.ApplySymbolOverrides`, `SymbolOverrideController.ApplyOverrides`, `FaceGraph.ApplyShape` | order: a controller's instance block is now written before the controllers ahead of it apply their overrides | no | only if a patch changes another controller's frame, position or transform | the tweak's exactness argument assumes they do not | FaceGraph: Dupery Fixed (3172734005, rank 125), Execution Platform (3823526811): face drawing only, the override block, not instance data |
| `Matrix2x3` operators (firstpass) | moved to workers | no | no | – | none |

Virtual: only controllers of exactly `KBatchedAnimController` go to workers, so a mod's subclass
override is never run off the main thread.

### fast-symbol-overrides (Graphics, on; both)

Guard: `KeptCalls()` owners, for parts 2 and 3 only, global and permanent, at the first
application then every 100,000. Part 1 has none (#6).

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `SymbolOverrideInfoGpuData.SetSymbolOverrideInfo` (both overloads) | R (prefix 400, false when unchanged) | **no** (part 1) / yes (parts 2–3) | yes | a transpiler or gating prefix is skipped on every unchanged write | none |
| `SymbolOverrideController.ApplyOverrides` | R/C (prefix `Last`, the application made from kept calls or in full here) | yes | yes | – | none (ONI Together patches `AddSymbolOverride` and the removals: these raise the version, which the tweak checks) |
| `KBatchedAnimController.SetSymbolOverrides`, `GetBatch`, `batchGroupID`; `KAnimControllerBase.symbolOverrideInfoGpuData`; `KAnimBatchManager.Instance`, `GetBatchGroupData`; `KBatchGroupData.GetSymbol`; `KAnim.Build.GetTexture`; `KAnimBatch.AtlasList.GetAtlasIdx`, `Add`, `Clear` | F (a replay calls only what changed) / C (the lookups) | yes | yes | – | none |
| `FaceGraph.ApplyShape` | W (called every application) | no | no | runs as before | Dupery Fixed, Execution Platform |
| `KAnimBatchManager.CreateInstance`, `DestroyInstance` | W (postfix) | – | no | – | none |
| state: the block's bytes (written only through the two setters), its version put back | S | – | – | a mod writing the block's bytes by reflection would not be replayed | none found |

### fast-overlays (Graphics, on; master only)

Guards, per path and reversible: power `{Power.Update, GetWattsNeededWhenActive}` every 3,000
updates; infrared `{OnTemperatureOverlayInfraredUpdate}` every 3,000; targets
`{AddTargetIfVisible}` every 100,000 calls; rows `{RemoveOffscreenTargets, UpdateHighlightTypeOverlay,
ClearOutsideViewObjects}` every 3,000 walks. `CallSiteReroute` puts its transpilers after
every other owner's (issue #41, Fast Track's CullConduits).

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `ConduitMode`, `Crop`, `Harvest`, `Decor`, `Logic`, `Power`, `SolidConveyor`, `Suit`, `TileMode` `.Update`, `TileMode.TryAddObject` | R (call-site reroute, `Priority.Last`) | n/a | no | mods' transpilers run first; mods' prefixes that replace `Update` call the game's helpers | many, all compatible: Fast Track CullConduits; Combined Conduit Display (2634633299, rank 52), Ronivan's Legacy (3557584850, rank 76), High Pressure Applications (2806200110, rank 307), Paint your Pipes & Wires (3160195957), Pipe Color Overlay (3678142367), Stacked Pipes & Rails, Utility Layers, Jumper Bridges, Longer Bridges, Insulated Solid Conduit, Colony Performance Fixes, Pressurized Pipes |
| `Mode.AddTargetIfVisible` | F (not called for a target already shown; also skips the target's `PosMin`/`PosMax`) | yes | yes | – | none |
| `Mode.RemoveOffscreenTargets`, `ClearOutsideViewObjects` | C | yes | yes | – | none |
| `ClusterUtil.GetMyWorldId` | C (inside the copy of `ClearOutsideViewObjects`) | no | yes | a patch on it is bypassed | none |
| `Mode.UpdateHighlightTypeOverlay` | C | yes | yes | – | none |
| `Mode.ResetDisplayValues`, `KBatchedAnimController.SetLayer`, `HighlightColour` | W (called as before) | – | no | – | Fast Track (`ResetDisplayValues`), Project Expedition (`set_HighlightColour`) |
| `TileMode.TryAddObject` | R (prefix `Last`, false for a tile already shown) | **only against `AddTargetIfVisible`** | yes | its own patches are not checked (#7) | none |
| `Infrared.Update` | W (prefix) | **no** | no (the place cache is by reference) | – | Colony Performance Fixes (replaces it; broken on U60) |
| `TemperatureOverlayInfraredVisualizerBase.OnTemperatureOverlayInfraredUpdate` | R (the dispatcher delegate in a static field replaced) / C | yes | yes | – | none |
| `TemperatureOverlayInfraredUpdate` (abstract) | W (called virtually, as before) | – | no | overrides still run | – |
| `CircuitManager.GetWattsNeededWhenActive` | F (once per circuit per update) | yes | yes | – | Controlled Power, Utility Layers, Colony Performance Fixes, ONI Multiplayer |
| `IEnergyConsumer.WattsNeededWhenActive` getters, `Battery`/`PowerTransformer` getters inside it | F | **no** | yes | (#5) | Smarter Conditioner, Wireless Power, Conduit Teleport, Project Expedition, 调谐仪 |
| state: fixed positions of spawned buildings (not rocket modules), transforms and controllers by place | S | – | – | a mod that moves a building (or swaps its controller) without respawning it would be drawn from the old place | none found |

### fast-overlay-textures (Graphics, on; master only)

Guard: `CreateRenderTarget` owners at every call and before every release, global, reversible.

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `MultipleRenderTargetProxy.CreateRenderTarget` | W (prefix `First`, postfix `Last`) | yes | yes | a mod freeing these itself | Stock Bug Fix (1967921388, rank 369) |
| `MultipleRenderTargetProxy.Update` | W (postfix) | – | no | – | none |
| state: the dropped copy textures are destroyed | S | – | – | a mod keeping one in a material or field of its own would draw a destroyed texture; the cheap checks look at the proxy, shader globals, cameras and the active target only | none found |

### fast-conveyor-paths (Simulation, on; both)

Guard: `TryAdd` owners (PerfProbe excepted), global, reversible, every 256 calls.

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `SolidConduitFlow.TryAdd` | R (prefix `Last`) | yes | yes | – | none |
| `SolidConduitFlow.SOAInfo.GetPermittedFlowDirections` | F (once per rail held by a path, not per path) | no | low | a pure getter | none (Advanced Flow Management reads it for liquid and gas flows) |
| state: the index of `pathList` | S | – | – | rebuilt whenever the list is not what it left | ONI Multiplayer transpiles `Sim200ms` to filter the list it walks (read-only) |

### fast-anim-events (Graphics, on; both)

Guard: none (#2).

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `AnimEventManager.Update(float, List<AnimData>, List<EventPlayerData>)` | R (prefix 400) | no | yes | – | none (Advanced Batteries 3821736687 patches the public `Update()`, which is unaffected) |
| `AnimEventManager.PlayEvents`, `StopEvents` | C | **no** | yes | (#2) | Fast Track *Disable sound* |
| `KAnimControllerBase.GetFrameIdx`, `KAnim.Anim.GetFrameIdx` | C | no | yes | – | none |
| `KAnimControllerBase.GetPlaySpeed` | F (cached) | no | yes | – | none |
| `KAnimControllerBase.StartQueuedAnim`, `Queue`, `set_PlayMode`, `OnDestroy`; `KBatchedAnimController.SwapAnims` | W (postfix flags) | – | no | flags run after any mod's change | `StartQueuedAnim`: Fast Track (transpiler on the mode stored: the flag follows it), Colony Cuisine (3799038247); `Queue`: ONI Together (3630759126, rank 87), Super Rover, GRAVNet |
| state: per entry, the controller's animation (frames, total time, frame rate), mode, speed, destroyed | S | – | – | `mode`, `playSpeed` and `curAnim` are protected fields; a mod writing them by reflection outside the flagged methods would be read stale until the next flag | none found writing them |

### fast-anim-cleanup (Graphics, on; both)

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `KBatchedAnimUpdater.CleanUp` | R (prefix 400) | no | yes | (#8) | none (Colony Performance Fixes calls it from its `LateUpdate` replacement, through the patched method) |
| `KBatchedAnimUpdater.DoGridProcessing` | C | no | yes | – | none |
| `KBatchedAnimController.OnDestroy` | W (prefix) | – | no | – | none |
| state: ids of recently destroyed controllers | S | – | – | a subclass whose `OnDestroy` does not call the base, or a controller removed from the grid otherwise, is handled the same (only destroyed entries are dropped); entries of a controller destroyed before its `OnDestroy` hook ran would wait for `sweepAll` | none found |

### fast-sublimation (Simulation, on; both)

Guard: none (#3). Not a patch of `Sim200ms`: the game's update buckets get a batch function.

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `UpdateBucketWithUpdater<ISim200ms>` loop for "Sublimates SIM_200ms" | R (`batch_update_delegate`) | no | yes | – | none (ONI Multiplayer runs buckets itself and calls the batch function) |
| `Sublimates.Sim200ms` | F (skipped for blocked items) | **no** | yes | (#3) | Stop Sublimation While In Storage, No sublimation, Controlled Sublimation, Insulated Solid Conduit |
| `Sublimates.RefreshStatusItem`, `ElementLoader.FindElementByHash`, `KPrefabID.HasTag`, `Grid.PosToCell`, `Grid.Mass` | F | no | yes | – | none |
| `PrimaryElement.get_Temperature` → `SimTemperatureTransfer.OnGetTemperature` | C (the heat-delta copy) | no | yes | – | none (Fast Insulated Self Sealing AirLock patches `StructureTemperatureComponents.OnGetTemperature`, another class) |
| `Sublimates.OnSpawn`, `OnCleanUp` | W | – | no | – | Excavation Yield Configurable (`OnSpawn`) |
| state: per item, cell, transform, element's low temperature, `info.maxDestinationMass` | S | – | – | a mod changing `info` or the element's `lowTemp` at run time is not seen until the item's next full update | none found |

### fast-jobs (Simulation, on; both)

Guard: none (#4).

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `JobManager.Run` | R (prefix 400) | no | yes | – | Debug Console (void prefix, still runs); 系统性能观察器 (transpiler, bypassed) |
| `JobManager.DoNextWorkItem`, `WorkerThread.Run`, `DecrementActiveWorkerThreadCount` | C | no | yes | – | Debug Console (`WorkerThread.Run`, replaced at the first `Run`); 系统性能观察器 (`DoNextWorkItem`) |
| `IWorkItemCollection.InternalDoWorkItem` | W (each item once, any thread) | – | no | – | – |
| state: `JobManager.runSingleThreaded` | S | – | – | the tweak ignores it once the manager has threads | none found setting it |

### fast-offset-tables (Simulation, on; both)

Guard: none (#9).

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `FetchManager.UpdatePickups` | R (transpiler: its `GlobalJobManager.Run(updateOffsetTables)` call rerouted) | no | yes | – | Colony Cuisine, Cooperative Colonies (read-only prefixes) |
| `FetchManager.FetchablesByPrefabId.UpdateOffsetTables`, the `UpdateOffsetTables` work item | C/R (never run) | no | yes | – | none |
| `Workable.GetOffsets`, `Workable.ValidateOffsets`, `OffsetTracker.GetOffsets` | F (marked items only) | no | yes | – | Fast Track `PickupOpts` (`OffsetTracker.GetOffsets`; its own pass covers every item) |
| `Workable.SetOffsets`, `SetOffsetTable`, `GetOffsets`, `ValidateOffsets` | W (transpiler adds a hook after the `offsetTracker` store) | – | no | – | Remote Work NOT Supported (3558877754), 地热提取器 (3822617560): `ref` prefixes on the arguments, compatible |
| `OffsetTracker.UpdateCell`, `OffsetTableTracker.UpdateCell`, `OnCellChanged`, `FetchManager.Add`, `Remove` | W | – | no | – | none |
| state: the marks (only the game's transitions mark) | S | – | – | a mod that sets `Workable.offsetTracker` by reflection, or changes a tracker's offsets without these methods, leaves an item unmarked | none found |

### fast-partitioner-cleanup (Simulation, on; both)

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `ScenePartitioner.Sim1000ms` | R (transpiler: the scan's `Count` becomes the tweak's clean-up) | no | yes | (#10) | none |
| `GameScenePartitioner.Lookup` | F | no | yes | – | none |
| `ScenePartitioner.Remove` | W (prefix) | – | no | – | none |
| state: handles removed since the last scan, per node | S | – | – | a handle freed without `ScenePartitioner.Remove` would stay | none found |

### fast-save-type-checks (Saving, on; both)

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `SaveLoadRoot.SaveWithoutTransform` | R (transpiler `Last`: `MemberInfo.IsDefined`, `Type.ToString` call sites) | by design: works on what other transpilers leave | no | the callees are BCL methods; types do not change | BetterSave (its own `IsDefined` cache), Colony Cuisine (read-only prefix, priority 800) |

### fast-serialization (Saving, on; both; host-side)

Guard: none (#1).

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `KSerialization.SerializationTemplate.SerializeData` | R (prefix 400) | **no** | yes | (#1) | Festive Halloween Spices (read-only prefix and postfix: run too late); BetterSave (bool prefix: skipped) |
| `KSerialization.Helper.WriteValue` | F (only for fields the direct path does not cover) | no | yes | – | Fast Save (*Use Delegates*) |
| `IOHelper.WriteSingleFast`, `BinaryWriter.Write` | W | – | no | – | none |
| state: a plan per template (fields, properties, callbacks) | S | – | – | templates are built once per type by the game; a mod editing a template's field list after the first save would not be seen | none found |

### fast-save-compression (Saving, on; both; host-side)

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `SaveLoader.CompressContents` | R (prefix `Last`; a writer that is not a `FileStream` goes to the game) | by order | yes | – | BetterSave (bool prefix, runs first and takes the body) |
| `SaveLoader.DecompressContents` | R (prefix 400) | no | yes | (#11) | none (Mod Profile Manager calls it) |
| `Ionic.Zlib.ZlibStream` | F (not used) | – | low | – | none |

### background-save (Saving, on; both; host-side)

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `SaveLoader.CompressContents` | R (prefix `Last`, as above) | by order | yes | – | BetterSave; Fast Save calls it from its own background autosave |
| `SaveLoader.Save`, `SaveLoader.Load`, `LoadScreen` deletes/copies/moves | W (prefix: wait for writes in flight) | – | no | – | ~40 mods on `Save` and ~20 on `Load`, all compatible with a wait |
| state: the save file on disk is only the header until the write ends | S | – | – | (#12) | Mod Profile Manager, Fast Save |

### no-save-gc (Saving, on; both; host-side)

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `SaveLoader.Save` | R (transpiler 400: its `GC.Collect()` becomes a nop) | no | yes, if `GC.Collect` is patched | (#13) | BetterSave (transpiler and prefix/postfix on `Save`, compatible), Unlimited Autosaves, Antigravity |
| `GC.Collect()` | F (not called by `Save`) | no | yes | – | BetterSave (prefix) |

### fast-particle-collision (Simulation, on; both; host-side)

| Method | Kind | Guarded | Step aside? | Why | Patched by |
|---|---|---|---|---|---|
| `HighEnergyParticle.CheckCollision` | R (transpiler 400: `GatherEntries` and `GetComponent<HighEnergyParticle>` call sites) | no | low (#14) | – | Rephysicalized (3609531831), RadBolt Fission (3683855614): transpilers on `ldc.r4 20`, compatible; Geyser Swapper (3670972563, rank 544): postfix; Fast Track `RadiationOpts` (on by default): prefix returning false, so with Fast Track the tweak does nothing |
| `GameScenePartitioner.GatherEntries`, `GameObject.GetComponent<HighEnergyParticle>` | F | no | yes for `GatherEntries` | – | none |
| `KCollider2D.Intersects` (virtual) | W (called virtually) | – | no | – | – |
| state: each radbolt's colliders, read once | S | – | – | a mod adding a collider to a live radbolt is not seen | none found |

### Opt-in tweaks

| Tweak | Method | Kind | Guarded | Patched by |
|---|---|---|---|---|
| `brain-rate` | `BrainScheduler.RenderEveryTick` | R (prefix 400) | no (#15) | Fast Track, Cooperative Colonies |
| | `BrainGroup.RenderEveryTick`, `PostRenderEveryTick`, `Brain.UpdateBrain` | F (the point of the tweak) | – | CycleTrim; Multiplayer Mod, Cooperative Colonies (`UpdateBrain`) |
| | `DupeBrainGroup`/`CreatureBrainGroup.InitialProbeCount` | W (postfix caps) | – | CycleTrim builds its own |
| `off-screen-conveyors` | `SolidConduitFlow.RenderEveryTick` | R (prefix 400) | no (#16) | Cooperative Colonies (gating prefix), Controlled Visuals, Multi Channel Utilities (postfixes) |
| | `Transform.SetPosition` of items | F (the point) | – | – |
| | `SolidConduitFlow.RemovePickupable` | W (prefix) | – | Ronivan's Legacy, Utility Layers, Insulated Solid Conduit |
| `slow-reachability` | `ReachabilityMonitor.InitializeStates` | R (transpiler: a constant) | no (#17) | Fast Track (replaces it) |
| `off-world-fallers` | `GravityComponents.FixedUpdate` | R (transpiler: `Grid.IsValidCellInWorld` call site) + W (postfix) | no (#18) | Bubbles Bathtub, Floating[fix], Floatation, Cooperative Colonies, Fast Track |
| `fix-disease-overlay-crash` (u60) | `CellChangeMonitor.RenderEveryTick` | W (read-only prefix, edits the dictionary) | – | Fast Track *FastReachability* (replaces it: the fix then does nothing), Cooperative Colonies |

## Guard shapes

One helper for every tweak, so the tweaks stop writing their own `Foreign`/`CheckPatches`:

- **What counts as foreign:** an owner other than `OniMoono.Host.GameTweaks` and the test tools
  (PerfProbe, E2eDriver only when a spec asks for it). Read-only prefixes and postfixes of
  methods the tweak still calls do not need a step-aside; the helper should take, per method,
  whether any patch, or only transpilers and bool/ref prefixes, counts.
- **Recheck, not install-time:** mods patch while loading, after the tweaks install, and some
  patch later (Debug Console at the first `JobManager.Run`). Every guard in the code today is
  already rechecked (first use, then every N calls); new ones should be too, with N such that a
  check costs well under 1 % of what the tweak saves.
- **Per path where the tweak has separable parts** (fast-overlays' power, infrared, targets and
  rows; fast-symbol-overrides' setters and applications; fast-anim-events' inline event loops;
  fast-sublimation per bucket), **global where the parts share state** (fast-jobs,
  fast-anim-batches, fast-anim-cleanup, fast-offset-tables, fast-partitioner-cleanup,
  brain-rate, off-screen-conveyors).
- **Reversible or permanent:** reversible where the tweak keeps no state the game's code would
  have changed meanwhile (fast-overlays, fast-conveyor-paths, fast-jobs); permanent where it
  does (fast-offset-tables' marks, fast-partitioner-cleanup's records, fast-anim-cleanup's ids,
  fast-anim-events' mirror: switching back would need a full rebuild of that state).
- **Prefixes that return false:** `Priority.Last` and `__runOriginal` everywhere, so mods'
  prefixes run first and a mod's false return skips the tweak (fast-serialization,
  fast-symbol-overrides' setters, fast-save-compression's `Decompress`, fast-jobs,
  fast-anim-cleanup, fast-anim-events, brain-rate, off-screen-conveyors).
- **Call-site transpilers:** `CallSiteReroute.TranspilerPriority` for every one (move the class
  to u60 with them).

## Testing each fix

The test proves the foreign patch runs as in the original, in both players:

- **Real mods, e2e specs** (`mods: [...]`), compared with the original:
  - Festive Halloween Spices (#1): spice a meal with a Halloween spice, save, then load the save
    with the mod off: the original's `Edible` has no modded spice, ours must match (today it
    keeps it). Better Save: its `SerializerPatch` call counter (`setup.call` on the static) is
    above 0 after a save in both players.
  - Fast Track *Disable sound* (#2): `count` `SoundEvent:Play` and `AnimEventManager:PlayEvents`
    over 30 s of a busy colony: 0 in both.
  - Stop Sublimation While In Storage and No sublimation (#3): `count` `Sublimates:Sim200ms` over
    60 s beside a pile of polluted dirt in a full gas cell: the counts match the original's.
  - Debug Console (#4): a work item that throws on a worker reaches its console log in both.
  - Smarter Conditioner (#5): the Power overlay's tint of a Thermo Regulator circuit, and
    `count` on its getter, match the original's.
  - Fast Track (#15, #17) and Cooperative Colonies (#15, #16) with `brain-rate`,
    `off-screen-conveyors` and `slow-reachability` on: the log line that the tweak stepped
    aside, and `count` on Fast Track's `AsyncBrainGroupUpdater.StartBrainCollect` above 0.
  - Fast Save and Mod Profile Manager (#12): an autosave with Fast Save's background save,
    loaded back; Mod Profile Manager's list read right after a pause-menu save.
- **A test patch where no mod exercises the method** (#6–#11, #13, #14): E2eDriver's `count`
  adds a read-only prefix, which Harmony runs even after a false prefix, so it measures calls
  but cannot show a skipped prefix. A `count` variant that patches with a bool prefix
  returning true (which a tweak's false prefix skips) and one that adds a transpiler marker
  would cover both: in the original the count is the number of calls; with the tweak it must
  match once the guard has seen the patch (within its recheck interval). A rechecked guard is
  then also proven to react to a late patch.
- **Unit tests** (as `CallSiteRerouteTests`) for the shared helper: owner filtering, the
  bool-prefix ordering with `Priority.Last`, and the transpiler priority, on stand-in types.

## Surprises

- **Tweaks that return false at the default priority hide mods' prefixes.** Harmony 2.4.2 skips
  every later prefix that returns `bool` or writes a `ref`. Nine tweak prefixes do this; the
  known victims are Better Save's serializer, Fast Track's brain scheduler and Cooperative
  Colonies' lockstep gates, and Festive Halloween Spices' read-only prefix runs after our write.
- **`fast-symbol-overrides` guards only half of itself:** the `KeptCalls` check gates the kept
  applications, not the setter skip of part 1.
- **`fast-overlays`' guard lists miss methods it acts on:** `TileMode.TryAddObject` (its own
  prefix), `Infrared.Update`, and the consumers' `WattsNeededWhenActive` getters.
- **`fast-anim-events` copies two methods Fast Track patches** (`PlayEvents`/`StopEvents`).
- **Fast Track already turns three tweaks into no-ops:** `fast-anim-batches` (steps aside),
  `fast-particle-collision` (Fast Track's `CheckCollision` prefix returns false) and
  `slow-reachability` (Fast Track replaces `InitializeStates`); on u60 also
  `fix-disease-overlay-crash`.
- **Background save under Fast Save's background autosave** is a second background write
  behind the first; it works on paper, untested.
- **Class comments call default-on tweaks "opt-in"** (fast-jobs, fast-sublimation,
  fast-offset-tables, fast-overlays, fast-symbol-overrides, fast-anim-events,
  fast-anim-batches, fast-anim-cleanup, fast-partitioner-cleanup, fast-conveyor-paths,
  fast-save-type-checks and the five host-side ones); only the Changes gameplay group is off by
  default.
