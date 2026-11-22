#!/usr/bin/env bash
# The check of OniMoono.Desktop's COM, struct, enum and DLL declarations (src/OniMoono.Desktop/D3d11/,
# Gl/Dxgi.cs) against the Windows SDK headers: tools/ComCheckGen writes com_check.cpp from them,
# and MSVC compiles it (src/OniMoono.NativeHost/com_check.cmd) in %TEMP% from WSL, or natively in
# Git Bash on Windows. Any difference from the headers fails the compile.
#   scripts/com-check.sh              generate and compile (generate only, with a note, when
#                                     there is no Windows side)
#   scripts/com-check.sh --emit OUT   generate OUT only (scripts/build-native-host.sh compiles it)
set -euo pipefail
source "$(dirname "$0")/env.sh"
generate() { dotnet run --project "$root/tools/ComCheckGen" -c Release -v q -- "$1"; }
if [ "${1:-}" = --emit ]; then generate "$2"; exit; fi
case "$(uname -s)" in MINGW*|MSYS*) native=1 ;; *) native= ;; esac
if [ -z "$native" ] && ! command -v cmd.exe > /dev/null; then
    out="$(mktemp)"; trap 'rm -f "$out"' EXIT
    generate "$out"
    echo "com-check: generated; not compiled (no Windows side for MSVC)"
    exit 0
fi
if [ -n "$native" ]; then
    stage="$(cygpath -u "$TEMP")/oni-moono-comcheck-$$"
else
    win_temp="$(cd /mnt/c && cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
    stage="$(wslpath "$win_temp")/oni-moono-comcheck-$$"
fi
rm -rf "$stage"; mkdir -p "$stage"
trap 'rm -rf "$stage"' EXIT
generate "$stage/com_check.cpp"
cp "$root/src/OniMoono.NativeHost/com_check.cmd" "$stage/"
status=0
log=$(cd "$stage" && MSYS2_ARG_CONV_EXCL='*' cmd.exe /c com_check.cmd 2>&1 | tr -d '\r') || status=$?
# cmd.exe's exit code does not come through the pipe from every shell: the success line decides.
if [ $status -ne 0 ] || ! grep -q '^com_check.cpp: every declaration matches' <<< "$log"; then
    grep -E 'error|warning|com_check.cmd' <<< "$log" | head -40
    echo "com-check: MSVC found declarations that differ from the Windows SDK headers" >&2
    exit 1
fi
echo "com-check: ${log##*com_check.cpp: }"
