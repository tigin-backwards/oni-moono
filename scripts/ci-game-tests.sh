#!/usr/bin/env bash
# The game tests as GitHub Actions runs them (build.yml's game job, docs/DEVELOPMENT.md "Game
# tests in CI"), on the stripped game (tools/GameStrip) in no-content mode:
#   scripts/ci-game-tests.sh push|nightly GAME_DIR
# push:    the game tweaks against the game's assemblies (check-tweaks), the unit tests that need
#          the game (the recorded-parity probes among them) and ci/game-tests/push.txt's specs;
# nightly: ci/game-tests/nightly.txt's specs.
# A run checks every ci-verify* spec's log with `qa-tools.py verify` (zero wrong answers, and a
# report from each check ci/game-tests/verify/SPEC.txt names), and
# every spec's log signatures against the run's budget (ci/game-tests/budget/BUILD.txt): a run of
# ours fails on an error or exception signature the budget lacks.
# Needs: GAME_DIR (scripts/fetch-game-content.py), its fixtures in tests/e2e/fixtures, QA_U57_STEAM
# (the DLC-owning Steam double, tools/SteamStub/build.sh --dlc) and E2E_ONIMCP (scripts/onimcp.py).
# The game, saves and logs are private: this prints verdicts and short report lines only, never a
# log or a dump, and leaves everything in out/ (CI uploads none of it).
set -euo pipefail
source "$(dirname "$0")/env.sh"
list="${1:?push or nightly}"
game="$(cd "${2:?GAME_DIR}" && pwd)"
[ -f "$root/ci/game-tests/$list.txt" ] || { echo "ci-game-tests: no ci/game-tests/$list.txt"; exit 2; }
build="$(python3 -I "$root/scripts/fetch-game-content.py" --print-default-build)"
export ONI_NO_CONTENT=1 ONI_CONFIG=release ONI_GAME_DIR="$game" QA_U57_GAME="$game"
failed=()
# CI_BUILD_WRAPPER: a command the builds run under, such as a local build lock (CI sets none).
dotnet_build() { ${CI_BUILD_WRAPPER:-} dotnet build "$@" -v q -nologo "-clp:ErrorsOnly;NoSummary"; }

echo "== build"
dotnet_build "$root/src/OniMoono.Host" -c Release -p:OniGameDir="$game"
# E2eDriver and our test mods (those this branch has).
for tool in E2eDriver OverlapTestMod SelectCycleTestMod CommandLineTestMod ReachStressTestMod GeoVentProbeTestMod; do
    [ -d "$root/tools/$tool" ] || continue
    dotnet_build "$root/tools/$tool" -c Release -p:OniGameDir="$game"
done

if [ "$list" = push ]; then
    echo "== check-tweaks"
    "$root/scripts/check-tweaks.sh" "$game" || failed+=(check-tweaks)

    # The tests that need the game's data and runnable assemblies, which the no-game unit test
    # job skips: run here against the stripped game, every other test of the suites with them.
    echo "== unit tests with the game"
    reports=$(mktemp -d)
    for suite in OniMoono.Assets.Tests OniMoono.UnityEngine.Tests; do
        dotnet_build "$root/tests/$suite" -c Debug -p:OniGameDir="$game"
        "$root/out/bin/$suite/debug/$suite" -noColor -result-xml "$reports/$suite.xml" > "$reports/$suite.txt" 2>&1 || failed+=("$suite")
        grep -E '^\s+(\S+ +Total:|\[FAIL\])' "$reports/$suite.txt" | head -40 || true
    done
    python3 -I "$root/scripts/test-skips.py" --fail-on-game "$reports"/*.xml || failed+=(game-test-skips)
    rm -rf "$reports"
fi

echo "== e2e ($list)"
# References for ours-alone runs: each spec's log-signature budget, the build's (e2e.py compares a
# run of ours with out/e2e-refs/SPEC/signatures.json). No UI dump or screenshot reference: the
# specs check their own expectations, and E2E_NO_SHOTS takes no picture.
export E2E_OUT="$root/out/e2e" E2E_NO_SHOTS=1 E2E_MEMORY_GB="${E2E_MEMORY_GB:-12}" E2E_DRIVER="$root/out/bin/E2eDriver/release"
refs="$E2E_OUT-refs"
rm -rf "$refs"
budget="$root/ci/game-tests/budget/$build.txt"
while read -r spec _; do
    mkdir -p "$refs/$spec"
    python3 -I -c 'import json, sys; json.dump([l.rstrip("\n") for l in open(sys.argv[1]) if l.strip() and not l.startswith("#")], open(sys.argv[2], "w"), indent=1)' \
        "$budget" "$refs/$spec/signatures.json"
done < <(grep -vE '^\s*(#|$)' "$root/ci/game-tests/$list.txt")
status=0
python3 -I "$root/scripts/e2e.py" --player ours --lanes "${CI_E2E_LANES:-2}" --no-perf-wait --run-id "ci-$list" --list "$root/ci/game-tests/$list.txt" || status=$?
[ $status -eq 0 ] || failed+=(e2e)

# The verify specs' reports: every check with zero wrong answers, and every check that
# ci/game-tests/verify/SPEC.txt names reported (silence is not zero wrong).
while read -r spec _; do
    case "$spec" in ci-verify*) ;; *) continue ;; esac
    echo "== verify: $spec"
    log="$E2E_OUT/$spec/ci-$list/ours-1/stdout.txt"
    if [ ! -f "$log" ]; then echo "FAIL $spec: no log"; failed+=(verify); continue; fi
    python3 -I "$root/scripts/qa-tools.py" verify "$log" --expect "$root/ci/game-tests/verify/$spec.txt" || failed+=("verify")
done < <(grep -vE '^\s*(#|$)' "$root/ci/game-tests/$list.txt")

[ ${#failed[@]} -eq 0 ] || { echo "ci-game-tests $list failed: ${failed[*]}"; exit 1; }
echo "ci-game-tests $list: all passed"
