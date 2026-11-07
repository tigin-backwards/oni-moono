#!/usr/bin/env bash
# The native crash records (ARCHITECTURE §7, "Native crashes"): the player started with
# --test-native-crash WHERE for each kind of crash, its Player.log checked for the crash lines and
# its minidump for the fault. Windows when out/win/host (scripts/build-windows.sh) and Windows are
# here: each case crashes before the window opens, so nothing shows on screen and nothing plays,
# started as Steam starts it (no standard streams), in its own run home under %TEMP%. Linux: a
# caught null reference writes no crash line, and the runtime's own dump settings, which
# docs/ARCHITECTURE.md gives Linux players, write a crash report for a crash in libc. Windows also
# checks the crash window: no case leaves one open (a run with --run-home shows none), and its
# drawing (--crash-dialog-render, nothing shown), zip and issue link are checked offscreen.
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

# A heap failure's dump (0xC0000374): its memory holds ntdll's record of the failure, which the
# exception's first parameter points at, and the damaged block the record names (its fourth
# word): "record and block", else what is missing.
dump_heap_record() {
    python3 -I - "$1" <<'PY'
import struct, sys
d = open(sys.argv[1], 'rb').read()
u32 = lambda o: struct.unpack_from('<I', d, o)[0]
u64 = lambda o: struct.unpack_from('<Q', d, o)[0]
streams = {}
for i in range(u32(8)):
    kind, size, at = struct.unpack_from('<III', d, u32(12) + 12 * i)
    streams.setdefault(kind, at)
mem = [(u64(streams[5] + 4 + 16 * i), u32(streams[5] + 12 + 16 * i), u32(streams[5] + 16 + 16 * i)) for i in range(u32(streams[5]))] if 5 in streams else []
def at(a):
    return next((r + a - s for s, z, r in mem if s <= a and a + 8 <= s + z), None)
record = at(u64(streams[6] + 40)) if 6 in streams else None
if record is None: print('no record'); sys.exit()
print('record and block' if at(u64(record + 24)) is not None else 'record without the block')
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

# The crash window drawn offscreen (--crash-dialog-render): the PNG's size at the scale and the
# palette's colours where the title bar, the body and the footer are; then the zip "Report on
# GitHub" writes (deflate, checked by Python's zipfile) and the issue link (under 7,500 characters
# with a long note, the error in its title).
png_check() { # png_check NAME FILE WIDTH CAPTION BODY FOOTER (colours as RRGGBB)
    local out; out=$(python3 -I - "$@" <<'PY'
import struct, sys, zlib
name, path, width, caption, body, footer = sys.argv[1:7]
d = open(path, 'rb').read()
if d[:8] != b'\x89PNG\r\n\x1a\n': print('not a PNG'); sys.exit()
at, idat, w = 8, b'', 0
while at < len(d):
    n, kind = struct.unpack_from('>I4s', d, at)
    if kind == b'IHDR': w, h, depth, ctype = struct.unpack_from('>IIBB', d, at + 8)
    if kind == b'IDAT': idat += d[at + 8:at + 8 + n]
    at += 12 + n
bpp = {6: 4, 2: 3}.get(ctype)
if depth != 8 or not bpp: print('PNG of depth %d type %d' % (depth, ctype)); sys.exit()
raw, rows, prev, stride = zlib.decompress(idat), [], bytearray(w * bpp), w * bpp
for y in range(h):
    f, line = raw[y * (stride + 1)], bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
    for i in range(stride):
        a = line[i - bpp] if i >= bpp else 0; b = prev[i]; c = prev[i - bpp] if i >= bpp else 0
        p = a + b - c; pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
        line[i] = (line[i] + (0, a, b, (a + b) // 2, a if pa <= pb and pa <= pc else b if pb <= pc else c)[f]) & 255
    rows.append(line); prev = line
px = lambda x, y: '%02X%02X%02X' % tuple(rows[y][x * bpp:x * bpp + 3])
problems = []
if w != int(width): problems.append('width %d, not %s' % (w, width))
if not (w * 0.45 < h < w * 1.6): problems.append('height %d' % h)
for what, (x, y), want in (('title bar', (w // 2, 3), caption), ('body', (3, h // 3), body), ('footer', (3, h - 3), footer)):
    if px(x, y) != want: problems.append('%s %s, not %s' % (what, px(x, y), want))
ink = sum(1 for y in range(h // 8, h - h // 8) for x in range(0, w, 2) if abs(rows[y][x * bpp] - int(body[4:6], 16)) > 96)
if ink < w: problems.append('almost no text (%d)' % ink)
print('; '.join(problems) if problems else 'ok %dx%d' % (w, h))
PY
)
    [ "${out%% *}" = ok ] && pass "windows crash window $1: $out" || fail "windows crash window $1: $out"
}
crash_window() {
    local exe="$stage/host/OniMoono.exe"
    render() { (cd /mnt/c && timeout 60 "$exe" --crash-dialog-render "$@" > /dev/null 2>&1) || return 1; }
    render "$wstage\\dark.png" --theme dark --scale 1.5 --sample gpu --details-open && png_check "dark, 150%, details" "$stage/dark.png" 660 100C0A 191512 1D1A17 \
        || fail "windows crash window: dark render"
    render "$wstage\\light.png" --theme light --scale 1.25 --sample managed --language zh --reported && png_check "light, 125%, Chinese" "$stage/light.png" 550 F7F4ED FFFCF6 F7F4ED \
        || fail "windows crash window: light render"
    # The zip: a 40 MB log (cut to its head and tail) and a dump of zeros and noise.
    python3 -I - "$stage/zip-in" <<'PY'
import os, random, sys
os.makedirs(sys.argv[1], exist_ok=True); random.seed(7)
with open(os.path.join(sys.argv[1], 'Player.log'), 'wb') as f:
    for i in range(700000): f.write(b'[engine] line %d of the log, with some text that repeats %d\r\n' % (i, random.randrange(1000)))
with open(os.path.join(sys.argv[1], 'crash-20261107-141502.dmp'), 'wb') as f:
    f.write(b'MDMP' + bytes(1 << 20) + random.randbytes(1 << 19))
PY
    render "$wstage\\zip.png" --zip "$wstage\\crash.zip" --log "$wstage\\zip-in\\Player.log" --dump "$wstage\\zip-in\\crash-20261107-141502.dmp" \
        --url "$wstage\\url.txt" --note "$(printf 'what I was doing %.0s' $(seq 600))" || fail "windows crash window: zip and link"
    local zipped; zipped=$(python3 -I - "$stage/crash.zip" "$stage/zip-in" <<'PY'
import os, sys, zipfile
z = zipfile.ZipFile(sys.argv[1]); bad = z.testzip()
if bad: print('bad entry', bad); sys.exit()
log = open(os.path.join(sys.argv[2], 'Player.log'), 'rb').read(); got = z.read('Player.log')
dump = open(os.path.join(sys.argv[2], 'crash-20261107-141502.dmp'), 'rb').read()
if len(log) <= 32 << 20 and got != log: print('log differs')
elif len(log) > 32 << 20 and not (got[:4 << 20] == log[:4 << 20] and got[-(28 << 20):] == log[-(28 << 20):] and b'left out by the crash window' in got): print('log not cut to head and tail')
elif z.read('crash-20261107-141502.dmp') != dump: print('dump differs')
else: print('ok %d KB for %d KB' % (os.path.getsize(sys.argv[1]) // 1024, (len(log) + len(dump)) // 1024))
PY
)
    [ "${zipped%% *}" = ok ] && pass "windows crash window zip: $zipped" || fail "windows crash window zip: $zipped"
    local linked; linked=$(python3 -I - "$stage/url.txt" <<'PY'
import sys, urllib.parse
u = open(sys.argv[1], encoding='utf-8').read(); q = urllib.parse.parse_qs(urllib.parse.urlsplit(u).query)
if not u.startswith('https://github.com/tigin-backwards/oni-moono/issues/new?'): print('not the issue page'); sys.exit()
if len(u) > 7500: print('%d characters' % len(u)); sys.exit()
if q.get('title') != ['Crash: 0xC0000005 (access violation) in nvoglv64.dll']: print('title', q.get('title')); sys.exit()
body = q['body'][0]
print('ok %d characters' % len(u) if '\u2026' in body and 'OniMoono-crash-20261107-1415.zip' in body and 'game build 762230' in body else 'body: ' + body[:200])
PY
)
    [ "${linked%% *}" = ok ] && pass "windows crash window link: $linked" || fail "windows crash window link: $linked"
}

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
            local home="$wstage\\${name:-$1${2:+-$2}${extra:+-full}}" shell='$true' redirect=''
            [ -n "${2:-}" ] && { shell='$false'; redirect="\$psi.RedirectStandardError = \$true;"; }
            (cd /mnt/c && timeout 300 powershell.exe -NoProfile -Command "
                \$psi = New-Object System.Diagnostics.ProcessStartInfo
                \$psi.FileName = '$wstage\\host\\OniMoono.exe'
                \$psi.Arguments = '--game \"$wgame\" --run-home \"$home\" --test-native-crash $1 ${extra:-}'
                \$psi.UseShellExecute = $shell; $redirect
                \$p = [System.Diagnostics.Process]::Start(\$psi)
                if (-not \$psi.UseShellExecute) { Set-Content -Path '$home-stderr.txt' -Value \$p.StandardError.ReadToEnd() }
                \$p.WaitForExit()" > /dev/null 2>&1) || true
            dir="$stage/${name:-$1${2:+-$2}${extra:+-full}}/AppData/LocalLow/Klei/Oxygen Not Included"
            log="$dir/Player.log"
            # The watcher writes once the game has gone.
            for _ in $(seq 50); do grep -q 'native crash: the process ended\|test native crash: the null' "$log" 2> /dev/null && break; sleep 0.2; done
            [ -f "$log" ] || { fail "windows $1: no Player.log"; return 1; }
            dump=$(ls -td "$dir"/crash-*.dmp 2> /dev/null | head -1) || true
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
        run heap && { crashed heap 0xC0000374
            [ -n "$dump" ] && { held=$(dump_heap_record "$dump"); [ "$held" = "record and block" ] && pass "windows heap: dump holds the heap failure's record and block" || fail "windows heap: dump: $held"; }; }
        # --crash-dumps full: the dump holds the heaps, many times the small one's size.
        small=$(du -k "$dump" 2> /dev/null | cut -f1)
        extra='--crash-dumps full' run heap && { crashed heap 0xC0000374
            full=$(du -k "$dump" 2> /dev/null | cut -f1)
            [ "${full:-0}" -gt $(( ${small:-0} * 3 )) ] && pass "windows heap full: memory in the dump ($full KB, $small KB without)" || fail "windows heap full: $full KB, not much more than the $small KB without"; }
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
        # The watcher keeps the two newest dumps: three crashes in one run home after three old
        # dumps, the last a full one, leave the last two; files not named as ours stay.
        pdir="$stage/prune/AppData/LocalLow/Klei/Oxygen Not Included"
        mkdir -p "$pdir/crash-20200104-000000.dmp"; touch -d 2020-01-04 "$pdir/crash-20200104-000000.dmp"
        for d in 01 02 03; do echo old > "$pdir/crash-202001$d-000000.dmp"; touch -d "2020-01-$d" "$pdir/crash-202001$d-000000.dmp"; done
        echo other > "$pdir/crash-old.dmp"; echo other > "$pdir/crash-20200101-000000.dmp.txt"
        touch -d 2020-01-01 "$pdir/crash-old.dmp" "$pdir/crash-20200101-000000.dmp.txt"
        kept=()
        for c in main worker heap; do
            [ $c = heap ] && extra='--crash-dumps full'
            name=prune run $c && kept+=("$(basename "$dump")")
            extra=''; sleep 1
        done
        left=$(cd "$pdir" && find . -maxdepth 1 -type f -regextype posix-extended -regex '.*/crash-[0-9]{8}-[0-9]{6}\.dmp' -printf '%f\n' | sort | tr '\n' ' ')
        [ "$left" = "$(printf '%s\n' "${kept[@]:1}" | sort | tr '\n' ' ')" ] && [ ${#kept[@]} -eq 3 ] \
            && pass "windows prune: the two newest dumps kept ($left)" || fail "windows prune: left $left after ${kept[*]}"
        [ -f "$pdir/crash-old.dmp" ] && [ -f "$pdir/crash-20200101-000000.dmp.txt" ] && [ -d "$pdir/crash-20200104-000000.dmp" ] \
            && pass "windows prune: other files and folders kept" || fail "windows prune: $(ls "$pdir")"
        [ -n "$dump" ] && { held=$(dump_heap_record "$dump"); [ "$held" = "record and block" ] \
            && pass "windows prune: the newest dump intact ($(du -k "$dump" | cut -f1) KB, full)" || fail "windows prune: newest dump: $held"; }
        # Standard error redirected: still passed on, with the runtime's text kept.
        run main redirect && { expect "windows main (stderr redirected): runtime text" "$log" 'native crash: runtime: Fatal error\.'
            expect "windows main (stderr redirected): passed on" "$stage/main-redirect-stderr.txt" '^Fatal error\.'; }
        # No crash window waits: each run has a run home of its own, which shows none. A watcher
        # still there (a window) is ended by its process id and fails the check.
        sleep 2
        left=$(cd /mnt/c && powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.ExecutablePath -eq '$wstage\\host\\OniMoono.exe' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force; \$_.ProcessId }" 2> /dev/null | tr -d '\r' | xargs) || true
        [ -z "$left" ] && pass "windows: no crash window left open" || fail "windows: crash watchers left running (ended): $left"
        crash_window
    fi
fi
echo "check-native-crash: $ok ok, $bad failed"
[ $bad -eq 0 ]
