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
did best.

**Shipped:** a call-counting delay of 0, set in the runtimeconfig (see
[The repeat and the decision](#the-repeat-and-the-decision)).

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

## The repeat and the decision

Repeat, 2026-11-21, same build and probes. Two runq `win-perf` jobs per save (j0007590 to
j0007593), each a discarded warm-up launch and then two rounds of base, delay 0 and a middle
delay, in turn (the second job in the reverse order): four second launches per arm and save.
The middle arm was `DOTNET_TC_CallCountingDelayMs=20`, which the runtime reads as hexadecimal:
32 ms. Medians of the four, with the range where it matters:

The #105 colony:

| Arm | Menu | Load | Frame 2 | UpdateCellInfo | JIT in frame 2, all / main | JIT by the load's end, all / main | First 10 | Steady `Game` |
|---|---|---|---|---|---|---|---|---|
| base (100 ms) | 16.4 s | 15.3 s (14.3–15.6) | 1,443 ms (1,402–1,642) | 1,340 ms | 760 / 10 ms | 9.8 / 4.6 s | 515 ms (383–1,048) | 2.6 ms |
| 32 ms | 15.5 s | 13.3 s (12.9–14.0) | 1,322 ms (1,209–1,389) | 1,231 ms | 950 / 11 ms | 13.1 / 4.6 s | 201 ms | 2.3 ms |
| 0 | 14.0 s | 8.7 s (8.4–9.1) | 270 ms (259–344) | 195 ms | 111 / 11 ms | 18.3 / 4.7 s | 201 ms (184–281) | 2.3 ms |

U57:

| Arm | Menu | Load | Frame 2 | UpdateCellInfo | JIT in frame 2, all / main | JIT by the load's end, all / main | First 10 (its JIT) | Steady frame / `Game` |
|---|---|---|---|---|---|---|---|---|
| base (100 ms) | 17.3 s | 20.9 s (20.4–22.3) | 2,047 ms (2,018–2,129) | 1,884 ms | 1,276 / 13 ms | 12.8 / 4.7 s | 912 ms (0.18 s) | 19.2 / 6.8 ms |
| 32 ms | 15.6 s | 18.0 s (17.2–18.5) | 858 ms (695–987) | 651 ms | 600 / 14 ms | 14.7 / 4.5 s | 700 ms (0.23 s) | 18.9 / 6.4 ms |
| 0 | 14.1 s | 15.5 s (14.9–20.3) | 726 ms (637–829) | 593 ms | 121 / 11 ms | 18.8 / 4.6 s | 945 ms (1.1 s) | 20.4 / 7.0 ms |

"JIT by the load's end" is the start-up stats' cumulative compile time at the end of the colony's
frame 3 (all threads / the main thread), so it includes start-up. The first table's "JIT to colony"
measured from another point and is not comparable.

- **Delay 0** holds up: the load is 5 to 7 s shorter on both saves, the slow frame 3 to 5 times
  faster, and the main menu comes 2.5 to 3 s sooner too (start-up's own code tiers up). The
  main thread compiles no more; the extra compiling, 1.5 to 1.9 times the total, is on
  background threads. The single run's slow first ten frames on the colony (1,029 ms) did not
  repeat (184–281 ms, against 383–1,048 ms for the base). On U57 they are about the same as the
  base's (870–1,030 ms against 818–996 ms), with 1.1 s of background JIT in them.
- **32 ms** keeps little of the gain on the colony (the load 2 s shorter, frame 2 barely
  faster): the load's compiles keep restarting the delay. On U57 it gets most of the frame-2
  gain but under half of the load's.
- **Steady frames** in the probe (its speed-3 pan, with the probe's timing patches) were within
  the bases' spread, so the frame rate was measured separately.

**Frame rate.** U57 at speed 3 (PerfProbe, `awake`, `noautosave`, `novictory`, `frametimes`), the
same build in both arms: delay 0 from the runtimeconfig, the base with
`DOTNET_TC_CallCountingDelayMs=64` (100 ms) in the environment. Two runq `win-perf` jobs
(j0007611, j0007613), each a warm-up launch then base, 0, 0, base (the second 0, base, base, 0),
1280×720 on the second monitor, unfocused, High priority, `--game-tweak exact`, vsync off. 9
windows of 600 frames, the first 3 left out.

| Arm | fps (each launch) | Median | p50 | p99 |
|---|---|---|---|---|
| base (100 ms) | 98.6, 87.3, 94.9, 80.8 | 91.1 | 9.6 ms | 33.7 ms |
| 0 | 89.7, 92.4, 97.1, 102.1 | 94.8 | 9.3 ms | 29.5 ms |

(p50 and p99 are the medians of the launches' own.) No loss: the arms overlap and the bases spread
by 18 fps. The first window after a load was faster with delay 0 in every launch (52 to 56 fps
against 27 to 28).

**Decision: delay 0, shipped.** `OniMoono.Host.csproj` sets the runtimeconfig knob
`System.Runtime.TieredCompilation.CallCountingDelayMs` to 0 (a `RuntimeHostConfigurationOption`;
CoreCLR's `EEConfig` reads it through `Configuration::GetKnobDWORDValue`, which takes
`DOTNET_TC_CallCountingDelayMs` from the environment first). So it applies to every start that
reads `OniMoono.runtimeconfig.json`: OniMoono.exe on Windows (Steam, the versioned install,
test runs) and the apphost on Linux, and a user's own variable still wins. The log's `tiering:`
line gives the delay in effect and where it came from. Exactness, with the change: `--check-types`
(debug and release builds) and `scripts/check.sh` pass; `late-patch`,
`issue-52-click-cycle-patched` and `issue-52-automation-overlay` pass
(`issue-52-fast-track-no-tiering` is skipped on U60: Fast Track does not load there; it runs with
tiering off anyway); and ten mod specs pass in ours: Resource Calculator (a late patch of an inlined
method), Mod Updater (D12's transpiled calls), DGSM, WGSM, Research Queue, Better Automation
Overlay, Falling Sand, Bigger Camera Zoom Out, Customizable Speed and Better Info Cards Plus.
Stock Bug Fix, Mod Origin Info Display and `plib-options-copies` failed to load their mods with
the delay at 0 and at 100 ms alike (the local Workshop copies name fields U60 764025 no longer
has), so not the change.

## What shipping one would take

Before the repeat; kept for the reasons. Delay 0 shipped (above).

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
j0007576 and j0007580), the repeat's `rep-*` folders beside them (j0007590 to j0007593), and the
frame-rate jobs' `~/oni-runs/j0007611` and `j0007613`. The probe's environment line was a local change to
`tools/UnpauseProbeTestMod` and is not committed.
