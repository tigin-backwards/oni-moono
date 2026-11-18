# U60 review: partitioner, offset, faller and radbolt tweaks (2026-10-29)

Static review of four game tweaks against U60 (build 761015, Unity 6000.3.17f1, public test),
from U59 (744825). No player ran: there is no Linux U60 build yet.

## Method

- **Source:** Klei's decompiled game code in oni-archives, `diff_type <Type> U59-744825
  U60-761015`. Line numbers below are U60's (`761015:raw/Assembly-CSharp/`).
- **Firstpass types** (`HybridListHashSet`, `KCompactedVectorBase`, `HandleVector`,
  `GlobalJobManager`) were not in the decompile read then. They were decompiled with ilspycmd from U59's
  Linux `Assembly-CSharp-firstpass.dll` and U60's Windows one and compared.
- **IL:** each transpiler's anchors were checked in the IL of U60's Windows `Assembly-CSharp.dll`
  (`ilspycmd -il`). That build's IL is unoptimised (`nop`s, comparisons stored to locals before
  branching), unlike U59's Linux build. All four transpilers match both shapes.
- **Build:** `dotnet build src/OniMoono.GameTweaks` and `src/OniMoono.Host` against
  `out/local-tools/win761015-game`: 0 errors.

None of the four needs a code change. Klei's U60 work does not cover what any of them does, so
none is a retire candidate. Each transpiler still throws `InvalidOperationException` at install
when its anchors are missing.

## fast-partitioner-cleanup

`src/OniMoono.GameTweaks/FastPartitionerCleanup.cs`. Verdict: **unchanged**.

| Game member | How the tweak uses it | U60 change | Still valid |
|---|---|---|---|
| `ScenePartitioner.Remove(Handle)` | Prefix: records the handle with the nodes it marks dirty | Lines 207-243. Adds a `DevOnly.Assert` that it runs on the main thread, and clears `entry.callbackContext` and `entry.eventCallback` next to `entry.obj`. The node loop and dirty marking are unchanged. | Yes |
| `ScenePartitioner.Sim1000ms` | Transpiler: the one `HybridListHashSet.Count` call after `ldfld ScenePartitionerNode::entries` becomes `Clean(entries, layer, y, x)`. The node is loaded by the instruction before the first `ldfld DirtyNode::layer`. | Source is unchanged (lines 245-260). The U60 IL has one `get_Count`, and `ldloc.1` comes before `ldfld DirtyNode::layer`. The unoptimised loop test (`clt; ceq; stloc; ldloc; brtrue`) still exits on the -1 that `Count - 1` gives when Clean returns 0. | Yes |
| `ScenePartitioner.GetNodeExtents(ScenePartitionerEntry)` (private, by name) | Delegate, gives the removed entry's nodes | Unchanged (lines 111-114) | Yes |
| `ScenePartitioner.DirtyNode` fields `layer`/`x`/`y`, `ScenePartitionerNode.entries` | Read by name | Unchanged (lines 7-21) | Yes |
| `GameScenePartitioner.Free` / `Lookup`, `ScenePartitionerEntry.Release` | Assumes a handle is freed only after `Release` calls `ScenePartitioner.Remove` | Free (lines 366-376) still calls `Release` (`ScenePartitionerEntry` line 63, which calls `Remove`) before `scenePartitionerEntries.Free`. Lookup is unchanged (94-103). The `Add` overloads now take an `Action<object, object>` with a context, which does not matter here. | Yes |
| `HybridListHashSet<T>.dict` (by name), `Remove`, `Count`, indexer | Swap-remove order and the place lookup | `Add` now uses `dict.TryAdd` (same result), and an `AsList()` was added. The `dict` field and `Remove`'s swap-last order are unchanged. | Yes |

**Runtime check:** with `ONI_TWEAK_VERIFY=1` on Gluttony, the periodic line `fast-partitioner-cleanup:
N nodes cleaned, M entries dropped, 0 wrong`, and no main-thread asserts from `Remove`.

## fast-offset-tables

`src/OniMoono.GameTweaks/FastOffsetTables.cs`. Verdict: **unchanged**. The way staleness is stored
changed, but the transitions the tweak marks did not.

| Game member | How the tweak uses it | U60 change | Still valid |
|---|---|---|---|
| `OffsetTracker.GetOffsets` / `ValidateOffsets` / `UpdateCell` | Staleness test; postfix on `UpdateCell` | `offsets` is now a `List<CellOffset>`, and staleness is a new `offsetsValid` flag (lines 11, 15-37) rather than `offsets == null`. It is cleared only by the new `InvalidateOffsets()` (line 39), which is called only from `OffsetTableTracker.UpdateCell` (59) and `OnCellChanged` (135). Both are still patched. `ValidateOffsets` is still `cell == previousCell && valid`. `GetOffsets` now asserts that it runs on the main thread. | Yes |
| `StandardOffsetTracker` ctor | Owner recorded at the `offsetTracker` assignment | Copies the offsets into a new list. `offsetsValid` now starts false, so the first `GetOffsets` runs the base `UpdateOffsets` (empty). In U59 the first check failed too, because `previousCell` was invalid. The assignment marks the item, so the outcome is the same. | Yes |
| `OffsetTableTracker.UpdateCell` / `OnCellChanged` (by name), field `cmp` | Postfixes that mark the owner | Both still exist with the same names. `OnCellChanged` is now reached through a static `_OnCellChanged(context, data)` lambda that calls it directly, instead of through a bound delegate. A direct call can be inlined; inlining into code compiled before the patch is handled by the host (ARCHITECTURE D34, `PatchInliners`). `UpdateOffsets` no longer allocates a pooled set. | Yes |
| `Workable.offsetTracker` assignments in `SetOffsets`, `SetOffsetTable`, `GetOffsets(int)`, `ValidateOffsets(int)` | Transpiler: exactly one `stfld offsetTracker` in each | Lines 615-649. `GetOffsets` returns `IReadOnlyList<CellOffset>`, and the default tracker is `new StandardOffsetTracker(OffsetGroups.Use)`. The U60 IL still has one `stfld` in each of the four. The parameterless `GetOffsets()` has none and is not patched. | Yes |
| `FetchManager.UpdatePickups` | Transpiler: `ldfld updateOffsetTables; call GlobalJobManager.Run` | Unchanged (lines 390-412). The U60 IL has the same pair at `IL_00a0`/`IL_00a5`. | Yes |
| `FetchManager.UpdateOffsetTables` job | Reimplemented: check `ValidateOffsets(cachedCell)`, recompute with `GetOffsets(cachedCell)` | Unchanged (lines 290-320) | Yes |
| `FetchManager.Add(pickupable)`, `Remove(prefab_tag, fetchable_handle)`, `prefabIdToFetchables`, `FetchablesByPrefabId.fetchables` | Postfix and prefix (Harmony binds them by parameter name) | Unchanged (lines 338, 349-367). The only FetchManager change is in `IsFetchablePickup`. | Yes |
| `Pickupable.cachedCell`, `UpdateCachedCell` | The cell checked; assumes a cell change calls `GetOffsets` | `Pickupable.OnCellChange` is now an `EventSystem` subscription (event 1088554450) rather than a `CellChangeMonitor` handler. `UpdateCachedCell` still sets `cachedCell` and calls `GetOffsets(cachedCell)` (lines 952-963), and it is the only writer (958). The new `KObject.cachedCell` is a separate field. | Yes |
| `KCompactedVectorBase.handles` / `dataHandleIndices`, `HandleVector.versions` / `Items` / `UnpackHandleUnchecked` (by name) | Rebuilding handles | Unchanged. `KCompactedVector` gained an assertion in `Free`. | Yes |

**Runtime check:** with `ONI_TWEAK_VERIFY=1`, `fast-offset-tables: N updates, M items recomputed,
0 stale items not marked`. Dig next to stored items to make `OnCellChanged` fire, and confirm
the items are marked and recomputed.

## off-world-fallers

`src/OniMoono.GameTweaks/OffWorldFallers.cs`. Verdict: **unchanged**. Klei did not fix the
endless fall.

| Game member | How the tweak uses it | U60 change | Still valid |
|---|---|---|---|
| `GravityComponents.FixedUpdate` | Transpiler: the one `Grid.IsValidCellInWorld` call becomes `InWorld(...)`. Anchors are the first `GetPosition(Transform)` and `GetGroundOffset` calls (each followed by a local store) and the load before `ldfld mayLeaveWorld`. | Lines 44-150. The fake-floor test moved into the new `IsLandableFakeFloor` (Unity null check, same result), and `cell2` was renamed `cell`. The leave-the-world test (line 139) and its `continue` are unchanged. U60 IL: `GetPosition` then `stloc.s 4`, `GetGroundOffset` then `stloc.s 9`, `ldloc.3` before `ldfld mayLeaveWorld`, and one `IsValidCellInWorld`. | Yes |
| `GravityComponent.onLanded` / `velocity` / `mayLeaveWorld` / `GetGroundOffset` | Read in `InWorld` | Unchanged | Yes |
| `FallerComponents.OnLandedAction` (by name), `OnLanded` → `RemoveGravity` | The item landing that the tweak calls | Line 8 is unchanged. `RemoveGravity` (lines 123-140) was reorganised: the solid-change partitioner entry now uses a static `OnSolidChagedDispatcher` with the GameObject as context, rather than a per-call closure. Its effect is unchanged: gravity removed, and a below-cell listener only if that cell is valid, which it is not below the grid. The faller's cell-change handler moved from `CellChangeMonitor` to the KObject's `EventSystem`, and an object that no longer moves sends no events either way. | Yes |
| `Grid.IsValidCellInWorld`, `IsValidCell`, `PosToCell(Vector2)` | Called | Decompiler restyling only (single return expressions). `PosToCell(KMonoBehaviour)` now uses `KObject.cachedCell`, but the tweak does not call it. | Yes |

**Runtime check:** load Gluttony and look for `[engine] off-world-fallers: N objects landed below
their worlds` (about 17,500 in U57). `GravityComponents.FixedUpdate` should drop back to a few
hundred live fallers, and `GameComps.Gravities` should no longer hold the diamonds and egg
shells under the two rocket interiors.

## fast-particle-collision

`src/OniMoono.Host/GameTweaks/FastParticleCollision.cs`. Verdict: **unchanged**.

| Game member | How the tweak uses it | U60 change | Still valid |
|---|---|---|---|
| `HighEnergyParticle.CheckCollision` | Transpiler: one `GameObject.GetComponent<HighEnergyParticle>()` and one `GameScenePartitioner.GatherEntries(int, int, int, int, ScenePartitionerLayer, List<ScenePartitionerEntry>)` | Lines 266 onwards. The pooled list is now `ListPool<ScenePartitionerEntry, FunctionScope>`, and the locals were renamed. The same overload is called with `(x-1, y-1, 3, 3, collisionLayer)` at line 297, and `GetComponent<HighEnergyParticle>()` at line 301. The merge test (`isCollideable`, circle `Intersects` both ways) is unchanged. U60 IL: one of each. | Yes |
| `HighEnergyParticle` ctor, `isCollideable` (by name) | Constructor postfix; field read | Unchanged (field at line 151). `HighEnergyParticleConfig` still adds one `HighEnergyParticle`. | Yes |
| `KCollider2D.partitionerEntry` / `cachedExtents` (by name), `Intersects(Vector2)` | Reads the registration; assumes `cachedExtents` is what is registered while the entry is valid | `MarkDirty` still sets both together (lines 60-77). `OnSpawn` now calls `OnMovementStateChanged(true)` if the object is already moving, and `OnMovementStateChanged(false)` now calls `MarkDirty()` after leaving the scheduler. Both only update the registration more often, so the invariant holds. | Yes |
| `KCircleCollider2D.Intersects` | Called (virtual) | Restyled only | Yes |
| `GameScenePartitioner.GatherEntries`, `ScenePartitioner.GatherEntries`' overlap test, `ScenePartitionerEntry.obj` | Gather made only when needed; field read in verify mode | Unchanged | Yes |

**Runtime check:** with `ONI_TWEAK_VERIFY=1`, fire radbolts across each other and past stored
items. Look for `fast-particle-collision: … 0 wrong skips` and `… 0 mismatches`.
