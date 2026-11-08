#!/usr/bin/env bash
# check-tweaks.sh [--review] [GAME_DIR...]: each game tweak against frozen game builds, from their
# files, without starting the game (OniMoono --check-tweaks; src/OniMoono.Host/GameTweaks/TweakCheckTool.cs).
# Without GAME_DIRs, every build in $ONI_TWEAK_BUILDS (default /mnt/e/oni-builds, its
# oni-linux-* and oni-win-* folders). Plain: the builds of this branch's game update must keep
# every tweak on (signatures, references, reviewed fingerprints; on Linux builds also each
# install and its transpilers' rewrite counts), other updates' builds are skipped; exit 1 when a
# tweak would be off. --review: every build, each tweak that would be off and why, with the
# fingerprints to add once its changed bodies are reviewed (a newly downloaded build), and the
# bodies' normalised IL in out/tweak-review/BUILD/ for diffing two builds; exit 0.
set -euo pipefail
source "$(dirname "$0")/env.sh"
[ -n "${ONI_CONTAINED:-}" ] || exec "$root/scripts/contain.sh" "$0" "$@"
mode=check
if [ "${1:-}" = --review ]; then mode=review; shift; fi
host="$root/out/bin/OniMoono.Host/debug/OniMoono"
[ -x "$host" ] || { echo "check-tweaks: no $host (dotnet build first)"; exit 1; }
dirs=("$@")
if [ ${#dirs[@]} -eq 0 ]; then
    builds="${ONI_TWEAK_BUILDS:-/mnt/e/oni-builds}"
    for d in "$builds"/oni-linux-* "$builds"/oni-win-*; do
        [ -d "$d/OxygenNotIncluded_Data/Managed" ] && dirs+=("$d")
    done
    if [ ${#dirs[@]} -eq 0 ]; then
        echo "check-tweaks skipped: no frozen game builds in $builds (set ONI_TWEAK_BUILDS)"
        exit 0
    fi
fi
mkdir -p "$root/out/tweak-check"
failed=()
for d in "${dirs[@]}"; do
    name=$(basename "$d")
    extra=()
    [ $mode = review ] && extra=(--tweak-listings "$root/out/tweak-review")
    status=0
    out=$("$host" --game "$d" --check-tweaks $mode "${extra[@]}" --log "$root/out/tweak-check/$name.log" 2>&1) || status=$?
    grep -v '^\[engine\]' <<< "$out" || true
    [ $status -eq 0 ] || failed+=("$name")
done
[ ${#failed[@]} -eq 0 ] || { echo "check-tweaks failed: ${failed[*]}"; exit 1; }
