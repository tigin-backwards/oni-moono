#!/usr/bin/env bash
# The steps .github/workflows/workshop-upload.yml shares between its dry run and its upload
# (docs/WORKSHOP-UPLOAD.md). Run in Actions, with GH_TOKEN and GH_REPO set.
#   workshop-upload.sh prepare RUN DIR MASTER ITEM VISIBILITY PREVIEW
#     DIR/item: the workshop-item artifact of RUN, a successful workshop.yml run on master or
#     u60, checked by ci/workshop/verify.sh and tied to RUN by its attestation; DIR/item.vdf:
#     its VDF, with MASTER's ci/workshop/description.bbcode (MASTER: a checkout of master),
#     the change note from the releases' notes, and MASTER's ci/workshop/preview.gif when
#     PREVIEW is true. ITEM is the published file ID, or new.
#   workshop-upload.sh steamcmd ROOT
#     Valve's steamcmd in ROOT/install, its home (and so its login) in ROOT/home: the bootstrap
#     archive checked against its pinned digest, then updated by steamcmd itself from Valve.
#   workshop-upload.sh upload ROOT DIR
#     Signs in to Steam with ROOT's steamcmd and uploads DIR/item.vdf. From the environment:
#     STEAM_USERNAME, and either STEAM_CONFIG_VDF (base64 of a config.vdf with a cached login,
#     used with no password and no prompt), or STEAM_PASSWORD with a Steam Guard code from
#     STEAM_SHARED_SECRET, or STEAM_PASSWORD alone (the phone confirms the sign-in).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
fail() { echo "::error::$*" >&2; exit 1; }

# Valve's Linux bootstrap; unchanged since 2018-01-05 (Last-Modified). A new one is checked by
# hand and its digest put here.
STEAMCMD_URL=https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz
STEAMCMD_SHA256=cebf0046bfd08cf45da6bc094ae47aa39ebf4155e5ede41373b579b8f1071e7c

prepare() {
    local run=$1 dir=$2 master=$3 item=$4 visibility=$5 preview=$6 info branch uris tag
    [[ "$run" =~ ^[0-9]+$ ]] || fail "a run is its number, not '$run'"
    info="$(gh api "repos/$GH_REPO/actions/runs/$run" \
        --jq '"\(.path) \(.head_branch) \(.conclusion) \(.event) \(.head_repository.full_name)"')"
    branch="$(cut -d' ' -f2 <<< "$info")"
    [ "$info" = ".github/workflows/workshop.yml $branch success workflow_dispatch $GH_REPO" ] && [[ "$branch" =~ ^(master|u60)$ ]] \
        || fail "run $run is not a successful workshop.yml run on master or u60 ($info)"
    mkdir -p "$dir"
    gh run download "$run" -n workshop-item -D "$dir/item"
    # Every file as attested, nothing else, the bundle from workshop.yml on master or u60.
    sh "$root/ci/workshop/verify.sh" "$dir/item"
    # And that bundle is this run's: its certificate names the run that signed it.
    uris="$(gh attestation verify "$dir/item/SHA256SUMS" --bundle "$dir/item/attestation/SHA256SUMS.sigstore.json" \
        -R "$GH_REPO" --signer-workflow "$GH_REPO/.github/workflows/attest.yml" --source-ref "refs/heads/$branch" \
        --format json --jq '.[].verificationResult.signature.certificate | "\(.buildConfigURI) \(.runInvocationURI)"')"
    grep -qE "^https://github.com/$GH_REPO/.github/workflows/workshop.yml@refs/heads/$branch https://github.com/$GH_REPO/actions/runs/$run/attempts/[0-9]+$" <<< "$uris" \
        || fail "the item's attestation is not run $run's: $uris"
    echo "run $run ($branch): the item is as attested"
    # The change note: the notes of each version's release, read from its attested moono.json.
    mkdir -p "$dir/notes"
    for tag in $(python3 -I -c 'import json, sys; print(*(json.load(open(p))["tag"] for p in sys.argv[1:]))' \
                 "$dir/item/moono.json" "$dir"/item/archived_versions/*/moono.json); do
        [[ "$tag" =~ ^v[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}$ ]] || fail "unexpected tag '$tag' in the item"
        gh release view "$tag" -R "$GH_REPO" --json tagName,name,url,body,isDraft > "$dir/notes/$tag.json" || rm -f "$dir/notes/$tag.json"
    done
    python3 -I "$here/workshop-upload.py" notes "$dir/item" "$dir/notes" "$dir/changenote.bbcode"
    local args=(--item "$item" --description "$master/ci/workshop/description.bbcode" --content "$dir/item"
                --changenote "$dir/changenote.bbcode" --visibility "$visibility")
    [ "$preview" != true ] || args+=(--preview "$master/ci/workshop/preview.gif")
    [ "$item" != new ] || args+=(--title "${WORKSHOP_TITLE:-Moono}")
    python3 -I "$here/workshop-upload.py" vdf "${args[@]}" "$dir/item.vdf"
    # The VDF up to its description, which is master's file as it is.
    sed '/"description"/,$d' "$dir/item.vdf"
    echo "change note:"; cat "$dir/changenote.bbcode"
}

install_steamcmd() {
    local dir=$1 attempt
    mkdir -p "$dir/install" "$dir/home"
    curl -fsSL --proto '=https' --tlsv1.2 -o "$dir/steamcmd_linux.tar.gz" "$STEAMCMD_URL"
    echo "$STEAMCMD_SHA256  $dir/steamcmd_linux.tar.gz" | sha256sum --strict --quiet -c \
        || fail "steamcmd_linux.tar.gz is not the pinned bootstrap"
    tar -xzf "$dir/steamcmd_linux.tar.gz" -C "$dir/install"
    rm "$dir/steamcmd_linux.tar.gz"
    # Its first start updates it; a second confirms the updated one starts.
    for attempt in 1 2; do
        HOME="$dir/home" "$dir/install/steamcmd.sh" +quit && return 0
        echo "steamcmd's update run $attempt failed"
    done
    fail "steamcmd did not update"
}

REPRIME="Prime a new session: docs/WORKSHOP-UPLOAD.md, 'Priming the cached session'"

# run_steamcmd ROOT SCRIPT LOG [SECONDS]: steamcmd runs SCRIPT (deleted once it exits) with no
# input, its output (ANSI colours removed) in LOG; within SECONDS if given. Its status.
run_steamcmd() {
    local steam=$1 script=$2 log=$3 limit=${4:-} status=0
    local cmd=(env HOME="$steam/home" "$steam/install/steamcmd.sh" +runscript "$script")
    [ -z "$limit" ] || cmd=(timeout "$limit" "${cmd[@]}")
    "${cmd[@]}" < /dev/null 2>&1 | tee "$log.raw" || status=$?
    rm -f "$script"
    sed 's/\x1b\[[0-9;]*m//g' "$log.raw" > "$log"
    rm -f "$log.raw"
    return $status
}

uploaded() { grep -q 'Committing update\.\.\.Success\.' "$1"; }

# steamcmd's warning when the cached login's token nears its end, as a warning of the run's.
token_expiry() {
    local when left
    when="$(grep -oE 'Login token expires at .*' "$1" | head -1 | cut -d' ' -f5- || true)"
    [ -n "$when" ] || return 0
    left=$(( ($(date -d "$when" +%s 2>/dev/null || echo 0) - $(date +%s)) / 86400 ))
    echo "::warning title=Steam session::The cached Steam session expires on $when ($left days). $REPRIME."
}

upload() {
    local steam=$1 dir=$2 config="$1/home/Steam/config/config.vdf" status deadline attempt line
    if [ -n "${STEAM_CONFIG_VDF:-}" ]; then
        # The cached login, never printed, deleted with steamcmd's home at the end of the job.
        mkdir -p "$(dirname "$config")"
        (umask 077; base64 -d <<< "$STEAM_CONFIG_VDF" > "$config") 2> /dev/null || fail "STEAM_CONFIG_VDF is not base64. $REPRIME."
        chmod 600 "$config"
        grep -q '"ConnectCache"' "$config" || fail "STEAM_CONFIG_VDF holds no cached login (no ConnectCache). $REPRIME."
        # A sign-in alone first, within 3 minutes: an expired session fails here, never waits.
        python3 -I "$here/workshop-upload.py" login --cached --check "$dir/item.vdf" "$dir/login.txt"
        status=0
        run_steamcmd "$steam" "$dir/login.txt" "$dir/check.log" 180 || status=$?
        if [ $status -ne 0 ] || ! grep -q 'Waiting for user info\.\.\.OK' "$dir/check.log"; then
            line="$(grep -m1 -iE 'FAILED|Invalid Password|Login Failure|License expired|password:|cached credentials not found|Steam Guard|confirm the login' "$dir/check.log" || true)"
            [ $status -ne 124 ] || line="no sign-in within 3 minutes"
            fail "The cached Steam session in STEAM_CONFIG_VDF has expired or was revoked (steamcmd: ${line:-exit $status}). $REPRIME."
        fi
        token_expiry "$dir/check.log"
        python3 -I "$here/workshop-upload.py" login --cached "$dir/item.vdf" "$dir/login.txt"
        status=0
        run_steamcmd "$steam" "$dir/login.txt" "$dir/steamcmd.log" || status=$?
        if [ $status -ne 0 ] || ! uploaded "$dir/steamcmd.log"; then fail "steamcmd did not upload the item (exit $status)"; fi
        return 0
    fi
    # The password: a Steam Guard code from STEAM_SHARED_SECRET, or the phone confirms. steamcmd
    # gives up on a confirmation after about a minute, so it signs in again while 5 minutes last.
    deadline=$((SECONDS + 300))
    for attempt in 1 2 3 4 5 6; do
        python3 -I "$here/workshop-upload.py" login "$dir/item.vdf" "$dir/login.txt"
        [ -n "${STEAM_SHARED_SECRET:-}" ] || echo "::notice title=Steam sign-in::Approve the sign-in on your phone: Steam Mobile app, Steam Guard tab (attempt $attempt, until $(date -u -d "+$((deadline - SECONDS)) seconds" +%H:%M:%S) UTC)."
        status=0
        run_steamcmd "$steam" "$dir/login.txt" "$dir/steamcmd.log" || status=$?
        if [ $status -eq 0 ] && uploaded "$dir/steamcmd.log"; then return 0; fi
        if [ -z "${STEAM_SHARED_SECRET:-}" ] && [ "$SECONDS" -lt "$deadline" ] \
           && grep -qiE 'timed out waiting for confirmation|wait for confirmation timed out' "$dir/steamcmd.log"; then
            echo "The sign-in was not confirmed in time; asking again."
            continue
        fi
        fail "steamcmd did not upload the item (exit $status)"
    done
    fail "the sign-in was not confirmed within 5 minutes"
}

case "${1:-}" in
    prepare) shift; [ $# -eq 6 ] || fail "prepare RUN DIR MASTER ITEM VISIBILITY PREVIEW"; prepare "$@" ;;
    steamcmd) shift; [ $# -eq 1 ] || fail "steamcmd ROOT"; install_steamcmd "$1" ;;
    upload) shift; [ $# -eq 2 ] || fail "upload ROOT DIR"; upload "$@" ;;
    *) fail "usage: workshop-upload.sh prepare|steamcmd|upload ..." ;;
esac
