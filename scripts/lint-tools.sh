# The pinned linter versions and where scripts/fetch-lint-tools.sh puts them (sourced).
# shellcheck shell=bash disable=SC2034  # read by the scripts that source it
shellcheck_version=0.11.0
ruff_version=0.17.2
actionlint_version=1.7.12
lint_tools="${LINT_TOOLS:-$HOME/.cache/oni-moono/lint-tools}"
shellcheck_bin="$lint_tools/shellcheck-$shellcheck_version/shellcheck"
ruff_bin="$lint_tools/ruff-$ruff_version/ruff"
actionlint_bin="$lint_tools/actionlint-$actionlint_version/actionlint"
