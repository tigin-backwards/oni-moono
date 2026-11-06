#!/usr/bin/env bash
# The QA harness: exercises what the engine and the game tweaks do, and fails on a regression.
#   scripts/qa.sh [--bless] [STAGE...]        (stages default to all of them, in this order)
# Stages:
#   unit     the build, both test suites and scripts/check.sh (API conformance, type loading,
#            assets, binding).
#   dogfood  scripted sessions through the game, each a scenario (scripts/qa-scenarios, or a
#            scripts/m9-scenarios name): the menu, a new game to embark, a colony's screens and
#            overlays, typing, save and load, a window resize, clicks within one frame. Fails on a
#            log exception or error whose signature the reference lacks, or a capture that differs
#            from the reference beyond the scenario's tolerance.
#   verify   U57 with every game tweak and every engine verify mode, for at least 10 game minutes:
#            every check reports zero mismatches.
#   perf     U57 with every game tweak: peak memory under 16 GB, the steady frame rate no lower than
#            the reference's less 15% (the machine's noise), multi-target framebuffers not piling up.
#            It holds the machine (see quiet_begin): new e2e specs wait for it.
#   memory   (not in the default list; also --memory or QA_MEMORY_ONLY=1, which runs it alone) U57's
#            peak resident memory only, under 16 GB: no perf lock, no wait for a quiet machine, and
#            it stops once the first autosave has passed and RSS has settled. It reports the
#            peak, the frame of the peak and the RssAnon/RssFile split (STAGE/memory.json).
# verify and perf need a large Spaced Out colony and its DLC content, which cannot be published:
# out/local-tools/qa.env sets QA_U57_GAME (a game folder with the DLC content), QA_U57_STEAM (a
# Steam double owning the DLC) and QA_U57_SAVE; without it those stages are skipped, and say so.
# It can also export the machine's GL settings for Xvfb (e.g. GALLIUM_DRIVER), which every run uses.
# A U57 run that logs no PERF line (PerfProbe's, every 600 frames) for QA_HANG_MIN minutes (default
# 5; the slowest legitimate gaps are about 140 s of load before the first and 110 s between two in
# the overlay sweep) is hung: scripts/hang-dump.sh takes the managed stacks of every thread,
# per-thread CPU and wait states and the log's tail into STAGE/hang, the player is stopped and the
# stage fails, naming that folder. ONI_TEST_HANG=FRAME[:spin|wait] (src/OniMoono.Host/TestHang.cs)
# hangs a player on purpose, to try it.
# --bless stores this run's captures, signatures and frame rate as the references
# (out/qa/reference), once they have been looked at; --bless-from=RUN stores a past run's.
# Players end with qa.sh, however it ends (a `timeout`, ^C, kill -9): each runs in a systemd scope
# of its own that qa.sh stops on exit, and a watcher stops when qa.sh is killed outright (see
# "the players' teardown"). scripts/e2e.py --status shows what runs and who waits for whom.
# A player is stopped with SIGTERM, which quits it at a frame boundary with its GPU work done (a
# player killed in a GPU wait on WSL was followed by a kernel soft lockup), and with SIGKILL only
# when it still runs E2E_QUIT_GRACE seconds (default 10) later. A stage's `timeout` runs inside
# its xvfb-run, so that it signals the player, not the wrapper that stops the X server.
set -uo pipefail
source "$(dirname "$0")/env.sh"
# Its builds, players and tests run in the memory-capped test slice (scripts/contain.sh).
[ -n "${ONI_CONTAINED:-}" ] || exec "$root/scripts/contain.sh" "$root/scripts/qa.sh" "$@"
export PATH="$HOME/.dotnet:$PATH"
bless=0; stages=(); bless_from=""
for a in "$@"; do
    case $a in
        --bless) bless=1 ;;
        --bless-from=*) bless_from=${a#--bless-from=} ;;
        --memory) stages+=(memory) ;;
        *) stages+=("$a") ;;
    esac
done
[ "${QA_MEMORY_ONLY:-}" = 1 ] && stages=(memory)
# --bless-from=RUN: a past run's dogfood captures and signatures, once looked at, become the
# references (without running the scenarios again).
if [ -n "$bless_from" ]; then
    status=0
    [ -d "$bless_from/dogfood" ] || { echo "FAIL: no dogfood runs in $bless_from"; exit 1; }
    for d in "$bless_from"/dogfood/*/; do
        name=$(basename "$d"); r="$root/out/qa/reference/dogfood/$name"
        if ! head -1 "$d/signatures.txt" 2>/dev/null | grep -qx 'exit 0' || ! ls "$d"/f*.png > /dev/null 2>&1; then
            echo "FAIL: $name did not end cleanly or has no captures; not blessed"; status=1; continue
        fi
        rm -rf "$r"; mkdir -p "$r"; cp "$d"/signatures.txt "$d"/f*.png "$r"/
        rm -f "$r"/f*-diff.png "$r"/moving-*.png; echo "blessed $name from $bless_from"
    done
    exit $status
fi
[ ${#stages[@]} -eq 0 ] && stages=(unit dogfood verify perf)
qa="$root/out/qa"; ref="$qa/reference"; run="$qa/run-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$run" "$ref"
[ -f "$root/out/local-tools/qa.env" ] && source "$root/out/local-tools/qa.env"
tools="$root/scripts/qa-tools.py"
failures=0
fail() { echo "FAIL $*"; echo "FAIL $*" >> "$run/summary.txt"; failures=$((failures + 1)); }
ok() { echo "ok   $*"; echo "ok   $*" >> "$run/summary.txt"; }
note() { echo "     $*"; echo "     $*" >> "$run/summary.txt"; }
# The Data Collection option off in a run's profile (KPrivacyPrefs): no test run submits a crash
# report to Klei's server, as e2e.py's profiles do.
no_data_collection() { mkdir -p "$1/Agreements/OxygenNotIncluded"; printf 'disableDataCollection: true\n' > "$1/Agreements/OxygenNotIncluded/kprivacyprefs.yaml"; }

# ---- the players' teardown ----
# Every player runs in a systemd scope of its own (scripts/contain.sh, ONI_SCOPE) and carries
# E2E_OWNER, this run's pid and start time. On any exit (the end, an error, SIGTERM from a
# `timeout`, ^C, a closed terminal) qa.sh stops them all (scripts/e2e.py --teardown-owner); a
# watcher in a session of its own (e2e.py --reap-after) does when qa.sh is killed outright.
qa_start=$(cut -d')' -f2 "/proc/$$/stat" | awk '{print $20}')
qa_owner="$$:$qa_start"; players=0
setsid -f python3 "$root/scripts/e2e.py" --reap-after $$ "$qa_start" < /dev/null > /dev/null 2>&1
quit_player() # PID: SIGTERM, then SIGKILL when it still runs after E2E_QUIT_GRACE seconds
{
    local i
    kill -TERM "$1" 2> /dev/null || return 0
    for ((i = 0; i < ${E2E_QUIT_GRACE:-10} * 5; i++)); do
        kill -0 "$1" 2> /dev/null || return 0
        sleep 0.2
    done
    kill -KILL "$1" 2> /dev/null
}
player() # LABEL CMD...: runs CMD in the background as one of this run's players; its pid in player_pid
{
    local label=$1; shift; players=$((players + 1))
    E2E_OWNER=$qa_owner ONI_SCOPE="oni-test-$$-$qa_start-qa-$label-$players.scope" "$root/scripts/contain.sh" "$@" &
    player_pid=$!
}
on_exit()
{
    [ -z "${quiet_flock:-}" ] || kill "$quiet_flock" 2> /dev/null
    [ -z "${quiet_fd:-}" ] || rm -f "$quiet_lockdir/perf.wanted"
    [ "$players" -eq 0 ] || python3 "$root/scripts/e2e.py" --teardown-owner "$qa_owner" > /dev/null 2>&1
}
trap on_exit EXIT
trap 'exit 143' TERM; trap 'exit 130' INT; trap 'exit 129' HUP

# ---- build ----
# The release player every stage runs: the engine, the UI packages and the host, which carries its
# own copy of the engine (a build of the engine alone does not reach the player). Once a run.
built=0
build_release()
{
    [ $built -eq 1 ] && return 0
    if "$root/scripts/build-engine.sh" release > "$run/build-release.txt" 2>&1 \
       && "$root/scripts/build-packages.sh" >> "$run/build-release.txt" 2>&1 \
       && dotnet build "$root/src/OniMoono.Host" -c Release -v q -nologo >> "$run/build-release.txt" 2>&1 \
       && dotnet build "$root/tools/PerfProbe" -c Release -v q -nologo >> "$run/build-release.txt" 2>&1; then
        built=1; return 0
    fi
    fail "release build (see $run/build-release.txt)"; return 1
}

# ---- unit ----
stage_unit()
{
    local log="$run/unit.txt"
    build_release || return
    "$root/scripts/check.sh" > "$log" 2>&1; local status=$?
    [ $status -eq 0 ] || fail "unit: scripts/check.sh exited $status (see $log)"
    grep -E 'Total: [0-9]+, Errors: [0-9]+, Failed: [0-9]+' "$log" | sed 's/\x1b\[[0-9;]*m//g' | while read -r line; do echo "     $line"; done
    if grep -qE 'Errors: [1-9]|Failed: [1-9]' "$log"; then fail "unit: test failures (see $log)"; else ok "unit: tests"; fi
    if grep -qE '^[1-9][0-9]* failures|unresolved scripts [1-9]|missing types: [1-9]|^FAIL' "$log"; then fail "unit: host checks (see $log)"; else ok "unit: host checks"; fi
    if grep -qE '^scripts: OK' "$log"; then ok "unit: script tests"; else fail "unit: script tests: $(grep -E '^scripts:' "$log" | head -1)"; fi
    local missing; missing=$(grep -oE 'missing members: [0-9]+' "$root/out/apicheck-summary.txt" | head -1 | grep -oE '[0-9]+')
    if [ "${missing:-99}" -le 13 ]; then ok "unit: ApiCheck, $missing members missing (13 known, UI Toolkit internals)"; else fail "unit: ApiCheck, $missing members missing"; fi
}

# ---- dogfood ----
SCENARIOS=(menu options newgame colony codex management research naming overlays saveload resize burst intro)
[ -n "${QA_SCENARIOS:-}" ] && read -ra SCENARIOS <<< "$QA_SCENARIOS"

run_scenario() # NAME
{
    local name=$1 out="$run/dogfood/$1" file
    file="$root/scripts/qa-scenarios/$name.sh"; [ -f "$file" ] || file="$root/scripts/m9-scenarios/$name.sh"
    # Scenario settings: TOLERANCE (fraction of pixels a capture may differ: random Duplicants,
    # real-time animation), KEPT_MAX (multi-target framebuffers alive at the end: one per target
    # set whose textures are still referenced), REQUIRE (log lines the run must print).
    # MOVING: captures that must each differ from the one before (a playing video; its content at
    # a given frame follows its audio clock, so it is not compared with a reference).
    # DUMP_MATCH / DUMP_ABSENT: "FRAME:REGEX" pairs; the UI dump of that frame must have / must
    # not have a line matching the extended regex (game state a capture comparison may miss).
    SAVE=""; FRAMES=0; DUMPS=""; ARGS=(); TOLERANCE=0.03; DLC=0; KEPT_MAX=1; REQUIRE=(); MOVING=""
    DUMP_MATCH=(); DUMP_ABSENT=()
    source "$file"
    # DLC=1: the scenario needs the DLC content and a DLC-owning Steam double (qa.env).
    local gamedir="$game" runner=("$root/scripts/run-offline.sh")
    if [ "$DLC" = 1 ]; then
        u57_ready || { note "dogfood $name: skipped (needs the DLC setup in out/local-tools/qa.env)"; return; }
        gamedir=$QA_U57_GAME; runner=("$root/scripts/run.sh" --native-override "steam_api=$QA_U57_STEAM")
    fi
    # QA_EXTRA_ARGS: host arguments added to every scenario (e.g. --game-tweak none).
    [ -n "${QA_EXTRA_ARGS:-}" ] && read -ra extra <<< "$QA_EXTRA_ARGS" && ARGS+=("${extra[@]}")
    local home="$out/home" d
    rm -rf "${out:?}"; mkdir -p "$out"
    d="$home/config/unity3d/Klei/Oxygen Not Included"; mkdir -p "$d"
    printf 'strings: {}\nints:\n  ResolutionWidth: 1024\n  ResolutionHeight: 768\n  RefreshRateNumerator: 60\n  RefreshRateDenominator: 1\n  FullScreen: 0\nfloats: {}\n' > "$d/kplayerprefs.yaml"
    no_data_collection "$d"
    if [ -n "$SAVE" ]; then
        local colony; colony=$(basename "$SAVE" .sav)
        mkdir -p "$d/cloud_save_files/76561197960265729/$colony"
        cp "$SAVE" "$d/cloud_save_files/76561197960265729/$colony/$colony.sav"
    fi
    local caps=() f
    for f in $DUMPS; do caps+=(--capture "$f:$out/f$f.png" --dump-ui "$f:$out/f$f.txt"); done
    # In the background and waited for, so that a signal's trap runs at once.
    ONI_LOG_ERRORS="$out/errors.txt" ONI_GAME_DIR="$gamedir" ONI_RUN_HOME="$home" ONI_CONFIG=release player "dogfood-$name" xvfb-run -a -s "-screen 0 1280x1024x24" \
        timeout -k "${E2E_QUIT_GRACE:-10}" 1800 "$root/scripts/audio-sink/record.sh" "$out/audio.raw" "${runner[@]}" \
        -screen-width 1024 -screen-height 768 -screen-fullscreen 0 --frames "$FRAMES" --max-frame-rate 60 \
        --log "$out/log.txt" "${caps[@]}" "${ARGS[@]}" > "$out/stdout.txt" 2>&1
    wait "$player_pid"
    local status=$?
    rm -f "$out/audio.raw"
    # The game's own log (Debug.Log, exceptions) goes to stdout with the engine's.
    { echo "exit $status"; python3 "$tools" signatures "$out/stdout.txt" "$out/errors.txt"; } > "$out/signatures.txt"
    local rdir="$ref/dogfood/$name"
    if [ $bless -eq 1 ]; then
        [ $status -eq 0 ] || { fail "dogfood $name: the player exited $status; not blessed"; return; }
        local f; for f in $DUMPS; do [ -f "$out/f$f.png" ] || { fail "dogfood $name: no capture f$f; not blessed"; return; }; done
        rm -rf "$rdir"; mkdir -p "$rdir"; cp "$out/signatures.txt" "$rdir/"
        for f in $DUMPS; do cp "$out/f$f.png" "$rdir/"; done
        ok "dogfood $name: blessed ($(($(wc -l < "$out/signatures.txt") - 1)) signatures)"; return
    fi
    if [ ! -d "$rdir" ]; then fail "dogfood $name: no reference (run with --bless after checking $out)"; return; fi
    local new; new=$(python3 "$tools" new-signatures "$rdir/signatures.txt" "$out/signatures.txt")
    if [ -n "$new" ]; then fail "dogfood $name: new log signatures:"; echo "$new" | sed 's/^/       /' | tee -a "$run/summary.txt"; fi
    local worst=0 bad=""
    local previous=""
    for f in $MOVING; do
        if [ -n "$previous" ]; then
            local m; m=$(python3 "$tools" image "$out/f$previous.png" "$out/f$f.png" "$out/moving-$f.png" | cut -d' ' -f1)
            awk -v m="${m:-0}" 'BEGIN { exit !(m > 0.1) }' || fail "dogfood $name: f$f hardly differs from f$previous (${m:-?}): the video is not playing"
        fi
        previous=$f
    done
    for f in $DUMPS; do
        case " $MOVING " in *" $f "*) continue ;; esac
        if [ ! -f "$out/f$f.png" ]; then bad="$bad f$f(missing)"; continue; fi
        local r; r=$(python3 "$tools" image "$rdir/f$f.png" "$out/f$f.png" "$out/f$f-diff.png" | cut -d' ' -f1)
        [ -n "$r" ] || r=1.0000 # an unreadable capture or reference is a difference
        awk -v r="$r" -v t="$TOLERANCE" 'BEGIN { exit !(r > t) }' && bad="$bad f$f($r)"
        awk -v r="$r" -v w="$worst" 'BEGIN { exit !(r > w) }' && worst=$r
    done
    if [ -n "$bad" ]; then fail "dogfood $name: captures differ beyond $TOLERANCE:$bad"; else [ -z "$new" ] && ok "dogfood $name (largest capture difference $worst)"; fi
    # Multi-target framebuffers go with their textures.
    local kept; kept=$(grep -aoE '[0-9]+ kept' "$out/stdout.txt" | tail -1 | cut -d' ' -f1)
    [ "${kept:-0}" -le "$KEPT_MAX" ] || fail "dogfood $name: $kept multi-target framebuffers kept at the end (at most $KEPT_MAX)"
    local line
    for line in "${REQUIRE[@]}"; do grep -aqF "$line" "$out/stdout.txt" || fail "dogfood $name: the log lacks \"$line\""; done
    local check
    for check in "${DUMP_MATCH[@]}"; do
        grep -aqE "${check#*:}" "$out/f${check%%:*}.txt" 2>/dev/null || fail "dogfood $name: the f${check%%:*} UI dump has no line matching ${check#*:}"
    done
    for check in "${DUMP_ABSENT[@]}"; do
        ! grep -aqE "${check#*:}" "$out/f${check%%:*}.txt" 2>/dev/null || fail "dogfood $name: the f${check%%:*} UI dump has a line matching ${check#*:}"
    done
}

# The perf stage measures frame rates, so it holds the machine: perf.wanted keeps new e2e specs
# from starting, and the exclusive hold of perf.lock waits for the running ones. The other stages
# run beside specs (a scenario that waits on real time, as newgame's worldgen, leaves it ample
# frames), and so does the memory stage: peak memory does not depend on the machine's load.
quiet_lockdir="${E2E_LOCK_DIR:-$HOME/.cache/oni-brutal-e2e}"
quiet_fd=""
quiet_begin() # STAGE
{
    mkdir -p "$quiet_lockdir"
    # What dead e2e runners left (players, Xvfbs) would keep the machine busy for nothing.
    python3 "$root/scripts/e2e.py" --clean | grep -v '^cleaned 0 ' | sed "s/^/     $1: /"
    echo $$ > "$quiet_lockdir/perf.wanted"
    exec {quiet_fd}>>"$quiet_lockdir/perf.lock"
    note "$1: waiting for running e2e specs to finish (scripts/e2e.py --status shows them)"
    flock -x "$quiet_fd" & quiet_flock=$!; wait $quiet_flock; quiet_flock=""
    # The lock keeps players out, not other work (another project's build or verifier on many
    # cores skewed a perf run by 13%): wait, at most QA_QUIET_MAX_MIN minutes, until under
    # QA_QUIET_CORES cores' worth of CPU is busy.
    local busy waited=0 max=$(( ${QA_QUIET_MAX_MIN:-30} * 60 ))
    while busy=$(cores_busy); win=$(windows_game); [ -n "$win" ] || awk -v b="$busy" -v m="${QA_QUIET_CORES:-2}" 'BEGIN { exit !(b > m) }'; do
        [ $waited -eq 0 ] && [ -n "$win" ] && note "$1: waiting while a game runs on Windows ($win), whose load WSL can't see"
        [ $waited -eq 0 ] && note "$1: waiting for the machine to go quiet ($busy cores busy; most: $(ps -eo pid=,pcpu=,comm= --sort=-pcpu | head -3 | xargs -L1 echo | paste -sd';' -))"
        if [ $waited -ge $max ]; then note "$1: still $busy cores busy after ${QA_QUIET_MAX_MIN:-30} min; measuring anyway"; break; fi
        sleep 15; waited=$((waited + 20))
    done
}
windows_game() # a game running on the Windows side (the user playing, or a Windows test run): its names
{
    command -v tasklist.exe > /dev/null || return 0
    tasklist.exe /NH 2>/dev/null | tr -d '\r' | awk 'tolower($1) ~ /^(onimoono|onibrutal|oxygennotincluded|brutalnotincluded)\.exe$/ { print $1 }' | sort -u | paste -sd, -
}
cores_busy() # cores' worth of CPU busy over 5 s, from /proc/stat
{
    local a b
    a=$(awk '/^cpu /{print $2+$3+$4+$7+$8+$9, $5+$6}' /proc/stat); sleep 5
    b=$(awk '/^cpu /{print $2+$3+$4+$7+$8+$9, $5+$6}' /proc/stat)
    awk -v a="$a" -v b="$b" -v n="$(nproc)" 'BEGIN { split(a, x, " "); split(b, y, " "); busy = y[1] - x[1]; idle = y[2] - x[2]; printf "%.1f", (busy + idle > 0 ? n * busy / (busy + idle) : 0) }'
}
quiet_end()
{
    flock -u "$quiet_fd"; exec {quiet_fd}>&-; quiet_fd=""
    rm -f "$quiet_lockdir/perf.wanted"
}

stage_dogfood()
{
    build_release || return
    local s
    for s in "${SCENARIOS[@]}"; do run_scenario "$s"; done
}

# ---- U57 (verify, perf) ----
u57_ready()
{
    [ -n "${QA_U57_GAME:-}" ] && [ -n "${QA_U57_STEAM:-}" ] && [ -f "${QA_U57_SAVE:-}" ]
}

# u57_run OUT FRAMES [host args] — U57 resumed from the menu, PerfProbe keeping it at speed 3,
# peak resident memory written to OUT/peak-rss (kB).
u57_run()
{
    local out=$1 frames=$2; shift 2
    local home="$out/home" d
    rm -rf "${out:?}"; mkdir -p "$out"
    d="$home/config/unity3d/Klei/Oxygen Not Included"
    mkdir -p "$d/cloud_save_files/76561197960265729/U57" "$d/mods/Local"
    cp "$QA_U57_SAVE" "$d/cloud_save_files/76561197960265729/U57/U57.sav"
    printf 'strings: {}\nints:\n  ResolutionWidth: 1024\n  ResolutionHeight: 768\n  RefreshRateNumerator: 60\n  RefreshRateDenominator: 1\n  FullScreen: 0\nfloats: {}\n' > "$d/kplayerprefs.yaml"
    no_data_collection "$d"
    cp -r "$root/out/bin/PerfProbe/release" "$d/mods/Local/PerfProbe"
    # U57_OVERLAYS: PerfProbe opens these overlays in turn, 300 frames each (its overlays.txt).
    [ -n "${U57_OVERLAYS:-}" ] && { echo 'hold 300'; printf '%s\n' $U57_OVERLAYS; } > "$d/mods/Local/PerfProbe/overlays.txt"
    # U57_SCREENS: then PerfProbe opens and closes these management screens in turn (its screens.txt).
    [ -n "${U57_SCREENS:-}" ] && { echo 'wait overlays'; for s in $U57_SCREENS; do printf 'open %s\nclose\n' "$s"; done; echo 'hold 600 after-screens'; } > "$d/mods/Local/PerfProbe/screens.txt"
    echo '{"version":1,"mods":[{"label":{"distribution_platform":0,"id":"PerfProbe","title":"PerfProbe","version":-2037174645},"status":1,"enabled":true,"enabledForDlc":["","EXPANSION1_ID"],"crash_count":0,"reinstall_path":null,"staticID":"PerfProbe"}],"mod_load_in_progress":false}' > "$d/mods/mods.json"
    ONI_LOG_ERRORS="$out/errors.txt" ONI_GAME_DIR="$QA_U57_GAME" ONI_RUN_HOME="$home" ONI_CONFIG=release player "u57-$(basename "$out")" xvfb-run -a -s "-screen 0 1280x1024x24" \
        timeout -k "${E2E_QUIT_GRACE:-10}" 3600 "$root/scripts/run.sh" --native-override "steam_api=$QA_U57_STEAM" -screen-width 1024 -screen-height 768 -screen-fullscreen 0 \
        --frames "$frames" --input 400:click:948,75 --input 900:click:153,192 --game-tweak all --log "$out/log.txt" "$@" > "$out/stdout.txt" 2>&1
    local pid=$player_pid peak=0 rss p perfs=0 n progress=$SECONDS hang=$(( ${QA_HANG_MIN:-5} * 60 ))
    # U57_MEMWATCH=1 (the memory stage): qa-tools.py memwatch watches it instead, and stops it
    # once the first autosave has passed and its memory has settled.
    if [ -n "${U57_MEMWATCH:-}" ]; then
        python3 "$tools" memwatch "$out" "$home" "$pid" > "$out/memory.txt" 2>&1 &
        wait $!
        wait $pid; echo $? > "$out/exit"
        return
    fi
    while kill -0 $pid 2>/dev/null; do
        # The hang watchdog: a PERF line at least every QA_HANG_MIN minutes, the load before the
        # first one included (see the header).
        n=$(grep -ac '\] PERF f' "$out/stdout.txt" 2>/dev/null); n=${n:-0}
        [ "$n" -gt "$perfs" ] && { perfs=$n; progress=$SECONDS; }
        for p in $(pgrep -x OniMoono); do
            grep -qa "$home" /proc/$p/environ 2>/dev/null || continue
            rss=$(awk '/VmRSS/ {print $2}' /proc/$p/status 2>/dev/null); rss=${rss:-0}
            [ "$rss" -gt "$peak" ] && peak=$rss
            echo "$(date +%s) $rss" >> "$out/rss.txt"
            # A runaway run is killed outright before it takes the machine down.
            [ "$rss" -gt $((24 * 1024 * 1024)) ] && kill -KILL $p
            if [ $((SECONDS - progress)) -ge $hang ] && [ ! -d "$out/hang" ]; then
                echo "$(date '+%F %T') hang watchdog: no PERF line for ${QA_HANG_MIN:-5} min (after $perfs); diagnostics, then stop" >> "$out/stdout.txt"
                "$root/scripts/hang-dump.sh" "$p" "$out/hang" "$out/stdout.txt" > /dev/null 2>&1
                quit_player "$p"
            fi
        done
        sleep 5
    done
    wait $pid; echo $? > "$out/exit"
    echo $peak > "$out/peak-rss"
}

# u57_check STAGE OUT: the run ended normally, and logged no exception or error the reference for
# STAGE lacks (the game's own known ones are in it).
u57_check()
{
    local stage=$1 out=$2 status; status=$(cat "$out/exit")
    if [ -d "$out/hang" ]; then
        fail "$stage: the player hung (no PERF line for ${QA_HANG_MIN:-5} min) and was stopped; diagnostics in $out/hang"
        sed 's/^/       /' "$out/hang/summary.txt" | tee -a "$run/summary.txt"
    elif [ "$status" != 0 ]; then fail "$stage: the player exited $status"; fi
    python3 "$tools" signatures "$out/stdout.txt" "$out/errors.txt" > "$out/signatures.txt"
    if [ $bless -eq 1 ]; then
        [ "$status" = 0 ] || return # a failed run is not a reference
        cp "$out/signatures.txt" "$ref/$stage-signatures.txt"; ok "$stage: $(wc -l < "$out/signatures.txt") log signatures blessed"; return
    fi
    if [ ! -f "$ref/$stage-signatures.txt" ]; then fail "$stage: no reference signatures (run with --bless after checking $out)"; return; fi
    local new; new=$(python3 "$tools" new-signatures "$ref/$stage-signatures.txt" "$out/signatures.txt")
    if [ -n "$new" ]; then fail "$stage: new log signatures:"; echo "$new" | sed 's/^/       /' | tee -a "$run/summary.txt"; else ok "$stage: no new exceptions or errors"; fi
}

stage_verify()
{
    u57_ready || { note "verify: skipped (no out/local-tools/qa.env)"; return; }
    build_release || return
    local out="$run/verify"
    # Every overlay is opened in turn for 300 frames, for the overlays' tweaks; then three
    # management screens are opened and closed, whose rect masks then clip out of sight.
    U57_SCREENS="consumables vitals jobs" U57_OVERLAYS="Oxygen Power Temperature Temperature:HeatFlow Temperature:StateChange TileMode Light Decor Disease Crop HarvestWhenReady Rooms LiquidConduit GasConduit SolidConveyor Logic Suit Radiation Priorities ThermalConductivity None" \
    ONI_TWEAK_VERIFY=1 ONI_VERIFY_CANVAS_SKIPS=1 ONI_VERIFY_CANVAS_PRODUCTS=1 ONI_VERIFY_COMPONENT_LAYOUTS=1 ONI_VERIFY_TEXEL_COPIES=1 ONI_VERIFY_PARTIAL_UPLOADS=1 ONI_VERIFY_PARALLEL_DRAWS=1 ONI_VERIFY_BINDLESS=1 ONI_VERIFY_RAW_LOADS=1 u57_run "$out" 10500
    u57_check verify "$out"
    [ -d "$out/hang" ] && return # a hung run has nothing more to check
    local report; report=$(python3 "$tools" verify "$out/stdout.txt"); local status=$?
    echo "$report" | sed 's/^/     /' | tee -a "$run/summary.txt"
    [ $status -eq 0 ] || fail "verify: a verify mode reported mismatches"
    local tweak
    for tweak in fast-anim-events fast-anim-batches fast-sublimation fast-jobs fast-particle-collision fast-offset-tables fast-anim-cleanup fast-partitioner-cleanup fast-overlays fast-conveyor-paths fast-overlay-textures fast-smi-lookups path-probe-repair instant-reachability "nav links verify (in play)" "path grids verify" "canvas skips" "canvas products" "component layouts" "texel copies" "partial uploads" "parallel draws"; do
        echo "$report" | grep -q "$tweak:" || fail "verify: no report from $tweak"
    done
    # The overlays tweak reports each of its parts.
    local part
    for part in "power answers" "kept positions" "kept targets"; do
        echo "$report" | grep -q "fast-overlays: .*$part" || fail "verify: no $part report from fast-overlays"
    done
    # The canvas skips: the walk's, the rect masks' and the raycasts'.
    for part in "verified" "graphics skipped by masks" "graphics left out of raycasts"; do
        echo "$report" | grep -q "canvas skips: .*$part" || fail "verify: no canvas skips report on $part"
    done
    # The radbolt tweak checks two things, each with its own report.
    echo "$report" | grep -q "fast-particle-collision: .*mismatches" || fail "verify: no lookup report from fast-particle-collision"
    echo "$report" | grep -q "fast-particle-collision: .*wrong skips" || fail "verify: no gather report from fast-particle-collision"
    read -r fps span windows <<< "$(python3 "$tools" perf "$out/stdout.txt")"
    if [ "${span:-0}" -ge 600 ]; then ok "verify: $span game seconds checked"; else fail "verify: only ${span:-0} game seconds checked (10 minutes needed)"; fi
}

stage_perf()
{
    u57_ready || { note "perf: skipped (no out/local-tools/qa.env)"; return; }
    # The user playing on Windows: perf is skipped rather than measured beside them (WSL can't see
    # that load) or held up; regressions have been rare enough that a later run will do.
    local win; win=$(windows_game)
    [ -z "$win" ] || { note "perf: skipped (a game is running on Windows: $win)"; return; }
    build_release || return
    local out="$run/perf"
    quiet_begin perf
    u57_run "$out" 4800
    quiet_end
    u57_check perf "$out"
    [ -d "$out/hang" ] && return # a hung run has nothing more to check
    local peak; peak=$(cat "$out/peak-rss")
    if [ "$peak" -lt $((16 * 1024 * 1024)) ]; then ok "perf: peak memory $((peak / 1024)) MB"; else fail "perf: peak memory $((peak / 1024)) MB (16 GB limit)"; fi
    read -r fps span windows <<< "$(python3 "$tools" perf "$out/stdout.txt")"
    local status; status=$(cat "$out/exit")
    local kept; kept=$(grep -aoE '[0-9]+ kept' "$out/stdout.txt" | tail -1 | cut -d' ' -f1)
    if [ "${kept:-0}" -le 1 ]; then ok "perf: multi-target framebuffers kept ${kept:-0}"; else fail "perf: $kept multi-target framebuffers kept"; fi
    awk -v f="${fps:-0}" 'BEGIN { exit !(f > 0) }' || { fail "perf: no frame rate measured"; return; }
    if [ $bless -eq 1 ]; then
        [ "$status" = 0 ] && [ "$peak" -lt $((16 * 1024 * 1024)) ] || { fail "perf: a failed run is not a baseline"; return; }
        echo "$fps" > "$ref/perf-baseline"; ok "perf: $fps fps blessed as the baseline"; return
    fi
    [ -f "$ref/perf-baseline" ] || { fail "perf: no baseline (run with --bless after checking $out)"; return; }
    local base; base=$(cat "$ref/perf-baseline")
    if awk -v f="$fps" -v b="$base" 'BEGIN { exit !(f >= b * 0.85) }'; then ok "perf: $fps fps over $windows windows (baseline $base)"; else fail "perf: $fps fps, below the baseline $base less 15%"; fi
}

# The memory stage: U57's peak resident memory alone, beside whatever else runs (it takes no
# perf lock and waits for no quiet machine). memwatch stops the player once the first autosave
# has passed and RSS has not risen by QA_MEMORY_RISE_MB (64) for QA_MEMORY_SETTLE_FRAMES (1200)
# frames; QA_MEMORY_MAX_FRAMES (9000) caps the run.
stage_memory()
{
    u57_ready || { note "memory: skipped (no out/local-tools/qa.env)"; return; }
    build_release || return
    local out="$run/memory"
    U57_MEMWATCH=1 u57_run "$out" "${QA_MEMORY_MAX_FRAMES:-9000}"
    note "memory: $(tail -1 "$out/memory.txt")"
    if [ -d "$out/hang" ]; then fail "memory: the player hung (no PERF line for ${QA_HANG_MIN:-5} min); diagnostics in $out/hang"; return; fi
    local peak autosave; peak=$(cat "$out/peak-rss" 2>/dev/null); peak=${peak:-0}
    autosave=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1])).get("autosave_frame") or "")' "$out/memory.json" 2>/dev/null)
    [ "$peak" -gt 0 ] || { fail "memory: no memory measured (see $out/stdout.txt)"; return; }
    [ -n "$autosave" ] || fail "memory: no autosave within ${QA_MEMORY_MAX_FRAMES:-9000} frames: the peak may be later"
    if [ "$peak" -lt $((16 * 1024 * 1024)) ]; then ok "memory: peak $((peak / 1024)) MB"; else fail "memory: peak $((peak / 1024)) MB (16 GB limit)"; fi
}

for s in "${stages[@]}"; do
    case $s in
        unit|dogfood|verify|perf|memory) echo "== $s"; "stage_$s" ;;
        *) echo "unknown stage $s"; exit 2 ;;
    esac
done
echo "== $failures failure(s); run in $run"
exit $(( failures > 0 ))
