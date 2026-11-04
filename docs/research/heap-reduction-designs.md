# Shrinking the three largest heap owners exactly: designs

Feasibility study, 2026-11-03, on u60 (5964fc9e); master/U59 differences noted where they
matter. heap-soggy-lab.md measured the managed heap of the #48 reporter's "soggy lab 2" (U59
data, 38 mods) and named, after NavGrid's links (since done: compact-nav-links,
navgrid-links-store.md), three owners this study takes in turn: the decompressed AssetBundles
(1,061-1,092 MB), `PathFinder+Cell[]` (901 MB) and `KAnim+Anim+FrameElement[]` (347 MB). The
question for each: can it shrink with the game behaving the same and drawing the same pixels,
how, by how much, at what risk to mods and for what work.

"MB" here is 10^6 bytes; the heap study's figures are MiB (its 901 "MB" is 945 MB here). The
evidence: the game's code (U60 761211 and U59 744825 decompiled, the same in what matters
here unless said), our engine's code, a census of the game's bundles and anim files read with
our own UnityFS and SerializedFile readers (`out/heapdesign/BundleCensus`), an IL scan of the
3,547 Workshop DLLs in `out/ws` and Fast Track (`out/heapdesign/ModScan`), and Linux runs of
soggy lab 2 and c100 with a probe that reads the path grids and the anim groups in the running
game (`out/heapdesign/HeapProbe`; method in §3.1). No Windows runs.

## Summary

| # | Owner and design | Now | Saving (estimate) | Exact | Mod risk | Effort | Verdict |
|---|---|---:|---:|---|---|---|---|
| 1 | Path grids: sparse pages behind PathGrid's `Cells`/`ProberCells` (§3) | 4.9 GB on c100 (205 whole-map grids), 0.84 GB on soggy lab 2 U60 (10 grids), 945 MB in the heap study | **c100 −4.0 GB, soggy lab 2 −0.7 to −0.8 GB** (pages ever written: 15 % and 3-5 % of them after 6 min) | by construction: unwritten pages read as the zeros the array holds | low: only Fast Track touches the arrays (`lock (grid.Cells)`), kept by a handle | medium, hot path: needs a perf check | **go** |
| 2 | Bundles: a decompressed-bundle cache file, mapped (§1) | 1,061-1,092 MiB, every save with every DLC | **−1.06 to −1.09 GiB of heap and commit** (+0.35-0.45 GB with hi-res textures); no decode on later launches | same bytes; a verify mode compares them | none (below `AssetBundle`) | medium; 1.1 GB of disk | **go** |
| 3 | Frame elements: `TrimExcess` after the anims load (§2) | 362 MB (10.05 M slots for 8.31 M elements) | **−63 MB** (−67 with the frame and symbol-frame lists) | only `Capacity` changes | none | a few lines | **go** |
| 4 | Frame elements: deduplicated store behind the list (§2) | 299 MB after 3 | −125 to −132 MB more (45 % of elements are distinct) | yes, by value | high: mods mutate the List with its whole API | high | **later** |

Rejected as not exact: sizing path grids to a world, pooling fewer of them, freeing bundle
nodes once their textures are read, narrowing FrameElement's fields (§1.2, §2.3, §3.1).

The order is by bytes on the saves that hurt: the path grids grow with the Duplicants (about
24 MB each on c100's 636×404 map, 84 MB on soggy lab 2's 1192×768 one), the bundles are a
fixed 1.1 GB on every save. Of the four, 2 and 3 cannot change behaviour at all; 1 cannot
either, but it sits on the path prober's innermost calls, so it needs its speed checked.

## 1. Decompressed AssetBundles

### 1.1 What is held, and by whom

`AssetBundle.LoadFromFile` (and BundlePrefetch, which runs the same decode on start-up threads)
reads the bundle file, `UnityFsArchive.ParseChecked` decompresses every block into one `byte[]
data`, and since ff946780 (u60) / 0552c7e7 (master) every node is an `ArraySegment` window on
that array (`AssetSystem.bundleNodes`). The windows are handed to `SerializedFile.Parse`, to
textures (`TexelSource.Node`) and kept in `bundleNodes` for as long as the bundle is mounted.
The game mounts one bundle per active content pack in `BundledAssetsLoader.OnPrefabInit` and
never unloads one, so each decompressed bundle lives for the session as a single LOH array.
The compressed file bytes are garbage after the decode.

The game's bundles (census of the files; the same decompressed contents on both builds):

| Bundle | U59 file | U60 file | Decompressed | `.resS` (texels) | Serialized file | of which TextAssets |
|---|---:|---:|---:|---:|---:|---:|
| expansion1_bundle | 60.2 MB | 88.4 MB | 517.0 MB | 451.9 | 65.2 | 64.8 (716) |
| dlc5_bundle | 18.3 | 30.1 | 176.5-178.4 | 147.9 | 28.6-32.9 | 28.5 (427) |
| dlc3_bundle | 12.9 | 21.2 | 153.3 | 105.2 | 48.1 | 48.0 (307) |
| dlc2_bundle | 14.0 | 21.1 | 134.4 | 111.9 | 22.5 | 22.4 (305) |
| dlc4_bundle | 11.1 | 17.5 | 96.5 | 69.3 | 27.2 | 27.1 (234) |
| cosmetic1_bundle | 3.7 | 5.4 | 34.5 | 30.1 | 4.4 | 4.3 (55) |
| **All six** | 120.2 | 183.7 | **1,112 MB** (1,061 MiB) | 916 (82 %) | 196 | 195 (2,044) |

The total is the heap study's 1,061 MiB without mods exactly; the mods' own bundles make up
its 1,092. The serialized files are almost all TextAssets (class 49): the kanim `anim.bytes`
and `build.bytes` of the DLCs; the rest (MonoBehaviours, GameObjects, the AssetBundle) is under
0.5 MB. The hi-res bundles (`hires_*`, loaded only with high-resolution textures on, by
LoadFromFileAsync) would add 320 MB (hires_base) and about 30 MB per DLC, the same way.

**U59 and U60 differ in compression.** U59's bundles are one LZMA block each (expansion1: one
517 MB block, 3.9 s to decode on one thread here); U60's are LZ4HC in 3,946 blocks of 128 KiB
(0.56 s). That changes the comparison with Unity: for LZMA, Unity's LoadFromFile also
decompresses the whole bundle into native memory (heap-soggy-lab.md), so on U59 our 1.1 GB is
parity; for chunked LZ4 Unity reads and decodes chunks on demand through a small cache and
holds almost nothing. **On U60 the 1.1 GB is memory Unity does not use.**

**Who reads the data after load.** Nearly nobody:

- **Texels (`.resS`, 916 MB).** A texture from a bundle holds `TexelSource.Node` until its texels
  are first read; loading queues its upload (`SetSource` → `GraphicsDevice.QueueUpload`), the
  upload copies the texels into `PixelMemory` or the device, and `source` is dropped. A
  non-readable texture then drops its CPU copy too. A bundle texture is never reloaded from its
  file (`Unload()` refuses `archive:/` sources). So once the colony's first frames are drawn, the
  `.resS` bytes are read again only for a texture object loaded later (LoadAsset after
  Resources.UnloadUnusedAssets destroyed the first one).
- **Serialized files (196 MB).** Objects are read when loaded (ObjectLoader). The game's root
  prefabs (`Expansion1Assets`, `<dlc>Assets`) pull the whole bundle in at start-up; the
  TextAssets are copied into their own pinned arrays (`TextAssetBinder`), parsed by
  KGlobalAnimParser and released (`KAnimFile.FinalizeLoading` nulls them; the game's
  Resources.UnloadUnusedAssets then unloads them). After that the bytes are read only if
  something loads an object again (a mod's LoadAsset, a reload after an unload).

So about all of the 1.1 GB is dead after start-up, kept only because something *might* read a
part of it again, which an exact design must allow.

### 1.2 Design: a decompressed-bundle cache, mapped

Decompress each bundle once into a cache file and map it read-only, as the data folder's
`.assets` and `.resS` already are (`MappedFile`, `ReadStreamInto`'s view accessor):

- **Cache.** `TemporaryCachePath/oni-moono/bundles/<file name>-<key>.bin`, beside the
  converted-assembly cache (`MonoConversions`). The file is the archive's decompressed data
  exactly (the `data` array today) after a small header (magic, format version, key, length).
  The key is the SHA-256 of the compressed bundle file: 0.12 s for all six U60 bundles on one
  thread here (1.5 GB/s), computed on the prefetch thread, so a changed bundle (a game update,
  a mod rebuilding its bundle) can never meet an old cache, whatever its size and time say.
- **Miss.** Decode straight into a writable mapping of `<name>-<key>.tmp` sized to the data,
  close it, rename it to `.bin` and map that read-only. A crash leaves a `.tmp`, which the next
  start deletes. A failure to create or write the file (disk full, read-only cache folder)
  falls back to today's managed array for that bundle, logged once.
- **Hit.** Map the `.bin`; nothing is decoded. U59 start-up saves the LZMA decode where
  BundlePrefetch had not hidden it (expansion1 alone is 3.9 s of one core).
- **Uncompressed bundles** (all blocks stored, flag 0; none of the game's, some mods' may be):
  map the bundle file itself at its data offset, no cache file.
- **Windows on the data.** `ArraySegment<byte>` becomes `ReadOnlyMemory<byte>` through the same
  path ff946780 changed (`UnityFsArchive.Slice`, `ReadNodes`, `AssetSystem.Mount`,
  `bundleNodes`, `MountedNode`, `TexelSource`, `SerializedFile.Parse`, `BundlePrefetch`): an
  array segment for the fallback and LoadFromMemory/LoadFromStream bundles, a
  `MemoryManager<byte>` over the mapping (which lives for the process, like `MappedFile`'s)
  otherwise. `DataReader` already reads from a mapping.
- **Cleanup.** Keep the newest cache file per bundle name; delete older keys of the same name
  when a new one is written.

**Saving.** The managed heap loses the whole 1,061-1,092 MB, and so does the commit charge,
which is what runs out on Windows (#48). The pages become clean file pages: those read at load
(the texels once, for upload; the TextAssets once) stay resident until the system needs the
memory and then cost nothing to drop; on Windows they are not in the private working set Task
Manager shows. Later launches decode nothing. With hi-res textures on, another 350-450 MB.

**Exactness.** The game reads the same bytes: the decoder's output, written once and mapped,
is the array it would have produced. Verified by:
- a verify mode (`ONI_BUNDLE_CACHE_VERIFY=1`) that also decodes each bundle as today and
  compares it with the mapping byte for byte at load, logging the result;
- CorpusTests and TexelSourceTests over a mapped archive as over an array (both kinds of
  window), and a test that a damaged or truncated cache file (wrong length, header, key) is
  rebuilt, never read;
- a cold start and a warm start of a spec with screenshots, which must match each other and the
  references. No pixel can change without a byte changing, and the verify mode rules that out.

**Mods.** No mod sees the change: it is below `AssetBundle`. Mods' own bundles benefit too.
LoadFromMemory/LoadFromStream keep managed arrays (their bytes are the caller's).

**Costs.** About 1.1 GB of disk (U60 or U59 with every DLC; 1.5 GB with hi-res), written once per
game update; the first launch after an update writes it (one sequential write, on the prefetch
threads, overlapping the decode). Effort: medium, about ten files: the cache and its
invalidation (the new part), the window type change (mechanical), tests.

**Alternative for U60 only: decode chunks on demand**, as Unity does, from the mapped bundle
file. No disk, near-zero resident memory, but every reader then needs chunk-aware access: a
serialized object or a texture straddles 128 KiB blocks, so each read decodes into a
temporary buffer, and DataReader's random access over a serialized file would need a chunk
cache. It does not help U59 (one LZMA block) or LZMA mod bundles. The cache file covers both
builds and every bundle with one mechanism.

**Rejected: releasing nodes when their textures have been read.** A texture object can be
loaded again after Resources.UnloadUnusedAssets destroyed it, and its texels must still be
there; telling when nothing can ask any more is not decidable from the bundle, so it is not
exact.

**Recommendation: go.** The largest and the safest of the three: exact by construction,
invisible to mods, and on U60 it removes memory Unity never spends.

## 2. KAnim frame elements

### 2.1 Layout and counts

`KAnim.Anim.FrameElement` is `{ Matrix2x3 transform (6 floats); KAnimHashedString symbol (int);
int frame; float multAlpha }`: 36 bytes, no padding. Each anim batch group
(`KBatchGroupData`) keeps its elements in one `List<FrameElement> frameElements`, appended one
at a time by `KGlobalAnimParser.ParseAnimData` while the game loads its anims (an element whose
symbol no build of the group has is dropped). A frame (`KAnim.Anim.Frame`) names a run
`firstElementIdx`, `numElements` in that list. The list grows by doubling, so its array has up
to twice the slots it uses; the heap study's 347 MiB (364 MB) is the arrays' size.

Every anim file of the game (census of the TextAssets in the data folder and the bundles,
before the missing-symbol drop, all DLCs):

| | U59 744825 | U60 761211 |
|---|---:|---:|
| Anim files / anims / frames | 2,684 / 17,992 / 430,390 | 2,711 / 18,217 / 436,991 |
| Elements | 8,221,659 (296.0 MB) | 8,380,239 (301.7 MB) |
| Distinct elements (all 36 bytes equal) | 3,672,029 (44.7 %) | 3,761,620 (44.9 %) |
| Distinct transforms / (symbol, frame) pairs / alphas | 2.54 M / 25,033 / 1,869 | 2.61 M / 25,131 / 2,591 |
| multAlpha exactly 1 / frame < 256 | 98.6 % / 99.9 % | 98.5 % / 99.9 % |
| Elements in a frame identical to an earlier frame of its file | 18.6 % | 18.6 % |

In the running game (HeapProbe, U60 with every DLC, no mods; the same on soggy lab 2 and c100,
since the game loads every anim whatever the save): 3,409 batch groups hold **8,306,511
elements in lists of 10,051,332 slots: 361.8 MB**, of which 299.0 MB are used. That is the heap
study's 347 MiB (364 MB) within the mods' few. 3,715,447 elements are distinct over all groups
(44.7 %), 3,905,921 counting each group apart (47.0 %); 2.59 M distinct transforms. The frame
lists (`animFrames`, 12 bytes) take 7.0 MB in 581,848 slots for 436,991 frames, the symbol
frame instances (48 bytes) 6.3 MB in 130,468 for 90,392.

### 2.2 Who reads and writes it

The game: `ParseAnimData` (appends), `KBatchGroupData.WriteAnimData` (once per group, into the
group's anim data texture, `buildAndAnimTex`, applied non-readable), `GetFrameElement(i)`, and
`frameElements[i]` / `.Count` in `KBatchedAnimController.GetSymbolLocalTransform`,
`BlinkMonitor`, `SpeechMonitor`, `FaceGraph`, `LonelyMinionHouse` and a dev tool; all by value
(the List indexer returns a copy). Our tweaks (fast-anim-batches, fast-symbol-overrides) do
not touch it (they use the symbol lists).

Mods (ModScan over `out/ws` and Fast Track; the game's own classes, which some mods ship in
publicized copies, left out):

| Mod | Use |
|---|---|
| Fast Track (KAnimLoopOptimizer.CompareFrames) | `GetFrameElement`, reads |
| Extended Building Width (DynamicAnimManager) | **`RemoveAt`, `InsertRange`** on the list, new elements with changed `frame` and `transform`: rewrites groups' elements at load |
| Lagoo (Patches.AdjustZOrder) | **`set_Item`, `FindIndex`, `FindLastIndex`** with predicates: reorders elements |
| Planetoid Designer (GeyserArtRuntime, previews, PoiNativeRenderer) | **`Add`** of new elements, `get_Item`, `Count` |
| Advanced Mopping, Baiye Resurrection, Duplicant Status Bar, Expeditions, Nuclear Meltdown Event | `get_Item` / `GetFrameElement`, reads |

So unlike NavGrid's links, the store is a BCL `List<T>` that mods mutate with its whole API.

### 2.3 Designs

**A. Trim the lists after the anims load.** `frameElements.TrimExcess()` (and the same for
`animFrames` and `symbolFrameInstances`) once the game has parsed every group
(after `KAnimGroupFile.LoadAll`'s parse, before the batch groups are built). The contents, the
indices and every reader are untouched; a mod that adds later just grows the list again.
Exact: `Capacity` is the only thing that changes, and nothing in the game or the scanned mods
reads it. Saving: 362 − 299 = **63 MB**, 67 MB with the two smaller lists (measured slots
against counts). Effort: a few lines in OniMoono.GameTweaks; a check that every
group's Count and contents are the same with the tweak on and off.

**B. Deduplicate behind the list.** 55 % of the elements repeat an earlier one bit for bit
(idle frames that hold still, symbols at rest), so a store of one 4-byte index per element into
a table of distinct elements holds the U60 set in 8.38 M × 4 + 3.76 M × 36 = 169 MB instead of
302 MB (+ the lists' slack); splitting out the 2.6 M distinct transforms brings it to about
141 MB. In the game the global table gives 8.31 M × 4 + 3.72 M × 36 = 167 MB, one table per
group 174 MB: 125-132 MB less than the trimmed lists. It would be built like compact-nav-links: keep the field a
`List<FrameElement>` (a handle the store names), rewrite each `List<FrameElement>` call in the
game and mods to the store's helpers, and fall back to the real list for anything else. But the
List is not an array: the rewrite has to cover `get_Item`, `get_Count`, `Add`, and for the
mods `set_Item`, `RemoveAt`, `InsertRange`, `FindIndex`/`FindLastIndex` with predicates, and
enumerators, or fall back whenever one of them appears (Extended Building Width and Lagoo, at
least, would always fall back). An unrewritten access (reflection, a later transpiler) would see
an empty list and silently find no symbols: no exception to catch it. Exactness itself is easy
(the elements are stored bit for bit, by value, and nothing takes a ref to one); the risk is in
covering the API. Effort: high, for 125-132 MB.

**C. Narrower fields.** `symbol` is a hash (all 32 bits used); `transform` and `multAlpha` are
floats that must stay bit-identical; only `frame` fits 16 bits. Changing the struct changes
every `ldfld` in the game and the mods for 2 bytes of 36. No.

**Recommendation: A go (tiny, exact); B later**, if the heap still matters once the bundles and
path grids are done, and then with mutation falling back to the list, not covered.

## 3. Path grids

### 3.1 What is allocated

A navigator's `PathGrid` (made in `Navigator.OnPrefabInit`) is the whole cluster grid
(`Grid.WidthInCells × HeightInCells`, `applyOffset` false) unless the navigator has a probe
radius (critters, Sweepy: small offset grids). Duplicants, Rovers and Flydos have none. Each
whole grid holds `PathFinder.Cell[cells × nav types]` (12 bytes: `cost`, `parent`, `queryId`,
packed nav types, `transitionId`) and `ProberCell[cells]` (8 bytes). The Duplicant grid has 7
nav types, so on soggy lab 2 (915,456 cells) one grid is 76.9 MB of Cells and 7.3 MB of prober
cells; on c100 (256,944 cells) 21.6 MB and 2.1 MB (the "21.7 MB per Duplicant").

AsyncPathProber's manager keeps a pool per grid shape (`gridPool`, keyed by width, height and
nav type count): its single worker takes a fresh grid from the pool for each probe, the
navigator takes the result in `TakeResult` and gives back its old grid. The pool keeps up to
`4 + agents` = 5 idle grids. So a shape has one grid per navigator plus up to five.

**Measured.** HeapProbe (`out/heapdesign/HeapProbe`, about 250 lines, not a mod) is loaded into
our player through E2eDriver's `setup.call`, as NavProbe was for the links. It finds every
navigator's grid and the pool's idle ones, reads both arrays by reflection, and counts, at
several page sizes, the pages holding any non-zero byte: the pages a grid has ever written,
which a sparse store would have to hold. It dumped at load and 2 and 6 minutes into play
(wall time, speed 3). Run on U60 761211 with every DLC, our player at u60 5964fc9e (release,
Linux, Xvfb, `--game-tweak exact path-probe-repair`, the users' defaults for what matters here),
through `scripts/e2e.py` on the shared queue; soggy lab 2 without its mods (they are U59 builds,
heap-soggy-lab.md), c100 from the fixture.

| | soggy lab 2 (1192×768) | c100 (636×404) |
|---|---:|---:|
| Whole-map grids found (Duplicants; critters have 65×65 offset grids) | 6 + 4 pooled, then in use | 205 + 5 pooled (1 idle) |
| One grid: Cells + ProberCells | 76.9 + 7.3 MB | 21.6 + 2.1 MB |
| All whole-map grids | about 840 MB | about 4,860 MB |
| Serial after 6 min | 11,309 (wraps at 65,535) | |
| Pages ever written, Cells, at 256 elements (3 KB) | 2.6 % | 14.8 % (mean of the grids) |
| Pages ever written, ProberCells, at 256 elements (2 KB) | 6.9 % | |
| All grids paged at 64 / 256 / 1,024 / 4,096 elements, Cells + ProberCells (incl. critters') | 17 / 28 / 55 / 123 MB | 419 / 831 / 1,533 / 3,191 MB |
| Map area of the worlds the grids touched | 4.0 % | 41.9 % |

(The soggy totals count the 6 Duplicants' grids and the critters'; the 4 pooled grids were out
at both dumps and are not in them. The counts grew between the 2- and 6-minute dumps by 1-7 %.)

A Duplicant's grid is mostly never written: the probe floods only what the Duplicant can reach,
and the rest of the cluster (rock, space, other asteroids) stays as allocated. soggy lab 2's six
Duplicants live on a few asteroids of a 15-world cluster, so their grids are 97 % untouched;
c100's 205 share one large asteroid and still leave 85 % of the pages untouched. Over a long
session a grid only gains pages: the pool hands each grid object to many navigators in turn,
so every grid tends towards the union of the colony's reachable cells. That union is the
long-run bound, and it is far below the whole map on both saves (the "worlds touched" row is
an upper bound for it).

**Exactness constraint: stale cells.** A grid is never cleared. `GetCell` accepts a cell whose
`queryId` equals the grid's serial number and treats the rest as unvisited; the serial is a
`ushort` that wraps every 65,535 probe ticks, and `ResetProberCells` on a wrap clears only the
prober cells. After a wrap, a cell left by a probe 65,536 ticks ago with the same serial reads
as visited (and its cost prunes the new probe's potentials there). That is the game's behaviour
and must be kept, so which grid object a probe gets, and everything that grid holds, is part of
the behaviour: a design may not clear, drop, shrink or swap grids differently.

That rules out:
- **Sizing a grid to its navigator's world.** Probes cross worlds (teleporters, rocket
  interiors), a pooled grid serves navigators of every world, and a cell outside the bounds
  would answer "out of range" where the game's grid answers with its (stale or empty) content.
- **Pooling fewer grids, or freeing idle ones.** A grid made fresh later is empty where the
  old one held stale cells.

### 3.2 Design: sparse pages behind the same arrays

Keep every grid, every write and the pool's flow, and store each grid's `Cells` and
`ProberCells` in pages allocated on first write. A page never written reads as zeros, which is
what the game's array holds there (it was allocated zeroed and nothing wrote it); a page once
written holds exactly what the array would. The arrays' content is the same at every index at
every moment, stale cells included, so the design is exact by construction.

- **The rewrite.** Both fields are private to PathGrid, so the accesses are PathGrid's own:
  the constructor (`newarr`), `GetCell` (`Cells.Length` and one read), `SetCell` (one write of
  each array and one prober read), `GetProberCell`, `GetCost`, `GetCostIgnoreProberOffset` and
  `ResetProberCells`. The same load-time rewriter as compact-nav-links' replaces those element
  accesses with calls to a page store and keeps the fields as one-element handle arrays.
  Keeping them non-null objects matters: Fast Track (master) takes `lock (grid.Cells)` in its
  BeginUpdate, OffsetCell and RunAsync patches, and that keeps working on a handle.
- **Pages.** A fixed number of elements a page (256 proposed, see "Page size" below) and a
  page table per array. A read is `pages[i >> 8]` and, when that is null, a zero cell; a write allocates the
  page if needed and publishes it with a volatile store. The worker writes only the grid it
  holds and the main thread reads only navigators' grids, as now; a reader racing a page's
  creation reads zeros, which is what it would have read a moment before.
- **ResetProberCells** clears the written prober pages (or frees them: zeros either way).
- **path-probe-repair** reads the two arrays directly (`cellsOf`/`probersOf` in ProbeRepair.cs)
  to diff and to write the repaired result into the navigator's grid; it would read and write
  through the page store instead. Its own scratch is whole-cluster too: the probe thread's
  ProbeEngine keeps 25 bytes a state (`ov`, `ovFlags`, `ovStamp`, `mark`, `setAt`: 160 MB on
  soggy lab 2) and every Duplicant 4 bits a state (3.2 MB). All of it is stamp-validated, so
  the same pages fit it and, being ours, without exactness questions; it is one engine, not one
  per Duplicant.

**Page size.** 256 elements (3 KB of Cells, 37 map cells of the Duplicant grid; 2 KB of
prober cells) holds 2.6-15 % of the Cells; 1,024 doubles that, 64 saves little more and costs a
larger table (a 256-element table is 25,000 pointers, 200 KB, per soggy-lab grid; 56 KB on c100).

**Saving.** At 256 elements: **c100 4.86 GB → about 0.83 GB (−4.0 GB)**; **soggy lab 2 about
840 MB → about 30 MB for the ten grids now (−0.8 GB)**, and still −0.7 GB if a long session
made every grid write five times the pages it has after six minutes. The heap study's 945 MB
(soggy lab 2 on U59 with its mods: about 11 grids) shrinks the same way. The saving is managed
heap and commit; on Linux, and in Windows' working set, the never-written parts of a large
array may not be resident today (fresh LOH memory is zero pages the GC does not touch), so
resident memory falls by less than the heap, except where the GC reused memory for a grid and
cleared it. What a Windows player runs out of (#48) is commit, and that falls in full.

The same holds for path-probe-repair's own whole-cluster arrays: 4 bits a state per Duplicant
(0.9 MB a Duplicant on c100: 185 MB for 205; 3.2 MB on soggy lab 2) and the probe thread's 25
bytes a state (45 MB on c100, 160 MB on soggy lab 2). They are ours and stamp-validated, so
the same pages can hold them without an exactness question.

**Verification.** A verify mode that keeps the flat arrays beside the pages and compares every
`GetCell`, `GetCost` and `BuildPath` answer (and at intervals the whole arrays) with them; the
probe digest (`ONI_PROBE_DIGEST`, the per-result digests of reachable cells and costs) with the
store on and off on c100, DeCrew and soggy lab 2 over long runs, past a serial wrap. Probe time
on the worker (frame stats' probe counters) and a Windows frame-time check at speed 3, since
`GetCell` and `SetCell` are the probe's innermost calls: one more dependent load and a null
test per access.

**Mods.** ModScan: only Fast Track touches the arrays (the locks above); patches on PathGrid
(Fast Track's BeginUpdate and OffsetCell prefixes, JiffyTube's BuildPath) call the rewritten
methods and keep working; no mod transpiles GetCell, SetCell or the prober methods, and none
reflects on `Cells` or `ProberCells` (the scan's "Cells" strings are unrelated). The mode
decision follows compact-nav-links': flat if a transpiler not on a reviewed list patches one of
the rewritten methods.

**Effort.** Medium: smaller than compact-nav-links (one class, private fields, about ten
accesses, one reader of ours) but on the probe's hot path, so it needs the perf check.

**Recommendation: go**, the largest saving on a big colony (−4 GB on c100), exact by
construction, with one known mod user that keeps working. First a micro-benchmark of GetCell
and SetCell over a dumped grid (as NavBench did for the links), then the probe-time and Windows
frame-time checks above. If paging costs the probe noticeably, 2D tiles (16×16 cells touched
2.9 % on soggy lab 2, 17 % on c100) hold about as much and keep a cell's vertical neighbours
in its page more often, at the price of a division to find the tile; the benchmark decides.

### 3.3 Built (2026-11-03): `paged-path-grids`

Design 3.2 as the game tweak `paged-path-grids` (Simulation, exact, on by default; TWEAKS.md):

- **The tables** (`src/OniMoono.Host/PathGridPages.cs`). A table is a `T[][]` of 256-element
  pages; every unwritten slot names one shared, never-written `Zero<T>.Page`, so a read is
  `pages[i >> 8][i & 255]` with no test, and the last page is allocated at the array's exact
  end, so the two bounds checks throw where the array's would. A store of anything but zeros
  into the zero page makes a page (on the pinned object heap: a page lives as long as its grid,
  and would otherwise be copied by each GC promotion) and publishes it with one
  compare-and-exchange; a store of zeros there stores nothing. Flat mode is the same code with
  one page, the game's array itself (`index >> 31` is 0 for every valid index), and the page
  size is a `static readonly` set once the first PathGrid decided the mode, so tier-1 code folds
  it.
- **The rewrite** (`PathGridPagesRewrite.cs`, in MonoConversions' pass after
  compact-nav-links'). PathGrid gets `<Cells>pages`, `<Cells>length` and the same for
  ProberCells; the constructor's `newarr` becomes `PathGridPages.New` and `Cells` gets
  `Handle` (one element, each grid its own, so `lock (grid.Cells)` keeps working); each
  `ldfld Cells` followed in straight-line code to its element access becomes the table's load
  with `Load`, `Store`, `Clear` (`ldelema` + `initobj` in ResetProberCells) or the length
  field (`ldlen`). The game has 12 accesses in 7 PathGrid methods, the same in U59 744825 and
  U60 761211; nothing else of the assembly changes (PathGridPagesTests compares every method).
  Mods are only checked: of the 4,580 DLLs of `out/ws` (library copies included) none uses the
  arrays; Fast Track's three locks are kept. Anything else, a mod assembly naming the arrays
  loaded without the check, or a transpiler of a rewritten PathGrid method makes the session
  flat (`PagedPathGrids.Decide`, logged).
- **path-probe-repair** reads and writes a grid through `ProbePages<T>` views of the same tables
  (pages made through the host's `Materialise`, verify mode's flat array kept written). Its own
  whole-map arrays are paged the same way: each Duplicant's path flags (c100: 183.5 → 14.7 MB
  for 204 Duplicants in the capture's replay) and the probe thread's repair scratch.
- **Verification.** `ONI_TWEAK_VERIFY=1` keeps a flat array beside each table, written by the
  same helpers, and compares every element every 30 s of play: 0 differed on c100 (1,446
  tables, 439.6 M elements, 4 comparisons), soggy lab 2 without its mods (1,146 tables, 105.8 M,
  4) and DeCrew (874, 23.4 M, 5), with path-probe-repair's own checks (repairs against floods,
  floods against the game's PathProber.Run on a rewritten grid) at 0 wrong as well.
  A write stores into the page and then into the flat array, and the comparison runs on the
  main thread while the probe threads write, so it can read an element between the two stores.
  The first comparison looked again after 1 ms, and that was not enough: a writer that the GC
  or the scheduler stops between its stores stays there longer, and one release QA run reported
  "1 differ" from that alone. Every write is now counted as it begins and as it ends, the host's
  helpers and path-probe-repair's views alike, and an element that differs is compared again,
  its page read afresh, only while no write of its table runs. Only a difference that remains
  then counts; the summary also gives the number seen mid-write.
  `--check-types` runs the loaded, rewritten PathGrid's own methods on a 40×20 grid against a
  model of its arrays (96,000 reads). Unit tests: reads and writes against a flat array in both
  modes, out-of-range indices, zero stores, writers racing on page creation with readers.

**Speed.** The micro-benchmark (`out/pathbench`: the game's PathProber.Run/AddPotentials over
GetCell/SetCell on dumped Duplicant grids, six pooled grids) put paging within this machine's
noise: paired medians 1.01-1.09 of flat for the layouts tried, the one built 1.01-1.02.
path-probe-repair's ProbeBench replays (c100, DeCrew captures; flat and paged alternating in one
process, pages made up front) gave floods 1.05 and 1.16 of flat; with every page made during
the replay (204 grids filled at once) 1.3-1.9, the first writes' cost, which a game pays once
per grid. In the game the probe thread showed no cost: on Linux (c100, speed 3, 3-5 minutes,
PageProbe timing WorkOrder.Execute's CPU) the game's own probe took 3.87 ms a probe flat and
3.96 paged, path-probe-repair's 0.87 and 0.87; on Windows (below) path-probe-repair's 0.66,
0.47 flat and 0.59, 0.60 paged.

**Memory and frame time.** Windows, U60 761211, c100, Vulkan defaults, speed 3, 1280×720 on
monitor 2, `--no-focus`, High priority, sound off, path-probe-repair on; base `--game-tweak
-paged-path-grids` (the game's IL as it is), base, new, base, new, 4,200 frames each, the last
line of frame stats' memory; other agents' Linux players loaded the machine:

| | base | paged |
|---|---:|---:|
| Managed heap | 8,729, 8,676 MiB | 4,308, 4,339 MiB |
| GC commit | 9,545, 9,416 MiB | 5,261, 5,270 MiB |
| Private bytes (the commit Windows charges) | 15,441, 15,291 MiB | 11,199, 11,104 MiB |
| Working set | 10,212, 10,083 MiB | 9,579, 9,618 MiB |
| Frame rate (the last four 600-frame windows) | 46.2, 54.2 | 46.3, 56.1 |
| Main thread (frame stats' parts) | 25.7, 16.1 ms | 17.8, 19.6 ms |
| `path probes` (main thread) | 0.18, 0.13 ms | 0.15, 0.17 ms |
| GC pauses | 0.34, 0.25 ms a frame | 0.52, 0.36 ms a frame |

The heap and the commit fall by 4.2-4.4 GiB, as estimated; the working set by 0.5 GiB only
(fresh LOH memory the game never wrote is not resident). Frame rate, main-thread time and the
probe thread moved within the runs' spread. On Linux (PageProbe after a full GC, 2 and 5
minutes in, without its mods for soggy lab 2): c100's heap 8,924-9,366 → 4,575-5,059 MB and GC
commit 9,901-10,044 → 5,366-5,870 MB; soggy lab 2's heap 4,267 → 3,276 MB and commit 5,554 →
4,189 MB (one pair on u60 at 19071e27, after the mapped bundle cache; path-probe-repair's probe
0.18 → 0.21 ms), its grids' 1,044-1,129 MB of arrays held in 46-52 MB (more grids than §3.1
counted: the critters' grids are paged too).

## Files

Not committed (the worktree's `out/heapdesign`): BundleCensus (bundle nodes and class sizes;
`census-u59.txt`, `census-u60.txt`; anim element statistics, `anims-u59-all.txt`,
`anims-u60-all.txt`), HeapProbe and its specs (`specs/heapprobe-soggy.yaml`,
`specs/heapprobe-c100.yaml`; dumps in `probe-soggy/`, `probe-c100/` and, at load only,
`probe-soggy-load/`), ModScan and `modscan.txt`, Fast Track's decompiled path patches (`ft/`).
§3.3's (the building worktree's `out/`): `pathbench` (the micro-benchmark and its dumps,
`engine-*.sh`, the ProbeBench comparisons), `pathgrid/PageProbe` and its specs
(`pathgrid/specs`, results in `pathgrid/measure`), the Windows scripts and logs
(`pathgrid/win-*.sh`, `winsum.py`, `pathgrid/winperf`).
