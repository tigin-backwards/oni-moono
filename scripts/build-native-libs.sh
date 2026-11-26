#!/usr/bin/env bash
# Builds the Windows player's vma.dll and shaderc_shared.dll from source with the static C
# runtime (src/OniMoono.NativeLibs), and puts them in DEST over the ones BRUTAL's packages
# ship, which import MSVCP140.dll: built with MSVC 14.41, their std::mutex faults under an
# MSVCP140.dll older than 14.40 (#126). DEST's SPIRV-Tools-shared.dll goes: the player never
# loads it (shaderc_shared.dll has SPIRV-Tools linked in).
#   scripts/build-native-libs.sh DEST
# From WSL or from Git Bash on Windows (a GitHub Actions Windows runner), as
# scripts/build-native-host.sh: the sources (src/OniMoono.NativeLibs/sources.sh, each archive
# checked against its SHA-256, kept in ONI_NATIVE_SOURCES, default
# ~/.cache/oni-moono/native-sources) are unpacked in %TEMP% and built there by build.cmd with
# the Visual Studio C++ build tools. The DLLs are kept in out/native-libs/KEY (KEY: a hash of
# the sources' pins and the build files), so the next build copies them.
# NATIVE_LIBS_JOBS=N: Ninja's parallel jobs (default: Ninja's).
set -euo pipefail
source "$(dirname "$0")/env.sh"
dest="$1"
dir="$root/src/OniMoono.NativeLibs"
source "$dir/sources.sh"
case "$(uname -s)" in MINGW*|MSYS*) native=1 ;; *) native= ;; esac
key="$(cat "$dir/sources.sh" "$dir/vma.cpp" "$dir/build.cmd" "$0" | sha256sum | cut -c1-16)"
built="$root/out/native-libs/$key"
libs=(vma.dll shaderc_shared.dll)
if ! [ -f "$built/vma.dll" ] || ! [ -f "$built/shaderc_shared.dll" ]; then
    [ -n "$native" ] || command -v cmd.exe >/dev/null || { echo "build-native-libs: needs Windows (WSL's cmd.exe, or Git Bash) and its Visual Studio C++ build tools" >&2; exit 3; }
    sources="${ONI_NATIVE_SOURCES:-$HOME/.cache/oni-moono/native-sources}"
    mkdir -p "$sources"
    if [ -n "$native" ]; then
        stage="$(cygpath -u "$TEMP")/moono-nl-$$"
    else
        win_temp="$(cd /mnt/c && cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
        stage="$(wslpath "$win_temp")/moono-nl-$$"
    fi
    rm -rf "$stage"; mkdir -p "$stage"
    trap 'rm -rf "$stage"' EXIT
    for entry in "${native_sources[@]}"; do
        read -r name repo commit sha <<< "$entry"
        archive="$sources/$name-$commit.tar.gz"
        if ! [ -f "$archive" ] || ! echo "$sha  $archive" | sha256sum -c --quiet - 2>/dev/null; then
            curl -fsSL --retry 3 -o "$archive.part" "https://codeload.github.com/$repo/tar.gz/$commit"
            echo "$sha  $archive.part" | sha256sum -c --quiet - || { rm -f "$archive.part"; echo "build-native-libs: $name: checksum mismatch for $repo@$commit" >&2; exit 1; }
            mv "$archive.part" "$archive"
        fi
        case "$name" in
            glslang|spirv-tools|spirv-headers) to="$stage/shaderc/third_party/$name" ;;
            *) to="$stage/$name" ;;
        esac
        mkdir -p "$to"
        tar -xzf "$archive" -C "$to" --strip-components=1
    done
    cp "$dir/vma.cpp" "$dir/build.cmd" "$stage/"
    # (MSYS2_ARG_CONV_EXCL: Git Bash passes /c to cmd.exe as it is.)
    (cd "$stage" && MSYS2_ARG_CONV_EXCL='*' cmd.exe /c build.cmd "${NATIVE_LIBS_JOBS:-0}" 2>&1 | tr -d '\r') || true
    for lib in "${libs[@]}"; do
        [ -f "$stage/out/$lib" ] || { echo "build-native-libs: the build failed ($lib missing)" >&2; exit 1; }
    done
    mkdir -p "$built.part"
    for lib in "${libs[@]}"; do cp "$stage/out/$lib" "$built.part/"; done
    rm -rf "$built"; mv "$built.part" "$built"
fi
for lib in "${libs[@]}"; do cp "$built/$lib" "$dest/"; done
rm -f "$dest/SPIRV-Tools-shared.dll"
