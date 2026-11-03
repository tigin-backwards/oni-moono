# Video memory at start-up and on big saves: four levers checked

Measured 2026-11-03. The question came from issue #48's reporter (GTX 1650, 4 GB, OpenGL), whose
log showed about 150,000 driver evictions (1.1 TB moved) and a 36 s first frame before the main
menu, and from our U60 baseline of 3.9-4.8 GB dedicated video memory on big saves (RTX 3090).
Compressed textures already stay compressed, and lazy uploads are not wanted. Four exact levers
were checked: render textures allocated earlier or held longer than Unity's player does, freed
textures released late, transient upload memory, and mip chains longer than asked for.

**Verdicts.**

1. **Render textures: none held when the original would not.** Ours allocates later than Unity,
   never earlier, and frees at once on Release and Destroy, as Unity does.
2. **Freed textures: no lag and no leak.** Destroy, UnloadUnusedAssets and the temporary pool
   free device memory at the same points as the original. Vulkan frees two frames later (its
   frames in flight).
3. **Upload staging: Vulkan's was unbounded.** A start-up frame staged 3.1 GiB of host-visible
   memory, 3.3 GB of "shared" video memory that the original never uses (283 MB at most). A
   64 MiB bound (u60) brings the process's shared peak to 245-373 MB on Windows; the load costs
   1-2.5 s more in one run each (noise not separated). OpenGL's
   is bounded (background reads 256 MiB, the copy pool 128 MiB). The copy pool is too small for
   c100: it allocates 5.8-7.5 GiB per 600 frames in new buffers.
4. **Mip chains: one case, fixed.** Textures get exactly their asset's mip count. A render texture
   with mipmaps got the full chain whatever its descriptor's `mipCount` asked for. The game makes
   none; mods may.

The whole-process difference is small at the menu and moderate on c100. Ours uses 3,163-3,290 MB
dedicated at the menu against the original's 3,147 MB, and 4,285 (OpenGL) and 4,410 MB (Vulkan)
against 4,053 MB in c100's last minute. The OpenGL texture ledger matches Unity's own texture
counter within 1% at the menu. One more finding: NVIDIA's eviction counter counts every eviction
on the system, so #48's 150,000 cannot be read as our own. The frame stats now give the count
since our device started.

## Method

- **Players.** The original is U60 761211 on Windows (`E:\oni-builds\BrutalNotIncluded-761211`,
  D3D11, BepInEx isolation). Ours is the `u60` branch plus these changes, on `oni-win-761211`, on
  Vulkan and on OpenGL (`--renderer`), with `--game-tweak none` and `--frame-stats`. Every run was
  1280x720 windowed on monitor 2, unfocused, at High priority, with sound off, one at a time
  under the shared Windows lock.
- **Video memory.** The process's `\GPU Process Memory(pid_*)\Dedicated Usage` and `Shared
  Usage` counters, sampled every second from outside by its PID, found from its run home
  (`out/vram/win/vram-sample.ps1` in the measuring worktree). The phases below are medians from
  each phase's third second to its end.
- **What each player reports.** UnityProbe's new `vram` scenario
  (`tools/UnityProbe/VramProbe.cs`, `tests/e2e/vram-probe.yaml`) at the main menu, in both players,
  gives the timing data. It makes eight 4096x4096 ARGB32 render textures with 24-bit depth and
  walks them through construction, Create, Release, a Blit, Release, reuse and Destroy. It then
  covers RenderTexture.active, copies made with `new RenderTexture(rt)`, the temporary pool,
  Texture2D Apply and Destroy, UnloadUnusedAssets of unreferenced textures and mip counts. Each
  phase holds 6 s and logs the UTC time, IsCreated and, in the original, Unity's own counters
  (`Texture.currentTextureMemory`, `Profiler.GetAllocatedMemoryForGraphicsDriver`).
- **Our side.** The new `--frame-stats` counters (below) on Windows. On Linux they ran through the
  e2e queue (Xvfb, Mesa d3d12, master on U59 and u60 on U60), with c100 run for 240 game seconds.
- **Saves.** The main menu with no save, and `c100.sav` (200 Duplicants) resumed from the menu
  and held for 7 minutes.

## Whole process (Windows, RTX 3090, U60)

| | Original (D3D11) | Ours, OpenGL | Ours, Vulkan |
|---|---|---|---|
| Main menu, dedicated (last minute) | 3,147 MB | 3,163 | 3,290 |
| Main menu, shared (last minute) | 7 MB | 32 | 437 |
| Start-up peak, shared | 283 MB | 136 | 3,315 |
| c100, dedicated (last minute) | 4,053 MB | 4,285 | 4,410 |
| c100, shared (last minute) | 225 MB | 32 | 473 |
| c100, peak dedicated / shared | 4,078 / 336 MB | 4,285 / 32 | 4,432 / 3,315 |

Unity counts 2,928 MB of textures at the menu (3,740 textures, no mip streaming, mipmap limit 0).
Our OpenGL ledger asks for 2,944 MB in 3,700 textures (2,311 MB of them DXT5).

On c100 ours holds 232 MB (OpenGL) and 357 MB (Vulkan) more dedicated memory than the original.
The ledgers say where c100's textures go: DXT5 2,343 / 2,366 MB, the animation batches' RGBAFloat
data textures 690 / 982 MB (6,550 of them), and render targets about 100 MB. The Vulkan RGBAFloat
figure is VMA's allocation sizes and the OpenGL one our own estimate. For the same 6,550 small
textures the two differ by about 46 KB a texture, which looks like per-image alignment (not
verified). The rest of the gap to the original is not explained here: the D3D11 player's per-image
overheads cannot be read from outside.

## 1. Render textures: allocation and release

| Phase (8 targets, 4096x4096 ARGB32, 24-bit depth) | Original | Ours, OpenGL | Ours, Vulkan |
|---|---|---|---|
| Constructed | not created, +0 | not created, +0 | not created, +0 |
| `Create()` | created; driver +1,536 MB, dedicated +0 | created, +0 | created, +0 |
| `Release()` | not created, freed | +0 | +0 |
| First Blit | dedicated +1,536 MB | +1,024 MB | +1,033 MB |
| `Release()` after use | freed (back to 3,163) | freed | freed (3,311) |
| Blit again | +1,536 | +1,024 | +1,024 |
| `Destroy` | freed the same frame | freed | freed |
| `RenderTexture.active` and back (4, no depth) | +256 MB | +0 | +0 |
| 8 copies (`new RenderTexture(rt)`), unused | +0 (the used source: +192) | +0 (source +128) | +0 (source +128) |

Unity's `Create()` reserves the storage: its driver counter rose by 1,536 MB, though Windows
committed the memory only when the texture was first written. Becoming `RenderTexture.active`
also creates and commits. Ours allocates a render texture's surfaces when a draw, a blit or a
clear first uses it (GlTextures.Prepare and VkDevice.CreateRenderTexture). Create() and the
active setter only set IsCreated, so ours is never earlier than Unity. Ours frees on Release and
Destroy as Unity does: GraphicsDevice.Release posts the delete to the device thread at once.

Ours' 24-bit depth surface is D24S8 (4 bytes a pixel) where the original used 8 bytes a pixel:
128 MB against 192 MB for each of these targets. Unity's own 1,536 MB says so too.

The game's render textures are made at screen size: LightBuffer, FlowOffsetRenderer, the
MultipleRenderTargetProxy targets, Infrared, CameraRenderTexture, TextureLerper, BloomEffect's
temporaries, LightBufferCompositor's and Timelapser's. `MultipleRenderTargetProxy` makes
`TexturesCopies` that nothing uses. Unity never allocates them, and neither does ours. The
`fast-overlay-textures` tweak covers the targets that overlay toggles leak in both players (PLAN.md).

## 2. Freed textures: when the device lets go

- **Destroy.** Both players free in the frame of the Destroy (texture and render texture
  phases above). OpenGL deletes the name in the same frame. Vulkan retires it to the frame being
  recorded and destroys it when that frame's fence has passed, two frames later (VkFrames.Retire).
- **UnloadUnusedAssets.** Eight unreferenced 4096x4096 RGBA32 textures (512 MB) were freed by the
  unload in both players. Unity's texture counter fell 3,440 → 2,928 MB within the call. Ours
  releases orphans right after the collection (AssetUnloader → GraphicsDevice.ReleaseOrphans).
  A textures-held-alive bug would show as live device textures with no engine owner; the new
  lines compare them at each unload.
  - Start-up, Windows OpenGL: 3,654 device textures, and 3,701 engine textures of which 3,700
    hold a device resource. The ones without a GL name yet are empty or still queued for
    background upload.
  - c100 on Linux: 3,721 device textures against 3,763 owners after the colony's unload.
  - Nothing piles up. Device textures at each later 600-frame line stay within a few of the menu's.
- **The temporary pool.** A released temporary render texture is destroyed 16 frames later in
  both players (`temp gone after 16 frames` in each). Ours: TemporaryRenderTextures, 15 unused
  frames.
- **Texture2D.Apply(false, true).** Unity uploads at Apply (+512 MB dedicated for 8 textures) and
  frees the CPU copy. Ours keeps the texels on the CPU and uploads on first draw, which a texture
  never drawn never gets: +0 dedicated, +512 MB of texel memory until Destroy. This is later and
  smaller in video memory, not larger, so it is no lever here.
- **Bindless handles** (OpenGL) are made non-resident when their texture is deleted
  (DeviceTexture.DeleteName → GlBindless.Release), so a freed texture keeps no resident handle.
  Idle residency (`ONI_BINDLESS_IDLE_FRAMES`) is another question.

## 3. Upload staging

| | Start-up peak | Steady (menu, c100) | Bounded? |
|---|---|---|---|
| Vulkan staging chunks (host-visible) | 3,148 MiB (Windows, Linux) | 32 MiB (4 chunks) | no, before this change |
| Vulkan staging, 64 MiB bound | 298 MiB (Linux) | 32 MiB | yes |
| OpenGL background reads (texels read ahead of upload) | 273-275 MiB | 0 | yes, 256 MiB plus one texture |
| Readable textures' copies (UploadBuffers), rented | 356 MiB (c100 load) | 143-161 MiB (c100) | the pool keeps 128 MiB |
| All texel memory (CPU) | 939 MiB (c100 load, OpenGL) | 593-817 MiB | follows the game |

**Vulkan.** Each texture upload stages its texels in the current frame's host-visible chunks
(VkFrames.Staging), and a frame's chunks are reused only when the frame's fence passes, two
frames on. Vulkan has no background uploads, so every texture a frame records is staged in that
frame. Batch submits within the frame do not reuse the chunks. The start-up frames that load the
game's bundles staged 3,098-3,148 MiB, 372 chunks of 8 MiB and more. The process's shared usage
rose to 3,315 MB and stayed there until the chunks went idle eight frames after the menu. This is
system memory, not VRAM, but it is a 3 GB transient on machines that have little to spare, and the
original's peak is 283 MB.

The change (u60, `vulkan: texture uploads stage at most 64 MiB at a time`): before an upload
that would take the frame's staging past `ONI_VULKAN_STAGING_MB` (default 64; 0 turns the bound
off), the device submits what is recorded and waits for the queue to go idle, as a readback
does. It then takes the frame's staging chunks again from their start. Only a load's burst
reaches the bound (c100's frames stage a few MiB), so steady frames are unchanged. On Linux, c100
from the menu: staging peak 3,148 → 298 MiB, 58 waits in the load, the colony loaded in 43 s
unbounded and 44 s bounded, and the two slowest frames 10.5 → 11.4 s and 16.1 → 17.1 s. The
peak stays above 64 MiB because a texture larger than a chunk gets a chunk of its own, and the
chunks made before the bound took effect stay until they go idle.

On Windows (one run each, same machine and settings):

| Vulkan | Unbounded | 64 MiB bound |
|---|---|---|
| Menu: shared peak / last minute | 3,315 / 437 MB | 245 / 245 MB |
| Menu: dedicated last minute | 3,290 MB | 3,289 MB |
| Menu: start-up frame 2 | 8.6 s | 8.0 s |
| c100: shared peak / last minute | 3,315 / 473 MB | 373 / 279 MB |
| c100: dedicated last minute | 4,410 MB | 4,410 MB |
| c100: the load's slowest frame | 17.4 s | 19.9 s |
| Staging waits (start-up, c100 load) | 0 | 49, 58 + 5 |

The bound does not change dedicated memory, and it lowers the steady shared usage by about
200 MB as well, because fewer chunks are left to go idle. Whether the 2.5 s on c100's load frame
is real or noise needs repeated runs. Linux showed 1 s.

**OpenGL.** Texels go to `glTexSubImage2D` from client memory: the driver copies them before the
call returns, into its own staging that we cannot see. Windows saw a shared peak of 136 MB. Our
own transient memory is bounded:
- background uploads read at most 256 MiB ahead (`ReadAhead`), with a fence every 32 MiB handed
  to the GPU;
- readable textures' snapshots come from UploadBuffers, which keeps at most 128 MiB.

On c100 the snapshots in flight reach 143-356 MiB. The pool then misses and allocates: 8,511
buffers (4.4 GiB) in the load's first 600 frames, and 11,419-63,927 (5.8-7.5 GiB) in every 600
frames after, on OpenGL and Vulkan alike. That is page-faulting memory churn (see UploadBuffers'
comment: 4 ms a frame against 0.3), not video memory. A pool limit of about 384 MiB, or one sized
to the frame's peak rent, would remove it at the cost of holding that much. It is a CPU-time
candidate for a separate measurement.

The per-upload transient allocations besides those: decoding a compressed format the context
cannot sample (none on desktop GPUs), and Vulkan's conversion scratch, which is reused. A
re-uploaded texture under 4 MiB also keeps a copy of its last texels, by design: partial uploads.

## 4. Mip chains

- **Textures** get exactly the asset's `m_MipCount` (Upload2D, UploadArray, Upload3D and the
  Vulkan Upload take `MipCount`). Runtime ones get the mipChain flag's levels or the
  `mipCount` constructor's. For a texture, mipmapCount is the device level count in both
  players (probe: full 11, asked 3 → 3).
- **Render textures** took the full chain whenever they had mipmaps (RenderShape.Of), whatever the
  descriptor's `mipCount`. Unity's player gives a descriptor's `mipCount` exactly: mipmapCount 3
  for 3, and 640 MB of driver memory for eight 4096x4096 targets asking for 2 levels, against
  683 MB for the full chain. Ours would have allocated 683. And `useMipMap = true` set at 256x256
  before a resize to 1024x1024 reports 11 levels and `descriptor.mipCount` -1 in Unity, 9 in
  ours. Fixed on both branches (`engine: render textures get the mip levels asked for`):
  RenderTextureNative keeps the count asked for (-1 for the full chain at the size the texture
  has), and the device gets that many levels. The game asks for no mipmapped render target
  (`set_useMipMap` and `set_mipCount` appear in neither game assembly), so this saves nothing in
  the base game. A 4096x4096 ARGB32 target asking for 2 levels saves 5.3 MB.
- Unity's texture quality settings play no part: `streamingMipmapsActive` is false and the
  mipmap limit is 0 in the original, so neither player drops levels.

## The NVIDIA eviction counter

`GPU_MEMORY_INFO_EVICTION_COUNT_NVX` and `GPU_MEMORY_INFO_EVICTED_MEMORY_NVX` count the evictions
seen by the system (NVX_gpu_memory_info), not the context's. On our RTX 3090 with 14 GB free they
read 14,584 evictions (49.7 GB) at our first frame, unchanged through start-up, the menu and a
c100 load (14,650 at the end). #48's ~150,000 evictions (1.1 TB) are therefore a count since the
reporter's boot, not necessarily a measure of our player. `video memory:` now gives the
evictions since the device started too
(`gl: frame stats give NVIDIA's evictions since the device started`), so the next log from a
4 GB card will say what our run caused.

## The new counters (`--frame-stats`)

`video memory:` (every 600 frames, and at the menu, after each scene load and after
UnloadUnusedAssets) now adds:
- `by kind:` the device memory asked for, with each kind's peak since the last line:
  - OpenGL: textures by format and target (and the format a decoded one is stored as), render
    target colour by format, render target depth, multisampled surfaces, mesh buffers, stream
    buffers;
  - Vulkan: the same at VMA's allocation sizes, plus staging, stream and uniform chunks,
    readback buffers, the presenter's and the splash's images, and the spare buffers meshes keep;
- `upload staging:` the pooled copies (rented and pooled, each with its peak, and the buffers
  newly allocated), OpenGL's background read-ahead, and all CPU texel memory with its peak;
- the engine's live textures, those holding a device resource, and the render textures among them.

They are logging only: counters updated where resources are made and freed, under a lock, and
formatted only when the line is written.

## Not done, and why

- Depth surfaces: ours already uses fewer bytes than the original.
- Lazy uploads of loaded textures: excluded by the brief, and Unity uploads them at load too.
- The 46 KB a texture difference between Vulkan's RGBAFloat allocations and OpenGL's estimate is
  an observation. Whether the drivers' alignment of small textures differs from D3D11's is not
  measurable from outside.
