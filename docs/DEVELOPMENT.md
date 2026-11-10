# Development

How to build, run and test Moono from source. If you just want to play, the
[README](../README.md) is all you need.

## Requirements

- Linux x64 with an OpenGL 4.5 driver. The Windows test player is built from WSL (its
  `OniMoono.exe` with the Visual Studio C++ build tools on the Windows side), and runs on
  Windows x64 with an OpenGL 4.5 driver and the .NET 10 runtime.
- The .NET SDK pinned in `global.json` (10.0.402, or a later patch release). The scripts use
  `~/.dotnet/dotnet` if it exists.
- The Linux build of Oxygen Not Included, expected at `~/projects/oni-linux`. To use another
  location, set `ONI_GAME_DIR` for the scripts and tests, and pass `-p:OniGameDir=...` to
  `dotnet build`.
- `python3` and binutils (`as`, `ld`), for the offline Steam stub.
- Optionally `libvpx`, for the one test that compares our VP8 decoder against it. The test is
  skipped without it; the player decodes the game's videos itself.

## Build

```sh
dotnet build OniMoono.slnx              # debug: engine, host, tools, tests
dotnet build src/OniMoono.Host -c Release
```

Both write to `out/bin/<project>/<config>/`. The host's output has everything needed to run: the
engine, BRUTAL's native libraries, and the Unity module facades in `engine/`. The host carries
its own copy of the engine, so after changing the engine, rebuild the host too.

`scripts/build-engine.sh [debug|release]` also lays out `out/engine` (the engine and its facades
without the host), which the API conformance check reads. `scripts/build-packages.sh` builds our
uGUI, TextMeshPro and Timeline into `out/packages`.

Test players and builds run through `scripts/contain.sh CMD...`, which starts them in the
memory-capped systemd user slice `oni-tests.slice` (`scripts/oni-tests.slice`, installed into
`~/.config/systemd/user` when it changes) as preferred OOM victims, so a player that over-commits
is killed instead of your session or WSL. `run.sh`, `check.sh`, `qa.sh` and `e2e.py` use it on
their own; use it for builds started by hand too: `scripts/contain.sh dotnet build …`.

## Run

```sh
scripts/run.sh                 # needs a running, logged-in Steam client
scripts/run-offline.sh         # no Steam: a stub stands in for libsteam_api
ONI_CONFIG=release scripts/run.sh
```

Both scripts keep saves, settings and mods in `out/run-home` (or `ONI_RUN_HOME`), away from your
real profile in `~/.config/unity3d`. Any other arguments go to the host. Useful ones for testing are
`--log FILE`, `--headless --frames N`, `--capture FRAME:PATH` and `--max-frame-rate N`. Unity's
`-screen-width`, `-screen-height`, `-screen-fullscreen`, `-window-mode`, `-monitor` and `-logFile`
work too, and `-force-glcore` and `-force-opengl` ask for the OpenGL Core the player always renders
with. `-force-vulkan` is noted in the log: there is no Vulkan renderer, so the player stays on
OpenGL (U59's Linux player tries Vulkan, and exits when the only Vulkan device is a software one).
Unity's other player arguments (`-popupwindow`, `-force-d3d11`…), and a malformed value for one of
these, are noted in the log and ignored, as Unity's player ignores what it does not know. An unknown
`--` option, such as `scripts/run.sh --help`, prints the host's usage line with every option.

The player's options come from built-in defaults, then the settings file
`<persistentDataPath>/oni-moono/settings.json` that the main menu's Moono Tweaks screen writes,
then the command line: `--game-tweak exact|all|none|NAME|-NAME` (repeatable; any of them sets the
whole set), `--splash moo|none`, `--frame-stats` and `--log-paths hidden|full`.
`--no-tweaks-menu` leaves the Moono Tweaks button off the main menu, as in the original. [TWEAKS.md](TWEAKS.md) lists the tweaks and their
verify mode (`ONI_TWEAK_VERIFY=1`).

These runs have no game tweaks and no splash, and log full paths, unless they name otherwise:
the scripts pass `--game-tweak none --splash none --log-paths full` first, so neither the
player's defaults nor a settings file in the run home changes what a test measures. `ONI_TWEAK_SETTINGS=1` leaves that out, to try the
Moono Tweaks screen as a player has it.

`Player.log` keeps its first 20 MB and its last 20 MB or so, cut with a banner between them;
`ONI_LOG_FULL=1` keeps all of it. `ONI_LOG_ERRORS=FILE` also writes the first line of each logged
error to FILE (`scripts/qa.sh` reads it: the log writes such errors as plain lines). More diagnostics (`--trace-exceptions`, `--dump-hierarchy`,
`ONI_OBJECT_CENSUS`, …) are in [ARCHITECTURE.md](ARCHITECTURE.md) §7.

The offline stub says you own the base game, with no DLC and no Workshop items. It's for testing
on machines without Steam, not for playing.

## Setup

To play through Steam like a player does, run `./setup.sh` (Linux) or `setup.cmd` (Windows, which
runs `setup.ps1`). From a clone they build a self-contained player first (`setup.sh` into
`out/player`); from a release they use the archive's `player/`, after deleting the `OniBrutal`
and `OniBrutal.*` files an older release extracted there may have left. `setup.ps1` installs the
player into `%LOCALAPPDATA%\oni-moono\player`. Both print the launch options line
(`OniMoono -- %command%`), or say that every Steam account already has it. On Linux,
`ONI_STEAM_SANDBOX=flatpak|snap|none` overrides the guess of how Steam is sandboxed.

## Releases

Before a release, its e2e run is the core list, both players on two lanes (about 40 minutes):
`scripts/e2e.py --player both --lanes 2 --list tests/e2e/release.txt`. The specs it leaves out,
in `tests/e2e/periodic.txt`, run periodically and do not block a release.

Releases are built by GitHub Actions, never on a workstation: run the **Release** workflow
(`.github/workflows/release.yml`) on `master`, or on `u60` for a U60 beta, with the release's tag:

```sh
gh workflow run release.yml --ref master -f tag=vYYYY.MM.DD.NN -f dry_run=false
```

It builds both archives on GitHub's runners, signs their provenance and leaves a **draft**
release with them, titled `YYYY.MM.DD.NN` (`YYYY.MM.DD.NN (U60 Beta)`, a pre-release, from
`u60`), whose notes start with where it was built and how to verify it. Write the notes in the
draft on GitHub, then publish it there: publishing creates the tag and makes the release
immutable. The workflow never publishes. The tag adds a two-digit counter for the day (`.01` for
its first release), so releases sort as text; the archives are named by the date of the commit
(`-u60` on `u60`). [RELEASES.md](RELEASES.md) has how the pipeline works, how to verify a
release, how to add a new game build's reference assemblies and how reproducible the builds are.

`scripts/release.sh` still builds the same archives locally, against your game install:

```sh
MOONO_RELEASE=vYYYY.MM.DD.NN scripts/release.sh   # out/release: the Windows zip and the Linux tar.gz
```

`release.sh [VERSION]` names the archives by VERSION, by default the date of HEAD's commit.
`MOONO_RELEASE` is the tag the release will get: the player compares it with the releases on
GitHub for the main menu's update badge ([TWEAKS.md](TWEAKS.md#update-check)), and `release.sh`
fails without it (`MOONO_RELEASE=none` builds a development player, which checks for no
updates). `MOONO_PLATFORMS=linux` or `windows` builds one archive. Each archive holds an
`oni-moono` folder with the setup script, the README, `NOTICE.txt` and the self-contained player
in `player/`; the setups install a release's player without building. Nothing of the game goes
into them.

## Windows

```sh
scripts/build-windows.sh       # out/win: OniMoono.exe, Restarter.exe and the Steam stub
scripts/run-windows.sh         # from WSL: a test run on the Windows install
```

`OniMoono.exe` is our native host (`src/OniMoono.NativeHost`, [ARCHITECTURE.md](ARCHITECTURE.md)
§2), built by `scripts/build-native-host.sh` with MSVC through `cmd.exe` (`VCVARS64` names
`vcvars64.bat` when vswhere can't find it), or from Git Bash on Windows, as the release workflow's
Windows runner does. `release.sh` builds it the same way. `scripts/check-windows-host.py EXE [--like OTHER.exe]` checks a built one from its
PE file (the two GPU exports present, writable and 1; the icon, manifest, version and subsystem);
`build-windows.sh` and `release.sh` run it, and `check.sh` runs it on `out/win/host` when
there is one. `scripts/check-native-crash.sh` (also in `check.sh`) crashes the player on purpose
with `--test-native-crash WHERE` and checks the crash lines and minidump in its log
(ARCHITECTURE §7, "Native crashes"): on Linux, and on Windows with `out/win/host`, in run homes
under `%TEMP%`, before any window opens (`ONI_WIN_GAME_DIR` names the game, as for
`run-windows.sh`). On Windows it also checks the crash window offscreen (`--crash-dialog-render`
PNGs, its zip and issue link) and that no crash case leaves one open.

The Windows player uses the Windows build's own data, whose shaders include OpenGL programs
alongside the Direct3D ones. `run-windows.sh` copies `out/win` to `%TEMP%\oni-brutal` and starts
it on the Steam install (`ONI_WIN_GAME_DIR` to change that). Test runs stay away from your game
data, Steam account and speakers:
- `--run-home` gives the game its own Documents, AppData and LocalLow under
  `%TEMP%\oni-brutal\run-home` (`ONI_RUN_HOME`)
- the Steam stub replaces `steam_api64.dll` (`ONI_STEAM_STUB` names another one)
- `--audio-wav` writes the game's sound to `fmodoutput.wav` there

To compare against the original Windows player under the same isolation:

```sh
scripts/windows-run-home.sh RUN_HOME SAVE out/bin/PerfProbe/release:-2037174645
ONI_RUN_HOME='C:\...' scripts/original-windows.sh -screen-width 1024 -screen-height 768 -screen-fullscreen 0
ONI_RUN_HOME='C:\...' scripts/run-windows.sh -screen-width 1024 -screen-height 768 -screen-fullscreen 0
```

Test windows open windowed on the second monitor without taking the focus (`-monitor 2`; our
player's `--no-focus`; `ONI_WIN_MONITOR` picks another), and the original's BepInEx console stays
closed. Focus one only through `out/local-tools/win-run.ps1 -Match RUN_HOME -Action focus`, and act
on Windows game processes only through it, never by name.

`windows-run-home.sh` prepares a run home with a single colony, which the main menu resumes on
its own, and mods enabled (`tools/PerfProbe` holds speed 3 and logs frame rates). The original
runs from a renamed copy of the install (`ORIG_DIR`, default `E:\Development\BrutalNotIncluded`)
with BepInEx 6 and `tools/OriginalIsolation`, which gives it the same separate data folders and
audio file. Its first setup copies BepInEx from an existing install (`ONI_BEPINEX_SOURCE`). The
script has the details.

## Testing

```sh
scripts/check.sh               # before every commit
scripts/qa.sh [STAGE...]       # the full harness: unit, dogfood, verify, perf
scripts/qa.sh --memory         # U57's peak memory alone, beside other runs
scripts/e2e.py SPEC...         # end-to-end specs: tests/e2e
scripts/e2e.py --player both --lanes 2 --list tests/e2e/release.txt   # release QA's e2e run
scripts/e2e.py --status        # what runs, holds and waits on the machine: look here first
```

`check.sh` builds the solution, runs the test suites and the scripts' tests
(`tests/scripts`), and cross-publishes the macOS host. Then it checks the game's assemblies
against the engine's API, and runs the host's type, asset, binding and native checks against the
installed game. Some tests read shaders and fonts from the game, so it needs to be installed.

`qa.sh` runs four stages, all by default: `unit` (the build and `check.sh`), `dogfood` (scripted
play sessions compared against reference captures: menu, new game, colony, every main screen and
overlay, typing, save, load, resize), `verify` (a large colony with every tweak in verify mode)
and `perf` (its frame rate and memory against the reference). `--bless` stores a run's results as
the references once they have been looked at. The large-colony stages need local setup in
`out/local-tools/qa.env`, and are skipped without it. A large-colony run that logs no frame rate
for `QA_HANG_MIN` minutes (default 5) is hung: its stacks go to the stage's `hang` folder
(`scripts/hang-dump.sh`) before it is killed; `ONI_TEST_HANG=FRAME[:spin|wait]` hangs one on
purpose. The `memory` stage (`--memory`, or `QA_MEMORY_ONLY=1`, runs it alone) measures the
large colony's peak memory only: it takes no perf lock and waits for no quiet machine (memory
does not depend on the load), and stops once the first autosave has passed and memory has not
risen for `QA_MEMORY_SETTLE_FRAMES` frames, reporting the peak, its frame and the RssAnon/RssFile
split. A player of `qa.sh` or `e2e.py` ends with its run, however that ends, and so does its Xvfb.
A player is stopped with SIGTERM, which quits ours at the next frame boundary with its GPU work
done (within `ONI_QUIT_DEADLINE` seconds, default 8; a quit that misses it logs each thread's
state and wait channel first), and killed only when it still runs `E2E_QUIT_GRACE` seconds
(default 10) later.

`e2e.py` plays a spec (a fixture save, Workshop mods, orders and UI input) in our player, the
original or both, and checks what came of it. Runners in every worktree share lanes (ports and
an Xvfb display each), a players' memory budget and locks in `~/.cache/oni-brutal-e2e`
(`E2E_LOCK_DIR`); `scripts/e2e.py --status` shows them, with who waits on what and what dead
runners left (`--clean` cleans that). `qa.sh`'s perf stage writes `perf.wanted`
there, so no new spec starts, and takes `perf.lock` exclusively once the running ones finish;
other perf jobs can do the same with `flock -x ~/.cache/oni-brutal-e2e/perf.lock CMD`.
[tests/e2e/README.md](../tests/e2e/README.md) describes specs, lanes and verdicts. Its fixture saves are not in
git: `tests/e2e/fixtures.json` lists them by hash, and `scripts/fixtures.py` fetches them into a
worktree from the shared cache `~/.cache/oni-brutal-e2e/fixtures` or the private refs
repository's releases (with your `gh` login), as `e2e.py` and `qa.sh` do before a run;
`scripts/fixtures.py bless PATH` uploads a new or re-blessed one and updates the manifest.
`scripts/mod-sweep.py NAME` runs Workshop mods in batches through the same machinery, in both
players, and bisects failing batches; `scripts/modtable.py > docs/MOD_COMPATIBILITY.md`
regenerates the compatibility table from the results.

Both install Workshop mods only through the mod gate (`scripts/mod-gate.py`, run by `ws-mods.py
profile`): a mod is played only when onimaxxing has reviewed the exact upload SteamCMD fetched.
Its upload handle (the `ugchandle` in SteamCMD's `appworkshop_457140.acf`, signed, taken mod
2^64) must be the `file` of a version with `reviewed: true` in onimaxxing's
`mods-data/BUILD/mod/ID/index.json`; the unpacked DLLs in `out/ws/ID` must include every
`managed[].sha256` of that version's facts (`mod/ID/FILE.json`), compared by hash because a
zip's non-ASCII names unpack under another encoding; and the catalog row must carry no
`malware`, `credentials`, `chatWebhook` or `obfuscated` flag. A mod with no binaries needs only
the last. Any other mod is skipped, with the reason ("not reviewed by onimaxxing yet: upload N
is not analysed"), and waits for onimaxxing's review: `e2e.py` reports its spec as skipped, and
`mod-sweep.py` records it as not run. `scripts/mod-gate.py ID...` checks mods without playing
them. onimaxxing's data is cached in `out/modgate/cache/BUILD/` with its fetch time, for an
hour; the build is the game folder's (761015 for `oni-linux-761015`, else U59's 744825), or
`ONI_GAME_BUILD`.

### Game tests in CI

GitHub's runners have no game, so the **Build** workflow's game job runs on a stripped copy of
it. `tools/GameStrip` makes the copy from a Linux install with its DLC content:

```sh
dotnet build tools/GameStrip -c Release
dotnet out/bin/GameStrip/release/GameStrip.dll census GAMEDIR       # what the install streams
dotnet out/bin/GameStrip/release/GameStrip.dll strip GAMEDIR OUT    # the stripped copy
```

The copy keeps every serialized file and header, and the game's assemblies, plugins and
StreamingAssets. It drops the texture and video payloads, which are only ever drawn or played,
the FMOD banks' sample data (FMOD still loads every event, which plays silently), and Unity's
player and Mono. Code reads texels only from readable textures, and all of those are stored
inline, so they stay. `OUT/OxygenNotIncluded_Data/moono-stripped.txt` lists what was kept. A U59
copy is about 820 MB, and 110 MB with zstd. Make a U59 copy with master's tool and a U60 copy with
u60's: the tool reads Unity's type trees for that game update.

The player runs a copy only in no-content mode, `ONI_NO_CONTENT=1`. A dropped texture keeps its
declared size, the GL device draws a 1×1 grey placeholder for it, and a dropped video plays black
for its length. Without the mode, a dropped payload is an error that names the variable, so
normal play never shows a placeholder.

The copies live in the private refs repository's `game/` folder, as zstd archives split into
parts under 50 MB. The fixture saves CI loads are there too, under `game/saves/SHA256`, because
CI's read-only deploy key can fetch git objects but not release assets. `ci/game-content.json`
names the commit and every hash, and `scripts/fetch-game-content.py` fetches and checks them:

```sh
scripts/fetch-game-content.py game OUT            # this branch's stripped build into OUT
scripts/fetch-game-content.py saves early-base    # fixtures, through scripts/fixtures.py's cache
scripts/ci-game-tests.sh push OUT                 # what the game job runs (QA_U57_STEAM, E2E_ONIMCP)
```

`scripts/ci-game-tests.sh` runs a list:

- **push** (every push to master or u60, about 15 minutes): `check-tweaks.sh` against the game's
  assemblies, the unit tests that need the game (the no-game unit test job skips them), and
  `ci/game-tests/push.txt`. That list holds headless load, simulate, save and reload runs on
  early-base and a Spaced Out colony with rockets, a verify run with every tweak (zero wrong
  answers, read by `qa-tools.py verify`), and `exact-worldgen` under Xvfb.
- **nightly** (the **Game tests (nightly)** workflow, on master; run it by hand on u60):
  `ci/game-tests/nightly.txt`, which holds UI specs that check their own expectations and dumps,
  mod loading with our own test mods, and a ten-minute verify run on a mid-game colony.

Each run is checked against its log-signature budget, `ci/game-tests/budget/BUILD.txt`: a run
fails on an error or exception the budget lacks. `E2E_NO_SHOTS=1` keeps specs from taking
screenshots, and no spec compares pixels. OniMcp is built fresh for each run by
`scripts/onimcp.py`, against the copy and from its author's repository, with its U60 change and
`MOONO-CHANGES.txt` as in a local build.

oni-moono is public, so the job:

- reads the refs repository only with the existing read-only deploy key
  (`GAME_REFS_DEPLOY_KEY`), and skips itself in any run without it, such as a fork's;
- caches no game files, saves or OniMcp builds, and fetches them fresh every run;
- uploads no game files, saves, screenshots, UI dumps or logs as artifacts, and prints verdicts
  and short report lines only.

Keep it that way.

To refresh a copy after a game update:

1. Strip it.
2. Archive it with
   `tar --sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner --transform s,^DIR,BUILD, -cf - DIR | zstd -19`.
3. Split it with `split -b 45M -d -a 2`.
4. Commit the parts to the refs repository's `game/BUILD/` with their `SHA256SUMS`.
5. Point `ci/game-content.json` at the commit, with the new hashes.

## The OniMoono rename

The internals were renamed from OniBrutal to OniMoono. `scripts/rename-internals.py` does that
rename and is idempotent: run it on a branch from before the rename before merging or rebasing it
onto master. `--dry-run` shows what it would change, and `--audit` lists what still names the old
one and fails on anything not kept on purpose (the lock folder, the Windows staging folder, the
code that recognizes older players).

## Known differences

[ARCHITECTURE.md](ARCHITECTURE.md) §2.1 lists where Mono and .NET, or Unity's player and ours,
differ in ways game or mod code notices (D1–D58), each with how the host resolves it or why it is
kept. The host patches a few game methods (D1, D3, D11, D26), rewrites game and mod IL as it
loads where it depends on Mono (D29–D31 and D47 for both; mods' alone in D22, D23, D27, D28, D32,
D40, D42, D50, D51, D56 and D57), and adjusts the game's Harmony where patches would run differently
(D12–D14, D17–D21, D33, D34, D43).

Float arithmetic in game code rounds as .NET does, not as Unity's Mono (D25), except in
worldgen under the `exact-worldgen` tweak; the engine's own math follows the original bit for bit
where that was measured.

## Documents

- `docs/PLAN.md`: milestones, their exit criteria, and current status
- `docs/ARCHITECTURE.md`: how the pieces fit together, and the deviations
- `docs/PARITY.md`: what has been compared against the original player, and how
- `docs/PERFORMANCE.md`: where the frame time goes, and what each change bought
- `docs/TWEAKS.md`: the game tweaks, and how "exactly the same" is checked
- `docs/MOD_COMPATIBILITY.md`: which Workshop mods work, generated by `scripts/modtable.py`
- `docs/ENGINE-CONVENTIONS.md`: coding rules for the engine
- `docs/LICENSING.md`: what we redistribute, and what we rely on the game install for
- `docs/GAME-UPDATES.md`: checks to run again when the game updates
- `tests/e2e/README.md`: end-to-end specs, how they run, and the mod batch workflow
- `tools/UnityProbe`: a mod that records how the original player behaves, so the same scenarios
  can be compared in both players
