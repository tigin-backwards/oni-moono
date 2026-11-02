#!/usr/bin/env bash
# contain.sh CMD [ARG...]: runs CMD as a preferred OOM victim (oom_score_adj 1000) in the
# memory-capped systemd user slice oni-tests.slice (scripts/oni-tests.slice, installed into
# ~/.config/systemd/user when missing or changed). Test players and builds run through it, so
# that when they over-commit memory the kernel kills one of them, not the user's sessions or WSL.
# The command keeps this process's pid (systemd-run --scope execs it). Without a systemd user
# manager (macOS, CI containers) it only raises the OOM score. ONI_CONTAINED marks a command
# already inside: nested calls exec directly. OOMPolicy=continue: a killed player ends alone, not
# the scope around it (a whole QA run).
# ONI_SCOPE=NAME.scope names the scope, inside a contained command too: a test runner
# (scripts/e2e.py, scripts/qa.sh) starts each player in a scope of its own and stops that scope
# whole when the run ends, so nothing the player started outlives it. It is not passed on.
# The user's desktop display reaches no command run here: WSLg's Wayland compositor always stays
# out, and so does DISPLAY when it is WSLg's desktop (:0), as scripts/e2e.py does for its
# players. A player, a probe or a check mode that makes a render context would otherwise put a
# window on the user's Windows desktop, even a hidden one for a moment (2026-11-01: --check-shaders
# showed a 1x1 one). Test displays (an Xvfb's :N) pass. ONI_DESKTOP_DISPLAY=1 keeps the desktop's,
# for a deliberate WSLg run (on the second monitor only).
# Sound stays out the same way: WSLg's PulseAudio server plays on the user's Windows speakers
# (2026-11-01: a test player's game sound did), so a command here gets a server that doesn't
# exist and plays nothing. ONI_DESKTOP_AUDIO=1 keeps it.
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
