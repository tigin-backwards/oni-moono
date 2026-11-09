#!/usr/bin/env bash
# hang-dump.sh PID DIR [LOG]: what a hung player is doing, before a watchdog kills it (scripts/qa.sh's
# U57 runs, scripts/e2e.py's players). DIR gets:
#   summary.txt         the main thread's state and CPU share (busy loop or wait), and what was taken
#   managed-stacks.txt  every managed thread's stack (dotnet-stack report, through the runtime's
#                       diagnostics IPC; needs no ptrace)
#   dump-stacks.txt     with HANG_DUMP=1, or when dotnet-stack fails: a mini dump (dotnet-dump
#                       collect, which the runtime writes itself) read by dotnet-dump analyze
#                       (clrthreads, clrstack -all); the dump stays beside it as core
#   native-stacks.txt   gdb's `thread apply all bt`, when gdb is installed
#   threads.txt         per thread: id, name, state, CPU ticks and the kernel wait channel
#   top.txt             top -H, two samples 3 s apart (the second is over those 3 s)
#   status.txt          /proc/PID/status and the command line;  log-tail.txt  LOG's last 300 lines
# dotnet-stack and dotnet-dump are .NET global tools (dotnet tool install -g dotnet-stack
# dotnet-dump, into ~/.dotnet/tools), looked for on PATH and there; missing ones are skipped and
# summary.txt says so. Each step is time-limited (a stopped process answers no IPC).
set -uo pipefail
pid=$1 dir=$2 log=${3:-}
mkdir -p "$dir"
[ -d "/proc/$pid" ] || { echo "hang-dump: no process $pid" | tee "$dir/summary.txt"; exit 1; }
[ -x "$HOME/.dotnet/dotnet" ] && export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
tool() { command -v "$1" 2>/dev/null || { [ -x "$HOME/.dotnet/tools/$1" ] && echo "$HOME/.dotnet/tools/$1"; }; }
notes=()

{ cat "/proc/$pid/status"; echo; echo "cmdline: $(tr '\0' ' ' < "/proc/$pid/cmdline")"; } > "$dir/status.txt" 2>&1
threads() # id name state utime+stime wchan, from /proc/PID/task
{
    local t stat
    printf '%-8s %-16s %-5s %-10s %s\n' tid name state cpu-ticks wchan
    for t in /proc/"$pid"/task/*; do
        stat=$(cat "$t/stat" 2>/dev/null) || continue
        # The name is in parentheses and may hold spaces: fields count from after its ')'.
        local rest=${stat##*) } name=${stat#*(}; name=${name%)*}
        read -r -a f <<< "$rest"
        printf '%-8s %-16s %-5s %-10s %s\n' "${t##*/}" "$name" "${f[0]}" $(( f[11] + f[12] )) "$(cat "$t/wchan" 2>/dev/null)"
    done
}
threads > "$dir/threads.txt"; t0=$(date +%s.%N)
timeout 20 top -H -b -n2 -d3 -w 200 -p "$pid" > "$dir/top.txt" 2>&1 || notes+=("top failed")

# The main thread (its id is the pid): its CPU share over the top samples, from its ticks before and after.
hz=$(getconf CLK_TCK)
main_state=$(awk -v p="$pid" '$1 == p { print $3 }' "$dir/threads.txt")
ticks0=$(awk -v p="$pid" '$1 == p { print $4 }' "$dir/threads.txt")
ticks1=$(threads | awk -v p="$pid" '$1 == p { print $4 }'); t1=$(date +%s.%N)
main_cpu=$(awk -v a="${ticks0:-0}" -v b="${ticks1:-0}" -v hz="$hz" -v s="$t0" -v e="$t1" 'BEGIN { printf "%.0f", 100 * (b - a) / hz / (e - s) }')
span=$(awk -v s="$t0" -v e="$t1" 'BEGIN { printf "%.0f", e - s }')
main_wchan=$(cat "/proc/$pid/wchan" 2>/dev/null)

stack=$(tool dotnet-stack) dump=$(tool dotnet-dump) stacks_ok=0
# The original player is Unity's Mono, which the .NET tools cannot read.
grep -q libcoreclr "/proc/$pid/maps" 2>/dev/null || { stack="" dump=""; stacks_ok=1; notes+=("not a CoreCLR process: no managed stacks"); }
if [ -n "$stack" ]; then
    if timeout 120 "$stack" report -p "$pid" > "$dir/managed-stacks.txt" 2>&1 && grep -q 'Thread (' "$dir/managed-stacks.txt"; then
        stacks_ok=1
    else notes+=("dotnet-stack report failed (see managed-stacks.txt)"); fi
else notes+=("no dotnet-stack (dotnet tool install -g dotnet-stack)"); fi
if [ -n "$dump" ] && { [ "${HANG_DUMP:-}" = 1 ] || [ $stacks_ok = 0 ]; }; then
    if timeout 300 "$dump" collect -p "$pid" --type Mini -o "$dir/core" > "$dir/dump-collect.txt" 2>&1; then
        timeout 300 "$dump" analyze "$dir/core" -c clrthreads -c "clrstack -all" -c exit > "$dir/dump-stacks.txt" 2>&1 \
            || notes+=("dotnet-dump analyze failed (see dump-stacks.txt)")
    else notes+=("dotnet-dump collect failed (see dump-collect.txt)"); fi
elif [ -z "$dump" ] && [ $stacks_ok = 0 ]; then notes+=("no dotnet-dump (dotnet tool install -g dotnet-dump)"); fi
if command -v gdb > /dev/null; then
    timeout 180 gdb -p "$pid" -batch -ex "thread apply all bt" > "$dir/native-stacks.txt" 2>&1 || notes+=("gdb failed (see native-stacks.txt)")
else notes+=("no gdb: no native stacks (threads.txt has each thread's kernel wait channel)"); fi
[ -n "$log" ] && [ -f "$log" ] && tail -n 300 "$log" > "$dir/log-tail.txt"

case $main_state in
    R) verdict="running, ${main_cpu}% of a core over ${span} s: a busy loop" ;;
    S|D) verdict="sleeping (${main_wchan:-?}), ${main_cpu}% of a core over ${span} s: waiting, a deadlock if it never wakes" ;;
    T|t) verdict="stopped (a signal or a debugger)" ;;
    *) verdict="state ${main_state:-?}, ${main_cpu}% of a core" ;;
esac
{
    echo "hang-dump of $pid at $(date '+%F %T'): the main thread is $verdict"
    echo "taken: $(cd "$dir" && ls -I summary.txt | paste -sd' ' -)"
    for n in "${notes[@]}"; do echo "note: $n"; done
} > "$dir/summary.txt"
cat "$dir/summary.txt"
