# The managed heap of a heavy save

Why "soggy lab 2" (a 1192×768 Spaced Out cluster at cycle 806, 38 mods) holds 7-10 GB of
managed heap in our player, who owns it, and what of it is the engine's and avoidable.
Measured 2026-11-02 on Linux (WSL, 16 cores), OpenGL on Xvfb, release builds, one player at a
time on an otherwise idle machine (runs that overlapped another agent's players were thrown
away), the save loaded through PerfProbe's `load.txt` and run at speed 3 with autosave off.

**Game build.** The save's mods are the U59 Workshop builds (Fast Track 0.18.6, Rocketry
Expanded 2.15.0 and 36 mods that pass `scripts/mod-gate.py`; `out/slowsave/mods-template`). On
U60 761211 Fast Track's patches fail (`LocText.ModifyLinkStrings` and
`KBatchedAnimController.lastChunkXY` are gone), and so do Automatic Geyser Calculation's,
Rocketry Expanded's and Better Info Cards'; the game's mod-error dialog then quits. So the
measurements are master (08ed561b) on the U59 744825 Linux data with the DLC content
(`out/local-tools/u57game`). The code that holds the heap (AssetSystem, ObjectTable, the
natives) is the same on u60 (3ac5d19f).

**Method.** `dotnet-counters` (System.Runtime) from start-up; 60 s after the colony was up, a
heap dump (`dotnet-dump collect --type Heap`) read with ClrMD: every object by type and module,
and an owner for each: the nearest object on its breadth-first path from the roots, itself
included, whose type is not the BCL's. Static fields are walked first, so what a static holds
belongs to the class declaring it. `dotnet-gcdump` does not work at this size: its reader stops
at 10,000,000 nodes ("Exceeded max node count") and saw 1.1 of 7.5 GB. GC reasons come from a
`dotnet-trace` of the GC keyword. The original's figures are Unity's
`Profiler.GetMonoUsedSizeLong` / `GetTotalAllocatedMemoryLong`, logged by PerfProbe's new
`memory.txt` (the same calls in ours return the .NET heap and the working set).

## Who owns the heap

With mods, at the dump: 7,536 MB in 20.3 M objects (5,621 MB of it on the LOH), 377 MB of free
blocks; the GC reported 8,306 MB allocated and 8,459 MB committed. Without mods: 6,810 MB in
14.3 M objects (5,325 MB LOH), 805 MB free.

| Owner | With mods MB | % | Without mods MB | % |
|---|---:|---:|---:|---:|
| Game: Assembly-CSharp | 4,323 | 57.4 | 4,137 | 60.8 |
| Engine: decompressed AssetBundles (AssetSystem.bundleNodes) | 1,092 | 14.5 | 1,061 | 15.6 |
| Engine: everything else (OniMoono.\*, UnityEngine facades, UI packages, host) | 884 | 11.7 | 648 | 9.5 |
| Game: Assembly-CSharp-firstpass | 717 | 9.5 | 686 | 10.1 |
| Unreachable (dead, not yet collected) | 388 | 5.2 | 266 | 3.9 |
| Fast Track | 62 | 0.8 | - | - |
| Harmony (MonoMod caches) | 36 | 0.5 | 4 | 0.1 |
| The other 37 mods together | 19 | 0.3 | - | - |
| Roots holding BCL objects directly, BCL, other game libraries | 14 | 0.2 | 8 | 0.1 |

The mods add little of their own. Their larger effect goes through game types: `CavityInfo`
cell lists are 202 MB with mods and 13 MB without, held through Fast Track's
`ConcurrentDictionary<int, CavityInfo>` (BackgroundRoomProber); the mods also add about 200,000
components (1.06 M `BehaviourNative` against 0.86 M).

The game's own share is its simulation data, the same objects Unity's Mono heap holds:
`NavGrid+Link[]` 2,277 MB (19 nav grids of 915,456 cells × links per cell × 8 bytes),
`PathFinder+Cell[]` 901 MB (AsyncPathProber's pooled path grids), `KAnim+Anim+FrameElement[]`
347 MB, `UtilityNetworkGridNode[]` 126 MB, `PathGrid+ProberCell[]` 95 MB.

**Against the original.** Unity's managed heap at the colony: 4,913 MB without mods, 5,128 MB
with them; ours at the same point 6,395 and 6,766 MB, so about 1.5-1.6 GB more. That gap is
the engine's 1.7-2.0 GB above, which Unity keeps in native memory: the native allocations
Unity tracks were 1,191 MB at the colony without mods and 2,014 MB with them, in all. For
the bundles Unity's documentation says the same of LoadFromFile: "In case of LZMA compression,
the file content will be fully decompressed into memory and loaded from there." The game's
bundles are LZMA, one block each (expansion1_bundle: 57 MB on disk, 493 MB decompressed). So
the bundle bytes and the native records replace native memory; they do not add to what Unity
holds. RSS does not compare across the two players here (llvmpipe for the original, d3d12 for
ours).

After the colony is up the heap grows from 6.8 to 7.4-7.5 GB used with mods, with 9.0-9.6 GB
committed and 14.0 GiB RSS; the Windows figures (7.2-9.7 GB heap, about 15 GB working set)
match.

## Engine candidates

Ranked by bytes. "Exact" means the game sees the same results.

1. **Decompressed bundles kept on the managed heap: 1,061-1,092 MB, steady.** `AssetSystem.Mount`
   keeps every node of every mounted bundle as a `byte[]` (`bundleNodes`), for SerializedFile
   reads and texture reads (`TexelSource.Node`, `ReadStreamInto`); the game never unloads its
   bundles. The largest nodes are the `.resS` texel streams: 431, 141, 107, 100, 66 MB.
   Unity also holds them (above), so this is parity, not waste; a fix would beat Unity:
   decompress each bundle once into a cache file and map it, as the data folder's `.assets`
   and `.resS` already are (`SerializedFile.Open`, `ReadStreamInto`'s MemoryMappedViewAccessor).
   Heap −1.06 GB, the pages become clean file pages the kernel can drop, and later launches
   skip the LZMA decode. Exact (the same bytes). Not small: the cache's place and invalidation
   (size, time, hash of the bundle), about 1.1 GB of disk, and `LoadFromMemory` bundles stay as
   they are.
2. **Each bundle decompressed twice at load: about 1.1 GB of extra LOH allocation.**
   `UnityFsArchive.Open` reads the file (`File.ReadAllBytes`), `ParseChecked` decompresses all
   blocks into one `data` array, and `AssetSystem.ReadNodes` copies each node out of it
   (`UnityFsArchive.Read`): every decompressed byte is allocated twice, and the first copy is
   garbage at once. With nodes as slices of `data` (ArraySegment through Mount, MountedNode,
   TexelSource and a SerializedFile.Parse with an offset) the copies go: −1.06 GB of LOH
   allocation and of peak memory during load, fewer AllocLarge gen2s (16 at load with mods).
   Exact. Medium: about eight files; a texture that still holds its source after
   Unload(false) would keep the whole array, its serialized part included.
3. **Blocking gen2s while mods load: about 86.** Of the 108 gen2s by the colony with mods, 89
   were induced (blocking): 86 before the save started loading, while the mods' DLLs loaded,
   and 3 during the load (the others: 16 AllocLarge background ones, 3 small). Without mods
   there are 38 gen2s in all. Our host probes each mod
   assembly in a collectible context and then loops `GC.Collect()` +
   `WaitForPendingFinalizers()` until the context is gone (`MissingInterfaceMethods.Collect`,
   so PLib cannot pick the probe's copy from `AppDomain.GetAssemblies`); the timing matches
   those loads (call stacks were not captured). On a loaded machine they paused about 2.9 s in
   all; these are likely most of the "118 gen2 collections mostly at load" seen on Windows. A fix: keep
   each probe's verdict (the methods CoreCLR finds missing) in a cache keyed by the image's
   SHA-256 and the identities of the assemblies it binds to, and skip the probe and its
   collections on later launches. Exact while the key covers every input.
4. **ComponentLayout match tables: 33.5 MB (19 MB without mods).** Each of 9,277 interned
   layouts has an `int[][]` indexed by the global query id (470 slots on average) though it is
   asked few types; every new id copies the table (copy on write). A dense per-layout index
   would save most of it. Exact; it is GetComponent's hot path, so it needs a perf check.
5. **GL diff copies of re-uploaded textures: 36.9 MB (21 MB without mods).**
   `DeviceTexture.UploadedTexels` keeps the last upload of each texture re-uploaded into the
   same storage (up to 4 MB each, 620 textures), so the next upload sends changed rows only.
   Memory for upload bandwidth; exact either way.
6. **Mesh UVs stored as Vector4: 33 MB in MeshNative.UvSet, 8.6 MB in CanvasGeometry.** Two-
   component UVs kept as 16-byte Vector4s; per-dimension storage would save about half.
   Replaces Unity's native vertex data; exact if the dimension is kept.
7. **Sorting-layer closures: 107 MB of garbage between gen2s.** 1.34 M
   `SortingLayers+<>c__DisplayClass4_0` and 1.25 M `Predicate<(string, int)>`, from
   `Array.FindIndex` in `SortingLayers.IndexOf` (called twice per `ValueOf`, per renderer and
   canvas sort). Already fixed on branch `perf-sortinglayers-master` (7c536dad, bd82faf5), not
   merged.
8. **PatchDependents' call graph: 58 MB (15 MB without mods).** Kept for patches made later;
   dropping it after the mods load is not exact if a mod patches later.

The rest of the engine's share is the native records Unity also has natively:
`BehaviourNative` 121 MB (1.06 M, 114 bytes each), `ObjectTable` 71 MB (the instance-id
dictionary and slots), Vector3/Vector4/Vector2/Color mesh arrays, `CanvasRendererNative` 31 MB,
`RectTransformNative`, `GameObjectNative` and `TransformNative` about 24 MB each, TMP's
`TMP_CharacterInfo[]` 42 MB (managed in Unity too).

## GC settings

The host runs Server GC (15 heaps here) with background GC, no conserve setting and no
GCHeapHardLimit. LOH fragmentation is small: 279 MB free in the LOH at the dump (4.5 %), 114 MB
in the pinned heap. The settings were tried with mods, one run each (Linux timings, so for
direction only):

| Setting | Load (s) | GC pause by the colony (ms) | gen0/1/2 by the colony | Heap after 90 s (MB) | Committed (MB) | RSS (GiB) |
|---|---:|---:|---|---:|---:|---:|
| Server (as shipped) | 19.8 | 5,606 | 174/128/116 | 7,809 | 9,552 | 14.0 |
| Server, GCConserveMemory=5 | 19.0 | 6,177 | 177/124/113 | 8,203 | 9,367 | 13.9 |
| Server, DATAS (GCDynamicAdaptationMode=1) | 19.9 | 5,161 | 183/129/117 | 7,998 | 9,482 | 14.0 |
| Workstation, concurrent | 20.4 | 15,332 | 301/226/106 | 9,506 | 9,560 | 13.8 |

None of them cuts the heap: it is live data. Conserve memory trims about 0.2 GB of commit and
DATAS changes nothing at this size; Workstation triples the start-up pauses. A hard limit would
only bring the out-of-memory point closer. The gen2 count is the induced collections of item 3,
which no setting removes. Steady-state frame times need a Windows run before anything is
concluded from them.
