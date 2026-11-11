# Fast Track's options under Moono (2026-11-10)

Status: measured. No code changes came out of it. Fast Track (Peter Han's performance mod, version 0.18.5) has 51 options. This doc flips each one away from its default, one at a time, on a heavily modded colony with 139 mods. It records which game systems each option moves, in milliseconds per frame, and what a Moono player should do with it.

Short version: keep Fast Track's defaults, with three exceptions.

- **Don't turn on Disable Sound.** With this game build, Fast Track fails to load and the game quits at the main menu (see "Disable Sound" below).
- **Leave Metrics off.** It doubles Fast Track's own time per frame (2.2 to 4.4 ms).
- **Use Mesh Renderers can stay on All.** Before Moono's culling fixes, All took this colony from 68.9 to 48.6 fps on Linux. Now All and None measure the same.

The other options either move less than the measurement can see, or move work from Fast Track to the game and back for about the same cost.

## Method

- **Player:** origin/master `513b4e0f`, release build, Linux (WSL), in the capped test slice (`scripts/contain.sh`), Xvfb at 1280x720. Default Moono tweaks, no settings file. Game build 744825 with Spaced Out.
- **Colony:** a heavily modded late-game colony with 139 mods, Fast Track among them. All of them passed the mod security gate. PerfProbe loads the save and keeps it at speed 3, with autosave off.
- **Arms:** a baseline launch uses Fast Track's defaults. Each option arm writes a Fast Track config into the run home that differs from the defaults in that one option. For multi-valued options, the arm takes the value furthest from the default (Use Mesh Renderers: None; Chore Priority: Normal; pipe animation: Minimal; Disable Achievements: Always).
  - Options that act only on screens a steady-state run never opens, or only while loading, ran together as one arm: Disable Load Previews, Disable Tutorial, Optimize Dialogs, Side Screen Opts, the watermark, No Placer Easing, Cluster Map Reduce, and Fast Track's hidden mod-load option.
  - Load Opts ran on its own; its measure is load time.
- **Each launch:**
  - Load the colony.
  - Play for 60 s.
  - Over the next 150 s: a perf capture of every thread (cpu-clock, 499 Hz, call stacks) plus PerfProbe's per-frame log.
  - Screenshots at the start, middle and end of the 150 s.
  - The perf capture costs the same in every arm; fps under it is lower than without it.
- **Analysis:** the per-system profile is the main evidence. It gives inclusive main-thread milliseconds per frame for about 110 named systems, and each thread's busy time.
  - The named systems include Fast Track's own code by namespace, the game's brain, sensor, fetch and conduit systems, rendering, and the canvas.
  - Each option arm is compared with the two baselines run closest to it in time.
- **Taking turns:** the machine was shared with other jobs.
  - A launch counted only if no other player ran during it and other processes used under 1.5 cores in its window. That figure includes our own Xvfb, about 1 core.
  - Launches that broke either rule were redone.
- **Count:**
  - **Current build:** 54 clean launches, 13 of them baselines. Every option has one launch, and eight have two.
  - **Earlier build:** 12 launches on `8e7cf48e`, from before allocation batches 2 and 3. They are compared only with that build's own baselines and are listed separately below.

### How far to trust a single launch

Baselines on the current build ran 75-96 fps: mean 87.6, sd 6.1 fps; frame time 11.47 ms, sd 0.85 ms.

The spread grew during the series as other work loaded the machine. Early in the series the sd was 2.6 fps (0.31 ms). Later the load average was 18-24, from tests that don't take the perf queue.

So a whole-frame change under about 1 ms from one launch means nothing on its own. What holds up is the systems an option itself touches: Fast Track's own code, the game system it replaces, and a background thread's busy time. Those barely move between baselines (sd 0.02-0.15 ms) even when the whole frame drifts.

Rows below marked **1 launch** have a single launch; treat their frame times as a direction at most.

## Results

Frame time is the arm minus its nearest two baselines (negative is faster). "Systems" lists what moved by more than the baselines' own spread.

### Options with a clear effect

| Option (tested value) | Launches | Frame | Systems | Recommendation |
|---|---|---|---|---|
| Use Mesh Renderers (None; default All) | 1 | -0.01 ms | Fast Track visual patches -0.12 ms, culling -0.09 ms (0.26 to 0.17 ms) | Keep All. No cost under Moono now. |
| Metrics (on; default off) | 1 | +0.67 ms | Fast Track's code 2.2 to 4.4 ms per frame (its profilers wrap game updates) | Keep off. Debug option. |
| PickupOpts (off; default on) | 2 | -0.87 ms | Fast Track's async brain updater -0.69 ms and path patches -0.73 ms; the game's BrainScheduler +0.54 ms, sensors +0.17 ms, fetch +0.18 ms | Keep on. The trade is close to even and the frame gain sits inside the noise. Turning it off would be the first thing to re-test on a quiet machine. |
| CachePaths (off; default on) | 2 | +0.04 ms | The game's async path-probe worker goes from 4% to 81% busy, about 9 ms per frame of background CPU. 84% of its time is in `PathProber.Run`. | Keep on. Without Fast Track's path cache the game runs full probes. |
| ReduceTileUpdates (off; default on) | 1 | -0.49 ms | Fast Track's threaded property-texture updater (-0.37 ms) is replaced by the game's own `PropertyTextures.LateUpdate` (+0.41 ms) | Either. The work just moves. |
| InfoCardOpts (off; default on) | 1 | +0.19 ms | Fast Track's hover code -0.13 ms, the game's hover card +0.14 ms | Either. Even swap. The cursor rested over the colony, so the hover card showed in every launch. |
| FastReachability (off; default on) | 1 | +0.16 ms | Fast Track's cell-change monitor -0.19 ms, the game's +0.21 ms | Either. Moono's instant-reachability doesn't take over (see "Stand-downs"). |
| ConduitOpts (on; default off) | 2 | -0.34 ms | The conduit updates leave the main thread (`Game.LateUpdate` -0.58 ms in the profile); pipe visualisers +0.06 ms | Small gain, inside the noise. On is safe to try. |
| MinimalKCV (on; default off) | 1 | +2.01 ms | Object destruction +0.31 ms, Fast Track's code +0.30 ms; load +5.5 s | Keep off. One launch, and it fell in a noisy stretch, but every part of it pointed the same way. |

### Disable Sound

**Don't turn this on with game build 744825.** With Disable Sound on, Fast Track's `TurnOffSoundsPatch` asks Harmony to patch a list of sound methods. One of them is `AudioMixer.SetSnapshotParameter(string, string, float, bool)`, and this build has no such overload. It has `SetSnapshotParameter(EventReference, string, float, bool)` and a private `(GUID, string, float)`.

The lookup returns null, and Harmony rejects the list: "TargetMethods() returned an unexpected result: some element was null". The game then disables Fast Track as a failed mod, and its mod-error dialog quits after 3 frames. All three launches ended this way.

The cause is a method signature in the game's code, not anything in Moono, so the original game should fail the same way. That was not checked on the original.

### Options without a clear effect

None of these moved a system it owns by more than the baselines' spread. The frame changes are listed for completeness; at one launch they are noise.

| Option (tested value) | Launches | Frame | Notes |
|---|---|---|---|
| FastStructureTemperature (off) | 2 | -0.27 ms | |
| AsyncPathProbe (off) | 2 | +0.84 ms | Both launches slower, but no single system stands out. |
| AnimOpts (off) | 1 | -0.29 ms | Fast Track's visual patches -0.21 ms. Moono's fast-anim-batches doesn't take over (see "Stand-downs"). |
| Pipe animation (Minimal; default Full) | 1 | +0.71 ms | |
| CullConduits (off) | 1 | +0.71 ms | |
| RenderTicks (off) | 1 | +0.45 ms | |
| NoTileDecor (on) | 1 | -0.24 ms | |
| NoSplash (off) | 1 | -1.03 ms | Next to slow baselines; whole-frame drift. |
| VirtualScroll (off) | 1 | -1.52 ms | Next to slow baselines; whole-frame drift, no single system. |
| SensorOpts (off) | 1 | +0.79 ms | |
| FastUpdatePickups (off) | 1 | +0.58 ms | |
| ParallelInventory (off) | 1 | +0.05 ms | |
| ChorePriorityMode (Normal; default Higher) | 1 | +0.83 ms | |
| FastAttributesMode (off) | 1 | -0.79 ms | |
| NoConversations (on) | 1 | -0.70 ms | |
| NoDisease (on) | 1 | +0.60 ms | Removes the colony's germs; gameplay change. |
| CustomStringFormat (off) | 1 | +0.52 ms | |
| ReduceDuplicantIdleMove (on) | 1 | -0.50 ms | |
| ReduceCritterIdleMove (on) | 1 | -0.25 ms | |
| ReduceSoundUpdates (off) | 1 | -0.47 ms | |
| Disable Achievements (Always; default sandbox/debug only) | 1 | -0.97 ms | |
| ReduceColonyTracking (on) | 1 | +0.09 ms | |
| NoBounce (off) | 1 | +1.80 ms | Every system rose together, the pattern of a busy machine, not of this option. |
| AllocOpts (off) | 1 | -0.04 ms | |
| MiscOpts (off) | 1 | +0.19 ms | |
| LoadOpts (off) | 1 | +0.81 ms | Load 2.5 s longer, inside the 3-9 s spread of load times in this series. |
| Load and screen options, together (flipped) | 1 | +0.13 ms | Load 3.6 s shorter, also inside the spread. |

On the earlier build (`8e7cf48e`), one launch each, nothing stood out against its baselines (sd 1.55 fps): BackgroundRoomRebuild, ENetOpts, FlattenAverages, LogicUpdates, NoReports, RadiationOpts, CritterConsumers. FastStructureTemperature off and ConduitOpts on measured -0.55 and -0.52 ms there. On the current build, with two launches each, they came out at -0.27 and -0.34 ms.

## Stand-downs: which Moono tweaks Fast Track turns off

With Fast Track's defaults, three default Moono tweaks stand down. Each logs why. Flipping the Fast Track option that looks responsible doesn't bring the tweak back in any of the three. Each case comes from something else patching the same code, and standing down is the safe choice in every one.

- **instant-reachability:** off with the defaults because Fast Track patches `ReachabilityMonitor.InitializeStates` (Fast Reachability).
  - With Fast Reachability off, it still stands down, now for Fast Track's prefix on `OffsetTracker.GetOffsets`, which PickupOpts adds.
  - That prefix skips the game's method and, inside jobs, defers the offset tracker's cell update to later. instant-reachability relies on those offsets being current.
  - It comes back only with both Fast Reachability and PickupOpts off.
- **fast-anim-batches:** off with the defaults because Fast Track's AnimOpts replaces `KAnimBatch.UpdateDirty`.
  - With AnimOpts off but Use Mesh Renderers still on, Fast Track adds a postfix on the same method instead. The postfix copies each changed batch into Fast Track's mesh renderers.
  - fast-anim-batches writes batches its own way, so that postfix wouldn't keep up, and Fast Track's mesh-rendered animations would go stale.
  - It comes back only with AnimOpts off and Use Mesh Renderers on None.
- **path-probe-repair:** off with the defaults because Fast Track's Cache Paths patches `PathProber.Run`.
  - With Cache Paths off it still stands down in this mod set, because the Stairs mod patches `MinionPathFinderAbilities.TraversePath`.
  - The 9 ms per frame of background probing in the CachePaths row is the game's own path prober, not Moono's.

## Correctness

- No launch logged an error, apart from the Disable Sound failure above.
- The two "Exception" lines in every log, baselines included, are Harmony dumping a patched method's IL (`EX_BeginException`, `EX_EndException`).
- Screenshots showed no rendering problems in any arm, Use Mesh Renderers None and NoTileDecor among them.

## Windows

Pending: on/off pairs for Use Mesh Renderers All vs None, PickupOpts, Metrics and Disable Achievements Always.
