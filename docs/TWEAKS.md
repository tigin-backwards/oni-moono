# Tweaks

Moono also speeds up some of the game's own code. Most tweaks don't change gameplay and are on by
default. Those that do are off by default: the ones under **changes gameplay** below, and
incremental path probes, which changes only which of two routes of the same cost a Duplicant
takes. Toggle any of
them in **Moono Tweaks** on the main menu. Measured gains are in
[PERFORMANCE.md](PERFORMANCE.md#the-tweaks).

## Choosing them

On the main menu, **Moono Tweaks** (under Mods) lists every tweak with an on/off switch, in the
sections below, each with a line on what it does; hovering a row says how it works, its
measured gain where it is large enough to notice, and its launch option. A **General** section
holds seven options of the player's own: the Gassy Moo loading splash (on by default), the
[update check](#update-check) (on by default), frame stats in the log (off by default), hiding
personal paths in the log (on by default), for
laptops with two GPUs, the graphics card the game draws with (default, discrete or integrated;
default does what the original does), on Windows, presenting through Direct3D as the
original does (on by default), and [garbage collection](#garbage-collection) (fast by default).
The search box above the list keeps the rows whose title, line, tooltip or tweak name contain
what you type (in the game's language, any case; each word on its own, as the game's own
searches match), and the headings of the sections that keep a row.
**Reset to defaults** sets every row back to its default. Changes apply when the game restarts, which the screen does for you.

Your choices are kept in `oni-moono/settings.json` in the game's own data folder (usually
`~/.config/unity3d/Klei/Oxygen Not Included` on Linux,
`%USERPROFILE%\AppData\LocalLow\Klei\Oxygen Not Included` on Windows), which updates never touch.
The file records only what differs from the defaults, so a default changed by an update still
reaches you unless you chose otherwise.

## Update check

With **Update check** on (the default), Moono makes one network request of its own: in the
background after start-up, a GET of `https://api.github.com/repos/tigin-backwards/oni-moono/releases`
with a `User-Agent: OniMoono-update-check` header and nothing else (no cookies, IDs or details
of your game). It waits at most 8 seconds. The answer is kept for 6 hours in
`oni-moono/update-check.json` beside the settings file, so most launches ask nothing. When a
newer release for your build is out, a Gassy Moo badge next to **Moono Tweaks** on the main menu
says so: hovering it lists what changed in each newer release, clicking it opens the release
page in your browser. A normal build looks at normal releases only, a U60 build at the U60
pre-releases only; releases marked "Superseded" are skipped. Offline, or when GitHub doesn't
answer, there is no badge and the log has one line about it. With the check off, Moono makes no
network requests of its own. A build made without a release tag (a developer build) checks
nothing.

For tests, `ONI_UPDATE_RELEASES_URL` reads the releases list from elsewhere (a `file://` URL, a
path or an http(s) URL; `none` checks nothing), never cached, and `ONI_UPDATE_TAG` sets the
release the build counts as. `scripts/run.sh` sets `ONI_UPDATE_RELEASES_URL=none`, so test runs
never ask GitHub; `tests/e2e/update-badge.yaml` gives the badge a fixture instead.

## Garbage collection

**Garbage collection** chooses how the game frees memory it no longer uses. It applies from the
next start.

- **Fast** (the default) is .NET's Server GC with DATAS (dynamic adaptation of the heap count,
  on by default for Server GC since .NET 9), concurrent. It collects on all cores, so pauses are
  rare and short, but it keeps more memory.
- **Low memory** is .NET's Workstation GC, concurrent: one heap and less RAM (about 0.8 GB less
  in play on a large colony, 0.3-0.4 GB at the load's peak), but it collects about ten times as
  often and a colony takes about 10 s longer to load. It is only worth choosing if your PC is
  short on RAM.

Measurements are in [research/gc-modes.md](research/gc-modes.md).

The runtime fixes its GC as it starts, before Moono's code runs. On Windows, OniMoono.exe reads
the choice itself (`--gc`, else `settings.json`'s `"gc"`) and sets the runtime property
`System.GC.Server` before starting .NET. On Linux and macOS the player starts through .NET's
own launcher, which reads only `OniMoono.runtimeconfig.json`: when the runtime started with the
other GC, the player starts itself again in place (the same process, `execve`) with
`DOTNET_gcServer=0` or `1`, before it opens the log. A `DOTNET_gcServer` you set yourself wins
over the option. `Player.log` has a `gc:` line naming the GC in use (Server GC with DATAS,
Workstation concurrent GC) and why, and a `gc config:` line with the runtime's GC settings.

## Launch options

Launch options still work and win over the screen, which then shows those rows as set by the
launch options:
- `--game-tweak exact`: every tweak that leaves the game as the original plays it: all but the
  Changes gameplay and Bug fixes groups (so without `path-probe-repair` and
  `fix-load-screen-clicks`, which are on by default).
- `--game-tweak all`: every tweak.
- `--game-tweak none`: no tweaks.
- Tweaks by name: `--game-tweak exact --game-tweak slow-reachability`. `--game-tweak -NAME` leaves
  one out of `exact` or `all`.
- `--splash moo|none`: the loading splash.
- `--frame-stats`: where each frame's time went, in the log every 60 frames, and every 600 frames
  the game's speed and update, the waits and the machine's state
  ([PERFORMANCE.md](PERFORMANCE.md#frame-stats-in-a-players-log)).
- `--log-paths hidden|full`: Player.log with your user folder, Documents folder, Steam ID,
  Steam name and Klei ID replaced (`%USERPROFILE%` or `~`, `%DOCUMENTS%`, `<steam id>`,
  `<name>`, `<klei id>`), or as logged.
- `--gpu default|discrete|integrated`: the graphics card on a laptop with two
  ([ARCHITECTURE.md](ARCHITECTURE.md#2-process-and-assembly-structure), "The GPU option").
- `--dxgi on|off`: Windows, frames shown through a Direct3D 11 swap chain or through OpenGL's
  SwapBuffers ([ARCHITECTURE.md](ARCHITECTURE.md#35-rendering), "Presentation on Windows").
  If HDR switches on or the screen goes very bright in fullscreen, look for the `present:` line
  in `Player.log`: when it says SwapBuffers is used, it says why. Then, on NVIDIA, set
  **Vulkan/OpenGL present method** to **Prefer native** in the NVIDIA Control Panel, and turn
  Auto HDR (Windows' Graphics settings) or RTX HDR (the NVIDIA app) off for OniMoono.exe.

- `--gc fast|low-memory`: the [garbage collection](#garbage-collection).

Older setups put `--game-tweak exact` in the launch options; remove it (the setup prints the new
line) to choose in the game instead.

At start-up the player logs every option it used and where it came from (default, settings file
or command line).

## How "exactly the same" is checked

Each exact tweak has a verify mode (`ONI_TWEAK_VERIFY=1`): the tweak still takes its shortcut, but
the game's own work is done as well and the two are compared, bit for bit where the result is
data. QA's verify stage runs a late-game colony with every tweak on in this mode, every overlay
opened in turn, for at least 10 game minutes, and fails on any mismatch.

## The tweaks

Grouped as on the screen. Names are as used with `--game-tweak`; the gains in the screen's
tooltips are those below, and the rest are in [PERFORMANCE.md](PERFORMANCE.md#the-tweaks).

<!-- tweaks:begin: generated from src/OniMoono.Host/GameTweaks/GameTweakNames.cs; ONI_UPDATE_DOCS=1 with the unit tests rewrites it -->

### General (on by default)

- **Faster Load screen** (`fast-load-screen`): With many saves, the Load screen opens and loads more colonies sooner. Each save's details are read from its file once a refresh instead of four times, several files at a time, and the previews of pages already shown are not made again. The colonies, their order, the previews and the log are the same. *Measured: Load screen with 4,637 saves opens in ~0.6 s (was ~1.4 s); U60, 4,637 saves, Linux under WSL2, 2026-11-03.*

### Saving (on by default)

All 5 together: autosave freeze ~12 s → ~5 s on a large colony; a late-game Spaced Out colony with every DLC (cycle 17,000+), Linux under WSL2, by 2026-10-20.

- **Faster save writing** (`fast-serialization`): Autosaves take less time; the save file is the same. Writes the colony's numbers without boxing each one in an object.
- **Faster save checks** (`fast-save-type-checks`): Autosaves skip repeating the same checks for every object. Remembers what saving asks about each kind of component instead of asking again for every object.
- **Faster save compression** (`fast-save-compression`): Saving and loading take seconds less; files stay compatible, a little larger. Compresses with .NET's zlib instead of the game's own; the files are about 14% larger.
- **Background saving** (`background-save`): The save is compressed and written to disk while the game plays on. Compression and the file write move to another thread; quitting waits for them.
- **No cleanup pause when saving** (`no-save-gc`): Skips the memory cleanup the game forces after each save, a pause of its own. Skips the full garbage collection the game asks for after saving.

### Simulation (on by default)

- **Faster worker threads** (`fast-jobs`): Less time lost waking idle worker threads every frame. Wakes only as many of the game's worker threads as a job has work for, instead of all of them for every job (about 14 a frame). *Measured: +11% frame rate on a large colony; a late-game Spaced Out colony with every DLC (cycle 17,000+), Linux under WSL2, by 2026-10-20.*
- **Faster sublimation** (`fast-sublimation`): Items that can't sublimate (polluted dirt in full gas) are skipped. Items already blocked by full gas cells are skipped in one batched update.
- **Faster radbolt collisions** (`fast-particle-collision`): Radbolts look for each other only where one can be. A radbolt skips searching every stored item nearby when no other radbolt is close.
- **Faster pickup checks** (`fast-offset-tables`): Where an item can be picked up from is rechecked only when its surroundings change. Fetch updates recheck only the items whose work spots can have changed.
- **Faster spatial index cleanup** (`fast-partitioner-cleanup`): The once-a-second tidy-up of what is where looks only at what was removed. The spatial index's cleanup visits only the entries that were removed.
- **Faster conveyor rebuilds** (`fast-conveyor-paths`): Building or removing conveyor rails no longer stalls the game. The rails' network rebuild finds its routes from an index instead of searching every kept route again. *Measured: no hitch on rail edits (was 0.05–0.5 s); a late-game Spaced Out colony with every DLC (cycle 17,000+), Linux under WSL2, 2026-10-25.*
- **Compact navigation links** (`compact-nav-links`): The map of where Duplicants and critters can move takes a fraction of the memory. Each pathfinding grid keeps a cell's moves packed together instead of a fixed row of slots for every cell of the map; the pathfinder reads the same moves in the same order.
- **Faster state machine lookups** (`fast-smi-lookups`): Finding one of an object's state machines no longer searches all of them each time. Each object remembers which of its state machines answered each lookup until its list of state machines changes.
- **Paged path grids** (`paged-path-grids`): Each Duplicant's map of path costs takes memory only where it can walk. A Duplicant's path grid covers the whole map; it is kept in small pages made the first time a probe writes there, and the parts never written read as the empty grid did. The pathfinder reads the same values.

### Graphics & overlays (on by default)

- **Faster animation events** (`fast-anim-events`): Animations check their sound and effect cues with less work. Keeps a compact copy of what each playing animation's per-frame event check reads.
- **Faster animation cleanup** (`fast-anim-cleanup`): Removed animations are swept out only where one was removed. The sweep for destroyed animations skips grid chunks that can't hold any.
- **Parallel animation uploads** (`fast-anim-batches`): Animation data for the graphics card is prepared on several threads. Animation batches write their instance and symbol data on worker threads, the rest in the game's order.
- **Fewer animation uploads** (`fast-symbol-overrides`): Swapped animation parts are re-sent to the graphics card only when they change. Unchanged symbol-override blocks of Duplicants and buildings aren't uploaded again.
- **Faster overlays** (`fast-overlays`): Overlays skip rereading what can't have changed since the last frame. Temperature passes over buildings out of view without reading them, power asks each circuit's load once a frame, the rest keep what their targets' visits read. *Measured: temperature overlay ~40% cheaper; a late-game Spaced Out colony with every DLC (cycle 17,000+), Linux under WSL2, 2026-10-24.*
- **Overlay texture cleanup** (`fast-overlay-textures`): Opening and closing overlays no longer piles up video memory. The screen-sized textures the game remakes on each coloured overlay toggle are freed, instead of kept until the next load (about 18 MB a toggle at 1024×768).

### Changes gameplay (off by default but where marked)

- **Incremental path probes** (`path-probe-repair`): Duplicants' path checks redo only what changed in the world, using less CPU. Each Duplicant's last path probe is repaired around what changed (digging, building, doors, liquids) instead of searched again from scratch. Costs and reachable cells stay the same; where two routes cost the same, a Duplicant may walk the other one, not the original's. Keeps about 0.9 MB a Duplicant. Off while another mod changes path probing (Fast Track's Cache Paths). *On by default.*
- **Slower reachability checks** (`slow-reachability`): Duplicants notice a newly opened or blocked path up to 3 s later. Which errands and items can be reached is rechecked every 4 s instead of every second; temperature and building checks are never delayed.
- **Lazy unseen conveyor items** (`off-screen-conveyors`): Items riding rails off screen are placed only when they stop or come into view. The rails carry exactly the same things, but items' positions lag while off screen.
- **Critter and dupe brain updates capped at 30 updates a second** (`brain-rate`): Critters and dupes would otherwise update on every frame. This trims that bookkeeping above 30 fps. The game updates 1 Duplicant and 5 critter brains every rendered frame; this caps them at 30 and 150 a second, spread over frames. One that just finished an errand or is under threat still gets the next update. Saves time only above 30 fps.

### Bug fixes (off by default but where marked)

- **Land items lost below a world** (`off-world-fallers`): Items that fall out the bottom of a world (a rocket's chute) stop falling forever. A bug in the game: they hang just below the world, stepped as falling every tick. This lands them where they hang.
- **Open a colony once on the Load screen** (`fix-load-screen-clicks`): After Load more, a click on a colony no longer builds its list of saves again for every refresh. A bug in the game's own code: each Load more adds another copy of the click to the colonies already shown, so one click builds the colony once for every refresh, and the last one stays on screen. With this, one click builds it once and logs once; the screen ends up the same. *On by default.*
<!-- tweaks:end -->

### Incremental path probes

On by default, though it changes gameplay in one way (it is in the Changes gameplay group and
not in `exact`): costs and reachable cells are the original's, but where two routes cost the
same a Duplicant may walk another one than in the original (Navigator builds the walking path
from the probe, PathGrid.BuildPath). It keeps about 0.9 MB a Duplicant (four bits of suit flags
a state, on a 640×404 world). It steps aside while Fast Track's Cache Paths is on (below).

Each Duplicant's path probe (the cost to every cell it can reach, which errands and pathing read
until its next probe) is redone every few seconds, four Duplicants a frame, on a thread of its
own. With `path-probe-repair`, a probe from the same cell, nav type, suit flags and abilities as
the Duplicant's last one repairs that one instead: what changed since (the nav grid's links,
doors and their permissions, liquid and solid cells, tube entrances, suit checkpoints) is found
through the game's own writers, the cells whose route changed or got worse are dropped with the
cells reached through them and found again, and cheaper routes spread as in the game's search.
Any other probe is the game's search, and so is a repair that would redo half of what the last
probe reached (a door by the Duplicant's spot): a search costs less then. The probes are not
delayed; temperature and building checks are untouched.

What differs from the original: where two routes to a cell cost the same, the game keeps the one
its search finds first, the repair the one it had or finds first. A Duplicant can then take the
other route of the same cost. The route it keeps can also change what lies beyond that cell: when
the two routes carry different suit flags (one passes a suit checkpoint), the cells past it may
cost more or less, or be reachable or not, depending on the flags; and a step back the way the
route came is never searched from a cell, so with the other route a cell next to it can be found
at another cost. Such a difference is what the game's own search gives with another search order,
and lasts until the Duplicant's next full search (when it moves). With `ONI_TWEAK_VERIFY=1` every
repair is checked against a new search, and one that answers otherwise is checked to be a result
the game's search can give (each cell set from its route, none reachable cheaper); only a repair
that is not counts as wrong. Every search is also checked against the game's own on the same
Duplicant. In a 200-Duplicant colony and a late-game base-game colony (U60) none of about
10,700 repairs and 180 searches checked gave another cost or reachable cell.

It stands down for the session, logged once in Player.log (`path-probe-repair: off (...)`), when
another mod patches the probe or the methods it relies on. Fast Track's **Cache Paths** does:
turn off Cache Paths to use this. Fast Track's **Background Pathing** only wraps two PathGrid
methods in locks; the tweak checks that they answer as the game's and runs alongside it. With
Fast Track's Fast Reachability on and its next chore priority set to Delay, Fast Track also
patches the probe's work order, and the tweak stands down.

### The Load screen

Each refresh of the Load screen (opening it, Load more, a conversion between cloud and local)
lists every save twice and reads each one's header four times. With `fast-load-screen` a refresh
reads each header once (a later read of the same file in the refresh, while its listed time is
the same, gets a copy of the first), reads them ahead on 8 threads, and still parses and logs
them in the game's order: the colonies, their order and the log are the game's, and a read that
fails is read and logged again as the game would. While the screen is shown, a preview whose
file has the same bytes as one already made gets the same pixels without being decoded or, on
U60, drawn and read back from the GPU again; each preview is still read from its file and is a
new texture of the game's own. With `ONI_TWEAK_VERIFY=1` each header and preview kept is checked
against the game's own read ([measurements](research/load-screen-many-saves.md#results-2026-11-03)).

`fix-load-screen-clicks` fixes a bug in the game's own code (U59 and U60). Each refresh adds the
colony list's click handler to the rows already shown again, without removing the old one, so
after k Load more clicks one click on a colony of an earlier page builds that colony's list of
saves k + 1 times in one frame (seconds for a colony of hundreds of saves); the last build stays
on screen. With the tweak a row keeps only the handler of the refresh that filled it. The screen
ends up the same; what differs from the original is that the colony is built once a click, so
the earlier builds' log lines (`Failed to find cluster ...` for a save of an unknown cluster)
are not written, a mod patching `LoadScreen.ShowColony` sees one call instead of k + 1, and the
rows those builds made and destroyed within the frame are not made. It is on by default (the
player's choice), but in the Bug fixes group and left out of `exact`, since the original does
the repeated work. It installs only where the Load screen still has the bug: when a game update
changes `LoadScreen.AddColonyToList` or the fields the fix reads, or the method no longer adds
the handler without clearing the button first, the tweak is left out with one line in
Player.log (`fix-load-screen-clicks: left out, ...`).

## Translating Moono Tweaks

The screen shows the game's language when the player has a translation for it, and English
otherwise. Translations are `.po` files in the game's format, in `translations/` (beside the
player in a release): `zh.po` is Simplified Chinese. The player picks the file named after the
game's language code (`Localization.GetLocale().Code`), then that code's part before `_` or `-`.
The code is `zh`, `ko` or `ru` for the game's own Chinese, Korean and Russian, and also `zh` for
a translation mod whose language contains it (Traditional Chinese packs too). For other
translation mods it is the `Language:` header of the mod's `strings.po`, such as `de` or `pt_BR`.

Each string has a key (`msgctxt`, such as `STRINGS.MOONO.TWEAKS.ROWS.FAST_JOBS.TITLE`), the
English it was translated from (`msgid`) and its translation (`msgstr`). A string that is
missing, empty, or whose placeholders (`{0}`) or tags differ from the English shows in English.
Launch options and paths are not translated.

To add a language:
1. Copy `translations/moono.pot` to `translations/CODE.po` (CODE as above) and fill in each
   `msgstr`. Only `\"` and `\n` work as escapes.
2. Run the catalog tests:
   `out/bin/OniMoono.UnityEngine.Tests/debug/OniMoono.UnityEngine.Tests -class OniMoono.Tests.TweakTranslationTests -showLiveOutput`
   (after `scripts/check.sh` or a build). They fail on an unknown key or a placeholder that does
   not match the English, and list the strings that are missing or were translated from older
   English (stale).

When the English changes in `src/OniMoono.Host/GameTweaks/GameTweakNames.cs` or `TweakStrings.cs`,
run the unit tests with `ONI_UPDATE_DOCS=1` to regenerate `moono.pot`; the tests then list each
translation's stale strings.
