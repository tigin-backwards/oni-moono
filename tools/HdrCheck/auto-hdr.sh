#!/usr/bin/env bash
# Auto HDR check (issue #112, the D3D11 plan's M4): does Windows' Auto HDR engage for the original
# and for Moono's D3D11 player? A runq windows job, run only with the user's word: it changes the
# user's Auto HDR setting and the HDR display's HDR state for its run, and restores both.
#   runq submit --kind windows --script tools/HdrCheck/auto-hdr.sh
# runq's windows-rules stop a job whose game is not on monitor 2 (docs/RUNQ.md): this job puts
# both players on the HDR display (\\.\DISPLAY1, AHDR_DISPLAY), so it needs that rule relaxed for
# it, or its players are killed at once.
#
# What it changes, HKCU only (tools/HdrCheck/auto-hdr-reg.ps1; no HKLM, no elevation):
# - phase global: HKCU\Software\Microsoft\DirectX\UserGpuPreferences, DirectXUserGlobalSettings
#   (REG_SZ), its AutoHDREnable=1 and SwapEffectUpgradeEnable=1 tokens set, any other tokens kept;
# - phase force (on top of global): ForceAutoHDR's per-title method for both executables:
#   UserGpuPreferences\<exe path> with AppStatus=1 and AutoHDREnable=2097 merged in, and
#   HKCU\Software\Microsoft\Direct3D\<exe name> with Name=<exe> and
#   D3DBehaviors=BufferUpgradeOverride=1;BufferUpgradeEnable10Bit=1;
# - HDR on for the display (DisplayConfigSetDeviceInfo SET_HDR_STATE, tools/HdrCheck/hdrtoggle).
# Every value it may touch is saved first (state.json in the job's folder: absent, or kind and
# data); on any exit a trap puts each back exactly (a value absent before is deleted, a key it
# created removed), switches HDR off if it was off, and prints both read back. If the job is
# killed outright (SIGKILL), restore by hand: RESTORE.txt in the job's folder has the commands.
#
# Each phase (AHDR_PHASES, default "global force") runs the original (the frozen build's
# isolated copy, original-windows.sh, ORIG_FOREGROUND_AT handover, launcher output kept) and ours
# (run-windows.sh --renderer d3d11, --no-focus), each borderless at the display's size. Once the
# player's window is seen on the display (DWM's frame bounds, so no invisible border), the display
# is captured in FP16 (dupcap) and cropped to the window. Game Bar's and the shell's visible
# windows (a notification) are listed meanwhile, never touched. No keys are sent, the pointer is
# not moved; run homes isolate the user's game, PlayerPrefs, Documents and LocalLow.
# Reading: Windows composes SDR content at the SDR white level (240 nits on that display, j0009190);
# a window whose maximum is well above it (AHDR_SDR_WHITE x 1.15) was taken for HDR content.
set -uo pipefail
root=/home/nigit/projects/oni-brutal/out/d3d11-m4-wt
: "${ONI_RUN_HOME:?}" "${RUNQ_OUT:?}" "${RUNQ_JOB:?}"
out="$RUNQ_OUT"
name="${AHDR_DISPLAY:-\\\\.\\DISPLAY1}"
phases="${AHDR_PHASES:-global force}"
white="${AHDR_SDR_WHITE:-240}"
orig_dir='E:\oni-builds\BrutalNotIncluded-764025'
orig_exe="$orig_dir\\BrutalNotIncluded-764025.exe"
win_temp="$(cd /mnt/c && cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
stage_win="${ONI_WIN_STAGE:-$win_temp\\oni-brutal}"
ours_exe="$stage_win\\host\\OniMoono.exe"
work_win="$win_temp\\oni-hdrcheck\\ahdr-$RUNQ_JOB"
work="$(wslpath "$work_win")"; mkdir -p "$work"
cp "$root/tools/HdrCheck/dupcap.c" "$root/tools/HdrCheck/hdrtoggle.c" "$root/tools/HdrCheck/build.cmd" "$root/tools/HdrCheck/auto-hdr-reg.ps1" "$work/"
(cd "$work" && cmd.exe /c build.cmd 2>&1 | tr -d '\r') || true
[ -f "$work/hdrtoggle.exe" ] && [ -f "$work/dupcap.exe" ] || { echo "hdrtoggle/dupcap did not build: aborted, nothing changed"; exit 1; }
toggle() { (cd "$work" && ./hdrtoggle.exe "$@" | tr -d '\r'); }
state="$work_win\\state.json"
reg() { # ACTION [ARGS]: the registry helper
    (cd /mnt/c && powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$work_win\\auto-hdr-reg.ps1" -Action "$@" 2>&1 | tr -d '\r'; exit "${PIPESTATUS[0]}")
}

# The display by its GDI name: bounds and its number in GLFW's and Unity's order (-monitor N:
# the primary first, then by X).
read -r mx my mw mh mon <<< "$(cd /mnt/c && powershell.exe -NoProfile -Command "
Add-Type -AssemblyName System.Windows.Forms
\$s = @([System.Windows.Forms.Screen]::AllScreens | Sort-Object { -not \$_.Primary }, { \$_.Bounds.X })
for (\$i = 0; \$i -lt \$s.Count; \$i++) { if (\$s[\$i].DeviceName -eq '$name') { \$b = \$s[\$i].Bounds; \"\$(\$b.X) \$(\$b.Y) \$(\$b.Width) \$(\$b.Height) \$(\$i + 1)\" } }" | tr -d '\r')"
[ -n "${mon:-}" ] || { echo "no screen named $name: aborted, nothing changed"; exit 1; }
echo "HDR display: $name ${mw}x$mh at $mx,$my, -monitor $mon"
echo "Windows: $(cd /mnt/c && powershell.exe -NoProfile -Command "\$v = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'; \"\$(\$v.DisplayVersion) build \$(\$v.CurrentBuild).\$(\$v.UBR)\"" | tr -d '\r')"
before="$(toggle list | grep -F "$name supported")"
echo "HDR before: $before"
[[ "$before" == *"hdrSupported 1"* ]] || { echo "$name reports no HDR support: aborted, nothing changed"; exit 1; }
was_on=0; [[ "$before" == *"active HDR"* ]] && was_on=1

saved=0; restored=0
restore() {
    [ $restored -eq 1 ] && return; restored=1
    echo "--- restore"
    if [ $saved -eq 1 ]; then reg restore -State "$state" | tee "$out/registry-after.txt"; fi
    if [ $was_on -eq 0 ]; then echo "HDR: $(toggle set "$name" off)"; fi
    echo "HDR after: $(toggle list | grep -F "$name supported")"
    [ $saved -eq 1 ] && reg show -State "$state" | tee -a "$out/registry-after.txt"
    cp "$work/state.json" "$out/" 2>/dev/null
}
trap restore EXIT
trap 'exit 130' INT; trap 'exit 143' TERM; trap 'exit 129' HUP

# Save first; nothing is written unless the save succeeded.
reg save -State "$state" -Exes "$orig_exe|$ours_exe" | tee "$out/registry-before.txt"
[ "${PIPESTATUS[0]}" -eq 0 ] && [ -f "$work/state.json" ] || { echo "registry save failed: aborted, nothing changed"; exit 1; }
saved=1; cp "$work/state.json" "$out/"
cat > "$out/RESTORE.txt" <<EOF
If this job died without its restore lines in out.log, from WSL:
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File '$work_win\\auto-hdr-reg.ps1' -Action restore -State '$state'
  (cd '$work' && ./hdrtoggle.exe set '$name' off)   # only if HDR was off before (HDR before: $before)
EOF

if [ $was_on -eq 0 ]; then toggle set "$name" on || { echo "HDR could not be switched on: aborted"; exit 1; }; fi
echo "dupcap now: $(cd "$work" && ./dupcap.exe $((mx + 10)) $((my + 10)) sdr now.ppm | head -1 | tr -d '\r')"

export ONI_WIN_GAME_DIR='E:\oni-builds\oni-win-764025'
home="$ONI_RUN_HOME"
prefs() { # prefs HOME: the game's settings, borderless at the display's size, muted
    local d; d="$(wslpath "$1")/Documents/Klei/OxygenNotIncluded"; mkdir -p "$d"
    printf 'strings: {}\nints:\n  ResolutionWidth: %s\n  ResolutionHeight: %s\n  RefreshRateNumerator: 60\n  RefreshRateDenominator: 1\n  FullScreen: 1\nfloats:\n  Volume_Master: 0\n  Volume_SFX: 0\n  Volume_Music: 0\n  Volume_Ambience: 0\n  Volume_UI: 0\n  Volume_VO: 0\n' "$mw" "$mh" > "$d/kplayerprefs.yaml"
}
args=(-screen-fullscreen 1 -window-mode borderless -screen-width "$mw" -screen-height "$mh")
on_display() { # X Y W H: the window lies on the display (2 px of slack)
    [ -n "${4:-}" ] && [ "$3" -gt 100 ] && [ "$1" -ge $((mx - 2)) ] && [ "$2" -ge $((my - 2)) ] && [ $(($1 + $3)) -le $((mx + mw + 2)) ] && [ $(($2 + $4)) -le $((my + mh + 2)) ]
}
overlays() { # TAG SECONDS: Game Bar's and the shell's visible windows, logged when they change
    local now; now="$(reg overlays)"
    [ "$now" = "${last_overlays:-}" ] && return; last_overlays="$now"
    printf '%s at %s s:\n%s\n' "$1" "$2" "$now" | tee -a "$out/overlays.txt" | sed "s/^/$1 overlays: /"
}
capture() { # TAG X Y W H (desktop): the display in FP16, and its crop to the window
    local x=$(($2 > mx ? $2 : mx)) y=$(($3 > my ? $3 : my))
    local w=$(( ($2 + $4 < mx + mw ? $2 + $4 : mx + mw) - x )) h=$(( ($3 + $5 < my + mh ? $3 + $5 : my + mh) - y ))
    (cd "$work" && ./dupcap.exe $((mx + 10)) $((my + 10)) fp16 "$1.pfm" $((x - mx)) $((y - my)) "$w" "$h" | tr -d '\r' | sed "s/^/$1: /")
    cp "$work/$1.pfm" "$out/" 2>/dev/null || return
    python3 - "$out/$1.pfm" $((x - mx)) $((y - my)) "$w" "$h" <<'PY'
import sys, numpy as np
path, x, y, w, h = sys.argv[1], *map(int, sys.argv[2:])
with open(path, 'rb') as f:
    f.readline(); W, H = map(int, f.readline().split()); s = float(f.readline())
    a = np.frombuffer(f.read(), dtype='<f4' if s < 0 else '>f4').reshape(H, W, 3)
top = H - (y + h)  # PFM rows run bottom up
crop = a[top:top + h, x:x + w]
with open(path.replace('.pfm', '-window.pfm'), 'wb') as f:
    f.write(b'PF\n%d %d\n-1.0\n' % (w, h)); f.write(np.ascontiguousarray(crop, dtype='<f4').tobytes())
PY
}
watch_and_capture() { # TAG PID MATCH PROC FIRST SETTLE MAX (seconds since the launch): from FIRST,
    # once the window is on the display, SETTLE more, then capture; overlays listed every poll
    local tag=$1 pid=$2 match=$3 proc=$4 first=$5 settle=$6 max=$7 seen="" r rv el t0=$SECONDS
    last_overlays=""
    while :; do
        sleep 1; el=$((SECONDS - t0))
        kill -0 "$pid" 2>/dev/null || { echo "$tag: the launcher ended after $el s, not captured"; return; }
        [ "$el" -le "$max" ] || break
        [ "$el" -ge "$first" ] || continue
        r="$(reg rect -Match "$match" -Proc "$proc")"; read -r -a rv <<< "$r"
        overlays "$tag" "$el"
        if on_display "${rv[@]}"; then
            [ -n "$seen" ] || { seen=$el; echo "$tag: window $r (desktop) on $name at $el s"; }
            if [ $((el - seen)) -ge "$settle" ]; then
                echo "$tag: captured at $el s, window $r"
                capture "$tag" "${rv[@]}"
                sleep 5; overlays "$tag" "$((SECONDS - t0))"
                return
            fi
        else
            [ -z "$seen" ] || { echo "$tag: window left the display ('${r:-none}') at $el s"; seen=""; }
            echo "$tag: $el s, window '${r:-none}', not on $name"
        fi
    done
    echo "$tag: no window on $name in $max s, not captured"
}
# Ends a player of this job by its run home (out/local-tools/win-run.ps1, never by name).
end_player() { (cd /mnt/c && powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w /home/nigit/projects/oni-brutal/out/local-tools/win-run.ps1)" -Match "$1" -Action kill | tr -d '\r'); }

for phase in $phases; do
    echo "=== phase $phase"
    reg apply -State "$state" -Phase "$phase" | tee -a "$out/registry-applied.txt"
    [ "${PIPESTATUS[0]}" -eq 0 ] || { echo "phase $phase: the registry write failed: stopped"; exit 1; }

    # The original: isolated as always, the launcher's output kept, made the foreground 30 s in.
    h="$home\\ahdr-$phase-orig"; prefs "$h"
    (cd "$root" && ONI_RUN_HOME="$h" ORIG_DIR="$orig_dir" ORIG_SECONDS=150 ORIG_FOREGROUND_AT=30 \
        ONI_STEAM_STUB=/home/nigit/projects/oni-brutal/out/local-tools/steamstub-dlc-win/steam_api64.dll \
        scripts/original-windows.sh "${args[@]}" -monitor "$mon" > "$out/original-$phase.launcher.txt" 2>&1) &
    launcher=$!
    # Its menu is up about 20 s in; captured from 45 s (15 s after the foreground handover).
    watch_and_capture "original-$phase" $launcher "ahdr-$phase-orig" 'BrutalNotIncluded*' 30 15 140
    wait $launcher
    tail -8 "$out/original-$phase.launcher.txt"

    # Ours: run-windows.sh (--no-focus, muted; -monitor after its own, so the HDR display); ended
    # by its run home once captured (--frames only bounds it).
    h="$home\\ahdr-$phase-ours"; prefs "$h"
    ONI_RUN_HOME="$h" "$root/scripts/run-windows.sh" --renderer d3d11 --frames 20000 --log "$h\\player.log" "${args[@]}" -monitor "$mon" > "$out/ours-$phase.launcher.txt" 2>&1 &
    launcher=$!
    watch_and_capture "ours-d3d11-$phase" $launcher "ahdr-$phase-ours" 'OniMoono*' 15 20 90
    end_player "$h"
    wait $launcher
    grep -a "renderer:\|d3d11: NVIDIA\|present" "$(wslpath "$h")/player.log" 2>/dev/null | head -4 | cut -c1-200
done

echo "=== summary (SDR white $white nits)"
if ls "$out"/*-window.pfm > /dev/null 2>&1; then
    python3 "$root/tools/HdrCheck/summary.py" "$out"/*-window.pfm | tee "$out/summary.txt"
    python3 - "$white" "$out"/*-window.pfm <<'PY' | tee -a "$out/summary.txt"
import sys, numpy as np
white = float(sys.argv[1])
for path in sys.argv[2:]:
    with open(path, 'rb') as f:
        f.readline(); w, h = map(int, f.readline().split()); f.readline()
        rgb = np.fromfile(f, dtype='<f4', count=w * h * 3).reshape(h, w, 3)
    nits = 80.0 * (0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2])
    peak = float(nits.max())
    above = int((nits > white * 1.15).sum())
    verdict = 'ABOVE the SDR white level: taken for HDR content (Auto HDR engaged)' if peak > white * 1.15 else 'at or below the SDR white level: composed as SDR'
    print(f'{path.rsplit("/", 1)[-1]}: peak {peak:.0f} nits, {above} pixels above {white * 1.15:.0f}: {verdict}')
PY
else
    echo "no window captured"
fi
