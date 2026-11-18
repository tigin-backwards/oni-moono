# U60 review: animation tweaks

This is a static review of `fast-anim-batches`, `fast-anim-cleanup`, `fast-anim-events` and
`fast-symbol-overrides` against U60 (761015). It was not checked in a player. The tweaks build
against U60 with no errors.

Sources:

- The Assembly-CSharp members come from Klei's decompiled source (oni-archives, tags U59-744825
  and U60-761015). Line numbers are U60 lines under `raw/Assembly-CSharp/`.
- The Assembly-CSharp-only decompile read then did not include the firstpass classes (KAnimBatch, KAnimBatchManager, BatchSet,
  KAnimBatchGroup, SymbolOverrideInfoGpuData, SymbolInstanceGpuData, KBatchedAnimInstanceData,
  KAnimConverter, KAnim, TransformExtensions). For those, `Assembly-CSharp-firstpass.dll` was
  decompiled with ilspycmd, from the U59 Linux install and from the U60 Windows build, and the
  two were diffed.

## Summary

| Tweak | Verdict |
|---|---|
| fast-anim-batches | **updated**: a renamed write-path method had turned it off on U60 |
| fast-anim-cleanup | unchanged |
| fast-anim-events | unchanged |
| fast-symbol-overrides | unchanged (not a retire candidate) |

## fast-anim-batches

| | |
|---|---|
| Game members touched | Transpiles `KAnimBatch.UpdateDirty`, matching its three calls to `WriteBatchedAnimInstanceData`, `WriteSymbolInstanceData` and `WriteSymbolOverrideInfoTex`, and throws unless it finds exactly 3. Calls those three methods through delegates. Prefix and postfix on `KAnimBatchManager.UpdateDirty`. Reads the fields `KAnimBatchManager.activeBatchSets`, `KAnimBatch.needsWrite`, `dirtySet`, `symbolInstanceSlots` and `symbolOverrideInfoSlots`, and reads `KAnimBatch.id`, `dataTex`, `symbolInstanceTex`, `isSetup`, `materialType`, `Controllers` and `size`, plus `BatchSet.dirty`, `batchCount` and `GetBatch`. Compares delegates with `KBatchedAnimTracker.MyAnimGetPosition`. The patch-owner check covers the write path, which runs on worker threads: `SymbolOverrideInfoGpuData.WriteToTexture`, `KBatchedAnimInstanceData.WriteToTexture`, `SymbolInstanceGpuData.WriteToTexture`, `KBatchedAnimController.GetCurrentFrameIndex`, `IsVisible`, `GetCurrentNumFrames`, `GetCustomShaderParams` (was `GetPostProcessingParams`), `GetFirstFrameIndex`, `GetPositionData`, `GetBatchInstanceData`, `GetSymbolTransform`, `GetSymbolLocalTransform` and `GetTransformMatrix()`, the `symbolInstanceGpuData` and `symbolOverrideInfoGpuData` getters, `KAnimControllerBase.PositionIncludingOffset`, `Offset` and `GetPivotSymbolPosition`, and `TransformExtensions.GetPosition`. It assumes that a controller's writes touch only its own batch slots, that applying overrides does not change another controller's instance or symbol data, and that `Apply` only marks a texture as changed. |
| U60 change | **1.** `KBatchedAnimController.GetPostProcessingParams(): float` was renamed to `GetCustomShaderParams(): uint` (KBatchedAnimController.cs:956, field `customShaderParameters` :58; same rename on `KAnimConverter.IAnimConverter`). `KBatchedAnimInstanceData.UpdateData` now packs `(params << 1) \| visible` into a `uint` at offset 8, where U59 stored `numFrames*1000 + params` as a float. The record is still 112 bytes. Because the tweak looked the method up by name, `AccessTools.Method` returned null. `CheckPatches` then logged `fast-anim-batches: off (a write-path method was not found)` on the first frame, so on U60 the tweak was **silently off**. **2.** The `symbolOverrideInfoGpuData` getter now creates the block lazily (KAnimControllerBase.cs:190–206, `ResetSymbolOverrideInfoGpuData` :430). Only `SetBatchGroup` resets it, which only `LoadAnims` reaches (KBatchedAnimController.cs:223). The loop's main-thread read in `WriteOverrides` creates the block before a deferred worker write reads it, so a worker read has no side effect. The constructor only allocates managed memory in any case. **3.** Batches are now pooled (`KAnimBatchGroup.GetOrCreateBatch`/`ReleaseBatch`, `KAnimBatch.Reuse`). `Reuse` assigns a new `id`, and `Work` is keyed by id and checks `Work.Batch == batch`, so a reused batch gets fresh bookkeeping. **4.** There is a new `MaterialType.Conduit`, used by the new `ConduitFlowKAnimVisualizer`. Its `customShaderParameters` writes happen on the main thread from overlay events, and its position and transform come from the same plain code as other controllers. Only UI batches need the main thread. **5.** Re-registration now goes through `ValidateRegistration` (KBatchedAnimController.cs:877) instead of DeRegister plus Register, and the abstract `Register`/`DeRegister` became private. Neither is on the write path. `KAnimBatch.UpdateDirty` is the same apart from the `UnityEngine.Object` qualification. U60 IL (ilspycmd `-il`) has exactly one `call` of each write method (IL_00c8, IL_00d4, IL_0197), so the transpiler still matches and still throws if the count changes. |
| Verdict | **Updated** (commit 8b11ff92). The write path now names `GetCustomShaderParams`. A missing write-path method now fails the install and names the method, where before the tweak quietly turned itself off. |
| Runtime check on a U60 player | With the tweak on, the log must not contain `fast-anim-batches: off`. With `ONI_TWEAK_VERIFY=1` (Gluttony-sized base), the periodic line should show "written ahead" and "skipped in the loop" both rising, with 0 mismatches. That includes conduit overlays with "view conduit flow" toggled, which exercises the new uint packing and the Conduit material. `ONI_TWEAK_TIMING=1` should report parallel frames. |

## fast-anim-cleanup

| | |
|---|---|
| Game members touched | Replaces `KBatchedAnimUpdater.CleanUp` (prefix, skip) and reimplements its 16-chunk sweep. Reads the fields `controllerGrid` and `cleanUpChunkIndex`. Prefix on `KBatchedAnimController.OnDestroy`. Assumes grid entries are keyed by `GetInstanceID()`, that an entry is dropped only when its controller is destroyed (`== null`), that `CleanUp` returns early when there is no grid or no `Camera.main`, and that the index advances by 16 modulo the grid length. |
| U60 change | `CleanUp` (KBatchedAnimUpdater.cs:477–505) is unchanged. `DoGridProcessing` (:507) was only rewritten as a single expression (`controllerGrid != null && Camera.main != null`). `ProcessRegistrations` was restructured: it still keys entries by `GetInstanceID()`, and the moving-controller assert now runs only when the entry is already present, with the same result. `OnDestroy` (KBatchedAnimController.cs:795) is unchanged. U60 has no change that skips clean chunks. |
| Verdict | **Unchanged**: the tweak is still valid. |
| Runtime check on a U60 player | With `ONI_TWEAK_VERIFY=1`, the periodic `fast-anim-cleanup: N chunks swept, M skipped, 0 wrong skips` line should show 0 wrong skips over a session that builds, deconstructs and kills critters. |

## fast-anim-events

| | |
|---|---|
| Game members touched | Replaces `AnimEventManager.Update(float, List<AnimData>, List<EventPlayerData>)` (prefix, skip) and reimplements its loop. Reads the field `AnimEventManager.finishedCalls` and the `EventPlayerData` fields (`controller`, `mode`, `elapsedTime`, `currentFrame`, `previousFrame`, `events`, `updatingEvents`). Reimplements `KAnim.Anim.GetFrameIdx` and `KAnimControllerBase.GetFrameIdx(time, false)`. Reads `KAnimControllerBase.mode`, `CurrentAnim` and `GetPlaySpeed()`. Postfixes `StartQueuedAnim`, `Queue`, the `PlayMode` setter, `KBatchedAnimController.SwapAnims` and `KAnimControllerBase.OnDestroy`, on the assumption that these are the only writers of `curAnim`, `mode` and `playSpeed`. |
| U60 change | `Update` (AnimEventManager.cs:222) only gained locals in the `PlayEvents`, `StopEvents` and `OnUpdate` loops. The logic is the same, and `finishedCalls` (:108) is still there. `GetFrameIdx` (KAnimControllerBase.cs:496), `GetPlaySpeed` (:541) and `KAnim.Anim.GetFrameIdx` are unchanged. The writers of `curAnim`, `mode` and `playSpeed` are the same set as in U59: the `PlayMode` setter (:290), `OnDestroy` (:637), `StartQueuedAnim` (:779–789) and `SwapAnims` (KBatchedAnimController.cs:282). The patched methods `Queue` (:680), `StartQueuedAnim` (:746), `PlayMode` (:282), `SwapAnims` (:267) and `OnDestroy` (:634) all exist with the same signatures. |
| Verdict | **Unchanged**: the tweak is still valid. |
| Runtime check on a U60 player | With `ONI_TWEAK_VERIFY=1`, the `fast-anim-events: N entries checked, 0 stale` line should report no `stale inputs` or `stale play speed` lines. Duplicant speech and conduit or overlay changes are good triggers. |

## fast-symbol-overrides

| | |
|---|---|
| Game members touched | Prefixes both `SymbolOverrideInfoGpuData.SetSymbolOverrideInfo` overloads, `(int, int, int, KBatchGroupData, int, int)` and `(int, ref SymbolFrameInstance)`, and skips a call that would store only values already held. Reads the field `symbolOverrideInfoConverter.symbolOverrideInfos`. Assumes these setters are the only writers of the block and that `version` is read only by `KAnimBatch.WriteSymbolOverrideInfoTex`. |
| U60 change | `SymbolOverrideInfoGpuData` and `SymbolOverrideController` are unchanged (empty diffs). The only readers of the block in U60 are still `KAnimBatch` and the controller's `SetSymbolOverrides`/`SetSymbolOverride` (KBatchedAnimController.cs:849–857). U60 now creates the block lazily (KAnimControllerBase.cs:190), which is Klei's allocation saving for controllers with no overrides. A new block starts at version 1 and all zeros, as in U59, so the "already holds" test behaves the same. Klei did not remove the version bump on setters that store unchanged values. |
| Verdict | **Unchanged**: the tweak is still valid. It is not a retire candidate, because U60 does not skip no-op setter calls. |
| Runtime check on a U60 player | `fast-anim-batches` in `ONI_TWEAK_VERIFY=1` mode, run with this tweak on, should show 0 mismatches. Comparing screenshots of Duplicants with clothes and items, critters and decor buildings with the tweak on and off should show no difference. |
