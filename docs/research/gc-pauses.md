# GC pauses on a late-game colony (2026-11-07)

Status: done, shipped as the `post-load-gc` game tweak (on by default, exact; [TWEAKS.md](../TWEAKS.md), `src/OniMoono.Host/GameTweaks/PostLoadGc.cs`). After a load it runs two blocking gen1 collections while the game is still paused (GCLOAD=1,1 below). No collection was added after saving.

## Method

- Player: origin/master c71edf88, release, Windows, U59 744825 with every DLC (all-DLC Steam stub), a late-game colony (about 16 GiB working set), default tweaks (no-save-gc and background-save among them), speed 3, 1280x720 on monitor 2, --no-focus, High priority, volume 0, no --frame-stats.
- Each launch is the second in a fresh run home (a warm-up to the menu first). It held windows-game.lock, plus perf.wanted and an exclusive perf.lock. No Linux players ran during any measured launch.
- GC events: the runtime's EventPipe env vars (Microsoft-Windows-DotNETRuntime:0x1:5), analysed with a TraceEvent tool. It gives each pause from SuspendEE to RestartEE, the GC's global phase times, the memory load, mark time per root kind per heap, survivors and promotions, and the compaction reason.
- Frame pacing: a local PerfProbe addition (not committed) that logs every frame's real time.
- Prototype switches (PerfProbe, local, not committed):
  - `gcload`: runs the listed blocking non-compacting collections on the first frame under 100 ms after the load's long frames. The game is still paused then.
  - `gcsave`: a postfix on SaveLoader.Save that runs one blocking non-compacting gen1 collection. That is where the game's own GC.Collect() was, which no-save-gc removes.
- Batch 2 and the A/B ran with PLAY=330 s and three back-to-back autosaves after 9,000 frames (PerfProbe autosave.txt), all arms alike.
- Windows: summary2.py (load, post-load 90 s, steady state outside the autosaves' span), inplay.py (post-load pauses in play, without the load-end collections), saves.py (pauses in and after the autosaves).

## Baseline (default: Server GC, DATAS, 15 heaps in use)

5 minutes of play (batch 1, no autosaves): 69-90 fps traced and 80 untraced, p99 26-40 ms. Steady play: longest pause 48-59 ms, no pauses >= 100 ms, under 1 ms/s of GC. Suspension was never longer than 0.07 ms. Load 22.6-24.2 s, of which about 3 s is GC.

One hitch stands out: a gen1 pause of 283-344 ms, 42-84 s into play (worst frame 319-388 ms). It happens once, after every load. Just before it, a gen0 of 98-216 ms runs at the end of the load while the game is still paused.

| Pause | GC | mark (cards) | plan | relocate | compact | survivors |
|---|---|---|---|---|---|---|
| 325 ms (first gen1 in play) | gen1, compacting | 86 ms (85 ms cards, 1,089 MiB marked through them) | 52 | 91 | 80 | 1,008 MiB of gen1 copied into gen2 |
| 97 ms (gen0 at the end of the load) | gen0 | 63 ms (61 ms cards) | 19 | 8 | - | 590 MiB promoted into gen1 |
| 53 / 44 ms (steady gen1) | gen1 | 19 / 14 ms | 7 / 5 | 12 / 11 | 9 / 9 | 60-100 MiB |
| 26 / 23 ms (steady gen0) | gen0 | 12 / 9 ms | 2 | 7 | 3 | 39-45 MiB |

What dominates is promotion volume, not root scanning or suspension. Stack, handle and finalization roots take under 1 ms. Card-marking time follows the bytes reached through the cards, so it also scales with survivors.

The cause: the load allocates about 1.6 GiB after its own forced full collections. Under DATAS the gen0 budget is about 3,000 MiB across the heaps and gen1's is 2-6 GiB, so that data is still young when play starts. The first young collections then copy it twice: gen0 to gen1, then gen1 to gen2. Relocate plus compact is 170 ms of the 325.

## Candidates tried

- **A, GCGen0MaxBudget cap (rejected).** DOTNET_GCGen0MaxBudget=0x4000000 is 64 MiB per heap, about 1 GiB in all. It did not remove the post-load pause: a64-1 and a64-2 still had a gen1 of 320 and 395 ms. Steady play got worse: a 105-118 ms pause and 1.3-1.5 ms/s of GC, against 48-59 ms and 0.8-1.0 ms/s. A smaller budget only changes when the load's survivors are copied, not how much is copied.
- **Card marking** has no knob. The bitwise region write barrier is already the default and most precise one. GCCompactRatio exists only in GC_CONFIG_DRIVEN builds.
- **Satori** needs its own coreclr, clrjit and CoreLib, so it can't load through GCName on stock .NET 10. Skipped.
- **B, collections at the end of the load (batch 2, one launch each).** A collection only moves survivors up one generation: gen0 to gen1 and gen1 to gen2, even in a full collection. Pushing the load's young data into gen2 therefore takes two collections.

| Variant | load-end collections (game paused) | first young GCs in play (90 s) | later gen1 that copies it into gen2 |
|---|---|---|---|
| GCLOAD=1 (x-l1) | gen1 297 ms (587 MiB to gen1, 435 MiB to gen2) | gen0 70 ms | **196 ms** at 127 s (652 MiB of gen1 into gen2) |
| GCLOAD=1,1 (x-l11) | gen1 306 + gen1 88 ms = 393 ms (the second moves the 592 MiB left in gen1 into gen2) | gen0 47 ms | none: the next gen1 is 55 ms |
| GCLOAD=2 (x-l2) | gen2 497 ms, non-compacting (still leaves 583 MiB of gen0 survivors in gen1) | gen0 103 ms | **193 ms** at 142 s (653 MiB into gen2) |

GCLOAD=1,1 is the only variant that removes the hitch. It is also cheaper than one gen2 (393 ms against 497 ms) because it never marks gen2. A single gen1 or gen2 only moves the hitch later and makes it smaller. GCLOAD=1,1 is the variant chosen for the A/B.

## A/B (alternating, two launches each, plus untraced controls)

Order: default, 1,1, gcsave, both; then the same again; then untraced default and both. "Worst in-play pause" covers 90 s after the load and leaves out the load-end collections, which run while the game is paused (default arms have a gen0 there too). Steady state covers the rest of the run without the autosaves' span.

| Arm (run) | load s (GC s) | load-end collections, paused | worst in-play pause, first 90 s | worst frame, first 90 s | steady longest pause | steady pauses >=50 / >=100 a min | steady GC ms/s | p99 ms | fps | autosave longest frames ms | gcsave ms | gen1 after the saves |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| default (ab-def-1) | 24.6 (3.8) | gen0 195 | **283 gen1** | **319** | 77 | 0.99 / 0 | 1.49 | 40.8 | 59 | 3146, 2287, 2299 | - | 77 |
| default (ab-def-2) | 24.3 (3.6) | gen0 193 | **344 gen1** | **388** | 66 | 1.24 / 0 | 1.73 | 44.9 | 68 | 3125, 2348, 1999 | - | 64 |
| 1,1 (ab-l11-1) | 24.0 (3.4) | gen1 267 + 128 = 395 | 34 gen0 | 188 | 120 * | 0.52 / 0.52 * | 2.01 | 37.5 | 58 | 3690, 2836, 2544 | - | 120 * |
| 1,1 (ab-l11-2) | 23.4 (3.7) | gen1 282 + 158 = 439 | 49 gen0 | 176 | 56 | 0.40 / 0 | 0.77 | 25.5 | 100 | 2573, 2012, 2153 | - | 56 |
| gcsave (ab-sav-1) | 24.5 (3.5) | gen0 216 | **298 gen1** | **321** | 90 | 1.34 / 0 | 1.56 | 40.1 | 58 | 3133, 2313, 2608 | 10, 40, 2 | 90 |
| gcsave (ab-sav-2) | 22.5 (3.2) | gen0 181 | **287 gen1** | **320** | 46 | 0 / 0 | 0.72 | 24.0 | 106 | 2910, 2114, 2062 | 32, 8, 1 | 46 |
| both (ab-both-1) | 25.2 (3.7) | gen1 302 + 153 = 455 | 68 gen0 | 191 | 62 | 0.42 / 0 | 1.30 | 33.8 | 66 | 2949, 2404, 3167 ** | 28, 222, 32 | none >= 50 |
| both (ab-both-2) | 22.1 (3.1) | gen1 268 + 126 = 394 | 32 gen0 | 184 | 45 | 0 / 0 | 0.76 | 23.4 | 104 | 2936, 2113, 2028 | 2, 18, 33 | 45 |
| default, untraced (nt-def-r) | 25.1 (3.9) | - | - | **343** (at 48 s) | - | - | - | 48.0 | 61 | 3411, 1989, 2179 | - | - |
| both, untraced (nt-both) | 22.5 (3.5) | 399 (gcload log) | - | 176 | - | - | - | 45.3 | 67 | 2939, 2098, 2277 | 14, 2, 33 | - |

\* The 120 ms gen1 in ab-l11-1 ran 58 s after the third autosave. It copied what the saves had left in gen1, not load-time data. Default runs have the same collection at 64-77 ms, and the other 1,1 runs at 56 ms.
\** Autosave 3 in ab-both-1 includes a 974 ms blocking compacting gen2 at a memory load of 95% (see "Machine conditions"). It was triggered by allocation, not by gcsave: gcsave's collection there took 32 ms.

The ~180-190 ms frame left in the 1,1 arms comes about 20 s into play in every arm, default included, and no GC pause is near it. It is game work, out of scope here.

Machine conditions: fps varied with other load on the machine, not with the arm. Round 1, nt-def-r and nt-both ran with 25-35% average machine CPU beyond the player's own, peaking at 65-85%. Round 2 was quiet: ab-l11-2, ab-sav-2 and ab-both-2 averaged 24-31% total and reached 100-106 fps. Windows memory load during these runs was 83-90%, against 76% in batch 1. Above 90% the GC switches to blocking compacting gen2s, the cause of the one 974 ms pause. The first untraced default (nt-def) is discarded: the whole machine stalled (the 1 s memory sampler missed 30 s at a time), so its 42 s load and 17 s autosave are not the player's. It was redone as nt-def-r. Pause and promotion figures were stable across all of this. Read fps and p99 only within round 2.

### What the A/B shows

- **1,1 removes the post-load hitch.** Every 1,1 and both launch, traced or untraced (5 of 5, with x-l11 6 of 6), has no pause over 68 ms in its first 90 s of play. Default and gcsave have a 283-344 ms gen1 there in 6 of 6, with 319-388 ms frames (343 ms untraced). The worst post-load frame drops to the ~180 ms non-GC frame every arm has.
- **Load time cost:** the two collections take 394-455 ms, on a paused frame behind the load. That replaces the 181-216 ms gen0 the default already runs at the same point, so the net cost is about 0.2-0.25 s on a 22-25 s load. Measured load times (22.1-25.2 s) vary more than that between launches.
- **Steady play is unchanged.** Ignoring the post-save gen1 noted above, the steady longest pause is 45-62 ms with 1,1 against 46-90 ms without. GC ms/s, p99 and fps follow machine load, not the arm.
- **Memory:** working set at the end, 14.4-16.0 GiB for 1,1 and both against 15.2-16.1 GiB for default. The collections don't make memory worse; if anything they keep the young generation smaller.
- **gcsave does nothing measurable.** Its gen1 takes 1-40 ms (once 222 ms). Autosave freezes are 2.0-3.7 s with or without it, and are the save itself. The gen1 after the saves is 46-90 ms with gcsave against 64-77 ms without, which is noise. The saves don't leave a large young surplus the way the load does: background-save's buffers are short-lived or on the LOH.

## Recommendation (as shipped)

Ship the post-load collection (GCLOAD=1,1) as a default, exact tweak. Don't ship gcsave.

- **What it does:** once a colony has loaded, on the first fast frame while the game is still paused: GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: false), twice.
- **Gain:** removes the one-time 283-344 ms gen1 pause (a 320-390 ms frame) that every load of a large colony hits 40-85 s into play.
- **Costs:** about 0.4 s of collection behind the load screen, net about +0.2-0.25 s load time, since it replaces the gen0 that runs there anyway. No change to steady play, memory or saving. It is exact: collection timing changes nothing the game can observe, except finalizer timing, which the game's own forced collections already make arbitrary.
- **Scale:** colonies whose load leaves less young data gain less and pay less, because both collections cost in proportion to the survivors. On a small colony both are a few tens of ms. Only run it under Server GC (GCSettings.IsServerGC). With the low-memory option (Workstation GC), gen0 budgets are small, the load's survivors are promoted during the load itself, and there is no hitch to remove.

### How it was to be implemented (the tweak follows this)

- **A game tweak in OniMoono.Host/GameTweaks, not engine code.** The point to hook is "a colony finished loading", which is game knowledge. The engine only sees scene loads and frame times. Put it in the Saving group next to no-save-gc (on by default, exact, so it stays in `exact`), with a row in GameTweakNames such as `post-load-gc` ("No pause a minute after loading"), installed from GameTweaks.cs like NoSaveGc.
- **Hook:** a postfix on Game.OnSpawn starts a coroutine on Game.Instance. (The prototype instead polls for Game.Instance and SpeedControlScreen.Instance, the same point the PerfProbe "PERF colony" line marks.) The coroutine waits until a frame's real time is under 100 ms after the load's long frames, as GcProto.AfterLoad does (frame 9 of the colony here, still paused), then runs the two collections and logs the time, for example `[engine] post-load gc: gen1 282 ms, gen1 158 ms`. Running on the first fast frame matters. Running earlier, inside the load, the load's remaining allocations would end up young again. Running later, unpaused, the game is already playing.
- **Once per load:** a load from the main menu or from the pause menu goes through Game.OnSpawn again, so each one gets its collections. Starting a new colony also qualifies; it's cheap there.
- **Saving:** leave SaveLoader.Save alone. no-save-gc (default on) already removes the game's own full blocking GC.Collect() after each save. These runs show the collector copes with the saves on its own schedule, and a replacement gen1 there brings nothing. (NoSaveGc.cs's comment still calls it opt-in, but the tweak is on by default.)
- **Testing:** an e2e spec that loads a large save and checks for the log line, then checks that the first gen1 in play stays under 100 ms. GC.GetGCMemoryInfo(GCKind.Ephemeral) or the PERF memory line's collection counts and pause total are enough; no EventPipe needed. Plus check.sh. A perf run like this A/B confirms it; it doesn't need a dogfood pass.

## Runs

Kept locally, not in the repository: base1-3, base-notrace, a64-1/2 (batch 1); x-l1, x-l11, x-l2 (batch 2); ab-{def,l11,sav,both}-{1,2}, nt-def (discarded), nt-def-r, nt-both (A/B). Each has trace.nettrace (except the nt-* runs), pauses.csv, analysis.txt, framelog.csv, player.log and ws.txt. The analyzer now also prints the GC's memory load per collection.

## As shipped

`post-load-gc` follows the recommendation, with two additions. When every paused frame after
the load is slow (a slow PC), it collects on the colony's 30th frame instead of waiting for a
fast one; once the game is unpaused it skips rather than collect in play. Under Workstation GC
(the low-memory option) it does nothing. The log has one line a load:
`post-load-gc: 2 young collections, 394 ms (282 + 112), on the colony's frame 9, still paused`.
The e2e specs `post-load-gc` and `post-load-gc-off` (tests/e2e, U57, ours only, debug build,
a Linux lane, 270 game seconds at speed 3 after the load) check it. First runs, 2026-11-07:

| Run | load-end collections, paused | worst frame's GC pause in play | gen1 in play |
|---|---|---|---|
| tweak off | - | 338 ms | 1 (the hitch) |
| tweak on | 869 + 216 = 1,084 ms, colony frame 9 | 98 ms | 0 |
| tweak on, again | 389 + 102 = 492 ms, colony frame 9 | 86 ms | 0 |

The collections take longer there than on Windows (a debug build on a WSL2 lane), but they run
while the game is paused.

## Allocations in play (2026-11-08)

Where managed allocations come from during play, to see whether allocating less could make
collections rarer, and what a "collect only at autosave" scheme would cost in memory.
Measurement only.

**Method.** Linux player, release, default tweaks, speed 3, autosave off, the second launch in
its run home. 180 s of `dotnet-trace` with the runtime's GC keyword at verbose level
(GCAllocationTick with stacks, about one sample per 100 KB per heap), 90 s after the colony's
first frame-rate window. Each sample goes to the innermost frame that is not BCL or Harmony
code, with the owners of the frame inventory: game (Assembly-CSharp, -firstpass, Harmony
replacements of game methods), engine (OniMoono.*), tweaks (OniMoono.GameTweaks), uGUI/TMP.
Totals come from GC.GetTotalAllocatedBytes per 600 frames and agree with the samples within 2%.
Speed 1 runs (counters only) separate per-frame from per-game-second allocation. Runs with more
than 1.5 cores of other CPU were redone.

**Rate.** The late-game colony allocates 20 MiB/s at 46 fps, a mid-game colony 13 MiB/s at
43 fps. A fit over the speed 1 and speed 3 runs gives about 210 KiB a frame plus 3.3 MiB a game
second for the late-game colony, and 245 KiB a frame plus 1.2 MiB a game second for the
mid-game one. Most allocation is per frame, so it rises with the frame rate: at 144-160 fps it
comes to about 40 MiB/s at speed 3.

**Owners** (shares of the bytes, late-game / mid-game colony):

| Owner | Late-game | Mid-game |
|---|---|---|
| Engine | 70% | 77% |
| - own work: rendering, canvas batching, GPU uploads, particles | 25% | 40% |
| - Unity API the game calls: mesh setters, property blocks, immediate draws | 21% | 22% |
| - Unity API uGUI/TMP calls: CanvasRenderer.SetMesh | 14% | 4% |
| - object lifecycle: Instantiate, Destroy, SetParent | 10% | 11% |
| Game | 27% | 16% |
| Tweaks | 3% | 4% |
| FMOD and other game libraries | 1% | 1.5% |
| uGUI/TMP directly | 0.1% | 0.8% |

The engine is the largest owner. Most of its bytes are data Unity keeps in native memory, so
the original game never puts them on its managed heap. Only the Instantiate path partly
matches managed allocations that Unity makes too (the cloned components).

**Largest sites** (late-game / mid-game colony, MiB/s):

| Site | Late | Mid | Avoidable without behaviour change |
|---|---|---|---|
| Mesh channel setters (MeshNative.NewChannel, Mesh.Widen): conduit flow meshes, falling water, uGUI | 3.0 | 0.4 | Yes. Spares are reused only at the same length, and these meshes change vertex count every frame. UVs are stored widened to Vector4. |
| Canvas batch merges (CanvasSystem.Concatenate) | 2.3 | - | Yes: write into the merged mesh's previous arrays. |
| CanvasRenderer.SetMesh copies (CanvasGeometry.From) | 2.2 | 0.5 | Yes: reuse the previous copy when no batch holds it. |
| MaterialPropertyBlock copy-on-write (PropertySheet: eight dictionaries made up front, then copied and grown) | 1.3 | 2.4 | Yes: lazy dictionaries, sheets recycled after their frame renders. |
| Draw-state cache misses (ResolvedState.Of: a new ResolvedFor when a material is drawn with alternating passes) | 0.7 | 1.6 | Yes: a cache per pass. |
| Instantiate (Cloner) | 1.5 | 0.8 | Partly: the per-call remap dictionary, native-side objects and boxing in the field copier are ours; the cloned components are not. |
| GPU upload records (TexelUpload, MeshUpload, layouts, index flattening) | 1.2 | 1.0 | Yes: pool them. |
| Particle geometry (new lists and arrays per system per frame) | - | 0.9 | Yes: keep the builder, reuse channel arrays. |
| Game: RemoteChore.CollectChores boxing a precondition context | 0.8 | - | Game code. |
| Game: UILineRenderer vertex arrays (diagnostics charts) | 0.6 | - | Game code. |

The engine's avoidable sites above come to about half of all allocated bytes in both colonies.

**Survival.** Each gen0 collection (about one a minute here) promotes 40-60 MiB of the roughly
0.9 GiB allocated since the last one, about 6%. A gen1 every 2-3 minutes moves 100-135 MiB
into gen2. Survival by owner was not measured. The object-creation paths (Instantiate, OnSpawn,
anim batches, state machines; 16-20% of the bytes) make the long-lived objects, while the
engine's per-frame arrays die within a frame or two. Allocating less per frame therefore makes
collections rarer but not much shorter.

**Per cycle.** A cycle is 600 game seconds, 200 s of real time at speed 3.

| | Late-game | Mid-game |
|---|---|---|
| Measured, Linux, speed 3 (46 / 43 fps) | 4.0 GiB | 2.6 GiB |
| Measured, Linux, speed 1 (65 / 54 fps) | 9.8 GiB | 8.2 GiB |
| Projected, speed 3 at 144 / 160 fps | 7.7 GiB | 8.2 GiB |
| Projected, speed 1 at the same fps | 19 GiB | 23 GiB |
| Resident memory in play | 14.6 GiB | 10 GiB |

**Collecting only at autosave** would have to hold a whole cycle's allocations on top of the
working set: on a fast PC about 22 GiB (late-game) and 18 GiB (mid-game) at speed 3, and
33-34 GiB at speed 1,
plus a collection at each save that copies the cycle's survivors (likely a few hundred MiB,
comparable to the post-load hitch). Capping the frame rate would shrink it. Cutting the
engine's avoidable per-frame allocations would lower both this and the ordinary collection
rate.

## GC strategies elsewhere (2026-11-09)

What shipped games and engines do about GC pauses, and which .NET 10 knobs still apply under
Server GC with DATAS. Labels: [doc] official documentation, [src] read in source at the linked
commit, [claim] unverified community statement, [inf] inference. Runtime source is
dotnet/runtime v10.0.0, `src/coreclr/gc/`.

**Unity's incremental GC doesn't carry over.** [doc] Unity's collector is Boehm: non-generational,
non-compacting. Its incremental mode marks in per-frame slices (default 3 ms, "only a guideline,
not a hard cap"), adds write barriers to reference stores, and falls back to a full collection
when references change faster than it can mark
([manual](https://docs.unity3d.com/Manual/performance-incremental-garbage-collection.html),
[time slice](https://docs.unity3d.com/ScriptReference/Scripting.GarbageCollector-incrementalTimeSliceNanoseconds.html)).
Unity itself is moving to CoreCLR's precise, generational, compacting GC
([blog](https://unity.com/blog/engine-platform/porting-unity-to-coreclr)). [doc] CoreCLR's
background GC covers gen2 only; gen0 and gen1 always stop every managed thread
([background GC](https://learn.microsoft.com/dotnet/standard/garbage-collection/background-gc)).
[inf] Slicing helps a collector that marks the whole heap each time. Our young collections
never mark gen2, and their cost is the card scan plus copying, which only a concurrent-copying
collector (Satori, ruled out above) could spread over frames.

**.NET knobs under Server GC with DATAS on .NET 10:**

- `GCSettings.LatencyMode`: [doc][src] `LowLatency` is Workstation-only, and the setter silently
  ignores it under Server GC (gc.cpp L52093). `SustainedLowLatency` is honoured, but next to the
  default `Interactive` it only stops fragmentation from forcing a blocking gen2 (L22323). It
  still allows the high-memory-load blocking gen2s (L22240-22283) and DATAS's compacting gen2
  (L21278), and it leaves gen0 and gen1 alone. `Batch` turns background GC off.
  ([latency modes](https://learn.microsoft.com/dotnet/standard/garbage-collection/latency))
- `GC.TryStartNoGCRegion`: [src] with regions there is no ephemeral-segment size limit
  (L23582), whatever the [API docs](https://learn.microsoft.com/dotnet/api/system.gc.trystartnogcregion)
  say. On entry it commits the space, or runs a full blocking GC unless `disallowFullBlockingGC`
  is set (L52210). At our allocation rate a 2 GiB region lasts 1-2 minutes.
- Gen0 budget: [src] without `GCGen0MaxBudget`, DATAS caps each heap's gen0 budget at 64 MiB
  (gcpriv.h L5084). DATAS's own formula
  ([docs](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#dynamic-adaptation-to-application-sizes-datas))
  gives about 1.7 GB on a 13 GB heap, but with 15 heaps the cap binds first, at about 0.94 GiB
  in all. That matches the 0.9 GiB per gen0 under "Allocations in play", and it explains
  candidate A: 64 MiB is already the default cap. `GCGen0MaxBudget` (env or
  `System.GC.Gen0MaxBudget`) raises the cap; the .NET 10 `GCDGen0GrowthPercent` scales the
  formula. `GCgen0size` (env only) sets a per-heap floor, but it also limits how many heaps
  DATAS may grow to (gc.cpp L26066). `GCGen1MaxBudget` can only lower gen1's cap, which
  defaults to 512 MiB a heap here.
- `GCConserveMemory`: [src] DATAS turns 0 into 5 (L14478), which brings a blocking compacting gen2
  at 50% gen2+LOH fragmentation (L21278). [doc] DATAS runs more gen0 and gen1 collections than
  classic Server GC ([DATAS](https://learn.microsoft.com/dotnet/standard/garbage-collection/datas)).
  `GCHeapCount` turns DATAS off (L14472). `GCHeapHardLimit` only constrains the heap.

**Shipped .NET games:**

- **tModLoader:** [src] the claim that it uses SustainedLowLatency isn't true of its source. At
  1.4.5 (net10.0, [df905cb3](https://github.com/tModLoader/tModLoader/blob/df905cb3592bc1f087c2d32aca625cfb34cdfd20/patches/Terraria/Terraria/runtimeconfig.template.json))
  nothing sets a latency mode, Server GC or concurrency. The runtimeconfig sets only the stack
  size, so it runs Workstation concurrent GC; its `GC.Collect` calls are on mod reload.
  Vanilla Terraria can't be checked without decompiling.
- **Stardew Valley 1.6:** [src] SMAPI's runtimeconfig mirrors the game's and sets only TieredCompilation
  off ([SMAPI](https://github.com/Pathoschild/SMAPI/blob/e5b8bf3d601bec0099252c8ee2f54e5571385f1a/src/SMAPI.Installer/assets/runtimeconfig.json)).
- **Celeste (Everest):** [src] no GC properties. The level-transition `GC.Collect()` becomes a
  forced gen1 on a background task by default
  ([Level.cs](https://github.com/EverestAPI/Everest/blob/54f09a60451bbd47e83c6c8317311ffa8e3e26b4/Celeste.Mod.mm/Patches/Level.cs#L623)).
  The pause is the same, since all threads stop.
- **FNA/MonoGame:** [src] no GC configuration or guidance beyond avoiding allocation.
- **Space Engineers:** [src] .NET Framework with a default GC, and full collections at session
  load and unload ([MySession.cs](https://github.com/KeenSoftwareHouse/SpaceEngineers/blob/54f2f0f3169cda687a25a438097902a43bdfa603/Sources/Sandbox.Game/Game/World/MySession.cs#L696)).
- **Vintage Story:** [claim] a forum `app.config` with `gcServer`
  ([thread](https://www.vintagestory.at/forums/topic/15208-fix-for-garbage-collection-pauses/)),
  with no measurements. [doc] app.config is read only by .NET Framework.
- **KSP (Unity Mono), for contrast:** [claim] MemGraph/HeapPadder pad the Boehm heap so it collects
  less often ([MemGraph](https://github.com/Gerry1135/MemGraph)).

None of these games tune the collector. They collect at scene boundaries, as `post-load-gc`
does, and avoid per-frame allocation. [inf] Our engine's per-frame arrays stored into
long-lived objects also dirty cards, so the reuse items under "Allocations in play" would cut
card-marking time as well as collection frequency.

**A/B worth running** (late-game colony, `post-load-gc` on, alternating, two launches each,
10 min or more of play). Log the heap count and the per-GC gen0 budget, because the 4-heap cap
in [gc-modes.md](gc-modes.md) paused 4-5x longer.

| Arm | Settings | Expected |
|---|---|---|
| control | as shipped | gen0 ~0.94 GiB, about one a minute |
| 2x budget | `DOTNET_GCGen0MaxBudget=0x8000000`, `DOTNET_GCDGen0GrowthPercent=0x96` | gen0 ~1.9 GiB, ~2x rarer and a little longer; gen1 somewhat rarer, about as long; +~1 GB |
| 4x budget | `DOTNET_GCGen0MaxBudget=0x10000000`, `DOTNET_GCDGen0GrowthPercent=0x12C` | gen0 ~3.8 GiB, ~4x rarer; +~3 GB |
| classic Server | `DOTNET_GCDynamicAdaptationMode=0` | 16 heaps at up to 200 MiB, no conserve-5 compaction; +2-3 GB |
| SLL control | `SustainedLowLatency` at start | no change to gen0/gen1 |
| gen1 cap (optional) | `DOTNET_GCGen1MaxBudget=0x8000000` | more gen1s; whether each gets shorter shows how much of gen1 is copying rather than cards |
