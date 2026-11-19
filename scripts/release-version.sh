#!/usr/bin/env bash
# The version a release's archives are named by (scripts/release.sh VERSION): RELEASE_DATE
# (YYYY.MM.DD, the release tag's date, which release.yml passes) when it is set, else the date
# of HEAD's commit, as oni-moono-2026.11.06-windows-x64.zip. (The U60 beta's builds, from the u60
# branch before the U60 cut-over, had -u60 after the date.)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
if [ -n "${RELEASE_DATE:-}" ]; then
    [[ "$RELEASE_DATE" =~ ^[0-9]{4}\.[0-9]{2}\.[0-9]{2}$ ]] || { echo "release-version: RELEASE_DATE must be YYYY.MM.DD, not '$RELEASE_DATE'" >&2; exit 1; }
    version="$RELEASE_DATE"
else
    version="$(git -C "$root" log -1 --format=%cd --date=format:%Y.%m.%d)"
fi
echo "$version"
