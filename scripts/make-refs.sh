#!/usr/bin/env bash
# Makes the game's reference assemblies for one game build: the files the release build compiles
# against (OniMoono.GameTweaks, OniMoono.Host) and the unit tests reference, from that build's
# install, with every method body stripped (JetBrains Refasmer, metadata only). Run once per new
# game build; the result goes into the private refs repository (docs/RELEASES.md), and the
# manifest entry it prints into ci/game-refs.json.
#   scripts/make-refs.sh GAME_DIR BUILD OUT
# writes OUT/BUILD/*.dll and OUT/BUILD/SHA256SUMS, and OUT/BUILD/ORIGINALS.sha256 (the hashes of
# the game's own files they were made from). Nothing here runs the game's code.
set -euo pipefail
source "$(dirname "$0")/env.sh"
[ $# -eq 3 ] || { sed -n '2,10p' "$0" >&2; exit 2; }
game_dir="$1" build="$2" out="$3"
[[ "$build" =~ ^[0-9]+$ ]] || { echo "make-refs: BUILD is the game's build number, like 744825" >&2; exit 2; }
managed="$game_dir/OxygenNotIncluded_Data/Managed"
[ -f "$managed/Assembly-CSharp.dll" ] || { echo "make-refs: no game install at $game_dir" >&2; exit 1; }
# What the compiler is given from the game install (the ReferencePath items from the game's
# Managed folder): GameTweaks and the host for the release, Newtonsoft.Json for the unit tests.
files=(0Harmony.dll Assembly-CSharp.dll Assembly-CSharp-firstpass.dll FMODUnity.dll Ionic.Zip.dll
       Newtonsoft.Json.dll Unity.TextMeshPro.dll UnityEngine.dll UnityEngine.CoreModule.dll
       UnityEngine.TextRenderingModule.dll UnityEngine.UI.dll UnityEngine.UIModule.dll)
refasmer_version=2.0.3
tools="$root/out/tools/refasmer-$refasmer_version"
[ -x "$tools/refasmer" ] || dotnet tool install --tool-path "$tools" JetBrains.Refasmer.CliTool --version "$refasmer_version" >/dev/null
dest="$out/$build"
rm -rf "$dest"; mkdir -p "$dest"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
for f in "${files[@]}"; do cp "$managed/$f" "$work/"; done
# Public and protected API only (with what InternalsVisibleTo grants); private members that take
# no part in it are dropped, as a compiler-made reference assembly drops them. Except in the
# game's own two: OniMoono.GameTweaks compiles against publicized copies of those and uses their
# private members directly, so they keep every type and member (--all; still without bodies).
own=(Assembly-CSharp.dll Assembly-CSharp-firstpass.dll)
others=()
for f in "${files[@]}"; do [[ " ${own[*]} " == *" $f "* ]] || others+=("$work/$f"); done
"$tools/refasmer" -q -c --omit-non-api-members=true -O "$dest" "${others[@]}"
"$tools/refasmer" -q -c --all -O "$dest" "${own[@]/#/$work/}"
for f in "${files[@]}"; do [ -s "$dest/$f" ] || { echo "make-refs: $f was not made" >&2; exit 1; }; done
(cd "$dest" && sha256sum "${files[@]}" > SHA256SUMS)
(cd "$managed" && sha256sum "${files[@]}" > "$dest/ORIGINALS.sha256")
# The entry for ci/game-refs.json (its source's commit is the refs repository's, once pushed).
python3 -I - "$dest" "$build" "${files[@]}" <<'EOF'
import hashlib, json, sys
dest, build, files = sys.argv[1], sys.argv[2], sys.argv[3:]
digest = lambda f: hashlib.sha256(open(f"{dest}/{f}", "rb").read()).hexdigest()
entry = {"source": {"kind": "git", "host": "github.com", "repository": "tigin-backwards/oni-moono-refs",
                    "commit": "COMMIT", "path": build},
         "files": {f: digest(f) for f in sorted(files)}}
print(json.dumps({build: entry}, indent=2))
EOF
