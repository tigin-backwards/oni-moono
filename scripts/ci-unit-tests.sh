#!/usr/bin/env bash
# The unit tests that need no game install, as GitHub Actions runs them (build.yml): each suite
# but the classes ci/tests-need-game.txt lists, which need the game's data or a display.
# Builds them against GAME_DIR, which may hold only the game's reference assemblies
# (scripts/fetch-game-refs.py).
#   scripts/ci-unit-tests.sh GAME_DIR
set -euo pipefail
source "$(dirname "$0")/env.sh"
game_dir="$1"
suites=(ModTriage.Tests OniMoono.Assets.Tests OniMoono.UnityEngine.Tests)
failed=()
# ModOverlapsTests reads the built game tweaks.
dotnet build "$root/src/OniMoono.GameTweaks" -c Debug -p:OniGameDir="$game_dir" -v q -nologo "-clp:ErrorsOnly;NoSummary"
for suite in "${suites[@]}"; do
    dotnet build "$root/tests/$suite" -c Debug -p:OniGameDir="$game_dir" -v q -nologo "-clp:ErrorsOnly;NoSummary"
    skip=()
    while read -r s class; do [ "$s" = "$suite" ] && skip+=(-noclass "$class"); done < <(grep -v '^#' "$root/ci/tests-need-game.txt")
    echo "== $suite ($(( ${#skip[@]} / 2 )) classes left out)"
    ONI_GAME_DIR="$game_dir" "$root/out/bin/$suite/debug/$suite" -noColor "${skip[@]}" || failed+=("$suite")
done
[ ${#failed[@]} -eq 0 ] || { echo "failed: ${failed[*]}"; exit 1; }
