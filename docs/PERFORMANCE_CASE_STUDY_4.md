# Performance case study 4: a heavily modded colony

The other case studies follow vanilla colonies. This one follows a player's colony with 139 mods,
where the colony itself is cheap to simulate and most of the extra cost comes from two mods. It
is a snapshot: Moono at master 740fb545 on the U59 game (744825), plus one engine change measured
afterwards.

## The colony

A Spaced Out colony at cycle 709:

| | With mods | Vanilla load |
|---|---|---|
| Worlds | 9 | 9 |
| Duplicants | 12 | 12 |
| Critters | 723 | 723 |
| Buildings | 20,067 | 16,918 (modded buildings dropped) |
| Crops | 1,148 | 1,147 |
| Loose debris items | 6,703 | 6,698 |
| Pipes / wires | 4,167 / 2,564 | 3,787 / 2,564 |
| Animated objects | 30,973 | 27,129 |
| Working set after load | 15.6 GiB | 8.8 GiB |

The vanilla load is the same save without the mods except Rest for the Weary, which its
schedules need to load at all. It drops the modded buildings.

## The numbers

Measured on Linux under WSL2 (Xvfb, 1280×720), speed 3, 150 s after a 60 s warm-up, every frame
timed. These show direction, not Windows frame rates (see [the note on measuring under
WSL](PERFORMANCE_CASE_STUDY_2.md#a-note-on-measuring-under-wsl)): the vanilla runs are limited by
WSL's GL layer, with the main thread only 75% busy, while the modded runs are limited by the main
thread on any platform.

| Run | fps | Median frame | p99 frame |
|---|---|---|---|
| With mods, default tweaks | 48.8 | 20.0 ms | 31.9 ms |
| With mods, Fast Track's "Use Mesh Renderers" set to None | 68.9 | 14.0 ms | 24.0 ms |
| Vanilla, default tweaks | 68.3 | 14.3 ms | 21.8 ms |
| With mods, no tweaks | 42.2 | 23.4 ms | 35.0 ms |
| Vanilla, no tweaks | 62.2 | 15.8 ms | 24.6 ms |

## Where a frame goes

Sampled main-thread time in ms a frame, by who owns the code. A Harmony-patched game method
counts as the game; the patch methods count under the mod that owns them.

| Owner | With mods | Mesh renderers off | Vanilla |
|---|---|---|---|
| Engine (Moono) | 11.1 | 7.6 | 3.5 |
| Game | 5.2 | 4.4 | 5.0 |
| Mods' own code | 1.7 | 1.3 | 0.0 |
| UI packages (uGUI, TextMeshPro) | 1.1 | 0.8 | 0.8 |
| Moono tweaks | 0.3 | 0.3 | 0.4 |
| Frame | 22.1 ms | 16.9 ms | 15.0 ms |

By system:

| System | With mods | Vanilla | Why it differs |
|---|---|---|---|
| Rendering | 6.33 | 1.74 | |
| · culling | 5.04 | 0.39 | |
| · · mesh renderer bounds | 2.13 | 0.03 | Fast Track's 20,600 mesh renderers |
| · · world-space canvas draws | 1.89 | 0.08 | Pipe Flow Overlay's 5,744 icons |
| Canvas build | 2.58 | 0.42 | the same icons, walked every frame |
| `Game.Update` | 4.54 | 2.46 | more sim steps per frame at the lower frame rate; per real second about 25% more, from mods' patches and 3,150 more buildings |
| · state machine sim sub-ticks | 2.81 | 1.66 | |
| UI input (raycasts) | 1.01 | 0.83 | |
| `World.LateUpdate` + property textures | 0.83 | 0.99 | Fast Track moves property textures; about even |
| `Game.LateUpdate` | 0.82 | 0.99 | |
| GL draw recording | 0.77 | 0.99 | |
| Brains | 0.40 | 0.64 | Fast Track moves brains to its own threads |
| Animation updates | 0.48 | 1.24 | Fast Track's animation changes |

The game's own work is about 5 ms a frame with or without mods. The late-game colony in
[PERFORMANCE.md](PERFORMANCE.md) spends about 14 ms there.

## What the mods cost

| Mod | Own code | With what it calls |
|---|---|---|
| Fast Track | 1.11 | 3.04 |
| Pipe Flow Overlay | 0.11 | 0.11, plus about 3.5–4 ms of engine canvas work |
| Rocketry Expanded | 0.10 | 0.11 |
| Ronivan's Legacy – Industrial Revolution | 0.10 | 0.12 |
| Every other mod | under 0.05 each | |

Fast Track also runs about 6.6 ms a frame on its own worker threads, off the main thread.

**Fast Track's "Use Mesh Renderers"** (default "All") replaces the game's per-frame mesh drawing
with about 20,600 persistent mesh renderers for ground tiles and animation batches. In Unity
that trades many per-frame draw calls for renderers Unity culls natively. In Moono each draw
call is cheap and each renderer was culled in managed code, once per camera per frame, so the
option cost more than it saved: setting it to None took the colony from 48.6 to 68.9 fps.

**Pipe Flow Overlay** keeps one icon per pipe segment active on a world-space canvas and hides
them by making them transparent. The engine still walks and batches all of them every frame,
about 3.5–4 ms (estimated from the profile, not measured without the mod).

**Moono tweaks that stand down for Fast Track** (fast-anim-batches, instant-reachability and
path-probe-repair) cost about 0.2 ms of main thread here, because Fast Track brings its own
versions of the same work.

## What changed for it

**Culling keeps renderer bounds until what they read changes.** Each renderer's world bounds were
found every frame through its components, filter and mesh, several dependent memory reads per
renderer per camera. They are now kept in flat arrays and reset only when the transform, mesh,
mesh bounds or bounds overrides change. On this colony with Fast Track's mesh renderers on:

| | Before | After |
|---|---|---|
| Renderer part of culling | 3.05 ms | 0.30 ms |
| Frame rate (Linux) | 49.2 fps | 60.4 fps |
| Vanilla frame rate | 66.8 fps | 67.2 fps |

`ONI_VERIFY_CULL_CACHE` checked 80 million kept bounds against fresh ones, none different.

**Canvas renderers honour `cullTransparentMesh`.** Unity skips a canvas element whose vertex
alphas are all below 0.001 when its renderer's flag is set, and the flag is on for renderers made
at run time. Moono ignored the flag and defaulted it to off, so Pipe Flow Overlay's hidden icons
were batched and drawn every frame. They are now skipped as in Unity, and still numbered and
hit by raycasts:

| | Before | After |
|---|---|---|
| World-space canvas draws | 1.52 ms | 0.03 ms |
| Canvas build | 2.02 ms | 1.71 ms |
| Frame rate (Linux) | about 66 fps | about 84 fps |
| A late-game vanilla colony, frame rate | 46.8 fps | 47.0 fps |

`ONI_VERIFY_CANVAS_CULL` checked 39 million elements on this colony, none wrong.

## What's left

- **Pipe Flow Overlay's icons in the canvas walk**, about 1.7 ms a frame: hidden icons are no
  longer drawn, but the walk still numbers them, as Unity does.
- **Mods' patch overhead in `Game.Update`**: 12 patches on `Game.Update` alone, and 6.9 ms of the
  frame has a patched method somewhere on the stack.
- **Collections:** with mods the heap holds 15.6 GiB against 8.8, and a gen1 collection of
  100–145 ms every 15–30 s accounts for most frames over 100 ms.
