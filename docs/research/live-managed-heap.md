# The live managed heap of a late-game colony (2026-11-17)

Status: inventory, cost measurements and designs. Candidate 1, past days' report entries, is
built (`compact-report-entries`, 2026-11-17). No engine-owned candidate is both contained and a
clear share of the pause ("Verdict").

A blocking full collection on the late-game colony pauses 0.73–0.78 s on Windows
([gc-pauses.md](gc-pauses.md), "Less garbage from each save"). It marks every live object,
plans and compacts the small-object heap, and fixes up every reference. Its cost follows the
live objects and references it traces, and the small-object bytes it moves, not the garbage.
This study counts what it traces, who owns it, and what could leave the traced heap.

## Method

- **Player.** u60 at f13ee106, release, Linux (WSL2, 16 cores, Server GC with 15 heaps), a
  late-game Spaced Out colony (cycle 17,000+, the e2e fixture), a player's default tweaks.
- **Heap dump.** `dotnet-dump collect --type Heap`, 2 minutes after the load, with
  `post-load-gc` done (11 GB dump, 31 s). It allocated 5 MB/s then, against 20 MB/s in play,
  so little had been played: play's caches (path grid pages, frame data) are smaller than
  after an hour.
- **Analysis.** A ClrMD 4.2 tool (local, `out/lh/HeapInv`; ClrMD 3.1 reads no static field of
  .NET 10). A breadth-first walk from the static fields first, then the handles and stacks. Each
  object gets:
  - its type, generation and non-null reference fields;
  - an owner: the nearest object on its path, itself included, whose type is neither the BCL's
    nor an array nor a closure. What a static holds through BCL objects belongs to the class
    declaring it.
- **Tracing cost.** A console benchmark (`out/lh/GcBench`, local): Server GC with 15 heaps on
  Linux, in runq's perf slot. It builds a structure, then takes the median of five
  `GC.Collect(2, Forced, blocking: true, compacting: true)` pauses (the runtime's own figure).
- **In-game pause.** A local E2eDriver probe ran three compacting and three non-compacting
  blocking full collections after 150 s of play, on the same colony and player.

## What a full collection traces

At the dump: 41.3 M objects, 6.5 GB, of which:

| | Objects | References | Bytes |
|---|---:|---:|---:|
| Live | 29.2 M | 92.1 M | 4.92 GB |
| - small-object heap (gen0–2) | 29.1 M | 83.7 M | 3.62 GB |
| - large-object heap | 0.1 M | 8.4 M | 1.17 GB |
| - pinned and frozen heaps | – | – | 0.14 GB |
| Dead, not yet collected | 9.7 M | – | 1.55 GB |

- 99.5% of the live objects are small. The large-object heap is 24% of the bytes but nearly
  free to trace: mostly arrays of primitives and structs (`FrameElement[]`, `PathFinder+Cell[]`,
  `int[]`, texel copies). It is never compacted.
- The gen2 small-object heap is spread evenly over the 15 heaps (265–273 MB each), so the
  collection's threads share the work.
- The Windows run of gc-pauses.md had 6–7 GB of heap after an hour of play; this dump is the
  same colony right after its load. The shares below should hold; the totals grow with play.

### By owner

| Domain | Owner | Objects | % | References | % | MB | Small-object MB |
|---|---|---:|---:|---:|---:|---:|---:|
| Engine object records (`BehaviourNative`, `GameObjectNative`, `TransformNative`, `ObjectTable`, layouts) | engine | 3,305,239 | 11.3 | 17,686,638 | 19.2 | 606 | 481 |
| Building components (`BuildingHP`, `Deconstructable`, `Repairable`, `Disinfectable`, `DecorProvider`, …) | game | 2,956,991 | 10.1 | 12,839,316 | 13.9 | 356 | 355 |
| Daily reports (`ReportManager+ReportEntry`) | game | 4,312,135 | 14.7 | 8,025,754 | 8.7 | 349 | 348 |
| State machine instances | game | 3,642,057 | 12.5 | 8,244,380 | 9.0 | 211 | 211 |
| Other game components and data | game | 2,510,245 | 8.6 | 8,710,937 | 9.5 | 400 | 262 |
| Event subscriptions (`EventSystem`) | game | 1,391,260 | 4.8 | 9,542,980 | 10.4 | 912 | 868 |
| Anim controllers and batches (`KBatchedAnimController`, `KBatchGroupData`, …) | game | 2,284,516 | 7.8 | 4,781,430 | 5.2 | 591 | 277 |
| Attributes, modifiers, amounts (`Klei.AI`) | game | 1,693,990 | 5.8 | 4,164,505 | 4.5 | 94 | 93 |
| Scene partitioner entries, offset trackers, cell watchers | game | 1,252,590 | 4.3 | 3,447,552 | 3.7 | 111 | 91 |
| Host and game tweaks | ours | 895,869 | 3.1 | 2,645,041 | 2.9 | 113 | 79 |
| Scheduler groups | game | 1,413,655 | 4.8 | 2,117,481 | 2.3 | 46 | 46 |
| `KPrefabID` and tag sets | game | 575,881 | 2.0 | 1,861,813 | 2.0 | 69 | 64 |
| Engine rendering (meshes, property sheets, GL objects) | engine | 664,777 | 2.3 | 1,211,024 | 1.3 | 231 | 130 |
| Status items, selectables | game | 424,973 | 1.5 | 1,296,826 | 1.4 | 40 | 40 |
| Fetch and chores | game | 176,472 | 0.6 | 1,380,475 | 1.5 | 41 | 34 |
| UnityEngine facades (`GameObject`, `Transform`, uGUI events) | engine | 602,576 | 2.1 | 673,426 | 0.7 | 25 | 25 |
| TextMeshPro, uGUI | packages | 389,483 | 1.3 | 862,015 | 0.9 | 95 | 92 |
| Sim, grid, conduits, networks | game | 111,474 | 0.4 | 905,219 | 1.0 | 110 | 72 |
| Path grids and navigation | game, tweaks | 86,602 | 0.3 | 215,449 | 0.2 | 152 | 7 |
| Asset and serialization caches, other engine | engine | 391,302 | 1.3 | 920,203 | 1.0 | 59 | 23 |
| The rest (UI screens, roots, `compact-report-history`'s store) | | 162,643 | 0.6 | 544,777 | 0.6 | 309 | 19 |

The game owns about 80% of the objects and references; the engine about 17%, most of it the
per-object records of what Unity keeps natively. Strings are few (0.5 M, 46 MB):
`shared-duplicates` already folded the reports' copies.

### Largest types

| Type | Owner | Objects | References | MB |
|---|---|---:|---:|---:|
| `ReportManager+ReportEntry` | game | 4,089,290 | 3,883,183 | 294 |
| `OniMoono.Engine.BehaviourNative` | engine | 2,334,613 | 9,355,271 | 280 |
| `int[]` (dictionary buckets, mostly) | both | 815,523 | 0 | 429 |
| `SchedulerGroup` and its `List<SchedulerHandle>` | game | 1,405,182 | 2,107,772 | 45 |
| `KAnimFile[]` | game | 602,245 | 114,907 | 15 |
| `ScenePartitionerEntry` | game | 529,470 | 1,567,788 | 38 |
| `Dictionary<int, SBOVector4<EventSystem+Entry>>` entries | game | 98,605 | 6,545,550 | 586 |
| `ReportManager+ReportEntry[]` | game | 186,687 | 4,089,290 | 53 |
| `object[]` (3.1 M references; 2.8 M in `ObjectTable`'s slots) | both | 351,926 | 3,104,619 | 51 |
| `UnityEngine.Component[]` (each GameObject's components) | engine | 168,374 | 2,557,565 | 25 |
| `KAnim+Anim+FrameElement[]` (large-object heap) | game | 2,184 | 0 | 300 |

## What tracing costs

**Measured in the game** (Linux, the colony after 150 s of play, 5.0 GB heap; three blocking
full collections of each kind in a row):

| Collection | Pauses |
|---|---|
| Compacting, the first (1.5 GB of garbage to sweep, live objects to move) | 1,154 ms |
| Compacting, again (nothing left to move) | 518, 528 ms |
| Not compacting | 352, 317, 317 ms |

- Marking alone takes about 320 ms; planning and fixing up the references of a compacted heap
  add about 200 ms even when nothing moves; moving the objects after a while of play doubles it.
  The 0.73–0.78 s Windows pause is the first kind, on a faster machine.
- **What it follows** (GcBench, the structure's added pause; 15 heaps, a structure allocated
  from one thread unless said):

| Structure | Objects | References | Compacting | Not compacting | Per object |
|---|---:|---:|---:|---:|---|
| Small objects without references | 10 M | 10 M (from arrays) | 43–51 ms | 32–33 ms | 4–5 ns |
| Small objects with 2 random references | 10 M | 30 M | 101–104 ms | 80 ms | 10 ns |
| The same, with as much garbage between them | 10 M | 30 M | 130–150 ms | 88–94 ms | 13–15 ns |
| Small objects with 8 random references | 5 M | 45 M | 158 ms | 135–171 ms | 32 ns |
| References to one object in large arrays | – | 100 M | 57–66 ms | 21–59 ms | 0.2–0.6 ns a reference |
| Large `int[]`s | – | – | 2–6 ms for 1.9 GB | 2–5 ms | – |
| Arrays of structs without references | 20 M records | – | 7–11 ms | 3–8 ms | – |

- So about 5 ns an object, 2.5–3.5 ns a reference to an object elsewhere, under 0.6 ns one to
  an object just visited, and nothing for large arrays of primitives. A structure built on one
  heap compacts much worse (8 random references, 10 M objects: 2.4 s compacting against 0.25 s
  not); the game's gen2 is spread evenly, so this does not apply to it.
- **The model** (5 ns an object, 2.5 ns a reference, 0.04 ns a small-object byte) gives 521 ms
  for this heap's counts, the measured compacting pause with nothing to move. It ranks the
  candidates below. It does not model the first compacting collection's moving, which grows with
  the small-object bytes and garbage.

**Shapes of the candidates, measured** (GcBench, at the colony's counts, allocated on 15
threads; median of seven, Linux):

| Shape | As held now | Compacted | Change |
|---|---|---|---|
| 2.33 M `BehaviourNative`-like records beside their facades, against the fields merged into the facades (168 K game objects, 27 K scripts) | 241 ms (192 not compacting) | 154 ms (121) | −36% (−37%) |
| 4.09 M report entries in 187 K arrays, against arrays of structs with a string index | 94 ms (83) | 10 ms (10) | −89% (−88%) |
| 98.6 K subscription tables, 4 inline slots for each of 46 hashes, 33 used, against 33 packed entries | 99 ms (97) | 72 ms (55) | −27% (−43%) |

The ranges were wide (the "none" control alone ran 4–110 ms); no run was flagged noisy. These
benchmarks pay more per object than the game does (cross-heap references from 15 allocating
threads), so only their ratios carry over: a saving is the ratio applied to the candidate's
share of the game's pause.

## Candidates, ranked

Share: of the modelled work of a full compacting collection, from the counts above (5 ns an
object, 2.5 ns a reference, 0.04 ns a small-object byte; "What tracing costs"). Pause: that share
of 0.75 s, the Windows pause. Saving: what the design removes, measured on its shape where the
table says so, else estimated. All estimates; nothing here was built in the game.

| # | Candidate | Owner | Objects | References | Small-object MB | Share | Of 0.75 s | Saving (design) | Exact | Risk | Size |
|---|---|---|---:|---:|---:|---:|---:|---|---|---|---|
| 1 | Past days' report entries | game | 4.31 M | 8.0 M | 348 | 10.7% | 80 ms | −89% of its share on the shape: ~70 ms | yes, with a field-access rewrite | medium-high | large |
| 2 | Event subscription tables | game | 1.39 M | 9.4 M | 868 | 12.5% | 94 ms | −27 to −43% on the shape: 25–40 ms | yes | medium (hot `Trigger`) | medium |
| 3 | `BehaviourNative` records (and the other component records) | engine | 2.34 M (+0.5 M) | 9.4 M | 280 | 8.9% | 67 ms | 35–45 ms, estimated from the objects and back references it removes (the shape: −36%) | yes | medium | large |
| 4 | State machine instances' eager stacks and arrays; scheduler groups | game | 2.2 M; 1.41 M | 1.4 M; 2.1 M | 120; 46 | 2.1%; 2.7% | 16; 20 ms | most of it, estimated | yes, with a rewrite | medium | medium |
| 5 | Host reflection caches (`PatchDependents`, `PatchInliners`, `MonoMissingMembers`) | engine | 0.64 M | 1.7 M | 49 | 1.8% | 13 ms | 10–13 ms, estimated | yes | low | small |
| – | Other engine records (`ObjectTable`, `Component[]`, `GameObjectNative`, layouts) | engine | 1.0 M | 8.5 M | 207 | 6.6% | 50 ms | shrinks with 3 | – | – | – |
| – | Attributes and modifiers; partitioner entries; anim controllers | game | 1.7; 1.25; 1.08 M | 4.2; 3.3; 2.5 M | 93; 91; 144 | 4.3; 3.5; 3.3% | 32; 26; 25 ms | not designed: one per component | – | – | – |
| – | Large arrays (frame data, path grids, the note store) | game, tweaks | 0.23 M | 0.4 M | 39 | 0.7% | 5 ms | nothing to gain | – | – | – |

The model's total matches the in-game compacting pause; each candidate's pause is an estimate
from its counts, not a measurement in the game.

## Designs

**1. Past days' report entries (game; a stage 2 of `compact-report-history`).**

- *What:* every day's `DailyReport` keeps its `List<ReportEntry>`, about 240 entries a day for
  17,000 days; each entry has its own `contextEntries` list. `compact-report-history` already
  moved their notes into flat arrays (the 285 MB `ReportNoteStore`, nearly free to trace); the
  4.1 M entry objects and their 187 K arrays remain.
- *Design:* at night, move the day that ended into flat struct arrays (type, hash, values, note
  id, context as a string-table index, children as a range); hand out `ReportEntry` objects
  again when something reads that day. Today's report stays as the game keeps it.
- *Exactness:* same values in the same order. The game and mods read
  `DailyReport.reportEntries` as a field (the reports screen, `GetEntry`, the colony summary at
  every save, stats mods), so the reads need a load-time rewrite of the field access, as
  `compact-nav-links` rewrites `NavGrid.Links`, over the game's and the mods' code. An entry
  handed out again is a new object: a mod that keeps one and writes to it would need the
  rewrite to write back. The colony summary walks every day's entries at every save, so it
  needs a path that reads the arrays directly, or each save rebuilds 4 M objects.
- *Size, risk:* large; medium-high (a field-access rewrite over mods, the save path).

*Built (2026-11-17): `compact-report-entries`* (a row of its own, beside
`compact-report-history`; docs/TWEAKS.md, "Past days' report entries"). How it differs from the
design:

- *No field rewrite.* The game reads `reportEntries` itself in four methods only (DailyReport's
  constructor and `GetEntry`, `SaveLoader.GetDailyReportMetrics` after `FindReport`, the stress
  panel's today), checked from its IL at start-up. So patches serve them: a prefix on `GetEntry`
  makes that one entry again (with its children), a postfix on `FindReport` the whole day, and a
  transpiler sends the colony summary's 16 `GetEntry` calls a day to scratch entries filled from
  the arrays. Mods are not rewritten: one that reads the field itself (by reference, as a
  Harmony `___reportEntries` parameter or by name) turns the tweak off for the session. Of the
  18 Workshop mods that name the report types, three do: ONI Multiplayer, ONI MCP Server and
  RemoveDailyReports.
- *Write-back.* Objects given out are kept for the last 32 days read; a day leaving them is
  compared with its arrays and packed again when it was written to.
- *Records:* 40 bytes an entry (4.09 M entries in 165 MB, in 64 K-entry chunks on the large
  object heap), 551 distinct contexts. Saves write the packed days from the arrays through
  `fast-serialization`'s history writer.

*Measured* (master, release build, U59 744825; Linux under WSL2 on a heavily loaded machine, the
late-game colony, a player's default tweaks, one heap dump of each about two minutes after the
load, HeapInv as in "Method"):

| Reachable | Without | With | Change |
|---|---:|---:|---:|
| Objects | 29,596,809 | 25,359,519 | −4,237,290 (−14.3%) |
| References | 85,959,106 | 78,075,547 | −7,883,559 (−9.2%) |
| Small-object heap | 2,982 MB | 2,632 MB | −349 MB (−11.7%) |
| Large-object heap | 2,163 MB | 2,327 MB | +164 MB (the records) |
| All live bytes | 5,275 MB | 5,093 MB | −182 MB |
| `ReportEntry` objects; their arrays | 4,089,286; 186,687 | 350; 19 | |
| GC committed memory (dotnet-counters) | 7.50 GB | 7.02 GB | −0.48 GB |
| Working set | 12.6 GB | 11.7 GB | −0.9 GB (one pair; it varies with play) |

- *The pause, estimated:* the cost model ("What tracing costs": 5 ns an object, 2.5 ns a
  reference, 0.04 ns a small-object byte) gives 461 ms of work before and 407 ms after: −55 ms,
  −12%. As that share of the 0.75 s Windows pause, about 90 ms. Not timed: the machine was under
  heavy load, so no pause, save or screen timing was taken.
- *Exactness:* the unit tests (CompactReportDaysTests) pack, give back and write every shape;
  on the colony, the e2e spec `compact-report-entries` in verify mode read the history's bytes,
  the reports screen paged back eight days, a save and its reload the same with the tweak on as
  with it off, with every given-out entry compared and each save's history bytes compared with
  the game's writer over the original objects (0 mismatches).

**2. Event subscription tables (game; a tweak).**

- *What:* each `KObject`'s `EventSystem` (98,660) holds `Dictionary<int, SBOVector4<Entry>>`,
  four inline entries per event hash whatever the count, plus `idToIndex`, and
  `List<Entry>`/`Action<object>` remnants. 1.4 M objects, 9.4 M references, 868 MB of
  small-object heap, mostly empty inline slots. The references are the handler (mostly
  static delegates shared by every instance, cheap to trace) and the handler's data (the
  component).
- *Design:* replace `Subscribe`, `Unsubscribe`, `Trigger` and `OnCleanUp` with one packed array
  per event system (hash, id, handler, data), the order per hash kept as the game's
  `RemoveAtSwap` leaves it. It removes most of the bytes and the dictionaries' objects, not the
  references: the handlers and their data stay.
- *Exactness:* the same handlers called in the same order; a scan for mods that reflect on
  `entryMap`/`idToIndex` is needed. `Trigger` is hot: needs a frame-time check.
- *Size, risk:* medium; medium.

**3. `BehaviourNative` merged into the facade (engine).**

- *What:* one record per `MonoBehaviour` (2.33 M), what Unity keeps natively: ids, flags,
  `Managed` (the facade), `GameObject`, `Script`, `Name`. The facade holds it back through
  `m_Native`, and `ObjectTable.slots` holds it too.
- *Design:* keep a behaviour's engine state in fields of `UnityEngine.Behaviour` itself, so the
  facade is the record; the same for `GameObject`, `Transform` and the other component records
  (another 0.5 M objects). Every engine path that takes a `BehaviourNative` (53 files use the
  record types) takes the facade instead; records that exist before their facade (objects
  bound lazily from asset files) need a placeholder form.
- *Exactness:* the game sees the same API and values. The game's classes gain base-class
  fields, visible only to reflection over `UnityEngine.Object`'s non-public members.
- *Size, risk:* large (the engine's object model); medium (no game-visible change, but every
  lifecycle path moves).

**4. State machine instances and scheduler groups (game).**

- *What:* each of 248 K state machine instances allocates three `Stack<>`s, a parameter context
  array, an update table, a transition context array and its scheduler groups up front
  (`SchedulerGroup` and a `List<SchedulerHandle>` 702 K times each).
- *Design:* allocate them when first used, through a load-time rewrite of their accesses.
- *Exactness:* only if every access goes through the rewrite (mods' too); the empty objects are
  never observed by value.
- *Size, risk:* medium; medium. About 2.2 M objects of the 3.6 M (3.6 M with the scheduler
  groups' 1.4 M), few references.

**5. Host reflection caches (engine).**

- *What:* `PatchDependents` (378 K objects: the inlining call graph of every patched method,
  as `MethodBase` objects, kept for patches made later, D21), `PatchInliners` (145 K) and
  `MonoMissingMembers` (112 K). They also keep the runtime's reflection caches alive, which it
  holds only weakly.
- *Design:* key the graph by `RuntimeMethodHandle` and `RuntimeTypeHandle` values in
  `Dictionary<(nint, nint), int[]>`, and make a `MethodBase` (`GetMethodFromHandle`) only when a
  patch rebuilds dependents. Methods without handles (dynamic methods) keep the object form.
- *Exactness:* the same graph and the same rebuilds; no game or mod access.
- *Size, risk:* small to medium (two files); low.

**Not candidates:**

- **Large arrays of primitives and structs** (`FrameElement[]`, `PathFinder+Cell[]`,
  `int[]` tables, texel copies, the note store): nearly free to trace and never compacted
  ("What tracing costs"). Moving them to native memory lowers memory, not the pause.
  `compact-nav-links` and `paged-path-grids` saved gigabytes this way, but no collection time.
- **`ObjectTable.slots`** (2.8 M references in one array) and **`Component[]`** (2.6 M): the
  engine's lookup tables; they shrink only with item 3.
- **Building components, attributes, partitioner entries**: the game's own objects, one per
  component; they are the colony.

## Verdict

- **Candidate 1 landed** (`compact-report-entries`): 4.2 M objects and 7.9 M references fewer,
  an estimated 12% of the full collection's work.
- **Nothing else landed.** The one engine candidate worth a clear share, merging the records into
  the facades (35–45 ms of 0.75 s), rewrites the engine's object model: not contained. The
  contained one, the host's reflection caches, is worth about 13 ms (under 2%): not worth a
  change to Harmony's patching path on its own.
- **The pause is the game's objects.** The game owns about 80% of what a full collection
  traces, in millions of small objects per component, state machine, subscription and report
  entry. Moving large arrays off the heap, as the nav and path-grid tweaks did, saves memory but
  not collection time.
- **Order to take them in**, if the pause is to shrink: past days' report entries (1, built), the event tables (2; 25–40 ms), the engine
  records (3; 35–45 ms), then 4 and 5. Together they would cut the pause by about a quarter;
  the rest is the colony itself. A collection made rarer or concurrent (gc-pauses.md) is the
  other lever.
- **Not measured:** any candidate built in the game; the heap after an hour of play; Windows.
  The tool and probe (`out/lh` in the study's worktree: HeapInv, GcBench, the full-collection
  probe) are local.
