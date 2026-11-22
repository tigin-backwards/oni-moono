#!/usr/bin/env bash
# Builds tools/D3d11Native (caps.exe, probe_native.dll) from WSL with the Windows machine's MSVC:
# stages the sources in %TEMP%, runs build.cmd there and copies the outputs to DEST (default
# out/win/d3d11native). Research tools for the D3D11 plan, not part of the product.
#   tools/D3d11Native/build.sh [DEST]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
dest="${1:-$root/out/win/d3d11native}"
win_temp="$(cd /mnt/c && cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
stage="$(wslpath "$win_temp")/oni-moono-d3d11native-$$"
rm -rf "$stage"; mkdir -p "$stage" "$dest"
trap 'rm -rf "$stage"' EXIT
cp "$here/"{caps.cpp,probe_native.c,ctx_hook.c,build.cmd} "$stage/"
(cd "$stage" && cmd.exe /c build.cmd 2>&1 | tr -d '\r') || true
[ -f "$stage/caps.exe" ] && [ -f "$stage/probe_native.dll" ] || { echo "D3d11Native: the build failed" >&2; exit 1; }
cp "$stage/caps.exe" "$stage/probe_native.dll" "$dest/"
echo "built $dest/caps.exe $dest/probe_native.dll"
