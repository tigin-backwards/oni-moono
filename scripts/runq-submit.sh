# runq_self_submit [RUNQ RUN OPTIONS...] -- CMD [ARG...] (sourced): a script's re-exec through
# runq (docs/RUNQ.md), so that it runs when the shared machine has room for it, with its legacy
# locks taken by runq. It returns, and the script runs directly as before, when:
# - RUNQ_JOB is set: the script already runs inside a runq job (the daemon itself runs it, or a
#   job's own script calls it), so it never queues itself again;
# - RUNQ=off, or `enabled = false` in ~/.config/oni-runq/config.toml (the kill switch): the old
#   direct path, with the old locks;
# - the daemon does not answer (one line on stderr): the old direct path too.
# RUNQ_LEVEL and RUNQ_OWNER pass on to runq (default level study, owner the worktree's name).
# shellcheck shell=bash
runq_self_submit()
{
    [ -z "${RUNQ_JOB:-}" ] || return 0
    case "${RUNQ:-}" in off|OFF|0|no|false) return 0 ;; esac
    local runq why
    runq="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/runq"
    if ! why=$(python3 "$runq" ping 2>&1 > /dev/null); then
        echo "runq: ${why#runq: }; running directly with the old locks" >&2
        return 0
    fi
    exec python3 "$runq" run "$@"
}
