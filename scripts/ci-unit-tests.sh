#!/usr/bin/env bash
# The unit tests as GitHub Actions runs them (build.yml), without the game: built against
# GAME_DIR, which may hold only the game's reference assemblies (scripts/fetch-game-refs.py).
# Every suite runs whole; a test that needs the game's data or its runnable assemblies is
# skipped without them (tests/Shared/GameInstall.cs, ONI_TESTS_NO_GAME), and the skips are
# listed by reason at the end.
#   scripts/ci-unit-tests.sh GAME_DIR
set -euo pipefail
source "$(dirname "$0")/env.sh"
game_dir="$1"
suites=(ModTriage.Tests OniMoono.Assets.Tests OniMoono.UnityEngine.Tests)
failed=()
reports=$(mktemp -d)
trap 'rm -rf "$reports"' EXIT
# ModOverlapsTests reads the built game tweaks.
dotnet build "$root/src/OniMoono.GameTweaks" -c Debug -p:OniGameDir="$game_dir" -v q -nologo "-clp:ErrorsOnly;NoSummary"
for suite in "${suites[@]}"; do
    dotnet build "$root/tests/$suite" -c Debug -p:OniGameDir="$game_dir" -v q -nologo "-clp:ErrorsOnly;NoSummary"
    echo "== $suite"
    ONI_TESTS_NO_GAME=1 ONI_GAME_DIR="$game_dir" "$root/out/bin/$suite/debug/$suite" -noColor -result-xml "$reports/$suite.xml" || failed+=("$suite")
done
python3 -I "$root/scripts/test-skips.py" "$reports"/*.xml || failed+=(skip-report)
[ ${#failed[@]} -eq 0 ] || { echo "failed: ${failed[*]}"; exit 1; }
