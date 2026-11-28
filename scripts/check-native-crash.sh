#!/usr/bin/env bash
# The native crash records (ARCHITECTURE §7, "Native crashes"): the player started with
# --test-native-crash WHERE for each kind of crash, its Player.log checked for the crash lines and
# its dump for the fault. Windows when out/win/host (scripts/build-windows.sh) and Windows are
# here: each case crashes before the window opens, so nothing shows on screen and nothing plays,
# started as Steam starts it (no standard streams), in its own run home under %TEMP%. Linux with
# the debug player's own executable (out/native/linux/OniMoono, scripts/build-native-host-linux.sh,
# copied over the apphost by the build): each case in its own run home, standard error
# redirected, headless; under the SDK's apphost only the runtime's own dump recipe. Both check
# the crash window: no case leaves one open (a run with --run-home shows none), and its drawing
# (--crash-dialog-render, nothing shown), zip and issue link are checked offscreen; on Linux
# also live under Xvfb, driven by key presses (XTest).
#   scripts/check-native-crash.sh [linux|windows|both]
# SC2015: `TEST && pass || fail` is an if/else here, as pass always succeeds.
# shellcheck disable=SC2015
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
    export ONI_UPDATE_RELEASES_URL=none
    if ! grep -q moono_crash_install "$player"; then
        echo "linux: the debug player is the SDK's apphost (no out/native/linux/OniMoono): the runtime's own dump recipe only"
        status=0
        "$player" --game "$game" --log "$tmp/null.log" --test-native-crash managed-null > /dev/null 2>&1 || status=$?
        [ $status -eq 0 ] && pass "linux managed-null: exit 0" || fail "linux managed-null: exit $status"
        refuse "linux managed-null: no crash line" "$tmp/null.log" '\[engine\] native crash:'
        # No core file, and the shell's "Segmentation fault" kept off the output.
        status=$( (ulimit -c 0; DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_EnableCrashReport=1 DOTNET_DbgMiniDumpName="$tmp/crash-%p.dmp" \
            "$player" --game "$game" --log "$tmp/main.log" --test-native-crash main > "$tmp/main.out" 2>&1; echo $?) 2> /dev/null)
        [ "$status" -eq 139 ] && pass "linux main: SIGSEGV" || fail "linux main: exit $status"
        report=$(ls "$tmp"/crash-*.dmp.crashreport.json 2>/dev/null | head -1)
        [ -n "$report" ] && grep -q 'libc' "$report" && pass "linux main: crash report names libc" || fail "linux main: no crash report naming libc ($(ls "$tmp"))"
    else
        linux_launcher=1
        # lrun CASE [ARGS...]: one headless player, in its own run home (NAME: $name, else the
        # case), its standard error to a file; the log's folder, the newest dump and crash report.
        lrun() {
            local c=$1 home; shift
            home="$tmp/${name:-$c}"
            mkdir -p "$home/log"
            lstatus=0
            "$player" --game "$game" --run-home "$home" --log "$home/log/Player.log" --headless --frames 300 --game-tweak none --splash none \
                --test-native-crash "$c" "$@" > "$home.out" 2> "$home.err" < /dev/null || lstatus=$?
            ldir="$home/log"; log="$ldir/Player.log"
            dump=$(ls -t "$ldir"/crash-*.dmp 2> /dev/null | head -1) || true
            report=$(ls -t "$ldir"/crash-*.crashreport.json 2> /dev/null | head -1) || true
        }
        lexit() { [ "$lstatus" -eq "$2" ] && pass "linux $1: exit $2" || fail "linux $1: exit $lstatus, not $2"; }
        ldumped() { # ldumped CASE: the dump line, an ELF core with its crash report beside it
            expect "linux $1: dump line" "$log" 'native crash dump: .*crash-[0-9]{8}-[0-9]{6}\.dmp \([0-9]+ KB\)'
            if [ -n "$dump" ] && [ "$(head -c 4 "$dump" | od -An -c | tr -d ' ')" = '177ELF' ] && [ -n "$report" ]; then
                pass "linux $1: dump an ELF core with its crash report ($(du -k "$dump" | cut -f1) KB)"
            else fail "linux $1: dump ${dump:-none}, crash report ${report:-none}"; fi
        }
        lcrashed() { # lcrashed CASE SIGNAL MEANING THREAD: the fault line, the stack, the end, the dump
            expect "linux $1: fault line" "$log" "native crash: $2 $3( \([^)]*\))? in [A-Za-z0-9_.+-]+\+0x[0-9A-F]+ \(build-id [0-9a-f]+, [^)]+\) on thread [0-9]+$4"
            expect "linux $1: stack" "$log" 'native crash stack: [A-Za-z0-9_.+-]+\+0x'
            expect "linux $1: end" "$log" "native crash: the process ended with $2"
            ldumped "$1"
        }
        lrun managed-null; lexit managed-null 0
        expect "linux managed-null: caught" "$log" 'test native crash: the null reference was caught'
        refuse "linux managed-null: no crash line" "$log" '\[engine\] native crash:'
        lrun main; lexit main 139; lcrashed main SIGSEGV 'invalid memory access' ' \(main\)'
        expect "linux main: in libc, reading" "$log" 'native crash: SIGSEGV invalid memory access \(address not mapped, reading 0x1\) in libc\.so\.6\+0x'
        expect "linux main: managed frames from the crash report" "$log" 'native crash: managed: +at OniMoono\.Host\.NativeCrashes\.Fault'
        refuse "linux main: no home path (hidden as ~)" "$log" "$HOME/"
        lrun worker; lexit worker 139; lcrashed worker SIGSEGV 'invalid memory access' ' "Test worker"'
        lrun thread; lexit thread 139; lcrashed thread SIGSEGV 'invalid memory access' '$'
        expect "linux thread: in the launcher, writing" "$log" 'native crash: SIGSEGV invalid memory access \(address not mapped, writing 0x0\) in OniMoono\+0x'
        lrun stack-overflow; lexit stack-overflow 139; lcrashed stack-overflow SIGSEGV 'invalid memory access' '$'
        expect "linux stack-overflow: the recursion counted" "$log" 'native crash stack: .*\(x[0-9]{2,}\)'
        lrun main-stack-overflow; lexit main-stack-overflow 134
        expect "linux main-stack-overflow: runtime text" "$log" 'native crash: runtime: (Stack overflow\.|Fatal error\.)'
        expect "linux main-stack-overflow: end" "$log" 'native crash: the process ended with SIGABRT'; ldumped main-stack-overflow
        lrun abort; lexit abort 134; lcrashed abort SIGABRT abort ' \(main\)'
        lrun heap; lexit heap 134; lcrashed heap SIGABRT abort ' \(main\)'
        expect "linux heap: glibc's line kept" "$log" 'native crash: runtime: free\(\): double free detected'
        expect "linux heap: passed on" "$tmp/heap.err" '^free\(\): double free detected'
        lrun failfast; lexit failfast 134
        expect "linux failfast: runtime text" "$log" 'native crash: runtime: Process terminated\.'
        expect "linux failfast: end" "$log" 'native crash: the process ended with SIGABRT'; ldumped failfast
        lrun exit; lexit exit 3
        expect "linux exit: end" "$log" "native crash: the process ended with 3 without the player's own exit"
        [ -z "$dump" ] && pass "linux exit: no dump" || fail "linux exit: a dump"
        lrun kill9; lexit kill9 137
        expect "linux kill9: end" "$log" 'native crash: the process ended with SIGKILL \(killed\); from outside'
        # --crash-dumps off: the crash report alone; full: a Heap dump, many times the Mini's size.
        small=$(du -k "$(ls "$tmp"/main/log/crash-*.dmp | head -1)" | cut -f1)
        name=off lrun main --crash-dumps off; lexit "main --crash-dumps off" 139
        [ -z "$dump" ] && [ -n "$report" ] && pass "linux --crash-dumps off: the crash report alone" || fail "linux --crash-dumps off: dump ${dump:-none}, report ${report:-none}"
        expect "linux --crash-dumps off: its stack" "$log" 'native crash stack: libc\.so\.6\+0x'
        name=full lrun main --crash-dumps full; lexit "main --crash-dumps full" 139
        full=$(du -k "$dump" 2> /dev/null | cut -f1)
        [ "${full:-0}" -gt $(( ${small:-0} * 3 )) ] && pass "linux --crash-dumps full: memory in the dump ($full KB, $small KB without)" \
            || fail "linux --crash-dumps full: $full KB, not much more than the $small KB without"
        # The two newest dumps kept, with their crash reports: three crashes in one run home after
        # an old dump, beside files not named as ours.
        pdir="$tmp/prune/log"; mkdir -p "$pdir"
        echo old > "$pdir/crash-20200101-000000.dmp"; echo old > "$pdir/crash-20200101-000000.crashreport.json"
        echo other > "$pdir/crash-old.dmp"; mkdir -p "$pdir/crash-20200102-000000.dmp"
        for c in main worker abort; do name=prune lrun $c; sleep 1; done
        left=$(cd "$pdir" && find . -maxdepth 1 -type f -printf '%f\n' | grep -cE '^crash-[0-9]{8}-[0-9]{6}\.dmp$')
        reports=$(cd "$pdir" && find . -maxdepth 1 -type f -printf '%f\n' | grep -cE '^crash-[0-9]{8}-[0-9]{6}\.crashreport\.json$')
        [ "$left" = 2 ] && [ "$reports" = 2 ] && [ ! -e "$pdir/crash-20200101-000000.dmp" ] && pass "linux prune: the two newest dumps and their reports kept" \
            || fail "linux prune: $(ls "$pdir" | tr '\n' ' ')"
        [ -f "$pdir/crash-old.dmp" ] && [ -d "$pdir/crash-20200102-000000.dmp" ] && pass "linux prune: other files and folders kept" || fail "linux prune: $(ls "$pdir" | tr '\n' ' ')"
        # Nothing left: each run has a run home of its own, which shows no window, so the watcher
        # ends with its game (the games' command lines name the run homes; the watchers' hide them).
        # shellcheck disable=SC2009  # ps and grep [x], not pgrep, which could match this script
        left=$(ps -eo pid,args | grep -F "$tmp/" | grep -v -e grep -e check-native-crash | awk '{print $1}' | xargs) || true
        [ -z "$left" ] && pass "linux: no game or watcher left running" || fail "linux: left running: $left"
    fi
fi

# The crash window drawn offscreen (--crash-dialog-render): the PNG's size at the scale and the
# palette's colours where the title bar, the body and the footer are; then the zip "Report on
# GitHub" writes (deflate, checked by Python's zipfile) and the issue link (under 7,500 characters
# with a long note, the error in its title).
png_check() { # png_check NAME FILE WIDTH CAPTION BODY FOOTER [X0,Y0,X1,Y1: a box drawn in, not the body's colour] (colours as RRGGBB)
    local out; out=$(python3 -I - "$@" <<'PY'
import struct, sys, zlib
name, path, width, caption, body, footer = sys.argv[1:7]
drawn = [int(v) for v in sys.argv[7].split(',')] if len(sys.argv) > 7 else None
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
if drawn:
    x0, y0, x1, y1 = drawn
    if sum(1 for y in range(y0, y1) for x in range(x0, x1) if px(x, y) != body) < (x1 - x0) * (y1 - y0) // 8: problems.append('nothing drawn in %s' % sys.argv[7])
print('; '.join(problems) if problems else 'ok %dx%d' % (w, h))
PY
)
    [ "${out%% *}" = ok ] && pass "${platform:-windows} crash window $1: $out" || fail "${platform:-windows} crash window $1: $out"
}
# zip_input DIR: a 40 MB log (cut to its head and tail by the zip) and a dump of zeros and noise.
zip_input() {
    python3 -I - "$1" <<'PY'
import os, random, sys
os.makedirs(sys.argv[1], exist_ok=True); random.seed(7)
with open(os.path.join(sys.argv[1], 'Player.log'), 'wb') as f:
    for i in range(700000): f.write(b'[engine] line %d of the log, with some text that repeats %d\r\n' % (i, random.randrange(1000)))
with open(os.path.join(sys.argv[1], 'crash-20261107-141502.dmp'), 'wb') as f:
    f.write(b'MDMP' + bytes(1 << 20) + random.randbytes(1 << 19))
PY
}
# zip_checks DIR TITLE ERROR: DIR/crash.zip against DIR/zip-in, DIR/url.txt's link (its title and
# the start of its details).
zip_checks() {
    local zipped; zipped=$(python3 -I - "$1/crash.zip" "$1/zip-in" <<'PY'
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
    [ "${zipped%% *}" = ok ] && pass "${platform:-windows} crash window zip: $zipped" || fail "${platform:-windows} crash window zip: $zipped"
    local linked; linked=$(python3 -I - "$1/url.txt" "$2" "$3" <<'PY'
import sys, urllib.parse
u = open(sys.argv[1], encoding='utf-8').read(); q = urllib.parse.parse_qs(urllib.parse.urlsplit(u).query)
if not u.startswith('https://github.com/tigin-backwards/oni-moono/issues/new?template=crash.yml&'): print('not the crash form'); sys.exit()
if len(u) > 7500: print('%d characters' % len(u)); sys.exit()
if q.get('title') != [sys.argv[2]]: print('title', q.get('title')); sys.exit()
if sorted(q) != ['details', 'doing', 'template', 'title']: print('fields', sorted(q)); sys.exit()
details, doing = q['details'][0], q['doing'][0]
if not details.startswith(sys.argv[3] + '\n') or 'game build 762230' not in details: print('details: ' + details[:200]); sys.exit()
print('ok %d characters' % len(u) if doing.startswith('what I was doing') and doing.endswith('\u2026') else 'doing: ' + doing[:200])
PY
)
    [ "${linked%% *}" = ok ] && pass "${platform:-windows} crash window link: $linked" || fail "${platform:-windows} crash window link: $linked"
}
# xvfb DISPLAY keys KEY... (X keysym names, text:WORDS) or xvfb DISPLAY shown FBDIR: XTest key
# presses, or whether Xvfb's frame buffer (its -fbdir XWD file) shows anything on its black root.
xvfb() {
    python3 -I - "$@" <<'PY'
import ctypes, struct, sys, time
display, what = sys.argv[1], sys.argv[2]
if what == 'shown':
    d = open(sys.argv[3] + '/Xvfb_screen0', 'rb').read()
    f = struct.unpack_from('>25I', d, 0)
    print('yes' if d[f[0] + f[19] * 12:].strip(b'\0') else 'no'); sys.exit()
x = ctypes.CDLL('libX11.so.6'); xt = ctypes.CDLL('libXtst.so.6')
x.XOpenDisplay.restype = ctypes.c_void_p; x.XStringToKeysym.restype = ctypes.c_ulong
x.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]; x.XFlush.argtypes = [ctypes.c_void_p]
xt.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
dpy = x.XOpenDisplay(display.encode())
shift = x.XKeysymToKeycode(dpy, x.XStringToKeysym(b'Shift_L'))
def press(sym, shifted=False):
    code = x.XKeysymToKeycode(dpy, x.XStringToKeysym(sym.encode()))
    if shifted: xt.XTestFakeKeyEvent(dpy, shift, 1, 0)
    xt.XTestFakeKeyEvent(dpy, code, 1, 0); xt.XTestFakeKeyEvent(dpy, code, 0, 0)
    if shifted: xt.XTestFakeKeyEvent(dpy, shift, 0, 0)
    x.XFlush(dpy); time.sleep(0.03)
for name in sys.argv[3:]:
    if name.startswith('text:'):
        for c in name[5:]: press('space' if c == ' ' else c.lower() if c.isalpha() else c, c.isupper())
    else: press(name)
time.sleep(0.3)
PY
}
# The Linux crash window: offscreen renders (as Windows'), the zip and the link; then live under
# Xvfb (no window manager, so the keys reach the window under the pointer, the screen's centre,
# where it opens): a crash with the window on, a note typed, Enter (Report on GitHub: the zip, its
# copy in Downloads, the link opened, recorded through ONI_MOONO_CRASH_OPEN), Esc (the watcher
# ends with the game's status).
linux_crash_window() {
    local platform=linux lstage="$tmp/window"
    mkdir -p "$lstage"
    lrender() { timeout 60 "$player" --crash-dialog-render "$@" > /dev/null 2>&1 < /dev/null; }
    # The Gassy Moo left of the headline, as on Windows: 88 x 56 DIPs under the 32-DIP title bar.
    lrender "$lstage/dark.png" --theme dark --scale 1.5 --sample gpu --details-open && png_check "dark, 150%, details" "$lstage/dark.png" 660 100C0A 191512 1D1A17 30,78,162,162 \
        || fail "linux crash window: dark render"
    lrender "$lstage/light.png" --theme light --scale 1.25 --sample managed --language zh --reported && png_check "light, 125%, Chinese" "$lstage/light.png" 550 F7F4ED FFFCF6 F7F4ED \
        || fail "linux crash window: light render"
    lrender "$lstage/text.png" --theme dark --scale 1 --text-size 2.25 --sample managed --details-open && png_check "dark, 100%, text size 225%" "$lstage/text.png" 715 100C0A 191512 1D1A17 \
        || fail "linux crash window: text size render"
    zip_input "$lstage/zip-in"
    lrender "$lstage/zip.png" --zip "$lstage/crash.zip" --log "$lstage/zip-in/Player.log" --dump "$lstage/zip-in/crash-20261107-141502.dmp" \
        --url "$lstage/url.txt" --note "$(printf 'what I was doing %.0s' $(seq 600))" || fail "linux crash window: zip and link"
    zip_checks "$lstage" 'Crash: SIGSEGV (invalid memory access) in libnvidia-glcore.so.580.95.05' 'SIGSEGV (invalid memory access) in libnvidia-glcore.so.580.95.05'
    # The page for a file Windows blocked lays out the same here (#135): the Gassy Moo, the README's link.
    lrender "$lstage/blocked.png" --theme light --scale 1 --sample blocked --url "$lstage/blocked-url.txt" \
        && png_check "blocked file, light" "$lstage/blocked.png" 440 F7F4ED FFFCF6 F7F4ED 20,52,108,108 || fail "linux crash window: blocked render"
    local link; link=$(cat "$lstage/blocked-url.txt" 2> /dev/null) || true
    [ "$link" = 'https://github.com/tigin-backwards/oni-moono#unsigned-releases' ] && pass "linux crash window: a blocked file's button opens the README, no issue" \
        || fail "linux crash window: a blocked file's link: ${link:-none}"
    if ! command -v Xvfb > /dev/null; then echo "linux crash window live: skipped, no Xvfb"; return; fi
    local lane="${RUNQ_LANES%%,*}" display xpid gpid status=0 shown=no
    display=":$((300 + ${lane:-9}))"
    mkdir -p "$lstage/fb" "$lstage/home/Downloads" "$lstage/live"
    Xvfb "$display" -screen 0 1280x800x24 -fbdir "$lstage/fb" -nolisten tcp > /dev/null 2>&1 & xpid=$!
    sleep 1
    DISPLAY="$display" HOME="$lstage/home" ONI_MOONO_CRASH_DIALOG=on ONI_MOONO_CRASH_OPEN="$lstage/opened.txt" \
        "$player" --game "$game" --run-home "$lstage/live" --log "$lstage/live/Player.log" --headless --frames 300 --game-tweak none --splash none \
        --test-native-crash main > /dev/null 2>&1 < /dev/null & gpid=$!
    for _ in $(seq 150); do sleep 0.1; shown=$(xvfb "$display" shown "$lstage/fb"); [ "$shown" = yes ] && break; done
    [ "$shown" = yes ] && pass "linux crash window live: shown" || fail "linux crash window live: not shown"
    sleep 0.5
    xvfb "$display" keys "text:Checking the crash window" Return; sleep 1.5
    xvfb "$display" keys Escape
    for _ in $(seq 100); do kill -0 $gpid 2> /dev/null || break; sleep 0.1; done
    kill -0 $gpid 2> /dev/null && { fail "linux crash window live: Esc did not close it"; kill $gpid; }
    wait $gpid || status=$?
    kill $xpid 2> /dev/null; wait $xpid 2> /dev/null || true
    [ $status -eq 139 ] && pass "linux crash window live: the watcher ends with the game's 139" || fail "linux crash window live: exit $status"
    local zip; zip=$(ls "$lstage"/live/OniMoono-crash-*.zip 2> /dev/null | head -1) || true
    if [ -n "$zip" ] && python3 -I -c "import sys, zipfile; z = zipfile.ZipFile(sys.argv[1]); sys.exit(z.testzip() is not None or sorted(n[:6] for n in z.namelist()) != ['Player', 'crash-'])" "$zip"; then
        pass "linux crash window live: the zip ($(du -k "$zip" | cut -f1) KB, the log and the dump)"
    else fail "linux crash window live: zip ${zip:-none}"; fi
    [ -n "$zip" ] && cmp -s "$zip" "$lstage/home/Downloads/$(basename "$zip")" && pass "linux crash window live: a copy in Downloads" || fail "linux crash window live: no copy in Downloads"
    expect "linux crash window live: the issue opened" "$lstage/opened.txt" '^url https://github\.com/tigin-backwards/oni-moono/issues/new\?template=crash\.yml&title=Crash%3A%20SIGSEGV.*&doing=Checking%20the%20crash%20window'
}
crash_window() {
    local exe="$stage/host/OniMoono.exe"
    render() { (cd /mnt/c && timeout 60 "$exe" --crash-dialog-render "$@" > /dev/null 2>&1) || return 1; }
    # The Gassy Moo left of the headline: 88 x 56 DIPs under the 32-DIP title bar.
    render "$wstage\\dark.png" --theme dark --scale 1.5 --sample gpu --details-open && png_check "dark, 150%, details" "$stage/dark.png" 660 100C0A 191512 1D1A17 30,78,162,162 \
        || fail "windows crash window: dark render"
    render "$wstage\\light.png" --theme light --scale 1.25 --sample managed --language zh --reported && png_check "light, 125%, Chinese" "$stage/light.png" 550 F7F4ED FFFCF6 F7F4ED \
        || fail "windows crash window: light render"
    # Windows' text size at its largest: the window widens by half as much (440 x 1.625).
    render "$wstage\\text.png" --theme dark --scale 1 --text-size 2.25 --sample managed --details-open && png_check "dark, 100%, text size 225%" "$stage/text.png" 715 100C0A 191512 1D1A17 \
        || fail "windows crash window: text size render"
    # The zip: a 40 MB log (cut to its head and tail) and a dump of zeros and noise.
    zip_input "$stage/zip-in"
    render "$wstage\\zip.png" --zip "$wstage\\crash.zip" --log "$wstage\\zip-in\\Player.log" --dump "$wstage\\zip-in\\crash-20261107-141502.dmp" \
        --url "$wstage\\url.txt" --note "$(printf 'what I was doing %.0s' $(seq 600))" || fail "windows crash window: zip and link"
    zip_checks "$stage" 'Crash: 0xC0000005 (access violation) in nvoglv64.dll' '0xC0000005 (access violation) in nvoglv64.dll'
    # A file Windows' application control blocked (#135): a different page, with the Gassy Moo, and its
    # button the README's link, not an issue.
    render "$wstage\\blocked.png" --theme light --scale 1 --sample blocked --url "$wstage\\blocked-url.txt" \
        && png_check "blocked file, light" "$stage/blocked.png" 440 F7F4ED FFFCF6 F7F4ED 20,52,108,108 || fail "windows crash window: blocked render"
    render "$wstage\\blocked-dark.png" --theme dark --scale 2 --sample blocked --details-open \
        && png_check "blocked file, dark, 200%, details" "$stage/blocked-dark.png" 880 100C0A 191512 1D1A17 40,104,216,216 || fail "windows crash window: blocked dark render"
    local link; link=$(cat "$stage/blocked-url.txt" 2> /dev/null) || true
    [ "$link" = 'https://github.com/tigin-backwards/oni-moono#unsigned-releases' ] && pass "windows crash window: a blocked file's button opens the README, no issue" \
        || fail "windows crash window: a blocked file's link: ${link:-none}"

}

[ -n "${linux_launcher:-}" ] && linux_crash_window

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
            # shellcheck disable=SC2016  # PowerShell's $true, written literally
            local home="$wstage\\${name:-$1${2:+-$2}${extra:+-full}}" shell='$true' redirect=''
            # shellcheck disable=SC2016  # PowerShell's $false and $psi, written literally
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
        # shellcheck disable=SC1003,SC2001  # backslashes in a regex, not quote escapes; sed escapes each one
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
        # With Windows Error Reporting on, as in play: its record names the module and offset, and
        # the instruction there the fast fail's code.
        run fastfail-reported && { expect "windows fastfail-reported: fault line" "$log" \
                'native crash: 0xC0000409 fast fail 7 \(abort\(\) or a fatal app exit\) in OniMoono\.exe\+0x[0-9A-F]+ \([0-9.]+, [^)]+\), as Windows Error Reporting recorded it'
            expect "windows fastfail-reported: end" "$log" 'native crash: the process ended with 0xC0000409 \(fast fail.*\); a fast fail skips every handler and leaves no dump'
            [ -z "$dump" ] && pass "windows fastfail-reported: no dump" || fail "windows fastfail-reported: a dump"; }
        run terminate && expect "windows terminate: end" "$log" "native crash: the process ended with 1 without the player's own exit"
        # A fault noted at first chance (a stack overflow a native thread handles), then an end
        # with another code no handler saw (#117): the noted fault kept as an earlier one, the end
        # still unexplained; no dump, as the game was gone before anything asked for one.
        run earlier-fault && { expect "windows earlier-fault: earlier fault" "$log" 'native crash: an earlier fault, [0-9]+\.[0-9] s before the end \(native code may have handled it\):[[:space:]]*$'
            grep -A1 'native crash: an earlier fault' "$log" > "$stage/earlier-fault.txt" || true
            expect "windows earlier-fault: its fault line next" "$stage/earlier-fault.txt" 'native crash: 0xC00000FD stack overflow in OniMoono\.exe\+0x[0-9A-F]+ \([0-9.]+, [^)]+\) on thread [0-9]+'
            expect "windows earlier-fault: its stack" "$log" 'native crash stack: .*OniMoono\.exe\+0x[0-9A-F]+ \(x[0-9]{2,}\)'
            expect "windows earlier-fault: end" "$log" 'native crash: the process ended with 0xC0000005 \(access violation\); no handler saw it[[:space:]]*$'
            [ -z "$dump" ] && pass "windows earlier-fault: no dump" || fail "windows earlier-fault: a dump"; }
        run failfast && { expect "windows failfast: runtime text" "$log" 'native crash: runtime: Process terminated\.'
            expect "windows failfast: end" "$log" 'native crash: the process ended with 0x80131623'; dumped failfast none; }
        # Windows' application control refusing a player assembly (#135): the runtime's text, and
        # the record saying so plainly (the window shows its page for it: see crash_window).
        run blocked && { expect "windows blocked: runtime text" "$log" 'native crash: runtime: Unhandled exception\. System\.IO\.FileLoadException: .*Brutal\.Vulkan\.dll.*\(0x800711C7\)'
            expect "windows blocked: said plainly" "$log" 'native crash: Windows blocked Brutal\.Vulkan\.dll \(an application control policy'; }
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
