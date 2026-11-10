#!/bin/sh
# Checks this folder against Moono's attested builds (docs/RELEASES.md): each version's release
# manifest (oni-moono-*.SHA256SUMS) by its SLSA provenance and every file of its player against
# it, then this folder's own SHA256SUMS by its provenance (looked up, and the bundle in
# attestation/ that Moono's installer checks offline) and every file against it, and that no
# file is here that SHA256SUMS doesn't list. Needs the GitHub CLI (gh) and sha256sum.
#   sh verify.sh
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
repo=tigin-backwards/oni-moono
signer="$repo/.github/workflows/attest.yml"
cd "$here"
for version in . archived_versions/*; do
    [ -f "$version/mod_info.yaml" ] || continue
    for manifest in "$version"/oni-moono-*.SHA256SUMS; do
        [ -f "$manifest" ] || continue
        case "$manifest" in *-windows-x64.SHA256SUMS) platform=windows-x64 ;; *-linux-x64.SHA256SUMS) platform=linux-x64 ;; *) echo "unexpected $manifest" >&2; exit 1 ;; esac
        gh attestation verify "$manifest" -R "$repo" --signer-workflow "$signer" >/dev/null
        (cd "$version/$platform" && sha256sum --quiet --strict -c "$here/$manifest")
        echo "$version/$platform: as the attested $(basename "$manifest")"
    done
done
gh attestation verify SHA256SUMS -R "$repo" --signer-workflow "$signer" >/dev/null
[ "$(ls attestation)" = SHA256SUMS.sigstore.json ] || { echo "attestation/ should hold SHA256SUMS.sigstore.json alone" >&2; exit 1; }
gh attestation verify SHA256SUMS -R "$repo" --signer-workflow "$signer" --bundle attestation/SHA256SUMS.sigstore.json >/dev/null
sha256sum --quiet --strict -c SHA256SUMS
listed="$(cut -c67- SHA256SUMS | LC_ALL=C sort)"
present="$(find . -type f ! -path ./SHA256SUMS ! -path ./attestation/SHA256SUMS.sigstore.json | sed 's|^\./||' | LC_ALL=C sort)"
[ "$listed" = "$present" ] || { echo "files here that SHA256SUMS doesn't list, or the other way round" >&2; exit 1; }
echo "All $(wc -l < SHA256SUMS) files are as attested."
