#!/usr/bin/env bash
# Pixel comparison of the Direct3D 11 device against the OpenGL device on Windows
# (docs/research/d3d11-plan.md, M2): ours is started twice in one run home, `--renderer opengl`
# then `--renderer d3d11`, each capturing the same frame of the main menu (`--capture`, the
# finished back buffer), and tools/PixelDiff/pixel_diff.py compares the two.
#   scripts/d3d11-pixel-diff.sh [--frame N] [--first opengl|d3d11] [-- EXTRA HOST ARGS...]
# The game's clocks advance a fixed 1/60 s a frame (ONI_FIXED_FRAME_SECONDS, unless set), so both
# runs show the same animation frame at frame N. Output (the run's $RUNQ_OUT, else
# out/d3d11-pixel-diff): opengl.png, d3d11.png, diff.png, diff.json and each run's log.
# Queues itself through runq as kind windows (docs/RUNQ.md); run-windows.sh's rules hold.
set -euo pipefail
source "$(dirname "$0")/env.sh"
source "$root/scripts/runq-submit.sh"
runq_self_submit --kind windows --label "d3d11-pixel-diff.sh${*:+ $*}" -- "$root/scripts/d3d11-pixel-diff.sh" "$@"
frame=600
first=opengl
while [ $# -gt 0 ]; do
  case "$1" in
    --frame) frame="$2"; shift 2 ;;
    --first) first="$2"; shift 2 ;;
    --) shift; break ;;
    *) echo "usage: $0 [--frame N] [--first opengl|d3d11] [-- HOST ARGS...]" >&2; exit 2 ;;
  esac
done
out="${RUNQ_OUT:-$root/out/d3d11-pixel-diff}"
mkdir -p "$out"
export ONI_FIXED_FRAME_SECONDS="${ONI_FIXED_FRAME_SECONDS:-0.016666668}"
export WSLENV="${WSLENV:+$WSLENV:}ONI_FIXED_FRAME_SECONDS"
win_temp="$(cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
run_home="${ONI_RUN_HOME:-$win_temp\\oni-brutal\\run-home}"
second=$([ "$first" = opengl ] && echo d3d11 || echo opengl)
status=0
for renderer in "$first" "$second"; do
  shot="$run_home\\pixel-diff-$renderer.png"
  rm -f "$(wslpath "$shot")"
  echo "== $renderer: frame $frame"
  "$root/scripts/run-windows.sh" --renderer "$renderer" --frames $((frame + 30)) --capture "$frame:$shot" "$@" > "$out/$renderer.log" 2>&1 || status=$?
  grep -E "^\[engine\] (renderer|d3d11)|d3d11 debug layer|Exiting after" "$out/$renderer.log" | head -20 || true
  cp "$(wslpath "$shot")" "$out/$renderer.png" || { echo "no capture from $renderer"; status=1; }
done
[ -f "$out/opengl.png" ] && [ -f "$out/d3d11.png" ] || exit 1
python3 "$root/tools/PixelDiff/pixel_diff.py" "$out/opengl.png" "$out/d3d11.png" --diff "$out/diff.png" --json "$out/diff.json" || status=$?
exit "$status"
