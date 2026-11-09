#!/usr/bin/env bash
# Downloads the linters scripts/lint.sh runs, at pinned versions, into LINT_TOOLS (default
# ~/.cache/oni-moono/lint-tools/<tool>-<version>/), checking each archive's SHA-256 before it is
# unpacked. Run it once (and again after a version bump); lint.sh itself never downloads.
#   scripts/fetch-lint-tools.sh
set -euo pipefail
source "$(dirname "$0")/lint-tools.sh"
case "$(uname -m)" in
    x86_64) arch=x86_64 ;;
    aarch64 | arm64) arch=aarch64 ;;
    *) echo "fetch-lint-tools: no pinned linters for $(uname -m)" >&2; exit 1 ;;
esac
[ "$(uname -s)" = Linux ] || { echo "fetch-lint-tools: Linux only" >&2; exit 1; }

# fetch NAME URL SHA256 MEMBER: download URL, check it, unpack MEMBER into $lint_tools/NAME/.
fetch() {
    local name=$1 url=$2 sha=$3 member=$4 dest="$lint_tools/$1" tmp
    if [ -x "$dest/$(basename "$member")" ]; then echo "$name: present"; return; fi
    tmp=$(mktemp -d)
    curl -fsSL --retry 3 -o "$tmp/archive" "$url"
    echo "$sha  $tmp/archive" | sha256sum -c --quiet - || { rm -rf "$tmp"; echo "$name: checksum mismatch for $url" >&2; return 1; }
    mkdir -p "$tmp/x"
    case "$url" in
        *.tar.xz) tar -xJf "$tmp/archive" -C "$tmp/x" "$member" ;;
        *) tar -xzf "$tmp/archive" -C "$tmp/x" "$member" ;;
    esac
    mkdir -p "$dest"
    install -m 755 "$tmp/x/$member" "$dest/"
    rm -rf "$tmp"
    echo "$name: installed in $dest"
}

declare -A shellcheck_sha=(
    [x86_64]=8c3be12b05d5c177a04c29e3c78ce89ac86f1595681cab149b65b97c4e227198
    [aarch64]=12b331c1d2db6b9eb13cfca64306b1b157a86eb69db83023e261eaa7e7c14588
)
declare -A ruff_sha=(
    [x86_64]=7b2e6a6e132ecf7b5fe726163b186d5d209befe93bb25f83767b2d97fbdf14e5
    [aarch64]=468f29d64949574f0d28442c02f0b4fb680cff7ba8fc26b6287a73b945308e1c
)
declare -A actionlint_sha=(
    [x86_64]=8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8
    [aarch64]=325e971b6ba9bfa504672e29be93c24981eeb1c07576d730e9f7c8805afff0c6
)
declare -A go_arch=([x86_64]=amd64 [aarch64]=arm64)

fetch "shellcheck-$shellcheck_version" \
    "https://github.com/koalaman/shellcheck/releases/download/v$shellcheck_version/shellcheck-v$shellcheck_version.linux.$arch.tar.xz" \
    "${shellcheck_sha[$arch]}" "shellcheck-v$shellcheck_version/shellcheck"
fetch "ruff-$ruff_version" \
    "https://github.com/astral-sh/ruff/releases/download/$ruff_version/ruff-$arch-unknown-linux-gnu.tar.gz" \
    "${ruff_sha[$arch]}" "ruff-$arch-unknown-linux-gnu/ruff"
fetch "actionlint-$actionlint_version" \
    "https://github.com/rhysd/actionlint/releases/download/v$actionlint_version/actionlint_${actionlint_version}_linux_${go_arch[$arch]}.tar.gz" \
    "${actionlint_sha[$arch]}" actionlint
