#!/usr/bin/env bash
# Builds the release archives into out/release (from WSL: the Windows player's OniMoono.exe is
# built with MSVC, scripts/build-native-host.sh): the self-contained player for Windows and for
# Linux, each with its setup script, the README and BRUTAL's NOTICE.txt, under one top folder
# named oni-moono (extracting a newer release over an older one keeps the paths the same).
#   MOONO_RELEASE=vYYYY.MM.DD.NN scripts/release.sh [VERSION]
#                                     (VERSION defaults to the date of HEAD's commit)
# MOONO_PLATFORMS="windows linux" (the default) picks the archives: GitHub Actions
# (.github/workflows/build.yml, docs/RELEASES.md) builds each on its own runner, the Windows one
# natively there. MOONO_RELEASE=none builds a development player (it checks for no updates).
# MOONO_RELEASE is the tag the release will have: the player compares it with the releases on
# GitHub for the main menu's update badge (src/OniMoono.Host/UpdateCheck.cs), so a release
# build without it (or with another form) fails here. Development builds (dotnet build,
# scripts/run.sh) need none: they check for no updates.
# The build compiles against the game's assemblies, so it runs where the game is installed
# (ONI_GAME_DIR); nothing of the game goes into the archives.
set -euo pipefail
tag="${MOONO_RELEASE:-}"
if [ "$tag" != none ] && ! [[ "$tag" =~ ^v[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}$ ]]; then
    echo "MOONO_RELEASE must be the tag this release will get, like v2026.11.03.01 (got '${tag}'):" >&2
    echo "the player compares it with GitHub's releases for the main menu's update badge" >&2
    exit 1
fi
source "$(dirname "$0")/env.sh"
platforms="${MOONO_PLATFORMS:-windows linux}"
release_prop=(); [ "$tag" = none ] || release_prop=(-p:MoonoRelease="$tag")
# python3, or on a Windows runner python.
py=python3; python3 -c "" 2>/dev/null || py=python
version="${1:-$(git -C "$root" log -1 --format=%cd --date=format:%Y.%m.%d)}"
out="$root/out/release"
rm -rf "$out"; mkdir -p "$out"
# Each build starts from an empty output folder, so nothing stale (the host from before it was
# named OniMoono, OniBrutal.exe and its OniBrutal.dll) goes into an archive.
build() { rm -rf "$root/out/bin/$(basename "$1")/release_$2"; dotnet build "$1" -c Release -r "$2" --self-contained true -p:OniGameDir="$game" "${release_prop[@]}" -v q -nologo "-clp:ErrorsOnly;NoSummary"; }

stage() { # stage DIR SETUP: the archive's oni-moono folder
    mkdir -p "$1/player"
    cp "$root/$2" "$root/README.md" "$root/NOTICE.txt" "$1/"
}

# manifest DIR FILE: FILE lists the SHA-256 of every file under DIR (the archive's oni-moono
# folder), with paths relative to it and /, sorted, in sha256sum's format: release.yml signs
# provenance for each file in it (docs/RELEASES.md), and it is a release asset of its own.
manifest() {
    (cd "$1" && $py -I -c "
import hashlib, os, sys
paths = sorted(os.path.relpath(os.path.join(d, f)).replace(os.sep, '/') for d, _, files in os.walk('.') for f in files)
with open(sys.argv[1], 'wb') as out:
    for p in paths:
        with open(p, 'rb') as f: out.write(('%s  %s\\n' % (hashlib.sha256(f.read()).hexdigest(), p)).encode())
" "$2")
}

# Windows: OniMoono.exe with the Restarter.exe the game's App.Restart starts.
if [[ " $platforms " == *" windows "* ]]; then
build "$root/src/OniMoono.Host" win-x64
build "$root/src/OniMoono.Restarter" win-x64
win="$out/windows/oni-moono"
stage "$win" setup.ps1
cp "$root/setup.cmd" "$win/"
cp -r "$root/out/bin/OniMoono.Host/release_win-x64/." "$win/player/"
rm -f "$win/player/Restarter" "$win/player/"FacadeGen*
cp "$root/out/bin/OniMoono.Restarter/release_win-x64/Restarter."{exe,dll,deps.json,runtimeconfig.json} "$win/player/"
# OniMoono.exe: our native host, which exports the GPU hints (ARCHITECTURE §2), in place of
# the SDK's apphost; built with MSVC on the Windows side (from WSL, or on a Windows runner).
"$root/scripts/build-native-host.sh" "$win/player/OniMoono.exe"
check=$($py -I "$root/scripts/check-windows-host.py" "$win/player/OniMoono.exe") || { echo "$check"; exit 1; }
[ -f "$win/player/translations/zh.po" ] || { echo "$win/player/translations/zh.po missing"; exit 1; }
manifest "$win" "$out/oni-moono-$version-windows-x64.SHA256SUMS"
# zip for Windows (the archive's paths with /, as from WSL); tar.gz for Linux, which keeps the
# executable bits.
(cd "$out/windows" && $py -I -c "
import os, sys, zipfile
with zipfile.ZipFile(sys.argv[1], 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for d, _, files in os.walk('oni-moono'):
        for f in files: z.write(os.path.join(d, f), os.path.join(d, f).replace(os.sep, '/'))
" "$out/oni-moono-$version-windows-x64.zip")
fi

# Linux: OniMoono with the Restarter script, and the launcher.
if [[ " $platforms " == *" linux "* ]]; then
build "$root/src/OniMoono.Host" linux-x64
lin="$out/linux/oni-moono"
stage "$lin" setup.sh
# The launcher to double-click (docs/INSTALL-MODES.md, Linux launcher), and the icon of
# setup's menu entry.
cp "$root/oni-moono.desktop" "$lin/"
cp -r "$root/out/bin/OniMoono.Host/release_linux-x64/." "$lin/player/"
cp "$root/src/OniMoono.Desktop/Moo.png" "$lin/player/"
rm -f "$lin/player/"FacadeGen*
chmod +x "$lin/setup.sh" "$lin/oni-moono.desktop" "$lin/player/OniMoono" "$lin/player/Restarter"
# The Moono Tweaks screen's translations (translations/*.po from the build).
[ -f "$lin/player/translations/zh.po" ] || { echo "$lin/player/translations/zh.po missing"; exit 1; }
manifest "$lin" "$out/oni-moono-$version-linux-x64.SHA256SUMS"
tar -C "$out/linux" -czf "$out/oni-moono-$version-linux-x64.tar.gz" oni-moono
$py -I "$root/scripts/check-linux-archive.py" "$out/oni-moono-$version-linux-x64.tar.gz"
fi
ls -la "$out"/oni-moono-*
