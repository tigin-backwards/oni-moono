#!/usr/bin/env bash
# Development and test runs of the Windows player from WSL: stages out/win
# (scripts/build-windows.sh) in %TEMP%\oni-brutal\host (ONI_WIN_STAGE names another folder) and
# starts OniMoono.exe with the Windows game install. Extra arguments go to the host:
#   scripts/run-windows.sh --frames 600 --capture 590:C:/path/shot.png
# A test run never touches the user's game data, Steam account or speakers:
# - the run home (ONI_RUN_HOME, default %TEMP%\oni-brutal\run-home) takes the place of
#   Documents, AppData and LocalLow (--run-home);
# - the offline Steam double replaces steam_api64.dll;
# - the game's audio goes to <run home>\fmodoutput.wav (--audio-wav).
# As scripts/run.sh, `--game-tweak none --splash none --log-paths full` come first.
# The window opens windowed on the second monitor without taking the focus (`-screen-fullscreen 0
# -monitor 2 --no-focus`; ONI_WIN_MONITOR=N picks another monitor), so a test never covers or
# takes over the user's main one. Focus it, and act on its process, only through
# out/local-tools/win-run.ps1 -Match RUN_HOME (by its run home, never by name).
# Queues itself through runq as kind windows, or RUNQ_KIND (win-perf for frame-rate runs):
# docs/RUNQ.md, "What each job gets", has the job's stage folder, run home and cleanup.
set -euo pipefail
source "$(dirname "$0")/env.sh"
source "$root/scripts/runq-submit.sh"
runq_self_submit --kind "${RUNQ_KIND:-windows}" --label "run-windows.sh${*:+ $*}" -- "$root/scripts/run-windows.sh" "$@"
win_temp="$(cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
# ONI_WIN_STAGE (a Windows path; runq sets one per job): where the host is staged.
stage_win="${ONI_WIN_STAGE:-$win_temp\\oni-brutal}"
stage="$(wslpath "$stage_win")"
mkdir -p "$stage"
rm -rf "$stage/host" "$stage/steamstub"
# Files, not links (-L): a stage never links into another install.
cp -rL "$root/out/win/host" "$root/out/win/steamstub" "$stage/"
# The validation layer scripts/get-vulkan-validation.sh fetched, for --vulkan-validation.
vvl="$root/out/local-tools/vulkan-validation/windows"
[ -f "$vvl/VkLayer_khronos_validation.json" ] && cp -r "$vvl" "$stage/host/vulkan-validation"
# ONI_STEAM_STUB=PATH: another steam_api64.dll for the run (a local test setup's).
[ -n "${ONI_STEAM_STUB:-}" ] && cp "$ONI_STEAM_STUB" "$stage/steamstub/steam_api64.dll"
stub=steamstub
run_home="${ONI_RUN_HOME:-$win_temp\\oni-brutal\\run-home}"
# In a runq job, the run's game settings have every volume at 0 (the run home may have been
# prepared after runq set it up, by scripts/windows-run-home.sh).
[ -z "${RUNQ_JOB:-}" ] || python3 -I "$root/scripts/runqlib/winenv.py" mute "$(wslpath "$run_home")"
# ONI_AFFINITY=HEXMASK: the logical processors the player may run on (3: the first two), which
# OniMoono.exe sets on itself before the runtime starts.
[ -z "${ONI_AFFINITY:-}" ] || export WSLENV="${WSLENV:+$WSLENV:}ONI_AFFINITY"
game="${ONI_WIN_GAME_DIR:-C:\\Program Files (x86)\\Steam\\steamapps\\common\\OxygenNotIncluded}"
# ONI_WIN_FOREGROUND_FROM=SECONDS (test runs given the user's word, the D3D11 plan's M4; as
# original-windows.sh's ORIG_FOREGROUND_AT): from that many seconds after the start, once a
# second, a helper lets the player's process set the foreground (AllowSetForegroundWindow: "A
# process that can set the foreground window can enable another process to set the foreground
# window"), so its `--input N:focus` can take it back; it logs each result's change. No key is
# sent and the pointer is not moved; the player keeps --no-focus.
if [ -n "${ONI_WIN_FOREGROUND_FROM:-}" ]; then
  powershell.exe -NoProfile -Command "
Add-Type -Namespace OniTest -Name Fg -MemberDefinition '[DllImport(\"user32.dll\")] public static extern bool AllowSetForegroundWindow(uint pid);'
Start-Sleep -Seconds ${ONI_WIN_FOREGROUND_FROM}
\$last = \$null
for (\$i = 0; \$i -lt 600; \$i++) {
  \$p = Get-CimInstance Win32_Process -Filter \"Name = 'OniMoono.exe'\" | Where-Object { \$_.CommandLine -like '*$run_home*' } | Select-Object -First 1
  if (-not \$p) { if (\$i -gt 5) { break } } else {
    \$r = [OniTest.Fg]::AllowSetForegroundWindow([uint32]\$p.ProcessId)
    if (\$r -ne \$last) { 'foreground helper: AllowSetForegroundWindow(' + \$p.ProcessId + ') ' + \$r + ' at ' + (Get-Date -Format HH:mm:ss.fff); \$last = \$r }
  }
  Start-Sleep -Seconds 1
}" 2>&1 | tr -d '\r' &
fi
exec "$stage/host/OniMoono.exe" --game "$game" --run-home "$run_home" --audio-wav "$run_home" --game-tweak none --splash none --log-paths full \
  --native-override "steam_api64=$stage_win\\$stub\\steam_api64.dll" -screen-fullscreen 0 -monitor "${ONI_WIN_MONITOR:-2}" --no-focus "$@"
