#!/bin/sh
# Checks a Workshop item folder against Moono's GitHub builds (docs/RELEASES.md). Run the copy
# from GitHub, not one inside the item: whoever controls the item can change that one.
#   gh api -H 'Accept: application/vnd.github.raw' repos/tigin-backwards/oni-moono/contents/ci/workshop/verify.sh > verify.sh
#   sh verify.sh ITEM-FOLDER
# 1. The release manifests (oni-moono-*.SHA256SUMS) by their SLSA provenance, from release.yml
#    on master (on u60 for an item laid out before the U60 cut-over), and every file of each
#    player against its manifest. The item has one version: an archived_versions folder is
#    refused.
# 2. The item's SHA256SUMS by its provenance from workshop.yml (looked up, and the bundle in
#    attestation/), every file against it, and that no file is here that SHA256SUMS doesn't list.
# Needs the GitHub CLI (gh) and sha256sum.
set -eu
item="$(cd "${1:-$(dirname "$0")}" && pwd)"
cd "$item"
repo=tigin-backwards/oni-moono
signer="$repo/.github/workflows/attest.yml"
fail() { echo "$*" >&2; exit 1; }

# attested FILE WORKFLOW BRANCH [gh options]: FILE's provenance by attest.yml, called by WORKFLOW
# on BRANCH of the repository.
attested() {
    file="$1"; workflow="$2"; branch="$3"; shift 3
    configs="$(gh attestation verify "$file" -R "$repo" --signer-workflow "$signer" --source-ref "refs/heads/$branch" "$@" \
        --format json --jq '.[].verificationResult.signature.certificate.buildConfigURI')" || return 1
    printf '%s\n' "$configs" | grep -qxF "https://github.com/$repo/.github/workflows/$workflow@refs/heads/$branch"
}
[ ! -e archived_versions ] || fail "archived_versions: the item has one version"
[ -f mod_info.yaml ] || fail "mod_info.yaml is missing"
for manifest in oni-moono-*.SHA256SUMS; do
    [ -f "$manifest" ] || fail "no release manifests"
    case "$manifest" in *-windows-x64.SHA256SUMS) platform=windows-x64 ;; *-linux-x64.SHA256SUMS) platform=linux-x64 ;; *) fail "unexpected $manifest" ;; esac
    branch=""
    for b in master u60; do
        if attested "$manifest" release.yml "$b" >/dev/null 2>&1; then branch=$b; break; fi
    done
    [ -n "$branch" ] || fail "$manifest: not attested as a release.yml build of master"
    (cd "$platform" && sha256sum --quiet --strict -c "$item/$manifest")
    echo "$platform: as the attested $manifest ($branch)"
done
[ "$(ls attestation)" = SHA256SUMS.sigstore.json ] || fail "attestation/ should hold SHA256SUMS.sigstore.json alone"
item_branch=""
for b in master u60; do
    if attested SHA256SUMS workshop.yml "$b" >/dev/null 2>&1 && attested SHA256SUMS workshop.yml "$b" --bundle attestation/SHA256SUMS.sigstore.json >/dev/null 2>&1; then item_branch=$b; break; fi
done
[ -n "$item_branch" ] || fail "SHA256SUMS: not attested by workshop.yml on master or u60"
echo "SHA256SUMS: attested by workshop.yml on $item_branch"
sha256sum --quiet --strict -c SHA256SUMS
listed="$(cut -c67- SHA256SUMS | LC_ALL=C sort)"
present="$(find . -type f ! -path ./SHA256SUMS ! -path ./attestation/SHA256SUMS.sigstore.json | sed 's|^\./||' | LC_ALL=C sort)"
[ "$listed" = "$present" ] || fail "files here that SHA256SUMS doesn't list, or the other way round"
echo "All $(wc -l < SHA256SUMS) files are as attested."
