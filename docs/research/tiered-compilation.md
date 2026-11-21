# Tiered compilation and the slow load frame

Measurement, 2026-11-21, on master (27d9f9c85, U60 764025), Windows, our player only. The
question: does .NET's tiered compilation explain why the colony's second paused frame after a
load (`World.UpdateCellInfo`'s first big pass) is slower in ours than in the original (1,767 vs
1,002 ms on the #105 reporter's colony)?

**Verdict: yes.** That frame runs tier-0 code. With the runtime's default 100 ms call-counting
delay, which every new tier-0 compile extends, nothing tiers up during a load: the load frame and
the load itself run unoptimised, PGO-instrumented code, while 1.2 s of tier-1 JIT happens on
background threads during that frame. Each of the three knobs cut the frame by 2 to 5 times. They
cut the load by 4 to 8 s, and steady frames did not get worse. `DOTNET_TC_CallCountingDelayMs=0`
did best. No default was changed.

## How it was run

`tests/e2e/unpause-probe-ice.yaml` (the #105 colony; a local fixture) and
`tests/e2e/unpause-probe.yaml` (U57), through `scripts/e2e-windows/run.py --player ours` in one
runq `win-perf` job per save (High priority, `--no-focus`, monitor 2, muted). Each job ran 5
launches in this order: base, the arms, base again. There was one launch per arm, so the numbers
are single samples (the two bases show the spread). The variables reached the game through
`WSLENV`, and the probe logged the process's `DOTNET_*` variables to show them. `ONI_STARTUP_STATS=1`
was on in every arm.

- **Load**: from "Loading save file" to the end of the colony's frame 3, the GC pause after
  the slow frame.
- **Frame 2**: the slow paused frame, with `World.UpdateCellInfo` and the JIT's work in that
  frame (all threads / main thread).
- **First 10**: the first ten unpaused frames, summed (speed 1, ortho 20).
- **Steady**: the median frame and median `Game` (the update's CPU) of the later frames at
  speed 3, ortho 50. On the colony those frames hit the 16.6 ms vsync cap, so only `Game`
  shows the CPU there.

## Results

The #105 colony:

| Arm | Menu | Load | Frame 2 | UpdateCellInfo | JIT in frame 2 | JIT to colony, all / main | First 10 | Steady frame / Game |
|---|---|---|---|---|---|---|---|---|
| base (first) | 23.7 s | 18.0 s | 1,900 ms | 1,744 ms | 1,189 / 17 ms | 7.3 / 3.6 s | 340 ms | 16.6 / 2.7 ms |
| base (last) | 17.8 s | 16.2 s | 1,488 ms | 1,384 ms | 903 / 11 ms | 6.3 / 3.2 s | 409 ms | 16.6 / 2.6 ms |
| `TC_CallCountingDelayMs=0` | 16.0 s | 10.4 s | 380 ms | 291 ms | 164 / 14 ms | 12.5 / 3.3 s | 1,029 ms | 16.6 / 2.9 ms |
| `TieredPGO=0` | 15.3 s | 11.6 s | 778 ms | 699 ms | 228 / 10 ms | 5.2 / 2.6 s | 184 ms | 16.6 / 2.4 ms |
| `TieredCompilation=0` | 17.6 s | 11.8 s | 356 ms | 276 ms | 31 / 30 ms | 7.7 / 6.9 s | 366 ms | 16.6 / 2.6 ms |

The first base launch was the job's cold one: the host took 9.7 s to reach scene 0, against 4.7 to
6.3 s in the other launches. The original's frame 2 on this colony is 1,002 ms
(`World.UpdateCellInfo` 848 ms).

U57:

| Arm | Menu | Load | Frame 2 | UpdateCellInfo | JIT in frame 2 | First 10 | Steady frame / Game |
|---|---|---|---|---|---|---|---|
| base (first) | 16.7 s | 21.7 s | 2,095 ms | 1,942 ms | 1,239 / 13 ms | 710 ms | 19.1 / 6.4 ms |
| base (last) | 16.3 s | 25.0 s | 2,411 ms | 2,227 ms | 1,475 / 14 ms | 1,344 ms | 19.8 / 6.8 ms |
| `TC_CallCountingDelayMs=0` | 14.7 s | 15.6 s | 640 ms | 518 ms | 118 / 10 ms | 955 ms | 17.3 / 5.4 ms |
| `TieredPGO=0` | 15.8 s | 17.2 s | 836 ms | 703 ms | 437 / 9 ms | 605 ms | 20.4 / 7.0 ms |
| `TieredCompilation=0` | 17.5 s | 19.7 s | 797 ms | 669 ms | 22 / 20 ms | 1,155 ms | 21.9 / 7.4 ms |

## Reading

- **Delay 0.** Tier-up starts at the 30th call, not after a quiet 100 ms, so a load's hot code
  is optimised before the first big pass. This arm had the shortest load frames, and the
  shortest load and menu times on U57. Steady frames were no worse. It compiles about twice as
  much in total (12 s against 6 to 7 s by the colony), all of it on background threads, with
  the main thread's share unchanged. On a machine with fewer cores that background work competes
  with the game's own threads (not measured). Its first ten unpaused frames on the colony were
  the slowest of all the arms (1,029 ms, 1.3 s of JIT); on U57 they fell between the two bases.
- **PGO off.** Tier 0 is not instrumented, and hot code goes straight to tier 1, skipping the
  instrumented tier. Frame 2 is about halved, the load 3 to 5 s shorter, and the JIT total
  shorter too. Tier-1 code has no profile data (no guarded devirtualisation, no hot/cold
  layout). Its U57 steady frames were 3 to 10% slower than the bases; one sample.
- **Tiering off.** Every method is compiled optimised once, on the thread that first calls it.
  The main thread's JIT doubles (6.9 s by the colony), the menu and the load gain the least, and
  steady frames on U57 are the slowest. It is also the arm ARCHITECTURE warns about: mod
  loaders' frames run optimised code compiled before the mods patched (§3.1, "Live frames",
  #52), and calls before `ret` become tail calls (D41). Not a candidate.

## What shipping one would take

Not done; for the owner's decision.

- `TieredPGO`: the runtimeconfig property `System.Runtime.TieredPGO`, which
  `OniMoono.runtimeconfig.json` already sets to `true`. Shipping it means setting that to
  `false`. Exactness: it removes the profile-guarded devirtualisation that ARCHITECTURE describes
  for late patches. It needs a quiet-machine fps comparison on a heavy colony first, as it may
  cost steady fps.
- `TC_CallCountingDelayMs`: the runtime read the environment variable here. Whether it also maps to
  a runtimeconfig property is not checked; if not, the host would set it in its own environment
  before `hostfxr_initialize_for_dotnet_command_line` (`src/OniMoono.NativeHost/host.c`; the Linux
  launcher likewise), unless the user has set it. Exactness: more methods reach tier 1, which
  inlines and makes tail calls, earlier, including while mods load. So a late patch takes D42's
  copy path more often, and the mod-loader frames of "Live frames" may hit optimised code. Before
  shipping, run the `--check-types`, `late-patch`, `issue-52-*` and mod specs. A middle value
  (10 to 30 ms) might keep most of the gain with less background JIT (not tried).
- Each arm here is one launch. Repeat pairs for whichever arm goes ahead, and run a Windows fps
  check at speed 3.

Evidence: `out/tiering/out/e2e/unpause-probe{,-ice}/tier-*/ours-1/win-player.log` (runq jobs
j0007576 and j0007580). The probe's environment line was a local change to
`tools/UnpauseProbeTestMod` and is not committed.
