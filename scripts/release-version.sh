#!/usr/bin/env bash
# The version a release's archives are named by (scripts/release.sh VERSION): RELEASE_DATE
# (YYYY.MM.DD, the release tag's date, which release.yml passes) when it is set, else the date
# of HEAD's commit; with -u60 for a build of the u60 branch (GITHUB_REF_NAME in GitHub Actions,
# else the checked-out branch), as oni-moono-2026.11.06-u60-windows-x64.zip.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
branch="${GITHUB_REF_NAME:-$(git -C "$root" rev-parse --abbrev-ref HEAD)}"
if [ -n "${RELEASE_DATE:-}" ]; then
    [[ "$RELEASE_DATE" =~ ^[0-9]{4}\.[0-9]{2}\.[0-9]{2}$ ]] || { echo "release-version: RELEASE_DATE must be YYYY.MM.DD, not '$RELEASE_DATE'" >&2; exit 1; }
    version="$RELEASE_DATE"
else
    version="$(git -C "$root" log -1 --format=%cd --date=format:%Y.%m.%d)"
fi
[ "$branch" != u60 ] || version="$version-u60"
echo "$version"
