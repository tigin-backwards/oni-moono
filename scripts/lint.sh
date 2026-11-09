#!/usr/bin/env bash
# The linters, as scripts/check.sh and CI's lint job run them: each prints one line, and the
# script fails if any found something. A linter whose pinned binary is not here is skipped with
# a note (scripts/fetch-lint-tools.sh fetches them; this script never downloads).
#   scripts/lint.sh
set -uo pipefail
source "$(dirname "$0")/env.sh"
source "$root/scripts/lint-tools.sh"
cd "$root" || exit 1
failed=()
# run NAME CMD...: CMD's output only when it fails, and its time.
run() {
    local name=$1 out lines status=0 start=$SECONDS
    shift
    out=$("$@" 2>&1) || status=$?
    if [ $status -eq 0 ]; then
        echo "lint $name: ok ($((SECONDS - start)) s)"
    else
        echo "lint $name: failed ($((SECONDS - start)) s)"
        mapfile -t lines <<< "$out"
        printf '  %s\n' "${lines[@]:0:60}"
        failed+=("$name")
    fi
}
skip() { echo "lint $1 skipped: no $2 (scripts/fetch-lint-tools.sh)"; }

mapfile -t shell_files < <(git ls-files '*.sh')
if [ -x "$shellcheck_bin" ]; then
    run shellcheck "$shellcheck_bin" -f gcc "${shell_files[@]}"
else
    skip shellcheck "$shellcheck_bin"
fi

[ ${#failed[@]} -eq 0 ] || { echo "lint failed: ${failed[*]}"; exit 1; }
