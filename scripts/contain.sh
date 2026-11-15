#!/usr/bin/env bash
# contain.sh CMD [ARG...]: runs CMD in the memory-capped systemd user slice oni-tests.slice
# (scripts/oni-tests.slice, installed into ~/.config/systemd/user when missing or changed) with
# oom_score_adj 1000, so that over-commit kills a test player or build, not the user's sessions
# or WSL. CMD keeps this process's pid (systemd-run --scope execs it).
# - Without a systemd user manager (macOS, CI containers) it only raises the OOM score.
# - ONI_CONTAINED marks a command already inside: nested calls exec directly.
# - OOMPolicy=continue: a killed player ends alone, not the scope around it (a whole QA run).
# - ONI_SCOPE=NAME.scope names the scope, inside a contained command too: scripts/e2e.py and
#   scripts/qa.sh start each player in one and stop it whole when the run ends, so nothing the
#   player started outlives it. It is not passed on.
# - No command here reaches the user's desktop: WAYLAND_DISPLAY is dropped, and DISPLAY when it
#   is WSLg's (:0). Any render context (a player, a probe, a check mode) would otherwise open a
#   window there, even a hidden one. Xvfb displays pass. ONI_DESKTOP_DISPLAY=1 keeps the
#   desktop's, for a deliberate WSLg run (second monitor only).
# - No sound: WSLg's PulseAudio plays on the user's Windows speakers, so PULSE_SERVER names a
#   server that does not exist. ONI_DESKTOP_AUDIO=1 keeps it.
if [ "${ONI_DESKTOP_DISPLAY:-}" != 1 ]; then
    unset WAYLAND_DISPLAY XDG_SESSION_TYPE
    case "${DISPLAY:-}" in :0|:0.0) [ -d /mnt/wslg ] && unset DISPLAY XAUTHORITY ;; esac
fi
if [ "${ONI_DESKTOP_AUDIO:-}" != 1 ]; then
    export PULSE_SERVER=unix:/nonexistent/oni-tests-no-audio
    export SDL_AUDIODRIVER=dummy
fi
set -euo pipefail
echo 1000 > /proc/self/oom_score_adj 2> /dev/null || true
scope="${ONI_SCOPE:-}"
unset ONI_SCOPE
if { [ -z "${ONI_CONTAINED:-}" ] || [ -n "$scope" ]; } && command -v systemd-run > /dev/null && systemctl --user show-environment > /dev/null 2>&1; then
    unit="$HOME/.config/systemd/user/oni-tests.slice"
    if ! cmp -s "$(dirname "$0")/oni-tests.slice" "$unit"; then
        mkdir -p "$(dirname "$unit")"
        cp "$(dirname "$0")/oni-tests.slice" "$unit"
        systemctl --user daemon-reload
    fi
    export ONI_CONTAINED=1
    exec systemd-run --user --scope --quiet --collect -p OOMPolicy=continue --slice=oni-tests.slice ${scope:+"--unit=$scope"} -- "$@"
fi
export ONI_CONTAINED=1
exec "$@"
