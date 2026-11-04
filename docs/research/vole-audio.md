# 1,500 Shove Voles and audio (2026-11-03)

The question: a Bilibili video author (an unverified claim) says a save with about 1,500 Shove
Voles crashed instantly on load in Moono with Fast Track, blames audio ("these two mods only
optimize graphics, not audio, and the noise of 1,500 voles crashed the game"), and published a
mod that mutes critter sounds. Is that a crash of the original too, something Moono does
differently, or Fast Track, and is U60 different?

GitHub issue #51 (2026-11-04) reports a freeze with 1,500 voles **in one cell**, which these
runs did not try: [that case reproduces](#issue-51-1500-voles-in-one-cell) in both players, as
below.

**Verdict for the save above: not reproduced, and audio is not where Moono differs.** A 1,500-vole save loads and
plays at speed 3 in Moono and in the original, U59 and U60, with and without Fast Track on U59,
with FMOD mixing in real time. Moono runs the game's own FMOD code and native library unchanged,
with the same settings as the original. 1,500 active voles create about 900 FMOD event instances
a second. FMOD does have a ceiling: 2,088,960 live handles. Both players reach it only at about
30,000 instances a second, some 30 times what the voles make. At the ceiling, U59 throws a
NullReferenceException for every sound it can't create, which ONI shows as a crash. U60 skips
those sounds without an error, so U60 is safer, but nothing in these runs comes close to the
ceiling. The author's claim that Fast Track leaves audio alone is wrong: by default it updates
looping sounds every other frame and ambience and the mix at 5 Hz. Neither change touches the
critter one-shots, though. Fast Track 0.18.6 doesn't load on U60 in either player.

## How audio runs in Moono

- The game's audio is FMOD Studio, through the game's own `FMODUnity.dll` and the native
  `libfmodstudio.so` (U59) or `libfmodstudioL.so` (U60), loaded as the original loads them
  (`AssemblyPolicy.requiredPlugins`). The engine supplies only what FMODUnity needs from Unity:
  paths, Transform, components, the Timeline types (ARCHITECTURE.md §3.8). Moono's only FMOD
  code is the test-only `--audio-wav` option (`src/OniMoono.Host/AudioCapture.cs`), which swaps
  the output type for FMOD's WAV writer. No game tweak touches sound: `fast-anim-events` keeps
  each animation's inputs in a mirror but plays every frame event, sounds included, in the
  game's order.
- `RuntimeManager.Update` (a MonoBehaviour Update, in normal script order) updates the attached
  instances and calls `studioSystem.update()` once a frame. FMOD Studio runs asynchronously: API
  calls go into a command queue that FMOD's own Studio thread processes, and FMOD's mixer thread
  feeds the device. Under `DEFERRED_CALLBACKS`, callbacks reach managed code during `update()`.
- Settings come from the game's FMODUnity settings asset, through our asset loader. Logged by
  the test mod from FMOD itself, they match the original's exactly, on U59 and U60: output ALSA,
  128 software channels (real), 512 virtual voices, DSP buffer 1024 × 4, 48 kHz stereo, logging
  `NONE`, error callback off, memory tracking off, platform `PlatformLinux`. The Windows player
  uses the same code with FMOD's Windows output (WASAPI). We didn't run it, because nothing in the
  audio path is platform code of ours.
- Linux test players are silent: `contain.sh` points PulseAudio at a server that doesn't exist.
  For these runs, `scripts/audio-sink/record.sh` gave each player a private ALSA device that
  takes the mix at a sound card's pace. FMOD mixed for real: the captured stream measured
  2,000–4,300 RMS per 10 s.

## How the game limits critter sounds

- Kanim sound events (`SoundEvent.OnPlay`, via `AnimEventManager`) play only when
  `SoundEvent.ShouldPlaySound` passes. The sound must be in the active world
  (`SoundCuller.IsAudibleWorld`) and audible from the camera
  (`CameraController.IsAudibleSound`, the event's falloff distance). It must not be low priority
  while zoomed out past `AudioMixer.LOW_PRIORITY_CUTOFF_DISTANCE`, and the game must not be
  paused. One-shots go through `KFMOD.BeginOneShot` → `KFMOD.CreateInstance` →
  `start()` + `release()`.
- Looping sounds (`LoopingSoundManager.RenderEveryTick`) are started and stopped by the camera's
  `SoundCuller` every frame. There's no per-sound or per-species cap in game code. The caps that
  exist are FMOD's own: 128 real voices, 512 virtual voices, and each event's max-instances and
  stealing setting from the banks.
- The voles' sounds (top 12 of 50,706 instances in 59 s, Moono U59) are landing and footstep
  sounds by floor material, plus `ShoveVole_drill_hide`, `ShoveVole_drill_emerge` and
  `ShoveVole_jump`. Rock and algae landings alone are 27,000 of them.
- U59 → U60 game code: `KFMOD` now caches each event's path by GUID (`eventPathsByGuid`,
  `eventGuidsByPath`), so `CreateInstance` no longer asks FMOD for the event's description and
  path on every instance. That saves CPU. It also removes the U59 failure below.
  `SoundEvent`, `LoopingSoundManager`, `SoundCuller`, `AudioMixer` and `AnimEventManager`
  differ only in decompiler noise. U60's `FMODUnity.dll` is a development build with FMOD's
  logging library: `FMOD.Debug.Initialize` is called, and Live Update and the overlay follow
  `!= Disabled` rather than `== Enabled`. The FMOD API version is the same (2.03.12), and logging
  stays `NONE`, so no FMOD messages reached either player's log. The Creatures, Buildings,
  DuplicantActions, Snapshots and master banks differ between the builds.

## Method

- **Players**: Moono release builds of 2026-11-01 (master for U59, u60 for U60), default tweaks
  unless stated; the original Unity players of the frozen installs (U59 744825, U60 761211),
  with the all-DLC Steam double. One player at a time, on Xvfb (Moono on the d3d12 GPU, the
  original on llvmpipe as e2e runs it, so the original's frame times are not comparable).
- **Fast Track**: 0.18.6 (approved), default options, installed as a Local mod.
- **Save**: the e2e `early-base` colony with 1,500 Shove Voles spawned in view (Moono U59), run
  30 s at speed 3 and saved (`voles1500-u59.sav`, 1.5 MB; voles over 129 floor cells).
- **Runs**: a small test mod (`out/vole/mod/VoleSwarm.cs` in this worktree, not committed). It
  loads the save from the main menu, puts the camera on the voles (orthographic size 20), keeps
  the game at speed 3, and logs once a second: frame times, process RSS, FMOD's channels playing,
  Studio command queue and handle usage, FMOD memory, instances created through
  `KFMOD.CreateInstance`, voles moving and their animations. Each run is 60–90 s after the load.
- The first Fast Track runs stayed paused: something pauses the game again after the load with
  Fast Track. They are left out. The runs below keep the game unpaused, and their logs show game
  time advancing.

## Results: loading the 1,500-vole save

| Run | Result | Frame avg | RSS peak | Channels (real) | Instances created |
| --- | --- | --- | --- | --- | --- |
| Moono U59 | plays, no errors | 17.7 ms | 8.9 GB | 112 (88) | 50,706 in 59 s |
| Moono U59, `exact` tweaks | plays | — | — | 113 (79) | 52,464 in 59 s |
| Moono U59 + Fast Track | plays | 21.6 ms | 10.0 GB | 100 (71) | 86,979 in 89 s |
| Moono U60 | plays | 17.5 ms | 8.7 GB | 106 (76) | 52,549 in 59 s |
| Original U59 | plays | (llvmpipe) | 8.4 GB | 72 (58) | 32,830 in 55 s |
| Original U59 + Fast Track | plays | (llvmpipe) | 8.6 GB | 72 (62) | 46,154 in 84 s |
| Original U60 | plays | (llvmpipe) | 8.3 GB | 68 (57) | 32,769 in 51 s |
| U60 + Fast Track 0.18.6 | Fast Track fails to load, both players | — | — | — | — |

- Live handles stayed at 42,000–58,000 in every run. The colony with no voles holds 42,344:
  the banks' own objects. Fast Track's run in the original peaked at 82,224, and the table grows
  as needed. The Studio command queue never stalled for long.
- Moono has no RSS problem with voles. Without voles it settles at 7.8 GB on this colony. With
  voles the original's RSS is within 0.5 GB of ours without Fast Track, and 1.4 GB below ours
  with it (llvmpipe and d3d12 also hold different driver memory). 5,000 spawned voles reach 11.2 GB in Moono and 10.7 GB in the original, and both
  play.
- The original makes fewer instances per real second only because llvmpipe holds it below speed
  3's game time: about 130 a frame at 4 fps, against Moono's 8 a frame at about 100 fps.
- On U60, Fast Track 0.18.6 fails in `KMod.DLLLoader` in both players. Harmony throws
  `Cannot assign method return type System.Boolean to __result type System.String` for
  `LocText.ParseText`, then `MissingMethodException: LocText.ModifyLinkStrings` comes from its
  `LocText.FilterInput` patch while the crash dialog builds. Both players exit with code 1 within
  about 20 s of launch, so there's no U60 + Fast Track row to measure.

## The FMOD ceiling, reached on purpose

The test mod plays extra `Rock_creature_land` one-shots through the game's own
`SoundEvent.PlayOneShot`: 30,000 a second of real time, near the camera, on top of the loaded
voles.

| Run | Handle table full after | Then |
| --- | --- | --- |
| Moono U59 | 13 s | `KFMOD.CreateInstance` throws NullReferenceException for every failed instance: the test's own calls and the voles' (2,650 from the game's sound events in 75 s) |
| Original U59 | about 50 s | the same exception on the test's calls (113,000 by t = 58 s) |
| Moono U60 | 11 s | no exceptions: sounds past the ceiling just don't play |
| Original U60 | 17 s | no exceptions |

- The table tops out at 2,088,960 handles in use (capacity 2,097,152) in every player. It fills
  because the Studio thread processes commands more slowly than they arrive: the command queue
  climbs to 12–18 million bytes and FMOD's memory to 1.2–1.9 GB. Once it is full,
  `createInstance` returns an invalid instance.
- U59's `KFMOD.CreateInstance` then asks the invalid instance for its description and path, gets
  null, and fails with a NullReferenceException. In game code `SoundEvent.PlaySound` catches it
  and rethrows it as an `ArgumentException`. `KCrashReporter.HandleLog` pauses the game and
  shows the crash dialog for the first exception logged (during a load it defers the dialog until
  the load ends). So on U59 a colony that keeps FMOD past its ceiling crashes like this, in the
  original as much as in Moono. U60's GUID path cache returns the description without asking the
  instance, and nothing throws.
- Moono filled the table sooner than the original at the same rate per second. The players
  hand Studio its commands in different batches (about 1,000 a frame at 30 fps against 7,500 at
  4 fps), and we didn't measure how that changes the Studio thread's pace. Either way it is
  native FMOD work the game asks for, and both players hit the same ceiling at the same order of
  rate.

## What this means for the report

- The run that matches the claim (Moono + Fast Track, 1,500 voles, loading the save) plays. The
  load phase is busier than steady play, with 2,300–2,600 instances in the first second, and
  stays far below the ceiling.
- The mechanism the author suggests, too many sounds crashing the game, exists only as the
  FMOD handle ceiling. That is identical in the original. On U59 it ends in the original's crash
  dialog, and U60 no longer crashes on it. Reaching it takes about 30 times the instance rate of
  1,500 voles on this machine. A much slower PC could hit it sooner if FMOD's Studio thread falls
  behind, but the original would crash the same way there. The muting mod
  (声音分类开关, Workshop 3833055007) skips creating instances of the chosen categories by
  event path, so it would keep critters away from the ceiling. From its description it
  changes nothing else. Its Workshop page now says it was removed from the community.
- Without the author's Player.log, the crash's cause is unknown. A crash dialog's stack trace (a
  `KFMOD.CreateInstance` NullReferenceException under `SoundEvent.PlaySound`) would confirm the
  ceiling. Anything else points elsewhere: a mod, memory on their PC, or something Windows-only.

## Fixes and mitigations

- Nothing to fix in Moono: its audio path is the original's, and so is its behaviour at the
  ceiling.
- If a player does hit the ceiling on U59, a game-side guard could return the invalid instance
  from `KFMOD.CreateInstance`, as U60 effectively does, instead of throwing. That would play
  identically below the ceiling and avoid the crash above it, a divergence from the original
  only where the original crashes. Backporting U60's GUID → path cache would do the same and
  save a `getDescription`/`getPath` round trip per instance. At 900 instances a second that is
  well under 0.1 ms a frame, so it isn't worth doing as a performance tweak. Neither is
  warranted without a report that shows the ceiling.

## Issue #51: 1,500 voles in one cell

The report: about 1,500 Shove Voles stacked in one cell on U60 public_testing (Windows, 4K, Fast
Track 0.18.4 with its animation optimisations). With the camera away the game runs smoothly; with
the camera close the frame rate drops sharply and the game may freeze. The runs above missed it
because their voles stood over 129 cells.

**Verdict: reproduced, the same in Moono and the original, on Windows and Linux.** With the camera
on the pile at orthographic size 20, both players freeze within 7–16 s: the main thread waits in
an FMOD call for minutes and the game never draws another frame. The cost is FMOD's own work for
thousands of copies of one event playing at one point: `Rock_creature_land`, which Klei's bank
caps at 5 instances, stealing the oldest. Klei fixed the same setting for Beakons in 740622. Moono's managed code on this path costs
under 0.5 ms a frame. There is no exact fix of any size. A per-frame dedupe of one-shots of the
same event in the same cell removes the freeze: 2,900 instances a second become 83, frames stay
at 60 fps, and the pile sounds quieter.

### Method

- **Fixture**: the e2e `early-base` colony on U60 761654. A test mod walled one floor cell with
  Neutronium (a 5×5 square minus the middle), spawned 1,500 voles in it, ran 60 s at speed 3
  and saved (`vole-pile-1500.sav`, 2.0 MB; gitignored in tests/e2e/fixtures). All 1,500 voles
  stay in the cell throughout every run.
- **Players**: Moono u60 (dc48d2d2) release, default game tweaks; the original 761654. On Windows,
  `E:\oni-builds\oni-win-761654` and its renamed copy `BrutalNotIncluded-761654`, 1280×720 on the
  second monitor, High priority, Moono with `--no-focus` and `--frame-stats`. On Linux, Xvfb: Moono
  on d3d12 and the original on llvmpipe, so the original's Linux frame times are not comparable.
- **Audio**: volume prefs at 1, so FMOD really mixes. Nothing reaches a sound device. On Windows,
  Moono's `--audio-wav` and the original's isolation plugin (`tools/OriginalIsolation`) both set
  FMOD's `WAVWRITER` output into the run home. The original therefore needed no zero volume. On
  Linux, `scripts/audio-sink/record.sh`'s private ALSA sink takes the mix at a sound card's pace.
- **Camera script** (the test mod, no pointer): 60 s away (100 cells to the side, size 20), then
  60 s each on the pile at sizes 20, 10, 6 and 4, then 60 s away. Speed 3 throughout.
- **Logging**, once a second by the mod in both players: frame times, instances created
  (`KFMOD.CreateInstance`), FMOD channels playing (total and real), the Studio command queue
  (`getBufferUsage`: usage, capacity, stalls and stall time), Studio handles, FMOD's own CPU use
  (mixer and Studio update), FMOD memory, private bytes and the managed heap. Harmony stopwatches
  time `AnimEventManager.Update`, `LoopingSoundManager.RenderEveryTick`,
  `SoundEvent.ShouldPlaySound`, `KFMOD.BeginOneShot`/`CreateInstance`/`EndOneShot`,
  `RuntimeManager.CreateInstance`/`Update`, `EventInstance.set3DAttributes`/`start`/`release`, and
  the rest of `EventInstance`, `Studio.System` and `EventDescription` as groups. A watchdog thread
  logs a stall when no frame has finished for 1 s, along with the timed method the main thread is
  in. On Linux, `dotnet-stack` took the main thread's stack during Moono's stalls. The mod's
  `getMemoryUsage` call was dropped after the first run: it reported nothing with memory tracking
  off.
- Every run was also done without the stopwatches (`timers=0`). It froze the same way.

### Results

Windows (frame cap 60 fps). On the pile, medians are of the seconds before the freeze:

| Run | Away: frame avg / instances a second | Pile, size 20: instances a second | Channels (real) | FMOD mixer / Studio CPU | Main thread |
| --- | --- | --- | --- | --- | --- |
| Moono | 17 ms / 0 | 2,000–2,900 | 512 (48) | 143% / 94% | a 1.2 s and a 5.8 s stall, then frozen from 10 s on (killed after 187 s frozen, still in `EventInstance.set3DAttributes`) |
| Moono, no stopwatches | 17 ms / 0 | 2,168 | 370 (59) | 142% / 66% | frozen from 10 s on (killed after 100 s) |
| Original | 18 ms / 0 | 1,400–3,300 | 110–512 (40–59) | 118% / 63% | an 11.5 s stall at 3 s, frozen from 16 s on (killed after 100 s, in `EventInstance.start`) |
| Original, no stopwatches | 18 ms / 0 | 2,418 | 512 (53) | 174% / 100% | frozen from 7 s on (killed after 100 s) |
| Moono + per-frame dedupe (below) | 17 ms / 0 | 74 at size 20, 67–70 at 10, 6, 4 | 38–55 (26–36) | 6% / 5% | no stall in 4 × 60 s on the pile; 16.7 ms frames, max 333 ms |

- Linux shows the same thing, a little later: Moono stalled for 1 s and 16 s, then froze 29 s
  after reaching the pile (killed after 192 s frozen). The original stalled for 1, 1.5, 11 and 1 s,
  then froze at 27 s (killed after 242 s).
- The smaller zooms were never reached without the dedupe: the freeze comes first. With the
  dedupe, sizes 10, 6 and 4 made no more instances than 20. Low-priority sounds play below 10, but
  the pile's sound is `Rock_creature_land`, which plays at every zoom.
- What plays: `Creatures/Generic Movement Materials/Rock/Rock_creature_land` (falloff 20 cells) is
  nearly all of it, from `FloorSoundEvent`. The voles in the cell keep landing on its floor.
- Memory isn't the problem. Private bytes rose 140 MB in Moono's pile seconds and FMOD's memory
  50 MB. Studio handles reached 166,000–280,000 of a table that grows to 2 million. The command
  queue held 280,000–450,000 bytes when the main thread blocked.
- During the freeze one native thread uses a whole core. On Linux it had 248 s of CPU in Moono and
  261 s in the original: FMOD's Studio thread. The managed stacks end in FMOD's P/Invoke wrappers
  (`EventInstance.setVolume`, `set3DAttributes`, `start`), called from `FloorSoundEvent.PlaySound`
  under `AnimEventManager.Update`. FMOD's command queue is full, and each new command waits for the
  Studio thread to make room.
- On Windows the mixer fell behind real time: the WAV writer got 88 s of audio in Moono's 297 s run.
  With the dedupe it got 382 s in 392 s.
- Quitting in that state hangs as well. `Application.Quit` reaches `RuntimeManager.OnDestroy` →
  `FMOD.Studio.System.release()`, which waits for the backlog. Moono was still in it 2 minutes
  later, on Linux. The original's quit wasn't tried in that state.

### What makes it expensive

Linux, Moono, no voles, a synthetic `Rock_creature_land` one-shot through `SoundEvent.PlayOneShot`
at 2,900 a second (the pile's rate):

| Where | Frames | Channels (real) | Mixer / Studio CPU | Main thread |
| --- | --- | --- | --- | --- |
| all at the camera's point | 9–11 ms, then frozen | 229 → 512 | rising to 100%+ | frozen after 3 s, for 150 s+, and quitting hung too |
| spread over 40 × 20 cells around the camera | 7–11 ms | 46–88 (30–56) | 5–7% / 18–24% | 0.02 s of stalls in 60 s |

The rate alone is cheap. So is one point, for most events: the same test with
`Rock_creature_footstep` at 2,900 a second at one point played at 5 ms frames, with Studio at 11%
and no stalls. What differs is the event's instance limit (next section). `Rock_creature_land`
caps at 5 and steals the oldest instance, so every new one starts and stops another. The spread
case puts part of its instances near the 20-cell falloff, which may be what lets FMOD drop them
cheaply there (an inference: FMOD's internals weren't profiled).

### Event limits in Klei's banks, and the Beakon fix

FMOD's API doesn't expose an event's max instances or stealing mode. The test mod measured them
instead: it starts 64 instances of an event at the camera's point in one frame and records which
ones play once none is still starting, and again 50 frames later. Both players load the same
banks, and Linux's bank files are byte-identical to Windows'. The patterns:

- **1 plays (the first), 63 never start**: max 1 instance, stealing *none*. A new instance over the
  limit fails at once.
- **all 64 start, the last 5 remain 50 frames later**: max 5, stealing *oldest*. Every new instance
  plays and stops an older one.
- **4 play, 60 virtual**: max 4, stealing *virtualize*.

| Event | 733790 (before the fix) | 744825 (after) | 761654 (now) |
| --- | --- | --- | --- |
| `Rock_creature_land` (shared by all critters landing on rock) | 5, oldest | 5, oldest | 5, oldest |
| `ShoveVole_movement`, `ShoveVole_drill_LP`/`_emerge`/`_hide` | 5, oldest | 5, oldest | 5, oldest |
| The other `ShoveVole_*` (10) and `ShoveVoleBaby_*` (12) events, `Rock_creature_footstep`, `Rock_creature_climb` | 1, none | 1, none | 1, none |
| 17 `ParrotFish_*` events (breathe, caught, settle, sleep in and out, swim, drink, grooming, hit, lay egg, poop, queue movement, react, sleep bubble) | 5, oldest (`grooming_pre`: start all, keep 5) | 1, none | 1, none |
| `ParrotFish_idle`, `ParrotFish_hatching` (also `objectCount` properties: maxObj 3) | 4, virtualize | 1, none | 1, none |
| `ParrotFish_swim_LP`, `ParrotFishBaby_swim_LP` | 4, virtualize | 4, virtualize | 4, virtualize |
| `ParrotFish_voice_die_*`, `ParrotFishBaby_poop` | 5, oldest | 5, oldest | 5, oldest |

- The Beakon is the ParrotFish. Game update 740622 (7 July 2026) listed "Fixed audio performance
  issue with Beakons" under the Aquatic Planet Pack. oni-versioning's 737790 → 740622 C# diff has
  no change in any sound code: `KFMOD`, `SoundEvent`, `AnimEventManager`, `LoopingSoundManager`
  and `AudioMixer` changed only in decompiler noise. The only ParrotFish change is the flop
  animation's frame range (`FlopStates.Def`). The fix was in the bank: `DLC5_Creatures.bank` of
  733790 (a local copy, `SimNotIncluded-U59-733790`; the build is its folder's name) against 740622's and 744825's (identical
  files). Klei turned 19 ParrotFish events from "5, steal oldest" or "4, virtualize" into "1, steal
  none". The DepotDownloader step wasn't needed: copies on E: bracket the fix.
- The vole pile runs on the events that kept "5, steal oldest". `Rock_creature_land` makes nearly
  all of the pile's 2,900 instances a second. This is the upstream root cause. The same bank
  change Klei made for Beakons (stealing *none*, or a lower limit) would apply to
  `Rock_creature_land` and the four `ShoveVole` events. Those banks are Klei's FMOD Studio project;
  only Klei can rebuild them.
- Measured cost of the difference, Linux, Moono, at one point, 2,900 a second: "5, steal oldest"
  (`Rock_creature_land`) freezes in 3 s; "1, steal none" (`Rock_creature_footstep`) runs at 5 ms
  frames with no stall.

### Command queue size

FMOD's documentation says a full Studio command queue stalls the calling thread until the async
update has made room (`FMOD_STUDIO_ADVANCEDSETTINGS.commandqueuesize`, 32 KB by default, set
before `Studio::System::initialize`). Studio is already up when mods load, in both players (the
test mod read `RuntimeManager.IsInitialized == true` in its `OnLoad`). So a normal mod can't set
the size. The test used a local, uncommitted hook instead: a Harmony prefix on
`FMOD.Studio.System.initialize`, installed in Moono's host next to `--audio-wav`, and in the
original's isolation plugin (BepInEx, before any game code). FMOD confirmed each size
(`getAdvancedSettings`: 32768 → the new size). Windows, Moono, the pile at size 20:

| Queue | Stalls on the pile | Frozen from | Handles / Studio CPU / mixer before the freeze |
| --- | --- | --- | --- |
| 32 KB (default) | 1.0 s, 1.1 s | 10 s | 135,000 / 66% / 142% |
| 256 KB | 1.0 s, 1.0 s | 10 s | 134,000 / 57% / 141% |
| 1 MB | 36 s at 3 s | 43 s | 275,000 / 87% / 121% |
| 4 MB | none for 4 s, then 41 s at 5 s | 54 s | climbing by about 75,000 a second to 870,000; Studio 88 → 180%; mixer 130–190% |

- A bigger queue only moves the freeze later and makes each stall longer. Handles, Studio CPU and
  the queue's own use keep climbing the whole time, because the Studio thread consumes commands
  more slowly than the pile makes them. There is no queue size at which it settles.
- The audio is the same in content and equally late. In every run, small queue or large, the WAV
  writer got about 53–56 s of audio in 175–210 s of play, and the loudness per second follows
  the same pattern. The mixer is overloaded either way, which no queue size changes. A run that
  didn't stall at the default queue, needed for a sample-for-sample comparison, exists only with
  the dedupe, which changes the sounds. So no WAV is the "right" one to compare against.
- So it is not a near-exact fix: it keeps the same commands in the same order, but the freeze
  stays. It would only help a load that bursts briefly above what the Studio thread can sustain.

### What Moono can change in FMOD, and what a mod can

| Lever | A Harmony mod | Moono (host, load-time rewriter) |
| --- | --- | --- |
| Studio init: command queue, handle table size, Studio update period, real and virtual channel counts, DSP buffer, output type | no: Studio initializes before `Global.Awake` loads mods; changing it means releasing and recreating the Studio system with every bank and instance | yes: the host's patches go in before any game code (as `--audio-wav` already sets the output type), so it can set any `ADVANCEDSETTINGS`/`setSoftwareChannels`/`setDSPBufferSize` field before `initialize` |
| Per-event max instances, stealing | no FMOD API at runtime | no FMOD API either; only through bank data |
| Bank data | can unload a loaded bank and load another file (`loadBankFile`, as the test mod did to compare bank versions) after start-up, or `loadBankMemory` with bytes it changed | the same, plus before the first load: redirect FMODUnity's bank paths or hand modified bytes to `loadBankMemory`. Editing the stealing mode needs the bank format, which FMOD doesn't document (an estimate: feasible but fragile, and every bank update breaks it) |
| Managed KFMOD/FMODUnity paths (`BeginOneShot`, `CreateInstance`, culling) | yes, normal Harmony patches (the dedupe above is one) | yes, as a game tweak, or rewritten at load time |
| Native P/Invoke layer (`fmodstudio` entry points) | only by patching FMODUnity's managed wrappers | can bind the DllImports to a shim library (`--native-override` resolves natives today) that sits in front of FMOD's: count, batch or refuse calls without touching managed code |

The levers that would remove the freeze change sounds (the bank's stealing mode, the dedupe, any
cap). The ones that keep the sound exact (queue size, Studio update period, P/Invoke speed)
leave FMOD's per-instance cost as it is.

### Where the time goes

- **FMOD native work** (the Studio thread and the mixer): all of the freeze. Both players use the
  same `FMODUnity.dll`, the same native library and the same settings (output aside: 512 × 4 DSP
  buffers on Windows, 1024 × 4 on Linux, from the game's settings). They freeze at the same point
  in the same calls.
- **The game's own path**: on Windows, in Moono's seconds without a stall, `AnimEventManager.Update`
  with all sound events took 0.25–0.35 ms a frame. `KFMOD.CreateInstance` took 0.10 ms a frame for
  2,900 instances (about 2 µs each, P/Invoke and Harmony included). `ShouldPlaySound` took 0.01 ms
  a frame, and `start` + `release` 0.01 ms. In the original, `AnimEventManager.Update` was
  1.5 ms a frame.
- **Moono's own code**: nothing measurable on this path. No Moono-only bug was found. Moono makes
  as many instances a game second as the original, and fails the same way.
- **Fast Track**: 0.18.6, the current release (the same hash as the approved copy; static scan:
  only PLib's update check makes a web request), doesn't load on 761654 in either player:
  `LocText.ParseText` `__result` type mismatch, then `MissingMethodException`
  `LocText.ModifyLinkStrings`. 0.18.4 isn't published on GitHub. So the reporter's setup can't be
  run on this build, and every run above is without Fast Track. Fast Track's animation options
  don't remove sound events (above), and the original freezes without it.

### Fixes

- **(a) Exact fixes: none worth doing.** The main thread's own work on this path is under 0.5 ms
  a frame in Moono. Faster P/Invoke or less managed overhead can't touch a stall that lasts minutes
  inside FMOD. Batching keeps the same calls in the same order, so FMOD does the same work.
  A larger Studio command queue was measured above: the freeze moves from 10 s to 54 s and stays.
- **Upstream**: the root cause is the bank. `Rock_creature_land` and four `ShoveVole` events cap at
  5 and steal the oldest instance. Klei fixed the same thing for Beakons in 740622 by changing
  their events to one instance with no stealing. That is the report to make to Klei.
- **(b) A game tweak, off by default, "Changes gameplay":** skip a one-shot when the same event has
  already started in the same cell in the same frame (prefix on `KFMOD.BeginOneShot(EventReference,
  Vector3, float)`, keyed by event GUID and cell). Measured on Windows, as in the table: 2,900
  instances a second → 83, of which `Rock_creature_land` is about 10 a second. No stalls at any
  zoom. The mixer stays at 6% and keeps real time. What changes audibly:
  - the pile plays one copy of a sound per frame per cell instead of up to about 40 at once, so it
    sounds much quieter and thinner. The first copy in each frame starts at the same time as before;
  - events that read the game's `objectCount` parameter (the copies of a path started in the
    frame) see 1 instead of N;
  - elsewhere it matters only when two objects start the same event in the same cell in the same
    frame. One of them is then silent, which can't be heard in normal play.
  Emulating the Beakon fix in managed code is the closer variant: refuse a new
  `Rock_creature_land` or `ShoveVole_movement`/`drill` one-shot while the earlier ones are still
  playing at that point, rather than letting FMOD steal. It needs the live instances kept per
  event and checked with `getPlaybackState` (one P/Invoke each). An estimate; not built.
  A time window (say 50 ms) instead of one frame would make it independent of the frame rate. That
  variant is in the test mod but wasn't run. A cap on live instances per event and position would
  also work, but it needs a count kept per instance until it stops, which the game doesn't track.
  The dedupe is simpler and measured.
- **(c) Moono-only bugs**: none found. The quit hang is FMOD's `System.release()` draining the same
  backlog. It happens in the original's code path too, but it wasn't run there.
- What to tell the reporter: it is the original game's behaviour with FMOD, not Moono's or Fast
  Track's. Until a tweak exists, spreading the voles over more cells avoids it, as the first runs
  above show, and so does muting critter sounds (the mod named above).
