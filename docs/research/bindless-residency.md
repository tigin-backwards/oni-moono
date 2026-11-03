# Bindless texture residency: limits, counts and safety (issue #50)

2026-11-03, on master (U59). Issue #50 is a garbled screen and then a frozen PC on an RTX 5070
Ti Laptop GPU (NVIDIA 610.47) with bindless textures, True Tiles and VisualFixesNTweaks. The GPU
hang audit (branch `worktree-agent-aaffb34b163efa719`, `docs/research/gpu-hang-risks.md`) ranked
two bindless hazards first: residency was never limited or checked, and handles were made
non-resident, and textures deleted, mid-frame with no fence. This note checks the premise of the
first, measures what colonies keep resident, and records what changed.

## The claimed limit

The audit cited a Khronos forum thread (https://community.khronos.org/t/bindless-textures-resident-textures-limits/109122,
October 2022, an NVIDIA GPU with 16 GB on Windows, driver not given): residency failed at 1,639
resident textures of 4 MiB and at about 5,600 of 1 MiB, by about 1.6 GB, silently or with
`GL_OUT_OF_MEMORY ... Exceeded buffer residency limits` in KHR_debug output only; others saw it
just under 4,000. The posters suspected WDDM's limit of 4,096 allocations.

`ONI_BINDLESS_STRESS=COUNT[:SIZE[:MIB[:CHECKMIB]]]` (GlBindlessStress.cs) tests it at start-up:
it makes COUNT white RGBA8 textures of SIZE x SIZE, makes each handle resident and reads
`glGetError`, `glIsTextureHandleResidentARB` and the debug messages after each, and every 1,024
handles one draw samples every handle reported resident and counts those that do not read white.

RTX 3090, NVIDIA 610.88, Windows 11, with `--gl-debug`:

| Textures | Size | Total | Failures | Sampled in one draw, wrong |
|---|---|---|---|---|
| 20,000 | 64x64 | 312 MiB | 0 | 20,000, 0 |
| 200,000 | 16x16 | 195 MiB | 0 | 200,000, 0 |
| 12,288 | 512x512 | 12 GiB | 0 | 12,288, 0 |
| 5,120 | 1024x1024 | 20 GiB | 0 | 4,096 (16 GiB), 0 |
| 28,000 | 512x512 | 28 GiB (past the card's 24) | 0 | 16,384 (16 GiB), 0 |

No error, no `false` from `glIsTextureHandleResidentARB` and no debug message other than
buffer-placement notes, in any run. Past video memory the driver evicted (NVX's eviction count
rose by 1,750) rather than refuse. **The forum's limit does not hold on this driver.** It may
still hold on others (the reporter's 610.47 is the same branch, on a laptop GPU), so residency is
now checked and a failure handled (below).

## What colonies keep resident

Frame stats (`--frame-stats`) print `N resident (...; most M; K sampled in the last 60 frames)`.
Windows, the same machine, speed 3, 7,200 frames from load (out of the box, everything ever
sampled stays resident):

| Colony | Resident in play | After the autosave | Sampled in 60 frames |
|---|---|---|---|
| c100 | 2,431 to 2,621 | 7,274 | |
| c100 with True Tiles, Material Pack, Skin Selector | 2,437 to 2,622 | 7,285 to 7,370 (the soak) | 2,132 |
| DeCrew | 20,111 to 20,255 from the first count | | 4,143 to 4,203 |
| soggy lab 2 (35 mods) | 1,410 to 1,488 | 11,673 to 11,748 | |
| U57 | 3,719 to 3,849 | 13,373 to 13,394 | |

An autosave's colony preview samples the whole world, and its textures stay resident after:
every colony that saved passed the forum's 4,000 then. Nothing ever failed here.

## What changed

- **Checked residency.** After `glMakeTextureHandleResidentARB`, `glGetError` (earlier errors
  taken first) and `glIsTextureHandleResidentARB`. A handle that fails is never sampled: the
  draw binds the texture and its sampler object to the sampler's unit and sets the uniform to the
  unit (ARB_bindless_texture lets a `bindless_sampler` uniform take either, Issue 12), so it
  samples the same texels with the same state. It is tried again 64 frames later. Logged once.
  The resident count at a failure becomes a cap, so the least recently sampled textures make
  room.
- **Fenced non-residency.** A texture deleted or given a new name (re-uploads whose size, format
  or levels change, render texture reshapes, releases) while its handles are resident keeps its
  old name and handles until a fence placed after the frame has signalled; then they are made
  non-resident and the name deleted. The idle and cap evictions also wait for a fence, and a
  texture sampled again before it simply stays resident.
- **A cap: 8,192 resident textures** (`ONI_BINDLESS_MAX_RESIDENT`, 0 for none), twice the largest
  60-frame working set measured (DeCrew's). Past it the least recently sampled (none of the last
  two frames) go non-resident, down to 7/8 of it, and resident again when next sampled.
  `ONI_BINDLESS_IDLE_FRAMES=N` stays opt-in: since the limit did not reproduce, idle eviction is
  not on by default; the cap is the safety net, and trims what an autosave preview leaves.
- **Validation** (`ONI_VERIFY_BINDLESS=1`): before each draw, every handle it samples belongs to
  its texture, the texture is not deleted, and the handle is resident by our count and GL's; a
  texture with handles never has its image or parameters specified; a mip generation on one
  gives no GL error. `bindless handles: N draws verified, M wrong` every 100,000 draws, read by
  `qa-tools.py verify` (qa.sh's verify stage sets it).

## Evidence

- **Unit tests** (BindlessResidencyTests, a fake GL that fails residency and holds fences back):
  a failed handle is bound instead and tried again after 64 frames; a failed wake leaves nothing
  resident; released names wait for their frame's fence; an idle texture sampled before its
  fence stays resident, and a later eviction is not ended by the old fence; the cap evicts the
  least recently sampled; a failure caps the count; nothing is made non-resident twice or
  deleted while resident.
- **Validation on Windows** (`ONI_VERIFY_BINDLESS=1`, NVIDIA 610.88): DeCrew 6,000 frames with
  everything resident (14.5 M draws, 0 wrong); c100 paused with idle eviction after 4 frames and
  a cap of 64 (3.6 M draws, 0 wrong; the paused colony samples its 2,160 textures every frame,
  so little went); DeCrew with the default cap, 5,400 frames (13.0 M draws, 0 wrong): its first
  sweep made 12,949 of 20,117 textures non-resident, then 5 to 82 came back per 600 frames,
  and the count settled at 7,261 to 7,527 with 4,100 to 4,160 sampled per 60 frames. Frame rates
  were those of the uncapped run (33-54 against 31-40 fps; one run each, not an interleaved
  comparison).
- **Soak**, c100 with the three True Tiles mods, Windows, speed 3, validation and frame stats on,
  the default cap, 30 minutes (the cap the plateau rule allowed; VRAM free, shared with other
  test players, never settled within its 256 MiB band): 117.4 M draws verified, 0 wrong, no
  exceptions, two autosaves. Resident textures (seconds after the watch began, about 3 minutes
  into the run):

  | s | 0 | 35 | 128 | 351 | 588 | 865 | 1144 | 1477 | 1662 |
  |---|---|---|---|---|---|---|---|---|---|
  | resident | 2,444 | 2,624 | 7,285 | 7,313 | 7,333 | 7,338 | 7,352 | 7,370 | 7,370 |
  | texture MiB | 3,742 | 3,764 | 4,100 | 4,101 | 4,100 | 4,100 | 4,112 | 4,110 | 4,110 |
  | process VRAM MiB | 3,990 | 1,351 | 2,033 | 2,006 | 2,024 | 2,028 | 2,063 | 2,068 | 2,048 |

  The first autosave (about 2 minutes in) takes the resident count from 2,600 to 7,300; after
  it, 85 more in 26 minutes. A Linux soak (c100 with True Tiles, 7,200 game seconds, 51 minutes;
  Mesa's d3d12 driver has no bindless textures, so it covers the bound path and the deletes)
  passed with no exceptions; its textures held at 10,600 to 10,700 (4.1 GiB).
- **Pixels.** Paused-colony captures are not repeatable here (two base runs of c100 differ in
  16 % of pixels, animations run while paused), so captures cannot prove parity; the stressed
  and default runs differed from base by no more than base from itself (16.0 % and 6-7 %). The
  argument is the mechanism: residency changes no texel or sampling state, and a texture bound
  instead of a handle is sampled with the same sampler object.
