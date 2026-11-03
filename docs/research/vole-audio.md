# 1,500 Shove Voles and audio (2026-11-03)

The question: a Bilibili video author (an unverified claim) says a save with about 1,500 Shove
Voles crashed instantly on load in Moono with Fast Track, blames audio ("these two mods only
optimize graphics, not audio, and the noise of 1,500 voles crashed the game"), and published a
mod that mutes critter sounds. Is that a crash of the original too, something Moono does
differently, or Fast Track, and is U60 different?

**Verdict: not reproduced, and audio is not where Moono differs.** A 1,500-vole save loads and
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
