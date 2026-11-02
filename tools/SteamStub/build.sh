#!/usr/bin/env bash
# Builds the offline Steam test double (see gen.py) into out/steamstub/libsteam_api.so. gen.py's
# options name the folder: --dlc out/steamstub-dlc/, --dlc-except APP_ID
# out/steamstub-dlc-except-APP_ID/, and --ugc (with ugc.s, against libc) adds -ugc.
#   tools/SteamStub/build.sh [--dlc | --dlc-except APP_ID] [--ugc]
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/out/steamstub"; ugc=0; args=("$@")
while [ $# -gt 0 ]; do
    case "$1" in
        --dlc) out+="-dlc" ;;
        --dlc-except) out+="-dlc-except-${2:?an app id}"; shift ;;
        --ugc) ugc=1 ;;
        *) echo "usage: $0 [--dlc | --dlc-except APP_ID] [--ugc]" >&2; exit 2 ;;
    esac
    shift
done
[ $ugc = 1 ] && out+="-ugc"
mkdir -p "$out"
python3 -I "$root/tools/SteamStub/gen.py" "$root/src/ThirdParty/Steamworks.NET/Runtime/autogen/NativeMethods.cs" "$out/steam_api_stub.s" "${args[@]}"
as --64 -o "$out/steam_api_stub.o" "$out/steam_api_stub.s"
if [ $ugc = 1 ]; then
    as --64 -o "$out/ugc.o" "$root/tools/SteamStub/ugc.s"
    ld -shared -soname libsteam_api.so -o "$out/libsteam_api.so" "$out/steam_api_stub.o" "$out/ugc.o" /lib/x86_64-linux-gnu/libc.so.6
else
    ld -shared -soname libsteam_api.so -o "$out/libsteam_api.so" "$out/steam_api_stub.o"
fi
echo "$out/libsteam_api.so"
