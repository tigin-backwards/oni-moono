#!/usr/bin/env bash
# The version a release's archives are named by (scripts/release.sh VERSION): the release's tag
# without its v (MOONO_RELEASE=vYYYY.MM.DD.NN, which build.yml passes), as
# oni-moono-2026.11.20.04-windows-x64.zip, so two releases of one day have different names.
# Without a tag (MOONO_RELEASE unset or none: a development build), the date of HEAD's commit.
# (Releases up to v2026.11.20.04 were named by the tag's date alone, oni-moono-2026.11.06-...;
# the U60 beta's builds, from the u60 branch before the U60 cut-over, had -u60 after the date.)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
tag="${MOONO_RELEASE:-}"
if [[ "$tag" =~ ^v([0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2})$ ]]; then
    version="${BASH_REMATCH[1]}"
elif [ -z "$tag" ] || [ "$tag" = none ]; then
    version="$(git -C "$root" log -1 --format=%cd --date=format:%Y.%m.%d)"
else
    echo "release-version: MOONO_RELEASE must be vYYYY.MM.DD.NN or none, not '$tag'" >&2; exit 1
fi
echo "$version"
