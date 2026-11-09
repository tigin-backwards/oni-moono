#!/usr/bin/env bash
# Runs tests/e2e/klei-unreachable.yaml (issue #28) with Klei's servers out of reach for the
# players alone: every proxy variable names a closed local port in their environment (the
# runner's own requests to OniMcp go direct, by no_proxy), and nothing system-wide changes. The
# Steam double hands out an auth ticket, as Steam does, so KleiAccount posts its login: a copy
# of the e2e double (QA_U57_STEAM, from out/local-tools/qa.env) built from the assembly beside
# it (stub.s), with ISteamUser_GetAuthSessionTicket giving four bytes. The original runs from a
# folder of links to E2E_ORIGINAL's, with that copy as its libsteam_api.so. Other arguments go to
# e2e.py, which runs on lane 18 unless they say otherwise. Its runs, references and the original's
# lane folders go to out/e2e-klei/e2e (E2E_OUT), apart from other specs' with the usual double:
#   scripts/e2e-klei-unreachable.sh --player both
set -euo pipefail
source "$(dirname "$0")/env.sh"
# As e2e.py: the environment's values first, else qa.env's.
# shellcheck source=/dev/null  # local and optional
if [ -f "$root/out/local-tools/qa.env" ]; then
    QA_U57_STEAM="${QA_U57_STEAM:-$(source "$root/out/local-tools/qa.env"; echo "${QA_U57_STEAM:-}")}"
    E2E_ORIGINAL="${E2E_ORIGINAL:-$(source "$root/out/local-tools/qa.env"; echo "${E2E_ORIGINAL:-}")}"
fi
: "${QA_U57_STEAM:?needs QA_U57_STEAM (out/local-tools/qa.env)}" "${E2E_ORIGINAL:?needs E2E_ORIGINAL (out/local-tools/qa.env)}"
work="$root/out/e2e-klei"
mkdir -p "$work/steam"
source_s="$(dirname "$QA_U57_STEAM")/stub.s"
[ -f "$source_s" ] || { echo "no $source_s: the ticket double is built from the e2e double's assembly" >&2; exit 2; }
python3 -I - "$source_s" "$work/steam/stub.s" << 'EOF'
import sys
text = open(sys.argv[1]).read()
label = "SteamAPI_ISteamUser_GetAuthSessionTicket:\n"
old = label + "  xor eax, eax\n  pxor xmm0, xmm0\n  ret\n"
if old not in text:
    sys.exit("the double's GetAuthSessionTicket is not the one this script replaces")
# (self, pTicket, cbMaxTicket, pcbTicket): a 4-byte ticket, handle 1.
new = label + "  mov dword ptr [rsi], 0x04030201\n  mov dword ptr [rcx], 4\n  mov eax, 1\n  ret\n"
open(sys.argv[2], "w").write(text.replace(old, new))
EOF
as --64 -o "$work/steam/stub.o" "$work/steam/stub.s"
ld -shared -soname libsteam_api.so -o "$work/steam/libsteam_api.so" "$work/steam/stub.o"

# The original's folder: links to everything but Plugins/libsteam_api.so.
# The folder the executable sits in, not where its link points: an install of links can have
# its own OxygenNotIncluded_Data (DLC content) beside a linked executable.
source_dir="$(cd "$(dirname "$E2E_ORIGINAL")" && pwd -P)"
original="$work/original"
rm -rf "$original"
mkdir -p "$original/OxygenNotIncluded_Data/Plugins"
# The folder's links (the install's files); its own files (the executable, logs) are not shared.
for f in "$source_dir"/*; do
    n="$(basename "$f")"
    case "$n" in OxygenNotIncluded_Data|unity.lock|"$(basename "$E2E_ORIGINAL")") continue ;; esac
    [ -L "$f" ] && ln -s "$(readlink -f "$f")" "$original/$n"
done
cp "$E2E_ORIGINAL" "$original/$(basename "$E2E_ORIGINAL")"
for f in "$source_dir"/OxygenNotIncluded_Data/*; do
    n="$(basename "$f")"
    case "$n" in Plugins|out) continue ;; esac
    ln -s "$(readlink -f "$f")" "$original/OxygenNotIncluded_Data/$n"
done
for f in "$source_dir"/OxygenNotIncluded_Data/Plugins/*; do
    n="$(basename "$f")"
    [ "$n" = libsteam_api.so ] || ln -s "$(readlink -f "$f")" "$original/OxygenNotIncluded_Data/Plugins/$n"
done
cp "$work/steam/libsteam_api.so" "$original/OxygenNotIncluded_Data/Plugins/libsteam_api.so"

E2E_ORIGINAL="$original/$(basename "$E2E_ORIGINAL")"
export QA_U57_STEAM="$work/steam/libsteam_api.so" E2E_ORIGINAL E2E_OUT="$work/e2e"
dead=http://127.0.0.1:9
export http_proxy=$dead https_proxy=$dead HTTP_PROXY=$dead HTTPS_PROXY=$dead ALL_PROXY=$dead all_proxy=$dead
export no_proxy=localhost,127.0.0.1 NO_PROXY=localhost,127.0.0.1
lanes=(--first-lane 18 --last-lane 18)
for a in "$@"; do case "$a" in --first-lane|--last-lane|--lanes) lanes=() ;; esac; done
exec python3 "$root/scripts/e2e.py" "${lanes[@]}" "$@" "$root/tests/e2e/klei-unreachable.yaml"
