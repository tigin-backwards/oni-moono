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

## Event limits across the banks (2026-11-04)

The questions: which of the game's FMOD events can do what `Rock_creature_land` does to a vole
pile, why did Beakons freeze games with far fewer critters (sometimes about 50), why does
Windows fall behind where Linux keeps up, and how audible would a tighter cap be in real
colonies?

**Verdict.**

- **Limits.** Stealing and virtualize are the norm, not an outlier. Of 5,184 events in U60's
  banks, 3,363 cap above one instance and steal or virtualize (most steal the quietest).
  [vole-audio-event-limits.tsv](vole-audio-event-limits.tsv) lists them all.
- **The freeze.** It coincides with FMOD's 512 voices running out, in both players, but more
  voices don't move it ([below](#raising-fmods-voice-limit-2026-11-04)): instances pile up,
  the Studio thread passes 100% CPU and the command queue blocks the main thread. Two kinds
  of event get there in a crowd:
  - stealing events started very fast at one point, where each start plays and stops another:
    `Rock_creature_land` at 2,000–3,000 a second;
  - virtualize events, where each instance over the limit keeps a virtual voice until it ends:
    pre-fix `ParrotFish_idle` freezes at 600–700 a second.
- **Beakons.** 50 Beakons on the pre-fix bank didn't freeze, on Linux or on Windows. They hold
  about 1.1 voices each at speed 3, so a quiet colony reaches 512 at about 400 Beakons on screen,
  a busy one at about 280. The reports of about 50 must have had something these runs didn't:
  [below](#beakons-pre-fix-and-now).
- **A leak.** One event leaks without bound: `Squirrel_eat_LP`. The Pip's eat loop plays it as
  a one-shot, but it is a loop that never ends.
- **Windows and Linux.** At the same rate, Windows is not worse. In game, at 1,500 starts a
  second at one point, Windows' Studio thread used 13% CPU and Linux's 22%. Windows' queue peak
  was the lower one. No startup setting changes which sounds play and also keeps a crowd under
  the cliff.
- **Real colonies.** Six colonies at normal zoom used 35–213 voices. Many of their stealing
  events play more than once at a time for much of each minute (`Rock_jump` 85% of the time),
  so a blanket "1, steal none" would be audible. A fix should target the crowd, not the list.

### Method

- **Limits.** `tools/FmodLimits/limits.py` is a standalone FMOD host in Python on the game's
  own `libfmodstudioL.so` (FMOD 2.03.12). It loads every bank of U60 761654 (base and all DLC)
  and enumerates every event: path, `isOneshot`, 3D, min and max distance, length, user
  properties.
  - Per event, 64 instances start at the listener in one update. If the first k play, it steals
    none. If the last k play, it steals. If all 64 stay alive and some are virtual, it
    virtualizes.
  - For stealing events, k old instances then meet one new one: quieter (volume 0.05), further
    (0.9 × max distance), or louder but further. That separates oldest, quietest and furthest.
  - A cooldown shows as a second instance that won't start for a while after the first.
  - Output is `NOSOUND_NRT` with synchronous updates, so every run gives the same result. All
    banks take about 6 minutes.
- **Pre-fix banks.** The same survey of 733790 (`SimNotIncluded-U59-733790`, all 55 banks). For
  the events that changed, also 744825's base banks.
- **Who plays what.** The game's audio sheets map each event name to the animation file,
  animation and sound-event type that play it. They are the CSV text assets in
  `resources.assets` that `GameAudioSheets` loads. The decompiled 761654 code gives the rest.
- **Load.** `tools/FmodLimits/rate.py` runs the same library in real time in its normal
  asynchronous mode, on Linux and on Windows (`python.exe` with the Windows `fmodstudioL.dll`,
  under the Windows game lock, no game running).
  - It starts one event N times a second at one point from a 60 Hz loop. Each start creates the
    instance, sets its 3D position and volume, starts it and releases it, as `KFMOD`'s one-shots
    do.
  - Once a second it logs the Studio queue, handles, CPU, live instances and voices.
- **In game.** The #51 test mod, Moono u60, speed 3, in two new modes:
  - **tank**: a box of water with Neutronium walls at the camera, with N Beakons in it. The DLC5
    bank can be swapped for 733790's at load.
  - **noise**: logs every instance created, with its cell and real time, and every event's live
    instance count twice a second. The camera spends 60 s at orthographic size 18 on each of
    three busy spots: the densest Duplicants and critters, the densest buildings, and both.
  - On Linux, Xvfb, with FMOD mixing in real time into the private ALSA sink. On Windows,
    1280×720 on monitor 2, `--no-focus`, WAV writer output, under the lock.

### What the banks hold

U60 761654, 5,253 bank entries (5,184 distinct events; snapshots left out):

| Bank | Events | Above 1, steal quietest | Above 1, steal oldest | Above 1, virtualize | 1, none | Cooldown | No limit | Other |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Buildings | 1428 | 1102 | 95 | 123 | 52 | 10 | 16 | 30 |
| Creatures | 684 | 119 | 80 | 18 | 396 | 57 | 0 | 14 |
| expansion1_Buildings | 421 | 362 | 14 | 30 | 11 | 3 | 1 | 0 |
| DuplicantActions | 366 | 191 | 4 | 49 | 37 | 1 | 1 | 83 |
| DLC5_Creatures | 320 | 27 | 166 | 6 | 88 | 33 | 0 | 0 |
| DuplicantVoices | 292 | 263 | 0 | 0 | 5 | 13 | 0 | 11 |
| UI | 264 | 17 | 28 | 17 | 77 | 51 | 8 | 66 |
| expansion1_Creatures | 237 | 86 | 28 | 6 | 62 | 55 | 0 | 0 |
| Ambiences | 146 | 6 | 4 | 0 | 7 | 10 | 0 | 119 |
| Environment | 143 | 18 | 13 | 22 | 35 | 0 | 0 | 55 |
| DLC4_Creatures | 100 | 5 | 0 | 1 | 68 | 26 | 0 | 0 |
| DLC3_Buildings | 91 | 78 | 11 | 2 | 0 | 0 | 0 | 0 |
| DLC2_Creatures | 89 | 0 | 0 | 0 | 68 | 21 | 0 | 0 |
| DLC5_Buildings | 66 | 25 | 2 | 30 | 2 | 0 | 7 | 0 |
| Plants | 62 | 58 | 3 | 1 | 0 | 0 | 0 | 0 |
| 32 smaller banks | 544 | 202 | 29 | 67 | 12 | 29 | 59 | 146 |
| **All** | **5253** | **2559** | **477** | **372** | **920** | **309** | **92** | **524** |

- **Quietest, not oldest.** The #51 section read `Rock_creature_land`'s "start all, keep the
  last 5" as stealing the oldest. It steals the quietest. At one point all instances are equally
  loud, so the quietest is the oldest and the vole pile behaves the same. The ParrotFish events
  do steal the oldest.
- **Other** holds limits of 2 or more with no stealing, limits of 1 with stealing or virtualize,
  and events whose pattern wasn't clean (often random or delayed instruments).
- **No limit** is mostly snapshots and videos. Every sound event has some limit.

**What Klei changed between 733790 and 744825** (740622, by its patch notes):

| Event | 733790 | 744825 and 761654 |
| --- | --- | --- |
| 17 `ParrotFish_*` events: `breathe_inhale`, `caught`, `cc_settle`, `cc_sleep_in`, `cc_sleep_out`, `cc_swim`, `drink_gulp`, `grooming_pre`, `grooming_pst`, `hit`, `lay_egg_pre`, `lay_egg_pst`, `poop`, `queue_movement`, `react_neg`, `react_pos`, `sleep_bubble` | 5, steal oldest | 1, none |
| `ParrotFish_idle`, `ParrotFish_hatching` | 4, virtualize | 1, none |
| `ParrotFish_eat_chomp` | 1, none, about 1 s cooldown | 1, none |
| `Open_DoorInternal`, `Close_DoorInternal` (doors) | 8, steal quietest | 1, none |
| `SteamTurbine_impact` | 6, steal quietest | 1, none |
| `Liquidator_draw`, `Chlorinator_popping`, `GeoTuner_Tracker_button` | 8, 4 and 4, stealing | 1, none |

- Unchanged:
  - `ParrotFish_swim_LP` and `ParrotFishBaby_swim_LP`: 4, virtualize. These are looping sounds
    that the game stops.
  - `ParrotFish_voice_die_pre`/`_pst`, `ParrotFishBaby_poop` and the baby's death voices: 5,
    steal oldest.
  - Every vole and generic movement event.
- The doors and the other building events changed in the same window, which the patch notes
  don't mention.
- Nothing else changed. The remaining differences are events added after 733790.

**Looping events played as one-shots.** `SoundEvent`, the sheets' plain type, starts an instance
and releases it, whatever the event is. 59 events that FMOD doesn't count as one-shots are
played this way.

- Each was started once, released and mixed for 60 s. All but 8 ended on their own.
- Of those 8, the game stops 5 itself. The vole drill events use `HatchDrillSoundEvent` and the
  Jukebot uses `PhonoboxSoundEvent`, both looping types.
- 3 never end:

| Event | Limit | Played by | Effect |
| --- | --- | --- | --- |
| `Squirrel_eat_LP` | 4, virtualize | every loop of a Pip's `eat_loop`, as a plain `SoundEvent` (the baby's `SquirrelBaby_eat_LP` is a `LoopingSoundEvent`) | each loop a Pip spends eating on screen adds an instance that never ends; past 4 they go virtual, each keeping a voice |
| `RocketDebris_steaming_LP` | 4, virtualize | rocket debris `landed`, a row with no type | the same, once per landing, if that sheet's default type is `SoundEvent` |
| `Geyser_erupt_LP`, `Geyser_erupt_water_LP` | 5, virtualize | one cool slush salt water geyser row with no type (every other geyser row is a `LoopingSoundEvent`) | the same, once per eruption, if that sheet's default is `SoundEvent` |

`Squirrel_eat_LP` is the one leak found that a colony can grow without bound. That rests on the
sheet, the decompiled `SoundEvent` and the standalone run; it wasn't run in game. The sheets'
default types are in the `GameAudioSheets` asset, which wasn't read.

`ParrotFish_hatching` (looping, played as a one-shot) looks like the same leak but can't happen.
It sits in a `fish_bioluminescent_kanim` `hatching_pst` row that no code plays. A hatching Beakon
is the baby, which plays `baby_bioluminescent_kanim`'s `hatching_pst` (`ParrotFishBaby_swim`). A
grown-up adult plays `growup_pst`.

### Beakons, pre-fix and now

The Beakon is the ParrotFish. Per Beakon in a tank at speed 3, the game starts `ParrotFish_idle`
1.7 times a second (`idle_loop`, frame 11). It starts `ParrotFish_swim` and a new
`ParrotFish_swim_LP` loop 0.4 times a second each. 50 Beakons make about 140 starts a second;
the 1,500 voles made 2,900.

In game: Moono, the early-base colony, 50 Beakons, speed 3, camera on the tank at size 20, for
180–360 s:

| Player | Bank | Tank | Starts a second | `ParrotFish_idle` alive (real, virtual, other) | Voices, median / max | Studio CPU, median / max | Frame average |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Linux | 733790 (pre-fix) | 24 × 12 | 131 | about 100 (4, 50, 46) | 102 / 145 | 5.2% / 13% | 9.1 ms |
| Linux | 761654 | 24 × 12 | 134 | 4–7 (1–2, 0, 3–5) | 49 / 69 | 3.3% / 6% | 10.5 ms |
| Linux | 733790 | 3 × 2 (packed) | 144 | about 100 | 113 / 141 | 5.6% / 10% | 7.9 ms |
| Linux | 761654 | 3 × 2 | 143 | 4–7 | 62 / 88 | 3.7% / 11% | 7.1 ms |
| Windows | 733790 | 3 × 2 | 145 | about 105 (4, 50, 50) | 111 / 159 | 3.6% / 7% | 16.7 ms (60 fps cap) |

- No run stalled. For 50 Beakons the pre-fix bank holds about 55 more voices and costs about 2
  more points of Studio CPU. Its idle instances end, and the count holds at about 100.
- Standalone, `ParrotFish_idle` at one point on Linux (Windows matched at 100, 400 and 1,000 a
  second):

| Starts a second | 733790: alive, voices, Studio CPU | 761654: alive, Studio CPU |
| --- | --- | --- |
| 100 | 165, –, 2% | 5, 1% |
| 250 | 410, 228, 5% | – |
| 400 | 655, –, 5% | 4, 2% |
| 500 | 820, 455, 10% | – |
| 700 | growing by 600 a second, **512**, 230%: frozen | – |
| 1,000 | growing by 700–800 a second: frozen (Windows too) | 4–21, 3% |

- **Not the voices.** With 1,024 voices instead of the game's 512, 400 a second stays up and
  1,000 freezes, as at 512: the cliff is at 550–600 a second at every limit tried
  ([below](#raising-fmods-voice-limit-2026-11-04)). Each virtual instance lives about 1.6 s,
  twice the event's 0.86 s.
- **Where 50 Beakons stand.** Pre-fix, Beakons on screen freeze FMOD once their `ParrotFish_idle`
  voices and the colony's own reach 512. Each Beakon holds about 1.1 voices at speed 3, about
  0.4 at speed 1. In the quiet early-base colony that is about 400 Beakons on screen at speed 3.
  In the busiest colony below, whose own sounds peak at 213 voices, it is about 280.
  Measured since: in early-base 300 Beakons hold and 400 freeze, at 512 voices and at 2,048
  alike, so the count is right but the voices aren't the limit
  ([below](#raising-fmods-voice-limit-2026-11-04)).
- **Why the reports differ.** 50 is far short of either. The reports of 50 had something these
  runs didn't: more of the colony on screen, more instances per Beakon in states a tank doesn't
  reach (feeding, grooming, queueing), or other events' voices. The pre-fix bank does cost more
  per Beakon, so it got there sooner. Klei's fix takes Beakons almost out of the voice budget: 4–7
  idle instances, however many Beakons.

### Windows and Linux

Standalone, `Rock_creature_land` at one point, 20 s, 1,024 voices. Each cell gives the queue's
peak and Studio's median CPU:

| Starts a second | Linux, 1,024 × 4 | Linux, 512 × 4 | Windows, 512 × 4 | Windows, 1,024 × 4 |
| --- | --- | --- | --- | --- |
| 1,000 | 8 KB, 19% | 8 KB, 18% | 7 KB, 12% | 7 KB, 10% |
| 2,000 | 16 KB, 32% | 43 KB (queue grew to 128 KB), 25% | 21 KB, 21% | 21 KB, 18% |
| 2,900 | frozen in 3 s (live 6,000+, voices at 1,024) | frozen in 4 s | 20 KB, 25% | 30 KB, 29% |

In game, `Rock_creature_land` at 1,500 a second at one point through `SoundEvent.PlayOneShot`,
early-base colony, 70 s:

| Player | Studio CPU, median / max | Queue peak (capacity) | Stalls | Voices, median / max | Frame average |
| --- | --- | --- | --- | --- | --- |
| Linux | 22% / 28% | 58–62 KB (128 KB) | 2, 3 ms | 66 / 193 | 4.4 ms |
| Windows | 13% / 16% | 36–42 KB (64 KB) | 1, 1 ms | 52 / 99 | 16.7 ms (60 fps cap) |

- **At the same rate Windows keeps up better.** It uses less Studio CPU and a smaller queue,
  standalone and in game. Standalone at 2,900 a second, Windows held in all four runs and Linux
  froze in three of four (Linux here is WSL 2).
- **Output and buffer don't matter.** NOSOUND against WAVWRITER, and 512 against 1,024 samples,
  made no difference that showed. WAVWRITER rows are left out of the table for that reason.
- **The queue grows by itself.** FMOD doubles a full command queue (32 KB → 64 KB → … 1 MB in the
  frozen runs), and each growth is a stall. The queue's peak tracks the start rate times how many
  60 Hz frames pass between Studio drains: about 210 bytes per one-shot.
  - On Windows a 10 ms Studio update period halved the peak at 2,900 a second (20 KB → 10 KB).
  - `timeBeginPeriod(1)` in the host process changed nothing.
- **Threads.**
  - Windows: FMOD's mixer runs at time-critical priority, one thread at highest, and the Studio
    update thread (the busy one) at normal.
  - Linux: every FMOD thread is `SCHED_OTHER`, nice 0. FMOD can't raise priorities without
    real-time rights.
  - Neither sets an affinity; all 16 cores are allowed.
  - The game's settings give 512 × 4 DSP buffers on Windows and 1,024 × 4 on Linux. Both get 128
    software channels, 512 voices, Studio's default 20 ms period and a 32 KB queue.
- **The #51 difference stays unexplained.** On the pile, Windows froze after 10 s and Linux after
  29 s, at 2,000–3,300 starts a second. These runs don't explain that from FMOD's settings: at
  equal rates Windows has more headroom. What differs is the rest of the frame: the pile's own
  work at 60 fps against Linux's uncapped frames, so a different number of commands per batch.
  That is an inference; it wasn't isolated.
- **Exact startup settings Moono could change.** These would go in the host before
  `Studio::System::initialize`, as `--audio-wav` already does:
  - a larger command queue: no growth stalls, but the freeze only comes later (#51);
  - a 10 ms Studio update period: half the queue's peak, and sounds start up to 10 ms sooner,
    with no change in which play.

  Neither keeps a stealing or virtualize event under its cliff, because the Studio thread's CPU
  runs out first and then the voices.

### How noisy real colonies are

Linux, Moono, speed 3, orthographic size 18, 60 s on each of three busy spots (the busiest
Duplicants and critters, the busiest buildings, and both), 761654 banks. Every instance created
was logged:

| Colony | Instances a second | Events heard | Of them, limit above 1 with stealing or virtualize | Share of instances | Voices, median / max | Studio CPU, median / max |
| --- | --- | --- | --- | --- | --- | --- |
| large colony 1 | 297 | 281 | 157 | 40% | 140 / 189 | 7.2% / 11% |
| large colony 2 | 232 | 335 | 178 | 20% | 169 / 213 | 4.5% / 7% |
| large colony 3, with voles | 70 | 145 | 63 | 44% | 69 / 85 | 2.1% / 4% |
| lab colony, without its mods | 16 | 129 | 42 | 32% | 35 / 54 | 1.5% / 3% |
| early colony | 12 | 89 | 27 | 5% | 34 / 52 | 1.3% / 2% |
| Prehistoric colony, cycle 1 | 34 | 89 | 29 | 23% | 44 / 57 | 1.7% / 3% |

"Demand" below counts the instances the game asked for that would still be playing, each for
the event's length. It is sampled every 10 ms over all three spots, before FMOD's limits. "Time
over 1" is the share of time a "1, none" cap would drop a sound that now plays; "time over 5" is
the same for a 5-instance cap. A stealing event already plays at most its limit. Changing it to
"none" at the same limit changes which instances play (the first instead of the newest), not how
many. The highest-rate events with a limit above 1 and stealing or virtualize:

| Event | Limit | Played by | Highest rate a second (colony) | Demand peak | Time over 1 | Time over 5 | Peak in one cell |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Rock_jump` | 5, quietest | Duplicants | 17.7 (large colony 1) | 73 | 85% | 72.5% | 17 |
| `Rock_creature_land` | 5, quietest | critters | 12.0 (large colony 3, with voles) | 69 | 27% | 26.8% | 48 |
| `BatteryCharger_small_working_electric` | 4, quietest | buildings | 6.2 (large colony 2) | 20 | 53% | 52.7% | 5 |
| `POI_Research_Unlock_malfunction_beep_add` | 4, quietest | buildings | 4.2 (Prehistoric colony, cycle 1) | 5 | 94% | 0.0% | 5 |
| `ColdBreather_output` | 8, quietest | plants | 4.0 (large colony 2) | 19 | 28% | 27.6% | 2 |
| `ColdBreather_intake` | 8, quietest | plants | 4.0 (large colony 2) | 21 | 28% | 27.0% | 2 |
| `TileBunker_creature_land` | 5, quietest | critters | 3.7 (large colony 3, with voles) | 25 | 27% | 25.9% | 25 |
| `DupVoc_04_sleep_inhale` | 7, quietest | Duplicants | 3.1 (large colony 1) | 10 | 29% | 13.9% | 2 |
| `LiquidVent_squirt` | 4, quietest | buildings | 3.1 (large colony 3, with voles) | 6 | 52% | 7.4% | 3 |
| `DupVoc_04_sleep_exhale` | 6, quietest | Duplicants | 3.1 (large colony 1) | 7 | 26% | 3.8% | 1 |
| `Sensor_Ping` | 8, quietest | buildings | 2.9 (large colony 3, with voles) | 8 | 57% | 21.8% | 2 |
| `Dupe_jump_boing` | 5, quietest | Duplicants | 2.7 (large colony 2) | 16 | 26% | 5.3% | 4 |
| `Battery_smart_rattle` | 8, quietest | buildings | 2.6 (large colony 1) | 9 | 57% | 12.7% | 3 |
| `Rock_scoot` | 5, quietest | Duplicants | 2.5 (large colony 1) | 8 | 24% | 0.9% | 3 |
| `ConveyorDropper_output` | 4, oldest | buildings | 2.4 (large colony 2) | 5 | 51% | 0.0% | 3 |
| `ConveyorIn_piston` | 12, oldest | buildings | 2.4 (large colony 2) | 8 | 31% | 0.2% | 4 |
| `POI_Research_Unlock_stuck_shake` | 4, quietest | buildings | 2.1 (Prehistoric colony, cycle 1) | 3 | 94% | 0.0% | 3 |
| `Sensor_Temperature` | 8, oldest | buildings | 1.9 (large colony 3, with voles) | 4 | 42% | 0.0% | 4 |
| `Liquid_Splat` | 5, virtualize | liquids, tiles | 1.8 (large colony 2) | 8 | 55% | 7.2% | 4 |
| `Laser_TakeOut_Build` | 2, quietest | Duplicants | 1.8 (large colony 1) | 6 | 5% | 0.0% | 5 |
| `Ladder_hand` | 5, quietest | Duplicants | 1.7 (large colony 1) | 6 | 10% | 0.2% | 4 |
| `MicrobeMusher_gross_working` | 4, quietest | buildings | 1.7 (large colony 1) | 11 | 28% | 18.2% | 4 |

- **Duplicants and buildings make the steady noise.** In large colony 1 `Rock_jump` played
  more than one copy 85% of the time and more than five 72%. "1, none" on Duplicant movement would
  be clearly audible: most of their jumps and lands would go silent.
- **Same-cell bursts come from critters.** `Rock_creature_land` reached 48 instances in one cell
  in the colony with voles, and `TileBunker_creature_land` 25. The busiest Duplicant event's
  same-cell peak was 17.
- **There is headroom.** No colony used more than 213 of the 512 voices, and Studio's CPU stayed
  under 11%.
- **The lab colony** is the save without its 35 mods, so it plays far less than it does with
  them.

### Ranking the crowd risks

By how often the game starts each event per object (code and the runs above), and by whether
the objects can crowd one spot:

| Rank | Events | Limit | Per object, at speed 3 | Crowds |
| --- | --- | --- | --- | --- |
| 1 | `*_creature_land` (24 materials; `FloorSoundEvent` on every landing and hop) | 5, steal quietest | about 2 a second per vole; 12 a second for one colony's voles | critters stack in a cell: the 1,500-vole pile froze; 48 in one cell in a real colony |
| 2 | `Squirrel_eat_LP` | 4, virtualize, never ends | one per `eat_loop` loop per Pip | not needed: it accumulates over time instead |
| 3 | `ShoveVole_movement` (one-shot), `ShoveVole_drill_LP`/`_emerge`/`_hide` (looping, stopped) | 5, steal quietest or oldest | voles moving and drilling | voles stack in a cell |
| 4 | `Creature_chew_<material>` (14; `CreatureChewSoundEvent`) | 5, steal oldest | each bite of an eating critter | ranches crowd a feeder |
| 5 | DLC5 critters' `eat`, `drink`, `excited`, `queue`, `flop` and `idle` events (`Blowfish_*`, `Snail_*`, `Squid_*`, `Merpip_*`, `Seahorse_*`, `Turtle_*` and their babies; 161) | 5, steal oldest | per loop of those animations, like the pre-fix ParrotFish | fish ranches crowd a tank; Klei changed only ParrotFish |
| 6 | Duplicant movement and voices (`Rock_jump`, `*_land`, `*_climb`, `DupVoc_*`) | 5–8, steal quietest | up to 18 a second for a whole colony | tens of Duplicants at most, not hundreds |
| 7 | Buildings (`Sensor_Ping`, batteries, conveyors, vents) | 4–12, stealing | 1–6 a second for a whole colony | one per tile; bounded by how many are on screen |
| 8 | Liquids and tiles (`Liquid_Splat`, 5 virtualize) | 4–8 | about 2 a second | bounded by what is on screen |

### What a fix should target

- **An exact fix isn't available.** FMOD exposes no per-event limit at runtime, the banks are
  Klei's, and the measured startup settings don't move the cliff.
- **By event list:** `*_creature_land`, `ShoveVole_movement`, `Creature_chew_*` and the DLC5
  critter events in rank 5. That is about 200 events, all played by critters, and Klei's own
  Beakon fix is this kind. It needs managed code, since the banks can't be changed: refuse a new
  one-shot of a listed event while one is already playing near the same point, rather than let
  FMOD steal. In real colonies that changes little: these events' same-cell peaks were 1–3,
  except in the colony with voles (48). A pile changes more, and audibly, as the #51 dedupe did.
- **By cause:** a per-frame, per-cell dedupe of one-shots, as built for #51, covers every crowd
  without a list. Real colonies show it would rarely fire outside critter piles. Keeping FMOD
  below its 512 voices covers the virtualize kind too. That guard would skip new low-priority
  one-shots when voices pass about 450, a state no colony above came near.
- **The leak:** start `Squirrel_eat_LP` as a looping sound (as the baby's is) or stop it when
  `eat_loop` ends. It is a game-data bug to report to Klei with the vole bank settings.
- **What to tell Klei:**
  - give `*_creature_land` and `ShoveVole_movement` the Beakon treatment;
  - give `Squirrel_eat_LP`'s sheet row the `LoopingSoundEvent` type;
  - the DLC5 critters in rank 5 have the pre-fix ParrotFish settings.

## Raising FMOD's voice limit (2026-11-04)

The question: the game initializes FMOD with 512 voices, real and virtual together (the
`maxchannels` of `Studio::System::initialize`), and 128 of them real. The sections above saw
the freezes start as the 512 ran out. Does a higher limit fix #51 and the related crowds, and
what does it cost?

**Verdict: no. A higher voice limit fixes none of the cases and delays none of them by a useful
margin.** The voices run out at the same moment the freeze starts, but they are not its cause.
FMOD's mixer and Studio threads run out of CPU first, and that cost follows the number of live
event instances, which the voice limit doesn't change. At 4,095 voices the vole pile freezes 10 s
after the camera reaches it, as at 512; 400 pre-fix Beakons freeze after 20–30 s at every limit;
the Pip eat-loop leak reaches the same Studio overload at the same minute. Below the limit
nothing changes either: a normal busy colony plays and sounds the same at 512 and 2,048. So
there is nothing to gain from raising it, and keeping 512 keeps the game's setting. The
corrections to the sections above are at the end.

### Where the limit is set

- `FMODUnity.RuntimeManager.Initialize` (FMODUnity.dll, U60 761654) reads the platform settings
  of the game's FMODUnity settings asset: `softwareChannels = Math.Min(RealChannelCount, 256)`
  for `coreSystem.setSoftwareChannels`, and `VirtualChannelCount` for
  `studioSystem.initialize(virtualChannelCount, DEFERRED_CALLBACKS, NORMAL, IntPtr.Zero)`. The
  asset gives 128 and 512 on Windows and Linux. FMODUnity's own defaults are 32 and 128; its
  play-in-editor platform uses 256 and 1,024. Assembly-CSharp doesn't touch either.
- If `initialize` fails, `RuntimeManager` retries once with `OUTPUTTYPE.NOSOUND` and then throws
  `SystemNotInitializedException`. A count FMOD rejects would therefore end in no sound at all
  or an exception at start-up (from the code; not run in game).
- A mod can't change it: Studio is initialized before mods load. For these runs a local,
  uncommitted hook did it, as the command queue test did: a Harmony prefix on
  `FMOD.Studio.System.initialize` that replaces `maxchannels`, in Moono's host (from
  `ONI_FMOD_VOICES` or `fmod-voices.txt` in the run home) and in the original's isolation plugin
  (`fmod-voices.txt` in the run home). Its postfix wrote the result: `maxchannels 512 -> 2048:
  OK; software channels 128` in every run.

### The cap

FMOD's old documentation (1.03) gave 4,093 as the maximum; the 2.03 documentation reviewed
here gives no number and suggests "a few hundred up to a thousand or more" for typical games.
The game's FMOD 2.03.12, called directly (`libfmodstudioL.so` on Linux, `fmodstudioL.dll` on
Windows, the game's settings, `NOSOUND_NRT`), then 10,000 `playSound` calls of a looping sound:

| `maxchannels` | `initialize` | Voices playing (real) | FMOD memory after `initialize` | With every bank loaded | With the voices full |
| --- | --- | --- | --- | --- | --- |
| 512 | OK | 512 (128) | 1.7 MB | 35.1 MB (Linux), 51.4 MB (Windows) | +0.3 MB |
| 1,024 | OK | 1,024 (128) | 2.8 MB | +1.1 MB | +0.5 MB |
| 2,048 | OK | 2,048 (128) | 5.0 MB | +3.3 MB | +0.9 MB |
| 4,095 | OK | 4,095 (128) | 9.2 MB | +7.5 MB | +1.8 MB |
| 4,096, 4,097, 8,192 | `ERR_INVALID_PARAM` (31) | – | – | – | – |

The cap is 4,095, on both platforms. A voice costs about 2.2 KB when FMOD starts and about
0.45 KB more while it plays. 4,092–4,095 behaved the same as 4,095.

### Method

- **Players**: Moono u60 (555e7907 plus the hook), release, default game tweaks; Windows on
  monitor 2 at 1280×720, `--no-focus`, frame cap 60 fps, under the Windows game lock, audio to
  FMOD's WAV writer (`--audio-wav`). Linux on Xvfb with the private ALSA sink. The original
  player was run for the vole pile only.
- **Test mod**: the #51 and survey mod (`VoleSwarm`), `timers=0`, logging once a second: frames,
  instances created, voices, Studio queue and handles, mixer and Studio CPU, `Memory_GetStats`,
  private bytes or RSS. For live instances it counted `getInstanceCount` per watched event.
- **Scenarios**, each at 512, 1,024, 2,048 and 4,095 voices where it mattered:
  - **A**: the 1,500-vole pile fixture, 30 s away, then the camera on the pile at size 20.
  - **B**: N Beakons in a 24 × 12 water tank on the early-base colony, with 733790's (pre-fix)
    `DLC5_Creatures.bank` swapped in at load, camera on the tank for 180 s. Standalone, the
    survey's `rate.py` started pre-fix `ParrotFish_idle` at one point at 400–2,000 a second.
  - **C**: a new mode of the mod: 16 Pip animations (`squirrel_kanim`, made with
    `FXHelpers.CreateEffect`) looping `eat_loop` on screen. The game's own anim events play
    `Squirrel_eat_LP` once per loop, as for an eating Pip, so this is the real path at a real
    per-Pip rate. 600 s on screen, then 60 s away.
  - **D**: the large colony 1 save, the survey's three busy spots at size 18, 60 s each.
- **Freeze**: the mod's watchdog logs every second the main thread finishes no frame; "frozen"
  is a stall that never ends (killed after 100 s).

### A: the vole pile

Windows, Moono. Times from the camera reaching the pile:

| Voices | Voices used, peak | Instances a second | Frozen from | Mixer / Studio CPU, peak before | Handles before | FMOD memory / private bytes |
| --- | --- | --- | --- | --- | --- | --- |
| 512 | 512 | 2,146 | 9 s | 161% / 86% | 276,000 | 368 MB / 8.2 GB |
| 1,024 | 1,024 | 2,167 | 9 s | 148% / 93% | 274,000 | 368 MB / 8.2 GB |
| 2,048 | 2,048 | 2,364 | 9 s | 150% / 87% | 279,000 | 369 MB / 8.1 GB |
| 4,095 | 2,709 | 2,178 | 10 s | 150% / 94% | 275,000 | 372 MB / 8.3 GB |

- Within 2 s of reaching the pile every run uses all its voices, and the mixer passes 100% in
  the same second. At 4,095 the pile never used all of them (2,709 at most) and froze all the
  same.
- Linux, Moono, 512 and 2,048: frozen from 18 s and 17 s, with a 6–11 s stall at 4 s in both
  (Linux runs shared the machine with the Windows runs).
- The original, with the hook in its isolation plugin: at 512 a 97 s stall from 4 s, then
  frozen from 112 s; at 2,048 frozen from 9 s, with all 2,048 voices in use (mixer 151%).

### B: Beakons on the pre-fix bank

In game, Windows, Moono, times from the spawn:

| Beakons | Voices | Instances a second | Voices used, peak | First stall of 5 s+ | Frozen from |
| --- | --- | --- | --- | --- | --- |
| 200 | 512 | 522 | 353 | none | no stall of 1 s in 180 s |
| 200 | 2,048 | 520 | 355 | none | no stall of 1 s in 180 s |
| 250 | 512 | 648 | 423 | none | no stall of 1 s in 180 s |
| 250 | 2,048 | 651 | 477 | none | no stall of 1 s in 180 s |
| 300 | 512 | 772 | 505 | none | no stall of 1 s in 180 s |
| 300 | 2,048 | 771 | 525 | none | no stall of 1 s in 180 s |
| 400 | 512 | 1,034 | 512 | 29 s | 56 s |
| 400 | 1,024 | 1,024 | 1,024 | 27 s | 51 s |
| 400 | 2,048 | 1,026 | 2,048 | 29 s | 55 s |
| 400 | 4,095 | 1,023 | 2,413 | 19 s | a 97 s stall from 40 s |

Standalone, pre-fix `ParrotFish_idle` at one point, Linux, 20 s per run ("over" means Studio
passed 100% CPU and its queue grew without end; repeats at 550 and 600):

| Starts a second | 512 | 1,024 | 2,048 | 4,095 |
| --- | --- | --- | --- | --- |
| 400, 500 | holds | holds | holds | holds |
| 550 | holds ×3 | – | holds ×3 | – |
| 600 | 3 of 4 hold | over at 14 s | 3 of 4 hold | over at 18 s |
| 700–2,000 | over in 2–4 s | over in 3–10 s | over in 3–4 s | over in 2–3 s |

- In game the line falls between 300 Beakons (770 instances a second, mixer 45%) and 400
  (1,030 a second, mixer past 100%), at 512 and 2,048 alike. 300 Beakons held at 512 with 505
  voices in use.
- Standalone the cliff sits at 550–600 starts a second at every limit. The runs at 600 go
  either way at 512 and at 2,048 alike.
- What climbs on the way is the mixer, not the voice count. At 500 a second (820 live, 460
  voices) the mixer uses 45% at 512 and at 1,024 voices alike; at 600 a second, 80–90%, then it
  passes 100% and live instances run away from 1,000 to 5,000 in a few seconds. At 600 a second
  with 1,024 voices, 550–670 were in use when the mixer passed 100%: there were voices to
  spare.
- So the earlier note that 1,024 voices moved this cliff was wrong: 400 a second holds at 512
  too, and 1,000 freezes at both.

### C: the Pip eat loop

16 Pips looping `eat_loop`, Windows, Moono:

| Voices | `Squirrel_eat_LP` live, gain a second | Voices full at | Studio past 100% at | Game stalls of 1 s+ in 600 s | Live after 600 s, then 60 s away |
| --- | --- | --- | --- | --- | --- |
| 512 | 61 | 10 s | 60 s (3,250 live) | 13, the longest 6 s | 12,048 → 12,064; Studio 252% |
| 2,048 | 61 | 40 s | 60 s (3,250 live) | 19, the longest 67 s (at 7 min) | 11,904 → 12,176; Studio 360% |

- The Pip plays `Squirrel_eat_LP` 3.8 times a second while it eats at speed 3, each a new
  instance that never ends. That makes the leak much faster than the survey assumed. One Pip
  eating on screen reaches the 3,250 instances where Studio passes 100% in about 14 minutes of
  eating; 10 eating Pips in about 85 s (estimates from the measured rate).
- Studio's CPU follows the live instances, not the voices: 7–10% at 550, 20% at 1,750, 40% at
  2,950, then past 100%, with 512 or 2,048 voices. Linux showed the same (Studio past 100% at
  50–60 s at both limits).
- After that the game keeps running at 60 fps most seconds, but stalls for seconds now and then,
  every synchronous FMOD call blocks (the mod's once-a-second queries took 1–6 s each), the
  command queue grows to 16 MB, and the audio falls behind: the WAV writer got about 60 s of
  audio for the 11 minutes.
- Taking the camera away doesn't help: the leaked instances stay, and so does the CPU. Only
  quitting clears them.
- More voices only change when the voices fill, which doesn't matter here. The one long stall
  came at 2,048; with one run each, that is no evidence either way.

### D: a busy colony at normal zoom

Large colony 1, the three busy spots, 180 s, Windows, Moono. Five runs at each limit (three
with music, two without); the ranges are of the runs' medians over their seconds:

| Voices | Frame avg | Instances a second | Voices used, median / max (real) | Mixer / Studio CPU, median | FMOD memory, max | Private bytes, max |
| --- | --- | --- | --- | --- | --- | --- |
| 512 | 25.0–32.8 ms | 313–350 | 132–139 / 173–201 (49–53) | 14.7–18.2% / 11.7–12.6% | 315.9–318.3 MB | 15.06–15.35 GB |
| 2,048 | 25.9–33.4 ms | 312–359 | 127–144 / 175–206 (47–53) | 14.5–16.5% / 11.5–12.3% | 318.2–320.7 MB | 14.86–15.08 GB |

No difference beyond run-to-run noise. The colony never comes near 512 voices, and FMOD's memory
is about 3 MB higher at 2,048, as the table of the cap predicts. On Linux (two runs each, sharing
the machine with the Windows runs) voices, mixer and Studio CPU matched the same way; its frame
times followed what else was running, not the limit.

### Audio

- **Below the limit it is the same.** In game no two runs give the same samples (music choice,
  frame timing, FMOD's random seed), so the check was made outside the game: `replay.py` plays a
  colony's logged one-shots (the survey's noise logs) through the game's FMOD and banks,
  non-real-time into a WAV file, with a fixed random seed. Large colony 1, 47,271 one-shots in
  200 s, peak 102 voices:

  | Pair | Identical samples | Difference RMS (signal 0.059) |
  | --- | --- | --- |
  | 512 and 2,048 | 97.7% | 0.0017 |
  | 512 and 512 again | 97.5% | 0.0019 |
  | 2,048 and 2,048 again | 97.5% | 0.0018 |

  Two runs at the same limit differ as much as runs at different limits (the first 17 s are
  identical sample for sample, then FMOD's own timing makes them drift apart), so the limit
  changes nothing measurable below it. Large colony 3 (8,251 one-shots, 50 voices; no repeat
  run) gave 81% identical samples and a difference RMS of 0.0029, against a signal of 0.029, at
  512 and 2,048.
- **In game, below the limit, the effects sound the same.** With the music muted, large colony 1
  at 512 (two runs plus one through the hook at 512), 1,024, 2,048 (two runs) and 4,095 voices:
  the spectrum over 170 s agrees within 0.3 dB in every band from 0 to 24 kHz, and the median
  loudness per second within 15% (888–1,023 RMS), with no order by limit.
- **The music differed, because of the hook, not the limit.** With music on, the three runs at
  512 without the hook had more music in the second half than the three at 2,048 (up to 8 dB
  more at 200–500 Hz over 120–170 s). Runs through the hook at 512 and at 1,024 matched the
  2,048 runs instead. The game picks its songs with `UnityEngine.Random`
  (`MusicManager`), and runs of one setup repeat each other closely here, so a change at start-up
  shifts the pick (an inference; the music path wasn't traced).
- **In the crowds** the extra voices make no audible difference, because the mix breaks down
  the same way at every limit. In A and B the WAV writer stops when the game freezes (53 s and
  37–38 s of audio, at 512 and at 2,048 alike). In C it falls behind: 79 s of audio in about
  11 minutes, with gaps of near silence.
- **Clips** for listening, in `C:\Users\Glyn\Downloads\fmod-voices\` (48 kHz stereo, from the
  WAV writer; A and B end where the game froze, and C's time runs slower than the game's once
  Studio overloads):
  - `A-vole-pile-512-voices-last20s-before-freeze.wav`
  - `A-vole-pile-2048-voices-last20s-before-freeze.wav`
  - `B-400-prefix-beakons-512-voices-last18s-before-freeze.wav`
  - `B-400-prefix-beakons-2048-voices-last18s-before-freeze.wav`
  - `C-pip-eat-leak-512-voices-whole-run.wav`
  - `C-pip-eat-leak-2048-voices-whole-run.wav`

### What it costs

- **Memory**: 2.2 KB a voice at start-up, about 0.45 KB more while it plays: +3.3 MB at 2,048,
  +7.5 MB at 4,095, and 1–2 MB more when they fill. Private bytes didn't move beyond noise.
- **CPU when they fill**: in the crowds the mixer and Studio pass 100% at every limit, so a
  higher limit's own cost can't be separated there. Where the voices fill without an overload
  (the Pip leak's first minute), 2,048 voices sometimes took more mixer CPU than 512: 54% against
  27% at 2,350 live instances on Windows, 71% against 56% on Linux, but level 10 s later (38%
  against 37%). One run each: an indication, not a measurement.
- **Frame time**: none measured in D (above).

### What to do instead

| Case | A higher voice limit | What helps |
| --- | --- | --- |
| A: one-cell pile of a stealing event | no effect: frozen at 9–10 s at every limit | the per-cell dedupe of #51 (measured: no stalls, 83 instances a second) |
| B: pre-fix Beakons | no effect on 400 (frozen at 20–30 s); the cliff stays at 550–600 starts a second | Klei's bank fix, already in U59 and U60 |
| C: Pip eat-loop leak | no effect: Studio overload at 60 s with 16 Pips at both limits | stop the leak: the sheet row as `LoopingSoundEvent`, or a game-side stop at `eat_loop`'s end (a Klei bug; a Moono tweak would change sound) |
| D: normal colonies | no change, a few MB | nothing needed |

- **Recommended combination**: keep 512; the per-cell dedupe for piles (#51); a fix for the
  `Squirrel_eat_LP` leak. A guard on live instances, as below, would catch the rest (not built).
- **A guard that does work**: the measure that predicts the overload is the number of live
  instances (about 1,000 of pre-fix `ParrotFish_idle`, 3,250 of the Pip loop, a few thousand
  for the pile), not the voices. At the game's 512, "voices above about 450" works only as an
  early warning: 300 Beakons held with 505 voices in use, and the Pip loop filled them 50 s
  before Studio overloaded.

### Corrections to the sections above

- "The freeze follows FMOD's voices" (event limits verdict): the voices fill at the same time,
  but the measured cause is mixer and Studio CPU, which follows live instances. Raising the
  limit to 4,095 moved no freeze.
- "With 1,024 voices instead of the game's 512, 400 a second stays up and 1,000 freezes":
  both hold at 512 too. The cliff is at 550–600 a second at 512, 1,024, 2,048 and 4,095.
- The Pip leak (`Squirrel_eat_LP`) now runs in game: about 3.8 never-ending instances a second
  per eating Pip at speed 3.

## The two sound fixes (2026-11-04)

The sections above found two causes of the #51 freezes that a game tweak can remove: the Pip
eat-loop leak, and one-shots piling up at one point. Both are now game tweaks in the Bug fixes
group, on by default and left out of `exact` (docs/TWEAKS.md, "The sound fixes"):
`fix-endless-sounds` and `fix-sound-piles`.

**Verdict: both freezes are gone, on Windows and Linux, U60 761654.** The 1,500-vole pile plays
at 60 fps on Windows for 150 s with no stall, where it froze 10 s after the camera reached it
without the fixes. 16 eating Pips hold 16 instances of their loop instead of gaining 3.8 a second
each. In six normal colonies `fix-endless-sounds` changed nothing (no eating Pip or crater
debris on the busy spots), and every copy `fix-sound-piles` skipped was of an event FMOD plays
one copy at a time, so it would not have played.

### What each fix changes

- **`fix-endless-sounds`** (src/OniMoono.GameTweaks/EndlessLoopSounds.cs). The game's audio
  sheets (the `SFXTags_*` text assets that `GameAudioSheets` loads) give each row a type, or the
  sheet's default when it has none. `SoundEvent` makes a one-shot per play; `LoopingSoundEvent`
  makes the same `SoundEvent` with `looping` set, which starts the sound once through the
  object's `LoopingSounds` and stops it when the animation stops. A prefix on
  `AudioSheets.CreateSound` turns these rows' type into `LoopingSoundEvent`, so the game's own
  constructor builds them:

  | Row (sheet) | Type | Event | Its sibling |
  | --- | --- | --- | --- |
  | `squirrel_kanim` `eat_loop` (Creatures) | `SoundEvent` | `Squirrel_eat_LP` (4, virtualize, loops) | `baby_squirrel_kanim` `eat_loop`: `LoopingSoundEvent`, `SquirrelBaby_eat_LP` |
  | `rocket_debris_combined_kanim` `landed` (Buildings) | none: the sheet's default, `SoundEvent` | `RocketDebris_steaming_LP` (4, virtualize, loops) | `railgun_capsule_kanim` `landed`: `LoopingSoundEvent`, `RailGunCapsule_steaming_LP` |

  The debris payload is what a self-destructed rocket leaves (`CraftModuleInterface
  .SpawnRocketDebris`); in its crater it loops `landed` (`RailGunPayload`'s `grounded.crater`,
  the same state as the railgun payload's). Both objects have `LoopingSounds`
  (`BaseSquirrelConfig`, `DebrisPayloadConfig`). A row is changed only when it is still a
  `GameAudioSheets` row of that file, animation and event, built as `SoundEvent`, and FMOD says
  the event is not a one-shot. The log says which rows it changed.
- **`fix-sound-piles`** (src/OniMoono.GameTweaks/OneShotPiles.cs). A prefix on
  `KFMOD.BeginOneShot(EventReference, Vector3, float)`, the call every positional one-shot goes
  through, keyed by event GUID, the grid cell of the position the game passes (the sound's own on
  screen; vertically scaled off screen) and `Time.frameCount`. Past 3 copies of one key, it
  returns no instance, as `BeginOneShot` does when FMOD isn't up. It counts only events FMOD calls
  3D one-shots (`isOneshot`, `is3D`, read once per event), not in `event:/UI/`, played anywhere
  but the origin (where the menu, UI and screen sounds play). Postfixes on both bool overloads of
  `SoundEvent.PlayOneShot` report a skipped copy as played; otherwise `SoundEvent.PlaySound` logs
  "SoundEvent has invalid sound" for each, 2,400 lines a second on the pile.

### Other rows with the same mistake

The sheets of 761654 and of 744825 (U59) have the same rows. Of the 59 events FMOD doesn't call
one-shots that a sheet plays as `SoundEvent` ([above](#what-the-banks-hold)), three never end:
the two above and the slush geyser's `erupt_water` row (`geyser_liquid_salt_water_cool_slush_kanim`,
no type, so `SoundEvent`: `Geyser_erupt_LP` and `Geyser_erupt_water_LP`, which every other
geyser's `erupt_water` row plays as `LoopingSoundEvent`). No code plays `erupt_water`: `Geyser`
plays `erupt`, which is a `LoopingSoundEvent` row for that geyser too. So that row can't leak and
is left alone.

### Method

- **Players**: Moono u60 with the fixes, release; "before" is the same build with
  `--game-tweak exact`, "after" adds `--game-tweak fix-endless-sounds --game-tweak
  fix-sound-piles`. Windows: 1280×720 on monitor 2, `--no-focus`, frame cap 60 fps, under the
  Windows game lock, audio to FMOD's WAV writer (`--audio-wav`). Linux: Xvfb, FMOD mixing in
  real time into the private ALSA sink. The #51 test mod (`VoleSwarm`, `timers=0`) logs once a
  second as above, plus the tweak's skip count (`OneShotPiles.Skipped`).
- **Vole pile**: the `vole-pile-1500.sav` fixture, 30 s away, then 60 s on the pile at size 20,
  45 s at 10 and 45 s at 6, at speed 3. The limit per cell and frame was set to 1, 2 and 3
  (`ONI_SOUND_PILE_LIMIT`).
- **Pip eat loop**: the mode of C above, with a `LoopingSounds` on each of the 16 Pip animations
  (a real Pip has one) and a cycle: 20 game seconds of `eat_loop`, then `eat_pst` and
  `idle_loop` for 20, so the loop starts and stops again and again. The same for 16 debris
  payloads looping `landed`.
- **Normal colonies**: the noise survey above (three busy spots, 60 s each at size 18, speed 3)
  on the four large colonies, the early colony and the Prehistoric colony. Once with the fixes,
  counting skips; once without, logging every one-shot with its frame and cell, so the copies a
  limit of 1, 2 or 3 would skip can be counted per event and checked against the event's limit
  in the banks (`all761654.jsonl` of the survey).

### Freezes

| Case | Platform | Before | After |
| --- | --- | --- | --- |
| Vole pile | Windows | frozen 10 s after the camera reached the pile (2,384 instances a second, Studio 91%, 512 voices); killed after 100 s frozen | limit 1, 2, 3: 60 fps on the pile at every size, no stall in 150 s; 71, 131 and 190 instances a second; Studio 5–8%; 40–58 voices |
| Vole pile | Linux | 5 fps on the pile, frozen 27 s after it reached it, for 200 s until killed | 5 fps on the pile at every limit, no stall in 150 s (1–6 s stalls only while quitting); Studio under 5% |
| 16 Pips eating, 20 s on, 20 s off | Windows, 300 s | `Squirrel_eat_LP` alive: 1,664 at 1 min, 6,848 at 5 min, never falling; Studio up to 99.5%, 30 stalls of 1 s or more | 16 while they eat, 0 after `eat_pst`; Studio at most 6.5%, no stall |
| 16 Pips eating, as above | Linux, 150 s | 944 at the first sample, 2,880 at 150 s | 16 while they eat, 0 between |
| 16 debris payloads in craters | Linux, 90 s | `RocketDebris_steaming_LP` alive grows by 3.2 a second per payload (3,040 at 80 s) | 16 throughout |

The Linux pile's 5 fps is the same before and after: it is the pile, not the fix (with the camera
away both run at 37–55 fps on that machine).

### How often they fire

`fix-sound-piles` on (limit 2 in these runs), three busy spots, 180 s:

| Colony | Positional one-shots made | Skipped | Mostly |
| --- | --- | --- | --- |
| large colony 1 | 45,755 | 807 | `Pacu_swim` (a fish tank) |
| large colony 2 | 42,624 | 4,125 | `Raptor_footstep`, `Raptor_VO_grunt` (a ranch) |
| large colony 3, with voles | 17,335 | 1,047 | `Liquid_footstep`, `ShoveVole_jump` |
| lab colony | 7,941 | 33 | `uw_footstep`, `Chlorinator_popping` |
| early colony | 7,880 | 0 | – |
| Prehistoric colony, cycle 1 | 11,650 | 0 | – |

From the runs without the fixes, the one-shots each limit would skip (counted ones only: 3D
one-shots off the origin), and how many of them FMOD would have refused anyway (an event of
limit k with no stealing, k at most the limit, plays no copy past its first k):

| Colony | Counted | Limit 1: skipped (refused anyway) | Limit 2 | Limit 3 |
| --- | --- | --- | --- | --- |
| large colony 1 | 43,936 | 3,914 (3,648) | 693 (686) | 224 (224) |
| large colony 2 | 40,255 | 7,261 (7,201) | 4,140 (4,140) | 3,029 (3,029) |
| large colony 3, with voles | 6,526 | 1,038 (1,034) | 410 (409) | 174 (174) |
| lab colony | 2,414 | 340 (340) | 42 (42) | 0 |
| early colony | 2,152 | 5 (4) | 0 | 0 |
| Prehistoric colony, cycle 1 | 5,193 | 1 (1) | 0 | 0 |

- At 3 every skipped copy is of a "1, steal none" event (`Pacu_swim`, `Raptor_footstep`,
  `Liquid_footstep` and the like), so in these colonies the limit changes no sound that plays.
  At 2, 8 copies in all could have played (`Rock_jump`, 5 stealing, in large colony 1); at 1,
  about 400. The tweak uses 3.
- With the fixes on, the in-game counts at limit 2 match the offline ones (807 against 693,
  4,125 against 4,140; the runs differ in what the colony does).
- `fix-endless-sounds` changed nothing in these colonies: no `Squirrel_eat_LP` or
  `RocketDebris_steaming_LP` instance was created on any busy spot, before or after.

### Audio

Windows, FMOD's WAV writer, 48 kHz stereo. In game no two runs give the same samples (frame
timing, FMOD's random seed), so the comparisons are of loudness. Clips for listening are in
`C:\Users\Glyn\Downloads\fmod-fixes\`.

| Clip | RMS | Peak |
| --- | --- | --- |
| Vole pile, music off: before, the first seconds on the pile before the freeze | −21.0 dBFS | −6.2 dBFS |
| Vole pile, music off: after, the first 8 s on the pile, limit 1 / 2 / 3 | −20.5 / −19.8 / −19.2 dBFS | −4.6 to −4.7 dBFS |
| Vole pile, music off: after, 20 s from 10 s on the pile, limit 1 / 2 / 3 | −20.0 / −19.9 / −20.0 dBFS | −3.6 to −3.9 dBFS |
| Same runs, the camera away | −20.8 to −21.7 dBFS | −4.7 to −4.9 dBFS |
| 16 Pips, 20 s from the start: before / after | −19.9 / −21.9 dBFS | −2.9 / −4.7 dBFS |
| 16 Pips, the last 30 s of 5 minutes: before / after | −39.1 / −24.1 dBFS | −13.9 / −3.6 dBFS |
| Large colony 1, music off, busy spots 1, 2, 3 (40 s each): before | −20.3, −22.9, −33.8 dBFS | |
| The same, after (limit 3; 155 skips in 180 s, nearly all `Pacu_swim`) | −20.0, −23.2, −32.8 dBFS | |

- **The pile** sounds about as loud with the fix as in its last seconds before the freeze, and
  each limit adds a little: the loudness rises 0.7 dB from 1 to 2 and 0.6 dB from 2 to 3 in the
  first seconds, where the voles land the most. Afterwards the pile is level at every limit and
  only about 1 dB above the colony around it: `Rock_creature_land` is capped at 5 instances by
  its bank either way. In the frozen run the mixer falls behind and the WAV writer stops 23 s
  after the pile is reached.
- **The Pips** are a little quieter at first with the fix: 16 copies of the loop, where the
  leak gains about 60 a second, of which FMOD plays 4 (4, virtualize). After 5 minutes the
  leak's mix is near silence with gaps (the WAV writer got 235 s of audio in 321 s), and the
  fix's is as at the start.
- **The normal colony** is the same within 1 dB at each spot, as two runs of one setup differ.
- Files:
  - music off: `vole-pile-before-nomusic-last20s-before-freeze.wav`,
    `vole-pile-after-limit{1,2,3}-nomusic.wav`;
  - music on: `vole-pile-before-last20s-before-freeze.wav`, `vole-pile-after-limit{1,2,3}.wav`,
    `pip-eat-loop-{before,after}-{first,last}30s.wav`;
  - music off: `normal-colony-{before,after}-busy-spot1-40s.wav`.

### Checks

- e2e specs, ours only: `fix-endless-sounds` (the two rows loop, their siblings and other rows
  are unchanged; of two Pip animations with `LoopingSounds`, the one still in `eat_loop` holds
  its sound after 20 s and the one that left it doesn't) and `fix-sound-piles` (ten Pip
  animations made in one cell while paused run in step, so their `Squirrel_voice_idle` starts
  together: more than 40 skips in 30 s). Both fail with the tweaks off. They pass on U60 761654
  and U59 744825, as do the Moono Tweaks and mod-overlap specs.
