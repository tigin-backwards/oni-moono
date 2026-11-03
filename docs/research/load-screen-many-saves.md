# The Load screen with thousands of saves (2026-11-02)

A user with 4,637 saves reported that the Load screen, and picking a save in it, "can take
forever". This note maps what the game does from opening the screen to loading a save, measures
it in our player and in the original with the same saves, and ranks exact ways to make it faster.
Nothing here has landed.

## The saves

The user's old saves folder (`cloud_save_files/backup`, once the cloud folder itself) was only
listed and stat'ed, never copied or opened by a player:

- 4,637 `.sav` files, 233 GB (median 3.5 MB, largest 208 MB, one empty file), and 4,300 `.png`
  previews (5.6 GB, two shapes: 528x306 at about 150 KB, 528x1842 at about 1.2 MB).
- 124 colony folders, with 3,959 of the saves in their `auto_save` subfolders. By the headers'
  colony IDs (`SaveGame.GetSaveUniqueID`: the colony GUID, else name and cluster) they are 183
  colonies, so 10 pages of 20 on the screen. The largest folders hold 1,046, 715, 441 and 394
  saves.
- Headers (the JSON `GameInfo` after the 16-byte header) are 302 to 471 bytes, 1.7 MB in all.
  The saves come from 90 game builds (475497 to 744825).

## What the game does

U59 (744825) and U60 (761211) have the same code here. Everything below runs on the main
thread, in the frame of the click.

**Opening the screen** (`MainMenu.LoadGame` → `LoadScreen.OnActivate` → `RefreshColonyList`):

1. `CheckCloudLocalOverlap` (when the platform has a user ID, as Steam does) calls
   `GetColonies(sort: false)`. That is a full pass over every save, only to find colonies with
   both local and cloud saves and move the local ones to the cloud folder. With no local saves
   it finds nothing.
2. `GetColonies(sort: true)` makes the same full pass again, sorted newest first.
3. Each pass starts with `SaveLoader.GetAllFiles`. It runs
   `Directory.GetFiles(dir, "*.sav", AllDirectories)` on `save_files/` and on
   `cloud_save_files/<user>/`, then `File.GetLastWriteTimeUtc` on each file (a stat each), then
   `List.Sort` by that time. The sort is an introsort and not stable, so saves with equal times
   come out in an order that depends on the listing. Then `GetColoniesDetails` does this for
   each file:
   - `IsFileValid` calls `SaveLoader.LoadHeader`, which opens the file, reads 16 bytes, seeks,
     reads the header, closes the file, and deserializes the JSON with Newtonsoft into
     `SaveGame.GameInfo`. A file that throws is logged as `Corrupted save file` with the
     exception, and skipped.
   - `SaveGame.GetFileInfo` calls `LoadHeader` again: the same file, the same work.
   - `new FileInfo(path).Length` stats the file again.
   - The file is added to a `Dictionary` by `GetSaveUniqueID`, in that order.

   So one opening reads every header **four times**: 18,548 opens and JSON parses for 4,637
   saves. It also stats every file four times. Only headers are read: no save is decompressed.
4. The first page, 20 colonies (`ITEMS_PER_PAGE`), gets one `AddColonyToList` each. It takes a
   pooled row, sorts the colony's saves by date, counts them, sums their sizes, and calls
   `SetPreview` for the newest save. `RetireColonyUtility.LoadColonyPreview` reads the `.png`
   beside the save (`File.ReadAllBytes`, `Texture2D.LoadImage`, `Sprite.Create`). Without one,
   it uses the colony's newest timelapse image in `RetiredColonies/<name>/`, which costs a
   directory listing and a decode. Nothing is cached: each opening decodes its previews into new
   textures.

**Load more...** calls `RefreshColonyList` again with one more page. That repeats both full
passes and decodes the previews of every page shown: 40 decodes for two pages, 200 for ten.

**A colony** (`ShowColony`) destroys the previous rows and instantiates one row per save, for
every save of the colony (there is no paging). Each row gets its texts, the DLC icons
(`KInstantiateUI` per DLC), the compatibility check and the button handlers. Then
`ShowColonySave` fills the details panel for the first save: the texts, the cluster's world
name from `SettingsCache`, and one preview decode. The following frames lay out every row. The
list is a `KScrollRect` under a stencil `Mask`, so no row is left out of layout or drawing.

**A save** (`UpdateSelected` + `ShowColonySave`) sets the toggle states of two rows, the detail
texts and one preview. It costs 0.5-0.7 ms in both players.

**Load** (`LoadScreen.Load` → `LoadingOverlay.Load(DoLoad)` → `DoLoad(filename)`) does one more
`LoadHeader` of the chosen file, then `SaveLoader.LoadScene`. The scene's `SaveLoader.Load`
reads the whole file (`File.ReadAllBytes`) and decompresses it. Nothing in the load depends on
how many other saves exist, so it is not part of this problem and was not measured here.

**Elsewhere**: the main menu's `RefreshResumeButton` (`GetLatestSaveForCurrentDLC`) lists and
stats every save, then reads headers newest first until one is compatible. It runs at every
main menu start and after loads, conversions and deletions.

**A game bug that multiplies the colony cost.** `AddColonyToList` adds its `ShowColony` handler
to the pooled row's button with `+=` and never clears it. The row's cloud and local buttons are
cleared; `Button` is not. Every `RefreshColonyList` within one opening of the screen (each Load
more, a conversion between cloud and local) adds another handler to the same rows. After k
Load more clicks, clicking a colony on an earlier page runs `ShowColony` k + 1 times in that
frame: (k + 1) x 715 rows are instantiated, and all but the last set are destroyed at the end
of the frame, after that frame's layout has already processed all of them. Closing the screen
destroys it (`KScreen.Deactivate`), so the next opening starts again with one handler. The
cost is measured below. To reach the user's tenth page takes nine Load more clicks.

## Method

- **Saves tree.** No run touches the real folder. `scripts/load-screen-saves.py REAL TEMPLATE
  OUT` builds a tree with the backup's shape:
  - the same relative paths, sizes (as sparse files) and modification times;
  - each `.sav` gets a header made from the `early-base` fixture's, with the folder's name as
    `baseName`, a GUID from the folder's name, and `isAutoSave` from the folder;
  - each preview is a hard link to one of two generated PNGs of the two real shapes (441 KB and
    74 KB).

  The tree groups into 124 colonies (the real headers make 183) and has no `RetiredColonies`
  fallbacks. The tree used here is `out/load-screen-saves/synthetic` (ignored). A tree of
  symlinks to the real files was read only by the standalone harness below.
- **In-game timings** come from `tests/e2e/load-screen-many-saves.yaml`, run with
  `E2E_CLOUD_SAVES` naming the tree (`scripts/e2e.py` hard-links it into the run home's cloud
  folder). The setup: U60 761211, both players on Xvfb lanes, 1280x720, at most two players.
  E2eDriver's new `stopwatch` command times game methods (Harmony prefix and postfix,
  inclusive). It also lists frames over 50 ms with the driver's own share, and holds 120 frames
  without driver queries to show the screen's steady cost. The original renders through llvmpipe
  (as every e2e run of it does), ours through Mesa's d3d12, so script times compare but frame
  times and draws do not. Each player ran 6 to 8 times; the tables give the range. The machine
  was not held quiet (other agents' players may have run beside), so runs vary by up to 1.7x.
- **Standalone harness** (.NET 10 with the game's Newtonsoft.Json; not committed): the screen's
  file work outside the game. It runs `GetSaveFiles`, then `LoadHeader` twice and
  `FileInfo.Length` per file, twice over. Variants: one header read per file, stat only, and
  headers read on 8 or 16 threads.
- **Not measured:** Windows (no Windows runs for this note), the original's draw count (Mesa's
  `GALLIUM_HUD` dump stayed empty for Unity's context), and loading itself.

## Measurements

### Opening the screen (4,637 saves, 20 previews)

Synthetic tree on ext4, milliseconds.

| | ours | original | ours vs original |
|---|---|---|---|
| `OnActivate` (the click's frame) | 529-926 | 1,454-2,326 | 2.4x faster |
| · `CheckCloudLocalOverlap` (pass 1) | 244-289 | 718-1,103 | |
| · both `GetColoniesDetails` passes | 419-534 | 1,221-1,840 | |
| · `SaveLoader.GetSaveFiles` (4 listings) | 54-71 | 210-265 | 3.7x |
| · `SaveLoader.LoadHeader` (18,544 calls) | 280-550 | 1,113-1,689 | 3.2x |
| · · of it `SaveGame.GetGameInfo` (the JSON) | 182-248 | 222-383 | 1.4x |
| · `AddColonyToList` (20 rows) | 225-245 | 124-213 | **ours 1.1-1.9x slower** |
| · · of it `LoadColonyPreview` (20 PNGs) | 210-227 | 81-157 | **ours 1.4-2.7x slower** |
| steady frame, colony list | 2.9-3.9 | 34-43 (llvmpipe) | |

Header I/O alone (`LoadHeader` less its JSON) costs 10-13 us a read in ours and 48-70 us in the
original's Mono on Linux. Our previews decode with stb_image, about 11 ms for the 528x1842 PNG;
the original's Unity decode takes 4-8 ms.

**The real files through WSL's 9P** (`/mnt/c`), measured with the standalone harness on CoreCLR,
one of the screen's two passes:
- the listing and its stats: 54-78 s;
- 9,273 header reads: 30-32 s (3.3 ms each);
- the JSON: 0.2 s;
- `FileInfo.Length`: 10-12 s.

On 9P every stat costs milliseconds, so in our Linux player an opening with the real folder
mounted there would take about 3 minutes. That cost belongs to WSL's file system, not to the
game, but it shows how the screen scales with the cost of a file operation: 4 opens and 4 stats
per save. Reading the headers on 16 threads took 2.5 s instead of 30.7 s.

On the local disk, the harness takes 390-450 ms for one pass of the screen's work (listing
45-73, header I/O 120, JSON 160-230, `Length` 20). With one header read per file it takes
120-230 ms; stat only, 46-76 ms; headers on 8 threads, 53-171 ms.

**Windows (estimate, not measured).** NTFS with Defender's real-time scan costs more per open
than ext4: tens of microseconds for a cached file that was already scanned, more on first touch
or on a spinning disk. At 30-100 us an open and 10-30 us a stat, one opening's 18,548 opens and
18,548 stats would take 0.7-2.4 s in the file system alone, before any parsing.

### Opening a colony (715 saves)

| | ours | original |
|---|---|---|
| `ShowColony` (the click's frame) | 114-370 | 492-811 |
| · `TMP_Text.text` sets (6,461) | 3.4 | 6.0 |
| · compatibility checks (715) | 2.2 | 3.3 |
| the next 3 frames (layout) | 1,140-1,320 in all (for example 348 + 718 + 250) | 1,340-1,470 (for example 673 + 630 + 164) |
| · `LayoutRebuilder.Rebuild` (87 calls) | 304-387 | 364-366 |
| · `TextMeshProUGUI.Rebuild` (about 5,700 calls) | 96-144 | 223-248 |
| click to settled | 1.4-1.7 s | 1.9-2.2 s |
| steady frame in the colony view | 26-30 (GPU-bound: 5,071 draws a frame against 254 in the list) | 35-41 (llvmpipe) |
| back to the list | one frame of 237-553 | one frame of 130-134 |

Most of `ShowColony` lies outside the methods timed: it is the 715 `Instantiate` calls of the row
template and their `HierarchyReferences` lookups. Ours does that about 2x faster.

Ours draws the colony view with **5,071 draws a frame**, about 7 a row. Our canvas batching
(`CanvasSystem.Batched`) merges only consecutive draws of the same state, so each row's
background, texts, labels and icons break the run before the next row. Unity's canvas batching
sorts by its own depth, which can put non-overlapping graphics of the same material together;
whether the original draws these rows in a few draws is not measured here. On the WSL GPU path
a draw costs about 5 us of GL thread time, so in ours the view runs at 33-38 fps while open. On
Windows a draw costs less, but the cost still grows with the colony.

Each of the driver's own queries in the colony view (a `**` path walk over 715 rows) cost 0.3-0.9
s in ours and 1.8-1.9 s in the original. Those frames are left out above.

### Load more, and the stacked handlers

| | ours | original |
|---|---|---|
| Load more to two pages (`RefreshColonyList`, 40 previews) | 792-1,019 | 1,831-2,198 |
| · of it the 40 previews | 318-437 | 165-225 |
| the first-page colony again after that Load more: `ShowColony` | **2 calls, 655 ms** | **2 calls, 1,834 ms** |
| · the next frames | 671 + 1,161 | 1,888 + 3,040 |

One Load more doubled the next colony click (more than doubled it in the original: 4.9 s from
click to settled). Projected to the user's folder (an estimate): reaching the tenth page takes
nine Load more clicks of 2-4 s each in the original. The file work runs ten times over, and
the previews are decoded 20 + 40 + … + 200 = 1,100 times. A colony on the first page then costs
ten builds in one click. In ours the 1,100 decodes alone take about 12 s.

## Exact speedups

"Exact" means the original's colonies, saves, order, texts, previews, enabled buttons and log
lines, for any saves folder, including files that change between openings. The options are
ranked by gain for the user's folder; estimates are marked.

### 1. One header read per file per refresh (game-side tweak, ours)

`LoadHeader` is a pure function of the file's bytes. A tweak memoizes it for the length of one
`RefreshColonyList`, including `CheckCloudLocalOverlap`'s pass inside it. The key is the full
path, checked against the file's length and the last write time the listing already read. The
four reads become one, and so do the four parses.

- **Gain**: ours about three quarters of the 280-550 ms in `LoadHeader`, and 0.8-1.3 s in the
  original's code. On Windows it removes three quarters of the opens (estimate: 0.5-1.8 s of the
  0.7-2.4 s above). It applies to every Load more as well.
- **Exactness**: each caller gets its own copy of `GameInfo`, with its `worldTraits` array and
  `dlcIds` list cloned, as each parse makes new ones. A read that throws is not memoized, so the
  next caller reads the file again, and the same `Corrupted save file` or `Exception while
  loading` warning is logged with a real stack trace. The once-only `SaveFileHeaderBlank` warning
  is unaffected. The memo is dropped when `RefreshColonyList` returns, so a file that changed
  between refreshes is read again.
- **Proof**:
  - the spec's `ui_dump`s of the colony list and colony view, diffed against the original's;
  - the warnings in the log, compared line by line;
  - a unit test over a tree with an empty file, a truncated header, a header of size 0, a 7.13
    save whose world traits get rewritten, and a file rewritten between two refreshes.

### 2. Previews: decode faster, and once (engine-side and game-side)

Every refresh decodes the previews of every page shown, and ours decodes 1.4-2.7x slower than
the original.

- **Engine-side**: a faster PNG decode in `ImageConversion.LoadImage`, for example a SIMD
  inflate, or decoding rows straight into the texture's layout instead of decode-then-swizzle.
  - Exactness: PNG is lossless, so this is exact by construction; a unit test compares the
    pixels against stb's.
  - Gain (estimate): about 130 ms per opening and per page of Load more, if it matches the
    original's 4-8 ms a decode. It also helps the timelapse images and the colony summaries.
- **Game-side**: keep each preview's decoded pixels for the length of the screen's opening,
  keyed by path, length and last write time, and make each requested `Texture2D` from them.
  - Exactness: a new texture and sprite each time, as now, with the same pixels; the file is
    stat'ed instead of read.
  - Gain: a page's decodes are paid once instead of once for every later Load more. At page 10
    that is 200 decodes instead of 1,100: about 10 s in ours and 4-7 s in the original
    (estimate).

### 3. Header reads in parallel, applied in order (game-side tweak, ours)

After the listing, read the raw headers (16 + `headerSize` bytes) of all files on the thread
pool. Then parse and group on the main thread in the original order. The file system's latency
overlaps, while parsing, grouping and logging stay in order.

- **Gain**: little on the local disk (ours spends 120 ms in I/O for 9,273 reads), much on
  high-latency storage (on 9P, 30.7 s became 2.5 s). Estimate for Windows with Defender or a hard
  disk: most of the open cost that remains after 1, 0.2-1 s.
- **Exactness**: only the bytes are read early. A read that fails on a worker is not reported
  from there: the main thread reads that file again itself, so the exception and log line are
  the original's. A file could change between the early read and its turn, so the main thread
  checks length and last write time again before using the bytes. That closes most of the
  window, not all of it; the original has the same race between its own four reads.

### 4. Clear the stacked colony handlers (game-side tweak, ours; needs the user's word)

A postfix on `AddColonyToList` that leaves the row's `Button` with only the handler just added.

- **Gain**: k builds of the colony per click after k Load more clicks. One Load more cost 0.3 s
  more in ours and 1.3 s more in the original (plus layout), and nine cost nine times that
  (estimate).
- **Exactness**: the earlier handlers' work is fully overwritten within the frame. Each
  `ShowColony` destroys every `Clone` row and makes new ones, and `UpdateSelected` and
  `currentColony` end as the last handler leaves them, so after the frame the screen is the last
  handler's. What differs:
  - the duplicate log lines of the earlier runs (`Failed to find cluster...` from
    `ShowColonySave`, only for saves of an unknown cluster);
  - objects that were made and destroyed within the frame (a mod watching instantiation would
    see fewer);
  - a different number of `ShowColony` calls for a mod that patches it.

  This fixes a game bug rather than only speeding it up. The proof would be a `ui_dump` after k
  Load more clicks against the original's, plus a log diff that names the missing duplicates.

### 5. Draw the colony view in fewer draws (engine-side)

A 715-save colony takes 5,071 draws a frame, about 7 a row. Batching graphics of the same state
across rows when they do not overlap, as Unity's depth-sorted batching can, would bring that
down to a few dozen.
- Exactness: the pixels are the same when the reordered graphics do not overlap. The proof is
  screenshots of this view, and of every UI spec, against the original's.
- Risk: this is a general engine change with risk on every screen. First confirm the premise
  with the original's draw count from a GPU capture (RenderDoc or apitrace).

### 6. A persistent header cache on disk (game-side, ours): not recommended

Headers kept across launches under `oni-moono/`, keyed by path, length and last write time, like
the mod-probe verdict cache. It would save the one remaining open per file on the first opening
after a launch.

It is not exact in every case. A file rewritten with the same length and modification time (a
backup tool restoring with preserved times, a save editor) would show stale details, and on
Windows the last write time is the only key available. With 1 and 3 in place, the remaining
open is cheap enough that the risk is not worth it.

### Not exact, or not worth it

- **Lazy rows or a virtualized colony view** would make fewer objects, but a different
  hierarchy: `ui_dump`s change, mods that patch `ShowColony` or walk the rows break, and the
  scroll bar's size before layout differs. It would no longer be the original's screen.
- **Skipping `CheckCloudLocalOverlap`'s pass when there are no local saves** gains little: the
  pass also logs each corrupt file once, and with the memo (1) it costs only its listing and
  stats.
- **Taking times and sizes from the directory listing** (`DirectoryInfo.EnumerateFiles`) instead
  of a stat each: on Windows the directory entry's times and size can lag behind the file's
  (NTFS updates them lazily, and hard links differ), so the values could differ. On Linux the
  listing has no times, so nothing is saved.

### Ours against the original

Ours is faster at:
- the file work: header I/O 3-5x, listing 3.7x, JSON 1.4x;
- building the colony: 1.5-2.5x;
- its layout.

Ours is slower at:
- decoding previews: 1.4-2.7x;
- drawing a long colony view: 5,071 draws, against an unmeasured count in the original;
- going back from a colony to the list: 237-553 ms against 130.

`MonoDirectoryListing` costs nothing measurable here: on Windows it does not sort, and on Linux
the four listings take 54-71 ms in all.

## Recommendation

1. **The header memo (1) and the parallel reads (3)**, together as one game tweak
   (`fast-load-screen`, in `exact`). Four reads a file become one, and the reads overlap.
   Expected (estimate): ours from 0.8 s to about 0.45 s an opening here, and on Windows most of
   the file system time, at every opening and every Load more.
2. **The previews (2)**: the engine-side faster decode, which is exact by construction, then the
   per-opening pixel memo, which takes the page-10 Load more from 1,100 decodes to 200.
3. **The stacked-handler fix (4)** as a separate tweak, for the user to decide. It is the largest
   win after a few Load more clicks, but it changes logs and fixes a game bug.
4. **Before deciding on fewer draws (5)**, measure the original's draw count in the colony view.

The spec, the `stopwatch` command and `E2E_CLOUD_SAVES` are in place to measure each step
against the original.
