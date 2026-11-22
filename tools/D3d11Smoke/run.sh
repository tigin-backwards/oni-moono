#!/usr/bin/env bash
# Builds tools/D3d11Smoke for win-x64 and runs it on the Windows side from WSL (staged in
# %TEMP%), printing its report. Windows only; through runq as a light job:
#   RUNQ_OWNER=... scripts/runq run --kind light -- tools/D3d11Smoke/run.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
source "$here/../../scripts/env.sh"
out="$root/out/win/d3d11smoke"
rm -rf "$out"
dotnet publish "$here" -c Release -r win-x64 --self-contained false -o "$out" -v q -nologo > /dev/null
win_temp="$(cd /mnt/c && cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
stage="$(wslpath "$win_temp")/oni-moono-d3d11smoke-$$"
rm -rf "$stage"; mkdir -p "$stage"
trap 'rm -rf "$stage"' EXIT
cp -r "$out/." "$stage/"
cd "$stage" && ./D3d11Smoke.exe | tr -d '\r'
