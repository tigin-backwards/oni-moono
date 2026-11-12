#!/usr/bin/env bash
# Comparison runs of the ORIGINAL Windows player from WSL, isolated as our own test runs are
# (scripts/run-windows.sh). Not part of the product.
#
# The original runs from a renamed copy of the install (ORIG_DIR, default
# E:\Development\BrutalNotIncluded; the frozen builds' copies are E:\oni-builds\BrutalNotIncluded-BUILD):
# Unity finds its data folder by the executable's name, so NAME.exe + NAME_Data run beside any
# other copy, and the Steam install is never run or written: a copy is made once from
# ONI_WIN_GAME_DIR (files, no links) and a copy inside a Steam library is refused. In the copy:
# - Unity Doorstop 4 (winhttp.dll, copied once from ONI_DOORSTOP_SOURCE: a folder holding it, a
#   BepInEx 6 install or another copy) starts tools/OriginalIsolation before the game's own
#   assemblies load. No BepInEx: its HarmonyX would replace the game's 0Harmony, and mods built
#   against the game's Harmony then fail. OriginalIsolation patches with the game's 0Harmony:
#   Documents, the local and roaming application data folders and the working folder answer the
#   run home (saves, settings, mods), the game's audio goes to <run home>\fmodoutput.wav, and
#   without a run home, or when a redirection fails, the player ends before reading any user
#   data. It confirms in <run home>\original-isolation.txt; when the confirmation is not there 3 s
#   after Unity's log appears (60 s after the start without a log), the launcher ends the player;
# - the offline Steam double replaces steam_api64.dll (ONI_STEAM_STUB=PATH: another one, a local
#   test setup's), so no Steam client or account is involved;
# - ignorekcrashreporter.txt keeps crash reports local;
# - the product name in globalgamemanagers (PlayerSettings) is set for each run to "Moono Orig
#   XXXXXXXX" (random, the same length as "Oxygen Not Included"), so Unity's own per-user data
#   (the registry key HKCU\Software\Klei\PRODUCT, LocalLow\Klei\PRODUCT) is the run's own and
#   never the installed game's or another run's. After the run the key is exported to
#   <run home>\unity-prefs.reg and deleted, and LocalLow\Klei\PRODUCT moves to <run home>\LocalLow.
#   The game's code does not read the product name. TEMP and TMP are <run home>\Temp.
# Unity's log goes to <run home>\Player.log. The run ends after ORIG_SECONDS (default 600), the
# process killed. A copy runs one player at a time: take windows-game.lock for the whole run.
# As scripts/run-windows.sh's, the window opens windowed on the second monitor (`-screen-fullscreen
# 0 -monitor 2` first, so the run's own arguments win; ONI_WIN_MONITOR=N picks another monitor),
# and when it takes the focus as it opens, the window that had it gets it back (in its first
# minute; Windows may refuse that). Focus it only through out/local-tools/win-run.ps1 -Match
# RUN_HOME -Action focus.
#
#   ONI_RUN_HOME='C:\...\orig-home' scripts/original-windows.sh -screen-width 1024 -screen-height 768 -screen-fullscreen 0
set -euo pipefail
source "$(dirname "$0")/env.sh"
: "${ONI_RUN_HOME:?set ONI_RUN_HOME to the run home (a Windows path)}"
orig_win="${ORIG_DIR:-E:\\Development\\BrutalNotIncluded}"
orig_win="${orig_win%\\}"
orig="$(wslpath "$orig_win")"
name="$(basename "$orig")"
source_win="${ONI_WIN_GAME_DIR:-C:\\Program Files (x86)\\Steam\\steamapps\\common\\OxygenNotIncluded}"
source="$(wslpath "$source_win")"
managed="$orig/${name}_Data/Managed"
shopt -s nocasematch
[[ "$orig_win" == *\\steamapps\\* || "$orig_win" == *\\Steam\\* ]] && { echo "ORIG_DIR $orig_win is in a Steam folder: copies live outside it" >&2; exit 1; }
shopt -u nocasematch
[ "$(realpath -m "$orig")" != "$(realpath -m "$source")" ] || { echo "ORIG_DIR is the game folder itself" >&2; exit 1; }

if [ ! -d "$orig/${name}_Data" ]; then
  doorstop="${ONI_DOORSTOP_SOURCE:-${ONI_BEPINEX_SOURCE:-}}"
  : "${doorstop:?set ONI_DOORSTOP_SOURCE to a folder with the winhttp.dll of Unity Doorstop 4 (a BepInEx 6 install, another copy), for the first setup}"
  [ -f "$doorstop/winhttp.dll" ] || { echo "no winhttp.dll in $doorstop" >&2; exit 1; }
  if [ -f "$doorstop/.doorstop_version" ] && ! grep -q '^4\.' "$doorstop/.doorstop_version"; then
    echo "$doorstop holds Doorstop $(cat "$doorstop/.doorstop_version"), not 4" >&2; exit 1
  fi
  echo "setting up $orig_win from $source_win"
  mkdir -p "$orig"
  # Files only (--copy-links): the copy holds no link into the install it came from.
  rsync -rt --copy-links --exclude='*.dmp' "$source/" "$orig/"
  mv "$orig/OxygenNotIncluded.exe" "$orig/$name.exe"
  mv "$orig/OxygenNotIncluded_Data" "$orig/${name}_Data"
  cp "$doorstop/winhttp.dll" "$orig/"
  [ -f "$doorstop/.doorstop_version" ] && cp "$doorstop/.doorstop_version" "$orig/"
fi

# Each file goes in only when it differs, so a copy the player has open is left as it is.
put() { cmp -s "$1" "$2" || cp "$1" "$2"; }
dotnet build "$root/tools/OriginalIsolation" -c Release -v q -nologo -p:GameManaged="$managed" > /dev/null
put "$root/out/bin/OriginalIsolation/release/OriginalIsolation.dll" "$orig/OriginalIsolation.dll"
stub="${ONI_STEAM_STUB:-$root/out/win/steamstub/steam_api64.dll}"
[ -f "$stub" ] || { echo "no Steam double at $stub: run scripts/build-windows.sh" >&2; exit 1; }
put "$stub" "$orig/${name}_Data/Plugins/x86_64/steam_api64.dll"
[ -f "$orig/ignorekcrashreporter.txt" ] || touch "$orig/ignorekcrashreporter.txt"
[ -f "$orig/winhttp.dll" ] || { echo "no winhttp.dll (Unity Doorstop) in $orig_win" >&2; exit 1; }
# Doorstop starts OriginalIsolation, whatever DOORSTOP_DISABLE says. A copy set up with BepInEx
# before keeps its BepInEx folder as BepInEx.unused: the BepInEx plugin of older checkouts then
# fails to build, so their script ends instead of running the player without isolation.
[ -d "$orig/BepInEx" ] && [ ! -e "$orig/BepInEx.unused" ] && mv "$orig/BepInEx" "$orig/BepInEx.unused"
[ -d "$orig/BepInEx" ] && { echo "$orig_win holds BepInEx and BepInEx.unused: remove one" >&2; exit 1; }
config="$(mktemp)"
printf '[General]\r\nenabled = true\r\ntarget_assembly = OriginalIsolation.dll\r\nredirect_output_log = false\r\nboot_config_override =\r\nignore_disable_switch = true\r\n\r\n[UnityMono]\r\ndll_search_path_override =\r\ndebug_enabled = false\r\ndebug_address = 127.0.0.1:10000\r\ndebug_suspend = false\r\n' > "$config"
put "$config" "$orig/doorstop_config.ini"; rm -f "$config"

# The run's own product name, so Unity's registry key and LocalLow folder are the run's.
product="Moono Orig $(od -An -tx1 -N4 /dev/urandom | tr -d ' \n')"
python3 -I - "$orig/${name}_Data/globalgamemanagers" "$product" <<'PY'
import sys
path, product = sys.argv[1], sys.argv[2].encode()
assert len(product) == 19
with open(path, 'r+b') as f:
    data = f.read()
    # PlayerSettings: companyName "Klei", then productName, 19 bytes long.
    head = b'\x04\x00\x00\x00Klei\x13\x00\x00\x00'
    assert data.count(head) == 1, 'product name not found once'
    f.seek(data.index(head) + len(head))
    f.write(product)
PY

home="$(wslpath "$ONI_RUN_HOME")"; mkdir -p "$home"
rm -f "$home/original-isolation.txt" "$home/Player.log"
printf '%s\n' "$product" > "$home/unity-product.txt"
args=""; for a in -logFile "$ONI_RUN_HOME\\Player.log" -screen-fullscreen 0 -monitor "${ONI_WIN_MONITOR:-2}" "$@"; do args="$args'${a//\'/\'\'}',"; done
# The launcher is a script file in the run home: its C# declarations' quotes stay out of the
# command line.
ps1="$home/.launch-original.ps1"
cat > "$ps1" <<EOF
\$runHome = '${ONI_RUN_HOME//\'/\'\'}'
\$product = '$product'
\$userTemp = \$env:TEMP
\$env:ONI_RUN_HOME = \$runHome
\$env:TEMP = "\$runHome\\Temp"; \$env:TMP = \$env:TEMP
[void](New-Item -ItemType Directory -Force -Path \$env:TEMP)
Add-Type -Namespace OniTest -Name Focus -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr w); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr w, out uint pid);'
\$confirm = "\$runHome\\original-isolation.txt"
\$log = "\$runHome\\Player.log"
\$start = Get-Date
\$end = \$start.AddSeconds(${ORIG_SECONDS:-600})
\$before = [OniTest.Focus]::GetForegroundWindow()
\$p = Start-Process -FilePath '$orig_win\\$name.exe' -WorkingDirectory '$orig_win' -ArgumentList @(${args%,}) -PassThru
# The watchdog: OriginalIsolation confirms its redirections before the game reads any user data,
# 3 s after Unity's log appears at the latest (60 s after the start when no log appears);
# otherwise the player ends here. In the first minute, whenever the game's window takes the
# focus, the window that had it gets it back.
\$isolated = \$false; \$logSeen = \$null; \$why = \$null
while (-not \$p.HasExited) {
  \$now = Get-Date
  if (-not \$isolated) {
    \$text = if (Test-Path \$confirm) { Get-Content -Raw -LiteralPath \$confirm } else { '' }
    if (\$text -match 'FAILED') { \$why = \$text.Trim() }
    elseif (\$text -match 'redirected') { \$isolated = \$true; 'isolation confirmed ' + [int](\$now - \$start).TotalMilliseconds + ' ms after the start' }
    else {
      if (-not \$logSeen -and (Test-Path \$log)) { \$logSeen = \$now }
      if (\$logSeen -and (\$now - \$logSeen).TotalSeconds -gt 3) { \$why = 'no isolation confirmed 3 s after the log appeared' }
      elseif ((\$now - \$start).TotalSeconds -gt 60) { \$why = 'no log and no isolation 60 s after the start' }
    }
    if (\$why) { Stop-Process -Id \$p.Id -Force -ErrorAction SilentlyContinue; 'ISOLATION FAILED: ' + \$why + ': player ended'; break }
  }
  if ((\$now - \$start).TotalSeconds -lt 60 -and \$before -ne [IntPtr]::Zero) {
    \$owner = [uint32]0
    [void][OniTest.Focus]::GetWindowThreadProcessId([OniTest.Focus]::GetForegroundWindow(), [ref]\$owner)
    if (\$owner -eq \$p.Id) { [void][OniTest.Focus]::SetForegroundWindow(\$before) }
  } elseif (\$isolated) { break }
  Start-Sleep -Milliseconds 100
}
\$left = [Math]::Max(0, [int](\$end - (Get-Date)).TotalMilliseconds)
if (\$why) { \$result = 'exited isolation-failed' }
elseif (-not \$p.WaitForExit(\$left)) { Stop-Process -Id \$p.Id -Force; [void]\$p.WaitForExit(10000); \$result = 'stopped after ${ORIG_SECONDS:-600} s' }
else { \$result = 'exited ' + \$p.ExitCode }
if ((Test-Path \$confirm) -and ((Get-Content -Raw -LiteralPath \$confirm) -match 'fmod: ')) { 'audio: WAV writer confirmed' } else { 'audio: no FMOD confirmation (FMOD never started)' }
# Unity's per-user data of this run's product name moves into the run home.
Start-Sleep -Milliseconds 500
\$key = "HKCU\\Software\\Klei\\\$product"
if (Test-Path "Registry::\$key") {
  [void](reg.exe export \$key "\$runHome\\unity-prefs.reg" /y)
  Remove-Item -LiteralPath "Registry::\$key" -Recurse -Force
  'unity prefs: ' + \$key + ' moved to unity-prefs.reg'
}
foreach (\$pair in @(@("\$env:USERPROFILE\\AppData\\LocalLow\\Klei\\\$product", "\$runHome\\LocalLow"), @("\$userTemp\\Klei\\\$product", "\$runHome\\Temp-user"))) {
  if (Test-Path -LiteralPath \$pair[0]) {
    try { Move-Item -LiteralPath \$pair[0] -Destination \$pair[1] -Force -ErrorAction Stop; 'moved ' + \$pair[0] + ' to ' + \$pair[1] }
    catch { 'could not move ' + \$pair[0] + ': ' + \$_ }
  }
}
\$result
EOF
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$ps1")" | tr -d '\r'
