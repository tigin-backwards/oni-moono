# U60 review: overlay tweaks

`fast-overlays` and `fast-overlay-textures` against U60 (761015, public test), from Klei's
decompiled game code (U59 = U59-744825, U60 = U60-761015 in oni-archives, paths under
`raw/Assembly-CSharp/`) and the U60 assemblies. Static review and a U60 compile only: no player ran.

Short version: Klei's U60 rewrite of the overlays does what each part of both tweaks did, or
removes what it patches.

- Before this review, turning `fast-overlays` on under U60 would have stopped the game
  starting. `AccessTools.Method(Mode, "AddTargetIfVisible")` throws AmbiguousMatchException
  (U60 has four overloads), and the type initializer throws (the
  `OnTemperatureOverlayInfraredUpdate` delegate and the `layerTargets` field ref).
- Both tweaks now check for the U59 shape of each target. On U60 they install nothing and log
  why.
- They are retire candidates, to retire after the U60 perf measurement.

## `fast-overlays`

| Part | Game members touched | U60 change | Verdict | Runtime check on a U60 player |
|---|---|---|---|---|
| Power | `OverlayModes.Power.Update` (prefix, finalizer); `CircuitManager.GetWattsNeededWhenActive` (prefix, postfix); assumes Power.Update asks each circuit once per building | Power.Update now collects the targets' `IBridgedNetworkItem`s and fills a pooled `Dictionary<ushort, CachedNetwork>` once per circuit before tinting (OverlayModes.cs U60 2320 `CachedNetwork`, 2510–2530; U59 asked per building at 2449). GetWattsNeededWhenActive itself now uses pooled sets and dictionaries (CircuitManager.cs U60 622–700), with the same results | Retire candidate. Klei now caches per circuit; the part would never hit a kept answer. Left out when `OverlayModes.Power+CachedNetwork` exists | Log has `fast-overlays: power left out`; the power overlay's frame time with the tweak on equals off |
| Temperature (infrared) | `Infrared.Update` (prefix); `TemperatureOverlayInfraredVisualizerBase.OverlayInfraredUpdateDispatcher` (static field replaced by reflection); `OnTemperatureOverlayInfraredUpdate` (delegate); `TemperatureOverlayInfraredUpdate` (delegate); assumes every visualizer subscribes to Game event -880408538 and tests its transform against the view bounds | Visualizers no longer subscribe to Game's event. They join a `visibleVisualizers` set on their controller's `ControllerVisibilityChanged` (6106694, KBatchedAnimController.cs U60 932; TemperatureOverlayInfraredVisualizerBase.cs U60 73). `Infrared.Update` calls `UpdateAllVisible`, which colours only those (Infrared.cs U60 105; base U60 127). The colour is kept per visualizer (memo, base U60 146–158). The dispatcher field and `OnTemperatureOverlayInfraredUpdate` are gone (U59 base 11, 35) | Retire candidate. Klei now skips out-of-view visualizers without reading them, which is what this part did. Left out when the field or the handler is missing | Log has `fast-overlays: temperature left out`; temperature overlay frame time on equals off |
| Targets (materials, decor, plant, pipe, conveyor, power, automation, exosuit) | Transpiler on `ConduitMode/Crop/Harvest/Decor/Logic/Power/SolidConveyor/Suit.Update`, `TileMode.Update`, `TileMode.TryAddObject` rerouting calls to `Mode.AddTargetIfVisible<T>` (7 params, ICollection), `Mode.RemoveOffscreenTargets<T>` (4 params, ICollection), `Mode.UpdateHighlightTypeOverlay<T>`; reimplements those and `ClearOutsideViewObjects`; calls `Mode.ResetDisplayValues(KBatchedAnimController)`; prefix on `TileMode.TryAddObject` reading `TileMode.layerTargets` as `HashSet<PrimaryElement>` | All targets are `HybridListHashSet<T>`, walked as lists. `AddTargetIfVisible` is split into four overloads; the core tests the extents and then `targets.Contains` before the fog-of-war walk (OverlayModes.cs U60 2255–2308). `RemoveOffscreenTargets` / `RemoveAndResetOffscreenTargets` test `Extents.From(item)` and no longer read transforms or the world id (U60 2035–2101). `UpdateHighlightTypeOverlay` is gone (U59 2133), replaced by `RemoveOffscreenAndUpdateHighlights`, one walk that removes and highlights (U60 2124). `TileMode.TryAddObject` is gone (U59 3475); its replacement `AddPrimaryElement` tests `layerTargets.Contains` first (U60 3620–3622), as the tweak's TileMode prefix did. The tweak's unused KPrefabID lookup is gone from the game's RemoveOffscreenTargets | Retire candidate. Its targets are gone, and Klei's rewrite dropped the allocations, the second walk and the late Contains. Left out unless all U59 helpers (with ICollection parameters), `TryAddObject` and a `HashSet<PrimaryElement>` `layerTargets` are present. The transpiler now throws when a patched method has no call to reroute, instead of patching nothing | Log has `fast-overlays: targets left out`. Then profile `RemoveOffscreenAndUpdateHighlights` and `TileMode.Update`: each still reads `transform.GetPosition()` and `TryGetComponent<KBatchedAnimController>` for every target every frame. If they are hot on a large base, the kept-row idea (fixed buildings' positions and controllers) could be ported to them |
| Verify report | `OverlayScreen.ToggleOverlay(HashedString, bool)` (prefix, ONI_TWEAK_VERIFY only) | Signature unchanged (OverlayScreen.cs U60 180); modes are registered in OnPrefabInit now, and LateUpdate skips `None` | Unchanged; patched only when some part installed | — |

## `fast-overlay-textures`

| Tweak | Game members touched | U60 change | Verdict | Runtime check on a U60 player |
|---|---|---|---|---|
| `fast-overlay-textures` | `MultipleRenderTargetProxy.CreateRenderTarget` (prefix, postfix), `MultipleRenderTargetProxy.Update` (postfix); reads `Textures`, `TexturesCopies`; assumes CreateRenderTarget makes new copies each call and drops the old ones undestroyed | CreateRenderTarget now calls `RecreateCopyRT`, which keeps a copy whose size, format and depth still match. Otherwise it hands the old copy to `RenderTextureDestroyer` (`DestroyRenderTexture`, destroyed one or two LateUpdates later), and it destroys `Textures[2]`/`TexturesCopies[2]` when the overlay buffer goes off (MultipleRenderTargetProxy.cs U60 39–57, 79–101; U59 39, 44, 53 made `new RenderTexture` each time). A new `OnDestroy` frees them all (U60 104–119) | Retire candidate. Klei fixed the leak. The tweak would only race RenderTextureDestroyer to destroy the same textures. Left out when `RecreateCopyRT` exists | Log has `fast-overlay-textures: left out`. 100 temperature overlay toggles keep live render textures flat (vanilla U60) |

## Out of scope, needed elsewhere

- `scripts/qa.sh` (verify step) expects reports from `fast-overlays` (its power, kept positions
  and kept targets parts) and from `fast-overlay-textures`. On U60 neither logs a verify report,
  so the step would fail for them until the tweaks are retired or dropped from that list.
- `docs/TWEAKS.md`, `docs/PERFORMANCE.md` and `GameTweakNames` describe U57/U59 behaviour.

## Commits

- `tweaks: leave the overlay tweaks out where U60 does their work`
- `docs: U60 review of the overlay tweaks`


## Outcome

Both tweaks are removed on the `u60` branch (their sources, registry rows, QA verify checks and
user docs); U59 keeps them on master until U60 ships.
