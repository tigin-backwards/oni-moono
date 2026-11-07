# End-to-end specs

A spec is one YAML file here, run by `scripts/e2e.py`. It starts from a fixture save, installs the
mods it names, drives the game the way a player does, and checks what came of it. The plan behind
it is [docs/research/mod-play-testing.md](../../docs/research/mod-play-testing.md).

```sh
scripts/e2e.py --check NAME                 # parse and validate only (and known-findings.yaml)
scripts/e2e.py NAME                         # ours, on the first free lane
scripts/e2e.py --player both --repeat 2 NAME
scripts/e2e.py --player original --bless NAME   # bless fixtures and references
scripts/e2e.py --lanes 4 A B C D            # four at once, from a queue
scripts/e2e.py --player both --lanes 2 --list tests/e2e/release.txt   # release QA (tests/e2e/periodic.txt: the rest)
scripts/e2e.py --player ours --repeat 2 --record --note "..." NAME   # write tests/e2e/results/NAME.json
scripts/e2e.py --status                     # lanes, holders, waits, leftovers: look here first
scripts/e2e.py --clean                      # clean what dead runners left on free lanes
```

A batch of mods, one agent per worktree, follows [Batch workflow](#batch-workflow); which mods
load at all, in both players, comes first from [Load sweeps](#load-sweeps).

## How a run goes

- **Players.** `--player both` runs the original Unity player first. A spec that fails there is a
  broken spec, or a mod broken upstream, never a verdict on us. Our runs of that spec then skip.
  Otherwise our runs are compared with the original's evidence (see [Verdicts](#verdicts)).
  `--player ours` alone compares with the references `--bless` stored in `out/e2e-refs/SPEC/`.
  Ours runs with `--game-tweak exact --game-tweak path-probe-repair`, the users' default game
  tweaks but the Bug fixes on by default (fix-load-screen-clicks, fix-endless-sounds and
  fix-sound-piles). path-probe-repair may walk another of two equally short routes; close enough
  to the original's. `E2E_GAME_TWEAKS` gives others, space-separated
  `--game-tweak` values: `E2E_GAME_TWEAKS=exact` (no gameplay changes at all), `=none`,
  `=background-save`.
- **The lock directory.** Runners in every worktree on the machine meet in one directory,
  `$E2E_LOCK_DIR` or by default `~/.cache/oni-brutal-e2e/`: lane locks, the players' budget, the
  perf lock and the reservations below live there, never in a worktree's `out/`.
- **`--status` first.** When a run waits, `scripts/e2e.py --status` says why. For each lane
  it prints the holder (pid, alive or gone, worktree, since when) and what the lane does now
  (`running SPEC in ours (1/2), run ID`, or the wait it is in: a perf job, memory), the
  processes it runs (player and Xvfb, with their pids and scope), and who listens on its ports
  or holds its display. A lane no runner holds but whose ports or display are busy shows as
  `FREE but busy`, with the leftovers the next runner would clean (`would clean: …`). Then:
  the free lanes, `perf.lock` (free, shared by N spec runs, or held exclusively by whom),
  `perf.wanted` (the pid waiting to run a perf job, alive or stale, and the lanes waiting for
  it), the runners (each `e2e.py` with its lanes, or waiting for one), `qa.sh` runs and their
  watchers, the players' budget, perf jobs, `oni-test-*` scopes and their runners, and X servers
  whose launcher has gone.
- **Nothing outlives its run.** Players and Xvfb run in sessions of their own (each player in a
  systemd scope, `oni-test-PID-START-laneL-…`, through `scripts/contain.sh` with `ONI_SCOPE`),
  so they would outlive a runner a `timeout` or `kill -9` ends. Instead:
  - every process a runner starts carries `E2E_OWNER=PID:START` (the runner's pid and start
    time), `E2E_LANE` and `E2E_LOCK_DIR`, and the lane's lock records it (pid, start time,
    scope) while it runs;
  - on any exit the runner sees (the end, an exception, SIGTERM, SIGINT, SIGHUP) it stops its
    players' scopes and kills every process carrying its `E2E_OWNER`;
  - a watcher (`e2e.py --reap-after PID START`, in a session of its own) does the same when the
    runner dies without seeing it (SIGKILL, or its parent killing its group);
  - a runner taking a lane whose last holder is dead first cleans what that holder left: only
    processes whose `E2E_OWNER` names a dead runner, the lane's recorded children (checked by
    start time), scopes named for a dead runner, and the stale `/tmp/.XN-lock` and socket of the
    lane's display. Leftovers of runners from before `E2E_OWNER` (a player with the lane's
    `E2E_LANE`, an Xvfb on the lane's display, with no `e2e.py` among their ancestors) count in
    the machine's lock directory only. `--clean` does this now for every lane no runner holds.

  `qa.sh` does the same for its players (see its header).
- **Players quit, then are killed.** Every teardown above, a hang watchdog's included, sends a
  player SIGTERM first, alone, while its X server still runs. Ours then quits at the next frame
  boundary as `Application.Quit` does, its graphics device and window going first, and exits
  within `ONI_QUIT_DEADLINE` seconds (default 8) however the quit goes
  (`src/OniMoono.Host/QuitSignals.cs`); a second signal ends it at once. A player still running
  `E2E_QUIT_GRACE` seconds (default 10) later gets SIGKILL. On WSL a player killed in the middle
  of a GPU wait (`dxgkio_wait_sync_object_cpu: Ioctl failed: -512` in the kernel log) was once
  followed by a kernel soft lockup that hung WSL.
- **Lanes** are machine-wide. Lane L uses:
  - OniMcp on port 8800 + 10 L, written to the run's `OniMcpConfig.json`;
  - E2eDriver on 8801 + 10 L, passed as `E2E_DRIVER_PORT`;
  - display :(300 + L) (`E2E_DISPLAY_BASE`), an Xvfb the runner starts and stops. `--xvfb-run`
    uses `xvfb-run -a` instead; `--displays :88,:89` names the displays.

  `--lanes N` (at most 6) runs up to N spec runs at once, on lanes from `--first-lane` (default 1)
  to `--last-lane` (default 19): `--first-lane 7 --last-lane 8` keeps a runner on lanes 7 and 8.
  A lane is skipped while another runner holds it (`lane-L.lock`, whose content names the
  holder), while another program listens on its port or runs its display, and when a port or
  its display is reserved: lines `ports 8788 8820-8831` and `displays :80-:85 :91` in the lock
  directory's `reserved` file, or in `E2E_RESERVED` (`;` between lines). Reserve there what
  sessions or tools outside this lock directory use.
- **Memory.** Each player is counted in the machine's budget (`players.json`) for the memory its
  fixture needs: 6 GB on a small save, 11 on 2minnow, 15 on U57, or the spec's `memory_gb`. A
  player starts only while all of them stay within `E2E_MEMORY_GB` (default 40 of the machine's
  62) and their count within `E2E_MAX_PLAYERS` (default 4: with more, the WSL GPU ran out of
  hardware queues and new players failed to create a GL context; a running `qa.sh` counts as one);
  one bigger than the whole budget starts alone. An entry whose process has gone is dropped.
- **Memory ceiling.** Players start through `scripts/contain.sh` (as do `scripts/run.sh`,
  `qa.sh` and `check.sh`): in the systemd user slice `oni-tests.slice` (`scripts/oni-tests.slice`,
  50 GB, 4 GB of swap) and as preferred OOM victims. When players over-commit, the kernel kills
  one of them inside the slice, which the run reports as a machine failure, and the user's
  sessions keep running. Players and builds started by hand go through it too:
  `scripts/contain.sh dotnet build …`.
- **Perf jobs.** A run holds `perf.lock` in the lock directory shared. A perf job that must not
  share the machine takes it exclusively: `flock -x ~/.cache/oni-brutal-e2e/perf.lock
  scripts/qa.sh`. Runs also wait while `ps` shows a perf job (`qa.sh`, the sweep and measurement
  scripts, `perf record`; see `PERF_JOBS`); `--no-perf-wait` skips the `ps` check. Peak memory is
  not a perf job: it does not depend on the machine's load, so measure it with `scripts/qa.sh
  --memory` (or `QA_MEMORY_ONLY=1 scripts/qa.sh`), which takes no lock, waits for no quiet
  machine and stops after the first autosave, never with `flock -x`.
- **Waits are visible.** A run waiting for a perf job, a lane or memory says what it waits for
  (the perf job's pid, how long it has run and its command; the lanes' or players' holders),
  again when that changes and every 10 minutes. `--max-wait MINUTES` (or `E2E_MAX_WAIT`) fails
  the run after that long, with what it was waiting for as its reason.
- **Hangs.** Each player has a watchdog. Its log growing, or any OniMcp or E2eDriver reply, is
  progress. With none for `E2E_HANG_MIN` minutes (default 5) the player counts as hung:
  `scripts/hang-dump.sh` saves diagnostics to the run folder's `hang/`, then the player is
  killed and the run fails with `the player hung (…); diagnostics in …/hang: <the main thread's
  state>`. The same diagnostics are taken before the player stops when a step fails after a
  minute without progress (an OniMcp request times out at 120 s). The folder holds every
  managed thread's stack (`managed-stacks.txt`, from `dotnet-stack report`), with `HANG_DUMP=1`
  or when that fails a mini dump read by `dotnet-dump analyze` (`dump-stacks.txt`), gdb's
  native stacks if gdb is installed, per-thread state, CPU and kernel wait channel
  (`threads.txt`, `top.txt`: a busy loop or a wait), `/proc/PID/status` and the log's tail.
  The tools are .NET global tools (`dotnet tool install -g dotnet-stack dotnet-dump`), and the
  original player gets only the non-.NET parts. A held player (`--hold`) is never hung.
  `ONI_TEST_HANG=FRAME[:spin|wait]` makes our player's main thread spin, or block forever,
  after that frame (`src/OniMoono.Host/TestHang.cs`), to try the watchdog. `scripts/qa.sh`'s
  U57 runs have the same watchdog, keyed to PerfProbe's PERF lines (`QA_HANG_MIN`).
- **Paused time.** The fixture loads paused. Time passes only in `run` steps. Take all UI evidence
  before the first `run`, or right after an expectation that pins the state.
- **The build menu catches up over frames.** The game counts the colony's inventory one tag a
  frame, checks ten buildings' requirements a frame and repaints ten of the menu's buttons a
  frame, so a button can show a stale state (greyed, not affordable) for dozens of frames after
  something changes. Before each `shot` and `ui_dump` the runner therefore waits, in both
  players, until E2eDriver's `buildmenu.settled` holds: every shown button and category button
  shows the state the game computes for it now, and the inventory counts are current. A menu
  that does not settle within 600 frames fails the step with the stale buttons as its reason.
- **Output.** Each invocation has a run id (its start time and a random part, or `--run-id`), so
  invocations never overwrite each other. Each run writes to `out/e2e/SPEC/RUN_ID/PLAYER-N/`
  (`out/e2e/SPEC/latest` points at the newest run id):
  - `player.log` or `stdout.txt`;
  - `results.jsonl`, one line per step;
  - `result.json`, the run's record;
  - `lane.json`, the lane's ports and display;
  - the evidence: `NAME.ui.txt`, `NAME.png` with `NAME.shot.json`, and `NAME.diff.png`.

  `out/e2e/runs/RUN_ID.json` (or `--results PATH`) holds one record per spec run of the
  invocation; `out/e2e/runs/latest` points at the newest. References stay one folder per spec,
  `out/e2e-refs/SPEC/`, replaced whole by each `--bless`; its `source.json` names the run that
  blessed it. `E2E_OUT` moves `out/e2e` elsewhere, and the references beside it
  (`$E2E_OUT-refs`).

## Top-level keys

```yaml
# The mod card goes here as a comment: what the mod does, how a player reaches each feature,
# what changes when it is used.
from: early-base                 # fixture: tests/e2e/fixtures/early-base.sav
mods: [1863428350]               # Workshop IDs, installed as Local mods (out/ws, ws-mods.py)
options:                         # PLib options, merged into the mod's config.json before launch
  SweepByType: {DisableIcons: true}       # by static ID (or Workshop ID)
  "2890083659": {$file: MiniBox.json, Wires: 3.0}   # a [ConfigFile] name other than config.json
require_log: ['\[SweepByType\] options saved']   # regexes: in the original's log -> must be in ours
mask_text: ['\d+:\d+', 'Cycle \d+']      # masked in every ui_dump's text before the diff
ignore_fields: [depth]           # NAME=VALUE members left out of every ui_dump's diff
tolerance: 2                     # ui_dump rect tolerance in pixels (default 2)
thresholds: {shot: 0.02, world_shot: 0.05}   # pixel fractions (the defaults)
memory_gb: 11                    # the players' budget counts each player for this (default: by fixture)
spaced_out: false                # Spaced Out off in both players (default: the fixture's mode, from its
                                 # header's dlcIds; on for `from: main-menu`)
translation: 929139073           # the language of this translation mod (one of `mods`), picked in the prefs
language: zh_klei                # or one of the game's own languages (zh_klei, ko_klei, ru_klei)
tweaks_menu: true                # ours keeps the main menu's Moono Tweaks button (run with --player ours)
game_tweaks: [brain-rate]        # ours runs with these `--game-tweak` values after E2E_GAME_TWEAKS's
env: {ONI_TWEAK_VERIFY: "1"}     # ours runs with these in its environment (ONI_TWEAK_SETTINGS: "1":
                                 # no --game-tweak at all, the tweaks as a player has them)
scaled_screen: true              # the game may render smaller than the display (below)
blocked_plugins: {libSimDLL.so: missing}   # each player runs a copy of its install (out/e2e/.blocked-LANE)
                                 # with these native plugins `missing` or `unreadable` (mode 000)
stray_plugins: [x86_64/SimDLL.dll]   # and these paths under Plugins added, empty (`DIR/` a folder),
                                 # as Steam can leave another branch's (stray-plugin-folder.yaml)
extra_saves: [terra-wide]        # more colonies in the profile: fixtures/NAME.sav (and NAME.png)
                                 # as save_files/NAME/NAME.sav, for the Load screen
retired_colonies: [retired-chart-colony]   # Colony Summary entries: fixtures/NAME.json as
                                 # RetiredColonies/COLONY/COLONY.json, as a save writes them
data_collection: true            # the Data Collection option on, for a spec that opens the Supply
                                 # Closet (KleiInventoryScreen reads it); off by default in every
                                 # e2e and qa.sh profile, so no test run sends Klei a crash report
                                 # (an error report did once); a spec that turns it on must not
                                 # provoke errors
workshop: {id: 1960996649, layout: folder}   # one gated mod as a Steam subscription (folder, or zip with corrupt: true);
                                 # both players need tools/SteamStub/build.sh --dlc --ugc's library
test_mods: [OverlapTestMod]      # our test mods (tools/NAME, built to out/bin/NAME/release) as Local mods
game_args: [-moddev, --baz=1]    # arguments for the game: after the original's own, after the game's
                                 # executable in ours' Steam form (`-- …/OxygenNotIncluded ARGS`)
update_check: {releases: update-releases.json, tag: v2026.11.01.04}   # ours' update check reads this
                                 # fixtures file as GitHub's releases list, as that release (else: none)
setup: [...]                     # steps run first; the only place for driver setup.call
steps: [...]
```

`scaled_screen: true` is for a spec that sets a smaller resolution in fullscreen
([`fullscreen-window-resolution.yaml`](fullscreen-window-resolution.yaml)): the game then renders
at that size, stretched over the whole display. Before each step the runner reads
`Screen.fullScreen` and `Display.main.renderingWidth`/`Height`, and while they differ from the
display's size it scales the pointer's positions (the game's pixels, as `ui.find` gives them) to
the display's, and a main-menu shot's crop and masks to its grab.

Every spec gets OniMcp and, in both players, E2eDriver: `E2eDriver.dll` from its build
(`E2E_DRIVER`, default `out/bin/E2eDriver/release`) with `tools/E2eDriver/mod.yaml` and
`mod_info.yaml`, stamped 1399985550 in `mods.json`. A spec with driver steps fails without a
build of it. Each Local mod's stamp is its folder name's Mono string hash
(`ws-mods.py mono_hash`), the one KMod computes in both players. PLib reads options from
`config.json` beside the mod's assembly, or from `mods/config/ASSEMBLY/` when the mod uses the
shared location. The runner writes both, in every folder of the mod that holds a DLL, because KMod
may load an archived version.

A translation (a mod with a `.po` file at its root and no assembly) is installed disabled, as a
Workshop subscription leaves a new mod: KMod loads no content of an enabled translation and
disables it with a load error. A player picks its language on the main menu's Translations
screen (any installed mod with a `.po` is listed, Local ones too), which saves two prefs and
restarts the game. `translation: ID` writes those prefs into the run's `kplayerprefs.yaml`
(`SelectedLanguageType: UGC`, `InstalledLanguage: 'ID'`), so the game starts in that language;
[`translation-screen.yaml`](translation-screen.yaml) drives the screen itself up to its restart
dialog. `language: CODE` picks one of the game's own translations instead
(`SelectedLanguageType: Preinstalled`, `SelectedLanguageCode: CODE`), as its Translations screen
does, for mods that change the game's own strings. The translations' specs
(`mods/929139073-german-translation.yaml` and the others) check a colony's research and
priorities screens and pause menu, then the main menu, the options menu and its Game options
screen.

## Steps

Each step is a one-key mapping. Paths in `ui`, `under`, `root`, `crop` and `mask` are E2eDriver
hierarchy paths, where `*` matches within a name and `**` matches any number of levels. Take real
paths from a `ui.find` or `ui_dump` of a run, not from guesses.

### Orders and the game (OniMcp)

```yaml
- dig: [110, 207, 118, 209]                      # dig orders on a rectangle (priority 5)
- build: {prefab: Outhouse, at: [119, 207]}      # a blueprint, its lower-left cell at x, y
- build: {prefab: Tile, cells: [[1, 2], [2, 2]], priority: 7, material: Granite}
- run: {seconds: 600, until: {pending_digs: 0}}  # game time at speed 3, until `until` holds
- run: {seconds: 8, speed: 1}                    # at speed 1 (or 2)
- expect: {duplicants: 3, buildings: {Bed: 1}, pending_construction: 0, rooms: {Latrine: 1}}
- call: {tool: navigation_control, args: {action: focus_cell, x: 140, y: 207}}   # any OniMcp tool
- save: early-base                               # saved; --bless copies it into fixtures/
```

`expect` and `until` take `buildings: {Prefab: N}`, `blueprints: {Prefab: N}` (unfinished
ones), `duplicants`, `pending_construction`, `pending_digs`, `rooms: {Type: N}` and `cycle`. N is a
number, or `">=N"`, `"<=N"`, `">N"`, `"<N"`.

`save` copies the save into the run's evidence once it is on disk whole: with background-save,
ours finishes writing it after OniMcp has answered.

`build` places a blueprint through OniMcp's viewport map. OniMcp would also connect a powered
building to a power source with wire blueprints and dig orders of its own choosing; the runner
turns that off (`autoConnectPower: false`) unless the step says `wire: true`. A blueprint whose
cells are not dug yet is deferred by OniMcp until they are: the step fails then, so dig those
cells in an earlier step.

### Opening screens

```yaml
- open: {management: research}        # vitals, consumables, priorities, schedule, skills, research,
                                      # starmap, report, database: the screen's Action through
                                      # E2eDriver's ui.action, as its hotkey (OniMcp's open_management
                                      # fails on U60: ManagementMenu.OpenResearch returns bool there)
- open: {key: R}                      # the screen's hotkey, through XTEST
- open: {user_menu: toggle_compost, at: [130, 207]}   # a user-menu button by OniMcp action key
                                      # (on the object at a cell, or `id: ID`)
```

### Real input (XTEST, located through E2eDriver)

Before each click, the runner finds the element with `ui.find`. The top raycast hit at its centre
must be the element or one of its children. If it is not, the step fails with "occluded by PATH",
and nothing is clicked. A click is: move, 2 frames of hover, press, 2 frames, release.

```yaml
- click: {ui: "**/ResearchScreen/**/Tech_FarmingTech/Button"}
- click: {ui: "**/Tech_FineDining/Button", mods: [Shift]}      # modifier-click
- click: {text: "Quick Learner", under: "**/UnityDuplicitySelectionScreen"}
- click: {ui: "**/List/Item", index: 2, button: right}         # the third match; right button
- click: {ui: "**/WorldSelector/**/Row", double: true}         # a double click
- click: {at: [640, 112.5]}                                    # a screen point (top-left pixels), unchecked:
                                                               # for IMGUI (OnGUI) controls, which no path finds
- toggle: {ui: "**/TypeSelectScreen/**/Sandstone", to: true}   # clicks only if needed, then checks isOn
- dropdown: {ui: "**/MaterialDropdown", option: "Sandstone"}   # opens it, clicks the option by text
- type: {ui: "**/FileNameDialog/**/Input", text: "e2e", enter: true, clear: true}
- key: {code: "Shift+B"}                                       # KKeyCode names; Shift, Ctrl, Alt
- key: {code: Escape}
- key: {code: Escape, until: "**/PauseScreen"}                 # pressed (at most four times) until that is active
- key: {code: Escape, until: "**/PauseScreen", cond: gone}     # (or gone, inactive)
- key: {code: O, at: [110, 209]}                               # the pointer over a cell first
- key: {code: Q, down: true, at: [110, 209]}                   # held across the next steps (a run too),
- key: {code: Q, up: true}                                     # until released (or the run ends)
- drag: {from: [108, 212], to: [136, 205]}                     # world cells; `mods`, `button`, `steps`,
                                                               # `hold` (frames at `to` before release)
- drag: {from: [109, 207], via: [[109, 209], [110, 209]], to: [110, 207]}   # through cells in order
- click_world: {cell: [150, 212]}
- click_world: {cell: [106, 206], select: PiapiPlantHelper}  # clicks again until that is selected
- click_world: {pos: [109.5, 206.2], select: Granite}          # a world position: debris lies low in its cell
- scroll: {at: center, notches: -40}                           # or at: [x, y] (a cell), or a UI path
- tool: {toolbar: Sweep}                  # a ToolMenu collection by its text or icon name
- tool: {ui: "**/ToolMenu/**/SomeButton"}
- tool: {key: "Alt+S"}
```

Key names follow the game's `KKeyCode`: `A`–`Z`, `Alpha0`–`Alpha9` (or `0`–`9`), `F1`–`F15`,
`Escape`, `Return`, `Space`, `Tab`, `Backspace`, `Delete`, `UpArrow`/`DownArrow`/`LeftArrow`/
`RightArrow`, `LeftShift`, `LeftControl`, `LeftAlt`, `Keypad0`–`Keypad9`, `KeypadPlus`,
`Comma`, `Period`, `Slash`, `Minus`, `Equals`, `LeftBracket` and others (`KKEYCODES` in
e2e.py). `type` types any printable ASCII, on a US keymap (or the spec's `keyboard`).

```yaml
keyboard: fr                     # top level: the lane display's XKB layout (setxkbmap), for both players
steps:
  - key: {keycode: 24}                 # a physical key by X keycode (evdev): AZERTY's A, QWERTY's Q
  - key: {keycode: 24, mods: [Ctrl]}
  - keyboard: us                       # the layout switched while the player runs
```

A spec with `keyboard` runs its Xvfb with `-noreset` (an X server resets its keymap when its
last client leaves, as setxkbmap does) and `-ardelay 30000` (a key held for frames at a
colony's frame rate would autorepeat). E2eDriver's key log (`driver: {keys.log: {}}`, then
`type:E2eDriver.KeyLog::Down`, `::Events` and `::Chars`) records what each key reported:
[keyboard-layout.yaml](keyboard-layout.yaml).

### Expectations on the UI and on state (E2eDriver)

```yaml
- expect_ui:
    "**/CharacterContainer/**/Traits": {text: "~Quick Learner", active: true}   # ~ contains
    "**/Clock": {text: "/^\\d+:\\d\\d$/"}                 # /regex/
    "**/OkButton": {interactable: true}
    "**/Toggle": {isOn: false}
    "**/ErrorPopup": {exists: false}
- ui_wait: {path: "**/BlueprintSelector", cond: stable, frames: 300}   # E2eDriver's ui.wait
- expect_state:
    "Research.Instance.queuedTech.Count": ">=3"
    "type:BlueprintsV2.BlueprintState.rotation": 1
    "selected:MinionIdentity.GetComponent<Traits>": {contains: QuickLearner}
    "file:blueprints/e2e.blueprint": exists         # file:PATH, in the run's profile (no driver)
    "file:mods/config/Mod/presets": ">= 1"           # a folder reads as its count of entries
    "mass:debris": {save_as: before}                # remember a value...
- expect_state: {"mass:debris": ">= 0.95 * before"} # ...and compare with it later
- expect_state: {"Research.Instance.queuedTech.Count": "== original"}   # the original's value here
- expect_hit: {at: center, not: "~TMP SubMeshUI"}  # the top raycast hit there (ui.at); or top: PATTERN
- wait_state: {state: {"file:save_files/e2e/auto_save/early-base Cycle 1.sav": exists}, seconds: 120}
```

`wait_state` waits until each of its `state` expectations holds (as `expect_state` checks them,
but never against `original`), checked every quarter second in wall time, so also while the game
is paused; it fails after `seconds` (default 60) and records nothing. It waits for what the game
does at its own pace, such as an autosave that starts once its indicator has played.

`expect_hit` takes `at` as `scroll` does (`center`, a cell or a UI path) and checks the path of
the topmost raycast hit there, the empty path when nothing is hit: `top` must match it, `not`
must not (exact, `~sub` or `/regex/`). It checks every 3 frames until that holds or `frames`
(default 30) have passed. It does not move the pointer.

`expect_ui` checks the first active match; `count` checks how many match (a value, `"OP EXPR"` or
`{save_as: NAME}`) and `any_text` passes when any match has that text. It retries for a few
frames before it fails. An `expect_state` value is one of:
- a value;
- `"OP EXPR"`, where OP is `>=`, `<=`, `>`, `<`, `==` or `!=`, and EXPR is made of numbers,
  names, `+ - * /` and parentheses: a name is a value saved with `save_as`, or `original`
  (`">= before + 0.95 * dug"`, `">= 0.9 * original"`);
- `"== 'TEXT'"` or `"!= 'TEXT'"`: text in quotes, for names such as an enum's;
- `exists` or `missing`;
- `{contains: X}`, `{equals: X}` or `{save_as: NAME}`;
- a list of these, which holds when each of them does: `[">= 990", "== original"]`,
  `[{contains: TECTile}, {contains: RomenH_TEGTile}]` (a literal list is `{equals: [...]}`).

A mapping never repeats a key: YAML would keep only the last, so the loader rejects a spec that
does, naming the key and both lines. Several checks on one expression go in a list.

Besides `file:PATH`, the runner reads these, each item through its own `state.get`:
- `cells:X1,Y1..X2,Y2::EXPR`: the sum of EXPR over a rectangle of cells, `{cell}` standing for
  each cell's index: `cells:119,209..120,210::Grid.Mass.get_Item({cell})`;
- `count:HEAD[*] if .MEMBER == VALUE and ...`: how many items of an array or list match, as in
  `count:all:Clearable::[*] if .isMarkedForClear == true`;
- `sum:HEAD[*].MEMBER if ...`: the sum of a member over the items that match, as in
  `sum:all:Clearable::[*].GetComponent<PrimaryElement>.Mass if .GetComponent<PrimaryElement>.ElementID == SandStone`.

A condition compares the member's value as text (`true`/`false` for booleans; `'TEXT'` or
`"TEXT"` in quotes may hold spaces, `and` and `==`, and a value with a space must be quoted). A
member may call a method, its quoted arguments and parentheses kept whole: `if
.spawnInfo.id.StartsWith("GeyserGeneric") == true`. `--check` reports a `count:` or `sum:` that
does not parse. `count:` and `sum:` read every item's members in one E2eDriver request (`state.each`), so
lists of thousands of items (a new world's spawners) take a frame. `cells:` still reads one cell
a frame.

`== original` is checked after our run, against the value the original player recorded at the same
step. The original run itself records it and passes.

### Evidence (compared with the original afterwards)

```yaml
- ui_dump: {name: selector, root: "**/BlueprintSelector", mask_text: ['\d{4}-\d\d-\d\d']}
- ui_dump: {name: graph, root: "**/Graph_*", mask_path: ['(?<=Graph_)-?\d+']}   # ids in element names
- shot: {name: selector, crop: "**/BlueprintSelector", mask: ["**/Portrait", "**/DateLabel", [0, 0, 64, 64]]}
- shot: {name: dialog, crop: "**/Dialog", threshold: 0.04}     # a text-dense screen
- shot: overview                                               # the whole frame
- world_shot: {name: zoomed, ortho: 45, at: [128, 210], hide_ui: true, mask: [[600, 300, 80, 80]]}
- file_shot: {name: preview, path: "save_files/e2e/e2e.png"}   # an image the game wrote in the profile
```

- `file_shot` copies an image the game wrote into the run's profile (a save's colony preview, a
  timelapse frame; `path` relative to the profile, a glob's newest match) into the evidence, once
  it exists and its size holds for a second (`wait`, default 60 s), and compares it as a
  `world_shot` (5 %; sizes more than 2 px apart fail).
- `expect_exit: {seconds: 120, code: 0}` waits for the player to quit by itself (Quit to
  Desktop), with that exit code (`null` for any). The click that quits says `quits: true`, so
  E2eDriver going away during its last frames is not a failure. Only steps that need no player may follow:
  `file_shot` and `expect_state` on `file:` paths. The run then needs no live player at its end.

- `shot` moves the pointer to the bottom-right corner first, then takes the whole frame through
  OniMcp. It records the crop rect (the element's `ui.find` rect) and mask rects (UI paths, or
  screen rects `[x, y, w, h]`) beside the shot. At the main menu and its screens (no colony),
  where OniMcp takes no screenshot, it reads the lane's display instead (XGetImage of the
  root window, a frame later). Ours draws BRUTAL's credit tile (IMGUI) left of the main menu's
  news tiles: mask it in a main menu shot (`[210, 525, 60, 195]` beside the menu buttons).
  `pointer: stay` leaves the pointer where the last step put it, for UI that reacts to the
  pointer leaving it: a building's card (ProductInfoScreen) hides its requirements, effects and
  description when the pointer leaves the build menu, and whether a shot after the move catches
  that depends on frame timing in both players (Christmas Tree's card: collapsed in 2 of 3
  original runs of one spec, full in 3 of 3 of another).
- `world_shot` sets the camera through E2eDriver (`at` is a cell, `ortho` the orthographic size),
  hides the UI canvases (`hide_ui`, default true), shoots, and shows them again. Its masks are
  screen rects or UI paths. Symbolic masks such as Duplicants are not supported yet.

### E2eDriver commands

```yaml
setup:
  - driver: {seed: 12345}                            # before a random screen opens
  - driver: {setup.call: {type: "BlueprintsV2.Store", method: "Seed", args: ["e2e"]}}   # setup only
  - driver: {camera.set: {pos: [128.5, 210.5], ortho: 20}}
  - driver: {count: {methods: ["Brain:UpdateBrain"]}}  # its calls from now on: E2eDriver.Counting.Of("Brain:UpdateBrain")
steps:
  - driver: {buildmenu.settled: {}}                  # waits until the build menu has caught up
```

`buildmenu.settled` waits, as a `shot` does, until the build menu shows the state the game
computes now (see "The build menu catches up over frames"). Use it before picking a building
whose research or materials setup just provided: until PlanScreen re-checks that building, its
cached state is the old one, so picking it opens the prebuild tool, and when the check comes
depends on the frame rate (the original on llvmpipe draws a few frames a game second).

`setup.call` (reflection) is allowed only in `setup:`. It may prepare a test, never perform the
thing under test: issue #6 was "Played" through reflection while its screens were broken. An
argument JSON cannot spell is given as a state expression, `{"$": "Db.Get().Techs.Get(\"HVAC\")"}`
(E2eDriver's README, setup.call).

## Verdicts

A run of either player fails if a step fails, a click lands on an occluded element, an
expectation does not hold, or the player is not alive at the end.

A run of ours also fails on these differences from the original (§5.3 of the plan):

| Evidence | Fails when |
|---|---|
| log signatures (`qa-tools.py signatures`) | ours has one that none of the original's runs has |
| `require_log` | a line is in the original's log, not in ours |
| `ui_dump` (`ui-diff.py --tolerance 2 --mask-text ...`) | an element is only in one dump; a state or text differs after masking; a rect is off by more than 2 px, a size by more than 2 px and 2 %; an element is 0-sized in one only |
| `shot` (`qa-tools.py image --crop --mask`) | more than `threshold` of the unmasked pixels differ by more than 40 in a channel (default 2 %); each player is cropped to its own rect |
| `world_shot` | the same, with a default threshold of 5 % |
| `== original` | ours does not meet the relation to the original's value |
| an `expect_ui`/`expect_state` failure | the result notes that it held in the original |

A Graphic's `depth` is left out of every `ui_dump` diff (`IGNORE_FIELDS`): our player numbers the
graphics of a canvas differently, whatever is drawn, so it would fail every screen. A spec or a
`ui_dump` step adds members with `ignore_fields`, for values that follow random content.

Each check's numbers are in `result.json` under `checks`. A shot's diff mask is `NAME.diff.png`
in our run's folder: white pixels differ, grey ones are masked.

### Known findings

[`known-findings.yaml`](known-findings.yaml) lists engine differences a spec's `ui_dump` found,
each reviewed, with an id, a description, the issue or doc where it is described, and what it
matches: the ui-diff component (`Canvas`, `ScrollRect`, `rect`, …), the members that may differ,
and optionally element path globs, regexes on either player's value, and the specs it is limited
to. A difference one of them explains is no failure: the run of ours is `warn`, its `checks`
count the difference under the finding's id (`checks.ui_dump.NAME.known`), and the id goes into
the results record, so the mod reads ⚠️ with the id cited. Every other difference in the same
dump still fails the run.

A finding is added only for a difference understood to be the engine's (not the mod's, not a
spec's randomness), and only while it is being fixed: a finding hides that difference in every
spec it matches, so match narrowly (fields, path) and remove the finding with the fix.
`--check` validates the file.

### The results record

`--record` writes `results/SPEC.json`, the committed record of a spec (§7 of the plan):

```json
{"spec": "mods/2527442674-full-miner-yield.yaml", "mods": ["2527442674"],
 "verdict": "played", "why": "passed once in the original and twice in ours",
 "findings": [], "note": "a dig leaves debris weighing as much as the dug cells",
 "updated": "2026-10-22", "commit": "14e677a", "mod_dlls": {"2527442674": {"FullMinerYield.dll": "SHA-256"}},
 "runs": {"original": [{"run": "RUN_ID", "index": 1, "verdict": "pass", "signatures": 4, ...}],
          "ours": [{"run": "RUN_ID", "index": 1, "verdict": "pass", "reference_run": "RUN_ID",
                    "new_signatures": 0, "findings": [], "ui_dump": {...}, "shots": {...}}, ...]}}
```

An invocation's runs replace the record's runs of the same player; the others stay, so the
original's run (`--player original --bless --record`) and ours (`--player ours --repeat 2
--record`) come from separate invocations. `--note` sets the note, which is kept otherwise. The
verdict follows from the runs:

| Verdict | When | Table |
|---|---|---|
| `played` | an original run passed, and two runs of ours compared with it passed | ✅ |
| `warning` | the same, but a run of ours had differences known findings explain | ⚠️, the findings cited |
| `failed` | a run of ours compared with the passing original run failed | ⬜, "spec fails in ours" |
| `pending` | no passing original run, or fewer than two runs of ours against the newest one | not shown |

A run of ours counts only against the original run it was compared with (`reference_run`): a
new `--bless` makes earlier runs of ours stale until ours runs again. `scripts/modtable.py` reads
every record for the Played column and the note, ahead of its hand-written sets; those stay for
mods no spec has recorded.

## Mod specs and fixtures

A mod's spec is `mods/ID-slug.yaml`, its header the mod card: what the mod does, how a player
reaches each feature, what changes when it is used. Run it with its path under `tests/e2e`
(`scripts/e2e.py --player both --bless mods/ID-slug`): the original first, which blesses the
references, then ours, compared with them. A spec naming OniMcp's Workshop item (3807166922)
runs with that build as the runner's OniMcp, in place of `E2E_ONIMCP`'s.

Fixtures are blessed saves (`--bless` copies a spec's `save:` into `fixtures/`, not kept in git):

- `landing`: a new Spaced Out colony at its printing pod;
- `early-base` (`early-base.yaml`): dug out on both sides of the pod, with an outhouse, a manual
  generator, an unpowered research station and a cot. Debris lies on the floors.
- `prehistoric1`: a Prehistoric colony at cycle 1 (copied from a played save, not blessed).
- `decrew`: a late Spaced Out colony with busy gas and liquid networks, copied from
  onimaxxing's `saves/DeCrew.sav` (not blessed; 145 MB, about 4.5 minutes to load in the
  original), for [`pipe-flow.yaml`](pipe-flow.yaml).
- `rockets-so`: a Spaced Out sandbox colony with two rockets on launch pads, copied from a Klei
  bug-tracker save (onimaxxing's `saves/forum-so/351956__Test_-_fuel_oxy.sav`, not blessed),
  for [`rocket-launch.yaml`](rocket-launch.yaml).
- `vanilla-base` (`vanilla-base.yaml`): a base-game colony, no Spaced Out, for mods that support
  the base game only (`supportedContent: VANILLA_ID`). The original player starts at the main
  menu with Spaced Out off (`from: main-menu`, `spaced_out: false`), plays New Game, Survival,
  Classic, Terra at coordinate `SNDST-A-12345-0-0-0`, embarks, digs out both sides of the
  printing pod and the cuprite above its west end (up a ladder), builds an outhouse, a cot, a
  manual generator and a research station wired to it, researches Basic Farming and is saved
  at cycle 3 (about 0.8 MB; its header's dlcIds are `[""]`). Bless it with
  `scripts/e2e.py --player original --bless vanilla-base`. A spec `from: vanilla-base` runs
  both players in the base game: the runner writes `EXPANSION1_ID.ENABLED: 0` into the run's
  `kplayerprefs.yaml` for a save whose header lists no `EXPANSION1_ID`, as
  `scripts/windows-run-home.sh` does.
- `gas-boundary` (`gas-boundary.yaml`): `landing` with a sealed box of oxygen, carbon dioxide,
  chlorine and hydrogen side by side, mixed for 4 seconds, for
  [`gas-view.yaml`](gas-view.yaml). Bless it with
  `scripts/e2e.py --player original --bless gas-boundary`.
- `issue-52-gate` (`issue-52-gate-fixture.yaml`): `early-base` with an AND gate under a logic
  wire in cell (109, 206), for the `issue-52-after-load*.yaml` specs. Bless it with
  `scripts/e2e.py --player original --bless issue-52-gate-fixture`.

`from: main-menu` loads no fixture: the steps start at the main menu, as `vanilla-base.yaml`'s
do. Spaced Out stays on there unless the spec says `spaced_out: false`.

What a spec needs beyond them (a researched tech, materials, Duplicants on offer) it sets up in
`setup:` through E2eDriver, as the pilot specs do, rather than from a later fixture.

### A new game (worldgen mods)

A worldgen mod's spec starts from `early-base` too and plays a new game from there, as a player
would (`mods/2919833683-single-asteroid-clusters.yaml` is the pattern):

1. Escape, the pause menu's Main Menu, Quit Without Saving; then New Game, `StandardGame`
   (Survival) and the cluster category the mod's cluster has: `ClassicStyleGame`
   (`clusterCategory: SpacedOutVanillaStyle` or 1), `SpacedOutGame` (`SpacedOutStyle`, 2) or
   `EventGame` (`Special`), under `**/ClusterCategorySelectionScreen/Panel/Content/RootToggles/`.
2. The carousel's clusters are `all:DestinationSelectPanel::[0].clusterKeys`.
3. Type the cluster's coordinate, its `coordinatePrefix` and a fixed seed, into
   `**/CoordinateContainer/SeedInput` (`PREFIX-12345-0-0-0`, `clear: true, enter: true`): the
   screen selects that cluster and seed, whatever its category. Wait for
   `**/Section - Destination/DestinationDetailsHeader/ClusterName` to read the cluster's name
   (`cond: text, value: "~NAME"`); `ui_dump` the list of asteroids,
   `**/Section - Destination/World Scoll (Rect Size Set By Screen Code!)/Content/Details` (a
   `shot` there reads the lane's display, see
   [Evidence](#evidence-compared-with-the-original-afterwards)). Leave the coordinate row out:
   its input field's width after typing varies from run to run in either player.
4. Click `**/ColonyDestinationScreen2/Layout/Buttons/LaunchButton`. Worldgen runs on its own
   thread and the world loads behind the Duplicant selection: wait for
   `**/MinionSelectScreen/Content/BottomContent/Buttons/ProceedButton` to be stable with a
   generous budget (`frames: 60000`; real time is cheap, our player under load is slow), click
   it (Embark) and wait for `**/MinionSelectScreen` to be `gone`.
5. Check the generated cluster functionally, never cell by cell (mod-play-testing.md, "Worldgen
   mods"): `ClusterManager.Instance.WorldContainers` (`.Count`, `count:... if .worldName == ...`),
   the start world's `worldName`, `worldSize.x`/`.y`, `Biomes` (subworld folders),
   `m_generatedSubworlds`, `WorldTraitIds`, `m_seasonIds`; templates and geysers wait among
   `SaveGame.Instance.worldGenSpawner.spawnables` until uncovered
   (`count:... if .spawnInfo.id == POIBunkerExteriorDoor`); starmap POIs are
   `all:HarvestablePOIClusterGridEntity::`, `all:ArtifactPOIClusterGridEntity::`,
   `all:TemporalTear::`.

Writing a spec, `--hold` keeps the player running after the steps for a look through OniMcp
(its port) and E2eDriver (`ui.find`, `ui.dump`, `state.get`); `--hold-on-fail` does so after a
failing step. Paths come from what `ui.find` returns, never from guesses. Things that went wrong
in the pilot:

- Wait for a screen's moving part to settle (`ui_wait ... cond: stable` on the part that moves,
  not the screen's root), or a click lands on the wrong thing in one player only.
- Random content (immigrant cards, names) differs between the players even with `seed`: assert
  relations, and compare by structure only what does not follow it.
- Some game actions need the game to have run since the load (Blueprints Expanded placed nothing
  before): a short `run` before the UI steps is allowed.

## Run time

On this machine's lanes (WSL, Xvfb, Mesa's d3d12 driver on the GPU) the original player is CPU
bound in one thread: Unity's graphics worker spends about 85 % of its time copying memory inside
Mesa's d3d12 driver. It shows its main menu after about 125 s, loads `early-base` in 20-30 s,
draws about 2.2 frames a second whatever the window size, vsync, GL level, buffer storage or
Unity's graphics threading, and a `run: {seconds: 60}` takes about 140 s. Vulkan is not offered
(WSL has no d3d12 Vulkan driver, and Unity refuses llvmpipe's), and zink needs one.

Mesa's software rasteriser is faster for it: `E2E_ORIGINAL_ENV` gives the original player its
own environment, for example in `out/local-tools/qa.env`:

```sh
export E2E_ORIGINAL_ENV="GALLIUM_DRIVER=llvmpipe MESA_SHADER_CACHE_DIR=$HOME/.cache/oni-brutal-e2e/mesa-shader-cache"
```

With it the main menu is up in about 18 s and `early-base` loads in about 22 s (48 s the first
time: llvmpipe compiles Unity's shaders, which the shared shader cache keeps; each run's own
cache folder would start empty). It draws 8-11 frames a second on 9-11 cores, and the 60 game
seconds take about 39 s. Its shots differ from the d3d12 ones in under 0.05 % of the pixels, and
ours compared with them gives the same fractions as before. With several lanes at once,
`LP_NUM_THREADS=4` keeps each to four cores (about 5 frames a second). Ours keeps the d3d12
driver: it draws about 28 frames a second paused there, though a `run` also slows it (about 3
frames a second while the simulation runs, the same 60 game seconds in about 100 s against 37 s
on llvmpipe).

`scripts/e2e-bench.py` measures such launch variants (Mesa driver, environment, Unity arguments,
a copy of boot.config, the display's depth and size) and `scripts/e2e-warm.py` runs several specs
in one player per kind, loading each fixture over the last colony instead of relaunching: an
`early-base` load takes 5-7 s there in the original and 2-4 s in ours. It applies only to specs
that agree on what a launch fixes (mods, options, language, keyboard, Spaced Out), so for mod
specs it serves a spec's repeats rather than a batch.

Five of the specs without mods (`geyser-title`, `build-menu-search`, `consumables-scroll`,
`overlap-manual`, `issue-23-storage-row-click`), `--player both` on one lane, all passing:
1206 s as the lanes run now, 506 s with `E2E_ORIGINAL_ENV` above, and 253 s in warm players (the
five and `geyser-title` again).

## Load sweeps

Before mods get specs, `scripts/mod-sweep.py` finds which of them load at all: in batches of
about 22, each batch in the original and in ours, to the main menu and then the 2minnow colony
(`tests/e2e/fixtures/2minnow.sav`, copied from onimaxxing's saves) played 90 s. Each run is an
e2e.py run of a one-step spec, so lanes, the players' budget and the perf lock are the ones above.
Its players' screen is 1280x1024 (`--screen`): the MOD DIFFERENCES dialog the save opens lists a
whole batch, and its OK button falls below a 720-pixel screen. A run the runner itself cannot get
through (a button off screen or covered) stops the sweep rather than count against the mods.

```sh
scripts/mod-sweep.py s2000 --ranks 751-2274 --download --dry-run   # fetch the mods, list the batches
scripts/mod-sweep.py s2000 --ranks 751-2274                       # lanes 22-25, two players at once
scripts/mod-sweep.py s2000 --ranks 751-2274 --batches 7          # only batch 07 (delete its state to rerun)
scripts/mod-sweep.py again --ids 3467986764 3760101842 --batch 2 --first-lane 7 --last-lane 8
```

- **Which mods.** Catalog ranks (or `--ids`), less the ones onimaxxing flags `notLoading`,
  `crashesAtStartup`, `brokenByUpdate` or `unanalysed`, and never a malware-flagged one. A translation is recorded,
  not batched (the players install it disabled); a mod's catalog requirements are installed with
  it.
- **A batch.** First, once per sweep, both players run the save without mods: signatures only
  ours logs there are the engine's, not a mod's. Then the original and ours run the batch side by
  side. A mod the original disables ("Failed to load mod ...disabling") fails in the original;
  the rest of the batch runs again without it. A bisection splits off first the mods whose
  assemblies the failing run's errors name, else halves.
  A failing run of the original is bisected in the original until each failing mod is named, and
  the rest of the batch runs again. Ours fails on a failing run, a mod disabled that the original
  loaded, or a log signature that neither the original's run nor the baseline has. It is bisected
  in ours (each half compared with the same original run) down to single mods, with the
  signatures each logs alone. A mod so named runs alone in the original too, and fails there
  when it does (one that loaded in its batch only through a library another mod of it ships,
  as Plant Growth Time needs PLib). When both halves pass, the batch runs once more: a failure that
  does not come back was flaky, and one that does is recorded for the mods of that set together.
- **Output.** Runs and state are in `out/sweep/NAME/` (`E2E_OUT` moves it); a sweep run again goes
  on from the first batch without a state file. Results are `sweeps/NAME.tsv`: per mod, `original` and `ours` are `ok`, `menu` (fails by or at the main
  menu), `colony` or `-` (not run), with the error as the note. `scripts/modtable.py` reads them
  for the Loads and Colony columns; commit them with the regenerated table.

## Batch workflow

Agents play batches of about 25 mods, each in its own worktree. A worktree needs:

```sh
ln -s ~/projects/oni-brutal/out/local-tools out/local-tools    # qa.env, OniMcp, the original
ln -s ~/projects/oni-brutal/out/ws out/ws                      # the Workshop builds
cp ~/projects/oni-brutal/tests/e2e/fixtures/*.sav tests/e2e/fixtures/   # or bless early-base
export PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH DOTNET_ROOT=$HOME/.dotnet
dotnet build src/OniMoono.Host -c Release
dotnet build tools/E2eDriver -c Release
```

Locks, lanes and the players' budget are machine-wide (see [How a run goes](#how-a-run-goes)),
so agents can run beside each other. Each agent keeps to the lanes it is given
(`--first-lane A --last-lane B`), and `scripts/e2e.py --status` shows who holds what. Results are
one file per spec, so batches do not conflict; `docs/MOD_COMPATIBILITY.md` is regenerated, not
merged.

For each mod of the batch:

1. **Triage.** Read its row in [docs/research/mod-triage.md](../../docs/research/mod-triage.md):
   its classes (P, B, T, S, K, O, V, M, X) and flags (`touches_ui`, `asset_bundle_ui`, ...). A
   multiplayer mod's session needs real Steam (lobbies, P2P), which the offline double lacks: at
   most a spec of its menus, recorded in the original only, as
   `mods/3635632392-antigravity-multiplayer.yaml` is; a translation gets one with `translation:` (see [Mod specs and
   fixtures](#mod-specs-and-fixtures)). A worldgen mod gets one, with functional
   expectations: its worlds generate with its content, not a layout identical to the original's
   (see mod-play-testing.md, "Worldgen mods").
2. **Decompile** it: `ilspycmd -p -o out/local-tools/moddecomp/ID out/ws/ID/MOD.dll` (never
   committed), and confirm the classes from the code.
3. **The mod card.** Write it as the header of `mods/ID-slug.yaml`: what the mod does, how a
   player reaches each feature (button, hotkey, tool, side screen), what changes when it is used.
4. **Start from `early-base`.** Set up what the spec needs beyond it in `setup:` (reflection
   through `driver: {setup.call: ...}` is allowed there only).
5. **Explore in the original**, on your lanes: `scripts/e2e.py --player original --hold
   --first-lane A --last-lane B mods/ID-slug`. The log names OniMcp's and E2eDriver's ports and
   the display; take paths from E2eDriver's `ui.find` and `ui.dump`, never from guesses. Remove
   the `hold` file the log names to end it.
6. **Drive the UI by clicks**: `click`, `toggle`, `dropdown`, `type`, `key`, `drag`, `tool`, as a
   player would, then `expect_ui` and `expect_state` on the UI's response and on game state.
7. **Evidence only for what is not random**: `ui_dump` and `shot` of screens whose content does
   not follow random numbers; mask (`mask`, `mask_text`, `ignore_fields`) or leave out the rest,
   and assert relations instead.
8. **Pass in the original and bless**: `scripts/e2e.py --player original --bless --record
   --first-lane A --last-lane B mods/ID-slug`, until it passes. It fails there? Fix the spec,
   or, when the mod is broken upstream, say so in `broken` in `scripts/modtable.py`.
9. **Twice in ours**: `scripts/e2e.py --player ours --repeat 2 --record --note "WHAT WAS
   EXERCISED" --first-lane A --last-lane B mods/ID-slug`. The note says what the spec drove and
   how close the screens came, as the table's other notes do.
10. **The verdict** is the record's (`results/ID-slug.json`): `played`, `warning`, `failed` or
    `pending`. A `ui_dump` difference that a known finding explains makes it `warning` by itself.
    A new difference is the engine's to fix: file or link an issue, and only then, reviewed, add
    a finding to `known-findings.yaml` and run ours again. A failure in ours stays `failed`, its
    note saying what fails.
11. **Commit** the spec and its results file (and any new finding).
12. **Regenerate the table** (`scripts/modtable.py > docs/MOD_COMPATIBILITY.md`; it reads
    `scripts/mod-catalog.tsv`, and the sweeps from the main checkout's `out/`) and check with
    `git diff docs/MOD_COMPATIBILITY.md` that only your mods' rows changed. When a mod had a
    hand-written entry in `modtable.py` (`played`, `differences`, `unplayed`), remove it in the
    same commit. After a merge, regenerate rather than resolve the table by hand.
13. **Leave no processes behind**: a run's players end with it, however it ends (see "Nothing
    outlives its run"); `scripts/e2e.py --status` lists no lane, player or scope of yours. Never
    kill a process you have not proven to be yours or a dead runner's: `--clean` does that check.
    Kill anything else by its pid in a separate command, never with `pkill -f`.

### Scratch files

Agents of one session share its scratchpad folder, and two agents writing `qa.sh`, `info.sh` or
`run.log` at its top level overwrite each other. Each agent (or run) works in a folder of its
own and never writes outside it: `$SCRATCHPAD/<worktree or agent id>/` (for example
`…/scratchpad/agent-a66ab08/`), with a subfolder per run when runs overlap. The same goes for
anything written under `/tmp` or `%TEMP%` for a run: name it after the run (its run id, its lane,
its worktree). Test tools already keep runs apart: `e2e.py` writes to `out/e2e/SPEC/RUN_ID/` in
its worktree, `qa.sh` to `out/qa/run-DATE/`, and Windows runs use the run home they are given.

### Windows test runs

Every Windows test window opens on the second monitor and never takes the focus:
`scripts/run-windows.sh` passes `-screen-fullscreen 0 -monitor 2 --no-focus` before the run's own
arguments, and `scripts/original-windows.sh` passes `-screen-fullscreen 0 -monitor 2`, gives the
focus back to the window that had it if the game takes it while opening, and turns BepInEx's
console window off (`ONI_WIN_MONITOR=N` picks another monitor for both). A window gets the focus
only through `out/local-tools/win-run.ps1 -Match RUN_HOME -Action focus`. Act on a Windows game
process only through `win-run.ps1` (it finds the process by the run home in its command line),
never by its name: the user plays the same executables on this machine.
