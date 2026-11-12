#!/usr/bin/env bash
# Prepares a Windows run home (scripts/run-windows.sh, scripts/original-windows.sh) holding one
# save as its only colony, which the main menu resumes on its own (the game's AutoResumeSaveFile
# setting), a 1024x768 window, every volume at 0 (test runs are silent) and, optionally, local
# mods enabled for the base game and Spaced Out. Not part of the product.
#   scripts/windows-run-home.sh RUN_HOME SAVE [MOD_DIR:STAMP ...]
# RUN_HOME is a WSL path (it is emptied first); MOD_DIR a built mod folder (mod.yaml with its
# staticID); STAMP the mod's mods.json version, the Mono string hash of its folder name
# (ARCHITECTURE D3; tools/PerfProbe: -2037174645).
set -euo pipefail
home=$1; save=$2; shift 2
name=$(basename "$save" .sav)
d="$home/Documents/Klei/OxygenNotIncluded"
rm -rf "$home"
mkdir -p "$d/cloud_save_files/76561197960265729/$name"
cp "$save" "$d/cloud_save_files/76561197960265729/$name/$name.sav"
file="$(wslpath -w "$d/cloud_save_files/76561197960265729/$name/$name.sav")"
# Spaced Out is active when the save is a Spaced Out one: the resumed save skips the menu's own
# check, and a save of the other mode would load into a game set up without its content (its
# sound banks, for one: Spaced Out's music events then fail when the colony starts).
expansion1=$(python3 -I - "$save" <<'PY'
import json, sys
head = open(sys.argv[1], 'rb').read(65536).decode('latin1')
start = head.index('{', 16); depth = 0   # after the four int32s (a header size can hold 0x7b)
for i, c in enumerate(head[start:], start):
    depth += (c == '{') - (c == '}')
    if depth == 0: break
info = json.loads(head[start:i + 1])
# Older saves (before 7.34ish) list no dlcIds; their cluster names the expansion.
print(1 if 'EXPANSION1_ID' in (info.get('dlcIds') or []) or str(info.get('clusterId', '')).startswith('expansion1::') else 0)
PY
)
printf 'strings:\n  AutoResumeSaveFile: %s\nints:\n  ResolutionWidth: 1024\n  ResolutionHeight: 768\n  RefreshRateNumerator: 60\n  RefreshRateDenominator: 1\n  FullScreen: 0\n  EXPANSION1_ID.ENABLED: %s\nfloats:\n  Volume_Master: 0\n  Volume_SFX: 0\n  Volume_Music: 0\n  Volume_Ambience: 0\n  Volume_UI: 0\n  Volume_VO: 0\n' "'${file//\'/\'\'}'" "$expansion1" > "$d/kplayerprefs.yaml"
entries=""
for m in "$@"; do
  dir=${m%:*}; stamp=${m##*:}
  id=$(sed -n 's/^staticID: *//p' "$dir/mod.yaml" | tr -d '\r')
  mkdir -p "$d/mods/Local"
  cp -r "$dir" "$d/mods/Local/$id"
  entries="$entries{\"label\":{\"distribution_platform\":0,\"id\":\"$id\",\"title\":\"$id\",\"version\":$stamp},\"status\":1,\"enabled\":true,\"enabledForDlc\":[\"\",\"EXPANSION1_ID\"],\"crash_count\":0,\"reinstall_path\":null,\"staticID\":\"$id\"},"
done
[ -n "$entries" ] && echo "{\"version\":1,\"mods\":[${entries%,}],\"mod_load_in_progress\":false}" > "$d/mods/mods.json"
true
