# Tweaks

Moono also speeds up some of the game's own code. Most tweaks don't change gameplay and are on by
default. Four do, are marked **changes gameplay** below, and are off by default. Toggle any of
them in **Moono Tweaks** on the main menu. Measured gains are in
[PERFORMANCE.md](PERFORMANCE.md#the-tweaks).

## Choosing them

On the main menu, **Moono Tweaks** (under Mods) lists every tweak with an on/off switch, in the
sections below, each with a line on what it does; hovering a row says how it works, its
measured gain where it is large enough to notice, and its launch option. A **General** section
holds five options of the player's own: the Gassy Moo loading splash (on by default), frame
stats in the log (off by default), hiding personal paths in the log (on by default), for
laptops with two GPUs, the graphics card the game draws with (default, discrete or integrated;
default does what the original does) and, on Windows, presenting through Direct3D as the
original does (on by default).
**Reset to defaults** sets every row back to its default. Changes apply when the game restarts, which the screen does for you.

Your choices are kept in `oni-moono/settings.json` in the game's own data folder (usually
`~/.config/unity3d/Klei/Oxygen Not Included` on Linux,
`%USERPROFILE%\AppData\LocalLow\Klei\Oxygen Not Included` on Windows), which updates never touch.
The file records only what differs from the defaults, so a default changed by an update still
reaches you unless you chose otherwise.

## Launch options

Launch options still work and win over the screen, which then shows those rows as set by the
launch options:
- `--game-tweak exact`: every tweak but the four that change gameplay.
- `--game-tweak all`: every tweak.
- `--game-tweak none`: no tweaks.
- Tweaks by name: `--game-tweak exact --game-tweak slow-reachability`. `--game-tweak -NAME` leaves
  one out of `exact` or `all`.
- `--splash moo|none`: the loading splash.
- `--frame-stats`: where each frame's time went, in the log every 60 frames.
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

### Saving (on by default)

All 5 together: autosave freeze ~12 s → ~5 s on a large colony; U57, Linux under WSL2, by 2026-10-20.

- **Faster save writing** (`fast-serialization`): Autosaves take less time; the save file is the same. Writes the colony's numbers without boxing each one in an object.
- **Faster save checks** (`fast-save-type-checks`): Autosaves skip repeating the same checks for every object. Remembers what saving asks about each kind of component instead of asking again for every object.
- **Faster save compression** (`fast-save-compression`): Saving and loading take seconds less; files stay compatible, a little larger. Compresses with .NET's zlib instead of the game's own; the files are about 14% larger.
- **Background saving** (`background-save`): The save is compressed and written to disk while the game plays on. Compression and the file write move to another thread; quitting waits for them.
- **No cleanup pause when saving** (`no-save-gc`): Skips the memory cleanup the game forces after each save, a pause of its own. Skips the full garbage collection the game asks for after saving.

### Simulation (on by default)

- **Faster worker threads** (`fast-jobs`): Less time lost waking idle worker threads every frame. Wakes only as many of the game's worker threads as a job has work for, instead of all of them for every job (about 14 a frame). *Measured: +11% frame rate on a large colony; U57, Linux under WSL2, by 2026-10-20.*
- **Faster sublimation** (`fast-sublimation`): Items that can't sublimate (polluted dirt in full gas) are skipped. Items already blocked by full gas cells are skipped in one batched update.
- **Faster radbolt collisions** (`fast-particle-collision`): Radbolts look for each other only where one can be. A radbolt skips searching every stored item nearby when no other radbolt is close.
- **Faster pickup checks** (`fast-offset-tables`): Where an item can be picked up from is rechecked only when its surroundings change. Fetch updates recheck only the items whose work spots can have changed.
- **Faster spatial index cleanup** (`fast-partitioner-cleanup`): The once-a-second tidy-up of what is where looks only at what was removed. The spatial index's cleanup visits only the entries that were removed.
- **Faster conveyor rebuilds** (`fast-conveyor-paths`): Building or removing conveyor rails no longer stalls the game. The rails' network rebuild finds its routes from an index instead of searching every kept route again. *Measured: no hitch on rail edits (was 0.05–0.5 s); U57, Linux under WSL2, 2026-10-25.*

### Graphics & overlays (on by default)

- **Faster animation events** (`fast-anim-events`): Animations check their sound and effect cues with less work. Keeps a compact copy of what each playing animation's per-frame event check reads.
- **Faster animation cleanup** (`fast-anim-cleanup`): Removed animations are swept out only where one was removed. The sweep for destroyed animations skips grid chunks that can't hold any.
- **Parallel animation uploads** (`fast-anim-batches`): Animation data for the graphics card is prepared on several threads. Animation batches write their instance and symbol data on worker threads, the rest in the game's order.
- **Fewer animation uploads** (`fast-symbol-overrides`): Swapped animation parts are re-sent to the graphics card only when they change. Unchanged symbol-override blocks of Duplicants and buildings aren't uploaded again.
- **Faster overlays** (`fast-overlays`): Overlays skip rereading what can't have changed since the last frame. Temperature passes over buildings out of view without reading them, power asks each circuit's load once a frame, the rest keep what their targets' visits read. *Measured: temperature overlay ~40% cheaper; U57, Linux under WSL2, 2026-10-24.*
- **Overlay texture cleanup** (`fast-overlay-textures`): Opening and closing overlays no longer piles up video memory. The screen-sized textures the game remakes on each coloured overlay toggle are freed, instead of kept until the next load (about 18 MB a toggle at 1024×768).

### Changes gameplay (off by default)

- **Slower reachability checks** (`slow-reachability`): Duplicants notice a newly opened or blocked path up to 3 s later. Which errands and items can be reached is rechecked every 4 s instead of every second; temperature and building checks are never delayed.
- **Lazy unseen conveyor items** (`off-screen-conveyors`): Items riding rails off screen are placed only when they stop or come into view. The rails carry exactly the same things, but items' positions lag while off screen.
- **Land items lost below a world** (`off-world-fallers`): Items that fall out the bottom of a world (a rocket's chute) stop falling forever. They hang just below the world, stepped as falling every tick; landing them where they hang stops that cost.
- **Critter and dupe brain updates capped at 30 updates a second** (`brain-rate`): Critters and dupes would otherwise update on every frame. This trims that bookkeeping above 30 fps. The game updates 1 Duplicant and 5 critter brains every rendered frame; this caps them at 30 and 150 a second, spread over frames. One that just finished an errand or is under threat still gets the next update. Saves time only above 30 fps.
<!-- tweaks:end -->

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
