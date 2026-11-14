# The hitch when a rocket launches or lands (2026-11-14)

A Spaced Out rocket's launch and landing each carry one long frame. The questions: how long,
where the time goes, whether the original has it, and what could shorten it. A study: nothing
was built beyond probes.

**Verdict.**
- **Two frames a flight, both the game's pipe network rebuild.** The frame in which the rocket
  is launched and the frame in which it touches down each rebuild every pipe network of one
  kind in the colony. On a late-game colony on Windows that is 14–25 ms in Moono, in a frame
  of 29–38 ms against a steady 9–14 ms. Nothing else in the sequence stands out: the countdown,
  the modules moving, leaving the world and the descent run at the steady frame.
- **The original has the same two frames, longer.** Its rebuild takes 39–62 ms there, in frames
  of 120–168 ms against a steady 77–81 ms. Moono's hitch is shorter in milliseconds and larger
  against its own frame (2.0–4.4 times the steady frame, the original's 1.5–2.2 times), which
  is why it is noticed.
- **Why a launch rebuilds pipes.** `RocketModule` removes each module's pipe ports from the
  pipe networks when the rocket leaves (`BuildingConduitEndpoints.RemoveEndPoint`) and adds
  them when it lands (`AddEndpoint`). Any change to a network, a port included, marks the whole
  network manager dirty (`UtilityNetworkManager.RemoveFromNetworks`, `AddToNetworks`), and the
  next `Game.LateUpdate` rebuilds all of that kind's networks. It is the same rebuild as after
  building one pipe.
- **Nothing of ours to fix.** Moving the modules costs 0.02–0.14 ms a frame. No first-use
  stall of the engine showed at ignition or lift-off on OpenGL or on Vulkan. The first launch
  of a session is slower than the second by about 10 ms in the rebuild frame, in the game's
  code. Nothing landed.
- **One frame is unexplained.** One of two Windows sessions had a single 392 ms frame a second
  into lift-off, with 633 MB allocated on the main thread. A traced repeat with the launch 20 s
  later did not have it, at lift-off or anywhere else, and the Linux session did not either.
  See [the unexplained frame](#the-unexplained-frame).
- **What would remove it** is an exact game tweak that skips the rebuild when the port that
  changed touches no pipe, as a rocket module's ports usually don't. It needs a proof first
  that such a rebuild changes nothing. See [candidates](#candidates).

## Method

- **Colony:** U57, the late-game Spaced Out colony (cycle 17,077, 26 rockets, three of them
  ready on their pads). The two launched: 12 modules each, a Robo-Pilot, a hydrogen engine,
  liquid fuel and oxidizer tanks, cargo bays. Speed 3, autosave off.
- **Probe:** PerfProbe's `jank.txt` recorder (the frame pacing study's, local), with commands
  for rockets (`Rocket.cs`, local branch `rockets-probe`, not committed). `launch` calls
  `Clustercraft.Launch`, as the launch button does once the checks pass. `land` puts the rocket
  in flight over its pad's hex with the pad as its destination, so the game lands it at its
  next 4 s check: the landing is the game's, the flight is skipped. Each frame records its real
  time, the rocket's status and launch state, and brackets on the update entry points and on
  the rocket's methods. The brackets cost Moono 5–8% of its frame rate.
- **Scenario:** 30 s after the colony is up, 8 s of steady frames with the camera on the pad,
  launch, 3 s in flight, landing, then the same for a second rocket.
- **Windows** (the numbers): Moono u60 24101dbf, release, default tweaks, second launch in its
  run home, Vulkan; the original 763023. 1280×720 windowed, unfocused, vsync off, High
  priority. One session each, and one traced Moono session.
- **Linux** (the split): one session under Xvfb on Mesa's Direct3D 12 layer (OpenGL), with
  `perf record -e cpu-clock:u -g` at 999 Hz on the monotonic clock, its samples cut at the
  recorder's frames.
- **The Windows sessions were disturbed.** Other processes used a median 11% of all cores
  during Moono's session (90th percentile 36%, peak 71%) and 15% during the original's (90th
  percentile 27%). Moono's steady frame went from 8.6 ms before the first launch to 14.3 ms
  before the second. So the time lost over a whole sequence is not given: it is within that
  drift. The two rebuild frames are single frames with a bracket of their own and stand clear
  of it.

## Timeline

Windows, speed 3, measured. Each cell is the stage's worst frame in ms; for the stages that last
(countdown, lift-off, descent) the median frame is given too, as "median / worst".

| Stage | Lasts | Moono, first launch | Moono, second | Original, first | Original, second |
|---|---|---|---|---|---|
| Steady, before the launch (median) | | 8.6 | 14.3 | 76.9 | 81.1 |
| **Launch: the command and the pipe rebuild** | 1 frame | **38.3** | **32.3** | **168.0** | **122.4** |
| Countdown, engine burning on the pad | 2.6 s (ours) | 7.7 / 26.1 | 14.2 / 36.8 | 95.9 / 143.1 | 81.5 / 149.6 |
| Lift-off, the modules moving | 5.6–5.9 s | 13.2 / 60.8 (and one of 391.8) | 14.3 / 40.6 | 90.9 / 145.2 | 91.8 / 180.7 |
| Leaving the world (modules off the grid) | 1 frame | 16.7 | 25.3 | 93.4 | 99.1 |
| Landing starts (modules placed on the pad) | 1 frame | 19.4 | 11.6 | 104.3 | 85.4 |
| Descent | 9.9 s (ours) | 12.7 / 41.6 | 14.3 / 43.4 | 79.6 / 143.8 | 83.6 / 165.6 |
| **Touchdown: the ports added, the pipe rebuild** | 1 frame | **37.5** | **28.9** | **120.3** | **133.2** |

The worst frames of the lasting stages are the game's 200 ms sim-step frames, the same as in
the steady span before (worst 32.5 and 37.6 ms in Moono, 153.1 and 134.9 in the original).

The two rebuild frames, measured:

| | Moono, first | Moono, second | Original, first | Original, second |
|---|---|---|---|---|
| Launch frame over the steady frame | 29.7 ms (4.4×) | 18.0 ms (2.3×) | 91.1 ms (2.2×) | 41.3 ms (1.5×) |
| · the rebuild (`Game.LateUpdate` over its median) | 24.7 | 14.1 | 61.8 | 38.7 |
| · `Clustercraft.Launch` itself | 2.6 | 0.7 | 7.5 | 1.9 |
| Touchdown frame over the steady frame | 28.9 ms (4.3×) | 14.6 ms (2.0×) | 43.4 ms (1.6×) | 52.1 ms (1.6×) |
| · the rebuild | 21.3 | 13.5 | 40.1 | 51.9 |
| Time lost a flight (the two frames) | 59 ms | 33 ms | 134 ms | 93 ms |

Linux (one session, steady frame 19–21 ms): launch frames of 62.4, 39.9 and 36.0 ms (first,
second, third launch), touchdown frames of 39.8 and 33.6 ms.

## Where the time goes

Linux, the five rebuild frames together (191 main-thread samples, 1 ms each; measured):

| Part | ms a frame | Whose |
|---|---|---|
| The frame's usual work | 18.2 | |
| `ConduitFlow.RebuildConnections`: every pipe added to the flow arrays again | 8.0 | game |
| `UtilityNetworkManager.RebuildNetworks`: the flood fill over every pipe cell | 4.8 | game |
| The pipes' "requires building intake" refresh, through `fast-pipe-rebuilds` | 4.2 | game's check, our dispatch |
| `ConduitFlow.BuildNetworkTask`: the flow order | 2.2 | game |
| Other | 0.8 | game |

About 20 ms of rebuild a frame on Linux. `fast-pipe-rebuilds` already takes the handlers from
every pipe to the rebuilt kind's pipes; without it this part is larger
([TWEAKS.md](../TWEAKS.md)). These rockets have liquid ports only; a rocket with gas ports
too would rebuild both kinds in the same frame (not measured).

The rest of the sequence, Linux brackets, measured:

- The launch loop with the module positions and world damage (`LaunchLoop`,
  `UpdatePartsAnimPositionsAndDamage`, `DoWorldDamage`): 0.09–0.12 ms a frame; the landing
  loop 0.06–0.08 ms. `Game.LateUpdate` and the anim update are the same while the modules move
  as in the steady span (4.2–4.9 against 4.1 ms, 2.2–2.6 against 2.1 ms).
- Leaving the world (`FinalizeLaunch` with `MoveToSpace` for 12 modules): 0.6–1.2 ms.
- Landing starts (`OnClusterDestinationReached` with `DoLand` and `MoveToPad`): 1.1–2.0 ms.
- `SetCraftStatus`: 0.2–0.5 ms. Logic ports, the surface reveal and the status items: under
  0.15 ms each.

**First launch against second.** The first launch's rebuild frame is longer by about 10 ms on
Windows (24.7 against 14.1 ms of rebuild, 2.6 against 0.7 ms in `Launch`) and by about 12 ms
on Linux. The JIT accounts for 3.4 ms of it (31 methods; 2.9 ms and 11 methods in the second).
The rebuild's methods run seldom, so they are still at a low JIT tier when the first launch
comes (`RebuildConnections` shows as quick-jitted code with an on-stack replacement in all
three Linux launches). The original's first rebuild is slower than its second too (61.8 against
38.7 ms). The two rockets differ in their cargo bays, so this is one rocket against another, not a
repeat of the same one.

### The unexplained frame

Moono's first Windows session had one frame of 391.8 ms 3.6 s after the launch, as the rocket
passed 1 cell of height: 633 MB allocated on the main thread, a generation 0 collection with a
66.7 ms pause, 36 ms in the canvas update, and about 290 ms outside every bracket. A repeat
was run for it, with an EventPipe trace and the launch 20 s later, to tell a cost of the
lift-off from one of the session's clock: the frame was not there, at the lift-off or in any
600-frame window of that session, and neither was it in the Linux session or in the first
session's second launch. So it is not a cost of every first launch, and its cause is not
known. The trace of the repeat did not load (the player is stopped by its run home, which cuts
the file short), which did not matter once the frame was absent.

## Candidates

For the game's cost; none built. Exact means the same results in the same order, standing down
when a mod patches the methods.

| # | Candidate | Exact | Would remove | Basis | Risk |
|---|---|---|---|---|---|
| 1 | **No rebuild for a port that touches no pipe.** `RemoveFromNetworks` and `AddToNetworks` for an endpoint whose cell holds no pipe of that kind leave every network as it is; skip marking the manager dirty | Only if a rebuild with nothing changed leaves the flow state as it was: `RebuildConnections` clears and refills the flow arrays, steps the pipe temperature manager and calls its listeners. To prove with a shadow rebuild in verify mode before anything is built | The whole rebuild where the modules' ports are clear of pipes: 14–25 ms a frame on Windows, both frames of a flight | Measured cost; how often ports are clear is assumed, to count on real saves | Medium |
| 2 | **A port change updates its own network only.** For an endpoint on a pipe: add or remove the item in that network in the game's order and rebuild that network's flow order, without the flood fill and the flow arrays | Possible; the item order inside a network and the network indexes must come out as the flood fill's | About 85% of the rebuild (estimate from the split: all but the flow order and part of the status refresh) | Estimate | High: mirrors the rebuild's ordering |
| 3 | **Status refresh for the changed networks only** | Yes if a network's sinks are compared before and after | 3–5 ms of the rebuild (estimate from the 21% share) | Estimate | Low, small gain |

Not proposed: spreading the rebuild over frames or doing it later. Pipes would flow by the old
networks for those frames, which changes results.

Candidate 1 also covers what else adds or removes a port without touching a pipe, such as
building or deconstructing a pipe-fed building before its pipes exist.

## Not tested

- A rocket with a habitat (its interior's ports, `RocketConduitSender` and `RocketConduitReceiver`,
  leave and join the networks too; a pilot and a crew) and a rocket with gas ports.
- The base game's rocket (`LaunchableRocket`).
- A real flight: the landing was forced over the pad's hex, and the starmap was closed.
- Sound: every run was muted.
- Speeds 1 and 2, and a colony with more pipes than this one.
- Windows without the brackets, and on a quiet machine: the whole-sequence loss.
