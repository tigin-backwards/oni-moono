# Idle state-machine and component updates (2026-11-09)

Minecraft's Lithium puts block entities to sleep while nothing they read changes. This census
asks whether the same would pay in Oxygen Not Included: how many of the updates that
`StateMachineUpdater` runs in buckets every tick, 33 ms, 200 ms, 1000 ms and 4000 ms change
nothing at all, and which could be skipped exactly. Measurement only.

## Method

Linux player, release, default tweaks, speed 3, autosave off, the second launch in its run
home, on a late-game colony (cycle 17,000) and a mid-game colony (cycle 2,000). A scratch mod,
not committed, replaces `StateMachineUpdater.BucketGroup.AdvanceOneSubTick`'s loop with the same
calls in the same order, `UpdateBucketWithUpdater<T>.Update` replicated step by step.

- **Timing** (300 s): each bucket call timed, nothing else. All the ms below come from here.
- **Census** (900 s, the game runs at 5-9 fps meanwhile): up to 24 entries of each update kind
  (rate, bucket, update method) are watched on every update. Before and after the update the
  probe copies the raw bytes of the instance, its GameObject, every component on it and the
  objects their references reach to depth 3 (400 objects, 1 MB), and compares them. It also
  compares the current state and `gotoId`, and counts calls made during the update to
  `EventSystem.Trigger`, every `SimMessages` method, chore creation and about 60 other
  side-effect methods (status items, operational flags, storage, partitioner, conduits,
  accumulators, effects, modifiers, tags, anims, sounds, scheduler, instantiate and destroy,
  ReportManager). An update is **idle** when none of these changed. 11.7 and 10.7 million
  updates were observed; the 224 and 478 that a garbage collection interrupted were dropped.

Limits: writes deeper than depth 3, into arrays over 64 KB, into statics or into native memory
(FMOD, the sim outside SimMessages) are not seen. Single-entry system updaters (sound manager,
temperature and pressure vulnerability) are therefore not counted as idle below. Duplicant
monitors often hit the object budget; the candidates below rarely did (0-11%).

## Results

| | Late game | Mid game |
|---|---|---|
| update kinds | 513 | 484 |
| bucket updates, ms a frame (51 / 38 fps) | 7.00 | 6.41 |
| of it the brain scheduler (never idle) | 2.25 | 1.73 |
| kinds at least 95% idle | 271 kinds, 1.79 ms | 238 kinds, 1.14 ms |
| ms × idle share, all kinds | 2.48 | 1.73 |
| idle updates × their own µs | 1.30 | 0.93 |

A third of the bucket time goes to updates that change nothing, but it is spread thin: the idle
calls cost 0.05-0.4 µs each, across about 270 kinds. Idleness is sticky: an idle entry is idle
again on its next update 95-100% of the time.

The busiest kinds are not idle: the brain scheduler, navigators (19% idle), amount updates
(0%), sublimation (13%), conveyor arms (0%) and the KComponents step.

Idle kinds whose inputs change through events, late / mid game:

| Update | ms a frame | idle | Reads | Would need |
|---|---|---|---|---|
| RequireInputs.Sim200ms | 0.32 / 0.18 | 100% | consumer connected and powered, conduit input connected and satisfied | a change signal from the circuit manager and conduit flow |
| RanchStation FindRanachable | 0.155 / 0.105 | 100 / 95% | the station's room and its critters | room creature-list and ranchable state changes |
| EggProtectionMonitor find_egg | 0.106 / 0.03 | 99% | every egg in the world | eggs added, removed or moved |
| ChoreDriver (has / no chore) | 0.10 / 0.10 | 89-100% | whether it is a live Duplicant | nothing for critters: the update does nothing for them |
| Door blocked checks | 0.077 / 0.033 | 100% | objects in the door's cells | objects entering or leaving those cells |
| Electrobank | 0.067 / 0.053 | 97-100% | a timer that clamps at 10 s | asleep once clamped, until power is drawn |
| CreatureCalorieMonitor | 0.061 / 0.038 | 100% | metabolism attribute total | the attribute's dirty event |
| SolidTransferArm (render) | 0.083 / 0.026 | 95% | arm target rotation | the arm moving |
| CritterEmoteMonitor cooldown | 0.038 / 0.031 | 100% | its cooldown dictionary | empty dictionary |

Idle but not trackable: critter temperature (87%; reads the critter's temperature, which the sim
changes nearly every update) and gas/liquid exposure (reads the cell above).

## Conclusion

**No general sleeping mechanism.** Its ceiling is about 1.3-2.5 ms a frame late game and
0.9-1.7 ms mid game, but per-kind input tracking would cost about what the tiny idle updates
cost. The kinds above add up to at most 1.0 ms (late) and 0.6 ms (mid); after tracking, about
0.5-0.8 and 0.3-0.5 ms, 2-4% of a frame.

**Worth a prototype**, in this order: RequireInputs (power-only buildings need only the circuit
manager's change), FindRanachable and EggProtectionMonitor (scans of several µs per call, so
tracking is cheap by comparison), and ChoreDriver on critters (an exact no-op). Each must wake
on the same sub-tick the game would have seen the change: no temperature or building check may
run late.
