#!/usr/bin/env bash
# The native crash records (ARCHITECTURE §7, "Native crashes"): the player started with
# --test-native-crash WHERE for each kind of crash, its Player.log checked for the crash lines and
# its minidump for the fault. Windows when out/win/host (scripts/build-windows.sh) and Windows are
# here: each case crashes before the window opens, so nothing shows on screen and nothing plays,
# started as Steam starts it (no standard streams), in its own run home under %TEMP%. Linux: a
# caught null reference writes no crash line, and the runtime's own dump settings, which
# docs/ARCHITECTURE.md gives Linux players, write a crash report for a crash in libc.
#   scripts/check-native-crash.sh [linux|windows|both]
set -euo pipefail
source "$(dirname "$0")/env.sh"
[ -n "${ONI_CONTAINED:-}" ] || exec "$root/scripts/contain.sh" "$0" "$@"
which="${1:-both}"
ok=0; bad=0
pass() { echo "ok   $1"; ok=$((ok + 1)); }
fail() { echo "FAIL $1"; bad=$((bad + 1)); }
expect() { # expect NAME FILE REGEX: a line of FILE matches
    if grep -Eq -- "$3" "$2"; then pass "$1"; else fail "$1: no line matching $3"; fi
}
refuse() { # refuse NAME FILE REGEX: no line of FILE matches
    if grep -Eq -- "$3" "$2"; then fail "$1: $(grep -Em1 -- "$3" "$2")"; else pass "$1"; fi
}

# The minidump's exception stream (MINIDUMP_EXCEPTION_STREAM, type 6): its code, or "none".
dump_exception() {
    python3 -I - "$1" <<'PY'
import struct, sys
d = open(sys.argv[1], 'rb').read()
if d[:4] != b'MDMP': print('not a minidump'); sys.exit()
count, rva = struct.unpack_from('<II', d, 8)
for i in range(count):
    kind, size, at = struct.unpack_from('<III', d, rva + 12 * i)
    if kind == 6:
        print('0x%08X' % struct.unpack_from('<I', d, at + 8)[0]); sys.exit()
print('none')
PY
}

if [ "$which" != windows ]; then
    player="$root/out/bin/OniMoono.Host/debug/OniMoono"
    tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
    status=0
    "$player" --game "$game" --log "$tmp/null.log" --test-native-crash managed-null > /dev/null 2>&1 || status=$?
    [ $status -eq 0 ] && pass "linux managed-null: exit 0" || fail "linux managed-null: exit $status"
    expect "linux managed-null: caught" "$tmp/null.log" 'test native crash: the null reference was caught'
    refuse "linux managed-null: no crash line" "$tmp/null.log" '\[engine\] native crash:'
    # No core file, and the shell's "Segmentation fault" kept off the output.
    status=$( (ulimit -c 0; DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_EnableCrashReport=1 DOTNET_DbgMiniDumpName="$tmp/crash-%p.dmp" \
        "$player" --game "$game" --log "$tmp/main.log" --test-native-crash main > "$tmp/main.out" 2>&1; echo $?) 2> /dev/null)
    [ $status -eq 139 ] && pass "linux main: SIGSEGV" || fail "linux main: exit $status"
    expect "linux main: hook ran" "$tmp/main.log" 'test native crash: main$'
    report=$(ls "$tmp"/crash-*.dmp.crashreport.json 2>/dev/null | head -1)
    if [ -n "$report" ] && grep -q 'libc' "$report"; then pass "linux main: crash report names libc ($(du -k "$(ls "$tmp"/crash-*.dmp | head -1)" | cut -f1) KB dump)"
    else fail "linux main: no crash report naming libc ($(ls "$tmp"))"; fi
fi

if [ "$which" != linux ]; then
    host="$root/out/win/host/OniMoono.exe"
    if [ ! -f "$host" ] || ! command -v powershell.exe > /dev/null; then
        echo "windows skipped: no out/win/host/OniMoono.exe (scripts/build-windows.sh) or no Windows"
    else
        wtemp="$(cd /mnt/c && cmd.exe /c 'echo %TEMP%' 2> /dev/null | tr -d '\r')"
        wstage="$wtemp\\oni-moono-crash-check-$$"
        stage="$(wslpath "$wstage")"
        rm -rf "$stage"; mkdir -p "$stage"
        cp -r "$root/out/win/host" "$stage/host"
        trap 'rm -rf "$stage"; [ -n "${tmp:-}" ] && rm -rf "$tmp"' EXIT
        wgame="${ONI_WIN_GAME_DIR:-C:\\Program Files (x86)\\Steam\\steamapps\\common\\OxygenNotIncluded}"
        profile="$(cd /mnt/c && cmd.exe /c 'echo %USERPROFILE%' 2> /dev/null | tr -d '\r')"
        # run CASE [redirect]: one player, in its own run home; with redirect, its standard error
        # to a file (as a terminal's 2> does), else no standard streams (as Steam starts it).
        run() {
            local home="$wstage\\$1${2:+-$2}" shell='$true' redirect=''
            [ -n "${2:-}" ] && { shell='$false'; redirect="\$psi.RedirectStandardError = \$true;"; }
            (cd /mnt/c && timeout 300 powershell.exe -NoProfile -Command "
                \$psi = New-Object System.Diagnostics.ProcessStartInfo
                \$psi.FileName = '$wstage\\host\\OniMoono.exe'
                \$psi.Arguments = '--game \"$wgame\" --run-home \"$home\" --test-native-crash $1'
                \$psi.UseShellExecute = $shell; $redirect
                \$p = [System.Diagnostics.Process]::Start(\$psi)
                if (-not \$psi.UseShellExecute) { Set-Content -Path '$home-stderr.txt' -Value \$p.StandardError.ReadToEnd() }
                \$p.WaitForExit()" > /dev/null 2>&1) || true
            dir="$stage/$1${2:+-$2}/AppData/LocalLow/Klei/Oxygen Not Included"
            log="$dir/Player.log"
            # The watcher writes once the game has gone.
            for _ in $(seq 50); do grep -q 'native crash: the process ended\|test native crash: the null' "$log" 2> /dev/null && break; sleep 0.2; done
            [ -f "$log" ] || { fail "windows $1: no Player.log"; return 1; }
            dump=$(ls "$dir"/crash-*.dmp 2> /dev/null | head -1) || true
        }
        crashed() { # crashed CASE CODE [THREAD]: the fault line, its stack, the end, the dump
            expect "windows $1: fault line" "$log" "native crash: $2 [a-z -]+ .* in [A-Za-z0-9_.]+\+0x[0-9A-F]+ \([0-9.]+, [^)]+\) on thread [0-9]+${3:-}"
            expect "windows $1: stack" "$log" 'native crash stack: [A-Za-z0-9_.]+\+0x'
            expect "windows $1: end" "$log" "native crash: the process ended with $2"
            dumped "$1" "$2"
        }
        dumped() { # dumped CASE CODE|none: the dump line, the file and its exception
            expect "windows $1: dump line" "$log" 'native crash dump: .*crash-[0-9]{8}-[0-9]{6}\.dmp \([0-9]+ KB\)'
            if [ -n "$dump" ]; then
                local code; code=$(dump_exception "$dump")
                [ "$code" = "$2" ] && pass "windows $1: dump exception $code ($(du -k "$dump" | cut -f1) KB)" || fail "windows $1: dump exception $code, not $2"
            else fail "windows $1: no dump"; fi
        }
        run main && { crashed main 0xC0000005 ' \(main\)'; expect "windows main: runtime text" "$log" 'native crash: runtime:    at OniMoono.Host.NativeCrashes.Fault'
            expect "windows main: paths hidden" "$log" 'in OniMoono.exe\+0x[0-9A-F]+ \([0-9.]+, %USERPROFILE%\\'
            refuse "windows main: no profile path" "$log" "$(sed 's/[\\.]/\\&/g' <<< "$profile")"; }
        run worker && { crashed worker 0xC0000005 ' "Test worker"'; expect "windows worker: runtime text" "$log" 'native crash: runtime: Fatal error\.'; }
        run thread && crashed thread 0xC0000005 '[[:space:]]*$'
        run stack-overflow && crashed stack-overflow 0xC00000FD
        run heap && crashed heap 0xC0000374
        run main-stack-overflow && { expect "windows main-stack-overflow: runtime text" "$log" 'native crash: runtime: Stack overflow\.'
            expect "windows main-stack-overflow: end" "$log" 'native crash: the process ended with 0xC00000FD'; dumped main-stack-overflow none; }
        for c in fastfail gs abort; do
            run $c && { expect "windows $c: end" "$log" 'native crash: the process ended with 0xC0000409 \(fast fail'; [ -z "$dump" ] && pass "windows $c: no dump" || fail "windows $c: a dump"; }
        done
        run terminate && expect "windows terminate: end" "$log" "native crash: the process ended with 1 without the player's own exit"
        run failfast && { expect "windows failfast: runtime text" "$log" 'native crash: runtime: Process terminated\.'
            expect "windows failfast: end" "$log" 'native crash: the process ended with 0x80131623'; dumped failfast none; }
        run managed-null && { expect "windows managed-null: caught" "$log" 'test native crash: the null reference was caught'
            refuse "windows managed-null: no crash line" "$log" '\[engine\] native crash:'; [ -z "$dump" ] && pass "windows managed-null: no dump" || fail "windows managed-null: a dump"; }
        # Standard error redirected: still passed on, with the runtime's text kept.
        run main redirect && { expect "windows main (stderr redirected): runtime text" "$log" 'native crash: runtime: Fatal error\.'
            expect "windows main (stderr redirected): passed on" "$stage/main-redirect-stderr.txt" '^Fatal error\.'; }
    fi
fi
echo "check-native-crash: $ok ok, $bad failed"
[ $bad -eq 0 ]
