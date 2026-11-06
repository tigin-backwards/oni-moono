# Performance mods inventory (2026-10-25)

Every Oxygen Not Included performance mod we could find, grouped by technique, set against what
Moono already does ([PERFORMANCE.md](../PERFORMANCE.md), [TWEAKS.md](../TWEAKS.md),
[overlays.md](overlays.md), [u57-frame-inventory.md](u57-frame-inventory.md)), to find ideas
worth borrowing.

## Method

- **Found through:** Steam Workshop searches for ONI (appid 457140), 49 terms (performance, fps,
  lag, optimiz*, frame rate, stutter, memory, gc, thread, multithread, save, load, cpu, cache,
  性能, 优化, 卡顿, 帧, 多线程…), each sorted by relevance, most recent and most subscribed, two
  pages each: 1,497 items, filtered on titles and descriptions. Cross-checked against
  `scripts/mod-catalog.tsv` and onimaxxing.com/mods (its catalog of 3,031 mods and its per-mod
  code reviews, which list each mod's Harmony targets), and GitHub (the known authors'
  repositories and a search).
- **Read:** Workshop descriptions; onimaxxing's reviews of each mod's current upload (patch targets
  and what the code does); public source where there is some (Fast Track, ONI Perf, oni-gc-runtime,
  Overlay Texture Memory Fix, CycleTrim, GCBudget); the earlier decompile of Colony Performance
  Fixes (`out/local-tools/moddecomp/3823893994`, an older version than the current 1.7.1). No mod
  was downloaded for this survey: of those not already decompiled, only Fast Save and
  Multithreaded Simulation (RC-3.0) are in `scripts/mod-catalog.tsv`, and both are described well
  enough by their source or review.
- **Claims and measurements:** a mod author's figure is a **claim** (their colony, their machine,
  their method); a **measured** figure is ours, from PERFORMANCE.md or the research notes. Moono's
  current frame is about 12.6 ms on U57 at speed 3 with every tweak, on Windows; the
  per-system times below come from there unless marked as the older Linux profile (where the
  game's code ran about half as fast).
- **Exact** means the game's results stay the same (what our `exact` tweaks promise). Anything
  that changes when or how often the game decides something is not exact.

## The techniques

Each entry opens with one of four verdicts: **covered** (Moono does it, or does better), **worth
investigating** (with a plan), **dropped before** (measured, see PERFORMANCE.md "Tried and
dropped") and **not applicable** (the cost doesn't exist in Moono, or the change is gameplay, not
performance).

### Frame pacing and process settings

- **VSync off, target frame rate set (240, 120 or user value)**: covered.
  - Mods: [Unlock FPS](https://steamcommunity.com/sharedfiles/filedetails/?id=3772228364),
    [FPS Limiter](https://steamcommunity.com/sharedfiles/filedetails/?id=3405082060),
    [Performance Assist](https://steamcommunity.com/sharedfiles/filedetails/?id=3772297140).
  - What it changes: `QualitySettings.vSyncCount = 0` and `Application.targetFrameRate`.
  - Exact: Yes, a display setting.
  - In Moono: The player paces by vSync, else by `targetFrameRate`, and `--max-frame-rate` caps
    either (`OniMoono.Host/Player.cs`, `Throttle`). These mods work through the same API.
  - Gain: None to frame time. It caps a menu running at thousands of fps.
- **Process priority High, re-applied every 10 s**: not applicable (the player's choice, not the
  engine's).
  - Mods: Performance Assist.
  - What it changes: OS scheduling.
  - Exact: Yes.
  - In Moono: Not done.
  - Claimed gain: "May reduce stutter" from background contention. Nobody has measured it.
- **Full speed while the window is unfocused**: not applicable (a preference; could be a player
  option).
  - Mods:
    [Colony Performance Fixes](https://steamcommunity.com/sharedfiles/filedetails/?id=3823893994)
    (an option, off by default), Performance Assist (sets `runInBackground` as a side effect).
  - What it changes: Stops the game's sleep while unfocused.
  - Exact: No. The game slows itself on purpose.
  - In Moono: The game's behaviour is kept. PerfProbe turns it off only for measuring.
  - Gain: Not a frame-time gain.
- **Adaptive game speed held to a target fps, benchmark per save**: not applicable (a feature, works
  as a mod).
  - Mods: [Controlled Framerate](https://steamcommunity.com/sharedfiles/filedetails/?id=3670573352).
  - What it changes: `Time.timeScale` every frame.
  - Exact: No, it changes game speed.
- **Unity job workers raised (`job-worker-count` in boot.config)**: not applicable.
  - Mods: [ONI Boost Kit](https://steamcommunity.com/sharedfiles/filedetails/?id=3760687013).
  - What it changes: Unity's native job threads (skinning, culling).
  - Exact: Yes.
  - In Moono: The engine has no Unity native jobs. The game's job threads are covered by
    `fast-jobs`.
  - Claimed gain: No figure given.

### Garbage collection

- **A rebuilt Mono runtime with parallel marking**: not applicable.
  - Mods: [oni-gc-runtime](https://github.com/Salacious-Oni-Mods/oni-gc-runtime), ONI Boost Kit.
  - What it changes: Replaces `mono-2.0-bdwgc.dll`.
  - Exact: Yes.
  - In Moono: Covered by the runtime. CoreCLR's collector runs in parallel on every core and pauses
    about a quarter as long, about 1.4% of a frame (PERFORMANCE "Why the engine alone is faster").
  - Claimed gain: A full collection 220–240 → 30–50 ms on a 2–3.5 GB heap (oni-gc-runtime).
- **Incremental GC (`gc-max-time-slice=3`)**: not applicable.
  - Mods: Colony Performance Fixes (an option, off by default).
  - What it changes: Spreads Boehm's collection over frames.
  - Exact: Yes.
  - In Moono: Covered by the runtime, as above.
  - Claimed gain: GC freezes about 370 → 60 ms.
- **GC latency mode, manual mode with collections on a timer or heap budget, a collection at focus
  loss**: not applicable.
  - Mods: Performance Assist (`SustainedLowLatency`),
    [BetterSave](https://steamcommunity.com/sharedfiles/filedetails/?id=3805601277) (GC disabled, a
    full collection every 10 min), [GCBudget](https://github.com/shikyo13/oni-gc-budget),
    [Clean Memory & Performance Capture](https://steamcommunity.com/sharedfiles/filedetails/?id=3091570212),
    GC Timing (in oni-gc-runtime).
  - What it changes: When collections happen.
  - Exact: Yes.
  - In Moono: The engine maps `GarbageCollector.GCMode` onto CoreCLR (`ManagedHeap`), so these modes
    still do what they ask. What they work around is the collector's pauses.
  - Claimed gain: A 0.5 s collection every 50 s becomes one every 10 min, at about 1 GB more memory
    (BetterSave).
- **The collector held off while a colony loads**: dropped before.
  - Mods: [ONI Perf](https://github.com/Salacious-Oni-Mods/oni-perf-mod) `loadGcHold` (with 24 GB or
    more installed).
  - What it changes: One collection at the end of the load.
  - Exact: Yes, but memory grows.
  - In Moono: Measured and dropped. U57's load pauses 3.2–3.9 s in collections, but neither a larger
    gen0 budget nor the non-concurrent collector loaded faster, and holding collections would keep
    12 GB of 2minnow's garbage (PERFORMANCE "Tried and dropped").
  - Claimed gain: Euphoria (239,000 objects) about 730 → 111 s on Mono.
- **The forced collection after each save skipped**: covered.
  - Mods: BetterSave, Fast Save.
  - What it changes: The `GC.Collect` at the end of `SaveLoader.Save`.
  - Exact: Yes.
  - In Moono: `no-save-gc`.

### Saving and loading

- **Compression and the file write on a background thread**: covered.
  - Mods: [Fast Save](https://steamcommunity.com/sharedfiles/filedetails/?id=1867707267) (Background
    Save, autosaves only), BetterSave.
  - What it changes: `SaveLoader.CompressContents` and the write.
  - Exact: Yes, the file is the same. BetterSave only logs write errors.
  - In Moono: `background-save`, `fast-save-compression`.
  - Measured gain: Autosave freeze 11.7 → 4.8 s with the save tweaks on Linux, 2.3 s on Windows.
  - Claimed gain: 3.6 → 0.56 s on a 103 MB save (BetterSave).
- **Compiled serialization delegates, type names cached as bytes**: covered.
  - Mods: Fast Save (Use Delegates, "may crash"), BetterSave (checked byte for byte against vanilla
    on the first two saves).
  - What it changes: `SerializationTemplate.SerializeData`, `Manager.GetSerializationTemplate`.
  - Exact: Yes.
  - In Moono: `fast-serialization`, `fast-save-type-checks`.
- **A reused save buffer**: worth investigating (small; measure the autosave's allocations first).
  - Mods: BetterSave.
  - What it changes: The `MemoryStream` the save is written into.
  - Exact: Yes.
  - In Moono: To measure, as allocation per autosave on U57.
  - Claimed gain: No figure given.
- **The colony preview: async GPU read-back, PNG encoded in the background**: worth investigating.
  - Mods: Fast Save (`Timelapser.RenderAndPrint`, autosaves).
  - What it changes: When the pixels are read and the PNG written.
  - Exact: Yes, the same image, later.
  - In Moono: Not done. The remaining freeze includes "the colony preview's GPU read-back" (PLAN.md,
    save tweaks).
  - Claimed gain: The preview's stall is about 1.1 s on its first save (BetterSave).
- **The preview captured once a session, then copied**: not applicable (prefer the exact read-back).
  - Mods: BetterSave.
  - What it changes: The save list's thumbnail goes stale.
  - Exact: No, the preview image differs.
  - Claimed gain: The same stall as above.
- **Old daily reports pruned or emptied**: not applicable as a tweak (for the record, measure what
  U57's 17,000 reports cost the save).
  - Mods: Fast Save (Moderate keeps 20, Aggressive 4),
    [RemoveDailyReports](https://steamcommunity.com/sharedfiles/filedetails/?id=1870873514) (keeps
    10).
  - What it changes: The save's report history.
  - Exact: No. Report screens, some achievements and the save's contents change.
  - Claimed gain: Saving and loading "up to 50%" faster late game (Fast Save).
- **Conveyor network rebuild from an index**: covered.
  - Mods: ONI Perf `conveyorPaths`.
  - What it changes: The path search in the `SolidConduitFlow` rebuild.
  - Exact: Yes.
  - In Moono: `fast-conveyor-paths`. ONI Perf's README lists it as its own, and the design is the
    same.
  - Measured gain: A rebuild 50–510 → 2–14 ms, the load's 840–850 → 14 ms.
- **`Type.GetType` cached for the state-machine load; the restore's 400-character type-name strings
  decoded once and shared**: worth investigating (profile U57's load for `StateMachineSerializer`
  and `Type.GetType` first).
  - Mods: ONI Perf `typeNameCache`, `smRestore`.
  - What it changes: The `StateMachineSerializer` restore.
  - Exact: Yes, the same strings and the same choices.
  - In Moono: Not done. The loading side of `fast-serialization` was measured and not kept (31.3 s
    against 31.6, PLAN.md), but that did not cover the state-machine restore.
  - Claimed gain: Part of the default load levers' gain, Some light torture 52 → 43 s with
    `loadGcHold` (ONI Perf).
- **The load spread over frames, far objects built after the loading screen**: not applicable
  (Moono's U57 load is 35 s; not exact).
  - Mods: ONI Perf `fastLoad` (experimental, off by default).
  - What it changes: Object spawn order and timing.
  - Exact: No. Objects far away appear after the colony starts, paused.
  - Claimed gain: Euphoria's window answers after 16.6 s instead of 110 s.

### Overlays

- **Temperature overlay: only what's in view coloured, a quarter a frame, changes of 0.05 K or
  more**: covered.
  - Mods: Colony Performance Fixes.
  - What it changes: `Infrared.Update` and the visualizers' event.
  - Exact: No. Colours come up to four frames late, and small changes aren't shown.
  - In Moono: Covered exactly. `fast-overlays` passes over out-of-view buildings without reading
    them.
  - Measured gain: `Infrared.Update` 10.4 → 6.0 ms on U57.
  - Claimed gain: Overlays 29–41 → 60–95 fps (Colony Performance Fixes).
- **Materials, decor, power and pipe overlays refreshed on camera moves or at 4 Hz; power overlay at
  0.5 Hz**: covered (the throttle itself isn't exact).
  - Mods: Colony Performance Fixes, Clean Memory & Performance Capture.
  - What it changes: The modes' `Update`.
  - Exact: No. Targets, tints and labels can be up to 0.25 s or 2 s old.
  - In Moono: Covered exactly where it paid. `fast-overlays` keeps what the targets' visits read
    (overlays.md).
  - Measured gain: Materials 4.05 → 3.8 ms, decor 4.32 → 3.6–4.0 ms.
- **A circuit's wattage computed once a frame**: covered.
  - Mods: Colony Performance Fixes.
  - What it changes: `CircuitManager.GetWattsNeededWhenActive`.
  - Exact: Almost. The cache is cleared on `Refresh` only.
  - In Moono: Covered exactly. `fast-overlays` keeps it within one `Power.Update`.
  - Measured gain: 0.52 → 0.07 ms.
- **Displaced render targets released when overlays switch the MRT setup**: worth investigating
  (memory, not fps).
  - Mods: [Overlay Texture Memory Fix](https://github.com/pastadiablo/ONI-OverlayTextureMemoryFix).
  - What it changes: `MultipleRenderTargetProxy.CreateRenderTarget` leaves the replaced
    `TexturesCopies` alive, and the fix releases and destroys them.
  - Exact: Yes. It fixes a leak, and nothing reads the old textures.
  - In Moono: Not done when this was written. The engine frees a texture when the game destroys it,
    as Unity does, so the leak should be the same. It has since been done (Shortlist item 9).
  - Claimed gain: 74 MB committed per toggle of the temperature or germ overlay, kept until the main
    menu.

### Animation

- **Idle animations advanced together at about 30 Hz, so their batches upload on fewer frames**:
  worth investigating (count batch uploads a frame at 79 fps first).
  - Mods: Colony Performance Fixes (on by default).
  - What it changes: `KBatchedAnimUpdater.LateUpdate`'s loop.
  - Exact: No. An animation's frame steps shift by up to a frame, and so do its frame events.
  - In Moono: Not done. `fast-anim-batches` and `fast-symbol-overrides` cut the cost of each upload,
    but the number of uploads stays the same. We measured animation updates at 0.9 ms and batch
    uploads at 0.6 ms at 79 fps.
  - Claimed gain: The normal view about 70 → 85–90 fps, with its other fixes.
- **Inactive controllers with no batch taken off the anim updater's list**: worth investigating
  (count such calls on Gluttony's 67 Duplicants).
  - Mods: ONI Perf `parkAnims`.
  - What it changes: `KBatchedAnimController.UpdateAnim`.
  - Exact: Claimed yes, since the calls removed do nothing. The updater's list order changes.
  - In Moono: Not done.
  - Claimed gain: 280 of 646 `UpdateAnim` calls a frame were no-ops, on 200 Duplicants.
- **`CellChangeMonitor.MarkDirty` stops at inactive children without a cell-change handler**: worth
  investigating (count them on Gluttony, with a verify mode).
  - Mods: ONI Perf `markSkip`.
  - What it changes: An inactive laser effect reads "not moving".
  - Exact: Almost. No cell event is lost, but an inactive object's moving state differs.
  - In Moono: Not done.
  - Claimed gain: 73% of transforms marked as moved were switched-off laser effects (14 per
    Duplicant). The saving is 0.20–0.40 ms.
- **Symbol overrides re-derived only when changed; only the changed range uploaded**: covered.
  - Mods: ONI Perf `symbolMemo`, `overrideRange`.
  - What it changes: Per-Duplicant override rebuilds and uploads.
  - Exact: Yes.
  - In Moono: `fast-symbol-overrides`, which doesn't upload unchanged blocks.
  - Measured gain: 1.47 → 1.20 ms.
  - Claimed gain: −0.98 to −2.14 ms and −0.38 to −0.59 ms.
- **Looping animations whose frames are identical played once**: dropped before.
  - Mods: Fast Track `AnimOpts`.
  - What it changes: `KAnimLoopOptimizer`.
  - Exact: No. Anim-complete events and repeating sounds change.
  - In Moono: Reviewed in u57-frame-inventory.md ("Fast Track reviewed").
- **Animation batch cleanup, spatial index cleanup**: covered.
  - Mods: Colony Performance Fixes ("anim batch cleanup", "partitioner cleanup", on by default).
  - What it changes: `KBatchedAnimUpdater` cleanup, `ScenePartitioner`.
  - Exact: Likely the same as ours. Our decompile predates them, so we can't see them.
  - In Moono: `fast-anim-cleanup`, `fast-partitioner-cleanup`.
  - Measured gain: 0.48 → 0.12 ms, Gluttony 0.38 → 0.16 ms.

### Interface and per-frame housekeeping

- **Closed screens put to sleep**: covered.
  - Mods: ONI Perf `screenSleep`.
  - What it changes: Disables closed screens' canvases, toggles, graphs and scroll views.
  - Exact: No, it disables the game's components.
  - In Moono: Covered in the engine. The UI walk, rect masks and raycasts skip closed screens. What
    is left (the toggles' and portraits' scripts, about 0.7 ms on 2minnow) needs disabling game
    components (PERFORMANCE "Tried and dropped").
  - Measured gain: Masks 6.3 → 0.3–0.4 ms on 2minnow.
  - Claimed gain: +17.95 ms a frame after one visit to Consumables.
- **The UI hover raycast reused while the mouse is still (up to 0.1 s), 30 Hz while it moves**:
  covered (what's left isn't exact).
  - Mods: Colony Performance Fixes, ONI Perf `raycastCache`.
  - What it changes: `StandaloneInputModule`'s pointer raycast.
  - Exact: No. Hover state can lag a screen change by 0.1 s.
  - In Moono: Covered in part. The raycast leaves out closed screens' graphics.
  - Measured gain: U57 2.0–2.3 → 0.6–0.9 ms.
  - Claimed gain: About 1 ms each time (Colony Performance Fixes).
- **The hover card rebuilt only when something it shows changes**: dropped before.
  - Mods: ONI Perf `hoverCache`, Fast Track `InfoCardOpts`.
  - What it changes: `SelectToolHoverTextCard`.
  - Exact: No. It shows live mass and temperature.
  - In Moono: Measured at about 0.3 ms.
- **Property textures refilled on a sim tick or view change**: dropped before.
  - Mods: ONI Perf `propTex`, Fast Track `ReduceTileUpdates` (threads it).
  - What it changes: `PropertyTextures.LateUpdate`.
  - Exact: No. Revealed cells and lights change between ticks.
  - In Moono: Measured at 0.5–0.9 ms in all. The engine already skips uploading unchanged rows.
- **Unused cameras off, lerper passes as blits**: dropped before.
  - Mods: ONI Perf `idleCams`, `lerpBlit`.
  - What it changes: The timelapse, sim overlay and cursor cameras, and six texture lerpers.
  - Exact: Yes.
  - In Moono: Measured together under 0.25 ms.
- **Idle trackers, heat effects, workers, outfits and progress bars put to sleep; pipe contents
  cached while paused**: dropped before.
  - Mods: ONI Perf `idleSleep`, `workerSleep`, `barRate`, `pipeCache`, `pausedSkip`.
  - What it changes: Their per-frame updates.
  - Exact: Yes, or they act only while paused.
  - In Moono: Measured at 0.02–0.12 ms each.
- **Steam Input polled at 20 Hz while no pad is in use; Steam callbacks at 30 Hz**: worth
  investigating (time both calls first).
  - Mods: ONI Perf `steamPoll`.
  - What it changes: `SteamInput.RunFrame`, `SteamAPI.RunCallbacks`.
  - Exact: Gameplay is the same. A pad tap under 50 ms can be missed, and Steam callbacks arrive up
    to 33 ms later.
  - In Moono: Not done. The engine calls the real Steam API (ARCHITECTURE, Steamworks.NET), so the
    native cost is the same.
  - Claimed gain: 0.41–0.49 ms and 0.38–0.46 ms a frame.
  - Measured gain: At most 0.3 ms in Moono, since `Global.Update`, which holds the input update, is
    0.3 ms in all on U57.
- **Achievement checks at a fixed rate**: not applicable (too small, and not exact).
  - Mods: ONI Perf `achieveRate`, Fast Track `DisableAchievements`.
  - What it changes: `ColonyAchievementTracker`.
  - Exact: No. An achievement can register seconds later.
  - In Moono: To measure.
  - Claimed gain: −0.10 ms.
- **The audio mixer's visible-Duplicant count at a fixed rate**: not applicable (too small).
  - Mods: ONI Perf `mixRate`.
  - What it changes: `AudioMixer.SetVisibleDuplicants`.
  - Exact: No. Mixer parameters can be up to 1/Hz late.
  - In Moono: To measure.
  - Claimed gain: 0.10 ms with 200 Duplicants.
- **Menu expand animations skipped**: not applicable (and doesn't load: no mod_info.yaml).
  - Mods: [Lightning fast Menus](https://steamcommunity.com/sharedfiles/filedetails/?id=1852077143).
  - What it changes: The `ExpandRevealUIContent` curves.
  - Exact: No, a visual change.

### Simulation and Duplicant logic

- **Brains at the rate the game was tuned for (30 a second of real time), not once per rendered
  frame**: worth investigating (a non-exact tweak).
  - Mods: ONI Perf `brainRate` (off by default), CycleTrim (critters only, at most 5 a frame and 400
    a second).
  - What it changes: `BrainScheduler`'s probe count. The game probes 1 Duplicant brain and 5 critter
    brains per rendered frame, with no `dt` term (`BrainScheduler.cs` in the game, whose constants
    state a 30 fps frame).
  - Exact: No. Chore searches happen less often per real second.
  - In Moono: Not done. Moono runs about 7 times the original's frame rate on U57, so it already
    searches chores about 7 times as often per game second as the original. Capping at 30 a second
    would still search more often than the original does. We measured brains at 1.4 ms of the
    12.6 ms frame on U57, 2.4 ms on Gluttony.
  - Claimed gain: Every lever together, `brainRate` and `moveHold` included, 54.9 → 123.8 fps
    against 77.2 with the defaults (ONI Perf). `brainRate` alone wasn't measured.
- **Busy Duplicants' pickup and chore refreshes throttled; pickup sensor every 3rd brain update;
  every sensor every 2nd**: not applicable on its own (a brain-rate cap, above, covers the same cost
  more uniformly).
  - Mods: [CycleTrim](https://steamcommunity.com/sharedfiles/filedetails/?id=3807166689),
    [Lag Fix](https://steamcommunity.com/sharedfiles/filedetails/?id=3781498375).
  - What it changes: `PickupableSensor.Update`, `Sensors.UpdateSensors`, chore searches.
  - Exact: No. New errands and items are noticed brain updates later.
  - In Moono: Not done. Sensors' scans measure 0.064 ms a frame on U57 (u57-frame-inventory.md), and
    fetch updates 0.5 ms with `fast-offset-tables`.
  - Claimed gain: Brain time 5.1 → 2.4 s, fps 70.6 → 85.1 (CycleTrim).
- **Stationary critters' path probes throttled; repeated async probes deduplicated**: not applicable
  (would only save worker-thread time).
  - Mods: CycleTrim.
  - What it changes: `Navigator.UpdateProbe`, `AsyncPathProber`.
  - Exact: No. Eight skips, then a forced refresh.
  - In Moono: Covered in effect. The path prober runs on a separate thread and takes about 0.02 ms
    of the main thread to hand out its work (PERFORMANCE "Where a frame goes now").
  - Claimed gain: Navigator hot path 827 → 48 ms in total, fps 81 → 109.
- **Pickup candidate costs cached in `UpdatePickups`**: not applicable.
  - Mods: CycleTrim, Fast Track `FastUpdatePickups`.
  - What it changes: `FetchManager.FetchablesByPrefabId.UpdatePickups`.
  - Exact: Claimed yes for CycleTrim.
  - In Moono: Reviewed. It runs on the game's worker jobs already.
  - Claimed gain: 41.3 → 35.7 µs a call (CycleTrim).
- **Nav-grid links rebuilt in parallel for batches of 64 cells or more**: dropped before.
  - Mods: Lag Fix.
  - What it changes: `NavGridUpdater.UpdateLinks`.
  - Exact: Yes.
  - In Moono: Measured and dropped. Each update is about 40 µs, too small for a fork and join
    (PERFORMANCE "Tried and dropped"), and big batches are rare.
- **The round-robin nav-grid update restored**: not applicable.
  - Mods: Lag Fix (experimental, off by default).
  - What it changes: `Pathfinding.UpdateNavGrids`.
  - Exact: No.
- **Reachability checked only when something nearby changed**: covered.
  - Mods: Fast Track `FastReachability`.
  - What it changes: `ReachabilityMonitor`.
  - Exact: No. It uses a prober of its own that refreshes every 4 s.
  - In Moono: `instant-reachability` (on by default), which rechecks only what changed, with the
    game's own check: the answers are the game's, up to a second sooner. It replaced
    `slow-reachability` (every 4 s instead of every second).
  - Measured gain: 1.88 → 0.53 ms.
- **A walking Duplicant stays "moving" between sim sub-ticks**: not applicable.
  - Mods: ONI Perf `moveHold` (off by default).
  - What it changes: The stopped event is delayed by up to 0.1 s.
  - Exact: No.
  - Gain: Not measured on its own.
- **Drained generators skipped when circuits draw power**: worth investigating (read the game's
  `PowerFromGenerator` for side effects of a zero draw).
  - Mods: Colony Performance Fixes ("skip drained generators", on by default; not in our decompile).
  - What it changes: `CircuitManager.PowerFromGenerator`, `Sim200msLast`.
  - Exact: Unknown until read.
  - In Moono: Not done. Power costs 1.6 ms for 109 generators (older Linux profile).
- **Rocket condition results cached**: worth investigating (time it on U57's rockets first; drop if
  under 0.1 ms).
  - Mods: Colony Performance Fixes ("rocket status cache", on by default).
  - What it changes: `CraftModuleInterface.EvaluateConditionSet`.
  - Exact: Unknown.
  - In Moono: To measure.
  - Gain: None claimed.
- **A rewritten, multithreaded native simulation**: not applicable (outside the engine; the wait
  bounds any gain at about 1 ms on U57).
  - Mods:
    [Multithreaded Simulation (RC-3.0)](https://steamcommunity.com/sharedfiles/filedetails/?id=3779276157)
    (Rust, [source](https://github.com/KeringFa/ONI-SimDLL_Rust)),
    [Multi threaded simulation](https://steamcommunity.com/sharedfiles/filedetails/?id=3563131181)
    (C++), CycleTrim's OniSim (Rust, on by default in 0.4.0).
  - What it changes: Replaces SimDLL, which simulates heat, gas, liquid and elements.
  - Exact: No for the C++ one, whose author says results differ. OniSim claims identical final
    states on its test fixtures, untested in real games.
  - In Moono: Moono runs Klei's SimDLL. On U57 the main thread waits on it about 1.0 ms a frame.
  - Claimed gain: CPU −9% to −52% per scene on fixtures (OniSim). ONI Perf claims 54.94 against
    54.91 fps with a faster sim.
- **Duplicants twice as fast, tasks twice as fast, more food**: not applicable (and fails to load on
  this build).
  - Mods: [Better Performance](https://steamcommunity.com/sharedfiles/filedetails/?id=2785899934).
  - What it changes: Gameplay tuning, so lower speeds suffice.
  - Exact: No.

### Fast Track's other options

[Fast Track](https://github.com/peterhaneve/ONIMods) (0.18.5 beta, MIT) read option by option
from its source, against the decompiled game. AnimOpts, FastReachability, FastUpdatePickups,
ConduitOpts, FastCellChangeMonitor and the visual and feature switches were reviewed before
(u57-frame-inventory.md). One correction there: FastUpdatePickups also replaces
`FetchListStatusItemUpdater.Render200ms` on the main thread, and not exactly (it sums
`TotalAmount` where the game sums `FetchTotalAmount`, and drops the game's skip of objects with no
world), so the status items it sets can differ.

As written, none of these options keeps the game's results, except SensorOpts, which never
takes effect in this build. The entries that point at an exact version Moono could write say so.

- **PickupOpts, ChorePriorityMode**: not applicable.
  - What it changes: Duplicant brains' fetch updates run on worker threads during
    `World.LateUpdate`. `BrainScheduler` is replaced, anim-complete triggers are deferred and
    priority brains limited.
  - Exact: No. Brain order and timing change, and the fetch list loses the game's de-duplication.
  - Moono cost: Brains 1.4 ms, fetch updates 0.5 ms (`fast-offset-tables` covers part).
- **ENetOpts**: not applicable (a building check).
  - What it changes: `CircuitManager.Refresh` and `Sim200msLast` replaced, battery drains batched.
  - Exact: No. Power values and unpowered flags differ, and a building counts as powered at 0 J
    where the game allows 0.01.
  - Moono cost: Not measured.
- **FlattenAverages**: worth investigating (an exact one-pass irrigation update in the game's float
  order; first split the 1.8 ms between the event dispatch and the handler).
  - What it changes: Amounts' batch update runs on separate jobs, and accumulators start at NaN.
    `IrrigationMonitor.UpdateIrrigation` is rewritten as one pass (`PlantElementPatches.cs:109`).
  - Exact: No. Averages differ in the first 3 s, and the irrigation rewrite multiplies in another
    float order, which can flip its threshold.
  - Moono cost: Plant element absorbers 1.8 ms (older Linux profile), mostly the storage-change
    event into irrigation monitors.
- **FastAttributesMode**: worth investigating, low priority (an exact side table keeping the game's
  checks).
  - What it changes: Dictionaries for attribute levels and converters. The experience code in
    `StandardWorker.Work` is rewritten.
  - Exact: No. It skips the `maxAttributeLevel == 0` check, so some Duplicants gain experience, and
    it matches by Id instead of by reference.
  - Moono cost: Part of workers doing work, 1.6 ms (older Linux profile).
- **CritterConsumers**: not applicable.
  - What it changes: `GasAndLiquidConsumerMonitor.FindTargetCell` as a best-cost query.
  - Exact: No, it picks other target cells.
  - Moono cost: Part of critter brains.
- **SensorOpts**: not applicable.
  - What it changes: `SafeCellQuery.IsMatch` stops once every flag is found.
  - Exact: Yes, but the path finder already stops at such a cell, so it never fires.
  - Moono cost: None.
- **LogicUpdates**: not applicable.
  - What it changes: Comet detectors and smart reservoirs send logic signals only on change.
  - Exact: No. Handlers of a same-value send stop running.
  - Moono cost: Tiny.
- **MiscOpts**: worth investigating (measure first).
  - What it changes: Suit markers at 200 ms, incremental achievement checks, the `Chatty` tag moved,
    `WorldInventory.Update` skipped while paused, and small caches.
  - Exact: Mostly no.
  - Moono cost: Not measured. Two parts deserve a stopwatch.
    `ColonyAchievementTracker.RenderEveryTick` checks one unfinished achievement a frame, and some
    walk every building (U57 has 62,000). In `SuitMarker.Update` each checkpoint finds its lockers
    every frame, and caching them, invalidated on building changes, would be exact.
- **ReduceColonyTracking**: worth investigating (measure first; only exact speed-ups of single
  trackers would do).
  - What it changes: `TrackerTool` 50 → 10 world trackers a frame, `ChoreDriver` reports every 1 s.
  - Exact: No.
  - Moono cost: Not measured. Some trackers walk every network or chore.
- **AllocOpts, MinimalKCV, CustomStringFormat**: not applicable.
  - What it changes: Fewer allocations, through pooled lists, `Cmps` add and remove, and Ryu number
    formatting.
  - Exact: No. Lists are shared, and the `Components` list order and number text change.
  - Moono cost: GC pressure, which matters under Mono's Boehm collector.
- **ParallelInventory**: not applicable.
  - What it changes: `WorldInventory.Update` runs on a worker thread, with some tags left out.
  - Exact: No.
  - Moono cost: Not measured.
- **BackgroundRoomRebuild, DecorProvider patches**: not applicable (rooms are building checks).
  - What it changes: Rooms are flood-filled in the background, and decor refreshes queued.
  - Exact: No, room timing changes.
  - Moono cost: Not in the profile.
- **RadiationOpts**: not applicable.
  - What it changes: Radiators emit in slices once a second.
  - Exact: No. Emission timing and random draws change.
  - Moono cost: Radbolts are covered by `fast-particle-collision`.
- **FastStructureTemperature**: not applicable (temperature).
  - What it changes: `StructureTemperatureComponents.Sim200ms` rewritten.
  - Exact: No, temperature values change.
  - Moono cost: Not in the profile.
- **CachePaths, AsyncPathProbe**: not applicable.
  - What it changes: CachePaths skips path probes if no nav cell nearby changed for 6 s.
    AsyncPathProbe runs probes on Fast Track's jobs.
  - Exact: No for CachePaths, timing only for AsyncPathProbe.
  - Moono cost: The path prober is on a separate thread, 0.02 ms of the main thread.
- **ReduceSoundUpdates**: not applicable.
  - What it changes: Ambience and mix at 200 ms, looping sounds every other frame.
  - Exact: No, audio timing changes.
  - Moono cost: Not in the profile.
- **InfoCardOpts, SideScreenOpts, VirtualScroll**: not applicable (exact clip culling in the engine
  would be the way for long lists).
  - What it changes: Hover selection, side screens and scroll lists rewritten.
  - Exact: No. Order, refresh rates and row activation change.
  - Moono cost: The hover card about 0.3 ms, the rest only while open.
- **MeshRendererOptions**: not applicable (Moono records draws itself).
  - What it changes: Persistent mesh renderers in place of `Graphics.DrawMesh`.
  - Exact: Visual differences only.
  - Moono cost: Unity's per-call draw cost.
- **RenderTicks**: not applicable.
  - What it changes: Light trackers at 200 ms, so Lux lags. Sublimation effects run only in view,
    which changes instance ids.
  - Exact: No.
  - Moono cost: Small.
- **LoadOpts**: not applicable (slow reflection under Mono, cheap on .NET).
  - What it changes: The async loader scans two assemblies, and event hashes are folded in.
  - Exact: Only start-up changes, but mods' loaders are skipped.
  - Moono cost: Start-up only.

### Diagnostics only

Not techniques, listed for completeness: [Performance Observer](https://steamcommunity.com/sharedfiles/filedetails/?id=3800923048)
(系统性能观察器; Harmony timing probes on 54 game methods and a panel), Clean Memory &
Performance Capture's CSV capture, Fast Track's `Metrics`, shikyo13's oni-profiler.

## The mods

Last updated is the Workshop's time or the latest GitHub release or push; the build is the
`minimumSupportedBuild` of the current upload. The current game is U59, build 744825. Each entry
opens with the mod's state.

- **Colony Performance Fixes 1.7.1 (Saito)**: current.
  [Workshop 3823893994](https://steamcommunity.com/sharedfiles/filedetails/?id=3823893994); updated
  2026-10-24, build 469369. It isn't in our catalog (too new), and our decompile is of an older
  version.
- **CycleTrim 0.4.0 (LIghtJUNction)**: early development.
  [Workshop 3807166689](https://steamcommunity.com/sharedfiles/filedetails/?id=3807166689),
  [source](https://github.com/LIghtJUNction/OniMods/tree/main/mods/CycleTrim); updated 2026-10-21,
  build 525812. By default it maps SimDLL to a Rust core of its own.
- **BetterSave v2.10.0 (更好的存档)**: current.
  [Workshop 3805601277](https://steamcommunity.com/sharedfiles/filedetails/?id=3805601277); updated
  2026-09-29, build 719533.
- **Performance Observer 0.2.0-dev (系统性能观察器)**: development release, diagnostics.
  [Workshop 3800923048](https://steamcommunity.com/sharedfiles/filedetails/?id=3800923048); updated
  2026-09-27, build 744825.
- **Multithreaded Simulation RC-3.0 (多线程模拟)**: current, Windows only.
  [Workshop 3779276157](https://steamcommunity.com/sharedfiles/filedetails/?id=3779276157); updated
  2026-09-06, build 744825. It is catalog rank 524 and failed to load in the original in our sweep
  (MOD_COMPATIBILITY.md).
- **Performance Assist 1.1.4**: current.
  [Workshop 3772297140](https://steamcommunity.com/sharedfiles/filedetails/?id=3772297140); updated
  2026-08-29, build 493472.
- **Unlock FPS 1.0.1**: current.
  [Workshop 3772228364](https://steamcommunity.com/sharedfiles/filedetails/?id=3772228364); updated
  2026-08-29, build 493472.
- **Lag Fix (Fetch/Sensor/NavGrid) 1.0.0**: current.
  [Workshop 3781498375](https://steamcommunity.com/sharedfiles/filedetails/?id=3781498375); updated
  2026-08-11, build 744825.
- **ONI Boost Kit 1.1.0**: current.
  [Workshop 3760687013](https://steamcommunity.com/sharedfiles/filedetails/?id=3760687013); updated
  2026-07-09, build 736649. On Windows, a hand-run installer replaces the Mono runtime.
- **Controlled Framerate 1.2.1**: current.
  [Workshop 3670573352](https://steamcommunity.com/sharedfiles/filedetails/?id=3670573352); updated
  2026-04-30, build 700386.
- **Fast Save 3.11 (Peter Han)**: current.
  [Workshop 1867707267](https://steamcommunity.com/sharedfiles/filedetails/?id=1867707267); updated
  2026-04-01, build 719533. It is catalog rank 37 and works in Moono (MOD_COMPATIBILITY.md).
- **Multi threaded simulation 0.4.1 (R9MX4)**: outdated.
  [Workshop 3563131181](https://steamcommunity.com/sharedfiles/filedetails/?id=3563131181); updated
  2025-10-25, build 600112. Its sim library (October 2025) replaces this build's July 2026 SimDLL.
- **RemoveDailyReports 4.0.1**: current.
  [Workshop 1870873514](https://steamcommunity.com/sharedfiles/filedetails/?id=1870873514); updated
  2025-01-31, build 651155.
- **FPS Limiter ("Help! My GPU screams at the main menu!")**: works.
  [Workshop 3405082060](https://steamcommunity.com/sharedfiles/filedetails/?id=3405082060); updated
  2025-01-11, build 648807. It only sets two values.
- **Clean Memory & Performance Capture**: outdated.
  [Workshop 3091570212](https://steamcommunity.com/sharedfiles/filedetails/?id=3091570212); updated
  2023-12-12, build 509600. It patches `EnergyInfoScreen`, which is gone in this build, so it fails
  to load.
- **Better Performance**: outdated.
  [Workshop 2785899934](https://steamcommunity.com/sharedfiles/filedetails/?id=2785899934); updated
  2022-03-28, build 467401. It throws while mods load.
- **Lightning fast Menus**: outdated.
  [Workshop 1852077143](https://steamcommunity.com/sharedfiles/filedetails/?id=1852077143); updated
  2019-09-02, no build given. It has no mod_info.yaml, so it isn't loaded.
- **Fast Track 0.18.5 beta (Peter Han)**: current beta, GitHub only.
  [GitHub](https://github.com/peterhaneve/ONIMods/releases/tag/FastTrackBeta); updated 2026-08-31,
  build U59. It works in Moono (MOD_COMPATIBILITY.md). ONI Perf reports it stalls on the original at
  this build without ONI Perf's `cpuFix`.
- **ONI Perf 0.3.0**: current, GitHub only (no Workshop item found).
  [GitHub](https://github.com/Salacious-Oni-Mods/oni-perf-mod); updated 2026-10-12, build 744825.
- **oni-gc-runtime 0.1.0**: alpha, Windows.
  [GitHub](https://github.com/Salacious-Oni-Mods/oni-gc-runtime); updated 2026-10-12, build 744825.
  It replaces the Mono runtime.
- **Overlay Texture Memory Fix**: current, GitHub only.
  [GitHub](https://github.com/pastadiablo/ONI-OverlayTextureMemoryFix); updated 2026-08-28, build
  U59-744825.
- **GCBudget 1.0.0**: GitHub only. [GitHub](https://github.com/shikyo13/oni-gc-budget); updated
  2026-09-13, build 526233.

Not performance mods despite their names: Optimized Digging, the 优化… building mods, Touch
Optimization, Smoother Light, Smooth Borders, Lagoo.

## Shortlist

Ranked by expected value in Moono. Each one starts with a measurement. Since this was written,
9 is done; 1 is the `brain-rate` tweak (changes gameplay, off by default); 2, 3, 4, 5, 7 and the
achievement tracker, `TrackerTool` and `SuitMarker` of 8 were measured and dropped
([PERFORMANCE.md](../PERFORMANCE.md#tried-and-dropped) has the numbers).

1. **Brains at 30 a second of real time** (non-exact, from ONI Perf `brainRate`). The game updates
   one Duplicant brain and five critter brains per rendered frame, whatever the frame rate. Moono
   runs U57 about 7 times as fast as the original (78–84 against 11.4 fps), so it already does
   about 7 times the original's chore searches per game second. Brains cost 1.4 ms of U57's
   12.6 ms frame and 2.4 ms on Gluttony. Held at 30 a second, they would run about 60% less often
   at 79 fps and still more often than in the original. Measure first: brain updates a second and
   `BrainScheduler.RenderEveryTick` time at speed 3 on U57 and Gluttony, then the same with the
   probe count scaled to a real-time allowance (ONI Perf's "spread" form, which keeps the frames
   even). This changes when Duplicants decide, so it needs the user's call on whether it belongs
   with the timing tweaks (like `brain-rate`) or not at all.
2. **Switched-off effects left out of the cell-change and animation lists** (ONI Perf `markSkip` and
   `parkAnims`, both on by default there, near exact). Every Duplicant carries 14 laser-effect
   children, almost always off, which are still marked dirty whenever it moves and then put on the
   animation updater's list for calls that do nothing. ONI Perf claims 73% of moved transforms and
   43% of `UpdateAnim` calls on 200 Duplicants. The cell-change monitor and animation updates are
   among Moono's largest remaining costs. Measure first: on Gluttony (67 Duplicants), how many
   `MarkDirty` transforms are inactive and have no cell-change handler, and how many `UpdateAnim`
   calls reach an inactive controller with no batch. A verify mode would check that every skipped
   call really did nothing.
3. **The autosave's colony preview read asynchronously** (exact, from Fast Save's Background Save).
   The remaining autosave freeze includes the preview's GPU read-back (PLAN.md). Reading the same
   pixels a few frames later and encoding the PNG in the background gives the same image. BetterSave
   claims about 1.1 s for that stall on the first save. Measure first: how much of U57's 2.3 s
   freeze is `Timelapser`'s capture, read-back and encode.
4. **Steam polled less often while no pad is in use** (from ONI Perf `steamPoll`; gameplay is the
   same, Steam callbacks arrive up to 33 ms later). The engine calls the real Steam API, so
   `SteamInput.RunFrame` and `SteamAPI.RunCallbacks` cost what they cost in the original, claimed
   0.4–0.5 ms each. The bound in Moono is lower: `Global.Update`, which holds the input update, is
   0.3 ms in all on U57. Measure first: a stopwatch around both calls on Windows.
5. **The state-machine load's strings and types cached** (exact, from ONI Perf `typeNameCache` and
   `smRestore`). The restore decodes and compares the same few hundred 400-character type names over
   a million times on a big colony. Our earlier load-side serialization work didn't cover this.
   Measure first: the share of U57's 35 s load spent in `StateMachineSerializer` and
   `Type.GetType`.
6. **Idle animations advanced on a shared 30 Hz beat** (non-exact, from Colony Performance Fixes).
   Animations step at 30 fps, but each one steps on its own frame, so at 79 fps some batch
   re-uploads on almost every frame. Stepping still animations together would upload most batches
   on fewer frames, at the cost of shifting their frames and frame events by up to one animation
   frame. Measure first: batches uploaded a frame on U57, and how many of them hold only idle
   controllers.
7. **An exact one-pass irrigation update** (from Fast Track FlattenAverages). Plant element
   absorbers cost 1.8 ms in the older Linux profile, and `IrrigationMonitor.UpdateIrrigation` scans
   storage once per consumed element and again for wrong elements. Measure first: how much of that
   is the handler's body rather than the event dispatch.
8. **Smaller, measure-first items:** `ColonyAchievementTracker.RenderEveryTick`, `TrackerTool.Update`,
   `SuitMarker.Update` (exact caching of attached lockers), and Colony Performance Fixes' "skip
   drained generators" (`CircuitManager.PowerFromGenerator`) and "rocket status cache"
   (`CraftModuleInterface.EvaluateConditionSet`). Each is worth a stopwatch on U57 and nothing more
   unless it shows 0.3 ms or more.
9. **The overlay render-target leak** (exact, from Overlay Texture Memory Fix). This saves memory,
   not frame time: a claimed 74 MB per temperature or germ overlay toggle, kept until the main
   menu. Measure first: GL texture memory after toggling the temperature overlay 100 times in
   Moono. If it grows, release the targets the game displaces, as the mod does.

   Done (2026-10-25): it grows in the original and in Moono alike, about 18 MB a toggle at
   1024×768; the `fast-overlay-textures` tweak frees them (PLAN.md).
