# Frame generation tools under Moono (2026-11-13)

A player reported that "AI frame generation", which they used with the original game, stops
working when the game runs through Moono. They did not name the tool. This note covers the three
likely ones (Lossless Scaling, NVIDIA Smooth Motion, AMD Fluid Motion Frames), how each attaches
to a game, how Moono looks to each, the likely causes, what a player can do now and what Moono
could change. Nothing was run for it: the Moono side is read from the code, the tool side from
the vendors' pages and, where those are silent, from secondary sources (marked). No tool was
tested against Moono, so every "breaks" or "works" below about a tool is an inference until
someone tries it.

## Summary

- **Most likely cause, all three tools:** the tool's setting is tied to `OxygenNotIncluded.exe`
  and Moono's process is `OniMoono.exe`. Smooth Motion is switched on per program in the NVIDIA
  app, AFMF often per game in Adrenalin, and a Lossless Scaling profile names an exe.
- **Second:** the tool's display-mode rule is met by the original and not by Moono in the same
  game setting. Moono's "exclusive fullscreen" is a topmost monitor-sized window with the display
  mode changed, never DXGI's fullscreen state.
- **Third:** the API the tool sees. Moono renders with OpenGL (U59) or Vulkan (U60) and presents
  through a small D3D11 device that only copies and presents. Whether a driver feature treats that
  as a D3D11 game is unknown.
- **Fourth, Lossless Scaling only:** frame generation may be doing what it was set to do. At 90 to
  100 fps an adaptive target is already met, and a fixed 2× exceeds most refresh rates.
- **We cannot test any of the three here** without installing software or changing driver
  settings: Smooth Motion is officially for RTX 40 and 50 only, and this machine has an RTX 3090.
- **Proposed, nothing landed:** a player-facing page in `docs/` once a tool has been tried; a
  different title for the hidden OpenGL windows; and a question back to the player (which tool,
  which GPU, which window mode).

## The tools

### Lossless Scaling (LSFG)

- **How it attaches:** it captures the game's window and draws the scaled, interpolated result in
  a fullscreen overlay window of its own, while the game window keeps the focus for input (NVIDIA
  forum thread on VRR and overlays [L4], a user's description). Capture is DXGI (Desktop
  Duplication, the default) or WGC (Windows Graphics Capture). The developer: DXGI "captures at
  the source frame rate"; WGC captures at a rate between the game's and the monitor's, so the
  game has to be capped at half the output rate [L2].
- **APIs:** any; it never touches the game's swap chain.
- **Requirements:** "Only windowed and borderless fullscreen modes are supported. Exclusive
  fullscreen mode may work only if the Lossless Scaling output is configured for a second
  display" (store page [L1]). The developer says driver sync and latency settings should stay at
  their defaults ("any sync or latency setting can break it") and that overlays interfere [L2].
  With LSFG working, its "Draw FPS" readout shows twice the base rate at 2× [L2].
- **Profiles:** a profile has a field for the game's exe, or a window name instead; the profile
  is applied for auto-scale and also when scaling with the hotkey or the Scale button (Steam
  discussion threads [L3]; user reports, not documentation).

### NVIDIA Smooth Motion

- **How it attaches:** in the driver, at presentation. NVIDIA: a "driver-based AI model" that
  infers "an additional frame between two rendered frames" [N1]. On Linux it is an implicit
  Vulkan layer, `VK_LAYER_NV_present`, which "overrides the application's presentation to inject
  additional frames" [N3]; the Windows mechanism is not documented (community projects name
  `NvPresent64.dll` [N5]).
- **How it is enabled:** per program. "To enable NVIDIA Smooth Motion, select a compatible
  DirectX 11, DirectX 12 or Vulkan game in Graphics > Program settings", then Driver Settings,
  Smooth Motion on [N1]. NVIDIA's page describes no global switch.
- **APIs:** DirectX 11, DirectX 12 and Vulkan [N1]. OpenGL is not listed.
- **GPUs:** RTX 50 at launch (January 2025, NVIDIA app 11.0.2 [N2]), all RTX 40 since the app and
  driver update of 18 August 2025 [N1]. NVIDIA's app page still says "for GeForce RTX 50 and 40
  Series" [N4]. RTX 30 has it only through community mods [N5].
- **Other requirements:** NVIDIA's pages give none for window mode, vsync, HDR or a minimum frame
  rate. It is meant for games without DLSS Frame Generation [N1].

### AMD Fluid Motion Frames (AFMF 2 / 2.1)

- **How it attaches:** in the driver, at presentation; switched on globally (Graphics tab, or the
  HYPR-RX profile on RX 7000 and later) or per game in Adrenalin's Gaming tab (secondary
  sources [A3]).
- **APIs:** DirectX 11 and 12; AFMF 2 added Vulkan and OpenGL (AMD's AFMF 2 preview notes and
  AFMF page [A1][A2]).
- **Requirements:** "exclusive or borderless fullscreen mode with V-SYNC disabled" (AFMF 2
  release notes, as quoted in search results [A1]). Borderless fullscreen needs RX 7000 or
  Radeon 700M and later; RX 6000 needs exclusive fullscreen [A1]. AFMF 2.1 needs Adrenalin 25.3.1
  or later [A2]. AMD's notes list "AFMF may become inactive after enabling certain on-screen
  overlays", and the Alt+R overlay shows whether it is active [A4].
- AMD's own pages timed out here; the AMD statements above come from search-result excerpts of
  them and from press coverage, not from a page read in full.

## How Moono looks to a tool

Read from the code (master at f4a3a222 for OpenGL, u60 at 4f3e5b7e for Vulkan).

| | The original (Unity 6, D3D11) | Moono |
|---|---|---|
| Process | `OxygenNotIncluded.exe` | `OniMoono.exe`, started in the game's place by Steam's launch options (`README.md`, `docs/INSTALL-MODES.md`) |
| Window title | Oxygen Not Included | the same: the game's `productName` (`Player.cs`, `WindowSettings`) |
| Window class | Unity's | `GLFW30` (GLFW's default class name) |
| Other windows | none known | OpenGL with DXGI on: two hidden 1×1 OpenGL windows with the same title and class (`GlfwPlatform.HiddenGlWindow`). Vulkan: none of these |
| Rendering API | D3D11 | OpenGL 4.5 (U59), Vulkan (U60) |
| Swap chain | D3D11 flip model | D3D11 flip model of a second, copy-only device (below) |

**The swap chain** (`Gl/DxgiSwapChain.cs`, shared by both renderers): a D3D11 device made with
`D3D11CreateDevice` on the renderer's adapter, default feature levels (11_0 or higher as the
driver gives; the level is logged). `CreateSwapChainForHwnd` on the visible window:
`R8G8B8A8_UNORM`, `FLIP_DISCARD`, 2 buffers, `DXGI_SCALING_STRETCH`, the default SDR colour
space, flags `FRAME_LATENCY_WAITABLE_OBJECT` plus `ALLOW_TEARING` where the system supports it.
Maximum frame latency 2. `MakeWindowAssociation(NO_WINDOW_CHANGES | NO_ALT_ENTER)`, and never
`SetFullscreenState`. It is made on the thread that starts the presenter and used by the
presenter thread alone, inside the `OniMoono.exe` process. `Present(vSyncCount, 0)` with vsync;
`Present(0, ALLOW_TEARING)` without. The start-up log's `present: DXGI on …` line states all of
this for a given run.

**Interop:** the D3D11 device draws nothing. OpenGL renders into a D3D11 texture shared through
`WGL_NV_DX_interop2` (`Gl/DxgiTarget.cs`); Vulkan into one imported with
`VK_KHR_external_memory_win32` and ordered by a keyed mutex (`Vulkan/VkDxgiTarget.cs`). Each
picture is one `CopySubresourceRegion` into the back buffer, then `Present`. With DXGI on there
is no `VkSwapchainKHR` and no `SwapBuffers` on the visible window, which has no pixel format.

**`--dxgi off`** (Moono Tweaks: "Present through Direct3D on Windows"): OpenGL presents with
`SwapBuffers` on the visible window; Vulkan through a `VkSwapchainKHR` (FIFO with vsync, else
IMMEDIATE or MAILBOX). The driver may layer either on DXGI itself (NVIDIA's "Vulkan/OpenGL
present method").

**Pacing:** with vsync the main thread waits for queued frames to pass the presenter
(`Gl/QueuedFrames.cs`), which changes when frames are made, not how they are presented. While no
frame comes for three usual frame times and at least 50 ms, the last picture is presented again
at sync interval 0 (`Gl/StallRepeats.cs`, `--stall-repeats`, DXGI only). While loading, the
splash redraws about 60 times a second. To a tool these are ordinary presents of identical or
near-identical pictures.

**Fullscreen:** the game's borderless mode is an undecorated monitor-sized window, not topmost.
The game's exclusive mode goes through GLFW (`window.SetFullscreen`): GLFW changes the display
mode and makes the window `WS_EX_TOPMOST` and monitor-sized, and iconifies it when it loses
focus (GLFW 3.4 `win32_window.c`: `getWindowExStyle`, `_glfwSetWindowMonitorWin32`,
`WM_KILLFOCUS`; read from GLFW's source, not checked against the build Moono ships). It is not
DXGI exclusive fullscreen.

## What could stop each tool

| | Lossless Scaling | Smooth Motion | AFMF |
|---|---|---|---|
| Exe is `OniMoono.exe` | profile keyed to the exe does not apply; a title-keyed one still does | the per-program setting does not apply: **likely the whole cause** | a per-game profile does not apply; a global switch still does |
| Window class, title | no effect expected | none | none |
| Hidden windows with the game's title (OpenGL) | a title-keyed profile or auto-scale could find a hidden 1×1 window first (inferred) | none | none |
| Copy-only D3D11 swap chain | none: it captures the window | unknown: D3D11 is supported and the swap chain is ordinary, but the game's rendering is in OpenGL or Vulkan | unknown, the same |
| No `VkSwapchainKHR` (U60, DXGI on) | none | the Vulkan path (a present layer on Linux [N3]) would have nothing to override; only the D3D11 path could apply | the same |
| OpenGL with `--dxgi off` | none | not a listed API | supported API |
| Tearing flag, waitable | none known | unknown | AFMF wants vsync off, which is when Moono tears; no conflict expected |
| Stall repeats, splash | extra identical frames; harmless | the same | the same |
| Borderless | supported | no stated rule | RX 7000 and later only |
| Exclusive | the original's is real exclusive, which Lossless Scaling does not support; Moono's is a topmost window it can capture, but its overlay may end up behind it (inferred) | no stated rule | RX 6000 needs exclusive; Moono's is not DXGI exclusive, so AFMF may not see it as such (inferred) |
| Frame rate now 90 to 100 | adaptive target may already be met; fixed 2× exceeds a 144 Hz display; WGC capture needs a cap at half the output rate [L2] | none stated | none stated |

## Likely causes, ranked

1. **The setting does not follow the exe.** Verified on our side: the process is `OniMoono.exe`.
   Verified on NVIDIA's: Smooth Motion is enabled per program [N1]. For AFMF and Lossless Scaling
   it depends on how the player set it up (per game, or by exe).
2. **Display mode.** A player on RX 6000 with AFMF needs exclusive fullscreen, which Moono does
   not provide in DXGI's sense. A Lossless Scaling user in the game's exclusive setting gets a
   topmost window under Moono.
3. **The driver does not treat the process as a supported game** because the rendering device is
   OpenGL or Vulkan and the D3D11 device only presents. Unknown for both vendors; needs a test.
4. **Lossless Scaling is working as set** but has little to add at the higher frame rate, or its
   capture is disturbed (hidden windows by title; WGC without a cap).

## What the player can do now

Ask first: which tool, which GPU, which window mode in the game's options, and whether vsync is
on.

- **Smooth Motion (RTX 40/50):** NVIDIA app → Graphics → Program settings → add
  `OniMoono.exe` (by default `%LOCALAPPDATA%\oni-moono\player\OniMoono.exe`) → Driver Settings →
  Smooth Motion: On. Restart the game. If it does not engage on U60, try Moono Tweaks' "Present
  through Direct3D on Windows" off (`--dxgi off`), which gives the driver a Vulkan swapchain, a
  supported API. On U59 leave it on: off means OpenGL presentation, which Smooth Motion does not
  list.
- **AFMF:** in Adrenalin's Gaming tab add `OniMoono.exe` and enable AFMF there, or enable it
  globally. Turn vsync off in the game. Use borderless on RX 7000 and later. Check the status in
  the Alt+R overlay. If it stays inactive, try "Present through Direct3D on Windows" off: AFMF 2
  lists OpenGL and Vulkan. On RX 6000 (exclusive only) it may not work with either setting.
- **Lossless Scaling:** set the game to borderless or windowed, not fullscreen. Scale with the
  hotkey while the game window has the focus. In the profile, point the exe field at
  `OniMoono.exe`. Turn on "Draw FPS": at 2× the second number should be twice the first. With an
  adaptive target, raise it above the new base rate. If the capture is black or frozen, switch
  the capture API (DXGI ↔ WGC), and with WGC cap the game at half the output rate.

## What Moono could change

Nothing here is landed. In order of value against risk:

1. **A player-facing page** in `docs/` (not the READMEs) with the steps above, once at least one
   tool has been tried, so it states what works and not what should.
2. **A different title for the hidden OpenGL windows** (for example "OniMoono OpenGL context"),
   so a lookup by the game's title finds only the visible window. Two lines in
   `GlfwPlatform.HiddenGlWindow`'s callers. OpenGL only by nature: Vulkan has no hidden windows.
3. **A log line** with the visible window's class, styles and topmost state next to the
   `present:` line, so a report can be read without a debugger. Both renderers.
4. **Exclusive fullscreen without topmost and auto-iconify** (GLFW's `FLOATING` and
   `AUTO_ICONIFY` attributes) would let capture tools draw over it. It changes how the window
   behaves on focus loss, so it needs its own comparison with the original. Both renderers.
5. **An install option that names the entry point `OxygenNotIncluded.exe`**, so settings keyed to
   the game's exe apply unchanged. It would also pick up every other driver and overlay profile
   for the game, wanted or not, and makes the two processes harder to tell apart in reports.
   Whether drivers match on the file name alone is not verified. Not recommended before items 1
   to 3 show it is needed.
6. **Not recommended:** writing a driver profile for `OniMoono.exe` ourselves (it changes the
   player's driver settings); DXGI exclusive fullscreen for AFMF on RX 6000 (`SetFullscreenState`
   makes DXGI message the window from the presenter thread, the deadlock the presenter's design
   avoids, ARCHITECTURE §3.5).

`--dxgi on|off` already gives each renderer both ways of presenting, so no new presenter mode is
proposed.

## What can be tested on this machine

- **Smooth Motion: no.** The machine has an RTX 3090 on driver 610.88 (`nvidia-smi`, read-only).
  NVIDIA lists RTX 50 and 40 only [N1][N4]; RTX 30 works only with community mods [N5], which
  would mean installing software and changing driver profiles.
- **AFMF: no.** It needs a Radeon as the rendering GPU and a driver setting changed.
- **Lossless Scaling: only by the user,** if they own it. It installs nothing into the game and
  changes no driver setting.

Manual checks the user could do:

1. **Smooth Motion is absent on the 3090.** NVIDIA app → Graphics → Program settings → select
   any DirectX 11 game → scroll to Driver Settings. Expected: no Smooth Motion row. Change
   nothing.
2. **What Moono's window looks like.** Start Moono as usual, quit, and open the log. The
   `present: DXGI on …` line gives the adapter, feature level, swap effect, buffer count and
   tearing support.
3. **Lossless Scaling, if owned.** (a) Game options: borderless, vsync on. (b) Start Lossless
   Scaling, Frame Generation LSFG, mode Fixed 2×, capture API DXGI, "Draw FPS" on. (c) Start
   Moono, load a colony, press the scale hotkey with the game focused. (d) Read the two numbers
   in the top-left corner: base and output. Output at twice the base means it works. (e) Repeat
   with the game's fullscreen (exclusive) setting, with vsync off, and with "Present through
   Direct3D on Windows" off. (f) Add a profile with the window name "Oxygen Not Included" and
   auto-scale on, and see whether it scales the game window on start (this is the hidden-window
   check, U59 only).
4. **Ask the reporter** for the tool, GPU, window mode and vsync setting. That alone probably
   decides between causes 1 and 2.

## Sources

Lossless Scaling
- [L1] Steam store page: https://store.steampowered.com/app/993090/Lossless_Scaling/
- [L2] Developer's reply on DXGI and WGC capture rates:
  https://steamcommunity.com/app/993090/discussions/0/4288061352818435631
- [L3] Profile fields and auto-scale (user threads):
  https://steamcommunity.com/app/993090/discussions/0/4429939323288710347 and
  https://steamcommunity.com/app/993090/discussions/0/5958905954910655850/
- [L4] The overlay window and VRR:
  https://forums.developer.nvidia.com/t/fix-vrr-for-overlays-always-on-top-windows/296168

NVIDIA
- [N1] NVIDIA, 18 August 2025, Smooth Motion for RTX 40 and how to enable it:
  https://www.nvidia.com/en-eu/geforce/news/nvidia-app-global-dlss-overrides-rtx-40-series-smooth-motion/
- [N2] NVIDIA app 11.0.2 release highlights:
  https://www.nvidia.com/en-gb/geforce/release-notes/NVAPP/11_0_2/Web/nvapp-v11_0_2-web-release-highlights/
- [N3] NVIDIA Linux driver README, chapter 39, NVIDIA Smooth Motion:
  https://download.nvidia.com/XFree86/Linux-x86_64/595.58.03/README/nvpresent.html
- [N4] NVIDIA app page: https://www.nvidia.com/en-us/software/nvidia-app/
- [N5] Community RTX 30 mods (secondary):
  https://www.tweaktown.com/news/113722/modders-get-nvidia-smooth-motion-running-on-rtx-30-gpus-without-official-support/index.html
  and https://github.com/xikarioz/Smooth-Motion-RTX30

AMD (not read in full, see above)
- [A1] AFMF 2 technical preview release notes:
  https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-AFMF2-TECH-Preview.html
- [A2] AFMF page: https://www.amd.com/en/products/software/adrenalin/afmf.html
- [A3] Enabling globally or per game (secondary):
  https://www.overclockers.co.uk/blog/amd-radeon-uncovered-amd-fluid-motion-frames/
- [A4] Adrenalin 24.9.1 release notes:
  https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-24-9-1.html

GLFW
- GLFW 3.4, `src/win32_window.c`: https://github.com/glfw/glfw/blob/3.4/src/win32_window.c
