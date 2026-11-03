# NavGrid.Links in a compact store: feasibility

Feasibility spike, 2026-11-02, on u60. The question: can our load-time assembly rewriter
(AssemblyPolicy, beside MonoConversions) replace the game's flat `NavGrid.Links` array with a
compact store, exactly, and what would it save? The heap study (heap-soggy-lab.md) measured
`NavGrid+Link[]` at 2,277 MB (MiB) on "soggy lab 2". Everything below was checked in the game's IL
(Mono.Cecil) and measured in a loaded colony, not taken from the decompiled C# alone.

**Verdict: feasible, with compressed sparse rows, exactly, for the game and for every mod in
the workspace including Fast Track.** On soggy lab 2 the links take 2,387.5 MB (19 grids ×
915,456 cells × up to 56 slots × 8 bytes) and hold 5.35 M links: 1.79 % of the slots. A CSR
store holds them in about 117 MB, a saving of 2,271 MB (95 %); c100 saves 616 MB and DeCrew
232 MB. The game touches the array in 28 methods, all by value except one write through a ref
(the end marker), with one writer; three mods read or pass it, four transpile methods that touch
it, and all of them keep working if the rewrite (a) keeps `NavGrid.Links` a `Link[]` (a
one-element handle naming the store) so no signature changes, and (b) changes only the element
accesses. The packed walk was 8-34 % faster than the game's in a micro-benchmark over the real
links (an estimate, Linux, shared machine). The bitmask over transitions is exact on every link
measured but saves less on a large map (183 MB left on soggy lab 2) and is slower to read; it is
not worth its conditions. Fallback to the flat array (same rewritten code, a real array) is
needed only for a transpiler not yet reviewed, an assembly the rewriter cannot handle, or a
ref escape, none of which exists today.

Not settled here: the concurrency design (§4) is reasoned, not built; the exact IL-shape
replacement of the three hot loops is not prototyped; a frame-time effect needs Windows.

## 1. Facts

### The array

The same in U59 744825 and U60 761211 (the two `Assembly-CSharp.dll` give the same 28 methods
touching links, and their decompiled NavGrid, NavGridUpdater, PathFinder, PathProber and
TravelTube differ only in compiler output: U60's Linux build is unoptimised).

- `NavGrid.Link` is a sequential struct `{ int link; NavType startNavType; NavType endNavType;
  byte transitionId; byte cost; }`. `NavType` is a `byte` enum, so a link is **8 bytes**
  (`Marshal.SizeOf<NavGrid.Link>()`, which DevToolNavGrid prints, gives 8).
- `public Link[] Links` is allocated once, in the NavGrid constructor:
  `Links = new Link[maxLinksPerCell * Grid.CellCount]`. Nothing reallocates, copies, clears or
  pins it. Cell `c` owns slots `c * maxLinksPerCell` to `c * maxLinksPerCell + maxLinksPerCell
  - 1`; its links come first, in the order CreateLinks finds them (by start nav type, then by
  the transition's index), and a link whose `link` is `-1` ends them.
- `maxLinksPerCell` is the constructor's `max_links_per_cell + 1` (one slot for the end
  marker). The Duplicant grid passes 55, so it is 56, as claimed; the other grids pass 16, 20,
  22 or their transition count (table below).
- Only the end marker's `link` field is written (`ldelema` + `stfld link`); its other fields,
  and every slot after it, keep whatever an earlier build of the cell left there. Nobody reads
  past the marker, except DevToolNavGrid's stats, which stops at it too.
- A cell with more than `maxLinksPerCell - 1` links logs "Out of nav links" and writes on into
  the next cell's slots (and the marker after them). The probe saw no such cell (below).

### Occupancy in three colonies

**How it was measured.** Our player (u60 at b6e6ffb1, release, Linux, Xvfb) on U60 761211,
through `scripts/e2e.py --player ours` on the shared queue (`E2E_MAX_PLAYERS=2`), one spec per
save from `tests/e2e/fixtures` (c100 from the user's Downloads, DeCrew the existing fixture,
soggy lab 2 the #48 reporter's save). The probe (`out/navprobe/NavProbe.cs`, about 150 lines)
is not a mod: the spec's `setup:` loads it with `Assembly.LoadFrom` and calls it through
E2eDriver's `setup.call`, after the colony is up (so after the load's full build and
`FlushNavGridsOnLoad`), and again from a background thread 35 s later while the game runs at
speed 3. It walks every cell of every grid as AddPotentials does, counts the links before the
marker, checks each link against its transition, and writes the packed links to a file.
**Soggy lab 2 was loaded without its 38 mods** (they are U59 builds that fail on U60, see
heap-soggy-lab.md); the game's MOD DIFFERENCES dialog was dismissed, which on this save needs
a 1500-pixel-high screen (`E2E_SCREEN=1920x1500`) to reach its OK button. Links come from the
world's cells, buildings and the grids' transitions, none of which Fast Track or the other
mods there change, so the counts should hold for the modded load; Travel Tubes Expanded, which
the save does not use, would add transitions.

Every save has the same 19 grids (U59 and U60 build the same set; the heap study's "19 nav
grids of 915,456 cells" is soggy lab 2).

| Grid | maxLinksPerCell | Transitions | Links: c100 | DeCrew | soggy lab 2 | Slots used: c100 | DeCrew | soggy | Most in a cell |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| MinionNavGrid | 56 | 215 | 518,447 | 208,721 | 777,689 | 3.6 % | 3.8 % | 1.5 % | 32 |
| RobotNavGrid | 23 | 215 | 25,070 | 37,918 | 30,938 | 0.4 % | 1.7 % | 0.1 % | 13 |
| WalkerNavGrid1x1 | 13 | 12 | 19,366 | 19,918 | 25,544 | 0.6 % | 1.6 % | 0.2 % | 6 |
| WalkerBabyNavGrid | 3 | 2 | 13,204 | 16,164 | 16,194 | 1.7 % | 5.5 % | 0.6 % | 2 |
| WalkerNavGrid1x1NoJump | 7 | 6 | 17,640 | 18,468 | 22,672 | 1.0 % | 2.7 % | 0.4 % | 4 |
| WalkerNavGrid1x2 | 13 | 12 | 15,832 | 18,722 | 20,922 | 0.5 % | 1.5 % | 0.2 % | 6 |
| WalkerNavGrid2x2 | 7 | 6 | 12,343 | 16,236 | 15,361 | 0.7 % | 2.4 % | 0.2 % | 3 |
| DreckoNavGrid | 17 | 48 | 77,950 | 73,890 | 101,036 | 1.8 % | 4.4 % | 0.6 % | 10 |
| DreckoBabyNavGrid | 17 | 24 | 77,212 | 72,914 | 99,516 | 1.8 % | 4.4 % | 0.6 % | 10 |
| FloaterNavGrid | 23 | 29 | 132,286 | 27,156 | 138,682 | 2.2 % | 1.2 % | 0.7 % | 16 |
| FlyerNavGrid1x1 | 17 | 19 | 550,724 | 270,330 | 813,792 | 12.6 % | 16.2 % | 5.2 % | 12 |
| RobotFlyerGrid1x1 | 17 | 19 | 551,825 | 272,925 | 814,614 | 12.6 % | 16.3 % | 5.2 % | 12 |
| FlyerNavGrid1x2 | 17 | 19 | 488,049 | 192,948 | 738,231 | 11.2 % | 11.5 % | 4.7 % | 12 |
| FlyerNavGrid2x2 | 17 | 19 | 463,082 | 171,467 | 703,740 | 10.6 % | 10.3 % | 4.5 % | 12 |
| SwimmerNavGrid | 9 | 8 | 115,162 | 7,066 | 113,366 | 5.0 % | 0.8 % | 1.4 % | 8 |
| SwimmerNavGrid1x2 | 9 | 8 | 100,662 | 5,628 | 99,382 | 4.4 % | 0.6 % | 1.2 % | 8 |
| SwimmerGrid2x2 | 17 | 8 | 96,282 | 5,020 | 91,735 | 2.2 % | 0.3 % | 0.6 % | 8 |
| DiggerNavGrid | 23 | 43 | 477,362 | 290,693 | 617,662 | 8.1 % | 12.9 % | 2.9 % | 15 |
| SquirrelNavGrid | 21 | 34 | 83,374 | 76,668 | 108,866 | 1.5 % | 3.7 % | 0.6 % | 12 |
| **All 19** | | | 3,835,872 | 1,802,852 | 5,349,942 | 4.58 % | 5.63 % | 1.79 % | |

c100 is 636×404 (256,944 cells), DeCrew 256×384 (98,304), soggy lab 2 1192×768 (915,456).
The "3.6 %" of the claim is the Duplicant grid on c100; over all grids of soggy lab 2 it is
1.79 %. Most cells of every grid hold nothing (solid rock, space, unexplored); a cell that
holds links holds a handful (the Duplicant grid's commonest count is 8: 69 % of its non-empty
cells on soggy lab 2). The most links in any cell was 32 of the Duplicant grid's 55: **no cell
overflowed** in any save. Every one of the 11 million links equals what its transition makes
from its cell (the 10 teleporter links of soggy lab 2 aside, which take their offset from
`teleportTransitions`), and every cell's links are in transition order. Stale slots (non-zero
slots after the marker) were 7,935, 40,261 and 14,114. After 35 s of play the counts had moved
by less than 0.1 % (DeCrew and soggy; c100's run ended before its second dump).

## 2. Every reader and writer in the game

From the IL of both builds (`out/navscan`, NavScan: every instruction whose operand is
`NavGrid::Links`, a `NavGrid/Link` field, method, local or array type, or `maxLinksPerCell`).
Assembly-CSharp-firstpass has none.

| Method | Thread | Access |
|---|---|---|
| `NavGrid..ctor` | main (load) | `newarr Link` × `maxLinksPerCell * CellCount`, `stfld Links` |
| `NavGrid.InitializeGraph`, `UpdateGraph(List<int>)` | main | `ldfld Links` passed on (escape as an argument) |
| `NavGridUpdater.InitializeNavGrid`, `UpdateNavGrid`, `UpdateLinks`, `CreateLinksForCell`, `CreateLinks(NavTable,…)` | main | `Link[]` parameter passed on |
| `NavGridUpdater.CreateLinkWorkItem` (ctor, `Run`) | job threads | the array stored in a struct field and passed on |
| `NavGridUpdater.CreateLinks(int cell, …)` | job threads at load, main afterwards | **the only writer**: `stelem.any Link` (`links[num] = new Link(…)`, `num` from `cell * max_links_per_cell` upwards), then `ldelema` + `stfld link` for the end marker |
| `PathFinder.FindPaths`, `PathProber.Run` | main / AsyncPathProber's worker | `ldfld Links` and `maxLinksPerCell` passed to AddPotentials |
| `PathFinder.AddPotentials` | main and the worker | `ldelem.any Link` (by value) from `cell * max` while below `cell * max + max` and not the marker; copies into the scratch pad's own `Link[]` (not the store) |
| `PathFinder.ValidatePath` | main | `ldelem.any` from `cell * maxLinksPerCell` up to the marker |
| `TravelTube.OnDirtyNavCellUpdated` | main | `ldelem.any` from `cell * maxLinksPerCell` up to the marker (Duplicant grid) |
| `NavGrid.DebugDrawLinks`, `DevToolNavGrid.DebugDrawLinks` | main (debug views) | `ldelema` + `ldfld` (reads through a ref, no writes), walk to the marker; the slot index is passed to `DrawNavTypeLink` (an index escape) |
| `DevToolNavGrid.DrawNavTypeLink` | main | `ldelema` + `ldfld` at the index it is given |
| `DevToolNavGrid.Contents` | main | `ldelem.any` walks, `Links == null`, `Links.Length` (`ldlen`), `Marshal.SizeOf<Link>()` |
| `PathFinder.PotentialScratchPad..ctor`, `Pathfinding.MaxLinksPerCell` | | `maxLinksPerCell` only (scratch arrays sized by it) |
| `PathFinderAbilities.GetSubmergedPathCostPenalty` and the Minion override, `DevToolNavGrid.DrawLink` | | a `Link` by value |

No `ldelema` result is written through except the end marker, none is passed to another
method, and none outlives its statement. No `foreach`, `Array.Copy`, `Array.Clear`, `fixed`,
`Span` or `Unsafe` use. The fixed stride appears only as `cell * maxLinksPerCell` index
arithmetic, a walk to the marker (`num++`), AddPotentials' bound `cell * max + max`, the
DevTool's `Links.Length <= maxLinksPerCell * selectedCell` check and the flat index passed to
`DrawNavTypeLink`. The overflow write past a cell's slots is the only write outside them.

**Threads.** The array is read and written concurrently, without a lock: AsyncPathProber's
worker runs `PathProber.Run` → `AddPotentials` while the main thread rewrites cells in
`Pathfinding.UpdateNavGrids` (every grid, every 200 ms sim tick: U60 forces `update_all`), and
the load-time build runs CreateLinks on GlobalJobManager's threads, one row per job. An
exception on the worker is rethrown on the main thread (`TickFrame`), so a store must never
index out of range under these races. The original tolerates torn reads of a cell being
rebuilt.

## 3. Mods

NavScan's mod mode over every DLL in `out/ws` (2,897 Workshop items, 3,547 DLLs after Harmony,
PLib and library copies are skipped; 2 unreadable) and Fast Track 0.18.5 and 0.18.6: IL uses
as in §2, and Harmony patches (attributes, and `ldtoken` + `ldstr` pairs next to `Patch` calls
or in `TargetMethod`) on the types of §2's methods. Every hit was then read in the decompiled
mod (`out/navscan/decomp`). No mod names `Links` as a string (no reflection on the field).
Distinct mods (archived versions and stray `obj/` copies counted once):

| Mod | What it does with links | Under a compact store |
|---|---|---|
| Direct Duplicant Control (3809293304) | its own reads: `Links == null`, `cell * maxLinksPerCell`, `Math.Min(… , Links.Length)`, `ldelem.any` | works if its IL is rewritten like the game's (mods go through the same pass); unrewritten, `Links.Length` of a stand-in array would silently find no links |
| Travel Tubes Flydo (3709304542) | its own reads: walk from `cachedCell * maxLinksPerCell` to the marker | works if rewritten |
| ONI Lag Fix (3781498375) | prefix on `NavGridUpdater.UpdateLinks` (takes `NavGrid.Link[] links`) that runs `CreateLinksForCell` through a delegate of its own `Link[]` signature in `Parallel.ForEach` | works only if the game's `Link[]` signatures stay; makes the writer run on many threads at once (disjoint cells) |
| Hazard Pathing (3818353735) | **transpiler** on `PathFinder.AddPotentials`; matches `ldfld Cell::cost; ldc.i4.m1; ceq` … and searches back for `ldfld Link::cost` | works if the rewrite leaves the second and third loops (the scratch pad's copies) alone; it throws on a changed shape |
| Transit Tube Overlay (3510927460) | **transpiler** on `TravelTube.OnDirtyNavCellUpdated`; matches `ldc.i4.0; stloc.3` and `ldfld endNavType; ldc.i4.8; beq.s` | works if the rewrite keeps local numbering and short branches |
| Travel Tubes Expanded (3430960564) | **transpiler** on the same method, replaces a `DirectionFromToCell` call | works |
| Fast Track 0.18.5, 0.18.6 | **transpiler** on `PathProber.Run` (swaps `PathGrid.BeginUpdate`/`EndUpdate`), prefix replacing `NavGrid.UpdateGraph()` that still calls `UpdateGraph(List<int>)`, postfix on the NavGrid constructor (validators only) | works: none of them touches links |
| No Atmo Suit Swim Penalty, Air Swim, Flying Duplicants | prefixes on `GetSubmergedPathCostPenalty` reading a `Link` by value | unaffected |
| CycleTrim, ONI Together, Horizon (×2), Just Fall Already, Super Rover, Stairs, Cannot Fly, Fear The Darkness, No Door Idle, Liquid Lock | patches on NavGrid/PathFinder/Pathfinding methods that do not touch links (`UpdateGraph` gating, `UpdateNavGrids`, `TraversePath`, `IsSubmerged`, `AddNavGrid`) | unaffected |

So: 3 mods read or pass the array themselves, 4 transpile a method that touches it (Fast Track
among them), none writes it, none reflects on it. The transpilers all run on the rewritten IL
(Harmony reads the loaded method), so the condition for them is that the rewrite changes only
the store accesses and keeps everything they match. A blanket fallback "a mod transpiles a
method that touches Links" would switch the store off for every Fast Track user, soggy lab 2
included; a fallback for transpilers that are not on a reviewed list, with these four on it, does
not.

## 4. The two designs

### What a store has to reproduce

From §1 and §2: reads by value at `cell * maxLinksPerCell + k`, walks that stop at the marker
or at `cell * max + max`, `ldelema` + `ldfld` reads, `Links == null` and `Links.Length`, the
array handed through `Link[]` parameters and a struct field (and, in ONI Lag Fix, through a
delegate of the game's private signature), one writer that appends a cell's links and writes
the marker, and all of it racing between the main thread, the load-time job threads and
AsyncPathProber's worker. Nobody reads past a marker, so the stale slots (thousands per grid,
table above) need not be kept, and nobody keeps a ref into the array.

### Keeping every signature: the array becomes a handle

Changing the field's type, and with it the `Link[]` parameters of NavGridUpdater and
`AddPotentials`, breaks ONI Lag Fix (its prefix's parameter and its delegate) and any Harmony
patch that names `links`. Both designs avoid it the same way: `NavGrid.Links` stays a `Link[]`,
but in compact mode the constructor (rewritten at its `newarr`) allocates a one-element handle
whose element names the grid's store. Every `Link[]` signature, field and Harmony binding stays
as it is; what changes is each element access the rewriter finds:

| IL on a `Link[]` | Becomes |
|---|---|
| `ldelem.any Link` | `call Link LinkStore.Load(Link[], int)` |
| `ldelema Link` followed by `ldfld` | `LinkStore.Load` then the same `ldfld` on a local copy (a read through a ref is a read) |
| `stelem.any Link` | `LinkStore.Store(Link[], int, Link)` |
| `ldelema Link` + `stfld Link::link` (the marker) | `LinkStore.StoreLink(Link[], int, int)` |
| `ldlen` | `LinkStore.Length(Link[])` (`maxLinksPerCell * CellCount` for a handle) |
| any other use of an `ldelema` result (passed on, `stobj`, `initobj`, a stored ref) | not rewritten: the session runs in flat mode |

Each helper first asks whether the array is a handle (one element whose `link` is a reserved
value below `-1`) and otherwise indexes it as before, so the scratch pad's `Link[]` and any
mod's own arrays behave as they do now, and **flat mode is the same rewritten code with a real
array**: no second copy of the game's image is needed. The JIT cannot hoist that check out of a
loop, which is why the game's three hot readers get the specialised form below.

A transpiler that runs later sees the rewritten IL. One that emits a raw `ldelem` on
`NavGrid.Links` would index the handle: `IndexOutOfRangeException`, loud, not silent. The mode
is decided when the first NavGrid is built (the colony's load, after every mod's `OnLoad`):
flat if a mod's assembly could not be rewritten, or if Harmony holds a transpiler on a §2
method that is not on the reviewed list (today Fast Track, Hazard Pathing, Transit Tube
Overlay, Travel Tubes Expanded, §3). A mod enabled or disabled later restarts the game, so the
decision holds for the session.

### Design A: compressed sparse rows (recommended)

Per grid: a 32-bit head per cell (a 24-bit offset << 8 | the count; 64-bit heads for a grid that
outgrows that) and the links packed in cell order in
append-only chunks. Exact reads at a flat index `i`: `c = i / max`, `k = i - c * max`, the
link `k < count ? packed[offset + k] : {link = -1}`.

- **Writes.** `CreateLinks(int cell, …)` appends `links[num++] = …` from `cell * max` and
  then writes the marker; the rewritten `Store` stages links in a thread-local buffer and
  `StoreLink(…, -1)` commits them: in place when they fit the cell's block, else in a new block
  (capacities 1, 2, 4, … 64) taken with `Interlocked.Add` from the chunk's tail, and the head
  published with one 32-bit write after the links. Blocks are never reused during a session
  (each load builds new grids), so a reader holding an old head reads an old block, never
  another cell's links; a cell outgrows its block at most six times, so the garbage stays below
  the live size. A reader racing a rewrite sees the old list, the new one, or a mix of the two
  in place: what the original can already see. Parallel writers (the load's jobs, ONI Lag
  Fix) only share the tail counter.
- **The overflow** (more than `max - 1` links, which logs an error and writes into the next
  cell) is the one write outside a cell. No cell came near it (the most was 32 of 55 on the
  Duplicant grid); the store can still reproduce it on a slow path that rewrites the next
  cell's first links.
- **Hot readers.** `AddPotentials`' first loop, `ValidatePath` and
  `TravelTube.OnDirtyNavCellUpdated` index `cell * max + k` in a loop: the rewriter replaces
  exactly that loop head (matched by IL shape, per game build, as the game tweaks' transpilers
  are) with a walk of `[offset, offset + count)`, and falls back to the generic helpers when the
  shape differs. Hazard Pathing and Transit Tube Overlay match other instructions of those
  methods, so the replacement must leave the rest byte for byte, locals and short branches
  included.

### Design B: a bitmask over the transitions

Per cell one bit per transition of the grid, in the order CreateLinks visits them (start nav
type, then index); a link is rebuilt from its transition (`link = OffsetCell(cell, x, y)`,
`startNavType`, `endNavType`, `transitionId`, `cost`). The probe checked the premise on every
link of every grid of the three saves: **every link is the one its transition gives, in that
order** (0 not derivable, 0 out of order). But:

- The Duplicant and Rover grids have 215 transitions: 4 words (32 bytes) a cell, whatever the
  cell holds. The other grids fit one word.
- A teleporter's links take `x, y` from `teleportTransitions` at build time; a later change of
  that dictionary without a rebuild of the cell would rebuild a different link. They need a
  side table of the targets as built (soggy lab 2 has 10 such links).
- A flat index means "the k-th set bit": a select (PDEP, slow on Zen 2, or a popcount loop) per
  access through the generic helper.
- It can only hold links its transitions make: a store of any other value (none today, but
  nothing forbids it) has no representation and forces flat mode.

### Memory

8 bytes per slot now; CSR is 8 bytes per link plus a head per cell (4-byte offsets in the
table; 8 with the count beside them); the bitmask is `ceil(transitions / 64)` words a cell. A
4-byte CSR link (the transition id and a 24-bit offset to the target, rebuilt like the bitmask's)
is listed for scale but has the bitmask's exactness conditions.

Computed from the probe's counts (MB = 10^6 bytes; the heap study's 2,277 "MB" is MiB, the
same 2,387.5 MB as here):

| Layout | c100 | DeCrew | soggy lab 2 |
|---|---:|---:|---:|
| Flat (the game's), 19 grids | 670.1 MB | 256.4 MB | 2,387.5 MB |
| CSR, exact fit: 8-byte links + a 4-byte offset per cell | 50.2 | 21.9 | 112.4 |
| CSR as proposed: 32-bit head (24-bit offset, count) per cell, blocks rounded up to a power of two | 53.7 | 24.7 | 116.9 |
| CSR with 64-bit heads, blocks rounded up | 73.2 | 32.2 | 186.5 |
| Bitmask (4 words a cell on the two 215-transition grids, 1 elsewhere) | 51.4 | 19.7 | 183.1 |
| CSR, 4-byte links (exactness of the bitmask) | 34.9 | 14.7 | 91.0 |
| **Saving with the proposed CSR** | **616 MB (92 %)** | **232 MB (90 %)** | **2,271 MB (95 %)** |

The heads dominate on a large, mostly empty map: 19 grids × 915,456 cells × 4 bytes is 70 MB
of soggy lab 2's 117. A 24-bit offset limits a grid to 16.7 M link slots, against 0.8 M links in
the fullest grid of soggy lab 2 plus at most as much again in outgrown blocks; a store that
reached it would move to 64-bit heads at the next load. Blocks rounded up to a power of two
add only 4.5 MB on soggy lab 2: most counts are 8. The bitmask has no edge: its cost follows
the cell count, not the links, and the 215-transition grids need 32 bytes a cell. The saving is
also the end of a 2.4 GB zeroed LOH allocation at every load of soggy lab 2.

### Speed (estimates)

NavBench (`out/navbench`) loads a dumped grid and walks it as the pathfinder does, in each
layout: a breadth-first flood over (cell, nav type) from 300 fixed start cells with
AddPotentials' first loop (the links of the cell whose start nav type matches), and a pass over
every cell. Before timing it checks that every layout gives the same link list for every cell
and nav type, which held for every grid tried. Median of 7 runs, Linux, a machine shared with
other agents' players (load average about 7.5 of 16), so only the ratios mean anything:

| Grid (save) | Flat (game) | CSR offsets | CSR 64-bit head | CSR behind the flat index | Bitmask |
|---|---:|---:|---:|---:|---:|
| MinionNavGrid (c100), 3.05 M states | 172 ms | 118 (0.69) | 117 (0.68) | 172 (1.00) | 124 (0.72) |
| FlyerNavGrid1x1 (c100), 2.06 M states | 95 | 68 (0.72) | 68 (0.72) | 103 (1.08) | 66 (0.69) |
| MinionNavGrid (DeCrew), 8.82 M states | 354 | 279 (0.79) | 293 (0.83) | 390 (1.10) | 354 (1.00) |
| MinionNavGrid (soggy lab 2), 2.30 M states | 72 | 48 (0.67) | 47 (0.66) | 69 (0.96) | 57 (0.80) |
| FlyerNavGrid1x1 (soggy lab 2), 1.68 M states | 32 | 29 (0.92) | 29 (0.91) | 44 (1.37) | 32 (1.01) |

(Flood times, the median of three invocations of seven runs each; the ratio to the game's
layout in brackets. Single runs varied by up to 40 % with the other players' load.)

The packed layouts are faster than the game's, not slower: a cell's links share one cache line
instead of starting a fresh line 448 bytes (Duplicant grid) away from the last cell's, and the
walk needs no marker test. The transparent flat-index form pays an integer division per access
and comes out level with the game's layout or up to 37 % slower, which is why the hot readers
get the walk. The bitmask's rebuild of each link costs about what the cache lines save. (The
bench's bitmask has a bit only for the transitions that occur, 2-3 words on the Duplicant grid;
the real one needs 4.)
Only the link walk is measured: a probe's PathGrid lookups, ability checks and heap work
dilute it, so a frame-time effect, if any, needs a Windows run at speed 3.

## 5. Recommended next step

1. **The store and its helpers** in OniMoono.Engine (or a host class the rewritten IL can
   call): handles, the 32-bit heads, append-only chunks, the thread-local staging and commit,
   the overflow slow path, and `Load`/`Store`/`StoreLink`/`Length` falling through to a plain
   array. Unit tests: a replay of NavProbe's dumps (`out/navprobe/*/*.bin`) through
   `CreateLinks`-shaped writes, from several threads, against the flat array; readers racing a
   writer never go out of range.
2. **The rewrite pass** beside MonoConversions, for the game and mods, with the rules of §4's
   table, run on both builds' Assembly-CSharp and on the three reading mods (Direct Duplicant
   Control, Travel Tubes Flydo, ONI Lag Fix); the cached game image keyed as now.
3. **The hot loops** of AddPotentials, ValidatePath and TravelTube matched by IL shape, with
   the four reviewed transpilers applied to the result in a test (each throws or warns on a
   shape it does not match, so a test can tell).
4. **The mode decision** at the first NavGrid constructor (Harmony's patch registry against
   the reviewed list), logged as a game tweak line, and a `--game-tweak` name to turn it off.
5. **Verification:** a probe in the store's own mode comparing every cell's links with a flat
   build of the same grid on c100, DeCrew and soggy lab 2 (with its mods on U59 data, as the
   heap study ran it), then heap and frame time on Windows at speed 3.

It belongs with the game tweaks (TWEAKS.md): it changes how Klei's code stores something
without changing what it computes.

## 6. Built (2026-11-03)

Design A as above, as the game tweak `compact-nav-links` (TWEAKS.md):

- **The store** (`src/OniMoono.Host/NavGridLinks.cs`). One change from §4: a 32-bit head is
  the count (8 bits), the block's size class (4 bits) and its number among the blocks of that
  size (20 bits), not a 24-bit offset into one space. A cell's block only grows, so a cell takes
  at most one block of each size, and 2^20 numbers per size hold every map of fewer than 2^20
  cells (soggy lab 2 has 915,456): a 32-bit store can never run out, so the 64-bit fallback is
  chosen when the store is made (more cells, or more than 255 slots a cell) rather than needing
  a switch in play. Blocks of a size live in 4,096-link chunks, allocated as needed. The
  marker's other fields read as 0 (the game's array has stale bytes there, which nothing reads).
- **The rewrite** (`NavGridLinksRewrite.cs`, first in MonoConversions' pass): §4's table, with a
  stack-type flow over every method that names the link type for `ldelema` uses, `ldlen` and
  escapes. The game has 30 accesses in 9 methods and no problem in either build; of the 1,340
  Workshop DLLs in `out/ws` that name NavGrid, MoreTek, Direct Duplicant Control and Travel
  Tubes Flydo are rewritten, none has a problem.
- **The hot walks** read through `NavGridLinks.Walk` and a cursor local when the method's IL
  fingerprint is U59's or U60's; each walk `ldelem.any` becomes `ldloca` + `call` (2 bytes
  more), every other instruction stays, and the short branches still reach in both builds.
  `--check-types` compares the loaded methods with the game's file and applies the four
  reviewed transpilers to them.
- **The decision** (`GameTweaks/CompactNavLinks.cs`) at the first NavGrid, as in §4.
  path-probe-repair (OniMoono.GameTweaks, outside the rewrite) reads the Duplicant grid's rows
  itself; given a handle it reads them through `NavGridLinks.Load`.
- **Hazard Pathing** throws on U60 with the tweak on and off alike ("expected exactly two
  edge-cost prune sites, found 0": its pattern is U59's optimised IL); on U59 all four reviewed
  transpilers apply to the rewritten methods.

Verification and Windows numbers are in perf-wins.md's verdicts (on the u60 branch).

## Files

Not committed (the worktree's `out/`): `out/navscan` (NavScan, the IL scan of the game and
mods; `game60.json`, `game59.json`, `mods.txt`, the decompiled mods in `decomp/`),
`out/navprobe` (the probe, `analyze.py`, and the dumps and stats of each save),
`out/navbench` (NavBench), and the scratch specs that ran the probe
(`out/navprobe/specs/navlinks-*.yaml`, run from `tests/e2e` with the saves as fixtures).
