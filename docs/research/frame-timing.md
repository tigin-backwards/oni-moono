# Frame timing for even camera panning (2026-11-04)

The question: how should the frame delta (`Time.unscaledDeltaTime`) be timed so the camera pans
evenly on screen, on fixed-refresh and variable-refresh (G-Sync, FreeSync) displays? Players say
Moono feels choppier when panning even though its frame rate is higher. `CameraController` pans
by `unscaledDeltaTime`. Ours has been the wall clock between frame starts
(`src/OniMoono.Host/Player.cs`). A rounding to whole refreshes was tried (6f6612e3) and
reverted (f0bde6ef), because nobody had checked it on variable refresh.

**Verdict (revised after variable-refresh runs at 60 and 144 Hz): round vsynced deltas to whole
refreshes, carrying the remainder (3edab6a2), on every display, with no variable-refresh guard.**

**Fixed refresh, borderless on a 59.95 Hz monitor:**
- On a small base at 60 fps, steady panning is even to 0.01%, against 0.56% for master and 0.49%
  for the original.
- On a 200-Duplicant colony at about 53 fps, the share of frames more than 10% off the pan's speed
  falls from 18-22% to 12-17%. The share more than 25% off falls from 13-14% to 9-12%.
- The original on that colony runs at about 30 fps, with 69-71% and 39-43% on the same two
  measures.

**Variable refresh (G-Sync, focused):**
- At 60 Hz, rounding is within noise of the wall clock.
- At 144 Hz, frames more than 10% off fall from 47-53% to 32-37%, and the steady spread from
  11-14% to 7%.

The grid guard built to switch rounding off under variable refresh (e1f17871, with a 10 s
hold-off in 6b00d621) works, but it only gives back that gain. NVAPI's VRR state reads
inactive even while variable refresh runs, so it cannot guard either.

PresentMon needs administrator rights, so the present mode was not measured by it.

**Landed (2026-11-04):** the plain rounding (6f6612e3 on master, 19b18737 on u60), with Godot's
DeltaSmoother exclusions added: a frame over a second, or under a millisecond, keeps its wall
clock. There is no variable-refresh guard. The prototypes below stay on local branches.

## Unity's behaviour

These come from Unity's blog post, posts by Unity staff, and measurement of the original. No
Unity engine source was read.

- **D3D11 and D3D12, vsync on:** the delta is the time between presentations, taken from DXGI
  frame statistics.
  - The blog post ([Fixing Time.deltaTime in Unity 2020.2](https://unity.com/blog/engine-platform/fixing-time-deltatime-in-unity-2020-2-for-smoother-gameplay),
    Tautvydas Žilys) says Unity uses "the exact timestamp of a frame being presented to the
    screen" and names `IDXGISwapChain::GetFrameStatistics`. The main thread waits for frame
    *n − maxQueuedFrames* to appear before it samples the time.
  - Žilys in the forum ([delta-time-consistency #2](https://discussions.unity.com/t/delta-time-consistency/785992/2)):
    "on DX11, Time.deltaTime will tell you the actual amount of time a frame has been on the
    screen".
  - It landed for D3D11 in 2020.2.0a8 and for D3D12 in 2020.2.0a12
    ([thread 639394](https://discussions.unity.com/t/time-deltatime-not-constant-vsync-camerafollow-and-jitter/639394),
    #351, #356, #392).
  - No public source says which field of the statistics Unity uses. Nothing says it uses the
    frame-latency waitable or the moment Present returns.
- **Windowed:**
  - Žilys (#380): "The delta time you get is coming straight from Windows compositor".
  - Žilys (#376): "I don't think it's possible to fix for windowed mode".
  - Microsoft's [GetFrameStatistics](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getframestatistics)
    page: statistics "are not reliable in many multiple monitor scenarios".
  - A Unity 6000.1 report has deltaTime "locked to exactly 1 / monitorRefreshRate" on external
    monitors. Unity staff put it down to GetFrameStatistics reporting broken values (UUM-107892,
    [thread](https://discussions.unity.com/t/time-deltatime-frozen-to-1-refreshrate-when-using-vsync-on-external-monitors/1664527)).
- **Vulkan, OpenGL and desktop Linux:** not covered by the fix.
  - Žilys (#400): "It doesn't look great for OpenGL, Vulkan & VR".
  - Žilys (#468), asked whether the Vulkan fix had landed for PC: "It has not, no".
  - Android (GLES and Vulkan) was fixed in 2021.1.0a2 (#464). No public source names its
    mechanism.
  - On Linux, Unity staff say they "depend on the graphics driver to align us with monitor
    refresh rate if VSync is enabled"
    ([thread](https://discussions.unity.com/t/linux-player-headless-xserver-has-0hz/1569617/8)).
  - Nothing public says Unity uses `VK_GOOGLE_display_timing`, `VK_KHR_present_wait` or
    `VK_EXT_present_timing`.
- **Vsync off:** the blog says that if vsync is disabled, "the time must still be calculated
  manually". Žilys (#338): "DirectX doesn't allow you to query exact Present() time if VSync is
  disabled".
- **Missed vblank:** the delta becomes the frame's real time on screen, a whole number of
  refreshes. The delta-time-consistency post says it does "not fluctuate unless your frame
  rate is missing vblanks".
- **Variable refresh:** no public statement found.

Measured in the original (U59 744825 and U60 761654; both run D3D11 on Windows), with the
probe below:

| Original, cell | fps | deltas on whole refreshes | sum of deltas / wall clock |
|---|---|---|---|
| U59, borderless, small base | 59.3 | 100% | n/a |
| U59, borderless, 200-Duplicant colony | 31.3 | 100% (1-7 refreshes) | 1.003 |
| U60, borderless, 200-Duplicant colony | 30.0 | 100% | 1.002 |
| U59, windowed, 200-Duplicant colony | 42.9 | 99% read **one** refresh; one delta of 1.37 s | 0.91 |
| U59, borderless, vsync off, 200-Duplicant colony | 34.6 | 2% | 1.001 |

- **Borderless fullscreen:** the original's delta reads whole refreshes that add up to the wall
  clock.
- **Windowed:** its frame statistics updated only 46 times in 62 s (ours, in the same mode:
  435-495 times). The delta then sticks at one refresh, and a delta of over a second catches
  up now and then. The earlier finding of "40% speed with jumps of several seconds" was this
  same behaviour.
- **Vsync off:** the delta is the CPU clock.
- **Which presentation the delta measures:** after a long displayed interval, the original's
  delta matched it at no single fixed lag. At lags of 0 to 7 frames it matched 33-62% of the
  time. Unity reads its statistics at its own moments, and they lag by a varying number of
  pictures.

## What the APIs here offer

The machine: Windows 11, an NVIDIA RTX 3090 with driver 610.88, and monitor 2, a Dell S2716DG
in a 59.951 Hz mode. The mode's maximum is 144 Hz; the mode was not changed for these runs.

- **DXGI frame statistics, borderless fullscreen:**
  - The statistics named a newer picture on 83-98% of frames.
  - The statistics always reported SyncRefreshCount equal to PresentRefreshCount, so each
    sample gives that picture's vblank time exactly.
  - At the moment of each Present, the picture on screen was 4 presents behind ours and 2-3
    behind the original.
- **DXGI frame statistics, windowed (composed):**
  - Statistics updated on 22-63% of frames. They stalled for about 200 ms roughly once a
    second, and for more than a second a few times per run.
  - The displayed picture lagged 5-6 presents behind.
  - `IDXGISwapChainMedia::GetFrameStatisticsMedia` read "composed" in every mode, but
    Microsoft documents that value for decode swap chains, so it does not show the present
    mode.
- **The frame-latency waitable:** Microsoft says it "signals when the DXGI adapter has finished
  presenting a new frame". It gives no timestamp, so the statistics are the only display time
  DXGI has.
- **Vulkan on this NVIDIA driver (vulkaninfo):**
  - Supported: `VK_EXT_present_timing` (revision 3, `presentAtRelativeTime`, stage queries for
    QUEUE_OPERATIONS_END and IMAGE_FIRST_PIXEL_OUT), `VK_KHR_present_wait` and
    `VK_KHR_present_wait2`, and `VK_KHR_present_id` and `VK_KHR_present_id2`.
  - Not supported: `VK_GOOGLE_display_timing`.
  - Ours on Windows presents Vulkan pictures through the DXGI swap chain (`VkDxgiTarget`), so
    these matter only on Linux. The Linux driver's support was not checked: WSL has no NVIDIA
    Vulkan display.
- **NVAPI `NvAPI_Disp_GetVRRInfo`** on monitor 2 (struct layout from
  [nvapi.h](https://github.com/NVIDIA/nvapi/blob/main/nvapi.h)), polled every 2 s from
  20:57 to 21:12 while a windowed and a borderless run were up:
  - `bIsVRRPossible=1`, `bIsDisplayInVRRMode=1`;
  - `bIsVRRRequested=0`, `bIsVRREnabled=0` ("VRR Mode is currently active") throughout.
  - So G-Sync was not requested for this display: it is off in the NVIDIA settings, or not set
    for windowed and borderless games. The settings were not changed.
- **PresentMon 2.6.0** (Intel's GitHub release; SHA-256 matches GitHub's digest; signed by
  Intel) stops with "failed to start trace session: access denied ... requires either
  administrative privileges or to be run by a user in the 'Performance Log Users' user group".
  The user's token is medium integrity, with Administrators for deny only. No elevation was
  tried.

## Method

- **Probe:** `tools/PerfProbe` on branch `frame-timing-probe`, U60 copy on
  `frame-timing-probe-u60`. `FrameTimes` pans the camera with the controller's own keyboard
  fields (panRight/panLeft), swapping direction every 180 frames, at orthographic size 12, at
  game speed 3. It records each frame's Update time, the first `onPreCull` time, the delta and
  the camera's x.
- **Present hook:** `DxgiHook` points the IDXGISwapChain vtable's Present and Present1 at a
  logger, in both players. It finds the vtable through a throwaway swap chain on a hidden
  window. Each call logs its QPC time, `GetLastPresentCount`, and `GetFrameStatistics` before
  and after. The same code reads the original's Unity swap chain and ours.
- **Display time:** a picture's display time is its sample's `SyncQPCTime`. Frame *j* goes to
  the (*j*+*s*)-th Present, *s* being the smallest offset with every Present after its frame's
  onPreCull. A frame's displayed interval is its picture's display time minus the previous
  picture's.
- **Metrics:**
  - On-screen speed = camera step / displayed interval. Reported: its sd relative to the median,
    and the share of frames more than 10% and 25% off the median.
  - "Steady sd" is the same sd over frames whose last 11 pictures each stayed one refresh.
  - "dt off display" is the share of frames whose delta differs from their displayed interval by
    more than half a refresh.
  - Frames held at the world's edge (steps under 0.01 cells) are left out.
- **Candidates replayed:** CameraController's keyboard pan is a filter on the delta.
  `keyPanDelta += speed·min(dt/(1/60), 10)`, then a share `smoothDt·easing` of it moves the
  camera, with `smoothDt` an average of the deltas (decomp CameraController.NormalCamUpdate).
  - Fitted to each run's real deltas, it reproduces the measured steps: for example 16.8% sd
    against 17.1% measured.
  - So each candidate's delta, computed from what the Present log knew at that frame's Update,
    can be replayed against the same displayed intervals.
  - The replays use Update times, which jitter more than our true frame starts. The replayed
    "wall" is therefore a little worse than the measured one.
- **"Oracle" bound:** each frame's own displayed interval as its delta. No timing can do this,
  because the interval is not known when the frame starts.
- **Run conditions:**
  - Windows, monitor 2 only, `--no-focus`, High priority (win-boost.ps1), sound prefs at 0.
  - Processes were found by run home only.
  - Borderless fullscreen was 2560×1440; windowed was 1280×720.
  - The original ran through `scripts/original-windows.sh`, which hands the focus back.
- **Display live:** the statistics advanced in every run. The windowed original's few updates
  came between borderless runs whose statistics updated normally, so the display was live.
  That run is kept for its deltas only.
- **Runs:** one per cell, except the 200-Duplicant colony's wall clock against the prototype,
  which was run twice, interleaved.

## Matrix

On-screen speed, measured. "Ours, wall" is master's timing. "Rounding" is 6f6612e3 without the
guard. "Prototype" is the recommended design. All rows use vsync on.

| Cell | fps | frames timed | shown >1 refresh | speed sd | >10% off | >25% off | dt off display | steady sd |
|---|---|---|---|---|---|---|---|---|
| **Small base, borderless, U59** | | | | | | | | |
| original | 59.3 | 97% | 2.4% | 8.2% | 3.8% | 2.9% | 2.0% | 0.49% |
| ours, wall | 59.7 | 96% | 1.9% | 8.4% | 3.1% | 2.7% | 2.6% | 0.56% |
| prototype | 59.6 | 96% | 2.2% | 10.2% | 4.8% | 4.0% | 3.2% | **0.01%** |
| **Small base, borderless, U60** | | | | | | | | |
| original | 59.6 | 98% | 1.5% | 6.5% | 1.9% | 1.7% | 1.6% | 0.40% |
| ours, wall (Vulkan) | 59.6 | 96% | 2.2% | 8.3% | 3.8% | 2.9% | 2.3% | 0.66% |
| **200-Duplicant colony, borderless, U59** | | | | | | | | |
| original | 31.3 | 83% | 60.9% | 33.1% | 70.8% | 43.1% | 42.3% | n/a |
| ours, wall (run 1) | 50.9 | 85% | 14.3% | 17.1% | 22.5% | 13.3% | 7.1% | 9.50% |
| ours, wall (run 2) | 53.7 | 89% | 10.4% | 30.8%¹ | 18.0% | 14.3% | 7.4% | 7.99% |
| rounding | 53.6 | 89% | 11.3% | 17.9% | 17.2% | 12.4% | 7.6% | 4.75% |
| prototype (run 1) | 52.5 | 89% | 11.4% | 19.9% | **13.9%** | **9.3%** | 6.2% | 7.78% |
| prototype (run 2) | 53.9 | 90% | 9.0% | 28.4%¹ | **11.8%** | **8.6%** | 6.4% | 6.46% |
| **200-Duplicant colony, borderless, U60** | | | | | | | | |
| original | 30.0 | 84% | 65.1% | 34.5% | 68.8% | 39.0% | 40.5% | n/a |
| ours, wall (Vulkan) | 52.4 | 83% | 13.7% | 19.9% | 19.2% | 14.0% | 10.9% | 4.40% |
| **200-Duplicant colony, windowed, U59**² | | | | | | | | |
| ours, wall | 51.3 | 63% | 5.8% | 10.0% | 8.0% | 3.7% | 2.7% | 4.82% |
| rounding | 53.4 | 22% | 7.4% | 11.8% | 4.8% | 4.1% | 2.7% | 7.50% |
| prototype | 53.3 | 59% | 5.1% | 12.2% | 5.9% | 4.8% | 3.7% | 5.47% |

¹ One long hitch dominates the sd. The shares are robust to it.

² Windowed statistics cover only part of the frames, and the composed path's pictures did not
always show (fewer pictures shown for more than one refresh than the frame rate implies). Treat
these rows as rough. The windowed original had too few statistics to time its frames.

Vsync off, 200-Duplicant colony, borderless, U59:

- Ours ran at 108.4 fps, and the original at 34.6 fps.
- Both deltas are the CPU clock: 0-2% on whole refreshes, each summing to the wall clock within
  0.1%.
- Displayed intervals mean nothing with tearing, so no on-screen metric.
- Both players presented with sync interval 0 and `DXGI_PRESENT_ALLOW_TEARING`.

Not run, and why:

- **G-Sync on:** variable refresh was not active (NVAPI above), so on and off could not be
  compared.
- **The other vsync-off cells:** these deltas are the CPU clock in both players, so the other
  cells would add nothing.
- **U60 windowed:** U60 uses the same DXGI swap chain as U59 (`VkDxgiTarget`), and windowed
  statistics are rough.

## Candidates

Replayed on our own runs: the same frames and displayed intervals, only the delta changed. Each
cell gives the share more than 10% off / the share more than 25% off / steady sd.

| Candidate | small base, borderless | colony, borderless, U59 | colony, borderless, U60 | colony, windowed |
|---|---|---|---|---|
| wall clock (replayed) | 3.2 / 2.7 / 1.22% | 27.0 / 13.5 / 10.1% | 21.9 / 13.2 / 5.40% | 15.2 / 4.0 / 7.26% |
| round to whole refreshes, with carry | 3.1 / 2.7 / **0.01%** | **12.6 / 9.9** / 8.69% | **16.2 / 12.6 / 0.83%** | **4.5 / 3.2 / 3.29%** |
| snap only within 10% of a whole refresh | 3.2 / 2.7 / 0.86% | 26.3 / 13.5 / 10.1% | 21.1 / 13.4 / 5.09% | 13.6 / 3.2 / 7.10% |
| display interval from frame statistics (Unity-like) | 5.1 / 4.0 / 1.15% | 18.0 / 11.1 / 3.11% | 21.7 / 16.4 / 1.03% | 13.9 / 3.7 / 4.87% |
| oracle (own displayed interval) | 3.2 / 0.7 / 0.28% | 11.1 / 3.0 / 0.76% | 15.4 / 6.3 / 0.79% | 2.4 / 0.8 / 0.08% |

- **Real display timestamps (Unity's way):**
  - Steady panning is even.
  - Around missed vblanks they are worse than the wall clock: the small base goes from 2.7% to
    4.0% more than 25% off, and the U60 colony from 13.2% to 16.4%.
  - The reason is lag. The statistics name a picture 4 presents back, so a long interval reaches
    the delta four frames late, when that frame's own interval is normal again: two wrong frames
    instead of one.
  - The deltas also drift from the wall clock where frames go unsampled: up to 0.4 s over a run,
    and up to 0.8 s when the last interval is held while the statistics stall (windowed, they
    stall).
  - This is the original's windowed failure: deltas stuck at one refresh, then a catch-up.
  - Better than the original, which runs at about 30 fps. Worse than plain rounding.
- **Snapping only within a tolerance:**
  - Barely better than the wall clock.
  - At 50-54 fps on a 60 Hz mode most frame times sit between one and two refreshes, outside
    any tolerance.
  - At 60 fps our frame start jitters by more than 10% often enough that the steady sd stays at
    0.86%.
  - Safe under variable refresh, but it fixes little.
- **Rounding to whole refreshes:**
  - The best on every fixed-refresh cell, close to the oracle on the share more than 10% off.
  - It keeps the sum within half a refresh of the wall clock: +17 to +142 ms over a run, since
    the carry is clamped.
  - Under variable refresh it would add up to half a refresh of error to each frame shown
    between refreshes. That was not measured here; it is derived from the rounding itself.
- **Rounding off when variable refresh is active (recommended):**
  - It is rounding on fixed refresh, so the measured gains above apply.
  - It falls back to the wall clock where variable refresh would put pictures off the grid.
  - The detector is built and its fixed-refresh side is measured: it read "on the grid" in every
    borderless run. Its variable-refresh side is not.
- **Detecting variable refresh through NVAPI (`bIsVRREnabled`):**
  - It would also catch the case, but it works only on NVIDIA.
  - The function's QueryInterface id is not in the public header; it is taken from memory and
    returned sensible data here.
  - The frame-statistics test needs no vendor code.

All of ours beat the original on the 200-Duplicant colony: its deltas are whole refreshes, but
at 30 fps 61-65% of its pictures stay up for more than one refresh, and its delta does not line
up with them (40-42% off display).

## Recommended design (first round; superseded)

The variable-refresh runs further down changed this: land the rounding without the guard. The
guard's design is kept here for the record.

Branch `frame-timing-research`. It is origin/master with 6f6612e3 cherry-picked, then e1f17871
on top.

- **Rounding (`FramePacing.Quantize`):** with vsync on DXGI, the wall-clock delta plus the
  remainder carried from earlier frames is rounded to whole refreshes, at least one. The
  refresh comes from the display mode, looked up every 300 pictures. The carry stays within
  half a refresh, so the deltas add up to the wall clock. All of this is unchanged from
  6f6612e3.
- **New guard (`DxgiSwapChain.CheckGrid`, `FramePacing.GridSample`, `FramePacing.OnGrid`):**
  - After each vsynced Present, read `GetFrameStatistics`. Count a sample only when its
    SyncRefreshCount equals PresentRefreshCount, because its SyncQPCTime is then that picture's
    vblank.
  - For two samples less than a quarter second apart that name different pictures, check whether
    the time between them is within a tenth of a whole number of refreshes.
  - Round only if an on-grid sample came within the last second and at most one of the last 16
    samples was off the grid. Otherwise use the wall clock.
  - Missed vblanks stay on the grid.
  - The log names each change: "pictures on the refresh grid" / "pictures off the refresh grid
    (variable refresh) or no frame statistics".
- **Unchanged:** the wall clock without vsync, without DXGI (Linux, macOS), after a quarter
  second without a picture, and when frames come under half a refresh eight times running.
- **Tests:** `FramePacingTests` covers the rounding and the guard's rule.

## Measured gain

Prototype against master's wall clock, U59, borderless, vsync on:

- **Small base, 60 fps:** steady panning goes from 0.56% to 0.01% sd of on-screen speed. The
  original has 0.49%.
  - The shares off are set by missed vblanks: 2.7% and 4.0% of frames more than 25% off, from
    1.9% and 2.2% of pictures shown for two refreshes, one run each.
  - No delta timing can fix a missed vblank; the oracle still has 0.7%.
- **200-Duplicant colony, about 53 fps:** frames more than 10% off fall from 22.5% and 18.0% to
  13.9% and 11.8%; frames more than 25% off fall from 13.3% and 14.3% to 9.3% and 8.6%. Two runs
  each, interleaved.
- **The original on the same colony:** 70.8% and 43.1%, at 31 fps.

## Second check after G-Sync was enabled (21:30-21:37)

The user turned on G-Sync for monitor 2, and four runs on U59 checked it. NVAPI was polled every
0.5 s during each run.

| Run | fps | displayed intervals off the refresh grid | NVAPI on monitor 2 |
|---|---|---|---|
| ours, borderless, small base | 60.0 | 0.0% | requested=0, active=0 |
| ours, borderless, 200-Duplicant colony | 52.2 | 0.0% (1-3 refreshes) | requested=0, active=0 |
| original, borderless, 200-Duplicant colony | 29.3 | 0.0% (1-5 refreshes) | requested=0, active=0 |
| ours, windowed, 200-Duplicant colony | 51.8 | 0.0% | requested=0, active=0 |

- `bIsVRRPossible` and `bIsDisplayInVRRMode` read 1 throughout, before the runs as well.
- Variable refresh still never engaged, so the variable-refresh measurements were not made.
- `bIsVRRRequested=0` means the driver is not asking for variable refresh on this display, so
  something in the setup still keeps it off. The doc does not narrow down what; the report
  lists the NVIDIA settings to check.
- One change since the first round: windowed frame statistics covered 93% of frames, against
  22-63% before.

## Variable refresh, focused runs (21:53-22:00)

Variable refresh engages only when the game window has the focus. The user approved short
focused runs for this check, all borderless, vsync on, U59, on the 200-Duplicant colony. Each
run measured 1,200 frames (about 23 s), took the focus while running, and gave it back to the
window that had it (focus.ps1: `SetForegroundWindow`; "restore True" after every run).

**Evidence that variable refresh was active:**
- **Vblank times left the grid:** 30-35% of the vblank intervals in the frame statistics, among
  those under 250 ms, were off the 59.95 Hz grid (for example 24.5, 26.5 and 39.3 ms). The
  unfocused run of the same colony had 0% off the grid.
- **The swap chain's reported mode changed:** `CompositionMode` read 1 in every focused present,
  against 0 unfocused.
- **NVAPI did not show it:** `NvAPI_Disp_GetVRRInfo` still read requested=0 and active=0 in
  every focused run. **It cannot serve as the guard.** That also rules out the planned
  NVAPI-guard run: such a guard would never have switched, so it behaves exactly as plain
  rounding, which was measured.

**DXGI's frame statistics under variable refresh:**
- They name a new picture on every Present.
- They gave a fresh vblank time for only 16% of presents. The rest repeated an old one for up
  to about 0.9 s.
- So the displayed interval cannot be timed from them. For these runs it is estimated by the
  interval between the frames' Present calls, since with variable refresh a picture goes up when
  it is ready, up to the mode's 60 Hz.
- That is an estimate, not a display measurement (vrrproxy.py). The same estimate is not valid
  on fixed refresh, where pictures wait for the next vblank.

| Run (focused, variable refresh) | fps | deltas on whole refreshes | speed sd | >10% off | >25% off | dt off Present interval |
|---|---|---|---|---|---|---|
| master's wall clock | 51.7 | 34% | 16.4% | 18.3% | 8.6% | 6.1% |
| plain rounding (3edab6a2's rule, probe build) | 50.6 | 100% | 15.8% | 15.2% | 7.0% | 6.7% |
| prototype, grid guard (e1f17871) | 50.4 | 55% | 14.6% | 18.1% | 6.8% | 5.5% |

- **The grid guard sees variable refresh, but not steadily.** It switched eight times in the
  run: four times to the wall clock, four times back to rounding. 55% of the frames were
  rounded. The stale statistics make "an on-grid sample within the last second" fail and
  succeed in turns.
- **Under variable refresh at this 60 Hz mode, no timing is clearly better.**
  - The three runs are within run-to-run noise: one run each, and the shares of 18.3, 15.2 and
    18.1% sit close together.
  - Replaying the wall-clock run with rounding gives 16.2% / 8.7%, against 23.0% / 8.4% for the
    replayed wall clock.
  - Most frames run at the 60 Hz cap, where variable refresh and fixed refresh behave the same.
  - The predicted harm of rounding (pictures between refreshes) needs frame times that sit
    between refreshes. At this mode that happens only on the occasional long frame.
- **Untested:** a 144 Hz mode (the monitor's maximum), where a 50 fps game would always show
  between refreshes. The display mode was not changed.

## The guard's hold-off (6b00d621), replayed

The guard's switching in the focused runs came from stale statistics. Under variable refresh,
DXGI gave a fresh vblank time for only 16% of Presents, and many of those landed on the grid
(frames at the 60 Hz cap). So "an on-grid sample within the last second" and "at most one of the
last 16 off the grid" passed and failed in turns.

The fix keeps the wall clock for 10 s after the grid is lost, meaning two of the last 16 samples
off it. An on-grid sample now counts for 5 s instead of 1.

guardsim.py replays DxgiSwapChain.CheckGrid and FramePacing.OnGrid on each run's recorded
Present log. For the old guard it reproduces the logged switches: 8 in the focused prototype
run, 7 in the windowed one, 1 in borderless.

| Runs | old guard (1 s, no hold): switches whole log / while recording, frames rounded | new guard (5 s, 10 s hold) |
|---|---|---|
| 3 focused variable-refresh runs at 60 Hz | 8-10 / 6, 32-40% | 2 / 0, 0% |
| 9 borderless fixed-refresh runs (both saves, U59 and U60) | 1 / 0, 100% | 1 / 0, 100% |
| 3 windowed fixed-refresh runs | 7-11 / 2-3, 77-90% | 1-3 / 0, 100% |
| 1 windowed run after G-Sync was enabled | 1 / 0, 100% | 1 / 0, 100% |

The sweep, on the same runs:
- A 2 s or 5 s hold still switched 4-8 times under variable refresh. 10 s held.
- A 1 s freshness switched windowed runs 2-3 times while recording. 5 s did not.
- Unit tests: `FramePacingTests` (5 pass).

## Variable refresh at 144 Hz (22:07-22:30)

Monitor 2 alone was switched to 144 Hz with `ChangeDisplaySettingsEx` on `\\.\DISPLAY2`, a
dynamic change that was not saved. Monitor 1 and the NVIDIA settings were not touched.
Afterwards it was set back, and QueryDisplayConfig read 59951/1000 Hz on both monitors, as
before.

All runs were focused, U59, borderless, vsync on, on the 200-Duplicant colony, 1,200 recorded
frames each (about 12 s at this frame rate). Each run gave the focus back ("restore True",
every run).

**Variable refresh was engaged:** 27-41% of the frame statistics' vblank intervals (under 250 ms)
were off the 6.9445 ms grid, for example 9.7, 16.6, 23.0 and 29.6 ms. At 144 Hz, unlike at 60 Hz,
the statistics gave a fresh vblank time on 91-96% of Presents, so the displayed intervals here
are measured, not estimated.

| Run (144 Hz, variable refresh) | fps | speed sd | >10% off | >25% off | dt off display | steady sd |
|---|---|---|---|---|---|---|
| master's wall clock (run 1) | 102.2 | 27.5% | 47.0% | 18.3% | 15.0% | 10.88% |
| master's wall clock (run 2) | 92.9 | 28.0% | 53.1% | 20.2% | 18.0% | 13.61% |
| plain rounding (run 1) | 103.0 | 27.6% | **32.2%** | 17.8% | 18.5% | **7.39%** |
| plain rounding (run 2) | 102.0 | 30.7% | **36.7%** | 19.8% | 18.9% | **6.88%** |
| prototype with hold-off (run 1) | 99.7 | 30.4% | 48.8% | 22.2% | 19.1% | 11.45% |
| prototype with hold-off (run 2) | 92.4 | 32.8% | 63.1% | 28.0% | 22.1% | 12.08% |

- **The guard did its job:** the prototype timed frames by the wall clock throughout. Run 1
  switched once, early; run 2 never left the wall clock.
- **That job turns out to be the wrong one.** Under variable refresh, rounding beats the wall
  clock on frames more than 10% off: 32-37% against 47-53%. It also beats it on steady spread:
  6.9-7.4% against 10.9-13.6%. On frames more than 25% off they are about level.
- **Replays agree.** Replaying each wall-clock and prototype run's own deltas with rounding gives
  37-56% more than 10% off, against 53-67% with the wall clock, in all four runs. On frames more
  than 25% off, rounding ranges from 0.3 points better to 2.8 points worse.
- **Why rounding helps:** the error of rounding to 6.94 ms quanta (at most half a quantum, the
  sum carried) is smaller than the jitter of our frame starts, and the camera's easing averages
  over frames.
- **The prototype's runs are the worst of the three.** They match the wall clock within
  run-to-run noise: the two runs of the same build differ by 4-14 points.
- **Missed vblanks still dominate.** Even the oracle, each frame's own displayed interval, has
  32-48% of frames more than 10% off here, because the game's frame times vary.

**Revised verdict: land plain rounding (3edab6a2), without a variable-refresh guard.** Rounding
is at least as even as the wall clock in every cell measured:
- fixed refresh at 60 Hz: clearly better;
- variable refresh at 60 Hz: within noise;
- variable refresh at 144 Hz: better on the share more than 10% off and on the steady spread,
  about level on the share more than 25% off.

The guard costs that gain under variable refresh. NVAPI cannot detect variable refresh here at
all: it reads inactive throughout.

## Not verified, and what would verify it

- **Variable refresh on other vendors' drivers (AMD FreeSync, Intel):** not measured. The gain at
  144 Hz comes from rounding's error being smaller than our frame-start jitter, which should not
  depend on the vendor.
- **PresentMon:** with Performance Log Users membership or an elevated run, it would give the
  present mode and displayed times independently of DXGI's statistics.
- **Windowed:** the statistics stall for over a second a few times per run, so the guard flips
  to the wall clock and back (7 switches in one windowed run). A five-second window for the on-grid
  sample would keep it steady; the off-grid count would still catch variable refresh. This was
  not measured.
- **Linux, Vulkan:** the wall clock stays. `VK_EXT_present_timing` (refresh duration, and
  first-pixel-out times) would give both the period and the grid test, where the driver has it.
- **Latency, a side finding:** at Present time, the picture on screen was 4 presents behind ours
  and 2-3 behind the original, borderless. Ours queues one or two pictures more. This is a
  separate question from delta timing.

Raw data and scripts: `out/ft2/out/results/` (local), scripts on branch `frame-timing-probe`
(`tools/PerfProbe/frametiming/`: ft-run.sh, ftan.py, table.py, lagcheck.py, events.py). The
original's late-colony behaviour comes from the earlier runs in `out/frametime/out/ft-results/`.
