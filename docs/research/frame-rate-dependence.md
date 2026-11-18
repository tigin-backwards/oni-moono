# Frame-rate and game-speed dependence (2026-11-17)

A sweep of U59 744825's code for behaviour that changes with frame rate or game speed beyond what
game time explains: the same colony at speed 3 or at a low frame rate ends up different from
speed 1 at a high frame rate. Code study only; nothing measured, no runq jobs. The model case is
the geothermal vent (`fix-geothermal-vents`, [TWEAKS.md](../TWEAKS.md#the-geothermal-vent-fix)).
Beetas and other attacking critters not targeting Duplicants at speed 3 are studied separately
and left out here. U60 status is from the oni-archives member index, U59-744825 against
U60-763325.

"Read" means the code path was followed end to end. "Inferred" means the mechanism was read but
the size of the effect is worked out on paper, not measured.

## How game time reaches the game's code

The game has four clocks, and they disagree whenever a frame is long.

| Clock | What it is | Who runs on it |
| --- | --- | --- |
| Sim sub-ticks | `Game.SimEveryTick`: `Time.deltaTime` capped at **0.2 s a frame**, one sub-tick per 1/60 s, remainder kept; a step with time every 12 sub-ticks | `ISim*` and state-machine `SIM_*` updates (always their nominal dt), `GameClock`, so `GameScheduler`/`ScheduleGoTo`; Navigator moves (`SIM_EVERY_TICK`), `Work` (`SIM_33ms`), gravity (`Sim33ms`), conduits, power |
| Scaled frame time | `Time.deltaTime` = real delta clamped to `maximumDeltaTime` (1/3 s) × speed, so **up to 1 s a frame at speed 3**; `Time.time` sums it | Animations and their events (`AnimEventManager`, `KBatchedAnimUpdater`), the logic clock (`LogicCircuitManager.Refresh` from `Game.Update`), gameplay in `MonoBehaviour.Update`, `StateMachine.Instance.timeinstate`, every `Time.time` timestamp, `FixedUpdate`, `WaitForSeconds` |
| Real time | `Time.unscaledDeltaTime`, `realtimeSinceStartup` | `RENDER_*` state-machine updates and `IRender*` (`Game.LateUpdate` passes `unscaledDeltaTime`), `WorldDamage`'s leak window |
| Frame count | once per frame | `BrainScheduler` (brains per frame), `GameScheduler.Update` dispatch, `WorldInventory` (one tag per frame), `CellChangeMonitor` |

The sim buckets are exact: `StateMachineUpdater.AdvanceOneSimSubTick` calls each bucket with its
nominal dt, and the sub-tick count follows game time, so a building emitting a fixed amount per
`Sim200ms` call emits the same per game second at any frame rate. The cases below are where code
on one clock acts on state kept by another.

**When the scaled clock outruns the sim.** Once a frame's `Time.deltaTime` passes 0.2 s, the sim
gets 0.2 s and the rest is dropped, but animations, the logic clock and `Time.time` get all of
it. The ratio R = `Time.deltaTime` / sim time per frame:

| Speed | R = 1 down to | R = 1.5 | R = 2 | R = 3 | Most R |
| --- | --- | --- | --- | --- | --- |
| 3 | 15 fps | 10 fps | 7.5 fps | 5 fps | 5 (3 fps and below) |
| 2 | 10 fps | 6.7 fps | 5 fps | 3.3 fps | 3.3 |
| 1 | 5 fps | — | — | — | 1.67 (3 fps and below) |

The original reaches this on large colonies at speed 3 (it runs a late-game colony at about 2.3×
on Windows, about 11 fps capped, R ≈ 1.3). Moono's higher frame rates already keep it out of this
regime more often than the original, so Moono is closer to the high-fps behaviour, not further.

## Ranked cases

### Changes outcomes

| # | Case | What goes wrong, when | Magnitude | Basis | U60 | Moono tweak | Fix idea | Per-frame cost |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Animation clock uncapped: `AnimEventManager.Update` (`Global.Update`, `Time.deltaTime`), `KBatchedAnimUpdater.LateUpdate`; every phase gated on an animation ending (`OnAnimQueueComplete`, `StandardWorker.Work`'s `PendingCompletion` waiting for `IsStopped`, non-looping Navigator transitions ending in `TransitionDriver.OnAnimComplete`) | Long frames (table above): animations play R× faster than the sim, so work post-animations, jumps, ledge climbs, eating and critter pre/post animations take less game time; Duplicants and critters get more done per game second | A 1 s animation takes 0.4-0.6 game s at speed 3 and 5 fps, 0.8-1.0 at 10 fps (frame rounding, case 9, partly cancels it there) | Read; size inferred | Not changed | `fast-anim-events` mirrors the loop (a fix must go there too) | **A** (below): cap `Time.deltaTime` at the sim's 0.2 s | Zero |
| 2 | Logic clock uncapped: `LogicCircuitManager.Refresh(Time.deltaTime)` ticks every 0.1 s of scaled time; `LogicGateBuffer`/`LogicGateFilter` delays and `LimitValve` resets count ticks, while `LogicTimerSensor` runs on `Sim33ms` | Long frames: buffer and filter delays last 1/R of their game time; circuits mixing timers and gates drift; a 10 s buffer driving a pump pumps 1/R as long | Speed 3, 5 fps: a 10 s buffer gives 3.3 game s | Read | Not changed | None | **A** | Zero |
| 3 | `Overheatable+StatesInstance.TryDoOverheatDamage`: `SIM_4000ms` check, damage when `Time.time` is 7.5 s past the last | Long frames: overheated buildings take damage faster and break sooner | ×min(R, 1.875): +50% at speed 3, 10 fps; +87.5% at 7.5 fps and below | Read | Not changed | None | **A** | Zero |
| 4 | `ScaldingMonitor+Instance.TemperatureDamage` (scalding and cold damage): `SIM_1000ms`, 10 HP when `Time.time` is over 5 s past the last hit | Long frames: Duplicants in scalding or freezing cells lose health faster | Hit every 6 game s at R = 1; 4 s at R = 1.5 (×1.5); 2 s at R ≥ 3 (×3) | Read | Not changed | None | **A** | Zero |
| 5 | `Klei.AI.SlimeSickness` cough: `SIM_4000ms`, cough when `Time.time` is 20 s past the last | Long frames: sick Duplicants cough, and spread germs, more often | Every 24 game s at R = 1; 16 s at R = 1.5; 8 s at R = 3 | Read | Not changed | None | **A** | Zero |
| 6 | `ExternalTemperatureMonitor` reads `CreatureSimTemperatureTransfer.LastTemperatureRecordIsReliable`: more than 5 samples in the last 4 s of `Time.time`, sampled in `Update` at most every 0.2 s | Speed 3 below 3.75 fps only (speeds 1 and 2 cap a frame at 0.33 and 0.67 s): a frame advances `Time.time` by more than 0.8 s, so 4 s never holds 6 samples; Duplicants are never Too Hot or Too Cold (no WarmAir/ColdAir effects, no recovery errands) | All-or-nothing below the threshold | Read | Not changed | None | **A** | Zero |
| 7 | `BipedTransitionLayer.EndTransition`: Athletics experience = `Time.time` spent in the transition | Long frames: walking earns Athletics R× faster per game second | ×R | Read | Not changed | None | **A** | Zero |
| 8 | `DissolvingElementDiseaseEmitter.MakeDiseaseAndBubbles` (`DissolvingAlgae`, algae in lit water): mass × `Time.deltaTime` in `Update` | Long frames: algae dissolves into oxygen bubbles R× faster | ×R | Read | Not changed | None | **A** | Zero |
| 9 | Animation ends seen on frame boundaries: `AnimEventManager.Update` tests `currentFrame` computed **before** adding dt, so an animation ends on the first frame after its last frame is reached, and its state moves on in the next frame's sim | Any speed, worse at high speed and moderate frame rates (no cap needed): each animation-gated step lasts about 0.5-1.5 frames of game time longer than at high fps (the range depends on whether `Global.Update` runs before or after `Game.Update`, not checked) | Speed 3 at 30 fps (0.1 game s a frame): +0.05 to 0.15 s per step, +10-30% for a 0.5 s animation; at 20 fps +0.08 to 0.23 s; speed 1 at 60 fps: +0.01 to 0.03 s | Read; size inferred | Not changed | `fast-anim-events` mirrors it | **D** (below), opt-in | Zero |
| 10 | Logic ticks back to back: when a frame covers more than 0.1 s of scaled time (speed 3 below 30 fps, speed 2 below 20, speed 1 below 10), `Refresh` runs 2+ `PropagateSignals` in one call, before the frame's sub-ticks | A network state lasting one tick (0.1 s) can begin and end inside one call: event-driven consumers see both edges, but no sub-tick runs in between, so a pump, door or valve switched on for one tick does nothing. Pulse-driven builds meter differently at speed 3 | Share of one-tick states the sim never sees ≈ 1 − 0.1/(scaled frame time): 33% at speed 3 and 20 fps, 50% at 15 fps, 67% at 10 fps | Read; share inferred | Not changed | None | **B** (below), opt-in | Zero (same ticks, moved) |
| 11 | Other `Time.time` thresholds: `timeinstate` in `ExternalTemperatureMonitor` (6 s), `PressureMonitor` (3 s), `BurrowMonitor`, `SweepStates` (16 s), `SweepBotReactMonitor`, `RocketSelfDestructMonitor`, `FoodFightEvent` (30 s); `CreatureCalorieMonitor`/`BeehiveCalorieMonitor` minimum time before pooping (10 s), `PoopStates` cooldown, `CallAdultMonitor`, `SeedPlantingMonitor`, `ClimbableTreeMonitor`, `Sleepable` wake delay, `SuitMarker`/`MaskStation` 2.8 s and `StandardWorker` 10 s fallbacks, `MinimumOperatingTemperature` 5 s hysteresis, `SlipperyMonitor` | Long frames: each wait lasts 1/R of its game time | Small one by one | Read | Not changed | None | **A** | Zero |
| 12 | `WorldDamage`: a liquid leak through a damaged tile (1 kg) at most once per **real** second per cell (`realtimeSinceStartup`) | Speed-dependent at any frame rate: at speed 3 a tile can leak a third as often per game second as at speed 1 | ×1/speed, only while damage events come faster than once a second per cell | Read | Not changed | `low-fps-timing`'s leak part (built, below) | Use `GameClock` time instead of real time | Zero |

### Only lags, or display only

| # | Case | What happens | Basis | U60 | Moono tweak | Fix idea |
| --- | --- | --- | --- | --- | --- | --- |
| 13 | `BrainScheduler.RenderEveryTick`: 1 Duplicant and 5 critter brains per frame | Decisions come later in game time at low fps and high speed; with thousands of critters at low fps, critters stop excreting (changes outcomes) | Read (known) | `Brain.UpdateBrain` changed, scheduler count not | `brain-rate`, `critter-needs-first`; attacking critters at speed 3 are with another agent | — |
| 14 | `GameScheduler.Update` once per frame (`ScheduleGoTo`, `Schedule`) | A callback fires on the first frame after its time: up to one frame of game time late; chains of short `ScheduleGoTo` loops (0.2-3 s) stretch by about half a frame per step | Read; size inferred | Changed to double time (case R1), dispatch still per frame | None | Not worth it alone |
| 15 | Real-time retargets: `RENDER_1000ms` in `BreathMonitor.UpdateRecoverBreathCell`, `HeatImmunityMonitor`/`ColdImmunityMonitor` shelter cells, `BionicMassOxygenAbsorbChore` safe cell, `FoodFightEvent` check; `GlobalChoreProvider.Render200ms` (storage-fetchable tags), `Clearable.Render1000ms` | Once a real second, so 3 game s apart at speed 3; they run while paused | Read | Not changed (`BreathMonitor` reworked, still `RENDER_1000ms`) | `instant-reachability` covers its own rechecks only | — |
| 16 | `WorldInventory.Update`: accessible amount of one tag per frame | With hundreds of tags, a tag's amount is refreshed every few hundred frames; `TinkerStation`, `BuildingDef` placement checks and status items read it | Read | Changed (cell lookup), still one tag a frame | None | — |
| 17 | `BuildingElementEmitter.UnsafeUpdate` reads `simData.emittedMassEntries` in `Sim200ms`, as U59's vent did | Above 60/120/180 fps at speed 1/2/3 the read often sees nothing: the flow-rate accumulator and the oxygen report undercount. Display only: the emitter's sim-side output is right | Read (TWEAKS.md says so) | Not changed | `sim-step-pace` removes the zero-time steps that cause it | Read after each step, as `fix-geothermal-vents` does |
| 18 | `LogicMassSensor.Update` (weight plate) with a 0.15 s `Time.deltaTime` cooldown; `FallingWater.UpdateParticles(Time.deltaTime)`; `CreatureSimTemperatureTransfer`'s shown transfer rate | Per frame; falling liquid lands R× sooner; display | Read | Not changed | None | **A** covers the scaled part |

## Fixes

- **A. Keep the scaled clock with the sim.** In `Time.BeginFrame` (OniMoono.UnityEngine), while
  a colony runs, clamp `Time.deltaTime` to 0.2 s, the cap `Game.SimEveryTick` applies (declared
  as a mirror of that method so an update that changes the cap turns it off). Animations, the
  logic clock, `Time.time` and `FixedUpdate` then advance exactly as far as the sim each frame.
  Below the cap nothing changes, so the result matches speed 1 at a high frame rate. Fixes cases
  1-8, 11 and the scaled part of 18. It changes outcomes against the original only on long
  frames: opt-in, Changes gameplay group. Cost: one `min` a frame. Check before landing: code that
  divides by `Time.deltaTime` (FMOD listener velocity, `FlowOffsetRenderer`) and visuals that
  would slow to the sim's pace, which is what the player already sees of the world.
- **B. Logic ticks inside the sub-tick loop.** Move the logic clock's tick test from
  `LogicCircuitManager.Refresh` into `Game.SimEveryTick`'s loop, one tick per 6 sub-ticks, so each
  tick's state gets its sub-ticks. Fixes case 10, and with A, case 2. Same number of ticks a frame.
  Opt-in: it changes the order of logic and sim within a frame, and pulse timing against the
  original. Topology refreshes (`conduitSystem.IsDirty`) stay per frame.
- **C. Scheduler time as double (U60's fix, case R1 below).** Opt-in bug fix like
  `fix-geothermal-vents`. Needs a double-keyed queue beside `FloatHOTQueue` with prefixes on
  `Scheduler.Schedule`, `Update`, `Clear` and `SchedulerHandle.TimeRemaining`; cost as the
  original's.
- **D. End animations on the frame they end.** Test the frame index after adding dt in
  `AnimEventManager.Update` (and its mirror in `fast-anim-events`). Removes one of the two frames
  of case 9; the other is the sim reading the new state next frame, which is inherent. The last
  frame of a non-looping animation would no longer be held for a frame, a visible change, so
  opt-in and low priority.
- `WorldDamage` (case 12): `GameClock` time instead of `realtimeSinceStartup`. Low priority
  (built 2026-11-18 as a third part of `low-fps-timing`, below).

**Recommended:** A first (eight outcome cases, no cost, one place), then B for automation
builds, then C as a U60 backport. D and case 12 only if someone reports them.

## Built (2026-11-18)

A and B, and later case 12, are the parts of one opt-in tweak, `low-fps-timing` ("Accurate timing at low FPS"),
in the Changes gameplay group, on master and u60
([TWEAKS.md](../TWEAKS.md#accurate-timing-at-low-fps)). They landed as two tweaks the same day
and were merged into one row before any release: one switch for every fix of this kind, each
part checking for other mods and standing down on its own (`low-fps-timing: PART off (...)` in
Player.log). Case 12 joined it as its third part; later fixes from this study (case 9, steps
ending on frame boundaries) would join it as parts too, not rows. Its in-game text names the
kind of problem it fixes, not each part, so a new part needs no new text.

- **A: the game time cap** (`FrameTimeCap`). A postfix on `Game.SimEveryTick` arms a one-frame cap in
  `Time.BeginFrame` (`Time.CapNextFrame`, through `OniMoono.Host.GameTimeCap`): the next frame's
  scaled `deltaTime` is at most 0.2 s, SimEveryTick's own `Mathf.Min(dt, 0.2f)` (declared as a
  mirror). The sim consumes exactly `min(deltaTime, 0.2)` a frame (sub-ticks of 1/60 s, the
  remainder under 1/60 s carried in `simDt`, the rest dropped), so the capped `deltaTime` equals
  what the sim takes, bit for bit. Frames at or under the cap are untouched. Only frames after
  one that ran SimEveryTick are capped: the main menu and loading never are. `Time.time`,
  `smoothDeltaTime`, `timeSinceLevelLoad` and the FixedUpdate steps follow; `unscaledDeltaTime`
  and `realtimeSinceStartup` stay real. Paused frames have no game time either way. Not changed
  by this part: case 9 (frame rounding) and case 12 (real time, its own part below). FMOD's listener velocity and
  `FlowOffsetRenderer` divide by `deltaTime`: a smaller divisor on long frames only, where the
  world already moves at the sim's pace.
- **B: automation ticks** (`LogicTicks`). Not one tick per 6 sub-ticks as first proposed (that changes the
  tick count when a frame passes the cap, and the tick phase): Refresh's own loop counts the
  frame's ticks, as many as the game's with the same `elapsedTime` left, and only where they run
  changes. The first tick of a frame runs where the game runs it; each later one before the
  first sub-tick at or after its moment in the frame, after the previous tick's sub-tick and
  leaving one for each tick after it. A frame with one tick or one sub-tick at most runs exactly
  as the game's (unit tests against copies of Refresh and SimEveryTick). It shares
  SimEveryTick's loop with `sim-step-pace`.
- **Case 12: leak timing** (`LeakTiming`). A transpiler on `WorldDamage.ApplyDamage` and one
  on `WorldDamage.Update` swap their `Time.realtimeSinceStartup` read (one in each) for game
  time: `GameClock`'s `timeSinceStartOfCycle + cycle × 600` in double (as U60's
  `GetTimeAsDouble`), counted from the session's first reading and stored as float.
  `GetTime` is a float on U59 and U60 alike and steps by 1/8 s at cycle 2,000; counted from the
  session it stays as fine as `realtimeSinceStartup`. A cell then leaks at most once a game
  second at any speed (at speed 1 as before, in game-time terms; at speed 3 three times as
  often per game second as without it). Every caller of `ApplyDamage` runs on the sim's updates
  or its damage reports, so nothing damages tiles while paused, and the window waiting with the
  paused clock changes nothing. Only liquid pressure and comets pass a source cell, so digging
  never leaks. Off when another owner patches either method other than with a postfix
  (`low-fps-timing: leak timing off (...)`): the clock reads real time again and the window's
  entries are dropped once. Unit tests against a copy of the window (`LeakTimingTests`); no
  game run.

Counts on a Linux lane (speed 3 at 5 fps against speed 1 at 220 fps, TWEAKS.md has the tables):
`Time.time` 3.0 → 1.0 s per sim second, automation ticks 30 → 10 a second, a 10 s Buffer Gate
3.3 → 10.0 game s, overheat hits 0.23 → 0.12 a second (target 0.12), scald hits 0.49 → 0.18
(0.17), a 1 s animation 0.41 → 1.20 game s a play (0.97; the extra 0.2 s is case 9). One-tick
signals the sim never sees: 33% → 0 at 20 fps, 83% → 0 at 5 fps. At 60 fps no frame was capped
and no tick moved.

## Related findings that are not frame rate

- **R1. Scheduler and `GameClock.GetTime` are floats** (fixed in U60). `GetTime` returns
  `timeSinceStartOfCycle + cycle * 600` as a float, and `Scheduler` keys its queue on it. From
  cycle ~7,000 a float holds that time to 0.5 s, from cycle ~14,000 to 1 s, from ~28,000 to 2 s.
  `Scheduler.Update` returns early while the time has not changed (`previousTime == time`), so at
  cycle 17,000 every `ScheduleGoTo` and `Schedule` callback fires on whole seconds and short waits
  round to 0 or 1 s. U60 made the clock, `Scheduler`, `SchedulerEntry` and `SchedulerHandle`
  double (`GetTimeAsDouble`). Other `GetTime` users (reaction cooldowns, report times) keep the
  float in both versions.
- `GameClock.AddTime` computes the previous block as `FloorToInt(timeSinceStartOfCycle - dt / 25f)`
  (precedence), so `GameHashes.NewBlock` fires on every `Sim33ms` instead of every 25 s.
  `ConduitSleepMonitor` re-evaluates 750 times as often; its answers are the same. Unchanged in
  U60.
- `PlantBlightEvent` saves a `Time.time` start time, meaningless after a load; the event is
  defined but never started.

## Checked and ruled out

- Sim buckets and fixed per-call amounts: exact (nominal dt, remainder kept, sub-tick count by
  game time). The 0.2 s cap slows the whole sim evenly.
- Navigator movement (`SIM_EVERY_TICK`, sub-tick dt), `Work` (`SIM_33ms`), gravity and fallers
  (`Sim33ms` via `KComponentSpawn`), reactables (polled per cell transition).
- Random rolls: the per-call ones found are in sim buckets with fixed dt.
- `FallingWater` at large dt: it scans every cell a particle crosses, so nothing tunnels.
- `DissolvingElementDiseaseEmitter` bubble threshold: the whole accumulated mass leaves, nothing
  is lost at large dt (only the rate is case 8).
- Other `simData` snapshot readers: `StructureTemperatureComponents` and `SimTemperatureTransfer`
  read temperatures, not per-step deltas; consumption, emission and disease callbacks are handled
  on every `StepTheSim` call.
- `SolidTransferArm` arm rotation, conveyor item positions, `LaunchPad` tower, `StressShockChore`
  beam, `AirConditioner`/`SteamTurbine` display dt: visual or display.
- `CircuitManager.RenderEveryTick`: rebuilds topology only; power runs in the 0.2 s step.
