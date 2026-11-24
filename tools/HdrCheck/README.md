# HDR check

Checks how the original game and Moono reach an HDR-capable monitor: Windows should show Moono as
it shows the original, its white UI at the "SDR content brightness" level, not as HDR content. It
runs five players one after another, fullscreen on that monitor at the main menu, and captures
the monitor each time in FP16 as Windows composes it:

- `original`: the original game (its isolated copy, as `scripts/original-windows.sh` runs it);
- `ours-vulkan-dxgi`, `ours-opengl-dxgi`: Moono presenting through Direct3D (the default);
- `ours-vulkan-own`, `ours-opengl-own`: Moono presenting through Vulkan's swapchain or OpenGL's
  SwapBuffers (`--dxgi off`), the presentation behind the report of forced HDR.
- `ours-d3d11-own` (only when named in `ONI_HDR_ONLY`): Moono's Direct3D 11 renderer
  (`--renderer d3d11`), which presents through its own flip-model swap chain whatever `--dxgi`
  says.

Nothing is changed on the system: no HDR, display or driver setting. The runs use their own run
homes and the offline Steam double, as the other test runs.

## Running it

1. Close Oxygen Not Included if it is running.
2. Switch HDR on for the monitor (Settings → System → Display → Use HDR).
3. From WSL, in a checkout with the Windows player built (`scripts/build-windows.sh`) and the
   original's isolated copy set up (`scripts/original-windows.sh`):

       tools/HdrCheck/hdr-check.sh 1

   `1` is the monitor, numbered as the game's `-monitor` (1 is the primary one). The monitor
   shows each player for about a minute; leave it alone meanwhile. For the U60 builds:
   `ORIG_DIR='E:\Development\BrutalNotIncluded-u60' ONI_HDR_GAME='E:\oni-builds\oni-win-761015'`
   before the command. `ONI_HDR_ONLY="original ours-vulkan-dxgi"` runs some of the players only.
4. Optionally, run it again with HDR off for that monitor: the report was that the game switched
   HDR on by itself.

The captures and `summary.txt` land in `out/hdr-check/`. Each player's line has the monitor's
colour space while it ran (HDR or SDR), its `present:` log line (Moono's), and in the summary the
median luminance (the menu's background) and the mean of the brightest 0.5% of pixels (the white
title and button text), in nits.

## Reading the result

- With HDR on: the `-dxgi` rows should be close to the original's, the brightest 0.5% near the
  "SDR content brightness" slider's level for all three. A row much brighter than the original's
  (hundreds of nits more) means Windows or the driver treated that player as HDR content (Auto HDR,
  RTX HDR, or NVIDIA's "layered on DXGI" present): expected, if anywhere, for the `-own` rows.
- With HDR off: every line should say `SDR`, as `before:` and `after:` do. A player whose line says
  `HDR` switched the monitor into HDR.
- Each Moono row's `present:` line must name what it used: DXGI on the adapter, or why not.

## The capture tool

`dupcap.c` (built by `build.cmd` with the Visual Studio C++ build tools) captures one monitor
through DXGI Desktop Duplication, no admin rights needed, and prints the monitor's colour space:

    dupcap X Y sdr OUT.ppm                    8-bit, what an SDR monitor shows
    dupcap X Y fp16 OUT.pfm [RX RY RW RH]     FP16 scRGB (IDXGIOutput5::DuplicateOutput1);
                                              prints the region's mean and maximum in nits

X, Y is any desktop point on the monitor. With HDR off the monitor gives an 8-bit frame even
when FP16 is asked for; dupcap then says so and reports sRGB-decoded values with SDR white as
80 nits.
