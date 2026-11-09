#!/usr/bin/env bash
# The HDR check (tools/HdrCheck/README.md): how the original game and Moono reach an HDR-capable
# monitor. Each player in turn opens fullscreen on that monitor at the main menu: the original,
# then Moono with Vulkan and with OpenGL, each presenting through DXGI (the default) and through
# the renderer's own presentation (--dxgi off). The monitor is captured in FP16 (dupcap, Desktop
# Duplication) and the luminance of its brightest UI (the white title and button text) printed in
# nits, with the monitor's colour space at that moment (HDR or SDR). Run from WSL in a checkout
# with the Windows player built (scripts/build-windows.sh) and the original's isolated copy set
# up (scripts/original-windows.sh; ORIG_DIR names it). Test runs only: own run homes, the offline
# Steam double; no setting is changed.
#   tools/HdrCheck/hdr-check.sh [MONITOR]      MONITOR: 1-based, as -monitor (default 1)
# ONI_HDR_GAME: the game folder Moono runs (default the installed game); ONI_HDR_ONLY: a list of
# the players to run (original ours-vulkan-dxgi ours-vulkan-own ours-opengl-dxgi ours-opengl-own).
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
monitor="${1:-1}"
[ "$(powershell.exe -NoProfile -Command "(Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).RunningAppID" | tr -d '\r')" = 457140 ] \
    && { echo "Oxygen Not Included is running in Steam; close it first." >&2; exit 1; }
[ -f "$root/out/win/host/OniMoono.exe" ] || { echo "no out/win: run scripts/build-windows.sh first" >&2; exit 1; }
win_temp="$(cd /mnt/c && cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
work="$(wslpath "$win_temp")/oni-hdrcheck"; workw="$win_temp\\oni-hdrcheck"
out="$root/out/hdr-check"
mkdir -p "$work" "$out"
cp "$root/tools/HdrCheck/dupcap.c" "$root/tools/HdrCheck/build.cmd" "$work/"
(cd "$work" && cmd.exe /c build.cmd 2>&1 | tr -d '\r') || true
[ -f "$work/dupcap.exe" ] || { echo "dupcap did not build (needs the Visual Studio C++ build tools)" >&2; exit 1; }
rm -rf "$work/host"; cp -r "$root/out/win/host" "$work/host"
cp "$root/out/win/steamstub/steam_api64.dll" "$work/steam_api64.dll"
game="${ONI_HDR_GAME:-C:\\Program Files (x86)\\Steam\\steamapps\\common\\OxygenNotIncluded}"
only="${ONI_HDR_ONLY:-original ours-vulkan-dxgi ours-vulkan-own ours-opengl-dxgi ours-opengl-own}"

# The monitor's desktop rectangle (GLFW's and Unity's order is the display order, monitor 1 the
# primary one): "X Y W H".
read -r mx my mw mh <<< "$(powershell.exe -NoProfile -Command "
Add-Type -AssemblyName System.Windows.Forms
\$s = [System.Windows.Forms.Screen]::AllScreens | Sort-Object { -not \$_.Primary }, { \$_.Bounds.X }
\$b = \$s[$monitor - 1].Bounds; \"\$(\$b.X) \$(\$b.Y) \$(\$b.Width) \$(\$b.Height)\"" | tr -d '\r')"
echo "monitor $monitor: ${mw}x$mh at $mx,$my"
echo "before: $(cd "$work" && ./dupcap.exe $((mx + 10)) $((my + 10)) sdr before.ppm | tr -d '\r' | head -1)"

home() { # home NAME: an empty run home whose game settings ask for fullscreen at the monitor's size
    local d="$work/$1/Documents/Klei/OxygenNotIncluded"
    rm -rf "${work:?}/$1"; mkdir -p "$d"
    printf 'strings: {}\nints:\n  ResolutionWidth: %s\n  ResolutionHeight: %s\n  RefreshRateNumerator: 60\n  RefreshRateDenominator: 1\n  FullScreen: 1\nfloats: {}\n' "$mw" "$mh" > "$d/kplayerprefs.yaml"
}

stop() { powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$root/tools/HdrCheck/stop.ps1")" -Match "$1" > /dev/null 2>&1 || true; }

capture() { # capture NAME SECONDS: the monitor in FP16, SECONDS after the player started
    sleep "$2"
    (cd "$work" && ./dupcap.exe $((mx + 10)) $((my + 10)) fp16 "$1.pfm" | tr -d '\r' | sed "s/^/$1: /")
    cp "$work/$1.pfm" "$out/$1.pfm"
}

pfms=()
for which in $only; do
    home "$which"
    if [ "$which" = original ]; then
        (cd "$root" && ONI_RUN_HOME="$workw\\original" ORIG_SECONDS=180 ONI_STEAM_STUB="$work/steam_api64.dll" scripts/original-windows.sh -monitor "$monitor" > /dev/null 2>&1) &
        capture original 140 # the original reaches the menu later than Moono
        stop "$workw\\original\\Player.log"
    else
        renderer=${which#ours-}; renderer=${renderer%-*}
        dxgi=on; [ "${which##*-}" = own ] && dxgi=off
        "$work/host/OniMoono.exe" --game "$game" --run-home "$workw\\$which" --audio-wav "$workw\\$which" --splash none --no-tweaks-menu \
            --native-override "steam_api64=$workw\\steam_api64.dll" --log "$workw\\$which\\player.log" -monitor "$monitor" \
            --renderer "$renderer" --dxgi "$dxgi" > /dev/null 2>&1 &
        capture "$which" 50
        stop "$workw\\$which\\player.log"
        grep -a '\] present:\|\] renderer:' "$work/$which/player.log" | sed "s/^/$which: /" | cut -c1-170
    fi
    wait || true
    pfms+=("$out/$which.pfm")
    sleep 3
done
echo "after: $(cd "$work" && ./dupcap.exe $((mx + 10)) $((my + 10)) sdr after.ppm | tr -d '\r' | head -1)"

python3 "$root/tools/HdrCheck/summary.py" "${pfms[@]}" | tee "$out/summary.txt"
