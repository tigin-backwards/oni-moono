#!/usr/bin/env bash
# The project's verification gate (ARCHITECTURE §9): build everything, unit tests, API
# conformance summary, and the host's type-load / runtime-services check against the game, with
# this project's UI packages.
set -euo pipefail
source "$(dirname "$0")/env.sh"
# Its builds and tests run in the memory-capped test slice (scripts/contain.sh).
[ -n "${ONI_CONTAINED:-}" ] || exec "$root/scripts/contain.sh" "$root/scripts/check.sh" "$@"
dotnet build "$root/OniMoono.slnx" -v q -nologo
# Every suite runs and reports; the gate fails at the end if any of them failed.
failed=()
tests() { # tests NAME: run a test binary, print its totals and failures
    local out status=0
    out=$("$root/out/bin/$1/debug/$1" 2>&1) || status=$?
    grep -E 'Total:|\[FAIL\]' <<< "$out" || true
    [ $status -eq 0 ] || failed+=("$1")
}
tests OniMoono.UnityEngine.Tests
tests OniMoono.Assets.Tests
tests ModTriage.Tests
scripts_log=$(python3 -m unittest discover -s "$root/tests/scripts" 2>&1) || failed+=(scripts)
echo "scripts: $(tail -1 <<< "$scripts_log")"
grep -A12 -E '^(FAIL|ERROR): ' <<< "$scripts_log" | sed 's/^/  /' || true
# Each publish carries what the host's build adds to its output: the UI packages, the game
# tweaks, the engine facades (as many as the debug build's), the Restarter, NOTICE.txt and the
# Moono Tweaks screen's translations. An earlier publish's are removed first, so they cannot
# stand in.
host_files() {
    local f facades
    for f in packages/UnityEngine.UI.dll packages/Unity.TextMeshPro.dll packages/Unity.Timeline.dll \
             OniMoono.GameTweaks.dll engine/UnityEngine.CoreModule.dll Restarter NOTICE.txt translations/zh.po; do
        [ -f "$1/$f" ] || { echo "$1/$f missing"; return 1; }
    done
    facades=$(ls "$root/out/bin/OniMoono.Host/debug/engine" | wc -l)
    [ "$(ls "$1/engine" | wc -l)" = "$facades" ] || { echo "$1/engine: $(ls "$1/engine" | wc -l) files, the build has $facades"; return 1; }
}
clean_publish() { rm -rf "$1/packages" "$1/engine" "$1/OniMoono.GameTweaks."* "$1/Restarter" "$1/NOTICE.txt" "$1/translations"; }
# The host cross-published for macOS (x86_64, run under Rosetta on Apple Silicon): it must build,
# and its apphost must be an x86_64 Mach-O executable (magic feedfacf, CPU type 0x01000007).
osx="$root/out/publish/osx-x64"; clean_publish "$osx"
if dotnet publish "$root/src/OniMoono.Host" -c Release -r osx-x64 --self-contained -o "$osx" -v q -nologo > "$root/out/publish-osx-x64.log" 2>&1 \
    && [ "$(od -An -tx4 -N8 "$osx/OniMoono" | tr -s ' ')" = " feedfacf 01000007" ] && host_files "$osx"; then
    echo "osx-x64 publish: $(ls "$osx" | wc -l) files, x86_64 Mach-O apphost, the host's files"
else
    failed+=(osx-x64-publish); echo "osx-x64 publish failed: out/publish-osx-x64.log"
fi
# The primary Mac build, native on Apple Silicon: arm64 apphost (CPU type 0x0100000c), with
# BRUTAL's osx-arm64 dylibs beside it. Its SimDLL is onimaxxing-sim's arm64 port (Klei's is
# x86_64 only), in plugins/ when one is at hand (ONI_SIM_PORT_ARM64).
osx_arm="$root/out/publish/osx-arm64"; clean_publish "$osx_arm"
sim_port="${ONI_SIM_PORT_ARM64:-$HOME/projects/oni-worldgen/dist/simdll-macos/libSimDLL-arm64-simthread.dylib}"
if dotnet publish "$root/src/OniMoono.Host" -c Release -r osx-arm64 --self-contained -o "$osx_arm" -v q -nologo > "$root/out/publish-osx-arm64.log" 2>&1 \
    && [ "$(od -An -tx4 -N8 "$osx_arm/OniMoono" | tr -s ' ')" = " feedfacf 0100000c" ] && host_files "$osx_arm"; then
    echo "osx-arm64 publish: $(ls "$osx_arm" | wc -l) files, arm64 Mach-O apphost, the host's files"
    if [ -f "$sim_port" ]; then mkdir -p "$osx_arm/plugins"; cp "$sim_port" "$osx_arm/plugins/libSimDLL.dylib"; else rm -f "$osx_arm/plugins/libSimDLL.dylib"; fi
else
    failed+=(osx-arm64-publish); echo "osx-arm64 publish failed: out/publish-osx-arm64.log"
fi
# The macOS build's shaders (docs/research/macos-feasibility.md §6.1), when its data is here: Metal
# reflection against the MSL, every Metal program's parameters against GLCore's, and the natives.
# The u60 branch checks U60's Mac data (a frozen copy of depot 457142 at build 761015, fetched as
# the Linux one was); until it is there, the Mac checks are left out rather than run on U59's data,
# which the version check refuses. The 761211 hotfix changed game code only (the Linux and Windows
# builds' bundles, shaders and natives hold 761015's content), so 761015's Mac data stands for it.
mac_app="${ONI_MAC_APP:-$HOME/projects/oni-mac-761015/OxygenNotIncluded.app}"
mac_data="$mac_app/Contents/Resources/Data"
if [ -d "$mac_data" ]; then
    survey=("$root/out/bin/ShaderSurvey/debug/ShaderSurvey")
    "${survey[@]}" check-metal "$mac_data" | tail -1 || failed+=(check-metal)
    compare=$("${survey[@]}" compare-metal "$mac_data") || failed+=(compare-metal)
    echo "compare-metal: $(head -1 <<< "$compare"); $(tail -1 <<< "$compare")"
    grep -A20 UNEXPLAINED <<< "$compare" | head -40 || true
    # The host's P/Invokes against the Mac bundle's plugins (their Mach-O exports), for the
    # x86_64 build (Klei's SimDLL) and, with the port in plugins/, for the arm64 build (its arm64
    # slices, the port, and BRUTAL's dylibs in the publish folder with what they link).
    natives_summary() { echo "$(grep -E '^  (SimDLL|fmodstudio|steam_api|glfw|freetype|stb|glad): [0-9]+ of ' <<< "$1" | sed -E 's/^  ([^:]+): ([0-9]+ of [0-9]+).*/\1 \2/' | paste -sd, -); $(tail -1 <<< "$1")"; }
    natives=$("$root/out/bin/OniMoono.Host/debug/OniMoono" --game "$mac_app" --check-natives --native-arch x86_64 --log "$root/out/check-natives-mac.log") || failed+=(check-natives-mac)
    echo "check-natives (macOS x86_64): $(natives_summary "$natives")"
    grep -E 'MISSING|NO PLUGIN|NO LIBRARY| CODE in' <<< "$natives" || true
    if [ -f "$osx_arm/plugins/libSimDLL.dylib" ]; then
        natives=$("$root/out/bin/OniMoono.Host/debug/OniMoono" --game "$mac_app" --check-natives --native-arch arm64 --native-dir "$osx_arm" --log "$root/out/check-natives-mac-arm64.log") || failed+=(check-natives-mac-arm64)
        echo "check-natives (macOS arm64, onimaxxing-sim): $(natives_summary "$natives")"
        grep -E 'MISSING|NO PLUGIN|NO LIBRARY| CODE in' <<< "$natives" || true
    else
        echo "check-natives (macOS arm64) skipped: no arm64 onimaxxing-sim at $sim_port (set ONI_SIM_PORT_ARM64)"
    fi
else
    echo "macOS data not found: its shader and native checks skipped (set ONI_MAC_APP)"
fi
"$root/scripts/build-engine.sh" >/dev/null
"$root/scripts/apicheck.sh" > "$root/out/apicheck-summary.txt" || true; head -2 "$root/out/apicheck-summary.txt"
# compact-nav-links' check applies the four reviewed transpilers of the methods that touch nav
# links when their mods are at hand (the Workshop copies, ONI_WS, and Fast Track's local build).
if [ -z "${ONI_NAVLINKS_MODS:-}" ]; then
    ws="${ONI_WS:-$HOME/projects/oni-brutal/out/ws}"
    for f in "$ws/3818353735/HazardPathing.dll" "$ws/3510927460/TransitTubeOverlay.dll" "$ws/3430960564/TravelTubesExpanded.dll" \
             "$HOME/projects/oni-brutal/out/slowsave/mods-template/Local/FastTrack/FastTrack.dll"; do
        if [ -f "$f" ]; then ONI_NAVLINKS_MODS="${ONI_NAVLINKS_MODS:+$ONI_NAVLINKS_MODS:}$f"; fi
    done
    export ONI_NAVLINKS_MODS
fi
"$root/out/bin/OniMoono.Host/debug/OniMoono" --game "$game" --check-types --log "$root/out/check-types.log" | grep -vE '^(ok   [A-Za-z0-9.-]+ +[0-9]+ types|from )'
# Again on the release build, which releases ship: the host's own code is compiled optimized
# there, as the game's is, and not in the debug build.
dotnet build "$root/src/OniMoono.Host" -c Release -v q -nologo "-clp:ErrorsOnly;NoSummary"
types=$("$root/out/bin/OniMoono.Host/release/OniMoono" --game "$game" --check-types --log "$root/out/check-types-release.log") || failed+=(check-types-release)
echo "check-types (release build): $(grep -c '^ok ' <<< "$types") ok, $(grep -c '^FAIL ' <<< "$types") failed"
grep -A8 '^FAIL ' <<< "$types" || true
"$root/out/bin/OniMoono.Host/debug/OniMoono" --game "$game" --check-assets --log "$root/out/check-assets.log" | grep -v '^\[engine\]'
"$root/out/bin/OniMoono.Host/debug/OniMoono" --game "$game" --check-bind --log "$root/out/check-bind.log" | grep -v '^\[engine\]'
natives=$("$root/out/bin/OniMoono.Host/debug/OniMoono" --game "$game" --check-natives --log "$root/out/check-natives.log") || failed+=(check-natives)
echo "check-natives: $(tail -1 <<< "$natives")"; grep -E 'MISSING|NO PLUGIN' <<< "$natives" || true
# The Windows player's native host (src/OniMoono.NativeHost), when a Windows build is here: building
# it needs Windows, checking it only its PE file (the GPU exports, icon, manifest, version).
if [ -f "$root/out/win/host/OniMoono.exe" ]; then
    winhost=$(python3 -I "$root/scripts/check-windows-host.py" "$root/out/win/host/OniMoono.exe") || { failed+=(check-windows-host); echo "$winhost"; }
    echo "check-windows-host: $(grep -c '^ok ' <<< "$winhost") ok, $(grep -c '^FAIL ' <<< "$winhost") failed"
else
    echo "check-windows-host skipped: no out/win/host/OniMoono.exe (scripts/build-windows.sh)"
fi
"$root/scripts/build-packages.sh" >/dev/null; echo "packages: $(sed -n 2p "$root/out/apicheck-packages.txt")"
[ ${#failed[@]} -eq 0 ] || { echo "test suites failed: ${failed[*]}"; exit 1; }
