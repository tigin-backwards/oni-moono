#!/usr/bin/env bash
# Runs the player with its own persistent-data and cache roots (out/run-home), so test runs
# never read or write the user's real game data (mods, saves, prefs) under ~/.config/unity3d.
# Extra arguments go to the host, e.g.: scripts/run.sh --headless --frames 300
# ONI_CONFIG=release runs the release build (scripts/build-engine.sh release and a release host).
# The player runs in the memory-capped test slice (scripts/contain.sh).
# Test runs measure and compare the game without tweaks unless they name some: `--game-tweak none`
# comes first, so the player's default tweaks and any Moono Tweaks settings file in the run home
# play no part, and a run's own `--game-tweak` arguments (e.g. `exact`) are its whole set. Likewise
# `--splash none`: the default loading splash stays out of test runs unless one asks for it, and
# `--log-paths full`: the log keeps its paths as logged, as the original's (the test tools read
# them).
# ONI_TWEAK_SETTINGS=1 leaves that out: the run home's settings and the defaults apply, as in a
# user's launch (for trying the main menu's Moono Tweaks screen).
# It runs as a runq job of kind linux-player (docs/RUNQ.md; a lane, a player's memory and cores);
# inside a job, with RUNQ=off or without a daemon it runs directly.
set -euo pipefail
source "$(dirname "$0")/env.sh"
source "$root/scripts/runq-submit.sh"
runq_self_submit --kind linux-player --label "run.sh${*:+ $*}" -- "$root/scripts/run.sh" "$@"
home="${ONI_RUN_HOME:-$root/out/run-home}"
mkdir -p "$home/config" "$home/cache"
export XDG_CONFIG_HOME="$home/config" XDG_CACHE_HOME="$home/cache"
# Test runs never ask GitHub for releases (the main menu's update check): ONI_UPDATE_RELEASES_URL
# names a releases file to use instead (tests/e2e/update-badge.yaml).
export ONI_UPDATE_RELEASES_URL="${ONI_UPDATE_RELEASES_URL:-none}"
tweaks=(--game-tweak none --splash none --log-paths full)
[ "${ONI_TWEAK_SETTINGS:-}" = 1 ] && tweaks=()
exec "$root/scripts/contain.sh" "$root/out/bin/OniMoono.Host/${ONI_CONFIG:-debug}/OniMoono" --game "$game" "${tweaks[@]}" "$@"
