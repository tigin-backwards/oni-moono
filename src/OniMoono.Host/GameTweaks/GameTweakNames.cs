#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OniMoono.Host.GameTweaks
{
    /// A row's measured gain, as a player would put it ("autosave freeze ~12 s → ~5 s on a large
    /// colony"), with where it comes from so it can be refreshed: the colony, the platform and
    /// when it was measured, and the document holding the measurement. `Key`: its translation
    /// key's last part (STRINGS.MOONO.TWEAKS.GAINS.KEY, TweakStrings).
    internal sealed record TweakGain(string Key, string Effect, string Colony, string Platform, string Measured, string Source);

    /// A section of the Moono Tweaks screen. `Key`: its translation key's last part
    /// (STRINGS.MOONO.TWEAKS.GROUPS.KEY, TweakStrings). `Gain`: one shared by all of its rows
    /// (the save tweaks are measured together). `ChangesGameplay`: its tweaks change what the game
    /// does (left out of `exact`, off by default but where OnByDefault; the heading in the warning
    /// colour, with a note under it).
    internal sealed record TweakGroup(string Key, string Title, TweakGain? Gain = null, bool ChangesGameplay = false);

    /// One row of the Moono Tweaks screen and of docs/TWEAKS.md: `Title` in sentence case,
    /// `Effect` one short line on what a player gets from it, `Detail` (optional) how it does it,
    /// for the row's tooltip and the docs. `IsTweak` is false for the player's options (the
    /// splash, frame stats, log paths and the GPU), which are rows but not `--game-tweak` names.
    /// `Choices`: a row with more values than on and off (the GPU's), in the order its button
    /// steps through them; null for a toggle. `CatalogChoices`: when the row offers fewer on
    /// some OS (the renderer's Direct3D 11, Windows only), every choice, so the translation
    /// catalog is the same on every OS. `ChangesBehaviour`: a tweak outside the groups
    /// that change gameplay (Changes gameplay, Bug fixes) that still changes what the game does a little (off by default, left out
    /// of `exact`). `OnByDefault`: a tweak that changes what the game does (left out of `exact`)
    /// but is on by default all the same (the player's choice: path-probe-repair,
    /// fix-load-screen-clicks, fix-sound-piles, fix-timelapse-snapshots, fix-table-scroll).
    internal sealed record TweakInfo(string Id, TweakGroup Group, string Title, string Effect, string Detail = "", TweakGain? Gain = null, bool IsTweak = true,
        string[]? Choices = null, bool ChangesBehaviour = false, bool OnByDefault = false, string[]? CatalogChoices = null)
    {
        /// The launch option that sets this row (shown in its tooltip); "" for none (update checks).
        public string LaunchOption => Id switch
        {
            GameTweakNames.UpdateCheckRow => "",
            GameTweakNames.SplashRow => "--splash moo",
            GameTweakNames.FrameStatsRow => "--frame-stats",
            GameTweakNames.LogPathsRow => "--log-paths hidden",
            GameTweakNames.GpuRow => "--gpu " + string.Join("|", Choices!),
            GameTweakNames.RendererRow => "--renderer " + string.Join("|", Choices!),
            GameTweakNames.DxgiRow => "--dxgi on|off",
            GameTweakNames.StallRepeatsRow => "--stall-repeats on|off",
            GameTweakNames.GcRow => "--gc " + string.Join("|", Choices!),
            _ => "--game-tweak " + Id,
        };
    }

    /// Every row of the Moono Tweaks screen, with its group, title, one-liner and gain: the one
    /// source for the screen (MoonoTweaksMenu), for option parsing (All, ChangingBehaviour) and
    /// for docs/TWEAKS.md's list (generated from this; GameTweakCatalogTests fails when they
    /// disagree, `ONI_UPDATE_DOCS=1` rewrites it). Plain .NET with no game types: options are read
    /// before the game's assemblies (Harmony among them) can be resolved, and the tests compile it in.
    internal static class GameTweakNames
    {
        public const string SplashRow = "splash", FrameStatsRow = "frame-stats", LogPathsRow = "log-paths", GpuRow = "gpu", RendererRow = "renderer", DxgiRow = "dxgi", StallRepeatsRow = "stall-repeats",
            UpdateCheckRow = "update-check", GcRow = "gc";
        public const string FastSaveCompression = "fast-save-compression", BackgroundSave = "background-save", NoSaveGc = "no-save-gc", PostLoadGc = "post-load-gc",
            FastSerialization = "fast-serialization", FastParticleCollision = "fast-particle-collision", CompactNavLinks = "compact-nav-links",
            PagedPathGrids = "paged-path-grids", FetchByCell = "fetch-by-cell", InstantReachability = "instant-reachability", SoundPiles = "fix-sound-piles", TimelapseSnapshots = "fix-timelapse-snapshots", TableScroll = "fix-table-scroll", LazyTableRefresh = "lazy-table-refresh",
            ExactWorldgen = "exact-worldgen", FastPipeRebuilds = "fast-pipe-rebuilds", SharedDuplicates = "shared-duplicates", CompactReports = "compact-report-history", CompactReportEntries = "compact-report-entries",
            SimStepPace = "sim-step-pace", LowFpsTiming = "low-fps-timing", FmodStealFix = "fmod-steal-fix",
            LoadingAnimation = "loading-animation";

        // The gains shown in the screen's tooltips. Measured on Linux under WSL2, where the game's
        // code ran about half as fast as on Windows (docs/PERFORMANCE.md, "How it's measured"); a
        // Windows remeasure is pending: update Effect, Platform and Measured together (and
        // PERFORMANCE.md).
        static readonly TweakGain SaveGain = new("SAVE", "autosave freeze ~12 s → ~5 s on a large colony",
            "a late-game Spaced Out colony with every DLC (cycle 17,000+)", "Linux under WSL2", "by 2026-10-20", "docs/PERFORMANCE.md#the-tweaks (save tweaks: 11.7 → 4.8 s)");
        static readonly TweakGain JobsGain = new("JOBS", "+11% frame rate on a large colony",
            "a late-game Spaced Out colony with every DLC (cycle 17,000+)", "Linux under WSL2", "by 2026-10-20", "docs/PERFORMANCE.md#the-tweaks (fast-jobs)");
        static readonly TweakGain ConveyorGain = new("CONVEYOR", "no hitch on rail edits (was 0.05–0.5 s)",
            "a late-game Spaced Out colony with every DLC (cycle 17,000+)", "Linux under WSL2", "2026-10-25", "docs/PERFORMANCE.md#the-tweaks (a rebuild 50–510 → 2–14 ms)");

        static readonly TweakGain LoadScreenGain = new("LOAD_SCREEN", "Load screen with 4,637 saves opens in ~0.6 s (was ~1.4 s)",
            "U60, 4,637 saves", "Linux under WSL2", "2026-11-03", "docs/research/load-screen-many-saves.md#results-2026-11-03 (opening 1,285-1,457 → 542-720 ms)");

        static readonly TweakGain PostLoadGain = new("POST_LOAD", "no ~0.3 s freeze a minute after loading a large colony; loading ~0.2 s longer",
            "a late-game Spaced Out colony with every DLC (cycle 17,000+)", "Windows", "2026-11-07", "docs/research/gc-pauses.md (a 283–344 ms pause in 6 of 6 loads → none over 68 ms in 6 of 6)");

        static readonly TweakGain FetchGain = new("FETCH", "fetch updates ~8× shorter on a colony with big piles (3.1–3.4 → 0.4 ms each)",
            "a late-game Spaced Out colony with 33,600 fetchable items", "Windows", "2026-11-08", "docs/PERFORMANCE.md#the-tweaks (fetch-by-cell)");

        static readonly TweakGain SharedGain = new("SHARED", "~0.3 GB less memory on a late-game colony",
            "a late-game Spaced Out colony with every DLC (cycle 17,000+)", "Linux under WSL2", "2026-11-09", "docs/research/heap-reduction-designs.md#41-built-2026-11-09-shared-duplicates (reachable heap 6,441 → 6,158 MiB)");

        static readonly TweakGain ReportsGain = new("REPORTS", "~0.9 GB less memory on a late-game colony; loading ~1 s faster",
            "a late-game Spaced Out colony with every DLC (cycle 17,000+)", "Linux under WSL2", "2026-11-10", "docs/research/heap-reduction-designs.md#42-built-2026-11-10-compact-report-history (reachable heap 6,136 → 5,289 MiB; load 15.0 → 13.8 s)");

        static readonly TweakGain PipesGain = new("PIPES", "each pipe network rebuild ~25 ms shorter on a colony with 27,000 pipes (~57 → ~32 ms)",
            "a late-game base-game colony with 27,000 pipes", "Linux under WSL2", "2026-11-10", "docs/PERFORMANCE.md#the-tweaks (fast-pipe-rebuilds)");

        static readonly TweakGain EntriesGain = new("ENTRIES", "4.2 M fewer objects and 7.9 M fewer references for a full memory cleanup to trace on a late-game colony; its pause an estimated ~12% shorter (from a cost model, not timed)",
            "a late-game Spaced Out colony with every DLC (cycle 17,000+)", "Linux under WSL2", "2026-11-17", "docs/research/live-managed-heap.md#designs (reachable objects 29.60 → 25.36 M, references 85.96 → 78.08 M, small-object heap 2,982 → 2,632 MB)");

        static readonly TweakGain PaceGain = new("PACE", "112 → 220 fps at normal speed on a large modded colony",
            "a large modded Spaced Out colony", "Windows", "2026-11-12", "docs/TWEAKS.md#simulation-at-its-own-pace (alternating segments, frame time p99 34 → 12 ms)");

        public static readonly TweakGroup General = new("GENERAL", "General");
        public static readonly TweakGroup Saving = new("SAVING", "Saving", SaveGain);
        public static readonly TweakGroup Simulation = new("SIMULATION", "Simulation");
        public static readonly TweakGroup Graphics = new("GRAPHICS", "Graphics & overlays");
        /// The tweaks that change what the game does for speed: their group, in the warning colour.
        public static readonly TweakGroup ChangesGameplay = new("CHANGES_GAMEPLAY", "Changes gameplay", ChangesGameplay: true);
        /// The tweaks that fix bugs in Klei's game: they change what the original does too, so
        /// they are left out of `exact` as Changes gameplay's are; last, in the warning colour.
        public static readonly TweakGroup BugFixes = new("BUG_FIXES", "Bug fixes", ChangesGameplay: true);

        public static readonly TweakGroup[] Groups = [General, Saving, Simulation, Graphics, ChangesGameplay, BugFixes];

        /// The rows in the screen's order (by group, then as listed).
        public static readonly TweakInfo[] Rows =
        [
            new(SplashRow, General, "Gassy Moo loading splash", "Gassy Moos rain over the window while the game starts, with a progress bar.", IsTweak: false),
            // (Not "Check for updates": the title font draws a lowercase f as a capital.)
            new(UpdateCheckRow, General, "Update check", "For installs from GitHub releases: the main menu shows when a newer Moono is out.",
                "Asks GitHub (api.github.com) for Moono's releases at most once every 6 hours, in the background. Nothing about you or your game is sent. "
                + "The answer is kept in the oni-moono data folder. Installs from the Steam Workshop ask nothing: Steam updates them. Off: Moono makes no network requests.", IsTweak: false),
            new(FrameStatsRow, General, "Frame stats in the log", "Notes where the frame time goes in Player.log, for reporting slowness.",
                "Every 60 frames.", IsTweak: false),
            new(LogPathsRow, General, "Hide personal paths in the log", "Player.log shows your user folder as %USERPROFILE% or ~ and hides your account IDs and name.",
                "Mods still get the full text.", IsTweak: false),
            new(GpuRow, General, "Graphics card", "For laptops with two GPUs: which one the game draws with. Needs a restart.",
                "Default: as the original game (on Windows the faster, discrete GPU; on Linux the system's choice). Discrete: the faster GPU. "
                + "Integrated: the power-saving one; on Windows with OpenGL, choose it in Windows' Graphics settings, which win over this. "
                + "On Linux, on a desktop whose display is on the discrete card, discrete may pick the integrated GPU.",
                IsTweak: false, Choices: [OniMoono.Host.GpuChoice.Default, OniMoono.Host.GpuChoice.Discrete, OniMoono.Host.GpuChoice.Integrated]),
            new(RendererRow, General, "Renderer", "Which graphics API the game draws with. Needs a restart.",
                "Auto: Vulkan, falling back to Direct3D 11 (Windows), then OpenGL, if one can't start on this PC; on integrated graphics Direct3D 11 comes first. Vulkan: Vulkan whenever it starts (also a software one). "
                + "Direct3D 11 (Windows): the original game's API on Windows. OpenGL: its API on Linux. The picture is the same; Player.log names the one used and why.",
                IsTweak: false, Choices: OniMoono.Desktop.Renderers.TweakChoices(OperatingSystem.IsWindows()), CatalogChoices: OniMoono.Desktop.Renderers.TweakChoices(windows: true)),
            new(DxgiRow, General, "Present through Direct3D on Windows", "Windows shows the game as it shows the original: no forced HDR or brightness. Needs a restart.",
                "With Vulkan or OpenGL, frames reach the screen through a Direct3D 11 swap chain, as in the original. Off: "
                + "the renderer presents them, and Auto HDR or RTX HDR can force HDR on the game. "
                + "If Direct3D can't start, the renderer presents and Player.log says why. No effect on Linux.",
                IsTweak: false),
            new(StallRepeatsRow, General, "Steady display during saves and loads", "G-Sync and FreeSync monitors don't flicker while the game saves or loads.",
                "When no new frame arrives, the last one is shown again 30 to 60 times a second, so the monitor's refresh rate doesn't drop. "
                + "Windows only, with Direct3D presentation on.",
                IsTweak: false),
            new(GcRow, General, "Garbage collection", "How the game frees memory it no longer uses. Needs a restart.",
                "Fast (default): collects garbage on all cores, with rare, short pauses, but uses more memory. "
                + "Low memory: uses less RAM (about 0.8 GB less on a large colony), but pauses more often and loads more slowly. "
                + "Only worth changing if your PC is short on RAM.",
                IsTweak: false, Choices: [OniMoono.Host.GcMode.Fast, OniMoono.Host.GcMode.LowMemory]),
            new("fast-load-screen", General, "Faster Load screen", "With many saves, the Load screen opens and loads more colonies sooner.",
                "Each save's details are read once per refresh instead of four times, several files at a time, and previews already shown are not made again. "
                + "The colonies, their order, the previews and the log are the same.", LoadScreenGain),
            new(LoadingAnimation, General, "Moving loading screen", "The loading screen keeps moving while a colony loads.",
                "While a save loads, the game shows no new picture, for half a minute on a large colony. "
                + "Moono keeps the loading screen moving, with a progress bar, and plays animated loading art from mods such as Loading Pip. "
                + "Loading takes as long as before."),
            new(FmodStealFix, General, "Stolen sound crash guard", "Fixes a bug in the game's sound engine that can crash the game when many sounds play at once.",
                "When too many copies of a sound play, FMOD, the sound engine, stops the oldest. If that one is still loading, FMOD frees it and then writes to it. "
                + "This skips that write by patching FMOD's code in memory, only for the exact FMOD files the game ships. Nothing you hear changes."),
            new(ExactWorldgen, General, "Worldgen as in the original", "A seed makes the same map as in the original game, so shared seeds match.",
                "Worldgen does its decimal maths at the precision the original game uses, rounding only where a value is kept. "
                + "Off: biome borders on a new map can come out slightly different. Maps already generated and saves are not affected."),

            new(FastSerialization, Saving, "Faster save writing", "Autosaves take less time; the save file is the same.",
                "Writes the colony's numbers without boxing each one in an object."),
            new("fast-save-type-checks", Saving, "Faster save checks", "Autosaves skip repeating the same checks for every object.",
                "Remembers what saving asks about each kind of component instead of asking again for every object."),
            new(FastSaveCompression, Saving, "Faster save compression", "Saving and loading take seconds less; files stay compatible, a little larger.",
                "Compresses with .NET's zlib instead of the game's; the files are about 14% larger."),
            new(BackgroundSave, Saving, "Background saving", "The save is compressed and written to disk while the game plays on.",
                "Compression and the file write move to another thread; quitting waits for them."),
            new(NoSaveGc, Saving, "No cleanup pause when saving", "Skips the memory cleanup the game forces after each save, which pauses the game.",
                "Skips the full garbage collection the game asks for after saving."),
            new(PostLoadGc, Saving, "Memory cleanup during loading", "Collects garbage during the loading screen, avoiding a large hitch shortly after the game starts.",
                "The memory cleanup that would pause the game about a minute into play runs at the end of the load instead, while the game is still paused. "
                + "Only with Fast garbage collection (the default).", PostLoadGain),

            new("fast-jobs", Simulation, "Faster worker threads", "Less time lost waking idle worker threads every frame.",
                "Wakes only as many of the game's worker threads as a job has work for, instead of all of them for every job (about 14 a frame).", JobsGain),
            new("fast-sublimation", Simulation, "Faster sublimation", "Items that can't sublimate (polluted dirt in full gas) are skipped.",
                "Items already blocked by full gas cells are skipped in one batched update."),
            new(FastParticleCollision, Simulation, "Faster radbolt collisions", "Radbolts look for each other only where one can be.",
                "A radbolt skips searching every stored item nearby when no other radbolt is close."),
            new("fast-offset-tables", Simulation, "Faster pickup checks", "Where an item can be picked up from is rechecked only when its surroundings change.",
                "Fetch updates recheck only the items whose work spots can have changed."),
            new("fast-partitioner-cleanup", Simulation, "Faster spatial index cleanup", "The once-a-second tidy-up of what is where looks only at what was removed.",
                "The spatial index's cleanup visits only the entries that were removed."),
            new("fast-partitioner-events", Simulation, "Faster item moves on the map", "An item changing tile no longer looks through every item near it.",
                "When an item moves or is stored, the game looks through every item nearby for listeners on that tile. "
                + "Items have no listeners, so this skips the search and keeps only its cleanup of removed entries."),
            new("fast-conduit-sorts", Simulation, "Faster pipe and rail updates", "Pipes and rails no longer re-sort every building's update each second.",
                "Each second the game sorts the list of pipe and rail buildings by priority again; this repeats the reordering its last sort made "
                + "while the priorities are the same, which gives the same order."),
            new("fast-conveyor-paths", Simulation, "Faster conveyor rebuilds", "Building or removing conveyor rails no longer stalls the game.",
                "The rails' network rebuild finds its routes from an index instead of searching every kept route again.", ConveyorGain),
            new(FastPipeRebuilds, Simulation, "Faster pipe network rebuilds", "Building or removing pipes no longer makes every pipe in the colony check its network.",
                "After a gas or liquid network changes, only the pipes of that kind recheck whether their network has a building taking from it, "
                + "all through one handler instead of one or two for every pipe. The status items and the Pipe Flow arrows come out the same.", PipesGain),
            new(CompactNavLinks, Simulation, "Compact navigation links", "The map of where Duplicants and critters can move takes a fraction of the memory.",
                "Each pathfinding grid keeps a cell's moves packed together instead of a fixed row of slots for every cell of the map; "
                + "the pathfinder reads the same moves in the same order."),
            new("fast-smi-lookups", Simulation, "Faster state machine lookups", "Finding one of an object's state machines no longer searches all of them each time.",
                "Each object remembers which of its state machines answered each lookup until its list of state machines changes."),
            new("fast-save-roots", Simulation, "Faster conveyor item moves", "Items moving between rails and buildings no longer search every saved item of their kind.",
                "When an item is stored, the game takes it out of its list of things to save by comparing it with every item of its kind; "
                + "this compares references instead, which finds the same entry."),
            new(PagedPathGrids, Simulation, "Paged path grids", "Each Duplicant's map of path costs takes memory only where it can walk.",
                "A Duplicant's path grid covers the whole map. This keeps it in small pages, each made the first time a path probe writes there. "
                + "The pathfinder reads the same values."),
            new(SharedDuplicates, Simulation, "Share identical data", "Names repeated across a colony's reports and other identical data are kept once, using less memory.",
                "While a save loads, copies of the same text (Duplicants' and critters' names in each day's report) become one. "
                + "Buildings with the same footprint share one table of work spots, and work animations left at their defaults share one list. "
                + "The game never writes to these, so it reads the same values.", SharedGain),
            new(CompactReports, Simulation, "Compact report history", "Past days' reports take a fraction of the memory.",
                "The notes of every day's report but today's are kept in flat arrays instead of a table for each entry; "
                + "the reports screen and saves read the same notes in the same order.", ReportsGain),
            new(CompactReportEntries, Simulation, "Compact report entries", "Past days' report entries take less memory and fewer objects for the game's memory cleanups.",
                "The entries of every day's report but today's are kept in flat arrays instead of an object each, and made again when something reads that day. "
                + "The reports screen, the colony summary and saves read the same values in the same order. Off while a mod reads past days' entries itself.", EntriesGain),
            new(FetchByCell, Simulation, "Fetch checks by pile", "Duplicants looking for things to fetch check each pile of identical items once, not item by item.",
                "Tracks the best item of each pile as items move, are reserved or spoil, and finds the path to each pile once. "
                + "Items on worlds a Duplicant can't reach are skipped. "
                + "Among items in a pile that the game ranks as equal, a Duplicant may take a different one than in the original. "
                + "Off while a mod changes how Duplicants choose what to fetch.", FetchGain, ChangesBehaviour: true),
            new("direct-rail-bridges", Simulation, "Direct conveyor bridge moves", "Conveyor bridges pass an item straight across instead of taking it off the rail and putting it back.",
                "The item only changes rail tile. What the rails carry and when, heat, germs, freshness, egg state and Conveyor Meter counts are the same. "
                + "Two things differ: "
                + "the Conveyor overlay may draw two overlapping items in the other order, and an item is no longer listed as fetchable for the instant it crosses. "
                + "Undiscovered kinds of item, critters and items a mod has added components to cross the game's way. "
                + "Off while another mod changes how items are stored on rails.", ChangesBehaviour: true, OnByDefault: true),

            new(LazyTableRefresh, Graphics, "Update only the table rows in sight", "Consumables, Vitals, Priorities and the other tables update only the rows on screen.",
                "The game updates every row of a table in one frame, once a second and each time it opens, which stutters with many Duplicants. "
                + "Rows out of sight are updated just before they scroll into view instead, so they show values read at that moment. "
                + "Off while a mod changes how tables update.", ChangesBehaviour: true, OnByDefault: true),
            new("fast-table-rows", Graphics, "Tables open sooner", "Consumables, Vitals and Priorities open much sooner on colonies with many Duplicants.",
                "For each cell of a table, the game searches every row and column built so far, and Consumables also goes through every row's scroll bar. "
                + "This notes a row's cells as the row is built and sets the scroll bars once a row. The table comes out the same."),

            new("fast-anim-events", Graphics, "Faster animation events", "Animations check their sound and effect cues with less work.",
                "Keeps a compact copy of what each playing animation's per-frame event check reads."),
            new("fast-anim-cleanup", Graphics, "Faster animation cleanup", "Removed animations are swept out only where one was removed.",
                "The sweep for destroyed animations skips grid chunks that can't hold any."),
            new("fast-anim-batches", Graphics, "Parallel animation uploads", "Animation data for the graphics card is prepared on several threads.",
                "Animation batches write their instance and symbol data on worker threads, the rest in the game's order."),
            new("fast-symbol-overrides", Graphics, "Fewer animation uploads", "Swapped animation parts are re-sent to the graphics card only when they change.",
                "Unchanged symbol-override blocks of Duplicants and buildings aren't uploaded again."),
            new("trim-anim-lists", Graphics, "Trimmed animation lists", "The game's animation data takes about 65 MB less memory.",
                "Once the animations are loaded, the lists of their frames and frame parts give back the room they grew into but don't use; "
                + "the game reads the same data."),
            new("fast-overlay-removals", Graphics, "Smoother decor overlay", "The Decor overlay no longer stutters when items are stored or used up.",
                "While the Decor overlay is open, the game takes each stored or removed item out of the overlay's index by comparing it with every entry; "
                + "this compares references instead, which finds the same entries."),

            new("path-probe-repair", ChangesGameplay, "Incremental path probes", "Duplicants' path checks redo only what changed in the world, using less CPU.",
                "Each Duplicant's last path probe is repaired around what changed (digging, building, doors, liquids) instead of redone from scratch. "
                + "Costs and reachable cells stay the same, but where two routes cost the same a Duplicant may walk a different one than in the original. "
                + "Uses about 0.9 MB per Duplicant. Off while another mod changes path probing (Fast Track's Cache Paths).", OnByDefault: true),
            new(InstantReachability, ChangesGameplay, "Instant reachability checks", "Duplicants notice a newly opened or blocked path at once, using less CPU.",
                "Errands and items are rechecked where and when a Duplicant's reach changes, instead of all of them once a second. "
                + "The answers are the same, up to 1 s sooner. Items it can't track (from some mods) are still checked every second.", OnByDefault: true),
            new("off-screen-conveyors", ChangesGameplay, "Lazy unseen conveyor items", "Items riding rails off screen are placed only when they stop or come into view.",
                "The rails carry exactly the same things, but items' positions lag while off screen."),
            new(SimStepPace, ChangesGameplay, "Simulation at its own pace", "Above 60 fps at normal speed, frames no longer wait for the simulation, and it uses less CPU.",
                "Above 60 fps at normal speed, the game calls the simulation on every frame between its five steps a second, and each call waits for the step underway. "
                + "This skips those calls, as the game does below 60 fps and at faster speeds. "
                + "Dug tiles, built tiles and other changes the game asks of the simulation then show about 0.3 s later (up to 0.4 s), as below 60 fps. "
                + "Off while paused, or while another mod changes these calls.", PaceGain),
            // One row for every fix of this kind (OniMoono.GameTweaks.LowFpsTiming): a new one is a part of it, not a row.
            new(LowFpsTiming, ChangesGameplay, "Accurate timing at low FPS", "At low frame rates or high speeds, animations, timers and automation keep pace with the simulation.",
                "On long frames and at high game speeds, some game timing drifts from the simulation: automation gates and one-tick signals, scalding and overheating damage, work that waits on an animation, and similar. "
                + "This keeps it in step with the simulation, as at a high frame rate, while the camera and menus keep real time. "
                + "Each part turns off if a mod changes the code it relies on."),
            new("brain-rate", ChangesGameplay, "Cap critter and dupe brain updates", "Above 30 fps, critters and Duplicants decide what to do only as often as at 30 fps.",
                "The game updates 1 Duplicant and 5 critter brains every frame; this caps them at 30 and 150 a second. "
                + "One that just finished an errand or is under threat still gets the next update, and a critter ready to inhale food, excrete or attack goes ahead of the others, so critters act at other moments than without this. "
                + "With many critters or Duplicants the cap rises with game speed, so each is updated within 15 seconds of game time."),
            new("critter-needs-first", BugFixes, "Critters ready to eat, excrete or attack get the next turn", "In big colonies at low frame rates, Slicksters and Pufts keep excreting and Beetas still attack.",
                "The game lets 5 critters decide what to do each frame, in a fixed order. "
                + "With thousands of critters at a low frame rate, one that inhales its food gets too few turns to stay fed, so it never excretes: "
                + "its stomach fills for cycles and empties all at once after a load. "
                + "A critter also notices a target only on its turn, so at speed 3 Beetas and other critters that attack often miss what passes by. "
                + "This gives a critter ready to inhale or excrete one of the frame's turns ahead of the others, "
                + "and a critter with a target in reach a turn as often as at normal speed and 30 fps. "
                + "No turns are added, so other critters wait slightly longer."),
            new("off-world-fallers", BugFixes, "Land items lost below a world", "Items that fall out the bottom of a world (a rocket's chute) stop falling forever.",
                "A bug in the game: they hang just below the world, stepped as falling every tick. This lands them where they hang."),
            new("fix-load-screen-clicks", BugFixes, "Open a colony once on the Load screen", "After Load more, a click on a colony no longer builds its list of saves again for every refresh.",
                "A bug in the game: "
                + "each Load more adds another copy of the click to the colonies already shown, so one click builds the colony once for every refresh. "
                + "This builds it once and logs once; the screen ends up the same.", OnByDefault: true),
            new(SoundPiles, BugFixes, "Cap repeated sounds in one tile", "Hundreds of critters in one tile no longer freeze the game with their sounds.",
                "When more than 3 copies of the same sound start in the same tile in the same frame, the rest are skipped. "
                + "A big pile of critters sounds quieter; anywhere else this almost never happens. UI and looping sounds are never skipped.", OnByDefault: true),
            new(TimelapseSnapshots, BugFixes, "Correct daily colony snapshots", "Colony snapshots saved each cycle no longer get vertical stripes, squeezed lights or stray effects.",
                "A bug in the game: the build grid, the lights and the heat, cold and dust effects are set up for the screen's view, which is wider than a snapshot's. "
                + "With a building picked for placement, the grid draws stripes over the left of the image. This sets them up for the snapshot's view.", OnByDefault: true),
            new(TableScroll, BugFixes, "Keep table columns where they were scrolled", "Scrolled table columns no longer jump back to the first ones every second or so.",
                "A bug in the game: each row of a table keeps its scroll position in step with the others. "
                + "When the rows are laid out again (every second or so while the columns move), one row's columns go back to the start and the game moves every row there. "
                + "This keeps the table's scroll position when a row is laid out.", OnByDefault: true),
        ];

        /// The tweaks in src/OniMoono.GameTweaks (named here: options are read before that assembly is loaded).
        public static readonly string[] Typed = ["off-screen-conveyors", "fast-save-type-checks", "fast-anim-cleanup", "fast-sublimation", "fast-anim-events", "fast-symbol-overrides", "fast-jobs", "fast-offset-tables", InstantReachability, "off-world-fallers", "fast-partitioner-cleanup", "fast-partitioner-events", "fast-conduit-sorts", "fast-anim-batches", "fast-conveyor-paths", "brain-rate", "critter-needs-first", SimStepPace, LowFpsTiming, "path-probe-repair", "fast-load-screen", "fix-load-screen-clicks", "fast-smi-lookups", "fast-save-roots", "trim-anim-lists", SoundPiles, TimelapseSnapshots, TableScroll, FetchByCell, SharedDuplicates, LazyTableRefresh, "fast-table-rows", CompactReports, CompactReportEntries, FastPipeRebuilds, "fast-overlay-removals", "direct-rail-bridges"];

        /// Guarded patches of the game that are no Moono Tweaks row and always install, checked as
        /// every tweak is: skip-own-workshop-item (OniMoono.GameTweaks.OwnWorkshopItem),
        /// wait-timelapse-writes (OniMoono.GameTweaks.TimelapseWrites) and the sim-data-check
        /// diagnostic (OniMoono.GameTweaks.SimDataCheck, #91).
        public static readonly string[] AlwaysOn = ["skip-own-workshop-item", "wait-timelapse-writes", "sim-data-check"];

        /// Every `--game-tweak` name, in the screen's order.
        public static readonly string[] All = Rows.Where(r => r.IsTweak).Select(r => r.Id).ToArray();

        /// The main menu's Moono Tweaks entry and screen (OniMoono.GameTweaks.MoonoTweaksMenu's
        /// `Name`): checked and installed as a tweak is (TweakInstall), but not one: no row, no
        /// `--game-tweak` name. Off, the game and the tweaks run without it.
        public const string Menu = "moono-tweaks-menu";

        /// What TweakInstall checks against the game: every tweak, the menu and AlwaysOn.
        public static readonly string[] Checked = [.. All, Menu, .. AlwaysOn];

        /// The tweaks that change what the game does (each documented in its class); `exact` turns
        /// on every tweak but these.
        public static readonly string[] ChangingBehaviour = Rows.Where(r => r.IsTweak && (r.Group.ChangesGameplay || r.ChangesBehaviour)).Select(r => r.Id).ToArray();

        /// The tweaks off by default: those that change what the game does, but those on by
        /// default all the same (OnByDefault).
        public static readonly string[] OffByDefault = Rows.Where(r => r.IsTweak && (r.Group.ChangesGameplay || r.ChangesBehaviour) && !r.OnByDefault).Select(r => r.Id).ToArray();

        /// Tweaks this build no longer has, and the tweak that took each one's place (null: none, the
        /// game fixes it now). A launch option naming one names its replacement instead, or is
        /// ignored, with a line on stderr (Options); a settings file's entry is ignored and kept, as
        /// any unknown tweak's (PlayerSettings). fix-endless-sounds: U60 762107's sound sheets loop
        /// both rows. fix-disease-overlay-crash: U60 762230's CellChangeMonitor drops a destroyed
        /// object's entry itself. slow-reachability (checked every 4 s instead of every second):
        /// instant-reachability checks only what changed, sooner and for less. fmod-stale-handles
        /// (every call on a destroyed FMOD instance skipped) gave way to fix-released-sounds,
        /// which cleared the handles where the game kept them, and that one left with U60 764961,
        /// whose AmbienceManager.Layer.Stop and LoopingSoundManager.StopAllSounds clear them.
        /// fmod-quarantine (FMOD allocating from a heap of Moono's that held freed blocks back and
        /// checked them) guarded against the writes in freed memory those stale handles caused,
        /// which 764961 no longer makes; it caught no real write in 188 logged test runs and cost
        /// about 90 MB on a large colony.
        public static readonly (string Old, string? New)[] Retired = [("fix-endless-sounds", null), ("fix-disease-overlay-crash", null), ("slow-reachability", InstantReachability), ("fmod-stale-handles", null), ("fix-released-sounds", null), ("fmod-quarantine", null)];

        /// Whether a `--game-tweak` value names a retired tweak, with the value naming its
        /// replacement (with its `-` when the name has one), or null when none took its place.
        public static bool IsRetired(string name, out string? replacement)
        {
            bool minus = name.StartsWith('-');
            var bare = minus ? name.Substring(1) : name;
            foreach (var (old, successor) in Retired)
                if (old == bare)
                {
                    replacement = successor == null ? null : minus ? "-" + successor : successor;
                    return true;
                }
            replacement = null;
            return false;
        }

        /// A `--game-tweak` value: a tweak's name, `-NAME` (left out of `all` or `exact`, for
        /// measuring one against the rest), `all`, `exact` (all but ChangingBehaviour) or `none`
        /// (no tweak unless one is named, whatever the settings file says: test runs).
        public static bool IsKnown(string name) => name is "all" or "exact" or "none" || Array.IndexOf(All, name.StartsWith('-') ? name.Substring(1) : name) >= 0;

        /// A row's gain as its tooltip shows it (the screen styles it), with the gain it comes
        /// from: the row's own, capitalized, or its group's ("Saving together: ..."); null for none.
        public static (TweakGain Gain, string Text)? GainText(TweakInfo row) =>
            row.Gain != null ? (row.Gain, char.ToUpperInvariant(row.Gain.Effect[0]) + row.Gain.Effect[1..])
            : row.Group.Gain != null ? (row.Group.Gain, $"{row.Group.Title} together: {row.Group.Gain.Effect}")
            : null;

        /// docs/TWEAKS.md's list of rows, between its `<!-- tweaks:begin -->` and
        /// `<!-- tweaks:end -->` markers.
        public static string Markdown()
        {
            var md = new System.Text.StringBuilder();
            md.Append("<!-- tweaks:begin: generated from src/OniMoono.Host/GameTweaks/GameTweakNames.cs; ONI_UPDATE_DOCS=1 with the unit tests rewrites it -->\n");
            foreach (var group in Groups)
            {
                var rows = Rows.Where(r => r.Group == group && r.IsTweak).ToArray();
                if (rows.Length == 0) continue;
                string defaults = !group.ChangesGameplay ? "on by default" : rows.Any(r => r.OnByDefault) ? "off by default but where marked" : "off by default";
                md.Append($"\n### {group.Title} ({defaults})\n\n");
                // The group's gain is that of the rows without one of their own.
                var own = rows.Where(r => r.Gain != null).Select(r => $"`{r.Id}`").ToArray();
                if (group.Gain != null)
                    md.Append(own.Length == 0 ? $"All {rows.Length} together" : $"All but {string.Join(", ", own)} together").Append($": {Gain(group.Gain)}.\n\n");
                foreach (var r in rows)
                {
                    md.Append($"- **{r.Title}** (`{r.Id}`): {r.Effect}");
                    if (r.Detail.Length > 0) md.Append(' ').Append(r.Detail);
                    if (r.Gain != null) md.Append($" *Measured: {Gain(r.Gain)}.*");
                    if (r.ChangesBehaviour && !group.ChangesGameplay && !r.OnByDefault) md.Append(" *Off by default.*");
                    if (r.OnByDefault) md.Append(r.ChangesBehaviour && !group.ChangesGameplay ? " *On by default; left out of `exact`.*" : " *On by default.*");
                    md.Append('\n');
                }
            }
            md.Append("<!-- tweaks:end -->");
            return md.ToString();
        }

        static string Gain(TweakGain g) => $"{g.Effect}; {g.Colony}, {g.Platform}, {g.Measured}";
    }
}
