#!/usr/bin/env bash
# Runs the player with persistent-data and cache roots under out/run-home (ONI_RUN_HOME), so test
# runs never read or write the user's game data (mods, saves, prefs) under ~/.config/unity3d.
# Extra arguments go to the host: scripts/run.sh --headless --frames 300
# - ONI_CONFIG=release runs the release build (scripts/build-engine.sh release and a release host).
# - `--game-tweak none --splash none --log-paths full` come first: no default tweaks, no Moono
#   Tweaks settings file from the run home, no loading splash, and the log's paths as logged (the
#   test tools read them). A run's `--game-tweak` arguments (e.g. `exact`) are its whole set.
# - ONI_TWEAK_SETTINGS=1 leaves those out: the run home's settings and the defaults apply, as in
#   a user's launch (for trying the main menu's Moono Tweaks screen).
# - The player runs in the memory-capped test slice (scripts/contain.sh).
# Queues itself through runq as kind linux-player (docs/RUNQ.md).
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
# A crash writes its record with no crash window to wait on (a run home here is XDG_CONFIG_HOME,
# not --run-home, which would keep it off too); ONI_MOONO_CRASH_DIALOG=on shows it.
export ONI_MOONO_CRASH_DIALOG="${ONI_MOONO_CRASH_DIALOG:-off}"
tweaks=(--game-tweak none --splash none --log-paths full)
[ "${ONI_TWEAK_SETTINGS:-}" = 1 ] && tweaks=()
exec "$root/scripts/contain.sh" "$root/out/bin/OniMoono.Host/${ONI_CONFIG:-debug}/OniMoono" --game "$game" "${tweaks[@]}" "$@"
