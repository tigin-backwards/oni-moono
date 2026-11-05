# Input latency with vsync (2026-11-05)

The question: does Moono take longer than the original to get a frame on screen with vsync on,
and if so, where do the extra frames wait? The frame-timing research
(docs/research/frame-timing.md) saw, at each Present, the picture on screen 4 presents behind
ours and 2-3 behind the original. Players say Moono feels choppier when panning.

**Verdict: yes, by one to two refreshes, from the main → device → presenter pipeline. Fixed by
waiting on the main thread before a frame, as Unity does, while the frame's stages fit a
refresh; when that holds is decided by Android Frame Pacing's (Swappy's) pipeline-mode rule.**

## How it was measured

Windows 11, RTX 3090 (driver 610.88), monitor 2 in a 59.951 Hz mode, borderless 2560x1440, vsync
on, unfocused, High priority, sound off. The PerfProbe mod (local branch `latency-probe`,
`tools/PerfProbe/latency/`) runs in both players:

- Every 30 frames its `Update` turns a 40x40 square at the window's top-left white or black, on
  a screen-space overlay canvas over everything. It notes the QPC then. It is test tooling only:
  the white square in a user's screenshot of such a run was this marker, and no Moono build
  draws it (a run of the change without the probe's marker, captured through Desktop
  Duplication, shows the game's own top-left corner).
- Its DXGI hook (on the `IDXGISwapChain` vtable, so the original's D3D11 swap chain and ours go
  through it alike) copies that pixel of every presented back buffer into a ring of 1x1 staging
  textures and reads it 8 presents later without waiting, so it never stalls the GPU queue. It
  also logs `GetFrameStatistics` around every Present.
- Latency: from the `Update` that changed the square to the vertical blank at which the first
  picture showing it was displayed (`SyncQPCTime` - (`SyncRefreshCount` - `PresentRefreshCount`)
  × the refresh period; borderless, the statistics name each picture's vblank exactly). 59
  changes a run while the camera pans.
- OpenGL without DXGI has no swap chain to hook; there `ddlat` (Desktop Duplication of the
  monitor) times when the desktop image first shows the change (`LastPresentTime`). In the same
  run of the original, Desktop Duplication read 35 ms where the frame statistics read 48: its
  times are earlier than the vblank, so they compare only with each other.

Frames are timed `Update` to `Update`: fps, the standard deviation of frame times, the mean of the
slowest 1%. Runs with another game process on the machine were left out.

## Where the extra frames were

| Small base, 60 Hz, borderless | fps | `Update`→Present | Present→display | `Update`→display (median) |
|---|---|---|---|---|
| Original U59 (D3D11) | 59.4 | 3.4-4.5 ms | 2.5-2.7 refreshes | 48-50 ms |
| Original U60 (D3D11) | 59.7 | 4.2 ms | 2.7 refreshes | 49 ms |
| Ours U59, OpenGL+DXGI, before the hidden-window fix | 59.7 | 33.3 ms | 3.0-3.1 | 84-85 ms |
| Ours U59, OpenGL+DXGI (master of today) | 59.0 | 33.4 ms | 2.0 | 66 ms |
| Ours U60, Vulkan+DXGI | 59.5 | 32.9 ms | 3.0 | 84 ms |

- The original presents 3-5 ms after its `Update`: Unity's render thread executes the frame's
  commands as the main thread issues them and presents at once; its main thread then waits
  before the next frame. Ours presents two refreshes after `Update`: the main thread hands a
  whole frame to the device thread once the device thread has executed the previous one, and
  the device thread hands it to the presenter once the presenter has presented and waited out
  the one before. With vsync each of the three holds a frame.
- After the presenter, DXGI's own queue was as deep as the original's or one picture deeper
  (its maximum frame latency 2 lets the presenter keep two pictures in flight). Which of the two
  depths a run settles into varied between runs and builds; master's hidden-window change
  (279c793f) left OpenGL+DXGI at 2 refreshes, Vulkan+DXGI stayed at 3.
- Unity's `QualitySettings.maxQueuedFrames`, measured in the original (default read back: 2):
  1 gave 27 ms at 52 fps, 2 gave 48 ms at 59.4 fps, 3 gave 67 ms at 59.3 fps. Each queued frame
  is a refresh of latency, and 1 costs frames.
- `ONI_DXGI_LATENCY=1` (one picture in flight) gave 67 ms on the small base at 58.8 fps: it
  shortens DXGI's queue but not the pipeline in front of it.

## What others do

- **Microsoft, DXGI 1.3 waitable swap chains** ("Reduce latency with DXGI 1.3 swap chains"):
  wait on the frame-latency waitable before rendering each frame; the default maximum latency
  of 1 "results in the least possible latency but also reduces CPU-GPU parallelism"; use 2 when
  the CPU and GPU each take under a refresh but their sum is over it.
- **Android Frame Pacing (Swappy, Apache-2.0)**, `SwappyCommon::updateSwapInterval`: frame
  durations (CPU and GPU time, and whether the frame missed its deadline) over a sliding 2 s
  window, judged only once the window spans 2 s and cleared after every change, so a mode lasts
  at least 2 s. With more than 10% of the frames missed (`FRAME_DROP_THRESHOLD`), pipelining
  is turned on (or the swap interval raised); with none missed it may lower the swap interval;
  otherwise, pipelined, with (CPU + GPU + 1 ms) × 1.5 (`NON_PIPELINE_PERCENT` 50, `FRAME_MARGIN`)
  under the refresh period × swap interval, pipelining is turned off. It starts pipelined.
  "Pipelining off" there means the CPU waits for the frame's GPU work after its swap before
  starting the next; the same trade as ours.
- **LatencyFleX (Apache-2.0)**: a BBR-like pacer that delays each frame's start to a target
  from EWMA estimates of the frame time and the frame's latency (end measured at GPU
  completion), probing up and down in alternate frames (`up_factor` 1.10, `down_factor` 0.985).
  Its README: it "does not provide any benefits when VSync is enabled", pending presentation
  timing. Our case is vsync, so it was not adopted.
- **NVIDIA Reflex, AMD Anti-Lag 2, Intel XeLL**: driver SDKs the game calls at a marked point
  (Reflex's markers and sleep, Anti-Lag 2's `Update` just before input is polled, XeLL's sleep
  and marker data) so the driver delays the CPU's frame start and the GPU's render queue stays
  empty ("just in time"). Vendor-specific, for D3D and Vulkan drivers; nothing to call from our
  OpenGL+DXGI path.
- **Godot 4**: fixed settings, `rendering/rendering_device/vsync/frame_queue_size` 2 and
  `swapchain_image_count` 3 (`core/config/project_settings.cpp`), no adaptation.
- **Unity**: the main thread waits for frame n − `maxQueuedFrames` to appear before it samples
  the time (Žilys, "Fixing Time.deltaTime in Unity 2020.2"); fixed, as measured above.

## The change

With vsync the main thread waits, after handing frame n over, until frame n + 1 −
`maxQueuedFrames` has been through the presenter (`QueuedFrames`), which takes the main thread
out of the pipeline in front of the presenter as Unity's does. Always on, it cost frames where
a frame's stages together exceed a refresh: a 200-Duplicant colony at about 50 fps fell to
38-47 fps in five runs (base 49-51). So it follows Swappy's pipeline-mode rule: on while
(the frame's stages summed + 1 ms) × 1.5 fits the refresh over 2 s of frames, off once more
than 10% of the frames miss their refresh, judged over a sliding 2 s window cleared at each
change. The stages: the main thread's time from the frame's start to its present, the device
thread's executing it and the presenter's until it issues the picture (which includes waiting
for the GPU). A heuristic of our own came first (no frame of the last 120 over three quarters
of a refresh, with a back-off after misses); Swappy's rule replaced it.

When the wait turns on, DXGI's next present first waits for one more picture to retire, once,
so the picture DXGI queued while it was off is shed: without it, a run that turned the wait on
after its first two seconds stayed a refresh deeper (68 ms against 50 with the wait on from the
start).

`QualitySettings.maxQueuedFrames` exists now (2 by default, as the original reads it): the
depth of the wait with vsync, and without vsync the presenter's queue (at most 2, three back
buffers). Set in the original, 0 reads back 0 and 20 reads back 10; ours does the same.

## Results

`Update`→display is the median of 59 marker changes; "switches" counts the wait's changes over
the whole run (loading included, the first turn-on counted). "Heuristic" is the rule of our own
this replaced; "Swappy" the change. The Swappy runs had `--frame-stats` on, the others not.

| Case (60 Hz, borderless) | Build | fps | frame-time sd | slowest 1% | `Update`→display | switches |
|---|---|---|---|---|---|---|
| Small base, U59 OpenGL+DXGI | master | 59.0 | 2.1 ms | 34.0 ms | 66.4 ms | |
| | heuristic | 58.7 | 3.7 ms | 38.4 ms | 56.0 ms | 10+ |
| | Swappy | 59.0 | 2.9 ms | 38.5 ms | 47.2 ms | 3 |
| | original U59 | 59.4 | 1.8-3.3 ms | 34-36 ms | 48.1-50.2 ms | |
| Small base, U60 Vulkan+DXGI | u60 | 59.5 | 2.2 ms | 33.3 ms | 84.1 ms | |
| | Swappy | 59.2 | 2.1 ms | 33.7 ms | 50.8 ms | 1 |
| | original U60 | 59.7-59.8 | 1.3-1.4 ms | 25-27 ms | 46.3-49.2 ms | |
| Small base + 11 ms of main-thread work a frame, U59 | master | 59.8 | 1.3 ms | 25.7 ms | 69.6 ms | |
| | Swappy | 59.5 | 3.0 ms | 36.8 ms | 71.9 ms | 0 |
| | original U59 | 57.6 | 3.1 ms | 34.4 ms | 36.3 ms | |
| Same, U60 Vulkan | u60 | 59.6 | 1.5 ms | 30.2 ms | 69.4 ms | |
| | Swappy | 59.4 | 3.1 ms | 35.9 ms | 72.1 ms | 0 |
| Small base + 13 ms a frame (55-57 fps), U59 | master | 55.8 | 1.6 ms | 25.0 ms | 27.2 ms | |
| | Swappy | 57.4 | 1.8 ms | 30.3 ms | 26.6 ms | 0 |
| 200-Duplicant colony, U59 OpenGL+DXGI | master | 44.5-50.7 | 9.7-18.4 ms | 61-145 ms | 70.4-76.0 ms | |
| | Swappy | 52.7 | 6.0 ms | 45.5 ms | 73.5 ms | 2 |
| | original U59 | 26.2-33.5 | 26-43 ms | 161-212 ms | 45.1-47.4 ms | |
| 200-Duplicant colony, U60 Vulkan+DXGI | u60 | 49.9 | 7.3 ms | 48.5 ms | 75.9 ms | |
| | Swappy | 49.9 | 7.9 ms | 52.3 ms | 61.0 ms | 0 |
| | original U60 | 26.0-28.3 | 16 ms | 88-89 ms | 46.0-48.4 ms | |

OpenGL without DXGI, small base, Desktop Duplication's time (earlier than the vblank; the
original read 35.0 ms on it): master 41.9 ms, Swappy 24.6 ms, both at 59.8-59.9 fps.

- Where frames leave time to spare (a light colony), the wait brings a frame's start to the
  screen to the original's: 47 ms through DXGI, 51 through Vulkan, against 66 and 84.
- Where they do not (11 ms of extra work a frame, or a busy colony), Swappy's rule keeps the
  wait off, and fps and latency stay master's. There the original is quicker to the screen than
  ours (36 ms against 70 with the extra work; 45-48 against 70-76 on the colony), at its own,
  lower frame rate: its main thread waits while ours goes on to the next frame. Closing that
  would need the device thread to execute a frame while the main thread records it, as Unity's
  render thread does, a larger change.
- Below the refresh (13 ms of extra work, 55-57 fps) nothing queues and both are at 27 ms.
- The heuristic of our own switched on and off ten times or more in a run of the small base
  (and its turning on at times left DXGI's queue a picture deeper); Swappy's rule switched
  three times in the same run, once at start-up.


## Not measured

Variable refresh (G-Sync), other GPUs than the RTX 3090 and the AMD iGPU here, Linux displays
(the rule applies there too, with the mean time between frames standing in for the refresh
period), windowed latency (the frame statistics are not reliable when composed).
