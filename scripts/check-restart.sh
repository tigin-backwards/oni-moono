#!/usr/bin/env bash
# The game's App.Restart (ARCHITECTURE, "The host directory carries a Restarter"), in two
# layouts made from the build output under a temporary folder:
# - versioned: a versioned install (docs/INSTALL-MODES.md): the entry point, player/VERSION/ with
#   a mod_info.yaml, state/current, and no Steam (an empty ONI_MOONO_STEAM_ROOTS); on Linux both
#   entry points, the shell script and Moono's own executable;
# - player: a player folder started directly, as Steam's launch options start one.
# Each is started as Steam starts it, `OPTIONS -- GAME_EXE GAME_ARGS`, the game's executable under
# a "Program Files (x86)" path (a link to the game) and game arguments with a space, a quote and a
# trailing backslash, with ONI_TEST_APP_RESTART, which calls the game's own App.Restart once
# App.instance exists. Checked from both runs' logs: the first recorded exactly those arguments,
# App.Restart's Restarter started the entry point (versioned) or the player (player) again, and
# the second run had the same arguments and game arguments, chose its version again (versioned)
# and ended by itself (--frames). Windows also: Restarter.exe went in beside the entry point.
#   scripts/check-restart.sh linux     the Linux build (out/bin/OniMoono.Host/debug), headless
#   scripts/check-restart.sh windows   out/win/host (scripts/build-windows.sh) on the frozen
#                                      build in E:\oni-builds (ONI_WIN_GAME_DIR names another)
# Run it through runq: scripts/runq run --kind linux-player|windows -- scripts/check-restart.sh ...
# Nothing of the user's install (%LOCALAPPDATA%\oni-moono), game data or Steam is read or
# written. The Windows player is muted, windowed on monitor 2 without the focus.
# SC2015: `TEST && pass || fail` is an if/else here, as pass always succeeds.
# shellcheck disable=SC2015
set -euo pipefail
source "$(dirname "$0")/env.sh"
[ -n "${ONI_CONTAINED:-}" ] || exec "$root/scripts/contain.sh" "$0" "$@"
which="${1:?usage: check-restart.sh linux|windows}"
ok=0; bad=0
pass() { echo "ok   $1"; ok=$((ok + 1)); }
fail() { echo "FAIL $1"; bad=$((bad + 1)); }
expect() { # expect NAME FILE TEXT: a line of FILE holds TEXT
    if grep -Fq -- "$3" "$2" 2> /dev/null; then pass "$1"; else fail "$1: no line with $3"; fi
}
refuse() { # refuse NAME FILE TEXT: no line of FILE holds TEXT
    if grep -Fq -- "$3" "$2" 2> /dev/null; then fail "$1: $(grep -Fm1 -- "$3" "$2")"; else pass "$1"; fi
}
line_of() { grep -Fm1 -- "$2" "$1" 2> /dev/null | sed 's/^[^]]*\] //'; }
same_line() { # same_line NAME FIRST SECOND TEXT: the line with TEXT is in both, the same
    local a b; a=$(line_of "$2" "$4"); b=$(line_of "$3" "$4")
    if [ -n "$a" ] && [ "$a" = "$b" ]; then pass "$1"; else fail "$1: first: ${a:-none}; second: ${b:-none}"; fi
}
# recorded NAME LOG ARG...: the log's restart line records exactly ARG... (its JSON array).
recorded() {
    local name="$1" log="$2"; shift 2
    local out; out=$(python3 -I - "$log" "$@" <<'PY'
import json, sys
line = next((l for l in open(sys.argv[1], encoding='utf-8', errors='replace') if 'restart: starts ' in l), None)
if line is None: print('no restart line'); sys.exit()
got = json.loads(line[line.index(', arguments ') + 12:].strip())
want = sys.argv[2:]
print('ok %d arguments' % len(got) if got == want else 'recorded %s, not %s' % (json.dumps(got), json.dumps(want)))
PY
)
    [ "${out%% *}" = ok ] && pass "$name: $out" || fail "$name: $out"
}
# The Linux entry point: PlayerFiles.EntryScript as the C# raw string literal gives it (its lines
# less the closing quotes' indentation).
entry_script() {
    python3 -I - "$root/src/OniMoono.Host/Install/PlayerFiles.cs" <<'PY'
import re, sys
s = open(sys.argv[1], encoding='utf-8').read()
m = re.search(r'EntryScript = """\n(.*?)\n( *)""";', s, re.S)
print('\n'.join(l[len(m.group(2)):] for l in m.group(1).split('\n')))
PY
}
mod_info() { printf 'minimumSupportedBuild: 0\nAPIVersion: 2\nversion: "%s"\n' "$1"; }
version=2026.11.17.01-restart
frames=600
# Game arguments as a player may give them in Steam: a space, a quote, a trailing backslash.
game_args=("a b c" 'x"y' "tr\\")
# wait_runs DIR: until the second run's log (Player.log, the first's now Player-prev.log) ends.
wait_runs() {
    for _ in $(seq 600); do
        grep -q 'OnApplicationQuit' "$1/Player.log" 2> /dev/null && grep -q 'Moono starting' "$1/Player-prev.log" 2> /dev/null && break
        sleep 0.5
    done
    sleep 3
}
# checks NAME FIRST SECOND MODE START RESTARTER_DIR ARG...: what both layouts share.
checks() {
    local name="$1" first="$2" second="$3" mode="$4" start="$5" rdir="$6"; shift 6
    expect "$name first: App.Restart called" "$first" 'ONI_TEST_APP_RESTART: App.Restart'
    expect "$name first: the Restarter's folder" "$first" "dir ($rdir)"
    refuse "$name first: the Restarter started" "$first" 'Win32Exception'
    expect "$name first: restart starts $mode" "$first" "restart: starts $mode"
    recorded "$name first: the arguments as started" "$first" "$@"
    expect "$name second: started" "$second" 'Moono starting:'
    expect "$name second: from $start" "$second" "exe=$start"
    same_line "$name second: the same arguments" "$first" "$second" 'restart: starts '
    same_line "$name second: the same game arguments" "$first" "$second" 'game arguments: '
    refuse "$name second: no second restart" "$second" 'ONI_TEST_APP_RESTART'
    expect "$name second: quit by itself" "$second" 'OnApplicationQuit'
}

if [ "$which" = linux ]; then
    tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
    common="$tmp/Program Files (x86)/Steam/steamapps/common"
    mkdir -p "$common" "$tmp/steam"
    ln -s "$(cd "$game" && pwd)" "$common/OxygenNotIncluded"
    exe="$common/OxygenNotIncluded/OxygenNotIncluded"
    run() { # run LAYOUT START: from a run home of its own; its logs' folder in $logs
        local home="$tmp/home-$1"
        mkdir -p "$home/config" "$home/cache"
        logs="$home/config/unity3d/Klei/Oxygen Not Included"
        args=(--game-tweak none --splash none --log-paths full --headless --frames "$frames" -- "$exe" "${game_args[@]}")
        local status=0
        (cd "$tmp" && XDG_CONFIG_HOME="$home/config" XDG_CACHE_HOME="$home/cache" ONI_UPDATE_RELEASES_URL=none ONI_MOONO_CRASH_DIALOG=off \
            ONI_MOONO_STEAM_ROOTS="$tmp/steam" ONI_TEST_APP_RESTART=1 timeout 600 "$2" "${args[@]}" > "$home/first.out" 2>&1) || status=$?
        [ $status -eq 0 ] && pass "linux $1: first run exit 0" || fail "linux $1: first run exit $status ($(tail -3 "$home/first.out" | tr '\n' ' '))"
        wait_runs "$logs"
    }
    # Hard links: the player never writes to its folder.
    vroot="$tmp/versioned"
    mkdir -p "$vroot/player" "$vroot/state"
    cp -al "$root/out/bin/OniMoono.Host/debug" "$vroot/player/$version"
    mod_info "$version" > "$vroot/player/$version/mod_info.yaml"
    echo "$version" > "$vroot/state/current"
    entry_script > "$vroot/OniMoono"; chmod +x "$vroot/OniMoono"
    run versioned "$vroot/OniMoono"
    checks "linux versioned" "$logs/Player-prev.log" "$logs/Player.log" "the entry point $vroot/OniMoono" "$vroot/player/$version/OniMoono" \
        "$vroot/player/$version" "${args[@]}"
    expect "linux versioned second: chose its version" "$logs/Player.log" "playing version $version"
    # The entry point as the install puts it with Moono's own executable (PlayerFiles.
    # InstallLinuxEntry): that executable, which picks the version itself.
    if grep -q moono_crash_install "$vroot/player/$version/OniMoono"; then
        rm "$vroot/OniMoono"; cp "$vroot/player/$version/OniMoono" "$vroot/OniMoono"
        run versioned-native "$vroot/OniMoono"
        checks "linux versioned (native entry point)" "$logs/Player-prev.log" "$logs/Player.log" "the entry point $vroot/OniMoono" \
            "$vroot/player/$version/OniMoono" "$vroot/player/$version" "${args[@]}"
        expect "linux versioned (native entry point) second: chose its version" "$logs/Player.log" "playing version $version"
    else
        echo "linux versioned (native entry point): skipped, the build's OniMoono is the SDK's apphost"
    fi
    cp -al "$root/out/bin/OniMoono.Host/debug" "$tmp/player"
    run player "$tmp/player/OniMoono"
    checks "linux player" "$logs/Player-prev.log" "$logs/Player.log" "this executable" "$tmp/player/OniMoono" "$tmp/player" "${args[@]}"
fi

if [ "$which" = windows ]; then
    host="$root/out/win/host"
    [ -f "$host/OniMoono.exe" ] && [ -f "$host/Restarter.dll" ] || { echo "windows: no out/win/host (scripts/build-windows.sh)"; exit 1; }
    # The frozen Windows build of the Linux game this branch tests (~/projects/oni-linux is U59's).
    build="$(basename "$game" | sed -n 's/^oni-linux-\([0-9]*\)$/\1/p')"
    wgame="${ONI_WIN_GAME_DIR:-E:\\oni-builds\\oni-win-${build:-744825}}"
    wtemp="$(cd /mnt/c && cmd.exe /c 'echo %TEMP%' 2> /dev/null | tr -d '\r')"
    wtmp="$wtemp\\oni-moono-restart-check-$$"
    tmp="$(wslpath "$wtmp")"
    wcommon="$wtmp\\Program Files (x86)\\Steam\\steamapps\\common"
    # The junction goes first, so nothing removes the game's files through it.
    trap '(cd /mnt/c && cmd.exe /c rmdir "$wcommon\\OxygenNotIncluded" > /dev/null 2>&1); rm -rf "$tmp"' EXIT
    mkdir -p "$(wslpath "$wcommon")" "$tmp/steam" "$tmp/stub"
    (cd /mnt/c && cmd.exe /c mklink /J "$wcommon\\OxygenNotIncluded" "$wgame" > /dev/null) || { echo "windows: no junction to $wgame"; exit 1; }
    exe="$wcommon\\OxygenNotIncluded\\OxygenNotIncluded.exe"
    python3 -I "$root/tools/SteamStub/gen.py" "$root/src/ThirdParty/Steamworks.NET/Runtime/autogen/NativeMethods.cs" "$tmp/stub/steam_api64.dll" > /dev/null
    export ONI_MOONO_STEAM_ROOTS="$wtmp\\steam" ONI_TEST_APP_RESTART=1 ONI_HOST_NO_DIALOG=1 ONI_UPDATE_RELEASES_URL=none
    export WSLENV="${WSLENV:+$WSLENV:}ONI_MOONO_STEAM_ROOTS:ONI_TEST_APP_RESTART:ONI_HOST_NO_DIALOG:ONI_UPDATE_RELEASES_URL"
    run() { # run LAYOUT START: from a run home of its own; its logs' folder in $logs
        local whome="$wtmp\\home-$1"
        mkdir -p "$(wslpath "$whome")"
        python3 -I "$root/scripts/runqlib/winenv.py" mute "$(wslpath "$whome")" 2> /dev/null || true
        logs="$(wslpath "$whome")/AppData/LocalLow/Klei/Oxygen Not Included"
        args=(--run-home "$whome" --audio-wav "$whome" --game-tweak none --splash none --log-paths full
            --native-override "steam_api64=$wtmp\\stub\\steam_api64.dll" --no-focus --frames "$frames"
            -- "$exe" -screen-fullscreen 0 -monitor 2 "${game_args[@]}")
        # The command line as Windows splits it back into exactly these arguments (the C
        # runtime's rules, as Steam's and .NET's quoting give them), started without a shell.
        local line; line=$(python3 -I -c '
import sys
def q(a):
    if a and not any(c in a for c in " \t\"\\"): return a
    out, bs = "\"", 0
    for c in a:
        if c == "\\": bs += 1; continue
        out += "\\" * (bs * 2 + 1 if c == "\"" else bs) + c; bs = 0
    return out + "\\" * (bs * 2) + "\""
print(" ".join(q(a) for a in sys.argv[1:]).replace("'"'"'", "'"''"'"))' "${args[@]}")
        local status=0
        (cd /mnt/c && timeout 600 powershell.exe -NoProfile -Command "
            \$psi = New-Object System.Diagnostics.ProcessStartInfo
            \$psi.FileName = '$2'
            \$psi.Arguments = '$line'
            \$psi.UseShellExecute = \$false
            \$p = [System.Diagnostics.Process]::Start(\$psi)
            \$p.WaitForExit(); exit \$p.ExitCode" > "$tmp/first-$1.out" 2>&1) || status=$?
        [ $status -eq 0 ] && pass "windows $1: first run exit 0" || fail "windows $1: first run exit $status ($(tail -3 "$tmp/first-$1.out" | tr -d '\r' | tr '\n' ' '))"
        wait_runs "$logs"
    }
    vroot="$tmp/versioned"; wroot="$wtmp\\versioned"
    mkdir -p "$vroot/player" "$vroot/state"
    cp -r "$host" "$vroot/player/$version"
    mod_info "$version" > "$vroot/player/$version/mod_info.yaml"
    printf '%s\n' "$version" > "$vroot/state/current"
    cp "$host/OniMoono.exe" "$vroot/OniMoono.exe"
    run versioned "$wroot\\OniMoono.exe"
    checks "windows versioned" "$logs/Player-prev.log" "$logs/Player.log" "the entry point $wroot\\OniMoono.exe" "$wroot\\OniMoono.exe" "$wroot" "${args[@]}"
    expect "windows versioned second: chose its version" "$logs/Player.log" "playing version $version"
    cmp -s "$vroot/Restarter.exe" "$host/OniMoono.exe" && pass "windows versioned: Restarter.exe beside the entry point is the native host" \
        || fail "windows versioned: no Restarter.exe beside the entry point ($(ls "$vroot" | tr '\n' ' '))"
    cp -r "$host" "$tmp/player"
    run player "$wtmp\\player\\OniMoono.exe"
    checks "windows player" "$logs/Player-prev.log" "$logs/Player.log" "this executable" "$wtmp\\player\\OniMoono.exe" "$wtmp\\player" "${args[@]}"
fi

echo "check-restart $which: $ok ok, $bad failed"
[ $bad -eq 0 ]
