#!/usr/bin/env bash
# Development/test runs of the Windows player from WSL: stages out/win (scripts/build-windows.sh)
# in %TEMP%\oni-brutal\host (ONI_WIN_STAGE names another folder) and starts OniMoono.exe on the
# Windows side with the Windows game install. Test runs never touch the user's own game data,
# Steam account or speakers: the run home (ONI_RUN_HOME, default %TEMP%\oni-brutal\run-home)
# takes the place of Documents, AppData and LocalLow (--run-home), the offline Steam double
# replaces steam_api64.dll, and the game's audio goes to <run home>\fmodoutput.wav (--audio-wav).
# Extra arguments go to the host.
# As scripts/run.sh, `--game-tweak none --splash none --log-paths full` come first: no tweaks, no
# loading splash and the log's paths as logged unless the run names them.
# The window opens windowed on the second monitor without taking the focus (`-screen-fullscreen 0
# -monitor 2 --no-focus`; ONI_WIN_MONITOR=N picks another monitor), so a test never covers or
# takes over the user's main one. Focus it only through out/local-tools/win-run.ps1 -Match RUN_HOME
# -Action focus, and act on its process only through win-run.ps1 (by its run home, never by name).
#   scripts/run-windows.sh --frames 600 --capture 590:C:/path/shot.png
# It runs as a runq job (docs/RUNQ.md) of kind windows, or RUNQ_KIND (win-perf for frame-rate
# runs): runq holds the Windows slot (and windows-game.lock for the old scripts), and gives the run
# a stage folder and run home of its own, %TEMP%\oni-runq\JOB\stage and \home (ONI_WIN_STAGE,
# ONI_RUN_HOME unless set), whose volume settings it sets to 0; it kills the game by that run home
# when the job ends. Inside a job, with RUNQ=off or without a daemon it runs directly.
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
game="${ONI_WIN_GAME_DIR:-C:\\Program Files (x86)\\Steam\\steamapps\\common\\OxygenNotIncluded}"
exec "$stage/host/OniMoono.exe" --game "$game" --run-home "$run_home" --audio-wav "$run_home" --game-tweak none --splash none --log-paths full \
  --native-override "steam_api64=$stage_win\\$stub\\steam_api64.dll" -screen-fullscreen 0 -monitor "${ONI_WIN_MONITOR:-2}" --no-focus "$@"
