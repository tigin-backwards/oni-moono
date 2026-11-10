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
    /// for the row's tooltip and the docs. `IsTweak` is false for the player's own options (the
    /// splash, frame stats, log paths and the GPU), which are rows but not `--game-tweak` names.
    /// `Choices`: a row with more values than on and off (the GPU's), in the order its button
    /// steps through them; null for a toggle. `ChangesBehaviour`: a tweak outside the groups
    /// that change gameplay (Changes gameplay, Bug fixes) that still changes what the game does a little (off by default, left out
    /// of `exact`). `OnByDefault`: a tweak that changes what the game does (left out of `exact`)
    /// but is on by default all the same (the player's choice: path-probe-repair,
    /// fix-load-screen-clicks, fix-endless-sounds, fix-sound-piles, fix-geothermal-vents).
    internal sealed record TweakInfo(string Id, TweakGroup Group, string Title, string Effect, string Detail = "", TweakGain? Gain = null, bool IsTweak = true,
        string[]? Choices = null, bool ChangesBehaviour = false, bool OnByDefault = false)
    {
        /// The launch option that sets this row (shown in its tooltip); "" for none (update checks).
        public string LaunchOption => Id switch
        {
            GameTweakNames.UpdateCheckRow => "",
            GameTweakNames.SplashRow => "--splash moo",
            GameTweakNames.FrameStatsRow => "--frame-stats",
            GameTweakNames.LogPathsRow => "--log-paths hidden",
            GameTweakNames.GpuRow => "--gpu " + string.Join("|", Choices!),
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
        public const string SplashRow = "splash", FrameStatsRow = "frame-stats", LogPathsRow = "log-paths", GpuRow = "gpu", DxgiRow = "dxgi", StallRepeatsRow = "stall-repeats", UpdateCheckRow = "update-check", GcRow = "gc";
        public const string FastSaveCompression = "fast-save-compression", BackgroundSave = "background-save", NoSaveGc = "no-save-gc", PostLoadGc = "post-load-gc",
            FastSerialization = "fast-serialization", FastParticleCollision = "fast-particle-collision", CompactNavLinks = "compact-nav-links",
            PagedPathGrids = "paged-path-grids", FetchByCell = "fetch-by-cell", InstantReachability = "instant-reachability", EndlessSounds = "fix-endless-sounds", SoundPiles = "fix-sound-piles", GeothermalVents = "fix-geothermal-vents",
            ExactWorldgen = "exact-worldgen", SharedDuplicates = "shared-duplicates", CompactReports = "compact-report-history",
            FmodQuarantine = "fmod-quarantine";

        // The gains shown in the screen's tooltips. Measured on Linux under WSL2, where the game's
        // code ran about half as fast as on Windows (docs/PERFORMANCE.md, "How it's measured"); a
        // Windows remeasure is pending: update Effect, Platform and Measured together (and
        // PERFORMANCE.md).
        static readonly TweakGain SaveGain = new("SAVE", "autosave freeze ~12 s → ~5 s on a large colony",
            "a late-game Spaced Out colony with every DLC (cycle 17,000+)", "Linux under WSL2", "by 2026-10-20", "docs/PERFORMANCE.md#the-tweaks (save tweaks: 11.7 → 4.8 s)");
        static readonly TweakGain JobsGain = new("JOBS", "+11% frame rate on a large colony",
            "a late-game Spaced Out colony with every DLC (cycle 17,000+)", "Linux under WSL2", "by 2026-10-20", "docs/PERFORMANCE.md#the-tweaks (fast-jobs)");
        static readonly TweakGain OverlaysGain = new("OVERLAYS", "temperature overlay ~40% cheaper",
            "a late-game Spaced Out colony with every DLC (cycle 17,000+)", "Linux under WSL2", "2026-10-24", "docs/research/overlays.md (Infrared.Update 10.44 → 6.0 ms; 2minnow 6.53 → 3.9)");
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

        public static readonly TweakGroup General = new("GENERAL", "General");
        public static readonly TweakGroup Saving = new("SAVING", "Saving", SaveGain);
        public static readonly TweakGroup Simulation = new("SIMULATION", "Simulation");
        public static readonly TweakGroup Graphics = new("GRAPHICS", "Graphics & overlays");
        /// The tweaks that change what the game does for speed: their own group, in the warning colour.
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
            new(UpdateCheckRow, General, "Update check", "The main menu shows when a newer Moono is out.",
                "Asks GitHub for Moono's list of releases (api.github.com) at most once every 6 hours, in the background, sending nothing about you or your game. "
                + "The answer is kept in the oni-moono data folder. Off: Moono makes no network requests of its own.", IsTweak: false),
            new(FrameStatsRow, General, "Frame stats in the log", "Notes where the frame time goes in Player.log, for reporting slowness.",
                "Every 60 frames.", IsTweak: false),
            new(LogPathsRow, General, "Hide personal paths in the log", "Your user folder reads %USERPROFILE% or ~ in Player.log, and your account IDs and name are hidden.",
                "Mods still get the full text.", IsTweak: false),
            new(GpuRow, General, "Graphics card", "For laptops with two GPUs: which one the game draws with. Needs a restart.",
                "Default: as the original game (on Windows the faster, discrete GPU; on Linux the system's choice). Discrete: the faster GPU. "
                + "Integrated: the power-saving one, on Linux; on Windows, choose it in Windows' Graphics settings, which win over this. "
                + "On Linux, on a desktop whose display is on the discrete card, discrete may pick the integrated GPU.",
                IsTweak: false, Choices: [OniMoono.Host.GpuChoice.Default, OniMoono.Host.GpuChoice.Discrete, OniMoono.Host.GpuChoice.Integrated]),
            new(DxgiRow, General, "Present through Direct3D on Windows", "Windows shows the game as it shows the original: no forced HDR or brightness. Needs a restart.",
                "Frames go to the screen through a Direct3D 11 swap chain, as the original's do. Off: through OpenGL's own presentation, "
                + "which Auto HDR or RTX HDR can take for HDR. If it can't start, OpenGL's own is used and Player.log says why. No effect on Linux.",
                IsTweak: false),
            new(StallRepeatsRow, General, "Steady display during saves and loads", "G-Sync and FreeSync monitors don't flicker while the game saves or loads.",
                "While no new frame comes for a moment, the last one is shown again at about the game's frame rate (30 to 60 times a second), "
                + "so the monitor's refresh rate doesn't drop. Nothing changes while frames come as usual. Windows, with Direct3D presentation on.",
                IsTweak: false),
            new(GcRow, General, "Garbage collection", "How the game frees memory it no longer uses. Needs a restart.",
                "Fast (default): collects garbage on all cores, with rare, short pauses, but uses more memory. "
                + "Low memory: uses less RAM (about 0.8 GB less on a large colony), but pauses more often and loads more slowly. "
                + "Only worth changing if your PC is short on RAM.",
                IsTweak: false, Choices: [OniMoono.Host.GcMode.Fast, OniMoono.Host.GcMode.LowMemory]),
            new("fast-load-screen", General, "Faster Load screen", "With many saves, the Load screen opens and loads more colonies sooner.",
                "Each save's details are read from its file once a refresh instead of four times, several files at a time, and the previews of pages already shown are not made again. "
                + "The colonies, their order, the previews and the log are the same.", LoadScreenGain),
            new(FmodQuarantine, General, "Sound memory guard", "Guards against a bug in the game's sound engine that can crash the game.",
                "FMOD, the sound engine, can write to memory it has already freed, a bug its makers confirmed, behind some \"heap corruption\" crashes. "
                + "With this, FMOD gets memory of its own and freed memory waits a while before reuse, so a late write lands where nothing else is, "
                + "and Player.log notes each one found. Uses about 50 MB more memory on a large colony. Windows only for now."),
            new(ExactWorldgen, General, "Worldgen as in the original", "A seed makes the same map as in the original game, so shared seeds match.",
                "Worldgen does its decimal maths at the precision the original game uses, rounding only where a value is kept. "
                + "Off: biome borders on a new map can come out slightly different. Maps already generated and saves are not affected."),

            new(FastSerialization, Saving, "Faster save writing", "Autosaves take less time; the save file is the same.",
                "Writes the colony's numbers without boxing each one in an object."),
            new("fast-save-type-checks", Saving, "Faster save checks", "Autosaves skip repeating the same checks for every object.",
                "Remembers what saving asks about each kind of component instead of asking again for every object."),
            new(FastSaveCompression, Saving, "Faster save compression", "Saving and loading take seconds less; files stay compatible, a little larger.",
                "Compresses with .NET's zlib instead of the game's own; the files are about 14% larger."),
            new(BackgroundSave, Saving, "Background saving", "The save is compressed and written to disk while the game plays on.",
                "Compression and the file write move to another thread; quitting waits for them."),
            new(NoSaveGc, Saving, "No cleanup pause when saving", "Skips the memory cleanup the game forces after each save, a pause of its own.",
                "Skips the full garbage collection the game asks for after saving."),
            new(PostLoadGc, Saving, "Memory cleanup during loading", "Collects garbage during the loading screen, avoiding a large hitch shortly after the game starts.",
                "Runs the memory cleanup that would otherwise pause the game about a minute into play at the end of the load, while the game is still paused. "
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
            new("fast-conveyor-paths", Simulation, "Faster conveyor rebuilds", "Building or removing conveyor rails no longer stalls the game.",
                "The rails' network rebuild finds its routes from an index instead of searching every kept route again.", ConveyorGain),
            new(CompactNavLinks, Simulation, "Compact navigation links", "The map of where Duplicants and critters can move takes a fraction of the memory.",
                "Each pathfinding grid keeps a cell's moves packed together instead of a fixed row of slots for every cell of the map; "
                + "the pathfinder reads the same moves in the same order."),
            new("fast-smi-lookups", Simulation, "Faster state machine lookups", "Finding one of an object's state machines no longer searches all of them each time.",
                "Each object remembers which of its state machines answered each lookup until its list of state machines changes."),
            new(PagedPathGrids, Simulation, "Paged path grids", "Each Duplicant's map of path costs takes memory only where it can walk.",
                "A Duplicant's path grid covers the whole map; it is kept in small pages made the first time a probe writes there, "
                + "and the parts never written read as the empty grid did. The pathfinder reads the same values."),
            new(SharedDuplicates, Simulation, "Share identical data", "Names repeated across a colony's reports and other identical data are kept once, using less memory.",
                "While a save loads, every copy of the same text (Duplicants' and critters' names in each day's report) becomes one; "
                + "buildings with the same footprint share one table of the spots they are worked from, and work animations left at their defaults share one list. "
                + "Nothing is written into them, so the game reads the same values.", SharedGain),
            new(CompactReports, Simulation, "Compact report history", "Past days' reports take a fraction of the memory.",
                "The notes of every day's report but today's are kept in flat arrays instead of a table for each entry; "
                + "the reports screen and saves read the same notes in the same order.", ReportsGain),
            new(FetchByCell, Simulation, "Fetch checks by pile", "Duplicants looking for things to fetch check each pile of identical items once, not item by item.",
                "Keeps the best item of each pile up to date as items move, are reserved or spoil, and finds the path to each pile once. Items on worlds a Duplicant can't reach are skipped. "
                + "Within a pile of items that are the same in every way the game compares, a Duplicant may take another one than in the original. "
                + "Off while a mod changes how Duplicants choose what to fetch.", FetchGain, ChangesBehaviour: true),

            new("fast-anim-events", Graphics, "Faster animation events", "Animations check their sound and effect cues with less work.",
                "Keeps a compact copy of what each playing animation's per-frame event check reads."),
            new("fast-anim-cleanup", Graphics, "Faster animation cleanup", "Removed animations are swept out only where one was removed.",
                "The sweep for destroyed animations skips grid chunks that can't hold any."),
            new("fast-anim-batches", Graphics, "Parallel animation uploads", "Animation data for the graphics card is prepared on several threads.",
                "Animation batches write their instance and symbol data on worker threads, the rest in the game's order."),
            new("fast-symbol-overrides", Graphics, "Fewer animation uploads", "Swapped animation parts are re-sent to the graphics card only when they change.",
                "Unchanged symbol-override blocks of Duplicants and buildings aren't uploaded again."),
            new("fast-overlays", Graphics, "Faster overlays", "Overlays skip rereading what can't have changed since the last frame.",
                "Temperature passes over buildings out of view without reading them, power asks each circuit's load once a frame, the rest keep what their targets' visits read.", OverlaysGain),
            new("fast-overlay-textures", Graphics, "Overlay texture cleanup", "Opening and closing overlays no longer piles up video memory.",
                "The screen-sized textures the game remakes on each coloured overlay toggle are freed, instead of kept until the next load (about 18 MB a toggle at 1024×768)."),

            new("path-probe-repair", ChangesGameplay, "Incremental path probes", "Duplicants' path checks redo only what changed in the world, using less CPU.",
                "Each Duplicant's last path probe is repaired around what changed (digging, building, doors, liquids) instead of searched again from scratch. "
                + "Costs and reachable cells stay the same; where two routes cost the same, a Duplicant may walk the other one, not the original's. "
                + "Keeps about 0.9 MB a Duplicant. Off while another mod changes path probing (Fast Track's Cache Paths).", OnByDefault: true),
            new(InstantReachability, ChangesGameplay, "Instant reachability checks", "Duplicants notice a newly opened or blocked path at once, using less CPU.",
                "Which errands and items can be reached is rechecked where and when a Duplicant's reach changes, instead of every one of them once a second. "
                + "The answers are the game's, up to 1 s sooner. Items it can't follow (some mods' own) are still checked every second.", OnByDefault: true),
            new("off-screen-conveyors", ChangesGameplay, "Lazy unseen conveyor items", "Items riding rails off screen are placed only when they stop or come into view.",
                "The rails carry exactly the same things, but items' positions lag while off screen."),
            new("brain-rate", ChangesGameplay, "Critter and dupe brain updates capped at 30 updates a second", "Critters and dupes would otherwise update on every frame. This trims that bookkeeping above 30 fps.",
                "The game updates 1 Duplicant and 5 critter brains every rendered frame; this caps them at 30 and 150 a second, spread over frames. One that just finished an errand or is under threat still gets the next update. Saves time only above 30 fps."),
            new("off-world-fallers", BugFixes, "Land items lost below a world", "Items that fall out the bottom of a world (a rocket's chute) stop falling forever.",
                "A bug in the game: they hang just below the world, stepped as falling every tick. This lands them where they hang."),
            new("fix-load-screen-clicks", BugFixes, "Open a colony once on the Load screen", "After Load more, a click on a colony no longer builds its list of saves again for every refresh.",
                "A bug in the game's own code: each Load more adds another copy of the click to the colonies already shown, so one click builds the colony once for every refresh, "
                + "and the last one stays on screen. With this, one click builds it once and logs once; the screen ends up the same.", OnByDefault: true),
            new(EndlessSounds, BugFixes, "Stop sounds that never end", "An eating Pip no longer leaves sounds playing forever, which in time froze the game.",
                "A bug in the game's sound data: the adult Pip's eating loop, and the debris left by a self-destructed rocket, start a new copy of a looping sound on every loop "
                + "and never stop one. With this they play as the baby Pip's does: one copy while the animation runs, stopped when it ends.", OnByDefault: true),
            new(SoundPiles, BugFixes, "Cap repeated sounds in one tile", "Hundreds of critters in one tile no longer freeze the game with their sounds.",
                "When more than 3 copies of the same sound start in the same tile in the same frame, the rest are skipped. "
                + "A big pile of critters sounds quieter; anywhere else this almost never happens. UI and looping sounds are never skipped.", OnByDefault: true),
            new(GeothermalVents, BugFixes, "Count all a geothermal vent emits", "A geothermal vent's kg remaining goes down as it vents, instead of stalling while it keeps emitting.",
                "A bug in the game's own code, fixed in its next update: a vent reads what it emitted at a time of its own, and at high frame rates it often reads nothing, "
                + "so it vents on and on and clogs its pipes. With this, as in that update, it counts what it emits after every step of the simulation.", OnByDefault: true),
        ];

        /// The tweaks in src/OniMoono.GameTweaks (named here: options are read before that assembly is loaded).
        public static readonly string[] Typed = ["off-screen-conveyors", "fast-save-type-checks", "fast-anim-cleanup", "fast-sublimation", "fast-anim-events", "fast-symbol-overrides", "fast-jobs", "fast-offset-tables", InstantReachability, "off-world-fallers", "fast-partitioner-cleanup", "fast-anim-batches", "fast-overlays", "fast-conveyor-paths", "fast-overlay-textures", "brain-rate", "path-probe-repair", "fast-load-screen", "fix-load-screen-clicks", "fast-smi-lookups", EndlessSounds, SoundPiles, FetchByCell, GeothermalVents, SharedDuplicates, CompactReports];

        /// Guarded patches of the game that are no Moono Tweaks row and always install, checked as
        /// every tweak is: skip-own-workshop-item (OniMoono.GameTweaks.OwnWorkshopItem).
        public static readonly string[] AlwaysOn = ["skip-own-workshop-item"];

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

        /// Tweaks this build no longer has, and the one that took each one's place: a launch
        /// option naming one names its replacement instead, with a line on stderr (Options); a
        /// settings file's entry is ignored and kept, as any unknown tweak's (PlayerSettings).
        /// slow-reachability (checked every 4 s instead of every second): instant-reachability
        /// checks only what changed, sooner and for less.
        public static readonly (string Old, string New)[] Replaced = [("slow-reachability", InstantReachability)];

        /// The tweak replacing a retired one (with its `-` when the name has one), or null.
        public static string? ReplacedBy(string name)
        {
            bool minus = name.StartsWith('-');
            var bare = minus ? name.Substring(1) : name;
            foreach (var (old, replacement) in Replaced)
                if (old == bare) return minus ? "-" + replacement : replacement;
            return null;
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
                    if (r.ChangesBehaviour && !group.ChangesGameplay) md.Append(" *Off by default.*");
                    if (r.OnByDefault) md.Append(" *On by default.*");
                    md.Append('\n');
                }
            }
            md.Append("<!-- tweaks:end -->");
            return md.ToString();
        }

        static string Gain(TweakGain g) => $"{g.Effect}; {g.Colony}, {g.Platform}, {g.Measured}";
    }
}
