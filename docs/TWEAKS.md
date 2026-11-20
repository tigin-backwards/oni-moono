# Tweaks

Moono also speeds up some of the game's code. Most tweaks don't change gameplay and are on by
default. Those that do are off by default: the ones under **changes gameplay** below, and
incremental path probes, which changes only which of two routes of the same cost a Duplicant
takes. Toggle any of them in **Moono Tweaks** on the main menu. Measured gains are in
[PERFORMANCE.md](PERFORMANCE.md#the-tweaks).

## Choosing them

On the main menu, **Moono Tweaks** (under Mods) lists every tweak with an on/off switch, in the
sections below, each with a line on what it does. Hovering a row says how it works, its
measured gain where it is large enough to notice, and its launch option.

A **General** section holds eight options of the player:

- The Gassy Moo loading splash (on by default).
- The [update check](#update-check) (on by default).
- Frame stats in the log (off by default).
- Hiding personal paths in the log (on by default).
- For laptops with two GPUs, the graphics card the game draws with (default, discrete or
  integrated; default does what the original does).
- The renderer (auto: Vulkan, or OpenGL when Vulkan can't start).
- On Windows, presenting through Direct3D as the original does (on by default).
- [Garbage collection](#garbage-collection) (fast by default).

Using the screen:

- **Search.** The search box above the list keeps the rows whose title, line, tooltip or tweak
  name contain what you type, and the headings of the sections that keep a row. It matches in
  the game's language, in any case, each word separately, as the game's searches do.
- **Reset to defaults** sets every row back to its default.
- **Version.** The title bar's left shows which Moono you run: its release's name
  (`Moono 2026.11.19.01`), or the commit of a build you made yourself.
- **Applying.** Changes apply when the game restarts, which the screen does for you.
- **Unavailable tweaks.** A tweak this build of Moono can't run on your game version (a game
  update changed the code it changes) is off. Its row is greyed out with "Not available on this
  game version", and the game's code runs instead. Your choice for it is kept for when it is
  available again.

Your choices are kept in `oni-moono/settings.json` in the game's data folder, which updates
never touch. That folder is usually `~/.config/unity3d/Klei/Oxygen Not Included` on Linux and
`%USERPROFILE%\AppData\LocalLow\Klei\Oxygen Not Included` on Windows. The file records only
what differs from the defaults, so a default changed by an update still reaches you unless you
chose otherwise.

## Mods that patch the same code

At each main menu, after every mod has made its Harmony patches, Moono looks for mods that patch
the methods the tweaks you have on change:

- Each tweak's Harmony patches (each tweak patches under its own Harmony ID,
  `OniMoono.Host.GameTweaks.NAME`).
- The methods the load-time rewrites of `compact-nav-links` and `paged-path-grids` changed.
- The methods whose patching makes a tweak step aside.

It only shows what it finds. It never turns a tweak on or off. The grades:

- **High**: a mod's transpiler on a method a tweak patches or rewrites, or a mod's prefix that
  can skip the original (returns `bool`) on a method a tweak replaces (with a skipping prefix, a
  transpiler or a rewrite). The row's icon is amber.
- **Low**: anything else (postfixes, finalizers, prefixes that return nothing). The icon is grey.
- **Steps aside**: the tweak turns itself off for such a patch, so there is no conflict and it
  isn't counted: `path-probe-repair` (the probe and what it relies on), `fast-anim-batches` (its
  write path), `fast-symbol-overrides` (its kept calls), `fast-smi-lookups`
  (`StateMachineController.GetSMI`), `fast-save-roots` (`SaveManager.Unregister`),
  `fast-partitioner-events` (the scene partitioner's event, insert, remove and scan; also while
  Fast Track, MoreTek, Colony Performance Fixes or Multiplayer Mod is active), `fast-conduit-sorts`
  (the pipe and rail flows' `Sim200ms`; also while those mods are active),
  `fast-conveyor-paths` (`SolidConduitFlow.TryAdd`),
  `off-world-fallers` (the gravity step, `GravityComponents.FixedUpdate`, for any patch but a
  postfix; see [Items lost below a world](#items-lost-below-a-world)),
  `fast-overlay-removals` (the Decor overlay's handler for removed objects and its grid's
  `Remove`),
  `direct-rail-bridges` (the conveyor bridge's update, the rails' `AddPickupable` and
  `RemovePickupable`, `Pickupable.OnStore` and what it calls for a rail item, the egg's and the
  world inventory's store handlers; see [Conveyor bridge moves](#conveyor-bridge-moves)),
  `fast-anim-events` (the animation event loop and what it reads; see
  [Animation events](#animation-events)), `fast-pipe-rebuilds` (the pipes' rebuild handlers,
  `Conduit.RefreshInvalidNetworkStatusItem` and the network managers' rebuild; see
  [Pipe network rebuilds](#pipe-network-rebuilds)),
  `sim-step-pace` (`Game.SimEveryTick` and `UnsafeSim200ms`),
  `low-fps-timing` (each part on its own: the game time cap for `Game.SimEveryTick`, for any
  patch but a postfix; the automation ticks for `LogicCircuitManager.Refresh` and
  `RenderEveryTick` or `Game.SimEveryTick`; leak timing for `WorldDamage.ApplyDamage` and
  `Update`, for any patch but a postfix; see
  [Accurate timing at low FPS](#accurate-timing-at-low-fps)),
  `instant-reachability` (the reachability monitor, the Duplicants' reach table, work cells and
  offsets), `fetch-by-cell` (the fetch groups' update and lists, what makes an item pickable and
  the fetch order; for path costs and probes only its skip of unreachable worlds),
  `fast-table-rows` (the tables' widget lookups and Consumables' scroller refresh),
  `lazy-table-refresh` (the tables' update, sort and row refresh and the columns' dirty marks),
  `fix-table-scroll` (the tables' scroller placing and the layout groups' child placing),
  `fast-serialization` (for the report history only: the serializer's writers),
  `background-save` (for the colony summary only: `RetireColonyUtility.SaveColonySummaryData`),
  `compact-report-entries` (`DailyReport.GetEntry`, `ReportManager.FindReport` and the colony
  summary's constructor, for any patch but a postfix; also while a mod reads past days' entries
  itself, see [Past days' report entries](#past-days-report-entries)),
  `fast-save-type-checks` (for its kept component arrays only: another transpiler of
  `SaveLoadRoot.SaveWithoutTransform`; the type checks stay on),
  and the two rewrites for a transpiler they did not review. The icon is the grey disabled sign.
  A tweak that steps aside stays off until the game restarts.

Where overlaps show:

- **The row.** An affected row gets the icon beside its switch, and its tooltip names each mod
  and what it patches ("Fast Track also patches KAnimBatch.UpdateDirty (it can skip the game's
  code). If something looks wrong, turn this tweak off.").
- **Search.** The search box finds a mod's name, which lists the tweaks it overlaps.
- **The main menu.** While a mod has a high-grade overlap, the main menu shows "N overlaps"
  beside **Moono Tweaks** (N counts each tweak and mod once; the update badge moves right of
  it). Clicking it opens the screen.
- **The log.** `Player.log` gets one block, `[Moono] mod overlaps: ...`, with a line per overlap
  (tweak, method, mod and its Harmony ID, patch kind, grade) and what reading them cost.

A mod that skips a patch when its own option is off leaves nothing to find. One that patches
anyway and checks its option while the game runs is flagged whatever the option says: Fast Track
does this for a few patches whose options share one switch (`WorkOrder.Execute` and the
synchronous `PathProber.Run` under Cache Paths or Fast Reachability, `Game.Update`,
`Global.Update`, the music, `KAnimBatch.UpdateDirty`'s mesh renderer choice) and always patches
the `NavGrid` constructor.

## Update check

With **Update check** on (the default), Moono makes one network request of its own:

- **The request.** In the background after start-up, a GET of
  `https://api.github.com/repos/tigin-backwards/oni-moono/releases` with a
  `User-Agent: OniMoono-update-check` header and nothing else (no cookies, IDs or details of
  your game). It waits at most 8 seconds.
- **Caching.** The answer is kept for 6 hours in `oni-moono/update-check.json` beside the
  settings file, so most launches ask nothing.
- **The badge.** When a newer release for your build is out, a Gassy Moo badge next to **Moono
  Tweaks** on the main menu says so. Hovering it lists what changed in each newer release.
  Clicking it opens the release page in your browser.
- **Which releases.** A normal build looks at normal releases only, a U60 build at the U60
  pre-releases only. Releases marked "Superseded" are skipped.
- **Offline,** or when GitHub doesn't answer, there is no badge and the log has one line about
  it.
- **Off.** With the check off, Moono makes no network requests of its own. A build made without
  a release tag (a developer build) checks nothing.

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

Measurements are in [research/gc-modes.md](research/gc-modes.md). With Fast, the
`post-load-gc` tweak (Saving, below) moves the collection that would pause a large colony about a
minute into play into the load ([research/gc-pauses.md](research/gc-pauses.md)).

The runtime fixes its GC as it starts, before Moono's code runs:

- **Windows:** OniMoono.exe reads the choice itself (`--gc`, else `settings.json`'s `"gc"`) and
  sets the runtime property `System.GC.Server` before starting .NET.
- **Linux and macOS:** the player starts through .NET's own launcher, which reads only
  `OniMoono.runtimeconfig.json`. When the runtime started with the other GC, the player starts
  itself again in place (the same process, `execve`) with `DOTNET_gcServer=0` or `1`, before it
  opens the log.
- A `DOTNET_gcServer` you set yourself wins over the option.
- `Player.log` has a `gc:` line naming the GC in use (Server GC with DATAS, Workstation
  concurrent GC) and why, and a `gc config:` line with the runtime's GC settings.

## When the game crashes

On Windows, when the game closes unexpectedly, a small window says so, with the error from the
crash.

- **Show details** lists what a report needs: Moono's version, the game's build, Windows, the
  GPU and its driver, memory, how long you played, how many mods are on, and the files.
- **Report on GitHub** packs `Player.log` and, if ticked, the crash dump into
  `OniMoono-crash-….zip` beside the log. It opens the crash report form on GitHub in your
  browser with the error, the details and your note filled in. Drag the zip from the window into
  the form's "Crash zip" box, then submit it; the form needs the zip. Nothing is sent unless you
  submit the issue yourself.
- **The dump** can hold bits of what the game had in memory. Untick it to leave it out.
- **The window.** The error and the details can be selected and copied. The window follows
  Windows' dark or light mode and its text size.
- **Turning it off.** `--crash-dialog off` turns the window off. `Player.log` still records the
  crash.

Linux has no crash window.

## Launch options

Launch options still work and win over the screen, which then shows those rows as set by the
launch options:
- `--game-tweak exact`: every tweak that leaves the game as the original plays it: all but the
  Changes gameplay and Bug fixes groups (so without `path-probe-repair`, `instant-reachability`,
  `fix-load-screen-clicks`, `fix-sound-piles` and `fix-table-scroll`, which are on by default).
- `--game-tweak all`: every tweak.
- `--game-tweak none`: no tweaks.
- Tweaks by name: `--game-tweak exact --game-tweak brain-rate`. `--game-tweak -NAME` leaves
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
- `--renderer auto|vulkan|opengl`: the graphics API; auto is Vulkan, or OpenGL when Vulkan can't
  start, the driver is on a short deny list, or an earlier start with this graphics card,
  driver and Moono build never got its first frames on screen; vulkan always tries Vulkan
  ([ARCHITECTURE.md](ARCHITECTURE.md#35-rendering)). If Vulkan stops working in play, the game
  quits with an error; choose OpenGL here or under Renderer in Moono Tweaks if it keeps
  happening.
- `--dxgi on|off`: Windows, frames shown through a Direct3D 11 swap chain or through the
  renderer's own presentation (Vulkan's swapchain, OpenGL's SwapBuffers) ([ARCHITECTURE.md](ARCHITECTURE.md#35-rendering), "Presentation on
  Windows").
- `--stall-repeats on|off`: Windows, with Direct3D presentation (Vulkan or OpenGL): while no
  frame comes (an autosave, a load), the last frame is shown again 30 to 60 times a second, so
  G-Sync and FreeSync monitors keep their refresh rate instead of flickering
  ([ARCHITECTURE.md](ARCHITECTURE.md#35-rendering), "Repeats while no frame comes").
- `--gc fast|low-memory`: the [garbage collection](#garbage-collection).
- `--crash-dialog on|off`: Windows, the [crash window](#when-the-game-crashes).

Arguments for the game itself, a mod's flags for instance, go after `%command%`:
`"…\OniMoono.exe" -- %command% -moddev --my-mod-flag=1`. The game and its mods see everything
after `%command%` as they would in the original player (`Environment.GetCommandLineArgs()`),
and none of Moono's options. Started without Steam, `--game-arg ARG` passes one argument to
the game, and so does any argument that does not start with `--`.

Older setups put `--game-tweak exact` in the launch options. Remove it (the setup prints the new
line) to choose in the game instead.

At start-up the player logs every option it used and where it came from (default, settings file
or command line).

## How "exactly the same" is checked

Each exact tweak has a verify mode (`ONI_TWEAK_VERIFY=1`). The tweak still takes its shortcut,
but the game's own work is done as well and the two are compared, bit for bit where the result
is data. QA's verify stage runs a late-game colony with every tweak on in this mode, every
overlay opened in turn, for at least 10 game minutes, and fails on any mismatch.

## Writing a tweak

A tweak's class (the one with its `Name` constant) declares every game member it relies on with
`[GameMember(...)]`, so a game update that changes one turns the tweak off instead of breaking
the game ([ARCHITECTURE.md](ARCHITECTURE.md#6-configuration-and-paths), "How game tweaks install"):

- each method it patches, by the kind of patch (`Prefix`, `Postfix`, `Transpiler` with its
  `Rewrites`, `Finalizer`; `Replaces` too for a prefix that can skip the method);
- each member or type it reaches by name (`Uses`): `AccessTools.Field(typeof(Grid), "x")`,
  `AccessTools.TypeByName("NavType")`, `Traverse`, `Type.GetMethod("x")` and the like. A name
  written as `nameof(...)` needs one too: the compiler checks only that the name exists, not
  the member's signature;
- each game method its code reimplements without patching it (`Mirrors`, with the method's
  fingerprint for each frozen build), with `[MirrorsGame("signature")]` on that code.

`scripts/check-tweaks.sh` (in check.sh) fails on a lookup or a mirror that is not declared and
prints the line to add. `--check-tweaks review` prints the fingerprints. A lookup whose type or
name is computed is listed as not checked. Once you've checked it, mark its method
`[NameLookupReviewed("why")]`.

## The tweaks

Grouped as on the screen. Names are as used with `--game-tweak`. The gains in the screen's
tooltips are those below, and the rest are in [PERFORMANCE.md](PERFORMANCE.md#the-tweaks).

<!-- tweaks:begin: generated from src/OniMoono.Host/GameTweaks/GameTweakNames.cs; ONI_UPDATE_DOCS=1 with the unit tests rewrites it -->

### General (on by default)

- **Faster Load screen** (`fast-load-screen`): With many saves, the Load screen opens and loads more colonies sooner. Each save's details are read once per refresh instead of four times, several files at a time, and previews already shown are not made again. The colonies, their order, the previews and the log are the same. *Measured: Load screen with 4,637 saves opens in ~0.6 s (was ~1.4 s); U60, 4,637 saves, Linux under WSL2, 2026-11-03.*
- **Moving loading screen** (`loading-animation`): The loading screen keeps moving while a colony loads. While a save loads, the game shows no new picture, for half a minute on a large colony. Moono keeps the loading screen moving, with a progress bar, and plays animated loading art from mods such as Loading Pip. Loading takes as long as before.
- **Sound memory guard** (`fmod-quarantine`): Guards against a bug in the game's sound engine that can crash the game. FMOD, the sound engine, can write to memory it has freed, a bug its makers confirmed and the cause of some "heap corruption" crashes. This gives FMOD separate memory and delays reuse of what it frees, so a late write harms nothing. Player.log notes each one found. Uses about 90 MB more memory on a large colony. Windows only for now.
- **Stolen sound crash guard** (`fmod-steal-fix`): Fixes a bug in the game's sound engine that can crash the game when many sounds play at once. When too many copies of a sound play, FMOD, the sound engine, stops the oldest. If that one is still loading, FMOD frees it and then writes to it. This skips that write by patching FMOD's code in memory, only for the exact FMOD files the game ships. Nothing you hear changes.
- **Forget released sounds** (`fix-released-sounds`): The game stops changing sounds it is done with, which after hours of play could crash it. A bug in the game: background sounds, and looping sounds after a switch to another asteroid, keep using a sound's handle after the game releases it. After a couple of hours on a large colony the sound engine reuses the handle, and the game's change lands on something else, sometimes not a sound at all. This forgets the handle when the sound is released. Nothing you hear changes.
- **Worldgen as in the original** (`exact-worldgen`): A seed makes the same map as in the original game, so shared seeds match. Worldgen does its decimal maths at the precision the original game uses, rounding only where a value is kept. Off: biome borders on a new map can come out slightly different. Maps already generated and saves are not affected.

### Saving (on by default)

All but `post-load-gc` together: autosave freeze ~12 s → ~5 s on a large colony; a late-game Spaced Out colony with every DLC (cycle 17,000+), Linux under WSL2, by 2026-10-20.

- **Faster save writing** (`fast-serialization`): Autosaves take less time; the save file is the same. Writes the colony's numbers without boxing each one in an object.
- **Faster save checks** (`fast-save-type-checks`): Autosaves skip repeating the same checks for every object. Remembers what saving asks about each kind of component instead of asking again for every object.
- **Faster save compression** (`fast-save-compression`): Saving and loading take seconds less; files stay compatible, a little larger. Compresses with .NET's zlib instead of the game's; the files are about 14% larger.
- **Background saving** (`background-save`): The save is compressed and written to disk while the game plays on. Compression and the file write move to another thread; quitting waits for them.
- **No cleanup pause when saving** (`no-save-gc`): Skips the memory cleanup the game forces after each save, which pauses the game. Skips the full garbage collection the game asks for after saving.
- **Memory cleanup during loading** (`post-load-gc`): Collects garbage during the loading screen, avoiding a large hitch shortly after the game starts. The memory cleanup that would pause the game about a minute into play runs at the end of the load instead, while the game is still paused. Only with Fast garbage collection (the default). *Measured: no ~0.3 s freeze a minute after loading a large colony; loading ~0.2 s longer; a late-game Spaced Out colony with every DLC (cycle 17,000+), Windows, 2026-11-07.*

### Simulation (on by default)

- **Faster worker threads** (`fast-jobs`): Less time lost waking idle worker threads every frame. Wakes only as many of the game's worker threads as a job has work for, instead of all of them for every job (about 14 a frame). *Measured: +11% frame rate on a large colony; a late-game Spaced Out colony with every DLC (cycle 17,000+), Linux under WSL2, by 2026-10-20.*
- **Faster sublimation** (`fast-sublimation`): Items that can't sublimate (polluted dirt in full gas) are skipped. Items already blocked by full gas cells are skipped in one batched update.
- **Faster radbolt collisions** (`fast-particle-collision`): Radbolts look for each other only where one can be. A radbolt skips searching every stored item nearby when no other radbolt is close.
- **Faster pickup checks** (`fast-offset-tables`): Where an item can be picked up from is rechecked only when its surroundings change. Fetch updates recheck only the items whose work spots can have changed.
- **Faster spatial index cleanup** (`fast-partitioner-cleanup`): The once-a-second tidy-up of what is where looks only at what was removed. The spatial index's cleanup visits only the entries that were removed.
- **Faster item moves on the map** (`fast-partitioner-events`): An item changing tile no longer looks through every item near it. When an item moves or is stored, the game looks through every item nearby for listeners on that tile. Items have no listeners, so this skips the search and keeps only its cleanup of removed entries.
- **Faster pipe and rail updates** (`fast-conduit-sorts`): Pipes and rails no longer re-sort every building's update each second. Each second the game sorts the list of pipe and rail buildings by priority again; this repeats the reordering its last sort made while the priorities are the same, which gives the same order.
- **Faster conveyor rebuilds** (`fast-conveyor-paths`): Building or removing conveyor rails no longer stalls the game. The rails' network rebuild finds its routes from an index instead of searching every kept route again. *Measured: no hitch on rail edits (was 0.05–0.5 s); a late-game Spaced Out colony with every DLC (cycle 17,000+), Linux under WSL2, 2026-10-25.*
- **Faster pipe network rebuilds** (`fast-pipe-rebuilds`): Building or removing pipes no longer makes every pipe in the colony check its network. After a gas or liquid network changes, only the pipes of that kind recheck whether their network has a building taking from it, all through one handler instead of one or two for every pipe. The status items and the Pipe Flow arrows come out the same. *Measured: each pipe network rebuild ~25 ms shorter on a colony with 27,000 pipes (~57 → ~32 ms); a late-game base-game colony with 27,000 pipes, Linux under WSL2, 2026-11-10.*
- **Compact navigation links** (`compact-nav-links`): The map of where Duplicants and critters can move takes a fraction of the memory. Each pathfinding grid keeps a cell's moves packed together instead of a fixed row of slots for every cell of the map; the pathfinder reads the same moves in the same order.
- **Faster state machine lookups** (`fast-smi-lookups`): Finding one of an object's state machines no longer searches all of them each time. Each object remembers which of its state machines answered each lookup until its list of state machines changes.
- **Faster conveyor item moves** (`fast-save-roots`): Items moving between rails and buildings no longer search every saved item of their kind. When an item is stored, the game takes it out of its list of things to save by comparing it with every item of its kind; this compares references instead, which finds the same entry.
- **Paged path grids** (`paged-path-grids`): Each Duplicant's map of path costs takes memory only where it can walk. A Duplicant's path grid covers the whole map. This keeps it in small pages, each made the first time a path probe writes there. The pathfinder reads the same values.
- **Share identical data** (`shared-duplicates`): Names repeated across a colony's reports and other identical data are kept once, using less memory. While a save loads, copies of the same text (Duplicants' and critters' names in each day's report) become one. Buildings with the same footprint share one table of work spots, and work animations left at their defaults share one list. The game never writes to these, so it reads the same values. *Measured: ~0.3 GB less memory on a late-game colony; a late-game Spaced Out colony with every DLC (cycle 17,000+), Linux under WSL2, 2026-11-09.*
- **Compact report history** (`compact-report-history`): Past days' reports take a fraction of the memory. The notes of every day's report but today's are kept in flat arrays instead of a table for each entry; the reports screen and saves read the same notes in the same order. *Measured: ~0.9 GB less memory on a late-game colony; loading ~1 s faster; a late-game Spaced Out colony with every DLC (cycle 17,000+), Linux under WSL2, 2026-11-10.*
- **Compact report entries** (`compact-report-entries`): Past days' report entries take less memory and fewer objects for the game's memory cleanups. The entries of every day's report but today's are kept in flat arrays instead of an object each, and made again when something reads that day. The reports screen, the colony summary and saves read the same values in the same order. Off while a mod reads past days' entries itself. *Measured: 4.2 M fewer objects and 7.9 M fewer references for a full memory cleanup to trace on a late-game colony; its pause an estimated ~12% shorter (from a cost model, not timed); a late-game Spaced Out colony with every DLC (cycle 17,000+), Linux under WSL2, 2026-11-17.*
- **Fetch checks by pile** (`fetch-by-cell`): Duplicants looking for things to fetch check each pile of identical items once, not item by item. Tracks the best item of each pile as items move, are reserved or spoil, and finds the path to each pile once. Items on worlds a Duplicant can't reach are skipped. Among items in a pile that the game ranks as equal, a Duplicant may take a different one than in the original. Off while a mod changes how Duplicants choose what to fetch. *Measured: fetch updates ~8× shorter on a colony with big piles (3.1–3.4 → 0.4 ms each); a late-game Spaced Out colony with 33,600 fetchable items, Windows, 2026-11-08.* *Off by default.*
- **Direct conveyor bridge moves** (`direct-rail-bridges`): Conveyor bridges pass an item straight across instead of taking it off the rail and putting it back. The item only changes rail tile. What the rails carry and when, heat, germs, freshness, egg state and Conveyor Meter counts are the same. Two things differ: the Conveyor overlay may draw two overlapping items in the other order, and an item is no longer listed as fetchable for the instant it crosses. Undiscovered kinds of item, critters and items a mod has added components to cross the game's way. Off while another mod changes how items are stored on rails. *On by default; left out of `exact`.*

### Graphics & overlays (on by default)

- **Update only the table rows in sight** (`lazy-table-refresh`): Consumables, Vitals, Priorities and the other tables update only the rows on screen. The game updates every row of a table in one frame, once a second and each time it opens, which stutters with many Duplicants. Rows out of sight are updated just before they scroll into view instead, so they show values read at that moment. Off while a mod changes how tables update. *On by default; left out of `exact`.*
- **Tables open sooner** (`fast-table-rows`): Consumables, Vitals and Priorities open much sooner on colonies with many Duplicants. For each cell of a table, the game searches every row and column built so far, and Consumables also goes through every row's scroll bar. This notes a row's cells as the row is built and sets the scroll bars once a row. The table comes out the same.
- **Faster animation events** (`fast-anim-events`): Animations check their sound and effect cues with less work. Keeps a compact copy of what each playing animation's per-frame event check reads.
- **Faster animation cleanup** (`fast-anim-cleanup`): Removed animations are swept out only where one was removed. The sweep for destroyed animations skips grid chunks that can't hold any.
- **Parallel animation uploads** (`fast-anim-batches`): Animation data for the graphics card is prepared on several threads. Animation batches write their instance and symbol data on worker threads, the rest in the game's order.
- **Fewer animation uploads** (`fast-symbol-overrides`): Swapped animation parts are re-sent to the graphics card only when they change. Unchanged symbol-override blocks of Duplicants and buildings aren't uploaded again.
- **Trimmed animation lists** (`trim-anim-lists`): The game's animation data takes about 65 MB less memory. Once the animations are loaded, the lists of their frames and frame parts give back the room they grew into but don't use; the game reads the same data.
- **Smoother decor overlay** (`fast-overlay-removals`): The Decor overlay no longer stutters when items are stored or used up. While the Decor overlay is open, the game takes each stored or removed item out of the overlay's index by comparing it with every entry; this compares references instead, which finds the same entries.

### Changes gameplay (off by default but where marked)

- **Incremental path probes** (`path-probe-repair`): Duplicants' path checks redo only what changed in the world, using less CPU. Each Duplicant's last path probe is repaired around what changed (digging, building, doors, liquids) instead of redone from scratch. Costs and reachable cells stay the same, but where two routes cost the same a Duplicant may walk a different one than in the original. Uses about 0.9 MB per Duplicant. Off while another mod changes path probing (Fast Track's Cache Paths). *On by default.*
- **Instant reachability checks** (`instant-reachability`): Duplicants notice a newly opened or blocked path at once, using less CPU. Errands and items are rechecked where and when a Duplicant's reach changes, instead of all of them once a second. The answers are the same, up to 1 s sooner. Items it can't track (from some mods) are still checked every second. *On by default.*
- **Lazy unseen conveyor items** (`off-screen-conveyors`): Items riding rails off screen are placed only when they stop or come into view. The rails carry exactly the same things, but items' positions lag while off screen.
- **Simulation at its own pace** (`sim-step-pace`): Above 60 fps at normal speed, frames no longer wait for the simulation, and it uses less CPU. Above 60 fps at normal speed, the game calls the simulation on every frame between its five steps a second, and each call waits for the step underway. This skips those calls, as the game does below 60 fps and at faster speeds. Dug tiles, built tiles and other changes the game asks of the simulation then show about 0.3 s later (up to 0.4 s), as below 60 fps. Off while paused, or while another mod changes these calls. *Measured: 112 → 220 fps at normal speed on a large modded colony; a large modded Spaced Out colony, Windows, 2026-11-12.*
- **Accurate timing at low FPS** (`low-fps-timing`): At low frame rates or high speeds, animations, timers and automation keep pace with the simulation. On long frames and at high game speeds, some game timing drifts from the simulation: automation gates and one-tick signals, scalding and overheating damage, work that waits on an animation, and similar. This keeps it in step with the simulation, as at a high frame rate, while the camera and menus keep real time. Each part turns off if a mod changes the code it relies on.
- **Cap critter and dupe brain updates** (`brain-rate`): Above 30 fps, critters and Duplicants decide what to do only as often as at 30 fps. The game updates 1 Duplicant and 5 critter brains every frame; this caps them at 30 and 150 a second. One that just finished an errand or is under threat still gets the next update, and a critter ready to inhale food, excrete or attack goes ahead of the others, so critters act at other moments than without this. With many critters or Duplicants the cap rises with game speed, so each is updated within 15 seconds of game time.

### Bug fixes (off by default but where marked)

- **Critters ready to eat, excrete or attack get the next turn** (`critter-needs-first`): In big colonies at low frame rates, Slicksters and Pufts keep excreting and Beetas still attack. The game lets 5 critters decide what to do each frame, in a fixed order. With thousands of critters at a low frame rate, one that inhales its food gets too few turns to stay fed, so it never excretes: its stomach fills for cycles and empties all at once after a load. A critter also notices a target only on its turn, so at speed 3 Beetas and other critters that attack often miss what passes by. This gives a critter ready to inhale or excrete one of the frame's turns ahead of the others, and a critter with a target in reach a turn as often as at normal speed and 30 fps. No turns are added, so other critters wait slightly longer.
- **Land items lost below a world** (`off-world-fallers`): Items that fall out the bottom of a world (a rocket's chute) stop falling forever. A bug in the game: they hang just below the world, stepped as falling every tick. This lands them where they hang.
- **Open a colony once on the Load screen** (`fix-load-screen-clicks`): After Load more, a click on a colony no longer builds its list of saves again for every refresh. A bug in the game: each Load more adds another copy of the click to the colonies already shown, so one click builds the colony once for every refresh. This builds it once and logs once; the screen ends up the same. *On by default.*
- **Cap repeated sounds in one tile** (`fix-sound-piles`): Hundreds of critters in one tile no longer freeze the game with their sounds. When more than 3 copies of the same sound start in the same tile in the same frame, the rest are skipped. A big pile of critters sounds quieter; anywhere else this almost never happens. UI and looping sounds are never skipped. *On by default.*
- **Correct daily colony snapshots** (`fix-timelapse-snapshots`): Colony snapshots saved each cycle no longer get vertical stripes, squeezed lights or stray effects. A bug in the game: the build grid, the lights and the heat, cold and dust effects are set up for the screen's view, which is wider than a snapshot's. With a building picked for placement, the grid draws stripes over the left of the image. This sets them up for the snapshot's view. *On by default.*
- **Keep table columns where they were scrolled** (`fix-table-scroll`): Scrolled table columns no longer jump back to the first ones every second or so. A bug in the game: each row of a table keeps its scroll position in step with the others. When the rows are laid out again (every second or so while the columns move), one row's columns go back to the start and the game moves every row there. This keeps the table's scroll position when a row is laid out. *On by default.*
<!-- tweaks:end -->

### Incremental path probes

On by default, though it changes gameplay in one way (it is in the Changes gameplay group and
not in `exact`). Costs and reachable cells are the original's, but where two routes cost the
same a Duplicant may walk another one than in the original (Navigator builds the walking path
from the probe, PathGrid.BuildPath). It keeps about 0.9 MB a Duplicant (four bits of suit flags
a state, on a 640×404 world). It steps aside while Fast Track's Cache Paths is on (below).

**In the game.** Each Duplicant's path probe is the cost to every cell it can reach, which
errands and pathing read until its next probe. It is redone every few seconds, four Duplicants a
frame, on a separate thread.

**With `path-probe-repair`,** a probe from the same cell, nav type, suit flags and abilities as
the Duplicant's last one repairs that one instead:

- What changed since is found through the game's own writers: the nav grid's links, doors and
  their permissions, liquid and solid cells, tube entrances, suit checkpoints.
- The cells whose route changed or got worse are dropped with the cells reached through them,
  and found again.
- Cheaper routes spread as in the game's search.

Any other probe is the game's search. So is a repair that would redo half of what the last probe
reached (a door by the Duplicant's spot), because a search costs less then. The probes are not
delayed, and temperature and building checks are untouched.

**What differs from the original.** Where two routes to a cell cost the same, the game keeps the
one its search finds first, the repair the one it had or finds first. A Duplicant can then take
the other route of the same cost. The route it keeps can also change what lies beyond that cell:

- When the two routes carry different suit flags (one passes a suit checkpoint), the cells past
  it may cost more or less, or be reachable or not, depending on the flags.
- A step back the way the route came is never searched from a cell, so with the other route a
  cell next to it can be found at another cost.

Such a difference is what the game's own search gives with another search order, and lasts until
the Duplicant's next full search (when it moves).

**Verify.** With `ONI_TWEAK_VERIFY=1` every repair is checked against a new search. One that
answers otherwise is checked to be a result the game's search can give (each cell set from its
route, none reachable cheaper), and only a repair that is not counts as wrong. Every search is
also checked against the game's own on the same Duplicant. In a 200-Duplicant colony and a
late-game base-game colony (U60) none of about 10,700 repairs and 180 searches checked gave
another cost or reachable cell.

**Other mods.** It stands down for the session, logged once in Player.log
(`path-probe-repair: off (...)`), when another mod patches the probe or the methods it relies
on:

- Fast Track's **Cache Paths** does. Turn off Cache Paths to use this.
- Fast Track's **Background Pathing** only wraps two PathGrid methods in locks. The tweak checks
  that they answer as the game's and runs alongside it.
- With Fast Track's Fast Reachability on and its next chore priority set to Delay, Fast Track
  also patches the probe's work order, and the tweak stands down.

### Instant reachability

On by default, though it changes timing (it is in the Changes gameplay group and not in `exact`):
the answers are the original's, but they come sooner.

**In the game.** Once a game second, the game rechecks whether a Duplicant can reach each errand
and item it tracks (tens of thousands in a large colony, most of them items in storage). It
compares the object's cell and work spots with the cells the Duplicants can reach. A change,
such as a door locked or a tile dug, is seen up to a second later, at each one's next check.

**With `instant-reachability`** an answer is rechecked only when something it reads changes:

- A cell becomes reachable or unreachable for every Duplicant (found where the game updates its
  count of Duplicants reaching each cell).
- The object moves.
- Its work spots change.

The recheck is the game's own, on the frame of the change. So the reachable tag, the status
items and the fetch lists change as they do in the original, up to a second sooner. An object
whose cell or work spots come from code the tweak does not know (some mods' own) keeps the
game's once-a-second check.

**Verify.** With `ONI_TWEAK_VERIFY=1` every answer is compared with the counts every few frames
(`instant-reachability: ... N checks, M mismatches` in Player.log every 30 s).

**Other mods.** It stands down for the session, logged once (`instant-reachability: off (...)`),
when another mod patches the monitor, the counts or how an object's cell and work spots are
read. Fast Track's **Fast Reachability** does, and replaces the same check its own way.

### Fetch checks by pile

Off by default, as it can change which of two identical items a Duplicant takes (it is left out
of `exact`).

**In the game.** Each time a Duplicant's brain updates (about 30 times a second in all), the
game rechecks every item that can be fetched: whether this Duplicant may pick it up, the path
cost of its cell, then a sort that keeps the best item of each kind and priority. A late-game
colony can hold tens of thousands of such items in a few thousand piles (one tested colony:
33,600 items in 3,600 cells), so most of that work repeats the same answer for every item in a
pile.

**With `fetch-by-cell`** a pile is the items of one kind, tags and priority in one cell:

- Each pile keeps its best item by the order the game uses within a cell (food quality, then
  freshness), updated as items are added, removed, moved, stored, retagged, reserved or spoil.
- An update checks that best item as the game would, finds each pile's path cost once, keeps
  the cheapest pile of each kind and priority, and writes the result in the game's order.
- Items whose answer can differ from one Duplicant to the next (reserved ones, those in conveyor
  loaders and receptacles) are checked one by one.
- A pile whose best item fails its check falls back to checking all of its items.
- A cell whose items take their work spots from different places (a bottle filler's stored
  water beside a loose bottle) is costed as the game costs it, through the first item there in
  the game's list that the Duplicant may pick up.

Piles on worlds the Duplicant can't reach are skipped. A pile at least a few cells inside a world
(by the largest work-spot offset any item has) on which the Duplicant's last path probe reached
no cell has no path for any item in it, so the game would drop it too.

**What differs.** The result is the game's except for ties. Where two items of one kind have the
same tags, priority, path cost, food quality and freshness, the game's sort picks either of them
and this may pick the other. The merged list the Duplicant's errands read keeps the same order.

**Other mods.** It stands down for the session, logged once (`fetch-by-cell: off (...)`), when
another mod patches the update or what it reads:

- Efficient Supply, CycleTrim and Fast Track's **Optimize Debris Collection** replace the
  update.
- Waste Not, Want Not and Delivery Temperature Limit change its order.
- No Manual Delivery, Forbid Items, Controlled Storage and others change who may pick up an
  item.

The world skip alone stands down while a mod changes path costs or probes (Teleport Suit,
GRAVNet, Fast Track's path cache). With Fast Track's **Reduce Debris Checks** the game's update
never runs, so neither does this.

With `ONI_VERIFY_FETCH_BY_CELL=1` (or `ONI_TWEAK_VERIFY=1`) the game's update runs and is used,
this one runs beside it, and Player.log reports every 30 s
(`fetch-by-cell: N updates; N group results, N ties, 0 differed; ...; N resyncs, 0 mismatches`)
with a line for each result that differs other than by a tie, for each item in a skipped cell
that had a path, and for each difference found when the mirror is checked against the game's
lists (every 5 s in this mode; in play only after a change it tracks came from another thread).

### Pipe network rebuilds

U60 only (the game's code it changes is new in U60). After each rebuild of the gas or the liquid
pipe networks, the game tells every pipe in the colony, gas and liquid alike: each pipe segment
looks up its own network and adds or removes "Pipe Network Requires Building Intake" (a network
with no building taking from it), and each pipe with Pipe Flow arrows checks whether its overlay
is open. That is one or two handlers for every pipe on each rebuild, and a rebuild comes with
every pipe or pipe building built, removed, broken or repaired.

With `fast-pipe-rebuilds`, the pipes register with one handler instead, which runs the game's own
check (`Conduit.RefreshInvalidNetworkStatusItem`) for the pipes of the rebuilt kind, for the
other kind's pipes while their networks wait for a rebuild of their own, and for pipes not checked
since they were built; and the arrows' update for the arrows of the rebuilt kind while its Pipe
Flow overlay is open, as their own handler does. A pipe of the other kind it skips would come out
of the check unchanged: its networks change only in their own rebuild, after which every pipe of
that kind is checked, or through a removal that marks them for one. A mod's handler on the same
event still gets it. A pipe whose handler is a mod's override keeps its own subscription, and
once a mod patches either handler, the check, the flow's rebuild trigger or the network
managers' update or removal, every pipe's handler runs on every rebuild until the game restarts.

With `ONI_VERIFY_PIPE_NETWORK_STATUS=1` (or `ONI_TWEAK_VERIFY=1`), after every rebuild each
pipe's status items are checked against the game's check run as a shadow (would it add or remove
an item now?), each arrow handler skipped against its own condition, and every 64th rebuild the
registered pipes against the colony's; Player.log reports
`fast-pipe-rebuilds: N rebuilds, N of N pipe refreshes made, N of N flow-arrow handlers run, 0 mismatches`
every 200 rebuilds or 30 s, and a line for each mismatch.

### Conveyor bridge moves

When a conveyor bridge (or a conveyor meter, which is one) passes a whole item on, the game takes
the item off the rail and puts it back: the item is told it left storage and, in the same call,
that it is stored again. In between it loses and regains its Stored tags, gets a faller, a place
in its tile's item list, partitioner entries and a place in the save list, and loses them all
again. On a late-game colony that is about 50 µs for each item a bridge moves.

`direct-rail-bridges` (Simulation) is on by default but left out of `exact`, as the rails'
internal handles and the world inventory's empty lists differ (below). With it the item only
changes rail tile, as it does between two plain rail tiles. The tweak does by hand the few
things the round trip leaves behind:

- The tile the item is filed under.
- The event that weight plates and other sensors of that tile listen for.
- The conveyor overlay's look.
- For an egg, its incubation monitor's two notices.

A split item (a meter reaching its limit) is moved by the game's code.

What is the same: what the rails carry and when, masses, temperatures and heat exchange, germs,
freshness, eggs (an egg at 100% still starts hatching at a bridge, as in the game), the meter's
count, and the save file.

What differs:

- An item keeps its place in the rails' internal list instead of going to its end. With the
  Conveyor overlay opened or closed while two rail items overlap on screen, they may be drawn in
  the other order.
- An item Duplicants can reach is, in the game, listed as fetchable for the instant of the
  crossing. Nothing can fetch it in that instant, but the world inventory keeps empty lists for
  its tags because of it. With the tweak those lists are made when an item of that kind is
  first really fetchable. The amounts shown are the same.
- Internal handles (the fetch manager's, the partitioner's, the item's status-item order) differ.

What still crosses the game's way:

- A kind of item the colony has not discovered yet (the crossing can be what discovers it).
- Critters.
- Anything with a storage of its own.
- Items a mod has added a component or state machine to.
- Any item not in the state the rails leave items in.

The tweak turns itself off while another mod patches the bridge's update, the rails' add or
remove, or the item's and the egg's store handlers.

**Verify.** With `ONI_TWEAK_VERIFY=1` every other eligible move is made by the game's round
trip. The item after both kinds of move is compared with what the audit says the round trip
leaves (tags, storage, parent, position, rotation, filed tile, mass, temperature, germs, sim
chunk, overlay look, selectable, save list, listeners, faller, reservations, status items,
every state machine's state, freshness, the egg's amounts, discovered kinds, the rail's
contents and handle). On a late-game colony, 150 s at speed 3: 12,928 direct moves and 12,928
of the game's round trips compared, 0 wrong.

**Measured.** On that colony (Linux, speed 3) the bridges' update went from 2.56 to 1.46 ms a
game second and the rails' whole step from 5.72 to 4.35 ms.

The audit, row by row, is [research/rail-bridge-moves.md](research/rail-bridge-moves.md).

### Simulation at its own pace

Off by default (`sim-step-pace`, in the Changes gameplay group, not in `exact`).

**In the game.** The game steps its simulation five times a second, each step 0.2 s of game
time (`Game.SimEveryTick`). On a frame with no game time to step, which at normal speed happens
only above 60 fps, it also calls the simulation with no time (`UnsafeSim200ms(0)`). That call
reads the simulation's state back, so the frame waits for the step still running (a median
25 ms on a large modded colony on Windows). It also runs another pass of the simulation, about
10 ms of CPU each.

**With the tweak** those calls are left out, so the simulation is called only at its steps, as
the game already does below 60 fps and at speeds 2 and 3. Sub-ticks, errands and the steps
themselves run as in the game.

**What changes,** above 60 fps at normal speed: a change the game asks of the simulation (a dug
tile, a built tile, a door) shows when the next step's results come back. That is about 0.3 s
later than without the tweak (median 27 → 310 ms for a dug tile, at most about 0.4 s), as it is
below 60 fps. Paused, the game's calls still run, so tools used while paused show at once.

**Other mods.** While another mod patches `Game.SimEveryTick` or `Game.UnsafeSim200ms`, the
game's code runs (`sim-step-pace: off (...)` in Player.log).

**Measured** on Windows on a large modded colony at 1×, off against on in alternating segments
of one run: 112 → 220 fps, frame times p50 7.4 → 3.9 ms and p99 34 → 12 ms, the sim's CPU about
160 → 38 ms a step.

### Accurate timing at low FPS

Off by default (`low-fps-timing`, in the Changes gameplay group, not in `exact`).

On long frames or at high game speeds, parts of the game run on the frame's time or real time
instead of the simulation's. This tweak holds the fixes for that, in three parts so far: the game
time cap, automation ticks and leak timing.
Each part checks for other mods on its own, so a mod that patches what one part relies on turns
off that part only, and the other stays on. Later fixes of this kind join this tweak as parts.
The cases are in [research/frame-rate-dependence.md](research/frame-rate-dependence.md).

#### The game time cap

**In the game.** `Game.SimEveryTick` gives the simulation at most 0.2 s of game time a frame
(its sub-ticks of 1/60 s; less than one is kept for the next frame) and drops the rest. The
frame's game time, `Time.deltaTime`, is the real frame time capped at 1/3 s and multiplied by
the speed: up to 1 s a frame at speed 3. Animations, the automation clock, `Time.time` and what
the game updates every frame advance by all of it, so on long frames they run up to 5 times as
fast as the simulation:

- Duplicants and critters finish what waits on an animation sooner (work, eating, jumps, ledge
  climbs), so they get more done per game second.
- Buffer and Filter Gate delays, and Limit Valve resets, last less game time.
- Timers on `Time.time` fire sooner: overheat damage (every 7.5 s), scald and cold damage
  (every 5 s), slimelung coughs, the 4 s window of temperature checks (below 3.75 fps at speed 3,
  Duplicants are then never Too Hot or Too Cold), Athletics from moves, algae dissolving.

**With this part** each frame's game time is capped at the simulation's 0.2 s while a colony
runs, so everything advances by the game time the simulation does. It acts only on frames
longer than that: below 15 fps at speed 3, 10 fps at speed 2 and 5 fps at speed 1. Above those
the game time is the same, bit for bit. `Time.time`, `smoothDeltaTime`, `timeSinceLevelLoad`
and the `FixedUpdate` steps follow the cap. `unscaledDeltaTime` and the real-time clocks stay
real, so the camera, menus and input are unchanged. The main menu and loading screens have no
cap. The cases are in [research/frame-rate-dependence.md](research/frame-rate-dependence.md)
(cases 1-8, 11 and 18).

**Other mods.** While another mod patches `Game.SimEveryTick` other than with a postfix (it could
change the simulation's cap), this part is off and the game's timing stays
(`low-fps-timing: game time cap off (...)` in Player.log). The automation part stays on unless
it steps aside too.

**Measured** with this part on alone, on a Linux lane with a new colony
(`tests/e2e/frame-rate-probe.yaml`, `tools/FrameRateProbeTestMod`), 40 s of game time at speed 1 at about 220 fps (the target),
then about 51 s at speed 3 capped at 5 fps (`--max-frame-rate 5`), 2026-11-18. Counts, not
frame rates:

| Speed 3 at 5 fps, per game second the simulation ran | Without | With | Speed 1, 220 fps |
| --- | --- | --- | --- |
| `Time.time` advanced | 3.0 s | 1.0 s | 1.0 s |
| Automation ticks | 30 | 10 | 10 |
| A 10 s Buffer Gate's delay (100 ticks), in game time | 3.3 s | 10.0 s | 10.0 s |
| Overheat hits (Overheatable's rule) | 0.23 | 0.12 | 0.12 |
| Scald hits (ScaldingMonitor's rule) | 0.49 | 0.18 | 0.17 |
| A 1 s animation played once, again on its end (game time a play) | 0.41 s | 1.20 s | 0.97 s |

With this part, a step that waits on an animation still ends on a frame boundary, a frame after
its last frame (case 9 of the study): at 0.2 s a frame, a 1 s animation takes 1.2 s, as it does
at speed 1 and 5 fps without it (1.12-1.19 s).

At speed 3 and 60 fps, and at 20 fps (0.15 s of game time a frame), no frame was capped.

#### Automation ticks

**In the game.** Automation ticks every 0.1 s of game time (`LogicCircuitManager.Refresh`, from
`Game.Update`), all of a frame's ticks before the frame's simulation sub-ticks. A frame of more
than 0.1 s of game time (below 30 fps at speed 3, 20 at speed 2, 10 at speed 1) holds two ticks
or more, run back to back. A signal that lasts one tick then turns on and off with no sub-tick in
between: buildings that react to the signal's change see both edges, but a pump, door or valve
switched on for one tick does nothing in it. At speed 3 that hides a third of one-tick signals
at 20 fps, half at 15 fps and two thirds at 10 fps.

**With this part** a frame's first tick runs where the game runs it, and each later one just
before the sub-tick it falls on, so a sub-tick follows each tick. The ticks are the game's own,
counted by its loop: as many, with the same clock left over. Frames with one tick, or one sub-tick
at most, run exactly as the game's. Network rebuilds, bridge lists and overload checks stay where
they are. It shares the simulation's loop with `sim-step-pace`; either can be on alone. Without
the game time cap, a long frame at speed 3 holds up to 10 ticks over its 12 sub-ticks, still a
sub-tick after each; with it, at most 2.

**Other mods.** While another mod patches `LogicCircuitManager.Refresh` or `RenderEveryTick`, or
`Game.SimEveryTick`, this part is off and the game's code runs
(`low-fps-timing: automation ticks off (...)` in Player.log). The game time cap stays on unless
it steps aside too.

**Measured** in the same runs, with this part on alone and with both: the share of automation
ticks followed by no sub-tick before the next one (a one-tick signal the simulation never sees),
2026-11-18.

| Speed 3 | Without | With |
| --- | --- | --- |
| 20 fps | 168 of 504 (33%) | 0 of 504 |
| 5 fps | 1,281 of 1,537 (83%) | 0 of 1,536 |
| 5 fps with the game time cap | 256 of 512 (50%) | 0 of 512 |
| 60 fps | — | 0 of 502, no tick moved |

Speed 1 at about 220 fps, the target, hid 2 of 402 ticks (on a hitch, a frame of over 0.1 s).
The plan costs about 70 ns a frame in the worst case (speed 3 at 5 fps, 6 ticks over 12
sub-ticks) and allocates nothing (`LogicTicksTests`); it moves the game's work, adding none.

#### Leak timing

**In the game.** A solid tile damaged past 15% with more than 1 kg of liquid behind it leaks 1 kg
into the open cell on its other side, at most once a second per cell
(`WorldDamage.ApplyDamage` and `Update`). That second is real time (`realtimeSinceStartup`), so
at speed 3 a damaged tile leaks a third as often per game second as at speed 1, and less again
with mods that raise the game speed. It matters only while a tile takes damage more often than
once a second, as when liquid pressure cracks it (digging leaks nothing).

**With this part** the second is game time, read from the game clock, so a tile leaks at most
once a game second at any speed: at speed 1 as in the game. While paused, nothing damages tiles
and the window waits with the game clock, so paused frames are unchanged.

**Other mods.** While another mod patches `WorldDamage.ApplyDamage` or `Update` other than with
a postfix, this part is off and the game's real-time window stays
(`low-fps-timing: leak timing off (...)` in Player.log). The other parts stay on.

### Critter turns

Off by default (`critter-needs-first`, in the Bug fixes group, not in `exact`). `brain-rate`
installs the same hooks for its critter cap.

**In the game.** Critters take turns deciding what to do, 5 a frame in a fixed order, so with
N critters each gets a turn every N / 5 frames: with 500 critters every 3.3 game seconds at
normal speed and 30 fps, but every 10 at speed 3 and every 20 at speed 3 below 15 fps. Two
things need turns more often than that:

- A critter that inhales its food (Slicksters, Pufts) starts one meal a turn. With thousands
  of critters at a low frame rate it stays hungry, so it never excretes.
- A critter that attacks on its own (Beetas, Pokeshells guarding their eggs, critters that
  hunt) looks for a target only on its turn. By the time it notices one, waits its 0–4 s and
  sets off, a Duplicant or critter passing by has gone.

**With the tweak,** such a critter is put in the game's own queue for the next turn, which the
game serves before the fixed order. No turn is added; the others wait that much longer.

- Ready to inhale or excrete: when its wait after a meal ends and it is hungry, and when it can
  first excrete.
- Ready to attack: a critter looking for a target with one it could take in its search box
  (41×41 cells, the game's own tests), or one under threat, once its last turn is older than
  it would be at normal speed and 30 fps (critters / 150 game seconds). Where turns come that
  often anyway, none is asked for.

At most 4 such critters are tested a frame, and at most one is asked for in every 5 of the
critter group's turns, so the fixed order keeps at least 4 in 5 of them. A test reads the
partitioner and the path cache, up to 50 entries, and allocates nothing.

The critters it follows belong to one colony: each new game forgets them before its critters
start. Leaving a colony frees the critters' state machines without them leaving their states,
so none stops being followed then. Until 2026-11-19 they were kept after loading another
colony, and the first test of one threw every frame (#93). A critter whose test throws is now
dropped.

**Measured** on Linux with 12 Beetas, 500 other critters and a Pip that visits the Beetas for 8
game seconds every 30–40 (`tests/e2e/critter-threat-turns.yaml`), counts only:

- Speed 3, frames held to 15 a second (a turn every 20 game seconds): 2 stings (1 during a
  visit) without the tweak, 19 with it, from 97 turns asked in 600 game seconds.
- Normal speed at 28 fps (a turn every 3.5–3.7 game seconds): 33 stings without, 23 with; the
  tweak asked 13 turns in 450 game seconds, as the round was just over its gap.

In both, nearly every sting came in the Pip's first three visits: in this test later visits
rarely end in one, with or without the tweak.

### Items lost below a world

Off by default (`off-world-fallers`, in the Bug fixes group, not in `exact`).

The game steps every falling object thirty times a game second
(`GravityComponents.FixedUpdate`). An object that may not leave its world is left where it is
when its next position would be outside that world, and stays falling. So an item dropped
through a hole in the bottom of a world (a chute out of a rocket interior) hangs half a cell
below the grid's bottom row and is stepped forever. A colony with a chute like that collects
them: 17,539 on a pile-heavy colony, where the step is 4.4 ms of a 35 ms frame.

The tweak lands such an item where it hangs, through the game's own landing for items, once a
step has left it in place and no later step could move it. That is when:

- It falls straight down.
- The cell under it is below the grid (so no liquid can slow it).
- At half its speed its next position would still be below the grid.

Then it no longer counts as falling. What that changes and what it doesn't:

- Nothing on screen changes, and neither does the save. Gravity is not saved, so after a load
  the items fall again until their first step lands them.
- No Landed event is sent, as none would be in the game.
- If the rocket is taken apart and its items are moved to the world below, a landed item falls
  from rest instead of at the speed it had.
- An item that hangs with a grid cell still under it is not landed: below an interior that is
  not on the grid's bottom rows, or stopped high enough in the bottom row.

The game's step itself is not patched. Until 2026-11-14 the tweak was a transpiler of it, and a
Harmony-patched copy of that loop runs about half as slow again for every falling object
(1.79 against 1.47 ms a call for 17,566 objects on that colony, Windows). The tweak now runs
around the step, from the scheduler's call that holds it (`KComponentSpawn.Sim33ms`; the
gravity step is the only work in it):

- **Before the step** it notes the falling items within 16 cells of the grid's bottom row,
  with their positions. Every falling item is looked at about twice a game second (sooner when
  one not noted could have fallen into reach), and the ones that just started falling at each
  step.
- **After the step** it tests the noted items the step reached and did not move, and lands
  the ones that hang for good, in the step's order.

These are the items the transpiler landed, after the same step and in the same order (the unit
tests run both against a copy of the game's step). Two cases land later than they did:

- An item that starts falling during a step already where it hangs (from another object's
  landing): by one step.
- An item that something else moves to where it hangs: by up to half a game second.

**Other mods.** While another mod patches the gravity step with anything but a postfix, the
tweak is off for the session (`off-world-fallers: off (...)` in Player.log), because its tests
mirror the game's step. The transpiler ran alongside such patches. Bubbles Bathtub,
Floating[fix], Cooperative Colonies and Fast Track's No Splash patch that step.

**Verify.** `ONI_TWEAK_VERIFY=1` installs the transpiler as well, only noting what it would
land. It compares each step's landings with its list, and checks each landed item for having
stopped falling where it was.

**Measured** on Windows at speed 3, in one launch started without the tweak, 10 s blocks
(2026-11-14, U60 763023):

- On the pile-heavy colony the game's step took 1.47 ms a call, 1.50 with the tweak's hook
  installed and idle, and 0.02 once the 17,539 items had landed. The frame went from 35.7 to
  26.7 ms (28.0 to 37.5 fps).
- On a mid-game colony with 3 such items the step took 6 to 7 µs a call with the tweak off,
  idle or on.

Details in [research/pile-heavy-colony.md](research/pile-heavy-colony.md).

### Animation events

Every frame the game's animation event loop visits each playing animation and reads its
controller: whether it was destroyed, its current animation and play mode (for the frame), and
its play speed. With `fast-anim-events` the loop is the same, entry by entry, but those inputs
come from a compact copy, refreshed whenever the game changes them (an animation started, queued
or swapped, a mode set, a controller destroyed, an animation's events restarted).

**Other mods.** It stands down for the session, logged once (`fast-anim-events: off (...)`),
when another mod patches the loop or the methods it copies or reads (`PlayEvents`,
`StopEvents`, the frame index, the play speed). That is checked at the first frame in a colony.

- A postfix alone keeps it on, except on the methods it copies or reads.
- On the methods whose changes refresh the copy only a finalizer counts. The refresh is marked
  after anything else a mod's patch does there (Fast Track's animation options patch one).

**Verify.** With `ONI_TWEAK_VERIFY=1` every entry's copy is compared with the controller's own,
and differences are logged.

A mod that writes an animation's play speed directly, without restarting the animation, isn't
seen until the animation next changes. On screen its events stay within 0.01 s of it (each frame
the game resets their clock to the animation's once the two differ by that much); off screen
their timing drifts from the animation until it next changes.

### The Load screen

Each refresh of the Load screen (opening it, Load more, a conversion between cloud and local)
lists every save twice and reads each one's header four times.

**`fast-load-screen`:**

- **Headers.** A refresh reads each header once. A later read of the same file in the refresh,
  while its listed time is the same, gets a copy of the first. Headers are read ahead on 8
  threads, and still parsed and logged in the game's order. The colonies, their order and the
  log are the game's, and a read that fails is read and logged again as the game would.
- **Previews.** While the screen is shown, a preview whose file has the same bytes as one
  already made gets the same pixels without being decoded or, on U60, drawn and read back from
  the GPU again. Each preview is still read from its file and is a new texture of the game's.
- **Verify.** With `ONI_TWEAK_VERIFY=1` each header and preview kept is checked against the
  game's own read ([measurements](research/load-screen-many-saves.md#results-2026-11-03)).

**`fix-load-screen-clicks`** fixes a bug in the game's code (U59 and U60):

- **The bug.** Each refresh adds the colony list's click handler to the rows already shown
  again, without removing the old one. After k Load more clicks, one click on a colony of an
  earlier page builds that colony's list of saves k + 1 times in one frame (seconds for a colony
  of hundreds of saves). The last build stays on screen.
- **The fix.** A row keeps only the handler of the refresh that filled it.
- **What differs from the original.** The screen ends up the same, but the colony is built once
  a click. So the earlier builds' log lines (`Failed to find cluster ...` for a save of an
  unknown cluster) are not written, a mod patching `LoadScreen.ShowColony` sees one call instead
  of k + 1, and the rows those builds made and destroyed within the frame are not made.
- **Default.** It is on by default (the player's choice), but in the Bug fixes group and left
  out of `exact`, since the original does the repeated work.
- **Game updates.** It installs only where the Load screen still has the bug. When a game update
  changes `LoadScreen.AddColonyToList` or the fields the fix reads, or the method no longer adds
  the handler without clearing the button first, the tweak is left out with one line in
  Player.log (`fix-load-screen-clicks: left out, ...`).

### The loading screen

A save loads inside one engine frame (the scene, the save read, every object made and spawned), so
the game shows no new picture for up to half a minute
([measurements](research/save-load-and-loading-screen.md)). With `loading-animation` the
presenter thread shows the loading screen again 30 times a second with a thin bar under it, in the
start-up splash's place and colours, until the colony's first frame, on Vulkan and on OpenGL;
nothing is drawn over a game frame. A loading overlay that no scene load follows within 250 ms gets a pulse instead.

The bar:

- **Phases:** the scene switch, the save read, its decompression, the world set up, the objects
  loaded (`SaveManager.Load`), what follows, and the Start pass that spawns every object.
- **What moves it within a phase** is the engine's or .NET's: the zlib stream's position in the
  compressed bytes (with `fast-save-compression`), the reader's position in the decompressed
  body, the engine's Start queue counted.
- **The game's methods** (`SaveLoader.DecompressContents`, `SaveManager.Load`) only mark where a
  phase starts, with prefixes and postfixes that change nothing. When one is missing after a
  game update, or another mod transpiles `SaveManager.Load`, that phase moves by time instead,
  and the load is not affected.
- **Shares.** Each phase takes the share of the bar it took the last time the same save (by path
  and size) was loaded, kept in `oni-moono/load-times.json` (32 saves at most). A save never
  loaded uses a mid-game colony's.
- **Without a count,** the bar moves about 80% of the way through a phase by the phase's
  expected time and creeps on after. It never goes back.

A kanim the loading screen plays (Pip Licks Butt's Pip) keeps playing. When the load starts, the
overlay is drawn once for each frame of the kanim's anim into a separate texture, with only the
frame number in the kanim batch's data changed and put back. The presenter plays them on at
the anim's rate. That costs 3 to 11 ms (at most 50 ms; past that it is not replayed). With
`ONI_TWEAK_VERIFY=1` the first frame replayed is compared with the frame the screen shows, pixel
by pixel, and the log says how many differ.

### The sound pile fix

Many copies of one sound starting together let FMOD, the game's sound engine, pile up event
instances until its Studio thread runs out of CPU: the game then stalls in an FMOD call and falls
silent, or freezes for good (issue #51; [measurements](research/vole-audio.md#the-two-sound-fixes-2026-11-04)).
The tweak is on by default, in the Bug fixes group and left out of `exact`.

**`fix-sound-piles`** limits how many copies of one sound start in one cell in one frame:

- **The limit.** A one-shot over the limit (3) of the same event, in the same cell, in the same
  frame, is not made.
- **What counts.** Only 3D one-shot events, played anywhere but the origin, where the game plays
  its menu, UI and screen sounds. Looping, 2D and UI sounds are never skipped.
- **What differs from the original.** Where many critters stand in one cell (1,500 Shove Voles
  in one cell start about 2,400 landing sounds a second, all at one point), only the first
  copies of each frame play. A pile sounds quieter, and the game doesn't freeze. Events that
  read the game's `objectCount` parameter see the copies made.
- **In normal colonies** it fires only where critters or Duplicants crowd one cell (a ranch, a
  fish tank). In the six colonies measured it fired only on sounds whose FMOD event plays one
  copy at a time anyway, so the copies it skipped would not have played.
- **The log.** Player.log notes how many it skipped, at most once a minute
  (`fix-sound-piles: skipped ...`).

### The sound memory guard

`fmod-quarantine` works around, and reports, a bug in FMOD, the game's sound engine. It is on by
default, in the General group and part of `exact`: it changes how FMOD gets its memory, not what
the game does. Windows only for now (OniMoono.exe carries the allocator; Linux has no native
library of Moono's yet, and there the tweak logs that it is not active).

Issues #72, #73, #58 and #78 are crashes with 0xC0000374 (heap corruption), found in
`RtlFreeHeap` on the "FMOD Studio update thread" while FMOD destroys an event instance. In #72 one byte of a heap block
header had been zeroed: a write into memory that had been freed and given to someone else. FMOD's
staff acknowledged such a write in the stop/steal path of FMOD 2.01.18 and later, "which could
crash if the memory is reallocated immediately", with no fix or workaround
([qa.fmod.com/t/hard-crash-in-fmodstudiol/24589](https://qa.fmod.com/t/hard-crash-in-fmodstudiol/24589)).
The game ships 2.03.12. FMOD allocates from the process heap, which it shares with .NET, the
graphics driver and the rest, so its late write can land in anything.

With the tweak, FMOD allocates through `src/OniMoono.NativeHost/fmod_heap.c`, given to
`FMOD_Memory_Initialize` before FMOD Studio starts (a prefix on FMODUnity's
`RuntimeManager.Initialize`, the same in U59 and U60):

- FMOD's blocks up to 16 KB are slots of 40 size classes, cut from 64 KB slabs in address space
  reserved for FMOD alone; larger ones get pages of their own. What the allocator knows about a
  block (its state, size and type, and each slab's free slots) is kept in side tables away from
  the blocks, found from the address alone. Nothing in or between FMOD's blocks is bookkeeping,
  so a late write can only change FMOD's own data or a freed block's fill, never something the
  allocator or Windows trusts.
- A freed block is filled with a pattern and kept in a quarantine, oldest out first, up to 32 MB
  (`ONI_FMOD_QUARANTINE_MB` sets another size; 0 keeps the rest without a quarantine). Blocks
  over 16 KB, mostly sample data, may hold a quarter of it at most, so the sample data FMOD frees
  by the megabyte while it loads sounds cannot push the small blocks (the event instances the bug
  writes to) below three quarters. A
  late write lands there, where nothing else lives. When the block leaves, it is checked, and a
  free slot is checked once more when it is reused, which catches a write that came after the
  quarantine.
- The bytes after each block's end, to the end of its slot, are a canary, so an overrun is noted
  too (a write just before a block lands in the slot before it, and is noted against that one);
  so are a double free and a pointer FMOD's allocator never returned. Slab memory is never given
  back, only reused, so a late write never faults.
- Once the player quits, a freed block is left as it is: not filled, checked, reused or
  unmapped. The game releases FMOD at the quit, and FMOD then frees all it holds (about 325 MB
  on a late-game colony, mostly sample data). Filling and checking that touches every page of
  it, which a system short of memory first reads back from its page file. A late write during
  the release still lands in memory that stays FMOD's. `ONI_FMOD_QUARANTINE_AT_QUIT=1` keeps the
  quarantine and its checks to the end.

What to look for in Player.log: a line per problem found, such as

```
[engine] fmod heap: write after free, a 32-byte block (type 0x0) at 0x1f2a3b4c5d0: 1 byte changed at offsets 5..5 (+5: 00 for db), 3 ms after it was freed; found 2026-11-10 14:03:12.345Z
```

with the block's size, FMOD's memory type, the changed bytes (offset from the pointer FMOD got:
the value found, and the pattern it should be) and how long after its free. A "write to a free
block after its quarantine" line is the same bug, found when the slot was reused. A write after free is the bug of #72 and #58, caught before it could
corrupt the heap. `fmod-quarantine: FMOD allocates from Moono's heap …` at start says the tweak
is active; at quit a line counts FMOD's allocations and the problems found, and `--frame-stats`
adds an `fmod heap:` line every 600 frames (what FMOD holds, how fast it allocates).

FMOD calls the allocator on its own mixer, update and loading threads, so it is native code that
never calls into .NET (a managed callback could wait there for a garbage collection); the lines
reach the log through a lock-free queue the main thread empties each frame. Off, FMOD allocates
as it always has: nothing is patched or called.

Measured on a late-game colony of 22 Duplicants with a very large base (Windows, 2026-11-11, at
speed 3):

- **FMOD's load:** 16,000-19,000 allocations a second, almost all of blocks up to 16 KB (about
  4 MB/s of them, and 1 MB/s of larger ones). It held about 325 MB live in 470,000 blocks.
- **The allocator** held at most 417 MB: the 32 MB quarantine, 14 MB of side tables, and the
  rest from rounding blocks up to their size class and from slabs not full. That is about 90 MB
  over what FMOD has live, and 38 MB more than with the first version's private heap, which
  held at most 379 MB (its 32-byte headers, not Windows' own heap overhead).
- **Quarantine time:** freed blocks stayed for about 6.4 s (2.5 s on a mid-game colony of 50
  Duplicants, where FMOD frees 12 MB/s).
- **Frame rate:** the same with either version (93-100 fps).
- **Cost:** an allocation and its free cost about 140 ns on one thread, as with the private
  heap. Eight threads at once ran 27 % faster.
- No problem was found.

In a replay of #78's writes (stale writes on 300 freed instances a round, after the quarantine
has moved on), the first version failed with 0xC0000374 in the first round of every run; this
one ran all 30 rounds and logged the writes it could see.

### The released sounds fix

`fix-released-sounds` fixes a bug in the game's own code that ends in heap corruption after
hours of play (issues #78, #72, #73 and #58): the game keeps the handle of an FMOD event
instance after it has released it, and goes on calling it. It is on by default, in the General
group and part of `exact`: nothing audible changes. It replaces `fmod-stale-handles`, an
earlier guard that skipped every call on a destroyed instance; a launch option naming that one
now names this one. It works on every platform (managed code only) and complements
`fmod-quarantine`, which catches such writes where they land.

**Why.** An FMOD Studio handle is 32 bits: a table (the low 8 bits), a slot (13 bits) and a
serial (11 bits). FMOD resolves a handle by checking that the object in its slot still holds
that handle; nothing checks the object's kind. A freed slot gets its serial raised by one
(wrapping after 2,047), and freed slots are reused oldest first. In U59 and U60 the game keeps
instances after it has released them at two places:

- An ambience layer (`AmbienceManager.Layer.Stop`) stops and releases its instance but keeps the
  handle, and `Quadrant.Update` calls `Stop` on every looping layer not among the top three each
  update: an `isValid()` on the released handle each time, then `stop()` and `release()` on
  whatever it resolves to.
- `LoopingSoundManager.StopAllSounds` (switching worlds, and `Game.StopBE` as a scene unloads)
  stops and releases every playing loop but leaves it flagged as playing: its position, volume
  and parameters (and the parameter updaters' `setParameterByID`) keep being set every frame
  while it is in view, and it is stopped and released again when it leaves it.

Each such call fails while the slot is free or holds an object with another serial. But once
the slot has come round 2,047 times, the object in it has the same handle, and the game's call
lands there: on another sound, or on another kind of FMOD object. Every command is resolved
again by the Studio update thread when it runs, without a check of the object's kind;
`EventInstance::stop`'s increments a counter at offset 0x84 and clears a flag at 0xBD, past the
end of a smaller object. That is the pair of writes (57 bytes apart) of #78's `fmod heap:`
lines, which landed in freed blocks next to a live object. On a late-game Spaced Out colony at
speed 3 (Windows, 2026-11-11) the game made 11 such calls a frame, almost all the ambience
layers' `isValid()`, and the serials came round about 16 times a minute: a wrap after about two
hours, and #78's writes began four and a half hours into a session.

**How.** Prefixes replace the two methods with the game's own bodies plus the missing step, as
the game's `SpeechMonitor` and `MainMenu` already do after a release:

- `Layer.Stop` clears the handle after `release()` (`EventInstance.clearHandle`). `isValid()`
  is then false without asking FMOD, as for a layer never started, and `Start` makes a new
  instance as before.
- `StopAllSounds` takes each playing loop off the parameter updaters as `StopSound` does (they
  hold their own copy of the handle and find it by it), then clears its handle. The loop stays
  flagged as playing, as in the game: the calls the game goes on making on it carry the null
  handle, which FMOD refuses (`ERR_INVALID_HANDLE`) whatever it has made since, and
  `RenderEveryTick` stops it when it leaves the view and starts it again when it comes back,
  just when the game's would be. The fix sent to Klei also clears the playing flag; tried here,
  that restarted the loops in the frame of the switch, while the sound culler still had the old
  view, and stopped them a frame later.

Each install checks the two bodies against fingerprints reviewed for U59 and every U60 build;
a game update that changes either turns the tweak off for that build.

With `ONI_TWEAK_VERIFY=1` (or `ONI_VERIFY_RELEASED_SOUNDS=1`) the handles released at the two
places are kept, and any later call on one through the `EventInstance` methods the game uses is
counted and logged with its caller (`VerifyStaleCalls`, which must stay 0). After each
`StopAllSounds` its loops are followed for 300 frames: the log says when the game stops each one
and whether one gets a new instance (`VerifyRestarted`, 0 when the game's timing holds), and
FMOD's answer to the first call on a cleared loop is logged.

**Measured** (Windows, a late-game Spaced Out colony at speed 3, 2026-11-11). To reach the wrap
in minutes, a test build made and released 300 extra instances a frame, for 45,000 frames.
Without the fix, as the serials came round, the quarantine found one write to a freed 80-byte
block (a byte cleared to 0, as by the stop command's write at +0xBD); with it, past the same
point, no `fmod heap:` problem and no call on a released handle. Switching to the colony's rocket
interior and back, with and without the fix and in the original game, the same loops played
before and after each switch (5 to 8 before; none on the rocket; the colony's own again after),
FMOD held the same number of instances (about 110-130 on the colony, 33-36 on the rocket), and
the recorded audio kept the same levels around each switch. With the fix, the game stopped each
loop it had cleared one frame after the switch, as it leaves the view, and none got a new
instance sooner; calls on a cleared loop got `ERR_INVALID_HANDLE`. A red alert loop playing as
the view switched back stayed flagged as playing with no instance, and silent, in all three:
the game's own behaviour, kept as it is.

### The FMOD steal fix

`fmod-steal-fix` patches, in memory, a write to freed memory inside FMOD itself. It is on by
default, in the General group and part of `exact`, and it applies only to the exact FMOD files it
was written for. Issues #78, #72, #73 and #58.

**The bug.** In FMOD 2.03.12 (build 160365), `EventInstance::changeStateStopped` fires STOPPED,
destroys the instance when the game has released it, and then clears the instance's timeline
position (`this+0x4B0`, what `getTimelinePosition` reads). When the destroy happened there and
then, that store writes 4 zero bytes into the freed instance (offset 0x4B8 of a freed
1,312-byte block). It happens when a bus's instance limit (Stealing = Oldest) steals an instance
that is still loading its sample data: `BusInstance::addPlayingEvent` stops it straight away, the
stack of FMOD's forum thread 24589. In the game, the Duplicants' TileSnow movement sounds share
such a bus: ten of them started and released in one frame write to freed memory twice.

**The patch.** Only that store is skipped, and only when the instance was destroyed before it:

- Linux (`libfmodstudio.so`): the destroy is inlined, and the store follows its call. The
  `test eax,eax; jne epilogue` after the call becomes `jmp epilogue`. eax is 0 after a successful
  destroy, the value the function returned after the store; on an error the original jumped to
  the epilogue too. The paths that defer the destroy jump to the store as before.
- Windows (`fmodstudio.dll`): the destroy is a helper (fmodstudio+0x69790) that destroys at once
  unless the instance is flagged at +0x2F8 (0x80) or the system is in its update (+0x7A8). The
  site jumps to a stub in the slack at the end of the module's code section, which reads those two
  conditions into edi (the helper reads them first thing, unchanged) and jumps back to the
  helper's call, still in the function; after the call, the original's `test eax, eax` and a jump
  to a second stub, which returns an error as before, goes back to the store when the destroy was
  deferred, or skips it, counts it in the slack at the end of the data section and returns 0. The
  stubs call nothing, so the only return address involved is the call's own, in the function, at
  an instruction that is no epilogue: a stack walk from inside the helper unwinds through the
  function's own unwind data. At start, the system's unwinder must take a synthetic stack from the
  new return address to the same caller as from the original one, or the original bytes are put
  back and nothing stays written. edi is saved by the function, used as a loop variable until
  +0x68A3A and dead from there to its restore at +0x68A85.
- U60 ships FMOD's logging build of the same version (`fmodstudioL.dll`, `libfmodstudioL.so`),
  the same code with logging around it. Linux: the `je store` after the destroy's call is
  retargeted to the `xor ebp,ebp; jmp epilogue` just past the store (one byte). Windows: the
  same two stubs, with r15 for the decision (the function saves r15 in its prolog, restores it
  in its epilogue and does not use it after the site; edi holds the call's result there), the
  original's `mov edi, eax` kept after the call, and an error sent to the original's logging
  path.

Every other path runs the same instructions on the same state, and the STOPPED and DESTROYED
callbacks come in the same order.

**When it applies.** It is applied in a postfix on FMODUnity's `RuntimeManager.Initialize` once
FMOD Studio runs, before the game has made any instance, and only when all three match:

- FMOD's version (2.03.12 build 160365, as the core library reports it to the core system's
  getVersion).
- The SHA-256 of the loaded Studio library's file (U59's `fmodstudio.dll` and
  `libfmodstudio.so`, which U60 763796 and 764025 ship too, and the U60 betas'
  `fmodstudioL.dll` and `libfmodstudioL.so`, the same from 761015 to 763612).
- Every byte the patch relies on: the site and what follows it to the epilogue, and the
  destroy's code (on Windows also the slack still empty).

With anything else, such as a game update that ships another FMOD, nothing is written.
`fmod-steal-fix: not applied, …` names what did not match, and `fmod-quarantine` stays the only
guard. `fmod-steal-fix: FMOD 2.03.12's store …` says it is in. On Windows the line at quit
counts the stores skipped.

**Measured** (2026-11-11):

- **FMOD's own repro,** with the game's banks and library (ten TileSnow instances started and
  released in one batch), on Windows and Linux, synchronous and asynchronous update, with U59's libraries
  and with U60's logging ones: two writes
  to freed memory without the patch, none with it (the Windows counter: 2 stores skipped). With
  the sample data loaded first (the control) there were none either way, and nothing skipped.
  The callbacks come in the same order with and without it.
- **In the game on Windows** the patch is applied at start and the colony plays on. A test that
  started and released ten TileSnow instances every 5 seconds for 50 seconds of a late-game
  colony stole no loading instance (0 skipped). Three sessions of 25 minutes or less without
  the patch logged none of its writes either. On that colony the path is rare.

### The colony snapshot fix

`fix-timelapse-snapshots` fixes the images the game's Timelapser writes: the colony snapshots in
`RetiredColonies` (every cycle up to cycle 50, then less often) and a save's colony preview.
It is on by default, in the Bug fixes group and left out of `exact`: it changes those images,
and nothing else.

The Timelapser moves the cameras to the snapshot's position and size at the end of one frame and
renders into its image (512×768 by default, 2:3) at the end of the next. That frame's own updates
set up the post-effects for the screen (16:9), 2.67 times wider than the image:
- `Game.LateUpdate` sets `_WsToCs` and `_WsToCcs`, which map the screen to the grid's and the
  world's cells, from the screen's frame. The heat, cold, dust and radiation effects land at
  the wrong cells, and the build grid (`GridCompositor`) maps the left of the image to cells left
  of the grid. There its grid pattern keeps the sign of a negative cell and draws the full grid
  colour in every column: vertical stripes. The grid is on while a building is picked for
  placement, and the Timelapser waits only until no drag is under way, so a player who leaves a
  building picked gets the stripes in that cycle's snapshot.
- The light buffer, drawn by a camera of its own into a screen-sized texture, covers the screen's
  frame and is read with the image's coordinates: lights are squeezed 2.67 times sideways.

**Measured** on an early Spaced Out colony whose snapshot frame reaches left of the grid (the
pod at x = 124, the frame 2 × 170 cells high), in the original and in Moono without the tweak
(2026-11-10):

- With a ladder picked, the image's left 150 columns are covered with stripes. Their brightness
  steps 15 times more from a column to the next than from a row to the next, and they are 119
  levels brighter on average than the image. With no tool picked they are not.
- The two players' images agree to under 0.3% of pixels.
- The building preview (on the `Place` layer, which the snapshot's camera draws) did not show in
  the original's snapshot, nor did anything else that follows the pointer.

**With the tweak,** for the capture's render only:

- The grid compositor is off.
- `_WsToCs` and `_WsToCcs` are set for the image's frame (the base camera's position and size at
  the image's aspect, with `Game.LateUpdate`'s formulas).
- In the frame of the capture, the light buffer camera draws at the image's aspect. The screen
  is behind the Timelapser's freeze frame then, so a player sees no change.

Each of these is set back right after the render, also when it fails. In U60, which reads the
image back asynchronously, the readback is queued before they are set back.

### The table scroll fix

`fix-table-scroll` keeps a table screen's columns where they were scrolled. It is on by default,
in the Bug fixes group and left out of `exact`.

Each row of a table screen (Consumables, Vitals, Priorities and the like) scrolls its columns
in a ScrollRect of its own, and the game keeps them in step: a row whose scroll position changed
sends it to `TableScreen.SetScrollersDirty`, which moves every row there. The row's scroller
object also holds the layout group that places the row's columns, so whenever the table's rows
are laid out again, the default ("New Duplicants") row's columns go back to their start. Its
ScrollRect reports position 0 at the next LateUpdate, and `SetScrollersDirty` moves every row to
the first columns. While the columns are scrolled sideways that happens every second or so: the
table jumps back for a frame, or stays back until the next notch of the wheel. Frame-by-frame
captures show it in the original on Direct3D 11 and Vulkan, and in Moono on Vulkan and OpenGL.

With the tweak, once a row's layout group has placed its columns, they go back to the table's
scroll position (the game's own target, as `PositionScrollers` sets it), so a layout pass no
longer scrolls a row and no row that was not scrolled moves the others. Scrolling with the wheel,
dragging a row or the header's scrollbar, and the table zeroing its columns as it opens are as
before. The tweak stands down, with a line in `Player.log`, when another mod patches
`TableScreen.SetScrollersDirty`, `TableScreen.PositionScrollers` or the layout groups' placing.
`tests/e2e/fix-table-scroll.yaml` scrolls Consumables' columns with the wheel while the game runs
and checks that they never move back.

### Table rows in sight

`lazy-table-refresh` (Graphics & overlays) changes when a table screen's rows are brought up to
date. It is on by default but left out of `exact`, as it changes when the game's code runs.

**The once-a-second update.** Once a second the render 1000 ms bucket marks every column of the
table screens dirty (`TableColumn.Render1000ms`), and the open table's next `ScreenUpdate` runs
each column's load action on the widget of every row. On a colony of 200 Duplicants that is
thousands of widgets in one frame, most of them scrolled out of sight. With the tweak:

- That frame updates the rows within the table's scroll view or within one view's height of it,
  in the game's order, and marks the others stale.
- Each frame, before the canvases are laid out and drawn (`Canvas.preWillRenderCanvases`), a
  stale row that has come that close (scrolled, the view resized, the rows laid out again)
  brings every stale row up to date, with the load actions each missed, in the same order. A
  row's texts changing lays the whole table out again, so catching rows up one or two a frame
  while a list scrolls would lay it out every frame.
- While the list is being scrolled (it moved within the last 1.5 seconds) the update is the
  game's, of every row, as that lays the table out once a second and no more.
- Sorting the rows first brings every stale row up to date.

**Opening a table.** The same goes for the frame in which a table goes through its rows
(`TableScreen.RefreshRows`: each time it is opened, and when a Duplicant is added or removed).
The game then runs every load action of every row it already has, 17,000 of them for
Consumables on a colony of 200 Duplicants. With the tweak:

- A row the table already had runs them only when it is near sight, before the rows are sorted
  and laid out or, when that brought it near, right after.
- The others are marked stale for every column and caught up as above.
- A row made in that frame is loaded as the game loads it, and a table opened for the first
  time is the game's throughout.

**What differs.** A row scrolled into view shows values read as it comes into view, at most a
second newer than the game would show. A mod reading the text of widgets out of sight would
read older values. Every row and object stays as the game made it.

**Verify.** With `ONI_TWEAK_VERIFY=1` each update also runs the game's, of every row, and the
widgets of the rows in sight must read the same after both (texts, sprites, colours, toggles'
states, active objects). No stale row may lie in the view in any frame.

**Other mods.** The tweak stands down when another mod patches the table's `ScreenUpdate`,
`SortRows` or `RefreshRows`, a row's `RefreshColumns`, or the columns' `Render1000ms`,
`MarkDirty` or `MarkClean`.

Measured on Windows (Vulkan, speed 3, exact tweaks with and without it, two launches each,
2026-11-13), the once-a-second update's cost over the two frames around it, and the mean frame,
off → on:

| Table | 200 Duplicants: update | Still | Scrolling | 50 Duplicants: update | Still | Scrolling |
|---|---|---|---|---|---|---|
| Consumables | 22.0 → 1.7 ms | 40.5 → 37.1 ms | 81.8 → 75.0 ms | 8.4 → 5.9 ms | 18.6 → 16.7 ms | 30.8 → 26.2 ms |
| Vitals | 20.3 → 15.1 ms | 18.9 → 15.0 ms | 32.1 → 22.2 ms | 6.6 → 4.0 ms | 11.6 → 12.2 ms | 14.6 → 14.2 ms |
| Priorities | 3.2 → 1.6 ms | 35.5 → 33.7 ms | 49.3 → 46.0 ms | 0.9 → 2.0 ms | 13.3 → 14.3 ms | 17.5 → 17.8 ms |

The other figures moved within the launches' spread (about 2 ms on this machine that day); the
Schedule screen, which is not a table of this kind, ran the same with and without it.

### Table rows as they are built

`fast-table-rows` (Graphics & overlays, exact, on by default) takes two searches out of the
frame in which a table screen builds its rows, the first time it is opened.

**The first search.** Every cell's load action asks the table which row and which column its
widget belongs to (`TableScreen.GetWidgetRow`, `GetWidgetColumn`). The table remembers each
answer, but finds it the first time by going through its rows, or its columns, in order and
searching each one's widgets for this one. For R rows of C columns that compares widgets about
R²C² / 2 times for the rows and R²C² / 4 for the columns. Consumables has some 80 columns.

With the tweak, before a row's cells are loaded (`TableRow.RefreshColumns`), each of its widgets
is entered in the table's two dictionaries with the row and the column it was made for, so the
game's lookups find every answer at once. The answers are the game's: the first row of the
table's list that holds the widget and the first of its columns that does.

**The second search.** Each of Consumables' food cells also ends its load action with
`ConsumablesTableScreen.refresh_scrollers`, which counts the discovered foods and then shows or
hides the scroll bar and border of every row of the table: every row, for each cell of every row.
Nothing a load action does changes what it reads or writes, so with the tweak it runs for the
first cell of each row a table loads and is skipped for that row's other cells (unless the table
has more rows by then). Outside a row's load it runs every time.

With `ONI_TWEAK_VERIFY=1` each entry is also found the game's way and must be the same, and each
`refresh_scrollers` call that would be skipped runs after all and must leave every row's scroll
bar, border and scroller as they were. The tweak stands down when another mod patches
`GetWidgetRow`, `GetWidgetColumn`, either `ContainsWidget` or `refresh_scrollers`. Measurements are
in [PERFORMANCE.md](PERFORMANCE.md#opening-a-table-screen).

### Colony images written in the background

U60's Timelapser writes its images (a save's colony preview, the daily snapshots) in the
background: it reads the render back asynchronously, then encodes and writes the PNG on a task.
It waits for those writes only when the colony is unloaded, not when the game reads the files.
The Load screen opened from the pause menu right after a save reads the preview that save is
still writing: it gets a partial file and shows no picture (on Windows the read can fail
outright). The Retired Colonies screen reads the snapshots the same way, and deleting a save
deletes its preview.

`wait-timelapse-writes` is always on, whatever tweaks are chosen, and has no row in Moono
Tweaks: before each of those reads and deletes, it waits for the Timelapser's pending read-backs
and writes (`Timelapser.FlushPendingScreenshots`, which returns at once when there are none). The
files and what the game shows of them are the game's own; only when they are read changes.
`tests/e2e/timelapse-background-writes.yaml` holds a preview's write back 8 s and opens the Load
screen right after the save: the preview shows.

### The report history in a save

A colony keeps every day's report for its whole life, and each save writes all of them. On a
late-game colony (cycle 17,000) that is about 4 million report entries and their notes: 496 MB
of the 707 MB a save writes before compression, and half of the time the game stands still for
it. Two of the Saving and Simulation tweaks write that history with separate code, and the same
bytes:

- **The entries** (`fast-serialization`). The game's serializer writes each day and each entry
  as an object of unknown type: for every one it looks the type's template up, calls it through
  a boxed reference, writes a length, and seeks back in the stream twice to fill it in; an
  entry's name and its list of per-Duplicant entries (a struct, boxed, with an array read one
  element at a time) go through the same steps again. `fast-serialization`'s compiled plans
  covered a template's plain fields, which left these lists to the game's walk. Now the two
  fields that hold the history (`ReportManager.dailyReports` and `todaysReport`) have a writer
  compiled for their types: it reads each field directly, builds a day's bytes in a buffer with
  the lengths filled in there, and hands the buffer to the save in blocks. The plans are also
  kept from one save to the next: the game makes every type's template anew for each save, and
  a plan was compiled for each again (about 750 types on a large colony).
- **The notes** (`compact-report-history`). The tweak keeps past days' notes in flat arrays; a
  save now packs them into blocks of the bytes the game's three writes a note make, instead of
  three calls of the writer for every note.

What stays the game's:

- **Side effects.** What the game's serializer does besides writing bytes is kept. The three
  types' templates enter the save's directory at the same first use and in the same order, and
  a name of 1,024 bytes or more logs the game's warning.
- **Unknown fields.** The typed writer writes only when the three types are exactly the fields
  it knows (checked at start-up: no callback, property or custom serializer among them).
  Otherwise the game's walk writes them, as it does for every other type.
- **Other mods.** It also steps aside, with a line in `Player.log` (`fast-serialization: the
  report history keeps the game's writer (...)`), while another mod patches a method it stands
  in for: `Helper.WriteValue`, `SerializationTemplate.SerializeData` or its constructor,
  `Manager.GetSerializationTemplate`, `IOHelper.WriteKleiString` or `IOHelper.WriteSingleFast`
  (Fast Save's Use Delegates and BetterSave patch the first three). The notes go out one value
  at a time while a mod patches `WriteSingleFast`.

With `ONI_TWEAK_VERIFY=1` each save writes the history both ways into memory, the game's with
`fast-serialization`'s plans off, and compares the bytes (`fast-serialization: verify: the
report history identical (N bytes against N; ...)`, and the same for today's report and for
`compact-report-history`'s notes).

### Past days' report entries

Every day's report keeps a list of about 240 entries, and each entry its own list of entries
per Duplicant or building. On a late-game colony (cycle 17,000) that is 4.1 million objects in
187,000 arrays that the game never changes once the day is over, and that every full garbage
collection traces. `compact-report-history` moved their notes into flat arrays;
`compact-report-entries` does the same for the entries themselves:

- **Packing.** After a load, every past day goes into flat arrays of 40 bytes an entry (its
  numbers, its context as an index in a table of strings, its children as a range), and its
  list becomes null. At each night the day that ended goes the same way. Today's report stays
  as the game keeps it. A day of a shape the arrays do not keep exactly (an entry of another
  type, a `gameHash` set, a null in a list) stays as the game has it.
- **Reading.** The game reads a day's list itself in four places, checked at start-up from its
  code: a new day, `DailyReport.GetEntry`, `SaveLoader.GetDailyReportMetrics` (a day from
  `ReportManager.FindReport`) and the stress panel (today's). `GetEntry` on a packed day makes
  that entry again as an object, with its children; `FindReport` makes the whole day. The
  objects made are kept for the last 32 days read, so paging the reports screen shows the
  same objects. A day leaving those 32 is compared with its arrays, and packed again from the
  objects when something wrote to them (a `GetEntry` that added an entry, a mod that changed a
  value).
- **The colony summary.** Each save's colony summary reads 16 entries of every day and keeps
  only numbers. Its calls read the arrays into four scratch entries, reused in turn, instead of
  making 300,000 objects a save.
- **Saving.** `fast-serialization`'s writer for the history writes a packed day from its arrays:
  the same bytes, and the same templates asked for in the same order. A save that goes
  through the game's own writer (`fast-serialization` off, or a mod patching what it stands in
  for) gets every day's list back for that save.

It is off for the session, with one line in `Player.log` (`compact-report-entries: off for this
session: ...`), when:

- a loaded mod reads or writes `DailyReport.reportEntries` itself, takes it as a Harmony
  parameter (`___reportEntries`) or names it in a string (reflection). Mods that read days
  through `GetEntry`, `FindReport`, `YesterdaysReport` and the like keep it on, and so do mods
  that read an entry's `contextEntries`: every entry given out is a whole object;
- another mod patches `DailyReport.GetEntry`, `ReportManager.FindReport` or the colony
  summary's constructor (anything but a postfix);
- the game reads `reportEntries` anywhere else, or its types have other fields.

Of the 18 Workshop mods whose code names the report types, three turn it off: ONI Multiplayer
(3807965885, which mirrors past reports for other players), ONI MCP Server (3807166922, its
colony report tool) and RemoveDailyReports (1870873514). Fast Track, Fast Save (which trims old
days), Vision Not Included, Achievement Tracker, Food Tooltip, Research Requirements, MoreTek and
the others read days through the game's methods, or ship copies of the game's types they never
use on the game's objects.

With `ONI_TWEAK_VERIFY=1` each packed day keeps its original list. Every entry given out, every
day made whole and every colony summary read is compared with it (`compact-report-entries:
verify: N reads compared, 0 mismatches`), and each save writes the history the game's way over
the original lists, whose bytes must be those of the arrays
(`fast-serialization: verify: the report history identical`).
`tests/e2e/compact-report-entries.yaml` compares the history's bytes, every past entry, the
reports screen paged back eight days, a save and its reload with the original's.

### The colony summary a save writes

Before its own file, every save rewrites the colony's summary (`RetiredColonies/NAME/NAME.json`,
what the main menu's Colony Summaries shows): the Duplicants, the buildings and 16 statistics
with a point for every cycle. The game gathers them, turns them into JSON and writes the file,
all on the main thread.

With `background-save` on, a save still gathers the data on the main thread. The JSON and the
file are made on the save's writer, ahead of the save's own write. The writer runs the game's
serializer straight into UTF-8 in a buffer it keeps, so no string of the whole JSON (19 MB on a
late-game colony) and no byte array of it (9.5 MB) are made for each save. The gathered data is a
snapshot (strings, numbers and collections made for it, nothing of the live game), and the
file's bytes are the same.

- **Readers wait.** The game's reader of those files (`RetireColonyUtility.LoadRetiredColonies`,
  the Colony Summaries screen) waits for the writer first, as the next save, a load and the end
  of the run do.
- **Other callers** of `SaveColonySummaryData` (an achievement, retiring the colony) get the
  game's method. So does a save while another mod patches that method or
  `JsonConvert.DefaultSettings` is set, with a line in `Player.log` (`background-save: the
  colony summary is written on the main thread (...)`).
- **Verify.** With `ONI_TWEAK_VERIFY=1` the JSON is made on the main thread too and its bytes
  compared (`background-save: verify: the colony summary's JSON identical`).

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

`zh.po` covers the whole catalog: a string added without its Chinese fails
`TweakTranslationTests.CompleteTranslationsCoverTheCatalog`, which names it, as
`moono-tweaks-search-zh.yaml` expects every row in Chinese.

The same files translate the Workshop loader mod's dialog (`STRINGS.MOONO.LOADER.*`,
`src/MoonoLoader/LoaderStrings.cs`; docs/INSTALL-MODES.md), which reads them from the item.

They also translate the messages the player shows before the game loads
(`STRINGS.MOONO.START.*`, `src/OniMoono.Host/Launch/StartText.cs`): a Moono build for another
game version, and the standalone launch's (no game found, Steam not running). The game's
translations are not loaded then, so the player picks the language itself:

1. The game's choice in `kplayerprefs.yaml` in its settings folder
   (`Documents\Klei\OxygenNotIncluded` on Windows, `~/.config/unity3d/Klei/Oxygen Not Included`
   on Linux): `SelectedLanguageType` `Preinstalled` with `SelectedLanguageCode` (`zh_klei` is
   `zh`), `UGC` with the translation mod's `strings.po` header, or `None` for English.
2. Without a choice there (no file, no key, a damaged file, a translation mod not found): the
   system's UI language.

The file is then found as above. Player.log's `start messages:` line says which language was
chosen and why.

When the English changes in `src/OniMoono.Host/GameTweaks/GameTweakNames.cs`, `TweakStrings.cs`,
`LoaderStrings.cs` or `StartText.cs`,
run the unit tests with `ONI_UPDATE_DOCS=1` to regenerate `moono.pot`; the tests then list each
translation's stale strings.
