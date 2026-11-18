# U60 review: simulation tweaks

Static review of seven game tweaks against U60 (build 761015, public test) before a U60
player exists. No game was run. Klei's decompiled source: U59 = oni-archives tag U59-744825,
U60 = U60-761015 (Assembly-CSharp, `raw/`). The firstpass types (JobManager, the update
buckets, the event system, the cell-change monitor) were not in the decompile read then; they were
compared by decompiling Assembly-CSharp-firstpass from the U59 Linux install and from the
U60 Windows build (`out/local-tools/win761015/Managed`). Line numbers are U60's.

`src/OniMoono.GameTweaks` builds against U60 with no errors and no obsolete-API warnings.

**Result: all seven are unchanged.** No code change was needed.

- Every game member they patch, reflect on or reimplement has the same behaviour in U60. The
  U60 diffs in those members are the newer compiler's shapes (locals split out, ternaries,
  field initialisers) or the event-system move, which they do not depend on.
- None is a retire candidate: Klei's U60 changes do not touch what any of them speeds up.

## FastSublimation (`fast-sublimation`)

| | |
|---|---|
| Game members touched | Harmony postfix `Sublimates.OnSpawn`, prefix `Sublimates.OnCleanUp`; replaces the update loop of the `Sublimates SIM_200ms` buckets through `UpdateBucketWithUpdater<ISim200ms>.batch_update_delegate` (found via `SimAndRenderScheduler.bucketTable` and its entry's `buckets`); reads by name `Sublimates.primaryElement`, `sublimatedMass`, `lastEmitState`, `SimTemperatureTransfer.simHandle`; reads `Sublimates.info` (`sublimatedElement`, `maxDestinationMass`), `PrimaryElement.sttOptimizationHook`, `getTemperatureCallback`, `Element.lowTemp`, `Grid.Mass`, `Game.simData.elementChunks[].temperature/deltaKJ`; subscribes to `GameHashes.TagsChanged` (-1582839653). Assumes `Sublimates.Sim200ms` does nothing but `RefreshStatusItem(BlockedOnPressure)` (a no-op when already blocked) when the item is unsealed, in the same cell, warmer than `lowTemp` and its cell holds at least `maxDestinationMass`; assumes the temperature read is `SimTemperatureTransfer.OnGetTemperature` with its `deltaKJ` copy; `EmitState.BlockedOnPressure == 1`. |
| U60 change | None that matters. `Sublimates.Sim200ms` (Sublimates.cs:158-258), `RefreshStatusItem` (286-311), `EmitState` (28-33), `OnSpawn`/`OnCleanUp` (77-88) are the same; the diff only moves the absorb/split handlers off `IntraObjectHandler` (54-74) and splits a span local (126-132). `SimTemperatureTransfer.OnGetTemperature` (208-223) is the same; `OnPrefabInit` now assigns a cached static delegate `_onGetTemperature` (142), whose `Method.DeclaringType` is still `SimTemperatureTransfer`, so the tweak's check holds. `PrimaryElement.Temperature` (112-122) still calls `getTemperatureCallback`. `Sim.IsValidHandle` only re-shaped (Sim.cs:987). Firstpass: `UpdateBucketWithUpdater` identical; `SimAndRenderScheduler` key format `"{Type.Name} {rate}"` unchanged (only loop locals renamed); `KPrefabID.AddTag`/`RemoveTag` still trigger -1582839653 on every change; `KMonoBehaviour.Subscribe(int, Action<object>)` kept and not obsolete (only the `IntraObjectHandler` overloads are). |
| Verdict | Unchanged |
| Runtime check | Run a large colony with `ONI_TWEAK_VERIFY=1`: the `fast-sublimation: N updates skipped, M run, 0 wrong skips` line every 100,000 skips must report 0 wrong, and the skip count must be non-zero (polluted dirt loose in full gas). |

## FastConveyorPaths (`fast-conveyor-paths`)

| | |
|---|---|
| Game members touched | Harmony prefix (skip) on private `SolidConduitFlow.TryAdd(List<Conduit>)`, reimplementing it; `MethodDelegate` to the original for verify mode; reads by name `SolidConduitFlow.pathList`; uses `GetSOAInfo().NumEntries`, `GetPermittedFlowDirections`, `Conduit.idx`; verify-mode postfix on `OnUtilityNetworksRebuilt`. Assumes TryAdd's three searches (new path held by a kept one; kept ones held by the new one, and empty ones, removed in order; single-direction shared rails cut from the new path) and that `pathList` is cleared by `RebuildConnections`. |
| U60 change | Semantics unchanged. `TryAdd` (SolidConduitFlow.cs:806-910): the only diff is in the third loop (895-906), where `FindIndex(...) != -1 && IsPowerOfTwo(...)` became nested ifs with locals; same test, same order. `pathList` (539), `OnUtilityNetworksRebuilt` (612-620), `RebuildConnections` clearing `pathList` (625), the single call site in `FindSinksInternal` (803) are the same. Other diffs in the file (`GetNextFlowSource` 395-470, `GetConduit` 1194) are compiler re-shaping. |
| Verdict | Unchanged |
| Runtime check | With `ONI_TWEAK_VERIFY=1`, build and remove conveyor rails in a base with many sources and sinks: each rebuild logs `fast-conveyor-paths: N paths checked, 0 wrong`. |

## OffscreenConveyors (`off-screen-conveyors`, changes behaviour)

| | |
|---|---|
| Game members touched | Harmony prefix (skip) on `SolidConduitFlow.RenderEveryTick(float)`, reimplementing it; prefix on `SolidConduitFlow.RemovePickupable(int)`; uses `GetSOAInfo`, `Conduit.GetLastFlowInfo`/`GetCell`, `GetCellFromDirection`, `GetContents`, `GetPickupable`, `GetConduit`, `ContinuousLerpPercent`, `KBatchedAnimUpdater.IsChunkVisible`/`PosToChunkXY`, `TransformExtensions.SetPosition`. Assumes RenderEveryTick only lerps each moving item between its source and destination cell centres. |
| U60 change | `RenderEveryTick` (SolidConduitFlow.cs:1017-1040), `RemovePickupable` (1157-1176) and `ContinuousLerpPercent` (554) are unchanged. `KBatchedAnimUpdater.IsChunkVisible` (257) unchanged, `PosToChunkXY` (268) only re-shaped. The U60 cell-change move: `SetPosition` now calls `CellChangeMonitor.MarkDirty(transform, position)`, which sets the KObject's `pendingCell`, and `CellChangeMonitor.RenderEveryTick` updates `KObject.cachedCell` and triggers the cell-changed event (1088554450) on the object; `Grid.PosToCell(KMonoBehaviour)` (Grid.cs:1321-1340) reads `pendingCell`/`cachedCell`. A skipped move therefore leaves `cachedCell` lagging exactly as the transform does, which is the documented behaviour; the final placement goes through the same `SetPosition` and updates it. |
| Verdict | Unchanged |
| Runtime check | Watch a conveyor loop off screen, pan to it: items must appear on the rail and continue; an item taken off-screen by a loader/receptacle must land in the receiver with the right position (no items dropped mid-rail on deconstruction). |

## BrainRate (`brain-rate`, changes behaviour)

| | |
|---|---|
| Game members touched | Harmony prefix (skip) on `BrainScheduler.RenderEveryTick(float)`, reimplementing its loop; postfixes on private nested `BrainScheduler.DupeBrainGroup.InitialProbeCount` and `CreatureBrainGroup.InitialProbeCount`; reads by name `BrainScheduler.brainGroups`; `MethodDelegate` to the abstract `BrainGroup.InitialProbeCount`; calls `BrainGroup.RenderEveryTick`/`PostRenderEveryTick`. Assumes the scheduler's loop is quit/loading check then each group's RenderEveryTick + PostRenderEveryTick, and that `InitialProbeCount` is the per-frame brain budget (also used by `CreatureBrainGroup.PostRenderEveryTick`). |
| U60 change | None that matters. `BrainScheduler.RenderEveryTick` (BrainScheduler.cs:365-376), `BrainGroup.RenderEveryTick` (86-115), the groups' `InitialProbeCount` (172-175, 243-246), `CreatureBrainGroup.PostRenderEveryTick` (278-293), `brainGroups` (307) are the same. The diff only replaces the unused `increaseLoadLabel`/`decreaseLoadLabel` with `tagLabel` (13, 25-29). `Brain.UpdateBrain` lost its Superluminal profiler calls (Brain.cs:30-40). |
| Verdict | Unchanged |
| Runtime check | Frame-stats brain updates per frame above 30 fps must drop as on U57 (e.g. 120 fps: Duplicant group about every fourth frame); idle Duplicants still pick up new errands promptly. |

## BrainRateAllowance (part of `brain-rate`)

| | |
|---|---|
| Game members touched | None (plain .NET, no game types). Its input is the game's per-frame count from `InitialProbeCount` (Tuning defaults 1 and 5, BrainScheduler.cs:150, 223), still the 30 fps budget stated by `BrainScheduler.framesPerSecond` (305). |
| U60 change | None. |
| Verdict | Unchanged |
| Runtime check | Covered by its unit tests; nothing U60-specific. |

## SlowReachability (`slow-reachability`, changes behaviour)

| | |
|---|---|
| Game members touched | Transpiler on `ReachabilityMonitor.InitializeStates`: matches `ldc.i4.6` (`UpdateRate.SIM_1000ms`), `ldc.i4.1` (`load_balance: true`), call `FastUpdate`, and changes the rate to `ldc.i4.7` (`SIM_4000ms`); throws (install fails and is reported) unless exactly one match. Assumes the `UpdateRate` values and that `UpdateReachability` is only timing-dependent. |
| U60 change | None. ReachabilityMonitor.cs has no diff; `root.FastUpdate("UpdateReachability", updateReachabilityCB, UpdateRate.SIM_1000ms, load_balance: true)` at line 46. U60 IL (ilspycmd -il): `IL_0021: ldc.i4.6`, `IL_0022: ldc.i4.1`, `IL_0023: callvirt ...State::FastUpdate(string, IUpdater, UpdateRate, bool)`, one occurrence. Firstpass `UpdateRate` identical (SIM_1000ms = 6, SIM_4000ms = 7). `MinionGroupProber.IsAllReachable` and `Workable.GetOffsets` now take/return `IReadOnlyList<CellOffset>` (MinionGroupProber.cs:38-70), which the tweak does not touch. |
| Verdict | Unchanged |
| Runtime check | The host log shows the tweak installed with no transpiler exception; opening a door to a sealed room makes its errands reachable within 4 game seconds. |

## FastJobs (`fast-jobs`)

| | |
|---|---|
| Game members touched | Harmony prefix (skip) on `JobManager.Run(IWorkItemCollection)` (firstpass), reimplementing it; reads by name `JobManager.threads`; calls `IWorkItemCollection.Count`/`InternalDoWorkItem(i, threadIndex)`; sets `JobManager.errorOccured`. Assumes thread indices 1..threads.Count for workers and 0 for the caller, every item run once before Run returns, and no work item depending on which thread runs it. U60 callers (Assembly-CSharp): FetchManager, GlobalChoreProvider/ChoreProvider (chore collection), ConduitFlow and ConduitFlowVisualizer, AmountInstance, NavGridUpdater, SolidTransferArm, PropertyTextures, SimDebugView, EggProtectionMonitor, CreatureLightToggleController. |
| U60 change | None. Decompiled firstpass `JobManager` differs only in `WorkerThread.ThreadMain` (a local split out); `Run`, `Initialize` (threads `KWorker{i}` with index i+1), `DoNextWorkItem`, `threads` are identical. `IWorkItemCollection` and `WorkItemCollection` identical. In Assembly-CSharp the only U60 change on a line mentioning JobManager, work items or thread indices is `TaskDivision.Run` (for loop to foreach, TaskDivision_2.cs:31-38); the callers' other U60 changes (e.g. GlobalChoreProvider's storage-fetch tag dedup, 307-341) run on the main thread or inside items and do not depend on which thread runs an item. |
| Verdict | Unchanged |
| Runtime check | With `ONI_TWEAK_VERIFY=1`, `fast-jobs: N runs checked, 0 wrong` every 20,000 runs; frame stats show time in JobManager.Run down as on U57. |

## Not in scope here

The task named FetchManager, Workable and CircuitManager: none of these tweaks patches them.
FetchManager reaches FastJobs only as a JobManager caller, and U60 did not change how it calls
it (FetchManager.cs: one fetch-amount test changed, 422-434, not job-related). Workable is
reached by SlowReachability only through `ReachabilityMonitor.UpdateReachability`, whose
timing is all the tweak changes. CircuitManager does not use JobManager in U60.
